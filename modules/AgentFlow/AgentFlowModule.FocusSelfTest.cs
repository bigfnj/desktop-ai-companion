using System;
using System.Text.Json;
using System.Threading;
using DesktopAICompanion.ModuleKit;

namespace DesktopAICompanion.AgentFlow
{
    public sealed partial class AgentFlowModule
    {
        /// <summary>
        /// 1.12.3, the owner's report of 2026-11-02: a press must not take focus from the card the user is working in.
        /// The press, probe and restore expressions run for real, in chakra.dll against <see cref="FakeFocusPage"/>'s DOM
        /// model, behind FakeCdpServer; reads stay canned. Sweeps go through SweepOnce, the pairing OnTick runs, so the
        /// panel list reaches the press the way production hands it on.
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
            int afterWindow = CdpApprover.FocusHoldMs + 150;
            bool sawPanel, sawBlind;

            // ---- (a) the steal is held: synchronous AND in a timeout, the user stays in their own card ------------------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("B", "claude", "sync,timeout");
                page.UserIn("A");
                var a = FocusTarget(page, "A", CdpApprover.ClaudeTargetMarker, "none");
                var b = FocusTarget(page, "B", CdpApprover.ClaudeTargetMarker, ClaudeCard);
                using (var server = new FakeCdpServer(new[] { a, b }))
                {
                    var budget = new PressBudget();
                    var pass = new SweepPass(server.Port, true, false, false, budget, () => true);
                    string note = SweepOnce(server.Port, pass, budget, 4000, out sawPanel, out sawBlind);
                    page.Pump();
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) a press in a card the user is not in leaves focus in the user's card: the card's "
                                + "own synchronous and 50 ms focus calls were held (" + FocusBrief(state) + ")",
                        FocusStr(state, null, "focused") == "A" && FocusBool(state, "A", "inputActive") && FocusStr(state, null, "log") == "");
                    probe.Check("(1.12.3) ...and the approval still happened, once",
                        pass.PressedAny && b.Clicks == 1 && FocusInt(state, "B", "approved") == 1);
                    probe.Check("(1.12.3) ...and the safety net had nothing to put back (" + (note ?? "<no note>") + ")",
                        note != null && note.IndexOf("auto-approve clicked", StringComparison.Ordinal) >= 0
                        && note.IndexOf("took focus", StringComparison.Ordinal) < 0);
                    probe.Check("WITNESS (1.12.3) after the hold's window the card's focus methods are the exact originals again, "
                                + "and the webview shell's realm was never touched",
                        FocusBool(state, "B", "exact") && !FocusBool(state, "B", "hold") && !FocusBool(state, "B", "shellTouched"));
                }
            }

            // ---- (b) the user IS in the pressed card: nothing is held, its own focus call works --------------------------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("B", "claude", "sync");
                page.UserIn("B");
                var a = FocusTarget(page, "A", CdpApprover.ClaudeTargetMarker, "none");
                var b = FocusTarget(page, "B", CdpApprover.ClaudeTargetMarker, ClaudeCard);
                using (var server = new FakeCdpServer(new[] { a, b }))
                {
                    var budget = new PressBudget();
                    var pass = new SweepPass(server.Port, true, false, false, budget, () => true);
                    SweepOnce(server.Port, pass, budget, 4000, out sawPanel, out sawBlind);
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) a press in the card the user is in holds nothing: no wrapper, no timer, and the "
                                + "card's own focus call landed (" + FocusBrief(state) + ")",
                        pass.PressedAny && FocusInt(state, "B", "approved") == 1 && !FocusBool(state, "B", "patched")
                        && !FocusBool(state, "B", "hold") && FocusInt(state, null, "timers") == 0
                        && FocusStr(state, null, "focused") == "B" && FocusBool(state, "B", "inputActive") && FocusStr(state, null, "log") == "B");
                }
            }

            // ---- (c) restored exactly: after the window, when the click throws, and across an overlapping press ---------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("B", "claude", "none");
                page.UserIn(null);
                var b = FocusTarget(page, "B", CdpApprover.ClaudeTargetMarker, ClaudeCard);
                using (var server = new FakeCdpServer(new[] { b }))
                {
                    string first = PressFocusCard(server, "B");
                    string during = page.State();
                    probe.Check("WITNESS (1.12.3) a press with the user elsewhere holds the card's focus methods for the window ("
                                + first + ", " + FocusBrief(during) + ")",
                        first == "clicked" && FocusBool(during, "B", "patched") && FocusBool(during, "B", "hold"));

                    // The overlap: a second press inside the first one's window extends it and captures nothing.
                    Thread.Sleep(400);
                    string second = PressFocusCard(server, "B");
                    Thread.Sleep(afterWindow - 400);
                    page.Pump();
                    string extended = page.State();
                    probe.Check("(1.12.3) a second press inside the window extends the hold past the first press's window ("
                                + second + ", " + FocusBrief(extended) + ")",
                        second == "clicked" && FocusBool(extended, "B", "patched") && FocusBool(extended, "B", "hold"));
                    Thread.Sleep(600);
                    page.Pump();
                    string after = page.State();
                    probe.Check("WITNESS (1.12.3) after overlapping presses the card's focus methods are the exact originals: the "
                                + "second press never captured the first one's wrapper (" + FocusBrief(after) + ")",
                        FocusBool(after, "B", "exact") && !FocusBool(after, "B", "hold") && FocusInt(after, null, "timers") == 0);

                    // The click throws after the hold went on.
                    page.Run("__page.breakClick('B'); 'ok'");
                    string thrown = PressFocusCard(server, "B");
                    string held = page.State();
                    Thread.Sleep(afterWindow);
                    page.Pump();
                    string released = page.State();
                    probe.Check("WITNESS (1.12.3) a click that throws still releases the hold exactly after its window ("
                                + thrown + ", during " + FocusBrief(held) + ", after " + FocusBrief(released) + ")",
                        thrown == "gone" && FocusBool(held, "B", "patched") && FocusBool(released, "B", "exact") && !FocusBool(released, "B", "hold"));
                }
            }

            // ---- (d) the safety net: a focus the hold cannot reach is put back -------------------------------------------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("B", "claude", "reveal");
                page.UserIn("A");
                var a = FocusTarget(page, "A", CdpApprover.ClaudeTargetMarker, "none");
                var b = FocusTarget(page, "B", CdpApprover.ClaudeTargetMarker, ClaudeCard);
                using (var server = new FakeCdpServer(new[] { a, b }))
                {
                    var budget = new PressBudget();
                    var pass = new SweepPass(server.Port, true, false, false, budget, () => true);
                    string note = SweepOnce(server.Port, pass, budget, 4000, out sawPanel, out sawBlind);
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) a focus the hold cannot reach (an extension's reveal) is put back on the element "
                                + "the user was in (" + FocusBrief(state) + ")",
                        pass.PressedAny && FocusInt(state, "B", "approved") == 1 && FocusStr(state, null, "log") == "B,A"
                        && FocusStr(state, null, "focused") == "A" && FocusBool(state, "A", "inputActive"));
                    probe.Check("(1.12.3) ...and the press's one log line says so (" + (note ?? "<no note>") + ")",
                        note != null && note.IndexOf("took focus from the panel you were in, and it was put back", StringComparison.Ordinal) > 0);
                }
            }

            // ---- the Codex click and the session-limit send hold too ------------------------------------------------------
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("C", "codex", "sync,timeout");
                page.UserIn("A");
                var a = FocusTarget(page, "A", CdpApprover.ClaudeTargetMarker, "none");
                var c = FocusTarget(page, "C", CdpApprover.CodexTargetMarker, CodexCards);
                using (var server = new FakeCdpServer(new[] { a, c }))
                {
                    var budget = new PressBudget();
                    var pass = new SweepPass(server.Port, true, false, false, budget, () => true);
                    SweepOnce(server.Port, pass, budget, 4000, out sawPanel, out sawBlind);
                    page.Pump();
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) a Codex press holds the card's synchronous and 50 ms focus calls too ("
                                + FocusBrief(state) + ")",
                        pass.PressedAny && FocusInt(state, "C", "approved") == 1 && FocusStr(state, null, "log") == ""
                        && FocusStr(state, null, "focused") == "A" && FocusBool(state, "C", "exact"));
                }
            }
            using (FakeFocusPage page = FakeFocusPage.TryCreate(out why))
            {
                page.AddFrame("A", "claude", "none");
                page.AddFrame("S", "chat", "sync,timeout");
                page.UserIn("A");
                var a = FocusTarget(page, "A", CdpApprover.ClaudeTargetMarker, "none");
                var s = FocusTarget(page, "S", CdpApprover.ClaudeTargetMarker, "none");
                using (var server = new FakeCdpServer(new[] { a, s }))
                {
                    string result = CdpApprover.SendPause(server.Port, "S", "pause for the limit", 1500, 0);
                    Thread.Sleep(afterWindow);
                    page.Pump();
                    string state = page.State();
                    probe.Check("WITNESS (1.12.3) the session-limit send holds the panel's focus calls while it types and presses "
                                + "Enter (" + result + ", " + FocusBrief(state) + ")",
                        result == "accepted" && FocusStr(state, "S", "sent") == "pause for the limit" && FocusStr(state, null, "log") == ""
                        && FocusStr(state, null, "focused") == "A" && FocusBool(state, "S", "exact"));
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

        private static JsonElement? StateField(string state, string frame, string name)
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
            JsonElement? value = StateField(state, frame, name);
            return value.HasValue && value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
        }

        private static bool FocusBool(string state, string frame, string name)
        {
            JsonElement? value = StateField(state, frame, name);
            return value.HasValue && value.Value.ValueKind == JsonValueKind.True;
        }

        private static int FocusInt(string state, string frame, string name)
        {
            JsonElement? value = StateField(state, frame, name);
            int number;
            return value.HasValue && value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt32(out number) ? number : -1;
        }
    }
}
