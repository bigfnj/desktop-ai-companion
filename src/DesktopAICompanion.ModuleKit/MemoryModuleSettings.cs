using System;
using System.Collections.Generic;
using System.Globalization;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.ModuleKit
{
    /// <summary>
    /// An in-memory <see cref="IModuleSettings"/> for the case where the host declines to hand one out.
    ///
    /// A module normally does <c>_settings = host.GetSettings(Id)</c> in Init and uses it freely, which is
    /// fine right up until a host answers null. That is not hypothetical: the app's own
    /// <c>--module-selftest=&lt;id&gt;</c> harness returns null from both GetSettings and GetStorage, and a
    /// module that builds its options SCHEMA during Init (any module whose dropdown options depend on a saved
    /// value) then dies with a NullReferenceException at load time, reported only as "module did not load".
    /// Both first-party modules that hit this were diagnosed the hard way.
    ///
    /// The ABI's own convention for a refused service is to degrade, not to throw into a module:
    /// GetCompanionManager returns a refusing instance, RegisterHotkey a no-op handle. This is the same idea for
    /// settings. Every read returns the caller's fallback until something writes, writes are kept for the
    /// life of the object, and <see cref="Save"/> returns false because nothing was persisted.
    ///
    /// Use it as <c>host.GetSettings(Id) ?? new MemoryModuleSettings()</c>. This is a degraded PRODUCTION
    /// path, deliberately not in the Testing namespace: a test double is for asserting behaviour, whereas
    /// this is for surviving a host that gave you nothing.
    /// </summary>
    public sealed class MemoryModuleSettings : IModuleSettings
    {
        private readonly Dictionary<string, string> _values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Get(string key, string fallback)
        {
            string value;
            if (key != null && _values.TryGetValue(key, out value)) return value;
            return fallback;
        }

        public int GetInt(string key, int fallback)
        {
            int parsed;
            string raw = Get(key, null);
            if (raw != null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return fallback;
        }

        public bool GetBool(string key, bool fallback)
        {
            bool parsed;
            string raw = Get(key, null);
            if (raw != null && bool.TryParse(raw, out parsed)) return parsed;
            return fallback;
        }

        public void Set(string key, string value)
        {
            if (key == null) return;
            _values[key] = value;
        }

        /// <summary>Always false: nothing was written anywhere, and a caller that reports "saved" on the
        /// strength of a true here would be lying to the user.</summary>
        public bool Save() { return false; }
    }
}
