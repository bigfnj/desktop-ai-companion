using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
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

        /// <summary>Called once per committed change (Saved, Imported, Removed, and the NothingToRemove that closed a store
        /// never used), after the lock is released, with the credential and the revision now current. Never with a value.</summary>
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
            if (!IsGranted(_directory, moduleId)) { reason = "not-allowed"; return HostCredentialState.ConsentRequired; }
            return null;
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
            internal bool Damaged;
            internal long Revision;
            internal string State = "";
            internal string Sealed = "";
        }

        private Envelope ReadEnvelope()
        {
            var envelope = new Envelope();
            string path = EnvelopePath;
            if (!File.Exists(path)) return envelope;   // never used: revision 0
            envelope.Exists = true;
            try
            {
                var o = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
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

        /// <summary>What an envelope holds, and the value when Saved.</summary>
        private static HostCredentialState Contents(Envelope envelope, out string value)
        {
            value = null;
            if (!envelope.Exists) return HostCredentialState.None;
            if (envelope.Damaged) return HostCredentialState.Unreadable;
            if (envelope.State == RemovedState) return HostCredentialState.None;
            try
            {
                byte[] clear = ProtectedData.Unprotect(Convert.FromBase64String(envelope.Sealed), Entropy, DataProtectionScope.CurrentUser);
                string unsealed = Encoding.UTF8.GetString(clear);
                if (ValueProblem(unsealed) != null) return HostCredentialState.NotAToken;
                value = unsealed;
                return HostCredentialState.Saved;
            }
            catch
            {
                return HostCredentialState.Unreadable;
            }
        }

        /// <summary>Whether an import may commit over <paramref name="envelope"/>: the store was never used, or holds nothing
        /// usable (a damaged file, a value this account cannot unseal, or one that is no token). Never after a removal.</summary>
        private static bool AllowsImport(Envelope envelope)
        {
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

        /// <summary>Null when <paramref name="value"/> could be a sign-in token as the HOST judges one: present, at most
        /// <see cref="MaximumValueCharacters"/>, and printable ASCII with no space. The module's own, stricter check
        /// (CodingAgentCli.CheckClaudeToken) runs before it ever calls.</summary>
        internal static string ValueProblem(string value)
        {
            if (string.IsNullOrEmpty(value)) return "The value is empty.";
            if (value.Length > MaximumValueCharacters) return "The value is longer than a sign-in token can be.";
            foreach (char c in value)
                if (c < '!' || c > '~') return "The value has a space or a character no sign-in token holds.";
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
                Log(moduleId, "read refused: " + reason);
                return new HostCredentialReading { State = refused.Value, Revision = -1 };
            }
            try
            {
                Envelope envelope = ReadEnvelope();
                string value;
                HostCredentialState state = Contents(envelope, out value);
                return new HostCredentialReading { State = state, Value = value, Revision = envelope.Revision };
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
                Envelope envelope = ReadEnvelope();
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

        /// <summary>Remove: one transaction under the lock. A store holding anything is replaced by a "removed" envelope; a
        /// store never used gets one too (NothingToRemove, and the change is announced), so no module's old copy can be
        /// imported after the user removed the sign-in through another; a store already removed is left alone.</summary>
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
                Envelope envelope = ReadEnvelope();
                if (envelope.Exists && !envelope.Damaged && envelope.State == RemovedState)
                {
                    Log(moduleId, "remove: nothing to remove revision=" + envelope.Revision);
                    return new HostCredentialCommit { Outcome = HostCredentialOutcome.NothingToRemove, Revision = envelope.Revision, Problem = "" };
                }
                string ignored;
                bool held = envelope.Exists && Contents(envelope, out ignored) != HostCredentialState.None;
                committed = NextRevision(envelope);
                if (!WriteEnvelope(committed, RemovedState, null))
                {
                    Log(moduleId, "remove failed: write");
                    return new HostCredentialCommit { Outcome = HostCredentialOutcome.Unavailable, Revision = envelope.Revision, Problem = "The sign-in store in the app's data folder could not be written." };
                }
                outcome = held ? HostCredentialOutcome.Removed : HostCredentialOutcome.NothingToRemove;
            }
            catch (Exception ex)
            {
                Log(moduleId, "remove failed: " + ex.GetType().Name);
                return new HostCredentialCommit { Outcome = HostCredentialOutcome.Unavailable, Revision = -1, Problem = "The sign-in store in the app's data folder could not be written." };
            }
            finally { lease.Dispose(); }

            Log(moduleId, (outcome == HostCredentialOutcome.Removed ? "removed" : "closed, nothing to remove") + " claude-code-signin revision=" + committed);
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

        private static List<string> ReadList(string directory, string member)
        {
            var ids = new List<string>();
            try
            {
                string path = GrantsPath(directory);
                if (!File.Exists(path)) return ids;
                var o = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                var list = o == null ? null : o[member] as JsonArray;
                if (list == null) return ids;
                foreach (JsonNode item in list)
                {
                    string id = item == null ? "" : (item.GetValue<string>() ?? "").Trim();
                    if (id.Length > 0) ids.Add(id);
                }
            }
            catch { ids.Clear(); }   // a damaged record grants nothing
            return ids;
        }

        private static List<string> ReadGrants(string directory)
        {
            return ReadList(directory, GrantsMember);
        }

        private static bool Contains(List<string> ids, string id)
        {
            foreach (string listed in ids)
                if (string.Equals(listed, id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Whether the user has allowed <paramref name="moduleId"/> the shared Claude Code sign-in.</summary>
        internal static bool IsGranted(string directory, string moduleId)
        {
            string id = (moduleId ?? "").Trim();
            if (id.Length == 0 || string.IsNullOrWhiteSpace(directory)) return false;
            return Contains(ReadGrants(directory), id);
        }

        /// <summary>Whether the user answered no for <paramref name="moduleId"/> (Don't ask again, or Revoke sign-in), so the
        /// start question is not asked for it.</summary>
        internal static bool IsNotAsked(string directory, string moduleId)
        {
            string id = (moduleId ?? "").Trim();
            if (id.Length == 0 || string.IsNullOrWhiteSpace(directory)) return false;
            return Contains(ReadList(directory, NotAskedMember), id);
        }

        /// <summary>Record the user's Allow (<paramref name="allowed"/> true) or Revoke for one module; false when the record
        /// could not be written. A Revoke is an answer, so the start question is not asked for that module again; an Allow
        /// clears that. Under its own lock, so two windows of the app cannot lose each other's change.</summary>
        internal static bool SetGrant(string directory, string moduleId, bool allowed, Action<string, string> log)
        {
            return Record(directory, moduleId, allowed, !allowed, log,
                "claude-code-signin " + (allowed ? "allowed" : "no longer allowed") + " by the user");
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
                    List<string> grants = ReadGrants(directory);
                    List<string> declined = ReadList(directory, NotAskedMember);
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
                    if (!AtomicFile.TryWriteAllText(path, o.ToJsonString(), null)) return false;
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
            if (loaded == null) return ask;
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

        /// <summary>The question for one module, named as the host records it.</summary>
        internal static TrayQuestion For(string displayName)
        {
            return new TrayQuestion
            {
                Key = Key,
                Title = "Shared Claude Code sign-in",
                Text = ModulePermissionConsent.StartQuestionText(displayName),
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
                word = HostCredentialStore.SetGrant(directory, moduleId, true, log) ? "allowed" : "allowed, not recorded";
            else if (string.Equals(answer.ChoiceId, NeverChoice, StringComparison.Ordinal))
                word = HostCredentialStore.SetNotAsked(directory, moduleId, log) ? "not allowed, not to be asked again" : "not recorded";
            else word = "not now";
            if (log != null)
                try { log(moduleId, "credentials: the start question for the shared Claude Code sign-in ended " + (answer == null ? "with no answer" : answer.Outcome.ToString()) + ": " + word); } catch { }
            return word;
        }
    }
}
