using System;
using System.Globalization;

namespace DesktopAICompanion.ReminderModule
{
    /// <summary>
    /// A reminder the user typed, independent of any calendar: a one-off, or a simple recurrence. Persisted as
    /// one pipe-delimited line (the text is sanitized of '|' and newlines and kept last), so no JSON and no ABI
    /// surface. Dedup is a per-reminder <see cref="LastFired"/> stamp rather than a global fired set, so the
    /// store stays bounded and a recurring reminder fires once per occurrence with no growing key list.
    /// </summary>
    internal sealed class PersonalReminder
    {
        public const string KindOnce = "once";
        public const string KindEveryN = "everyN";
        public const string KindDaily = "daily";
        public const string KindWeekdays = "weekdays";

        public string Id;
        public string Text;
        public string Kind;
        public bool Enabled = true;
        public DateTimeOffset When;        // KindOnce: when to fire
        public int IntervalMinutes;        // KindEveryN
        public int TimeOfDayMinutes;       // KindDaily / KindWeekdays: minutes since local midnight
        public DateTimeOffset Anchor;      // stable anchor for KindEveryN occurrence math (creation time)
        public string LastFired = "";      // the last occurrence key this fired, so it fires each occurrence once

        public string ScheduleSummary()
        {
            switch (Kind)
            {
                case KindOnce: return "once, " + When.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                case KindEveryN: return "every " + IntervalMinutes + " min";
                case KindDaily: return "daily at " + Hhmm(TimeOfDayMinutes);
                case KindWeekdays: return "weekdays at " + Hhmm(TimeOfDayMinutes);
                default: return Kind;
            }
        }

        private static string Hhmm(int minutes)
        {
            int h = (minutes / 60) % 24, m = minutes % 60;
            return h.ToString("00", CultureInfo.InvariantCulture) + ":" + m.ToString("00", CultureInfo.InvariantCulture);
        }

        public static string Encode(PersonalReminder r)
        {
            string text = (r.Text ?? "").Replace("|", "/").Replace("\r", " ").Replace("\n", " ").Trim();
            return string.Join("|", new[]
            {
                r.Id ?? "",
                r.Enabled ? "1" : "0",
                r.Kind ?? "",
                r.When.ToString("o", CultureInfo.InvariantCulture),
                r.IntervalMinutes.ToString(CultureInfo.InvariantCulture),
                r.TimeOfDayMinutes.ToString(CultureInfo.InvariantCulture),
                r.Anchor.ToString("o", CultureInfo.InvariantCulture),
                (r.LastFired ?? "").Replace("|", "/").Replace("\r", " ").Replace("\n", " "),
                text,
            });
        }

        public static PersonalReminder Decode(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            string[] f = line.Split('|');
            if (f.Length < 9) return null;
            try
            {
                return new PersonalReminder
                {
                    Id = f[0],
                    Enabled = f[1] == "1",
                    Kind = f[2],
                    When = DateTimeOffset.Parse(f[3], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    IntervalMinutes = int.Parse(f[4], CultureInfo.InvariantCulture),
                    TimeOfDayMinutes = int.Parse(f[5], CultureInfo.InvariantCulture),
                    Anchor = DateTimeOffset.Parse(f[6], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    LastFired = f[7],
                    Text = f[8],
                };
            }
            catch { return null; }
        }

        // SelfCheck, not SelfTest: see the note in AggregateCalendarSource. ReminderModule.SelfTest aggregates.
        internal static bool SelfCheck(out string detail)
        {
            var r = new PersonalReminder
            {
                Id = "abc", Text = "call the vet | now\nplease", Kind = KindDaily,
                TimeOfDayMinutes = 9 * 60 + 30, Anchor = DateTimeOffset.Now, When = DateTimeOffset.Now, LastFired = "",
            };
            PersonalReminder back = Decode(Encode(r));
            bool ok = back != null && back.Id == "abc" && back.Kind == KindDaily && back.TimeOfDayMinutes == 570
                && back.Text == "call the vet / now please";   // sanitized round-trip
            detail = ok ? "personal reminder encode/decode round-trips and sanitizes text"
                        : "personal reminder round-trip failed";
            return ok;
        }
    }
}
