using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>One tool call that has been issued and has no result yet.</summary>
    public sealed class OutstandingCall
    {
        public string Id;
        public string Tool;
        /// <summary>The command text, for rule evaluation only. NEVER logged or spoken.</summary>
        public string Command;
        /// <summary>
        /// The one argument the permission rules can actually address, for a tool that is not a
        /// shell: a file path, a URL, a glob. For rule evaluation only; NEVER logged or spoken.
        ///
        /// Without this, every non-shell call evaluated as `Tool()` with an empty specifier, which
        /// matches no rule, and "no rule matched" means PROMPT -- so every outstanding Read, Edit
        /// or Write read as would-prompt no matter what the rules say. That is a check that cannot
        /// fail, and it is why this field exists rather than defaulting the specifier to empty.
        /// </summary>
        public string Argument;
        public DateTime StartedUtc;
        /// <summary>The permission mode in force when the call was issued.</summary>
        public string Mode;
    }

    /// <summary>What one agent session looks like from its transcript alone.</summary>
    public sealed class AgentSession
    {
        public string Agent;          // "claude" or "codex"
        public string SessionId;
        // Path removed 2026-09-21: written by every reader and read by nothing. The cursor
        // keys on the path itself, so nothing downstream ever needed it on the session.
        public string Cwd;
        public string Mode;           // permission mode last seen
        public DateTime LastWriteUtc;
        public bool SawAnyCall;       // false means the adapter may be stale
        public List<OutstandingCall> Outstanding = new List<OutstandingCall>();

        /// <summary>
        /// Calls that PAIRED, i.e. finished. The detector does not look at these -- a finished call
        /// is not blocking anyone -- but an approval module owes the user an account of what ran on
        /// their behalf WITHOUT asking, and that is exactly this list filtered to WouldAllow.
        ///
        /// Bounded at <see cref="CompletedCap"/> and keeping the MOST RECENT, because a long session
        /// pairs thousands of calls and this is re-read from disk on every poll. The bound is a
        /// memory bound, not an audit window: the module tallies each call id once and remembers
        /// that it has, so a call that falls off this list has already been counted.
        /// </summary>
        public List<OutstandingCall> Completed = new List<OutstandingCall>();

        /// <summary>How many completed calls one session keeps. See <see cref="Completed"/>.</summary>
        public const int CompletedCap = 2000;

        /// <summary>Record a paired call, dropping the oldest once the cap is reached.</summary>
        public void NoteCompleted(OutstandingCall call)
        {
            if (call == null) return;
            Completed.Add(call);
            if (Completed.Count > CompletedCap) Completed.RemoveAt(0);
        }

        /// <summary>Seconds since the transcript was last written.</summary>
        public double IdleSeconds(DateTime nowUtc)
        {
            return (nowUtc - LastWriteUtc).TotalSeconds;
        }
    }

    /// <summary>
    /// Reads the append-only JSONL transcripts that Claude Code and Codex write about their own
    /// sessions, and reports which tool calls are outstanding.
    ///
    /// This is the whole reason AgentFlow needs no IDE integration. Both agents pair a tool call to
    /// its result by id in a file on disk, and they write the same file whether the agent is running
    /// in VS Code, a JetBrains terminal, Antigravity, or a bare shell. An unpaired call plus a
    /// quiet file means the agent is waiting on something.
    ///
    ///   claude   %USERPROFILE%\.claude\projects\&lt;slug&gt;\&lt;session&gt;.jsonl
    ///            message.content[] blocks: tool_use{id,name} paired by tool_result{tool_use_id}
    ///   codex    %USERPROFILE%\.codex\sessions\YYYY\MM\DD\rollout-&lt;ts&gt;-&lt;id&gt;.jsonl
    ///            payload{type,call_id,name}: custom_tool_call / function_call paired by the
    ///            matching *_output record carrying the same call_id
    ///
    /// PRIVACY. These files contain every command the user ran and the full text of what they typed.
    /// Command text is read here ONLY so PermissionRules can evaluate it, and it never leaves this
    /// object: nothing in this module logs it, speaks it, or writes it anywhere. That is the same
    /// line the measurement harnesses in docs/agentflow hold, and it is why the module declares
    /// ModulePermissions.AgentTranscripts so a user sees the read coming before installing.
    /// </summary>
    public static class TranscriptReader
    {
        public const string AgentClaude = "claude";
        public const string AgentCodex = "codex";

        // Codex has renamed its shell entry point at least once (shell_command -> exec_command),
        // and permission-wildcarding's own history adapters record that the rename silently zeroed
        // its whole corpus, because an unknown name is indistinguishable from a session that called
        // no tools. So accept every known spelling, and surface SawAnyCall so a caller can shout
        // rather than report a clean "nothing outstanding".
        private static readonly string[] CodexCallTypes = { "custom_tool_call", "function_call" };
        private static readonly string[] CodexOutputTypes =
            { "custom_tool_call_output", "function_call_output" };

        /// <summary>Environment override for the Claude transcript root.</summary>
        public const string ClaudeRootVariable = "AGENTFLOW_CLAUDE_ROOT";
        /// <summary>Environment override for the Codex transcript root.</summary>
        public const string CodexRootVariable = "AGENTFLOW_CODEX_ROOT";

        /// <summary>
        /// Where Claude Code writes its transcripts, overridable by
        /// <see cref="ClaudeRootVariable"/>.
        ///
        /// The override exists for two reasons, and the second is why it is not just test
        /// scaffolding. An agent can be configured to keep its state somewhere other than
        /// %USERPROFILE%\.claude, in which case the default here finds nothing and the module
        /// would silently appear broken. And it is the only way to exercise this module
        /// end-to-end without writing a fabricated session file into the user's REAL transcript
        /// store, which is both intrusive and indistinguishable from tampering with their history.
        ///
        /// Must be FULLY QUALIFIED, matching the rule the app applies to its own
        /// DESKTOP_AI_COMPANION_DATA_ROOT override: a relative or drive-relative path is ignored
        /// rather than resolved against whatever the working directory happens to be.
        /// </summary>
        public static string ClaudeRoot
        {
            get
            {
                string over = FullyQualifiedOverride(ClaudeRootVariable);
                if (over != null) return over;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".claude", "projects");
            }
        }

        /// <summary>Where Codex writes its rollouts, overridable by <see cref="CodexRootVariable"/>.</summary>
        public static string CodexRoot
        {
            get
            {
                string over = FullyQualifiedOverride(CodexRootVariable);
                if (over != null) return over;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".codex", "sessions");
            }
        }

        /// <summary>An override only when it is set AND fully qualified; otherwise null.</summary>
        internal static string FullyQualifiedOverride(string variable)
        {
            string value;
            try { value = Environment.GetEnvironmentVariable(variable); }
            catch (System.Security.SecurityException) { return null; }
            if (string.IsNullOrEmpty(value)) return null;
            value = value.Trim().Trim('"');
            if (value.Length == 0) return null;
            try
            {
                // Path.IsPathFullyQualified rejects both "foo\bar" and "\foo\bar" (drive-relative),
                // which is the distinction that matters: the latter LOOKS absolute and resolves
                // against the current drive.
                if (!Path.IsPathFullyQualified(value)) return null;
            }
            catch (ArgumentException) { return null; }
            return value;
        }

        /// <summary>
        /// Every transcript written within <paramref name="windowSeconds"/>, newest first.
        ///
        /// NOT "the single newest file", which is what the research probe did first and which is
        /// WRONG. Measured 2026-09-16: with two sessions live, the newest-file rule flipped between
        /// them on every poll, attributed one session's call to the other, and reported "nothing
        /// outstanding" whenever it happened to land on the quiet file. A blocked agent would have
        /// been missed entirely whenever a second session wrote more recently, which is the normal
        /// case on a machine running concurrent agents -- exactly the machine this is for.
        ///
        /// <para>
        /// DirectoryInfo, NOT Directory.EnumerateFiles, and the difference is a syscall per file.
        /// The string overload hands back paths, so the write time then costs a separate stat;
        /// FileInfo arrives with that field already filled from the directory scan Windows just
        /// did. MEASURED 2026-09-21, the two implementations alternating in one warm process over
        /// this box's 705 Claude transcripts, both agreeing on the count every time:
        ///
        ///   strings + stat   31.7 / 28.7 / 27.6 / 17.6 / 26.3 / 27.0 ms
        ///   FileInfo         11.1 / 11.4 /  9.2 /  7.3 / 10.5 / 10.5 ms
        ///
        /// That is the LOOP. This whole method, measured the same way before and after, went from
        /// 18.3-20.9 ms to 11.0-12.6 ms -- 1.7x rather than 2.7x, because the sort and the list
        /// building are unchanged and are now most of what is left. Quote the method number when
        /// talking about what a tick costs; the loop number only says which loop is better.
        ///
        /// Warm on purpose. A fresh-process figure answers "which implementation is faster"; the
        /// question here is what the five-hundredth tick of a long-running app costs, and cold
        /// numbers overstate that -- 31 ms cold against 19 ms warm for the same code.
        /// </para>
        /// </summary>
        public static List<string> ActiveTranscripts(string root, double windowSeconds,
                                                     string skipDirectoryName)
        {
            var found = new List<KeyValuePair<DateTime, string>>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return new List<string>();
            DateTime cutoff = DateTime.UtcNow.AddSeconds(-windowSeconds);
            try
            {
                foreach (FileInfo file in new DirectoryInfo(root).EnumerateFiles(
                             "*.jsonl", SearchOption.AllDirectories))
                {
                    if (!string.IsNullOrEmpty(skipDirectoryName))
                    {
                        DirectoryInfo parent = file.Directory;
                        if (parent != null && string.Equals(parent.Name, skipDirectoryName,
                                                            StringComparison.OrdinalIgnoreCase))
                            continue;   // a blocked SUBAGENT is not a prompt the user can answer
                    }
                    // No try/catch around this the way the stat needed one: the value came with
                    // the enumeration, so reading it cannot fail. A file deleted since the scan
                    // yields its last known time rather than throwing, and the cursor that opens
                    // it afterwards already handles a file that is no longer there.
                    DateTime written = file.LastWriteTimeUtc;
                    if (written >= cutoff)
                        found.Add(new KeyValuePair<DateTime, string>(written, file.FullName));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            found.Sort((left, right) => right.Key.CompareTo(left.Key));
            var paths = new List<string>(found.Count);
            foreach (KeyValuePair<DateTime, string> entry in found) paths.Add(entry.Value);
            return paths;
        }

        /// <summary>
        /// Read one Claude transcript whole.
        ///
        /// Never throws. It also never returns null, despite what this comment used to claim:
        /// an unreadable file yields a session with SawAnyCall false, which downstream becomes
        /// AdapterSuspect rather than "skip". That distinction is deliberate -- "I understood
        /// none of this" is not the same answer as "everything is fine".
        ///
        /// This is now the COLD path. TranscriptCursor folds the same records incrementally and
        /// calls the same FoldClaudeRecord, so the two cannot drift: there is one implementation
        /// of what a record means, and the cursor only changes where the byte stream is cut.
        /// </summary>
        public static AgentSession ReadClaude(string path)
        {
            return ReadWhole(path, AgentClaude);
        }

        /// <summary>Read one Codex rollout whole. See ReadClaude: same contract, same fold.</summary>
        public static AgentSession ReadCodex(string path)
        {
            return ReadWhole(path, AgentCodex);
        }

        private static AgentSession ReadWhole(string path, string agent)
        {
            var state = new FoldState();
            foreach (string line in ReadLines(path))
            {
                JsonElement record;
                if (!TryParse(line, out record)) continue;
                if (agent == AgentCodex) FoldCodexRecord(record, state);
                else FoldClaudeRecord(record, state);
            }

            var session = new AgentSession
            {
                Agent = agent,
                SessionId = Path.GetFileNameWithoutExtension(path),
                Cwd = state.Cwd,
                Mode = state.Mode,
                SawAnyCall = state.SawAnyCall,
            };
            foreach (OutstandingCall call in state.Pending.Values) session.Outstanding.Add(call);
            foreach (OutstandingCall call in state.CompletedSinceSnapshot) session.NoteCompleted(call);
            try { session.LastWriteUtc = File.GetLastWriteTimeUtc(path); }
            catch (IOException) { session.LastWriteUtc = DateTime.UtcNow; }
            catch (UnauthorizedAccessException) { session.LastWriteUtc = DateTime.UtcNow; }
            return session;
        }

        /// <summary>The cursor needs the same line-to-record step; exposed rather than copied.</summary>
        internal static bool TryParseForFold(string line, out JsonElement record)
        {
            return TryParse(line, out record);
        }

        /// <summary>
        /// One Claude record, folded into the running state.
        ///
        /// The mode read comes BEFORE the message/content guard: a `permission-mode` record
        /// carries no message and no content, so reading the mode afterwards would never see
        /// one. It also carries NO timestamp -- its only keys are permissionMode, sessionId and
        /// type -- which is why the carry-forward is positional, by file order, and why anything
        /// that sorted records by time would drop every mode change on the floor. An incremental
        /// reader preserves that ordering for free, because append order IS file order.
        /// </summary>
        internal static void FoldClaudeRecord(JsonElement record, FoldState state)
        {
            string declared = GetString(record, "permissionMode");
            if (!string.IsNullOrEmpty(declared)) state.Mode = declared;

            string cwd = GetString(record, "cwd");
            if (!string.IsNullOrEmpty(cwd)) state.Cwd = cwd;

            DateTime when;
            bool haveWhen = TryGetTimestamp(record, out when);

            JsonElement message;
            if (!record.TryGetProperty("message", out message)
                || message.ValueKind != JsonValueKind.Object) return;
            JsonElement content;
            if (!message.TryGetProperty("content", out content)
                || content.ValueKind != JsonValueKind.Array) return;

            foreach (JsonElement block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                string kind = GetString(block, "type");
                if (kind == "tool_use")
                {
                    state.SawAnyCall = true;
                    string id = GetString(block, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    JsonElement input;
                    string command = null, argument = null;
                    if (block.TryGetProperty("input", out input)
                        && input.ValueKind == JsonValueKind.Object)
                    {
                        command = GetString(input, "command");
                        argument = FirstAddressable(input);
                    }
                    state.Pending[id] = new OutstandingCall
                    {
                        Id = id,
                        Tool = GetString(block, "name") ?? "?",
                        Command = command,
                        Argument = argument,
                        StartedUtc = haveWhen ? when : DateTime.UtcNow,
                        Mode = state.Mode,
                    };
                }
                else if (kind == "tool_result")
                {
                    state.SawAnyCall = true;
                    // MOVED, not dropped. The call finished, so it is not blocking anyone -- and
                    // it is the raw material for the approval audit, because a call that
                    // completed without a prompt is a call the rules approved on the user's
                    // behalf. Dropping it here is why the module could only ever report what it
                    // STOPPED.
                    string id = GetString(block, "tool_use_id");
                    if (!string.IsNullOrEmpty(id)) state.Complete(id);
                }
            }
        }

        /// <summary>
        /// One Codex record, folded into the running state.
        ///
        /// turn_context names itself on the RECORD, and its payload carries no "type" at all, so
        /// it is dispatched before the payload-type switch, which would drop it on the null
        /// guard. The first version of this read payload.type and the fixture invented one no
        /// real rollout has, so the test agreed with the code and both were wrong.
        /// </summary>
        internal static void FoldCodexRecord(JsonElement record, FoldState state)
        {
            DateTime when;
            bool haveWhen = TryGetTimestamp(record, out when);

            JsonElement payload;
            if (!record.TryGetProperty("payload", out payload)
                || payload.ValueKind != JsonValueKind.Object) return;

            if (string.Equals(GetString(record, "type"), "turn_context", StringComparison.Ordinal))
            {
                // approval_policy, NOT collaboration_mode.mode. The latter also says "default"
                // while sitting beside approval_policy `never` and full disk access: Claude's
                // word for "ask me every time", meaning the opposite. Last writer wins, because
                // the policy is per turn.
                string seenPolicy = GetString(payload, "approval_policy");
                if (!string.IsNullOrEmpty(seenPolicy)) state.Mode = seenPolicy;
                return;
            }

            string kind = GetString(payload, "type");
            if (kind == null) return;

            if (Contains(CodexCallTypes, kind))
            {
                state.SawAnyCall = true;
                string id = GetString(payload, "call_id");
                if (string.IsNullOrEmpty(id)) return;
                state.Pending[id] = new OutstandingCall
                {
                    Id = id,
                    Tool = GetString(payload, "name") ?? "?",
                    Command = null,
                    StartedUtc = haveWhen ? when : DateTime.UtcNow,
                    Mode = null,
                };
            }
            else if (Contains(CodexOutputTypes, kind))
            {
                state.SawAnyCall = true;
                string id = GetString(payload, "call_id");
                if (!string.IsNullOrEmpty(id)) state.Complete(id);
            }
            else if (kind == "session_meta")
            {
                string cwd = GetString(payload, "cwd");
                if (!string.IsNullOrEmpty(cwd)) state.Cwd = cwd;
            }
        }
        private static readonly string[] AddressableKeys =
            { "file_path", "path", "notebook_path", "url", "pattern" };

        private static string FirstAddressable(JsonElement input)
        {
            foreach (string key in AddressableKeys)
            {
                string value = GetString(input, key);
                if (!string.IsNullOrEmpty(value)) return value;
            }
            return null;
        }

        private static bool Contains(string[] values, string candidate)
        {
            foreach (string value in values)
                if (string.Equals(value, candidate, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Lines of a file that another process is appending to, right now.</summary>
        private static IEnumerable<string> ReadLines(string path)
        {
            StreamReader reader;
            try
            {
                // ReadWrite share: the agent holds this file open for append while we read it, and
                // a plain File.OpenText would fail with a sharing violation.
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                            FileShare.ReadWrite | FileShare.Delete);
                reader = new StreamReader(stream);
            }
            catch (IOException) { yield break; }
            catch (UnauthorizedAccessException) { yield break; }

            using (reader)
            {
                string line;
                while ((line = ReadLineSafely(reader)) != null)
                {
                    if (line.Length > 0) yield return line;
                }
            }
        }

        private static string ReadLineSafely(StreamReader reader)
        {
            try { return reader.ReadLine(); }
            catch (IOException) { return null; }
        }

        private static bool TryParse(string line, out JsonElement record)
        {
            record = default(JsonElement);
            try
            {
                using (JsonDocument document = JsonDocument.Parse(line))
                {
                    // Clone, because the element is only valid while the document lives.
                    record = document.RootElement.Clone();
                }
                return record.ValueKind == JsonValueKind.Object;
            }
            catch (JsonException)
            {
                return false;   // a torn final line while the agent is mid-write
            }
        }

        private static string GetString(JsonElement element, string name)
        {
            JsonElement value;
            if (!element.TryGetProperty(name, out value)) return null;
            if (value.ValueKind != JsonValueKind.String) return null;
            return value.GetString();
        }

        private static bool TryGetTimestamp(JsonElement record, out DateTime when)
        {
            when = default(DateTime);
            string text = GetString(record, "timestamp");
            if (string.IsNullOrEmpty(text)) return false;
            DateTime parsed;
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture,
                                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                   out parsed))
                return false;
            when = parsed;
            return true;
        }
    }
}
