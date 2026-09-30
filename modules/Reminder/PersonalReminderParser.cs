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
                if (!QuietHours.TryParseTimeOfDay(tok, out hhmm)) { error = "After '" + headLower + "', give a time like 09:00. " + Help; return false; }
                r.Kind = headLower == "daily" ? PersonalReminder.KindDaily : PersonalReminder.KindWeekdays;
                r.TimeOfDayMinutes = hhmm; r.Text = text;
            }
            else if (headLower == "at")
            {
                string tok = FirstWord(rest, out string text);
                if (!QuietHours.TryParseTimeOfDay(tok, out hhmm)) { error = "After 'at', give a time like 15:00. " + Help; return false; }
                r.Kind = PersonalReminder.KindOnce; r.When = TodayOrTomorrowAt(now, hhmm); r.Text = text;
            }
            else if (TryDate(head, out date))
            {
                string tok = FirstWord(rest, out string text);
                if (!QuietHours.TryParseTimeOfDay(tok, out hhmm)) { error = "After a date, give a time like 14:00. " + Help; return false; }
                DateTime dt = date.AddMinutes(hhmm);
                r.Kind = PersonalReminder.KindOnce;
                r.When = new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
                r.Text = text;
            }
            else if (QuietHours.TryParseTimeOfDay(head, out hhmm))
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

        // HH:mm goes through QuietHours.TryParseTimeOfDay, the module's one parser (F203). The copy that sat here
        // used NumberStyles.Integer, so "+9:00" was a valid time for a typed reminder and not for the quiet-hours
        // window two fields away.

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
            // One named entry per case, as a FAIL: line of its own, so the detail says WHICH case failed and the
            // mutation harness can name it. One `err` shared by fourteen calls used to be quoted at the end, and
            // the last call is a must-fail case, so whichever case broke the detail quoted that case's refusal
            // text (RA-172).
            var failed = new System.Collections.Generic.List<string>();
            Action<string, DateTimeOffset, Func<PersonalReminder, bool>> expect = (input, at, fields) =>
            {
                PersonalReminder parsed; string why;
                if (!TryParse(input, at, out parsed, out why)) { failed.Add("'" + input + "' was refused: " + (why ?? "null")); return; }
                if (!fields(parsed)) failed.Add("'" + input + "' parsed with the wrong fields (" + parsed.ScheduleSummary() + ")");
            };
            Action<string, DateTimeOffset, string> refuse = (input, at, reason) =>
            {
                PersonalReminder parsed; string why;
                if (TryParse(input, at, out parsed, out why)) failed.Add("'" + input + "' parsed and must not: " + reason);
            };

            expect("every 60m Stand up", now, r => r.Kind == PersonalReminder.KindEveryN && r.IntervalMinutes == 60 && r.Text == "Stand up");
            expect("daily 09:00 Standup", now, r => r.Kind == PersonalReminder.KindDaily && r.TimeOfDayMinutes == 540);
            expect("in 2h Call back", now, r => r.Kind == PersonalReminder.KindOnce && Math.Abs((r.When - now).TotalMinutes - 120) < 0.5);
            expect("weekdays 17:00 Log off", now, r => r.Kind == PersonalReminder.KindWeekdays && r.TimeOfDayMinutes == 1020);
            expect("2026-09-01 14:00 Dentist", now, r => r.Kind == PersonalReminder.KindOnce && r.When.Hour == 14 && r.Text == "Dentist");

            // 'at' and the bare HH:mm form, and the roll-to-tomorrow rule they share. The detail line
            // below claimed 'at' was covered while nothing parsed one, so deleting either branch, or
            // TodayOrTomorrowAt, left this green (F194). A LOCAL-zone `now`, because TodayOrTomorrowAt
            // rolls on the machine's local calendar day: the -7 fixture above is a 17:00 UTC instant, which
            // a UTC runner would already have past 15:00 of. 10:00 local, so 15:00 is today and 09:00 is
            // tomorrow, and the expectations are literal dates rather than a mirror of the rule.
            var localTen = new DateTime(2026, 8, 26, 10, 0, 0);
            var localNow = new DateTimeOffset(localTen, TimeZoneInfo.Local.GetUtcOffset(localTen));
            expect("at 15:00 Call the vet", localNow, r => r.Kind == PersonalReminder.KindOnce
                && r.When.LocalDateTime == new DateTime(2026, 8, 26, 15, 0, 0) && r.Text == "Call the vet");
            expect("at 09:00 Early", localNow, r => r.Kind == PersonalReminder.KindOnce
                && r.When.LocalDateTime == new DateTime(2026, 8, 27, 9, 0, 0) && r.Text == "Early");
            expect("07:30 Gym", localNow, r => r.Kind == PersonalReminder.KindOnce
                && r.When.LocalDateTime == new DateTime(2026, 8, 27, 7, 30, 0) && r.Text == "Gym");
            refuse("at soon Call the vet", localNow, "'at' without a time");
            refuse("every Stand up", now, "missing interval");
            refuse("daily 09:00", now, "missing text");
            refuse("gibberish here", now, "no schedule");
            refuse("daily 25:00 Late", now, "hour out of range");
            refuse("at +9:00 Signed", localNow, "a sign is not a time (F203)");

            var sb = new System.Text.StringBuilder();
            foreach (string f in failed) sb.AppendLine("FAIL: " + f);
            sb.Append(failed.Count == 0
                ? "personal-reminder parser: every/daily/in/weekdays/date/at and bare HH:mm parse; malformed rejected"
                : "personal-reminder parser wrong: " + failed.Count.ToString(CultureInfo.InvariantCulture) + " case(s) above");
            detail = sb.ToString();
            return failed.Count == 0;
        }
    }
}
