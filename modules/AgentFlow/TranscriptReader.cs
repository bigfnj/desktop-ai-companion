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
        public DateTime StartedUtc;
        /// <summary>The permission mode in force when the call was issued.</summary>
        public string Mode;
    }

    /// <summary>What one agent session looks like from its transcript alone.</summary>
    public sealed class AgentSession
    {
        public string Agent;          // "claude" or "codex"
        public string SessionId;
        public string Path;
        public string Cwd;
        public string Mode;           // permission mode last seen
        public DateTime LastWriteUtc;
        public bool SawAnyCall;       // false means the adapter may be stale
        public List<OutstandingCall> Outstanding = new List<OutstandingCall>();

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

        public static string ClaudeRoot
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".claude", "projects");
            }
        }

        public static string CodexRoot
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".codex", "sessions");
            }
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
        /// </summary>
        public static List<string> ActiveTranscripts(string root, double windowSeconds,
                                                     string skipDirectoryName)
        {
            var found = new List<KeyValuePair<DateTime, string>>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return new List<string>();
            DateTime cutoff = DateTime.UtcNow.AddSeconds(-windowSeconds);
            try
            {
                foreach (string path in Directory.EnumerateFiles(root, "*.jsonl",
                                                                 SearchOption.AllDirectories))
                {
                    if (!string.IsNullOrEmpty(skipDirectoryName))
                    {
                        string parent = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
                        if (string.Equals(parent, skipDirectoryName, StringComparison.OrdinalIgnoreCase))
                            continue;   // a blocked SUBAGENT is not a prompt the user can answer
                    }
                    DateTime written;
                    try { written = File.GetLastWriteTimeUtc(path); }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    if (written >= cutoff)
                        found.Add(new KeyValuePair<DateTime, string>(written, path));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            found.Sort((left, right) => right.Key.CompareTo(left.Key));
            var paths = new List<string>(found.Count);
            foreach (KeyValuePair<DateTime, string> entry in found) paths.Add(entry.Value);
            return paths;
        }

        /// <summary>Read one Claude transcript. Never throws; returns null when unreadable.</summary>
        public static AgentSession ReadClaude(string path)
        {
            var session = new AgentSession
            {
                Agent = AgentClaude,
                Path = path,
                SessionId = Path.GetFileNameWithoutExtension(path),
            };
            var pending = new Dictionary<string, OutstandingCall>(StringComparer.Ordinal);
            string mode = null;

            foreach (string line in ReadLines(path))
            {
                JsonElement record;
                if (!TryParse(line, out record)) continue;

                // BEFORE the content check: a `permission-mode` record carries no message and no
                // content, so reading the mode afterwards would never see one. It also carries NO
                // timestamp -- its only keys are permissionMode, sessionId and type -- which is why
                // the carry-forward here is positional, by file order, and why anything that sorts
                // records by time would drop every mode change on the floor.
                string declared = GetString(record, "permissionMode");
                if (!string.IsNullOrEmpty(declared)) mode = declared;

                string cwd = GetString(record, "cwd");
                if (!string.IsNullOrEmpty(cwd)) session.Cwd = cwd;

                DateTime when;
                bool haveWhen = TryGetTimestamp(record, out when);

                JsonElement message;
                if (!record.TryGetProperty("message", out message)
                    || message.ValueKind != JsonValueKind.Object) continue;
                JsonElement content;
                if (!message.TryGetProperty("content", out content)
                    || content.ValueKind != JsonValueKind.Array) continue;

                foreach (JsonElement block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object) continue;
                    string kind = GetString(block, "type");
                    if (kind == "tool_use")
                    {
                        session.SawAnyCall = true;
                        string id = GetString(block, "id");
                        if (string.IsNullOrEmpty(id)) continue;
                        JsonElement input;
                        string command = null;
                        if (block.TryGetProperty("input", out input)
                            && input.ValueKind == JsonValueKind.Object)
                        {
                            command = GetString(input, "command");
                        }
                        pending[id] = new OutstandingCall
                        {
                            Id = id,
                            Tool = GetString(block, "name") ?? "?",
                            Command = command,
                            StartedUtc = haveWhen ? when : DateTime.UtcNow,
                            Mode = mode,
                        };
                    }
                    else if (kind == "tool_result")
                    {
                        session.SawAnyCall = true;
                        string id = GetString(block, "tool_use_id");
                        if (!string.IsNullOrEmpty(id)) pending.Remove(id);
                    }
                }
            }

            session.Mode = mode;
            foreach (OutstandingCall call in pending.Values) session.Outstanding.Add(call);
            try { session.LastWriteUtc = File.GetLastWriteTimeUtc(path); }
            catch (IOException) { session.LastWriteUtc = DateTime.UtcNow; }
            catch (UnauthorizedAccessException) { session.LastWriteUtc = DateTime.UtcNow; }
            return session;
        }

        /// <summary>Read one Codex rollout. Never throws; returns null when unreadable.</summary>
        public static AgentSession ReadCodex(string path)
        {
            var session = new AgentSession
            {
                Agent = AgentCodex,
                Path = path,
                SessionId = Path.GetFileNameWithoutExtension(path),
            };
            var pending = new Dictionary<string, OutstandingCall>(StringComparer.Ordinal);

            foreach (string line in ReadLines(path))
            {
                JsonElement record;
                if (!TryParse(line, out record)) continue;
                DateTime when;
                bool haveWhen = TryGetTimestamp(record, out when);

                JsonElement payload;
                if (!record.TryGetProperty("payload", out payload)
                    || payload.ValueKind != JsonValueKind.Object) continue;
                string kind = GetString(payload, "type");
                if (kind == null) continue;

                if (Contains(CodexCallTypes, kind))
                {
                    session.SawAnyCall = true;
                    string id = GetString(payload, "call_id");
                    if (string.IsNullOrEmpty(id)) continue;
                    pending[id] = new OutstandingCall
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
                    session.SawAnyCall = true;
                    string id = GetString(payload, "call_id");
                    if (!string.IsNullOrEmpty(id)) pending.Remove(id);
                }
                else if (kind == "session_meta")
                {
                    string cwd = GetString(payload, "cwd");
                    if (!string.IsNullOrEmpty(cwd)) session.Cwd = cwd;
                }
            }

            foreach (OutstandingCall call in pending.Values) session.Outstanding.Add(call);
            try { session.LastWriteUtc = File.GetLastWriteTimeUtc(path); }
            catch (IOException) { session.LastWriteUtc = DateTime.UtcNow; }
            catch (UnauthorizedAccessException) { session.LastWriteUtc = DateTime.UtcNow; }
            return session;
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
