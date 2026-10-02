using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Ai;
using DesktopAICompanion.Modules;
using DesktopAICompanion.ModuleKit;   // EmbeddedResources

namespace DesktopAICompanion.AiBrainModule
{
    /// <summary>
    /// The AI-brain module: the optional, off-by-default screen-commentary LLM. It owns the "ask about my screen"
    /// flow through host services: a drop responder at priority 10 that outranks Fortunes on the host's global
    /// drop schedule (the module's own idle loop went in 1.2.3), a poke responder (the text path, the one exception
    /// to vision-for-every-remark), a global hotkey, two tray rows and the "AI Brain" options pane, plus the
    /// emotion->animation reaction. The brain lifecycle (generation/supersede, prepare/retire) is
    /// <see cref="AiSessionManager"/>; settings and the DPAPI-scoped keys are the module's own store
    /// (engine/AiSettings). OFF by default (its own AiBrainEnabled), so a fresh install does nothing until enabled
    /// from the tray or the pane. (This summary described an idle-commentary loop and "no tray/Options UI yet" long
    /// after both had changed, RA-054.)
    /// </summary>
    public sealed class AiBrainModule : IModule
    {
        private IHost _host;
        private SynchronizationContext _ui;                 // captured on the UI thread in Init
        private readonly AiSessionManager _session = new AiSessionManager();
        /// <summary>The session, for the module self-test only: it asserts what a pane action does to the LIVE
        /// brain (R-015).</summary>
        internal AiSessionManager SessionForDiagnostics { get { return _session; } }
        private AiSettings _settings;
        private CancellationTokenSource _lifetime = new CancellationTokenSource();
        private IDisposable _dropResponder;
        private IDisposable _pokeResponder;
        private IDisposable _hotkey;
        private Action<bool> _fullscreenChanged;
        private ICompanion _lastPet;                              // most-recently-seen pet (screen-context anchor)
        // Still load-bearing without the old idle timer: it keeps a hotkey ask and a drop that land within
        // 30s of each other from becoming two answers in a row.
        private DateTime _lastInteractionUtc = DateTime.MinValue;

        // Model-picker dropdowns: explicit-refresh-only caches (no TTL - populated by the "Refresh ...
        // models" actions, empty until the user clicks one) and the retained SettingField objects the pane's
        // Schema holds, so a refresh can mutate .Options IN PLACE on those same objects (the only way a
        // PaneAction.ReloadPaneAfter rebuild picks up a fresh list - see RefreshModelFieldOptions).
        private readonly List<ModelListing> _localModels = new List<ModelListing>();
        private readonly List<ModelListing> _cloudModels = new List<ModelListing>();
        // Maps a displayed dropdown LABEL back to its underlying model id for the current pane session.
        // A label can carry a size prefix and/or an uncensored suffix (see FormatModelLabel), so recovering
        // the id needs this lookup rather than a fixed string pattern; every label FormatModelLabel produces
        // (both for Load's current-value and for each listed model) registers itself here first, and Load
        // always runs before Save can be called, so a lookup here always succeeds for anything the user
        // could have actually picked from a dropdown.
        private readonly Dictionary<string, string> _modelIdByLabel = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Guards <see cref="_localModels"/>, <see cref="_cloudModels"/> and <see cref="_modelIdByLabel"/>.
        ///
        /// The two "Refresh ... models" pane actions can be in flight together -- the host disables only
        /// the button that was clicked -- and both resume on a POOL thread after their
        /// ConfigureAwait(false), then Clear/AddRange one list and call RefreshModelFieldOptions, which
        /// reads BOTH lists and writes the label dictionary through FormatModelLabel. Concurrently that is
        /// a List being rebuilt while another thread enumerates it and two threads writing one
        /// Dictionary: torn dropdowns at best, a corrupted dictionary or InvalidOperationException at
        /// worst. LoadPaneValues reads the same three from the UI thread, so the race does not even need
        /// two refreshes -- one refresh plus a pane open is enough.
        ///
        /// Monitor is reentrant, so the coarse lock in each refresh action and the fine ones in
        /// FormatModelLabel / ResolveModelId nest safely on the same thread. The coarse one is what makes
        /// "replace the list, then rebuild the options from it" atomic rather than merely thread-safe in
        /// pieces.
        /// </summary>
        private readonly object _modelsLock = new object();

        /// <summary>
        /// The VRAM residency sentence, refreshed OFF the UI thread.
        ///
        /// MEASURED 2026-09-27 with the same mechanism OllamaClient uses (HttpClient, 2s deadline,
        /// literal 127.0.0.1 so no DNS): server running 5-56 ms, server REFUSED 2005-2008 ms, host
        /// unreachable 2010 ms. A refused localhost connection does not fail fast -- it burns the whole
        /// deadline and comes back as TaskCanceledException. So the old synchronous call did not cost
        /// 2 seconds in a rare hung-server case; it cost 2 seconds for every user who does not have
        /// Ollama running, on every options-pane open, and three pane actions rebuild the pane.
        ///
        /// Hence: never block the pane on it. Serve the last answer, refresh behind, and say "checking"
        /// the first time rather than freezing while we find out.
        /// </summary>
        private volatile string _vramResident;
        private long _vramStampTicks;
        private int _vramProbing;
        private const int VramFreshnessSeconds = 5;
        private SettingField _textModelField;
        private SettingField _visionModelField;
        private SettingField _cloudTextModelField;
        private SettingField _cloudVisionModelField;

        private static readonly string[] NoAnimation = new string[0];

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "aibrain",
            Name = "AI Brain",
            Version = "1.2.0",   // 1.2.0: stands down while Remembrance runs a local model, so a remark cannot
                                 //        evict the model of a transcription or a summary in progress (owner
                                 //        request, 2026-10-02). Remembrance publishes `remembrance.busy` on the
                                 //        host's shared context; AI Brain reads it and makes it the second
                                 //        reason of the one stand-down the fullscreen app already had. Lane
                                 //        feature/aibrain-standdown; its decisions are under that heading in
                                 //        docs/DESIGN-REGISTER.md.
                                 // 1.1.14: the 2026-09-29 audit campaign, lane fix/aibrain. Vision, when on,
                                 //         applies to every remark including the unprompted drop (owner
                                 //         decision, BUG-010): the code stood, the label and comments changed,
                                 //         and the module self-test pins the drop's routing. The hotkey and
                                 //         the tray row honour the fullscreen stand-down. LaunchProcess is
                                 //         declared. The settings store reads a BOM'd or UTF-16 file, keeps a
                                 //         rejected primary as ai-settings.corrupt-*.json, says in the log
                                 //         when it could not load, restores an empty endpoint or hotkey, and
                                 //         has no %TEMP% fallback root. A cloud slot needs a model before the
                                 //         brain builds; the cloud+local composite can be enumerated, so a bad
                                 //         cloud id is re-validated (BUG-002); a vision model the marker list
                                 //         does not know is used, not refused; an answered 401 is reachable
                                 //         and described as a rejected key, and the log files answered
                                 //         statuses as http-<n>; model listings read under their own 8 MiB
                                 //         cap; the failure line names the model sent and the host built
                                 //         against; a substitution is announced before any capture. The
                                 //         reachability probes are bounded by their own 10 s deadline and
                                 //         the composite runs both legs at once; the warm-up carries the
                                 //         residency's keep_alive; an Apply that leaves the backend as it
                                 //         was keeps the model resident; the timed-out dispose releases the
                                 //         model before disposing the backend; a reply that completed is
                                 //         never discarded to a deadline that fired during the read; the
                                 //         audition holds its model between samples and warms nothing; a
                                 //         fallover picks the local model by whether the request carries an
                                 //         image. The brain factory reads a private copy of the settings
                                 //         taken on the UI thread, and a factory that throws is logged; the
                                 //         inventory is taken when the backend is first seen up and again
                                 //         when it comes back, and the pane's Refresh reaches the live
                                 //         brain; the OCR engine resolves once per brain and the PATH walk
                                 //         throws nothing; Windows OCR is fed the capture's pixels, not a
                                 //         PNG round trip; the audition guard is interlocked. Lane
                                 //         fix/deadcode (same version, zip not yet republished): the seven
                                 //         Fortunes-era settings fields, the local provider presets and the
                                 //         "ollama" branches behind them, the legacy-root read that could
                                 //         never run, the second downscale and the OCR display name nothing
                                 //         showed are gone; an unknown provider id changes nothing; the
                                 //         audition pane shows each sample's latency; a live-vision audition
                                 //         carries one instruction; the probe names a throwing group and
                                 //         survives a renamed reflected field. Every item is
                                 //         dispositioned in BACKLOG.md; decisions under `#### fix/aibrain`
                                 //         and `#### fix/deadcode` in docs/DESIGN-REGISTER.md.
                                 //         Round 2 (2026-09-30, the early regression review): UseVision joined
                                 //         the backend fingerprint; model listings run under their own 30 s
                                 //         bound and an empty re-list keeps the previous inventory; "Test
                                 //         OCR" and "Choose OCR engine" reset the LIVE brain's Tesseract
                                 //         cache; the substitution loop excludes a reported-blind model as
                                 //         the gate does; a cloud primary never substitutes a missing model
                                 //         (R-011, R-014, R-015, R-020, R-022).
                                 //         Phase 8 burn-down (2026-09-30, lane burn/aibrain, same version): the
                                 //         audition, Test OCR and Test connection read the pane's PENDING values
                                 //         through PaneAction.InvokeWithPendingAsync, a member host 1.2.5 added,
                                 //         so MinHostVersion is RAISED from 1.1.0 to 1.2.5 in this version (the
                                 //         publish round must carry it into the catalog entry); a typed
                                 //         key that cannot be stored refuses the save and says why; the pane shows
                                 //         whether the brain started and the tray row hides while it has not; a save refused for its key
                                 //         leaves the live settings untouched (N-burn-aibrain-02, 2026-10-01);
                                 //         every explicit-ask refusal is logged; a model refresh names an answered
                                 //         401; the audition applies the cloud no-substitution rule, evicts within
                                 //         2 s and cuts samples at code points; the release names the substitute a
                                 //         turn sent and the local model the composite warmed by PATH; the composite
                                 //         cannot enumerate while its primary is down and an unknown inventory is
                                 //         re-listed; the session owns the backend fingerprint; a chosen OCR path
                                 //         survives a walk in flight; a held settings file is not corruption, the
                                 //         load warning names the backup's fate and a failed default write, a save
                                 //         over a missing file keeps unknown keys, the residency token is clamped
                                 //         and the active-slot snapshot cannot be saved; the probe has no SKIP line
                                 //         and guards every check (RA-054 to RA-087, R-012, R-013, R-016 to R-019,
                                 //         R-021, R-023).
                                 // 1.1.13: the emotion reaction reached 18/54, 35/54, 8/54, 8/54 and
                                 //         8/54 companions. "thinking" fires on EVERY ask, so on 46 of
                                 //         54 it silently did nothing -- the eSheep-era names it used
                                 //         are absent from the 32 converted shimeji. Original names
                                 //         kept FIRST, so nothing that worked changes; the tail is a
                                 //         fallback. Now 42/42/39/40/43. Also dropped a write-only
                                 //         _generation field.  // 1.1.12: exposes SelfTest on the module class, so --module-selftest runs
                                 //         AiEngineProbe through the convention the gate and CI use.
                                 // 1.1.11: the options pane no longer freezes for 2s when Ollama is not
                                 //         running. The VRAM line was a synchronous network call on the UI
                                 //         thread; a REFUSED localhost connection burns the full 2s deadline
                                 //         rather than failing fast, so that was the common case, not the
                                 //         worst one. It is served from cache and refreshed behind now.
                                 // 1.1.10: settings saves from the UI are bounded at 1.5s instead of the 10s
                                 //         cross-session budget, so a hung second instance can no longer freeze
                                 //         the settings window; and the tray AI toggle now says when a save
                                 //         failed instead of silently reverting on the next launch.
                                 // 1.1.9: the local and cloud "Refresh models" actions raced. Both resume on a
                                 //        pool thread and rebuilt the shared model lists and label map with no
                                 //        synchronisation, and a pane open read the same three from the UI
                                 //        thread. All three are guarded now.   // 1.1.8: the fullscreen release now unloads the model it actually loaded. The local
                                 //        leg of the fallback ignored its argument and always passed the TEXT model,
                                 //        so after a cloud-primary fallback had loaded local llava:13b (~8 GB) the
                                 //        vision model kept its VRAM for the whole game.
                                 // 1.1.7: dead code out, no behaviour change. DisposeWithin was byte-for-byte
                                 //        DisposeForDiagnostics with no callers, and CredentialIdentity had none
                                 //        either while decrypting the API key on every call.
                                 // 1.1.6: "Test connection" reports whether it got an ANSWER; a saved vision
                                 //        model the filter rejects is still offered; and the drop and poke
                                 //        responders no longer claim a turn they declined.
                                 // 1.1.4: the persona audition can run against the REAL screen ("5 about
                                 //        my screen"), which is the more honest test. Needed a real
                                 //        anti-repetition mechanism first: the prompt's "do not repeat
                                 //        anything you have said recently" was INERT, because each
                                 //        sample is an independent single-turn request and nothing told
                                 //        the model what it had already said. Five asks about one
                                 //        unchanging screen came back near-identical; now each prompt
                                 //        carries the previous remarks, bounded to the last four.
                                 // 1.1.3: a failed AI turn now says WHICH failure it was (backend
                                 //        availability transitions, request latency/attempts/parse
                                 //        outcome, and the OCR engine plus every silent `return ""` in
                                 //        the tesseract path), and the Persona card gained "Show me 5
                                 //        examples" -- five remarks across five canned scenes, so a
                                 //        disposition is judged by its voice. Reports the model that
                                 //        answered, because ChooseModel substitutes silently.
                                 // 1.1.2: republished so the bundled ModuleKit.dll no longer carries the
                                 //        maintainer's absolute build path (Contracts + ModuleKit moved to
                                 //        DebugType=embedded). NO functional change here; the bump exists
                                 //        because the catalog offers an update by VERSION, so without it the
                                 //        cleaned payload would only ever reach new installs.
                                 // 1.1.1: the shipped TextModel default was llama3.1:8b, a model that was
                                 //        never in the measured comparison and that the Readme tables do
                                 //        not mention, so a fresh install pointed its text model at
                                 //        something the user very likely did not have. Now gemma3:4b,
                                 //        matching VisionModel so one download serves both jobs and the
                                 //        backend can keep a single model resident. The value was
                                 //        duplicated across five sites; all are now in step.
                                 // 1.1.0: NEW: screen reactions capture the FOREGROUND WINDOW rather than the
                                 //        whole monitor, and the prompt names what else is open. Needs host
                                 //        1.1.0 for ScreenContext.ForegroundWindowBounds / .Windows.
                                 // 1.0.0: rebased with the host for the Desktop AI Companion rename. Not a
                                 //        rollback -- the previous line below is the higher number, and
                                 //        every module restarts its numbering here alongside the app.
                                 // 1.4.0: NEW: "Stand down while a fullscreen app is running" -- releases the
                                 //        model and lets free fortunes speak instead, so a local model cannot
                                 //        claim VRAM beside a game that already owns it. Releases on the
                                 //        TRANSITION (host FullscreenChanged), because a model loaded before
                                 //        the game started is not helped by merely declining to load. Needs
                                 //        host 1.9.9 for the fullscreen predicate + event.
                                 // 1.3.0: NEW: "Model residency" -- one choice for how long a local model may
                                 //        hold VRAM, defaulting to unloading after each remark. Replaces
                                 //        "Preload model on launch", which could contradict a short eject
                                 //        window (it pinned keep_alive to 10m) and needed a paragraph of
                                 //        explanation; one setting cannot disagree with itself. The pane now
                                 //        reads GET /api/ps and reports what is ACTUALLY resident -- model,
                                 //        GB, seconds to eviction -- instead of printing a documented default
                                 //        that OLLAMA_KEEP_ALIVE can override on the user's own machine.
                                 // 1.2.3: unprompted commentary now rides the HOST's global "Randomly drop a
                                 //        fortune / insight" schedule. The module's own idle timer and its
                                 //        three settings (Idle commentary / min / max) are gone: two
                                 //        schedules were driving the same model into the same bubble with no
                                 //        shared cooldown. The Ask hotkey is unchanged.
                                 // 1.2.2: payload refresh only, no behaviour change -- the bundled ModuleKit
                                 //        was 4 commits stale. See the note on Fortunes 1.2.4.
                                 // 1.2.1: the tray icon is the blue brain glyph, not the retired red-X
                                 //        disable-ai.png. 0f3def7 changed the source but never republished the
                                 //        payload, so every download still carried the old icon -- and because
                                 //        the version did not move, no update was ever offered. This bump is
                                 //        what actually ships that change.
                                 // 1.2.0: the question, the thinking cue and the answer all belong to ONE pet
                                 //        (and an answer whose pet has gone is dropped, not handed to another)
                                 // 1.1.2: helpers come from DesktopAICompanion.ModuleKit instead of local copies
                                 // 1.1.1: OCR output is decoded as UTF-8 (was the ANSI codepage -> "asÂ®")
                                 // 1.1.0: reads the screen with Windows' built-in OCR when Tesseract is absent
            // 1.9.9 is the host that added IHost.IsFullscreenActive + FullscreenChanged, which the
            // stand-down-for-a-game guard needs. Declaring it means an older host refuses this module with a
            // legible reason instead of loading it and failing at a missing member. (1.5.0 added the pet-aware
            // responders and IsCompanionAlive, which this also uses.)
            // Raised to 1.1.0 because the screen-reaction path now reads
            // ScreenContext.ForegroundWindowBounds and .Windows, both introduced by that host. This is the
            // first module to raise its floor since the 1.0.0 rebase flattened every one of them, so it is
            // also the first to exercise the sequencing rule: do NOT publish this to the catalog until host
            // 1.1.0 has shipped, or the catalog offers users a module their host correctly refuses.
            // Raised to 1.2.5 on 2026-09-30 (RA-055): the three pane actions set PaneAction.InvokeWithPendingAsync,
            // which host 1.2.5 introduced, and an older host would fail at the missing member while running Init.
            // The shipping host is 1.2.6, so the sequencing rule above is already satisfied.
            MinHostVersion = "1.2.5",
            // LaunchProcess: this module starts `ollama serve` (engine\OllamaClient.TryStartServer) and runs
            // tesseract.exe as a child for OCR (engine\AiBrain.RunOcrAsync), and no flag had ever said so on
            // the consent screen (F226: the flag shipped in host 1.2.5 naming this module as a holder, and no
            // module declared it). A disclosure, not a gate, like every flag in the enum. Declaring an existing
            // flag needs no MinHostVersion raise.
            Permissions = ModulePermissions.Speech | ModulePermissions.Animation |
                          ModulePermissions.ScreenContext | ModulePermissions.Network |
                          ModulePermissions.Hotkey | ModulePermissions.Storage |
                          ModulePermissions.LaunchProcess,
        };

        public void Init(IHost host)
        {
            _host = host;
            _ui = SynchronizationContext.Current;   // the WinForms UI-thread context (host loads modules there)

            // Give the engine a way into the diagnostic log. Until this existed the whole of
            // modules/AiBrain had ONE IHost.Log call, in a module that fails by returning null on
            // purpose -- so every failure looked like "nothing to say" (BUG-002). Set before anything
            // else here so a fault during the rest of Init is itself recorded.
            AiBrain.LogSink = delegate(string line)
            {
                IHost h = _host;
                if (h == null) return;
                try { h.Log(Info.Id, line); } catch { }
            };

            try
            {
                IModuleStorage storage = host.GetStorage("aibrain");
                if (storage != null && !string.IsNullOrEmpty(storage.DataDirectory))
                {
                    AiPaths.SetRoot(storage.DataDirectory);
                    MigrateFromBaseIfNeeded(storage.DataDirectory);   // one-time, non-destructive
                }
                _settings = AiSettings.Load();
                // Said out loud. Load never throws, and until 2026-09-29 it also could not SAY anything: a
                // corrupt primary recovered from the backup, a lock that timed out and a host that gave no
                // storage all produced a settings object and nothing in the file SUPPORT.md asks for (F096,
                // F098, N-gates-02). One line, once, at the start of the module's life.
                if (!string.IsNullOrEmpty(_settings.LoadWarning))
                    try { host.Log(Info.Id, "settings: " + _settings.LoadWarning); } catch { }
            }
            catch { _settings = new AiSettings(); }

            // Track the current pet for the screen-context anchor; harmless while the brain is off.
            host.CompanionSpawned += OnPetSeen;
            host.CompanionLanded += OnPetSeen;
            host.CompanionPoked += OnPetPoked;
            // Outrank Fortunes (priority 0) on the shared drop: when the brain is on, the drop is an AI
            // insight and this responder handles it; when off, it declines and Fortunes speaks instead.
            _dropResponder = host.RegisterCompanionDropResponder(10, OnDrop);
            // Same for the first poke of a session, except the user's "Trigger Speech" preference can
            // override this ordering entirely (or randomize it).
            _pokeResponder = host.RegisterCompanionPokeResponder(Info.Id, 10, OnPokeReaction);

            // A game STARTING is the only moment we can hand VRAM back before it is needed rather than after,
            // so this is an event rather than something checked at our own next tick (which could be 15
            // minutes away, long after the game has failed to get the memory).
            _fullscreenChanged = OnFullscreenChanged;
            host.FullscreenChanged += _fullscreenChanged;

            // Contribute the AI tray items (S5a): the host merges these into the tray, re-evaluating
            // DynamicText/Visible on each open. This is the module's own enable/ask entry point now that the
            // base's AI tray items are gone (closes the S4 accept-the-gap).
            host.AddTrayItems(new[]
            {
                new TrayItem
                {
                    Label = "Enable AI", Group = 50, Order = 0,
                    DynamicText = delegate { return (_settings != null && _settings.AiBrainEnabled) ? "Disable AI" : "Enable AI"; },
                    Click = ToggleEnabled,
                    IconPng = LoadIconResource("ai-brain.png"),
                },
                new TrayItem
                {
                    Label = "Ask about my screen", Group = 50, Order = 1,
                    // Offered only while the brain is actually STARTED. On the stored switch alone the row stayed
                    // visible, and inert, after CanUse had refused the configuration (a cloud slot with no model,
                    // consent missing, a bad endpoint): the one signal was a log line (R-013). The toggle above keeps
                    // reading the switch, because the switch is what it flips; the pane's Status row says why.
                    Visible = delegate { return _session.Enabled; },
                    Click = delegate { Ask(null, true); },
                    IconPng = LoadIconResource("monitor.png"),
                },
            });

            // Model-picker dropdowns: build the retained SettingField objects first (so a later refresh can
            // mutate .Options on these SAME objects) and seed their Options from whatever's already saved
            // (the caches are empty pre-refresh, so this is just the safety-net current-value entry - see
            // RefreshModelFieldOptions/BuildModelOptions).
            _textModelField = new SettingField { Id = "textModel", Label = "Local text model", Kind = SettingKind.Enum, Group = "Local provider" };
            _visionModelField = new SettingField { Id = "visionModel", Label = "Local vision model", Kind = SettingKind.Enum, Group = "Local provider" };
            _cloudTextModelField = new SettingField { Id = "cloudTextModel", Label = "Cloud text model", Kind = SettingKind.Enum, Group = "Cloud provider" };
            _cloudVisionModelField = new SettingField { Id = "cloudVisionModel", Label = "Cloud vision model", Kind = SettingKind.Enum, Group = "Cloud provider" };
            RefreshModelFieldOptions();

            // Contribute the AI config as a schema-driven OptionsPane (S5b): the host renders it in the WPF
            // settings window and round-trips values through this Load/Save, which persist to the module's
            // own AiSettings store. Exercises every field kind (bool/int/text/enum/secret).
            host.AddOptionsPane(new OptionsPane
            {
                Title = "AI Brain",
                Schema = new[]
                {
                    new SettingField { Id = "enabled", Label = "Enable AI brain", Kind = SettingKind.Bool, Group = "AI brain" },
                    // Whether the brain STARTED, and why not when it did not: CanUse's refusal used to live in the
                    // diagnostic log alone while Save reported success (R-013). See BrainStatusLine.
                    new SettingField { Id = "brainStatus", Label = "Status", Kind = SettingKind.Info, Group = "AI brain" },
                    new SettingField { Id = "companionName", Label = "Companion name", Kind = SettingKind.Text, Group = "Persona" },
                    new SettingField { Id = "userName", Label = "Your name (optional)", Kind = SettingKind.Text, Group = "Persona" },
                    new SettingField { Id = "disposition", Label = "Disposition", Kind = SettingKind.Enum, Options = DispositionNames(), Group = "Persona" },
                    // Local provider (always available; defaults to Ollama but can instead speak the
                    // generic OpenAI-compatible /v1 protocol for llama.cpp/LM Studio/other local servers).
                    new SettingField { Id = "localBackendKind", Label = "Local backend", Kind = SettingKind.Enum, Options = LocalBackendKindLabels(), Group = "Local provider" },
                    new SettingField { Id = "endpoint", Label = "Local endpoint (base URL)", Kind = SettingKind.Text, Group = "Local provider" },
                    _textModelField,
                    _visionModelField,
                    // Vision, when on, applies to EVERY remark about the screen: the hotkey, the tray row and
                    // the unprompted drop alike, so the companion reacts to what is actually on screen rather
                    // than to OCR text. Owner decision 2026-09-29 (BUG-010): the label used to say "on explicit
                    // asks" while OnDrop had always allowed vision; the code stood and the words changed. The
                    // poke reaction is the one exception and stays on the text path (see OnPokeReaction).
                    new SettingField { Id = "useVision", Label = "Use vision (send a screenshot, not OCR text, with each remark)", Kind = SettingKind.Bool, Group = "Local provider" },
                    // Screen reading uses this OCR engine whenever vision is off or the chosen model cannot see.
                    // Empty = search the usual install locations, then PATH.
                    new SettingField { Id = "tesseractPath", Label = "OCR engine (blank = auto-detect)", Kind = SettingKind.Text, Group = "Screen reading" },
                    new SettingField { Id = "autoStart", Label = "Start Ollama automatically", Kind = SettingKind.Bool, Group = "Local server (Ollama only)" },
                    // ONE choice, not a "preload" switch plus an eject window that could contradict it.
                    // Defaults to unloading: the module holds VRAM only for a remark it has already made.
                    new SettingField
                    {
                        Id = "standDownFullscreen",
                        Label = "Stand down while a fullscreen app is running (releases VRAM, declines the Ask hotkey; fortunes speak instead)",
                        Kind = SettingKind.Bool,
                        Group = "Local server (Ollama only)",
                    },
                    new SettingField
                    {
                        Id = "residency",
                        Label = "Model residency (how long it may hold VRAM)",
                        Kind = SettingKind.Enum,
                        Options = ResidencyLabels(),
                        Group = "Local server (Ollama only)",
                    },
                    // Deliberately NOT a sentence claiming "the default is 5 minutes". It is 5 minutes in
                    // Ollama's docs, but OLLAMA_KEEP_ALIVE overrides it server-wide, so the claim would be
                    // wrong on exactly the machines whose owner had tuned it. This reads /api/ps and reports
                    // what is actually resident, which is the honest version of "say whatever the default is".
                    new SettingField { Id = "vramStatus", Label = "In VRAM right now", Kind = SettingKind.Info, Group = "Local server (Ollama only)" },
                    // Cloud provider (optional; primary when selected).
                    new SettingField { Id = "cloudProvider", Label = "Cloud provider", Kind = SettingKind.Enum, Options = CloudProviderLabels(), Group = "Cloud provider" },
                    new SettingField { Id = "cloudEndpoint", Label = "Cloud base URL", Kind = SettingKind.Text, Group = "Cloud provider" },
                    new SettingField { Id = "apiKey", Label = "API key (cloud providers)", Kind = SettingKind.Secret, Group = "Cloud provider" },
                    _cloudTextModelField,
                    _cloudVisionModelField,
                    new SettingField { Id = "cloudConsent", Label = "Allow cloud data sharing", Kind = SettingKind.Bool, Group = "Cloud provider" },
                    // Fallback: the runtime half is FallbackBackend, built by CreateBrain whenever a cloud provider is
                    // primary and this is on (F094; this line said "a later change" long after it shipped, RA-054).
                    new SettingField { Id = "useLocalFallback", Label = "Use local provider as fallback", Kind = SettingKind.Bool, Group = "Fallback" },
                    // Unprompted commentary has no controls here on purpose: it rides the host's global
                    // "Randomly drop a fortune / insight" schedule in Preferences via OnDrop. The hotkey is
                    // the only trigger this module still owns, because it is the only one that is its own.
                    new SettingField { Id = "hotkey", Label = "Ask hotkey", Kind = SettingKind.Text, Group = "Triggers" },
                },
                Load = LoadPaneValues,
                Save = SavePaneValues,
                Actions = new[]
                {
                    // Two actions rather than one action plus a mode switch, and deliberately: pressing one of two
                    // buttons is inherently exclusive and needs no Apply, and the "which screen" choice is not a
                    // setting worth persisting. (The pending-values reason this used to give went with RA-055: the
                    // actions below now DO see what is on screen.)
                    //
                    // Both delegates on the three actions that read settings. InvokeWithPendingAsync (host 1.2.5)
                    // is handed the pane as it stands and wins when set, so the audition, Test OCR and Test
                    // connection answer about what the user is looking at rather than about the last Apply; the
                    // saved-values delegate stays as the documented fallback shape (RA-055, adopting the member
                    // F227 recorded as having no adopter). MinHostVersion is 1.2.5 for this.
                    new PaneAction { Label = "Show me 5 examples", InvokeAsync = PreviewDispositionAsync, InvokeWithPendingAsync = PreviewDispositionPendingAsync, Group = "Persona" },
                    new PaneAction { Label = "5 about my screen", InvokeAsync = PreviewDispositionLiveAsync, InvokeWithPendingAsync = PreviewDispositionLivePendingAsync, Group = "Persona" },
                    new PaneAction { Label = "Refresh local models", InvokeAsync = RefreshLocalModelsAsync, Group = "Local provider", ReloadPaneAfter = true },
                    new PaneAction { Label = "Test connection", InvokeAsync = TestConnectionAsync, InvokeWithPendingAsync = TestConnectionPendingAsync, Group = "Cloud provider" },
                    new PaneAction { Label = "Refresh cloud models", InvokeAsync = RefreshCloudModelsAsync, Group = "Cloud provider", ReloadPaneAfter = true },
                    new PaneAction { Label = "Choose OCR engine…", InvokeAsync = ChooseOcrEngineAsync, Group = "Screen reading", ReloadPaneAfter = true },
                    new PaneAction { Label = "Get Tesseract…", InvokeAsync = GetTesseractAsync, Group = "Screen reading" },
                    new PaneAction { Label = "Test OCR", InvokeAsync = TestOcrAsync, InvokeWithPendingAsync = TestOcrPendingAsync, Group = "Screen reading" },
                },
            });

            ApplyState();
        }

        /// <summary>
        /// Guard against a second audition starting while one is running. Five sequential generations is
        /// the one action in this pane long enough for an impatient user to press twice, and the backlog
        /// called that out: pressing it repeatedly while comparing characters would otherwise queue 25
        /// generations against one local model. The press arrives on the UI thread, but the CLEAR does not:
        /// the audition awaits with ConfigureAwait(false), so its finally runs on a pool thread. An earlier
        /// comment here claimed a single-thread model the code had stopped following (F062); the flag is an
        /// int taken and released with interlocked operations, so the check and the set are one step and a
        /// second press gets an explanation instead of silently doubling the work.
        /// </summary>
        private int _auditionRunning;

        /// <summary>Claim the audition slot; false when one is already running.</summary>
        internal bool TryBeginAudition()
        {
            return Interlocked.CompareExchange(ref _auditionRunning, 1, 0) == 0;
        }

        /// <summary>Release the audition slot, from whichever thread the audition ended on.</summary>
        internal void EndAudition()
        {
            Interlocked.Exchange(ref _auditionRunning, 0);
        }

        /// <summary>
        /// "Show me 5 examples" for the Persona card: generate one remark per canned scene so a
        /// disposition can be judged by how it SOUNDS rather than by its name.
        ///
        /// Two entry points per button. <c>PaneAction.InvokeAsync</c> reads the SAVED settings and says so in
        /// the header; <c>PaneAction.InvokeWithPendingAsync</c> (host 1.2.5) is handed the pane as it stands and
        /// auditions THAT, so the user hears the disposition they just picked in the dropdown before committing
        /// to it. The rule this comment used to record, "a module cannot see a pending field value", was true
        /// until host 1.2.5 added the member; F227 noted it had no adopter, and this is the adoption (RA-055). The
        /// host prefers the pending-aware delegate when both are set. The pending copy is detached (CloneForBrain),
        /// so nothing an audition does with it can reach the settings file.
        /// </summary>
        private Task<string> PreviewDispositionAsync()
        {
            return PreviewDispositionAsync(false);
        }

        private Task<string> PreviewDispositionPendingAsync(IReadOnlyDictionary<string, string> pending)
        {
            return PreviewDispositionAsync(false, pending);
        }

        /// <summary>
        /// "5 about my screen": the same audition, but against the REAL screen instead of canned scenes.
        ///
        /// The more honest of the two, and the maintainer's point when asking for it: it is literally
        /// what the companion would say about what you are doing, carrying every quirk a real turn has
        /// (the capture clamped to the COMPANION's monitor, the OCR engine that is actually installed,
        /// the vision downscale). Needs a companion on screen, because ScreenContext is anchored to one
        /// and MonitorBounds means "the monitor the companion is on".
        /// </summary>
        private Task<string> PreviewDispositionLiveAsync()
        {
            return PreviewDispositionAsync(true);
        }

        private Task<string> PreviewDispositionLivePendingAsync(IReadOnlyDictionary<string, string> pending)
        {
            return PreviewDispositionAsync(true, pending);
        }

        // internal, not private: the module self-test presses it with the slot held (F062).
        internal Task<string> PreviewDispositionAsync(bool live)
        {
            return PreviewDispositionAsync(live, null);
        }

        /// <param name="pending">The pane's on-screen values, or null to audition the saved settings (RA-055).</param>
        internal async Task<string> PreviewDispositionAsync(bool live, IReadOnlyDictionary<string, string> pending)
        {
            if (!TryBeginAudition()) return "⏳ Already generating examples — give it a moment.";
            try { return await RunAuditionAsync(live, pending).ConfigureAwait(false); }
            finally { EndAudition(); }
        }

        /// <summary>
        /// The settings as the pane shows them right now: a detached copy of the live instance with the on-screen
        /// values applied through the same mapping Save uses, so an action asked about "what is on screen" answers
        /// about exactly what Apply would store. It cannot be saved (CloneForBrain marks it), so nothing an action
        /// does with it reaches the file. Null, with the reason, when a typed key could not be stored on the copy
        /// (no provider or endpoint to scope it to, or DPAPI refused): the action would otherwise run on a key
        /// other than the one typed (RA-055).
        /// </summary>
        private AiSettings PendingSettings(AiSettings live, IReadOnlyDictionary<string, string> values, out string error)
        {
            AiSettings pending = live.CloneForBrain();
            if (!ApplyPaneValues(pending, values, out error)) return null;
            return pending;
        }

        private async Task<string> RunAuditionAsync(bool live, IReadOnlyDictionary<string, string> pending)
        {
            AiSettings saved = _settings;
            if (saved == null) return "✗ No settings.";
            string pendingError = null;
            AiSettings s = pending == null ? saved : PendingSettings(saved, pending, out pendingError);
            if (s == null) return "✗ " + pendingError;

            string dispositionName = DispositionNameForId(s.Disposition);

            // A live audition reads the screen through a companion handle, so one has to be out. Said
            // plainly rather than falling back to canned scenes, which would silently answer a different
            // question than the one the button asks.
            ScreenContext liveContext = null;
            string petZone = null;
            if (live)
            {
                IHost host = _host;
                ICompanion pet = _lastPet;
                if (host == null) return "✗ No host.";
                if (pet == null || !host.IsCompanionAlive(pet))
                    return "✗ No companion on screen to look through — add one from the tray, then try again.";
                try { liveContext = host.CaptureScreenContext(pet); }
                catch (Exception ex) { return "✗ Couldn't read the screen: " + ex.Message; }
                if (liveContext == null)
                    return "✗ Couldn't read the screen (the companion went away).";
                petZone = liveContext.WindowUnderCompanion;
            }

            // A cloud provider means five billable requests per press, and the persona is exactly the
            // thing a user presses repeatedly while comparing. CreateBrain already refuses a non-local
            // endpoint without CloudDataConsent, so the consent gate is inherited rather than
            // reimplemented; what is added here is telling the user the COST before they spend it.
            string endpoint = SelectedEndpoint(s);
            string normalized, endpointError;
            if (!AiEndpointPolicy.TryNormalize(endpoint, out normalized, out endpointError))
                return "✗ " + endpointError;
            bool cloud = !AiEndpointPolicy.IsLoopbackEndpoint(normalized);
            if (cloud && !s.CloudDataConsent)
                return "✗ Approve cloud data sharing first — an audition sends " +
                       DispositionScenes.All.Length + " requests to your provider.";

            // Per sample rather than for the run, so one stuck generation costs one example instead of
            // all five. Floor of 20s because a local 4B model answering a text prompt is ~400ms but a
            // cold first load is seconds, and a ceiling so a misconfigured timeout cannot pin the pane.
            TimeSpan perSample = TimeSpan.FromSeconds(Math.Max(20, Math.Min(90, s.TimeoutSeconds)));
            // A whole-run bound as well: PaneAction has no cancel affordance, so "cancellable" here means
            // "cannot run forever". The engine also honours this token between samples.
            TimeSpan whole = TimeSpan.FromSeconds(perSample.TotalSeconds * DispositionScenes.All.Length + 15);

            try
            {
                AiBrain brain;
                // A short positive keep_alive for the audition's Ollama client under "unload" residency: five
                // back-to-back samples with keep_alive:0 raced Ollama's eviction and could pay up to four extra
                // cold loads (F070). Evicted explicitly when the run ends, below. Built through the seam when the
                // self-test set one, so the three decisions this method makes (the keep_alive window, no warm-up,
                // the eviction at the end) are pinned where they are MADE and not only on the helpers they call
                // (RA-073).
                int? keepAlive = AuditionKeepAliveSeconds(s);
                Func<AiSettings, int?, AiBrain> factory = AuditionBrainFactoryForDiagnostics;
                try { brain = factory != null ? factory(s, keepAlive) : CreateBrain(s, keepAlive); }
                catch (Exception ex) { return "✗ " + ex.Message; }

                using (brain)
                using (var run = new CancellationTokenSource(whole))
                {
                    // Fills the model inventory, so ChooseModel can tell "configured model is missing"
                    // from "backend is down" instead of producing five identical silences (BUG-002). No warm-up:
                    // the canned samples run on the text model and the live ones load whatever they use on their
                    // first sample; PrepareAsync's warm-up belongs to the launch routine (F063).
                    if (!await brain.PrepareAsync(run.Token, false).ConfigureAwait(false))
                        return "✗ Not reachable at " + normalized + " — start the provider and try again.";

                    DispositionAudition audition =
                        await brain.SampleDispositionAsync(
                            s.Disposition, liveContext, petZone, perSample, run.Token)
                            .ConfigureAwait(false);
                    // The VRAM back now, under "unload": the samples held the model for a minute between them, and
                    // the residency's promise is that it is gone after the remark. Bounded like a retirement's
                    // eviction (RA-056): on a cloud composite this round-trips to local Ollama for a model the canned
                    // samples never loaded, about 4 s per request against nothing listening on localhost, and it
                    // used to run under the chat deadline (120 s by default) with the pane waiting on it.
                    if (string.Equals(s.ModelResidency, AiSettings.ResidencyUnload, StringComparison.OrdinalIgnoreCase))
                        await UnloadWithinAsync(brain, AuditionUnloadBudget, run.Token).ConfigureAwait(false);
                    return FormatAudition(dispositionName, audition, cloud, live, pending != null);
                }
            }
            catch (OperationCanceledException)
            {
                return "✗ Gave up after " + (int)whole.TotalSeconds + "s — the provider is too slow for an audition.";
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        /// <summary>
        /// Render an audition. Names the persona (so previewing the saved one is obvious), labels each
        /// sample with the scene that produced it (so a character that only works on code is visible), and
        /// reports failures per sample rather than collapsing the run into one error.
        /// </summary>
        private static string FormatAudition(
            string dispositionName, DispositionAudition audition, bool cloud, bool live, bool pendingValues)
        {
            IReadOnlyList<DispositionSample> samples = audition == null ? null : audition.Samples;
            if (samples == null || samples.Count == 0)
                return "✗ No examples were produced.";

            var sb = new StringBuilder();
            // Which settings this is an audition OF. The pending-aware press names the dropdown's choice, unsaved;
            // the saved-values press says so, as it always has (RA-055).
            sb.Append(dispositionName).Append(pendingValues ? " — as shown in the pane, not yet applied" : " — as currently saved");
            if (!string.IsNullOrEmpty(audition.ModelUsed)) sb.Append(" · ").Append(audition.ModelUsed);
            sb.Append(live ? " · your real screen" : " · made-up scenes");
            if (cloud) sb.Append(" · ").Append(samples.Count).Append(" cloud requests");
            sb.Append(pendingValues
                ? "\nHit Apply to keep it, or pick another and press again."
                : "\nChange the dropdown and hit Apply to audition a different one.");
            // A substituted model has to be said out loud here. ChooseModel swaps in whatever is
            // available when the configured id is missing, and a user auditioning personas on a model
            // they never picked would blame the character for the model's output.
            if (!string.IsNullOrEmpty(audition.Advisory))
                sb.Append("\n⚠ ").Append(audition.Advisory);

            int ok = 0;
            int index = 0;
            foreach (DispositionSample sample in samples)
            {
                index++;
                sb.Append("\n\n");
                // Canned mode labels each remark with the scene that produced it, because the scene is
                // what varies. Live mode asks about ONE screen, so the label would be the same five
                // times; numbering is what carries information there.
                sb.Append("• [").Append(live ? index.ToString(CultureInfo.InvariantCulture) : sample.SceneLabel).Append("] ");
                if (sample.Ok)
                {
                    ok++;
                    sb.Append(Ellipsize(sample.Text, 220));
                }
                else
                {
                    sb.Append("✗ ").Append(sample.Error);
                }
                // The sample's own latency, so "the provider is too slow" (the timeout message's guess) can be
                // read off the pane: one cold-load count followed by four warm sub-second ones is the residency
                // story in a line. The diagnostic log carried this per request; the pane threw it away (F076).
                sb.Append(" · ")
                  .Append((sample.ElapsedMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture))
                  .Append(" s");
            }
            if (ok == 0)
                sb.Append("\n\nNothing came back. The diagnostic log records why, under Modules.");
            return sb.ToString();
        }

        // internal, not private: the module self-test cuts a sample that ends in an emoji through it (RA-057).
        internal static string Ellipsize(string value, int maximum)
        {
            string one = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            // At a code-point boundary, the helper the OCR text goes through two lines from the other cut: a sample
            // is model text and can end in an emoji, and a plain Substring at a UTF-16 index split the pair and put a
            // lone surrogate (U+FFFD on screen) in the pane (RA-057).
            return one.Length > maximum ? UnicodeTextProgress.TruncateAtCodePointBoundary(one, maximum) + "…" : one;
        }

        /// <summary>Test-connection action for the WPF pane: build a backend from the settings, probe availability +
        /// a tiny chat, and report a status line. Async so the pane stays responsive. Saved and pending entry points,
        /// as for the audition (RA-055): the pending one tests the provider, endpoint, key and model on screen.</summary>
        private Task<string> TestConnectionAsync()
        {
            return TestConnectionAsync(_settings);
        }

        private Task<string> TestConnectionPendingAsync(IReadOnlyDictionary<string, string> pending)
        {
            AiSettings saved = _settings;
            if (saved == null) return Task.FromResult("No settings.");
            string error;
            AiSettings s = PendingSettings(saved, pending, out error);
            return s == null ? Task.FromResult("✗ " + error) : TestConnectionAsync(s);
        }

        private async Task<string> TestConnectionAsync(AiSettings s)
        {
            if (s == null) return "No settings.";
            string endpoint = SelectedEndpoint(s);
            string normalized, err;
            if (!AiEndpointPolicy.TryNormalize(endpoint, out normalized, out err)) return "✗ " + err;
            if (!AiEndpointPolicy.IsLoopbackEndpoint(normalized) && !s.CloudDataConsent)
                return "✗ Approve cloud data sharing first.";
            try
            {
                TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(10, Math.Min(120, s.TimeoutSeconds)));
                bool local = IsLocalSlot(s);
                ICompanionBrainBackend backend = local
                    ? BuildLocalBackend(s, normalized, timeout)
                    : (ICompanionBrainBackend)new OpenAiCompatBackend(normalized, s.ApiKey, timeout);
                using (backend)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    if (!await backend.IsAvailableAsync(CancellationToken.None).ConfigureAwait(false))
                        return "✗ Not reachable at " + normalized;
                    // Test whichever slot is active: cloud model when a cloud provider is selected, else local.
                    // A blank cloud model is refused, not defaulted: this used to substitute the LOCAL default
                    // "gemma3:4b" and report the provider's 400 (F101).
                    string activeModel = local ? s.TextModel : s.CloudTextModel;
                    if (string.IsNullOrWhiteSpace(activeModel))
                        return local
                            ? "✗ No local text model is set."
                            : "✗ Pick a cloud text model first (Refresh cloud models, then choose one).";
                    string model = activeModel.Trim();
                    var msgs = new List<ChatMessage> { ChatMessage.System("Reply with OK."), ChatMessage.User("OK?", null) };
                    string reply = await backend.ChatAsync(model, msgs, false, CancellationToken.None).ConfigureAwait(false);
                    sw.Stop();
                    return TestConnectionVerdict(reply, model, sw.ElapsedMilliseconds);
                }
            }
            catch (AiBackendHttpException ex) { return DescribeHttpFailure(ex, normalized); }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        /// <summary>
        /// The pane's line for an answered HTTP failure: the status, the host it came from, the likely cause
        /// for the ones a user can fix themselves, and the provider's own words when it sent any. "AI backend
        /// returned HTTP 401." named neither the host nor the key (F107), and the provider's error.message
        /// ("Insufficient credits", "not a valid model ID") was never read at all (F080). Pane text only; the
        /// diagnostic log carries the status category.
        /// </summary>
        internal static string DescribeHttpFailure(AiBackendHttpException ex, string endpoint)
        {
            string host = AiBrain.DescribeEndpoint(endpoint);
            string cause;
            switch (ex.StatusCode)
            {
                case 401:
                case 403: cause = "the provider rejected the API key"; break;
                case 402: cause = "the provider wants payment or credits on this key"; break;
                case 404: cause = "check the base URL (it usually ends in /v1) and the model id"; break;
                case 429: cause = "the provider is rate-limiting this key"; break;
                default: cause = ex.StatusCode >= 500 ? "the provider is having trouble; try again later" : ""; break;
            }
            string line = "✗ HTTP " + ex.StatusCode.ToString(CultureInfo.InvariantCulture) + " from " + host +
                          (cause.Length > 0 ? ": " + cause : "");
            if (!string.IsNullOrEmpty(ex.ProviderMessage)) line += " (" + ex.ProviderMessage + ")";
            return line;
        }

        /// <summary>
        /// "Choose OCR engine…": browse to a tesseract.exe the auto-detect didn't find (a portable or
        /// toolbox install). The host owns the dialog; the path is saved and immediately re-tested, so the
        /// user gets a green/red answer in one step rather than picking blind and wondering.
        /// </summary>
        private async Task<string> ChooseOcrEngineAsync()
        {
            IHost host = _host;
            AiSettings s = _settings;
            if (host == null || s == null) return "No settings.";
            try
            {
                IReadOnlyList<string> picked = host.PickFilesToOpen(
                    "Choose an OCR engine (tesseract.exe)", "Programs", new[] { "exe" });
                if (picked == null || picked.Count == 0) return "";   // cancelled

                s.TesseractPath = picked[0];
                if (!s.SaveWithin(AiSettings.UiSaveBudgetMilliseconds))
                    return "✗ Couldn't save the OCR engine path.";
                return await TestOcrAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { return "✗ Couldn't set the OCR engine: " + ex.Message; }
        }

        /// <summary>
        /// "Get Tesseract…": open the official download page. Screen reading works without it (Windows'
        /// built-in engine is the fallback), but Tesseract generally reads dense text better, so this is
        /// how a user finds the upgrade instead of having to know it exists. The standard installer lands
        /// in %ProgramFiles%\Tesseract-OCR, which auto-detect already checks — so after installing, "Test
        /// OCR" just goes green with no path to configure.
        /// </summary>
        private Task<string> GetTesseractAsync()
        {
            IHost host = _host;
            if (host == null) return Task.FromResult("No host.");
            const string url = "https://tesseract-ocr.github.io/tessdoc/Installation.html";
            bool opened = host.OpenLink(Info.Id, url);
            return Task.FromResult(opened
                ? "Opened the Tesseract install guide. Install it, then click Test OCR."
                : ("Couldn't open a browser — see " + url));
        }

        /// <summary>"Test OCR" action: run the OCR self-test (resolve tesseract + read a known image) so a
        /// missing/broken engine surfaces as a red status instead of silently making remarks screen-blind. Saved and
        /// pending entry points (RA-055): the pending one tests the path typed in the pane, applied or not.</summary>
        // internal, not private: the module self-test presses it and asserts what it does to the LIVE brain (R-015).
        internal Task<string> TestOcrAsync()
        {
            return TestOcrAsync(_settings ?? new AiSettings());
        }

        private Task<string> TestOcrPendingAsync(IReadOnlyDictionary<string, string> pending)
        {
            AiSettings saved = _settings ?? new AiSettings();
            string error;
            AiSettings s = PendingSettings(saved, pending, out error);
            return s == null ? Task.FromResult("✗ " + error) : TestOcrAsync(s);
        }

        private async Task<string> TestOcrAsync(AiSettings s)
        {
            try
            {
                string verdict;
                using (var probe = new AiBrain(null, s))
                    verdict = await probe.SelfTestOcrAsync(CancellationToken.None).ConfigureAwait(false);
                // The probe above is a throwaway. The brain that reads screens lives in the session and cached its
                // own resolution when it was built, so until 2026-09-30 this button went green after an install while
                // every remark kept the engine the live brain had resolved at build time (R-015). Forget that cache,
                // with the path the SAVED settings name: "Choose OCR engine..." saves the chosen path and lands here,
                // while a path typed but not applied is tested on the throwaway above and must not re-point the live
                // brain at an engine the settings do not name yet (Apply rebuilds the brain anyway, RA-055).
                AiSettings live = _settings;
                _session.ForgetOcrResolution(live != null ? live.TesseractPath : s.TesseractPath);
                return verdict;
            }
            catch (Exception ex) { return "✗ OCR test failed: " + ex.Message; }
        }

        private IReadOnlyDictionary<string, string> LoadPaneValues()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            AiSettings s = _settings;
            if (s != null)
            {
                d["enabled"] = s.AiBrainEnabled ? "true" : "false";
                d["brainStatus"] = BrainStatusLine(s);
                d["companionName"] = s.CompanionName ?? "";
                d["userName"] = s.UserName ?? "";
                d["disposition"] = DispositionNameForId(s.Disposition);
                // Local provider slot (always available; Ollama-native by default, or a generic
                // OpenAI-compatible /v1 server such as llama.cpp/LM Studio).
                d["localBackendKind"] = LocalBackendKindLabelForId(s.LocalBackendKind);
                d["endpoint"] = s.Endpoint ?? "";
                // SNAPSHOT under the lock, FORMAT outside it. The lock guards the two list copies and
                // nothing else: the label formatting below and VramStatusLine stay outside it. (This used
                // to cite VramStatusLine's "blocking model query -- up to a couple of seconds" as the thing
                // kept out of the critical section; that line has been a cached string plus a pool-thread
                // probe since 1.1.11, F064.) Copying two short lists costs nothing and gives the four label
                // reads below a consistent view even if a refresh replaces a list midway.
                ModelListing[] localSnapshot, cloudSnapshot;
                lock (_modelsLock)
                {
                    localSnapshot = _localModels.ToArray();
                    cloudSnapshot = _cloudModels.ToArray();
                }
                d["textModel"] = FormatModelLabel(s.TextModel, localSnapshot);
                d["visionModel"] = FormatModelLabel(s.VisionModel, localSnapshot);
                d["useVision"] = s.UseVision ? "true" : "false";
                d["tesseractPath"] = s.TesseractPath ?? "";
                d["autoStart"] = s.AutoStartServer ? "true" : "false";
                d["residency"] = ResidencyLabel(s.ModelResidency);
                d["standDownFullscreen"] = s.StandDownForFullscreen ? "true" : "false";
                d["vramStatus"] = VramStatusLine(s);
                // Cloud provider slot.
                d["cloudProvider"] = CloudProviderLabelForId(s.Provider);
                d["cloudEndpoint"] = s.OpenAiBaseUrl ?? "";
                d["cloudTextModel"] = FormatModelLabel(s.CloudTextModel, cloudSnapshot);
                d["cloudVisionModel"] = FormatModelLabel(s.CloudVisionModel, cloudSnapshot);
                d["cloudConsent"] = s.CloudDataConsent ? "true" : "false";
                d["apiKey"] = string.IsNullOrEmpty(s.ApiKey) ? "" : "set";   // cloud-key presence hint; never the plaintext
                d["useLocalFallback"] = s.UseLocalFallback ? "true" : "false";
                d["hotkey"] = s.Hotkey ?? "";
            }
            return d;
        }

        private bool SavePaneValues(IReadOnlyDictionary<string, string> values)
        {
            AiSettings s = _settings;
            if (s == null || values == null) return false;
            string keyError;
            // Judged on a detached copy BEFORE anything is written onto the live instance (N-burn-aibrain-02):
            // ApplyPaneValues writes every other field before it reaches the key, so a save refused for its key
            // used to leave the live settings carrying the pane's typed values, unsaved and unapplied, until the
            // next Apply or restart. The copy is PendingSettings' (the mapping the pending-aware actions already
            // use), and the live write runs only once the copy has proved the key storable; its own answer is
            // still read, so a key refused between the two calls cannot be dropped silently. One short-circuit,
            // one decision, so a single mutant can defeat it.
            bool storable = PendingSettings(s, values, out keyError) != null && ApplyPaneValues(s, values, out keyError);
            if (!storable)
            {
                // A key was typed and NOT stored: no cloud provider or endpoint to scope it to, or DPAPI refused.
                // Persisting the rest and answering true made the host report a saved pane with the key silently
                // dropped, and the pane's "set" hint then described a key that did not exist (RA-058). Nothing is
                // saved or applied: the host says these settings could not be saved, the typed values stay on
                // screen for the user to fix and press again, and the running brain keeps the saved configuration.
                // The reason goes to the log, the channel the pane's bool cannot carry.
                try { if (_host != null) _host.Log(Info.Id, "api key not stored: " + keyError); } catch { }
                return false;
            }
            bool ok = s.SaveWithin(AiSettings.UiSaveBudgetMilliseconds);
            ApplyState();   // re-apply triggers/backend to reflect the new config
            return ok;
        }

        /// <summary>
        /// Write the pane's values onto <paramref name="s"/>: every field the schema declares, in the order the
        /// cloud key needs (provider and endpoint before the key that is scoped to them). Shared by Save (onto the
        /// live instance) and by the pending-aware actions (onto a detached copy), so an action asked about "what is
        /// on screen" maps the pane exactly as Apply would (RA-055). False, with the reason, when a typed key could
        /// not be stored; every other field is applied regardless.
        /// </summary>
        private bool ApplyPaneValues(AiSettings s, IReadOnlyDictionary<string, string> values, out string keyError)
        {
            keyError = null;
            bool keyStored = true;
            string v;
            bool b;
            if (values.TryGetValue("enabled", out v) && bool.TryParse(v, out b)) s.AiBrainEnabled = b;
            if (values.TryGetValue("companionName", out v)) s.CompanionName = (v ?? "").Trim();
            if (values.TryGetValue("userName", out v)) s.UserName = (v ?? "").Trim();
            if (values.TryGetValue("disposition", out v)) s.Disposition = DispositionIdForName(v);
            // ---- Local provider slot: always present; Ollama-native by default, or a generic
            // OpenAI-compatible /v1 server (llama.cpp/LM Studio/other) via localBackendKind ----
            if (values.TryGetValue("localBackendKind", out v)) s.LocalBackendKind = LocalBackendKindIdForLabel(v);
            if (values.TryGetValue("endpoint", out v) && !string.IsNullOrWhiteSpace(v)) s.Endpoint = v.Trim();
            if (values.TryGetValue("textModel", out v) && !string.IsNullOrWhiteSpace(v)) s.TextModel = ResolveModelId(v);
            if (values.TryGetValue("visionModel", out v) && !string.IsNullOrWhiteSpace(v)) s.VisionModel = ResolveModelId(v);
            if (values.TryGetValue("useVision", out v) && bool.TryParse(v, out b)) s.UseVision = b;
            // Blank is meaningful here (= auto-detect), so unlike endpoint/model this one accepts an empty
            // value rather than treating it as "leave unchanged".
            if (values.TryGetValue("tesseractPath", out v)) s.TesseractPath = (v ?? "").Trim();
            if (values.TryGetValue("autoStart", out v) && bool.TryParse(v, out b)) s.AutoStartServer = b;
            if (values.TryGetValue("residency", out v)) s.ModelResidency = ResidencyFromLabel(v);
            if (values.TryGetValue("standDownFullscreen", out v) && bool.TryParse(v, out b)) s.StandDownForFullscreen = b;
            // ---- Cloud provider slot ----
            // Switching the cloud provider prefills its preset endpoint (the stale endpoint field is ignored
            // on a switch); "(none)" clears the cloud selection (local-only); keeping the provider honors an
            // edited cloud endpoint. Reuses the unchanged SelectProviderEndpoint/UpdateSelectedProviderEndpoint.
            bool cloudProviderChanged = false;
            if (values.TryGetValue("cloudProvider", out v))
            {
                string newProvider = CloudProviderIdForLabel(v);
                cloudProviderChanged = !string.Equals(newProvider, s.Provider ?? "", StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrEmpty(newProvider))
                    s.Provider = "";   // "(none)" -> local-only; leaves the remembered cloud endpoint intact
                else
                    s.SelectProviderEndpoint(newProvider, cloudProviderChanged);
            }
            if (!cloudProviderChanged && !string.IsNullOrEmpty(s.Provider) &&
                values.TryGetValue("cloudEndpoint", out v) && !string.IsNullOrWhiteSpace(v))
                s.UpdateSelectedProviderEndpoint(v.Trim());
            // Cloud models are optional (empty = unset), so unlike the local models they may be cleared.
            if (values.TryGetValue("cloudTextModel", out v)) s.CloudTextModel = ResolveModelId((v ?? "").Trim());
            if (values.TryGetValue("cloudVisionModel", out v)) s.CloudVisionModel = ResolveModelId((v ?? "").Trim());
            if (values.TryGetValue("cloudConsent", out v) && bool.TryParse(v, out b)) s.CloudDataConsent = b;
            // Secret: only present when the user typed a new key; scoped to the CURRENT cloud provider +
            // endpoint (set just above), so it must run after the provider/endpoint fields. Its answer is the
            // method's: a false here and its reason used to be discarded (RA-058).
            if (values.TryGetValue("apiKey", out v) && !string.IsNullOrEmpty(v)) keyStored = s.TrySetApiKey(v, out keyError);
            // ---- Fallback + triggers ----
            if (values.TryGetValue("useLocalFallback", out v) && bool.TryParse(v, out b)) s.UseLocalFallback = b;
            if (values.TryGetValue("hotkey", out v) && !string.IsNullOrWhiteSpace(v)) s.Hotkey = v.Trim();
            return keyStored;
        }

        // Disposition enum: the pane shows the friendly Name, the setting stores the Id. The catalog itself
        // (curated characters, each a complete tone+voice instruction) lives in Dispositions.cs, shared with
        // the runtime prompt builder.
        private static string[] DispositionNames()
        {
            var names = new List<string>(Dispositions.All.Length);
            foreach (Dispositions.Disposition d in Dispositions.All) names.Add(d.Name);
            return names.ToArray();
        }
        private static string DispositionNameForId(string id)
        {
            foreach (Dispositions.Disposition d in Dispositions.All)
                if (string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) return d.Name;
            foreach (Dispositions.Disposition d in Dispositions.All)
                if (string.Equals(d.Id, Dispositions.DefaultId, StringComparison.OrdinalIgnoreCase)) return d.Name;
            return "";
        }
        private static string DispositionIdForName(string name)
        {
            foreach (Dispositions.Disposition d in Dispositions.All)
                if (string.Equals(d.Name, name, StringComparison.Ordinal)) return d.Id;
            return Dispositions.DefaultId;
        }

        // Tray-item icons (TrayItem.IconPng): read once at Init from the module's own embedded PNGs, so the
        // base can show them without the ABI depending on System.Drawing. Null on any failure -- a
        // missing/malformed icon must never break the tray item, which is ModuleKit's contract too.
        private static byte[] LoadIconResource(string fileName)
        {
            return EmbeddedResources.LoadBytes(typeof(AiBrainModule).Assembly, fileName);
        }

        // Cloud-provider dropdown (schema v2): only the CLOUD selectors, with a friendly "(none)" for the
        // empty (local-only) value. The dropdown shows the label; the setting stores the id ("" for none).
        // The local presets (ollama/lmstudio/llamacpp) are intentionally NOT offered — the local slot is the
        // fixed Endpoint field now, not a Provider choice.
        private const string CloudNoneLabel = "(none)";
        private static string[] CloudProviderLabels()
        {
            return new[] { CloudNoneLabel, "openai", "openrouter", "custom" };
        }
        private static string CloudProviderLabelForId(string id)
        {
            switch ((id ?? "").Trim().ToLowerInvariant())
            {
                case "openai": return "openai";
                case "openrouter": return "openrouter";
                case "custom": return "custom";
                default: return CloudNoneLabel;   // "" / legacy local id / unknown -> none (local-only)
            }
        }
        private static string CloudProviderIdForLabel(string label)
        {
            switch ((label ?? "").Trim().ToLowerInvariant())
            {
                case "openai": return "openai";
                case "openrouter": return "openrouter";
                case "custom": return "custom";
                default: return "";   // "(none)" or anything unrecognized -> local-only
            }
        }

        // Local-backend dropdown: which protocol the LOCAL slot speaks (see AiSettings.LocalBackendKind).
        // Only two choices, so a straight label<->id mapping (no "none" case — local is always available).
        private const string OllamaNativeLabel = "Ollama (native)";
        private const string LocalCompatLabel = "Generic OpenAI-compatible (llama.cpp / LM Studio / other)";
        private static string[] LocalBackendKindLabels()
        {
            return new[] { OllamaNativeLabel, LocalCompatLabel };
        }
        private static string LocalBackendKindLabelForId(string id)
        {
            return string.Equals(id, "openai-compat", StringComparison.OrdinalIgnoreCase)
                ? LocalCompatLabel : OllamaNativeLabel;
        }
        private static string LocalBackendKindIdForLabel(string label)
        {
            return string.Equals((label ?? "").Trim(), LocalCompatLabel, StringComparison.OrdinalIgnoreCase)
                ? "openai-compat" : "ollama";
        }

        // ---- Model-picker dropdowns (local + cloud text/vision) --------------------------------------

        /// <summary>
        /// Formats a model id into its dropdown label — <c>"SIZE · id · uncensored"</c>, with the size
        /// prefix present only when <paramref name="models"/> has a listing for this id with a known
        /// <see cref="ModelListing.SizeBytes"/> (a real value from Ollama's own "size" field — a solid proxy
        /// for VRAM/weight footprint; the generic OpenAI-compatible list has no such metadata, so cloud/
        /// openai-compat entries never get a size prefix) and the uncensored suffix only when
        /// <see cref="AiModelPolicy.LooksUncensored"/> matches. Registers the (label, id) pair into
        /// <see cref="_modelIdByLabel"/> as a side effect so <see cref="ResolveModelId"/> can reverse it —
        /// a variable-length size prefix can't be recovered by a fixed string pattern the way the old
        /// suffix-only scheme could.
        /// </summary>
        private string FormatModelLabel(string id, IReadOnlyList<ModelListing> models)
        {
            if (string.IsNullOrEmpty(id)) return "";
            long? size = null;
            if (models != null)
                foreach (ModelListing m in models)
                    if (m != null && string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
                    {
                        size = m.SizeBytes;
                        break;
                    }
            var parts = new List<string>(3);
            if (size.HasValue) parts.Add(FormatSize(size.Value));
            parts.Add(id);
            if (AiModelPolicy.LooksUncensored(id)) parts.Add("uncensored");
            string label = string.Join(" · ", parts);
            lock (_modelsLock) _modelIdByLabel[label] = id;
            return label;
        }

        // Look up a label the pane returned back to id (registered by FormatModelLabel). A label that was
        // never produced by FormatModelLabel this session (shouldn't normally happen — Load always runs
        // before Save) falls back to treating it as a raw id, so a save is never silently lost.
        private string ResolveModelId(string label)
        {
            string id;
            lock (_modelsLock)
                if (!string.IsNullOrEmpty(label) && _modelIdByLabel.TryGetValue(label, out id)) return id;
            return label ?? "";
        }

        // A rough, human-scannable VRAM/weight-footprint size: whole MB under 1GB, one-decimal GB above.
        // Decimal (1000-based) units, matching the common informal convention for a plain "GB"/"MB" label.
        private static string FormatSize(long bytes)
        {
            const double MB = 1_000_000.0;
            const double GB = 1_000_000_000.0;
            if (bytes < 0) return "";
            return bytes >= GB
                ? (bytes / GB).ToString("0.0", CultureInfo.InvariantCulture) + "GB"
                : Math.Round(bytes / MB).ToString(CultureInfo.InvariantCulture) + "MB";
        }

        /// <summary>
        /// Builds one dropdown's Options: every listed model (or only vision-capable ones when
        /// <paramref name="visionOnly"/> — the UNION of what the backend reported and the
        /// <see cref="AiModelPolicy.LooksVisionCapable"/> heuristic, because Ollama's /api/tags omits
        /// "vision" for some models that have it), tagged/sorted so uncensored-leaning
        /// models (<see cref="AiModelPolicy.LooksUncensored"/> — an advisory for personas that need a model
        /// to actually comply, e.g. Samuel/Triumph; never a hard filter) come first, each labeled with its
        /// known size (see <see cref="FormatModelLabel"/>) when available. SAFETY INVARIANT: the
        /// currently-saved value is always unioned in (labeled the same way), even if the fetch hasn't run
        /// yet, came back empty, or doesn't cover it — the pane's Enum dropdown is a closed, non-editable
        /// ComboBox (see PaneView.Build), so a value missing from Options would show nothing selected and a
        /// save would silently blank the field.
        /// </summary>
        internal string[] BuildModelOptions(IReadOnlyList<ModelListing> models, string currentValue, bool visionOnly)
        {
            var uncensoredLabels = new List<string>();
            var otherLabels = new List<string>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (models != null)
                foreach (ModelListing model in models)
                {
                    // Contains, NOT Add. seenIds is what the union guard at the bottom consults to decide
                    // whether the saved value still needs inserting, so an id must not count as "already
                    // offered" until it has survived the vision filter below. Marking it here meant a saved
                    // vision model the filter rejects was neither listed NOR unioned back in -- and the
                    // dropdown is a closed ComboBox, so it showed nothing selected and the next save wrote
                    // the blank. That is exactly what the SAFETY INVARIANT above says cannot happen.
                    if (model == null || string.IsNullOrEmpty(model.Id) || seenIds.Contains(model.Id)) continue;
                    // UNION, not fallback. `??` only fires when the backend reported nothing, and
                    // Ollama's /api/tags reports a capabilities array that is INCOMPLETE for the Gemma
                    // family. Measured 2026-09-10 on one machine:
                    //
                    //   gemma3:4b              /api/show: completion,vision   /api/tags: completion
                    //   gemma4:26b             /api/show: completion,vision,… /api/tags: completion,…
                    //   mistral-small3.2:24b   /api/show: completion,vision,… /api/tags: vision,…
                    //
                    // So VisionFromCapabilities saw ["completion"], correctly returned false rather than
                    // null, and `??` treated an incomplete report as authoritative -- which hid the
                    // RECOMMENDED vision model from its own dropdown, while the "gemma3" name marker that
                    // exists for precisely this case was never consulted.
                    //
                    // Re-measured 2026-09-30 on Ollama 0.34.4: /api/tags and /api/show AGREE for gemma3:4b,
                    // gemma4:12b and gemma4:26b, so the under-report belongs to older servers. The union stays
                    // here because such a server may still be in use and a hidden model is the worse failure;
                    // the ASK trusts a reported false (AiModelPolicy.ChooseModel, F102 and R-020).
                    //
                    // The asymmetry decides the direction: a false positive is visible and recoverable
                    // (the user picks a model that cannot see, and changes it), while a false negative
                    // hides a working model with no way to discover it exists. /api/show would be
                    // authoritative but costs one request per model on every pane open; revisit if the
                    // name markers ever go stale enough to matter.
                    bool isVision = AiModelPolicy.IsVisionCapable(model.Id, model.Vision);
                    if (visionOnly && !isVision) continue;
                    seenIds.Add(model.Id);
                    (AiModelPolicy.LooksUncensored(model.Id) ? uncensoredLabels : otherLabels).Add(FormatModelLabel(model.Id, models));
                }
            var result = new List<string>(uncensoredLabels.Count + otherLabels.Count + 1);
            result.AddRange(uncensoredLabels);
            result.AddRange(otherLabels);
            if (!string.IsNullOrEmpty(currentValue) && seenIds.Add(currentValue))
                result.Insert(0, FormatModelLabel(currentValue, models));
            return result.ToArray();
        }

        /// <summary>
        /// The verdict half of "Test connection", separate from the I/O so it can be asserted with no
        /// server at all.
        ///
        /// An empty reply is a FAILURE, and that is the whole point. The caller used to discard the
        /// reply and report a green tick, so what it proved was that nothing threw -- and neither backend
        /// throws on a 200 carrying no content: OpenAiCompatBackend.ChatAsync returns "" when the choices
        /// array is missing or empty, and OllamaClient.ChatAsync has the same shape. A model id the
        /// provider does not serve, an exhausted quota and a moderation refusal all land there, and all
        /// three were reported as "connected".
        /// </summary>
        internal static string TestConnectionVerdict(string reply, string model, long elapsedMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(reply))
                return "✗ " + model + " answered with nothing — check the model id, the quota, and whether the provider refused.";
            return "✓ connected · " + model + " OK " +
                   (elapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }

        /// <summary>
        /// Rebuild the four model dropdowns' Options IN PLACE on the retained SettingField objects (the only
        /// way a refresh becomes visible to the pane — Schema itself is never rebuilt; PaneView re-reads
        /// Options fresh on each rebuild triggered by a PaneAction's ReloadPaneAfter). Called once at Init
        /// (the caches start empty, so this just seeds the current-value safety net) and again at the end of
        /// each "Refresh ... models" action.
        /// </summary>
        private void RefreshModelFieldOptions()
        {
            AiSettings s = _settings;
            lock (_modelsLock)
            {
                _textModelField.Options = BuildModelOptions(_localModels, s != null ? s.TextModel : "", false);
                _visionModelField.Options = BuildModelOptions(_localModels, s != null ? s.VisionModel : "", true);
                _cloudTextModelField.Options = BuildModelOptions(_cloudModels, s != null ? s.CloudTextModel : "", false);
                _cloudVisionModelField.Options = BuildModelOptions(_cloudModels, s != null ? s.CloudVisionModel : "", true);
            }
        }

        private static int CountUncensored(IReadOnlyList<ModelListing> models)
        {
            int count = 0;
            if (models != null)
                foreach (ModelListing m in models)
                    if (m != null && AiModelPolicy.LooksUncensored(m.Id)) count++;
            return count;
        }

        /// <summary>
        /// The status line of a "Refresh ... models" press. An empty list is named for its CAUSE (RA-059): both
        /// ListModelsAsync implementations fold every failure into an empty list by contract, because the brain
        /// reads empty as "unknown, do not complain", so an answered 401 read here as "No models found at <url>"
        /// and sent the user to check the URL rather than the key. An answered status gets F107's wording, the
        /// line "Test connection" gives for the same answer; a transport failure says not reachable; only a
        /// listing that answered and was empty says no models.
        /// </summary>
        internal static string ModelListStatus(IReadOnlyList<ModelListing> models, string target, Exception listingFailure)
        {
            if (models == null || models.Count == 0)
            {
                AiBackendHttpException answered = listingFailure as AiBackendHttpException;
                if (answered != null) return DescribeHttpFailure(answered, target);
                if (listingFailure != null)
                    return "✗ Not reachable at " + target + " (" + AiBrain.DescribeError(listingFailure) + ")";
                return "✗ No models found at " + target;
            }
            int uncensoredCount = CountUncensored(models);
            return "✓ " + models.Count.ToString(CultureInfo.InvariantCulture) + " model(s) found" +
                (uncensoredCount > 0
                    ? " (" + uncensoredCount.ToString(CultureInfo.InvariantCulture) + " tagged uncensored)"
                    : "");
        }

        /// <summary>"Refresh local models" pane action: lists whatever the LOCAL slot's configured backend
        /// reports (Ollama's real capabilities, or the generic /v1 id-only list for openai-compat), caches
        /// it, and rebuilds the local text/vision dropdowns from it.</summary>
        private async Task<string> RefreshLocalModelsAsync()
        {
            AiSettings s = _settings;
            if (s == null) return "✗ No settings.";
            string normalized, error;
            if (!AiEndpointPolicy.TryNormalize(s.Endpoint, out normalized, out error))
                return "✗ " + error;
            TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(10, Math.Min(120, s.TimeoutSeconds)));
            try
            {
                IReadOnlyList<ModelListing> models;
                Exception listingFailure = null;
                using (ICompanionBrainBackend backend = BuildLocalBackend(s, normalized, timeout))
                {
                    models = await ListBackendModelsAsync(backend, CancellationToken.None).ConfigureAwait(false)
                             ?? (IReadOnlyList<ModelListing>)new List<ModelListing>();
                    IModelListingStatus status = backend as IModelListingStatus;
                    if (status != null) listingFailure = status.LastListingFailure;   // RA-059
                }
                // One critical section: swapping the list and rebuilding the dropdowns from it is a
                // single logical update, so a concurrent cloud refresh cannot read a half-replaced list.
                lock (_modelsLock)
                {
                    _localModels.Clear();
                    _localModels.AddRange(models);
                    RefreshModelFieldOptions();
                }
                // The live brain re-lists its own backend too, so the pane and the brain agree about what is
                // installed: a model pulled mid-session stayed "missing" to the brain until the next Apply (F071).
                _ = _session.RefreshInventoryAsync(_lifetime.Token);
                return ModelListStatus(models, normalized, listingFailure);
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        /// <summary>"Refresh cloud models" pane action: lists the configured cloud endpoint's models
        /// (generic /v1, id-only), caches it, and rebuilds the cloud text/vision dropdowns from it.</summary>
        private async Task<string> RefreshCloudModelsAsync()
        {
            AiSettings s = _settings;
            if (s == null) return "✗ No settings.";
            if (string.IsNullOrEmpty(s.Provider)) return "✗ Select a cloud provider first.";
            string normalized, error;
            if (!AiEndpointPolicy.TryNormalize(s.OpenAiBaseUrl, out normalized, out error))
                return "✗ " + error;
            TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(10, Math.Min(120, s.TimeoutSeconds)));
            try
            {
                IReadOnlyList<ModelListing> models;
                Exception listingFailure;
                using (var backend = new OpenAiCompatBackend(normalized, s.ApiKey, timeout))
                {
                    models = await backend.ListModelsAsync(CancellationToken.None).ConfigureAwait(false);
                    listingFailure = backend.LastListingFailure;   // RA-059
                }
                lock (_modelsLock)
                {
                    _cloudModels.Clear();
                    _cloudModels.AddRange(models);
                    RefreshModelFieldOptions();
                }
                _ = _session.RefreshInventoryAsync(_lifetime.Token);   // as above (F071)
                return ModelListStatus(models, normalized, listingFailure);
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        /// <summary>Tray toggle: flip the module's own AiBrainEnabled, persist, and (re)build the brain.</summary>
        private void ToggleEnabled()
        {
            AiSettings s = _settings;
            if (s == null) return;
            s.AiBrainEnabled = !s.AiBrainEnabled;
            // SAYS SO WHEN IT FAILS. This was `try { s.Save(); } catch { }`, which discarded both the
            // return value and the exception -- so a toggle that did not persist looked exactly like one
            // that did, and came back on the next launch with no explanation. That matters more now the
            // budget is bounded: a contended save can legitimately return false.
            bool persisted;
            try { persisted = s.SaveWithin(AiSettings.UiSaveBudgetMilliseconds); }
            catch (Exception ex)
            {
                persisted = false;
                if (_host != null) _host.Log(Info.Id, "AI toggle not saved: " + ex.GetType().Name);
            }
            if (!persisted && _host != null)
                _host.Log(Info.Id, "AI toggle applied for this session but NOT saved; it will revert on restart");
            ApplyState();
        }

        private void OnPetSeen(ICompanion pet) { if (pet != null) _lastPet = pet; }
        private void OnPetPoked(PokeInfo info) { if (info != null && info.Pet != null) _lastPet = info.Pet; }

        /// <summary>
        /// Drop responder, and since 1.2.3 the ONLY schedule for unprompted commentary: the host's global
        /// "Randomly drop a fortune / insight" setting drives this, and the module no longer runs a timer of
        /// its own. When the brain is enabled this takes the tick as an AI insight (returns handled, so
        /// Fortunes stays quiet); otherwise it declines and a local fortune speaks.
        /// </summary>
        private bool OnDrop(ICompanion pet)
        {
            if (!_session.Enabled) return false;
            // Decline if the AI spoke very recently (a hotkey ask landing just before a drop), so the user
            // gets a fortune rather than two model answers back to back. This is the one piece of the old
            // idle-loop suppression worth keeping, and declining is strictly better than going silent
            // because the responder chain falls through to Fortunes.
            if ((DateTime.UtcNow - _lastInteractionUtc).TotalSeconds < 30) return false;
            // Don't load several GB of VRAM next to a game that already owns it. Declining is exactly right
            // here rather than going silent: the responder chain falls through to Fortunes, so the pet still
            // says something, it just says something free. And while a game is fullscreen the pet is hidden
            // anyway, so a model answer would be invisible as well as risky.
            if (FullscreenBlocked()) return false;
            // allowVision: TRUE, deliberately, and pinned by the module self-test. A drop is unprompted
            // commentary, and with UseVision on it is a vision turn exactly like the hotkey and the tray row:
            // the owner's decision of 2026-09-29 (BUG-010, docs/ISSUES-post-1.0.0.md) is that vision, when
            // enabled, applies to every remark, because the screen is what the remark is about. The audit
            // proposed `false` here to match a label written for the module's OLD idle loop; the label was
            // what changed. Only the poke stays text-only, and OnPokeReaction says why.
            return Ask(pet, true);
        }

        /// <summary>
        /// True when a fullscreen app is running AND the user asked us to stand down for it.
        ///
        /// Also releases anything already resident, which is the half that actually protects a game: a model
        /// loaded BEFORE the game started is not helped by declining to load. Cheap to call -- the host answers
        /// from the scan the pets already run.
        /// </summary>
        private bool FullscreenBlocked()
        {
            if (_settings == null || !_settings.StandDownForFullscreen) return false;
            bool active;
            try { active = _host != null && _host.IsFullscreenActive; } catch { return false; }
            if (!active) return false;
            ReleaseModelForFullscreen();
            return true;
        }

        /// <summary>
        /// Evict the local model so a game gets its VRAM back. Best-effort and fire-and-forget: this runs
        /// while a game is starting, which is the worst possible moment to block on anything.
        /// </summary>
        private void ReleaseModelForFullscreen()
        {
            try
            {
                _ = _session.ReleaseModelAsync(_lifetime.Token);
            }
            catch { }
        }

        /// <summary>Host said a fullscreen app appeared or went away. Appearing is the one that matters: it is
        /// the only moment we can release VRAM BEFORE the game needs it rather than after.</summary>
        private void OnFullscreenChanged(bool active)
        {
            if (!active) return;
            if (_settings == null || !_settings.StandDownForFullscreen) return;
            ReleaseModelForFullscreen();
        }

        /// <summary>Poke responder: the first poke of a session becomes an AI quip about the screen when
        /// the brain is on. Declines when off, so Fortunes (or nothing) handles it instead. Text-only —
        /// a vision glance can take tens of seconds, far too slow to feel like a reaction to a click.</summary>
        private bool OnPokeReaction(ICompanion pet)
        {
            if (!_session.Enabled) return false;
            if (FullscreenBlocked()) return false;   // same rule as the drop; Fortunes answers instead
            return Ask(pet, false);
        }

        // ---- the ask flow (mirrors the old StartUp.AskAboutScreen) --------------------------------

        /// <summary>
        /// Kick off one screen-commentary turn for a specific pet, on the UI thread (the drop, the poke, the
        /// hotkey, the tray row). <paramref name="subject"/> is the pet this turn belongs to; the hotkey and the
        /// tray row have no natural one, so they pass null and fall back to the last pet seen. Returns TRUE only
        /// when a turn was actually STARTED. (Two summaries sat here, the first naming an idle tick that went in
        /// 1.2.3, RA-054.)
        ///
        /// The drop and poke responders forward this as their "handled" answer. The chain runs highest
        /// priority first until one handler returns true and this module registers at 10 to outrank
        /// Fortunes, so claiming a turn that was declined is what makes the pet go silent instead of
        /// falling through to a fortune. Both callers used to `Ask(...); return true;`, which reported
        /// "handled" for all four of the early returns below -- most often RequestInProgress, i.e. exactly
        /// while a slow vision load is already running and a second poke arrives.
        ///
        /// The two hotkey/tray callers use this as a statement and ignore the result, which is correct: there is
        /// no responder chain behind them to fall through to. That is also why THEIR refusals are logged and the
        /// responders' are not (RA-060, extending F067's one line to every early return): a declined drop falls
        /// through to a fortune the user hears, while a dropped hotkey press is silent to the user and, until
        /// 2026-09-30, to the log SUPPORT.md asks for.
        /// </summary>
        private bool Ask(ICompanion subject, bool allowVision)
        {
            IHost host = _host;
            AiSessionManager session = _session;
            bool explicitPath = subject == null;
            if (host == null) return false;
            if (!session.Enabled) return Declined(host, explicitPath, "brain off");
            if (!host.SpeechEnabled) return Declined(host, explicitPath, "speech off");
            // One in-flight ask at a time, so at most one pending subject. Per-pet concurrency (two pets
            // asked at once) is BACKLOG #16(a) and deliberately not attempted here.
            if (session.RequestInProgress) return Declined(host, explicitPath, "busy");
            // The stand-down applies to EVERY entry point, not only the two responders. A global hotkey is
            // delivered while a game has focus, and the explicit path allows vision, so a press during a game
            // used to load the vision model beside the game the setting exists to protect and deliver the
            // answer to a companion the fullscreen logic had hidden (F067). The responders keep their own call
            // in front of this one: declining THERE is what lets the chain fall through to Fortunes, and the
            // source invariant asserts it there. The explicit path has no chain behind it, so a refusal here
            // would be silent to the user and to the log SUPPORT.md asks for; hence the line, for every caller
            // that reaches it.
            if (FullscreenBlocked())
            {
                LogDeclined(host, "fullscreen stand-down");
                return false;
            }
            ICompanion pet = subject ?? _lastPet;
            if (pet == null || !host.IsCompanionAlive(pet)) return Declined(host, explicitPath, "no companion");

            _lastInteractionUtc = DateTime.UtcNow;
            ScreenContext ctx;
            try { ctx = host.CaptureScreenContext(pet); } catch { ctx = null; }
            if (ctx == null) return Declined(host, explicitPath, "capture failed");

            // A "pondering" cue while the model responds (we are on the UI thread here). It belongs to the pet
            // being asked: PlayAnimationAll + SayAll made EVERY pet ponder a question only one of them was
            // asked, which is the same bug the answer below had.
            try { PlayEmotionOn(host, pet, "thinking"); host.Say(pet, "…"); } catch { }

            Func<ScreenContext, string, bool, ICompanion, Task> sink = AskSinkForDiagnostics;
            _ = sink != null
                ? sink(ctx, ctx.WindowUnderCompanion, allowVision, pet)
                : AskCoreAsync(session, ctx, ctx.WindowUnderCompanion, allowVision, pet);
            return true;
        }

        /// <summary>A refusal on the explicit path (the hotkey, the tray row) is said in the log with its category;
        /// a responder's is not, because the chain falls through to Fortunes and the user hears a fortune (RA-060).
        /// Always false: the turn was not started.</summary>
        private bool Declined(IHost host, bool explicitPath, string reason)
        {
            if (explicitPath) LogDeclined(host, reason);
            return false;
        }

        private void LogDeclined(IHost host, string reason)
        {
            try { host.Log(Info.Id, "ask declined: " + reason); } catch { }
        }

        /// <summary>
        /// Test seam: where a STARTED turn goes instead of <see cref="AskCoreAsync"/>. Null in the shipped
        /// module. The module self-test sets it to record the routing decision each entry point makes, which
        /// pet and whether vision was allowed, WITHOUT the turn reaching the session and a backend: the decision
        /// under test is made before this point, by OnDrop, OnPokeReaction, the tray row and the hotkey, and
        /// everything after it needs a screen, a model and a network. Same shape as
        /// AiSessionManager.ReconfigureAdmittedForDiagnostics. It exists because BUG-010 was a routing
        /// argument that four sentences described and nothing asserted.
        /// </summary>
        internal Func<ScreenContext, string, bool, ICompanion, Task> AskSinkForDiagnostics;

        /// <summary>Play the first animation this pet actually defines for an emotion. The module owns the
        /// emotion -&gt; candidates mapping, so this needs no host verb beyond TryPlayAnimation.</summary>
        private void PlayEmotionOn(IHost host, ICompanion pet, string emotion)
        {
            if (host == null || pet == null) return;
            foreach (string name in EmotionAnimations(emotion))
                if (host.TryPlayAnimation(pet, name)) break;
        }

        private async Task AskCoreAsync(AiSessionManager session, ScreenContext ctx, string petZone, bool allowVision, ICompanion subject)
        {
            BrainResponse r;
            try { r = await session.AskAsync(ctx, petZone, allowVision, _lifetime.Token).ConfigureAwait(false); }
            catch { r = null; }
            if (r == null || string.IsNullOrWhiteSpace(r.Text)) return;

            // Apply on the UI thread: map the emotion to an animation, then speak.
            PostToUi(delegate
            {
                IHost host = _host;
                if (host == null) return;
                // The subject is carried through rather than re-read from _lastPet, which CompanionSpawned,
                // CompanionLanded and CompanionPoked all move -- and a model round trip is easily long enough for that to
                // happen. If the pet that asked is gone, DROP the answer. Handing it to a different pet is the
                // same bug wearing a hat: that pet showed no "…" and was never asked.
                if (!host.IsCompanionAlive(subject))
                {
                    host.Log(Info.Id, "answer dropped: the companion it was for is no longer on screen");
                    return;
                }
                try
                {
                    PlayEmotionOn(host, subject, r.Emotion);
                    host.Say(subject, r.Text);
                }
                catch { }
            });
        }

        // ---- state application (mirrors ApplyAiBrainState + ApplyAiTriggers) ----------------------

        /// <summary>Build/retire the backend and (re)arm the hotkey from the current settings. (The idle loop this
        /// used to name went in 1.2.3, F064.)</summary>
        private void ApplyState()
        {
            AiSettings s = _settings ?? new AiSettings();
            // Publish the user's name so other modules (the fortunes welcome) address them the same when the
            // brain is on; clear it when off so they fall back to their own default (the Windows user name).
            try { if (_host != null) _host.SetOwnerName((s.AiBrainEnabled && !string.IsNullOrWhiteSpace(s.UserName)) ? s.UserName.Trim() : ""); }
            catch { }
            string err = null;
            bool allowed = s.AiBrainEnabled && CanUse(s, out err);
            // The reason the brain is NOT being built, said once per Apply. `err` was computed and dropped, so
            // an invalid endpoint, a missing consent or a missing cloud model disabled the brain with nothing
            // in the log while the tray row still read "Disable AI" (F101, F099).
            if (s.AiBrainEnabled && !allowed && _host != null)
                try { _host.Log(Info.Id, "AI brain not started: " + err); } catch { }
            bool prepare = allowed && (s.AutoStartServer || s.WarmUpDesired);
            // A private copy, taken HERE on the UI thread. The factory below runs on a pool thread once the previous
            // brain has retired (up to ~2 s later), and it used to read the live instance: a pane Save in that
            // window rotated the key or replaced a collection under CreateBrain's read (F068, F100). The copy
            // cannot be saved.
            AiSettings forBrain = s.CloneForBrain();
            // Retire WITHOUT evicting when the replacement targets the same backend and models. Every Apply
            // rebuilds the brain (the persona is read from its settings clone, so a name or disposition edit must
            // reach a NEW brain, and a fingerprint that skipped the rebuild would have to know every field the
            // brain reads), but under "keep" or "server" residency the old brain's retirement also evicted a model
            // the user asked to keep resident, so a hotkey edit cost a cold reload (F095, F065). The fingerprint
            // names every field that decides WHICH model is resident WHERE and nothing else; under "unload" the
            // model is gone after each remark anyway, so the eviction on retire stays (it is free there). The
            // COMPARISON is the session's, made at retire time against the brain it actually has: recorded here,
            // when the Apply was issued, the fingerprint described the brain the session WOULD build, and an Apply
            // cancelled while queued behind an ask left its fingerprint behind for the next one to match (R-012).
            bool keepResident = !string.Equals(s.ModelResidency, AiSettings.ResidencyUnload, StringComparison.OrdinalIgnoreCase);

            // Fire-and-forget: the session serializes generations, so a stale config can never apply.
            _ = _session.ReconfigureForBackendAsync(
                allowed ? (Func<AiBrain>)delegate { return CreateBrain(forBrain); } : null,
                allowed,
                prepare,
                _lifetime.Token,
                allowed ? BackendFingerprint(s) : null,
                keepResident);

            if (_hotkey != null) { try { _hotkey.Dispose(); } catch { } _hotkey = null; }
            if (allowed && s.HotkeyEnabled && _host != null)
                _hotkey = _host.RegisterHotkey(s.Hotkey, delegate { Ask(null, true); });

        }

        // ---- brain construction (mirrors StartUp.CreateBrain / CanUseAiConfiguration) -------------

        /// <summary>
        /// Every setting that decides which model is resident where: the local slot's endpoint, protocol and two
        /// models, the cloud selector with its endpoint and models, the fallback switch, the residency, the
        /// executable path, and whether vision is on. UseVision joined on 2026-09-30 (R-011): it decides which of the
        /// two models the warm-up and every ask load, so under "keep" a vision toggle left the vision model resident
        /// for nothing until shutdown; the cost is one cold reload on that toggle, which is the eviction the residency
        /// otherwise never gets. Persona fields are deliberately absent: the brain is rebuilt on every Apply
        /// regardless, and this decides only whether its retirement EVICTS (F095). Handed to the session, which owns
        /// the live brain's fingerprint and compares at retire time (R-012). Internal so the self-test can pin what
        /// is and is not in it.
        /// </summary>
        internal static string BackendFingerprint(AiSettings s)
        {
            return string.Join("\n", new[]
            {
                s.Endpoint ?? "", s.LocalBackendKind ?? "", s.TextModel ?? "", s.VisionModel ?? "",
                s.Provider ?? "", s.OpenAiBaseUrl ?? "", s.CloudTextModel ?? "", s.CloudVisionModel ?? "",
                s.UseLocalFallback ? "fallback" : "no-fallback", s.ModelResidency ?? "", s.OllamaPath ?? "",
                s.UseVision ? "vision" : "text",
            });
        }

        /// <summary>
        /// The keep_alive the AUDITION brain's Ollama client sends. Under the default "unload" residency every chat
        /// carries keep_alive:0, so five back-to-back samples raced Ollama's eviction and could pay up to four extra
        /// loads, about 5 s each on gemma3:4b against about 400 ms of inference (F070; the repo's one recorded
        /// audition timing, 5498/3803/419/380/385 ms, shows the race being won three times out of five). The
        /// audition is a burst the module itself issues, so it holds the model for a minute between samples and
        /// PreviewDispositionAsync evicts explicitly when the run ends. The other residencies keep their own value:
        /// "keep" is resident anyway and "server" defers to Ollama.
        /// </summary>
        internal static int? AuditionKeepAliveSeconds(AiSettings s)
        {
            return string.Equals(s.ModelResidency, AiSettings.ResidencyUnload, StringComparison.OrdinalIgnoreCase)
                ? (int?)AuditionKeepAliveWindowSeconds
                : s.KeepAliveForRequests;
        }

        private const int AuditionKeepAliveWindowSeconds = 60;

        /// <summary>The live settings, for the module self-test only: it reads the seeded endpoint (RA-072) and
        /// flips the residency between audition presses (RA-073).</summary>
        internal AiSettings SettingsForDiagnostics { get { return _settings; } }

        /// <summary>
        /// Test seam: builds the audition's brain in place of <see cref="CreateBrain(AiSettings, int?)"/>, handed
        /// the settings and the keep_alive the audition decided on. Null in the shipped module. It exists because
        /// the audition's three decisions (the keep_alive window, no warm-up, the eviction at the end) are made in
        /// RunAuditionAsync and were asserted only on the helpers they call, so reverting any of the three call
        /// sites compiled and left both self-test flags green (RA-073). Same shape as AskSinkForDiagnostics.
        /// </summary>
        internal Func<AiSettings, int?, AiBrain> AuditionBrainFactoryForDiagnostics;

        /// <summary>How long the audition waits for its end-of-run eviction before handing the pane its text:
        /// RetireBrainAsync's own bound, for the same reason (RA-056).</summary>
        private static readonly TimeSpan AuditionUnloadBudget = TimeSpan.FromSeconds(2);

        /// <summary>Evict the brain's models within a budget. An unload still pending afterwards is observed and
        /// left to the brain's disposal, which follows at once in the caller's using block and aborts it (RA-056).
        /// Internal so the self-test can drive it against a backend whose unload never completes.</summary>
        internal static async Task UnloadWithinAsync(AiBrain brain, TimeSpan budget, CancellationToken ct)
        {
            using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                bounded.CancelAfter(budget);
                Task unload;
                try { unload = brain.UnloadAsync(bounded.Token); }
                catch { return; }
                Task completed = await Task.WhenAny(unload, Task.Delay(budget, ct)).ConfigureAwait(false);
                if (completed == unload)
                {
                    try { await unload.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch { }
                }
                else
                {
                    AiEndpointPolicy.ObserveTaskFailure(unload);
                }
            }
        }

        /// <summary>
        /// The "Status" row of the pane: whether the brain is off, started, or NOT started and why. CanUse's refusal
        /// (an invalid endpoint, consent missing, a cloud slot with no model: the two F101 gates) had no channel
        /// but the diagnostic log: Save reported success, the tray offered an inert "Ask about my screen" (hidden
        /// now: the row is gated on the session, not on the stored switch) and nothing on the pane said why remarks
        /// never came (R-013). Read from the SETTINGS rather than the session, so it is right the moment the pane
        /// reloads after Apply and needs no wait on the pool thread that builds the brain.
        /// </summary>
        internal static string BrainStatusLine(AiSettings s)
        {
            if (s == null) return "No settings.";
            if (!s.AiBrainEnabled) return "Off. Tick \"Enable AI brain\" and Apply to start it.";
            string why;
            if (!CanUse(s, out why)) return "Not started: " + why;
            return "On.";
        }

        // internal, not private: the self-test builds a cloud-primary brain from settings alone (no network is
        // touched by construction) to assert what the failure line will name as its endpoint (F073).
        internal static AiBrain CreateBrain(AiSettings s)
        {
            return CreateBrain(s, s.KeepAliveForRequests);
        }

        /// <param name="localKeepAliveSeconds">The keep_alive the LOCAL Ollama client puts on each chat request: the
        /// residency's own value for the live brain, a short positive window for the audition brain (F070).</param>
        internal static AiBrain CreateBrain(AiSettings s, int? localKeepAliveSeconds)
        {
            string endpoint = SelectedEndpoint(s);
            string normalized, error;
            if (!AiEndpointPolicy.TryNormalize(endpoint, out normalized, out error))
                throw new InvalidDataException(error);
            if (!AiEndpointPolicy.IsLoopbackEndpoint(normalized) && !s.CloudDataConsent)
                throw new InvalidOperationException("Cloud data consent is required for a non-local AI endpoint.");

            TimeSpan timeout = TimeSpan.FromSeconds(s.TimeoutSeconds);
            // The host(s) the brain will name in its log lines: the endpoint it was built against, plus the
            // local leg when one is wrapped in below. The snapshot the brain receives keeps the LOCAL Endpoint
            // for a cloud-primary brain, so without this every line said "endpoint=localhost" (F073).
            string backendHosts = AiBrain.DescribeEndpoint(normalized);
            // No cloud selected (Provider == "") -> the LOCAL backend (BuildLocalBackend: Ollama-native or a
            // generic OpenAI-compatible /v1 server per LocalBackendKind). A cloud selector -> the OpenAI-
            // compatible backend with the cloud-scoped key; and when "use local as fallback" is on and the
            // local slot is a valid loopback endpoint, wrap it in a FallbackBackend so a retryable cloud
            // failure fails over to the local model. The brain's settings snapshot carries the active
            // (primary) slot's models; the composite maps to the local models on fallback.
            ICompanionBrainBackend backend;
            if (IsLocalSlot(s))
            {
                backend = BuildLocalBackend(s, normalized, timeout, localKeepAliveSeconds);
            }
            else
            {
                ICompanionBrainBackend cloud = new OpenAiCompatBackend(normalized, s.ApiKey, timeout);
                string localNormalized, localError;
                if (s.UseLocalFallback &&
                    AiEndpointPolicy.TryNormalize(s.Endpoint, out localNormalized, out localError) &&
                    AiEndpointPolicy.IsLoopbackEndpoint(localNormalized))
                {
                    ICompanionBrainBackend local = BuildLocalBackend(s, localNormalized, timeout, localKeepAliveSeconds);
                    backend = new FallbackBackend(cloud, local, s.CloudVisionModel, s.TextModel, s.VisionModel);
                    backendHosts += "->" + AiBrain.DescribeEndpoint(localNormalized);
                }
                else
                {
                    backend = cloud;
                }
            }
            AiBrain brain = new AiBrain(backend, s.ActiveSlotSnapshot());
            brain.BackendHostDescription = backendHosts;
            // Substitution (BUG-002: a configured model the backend lacks is replaced by the first usable listing) is a
            // courtesy for a local backend, where the first listing is free. A cloud primary bills every request, so a
            // model the user never chose is not sent there: the ask ends on an advisory naming the host (R-022).
            brain.SubstituteMissingModel = IsLocalSlot(s);
            // Let the brain re-validate its configured model against what the backend actually offers
            // (BUG-002). Uses the SAME listing call the Options pane uses, so the two can never disagree
            // about what is installed. Captures the backend the brain owns, not a fresh one.
            brain.ModelLister = delegate(CancellationToken ct) { return ListBackendModelsAsync(backend, ct); };
            return brain;
        }

        /// <summary>
        /// List what a backend offers, or NULL when that backend cannot enumerate. Null and empty mean different
        /// things downstream: <see cref="AiModelPolicy.ChooseModel"/> treats an unknown inventory as "do not
        /// complain", so a backend that cannot enumerate must not be able to make the brain claim a model is
        /// missing. Through <see cref="IModelLister"/> rather than two type tests: the type tests answered null
        /// for the cloud+local composite, i.e. for every cloud user who left the default fallback on, so a
        /// removed or mistyped cloud id was never re-validated and BUG-002's spoken advisory never fired on the
        /// configuration it was written for (F103).
        /// </summary>
        internal static async Task<IReadOnlyList<ModelListing>> ListBackendModelsAsync(
            ICompanionBrainBackend backend, CancellationToken ct)
        {
            IModelLister lister = backend as IModelLister;
            if (lister == null) return null;
            return await lister.ListModelsAsync(ct).ConfigureAwait(false);
        }

        // internal, not private: the self-test asserts the cloud-slot rule below without a host.
        internal static bool CanUse(AiSettings s, out string error)
        {
            error = null;
            if (s == null) { error = "AI settings are unavailable."; return false; }
            string normalized;
            if (!AiEndpointPolicy.TryNormalize(SelectedEndpoint(s), out normalized, out error)) return false;
            if (!AiEndpointPolicy.IsLoopbackEndpoint(normalized) && !s.CloudDataConsent)
            {
                error = "Approve cloud data sharing before using a non-local AI endpoint.";
                return false;
            }
            // A cloud slot has no model until the user picks one: the dropdown is empty until "Refresh cloud
            // models" runs, so a first-time cloud setup naturally leaves it blank, and the brain used to fill
            // the blank with the LOCAL default "gemma3:4b" and send that to the provider: HTTP 400 on every
            // remark, logged as unreachable, or with the fallback off a silent substitution to whatever the
            // provider listed first, billed to the user's key (F101). Refused here, where ApplyState logs the
            // reason, rather than discovered one remark at a time.
            if (!IsLocalSlot(s))
            {
                if (string.IsNullOrWhiteSpace(s.CloudTextModel))
                {
                    error = "Pick a cloud text model first (Refresh cloud models, then choose one).";
                    return false;
                }
                if (s.UseVision && string.IsNullOrWhiteSpace(s.CloudVisionModel))
                {
                    error = "Pick a cloud vision model first (Refresh cloud models, then choose one), or turn vision off.";
                    return false;
                }
            }
            return true;
        }

        // Schema v2: the LOCAL slot is active (Ollama at Endpoint) when no cloud provider is selected
        // (Provider == ""); any cloud selector (openai/openrouter/custom) makes the cloud slot primary.
        private static bool IsLocalSlot(AiSettings s)
        {
            return s == null || string.IsNullOrEmpty(s.Provider);
        }

        private static string SelectedEndpoint(AiSettings s)
        {
            return IsLocalSlot(s) ? s.Endpoint : s.OpenAiBaseUrl;
        }

        /// <summary>
        /// Build the LOCAL backend for a normalized local endpoint: the native <see cref="OllamaClient"/> (its
        /// lifecycle features — auto-start/warm-up/unload via <see cref="AiSettings.OllamaPath"/> — only make
        /// sense here) when <see cref="AiSettings.LocalBackendKind"/> is Ollama-native, else a generic
        /// <see cref="OpenAiCompatBackend"/> (llama.cpp/LM Studio/other — those lifecycle calls are already
        /// harmless no-ops on that backend) with no key, since local servers don't need one. Shared by
        /// <see cref="TestConnectionAsync"/> and both local-backend sites in <see cref="CreateBrain"/> so the
        /// LOCAL slot is never hardcoded to one protocol.
        /// </summary>
        // The residency dropdown. Labels carry the trade-off so the choice is legible without a help panel;
        // the STORED value is the stable token, so rewording a label cannot invalidate a saved setting.
        private const string ResidencyUnloadLabel = "Unload after each remark (frees VRAM)";
        private const string ResidencyKeepLabel = "Keep loaded while the app runs (fastest)";
        private const string ResidencyServerLabel = "Leave it to Ollama";

        internal static string[] ResidencyLabels()
        {
            return new[] { ResidencyUnloadLabel, ResidencyKeepLabel, ResidencyServerLabel };
        }

        internal static string ResidencyLabel(string stored)
        {
            if (string.Equals(stored, AiSettings.ResidencyKeep, StringComparison.OrdinalIgnoreCase)) return ResidencyKeepLabel;
            if (string.Equals(stored, AiSettings.ResidencyServer, StringComparison.OrdinalIgnoreCase)) return ResidencyServerLabel;
            return ResidencyUnloadLabel;
        }

        // An unrecognised label falls back to the DEFAULT rather than throwing or storing the label text: the
        // pane is the only thing that produces these, so anything else means the schema moved under us.
        internal static string ResidencyFromLabel(string label)
        {
            if (string.Equals(label, ResidencyKeepLabel, StringComparison.Ordinal)) return AiSettings.ResidencyKeep;
            if (string.Equals(label, ResidencyServerLabel, StringComparison.Ordinal)) return AiSettings.ResidencyServer;
            return AiSettings.ResidencyUnload;
        }

        /// <summary>
        /// What is resident in VRAM right now, read from the server rather than asserted.
        ///
        /// Also states the reload cost, which is real and is paid per remark under "unload": better said here than
        /// discovered as lag. This comment used to describe a second thing, "Preload model on launch" pinning
        /// keep_alive to 10 minutes and outliving a short eject window; that setting was folded into the residency
        /// choice in 1.3.0 and the last trace of its 10-minute pin, in OllamaClient.WarmUpAsync, went with F106.
        /// </summary>
        private string VramStatusLine(AiSettings s)
        {
            // No "these two settings fight each other" paragraph any more: there is one setting, and it cannot
            // disagree with itself. What is left is the honest cost of the choice actually made.
            string cost;
            if (string.Equals(s.ModelResidency, AiSettings.ResidencyUnload, StringComparison.OrdinalIgnoreCase))
                // "a second or two" was optimistic by roughly an order of magnitude. Measured on
                // gemma3:4b, the recommended and smallest sensible model: cold load about 5 seconds on
                // the text path and about 11 with an image, against ~60ms and ~400ms warm. A bigger
                // model is worse. Quoting the real range matters because this setting's whole purpose
                // is trading latency for VRAM, and a user cannot make that trade against a wrong number.
                cost = "  The model reloads for each remark, which costs roughly five seconds — ten or more " +
                       "for a vision glance, and longer on a larger model — that is the trade for the VRAM.";
            else if (string.Equals(s.ModelResidency, AiSettings.ResidencyKeep, StringComparison.OrdinalIgnoreCase))
                cost = "  The model stays loaded for the whole session, so remarks are instant and the VRAM is held throughout.";
            else
                cost = "  Ollama decides (documented as 5 minutes, unless OLLAMA_KEEP_ALIVE is set on this machine).";

            if (!string.Equals(s.LocalBackendKind, "ollama", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(s.LocalBackendKind))
                return "This setting is Ollama-only; the selected local backend has no equivalent.";

            // Serve what we last learned, and start a refresh if it has gone stale. Never wait.
            string cached = _vramResident;
            long stamp = Interlocked.Read(ref _vramStampTicks);
            bool stale = stamp == 0 ||
                (DateTime.UtcNow - new DateTime(stamp, DateTimeKind.Utc)).TotalSeconds > VramFreshnessSeconds;
            if (stale) BeginVramProbe(s);
            return (cached ?? "Checking what is resident…") + cost;
        }

        /// <summary>
        /// Refresh <see cref="_vramResident"/> on a pool thread. At most one in flight: a pane that
        /// rebuilds three times in a row must not start three probes, each holding a socket for two
        /// seconds against a server that is not there.
        /// </summary>
        private void BeginVramProbe(AiSettings s)
        {
            if (Interlocked.CompareExchange(ref _vramProbing, 1, 0) != 0) return;
            Task.Run(delegate
            {
                try { _vramResident = ProbeVramResident(s); }
                catch { _vramResident = "Could not ask the server what is resident (it may not be running)."; }
                finally
                {
                    Interlocked.Exchange(ref _vramStampTicks, DateTime.UtcNow.Ticks);
                    Interlocked.Exchange(ref _vramProbing, 0);
                }
            });
        }

        /// <summary>The network half of the VRAM line, with no UI thread anywhere near it.</summary>
        private static string ProbeVramResident(AiSettings s)
        {
            try
            {
                using (var client = new OllamaClient(
                    AiEndpointPolicy.NormalizeOrThrow(
                        string.IsNullOrWhiteSpace(s.Endpoint) ? "http://localhost:11434" : s.Endpoint,
                        "endpoint"),
                    TimeSpan.FromSeconds(2),
                    s.OllamaPath))
                {
                    IReadOnlyList<OllamaClient.RunningModel> running =
                        client.RunningModelsAsync(CancellationToken.None).GetAwaiter().GetResult();
                    if (running == null || running.Count == 0)
                        return "Nothing resident — no model is holding VRAM.";

                    var sb = new StringBuilder();
                    foreach (OllamaClient.RunningModel m in running)
                    {
                        if (sb.Length > 0) sb.Append("; ");
                        sb.Append(m.Name);
                        if (m.VramBytes > 0)
                            sb.Append(" (").Append((m.VramBytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture)).Append(" GB");
                        else sb.Append(" (VRAM unreported");
                        if (m.ExpiresAt.HasValue)
                        {
                            double secs = (m.ExpiresAt.Value - DateTimeOffset.UtcNow).TotalSeconds;
                            sb.Append(", ").Append(secs <= 0
                                ? "evicting now"
                                : "evicts in " + Math.Round(secs) + "s");
                        }
                        sb.Append(')');
                    }
                    return sb.ToString();
                }
            }
            catch
            {
                // Not reachable is a legitimate answer, not an error to explain: the server may simply be off.
                return "Could not ask the server what is resident (it may not be running).";
            }
        }

        // internal, not private: this is the seam where a settings value becomes a live client, and mutation
        // testing showed nothing covered it -- breaking the propagation was SILENT. A correct setting nobody
        // plumbs through is the exact failure this project's rule about source-text checks warns about.
        internal static ICompanionBrainBackend BuildLocalBackend(AiSettings s, string normalizedLocalEndpoint, TimeSpan timeout)
        {
            return BuildLocalBackend(s, normalizedLocalEndpoint, timeout, s.KeepAliveForRequests);
        }

        /// <param name="keepAliveSeconds">What the Ollama client puts on each chat request; the residency's value
        /// for the live brain (the overload above), a short window for the audition brain (F070).</param>
        internal static ICompanionBrainBackend BuildLocalBackend(
            AiSettings s, string normalizedLocalEndpoint, TimeSpan timeout, int? keepAliveSeconds)
        {
            if (string.Equals(s.LocalBackendKind, "openai-compat", StringComparison.OrdinalIgnoreCase))
                return new OpenAiCompatBackend(normalizedLocalEndpoint, "", timeout);
            // keep_alive is an Ollama-native field, so the residency setting only reaches the Ollama client.
            // On an OpenAI-compat server it has no equivalent and is silently not applied, which is why the
            // pane says the setting is Ollama-only rather than appearing to work everywhere.
            return new OllamaClient(normalizedLocalEndpoint, timeout, s.OllamaPath)
            {
                KeepAliveSeconds = keepAliveSeconds,
            };
        }

        /// <summary>
        /// Prioritized candidate animations per emotion; the host plays the first one each pet's XML
        /// defines. Neutral/unknown => no forced animation.
        ///
        /// CHOSEN BY COVERAGE, and the original names are kept FIRST so no companion that already
        /// reacted changes what it plays. The tail is a fallback for the pets that silently did
        /// nothing, because StartUp.PlayAnimationOnAll tries each candidate and then does nothing at
        /// all -- so a list that misses is indistinguishable from a feature that is switched off.
        ///
        /// MEASURED 2026-09-28 across all 54 Companions/*/animations.xml, case-insensitively, the
        /// same comparison FormCompanion.TryPlayAnimation uses:
        ///
        ///     emotion    shipped candidates      reached   with the fallback
        ///     happy      flower, jump, boing       18/54          42/54
        ///     excited    run, jump, boing          35/54          42/54
        ///     sad        sleep1a, sleep2a           8/54          39/54
        ///     thinking   sleep1a                    8/54          40/54
        ///     confused   rotate1a, boing            8/54          43/54
        ///
        /// The old table came from the eSheep-era StartUp code, and 32 of this corpus are converted
        /// shimeji with entirely different names. "thinking" fires on EVERY ask, immediately before
        /// the reply, so on 46 of 54 companions the reaction users were told about never happened.
        /// boing exists on exactly ONE companion; flower on one.
        ///
        /// NOTHING REACHES ALL 54, and that is not a list that can be improved into existence: the
        /// intersection across this corpus is EMPTY, and the four names on 50+ pets are the engine's
        /// reserved lifecycle animations -- fall 53, drag 52, kill 51, sync 50 -- so a "safe" list
        /// built from them would offer to play the dying animation. AgentFlow's PetAnimations.cs
        /// records the same measurement for its own dropdown and reached the same conclusion.
        /// </summary>
        private static string[] EmotionAnimations(string emotion)
        {
            if (string.IsNullOrWhiteSpace(emotion)) return NoAnimation;
            switch (emotion.Trim().ToLowerInvariant())
            {
                case "happy":    return new string[] { "flower", "jump", "boing", "bounce", "run", "walk" };
                case "excited":  return new string[] { "run", "jump", "boing", "dash", "bounce", "walk" };
                case "sad":      return new string[] { "sleep1a", "sleep2a", "sit", "sit_down", "sitwithlegsup", "stand" };
                case "thinking": return new string[] { "sleep1a", "sit", "sit_down", "stand", "turn" };
                case "confused": return new string[] { "rotate1a", "boing", "turn", "tripping", "stand", "walk" };
                default:         return NoAnimation;
            }
        }

        /// <summary>
        /// The engine's reserved lifecycle animations. They are the ONLY names present on nearly
        /// every companion, which makes them the tempting answer to "what is safe to play" and the
        /// wrong one: playing `kill` as a reaction to a cheerful reply is not a coverage win.
        /// </summary>
        internal static readonly string[] ReservedLifecycleAnimations =
            new string[] { "fall", "drag", "kill", "sync", "spawn" };

        /// <summary>The emotions this module maps, for the self-test to iterate rather than restate.</summary>
        internal static readonly string[] MappedEmotions =
            new string[] { "happy", "excited", "sad", "thinking", "confused" };

        /// <summary>Test seam: the candidate list for an emotion, without reaching a live host.</summary>
        internal static string[] EmotionAnimationsForSelfTest(string emotion)
        {
            return EmotionAnimations(emotion);
        }

        /// <summary>One-time, non-destructive migration: if the module has no settings yet but the base
        /// ai-settings.json exists, copy it (including the DPAPI-encrypted keys, decryptable by the same
        /// Windows user) into the module store. The base file is left intact: the host reads it once more for
        /// the random-drop bridge (LocalData.MigrateRandomDropIfAbsent). Any Fortunes-era keys it carries land
        /// in this module's ExtensionData, inert (F093; the base has not read them since the Fortunes module
        /// took its own settings).</summary>
        private static void MigrateFromBaseIfNeeded(string moduleDir)
        {
            try
            {
                string moduleFile = Path.Combine(moduleDir, "ai-settings.json");
                if (File.Exists(moduleFile)) return;

                string baseRoot = Environment.GetEnvironmentVariable("DESKTOP_AI_COMPANION_DATA_ROOT");
                if (string.IsNullOrWhiteSpace(baseRoot))
                    baseRoot = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DesktopAICompanion");
                string baseFile = Path.Combine(baseRoot, "ai-settings.json");
                if (!File.Exists(baseFile)) return;

                File.Copy(baseFile, moduleFile, false);
            }
            catch { }
        }

        private void PostToUi(Action action)
        {
            if (action == null) return;
            SynchronizationContext ui = _ui;
            if (ui != null) ui.Post(delegate { action(); }, null);
            else action();
        }


        /// <summary>
        /// The module's own self-test, for <c>--module-selftest=aibrain</c>.
        ///
        /// A DELEGATION, not a second set of assertions: AiEngineProbe.Run already holds them, and the host's
        /// dedicated flag has run it for a long time. What was missing was the entry point ON THE MODULE
        /// TYPE, which is what the convention looks for -- so this module reported "the module exposes
        /// static bool SelfTest(out string detail)" and stopped after four generic checks, while its real
        /// assertions ran only under the other flag.
        ///
        /// Safe to name this SelfTest now. It used not to be: the harness once took the FIRST
        /// <c>bool SelfTest(out string)</c> anywhere in the assembly, so a helper could beat the module's
        /// own entry point (Reminder had six). That was fixed -- ModuleConventionSelfTest resolves it
        /// against the module TYPE -- and the comments elsewhere warning about it are stale.
        /// </summary>
        public static bool SelfTest(out string detail)
        {
            return AiEngineProbe.Run(out detail);
        }
        public void Shutdown()
        {
            IHost host = _host;
            if (host != null)
            {
                host.CompanionSpawned -= OnPetSeen;
                host.CompanionLanded -= OnPetSeen;
                if (_fullscreenChanged != null)
                {
                    try { host.FullscreenChanged -= _fullscreenChanged; } catch { }
                    _fullscreenChanged = null;
                }
                host.CompanionPoked -= OnPetPoked;
            }
            if (_dropResponder != null) { try { _dropResponder.Dispose(); } catch { } _dropResponder = null; }
            if (_pokeResponder != null) { try { _pokeResponder.Dispose(); } catch { } _pokeResponder = null; }
            if (_hotkey != null) { try { _hotkey.Dispose(); } catch { } _hotkey = null; }
            try { _lifetime.Cancel(); _lifetime.Dispose(); } catch { }
            try { _session.Dispose(); } catch { }
            // The sink is a STATIC field holding a delegate over this instance. Leaving it set would keep
            // the module alive past unload and route lines at a nulled host, so it is cleared here. The
            // delegate re-reads _host each call, so an in-flight line during teardown is a no-op, not a
            // throw.
            AiBrain.LogSink = null;
            _host = null;
        }
    }
}
