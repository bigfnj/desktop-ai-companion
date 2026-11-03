using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using DesktopAICompanion.ModuleKit;

namespace DesktopAICompanion.ReminderModule
{
    /// <summary>What <see cref="ZoomMeetingsSource"/> needs of a sign-in: a usable access token for a client ID.</summary>
    internal interface IZoomAccessToken
    {
        /// <summary>The access token to send for <paramref name="clientId"/>, renewed when it is near expiry, or
        /// null with the reason in <paramref name="error"/>. <paramref name="rejected"/> is a token Zoom has just
        /// answered 401 to: it is renewed unless the store already holds a newer one, so two attempts that hit the
        /// same 401 spend one rotation, not two.</summary>
        string GetAccessToken(string clientId, string rejected, out string error);
    }

    /// <summary>
    /// The Zoom sign-in, kept the way Remembrance keeps its cloud key (CloudKeyStore) and AI Brain its API key: DPAPI
    /// for the current Windows user with an entropy string of its own, in this module's data folder
    /// (zoom-signin.dpapi), never in settings.json, which the host stores as cleartext. The file holds the client ID
    /// and the REFRESH token; the access token lives in memory only and lasts an hour. Not IHostCredentials: that
    /// store holds one fixed credential, the Claude Code sign-in, and a second would be a host release.
    ///
    /// Zoom rotates refresh tokens: every renewal answers a new one and the old one is spent. So renewals are
    /// serialised (one at a time, <see cref="_renewLock"/>), the new token is held in memory before the file is
    /// rewritten, and a renewal that lands after a sign-in or a sign-out changed the store is discarded rather than
    /// written back over it (<see cref="Generation"/>). The fields have a lock of their own that is never held across
    /// the network, so the card's status line on the UI thread never waits on a renewal in flight.
    ///
    /// Not covered: a second copy of the app on the same data folder renewing at the same moment. One of the two
    /// would hold a spent token and the next renewal would answer invalid_grant: the slot says so and a sign-in
    /// fixes it. Recorded rather than locked across processes, because the host runs one copy per data folder.
    /// </summary>
    internal sealed class ZoomTokenStore : IZoomAccessToken
    {
        internal const string FileName = "zoom-signin.dpapi";
        internal static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DesktopAICompanion.Reminder.ZoomSignIn");

        /// <summary>Renew this long before the hour is up, so a token never expires between the check and the
        /// request it is sent on.</summary>
        internal static readonly TimeSpan RenewAhead = TimeSpan.FromMinutes(5);

        private readonly string _path;
        private readonly Func<string, string, ZoomSignIn.Tokens> _renew;
        private readonly Func<DateTimeOffset> _clock;
        private readonly object _renewLock = new object();
        private readonly object _stateLock = new object();

        private bool _loaded;
        private bool _unreadable;
        private bool _lastWriteFailed;
        private string _clientId;        // whose sign-in this is; null when signed out
        private string _refreshToken;    // the newest one; the file holds the same unless its last write failed
        private string _accessToken;
        private DateTimeOffset _accessExpiresUtc;
        private int _generation;

        /// <param name="folder">This module's data folder, or null when the host gave none.</param>
        internal ZoomTokenStore(string folder) : this(folder, ZoomSignIn.Refresh, delegate { return DateTimeOffset.UtcNow; }) { }

        /// <summary>The self-test's constructor: a renewal and a clock it controls.</summary>
        internal ZoomTokenStore(string folder, Func<string, string, ZoomSignIn.Tokens> renew, Func<DateTimeOffset> clock)
        {
            _path = string.IsNullOrWhiteSpace(folder) ? null : Path.Combine(folder, FileName);
            _renew = renew ?? ZoomSignIn.Refresh;
            _clock = clock ?? delegate { return DateTimeOffset.UtcNow; };
        }

        /// <summary>Bumped by every sign-in, sign-out and forced forget. The Zoom slot folds it into its refresh
        /// key, so a change is fetched at once and the previous sign-in's events are never served under the new one
        /// (the R-043 rule, applied to an account instead of a URL).</summary>
        internal int Generation { get { return Volatile.Read(ref _generation); } }

        internal bool HasFolder { get { return _path != null; } }

        /// <summary>The client ID the saved sign-in belongs to, or null when there is none.</summary>
        internal string SignedInClientId { get { lock (_stateLock) { EnsureLoaded(); return _clientId; } } }

        /// <summary>A sign-in file is there and could not be opened: another Windows user's, or damaged.</summary>
        internal bool Unreadable { get { lock (_stateLock) { EnsureLoaded(); return _unreadable; } } }

        /// <summary>The last renewal could not be written: this session holds the newest refresh token, and the file
        /// a restart would read holds a spent one until a later write succeeds.</summary>
        internal bool LastWriteFailed { get { lock (_stateLock) return _lastWriteFailed; } }

        /// <summary>Keep a fresh sign-in, replacing whatever was saved, for any client ID. Refused when the store's
        /// <see cref="Generation"/> is no longer <paramref name="startedAt"/>, the one read when this sign-in began: a
        /// Sign out pressed while the browser was open, or a second sign-in that finished first, wins, and this one's
        /// tokens are never written over it.</summary>
        internal bool Save(string clientId, ZoomSignIn.Tokens tokens, int startedAt, out string error)
        {
            if (tokens == null || !ZoomSignIn.IsTokenShaped(tokens.AccessToken) || !ZoomSignIn.IsTokenShaped(tokens.RefreshToken))
            {
                error = "Zoom's sign-in answer did not carry the tokens it should.";
                return false;
            }
            lock (_stateLock)
            {
                if (_generation != startedAt)
                {
                    error = "The Zoom sign-in changed while this one was in the browser, so this one was not kept. Press Sign in to Zoom again if you still want it.";
                    return false;
                }
                if (!WriteFile(clientId, tokens.RefreshToken, out error)) return false;
                _loaded = true;
                _unreadable = false;
                _lastWriteFailed = false;
                _clientId = clientId;
                _refreshToken = tokens.RefreshToken;
                _accessToken = tokens.AccessToken;
                _accessExpiresUtc = tokens.ExpiresUtc;
                Interlocked.Increment(ref _generation);
                return true;
            }
        }

        /// <summary>Delete the saved sign-in and forget the tokens in memory. The user's data, so the caller logs it
        /// either way (the delete-logging rule). Zoom is not told: the app's access ends when the user removes it
        /// under Zoom's Added Apps, which ZOOM.md says.</summary>
        internal bool SignOut(out string words)
        {
            lock (_stateLock)
            {
                EnsureLoaded();
                bool had = _clientId != null || _unreadable;
                // Delete without asking File.Exists first: it answers false for a file it may not read as well as for
                // none, and a sign-in file Windows would not let us at would then be reported as removed while it is
                // still there. Delete succeeds on a missing file and throws on a refused one; only a missing FOLDER
                // (nothing was ever saved) is the absence it reports by throwing.
                try
                {
                    if (_path != null) File.Delete(_path);
                }
                catch (DirectoryNotFoundException) { }
                catch (Exception ex)
                {
                    words = "✗ The saved Zoom sign-in could not be removed (" + ex.GetType().Name + "). Press Sign out again in a moment.";
                    return false;
                }
                Clear();
                words = had ? "✓ Signed out of Zoom. The Zoom card reads nothing until you sign in again." : "Not signed in to Zoom.";
                return true;
            }
        }

        public string GetAccessToken(string clientId, string rejected, out string error)
        {
            lock (_renewLock)
            {
                string refreshToken;
                int generation;
                lock (_stateLock)
                {
                    EnsureLoaded();
                    if (_clientId == null)
                    {
                        error = _unreadable
                            ? "The saved Zoom sign-in could not be opened (another Windows user's, or damaged). Press Sign in to Zoom again."
                            : "Not signed in to Zoom. Press Sign in to Zoom on the Zoom card.";
                        return null;
                    }
                    if (!string.Equals(_clientId, clientId, StringComparison.Ordinal))
                    {
                        error = "The Zoom sign-in was made with a different client ID from the one on the card. Press Sign in to Zoom again.";
                        return null;
                    }
                    // A renewal whose write failed left the only live refresh token in memory: try the file again on
                    // every call until it lands, so a restart after a transient failure does not lose the sign-in.
                    if (_lastWriteFailed)
                    {
                        string retryError;
                        _lastWriteFailed = !WriteFile(_clientId, _refreshToken, out retryError);
                    }
                    bool usable = _accessToken != null && _clock() < _accessExpiresUtc - RenewAhead;
                    if (usable && !string.Equals(_accessToken, rejected, StringComparison.Ordinal))
                    {
                        error = null;
                        return _accessToken;
                    }
                    refreshToken = _refreshToken;
                    generation = _generation;
                }

                // The network call, outside the state lock (see the class comment).
                ZoomSignIn.Tokens fresh;
                try { fresh = _renew(clientId, refreshToken); }
                catch (Exception ex) { fresh = new ZoomSignIn.Tokens { Error = "Zoom's sign-in service could not be reached (" + ex.GetType().Name + ")." }; }
                if (fresh == null) fresh = new ZoomSignIn.Tokens { Error = "Zoom's sign-in service gave no answer." };

                lock (_stateLock)
                {
                    if (_generation != generation)
                    {
                        // A sign-in or sign-out landed while this was out. Its result belongs to the old sign-in.
                        error = "The Zoom sign-in changed while it was being renewed; the next check uses the new one.";
                        return null;
                    }
                    if (fresh.Error != null || !ZoomSignIn.IsTokenShaped(fresh.AccessToken) || !ZoomSignIn.IsTokenShaped(fresh.RefreshToken))
                    {
                        if (fresh.Revoked)
                        {
                            // invalid_grant: this refresh token will never work again, so keeping it only repeats the
                            // refusal every five minutes. The status line says to sign in again.
                            try { if (_path != null) File.Delete(_path); } catch { }
                            Clear();
                        }
                        error = fresh.Error ?? "Zoom's sign-in answer did not carry the tokens it should.";
                        return null;
                    }
                    // The refresh token just sent is spent the moment Zoom answered. Memory first, then the file: a
                    // failed write leaves this session working and says so (LastWriteFailed) rather than dropping
                    // the only live token.
                    _refreshToken = fresh.RefreshToken;
                    _accessToken = fresh.AccessToken;
                    _accessExpiresUtc = fresh.ExpiresUtc;
                    string writeError;
                    _lastWriteFailed = !WriteFile(clientId, fresh.RefreshToken, out writeError);
                    error = null;
                    return fresh.AccessToken;
                }
            }
        }

        // Caller holds _stateLock.
        private void Clear()
        {
            _loaded = true;
            _unreadable = false;
            _lastWriteFailed = false;
            _clientId = null;
            _refreshToken = null;
            _accessToken = null;
            _accessExpiresUtc = DateTimeOffset.MinValue;
            Interlocked.Increment(ref _generation);
        }

        // Caller holds _stateLock. Reads the file once; Save, SignOut and a revoked renewal keep memory in step after.
        private void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            if (_path == null) return;
            try
            {
                if (!File.Exists(_path)) return;
                byte[] clear = ProtectedData.Unprotect(Convert.FromBase64String(File.ReadAllText(_path).Trim()), Entropy,
                    DataProtectionScope.CurrentUser);
                using (JsonDocument doc = JsonDocument.Parse(clear))
                {
                    JsonElement root = doc.RootElement;
                    string clientId = root.TryGetProperty("clientId", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    string refresh = root.TryGetProperty("refreshToken", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                    string checkedId;
                    if (ZoomSignIn.CheckClientId(clientId, out checkedId) != null || !ZoomSignIn.IsTokenShaped(refresh))
                    {
                        _unreadable = true;
                        return;
                    }
                    _clientId = checkedId;
                    _refreshToken = refresh;
                }
            }
            catch
            {
                _unreadable = true;
            }
        }

        // Caller holds _stateLock.
        private bool WriteFile(string clientId, string refreshToken, out string error)
        {
            if (_path == null)
            {
                error = "This module has no data folder of its own to keep a Zoom sign-in in.";
                return false;
            }
            string sealedText;
            try
            {
                byte[] clear = JsonSerializer.SerializeToUtf8Bytes(new SavedSignIn { Version = 1, ClientId = clientId, RefreshToken = refreshToken });
                sealedText = Convert.ToBase64String(ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex)
            {
                error = "Windows would not encrypt the Zoom sign-in (" + ex.GetType().Name + ").";
                return false;
            }
            if (!AtomicFile.TryWriteAllText(_path, sealedText, null))
            {
                error = "The Zoom sign-in could not be written to this module's folder.";
                return false;
            }
            error = null;
            return true;
        }

        private sealed class SavedSignIn
        {
            [System.Text.Json.Serialization.JsonPropertyName("v")] public int Version { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("clientId")] public string ClientId { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("refreshToken")] public string RefreshToken { get; set; }
        }
    }
}
