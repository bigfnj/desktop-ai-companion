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
    /// OFF BY DEFAULT, like everything here that acts in another application on the user's behalf. It types in Notify and
    /// in auto-approve, never in Log or Off (Log only since N-audit-1106-007: see LimitMayType), and only while VS Code's
    /// debugging port answers, because it reaches the panels the same way the press does.
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

        /// <summary>
        /// Whether the watch may ask a panel to pause: its switch on AND a mode that acts (N-audit-1106-007). The same
        /// modes as the pane's WhileItStillSpeaks, which is what greys the switch, so a box shown greyed is never live.
        /// One place for the tick's snapshot and the step's last look, so the two cannot disagree. Pure, for the
        /// self-test.
        /// </summary>
        internal static bool LimitMayType(bool pauseAtLimit, string mode)
        {
            return pauseAtLimit && AgentMode.Speaks(mode);
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

        /// <summary>The pane rows and the tray row once VS Code's port has stopped answering (N-audit-1106-094): the
        /// tick writes these in place of the last reading, which nothing can refresh until the port is back.</summary>
        internal const string LimitNotAnsweringLine = "Not read: VS Code is not answering.";
        internal const string LimitNotAnsweringTray = "Session limit: not read (VS Code is not answering)";

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
                    // down, or switched off, must not type into a panel on the way out. The mode is in it as well
                    // (N-audit-1106-007): a user who moves to Log while a step is running gets nothing typed either.
                    if (_shuttingDown || !LimitMayType(PauseAtLimit, Mode)) break;
                    string result = CdpApprover.SendPause(port, panel.TargetId, message, 1500, ConfirmAfterMs);
                    _limitWatch.Record(panel.TargetId, result, resetsAt);
                    if (result == "accepted") accepted++;
                    results.Add(PanelNote(panel, result));
                }
                string reading = SessionLimit.Describe(session, now, LimitZone);
                // The panels ASKED, not the panels planned (N-audit-1106-106). The loop above stops at its last look, so a
                // step cut short by the switch, the mode or shutdown said it had asked panels it never typed into, and with
                // none asked the notice said "I asked 1 working session" beside a log line saying none was working.
                int asked = results.Count;
                int notAsked = plan.SendTo.Count - asked;
                string stopped = notAsked > 0
                    ? "; stopped before " + notAsked.ToString(CultureInfo.InvariantCulture)
                      + " more: pausing was switched off, the mode stopped acting, or AgentFlow began shutting down"
                    : "";
                if (asked > 0)
                    outcome.Log.Add("session limit " + reading + ": asked " + asked.ToString(CultureInfo.InvariantCulture)
                                    + " working panel(s) to pause and resume (" + string.Join(", ", results.ToArray()) + ")" + stopped);
                else if (notAsked > 0)
                    outcome.Log.Add("session limit " + reading + ": asked no panel" + stopped);
                if (plan.NoticeSession)
                {
                    _limitWatch.NoticedSession(resetsAt);
                    outcome.Notice = SessionNotice(session, accepted, asked, notAsked, OutsideVsCode(live), now);
                    if (plan.SendTo.Count == 0)
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

        /// <summary>The companion's sentence when the session limit crosses the threshold, with nobody left unasked. Pure,
        /// for the self-test.</summary>
        internal static string SessionNotice(LimitReading session, int accepted, int asked, int outside, DateTimeOffset now)
        {
            return SessionNotice(session, accepted, asked, 0, outside, now);
        }

        /// <summary>As above; <paramref name="notAsked"/> working sessions were planned and not asked, because the step's
        /// last look stopped it (N-audit-1106-106). When the notice is heard at all, that was the pause switch: Log and Off
        /// deliver no notice, and shutdown drops it.</summary>
        internal static string SessionNotice(LimitReading session, int accepted, int asked, int notAsked, int outside, DateTimeOffset now)
        {
            string head = "Your Claude session limit is at " + SessionLimit.Whole(session.Percent) + " % and resets "
                          + (session.ResetIsEstimate ? "by about " : "at ")
                          + TimeZoneInfo.ConvertTime(session.ResetsAt.Value, LimitZone ?? TimeZoneInfo.Local)
                              .ToString("HH:mm", CultureInfo.InvariantCulture) + ".";
            string body;
            if (asked == 0 && notAsked > 0) body = " Pausing was switched off before I asked any working session.";
            else if (asked == 0) body = " No Claude Code session in VS Code was working, so I asked nobody to pause.";
            else if (accepted == asked)
                body = " I asked " + Count(asked, "working session") + " to stop at a safe point and resume after the reset.";
            else
                body = " I asked " + Count(asked, "working session") + " to stop at a safe point and resume after the reset; "
                       + (asked - accepted).ToString(CultureInfo.InvariantCulture) + " did not take the message yet.";
            if (asked > 0 && notAsked > 0)
                body += " Pausing was switched off before I asked " + Count(notAsked, "more working session") + ".";
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
            return PauseNow("the pane").ContinueWith(t => t.Status == TaskStatus.RanToCompletion
                ? t.Result.Pane : "Could not ask the sessions to pause.", TaskScheduler.Default);
        }

        /// <summary>What one pause request did: the pane's sentence (with each panel's id and result) and a short one to
        /// say aloud, for the tray, which has nowhere to show text (1.9.0).</summary>
        internal sealed class PauseResult
        {
            public string Pane = "";
            public string Spoken = "";
        }

        /// <summary>The pane button and the tray row (1.9.0) do the same thing; <paramref name="from"/> names which in
        /// the log ("the pane", "the tray").</summary>
        internal Task<PauseResult> PauseNow(string from)
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
                if (!took)
                {
                    const string busy = "AgentFlow was busy reading the panels. Try again in a moment.";
                    return new PauseResult { Pane = busy, Spoken = busy };
                }
                try
                {
                    DateTimeOffset now = LimitClock();
                    List<PanelUsage> panels = CdpApprover.ReadUsage(port, 1500, now);
                    if (panels.Count == 0)
                    {
                        const string none = "No Claude Code panel could be read. Is VS Code open with AgentFlow enabled for it?";
                        return new PauseResult { Pane = none, Spoken = none };
                    }
                    LimitReading session = SessionLimit.Best(panels, now, false);
                    string message = SessionLimit.PauseMessage(session, now, LimitZone);
                    var results = new List<string>();
                    int working = 0, accepted = 0;
                    foreach (PanelUsage panel in panels)
                    {
                        if (!SessionLimit.Pausable(panel)) continue;
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
                    string logLine = "pause requested from " + from + ": " + summary;
                    PostToUi(() => { if (!_shuttingDown) Log(logLine); });
                    return new PauseResult
                    {
                        Pane = DescribePauseNow(summary, working, session, now),
                        Spoken = SpokenPause(working, accepted),
                    };
                }
                finally { Volatile.Write(ref _scanning, 0); }
            });
        }

        /// <summary>The tray click's spoken answer: counts only, no panel ids. Pure, for the self-test.</summary>
        internal static string SpokenPause(int working, int accepted)
        {
            if (working == 0) return "No Claude Code session was working, so nothing was paused.";
            return "Asked " + Count(working, "working session") + " to pause; "
                   + accepted.ToString(CultureInfo.InvariantCulture) + " took the message.";
        }

        /// <summary>The last tray click's request, for the self-test to wait on.</summary>
        internal Task<PauseResult> LastTrayPauseForSelfTest;

        /// <summary>
        /// The tray's Session limit row, clicked (1.9.0, the owner's request of 2026-10-16): the same request as the pane's
        /// "Pause working sessions now", answered aloud because a tray menu has nowhere to show a result. The click returns
        /// at once; the work runs on a pool thread and the answer comes back on the UI thread.
        /// </summary>
        internal void PauseNowFromTray()
        {
            Task<PauseResult> pausing = PauseNow("the tray");
            LastTrayPauseForSelfTest = pausing;
            pausing.ContinueWith(t =>
            {
                string spoken = t.Status == TaskStatus.RanToCompletion ? t.Result.Spoken : "Could not ask the sessions to pause.";
                PostToUi(() =>
                {
                    if (_shuttingDown) return;
                    IHost host = _host;
                    if (host == null) return;
                    try { host.SayAll(spoken); } catch { }
                });
            }, TaskScheduler.Default);
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
