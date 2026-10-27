using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.CodingAgent;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// Lane feature/cli-followups (remembrance 2.7.0): the CLI card's "Keep my Claude Code settings out of companion calls"
    /// switch, as lane feature/cli-runner-aibrain's hand-off specified it (the row, its default, its Apply, Validate on the
    /// value on screen, the Goes through Claude Code row), and the open pane told when the saved CLI's details read lands
    /// (BACKLOG N-remembrance-backlog-01, Remembrance's wiring). The summary's two paths on the switch (a recording's on
    /// the saved value, Summarize a transcript on the screen's) are held in SelfCheckCliSummary, beside the model and
    /// effort they read with it. Every CLI call goes to the shared runner's fake: no process is started and no model is
    /// called. A command line is read for the switch's flag through FakeCliCall, so no code here names the flag
    /// (tests/runtime-hardening-selftest.ps1 counts it in the modules, N-cli-runner-03).
    /// </summary>
    public sealed partial class RemembranceModule
    {
        private static void SelfCheckCliFollowups(Action<string, bool> check)
        {
            Func<string, CancellationToken, Task<IReadOnlyList<string>>> savedLister = ListModels;
            SynchronizationContext previous = SynchronizationContext.Current;
            var ui = new RecorderSelfCheck.QueueSynchronizationContext();
            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-clifollowups-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                SynchronizationContext.SetSynchronizationContext(ui);
                ListModels = delegate { return Task.FromResult((IReadOnlyList<string>)new List<string>()); };
                System.IO.Directory.CreateDirectory(scratch);
                using (var cliScratch = new FakeCliScratch())
                using (var devices = new RecorderSelfCheck.FakeDevices())
                {
                    var fake = new FakeCliProcess { Respond = FakeCliProcess.Answering("OK") };
                    CodingAgentCli runner = cliScratch.NewRunner(fake, new List<string>());
                    var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings s = host.SettingsFor(Id);
                    s.Set("storageLocation", System.IO.Path.Combine(scratch, "store"));
                    s.Set("whisperExe", @"c:\seeded\whisper-cli.exe");
                    s.Set("summaryModelsCache", "alpha:1b");
                    s.Set("summaryOn", "true");
                    s.Set(SummaryRoute.SettingKey, CodingAgents.ClaudeId);
                    var module = new RemembranceModule();
                    module.CliForDiagnostics = runner;
                    module.Init(host);
                    try
                    {
                        OptionsPane pane = host.OptionsPanes[0];

                        // ---- N-remembrance-backlog-01: the saved CLI's details read tells the open pane when it lands ----
                        int notified = 0;
                        Action counter = delegate { Interlocked.Increment(ref notified); };
                        pane.InfoChanged += counter;
                        string before, after;
                        bool landed;
                        int afterLanding, afterReload;
                        try
                        {
                            before = pane.Load()["cliNameClaude"];
                            landed = SpinWait.SpinUntil(delegate { ui.Drain(); return Volatile.Read(ref notified) >= 1; }, TimeSpan.FromSeconds(10));
                            Thread.Sleep(50);
                            afterLanding = Volatile.Read(ref notified);
                            after = pane.Load()["cliNameClaude"];
                            Thread.Sleep(100);
                            ui.Drain();
                            afterReload = Volatile.Read(ref notified);
                        }
                        finally { pane.InfoChanged -= counter; }
                        check("remembrance cli-followups: the saved CLI's details read tells the open pane when it lands, and the row then names the CLI: " +
                              before + " -> " + after,
                            before == "Claude Code: checking…" && landed && afterLanding == 1 && after.StartsWith("Claude Code 2.1.292, ", StringComparison.Ordinal));
                        check("WITNESS remembrance cli-followups: ...and the Load that follows finds the reading fresh and tells the pane nothing more (" +
                              afterReload + ")",
                            afterReload == afterLanding && !runner.DetailsReadInFlightForDiagnostics(CodingAgentKind.Claude));

                        // ---- the switch: the row, its default, Apply ----
                        SettingField row = FieldFor(pane, SummaryRoute.ClaudeSettingsOutKey);
                        int effortAt = IndexOfField(pane, SummaryRoute.ClaudeEffortKey);
                        check("remembrance cli-followups: the CLI card has the switch right after the Claude Code effort row, live only while Claude Code is the CLI on screen: " +
                              (row == null ? "(no row)" : row.Label),
                            row != null && row.Id == "cliClaudeSettingsOut" && row.Kind == SettingKind.Bool && row.Group == SummaryRoute.CardGroup &&
                            row.Label == "Keep my Claude Code settings out of companion calls" &&
                            row.Label == CodingAgentCliText.LeaveOutOwnClaudeSettingsLabel && row.EnabledWhen == "summaryRunsOn=Claude Code CLI" &&
                            effortAt >= 0 && IndexOfField(pane, SummaryRoute.ClaudeSettingsOutKey) == effortAt + 1);
                        IReadOnlyDictionary<string, string> fresh = pane.Load();
                        check("remembrance cli-followups: the switch is off by default, and Goes through Claude Code then says nothing of it",
                            fresh[SummaryRoute.ClaudeSettingsOutKey] == "false" && s.Get(SummaryRoute.ClaudeSettingsOutKey, null) == null &&
                            !fresh["cliSendsClaude"].Contains(CodingAgentCliText.ClaudeOwnSettingsFilesLeftOut));
                        Dictionary<string, string> on = CopyOf(fresh);
                        on[SummaryRoute.ClaudeSettingsOutKey] = "true";
                        bool applied = pane.Save(on);
                        IReadOnlyDictionary<string, string> shownOn = pane.Load();
                        check("remembrance cli-followups: an Apply of the switch on saves it as a bool, and the pane loads it so",
                            applied && s.Get(SummaryRoute.ClaudeSettingsOutKey, null) == "true" && shownOn[SummaryRoute.ClaudeSettingsOutKey] == "true" &&
                            SummaryRoute.LeavesOutOwnClaudeSettings(s));
                        check("remembrance cli-followups: with it saved on, Goes through Claude Code says the settings files are not read and what that drops: " +
                              shownOn["cliSendsClaude"],
                            shownOn["cliSendsClaude"].EndsWith(" " + CodingAgentCliText.ClaudeOwnSetupLeftOut + " " + CodingAgentCliText.ClaudeOwnSettingsFilesLeftOut,
                                StringComparison.Ordinal) &&
                            !shownOn["cliSendsCodex"].Contains(CodingAgentCliText.ClaudeOwnSettingsFilesLeftOut));

                        // ---- Validate tests the switch as the card shows it, applied or not ----
                        PaneAction validate = PaneActionFor(pane, SummaryRoute.CardGroup, "Validate");
                        Dictionary<string, string> offOnScreen = CopyOf(shownOn);
                        offOnScreen[SummaryRoute.ClaudeSettingsOutKey] = "false";
                        fake.Clear();
                        string offAnswer = Press(validate, offOnScreen, ui, TimeSpan.FromSeconds(10)) ?? "(no answer)";
                        FakeCliCall offCall = fake.Calls.Find(delegate(FakeCliCall c) { return c.IsModelCall; });
                        Dictionary<string, string> onOnScreen = CopyOf(shownOn);
                        fake.Clear();
                        string onAnswer = Press(validate, onOnScreen, ui, TimeSpan.FromSeconds(10)) ?? "(no answer)";
                        FakeCliCall onCall = fake.Calls.Find(delegate(FakeCliCall c) { return c.IsModelCall; });
                        check("remembrance cli-followups: Validate tests the switch as the card shows it, applied or not: " + offAnswer + " | " + onAnswer,
                            offAnswer.StartsWith("✓ Claude Code", StringComparison.Ordinal) && offCall != null && !offCall.NamesSettingSources &&
                            onAnswer.StartsWith("✓ Claude Code", StringComparison.Ordinal) && onCall != null && onCall.LeavesOutOwnClaudeSettings &&
                            s.Get(SummaryRoute.ClaudeSettingsOutKey, null) == "true");
                        Dictionary<string, string> onCodex = CopyOf(shownOn);
                        onCodex[SummaryRoute.SettingKey] = "Codex CLI";
                        fake.Clear();
                        string codexAnswer = Press(validate, onCodex, ui, TimeSpan.FromSeconds(10)) ?? "(no answer)";
                        FakeCliCall codexCall = fake.Calls.Find(delegate(FakeCliCall c) { return c.IsModelCall; });
                        check("WITNESS remembrance cli-followups: a Codex Validate never carries the switch's flag, the switch saved on: " + codexAnswer,
                            codexAnswer.StartsWith("✓ Codex", StringComparison.Ordinal) && codexCall != null && codexCall.IsCodex &&
                            !codexCall.NamesSettingSources);
                    }
                    finally { try { module.Shutdown(); } catch { } }
                }
            }
            catch (Exception ex) { check("remembrance cli-followups: the checks threw " + ex.GetType().Name + ": " + ex.Message, false); }
            finally
            {
                ListModels = savedLister;
                SynchronizationContext.SetSynchronizationContext(previous);
                try { System.IO.Directory.Delete(scratch, true); } catch { }
            }
        }
    }
}
