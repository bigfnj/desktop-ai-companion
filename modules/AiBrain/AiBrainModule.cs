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
    /// The AI-brain module (S4): the optional, off-by-default screen-commentary LLM, now LIVE (S4b). It owns
    /// the "ask about my screen" flow — a global hotkey, the arbitrated periodic drop (outranking Fortunes),
    /// and an opt-in idle-commentary loop — plus the emotion->animation reaction, all through host services
    /// (CaptureScreenContext, RegisterHotkey, RegisterDropResponder, SayAll, PlayAnimationAll). The brain
    /// lifecycle (generation/supersede, prepare/retire) is the relocated AiSessionManager; settings + chat
    /// history are the module's own DPAPI-scoped store. It is OFF by default (its own AiBrainEnabled), so a
    /// fresh install does nothing until enabled. There is no tray/Options UI yet (accept-the-gap): the
    /// enable + config UI is rebuilt from module contributions in S5; until then it reads its settings file.
    /// </summary>
    public sealed class AiBrainModule : IModule
    {
        private IHost _host;
        private SynchronizationContext _ui;                 // captured on the UI thread in Init
        private readonly AiSessionManager _session = new AiSessionManager();
        private AiSettings _settings;
        private CancellationTokenSource _lifetime = new CancellationTokenSource();
        private int _generation;
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
        private SettingField _textModelField;
        private SettingField _visionModelField;
        private SettingField _cloudTextModelField;
        private SettingField _cloudVisionModelField;

        private static readonly string[] NoAnimation = new string[0];

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "aibrain",
            Name = "AI Brain",
            Version = "1.0.0",   // 1.0.0: rebased with the host for the Desktop AI Companion rename. Not a
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
            MinHostVersion = "1.0.0",
            Permissions = ModulePermissions.Speech | ModulePermissions.Animation |
                          ModulePermissions.ScreenContext | ModulePermissions.Network |
                          ModulePermissions.Hotkey | ModulePermissions.Storage,
        };

        public void Init(IHost host)
        {
            _host = host;
            _ui = SynchronizationContext.Current;   // the WinForms UI-thread context (host loads modules there)

            try
            {
                IModuleStorage storage = host.GetStorage("aibrain");
                if (storage != null && !string.IsNullOrEmpty(storage.DataDirectory))
                {
                    AiPaths.SetRoot(storage.DataDirectory);
                    MigrateFromBaseIfNeeded(storage.DataDirectory);   // one-time, non-destructive
                }
                _settings = AiSettings.Load();
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
                    Visible = delegate { return _settings != null && _settings.AiBrainEnabled; },
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
                    new SettingField { Id = "petName", Label = "Pet name", Kind = SettingKind.Text, Group = "Persona" },
                    new SettingField { Id = "userName", Label = "Your name (optional)", Kind = SettingKind.Text, Group = "Persona" },
                    new SettingField { Id = "disposition", Label = "Disposition", Kind = SettingKind.Enum, Options = DispositionNames(), Group = "Persona" },
                    // Local provider (always available; defaults to Ollama but can instead speak the
                    // generic OpenAI-compatible /v1 protocol for llama.cpp/LM Studio/other local servers).
                    new SettingField { Id = "localBackendKind", Label = "Local backend", Kind = SettingKind.Enum, Options = LocalBackendKindLabels(), Group = "Local provider" },
                    new SettingField { Id = "endpoint", Label = "Local endpoint (base URL)", Kind = SettingKind.Text, Group = "Local provider" },
                    _textModelField,
                    _visionModelField,
                    new SettingField { Id = "useVision", Label = "Use vision on explicit asks", Kind = SettingKind.Bool, Group = "Local provider" },
                    // Screen reading uses this OCR engine on the fast path (vision is explicit-asks only).
                    // Empty = search the usual install locations, then PATH.
                    new SettingField { Id = "tesseractPath", Label = "OCR engine (blank = auto-detect)", Kind = SettingKind.Text, Group = "Screen reading" },
                    new SettingField { Id = "autoStart", Label = "Start Ollama automatically", Kind = SettingKind.Bool, Group = "Local server (Ollama only)" },
                    // ONE choice, not a "preload" switch plus an eject window that could contradict it.
                    // Defaults to unloading: the module holds VRAM only for a remark it has already made.
                    new SettingField
                    {
                        Id = "standDownFullscreen",
                        Label = "Stand down while a fullscreen app is running (releases VRAM; fortunes speak instead)",
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
                    // Fallback (persisted here; the runtime fallback backend is a later change).
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
                    new PaneAction { Label = "Refresh local models", InvokeAsync = RefreshLocalModelsAsync, Group = "Local provider", ReloadPaneAfter = true },
                    new PaneAction { Label = "Test connection", InvokeAsync = TestConnectionAsync, Group = "Cloud provider" },
                    new PaneAction { Label = "Refresh cloud models", InvokeAsync = RefreshCloudModelsAsync, Group = "Cloud provider", ReloadPaneAfter = true },
                    new PaneAction { Label = "Choose OCR engine…", InvokeAsync = ChooseOcrEngineAsync, Group = "Screen reading", ReloadPaneAfter = true },
                    new PaneAction { Label = "Get Tesseract…", InvokeAsync = GetTesseractAsync, Group = "Screen reading" },
                    new PaneAction { Label = "Test OCR", InvokeAsync = TestOcrAsync, Group = "Screen reading" },
                },
            });

            ApplyState();
        }

        /// <summary>Test-connection action for the WPF pane: build a backend from the current settings, probe
        /// availability + a tiny chat, and report a status line. Async so the pane stays responsive.</summary>
        private async Task<string> TestConnectionAsync()
        {
            AiSettings s = _settings;
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
                    string activeModel = local ? s.TextModel : s.CloudTextModel;
                    string model = string.IsNullOrWhiteSpace(activeModel) ? "llama3.1:8b" : activeModel.Trim();
                    var msgs = new List<ChatMessage> { ChatMessage.System("Reply with OK."), ChatMessage.User("OK?", null) };
                    await backend.ChatAsync(model, msgs, false, CancellationToken.None).ConfigureAwait(false);
                    sw.Stop();
                    return "✓ connected · " + model + " OK " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s";
                }
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
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
                if (!s.Save()) return "✗ Couldn't save the OCR engine path.";
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
        /// missing/broken engine surfaces as a red status instead of silently making remarks screen-blind.</summary>
        private async Task<string> TestOcrAsync()
        {
            AiSettings s = _settings ?? new AiSettings();
            try
            {
                using (var probe = new AiBrain(null, s))
                    return await probe.SelfTestOcrAsync(CancellationToken.None).ConfigureAwait(false);
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
                d["petName"] = s.PetName ?? "";
                d["userName"] = s.UserName ?? "";
                d["disposition"] = DispositionNameForId(s.Disposition);
                // Local provider slot (always available; Ollama-native by default, or a generic
                // OpenAI-compatible /v1 server such as llama.cpp/LM Studio).
                d["localBackendKind"] = LocalBackendKindLabelForId(s.LocalBackendKind);
                d["endpoint"] = s.Endpoint ?? "";
                d["textModel"] = FormatModelLabel(s.TextModel, _localModels);
                d["visionModel"] = FormatModelLabel(s.VisionModel, _localModels);
                d["useVision"] = s.UseVision ? "true" : "false";
                d["tesseractPath"] = s.TesseractPath ?? "";
                d["autoStart"] = s.AutoStartServer ? "true" : "false";
                d["residency"] = ResidencyLabel(s.ModelResidency);
                d["standDownFullscreen"] = s.StandDownForFullscreen ? "true" : "false";
                d["vramStatus"] = VramStatusLine(s);
                // Cloud provider slot.
                d["cloudProvider"] = CloudProviderLabelForId(s.Provider);
                d["cloudEndpoint"] = s.OpenAiBaseUrl ?? "";
                d["cloudTextModel"] = FormatModelLabel(s.CloudTextModel, _cloudModels);
                d["cloudVisionModel"] = FormatModelLabel(s.CloudVisionModel, _cloudModels);
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
            string v;
            bool b;
            if (values.TryGetValue("enabled", out v) && bool.TryParse(v, out b)) s.AiBrainEnabled = b;
            if (values.TryGetValue("petName", out v)) s.PetName = (v ?? "").Trim();
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
            // endpoint (set just above), so it must run after the provider/endpoint fields. Best-effort.
            if (values.TryGetValue("apiKey", out v) && !string.IsNullOrEmpty(v)) { string err; s.TrySetApiKey(v, out err); }
            // ---- Fallback + triggers ----
            if (values.TryGetValue("useLocalFallback", out v) && bool.TryParse(v, out b)) s.UseLocalFallback = b;
            if (values.TryGetValue("hotkey", out v) && !string.IsNullOrWhiteSpace(v)) s.Hotkey = v.Trim();
            bool ok = s.Save();
            ApplyState();   // re-apply triggers/backend to reflect the new config
            return ok;
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
            _modelIdByLabel[label] = id;
            return label;
        }

        // Look up a label the pane returned back to id (registered by FormatModelLabel). A label that was
        // never produced by FormatModelLabel this session (shouldn't normally happen — Load always runs
        // before Save) falls back to treating it as a raw id, so a save is never silently lost.
        private string ResolveModelId(string label)
        {
            string id;
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
        /// <paramref name="visionOnly"/> — real capability from the backend when it reported one, else the
        /// <see cref="AiModelPolicy.LooksVisionCapable"/> heuristic), tagged/sorted so uncensored-leaning
        /// models (<see cref="AiModelPolicy.LooksUncensored"/> — an advisory for personas that need a model
        /// to actually comply, e.g. Samuel/Triumph; never a hard filter) come first, each labeled with its
        /// known size (see <see cref="FormatModelLabel"/>) when available. SAFETY INVARIANT: the
        /// currently-saved value is always unioned in (labeled the same way), even if the fetch hasn't run
        /// yet, came back empty, or doesn't cover it — the pane's Enum dropdown is a closed, non-editable
        /// ComboBox (see PaneView.Build), so a value missing from Options would show nothing selected and a
        /// save would silently blank the field.
        /// </summary>
        private string[] BuildModelOptions(IReadOnlyList<ModelListing> models, string currentValue, bool visionOnly)
        {
            var uncensoredLabels = new List<string>();
            var otherLabels = new List<string>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (models != null)
                foreach (ModelListing model in models)
                {
                    if (model == null || string.IsNullOrEmpty(model.Id) || !seenIds.Add(model.Id)) continue;
                    bool isVision = model.Vision ?? AiModelPolicy.LooksVisionCapable(model.Id);
                    if (visionOnly && !isVision) continue;
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
        /// Rebuild the four model dropdowns' Options IN PLACE on the retained SettingField objects (the only
        /// way a refresh becomes visible to the pane — Schema itself is never rebuilt; PaneView re-reads
        /// Options fresh on each rebuild triggered by a PaneAction's ReloadPaneAfter). Called once at Init
        /// (the caches start empty, so this just seeds the current-value safety net) and again at the end of
        /// each "Refresh ... models" action.
        /// </summary>
        private void RefreshModelFieldOptions()
        {
            AiSettings s = _settings;
            _textModelField.Options = BuildModelOptions(_localModels, s != null ? s.TextModel : "", false);
            _visionModelField.Options = BuildModelOptions(_localModels, s != null ? s.VisionModel : "", true);
            _cloudTextModelField.Options = BuildModelOptions(_cloudModels, s != null ? s.CloudTextModel : "", false);
            _cloudVisionModelField.Options = BuildModelOptions(_cloudModels, s != null ? s.CloudVisionModel : "", true);
        }

        private static int CountUncensored(IReadOnlyList<ModelListing> models)
        {
            int count = 0;
            if (models != null)
                foreach (ModelListing m in models)
                    if (m != null && AiModelPolicy.LooksUncensored(m.Id)) count++;
            return count;
        }

        private static string ModelListStatus(IReadOnlyList<ModelListing> models, string unreachableTarget)
        {
            if (models == null || models.Count == 0) return "✗ No models found at " + unreachableTarget;
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
                using (ICompanionBrainBackend backend = BuildLocalBackend(s, normalized, timeout))
                {
                    OllamaClient ollama = backend as OllamaClient;
                    OpenAiCompatBackend compat = backend as OpenAiCompatBackend;
                    if (ollama != null)
                        models = await ollama.ListModelsAsync(CancellationToken.None).ConfigureAwait(false);
                    else if (compat != null)
                        models = await compat.ListModelsAsync(CancellationToken.None).ConfigureAwait(false);
                    else
                        models = new List<ModelListing>();
                }
                _localModels.Clear();
                _localModels.AddRange(models);
                RefreshModelFieldOptions();
                return ModelListStatus(models, normalized);
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
                using (var backend = new OpenAiCompatBackend(normalized, s.ApiKey, timeout))
                    models = await backend.ListModelsAsync(CancellationToken.None).ConfigureAwait(false);
                _cloudModels.Clear();
                _cloudModels.AddRange(models);
                RefreshModelFieldOptions();
                return ModelListStatus(models, normalized);
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        /// <summary>Tray toggle: flip the module's own AiBrainEnabled, persist, and (re)build the brain.</summary>
        private void ToggleEnabled()
        {
            AiSettings s = _settings;
            if (s == null) return;
            s.AiBrainEnabled = !s.AiBrainEnabled;
            try { s.Save(); } catch { }
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
            Ask(pet, true);
            return true;
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
            Ask(pet, false);
            return true;
        }

        // ---- the ask flow (mirrors the old StartUp.AskAboutScreen) --------------------------------

        /// <summary>
        /// Kick off one screen-commentary turn for a specific pet. Call on the UI thread (drop/poke/hotkey/
        /// idle tick). <paramref name="subject"/> is the pet this turn belongs to; the hotkey and the idle
        /// loop have no natural one, so they pass null and fall back to the last pet seen.
        /// </summary>
        private void Ask(ICompanion subject, bool allowVision)
        {
            IHost host = _host;
            AiSessionManager session = _session;
            if (host == null || !session.Enabled || !host.SpeechEnabled) return;
            // One in-flight ask at a time, so at most one pending subject. Per-pet concurrency (two pets
            // asked at once) is BACKLOG #16(a) and deliberately not attempted here.
            if (session.RequestInProgress) return;
            ICompanion pet = subject ?? _lastPet;
            if (pet == null || !host.IsCompanionAlive(pet)) return;

            _lastInteractionUtc = DateTime.UtcNow;
            ScreenContext ctx;
            try { ctx = host.CaptureScreenContext(pet); } catch { ctx = null; }
            if (ctx == null) return;

            // A "pondering" cue while the model responds (we are on the UI thread here). It belongs to the pet
            // being asked: PlayAnimationAll + SayAll made EVERY pet ponder a question only one of them was
            // asked, which is the same bug the answer below had.
            try { PlayEmotionOn(host, pet, "thinking"); host.Say(pet, "…"); } catch { }

            _ = AskCoreAsync(session, ctx, ctx.WindowUnderCompanion, allowVision, pet);
        }

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
                    host.Log(Info.Id, "answer dropped: the pet it was for is no longer on screen");
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

        /// <summary>Build/retire the backend and (re)arm the hotkey + idle loop from the current settings.</summary>
        private void ApplyState()
        {
            AiSettings s = _settings ?? new AiSettings();
            // Publish the user's name so other modules (the fortunes welcome) address them the same when the
            // brain is on; clear it when off so they fall back to their own default (the Windows user name).
            try { if (_host != null) _host.SetOwnerName((s.AiBrainEnabled && !string.IsNullOrWhiteSpace(s.UserName)) ? s.UserName.Trim() : ""); }
            catch { }
            string err;
            bool allowed = s.AiBrainEnabled && CanUse(s, out err);
            int gen = ++_generation;
            bool prepare = allowed && (s.AutoStartServer || s.WarmUpDesired);
            AiSettings snapshot = s;

            // Fire-and-forget: the session serializes generations, so a stale config can never apply.
            _ = _session.ReconfigureAsync(
                allowed ? (Func<AiBrain>)delegate { return CreateBrain(snapshot); } : null,
                allowed,
                prepare,
                _lifetime.Token);

            if (_hotkey != null) { try { _hotkey.Dispose(); } catch { } _hotkey = null; }
            if (allowed && s.HotkeyEnabled && _host != null)
                _hotkey = _host.RegisterHotkey(s.Hotkey, delegate { Ask(null, true); });

        }

        // ---- brain construction (mirrors StartUp.CreateBrain / CanUseAiConfiguration) -------------

        private static AiBrain CreateBrain(AiSettings s)
        {
            string endpoint = SelectedEndpoint(s);
            string normalized, error;
            if (!AiEndpointPolicy.TryNormalize(endpoint, out normalized, out error))
                throw new InvalidDataException(error);
            if (!AiEndpointPolicy.IsLoopbackEndpoint(normalized) && !s.CloudDataConsent)
                throw new InvalidOperationException("Cloud data consent is required for a non-local AI endpoint.");

            TimeSpan timeout = TimeSpan.FromSeconds(s.TimeoutSeconds);
            // No cloud selected (Provider == "") -> the LOCAL backend (BuildLocalBackend: Ollama-native or a
            // generic OpenAI-compatible /v1 server per LocalBackendKind). A cloud selector -> the OpenAI-
            // compatible backend with the cloud-scoped key; and when "use local as fallback" is on and the
            // local slot is a valid loopback endpoint, wrap it in a FallbackBackend so a retryable cloud
            // failure fails over to the local model. The brain's settings snapshot carries the active
            // (primary) slot's models; the composite maps to the local models on fallback.
            ICompanionBrainBackend backend;
            if (IsLocalSlot(s))
            {
                backend = BuildLocalBackend(s, normalized, timeout);
            }
            else
            {
                ICompanionBrainBackend cloud = new OpenAiCompatBackend(normalized, s.ApiKey, timeout);
                string localNormalized, localError;
                if (s.UseLocalFallback &&
                    AiEndpointPolicy.TryNormalize(s.Endpoint, out localNormalized, out localError) &&
                    AiEndpointPolicy.IsLoopbackEndpoint(localNormalized))
                {
                    ICompanionBrainBackend local = BuildLocalBackend(s, localNormalized, timeout);
                    backend = new FallbackBackend(cloud, local, s.CloudVisionModel, s.TextModel, s.VisionModel);
                }
                else
                {
                    backend = cloud;
                }
            }
            return new AiBrain(backend, s.ActiveSlotSnapshot());
        }

        private static bool CanUse(AiSettings s, out string error)
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
        /// Also states the two things that would otherwise make the eject setting look broken:
        ///   * "Preload model on launch" pins keep_alive to 10 minutes, so a warmed model OUTLIVES a short
        ///     eject setting until the next remark re-stamps it. Two settings that appear to contradict each
        ///     other, with no explanation, is a support question waiting to happen.
        ///   * the reload cost is real and is paid per remark. Better said here than discovered as lag.
        /// </summary>
        private static string VramStatusLine(AiSettings s)
        {
            // No "these two settings fight each other" paragraph any more: there is one setting, and it cannot
            // disagree with itself. What is left is the honest cost of the choice actually made.
            string cost;
            if (string.Equals(s.ModelResidency, AiSettings.ResidencyUnload, StringComparison.OrdinalIgnoreCase))
                cost = "  The model reloads for each remark, which costs a second or two — that is the trade for the VRAM.";
            else if (string.Equals(s.ModelResidency, AiSettings.ResidencyKeep, StringComparison.OrdinalIgnoreCase))
                cost = "  The model stays loaded for the whole session, so remarks are instant and the VRAM is held throughout.";
            else
                cost = "  Ollama decides (documented as 5 minutes, unless OLLAMA_KEEP_ALIVE is set on this machine).";

            if (!string.Equals(s.LocalBackendKind, "ollama", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(s.LocalBackendKind))
                return "This setting is Ollama-only; the selected local backend has no equivalent.";

            try
            {
                using (var client = new OllamaClient(
                    AiEndpointPolicy.NormalizeOrThrow(
                        string.IsNullOrWhiteSpace(s.Endpoint) ? "http://localhost:11434" : s.Endpoint,
                        "endpoint"),
                    TimeSpan.FromSeconds(2),
                    s.OllamaPath))
                {
                    // Synchronous wait on a 2s-deadline probe: this runs while the options pane is being
                    // built, and a pane that cannot answer must render anyway.
                    IReadOnlyList<OllamaClient.RunningModel> running =
                        client.RunningModelsAsync(CancellationToken.None).GetAwaiter().GetResult();
                    if (running == null || running.Count == 0)
                        return "Nothing resident — no model is holding VRAM." + cost;

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
                    return sb.ToString() + cost;
                }
            }
            catch
            {
                // Not reachable is a legitimate answer, not an error to explain: the server may simply be off.
                return "Could not ask the server what is resident (it may not be running)." + cost;
            }
        }

        // internal, not private: this is the seam where a settings value becomes a live client, and mutation
        // testing showed nothing covered it -- breaking the propagation was SILENT. A correct setting nobody
        // plumbs through is the exact failure this project's rule about source-text checks warns about.
        internal static ICompanionBrainBackend BuildLocalBackend(AiSettings s, string normalizedLocalEndpoint, TimeSpan timeout)
        {
            if (string.Equals(s.LocalBackendKind, "openai-compat", StringComparison.OrdinalIgnoreCase))
                return new OpenAiCompatBackend(normalizedLocalEndpoint, "", timeout);
            // keep_alive is an Ollama-native field, so the residency setting only reaches the Ollama client.
            // On an OpenAI-compat server it has no equivalent and is silently not applied, which is why the
            // pane says the setting is Ollama-only rather than appearing to work everywhere.
            return new OllamaClient(normalizedLocalEndpoint, timeout, s.OllamaPath)
            {
                KeepAliveSeconds = s.KeepAliveForRequests,
            };
        }

        /// <summary>Prioritized candidate animations per emotion (data lifted from the old StartUp table);
        /// the host plays the first one each pet's XML defines. Neutral/unknown => no forced animation.</summary>
        private static string[] EmotionAnimations(string emotion)
        {
            if (string.IsNullOrWhiteSpace(emotion)) return NoAnimation;
            switch (emotion.Trim().ToLowerInvariant())
            {
                case "happy":    return new string[] { "flower", "jump", "boing" };
                case "excited":  return new string[] { "run", "jump", "boing" };
                case "sad":      return new string[] { "sleep1a", "sleep2a" };
                case "thinking": return new string[] { "sleep1a" };
                case "confused": return new string[] { "rotate1a", "boing" };
                default:         return NoAnimation;
            }
        }

        /// <summary>One-time, non-destructive migration: if the module has no settings yet but the base
        /// ai-settings.json exists, copy it (including the DPAPI-encrypted keys, decryptable by the same
        /// Windows user) into the module store. The base file is left intact — the base still reads its
        /// fortune fields from it, and the copied fortune fields are simply unused by this module.</summary>
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
            _host = null;
        }
    }
}
