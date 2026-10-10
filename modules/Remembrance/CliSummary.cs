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
        /// <summary>EnabledWhen for the sign-in token row, which Claude Code alone reads, and since 2.3.0 for the Claude Code
        /// model and effort rows.</summary>
        internal const string OnClaudeCliOnly = SettingKey + "=Claude Code CLI";
        /// <summary>EnabledWhen for the Codex model and effort rows, which Codex alone reads (2.3.0).</summary>
        internal const string OnCodexCliOnly = SettingKey + "=Codex CLI";

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
        /// for the prompt overhead and the answer. Since 2.3.0 the window is the model the CLI card chooses (an alias, or
        /// Claude Code's default as before), still taken as 200k tokens: that figure was not re-measured per alias when
        /// the choice arrived, so a model with a smaller window would need this limit lowered with it. At the measured
        /// rate of about 54 KB of transcript an hour, the limit is
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
        /// the local header says nothing left the machine, and since 2.3.0 the model that ANSWERED and the effort it ran
        /// at (<see cref="AnsweredPhrase"/>), where 2.1.0 to 2.2.0 said "its default model" for every Claude Code
        /// summary.</summary>
        internal static string FileHeader(string meetingName, CodingAgentKind agent, CliAnswer answer)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.IsNullOrWhiteSpace(meetingName) ? "Recording" : meetingName.Trim());
            sb.AppendLine("Summary written: " + DateTime.Now.ToString("f"));
            sb.AppendLine("Model: " + CodingAgents.ChoiceLabel(agent) + ", " + AnsweredPhrase(answer) +
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

        // ---- the CLI's model and effort (lane feature/cli-model-effort, remembrance 2.3.0) ----------------------------------
        //
        // The owner, 2026-10-09: cheap calls on a small model, heavy ones on a large one. Until 2.3.0 a summary named no model
        // and no effort, so Claude Code ran on whatever the user's own setup resolved (on the owner's machine Opus at xhigh:
        // one one-word call cost $0.0407 there against $0.0011 with --model haiku, Claude Code's own list-price estimate,
        // measured that day). The summary is this pair of modules' one heavy call (a one-hour meeting is about 18k tokens), so
        // its defaults are its own, not AI Brain's. One DEFAULT per CLI per setting, each a single named constant below, and
        // ModelFor and EffortFor are the only readers, so there is no second copy to keep in step. The coordinator's live eval
        // of the same day confirmed all four (docs/DESIGN-REGISTER.md, "Defaults chosen by measurement"): on 4 synthetic
        // meetings every Claude arm recalled every planted item, and sonnet at medium could not be told apart from the best
        // arm (opus at medium) by an exact permutation test, at about half the cost of opus at low ($0.15 against $0.29 a
        // summary at list price); haiku at low, the cheapest, lost on polish rather than facts. Codex's Automatic at low (the
        // pick was gpt-6.1-sol) scored with the best Codex arm.
        //
        // A settings file written before 2.3.0 has none of the four keys, so every read answers the default: an existing
        // install moves to it, as the owner decided. An Apply writes all four (every field the pane shows is stored, as every
        // other field's always was; the Save comment in BuildOptionsPane says why an untouched Apply persists what Load
        // showed), so from then on the value is the install's, and a default changed in a later version reaches only a file
        // that never held the key, as in AI Brain 1.5.0.
        //
        // The values are what the shared runner takes (CodingAgentCli.CheckChoice): a Claude Code ALIAS (haiku, sonnet, opus;
        // never a full id, because the user's two organisations serve different catalogs and an alias resolves in whichever
        // serves the call) or "" for Claude Code's own default; a Codex slug from its own catalog or "" for the runner's
        // automatic pick; an effort of low, medium or high. No xhigh or max is offered: the runner passes on only those three
        // (CodingAgentCli.Efforts), one of the user's organisations serves no max at all, and a summary is one call under a
        // ten-minute bound (CallTimeout) whose spend is what this lane exists to cut. A value this version does not offer (a
        // hand edit, or a later version's alias after a downgrade) is KEPT, not replaced: the runner refuses it before
        // anything starts and the summary's failure or Validate names it (ChoiceRefused), and the dropdown shows it, where
        // clamping it to the default would change a model the user chose without a word.

        internal const string ClaudeModelKey = "cliClaudeModel";
        internal const string ClaudeEffortKey = "cliClaudeEffort";
        internal const string CodexModelKey = "cliCodexModel";
        internal const string CodexEffortKey = "cliCodexEffort";

        /// <summary>Remembrance's default Claude Code model for the summary: an alias, or "" for Claude Code's own default.</summary>
        internal const string DefaultClaudeModel = "sonnet";
        /// <summary>Remembrance's default Claude Code effort for the summary.</summary>
        internal const string DefaultClaudeEffort = "medium";
        /// <summary>Remembrance's default Codex model for the summary: a slug, or "" for the runner's automatic pick ("Automatic").</summary>
        internal const string DefaultCodexModel = "";
        /// <summary>Remembrance's default Codex effort for the summary.</summary>
        internal const string DefaultCodexEffort = "low";

        /// <summary>Longer than the runner's 64-character slug limit, so the cut can never turn a value the runner would refuse
        /// into one it passes on, and short enough that the pane's refusal, which quotes the value, stays one line.</summary>
        private const int MaximumChoiceLength = 96;

        /// <summary>The model the summary runs on for <paramref name="agent"/>, as the runner takes it ("" for none: Claude
        /// Code's default, Codex's automatic pick), read from <paramref name="settings"/>: the saved settings at a
        /// recording's stop, the on-screen copy (OnScreenSettings) for a press. "" for no CLI.</summary>
        internal static string ModelFor(DesktopAICompanion.Modules.IModuleSettings settings, CodingAgentKind agent)
        {
            if (agent == CodingAgentKind.Claude) return ReadChoice(settings, ClaudeModelKey, DefaultClaudeModel, false);
            if (agent == CodingAgentKind.Codex) return ReadChoice(settings, CodexModelKey, DefaultCodexModel, false);
            return "";
        }

        /// <summary>The effort the summary runs at for <paramref name="agent"/> (never "" for a CLI); "" for no CLI.</summary>
        internal static string EffortFor(DesktopAICompanion.Modules.IModuleSettings settings, CodingAgentKind agent)
        {
            if (agent == CodingAgentKind.Claude) return ReadChoice(settings, ClaudeEffortKey, DefaultClaudeEffort, true);
            if (agent == CodingAgentKind.Codex) return ReadChoice(settings, CodexEffortKey, DefaultCodexEffort, true);
            return "";
        }

        /// <param name="emptyIsDefault">True for an effort, which has no "none" to choose (the runner would run it at its own
        /// floor, which the card would not name), so a blank one is the default; false for a model, whose blank is a choice:
        /// Claude Code's default, or Codex's automatic pick.</param>
        private static string ReadChoice(DesktopAICompanion.Modules.IModuleSettings settings, string key, string fallback, bool emptyIsDefault)
        {
            // A missing key is the default (an install from before 2.3.0); case and spacing are read through, as the radio's
            // id is; a value this version does not offer is kept (see above).
            string stored = settings == null ? null : settings.Get(key, null);
            if (stored == null) return fallback;
            string value = stored.Trim();
            if (value.Length > MaximumChoiceLength) value = value.Substring(0, MaximumChoiceLength);
            value = value.ToLowerInvariant();
            if (emptyIsDefault && value.Length == 0) value = fallback;
            return value;
        }

        // The rows show labels and the settings store what the runner takes, mapped here the way the radio's options map to
        // their ids (FromDisplay). Text that is no option maps to NULL, which stores nothing: the "" a closed Enum hands back
        // for a value it could not show would otherwise read as Claude Code's default, which on the owner's machine is Opus at
        // xhigh, the spend this lane exists to cut. The labels are AI Brain 1.5.0's, so one user meets one vocabulary.

        private static readonly string[][] ClaudeModelChoices =
        {
            new[] { "Haiku (fastest, lightest on usage)", "haiku" },
            new[] { "Sonnet", "sonnet" },
            new[] { "Opus (most capable, heaviest on usage)", "opus" },
            new[] { "Claude Code's default", "" },
        };

        private static readonly string[][] EffortChoices =
        {
            new[] { "Low", "low" },
            new[] { "Medium", "medium" },
            new[] { "High", "high" },
        };

        /// <summary>The label Codex's automatic pick goes by in the Codex model dropdown.</summary>
        internal const string CodexAutomaticLabel = "Automatic";

        /// <summary>The fixed options of a two-column choice list, with the saved value unioned in when it is none of them
        /// (shown as itself, so a value this version does not offer is visible and survives an Apply).</summary>
        private static string[] ChoiceOptions(string[][] choices, string saved)
        {
            var options = new System.Collections.Generic.List<string>(choices.Length + 1);
            bool offered = false;
            foreach (string[] choice in choices)
            {
                options.Add(choice[0]);
                if (string.Equals(choice[1], saved ?? "", StringComparison.Ordinal)) offered = true;
            }
            if (!offered && !string.IsNullOrEmpty(saved)) options.Insert(0, saved);
            return options.ToArray();
        }

        private static string ChoiceLabel(string[][] choices, string value)
        {
            foreach (string[] choice in choices)
                if (string.Equals(choice[1], value ?? "", StringComparison.Ordinal)) return choice[0];
            return value ?? "";
        }

        /// <summary>The value for an option's text, or null for text that is no option, which stores nothing. A saved value
        /// the list does not offer is shown as itself (ChoiceOptions) and comes back as null, so it stays saved.</summary>
        private static string ChoiceValue(string[][] choices, string label)
        {
            string text = (label ?? "").Trim();
            foreach (string[] choice in choices)
                if (string.Equals(choice[0], text, StringComparison.Ordinal)) return choice[1];
            return null;
        }

        internal static string[] ClaudeModelOptions(string saved) { return ChoiceOptions(ClaudeModelChoices, saved); }
        internal static string ClaudeModelLabel(string alias) { return ChoiceLabel(ClaudeModelChoices, alias); }
        internal static string ClaudeModelForLabel(string label) { return ChoiceValue(ClaudeModelChoices, label); }
        internal static string[] EffortOptions(string saved) { return ChoiceOptions(EffortChoices, saved); }
        internal static string EffortLabel(string effort) { return ChoiceLabel(EffortChoices, effort); }
        internal static string EffortForLabel(string label) { return ChoiceValue(EffortChoices, label); }

        /// <summary>The aliases and efforts the rows offer, for the self-test's check that they are the runner's.</summary>
        internal static System.Collections.Generic.IEnumerable<string> ClaudeModelValuesForDiagnostics()
        {
            foreach (string[] choice in ClaudeModelChoices) yield return choice[1];
        }

        internal static System.Collections.Generic.IEnumerable<string> EffortValuesForDiagnostics()
        {
            foreach (string[] choice in EffortChoices) yield return choice[1];
        }

        /// <summary>
        /// What a CLI summary ran on, for its file's header: "claude-sonnet-5-5 at medium effort", "claude-sonnet-5-5 (asked
        /// for haiku) at medium effort" when Claude Code answered on another family than the alias asked for (a fact, not a
        /// failure), with Claude Code's own fallback when it reported one; "text-only-low at low effort" for Codex, whose model
        /// is the slug it was sent; "a model it did not name (asked for sonnet) at medium effort" when an alias was asked for
        /// and the stream named no model (never the alias as if it had answered, review findings F5 and F12); "its default
        /// model at low effort" only when nothing was asked for and nothing named. The runner's RanOn without its leading
        /// "on ", so the header and the runner's sentences cannot word one answer two ways.
        /// </summary>
        internal static string AnsweredPhrase(CliAnswer answer)
        {
            string ran = CodingAgentCliText.RanOn(answer);
            if (ran.StartsWith("on ", StringComparison.Ordinal)) return ran.Substring(3);
            return "its default model" + (ran.Length > 0 ? " " + ran : "");
        }
    }
}
