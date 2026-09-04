using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using DesktopAICompanion.ModuleKit;   // AtomicFile / CrossSessionLock / UnicodeTextProgress

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// AI-layer configuration, persisted as JSON under the canonical application data root. Kept
    /// separate from the WinForms user.config so the AI layer stays self-contained and the original
    /// engine's settings are never touched. Missing/corrupt files fall back to recovery/defaults.
    /// </summary>
    internal sealed class AiSettings
    {
        public const int CurrentSchemaVersion = 3;
        private const int MaximumSettingsBytes = 256 * 1024;
        private const int MaximumEndpointCharacters = 2048;
        internal const int MaximumModelCharacters = 256;
        private const int MaximumPathCharacters = 1024;
        private const int MaximumNameCharacters = 80;
        private const int MaximumApiKeyCharacters = 8192;
        private const int MaximumEncryptedApiKeyCharacters = 16384;
        internal const int MaximumApiKeyScopes = 32;
        private const int MaximumDisabledSources = 128;
        private const int MaximumSourceCharacters = 128;
        private const int ProcessLockTimeoutMilliseconds = 10000;
        private static readonly object ProcessLock = new object();
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly string[] PersistedFieldNames = BuildPersistedFieldNames();

        // Persisted via public FIELDS -> IncludeFields is required (STJ ignores fields otherwise). MaxDepth
        // mirrors the old JsonTextReader bound; WriteIndented matches the previous Formatting.Indented; the
        // relaxed encoder keeps user text (persona/name) and base64 ciphertext literal instead of \uXXXX-
        // escaping, as Newtonsoft did. Default null handling is kept on purpose. One options object serves
        // deserialize, SerializeToNode (DOM; WriteIndented/Encoder are no-ops there), and ToJsonString.
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true,
            WriteIndented = true,
            MaxDepth = 32,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        [JsonIgnore]
        private bool _writesBlockedByFutureSchema;

        [JsonIgnore]
        private JsonObject _baseline;

        /// <summary>Persistence schema for forward migrations.</summary>
        public int SchemaVersion = CurrentSchemaVersion;

        /// <summary>
        /// The LOCAL slot's base endpoint. Its protocol is selected by <see cref="LocalBackendKind"/> — the
        /// default is Ollama's native API (no trailing slash needed); pointed at a generic OpenAI-compatible
        /// <c>/v1</c> server (llama.cpp, LM Studio, or similar) instead, include the <c>/v1</c> suffix.
        /// </summary>
        public string Endpoint = "http://localhost:11434";

        /// <summary>
        /// Which protocol the LOCAL slot speaks: <c>"ollama"</c> (native Ollama API — the default; gets the
        /// lifecycle features below via <see cref="OllamaPath"/>: auto-start, warm-up, unload) or
        /// <c>"openai-compat"</c> (the generic OpenAI-compatible <c>/v1</c> protocol spoken by llama.cpp,
        /// LM Studio, and similar local servers — those lifecycle calls are harmless no-ops there).
        /// </summary>
        public string LocalBackendKind = "ollama";

        /// <summary>Fast text-only model used for OCR-based commentary.</summary>
        public string TextModel = "llama3.1:8b";

        /// <summary>
        /// Multimodal model used when <see cref="UseVision"/> is on (much slower/heavier than OCR).
        /// Default is a small, fast vision model; bigger ones (e.g. mistral-small3.2:24b) read the
        /// screen better but can take a minute per glance. See the grimoire for recommendations.
        /// </summary>
        public string VisionModel = "gemma3:4b";

        /// <summary>
        /// When true, send a downscaled screenshot to the vision model instead of OCR text.
        /// Only used for explicit asks (hotkey/tray) — idle commentary always stays on the fast
        /// text path, since a vision glance can take tens of seconds.
        /// </summary>
        public bool UseVision = false;

        /// <summary>Per-request HTTP timeout. Generous because a cold vision model on a full-screen
        /// image can take a minute or more.</summary>
        public int TimeoutSeconds = 120;

        /// <summary>Full path to tesseract.exe. Empty means "find <c>tesseract</c> on PATH".</summary>
        public string TesseractPath = "";

        // ---- Phase 5: persona ----------------------------------------------

        /// <summary>The pet's name, injected into its persona. Empty -> a generic "desktop pet".</summary>
        public string PetName = "eSheep";

        /// <summary>Optional name the pet may address you by. Empty -> it won't use one.</summary>
        public string UserName = "";

        /// <summary>Which curated character voice the pet speaks in: a known id from
        /// <see cref="Dispositions.All"/> (e.g. "ted-lasso", "samuel"). Replaces the older separate
        /// Personality-blurb + SpeechPattern-id pair (schema v2), which let a tone preset and a
        /// delivery style combine into incoherent pairings; each disposition now bakes tone and
        /// delivery into one instruction.</summary>
        public string Disposition = Dispositions.DefaultId;

        /// <summary>
        /// Explicit consent for sending screen/OCR/window context to a non-loopback provider.
        /// Endpoint policy enforces this before any cloud request.
        /// </summary>
        public bool CloudDataConsent = false;

        // ---- fortunes (Phase A) --------------------------------------------

        /// <summary>Include edgier/adult fortunes (see <see cref="SpicyTier"/>) on top of the
        /// family-friendly ones. Off = general content only.</summary>
        public bool SpicyFortunes = false;

        /// <summary>
        /// Which spicy content to include when <see cref="SpicyFortunes"/> is on:
        /// <c>"edgy"</c> = crude/adult humor + explicit (everything), <c>"nsfw"</c> = explicit only.
        /// </summary>
        public string SpicyTier = "edgy";

        /// <summary>
        /// With <see cref="SpicyFortunes"/> on, pull ONLY the spicy tiers and skip the tame
        /// (general) ones. Ignored when SpicyFortunes is off.
        /// </summary>
        public bool SpicyOnly = false;

        /// <summary>
        /// Drop any fortune flagged for recognized profanity or explicit sexual content, at every
        /// level (a conservative hard filter).
        /// </summary>
        public bool NoProfanity = false;

        /// <summary>
        /// Pick fortunes that fit what's on screen (local bge-small embedder). Fully offline, bundled,
        /// no keys. Falls back to random automatically if the model is missing or no good match is found.
        /// </summary>
        public bool SmartFortunes = true;

        /// <summary>
        /// Source collections the user has switched OFF in the picker. Empty = all sources enabled
        /// (so newly-added collections default to on).
        /// </summary>
        public System.Collections.Generic.List<string> DisabledSources =
            new System.Collections.Generic.List<string>();

        /// <summary>
        /// Delivery genres the user has switched OFF in the picker (e.g. "tv-quote", "insult").
        /// Empty = all genres enabled (so newly-added genres default to on). Like
        /// <see cref="DisabledSources"/>, this is a hard preference filter that is never relaxed.
        /// </summary>
        public System.Collections.Generic.List<string> DisabledGenres =
            new System.Collections.Generic.List<string>();

        /// <summary>
        /// Periodically speak an unprompted line: a random fortune when the AI brain is off, or an AI
        /// insight about the screen when it is on. Fires at a randomized interval of
        /// <see cref="RandomDropMinutes"/> ± <see cref="RandomDropJitterMinutes"/> minutes.
        /// </summary>
        public bool RandomDropEnabled = false;

        /// <summary>Center of the random-drop interval, in minutes (1..9999). Default 15.</summary>
        public int RandomDropMinutes = 15;

        /// <summary>Plus/minus jitter around <see cref="RandomDropMinutes"/>, in minutes. Default 3,
        /// clamped below the center so the interval stays positive.</summary>
        public int RandomDropJitterMinutes = 3;

        // ---- AI brain master switch ----------------------------------------

        /// <summary>
        /// Master switch for the optional screen-commentary LLM. OFF by default, so no selected
        /// provider is contacted and only the local CPU smart-fortunes embedder runs. Toggle it from
        /// the tray ("Enable AI" / "Disable AI") or the AI tab. Ollama additionally supports
        /// keep-alive model warm-up and unload; generic OpenAI-compatible providers do not.
        /// </summary>
        public bool AiBrainEnabled = false;

        // ---- provider (schema v2: the LOCAL slot is fixed above; Provider is the CLOUD selector) ----

        /// <summary>
        /// CLOUD provider selector: <c>""</c> (no cloud — local-only) | <c>openai</c> | <c>openrouter</c> |
        /// <c>custom</c>. The LOCAL slot is the fixed <see cref="Endpoint"/> / <see cref="TextModel"/> /
        /// <see cref="VisionModel"/> (Ollama); this field selects the optional cloud provider that, when set,
        /// is primary. Schema v2 reinterpretation: the legacy local ids (ollama/lmstudio/llamacpp) migrate to
        /// <c>""</c> and an old cloud id keeps its slot — see <see cref="Normalize"/>.
        /// </summary>
        public string Provider = "";

        /// <summary>Base URL (including <c>/v1</c>) for the cloud OpenAI-compatible provider.</summary>
        public string OpenAiBaseUrl = "";

        /// <summary>
        /// Last endpoint entered for the Custom provider. Preserving this separately prevents a
        /// preset selection from making an endpoint-scoped Custom credential unreachable.
        /// </summary>
        public string CustomOpenAiBaseUrl = "";

        /// <summary>Cloud text model id, used when a cloud <see cref="Provider"/> is selected. Empty = unset.</summary>
        public string CloudTextModel = "";

        /// <summary>Cloud vision model id, used when a cloud <see cref="Provider"/> is selected. Empty = unset.</summary>
        public string CloudVisionModel = "";

        /// <summary>
        /// When a cloud <see cref="Provider"/> is primary, fall back to the LOCAL slot if the cloud backend is
        /// unavailable. Persisted + surfaced here; the runtime fallback backend is wired in a later change.
        /// </summary>
        public bool UseLocalFallback = true;

        /// <summary>
        /// Legacy single DPAPI-encrypted key. Normalization migrates it once to the currently
        /// selected provider/endpoint scope, then clears this field so a provider switch cannot
        /// reuse it.
        /// </summary>
        public string ApiKeyEnc = "";

        /// <summary>
        /// DPAPI-encrypted keys keyed by a hash of provider plus normalized endpoint. Neither
        /// plaintext credentials nor endpoint text are stored in the dictionary keys.
        /// </summary>
        public Dictionary<string, string> ApiKeysEnc =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Plaintext API key for the currently selected provider/endpoint scope. Not serialized.
        /// </summary>
        [JsonIgnore]
        public string ApiKey
        {
            get { return GetApiKey(Provider, SelectedCredentialEndpoint()); }
            set
            {
                string error;
                if (!TrySetApiKey(value, out error))
                    throw new InvalidOperationException(error);
            }
        }

        // ---- Phase 3: triggers ---------------------------------------------

        /// <summary>Register a global hotkey that fires the reactive "ask about my screen" flow.</summary>
        public bool HotkeyEnabled = true;

        /// <summary>Global hotkey combination, e.g. "Ctrl+Alt+P". Needs at least one modifier.</summary>
        public string Hotkey = "Ctrl+Alt+P";

        // Unprompted commentary has NO settings of its own. It is driven entirely by the host's global
        // "Randomly drop a fortune / insight" schedule in Preferences, reaching this module through the drop
        // responder (OnDrop). The module used to carry its own IdleCommentaryEnabled / IdleMinSeconds /
        // IdleMaxSeconds / IdleChangeThresholdPercent on a separate 90-150s timer, which meant two
        // independent schedules driving the same LLM into the same speech bubble with no shared cooldown:
        // with both on, the idle loop fired roughly 8x more often and the global drop became statistically
        // invisible. One schedule, one set of controls.

        // ---- launch preparation --------------------------------------------

        /// <summary>On launch, start the Ollama server (<c>ollama serve</c>) if it isn't already reachable.</summary>
        public bool AutoStartServer = true;

        /// <summary>
        /// How long the model may hold VRAM: <see cref="ResidencyUnload"/> (the default),
        /// <see cref="ResidencyKeep"/>, or <see cref="ResidencyServer"/>.
        ///
        /// ONE setting, because the two it replaced ("preload on launch" and "unload N seconds after a
        /// remark") could contradict each other: preload pinned the model for 10 minutes, so a warmed model
        /// outlived a short eject window and the pane had to carry a paragraph explaining why. A single choice
        /// cannot disagree with itself, needs no greying-out logic, and needs no explanation.
        /// </summary>
        public string ModelResidency = ResidencyUnload;

        /// <summary>
        /// While a fullscreen app (a game) is running: release the model and make no remarks that need one,
        /// letting the free local fortunes speak instead.
        ///
        /// A crash guard, not a courtesy. A model claiming several GB of VRAM beside a game that already owns
        /// it can take the game down. ON by default: the cost of being wrong is a fortune instead of a quip,
        /// against a game crash the other way, and while a game is fullscreen the pet is hidden anyway so a
        /// model answer would not even be seen.
        /// </summary>
        public bool StandDownForFullscreen = true;

        /// <summary>Evict as soon as a remark is answered. The default: this module's whole reason for holding
        /// VRAM is a remark it has already made.</summary>
        public const string ResidencyUnload = "unload";

        /// <summary>Load on launch and hold it for the session. Fastest, and holds VRAM the whole time.</summary>
        public const string ResidencyKeep = "keep";

        /// <summary>Say nothing and let the Ollama server decide (documented as 5 minutes, but
        /// OLLAMA_KEEP_ALIVE overrides it machine-wide).</summary>
        public const string ResidencyServer = "server";

        /// <summary>True when the model should be loaded at launch. Only the "keep" choice wants this: warming
        /// a model up and then evicting it after the first remark would be work done to be thrown away.</summary>
        [JsonIgnore]
        public bool WarmUpDesired
        {
            get { return string.Equals(ModelResidency, ResidencyKeep, StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>
        /// The <c>keep_alive</c> value to put on a chat request, or null to omit the field entirely.
        ///
        /// Three distinct wire values, which is why this is nullable rather than an int with a sentinel: 0
        /// means evict now, a NEGATIVE number means stay resident indefinitely, and omitting the field means
        /// "server's choice". Reusing -1 as "omit" would have asked Ollama for the exact opposite of what was
        /// intended -- resident for ever.
        /// </summary>
        [JsonIgnore]
        public int? KeepAliveForRequests
        {
            get
            {
                if (string.Equals(ModelResidency, ResidencyKeep, StringComparison.OrdinalIgnoreCase)) return -1;
                if (string.Equals(ModelResidency, ResidencyServer, StringComparison.OrdinalIgnoreCase)) return null;
                return 0;   // "unload", and the fallback for an unrecognised stored value
            }
        }

        /// <summary>Full path to ollama.exe. Empty means autodetect (PATH + default install locations).</summary>
        public string OllamaPath = "";

        // System.Text.Json requires the extension-data sink to be a PROPERTY (a field is rejected). Kept
        // non-null with an Ordinal comparer so deserialization adds unknown fields into this instance,
        // which is what round-trips a future-same-schema doc's unknown data.
        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtensionData { get; set; } =
            new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        [JsonIgnore]
        public static string FilePath
        {
            get { return AiPaths.AiSettingsFile; }
        }

        /// <summary>Load settings, writing a default file on first run. Never throws.</summary>
        public static AiSettings Load()
        {
            lock (ProcessLock)
            {
                try
                {
                    return WithFileLock(LoadCore);
                }
                catch { }
                // A lock/read failure must not turn into a later blind overwrite of settings that
                // this process never observed.
                return new AiSettings { _writesBlockedByFutureSchema = true };
            }
        }

        /// <summary>Persist settings. Returns false when durable storage is unavailable or blocked.</summary>
        public bool Save()
        {
            return SaveWithin(ProcessLockTimeoutMilliseconds);
        }

        /// <summary>
        /// Persist settings within one aggregate lock budget. UI callers use a short budget so a
        /// hung peer cannot freeze the message thread for the full cross-session timeout.
        /// </summary>
        internal bool SaveWithin(int timeoutMilliseconds)
        {
            timeoutMilliseconds = Math.Max(0, timeoutMilliseconds);
            Stopwatch stopwatch = Stopwatch.StartNew();
            bool entered = false;
            try
            {
                entered = Monitor.TryEnter(ProcessLock, timeoutMilliseconds);
                if (!entered) return false;
                int remaining = RemainingMilliseconds(
                    timeoutMilliseconds,
                    stopwatch.ElapsedMilliseconds);
                return WithFileLock(SaveMerged, remaining);
            }
            catch { return false; }
            finally
            {
                if (entered) Monitor.Exit(ProcessLock);
            }
        }

        private static AiSettings LoadCore()
        {
            AiSettings loaded;
            ReadResult result = TryRead(FilePath, out loaded);
            if (result == ReadResult.Loaded || result == ReadResult.FutureSchema)
            {
                loaded._writesBlockedByFutureSchema =
                    result == ReadResult.FutureSchema;
                bool changed = loaded.Normalize();
                if (changed && result == ReadResult.Loaded) loaded.SaveCore();
                loaded.CaptureBaseline();
                return loaded;
            }

            ReadResult backupResult = TryRead(FilePath + ".bak", out loaded);
            if (backupResult == ReadResult.Loaded ||
                backupResult == ReadResult.FutureSchema)
            {
                loaded._writesBlockedByFutureSchema =
                    backupResult == ReadResult.FutureSchema;
                loaded.Normalize();
                if (backupResult == ReadResult.Loaded)
                    loaded._writesBlockedByFutureSchema =
                        !loaded.RestorePrimaryWithoutRotatingBackup();
                loaded.CaptureBaseline();
                return loaded;
            }

            if (result == ReadResult.Missing && AiPaths.LegacyMigrationEnabled)
            {
                string legacy = Path.Combine(
                    AiPaths.LegacyRoamingDataRoot,
                    "ai-settings.json");
                if (!string.Equals(
                        Path.GetFullPath(legacy),
                        Path.GetFullPath(FilePath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    ReadResult legacyResult = TryRead(legacy, out loaded);
                    if (legacyResult == ReadResult.Loaded)
                    {
                        loaded.Normalize();
                        loaded.SaveCore();
                        loaded.CaptureBaseline();
                        return loaded;
                    }
                    if (legacyResult == ReadResult.FutureSchema)
                    {
                        loaded._writesBlockedByFutureSchema = true;
                        loaded.Normalize();
                        loaded.CaptureBaseline();
                        return loaded;
                    }
                }
            }

            AiSettings defaults = new AiSettings();
            defaults.SaveCore();
            defaults.CaptureBaseline();
            return defaults;
        }

        private bool SaveMerged()
        {
            if (_writesBlockedByFutureSchema ||
                SchemaVersion > CurrentSchemaVersion)
                return false;

            AiSettings existing;
            ReadResult result = TryRead(FilePath, out existing);
            if (result == ReadResult.FutureSchema)
            {
                _writesBlockedByFutureSchema = true;
                return false;
            }

            Normalize();
            JsonObject current = (JsonObject)JsonSerializer.SerializeToNode(this, JsonOptions);
            JsonObject target;
            bool applyAll = result != ReadResult.Loaded || _baseline == null;
            if (result == ReadResult.Loaded)
            {
                existing.Normalize();
                target = (JsonObject)JsonSerializer.SerializeToNode(existing, JsonOptions);
            }
            else
            {
                target = new JsonObject();
            }

            foreach (string fieldName in PersistedFieldNames)
            {
                if (string.Equals(
                        fieldName,
                        "ApiKeysEnc",
                        StringComparison.Ordinal))
                {
                    MergeCredentialScopes(
                        current,
                        target,
                        _baseline,
                        applyAll);
                    continue;
                }
                JsonNode value = current[fieldName];
                JsonNode baselineValue = _baseline == null ? null : _baseline[fieldName];
                if (!applyAll && JsonNode.DeepEquals(value, baselineValue)) continue;
                // DeepClone detaches the node from `current` before it is re-parented into `target`
                // (STJ throws when a node that already has a parent is assigned elsewhere).
                if (value == null)
                    target.Remove(fieldName);
                else
                    target[fieldName] = value.DeepClone();
            }

            if (!SaveDocument(target)) return false;
            _baseline = current;
            return true;
        }

        private static void MergeCredentialScopes(
            JsonObject current,
            JsonObject target,
            JsonObject baseline,
            bool applyAll)
        {
            const string FieldName = "ApiKeysEnc";
            JsonNode currentValue = current[FieldName];
            if (applyAll)
            {
                if (currentValue == null)
                    target.Remove(FieldName);
                else
                    target[FieldName] = currentValue.DeepClone();
                return;
            }

            var currentScopes = currentValue as JsonObject ?? new JsonObject();
            var baselineScopes =
                (baseline == null ? null : baseline[FieldName]) as JsonObject ??
                new JsonObject();
            // When target already holds an ApiKeysEnc object, mutate it IN PLACE: re-assigning a node that
            // still has `target` as its parent would throw. Only a freshly created scope object (target had
            // no object there) needs to be attached at the end.
            JsonObject attachedScopes = target[FieldName] as JsonObject;
            JsonObject targetScopes = attachedScopes ?? new JsonObject();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in currentScopes)
                names.Add(property.Key);
            foreach (var property in baselineScopes)
                names.Add(property.Key);

            foreach (string name in names)
            {
                JsonNode value = currentScopes[name];
                JsonNode baselineValue = baselineScopes[name];
                if (JsonNode.DeepEquals(value, baselineValue)) continue;
                if (value == null)
                    targetScopes.Remove(name);
                else
                    targetScopes[name] = value.DeepClone();
            }
            if (attachedScopes == null)
                target[FieldName] = targetScopes;
        }

        /// <summary>Clamp persisted operational ranges before any caller consumes them.</summary>
        internal bool Normalize()
        {
            bool changed = false;

            // Schema-migration gate: a doc read BELOW the current schema gets the one-time v1 -> v2 slot
            // migration (further down, once Provider is trimmed/lowercased). A future-schema doc
            // (SchemaVersion > Current) is never migrated here and its writes stay blocked upstream, so it is
            // left byte-for-byte intact. Capture the flag before advancing the stored version.
            bool needsSchemaMigration = SchemaVersion < CurrentSchemaVersion;
            if (needsSchemaMigration)
            {
                SchemaVersion = CurrentSchemaVersion;
                changed = true;
            }
            changed |= Clamp(ref TimeoutSeconds, 10, 600);
            changed |= Clamp(ref RandomDropMinutes, 1, 9999);
            changed |= Clamp(ref RandomDropJitterMinutes, 0, RandomDropMinutes - 1);

            changed |= NormalizeString(
                ref Endpoint, "http://localhost:11434", MaximumEndpointCharacters);
            changed |= NormalizeString(ref LocalBackendKind, "ollama", 32);
            string normalizedLocalKind = LocalBackendKind.ToLowerInvariant();
            if (!string.Equals(LocalBackendKind, normalizedLocalKind, StringComparison.Ordinal))
            {
                LocalBackendKind = normalizedLocalKind;
                changed = true;
            }
            if (!IsKnownLocalBackendKind(LocalBackendKind))
            {
                LocalBackendKind = "ollama";
                changed = true;
            }
            changed |= NormalizeModel(ref TextModel, "llama3.1:8b");
            changed |= NormalizeModel(ref VisionModel, "gemma3:4b");
            changed |= NormalizeString(ref TesseractPath, "", MaximumPathCharacters);
            changed |= NormalizeString(ref PetName, "eSheep", MaximumNameCharacters);
            changed |= NormalizeString(ref UserName, "", MaximumNameCharacters);
            changed |= NormalizeString(ref Disposition, Dispositions.DefaultId, 32);
            string canonicalDisposition = Disposition.ToLowerInvariant();
            if (!string.Equals(Disposition, canonicalDisposition, StringComparison.Ordinal))
            {
                Disposition = canonicalDisposition;
                changed = true;
            }
            // One-time v2 -> v3 reinterpretation, BEFORE the known-id clamp below so a legacy id this
            // schema absorbed (see MigrateDispositionFromV2) still steers it.
            if (needsSchemaMigration)
                changed |= MigrateDispositionFromV2();
            if (!Dispositions.IsKnown(Disposition))
            {
                Disposition = Dispositions.DefaultId;
                changed = true;
            }
            changed |= NormalizeString(ref SpicyTier, "edgy", 16);
            if (!string.Equals(SpicyTier, "edgy", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(SpicyTier, "nsfw", StringComparison.OrdinalIgnoreCase))
            {
                SpicyTier = "edgy";
                changed = true;
            }
            changed |= NormalizeString(ref Provider, "", 32);
            string normalizedProvider = Provider.ToLowerInvariant();
            if (!string.Equals(Provider, normalizedProvider, StringComparison.Ordinal))
            {
                Provider = normalizedProvider;
                changed = true;
            }
            // One-time v1 -> v2 slot migration. Runs BEFORE the known-provider clamp so the legacy local ids
            // (ollama/lmstudio/llamacpp) still steer it; a cloud id keeps its slot and promotes the old
            // TextModel/VisionModel into the cloud slot, anything else clears the selector to "" (local-only).
            if (needsSchemaMigration)
                changed |= MigrateCloudSlotFromV1();
            if (!IsKnownProvider(Provider))
            {
                Provider = "";
                changed = true;
            }
            changed |= NormalizeString(
                ref OpenAiBaseUrl, "", MaximumEndpointCharacters);
            changed |= NormalizeString(
                ref CustomOpenAiBaseUrl, "", MaximumEndpointCharacters);
            changed |= NormalizeOptionalModel(ref CloudTextModel);
            changed |= NormalizeOptionalModel(ref CloudVisionModel);
            if (string.Equals(
                    Provider,
                    "custom",
                    StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrEmpty(CustomOpenAiBaseUrl) &&
                !string.IsNullOrEmpty(OpenAiBaseUrl))
            {
                CustomOpenAiBaseUrl = OpenAiBaseUrl;
                changed = true;
            }
            changed |= NormalizeString(
                ref ApiKeyEnc, "", MaximumEncryptedApiKeyCharacters);
            changed |= NormalizeApiKeyScopes();
            if (!string.IsNullOrEmpty(ApiKeyEnc))
            {
                string scope = BuildCredentialScope(
                    Provider,
                    SelectedCredentialEndpoint());
                if (!IsWellFormedEncryptedApiKey(ApiKeyEnc))
                {
                    ApiKeyEnc = "";
                    changed = true;
                }
                else if (!string.IsNullOrEmpty(scope))
                {
                    if (ApiKeysEnc.ContainsKey(scope))
                    {
                        // A provider-scoped credential supersedes the legacy singleton.
                        ApiKeyEnc = "";
                        changed = true;
                    }
                    else
                    {
                        string migratedKey;
                        if (TryDecryptApiKey(ApiKeyEnc, out migratedKey))
                        {
                            ApiKeysEnc[scope] = ApiKeyEnc;
                            ApiKeyEnc = "";
                            changed = true;
                        }
                        // A well-formed value that DPAPI cannot currently decrypt is preserved.
                        // Profile/DPAPI failures can be transient and must not become credential
                        // deletion during automatic normalization.
                    }
                }
            }
            changed |= NormalizeString(ref Hotkey, "Ctrl+Alt+P", 64);
            changed |= NormalizeString(ref OllamaPath, "", MaximumPathCharacters);
            changed |= NormalizeDisabledSources();
            return changed;
        }

        /// <summary>
        /// One-time schema v1 -> v2 reinterpretation of the single legacy <see cref="Provider"/> selector into
        /// a fixed LOCAL slot plus an optional CLOUD selector. Called from <see cref="Normalize"/> only for a
        /// doc read below the current schema, with <see cref="Provider"/> already trimmed + lowercased:
        /// <list type="bullet">
        /// <item>a cloud id (openai/openrouter/custom): the user WAS on cloud, so the old
        /// <see cref="TextModel"/>/<see cref="VisionModel"/> were the cloud models — promote them into
        /// <see cref="CloudTextModel"/>/<see cref="CloudVisionModel"/> and reset the local slot models to
        /// their defaults. Provider and <see cref="OpenAiBaseUrl"/> are kept, so the scoped credential in
        /// <see cref="ApiKeysEnc"/> (keyed by provider + endpoint) keeps the SAME scope hash and stays valid.</item>
        /// <item>a legacy local id (ollama/lmstudio/llamacpp) or anything unknown: no cloud — clear the
        /// selector to "" and leave the local slot (Endpoint/TextModel/VisionModel) as-is.</item>
        /// </list>
        /// </summary>
        private bool MigrateCloudSlotFromV1()
        {
            if (string.Equals(Provider, "openai", StringComparison.Ordinal) ||
                string.Equals(Provider, "openrouter", StringComparison.Ordinal) ||
                string.Equals(Provider, "custom", StringComparison.Ordinal))
            {
                CloudTextModel = TextModel;
                CloudVisionModel = VisionModel;
                TextModel = "llama3.1:8b";
                VisionModel = "gemma3:4b";
                return true;
            }
            Provider = "";
            return true;
        }

        /// <summary>
        /// One-time schema v2 -> v3 reinterpretation of the old Personality-blurb + SpeechPattern-id pair
        /// into the single <see cref="Disposition"/> id. STJ routes both retired keys into
        /// <see cref="ExtensionData"/> during deserialize (their fields no longer exist on this class), so
        /// they are read from there. The free-text Personality blurb can't be reliably reversed onto a
        /// curated disposition and is discarded; a legacy SpeechPattern id that this schema's curated list
        /// absorbed under the SAME id (samuel/pirate/leet/rhyme/pun/yoda/valley) carries over directly since
        /// it was already a deliberate character choice, otherwise <see cref="Dispositions.DefaultId"/> is
        /// left in place (the caller re-picks from the new list). Both legacy keys are removed from
        /// <see cref="ExtensionData"/> so they do not linger in the file forever as dead cruft.
        /// </summary>
        private bool MigrateDispositionFromV2()
        {
            string legacySpeech = "";
            JsonElement speechElement;
            if (ExtensionData.TryGetValue("SpeechPattern", out speechElement) &&
                speechElement.ValueKind == JsonValueKind.String)
                legacySpeech = (speechElement.GetString() ?? "").Trim().ToLowerInvariant();
            ExtensionData.Remove("SpeechPattern");
            ExtensionData.Remove("Personality");
            if (Dispositions.IsKnown(legacySpeech))
            {
                Disposition = legacySpeech;
                return true;
            }
            return false;
        }

        private bool SaveCore()
        {
            if (_writesBlockedByFutureSchema ||
                SchemaVersion > CurrentSchemaVersion)
                return false;
            return SaveDocument((JsonObject)JsonSerializer.SerializeToNode(this, JsonOptions));
        }

        private bool RestorePrimaryWithoutRotatingBackup()
        {
            if (_writesBlockedByFutureSchema ||
                SchemaVersion > CurrentSchemaVersion)
                return false;
            return SaveDocument((JsonObject)JsonSerializer.SerializeToNode(this, JsonOptions), null);
        }

        private static bool SaveDocument(JsonObject document)
        {
            return SaveDocument(document, FilePath + ".bak");
        }

        private static bool SaveDocument(JsonObject document, string backupPath)
        {
            if (document == null) return false;
            string json = document.ToJsonString(JsonOptions);
            if (StrictUtf8.GetByteCount(json) > MaximumSettingsBytes)
                return false;
            return AtomicFile.TryWriteAllText(FilePath, json, backupPath);
        }

        private void CaptureBaseline()
        {
            _baseline = (JsonObject)JsonSerializer.SerializeToNode(this, JsonOptions);
        }

        private static T WithFileLock<T>(Func<T> action)
        {
            return WithFileLock(action, ProcessLockTimeoutMilliseconds);
        }

        private static T WithFileLock<T>(
            Func<T> action,
            int timeoutMilliseconds)
        {
            using (CrossSessionLock.Acquire(
                BuildMutexName(FilePath),
                FilePath,
                Math.Max(0, timeoutMilliseconds),
                "AI settings"))
                return action();
        }

        private static int RemainingMilliseconds(
            int timeoutMilliseconds,
            long elapsedMilliseconds)
        {
            long remaining = (long)Math.Max(0, timeoutMilliseconds) -
                Math.Max(0L, elapsedMilliseconds);
            if (remaining <= 0) return 0;
            return remaining >= int.MaxValue
                ? int.MaxValue
                : (int)remaining;
        }

        private static string BuildMutexName(string path)
        {
            return CrossSessionLock.BuildGlobalMutexName("AiSettings", path);
        }

        private static string[] BuildPersistedFieldNames()
        {
            var names = new List<string>();
            foreach (FieldInfo field in typeof(AiSettings).GetFields(
                BindingFlags.Instance | BindingFlags.Public))
            {
                if (field.IsDefined(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true) ||
                    field.IsDefined(typeof(System.Text.Json.Serialization.JsonExtensionDataAttribute), true))
                    continue;
                names.Add(field.Name);
            }
            names.Sort(StringComparer.Ordinal);
            return names.ToArray();
        }

        private static ReadResult TryRead(string path, out AiSettings settings)
        {
            settings = null;
            try
            {
                if (!File.Exists(path)) return ReadResult.Missing;
                string json = ReadBoundedUtf8(path, MaximumSettingsBytes);
                settings = JsonSerializer.Deserialize<AiSettings>(json, JsonOptions);
                if (settings == null) return ReadResult.Unreadable;
                return settings.SchemaVersion > CurrentSchemaVersion
                    ? ReadResult.FutureSchema
                    : ReadResult.Loaded;
            }
            catch
            {
                settings = null;
                return ReadResult.Unreadable;
            }
        }

        private static string ReadBoundedUtf8(string path, int maximumBytes)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan))
            {
                if (stream.Length > maximumBytes)
                    throw new InvalidDataException("AI settings file exceeds its size limit.");
                using (var memory = new MemoryStream((int)stream.Length))
                {
                    byte[] buffer = new byte[4096];
                    int total = 0;
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total = checked(total + read);
                        if (total > maximumBytes)
                            throw new InvalidDataException(
                                "AI settings file exceeds its size limit.");
                        memory.Write(buffer, 0, read);
                    }
                    return StrictUtf8.GetString(memory.ToArray());
                }
            }
        }

        private static bool Clamp(ref int value, int minimum, int maximum)
        {
            int normalized = Math.Max(minimum, Math.Min(maximum, value));
            if (normalized == value) return false;
            value = normalized;
            return true;
        }

        private static bool NormalizeString(
            ref string value,
            string fallback,
            int maximumCharacters)
        {
            string original = value;
            value = value ?? fallback;
            var clean = new StringBuilder(Math.Min(value.Length, maximumCharacters));
            string candidate = value.Trim();
            for (int index = 0; index < candidate.Length;)
            {
                char character = candidate[index++];
                if (char.IsControl(character)) continue;
                if (char.IsHighSurrogate(character))
                {
                    if (index >= candidate.Length ||
                        !char.IsLowSurrogate(candidate[index]))
                        continue;
                    if (clean.Length + 2 > maximumCharacters) break;
                    clean.Append(character);
                    clean.Append(candidate[index++]);
                    continue;
                }
                if (char.IsLowSurrogate(character)) continue;
                if (clean.Length >= maximumCharacters) break;
                clean.Append(character);
            }
            value = clean.ToString();
            return !string.Equals(original, value, StringComparison.Ordinal);
        }

        private static bool NormalizeModel(ref string value, string fallback)
        {
            string original = value;
            string normalized;
            if (!AiModelPolicy.TryNormalize(value, out normalized))
                normalized = fallback;
            value = normalized;
            return !string.Equals(original, value, StringComparison.Ordinal);
        }

        // Cloud model ids are OPTIONAL (empty = unset), unlike the always-present local models. Empty stays
        // empty; a non-empty value is bounded + sanitized by the same policy, and anything invalid collapses
        // to empty rather than a local fallback (a cloud slot has no meaningful Ollama default).
        private static bool NormalizeOptionalModel(ref string value)
        {
            string original = value ?? "";
            string candidate = original.Trim();
            string normalized;
            if (candidate.Length == 0 ||
                !AiModelPolicy.TryNormalize(candidate, out normalized))
                normalized = "";
            value = normalized;
            return !string.Equals(original, value, StringComparison.Ordinal);
        }

        internal string CredentialIdentity()
        {
            string key = ApiKey;
            if (string.IsNullOrEmpty(key)) return "anonymous";
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(
                    StrictUtf8.GetBytes(
                        "DesktopAICompanion.CredentialIdentity.v1\n" + key));
                return ToHex(hash);
            }
        }

        internal static string BuildCredentialScope(
            string provider,
            string endpoint)
        {
            string normalizedProvider =
                string.IsNullOrWhiteSpace(provider)
                    ? "ollama"
                    : provider.Trim().ToLowerInvariant();
            if (string.Equals(
                    normalizedProvider,
                    "ollama",
                    StringComparison.Ordinal))
                return "";

            string endpointIdentity = (endpoint ?? "").Trim();
            string normalizedEndpoint;
            string error;
            if (AiEndpointPolicy.TryNormalize(
                    endpointIdentity,
                    out normalizedEndpoint,
                    out error))
                endpointIdentity = normalizedEndpoint;
            if (string.IsNullOrEmpty(endpointIdentity)) return "";

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(
                    StrictUtf8.GetBytes(
                        normalizedProvider + "\n" + endpointIdentity));
                return ToHex(hash);
            }
        }

        private string SelectedCredentialEndpoint()
        {
            return string.Equals(
                    Provider,
                    "ollama",
                    StringComparison.OrdinalIgnoreCase)
                ? Endpoint
                : OpenAiBaseUrl;
        }

        /// <summary>
        /// A shallow snapshot for building the ACTIVE backend's <see cref="AiBrain"/>: identical to this
        /// instance except that, when a cloud <see cref="Provider"/> is selected, the cloud models are
        /// promoted into <see cref="TextModel"/>/<see cref="VisionModel"/> so the brain's model-selection
        /// path uses the active slot's models. Local-only returns an equivalent copy. Read-only — callers
        /// must not persist it (it shares the credential/collection references with this instance).
        /// </summary>
        internal AiSettings ActiveSlotSnapshot()
        {
            AiSettings clone = (AiSettings)MemberwiseClone();
            if (!string.IsNullOrEmpty(Provider))
            {
                clone.TextModel = CloudTextModel ?? "";
                clone.VisionModel = CloudVisionModel ?? "";
            }
            return clone;
        }

        /// <summary>
        /// Select a provider and return the endpoint its UI should display. Preset providers may
        /// replace the shared active endpoint; Custom always restores its remembered endpoint.
        /// </summary>
        internal string SelectProviderEndpoint(
            string provider,
            bool prefillPreset)
        {
            RememberSelectedCustomEndpoint();
            AiProviders.Preset preset = AiProviders.Get(provider);
            Provider = preset.Id;

            if (string.Equals(
                    Provider,
                    "ollama",
                    StringComparison.OrdinalIgnoreCase))
            {
                string endpoint = Endpoint;
                if (prefillPreset || string.IsNullOrWhiteSpace(endpoint))
                    endpoint = preset.BaseUrl;
                Endpoint = (endpoint ?? "").Trim();
                return Endpoint;
            }

            if (string.Equals(
                    Provider,
                    "custom",
                    StringComparison.OrdinalIgnoreCase))
            {
                OpenAiBaseUrl = (CustomOpenAiBaseUrl ?? "").Trim();
                return OpenAiBaseUrl;
            }

            string compatibleEndpoint = OpenAiBaseUrl;
            if (prefillPreset || string.IsNullOrWhiteSpace(compatibleEndpoint))
                compatibleEndpoint = preset.BaseUrl;
            OpenAiBaseUrl = (compatibleEndpoint ?? "").Trim();
            return OpenAiBaseUrl;
        }

        internal void UpdateSelectedProviderEndpoint(string endpoint)
        {
            endpoint = (endpoint ?? "").Trim();
            if (string.Equals(
                    Provider,
                    "ollama",
                    StringComparison.OrdinalIgnoreCase))
            {
                Endpoint = endpoint;
                return;
            }

            OpenAiBaseUrl = endpoint;
            if (string.Equals(
                    Provider,
                    "custom",
                    StringComparison.OrdinalIgnoreCase))
                CustomOpenAiBaseUrl = endpoint;
        }

        private void RememberSelectedCustomEndpoint()
        {
            if (string.Equals(
                    Provider,
                    "custom",
                    StringComparison.OrdinalIgnoreCase))
                CustomOpenAiBaseUrl = (OpenAiBaseUrl ?? "").Trim();
        }

        private string GetApiKey(string provider, string endpoint)
        {
            string scope = BuildCredentialScope(provider, endpoint);
            if (string.IsNullOrEmpty(scope) || ApiKeysEnc == null)
                return "";
            string encrypted;
            string clear;
            return ApiKeysEnc.TryGetValue(scope, out encrypted) &&
                   TryDecryptApiKey(encrypted, out clear)
                ? clear
                : "";
        }

        internal bool TrySetApiKey(string value, out string error)
        {
            error = "";
            value = value ?? "";
            string scope = BuildCredentialScope(
                Provider,
                SelectedCredentialEndpoint());
            if (string.IsNullOrEmpty(scope))
            {
                if (value.Length == 0) return true;
                error =
                    "Select a provider and valid endpoint before entering an API key.";
                return false;
            }
            if (ApiKeysEnc == null)
                ApiKeysEnc = new Dictionary<string, string>(
                    StringComparer.Ordinal);

            if (string.IsNullOrEmpty(value))
            {
                ApiKeysEnc.Remove(scope);
                return true;
            }
            // Invalid input or a transient DPAPI failure must not erase a previously durable key.
            if (value.Length > MaximumApiKeyCharacters)
            {
                error =
                    "The API key is too long. Enter at most " +
                    MaximumApiKeyCharacters.ToString(
                        CultureInfo.InvariantCulture) +
                    " characters.";
                return false;
            }
            if (!ApiKeysEnc.ContainsKey(scope) &&
                ApiKeysEnc.Count >= MaximumApiKeyScopes)
            {
                error =
                    "DesktopAICompanion already stores the maximum of " +
                    MaximumApiKeyScopes.ToString(
                        CultureInfo.InvariantCulture) +
                    " provider/endpoint API keys. Clear an existing key before adding another.";
                return false;
            }
            string encrypted;
            if (!TryEncryptApiKey(value, out encrypted))
            {
                error =
                    "Windows could not encrypt this API key for the current user. " +
                    "The previously saved key was left unchanged.";
                return false;
            }
            ApiKeysEnc[scope] = encrypted;
            return true;
        }

        private bool NormalizeApiKeyScopes()
        {
            bool changed = ApiKeysEnc == null;
            var normalized =
                new Dictionary<string, string>(StringComparer.Ordinal);
            if (ApiKeysEnc != null)
            {
                var keys = new List<string>(ApiKeysEnc.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (string scope in keys)
                {
                    if (normalized.Count >= MaximumApiKeyScopes)
                    {
                        changed = true;
                        break;
                    }
                    string encrypted = ApiKeysEnc[scope];
                    if (!IsHex(scope, 64) ||
                        !IsWellFormedEncryptedApiKey(encrypted))
                    {
                        changed = true;
                        continue;
                    }
                    normalized.Add(scope, encrypted);
                }
            }
            if (!changed && normalized.Count == ApiKeysEnc.Count)
            {
                foreach (KeyValuePair<string, string> item in normalized)
                {
                    string existing;
                    if (!ApiKeysEnc.TryGetValue(item.Key, out existing) ||
                        !string.Equals(
                            existing,
                            item.Value,
                            StringComparison.Ordinal))
                    {
                        changed = true;
                        break;
                    }
                }
            }
            ApiKeysEnc = normalized;
            return changed;
        }

        private static bool TryEncryptApiKey(
            string value,
            out string encrypted)
        {
            encrypted = "";
            try
            {
                if (string.IsNullOrEmpty(value) ||
                    value.Length > MaximumApiKeyCharacters)
                    return false;
                byte[] bytes = StrictUtf8.GetBytes(value);
                byte[] protectedBytes = ProtectedData.Protect(
                    bytes,
                    null,
                    DataProtectionScope.CurrentUser);
                encrypted = Convert.ToBase64String(protectedBytes);
                return encrypted.Length <= MaximumEncryptedApiKeyCharacters;
            }
            catch
            {
                encrypted = "";
                return false;
            }
        }

        private static bool TryDecryptApiKey(
            string encrypted,
            out string value)
        {
            value = "";
            try
            {
                if (string.IsNullOrEmpty(encrypted) ||
                    encrypted.Length > MaximumEncryptedApiKeyCharacters)
                    return false;
                byte[] bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(encrypted),
                    null,
                    DataProtectionScope.CurrentUser);
                if (bytes.Length > MaximumApiKeyCharacters * 4)
                    return false;
                value = StrictUtf8.GetString(bytes);
                return value.Length > 0 &&
                       value.Length <= MaximumApiKeyCharacters;
            }
            catch
            {
                value = "";
                return false;
            }
        }

        private static bool IsWellFormedEncryptedApiKey(string encrypted)
        {
            if (string.IsNullOrEmpty(encrypted) ||
                encrypted.Length > MaximumEncryptedApiKeyCharacters)
                return false;
            try
            {
                byte[] bytes = Convert.FromBase64String(encrypted);
                return bytes.Length > 0 &&
                       bytes.Length <= MaximumEncryptedApiKeyCharacters;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsHex(string value, int length)
        {
            if (string.IsNullOrEmpty(value) || value.Length != length)
                return false;
            foreach (char character in value)
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f')))
                    return false;
            return true;
        }

        private static string ToHex(byte[] value)
        {
            var result = new StringBuilder(value.Length * 2);
            for (int index = 0; index < value.Length; index++)
                result.Append(
                    value[index].ToString(
                        "x2",
                        CultureInfo.InvariantCulture));
            return result.ToString();
        }

        private bool NormalizeDisabledSources()
        {
            bool changed = DisabledSources == null;
            var normalized = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (DisabledSources != null)
            {
                foreach (string sourceValue in DisabledSources)
                {
                    if (normalized.Count >= MaximumDisabledSources)
                    {
                        changed = true;
                        break;
                    }
                    string source = sourceValue;
                    changed |= NormalizeString(
                        ref source,
                        "",
                        MaximumSourceCharacters);
                    if (string.IsNullOrWhiteSpace(source) || !seen.Add(source))
                    {
                        changed = true;
                        continue;
                    }
                    normalized.Add(source);
                }
            }
            if (!changed && DisabledSources.Count == normalized.Count)
            {
                for (int i = 0; i < normalized.Count; i++)
                    if (!string.Equals(
                            DisabledSources[i],
                            normalized[i],
                            StringComparison.Ordinal))
                    {
                        changed = true;
                        break;
                    }
            }
            DisabledSources = normalized;
            return changed;
        }

        // Schema v2: the LOCAL slot is fixed (Endpoint/TextModel/VisionModel = Ollama), so Provider is now the
        // CLOUD selector only. "" = no cloud (local-only); the legacy local ids (ollama/lmstudio/llamacpp)
        // are no longer valid selectors and are migrated/clamped to "".
        private static bool IsKnownProvider(string provider)
        {
            switch (provider)
            {
                case "":            // no cloud (local-only)
                case "openrouter":
                case "openai":
                case "custom":
                    return true;
                default:
                    return false;
            }
        }

        // Which protocol the LOCAL slot speaks (see LocalBackendKind's doc comment).
        private static bool IsKnownLocalBackendKind(string kind)
        {
            switch (kind)
            {
                case "ollama":
                case "openai-compat":
                    return true;
                default:
                    return false;
            }
        }

        private enum ReadResult
        {
            Missing,
            Loaded,
            Unreadable,
            FutureSchema
        }
    }

    /// <summary>Bounds model identifiers and removes UI/log injection characters.</summary>
    internal static class AiModelPolicy
    {
        public static bool TryNormalize(string value, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(value)) return false;
            string candidate = value.Trim();
            if (candidate.Length < 1 ||
                candidate.Length > AiSettings.MaximumModelCharacters)
                return false;

            for (int index = 0; index < candidate.Length; index++)
            {
                char character = candidate[index];
                if (char.IsControl(character) || char.IsWhiteSpace(character))
                    return false;
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 >= candidate.Length ||
                        !char.IsLowSurrogate(candidate[index + 1]))
                        return false;
                    index++;
                }
                else if (char.IsLowSurrogate(character))
                {
                    return false;
                }
                else
                {
                    UnicodeCategory category = char.GetUnicodeCategory(character);
                    if (category == UnicodeCategory.Format ||
                        category == UnicodeCategory.LineSeparator ||
                        category == UnicodeCategory.ParagraphSeparator)
                        return false;
                }
            }

            normalized = candidate;
            return true;
        }

        public static string NormalizeOrThrow(string value, string parameterName)
        {
            string normalized;
            if (!TryNormalize(value, out normalized))
                throw new ArgumentException(
                    "Enter a model identifier without whitespace or control characters.",
                    parameterName);
            return normalized;
        }

        // Known multimodal (image-capable) families, matched case-insensitively as substrings of the
        // model id. Provider-agnostic and deliberately loose so a genuine vision model is rarely
        // mis-flagged; a model with no marker is treated as text-only and gets an advisory.
        private static readonly string[] VisionModelMarkers =
        {
            "llava", "bakllava", "moondream", "vision", "-vl", "vl-", "vl:", "pixtral",
            "minicpm-v", "minicpm-o", "gemma3", "gemma-3", "mllama", "llama4", "llama-4",
            "internvl", "cogvlm", "gpt-4o", "gpt-4-turbo", "gpt-4.1", "chatgpt-4o",
            "claude-3", "claude-4", "claude-opus", "claude-sonnet", "claude-haiku",
            "gemini-1.5", "gemini-2", "gemini-pro-vision", "glm-4v", "deepseek-vl",
            "phi-3-vision", "phi3.5-vision", "phi-4-multimodal", "smolvlm", "aya-vision",
        };

        /// <summary>Best-effort, name-based guess of whether a model accepts image input, so the
        /// options UI can advise when a text-only model is picked for the vision feature. Advisory
        /// only, never a hard gate. Empty -> true (no advisory).</summary>
        public static bool LooksVisionCapable(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return true;
            string m = model.Trim().ToLowerInvariant();
            foreach (string marker in VisionModelMarkers)
                if (m.IndexOf(marker, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // Well-known naming conventions for models tuned/fine-tuned to drop refusal behavior, matched
        // case-insensitively as substrings of the model id. Deliberately conservative (only self-described
        // or widely-recognized markers) — a model with none of these is simply untagged, not "safe"; this is
        // a positive advisory tag for model-picker UI (e.g. surfacing a model that will actually commit to a
        // profane persona), never a claim about actual content or a hard filter.
        private static readonly string[] UncensoredModelMarkers =
        {
            "dolphin", "uncensored", "abliterated", "unfiltered",
        };

        /// <summary>Best-effort, name-based guess of whether a model is tuned to drop refusal/safety
        /// behavior, so the options UI can tag it for personas that need a model to actually comply
        /// (e.g. an insult-comic persona). Advisory only. Empty/unknown -> false (no claim).</summary>
        public static bool LooksUncensored(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return false;
            string m = model.Trim().ToLowerInvariant();
            foreach (string marker in UncensoredModelMarkers)
                if (m.IndexOf(marker, StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }
}
