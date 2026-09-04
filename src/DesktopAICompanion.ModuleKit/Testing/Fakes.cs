using System;
using System.Collections.Generic;
using System.IO;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.ModuleKit.Testing
{
    /// <summary>An <see cref="IModuleSettings"/> backed by a dictionary — no disk, no host.</summary>
    public sealed class FakeModuleSettings : IModuleSettings
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>How many times Save() was called, so a test can assert a pane actually persisted.</summary>
        public int SaveCount { get; private set; }

        /// <summary>Make Save() report failure, to exercise a module's degraded path.</summary>
        public bool FailSaves { get; set; }

        public IReadOnlyDictionary<string, string> Values { get { return _values; } }

        public string Get(string key, string fallback)
        {
            string value;
            return key != null && _values.TryGetValue(key, out value) ? value : fallback;
        }

        public int GetInt(string key, int fallback)
        {
            int parsed;
            string value = Get(key, null);
            return value != null && int.TryParse(value, out parsed) ? parsed : fallback;
        }

        public bool GetBool(string key, bool fallback)
        {
            bool parsed;
            string value = Get(key, null);
            return value != null && bool.TryParse(value, out parsed) ? parsed : fallback;
        }

        public void Set(string key, string value)
        {
            if (key == null) return;
            _values[key] = value;
        }

        public bool Save()
        {
            SaveCount++;
            return !FailSaves;
        }
    }

    /// <summary>
    /// An <see cref="IModuleStorage"/> pointing at a fresh temp directory, deleted on Dispose. Wrap it in a
    /// using so a self-test leaves nothing behind:
    /// <code>using (var storage = new TempModuleStorage("mymodule")) { ... }</code>
    /// </summary>
    public sealed class TempModuleStorage : IModuleStorage, IDisposable
    {
        private readonly string _directory;

        public TempModuleStorage(string moduleId)
        {
            _directory = Path.Combine(Path.GetTempPath(),
                "dp-modulekit-" + (moduleId ?? "module") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        public string DataDirectory { get { return _directory; } }

        public void Dispose()
        {
            try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); } catch { }
        }
    }

    /// <summary>
    /// The pet manager a module gets when it did NOT declare <see cref="ModulePermissions.Companions"/>: every
    /// verb refuses with a reason and nothing throws. Mirrors the host's own denying bridge, so a module can
    /// prove it degrades gracefully rather than crashing on a permission it forgot to declare.
    /// </summary>
    public sealed class DenyingCompanionManager : ICompanionManager
    {
        private const string Denied = "This module has not declared the Pets permission.";

        public int MaxCompanions { get { return 16; } }
        public bool IsAtMax { get { return true; } }
        public string CompanionsDirectory { get { return ""; } }
        public IReadOnlyList<CompanionTypeInfo> InstalledTypes() { return new List<CompanionTypeInfo>(); }
        public bool TryReadTypeXml(string typeId, out string animationsXml, out string error) { animationsXml = null; error = Denied; return false; }
        public IReadOnlyList<CompanionCount> OnScreenMix() { return new List<CompanionCount>(); }
        public bool SpawnOne(string typeId) { return false; }
        public bool RemoveOne(string typeId) { return false; }
        public bool ValidateXml(string animationsXml, out string error) { error = Denied; return false; }
        public ICompanionPreview SpawnPreview(string animationsXml, out string error) { error = Denied; return null; }
        public bool InstallType(string typeId, string animationsXml, out string error) { error = Denied; return false; }
        public bool UninstallType(string typeId, out string error) { error = Denied; return false; }
    }

    /// <summary>A pet handle for driving a module's event handlers.</summary>
    public sealed class FakeCompanion : ICompanion
    {
        public FakeCompanion() : this(1, "") { }
        public FakeCompanion(int id, string typeId) { Id = id; TypeId = typeId ?? ""; }

        public int Id { get; private set; }
        public bool IsBusy { get; set; }
        public string TypeId { get; private set; }
    }

    /// <summary>A no-op registration handle (hotkeys, responders).</summary>
    public sealed class NoopDisposable : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() { Disposed = true; }
    }
}
