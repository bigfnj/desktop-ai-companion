using System;
using System.Text;
using DesktopAICompanion.CodingAgent;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// Where the summary runs (remembrance 2.1.0, lane feature/cli-backend): the local Ollama map-reduce it always used,
    /// or ONE call through a coding-agent CLI, Claude Code or Codex, which the user opts into under "Summary runs on".
    /// Since 2.2.0 also ONE call to a cloud provider (CloudSummary.cs), the fourth of AI Brain's engines.
    ///
    /// This reverses the shipped "local-only, no cloud summary path, ever" (docs/IDEAS.md:77): the owner ruled on
    /// 2026-10-06 that the choice belongs to the end user. So it is off by default (an install that never chose reads
    /// "local"), it is this module's own setting and works with AI Brain absent, and the card says plainly what leaves the
    /// machine. Transcription is untouched: it stays local Whisper, because neither CLI accepts audio and the recording is
    /// the most sensitive thing this module holds.
    ///
    /// Why one call: measured 2026-10-06 (the lane's brief), both CLIs got every planted decision, owner, open question and
    /// trap right on synthetic one- and three-hour meetings with the single-shot prompt on stdin, a decision reversed 2.5
    /// hours later included. The map-reduce exists because a local model's context is small; a CLI's is not, and one call
    /// sees the whole meeting at once.
    /// </summary>
    internal static class SummaryRoute
    {
        internal const string SettingKey = "summaryRunsOn";
        internal const string LocalId = "local";
        /// <summary>A cloud provider (2.2.0): CloudSummary.cs.</summary>
        internal const string CloudId = "cloud";
        // The options' text is AI Brain's "Brain runs on" since 2.2.0 (the owner, 2026-10-07: "the 'runs on' Local Model,
        // cloud provider, claude cli, codex cli box"). The local engine was "Local Ollama", R2's word; the stored id stays
        // "local", so no settings file changes, and the card under it still says Ollama.
        internal const string LocalDisplay = "Local model";
        internal const string CloudDisplay = "Cloud provider";
        internal const string CardGroup = "Coding-agent CLI";
        internal const string CloudCardGroup = "Cloud provider";

        /// <summary>EnabledWhen for the Ollama settings, which grey unless the local model is chosen, and for the CLI card's
        /// rows, which grey unless a CLI is. The host compares the option TEXT on screen.</summary>
        internal const string OnLocalOnly = SettingKey + "=" + LocalDisplay;
        internal const string OnCloudOnly = SettingKey + "=" + CloudDisplay;
        internal const string OnCliOnly = SettingKey + "=Claude Code CLI|Codex CLI";
        /// <summary>EnabledWhen for the sign-in token row, which Claude Code alone reads.</summary>
        internal const string OnClaudeCliOnly = SettingKey + "=Claude Code CLI";

        /// <summary>The four engines, in AI Brain's order.</summary>
        internal static string[] Displays()
        {
            return new[]
            {
                LocalDisplay,
                CloudDisplay,
                CodingAgents.ChoiceLabel(CodingAgentKind.Claude),
                CodingAgents.ChoiceLabel(CodingAgentKind.Codex),
            };
        }

        /// <summary>The stored id for an option's text, or null for text that is no option, which then stores nothing
        /// (the folder layout's rule, 2.0.0).</summary>
        internal static string FromDisplay(string display)
        {
            string value = (display ?? "").Trim();
            if (string.Equals(value, LocalDisplay, StringComparison.Ordinal)) return LocalId;
            if (string.Equals(value, CloudDisplay, StringComparison.Ordinal)) return CloudId;
            if (string.Equals(value, CodingAgents.ChoiceLabel(CodingAgentKind.Claude), StringComparison.Ordinal)) return CodingAgents.ClaudeId;
            if (string.Equals(value, CodingAgents.ChoiceLabel(CodingAgentKind.Codex), StringComparison.Ordinal)) return CodingAgents.CodexId;
            return null;
        }

        /// <summary>The option for a stored id. Absent, "local" and anything unknown are the local path: the conservative
        /// reading, since the other three send the transcript off the machine.</summary>
        internal static string ToDisplay(string stored)
        {
            if (IsCloud(stored)) return CloudDisplay;
            CodingAgentKind agent = AgentOf(stored);
            return agent == CodingAgentKind.None ? LocalDisplay : CodingAgents.ChoiceLabel(agent);
        }

        internal static CodingAgentKind AgentOf(string stored)
        {
            return CodingAgents.FromId(stored);
        }

        internal static bool IsCloud(string stored)
        {
            return string.Equals((stored ?? "").Trim(), CloudId, StringComparison.Ordinal);
        }

        /// <summary>True for the local engine: neither a cloud provider nor a CLI.</summary>
        internal static bool IsLocal(string stored)
        {
            return !IsCloud(stored) && AgentOf(stored) == CodingAgentKind.None;
        }

        /// <summary>
        /// The one-call limit, in UTF-8 bytes of transcript: over it, the local map-reduce summarizes instead. 360 KB, chosen
        /// from the brief's measurements rather than guessed: the three-hour synthetic meeting was 162,039 bytes and cost
        /// Claude Code 60.3k input tokens with about 6k of them prompt overhead, so about 2.95 bytes a token (Codex counted
        /// 45.6k, about 4 bytes a token). The binding window is Claude Code's default model's, taken as 200k tokens; at a
        /// conservative 2.5 bytes a token (denser text, a non-Latin script) 360 KB is about 144k tokens, which leaves room
        /// for the prompt overhead and the answer. At the measured rate of about 54 KB of transcript an hour, the limit is
        /// over six hours of talk, and whisper's own limit on one recording is six (Transcriber.MaximumWhisperTimeout), so
        /// a recording practically always takes the one call, and the fallback is for a long transcript summarized by hand.
        /// Bytes rather than characters because a tokenizer works on bytes: a character count would let a CJK transcript
        /// three times as heavy through.
        /// </summary>
        internal const int MaximumTranscriptBytes = 360 * 1000;

        internal static long Utf8Bytes(string text)
        {
            return Encoding.UTF8.GetByteCount(text ?? "");
        }

        internal static bool FitsOneCall(long transcriptUtf8Bytes)
        {
            return transcriptUtf8Bytes <= MaximumTranscriptBytes;
        }

        /// <summary>The CLI's whole system prompt, in place of the coding agent's own: the brief's measured lever (Claude
        /// Code's input fell from 18k to 6k tokens with a short one). The instructions themselves are the single-shot
        /// prompt the local path sends, on stdin.</summary>
        internal const string SystemPrompt =
            "You write summaries of meeting transcripts. Answer with the summary itself, in plain text, with nothing before or after it.";

        /// <summary>How long one summary call may take. The brief's measurements were 7 to 19 seconds for one and three
        /// hours; ten minutes is the bound for a slow day, not an estimate.</summary>
        internal static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(10);

        /// <summary>The header above a summary the CLI wrote: where the transcript went, said in the file itself, the way
        /// the local header says nothing left the machine.</summary>
        internal static string FileHeader(string meetingName, CodingAgentKind agent, string model)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.IsNullOrWhiteSpace(meetingName) ? "Recording" : meetingName.Trim());
            sb.AppendLine("Summary written: " + DateTime.Now.ToString("f"));
            sb.AppendLine("Model: " + CodingAgents.ChoiceLabel(agent) + ", " +
                          (string.IsNullOrWhiteSpace(model) ? "its default model" : model.Trim()) +
                          " (the transcript was sent to " + CodingAgents.Vendor(agent) +
                          " to be summarized; the recording and its transcription stayed on this machine)");
            sb.AppendLine(new string('-', 48));
            sb.AppendLine();
            return sb.ToString();
        }

        /// <summary>The plain refusal an Ollama-only button gives while a CLI is chosen, since a PaneAction has no
        /// EnabledWhen to grey it with its card.</summary>
        internal static string NotUsedOnCli(CodingAgentKind agent)
        {
            return "✗ Not used while Summary runs on " + CodingAgents.ChoiceLabel(agent) + ".";
        }

        internal const string PickACliFirst = "⚠ Pick Claude Code CLI or Codex CLI under \"Summary runs on\" first.";
        internal const string PickCloudFirst = "⚠ Pick Cloud provider under \"Summary runs on\" first.";
        internal const string NotUsedOnCloud = "✗ Not used while Summary runs on Cloud provider.";

        /// <summary>The header above a summary a cloud provider wrote: the provider, the model and the host the transcript
        /// went to, in the file itself.</summary>
        internal static string CloudFileHeader(string meetingName, string provider, string model, string host)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.IsNullOrWhiteSpace(meetingName) ? "Recording" : meetingName.Trim());
            sb.AppendLine("Summary written: " + DateTime.Now.ToString("f"));
            sb.AppendLine("Model: " + CloudDisplay + " (" + (provider ?? "").Trim() + "), " + (model ?? "").Trim() +
                          " (the transcript was sent to " + (host ?? "").Trim() +
                          " to be summarized; the recording and its transcription stayed on this machine)");
            sb.AppendLine(new string('-', 48));
            sb.AppendLine();
            return sb.ToString();
        }

        /// <summary>The cloud card's "Goes through it" row.</summary>
        internal static string CloudSendsLine(string host)
        {
            string to = string.IsNullOrWhiteSpace(host) ? "the provider set in this card" : host.Trim();
            return "The transcript's text goes to " + to + " once per recording, in one call with the summary prompt, and so does a " +
                   "transcript you summarize with \"Summarize a transcript…\". The audio never leaves this machine: transcription " +
                   "stays on local Whisper. A transcript over 360 KB (more than six hours of talk) is summarized by the local model instead.";
        }

        /// <summary>The card's "Goes through it" row: what leaves the machine on this path, in one plain account.</summary>
        internal static string SendsLine(CodingAgentKind agent)
        {
            string to = agent == CodingAgentKind.None ? "Anthropic (Claude Code) or OpenAI (Codex)" : CodingAgents.Vendor(agent);
            return "The transcript's text goes to " + to + " once per recording, in one call with the summary prompt, and so does a " +
                   "transcript you summarize with \"Summarize a transcript…\". The audio never leaves this machine: transcription " +
                   "stays on local Whisper. A transcript over 360 KB (more than six hours of talk) is summarized by the local model instead.";
        }
    }
}
