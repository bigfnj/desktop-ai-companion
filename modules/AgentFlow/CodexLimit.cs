using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>
    /// What one Codex session's rollout last said about its account's usage limits (1.10.0). Numbers and a plan name
    /// only: the record it comes from carries the session's token counts beside it, and those are not read.
    ///
    /// REPLACED, NEVER MUTATED. The fold swaps in a new reading for each record, so a session snapshot can share this
    /// object with the cursor that is still folding, and the UI thread can read it while the poll worker moves on.
    /// </summary>
    internal sealed class CodexLimitReading
    {
        /// <summary>The account's plan in words written here ("Business", "Pro"), or null for a plan this module does not
        /// list. The raw value is not kept, so nothing read off disk reaches the pane or the log as it stands.</summary>
        public string Plan;
        /// <summary>The five-hour window, when the server sent one. No rollout measured on 2026-10-20 carried one.</summary>
        public LimitReading Session;
        /// <summary>The seven-day window, when the server sent one.</summary>
        public LimitReading Weekly;
        /// <summary>The server said a limit is reached (`rate_limit_reached_type` was set).</summary>
        public bool Reached;
        /// <summary>When the rollout recorded it.</summary>
        public DateTimeOffset CapturedAt;
    }

    /// <summary>One live Codex session's reading, with the app it runs in. Built from a scan's detections.</summary>
    internal sealed class CodexSessionLimit
    {
        public string SessionId = "";
        /// <summary>The app, in words from <see cref="AgentHosts"/>, or "Codex (app not named)".</summary>
        public string App = "";
        public CodexLimitReading Reading;
    }

    /// <summary>
    /// Codex's usage limits, READ ONLY (1.10.0, the owner's call of 2026-10-20: "put the scaffolding to read codex, when
    /// or if limits are imposed it will accelerate the full buildout").
    ///
    /// WHERE THEY COME FROM. Not the Codex panel: Codex writes them into its own rollout on every token_count event,
    /// {type:"event_msg", payload:{type:"token_count", info:{...token counts}, rate_limits:{limit_id, plan_type, primary,
    /// secondary, credits, rate_limit_reached_type, ...}}}, and the transcript reader already tails that file. So the
    /// figure is PER SESSION and per account, which is what pausing each Codex session on its own limit will need, and it
    /// needs no debugging port.
    ///
    /// WHAT THERE WAS TO READ, over this box's 34 October rollouts on 2026-10-20. The Business workspace (22 sessions in
    /// VS Code, 2 in the desktop app) sends primary and secondary as null, so it has no window to hold a threshold
    /// against. The personal Pro account in the desktop app (10 sessions) sends one 10080-minute window, in the PRIMARY
    /// slot. No rollout carried a 300-minute window. So a window is told apart by its LENGTH, never by its slot: read by
    /// slot, the Pro account's weekly window would be called its session window.
    ///
    /// NOTHING HERE PAUSES ANYTHING. <see cref="OverThreshold"/> is the full buildout's decision, written and tested now so
    /// it is ready. The one thing that types into a panel, <see cref="CdpApprover.SendPause"/>, refuses every target that
    /// is not a Claude Code panel, and letting it reach Codex is a decision of its own: Escape in the Codex panel is Deny.
    /// </summary>
    internal static class CodexLimits
    {
        internal const int SessionWindowMinutes = 300;
        internal const int WeeklyWindowMinutes = 10080;

        /// <summary>The sentence's last words: the pause reaches Claude Code only, so the Codex row says so.</summary>
        internal const string ReadOnlyNote = "Read only: AgentFlow pauses Claude Code sessions only.";

        // The plan as Codex spells it, to words written here. "business" and "pro" were read on this box on 2026-10-20;
        // the others are ChatGPT's other plan names, listed so that a reading on another account names its plan rather
        // than none. Ordinal, because it is an identifier the server writes, not a word a person types.
        private static readonly string[][] Plans =
        {
            new[] { "business", "Business" },
            new[] { "pro", "Pro" },
            new[] { "plus", "Plus" },
            new[] { "team", "Team" },
            new[] { "enterprise", "Enterprise" },
            new[] { "edu", "Edu" },
            new[] { "free", "Free" },
        };

        /// <summary>The plan's name, or null when this module does not list it.</summary>
        internal static string PlanName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            foreach (string[] plan in Plans)
                if (string.Equals(plan[0], raw, StringComparison.Ordinal)) return plan[1];
            return null;
        }

        /// <summary>
        /// One rate_limits object, as the rollout writes it. Null when it is not an object. Pure, so the self-test drives it
        /// with the shapes measured on 2026-10-20.
        /// </summary>
        internal static CodexLimitReading Parse(JsonElement rateLimits, DateTimeOffset at)
        {
            if (rateLimits.ValueKind != JsonValueKind.Object) return null;
            var reading = new CodexLimitReading { Plan = PlanName(Str(rateLimits, "plan_type")), CapturedAt = at };
            Window(rateLimits, "primary", at, reading);
            Window(rateLimits, "secondary", at, reading);
            JsonElement reached;
            reading.Reached = rateLimits.TryGetProperty("rate_limit_reached_type", out reached)
                              && reached.ValueKind != JsonValueKind.Null;
            return reading;
        }

        /// <summary>One slot's window, filed by its length. A length that is neither five hours nor seven days is not
        /// filed at all: there is no Claude window it corresponds to, and no threshold it could be held against.</summary>
        private static void Window(JsonElement rateLimits, string slot, DateTimeOffset at, CodexLimitReading into)
        {
            JsonElement window;
            if (!rateLimits.TryGetProperty(slot, out window) || window.ValueKind != JsonValueKind.Object) return;
            double used, minutes;
            if (!Number(window, "used_percent", out used) || !Number(window, "window_minutes", out minutes)) return;
            var reading = new LimitReading { Percent = Percent(used), ResetsAt = Epoch(window, "resets_at"), Source = "codex", CapturedAt = at };
            if (minutes == SessionWindowMinutes) into.Session = reading;
            else if (minutes == WeeklyWindowMinutes) into.Weekly = reading;
        }

        /// <summary>
        /// used_percent is ALREADY a percentage: the Pro account's 1.0 is one percent. Never <see cref="SessionLimit.ToPercent"/>,
        /// which takes anything up to 1.5 as Claude's fraction and would read that 1 % as 100 %.
        /// </summary>
        internal static double Percent(double used)
        {
            if (double.IsNaN(used) || used < 0) return 0;
            return Math.Min(100, used);
        }

        /// <summary>
        /// Every live Codex session that has reported a reading, from one scan's detections. Codex sessions that have not
        /// written a token_count yet are not here; <see cref="Describe"/> counts them from the detections.
        /// </summary>
        internal static List<CodexSessionLimit> FromSessions(IEnumerable<Detection> live)
        {
            var found = new List<CodexSessionLimit>();
            if (live == null) return found;
            foreach (Detection detection in live)
            {
                AgentSession session = detection == null ? null : detection.Session;
                if (session == null || session.Agent != TranscriptReader.AgentCodex || session.CodexLimits == null) continue;
                found.Add(new CodexSessionLimit
                {
                    SessionId = session.SessionId ?? "",
                    App = AgentHosts.NameOf(session) ?? "Codex (app not named)",
                    Reading = session.CodexLimits,
                });
            }
            return found;
        }

        /// <summary>A window whose reset has passed describes a window that is over, as for Claude's.</summary>
        internal static LimitReading Live(LimitReading window, DateTimeOffset now)
        {
            if (window == null) return null;
            return window.ResetsAt.HasValue && window.ResetsAt.Value <= now ? null : window;
        }

        /// <summary>
        /// The full buildout's decision, for when a Codex account sends a five-hour window: this session's OWN window is
        /// past the threshold and its reset is known, as the Claude watch requires. Nothing sends on it yet.
        /// </summary>
        internal static bool OverThreshold(CodexLimitReading reading, DateTimeOffset now, int threshold)
        {
            threshold = Math.Max(SessionLimit.MinThreshold, Math.Min(SessionLimit.MaxThreshold, threshold));
            LimitReading session = reading == null ? null : Live(reading.Session, now);
            return session != null && session.ResetsAt.HasValue && session.Percent >= threshold;
        }

        /// <summary>
        /// The Status card's Codex row: each app and plan's newest reading, how many sessions have not reported yet, and that
        /// none of it is acted on. <paramref name="codexActive"/> is the scan's count of live Codex sessions. Pure.
        /// </summary>
        internal static string Describe(IList<CodexSessionLimit> sessions, int codexActive, DateTimeOffset now, TimeZoneInfo zone,
                                        bool pauseOn, int threshold)
        {
            zone = zone ?? TimeZoneInfo.Local;
            int reported = sessions == null ? 0 : sessions.Count;
            if (codexActive <= 0 && reported == 0) return "No Codex session active.";
            if (reported == 0)
                return Count(codexActive, "Codex session") + " active, none has reported its usage yet. " + ReadOnlyNote;

            // One part per app and plan, in the order the scan found them, each carrying that group's NEWEST reading.
            var order = new List<string>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var newest = new Dictionary<string, CodexSessionLimit>(StringComparer.Ordinal);
            foreach (CodexSessionLimit entry in sessions)
            {
                if (entry == null || entry.Reading == null) continue;
                string key = entry.App + "\n" + (entry.Reading.Plan ?? "");
                int count;
                if (!counts.TryGetValue(key, out count)) order.Add(key);
                counts[key] = count + 1;
                CodexSessionLimit have;
                if (!newest.TryGetValue(key, out have) || entry.Reading.CapturedAt > have.Reading.CapturedAt) newest[key] = entry;
            }
            var parts = new List<string>();
            foreach (string key in order)
            {
                CodexSessionLimit entry = newest[key];
                CodexLimitReading reading = entry.Reading;
                string part = Count(counts[key], "session") + " in " + entry.App
                              + (reading.Plan != null ? " on " + reading.Plan : " on an unlisted plan") + ": ";
                var windows = new List<string>();
                LimitReading session = Live(reading.Session, now), weekly = Live(reading.Weekly, now);
                if (session != null) windows.Add("session " + Figure(session, now, zone));
                if (weekly != null) windows.Add("weekly " + Figure(weekly, now, zone));
                part += windows.Count > 0 ? string.Join(", ", windows.ToArray()) : "no usage window reported";
                if (reading.Reached) part += " · a limit is reached";
                if (pauseOn && OverThreshold(reading, now, threshold))
                    part += " · past your " + threshold.ToString(CultureInfo.InvariantCulture) + " % pause threshold, not paused";
                parts.Add(part);
            }
            int unreported = codexActive - reported;
            string line = string.Join("; ", parts.ToArray()) + ".";
            if (unreported > 0)
                line += " " + Count(unreported, "more session") + (unreported == 1 ? " has" : " have") + " not reported yet.";
            return line + " " + ReadOnlyNote;
        }

        /// <summary>"1 % · resets 2026-10-27 09:14", or the clock alone when the reset is today.</summary>
        private static string Figure(LimitReading window, DateTimeOffset now, TimeZoneInfo zone)
        {
            string text = SessionLimit.Whole(window.Percent) + " %";
            if (!window.ResetsAt.HasValue) return text;
            DateTimeOffset local = TimeZoneInfo.ConvertTime(window.ResetsAt.Value, zone);
            DateTimeOffset today = TimeZoneInfo.ConvertTime(now, zone);
            return text + " · resets " + local.ToString(local.Date == today.Date ? "HH:mm" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        private static string Count(int n, string noun)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");
        }

        private static string Str(JsonElement element, string name)
        {
            JsonElement value;
            if (!element.TryGetProperty(name, out value) || value.ValueKind != JsonValueKind.String) return null;
            return value.GetString();
        }

        private static bool Number(JsonElement element, string name, out double number)
        {
            number = 0;
            JsonElement value;
            if (!element.TryGetProperty(name, out value) || value.ValueKind != JsonValueKind.Number) return false;
            return value.TryGetDouble(out number) && !double.IsNaN(number) && !double.IsInfinity(number);
        }

        /// <summary>Epoch SECONDS, as resets_at travels. Null when absent or out of any sane range.</summary>
        private static DateTimeOffset? Epoch(JsonElement element, string name)
        {
            double seconds;
            if (!Number(element, name, out seconds) || seconds < 1e9 || seconds > 1e11) return null;
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000.0));
        }
    }
}
