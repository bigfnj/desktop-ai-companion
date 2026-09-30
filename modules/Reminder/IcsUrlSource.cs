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

            try
            {
                using (var cts = new CancellationTokenSource(deadline))
                using (HttpResponseMessage response = Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                           .GetAwaiter().GetResult())
                {
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
                throw new TimeoutException("The calendar feed did not finish downloading within "
                    + deadline.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " seconds.");
            }
        }

        /// <summary>Parse ICS text and expand occurrences in the near-term window. Testable without a network:
        /// give it the raw ICS and "now". Never throws; a parse failure returns an error snapshot.</summary>
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
    }
}
