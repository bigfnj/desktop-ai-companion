using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace DesktopAICompanion.ReminderModule
{
    /// <summary>
    /// The Zoom slot's source (1.3.0): the scheduled meetings the signed-in Zoom user HOSTS, read from Zoom's REST API
    /// (GET /users/me/meetings?type=upcoming). Meetings someone else hosts and invited the user to are not in that
    /// list (Zoom's own documentation says it lists a host's meetings); they usually arrive as calendar invites, which
    /// the user's Outlook or .ics slot already reads, and the aggregate drops the Zoom copy of a meeting both carry.
    ///
    /// When the user's Zoom account has a calendar connected (so the meetings they are INVITED to show in the Zoom
    /// client but reach no slot of this app), the card's "Also read meetings I'm invited to" switch adds a second read,
    /// GET /users/me/upcoming_meetings, which Zoom documents as the meetings the user is invited to within the next 24
    /// hours. It needs one more scope (meeting:read:list_upcoming_meetings) and is OFF by default, so a sign-in that was
    /// set up for hosted meetings only never fails on a scope it was not granted. Its meetings are marked invited: no
    /// per-series detail call (the user does not host them, so GET /meetings/{id} would be refused), the status left
    /// unknown rather than "accepted", and a failure of THIS read is a warning beside the hosted meetings, never a
    /// reason to drop them. Whether that endpoint returns a given connected-calendar meeting is Zoom's to decide; the
    /// switch is how the user finds out for their account.
    ///
    /// A recurring meeting with a fixed time (type 8) may appear in the list once, with one of its times, so each
    /// distinct series is read again by id (GET /meetings/{id}) for its occurrences[], which carry their own start,
    /// duration and status. Instant meetings (type 1) and recurring meetings with no fixed time (type 3) have no
    /// time to remind about and are left out. The same 2-minute / 48-hour window as the other sources.
    ///
    /// Anything that leaves the read short of the whole picture -- the page cap reached with more to come, the series
    /// cap, a series that could not be read -- is a warning in Error, so the status line shows it AND the fired-id
    /// prune treats the slot as unvouched (F204): a healthy-looking partial list would tell the prune that every
    /// meeting it did not reach had been cancelled. One 60-second budget covers the meeting requests, each getting what
    /// is left of it (at most 30 s), so twenty-odd requests cannot hold the slot's refresh for ten minutes. The token is
    /// outside that budget: a renewal (the first of a read, or the one after a 401, which is not tried once the budget
    /// is spent) has its own 30-second bound and may first wait for another renewal in flight. So a read ends within
    /// about two minutes, not one.
    ///
    /// FetchCore reads no settings (F192): the client ID and the sign-in generation travel in the key.
    /// </summary>
    internal sealed class ZoomMeetingsSource : CachingCalendarSource
    {
        internal const string ApiBase = "https://api.zoom.us/v2";
        internal const int Scheduled = 2;
        internal const int RecurringFixedTime = 8;
        internal const int MaximumPages = 5;
        internal const int PageSize = 300;
        internal const int MaximumSeriesLookups = 20;
        internal static readonly TimeSpan FetchBudget = TimeSpan.FromSeconds(60);

        private static readonly TimeSpan WindowBack = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan WindowForward = TimeSpan.FromHours(48);
        private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(30);
        private const long MaximumBytes = 2 * 1024 * 1024;
        private const int MaximumOccurrences = 2000;
        private const int MaximumDescriptionCharacters = 8192;

        /// <summary>One GET with a bearer token and a deadline. The shipped one is <see cref="ZoomHttp.Get"/>; the
        /// self-test hands a fake that answers from fixtures and counts what it was asked.</summary>
        internal delegate ZoomHttp.Response Getter(string url, string bearer, TimeSpan deadline);

        private readonly Func<string> _clientIdGetter;
        private readonly Func<bool> _readInvited;
        private readonly Func<int> _generation;
        private readonly IZoomAccessToken _tokens;
        private readonly Func<string> _tokenWarning;
        private readonly Getter _get;

        /// <param name="readInvited">The card's "Also read meetings I'm invited to" switch, read on the caller's thread.</param>
        /// <param name="get">Null for the shipped HTTP path; the module's self-test hands a fake.</param>
        internal ZoomMeetingsSource(Func<string> clientIdGetter, Func<bool> readInvited, ZoomTokenStore store, Getter get)
            : this(clientIdGetter,
                   readInvited,
                   delegate { return store.Generation; },
                   store,
                   delegate { return store.LastWriteFailed ? RenewalNotSavedWarning : null; },
                   get)
        { }

        internal ZoomMeetingsSource(Func<string> clientIdGetter, Func<bool> readInvited, Func<int> generation, IZoomAccessToken tokens,
            Func<string> tokenWarning, Getter get)
            : base(TimeSpan.FromMinutes(5))
        {
            _clientIdGetter = clientIdGetter ?? throw new ArgumentNullException(nameof(clientIdGetter));
            _readInvited = readInvited ?? delegate { return false; };
            _generation = generation ?? throw new ArgumentNullException(nameof(generation));
            _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
            _tokenWarning = tokenWarning ?? delegate { return null; };
            _get = get ?? delegate(string url, string bearer, TimeSpan deadline) { return ZoomHttp.Get(url, bearer, deadline, MaximumBytes); };
        }

        internal const string RenewalNotSavedWarning =
            "the renewed Zoom sign-in could not be saved; it works until the app closes and the save is retried on every check";

        public override string Name { get { return "Zoom"; } }
        protected override string LoadingMessage { get { return "Reading your Zoom meetings…"; } }

        // All three parts are read on the caller's thread: the client ID and the invited switch from the settings, the
        // generation from the token store (a volatile int, safe anywhere). The switch is in the key so turning it on or
        // off is read at once, and so meetings read with it on are never served as the answer with it off (R-043).
        protected override string RefreshKey()
        {
            return (_clientIdGetter() ?? "").Trim() + "|" + _generation().ToString(CultureInfo.InvariantCulture)
                + "|" + (_readInvited() ? "1" : "0");
        }

        /// <summary>The fetch the base runs on a pool thread, callable directly by the self-test with a fixed now.</summary>
        internal CalendarSnapshot FetchForSelfTest(DateTimeOffset now) { return FetchCore(RefreshKey(), now); }

        protected override CalendarSnapshot FetchCore(string key, DateTimeOffset now)
        {
            // clientId|generation|invited. A client ID never holds '|' (ZoomSignIn.CheckClientId), so the bars are the
            // boundaries.
            string raw = key ?? "";
            int invitedBar = raw.LastIndexOf('|');
            if (invitedBar < 0) return Fail("The Zoom slot was handed a key it does not understand.");
            string invitedFlag = raw.Substring(invitedBar + 1);
            if (invitedFlag != "0" && invitedFlag != "1") return Fail("The Zoom slot was handed a key it does not understand.");
            bool readInvited = invitedFlag == "1";
            string head = raw.Substring(0, invitedBar);
            int bar = head.LastIndexOf('|');
            int generation;
            if (bar < 0 || !int.TryParse(head.Substring(bar + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out generation))
                return Fail("The Zoom slot was handed a key it does not understand.");
            string clientId;
            string problem = ZoomSignIn.CheckClientId(head.Substring(0, bar), out clientId);
            if (problem != null)
                return Fail(problem + " Make a Zoom app as ZOOM.md describes and paste its client ID on the Zoom card.");

            string error;
            var session = new Session(this, clientId, DateTimeOffset.UtcNow + FetchBudget);
            if (!session.Start(out error)) return Fail(error);

            var meetings = new List<ZoomMeeting>();
            var warnings = new List<string>();
            string pageToken = null;
            int unreadable = 0;
            for (int page = 1; ; page++)
            {
                string url = ApiBase + "/users/me/meetings?type=upcoming&page_size=" + PageSize.ToString(CultureInfo.InvariantCulture)
                    + (pageToken == null ? "" : "&next_page_token=" + Uri.EscapeDataString(pageToken));
                ZoomHttp.Response response = session.Get(url, out error);
                if (response == null) return Fail(error);
                string next;
                int skipped;
                if (!ParseList(response.Body, meetings, out next, out skipped)) return Fail("Zoom's meeting list could not be read.");
                unreadable += skipped;
                if (string.IsNullOrEmpty(next)) break;
                if (page >= MaximumPages)
                {
                    warnings.Add("Zoom lists more than " + (MaximumPages * PageSize).ToString(CultureInfo.InvariantCulture)
                        + " upcoming meetings and only the first ones were read");
                    break;
                }
                pageToken = next;
            }
            if (unreadable > 0)
                warnings.Add(unreadable.ToString(CultureInfo.InvariantCulture) + " meeting(s) in Zoom's list could not be read");

            // Each series once, in list order, before the cap is applied: the list can name a series more than once.
            var series = new List<string>();
            var seenSeries = new HashSet<string>(StringComparer.Ordinal);
            foreach (ZoomMeeting m in meetings)
                if (m.Type == RecurringFixedTime && seenSeries.Add(m.Id)) series.Add(m.Id);
            var occurrences = new Dictionary<string, List<ZoomOccurrence>>(StringComparer.Ordinal);
            int unexpanded = 0;
            string why = null;
            for (int i = 0; i < series.Count; i++)
            {
                if (i >= MaximumSeriesLookups)
                {
                    unexpanded += series.Count - i;
                    why = why ?? "more than " + MaximumSeriesLookups.ToString(CultureInfo.InvariantCulture) + " recurring meetings";
                    break;
                }
                ZoomHttp.Response response = session.Get(ApiBase + "/meetings/" + series[i], out error);
                List<ZoomOccurrence> occ;
                if (response != null && ParseOccurrences(response.Body, out occ)) { occurrences[series[i]] = occ; continue; }
                unexpanded++;
                why = why ?? (response == null ? TrimSentence(error) : "an answer that could not be read");
            }
            if (unexpanded > 0)
                warnings.Add(unexpanded.ToString(CultureInfo.InvariantCulture) + " recurring Zoom meeting(s) could not be expanded ("
                    + why + "), so some of their times may be missing");

            // The meetings the user is invited to, when the card asks for them. After the hosted read on purpose: that
            // one has already proved the sign-in works, so whatever goes wrong here is about this read alone, and it is
            // said beside the hosted meetings rather than instead of them.
            if (readInvited)
            {
                string invitedWarning = ReadInvited(session, meetings);
                if (invitedWarning != null) warnings.Add(invitedWarning);
            }

            // A sign-in or sign-out while this ran: what was read belongs to the account that is no longer there.
            if (_generation() != generation)
                return Fail("The Zoom sign-in changed while the meetings were being read; the next check reads the new one.");

            CalendarSnapshot snap = Expand(meetings, occurrences, now);
            if (!string.IsNullOrEmpty(snap.Error)) warnings.Add(snap.Error);
            string tokenWarning = _tokenWarning();
            if (!string.IsNullOrEmpty(tokenWarning)) warnings.Add(tokenWarning);
            snap.Error = warnings.Count > 0 ? Capitalise(string.Join("; ", warnings)) + "." : null;
            return snap;
        }

        private static CalendarSnapshot Fail(string error)
        {
            return new CalendarSnapshot { Events = Array.Empty<CalendarEvent>(), Error = error };
        }

        /// <summary>The scopes each read needs, named in the refusal when Zoom says one is missing.</summary>
        internal const string HostedScopes = "meeting:read:list_meetings and meeting:read:meeting";
        internal const string InvitedScope = "meeting:read:list_upcoming_meetings";

        /// <summary>
        /// GET /users/me/upcoming_meetings, following next_page_token up to the page cap, appended to
        /// <paramref name="into"/> marked invited. Null when it read cleanly, otherwise the warning the caller puts beside
        /// the hosted meetings. What arrived before a failure is kept: the warning already leaves the slot unvouched, so
        /// the prune cannot take what did not arrive for cancelled. No query parameters on the first request: Zoom
        /// documents the endpoint by its window (the next 24 hours), not by paging options, and an unknown parameter is
        /// not worth a refusal. A meeting also in the hosted list keeps its hosted copy (Expand's dedupe, hosted first).
        /// </summary>
        private static string ReadInvited(Session session, List<ZoomMeeting> into)
        {
            var invited = new List<ZoomMeeting>();
            string pageToken = null;
            int unreadable = 0;
            string warning = null;
            for (int page = 1; ; page++)
            {
                string url = ApiBase + "/users/me/upcoming_meetings"
                    + (pageToken == null ? "" : "?next_page_token=" + Uri.EscapeDataString(pageToken));
                string error;
                ZoomHttp.Response response = session.Get(url, InvitedScope, out error);
                if (response == null) { warning = "the meetings you are invited to could not be read (" + TrimSentence(error) + ")"; break; }
                string next;
                int skipped;
                if (!ParseList(response.Body, invited, out next, out skipped))
                {
                    // A bare {} is named apart from other unreadable answers: it is a fault Zoom has acknowledged on
                    // this endpoint (its developer forum, 2026, reported with a connected calendar), and the user
                    // checking whether invites come through needs to tell "Zoom sent nothing" from "Zoom sent no such
                    // meeting". Still a warning, not an empty list, for the same F204 reason as any other.
                    warning = IsEmptyObject(response.Body)
                        ? EmptyInvitedAnswerWarning
                        : "the meetings you are invited to could not be read (an answer that could not be read)";
                    break;
                }
                unreadable += skipped;
                if (string.IsNullOrEmpty(next)) break;
                if (page >= MaximumPages) { warning = "Zoom lists more meetings you are invited to than were read"; break; }
                pageToken = next;
            }
            foreach (ZoomMeeting m in invited)
            {
                // Every invited row must carry its own time. ParseList only demands one of a scheduled meeting, because
                // a hosted recurring row may lean on the series call; an invited row cannot (that call is the host's),
                // so a row with no readable start is unreadable HERE, a warning, never a silent gap the slot would
                // report as healthy and the prune would take for a cancellation (review of the invited delta).
                if (m.StartUtc == null) { unreadable++; continue; }
                m.Invited = true;
                into.Add(m);
            }
            if (unreadable > 0)
            {
                string skippedWarning = unreadable.ToString(CultureInfo.InvariantCulture) + " meeting(s) you are invited to could not be read";
                warning = warning == null ? skippedWarning : warning + "; " + skippedWarning;
            }
            return warning;
        }

        internal const string EmptyInvitedAnswerWarning =
            "Zoom sent an empty answer for the meetings you are invited to (a fault on Zoom's side; ZOOM.md says what to do)";

        /// <summary>True for a JSON object with no members, whitespace allowed: Zoom's empty answer.</summary>
        internal static bool IsEmptyObject(string body)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(body ?? ""))
                {
                    return doc.RootElement.ValueKind == JsonValueKind.Object && !doc.RootElement.EnumerateObject().MoveNext();
                }
            }
            catch (JsonException) { return false; }
        }

        private static string TrimSentence(string s) { return (s ?? "no reason given").TrimEnd('.', ' '); }

        private static string Capitalise(string s)
        {
            return string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        /// <summary>One read's access token and its budget. A 401 renews once, naming the token Zoom refused, and the
        /// request is sent again; a second 401 is the answer.</summary>
        private sealed class Session
        {
            private readonly ZoomMeetingsSource _owner;
            private readonly string _clientId;
            private readonly DateTimeOffset _deadlineUtc;
            private string _token;

            internal Session(ZoomMeetingsSource owner, string clientId, DateTimeOffset deadlineUtc)
            {
                _owner = owner;
                _clientId = clientId;
                _deadlineUtc = deadlineUtc;
            }

            internal bool Start(out string error)
            {
                _token = _owner._tokens.GetAccessToken(_clientId, null, out error);
                return _token != null;
            }

            internal ZoomHttp.Response Get(string url, out string error) { return Get(url, HostedScopes, out error); }

            /// <param name="scopes">What a missing-scope refusal names: the scope this request needs.</param>
            internal ZoomHttp.Response Get(string url, string scopes, out string error)
            {
                ZoomHttp.Response response = Send(url, out error);
                if (response == null) return null;
                if (response.Status == 401)
                {
                    if (_deadlineUtc - DateTimeOffset.UtcNow <= TimeSpan.Zero)
                    {
                        error = "The Zoom check ran out of time (" + FetchBudget.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " seconds for the whole read).";
                        return null;
                    }
                    string renewed = _owner._tokens.GetAccessToken(_clientId, _token, out error);
                    if (renewed == null) return null;
                    _token = renewed;
                    response = Send(url, out error);
                    if (response == null) return null;
                }
                if (response.Status == 200) { error = null; return response; }
                error = Describe(response, scopes);
                return null;
            }

            private ZoomHttp.Response Send(string url, out string error)
            {
                TimeSpan left = _deadlineUtc - DateTimeOffset.UtcNow;
                if (left <= TimeSpan.Zero)
                {
                    error = "The Zoom check ran out of time (" + FetchBudget.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " seconds for the whole read).";
                    return null;
                }
                try
                {
                    ZoomHttp.Response response = _owner._get(url, _token, left < RequestDeadline ? left : RequestDeadline);
                    error = response == null ? "Zoom gave no answer." : null;
                    return response;
                }
                catch (TimeoutException ex) { error = ex.Message; return null; }
                catch (Exception ex) { error = "Zoom could not be reached (" + ex.GetType().Name + ")."; return null; }
            }
        }

        /// <summary>
        /// A refusal in the status line's words. Zoom's own message text is NOT echoed: it can name the account (a
        /// "user does not exist" names the address), and the line is also written to the diagnostics log, which users
        /// are told they may attach to an issue. Only the status and Zoom's numeric code are kept.
        /// </summary>
        internal static string Describe(ZoomHttp.Response response) { return Describe(response, HostedScopes); }

        internal static string Describe(ZoomHttp.Response response, string scopes)
        {
            int status = response == null ? 0 : response.Status;
            int code = ErrorCode(response == null ? null : response.Body);
            if (status == 401)
                return "Zoom refused the sign-in. Press Sign in to Zoom again.";
            if (code == 4700 || code == 4711)
                return "The Zoom app is missing a scope it needs (" + (scopes ?? HostedScopes) + "). ZOOM.md lists them.";
            if (status == 429)
                return "Zoom asked for fewer requests (HTTP 429); the next check tries again.";
            return "Zoom answered HTTP " + status.ToString(CultureInfo.InvariantCulture)
                + (code > 0 ? " (Zoom code " + code.ToString(CultureInfo.InvariantCulture) + ")" : "") + ".";
        }

        private static int ErrorCode(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return 0;
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(body))
                {
                    JsonElement code;
                    int value;
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("code", out code)
                        && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out value))
                        return value;
                }
            }
            catch (JsonException) { }
            return 0;
        }

        // --- parsing, pure ---------------------------------------------------------------------------

        internal sealed class ZoomMeeting
        {
            public string Id;
            public int Type;
            public string Topic;
            public DateTimeOffset? StartUtc;
            public int DurationMinutes;
            public string JoinUrl;
            public string Agenda;
            // From GET /users/me/upcoming_meetings: a meeting the user is invited to, taken at the time Zoom gives.
            public bool Invited;
        }

        internal sealed class ZoomOccurrence
        {
            public DateTimeOffset? StartUtc;
            public int DurationMinutes;
            public bool Deleted;
        }

        /// <summary>
        /// Add one list page's meetings to <paramref name="into"/>. False for an answer that is not a JSON object with
        /// a "meetings" ARRAY: a 200 of {} is not "no meetings", and taking it for an empty calendar would let the
        /// fired-id prune drop every mark the slot holds. An explicit [] is a healthy empty page. An entry that cannot
        /// be read (not an object, no numeric id, a scheduled meeting with no readable time) is counted in
        /// <paramref name="unreadable"/>, which the caller turns into a warning for the same reason.
        /// </summary>
        internal static bool ParseList(string body, List<ZoomMeeting> into, out string nextPageToken, out int unreadable)
        {
            nextPageToken = null;
            unreadable = 0;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body ?? ""); }
            catch (JsonException) { return false; }
            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;
                JsonElement list;
                if (!root.TryGetProperty("meetings", out list) || list.ValueKind != JsonValueKind.Array) return false;
                nextPageToken = Str(root, "next_page_token");
                foreach (JsonElement item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) { unreadable++; continue; }
                    string id = MeetingId(item);
                    if (id == null) { unreadable++; continue; }
                    var meeting = new ZoomMeeting
                    {
                        Id = id,
                        Type = Int(item, "type"),
                        Topic = Str(item, "topic"),
                        StartUtc = Time(item, "start_time"),
                        DurationMinutes = Int(item, "duration"),
                        JoinUrl = Str(item, "join_url"),
                        Agenda = Str(item, "agenda"),
                    };
                    if (meeting.Type == Scheduled && meeting.StartUtc == null) { unreadable++; continue; }
                    into.Add(meeting);
                }
                return true;
            }
        }

        /// <summary>A series' occurrences from GET /meetings/{id}. False for an answer that is not a JSON object with an
        /// "occurrences" array, or one with an occurrence that is neither deleted nor readable: the series is then
        /// "could not be expanded", a warning, rather than a series with no times. An explicit [] is a series with
        /// none left.</summary>
        internal static bool ParseOccurrences(string body, out List<ZoomOccurrence> occurrences)
        {
            occurrences = new List<ZoomOccurrence>();
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body ?? ""); }
            catch (JsonException) { return false; }
            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;
                JsonElement list;
                if (!root.TryGetProperty("occurrences", out list) || list.ValueKind != JsonValueKind.Array) return false;
                foreach (JsonElement item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) return false;
                    var occurrence = new ZoomOccurrence
                    {
                        StartUtc = Time(item, "start_time"),
                        DurationMinutes = Int(item, "duration"),
                        Deleted = string.Equals(Str(item, "status"), "deleted", StringComparison.OrdinalIgnoreCase),
                    };
                    if (!occurrence.Deleted && occurrence.StartUtc == null) return false;
                    occurrences.Add(occurrence);
                }
                return true;
            }
        }

        /// <summary>The meetings as calendar events inside the window. A series whose occurrences were read uses them
        /// (each with its own duration, a deleted one left out); one that could not be read falls back to the time the
        /// list gave it, which is the warning's "may be missing". Id is meetingId@startUtc, so a series never collapses
        /// to one fired id, exactly as the .ics source does with uid@startUtc.</summary>
        internal static CalendarSnapshot Expand(List<ZoomMeeting> meetings, Dictionary<string, List<ZoomOccurrence>> occurrences, DateTimeOffset now)
        {
            var events = new List<CalendarEvent>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            int overflow = 0;
            DateTimeOffset from = now - WindowBack, to = now + WindowForward;
            Action<ZoomMeeting, DateTimeOffset?, int> add = delegate(ZoomMeeting m, DateTimeOffset? start, int minutes)
            {
                if (start == null) return;
                DateTimeOffset s = start.Value.ToUniversalTime();
                if (s < from || s >= to) return;
                string id = EventId(m.Id, s);
                if (!ids.Add(id)) return;
                if (events.Count >= MaximumOccurrences) { overflow++; return; }
                events.Add(new CalendarEvent
                {
                    Id = id,
                    Title = m.Topic,
                    Start = s,
                    End = minutes > 0 ? s.AddMinutes(minutes) : (DateTimeOffset?)null,
                    // Spoken in the bubble ("(Zoom)"), so a word, not the link. The link goes in the description,
                    // which MeetingLinkDetector reads for Join and the join question.
                    Location = "Zoom",
                    Description = DescriptionOf(m),
                    AllDay = false,
                    // The hosted list is the user's own meetings. An invited one's answer is not in what Zoom sends, so it
                    // is left unknown: "skip declined" then never skips it on a guess, and a calendar copy of the same
                    // meeting, which does hold the answer, decides for it (AggregateCalendarSource.LeaveOutTwins).
                    ResponseStatus = m.Invited ? "" : "accepted",
                });
            };
            foreach (ZoomMeeting m in meetings ?? new List<ZoomMeeting>())
            {
                if (m == null) continue;
                if (m.Invited)
                {
                    // The upcoming-meetings read gives concrete times in its 24-hour window, and the series detail call is
                    // the host's to make, so an invited meeting is taken as given, whatever its type.
                    add(m, m.StartUtc, m.DurationMinutes);
                }
                else if (m.Type == Scheduled)
                {
                    add(m, m.StartUtc, m.DurationMinutes);
                }
                else if (m.Type == RecurringFixedTime)
                {
                    List<ZoomOccurrence> occ;
                    if (occurrences != null && occurrences.TryGetValue(m.Id, out occ))
                    {
                        foreach (ZoomOccurrence o in occ)
                            if (o != null && !o.Deleted) add(m, o.StartUtc, o.DurationMinutes > 0 ? o.DurationMinutes : m.DurationMinutes);
                    }
                    else
                    {
                        add(m, m.StartUtc, m.DurationMinutes);
                    }
                }
            }
            events.Sort((a, b) => a.Start.CompareTo(b.Start));
            // Past the cap is a truncation like the page and series caps: said, so the slot is unvouched (F204).
            string warning = overflow > 0
                ? "more than " + MaximumOccurrences.ToString(CultureInfo.InvariantCulture) + " Zoom meeting times fall in the next 48 hours and "
                  + overflow.ToString(CultureInfo.InvariantCulture) + " were left out"
                : null;
            return new CalendarSnapshot { Events = events, Updated = now, Error = warning };
        }

        /// <summary>The id a Zoom meeting time carries: meetingId@start, the start in UTC to the minute. One function
        /// for this source and for the alias a calendar invite of the same meeting maps to
        /// (ReminderModule.ZoomAliasPairs), so the two can never be spelled differently. To the minute because that is
        /// what a calendar and Zoom agree on; Zoom's own times carry no seconds.</summary>
        internal static string EventId(string meetingId, DateTimeOffset start)
        {
            DateTime utc = start.UtcDateTime;
            var minute = new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
            return meetingId + "@" + minute.ToString("o", CultureInfo.InvariantCulture);
        }

        private static string DescriptionOf(ZoomMeeting m)
        {
            string join = !string.IsNullOrEmpty(m.JoinUrl) && m.JoinUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? m.JoinUrl : null;
            string agenda = string.IsNullOrWhiteSpace(m.Agenda) ? null : m.Agenda.Trim();
            string text = join == null ? agenda : (agenda == null ? join : join + "\n\n" + agenda);
            if (text != null && text.Length > MaximumDescriptionCharacters) text = text.Substring(0, MaximumDescriptionCharacters);
            return text;
        }

        /// <summary>A meeting id as digits, from a JSON number or string; null for anything else. It goes into a URL
        /// path (GET /meetings/{id}), so nothing but digits is accepted.</summary>
        internal static string MeetingId(JsonElement item)
        {
            JsonElement id;
            if (!item.TryGetProperty("id", out id)) return null;
            string text = id.ValueKind == JsonValueKind.Number ? id.GetRawText()
                : (id.ValueKind == JsonValueKind.String ? id.GetString() : null);
            if (string.IsNullOrEmpty(text) || text.Length > 20) return null;
            foreach (char c in text)
                if (c < '0' || c > '9') return null;
            return text;
        }

        private static string Str(JsonElement obj, string name)
        {
            JsonElement value;
            return obj.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        private static int Int(JsonElement obj, string name)
        {
            JsonElement value;
            int n;
            return obj.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out n) ? n : 0;
        }

        private static DateTimeOffset? Time(JsonElement obj, string name)
        {
            string s = Str(obj, name);
            DateTimeOffset t;
            if (string.IsNullOrEmpty(s)) return null;
            return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t)
                ? t : (DateTimeOffset?)null;
        }
    }
}
