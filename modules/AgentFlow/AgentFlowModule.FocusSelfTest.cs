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

            // ---- (b) the user IS in the pressed card: nothing is held, its own focus call works --------------------------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("B", "claude", "sync");
                page.UserIn("B");
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", CdpApprover.ClaudeTargetMarker, "none"),
                                                              FocusTarget(page, "B", CdpApprover.ClaudeTargetMarker, ClaudeCard) }))
                {
                    var budget = new PressBudget();
                    var pass = new SweepPass(server.Port, true, false, false, budget, () => true);
                    SweepOnce(server.Port, pass, budget, 4000, out bool sawPanel, out bool sawBlind);
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) a press in the card the user is in holds nothing: no wrapper, no listener, no "
                                + "timer, and the card's own focus call landed (" + FocusBrief(state) + ")",
                        pass.PressedAny && FocusInt(state, "B", "approved") == 1 && !FocusBool(state, "B", "patched")
                        && !FocusBool(state, "B", "hold") && FocusInt(state, "B", "listeners") == 0 && FocusInt(state, null, "timers") == 0
                        && FocusStr(state, null, "focused") == "B" && FocusBool(state, "B", "inputActive") && FocusStr(state, null, "log") == "B");
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
                        && FocusStr(state, null, "focused") == "A" && FocusBool(state, "A", "inputActive"));
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

            // ---- (e) the user's intent: their own press in the held card ends the hold, and the net leaves them there ----
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("B", "claude", "none");
                page.UserIn("A");
                using (var server = new FakeCdpServer(new[] { FocusTarget(page, "A", CdpApprover.ClaudeTargetMarker, "none"),
                                                              FocusTarget(page, "B", CdpApprover.ClaudeTargetMarker, ClaudeCard) }))
                {
                    var budget = new PressBudget();
                    var pass = new SweepPass(server.Port, true, false, false, budget, () => true);
                    string note = null;
                    Task sweep = Task.Run(() => { note = SweepOnce(server.Port, pass, budget, 4000, out bool p, out bool b); });
                    // The user clicks into the pressed card while the net is still waiting: after the press, before its look.
                    var waited = Stopwatch.StartNew();
                    while (FocusInt(page.State(), "B", "approved") < 1 && waited.ElapsedMilliseconds < 3000) Thread.Sleep(20);
                    page.UserPointer("B");
                    string chose = page.State();
                    sweep.Wait(10000);
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) the user's own pointer press in the held card releases the hold at once: the exact "
                                + "originals, no listener left (" + FocusBrief(chose) + ")",
                        FocusInt(chose, "B", "approved") == 1 && FocusBool(chose, "B", "exact") && !FocusBool(chose, "B", "hold")
                        && FocusInt(chose, "B", "listeners") == 0);
                    probe.Check("WITNESS (1.12.3) ...and the safety net leaves the user in the card they chose (" + FocusBrief(state)
                                + "; " + (note ?? "<no note>") + ")",
                        sweep.IsCompleted && FocusStr(state, null, "focused") == "B" && FocusStr(state, null, "log") == "user:B"
                        && note != null && note.IndexOf("took focus", StringComparison.Ordinal) < 0);
                }
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
            return new FakeCdpServer.Target
            {
                Id = frame,
                Url = "vscode-webview://" + frame.ToLowerInvariant() + "/index.html?" + marker,
                Evaluator = expression =>
                    expression == CdpApprover.ReadExpressionForSelfTest || expression == CdpApprover.CodexReadExpressionForSelfTest
                        ? cannedRead
                        : page.EvaluateAs(frame, expression),
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
