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
        /// <summary>
        /// Found, names a port, and VS Code will IGNORE it: the value is an unquoted JSON number.
        ///
        /// Its own handler takes `true`, `"true"` or a non-empty string and calls appendSwitch; a
        /// number matches no branch. So `"remote-debugging-port": 9321` is valid JSON, parses as a
        /// port, and does nothing at launch. Before this state existed the module read that number,
        /// reported "argv.json asks for port 9321", probed, found silence, and blamed a missing
        /// restart -- forever, because a restart was never going to help. The class doc above called
        /// this exact shape "the kind of failure that looks like success" and then did not detect it.
        /// </summary>
        Inert = 5,
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
            bool ignored;
            return ReadPort(text, out ignored);
        }

        /// <summary>
        /// The port named in the file, or 0, AND whether it is written in the form VS Code honours.
        ///
        /// <paramref name="quoted"/> is the whole point. VS Code takes `true`, `"true"` or a
        /// non-empty string; an unquoted number matches none of its branches and is dropped at
        /// launch. Reading the number and saying nothing about its form is what let the pane insist
        /// a restart would help when nothing could.
        /// </summary>
        public static int ReadPort(string text, out bool quoted)
        {
            quoted = false;
            if (string.IsNullOrEmpty(text)) return 0;
            // BLANKED, not stripped: offsets here also index the original, which the callers below
            // rely on, and a commented-out key must never be mistaken for the live one.
            string body = BlankLineComments(text);
            int key = body.IndexOf("\"" + PortKey + "\"", StringComparison.Ordinal);
            if (key < 0) return 0;
            int colon = body.IndexOf(':', key);
            if (colon < 0) return 0;

            int i = colon + 1;
            while (i < body.Length && (body[i] == ' ' || body[i] == '\t')) i++;
            bool opensWithQuote = i < body.Length && body[i] == '"';
            if (opensWithQuote) i++;

            var digits = new StringBuilder();
            for (; i < body.Length; i++)
            {
                char c = body[i];
                if (char.IsDigit(c)) digits.Append(c);
                else break;
            }
            int value;
            if (!int.TryParse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture,
                              out value))
                return 0;
            quoted = opensWithQuote;
            return value;
        }

        /// <summary>
        /// Comments removed, for the SELF-TEST's inspection and for two mutation fixtures, and for
        /// nothing else: every production edit in this class runs on <see cref="BlankLineComments"/>,
        /// which preserves the length so an offset into the result indexes the original (F061). A `//`
        /// inside a string literal is left alone, because a Windows path in a value ("C:\\x//y") would
        /// otherwise truncate the line and hide a real key.
        ///
        /// INTERNAL since 2026-09-30 (R-010). F061 removed its last production caller, and a public
        /// method on a public static class reads as a supported entry point -- which invites the next
        /// offset-based edit to reach for it and reintroduce exactly the class of defect F061 fixed.
        /// It is kept rather than deleted because two things still name it: SelfCheckVsCodeSetup, which
        /// asserts the shipped argv.json is JSONC, and tests/mutate-agentflow.py's two F061 mutants,
        /// which restore it as the shape they break. Both are in this assembly, so internal is enough.
        /// </summary>
        internal static string StripLineComments(string text)
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
        /// Comments blanked to SPACES rather than removed, so every index into the result is also a
        /// valid index into the original text.
        ///
        /// <see cref="StripLineComments"/> deletes them, which is right for inspection and wrong for
        /// anything that then edits by offset. FindKeySpan used to confirm a LIVE key against the
        /// stripped text and hand back only the needle, and WithPort/WithoutPort located it with
        /// IndexOf on the ORIGINAL -- so with a commented-out `remote-debugging-port` line above the
        /// live one, Disable deleted the COMMENT, saw the text change, and reported the port removed
        /// while it kept opening on every launch.
        ///
        /// EVERY offset-based edit in this class runs on the blanked text now. The sentence that used
        /// to end this paragraph said IndexOfTopLevelBrace "does this mapping properly": it did not.
        /// It and FixDanglingComma found their character in the STRIPPED text and mapped it back by
        /// counting braces or commas, on the premise that stripping preserves every non-comment
        /// character -- true, and beside the point, because a comment can hold a brace or a comma
        /// too, and VS Code's own stock file does (F061). Counting is gone; an index into the
        /// blanked text is used directly on the original, and there is nothing left to get wrong.
        /// </summary>
        private static string BlankLineComments(string text)
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
                    while (i < text.Length && text[i] != '\n') { output.Append(' '); i++; }
                    if (i < text.Length) output.Append('\n');
                    continue;
                }
                output.Append(c);
            }
            return output.ToString();
        }

        /// <summary>Index of the LIVE port key in <paramref name="text"/>, or -1. Never a commented one.</summary>
        private static int FindKeyIndex(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            return BlankLineComments(text)
                .IndexOf("\"" + PortKey + "\"", StringComparison.Ordinal);
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
            string blanked = BlankLineComments(text ?? "");
            int keyAt = blanked.IndexOf("\"" + PortKey + "\"", StringComparison.Ordinal);
            if (keyAt >= 0)
            {
                // Replace just the VALUE, leaving the key, its indentation and any trailing comment.
                // keyAt comes from BlankLineComments, so it indexes the LIVE key: rewriting the first
                // textual match would edit a commented-out line and leave the real one untouched.
                //
                // The value span is found on the BLANKED text too, and spliced out of the original
                // at the same indices. This used to scan the ORIGINAL forward from the colon to the
                // first comma, newline or brace, so on a LAST member -- no comma -- it ran to the
                // newline and the replaced span swallowed a trailing `// ...` comment, which the
                // sentence above promised to keep; and a comma INSIDE that comment stopped the scan
                // early and left half a comment behind, which is not JSONC any more (F060). Blanked,
                // a comment is a run of spaces, so the value ends where the value ends.
                int colon = blanked.IndexOf(':', keyAt);
                if (colon < 0) return null;
                int start = colon + 1;
                while (start < blanked.Length && (blanked[start] == ' ' || blanked[start] == '\t')) start++;
                int end = ValueEnd(blanked, start);
                return text.Substring(0, start) + value + text.Substring(end);
            }

            int brace = IndexOfTopLevelBrace(text);
            if (brace < 0) return null;
            string newline = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            // A comma is needed only when the object already has a member. Detecting that on the
            // COMMENT-BLANKED text, because the first thing after `{` is usually a comment block.
            bool hasMember = HasAnyMember(text, brace);
            string inserted = newline + "\t\"" + PortKey + "\": " + value + (hasMember ? "," : "");
            return text.Substring(0, brace + 1) + inserted + text.Substring(brace + 1);
        }

        /// <summary>
        /// Remove the key, leaving the rest byte-for-byte.
        ///
        /// THE MEMBER, NOT THE LINE IT SITS ON (RA-052). This deleted from the start of the key's line
        /// to the end of it, which is right for the pretty-printed file VS Code ships and wrong for
        /// every other shape: a one-line argv.json lost its siblings and came back `{`-less or empty,
        /// and a port key sharing a line with another member took that member with it -- and
        /// DisableCdpAsync wrote the result and reported success, because FixDanglingComma returns the
        /// text unchanged when it finds no closing brace. The member's own span is spliced out now,
        /// with the comma that separates it from the next member (or, when it is the last member, the
        /// comma before it), and the line goes only when nothing but whitespace is left on it -- which
        /// is what the old whole-line delete got right, and the only reason it survived this long.
        /// </summary>
        public static string WithoutPort(string text)
        {
            // Offsets come from the BLANKED text and are used on the original, so a commented-out key
            // cannot be mistaken for the live one: this used to be text.IndexOf(needle), which finds the
            // comment first -- so Disable deleted the comment, saw the text change, reported the port
            // removed, and left an unauthenticated loopback port opening on every VS Code launch.
            if (string.IsNullOrEmpty(text)) return text;
            string blanked = BlankLineComments(text);
            int keyAt = blanked.IndexOf("\"" + PortKey + "\"", StringComparison.Ordinal);
            if (keyAt < 0) return text;
            int colon = blanked.IndexOf(':', keyAt);
            if (colon < 0) return text;
            int valueStart = colon + 1;
            while (valueStart < blanked.Length
                   && (blanked[valueStart] == ' ' || blanked[valueStart] == '\t')) valueStart++;
            int cut = ValueEnd(blanked, valueStart);
            // The separator AFTER the member, when there is one.
            int afterValue = cut;
            while (afterValue < blanked.Length
                   && (blanked[afterValue] == ' ' || blanked[afterValue] == '\t')) afterValue++;
            bool tookNextComma = afterValue < blanked.Length && blanked[afterValue] == ',';
            if (tookNextComma) cut = afterValue + 1;
            int start = keyAt;
            if (!tookNextComma)
            {
                // The LAST member: take the comma that preceded it instead, or the object would keep a
                // dangling one. FixDanglingComma stays below as the backstop it always was.
                int back = keyAt - 1;
                while (back >= 0 && char.IsWhiteSpace(blanked[back])) back--;
                if (back >= 0 && blanked[back] == ',') start = back;
            }
            string spliced = text.Substring(0, start) + text.Substring(cut);
            return FixDanglingComma(RemoveBlankLineAt(spliced, start));
        }

        /// <summary>
        /// Drop the line holding <paramref name="index"/> when nothing but whitespace is left on it,
        /// which is what removing a member that had its own line leaves behind. The CR of a CRLF file
        /// goes with it, so the file's line endings survive.
        /// </summary>
        private static string RemoveBlankLineAt(string text, int index)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (index < 0) index = 0;
            if (index > text.Length) index = text.Length;
            int lineStart = index == 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
            int lineEnd = text.IndexOf('\n', lineStart);
            int cut = lineEnd < 0 ? text.Length : lineEnd + 1;
            for (int i = lineStart; i < cut; i++)
                if (!char.IsWhiteSpace(text[i])) return text;
            return text.Substring(0, lineStart) + text.Substring(cut);
        }

        /// <summary>
        /// Removing the LAST member leaves the comma of the member before it dangling; take it out.
        ///
        /// ON THE BLANKED TEXT, and that is the whole of F061. This used to find the dangling comma
        /// in the comment-STRIPPED text and map it back into the original by counting commas, on the
        /// stated premise that stripping "preserves every non-comment character". It does, and it
        /// is beside the point: a COMMENT can hold a comma, and VS Code's own stock argv.json does
        /// -- `// "disable-hardware-acceleration": true,` sits above the members in every installed
        /// copy, written by VS Code itself. So with the port key hand-appended as the last member,
        /// Disable counted one comma short, deleted the comma after `"enable-crash-reporter": true`
        /// instead, and left a missing comma AND a trailing one. VS Code's reader tolerates only the
        /// second, so every launch fell back to defaults for the WHOLE file -- crash-reporter id,
        /// locale, comments -- while this module, seeing the text change and the write succeed,
        /// reported the port removed. The module's own Enable inserts the key FIRST, so its own
        /// round trip never formed the shape; a hand edit following the usual instructions does.
        ///
        /// BlankLineComments keeps the length, so an index into it is an index into the original
        /// and nothing needs mapping. The rejected alternative was to keep the count and also
        /// count commas inside comments, which is the same mistake with more arithmetic.
        /// </summary>
        private static string FixDanglingComma(string text)
        {
            string blanked = BlankLineComments(text);
            int close = blanked.LastIndexOf('}');
            if (close < 0) return text;
            // Walk back from the closing brace over whitespace; a comma there is now dangling.
            int i = close - 1;
            while (i >= 0 && char.IsWhiteSpace(blanked[i])) i--;
            if (i < 0 || blanked[i] != ',') return text;
            return text.Remove(i, 1);
        }

        /// <summary>
        /// One past the last character of the JSON value that starts at <paramref name="start"/> in
        /// BLANKED text. A quoted string runs to its closing quote, escapes honoured; anything else
        /// -- a number, true, false, null -- runs to the first whitespace, comma or closing brace.
        /// Blanked, so a trailing comment is whitespace and can never fall inside the span.
        /// </summary>
        private static int ValueEnd(string blanked, int start)
        {
            if (start >= blanked.Length) return start;
            if (blanked[start] == '"')
            {
                bool escaped = false;
                for (int i = start + 1; i < blanked.Length; i++)
                {
                    char c = blanked[i];
                    if (escaped) { escaped = false; continue; }
                    if (c == '\\') { escaped = true; continue; }
                    if (c == '"') return i + 1;
                }
                return blanked.Length;
            }
            int end = start;
            while (end < blanked.Length && !char.IsWhiteSpace(blanked[end])
                   && blanked[end] != ',' && blanked[end] != '}') end++;
            return end;
        }

        /// <summary>
        /// Index of the first `{` outside a comment, or -1. Blanked for the same reason as
        /// <see cref="FixDanglingComma"/>: this used to count braces through the stripped text, so a
        /// `{` inside the header comment shifted the count and WithPort spliced the key into that
        /// comment line -- a broken file that ReadPort then read as live, so Inspect would have said
        /// "waiting for a restart" for ever.
        /// </summary>
        private static int IndexOfTopLevelBrace(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            return BlankLineComments(text).IndexOf('{');
        }

        private static bool HasAnyMember(string text, int braceIndex)
        {
            string blanked = BlankLineComments(text);
            for (int i = braceIndex + 1; i < blanked.Length; i++)
            {
                if (blanked[i] == '}') return false;
                if (blanked[i] == '"') return true;
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
        /// <summary>
        /// Is anything listening on that loopback port?
        ///
        /// DO NOT "SIMPLIFY" THE TIMEOUT AWAY. The obvious reading is that a closed loopback port
        /// refuses instantly, so the wait is pointless ceremony. MEASURED 2026-09-21, one call per
        /// fresh process: a synchronous connect to a closed port on this box takes **2,063 ms** to
        /// come back with ConnectionRefused (2063.1 / 2066.3 / 2066.1). The bound is the only
        /// reason this returns in a quarter of a second instead of two.
        ///
        /// Also measured, because each looked like the bug at the time and none was: an explicit
        /// IPv4 IPEndPoint instead of the host string, and ConnectAsync + Task.Wait instead of
        /// BeginConnect, both come back at ~275 ms against a closed port, identical to this. The
        /// ~250 ms is the timeout doing its job, not a wait failing to notice an answer. Three
        /// APIs agreeing to the millisecond is what that looks like.
        ///
        /// A LISTENING port answers in ~25 ms including process start, so the fast path is fast
        /// and needs nothing.
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
                // The pane's own label (RA-053). "Browse" is a button this pane has never had.
                report.Detail = "no argv.json found. VS Code creates it on first run; if yours "
                                + "lives somewhere else, point at it with “"
                                + AgentFlowModule.FindArgvLabel + "”.";
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
            bool quoted;
            report.Port = ReadPort(text, out quoted);
            if (report.Port <= 0)
            {
                report.State = SetupState.Off;
                report.Detail = "found " + path + "; it does not ask for a debugging port.";
                return report;
            }
            if (!quoted)
            {
                // Do NOT probe. The port was never requested as far as VS Code is concerned, so
                // silence on it proves nothing and "needs a restart" would be a lie.
                report.State = SetupState.Inert;
                // The pane's own label (RA-053): this said \u201cEnable approving\u201d, renamed in 611db4d.
                report.Detail = "argv.json names port " + report.Port + " as a NUMBER, and VS Code "
                                + "only honours a quoted string, so it is ignored at every launch. "
                                + "Press \u201c" + AgentFlowModule.EnableLabel + "\u201d to rewrite it as \""
                                + report.Port.ToString(CultureInfo.InvariantCulture)
                                + "\", then restart VS Code.";
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
