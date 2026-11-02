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
        /// reads the page's focus log, so focus that left at any moment fails it.
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
                    probe.Check("(1.12.3) ...and the approval still happened, once, with no detection line ("
                                + (note ?? "<no note>") + ")",
                        pass.PressedAny && FocusInt(state, "B", "approved") == 1 && note != null
                        && note.IndexOf("auto-approve clicked", StringComparison.Ordinal) >= 0
                        && note.IndexOf(CdpApprover.FocusMovedNote, StringComparison.Ordinal) < 0);
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
                        && FocusBool(left, "B", "hold"));
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

            // ---- (d) detection: a focus the hold cannot reach is reported, and nothing is moved back ------------------------
            // There is no safety net that refocuses (removed by decision, see FocusHold.cs): B's card takes focus through
            // reveal, which no in-page hold reaches, and the press says so in one line while focus stays where it went.
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
                    probe.Check("WITNESS (1.12.3) a focus the hold cannot reach (an extension's reveal) is reported on the press's "
                                + "one log line, and AgentFlow moves nothing back (" + FocusBrief(state) + "; " + (note ?? "<no note>") + ")",
                        pass.PressedAny && FocusInt(state, "B", "approved") == 1 && FocusStr(state, null, "log") == "B"
                        && FocusStr(state, null, "focused") == "B" && FocusInt(state, "A", "listeners") == 0
                        && note != null && note.IndexOf("; " + CdpApprover.FocusMovedNote, StringComparison.Ordinal) > 0);
                }
            }

            // ---- (e) the user's own choice of the pressed card stays theirs ------------------------------------------------
            // Four schedules, each with the user in A when the press begins and B's card not moving focus itself, so focus
            // reaches B only by the user's own choice: (e1) a pointer press in B after the act, (e2) a pointer press in B
            // BEFORE the act, while the press is attaching, (e3) leaving A for B by keyboard alone, (e4) choosing B through
            // VS Code itself (the host focuses B's frame window, an unheld path, and no input reaches either page). Nothing
            // may move the user back to A in any of them.
            for (int schedule = 1; schedule <= 4; schedule++)
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
                        var waited = Stopwatch.StartNew();
                        while (FocusInt(page.State(), "B", "approved") < 1 && waited.ElapsedMilliseconds < 3000) Thread.Sleep(20);
                        if (schedule == 1) page.UserPointer("B");
                        if (schedule == 3) page.UserKeyTo("A", "B");
                        if (schedule == 4) page.Run("__page.frames['B'].inner.focus(); 'ok'");
                        sweep.Wait(10000);
                        page.Advance(CdpApprover.FocusCheckAfterMs);
                        string state = page.State();
                        string expectLog = schedule == 4 ? "B" : "user:B";
                        string[] names = { "", "a pointer press in the pressed card after the act",
                                           "a pointer press in the pressed card BEFORE the act, while the press was attaching",
                                           "leaving the user's panel for the pressed card by keyboard alone",
                                           "choosing the pressed card through VS Code itself" };
                        probe.Check("WITNESS (1.12.3) the user's own choice of the pressed card stays theirs: " + names[schedule]
                                    + " leaves them there, and nothing moves them back (" + FocusBrief(state) + "; " + (note ?? "<no note>") + ")",
                            sweep.IsCompleted && pass.PressedAny && FocusStr(state, null, "focused") == "B"
                            && FocusStr(state, null, "log") == expectLog);
                        if (schedule == 2)
                            probe.Check("(1.12.3) ...and a press that found the user already in the card writes no detection line ("
                                        + (note ?? "<no note>") + ")",
                                note != null && note.IndexOf(CdpApprover.FocusMovedNote, StringComparison.Ordinal) < 0);
                    }
                }
            }

            // ---- (e5) the user's entry through a dropdown that prevents native focus is let through -------------------------
            // Codex's recheck: the installed Codex "Approval options" trigger prevents the native pointer and mouse focus, and
            // its menu focuses itself when it mounts. With B held and A focused, the user's trusted press opens the gesture
            // allowance, so the menu's own focus 50 ms later lands in B; a background focus after the allowance is held again.
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("C", "codex", "none");
                page.UserIn(null);
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "C", CdpApprover.CodexTargetMarker, CodexCards) }))
                {
                    string pressed = CdpApprover.Click(server.Port, "C", 1, "Allow once", 1500, CdpApprover.AgentCodex, "", null,
                                                       out string ignoredNote);
                    page.UserIn("A");
                    page.UserPressNoFocus("C");
                    page.Advance(100);
                    string entered = page.State();
                    probe.Check("WITNESS (1.12.3) with the card held, the user's own press on a dropdown that prevents native focus "
                                + "still enters the card: the menu's focus 50 ms later goes through (" + pressed + ", "
                                + FocusBrief(entered) + ")",
                        pressed == "clicked" && FocusBool(entered, "C", "hold") && FocusStr(entered, null, "focused") == "C"
                        && FocusStr(entered, null, "log") == "C");
                    page.UserPointer("A");
                    page.Advance(CdpApprover.FocusGestureMs);
                    page.Run("__page.frames['C'].input.focus(); 'ok'");
                    string later = page.State();
                    probe.Check("WITNESS (1.12.3) ...and once the gesture allowance has passed, a background focus in that card is "
                                + "held again: the hold did not end (" + FocusBrief(later) + ")",
                        FocusBool(later, "C", "hold") && !FocusBool(later, "C", "allowing")
                        && FocusStr(later, null, "focused") == "A" && FocusStr(later, null, "log") == "C,user:A");
                }
            }

            // ---- (h) detection answers only for its own press ------------------------------------------------------------------
            // The production expressions directly: act in B for press Y (B's card takes focus through reveal), then ask for
            // press X and for press Y. Another press's id must not read as 'moved'; its own must (the control).
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("B", "claude", "reveal");
                page.UserIn(null);
                string acted = page.EvaluateAs("B", CdpApprover.BuildClaudeClick(0, "Yes", "press-y"));
                string probeX = page.EvaluateAs("B", CdpApprover.FocusMovedForSelfTest("press-x"));
                string probeY = page.EvaluateAs("B", CdpApprover.FocusMovedForSelfTest("press-y"));
                probe.Check("WITNESS (1.12.3) the pressed panel's record answers only for its own press: another press's id is "
                            + "'other', its own is 'moved' (" + acted + ", " + probeX + ", " + probeY + ")",
                    acted == "clicked" && probeX == "other" && probeY == "moved");
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
                    // installed only listens. The user's trusted press opens the gesture allowance and is left alone; a
                    // trusted Escape is not touched either.
                    page.UserPressNoFocus("C");
                    page.UserEscape("C");
                    string keys = page.State();
                    probe.Check("WITNESS (1.12.3) the Codex clicker builds and dispatches no key, and its input observer only "
                                + "listens: the user's trusted press is observed (the gesture allowance opens) and neither it nor a "
                                + "trusted Escape is prevented or "
                                + "stopped (" + FocusBrief(keys) + ")",
                        FocusInt(keys, null, "keysBuilt") == 0 && FocusInt(keys, null, "keysSent") == 0
                        && FocusInt(keys, null, "keyActions") == 0 && FocusBool(keys, "C", "allowing"));
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
