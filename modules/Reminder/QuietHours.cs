using System;
using System.Globalization;
using System.Text;

namespace DesktopAICompanion.ReminderModule
{
    /// <summary>
    /// A "quiet hours" window for the Reminder module: a user-configured span of the day during which the
    /// pet stays silent. It only DECIDES whether a given instant falls inside the window -- it announces
    /// nothing and remembers nothing. The caller (see ReminderModule.CheckDue) uses that decision to skip
    /// SayAll without marking the event fired, so a reminder still fires once the window ends if the event
    /// is still inside its lead-time fire window.
    ///
    /// SETTING SHAPE (recommended): two "HH:mm" 24-hour strings, "quietFrom" and "quietTo".
    ///   * Text, not two Int spinners: one field per boundary reads as a clock time in the options pane, and
    ///     Text round-trips through IModuleSettings.Get/Set exactly as the rest of the module already stores
    ///     values (see "url"/"file"). SettingKind has no Time member (Bool/Int/Text/Enum/Secret/Info), so
    ///     Text is the honest fit; "HH:mm" is culture-neutral and unambiguous.
    ///   * Two strings, not a single "22:00-07:00": each half is independently editable and independently
    ///     validated, and an empty half is a natural "off" signal.
    ///   * BOTH BLANK (or either blank) => the feature is OFF. Any unparseable or out-of-range value is also
    ///     treated as OFF -- the module never suppresses reminders because of a typo, and never throws.
    ///
    /// BOUNDARY CHOICE: the window is half-open [from, to) in local wall-clock minutes -- the start minute is
    /// quiet, the end minute is NOT. So 22:00-07:00 is silent from 22:00:00 up to (but not including) 07:00,
    /// and the 07:00 reminder lands. This matches "reminders resume once quiet hours end".
    ///
    /// from == to => OFF (never quiet). An equal pair is ambiguous between "zero-width window" and "the whole
    /// day"; the whole-day reading would silence reminders forever, which is a footgun, so we treat equal as
    /// disabled -- consistent with the blank-means-off rule.
    ///
    /// PURE + self-contained: no host, no settings, no I/O, no static mutable state. Time comparisons use the
    /// LOCAL time-of-day of the supplied instant (now.ToLocalTime().TimeOfDay), so the result matches the
    /// user's wall clock regardless of the offset carried by <paramref name="now"/>.
    /// </summary>
    public sealed class QuietHours
    {
        private readonly bool _enabled;
        private readonly int _fromMinutes;   // minutes since local midnight, 0..1439 (only when _enabled)
        private readonly int _toMinutes;

        /// <summary>
        /// Build an instance from the two saved "HH:mm" strings. Blank, unparseable, out-of-range, or equal
        /// endpoints all yield a DISABLED instance whose <see cref="IsQuiet(DateTimeOffset)"/> is always false.
        /// </summary>
        public QuietHours(string quietFrom, string quietTo)
        {
            int from, to;
            if (TryParseTimeOfDay(quietFrom, out from) && TryParseTimeOfDay(quietTo, out to) && from != to)
            {
                _enabled = true;
                _fromMinutes = from;
                _toMinutes = to;
            }
            else
            {
                _enabled = false;
                _fromMinutes = -1;
                _toMinutes = -1;
            }
        }

        /// <summary>True when this instance holds a usable window (both endpoints valid and not equal).</summary>
        public bool Enabled { get { return _enabled; } }

        /// <summary>True when <paramref name="now"/>'s LOCAL time-of-day falls inside the window [from, to).</summary>
        public bool IsQuiet(DateTimeOffset now)
        {
            if (!_enabled) return false;
            return InWindow(LocalMinuteOfDay(now), _fromMinutes, _toMinutes);
        }

        /// <summary>
        /// Stateless one-shot: does <paramref name="now"/> fall inside the window described by the two "HH:mm"
        /// strings? Returns false (never quiet) for blank/unparseable/out-of-range input or equal endpoints,
        /// and never throws. This is the form the module calls straight from its saved settings.
        /// </summary>
        public static bool IsQuiet(DateTimeOffset now, string quietFrom, string quietTo)
        {
            int from, to;
            if (!TryParseTimeOfDay(quietFrom, out from)) return false;
            if (!TryParseTimeOfDay(quietTo, out to)) return false;
            if (from == to) return false;   // equal endpoints => OFF (documented above)
            return InWindow(LocalMinuteOfDay(now), from, to);
        }

        // --- internals --------------------------------------------------------------------------------

        // Local wall-clock minute of day (0..1439). ToLocalTime() re-expresses the same instant in the
        // machine's local zone; TotalMinutes is truncated to the containing minute, which is the right
        // resolution for an HH:mm window (e.g. 06:59:30 counts as 06:59).
        private static int LocalMinuteOfDay(DateTimeOffset now)
        {
            return (int)now.ToLocalTime().TimeOfDay.TotalMinutes;
        }

        // Half-open [from, to). from < to is a same-day span; from > to wraps past midnight. Never called
        // with from == to (both entry points reject that first).
        private static bool InWindow(int minute, int from, int to)
        {
            if (from < to) return minute >= from && minute < to;   // normal daytime window
            return minute >= from || minute < to;                  // overnight wrap window
        }

        // Parse "HH:mm" 24-hour into minutes-since-midnight. Requires exactly one ':', digits only (no sign,
        // no whitespace, no seconds), hours 0..23 and minutes 0..59. Anything else => false, never throws.
        private static bool TryParseTimeOfDay(string text, out int minutes)
        {
            minutes = -1;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string s = text.Trim();
            int colon = s.IndexOf(':');
            if (colon <= 0 || colon != s.LastIndexOf(':') || colon == s.Length - 1) return false;

            string hh = s.Substring(0, colon);
            string mm = s.Substring(colon + 1);

            int h, m;
            if (!int.TryParse(hh, NumberStyles.None, CultureInfo.InvariantCulture, out h)) return false;
            if (!int.TryParse(mm, NumberStyles.None, CultureInfo.InvariantCulture, out m)) return false;
            if (h < 0 || h > 23 || m < 0 || m > 59) return false;

            minutes = h * 60 + m;
            return true;
        }

        // --- self-test --------------------------------------------------------------------------------

        /// <summary>
        /// Proves the overnight-wrap case, the normal daytime case, the [from, to) boundary choice, the
        /// equal-endpoints=OFF rule, and the disabled/blank/garbage cases. Pure and timezone-independent:
        /// each probe instant is a DateTime tagged Local, so its wall-clock time-of-day survives ToLocalTime()
        /// unchanged on any machine.
        /// </summary>
        // SelfCheck, not SelfTest: see the note in AggregateCalendarSource. ReminderModule.SelfTest aggregates.
        internal static bool SelfCheck(out string detail)
        {
            var sb = new StringBuilder();
            bool ok = true;

            // Instant whose LOCAL time-of-day is exactly h:m (date is arbitrary; Jan 1 avoids DST edges).
            Func<int, int, DateTimeOffset> at = (h, m) =>
                new DateTimeOffset(new DateTime(2024, 1, 1, h, m, 0, DateTimeKind.Local));

            // Overnight wrap 22:00 -> 07:00 (from > to): quiet band straddles midnight.
            ok &= Check(sb, "overnight 23:30 is quiet", QuietHours.IsQuiet(at(23, 30), "22:00", "07:00"));
            ok &= Check(sb, "overnight 06:59 is quiet", QuietHours.IsQuiet(at(6, 59), "22:00", "07:00"));
            ok &= Check(sb, "overnight 12:00 is NOT quiet", !QuietHours.IsQuiet(at(12, 0), "22:00", "07:00"));

            // Boundary choice: start inclusive, end exclusive.
            ok &= Check(sb, "overnight start 22:00 is quiet (inclusive)", QuietHours.IsQuiet(at(22, 0), "22:00", "07:00"));
            ok &= Check(sb, "overnight end 07:00 is NOT quiet (exclusive)", !QuietHours.IsQuiet(at(7, 0), "22:00", "07:00"));

            // Normal daytime 09:00 -> 17:00 (from < to).
            ok &= Check(sb, "daytime 12:00 is quiet", QuietHours.IsQuiet(at(12, 0), "09:00", "17:00"));
            ok &= Check(sb, "daytime 08:59 is NOT quiet", !QuietHours.IsQuiet(at(8, 59), "09:00", "17:00"));
            ok &= Check(sb, "daytime start 09:00 is quiet (inclusive)", QuietHours.IsQuiet(at(9, 0), "09:00", "17:00"));
            ok &= Check(sb, "daytime end 17:00 is NOT quiet (exclusive)", !QuietHours.IsQuiet(at(17, 0), "09:00", "17:00"));

            // from == to => OFF, at the mark and elsewhere (never "always quiet").
            ok &= Check(sb, "equal endpoints at the mark is NOT quiet", !QuietHours.IsQuiet(at(13, 0), "13:00", "13:00"));
            ok &= Check(sb, "equal endpoints elsewhere is NOT quiet", !QuietHours.IsQuiet(at(3, 0), "13:00", "13:00"));

            // Disabled / blank / garbage: never quiet, never throw.
            ok &= Check(sb, "both blank is off", !QuietHours.IsQuiet(at(23, 30), "", ""));
            ok &= Check(sb, "one blank is off", !QuietHours.IsQuiet(at(23, 30), "22:00", ""));
            ok &= Check(sb, "null is off", !QuietHours.IsQuiet(at(23, 30), null, "07:00"));
            ok &= Check(sb, "garbage text is off", !QuietHours.IsQuiet(at(23, 30), "nope", "07:00"));
            ok &= Check(sb, "hour out of range is off", !QuietHours.IsQuiet(at(23, 30), "25:00", "07:00"));
            ok &= Check(sb, "minute out of range is off", !QuietHours.IsQuiet(at(23, 30), "22:70", "07:00"));
            ok &= Check(sb, "extra colon is off", !QuietHours.IsQuiet(at(23, 30), "22:00:00", "07:00"));

            // Instance path mirrors the static path.
            var q = new QuietHours("22:00", "07:00");
            ok &= Check(sb, "instance built from strings is enabled", q.Enabled);
            ok &= Check(sb, "instance 23:30 is quiet", q.IsQuiet(at(23, 30)));
            ok &= Check(sb, "instance 07:00 is NOT quiet", !q.IsQuiet(at(7, 0)));
            var off = new QuietHours("", "");
            ok &= Check(sb, "instance from blanks is disabled", !off.Enabled);
            ok &= Check(sb, "disabled instance is never quiet", !off.IsQuiet(at(23, 30)));

            sb.AppendLine(ok ? "QuietHours self-test PASSED" : "QuietHours self-test FAILED");
            detail = sb.ToString();
            return ok;
        }

        private static bool Check(StringBuilder sb, string name, bool condition)
        {
            sb.AppendLine((condition ? "  ok   " : "  FAIL ") + name);
            return condition;
        }
    }
}
