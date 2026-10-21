using System;
using System.Collections.Generic;
using System.Globalization;
using DesktopAICompanion.ModuleKit;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentFlow
{
    public sealed partial class AgentFlowModule
    {
        /// <summary>
        /// 1.11.0, the owner's asks of 2026-10-21: one sweep presses the prompt in every panel, the repeat guard counts
        /// per panel and card (BUG-018) and leaves a card alone after five presses, the check interval is a setting
        /// with the transcripts on their own beat, and Codex cards are told apart by their element. Sweeps run over
        /// FakeCdpServer through SweepOnce, the pairing OnTick runs, so the budget's end of sweep is the production
        /// one rather than a copy of it.
        /// </summary>
        private static bool SelfCheckMultiPress(SelfTestProbe probe)
        {
            string claudeUrl = "vscode-webview://x/index.html?" + CdpApprover.ClaudeTargetMarker;
            string codexUrl = "vscode-webview://y/index.html?" + CdpApprover.CodexTargetMarker;
            // A Claude Bash card with an element id, and a Codex card in Codex's own labels; __CARD__ is the element.
            const string ClaudeCard = "{\"tool\":\"Bash\",\"header\":\"ls\",\"ext\":\"\",\"fp\":\"0badf00d\",\"card\":\"__CARD__\","
                                      + "\"fresh\":false,\"options\":[\"Yes\",\"No\"],\"disabled\":[false,false]}";
            const string CodexCard = "{\"tool\":\"\",\"header\":\"\",\"ext\":\"\",\"fp\":\"0c0dec0d\",\"card\":\"__CARD__\","
                                     + "\"fresh\":false,\"options\":[\"Deny\",\"Allow once\"],\"disabled\":[false,false]}";
            Func<string, string> claude = id => ClaudeCard.Replace("__CARD__", id);
            Func<string, string> codex = id => CodexCard.Replace("__CARD__", id);
            bool sawPanel, sawBlind;
            int cap = PressBudget.MaxIdenticalPresses;

            probe.Check("WITNESS (1.11.0) the repeat cap is five presses of one card, the owner's number of 2026-10-21",
                cap == 5);

            // ---- every panel in one sweep ------------------------------------------------------------------
            var c1 = new FakeCdpServer.Target { Id = "claude-1", Url = claudeUrl, EvaluateResult = claude("a0000001"), ClickResult = "clicked" };
            var c2 = new FakeCdpServer.Target { Id = "claude-2", Url = claudeUrl, EvaluateResult = claude("a0000002"), ClickResult = "clicked" };
            var x1 = new FakeCdpServer.Target { Id = "codex-1", Url = codexUrl, EvaluateResult = codex("a0000003"), ClickResult = "clicked" };
            using (var three = new FakeCdpServer(new[] { c1, c2, x1 }))
            {
                var budget = new PressBudget();
                var pass = new SweepPass(three.Port, true, false, false, budget, () => true);
                string note = SweepOnce(three.Port, pass, budget, 4000, out sawPanel, out sawBlind);
                probe.Check("WITNESS (1.11.0) one sweep presses the waiting prompt in every panel, once each ("
                            + c1.Clicks.ToString(CultureInfo.InvariantCulture) + "/" + c2.Clicks.ToString(CultureInfo.InvariantCulture)
                            + "/" + x1.Clicks.ToString(CultureInfo.InvariantCulture) + ")",
                    c1.Clicks == 1 && c2.Clicks == 1 && x1.Clicks == 1 && pass.PressedAny && sawPanel);
                probe.Check("(1.11.0) ...each press has its own note, and the sweep's answer joins all three",
                    pass.PressNotes.Count == 3 && note != null && CountOccurrences(note, "auto-approve clicked") == 3);
            }

            // ---- BUG-018: a stuck card beside a working panel ----------------------------------------------
            // The stuck card is one element that never leaves; the other panel shows a new card each sweep, as a
            // queue does. Until 1.11.0 the guard was one counter, and the other panel's different signature reset it,
            // so the stuck card was pressed again as a new prompt, three ticks in four, for as long as it stood. Both
            // orders, because which panel /json/list names first must not decide it.
            foreach (bool stuckFirst in new[] { true, false })
            {
                var stuck = new FakeCdpServer.Target { Id = "claude-stuck", Url = claudeUrl, EvaluateResult = claude("5ca1ab1e"), ClickResult = "clicked" };
                var queue = new FakeCdpServer.Target { Id = "claude-queue", Url = claudeUrl, ClickResult = "clicked" };
                using (var pair = new FakeCdpServer(stuckFirst ? new[] { stuck, queue } : new[] { queue, stuck }))
                {
                    var budget = new PressBudget();
                    const int Sweeps = 12;
                    string last = null;
                    for (int i = 0; i < Sweeps; i++)
                    {
                        queue.EvaluateResult = claude("e0e0e" + i.ToString("x3", CultureInfo.InvariantCulture));
                        var pass = new SweepPass(pair.Port, true, false, false, budget, () => true);
                        last = SweepOnce(pair.Port, pass, budget, 4000, out sawPanel, out sawBlind);
                    }
                    string order = stuckFirst ? "stuck card listed first" : "stuck card listed second";
                    probe.Check("WITNESS (1.11.0) BUG-018: a stuck card beside a working panel is pressed up to the cap and no more ("
                                + order + ": " + stuck.Clicks.ToString(CultureInfo.InvariantCulture) + " presses in "
                                + Sweeps.ToString(CultureInfo.InvariantCulture) + " sweeps)",
                        stuck.Clicks == cap);
                    probe.Check("(1.11.0) ...while the working panel's queue is pressed on every sweep (" + order + ": "
                                + queue.Clicks.ToString(CultureInfo.InvariantCulture) + ")",
                        queue.Clicks == Sweeps && last != null && last.IndexOf("auto-approve clicked", StringComparison.Ordinal) >= 0);
                    probe.Check("(1.11.0) ...and the budget holds a count for each card on screen, not for every card ever pressed ("
                                + order + ": " + budget.RepeatsHeld.ToString(CultureInfo.InvariantCulture) + ")",
                        budget.RepeatsHeld == 2);

                    if (!stuckFirst) continue;

                    // What keeps a stood-down card's count, and what drops it, on the same panel.
                    stuck.RefuseAttach = true;
                    SweepOnce(pair.Port, new SweepPass(pair.Port, true, false, false, budget, () => true), budget, 4000,
                              out sawPanel, out sawBlind);
                    stuck.RefuseAttach = false;
                    SweepOnce(pair.Port, new SweepPass(pair.Port, true, false, false, budget, () => true), budget, 4000,
                              out sawPanel, out sawBlind);
                    probe.Check("WITNESS (1.11.0) a panel that could not be read keeps its card's count: the stuck card is still left alone after it",
                        stuck.Clicks == cap);

                    stuck.EvaluateResult = claude("5ca1ab1e").Replace("[false,false]", "[true,true]");
                    SweepOnce(pair.Port, new SweepPass(pair.Port, true, false, false, budget, () => true), budget, 4000,
                              out sawPanel, out sawBlind);
                    stuck.EvaluateResult = claude("5ca1ab1e");
                    SweepOnce(pair.Port, new SweepPass(pair.Port, true, false, false, budget, () => true), budget, 4000,
                              out sawPanel, out sawBlind);
                    probe.Check("WITNESS (1.11.0) a stood-down card read once with its rows disabled keeps its count too",
                        stuck.Clicks == cap);

                    stuck.EvaluateResult = "none";
                    SweepOnce(pair.Port, new SweepPass(pair.Port, true, false, false, budget, () => true), budget, 4000,
                              out sawPanel, out sawBlind);
                    stuck.EvaluateResult = claude("5ca1ab1e");
                    SweepOnce(pair.Port, new SweepPass(pair.Port, true, false, false, budget, () => true), budget, 4000,
                              out sawPanel, out sawBlind);
                    probe.Check("WITNESS (1.11.0) ...while a sweep that read the panel without the card drops its count, so it is pressed afresh",
                        stuck.Clicks == cap + 1);
                }
            }

            // ---- the budget's end of sweep, directly -------------------------------------------------------
            string sig = PressBudget.Signature("", new[] { "Deny", "Allow once" }, "0c0dec0d");
            DateTime t0 = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
            string refusal;
            var twoPanels = new PressBudget();
            for (int i = 0; i < cap; i++) twoPanels.TryPress("panel-1", sig, t0.AddSeconds(i), out refusal);
            probe.Check("(1.11.0) alike cards in two panels are two counts: the first panel's card at the cap does not stand the second down",
                !twoPanels.TryPress("panel-1", sig, t0.AddSeconds(10), out refusal)
                && twoPanels.TryPress("panel-2", sig, t0.AddSeconds(11), out refusal));

            var unread = new PressBudget();
            for (int i = 0; i < cap; i++) unread.TryPress("panel-1", sig, t0.AddSeconds(i), out refusal);
            unread.EndSweep(new[] { "panel-1" }, new string[0]);
            probe.Check("(1.11.0) a panel listed and not read keeps its counts",
                unread.RepeatsHeld == 1 && !unread.TryPress("panel-1", sig, t0.AddSeconds(20), out refusal));
            unread.EndSweep(new[] { "panel-2" }, new[] { "panel-2" });
            probe.Check("WITNESS (1.11.0) a panel no longer listed loses its counts, so a closed panel leaves nothing behind",
                unread.RepeatsHeld == 0 && unread.TryPress("panel-1", sig, t0.AddSeconds(30), out refusal));

            // ---- Codex: alike cards told apart by their element --------------------------------------------
            // N-agentflow-card-identity-01: a run of Codex cards that read alike, answered one after another with no
            // empty sweep between, stood auto-approve down while Codex reads carried no element id. They carry one now.
            var codexQueue = new FakeCdpServer.Target { Id = "codex-q", Url = codexUrl, ClickResult = "clicked" };
            using (var codexServer = new FakeCdpServer(new[] { codexQueue }))
            {
                Func<bool, int> run = withIds =>
                {
                    var budget = new PressBudget();
                    codexQueue.Clicks = 0;
                    for (int i = 0; i < 7; i++)
                    {
                        string reply = codex("c0dec" + i.ToString("x3", CultureInfo.InvariantCulture));
                        if (!withIds) reply = reply.Replace(",\"card\":\"c0dec" + i.ToString("x3", CultureInfo.InvariantCulture) + "\"", "");
                        codexQueue.EvaluateResult = reply;
                        SweepOnce(codexServer.Port, new SweepPass(codexServer.Port, true, false, false, budget, () => true),
                                  budget, 4000, out sawPanel, out sawBlind);
                    }
                    return codexQueue.Clicks;
                };
                int withElements = run(true);
                int textOnly = run(false);
                probe.Check("WITNESS (1.11.0) seven alike Codex cards on seven elements are all pressed ("
                            + withElements.ToString(CultureInfo.InvariantCulture) + ")",
                    withElements == 7);
                probe.Check("(1.11.0) ...while the same cards with no element id still stop at the cap, so the seven were told apart by element ("
                            + textOnly.ToString(CultureInfo.InvariantCulture) + ")",
                    textOnly == cap);
            }

            // ---- the check interval, and the transcripts' own beat -----------------------------------------
            probe.Check("WITNESS (1.11.0) the transcripts are read on the first tick, and again once ten seconds less the slack have passed",
                TranscriptsDue(-1, 0) && TranscriptsDue(0, 9500) && !TranscriptsDue(0, 9499));
            Func<int, int, int> readsOver = (stepMs, spanMs) =>
            {
                long last = -1;
                int reads = 0;
                for (long now = 0; now <= spanMs; now += stepMs)
                    if (TranscriptsDue(last, now)) { last = now; reads++; }
                return reads;
            };
            probe.Check("WITNESS (1.11.0) at a one-second check the transcripts are read every ten seconds, not every second ("
                        + readsOver(1000, 60000).ToString(CultureInfo.InvariantCulture) + " reads in a minute)",
                readsOver(1000, 60000) == 7);
            probe.Check("(1.11.0) ...at the default ten seconds they are read on every tick, a timer firing 10 ms early included",
                readsOver(10000, 60000) == 7 && readsOver(9990, 59940) == 7);
            probe.Check("(1.11.0) ...and at thirty seconds on every tick",
                readsOver(30000, 60000) == 3);

            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            using (var storage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow"))
            {
                host.UseStorage("agentflow", storage);
                host.SettingsFor("agentflow").Set(SettingMode, AgentMode.Off);   // no scan at Init (F037)
                var module = new AgentFlowModule();
                module.Init(host);
                OptionsPane pane = host.OptionsPanes[0];

                probe.Check("WITNESS (1.11.0) the check interval defaults to ten seconds, and the timer runs at it",
                    module.CheckSeconds == DefaultCheckSeconds && DefaultCheckSeconds == 10
                    && module.TimerIntervalForSelfTest == 10000
                    && Shown(pane)[SettingCheckSeconds] == "10");

                pane.Save(new Dictionary<string, string> { { SettingCheckSeconds, "3" } });
                probe.Check("WITNESS (1.11.0) a saved interval reaches the running timer, not the next launch ("
                            + module.TimerIntervalForSelfTest.ToString(CultureInfo.InvariantCulture) + " ms)",
                    Shown(pane)[SettingCheckSeconds] == "3" && module.TimerIntervalForSelfTest == 3000);
                // The clamp where the value is READ, before any of it reaches the timer, which throws on an interval of
                // 0: so an unclamped read fails this line rather than taking the whole group down with it.
                string savedInterval = module._settings.Get(SettingCheckSeconds, null);
                module._settings.Set(SettingCheckSeconds, "0");
                bool floorRead = module.CheckSeconds == MinCheckSeconds;
                module._settings.Set(SettingCheckSeconds, "99");
                bool ceilingRead = module.CheckSeconds == MaxCheckSeconds;
                module._settings.Set(SettingCheckSeconds, savedInterval ?? "3");
                probe.Check("WITNESS (1.11.0) an out-of-range interval clamps to 1 to 30 seconds when it is read, so a hand-edited 0 never reaches the timer",
                    floorRead && ceilingRead);
                pane.Save(new Dictionary<string, string> { { SettingCheckSeconds, "0" } });
                bool floor = Shown(pane)[SettingCheckSeconds] == "1" && module.TimerIntervalForSelfTest == 1000;
                pane.Save(new Dictionary<string, string> { { SettingCheckSeconds, "99" } });
                bool ceiling = Shown(pane)[SettingCheckSeconds] == "30" && module.TimerIntervalForSelfTest == 30000;
                probe.Check("(1.11.0) ...and the pane and the running timer show the clamped value",
                    floor && ceiling);

                SettingField field = null;
                foreach (SettingField f in pane.Schema) if (f != null && f.Id == SettingCheckSeconds) field = f;
                probe.Check("(1.11.0) the pane's interval box is a 1 to 30 whole-second field, live in every mode that looks at the panels and greyed in Off",
                    field != null && field.Kind == SettingKind.Int && field.Min == 1 && field.Max == 30
                    && field.Label == CheckSecondsLabel
                    && field.EnabledWhen != null
                    && field.EnabledWhen.IndexOf(AgentMode.ToDisplay(AgentMode.AutoApprove), StringComparison.Ordinal) >= 0
                    && field.EnabledWhen.IndexOf(AgentMode.ToDisplay(AgentMode.Notify), StringComparison.Ordinal) >= 0
                    && field.EnabledWhen.IndexOf(AgentMode.ToDisplay(AgentMode.Log), StringComparison.Ordinal) >= 0
                    && field.EnabledWhen.IndexOf(AgentMode.ToDisplay(AgentMode.Off), StringComparison.Ordinal) < 0);

                // Each press its own log line, a refusal once.
                const string pressA = "auto-approve clicked for Bash: pressing option 1, recognised as 'yes' (approve-once)";
                const string pressB = "auto-approve clicked for an edit (.sh): pressing option 1, recognised as 'yes' (approve-once)";
                int before = CountLoggedContaining(host.LoggedLines, "auto-approve clicked");
                module.LogApprovalAttempts(pressA + " | " + pressB + " | " + pressA, true, new List<string> { pressA, pressB, pressA });
                probe.Check("WITNESS (1.11.0) a sweep that pressed three panels writes three log lines, one per press",
                    CountLoggedContaining(host.LoggedLines, "auto-approve clicked") - before == 3
                    && CountLoggedContaining(host.LoggedLines, pressA + " | ") == 0);
                module.Shutdown();
            }

            // A module seeded with an interval starts its timer at it.
            var seededHost = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            using (var seededStorage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow"))
            {
                seededHost.UseStorage("agentflow", seededStorage);
                seededHost.SettingsFor("agentflow").Set(SettingMode, AgentMode.Off);
                seededHost.SettingsFor("agentflow").Set(SettingCheckSeconds, "4");
                var seeded = new AgentFlowModule();
                seeded.Init(seededHost);
                probe.Check("(1.11.0) a saved interval is the timer's from the first tick after launch",
                    seeded.TimerIntervalForSelfTest == 4000);
                seeded.Shutdown();
            }
            return true;
        }
    }
}
