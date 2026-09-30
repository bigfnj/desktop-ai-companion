using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>
    /// Loads the permission rules already on disk, merged across the tiers.
    ///
    /// Arrays MERGE across tiers rather than the managed file replacing the user's, and evaluation
    /// is deny then ask then allow. That ordering is the whole reason a managed `ask` beats a user
    /// `allow`: the ask matches first and the allow is never reached. Getting the merge wrong in the
    /// other direction (managed replaces user) would silently drop hundreds of the user's own rules.
    /// </summary>
    public static class RuleLoader
    {
        /// <summary>Settings files in evaluation order. Missing ones are skipped, not an error.</summary>
        /// <summary>Environment override for the directory holding Claude's settings files.
        /// Same convention, and the same two reasons, as
        /// <see cref="TranscriptReader.ClaudeRootVariable"/>: an agent can keep its state
        /// somewhere else, and it is the only way to exercise rule discovery without writing into
        /// the user's real settings. Must be fully qualified or it is ignored.</summary>
        public static readonly string HomeVariable = "AGENTFLOW_CLAUDE_HOME";

        public static IEnumerable<string> DefaultPaths()
        {
            // NOT Environment.GetEnvironmentVariable("USERPROFILE"): GetFolderPath goes to the
            // shell API and ignores that variable entirely, measured 2026-09-22, so overriding it
            // does nothing. This is a separate, explicit override for the same reason the
            // transcript roots have one.
            string overridden = Environment.GetEnvironmentVariable(HomeVariable);
            string home = !string.IsNullOrWhiteSpace(overridden)
                          && Path.IsPathRooted(overridden)
                          && overridden.IndexOf(Path.VolumeSeparatorChar) == 1
                ? overridden
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, ".claude", "settings.json");
            yield return Path.Combine(home, ".claude", "remote-settings.json");
            yield return Path.Combine(home, ".claude", "settings.local.json");
        }

        /// <summary>
        /// The two PROJECT-scope settings files Claude Code reads for a session started in
        /// <paramref name="cwd"/>: the shared one and the local one. Nothing for an empty or
        /// relative cwd.
        ///
        /// These are where Claude Code WRITES. Answering "Yes, allow ... for this project (just you)"
        /// in the CLI lands the rule in .claude/settings.local.json under the directory the session
        /// was started in, and the shared variant in .claude/settings.json beside it. The module's
        /// own PromptOptions.RuleDestinations lists both so it can refuse to PRESS them, and until
        /// 2026-09-29 it never read what they held: a call allowed only by a project rule evaluated
        /// WouldPrompt, which is a false "waiting for an answer" bubble in default mode and a hole in
        /// the approvals audit in every mode (F053). The transcript's cwd IS the starting directory,
        /// which is where Claude Code resolves project settings, so no git-root walk is needed.
        /// </summary>
        public static IEnumerable<string> ProjectPaths(string cwd)
        {
            if (string.IsNullOrWhiteSpace(cwd)) yield break;
            bool rooted;
            try { rooted = Path.IsPathRooted(cwd); }
            catch (ArgumentException) { rooted = false; }
            if (!rooted) yield break;
            yield return Path.Combine(cwd, ".claude", "settings.json");
            yield return Path.Combine(cwd, ".claude", "settings.local.json");
        }

        /// <summary>
        /// <paramref name="home"/> plus the project-scope files for <paramref name="cwd"/>, merged in
        /// the order Claude Code merges them: arrays concatenate across tiers, evaluation stays deny
        /// then ask then allow. Returns <paramref name="home"/> itself when there is nothing to add,
        /// so the common case -- a session with no project rules -- allocates nothing.
        /// </summary>
        internal static RuleSet WithProjectRules(RuleSet home, string cwd, RuleCache cache)
        {
            int sources;
            IEnumerable<string> paths = ProjectPaths(cwd);
            RuleSet project = cache != null ? cache.Load(paths, out sources) : Load(paths, out sources);
            if (sources == 0 || home == null) return home;
            var merged = new RuleSet();
            AppendAll(home, merged);
            AppendAll(project, merged);
            return merged;
        }

        /// <summary>Copy every tier of <paramref name="from"/> onto the end of <paramref name="into"/>.</summary>
        internal static void AppendAll(RuleSet from, RuleSet into)
        {
            if (from == null || into == null) return;
            into.Deny.AddRange(from.Deny);
            into.Ask.AddRange(from.Ask);
            into.Allow.AddRange(from.Allow);
        }

        /// <summary>Merge every readable settings file into one rule set. Never throws.</summary>
        public static RuleSet Load(IEnumerable<string> paths, out int sources)
        {
            var rules = new RuleSet();
            sources = 0;
            if (paths == null) return rules;
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                string text;
                try { text = File.ReadAllText(path); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                try
                {
                    using (JsonDocument document = JsonDocument.Parse(text))
                    {
                        JsonElement permissions;
                        if (!document.RootElement.TryGetProperty("permissions", out permissions)
                            || permissions.ValueKind != JsonValueKind.Object) continue;
                        sources++;
                        Append(permissions, "deny", rules.Deny);
                        Append(permissions, "ask", rules.Ask);
                        Append(permissions, "allow", rules.Allow);
                    }
                }
                catch (JsonException)
                {
                    // A malformed settings file is the user's problem, not a reason to stop
                    // reading the others -- and it must not take the module down with it.
                    continue;
                }
            }
            return rules;
        }

        private static void Append(JsonElement permissions, string key, List<string> into)
        {
            JsonElement array;
            if (!permissions.TryGetProperty(key, out array)
                || array.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement entry in array.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String) continue;
                string value = entry.GetString();
                if (!string.IsNullOrEmpty(value)) into.Add(value.Trim());
            }
        }
    }

    /// <summary>
    /// The parsed settings files, kept between ticks and re-read only when a file changes.
    ///
    /// Scan called <see cref="RuleLoader.Load"/> on every 10 s tick: three File.Exists, up to three
    /// ReadAllText and JsonDocument.Parse calls against files that change only when the user edits
    /// them -- about 8,600 reads per present file per day for a rule set that changed once (F055,
    /// F030). Reading the project-scope files per SESSION (F053) would have multiplied that by the
    /// number of live cwds. So every path is keyed on (exists, LastWriteTimeUtc, Length): one stat
    /// per path per tick, a parse only when the key moves. A file that APPEARS or VANISHES moves the
    /// key like any other edit, which is what lets the self-test swap the whole home directory
    /// between two scans and see the difference.
    ///
    /// Owned by the poll worker, through SessionCache, and never shared: "Check now" and the
    /// assertions take the cold path (RuleLoader.Load, no cache) exactly as they do for transcripts,
    /// so this has no lock and needs none.
    ///
    /// Bounded the same way every other set in this module is, against what the current tick asked
    /// for: <see cref="Retain"/> drops every path not requested since the last call.
    /// </summary>
    internal sealed class RuleCache
    {
        private sealed class Entry
        {
            public bool Exists;
            public DateTime WrittenUtc;
            public long Length;
            /// <summary>The file's own tiers, or null when it exists but holds no permissions object.</summary>
            public RuleSet Rules;
            public bool Touched;
        }

        private readonly Dictionary<string, Entry> _files =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Files parsed since construction. A test seam: the point of this class is what
        /// it does NOT do, and "did not parse" is only observable by counting.</summary>
        internal int Parses { get; private set; }

        internal int Count { get { return _files.Count; } }

        /// <summary>Same contract as <see cref="RuleLoader.Load"/>, re-reading only the files whose
        /// stat key moved. The cached tiers are copied into a fresh set, never handed out.</summary>
        internal RuleSet Load(IEnumerable<string> paths, out int sources)
        {
            var merged = new RuleSet();
            sources = 0;
            if (paths == null) return merged;
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                Entry entry = Refresh(path);
                entry.Touched = true;
                if (entry.Rules == null) continue;
                sources++;
                RuleLoader.AppendAll(entry.Rules, merged);
            }
            return merged;
        }

        private Entry Refresh(string path)
        {
            bool exists;
            DateTime written = default(DateTime);
            long length = 0;
            try
            {
                var info = new FileInfo(path);
                exists = info.Exists;
                if (exists)
                {
                    written = info.LastWriteTimeUtc;
                    length = info.Length;
                }
            }
            catch (IOException) { exists = false; }
            catch (UnauthorizedAccessException) { exists = false; }
            catch (ArgumentException) { exists = false; }      // a cwd holding characters Windows refuses
            catch (NotSupportedException) { exists = false; }

            Entry entry;
            if (_files.TryGetValue(path, out entry) && entry.Exists == exists
                && entry.WrittenUtc == written && entry.Length == length)
                return entry;

            entry = new Entry { Exists = exists, WrittenUtc = written, Length = length };
            if (exists)
            {
                int held;
                RuleSet parsed = RuleLoader.Load(new[] { path }, out held);
                entry.Rules = held > 0 ? parsed : null;
                Parses++;
            }
            _files[path] = entry;
            return entry;
        }

        /// <summary>Drop every path not asked for since the last call, and re-arm the rest.</summary>
        internal void Retain()
        {
            var drop = new List<string>();
            foreach (KeyValuePair<string, Entry> pair in _files)
            {
                if (!pair.Value.Touched) drop.Add(pair.Key);
                pair.Value.Touched = false;
            }
            foreach (string path in drop) _files.Remove(path);
        }
    }
}
