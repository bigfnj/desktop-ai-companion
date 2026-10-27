using System;
using System.IO;
using System.Text;
using DesktopAICompanion.CodingAgent;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// "After the summary" (remembrance 2.8.0, lane feature/remembrance-next; the owner, 2026-10-27: "yes, build the ability
    /// to chain please"): once a summary is written, it is passed to a second coding-agent CLI, Codex unless the user picks
    /// Claude Code, with a prompt the user writes, for whatever other work they ask of it. The answer is saved beside the
    /// summary as "&lt;summary's name&gt; - follow-up.md". The prompt is a .txt file the card points at, read again for every
    /// follow-up, the summary prompt's mechanism (SummaryPrompt, the owner's design of 2026-10-27); there is no built-in one,
    /// so with no usable file no follow-up runs, and that is said.
    ///
    /// OFF BY DEFAULT, and when on it runs after EVERY summary, a recording's and one made with "Summarize a transcript…"
    /// alike, whichever engine wrote it; "Run the follow-up on the latest summary" runs it by hand, on or off. What it sends is
    /// the summary, and the transcript too only when "The follow-up reads" says so; the card's "Goes through" rows and
    /// PRIVACY.md say that the summary then also goes to that CLI's vendor. Its model, effort and Claude Code switch are the
    /// ones the Coding-agent CLI card saves for that CLI (SummaryRoute.ModelFor and the rest), so one CLI is set up in one
    /// place and the follow-up honours the same privacy choices; refused: a second set of model and effort rows in this card,
    /// four rows that would have to be kept in step with the CLI card's. A follow-up that fails is said in the Status line and
    /// its card, and never touches the summary: nothing here writes the summary's file.
    /// </summary>
    internal static class FollowUp
    {
        internal const string OnKey = "followUpOn";
        internal const string CliKey = "followUpCli";
        internal const string InputKey = "followUpInput";
        internal const string PromptFileKey = "followUpPromptFile";
        internal const string PromptFileLabel = "Follow-up prompt file (.txt)";
        internal const string CardGroup = "After the summary";

        internal const string InputSummaryId = "summary";
        internal const string InputBothId = "summary-and-transcript";
        internal const string InputSummaryDisplay = "The summary";
        internal const string InputBothDisplay = "The summary and the transcript";

        /// <summary>The owner's proposal: Codex unless the user picks Claude Code.</summary>
        internal const string DefaultCliId = CodingAgents.CodexId;

        /// <summary>How long one follow-up call may take: the summary's bound, for the same reasons.</summary>
        internal static readonly TimeSpan CallTimeout = SummaryRoute.CallTimeout;

        /// <summary>The CLI's whole system prompt for a follow-up, in place of the coding agent's own (the summary's lever).
        /// What the work is, is the user's instructions, on stdin.</summary>
        internal const string SystemPrompt =
            "You carry out a request about a meeting, working from its summary, and from its transcript when one is given. " +
            "Answer with the result itself, with nothing before or after it.";

        internal const string FileSuffix = " - follow-up.md";

        internal const string RunLabel = "Run the follow-up on the latest summary";

        internal static string[] CliDisplays()
        {
            return new[] { CodingAgents.ChoiceLabel(CodingAgentKind.Claude), CodingAgents.ChoiceLabel(CodingAgentKind.Codex) };
        }

        /// <summary>The stored id for an option's text, or null for text that is no option, which then stores nothing.</summary>
        internal static string CliFromDisplay(string display)
        {
            string value = (display ?? "").Trim();
            if (string.Equals(value, CodingAgents.ChoiceLabel(CodingAgentKind.Claude), StringComparison.Ordinal)) return CodingAgents.ClaudeId;
            if (string.Equals(value, CodingAgents.ChoiceLabel(CodingAgentKind.Codex), StringComparison.Ordinal)) return CodingAgents.CodexId;
            return null;
        }

        /// <summary>The CLI a follow-up runs on: the saved one, Codex when none is saved or the value is no CLI.</summary>
        internal static CodingAgentKind AgentOf(IModuleSettings settings)
        {
            CodingAgentKind agent = CodingAgents.FromId(settings == null ? null : settings.Get(CliKey, DefaultCliId));
            return agent == CodingAgentKind.None ? CodingAgents.FromId(DefaultCliId) : agent;
        }

        internal static string InputFromDisplay(string display)
        {
            string value = (display ?? "").Trim();
            if (string.Equals(value, InputSummaryDisplay, StringComparison.Ordinal)) return InputSummaryId;
            if (string.Equals(value, InputBothDisplay, StringComparison.Ordinal)) return InputBothId;
            return null;
        }

        /// <summary>Whether a follow-up is sent the transcript as well as the summary: only when the setting says so.</summary>
        internal static bool ReadsTranscript(IModuleSettings settings)
        {
            return settings != null && string.Equals(settings.Get(InputKey, InputSummaryId), InputBothId, StringComparison.Ordinal);
        }

        internal static string PromptFileOf(IModuleSettings settings)
        {
            return settings == null ? "" : (settings.Get(PromptFileKey, "") ?? "").Trim();
        }

        /// <summary>The follow-up's prompt, read from its file now: the text, or null with why in plain words.</summary>
        internal static string ReadPrompt(string path, out string problem)
        {
            string text = SummaryPrompt.ReadPromptFile(path, out problem);
            if (problem != null)
                problem = problem.Replace("no prompt file is chosen", "no follow-up prompt file is chosen")
                                 .Replace("the prompt file", "the follow-up prompt file");
            return text;
        }

        internal static bool IsOn(IModuleSettings settings)
        {
            return settings != null && settings.GetBool(OnKey, false);
        }

        /// <summary>The follow-up's file for a summary's: "&lt;name&gt; - follow-up.md" beside "&lt;name&gt;.summary.txt";
        /// null for a path that is no summary's.</summary>
        internal static string PathFor(string summaryPath)
        {
            string stem = StemOf(summaryPath);
            return stem == null ? null : stem + FileSuffix;
        }

        /// <summary>The transcript beside a summary: "&lt;name&gt;.transcript.txt"; null for a path that is no summary's.</summary>
        internal static string TranscriptPathFor(string summaryPath)
        {
            string stem = StemOf(summaryPath);
            return stem == null ? null : stem + ".transcript.txt";
        }

        private static string StemOf(string summaryPath)
        {
            const string Ending = ".summary.txt";
            if (string.IsNullOrEmpty(summaryPath) || !summaryPath.EndsWith(Ending, StringComparison.OrdinalIgnoreCase)) return null;
            return summaryPath.Substring(0, summaryPath.Length - Ending.Length);
        }

        /// <summary>A summary file's text without the header Remembrance wrote above it (the lines down to the rule of
        /// dashes), so the follow-up is sent what the summary said; the whole text when there is no such header.</summary>
        internal static string BodyOf(string summaryFileText)
        {
            string text = (summaryFileText ?? "").Replace("\r\n", "\n");
            string rule = "\n" + new string('-', 48) + "\n";
            int at = text.IndexOf(rule, StringComparison.Ordinal);
            if (at < 0 || at > 2000) return text.Trim();
            return text.Substring(at + rule.Length).Trim();
        }

        /// <summary>The follow-up's prompt, on stdin: the user's instructions, the meeting's name, then the summary, then
        /// the transcript when the setting sends it.</summary>
        internal static string BuildPrompt(string instructions, string meetingName, string summary, string transcript)
        {
            var sb = new StringBuilder();
            foreach (string line in (SummaryPrompt.Usable(instructions) ?? "").Replace("\r\n", "\n").Split('\n')) sb.AppendLine(line);
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(meetingName)) sb.Append("Meeting: ").AppendLine(meetingName.Trim());
            sb.AppendLine("SUMMARY:");
            sb.AppendLine(summary ?? "");
            if (transcript != null)
            {
                sb.AppendLine();
                sb.AppendLine("The transcript is machine-generated, so expect mishearings.");
                sb.AppendLine("TRANSCRIPT:");
                sb.AppendLine(transcript);
            }
            return sb.ToString();
        }

        /// <summary>The header above a follow-up: what it was made from, where that went, and the model that answered.</summary>
        internal static string FileHeader(string meetingName, CodingAgentKind agent, CliAnswer answer, bool sentTranscript)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.IsNullOrWhiteSpace(meetingName) ? "Recording" : meetingName.Trim());
            sb.AppendLine("Follow-up written: " + DateTime.Now.ToString("f"));
            sb.AppendLine("Model: " + CodingAgents.ChoiceLabel(agent) + ", " + SummaryRoute.AnsweredPhrase(answer) +
                          " (the summary" + (sentTranscript ? " and the transcript were" : " was") + " sent to " + CodingAgents.Vendor(agent) +
                          " with your follow-up prompt; the recording stayed on this machine)");
            sb.AppendLine(new string('-', 48));
            sb.AppendLine();
            return sb.ToString();
        }

        /// <summary>The card's "Goes through" row for <paramref name="agent"/>, one per CLI, each live while its CLI is chosen
        /// on screen (the CLI card's rule: the privacy row never names a vendor under a radio showing the other).</summary>
        internal static string SendsLine(CodingAgentKind agent, string choice)
        {
            return "Runs on " + choice + ", the model and effort the Coding-agent CLI card saves for " + CodingAgents.ProductName(agent) +
                   ". With the follow-up on, each summary's text goes to " + CodingAgents.Vendor(agent) + " once more after the summary is " +
                   "written, in one call with your follow-up prompt, and so does the latest summary when you press \"" + RunLabel +
                   "\". With \"The follow-up reads\" set to " + InputBothDisplay.ToLowerInvariant() +
                   ", the transcript goes with it. The answer is saved beside the summary as \"<its name>" + FileSuffix + "\"." +
                   (agent == CodingAgentKind.Claude ? " " + CodingAgentCliText.ClaudeOwnSetupLeftOut : "");
        }

        /// <summary>The card's "Follow-up prompt" row, for the SAVED file, read as the next follow-up would read it.</summary>
        internal static string PromptLine(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "None chosen, so no follow-up runs: write what the second CLI is to do with each summary in a .txt file, and " +
                       "choose it above.";
            string problem;
            string text = ReadPrompt(path, out problem);
            if (text == null) return "⚠ No follow-up runs for now: " + problem + ".";
            return "Your file " + Path.GetFileName(path) + ", " + SummaryPrompt.SizeOf(text) + ", read again for every follow-up, so an " +
                   "edit to it changes the next one with no Apply.";
        }

        /// <summary>A follow-up's failure for the Status line and the card: the summary is said to be untouched, because it is.</summary>
        internal static string Failed(string why)
        {
            return "follow-up failed: " + (why ?? "").Trim().TrimEnd('.') + "; the summary is unchanged";
        }
    }
}
