using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopAICompanion.ReminderModule
{
    /// <summary>
    /// Signing in to Zoom for the Zoom slot (1.3.0): OAuth 2.0's authorization-code flow with PKCE (RFC 7636), the
    /// shape Zoom requires of a public client, one that keeps no secret. The user creates their own Zoom app and
    /// pastes its client ID (ZOOM.md says how). The browser does the signing in, and Zoom sends it back to a one-shot
    /// listener at http://127.0.0.1:&lt;free port&gt;/zoom/callback. Zoom matches a loopback redirect ignoring only
    /// the port, so the app registers http://127.0.0.1/zoom/callback once and any free port works.
    ///
    /// A TcpListener, not HttpListener: HttpListener goes through http.sys, which refuses a prefix a standard user
    /// holds no URL reservation for, and all this needs is one GET line. It is bound to the IPv4 loopback only. The
    /// state value is compared before a code is taken, so a request another local process aims at the port, or a
    /// stale tab from an earlier attempt, is ignored rather than mistaken for the answer; and the verifier makes a
    /// code intercepted on the way useless without it. Neither the code nor any token is ever logged or shown.
    /// </summary>
    internal static class ZoomSignIn
    {
        internal const string AuthorizeEndpoint = "https://zoom.us/oauth/authorize";
        internal const string TokenEndpoint = "https://zoom.us/oauth/token";
        internal const string CallbackPath = "/zoom/callback";

        /// <summary>How long a sign-in waits for the browser to come back: long enough to type a password and pass
        /// an SSO prompt, short enough that an abandoned tab does not hold a port for the session.</summary>
        internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

        private static readonly TimeSpan TokenDeadline = TimeSpan.FromSeconds(30);
        // Per connection. A browser may open a connection it never sends on (a speculative preconnect), and the
        // listener serves one connection at a time, so this is how long such a socket can delay the real request.
        private static readonly TimeSpan RequestReadDeadline = TimeSpan.FromSeconds(3);
        private const int MaximumRequestHeadBytes = 8 * 1024;
        private const long MaximumTokenResponseBytes = 64 * 1024;
        // A sign-in that is never answered still ends at Timeout; this only stops a flood of unrelated requests.
        private const int MaximumConnections = 64;
        internal const int MaximumTokenCharacters = 8192;

        // --- PKCE -------------------------------------------------------------------------------------

        /// <summary>A fresh code verifier: 32 random bytes as unpadded base64url, 43 characters, inside RFC 7636's
        /// 43-128 range and its unreserved alphabet.</summary>
        internal static string NewVerifier() { return Base64Url(RandomNumberGenerator.GetBytes(32)); }

        internal static string NewState() { return Base64Url(RandomNumberGenerator.GetBytes(24)); }

        /// <summary>The S256 challenge: base64url(SHA-256(ASCII(verifier))), unpadded.</summary>
        internal static string ChallengeOf(string verifier)
        {
            return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier ?? "")));
        }

        internal static string Base64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes ?? Array.Empty<byte>()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        internal static string BuildAuthorizeUrl(string clientId, string redirectUri, string challenge, string state)
        {
            return AuthorizeEndpoint
                + "?response_type=code"
                + "&client_id=" + Uri.EscapeDataString(clientId ?? "")
                + "&redirect_uri=" + Uri.EscapeDataString(redirectUri ?? "")
                + "&code_challenge=" + Uri.EscapeDataString(challenge ?? "")
                + "&code_challenge_method=S256"
                + "&state=" + Uri.EscapeDataString(state ?? "");
        }

        /// <summary>Null when <paramref name="value"/> can be a Zoom app's client ID, which comes back trimmed; else
        /// the reason, in the card's words. It goes into a URL and a form body, so it is held to the characters a
        /// client ID is made of rather than escaped into something Zoom will refuse anyway.</summary>
        internal static string CheckClientId(string value, out string clientId)
        {
            clientId = (value ?? "").Trim();
            if (clientId.Length == 0) return "No Zoom app client ID is set.";
            if (clientId.Length > 128) return "That is longer than a Zoom client ID can be.";
            foreach (char c in clientId)
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '_' && c != '-' && c != '.')
                    return "A Zoom client ID is letters, digits, '-', '_' and '.', and this one has another character in it, so it was probably not copied whole.";
            return null;
        }

        // --- the loopback answer ------------------------------------------------------------------------

        internal enum CallbackKind { Ignored, Code, Denied }

        internal sealed class Callback
        {
            public CallbackKind Kind;
            public string Code;
            public string Error;
        }

        /// <summary>
        /// Read one request head ("GET /zoom/callback?code=..&amp;state=.. HTTP/1.1", then its headers) against the
        /// state this attempt sent and the host it listens on. Ignored for anything that is not a GET of the callback
        /// path, addressed to 127.0.0.1:&lt;port&gt;, carrying this attempt's state exactly once: a favicon fetch, a
        /// stale tab from an earlier attempt, a page that rebound its own name to the loopback (its Host is its own
        /// name) and a stranger's probe all land there, and the wait goes on. State is checked before a code OR an
        /// error is looked at, so only this attempt's answer can end it. A query that repeats state, code or error,
        /// or carries both a code and an error, is not an answer Zoom sends, and is ignored too. Denied when Zoom
        /// answers with an error (the user pressed Decline, or the account does not allow the app), which ends the
        /// attempt. Pure, so the self-test drives every branch.
        /// </summary>
        internal static Callback ParseCallback(string requestHead, string expectedState, string expectedHost)
        {
            var ignored = new Callback { Kind = CallbackKind.Ignored };
            if (string.IsNullOrEmpty(requestHead) || string.IsNullOrEmpty(expectedState)) return ignored;
            string[] lines = requestHead.Replace("\r\n", "\n").Split('\n');
            string[] parts = lines[0].Split(' ');
            if (parts.Length != 3 || !string.Equals(parts[0], "GET", StringComparison.Ordinal)
                || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                return ignored;
            if (!string.IsNullOrEmpty(expectedHost))
            {
                string host = null;
                int hosts = 0;
                for (int i = 1; i < lines.Length; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    if (colon <= 0) continue;
                    if (!string.Equals(lines[i].Substring(0, colon).Trim(), "Host", StringComparison.OrdinalIgnoreCase)) continue;
                    hosts++;
                    host = lines[i].Substring(colon + 1).Trim();
                }
                if (hosts != 1 || !string.Equals(host, expectedHost, StringComparison.OrdinalIgnoreCase)) return ignored;
            }
            string target = parts[1];
            int q = target.IndexOf('?');
            string path = q < 0 ? target : target.Substring(0, q);
            if (!string.Equals(path, CallbackPath, StringComparison.Ordinal)) return ignored;
            HashSet<string> repeated;
            Dictionary<string, string> query = ParseQuery(q < 0 ? "" : target.Substring(q + 1), out repeated);
            if (repeated.Contains("state") || repeated.Contains("code") || repeated.Contains("error")) return ignored;
            string state;
            if (!query.TryGetValue("state", out state) || !FixedTimeEquals(state, expectedState)) return ignored;
            string error, code;
            bool hasError = query.TryGetValue("error", out error) && error.Length > 0;
            bool hasCode = query.TryGetValue("code", out code) && code.Length > 0;
            if (hasError && hasCode) return ignored;
            if (hasError) return new Callback { Kind = CallbackKind.Denied, Error = SafeWord(error) };
            if (!hasCode || code.Length > MaximumTokenCharacters) return ignored;
            return new Callback { Kind = CallbackKind.Code, Code = code };
        }

        /// <summary>Query pairs, the first of each name kept, and the names that appeared more than once. '+' is a
        /// space, as a browser encodes a form.</summary>
        private static Dictionary<string, string> ParseQuery(string query, out HashSet<string> repeated)
        {
            var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
            repeated = new HashSet<string>(StringComparer.Ordinal);
            foreach (string pair in (query ?? "").Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string name, value;
                try
                {
                    name = Uri.UnescapeDataString((eq < 0 ? pair : pair.Substring(0, eq)).Replace('+', ' '));
                    value = eq < 0 ? "" : Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                }
                catch { continue; }
                if (pairs.ContainsKey(name)) repeated.Add(name);
                else pairs[name] = value;
            }
            return pairs;
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            byte[] x = Encoding.UTF8.GetBytes(a ?? ""), y = Encoding.UTF8.GetBytes(b ?? "");
            return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
        }

        /// <summary>An OAuth error code fit for the status line: [a-z0-9_] only, at most 64 characters. Anything
        /// else in it (a description, a URL) is dropped rather than shown.</summary>
        internal static string SafeWord(string word)
        {
            var sb = new StringBuilder();
            foreach (char c in word ?? "")
            {
                char lower = char.ToLowerInvariant(c);
                if ((lower >= 'a' && lower <= 'z') || (lower >= '0' && lower <= '9') || lower == '_') sb.Append(lower);
                if (sb.Length >= 64) break;
            }
            return sb.Length == 0 ? "no reason given" : sb.ToString();
        }

        internal sealed class Authorization
        {
            public string Code;
            public string RedirectUri;
            public string Verifier;
            public string Error;   // null on success; the card's words otherwise
        }

        /// <summary>
        /// Open the authorize page through <paramref name="openBrowser"/> and wait, bounded by
        /// <paramref name="timeout"/> and <paramref name="cancel"/>, for the browser to come back with a code.
        /// Never throws. Every await inside is ConfigureAwait(false): the socket work never needs the UI thread the
        /// pane button awaits this on.
        /// </summary>
        internal static async Task<Authorization> AuthorizeAsync(string clientId, Func<string, bool> openBrowser,
            TimeSpan timeout, CancellationToken cancel)
        {
            string verifier = NewVerifier();
            string state = NewState();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try { listener.Start(); }
            catch (SocketException ex)
            {
                return new Authorization { Error = "A local port for Zoom's answer could not be opened (" + ex.SocketErrorCode + ")." };
            }
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                string host = "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
                string redirect = "http://" + host + CallbackPath;
                bool opened;
                try { opened = openBrowser != null && openBrowser(BuildAuthorizeUrl(clientId, redirect, ChallengeOf(verifier), state)); }
                catch { opened = false; }
                if (!opened) return new Authorization { Error = "The browser could not be opened for the Zoom sign-in." };

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    cts.CancelAfter(timeout);
                    for (int i = 0; i < MaximumConnections; i++)
                    {
                        TcpClient client;
                        try { client = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException)
                        {
                            return new Authorization
                            {
                                Error = cancel.IsCancellationRequested
                                    ? "The sign-in was cancelled."
                                    : "Zoom did not answer within " + ((int)timeout.TotalMinutes).ToString(CultureInfo.InvariantCulture)
                                      + " minutes. Press Sign in to Zoom to try again.",
                            };
                        }
                        using (client)
                        {
                            Callback answer = await ReadAndAnswerAsync(client, state, host, cts.Token).ConfigureAwait(false);
                            if (answer.Kind == CallbackKind.Code)
                                return new Authorization { Code = answer.Code, RedirectUri = redirect, Verifier = verifier };
                            if (answer.Kind == CallbackKind.Denied)
                                return new Authorization { Error = "Zoom did not sign you in (" + answer.Error + ")." };
                        }
                    }
                    return new Authorization { Error = "Too many unrelated requests reached the sign-in port. Press Sign in to Zoom to try again." };
                }
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }
        }

        private static async Task<Callback> ReadAndAnswerAsync(TcpClient client, string state, string host, CancellationToken cancel)
        {
            var ignored = new Callback { Kind = CallbackKind.Ignored };
            try
            {
                using (var read = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    read.CancelAfter(RequestReadDeadline);
                    NetworkStream stream = client.GetStream();
                    string head = await ReadRequestHeadAsync(stream, read.Token).ConfigureAwait(false);
                    if (head == null) return ignored;
                    Callback answer = ParseCallback(head, state, host);
                    string status, page;
                    if (answer.Kind == CallbackKind.Code)
                    {
                        status = "200 OK";
                        // Not "signed in": the code is yet to be traded and kept, and either can still fail. The card says.
                        page = "Zoom has answered. You can close this tab; the Zoom card in Desktop AI Companion says when the sign-in is done.";
                    }
                    else if (answer.Kind == CallbackKind.Denied)
                    {
                        status = "200 OK";
                        page = "Zoom did not sign you in. You can close this tab; Desktop AI Companion says why on the Zoom card.";
                    }
                    else
                    {
                        status = "404 Not Found";
                        page = "Not found.";
                    }
                    byte[] body = Encoding.UTF8.GetBytes("<!doctype html><html><head><meta charset=\"utf-8\"><title>Desktop AI Companion</title></head>"
                        + "<body style=\"font-family:sans-serif;margin:3em\"><p>" + page + "</p></body></html>");
                    byte[] responseHead = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\n"
                        + "Content-Type: text/html; charset=utf-8\r\n"
                        + "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
                        + "Cache-Control: no-store\r\n"
                        + "Referrer-Policy: no-referrer\r\n"
                        + "Connection: close\r\n\r\n");
                    await stream.WriteAsync(responseHead, 0, responseHead.Length, read.Token).ConfigureAwait(false);
                    await stream.WriteAsync(body, 0, body.Length, read.Token).ConfigureAwait(false);
                    return answer;
                }
            }
            catch
            {
                // A connection that sent nothing, reset, or outlived its read deadline: not the answer; keep waiting.
                return ignored;
            }
        }

        /// <summary>The request line and headers, up to the blank line, as ASCII; null when the connection closes
        /// first (a partial request is not an answer) or the head outgrows the cap. The body, if any, is never read.</summary>
        private static async Task<string> ReadRequestHeadAsync(NetworkStream stream, CancellationToken cancel)
        {
            var bytes = new List<byte>(1024);
            byte[] chunk = new byte[1024];
            while (bytes.Count < MaximumRequestHeadBytes)
            {
                int read = await stream.ReadAsync(chunk, 0, chunk.Length, cancel).ConfigureAwait(false);
                if (read <= 0) return null;
                for (int i = 0; i < read; i++)
                {
                    bytes.Add(chunk[i]);
                    int n = bytes.Count;
                    if (n >= 4 && bytes[n - 4] == (byte)'\r' && bytes[n - 3] == (byte)'\n' && bytes[n - 2] == (byte)'\r' && bytes[n - 1] == (byte)'\n')
                        return Encoding.ASCII.GetString(bytes.ToArray(), 0, n - 4);
                    if (n >= MaximumRequestHeadBytes) return null;
                }
            }
            return null;
        }

        // --- the token endpoint -------------------------------------------------------------------------

        internal sealed class Tokens
        {
            public string AccessToken;
            public string RefreshToken;
            public DateTimeOffset ExpiresUtc;
            public string Error;     // null on success; the card's words otherwise, never a token
            public bool Revoked;     // invalid_grant: this refresh token (or code) will never work again
        }

        /// <summary>Trade the code for the first token pair. PKCE's public-client exchange: the verifier and the
        /// client ID in the form body, no Authorization header and no secret (Zoom's OAuth documentation).</summary>
        internal static Tokens ExchangeCode(string clientId, string code, string redirectUri, string verifier)
        {
            return PostToken(new Dictionary<string, string>
            {
                { "grant_type", "authorization_code" },
                { "code", code ?? "" },
                { "redirect_uri", redirectUri ?? "" },
                { "code_verifier", verifier ?? "" },
                { "client_id", clientId ?? "" },
            });
        }

        /// <summary>Trade a refresh token for a new pair. Zoom rotates refresh tokens: the answer's refresh token
        /// replaces the one sent, and the caller must keep the new one (<see cref="ZoomTokenStore"/>).
        ///
        /// UNVERIFIED as of 1.3.0: Zoom's documentation shows the public-client CODE exchange (client_id in the body,
        /// no Authorization header) but still illustrates the refresh with a confidential client's Basic header. This
        /// sends the public form by inference. If Zoom refuses it, the first renewal an hour after signing in fails
        /// and the card says so; SMOKETEST's Zoom row checks exactly that hour. Kept to this one method so a fix is
        /// one place.</summary>
        internal static Tokens Refresh(string clientId, string refreshToken)
        {
            return PostToken(new Dictionary<string, string>
            {
                { "grant_type", "refresh_token" },
                { "refresh_token", refreshToken ?? "" },
                { "client_id", clientId ?? "" },
            });
        }

        private static Tokens PostToken(Dictionary<string, string> form)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(form) })
                {
                    request.Headers.Accept.ParseAdd("application/json");
                    ZoomHttp.Response response = ZoomHttp.Send(request, TokenDeadline, MaximumTokenResponseBytes);
                    return ParseTokenResponse(response.Status, response.Body, DateTimeOffset.UtcNow);
                }
            }
            catch (TimeoutException ex) { return new Tokens { Error = ex.Message }; }
            catch (Exception ex) { return new Tokens { Error = "Zoom's sign-in service could not be reached (" + ex.GetType().Name + ")." }; }
        }

        /// <summary>
        /// Read the token endpoint's answer. Never throws and never puts a token in Error. A 200 must carry both
        /// tokens: an answer with no refresh token would leave the slot signed in for one hour and then silently
        /// signed out, so it is refused at the door. invalid_grant on a 4xx is the one answer that means "this
        /// sign-in is over" (a rotated-away or revoked refresh token, a used code); every other failure is
        /// transient as far as the saved sign-in is concerned.
        /// </summary>
        internal static Tokens ParseTokenResponse(int status, string body, DateTimeOffset nowUtc)
        {
            JsonDocument doc = null;
            try
            {
                try { doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body); }
                catch (JsonException) { doc = null; }
                JsonElement root = doc != null && doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement : default(JsonElement);
                if (status == 200)
                {
                    string access = Str(root, "access_token"), refresh = Str(root, "refresh_token");
                    if (!IsTokenShaped(access) || !IsTokenShaped(refresh))
                        return new Tokens { Error = "Zoom's sign-in answer did not carry the tokens it should." };
                    long seconds = 3600;
                    JsonElement expires;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("expires_in", out expires)
                        && expires.ValueKind == JsonValueKind.Number && expires.TryGetInt64(out seconds))
                    { }
                    if (seconds < 60 || seconds > 86400) seconds = 3600;
                    return new Tokens { AccessToken = access, RefreshToken = refresh, ExpiresUtc = nowUtc.AddSeconds(seconds) };
                }
                string error = Str(root, "error");
                if (status >= 400 && status < 500 && string.Equals(error, "invalid_grant", StringComparison.Ordinal))
                    return new Tokens { Revoked = true, Error = "Zoom no longer accepts this sign-in. Press Sign in to Zoom again." };
                if (status >= 400 && status < 500 && !string.IsNullOrEmpty(error))
                    return new Tokens { Error = "Zoom refused the sign-in (" + SafeWord(error) + ")." };
                return new Tokens { Error = "Zoom's sign-in service answered HTTP " + status.ToString(CultureInfo.InvariantCulture) + "." };
            }
            finally
            {
                if (doc != null) doc.Dispose();
            }
        }

        private static string Str(JsonElement obj, string name)
        {
            JsonElement value;
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out value) || value.ValueKind != JsonValueKind.String) return null;
            return value.GetString();
        }

        /// <summary>One unbroken run of printable ASCII, under the cap. A token goes into a header and a file, and a
        /// value that is not that shape is a broken answer, not a token.</summary>
        internal static bool IsTokenShaped(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length > MaximumTokenCharacters) return false;
            foreach (char c in token)
                if (c < '!' || c > '~') return false;
            return true;
        }
    }
}
