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
        /// <summary>The full cross-session lock budget: what a background writer waits for a peer process. Internal
        /// for the probes, which are the only callers that save with it (the UI uses UiSaveBudgetMilliseconds).</summary>
        internal const int ProcessLockTimeoutMilliseconds = 10000;
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

        // No [JsonIgnore] on these: STJ never serializes a non-public field (nor a static member), so the
        // attribute was inert here and on FilePath below (F097). The persisted set is the PUBLIC instance fields;
        // see BuildPersistedFieldNames.
        private bool _writesBlockedByFutureSchema;

        /// <summary>True on a copy made by <see cref="CloneForBrain"/>: read by the brain, never saved.</summary>
        private bool _detachedCopy;

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

        /// <summary>
        /// Fast text-only model used for OCR-based commentary.
        ///
        /// gemma3:4b, matching <see cref="VisionModel"/> on purpose: one 3.3 GB download serves both
        /// jobs, and an identical id lets the backend keep a single model resident instead of swapping
        /// between two. Measured 2026-09-10 over 15 generations per candidate, it parsed 15/15 and led
        /// on every axis, so this is the recommendation rather than a guess.
        ///
        /// Was llama3.1:8b, which was never in that comparison and which the Readme's model tables do
        /// not mention at all -- so a fresh install pointed its text model at something the user very
        /// likely did not have, and before BUG-002 that failed silently and looked like a companion
        /// with nothing to say. Changing the default is the other half of that fix: BUG-002 makes a
        /// missing model SAY so, this stops a fresh install starting out missing one.
        /// </summary>
        public string TextModel = "gemma3:4b";

        /// <summary>
        /// Multimodal model used when <see cref="UseVision"/> is on (much slower/heavier than OCR).
        /// Default is a small, fast vision model; bigger ones (e.g. mistral-small3.2:24b) read the
        /// screen better but can take a minute per glance. See the grimoire for recommendations.
        /// </summary>
        public string VisionModel = "gemma3:4b";

        /// <summary>
        /// When true, send a downscaled screenshot to the vision model instead of OCR text, for EVERY remark
        /// about the screen: the hotkey, the tray row and the unprompted drop alike. Owner decision 2026-09-29
        /// (BUG-010, docs/ISSUES-post-1.0.0.md): "how else would it know what is on the screen to react to?"
        /// This comment, the pane label and the brain's routing comment used to say "explicit asks only", a
        /// rule written for the module's own idle loop (backlog 6.2, when a full-screen glance took about a
        /// minute) that outlived the loop; the drop responder never honoured it, so the code stood and the
        /// words changed. The poke reaction is the one path that stays text-only, because a vision glance is
        /// too slow to feel like a reaction to a click. OCR is the fallback when this is off or the chosen model
        /// cannot see.
        /// </summary>
        public bool UseVision = false;

        /// <summary>Per-request HTTP timeout. Generous because a cold vision model on a full-screen
        /// image can take a minute or more.</summary>
        public int TimeoutSeconds = 120;

        /// <summary>Full path to tesseract.exe. Empty means "find <c>tesseract</c> on PATH".</summary>
        public string TesseractPath = "";

        // ---- Phase 5: persona ----------------------------------------------

        /// <summary>The pet's name, injected into its persona. Empty -> a generic "desktop companion".</summary>
        public string CompanionName = "eSheep";

        /// <summary>Optional name the pet may address you by. Empty -> it won't use one.</summary>
        public string UserName = "";

        /// <summary>Which curated character voice the pet speaks in: a known id from
        /// <see cref="Dispositions.All"/> (e.g. "ted-lasso", "samuel"). Replaces the older separate
        /// Personality-blurb + SpeechPattern-id pair (schema v2), which let a tone preset and a
        /// delivery style combine into incoherent pairings; each disposition now bakes tone and
        /// delivery into one instruction.</summary>
        public string Disposition = Dispositions.DefaultId;

        /// <summary>
        /// Explicit consent for sending screen/OCR/window context to a non-loopback provider. AiBrainModule
        /// enforces it before any cloud request, four times over: the audition, TestConnectionAsync, CreateBrain
        /// and CanUse each refuse a non-loopback provider without it. (AiEndpointPolicy carries no consent
        /// logic; this comment said it did, F094.) It does not gate a coding-agent CLI (1.3.0): choosing the CLI
        /// under "Brain runs on" is the consent, in the owner's words "if the feature is enabled, the user is well
        /// aware", and the CLI card says what goes through it (docs/DESIGN-REGISTER.md, feature/cli-backend).
        /// </summary>
        public bool CloudDataConsent = false;

        // ---- fortunes (Phase A) --------------------------------------------
        // Seven Fortunes-era fields lived here until 2026-09-30 -- SpicyFortunes, SpicyTier, SpicyOnly,
        // NoProfanity, SmartFortunes, DisabledSources and DisabledGenres -- persisted by this class and read by
        // nothing in modules/AiBrain (the Fortunes module has its own FortuneSettings). A note kept them by
        // saying that removing a persisted field changes the on-disk schema; the random-drop paragraph below
        // already refutes that for this serializer, and it holds here too: STJ routes an unmatched key from an
        // existing file into ExtensionData, PersistedFieldNames is built by reflection, and SaveMerged re-emits
        // the extension data, so the old keys round-trip inert and no migration is needed (F093). The decision
        // the NoProfanity paragraph carried -- on the AI path the consent IS the disposition, so no profanity
        // switch may ever gate a persona the user chose by name -- is recorded in docs/DESIGN-REGISTER.md
        // under `#### fix/deadcode`.

        // The random-drop trio (RandomDropEnabled / RandomDropMinutes / RandomDropJitterMinutes) lived here
        // and is deleted. Unprompted commentary rides the HOST's global "randomly drop a fortune or insight"
        // schedule, so the live values come from AppSettingsStore and nothing in AiBrainModule ever read
        // these. They were not harmless: they are why the settings file looked as though the module owned a
        // second, competing trigger group. No migration is needed -- STJ routes an unmatched key from an
        // existing ai-settings.json into ExtensionData, where it is inert.

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
        /// unavailable. The runtime half is FallbackBackend, built by AiBrainModule.CreateBrain whenever a cloud
        /// provider is selected. (This said the fallback was "wired in a later change" long after it was, F094.)
        /// </summary>
        public bool UseLocalFallback = true;

        /// <summary>
        /// The coding-agent CLI the brain runs on: <c>""</c> (none: the local slot or the cloud provider above decide, as
        /// before) | <c>"claude"</c> (Claude Code) | <c>"codex"</c> (Codex). Lane feature/cli-backend, aibrain 1.3.0. When
        /// set it wins over both slots and EVERY call goes through that CLI. A file written before 1.3.0 has no such key,
        /// and the deserializer leaves this field at its initialiser, so an existing install lands on exactly the slot it
        /// was on. An unknown value normalizes to <c>""</c>, the conservative reading.
        /// </summary>
        public string CliBackend = "";

        /// <summary>
        /// The cloud provider last chosen, kept while "Brain runs on" is the local model so that choosing Cloud provider
        /// again restores it (feature/cli-backend, 1.3.0): the cloud dropdown lost its "(none)" to the radio, and
        /// <see cref="Provider"/> still means "the cloud is primary" when set. "" until a provider was chosen; an id no
        /// preset knows normalizes to "".
        /// </summary>
        public string LastCloudProvider = "";

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

        /// <summary>
        /// While Remembrance runs a local model (whisper, or its Ollama summary), send nothing to the LOCAL slot:
        /// decline every remark the way the fullscreen stand-down does, so the free local fortunes speak instead,
        /// warm nothing and evict nothing. Unlike the fullscreen stand-down it RELEASES nothing either, because AI
        /// Brain's model can be the very model Remembrance is using on the same server (Addendum 1 of the brief).
        /// Remembrance says so through its `remembrance.busy` flag on the host's shared context (see
        /// RemembranceBusyFlag). A cloud provider's requests go ahead; only a cloud fallback to the local slot waits.
        ///
        /// ON by default, for the reason the fullscreen switch is: the cost of being wrong is a fortune instead
        /// of a quip, against a remark that evicts the model of a transcription in progress the other way. The
        /// owner asked for it on 2026-10-02 in exactly those terms. An existing settings file without the key
        /// reads as on: the deserializer leaves a field it finds no key for at this initialiser.
        /// </summary>
        public bool StandDownForRemembrance = true;

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

        // A PROPERTY by choice, not by requirement: STJ accepts a [JsonExtensionData] FIELD too (measured on
        // .NET 10 with IncludeFields, F097), which is why BuildPersistedFieldNames still tests public fields for
        // the attribute -- the day the sink moves onto a field, that test is what keeps the sink's own name out
        // of the persisted set. Kept non-null with an Ordinal comparer so deserialization adds unknown fields
        // into this instance, which is what round-trips a future-same-schema doc's unknown data.
        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtensionData { get; set; } =
            new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        public static string FilePath
        {
            get { return AiPaths.AiSettingsFile; }
        }

        /// <summary>
        /// One line about how this instance came to be, when the load was anything other than an ordinary read
        /// of the primary: recovered from the backup, reset to defaults, written by a newer build, the lock
        /// timed out, the host gave no storage. Null for the ordinary case. The module logs it once at Init.
        /// Until 2026-09-29 every one of those paths produced a settings object and nothing in the diagnostic
        /// log (F096, F098). Names only a failure category and a file NAME beside the store, never a path, per
        /// AiBrain.LogSink's contract.
        /// </summary>
        [JsonIgnore]
        public string LoadWarning { get; private set; }

        /// <summary>Load settings, writing a default file on first run. Never throws.</summary>
        public static AiSettings Load()
        {
            return LoadWithin(ProcessLockTimeoutMilliseconds);
        }

        /// <summary>
        /// Load within one lock budget. The shipped Init uses the full cross-session budget; the self-test uses
        /// a short one to prove the timeout path SAYS something. The live budget is deliberately NOT shortened
        /// (F096): a save that returns false is retried by the next click, but a load that gives up yields
        /// defaults with every write blocked until restart, so a transient stall is waited out rather than
        /// converted into a session of silent non-persistence. What changed is that giving up is no longer
        /// silent: the returned instance carries a <see cref="LoadWarning"/> the module logs.
        /// </summary>
        internal static AiSettings LoadWithin(int timeoutMilliseconds)
        {
            lock (ProcessLock)
            {
                if (!AiPaths.HasRoot)
                {
                    // No storage, no file: defaults, nothing saved, said out loud (N-gates-02). See AiPaths.
                    var unrooted = new AiSettings { _writesBlockedByFutureSchema = true };
                    unrooted.LoadWarning =
                        "the host gave this module no storage directory; running on defaults and saving nothing";
                    return unrooted;
                }
                try
                {
                    return WithFileLock(LoadCore, timeoutMilliseconds);
                }
                catch (Exception ex)
                {
                    // A lock/read failure must not turn into a later blind overwrite of settings that
                    // this process never observed.
                    var blocked = new AiSettings { _writesBlockedByFutureSchema = true };
                    blocked.LoadWarning = DescribeLoadFailure(ex);
                    return blocked;
                }
            }
        }

        private static string DescribeLoadFailure(Exception ex)
        {
            return "AI settings could not be loaded (" + (ex == null ? "unknown" : ex.GetType().Name) +
                   "); running on defaults, and nothing will be saved this session so the file is not overwritten blind";
        }

        /// <summary>
        /// The budget a UI-thread save gets. Short on purpose: the 10 s cross-session timeout is sized for
        /// a background writer waiting out a peer process, and spending it on the message thread is a
        /// ten-second freeze of the settings window.
        ///
        /// That distinction was DOCUMENTED and not implemented. SaveWithin's own summary said "UI callers
        /// use a short budget so a hung peer cannot freeze the message thread for the full cross-session
        /// timeout", and every UI caller went through Save() at 10,000 ms; SaveWithin's only caller was a
        /// self-test. 1500 ms is far beyond an uncontended save (immediate) and beyond two instances
        /// overlapping, while staying inside what reads as a responsive click.
        /// </summary>
        internal const int UiSaveBudgetMilliseconds = 1500;

        /// <summary>
        /// Persist settings within one aggregate lock budget. UI callers use <see cref="UiSaveBudgetMilliseconds"/>
        /// so a hung peer cannot freeze the message thread for the full cross-session timeout; the probes save
        /// with <see cref="ProcessLockTimeoutMilliseconds"/>. (A parameterless Save() with the full budget stood
        /// beside this until 2026-09-30, read like production API and was called only by probes, F097.)
        /// Returns false when durable storage is unavailable or blocked.
        /// </summary>
        internal bool SaveWithin(int timeoutMilliseconds)
        {
            // A copy made for the brain factory is never persisted (CloneForBrain): refused before the lock, so
            // it does not even touch the .lock file.
            if (_detachedCopy) return false;
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
            string failure;
            ReadResult result = TryRead(FilePath, out loaded, out failure);
            if (result == ReadResult.Loaded || result == ReadResult.FutureSchema)
            {
                loaded._writesBlockedByFutureSchema =
                    result == ReadResult.FutureSchema;
                bool changed = loaded.Normalize();
                // Said when the corrected file could not be written (RA-080): the values in memory are the
                // normalized ones, the file still holds what it held, and the next successful save rewrites it.
                if (changed && result == ReadResult.Loaded && !loaded.SaveCore())
                    loaded.LoadWarning =
                        "ai-settings.json needed normalizing and the corrected file could not be written; " +
                        "running on the corrected values until a save succeeds";
                if (result == ReadResult.FutureSchema)
                    loaded.LoadWarning =
                        "ai-settings.json was written by a newer version (schema " +
                        loaded.SchemaVersion.ToString(CultureInfo.InvariantCulture) + "; this build understands " +
                        CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture) +
                        "); it is read, and nothing will be saved this session so it is not damaged";
                loaded.CaptureBaseline();
                return loaded;
            }

            // The primary is HELD, not broken: a sharing violation or an access denial says nothing about the
            // document, so it is neither copied aside as corrupt nor restored over from the backup (R-018).
            // Defaults for this session, every write blocked so a file this process never read is not overwritten
            // blind, and the reason said once; the next launch reads the file as it was.
            if (result == ReadResult.Locked)
            {
                var held = new AiSettings { _writesBlockedByFutureSchema = true };
                held.LoadWarning =
                    "ai-settings.json is held open by another process (" + (failure ?? "IOException") +
                    "); running on defaults, and nothing will be saved this session so the file is not overwritten blind";
                return held;
            }

            // The primary exists and could not be read. Keep it beside the store BEFORE anything overwrites it.
            // The recovery below restores the backup over the primary with no rotation (so the good .bak is not
            // itself rotated over), which until 2026-09-29 meant the rejected document, and with it the last
            // save and whatever edit broke it, was destroyed with nothing logged (F098). The host's own settings
            // store has kept its corrupt primary as `<name>.corrupt-<stamp>-<guid>.json` since before 1.0.0;
            // this is the same shape, and the copy is what makes a "corrupt" verdict checkable afterwards.
            bool primaryRemoved = true;
            string preserved = result == ReadResult.Unreadable ? PreserveCorruptPrimary(out primaryRemoved) : null;

            string backupFailure;
            ReadResult backupResult = TryRead(FilePath + ".bak", out loaded, out backupFailure);
            if (backupResult == ReadResult.Loaded ||
                backupResult == ReadResult.FutureSchema)
            {
                loaded._writesBlockedByFutureSchema =
                    backupResult == ReadResult.FutureSchema;
                loaded.Normalize();
                if (backupResult == ReadResult.Loaded)
                    loaded._writesBlockedByFutureSchema =
                        !loaded.RestorePrimaryWithoutRotatingBackup();
                loaded.LoadWarning = DescribeRecovery(
                    result, failure, "recovered from the backup", preserved, primaryRemoved, null);
                loaded.CaptureBaseline();
                return loaded;
            }

            // No legacy-root read here. A branch behind AiPaths.LegacyMigrationEnabled (hard-coded false, and
            // its "legacy" path was this store's own file) sat here until 2026-09-30; the import of a base
            // ai-settings.json is AiBrainModule.MigrateFromBaseIfNeeded, run at Init before this Load (F088).
            AiSettings defaults = new AiSettings();
            // Its result used to be discarded (RA-080): a first run on a read-only or full disk produced defaults,
            // no file, and nothing in the log, and the brain then ran on defaults with every save failing quietly.
            bool defaultsWritten = defaults.SaveCore();
            defaults.CaptureBaseline();
            if (result != ReadResult.Missing)
                defaults.LoadWarning =
                    DescribeRecovery(result, failure, "reset to defaults", preserved, primaryRemoved, backupFailure) +
                    (defaultsWritten ? "" : "; the default file could not be written either");
            else if (!defaultsWritten)
                defaults.LoadWarning =
                    "ai-settings.json was missing and the default file could not be written; running on defaults until a save succeeds";
            return defaults;
        }

        /// <summary>The LoadWarning for a recovery: what was wrong, what was done, where the rejected file went, and
        /// why the backup did not serve when it did not.</summary>
        private static string DescribeRecovery(
            ReadResult primary, string failure, string action, string preserved, bool primaryRemoved, string backupFailure)
        {
            string why = primary == ReadResult.Missing
                ? "ai-settings.json was missing"
                : "ai-settings.json could not be read (" + (failure ?? "unreadable") + ")";
            string kept = primary != ReadResult.Unreadable
                ? ""
                : preserved != null
                    ? "; the rejected file is kept beside the store as " + Path.GetFileName(preserved) +
                      // Copy succeeded, Delete did not (a handle without delete sharing): the copy is real and the
                      // primary stays where it is, overwritten in place. This used to read as "could not be kept and
                      // was overwritten" while the copy sat on disk (R-018).
                      (primaryRemoved ? "" : ", and the primary could not be removed, so it is overwritten in place")
                    : "; the rejected file could not be kept and was overwritten";
            // Only a missing or an unreadable .bak reaches the reset-to-defaults branch, and the two ask different
            // things of the user; TryRead's answer for it used to be dropped (RA-081).
            string backup = backupFailure == null
                ? ""
                : backupFailure == "missing"
                    ? "; there was no ai-settings.json.bak to recover from"
                    : "; ai-settings.json.bak could not be read either (" + backupFailure + ")";
            return why + "; " + action + kept + backup;
        }

        /// <summary>
        /// Copy the unreadable primary aside as <c>ai-settings.corrupt-&lt;utc stamp&gt;-&lt;guid&gt;.json</c> and
        /// remove it, so the recovery writes a fresh primary and never rotates the rejected bytes into the
        /// backup. Returns the copy's path, or null when it could not be kept (the primary is then left in place
        /// and overwritten as before, and the warning says so). <paramref name="primaryRemoved"/> is false when the
        /// copy exists but the primary could not be deleted: Copy needs only read sharing while Delete needs
        /// delete sharing, so a primary another process holds open passes the first and fails the second, and one
        /// try around both answered null with the copy already on disk (R-018).
        /// </summary>
        private static string PreserveCorruptPrimary(out bool primaryRemoved)
        {
            primaryRemoved = false;
            string primary = FilePath;
            string recovery;
            try
            {
                string directory = Path.GetDirectoryName(primary);
                recovery = Path.Combine(
                    directory,
                    Path.GetFileNameWithoutExtension(primary) + ".corrupt-" +
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + "-" +
                    Guid.NewGuid().ToString("N") + Path.GetExtension(primary));
                File.Copy(primary, recovery, false);
            }
            catch
            {
                return null;
            }
            try
            {
                File.Delete(primary);
                primaryRemoved = true;
            }
            catch { }
            return recovery;
        }

        private bool SaveMerged()
        {
            if (_writesBlockedByFutureSchema ||
                SchemaVersion > CurrentSchemaVersion)
                return false;

            AiSettings existing;
            string existingFailure;
            ReadResult result = TryRead(FilePath, out existing, out existingFailure);
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
                // The file is missing, held or unreadable at save time, so the document to write is THIS instance's,
                // extension data included. An empty seed copied only the declared fields, so the unknown keys the
                // read had routed into ExtensionData (a newer same-schema build's, F093) fell off the disk on the
                // first save after the file went away and were never written again (RA-082).
                target = (JsonObject)current.DeepClone();
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

            changed |= NormalizeString(
                ref Endpoint, "http://localhost:11434", MaximumEndpointCharacters);
            // Empty is not a selector. Every other clamp in this method restores a default for an invalid
            // value; Endpoint and Hotkey kept an empty string (NormalizeString substitutes its fallback only for
            // null), and the consumers then disagreed about what "" meant: OllamaClient and the VRAM line read it
            // as localhost, CanUse refused it and silently disabled the brain, and an empty hotkey registered a
            // no-op handle while HotkeyEnabled stayed true (F099). The pane cannot write either (it skips blank
            // values), so the only source is a hand-edited file, which is what a normalizer is for.
            if (Endpoint.Length == 0)
            {
                Endpoint = "http://localhost:11434";
                changed = true;
            }
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
            // The residency token, held to the three values its consumers compare against (RA-083): a hand-edited
            // "keep " kept its space in the file and read as "unload" everywhere (KeepAliveForRequests' fallback for
            // an unrecognised value) while the pane showed the unload label, and the file healed only on the next
            // Apply. Same shape as the LocalBackendKind clamp above.
            changed |= NormalizeString(ref ModelResidency, ResidencyUnload, 16);
            string normalizedResidency = ModelResidency.ToLowerInvariant();
            if (!string.Equals(ModelResidency, normalizedResidency, StringComparison.Ordinal))
            {
                ModelResidency = normalizedResidency;
                changed = true;
            }
            if (!IsKnownResidency(ModelResidency))
            {
                ModelResidency = ResidencyUnload;
                changed = true;
            }
            // Keep this fallback in step with the TextModel field default above: it is a SECOND
            // source of truth for the same value, so changing only the declaration would leave a
            // blank or invalid setting normalizing back to the old model.
            changed |= NormalizeModel(ref TextModel, "gemma3:4b");
            changed |= NormalizeModel(ref VisionModel, "gemma3:4b");
            changed |= NormalizeString(ref TesseractPath, "", MaximumPathCharacters);
            changed |= NormalizeString(ref CompanionName, "eSheep", MaximumNameCharacters);
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
            // The CLI selector, held to its three values (feature/cli-backend): a hand-edited "Claude " or an id a later
            // version adds reads as "" (no CLI) rather than as a CLI nothing here knows how to run.
            changed |= NormalizeString(ref CliBackend, "", 16);
            string normalizedCli = DesktopAICompanion.CodingAgent.CodingAgents.IdOf(
                DesktopAICompanion.CodingAgent.CodingAgents.FromId(CliBackend));
            if (!string.Equals(CliBackend, normalizedCli, StringComparison.Ordinal))
            {
                CliBackend = normalizedCli;
                changed = true;
            }
            changed |= NormalizeString(ref LastCloudProvider, "", 32);
            string normalizedLast = LastCloudProvider.ToLowerInvariant();
            if (!IsKnownProvider(normalizedLast) || normalizedLast.Length == 0) normalizedLast = "";
            if (!string.Equals(LastCloudProvider, normalizedLast, StringComparison.Ordinal))
            {
                LastCloudProvider = normalizedLast;
                changed = true;
            }
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
            if (Hotkey.Length == 0)
            {
                Hotkey = "Ctrl+Alt+P";   // see the Endpoint clamp above (F099)
                changed = true;
            }
            changed |= NormalizeString(ref OllamaPath, "", MaximumPathCharacters);
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
                // The local defaults, kept in step with the field declarations above.
                TextModel = "gemma3:4b";
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

        // Every caller passes its own budget (LoadWithin its lock budget, SaveWithin what remains of its). A
        // one-argument overload defaulting to ProcessLockTimeoutMilliseconds stood here until 2026-09-30 with no
        // caller since F096 made Load pass its budget explicitly (R-019, RA-084).
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
                // Neither attribute sits on a public field today, so neither branch trips; both are guards for
                // the field that one day carries one, which would otherwise leak its own name into the merged
                // save (F097). STJ does accept a field as the extension-data sink, so the second is not dead.
                if (field.IsDefined(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true) ||
                    field.IsDefined(typeof(System.Text.Json.Serialization.JsonExtensionDataAttribute), true))
                    continue;
                names.Add(field.Name);
            }
            names.Sort(StringComparer.Ordinal);
            return names.ToArray();
        }

        /// <param name="failure">Why the read did not yield a document: the exception's type name (a message
        /// can carry the path), "empty document", or "missing". Null when it did.</param>
        private static ReadResult TryRead(string path, out AiSettings settings, out string failure)
        {
            settings = null;
            failure = null;
            try
            {
                if (!File.Exists(path))
                {
                    failure = "missing";
                    return ReadResult.Missing;
                }
                string json = ReadBoundedUtf8(path, MaximumSettingsBytes);
                settings = JsonSerializer.Deserialize<AiSettings>(json, JsonOptions);
                if (settings == null)
                {
                    failure = "empty document";
                    return ReadResult.Unreadable;
                }
                return settings.SchemaVersion > CurrentSchemaVersion
                    ? ReadResult.FutureSchema
                    : ReadResult.Loaded;
            }
            catch (UnauthorizedAccessException ex)
            {
                // HELD, not broken (R-018): a denied open says nothing about the document, so LoadCore neither
                // copies it aside as corrupt nor restores the backup over it.
                failure = ex.GetType().Name;
                settings = null;
                return ReadResult.Locked;
            }
            catch (IOException ex) when (!(ex is FileNotFoundException) &&
                                         !(ex is DirectoryNotFoundException) &&
                                         !(ex is PathTooLongException))
            {
                // A sharing violation: a sync client, a scanner or an editor holding the file open without read
                // sharing. The same verdict as an access denial, for the same reason. The three excluded kinds are
                // about the path, not a holder, and keep their old classification.
                failure = ex.GetType().Name;
                settings = null;
                return ReadResult.Locked;
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name;
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
                    return DecodeSettingsText(memory.ToArray());
                }
            }
        }

        /// <summary>
        /// Decode a settings document that an editor may have re-saved. Strict UTF-8 is still the contract
        /// (AtomicFile writes it with no BOM), but Encoding.GetString never strips a preamble and STJ's string
        /// overload rejects U+FEFF as "an invalid start of a value", so a file every other JSON reader accepts
        /// was classed corrupt and recovered over (F098; measured 2026-09-29 on .NET 10: a UTF-8 BOM throws
        /// JsonException, UTF-16 bytes throw DecoderFallbackException). A UTF-8 BOM is skipped; a UTF-16 BOM
        /// (what Windows PowerShell 5.1's Out-File writes by default) is honoured and the text transcoded;
        /// anything else is the strict decode as before, so invalid bytes still fail loudly rather than becoming
        /// U+FFFD. The next save rewrites the file as UTF-8 without a BOM either way.
        /// </summary>
        internal static string DecodeSettingsText(byte[] bytes)
        {
            if (bytes == null) return "";
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return StrictUtf8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);
            return StrictUtf8.GetString(bytes);
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

        internal static string BuildCredentialScope(
            string provider,
            string endpoint)
        {
            // No cloud provider, no scope: the local slot carries no credential. Until 2026-09-30 a blank
            // provider was first mapped to "ollama" and then tested for it; the legacy local ids stopped being
            // valid selectors with schema v2 (IsKnownProvider clamps them), so the test is the blank one (F108).
            if (string.IsNullOrWhiteSpace(provider)) return "";
            string normalizedProvider = provider.Trim().ToLowerInvariant();

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

        /// <summary>The endpoint a credential is scoped to: the CLOUD one. Provider is the cloud selector under
        /// schema v2 and can no longer read "ollama", so the branch that answered with the local Endpoint was
        /// unreachable (F108).</summary>
        private string SelectedCredentialEndpoint()
        {
            return OpenAiBaseUrl;
        }

        /// <summary>
        /// A shallow snapshot for building the ACTIVE backend's <see cref="AiBrain"/>: identical to this
        /// instance except that, when a cloud <see cref="Provider"/> is selected, the cloud models are
        /// promoted into <see cref="TextModel"/>/<see cref="VisionModel"/> so the brain's model-selection
        /// path uses the active slot's models. Local-only returns an equivalent copy. Read-only — callers
        /// must not persist it (it shares the credential/collection references with this instance), and since
        /// 2026-09-30 they cannot: the copy is marked detached, so SaveWithin refuses it the way it refuses
        /// CloneForBrain's; the audition brain's snapshot of the live instance was saveable before (RA-085).
        /// </summary>
        internal AiSettings ActiveSlotSnapshot()
        {
            AiSettings clone = (AiSettings)MemberwiseClone();
            clone._detachedCopy = true;
            DesktopAICompanion.CodingAgent.CodingAgentKind cli = DesktopAICompanion.CodingAgent.CodingAgents.FromId(CliBackend);
            if (cli != DesktopAICompanion.CodingAgent.CodingAgentKind.None)
            {
                // A CLI slot names no model of the user's: Claude Code runs on its default and Codex on its own pick
                // (the runner). The brain's model policy still wants an id to resolve and to log, so both paths carry
                // the CLI's name, which the backend ignores (feature/cli-backend).
                clone.TextModel = CliModelName(cli);
                clone.VisionModel = CliModelName(cli);
            }
            else if (!string.IsNullOrEmpty(Provider))
            {
                clone.TextModel = CloudTextModel ?? "";
                clone.VisionModel = CloudVisionModel ?? "";
            }
            return clone;
        }

        /// <summary>
        /// A private copy for the brain factory, taken on the UI thread and handed to the pool thread that builds
        /// the brain. <see cref="ActiveSlotSnapshot"/>'s MemberwiseClone shares <see cref="ApiKeysEnc"/> and
        /// <see cref="ExtensionData"/> with the live instance (the two Fortunes-era lists it also copied went with
        /// F093), and the factory runs only after the previous brain has retired, up to about two seconds later: a
        /// pane Save in that window rotates the key or lets Normalize replace those collections under the factory's
        /// read (F068, F100). The copy owns its collections, so nothing the pane does afterwards reaches it, and it
        /// can never be persisted: a stray Save through the copy would otherwise overwrite the live file with a
        /// stale one. The audition (PreviewDispositionAsync) still builds from the live instance, on the UI thread
        /// and before its first await, where there is nothing to race.
        /// </summary>
        /// <summary>The id a CLI slot's brain resolves and logs in place of a model (its snapshot's TextModel and
        /// VisionModel): "claude-code-cli" or "codex-cli". No model of that name is ever sent anywhere.</summary>
        internal static string CliModelName(DesktopAICompanion.CodingAgent.CodingAgentKind cli)
        {
            return DesktopAICompanion.CodingAgent.CodingAgents.IdOf(cli) == "claude" ? "claude-code-cli" : "codex-cli";
        }

        internal AiSettings CloneForBrain()
        {
            AiSettings clone = (AiSettings)MemberwiseClone();
            clone.ApiKeysEnc = ApiKeysEnc == null ? null : new Dictionary<string, string>(ApiKeysEnc, StringComparer.Ordinal);
            clone.ExtensionData = ExtensionData == null
                ? null
                : new Dictionary<string, JsonElement>(ExtensionData, StringComparer.Ordinal);
            clone._detachedCopy = true;
            return clone;
        }

        /// <summary>
        /// Select a cloud provider and return the endpoint its UI should display. Preset providers may replace
        /// the shared cloud endpoint; Custom always restores its remembered endpoint. An id no preset knows
        /// changes NOTHING and answers with the current cloud endpoint. Until 2026-09-30 AiProviders answered an
        /// unknown id with its FIRST preset, the local Ollama one, so this wrote Provider = "ollama" and, with
        /// prefill, the preset URL over the LOCAL Endpoint; no caller passed such an id (the pane maps labels
        /// through CloudProviderIdForLabel), which is the only reason the trap never fired (F108). The probe pins
        /// the untouched case.
        /// </summary>
        internal string SelectProviderEndpoint(
            string provider,
            bool prefillPreset)
        {
            AiProviders.Preset preset;
            if (!AiProviders.TryGet(provider, out preset)) return OpenAiBaseUrl;
            RememberSelectedCustomEndpoint();
            Provider = preset.Id;

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

        /// <summary>The CLOUD endpoint only: Provider is the cloud selector under schema v2, so the branch that
        /// wrote the local Endpoint for an "ollama" provider was unreachable (F108).</summary>
        internal void UpdateSelectedProviderEndpoint(string endpoint)
        {
            endpoint = (endpoint ?? "").Trim();
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
            // No re-comparison of `normalized` against ApiKeysEnc here: under !changed every entry was copied
            // verbatim from ApiKeysEnc's own keys, so no input could make such a loop flip `changed` (F097).
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

        // The three residency tokens (see ModelResidency's doc comment); anything else normalizes to unload (RA-083).
        private static bool IsKnownResidency(string residency)
        {
            switch (residency)
            {
                case ResidencyUnload:
                case ResidencyKeep:
                case ResidencyServer:
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
            FutureSchema,
            /// <summary>The file is there and another process holds it: neither corrupt nor readable (R-018).</summary>
            Locked
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
        // mis-flagged. A model with no marker is offered in no vision dropdown; on a backend that reports
        // nothing about capabilities, a CONFIGURED one is used unverified rather than refused (see
        // ChooseModel, F102). Maintained by hand, so it goes stale: refreshed 2026-09-29 with the families
        // that had shipped since it was written (gpt-5, gemini-3, grok-4, mistral-small/medium-3, glm-4.5v).
        private static readonly string[] VisionModelMarkers =
        {
            "llava", "bakllava", "moondream", "vision", "-vl", "vl-", "vl:", "pixtral",
            "minicpm-v", "minicpm-o", "gemma3", "gemma-3", "gemma4", "gemma-4", "mllama",
            "llama4", "llama-4",
            "internvl", "cogvlm", "gpt-4o", "gpt-4-turbo", "gpt-4.1", "chatgpt-4o",
            "claude-3", "claude-4", "claude-opus", "claude-sonnet", "claude-haiku",
            "gemini-1.5", "gemini-2", "gemini-pro-vision", "glm-4v", "deepseek-vl",
            "phi-3-vision", "phi3.5-vision", "phi-4-multimodal", "smolvlm", "aya-vision",
            "gpt-5", "gemini-3", "grok-4", "mistral-small-3", "mistral-medium-3", "glm-4.5v", "glm-4.6v",
        };

        /// <summary>
        /// Whether to offer a model as vision-capable: the UNION of what the backend reported and the
        /// name heuristic, never one overriding the other.
        ///
        /// A fallback (report first, heuristic only when the report is absent) looks more principled
        /// and is wrong, because a report can be present AND incomplete. Ollama's /api/tags did
        /// exactly that for the Gemma family: measured 2026-09-10, gemma3:4b and gemma4:26b both omitted
        /// "vision" there while /api/show listed it for the same models, and mistral-small3.2:24b
        /// reported it correctly in both. Trusting the incomplete report hid the recommended vision
        /// model from its own dropdown. Re-measured 2026-09-30 on Ollama 0.34.4: /api/tags and /api/show
        /// agree (gemma3:4b, gemma4:12b and gemma4:26b all report vision), so the under-report belongs to
        /// older servers. The union stays for the DROPDOWN, which this decides, because an older server
        /// may still be in use and a hidden model is the worse failure; the ASK trusts a reported false
        /// (<see cref="ChooseModel"/>, F102 and R-020), and its substitute is never a reported-blind model.
        ///
        /// The asymmetry decides the direction: a false positive is visible and recoverable, a false
        /// negative hides a working model with no way to discover it.
        /// </summary>
        /// <param name="reportedVision">What the backend said: true, false, or null for "did not say".</param>
        public static bool IsVisionCapable(string model, bool? reportedVision)
        {
            return (reportedVision ?? false) || LooksVisionCapable(model);
        }

        /// <summary>
        /// Does a configured model id name the same model as a backend listing? Ollama's list carries the
        /// explicit tag (<c>gemma3:4b</c>, <c>llama3.1:latest</c>) while a setting or a default may omit
        /// it, and treating <c>gemma3</c> as absent when the backend offers <c>gemma3:latest</c> would
        /// report a missing model that is installed.
        /// </summary>
        internal static bool ModelIdMatches(string configured, string listed)
        {
            if (string.IsNullOrWhiteSpace(configured) || string.IsNullOrWhiteSpace(listed)) return false;
            string a = configured.Trim();
            string b = listed.Trim();
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            const string latest = ":latest";
            if (a.IndexOf(':') < 0 &&
                string.Equals(a + latest, b, StringComparison.OrdinalIgnoreCase)) return true;
            if (b.IndexOf(':') < 0 &&
                string.Equals(b + latest, a, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Decide which model an ask should actually use, given what the backend reports it has.
        ///
        /// BUG-002: a saved model id was never re-validated against the backend, so a setting that was
        /// valid when chosen kept pointing at a model that had since been removed -- and because the ask
        /// path returns null on any failure, the result was indistinguishable from "nothing to say". The
        /// shipped default (<c>gemma3:4b</c>) is exactly such an id on a machine that never had it.
        ///
        /// Deliberately does NOT nag when the list is empty or null: that means the backend is offline or
        /// does not support listing, which is not evidence the model is missing. Only a list we actually
        /// have can prove absence.
        /// </summary>
        /// <param name="configured">The id from settings (already normalized).</param>
        /// <param name="available">What the backend reports, or null/empty when unknown.</param>
        /// <param name="needVision">True when this ask needs image input.</param>
        /// <param name="allowSubstitution">False for a cloud primary: a model the user did not choose is never sent
        /// where it would be billed; the turn ends on an advisory naming the host instead (R-022).</param>
        /// <param name="backendDescription">The host(s) named in that advisory.</param>
        internal static ModelChoice ChooseModel(
            string configured,
            System.Collections.Generic.IReadOnlyList<ModelListing> available,
            bool needVision,
            bool allowSubstitution = true,
            string backendDescription = null)
        {
            // No id at all is not "unknown inventory": nothing can be sent, and the fix is in the pane. An
            // empty id used to reach here only because the constructor had filled it with the local default
            // (F101); now that a cloud slot keeps its blank, this is the branch that says so, once.
            if (string.IsNullOrWhiteSpace(configured))
                return new ModelChoice(
                    null,
                    needVision
                        ? "No vision model is set for this provider. Pick one in AI Brain settings."
                        : "No text model is set for this provider. Pick one in AI Brain settings.",
                    "none-configured");

            if (available == null || available.Count == 0)
                return new ModelChoice(configured, null, "model-list-unknown");

            bool listedButCannotSee = false;
            foreach (ModelListing listing in available)
            {
                if (listing == null) continue;
                if (!ModelIdMatches(configured, listing.Id)) continue;
                // Present, but it still has to be able to do the job asked of it. A text-only model selected
                // for the vision path fails the same silent way a missing one does, so a backend that REPORTS
                // the model cannot see (Ollama's capabilities array) is a hard gate. A backend that reports
                // nothing (every OpenAI-compatible /v1 list carries Vision = null) is not: the name-marker
                // list is a hint maintained by hand, and on 2026-09-29 it knew no gpt-5, gemini-3 or grok-4, so
                // treating a miss as "cannot see" rerouted a working configured model to another vendor's,
                // with an advisory claiming it "isn't available" while it sat in the list (F102). Unverified
                // is used as configured and named in the log; a genuinely blind model then fails at request
                // time with a logged 4xx, which is the direction the dropdown's union rule already chose.
                if (needVision && listing.Vision == false)
                {
                    listedButCannotSee = true;
                    break;
                }
                if (needVision && listing.Vision == null && !LooksVisionCapable(listing.Id))
                    return new ModelChoice(listing.Id, null, "configured-unverified-vision");
                return new ModelChoice(listing.Id, null, "configured");
            }

            if (!allowSubstitution)
            {
                // A cloud primary: every request is billed, so a model the user did not choose is never sent. The
                // turn ends on an advisory naming the host, spoken once (AdvisoryOnce), until the pane is fixed. On
                // a local backend the first usable listing is free and BUG-002's substitution stands (R-022).
                string host = string.IsNullOrWhiteSpace(backendDescription) ? "this provider" : backendDescription;
                return new ModelChoice(
                    null,
                    listedButCannotSee
                        ? Describe(configured) + " can't see images, and I don't switch models on " + host +
                          ". Pick a vision model in AI Brain settings."
                        : Describe(configured) + " isn't offered by " + host + ". Pick a model in AI Brain settings.",
                    "none-offered");
            }

            foreach (ModelListing listing in available)
            {
                if (listing == null || string.IsNullOrWhiteSpace(listing.Id)) continue;
                // The gate's own rule, applied to the substitute: a listing the backend REPORTS as blind is never
                // picked, marker or not, so the advisory can never name the model it just rejected ("gemma3:4b
                // can't see images, so I'm using gemma3:4b instead" was reachable through the union here, R-020).
                // A listing with no report falls back to the name marker, as the dropdown does.
                if (needVision && (listing.Vision == false || !IsVisionCapable(listing.Id, listing.Vision))) continue;
                return new ModelChoice(
                    listing.Id,
                    Describe(configured) + (listedButCannotSee ? " can't see images" : " isn't available") +
                        ", so I'm using " + listing.Id + " instead.",
                    "substituted");
            }

            return new ModelChoice(
                null,
                needVision
                    ? Describe(configured) + " isn't available and I can't find another model that can " +
                      "see images. Pick one in AI Brain settings."
                    : Describe(configured) + " isn't available. Pick a model in AI Brain settings.",
                "none-usable");
        }

        private static string Describe(string model)
        {
            return string.IsNullOrWhiteSpace(model) ? "The configured model" : model;
        }

        /// <summary>Best-effort, name-based guess of whether a model accepts image input. Empty -> true (no
        /// advisory). A hint maintained by hand: on a backend that reports nothing it decides the vision
        /// DROPDOWNS (a miss hides the model from them) but no longer the ASK, where a configured model it does
        /// not know is used unverified (see <see cref="ChooseModel"/>, F102).</summary>
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
        //
        // MEASURED 2026-09-10, and the tag turned out to predict nothing. A/B across five local text
        // models against the Jules Winnfield disposition, whose instruction explicitly requires curse
        // words spelled out in full: the safety-tuned control (gemma3:4b) complied, and the ONLY model
        // that failed was `llama2-uncensored` -- the one this marker list flags most confidently. It
        // dropped the persona entirely and replied "The screen is displaying a code editor.", twice, in
        // independent runs. Two of the three `dolphin` matches did comply, so the list is not useless,
        // but it is a naming convention and not a capability test. Do not let the UI imply otherwise,
        // and do not use it to steer a persona to a model. See the model table in Readme.md.
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
