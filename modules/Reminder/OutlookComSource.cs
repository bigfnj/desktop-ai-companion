using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace DesktopAICompanion.ReminderModule
{
    /// <summary>
    /// Reads the locally-installed desktop Outlook's default calendar over late-bound COM (no PIA reference, so
    /// it works with whatever Outlook version is installed). This is the "local Outlook" path, distinct from a
    /// published Outlook/M365 URL (which the <see cref="IcsUrlSource"/> already covers).
    ///
    /// Deliberately non-intrusive: it only ATTACHES to an Outlook that is already running (Outlook is a
    /// single-instance COM server, so creating the Application object returns the live one) and never launches
    /// Outlook itself — a background reminder poller silently starting Outlook would be hostile. Runs on an STA
    /// thread off the UI thread (see <see cref="CachingCalendarSource"/>), releases every COM object it takes,
    /// and never quits the user's Outlook.
    /// </summary>
    public sealed class OutlookComSource : CachingCalendarSource
    {
        private const int OlFolderCalendar = 9;
        private static readonly TimeSpan WindowBack = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan WindowForward = TimeSpan.FromHours(48);
        private const int MaximumOccurrences = 2000;

        public OutlookComSource() : base(TimeSpan.FromMinutes(2)) { }

        public override string Name { get { return "Local Outlook"; } }
        protected override string LoadingMessage { get { return "Reading Outlook…"; } }
        protected override bool RequiresSta { get { return true; } }   // COM to Outlook wants an STA thread
        protected override string RefreshKey() { return "outlook"; }   // no per-config key; refresh on interval

        protected override CalendarSnapshot FetchCore(string key, DateTimeOffset now)
        {
            // `key` is the constant above: this source reads no setting, so there is nothing to thread through.
            // Attach only to a running Outlook; do not start it.
            if (Process.GetProcessesByName("OUTLOOK").Length == 0)
                // Array.Empty, not LastGood: CachingCalendarSource.DoRefresh now restores the last good
                // feed behind ANY error snapshot that brought nothing. This line was the only one of the
                // eight error returns across the three sources that remembered to do it by hand.
                return new CalendarSnapshot { Events = Array.Empty<CalendarEvent>(), Error = "Outlook isn't running — start Outlook to read the local calendar." };

            Type appType = Type.GetTypeFromProgID("Outlook.Application");
            if (appType == null)
                return new CalendarSnapshot { Events = Array.Empty<CalendarEvent>(), Error = "Outlook is not installed on this PC." };

            object app = null, ns = null, folder = null, items = null, restricted = null;
            try
            {
                app = Activator.CreateInstance(appType);            // the single running instance
                dynamic application = app;
                ns = application.GetNamespace("MAPI");
                dynamic mapi = ns;
                folder = mapi.GetDefaultFolder(OlFolderCalendar);
                dynamic calendar = folder;
                items = calendar.Items;
                dynamic itemList = items;

                // Standard Outlook idiom for expanding recurrences over a range: sort by Start ascending,
                // turn on IncludeRecurrences, THEN Restrict to the window. Dates in the current locale format.
                itemList.IncludeRecurrences = true;
                itemList.Sort("[Start]");
                restricted = itemList.Restrict(RestrictFilter(now.LocalDateTime - WindowBack, now.LocalDateTime + WindowForward, CultureInfo.CurrentCulture));
                dynamic range = restricted;

                var events = new List<CalendarEvent>();
                int count = 0;
                foreach (dynamic appt in range)
                {
                    object apptObj = appt;
                    try
                    {
                        if (++count > MaximumOccurrences) break;
                        DateTime startLocal = appt.Start;               // AppointmentItem.Start (local)
                        var start = new DateTimeOffset(DateTime.SpecifyKind(startLocal, DateTimeKind.Local));
                        DateTimeOffset? end = null;
                        try { end = new DateTimeOffset(DateTime.SpecifyKind((DateTime)appt.End, DateTimeKind.Local)); }
                        catch { }
                        string entryId;
                        try { entryId = (string)appt.EntryID; } catch { entryId = null; }
                        string id = (string.IsNullOrEmpty(entryId) ? "outlook" : entryId) + "@" + start.UtcDateTime.ToString("o");

                        string title = null, location = null, body = null;
                        try { title = (string)appt.Subject; } catch { }
                        try { location = (string)appt.Location; } catch { }
                        try { body = (string)appt.Body; } catch { }
                        if (body != null && body.Length > 8192) body = body.Substring(0, 8192);   // a body can be a whole thread; the join link is near the top
                        bool allDay = false;
                        try { allDay = (bool)appt.AllDayEvent; } catch { }
                        string response = "";
                        try { response = MapResponse((int)appt.ResponseStatus); } catch { }

                        events.Add(new CalendarEvent
                        {
                            Id = id, Title = title, Start = start, End = end, Location = location,
                            Description = body, AllDay = allDay, ResponseStatus = response,
                            Attendees = ReadOutlookAttendees(appt),
                        });
                    }
                    catch { /* skip a stray non-appointment item */ }
                    finally { Release(apptObj); }
                }

                return new CalendarSnapshot { Events = events, Updated = now, Error = null };
            }
            finally
            {
                // Release everything we took, in reverse. Never Quit(): that would close the user's Outlook.
                Release(restricted); Release(items); Release(folder); Release(ns); Release(app);
            }
        }

        /// <summary>
        /// The Items.Restrict filter for the window: every appointment that has not ENDED by <paramref name="fromLocal"/>
        /// and starts by <paramref name="toLocal"/>. It was "[Start] >= from", which dropped a meeting two minutes after
        /// it began: meeting.current, the Join tray entry and Remembrance's record offer all read the meeting the user
        /// is in from this list, so each went blank a few minutes into every Outlook meeting (N-audit-1106-042). Nothing
        /// announces twice for being kept: a lead fires only up to a minute past the start and the join question only
        /// three, both against the fired set. Dates in the locale format Outlook parses, which is why the culture is a
        /// parameter rather than read here: the self-check pins the shape under the invariant culture.
        /// </summary>
        internal static string RestrictFilter(DateTime fromLocal, DateTime toLocal, CultureInfo culture)
        {
            return "[End] >= '" + fromLocal.ToString("g", culture) +
                "' AND [Start] <= '" + toLocal.ToString("g", culture) + "'";
        }

        // SelfCheck, not SelfTest: see the note in AggregateCalendarSource. ReminderModule.SelfTest aggregates. The COM
        // read itself needs a running Outlook and is not reachable here; the window it asks Outlook for is.
        internal static bool SelfCheck(out string detail)
        {
            var now = new DateTime(2026, 3, 10, 22, 0, 0, DateTimeKind.Local);
            string filter = RestrictFilter(now - WindowBack, now + WindowForward, CultureInfo.InvariantCulture);
            string from = (now - WindowBack).ToString("g", CultureInfo.InvariantCulture);
            string to = (now + WindowForward).ToString("g", CultureInfo.InvariantCulture);
            bool onEnd = filter.Contains("[End] >= '" + from + "'", StringComparison.Ordinal);
            bool startBound = filter.Contains("[Start] <= '" + to + "'", StringComparison.Ordinal);
            bool noStartFloor = !filter.Contains("[Start] >= ", StringComparison.Ordinal);
            bool ok = onEnd && startBound && noStartFloor;
            detail = (ok ? "PASS: " : "FAIL: ")
                + (onEnd ? "[ok] " : "[X] ") + "a meeting that started before the window and has not ended is asked for (on [End]); "
                + (startBound ? "[ok] " : "[X] ") + "WITNESS the window still ends 48 hours out (on [Start]); "
                + (noStartFloor ? "[ok] " : "[X] ") + "no floor on [Start] drops a meeting the user is in; filter: " + filter;
            return ok;
        }

        // The meeting's invited roster from AppointmentItem.Recipients (1-based COM collection). Each recipient
        // gives a Name and a per-recipient MeetingResponseStatus (same OlResponseStatus values as MapResponse).
        // Non-meeting appointments have no recipients; that comes back null. Every COM object taken is released.
        private static List<Attendee> ReadOutlookAttendees(dynamic appt)
        {
            var list = new List<Attendee>();
            object recipientsObj = null;
            try
            {
                recipientsObj = appt.Recipients;
                if (recipientsObj == null) return null;
                dynamic recipients = recipientsObj;
                int count = (int)recipients.Count;
                for (int i = 1; i <= count; i++)
                {
                    object recObj = recipients[i];
                    try
                    {
                        dynamic rec = recObj;
                        string name = null;
                        try { name = (string)rec.Name; } catch { }
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        string status = "";
                        try { status = MapResponse((int)rec.MeetingResponseStatus); } catch { }
                        list.Add(new Attendee { Name = name.Trim(), Status = status });
                    }
                    catch { }
                    finally { Release(recObj); }
                }
            }
            catch { }
            finally { Release(recipientsObj); }
            return list.Count > 0 ? list : null;
        }

        // OlResponseStatus -> the module's normalized status. 4=declined, 2=tentative, 1=organized/3=accepted.
        private static string MapResponse(int status)
        {
            switch (status)
            {
                case 4: return "declined";
                case 2: return "tentative";
                case 1:
                case 3: return "accepted";
                default: return "";
            }
        }

        private static void Release(object comObject)
        {
            try { if (comObject != null && Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject); }
            catch { }
        }
    }
}
