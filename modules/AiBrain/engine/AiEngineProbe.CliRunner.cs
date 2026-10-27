using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using DesktopAICompanion.Ai;
using DesktopAICompanion.CodingAgent;
using DesktopAICompanion.ModuleKit.Testing;   // RecordingHost (through CliRig), FakeCompanion
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AiBrainModule
{
    /// <summary>
    /// Lane feature/cli-runner-aibrain: AI Brain's own items of the 2026-10-26 campaign. The opt-in that keeps the user's own
    /// Claude Code settings files out of this module's calls (BACKLOG N-cli-home-files-01), the "When OCR is used" row's
    /// words per engine (N-cli-model-effort-01), the pane told to reload its Info rows after a remark, an audition and a
    /// Validate (N-cli-model-effort-07, host 1.6.1), no cloud provider stored that nobody chose (N-cli-model-effort-11), and
    /// the stronger no-names rule a CLI turn's persona carries (N-cli-model-effort-03, part 1 of 2). The per-CLI rows
    /// (N-cli-model-effort-15) are held where the card's rows always were (AiEngineProbe.Cli.cs, CliChoice.cs) and the
    /// recovery's delete failure (N-remembrance-delete-logging-05) in AiEngineProbe.Security.cs. No CLI is started, no
    /// model is called: every call goes to the rig's fake CLI.
    /// </summary>
    public static partial class AiEngineProbe
    {
        private static bool CheckCliRunnerLane(StringBuilder sb)
        {
            bool ok = true;
            ok &= CheckLaneOwnSettingsOptIn(sb);
            ok &= CheckLaneOcrRow(sb);
            ok &= CheckLanePaneNotified(sb);
            ok &= CheckLaneNoUnchosenCloudProvider(sb);
            ok &= CheckLaneCliNamesRule(sb);
            return ok;
        }

        /// <summary>The model call a fake saw last carries --setting-sources with an empty list, once.</summary>
        private static bool CarriesNoSettingSources(FakeCliCall call)
        {
            return call != null && !call.Arguments.Exists(delegate(string a) { return a.StartsWith("--setting-sources", StringComparison.Ordinal); });
        }

        private static bool CarriesEmptySettingSources(FakeCliCall call)
        {
            return call != null &&
                   call.Arguments.FindAll(delegate(string a) { return a.StartsWith("--setting-sources", StringComparison.Ordinal); }).Count == 1 &&
                   call.After("--setting-sources") == "";
        }

        /// <summary>N-cli-home-files-01: the row, its default, its Apply, what a remark and a Validate send, and the privacy row.</summary>
        private static bool CheckLaneOwnSettingsOptIn(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new CliRig("aibrain-cli-runner-optin", ""))
            {
                OptionsPane pane = rig.Pane;
                rig.Fake.Respond = FakeCliProcess.Answering(CliReply);
                SettingField row = FieldOf(pane, "cliClaudeSettingsOut");
                ok &= Check(sb, "aibrain cli-runner: the opt-in row is a switch in the CLI card, live only while Claude Code is the CLI on screen: " +
                                (row == null ? "(no row)" : row.Label),
                    row != null && row.Kind == SettingKind.Bool && row.Group == AiBrainModule.CliCardGroup &&
                    row.Label == "Keep my Claude Code settings out of companion calls" &&
                    row.EnabledWhen == AiBrainModule.OnClaudeCliOnly && IsStrictlyNarrower(row.EnabledWhen, CardGateOf(pane, "cliClaudeSettingsOut")));
                ok &= Check(sb, "aibrain cli-runner: the opt-in is off by default, as the settings and the pane say",
                    !rig.Settings.CliLeaveOutClaudeSettings && pane.Load()["cliClaudeSettingsOut"] == "false" && !new AiSettings().CliLeaveOutClaudeSettings);

                bool applied = rig.Save("brainRunsOn", "Claude Code CLI", "cliClaudeSettingsOut", "true", "useVision", "false");
                ok &= Check(sb, "aibrain cli-runner: an Apply of the switch on saves it, and the pane loads it so",
                    applied && rig.Settings.CliLeaveOutClaudeSettings && AiSettings.Load().CliLeaveOutClaudeSettings &&
                    pane.Load()["cliClaudeSettingsOut"] == "true");
                rig.Fake.Clear();
                BrainResponse onRemark;
                using (AiBrain brain = AiBrainModule.CreateBrain(rig.Settings, null, null, rig.Runner))
                    onRemark = RemarkOrNull(brain, "claude-code-cli");
                FakeCliCall onCall = LastModelCall(rig.Fake);
                ok &= Check(sb, "aibrain cli-runner: a remark with the switch saved on carries --setting-sources with an empty list",
                    onRemark != null && CarriesEmptySettingSources(onCall));
                string sends = pane.Load()["cliSendsClaude"];
                ok &= Check(sb, "aibrain cli-runner: with it on, Goes through Claude Code says the settings files are not read and what that drops: " + sends,
                    sends.EndsWith(" " + CodingAgentCliText.ClaudeOwnSetupLeftOut + " " + CodingAgentCliText.ClaudeOwnSettingsFilesLeftOut, StringComparison.Ordinal) &&
                    CodingAgentCliText.ClaudeOwnSettingsFilesLeftOut.Contains("sign-in helper") && CodingAgentCliText.ClaudeOwnSettingsFilesLeftOut.Contains("proxy"));

                PaneAction validate = FindAction(pane, AiBrainModule.CliCardGroup, "Validate");
                rig.Fake.Clear();
                string offOnScreen = PressWith(validate, "brainRunsOn", "Claude Code CLI", "cliClaudeSettingsOut", "false");
                FakeCliCall offCall = LastModelCall(rig.Fake);
                rig.Save("cliClaudeSettingsOut", "false");
                rig.Fake.Clear();
                string onOnScreen = PressWith(validate, "brainRunsOn", "Claude Code CLI", "cliClaudeSettingsOut", "true");
                FakeCliCall onScreenCall = LastModelCall(rig.Fake);
                ok &= Check(sb, "aibrain cli-runner: Validate tests the switch as the card shows it, applied or not",
                    offOnScreen != null && offOnScreen.StartsWith("✓ Claude Code", StringComparison.Ordinal) && CarriesNoSettingSources(offCall) &&
                    onOnScreen != null && onOnScreen.StartsWith("✓ Claude Code", StringComparison.Ordinal) && CarriesEmptySettingSources(onScreenCall) &&
                    !rig.Settings.CliLeaveOutClaudeSettings);

                rig.Fake.Clear();
                using (AiBrain brain = AiBrainModule.CreateBrain(rig.Settings, null, null, rig.Runner))
                    RemarkOrNull(brain, "claude-code-cli");
                FakeCliCall offRemark = LastModelCall(rig.Fake);
                ok &= Check(sb, "WITNESS aibrain cli-runner: with the switch off a remark carries no --setting-sources, and Goes through Claude Code says nothing of it",
                    CarriesNoSettingSources(offRemark) &&
                    !pane.Load()["cliSendsClaude"].Contains(CodingAgentCliText.ClaudeOwnSettingsFilesLeftOut));
                rig.Save("brainRunsOn", "Codex CLI", "cliClaudeSettingsOut", "true");
                rig.Fake.Clear();
                using (AiBrain brain = AiBrainModule.CreateBrain(rig.Settings, null, null, rig.Runner))
                    RemarkOrNull(brain, "codex-cli");
                FakeCliCall codexCall = LastModelCall(rig.Fake);
                ok &= Check(sb, "aibrain cli-runner: a Codex remark never carries --setting-sources, the switch saved on",
                    codexCall != null && codexCall.IsCodex && CarriesNoSettingSources(codexCall) &&
                    !pane.Load()["cliSendsCodex"].Contains(CodingAgentCliText.ClaudeOwnSettingsFilesLeftOut));
            }
            return ok;
        }

        /// <summary>N-cli-model-effort-01: the row says what each engine does, and no longer that any remark whose model
        /// cannot see images is read as text, which on the local and cloud slots none is.</summary>
        private static bool CheckLaneOcrRow(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new CliRig("aibrain-cli-runner-ocr", ""))
            {
                string shown = rig.Pane.Load()["ocrWhen"];
                ok &= Check(sb, "aibrain cli-runner: When OCR is used says what each engine does with a model that cannot see images: " + shown,
                    shown == AiBrainModule.OcrWhenLine &&
                    shown.Contains("with Use vision on, the poke reaction and, on Codex CLI, a remark whose chosen model takes no images") &&
                    shown.Contains("the local model and a cloud provider never read the screen as text for a model that cannot see images") &&
                    shown.Contains("the local model switches to one that can (or says it found none)") &&
                    shown.Contains("a cloud provider says the model can't see images") &&
                    !shown.Contains("any remark whose model cannot see images"));
            }
            return ok;
        }

        /// <summary>N-cli-model-effort-07 (AI Brain's half): the pane is told to reload its Info rows after a remark, an
        /// audition and a Validate, through OptionsPane.InfoChanged, which RecordingHost hands back as the pane the module
        /// added; a Load alone tells it nothing (the WITNESS that the count is the three acts', not the reading's).</summary>
        private static bool CheckLanePaneNotified(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new CliRig("aibrain-cli-runner-notify", ", \"CliBackend\": \"claude\""))
            {
                OptionsPane pane = rig.Pane;
                rig.Fake.Respond = FakeCliProcess.Answering(CliReply);
                // The saved CLI's details read first, awaited, which tells nobody (lane feature/cli-followups): with none
                // kept, the Load below would start one behind it, and that read tells the open pane when it lands
                // (CodingAgentCli.DetailsRead, AiEngineProbe.CliFollowups.cs holds it), a fourth notification racing the three
                // this group counts.
                rig.Runner.RefreshDetailsAsync(CodingAgentKind.Claude, CancellationToken.None).Wait(TimeSpan.FromSeconds(10));
                int notified = 0;
                Action counter = delegate { Interlocked.Increment(ref notified); };
                pane.InfoChanged += counter;
                try
                {
                    pane.Load();
                    int afterLoad = Volatile.Read(ref notified);
                    rig.Module.RecordRemark(DateTime.Now, 1200, null, "claude-selftest-remark-1");
                    int afterRemark = Volatile.Read(ref notified);
                    AuditionSaved(rig);
                    int afterAudition = Volatile.Read(ref notified);
                    PressWith(FindAction(pane, AiBrainModule.CliCardGroup, "Validate"), "brainRunsOn", "Claude Code CLI");
                    int afterValidate = Volatile.Read(ref notified);
                    ok &= Check(sb, "WITNESS aibrain cli-runner: a Load alone tells the pane nothing (" + afterLoad + ")", afterLoad == 0);
                    ok &= Check(sb, "aibrain cli-runner: a remark tells the open pane to reload its Info rows (" + afterRemark + ")",
                        afterRemark == afterLoad + 1);
                    ok &= Check(sb, "aibrain cli-runner: an audition tells the open pane to reload its Info rows (" + (afterAudition - afterRemark) + ")",
                        afterAudition > afterRemark);
                    ok &= Check(sb, "aibrain cli-runner: a Validate tells the open pane to reload its Info rows (" + (afterValidate - afterAudition) + ")",
                        afterValidate > afterAudition);
                }
                finally { pane.InfoChanged -= counter; }
            }
            return ok;
        }

        /// <summary>N-cli-model-effort-11 (AI Brain's half): a Local model Apply with the dropdown as Load showed it stores no
        /// cloud provider, where it stored "openai" for an install that never chose one; a provider picked in the dropdown
        /// is still remembered (the WITNESS).</summary>
        private static bool CheckLaneNoUnchosenCloudProvider(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new CliRig("aibrain-cli-runner-provider", ""))
            {
                OptionsPane pane = rig.Pane;
                string shown = pane.Load()["cloudProvider"];
                bool saved = rig.Save("brainRunsOn", "Local model", "cloudProvider", shown);
                ok &= Check(sb, "aibrain cli-runner: a Local model Apply with the cloud dropdown as Load showed it stores no cloud provider nobody chose: \"" +
                                rig.Settings.LastCloudProvider + "\"",
                    shown == "openai" && saved && rig.Settings.LastCloudProvider == "" && AiSettings.Load().LastCloudProvider == "" &&
                    pane.Load()["cloudProvider"] == "openai");
                rig.Save("brainRunsOn", "Local model", "cloudProvider", "openrouter");
                ok &= Check(sb, "WITNESS aibrain cli-runner: ...and a provider picked in the dropdown while Local model is chosen is remembered",
                    rig.Settings.LastCloudProvider == "openrouter" && pane.Load()["cloudProvider"] == "openrouter");
            }
            return ok;
        }

        /// <summary>N-cli-model-effort-03, part 1 of 2: a CLI turn's persona carries the stronger no-names rule, in words
        /// typed here, with the user's own name the one allowed when they gave one; the local and cloud slots' prompt does not
        /// (the WITNESS). Whether the rule works is the eval's to say (another lane, approved by the owner).</summary>
        private static bool CheckLaneCliNamesRule(StringBuilder sb)
        {
            bool ok = true;
            const string rule = " You run inside a coding assistant that may know who is signed in to it: never use that person's name, " +
                                "username, email address or organisation, or any name from your own context. Never repeat a person's name, " +
                                "username, handle, email address, phone number or other private detail you see on the screen either; " +
                                "remark on the programs, files and things on the screen, not on the people.";
            using (var claudeBrain = new AiBrain(new RecordingBackend("", true), new AiSettings { CliBackend = "claude" }))
            using (var codexBrain = new AiBrain(new RecordingBackend("", true), new AiSettings { CliBackend = "codex", UserName = "Quill" }))
            using (var localBrain = new AiBrain(new RecordingBackend("", true), new AiSettings()))
            {
                string claude = claudeBrain.BuildSystemPrompt();
                string codex = codexBrain.BuildSystemPrompt();
                string local = localBrain.BuildSystemPrompt();
                ok &= Check(sb, "aibrain cli-runner: a CLI turn's persona forbids a name from the assistant's own context and a person's details off the screen",
                    claude.Contains(rule) && claude.IndexOf(" You do not know your human's name", StringComparison.Ordinal) <
                                              claude.IndexOf(rule, StringComparison.Ordinal));
                ok &= Check(sb, "aibrain cli-runner: ...with the name the user gave the one allowed, on either CLI",
                    codex.Contains(" You run inside a coding assistant that may know who is signed in to it:") &&
                    codex.Contains("you see on the screen either (the one name you may use is Quill); remark on the programs"));
                ok &= Check(sb, "WITNESS aibrain cli-runner: the local and cloud slots' persona carries no such rule",
                    !local.Contains("You run inside a coding assistant") && local.Contains("You do not know your human's name"));
            }
            return ok;
        }
    }
}
