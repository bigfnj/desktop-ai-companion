using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DesktopAICompanion.ReminderModule
{
    /// <summary>
    /// The pure due-selection rule, kept separate from the module so it can be unit-tested without a host.
    /// An event fires once, in the window [start - lead, start + grace]: the lead is the user's "remind me N
    /// minutes before", and the small grace both lets a coarse tick catch the moment and lets a just-launched
    /// app still fire a reminder for something imminent -- while NOT nagging about events that already came
    /// and went while the app was closed.
    /// </summary>
    public static class ReminderScheduler
    {
        /// <summary>Minutes after an event's start that a reminder may still fire (past this it is missed, not nagged).</summary>
        public const int GraceMinutes = 1;

        public static IReadOnlyList<CalendarEvent> DueNow(
            IReadOnlyList<CalendarEvent> events, DateTimeOffset now, int leadMinutes, ISet<string> firedIds)
        {
            var due = new List<CalendarEvent>();
            if (events == null) return due;
            if (leadMinutes < 0) leadMinutes = 0;
            foreach (CalendarEvent e in events)
            {
                if (e == null || string.IsNullOrEmpty(e.Id)) continue;
                if (firedIds != null && firedIds.Contains(e.Id)) continue;
                DateTimeOffset fireAt = e.Start.AddMinutes(-leadMinutes);
                DateTimeOffset expireAt = e.Start.AddMinutes(GraceMinutes);
                if (now >= fireAt && now < expireAt) due.Add(e);
            }
            return due;
        }

        // --- multi-lead ---------------------------------------------------------------------------------
        //
        // Same window rule as DueNow, but an event can carry SEVERAL leads at once (a 15-minute warning AND a
        // 5-minute warning, say). Each (event, lead) pair is its own reminder: it opens its own window
        // [start - lead, start + grace] and is de-duplicated on its OWN composite id, so the 15-min warning
        // firing does not suppress the 5-min one. The larger lead's window is a superset of the smaller's, so
        // both can be open at the same instant; when neither has fired yet (e.g. a fresh launch inside both
        // windows) both come due in the same call -- that is the same "catch the imminent thing on launch"
        // grace the single-lead path already grants, applied per lead. The caller keys its fired set on
        // DueReminder.FiredId (identical to FiredKey(event.Id, lead)) and reads DueReminder.LeadMinutes to say
        // "in 15 minutes" vs "in 5 minutes".

        /// <summary>The composite fired-id for one (event, lead) pair: "&lt;eventId&gt;@&lt;lead&gt;". Distinct
        /// per lead so leads de-duplicate independently. The lead is culture-invariant digits.</summary>
        public static string FiredKey(string eventId, int leadMinutes)
        {
            if (leadMinutes < 0) leadMinutes = 0;
            return eventId + "@" + leadMinutes.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Inverse of the eventId part of <see cref="FiredKey"/>: everything before the final '@'.
        /// Lets the caller bound its fired set to the current feed even though its ids are composite (an event
        /// id may itself contain '@', so this splits on the LAST one, which the numeric lead never holds).
        /// Returns the input unchanged if there is no '@'.</summary>
        public static string EventIdOf(string firedKey)
        {
            if (string.IsNullOrEmpty(firedKey)) return firedKey;
            int at = firedKey.LastIndexOf('@');
            return at < 0 ? firedKey : firedKey.Substring(0, at);
        }

        /// <summary>
        /// Every (event, lead) pair that is due right now and has not already fired. Leads may arrive out of
        /// order, duplicated, or negative: they are clamped to >= 0 and de-duplicated, so a repeated or
        /// reordered list can never double-fire or change the result. Each returned <see cref="DueReminder"/>
        /// carries the event, which lead fired it, and the composite id the caller must remember.
        /// </summary>
        public static IReadOnlyList<DueReminder> DueNowMulti(
            IReadOnlyList<CalendarEvent> events, DateTimeOffset now, IReadOnlyList<int> leadMinutes, ISet<string> firedIds)
        {
            var due = new List<DueReminder>();
            if (events == null || leadMinutes == null) return due;

            // Normalize once: clamp negatives to 0 and drop duplicates so order/repeats don't affect output.
            var leads = new List<int>();
            var seenLeads = new HashSet<int>();
            foreach (int raw in leadMinutes)
            {
                int lead = raw < 0 ? 0 : raw;
                if (seenLeads.Add(lead)) leads.Add(lead);
            }
            if (leads.Count == 0) return due;

            foreach (CalendarEvent e in events)
            {
                if (e == null || string.IsNullOrEmpty(e.Id)) continue;
                DateTimeOffset expireAt = e.Start.AddMinutes(GraceMinutes);
                foreach (int lead in leads)
                {
                    string key = FiredKey(e.Id, lead);
                    if (firedIds != null && firedIds.Contains(key)) continue;
                    DateTimeOffset fireAt = e.Start.AddMinutes(-lead);
                    if (now >= fireAt && now < expireAt)
                        due.Add(new DueReminder { Event = e, LeadMinutes = lead, FiredId = key });
                }
            }
            return due;
        }

        // --- self-test ----------------------------------------------------------------------------------
        //
        // Hand-verified against an event E1 whose Start is now + 3 minutes, with leads {15, 5, 1, 15, -10}
        // (out of order, a duplicate 15, and a negative that clamps to 0). Windows at 'now':
        //   lead 15 -> [start-15, start+1] = [now-12, now+4]  -> now inside  -> due
        //   lead  5 -> [start-5,  start+1] = [now-2,  now+4]  -> now inside  -> due
        //   lead  1 -> [start-1,  start+1] = [now+2,  now+4]  -> now before  -> NOT due
        //   lead  0 -> [start,    start+1] = [now+3,  now+4]  -> now before  -> NOT due
        //   duplicate 15 collapses, so E1@15 appears once, not twice.
        // So the first call yields exactly E1@15 and E1@5 (two distinct ids); after recording them the same
        // instant yields nothing; advancing to start-1 opens only the 1-min window and it fires exactly once
        // while the recorded 15/5 stay suppressed.
        // SelfCheck, not SelfTest: see the note in AggregateCalendarSource. ReminderModule.SelfTest aggregates.
        internal static bool SelfCheck(out string detail)
        {
            var sb = new StringBuilder();
            bool ok = true;

            DateTimeOffset now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
            var e = new CalendarEvent { Id = "E1", Title = "Standup", Start = now.AddMinutes(3) };
            var events = new[] { e };
            var leads = new[] { 15, 5, 1, 15, -10 };   // out of order, duplicate 15, negative -> 0
            var fired = new HashSet<string>(StringComparer.Ordinal);

            string id15 = FiredKey("E1", 15);
            string id5 = FiredKey("E1", 5);
            string id1 = FiredKey("E1", 1);
            string id0 = FiredKey("E1", 0);

            // 1) Two in-window leads produce two distinct due-fires with distinct ids.
            IReadOnlyList<DueReminder> first = DueNowMulti(events, now, leads, fired);
            ok &= Check(sb, "two leads come due", first.Count == 2);
            ok &= Check(sb, "distinct ids E1@15 and E1@5", id15 != id5 && Contains(first, id15) && Contains(first, id5));
            // the tag travels with each fire so the caller can phrase "in 15" vs "in 5"
            ok &= Check(sb, "leads reported (15 and 5)", LeadOf(first, id15) == 15 && LeadOf(first, id5) == 5);
            // a lead whose window has not arrived does not fire (and the duplicate 15 did not double up)
            ok &= Check(sb, "future-window leads (1, 0) do not fire", !Contains(first, id1) && !Contains(first, id0));

            foreach (DueReminder d in first) fired.Add(d.FiredId);

            // 2) Once fired, neither re-fires at the same instant.
            IReadOnlyList<DueReminder> second = DueNowMulti(events, now, leads, fired);
            ok &= Check(sb, "no re-fire at same instant", second.Count == 0);

            // 3) Advancing to start-1 opens the 1-min window: it fires once; recorded 15/5 stay suppressed.
            DateTimeOffset later = e.Start.AddMinutes(-1);
            IReadOnlyList<DueReminder> third = DueNowMulti(events, later, leads, fired);
            ok &= Check(sb, "1-min lead fires when its window opens, alone", third.Count == 1 && Contains(third, id1));

            detail = (ok ? "PASS: " : "FAIL: ") + sb.ToString().TrimEnd();
            return ok;
        }

        private static bool Check(StringBuilder sb, string name, bool pass)
        {
            sb.Append(pass ? "[ok] " : "[X] ").Append(name).Append("; ");
            return pass;
        }

        private static bool Contains(IReadOnlyList<DueReminder> list, string firedId)
        {
            foreach (DueReminder d in list) if (d.FiredId == firedId) return true;
            return false;
        }

        private static int LeadOf(IReadOnlyList<DueReminder> list, string firedId)
        {
            foreach (DueReminder d in list) if (d.FiredId == firedId) return d.LeadMinutes;
            return -1;
        }
    }

    /// <summary>One (event, lead) reminder that came due: the event, which lead fired it (so the announcement
    /// can say "in 15 minutes" vs "in 5 minutes"), and the composite id the caller records so that exact pair
    /// never re-fires. <see cref="FiredId"/> equals <see cref="ReminderScheduler.FiredKey"/>(Event.Id, LeadMinutes).</summary>
    public sealed class DueReminder
    {
        public CalendarEvent Event { get; set; }
        public int LeadMinutes { get; set; }
        public string FiredId { get; set; }
    }
}
