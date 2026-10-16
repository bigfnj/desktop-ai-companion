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
        /// The Status card (1.7.0): pinned first and full width, one row per fact, ✓ only for a connection that can
        /// actually read a panel, each row's wording, and that the panel read it needs reads without the pause switch and
        /// still types into nothing.
        /// </summary>
        private static bool SelfCheckStatusCard(SelfTestProbe probe)
        {
            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            using (var storage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow"))
            {
                host.UseStorage("agentflow", storage);
                host.SettingsFor("agentflow").Set(SettingMode, AgentMode.Off);   // no scan at Init (F037)
                var module = new AgentFlowModule();
                module.Init(host);

                IReadOnlyDictionary<string, string> shown = Shown(host.OptionsPanes[0]);
                IReadOnlyList<SettingField> schema = host.OptionsPanes[0].Schema;
                SettingField first = schema.Count > 0 ? schema[0] : null;
                probe.Check("status: the Status card is the schema's first card, full width and pinned to the top",
                    first != null && first.Group == GroupStatus && first.FullWidth && first.PinTop);
                int statusRows = 0, approvalsAt = -1, lastStatusAt = -1;
                for (int i = 0; i < schema.Count; i++)
                {
                    if (schema[i].Group == GroupStatus) { statusRows++; lastStatusAt = i; }
                    if (schema[i].Id == FieldApprovals) approvalsAt = i;
                }
                probe.Check("status: its five rows come before the approvals card, which is pinned too",
                    statusRows == 5 && approvalsAt > lastStatusAt);
                probe.Check("status: every row is display-only and filled on open",
                    shown.ContainsKey("aboutStatusVsCode") && shown.ContainsKey("aboutStatusSessions")
                    && shown.ContainsKey("aboutStatusPanels") && shown.ContainsKey("aboutStatusLimit")
                    && shown.ContainsKey("aboutStatusAgentFlow"));
                probe.Check("status: in Off mode the connection row is a red ✗ that says why, and nothing is claimed as read",
                    shown["aboutStatusVsCode"].StartsWith("✗", StringComparison.Ordinal)
                    && shown["aboutStatusVsCode"].IndexOf("Off", StringComparison.Ordinal) > 0
                    && shown["aboutStatusSessions"].StartsWith("Not watched", StringComparison.Ordinal)
                    && shown["aboutStatusPanels"].StartsWith("Not read", StringComparison.Ordinal));
                TrayItem limitRow = null;
                foreach (TrayItem item in host.TrayItems[0].BuildChildren())
                    if (item.DynamicText != null && item.DynamicText().StartsWith("Session limit:", StringComparison.Ordinal)) limitRow = item;
                probe.Check("tray: the submenu has a Session limit row, in a group of its own, with nothing to click",
                    limitRow != null && limitRow.Group == 2 && limitRow.Click == null && limitRow.BuildChildren == null);
                probe.Check("tray: in Off mode the row says it was not read, not a stale figure",
                    limitRow != null && limitRow.DynamicText() == "Session limit: not read (AgentFlow is off)");
                module.SavePaneValues(new Dictionary<string, string> { { "aboutStatusVsCode", "prose" }, { "aboutStatusLimit", "prose" } });
                probe.Check("status: Save never writes a status row into settings",
                    host.SettingsFor("agentflow").Get("aboutStatusVsCode", "missing") == "missing"
                    && host.SettingsFor("agentflow").Get("aboutStatusLimit", "missing") == "missing");
                module.Shutdown();
            }

            // ---- the connection row -----------------------------------------------------------------
            var listening = new SetupReport { State = SetupState.Listening, Port = 9321 };
            probe.Check("status: ✓ only when the port answers AND an agent panel was read",
                ConnectionLine(AgentMode.Notify, listening, true, true).StartsWith("✓ Connected", StringComparison.Ordinal));
            probe.Check("status: a port that answers into nothing readable is a ✗, and says to open the panel",
                ConnectionLine(AgentMode.Notify, listening, true, false).StartsWith("✗", StringComparison.Ordinal)
                && ConnectionLine(AgentMode.Notify, listening, true, false).IndexOf("Open the Claude Code or Codex panel", StringComparison.Ordinal) > 0);
            probe.Check("status: a port that stopped answering is a ✗",
                ConnectionLine(AgentMode.AutoApprove, listening, false, true).StartsWith("✗", StringComparison.Ordinal));
            probe.Check("status: VS Code not set up is a ✗ that names the pane's own Enable button",
                ConnectionLine(AgentMode.Notify, new SetupReport { State = SetupState.Off, Port = 9321 }, false, false)
                    .IndexOf(EnableLabel, StringComparison.Ordinal) > 0);
            probe.Check("status: set up and waiting for a restart is a ✗ that says so",
                ConnectionLine(AgentMode.Notify, new SetupReport { State = SetupState.On, Port = 9321 }, false, false)
                    .IndexOf("restart", StringComparison.Ordinal) > 0);
            string checking = ConnectionLine(AgentMode.Notify, null, false, false);
            probe.Check("status: not inspected yet is neither a pass nor a fail",
                !checking.StartsWith("✓", StringComparison.Ordinal) && !checking.StartsWith("✗", StringComparison.Ordinal));

            // ---- the sessions, panels and limit rows ---------------------------------------------------
            var sessions = new List<Detection>
            {
                new Detection { Outcome = DetectionOutcome.Blocked, Session = new AgentSession { Agent = "claude", Host = "claude-vscode" } },
                new Detection { Outcome = DetectionOutcome.Idle, Session = new AgentSession { Agent = "claude", Host = "claude-vscode" } },
                new Detection { Outcome = DetectionOutcome.Working, Session = new AgentSession { Agent = "claude", Host = "claude-desktop" } },
                new Detection { Outcome = DetectionOutcome.Idle, Session = new AgentSession { Agent = "codex" } },
            };
            string sessionsLine = SessionsLine(sessions);
            probe.Check("status: the sessions row counts them by app and says how many are waiting",
                sessionsLine.StartsWith("4 active in the last 15 minutes: 2 " + AgentHosts.ClaudeVsCode + ", 1 " + AgentHosts.ClaudeDesktop
                                        + ", 1 Codex (app not named).", StringComparison.Ordinal)
                && sessionsLine.EndsWith("1 waiting on a prompt.", StringComparison.Ordinal));
            probe.Check("status: no sessions is said, not left blank",
                SessionsLine(new List<Detection>()) == "None active in the last 15 minutes.");

            var panels = new List<PanelUsage>
            {
                new PanelUsage { TargetId = "A", Reachable = true, Working = true },
                new PanelUsage { TargetId = "B", Reachable = true, Working = true, Subagents = true },
                new PanelUsage { TargetId = "C", Reachable = true },
                new PanelUsage { TargetId = "D", Reachable = false },
            };
            probe.Check("status: the panels row says which Claude panels are working, delegating or idle, and the Codex ones",
                PanelsLine(panels, 1) == "3 Claude Code panels: 1 working, 1 with subagents working, 1 idle · 1 Codex panel.");
            probe.Check("status: no panel open is said as such",
                PanelsLine(new List<PanelUsage>(), 0) == "No Claude Code panel open · 0 Codex panels.");
            probe.Check("status: the limit row carries the reading and the pause switch's state",
                LimitStatusLine(true, "Session: 24 %.", true, 90) == "Session: 24 %. Pause & resume: on, at 90 %."
                && LimitStatusLine(true, "", false, 90).StartsWith("Not read yet", StringComparison.Ordinal)
                && LimitStatusLine(false, "Session: 24 %.", false, 90).StartsWith("Not read: AgentFlow is Off", StringComparison.Ordinal));

            // ---- the read the card needs, with the pause switch OFF -------------------------------------------
            var now = new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
            string json = "{\"working\":true,\"subagents\":false,\"input\":true,\"draft\":false,\"prompt\":false,"
                + "\"info\":{\"status\":\"allowed_warning\",\"type\":\"five_hour\",\"utilization\":0.95,\"resetsAt\":"
                + now.AddHours(2).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + ",\"windows\":{}},"
                + "\"infoAt\":" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
                + ",\"windows\":null,\"windowsAt\":0,\"banner\":\"\"}";
            var claude = new FakeCdpServer.Target { Id = "STATUS01", Url = "vscode-webview://s?" + CdpApprover.ClaudeTargetMarker,
                EvaluateResult = json, SendResult = "sent", ConfirmResult = "accepted" };
            var codex = new FakeCdpServer.Target { Id = "STATUS02", Url = "vscode-webview://t?" + CdpApprover.CodexTargetMarker,
                EvaluateResult = json };
            Func<DateTimeOffset> clockWas = LimitClock;
            TimeZoneInfo zoneWas = LimitZone;
            try
            {
                LimitClock = () => now;
                LimitZone = TimeZoneInfo.Utc;
                var reader = new AgentFlowModule();
                using (var server = new FakeCdpServer(new[] { claude, codex }))
                {
                    LimitOutcome read = reader.WatchSessionLimit(server.Port, false, 90, null);
                    probe.Check("status: with the pause switch off the panels are still read for the card",
                        read != null && read.Panels == "1 Claude Code panel: 1 working, 0 with subagents working, 0 idle · 1 Codex panel."
                        && read.Line.IndexOf("95 %", StringComparison.Ordinal) > 0);
                    probe.Check("WITNESS status: ...and at 95 % with the switch off nobody is typed into and nobody is told",
                        claude.LastSendExpression == null && read != null && read.Notice == null && read.Log.Count == 0);
                    probe.Check("tray: the same read gives the tray row its figure and reset",
                        read != null && read.Tray == "Session limit: 95 % · resets 12:00");
                    var weekly = new LimitReading { Percent = 41.7, ResetsAt = now.AddDays(2), Source = "event", CapturedAt = now };
                    var estimate = new LimitReading { Percent = 12.4, ResetsAt = now.AddHours(3), ResetIsEstimate = true, Source = "banner", CapturedAt = now };
                    probe.Check("tray: the row adds the weekly figure and the pause threshold when there are any",
                        LimitTrayText(estimate, weekly, true, 90) == "Session limit: 12 % · resets by ~13:00 · weekly 41 % · pause at 90 %");
                    probe.Check("tray: a read with no figure says so",
                        LimitTrayText(null, null, false, 90) == "Session limit: no figure yet");
                }
            }
            finally
            {
                LimitClock = clockWas;
                LimitZone = zoneWas;
            }
            return true;
        }
    }
}
