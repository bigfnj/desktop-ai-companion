using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

        /// <summary>Read the document, or a default-constructed one when the file is absent or unreadable.
        /// Unknown JSON properties are preserved for a round-trip only if <typeparamref name="T"/> carries a
        /// [JsonExtensionData] member — the trick this codebase uses to migrate a retired field.</summary>
        public T Load()
        {
            lock (_processLock)
            {
                try
                {
                    if (!File.Exists(_path)) return new T();
                    string json;
                    using (CrossSessionLock.TryAcquire(MutexName(), _path, LockTimeoutMilliseconds))
                    {
                        // A read still proceeds if the lock timed out (null lease): a stale reader is far
                        // better than refusing to start.
                        json = File.ReadAllText(_path);
                    }
                    if (string.IsNullOrWhiteSpace(json)) return new T();
                    T loaded = JsonSerializer.Deserialize<T>(json.TrimStart('﻿'), ReadOptions());
                    return loaded ?? new T();
                }
                catch
                {
                    return new T();
                }
            }
        }

        /// <summary>Write the document durably. Returns false instead of throwing, so a failed save can be
        /// surfaced in the UI without unwinding the caller.</summary>
        public bool Save(T value)
        {
            if (value == null) return false;
            lock (_processLock)
            {
                try
                {
                    string json = JsonSerializer.Serialize(value, WriteOptions());
                    using (IDisposable lease = CrossSessionLock.TryAcquire(MutexName(), _path, LockTimeoutMilliseconds))
                    {
                        // Unlike a read, a write without the lease is a corruption risk, so refuse it.
                        if (lease == null) return false;
                        return AtomicFile.TryWriteAllText(_path, json, null);
                    }
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>Load, mutate, and save in one locked step. Returns false when the save failed.</summary>
        public bool Update(Action<T> mutate)
        {
            if (mutate == null) return false;
            lock (_processLock)
            {
                T current = Load();
                try { mutate(current); }
                catch { return false; }
                return Save(current);
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
