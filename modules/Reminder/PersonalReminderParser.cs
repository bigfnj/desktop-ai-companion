using System;
using System.Globalization;

namespace DesktopAICompanion.ReminderModule
{
    /// <summary>
    /// Turns a short typed line into a <see cref="PersonalReminder"/>. The leading token(s) are the schedule and
    /// the rest is the spoken text:
    ///   every 60m Stand up   |   in 30m Take the pizza out   |   daily 09:00 Standup
    ///   weekdays 17:00 Log off   |   at 15:00 Call the vet   |   2026-09-01 14:00 Dentist
    /// Minutes default when no unit is given; "h" means hours. Pure and string-only, so it is unit-testable.
    /// </summary>
    internal static class PersonalReminderParser
    {
        private const string Help = "Try: 'daily 09:00 Standup', 'every 60m Stretch', 'in 30m Pizza', or '2026-09-01 14:00 Dentist'.";

        public static bool TryParse(string input, DateTimeOffset now, out PersonalReminder reminder, out string error)
        {
            reminder = null; error = null;
            if (string.IsNullOrWhiteSpace(input)) { error = "Type a reminder. " + Help; return false; }

            string s = input.Trim();
            string head = FirstWord(s, out string rest);
            string headLower = head.ToLowerInvariant();

            var r = new PersonalReminder { Id = NewId(), Anchor = now, Enabled = true, When = now };

            int mins, hhmm;
            DateTime date;
            if (headLower == "every")
            {
                string tok = FirstWord(rest, out string text);
                if (!TryInterval(tok, out mins)) { error = "After 'every', give an interval like 60m or 2h. " + Help; return false; }
                r.Kind = PersonalReminder.KindEveryN; r.IntervalMinutes = mins; r.Text = text;
            }
            else if (headLower == "in")
            {
                string tok = FirstWord(rest, out string text);
                if (!TryInterval(tok, out mins)) { error = "After 'in', give a delay like 30m or 2h. " + Help; return false; }
                r.Kind = PersonalReminder.KindOnce; r.When = now.AddMinutes(mins); r.Text = text;
            }
            else if (headLower == "daily" || headLower == "weekdays")
            {
                string tok = FirstWord(rest, out string text);
                if (!TryHhmm(tok, out hhmm)) { error = "After '" + headLower + "', give a time like 09:00. " + Help; return false; }
                r.Kind = headLower == "daily" ? PersonalReminder.KindDaily : PersonalReminder.KindWeekdays;
                r.TimeOfDayMinutes = hhmm; r.Text = text;
            }
            else if (headLower == "at")
            {
                string tok = FirstWord(rest, out string text);
                if (!TryHhmm(tok, out hhmm)) { error = "After 'at', give a time like 15:00. " + Help; return false; }
                r.Kind = PersonalReminder.KindOnce; r.When = TodayOrTomorrowAt(now, hhmm); r.Text = text;
            }
            else if (TryDate(head, out date))
            {
                string tok = FirstWord(rest, out string text);
                if (!TryHhmm(tok, out hhmm)) { error = "After a date, give a time like 14:00. " + Help; return false; }
                DateTime dt = date.AddMinutes(hhmm);
                r.Kind = PersonalReminder.KindOnce;
                r.When = new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
                r.Text = text;
            }
            else if (TryHhmm(head, out hhmm))
            {
                r.Kind = PersonalReminder.KindOnce; r.When = TodayOrTomorrowAt(now, hhmm); r.Text = rest;
            }
            else
            {
                error = "I couldn't read the schedule. " + Help; return false;
            }

            if (string.IsNullOrWhiteSpace(r.Text)) { error = "Add the reminder text after the schedule. " + Help; return false; }
            r.Text = r.Text.Trim();
            reminder = r;
            return true;
        }

        private static DateTimeOffset TodayOrTomorrowAt(DateTimeOffset now, int hhmm)
        {
            DateTime dt = now.LocalDateTime.Date.AddMinutes(hhmm);
            DateTimeOffset when = new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
            return when <= now ? when.AddDays(1) : when;
        }

        private static string FirstWord(string s, out string rest)
        {
            s = (s ?? "").TrimStart();
            int i = s.IndexOf(' ');
            if (i < 0) { rest = ""; return s; }
            rest = s.Substring(i + 1).TrimStart();
            return s.Substring(0, i);
        }

        private static bool TryInterval(string tok, out int minutes)
        {
            minutes = 0;
            if (string.IsNullOrWhiteSpace(tok)) return false;
            tok = tok.Trim().ToLowerInvariant();
            int mult = 1;
            if (tok.EndsWith("m")) tok = tok.Substring(0, tok.Length - 1);
            else if (tok.EndsWith("h")) { mult = 60; tok = tok.Substring(0, tok.Length - 1); }
            int n;
            if (!int.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n <= 0) return false;
            long total = (long)n * mult;
            if (total > 7 * 24 * 60) return false;   // cap at a week
            minutes = (int)total;
            return true;
        }

        private static bool TryHhmm(string tok, out int minutes)
        {
            minutes = 0;
            if (string.IsNullOrWhiteSpace(tok)) return false;
            string[] p = tok.Trim().Split(':');
            int h, m;
            if (p.Length == 2 &&
                int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out h) &&
                int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out m) &&
                h >= 0 && h < 24 && m >= 0 && m < 60)
            {
                minutes = h * 60 + m;
                return true;
            }
            return false;
        }

        private static bool TryDate(string tok, out DateTime date)
        {
            return DateTime.TryParseExact(tok, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date);
        }

        private static string NewId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        // SelfCheck, not SelfTest: see the note in AggregateCalendarSource. ReminderModule.SelfTest aggregates.
        internal static bool SelfCheck(out string detail)
        {
            var now = new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.FromHours(-7));
            bool ok = true;
            PersonalReminder r; string err;

            ok &= TryParse("every 60m Stand up", now, out r, out err) && r.Kind == PersonalReminder.KindEveryN && r.IntervalMinutes == 60 && r.Text == "Stand up";
            ok &= TryParse("daily 09:00 Standup", now, out r, out err) && r.Kind == PersonalReminder.KindDaily && r.TimeOfDayMinutes == 540;
            ok &= TryParse("in 2h Call back", now, out r, out err) && r.Kind == PersonalReminder.KindOnce && Math.Abs((r.When - now).TotalMinutes - 120) < 0.5;
            ok &= TryParse("weekdays 17:00 Log off", now, out r, out err) && r.Kind == PersonalReminder.KindWeekdays && r.TimeOfDayMinutes == 1020;
            ok &= TryParse("2026-09-01 14:00 Dentist", now, out r, out err) && r.Kind == PersonalReminder.KindOnce && r.When.Hour == 14 && r.Text == "Dentist";
            ok &= !TryParse("every Stand up", now, out r, out err);            // missing interval
            ok &= !TryParse("daily 09:00", now, out r, out err);               // missing text
            ok &= !TryParse("gibberish here", now, out r, out err);            // no schedule

            detail = ok ? "personal-reminder parser: every/daily/in/weekdays/date/at parse; malformed rejected"
                        : "personal-reminder parser wrong (last err=" + (err ?? "null") + ")";
            return ok;
        }
    }
}
