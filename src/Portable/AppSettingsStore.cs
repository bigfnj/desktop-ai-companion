using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Xml;

namespace DesktopAICompanion
{
    /// <summary>Versioned persisted settings owned by <see cref="LocalData"/>.</summary>
    internal sealed class AppSettingsDocument
    {
        public const int CurrentSchemaVersion = 2;
        public const int MaximumAutoStartPets = 16;
        public const int MaximumOnScreenPets = 16;   // total pets across all types (matches MAX_SHEEPS)
        public const int MaximumPetIdLength = 128;
        public const int MaximumPetSizeEntries = 256;   // bounds the per-pet size-override list
        public const int MaximumXmlBytes = 12 * 1024 * 1024;
        public const int MaximumLegacyImageCharacters = 16 * 1024 * 1024;
        public const int MaximumLegacyIconCharacters = 1024 * 1024;

        [JsonPropertyName("schemaVersion"), JsonPropertyOrder(1)]
        public int SchemaVersion;

        [JsonPropertyName("volume"), JsonPropertyOrder(2)]
        public double Volume;

        [JsonPropertyName("scaleLevel"), JsonPropertyOrder(3)]
        public int ScaleLevel;

        // Global fractional size as a PERCENT (25..400, 100 = 1x). 0 = follow the legacy ScaleLevel above.
        // Lets the size slider go BELOW 1x, which the integer level cannot express.
        [JsonPropertyName("scalePercent"), JsonPropertyOrder(15)]
        public int ScalePercent;

        [JsonPropertyName("autoStartPets"), JsonPropertyOrder(4)]
        public int AutoStartPets;

        [JsonPropertyName("multiScreen"), JsonPropertyOrder(5)]
        public bool MultiScreen;

        [JsonPropertyName("windowForeground"), JsonPropertyOrder(6)]
        public bool WindowForeground;

        [JsonPropertyName("stealTaskbarFocus"), JsonPropertyOrder(7)]
        public bool StealTaskbarFocus;

        [JsonPropertyName("speechEnabled"), JsonPropertyOrder(8)]
        public bool SpeechEnabled;

        [JsonPropertyName("speechDurationSeconds"), JsonPropertyOrder(9)]
        public int SpeechDurationSeconds;

        [JsonPropertyName("xml"), JsonPropertyOrder(10)]
        public string Xml;

        [JsonPropertyName("images"), JsonPropertyOrder(11)]
        public string Images;

        [JsonPropertyName("icon"), JsonPropertyOrder(12)]
        public string Icon;

        // The on-screen pet mix: how many pets of each type to spawn/restore. id "" = the active/
        // default pet (the one described by Xml above); other ids are pet folder ids. Introduced in
        // schema v2; migrated from the single AutoStartPets count for older docs (see Normalize).
        [JsonPropertyName("pets"), JsonPropertyOrder(13)]
        public List<CompanionCountEntry> Pets;

        // Per-pet size overrides: pet id -> scale level (1/2/3). Absent = follow the global ScaleLevel;
        // id "" is the active/default pet. Optional (older docs carry none). Sits alongside the pet mix.
        [JsonPropertyName("petSizes"), JsonPropertyOrder(14)]
        public List<CompanionSizeEntry> PetSizes;

        // Which monitor a pet TYPE is pinned to. Absent from the list = unpinned, which is the default and
        // means the pet behaves as it always has: it spawns on a random screen and may be relocated off a
        // monitor a fullscreen app has taken. A pin is an explicit instruction and is honoured strictly --
        // a pinned pet HIDES rather than moving, because "put Hornet on monitor 2" is not a preference the
        // app should quietly override the first time a game starts.
        [JsonPropertyName("petMonitors"), JsonPropertyOrder(36)]
        public List<CompanionMonitorEntry> PetMonitors;

        // UI theme for the settings window: "system" (follow the OS), "light", or "dark". Optional
        // (older docs default to "system" on load).
        [JsonPropertyName("themeMode"), JsonPropertyOrder(15)]
        public string ThemeMode;

        // Audio output device GUID (DirectSound) for host-owned playback; "" = the default device. Optional.
        [JsonPropertyName("audioDeviceId"), JsonPropertyOrder(16)]
        public string AudioDeviceId;

        // Pet type ids whose animation sounds are muted (per-pet sound toggle, B3). Absent from this list =
        // sound on (the default). id "" is the active/default pet. Optional (older docs mute nothing).
        [JsonPropertyName("mutedPets"), JsonPropertyOrder(17)]
        public List<string> MutedPets;

        // The real id of the active/default pet, so per-pet settings (size/sound) key by the actual pet
        // rather than the "" active-slot placeholder. Default = the built-in eSheep. Set when the user picks
        // a pet ("Use"/restore). Optional (older docs default to the built-in).
        [JsonPropertyName("activePetId"), JsonPropertyOrder(18)]
        public string ActivePetId;

        // Master "don't say the same message twice in a row" guard, enforced in the host's SayAll so it covers
        // every speaker (AI brain, fortunes, welcome, ...). A Preferences toggle, default ON. Nullable so a
        // doc written before this field existed loads as null (absent) — distinct from an explicit false —
        // and GetSuppressRepeats() treats null as ON. (A plain bool + DefaultValueHandling.Populate defaulted
        // to false in practice, leaving the guard silently disabled.)
        [JsonPropertyName("suppressRepeats"), JsonPropertyOrder(19)]
        public bool? SuppressRepeats;

        // Random-drop cadence: periodically speak an unprompted line (a fortune/insight). Rehomed here out
        // of the retired AiSettings blob (S5c). Nullable so a doc written before this field existed loads as
        // null (absent) — LocalData then one-time-migrates the values from the legacy ai-settings.json; the
        // GetRandomDrop* accessors treat null as the field defaults (off / 15 min / ±3 min).
        [JsonPropertyName("randomDropEnabled"), JsonPropertyOrder(20)]
        public bool? RandomDropEnabled;

        // Two global audio master switches, nullable so a pre-existing settings file defaults them ON (the
        // same reason RandomDropEnabled is nullable): the pet's own <sound> SFX, and module notification
        // sounds (chimes) played through IHost.PlaySound. Off silences that whole category regardless of the
        // finer per-pet mute / per-module toggles.
        [JsonPropertyName("petSoundsEnabled"), JsonPropertyOrder(30)]
        public bool? PetSoundsEnabled;

        [JsonPropertyName("notificationSoundsEnabled"), JsonPropertyOrder(31)]
        public bool? NotificationSoundsEnabled;

        [JsonPropertyName("randomDropMinutes"), JsonPropertyOrder(21)]
        public int? RandomDropMinutes;

        [JsonPropertyName("randomDropJitterMinutes"), JsonPropertyOrder(22)]
        public int? RandomDropJitterMinutes;

        // Which module speaks the FIRST poke of a fresh right-click session ("" = default & random: any
        // registered poke responder may win, including none of them). Stored as a LIST keyed by pet type id
        // rather than a single scalar, because per-pet voices are a planned follow-up (BACKLOG #16) and a
        // scalar here would need a migration to get there; the entry with id "" is the global/all-pets
        // choice, which is the only one today's UI writes. Absent/empty list = the default.
        [JsonPropertyName("triggerSpeech"), JsonPropertyOrder(23)]
        public List<TriggerSpeechEntry> TriggerSpeech;

        // Once a month, ask the content catalog whether an installed module has a newer build (notify only —
        // nothing downloads or installs itself). A Preferences toggle, default ON, and the only thing in the app
        // that reaches the network without the user asking, which is exactly why it is switchable. Nullable for
        // the same reason as SuppressRepeats: a doc written before this field existed must load as "absent" and
        // be treated as ON, not as an explicit false.
        [JsonPropertyName("monthlyModuleUpdateCheck"), JsonPropertyOrder(24)]
        public bool? MonthlyModuleUpdateCheck;

        // Which pet speaks a message addressed to nobody in particular (IHost.SayAll). Empty = the oldest pet
        // on screen. Before this, SayAll drew a bubble on EVERY pet at the same instant, which the ABI's own
        // comment already called out as reading like a bug. Stored as a pet TYPE id, not a live pet handle,
        // because the choice has to survive the pet being removed and re-added.
        [JsonPropertyName("defaultSpeakingPet"), JsonPropertyOrder(32)]
        public string DefaultSpeakingPet;

        // Nullable for the same reason as MonthlyModuleUpdateCheck: a doc written before this field existed
        // must read as "absent" and be treated as ON, not as an explicit false.
        [JsonPropertyName("appUpdateCheck"), JsonPropertyOrder(33)]
        public bool? AppUpdateCheck;

        // The throttle and the cache for that check, so a launch does not hit the network more than once a
        // day and the footer can show a known-newer version without waiting on (or needing) a request.
        [JsonPropertyName("appUpdateLastCheckUtc"), JsonPropertyOrder(34)]
        public string AppUpdateLastCheckUtc;

        [JsonPropertyName("appUpdateLatestVersion"), JsonPropertyOrder(35)]
        public string AppUpdateLatestVersion;

        // The same throttle-and-cache shape as appUpdate*, for modules and pets.
        //
        // Caching the RESULT and not just the timestamp is the whole point. The module check already existed
        // and already ran, but it threw its findings away and only raised a balloon, so the Modules pane
        // still knew nothing when you opened it and you still had to press a button. A pane can only render
        // an update the instant it opens if the last answer is written down.
        //
        // Compact single strings rather than lists: these are a cache, not user data. Nothing else reads
        // them, a stale or malformed entry costs one redundant network check, and a List<T> would need its
        // own equality and clone helpers in three more places (see PetMonitorsEqual/ClonePetMonitors).
        [JsonPropertyName("moduleUpdateLastCheckUtc"), JsonPropertyOrder(37)]
        public string ModuleUpdateLastCheckUtc;

        // "id=version;id=version", e.g. "aibrain=1.4.1;fortunes=1.2.8".
        [JsonPropertyName("moduleUpdateOffers"), JsonPropertyOrder(38)]
        public string ModuleUpdateOffers;

        // Nullable for the same reason as the two above it: a doc written before this field existed must
        // read as absent and be treated as ON, not as an explicit false.
        [JsonPropertyName("petUpdateCheck"), JsonPropertyOrder(39)]
        public bool? PetUpdateCheck;

        [JsonPropertyName("petUpdateLastCheckUtc"), JsonPropertyOrder(40)]
        public string PetUpdateLastCheckUtc;

        // "id;id", the catalog pets whose installed copy no longer matches the catalog hash.
        [JsonPropertyName("petUpdateStaleIds"), JsonPropertyOrder(41)]
        public string PetUpdateStaleIds;

        // Keep in sync with CompanionCatalog.BuiltInPetId (which AppSettingsStore can't reference — it compiles
        // into the SecureDownload-free CoreTests set).
        internal const string DefaultActivePetId = "eSheep";

        // System.Text.Json requires the extension-data sink to be a PROPERTY (a field is rejected). Kept
        // non-null with an Ordinal comparer so deserialization adds unknown fields into this instance
        // (preserving the comparer), which is what round-trips a future-schema doc's unknown data.
        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtensionData { get; set; } =
            new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        public static AppSettingsDocument CreateDefault()
        {
            return new AppSettingsDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                Volume = 0.3,
                ScaleLevel = 1,
                AutoStartPets = 1,
                MultiScreen = false,
                WindowForeground = false,
                StealTaskbarFocus = false,
                SpeechEnabled = true,
                SpeechDurationSeconds = 6,
                Xml = "",
                Images = "",
                Icon = "",
                Pets = new List<CompanionCountEntry>(),
                PetSizes = new List<CompanionSizeEntry>(),
                PetMonitors = new List<CompanionMonitorEntry>(),
                ThemeMode = "system",
                AudioDeviceId = "",
                MutedPets = new List<string>(),
                ActivePetId = DefaultActivePetId,
                SuppressRepeats = true,
                RandomDropEnabled = false,
                PetSoundsEnabled = true,
                NotificationSoundsEnabled = true,
                RandomDropMinutes = 15,
                RandomDropJitterMinutes = 3,
                MonthlyModuleUpdateCheck = true,
                DefaultSpeakingPet = "",
                AppUpdateCheck = true,
                AppUpdateLastCheckUtc = "",
                AppUpdateLatestVersion = "",
                ModuleUpdateLastCheckUtc = "",
                ModuleUpdateOffers = "",
                PetUpdateCheck = true,
                PetUpdateLastCheckUtc = "",
                PetUpdateStaleIds = "",
                TriggerSpeech = new List<TriggerSpeechEntry>()
            };
        }

        /// <summary>Upgrade known older schemas and clamp every externally persisted range.</summary>
        public bool Normalize()
        {
            bool changed = false;
            int originalSchema = SchemaVersion;

            if (SchemaVersion < CurrentSchemaVersion)   // covers legacy v0 and v1 docs
            {
                SchemaVersion = CurrentSchemaVersion;
                changed = true;
            }

            if (double.IsNaN(Volume) || double.IsInfinity(Volume))
            {
                Volume = 0.3;
                changed = true;
            }
            double volume = Math.Max(0.0, Math.Min(1.0, Volume));
            if (Math.Abs(volume - Volume) > double.Epsilon)
            {
                Volume = volume;
                changed = true;
            }
            int scale = ScalePolicy.ClampLevel(ScaleLevel);
            if (scale != ScaleLevel) { ScaleLevel = scale; changed = true; }
            if (ScalePercent != 0)                      // 0 = follow the legacy level; else clamp to 25..400
            {
                int pct = ScalePolicy.ClampPercent(ScalePercent);
                if (pct != ScalePercent) { ScalePercent = pct; changed = true; }
            }
            int pets = Math.Max(1, Math.Min(MaximumAutoStartPets, AutoStartPets));
            if (pets != AutoStartPets) { AutoStartPets = pets; changed = true; }
            int speech = Math.Max(2, Math.Min(30, SpeechDurationSeconds));
            if (speech != SpeechDurationSeconds)
            {
                SpeechDurationSeconds = speech;
                changed = true;
            }
            changed |= NormalizePayload(ref Xml, MaximumXmlBytes, MaximumXmlBytes);
            changed |= NormalizePayload(
                ref Images,
                MaximumLegacyImageCharacters,
                MaximumLegacyImageCharacters);
            changed |= NormalizePayload(
                ref Icon,
                MaximumLegacyIconCharacters,
                MaximumLegacyIconCharacters);

            // Upgrading a pre-v2 doc: seed the on-screen pet mix from the old single count so the first
            // launch after upgrade restores the same number of pets (id "" = the active/default pet).
            // AutoStartPets is already clamped above.
            if (originalSchema < 2 && (Pets == null || Pets.Count == 0))
            {
                Pets = new List<CompanionCountEntry> { new CompanionCountEntry { Id = "", Count = AutoStartPets } };
                changed = true;
            }
            changed |= NormalizePetMix();
            changed |= NormalizePetSizes();
            changed |= NormalizeTriggerSpeech();
            string theme = NormalizeThemeMode(ThemeMode);
            if (!string.Equals(theme, ThemeMode, StringComparison.Ordinal)) { ThemeMode = theme; changed = true; }
            string device = NormalizeAudioDeviceId(AudioDeviceId);
            if (!string.Equals(device, AudioDeviceId, StringComparison.Ordinal)) { AudioDeviceId = device; changed = true; }
            changed |= NormalizeMutedPets();
            string active = NormalizeActivePetId(ActivePetId);
            if (!string.Equals(active, ActivePetId, StringComparison.Ordinal)) { ActivePetId = active; changed = true; }
            changed |= NormalizeRandomDrop();
            return changed;
        }

        // Clamp the random-drop cadence to the same bounds the old AiSettings enforced (center 1..9999,
        // jitter 0..center-1 so the interval stays positive). Only touches present values — null means
        // "absent, awaiting LocalData's one-time migration" and is deliberately left null.
        private bool NormalizeRandomDrop()
        {
            bool changed = false;
            if (RandomDropMinutes.HasValue)
            {
                int m = Math.Max(1, Math.Min(9999, RandomDropMinutes.Value));
                if (m != RandomDropMinutes.Value) { RandomDropMinutes = m; changed = true; }
            }
            if (RandomDropJitterMinutes.HasValue)
            {
                int center = RandomDropMinutes ?? 15;
                int j = Math.Max(0, Math.Min(center - 1, RandomDropJitterMinutes.Value));
                if (j != RandomDropJitterMinutes.Value) { RandomDropJitterMinutes = j; changed = true; }
            }
            return changed;
        }

        // The active pet id must name a real pet, so an empty/unsafe value falls back to the built-in
        // (unlike the "" that means "the active slot" in the pet mix / muted list).
        internal static string NormalizeActivePetId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return DefaultActivePetId;
            id = id.Trim();
            if (id.Length > MaximumPetIdLength) return DefaultActivePetId;
            foreach (char c in id)
                if (c == '/' || c == '\\' || c == ':' || char.IsControl(c)) return DefaultActivePetId;
            return id;
        }

        // Validate the muted-pets list: drop null/unsafe ids, dedupe (case-insensitive), cap the count.
        private bool NormalizeMutedPets()
        {
            List<string> original = MutedPets;
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (MutedPets != null)
                foreach (string raw in MutedPets)
                {
                    string id = raw ?? "";
                    if (!IsAcceptablePetId(id) || !seen.Add(id)) continue;
                    if (result.Count >= MaximumPetSizeEntries) break;
                    result.Add(id);
                }
            MutedPets = result;
            if (original == null) return true;
            if (original.Count != result.Count) return true;
            for (int i = 0; i < result.Count; i++)
                if (!string.Equals(original[i] ?? "", result[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Normalize the audio device id: null/whitespace/over-long -> "" (the default device).</summary>
        internal static string NormalizeAudioDeviceId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "";
            id = id.Trim();
            return id.Length > 128 ? "" : id;
        }

        /// <summary>Clamp the settings-window theme to one of "system" / "light" / "dark" (default system).</summary>
        internal static string NormalizeThemeMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return "system";
            switch (mode.Trim().ToLowerInvariant())
            {
                case "light": return "light";
                case "dark": return "dark";
                default: return "system";
            }
        }

        // Validate the persisted pet mix on every load: drop null/unsafe-id entries, clamp each count
        // to [1, MaximumOnScreenPets], dedupe by id (summing counts), then cap the running total across
        // all types to MaximumOnScreenPets. Deliberately does NOT call SecureDownload.IsSafeId (the core
        // regression test project does not compile it); the runtime load path applies the full safe-id
        // check where files are actually opened. id "" (the active/default pet) is allowed.
        private bool NormalizePetMix()
        {
            List<CompanionCountEntry> original = Pets;
            var merged = new List<CompanionCountEntry>();
            var indexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (Pets != null)
            {
                foreach (CompanionCountEntry entry in Pets)
                {
                    if (entry == null) continue;
                    string id = entry.Id ?? "";
                    if (!IsAcceptablePetId(id)) continue;
                    int count = Math.Max(1, Math.Min(MaximumOnScreenPets, entry.Count));
                    int existing;
                    if (indexById.TryGetValue(id, out existing))
                        merged[existing].Count = Math.Min(
                            MaximumOnScreenPets,
                            merged[existing].Count + count);
                    else
                    {
                        indexById[id] = merged.Count;
                        merged.Add(new CompanionCountEntry { Id = id, Count = count });
                    }
                }
            }

            var result = new List<CompanionCountEntry>();
            int total = 0;
            foreach (CompanionCountEntry entry in merged)
            {
                if (total >= MaximumOnScreenPets) break;
                int count = Math.Min(entry.Count, MaximumOnScreenPets - total);
                result.Add(new CompanionCountEntry { Id = entry.Id, Count = count });
                total += count;
            }

            Pets = result;
            return !PetMixEquals(original, result);
        }

        private static bool IsAcceptablePetId(string id)
        {
            if (id == null) return false;
            if (id.Length == 0) return true;                 // the active/default pet
            if (id.Length > MaximumPetIdLength) return false;
            foreach (char c in id)
                if (c == '/' || c == '\\' || c == ':' || char.IsControl(c)) return false;
            return true;
        }

        internal static bool PetMixEquals(List<CompanionCountEntry> a, List<CompanionCountEntry> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                CompanionCountEntry x = a[i], y = b[i];
                if (x == null || y == null) return false;
                if (!string.Equals(x.Id ?? "", y.Id ?? "", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (x.Count != y.Count) return false;
            }
            return true;
        }

        internal static List<CompanionCountEntry> ClonePetMix(List<CompanionCountEntry> source)
        {
            if (source == null) return null;
            var copy = new List<CompanionCountEntry>(source.Count);
            foreach (CompanionCountEntry entry in source)
                copy.Add(entry == null ? null : new CompanionCountEntry { Id = entry.Id, Count = entry.Count });
            return copy;
        }

        // Validate the per-pet size overrides on every load: drop null/unsafe-id entries and any whose
        // level is outside the valid range (out of range means "no override" -> follow the global size),
        // dedupe by id (last wins), then cap the list. Mirrors NormalizePetMix; id "" (active pet) allowed.
        private bool NormalizePetSizes()
        {
            List<CompanionSizeEntry> original = PetSizes;
            var merged = new List<CompanionSizeEntry>();
            var indexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (PetSizes != null)
            {
                foreach (CompanionSizeEntry entry in PetSizes)
                {
                    if (entry == null) continue;
                    string id = entry.Id ?? "";
                    if (!IsAcceptablePetId(id)) continue;
                    bool hasLevel = entry.Level >= ScalePolicy.MinimumLevel &&
                                    entry.Level <= ScalePolicy.MaximumLevel;
                    bool hasPercent = entry.Percent >= ScalePolicy.MinimumPercent &&
                                      entry.Percent <= ScalePolicy.MaximumPercent;
                    if (!hasLevel && !hasPercent) continue;                 // absence = follow global
                    int level = hasLevel ? entry.Level : 0;
                    int percent = hasPercent ? entry.Percent : 0;
                    int existing;
                    if (indexById.TryGetValue(id, out existing))
                    {
                        merged[existing].Level = level;                     // last wins
                        merged[existing].Percent = percent;
                    }
                    else
                    {
                        indexById[id] = merged.Count;
                        merged.Add(new CompanionSizeEntry { Id = id, Level = level, Percent = percent });
                    }
                }
            }

            List<CompanionSizeEntry> result = merged.Count > MaximumPetSizeEntries
                ? merged.GetRange(0, MaximumPetSizeEntries)
                : merged;
            PetSizes = result;
            return !PetSizesEqual(original, result);
        }

        internal static bool PetMonitorsEqual(List<CompanionMonitorEntry> a, List<CompanionMonitorEntry> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return (a == null ? 0 : a.Count) == (b == null ? 0 : b.Count);
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                CompanionMonitorEntry x = a[i], y = b[i];
                if (x == null || y == null) { if (x != y) return false; continue; }
                if (!string.Equals(x.Id, y.Id, StringComparison.Ordinal) || x.Display != y.Display) return false;
            }
            return true;
        }

        internal static List<CompanionMonitorEntry> ClonePetMonitors(List<CompanionMonitorEntry> source)
        {
            var result = new List<CompanionMonitorEntry>();
            if (source == null) return result;
            foreach (CompanionMonitorEntry entry in source)
                if (entry != null && !string.IsNullOrEmpty(entry.Id))
                    result.Add(new CompanionMonitorEntry { Id = entry.Id, Display = entry.Display });
            return result;
        }
        internal static bool PetSizesEqual(List<CompanionSizeEntry> a, List<CompanionSizeEntry> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                CompanionSizeEntry x = a[i], y = b[i];
                if (x == null || y == null) return false;
                if (!string.Equals(x.Id ?? "", y.Id ?? "", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (x.Level != y.Level) return false;
                if (x.Percent != y.Percent) return false;
            }
            return true;
        }

        internal static List<CompanionSizeEntry> ClonePetSizes(List<CompanionSizeEntry> source)
        {
            if (source == null) return null;
            var copy = new List<CompanionSizeEntry>(source.Count);
            foreach (CompanionSizeEntry entry in source)
                copy.Add(entry == null ? null : new CompanionSizeEntry { Id = entry.Id, Level = entry.Level, Percent = entry.Percent });
            return copy;
        }

        // Trigger-speech entries mirror the per-pet-size list exactly (bounded, de-duplicated by id with
        // last-wins, dropped when the id is unacceptable). The module id itself is only length-bounded here,
        // not checked against installed modules: a module can be uninstalled and later reinstalled, and
        // silently dropping the choice in between would lose the user's setting. An unresolvable choice is
        // handled at read time (falls back to the default) instead.
        private bool NormalizeTriggerSpeech()
        {
            List<TriggerSpeechEntry> original = TriggerSpeech;
            var merged = new List<TriggerSpeechEntry>();
            var indexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (TriggerSpeech != null)
            {
                foreach (TriggerSpeechEntry entry in TriggerSpeech)
                {
                    if (entry == null) continue;
                    string id = entry.Id ?? "";
                    // "" is the valid global/all-pets key here (unlike PetSizes, where "" is meaningless).
                    if (id.Length > 0 && !IsAcceptablePetId(id)) continue;
                    string module = (entry.Module ?? "").Trim();
                    if (module.Length > 64) module = module.Substring(0, 64);
                    int existing;
                    if (indexById.TryGetValue(id, out existing))
                        merged[existing].Module = module;                      // last wins
                    else
                    {
                        indexById[id] = merged.Count;
                        merged.Add(new TriggerSpeechEntry { Id = id, Module = module });
                    }
                }
            }

            List<TriggerSpeechEntry> result = merged.Count > MaximumPetSizeEntries
                ? merged.GetRange(0, MaximumPetSizeEntries)
                : merged;
            TriggerSpeech = result;
            return !TriggerSpeechEqual(original, result);
        }

        internal static bool TriggerSpeechEqual(List<TriggerSpeechEntry> a, List<TriggerSpeechEntry> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                TriggerSpeechEntry x = a[i], y = b[i];
                if (x == null || y == null) return false;
                if (!string.Equals(x.Id ?? "", y.Id ?? "", StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.Equals(x.Module ?? "", y.Module ?? "", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        internal static List<TriggerSpeechEntry> CloneTriggerSpeech(List<TriggerSpeechEntry> source)
        {
            if (source == null) return null;
            var copy = new List<TriggerSpeechEntry>(source.Count);
            foreach (TriggerSpeechEntry entry in source)
                copy.Add(entry == null ? null : new TriggerSpeechEntry { Id = entry.Id, Module = entry.Module });
            return copy;
        }

        private static bool NormalizePayload(
            ref string value,
            int maximumCharacters,
            int maximumBytes)
        {
            string original = value;
            value = value ?? "";
            try
            {
                if (value.Length > maximumCharacters ||
                    new UTF8Encoding(false, true).GetByteCount(value) > maximumBytes)
                    value = "";
            }
            catch (EncoderFallbackException)
            {
                value = "";
            }
            return !string.Equals(original, value, StringComparison.Ordinal);
        }
    }

    /// <summary>One entry in the on-screen pet mix: a pet type id and how many of it to show.</summary>
    internal sealed class CompanionCountEntry
    {
        [JsonPropertyName("id")]
        public string Id;

        [JsonPropertyName("count")]
        public int Count;
    }

    /// <summary>One per-pet size override: a pet type id and its scale level (1/2/3).</summary>
    /// <summary>A pet TYPE pinned to one monitor. Absent = unpinned (spawn anywhere, relocate if covered).</summary>
    internal sealed class CompanionMonitorEntry
    {
        [JsonPropertyName("id")]
        public string Id;

        /// <summary>Zero-based index into Screen.AllScreens. Validated at READ time, never at write time:
        /// monitors are unplugged, and a pin to a screen that is gone must degrade to "unpinned" rather than
        /// hide the pet for ever on a display that does not exist.</summary>
        [JsonPropertyName("display")]
        public int Display;
    }
    internal sealed class CompanionSizeEntry
    {
        [JsonPropertyName("id")]
        public string Id;

        [JsonPropertyName("level")]
        public int Level;

        // Fractional size as a PERCENT (25..400). 0 = fall back to Level above. New; older docs carry none.
        [JsonPropertyName("percent")]
        public int Percent;
    }

    /// <summary>One poke-speech source choice: a pet type id ("" = all pets, the only id today's UI writes)
    /// and the module id that should speak its first poke ("" = default &amp; random). Per-pet ids are
    /// reserved for the planned per-pet-voice work (BACKLOG #16) — the shape is here now so that lands
    /// without a settings migration.</summary>
    internal sealed class TriggerSpeechEntry
    {
        [JsonPropertyName("id")]
        public string Id;

        [JsonPropertyName("module")]
        public string Module;
    }

    /// <summary>
    /// Atomic JSON settings persistence with a previous-version backup, corrupt-file preservation,
    /// schema migration, and one-time import from the historical DesktopAICompanion.config formats.
    /// </summary>
    internal sealed class AppSettingsStore
    {
        private const int MaximumSettingsFileBytes = 12 * 1024 * 1024;
        private const int ProcessLockTimeoutMilliseconds = 10000;
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        // The document POCO persists via public FIELDS, so IncludeFields is required (STJ ignores fields
        // otherwise -> a silent empty write). MaxDepth mirrors the old JsonTextReader bound; WriteIndented
        // matches the previous Formatting.Indented; the relaxed encoder keeps raw XML/base64 in xml/images/
        // icon readable (not \uXXXX-escaped). Default null handling is kept on purpose: absent nullable keys
        // load as null and null values are written explicitly, preserving the absent-vs-null distinction the
        // nullable settings (suppressRepeats / randomDrop*) rely on.
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true,
            WriteIndented = true,
            MaxDepth = 32,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private enum ReadResult
        {
            Missing,
            Loaded,
            Unreadable,
            FutureSchema
        }

        private static readonly object ProcessLock = new object();

        private readonly string _filePath;
        private readonly string _backupPath;
        private readonly string _mutexName;
        private readonly IList<string> _legacyFiles;
        private readonly int _processLockTimeoutMilliseconds;
        private bool _writesBlockedByFutureSchema;
        private bool _writesBlockedByLoadFailure;
        private AppSettingsDocument _baseline;

        public string FilePath { get { return _filePath; } }
        public string BackupPath { get { return _backupPath; } }
        public string LastRecoveryFile { get; private set; }
        public string LastLoadWarning { get; private set; }
        public bool IsReadOnlyFallback { get { return _writesBlockedByLoadFailure; } }

        public AppSettingsStore(string filePath, IEnumerable<string> legacyFiles)
            : this(
                filePath,
                legacyFiles,
                ProcessLockTimeoutMilliseconds)
        {
        }

        internal AppSettingsStore(
            string filePath,
            IEnumerable<string> legacyFiles,
            int processLockTimeoutMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Settings path is required.", "filePath");
            if (!AppPaths.IsFullyQualifiedPath(filePath))
                throw new ArgumentException("Settings path must be absolute.", "filePath");
            if (processLockTimeoutMilliseconds < 1 ||
                processLockTimeoutMilliseconds > 60 * 60 * 1000)
                throw new ArgumentOutOfRangeException(
                    "processLockTimeoutMilliseconds");

            _filePath = Path.GetFullPath(filePath);
            _backupPath = _filePath + ".bak";
            _mutexName = BuildMutexName(_filePath);
            _processLockTimeoutMilliseconds =
                processLockTimeoutMilliseconds;
            _legacyFiles = new List<string>();
            if (legacyFiles != null)
            {
                foreach (string path in legacyFiles)
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    try { AddUnique(_legacyFiles, Path.GetFullPath(path)); }
                    catch { }
                }
            }
        }

        public AppSettingsDocument Load()
        {
            lock (ProcessLock)
            {
                LastLoadWarning = null;
                try
                {
                    AppSettingsDocument loaded =
                        WithFileLock(LoadCore);
                    _writesBlockedByLoadFailure = false;
                    return loaded;
                }
                catch (Exception ex)
                {
                    _writesBlockedByLoadFailure = true;
                    AppSettingsDocument fallback =
                        Clone(_baseline) ??
                        AppSettingsDocument.CreateDefault();
                    fallback.Normalize();
                    LastLoadWarning =
                        "Settings storage is unavailable. DesktopAICompanion is using " +
                        "an in-memory read-only fallback for this session. " +
                        ex.Message;
                    return fallback;
                }
            }
        }

        public bool Save(AppSettingsDocument settings)
        {
            if (settings == null) return false;
            lock (ProcessLock)
            {
                if (_writesBlockedByLoadFailure) return false;
                try
                {
                    return WithFileLock(delegate { return SaveMerged(settings); });
                }
                catch
                {
                    return false;
                }
            }
        }

        private AppSettingsDocument LoadCore()
        {
            _writesBlockedByFutureSchema = false;
            AppSettingsDocument loaded;
            bool changed;

            ReadResult primaryResult = TryRead(_filePath, out loaded, out changed);
            if (primaryResult == ReadResult.Loaded)
            {
                if (changed)
                    SaveDuringLoad(
                        loaded,
                        "Normalized settings could not be persisted.");
                _baseline = Clone(loaded);
                return loaded;
            }
            if (primaryResult == ReadResult.FutureSchema)
            {
                _writesBlockedByFutureSchema = true;
                _baseline = Clone(loaded);
                return loaded;
            }

            if (primaryResult == ReadResult.Unreadable)
                PreserveCorruptPrimary();

            ReadResult backupResult = TryRead(_backupPath, out loaded, out changed);
            if (backupResult == ReadResult.Loaded)
            {
                SaveDuringLoad(
                    loaded,
                    "Recovered settings could not be restored to the primary file.");
                _baseline = Clone(loaded);
                return loaded;
            }
            if (backupResult == ReadResult.FutureSchema)
            {
                _writesBlockedByFutureSchema = true;
                _baseline = Clone(loaded);
                return loaded;
            }

            if (LegacySettingsReader.TryRead(_legacyFiles, out loaded))
            {
                loaded.Normalize();
                SaveDuringLoad(
                    loaded,
                    "Migrated settings could not be persisted.");
                _baseline = Clone(loaded);
                return loaded;
            }

            loaded = AppSettingsDocument.CreateDefault();
            SaveDuringLoad(
                loaded,
                "Default settings could not be persisted.");
            _baseline = Clone(loaded);
            return loaded;
        }

        private void SaveDuringLoad(
            AppSettingsDocument settings,
            string warning)
        {
            if (!SaveCore(settings) &&
                string.IsNullOrEmpty(LastLoadWarning))
                LastLoadWarning = warning;
        }

        private bool SaveMerged(AppSettingsDocument settings)
        {
            if (_writesBlockedByFutureSchema ||
                settings.SchemaVersion > AppSettingsDocument.CurrentSchemaVersion)
                return false;

            AppSettingsDocument existing;
            bool changed;
            ReadResult result = TryRead(_filePath, out existing, out changed);
            if (result == ReadResult.FutureSchema)
            {
                _writesBlockedByFutureSchema = true;
                return false;
            }

            settings.Normalize();
            AppSettingsDocument target =
                result == ReadResult.Loaded ? existing : Clone(settings);
            if (result == ReadResult.Loaded)
            {
                target.Normalize();
                MergeChangedFields(settings, _baseline, target);
            }

            if (!SaveCore(target)) return false;

            // The baseline follows the values this caller observed/wrote, not unrelated fields
            // merged from another process. A later save therefore changes only newly edited fields.
            _baseline = Clone(settings);
            return true;
        }

        private ReadResult TryRead(
            string path,
            out AppSettingsDocument settings,
            out bool changed)
        {
            settings = null;
            changed = false;
            try
            {
                if (!File.Exists(path)) return ReadResult.Missing;
                string json = ReadBoundedUtf8(path, MaximumSettingsFileBytes);
                settings = JsonSerializer.Deserialize<AppSettingsDocument>(json, JsonOptions);
                if (settings == null) return ReadResult.Unreadable;
                if (settings.SchemaVersion > AppSettingsDocument.CurrentSchemaVersion)
                {
                    // Use the fields this version understands for the current session, but never
                    // rewrite a document owned by a newer version and discard its unknown data.
                    settings.Normalize();
                    return ReadResult.FutureSchema;
                }
                changed = settings.Normalize();
                return ReadResult.Loaded;
            }
            catch
            {
                settings = null;
                return ReadResult.Unreadable;
            }
        }

        private bool SaveCore(AppSettingsDocument settings)
        {
            try
            {
                string json = JsonSerializer.Serialize(settings, JsonOptions);
                if (StrictUtf8.GetByteCount(json) > MaximumSettingsFileBytes)
                    return false;
                return AtomicFile.TryWriteAllText(_filePath, json, _backupPath);
            }
            catch
            {
                return false;
            }
        }

        private static void MergeChangedFields(
            AppSettingsDocument current,
            AppSettingsDocument baseline,
            AppSettingsDocument target)
        {
            bool all = baseline == null;
            if (all || current.SchemaVersion != baseline.SchemaVersion)
                target.SchemaVersion = current.SchemaVersion;
            if (all || current.Volume != baseline.Volume)
                target.Volume = current.Volume;
            if (all || current.ScaleLevel != baseline.ScaleLevel)
                target.ScaleLevel = current.ScaleLevel;
            if (all || current.AutoStartPets != baseline.AutoStartPets)
                target.AutoStartPets = current.AutoStartPets;
            if (all || current.MultiScreen != baseline.MultiScreen)
                target.MultiScreen = current.MultiScreen;
            if (all || current.WindowForeground != baseline.WindowForeground)
                target.WindowForeground = current.WindowForeground;
            if (all || current.StealTaskbarFocus != baseline.StealTaskbarFocus)
                target.StealTaskbarFocus = current.StealTaskbarFocus;
            if (all || current.SpeechEnabled != baseline.SpeechEnabled)
                target.SpeechEnabled = current.SpeechEnabled;
            if (all || current.SpeechDurationSeconds != baseline.SpeechDurationSeconds)
                target.SpeechDurationSeconds = current.SpeechDurationSeconds;
            if (all || !string.Equals(current.Xml, baseline.Xml, StringComparison.Ordinal))
                target.Xml = current.Xml;
            if (all || !string.Equals(current.Images, baseline.Images, StringComparison.Ordinal))
                target.Images = current.Images;
            if (all || !string.Equals(current.Icon, baseline.Icon, StringComparison.Ordinal))
                target.Icon = current.Icon;
            if (all || !AppSettingsDocument.PetMixEquals(current.Pets, baseline.Pets))
                target.Pets = AppSettingsDocument.ClonePetMix(current.Pets);
            if (all || !AppSettingsDocument.PetSizesEqual(current.PetSizes, baseline.PetSizes))
                target.PetSizes = AppSettingsDocument.ClonePetSizes(current.PetSizes);
            if (all || !AppSettingsDocument.PetMonitorsEqual(current.PetMonitors, baseline.PetMonitors))
                target.PetMonitors = AppSettingsDocument.ClonePetMonitors(current.PetMonitors);
            if (all || !string.Equals(current.ThemeMode, baseline.ThemeMode, StringComparison.Ordinal))
                target.ThemeMode = current.ThemeMode;
            if (all || !string.Equals(current.AudioDeviceId, baseline.AudioDeviceId, StringComparison.Ordinal))
                target.AudioDeviceId = current.AudioDeviceId;
            if (all || !StringListEquals(current.MutedPets, baseline.MutedPets))
                target.MutedPets = current.MutedPets == null ? null : new List<string>(current.MutedPets);
            if (all || !string.Equals(current.ActivePetId, baseline.ActivePetId, StringComparison.Ordinal))
                target.ActivePetId = current.ActivePetId;
            if (all || current.SuppressRepeats != baseline.SuppressRepeats)
                target.SuppressRepeats = current.SuppressRepeats;
            if (all || current.RandomDropEnabled != baseline.RandomDropEnabled)
                target.RandomDropEnabled = current.RandomDropEnabled;
            if (all || current.PetSoundsEnabled != baseline.PetSoundsEnabled)
                target.PetSoundsEnabled = current.PetSoundsEnabled;
            if (all || current.NotificationSoundsEnabled != baseline.NotificationSoundsEnabled)
                target.NotificationSoundsEnabled = current.NotificationSoundsEnabled;
            if (all || current.RandomDropMinutes != baseline.RandomDropMinutes)
                target.RandomDropMinutes = current.RandomDropMinutes;
            if (all || current.RandomDropJitterMinutes != baseline.RandomDropJitterMinutes)
                target.RandomDropJitterMinutes = current.RandomDropJitterMinutes;
            if (all || current.MonthlyModuleUpdateCheck != baseline.MonthlyModuleUpdateCheck)
                target.MonthlyModuleUpdateCheck = current.MonthlyModuleUpdateCheck;
            if (all || !string.Equals(current.DefaultSpeakingPet, baseline.DefaultSpeakingPet, StringComparison.Ordinal))
                target.DefaultSpeakingPet = current.DefaultSpeakingPet;
            if (all || current.AppUpdateCheck != baseline.AppUpdateCheck)
                target.AppUpdateCheck = current.AppUpdateCheck;
            if (all || !string.Equals(current.AppUpdateLastCheckUtc, baseline.AppUpdateLastCheckUtc, StringComparison.Ordinal))
                target.AppUpdateLastCheckUtc = current.AppUpdateLastCheckUtc;
            if (all || !string.Equals(current.AppUpdateLatestVersion, baseline.AppUpdateLatestVersion, StringComparison.Ordinal))
                target.AppUpdateLatestVersion = current.AppUpdateLatestVersion;
            if (all || !string.Equals(current.ModuleUpdateLastCheckUtc, baseline.ModuleUpdateLastCheckUtc, StringComparison.Ordinal))
                target.ModuleUpdateLastCheckUtc = current.ModuleUpdateLastCheckUtc;
            if (all || !string.Equals(current.ModuleUpdateOffers, baseline.ModuleUpdateOffers, StringComparison.Ordinal))
                target.ModuleUpdateOffers = current.ModuleUpdateOffers;
            if (all || current.PetUpdateCheck != baseline.PetUpdateCheck)
                target.PetUpdateCheck = current.PetUpdateCheck;
            if (all || !string.Equals(current.PetUpdateLastCheckUtc, baseline.PetUpdateLastCheckUtc, StringComparison.Ordinal))
                target.PetUpdateLastCheckUtc = current.PetUpdateLastCheckUtc;
            if (all || !string.Equals(current.PetUpdateStaleIds, baseline.PetUpdateStaleIds, StringComparison.Ordinal))
                target.PetUpdateStaleIds = current.PetUpdateStaleIds;
            if (all || !AppSettingsDocument.TriggerSpeechEqual(current.TriggerSpeech, baseline.TriggerSpeech))
                target.TriggerSpeech = AppSettingsDocument.CloneTriggerSpeech(current.TriggerSpeech);
        }

        internal static bool StringListEquals(List<string> a, List<string> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i] ?? "", b[i] ?? "", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        internal static AppSettingsDocument Clone(AppSettingsDocument source)
        {
            if (source == null) return null;
            Dictionary<string, JsonElement> extension = null;
            if (source.ExtensionData != null)
            {
                extension = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, JsonElement> item in source.ExtensionData)
                    extension[item.Key] = item.Value.Clone();
            }

            return new AppSettingsDocument
            {
                SchemaVersion = source.SchemaVersion,
                Volume = source.Volume,
                ScaleLevel = source.ScaleLevel,
                AutoStartPets = source.AutoStartPets,
                MultiScreen = source.MultiScreen,
                WindowForeground = source.WindowForeground,
                StealTaskbarFocus = source.StealTaskbarFocus,
                SpeechEnabled = source.SpeechEnabled,
                SpeechDurationSeconds = source.SpeechDurationSeconds,
                Xml = source.Xml,
                Images = source.Images,
                Icon = source.Icon,
                Pets = AppSettingsDocument.ClonePetMix(source.Pets),
                PetSizes = AppSettingsDocument.ClonePetSizes(source.PetSizes),
                PetMonitors = AppSettingsDocument.ClonePetMonitors(source.PetMonitors),
                ThemeMode = source.ThemeMode,
                AudioDeviceId = source.AudioDeviceId,
                MutedPets = source.MutedPets == null ? null : new List<string>(source.MutedPets),
                ActivePetId = source.ActivePetId,
                SuppressRepeats = source.SuppressRepeats,
                RandomDropEnabled = source.RandomDropEnabled,
                PetSoundsEnabled = source.PetSoundsEnabled,
                NotificationSoundsEnabled = source.NotificationSoundsEnabled,
                RandomDropMinutes = source.RandomDropMinutes,
                RandomDropJitterMinutes = source.RandomDropJitterMinutes,
                MonthlyModuleUpdateCheck = source.MonthlyModuleUpdateCheck,
                DefaultSpeakingPet = source.DefaultSpeakingPet,
                AppUpdateCheck = source.AppUpdateCheck,
                AppUpdateLastCheckUtc = source.AppUpdateLastCheckUtc,
                AppUpdateLatestVersion = source.AppUpdateLatestVersion,
                ModuleUpdateLastCheckUtc = source.ModuleUpdateLastCheckUtc,
                ModuleUpdateOffers = source.ModuleUpdateOffers,
                PetUpdateCheck = source.PetUpdateCheck,
                PetUpdateLastCheckUtc = source.PetUpdateLastCheckUtc,
                PetUpdateStaleIds = source.PetUpdateStaleIds,
                TriggerSpeech = AppSettingsDocument.CloneTriggerSpeech(source.TriggerSpeech),
                ExtensionData = extension
            };
        }

        private T WithFileLock<T>(Func<T> action)
        {
            using (CrossSessionLock.Acquire(
                _mutexName,
                _filePath,
                _processLockTimeoutMilliseconds,
                "settings"))
                return action();
        }

        private static string BuildMutexName(string path)
        {
            return CrossSessionLock.BuildGlobalMutexName("AppSettings", path);
        }

        private static string ReadBoundedUtf8(string path, int maximumBytes)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                8192,
                FileOptions.SequentialScan))
            {
                if (stream.Length > maximumBytes)
                    throw new InvalidDataException("Settings file exceeds its size limit.");
                using (var memory = new MemoryStream((int)stream.Length))
                {
                    byte[] buffer = new byte[8192];
                    int total = 0;
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total = checked(total + read);
                        if (total > maximumBytes)
                            throw new InvalidDataException(
                                "Settings file exceeds its size limit.");
                        memory.Write(buffer, 0, read);
                    }
                    return StrictUtf8.GetString(memory.ToArray());
                }
            }
        }

        private void PreserveCorruptPrimary()
        {
            try
            {
                string directory = Path.GetDirectoryName(_filePath);
                Directory.CreateDirectory(directory);
                string name = Path.GetFileNameWithoutExtension(_filePath) +
                    ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) +
                    "-" + Guid.NewGuid().ToString("N") + Path.GetExtension(_filePath);
                string recovery = Path.Combine(directory, name);
                File.Copy(_filePath, recovery, false);
                File.Delete(_filePath);
                LastRecoveryFile = recovery;
            }
            catch
            {
                // Keep the unreadable primary untouched if it cannot be preserved safely.
            }
        }

        private static void AddUnique(ICollection<string> values, string candidate)
        {
            foreach (string value in values)
            {
                if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            values.Add(candidate);
        }
    }

    /// <summary>
    /// Coordinates durable per-user files across console and RDP sessions. A Global named mutex
    /// with a current-user ACL provides the normal fast path, while a same-directory file lease is
    /// always held as the cross-session fail-safe when the Global namespace is restricted.
    /// </summary>
    internal static class CrossSessionLock
    {
        private const int RetryMilliseconds = 25;

        public static string BuildGlobalMutexName(string category, string path)
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("A lock category is required.", "category");
            string normalized = Path.GetFullPath(path).ToUpperInvariant();
            string user = CurrentUserSid();
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(
                    Encoding.UTF8.GetBytes(user + "\n" + normalized));
                var suffix = new StringBuilder(32);
                for (int index = 0; index < 16; index++)
                    suffix.Append(
                        hash[index].ToString("x2", CultureInfo.InvariantCulture));
                return @"Global\DesktopAICompanion." + category + "." + suffix;
            }
        }

        public static IDisposable Acquire(
            string mutexName,
            string dataPath,
            int timeoutMilliseconds,
            string description)
        {
            IDisposable lease = TryAcquire(
                mutexName,
                dataPath,
                timeoutMilliseconds);
            if (lease == null)
                throw new IOException(
                    "Timed out waiting for the " +
                    (string.IsNullOrWhiteSpace(description)
                        ? "application data"
                        : description) +
                    " lock.");
            return lease;
        }

        public static IDisposable TryAcquire(
            string mutexName,
            string dataPath,
            int timeoutMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(mutexName) ||
                !mutexName.StartsWith(@"Global\", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(dataPath) ||
                timeoutMilliseconds < 0)
                return null;

            var stopwatch = Stopwatch.StartNew();
            bool mutexAvailable;
            IDisposable mutexLease = TryAcquireGlobalMutex(
                mutexName,
                timeoutMilliseconds,
                out mutexAvailable);
            if (mutexAvailable && mutexLease == null)
                return null;

            int remaining = RemainingMilliseconds(
                timeoutMilliseconds,
                stopwatch.ElapsedMilliseconds);
            IDisposable fileLease = TryAcquireFileLease(
                dataPath + ".lock",
                remaining);
            if (fileLease == null)
            {
                if (mutexLease != null) mutexLease.Dispose();
                return null;
            }
            return new CompositeLease(fileLease, mutexLease);
        }

        private static IDisposable TryAcquireGlobalMutex(
            string name,
            int timeoutMilliseconds,
            out bool available)
        {
            available = false;
            Mutex mutex = null;
            try
            {
                bool created;
                try
                {
                    // net10: the net48 `new Mutex(bool,string,out bool,MutexSecurity)` overload moved
                    // to MutexAcl.Create (System.Threading), preserving the current-user ACL intent.
                    mutex = MutexAcl.Create(
                        false,
                        name,
                        out created,
                        CurrentUserMutexSecurity());
                }
                catch (UnauthorizedAccessException)
                {
                    mutex = MutexAcl.OpenExisting(
                        name,
                        MutexRights.Synchronize | MutexRights.Modify);
                }
                available = true;
                bool acquired;
                try
                {
                    acquired = mutex.WaitOne(timeoutMilliseconds);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
                if (!acquired)
                {
                    mutex.Dispose();
                    return null;
                }
                return new MutexLease(mutex);
            }
            catch (Exception ex)
            {
                if (mutex != null) mutex.Dispose();
                if (ex is OutOfMemoryException) throw;
                available = false;
                return null;
            }
        }

        private static IDisposable TryAcquireFileLease(
            string lockPath,
            int timeoutMilliseconds)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    string directory = Path.GetDirectoryName(
                        Path.GetFullPath(lockPath));
                    if (string.IsNullOrEmpty(directory)) return null;
                    Directory.CreateDirectory(directory);
                    return new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        1,
                        FileOptions.None);
                }
                catch (IOException)
                {
                    if (stopwatch.ElapsedMilliseconds >= timeoutMilliseconds)
                        return null;
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }
                Thread.Sleep(Math.Min(
                    RetryMilliseconds,
                    Math.Max(
                        1,
                        RemainingMilliseconds(
                            timeoutMilliseconds,
                            stopwatch.ElapsedMilliseconds))));
            }
        }

        private static int RemainingMilliseconds(int timeout, long elapsed)
        {
            if (elapsed >= timeout) return 0;
            return (int)Math.Min(int.MaxValue, timeout - elapsed);
        }

        private static MutexSecurity CurrentUserMutexSecurity()
        {
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
            if (user == null)
                throw new UnauthorizedAccessException(
                    "The current Windows user has no security identifier.");
            var security = new MutexSecurity();
            security.AddAccessRule(new MutexAccessRule(
                user,
                MutexRights.Synchronize | MutexRights.Modify,
                AccessControlType.Allow));
            return security;
        }

        private static string CurrentUserSid()
        {
            try
            {
                SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
                return user == null ? "" : user.Value;
            }
            catch
            {
                return "";
            }
        }

        private sealed class MutexLease : IDisposable
        {
            private Mutex _mutex;

            public MutexLease(Mutex mutex)
            {
                _mutex = mutex;
            }

            public void Dispose()
            {
                Mutex mutex = Interlocked.Exchange(ref _mutex, null);
                if (mutex == null) return;
                try { mutex.ReleaseMutex(); }
                finally { mutex.Dispose(); }
            }
        }

        private sealed class CompositeLease : IDisposable
        {
            private IDisposable _fileLease;
            private IDisposable _mutexLease;

            public CompositeLease(
                IDisposable fileLease,
                IDisposable mutexLease)
            {
                _fileLease = fileLease;
                _mutexLease = mutexLease;
            }

            public void Dispose()
            {
                IDisposable fileLease =
                    Interlocked.Exchange(ref _fileLease, null);
                IDisposable mutexLease =
                    Interlocked.Exchange(ref _mutexLease, null);
                try
                {
                    if (fileLease != null) fileLease.Dispose();
                }
                finally
                {
                    if (mutexLease != null) mutexLease.Dispose();
                }
            }
        }
    }

    /// <summary>Same-directory durable replace helper reusable by the other JSON stores.</summary>
    internal static class AtomicFile
    {
        private const int MoveFileReplaceExisting = 0x1;
        private const int MoveFileWriteThrough = 0x8;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(
            string existingFileName,
            string newFileName,
            int flags);

        internal static void ReplaceExisting(
            string temporaryPath,
            string destinationPath,
            string backupPath,
            CancellationToken cancellationToken)
        {
            ReplaceExisting(
                temporaryPath,
                destinationPath,
                backupPath,
                cancellationToken,
                null);
        }

        internal static void ReplaceExisting(
            string temporaryPath,
            string destinationPath,
            string backupPath,
            CancellationToken cancellationToken,
            Action<string, string, string, bool> replaceFile)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (replaceFile == null)
                    File.Replace(temporaryPath, destinationPath, backupPath, true);
                else
                    replaceFile(temporaryPath, destinationPath, backupPath, true);
                return;
            }
            catch (PlatformNotSupportedException)
            {
                // Portable copies can live on filesystems where File.Replace is unavailable.
            }
            catch (NotSupportedException)
            {
            }
            catch (IOException)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(backupPath))
                File.Copy(destinationPath, backupPath, true);
            cancellationToken.ThrowIfCancellationRequested();
            if (!MoveFileEx(
                    temporaryPath,
                    destinationPath,
                    MoveFileReplaceExisting | MoveFileWriteThrough))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public static bool TryWriteAllText(string path, string contents, string backupPath)
        {
            string temp = null;
            try
            {
                if (!AppPaths.IsFullyQualifiedPath(path))
                    return false;

                path = Path.GetFullPath(path);
                string directory = Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);
                temp = Path.Combine(
                    directory,
                    "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

                using (var stream = new FileStream(
                    temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                    FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(contents ?? "");
                    writer.Flush();
                    stream.Flush(true);
                }

                if (!File.Exists(path))
                {
                    try
                    {
                        File.Move(temp, path);
                        temp = null;
                        return true;
                    }
                    catch (IOException)
                    {
                        // Another process may have created the destination after our existence check.
                        if (!File.Exists(path)) throw;
                    }
                }

                ReplaceExisting(
                    temp,
                    path,
                    backupPath,
                    CancellationToken.None);
                temp = null;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (temp != null)
                {
                    try { File.Delete(temp); } catch { }
                }
            }
        }
    }

    /// <summary>Strict, read-only importer for historical appSettings and userSettings XML.</summary>
    internal static class LegacySettingsReader
    {
        private const long MaximumLegacyConfigurationBytes = 12L * 1024L * 1024L;

        public static bool TryRead(
            IEnumerable<string> candidates,
            out AppSettingsDocument settings)
        {
            AppSettingsDocument imported = AppSettingsDocument.CreateDefault();
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (candidates != null)
            {
                foreach (string path in candidates)
                    ReadCandidate(path, values);
            }

            bool found = false;
            found |= ApplyString(values, "xml", delegate (string value) { imported.Xml = value; });
            found |= ApplyString(values, "Images", delegate (string value) { imported.Images = value; });
            found |= ApplyString(values, "Icon", delegate (string value) { imported.Icon = value; });
            found |= ApplyBool(values, "Multiscreen", delegate (bool value) { imported.MultiScreen = value; });
            found |= ApplyBool(values, "WinForeground", delegate (bool value) { imported.WindowForeground = value; });
            found |= ApplyBool(values, "StealTaskbarFocus", delegate (bool value) { imported.StealTaskbarFocus = value; });
            found |= ApplyBool(values, "SpeechEnabled", delegate (bool value) { imported.SpeechEnabled = value; });
            found |= ApplyInt(values, "Scale", delegate (int value) { imported.ScaleLevel = value; });
            found |= ApplyInt(values, "AutostartPets", delegate (int value) { imported.AutoStartPets = value; });
            found |= ApplyInt(values, "SpeechDuration", delegate (int value) { imported.SpeechDurationSeconds = value; });

            string volumeText;
            if (values.TryGetValue("Volume", out volumeText))
            {
                double volume;
                if (TryParseDouble(volumeText, out volume))
                {
                    // The old default was normalized ("0.3"), while the old UI persisted integer
                    // percentages ("30"). Accept both representations during the one-time import.
                    if (volume > 1.0) volume /= 100.0;
                    imported.Volume = volume;
                    found = true;
                }
            }

            imported.Normalize();
            settings = imported;
            return found;
        }

        private static void ReadCandidate(
            string path,
            IDictionary<string, string> values)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
                var file = new FileInfo(path);
                if (file.Length > MaximumLegacyConfigurationBytes) return;

                var readerSettings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersFromEntities = 0,
                    MaxCharactersInDocument = MaximumLegacyConfigurationBytes
                };
                var document = new XmlDocument { XmlResolver = null };
                using (XmlReader reader = XmlReader.Create(path, readerSettings))
                    document.Load(reader);

                XmlNodeList appSettings = document.SelectNodes("/configuration/appSettings/add");
                if (appSettings != null)
                {
                    foreach (XmlNode node in appSettings)
                    {
                        string key = Attribute(node, "key");
                        string value = Attribute(node, "value");
                        AddFirst(values, key, value);
                    }
                }

                XmlNodeList userSettings =
                    document.SelectNodes("/configuration/userSettings/*/setting");
                if (userSettings != null)
                {
                    foreach (XmlNode node in userSettings)
                    {
                        string key = Attribute(node, "name");
                        XmlNode valueNode = node.SelectSingleNode("value");
                        AddFirst(values, key, valueNode == null ? null : valueNode.InnerText);
                    }
                }
            }
            catch
            {
                // A malformed candidate does not block later candidates or fresh defaults.
            }
        }

        private static string Attribute(XmlNode node, string name)
        {
            if (node == null || node.Attributes == null) return null;
            XmlAttribute attribute = node.Attributes[name];
            return attribute == null ? null : attribute.Value;
        }

        private static void AddFirst(
            IDictionary<string, string> values,
            string key,
            string value)
        {
            if (string.IsNullOrWhiteSpace(key) || value == null || values.ContainsKey(key))
                return;
            values[key] = value;
        }

        private static bool ApplyString(
            IDictionary<string, string> values,
            string key,
            Action<string> apply)
        {
            string value;
            if (!values.TryGetValue(key, out value)) return false;
            apply(value ?? "");
            return true;
        }

        private static bool ApplyBool(
            IDictionary<string, string> values,
            string key,
            Action<bool> apply)
        {
            string text;
            bool value;
            if (!values.TryGetValue(key, out text) || !bool.TryParse(text, out value))
                return false;
            apply(value);
            return true;
        }

        private static bool ApplyInt(
            IDictionary<string, string> values,
            string key,
            Action<int> apply)
        {
            string text;
            int value;
            if (!values.TryGetValue(key, out text) ||
                !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return false;
            apply(value);
            return true;
        }

        private static bool TryParseDouble(string text, out double value)
        {
            return double.TryParse(
                    text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }
    }
}
