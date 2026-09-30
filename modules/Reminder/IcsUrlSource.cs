using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using Ical.Net;
using Ical.Net.DataTypes;
// This module has its own CalendarEvent DTO; alias iCal.Net's so the source-component cast is unambiguous.
using IcsEvent = Ical.Net.CalendarComponents.CalendarEvent;
using IcsAttendee = Ical.Net.DataTypes.Attendee;

namespace DesktopAICompanion.ReminderModule
{
    /// <summary>
    /// Reads a public/secret iCalendar (.ics) URL: Google Calendar's "Secret address in iCal format", an
    /// Outlook.com / Microsoft 365 published calendar, or iCloud. iCal.Net does the hard parts (RFC 5545
    /// recurrence + VTIMEZONE), so this expands occurrences in a bounded near-term window into the same
    /// <see cref="CalendarEvent"/> shape every other source produces. The fetch runs on a background thread
    /// via <see cref="CachingCalendarSource"/>, so a slow feed never freezes the pet.
    /// </summary>
    public sealed class IcsUrlSource : CachingCalendarSource
    {
        private static readonly TimeSpan WindowBack = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan WindowForward = TimeSpan.FromHours(48);
        private const long MaximumBytes = 8 * 1024 * 1024;
        private const int MaximumOccurrences = 2000;   // guard a runaway (e.g. every-minute) recurrence

        // ONE budget for the whole download, headers and body together. The old shape was HttpClient.Timeout
        // = 20 s with ResponseHeadersRead: that timeout ends when the headers arrive, and the body was then
        // read with no token at all, so a server that sent 200 and stalled mid-body held the fetch, and with
        // it the slot's refresh latch, for as long as it liked (F189; measured on .NET 10 by the audit, the
        // stalled read was still WaitingForActivation eight seconds after a 2 s timeout). 30 s rather than
        // the old 20: the budget now covers the body too, and an 8 MiB feed on a slow link deserves the room.
        private static readonly TimeSpan DownloadDeadline = TimeSpan.FromSeconds(30);

        private static readonly HttpClient Http = CreateClient();
        private readonly Func<string> _urlGetter;

        public IcsUrlSource(Func<string> urlGetter) : base(TimeSpan.FromMinutes(5))
        {
            _urlGetter = urlGetter ?? throw new ArgumentNullException(nameof(urlGetter));
        }

        public override string Name { get { return "Calendar URL"; } }
        protected override string LoadingMessage { get { return "Fetching the calendar…"; } }
        protected override string RefreshKey() { return (_urlGetter() ?? "").Trim(); }

        private static HttpClient CreateClient()
        {
            // Timeout bounds SendAsync, which under ResponseHeadersRead means the headers; the body read in
            // Download carries its own token cut from the same deadline. Both are set so neither can be
            // forgotten when one of them is next edited.
            var http = new HttpClient { Timeout = DownloadDeadline };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopAICompanion-Reminder/1.0");
            return http;
        }

        protected override CalendarSnapshot FetchCore(string key, DateTimeOffset now)
        {
            // The URL the caller's thread read from the settings; not re-read here, this is a pool thread (F192).
            string url = key ?? "";
            if (url.Length == 0)
                return new CalendarSnapshot { Events = Array.Empty<CalendarEvent>(), Error = "No calendar URL is configured." };
            string ics = Download(url, DownloadDeadline, MaximumBytes);
            return ParseIcs(ics, now);
        }

        /// <summary>
        /// GET the feed with both bounds enforced WHILE the body arrives: one deadline over headers and body,
        /// and a size cap checked on every chunk. The cap used to be checked after ReadAsByteArrayAsync had
        /// buffered the whole body -- HttpClient's own MaxResponseContentBufferSize is not consulted on the
        /// unbuffered path -- so a chunked response with no Content-Length could be buffered to 2 GB before the
        /// 8 MiB check ran. Internal, with the bounds as parameters, so the self-test can drive it against a
        /// loopback server with a deadline of milliseconds and a cap of kilobytes; the module passes its
        /// constants. Throws on any refusal; DoRefresh turns that into an error snapshot behind the last good
        /// feed.
        /// </summary>
        internal static string Download(string url, TimeSpan deadline, long maximumBytes)
        {
            // webcal:// is just http(s) by another name; normalize it, and refuse anything that isn't http(s).
            if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url.Substring("webcal://".Length);
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                throw new InvalidOperationException("The calendar URL must be an http(s) or webcal address.");

            // Which phase the deadline fired in, for the message. GetAsync returns once the HEADERS are in
            // (ResponseHeadersRead), so a cancellation before that is a server that did not answer, and one after
            // it is a body that stalled, the F189 shape. One message for both told the user "did not finish
            // downloading" about a feed that never began, and let the self-test's stall check pass on a loaded
            // machine where the client's own connect took the whole deadline, proving nothing about the body read
            // it exists to bound (R-050). HttpClient.Timeout throws the same exception type for the headers phase
            // in production, so it reads the same way here.
            bool headersReceived = false;
            try
            {
                using (var cts = new CancellationTokenSource(deadline))
                using (HttpResponseMessage response = Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                           .GetAwaiter().GetResult())
                {
                    headersReceived = true;
                    response.EnsureSuccessStatusCode();
                    // A declared length over the cap is refused before a byte of body is read. A body that
                    // declares nothing, or lies, is caught by the running total below.
                    if (response.Content.Headers.ContentLength.HasValue &&
                        response.Content.Headers.ContentLength.Value > maximumBytes)
                        throw new InvalidOperationException("The calendar feed is too large.");
                    using (Stream body = response.Content.ReadAsStreamAsync(cts.Token).GetAwaiter().GetResult())
                    using (var buffer = new MemoryStream())
                    {
                        byte[] chunk = new byte[64 * 1024];
                        while (true)
                        {
                            int read = body.ReadAsync(chunk, 0, chunk.Length, cts.Token).GetAwaiter().GetResult();
                            if (read <= 0) break;
                            if (buffer.Length + read > maximumBytes)
                                throw new InvalidOperationException("The calendar feed is too large.");
                            buffer.Write(chunk, 0, read);
                        }
                        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // "A task was canceled." is what the user would otherwise read in the status line.
                string seconds = deadline.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
                throw new TimeoutException(headersReceived
                    ? "The calendar feed did not finish downloading within " + seconds + " seconds."
                    : "The calendar feed did not respond within " + seconds + " seconds.");
            }
        }

        /// <summary>Parse ICS text and expand occurrences in the near-term window. Testable without a network:
        /// give it the raw ICS and "now", which is what <see cref="SelfCheck"/> does. Never throws; a parse
        /// failure returns an error snapshot.</summary>
        internal static CalendarSnapshot ParseIcs(string ics, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(ics))
                return new CalendarSnapshot { Events = Array.Empty<CalendarEvent>(), Error = "The calendar feed was empty." };

            Calendar calendar;
            try { calendar = Calendar.Load(ics); }
            catch (Exception ex)
            {
                return new CalendarSnapshot { Events = Array.Empty<CalendarEvent>(), Error = "The calendar feed is not valid iCalendar: " + Short(ex.Message) };
            }
            if (calendar == null)
                return new CalendarSnapshot { Events = Array.Empty<CalendarEvent>(), Error = "The calendar feed is not valid iCalendar." };

            DateTime fromUtc = now.UtcDateTime - WindowBack;
            DateTime toUtc = now.UtcDateTime + WindowForward;

            var events = new List<CalendarEvent>();
            try
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                int count = 0;
                // GetOccurrences yields ascending occurrences (recurrence + timezones already resolved); stop
                // once we pass the window end, and cap the count so a pathological recurrence can't run away.
                foreach (Occurrence occ in calendar.GetOccurrences(new CalDateTime(fromUtc, "UTC")))
                {
                    if (++count > MaximumOccurrences) break;
                    DateTime startUtc;
                    try { startUtc = occ.Period.StartTime.AsUtc; }
                    catch { continue; }
                    if (startUtc >= toUtc) break;

                    var source = occ.Source as IcsEvent;
                    string uid = source != null && !string.IsNullOrEmpty(source.Uid) ? source.Uid : "ics";
                    string id = uid + "@" + startUtc.ToString("o");
                    if (!seen.Add(id)) continue;

                    DateTimeOffset? endOffset = null;
                    try
                    {
                        CalDateTime end = occ.Period.EffectiveEndTime;
                        if (end != null) endOffset = new DateTimeOffset(end.AsUtc, TimeSpan.Zero);
                    }
                    catch { }

                    events.Add(new CalendarEvent
                    {
                        Id = id,
                        Title = source != null ? source.Summary : null,
                        Start = new DateTimeOffset(startUtc, TimeSpan.Zero),
                        End = endOffset,
                        Location = source != null ? source.Location : null,
                        Description = source != null ? source.Description : null,
                        AllDay = source != null && source.IsAllDay,
                        Attendees = ReadIcsAttendees(source),
                        // ResponseStatus left null: an .ics feed carries per-attendee PARTSTAT, not "my" status.
                    });
                }
            }
            catch (Exception ex)
            {
                return new CalendarSnapshot { Events = Array.Empty<CalendarEvent>(), Error = "Could not expand the calendar's events: " + Short(ex.Message) };
            }

            return new CalendarSnapshot { Events = events, Updated = now, Error = null };
        }

        // The invited roster from an .ics VEVENT: the CN (or the mailto address) + PARTSTAT normalized.
        private static List<Attendee> ReadIcsAttendees(IcsEvent source)
        {
            if (source == null || source.Attendees == null || source.Attendees.Count == 0) return null;
            var list = new List<Attendee>();
            foreach (IcsAttendee a in source.Attendees)
            {
                if (a == null) continue;
                string name = a.CommonName;
                if (string.IsNullOrWhiteSpace(name) && a.Value != null)
                {
                    string s = a.Value.ToString();
                    const string mailto = "mailto:";
                    name = s.StartsWith(mailto, StringComparison.OrdinalIgnoreCase) ? s.Substring(mailto.Length) : s;
                }
                if (string.IsNullOrWhiteSpace(name)) continue;
                list.Add(new Attendee { Name = name.Trim(), Status = MapPartStat(a.ParticipationStatus) });
            }
            return list.Count > 0 ? list : null;
        }

        private static string MapPartStat(string partstat)
        {
            if (string.IsNullOrWhiteSpace(partstat)) return "";
            switch (partstat.Trim().ToUpperInvariant())
            {
                case "ACCEPTED": return "accepted";
                case "DECLINED": return "declined";
                case "TENTATIVE": return "tentative";
                default: return "";
            }
        }

        // --- self-test --------------------------------------------------------------------------------

        /// <summary>
        /// The parse half of this source, over an embedded feed: the near-term window, the uid@startUtc id and
        /// the dedupe it keys, an EXDATE'd recurrence, the all-day flag, the attendee CN/mailto and PARTSTAT
        /// mapping, and the never-throws promise on garbage. Only the fetch needs a network; this never did, and
        /// nothing ran it until F190. A fixed "now" (2026-03-10 22:00Z), so the window is [21:58Z on the 10th,
        /// 22:00Z on the 12th]; the all-day event's local midnight on the 12th lands inside it from any zone
        /// between UTC-12 and UTC+14.
        /// </summary>
        // SelfCheck, not SelfTest: see the note in AggregateCalendarSource. ReminderModule.SelfTest aggregates.
        internal static bool SelfCheck(out string detail)
        {
            var sb = new System.Text.StringBuilder();
            bool ok = true;
            var now = new DateTimeOffset(2026, 3, 10, 22, 0, 0, TimeSpan.Zero);
            string feed = string.Join("\n", new[]
            {
                "BEGIN:VCALENDAR",
                "VERSION:2.0",
                "PRODID:-//DesktopAICompanion//Reminder self-check//EN",
                "BEGIN:VEVENT",
                "UID:daily@test",
                "DTSTART:20260309T230000Z",
                "DTEND:20260309T233000Z",
                "RRULE:FREQ=DAILY;COUNT=5",
                "EXDATE:20260311T230000Z",
                "SUMMARY:Daily sync",
                "LOCATION:Teams",
                "ATTENDEE;CN=Ada Lovelace;PARTSTAT=ACCEPTED:mailto:ada@example.invalid",
                "ATTENDEE;PARTSTAT=TENTATIVE:mailto:bob@example.invalid",
                "END:VEVENT",
                "BEGIN:VEVENT",
                "UID:allday@test",
                "DTSTART;VALUE=DATE:20260312",
                "DTEND;VALUE=DATE:20260313",
                "SUMMARY:Offsite",
                "END:VEVENT",
                "BEGIN:VEVENT",
                "UID:far@test",
                "DTSTART:20260320T100000Z",
                "DTEND:20260320T110000Z",
                "SUMMARY:Far away",
                "END:VEVENT",
                "BEGIN:VEVENT",
                "UID:dup@test",
                "DTSTART:20260311T090000Z",
                "DTEND:20260311T093000Z",
                "SUMMARY:Listed twice",
                "END:VEVENT",
                "BEGIN:VEVENT",
                "UID:dup@test",
                "DTSTART:20260311T090000Z",
                "DTEND:20260311T093000Z",
                "SUMMARY:Listed twice",
                "END:VEVENT",
                "END:VCALENDAR",
            });

            CalendarSnapshot snap = ParseIcs(feed, now);
            ok &= Check(sb, "a well-formed feed parses without an error", snap.Error == null && snap.Events != null);
            List<CalendarEvent> daily = WithUid(snap, "daily@test");
            var dailyStart = new DateTimeOffset(2026, 3, 10, 23, 0, 0, TimeSpan.Zero);
            ok &= Check(sb, "a daily series keeps the one occurrence its EXDATE and the window leave (" + daily.Count + ")",
                daily.Count == 1 && daily[0].Start == dailyStart);
            ok &= Check(sb, "an occurrence id is uid@startUtc, so a series never collapses to one fired id",
                daily.Count == 1 && daily[0].Id.StartsWith("daily@test@2026-03-10T23:00:00", StringComparison.Ordinal));
            ok &= Check(sb, "the title and location travel and a timed event is not all-day",
                daily.Count == 1 && daily[0].Title == "Daily sync" && daily[0].Location == "Teams" && !daily[0].AllDay);
            ok &= Check(sb, "attendees map CN or the mailto address, and PARTSTAT",
                daily.Count == 1 && daily[0].Attendees != null && daily[0].Attendees.Count == 2 &&
                daily[0].Attendees[0].Name == "Ada Lovelace" && daily[0].Attendees[0].Status == "accepted" &&
                daily[0].Attendees[1].Name == "bob@example.invalid" && daily[0].Attendees[1].Status == "tentative");
            List<CalendarEvent> allDay = WithUid(snap, "allday@test");
            ok &= Check(sb, "an all-day event is flagged (" + allDay.Count + ")", allDay.Count == 1 && allDay[0].AllDay);
            ok &= Check(sb, "an event past the 48-hour window is left out", WithUid(snap, "far@test").Count == 0);
            ok &= Check(sb, "a duplicate listing is one event", WithUid(snap, "dup@test").Count == 1);
            ok &= Check(sb, "nothing else arrived (" + (snap.Events == null ? -1 : snap.Events.Count) + ")",
                snap.Events != null && snap.Events.Count == 3);

            CalendarSnapshot empty = ParseIcs("   ", now);
            ok &= Check(sb, "an empty feed is an error snapshot, not an exception",
                empty.Error != null && empty.Events != null && empty.Events.Count == 0);
            // Error asserted too, not only "no events": reported as a healthy empty calendar, garbage would make
            // the slot vouch for an empty feed and the F204 prune would drop every fired id it holds (RA-170).
            CalendarSnapshot garbage = ParseIcs("this is not a calendar", now);
            ok &= Check(sb, "garbage is an error snapshot with no events, not an empty healthy calendar (and never throws)",
                garbage.Error != null && garbage.Events != null && garbage.Events.Count == 0);

            sb.AppendLine(ok ? "IcsUrlSource self-test PASSED" : "IcsUrlSource self-test FAILED");
            detail = sb.ToString();
            return ok;
        }

        private static List<CalendarEvent> WithUid(CalendarSnapshot snap, string uid)
        {
            var list = new List<CalendarEvent>();
            if (snap == null || snap.Events == null) return list;
            foreach (CalendarEvent e in snap.Events)
                if (e != null && e.Id != null && e.Id.StartsWith(uid + "@", StringComparison.Ordinal)) list.Add(e);
            return list;
        }

        private static bool Check(System.Text.StringBuilder sb, string name, bool condition)
        {
            sb.AppendLine((condition ? "  ok   " : "  FAIL ") + name);
            return condition;
        }
    }
}
