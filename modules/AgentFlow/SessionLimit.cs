using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>
    /// One usage figure for one limit window: how much of it is used and when it resets.
    /// </summary>
    internal sealed class LimitReading
    {
        /// <summary>0 to 100.</summary>
        public double Percent;
        /// <summary>When the window resets. Null when the panel did not say.</summary>
        public DateTimeOffset? ResetsAt;
        /// <summary>
        /// True when <see cref="ResetsAt"/> came from the banner's rounded "resets in 2h", and is then the LATEST the
        /// reset can be (the panel floors to the unit), so a resume scheduled for it lands after the reset, never before.
        /// </summary>
        public bool ResetIsEstimate;
        /// <summary>"event" (a rate_limit_event the panel received), "update" (a panel_usage_update) or "banner".</summary>
        public string Source = "";
        /// <summary>When the panel received it, for choosing the newest of several panels' readings.</summary>
        public DateTimeOffset CapturedAt;
    }

    /// <summary>What one read of one Claude Code panel found. Never carries the session's title or any chat text.</summary>
    internal sealed class PanelUsage
    {
        public string TargetId = "";
        /// <summary>The conversation was reachable inside the webview at all.</summary>
        public bool Reachable;
        /// <summary>The panel shows its Stop button (the agent is mid-turn), or its agent map says subagents are working:
        /// either way the session is spending, which is when a pause is worth asking for.</summary>
        public bool Working;
        /// <summary>The agent map says subagents are working (1.6.1). Said in the log, so "working" can be told apart.</summary>
        public bool Subagents;
        public bool HasInput;
        /// <summary>The chat box holds text the user typed and has not sent. Never overwritten.</summary>
        public bool HasDraft;
        /// <summary>A permission card is on screen. Nothing is sent then: Enter must never be what answers a prompt.</summary>
        public bool PromptUp;
        /// <summary>The five-hour session window, or null.</summary>
        public LimitReading Session;
        /// <summary>The seven-day window, or null.</summary>
        public LimitReading Weekly;
    }

    /// <summary>
    /// The session-limit watch (agentflow 1.6.0, the owner's request of 2026-10-15): when the Claude session limit passes
    /// a threshold, ask every Claude Code panel that is WORKING to stop at a safe point and schedule its own resume for
    /// after the reset.
    ///
    /// WHERE THE FIGURES COME FROM, AND WHY NOT THE STATUS LINE. The plan was Claude Code's status-line command, whose
    /// JSON carries rate_limits. Measured 2026-10-15 against extension 2.1.300: the VS Code panel never runs a status
    /// line (the setting appears only in its schema; a temporary one ran zero times over ten turns). The panel does
    /// receive the figures: the CLI emits a rate_limit_event when the limit's status changes, the extension forwards it
    /// to the webview as {type:"from-extension", message:{type:"io_message", message:{type:"rate_limit_event",
    /// rate_limit_info:{status, rateLimitType, utilization, resetsAt, unifiedWindows:{five_hour:{utilization, resetsAt},
    /// seven_day:{...}}}}}}, and also sends a panel_usage_update request carrying unifiedWindows after sign-in. The
    /// panel then draws a banner, "You've used 91% of your session limit · resets in 2h · View usage", once usage is at
    /// least 70 % and the status is not plain "allowed".
    ///
    /// So the read does two things. It keeps a listener in the webview that holds ONLY the newest usage-shaped message
    /// (a few numbers; never a chat message), because the event carries the exact reset time and arrives only on a
    /// change. And it reads the banner, anchored on the banner's own "View usage" button, because the banner persists
    /// while a listener installed after the event has nothing. Never the banner's words found anywhere else: a
    /// conversation can contain the same sentence (this feature's own design session did).
    ///
    /// Utilization arrives as a fraction (the webview compares it to 0.7 and 0.99) and resetsAt in epoch seconds.
    /// </summary>
    internal static class SessionLimit
    {
        internal const int DefaultThreshold = 90;
        internal const int MinThreshold = 80;
        internal const int MaxThreshold = 98;

        /// <summary>The resume is scheduled this long after the reset, so it does not land on the boundary itself.</summary>
        internal static readonly TimeSpan ResumeAfterReset = TimeSpan.FromMinutes(5);

        /// <summary>How often the panels are read while the watch is on. The tick is 10 s; usage moves slower than that.</summary>
        internal static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(30);

        /// <summary>The message's opening tag, so a session's transcript shows where the request came from.</summary>
        internal const string Tag = "[AgentFlow]";

        // ---- reading the panel's JSON ------------------------------------------------------------

        /// <summary>
        /// One panel's read, from the read expression's JSON. Null when the JSON is not the expected shape. Pure, so the
        /// self-test drives it with the shapes measured on 2026-10-15 without an editor.
        /// </summary>
        internal static PanelUsage Parse(string targetId, string json, DateTimeOffset now)
        {
            if (string.IsNullOrEmpty(json)) return null;
            if (string.Equals(json, "unreachable", StringComparison.Ordinal))
                return new PanelUsage { TargetId = targetId ?? "", Reachable = false };
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return null;
                    var panel = new PanelUsage
                    {
                        TargetId = targetId ?? "",
                        Reachable = true,
                        Working = Bool(root, "working"),
                        Subagents = Bool(root, "subagents"),
                        HasInput = Bool(root, "input"),
                        HasDraft = Bool(root, "draft"),
                        PromptUp = Bool(root, "prompt"),
                    };

                    // The listener's newest message: the windows (both limits), then the event's own figure for its type.
                    JsonElement windows;
                    DateTimeOffset windowsAt = Stamp(root, "windowsAt", now);
                    if (root.TryGetProperty("windows", out windows) && windows.ValueKind == JsonValueKind.Object)
                    {
                        panel.Session = Window(windows, "five_hour", "update", windowsAt);
                        panel.Weekly = Window(windows, "seven_day", "update", windowsAt);
                    }
                    JsonElement info;
                    DateTimeOffset infoAt = Stamp(root, "infoAt", now);
                    if (root.TryGetProperty("info", out info) && info.ValueKind == JsonValueKind.Object)
                    {
                        // The event's windows are as new as the event, so they replace an older update's.
                        JsonElement eventWindows;
                        if (info.TryGetProperty("windows", out eventWindows) && eventWindows.ValueKind == JsonValueKind.Object)
                        {
                            panel.Session = Newer(panel.Session, Window(eventWindows, "five_hour", "event", infoAt));
                            panel.Weekly = Newer(panel.Weekly, Window(eventWindows, "seven_day", "event", infoAt));
                        }
                        string type = Str(info, "type");
                        double utilization;
                        if (Number(info, "utilization", out utilization))
                        {
                            var own = new LimitReading
                            {
                                Percent = ToPercent(utilization),
                                ResetsAt = Epoch(info, "resetsAt"),
                                Source = "event",
                                CapturedAt = infoAt,
                            };
                            if (type == "five_hour") panel.Session = Newer(panel.Session, own);
                            else if (type == "seven_day") panel.Weekly = Newer(panel.Weekly, own);
                        }
                        else if (string.Equals(Str(info, "status"), "rejected", StringComparison.Ordinal))
                        {
                            // The limit was hit: no utilization travels with it, and none is needed.
                            var hit = new LimitReading { Percent = 100, ResetsAt = Epoch(info, "resetsAt"), Source = "event", CapturedAt = infoAt };
                            if (type == "five_hour") panel.Session = Newer(panel.Session, hit);
                            else if (type == "seven_day") panel.Weekly = Newer(panel.Weekly, hit);
                        }
                    }

                    // The banner, when the listener had nothing newer for that window.
                    string banner = Str(root, "banner");
                    if (banner.Length > 0)
                    {
                        bool weekly;
                        LimitReading fromBanner = ParseBanner(banner, now, out weekly);
                        if (fromBanner != null)
                        {
                            if (weekly) { if (panel.Weekly == null) panel.Weekly = fromBanner; }
                            else if (panel.Session == null) panel.Session = fromBanner;
                        }
                    }
                    return panel;
                }
            }
            catch (JsonException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (FormatException) { return null; }
        }

        private static readonly Regex BannerPattern = new Regex(
            @"^(?:You've used (?<pct>\d{1,3})% of your (?<limit>[^·]+?)|You've hit your (?<hit>[^·]+?)|Approaching (?<near>[^·]+?))"
            + @"(?: · resets (?<when>in \d{1,4}[mhd]|soon))?(?: ·|$)",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// The banner's sentence, as the panel words it (webview Fl0, extension 2.1.300): "You've used 91% of your session
        /// limit · resets in 2h", "You've hit your session limit · resets in 45m", "Approaching weekly limit · resets in 3d".
        /// Null for anything else, and for "Approaching" (no figure to compare with a threshold) and for the limits this
        /// watch does not act on (usage credits, a model's own weekly limit). <paramref name="weekly"/> says which window.
        /// </summary>
        internal static LimitReading ParseBanner(string text, DateTimeOffset now, out bool weekly)
        {
            weekly = false;
            if (string.IsNullOrEmpty(text)) return null;
            Match match = BannerPattern.Match(text.Trim());
            if (!match.Success) return null;
            string limit = match.Groups["limit"].Success ? match.Groups["limit"].Value
                         : match.Groups["hit"].Success ? match.Groups["hit"].Value : match.Groups["near"].Value;
            limit = limit.Trim();
            if (string.Equals(limit, "session limit", StringComparison.Ordinal)) weekly = false;
            else if (string.Equals(limit, "weekly limit", StringComparison.Ordinal)) weekly = true;
            else return null;
            double percent;
            if (match.Groups["pct"].Success)
                percent = Math.Min(100, int.Parse(match.Groups["pct"].Value, CultureInfo.InvariantCulture));
            else if (match.Groups["hit"].Success) percent = 100;
            else return null;
            return new LimitReading
            {
                Percent = percent,
                ResetsAt = match.Groups["when"].Success ? LatestReset(match.Groups["when"].Value, now) : (DateTimeOffset?)null,
                ResetIsEstimate = match.Groups["when"].Success,
                Source = "banner",
                CapturedAt = now,
            };
        }

        /// <summary>
        /// The LATEST moment "resets in 2h" can mean. The panel floors (2 h 59 m reads "in 2h"), so the reset is before
        /// now + 3 h; scheduling the resume for the bound keeps it after the reset, which is the side that matters.
        /// </summary>
        internal static DateTimeOffset LatestReset(string when, DateTimeOffset now)
        {
            if (string.Equals(when, "soon", StringComparison.Ordinal)) return now.AddMinutes(1);
            int amount = int.Parse(when.Substring(3, when.Length - 4), CultureInfo.InvariantCulture);
            switch (when[when.Length - 1])
            {
                case 'm': return now.AddMinutes(amount + 1);
                case 'h': return now.AddHours(amount + 1);
                default: return now.AddDays(amount + 1);
            }
        }

        /// <summary>Utilization is a fraction on the wire; anything above 1.5 is taken as already a percentage.</summary>
        internal static double ToPercent(double utilization)
        {
            double percent = utilization <= 1.5 ? utilization * 100.0 : utilization;
            if (double.IsNaN(percent) || percent < 0) return 0;
            return Math.Min(100, percent);
        }

        /// <summary>
        /// The account's reading for one window across every panel read (they share the account): the newest exact
        /// reading (event or update) whose reset is still ahead, else the highest banner reading. Null when none.
        /// </summary>
        internal static LimitReading Best(IEnumerable<PanelUsage> panels, DateTimeOffset now, bool weekly)
        {
            LimitReading exact = null, banner = null;
            if (panels == null) return null;
            foreach (PanelUsage panel in panels)
            {
                LimitReading reading = panel == null ? null : (weekly ? panel.Weekly : panel.Session);
                if (reading == null) continue;
                // A window whose reset has passed describes a window that is over.
                if (reading.ResetsAt.HasValue && reading.ResetsAt.Value <= now) continue;
                if (reading.Source == "banner")
                {
                    if (banner == null || reading.Percent > banner.Percent) banner = reading;
                }
                else if (exact == null || reading.CapturedAt > exact.CapturedAt) exact = reading;
            }
            return exact ?? banner;
        }

        // ---- the message ----------------------------------------------------------------------

        /// <summary>
        /// The pause-and-resume request a working session receives. The owner's wording of 2026-10-15, with the times in
        /// local time and the date, so a session can schedule against them without guessing the day.
        /// </summary>
        internal static string PauseMessage(LimitReading session, DateTimeOffset now, TimeZoneInfo zone)
        {
            if (session == null || !session.ResetsAt.HasValue) return ManualPauseMessage();
            zone = zone ?? TimeZoneInfo.Local;
            DateTimeOffset reset = TimeZoneInfo.ConvertTime(session.ResetsAt.Value, zone);
            DateTimeOffset resume = TimeZoneInfo.ConvertTime(session.ResetsAt.Value + ResumeAfterReset, zone);
            string resetText = (session.ResetIsEstimate ? "by about " : "at ") + Clock(reset, now, zone);
            return Tag + " Your Claude session limit is at " + Whole(session.Percent) + " % and resets " + resetText + ". "
                   + "Finish the current step and stop at a safe point. " + SubagentsSentence
                   + "Write a short resume note (what is done, what is next). "
                   + "Then use CronCreate to schedule a one-shot prompt at " + Clock(resume, now, zone)
                   + " that resumes from that note, and stop.";
        }

        /// <summary>The pane button's message when no reading with a reset time exists: pause, note, and wait.</summary>
        internal static string ManualPauseMessage()
        {
            return Tag + " Pause requested. Finish the current step and stop at a safe point. " + SubagentsSentence
                   + "Write a short resume note (what is done, what is next), then stop and wait.";
        }

        /// <summary>
        /// Subagents and background tasks keep spending after the main agent stops, so the request names them (1.6.1). The
        /// session that showed this was running three subagents with its own chat box idle.
        /// </summary>
        internal const string SubagentsSentence =
            "If subagents or background tasks are running, let them reach a safe point or stop them, and note what each was doing. ";

        /// <summary>"16:50 local time on 2026-10-15" -- a 24-hour clock and an ISO date read the same on every locale.</summary>
        internal static string Clock(DateTimeOffset when, DateTimeOffset now, TimeZoneInfo zone)
        {
            return when.ToString("HH:mm", CultureInfo.InvariantCulture) + " local time on "
                   + when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        internal static string Whole(double percent)
        {
            return ((int)Math.Floor(percent)).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The reading for the pane and the log: "62 % · resets 16:45 (from the panel's usage event)".</summary>
        internal static string Describe(LimitReading reading, DateTimeOffset now, TimeZoneInfo zone)
        {
            if (reading == null) return "no figure yet";
            zone = zone ?? TimeZoneInfo.Local;
            string text = Whole(reading.Percent) + " %";
            if (reading.ResetsAt.HasValue)
                text += " · resets " + (reading.ResetIsEstimate ? "by about " : "at ")
                        + TimeZoneInfo.ConvertTime(reading.ResetsAt.Value, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
            string from = reading.Source == "banner" ? "the panel's usage banner"
                        : reading.Source == "event" ? "the panel's usage event" : "the panel's usage update";
            return text + " (from " + from + ")";
        }

        // ---- small readers ---------------------------------------------------------------------

        private static LimitReading Window(JsonElement windows, string name, string source, DateTimeOffset at)
        {
            JsonElement window;
            if (!windows.TryGetProperty(name, out window) || window.ValueKind != JsonValueKind.Object) return null;
            double utilization;
            if (!Number(window, "utilization", out utilization)) return null;
            return new LimitReading { Percent = ToPercent(utilization), ResetsAt = Epoch(window, "resetsAt"), Source = source, CapturedAt = at };
        }

        private static LimitReading Newer(LimitReading have, LimitReading candidate)
        {
            if (candidate == null) return have;
            if (have == null) return candidate;
            return candidate.CapturedAt >= have.CapturedAt ? candidate : have;
        }

        private static bool Bool(JsonElement element, string name)
        {
            JsonElement value;
            return element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.True;
        }

        private static string Str(JsonElement element, string name)
        {
            JsonElement value;
            if (!element.TryGetProperty(name, out value)) return "";
            return value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "") : "";
        }

        private static bool Number(JsonElement element, string name, out double number)
        {
            number = 0;
            JsonElement value;
            if (!element.TryGetProperty(name, out value) || value.ValueKind != JsonValueKind.Number) return false;
            return value.TryGetDouble(out number) && !double.IsNaN(number) && !double.IsInfinity(number);
        }

        /// <summary>Epoch SECONDS, as resetsAt travels. Null when absent or out of any sane range.</summary>
        private static DateTimeOffset? Epoch(JsonElement element, string name)
        {
            double seconds;
            if (!Number(element, name, out seconds)) return null;
            if (seconds < 1e9 || seconds > 1e11) return null;
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000.0));
        }

        /// <summary>A webview Date.now() stamp (epoch MILLISECONDS), or <paramref name="fallback"/>.</summary>
        private static DateTimeOffset Stamp(JsonElement element, string name, DateTimeOffset fallback)
        {
            double ms;
            if (!Number(element, name, out ms) || ms < 1e12 || ms > 1e14) return fallback;
            return DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
        }
    }

    /// <summary>What one look at the panels decided. Built on the poll worker, acted on there, reported on the UI thread.</summary>
    internal sealed class LimitPlan
    {
        public LimitReading Session;
        public LimitReading Weekly;
        /// <summary>Working panels to ask now.</summary>
        public readonly List<PanelUsage> SendTo = new List<PanelUsage>();
        /// <summary>The session limit crossed the threshold and the user has not been told about this window yet.</summary>
        public bool NoticeSession;
        /// <summary>The weekly limit crossed the threshold and the user has not been told about this window yet.</summary>
        public bool NoticeWeekly;
        /// <summary>Panels that were reachable at all on this read.</summary>
        public int Reachable;
    }

    /// <summary>
    /// The watch's memory, between reads: which panel was asked about which window, and which windows the user has been
    /// told about. POLL WORKER ONLY, under the module's scan guard, like PressBudget.
    /// </summary>
    internal sealed class SessionLimitWatch
    {
        /// <summary>A panel that would not take the message is tried this many times in a window, then left alone.</summary>
        internal const int MaxAttempts = 4;

        // targetId -> the reset the panel was asked about. Asked again only once that has passed.
        private readonly Dictionary<string, DateTimeOffset> _asked = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _attempts = new Dictionary<string, int>(StringComparer.Ordinal);
        private DateTimeOffset _sessionNoticedUntil = DateTimeOffset.MinValue;
        private DateTimeOffset _weeklyNoticedUntil = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRead = DateTimeOffset.MinValue;

        internal int AskedCountForSelfTest { get { return _asked.Count; } }

        /// <summary>Whether this tick should read the panels at all (every <see cref="SessionLimit.ReadEvery"/>).</summary>
        internal bool DueToRead(DateTimeOffset now)
        {
            if (now - _lastRead < SessionLimit.ReadEvery) return false;
            _lastRead = now;
            return true;
        }

        internal LimitPlan Plan(IList<PanelUsage> panels, DateTimeOffset now, int threshold)
        {
            var plan = new LimitPlan();
            threshold = Math.Max(SessionLimit.MinThreshold, Math.Min(SessionLimit.MaxThreshold, threshold));
            if (panels != null)
                foreach (PanelUsage panel in panels) if (panel != null && panel.Reachable) plan.Reachable++;
            plan.Session = SessionLimit.Best(panels, now, false);
            plan.Weekly = SessionLimit.Best(panels, now, true);

            // A window that has reset forgets who was asked about it.
            var expired = new List<string>();
            foreach (KeyValuePair<string, DateTimeOffset> entry in _asked) if (entry.Value <= now) expired.Add(entry.Key);
            foreach (string key in expired) { _asked.Remove(key); _attempts.Remove(key); }

            LimitReading session = plan.Session;
            // A threshold with no reset time is not acted on: the message's whole point is WHEN to resume.
            if (session != null && session.Percent >= threshold && session.ResetsAt.HasValue)
            {
                if (now >= _sessionNoticedUntil) plan.NoticeSession = true;
                if (panels != null)
                    foreach (PanelUsage panel in panels)
                    {
                        if (panel == null || !panel.Reachable || !panel.Working || !panel.HasInput) continue;
                        if (_asked.ContainsKey(panel.TargetId)) continue;
                        plan.SendTo.Add(panel);
                    }
            }
            LimitReading weekly = plan.Weekly;
            if (weekly != null && weekly.Percent >= threshold && now >= _weeklyNoticedUntil) plan.NoticeWeekly = true;
            return plan;
        }

        /// <summary>
        /// What happened when one panel was asked. "accepted" settles it for the window. "prompt" (a permission card is
        /// up) and "draft" (the user is typing there) are not attempts at all: the panel is tried again on the next read.
        /// Anything else is an attempt, and the fourth gives up for the window, so a panel that never takes the message
        /// is not typed into every thirty seconds until the reset.
        /// </summary>
        internal void Record(string targetId, string outcome, DateTimeOffset resetsAt)
        {
            if (string.IsNullOrEmpty(targetId)) return;
            if (outcome == "accepted") { _asked[targetId] = resetsAt; _attempts.Remove(targetId); return; }
            if (outcome == "prompt" || outcome == "draft") return;
            int count;
            _attempts.TryGetValue(targetId, out count);
            count++;
            if (count >= MaxAttempts) { _asked[targetId] = resetsAt; _attempts.Remove(targetId); }
            else _attempts[targetId] = count;
        }

        internal void NoticedSession(DateTimeOffset until) { _sessionNoticedUntil = until; }
        internal void NoticedWeekly(DateTimeOffset until) { _weeklyNoticedUntil = until; }
    }

    internal static partial class CdpApprover
    {
        /// <summary>
        /// Read one Claude Code panel for the watch: its working state, whether its chat box is free, whether a
        /// permission card is up, and the usage figures. Installs the listener on the first read of a page and finds it
        /// there on every later one (versioned, so a newer build's listener is a new one rather than a stale shape).
        ///
        /// It reads NO text but the banner's own sentence, which it finds through the banner's "View usage" button and
        /// cuts at 200 characters. Not the session title (a button label), not the chat box's contents (only whether it
        /// is empty), not a message. The listener keeps four numbers and two statuses from usage messages and drops every
        /// other message unread past its type.
        /// </summary>
        private const string UsageReadExpression = @"
(function () {" + DocumentPrelude + @"
  var a = appDocument();
  if (!a.doc || a.elements < MinElements) return 'unreachable';
  var d = a.doc, w = d.defaultView;
  function pick(u) {
    var o = {};
    if (!u || typeof u !== 'object') return o;
    ['five_hour', 'seven_day'].forEach(function (k) {
      var x = u[k];
      if (x && typeof x.utilization === 'number')
        o[k] = { utilization: x.utilization, resetsAt: typeof x.resetsAt === 'number' ? x.resetsAt : null };
    });
    return o;
  }
  if (w && !w.__agentflowUsage1) {
    w.__agentflowUsage1 = { info: null, infoAt: 0, windows: null, windowsAt: 0 };
    w.addEventListener('message', function (e) {
      try {
        var m = e.data && e.data.message;
        if (!m || typeof m !== 'object') return;
        var keep = w.__agentflowUsage1;
        if (m.type === 'io_message' && m.message && m.message.type === 'rate_limit_event' && m.message.rate_limit_info) {
          var r = m.message.rate_limit_info;
          keep.info = { status: String(r.status || ''), type: String(r.rateLimitType || ''),
                        utilization: typeof r.utilization === 'number' ? r.utilization : null,
                        resetsAt: typeof r.resetsAt === 'number' ? r.resetsAt : null,
                        windows: pick(r.unifiedWindows) };
          keep.infoAt = Date.now();
        } else if (m.type === 'request' && m.request && m.request.type === 'panel_usage_update') {
          keep.windows = pick(m.request.unifiedWindows);
          keep.windowsAt = Date.now();
        }
      } catch (x) { }
    });
  }
  var banner = '';
  var links = d.querySelectorAll('button[class*=""usageLink""]');
  for (var i = 0; i < links.length; i++) {
    if ((links[i].textContent || '').trim() !== 'View usage') continue;
    var box = links[i].closest('[class*=""banner""]');
    if (!box) continue;
    var span = box.querySelector('span');
    banner = ((span || box).textContent || '').replace(/\s+/g, ' ').trim().slice(0, 200);
    break;
  }
  var input = d.querySelector('[aria-label=""Message input""]');
  var keep = w ? w.__agentflowUsage1 : null;
  // Working is the main agent's Stop button, OR the agent map saying its subagents are working (1.6.1): a session that
  // has handed its work to subagents shows an idle chat box and a Send button while they spend, and 1.6.0 called it
  // idle (the owner's report of 2026-10-16, measured: '3 agents · Agents are working · Click to open the agent map').
  var subagents = false;
  var labelled = d.querySelectorAll('button[aria-label]');
  for (var li = 0; li < labelled.length; li++) {
    if (/Agents are working/i.test(labelled[li].getAttribute('aria-label') || '')) { subagents = true; break; }
  }
  return JSON.stringify({
    working: !!d.querySelector('button[aria-label=""Stop""]') || subagents,
    subagents: subagents,
    input: !!input,
    draft: !!(input && (input.textContent || '').trim()),
    prompt: !!d.querySelector('[class*=""permissionRequestContainer""]'),
    info: keep ? keep.info : null, infoAt: keep ? keep.infoAt : 0,
    windows: keep ? keep.windows : null, windowsAt: keep ? keep.windowsAt : 0,
    banner: banner
  });
})()";

        /// <summary>
        /// Put the message in the chat box and press Enter, which in a working panel queues it for the agent. Refuses
        /// when a permission card is up (Enter must never be what answers a prompt), when the box holds the user's draft,
        /// and when the box is missing. CLAUDE CODE PANELS ONLY: the Codex panel takes no keyboard event from this module,
        /// ever, because Escape there is Deny. Never focuses anything, so it cannot take the keyboard from whatever the
        /// user is doing.
        /// </summary>
        private const string SendTemplate = @"
(function () {" + DocumentPrelude + @"
  var a = appDocument();
  if (!a.doc || a.elements < MinElements) return 'unreachable';
  var d = a.doc;
  if (d.querySelector('[class*=""permissionRequestContainer""]')) return 'prompt';
  var box = d.querySelector('[aria-label=""Message input""]');
  if (!box) return 'no-input';
  if ((box.textContent || '').trim()) return 'draft';
  var text = __MESSAGE__;
  box.textContent = text;
  box.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: text }));
  box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true }));
  return 'sent';
})()";

        /// <summary>
        /// Whether the panel took it: an accepted message leaves the chat box. One still holding exactly our text was not
        /// taken, and is taken back out, so the user never finds a half-sent request sitting in their chat box.
        /// </summary>
        private const string ConfirmTemplate = @"
(function () {" + DocumentPrelude + @"
  var a = appDocument();
  if (!a.doc) return 'gone';
  var box = a.doc.querySelector('[aria-label=""Message input""]');
  if (!box) return 'gone';
  if ((box.textContent || '') !== __MESSAGE__) return 'accepted';
  box.textContent = '';
  box.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'deleteContentBackward' }));
  return 'not-accepted';
})()";

        internal static string UsageReadExpressionForSelfTest { get { return UsageReadExpression; } }
        internal static string BuildSendForSelfTest(string message) { return BuildSend(message); }
        internal static string BuildConfirmForSelfTest(string message) { return BuildConfirm(message); }

        private static string BuildSend(string message)
        {
            return SendTemplate.Replace("__MESSAGE__", JsonEncode(message ?? ""));
        }

        private static string BuildConfirm(string message)
        {
            return ConfirmTemplate.Replace("__MESSAGE__", JsonEncode(message ?? ""));
        }

        /// <summary>
        /// Read every Claude Code panel on the port for the watch. Codex panels are not read: the limit is Claude's.
        /// Empty when the port does not answer or nothing is open; a target that throws is skipped.
        /// </summary>
        internal static List<PanelUsage> ReadUsage(int port, int timeoutMs, DateTimeOffset now)
        {
            var panels = new List<PanelUsage>();
            List<KeyValuePair<string, string>> work = AgentTargets(port, timeoutMs);
            if (work.Count == 0) return panels;
            string browserUrl = BrowserSocketUrl(port, timeoutMs);
            if (string.IsNullOrEmpty(browserUrl)) return panels;
            try
            {
                using (var session = new CdpSession(browserUrl, timeoutMs))
                {
                    foreach (KeyValuePair<string, string> item in work)
                    {
                        if (item.Value != AgentClaude) continue;
                        string sessionId = session.Attach(item.Key);
                        if (sessionId == null) continue;
                        try
                        {
                            PanelUsage panel = SessionLimit.Parse(item.Key, session.Evaluate(sessionId, UsageReadExpression), now);
                            if (panel != null) panels.Add(panel);
                        }
                        finally { session.Detach(sessionId); }
                    }
                }
            }
            catch (Exception) { }
            return panels;
        }

        /// <summary>
        /// Ask one Claude Code panel, and say what happened: accepted, not-accepted, prompt, draft, no-input,
        /// unreachable or gone. The confirm runs on the same connection after a short wait, because the panel takes a
        /// message on its own schedule.
        /// </summary>
        internal static string SendPause(int port, string targetId, string message, int timeoutMs, int confirmAfterMs)
        {
            if (string.IsNullOrEmpty(targetId) || string.IsNullOrEmpty(message)) return "gone";
            string browserUrl = BrowserSocketUrl(port, timeoutMs);
            if (string.IsNullOrEmpty(browserUrl)) return "gone";
            try
            {
                using (var session = new CdpSession(browserUrl, timeoutMs))
                {
                    string sessionId = session.Attach(targetId);
                    if (sessionId == null) return "gone";
                    try
                    {
                        string sent = session.Evaluate(sessionId, BuildSend(message));
                        if (sent != "sent") return string.IsNullOrEmpty(sent) ? "gone" : sent;
                        if (confirmAfterMs > 0) Thread.Sleep(confirmAfterMs);
                        string confirmed = session.Evaluate(sessionId, BuildConfirm(message));
                        return string.IsNullOrEmpty(confirmed) ? "gone" : confirmed;
                    }
                    finally { session.Detach(sessionId); }
                }
            }
            catch (Exception) { return "gone"; }
        }
    }
}
