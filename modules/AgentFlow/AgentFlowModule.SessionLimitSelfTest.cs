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
        /// The session-limit watch (1.6.0): the panel's JSON and banner as measured against extension 2.1.300, the
        /// once-per-window decision, the message, the expressions' guards, the wire through the fake CDP server, the
        /// module's worker step, and the pane. Nothing here reaches a real editor.
        /// </summary>
        private static bool SelfCheckSessionLimit(SelfTestProbe probe)
        {
            var now = new DateTimeOffset(2026, 10, 15, 15, 0, 0, TimeSpan.Zero);
            long nowMs = now.ToUnixTimeMilliseconds();
            long resetSeconds = now.AddHours(2).ToUnixTimeSeconds();
            long weeklySeconds = now.AddDays(3).ToUnixTimeSeconds();
            string r = resetSeconds.ToString(CultureInfo.InvariantCulture);
            string wk = weeklySeconds.ToString(CultureInfo.InvariantCulture);
            string at = nowMs.ToString(CultureInfo.InvariantCulture);

            // ---- the panel's JSON ----------------------------------------------------------------
            string eventJson = "{\"working\":true,\"input\":true,\"draft\":false,\"prompt\":false,"
                + "\"info\":{\"status\":\"allowed_warning\",\"type\":\"five_hour\",\"utilization\":0.91,\"resetsAt\":" + r + ","
                + "\"windows\":{\"five_hour\":{\"utilization\":0.91,\"resetsAt\":" + r + "},\"seven_day\":{\"utilization\":0.4,\"resetsAt\":" + wk + "}}},"
                + "\"infoAt\":" + at + ",\"windows\":null,\"windowsAt\":0,\"banner\":\"\"}";
            PanelUsage fromEvent = SessionLimit.Parse("T1", eventJson, now);
            probe.Check("session limit: a rate_limit_event's figure is read as a percentage of the session window",
                fromEvent != null && fromEvent.Session != null && Math.Abs(fromEvent.Session.Percent - 91) < 0.01
                && fromEvent.Session.Source == "event" && !fromEvent.Session.ResetIsEstimate);
            probe.Check("...with its exact reset, from epoch seconds",
                fromEvent != null && fromEvent.Session != null && fromEvent.Session.ResetsAt == now.AddHours(2));
            probe.Check("...and the weekly window it carries beside it",
                fromEvent != null && fromEvent.Weekly != null && Math.Abs(fromEvent.Weekly.Percent - 40) < 0.01);
            probe.Check("...and the panel's state: working, a chat box, no draft, no prompt",
                fromEvent != null && fromEvent.Reachable && fromEvent.Working && fromEvent.HasInput && !fromEvent.HasDraft && !fromEvent.PromptUp);

            string updateJson = "{\"working\":false,\"input\":true,\"draft\":false,\"prompt\":false,\"info\":null,\"infoAt\":0,"
                + "\"windows\":{\"five_hour\":{\"utilization\":0.62,\"resetsAt\":" + r + "}},\"windowsAt\":" + at + ",\"banner\":\"\"}";
            PanelUsage fromUpdate = SessionLimit.Parse("T2", updateJson, now);
            probe.Check("session limit: a panel_usage_update's windows are read too",
                fromUpdate != null && fromUpdate.Session != null && Math.Abs(fromUpdate.Session.Percent - 62) < 0.01
                && fromUpdate.Session.Source == "update" && !fromUpdate.Working);

            // An update NEWER than the event wins for the window it carries.
            string mixedJson = "{\"working\":true,\"input\":true,\"draft\":false,\"prompt\":false,"
                + "\"info\":{\"status\":\"allowed_warning\",\"type\":\"five_hour\",\"utilization\":0.80,\"resetsAt\":" + r + ",\"windows\":{}},"
                + "\"infoAt\":" + (nowMs - 60000).ToString(CultureInfo.InvariantCulture) + ","
                + "\"windows\":{\"five_hour\":{\"utilization\":0.93,\"resetsAt\":" + r + "}},\"windowsAt\":" + at + ",\"banner\":\"\"}";
            PanelUsage mixed = SessionLimit.Parse("T3", mixedJson, now);
            probe.Check("session limit: of an event and an update, the newer figure is kept",
                mixed != null && mixed.Session != null && Math.Abs(mixed.Session.Percent - 93) < 0.01);

            string rejectedJson = "{\"working\":true,\"input\":true,\"draft\":false,\"prompt\":false,"
                + "\"info\":{\"status\":\"rejected\",\"type\":\"five_hour\",\"utilization\":null,\"resetsAt\":" + r + ",\"windows\":{}},"
                + "\"infoAt\":" + at + ",\"windows\":null,\"windowsAt\":0,\"banner\":\"\"}";
            PanelUsage rejected = SessionLimit.Parse("T4", rejectedJson, now);
            probe.Check("session limit: a limit that was HIT reads as 100 % with its reset",
                rejected != null && rejected.Session != null && rejected.Session.Percent >= 100 && rejected.Session.ResetsAt == now.AddHours(2));

            // 1.6.1: the owner's other session on 2026-10-16 -- three subagents working, its own chat box idle.
            PanelUsage delegating = SessionLimit.Parse("T8",
                "{\"working\":true,\"subagents\":true,\"input\":true,\"draft\":false,\"prompt\":false,\"info\":null,\"infoAt\":0,"
                + "\"windows\":null,\"windowsAt\":0,\"banner\":\"\"}", now);
            probe.Check("session limit: a panel whose subagents are working reads as working, and says why",
                delegating != null && delegating.Working && delegating.Subagents
                && PanelNote(delegating, "accepted") == "t8 (subagents) accepted");

            PanelUsage unreachable = SessionLimit.Parse("T5", "unreachable", now);
            probe.Check("session limit: an unreachable panel is said as such, not as an empty one",
                unreachable != null && !unreachable.Reachable);
            probe.Check("session limit: JSON of another shape is no reading at all",
                SessionLimit.Parse("T6", "[1,2]", now) == null && SessionLimit.Parse("T6", "{not json", now) == null
                && SessionLimit.Parse("T6", null, now) == null);

            // ---- the banner ------------------------------------------------------------------------
            bool weekly;
            LimitReading used = SessionLimit.ParseBanner("You've used 93% of your session limit · resets in 2h · View usage", now, out weekly);
            probe.Check("session limit: the banner's \"You've used N% of your session limit\" is read",
                used != null && !weekly && Math.Abs(used.Percent - 93) < 0.01 && used.Source == "banner");
            probe.Check("...and its rounded \"resets in 2h\" becomes the LATEST the reset can be, marked an estimate",
                used != null && used.ResetIsEstimate && used.ResetsAt == now.AddHours(3));
            LimitReading hit = SessionLimit.ParseBanner("You've hit your session limit · resets in 45m · View usage", now, out weekly);
            probe.Check("session limit: \"You've hit your session limit\" reads as 100 %",
                hit != null && !weekly && hit.Percent >= 100 && hit.ResetsAt == now.AddMinutes(46));
            LimitReading week = SessionLimit.ParseBanner("You've used 91% of your weekly limit · resets in 3d · View usage", now, out weekly);
            probe.Check("session limit: the weekly banner is told apart from the session one",
                week != null && weekly && week.ResetsAt == now.AddDays(4));
            probe.Check("session limit: \"Approaching\" carries no figure, so it is no reading",
                SessionLimit.ParseBanner("Approaching session limit · resets in 2h · View usage", now, out weekly) == null);
            probe.Check("session limit: a model's own weekly limit and usage credits are not acted on",
                SessionLimit.ParseBanner("You've used 95% of your weekly Opus limit · resets in 2d · View usage", now, out weekly) == null
                && SessionLimit.ParseBanner("You've used 95% of your usage credit limit · View usage", now, out weekly) == null);
            probe.Check("session limit: a sentence that only CONTAINS the banner's words is not a banner",
                SessionLimit.ParseBanner("He wrote: You've used 93% of your session limit", now, out weekly) == null
                && SessionLimit.ParseBanner("I saw \"You've used 93% of your session limit\" earlier", now, out weekly) == null);
            PanelUsage bannerOnly = SessionLimit.Parse("T7",
                "{\"working\":true,\"input\":true,\"draft\":false,\"prompt\":false,\"info\":null,\"infoAt\":0,\"windows\":null,\"windowsAt\":0,"
                + "\"banner\":\"You've used 92% of your session limit · resets in 1h · View usage\"}", now);
            probe.Check("session limit: the banner is used when the listener has nothing",
                bannerOnly != null && bannerOnly.Session != null && bannerOnly.Session.Source == "banner");

            // ---- choosing the account's reading -----------------------------------------------------
            var stale = new PanelUsage { TargetId = "S", Reachable = true,
                Session = new LimitReading { Percent = 99, ResetsAt = now.AddMinutes(-5), Source = "event", CapturedAt = now.AddHours(-5) } };
            probe.Check("session limit: a reading whose reset has passed describes a window that is over",
                SessionLimit.Best(new List<PanelUsage> { stale }, now, false) == null);
            probe.Check("session limit: an exact reading beats a banner's estimate",
                SessionLimit.Best(new List<PanelUsage> { bannerOnly, fromUpdate }, now, false).Source == "update");

            // ---- the decision ------------------------------------------------------------------------
            var watch = new SessionLimitWatch();
            var idle = new PanelUsage { TargetId = "IDLE", Reachable = true, Working = false, HasInput = true,
                Session = fromEvent.Session };
            var busy = new PanelUsage { TargetId = "BUSY", Reachable = true, Working = true, HasInput = true, Session = fromEvent.Session };
            var low = new PanelUsage { TargetId = "BUSY", Reachable = true, Working = true, HasInput = true, Session = fromUpdate.Session };
            LimitPlan below = watch.Plan(new List<PanelUsage> { low }, now, 90);
            probe.Check("session limit: below the threshold nothing is asked and nothing is said",
                below.SendTo.Count == 0 && !below.NoticeSession);
            LimitPlan over = watch.Plan(new List<PanelUsage> { idle, busy }, now, 90);
            probe.Check("session limit: past the threshold only the WORKING panel is asked",
                over.SendTo.Count == 1 && over.SendTo[0].TargetId == "BUSY" && over.NoticeSession);
            var noReset = new PanelUsage { TargetId = "NR", Reachable = true, Working = true, HasInput = true,
                Session = new LimitReading { Percent = 95, Source = "event", CapturedAt = now } };
            probe.Check("session limit: a figure with no reset time asks nobody, because WHEN to resume is the point",
                new SessionLimitWatch().Plan(new List<PanelUsage> { noReset }, now, 90).SendTo.Count == 0);
            var noBox = new PanelUsage { TargetId = "NB", Reachable = true, Working = true, HasInput = false, Session = fromEvent.Session };
            probe.Check("session limit: a panel with no chat box is not asked",
                new SessionLimitWatch().Plan(new List<PanelUsage> { noBox }, now, 90).SendTo.Count == 0);
            Func<double, PanelUsage> panelAt = percent => new PanelUsage { TargetId = "C", Reachable = true, Working = true, HasInput = true,
                Session = new LimitReading { Percent = percent, ResetsAt = now.AddHours(2), Source = "event", CapturedAt = now } };
            probe.Check("session limit: a threshold below 80 is read as 80",
                new SessionLimitWatch().Plan(new List<PanelUsage> { panelAt(85) }, now, 10).SendTo.Count == 1
                && new SessionLimitWatch().Plan(new List<PanelUsage> { panelAt(79) }, now, 10).SendTo.Count == 0);
            probe.Check("session limit: a threshold above 98 is read as 98",
                new SessionLimitWatch().Plan(new List<PanelUsage> { panelAt(99) }, now, 200).SendTo.Count == 1
                && new SessionLimitWatch().Plan(new List<PanelUsage> { panelAt(97) }, now, 200).SendTo.Count == 0);

            watch.Record("BUSY", "accepted", now.AddHours(2));
            watch.NoticedSession(now.AddHours(2));
            LimitPlan again = watch.Plan(new List<PanelUsage> { idle, busy }, now.AddMinutes(1), 90);
            probe.Check("session limit: a panel that took the message is not asked again in the same window",
                again.SendTo.Count == 0);
            probe.Check("...and the user is told once per window",
                !again.NoticeSession);
            LimitPlan nextWindow = watch.Plan(new List<PanelUsage> {
                new PanelUsage { TargetId = "BUSY", Reachable = true, Working = true, HasInput = true,
                    Session = new LimitReading { Percent = 95, ResetsAt = now.AddHours(7), Source = "event", CapturedAt = now.AddHours(2.5) } } },
                now.AddHours(2.5), 90);
            probe.Check("session limit: after the reset passes, the next window asks again and tells again",
                nextWindow.SendTo.Count == 1 && nextWindow.NoticeSession);

            var retry = new SessionLimitWatch();
            // More of each than the attempt limit, so a prompt or a draft that WAS counted would give the panel up.
            for (int i = 0; i < SessionLimitWatch.MaxAttempts; i++)
            {
                retry.Record("P", "prompt", now.AddHours(2));
                retry.Record("P", "draft", now.AddHours(2));
            }
            var promptPanel = new PanelUsage { TargetId = "P", Reachable = true, Working = true, HasInput = true, Session = fromEvent.Session };
            probe.Check("session limit: a permission prompt or the user's draft is waited out, not counted against the panel",
                retry.Plan(new List<PanelUsage> { promptPanel }, now, 90).SendTo.Count == 1);
            for (int i = 0; i < SessionLimitWatch.MaxAttempts; i++) retry.Record("P", "not-accepted", now.AddHours(2));
            probe.Check("session limit: a panel that never takes the message is left alone after "
                        + SessionLimitWatch.MaxAttempts.ToString(CultureInfo.InvariantCulture) + " tries",
                retry.Plan(new List<PanelUsage> { promptPanel }, now, 90).SendTo.Count == 0);

            var weeklyWatch = new SessionLimitWatch();
            var weeklyHigh = new PanelUsage { TargetId = "W", Reachable = true, Working = true, HasInput = true,
                Weekly = new LimitReading { Percent = 92, ResetsAt = now.AddDays(3), Source = "event", CapturedAt = now } };
            LimitPlan weeklyPlan = weeklyWatch.Plan(new List<PanelUsage> { weeklyHigh }, now, 90);
            probe.Check("session limit: the weekly limit is said, and pauses nobody",
                weeklyPlan.NoticeWeekly && weeklyPlan.SendTo.Count == 0);

            // ---- the message -----------------------------------------------------------------------
            string message = SessionLimit.PauseMessage(fromEvent.Session, now, TimeZoneInfo.Utc);
            probe.Check("session limit: the message says the figure and the reset, in local time with the date",
                message.StartsWith(SessionLimit.Tag, StringComparison.Ordinal) && message.IndexOf("91 %", StringComparison.Ordinal) > 0
                && message.IndexOf("at 17:00 local time on 2026-10-15", StringComparison.Ordinal) > 0);
            probe.Check("...asks for a safe stop and a resume note, and a CronCreate resume five minutes after the reset",
                message.IndexOf("safe point", StringComparison.Ordinal) > 0 && message.IndexOf("resume note", StringComparison.Ordinal) > 0
                && message.IndexOf("CronCreate", StringComparison.Ordinal) > 0
                && message.IndexOf("17:05 local time", StringComparison.Ordinal) > 0);
            probe.Check("session limit: both messages ask for subagents and background tasks to be brought to a safe point",
                message.IndexOf(SessionLimit.SubagentsSentence, StringComparison.Ordinal) > 0
                && SessionLimit.ManualPauseMessage().IndexOf(SessionLimit.SubagentsSentence, StringComparison.Ordinal) > 0);
            string estimated = SessionLimit.PauseMessage(used, now, TimeZoneInfo.Utc);
            probe.Check("session limit: an estimated reset is said as \"by about\", and the resume is after the estimate",
                estimated.IndexOf("resets by about 18:00", StringComparison.Ordinal) > 0
                && estimated.IndexOf("18:05 local time", StringComparison.Ordinal) > 0);
            string manual = SessionLimit.PauseMessage(null, now, TimeZoneInfo.Utc);
            probe.Check("session limit: with no reset time the message pauses and waits, and schedules nothing",
                manual == SessionLimit.ManualPauseMessage() && manual.IndexOf("CronCreate", StringComparison.Ordinal) < 0);

            // ---- the expressions ---------------------------------------------------------------------
            string read = CdpApprover.UsageReadExpressionForSelfTest;
            probe.Check("session limit: the read finds the banner through its own View usage button",
                read.IndexOf("usageLink", StringComparison.Ordinal) > 0 && read.IndexOf("'View usage'", StringComparison.Ordinal) > 0
                && read.IndexOf("closest('[class*=\"banner\"]')", StringComparison.Ordinal) > 0);
            probe.Check("...cuts what it takes from it at 200 characters",
                read.IndexOf(".slice(0, 200)", StringComparison.Ordinal) > 0);
            probe.Check("...keeps only usage-shaped messages in its listener",
                read.IndexOf("m.message.type === 'rate_limit_event'", StringComparison.Ordinal) > 0
                && read.IndexOf("m.request.type === 'panel_usage_update'", StringComparison.Ordinal) > 0);
            probe.Check("...and says whether the chat box is EMPTY without returning what is in it",
                read.IndexOf("draft: !!(input", StringComparison.Ordinal) > 0
                && read.IndexOf("input.textContent,", StringComparison.Ordinal) < 0);
            probe.Check("session limit: the read counts a panel whose subagents are working, through the agent map's label",
                read.IndexOf("/Agents are working/i.test(", StringComparison.Ordinal) > 0
                && read.IndexOf("|| subagents,", StringComparison.Ordinal) > 0);
            probe.Check("WITNESS the read never takes the whole document's text",
                read.IndexOf("body.innerText", StringComparison.Ordinal) < 0 && read.IndexOf("documentElement.textContent", StringComparison.Ordinal) < 0);

            string send = CdpApprover.BuildSendForSelfTest("say \"hi\" </script> \\ ok");
            int keyAt = send.IndexOf("new KeyboardEvent(", StringComparison.Ordinal);
            probe.Check("session limit: the send refuses a permission prompt BEFORE any key is built",
                keyAt > 0 && send.IndexOf("return 'prompt'", StringComparison.Ordinal) > 0
                && send.IndexOf("return 'prompt'", StringComparison.Ordinal) < keyAt);
            probe.Check("...refuses the user's unsent draft before it too",
                send.IndexOf("return 'draft'", StringComparison.Ordinal) > 0 && send.IndexOf("return 'draft'", StringComparison.Ordinal) < keyAt);
            probe.Check("...presses Enter and nothing else",
                send.IndexOf("key: 'Enter'", StringComparison.Ordinal) > 0 && send.IndexOf("Escape", StringComparison.Ordinal) < 0
                && send.IndexOf(".click()", StringComparison.Ordinal) < 0);
            probe.Check("WITNESS the send never takes focus, so it cannot take the keyboard from the user",
                send.IndexOf(".focus(", StringComparison.Ordinal) < 0 && send.IndexOf("execCommand", StringComparison.Ordinal) < 0);
            probe.Check("WITNESS the message travels as one JSON string literal, quotes and all",
                send.IndexOf("__MESSAGE__", StringComparison.Ordinal) < 0
                && send.IndexOf("var text = " + CdpApprover.JsonEncode("say \"hi\" </script> \\ ok") + ";", StringComparison.Ordinal) > 0);
            string confirm = CdpApprover.BuildConfirmForSelfTest("x");
            probe.Check("session limit: a message the panel did not take is taken back out of the box",
                confirm.IndexOf("box.textContent = ''", StringComparison.Ordinal) > 0
                && confirm.IndexOf("return 'not-accepted'", StringComparison.Ordinal) > 0
                && confirm.IndexOf("KeyboardEvent", StringComparison.Ordinal) < 0);

            // ---- the wire, through the fake server ---------------------------------------------------
            var claude = new FakeCdpServer.Target { Id = "CLAUDE1", Url = "vscode-webview://x?" + CdpApprover.ClaudeTargetMarker,
                EvaluateResult = eventJson, SendResult = "sent", ConfirmResult = "accepted" };
            var codex = new FakeCdpServer.Target { Id = "CODEX1", Url = "vscode-webview://y?" + CdpApprover.CodexTargetMarker,
                EvaluateResult = eventJson, SendResult = "sent", ConfirmResult = "accepted" };
            using (var server = new FakeCdpServer(new[] { claude, codex }))
            {
                List<PanelUsage> panels = CdpApprover.ReadUsage(server.Port, 1500, now);
                probe.Check("session limit: the read reaches the Claude panel over the wire",
                    panels.Count == 1 && panels[0].TargetId == "CLAUDE1" && panels[0].Session != null);
                probe.Check("WITNESS the Codex panel is not even attached to: the limit is Claude's",
                    server.AttachCount == 1);
                string result = CdpApprover.SendPause(server.Port, "CLAUDE1", message, 1500, 0);
                probe.Check("session limit: a send that the panel takes reads back as accepted",
                    result == "accepted" && claude.LastSendExpression != null
                    && claude.LastSendExpression.IndexOf("CronCreate", StringComparison.Ordinal) > 0);
                claude.ConfirmResult = "not-accepted";
                probe.Check("...and one it did not take reads back as not-accepted",
                    CdpApprover.SendPause(server.Port, "CLAUDE1", message, 1500, 0) == "not-accepted");
                claude.SendResult = "prompt";
                probe.Check("...and a prompt on screen comes back as prompt, with no confirm attempted",
                    CdpApprover.SendPause(server.Port, "CLAUDE1", message, 1500, 0) == "prompt");
            }
            probe.Check("session limit: a closed port reads as no panels, not as a throw",
                CdpApprover.ReadUsage(1, 300, now).Count == 0);

            // ---- the module's worker step and pane ----------------------------------------------------
            Func<DateTimeOffset> clockWas = LimitClock;
            TimeZoneInfo zoneWas = LimitZone;
            int confirmWas = ConfirmAfterMs;
            try
            {
                LimitClock = () => now;
                LimitZone = TimeZoneInfo.Utc;
                ConfirmAfterMs = 0;
                var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                using (var storage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow"))
                {
                    host.UseStorage("agentflow", storage);
                    host.SettingsFor("agentflow").Set(SettingMode, AgentMode.Off);   // no scan at Init (F037)
                    var module = new AgentFlowModule();
                    module.Init(host);
                    probe.Check("session limit: the switch is OFF until the user turns it on, at 90 %",
                        !module.PauseAtLimit && module.PauseAtPercent == SessionLimit.DefaultThreshold);

                    IReadOnlyDictionary<string, string> shown = Shown(host.OptionsPanes[0]);
                    probe.Check("session limit: the pane shows the switch, the percentage and the two lines",
                        shown.ContainsKey(SettingPauseAtLimit) && shown[SettingPauseAtLimit] == "false"
                        && shown.ContainsKey(SettingPauseAtPercent) && shown[SettingPauseAtPercent] == "90"
                        && shown.ContainsKey("aboutLimitNow") && shown.ContainsKey("aboutLimitHow"));
                    bool hasButton = false;
                    foreach (PaneAction action in host.OptionsPanes[0].Actions)
                        if (action.Label == PauseNowLabel && action.Group == GroupSessionLimit) hasButton = true;
                    probe.Check("session limit: the pane has the Pause working sessions now button in the card", hasButton);
                    module.SavePaneValues(new Dictionary<string, string> { { SettingPauseAtLimit, "true" }, { SettingPauseAtPercent, "85" },
                        { "aboutLimitNow", "prose" } });
                    probe.Check("session limit: the pane saves the switch and the percentage, and not the prose",
                        module.PauseAtLimit && module.PauseAtPercent == 85
                        && host.SettingsFor("agentflow").Get("aboutLimitNow", "missing") == "missing");

                    var panel = new FakeCdpServer.Target { Id = "PANELAB12CD", Url = "vscode-webview://z?" + CdpApprover.ClaudeTargetMarker,
                        EvaluateResult = eventJson, SendResult = "sent", ConfirmResult = "accepted" };
                    using (var server = new FakeCdpServer(new[] { panel }))
                    {
                        LimitOutcome first = module.WatchSessionLimit(server.Port, 90, null);
                        probe.Check("session limit: the worker step asks the working panel and says so in the log",
                            first != null && first.Log.Count == 1 && first.Log[0].IndexOf("panelab1 accepted", StringComparison.Ordinal) > 0);
                        probe.Check("...and tells the user once, with the figure and the reset",
                            first != null && first.Notice != null && first.Notice.IndexOf("91 %", StringComparison.Ordinal) > 0
                            && first.Notice.IndexOf("17:00", StringComparison.Ordinal) > 0
                            && first.Notice.IndexOf("I asked 1 working session", StringComparison.Ordinal) > 0);
                        probe.Check("WITNESS the log line names the panel by its id and carries no message text",
                            first != null && first.Log.Count == 1 && first.Log[0].IndexOf("CronCreate", StringComparison.Ordinal) < 0);
                        probe.Check("session limit: inside the 30-second read interval the step does nothing",
                            module.WatchSessionLimit(server.Port, 90, null) == null);
                        LimitClock = () => now.AddMinutes(1);
                        LimitOutcome second = module.WatchSessionLimit(server.Port, 90, null);
                        probe.Check("session limit: the next read asks nobody again and tells nobody again",
                            second != null && second.Log.Count == 0 && second.Notice == null);
                    }

                    // The last look before typing: the switch read again inside the step, as the press re-reads its
                    // own. A step already running when the user turns the switch off types into nothing.
                    module.SavePaneValues(new Dictionary<string, string> { { SettingPauseAtLimit, "false" } });
                    // A window still open at the later clock, so the only thing that can stop the send is the switch.
                    string lateJson = eventJson.Replace(r, now.AddHours(6).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
                                               .Replace(at, now.AddHours(3).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
                    var late = new FakeCdpServer.Target { Id = "LATE1", Url = "vscode-webview://w?" + CdpApprover.ClaudeTargetMarker,
                        EvaluateResult = lateJson, SendResult = "sent", ConfirmResult = "accepted" };
                    using (var server = new FakeCdpServer(new[] { late }))
                    {
                        LimitClock = () => now.AddHours(3).AddMinutes(1);
                        LimitOutcome afterOff = module.WatchSessionLimit(server.Port, 90, null);
                        probe.Check("session limit: (control) the later window is open and its panel is due to be asked",
                            afterOff != null && afterOff.Notice != null);
                        probe.Check("WITNESS the last look: with the switch turned off the step types into no panel",
                            late.LastSendExpression == null);
                    }
                    module.Shutdown();
                }
            }
            finally
            {
                LimitClock = clockWas;
                LimitZone = zoneWas;
                ConfirmAfterMs = confirmWas;
            }

            var desktop = new Detection { Session = new AgentSession { Agent = "claude", Host = "claude-desktop" } };
            var inVsCode = new Detection { Session = new AgentSession { Agent = "claude", Host = "claude-vscode" } };
            probe.Check("session limit: a live Claude desktop session is counted as one AgentFlow cannot reach, a VS Code one is not",
                OutsideVsCode(new List<Detection> { desktop, inVsCode }) == 1);
            probe.Check("session limit: the button's answer names no message and no reset when nothing was sent",
                DescribePauseNow("No Claude Code session was working, so nothing was sent.", 0, fromEvent.Session, now)
                    == "No Claude Code session was working, so nothing was sent.");
            probe.Check("...and names the reset when something was",
                DescribePauseNow("Asked 1.", 1, fromEvent.Session, now).IndexOf("resume after the reset", StringComparison.Ordinal) > 0);
            string notice = SessionNotice(fromEvent.Session, 1, 2, 1, now);
            probe.Check("session limit: the notice says how many did not take it, and names what it cannot reach",
                notice.IndexOf("1 did not take the message yet", StringComparison.Ordinal) > 0
                && notice.IndexOf("1 Claude session outside VS Code is yours to pause", StringComparison.Ordinal) > 0);
            return true;
        }
    }
}
