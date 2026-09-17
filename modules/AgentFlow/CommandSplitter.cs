using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>One top-level segment of a compound command, plus the separator that preceded it.</summary>
    public sealed class CommandSegment
    {
        public string Value;
        /// <summary>"&amp;&amp;", "||", "|", "|&amp;", ";", "&amp;", "\n", or null for the first segment.</summary>
        public string Separator;
    }

    /// <summary>
    /// Splits a compound command at its TOP-LEVEL separators only, leaving quoted and grouped
    /// constructs intact.
    ///
    /// Why this exists at all: Claude Code evaluates each sub-command of a compound against the
    /// permission rules SEPARATELY, so a chain is only "allowed" when every part is. Split it wrong
    /// and the rule join is corrupted in both directions -- an over-split invents a root that was
    /// never a command and reports a prompt that would not happen, while an under-split hides the
    /// part that would.
    ///
    /// THIS IS THE THIRD IMPLEMENTATION OF ONE ALGORITHM. The original is JavaScript, in the sibling
    /// permission-wildcarding project (src/auto-learn.js: splitSegmentsDetailed); a Python port lives
    /// in docs/agentflow/agentflow_join.py for the measurement harness; this is the shipping one.
    /// Three copies drift, and the drift is invisible because each produces a plausible segment list.
    /// So the differential is not optional: agentflow_join.py --difftest compares the Python against
    /// the JS, and AgentFlowModule.SelfTest compares THIS against the same labelled corpus. Change
    /// the algorithm here and you must change it in all three.
    ///
    /// Ported rather than rewritten, deliberately. The original is already exercised against both
    /// agents' full history in that project, and a fresh implementation would re-earn every one of
    /// the edge cases below the hard way: a bare `&amp;` is a separator but `2&gt;&amp;1` and `&amp;&gt;` are not, a
    /// heredoc body is data rather than commands and can sit inside a command substitution, and the
    /// escape character differs between PowerShell (backtick) and bash (backslash).
    /// </summary>
    public static class CommandSplitter
    {
        public const string ShellPowerShell = "powershell";
        public const string ShellBash = "bash";

        private static readonly Regex PowerShellShell =
            new Regex(@"^(?:powershell|pwsh|ps)$", RegexOptions.Compiled);
        private static readonly Regex PosixShell =
            new Regex(@"^(?:bash|sh|zsh|fish|ksh|dash)$", RegexOptions.Compiled);

        // An unquoted heredoc delimiter must be UPPER-CASE -- the real-world convention -- so that
        // `echo "a << b"` and the `<<<` here-string keep their ordinary handling.
        private static readonly Regex HeredocOpener = new Regex(
            @"(?<!<)<<-?[ \t]*(?:(['""])([A-Za-z_][A-Za-z0-9_]*)\1|([A-Z][A-Z0-9_]*))",
            RegexOptions.Compiled);

        /// <summary>Resolve a shell name, falling back to the tool name and then to bash.</summary>
        public static string NormalizeShell(string shell, string tool)
        {
            string explicitShell = (shell ?? string.Empty).Trim().ToLowerInvariant();
            string toolName = (tool ?? string.Empty).Trim().ToLowerInvariant();
            if (PowerShellShell.IsMatch(explicitShell)) return ShellPowerShell;
            if (PosixShell.IsMatch(explicitShell)) return ShellBash;
            if (explicitShell.Length > 0) return explicitShell;
            if (toolName.IndexOf("powershell", StringComparison.Ordinal) >= 0 || toolName == "pwsh")
                return ShellPowerShell;
            if (toolName == "bash") return ShellBash;
            if (toolName.IndexOf("shell_command", StringComparison.Ordinal) >= 0
                || toolName.IndexOf("exec_command", StringComparison.Ordinal) >= 0
                || toolName == "shell")
            {
                // The host is always Windows here, so this arm matches the JS on this platform.
                return ShellPowerShell;
            }
            return ShellBash;
        }

        /// <summary>
        /// Blank out heredoc bodies, preserving line count, so they cannot be segmented.
        ///
        /// A heredoc body is data, not commands, and it can appear inside quotes or a command
        /// substitution (`git commit -m "$(cat &lt;&lt;'EOF' ... EOF)"`), so masking it up front is both
        /// simpler and safer than carrying it as parser state through the scanner below.
        /// </summary>
        public static string MaskHeredocBodies(string text)
        {
            if (text == null || text.IndexOf("<<", StringComparison.Ordinal) < 0) return text;
            var output = new List<string>();
            var pending = new List<string>();
            foreach (string line in text.Split('\n'))
            {
                if (pending.Count > 0)
                {
                    output.Add(string.Empty);
                    if (line.TrimEnd('\r').Trim() == pending[0]) pending.RemoveAt(0);
                    continue;
                }
                output.Add(line);
                foreach (Match match in HeredocOpener.Matches(line))
                {
                    string delimiter = match.Groups[2].Success
                        ? match.Groups[2].Value
                        : match.Groups[3].Value;
                    pending.Add(delimiter);
                }
            }
            return string.Join("\n", output);
        }

        /// <summary>Character at an index, or '\0' past either end.</summary>
        private static char At(string text, int index)
        {
            if (index < 0 || index >= text.Length) return '\0';
            return text[index];
        }

        private static bool IsCommentStart(string text, int index)
        {
            return text[index] == '#'
                   && (index == 0 || char.IsWhiteSpace(At(text, index - 1)));
        }

        /// <summary>A bare `&amp;` backgrounds a job; `&amp;&gt;` and `2&gt;&amp;1` are redirections, not separators.</summary>
        private static bool IsBashAmpersandSeparator(string text, int index)
        {
            if (At(text, index + 1) == '>') return false;
            int previous = index - 1;
            while (previous >= 0 && char.IsWhiteSpace(text[previous])) previous--;
            return At(text, previous) != '>';
        }

        /// <summary>Segments with their separators. Never null; empty for blank input.</summary>
        public static List<CommandSegment> SplitDetailed(string command, string shell)
        {
            string mode = NormalizeShell(shell, null);
            string raw = command ?? string.Empty;
            string text = mode == ShellPowerShell ? raw : MaskHeredocBodies(raw);

            var segments = new List<CommandSegment>();
            var current = new StringBuilder();
            string quote = null;
            int depth = 0;
            string separator = null;

            Action flush = () =>
            {
                string value = current.ToString().Trim();
                current.Length = 0;
                if (value.Length > 0)
                    segments.Add(new CommandSegment { Value = value, Separator = separator });
            };

            int i = 0, n = text.Length;
            while (i < n)
            {
                char ch = text[i];
                char next = At(text, i + 1);

                if (quote == "single")
                {
                    current.Append(ch);
                    if (mode == ShellPowerShell && ch == '\'' && next == '\'')
                    {
                        i++;
                        current.Append(text[i]);
                    }
                    else if (ch == '\'') quote = null;
                    i++;
                    continue;
                }

                if (quote == "double")
                {
                    current.Append(ch);
                    if ((mode == ShellPowerShell && ch == '`') || (mode != ShellPowerShell && ch == '\\'))
                    {
                        if (i + 1 < n)
                        {
                            i++;
                            current.Append(text[i]);
                        }
                    }
                    else if (ch == '"') quote = null;
                    i++;
                    continue;
                }

                // PowerShell block comment <# ... #>
                if (mode == ShellPowerShell && ch == '<' && next == '#')
                {
                    int end = text.IndexOf("#>", i + 2, StringComparison.Ordinal);
                    current.Append(' ');
                    i = (end < 0 ? n : end + 1) + 1;
                    continue;
                }

                // PowerShell here-string @" ... "@ / @' ... '@
                if (mode == ShellPowerShell && ch == '@' && (next == '"' || next == '\''))
                {
                    string terminator = next == '"' ? "\"@" : "'@";
                    int end = text.IndexOf(terminator, i + 2, StringComparison.Ordinal);
                    current.Append(' ');
                    i = end < 0 ? n : end + terminator.Length;
                    continue;
                }

                if (IsCommentStart(text, i))
                {
                    while (i < n && text[i] != '\n' && text[i] != '\r') i++;
                    flush();
                    separator = "\n";
                    if (At(text, i) == '\r' && At(text, i + 1) == '\n') i++;
                    i++;
                    continue;
                }

                if (ch == '\'')
                {
                    quote = "single";
                    current.Append(ch);
                    i++;
                    continue;
                }
                if (ch == '"')
                {
                    quote = "double";
                    current.Append(ch);
                    i++;
                    continue;
                }
                if ((mode == ShellPowerShell && ch == '`') || (mode != ShellPowerShell && ch == '\\'))
                {
                    current.Append(ch);
                    if (i + 1 < n)
                    {
                        i++;
                        current.Append(text[i]);
                    }
                    i++;
                    continue;
                }

                if (ch == '(' || ch == '[' || ch == '{')
                {
                    depth++;
                    current.Append(ch);
                    i++;
                    continue;
                }
                if (ch == ')' || ch == ']' || ch == '}')
                {
                    depth = Math.Max(0, depth - 1);
                    current.Append(ch);
                    i++;
                    continue;
                }

                if (depth == 0)
                {
                    int length = 0;
                    if (ch == '\r' || ch == '\n' || ch == ';')
                        length = (ch == '\r' && next == '\n') ? 2 : 1;
                    else if (ch == '&' && next == '&') length = 2;
                    else if (ch == '|' && (next == '|' || next == '&')) length = 2;
                    else if (ch == '|') length = 1;
                    else if (mode != ShellPowerShell && ch == '&' && IsBashAmpersandSeparator(text, i))
                        length = 1;

                    if (length > 0)
                    {
                        flush();
                        separator = (ch == '\r' || ch == '\n') ? "\n" : text.Substring(i, length);
                        i += length;
                        continue;
                    }
                }

                current.Append(ch);
                i++;
            }

            flush();
            return segments;
        }

        /// <summary>Just the segment texts, which is all the rule join needs.</summary>
        public static List<string> Split(string command, string shell)
        {
            List<CommandSegment> detailed = SplitDetailed(command, shell);
            var values = new List<string>(detailed.Count);
            foreach (CommandSegment segment in detailed) values.Add(segment.Value);
            return values;
        }
    }
}
