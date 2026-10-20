using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using DesktopAICompanion.ModuleKit;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentFlow
{
    public sealed partial class AgentFlowModule
    {
        /// <summary>
        /// Codex's usage limits, read only (1.10.0): the rate_limits shapes measured over this box's rollouts on 2026-10-20,
        /// the fold through both transcript readers, the Status card's row, and the full buildout's threshold decision,
        /// which nothing acts on yet.
        /// </summary>
        private static bool SelfCheckCodexLimits(SelfTestProbe probe)
        {
            var now = new DateTimeOffset(2026, 10, 20, 19, 0, 0, TimeSpan.Zero);
            var weeklyReset = new DateTimeOffset(2026, 10, 27, 9, 14, 0, TimeSpan.Zero);
            DateTimeOffset sessionReset = now.AddHours(2);
            string wk = weeklyReset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            string ss = sessionReset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

            // ---- the shapes, as the rollouts carry them ----------------------------------------------
            // The personal Pro account in Codex desktop: one weekly window, in the PRIMARY slot, at 1.0.
            Func<string, string> proAt = used => "{\"limit_id\":\"codex\",\"limit_name\":null,"
                + "\"primary\":{\"used_percent\":" + used + ",\"window_minutes\":10080,\"resets_at\":" + wk + "},\"secondary\":null,"
                + "\"credits\":{\"has_credits\":true,\"unlimited\":false,\"balance\":\"62500.0000000000\"},\"individual_limit\":null,"
                + "\"spend_control_reached\":null,\"plan_type\":\"pro\",\"rate_limit_reached_type\":null}";
            // The Business workspace the same day, in VS Code and the desktop app alike: no window at all.
            const string business = "{\"limit_id\":\"codex\",\"limit_name\":null,\"primary\":null,\"secondary\":null,"
                + "\"credits\":{\"has_credits\":true,\"unlimited\":false,\"balance\":null},\"individual_limit\":null,"
                + "\"spend_control_reached\":null,\"plan_type\":\"business\",\"rate_limit_reached_type\":null}";

            CodexLimitReading fromPro = CodexLimitsFromJson(proAt("1.0"), now);
            probe.Check("codex limits: the Pro account's used_percent 1.0 reads as 1 %, not as a fraction's 100 %",
                fromPro != null && fromPro.Weekly != null && Math.Abs(fromPro.Weekly.Percent - 1) < 0.01);
            probe.Check("codex limits: a 10080-minute window in the PRIMARY slot is the weekly one, told by its length",
                fromPro != null && fromPro.Weekly != null && fromPro.Session == null);
            probe.Check("...with its reset from epoch seconds, and its plan named",
                fromPro != null && fromPro.Weekly != null && fromPro.Weekly.ResetsAt == weeklyReset
                && fromPro.Plan == "Pro" && !fromPro.Reached);
            CodexLimitReading fromBusiness = CodexLimitsFromJson(business, now);
            probe.Check("codex limits: the Business workspace's null windows are no window at all, and its plan is named",
                fromBusiness != null && fromBusiness.Session == null && fromBusiness.Weekly == null && fromBusiness.Plan == "Business");
            CodexLimitReading both = CodexLimitsFromJson("{\"primary\":{\"used_percent\":40.5,\"window_minutes\":10080,\"resets_at\":" + wk + "},"
                + "\"secondary\":{\"used_percent\":92.0,\"window_minutes\":300,\"resets_at\":" + ss + "},\"plan_type\":\"plus\"}", now);
            probe.Check("codex limits: a 300-minute window is the session window, in whichever slot it comes",
                both != null && both.Session != null && Math.Abs(both.Session.Percent - 92) < 0.01 && both.Session.ResetsAt == sessionReset
                && both.Weekly != null && Math.Abs(both.Weekly.Percent - 40.5) < 0.01 && both.Plan == "Plus");
            CodexLimitReading odd = CodexLimitsFromJson("{\"primary\":{\"used_percent\":50,\"window_minutes\":60,\"resets_at\":" + ss + "},"
                + "\"plan_type\":\"quantum\",\"rate_limit_reached_type\":\"primary\"}", now);
            probe.Check("codex limits: a window of another length is filed as neither, and an unlisted plan names nothing",
                odd != null && odd.Session == null && odd.Weekly == null && odd.Plan == null);
            probe.Check("codex limits: rate_limit_reached_type set reads as a limit reached",
                odd != null && odd.Reached);
            probe.Check("codex limits: rate_limits that are not an object are no reading",
                CodexLimitsFromJson("null", now) == null && CodexLimitsFromJson("[1]", now) == null);

            // ---- the fold, through both readers ------------------------------------------------------
            string head = "{\"timestamp\":\"2026-10-20T18:00:00.000Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"x\",\"originator\":\"Codex Desktop\"}}\n"
                + "{\"timestamp\":\"2026-10-20T18:01:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null,\"rate_limits\":" + proAt("1.0") + "}}\n";
            string tail = "{\"timestamp\":\"2026-10-20T18:02:00.000Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"call_id\":\"c1\",\"name\":\"shell\"}}\n"
                + "{\"timestamp\":\"2026-10-20T18:03:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\","
                + "\"info\":{\"total_token_usage\":{\"input_tokens\":5}},\"rate_limits\":" + proAt("3.0") + "}}\n"
                + "{\"timestamp\":\"2026-10-20T18:04:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null,\"rate_limits\":null}}\n";
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-agentflow-codex-limits-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".jsonl");
            try
            {
                var utf8 = new System.Text.UTF8Encoding(false);
                System.IO.File.WriteAllText(path, head, utf8);
                var cursor = new TranscriptCursor(path, TranscriptReader.AgentCodex);
                string reason;
                AgentSession first = cursor.Advance(out reason);
                probe.Check("codex limits: the rollout's rate_limits reach the session on the cursor's first read",
                    first != null && first.CodexLimits != null && first.CodexLimits.Weekly != null
                    && Math.Abs(first.CodexLimits.Weekly.Percent - 1) < 0.01);
                System.IO.File.AppendAllText(path, tail, utf8);
                AgentSession second = cursor.Advance(out reason);
                AgentSession whole = TranscriptReader.ReadCodex(path);
                probe.Check("codex limits: the NEWEST rate_limits win, through the cursor and the whole-file reader alike",
                    second != null && second.CodexLimits != null && second.CodexLimits.Weekly != null
                    && Math.Abs(second.CodexLimits.Weekly.Percent - 3) < 0.01
                    && whole.CodexLimits != null && whole.CodexLimits.Weekly != null
                    && Math.Abs(whole.CodexLimits.Weekly.Percent - 3) < 0.01
                    && second.CodexLimits.CapturedAt == new DateTimeOffset(2026, 10, 20, 18, 3, 0, TimeSpan.Zero));
                probe.Check("...a token_count whose rate_limits is null leaves the last reading standing",
                    reason == null && whole.CodexLimits != null && whole.CodexLimits.Plan == "Pro");
                probe.Check("WITNESS the fold still reads the rest of the rollout around the figures",
                    whole.Host == "Codex Desktop" && whole.Outstanding.Count == 1 && whole.SawAnyCall);
            }
            finally { try { System.IO.File.Delete(path); } catch { } }
            var replaced = new FoldState { CodexLimits = fromPro };
            replaced.Reset();
            probe.Check("codex limits: a replaced file's figures go with it", replaced.CodexLimits == null);

            // ---- which sessions, and the Status card's row ---------------------------------------------
            var inVsCode = new Detection { Session = new AgentSession { Agent = "codex", Host = "codex_vscode", SessionId = "r1", CodexLimits = fromBusiness } };
            var silent = new Detection { Session = new AgentSession { Agent = "codex", Host = "Codex Desktop", SessionId = "r2" } };
            var claudeOne = new Detection { Session = new AgentSession { Agent = "claude", Host = "claude-vscode", SessionId = "s1", CodexLimits = fromPro } };
            List<CodexSessionLimit> found = CodexLimits.FromSessions(new List<Detection> { inVsCode, silent, claudeOne, null });
            probe.Check("codex limits: only Codex sessions that reported are read, each named by its app",
                found.Count == 1 && found[0].SessionId == "r1" && found[0].App == AgentHosts.CodexVsCode);

            var vs1 = new CodexSessionLimit { SessionId = "a", App = AgentHosts.CodexVsCode, Reading = fromBusiness };
            var vs2 = new CodexSessionLimit { SessionId = "b", App = AgentHosts.CodexVsCode, Reading = fromBusiness };
            var desk = new CodexSessionLimit { SessionId = "c", App = AgentHosts.CodexDesktop, Reading = fromPro };
            string described = CodexLimits.Describe(new List<CodexSessionLimit> { vs1, vs2, desk }, 4, now, TimeZoneInfo.Utc, false, 88);
            probe.Check("codex limits: the row groups by app and plan, and says when a workspace sends no window",
                described.StartsWith("2 sessions in " + AgentHosts.CodexVsCode + " on Business: no usage window reported; ", StringComparison.Ordinal));
            probe.Check("...and gives the Pro account's weekly figure with its reset day",
                described.IndexOf("1 session in " + AgentHosts.CodexDesktop + " on Pro: weekly 1 % · resets 2026-10-27 09:14.", StringComparison.Ordinal) > 0);
            probe.Check("...counts the session that has not reported yet, and says none of it is acted on",
                described.IndexOf(" 1 more session has not reported yet. ", StringComparison.Ordinal) > 0
                && described.EndsWith(CodexLimits.ReadOnlyNote, StringComparison.Ordinal));
            var older = new CodexSessionLimit { SessionId = "d", App = AgentHosts.CodexDesktop, Reading = new CodexLimitReading
            {
                Plan = "Pro", CapturedAt = now.AddMinutes(-30),
                Weekly = new LimitReading { Percent = 50, ResetsAt = weeklyReset, Source = "codex", CapturedAt = now.AddMinutes(-30) },
            } };
            probe.Check("codex limits: of one app and plan's sessions the NEWEST figure is shown, whichever the scan listed first",
                CodexLimits.Describe(new List<CodexSessionLimit> { older, desk }, 2, now, TimeZoneInfo.Utc, false, 88)
                    .StartsWith("2 sessions in " + AgentHosts.CodexDesktop + " on Pro: weekly 1 %", StringComparison.Ordinal));
            var over = new CodexSessionLimit { SessionId = "e", App = AgentHosts.CodexDesktop, Reading = new CodexLimitReading
            {
                Plan = "Pro", CapturedAt = now.AddDays(-8),
                Weekly = new LimitReading { Percent = 99, ResetsAt = now.AddMinutes(-1), Source = "codex", CapturedAt = now.AddDays(-8) },
            } };
            probe.Check("codex limits: a window whose reset has passed is over, and not shown",
                CodexLimits.Describe(new List<CodexSessionLimit> { over }, 1, now, TimeZoneInfo.Utc, false, 88)
                    .StartsWith("1 session in " + AgentHosts.CodexDesktop + " on Pro: no usage window reported.", StringComparison.Ordinal));
            probe.Check("codex limits: sessions with nothing reported yet are counted, not left out",
                CodexLimits.Describe(new List<CodexSessionLimit>(), 2, now, TimeZoneInfo.Utc, false, 88)
                    == "2 Codex sessions active, none has reported its usage yet. " + CodexLimits.ReadOnlyNote);

            // ---- the full buildout's decision, which nothing acts on --------------------------------------
            probe.Check("codex limits: the threshold is held against a session's OWN five-hour window",
                CodexLimits.OverThreshold(both, now, 88) && !CodexLimits.OverThreshold(both, now, 95));
            probe.Check("...never against a weekly window, a passed reset, or a figure with no reset",
                !CodexLimits.OverThreshold(new CodexLimitReading { Weekly = new LimitReading { Percent = 99, ResetsAt = weeklyReset } }, now, 88)
                && !CodexLimits.OverThreshold(both, sessionReset.AddMinutes(1), 88)
                && !CodexLimits.OverThreshold(new CodexLimitReading { Session = new LimitReading { Percent = 99 } }, now, 88));
            var pastIt = new List<CodexSessionLimit> { new CodexSessionLimit { SessionId = "f", App = AgentHosts.CodexVsCode, Reading = both } };
            probe.Check("codex limits: with pause & resume on, a session past the threshold is said, and said not to be paused",
                CodexLimits.Describe(pastIt, 1, now, TimeZoneInfo.Utc, true, 88)
                    == "1 session in " + AgentHosts.CodexVsCode + " on Plus: session 92 % · resets 21:00, weekly 40 % · resets 2026-10-27 09:14"
                       + " · past your 88 % pause threshold, not paused. " + CodexLimits.ReadOnlyNote);
            probe.Check("WITNESS ...and with it off, the threshold is not mentioned",
                CodexLimits.Describe(pastIt, 1, now, TimeZoneInfo.Utc, false, 88).IndexOf("threshold", StringComparison.Ordinal) < 0);

            var detections = new List<Detection> { inVsCode, silent, claudeOne };
            probe.Check("status: the Codex usage row reads from the scan, with no port at all",
                CodexStatusLine(detections, true, now, false, 88)
                    == "1 session in " + AgentHosts.CodexVsCode + " on Business: no usage window reported. 1 more session has not reported yet. "
                       + CodexLimits.ReadOnlyNote);
            probe.Check("status: with Watch Codex off the row says so, and with no Codex session it says that",
                CodexStatusLine(detections, false, now, false, 88) == "Not read: Watch Codex is off."
                && CodexStatusLine(new List<Detection> { claudeOne }, true, now, false, 88) == "No Codex session active in the last 15 minutes.");

            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            using (var storage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow"))
            {
                host.UseStorage("agentflow", storage);
                host.SettingsFor("agentflow").Set(SettingMode, AgentMode.Off);   // no scan at Init (F037)
                var module = new AgentFlowModule();
                module.Init(host);
                module.Apply(detections);
                probe.Check("status: each scan's results refresh the Codex usage row",
                    module._codexLine.StartsWith("1 session in " + AgentHosts.CodexVsCode + " on Business", StringComparison.Ordinal));
                probe.Check("status: ...which the card shows as not read while AgentFlow is Off",
                    Shown(host.OptionsPanes[0])["aboutStatusCodex"] == "Not read: AgentFlow is Off.");
                module.Shutdown();
            }
            return true;
        }

        /// <summary>One rate_limits object from its JSON text, for the self-test.</summary>
        private static CodexLimitReading CodexLimitsFromJson(string json, DateTimeOffset at)
        {
            using (JsonDocument document = JsonDocument.Parse(json))
                return CodexLimits.Parse(document.RootElement, at);
        }
    }
}
