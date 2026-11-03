using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Modules;
using DesktopAICompanion.ModuleKit;
using DesktopAICompanion.ModuleKit.Testing;

namespace DesktopAICompanion.ReminderModule
{
    // The Zoom slot's checks (1.3.0), run from ReminderModule.SelfTest. None of them reaches zoom.us or api.zoom.us: the
    // sign-in's browser is a TcpClient on the loopback listener, the code exchange and the renewal are fakes, and the
    // meetings calls answer from fixtures. Each line starts "zoom", so a failure names the area.
    public sealed partial class ReminderModule
    {
        internal static void CheckZoom(Action<string, bool> check)
        {
            CheckZoomSignInParsing(check);
            CheckZoomLoopback(check);
            CheckZoomTokenStore(check);
            CheckZoomMeetings(check);
            CheckZoomTwins(check);
            CheckZoomSlotModule(check);
            try { CheckZoomJoinQuestion(check); }
            finally { ReminderClock = () => DateTimeOffset.Now; }
        }

        /// <summary>The join question follows the leads' rule: the same Zoom invite on two calendars, the Zoom card on and
        /// not hosting it, asks Join once at the start and marks both copies; with no Zoom slot each copy asks, as before
        /// 1.3.0. Through CheckDue on the questions double, with no companion out (the card needs none).</summary>
        private static void CheckZoomJoinQuestion(Action<string, bool> check)
        {
            var t0 = new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
            var clock = new TestClock { Now = t0.AddSeconds(10) };
            ReminderClock = () => clock.Now;
            const string link = "https://acme.zoom.us/j/91234567890?pwd=x";
            Func<bool, ICalendarSource> twoCopies = delegate(bool zoomSlot)
            {
                var slots = new List<AggregateCalendarSource.Slot>
                {
                    new AggregateCalendarSource.Slot { Id = "cal1", Label = "Work", Source = new AggregateCalendarSource.StubSource(new[] { new CalendarEvent { Id = "j1w", Title = "Sync", Start = t0, End = t0.AddMinutes(30), Description = link } }, null) },
                    new AggregateCalendarSource.Slot { Id = "cal2", Label = "Gmail", Source = new AggregateCalendarSource.StubSource(new[] { new CalendarEvent { Id = "j1g", Title = "Sync", Start = t0, End = t0.AddMinutes(30), Description = link } }, null) },
                };
                if (zoomSlot)
                    slots.Add(new AggregateCalendarSource.Slot { Id = "cal5", Label = "", YieldsToOthers = true, Source = new AggregateCalendarSource.StubSource(new CalendarEvent[0], null) });
                return new AggregateCalendarSource(slots);
            };
            Action<FakeModuleSettings> gmail = delegate(FakeModuleSettings s)
            {
                s.Set(SlotKey(2, "type"), SourceLocalFile);
                s.Set(SlotKey(2, "label"), "Gmail");
            };
            using (var rig = new QuestionRig(gmail))
            {
                rig.Module._source = twoCopies(true);
                rig.Tick();
                string saved = rig.Settings.Get("fired", "");
                check("zoom join: the same Zoom invite on two calendars asks Join once at the start (" + rig.Host.Of("join").Count + ")",
                    rig.Host.Of("join").Count == 1 && rig.Host.Refusals.Count == 0);
                check("zoom join: ...and both copies carry the asked mark, saved",
                    saved.Contains("cal1|j1w@join") && saved.Contains("cal2|j1g@join"));
                rig.Tick();
                check("zoom join: ...and the next tick asks nothing more", rig.Host.Of("join").Count == 1);
            }
            using (var rig = new QuestionRig(gmail))
            {
                rig.Module._source = twoCopies(false);
                rig.Tick();
                check("zoom join: WITNESS with no Zoom slot each calendar's copy asks, as before 1.3.0",
                    rig.Host.Of("join").Count == 2);
            }

            // An invite on the Outlook slot that the invited read also brings in, the Zoom copy knowing no answer: the
            // join question follows the calendar's answer, through the module's own filter ("skip declined" is on by
            // default). Accepted in Outlook asks once, from Outlook (the WITNESS); declined asks nothing from either copy.
            Func<ReminderModule, string, ICalendarSource> answeredCopies = delegate(ReminderModule m, string answer)
            {
                const string clientLink = "https://client.zoom.us/j/44345678901";
                return new AggregateCalendarSource(new[]
                {
                    new AggregateCalendarSource.Slot { Id = "cal1", Label = "Work", Source = new AggregateCalendarSource.StubSource(new[] { new CalendarEvent { Id = "j2w", Title = "Client sync", Start = t0, End = t0.AddMinutes(30), Description = clientLink, ResponseStatus = answer } }, null) },
                    new AggregateCalendarSource.Slot
                    {
                        Id = "cal5", Label = "", YieldsToOthers = true,
                        Source = new AggregateCalendarSource.StubSource(new[] { new CalendarEvent { Id = ZoomMeetingsSource.EventId("44345678901", t0), Title = "Client sync", Start = t0, End = t0.AddMinutes(30), Description = clientLink, Location = "Zoom", ResponseStatus = "" } }, null),
                    },
                }) { TwinOwnerFilter = m.PassesFilter };
            };
            using (var rig = new QuestionRig(null))
            {
                rig.Module._source = answeredCopies(rig.Module, "accepted");
                rig.Tick();
                check("zoom join: WITNESS an invite accepted in Outlook that the invited read also has asks Join once, from Outlook ("
                    + rig.Host.Of("join").Count + ")",
                    rig.Host.Of("join").Count == 1 && rig.Settings.Get("fired", "").Contains("cal1|j2w@join"));
            }
            using (var rig = new QuestionRig(null))
            {
                rig.Module._source = answeredCopies(rig.Module, "declined");
                rig.Tick();
                check("zoom join: an invite declined in Outlook asks no Join from the Zoom card's invited copy ("
                    + rig.Host.Of("join").Count + ")",
                    rig.Host.Of("join").Count == 0);
            }
        }

        // ---- the pure halves of the sign-in -------------------------------------------------------------

        private static void CheckZoomSignInParsing(Action<string, bool> check)
        {
            // RFC 7636, appendix B: the worked S256 example.
            check("zoom sign-in: the S256 challenge matches RFC 7636's worked example",
                ZoomSignIn.ChallengeOf("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
            string v1 = ZoomSignIn.NewVerifier(), v2 = ZoomSignIn.NewVerifier();
            check("zoom sign-in: a verifier is 43 unreserved characters and fresh each time",
                v1.Length == 43 && v1 != v2 && v1.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_'));
            string url = ZoomSignIn.BuildAuthorizeUrl("Ab_c-1.2", "http://127.0.0.1:5000/zoom/callback", "CH", "ST");
            check("zoom sign-in: the authorize URL asks for a code with S256 and carries the redirect escaped",
                url.StartsWith(ZoomSignIn.AuthorizeEndpoint + "?", StringComparison.Ordinal) && url.Contains("response_type=code")
                && url.Contains("code_challenge_method=S256") && url.Contains("code_challenge=CH") && url.Contains("state=ST")
                && url.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A5000%2Fzoom%2Fcallback"));
            string id;
            check("zoom sign-in: a client ID is trimmed, and one with a space or a URL character in it is refused",
                ZoomSignIn.CheckClientId("  Ab_c-1.2 ", out id) == null && id == "Ab_c-1.2"
                && ZoomSignIn.CheckClientId("", out id) != null && ZoomSignIn.CheckClientId("abc def", out id) != null
                && ZoomSignIn.CheckClientId("abc&x=1", out id) != null);

            const string host = "127.0.0.1:5000";
            Func<string, string, ZoomSignIn.Callback> parse = delegate(string target, string extraHeaders)
            {
                return ZoomSignIn.ParseCallback("GET " + target + " HTTP/1.1\r\nHost: " + host + (extraHeaders ?? ""), "ST", host);
            };
            ZoomSignIn.Callback answer = parse("/zoom/callback?code=C0de&state=ST", null);
            check("zoom sign-in: the callback under this attempt's state and host carries the code",
                answer.Kind == ZoomSignIn.CallbackKind.Code && answer.Code == "C0de");
            check("zoom sign-in: a wrong state is ignored, neither taken nor fatal",
                parse("/zoom/callback?code=C0de&state=XX", null).Kind == ZoomSignIn.CallbackKind.Ignored);
            check("zoom sign-in: another path (a favicon) is ignored",
                parse("/favicon.ico", null).Kind == ZoomSignIn.CallbackKind.Ignored);
            ZoomSignIn.Callback denied = parse("/zoom/callback?error=access_denied&error_description=No%20https%3A%2F%2Fevil.example&state=ST", null);
            check("zoom sign-in: Zoom's error under this attempt's state ends it, with only the error code kept",
                denied.Kind == ZoomSignIn.CallbackKind.Denied && denied.Error == "access_denied");
            check("zoom sign-in: an error under another state is ignored",
                parse("/zoom/callback?error=access_denied&state=XX", null).Kind == ZoomSignIn.CallbackKind.Ignored);
            check("zoom sign-in: a code AND an error is not an answer",
                parse("/zoom/callback?code=C0de&error=x&state=ST", null).Kind == ZoomSignIn.CallbackKind.Ignored);
            check("zoom sign-in: a repeated state or code is not an answer",
                parse("/zoom/callback?code=C0de&state=ST&state=ST", null).Kind == ZoomSignIn.CallbackKind.Ignored
                && parse("/zoom/callback?code=A&code=B&state=ST", null).Kind == ZoomSignIn.CallbackKind.Ignored);
            check("zoom sign-in: a request naming another host (a page that rebound its name to the loopback) is ignored",
                ZoomSignIn.ParseCallback("GET /zoom/callback?code=C0de&state=ST HTTP/1.1\r\nHost: evil.example:5000", "ST", host).Kind == ZoomSignIn.CallbackKind.Ignored);
            check("zoom sign-in: two Host headers, or none, is not an answer",
                parse("/zoom/callback?code=C0de&state=ST", "\r\nHost: " + host).Kind == ZoomSignIn.CallbackKind.Ignored
                && ZoomSignIn.ParseCallback("GET /zoom/callback?code=C0de&state=ST HTTP/1.1", "ST", host).Kind == ZoomSignIn.CallbackKind.Ignored);
            check("zoom sign-in: only a GET is an answer",
                ZoomSignIn.ParseCallback("POST /zoom/callback?code=C0de&state=ST HTTP/1.1\r\nHost: " + host, "ST", host).Kind == ZoomSignIn.CallbackKind.Ignored);

            var now = new DateTimeOffset(2026, 3, 10, 14, 0, 0, TimeSpan.Zero);
            ZoomSignIn.Tokens good = ZoomSignIn.ParseTokenResponse(200,
                "{\"access_token\":\"acc.1\",\"refresh_token\":\"ref.1\",\"expires_in\":3599,\"token_type\":\"bearer\"}", now);
            check("zoom sign-in: a 200 with both tokens is a sign-in, its expiry from expires_in",
                good.Error == null && good.AccessToken == "acc.1" && good.RefreshToken == "ref.1" && good.ExpiresUtc == now.AddSeconds(3599));
            ZoomSignIn.Tokens half = ZoomSignIn.ParseTokenResponse(200, "{\"access_token\":\"acc.secret\"}", now);
            check("zoom sign-in: a 200 without a refresh token is refused at the door, and its error carries no token",
                half.Error != null && half.AccessToken == null && half.Error.IndexOf("acc.secret", StringComparison.Ordinal) < 0);
            ZoomSignIn.Tokens revoked = ZoomSignIn.ParseTokenResponse(400, "{\"reason\":\"Invalid Token!\",\"error\":\"invalid_grant\"}", now);
            check("zoom sign-in: invalid_grant means this sign-in is over", revoked.Revoked && revoked.Error != null);
            ZoomSignIn.Tokens client = ZoomSignIn.ParseTokenResponse(401, "{\"error\":\"invalid_client\"}", now);
            check("zoom sign-in: another refusal is named and does not end the sign-in",
                !client.Revoked && client.Error != null && client.Error.Contains("invalid_client"));
            check("zoom sign-in: a 5xx, or a 200 that is not JSON, is an error, never a sign-in",
                ZoomSignIn.ParseTokenResponse(503, "", now).Error != null && ZoomSignIn.ParseTokenResponse(200, "<html>", now).Error != null);
            check("zoom sign-in: a token with a space or a newline in it is not a token",
                !ZoomSignIn.IsTokenShaped("a b") && !ZoomSignIn.IsTokenShaped("a\nb") && !ZoomSignIn.IsTokenShaped("")
                && ZoomSignIn.IsTokenShaped("eyJ.abc-_~"));
        }

        // ---- the loopback listener, end to end on 127.0.0.1 ---------------------------------------------

        private static void CheckZoomLoopback(Action<string, bool> check)
        {
            string opened = null;
            var replies = new List<string>();
            Task browser = null;
            Func<string, bool> open = delegate(string url)
            {
                opened = url;
                var redirect = new Uri(QueryValue(url, "redirect_uri"));
                string state = QueryValue(url, "state");
                string hostHeader = "Host: " + redirect.Host + ":" + redirect.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n";
                browser = Task.Run(delegate
                {
                    // What a real browser and a real desktop throw at the port before the answer, in one sequence.
                    replies.Add(SendRaw(redirect.Port, null));                                   // a connection that says nothing
                    replies.Add(SendRaw(redirect.Port, "GET /zoom/call"));                       // a partial request, then closed
                    replies.Add(SendRaw(redirect.Port, "GET /favicon.ico HTTP/1.1\r\n" + hostHeader + "\r\n"));
                    replies.Add(SendRaw(redirect.Port, "GET " + redirect.AbsolutePath + "?code=stale&state=other HTTP/1.1\r\n" + hostHeader + "\r\n"));
                    replies.Add(SendRaw(redirect.Port, "GET " + redirect.AbsolutePath + "?code=the-code&state=" + state + " HTTP/1.1\r\n" + hostHeader + "\r\n"));
                });
                return true;
            };
            ZoomSignIn.Authorization auth = Task.Run(() => ZoomSignIn.AuthorizeAsync("Ab_c-1.2", open, TimeSpan.FromSeconds(20), CancellationToken.None))
                .GetAwaiter().GetResult();
            bool browserDone = browser != null && browser.Wait(TimeSpan.FromSeconds(10));
            check("zoom loopback: the sign-in waits past an empty connection, a partial one, a favicon and a stale state, and takes the answer",
                auth.Error == null && auth.Code == "the-code");
            check("zoom loopback: the redirect is 127.0.0.1 on the listener's own port, path /zoom/callback",
                auth.RedirectUri != null && auth.RedirectUri.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)
                && auth.RedirectUri.EndsWith(ZoomSignIn.CallbackPath, StringComparison.Ordinal));
            check("zoom loopback: the verifier handed back is the one the challenge in the authorize URL came from",
                opened != null && auth.Verifier != null && ZoomSignIn.ChallengeOf(auth.Verifier) == QueryValue(opened, "code_challenge"));
            check("zoom loopback: the favicon and the stale state got a 404, the answer a 200 that does not echo the code",
                browserDone && replies.Count == 5 && replies[2].StartsWith("HTTP/1.1 404", StringComparison.Ordinal)
                && replies[3].StartsWith("HTTP/1.1 404", StringComparison.Ordinal)
                && replies[4].StartsWith("HTTP/1.1 200", StringComparison.Ordinal) && replies[4].IndexOf("the-code", StringComparison.Ordinal) < 0);
            int port = auth.RedirectUri == null ? 0 : new Uri(auth.RedirectUri).Port;
            check("zoom loopback: the listener is closed once the answer is in",
                port > 0 && SendRaw(port, "GET / HTTP/1.1\r\n\r\n").StartsWith("EXC", StringComparison.Ordinal));

            ZoomSignIn.Authorization timedOut = Task.Run(() => ZoomSignIn.AuthorizeAsync("Ab_c-1.2", delegate { return true; },
                TimeSpan.FromMilliseconds(300), CancellationToken.None)).GetAwaiter().GetResult();
            check("zoom loopback: a browser that never comes back ends at the timeout with a reason",
                timedOut.Code == null && timedOut.Error != null && timedOut.Error.Contains("did not answer"));
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
            {
                ZoomSignIn.Authorization cancelled = Task.Run(() => ZoomSignIn.AuthorizeAsync("Ab_c-1.2", delegate { return true; },
                    TimeSpan.FromSeconds(20), cancel.Token)).GetAwaiter().GetResult();
                check("zoom loopback: a cancelled sign-in (Sign out, or the app closing) says so",
                    cancelled.Code == null && cancelled.Error == "The sign-in was cancelled.");
            }
            ZoomSignIn.Authorization noBrowser = Task.Run(() => ZoomSignIn.AuthorizeAsync("Ab_c-1.2", delegate { return false; },
                TimeSpan.FromSeconds(20), CancellationToken.None)).GetAwaiter().GetResult();
            check("zoom loopback: a browser that could not be opened is said at once, not after three minutes",
                noBrowser.Code == null && noBrowser.Error != null && noBrowser.Error.Contains("browser"));
        }

        /// <summary>One request on a fresh connection, half-closed, and whatever came back; "EXC ..." when the
        /// connection itself failed (nothing listening).</summary>
        private static string SendRaw(int port, string request)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    client.Connect(IPAddress.Loopback, port);
                    NetworkStream stream = client.GetStream();
                    stream.ReadTimeout = 5000;
                    if (request != null)
                    {
                        byte[] bytes = Encoding.ASCII.GetBytes(request);
                        stream.Write(bytes, 0, bytes.Length);
                    }
                    client.Client.Shutdown(SocketShutdown.Send);
                    var sb = new StringBuilder();
                    byte[] buffer = new byte[4096];
                    int n;
                    while ((n = stream.Read(buffer, 0, buffer.Length)) > 0) sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
                    return sb.ToString();
                }
            }
            catch (Exception ex)
            {
                return "EXC " + ex.GetType().Name;
            }
        }

        private static string QueryValue(string url, string name)
        {
            int q = (url ?? "").IndexOf('?');
            if (q < 0) return null;
            foreach (string pair in url.Substring(q + 1).Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0 && pair.Substring(0, eq) == name) return Uri.UnescapeDataString(pair.Substring(eq + 1));
            }
            return null;
        }

        // ---- the token store ----------------------------------------------------------------------------

        private static void CheckZoomTokenStore(Action<string, bool> check)
        {
            string root = Path.Combine(Path.GetTempPath(), "dp-reminder-zoom-" + Guid.NewGuid().ToString("N"));
            try
            {
                var clock = new TestClock { Now = new DateTimeOffset(2026, 3, 10, 14, 0, 0, TimeSpan.Zero) };
                var handed = new List<string>();
                int issued = 1;
                Func<string, string, ZoomSignIn.Tokens> renew = delegate(string clientId, string refreshToken)
                {
                    lock (handed) handed.Add(refreshToken);
                    int n = Interlocked.Increment(ref issued);
                    return new ZoomSignIn.Tokens { AccessToken = "acc." + n, RefreshToken = "ref." + n, ExpiresUtc = clock.Now.AddHours(1) };
                };
                string folder = Path.Combine(root, "a");
                string file = Path.Combine(folder, ZoomTokenStore.FileName);
                var store = new ZoomTokenStore(folder, renew, () => clock.Now);
                string error;
                check("zoom store: with nothing saved there is no sign-in and no token",
                    store.SignedInClientId == null && store.GetAccessToken("app1", null, out error) == null && error.Contains("Not signed in"));

                int before = store.Generation;
                bool saved = store.Save("app1", new ZoomSignIn.Tokens { AccessToken = "acc.1", RefreshToken = "ref.1-secret", ExpiresUtc = clock.Now.AddHours(1) }, before, out error);
                check("zoom store: a sign-in is saved sealed: the file is there and does not hold the refresh token in clear",
                    saved && File.Exists(file) && File.ReadAllText(file).IndexOf("ref.1-secret", StringComparison.Ordinal) < 0 && store.Generation != before);
                check("zoom store: a second copy of the store opens the same sign-in",
                    new ZoomTokenStore(folder, renew, () => clock.Now).SignedInClientId == "app1");
                check("zoom store: a fresh access token is used as it is, with no renewal",
                    store.GetAccessToken("app1", null, out error) == "acc.1" && handed.Count == 0);

                clock.Now = clock.Now.AddMinutes(56);
                string renewed = store.GetAccessToken("app1", null, out error);
                check("zoom store: inside its last five minutes the token is renewed with the saved refresh token",
                    renewed == "acc.2" && handed.Count == 1 && handed[0] == "ref.1-secret");
                string sealedNow = Unseal(file);
                check("zoom store: the rotated refresh token is what the file holds afterwards, the spent one gone",
                    sealedNow.Contains("\"ref.2\"") && !sealedNow.Contains("ref.1-secret"));

                string first = store.GetAccessToken("app1", "acc.2", out error);
                string second = store.GetAccessToken("app1", "acc.2", out error);
                check("zoom store: a token Zoom refused is renewed once; a second attempt holding the same refusal gets the new one",
                    first == "acc.3" && second == "acc.3" && handed.Count == 2);
                check("zoom store: another client ID is refused with no renewal",
                    store.GetAccessToken("app2", null, out error) == null && error.Contains("different client ID") && handed.Count == 2);
                check("zoom store: a sign-in begun before the store changed is not kept",
                    !store.Save("app1", new ZoomSignIn.Tokens { AccessToken = "acc.x", RefreshToken = "ref.x", ExpiresUtc = clock.Now.AddHours(1) }, before, out error)
                    && Unseal(file).Contains("\"ref.3\""));

                // A Sign out while a renewal is out: the renewal's tokens must not come back.
                string raceFolder = Path.Combine(root, "race");
                var entered = new ManualResetEventSlim(false);
                var gate = new ManualResetEventSlim(false);
                var racing = new ZoomTokenStore(raceFolder, delegate(string c, string r)
                {
                    entered.Set();
                    gate.Wait(TimeSpan.FromSeconds(10));
                    return new ZoomSignIn.Tokens { AccessToken = "acc.late", RefreshToken = "ref.late", ExpiresUtc = clock.Now.AddHours(1) };
                }, () => clock.Now);
                racing.Save("app1", new ZoomSignIn.Tokens { AccessToken = "acc.old", RefreshToken = "ref.old", ExpiresUtc = clock.Now.AddMinutes(1) }, racing.Generation, out error);
                string raced = "unset";
                Task renewal = Task.Run(delegate { string e; raced = racing.GetAccessToken("app1", null, out e); });
                bool inside = entered.Wait(TimeSpan.FromSeconds(10));
                string words;
                bool signedOut = racing.SignOut(out words);
                gate.Set();
                renewal.Wait(TimeSpan.FromSeconds(10));
                check("zoom store: a renewal that lands after a sign-out is dropped, and nothing is written back",
                    inside && signedOut && raced == null && racing.SignedInClientId == null
                    && !File.Exists(Path.Combine(raceFolder, ZoomTokenStore.FileName)));

                // A renewal whose save fails keeps the session working, says so, and saves on a later call.
                string flakyFolder = Path.Combine(root, "flaky");
                var flaky = new ZoomTokenStore(flakyFolder, renew, () => clock.Now);
                flaky.Save("app1", new ZoomSignIn.Tokens { AccessToken = "acc.f", RefreshToken = "ref.f", ExpiresUtc = clock.Now.AddMinutes(1) }, flaky.Generation, out error);
                Directory.Delete(flakyFolder, true);
                File.WriteAllText(flakyFolder, "a file where the folder was");
                string kept = flaky.GetAccessToken("app1", null, out error);
                check("zoom store: a renewal that could not be saved still answers its token and reports the failed save",
                    kept != null && kept.StartsWith("acc.", StringComparison.Ordinal) && flaky.LastWriteFailed);
                File.Delete(flakyFolder);
                flaky.GetAccessToken("app1", null, out error);
                check("zoom store: ...and the next call saves it once the folder can be written",
                    !flaky.LastWriteFailed && File.Exists(Path.Combine(flakyFolder, ZoomTokenStore.FileName)));

                // invalid_grant: the sign-in is over, and kept no longer.
                string revokedFolder = Path.Combine(root, "revoked");
                var revoking = new ZoomTokenStore(revokedFolder, delegate(string c, string r)
                {
                    return new ZoomSignIn.Tokens { Revoked = true, Error = "Zoom no longer accepts this sign-in. Press Sign in to Zoom again." };
                }, () => clock.Now);
                revoking.Save("app1", new ZoomSignIn.Tokens { AccessToken = "acc.r", RefreshToken = "ref.r", ExpiresUtc = clock.Now.AddMinutes(1) }, revoking.Generation, out error);
                int beforeRevoke = revoking.Generation;
                string none = revoking.GetAccessToken("app1", null, out error);
                check("zoom store: a refresh token Zoom no longer accepts ends the sign-in and deletes it",
                    none == null && error.Contains("no longer accepts") && revoking.SignedInClientId == null
                    && revoking.Generation != beforeRevoke && !File.Exists(Path.Combine(revokedFolder, ZoomTokenStore.FileName)));

                string damagedFolder = Path.Combine(root, "damaged");
                Directory.CreateDirectory(damagedFolder);
                File.WriteAllText(Path.Combine(damagedFolder, ZoomTokenStore.FileName), "not sealed");
                var damaged = new ZoomTokenStore(damagedFolder, renew, () => clock.Now);
                check("zoom store: a damaged file is reported as unreadable, not as signed out and not as a token",
                    damaged.Unreadable && damaged.SignedInClientId == null && damaged.GetAccessToken("app1", null, out error) == null
                    && error.Contains("could not be opened"));

                bool out1 = store.SignOut(out words);
                check("zoom store: Sign out deletes the saved sign-in and forgets the tokens",
                    out1 && words.StartsWith("✓", StringComparison.Ordinal) && !File.Exists(file) && store.SignedInClientId == null
                    && store.GetAccessToken("app1", null, out error) == null);
                string neverFolder = Path.Combine(root, "never-made");
                string neverWords;
                check("zoom store: signing out where nothing was ever saved (no folder at all) is not a failure",
                    new ZoomTokenStore(neverFolder, renew, () => clock.Now).SignOut(out neverWords) && !Directory.Exists(neverFolder));
                check("zoom store: a store with no folder refuses to save, in words",
                    !new ZoomTokenStore(null, renew, () => clock.Now).Save("app1", new ZoomSignIn.Tokens { AccessToken = "a", RefreshToken = "r" }, 0, out error)
                    && error.Contains("no data folder"));
            }
            finally
            {
                try { if (File.Exists(Path.Combine(root, "flaky"))) File.Delete(Path.Combine(root, "flaky")); } catch { }
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static string Unseal(string file)
        {
            try
            {
                byte[] clear = ProtectedData.Unprotect(Convert.FromBase64String(File.ReadAllText(file).Trim()), ZoomTokenStore.Entropy,
                    DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(clear);
            }
            catch { return ""; }
        }

        // ---- the meetings source, against fixtures ----------------------------------------------------------

        private sealed class FakeZoomTokens : IZoomAccessToken
        {
            internal string Current = "acc.1";
            internal string Refusal;
            internal readonly List<string> Rejected = new List<string>();

            public string GetAccessToken(string clientId, string rejected, out string error)
            {
                if (Refusal != null) { error = Refusal; return null; }
                if (rejected != null && rejected == Current)
                {
                    Rejected.Add(rejected);
                    Current = "acc." + (Rejected.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                error = null;
                return Current;
            }
        }

        private sealed class FakeZoomApi
        {
            internal readonly List<string> Urls = new List<string>();
            internal readonly List<string> Bearers = new List<string>();
            internal readonly List<TimeSpan> Deadlines = new List<TimeSpan>();
            internal Func<string, string, ZoomHttp.Response> Answer;

            internal ZoomHttp.Response Get(string url, string bearer, TimeSpan deadline)
            {
                Urls.Add(url);
                Bearers.Add(bearer);
                Deadlines.Add(deadline);
                return Answer(url, bearer);
            }
        }

        private static ZoomHttp.Response Ok(string body) { return new ZoomHttp.Response { Status = 200, Body = body }; }

        private const string ZoomPage1 = @"{""page_size"":300,""next_page_token"":""p2"",""meetings"":[
{""id"":91234567890,""type"":2,""topic"":""Design Review"",""start_time"":""2026-03-10T15:00:00Z"",""duration"":30,""join_url"":""https://acme.zoom.us/j/91234567890?pwd=x"",""agenda"":""Agenda text""},
{""id"":81111111111,""type"":8,""topic"":""Weekly"",""start_time"":""2026-03-24T16:00:00Z"",""duration"":60,""join_url"":""https://acme.zoom.us/j/81111111111""},
{""id"":81111111111,""type"":8,""topic"":""Weekly"",""start_time"":""2026-03-31T16:00:00Z"",""duration"":60,""join_url"":""https://acme.zoom.us/j/81111111111""},
{""id"":70000000000,""type"":3,""topic"":""No fixed time""},
{""id"":60000000000,""type"":1,""topic"":""Instant"",""start_time"":""2026-03-10T14:00:00Z""},
{""id"":50000000000,""type"":2,""topic"":""Far"",""start_time"":""2026-03-20T10:00:00Z"",""duration"":30}]}";

        private const string ZoomPage2 = @"{""next_page_token"":"""",""meetings"":[
{""id"":""40000000000"",""type"":2,""topic"":""Early"",""start_time"":""2026-03-11T08:00:00Z"",""duration"":15,""join_url"":""https://zoom.us/j/40000000000""}]}";

        private const string ZoomSeries = @"{""id"":81111111111,""type"":8,""occurrences"":[
{""occurrence_id"":""1"",""start_time"":""2026-03-10T16:00:00Z"",""duration"":45,""status"":""available""},
{""occurrence_id"":""2"",""start_time"":""2026-03-11T16:00:00Z"",""duration"":60,""status"":""deleted""},
{""occurrence_id"":""3"",""start_time"":""2026-03-17T16:00:00Z"",""duration"":60,""status"":""available""}]}";

        private static ZoomHttp.Response ZoomFixtures(string url, string bearer)
        {
            if (url.Contains("/users/me/meetings?") && url.Contains("next_page_token=p2")) return Ok(ZoomPage2);
            if (url.Contains("/users/me/meetings?")) return Ok(ZoomPage1);
            if (url.EndsWith("/meetings/81111111111", StringComparison.Ordinal)) return Ok(ZoomSeries);
            return new ZoomHttp.Response { Status = 404, Body = "{\"code\":3001}" };
        }

        private static void CheckZoomMeetings(Action<string, bool> check)
        {
            var now = new DateTimeOffset(2026, 3, 10, 14, 0, 0, TimeSpan.Zero);
            int generation = 7;
            Func<FakeZoomApi, FakeZoomTokens, string, Func<string>, ZoomMeetingsSource> sourceOf = delegate(FakeZoomApi api, FakeZoomTokens tokens, string clientId, Func<string> warning)
            {
                return new ZoomMeetingsSource(delegate { return clientId; }, delegate { return false; }, delegate { return generation; }, tokens, warning, api.Get);
            };

            var api1 = new FakeZoomApi { Answer = ZoomFixtures };
            CalendarSnapshot snap = sourceOf(api1, new FakeZoomTokens(), "app1", null).FetchForSelfTest(now);
            List<CalendarEvent> events = snap.Events == null ? new List<CalendarEvent>() : snap.Events.ToList();
            check("zoom meetings: two pages read, the window kept, instant / no-fixed-time / far-off left out (" + events.Count + ")",
                snap.Error == null && events.Count == 3 && events.Select(e => e.Title).SequenceEqual(new[] { "Design Review", "Weekly", "Early" }));
            CalendarEvent review = events.FirstOrDefault(e => e.Title == "Design Review");
            CalendarEvent weekly = events.FirstOrDefault(e => e.Title == "Weekly");
            string joinUrl, joinKind;
            check("zoom meetings: an event is meetingId@startUtc, ends after its duration, says (Zoom), is the host's own, and carries its join link",
                review != null && review.Id == "91234567890@2026-03-10T15:00:00.0000000Z" && review.End == review.Start.AddMinutes(30)
                && review.Location == "Zoom" && review.ResponseStatus == "accepted"
                && MeetingLinkDetector.TryFind(review, out joinUrl, out joinKind) && joinKind == "Zoom"
                && joinUrl == "https://acme.zoom.us/j/91234567890?pwd=x" && review.Description.Contains("Agenda text"));
            check("zoom meetings: a series is expanded from its occurrences, with each one's own duration and the deleted one left out",
                weekly != null && weekly.Start == new DateTimeOffset(2026, 3, 10, 16, 0, 0, TimeSpan.Zero) && weekly.End == weekly.Start.AddMinutes(45)
                && !events.Any(e => e.Id.StartsWith("81111111111@2026-03-11", StringComparison.Ordinal)));
            check("zoom meetings: the series listed twice is read once, and nothing else is asked for (" + api1.Urls.Count + " calls)",
                api1.Urls.Count == 3 && api1.Urls.Count(u => u.EndsWith("/meetings/81111111111", StringComparison.Ordinal)) == 1
                && api1.Urls[1].Contains("next_page_token=p2") && api1.Urls.All(u => u.StartsWith(ZoomMeetingsSource.ApiBase + "/", StringComparison.Ordinal)));
            check("zoom meetings: every request carries the token and a deadline of at most 30 seconds",
                api1.Bearers.All(b => b == "acc.1") && api1.Deadlines.All(d => d > TimeSpan.Zero && d <= TimeSpan.FromSeconds(30)));

            var tokens401 = new FakeZoomTokens();
            var api401 = new FakeZoomApi
            {
                Answer = delegate(string url, string bearer)
                {
                    return bearer == "acc.1" ? new ZoomHttp.Response { Status = 401, Body = "{\"code\":124}" } : ZoomFixtures(url, bearer);
                },
            };
            CalendarSnapshot renewed = sourceOf(api401, tokens401, "app1", null).FetchForSelfTest(now);
            check("zoom meetings: a 401 renews once, naming the token Zoom refused, and the read goes on",
                renewed.Error == null && renewed.Events.Count == 3 && tokens401.Rejected.Count == 1 && tokens401.Rejected[0] == "acc.1");

            var apiNone = new FakeZoomApi { Answer = ZoomFixtures };
            CalendarSnapshot notSignedIn = sourceOf(apiNone, new FakeZoomTokens { Refusal = "Not signed in to Zoom. Press Sign in to Zoom on the Zoom card." }, "app1", null).FetchForSelfTest(now);
            check("zoom meetings: not signed in is the slot's error, and nothing is asked of Zoom",
                notSignedIn.Error != null && notSignedIn.Error.StartsWith("Not signed in", StringComparison.Ordinal) && apiNone.Urls.Count == 0);
            CalendarSnapshot blank = sourceOf(apiNone, new FakeZoomTokens(), "", null).FetchForSelfTest(now);
            check("zoom meetings: no client ID is the slot's error, pointing at ZOOM.md, with no request",
                blank.Error != null && blank.Error.Contains("ZOOM.md") && apiNone.Urls.Count == 0);

            var apiPages = new FakeZoomApi { Answer = delegate { return Ok("{\"next_page_token\":\"more\",\"meetings\":[]}"); } };
            CalendarSnapshot pages = sourceOf(apiPages, new FakeZoomTokens(), "app1", null).FetchForSelfTest(now);
            check("zoom meetings: the page cap reached with more to come is a warning (an unvouched slot), not a quiet truncation",
                apiPages.Urls.Count == ZoomMeetingsSource.MaximumPages && pages.Error != null && pages.Error.Contains("only the first ones"));

            var many = new StringBuilder("{\"meetings\":[");
            for (int i = 0; i < ZoomMeetingsSource.MaximumSeriesLookups + 1; i++)
                many.Append(i == 0 ? "" : ",").Append("{\"id\":" + (82000000000L + i) + ",\"type\":8,\"topic\":\"S" + i + "\",\"start_time\":\"2026-03-10T18:00:00Z\",\"duration\":30}");
            many.Append("]}");
            var apiMany = new FakeZoomApi
            {
                Answer = delegate(string url, string bearer) { return url.Contains("/users/me/meetings?") ? Ok(many.ToString()) : Ok("{\"occurrences\":[]}"); },
            };
            CalendarSnapshot capped = sourceOf(apiMany, new FakeZoomTokens(), "app1", null).FetchForSelfTest(now);
            check("zoom meetings: past the series cap the rest are not read, and the slot says how many were not expanded",
                apiMany.Urls.Count == 1 + ZoomMeetingsSource.MaximumSeriesLookups && capped.Error != null
                && capped.Error.Contains("1 recurring Zoom meeting(s) could not be expanded") && capped.Events.Count == 1);

            var apiDetailFails = new FakeZoomApi
            {
                Answer = delegate(string url, string bearer)
                {
                    return url.EndsWith("/meetings/81111111111", StringComparison.Ordinal) ? new ZoomHttp.Response { Status = 404, Body = "{\"code\":3001}" } : ZoomFixtures(url, bearer);
                },
            };
            CalendarSnapshot detailFailed = sourceOf(apiDetailFails, new FakeZoomTokens(), "app1", null).FetchForSelfTest(now);
            check("zoom meetings: a series that could not be read is a warning, and the rest of the read is kept",
                detailFailed.Error != null && detailFailed.Error.Contains("could not be expanded") && detailFailed.Events.Count == 2);

            var apiScope = new FakeZoomApi
            {
                Answer = delegate { return new ZoomHttp.Response { Status = 400, Body = "{\"code\":4711,\"message\":\"Invalid access token, does not contain scopes for jdoe@example.invalid\"}" }; },
            };
            CalendarSnapshot scope = sourceOf(apiScope, new FakeZoomTokens(), "app1", null).FetchForSelfTest(now);
            check("zoom meetings: a missing scope is named, and Zoom's own message (which can name the account) is not echoed",
                scope.Error != null && scope.Error.Contains("missing a scope") && scope.Error.IndexOf("example.invalid", StringComparison.Ordinal) < 0);

            var apiSwitch = new FakeZoomApi
            {
                Answer = delegate(string url, string bearer) { generation++; return ZoomFixtures(url, bearer); },
            };
            CalendarSnapshot switched = sourceOf(apiSwitch, new FakeZoomTokens(), "app1", null).FetchForSelfTest(now);
            check("zoom meetings: a sign-in change during the read drops what was read",
                switched.Error != null && switched.Error.Contains("changed while") && switched.Events.Count == 0);

            var apiWarn = new FakeZoomApi { Answer = ZoomFixtures };
            CalendarSnapshot warned = sourceOf(apiWarn, new FakeZoomTokens(), "app1", delegate { return ZoomMeetingsSource.RenewalNotSavedWarning; }).FetchForSelfTest(now);
            check("zoom meetings: a renewal that could not be saved is a warning beside the meetings, not instead of them",
                warned.Events.Count == 3 && warned.Error != null && warned.Error.Contains("could not be saved"));

            var apiGarbage = new FakeZoomApi { Answer = delegate { return Ok("<html>sign in</html>"); } };
            CalendarSnapshot garbage = sourceOf(apiGarbage, new FakeZoomTokens(), "app1", null).FetchForSelfTest(now);
            check("zoom meetings: an answer that is not JSON is an error snapshot, never an empty healthy calendar",
                garbage.Error != null && garbage.Events.Count == 0);
            var apiEmptyObject = new FakeZoomApi { Answer = delegate { return Ok("{}"); } };
            CalendarSnapshot emptyObject = sourceOf(apiEmptyObject, new FakeZoomTokens(), "app1", null).FetchForSelfTest(now);
            check("zoom meetings: a 200 of {} (no meetings array) is not an empty calendar",
                emptyObject.Error != null && emptyObject.Error.Contains("could not be read") && emptyObject.Events.Count == 0);
            var apiEmptyList = new FakeZoomApi { Answer = delegate { return Ok("{\"meetings\":[]}"); } };
            CalendarSnapshot emptyList = sourceOf(apiEmptyList, new FakeZoomTokens(), "app1", null).FetchForSelfTest(now);
            check("zoom meetings: WITNESS an explicit empty list is a healthy empty calendar",
                emptyList.Error == null && emptyList.Events.Count == 0);

            var apiUnreadable = new FakeZoomApi
            {
                Answer = delegate
                {
                    return Ok(@"{""meetings"":[
{""id"":""12/../x"",""type"":2,""topic"":""Bad id"",""start_time"":""2026-03-10T15:00:00Z""},
{""id"":30000000000,""type"":2,""topic"":""No time""},
""not an object"",
{""id"":20000000000,""type"":2,""topic"":""Fine"",""start_time"":""2026-03-10T15:30:00Z"",""duration"":30}]}");
                },
            };
            CalendarSnapshot unreadable = sourceOf(apiUnreadable, new FakeZoomTokens(), "app1", null).FetchForSelfTest(now);
            check("zoom meetings: entries that cannot be read (a non-numeric id, a scheduled meeting with no time, a non-object) are a warning, the rest kept",
                unreadable.Error != null && unreadable.Error.Contains("3 meeting(s) in Zoom's list could not be read")
                && unreadable.Events.Count == 1 && unreadable.Events[0].Title == "Fine" && apiUnreadable.Urls.Count == 1);

            var apiNoOccurrences = new FakeZoomApi
            {
                Answer = delegate(string url, string bearer)
                {
                    return url.EndsWith("/meetings/81111111111", StringComparison.Ordinal) ? Ok("{\"id\":81111111111,\"type\":8}") : ZoomFixtures(url, bearer);
                },
            };
            CalendarSnapshot noOccurrences = sourceOf(apiNoOccurrences, new FakeZoomTokens(), "app1", null).FetchForSelfTest(now);
            check("zoom meetings: a series answer with no occurrences array is 'could not be expanded', not a series with no times",
                noOccurrences.Error != null && noOccurrences.Error.Contains("could not be expanded"));

            // The occurrence cap, at the boundary and one past it.
            Func<int, CalendarSnapshot> expandN = delegate(int n)
            {
                var list = new List<ZoomMeetingsSource.ZoomMeeting>();
                for (int i = 0; i < n; i++)
                    list.Add(new ZoomMeetingsSource.ZoomMeeting
                    {
                        Id = (10000000000L + i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Type = ZoomMeetingsSource.Scheduled, Topic = "M", StartUtc = now.AddMinutes(10 + i % 600), DurationMinutes = 15,
                    });
                return ZoomMeetingsSource.Expand(list, null, now);
            };
            CalendarSnapshot atCap = expandN(2000), pastCap = expandN(2001);
            check("zoom meetings: exactly at the occurrence cap is complete and healthy",
                atCap.Error == null && atCap.Events.Count == 2000);
            check("zoom meetings: one past the occurrence cap is a warning naming what was left out",
                pastCap.Events.Count == 2000 && pastCap.Error != null && pastCap.Error.Contains("1 were left out"));
            check("zoom meetings: a meeting time's id is spelled to the minute in UTC, the spelling a calendar invite's alias uses",
                ZoomMeetingsSource.EventId("91234567890", new DateTimeOffset(2026, 3, 10, 10, 0, 42, TimeSpan.FromHours(-5)))
                == "91234567890@2026-03-10T15:00:00.0000000Z");

            CheckZoomInvitedMeetings(check, now, () => generation);
        }

        // The meetings the user is invited to (GET /users/me/upcoming_meetings), read only when the card's switch is on.
        private const string ZoomInvitedPage = @"{""total_records"":3,""meetings"":[
{""id"":33000000001,""type"":2,""topic"":""Client standup"",""start_time"":""2026-03-10T16:30:00Z"",""duration"":15,""join_url"":""https://client.zoom.us/j/33000000001?pwd=c""},
{""id"":91234567890,""type"":2,""topic"":""Design Review"",""start_time"":""2026-03-10T15:00:00Z"",""duration"":30,""join_url"":""https://acme.zoom.us/j/91234567890?pwd=x""},
{""id"":34000000002,""type"":8,""topic"":""Client weekly"",""start_time"":""2026-03-11T09:00:00Z"",""duration"":30,""join_url"":""https://client.zoom.us/j/34000000002""}]}";

        private static ZoomHttp.Response ZoomFixturesWithInvited(string url, string bearer)
        {
            if (url.EndsWith("/users/me/upcoming_meetings", StringComparison.Ordinal)) return Ok(ZoomInvitedPage);
            return ZoomFixtures(url, bearer);
        }

        private static void CheckZoomInvitedMeetings(Action<string, bool> check, DateTimeOffset now, Func<int> generation)
        {
            bool switchOn = true;
            Func<FakeZoomApi, ZoomMeetingsSource> invitedSourceOf = delegate(FakeZoomApi api)
            {
                return new ZoomMeetingsSource(delegate { return "app1"; }, delegate { return switchOn; }, generation, new FakeZoomTokens(), null, api.Get);
            };

            var off = new FakeZoomApi { Answer = ZoomFixturesWithInvited };
            switchOn = false;
            CalendarSnapshot hostedOnly = invitedSourceOf(off).FetchForSelfTest(now);
            check("zoom invited: with the switch off nothing is asked of upcoming_meetings, so a sign-in without its scope is never refused",
                hostedOnly.Error == null && hostedOnly.Events.Count == 3 && !off.Urls.Any(u => u.Contains("upcoming_meetings")));

            var on = new FakeZoomApi { Answer = ZoomFixturesWithInvited };
            switchOn = true;
            CalendarSnapshot both = invitedSourceOf(on).FetchForSelfTest(now);
            List<CalendarEvent> events = both.Events == null ? new List<CalendarEvent>() : both.Events.ToList();
            CalendarEvent standup = events.FirstOrDefault(e => e.Title == "Client standup");
            CalendarEvent weekly = events.FirstOrDefault(e => e.Title == "Client weekly");
            string joinUrl, joinKind;
            check("zoom invited: with the switch on the meetings you are invited to are read beside the ones you host (" + events.Count + ")",
                both.Error == null && events.Count == 5 && standup != null && weekly != null);
            check("zoom invited: an invited meeting says (Zoom), carries its join link, and its answer is left unknown, not 'accepted'",
                standup != null && standup.Location == "Zoom" && standup.ResponseStatus == ""
                && MeetingLinkDetector.TryFind(standup, out joinUrl, out joinKind) && joinKind == "Zoom"
                && standup.End == standup.Start.AddMinutes(15));
            check("zoom invited: an invited recurring meeting is taken at the time Zoom gives, with no series call (it is the host's)",
                weekly != null && weekly.Start == new DateTimeOffset(2026, 3, 11, 9, 0, 0, TimeSpan.Zero)
                && !on.Urls.Any(u => u.EndsWith("/meetings/34000000002", StringComparison.Ordinal)));
            check("zoom invited: a meeting you host that is also listed as upcoming appears once, as yours",
                events.Count(e => e.Title == "Design Review") == 1
                && events.First(e => e.Title == "Design Review").ResponseStatus == "accepted");
            check("zoom invited: the invited read comes after the hosted read, with no query parameters on its first request",
                on.Urls.Count == 4 && on.Urls[3] == ZoomMeetingsSource.ApiBase + "/users/me/upcoming_meetings");

            var scopeMissing = new FakeZoomApi
            {
                Answer = delegate(string url, string bearer)
                {
                    return url.Contains("/upcoming_meetings")
                        ? new ZoomHttp.Response { Status = 400, Body = "{\"code\":4711,\"message\":\"Invalid access token, does not contain scopes\"}" }
                        : ZoomFixtures(url, bearer);
                },
            };
            CalendarSnapshot noScope = invitedSourceOf(scopeMissing).FetchForSelfTest(now);
            check("zoom invited: a missing invited scope is a warning naming THAT scope, and the hosted meetings are kept",
                noScope.Events.Count == 3 && noScope.Error != null && noScope.Error.Contains("invited to could not be read")
                && noScope.Error.Contains(ZoomMeetingsSource.InvitedScope) && !noScope.Error.Contains(ZoomMeetingsSource.HostedScopes));

            var invitedGarbage = new FakeZoomApi
            {
                Answer = delegate(string url, string bearer) { return url.Contains("/upcoming_meetings") ? Ok("{\"total_records\":0}") : ZoomFixtures(url, bearer); },
            };
            CalendarSnapshot garbled = invitedSourceOf(invitedGarbage).FetchForSelfTest(now);
            check("zoom invited: an invited answer with no meetings array is a warning, not an empty list, and the hosted meetings stay",
                garbled.Events.Count == 3 && garbled.Error != null && garbled.Error.Contains("invited to could not be read"));

            // Zoom's acknowledged fault on this endpoint (2026): a 200 whose body is a bare {}. Named apart, so the user
            // checking whether invites come through can tell "Zoom sent nothing" from "Zoom sent no such meeting", and
            // still a warning, so the slot stays unvouched for the prune.
            var invitedEmpty = new FakeZoomApi
            {
                Answer = delegate(string url, string bearer) { return url.Contains("/upcoming_meetings") ? Ok(" { } ") : ZoomFixtures(url, bearer); },
            };
            CalendarSnapshot empty = invitedSourceOf(invitedEmpty).FetchForSelfTest(now);
            check("zoom invited: Zoom's empty {} answer is named as Zoom's empty answer, still a warning, and the hosted meetings stay",
                empty.Events.Count == 3 && empty.Error != null && empty.Error.Contains("Zoom sent an empty answer for the meetings you are invited to")
                && !empty.Error.Contains("could not be read"));
            // Every invited row needs its own time (the series call is the host's): a recurring row whose time cannot be
            // read and a row with no type and no time are counted unreadable, a warning, never dropped in silence; the
            // readable invited row and the hosted meetings stay.
            const string untimedPage = @"{""meetings"":[
{""id"":33000000001,""type"":2,""topic"":""Client standup"",""start_time"":""2026-03-10T16:30:00Z"",""duration"":15,""join_url"":""https://client.zoom.us/j/33000000001?pwd=c""},
{""id"":35000000003,""type"":8,""topic"":""Client weekly"",""start_time"":""bad""},
{""id"":36000000004,""topic"":""No type""}]}";
            var invitedUntimed = new FakeZoomApi
            {
                Answer = delegate(string url, string bearer) { return url.Contains("/upcoming_meetings") ? Ok(untimedPage) : ZoomFixtures(url, bearer); },
            };
            CalendarSnapshot untimed = invitedSourceOf(invitedUntimed).FetchForSelfTest(now);
            check("zoom invited: an invited row with no readable time is counted unreadable, a warning, not dropped in silence ("
                + untimed.Events.Count + ")",
                untimed.Events.Count == 4 && untimed.Events.Any(e => e.Title == "Client standup")
                && untimed.Error != null && untimed.Error.Contains("2 meeting(s) you are invited to could not be read"));
            check("zoom invited: only a member-less JSON object counts as Zoom's empty answer",
                ZoomMeetingsSource.IsEmptyObject("{}") && ZoomMeetingsSource.IsEmptyObject(" {\r\n} ")
                && !ZoomMeetingsSource.IsEmptyObject("{\"meetings\":[]}") && !ZoomMeetingsSource.IsEmptyObject("[]")
                && !ZoomMeetingsSource.IsEmptyObject("") && !ZoomMeetingsSource.IsEmptyObject(null) && !ZoomMeetingsSource.IsEmptyObject("{"));

            // The switch travels in the refresh key: the same instance answers differently once it is flipped.
            var flipApi = new FakeZoomApi { Answer = ZoomFixturesWithInvited };
            ZoomMeetingsSource flip = invitedSourceOf(flipApi);
            switchOn = true;
            int withInvited = flip.FetchForSelfTest(now).Events.Count;
            switchOn = false;
            int withoutInvited = flip.FetchForSelfTest(now).Events.Count;
            check("zoom invited: the switch is part of the refresh key, so flipping it is read at once",
                withInvited == 5 && withoutInvited == 3);
        }

        // ---- one meeting, two copies: the aggregate, the prune and the fired marks together -----------------

        private static void CheckZoomTwins(Action<string, bool> check)
        {
            var start = new DateTimeOffset(2026, 3, 10, 15, 0, 0, TimeSpan.Zero);
            const string link = "https://acme.zoom.us/j/91234567890?pwd=x";
            string zoomId = ZoomMeetingsSource.EventId("91234567890", start);   // the id the Zoom source gives it
            string zoomAgg = "cal5|" + zoomId;
            var outlookCopy = new CalendarEvent { Id = "o1", Title = "Design Review", Start = start, Description = link };
            var zoomCopy = new CalendarEvent { Id = zoomId, Title = "Design Review", Start = start, Description = link };
            // withOutlook: the calendar has its copy. zoomState: "ready" serves the Zoom copy, "loading" is the first tick
            // after a start (an error snapshot with no events), "none" is a Zoom slot that does not have this meeting.
            Func<bool, string, CalendarSnapshot> snapshot = delegate(bool withOutlook, string zoomState)
            {
                var zoomSource = new AggregateCalendarSource.StubSource(
                    zoomState == "ready" ? new[] { zoomCopy } : new CalendarEvent[0],
                    zoomState == "loading" ? "Reading your Zoom meetings…" : null);
                return new AggregateCalendarSource(new[]
                {
                    new AggregateCalendarSource.Slot { Id = "cal1", Label = "Work", Source = new AggregateCalendarSource.StubSource(withOutlook ? new[] { outlookCopy } : new CalendarEvent[0], null) },
                    new AggregateCalendarSource.Slot { Id = "cal5", Label = "", Source = zoomSource, YieldsToOthers = true },
                }).Fetch();
            };
            var leads = new[] { 5 };
            DateTimeOffset due = start.AddMinutes(-5);
            // One tick's fired-mark handling, in CheckDue's order: prune, mirror, spend what is due, mirror again.
            Func<CalendarSnapshot, HashSet<string>, int> tick = delegate(CalendarSnapshot snap, HashSet<string> marks)
            {
                PruneFiredAgainstFeed(snap, marks);
                MirrorTwinFired(snap, marks);
                IReadOnlyList<DueReminder> spent = ReminderScheduler.DueNowMulti(snap.Events, due, leads, marks);
                foreach (DueReminder d in spent) marks.Add(d.FiredId);
                MirrorTwinFired(snap, marks);
                return spent.Count;
            };

            // Zoom alone, then the invite arrives (the copies merge), then it goes again (they part): one announcement.
            var fired = new HashSet<string>(StringComparer.Ordinal);
            int firstAnnounce = tick(snapshot(false, "ready"), fired);
            check("zoom twins: WITNESS the Zoom copy alone fires", firstAnnounce == 1 && fired.Contains(zoomAgg + "@5"));
            CalendarSnapshot merged = snapshot(true, "ready");
            int onMerge = tick(merged, fired);
            check("zoom twins: once merged, the calendar copy counts as fired for the lead the Zoom copy fired, and the Zoom mark survives the prune",
                onMerge == 0 && fired.Contains("cal1|o1@5") && fired.Contains(zoomAgg + "@5"));
            check("zoom twins: parted again, the Zoom copy does not announce a second time", tick(snapshot(false, "ready"), fired) == 0);

            // A restart where the calendar loads before the Zoom slot: no twin is known yet, only the saved Zoom mark.
            var saved = new HashSet<string>(new[] { zoomAgg + "@5" }, StringComparer.Ordinal);
            check("zoom twins: after a restart with the calendar loaded and Zoom still reading, the calendar copy does not announce what the Zoom copy did",
                tick(snapshot(true, "loading"), saved) == 0 && saved.Contains("cal1|o1@5"));

            // The calendar copy fires while both are there, and is gone by the next tick: the Zoom copy stays quiet.
            var fromCalendar = new HashSet<string>(StringComparer.Ordinal);
            int calendarFires = tick(snapshot(true, "ready"), fromCalendar);
            check("zoom twins: a mark spent this tick reaches the other copy before the save",
                calendarFires == 1 && fromCalendar.Contains("cal1|o1@5") && fromCalendar.Contains(zoomAgg + "@5"));
            check("zoom twins: ...so with the calendar copy gone next tick, the Zoom copy does not announce", tick(snapshot(false, "ready"), fromCalendar) == 0);
            var joined = new HashSet<string>(new[] { "cal1|o1@join" }, StringComparer.Ordinal);
            MirrorTwinFired(snapshot(true, "ready"), joined);
            check("zoom twins: the join question's mark is carried like a lead's", joined.Contains(zoomAgg + "@join"));

            // A meeting the user is only invited to: the Zoom slot never has it, and the alias mark must not churn.
            var invited = new HashSet<string>(StringComparer.Ordinal);
            tick(snapshot(true, "none"), invited);
            bool prunedAgain = PruneFiredAgainstFeed(snapshot(true, "none"), invited);
            bool mirroredAgain = MirrorTwinFired(snapshot(true, "none"), invited);
            check("zoom twins: a calendar meeting the Zoom slot does not have keeps its alias mark steady, so the tick does not rewrite settings",
                invited.Contains("cal1|o1@5") && invited.Contains(zoomAgg + "@5") && !prunedAgain && !mirroredAgain);
            check("zoom twins: with no Zoom slot configured, nothing is mirrored to one",
                !MirrorTwinFired(new AggregateCalendarSource(new[]
                {
                    new AggregateCalendarSource.Slot { Id = "cal1", Label = "Work", Source = new AggregateCalendarSource.StubSource(new[] { outlookCopy }, null) },
                }).Fetch(), new HashSet<string>(new[] { "cal1|o1@5" }, StringComparer.Ordinal)));
        }

        // ---- the module: the card, the move out of slot 5, the source, a sign-in end to end -------------------

        private static void CheckZoomSlotModule(Action<string, bool> check)
        {
            // The card.
            var host = new RecordingHost();
            var module = new ReminderModule();
            module.Init(host);
            try
            {
                OptionsPane pane = host.OptionsPanes.Count > 0 ? host.OptionsPanes[0] : null;
                List<SettingField> fields = pane == null || pane.Schema == null ? new List<SettingField>() : pane.Schema.ToList();
                SettingField zoomType = fields.FirstOrDefault(f => f.Id == SlotKey(ZoomSlot, "type"));
                check("zoom module: slot 5's card is Zoom, its feed Off or Zoom only",
                    zoomType != null && zoomType.Group == "Zoom" && zoomType.Options != null && zoomType.Options.SequenceEqual(new[] { SourceOff, SourceZoom }));
                check("zoom module: the Zoom card has a client ID and a sign-in line, and no URL or file box",
                    fields.Any(f => f.Id == SlotKey(ZoomSlot, ZoomClientIdKey) && f.Group == "Zoom" && !string.IsNullOrEmpty(f.EnabledWhen))
                    && fields.Any(f => f.Id == SlotKey(ZoomSlot, ZoomStatusKey) && f.Kind == SettingKind.Info)
                    && !fields.Any(f => f.Id == SlotKey(ZoomSlot, "url") || f.Id == SlotKey(ZoomSlot, "file")));
                check("zoom module: the card says outright that it reads the meetings you host",
                    fields.Any(f => f.Id == SlotKey(ZoomSlot, ZoomAboutKey)) && ZoomAbout.Contains("HOST"));
                IReadOnlyDictionary<string, string> loaded = pane != null && pane.Load != null ? pane.Load() : null;
                string invitedLoaded;
                check("zoom module: 'Also read meetings I'm invited to' is on the Zoom card, greyed unless Zoom is chosen, and OFF by default",
                    fields.Any(f => f.Id == SlotKey(ZoomSlot, ZoomInvitedKey) && f.Kind == SettingKind.Bool && f.Group == "Zoom"
                        && !string.IsNullOrEmpty(f.EnabledWhen))
                    && loaded != null && loaded.TryGetValue(SlotKey(ZoomSlot, ZoomInvitedKey), out invitedLoaded) && invitedLoaded == "false");
                if (pane != null && pane.Save != null)
                {
                    pane.Save(new Dictionary<string, string> { { SlotKey(ZoomSlot, ZoomInvitedKey), "true" } });
                    IReadOnlyDictionary<string, string> reloaded = pane.Load();
                    string invitedSaved;
                    check("zoom module: ...and ticking it is saved",
                        reloaded.TryGetValue(SlotKey(ZoomSlot, ZoomInvitedKey), out invitedSaved) && invitedSaved == "true");
                }
                check("zoom module: calendars 1-4 are unchanged, Zoom not among their feeds",
                    Enumerable.Range(1, 4).All(i => fields.Any(f => f.Id == SlotKey(i, "type") && f.Group == "Calendar " + i
                        && f.Options.SequenceEqual(new[] { SourceOff, SourceLocalFile, SourceCalendarUrl, SourceOutlook }))));
                check("zoom module: Sign in and Sign out sit on the Zoom card, Sign in reading the values on screen",
                    pane != null && pane.Actions.Any(a => a.Label == "Sign in to Zoom…" && a.Group == "Zoom" && a.InvokeWithPendingAsync != null)
                    && pane.Actions.Any(a => a.Label == "Sign out of Zoom" && a.Group == "Zoom"));
                check("zoom module: Test and Browse on the Zoom card are in the Zoom card",
                    pane != null && pane.Actions.Count(a => a.Group == "Zoom") == 4);
            }
            finally { module.Shutdown(); }

            // A Calendar 5 from before 1.3.0 moves to the first free slot, its fired marks with it. Local file with no
            // path, so the moved slot never reads anything.
            var moveHost = new RecordingHost();
            FakeModuleSettings move = moveHost.SettingsFor(Id);
            move.Set(SlotKey(1, "type"), SourceLocalFile);
            move.Set(SlotKey(2, "type"), SourceOff);
            // An Off slot is not a blank one: an earlier calendar left its chime off and its colour red here.
            move.Set(SlotKey(2, "chimeOn"), "false");
            move.Set(SlotId(2) + "." + SpeechStyleSettings.ColorKey, "Red");
            move.Set(SlotKey(5, "type"), SourceLocalFile);
            move.Set(SlotKey(5, "label"), "Old five");
            move.Set(SlotId(5) + "." + SpeechStyleSettings.BoldKey, "true");   // set; its chime and colour never were
            move.Set("fired", "cal5|old@5\ncal1|keep@5");
            move.Save();
            var mover = new ReminderModule();
            mover.Init(moveHost);
            try
            {
                check("zoom module: a pre-1.3.0 Calendar 5 moves to the first free slot with its settings",
                    move.Get(SlotKey(2, "type"), "") == SourceLocalFile && move.Get(SlotKey(2, "label"), "") == "Old five"
                    && move.Get(SlotId(2) + "." + SpeechStyleSettings.BoldKey, "") == "true");
                check("zoom module: ...as it read: a key it never set takes the default, not what the Off slot held before",
                    move.Get(SlotKey(2, "chimeOn"), "") == "true" && move.Get(SlotId(2) + "." + SpeechStyleSettings.ColorKey, "") == "Default");
                check("zoom module: ...its fired marks are re-prefixed, so it does not announce again what it already did",
                    mover._fired.Contains("cal2|old@5") && !mover._fired.Contains("cal5|old@5") && mover._fired.Contains("cal1|keep@5"));
                check("zoom module: ...and slot 5 starts as Zoom's, switched off and back to defaults, the move logged",
                    move.Get(SlotKey(5, "type"), "") == SourceOff && move.Get(SlotKey(5, "label"), "x") == ""
                    && move.Get(SlotId(5) + "." + SpeechStyleSettings.BoldKey, "") == "false"
                    && move.Get(ZoomMigratedKey, "") == "1" && moveHost.LoggedLines.Any(l => l.Contains("moved to calendar 2")));
            }
            finally { mover.Shutdown(); }
            var again = new ReminderModule();
            again.Init(moveHost);
            try
            {
                check("zoom module: the move runs once: a second start leaves the moved slot alone",
                    move.Get(SlotKey(2, "label"), "") == "Old five" && moveHost.LoggedLines.Count(l => l.Contains("moved to calendar")) == 1);
            }
            finally { again.Shutdown(); }

            // All four in use: parked, nothing deleted, and the card says so.
            var parkHost = new RecordingHost();
            FakeModuleSettings park = parkHost.SettingsFor(Id);
            for (int i = 1; i <= 4; i++) park.Set(SlotKey(i, "type"), SourceLocalFile);
            park.Set(SlotKey(5, "type"), SourceCalendarUrl);
            park.Set(SlotKey(5, "url"), "https://calendar.example.invalid/basic.ics");
            park.Save();
            var parker = new ReminderModule();
            parker.Init(parkHost);
            try
            {
                check("zoom module: with calendars 1-4 in use the old Calendar 5 is parked, its values kept",
                    park.Get(SlotKey(5, "type"), "") == SourceOff && park.Get(ZoomParkedPrefix + "type", "") == SourceCalendarUrl
                    && park.Get(ZoomParkedPrefix + "url", "") == "https://calendar.example.invalid/basic.ics");
                check("zoom module: ...and the Zoom card leads with the note until Zoom is turned on",
                    parker.ZoomStatusText().StartsWith("Calendar 5 (" + SourceCalendarUrl + ") was switched off", StringComparison.Ordinal)
                    && parkHost.LoggedLines.Any(l => l.Contains("parked")));
            }
            finally { parker.Shutdown(); }

            // A sign-in end to end: the real listener, a simulated browser, a fake exchange, a fake API.
            var signHost = new RecordingHost();
            using (var storage = new TempModuleStorage(Id))
            {
                signHost.UseStorage(Id, storage);
                FakeModuleSettings sign = signHost.SettingsFor(Id);
                sign.Set(SlotKey(1, "type"), SourceZoom);   // Zoom named on another slot is not polled
                sign.Set(SlotKey(ZoomSlot, "type"), SourceZoom);
                sign.Set(SlotKey(ZoomSlot, ZoomClientIdKey), "testapp");
                sign.Set("hushPresenting", "false");
                sign.Save();
                var signer = new ReminderModule();
                signer.Init(signHost);
                try
                {
                    check("zoom module: Zoom is slot 5's source and no other slot's",
                        signer._slotSources[ZoomSlot] is ZoomMeetingsSource && signer._slotSources[1] == null);
                    // From here on the slot never reaches Zoom: it is rebuilt on a fake API.
                    var bearers = new List<string>();
                    signer.ZoomGetForSelfTest = delegate(string url, string bearer, TimeSpan deadline)
                    {
                        lock (bearers) bearers.Add(bearer);
                        return Ok("{\"meetings\":[]}");
                    };
                    signer._slotTypes[ZoomSlot] = null;
                    signer._source = signer.BuildSource();
                    check("zoom module: before a sign-in the card says to sign in",
                        signer.ZoomStatusText().StartsWith("Not signed in", StringComparison.Ordinal));

                    string exchangedCode = null, exchangedVerifier = null, challenge = null;
                    signer.ZoomExchangeForSelfTest = delegate(string clientId, string code, string redirect, string verifier)
                    {
                        exchangedCode = code;
                        exchangedVerifier = verifier;
                        return new ZoomSignIn.Tokens { AccessToken = "acc.sim", RefreshToken = "ref.sim", ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) };
                    };
                    Task browser = null;
                    signer.OpenUrlForSelfTest = delegate(string url)
                    {
                        challenge = QueryValue(url, "code_challenge");
                        var redirect = new Uri(QueryValue(url, "redirect_uri"));
                        string state = QueryValue(url, "state");
                        browser = Task.Run(() => SendRaw(redirect.Port, "GET " + redirect.AbsolutePath + "?code=sim-code&state=" + state
                            + " HTTP/1.1\r\nHost: " + redirect.Host + ":" + redirect.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n\r\n"));
                    };
                    OptionsPane pane = signHost.OptionsPanes[0];
                    PaneAction signIn = pane.Actions.First(a => a.Label == "Sign in to Zoom…");
                    PaneAction signOut = pane.Actions.First(a => a.Label == "Sign out of Zoom");
                    string mismatch = Task.Run(() => signIn.InvokeWithPendingAsync(new Dictionary<string, string>
                    {
                        { SlotKey(ZoomSlot, "type"), SourceZoom }, { SlotKey(ZoomSlot, ZoomClientIdKey), "another" },
                    })).GetAwaiter().GetResult();
                    check("zoom module: Sign in refuses a client ID typed and not applied", mismatch.StartsWith("✗ Apply first", StringComparison.Ordinal));
                    string result = Task.Run(() => signIn.InvokeWithPendingAsync(new Dictionary<string, string>
                    {
                        { SlotKey(ZoomSlot, "type"), SourceZoom }, { SlotKey(ZoomSlot, ZoomClientIdKey), "testapp" },
                    })).GetAwaiter().GetResult();
                    if (browser != null) browser.Wait(TimeSpan.FromSeconds(10));
                    check("zoom module: a sign-in through the browser and the loopback listener is kept (" + result + ")",
                        result.StartsWith("✓ Signed in", StringComparison.Ordinal) && signer._zoomTokens.SignedInClientId == "testapp");
                    check("zoom module: the exchange got the browser's code and the verifier the challenge came from",
                        exchangedCode == "sim-code" && exchangedVerifier != null && ZoomSignIn.ChallengeOf(exchangedVerifier) == challenge);
                    check("zoom module: the sign-in is logged without any token or code",
                        signHost.LoggedLines.Any(l => l.Contains("zoom: signed in"))
                        && !signHost.LoggedLines.Any(l => l.Contains("acc.sim") || l.Contains("ref.sim") || l.Contains("sim-code")));
                    bool read = SpinWait.SpinUntil(delegate { lock (bearers) return bearers.Contains("acc.sim"); }, TimeSpan.FromSeconds(10));
                    check("zoom module: the slot reads Zoom with the new sign-in's token straight away", read);
                    CalendarSnapshot landed = WaitForFeed(signer._source, delegate(CalendarSnapshot s)
                    {
                        bool healthy;
                        return s != null && s.SlotHealthy != null && s.SlotHealthy.TryGetValue(SlotId(ZoomSlot), out healthy) && healthy;
                    });
                    signer._lastSnapshot = landed;
                    check("zoom module: ...and the card says signed in once it has read",
                        landed != null && signer.ZoomStatusText().StartsWith("✓ Signed in", StringComparison.Ordinal));

                    string outWords = Task.Run(() => signOut.InvokeAsync()).GetAwaiter().GetResult();
                    check("zoom module: Sign out removes the sign-in, says so and logs it",
                        outWords.StartsWith("✓ Signed out", StringComparison.Ordinal) && signer._zoomTokens.SignedInClientId == null
                        && signHost.LoggedLines.Any(l => l.Contains("zoom: signed out")));

                    // A Sign out while a sign-in is still in the browser ends that sign-in, and it never lands.
                    signer.OpenUrlForSelfTest = delegate { };
                    Task<string> pendingSignIn = Task.Run(() => signIn.InvokeAsync());
                    bool waiting = SpinWait.SpinUntil(delegate { return signer._zoomSignInCts != null; }, TimeSpan.FromSeconds(10));
                    Task.Run(() => signOut.InvokeAsync()).GetAwaiter().GetResult();
                    bool finished = pendingSignIn.Wait(TimeSpan.FromSeconds(10));
                    check("zoom module: Sign out during a sign-in cancels it, and nothing is kept",
                        waiting && finished && pendingSignIn.Result == "✗ The sign-in was cancelled." && signer._zoomTokens.SignedInClientId == null);

                    // An Apply naming another client ID while the browser's code is being traded: that sign-in is not kept.
                    var trading = new ManualResetEventSlim(false);
                    var release = new ManualResetEventSlim(false);
                    signer.ZoomExchangeForSelfTest = delegate(string clientId, string code, string redirect, string verifier)
                    {
                        trading.Set();
                        release.Wait(TimeSpan.FromSeconds(10));
                        return new ZoomSignIn.Tokens { AccessToken = "acc.held", RefreshToken = "ref.held", ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) };
                    };
                    signer.OpenUrlForSelfTest = delegate(string url)
                    {
                        var redirect = new Uri(QueryValue(url, "redirect_uri"));
                        string state = QueryValue(url, "state");
                        browser = Task.Run(() => SendRaw(redirect.Port, "GET " + redirect.AbsolutePath + "?code=held-code&state=" + state
                            + " HTTP/1.1\r\nHost: " + redirect.Host + ":" + redirect.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n\r\n"));
                    };
                    Task<string> heldSignIn = Task.Run(() => signIn.InvokeAsync());
                    bool inExchange = trading.Wait(TimeSpan.FromSeconds(10));
                    pane.Save(new Dictionary<string, string> { { SlotKey(ZoomSlot, ZoomClientIdKey), "otherapp" } });
                    release.Set();
                    bool heldDone = heldSignIn.Wait(TimeSpan.FromSeconds(10));
                    check("zoom module: an Apply naming another client ID during the exchange means that sign-in is not kept",
                        inExchange && heldDone && heldSignIn.Result.StartsWith("✗ The Zoom card changed", StringComparison.Ordinal)
                        && signer._zoomTokens.SignedInClientId == null);
                }
                finally { signer.Shutdown(); }
            }

            CheckZoomThroughTick(check);
        }

        /// <summary>
        /// The twin rules through the module's own tick (CheckDue) on a RecordingHost with a companion out, not through the
        /// helpers: one meeting from the calendar and from Zoom is announced once across the copies merging, parting and a
        /// restart. Slot 1 is a Local file with no path and slot 5 Zoom with no sign-in, so the module's own sources never
        /// load and never vouch, which keeps a restart's first tick from pruning the saved marks (the shipped rule for a
        /// slot that cannot see its calendar); each step then feeds the module a stub aggregate.
        /// </summary>
        private static void CheckZoomThroughTick(Action<string, bool> check)
        {
            var host = new RecordingHost();
            FakeModuleSettings settings = host.SettingsFor(Id);
            settings.Set("hushPresenting", "false");
            settings.Set("leads", "5");
            settings.Set(SlotKey(1, "type"), SourceLocalFile);
            settings.Set(SlotKey(1, "label"), "Work");   // the spoken name comes from the settings, not the slot's label
            settings.Set(SlotKey(ZoomSlot, "type"), SourceZoom);
            settings.Set(SlotKey(ZoomSlot, ZoomClientIdKey), "testapp");
            settings.Save();
            DateTimeOffset start = DateTimeOffset.Now.AddMinutes(3);
            const string link = "https://acme.zoom.us/j/91234567890?pwd=x";
            var calendarCopy = new CalendarEvent { Id = "inv1", Title = "Design Review", Start = start, Description = link };
            var zoomCopy = new CalendarEvent { Id = ZoomMeetingsSource.EventId("91234567890", start), Title = "Design Review", Start = start, Description = link, Location = "Zoom" };
            Func<ReminderModule, bool, string, ICalendarSource> feed = delegate(ReminderModule m, bool withCalendar, string zoomState)
            {
                return new AggregateCalendarSource(new[]
                {
                    new AggregateCalendarSource.Slot { Id = "cal1", Label = "Work", Source = new AggregateCalendarSource.StubSource(withCalendar ? new[] { calendarCopy } : new CalendarEvent[0], null) },
                    new AggregateCalendarSource.Slot
                    {
                        Id = "cal5", Label = "", YieldsToOthers = true,
                        Source = new AggregateCalendarSource.StubSource(zoomState == "ready" ? new[] { zoomCopy } : new CalendarEvent[0],
                            zoomState == "loading" ? "Reading your Zoom meetings…" : null),
                    },
                }) { TwinOwnerFilter = m.PassesFilter };
            };
            Func<ReminderModule> start1 = delegate
            {
                var m = new ReminderModule();
                m.Init(host);
                host.RaiseCompanionSpawned(new FakeCompanion(1, "sheep"));
                return m;
            };

            ReminderModule module = start1();
            try
            {
                module._source = feed(module, true, "ready");
                module.CheckDue();
                check("zoom tick: WITNESS with both copies loaded the meeting is announced once, from the calendar ("
                    + host.BroadcastLines.Count + (host.BroadcastLines.Count > 0 ? ": " + host.BroadcastLines[0] : "") + ")",
                    host.BroadcastLines.Count == 1 && host.BroadcastLines[0].StartsWith("Work: ", StringComparison.Ordinal));
                module._source = feed(module, false, "ready");
                module.CheckDue();
                check("zoom tick: the calendar copy gone by the next tick, the Zoom copy does not announce it again",
                    host.BroadcastLines.Count == 1);
            }
            finally { module.Shutdown(); }

            // A second meeting, Zoom first; then a restart where the calendar loads before Zoom has read anything.
            var zoomFirstHost = host;
            var calendarCopy2 = new CalendarEvent { Id = "inv2", Title = "Retro", Start = start, Description = "https://acme.zoom.us/j/81234567890" };
            var zoomCopy2 = new CalendarEvent { Id = ZoomMeetingsSource.EventId("81234567890", start), Title = "Retro", Start = start, Description = "https://acme.zoom.us/j/81234567890", Location = "Zoom" };
            Func<ReminderModule, bool, bool, string, ICalendarSource> feed2 = delegate(ReminderModule m, bool withCalendar, bool zoomReady, string zoomError)
            {
                return new AggregateCalendarSource(new[]
                {
                    new AggregateCalendarSource.Slot { Id = "cal1", Label = "Work", Source = new AggregateCalendarSource.StubSource(withCalendar ? new[] { calendarCopy2 } : new CalendarEvent[0], null) },
                    new AggregateCalendarSource.Slot
                    {
                        Id = "cal5", Label = "", YieldsToOthers = true,
                        Source = new AggregateCalendarSource.StubSource(zoomReady ? new[] { zoomCopy2 } : new CalendarEvent[0], zoomError),
                    },
                }) { TwinOwnerFilter = m.PassesFilter };
            };
            ReminderModule first = start1();
            int before = zoomFirstHost.BroadcastLines.Count;
            try
            {
                first._source = feed2(first, false, true, null);
                first.CheckDue();
                check("zoom tick: WITNESS a meeting only Zoom has yet is announced from Zoom", zoomFirstHost.BroadcastLines.Count == before + 1);
            }
            finally { first.Shutdown(); }
            ReminderModule restarted = start1();
            try
            {
                restarted._source = feed2(restarted, true, false, "Reading your Zoom meetings…");
                restarted.CheckDue();
                check("zoom tick: after a restart where the calendar loads before Zoom, the calendar copy does not announce it again",
                    zoomFirstHost.BroadcastLines.Count == before + 1);
                restarted._source = feed2(restarted, true, true, null);
                restarted.CheckDue();
                check("zoom tick: ...nor once Zoom has loaded and the copies merge", zoomFirstHost.BroadcastLines.Count == before + 1);
            }
            finally { restarted.Shutdown(); }

            // Two calendars carrying the same Zoom invite, the Zoom slot on but not hosting it: announced once, from the
            // first slot, whether both copies load together or one a tick later; with no Zoom slot, both announce as
            // they did before 1.3.0.
            settings.Set(SlotKey(2, "type"), SourceLocalFile);
            settings.Set(SlotKey(2, "label"), "Gmail");
            settings.Save();
            Func<string, string, bool, bool, bool, ICalendarSource> twoCalendars = delegate(string number, string id, bool inWork, bool inGmail, bool zoomSlot)
            {
                string url = "https://acme.zoom.us/j/" + number;
                var slots = new List<AggregateCalendarSource.Slot>
                {
                    new AggregateCalendarSource.Slot { Id = "cal1", Label = "Work", Source = new AggregateCalendarSource.StubSource(inWork ? new[] { new CalendarEvent { Id = id + "w", Title = "Sync", Start = start, Description = url } } : new CalendarEvent[0], null) },
                    new AggregateCalendarSource.Slot { Id = "cal2", Label = "Gmail", Source = new AggregateCalendarSource.StubSource(inGmail ? new[] { new CalendarEvent { Id = id + "g", Title = "Sync", Start = start, Description = url } } : new CalendarEvent[0], null) },
                };
                if (zoomSlot)
                    slots.Add(new AggregateCalendarSource.Slot { Id = "cal5", Label = "", YieldsToOthers = true, Source = new AggregateCalendarSource.StubSource(new CalendarEvent[0], null) });
                return new AggregateCalendarSource(slots);
            };
            ReminderModule both = start1();
            try
            {
                int at = host.BroadcastLines.Count;
                both._source = twoCalendars("71234567890", "same", true, true, true);
                both.CheckDue();
                check("zoom tick: the same Zoom invite on two calendars due in one tick is announced once, from the first slot",
                    host.BroadcastLines.Count == at + 1 && host.BroadcastLines[at].StartsWith("Work: ", StringComparison.Ordinal));
                int staggered = host.BroadcastLines.Count;
                both._source = twoCalendars("61234567890", "late", true, false, true);
                both.CheckDue();
                both._source = twoCalendars("61234567890", "late", true, true, true);
                both.CheckDue();
                check("zoom tick: ...and once when the second calendar's copy loads a tick later",
                    host.BroadcastLines.Count == staggered + 1);
                int plain = host.BroadcastLines.Count;
                both._source = twoCalendars("51234567890", "plain", true, true, false);
                both.CheckDue();
                check("zoom tick: WITNESS with no Zoom slot the two calendars announce independently, as before 1.3.0",
                    host.BroadcastLines.Count == plain + 2);
            }
            finally { both.Shutdown(); }

            // An invite on the Outlook slot that the invited read also brings in. The Zoom copy knows no answer
            // (ResponseStatus ""); the calendar's copy has the user's. Through the module's own filter, where "Skip
            // meetings I've declined" is on by default: accepted in Outlook, announced once from Outlook (the WITNESS);
            // declined in Outlook, not announced from either copy.
            Func<ReminderModule, string, string, ICalendarSource> answered = delegate(ReminderModule m, string number, string answer)
            {
                string url = "https://client.zoom.us/j/" + number;
                return new AggregateCalendarSource(new[]
                {
                    new AggregateCalendarSource.Slot { Id = "cal1", Label = "Work", Source = new AggregateCalendarSource.StubSource(new[] { new CalendarEvent { Id = "ans" + number, Title = "Client sync", Start = start, Description = url, ResponseStatus = answer } }, null) },
                    new AggregateCalendarSource.Slot
                    {
                        Id = "cal5", Label = "", YieldsToOthers = true,
                        Source = new AggregateCalendarSource.StubSource(new[] { new CalendarEvent { Id = ZoomMeetingsSource.EventId(number, start), Title = "Client sync", Start = start, Description = url, Location = "Zoom", ResponseStatus = "" } }, null),
                    },
                }) { TwinOwnerFilter = m.PassesFilter };
            };
            ReminderModule invitedTick = start1();
            try
            {
                int at = host.BroadcastLines.Count;
                invitedTick._source = answered(invitedTick, "42345678901", "accepted");
                invitedTick.CheckDue();
                check("zoom tick: WITNESS an invite accepted in Outlook that the invited read also has is announced once, from Outlook",
                    host.BroadcastLines.Count == at + 1 && host.BroadcastLines[at].StartsWith("Work: ", StringComparison.Ordinal));
                int declinedAt = host.BroadcastLines.Count;
                invitedTick._source = answered(invitedTick, "43345678901", "declined");
                invitedTick.CheckDue();
                check("zoom tick: an invite declined in Outlook is not brought back by the Zoom card's invited read ("
                    + (host.BroadcastLines.Count - declinedAt) + " announced)",
                    host.BroadcastLines.Count == declinedAt);
            }
            finally { invitedTick.Shutdown(); }
        }
    }
}
