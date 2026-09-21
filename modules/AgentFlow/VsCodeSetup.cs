using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>What the module found when it looked at VS Code's runtime-arguments file.</summary>
    public enum SetupState
    {
        /// <summary>No argv.json anywhere the module knows to look. The user must point at it.</summary>
        NotFound = 0,
        /// <summary>Found, and it does not ask for a debugging port.</summary>
        Off = 1,
        /// <summary>Found, and it asks for a port. Whether one LISTENS is a separate question.</summary>
        On = 2,
        /// <summary>Found, asks for a port, and the port answers. The only state that means working.</summary>
        Listening = 3,
        /// <summary>Found, but the file is not something this module will edit.</summary>
        Unreadable = 4,
    }

    /// <summary>The result of looking, or of editing.</summary>
    public sealed class SetupReport
    {
        public SetupState State;
        public string Path;
        public int Port;
        // Removed: written by Inspect, read by nothing, and the write was a process
        // enumeration. Ask IsVsCodeRunning() directly if you need it.
        /// <summary>Written so it can say the operation FAILED. Never a bare "done".</summary>
        public string Detail;
    }

    /// <summary>
    /// Turns the CDP port in VS Code's runtime-arguments file on and off, and says honestly whether
    /// it is actually working.
    ///
    /// WHY THIS IS NOT A ONE-LINE FILE WRITE. Four things make it worth its own class, and each one
    /// is a defect in the obvious version:
    ///
    ///   1. argv.json is JSONC. The file VS Code ships carries FOURTEEN comment lines, including
    ///      "PLEASE DO NOT CHANGE WITHOUT UNDERSTANDING THE IMPACT", and a strict JSON parse of it
    ///      fails outright. So this edits TEXT and never parses-then-reserializes: the latter either
    ///      throws or silently deletes Microsoft's own documentation out of the user's config.
    ///   2. THE VALUE MUST BE A STRING. VS Code's own handler reads
    ///      `if (c === true || c === "true") appendSwitch(key) else if (typeof c === "string" && c)
    ///      appendSwitch(key, c)`, so a JSON NUMBER matches neither branch and is accepted by the
    ///      file and ignored at launch. `"remote-debugging-port": 9222` does nothing at all, which
    ///      is the kind of failure that looks like success.
    ///   3. WRITING THE FILE PROVES NOTHING. The port exists only after a restart, so "enabled"
    ///      cannot mean "the write succeeded". <see cref="Probe"/> connects, and the state machine
    ///      distinguishes On (asked for) from Listening (answering).
    ///   4. IT IS A LIFECYCLE. A key left in argv.json means every future VS Code launch opens an
    ///      unauthenticated loopback port, not just while this module is in use. So Disable exists,
    ///      removes the key, and is as easy to reach as Enable.
    ///
    /// The allowlist was read out of the installed builds rather than taken from documentation:
    /// `main.js` in both 1.137.0 and 1.138.0 carries
    /// ["disable-hardware-acceleration","force-color-profile","disable-lcd-text","proxy-bypass-list",
    /// "remote-debugging-port"], so the key is honoured from this file and no CLI launch is needed.
    /// </summary>
    public static class VsCodeSetup
    {
        /// <summary>The argv.json key. Honoured by VS Code 1.137 and 1.138; verified in main.js.</summary>
        public const string PortKey = "remote-debugging-port";

        /// <summary>
        /// Not 9222. That is the default every Chromium tool reaches for, including this box's own
        /// debug Chrome, and two processes cannot share it -- the second simply fails to listen and
        /// the user is left with a port that answers to the wrong program.
        /// </summary>
        public const int DefaultPort = 9321;

        /// <summary>
        /// Where VS Code looks for argv.json, in ITS order of preference.
        ///
        /// Taken from the resolver in main.js: VSCODE_PORTABLE wins, otherwise the data folder under
        /// the user profile, with a `-dev` suffix when VSCODE_DEV is set. Guessing only
        /// %USERPROFILE%\.vscode would silently edit a file a portable install never reads.
        /// </summary>
        public static IEnumerable<string> CandidatePaths()
        {
            string portable = Environment.GetEnvironmentVariable("VSCODE_PORTABLE");
            if (!string.IsNullOrWhiteSpace(portable))
                yield return Path.Combine(portable, "argv.json");
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VSCODE_DEV")))
                yield return Path.Combine(home, ".vscode-dev", "argv.json");
            yield return Path.Combine(home, ".vscode", "argv.json");
            yield return Path.Combine(home, ".vscode-insiders", "argv.json");
        }

        /// <summary>True while any VS Code window is open. It rewrites argv.json itself, and the
        /// restart that applies our edit is the user's to perform.</summary>
        public static bool IsVsCodeRunning()
        {
            foreach (string name in new[] { "Code", "Code - Insiders" })
            {
                try
                {
                    if (System.Diagnostics.Process.GetProcessesByName(name).Length > 0) return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>The port named in the file, or 0. Text scan, because the file is JSONC.</summary>
        public static int ReadPort(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            string body = StripLineComments(text);
            int key = body.IndexOf("\"" + PortKey + "\"", StringComparison.Ordinal);
            if (key < 0) return 0;
            int colon = body.IndexOf(':', key);
            if (colon < 0) return 0;
            var digits = new StringBuilder();
            for (int i = colon + 1; i < body.Length; i++)
            {
                char c = body[i];
                if (char.IsDigit(c)) digits.Append(c);
                else if (c == '"' || c == ' ' || c == '\t') { if (digits.Length > 0) break; }
                else break;
            }
            int value;
            return int.TryParse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture,
                                out value) ? value : 0;
        }

        /// <summary>
        /// Comments removed for INSPECTION only, never for writing. A `//` inside a string literal
        /// is left alone, because a Windows path in a value ("C:\\x//y") would otherwise truncate
        /// the line and hide a real key.
        /// </summary>
        public static string StripLineComments(string text)
        {
            var output = new StringBuilder(text.Length);
            bool inString = false, escaped = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    output.Append(c);
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; output.Append(c); continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    if (i < text.Length) output.Append('\n');
                    continue;
                }
                output.Append(c);
            }
            return output.ToString();
        }

        /// <summary>
        /// Insert or update the port key, preserving every comment and the file's line endings.
        ///
        /// Returns the new text, or null when the file is not something to edit. Refusing is a real
        /// outcome: a file with no top-level object is either not argv.json or is corrupt, and
        /// writing a fresh one over it would destroy whatever the user had.
        /// </summary>
        public static string WithPort(string text, int port)
        {
            if (port <= 0 || port > 65535) return null;
            string value = "\"" + port.ToString(CultureInfo.InvariantCulture) + "\"";
            string existing = FindKeySpan(text);
            if (existing != null)
            {
                // Replace just the VALUE, leaving the key, its indentation and any trailing comment.
                int keyAt = text.IndexOf(existing, StringComparison.Ordinal);
                int colon = text.IndexOf(':', keyAt);
                if (colon < 0) return null;
                int end = colon + 1;
                while (end < text.Length && text[end] != ',' && text[end] != '\n'
                       && text[end] != '}') end++;
                return text.Substring(0, colon + 1) + " " + value + text.Substring(end);
            }

            int brace = IndexOfTopLevelBrace(text);
            if (brace < 0) return null;
            string newline = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            // A comma is needed only when the object already has a member. Detecting that on the
            // COMMENT-STRIPPED text, because the first thing after `{` is usually a comment block.
            bool hasMember = HasAnyMember(text, brace);
            string inserted = newline + "\t\"" + PortKey + "\": " + value + (hasMember ? "," : "");
            return text.Substring(0, brace + 1) + inserted + text.Substring(brace + 1);
        }

        /// <summary>Remove the key entirely, with its line, leaving the rest byte-for-byte.</summary>
        public static string WithoutPort(string text)
        {
            string span = FindKeySpan(text);
            if (span == null) return text;
            int keyAt = text.IndexOf(span, StringComparison.Ordinal);
            int lineStart = text.LastIndexOf('\n', keyAt) + 1;
            int lineEnd = text.IndexOf('\n', keyAt);
            if (lineEnd < 0) lineEnd = text.Length; else lineEnd++;
            string before = text.Substring(0, lineStart);
            string after = text.Substring(lineEnd);
            // Removing the LAST member leaves a dangling comma on the line above it.
            string joined = before + after;
            return FixDanglingComma(joined);
        }

        private static string FixDanglingComma(string text)
        {
            string stripped = StripLineComments(text);
            int close = stripped.LastIndexOf('}');
            if (close < 0) return text;
            // Walk back from the closing brace over whitespace; a comma there is now dangling.
            int i = close - 1;
            while (i >= 0 && char.IsWhiteSpace(stripped[i])) i--;
            if (i < 0 || stripped[i] != ',') return text;
            // Map that index back into the ORIGINAL text by counting commas, which is exact because
            // StripLineComments preserves every non-comment character.
            int target = CountUpTo(stripped, i, ',');
            int seen = 0;
            for (int j = 0; j < text.Length; j++)
            {
                if (text[j] != ',') continue;
                if (seen == target) return text.Substring(0, j) + text.Substring(j + 1);
                seen++;
            }
            return text;
        }

        private static int CountUpTo(string text, int index, char what)
        {
            int count = 0;
            for (int i = 0; i < index; i++) if (text[i] == what) count++;
            return count;
        }

        private static string FindKeySpan(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string stripped = StripLineComments(text);
            string needle = "\"" + PortKey + "\"";
            return stripped.IndexOf(needle, StringComparison.Ordinal) >= 0 ? needle : null;
        }

        private static int IndexOfTopLevelBrace(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            string stripped = StripLineComments(text);
            int at = stripped.IndexOf('{');
            if (at < 0) return -1;
            // Same offset in the original, because stripping preserves non-comment characters only
            // by REPLACING comments with a newline -- so count instead.
            int seen = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '{') continue;
                if (seen == CountUpTo(stripped, at, '{')) return i;
                seen++;
            }
            return -1;
        }

        private static bool HasAnyMember(string text, int braceIndex)
        {
            string stripped = StripLineComments(text.Substring(braceIndex + 1));
            foreach (char c in stripped)
            {
                if (c == '}') return false;
                if (c == '"') return true;
            }
            return false;
        }

        /// <summary>
        /// Does something answer on the port? This is the only check that means "working".
        ///
        /// A plain TCP connect, not an HTTP request for /json/version: the question here is whether
        /// the port is open, and a connect answers it without this module speaking CDP or pulling in
        /// an HTTP client. Short timeout, because it runs behind a button press.
        /// </summary>
        public static bool Probe(int port, int timeoutMilliseconds)
        {
            if (port <= 0 || port > 65535) return false;
            try
            {
                using (var client = new TcpClient())
                {
                    IAsyncResult pending = client.BeginConnect("127.0.0.1", port, null, null);
                    if (!pending.AsyncWaitHandle.WaitOne(timeoutMilliseconds)) return false;
                    client.EndConnect(pending);
                    return client.Connected;
                }
            }
            catch { return false; }
        }

        /// <summary>Look, and report. Never writes.</summary>
        public static SetupReport Inspect(string overridePath, int probeTimeoutMilliseconds)
        {
            // IsVsCodeRunning() is NOT called here any more. The field nobody read cost a
            // full process enumeration on every Inspect, which runs on each pane open. The
            // two callers that genuinely want the answer ask the method directly.
            var report = new SetupReport();
            string path = overridePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                foreach (string candidate in CandidatePaths())
                {
                    try { if (File.Exists(candidate)) { path = candidate; break; } }
                    catch { }
                }
            }
            if (string.IsNullOrWhiteSpace(path) || !SafeExists(path))
            {
                report.State = SetupState.NotFound;
                report.Detail = "no argv.json found. VS Code creates it on first run; if yours "
                                + "lives somewhere else, point at it with Browse.";
                return report;
            }
            report.Path = path;
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception exception)
            {
                report.State = SetupState.Unreadable;
                report.Detail = "could not read " + path + ": " + exception.GetType().Name;
                return report;
            }
            if (IndexOfTopLevelBrace(text) < 0)
            {
                report.State = SetupState.Unreadable;
                report.Detail = "the file has no top-level { } object, so it is either not "
                                + "argv.json or it is damaged. Not editing it.";
                return report;
            }
            report.Port = ReadPort(text);
            if (report.Port <= 0)
            {
                report.State = SetupState.Off;
                report.Detail = "found " + path + "; it does not ask for a debugging port.";
                return report;
            }
            bool listening = Probe(report.Port, probeTimeoutMilliseconds);
            report.State = listening ? SetupState.Listening : SetupState.On;
            report.Detail = listening
                ? "port " + report.Port + " is open and answering."
                : "argv.json asks for port " + report.Port + ", but nothing is answering there. "
                  + "VS Code applies this at launch, so it needs a restart -- or another program "
                  + "already holds the port.";
            return report;
        }

        private static bool SafeExists(string path)
        {
            try { return File.Exists(path); } catch { return false; }
        }
    }
}
