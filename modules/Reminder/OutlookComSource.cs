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

        protected override CalendarSnapshot FetchCore(DateTimeOffset now)
        {
            // Attach only to a running Outlook; do not start it.
            if (Process.GetProcessesByName("OUTLOOK").Length == 0)
                return new CalendarSnapshot { Events = LastGood ?? Array.Empty<CalendarEvent>(), Error = "Outlook isn't running — start Outlook to read the local calendar." };

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
                DateTime fromLocal = now.LocalDateTime - WindowBack;
                DateTime toLocal = now.LocalDateTime + WindowForward;
                string filter = "[Start] >= '" + fromLocal.ToString("g", CultureInfo.CurrentCulture) +
                    "' AND [Start] <= '" + toLocal.ToString("g", CultureInfo.CurrentCulture) + "'";
                restricted = itemList.Restrict(filter);
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
