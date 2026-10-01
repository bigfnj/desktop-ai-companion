using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace DesktopAICompanion.ModuleKit
{
    /// <summary>
    /// A module's own settings file: JSON on disk, written durably and safe against a second session writing
    /// at the same time.
    ///
    /// The host's <see cref="Modules.IModuleSettings"/> already covers flat string/int/bool keys and is the
    /// right choice for a settings PANE. Reach for this instead when a module owns structured state the pane
    /// schema cannot express — lists, nested objects, a schema version to migrate. It is the durable-write
    /// core distilled out of the AI brain's settings store (<see cref="AtomicFile"/> +
    /// <see cref="CrossSessionLock"/>), without that module's DPAPI/credential machinery, which stays
    /// module-specific by design.
    ///
    /// Load never throws: a missing, empty, or corrupt file yields a fresh <typeparamref name="T"/>, because
    /// a module that cannot read its settings should start at defaults rather than take the pet down.
    /// </summary>
    /// <typeparam name="T">The settings document. Needs a public parameterless constructor.</typeparam>
    public class JsonSettingsStore<T> where T : class, new()
    {
        private const int LockTimeoutMilliseconds = 3000;

        private readonly string _path;
        private readonly string _lockCategory;
        private readonly object _processLock = new object();

        /// <param name="path">Full path to the JSON file (e.g. paths.File("settings.json")).</param>
        /// <param name="lockCategory">Short name separating this file's lock from unrelated ones.</param>
        public JsonSettingsStore(string path, string lockCategory)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.", "path");
            _path = Path.GetFullPath(path);
            _lockCategory = string.IsNullOrWhiteSpace(lockCategory) ? "modulesettings" : lockCategory;
        }

        public string Path_ { get { return _path; } }

        /// <summary>The path of the backup <see cref="Save"/> keeps: the previous document, so one bad write
        /// is recoverable by hand, as the two stores this class was distilled from already did (F230).</summary>
        public string BackupPath_ { get { return _path + ".bak"; } }

        /// <summary>True when the last <see cref="Load"/> found a file it could not read and answered
        /// defaults: corrupt, not JSON, or not readable at that moment (held open without sharing by another
        /// process, an I/O error). The last case is deliberate (RA-210, 2026-10-01): a document this process could not
        /// SEE is one it must not write defaults over either, so <see cref="Update"/> refuses all three
        /// alike, and a later <see cref="Load"/> that succeeds clears the flag.</summary>
        public bool LastLoadWasUnreadable { get; private set; }

        private enum ReadResult { Missing, Loaded, Unreadable }

        // ---- the cross-session lease, re-entrant for the thread that holds it (RA-212) ----
        // CrossSessionLock's lease is a Global mutex (recursive for its owning thread) AND a FileShare.None
        // handle on <path>.lock, which is not, while _processLock is a Monitor, which is. So until 2026-10-01
        // a mutate passed to Update that called Load, Save or Update on the SAME store re-entered the monitor,
        // then waited the full 3 s for a file lease its own thread was holding and failed: Save answered false
        // in silence, Load fell back to a stale read. The owning thread now reuses the lease it holds; the
        // depth count releases the real lease when the outermost scope ends. Both fields are only ever touched
        // under _processLock, which is what makes a plain int safe here.
        private int _leaseOwnerThread;
        private int _leaseDepth;

        private sealed class Lease : IDisposable
        {
            private JsonSettingsStore<T> _store;
            private readonly IDisposable _inner;
            public Lease(JsonSettingsStore<T> store, IDisposable inner) { _store = store; _inner = inner; }
            public void Dispose()
            {
                JsonSettingsStore<T> store = _store;
                _store = null;
                if (store == null) return;
                store._leaseDepth--;
                if (store._leaseDepth > 0) return;
                store._leaseOwnerThread = 0;
                if (_inner != null) _inner.Dispose();
            }
        }

        /// <summary>The lease for one Load/Save/Update, or null when it could not be taken. Caller holds
        /// _processLock. A thread already holding the lease gets a nested scope at once.</summary>
        private IDisposable TryLease()
        {
            int me = Thread.CurrentThread.ManagedThreadId;
            if (_leaseDepth > 0 && _leaseOwnerThread == me) { _leaseDepth++; return new Lease(this, null); }
            IDisposable inner = CrossSessionLock.TryAcquire(MutexName(), _path, LockTimeoutMilliseconds);
            if (inner == null) return null;
            _leaseOwnerThread = me;
            _leaseDepth = 1;
            return new Lease(this, inner);
        }

        /// <summary>Read the document, or a default-constructed one when the file is absent or unreadable.
        /// Unknown JSON properties are preserved for a round-trip only if <typeparamref name="T"/> carries a
        /// [JsonExtensionData] member — the trick this codebase uses to migrate a retired field.</summary>
        public T Load()
        {
            lock (_processLock)
            {
                T value;
                using (TryLease())
                {
                    // A read still proceeds if the lock timed out (null lease): a stale reader is far
                    // better than refusing to start.
                    ReadResult result = TryRead(out value);
                    LastLoadWasUnreadable = result == ReadResult.Unreadable;
                }
                return value ?? new T();
            }
        }

        /// <summary>The read itself, with the verdict Load's contract hides: Missing and Loaded may be written
        /// over, Unreadable must not be (F230). Takes no lease; the caller holds one.</summary>
        private ReadResult TryRead(out T value)
        {
            value = null;
            try
            {
                if (!File.Exists(_path)) { value = new T(); return ReadResult.Missing; }
                string json = File.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(json)) { value = new T(); return ReadResult.Loaded; }
                T loaded = JsonSerializer.Deserialize<T>(json.TrimStart('﻿'), ReadOptions());
                if (loaded == null) return ReadResult.Unreadable;
                value = loaded;
                return ReadResult.Loaded;
            }
            catch
            {
                value = new T();
                return ReadResult.Unreadable;
            }
        }

        /// <summary>Write the document durably, keeping the previous one at <see cref="BackupPath_"/>. Returns
        /// false instead of throwing, so a failed save can be surfaced in the UI without unwinding the caller.
        /// Save writes WHATEVER it is given, including over a document the last <see cref="Load"/> could not
        /// read (RA-211): it is the module's explicit "write this", the verb behind a deliberate reset, and
        /// the next Save then rotates the unreadable original out of <see cref="BackupPath_"/>. The verb that
        /// refuses to write over an unreadable document is <see cref="Update"/>, because that one derives
        /// what it writes from what it read.</summary>
        public bool Save(T value)
        {
            if (value == null) return false;
            lock (_processLock)
            {
                try
                {
                    using (IDisposable lease = TryLease())
                    {
                        // Unlike a read, a write without the lease is a corruption risk, so refuse it.
                        if (lease == null) return false;
                        return SaveCore(value);
                    }
                }
                catch
                {
                    return false;
                }
            }
        }

        private bool SaveCore(T value)
        {
            string json = JsonSerializer.Serialize(value, WriteOptions());
            return AtomicFile.TryWriteAllText(_path, json, BackupPath_);
        }

        /// <summary>
        /// Load, mutate, and save under ONE cross-session lease (F231): Load and Save each took their own, so a
        /// second instance could write between them and lose to this one's Save. Returns false when the lease
        /// could not be taken, when the file exists but could not be read (F230: writing defaults over a
        /// document that is merely unreadable to this build is how a setting vanishes), when the mutation
        /// threw, or when the save failed. The lease is held across <paramref name="mutate"/>, so keep it short.
        /// Calling Load, Save or Update on this same store from inside <paramref name="mutate"/> is allowed
        /// (RA-212): the thread that holds the lease reuses it, a nested Load reads the document as it was
        /// before the mutation, and a nested Save is written and then overwritten by this Update's own save,
        /// which carries the mutated document and wins.
        /// </summary>
        public bool Update(Action<T> mutate)
        {
            if (mutate == null) return false;
            lock (_processLock)
            {
                try
                {
                    using (IDisposable lease = TryLease())
                    {
                        if (lease == null) return false;
                        T current;
                        ReadResult result = TryRead(out current);
                        LastLoadWasUnreadable = result == ReadResult.Unreadable;
                        if (result == ReadResult.Unreadable) return false;
                        try { mutate(current); }
                        catch { return false; }
                        return SaveCore(current);
                    }
                }
                catch
                {
                    return false;
                }
            }
        }

        private string MutexName() { return CrossSessionLock.BuildGlobalMutexName(_lockCategory, _path); }

        private static JsonSerializerOptions ReadOptions()
        {
            return new JsonSerializerOptions
            {
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                PropertyNameCaseInsensitive = true,
            };
        }

        private static JsonSerializerOptions WriteOptions()
        {
            return new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
        }
    }
}
