using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.ModuleKit;

namespace DesktopAICompanion.AgentFlow
{
    public sealed partial class AgentFlowModule
    {
        /// <summary>
        /// 1.12.3, the owner's report of 2026-11-02 and the live failure of the press-time hold the same day: an agent card
        /// must not take focus from the card the user is working in, whether it is pressed or not. The guard install and
        /// removal, the press and the detection run for real, in chakra.dll against <see cref="FakeFocusPage"/>'s DOM
        /// model, behind FakeCdpServer; reads stay canned. Sweeps go through SweepOnce, the pairing OnTick runs, with
        /// GuardFocus set as the tick sets it. The focus paths are the ones Codex's audit traced: a Codex approval card's
        /// autofocus on mount, Claude Code's permission-clear refocus (100 ms after the card clears, ten seconds later on its
        /// diff path). Every "never left" check reads the page's focus log, so focus that left at any moment fails it.
        /// </summary>
        private static bool SelfCheckFocusHold(SelfTestProbe probe)
        {
            string why;
            using (FakeFocusPage probePage = FakeFocusPage.TryCreate(out why))
            {
                if (probePage == null)
                {
                    probe.Skip("(1.12.3) the focus checks run the guard and press expressions in Windows' chakra.dll JsRT, and "
                               + "it is not usable here: " + why);
                    return true;
                }
            }
            const string ClaudeCard = "{\"tool\":\"Bash\",\"header\":\"ls\",\"ext\":\"\",\"fp\":\"0badf00d\",\"card\":\"b0000001\","
                                      + "\"fresh\":false,\"options\":[\"Yes\",\"No\"],\"disabled\":[false,false]}";
            const string CodexCards = "{\"cards\":[{\"tool\":\"\",\"header\":\"\",\"ext\":\"\",\"fp\":\"0c0dec0d\",\"card\":\"\","
                                      + "\"fresh\":false,\"options\":[\"Deny\",\"Allow once\"],\"disabled\":[false,false]}],\"blind\":0}";
            string claudeMarker = CdpApprover.ClaudeTargetMarker, codexMarker = CdpApprover.CodexTargetMarker;
            SweepPass pass;

            // ---- (s1) a Codex approval card mounting on its own, no press: the owner's live failure -------------------------
            foreach (bool guard in new[] { true, false })
            {
                using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
                {
                    page.AddFrame("A", "claude", "none");
                    page.AddFrame("C", "codex", "none");
                    page.UserIn("A");
                    using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", claudeMarker, "none"),
                                                                  FocusTarget(page, "C", codexMarker, "none") }))
                    {
                        FocusSweep(server, true, guard, out pass);
                        page.MountCard("C");
                        string state = page.State();
                        if (guard)
                            probe.Check("WITNESS (1.12.3) with the guard on, a Codex approval card that mounts on its own and "
                                        + "autofocuses its button, with no press anywhere, leaves focus in the user's card ("
                                        + FocusBrief(state) + ")",
                                FocusStr(state, null, "focused") == "A" && FocusBool(state, "A", "inputActive")
                                && FocusStr(state, null, "log") == "" && FocusBool(state, "C", "hold"));
                        else
                            probe.Check("(1.12.3) ...and with it off, as in 1.12.2, the same mount takes focus: the case is real ("
                                        + FocusBrief(state) + ")",
                                FocusStr(state, null, "focused") == "C" && FocusStr(state, null, "log") == "C"
                                && !FocusBool(state, "C", "hold") && !FocusBool(state, "C", "patched"));
                    }
                }
            }

            // ---- (s2) Claude Code's card clearing on its own, no press: its 100 ms refocus is held ---------------------------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("B", "claude", "none");
                page.UserIn("A");
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", claudeMarker, "none"),
                                                              FocusTarget(page, "B", claudeMarker, "none") }))
                {
                    FocusSweep(server, true, true, out pass);
                    page.ClearCard("B");
                    page.Advance(300);
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) Claude Code's composer refocus 100 ms after a card clears on its own, no press, "
                                + "is held (" + FocusBrief(state) + ")",
                        FocusStr(state, null, "focused") == "A" && FocusStr(state, null, "log") == "" && FocusBool(state, "B", "hold"));
                }
            }

            // ---- (a) a press: synchronous, 50 ms, select() and the permission-clear refocus all held; the guard stays ---------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("B", "claude", "sync,timeout,select,effect");
                page.UserIn("A");
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", claudeMarker, "none"),
                                                              FocusTarget(page, "B", claudeMarker, ClaudeCard) }))
                {
                    string note = FocusSweep(server, true, true, out pass);
                    page.Pump();
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) a press in a card the user is not in leaves focus in the user's card: the card's "
                                + "synchronous, 50 ms, select() and 100 ms permission-clear focus were all held, and focus never "
                                + "left (" + FocusBrief(state) + ")",
                        FocusStr(state, null, "focused") == "A" && FocusBool(state, "A", "inputActive") && FocusStr(state, null, "log") == "");
                    probe.Check("(1.12.3) ...and the approval still happened, once, with no detection line (" + (note ?? "<no note>") + ")",
                        pass.PressedAny && FocusInt(state, "B", "approved") == 1 && note != null
                        && note.IndexOf("auto-approve clicked", StringComparison.Ordinal) >= 0
                        && note.IndexOf(CdpApprover.FocusMovedNote, StringComparison.Ordinal) < 0);
                    page.Advance(5000);
                    string after = page.State();
                    probe.Check("WITNESS (1.12.3) the guard stands after the press and after its card has gone: no settle, no cap, "
                                + "and the webview shell's realm is never touched (" + FocusBrief(after) + ")",
                        FocusBool(after, "B", "hold") && FocusBool(after, "B", "patched") && FocusBool(after, "A", "hold")
                        && !FocusBool(after, "B", "shellGuard") && !FocusBool(after, "B", "shellTouched"));
                }
            }

            // ---- (a2) Claude's diff path: the card clears ten seconds after the press ------------------------------------------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("B", "claude", "diff");
                page.UserIn("A");
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", claudeMarker, "none"),
                                                              FocusTarget(page, "B", claudeMarker, ClaudeCard) }))
                {
                    FocusSweep(server, true, true, out pass);
                    page.Advance(10500);
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) Claude's diff path: the card clears ten seconds after the press and the composer "
                                + "focus 100 ms later is held; focus never left the user's card (" + FocusBrief(state) + ")",
                        pass.PressedAny && FocusStr(state, null, "log") == "" && FocusStr(state, null, "focused") == "A");
                }
            }

            // ---- (b) the user IS in the card: their own focus works, and leaving keeps the card's later focus held ---------------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("B", "claude", "sync,late,effect");
                page.UserIn("B");
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", claudeMarker, "none"),
                                                              FocusTarget(page, "B", claudeMarker, ClaudeCard) }))
                {
                    FocusSweep(server, true, true, out pass);
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) in the card the user is in, the guard is on and the card's own focus call made "
                                + "while the card has focus goes through (" + FocusBrief(state) + ")",
                        pass.PressedAny && FocusBool(state, "B", "hold") && FocusStr(state, null, "focused") == "B"
                        && FocusBool(state, "B", "inputActive") && FocusStr(state, null, "log") == "B");
                    page.UserType("B");
                    page.UserPointer("A");
                    page.Advance(700);
                    string left = page.State();
                    probe.Check("WITNESS (1.12.3) the user types in the card and then leaves it: the card's focus 600 ms after the "
                                + "press is held, and the user stays where they went (" + FocusBrief(left) + ")",
                        FocusStr(left, null, "focused") == "A" && FocusStr(left, null, "log") == "B,user:A");
                }
            }
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("B", "claude", "late,effect");
                page.UserIn("B");
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", claudeMarker, "none"),
                                                              FocusTarget(page, "B", claudeMarker, ClaudeCard) }))
                {
                    FocusSweep(server, true, true, out pass);
                    page.UserKeyTo("B", "A");
                    page.Advance(700);
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) the user leaves the card by keyboard alone: its focus 600 ms after the press is "
                                + "held (" + FocusBrief(state) + ")",
                        pass.PressedAny && FocusStr(state, null, "focused") == "A" && FocusStr(state, null, "log") == "user:A");
                }
            }

            // ---- (s4) the user switches to a guarded card through VS Code: the card's calls then go through ---------------------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("B", "claude", "none");
                page.UserIn("A");
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", claudeMarker, "none"),
                                                              FocusTarget(page, "B", claudeMarker, "none") }))
                {
                    FocusSweep(server, true, true, out pass);
                    page.Run("__page.frames['B'].inner.focus(); __page.frames['B'].input.focus(); 'ok'");
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) switching to a guarded card through VS Code (the host focuses its frame, an "
                                + "unwrapped path) lets the card's own focus call through afterwards (" + FocusBrief(state) + ")",
                        FocusBool(state, "B", "hold") && FocusStr(state, null, "focused") == "B" && FocusBool(state, "B", "inputActive")
                        && FocusStr(state, null, "log") == "B,B");
                }
            }

            // ---- (c) idempotent across sweeps, and removal restores every realm exactly --------------------------------------------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("B", "claude", "none");
                page.UserIn(null);
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", claudeMarker, "none"),
                                                              FocusTarget(page, "B", claudeMarker, "none") }))
                {
                    FocusSweep(server, true, true, out pass);
                    page.Run("__page.firstWrapper = __page.frames['B'].inner.HTMLElement.prototype.focus; 'ok'");
                    FocusSweep(server, true, true, out pass);
                    FocusSweep(server, true, true, out pass);
                    string same = page.Run("String(__page.firstWrapper === __page.frames['B'].inner.HTMLElement.prototype.focus)");
                    string installed = page.State();
                    int removed = CdpApprover.RemoveFocusGuard(server.Port, 1500);
                    string after = page.State();
                    probe.Check("WITNESS (1.12.3) a sweep's install is a no-op where the guard stands: three sweeps, one wrapper ("
                                + same + ", " + FocusBrief(installed) + ")",
                        same == "true" && FocusBool(installed, "A", "hold") && FocusBool(installed, "B", "hold"));
                    probe.Check("WITNESS (1.12.3) removing the guard puts the exact original focus and select methods back in every "
                                + "panel's realm, with no listener left (" + removed.ToString(CultureInfo.InvariantCulture) + " removed, "
                                + FocusBrief(after) + ")",
                        removed == 2 && FocusBool(after, "A", "exact") && FocusBool(after, "B", "exact")
                        && !FocusBool(after, "A", "hold") && !FocusBool(after, "B", "hold")
                        && FocusInt(after, "A", "listeners") == 0 && FocusInt(after, "B", "listeners") == 0);
                }
            }

            // ---- (r) a reloaded page lost its wrappers: the next sweep puts them back ----------------------------------------------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("B", "claude", "none");
                page.UserIn("A");
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", claudeMarker, "none"),
                                                              FocusTarget(page, "B", claudeMarker, "none") }))
                {
                    FocusSweep(server, true, true, out pass);
                    page.ReloadFrame("B");
                    string reloaded = page.State();
                    FocusSweep(server, true, true, out pass);
                    page.ClearCard("B");
                    page.Advance(300);
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) a page reloaded without its guard gets it back at the next sweep, and its refocus "
                                + "is held again (" + FocusBrief(reloaded) + " then " + FocusBrief(state) + ")",
                        !FocusBool(reloaded, "B", "hold") && FocusBool(state, "B", "hold")
                        && FocusStr(state, null, "focused") == "A" && FocusStr(state, null, "log") == "");
                }
            }

            // ---- (d) detection: a focus no in-page guard reaches is reported, and nothing is moved back -----------------------------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("B", "claude", "reveal");
                page.UserIn("A");
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", claudeMarker, "none"),
                                                              FocusTarget(page, "B", claudeMarker, ClaudeCard) }))
                {
                    string note = FocusSweep(server, true, true, out pass);
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) a focus no in-page guard reaches (an extension's reveal) is reported on the press's "
                                + "one log line, and AgentFlow moves nothing back (" + FocusBrief(state) + "; " + (note ?? "<no note>") + ")",
                        pass.PressedAny && FocusInt(state, "B", "approved") == 1 && FocusStr(state, null, "log") == "B"
                        && FocusStr(state, null, "focused") == "B"
                        && note != null && note.IndexOf("; " + CdpApprover.FocusMovedNote, StringComparison.Ordinal) > 0);
                }
            }

            // ---- (e) the user's own choice of the pressed card stays theirs --------------------------------------------------------
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
                    using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", claudeMarker, "none"),
                                                                  FocusTarget(page, "B", claudeMarker, ClaudeCard, beforeAct) }))
                    {
                        string note = null;
                        SweepPass sweepPass = null;
                        Task sweep = Task.Run(() => { note = FocusSweep(server, true, true, out sweepPass); });
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
                            sweep.IsCompleted && sweepPass != null && sweepPass.PressedAny && FocusStr(state, null, "focused") == "B"
                            && FocusStr(state, null, "log") == expectLog);
                        if (schedule == 2)
                            probe.Check("(1.12.3) ...and a press that found the user already in the card writes no detection line ("
                                        + (note ?? "<no note>") + ")",
                                note != null && note.IndexOf(CdpApprover.FocusMovedNote, StringComparison.Ordinal) < 0);
                    }
                }
            }

            // ---- (e5) the user's entry through a dropdown that prevents native focus is let through -----------------------------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("C", "codex", "none");
                page.UserIn("A");
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", claudeMarker, "none"),
                                                              FocusTarget(page, "C", codexMarker, "none") }))
                {
                    FocusSweep(server, true, true, out pass);
                    page.UserPressNoFocus("C");
                    page.Advance(100);
                    string entered = page.State();
                    probe.Check("WITNESS (1.12.3) with the card guarded, the user's own press on a dropdown that prevents native "
                                + "focus still enters the card: the menu's focus 50 ms later goes through (" + FocusBrief(entered) + ")",
                        FocusBool(entered, "C", "hold") && FocusStr(entered, null, "focused") == "C" && FocusStr(entered, null, "log") == "C");
                    page.UserPointer("A");
                    page.Advance(CdpApprover.FocusGestureMs);
                    page.Run("__page.frames['C'].input.focus(); 'ok'");
                    string later = page.State();
                    probe.Check("WITNESS (1.12.3) ...and once the gesture allowance has passed, a background focus in that card is "
                                + "held again (" + FocusBrief(later) + ")",
                        FocusBool(later, "C", "hold") && !FocusBool(later, "C", "allowing")
                        && FocusStr(later, null, "focused") == "A" && FocusStr(later, null, "log") == "C,user:A");
                }
            }

            // ---- (h) detection answers only for its own press ------------------------------------------------------------------------
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

            // ---- (f) a Codex press, its follow-on card's autofocus, and the Codex transport rule by behaviour ----------------------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("C", "codex", "sync,timeout,nextcard");
                page.UserIn("A");
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", claudeMarker, "none"),
                                                              FocusTarget(page, "C", codexMarker, CodexCards) }))
                {
                    FocusSweep(server, true, true, out pass);
                    page.Pump();
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) a Codex press holds the card's synchronous and 50 ms focus and the follow-on "
                                + "card's autofocus on mount; focus never left the user's card (" + FocusBrief(state) + ")",
                        pass.PressedAny && FocusInt(state, "C", "approved") == 1 && FocusStr(state, null, "log") == ""
                        && FocusStr(state, null, "focused") == "A" && FocusBool(state, "C", "hold"));
                    page.UserPressNoFocus("C");
                    page.UserEscape("C");
                    string keys = page.State();
                    probe.Check("WITNESS (1.12.3) the Codex clicker builds and dispatches no key, and the guard's input observer only "
                                + "listens: the user's trusted press is observed (the gesture allowance opens) and neither it nor a "
                                + "trusted Escape is prevented or stopped (" + FocusBrief(keys) + ")",
                        FocusInt(keys, null, "keysBuilt") == 0 && FocusInt(keys, null, "keysSent") == 0
                        && FocusInt(keys, null, "keyActions") == 0 && FocusBool(keys, "C", "allowing"));
                }
            }

            // ---- (g) the session-limit send into a guarded panel ------------------------------------------------------------------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("S", "chat", "sync,timeout");
                page.UserIn("A");
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", claudeMarker, "none"),
                                                              FocusTarget(page, "S", claudeMarker, "none") }))
                {
                    FocusSweep(server, true, true, out pass);
                    string result = CdpApprover.SendPause(server.Port, "S", "pause for the limit", 1500, 0);
                    page.Advance(200);
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) the session-limit send into a guarded panel: the panel's focus calls while it "
                                + "types and presses Enter are held (" + result + ", " + FocusBrief(state) + ")",
                        result == "accepted" && FocusStr(state, "S", "sent") == "pause for the limit" && FocusStr(state, null, "log") == ""
                        && FocusStr(state, null, "focused") == "A");
                }
            }

            // ---- (m) the module: the setting, the off path, Shutdown ---------------------------------------------------------------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            using (var storage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow-focus"))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("B", "claude", "none");
                page.UserIn(null);
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", claudeMarker, "none"),
                                                              FocusTarget(page, "B", claudeMarker, "none") }))
                {
                    var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    AgentFlowModule module = SpeakingModule(host, storage, AgentMode.AutoApprove);
                    module._settings.Set(SettingCdpPort, server.Port.ToString(CultureInfo.InvariantCulture));
                    bool wantedByDefault = module.FocusGuardWanted;
                    module._settings.Set(SettingFocusGuard, "false");
                    bool wantedWhenOff = module.FocusGuardWanted;
                    probe.Check("WITNESS (1.12.3) the guard is wanted by default while approving, and not once the setting is "
                                + "off (" + wantedByDefault + ", " + wantedWhenOff + ")",
                        wantedByDefault && !wantedWhenOff);

                    // The off path, as the tick runs it: installed by a guarded sweep, then the setting turned off.
                    module._settings.Set(SettingFocusGuard, "true");
                    FocusSweep(server, true, module.FocusGuardWanted, out pass);
                    module._focusGuardOn = true;
                    string installed = page.State();
                    module._settings.Set(SettingFocusGuard, "false");
                    int removed = module.SyncFocusGuard(module.CdpPort, module.FocusGuardWanted);
                    string off = page.State();
                    probe.Check("WITNESS (1.12.3) turning the setting off takes the guard out of every panel, exactly ("
                                + removed.ToString(CultureInfo.InvariantCulture) + " removed, " + FocusBrief(off) + ")",
                        FocusBool(installed, "A", "hold") && FocusBool(installed, "B", "hold") && removed == 2
                        && FocusBool(off, "A", "exact") && FocusBool(off, "B", "exact"));
                    FocusSweep(server, true, module.FocusGuardWanted, out pass);
                    string offSweep = page.State();
                    probe.Check("(1.12.3) ...and with it off a sweep installs nothing, as in 1.12.2 (" + FocusBrief(offSweep) + ")",
                        !FocusBool(offSweep, "A", "hold") && !FocusBool(offSweep, "B", "patched"));

                    // Shutdown, with the guard standing.
                    module._settings.Set(SettingFocusGuard, "true");
                    FocusSweep(server, true, module.FocusGuardWanted, out pass);
                    module._focusGuardOn = true;
                    module.Shutdown();
                    string shut = page.State();
                    probe.Check("WITNESS (1.12.3) Shutdown takes the standing guard out of every panel, exactly (" + FocusBrief(shut) + ")",
                        FocusBool(shut, "A", "exact") && FocusBool(shut, "B", "exact")
                        && !FocusBool(shut, "A", "hold") && !FocusBool(shut, "B", "hold")
                        && FocusInt(shut, "A", "listeners") == 0 && FocusInt(shut, "B", "listeners") == 0);
                }
            }
            return true;
        }

        /// <summary>One sweep as the tick runs it, with GuardFocus set to <paramref name="guard"/>.</summary>
        private static string FocusSweep(FakeCdpServer server, bool mayPress, bool guard, out SweepPass pass)
        {
            var budget = new PressBudget();
            pass = new SweepPass(server.Port, mayPress, false, false, budget, () => true) { GuardFocus = guard };
            bool sawPanel, sawBlind;
            return SweepOnce(server.Port, pass, budget, 4000, out sawPanel, out sawBlind);
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
