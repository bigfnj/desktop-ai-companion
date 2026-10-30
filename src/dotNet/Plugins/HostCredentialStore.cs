using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using DesktopAICompanion.ModuleKit;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.Plugins
{
    /// <summary>
    /// The one Claude Code sign-in token the host keeps for the modules the user allows (IHostCredentials, host 1.8.0;
    /// BACKLOG N-cli-token-02, the owner's decision of 2026-10-29). CompanionHost answers the interface through this; the
    /// design and the review it answered are under "#### feature/host-abi-180" in docs/DESIGN-REGISTER.md.
    ///
    /// THE GATE, on every read and every change: the caller must be a loaded module declaring
    /// ModulePermissions.ClaudeCodeSignIn (the declaration predicate CompanionHost hands in, its ModuleDeclares) AND hold
    /// the user's grant, recorded here in grants.json by the Modules pane (an Allow on the module's row, or a yes to an
    /// update that adds the flag). Refused is refused: nothing is read, written, imported or moved, and the refusal is
    /// logged by module id and reason only.
    ///
    /// THE FILE is one envelope, replaced whole by AtomicFile: a schema number, the revision, the state ("saved" with the
    /// DPAPI-sealed value, or "removed") and nothing else. No file is the store never used, revision 0. One file rather than
    /// a sealed blob beside a marker, so the state, the revision and the value can never disagree after a crash; it is
    /// named .json rather than the .dpapi the first design named because the DPAPI blob is now one member of it.
    ///
    /// ONE TRANSACTION PER CHANGE. Save, import and remove each take the store's CrossSessionLock (a Global mutex and a
    /// file lease, so a second instance of the app on the same data folder waits too), re-read the envelope under it,
    /// decide, commit, and release, on one thread with no await inside. An import commits only if the envelope it read
    /// UNDER THE LOCK still allows one, which is the compare-and-set the review asked for: a reader that saw an empty store,
    /// then lost the race to another module's save, finds the save there and imports nothing. Reads take no lock (the
    /// replace is atomic, so a reader sees the old envelope or the new one).
    ///
    /// The change callback runs after the lock is released; CompanionHost posts it to the UI thread and raises each
    /// handler on its own. Values never reach the log, the callback or an outcome.
    /// </summary>
    internal sealed class HostCredentialStore
    {
        internal const string DirectoryName = "credentials";
        internal const string ClaudeCodeSignInFileName = "claude-code-signin.json";
        internal const string GrantsFileName = "grants.json";
        internal const int MaximumValueCharacters = 4096;
        internal const int LockTimeoutMilliseconds = 5000;
        private const int Schema = 1;
        private const string SavedState = "saved";
        private const string RemovedState = "removed";
        private const string GrantsMember = "claudeCodeSignIn";

        /// <summary>Separates this store's DPAPI data from the same user's other DPAPI data; not a secret, and no boundary
        /// between modules (DPAPI CurrentUser opens for any process of the user, which is why the gate above exists).</summary>
        internal static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DesktopAICompanion.Host.Credentials.ClaudeCodeSignIn");

        private readonly string _directory;
        private readonly Func<string, bool> _declares;
        private readonly Action<string, string> _log;

        /// <summary>Called once per committed change (Saved, Imported, Removed; a NothingToRemove commits nothing), after the
        /// lock is released, with the credential and the revision now current. Never with a value. CompanionHost hands it
        /// on only to the modules allowed the credential (<see cref="Allows"/>).</summary>
        internal Action<HostCredentialId, long> Committed;

        /// <summary>How long a change waits for the store's lock before answering Unavailable; the self-test shortens it.</summary>
        internal int LockTimeout = LockTimeoutMilliseconds;

        /// <param name="directory">Where the envelope and the grants live: <see cref="DefaultDirectory"/> in the app.</param>
        /// <param name="declares">Whether a module id is a loaded module declaring ModulePermissions.ClaudeCodeSignIn.</param>
        /// <param name="log">The host's log, as (module id, line). Never handed a value.</param>
        internal HostCredentialStore(string directory, Func<string, bool> declares, Action<string, string> log)
        {
            _directory = directory;
            _declares = declares;
            _log = log;
            lock (LiveStores)
            {
                LiveStores.RemoveAll(w => { HostCredentialStore s; return !w.TryGetTarget(out s); });
                LiveStores.Add(new WeakReference<HostCredentialStore>(this));
            }
        }

        // A RECORDED ALLOW REACHES THE STORES ON ITS FOLDER (N-final-host-02). SetGrant is static, called by the Modules pane and
        // the start question, neither of which holds a host; since CredentialChanged reaches only allowed modules (N-audit-
        // runner-02), a module allowed just now heard nothing and kept its refused readings until it next read the store. Weak,
        // so the list pins no store and no host (a host has no teardown, and the self-tests make many).
        private static readonly List<WeakReference<HostCredentialStore>> LiveStores = new List<WeakReference<HostCredentialStore>>();

        /// <summary>Called with a module's id after an Allow for it was recorded in this store's folder (CompanionHost raises
        /// CredentialChanged to that module alone at the current revision). Never with a value.</summary>
        internal Action<string> Granted;

        /// <summary>The revision the envelope holds now, -1 when it cannot be read: what a newly allowed module is told, so its
        /// runner reads the store it was refused until now. No gate: a number, never a value.</summary>
        internal long CurrentRevision()
        {
            try
            {
                Envelope envelope = ReadEnvelope();
                if (envelope.Unavailable || envelope.Damaged) return -1;
                return envelope.Exists ? envelope.Revision : 0;
            }
            catch { return -1; }
        }

        private static void AnnounceGrant(string directory, string moduleId)
        {
            var targets = new List<HostCredentialStore>();
            lock (LiveStores)
                foreach (WeakReference<HostCredentialStore> w in LiveStores)
                {
                    HostCredentialStore s;
                    if (w.TryGetTarget(out s) && SameDirectory(s._directory, directory)) targets.Add(s);
                }
            foreach (HostCredentialStore s in targets)
            {
                Action<string> granted = s.Granted;
                if (granted == null) continue;
                try { granted(moduleId); } catch { }
            }
        }

        private static bool SameDirectory(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>&lt;data folder&gt;\credentials: outside every module's folder (an uninstall deletes those,
        /// PendingModuleRemovals), so one module's uninstall leaves the sign-in for the others, and inside the data folder,
        /// so a factory reset (FactoryReset.Wipe) deletes it.</summary>
        internal static string DefaultDirectory { get { return Path.Combine(AppPaths.DataRoot, DirectoryName); } }

        internal string StoreDirectory { get { return _directory; } }

        private string EnvelopePath { get { return Path.Combine(_directory ?? "", ClaudeCodeSignInFileName); } }

        // ---- the gate ----

        /// <summary>Null when <paramref name="moduleId"/> may use <paramref name="credential"/>; otherwise the refusal, as a
        /// state, with its log word.</summary>
        private HostCredentialState? Refusal(string moduleId, HostCredentialId credential, out string reason)
        {
            reason = null;
            if (credential != HostCredentialId.ClaudeCodeSignIn) { reason = "unknown-credential"; return HostCredentialState.Denied; }
            if (string.IsNullOrWhiteSpace(_directory)) { reason = "no-data-folder"; return HostCredentialState.Unavailable; }
            bool declared;
            try { declared = _declares != null && _declares((moduleId ?? "").Trim()); }
            catch { declared = false; }
            if (!declared) { reason = "not-declared"; return HostCredentialState.Denied; }
            // A grants record that cannot be read, or is damaged, is Unavailable and never "not allowed" (the independent
            // review's P2-1): read as not allowed, a module with no old token would run its calls on Claude Code's own
            // sign-in, another account than the one the user saved a token for.
            bool readable;
            bool granted = IsGranted(_directory, moduleId, out readable);
            if (!readable) { reason = "grants-unreadable"; return HostCredentialState.Unavailable; }
            if (!granted) { reason = "not-allowed"; return HostCredentialState.ConsentRequired; }
            return null;
        }

        /// <summary>Whether <paramref name="moduleId"/> passes the gate for <paramref name="credential"/> now: a loaded module
        /// declaring the flag, with the user's grant, read from a grants record that can be read. CompanionHost asks it before
        /// it hands a module CredentialChanged (BACKLOG N-audit-runner-02), so a module the store would refuse never hears a
        /// revision. Logs nothing, and false on any failure.</summary>
        internal bool Allows(string moduleId, HostCredentialId credential)
        {
            try
            {
                string reason;
                return !Refusal(moduleId, credential, out reason).HasValue;
            }
            catch { return false; }
        }

        /// <summary>Each (module, verb, reason) refusal is logged once per store, not on every read: a module the user has
        /// not allowed reads the store on every pane open and every call (the independent review's P3-8).</summary>
        private readonly HashSet<string> _loggedRefusals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void LogRefusal(string moduleId, string verb, string reason)
        {
            lock (_loggedRefusals)
                if (!_loggedRefusals.Add((moduleId ?? "") + "|" + verb + "|" + reason)) return;
            Log(moduleId, verb + " refused: " + reason);
        }

        private static HostCredentialOutcome OutcomeFor(HostCredentialState refusal)
        {
            if (refusal == HostCredentialState.ConsentRequired) return HostCredentialOutcome.ConsentRequired;
            if (refusal == HostCredentialState.Unavailable) return HostCredentialOutcome.Unavailable;
            return HostCredentialOutcome.Denied;
        }

        private void Log(string moduleId, string line)
        {
            Action<string, string> sink = _log;
            if (sink == null) return;
            try { sink(string.IsNullOrWhiteSpace(moduleId) ? null : moduleId.Trim(), "credentials: " + line); } catch { }
        }

        // ---- the envelope ----

        private sealed class Envelope
        {
            internal bool Exists;
            /// <summary>The file is there and could not be read now (held by another process, access refused): neither
            /// never used nor damaged, so nothing may be decided over it.</summary>
            internal bool Unavailable;
            internal bool Damaged;
            internal long Revision;
            internal string State = "";
            internal string Sealed = "";
        }

        internal enum FileRead { Missing, Read, Unavailable }

        /// <summary>
        /// A file's text, told apart three ways (the independent review's P2-1): Missing only for a file or folder that is not
        /// there; Unavailable for every other failure (a sharing violation, access refused, a bad path), which before read as
        /// "damaged" or "never used" and let an import commit over a removed store. Opened sharing read, write AND delete, so
        /// a reader never makes a commit's atomic replace fail (P3-7).
        /// </summary>
        internal static FileRead TryReadShared(string path, out string text)
        {
            text = null;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    text = reader.ReadToEnd();
                return FileRead.Read;
            }
            catch (FileNotFoundException) { return FileRead.Missing; }
            catch (DirectoryNotFoundException) { return FileRead.Missing; }
            catch (Exception) { return FileRead.Unavailable; }
        }

        private Envelope ReadEnvelope()
        {
            var envelope = new Envelope();
            string path = EnvelopePath;
            string text;
            FileRead read = TryReadShared(path, out text);
            if (read == FileRead.Missing) return envelope;   // never used: revision 0
            envelope.Exists = true;
            if (read == FileRead.Unavailable)
            {
                envelope.Unavailable = true;
                envelope.Revision = -1;
                return envelope;
            }
            try
            {
                var o = JsonNode.Parse(text) as JsonObject;
                long revision;
                if (o == null || o["schema"] == null || o["schema"].GetValue<int>() != Schema ||
                    o["revision"] == null || !long.TryParse(o["revision"].ToString(), out revision))
                {
                    envelope.Damaged = true;
                    envelope.Revision = -1;
                    return envelope;
                }
                envelope.Revision = revision;
                envelope.State = o["state"] == null ? "" : o["state"].GetValue<string>() ?? "";
                envelope.Sealed = o["sealed"] == null ? "" : o["sealed"].GetValue<string>() ?? "";
                if (envelope.State != SavedState && envelope.State != RemovedState) envelope.Damaged = true;
            }
            catch
            {
                envelope.Damaged = true;
                envelope.Revision = -1;
            }
            return envelope;
        }

        // The last sealed value unsealed, and what it held (the independent review's P3-8): every revision check a runner makes
        // reads the store, and unsealing the same blob again each time was wasted work, and a fresh plaintext copy each time.
        // Keyed on the sealed text itself, so a new commit, whatever its revision, is unsealed afresh.
        private readonly object _unsealSync = new object();
        private string _unsealedFrom;
        private HostCredentialState _unsealedState;
        private string _unsealedValue;

        /// <summary>How many times a sealed value was handed to DPAPI: the self-test's view of the cache above.</summary>
        internal int UnsealsForDiagnostics;

        /// <summary>Whether the cache above holds anything (a sealed text or its plaintext): the self-test's proof that a
        /// commit let go of the token it replaced or removed (the 2026-10-29 audit, A-host-05).</summary>
        internal bool HoldsUnsealedForDiagnostics
        {
            get { lock (_unsealSync) return _unsealedFrom != null || _unsealedValue != null; }
        }

        /// <summary>
        /// Let go of the cached plaintext (the 2026-10-29 audit, A-host-05, BACKLOG N-audit-20261029-12). Called by every
        /// commit, a save or an import before the new value is ever read, so the old token is not held beside the new one,
        /// and a removal, after which every read answered None before reaching the cache, so nothing would ever have
        /// replaced the removed token: it stayed referenced by this store, which CompanionHost keeps for the life of the
        /// process, and a process or crash dump taken any time later held it. Called too by a read that finds the store
        /// holding no saved value, which is how another window's Remove reaches this process. A revoked grant is not a
        /// commit here and leaves the cache: a revoke keeps the token saved for the other modules (the Modules pane says
        /// so), and clearing on every refused read would unseal again after each one, the churn P3-8 removed.
        /// </summary>
        private void ForgetUnsealed()
        {
            lock (_unsealSync)
            {
                _unsealedFrom = null;
                _unsealedState = HostCredentialState.None;
                _unsealedValue = null;
            }
        }

        /// <summary>What an envelope holds, and the value when Saved.</summary>
        private HostCredentialState Contents(Envelope envelope, out string value)
        {
            value = null;
            if (envelope.Unavailable) return HostCredentialState.Unavailable;
            if (!envelope.Exists || envelope.Damaged || envelope.State == RemovedState)
            {
                // Nothing saved is held now (never, damaged, or removed, perhaps by another window of the app): the cached
                // plaintext, if any, is of a token the store no longer holds.
                ForgetUnsealed();
                return envelope.Damaged ? HostCredentialState.Unreadable : HostCredentialState.None;
            }
            lock (_unsealSync)
            {
                if (_unsealedFrom != null && string.Equals(_unsealedFrom, envelope.Sealed, StringComparison.Ordinal))
                {
                    value = _unsealedValue;
                    return _unsealedState;
                }
            }
            HostCredentialState state;
            string unsealedValue = null;
            try
            {
                Interlocked.Increment(ref UnsealsForDiagnostics);
                byte[] clear = ProtectedData.Unprotect(Convert.FromBase64String(envelope.Sealed), Entropy, DataProtectionScope.CurrentUser);
                string unsealed = Encoding.UTF8.GetString(clear);
                if (ValueProblem(unsealed) != null) state = HostCredentialState.NotAToken;
                else
                {
                    unsealedValue = unsealed;
                    state = HostCredentialState.Saved;
                }
            }
            catch
            {
                state = HostCredentialState.Unreadable;
            }
            lock (_unsealSync)
            {
                _unsealedFrom = envelope.Sealed;
                _unsealedState = state;
                _unsealedValue = unsealedValue;
            }
            value = unsealedValue;
            return state;
        }

        /// <summary>Whether an import may commit over <paramref name="envelope"/>: the store was never used, or holds nothing
        /// usable (a damaged file, a value this account cannot unseal, or one that is no token). Never after a removal, and
        /// never over a file that could not be read now (Change refuses that before it gets here).</summary>
        private bool AllowsImport(Envelope envelope)
        {
            if (envelope.Unavailable) return false;
            if (!envelope.Exists || envelope.Damaged) return true;
            if (envelope.State == RemovedState) return false;
            string ignored;
            HostCredentialState held = Contents(envelope, out ignored);
            return held == HostCredentialState.Unreadable || held == HostCredentialState.NotAToken;
        }

        /// <summary>The revision after <paramref name="current"/>: the next number, or, when the file was damaged and its
        /// revision unknown, one no reader can already hold (the clock's ticks).</summary>
        private static long NextRevision(Envelope envelope)
        {
            return envelope.Revision >= 0 ? envelope.Revision + 1 : DateTime.UtcNow.Ticks;
        }

        /// <summary>Null when <paramref name="value"/> could be a sign-in token: present, at most
        /// <see cref="MaximumValueCharacters"/>, printable ASCII with no space, not an Anthropic API key, not a web address,
        /// and only RFC 6750's bearer-token characters (letters, digits and - . _ ~ + /, with = at the end). The shared
        /// runner's CodingAgentCli.CheckClaudeToken rule, refusal for refusal (the independent review's P3-9): with a looser
        /// host rule, a value the runner will never send counted as "the store holds one", and a module's migration then
        /// deleted its own valid old token in its favour. The words are the host's; the module's own check comes first.</summary>
        internal static string ValueProblem(string value)
        {
            if (string.IsNullOrEmpty(value)) return "The value is empty.";
            if (value.Length > MaximumValueCharacters) return "The value is longer than a sign-in token can be.";
            foreach (char c in value)
                if (c < '!' || c > '~') return "The value has a space or a character no sign-in token holds.";
            if (value.StartsWith("sk-ant-api", StringComparison.Ordinal)) return "The value is an Anthropic API key, not a sign-in token.";
            if (value.IndexOf("://", StringComparison.Ordinal) >= 0 || value.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                return "The value is a web address, not a sign-in token.";
            int equals = value.Length;
            while (equals > 0 && value[equals - 1] == '=') equals--;
            for (int i = 0; i < equals; i++)
            {
                char c = value[i];
                bool tokenCharacter = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                                      c == '-' || c == '.' || c == '_' || c == '~' || c == '+' || c == '/';
                if (!tokenCharacter) return "The value has a character no sign-in token holds.";
            }
            return null;
        }

        private bool WriteEnvelope(long revision, string state, string sealedValue)
        {
            var o = new JsonObject
            {
                ["schema"] = Schema,
                ["revision"] = revision,
                ["state"] = state,
            };
            if (sealedValue != null) o["sealed"] = sealedValue;
            System.IO.Directory.CreateDirectory(_directory);
            return AtomicFile.TryWriteAllText(EnvelopePath, o.ToJsonString(), null);
        }

        private IDisposable TryLock()
        {
            try
            {
                System.IO.Directory.CreateDirectory(_directory);
                string path = EnvelopePath;
                return CrossSessionLock.TryAcquire(CrossSessionLock.BuildGlobalMutexName("credentials", path), path, LockTimeout);
            }
            catch { return null; }
        }

        private const string Busy = "The sign-in store is busy in another window of the app; try again in a moment.";
        private const string Unreadable = "The sign-in store's file could not be read just now (another program may hold it); try again in a moment.";

        private void Announce(HostCredentialId credential, long revision)
        {
            Action<HostCredentialId, long> committed = Committed;
            if (committed == null) return;
            try { committed(credential, revision); } catch { }
        }

        // ---- the four verbs ----

        internal HostCredentialReading Read(string moduleId, HostCredentialId credential)
        {
            string reason;
            HostCredentialState? refused = Refusal(moduleId, credential, out reason);
            if (refused.HasValue)
            {
                LogRefusal(moduleId, "read", reason);
                return new HostCredentialReading { State = refused.Value, Revision = -1 };
            }
            try
            {
                Envelope envelope = ReadEnvelope();
                string value;
                HostCredentialState state = Contents(envelope, out value);
                if (state == HostCredentialState.Unavailable) LogRefusal(moduleId, "read", "file-unreadable-now");
                return new HostCredentialReading { State = state, Value = value, Revision = envelope.Unavailable ? -1 : envelope.Revision };
            }
            catch (Exception ex)
            {
                Log(moduleId, "read failed: " + ex.GetType().Name);
                return new HostCredentialReading { State = HostCredentialState.Unavailable, Revision = -1 };
            }
        }

        internal HostCredentialCommit TrySave(string moduleId, HostCredentialId credential, string value)
        {
            return Change(moduleId, credential, "save", value, false);
        }

        internal HostCredentialCommit ImportIfUnset(string moduleId, HostCredentialId credential, string value)
        {
            return Change(moduleId, credential, "import", value, true);
        }

        /// <summary>Save or import: one transaction under the lock. An import re-reads the envelope there and commits only if
        /// it still allows one (AllowsImport), the compare-and-set that keeps a slower reader's old token from landing over
        /// another module's newer save or a removal.</summary>
        private HostCredentialCommit Change(string moduleId, HostCredentialId credential, string verb, string value, bool import)
        {
            string reason;
            HostCredentialState? refused = Refusal(moduleId, credential, out reason);
            if (refused.HasValue)
            {
                Log(moduleId, verb + " refused: " + reason);
                return new HostCredentialCommit { Outcome = OutcomeFor(refused.Value), Revision = -1, Problem = "" };
            }
            string problem = ValueProblem(value);
            if (problem != null)
            {
                Log(moduleId, verb + " refused: not-a-token");
                return new HostCredentialCommit { Outcome = HostCredentialOutcome.Refused, Revision = -1, Problem = problem };
            }
            string sealedValue;
            try
            {
                sealedValue = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex)
            {
                Log(moduleId, verb + " failed: dpapi " + ex.GetType().Name);
                return new HostCredentialCommit { Outcome = HostCredentialOutcome.Unavailable, Revision = -1, Problem = "Windows would not encrypt the token (" + ex.GetType().Name + ")." };
            }

            long committed;
            HostCredentialOutcome outcome;
            IDisposable lease = TryLock();
            if (lease == null)
            {
                Log(moduleId, verb + " failed: lock-timeout");
                return new HostCredentialCommit { Outcome = HostCredentialOutcome.Unavailable, Revision = -1, Problem = Busy };
            }
            try
            {
                // The gate again, now the lock is held (the independent review's P3-13): a Revoke made while this waited up to
                // LockTimeout for another window's commit refuses this one too.
                HostCredentialState? late = Refusal(moduleId, credential, out reason);
                if (late.HasValue)
                {
                    Log(moduleId, verb + " refused under the lock: " + reason);
                    return new HostCredentialCommit { Outcome = OutcomeFor(late.Value), Revision = -1, Problem = "" };
                }
                Envelope envelope = ReadEnvelope();
                // A file that could not be read now decides nothing (P2-1): it may hold a removal an import must not undo.
                if (envelope.Unavailable)
                {
                    Log(moduleId, verb + " failed: the store's file could not be read");
                    return new HostCredentialCommit { Outcome = HostCredentialOutcome.Unavailable, Revision = -1, Problem = Unreadable };
                }
                if (import && !AllowsImport(envelope))
                {
                    Log(moduleId, "import not taken: the store is already " + (envelope.State == RemovedState ? "removed" : "set") +
                                  " revision=" + envelope.Revision);
                    return new HostCredentialCommit { Outcome = HostCredentialOutcome.NotImported, Revision = envelope.Revision, Problem = "" };
                }
                committed = NextRevision(envelope);
                if (!WriteEnvelope(committed, SavedState, sealedValue))
                {
                    Log(moduleId, verb + " failed: write");
                    return new HostCredentialCommit { Outcome = HostCredentialOutcome.Unavailable, Revision = envelope.Revision, Problem = "The sign-in token could not be written to the app's data folder." };
                }
                // The token this replaced goes from memory with the commit, before the new one is ever read (A-host-05).
                ForgetUnsealed();
                outcome = import ? HostCredentialOutcome.Imported : HostCredentialOutcome.Saved;
            }
            catch (Exception ex)
            {
                Log(moduleId, verb + " failed: " + ex.GetType().Name);
                return new HostCredentialCommit { Outcome = HostCredentialOutcome.Unavailable, Revision = -1, Problem = "The sign-in token could not be written to the app's data folder." };
            }
            finally { lease.Dispose(); }

            Log(moduleId, (import ? "imported" : "saved") + " claude-code-signin revision=" + committed);
            Announce(credential, committed);
            return new HostCredentialCommit { Outcome = outcome, Revision = committed, Problem = "" };
        }

        /// <summary>Remove: one transaction under the lock. A store holding anything, usable or not, is replaced by a "removed"
        /// envelope (Removed, announced), which closes it to imports; a store already removed, or never used, is left alone
        /// (NothingToRemove, nothing written, nothing announced).</summary>
        internal HostCredentialCommit Remove(string moduleId, HostCredentialId credential)
        {
            string reason;
            HostCredentialState? refused = Refusal(moduleId, credential, out reason);
            if (refused.HasValue)
            {
                Log(moduleId, "remove refused: " + reason);
                return new HostCredentialCommit { Outcome = OutcomeFor(refused.Value), Revision = -1, Problem = "" };
            }
            IDisposable lease = TryLock();
            if (lease == null)
            {
                Log(moduleId, "remove failed: lock-timeout");
                return new HostCredentialCommit { Outcome = HostCredentialOutcome.Unavailable, Revision = -1, Problem = Busy };
            }
            long committed;
            HostCredentialOutcome outcome;
            try
            {
                HostCredentialState? late = Refusal(moduleId, credential, out reason);
                if (late.HasValue)
                {
                    Log(moduleId, "remove refused under the lock: " + reason);
                    return new HostCredentialCommit { Outcome = OutcomeFor(late.Value), Revision = -1, Problem = "" };
                }
                Envelope envelope = ReadEnvelope();
                if (envelope.Unavailable)
                {
                    Log(moduleId, "remove failed: the store's file could not be read");
                    return new HostCredentialCommit { Outcome = HostCredentialOutcome.Unavailable, Revision = -1, Problem = Unreadable };
                }
                if (envelope.Exists && !envelope.Damaged && envelope.State == RemovedState)
                {
                    // Removed already, perhaps by another window of the app: whatever this process still held of it goes.
                    ForgetUnsealed();
                    Log(moduleId, "remove: nothing to remove revision=" + envelope.Revision);
                    return new HostCredentialCommit { Outcome = HostCredentialOutcome.NothingToRemove, Revision = envelope.Revision, Problem = "" };
                }
                // A STORE NEVER USED is left as it is (BACKLOG N-audit-runner-02, moving the shared runner's rule,
                // N-audit-20261029-01, into the host so every caller of the ABI gets it). This wrote "removed" over it, which
                // closed the store to imports, so another module's valid old token, still waiting to move in, was refused at
                // that module's next use and deleted, though this Remove removed nothing. The first design decided the
                // opposite ("so no module's old copy can be imported after the user removed the sign-in through another"),
                // but with no file there is no shared sign-in to remove: the old copies are each module's own, removed by
                // that module's Remove token, and closing the store is for a sign-in the user removed. The runner keeps its
                // own guard as a belt for a host before this one. Revision 0, as a reading of the store says.
                if (!envelope.Exists)
                {
                    Log(moduleId, "remove: nothing to remove, the store was never used and is left open");
                    return new HostCredentialCommit { Outcome = HostCredentialOutcome.NothingToRemove, Revision = 0, Problem = "" };
                }
                // Past the two tests above the file holds something, usable or not (a saved value, one this account cannot
                // unseal, one that is no token, or a damaged file), so the answer is Removed (P3-14); it used to unseal the
                // value only to learn that, and to call a never-used store NothingToRemove.
                committed = NextRevision(envelope);
                if (!WriteEnvelope(committed, RemovedState, null))
                {
                    Log(moduleId, "remove failed: write");
                    return new HostCredentialCommit { Outcome = HostCredentialOutcome.Unavailable, Revision = envelope.Revision, Problem = "The sign-in store in the app's data folder could not be written." };
                }
                // The removed token's plaintext goes with the commit (A-host-05): no later read reaches the cache to replace
                // it, so without this it stayed in this process for good.
                ForgetUnsealed();
                outcome = HostCredentialOutcome.Removed;
            }
            catch (Exception ex)
            {
                Log(moduleId, "remove failed: " + ex.GetType().Name);
                return new HostCredentialCommit { Outcome = HostCredentialOutcome.Unavailable, Revision = -1, Problem = "The sign-in store in the app's data folder could not be written." };
            }
            finally { lease.Dispose(); }

            Log(moduleId, "removed claude-code-signin revision=" + committed);
            Announce(credential, committed);
            return new HostCredentialCommit { Outcome = outcome, Revision = committed, Problem = "" };
        }

        // ---- the grants ----
        //
        // grants.json: {"schema":1,"claudeCodeSignIn":["aibrain","remembrance"]}, module ids compared ignoring case as the
        // loader compares them. Written only by the host (the Modules pane, and an uninstall's removal); a module has no verb
        // that reaches it. Read on every gate, never cached, so an Allow or a Revoke counts from the next call.

        private static string GrantsPath(string directory) { return Path.Combine(directory ?? "", GrantsFileName); }

        // Beside the grants, since the start question (SignInGrantQuestion), the modules the user ANSWERED no for: "Don't
        // ask again" on that question, or Revoke sign-in on the Modules row. A refusal recorded, never a grant: the store
        // reads only the grants, and this list only decides whether the start question is asked.
        private const string NotAskedMember = "claudeCodeSignInNotAsked";

        /// <summary>
        /// Both lists of grants.json, told apart three ways (the independent review's P2-1 and P3-15): no file is an empty
        /// record; a file that could not be read now, or that is damaged, is NO answer at all (false), never an empty list.
        /// An empty list read the gate as "not allowed", so a module with no old token ran on Claude Code's own sign-in, and
        /// the next Record wrote the file back from empty, dropping every other module's grant and recorded no.
        /// </summary>
        private static bool TryReadAnswers(string directory, out List<string> grants, out List<string> notAsked)
        {
            grants = new List<string>();
            notAsked = new List<string>();
            string text;
            FileRead read = TryReadShared(GrantsPath(directory), out text);
            if (read == FileRead.Missing) return true;
            if (read == FileRead.Unavailable) return false;
            try
            {
                var o = JsonNode.Parse(text) as JsonObject;
                if (o == null || o["schema"] == null || o["schema"].GetValue<int>() != Schema) return false;
                return ReadIds(o[GrantsMember], grants) && ReadIds(o[NotAskedMember], notAsked);
            }
            catch { return false; }
        }

        private static bool ReadIds(JsonNode node, List<string> ids)
        {
            if (node == null) return true;          // a record written before this list existed
            var list = node as JsonArray;
            if (list == null) return false;
            foreach (JsonNode item in list)
            {
                string id = item == null ? "" : (item.GetValue<string>() ?? "").Trim();
                if (id.Length > 0) ids.Add(id);
            }
            return true;
        }

        private static bool Contains(List<string> ids, string id)
        {
            foreach (string listed in ids)
                if (string.Equals(listed, id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Whether the user has allowed <paramref name="moduleId"/> the shared Claude Code sign-in; false too when the
        /// record could not be read, which only the gate (the overload below) tells apart.</summary>
        internal static bool IsGranted(string directory, string moduleId)
        {
            bool readable;
            return IsGranted(directory, moduleId, out readable);
        }

        /// <param name="readable">False when grants.json is there and could not be read now, or is damaged: no answer.</param>
        internal static bool IsGranted(string directory, string moduleId, out bool readable)
        {
            readable = true;
            string id = (moduleId ?? "").Trim();
            if (id.Length == 0 || string.IsNullOrWhiteSpace(directory)) return false;
            List<string> grants, notAsked;
            readable = TryReadAnswers(directory, out grants, out notAsked);
            return readable && Contains(grants, id);
        }

        /// <summary>Whether the user answered no for <paramref name="moduleId"/> (Don't ask again, or Revoke sign-in), so the
        /// start question is not asked for it.</summary>
        internal static bool IsNotAsked(string directory, string moduleId)
        {
            string id = (moduleId ?? "").Trim();
            if (id.Length == 0 || string.IsNullOrWhiteSpace(directory)) return false;
            List<string> grants, notAsked;
            return TryReadAnswers(directory, out grants, out notAsked) && Contains(notAsked, id);
        }

        /// <summary>Whether grants.json can be read now (missing counts): the start question asks nothing while it cannot.</summary>
        internal static bool AnswersReadable(string directory)
        {
            List<string> grants, notAsked;
            return !string.IsNullOrWhiteSpace(directory) && TryReadAnswers(directory, out grants, out notAsked);
        }

        /// <summary>Record the user's Allow (<paramref name="allowed"/> true) or Revoke for one module; false when the record
        /// could not be written. A Revoke is an answer, so the start question is not asked for that module again; an Allow
        /// clears that. Under its own lock, so two windows of the app cannot lose each other's change.</summary>
        internal static bool SetGrant(string directory, string moduleId, bool allowed, Action<string, string> log)
        {
            bool recorded = Record(directory, moduleId, allowed, !allowed, log,
                "claude-code-signin " + (allowed ? "allowed" : "no longer allowed") + " by the user");
            if (recorded && allowed) AnnounceGrant(directory, (moduleId ?? "").Trim());
            return recorded;
        }

        /// <summary>The start question's "Don't ask again": no grant, and the question is not asked for it again.</summary>
        internal static bool SetNotAsked(string directory, string moduleId, Action<string, string> log)
        {
            return Record(directory, moduleId, false, true, log, "claude-code-signin not allowed, and not to be asked again, by the user");
        }

        /// <summary>An uninstall applied: the module's grant AND its recorded no go, so a module installed later under the
        /// same id is asked afresh.</summary>
        internal static bool ForgetGrant(string directory, string moduleId, Action<string, string> log)
        {
            return Record(directory, moduleId, false, false, log, "claude-code-signin answer forgotten");
        }

        /// <summary>How many tries a grants write gets, RecordRetryDelay apart (the independent review's P3-7: a write that
        /// meets a passing reader, or an indexer, fails once and lands on the next try).</summary>
        internal const int RecordAttempts = 3;
        internal static readonly TimeSpan RecordRetryDelay = TimeSpan.FromMilliseconds(100);

        private static bool Record(string directory, string moduleId, bool granted, bool notAsked, Action<string, string> log, string line)
        {
            string id = (moduleId ?? "").Trim();
            if (id.Length == 0 || string.IsNullOrWhiteSpace(directory)) return false;
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                string path = GrantsPath(directory);
                using (IDisposable lease = CrossSessionLock.TryAcquire(CrossSessionLock.BuildGlobalMutexName("credential-grants", path), path, LockTimeoutMilliseconds))
                {
                    if (lease == null) return false;
                    List<string> grants, declined;
                    // A record that cannot be read now, or is damaged, is kept as it is and the answer refused: rewriting it
                    // from what could be read would drop every other module's answer (P3-15).
                    if (!TryReadAnswers(directory, out grants, out declined))
                    {
                        if (log != null) try { log(id, "credentials: the answer was not recorded: grants.json could not be read"); } catch { }
                        return false;
                    }
                    Predicate<string> same = delegate (string g) { return string.Equals(g, id, StringComparison.OrdinalIgnoreCase); };
                    grants.RemoveAll(same);
                    declined.RemoveAll(same);
                    if (granted) grants.Add(id.ToLowerInvariant());
                    if (notAsked) declined.Add(id.ToLowerInvariant());
                    var grantList = new JsonArray();
                    foreach (string g in grants) grantList.Add(JsonValue.Create(g));
                    var declinedList = new JsonArray();
                    foreach (string g in declined) declinedList.Add(JsonValue.Create(g));
                    var o = new JsonObject { ["schema"] = Schema, [GrantsMember] = grantList, [NotAskedMember] = declinedList };
                    string json = o.ToJsonString();
                    bool written = false;
                    for (int attempt = 1; attempt <= RecordAttempts && !written; attempt++)
                    {
                        written = AtomicFile.TryWriteAllText(path, json, null);
                        if (!written && attempt < RecordAttempts) Thread.Sleep(RecordRetryDelay);
                    }
                    if (!written) return false;
                }
                if (log != null) try { log(id, "credentials: " + line); } catch { }
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// The question the app asks at start for a module that declares ModulePermissions.ClaudeCodeSignIn and holds no
    /// recorded answer (host 1.8.0; the coordinator's finding of 2026-10-29). The MSI installs AI Brain and Remembrance
    /// itself, and a module placed in the modules folder by hand loads the same way: neither ever passes the Modules pane's
    /// update question, the one other place a grant was given, so an upgrade from 1.7.1 left both modules with their old
    /// token waiting and no grant, and their Claude Code calls stopped. Asked through the host's own question card
    /// (ITrayQuestions' queue, attributed to the module by the host's record of it), once per module per start, and only
    /// the user's press of Allow grants: a timeout, a held card that expired, a withdrawal, the app closing or a refused
    /// question records nothing, and the question comes back at the next start. "Don't ask again" records a no, as Revoke
    /// sign-in on the Modules row does; that row stays the place to change either answer.
    /// </summary>
    internal static class SignInGrantQuestion
    {
        internal const string Key = "claude-code-signin.grant";
        internal const string AllowChoice = "allow";
        internal const string NotNowChoice = "not-now";
        internal const string NeverChoice = "never";

        /// <summary>How long the card waits while it can be seen. Not RunsWhileHeld: a grant must be a press the user saw.</summary>
        internal static readonly TimeSpan Wait = TimeSpan.FromMinutes(10);

        /// <summary>The loaded modules the start question is for: each declares ClaudeCodeSignIn, holds no grant and has no
        /// recorded no, in load order, ids once.</summary>
        internal static List<string> ModulesToAsk(IEnumerable<ModuleInfo> loaded, string directory)
        {
            var ask = new List<string>();
            // Nothing while grants.json cannot be read: every answer the user gave could be in it.
            if (loaded == null || !HostCredentialStore.AnswersReadable(directory)) return ask;
            foreach (ModuleInfo info in loaded)
            {
                if (info == null || string.IsNullOrWhiteSpace(info.Id)) continue;
                if ((info.Permissions & ModulePermissions.ClaudeCodeSignIn) != ModulePermissions.ClaudeCodeSignIn) continue;
                string id = info.Id.Trim();
                if (HostCredentialStore.IsGranted(directory, id) || HostCredentialStore.IsNotAsked(directory, id)) continue;
                bool listed = false;
                foreach (string a in ask) if (string.Equals(a, id, StringComparison.OrdinalIgnoreCase)) listed = true;
                if (!listed) ask.Add(id);
            }
            return ask;
        }

        /// <summary>How the question names a module (the independent review's P3-5): its name AND its id, since the name is the
        /// module's own choice and the id is what the grant is recorded under, shortened with an ellipsis so the text always
        /// fits the 240 characters the card takes. A long name used to make the question refused, and the module never asked.</summary>
        internal static string Who(string displayName, string moduleId, int room)
        {
            string id = (moduleId ?? "").Trim();
            string name = string.IsNullOrWhiteSpace(displayName) ? id : displayName.Trim();
            string idPart = name.Equals(id, StringComparison.OrdinalIgnoreCase) || id.Length == 0 ? "" : " (" + id + ")";
            string who = name + idPart;
            if (who.Length <= room) return who;
            if (idPart.Length + 2 > room) return who.Substring(0, Math.Max(1, room - 1)) + "…";
            return name.Substring(0, Math.Max(1, room - idPart.Length - 1)) + "…" + idPart;
        }

        /// <summary>The question for one module, named as the host records it, with its id.</summary>
        internal static TrayQuestion For(string displayName, string moduleId)
        {
            int room = TrayQuestionRules.MaxText - (ModulePermissionConsent.StartQuestionText("x").Length - 1);
            return new TrayQuestion
            {
                Key = Key,
                Title = "Shared Claude Code sign-in",
                Text = ModulePermissionConsent.StartQuestionText(Who(displayName, moduleId, room)),
                Choices = new[]
                {
                    new TrayChoice(AllowChoice, "Allow"),
                    new TrayChoice(NotNowChoice, "Not now"),
                    new TrayChoice(NeverChoice, "Don't ask again"),
                },
                TimeoutChoiceId = NotNowChoice,
                Timeout = Wait,
                RunsWhileHeld = false,
            };
        }

        /// <summary>Act on the answer: the grant only for the user's press of Allow, the recorded no only for their press of
        /// Don't ask again, and nothing for anything else. Returns the word logged.</summary>
        internal static string Apply(string directory, string moduleId, TrayAnswer answer, Action<string, string> log)
        {
            string word;
            if (answer == null || answer.Outcome != TrayQuestionOutcome.Chosen) word = "unanswered";
            else if (string.Equals(answer.ChoiceId, AllowChoice, StringComparison.Ordinal))
                word = HostCredentialStore.SetGrant(directory, moduleId, true, log) ? "allowed" : NotRecorded;
            else if (string.Equals(answer.ChoiceId, NeverChoice, StringComparison.Ordinal))
                word = HostCredentialStore.SetNotAsked(directory, moduleId, log) ? "not allowed, not to be asked again" : "not recorded";
            else word = "not now";
            if (log != null)
                try { log(moduleId, "credentials: the start question for the shared Claude Code sign-in ended " + (answer == null ? "with no answer" : answer.Outcome.ToString()) + ": " + word); } catch { }
            return word;
        }

        // THE ALLOW THAT WAS NOT RECORDED (the coordinator's condition for accepting the review's P3-7): when the user pressed
        // Allow and the grant could not be written, the card has gone, so a second card says so and offers to try again, once.
        // Only its press of Try again records anything; anything else leaves the start question for the next start.
        internal const string RetryKey = "claude-code-signin.grant-retry";
        internal const string RetryChoice = "retry";
        internal const string NotRecorded = "allowed, not recorded";

        internal static TrayQuestion RetryFor(string displayName, string moduleId)
        {
            int room = TrayQuestionRules.MaxText - (ModulePermissionConsent.RetryQuestionText("x").Length - 1);
            return new TrayQuestion
            {
                Key = RetryKey,
                Title = "Shared Claude Code sign-in",
                Text = ModulePermissionConsent.RetryQuestionText(Who(displayName, moduleId, room)),
                Choices = new[] { new TrayChoice(RetryChoice, "Try again"), new TrayChoice(NotNowChoice, "Not now") },
                TimeoutChoiceId = NotNowChoice,
                Timeout = Wait,
                RunsWhileHeld = false,
            };
        }

        /// <summary>The second card's answer: the grant only for the user's press of Try again. Returns the word logged.</summary>
        internal static string ApplyRetry(string directory, string moduleId, TrayAnswer answer, Action<string, string> log)
        {
            string word;
            if (answer == null || answer.Outcome != TrayQuestionOutcome.Chosen || !string.Equals(answer.ChoiceId, RetryChoice, StringComparison.Ordinal))
                word = "not tried again";
            else word = HostCredentialStore.SetGrant(directory, moduleId, true, log) ? "allowed" : NotRecorded;
            if (log != null)
                try { log(moduleId, "credentials: the retry of the shared Claude Code sign-in's Allow ended: " + word); } catch { }
            return word;
        }
    }
}
