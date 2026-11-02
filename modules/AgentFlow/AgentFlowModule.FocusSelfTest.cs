using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.ModuleKit;

namespace DesktopAICompanion.AgentFlow
{
    public sealed partial class AgentFlowModule
    {
        /// <summary>
        /// 1.12.3, the owner's report of 2026-11-02: a press must not take focus from the card the user is working in.
        /// The press, probe and restore expressions run for real, in chakra.dll against <see cref="FakeFocusPage"/>'s DOM
        /// model, behind FakeCdpServer; reads stay canned. Sweeps go through SweepOnce, the pairing OnTick runs, so the
        /// panel list reaches the press the way production hands it on. The focus paths are the ones Codex's audit of the
        /// installed bundles traced (2026-11-02): Claude Code's permission-clear effect (100 ms after the card clears,
        /// ten seconds later on its diff path) and a Codex follow-on card's autofocus on mount. Every "never left" check
        /// reads the page's focus log, so focus that left and was put back fails it: the net cannot mask the hold.
        /// </summary>
        private static bool SelfCheckFocusHold(SelfTestProbe probe)
        {
            string why;
            using (FakeFocusPage probePage = FakeFocusPage.TryCreate(out why))
            {
                if (probePage == null)
                {
                    probe.Skip("(1.12.3) the focus checks run the press expressions in Windows' chakra.dll JsRT, and it is "
                               + "not usable here: " + why);
                    return true;
                }
            }
            const string ClaudeCard = "{\"tool\":\"Bash\",\"header\":\"ls\",\"ext\":\"\",\"fp\":\"0badf00d\",\"card\":\"b0000001\","
                                      + "\"fresh\":false,\"options\":[\"Yes\",\"No\"],\"disabled\":[false,false]}";
            const string CodexCards = "{\"cards\":[{\"tool\":\"\",\"header\":\"\",\"ext\":\"\",\"fp\":\"0c0dec0d\",\"card\":\"\","
                                      + "\"fresh\":false,\"options\":[\"Deny\",\"Allow once\"],\"disabled\":[false,false]}],\"blind\":0}";
            int settled = CdpApprover.FocusSettleMs + 2 * CdpApprover.FocusPollMs;

            // ---- (a) Claude: the card's synchronous, 50 ms, select() and permission-clear focus are all held -----------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("B", "claude", "sync,timeout,select,effect");
                page.UserIn("A");
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", CdpApprover.ClaudeTargetMarker, "none"),
                                                              FocusTarget(page, "B", CdpApprover.ClaudeTargetMarker, ClaudeCard) }))
                {
                    var budget = new PressBudget();
                    var pass = new SweepPass(server.Port, true, false, false, budget, () => true);
                    string note = SweepOnce(server.Port, pass, budget, 4000, out bool sawPanel, out bool sawBlind);
                    page.Pump();
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) a press in a card the user is not in leaves focus in the user's card: the card's "
                                + "synchronous, 50 ms, select() and 100 ms permission-clear focus were all held, and focus never "
                                + "left (" + FocusBrief(state) + ")",
                        FocusStr(state, null, "focused") == "A" && FocusBool(state, "A", "inputActive") && FocusStr(state, null, "log") == "");
                    probe.Check("(1.12.3) ...and the approval still happened, once, with nothing for the safety net to put back ("
                                + (note ?? "<no note>") + ")",
                        pass.PressedAny && FocusInt(state, "B", "approved") == 1 && note != null
                        && note.IndexOf("auto-approve clicked", StringComparison.Ordinal) >= 0
                        && note.IndexOf("took focus", StringComparison.Ordinal) < 0);
                    page.Advance(settled);
                    string after = page.State();
                    probe.Check("WITNESS (1.12.3) once the card has settled the hold lets go: the exact original focus and select "
                                + "methods, no listener or timer left, the webview shell's realm never touched (" + FocusBrief(after) + ")",
                        FocusBool(after, "B", "exact") && !FocusBool(after, "B", "hold") && FocusInt(after, "B", "listeners") == 0
                        && FocusInt(after, null, "timers") == 0 && !FocusBool(after, "B", "shellTouched"));
                }
            }

            // ---- (a2) Claude's diff path: the card clears ten seconds after the press, still inside the hold ------------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("B", "claude", "diff");
                page.UserIn("A");
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", CdpApprover.ClaudeTargetMarker, "none"),
                                                              FocusTarget(page, "B", CdpApprover.ClaudeTargetMarker, ClaudeCard) }))
                {
                    var budget = new PressBudget();
                    var pass = new SweepPass(server.Port, true, false, false, budget, () => true);
                    SweepOnce(server.Port, pass, budget, 4000, out bool sawPanel, out bool sawBlind);
                    page.Advance(9000);
                    string waiting = page.State();
                    page.Advance(1500);
                    string cleared = page.State();
                    probe.Check("WITNESS (1.12.3) Claude's diff path: the card clears ten seconds after the press and the composer "
                                + "focus 100 ms later is still held; focus never left the user's card (at 9 s " + FocusBrief(waiting)
                                + ", at 10.5 s " + FocusBrief(cleared) + ")",
                        pass.PressedAny && FocusBool(waiting, "B", "hold") && FocusStr(cleared, null, "log") == ""
                        && FocusStr(cleared, null, "focused") == "A" && FocusBool(cleared, "A", "inputActive"));
                    page.Advance(settled);
                    string after = page.State();
                    probe.Check("(1.12.3) ...and it lets go once that card has settled, the originals exact (" + FocusBrief(after) + ")",
                        FocusBool(after, "B", "exact") && !FocusBool(after, "B", "hold"));
                }
            }

            // ---- (b) the user IS in the pressed card: the hold is on, but decides per call --------------------------------
            // A focused call goes through; after the user types and leaves, the card's later background focus does not;
            // once the card has settled nothing is suppressed any more.
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("B", "claude", "sync,late,effect");
                page.UserIn("B");
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", CdpApprover.ClaudeTargetMarker, "none"),
                                                              FocusTarget(page, "B", CdpApprover.ClaudeTargetMarker, ClaudeCard) }))
                {
                    var budget = new PressBudget();
                    var pass = new SweepPass(server.Port, true, false, false, budget, () => true);
                    SweepOnce(server.Port, pass, budget, 4000, out bool sawPanel, out bool sawBlind);
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) a press in the card the user is in still installs the hold, and the card's own "
                                + "focus call made while the card has focus goes through (" + FocusBrief(state) + ")",
                        pass.PressedAny && FocusInt(state, "B", "approved") == 1 && FocusBool(state, "B", "hold")
                        && FocusStr(state, null, "focused") == "B" && FocusBool(state, "B", "inputActive") && FocusStr(state, null, "log") == "B");
                    page.UserType("B");
                    page.UserPointer("A");
                    page.Advance(700);
                    string left = page.State();
                    probe.Check("WITNESS (1.12.3) the user types in the card and then leaves it: the card's focus 600 ms after the "
                                + "press is still suppressed, and the user stays where they went (" + FocusBrief(left) + ")",
                        FocusStr(left, null, "focused") == "A" && FocusStr(left, null, "log") == "B,user:A"
                        && FocusBool(left, "B", "hold") && FocusBool(left, "B", "intent"));
                    page.Advance(settled);
                    page.Run("__page.frames['B'].input.focus(); 'ok'");
                    string released = page.State();
                    probe.Check("(1.12.3) once the card has settled nothing is suppressed any more: a background focus after the "
                                + "settle window goes through (" + FocusBrief(released) + ")",
                        FocusBool(released, "B", "exact") && !FocusBool(released, "B", "hold")
                        && FocusStr(released, null, "log") == "B,user:A,B" && FocusStr(released, null, "focused") == "B");
                }
            }
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("B", "claude", "late,effect");
                page.UserIn("B");
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", CdpApprover.ClaudeTargetMarker, "none"),
                                                              FocusTarget(page, "B", CdpApprover.ClaudeTargetMarker, ClaudeCard) }))
                {
                    var budget = new PressBudget();
                    var pass = new SweepPass(server.Port, true, false, false, budget, () => true);
                    SweepOnce(server.Port, pass, budget, 4000, out bool sawPanel, out bool sawBlind);
                    page.UserKeyTo("B", "A");
                    page.Advance(700);
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) the user leaves the card by keyboard alone: its focus 600 ms after the press is "
                                + "still suppressed (" + FocusBrief(state) + ")",
                        pass.PressedAny && FocusStr(state, null, "focused") == "A" && FocusStr(state, null, "log") == "user:A"
                        && FocusBool(state, "B", "hold"));
                }
            }

            // ---- (c) restored exactly: overlapping presses, the hard cap, a click that throws ---------------------------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("B", "claude", "none");
                page.UserIn(null);
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "B", CdpApprover.ClaudeTargetMarker, ClaudeCard) }))
                {
                    string first = PressFocusCard(server, "B");
                    string during = page.State();
                    probe.Check("WITNESS (1.12.3) a press with the user elsewhere holds the card's focus methods while its card "
                                + "stands (" + first + ", " + FocusBrief(during) + ")",
                        first == "clicked" && FocusBool(during, "B", "patched") && FocusBool(during, "B", "hold"));

                    // The card never settles here, so only the cap ends the hold: the second press's, not the first's.
                    page.Advance(5000);
                    string second = PressFocusCard(server, "B");
                    page.Advance(CdpApprover.FocusHoldCapMs - 5000 + 1000);
                    string extended = page.State();
                    probe.Check("(1.12.3) a second press refreshes the hold: still on past the first press's cap (" + second + ", "
                                + FocusBrief(extended) + ")",
                        second == "clicked" && FocusBool(extended, "B", "patched") && FocusBool(extended, "B", "hold"));
                    page.Advance(5000);
                    string after = page.State();
                    probe.Check("WITNESS (1.12.3) after overlapping presses, at the latest one's hard cap, the card's focus and select "
                                + "methods are the exact originals: no wrapper was ever captured as an original (" + FocusBrief(after) + ")",
                        FocusBool(after, "B", "exact") && !FocusBool(after, "B", "hold") && FocusInt(after, null, "timers") == 0);

                    page.Run("__page.breakClick('B'); 'ok'");
                    string thrown = PressFocusCard(server, "B");
                    string released = page.State();
                    probe.Check("WITNESS (1.12.3) a click that throws releases the hold it made at once, exactly (" + thrown + ", "
                                + FocusBrief(released) + ")",
                        thrown == "gone" && FocusBool(released, "B", "exact") && !FocusBool(released, "B", "hold")
                        && FocusInt(released, "B", "listeners") == 0);
                }
            }

            // ---- (d) the safety net: a focus the hold cannot reach is put back, and only onto the element remembered -----
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("B", "claude", "reveal");
                page.UserIn("A");
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", CdpApprover.ClaudeTargetMarker, "none"),
                                                              FocusTarget(page, "B", CdpApprover.ClaudeTargetMarker, ClaudeCard) }))
                {
                    var budget = new PressBudget();
                    var pass = new SweepPass(server.Port, true, false, false, budget, () => true);
                    string note = SweepOnce(server.Port, pass, budget, 4000, out bool sawPanel, out bool sawBlind);
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) a focus the hold cannot reach (an extension's reveal) is put back on the element "
                                + "the user was in (" + FocusBrief(state) + ")",
                        pass.PressedAny && FocusInt(state, "B", "approved") == 1 && FocusStr(state, null, "log") == "B,A"
                        && FocusStr(state, null, "focused") == "A" && FocusBool(state, "A", "inputActive")
                        && FocusInt(state, "A", "listeners") == 0);
                    probe.Check("(1.12.3) ...and the press's one log line says so (" + (note ?? "<no note>") + ")",
                        note != null && note.IndexOf("took focus from the panel you were in, and it was put back", StringComparison.Ordinal) > 0);
                }
            }
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("B", "claude", "reveal");
                page.UserAtBody("A");
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", CdpApprover.ClaudeTargetMarker, "none"),
                                                              FocusTarget(page, "B", CdpApprover.ClaudeTargetMarker, ClaudeCard) }))
                {
                    var budget = new PressBudget();
                    var pass = new SweepPass(server.Port, true, false, false, budget, () => true);
                    string note = SweepOnce(server.Port, pass, budget, 4000, out bool sawPanel, out bool sawBlind);
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) the net never falls back to a body or root: with no element focused in the user's "
                                + "panel it leaves focus where it went and says it could not put it back (" + FocusBrief(state) + "; "
                                + (note ?? "<no note>") + ")",
                        pass.PressedAny && FocusStr(state, null, "focused") == "B" && FocusStr(state, null, "log") == "B"
                        && note != null && note.IndexOf("could not be put back (lost)", StringComparison.Ordinal) > 0);
                }
            }

            // ---- (e) the user's newer choice: the net never overrides it -----------------------------------------------------
            // Three schedules, each with the user in A when the press begins and B's card not moving focus itself, so focus
            // reaches B only by the user's own choice: (e1) a pointer press in B after the act (the control: the act's
            // listeners see it), (e2) a pointer press in B BEFORE the act, while the press is attaching (Codex's review:
            // no listener exists yet, so only the act's own observation can know), (e3) leaving A for B by keyboard alone,
            // the Ctrl shortcut delivered to A, not to B.
            for (int schedule = 1; schedule <= 3; schedule++)
            {
                using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
                {
                    page.AddFrame("A", "claude", "none");
                    page.AddFrame("B", "claude", "none");
                    page.UserIn("A");
                    bool chosen = false;
                    Action<string> beforeAct = expression =>
                    {
                        if (schedule != 2 || chosen || expression.IndexOf("b.click()", StringComparison.Ordinal) < 0) return;
                        chosen = true;
                        page.UserPointer("B");
                    };
                    using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", CdpApprover.ClaudeTargetMarker, "none"),
                                                                  FocusTarget(page, "B", CdpApprover.ClaudeTargetMarker, ClaudeCard, beforeAct) }))
                    {
                        var budget = new PressBudget();
                        var pass = new SweepPass(server.Port, true, false, false, budget, () => true);
                        string note = null;
                        Task sweep = Task.Run(() => { note = SweepOnce(server.Port, pass, budget, 4000, out bool p, out bool b); });
                        // (e1) and (e3) act after the press and before the net's look.
                        var waited = Stopwatch.StartNew();
                        while (FocusInt(page.State(), "B", "approved") < 1 && waited.ElapsedMilliseconds < 3000) Thread.Sleep(20);
                        if (schedule == 1) page.UserPointer("B");
                        if (schedule == 3) page.UserKeyTo("A", "B");
                        sweep.Wait(10000);
                        // No clock advance: the user's panel's watch must already be gone, taken out by the net's own
                        // decline or restore, not by the watch's timer.
                        string state = page.State();
                        const string expectLog = "user:B";
                        string[] names = { "", "a pointer press in the pressed card after the act",
                                           "a pointer press in the pressed card BEFORE the act, while the press was attaching",
                                           "leaving the user's panel for the pressed card by keyboard alone" };
                        probe.Check("WITNESS (1.12.3) the safety net never overrides the user's newer choice: " + names[schedule]
                                    + " leaves them in the card they chose (" + FocusBrief(state) + "; " + (note ?? "<no note>") + ")",
                            sweep.IsCompleted && pass.PressedAny && FocusStr(state, null, "focused") == "B"
                            && FocusStr(state, null, "log") == expectLog && FocusInt(state, "A", "listeners") == 0
                            && note != null && note.IndexOf("took focus", StringComparison.Ordinal) < 0);
                        if (schedule == 1)
                            probe.Check("(1.12.3) ...and that press marks the user's intent without ending the hold (" + FocusBrief(state) + ")",
                                FocusBool(state, "B", "intent") && FocusBool(state, "B", "hold"));
                    }
                }
            }

            // ---- (h) one press's observation never authorises another's -------------------------------------------------
            // The net's steps compared directly, each a production expression: remember A for press X, act in B for press
            // Y (B's card takes focus through reveal, which no hold reaches). Each step must refuse the other press's id,
            // and accept its own (the controls), so the comparison is what decides.
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("B", "claude", "reveal");
                page.UserIn("A");
                string remembered = page.EvaluateAs("A", CdpApprover.FocusRememberForSelfTest("press-x"));
                string acted = page.EvaluateAs("B", CdpApprover.BuildClaudeClick(0, "Yes", "press-y"));
                string probeX = page.EvaluateAs("B", CdpApprover.FocusActionProbeForSelfTest("press-x"));
                string probeY = page.EvaluateAs("B", CdpApprover.FocusActionProbeForSelfTest("press-y"));
                string restoreY = page.EvaluateAs("A", CdpApprover.FocusRestoreForSelfTest("press-y"));
                string stillB = page.State();
                page.Run("__page.user('A'); 'ok'");
                page.EvaluateAs("A", CdpApprover.FocusRememberForSelfTest("press-y"));
                page.Run("__page.focusTo(__page.frames['B'].inner, __page.frames['B'].input); 'ok'");
                string restoreOwn = page.EvaluateAs("A", CdpApprover.FocusRestoreForSelfTest("press-y"));
                probe.Check("WITNESS (1.12.3) the pressed panel's record answers only for its own press: another press's id is "
                            + "'other', its own is 'stolen' (" + remembered + ", " + acted + ", " + probeX + ", " + probeY + ")",
                    remembered == "focused" && acted == "clicked" && probeX == "other" && probeY == "stolen");
                probe.Check("WITNESS (1.12.3) the user's panel restores only for the press it was remembered for: another press's "
                            + "memo is 'lost' and focus stays put, its own is 'restored' (" + restoreY + ", " + restoreOwn + ", "
                            + FocusBrief(stillB) + ")",
                    restoreY == "lost" && FocusStr(stillB, null, "focused") == "B" && restoreOwn == "restored");
            }

            // ---- (f) Codex: a follow-on card's autofocus on mount is held --------------------------------------------------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("C", "codex", "sync,timeout,nextcard");
                page.UserIn("A");
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", CdpApprover.ClaudeTargetMarker, "none"),
                                                              FocusTarget(page, "C", CdpApprover.CodexTargetMarker, CodexCards) }))
                {
                    var budget = new PressBudget();
                    var pass = new SweepPass(server.Port, true, false, false, budget, () => true);
                    SweepOnce(server.Port, pass, budget, 4000, out bool sawPanel, out bool sawBlind);
                    page.Pump();
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) a Codex press holds the card's synchronous and 50 ms focus and the follow-on "
                                + "card's autofocus on mount; focus never left the user's card (" + FocusBrief(state) + ")",
                        pass.PressedAny && FocusInt(state, "C", "approved") == 1 && FocusStr(state, null, "log") == ""
                        && FocusStr(state, null, "focused") == "A" && FocusBool(state, "C", "hold"));
                    // SelfCheckCodexTransport's rule, by behaviour: the press built and dispatched no key, and what it
                    // installed only listens. A trusted Escape to the panel is recorded as intent and left alone.
                    page.UserEscape("C");
                    string keys = page.State();
                    probe.Check("WITNESS (1.12.3) the Codex clicker builds and dispatches no key, and its keydown observer only "
                                + "listens: a trusted Escape to the panel is recorded as the user's intent, never prevented or "
                                + "stopped (" + FocusBrief(keys) + ")",
                        FocusInt(keys, null, "keysBuilt") == 0 && FocusInt(keys, null, "keysSent") == 0
                        && FocusInt(keys, null, "keyActions") == 0 && FocusBool(keys, "C", "intent"));
                    page.Advance(CdpApprover.FocusHoldCapMs + 1000);
                    string after = page.State();
                    probe.Check("(1.12.3) ...and with the follow-on card left standing, the hard cap still lets go, exactly ("
                                + FocusBrief(after) + ")",
                        FocusBool(after, "C", "exact") && !FocusBool(after, "C", "hold"));
                }
            }

            // ---- (g) the session-limit send holds too ----------------------------------------------------------------------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("S", "chat", "sync,timeout");
                page.UserIn("A");
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", CdpApprover.ClaudeTargetMarker, "none"),
                                                              FocusTarget(page, "S", CdpApprover.ClaudeTargetMarker, "none") }))
                {
                    string result = CdpApprover.SendPause(server.Port, "S", "pause for the limit", 1500, 0);
                    page.Pump();
                    string state = page.State();
                    page.Advance(settled);
                    string after = page.State();
                    probe.Check("WITNESS (1.12.3) the session-limit send holds the panel's focus calls while it types and presses "
                                + "Enter, then lets go exactly (" + result + ", " + FocusBrief(state) + ")",
                        result == "accepted" && FocusStr(state, "S", "sent") == "pause for the limit" && FocusStr(state, null, "log") == ""
                        && FocusStr(state, null, "focused") == "A" && FocusBool(after, "S", "exact") && !FocusBool(after, "S", "hold"));
                }
            }
            return true;
        }

        /// <summary>A target whose reads are canned and whose every other expression runs in <paramref name="page"/>
        /// as frame <paramref name="frame"/>.</summary>
        private static FakeCdpServer.Target FocusTarget(FakeFocusPage page, string frame, string marker, string cannedRead)
        {
            return FocusTarget(page, frame, marker, cannedRead, null);
        }

        /// <summary>As above, with <paramref name="before"/> run on each expression just before the page evaluates it: what
        /// the user does while the press is on its way.</summary>
        private static FakeCdpServer.Target FocusTarget(FakeFocusPage page, string frame, string marker, string cannedRead,
                                                        Action<string> before)
        {
            return new FakeCdpServer.Target
            {
                Id = frame,
                Url = "vscode-webview://" + frame.ToLowerInvariant() + "/index.html?" + marker,
                Evaluator = expression =>
                {
                    if (expression == CdpApprover.ReadExpressionForSelfTest || expression == CdpApprover.CodexReadExpressionForSelfTest)
                        return cannedRead;
                    if (before != null) before(expression);
                    return page.EvaluateAs(frame, expression);
                },
            };
        }

        /// <summary>Press the Claude card's Yes in one frame through the production click, with no panel list (no net),
        /// which returns as soon as the click is answered.</summary>
        private static string PressFocusCard(FakeCdpServer server, string frame)
        {
            string focusNote;
            return CdpApprover.Click(server.Port, frame, 0, "Yes", 1500, CdpApprover.AgentClaude, "", null, out focusNote);
        }

        private static string FocusBrief(string state)
        {
            return state == null ? "<no state>" : (state.Length > 400 ? state.Substring(0, 400) : state);
        }

        private static JsonElement? FocusField(string state, string frame, string name)
        {
            if (string.IsNullOrEmpty(state)) return null;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(state))
                {
                    JsonElement at = document.RootElement;
                    if (frame != null && !(at.TryGetProperty("frames", out at) && at.TryGetProperty(frame, out at))) return null;
                    JsonElement value;
                    return at.TryGetProperty(name, out value) ? value.Clone() : (JsonElement?)null;
                }
            }
            catch (JsonException) { return null; }
        }

        private static string FocusStr(string state, string frame, string name)
        {
            JsonElement? value = FocusField(state, frame, name);
            return value.HasValue && value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
        }

        private static bool FocusBool(string state, string frame, string name)
        {
            JsonElement? value = FocusField(state, frame, name);
            return value.HasValue && value.Value.ValueKind == JsonValueKind.True;
        }

        private static int FocusInt(string state, string frame, string name)
        {
            JsonElement? value = FocusField(state, frame, name);
            int number;
            return value.HasValue && value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt32(out number) ? number : -1;
        }
    }
}
