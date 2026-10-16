using System;
using System.Collections.Generic;
using System.Globalization;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>
    /// The pane's Status card (1.7.0, the owner's request of 2026-10-16: "the same status type bar across the top of the
    /// module that shows Connection to VSCode with a green check or red x, ... how many sessions are currently running and
    /// what we know about the session limit"). Full width and pinned above every other card, as AI Brain's, Remembrance's,
    /// Fortunes' and Reminder's are. One Info row per fact, because the host colours an Info row green when it starts with
    /// ✓ and red when it starts with ✗, and only the connection row is a pass or a fail.
    ///
    /// Every row is rendered from state the poll worker already gathered: the pane is built on the UI thread, and this
    /// module measured what a probe there costs (273-285 ms with the port closed, 1.3.2).
    /// </summary>
    public sealed partial class AgentFlowModule
    {
        private const string GroupStatus = "Status";

        /// <summary>UI thread. The last scan's sessions, as a sentence.</summary>
        private string _sessionsLine = "";
        /// <summary>UI thread. The last panel read's census, as a sentence; empty when the panels could not be read.</summary>
        private string _panelsLine = "";
        /// <summary>UI thread. When the last tick's results arrived.</summary>
        private DateTimeOffset? _lastCheckedAt;

        private IEnumerable<SettingField> StatusFields()
        {
            // The card's flags are read from its FIRST field (host 1.1.6): full width, and sorted ahead of the unpinned
            // cards. "Recently auto-approved" is pinned too and follows, because pinned cards keep their schema order.
            yield return new SettingField
            {
                Id = "aboutStatusVsCode", Label = "Connection to VS Code", Kind = SettingKind.Info, Group = GroupStatus,
                FullWidth = true, PinTop = true,
            };
            yield return new SettingField { Id = "aboutStatusSessions", Label = "Agent sessions", Kind = SettingKind.Info, Group = GroupStatus };
            yield return new SettingField { Id = "aboutStatusPanels", Label = "VS Code panels", Kind = SettingKind.Info, Group = GroupStatus };
            yield return new SettingField { Id = "aboutStatusLimit", Label = "Session limit", Kind = SettingKind.Info, Group = GroupStatus };
            yield return new SettingField { Id = "aboutStatusAgentFlow", Label = "AgentFlow", Kind = SettingKind.Info, Group = GroupStatus };
        }

        private void AddStatusValues(Dictionary<string, string> values)
        {
            string mode = Mode;
            bool watching = AgentMode.Scans(mode);
            // SetupStatusLine starts the background probe when nothing is cached yet, so the next open has an answer.
            SetupStatusLine();
            values["aboutStatusVsCode"] = ConnectionLine(mode, _setupCache, _portAnswering, _panelReadable);
            values["aboutStatusSessions"] = !watching ? "Not watched: AgentFlow is Off."
                : _sessionsLine.Length > 0 ? _sessionsLine : "Waiting for the first check.";
            values["aboutStatusPanels"] = !watching ? "Not read: AgentFlow is Off."
                : _panelsLine.Length > 0 ? _panelsLine : "Not read: needs the connection above.";
            values["aboutStatusLimit"] = LimitStatusLine(watching, _limitLine, PauseAtLimit, PauseAtPercent);
            values["aboutStatusAgentFlow"] = "Mode: " + AgentMode.ToDisplay(mode)
                + (_lastCheckedAt.HasValue
                    ? " · last checked " + _lastCheckedAt.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    : watching ? " · not checked yet" : "");
        }

        /// <summary>
        /// The connection row. ✓ only when VS Code's debugging port answers AND an agent panel could be read on the last
        /// look, because a port that answers into nothing readable is exactly the failure this module once shipped looking
        /// healthy with (2026-09-18). Pure, for the self-test.
        /// </summary>
        internal static string ConnectionLine(string mode, SetupReport report, bool portAnswering, bool panelReadable)
        {
            if (!AgentMode.Scans(mode)) return "✗ Not checked: AgentFlow is Off. Pick a mode below to connect.";
            if (report == null) return "Checking…";
            int port = report.Port;
            switch (report.State)
            {
                case SetupState.Listening:
                    if (!portAnswering)
                        return "✗ Port " + port.ToString(CultureInfo.InvariantCulture) + " stopped answering. Is VS Code still open?";
                    if (!panelReadable)
                        return "✗ Port " + port.ToString(CultureInfo.InvariantCulture)
                               + " answers, but no agent panel could be read. Open the Claude Code or Codex panel in VS Code.";
                    return "✓ Connected: port " + port.ToString(CultureInfo.InvariantCulture) + " answers and the agent panels can be read.";
                case SetupState.On:
                    return "✗ Not connected: set up, waiting for VS Code to restart.";
                case SetupState.Off:
                    return "✗ Not connected: VS Code is not set up. Press “" + EnableLabel + "”, then restart VS Code.";
                case SetupState.Inert:
                    return "✗ Not connected: the debugging port is set in a form VS Code ignores. See VSCode Enablement below.";
                case SetupState.Unreadable:
                    return "✗ Not connected: argv.json is not a file AgentFlow will edit. See VSCode Enablement below.";
                default:
                    return "✗ Not connected: no argv.json found. Use “" + FindArgvLabel + "” below.";
            }
        }

        /// <summary>
        /// The sessions row, from the last scan: how many agent sessions wrote to their transcripts in the last
        /// <see cref="ActiveWindowSeconds"/>, by the app they run in, and how many are waiting on a prompt. Pure.
        /// </summary>
        internal static string SessionsLine(List<Detection> results)
        {
            if (results == null || results.Count == 0)
                return "None active in the last " + ((int)(ActiveWindowSeconds / 60)).ToString(CultureInfo.InvariantCulture) + " minutes.";
            var order = new List<string>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            int waiting = 0;
            foreach (Detection detection in results)
            {
                if (detection == null) continue;
                if (detection.Outcome == DetectionOutcome.Blocked) waiting++;
                string app = detection.Session == null ? null : AgentHosts.NameOf(detection.Session);
                if (app == null)
                    app = detection.Session != null && detection.Session.Agent == "codex" ? "Codex (app not named)" : "Claude Code (app not named)";
                int count;
                if (!counts.TryGetValue(app, out count)) order.Add(app);
                counts[app] = count + 1;
            }
            var parts = new List<string>();
            foreach (string app in order) parts.Add(counts[app].ToString(CultureInfo.InvariantCulture) + " " + app);
            return results.Count.ToString(CultureInfo.InvariantCulture) + " active in the last "
                   + ((int)(ActiveWindowSeconds / 60)).ToString(CultureInfo.InvariantCulture) + " minutes: "
                   + string.Join(", ", parts.ToArray()) + ". "
                   + (waiting == 0 ? "None waiting on a prompt." : waiting.ToString(CultureInfo.InvariantCulture) + " waiting on a prompt.");
        }

        /// <summary>The panels row, from the last panel read: Claude Code panels by state, and the Codex panels. Pure.</summary>
        internal static string PanelsLine(List<PanelUsage> claude, int codexPanels)
        {
            int open = 0, working = 0, subagents = 0;
            if (claude != null)
                foreach (PanelUsage panel in claude)
                {
                    if (panel == null || !panel.Reachable) continue;
                    open++;
                    if (panel.Subagents) subagents++;
                    else if (panel.Working) working++;
                }
            string line = open == 0 ? "No Claude Code panel open"
                : open.ToString(CultureInfo.InvariantCulture) + " Claude Code panel" + (open == 1 ? "" : "s") + ": "
                  + working.ToString(CultureInfo.InvariantCulture) + " working, "
                  + subagents.ToString(CultureInfo.InvariantCulture) + " with subagents working, "
                  + (open - working - subagents).ToString(CultureInfo.InvariantCulture) + " idle";
            return line + " · " + codexPanels.ToString(CultureInfo.InvariantCulture) + " Codex panel" + (codexPanels == 1 ? "" : "s") + ".";
        }

        /// <summary>The session-limit row: the last reading, and whether pause & resume is on. Pure.</summary>
        internal static string LimitStatusLine(bool watching, string reading, bool pauseOn, int percent)
        {
            string figure = !watching ? "Not read: AgentFlow is Off."
                : string.IsNullOrEmpty(reading) ? "Not read yet: needs the connection above."
                : reading;
            return figure + " Pause & resume: "
                   + (pauseOn ? "on, at " + percent.ToString(CultureInfo.InvariantCulture) + " %." : "off.");
        }
    }
}
