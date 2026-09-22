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
}
