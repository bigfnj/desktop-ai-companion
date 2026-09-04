using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace DesktopAICompanion
{
    /// <summary>
    /// Runtime facade over the versioned settings store. Values are held in one normalized snapshot;
    /// every durable update replaces the JSON file atomically.
    /// </summary>
    public class LocalData
    {
        private readonly object _sync = new object();
        private readonly AppSettingsStore _store;
        private AppSettingsDocument _settings;

        public string SettingsWarning { get; private set; }

        public LocalData()
            : this(new AppSettingsStore(AppPaths.SettingsFile, BuildLegacyCandidates()))
        {
        }

        internal LocalData(AppSettingsStore store)
        {
            _store = store ?? throw new ArgumentNullException("store");
            LoadSettings();
        }

        public void LoadSettings()
        {
            lock (_sync)
            {
                _settings = _store.Load() ?? AppSettingsDocument.CreateDefault();
                SettingsWarning = _store.LastLoadWarning;
                MigrateRandomDropIfAbsent();
                _settings.Normalize();
                SynchronizeLegacySettingsObject();
            }
        }

        public bool SetVolume(double volume)
        {
            volume = NormalizeVolume(volume);
            return Update(
                delegate { return Math.Abs(_settings.Volume - volume) > 0.000001; },
                delegate { _settings.Volume = volume; });
        }

        public float GetVolume()
        {
            lock (_sync)
                return (float)NormalizeVolume(_settings.Volume);
        }

        public bool SetScale(int level)
        {
            level = ScalePolicy.ClampLevel(level);
            return Update(
                delegate { return _settings.ScaleLevel != level; },
                delegate { _settings.ScaleLevel = level; });
        }

        /// <summary>The persisted UI level: 1, 2, or 3.</summary>
        public int GetScale()
        {
            lock (_sync)
                return ScalePolicy.ClampLevel(_settings.ScaleLevel);
        }

        /// <summary>The effective rendering/movement factor: 1x, 2x, or 4x.</summary>
        public bool GetMultiscreen()
        {
            lock (_sync) return _settings.MultiScreen;
        }

        public bool SetMultiscreen(bool multi)
        {
            return Update(
                delegate { return _settings.MultiScreen != multi; },
                delegate { _settings.MultiScreen = multi; });
        }

        public bool GetWindowForeground()
        {
            lock (_sync) return _settings.WindowForeground;
        }

        public bool SetWindowForeground(bool foreground)
        {
            return Update(
                delegate { return _settings.WindowForeground != foreground; },
                delegate { _settings.WindowForeground = foreground; });
        }

        public bool SetStealTaskbarFocus(bool steal)
        {
            return Update(
                delegate { return _settings.StealTaskbarFocus != steal; },
                delegate { _settings.StealTaskbarFocus = steal; });
        }

        public bool GetStealTaskbarFocus()
        {
            lock (_sync) return _settings.StealTaskbarFocus;
        }

        public int GetAutoStartPets()
        {
            lock (_sync)
                return Math.Max(
                    1,
                    Math.Min(AppSettingsDocument.MaximumAutoStartPets, _settings.AutoStartPets));
        }

        public bool SetAutoStartPets(int autostart)
        {
            autostart = Math.Max(
                1,
                Math.Min(AppSettingsDocument.MaximumAutoStartPets, autostart));
            return Update(
                delegate { return _settings.AutoStartPets != autostart; },
                delegate { _settings.AutoStartPets = autostart; });
        }

        // The on-screen pet mix (which pet types, and how many of each, to restore next launch).
        // Returns a deep copy so callers can't mutate the stored snapshot.
        internal System.Collections.Generic.List<CompanionCountEntry> GetPetMix()
        {
            lock (_sync)
                return AppSettingsDocument.ClonePetMix(_settings.Pets)
                    ?? new System.Collections.Generic.List<CompanionCountEntry>();
        }

        internal bool SetPetMix(System.Collections.Generic.List<CompanionCountEntry> pets)
        {
            System.Collections.Generic.List<CompanionCountEntry> value =
                AppSettingsDocument.ClonePetMix(pets)
                ?? new System.Collections.Generic.List<CompanionCountEntry>();
            return Update(
                delegate { return !AppSettingsDocument.PetMixEquals(_settings.Pets, value); },
                delegate { _settings.Pets = value; });
        }

        /// <summary>
        /// The monitor a pet TYPE is pinned to, or -1 for unpinned (the default).
        ///
        /// Validated against the CURRENT screen list on every read, never on write: monitors get unplugged,
        /// and a pin to a display that no longer exists must read as "unpinned" so the pet still appears.
        /// Storing the stale value is deliberate -- plug the screen back in and the pin returns.
        /// </summary>
        public int GetPetMonitor(string id, int screenCount)
        {
            if (string.IsNullOrEmpty(id) || screenCount <= 0) return -1;
            lock (_sync)
            {
                if (_settings.PetMonitors == null) return -1;
                foreach (CompanionMonitorEntry entry in _settings.PetMonitors)
                {
                    if (entry == null || !string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
                    return entry.Display >= 0 && entry.Display < screenCount ? entry.Display : -1;
                }
                return -1;
            }
        }

        /// <summary>Pin a pet type to a monitor, or pass a negative display to unpin it.</summary>
        public bool SetPetMonitor(string id, int display)
        {
            string key = (id ?? "").Trim();
            if (key.Length == 0 || key.Length > AppSettingsDocument.MaximumPetIdLength) return false;
            return Update(
                delegate
                {
                    int current = -1;
                    if (_settings.PetMonitors != null)
                        foreach (CompanionMonitorEntry e in _settings.PetMonitors)
                            if (e != null && string.Equals(e.Id, key, StringComparison.OrdinalIgnoreCase)) { current = e.Display; break; }
                    return current != display;
                },
                delegate
                {
                    if (_settings.PetMonitors == null) _settings.PetMonitors = new List<CompanionMonitorEntry>();
                    _settings.PetMonitors.RemoveAll(delegate(CompanionMonitorEntry e)
                        { return e == null || string.Equals(e.Id, key, StringComparison.OrdinalIgnoreCase); });
                    if (display >= 0) _settings.PetMonitors.Add(new CompanionMonitorEntry { Id = key, Display = display });
                });
        }

        // A pet type's size override level (1/2/3), or 0 when the pet follows the global size. id "" is
        // the active/default pet.
        internal int GetPetSizeLevel(string id)
        {
            lock (_sync) return GetPetSizeLevelNoLock(id);
        }

        private int GetPetSizeLevelNoLock(string id)
        {
            string key = id ?? "";
            if (_settings.PetSizes != null)
                foreach (CompanionSizeEntry entry in _settings.PetSizes)
                    if (entry != null &&
                        string.Equals(entry.Id ?? "", key, StringComparison.OrdinalIgnoreCase))
                        return ScalePolicy.ClampLevel(entry.Level);
            return 0;
        }

        /// <summary>
        /// The effective rendering/movement factor (1x/2x/4x) for a pet: its own size override when set,
        /// otherwise the global factor. Used when a pet type is staged.
        /// </summary>
        public int GetEffectivePetScaleFactor(string id)
        {
            lock (_sync)
            {
                int level = GetPetSizeLevelNoLock(id);
                return level >= ScalePolicy.MinimumLevel
                    ? ScalePolicy.FactorFromLevel(level)
                    : ScalePolicy.FactorFromLevel(ScalePolicy.ClampLevel(_settings.ScaleLevel));
            }
        }

        // Set (level 1/2/3) or clear (level 0 or out of range -> follow global) a pet's size override.
        internal bool SetPetSizeLevel(string id, int level)
        {
            string key = id ?? "";
            bool clear = level < ScalePolicy.MinimumLevel || level > ScalePolicy.MaximumLevel;
            return Update(
                delegate
                {
                    int current = GetPetSizeLevelNoLock(key);
                    return clear ? current != 0 : current != level;
                },
                delegate
                {
                    var list = _settings.PetSizes ?? new List<CompanionSizeEntry>();
                    list.RemoveAll(delegate (CompanionSizeEntry e)
                    {
                        return e == null ||
                            string.Equals(e.Id ?? "", key, StringComparison.OrdinalIgnoreCase);
                    });
                    if (!clear) list.Add(new CompanionSizeEntry { Id = key, Level = level });
                    _settings.PetSizes = list;
                });
        }

        // --- Fractional size (the size slider; percent 25..400). Precedence: per-pet percent, else the global
        // percent, else the legacy 1x/2x/4x level. A pet override that only carries a legacy Level still works. ---

        /// <summary>The effective FRACTIONAL rendering/movement factor for a pet, honouring a sub-1 size.</summary>
        public double GetEffectivePetScaleFactorD(string id)
        {
            lock (_sync)
            {
                CompanionSizeEntry entry = FindPetSizeEntryNoLock(id);
                if (entry != null)
                {
                    if (entry.Percent >= ScalePolicy.MinimumPercent)
                        return ScalePolicy.FactorFromPercent(entry.Percent);
                    if (entry.Level >= ScalePolicy.MinimumLevel && entry.Level <= ScalePolicy.MaximumLevel)
                        return ScalePolicy.FactorFromLevel(entry.Level);
                }
                if (_settings.ScalePercent >= ScalePolicy.MinimumPercent)
                    return ScalePolicy.FactorFromPercent(_settings.ScalePercent);
                return ScalePolicy.FactorFromLevel(ScalePolicy.ClampLevel(_settings.ScaleLevel));
            }
        }

        /// <summary>The effective size PERCENT (25..400) for a pet, for the slider UI.</summary>
        public int GetEffectivePetScalePercent(string id)
        {
            lock (_sync)
            {
                CompanionSizeEntry entry = FindPetSizeEntryNoLock(id);
                if (entry != null)
                {
                    if (entry.Percent >= ScalePolicy.MinimumPercent) return ScalePolicy.ClampPercent(entry.Percent);
                    if (entry.Level >= ScalePolicy.MinimumLevel && entry.Level <= ScalePolicy.MaximumLevel)
                        return ScalePolicy.PercentFromLevel(entry.Level);
                }
                if (_settings.ScalePercent >= ScalePolicy.MinimumPercent) return ScalePolicy.ClampPercent(_settings.ScalePercent);
                return ScalePolicy.PercentFromLevel(ScalePolicy.ClampLevel(_settings.ScaleLevel));
            }
        }

        private CompanionSizeEntry FindPetSizeEntryNoLock(string id)
        {
            string key = id ?? "";
            if (_settings.PetSizes != null)
                foreach (CompanionSizeEntry entry in _settings.PetSizes)
                    if (entry != null &&
                        string.Equals(entry.Id ?? "", key, StringComparison.OrdinalIgnoreCase))
                        return entry;
            return null;
        }

        /// <summary>Set (25..400) or clear (0/out of range -&gt; follow global) a pet's size percent override.</summary>
        internal bool SetPetScalePercent(string id, int percent)
        {
            string key = id ?? "";
            bool clear = percent < ScalePolicy.MinimumPercent || percent > ScalePolicy.MaximumPercent;
            int value = clear ? 0 : percent;
            return Update(
                delegate
                {
                    CompanionSizeEntry current = FindPetSizeEntryNoLock(key);
                    int currentPercent = current != null && current.Percent >= ScalePolicy.MinimumPercent
                        ? current.Percent : 0;
                    return currentPercent != value;
                },
                delegate
                {
                    var list = _settings.PetSizes ?? new List<CompanionSizeEntry>();
                    list.RemoveAll(delegate (CompanionSizeEntry e)
                    {
                        return e == null ||
                            string.Equals(e.Id ?? "", key, StringComparison.OrdinalIgnoreCase);
                    });
                    if (!clear) list.Add(new CompanionSizeEntry { Id = key, Percent = value });
                    _settings.PetSizes = list;
                });
        }

        /// <summary>
        /// The module that should speak a pet's first poke ("" = default &amp; random). <paramref name="petId"/>
        /// is reserved for per-pet voices (BACKLOG #16): today only the global entry ("") is written, and a
        /// per-pet lookup falls back to it, so adding per-pet UI later needs no settings migration.
        /// </summary>
        public string GetTriggerSpeechModule(string petId)
        {
            lock (_sync)
            {
                string specific = FindTriggerSpeechNoLock(petId ?? "");
                if (!string.IsNullOrEmpty(specific)) return specific;
                return string.IsNullOrEmpty(petId) ? "" : FindTriggerSpeechNoLock("");
            }
        }

        private string FindTriggerSpeechNoLock(string key)
        {
            if (_settings.TriggerSpeech != null)
                foreach (TriggerSpeechEntry entry in _settings.TriggerSpeech)
                    if (entry != null &&
                        string.Equals(entry.Id ?? "", key, StringComparison.OrdinalIgnoreCase))
                        return entry.Module ?? "";
            return "";
        }

        /// <summary>Every pet id that currently has its OWN speech choice, excluding the "" all-pets entry.
        /// Backs the tray's "reset all pets", which is the only way back once a per-pet choice outlives the
        /// pet it was made for (the Preferences reset deliberately clears only the global entry).</summary>
        public List<string> TriggerSpeechPetIds()
        {
            var ids = new List<string>();
            lock (_sync)
            {
                if (_settings.TriggerSpeech != null)
                    foreach (TriggerSpeechEntry entry in _settings.TriggerSpeech)
                        if (entry != null && !string.IsNullOrEmpty(entry.Id)) ids.Add(entry.Id);
            }
            return ids;
        }

        /// <summary>Set (module id) or clear ("" = default &amp; random) the poke speaker for a pet id
        /// ("" = all pets). Per-pet entries are written by the tray's Pet Speech cascade; the Preferences
        /// dropdown writes the "" entry.</summary>
        public bool SetTriggerSpeechModule(string petId, string moduleId)
        {
            string key = petId ?? "";
            string module = (moduleId ?? "").Trim();
            return Update(
                delegate
                {
                    return !string.Equals(FindTriggerSpeechNoLock(key), module, StringComparison.OrdinalIgnoreCase);
                },
                delegate
                {
                    var list = _settings.TriggerSpeech ?? new List<TriggerSpeechEntry>();
                    list.RemoveAll(delegate (TriggerSpeechEntry e)
                    {
                        return e == null ||
                            string.Equals(e.Id ?? "", key, StringComparison.OrdinalIgnoreCase);
                    });
                    if (module.Length > 0) list.Add(new TriggerSpeechEntry { Id = key, Module = module });
                    _settings.TriggerSpeech = list;
                });
        }

        /// <summary>The settings-window theme mode: "system", "light", or "dark".</summary>
        public string GetThemeMode()
        {
            lock (_sync) return AppSettingsDocument.NormalizeThemeMode(_settings.ThemeMode);
        }

        public bool SetThemeMode(string mode)
        {
            string value = AppSettingsDocument.NormalizeThemeMode(mode);
            return Update(
                delegate { return !string.Equals(AppSettingsDocument.NormalizeThemeMode(_settings.ThemeMode), value, StringComparison.Ordinal); },
                delegate { _settings.ThemeMode = value; });
        }

        /// <summary>The chosen audio output device GUID (DirectSound); "" = the default device.</summary>
        public string GetAudioDeviceId()
        {
            lock (_sync) return AppSettingsDocument.NormalizeAudioDeviceId(_settings.AudioDeviceId);
        }

        public bool SetAudioDeviceId(string id)
        {
            string value = AppSettingsDocument.NormalizeAudioDeviceId(id);
            return Update(
                delegate { return !string.Equals(AppSettingsDocument.NormalizeAudioDeviceId(_settings.AudioDeviceId), value, StringComparison.Ordinal); },
                delegate { _settings.AudioDeviceId = value; });
        }

        /// <summary>True unless the pet type's animation sound is muted (per-pet sound toggle). id "" = active.</summary>
        public bool IsPetSoundEnabled(string id)
        {
            lock (_sync) return !IsPetMutedNoLock(id ?? "");
        }

        private bool IsPetMutedNoLock(string key)
        {
            if (_settings.MutedPets != null)
                foreach (string m in _settings.MutedPets)
                    if (string.Equals(m ?? "", key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal bool SetPetSoundEnabled(string id, bool enabled)
        {
            string key = id ?? "";
            return Update(
                delegate { return IsPetMutedNoLock(key) == enabled; },   // muted==enabled means the state must flip
                delegate
                {
                    var list = _settings.MutedPets ?? new List<string>();
                    list.RemoveAll(delegate (string m) { return string.Equals(m ?? "", key, StringComparison.OrdinalIgnoreCase); });
                    if (!enabled) list.Add(key);
                    _settings.MutedPets = list;
                });
        }

        /// <summary>The real id of the active/default pet (so per-pet size/sound key by the actual pet, not
        /// the "" active-slot placeholder). Defaults to the built-in pet.</summary>
        public string GetActivePetId()
        {
            lock (_sync) return AppSettingsDocument.NormalizeActivePetId(_settings.ActivePetId);
        }

        public bool SetActivePetId(string id)
        {
            string value = AppSettingsDocument.NormalizeActivePetId(id);
            return Update(
                delegate { return !string.Equals(AppSettingsDocument.NormalizeActivePetId(_settings.ActivePetId), value, StringComparison.Ordinal); },
                delegate { _settings.ActivePetId = value; });
        }

        /// <summary>Global master switch for the pet's own animation SFX (the &lt;sound&gt; in a pet's XML).
        /// Off silences every pet's sounds, on top of the per-pet mute. Defaults on.</summary>
        public bool GetPetSoundsEnabled()
        {
            lock (_sync) return _settings.PetSoundsEnabled ?? true;
        }

        public bool SetPetSoundsEnabled(bool enabled)
        {
            return Update(
                delegate { return (_settings.PetSoundsEnabled ?? true) != enabled; },
                delegate { _settings.PetSoundsEnabled = enabled; });
        }

        /// <summary>Global master switch for module notification sounds (chimes) played via IHost.PlaySound.
        /// Off makes PlaySound a no-op so a module falls back to a silent bubble. Defaults on.</summary>
        public bool GetNotificationSoundsEnabled()
        {
            lock (_sync) return _settings.NotificationSoundsEnabled ?? true;
        }

        public bool SetNotificationSoundsEnabled(bool enabled)
        {
            return Update(
                delegate { return (_settings.NotificationSoundsEnabled ?? true) != enabled; },
                delegate { _settings.NotificationSoundsEnabled = enabled; });
        }

        public bool GetSpeechEnabled()
        {
            lock (_sync) return _settings.SpeechEnabled;
        }

        public bool SetSpeechEnabled(bool enabled)
        {
            return Update(
                delegate { return _settings.SpeechEnabled != enabled; },
                delegate { _settings.SpeechEnabled = enabled; });
        }

        public int GetSpeechDuration()
        {
            lock (_sync)
                return Math.Max(2, Math.Min(30, _settings.SpeechDurationSeconds));
        }

        public bool SetSpeechDuration(int seconds)
        {
            seconds = Math.Max(2, Math.Min(30, seconds));
            return Update(
                delegate { return _settings.SpeechDurationSeconds != seconds; },
                delegate { _settings.SpeechDurationSeconds = seconds; });
        }

        /// <summary>Master "don't say the same message twice in a row" guard (host-enforced across modules).
        /// Absent (null in an older doc) counts as ON, so the guard is the default without a settings edit.</summary>
        public bool GetSuppressRepeats()
        {
            lock (_sync) return _settings.SuppressRepeats ?? true;
        }

        public bool SetSuppressRepeats(bool on)
        {
            return Update(
                delegate { return _settings.SuppressRepeats != on; },
                delegate { _settings.SuppressRepeats = on; });
        }

        /// <summary>Monthly "is a newer build of an installed module published?" check (notify only; nothing
        /// installs itself). Absent (null in an older doc) counts as ON, matching a fresh install's default —
        /// otherwise everyone upgrading from 1.4.1 would silently never be told about a module fix.</summary>
        public bool GetMonthlyModuleUpdateCheck()
        {
            lock (_sync) return _settings.MonthlyModuleUpdateCheck ?? true;
        }

        public bool SetMonthlyModuleUpdateCheck(bool on)
        {
            return Update(
                delegate { return _settings.MonthlyModuleUpdateCheck != on; },
                delegate { _settings.MonthlyModuleUpdateCheck = on; });
        }

        /// <summary>The pet TYPE that speaks a message addressed to nobody in particular. "" = the oldest pet
        /// on screen, which is also the fallback when the chosen type is not currently out.</summary>
        public string GetDefaultSpeakingPet()
        {
            lock (_sync) return _settings.DefaultSpeakingPet ?? "";
        }

        public bool SetDefaultSpeakingPet(string typeId)
        {
            string v = (typeId ?? "").Trim();
            if (v.Length > AppSettingsDocument.MaximumPetIdLength) v = "";
            return Update(
                delegate { return !string.Equals(_settings.DefaultSpeakingPet, v, StringComparison.Ordinal); },
                delegate { _settings.DefaultSpeakingPet = v; });
        }

        /// <summary>Whether launch may check once a day whether a newer app version exists. Absent (an older
        /// doc) reads as ON, matching a fresh install. Notify-only: nothing downloads or installs.</summary>
        public bool GetAppUpdateCheck()
        {
            lock (_sync) return _settings.AppUpdateCheck ?? true;
        }

        public bool SetAppUpdateCheck(bool on)
        {
            return Update(
                delegate { return _settings.AppUpdateCheck != on; },
                delegate { _settings.AppUpdateCheck = on; });
        }

        /// <summary>When the app version was last looked up, so a launch checks at most once a day.
        /// DateTimeOffset.MinValue when never (or unparseable, which is treated the same).</summary>
        public DateTimeOffset GetAppUpdateLastCheckUtc()
        {
            string raw;
            lock (_sync) raw = _settings.AppUpdateLastCheckUtc ?? "";
            DateTimeOffset when;
            if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out when))
                return DateTimeOffset.MinValue;
            return when;
        }

        /// <summary>The newest version the last check saw, cached so the footer can offer the link without
        /// waiting on (or needing) a request. "" when nothing newer is known.</summary>
        public string GetAppUpdateLatestVersion()
        {
            lock (_sync) return _settings.AppUpdateLatestVersion ?? "";
        }

        /// <summary>Record the outcome of a check: the moment it ran, and the newest version it saw.</summary>
        public bool SetAppUpdateResult(DateTimeOffset checkedUtc, string latestVersion)
        {
            string stamp = checkedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            string v = (latestVersion ?? "").Trim();
            if (v.Length > 64) v = "";
            return Update(
                delegate
                {
                    return !string.Equals(_settings.AppUpdateLastCheckUtc, stamp, StringComparison.Ordinal)
                        || !string.Equals(_settings.AppUpdateLatestVersion, v, StringComparison.Ordinal);
                },
                delegate
                {
                    _settings.AppUpdateLastCheckUtc = stamp;
                    _settings.AppUpdateLatestVersion = v;
                });
        }

        // ---- module and pet update checks -------------------------------------------------------------
        // Same shape as the app check above: a stamp so a launch does not hit the network more than once a
        // week, and the RESULT so a pane can render an available update the instant it opens rather than
        // after the user presses a button. The parse helper is shared so all three read a bad stamp the same
        // way -- as "never checked", which errs towards checking again rather than towards going quiet.

        private static DateTimeOffset ParseStamp(string raw)
        {
            DateTimeOffset when;
            if (!DateTimeOffset.TryParse(raw ?? "", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out when))
                return DateTimeOffset.MinValue;
            return when;
        }

        /// <summary>When modules were last checked against the catalog. MinValue when never.</summary>
        public DateTimeOffset GetModuleUpdateLastCheckUtc()
        {
            lock (_sync) return ParseStamp(_settings.ModuleUpdateLastCheckUtc);
        }

        /// <summary>The last check's offers, "id=version;id=version". "" when none were found.</summary>
        public string GetModuleUpdateOffers()
        {
            lock (_sync) return _settings.ModuleUpdateOffers ?? "";
        }

        /// <summary>Record a module check: when it ran, and what it found.</summary>
        public bool SetModuleUpdateResult(DateTimeOffset checkedUtc, string offers)
        {
            string stamp = checkedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            string value = Clamp(offers);
            return Update(
                delegate
                {
                    return !string.Equals(_settings.ModuleUpdateLastCheckUtc, stamp, StringComparison.Ordinal)
                        || !string.Equals(_settings.ModuleUpdateOffers, value, StringComparison.Ordinal);
                },
                delegate
                {
                    _settings.ModuleUpdateLastCheckUtc = stamp;
                    _settings.ModuleUpdateOffers = value;
                });
        }

        /// <summary>Whether pets may be checked against the catalog on a schedule. Absent reads as ON.</summary>
        public bool GetPetUpdateCheck()
        {
            lock (_sync) return _settings.PetUpdateCheck ?? true;
        }

        public bool SetPetUpdateCheck(bool on)
        {
            return Update(
                delegate { return _settings.PetUpdateCheck != on; },
                delegate { _settings.PetUpdateCheck = on; });
        }

        /// <summary>When pets were last checked against the catalog. MinValue when never.</summary>
        public DateTimeOffset GetPetUpdateLastCheckUtc()
        {
            lock (_sync) return ParseStamp(_settings.PetUpdateLastCheckUtc);
        }

        /// <summary>The catalog pets whose installed copy is stale, "id;id". "" when none.</summary>
        public string GetPetUpdateStaleIds()
        {
            lock (_sync) return _settings.PetUpdateStaleIds ?? "";
        }

        /// <summary>Record a pet check: when it ran, and which ids were stale.</summary>
        public bool SetPetUpdateResult(DateTimeOffset checkedUtc, string staleIds)
        {
            string stamp = checkedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            string value = Clamp(staleIds);
            return Update(
                delegate
                {
                    return !string.Equals(_settings.PetUpdateLastCheckUtc, stamp, StringComparison.Ordinal)
                        || !string.Equals(_settings.PetUpdateStaleIds, value, StringComparison.Ordinal);
                },
                delegate
                {
                    _settings.PetUpdateLastCheckUtc = stamp;
                    _settings.PetUpdateStaleIds = value;
                });
        }

        /// <summary>Bound a cached list so a pathological catalog cannot grow settings.json without limit.
        /// Dropping an over-long value costs one redundant check, which is the harmless direction.</summary>
        private static string Clamp(string value)
        {
            string v = (value ?? "").Trim();
            return v.Length > 2048 ? "" : v;
        }

        /// <summary>Random-drop cadence (rehomed out of AiSettings, S5c). Absent (null) reads as the field
        /// default: off / 15 minutes / plus-or-minus 3 minutes.</summary>
        public bool GetRandomDropEnabled()
        {
            lock (_sync) return _settings.RandomDropEnabled ?? false;
        }

        public int GetRandomDropMinutes()
        {
            lock (_sync) return _settings.RandomDropMinutes ?? 15;
        }

        public int GetRandomDropJitterMinutes()
        {
            lock (_sync) return _settings.RandomDropJitterMinutes ?? 3;
        }

        /// <summary>Persist all three random-drop fields together (the Preferences pane edits them as a set).</summary>
        public bool SetRandomDrop(bool enabled, int minutes, int jitter)
        {
            return Update(
                delegate
                {
                    return _settings.RandomDropEnabled != enabled
                        || _settings.RandomDropMinutes != minutes
                        || _settings.RandomDropJitterMinutes != jitter;
                },
                delegate
                {
                    _settings.RandomDropEnabled = enabled;
                    _settings.RandomDropMinutes = minutes;
                    _settings.RandomDropJitterMinutes = jitter;
                });
        }

        /// <summary>
        /// One-time bridge (S5c): random-drop moved out of the legacy <c>ai-settings.json</c> into
        /// <c>settings.json</c>. A settings doc written before the move has these keys absent (null); seed
        /// them once from the legacy file if it still exists, else the field defaults. Idempotent (once the
        /// values are non-null this no-ops) and self-contained (no AiSettings dependency), so it keeps
        /// working after the AI cluster is removed. Not persisted here — the seeded values are durable on the
        /// next settings write; until then this simply re-runs harmlessly with the same result.
        /// </summary>
        private void MigrateRandomDropIfAbsent()
        {
            if (_settings.RandomDropEnabled.HasValue
                || _settings.RandomDropMinutes.HasValue
                || _settings.RandomDropJitterMinutes.HasValue)
                return;

            bool enabled = false;
            int minutes = 15;
            int jitter = 3;
            try
            {
                string legacy = AppPaths.AiSettingsFile;
                if (File.Exists(legacy))
                {
                    JsonNode o = JsonNode.Parse(File.ReadAllText(legacy, Encoding.UTF8));
                    if (o != null)
                    {
                        enabled = JsonRead.BoolOrNull(o["RandomDropEnabled"]) ?? enabled;
                        minutes = JsonRead.IntOrNull(o["RandomDropMinutes"]) ?? minutes;
                        jitter = JsonRead.IntOrNull(o["RandomDropJitterMinutes"]) ?? jitter;
                    }
                }
            }
            catch { /* legacy file absent or unreadable: keep the defaults */ }

            _settings.RandomDropEnabled = enabled;
            _settings.RandomDropMinutes = minutes;
            _settings.RandomDropJitterMinutes = jitter;
        }

        public bool SetXml(string xml, string folder)
        {
            string value = xml ?? "";
            return Update(
                delegate { return !string.Equals(_settings.Xml, value, StringComparison.Ordinal); },
                delegate { _settings.Xml = value; });
        }

        /// <summary>
        /// Atomically commit all persisted assets for a staged pet. The in-memory snapshot is
        /// restored if durable persistence is blocked or fails.
        /// </summary>
        public bool TrySetPetAssets(string xml, string images, string icon)
        {
            lock (_sync)
            {
                AppSettingsDocument before = CloneSettings(_settings);
                _settings.Xml = xml ?? "";
                _settings.Images = images ?? "";
                _settings.Icon = icon ?? "";
                _settings.Normalize();
                SynchronizeLegacySettingsObject();
                if (_store.Save(_settings)) return true;
                _settings = before;
                SynchronizeLegacySettingsObject();
                return false;
            }
        }

        public string GetXml()
        {
            lock (_sync) return _settings.Xml ?? "";
        }

        public string LoadXML()
        {
            // Runtime pet replacement is validated and committed by StartUp. Legacy installpet.xml,
            // arbitrary URL, and direct file side channels are intentionally no longer consumed here.
            return GetXml();
        }

        public string GetImages()
        {
            lock (_sync) return _settings.Images ?? "";
        }

        public string GetIcon()
        {
            lock (_sync) return _settings.Icon ?? "";
        }

        public bool SetIcon(string icon)
        {
            string value = icon ?? "";
            return Update(
                delegate { return !string.Equals(_settings.Icon, value, StringComparison.Ordinal); },
                delegate { _settings.Icon = value; });
        }

        public delegate void MyFunction(object source, FileSystemEventArgs e);

        public void ListenOnXMLChanged(MyFunction f)
        {
            // Not implemented in the portable build.
        }

        public void ListenOnOptionsChanged(MyFunction f)
        {
            // Not implemented in the portable build.
        }

        private bool Update(Func<bool> changed, Action apply)
        {
            lock (_sync)
            {
                if (!changed()) return true;
                AppSettingsDocument before = CloneSettings(_settings);
                apply();
                _settings.Normalize();
                SynchronizeLegacySettingsObject();
                if (_store.Save(_settings)) return true;
                _settings = before;
                SynchronizeLegacySettingsObject();
                return false;
            }
        }

        private static AppSettingsDocument CloneSettings(AppSettingsDocument source)
        {
            return AppSettingsStore.Clone(source);
        }

        private static double NormalizeVolume(double volume)
        {
            if (double.IsNaN(volume) || double.IsInfinity(volume)) return 0.3;
            return Math.Max(0.0, Math.Min(1.0, volume));
        }

        /// <summary>
        /// Keep the generated settings object coherent for old extension code, but never call its
        /// Save method. The canonical durable file is AppPaths.SettingsFile.
        /// </summary>
        private void SynchronizeLegacySettingsObject()
        {
            try
            {
                Properties.Settings.Default.Volume = (float)_settings.Volume;
                Properties.Settings.Default.Scale = _settings.ScaleLevel;
                Properties.Settings.Default.AutostartPets = _settings.AutoStartPets;
                Properties.Settings.Default.Multiscreen = _settings.MultiScreen;
                Properties.Settings.Default.WinForeground = _settings.WindowForeground;
                Properties.Settings.Default.StealTaskbarFocus = _settings.StealTaskbarFocus;
                Properties.Settings.Default.SpeechEnabled = _settings.SpeechEnabled;
                Properties.Settings.Default.SpeechDuration = _settings.SpeechDurationSeconds;
                Properties.Settings.Default.xml = _settings.Xml;
                Properties.Settings.Default.Images = _settings.Images;
                Properties.Settings.Default.Icon = _settings.Icon;
            }
            catch
            {
                // A corrupt legacy user.config cannot make the canonical settings unusable.
            }
        }

        private static IEnumerable<string> BuildLegacyCandidates()
        {
            var candidates = new List<string>(AppPaths.LegacySettingsFiles);
            try
            {
                string userConfig = System.Configuration.ConfigurationManager
                    .OpenExeConfiguration(System.Configuration.ConfigurationUserLevel.PerUserRoamingAndLocal)
                    .FilePath;
                if (!string.IsNullOrWhiteSpace(userConfig))
                    candidates.Add(Path.GetFullPath(userConfig));
            }
            catch
            {
            }
            return candidates;
        }
    }
}
