using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using DesktopAICompanion.ModuleKit;

namespace DesktopAICompanion.AgentFlow
{
    public sealed partial class AgentFlowModule
    {
        /// <summary>
        /// 1.12.0, the owner's report of 2026-10-21 that 1.11.0 was "still not clearing multiple Codex requests in a
        /// single pass". Measured the same day with docs/agentflow/agentflow_codex_card_count.py: ONE Codex panel shows
        /// both shapes. Stacked, two cards in the DOM at once, where the reader took the first and the second waited
        /// 74 s through fifteen sweeps. Queued, the next request's card mounting 26 to 240 ms after a press, so a queue
        /// of N took N sweeps. The reader now returns every card, the click finds its card by element id, and a press
        /// in a Codex panel is followed by re-reads of it within the sweep. Sweeps run over FakeCdpServer through
        /// SweepOnce, the pairing OnTick runs; AfterClicks scripts what the page becomes after each press.
        /// </summary>
        private static bool SelfCheckCodexQueue(SelfTestProbe probe)
        {
            string codexUrl = "vscode-webview://y/index.html?" + CdpApprover.CodexTargetMarker;
            string claudeUrl = "vscode-webview://x/index.html?" + CdpApprover.ClaudeTargetMarker;
            Func<string, string> codex = id => "{\"tool\":\"\",\"header\":\"\",\"ext\":\"\",\"fp\":\"0c0dec0d\",\"card\":\"" + id
                                               + "\",\"fresh\":false,\"options\":[\"Deny\",\"Allow once\"],\"disabled\":[false,false]}";
            Func<string, string> claude = id => "{\"tool\":\"Bash\",\"header\":\"ls\",\"ext\":\"\",\"fp\":\"0badf00d\",\"card\":\"" + id
                                                + "\",\"fresh\":false,\"options\":[\"Yes\",\"No\"],\"disabled\":[false,false]}";
            Func<string[], string> stacked = ids =>
            {
                var parts = new List<string>();
                foreach (string id in ids) parts.Add(codex(id));
                return "{\"cards\":[" + string.Join(",", parts.ToArray()) + "],\"blind\":0}";
            };
            Func<int, string, KeyValuePair<int, string>> at = (ms, reply) => new KeyValuePair<int, string>(ms, reply);
            Func<FakeCdpServer.Target, string> clicked = t => string.Join(",", t.ClickedCards.ToArray());
            bool sawPanel, sawBlind;

            // ---- the reply and the expressions ------------------------------------------------------------
            int blind;
            List<PromptView> two = CdpApprover.ParseCards("t1", stacked(new[] { "c0de0001", "c0de0002" }), out blind);
            probe.Check("WITNESS (1.12.0) a read of two cards is two views, in document order, each with its own element id",
                two.Count == 2 && blind == 0 && two[0].CardId == "c0de0001" && two[1].CardId == "c0de0002"
                && two[1].TargetId == "t1" && two[1].Options.Count == 2);
            List<PromptView> partly = CdpApprover.ParseCards("t1",
                "{\"cards\":[" + codex("c0de0003") + ",{\"card\":\"c0de0004\",\"options\":[]}],\"blind\":1}", out blind);
            probe.Check("(1.12.0) ...a card that will not come apart is counted blind beside the reader's own count, and the rest still read",
                partly.Count == 1 && partly[0].CardId == "c0de0003" && blind == 2);
            List<PromptView> single = CdpApprover.ParseCards("t1", codex("c0de0005"), out blind);
            probe.Check("(1.12.0) ...a one-card reply, every reply before 1.12.0 and every Claude one, is one view",
                single.Count == 1 && single[0].CardId == "c0de0005" && blind == 0);
            probe.Check("(1.12.0) ...and malformed JSON is no cards, not a crash",
                CdpApprover.ParseCards("t1", "{not json", out blind).Count == 0 && blind == 0
                && CdpApprover.ParseCards("t1", null, out blind).Count == 0);

            string read = CdpApprover.CodexReadExpressionForSelfTest;
            probe.Check("WITNESS (1.12.0) the Codex reader reads EVERY card, not querySelector's first, and answers them all",
                read.IndexOf("var cards = a.doc.querySelectorAll('[class*=\"@container/approval-card\"]');", StringComparison.Ordinal) >= 0
                && read.IndexOf("for (var ci = 0; ci < cards.length; ci++)", StringComparison.Ordinal) >= 0
                && read.IndexOf("return JSON.stringify({ cards: all, blind: blind });", StringComparison.Ordinal) >= 0
                && read.IndexOf("a.doc.querySelector('[class*=\"@container/approval-card\"]')", StringComparison.Ordinal) < 0);
            string named = CdpApprover.BuildCodexClick(1, "Allow once", "c0ffee01");
            string unnamed = CdpApprover.BuildCodexClick(1, "Allow once");
            probe.Check("WITNESS (1.12.0) the Codex click names the card it was decided on, as a string literal",
                named.IndexOf("var want = \"c0ffee01\";", StringComparison.Ordinal) >= 0
                && named.IndexOf("__CARD__", StringComparison.Ordinal) < 0
                && unnamed.IndexOf("var want = \"\";", StringComparison.Ordinal) >= 0);
            string click = CdpApprover.CodexClickExpressionForSelfTest;
            probe.Check("WITNESS (1.12.0) ...finds it by the id the reader minted, in the reader's map, and takes an unnamed card only when it is the only one",
                click.IndexOf("var minted = window.__agentflowCards;", StringComparison.Ordinal) >= 0
                && click.IndexOf("if (minted.get(cards[ci]) === want) { card = cards[ci]; break; }", StringComparison.Ordinal) >= 0
                && click.IndexOf("} else if (cards.length === 1) card = cards[0];", StringComparison.Ordinal) >= 0
                && click.IndexOf("if (!card) return 'gone';", StringComparison.Ordinal) >= 0);

            // ---- stacked: two cards in one panel at once -------------------------------------------------
            var pair = new FakeCdpServer.Target { Id = "codex-stacked", Url = codexUrl, ClickResult = "clicked",
                                                  EvaluateResult = stacked(new[] { "5a000001", "5a000002" }) };
            pair.AfterClicks.Enqueue(new[] { at(0, stacked(new[] { "5a000002" })) });
            pair.AfterClicks.Enqueue(new[] { at(0, "none") });
            using (var server = new FakeCdpServer(new[] { pair }))
            {
                var budget = new PressBudget();
                var pass = new SweepPass(server.Port, true, false, false, budget, () => true);
                SweepOnce(server.Port, pass, budget, 4000, out sawPanel, out sawBlind);
                probe.Check("WITNESS (1.12.0) two Codex cards on screen at once in one panel are both pressed in one sweep, each by its own id ("
                            + clicked(pair) + ")",
                    pair.Clicks == 2 && clicked(pair) == "5a000001,5a000002" && pass.PressNotes.Count == 2 && sawPanel);
            }

            // A stack whose FIRST card is refused: nothing is pressed there, so nothing is followed, and only reading
            // every card reaches the second. Before 1.12.0 the second card waited for as long as the first stood.
            var refusedFirst = new FakeCdpServer.Target { Id = "codex-refused-first", Url = codexUrl, ClickResult = "clicked",
                EvaluateResult = "{\"cards\":[" + codex("4e000001").Replace("\"Allow once\"", "\"Something new\"") + ","
                                 + codex("4e000002") + "],\"blind\":0}" };
            using (var server = new FakeCdpServer(new[] { refusedFirst }))
            {
                var budget = new PressBudget();
                var pass = new SweepPass(server.Port, true, false, false, budget, () => true);
                SweepOnce(server.Port, pass, budget, 4000, out sawPanel, out sawBlind);
                probe.Check("WITNESS (1.12.0) a card beside a refused first card is still pressed on the same sweep, by its own id ("
                            + clicked(refusedFirst) + ")",
                    refusedFirst.Clicks == 1 && clicked(refusedFirst) == "4e000002");
            }

            // ---- queued: the next card mounts after a press ------------------------------------------------
            // The measured shape: the pressed card leaves at once, and the next request's card mounts 150 ms later.
            var queue = new FakeCdpServer.Target { Id = "codex-queue", Url = codexUrl, ClickResult = "clicked", EvaluateResult = codex("9e000001") };
            queue.AfterClicks.Enqueue(new[] { at(0, "none"), at(150, codex("9e000002")) });
            queue.AfterClicks.Enqueue(new[] { at(0, "none"), at(150, codex("9e000003")) });
            queue.AfterClicks.Enqueue(new[] { at(0, "none") });
            using (var server = new FakeCdpServer(new[] { queue }))
            {
                var budget = new PressBudget();
                var pass = new SweepPass(server.Port, true, false, false, budget, () => true);
                Stopwatch took = Stopwatch.StartNew();
                SweepOnce(server.Port, pass, budget, 4000, out sawPanel, out sawBlind);
                took.Stop();
                probe.Check("WITNESS (1.12.0) a queue of three Codex requests in one panel is cleared in ONE sweep, in order ("
                            + clicked(queue) + ")",
                    queue.Clicks == 3 && clicked(queue) == "9e000001,9e000002,9e000003" && pass.PressNotes.Count == 3);
                probe.Check("(1.12.0) ...and the sweep that followed it ends within its budget (" + took.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)",
                    took.ElapsedMilliseconds < CdpApprover.QueueBudgetMs + 1500);
            }

            // ---- the same card is never pressed twice in one sweep ----------------------------------------
            // A card whose Allow once does nothing: the follow sees the same element and waits it out. The repeat
            // guard then counts one press per sweep, so it is still five sweeps to the stand-down, never one.
            var stuck = new FakeCdpServer.Target { Id = "codex-stuck", Url = codexUrl, ClickResult = "clicked", EvaluateResult = codex("57000001") };
            using (var server = new FakeCdpServer(new[] { stuck }))
            {
                var budget = new PressBudget();
                SweepOnce(server.Port, new SweepPass(server.Port, true, false, false, budget, () => true), budget, 4000, out sawPanel, out sawBlind);
                int afterOne = stuck.Clicks;
                for (int i = 1; i < PressBudget.MaxIdenticalPresses + 1; i++)
                    SweepOnce(server.Port, new SweepPass(server.Port, true, false, false, budget, () => true), budget, 4000, out sawPanel, out sawBlind);
                probe.Check("WITNESS (1.12.0) a card that stays after its press is pressed once a sweep, never again inside the sweep that pressed it ("
                            + afterOne.ToString(CultureInfo.InvariantCulture) + ")",
                    afterOne == 1);
                probe.Check("(1.12.0) ...so the repeat guard still stands it down at five presses over five sweeps ("
                            + stuck.Clicks.ToString(CultureInfo.InvariantCulture) + " in "
                            + (PressBudget.MaxIdenticalPresses + 1).ToString(CultureInfo.InvariantCulture) + ")",
                    stuck.Clicks == PressBudget.MaxIdenticalPresses);
            }

            // ---- a card slow to leave, and a successor that mounts disabled -------------------------------
            var slow = new FakeCdpServer.Target { Id = "codex-slow", Url = codexUrl, ClickResult = "clicked", EvaluateResult = codex("51000001") };
            slow.AfterClicks.Enqueue(new[] { at(0, codex("51000001").Replace("[false,false]", "[true,true]")), at(300, "none"), at(450, codex("51000002")) });
            slow.AfterClicks.Enqueue(new[] { at(0, "none") });
            using (var server = new FakeCdpServer(new[] { slow }))
            {
                var budget = new PressBudget();
                var pass = new SweepPass(server.Port, true, false, false, budget, () => true);
                SweepOnce(server.Port, pass, budget, 4000, out sawPanel, out sawBlind);
                probe.Check("WITNESS (1.12.0) a pressed card still leaving is waited out, not pressed again, and the card after it is pressed in the same sweep ("
                            + clicked(slow) + ")",
                    slow.Clicks == 2 && clicked(slow) == "51000001,51000002");
            }
            var settling = new FakeCdpServer.Target { Id = "codex-settle", Url = codexUrl, ClickResult = "clicked", EvaluateResult = codex("5e000001") };
            settling.AfterClicks.Enqueue(new[] { at(0, "none"),
                at(120, codex("5e000002").Replace("\"fresh\":false", "\"fresh\":true").Replace("[false,false]", "[true,true]")),
                at(420, codex("5e000002")) });
            settling.AfterClicks.Enqueue(new[] { at(0, "none") });
            using (var server = new FakeCdpServer(new[] { settling }))
            {
                var budget = new PressBudget();
                var pass = new SweepPass(server.Port, true, false, false, budget, () => true);
                SweepOnce(server.Port, pass, budget, 4000, out sawPanel, out sawBlind);
                probe.Check("WITNESS (1.12.0) a next card that mounts with its rows disabled is waited for and pressed once they enable, in the same sweep, unannounced ("
                            + clicked(settling) + ")",
                    settling.Clicks == 2 && clicked(settling) == "5e000001,5e000002" && pass.Unpressed.Count == 0);
            }

            var stillSettling = new FakeCdpServer.Target { Id = "codex-unsettled", Url = codexUrl, ClickResult = "clicked", EvaluateResult = codex("5d000001") };
            stillSettling.AfterClicks.Enqueue(new[] { at(0, "none"),
                at(100, codex("5d000002").Replace("\"fresh\":false", "\"fresh\":true").Replace("[false,false]", "[true,true]")),
                at(250, codex("5d000002").Replace("[false,false]", "[true,true]")) });
            using (var server = new FakeCdpServer(new[] { stillSettling }))
            {
                var budget = new PressBudget();
                var pass = new SweepPass(server.Port, true, false, false, budget, () => true);
                SweepOnce(server.Port, pass, budget, 4000, out sawPanel, out sawBlind);
                probe.Check("WITNESS (1.12.0) a next card still disabled when the window closes is left for the next sweep, neither pressed nor announced on this one ("
                            + stillSettling.Clicks.ToString(CultureInfo.InvariantCulture) + " press, "
                            + pass.Unpressed.Count.ToString(CultureInfo.InvariantCulture) + " announced)",
                    stillSettling.Clicks == 1 && pass.Unpressed.Count == 0);
            }

            // ---- what is not followed ------------------------------------------------------------------------
            var unminted = new FakeCdpServer.Target { Id = "codex-unminted", Url = codexUrl, ClickResult = "clicked", EvaluateResult = codex("0e000001") };
            unminted.AfterClicks.Enqueue(new[] { at(0, codex("").Replace(",\"card\":\"\"", "").Replace("0c0dec0d", "0c0dec0f")) });
            using (var server = new FakeCdpServer(new[] { unminted }))
            {
                var budget = new PressBudget();
                SweepOnce(server.Port, new SweepPass(server.Port, true, false, false, budget, () => true), budget, 4000, out sawPanel, out sawBlind);
                probe.Check("WITNESS (1.12.0) a next card with no element id is not pressed by the follow, which could not tell it from itself on the next read ("
                            + unminted.Clicks.ToString(CultureInfo.InvariantCulture) + ")",
                    unminted.Clicks == 1);
            }
            var noId = new FakeCdpServer.Target { Id = "codex-noid", Url = codexUrl, ClickResult = "clicked",
                                                  EvaluateResult = codex("").Replace(",\"card\":\"\"", "") };
            noId.AfterClicks.Enqueue(new[] { at(0, codex("").Replace(",\"card\":\"\"", "").Replace("0c0dec0d", "0c0dec0e")) });
            var claudeQueue = new FakeCdpServer.Target { Id = "claude-queue", Url = claudeUrl, ClickResult = "clicked", EvaluateResult = claude("c1000001") };
            claudeQueue.AfterClicks.Enqueue(new[] { at(0, claude("c1000002")) });
            using (var server = new FakeCdpServer(new[] { claudeQueue, noId }))
            {
                var budget = new PressBudget();
                SweepOnce(server.Port, new SweepPass(server.Port, true, false, false, budget, () => true), budget, 4000, out sawPanel, out sawBlind);
                probe.Check("WITNESS (1.12.0) a press of a Codex card with no element id is not followed: nothing could tell the next card from it ("
                            + noId.Clicks.ToString(CultureInfo.InvariantCulture) + ")",
                    noId.Clicks == 1);
                probe.Check("WITNESS (1.12.0) a Claude Code panel is still read once a sweep: its queue is not followed ("
                            + claudeQueue.Clicks.ToString(CultureInfo.InvariantCulture) + ")",
                    claudeQueue.Clicks == 1 && !CdpApprover.FollowsQueue(CdpApprover.AgentClaude) && CdpApprover.FollowsQueue(CdpApprover.AgentCodex));
            }

            // ---- the bounds ----------------------------------------------------------------------------------
            var endless = new FakeCdpServer.Target { Id = "codex-endless", Url = codexUrl, ClickResult = "clicked", EvaluateResult = codex("e0000000") };
            for (int i = 1; i <= 30; i++)
                endless.AfterClicks.Enqueue(new[] { at(0, codex("e" + i.ToString("x7", CultureInfo.InvariantCulture))) });
            using (var server = new FakeCdpServer(new[] { endless }))
            {
                var budget = new PressBudget();
                Stopwatch took = Stopwatch.StartNew();
                SweepOnce(server.Port, new SweepPass(server.Port, true, false, false, budget, () => true), budget, 4000, out sawPanel, out sawBlind);
                took.Stop();
                probe.Check("WITNESS (1.12.0) a queue that never ends is pressed at most " + CdpApprover.QueuePressesPerPanel.ToString(CultureInfo.InvariantCulture)
                            + " times in one panel in one sweep (" + endless.Clicks.ToString(CultureInfo.InvariantCulture) + " in "
                            + took.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)",
                    endless.Clicks == CdpApprover.QueuePressesPerPanel);
            }
            // Ten cards at once, all handed on by the read itself, and an eleventh that mounts after the tenth press: the
            // read's own presses count toward the cap, so the follow does not start.
            var tenIds = new List<string>();
            for (int i = 0; i < CdpApprover.QueuePressesPerPanel; i++) tenIds.Add("70" + i.ToString("x6", CultureInfo.InvariantCulture));
            var ten = new FakeCdpServer.Target { Id = "codex-ten", Url = codexUrl, ClickResult = "clicked", EvaluateResult = stacked(tenIds.ToArray()) };
            for (int i = 1; i < CdpApprover.QueuePressesPerPanel; i++) ten.AfterClicks.Enqueue(new KeyValuePair<int, string>[0]);
            ten.AfterClicks.Enqueue(new[] { at(0, codex("7f000000")) });
            using (var server = new FakeCdpServer(new[] { ten }))
            {
                var budget = new PressBudget();
                SweepOnce(server.Port, new SweepPass(server.Port, true, false, false, budget, () => true), budget, 4000, out sawPanel, out sawBlind);
                probe.Check("WITNESS (1.12.0) the read's own presses count toward the cap: ten cards at once are all pressed, and the follow does not start for an eleventh ("
                            + ten.Clicks.ToString(CultureInfo.InvariantCulture) + ")",
                    ten.Clicks == CdpApprover.QueuePressesPerPanel);
            }
            var drip = new FakeCdpServer.Target { Id = "codex-drip", Url = codexUrl, ClickResult = "clicked", EvaluateResult = codex("d0000000") };
            for (int i = 1; i <= 30; i++)
                drip.AfterClicks.Enqueue(new[] { at(0, "none"), at(450, codex("d" + i.ToString("x7", CultureInfo.InvariantCulture))) });
            using (var server = new FakeCdpServer(new[] { drip }))
            {
                var budget = new PressBudget();
                Stopwatch took = Stopwatch.StartNew();
                SweepOnce(server.Port, new SweepPass(server.Port, true, false, false, budget, () => true), budget, 4000, out sawPanel, out sawBlind);
                took.Stop();
                probe.Check("WITNESS (1.12.0) a queue that drips a card every 450 ms is followed for the sweep's budget and no longer ("
                            + drip.Clicks.ToString(CultureInfo.InvariantCulture) + " presses in " + took.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)",
                    drip.Clicks >= 3 && drip.Clicks < CdpApprover.QueuePressesPerPanel
                    && took.ElapsedMilliseconds < CdpApprover.QueueBudgetMs + 1500);
            }

            // ---- every press is still a press: the switch, the rate cap ------------------------------------
            var armed = new FakeCdpServer.Target { Id = "codex-armed", Url = codexUrl, ClickResult = "clicked", EvaluateResult = codex("a7000001") };
            armed.AfterClicks.Enqueue(new[] { at(0, codex("a7000002")) });
            using (var server = new FakeCdpServer(new[] { armed }))
            {
                var budget = new PressBudget();
                int looks = 0;
                var pass = new SweepPass(server.Port, true, false, false, budget, () => ++looks <= 1);
                SweepOnce(server.Port, pass, budget, 4000, out sawPanel, out sawBlind);
                probe.Check("WITNESS (1.12.0) the switch is looked at again before every press the queue makes: switched off after the first, the next card is left ("
                            + armed.Clicks.ToString(CultureInfo.InvariantCulture) + ")",
                    armed.Clicks == 1 && pass.PressNotes.Count == 1 && looks >= 2);
            }
            var capped = new FakeCdpServer.Target { Id = "codex-capped", Url = codexUrl, ClickResult = "clicked", EvaluateResult = codex("ca000001") };
            capped.AfterClicks.Enqueue(new[] { at(0, codex("ca000002")) });
            capped.AfterClicks.Enqueue(new[] { at(0, codex("ca000003")) });
            using (var server = new FakeCdpServer(new[] { capped }))
            {
                var budget = new PressBudget();
                budget.SetPressLimit(2);
                var pass = new SweepPass(server.Port, true, false, false, budget, () => true);
                SweepOnce(server.Port, pass, budget, 4000, out sawPanel, out sawBlind);
                probe.Check("WITNESS (1.12.0) every press the queue makes counts against the approval limit: a limit of two stops a queue of three at two ("
                            + capped.Clicks.ToString(CultureInfo.InvariantCulture) + ")",
                    capped.Clicks == 2);
            }

            // ---- two panels, both shapes, and a card that cannot be read ------------------------------------
            var x = new FakeCdpServer.Target { Id = "codex-x", Url = codexUrl, ClickResult = "clicked", EvaluateResult = codex("aa000001") };
            x.AfterClicks.Enqueue(new[] { at(0, "none"), at(100, codex("aa000002")) });
            x.AfterClicks.Enqueue(new[] { at(0, "none") });
            var y = new FakeCdpServer.Target { Id = "codex-y", Url = codexUrl, ClickResult = "clicked",
                                               EvaluateResult = stacked(new[] { "bb000001", "bb000002" }).Replace("\"blind\":0", "\"blind\":1") };
            y.AfterClicks.Enqueue(new[] { at(0, stacked(new[] { "bb000002" }).Replace("\"blind\":0", "\"blind\":1")) });
            y.AfterClicks.Enqueue(new[] { at(0, "blind") });
            using (var server = new FakeCdpServer(new[] { x, y }))
            {
                var budget = new PressBudget();
                var pass = new SweepPass(server.Port, true, false, false, budget, () => true);
                SweepOnce(server.Port, pass, budget, 4000, out sawPanel, out sawBlind);
                probe.Check("WITNESS (1.12.0) one sweep clears a queue in one Codex panel and a stack in another ("
                            + clicked(x) + " / " + clicked(y) + ")",
                    clicked(x) == "aa000001,aa000002" && clicked(y) == "bb000001,bb000002" && pass.PressNotes.Count == 4);
                probe.Check("(1.12.0) ...and a card it could not read beside them is still said, with the panel still seen",
                    sawBlind && sawPanel);
            }
            return true;
        }
    }
}
