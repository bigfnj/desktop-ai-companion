using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>
    /// The session-limit watch's half of the module (1.6.0): the two settings, the step the poll worker runs, what it
    /// reports on the UI thread, and the pane's card. The reading and the sending are in SessionLimit.cs.
    ///
    /// OFF BY DEFAULT, like everything here that acts in another application on the user's behalf. It runs in Notify and
    /// in auto-approve, never in Off, and only while VS Code's debugging port answers, because it reaches the panels the
    /// same way the press does.
    /// </summary>
    public sealed partial class AgentFlowModule
    {
        private const string SettingPauseAtLimit = "pauseAtLimit";
        private const string SettingPauseAtPercent = "pauseAtLimitPercent";
        private const string GroupSessionLimit = "Session limit";

        /// <summary>The owner's wording for the switch (2026-10-15).</summary>
        internal const string PauseAtLimitLabel = "Send a pause & resume command when the session limit passes";
        internal const string PauseAtPercentLabel = "...this percentage of the session limit";
        internal const string PauseNowLabel = "Pause working sessions now";

        /// <summary>Poll worker only, under the scan guard (or the pane action's turn of it).</summary>
        private readonly SessionLimitWatch _limitWatch = new SessionLimitWatch();

        /// <summary>The pane's line about the last reading. Written on the UI thread, read there.</summary>
        private string _limitLine = "";

        /// <summary>Test seams: the clock the watch reads, the zone its messages are written in, and how long a send waits
        /// before checking that the panel took the message.</summary>
        internal static Func<DateTimeOffset> LimitClock = () => DateTimeOffset.Now;
        internal static TimeZoneInfo LimitZone = null;
        internal static int ConfirmAfterMs = 600;

        internal bool PauseAtLimit
        {
            get { return _settings != null && _settings.GetBool(SettingPauseAtLimit, false); }
        }

        internal int PauseAtPercent
        {
            get
            {
                int value = _settings == null ? SessionLimit.DefaultThreshold
                    : _settings.GetInt(SettingPauseAtPercent, SessionLimit.DefaultThreshold);
                return (int)Clamp(value, SessionLimit.MinThreshold, SessionLimit.MaxThreshold);
            }
        }

        /// <summary>What one watch step found and did, carried from the worker to the UI thread.</summary>
        internal sealed class LimitOutcome
        {
            /// <summary>For the pane: the last reading, or why there is none.</summary>
            public string Line = "";
            /// <summary>For the diagnostic log. Never a session title, never chat text.</summary>
            public readonly List<string> Log = new List<string>();
            /// <summary>For the companion, through the notify channels. Null when there is nothing to tell.</summary>
            public string Notice;
            /// <summary>For the Status card: the panels read, by state (1.7.0).</summary>
            public string Panels = "";
            /// <summary>For the tray submenu's row (1.8.0): "Session limit: 12 % · resets 12:00".</summary>
            public string Tray = "";
        }

        /// <summary>UI thread. The tray row's text from the last panel read; empty until there is one.</summary>
        private string _limitTray = "";

        /// <summary>
        /// The tray row, evaluated on every menu open (the owner's request of 2026-10-16: "in our tray menu can we add a
        /// line Session Limit: 12%"). A snapshot of the last read, like every tray text a module can give.
        /// </summary>
        internal string LimitTrayRow()
        {
            if (!Enabled) return "Session limit: not read (AgentFlow is off)";
            return _limitTray.Length > 0 ? _limitTray : "Session limit: not read yet";
        }

        /// <summary>"Session limit: 12 % · resets 12:00", then the weekly figure and the pause threshold when there are
        /// any. Pure, for the self-test.</summary>
        internal static string LimitTrayText(LimitReading session, LimitReading weekly, bool pauseOn, int percent)
        {
            TimeZoneInfo zone = LimitZone ?? TimeZoneInfo.Local;
            string text = "Session limit: ";
            if (session == null) text += "no figure yet";
            else
            {
                text += SessionLimit.Whole(session.Percent) + " %";
                if (session.ResetsAt.HasValue)
                    text += " · resets " + (session.ResetIsEstimate ? "by ~" : "")
                            + TimeZoneInfo.ConvertTime(session.ResetsAt.Value, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
            }
            if (weekly != null) text += " · weekly " + SessionLimit.Whole(weekly.Percent) + " %";
            if (pauseOn) text += " · pause at " + percent.ToString(CultureInfo.InvariantCulture) + " %";
            return text;
        }

        /// <summary>
        /// One step of the watch, on the poll worker. Reads the Claude Code panels every
        /// <see cref="SessionLimit.ReadEvery"/> (for the Status card as well, since 1.7.0), and when
        /// <paramref name="pauseOn"/> is set and the session limit has passed the threshold with a known reset, asks each
        /// WORKING panel once per window. With it off the step reads and reports, and asks nobody and tells nobody. Null
        /// when it is not this tick's turn to read.
        /// </summary>
        internal LimitOutcome WatchSessionLimit(int port, bool pauseOn, int threshold, List<Detection> live)
        {
            DateTimeOffset now = LimitClock();
            if (!_limitWatch.DueToRead(now)) return null;
            int codexPanels;
            List<PanelUsage> panels = CdpApprover.ReadUsage(port, 1500, now, out codexPanels);
            LimitPlan plan = _limitWatch.Plan(panels, now, threshold);
            var outcome = new LimitOutcome
            {
                Line = LimitLine(plan, now),
                Panels = PanelsLine(panels, codexPanels),
                // No panel read at all is not "no figure": leave the row saying it was not read.
                Tray = plan.Reachable == 0 ? "" : LimitTrayText(plan.Session, plan.Weekly, pauseOn, threshold),
            };
            if (!pauseOn) return outcome;

            if (plan.SendTo.Count > 0 || plan.NoticeSession)
            {
                LimitReading session = plan.Session;
                DateTimeOffset resetsAt = session.ResetsAt.Value;
                string message = SessionLimit.PauseMessage(session, now, LimitZone);
                var results = new List<string>();
                int accepted = 0;
                foreach (PanelUsage panel in plan.SendTo)
                {
                    // The last look before acting in another application, as the press takes one: a module being shut
                    // down, or switched off, must not type into a panel on the way out.
                    if (_shuttingDown || !PauseAtLimit) break;
                    string result = CdpApprover.SendPause(port, panel.TargetId, message, 1500, ConfirmAfterMs);
                    _limitWatch.Record(panel.TargetId, result, resetsAt);
                    if (result == "accepted") accepted++;
                    results.Add(PanelNote(panel, result));
                }
                string reading = SessionLimit.Describe(session, now, LimitZone);
                if (results.Count > 0)
                    outcome.Log.Add("session limit " + reading + ": asked " + plan.SendTo.Count.ToString(CultureInfo.InvariantCulture)
                                    + " working panel(s) to pause and resume (" + string.Join(", ", results.ToArray()) + ")");
                if (plan.NoticeSession)
                {
                    _limitWatch.NoticedSession(resetsAt);
                    outcome.Notice = SessionNotice(session, accepted, plan.SendTo.Count, OutsideVsCode(live), now);
                    if (results.Count == 0)
                        outcome.Log.Add("session limit " + reading + ": no Claude Code panel was working, so nothing was asked");
                }
            }

            if (plan.NoticeWeekly)
            {
                LimitReading weekly = plan.Weekly;
                _limitWatch.NoticedWeekly(weekly.ResetsAt ?? now.AddDays(1));
                string weeklyLine = "Your weekly Claude limit is at " + SessionLimit.Whole(weekly.Percent) + " %"
                                    + (weekly.ResetsAt.HasValue ? ", resetting " + Day(weekly.ResetsAt.Value, now) : "")
                                    + ". AgentFlow pauses sessions for the session limit only.";
                outcome.Notice = outcome.Notice == null ? weeklyLine : outcome.Notice + " " + weeklyLine;
                outcome.Log.Add("weekly limit " + SessionLimit.Describe(weekly, now, LimitZone) + ": said so; nothing is paused for it");
            }
            return outcome;
        }

        /// <summary>The companion's sentence when the session limit crosses the threshold. Pure, for the self-test.</summary>
        internal static string SessionNotice(LimitReading session, int accepted, int asked, int outside, DateTimeOffset now)
        {
            string head = "Your Claude session limit is at " + SessionLimit.Whole(session.Percent) + " % and resets "
                          + (session.ResetIsEstimate ? "by about " : "at ")
                          + TimeZoneInfo.ConvertTime(session.ResetsAt.Value, LimitZone ?? TimeZoneInfo.Local)
                              .ToString("HH:mm", CultureInfo.InvariantCulture) + ".";
            string body;
            if (asked == 0) body = " No Claude Code session in VS Code was working, so I asked nobody to pause.";
            else if (accepted == asked)
                body = " I asked " + Count(asked, "working session") + " to stop at a safe point and resume after the reset.";
            else
                body = " I asked " + Count(asked, "working session") + " to stop at a safe point and resume after the reset; "
                       + (asked - accepted).ToString(CultureInfo.InvariantCulture) + " did not take the message yet.";
            if (outside > 0)
                body += " " + Count(outside, "Claude session") + " outside VS Code " + (outside == 1 ? "is" : "are")
                        + " yours to pause: AgentFlow cannot type there.";
            return head + body;
        }

        private static string Count(int n, string noun)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");
        }

        /// <summary>Live Claude sessions in the desktop app or a terminal, which no panel read can reach.</summary>
        internal static int OutsideVsCode(List<Detection> live)
        {
            int count = 0;
            if (live == null) return 0;
            foreach (Detection detection in live)
            {
                if (detection == null || detection.Session == null) continue;
                string app = AgentHosts.NameOf(detection.Session);
                if (app == AgentHosts.ClaudeDesktop || app == AgentHosts.ClaudeCli) count++;
            }
            return count;
        }

        /// <summary>"at 16:45" today, "on 2026-10-18 at 09:00" otherwise.</summary>
        private static string Day(DateTimeOffset when, DateTimeOffset now)
        {
            TimeZoneInfo zone = LimitZone ?? TimeZoneInfo.Local;
            DateTimeOffset local = TimeZoneInfo.ConvertTime(when, zone);
            DateTimeOffset today = TimeZoneInfo.ConvertTime(now, zone);
            string clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);
            return local.Date == today.Date ? "at " + clock
                 : "on " + local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " at " + clock;
        }

        /// <summary>"aaba8485 accepted", or "aaba8485 (subagents) accepted" for a panel whose main agent was idle while its
        /// subagents worked, so the log says why an idle-looking panel was asked.</summary>
        internal static string PanelNote(PanelUsage panel, string result)
        {
            return ShortTarget(panel.TargetId) + (panel.Subagents ? " (subagents)" : "") + " " + result;
        }

        /// <summary>
        /// The pane button's answer. The reset is named only when something was sent: 1.6.0 said "The message asks each
        /// one to resume after the reset" beneath "nothing was sent", which the owner saw on 2026-10-16.
        /// </summary>
        internal static string DescribePauseNow(string summary, int working, LimitReading session, DateTimeOffset now)
        {
            if (working == 0) return summary;
            return summary + (session != null && session.ResetsAt.HasValue
                ? " The message asks each one to resume after the reset (" + SessionLimit.Describe(session, now, LimitZone) + ")."
                : " No reset time was available, so the message asks each one to pause and wait.");
        }

        /// <summary>A CDP target id is a random hex string; eight characters of it name a panel in the log.</summary>
        private static string ShortTarget(string targetId)
        {
            if (string.IsNullOrEmpty(targetId)) return "?";
            return (targetId.Length > 8 ? targetId.Substring(0, 8) : targetId).ToLowerInvariant();
        }

        private static string LimitLine(LimitPlan plan, DateTimeOffset now)
        {
            if (plan.Reachable == 0) return "No Claude Code panel could be read on the last look.";
            string line = "Session: " + SessionLimit.Describe(plan.Session, now, LimitZone) + ".";
            if (plan.Weekly != null) line += " Weekly: " + SessionLimit.Describe(plan.Weekly, now, LimitZone) + ".";
            if (plan.Session == null)
                line += " The panel shows a figure from about 70 % used, or when the limit's status changes.";
            return line;
        }

        /// <summary>UI thread: the log lines, the pane line, and the companion's notice over the notify channels.</summary>
        private void ReportSessionLimit(LimitOutcome outcome)
        {
            if (outcome == null) return;
            _limitLine = outcome.Line;
            _panelsLine = outcome.Panels;
            _limitTray = outcome.Tray;
            foreach (string line in outcome.Log) Log(line);
            if (outcome.Notice == null) return;
            Delivery delivery = Deliver(outcome.Notice);
            if (delivery.Wanted && !delivery.Delivered)
                Log("the session-limit notice reached nobody: " + delivery.Undelivered);
        }

        /// <summary>
        /// The pane button: ask every WORKING Claude Code panel now, without waiting for the threshold. With a reading
        /// that has a reset time it sends the same pause-and-resume request the watch would; without one, a plain pause
        /// (stop at a safe point, write the note, wait), because there is no time to resume at. It takes the scan guard
        /// so it cannot type into a panel the watch is typing into.
        /// </summary>
        private Task<string> PauseNowAsync()
        {
            int port = CdpPort;
            return Task.Run(() =>
            {
                bool took = false;
                for (int attempt = 0; attempt < 30 && !took; attempt++)
                {
                    took = Interlocked.CompareExchange(ref _scanning, 1, 0) == 0;
                    if (!took) Thread.Sleep(100);
                }
                if (!took) return "AgentFlow was busy reading the panels. Try again in a moment.";
                try
                {
                    DateTimeOffset now = LimitClock();
                    List<PanelUsage> panels = CdpApprover.ReadUsage(port, 1500, now);
                    if (panels.Count == 0)
                        return "No Claude Code panel could be read. Is VS Code open with AgentFlow enabled for it?";
                    LimitReading session = SessionLimit.Best(panels, now, false);
                    string message = SessionLimit.PauseMessage(session, now, LimitZone);
                    var results = new List<string>();
                    int working = 0, accepted = 0;
                    foreach (PanelUsage panel in panels)
                    {
                        if (!panel.Reachable || !panel.Working || !panel.HasInput) continue;
                        working++;
                        string result = CdpApprover.SendPause(port, panel.TargetId, message, 1500, ConfirmAfterMs);
                        if (result == "accepted") accepted++;
                        results.Add(PanelNote(panel, result));
                    }
                    string summary = working == 0
                        ? "No Claude Code session was working, so nothing was sent."
                        : "Asked " + working.ToString(CultureInfo.InvariantCulture) + " working session(s); "
                          + accepted.ToString(CultureInfo.InvariantCulture) + " took the message ("
                          + string.Join(", ", results.ToArray()) + ").";
                    string logLine = "pause requested from the pane: " + summary;
                    PostToUi(() => { if (!_shuttingDown) Log(logLine); });
                    return DescribePauseNow(summary, working, session, now);
                }
                finally { Volatile.Write(ref _scanning, 0); }
            });
        }

        // ---- the pane ------------------------------------------------------------------------------

        private IEnumerable<SettingField> SessionLimitFields()
        {
            yield return new SettingField
            {
                Id = SettingPauseAtLimit,
                Label = PauseAtLimitLabel,
                Kind = SettingKind.Bool,
                Group = GroupSessionLimit,
                EnabledWhen = WhileItStillSpeaks,
            };
            yield return new SettingField
            {
                Id = SettingPauseAtPercent,
                Label = PauseAtPercentLabel,
                Kind = SettingKind.Int,
                Min = SessionLimit.MinThreshold,
                Max = SessionLimit.MaxThreshold,
                Group = GroupSessionLimit,
                EnabledWhen = WhileItStillSpeaks,
            };
            yield return new SettingField { Id = "aboutLimitNow", Label = "Last reading", Kind = SettingKind.Info, Group = GroupSessionLimit };
            yield return new SettingField { Id = "aboutLimitHow", Label = "How it works", Kind = SettingKind.Info, Group = GroupSessionLimit };
        }

        private void AddSessionLimitValues(Dictionary<string, string> values)
        {
            values[SettingPauseAtLimit] = PauseAtLimit ? "true" : "false";
            values[SettingPauseAtPercent] = PauseAtPercent.ToString(CultureInfo.InvariantCulture);
            values["aboutLimitNow"] = _limitLine.Length > 0 ? _limitLine : "Waiting for the first look at the panels.";
            values["aboutLimitHow"] =
                "It reads the session-limit figure the Claude Code panels in VS Code show, once a minute while AgentFlow "
                + "is watching and connected (the Status card shows it). With the box above ticked, when the session limit "
                + "passes the percentage above and the reset time is known, it types one message into "
                + "each panel whose agent is working: stop at a safe point, write a resume note, and schedule a one-shot "
                + "resume with CronCreate for five minutes after the reset. Once per panel per limit window. It never types "
                + "into a panel with a permission prompt up or with your own unsent text in its box, and never into Codex. "
                + "Sessions in the Claude desktop app or a terminal cannot be reached and are named instead. A resume scheduled "
                + "with CronCreate lives as long as its session, so the note is what to pick up from if the session closes.";
        }
    }
}
