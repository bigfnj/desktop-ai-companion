# Issues and pull requests before v1.0.0

Preserved from the repository that preceded **Desktop AI Companion** v1.0.0, which was deleted
when the project moved to a new repository with a fresh history. A fork does not inherit issues,
so every one of these belongs to this project rather than to the upstream it was forked from.

Kept because several record *why an approach was abandoned*, which nothing in the code says.
Cross-references between them, and to commits, no longer resolve.

| | |
|---|---|
| Pull requests | 89 |
| Issues | 0 |
| Highest number | #89 |

---

### PR #1 — Migrate .NET Framework 4.8 -> .NET 10 (LTS) [Stream 1]

`closed` · opened 2026-08-06

## Migrate .NET Framework 4.8 → .NET 10 (LTS)

Stream 1 of the plugin-framework pivot: port the monolith to .NET 10 at **behavior parity**, one axis at a time, before any re-architecture. Framework-dependent (the apphost prompts for the .NET Desktop runtime if missing). The pet, tray, Options (incl. the WebView2 Fortunes tab), downloads, AI, and run-at-startup all behave identically; this is a runtime/build/packaging change, not a behavior change.

### Phases (each committed, gated green)
- **M1** — SDK-style `net10.0-windows` csproj; DPI via `SetHighDpiMode`; `app.config` retired; DPAPI/`MutexAcl`/`SupportedOSPlatform`/`StatusCode` fixes. NU1510 confirmed ConfigurationManager/ProtectedData/System.Drawing are in-box on net10-windows (no out-of-band packages added).
- **M2** — `build.ps1` on `dotnet`; RID pinned (`win-x64`, still FDD) to flatten native assets; `runtime-files.txt` reconciled to the multi-file FDD payload; deterministic ZIP; flat-only payload proven from the extracted ZIP.
- **M3** — WiX drops the .NET 4.8 launch gate; deterministic ICE-clean MSI.
- **M4** — CoreTests → net10; the two PowerShell `LoadFrom` reflection harnesses moved **in-process** as `--pettyperegistry-selftest` / `--hardening-selftest` (no PowerShell hosts a net10 assembly); the source-text invariant checks stay as a plain PS script; `build.yml` drops `setup-msbuild`, builds via `dotnet`, runs the new flags.
- **M5** — version bump to 1.1.0.

### Verified locally
`build.ps1 -Release -Zip` (manifest OK, deterministic ZIP) · CoreTests 23 groups · all 11 self-test flags · source-invariant script 5/5 · resource-churn soak PASS (pet spawn + Options open/close incl. WebView Fortunes) · deterministic ICE-clean MSI · WinForms GUI launches; native ONNX + WebView2 + DPAPI all work on net10.

This PR exists to validate the above on the windows-2025 runner before it reaches `master`.

### PR #2 — S1: plugin-host foundation (contracts ABI + loader + live CompanionHost)

`closed` · opened 2026-08-06

## S1 — plugin-host foundation (Stream 2)

First phase of the plugin re-architecture: the contract ABI, the module loader, and the live host bridge. **No capability is moved yet** — existing fortunes/AI/sound stay exactly as they are; the host just starts raising lifecycle events into whatever modules are loaded. This is the base S2–S7 build on.

### What's here
- **S1a `DesktopPet.Contracts`** — the stable, dependency-free plugin ABI (AssemblyVersion 1.0.0 = the ABI version): `IModule`; `IHost` with lifecycle events (`CompanionSpawned`/`CompanionPoked`/`CompanionLanded`/`CompanionIdle`/`AnimationStarted`/`HostShutdown`), services (`Say`/`SayAll`, `TryPlayAnimation`, `CaptureScreenContext`, `RegisterHotkey`, per-module `Storage`/`Settings`, priority-arbitrated `RegisterDropResponder`), and contributions (`AddTrayItems`, declarative-schema `AddOptionsPane`); `ICompanion` handle; `PixelRect`/`ScreenContext` value types (no WinForms/WPF/System.Drawing leakage). Ships beside the exe.
- **S1b `ModuleHost`** — loads module DLLs from `<baseDir>\modules\<id>\`, each in its own collectible `AssemblyLoadContext` that shares `DesktopPet.Contracts` from the default context so `IModule`/`IHost` types unify. A bad module is isolated (logged + skipped); shutdown unsubscribes + unloads. A real external test-module DLL + `--module-host-selftest` prove the pipeline end-to-end.
- **S1c `CompanionHost`** — the live `IHost`: services delegate to `StartUp`/`FormCompanion`/`Program.MyData`; `StartUp` raises the events at the existing hook points (spawn/poke/land/shutdown); contributions are collected for the WPF-shell renderer (S5).

### Deferred to consuming phases (not gaps)
Tray/options rendering of contributions (S5); `CompanionIdle`/`AnimationStarted` raises (S2/S4); the real global-hotkey registrar (S4).

### Verified
Contracts compile clean; `build.ps1 -Release` OK; `--module-host-selftest` 6/6; security/smart/hardening self-tests pass; and the resource-churn soak PASS (6 live-app cycles with `CompanionHost` + the test module loaded, `error=null`) — zero regression.

### PR #3 — S2: extract the Sound module (NAudio leaves the base)

`closed` · opened 2026-08-06

## S2 — extract the Sound module (Stream 2)

The first real capability moved out of the base into an isolated .NET 10 plugin. **No pet-visible behavior changes** when the module is present; the base just stops owning audio. Without the module a pet is simply silent.

### The shape (a proper plugin, not a bolt-on)
- **The seam is the published ABI event**, not a sound-special hook. The base raises `AnimationStarted(AnimationInfo{…, SoundData, SoundLoop})` through `IHost` — the same lifecycle mechanism any third-party module uses. Someone could write an alternative audio module against the identical contract.
- **The base is NAudio-free.** NAudio is removed from the csproj, the payload manifest, and the base lock file. The base parses `<sound>`, does the probability roll, and carries the selected raw MP3 bytes; it opens no audio device and knows nothing about codecs.
- **The module owns audio in its own load context.** `modules/Sound` references only the ABI (shared from the default context) + NAudio (its own dependency, copied beside `Sound.dll` with a `deps.json` so the module's `AssemblyLoadContext` resolves it). It decodes + plays via `WaveOutEvent` at `host.Volume`, caches one replayable sound per MP3 byte[], disposes all on Shutdown, and swallows device/decoder errors so a bad/absent audio device never disturbs the host.

### Contract change (additive)
`AnimationInfo` gains `SoundData` + `SoundLoop`. `Pet` is `null` on the engine-raised sound path — the shared per-type `Animations` engine has no per-pet identity and sound is global. Documented in the ABI and backlogged (real per-pet identity is what S4's AI reactions will want).

### Deferred to later phases (not gaps)
Bundling first-party modules into the ZIP/MSI installer payload is **S6**; for now modules build into the runtime `modules\<id>\` folders (local run + self-tests), not the root payload manifest. A host→options module-health/status channel (the old audio-error label) arrives with the **S5** WPF shell.

### Backlog captured (`modules/Sound/BACKLOG.md`)
- **"Now playing" integration** — have the pet announce the current song + artist from **Spotify** / **YouTube Music** (favor the WinRT `GlobalSystemMediaTransportControlsSession` route first — it covers any media app with no per-service OAuth; would add `ModulePermissions.Network` for the Spotify Web API path).
- Real per-pet `AnimationStarted` identity (for S4).

### Verified
`build.ps1 -Release` clean (base NAudio-free; runtime manifest matches; both modules build); `--sound-selftest` PASS incl. **"NAudio decodes a real MP3 inside the module's load context"** (proves ALC isolation — zero NAudio in the base); `--module-host-selftest` (both modules load); `--security-selftest` (new base header-check + raw-bytes-carried tests) + `--hardening`/`--pettyperegistry`/`--smart` all PASS; and the resource-churn soak PASS (6/6 cycles, `error=null`) on the live app with the Sound module loaded.

### PR #4 — S3 (part 1): Fortunes module boundary + personalized welcome starter

`closed` · opened 2026-08-06

## S3 (part 1) — Fortunes module: boundary + personalized welcome starter

The first, self-contained slice of the Fortunes extraction. **Additive and no-op toward the base** — the base still owns fortunes (land/poke/drop) unchanged, so there's zero regression. What lands here is the module boundary plus a delightful personalized starter.

### Design (locked in): ship the engine, not the content
The Fortunes module ships the fortune **framework** (dumb + smart) and **zero bundled fortune content**. A fresh install greets you by name and is otherwise silent until you add a pack. The ~486KB embedded corpus will become the importable/downloadable "starter pack" (not shipped in the module); the bge-small ONNX model is *engine* and will travel with the module like NAudio did for Sound. Recorded in `modules/Fortunes/BACKLOG.md`.

### What's here
- **S3a — boundary:** `modules/Fortunes` (id `fortunes`), references only the ABI, wired into the build. No-op at runtime.
- **S3b — personalized welcome starter:** on the first pet spawn of the session the module speaks a sheep-themed welcome line with the **Windows username** substituted in (`Environment.UserName`, fallback `"friend"`) — the "landing quote" tailored to whoever's logged in. The 116-line corpus (`welcome.json`, adapted from the ai-platform DeskPet welcome quips) is embedded and parsed with `System.Text.Json` **inside the module's own load context**. Once per session; unsubscribes on shutdown; never throws into the host. (welcome-on-spawn doesn't collide with the base's land/poke fortunes.)

### Verified (locally; GH Actions is globally down)
`build.ps1 -Release` clean (base + 3 modules); `--fortunes-selftest` PASS (corpus of 116 parsed in the module ALC; personalized welcome contains the user name with no leftover `{name}`; fires once; unsubscribes) — e.g. it spoke *"I have SO much to not tell you, &lt;user&gt;. Welcome."*; `--module-host-selftest` + `--sound-selftest` PASS (no regression); resource-churn soak PASS (fresh run, 6/6 cycles, `error=null`) with all three modules live.

### Next
The heavier S3 increment relocates the real engine (`FortuneProvider` / `SmartFortunes` / `Embedder` / `FortuneFileImporter`) + the `StartUp` land/poke/drop fortune loop out of the base, rebinding base infrastructure (data paths, settings, screen context, ONNX model) to the ABI — a separate, carefully-verified change.

### PR #5 — S3c: relocate the fortune engine (dumb + smart + ONNX) into the module, dormant

`closed` · opened 2026-08-07

## S3c — relocate the fortune engine into the module (dumb + smart + ONNX), dormant

The *expand* half of the engine relocation (expand/contract). The whole fortune engine now lives in the Fortunes module; it stays **dormant** (the module still only speaks the welcome), so the base is **untouched** and keeps owning fortunes at runtime — **zero regression**. The *contract* half (S3d: flip the base over) is next.

### What moved (into `modules/Fortunes/engine/`)
- **`FortuneProvider` + `FortuneFileImporter`** (S3c-1) and **`SmartFortunes` + `Embedder`** (S3c-2), copied keeping their `DesktopPet.Ai` namespace so their mutual references resolve in-module.
- **Rebinds to the ABI / module-local seams:** `AiSettings` fortune fields → a module `FortuneSettings`; `AppPaths` dirs → `FortunePaths` (host-storage-backed, temp fallback); `AtomicFile` + `CrossSessionLock` copied into `engine/FileHelpers.cs`; the embedded corpus simply not shipped (the loader returns empty when absent = the "no bundled content" design); the classifier-parity TSV embedded as a test fixture; the base UI/AI calls stripped from `FilterSelfTest`.
- **ONNX as the module's own dependency:** `Microsoft.ML.OnnxRuntime` + the bge-small model beside `Fortunes.dll`.

### The load-bearing bit: native ONNX inside a plugin's `AssemblyLoadContext`
The loader's `ModuleLoadContext` gains a `LoadUnmanagedDll` override — resolve via the module's `deps.json`, then **fall back to probing the module's own folder**. That fallback matters: onnxruntime's build targets flatten the native `onnxruntime.dll` beside the module dll (not under `runtimes/…/native/`), and it must resolve on an installed machine with no NuGet cache. (Additive host infra; NAudio was pure-managed and never needed it.)

### Also
`global.json` relaxed from the exact `10.0.201` pin to `10.0.100` + `rollForward: latestMinor` (that SDK was uninstalled from the dev box, leaving only 10.0.302 — it was blocking all local builds; CI still sets up 10.0.201).

### Verified (locally; GitHub Actions was down)
`build.ps1 -Release` clean (base + 3 modules); `--fortunes-engine-selftest` PASS incl. *"Embedder loads ONNX + embeds in the module ALC"* and *"SmartFortunes warms the pool in-module"*; full regression PASS — including the base's own `--filter-selftest` and `--smart-selftest`, confirming the base engine is untouched; resource-churn soak fresh **6/6, `error: null`**.

### Note
The base and the module both carry ONNX + the 34 MB model in the local build output now — expected temporary duplication; the base drops its copy in **S3d**, and modules aren't in the installer until **S6**.

### PR #6 — S3d: flip fortunes to the module + shed the smart/ONNX engine from the base

`closed` · opened 2026-08-07

## S3d — flip the base over to the module engine (the *contract* half)

Completes the fortune relocation. After S3c moved the engine into the module (dormant), S3d makes the module the **live** fortune source and strips the smart/ONNX engine out of the base.

### S3d-1 — module goes live, base stops speaking (no double-speak)
`FortunesModule` now builds its engine from its own storage/settings and speaks a fortune on **land / poke (1-2) / the drop tick** (smart pick when ready, else random), keeping the personalized welcome. The base's five `SayFortune` call-sites were redirected — land/poke just raise the events the module listens to; the drop `else`, the poke-12 escape fallback, and the brain-off ask-fallback call `Host.RaiseDropTick()` (the module's arbitrated fortune responder); init/reload/rebuild call `ApplyRandomDrop` instead of building the base engine.

### S3d-2 — shed the smart/ONNX layer from the base (−3,244 lines)
Removed `Embedder` + `SmartFortunes` and their ~50 MB payload (`onnxruntime.dll` + the 34 MB bge-small model + `Microsoft.ML.OnnxRuntime.dll` + `System.Numerics.Tensors.dll` + ONNX licenses) from the base — that engine lives only in the module now. Also deleted the dead StartUp glue (`fortuneRuntime`/`FortuneRuntimeState`/`StartFortuneGeneration`/`SayFortune`, stubbed `SmartFortunesStatus`), the four smart-fortune-lifecycle tests in `SecuritySelfTest`, and the `--embed`/`--smart`/`--smart-progress` flags.

**Scope note:** the *dumb* `FortuneProvider` + `FortuneFileImporter` (no ONNX) intentionally **stay** in the base — `RemoteCatalog` uses `FortunePackLoadPolicy` for pack-download limits and the Options `FortunesController` enumerates sources. Those, the residual embedded corpus, and the now-disconnected fortunes Options tab move to the module when the Options UI is rebuilt in **S5**. So no Options stub / RemoteCatalog rework here.

### Design consequence
With **no fortune pack installed**, the pet now speaks only the personalized welcome (land/poke/drop go quiet) — the intended "engine ships, content doesn't" design. The classic 486 KB corpus becomes an importable starter pack later (S7).

### Validated (full suite, all PASS, zero regression)
Base output confirmed **ONNX-free** (module carries it). `--fortunes-selftest` (live flip end-to-end: land/poke-1/drop speak a pack line, poke-4 silent) + `--fortunes-engine-selftest` (dumb + smart + ONNX-in-ALC) + `--module-host` + `--sound` + `--filter` + `--fortunecache` + `--options` + `--fortunes-webview` + `--security` (smart-lifecycle tests cleanly removed, rest intact) + `--hardening` + `--pettyperegistry` + `--catalog` + `--fullscreen`; resource-churn soak **6/6, `error: null`**.

### PR #7 — S4: extract the AI-brain module (functional flip)

`closed` · opened 2026-08-07

## S4 — extract the AI-brain module (functional flip)

The optional, off-by-default screen-commentary LLM is extracted from the base into `modules/AiBrain`, following the same expand/contract pattern as S2 (Sound) and S3 (Fortunes). The module now **owns the AI brain at runtime**; the base is runtime-disconnected.

### What landed
- **S4a — expand (dormant):**
  - Additive ABI: `IHost.PlayAnimationAll(candidates)` (emote every pet, parallels `SayAll`) + `ScreenContext.WindowUnderCompanion` (screen-zone awareness). All in-repo `IHost` impls updated; no third-party hosts exist.
  - Real global-hotkey registrar in `CompanionHost` (wraps the proven `HotkeyListener`); was a no-op stub.
  - `modules/AiBrain` scaffold + the whole brain engine relocated to `modules/AiBrain/engine/` (AiBrain / AiSessionManager / backends / ChatHistory / Personas / AiEndpointPolicy / AiExecutablePolicy / settings), rebound to the ABI (`AiPaths`, `ScreenContext`, copied `AtomicFile`/`CrossSessionLock`/`UnicodeTextProgress`, module-carried Newtonsoft). Dormant; base untouched.
  - `--aibrain-selftest` proves the relocated engine runs **inside the module's AssemblyLoadContext**, including a DPAPI API-key round-trip (encrypt → atomic write → cross-session lock → reload → decrypt), chat-history persistence, endpoint/persona/model policy, and backend construction.
- **S4b — flip:**
  - The module goes live: owns the ask/hotkey/idle/drop/emote flow through host services only; drop responder outranks Fortunes; async LLM responses marshalled to the UI thread via the captured `SynchronizationContext`. A **non-destructive migrator** copies the base `ai-settings.json` (incl. DPAPI keys) into the module store on first run, leaving the original intact.
  - The base is runtime-disconnected: `DropTimer_Tick` always raises the arbitrated drop tick; `ApplyAiBrainState` is neutered (only ever retires); AI tray items removed; the `AiController`/`AiState` Options seam removed.

### Deferred to S5 (like S3d deferred the fortune UI/engine)
Deleting the 8 base AI-brain files, removing the FormOptions AI tab, and trimming the SecuritySelfTest AI tests are entangled with `AiSettings`' DPAPI credential machinery (which needs `Personas`/`AiEndpointPolicy`/`AiProviders` until the AiSettings split). They're cut together with the **AiSettings split + WPF Options rebuild** in S5. The base still compiles and its AI defensive self-tests still pass — it just never runs the brain. During the gap the AI brain is off by default and reachable via its own setting/hotkey (accept-the-gap).

### Validation (local, this box)
Clean `-Release` (base + 4 modules); full self-test suite green — `--filter/--security/--catalog/--fullscreen/--webview/--pettyperegistry/--hardening/--module-host/--sound/--fortunes/--fortunes-engine/--aibrain` + `--options/--fortunecache/--fortunes-webview` (under `DESKTOPPET_DATA_ROOT`) + the runtime-hardening source invariants + the resource-churn soak (exit 0). Zero regression. Nothing reinstalled or released; box stays on v1.1.0.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #8 — S5a: tray assembled from module contributions (closes the S4 AI gap)

`closed` · opened 2026-08-07

## S5a — tray-from-contributions renderer

First slice of S5. The context menu now renders module-contributed `TrayItem`s, and the **AiBrain module contributes its own Enable/Disable + Ask items** — so the AI brain is reachable from the tray again (it was settings-only after the S4 flip; this closes that accept-the-gap).

- **ContextMenus:** on menu `Opening`, merge `CompanionHost.TrayItems` (sorted by Group then Order, separators between groups) just after Test Speech — re-evaluating each item's `Visible`/`DynamicText` live and building `BuildChildren` submenus lazily on open. Rebuilt every open so late-loaded modules appear and dynamic labels refresh. Fully defensive: a throwing module item can never break the core tray; the handler is unhooked + tracked items cleared on dispose.
- **AiBrainModule:** contributes "Enable AI"/"Disable AI" (DynamicText toggle → flips the module's own `AiBrainEnabled`, saves, rebuilds the brain) and "Ask about my screen" (visible only when enabled).
- `--aibrain-selftest` asserts the module contributes exactly its 2 tray items.

WinForms-only, additive, no WPF dependency. The WPF options shell + schema panes are S5b.

### Validation (local)
Clean `-Release` (base + 4 modules); `--aibrain-selftest` (2 tray contributions + in-ALC engine probe) + module-host / sound / fortunes / security / hardening + resource-churn soak all green (exit 0). Live tray render is manual-eyeball (a real tray menu can't be opened headlessly). Zero regression. Box stays v1.1.0.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #9 — S5b-1: minimal WPF settings shell with schema-driven module panes

`closed` · opened 2026-08-07

## S5b-1 — minimal WPF settings shell (schema-driven module panes)

First cut of the WPF module-manager window that will replace `FormOptions`. It renders the **core Preferences pane** plus each **module's schema-driven `OptionsPane`**, and modules persist to their *own* store via a new `OptionsPane` Load/Save binding. Coexists with the classic Options dialog for now (opened from a new **"Module settings…"** tray item); `FormOptions` retirement + the Pets/Fortunes tab port are later S5 steps.

- **`UseWPF` enabled** alongside WinForms (committed separately, `7cb07ff`, verified in isolation — no payload change, WPF is framework-provided/FDD). Pet stays WinForms; the window shows modally from the WinForms UI thread.
- **ABI:** `OptionsPane` gains `Load()`/`Save(values)` delegates so a module renders as a declarative schema yet persists to its own (possibly DPAPI-scoped) store — not just the host's `IModuleSettings` bag. Secrets stay write-only (Load never returns plaintext; Save gets a secret key only when the user typed one).
- **WPF (programmatic, no XAML** → no BAML/packaging change): `OptionsWindow` (left-nav + content + Apply/Close) and `PaneView` (schema → controls: Bool=CheckBox, Int/Text=TextBox, Enum=ComboBox, Secret=PasswordBox). `PaneView` is headless-constructable so the render + round-trip is self-testable.
- **`OptionsShell`** assembles the LocalData-backed core Preferences pane + the module panes and opens the window.
- **AiBrain** contributes a full "AI Brain" pane (enable/provider/models/vision/hotkey/idle/consent/api-key) bound to its own `AiSettings` — exercising every `SettingKind` incl. the enum + write-only secret.
- **`--wpf-options-selftest`** (STA): OptionsShell yields the core pane; PaneView renders all 5 kinds + round-trips Load→controls→Collect; blank secret omitted; Save forwards. Wired into `build.yml`.

### Validation (local)
Clean `-Release` (base + 4 modules); full self-test suite (13 no-env flags incl. `--wpf-options-selftest` + `--options` under `DESKTOPPET_DATA_ROOT`) + source invariants + resource-churn soak all green (exit 0). The live modal window is manual-eyeball (a modal WPF window can't be shown headlessly). Zero regression. Box stays v1.1.0.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #10 — S5b-2a: action buttons on schema options panes + AI Test connection

`closed` · opened 2026-08-07

## S5b-2a — action buttons on schema options panes + AI Test connection

Follow-up to S5b-1: the WPF pane could render *data* fields but had no way to express things a module *does* (the classic Options AI tab's **Test connection** / **Clear history** buttons had no equivalent). This adds an action concept.

- **ABI:** `OptionsPane` gains an `Actions` list of `PaneAction { Label, InvokeAsync }`. `InvokeAsync` is async (`Task<string>`) so a slow (~15s) connection probe never freezes the UI; it returns a short status line.
- **WPF:** `PaneView` renders each action as a button + status line — on click it disables the button, shows "working…", awaits `InvokeAsync`, shows the result. A throwing action reports "failed: …" rather than breaking the pane.
- **AiBrain** contributes **"Test connection"** (builds a backend from the current settings, probes availability + a tiny chat, reports `✓ connected · <model> OK <n>s` or the error) and **"Clear chat history"**.
- `--wpf-options-selftest` gains an action-invocation assertion.

### Validation (local)
Clean `-Release` (base + 4 modules); `--wpf-options-selftest` (now incl. the action) + `--aibrain-selftest` + 11 regression flags + resource-churn soak all green. Zero regression. Dev build reinstalled locally for eyeball testing (v1.2.0).

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #11 — S5b-2(a): complete the AI Brain pane to near-parity with the legacy tab

`closed` · opened 2026-08-07

## S5b-2(a) — complete the AI Brain pane to near-parity with the legacy AI tab

Follow-up to S5b-1/2a: brings the WPF AI Brain pane up to the substance of the classic Options → AI tab (which surfaced a big gap when compared side by side). All new fields bind to the module's own `AiSettings` via the existing Load/Save.

**Added:** Pet name, Your name, Personality; **Speech style** (enum of the friendly Personas names — stored as the id); Remember recent remarks (memory); **Endpoint / base URL** with a provider/endpoint dance (switching Provider prefills that provider's default endpoint; keeping the provider honors an edited endpoint); Idle max; Start Ollama automatically; Preload model on launch.

**Deferred (focused follow-on, not parity of substance):**
- Persona **preset** dropdown — needs reactive enum→text linkage the static schema doesn't express.
- Text/Vision model **dropdowns + "Refresh model list"** — needs a new backend list-models call + a dynamic-enum re-render. Model fields stay editable text for now; Test connection still validates the chosen model.

### Validation (local)
Clean `-Release` (base + 4 modules); `--aibrain-selftest` + `--wpf-options-selftest` + module-host / sound / fortunes / security / hardening + resource-churn soak all green. Zero regression. Dev build reinstalled locally (v1.2.0) for eyeball testing.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #12 — S5b-2(b): complete the core Preferences pane + Restore-pet

`closed` · opened 2026-08-07

## S5b-2(b) — complete the core Preferences pane

Brings the WPF Preferences pane up to the legacy Preferences tab. Added (alongside volume/speech/duration): **Run at Windows startup, Bring collided window to front, Keep pet above the taskbar, Allow multiple screens, Pets at startup, Size (1–3)**, and the **Randomly-drop-a-fortune** toggle + its every / ±minutes — plus a **"Restore default pet"** action button.

Backing: LocalData for the core prefs (persist immediately), `StartupRegistration` (HKCU Run) for run-at-startup, and `AiSettings` (load-mutate-save) for the random-drop trio; on save it nudges the running pet via `ICompanionRuntime.ReloadAiSettings` + refreshes the tray speech item. Restore-pet reuses `CompanionCatalog` + the runtime's `LoadNewXMLFromString`.

### Validation (local)
Clean `-Release` (base + 4 modules); `--wpf-options-selftest` + `--aibrain-selftest` + module-host / fortunes / security / hardening / pettyperegistry + `--options` (isolated root) + resource-churn soak all green. Zero regression. Dev build reinstalled locally (v1.2.0) for eyeball testing.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #13 — S5b-2(c): host Pets gallery pane (custom WPF control)

`closed` · opened 2026-08-07

## S5b-2(c) — host Pets gallery pane (custom WPF control)

Adds the Pets gallery to the WPF settings window. A thumbnail gallery isn't expressible as a data schema, so the `OptionsWindow` now supports **host-built custom panes** alongside schema panes — **without leaking any WPF type into the plugin ABI** (the ABI stays schema-only + framework-agnostic; the module-supplied custom-control escape hatch stays deferred to when a third party needs it).

- **OptionsWindow:** new host-side `ShellPane` abstraction — `SchemaShellPane` wraps an ABI `OptionsPane` → `PaneView`; `CustomShellPane` hosts a host-built control. Apply shows only for schema panes; custom panes apply via their own controls. `CollectPanes` → `ShellPane[]`: Preferences (schema) + Pets (custom) + each module's schema pane.
- **CompanionsPaneControl:** a card per installed pet (thumbnail + name + Use/Add + an Active marker), backed by the base `CompanionsController`; Use/Add apply immediately through the runtime and refresh the gallery. Local pets only for now (the online catalog is a follow-on — needs an `ICatalogService`).
- **CompanionThumbnails.GetPng(id):** raw PNG bytes so the WPF gallery builds a `BitmapImage` directly.
- `--wpf-options-selftest` updated for the `ShellPane` return.

### Validation (local)
Clean `-Release` (base + 4 modules); `--wpf-options-selftest` + `--aibrain-selftest` + module-host / fortunes / pettyperegistry / security + resource-churn soak all green. Live gallery render is manual-eyeball. Zero regression. Dev build reinstalled (v1.2.0).

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #14 — S5b-2(c2): Pets gallery multi-pet count + Remove button + eSheep icon

`closed` · opened 2026-08-07

Addresses eyeball feedback: the gallery now shows the live on-screen mix (per-pet 'on screen: N' + an 'active' tag) via StartUp.OnScreenMix; a Remove button per on-screen pet (RemoveOnePet); and the built-in eSheep card falls back to the app icon. Local validation: --wpf-options-selftest + --pettyperegistry green; live render manual-eyeball. 🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #15 — S5b-2(c3): Pets card enrichment (descriptions, counts, quips)

`closed` · opened 2026-08-07

feat(ui): S5b-2(c3) - Pets card enrichment (descriptions, animation/sound counts, quips)

Each pet card now shows a unique tongue-in-cheek blurb plus an "N animations . M
sounds" line. The seven colored sheep share one 268-move set, so each gets its
own colour-based quip (CompanionBlurbs) to keep the descriptions distinct. Counts are
read from each pet's animations.xml (animation / sound elements) and cached per id.

Request A of the Pets feature set (A card enrichment / B per-pet sound /
C bundle-all / D check-for-new).

Verified: clean -Release (base + 4 modules); --wpf-options-selftest green.
Live gallery manual-eyeball. Zero regression.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>

### PR #16 — S5b-2(c4): Check-for-new-pets button in the Pets pane

`closed` · opened 2026-08-07

Adds a footer **Check for new pets** button to the WPF Pets gallery.

- Fetches the online catalog (`RemoteCatalogClient.FetchAsync`), diffs it against the locally present pets (bundled + downloaded), and reports the count.
- Lists any new pets as **download cards** (thumbnail + name + author + Download).
- Download reuses the HTTPS-trusted, SHA-256-verified path the classic Options window used: `DownloadVerifiedAsync` -> `CompanionXmlValidator.TryParse` -> atomic write to the library pets dir, then the gallery refreshes and re-diffs against the **cached** catalog (no re-fetch).
- The network `CancellationTokenSource` is cancelled on pane unload.

Request **D** of the Pets feature set (A card enrichment / B per-pet sound / C bundle-all / **D check-for-new**).

Verified: clean `-Release` (base + 4 modules); `--wpf-options-selftest` green.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #17 — S5b-2(e): per-pet size override

`closed` · opened 2026-08-07

Adds a **per-pet size override** so a pet can be sized independently of the others — e.g. Pingus at 2 while the sheep stay at 1.

Each pet card gets a small **Size** dropdown (`Default / 1 / 2 / 3`). "Default" follows the global size. The override is a scale level baked in when the pet **type** is staged, so it applies the next time that pet is added (or on the next launch); pets of that type already on screen keep their size until then — matching how the global size already behaves.

**Changes**
- `AppSettingsDocument`: new optional `petSizes` list (`id -> level 1/2/3`), normalized / deduped / clamped like the pet mix, and wired into `Clone` + the cross-process merge. Absent = follow global; older docs carry none (additive, no schema bump).
- `LocalData`: `GetPetSizeLevel` / `SetPetSizeLevel` / `GetEffectivePetScaleFactor`.
- `StartUp`: `TryStageRuntime` now takes the effective factor; the active/default, extra-type (`ResolveExtraType`), and "Use this pet" staging paths all pass the per-pet factor. New `SetPetSize(id, level)` persists the override and drops a staged-but-unused type so a fresh add re-stages at the new size immediately.
- `CompanionsPaneControl`: the per-card Size dropdown.

**Verified**
- CoreTests: 23 groups pass, including a new `Settings per-pet size validation`.
- Clean `-Release` build (base + 4 modules).
- `--wpf-options-selftest` green.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #18 — S5b-2(e2): per-pet size as a top-right cycle button

`closed` · opened 2026-08-07

Per user feedback the per-card **Size** dropdown cluttered the box. Replaced it with a compact **Size N** button in the card top-right corner that cycles `1 -> 2 -> 3` on click. Behavior unchanged: each click sets an explicit override applied when the pet is next added (or on restart); the button seeds from the stored override, else the effective global level.

Verified: clean `-Release`; `--wpf-options-selftest` green.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #19 — S5b-2(e3): size as an inline clickable number in the stats line

`closed` · opened 2026-08-07

Per user feedback, drop the top-right Size button and put the size level as an inline **clickable number** in the card stats line: `N animations · M sounds · size K`.

It is a `Hyperlink` styled like the surrounding gray text (no underline at rest, no box, `Focusable=false`, hand cursor + tooltip); clicking the number cycles `1 -> 2 -> 3`. Behavior unchanged: each click sets an explicit override applied when the pet is next added (or on restart); the number seeds from the stored override, else the effective global level.

Verified: clean `-Release`; `--wpf-options-selftest` green.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #20 — S5b-2(f): default settings window to 1050x820 (Pets 3-across)

`closed` · opened 2026-08-07

Bumps the WPF settings window default from `720x560` to `1050x820` with a min size, so the Pets gallery reflows to **3 cards across** and ~4-5 rows down by default (the gallery WrapPanel already wraps to fewer columns as the window shrinks). Resizable; floor still fits ~2 columns + the nav.

Schema panes (Preferences / AI Brain) are unchanged for now - columnizing field forms is flagged for a UX read first (multi-column cramps URL/key fields).

Verified: clean `-Release`; `--wpf-options-selftest` green.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #21 — S5b-2(g): light/dark/system theme for the settings window

`closed` · opened 2026-08-07

feat(ui): S5b-2(g) - light/dark/system theme for the settings window

Adds a themeMode preference (System/Light/Dark, default System) and a Theme dropdown
in the Preferences pane. A new WpfTheme applies it when the settings window opens:
System consults the OS (WindowTheme.IsDark, the same registry check the WinForms
dialogs use); Dark paints the window + installs implicit control styles (nav / buttons
/ inputs / lists) and the immersive dark title bar; Light keeps the stock WPF look
(lower risk than fighting the default light templates). A theme change takes effect on
the next open (live re-theme is a follow-up). Combo dropdowns / scrollbars keep default
chrome (WPF template limits) - noted for polish.

- AppSettingsDocument: themeMode (Order 15), normalized to system/light/dark, wired
  into Clone + the cross-process merge; default system; older docs default on load.
- LocalData: GetThemeMode / SetThemeMode.
- WpfTheme: palette + implicit styles + DWM dark title bar; EffectiveDark(mode).
- Preferences pane: Theme enum field + Load/Save.

Verified: CoreTests (24 groups, incl. theme-mode normalization); clean -Release
(base + 4 modules); --wpf-options-selftest green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>

### PR #22 — B1: host-owned audio output (NAudio 3, base plays pet sounds)

`closed` · opened 2026-08-07

feat(audio): B1 - host-owned audio output (NAudio 3, base plays pet sounds)

Option B: the base now owns audio playback instead of the Sound module. A new
AudioOutput (src/dotNet/AudioOutput.cs) is a single shared mixer + output device
(MixingSampleProvider + WaveOut) that plays the pet's animation sounds today and the
AI speech engine (TTS) later, through one path. Pet MP3s decode once (ACM via the OS
codec, no shipped native binary) into a cached float buffer at the mixer format; each
play adds a volume-wrapped, optionally-looping input, so distinct sounds overlap and
speech can duck SFX once TTS lands. Device errors are swallowed (no audio device =
silent, never throws into the engine).

StartUp routes the engine's animation-sound selection (Animations.SoundSink) straight
to AudioOutput instead of raising AnimationStarted for the module, so the base plays
and the S2 Sound module is inert (retired in B4).

NAudio is a base dependency again (it left in S2 on the false premise that no pet ships
audio - every bundled pet does). Only NAudio.Core + NAudio.WinMM (3.0.0-preview.6, net9+
Span-based; verified decoding a real pet MP3 on net10 via a spike) plus the transitive
NAudio.Midi + System.Numerics.Tensors; no native binary. Payload manifest, the
NAUDIO_LICENSE.txt copy, and THIRD_PARTY_NOTICES updated.

Chose NAudio 3 preview after research: leaner/modular vs v2 (no Dmo/Asio/Wasapi/WinForms),
Span-based, and its MixingSampleProvider makes the future TTS mixer free.

Verified: clean -Release (base + 4 modules; payload set-equality OK); CoreTests;
--sound-selftest / --module-host-selftest / --wpf-options-selftest green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>

### PR #23 — B1.5: audio output device chooser + Test-sound button (DirectSound)

`closed` · opened 2026-08-07

feat(audio): B1.5 - output device chooser + Test-sound button (DirectSound)

Adds a "Sound output device" dropdown and a "Test sound" button to the Preferences
pane so pet sounds (and later TTS) can be routed to a chosen playback device.

Output moves from WaveOut to DirectSound (NAudio.Dmo): DirectSoundOut enumerates
devices with full friendly names + GUIDs and plays through a selected device. WASAPI
was rejected - its package requires a Win10-versioned TFM that drags a ~25 MB Windows
SDK projection (Microsoft.Windows.SDK.NET.dll) into the payload; DirectSound needs no
TFM bump and no native binary (verified on net10 via a spike). DirectSoundOut is not
obsolete in NAudio 3, so the build stays warning-clean.

- AudioOutput: DirectSoundOut targeting the chosen device GUID, falling back to the
  default device if the chosen one is gone; PCM16 to the device; SetDevice (live
  switch), static EnumerateDevices, PlayTestTone (a short 440 Hz tone at a fixed
  audible level, ignoring mute since the user explicitly asked to hear it).
- Setting: audioDeviceId (device GUID; "" = default) in AppSettingsDocument, normalized
  / cloned / merged; LocalData Get/SetAudioDeviceId.
- StartUp applies the saved device on init; ApplyAudioDevice (live) + PlayTestSound.
- Preferences pane: device dropdown (name<->GUID map; the default device is stored as
  "") + a "Test sound" action. Applying a device switches the live output immediately.
- Packaging: NAudio.Dmo.dll added to the payload manifest; notices updated.

Verified: CoreTests (+ audio-device id normalization); clean -Release (base + 4 modules,
payload set-equality OK); --sound / --module-host / --wpf-options-selftest green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>

### PR #24 — B2/B3: per-pet sound toggle

`closed` · opened 2026-08-08

feat(audio): B2/B3 - per-pet sound toggle

Adds an inline "sound on / sound off" toggle to each Pets card (only for pets that have
sounds), so a pet type's animation sounds can be muted independently - e.g. mute Pingus
while the sheep keep chattering.

- B2 (identity): Animations.SoundSink now carries the pet TYPE id (petTypeId, animId,
  data, loop). Each staged Animations is tagged with its id at stage time ("" = the
  active/default pet, folder id for extras).
- B3 (toggle): StartUp's sink gates playback on a per-pet mute checked at PLAY time, so
  toggling takes effect on the next sound with no restage. New mutedPets list in
  AppSettingsDocument (ids with sound off; absent = on), normalized/cloned/merged;
  LocalData IsPetSoundEnabled / SetPetSoundEnabled; StartUp.SetPetSound; an inline
  clickable toggle in CompanionsPaneControl matching the size-number style.

Per-TYPE (like per-pet size): keyed by the specific pet id, so it works on extras
wherever they're on screen; the active/default pet is keyed "" (shared follow-up: key
the active pet by its real id so its card toggle applies while it's the active pet).

Verified: CoreTests (+ muted-pets validation); clean -Release (base + 4 modules);
--sound / --module-host / --wpf-options-selftest green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>

### PR #25 — B4: retire the inert S2 Sound module

`closed` · opened 2026-08-08

chore(audio): B4 - retire the inert S2 Sound module

The base owns audio playback since B1 (AudioOutput), so the S2 Sound module never
receives AnimationStarted and is dead weight. Removed it and its bundled NAudio 2.3.0.

- Deleted modules/Sound (SoundModule.cs, Sound.csproj, its BACKLOG) + the base
  --sound-selftest (SoundModuleSelfTest.cs, the Program.cs dispatch, the build.yml flag,
  the csproj compile item, the build.ps1 module-list entry + comment).
- THIRD_PARTY_NOTICES: the base now ships only NAudio 3.0.0-preview.6 (Core/Dmo/Midi/
  WinMM); dropped the module's NAudio 2.3.0 rows (Asio/Wasapi/WinForms/meta).
- The AnimationStarted ABI event + AnimationInfo stay (a legit lifecycle event for future
  modules; no live consumer today).
- Backlogged the TTS/speech module as its own future module (calendar/appointments "speak"
  on the shared output) per the user's direction.

Verified: clean -Release (base + 3 modules; payload set-equality OK); CoreTests;
--module-host-selftest (3 modules) / --wpf-options-selftest green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>

### PR #26 — fix(pets): key active pet size/sound by its real id

`closed` · opened 2026-08-10

fix(pets): key the active pet's size/sound by its real id (not "")

Per-pet size + sound key by the specific pet id, so they worked on pets added alongside
(extras) but NOT on the active/default pet, which staged with the "" active-slot
placeholder. Now the active pet is keyed by its real id, so its card toggles apply.

- New activePetId setting (default the built-in "eSheep"; normalized to a real pet id,
  empty/unsafe -> built-in) in AppSettingsDocument; LocalData Get/SetActivePetId.
- StartUp keys the active-pet staging (Init + LoadNewXMLFromString) by GetActivePetId()
  for both the scale factor and Animations.PetTypeId, instead of "".
- The pick-a-pet paths persist it first: CompanionsController.UsePet + RestoreDefaultPet and
  FormOptions.ApplyPet call SetActivePetId before LoadNewXMLFromString. Raw-XML drops /
  the restore-on-reload path keep the current active id.
- The on-screen pet MIX still keys the active type as "" (that's spawn counts, separate
  from per-pet settings) - unchanged.

Verified: CoreTests (+ active-pet id normalization); clean -Release (base + 3 modules);
--module-host / --wpf-options-selftest green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>

### PR #27 — S5: grouped-card settings layout (settings re-eval foundation)

`closed` · opened 2026-08-10

feat(ui): grouped-card settings layout (settings re-eval foundation)

Settings panes now render as titled cards that flow into responsive columns (2-3 across
in the wide window) instead of one skinny column. This is the model Fortunes + future
modules build on: a module declares grouped settings, the shell renders titled cards, and
a rich section (e.g. a fortune-pack list) drops in later as one custom card.

- ABI: optional SettingField.Group + PaneAction.Group (additive; null/"" => one default
  card). Fields/actions sharing a Group name render in one titled card.
- PaneView: buckets fields + actions by Group (first-appearance order) into titled Border
  cards laid out in a WrapPanel (responsive columns); narrower wrapping label column; the
  Save/Collect path is keyed by field Id and unchanged.
- Preferences: grouped into Startup & window / Sound (+ Test sound) / Speech / Fortune
  drop; Restore-default-pet in a default card.
- AI Brain module: grouped into AI brain / Persona (+ Clear history) / Provider (+ Test
  connection) / Triggers / Local server (Ollama).

Verified: clean -Release (base + 3 modules); --wpf-options-selftest (grouped render + Save
round-trip) / --module-host-selftest / CoreTests green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>

### PR #28 — fix(ui): settings-window polish (masonry packing + readable dark dropdowns)

`closed` · opened 2026-08-10

Two settings-window polish fixes on top of the grouped-card foundation (PR #27), both surfaced by eyeballing the live dev install.

## 1. Masonry packing (no more tall empty boxes)

Grouped cards were laid out row-by-row, so a short card (AI brain's single toggle) next to a tall one (Persona) stretched into a big empty box, and a lone small card (Local server) was stranded on its own row. Swapped the row-wrap for a small **masonry** panel: cards flow into a responsive number of equal-width columns and each card drops into the currently-shortest column, so differing-height cards pack and the columns stay level. Column count derives from the available width, so it reflows on resize.

- `MasonryPanel : Panel` — Measure/Arrange place each child in the shortest column; column count = availableWidth / pitch (min 1).
- `PaneView` uses `MasonryPanel` in place of `WrapPanel`; card width/margins and the Save round-trip (keyed by field Id) unchanged.

## 2. Readable dark ComboBox dropdown

In dark mode the closed combo was themed, but the open dropdown popup used the stock template (painted from SystemColors → light popup, faint text), so the provider/speech-style/audio-device lists were nearly unreadable. Gave `ComboBox` a full dark template in `WpfTheme`: dark closed box + dark popup, plus a `ComboBoxItem` style with near-white text and a blue hover/select highlight. Parsed as a ResourceDictionary so the styles register implicitly and reach the popup items.

## Verification

- `build.ps1 -Release` clean (base + Contracts + Fortunes + AiBrain).
- `--wpf-options-selftest` (builds the window + applies the dark theme, so both the grouped render/Save round-trip and the combo template XAML are exercised) / `--module-host-selftest` / CoreTests all exit 0.
- Dev install refreshed + eyeballed.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #29 — feat(ui): version stamp in the settings window (bottom-left)

`closed` · opened 2026-08-10

Restores the bottom-left build-version stamp the old FormOptions dialog had, now in the WPF settings window. Bottom bar is a DockPanel: version (muted grey) left, Apply/Close right. Value from `Application.ProductVersion` (never hardcoded; currently v1.2.0).

Verified: clean `-Release`; `--wpf-options-selftest` green; dev install eyeballed.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #30 — fix(speech): repaint bubble when the tail moves (no stale streaks / ghost notch)

`closed` · opened 2026-08-10

fix(speech): repaint the bubble when the tail moves (no stale streaks / ghost notch)

FormCompanion calls FormSpeech.Reposition every tick so the bubble follows the pet.
As the pet walks, the tail slides along the bubble edge (and flips top/bottom)
without the bubble changing size. Reposition updated the window bounds and the
clip Region (the new tail shape) but never invalidated, and a same-size window
move just blits the old pixels — so the painted outline kept the OLD tail while
the Region already clipped to the NEW one. Result: stale black lines across the
moved tail and a leftover notch in the border where the tail used to be.

Add Invalidate() after SetBounds/UpdateRegion in Reposition so OnPaint redraws
the outline to match the new Region. It sits below the existing no-op guard, so
an idle (unmoved) bubble still never repaints.

Verified: clean -Release; --wpf-options-selftest / --module-host-selftest /
CoreTests (incl. "Unicode speech and logical sprite anchoring") green; dev
install eyeballed (walk + fall while a bubble is up — tail tracks cleanly).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>

### PR #31 — feat(ui): drop Size field; Restore-default-pet -> Reset-to-default-settings

`closed` · opened 2026-08-10

feat(ui): drop redundant Size field; "Restore default pet" -> "Reset to default settings"

Two Preferences-pane changes now that per-pet size lives in the Pets module.

1. Drop the "Size (1-3)" field. Per-pet size is set on each pet card in the Pets
   pane; the global scale stays only as the internal fallback for pets without an
   override (GetEffectivePetScaleFactor / CompanionsPaneControl), so it's no longer a
   Preferences field.

2. Replace the "Restore default pet" button with "Reset to default settings". It
   restores the preferences shown on this page — startup/window behavior, volume,
   audio device, speech, and fortune-drop — to their defaults behind a Yes/No
   confirmation, then persists. Scoped on purpose: the loaded pet (XML/images),
   per-pet sizes/mutes, and the AI Brain module's own settings are left untouched.
   Run-at-startup (registry) resets to off; the reset output device applies to the
   running pet immediately.

Supporting (additive, reusable): PaneAction.ReloadPaneAfter + ShellPane.RequestReload
so an action can ask the host to rebuild its pane afterward — the reset uses it so
the fields visibly snap to their defaults. The delegate may set ReloadPaneAfter from
inside InvokeAsync (the host reads it post-await), so it declines the reload on cancel.

Verified: clean -Release; --wpf-options-selftest / --module-host-selftest / CoreTests
green; dev install eyeballed (reset confirm + live refresh, Size field gone).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>

### PR #32 — feat(fortunes): Fortunes settings pane, part 1 (selection + content toggles)

`closed` · opened 2026-08-10

feat(fortunes): Fortunes settings pane, part 1 (selection + content toggles)

The Fortunes module contributed no UI: its settings (smart / spicy / tier /
spicyOnly / noProfanity) were only ever read once at Init, and the old FormOptions
"Fortunes" tab edited the BASE engine's AiSettings — a copy the running pet doesn't
use. So changing fortune settings in the old UI never affected the pet.

Give the module its own schema-driven OptionsPane (rendered by the WPF grouped-card
shell), so the settings edit the LIVE module:

- Grouped fields: Selection (Smart, context-aware picks) / Content level (Enable
  spicy, Spice level [Edgy+NSFW | True NSFW only], Skip the tame ones, Remove
  profanity). Enum tier maps friendly labels <-> stored "edgy"/"nsfw".
- Load/Save round-trip through host.GetSettings("fortunes"); Save persists then
  calls RebuildEngine() so the change (and any pack added to the folder) takes
  effect on the running pet immediately — no restart.
- "Rebuild smart index" action reloads packs + re-warms the semantic index and
  reports status via SmartFortunes.WarmProgress (real module status, replacing the
  base's stubbed placeholder).

Init's engine build is refactored into RebuildEngine() (shared by Init + Save).
This is part 1; the richer Sources / Genres / Packs list (import, per-source enable,
open-folder) is the next increment — it needs a declarative list-card primitive.

Verified: clean -Release (base + Contracts + Fortunes + AiBrain); --wpf-options-selftest
/ --module-host-selftest / --fortunes-engine-selftest / CoreTests green; dev install
eyeballed (Fortunes pane renders, toggles save + drive the live engine).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>

### PR #33 — feat(fortunes): Fortunes pane part 2 — Sources + Genres list cards

`closed` · opened 2026-08-10

feat(fortunes): Fortunes pane part 2 — Sources + Genres list cards (generic list-card ABI)

Adds the rich pack/genre management the old FormOptions "Fortunes" tab had, on a new
declarative list-card primitive so the ABI stays framework-agnostic.

ABI (additive): ListItem { Id, Label, Detail, Checked } + ListCard { Title, LoadItems,
SetChecked, Actions, EmptyHint } + OptionsPane.Lists. A module supplies data + delegates;
the host renders the WPF, so a checkable dynamic list a flat schema can't express (fortune
packs, genres) now has a home. Reusable by any module.

Host renderer (PaneView): each ListCard renders as a titled card (shared card chrome via
NewCard) with a height-capped, scrollable checkbox list (label + detail/count) that toggles
live through SetChecked, plus card-level PaneAction buttons. Flows into the same masonry
columns as the schema cards. IsChecked is set before wiring events so building never fires a
spurious toggle.

Fortunes module: two list cards driving the LIVE engine —
- "Fortune packs" (sources): each installed .txt pack with its line count (· spicy when it
  has edgy/nsfw lines); unchecking disables it (persisted disabledSources). Buttons: Open
  fortunes folder (Explorer) + Rescan folder (rebuild + refresh the card).
- "Genres": each delivery genre with its count; unchecking disables it (disabledGenres).
Disabled lists persist to host.GetSettings("fortunes") (newline-joined) and are read back in
LoadFortuneSettings; every toggle rebuilds the engine so it applies to the running pet at once.

WPF self-test now builds a probe ListCard (exercises BuildListCard headlessly).

Deferred (next): a validated file-import picker ("Add fortunes…") and the online catalog
packs — import needs a host file-pick decision; catalog ties into S7.

Verified: clean -Release; --wpf-options-selftest / --module-host-selftest /
--fortunes-engine-selftest / CoreTests green; dev install eyeballed.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>

### PR #34 — feat(ai): canned Personality presets (dropdown) instead of free text

`closed` · opened 2026-08-10

feat(ai): canned Personality presets (dropdown) instead of free text

The AI Brain persona's "Personality" was a free-text field, which a user could phrase
in a way that doesn't slot cleanly into the system prompt ("Your personality: <text>.")
or read naturally. Replace it with a dropdown of 12 curated presets (same label<->value
pattern as Speech style): the dropdown shows a short label, the stored value is the full
blurb that goes into the prompt.

Presets: Friendly & upbeat, Dry & sarcastic, Cheerful & bubbly, Calm & zen, Sassy & bold,
Shy & sweet, Grumpy but lovable, Curious & nerdy, Wise mentor, Chaotic & goofy, Cool &
aloof, Motivational coach.

- personality SettingField: Text -> Enum (Options = preset labels).
- Load maps the stored blurb -> its label; Save maps the picked label -> its blurb.
- First preset's blurb == the AiSettings default, so a fresh install round-trips; an older
  free-text value that matches no preset falls back to the first preset (user re-picks).

Verified: clean -Release; --wpf-options-selftest / --module-host-selftest / CoreTests green;
dev install eyeballed.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>

### PR #35 — fix(welcome): greet by the configured name, not the Windows username

`closed` · opened 2026-08-10

fix(welcome): greet by the configured name, not the Windows username

The Fortunes out-of-box welcome greeted with Environment.UserName ("Admin"), ignoring
the name set in the AI persona — because the two modules are ALC-isolated and can't read
each other's settings. Add a small host-mediated shared "owner name" so the AI name wins
when the brain is on, matching the user's request.

- ABI (additive): IHost.OwnerName (get) + IHost.SetOwnerName(name). "" = none set.
- CompanionHost holds it in-memory (trimmed, capped at 64 chars); "" by default.
- AiBrain module publishes it in ApplyState: the user's name when the brain is enabled and
  a name is set, else "" (clears it) — so toggling AI on/off updates it live.
- Fortunes welcome greets with host.OwnerName when set, else falls back to the Windows
  user name (out-of-box behaviour preserved when the brain is off).
- All IHost stubs (CompanionHost + 4 self-test recording hosts) implement the new members.

Timing: modules Init (AiBrain publishes) before the first pet spawn (Fortunes welcome
reads), so the very first greeting already uses the configured name.

Verified: clean -Release; --wpf-options-selftest / --module-host-selftest /
--fortunes-engine-selftest / CoreTests green; dev install refreshed.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>

### PR #36 — fix(host): repeat guard actually defaults ON (nullable SuppressRepeats)

`closed` · opened 2026-08-10

fix(host): make the repeat guard actually default ON (nullable SuppressRepeats)

The "don't repeat the same message" guard was silently disabled: settings written before
the field existed have no "suppressRepeats" key, and the plain-bool + DefaultValueHandling
.Populate default didn't apply on load, so GetSuppressRepeats() returned false and the host
dedupe never ran — the same AI quip kept repeating.

Make SuppressRepeats a bool? (nullable): absent/null is distinct from an explicit false,
and GetSuppressRepeats() returns `SuppressRepeats ?? true`, so the guard is ON by default
for any existing doc without a settings edit. No reliance on Newtonsoft default-population.

Verified: clean -Release; --wpf-options-selftest / --module-host-selftest / CoreTests green;
dev install refreshed (existing settings now dedupe without any change to the file).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>

### PR #37 — refactor(s5b-3): retire FormOptions dialog + WebView2 layer

`closed` · opened 2026-08-10

## What

Retires the legacy WinForms `FormOptions` dialog and the WebView2 rendering
layer. Settings is now WPF-only (`DesktopPet.Wpf.OptionsShell`, already the
live UI). This is a pure-deletion refactor: no code was relocated.

## Deleted

- `src/Portable/FormOptions.cs` (+ `.designer.cs`, `.resx`) — the classic
  WinForms options dialog.
- `src/Portable/Options/FortunesWebView.cs` (`FortunesWebView` +
  `FortunesWebViewSelfTest`) and `src/dotNet/WebViewHost.cs` (`WebViewHost` +
  `WebViewSelfTest`) — the WebView2 host/control-center.
- `src/Fortunes/fortunes-view.html` — the embedded WebView2 page.
- `src/dotNet/TrustedPack.cs` — model that only the deleted FormOptions
  fortune-pack install path populated (its fields became never-assigned →
  CS0649 under warnings-as-errors).
- `Microsoft.Web.WebView2` dependency and all its build/packaging wiring
  (csproj items, `runtime-files.txt`, `legal-files.json`,
  `THIRD_PARTY_NOTICES.md`, regenerated `packages.lock.json`).

## Residual helpers/self-tests removed with their owners

- `FortuneProvider.FilterSelfTest`: the three `FormOptions.Run*SelfTest` calls.
- `SecuritySelfTest`: the `QuoteWindowsProcessArgument` assertion, the
  `FetchModelNamesAsync` deadline check, and the entire `CheckTestModelCleanup`
  test plus its now-unused `TestModelBehavior`/`TestModelBackend` helper types.
  Every other SecuritySelfTest check is untouched.
- CI (`build.yml`) and `tests/runtime-hardening-selftest.ps1`: dropped the
  `--webview-selftest` / `--fortunes-webview-selftest` runs and the
  `FormOptions.cs` source-text invariant.

## Verification

- `build.ps1 -Release`: **Build succeeded, 0 warnings, 0 errors** for base +
  Contracts + Fortunes + AiBrain modules.
- App self-tests (all exit 0): `--security-selftest`, `--filter-selftest`,
  `--wpf-options-selftest`, `--module-host-selftest`,
  `--fortunes-engine-selftest`, `--hardening-selftest`,
  `--pettyperegistry-selftest`.
- CoreTests: **PASS: 23 DesktopPet core regression groups**, exit 0.
- `WebView2` no longer appears in the build output or `packages.lock.json`;
  zero code references to `FormOptions` / `FortunesWebView` / `WebViewHost` /
  `WebView2` remain (only historical comments).

Net diff: 18 files changed, ~5.5k lines removed.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #38 — docs(backlog): Triumph insult-comic persona + persona x speech combinations

`closed` · opened 2026-08-10

Backlog entry #12: a 'Triumph' insult-comic personality preset + the personality x speech combination concept (Triumph personality + Samuel speech = profane roast). Docs-only.

### PR #39 — refactor(cleanup): del dead FormOptions controls + legacy tree; fix 2 CTS disposals

`closed` · opened 2026-08-10

refactor(cleanup): delete dead FormOptions controls + legacy tree; fix two CTS disposals

Audit follow-up, bucket 1 (safe deletions + resource fixes):
- Delete DarkTabControl + DarkNumericUpDown — 0 code consumers (they were FormOptions-only
  custom controls; FormOptions was retired). Drop their csproj Compile entries.
- Delete src/legacy/ — the old net48/UWP monolith tree; not in any build (build.ps1 builds
  only the portable csproj + the 3 module csprojs; no .sln, no CI ref).
- Dispose CancellationTokenSources that were only cancelled: AiBrainModule._lifetime
  (Shutdown now Cancel()+Dispose()) and CompanionsPaneControl._netCts (Unloaded now
  Cancel()+Dispose()+null).

Verified: clean -Release (base + Contracts + Fortunes + AiBrain); --wpf-options-selftest /
--module-host-selftest / CoreTests green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>

### PR #40 — refactor(cleanup): strip residual base AI-brain cluster + fortune engine + OptionsController seam

`closed` · opened 2026-08-11

Pure-deletion cleanup of dead residue left over from the plugin-host migration (modules own the brain and fortunes). Build stays green with **0 warnings / 0 errors**.

## GROUP 1 — base AI-brain build/trigger residue (`StartUp.cs`)
Removed the dead brain-BUILD + trigger surface: `CreateBrain`, `SelectedEndpoint`, `CanUseAiConfiguration`, `Observe`, `AskAboutScreen`/`AskAboutScreenAsync`, `EmoteAll`/`EmotionAnimations`, `ApplyAiTriggers`, `ScheduleIdle`, `IdleTimer_Tick`, the public `SetAiBrainEnabled` + `AiBrainEnabled` property (0 external callers), and the now-dead fields `aiHotkey`, `aiIdleTimer`(+handler), `aiLastInteractionUtc`, `idleSchedule` (+ Dispose cleanup).

**Kept the RETIRE path:** `ApplyAiBrainState` still calls `aiSession.ReconfigureAsync(null, false, false, …)` so any prior brain is torn down and history is cleared on request. Simplified the dead `allowed`/`prepare`/`CreateBrain` factory away. `PlayAnimationOnAll` stays (CompanionHost service).

### ⚠ Brain FILES deliberately kept (surprise live consumer)
`AiBrain.cs`, `BrainResponse.cs`, `ICompanionBrainBackend.cs`, `AiExecutablePolicy.cs`, `OllamaClient.cs`, `OpenAiCompatBackend.cs` were **NOT** deleted. The KEPT `AiSessionManager` embeds `AiBrain` in its type surface (`Func<AiBrain>` factory, `AiBrain _brain`, `RetireBrainAsync(AiBrain)`) and returns `BrainResponse`; `AiBrain` in turn requires `ICompanionBrainBackend`/`BrainResponse`/`AiExecutablePolicy` (tesseract OCR resolution). These are also exercised by kept `SecuritySelfTest` sections and linked by `Tools/PetTester`. Deleting them would break the KEPT `AiSessionManager`, so per the stop-and-report guidance they stay. **`SecuritySelfTest.cs` was left unmodified** (all its AI tests target kept classes and keep passing).

## GROUP 2 — base fortune engine (module owns fortunes)
- Deleted `FortuneFileImporter.cs` (only consumer was the deleted `FortuneProvider.FilterSelfTest`).
- Reduced `FortuneProvider.cs` (base engine + `FortuneEntry`/`SourceStat`/`GenreStat`/`FortuneTaxonomy`/`FortuneClassifier` + self-tests) to the one live type, renamed to **`FortunePackLoadPolicy.cs`**. `RemoteCatalog.cs` consumes `FortunePackLoadPolicy.TryValidatePackMetadata`/`MaximumFileBytes` for catalog pack bounds. Verified **`RemoteCatalog`/`PackCollections` do NOT reference base `FortuneProvider`**.
- csproj: swapped the Compile entry, removed `FortuneFileImporter.cs`, removed the orphaned embedded resources `Fortunes\fortunes.txt` and the classifier-parity TSV (`DesktopPet.ClassifierParity.tsv`).
- `Program.cs` + `build.yml`: removed `--filter-selftest` and `--fortunecache-selftest`.

## GROUP 3 — OptionsController seam (self-test-only except CompanionsController)
- Deleted the `OptionsController` façade, `PreferencesController` (+`PreferencesState`), `FortunesController` (+`SourceStatus`/`SourceRow`/`GenreRow`/`FortunesState`), and `OptionsSelfTest`. **Kept `CompanionsController`** (used by `Portable/Wpf/CompanionsPaneControl.cs`) + deps (`ICompanionRuntime`, `ICatalogService`, `OpResult`/`OpResult<T>`, `CompanionRow`, `CompanionsState`).
- `Program.cs` + `build.yml`: removed `--options-selftest` (+ orphaned `DESKTOPPET_DATA_ROOT` setup).

## Gate results
- `build.ps1 -Release` → **Build succeeded, 0 Warning(s), 0 Error(s)** (base + Contracts + Fortunes + AiBrain + TestModule).
- Self-tests all exit 0: `--security-selftest`, `--wpf-options-selftest`, `--module-host-selftest`, `--fortunes-engine-selftest`, `--hardening-selftest`, `--pettyperegistry-selftest`, `--catalog-selftest`.
- CoreTests: `PASS: 23 DesktopPet core regression groups`, exit 0.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #41 — refactor(cleanup): collapse ContextMenus to PORTABLE-only + FormHelp guard (bucket 1b)

`closed` · opened 2026-08-11

﻿## Bucket 1b: dead conditional-compilation strip + FormHelp guard

Audit follow-up. `ContextMenus.cs` still carried `#if !PORTABLE` (UWP) branches that have been dead since the .NET 10 port.

**Changes**
- Remove every `#if !PORTABLE` branch: the UWP `Launcher.LaunchUriAsync` shim (`OpenOptionWindow`, driven by `xamlesheep://` URIs), the `Windows.Storage` `LocalData` ctor field, and the four `Windows.*` UWP usings. All dead in the shipping PORTABLE build.
- Delete the first-boot auto-open of the options window — it called the PORTABLE `OpenOptionWindow` **stub** (a no-op), so it never did anything.
- **FormHelp fix:** Help had no re-entry guard and used modeless `Show()` (never disposed). Now matches About/Options — an `isHelpLoaded` guard + `using` + `ShowDialog()`. Added the `isHelpLoaded` field; About / Options / Help are now mutually exclusive.

Net: **30 insertions, 72 deletions**, one file.

**Verification (all green, local):**
- `build.ps1 -Release` — base + Contracts + Fortunes + AiBrain, 0 warnings / 0 errors
- `--module-host-selftest`, `--wpf-options-selftest`, `--hardening-selftest` — exit 0
- `tests/runtime-hardening-selftest.ps1` (source-invariant grep incl. ContextMenus.cs) — exit 0
- CoreTests console harness — 23 regression groups PASS

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #42 — fix(fortunes,legal): ship ONNX Runtime license + notices with the module

`closed` · opened 2026-08-11

﻿## ONNX Runtime license/notices compliance fix

The Fortunes module redistributes the ONNX Runtime — native `onnxruntime.dll` (15.8 MB) + managed `Microsoft.ML.OnnxRuntime.dll` — but shipped **no copy of its MIT license or third-party notices**. The base `DesktopPet_Portable.csproj` already carried a comment claiming *"ONNX runtime licenses now ship with the Fortunes module (they moved with the engine, S3d)"*, but no project actually copied them, so the claim was false and the redistribution was non-compliant.

**Fix**
- Add `GeneratePathProperty="true"` to the module's `Microsoft.ML.OnnxRuntime` PackageReference.
- Copy from the restored NuGet package into `modules/fortunes/`, beside the binaries they cover:
  - `LICENSE` → `ONNXRUNTIME_LICENSE.txt` (1094 B)
  - `ThirdPartyNotices.txt` → `ONNXRUNTIME_THIRD_PARTY_NOTICES.txt` (331 175 B)
- Same pattern the base already uses for Newtonsoft; version-pinned to the package (1.28.0) so the text never drifts.

This is exactly what the orphaned `packaging/legal-files.json` already declared for these two outputs.

**Verification**
- `build.ps1 -Release` — 0 warnings / 0 errors.
- Both files present in the module output, sizes matching the 1.28.0 package.
- `--module-host-selftest` — exit 0 (Fortunes module still loads).

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #43 — docs(backlog): record cleanup audit + queue Provider/OCR features

`closed` · opened 2026-08-11

﻿Docs-only. Records the post-conversion cleanup audit in the repo backlog and captures two unbuilt queued features so they are not lost.

- **Maintenance entry** `✅ DONE (2026-08-10) — Post-conversion cleanup audit` covering PRs #39/#40/#41/#42, and the deliberately-deferred **AI-cluster / Newtonsoft→STJ** decision (the base `src/dotNet/Ai/` cluster is dead-but-anchored and duplicated by the live module; removing it is the planned S5c/d/e "AiSettings split", not a safe delete).
- **Queued feature #13** — AI provider redesign: rename to **Local provider**, add a **Cloud provider** section + a **"use local provider as fallback"** toggle.
- **Queued feature #14** — bundle a portable OCR engine (Tesseract) inside the AiBrain module + a "Choose OCR engine…" picker (the Test-OCR button + picker shipped this session; bundling is what remains).

No code or gate impact.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #44 — test(aibrain): relocate the base AI security suite into the module probe (S5c ph1)

`closed` · opened 2026-08-11

﻿## S5c Phase 1 (expand) — relocate the AI security suite into the module

Groundwork for removing the dead base AI-brain cluster (`src/dotNet/Ai/*`, duplicated by the live `modules/AiBrain/engine/*`). The base's ~50 AI **security** assertions in `SecuritySelfTest.cs` currently test the *dead* base copies. Before deleting that code (a later phase), relocate those assertions into the live module's `--aibrain-selftest` so they exercise the **shipping** engine — zero coverage loss.

**This phase touches `modules/AiBrain/` only — the base is byte-for-byte untouched.**

### What moved
- `engine/AiEngineProbe.Security.cs` (new) — `RunSecurity` + all 12 relocated check-methods: endpoint reject/SSRF, DPAPI-failure ciphertext preservation + no-plaintext-key + corrupt-primary/future-schema resilience, credential scoping + scope-count bound, normalization/clamping incl. CRLF-injection reject, executable allow-list (UNC/device/reparse reject), response sanitize/bounds (Content-Length lie, invalid UTF-8), read deadlines, HTTP-retry policy, and the session retire/dispose/after-retire durability races.
- `engine/AiSelfTestDoubles.cs` (new) — the module's own copies of the backend + HTTP-handler test doubles.
- `engine/AiEngineProbe.cs` — made `partial`; `Run` now also calls `RunSecurity`.

Ported ~verbatim against the module's parity impls + `*ForSelfTest`/`*ForDiagnostics` hooks; **no assertion weakened**. `CheckIdleScheduleGeneration` deliberately stays in the base (it tests `StartUp.GenerationAwareIdleSchedule`, not `Ai/*`).

### Verification (all green, local)
- `build.ps1 -Release` — 0 warnings / 0 errors (base + 3 modules).
- `--aibrain-selftest` — **RESULT=PASS**, 80 PASS / 0 FAIL (the relocated invariants appear and pass).
- `--security-selftest` (base unchanged) — **PASS**.
- `tests/runtime-hardening-selftest.ps1` — exit 0; CoreTests — 23 groups PASS.
- `git diff` = `modules/AiBrain/` only.

Part of the S5c "AiSettings split" stream (phases: 1 relocate tests → 2 rehome random-drop → 3 delete the base cluster). Newtonsoft→STJ is out of scope.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #45 — refactor(settings): rehome random-drop cadence into settings.json (S5c ph2)

`closed` · opened 2026-08-11

﻿## S5c Phase 2 — rehome the random-drop cadence into settings.json

The **random-drop trio** (`RandomDropEnabled`/`RandomDropMinutes`/`RandomDropJitterMinutes`) is the *only* non-AI setting the base still reads out of the `AiSettings` blob. Move it into the base's own store so deleting the AI cluster (phase 3) doesn't touch it.

### Changes
- **`AppSettingsDocument`** — three nullable fields (`randomDropEnabled`/`Minutes`/`JitterMinutes`, Order 20-22) modeled on `suppressRepeats`; `CreateDefault` = off / 15 / ±3; `NormalizeRandomDrop` clamps interval `1..9999` and jitter `0..center-1`; wired into `Clone` + cross-process `MergeChangedFields`.
- **`LocalData`** — `GetRandomDrop*` / `SetRandomDrop` accessors + `MigrateRandomDropIfAbsent`: a one-time, self-contained bridge that seeds the fields from the legacy `ai-settings.json` when they're absent (null), else the defaults. No `AiSettings` dependency, so it survives phase 3.
- **`StartUp`** — `ApplyRandomDrop()` / `ScheduleDrop` read the cadence from `Program.MyData` (LocalData) instead of `aiConfig`; the three callers drop the `AiSettings` arg.
- **`OptionsShell`** Preferences pane reads / writes / resets random-drop via `LocalData.SetRandomDrop`; dropped the now-unused `using DesktopPet.Ai`.
- **CoreTests** — new `Settings random-drop validation` group (fresh defaults, custom round-trip, interval + jitter clamp, and absent-keys-load-as-null so the migration signal is preserved) → **24 groups**.

After this the base no longer reads `AiSettings` for random-drop (only the phase-3-doomed `MemoryEnabled` read remains). Newtonsoft→STJ untouched (out of scope).

### Verification (all green, local)
- `build.ps1 -Release` — 0 warnings / 0 errors (base + 3 modules).
- CoreTests — **24 groups PASS**.
- `--wpf-options-selftest` **PASS**, `--module-host` / `--hardening` / `--security` / `--aibrain` self-tests exit 0, `runtime-hardening-selftest.ps1` exit 0.

Part of the S5c stream (1 relocate tests ✅ #44 → **2 rehome random-drop** → 3 delete the base AI cluster).

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #46 — refactor(base): delete the dead AI-brain cluster (S5c ph3)

`closed` · opened 2026-08-11

﻿## S5c Phase 3 (contract) — delete the dead base AI-brain cluster

The final S5c step. The base's AI-brain code was fully duplicated by the live `modules/AiBrain` plugin; its **security tests were relocated into that module** (#44) and its one non-AI setting (**random-drop**) was **moved to settings.json** (#45). Now delete the dead base code.

**Net: ~6.8k lines removed.** Newtonsoft stays (6 non-AI base files still use it) — this is NOT the System.Text.Json migration.

### Deleted (12 files under `src/dotNet/Ai/` + their csproj `<Compile>` entries)
AiBrain, AiSessionManager, AiEndpointPolicy, AiExecutablePolicy, AiProviders, OllamaClient, OpenAiCompatBackend, ICompanionBrainBackend, BrainResponse, ChatHistory, Personas, AiSettings.

### Kept (still live, same folder)
`ActiveWindow` + `HotkeyListener` (back the CompanionHost screen-context/hotkey host services), `PokeReactions` (poke sass), `FortunePackLoadPolicy` (RemoteCatalog).

### `StartUp.cs`
Dropped the dead retire machinery: `aiSession` / `lifetimeCancellation` / `aiConfigurationVersion` / `aiConfig` fields, the AI shutdown block (+ the now-unused `ShutdownBudget` field, CS0414 under warnings-as-errors), all `ApplyAiBrainState` overloads, and the uncalled `ClearAiHistory`. `InitAiTriggers` → `InitDropTriggers` (now only arms the drop timer + land greeting). `ReloadAiSettings` / `RebuildSmartFortunes` (ICompanionRuntime members) keep their signatures and just resync the drop timer. Kept `ApplyRandomDrop`/`ScheduleDrop`/`aiRand`/`RemainingShutdownBudget`/`GenerationAwareIdleSchedule`.

### `SecuritySelfTest.cs`
Removed the 12 AI test methods + their `Run()` calls + the AI-only test doubles + AI-only helpers. **Kept every non-AI section**, the shared HTTP-handler doubles (used by `CheckSecureDownloadDeadline`), `CheckIdleScheduleGeneration` (tests `StartUp.GenerationAwareIdleSchedule`, not AI), and `CheckCrossSessionLock`. The AI security coverage now lives in the module's `--aibrain-selftest`.

The base is now AI-cluster-free (verified: no `src/` references to the deleted types remain).

### Verification (all green, local)
- `build.ps1 -Release` — 0 warnings / 0 errors (base + 3 modules).
- `--security-selftest` **PASS** (non-AI half, 0 FAIL); `--aibrain-selftest` **PASS**; `--module-host` / `--wpf-options` / `--hardening` exit 0.
- CoreTests — **24 groups PASS**; `runtime-hardening-selftest.ps1` exit 0; `--resource-churn-selftest` exit 0.

Completes the S5c stream (1 relocate tests ✅ #44 → 2 rehome random-drop ✅ #45 → **3 delete the cluster**).

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #47 — docs(backlog): record S5c base AI-cluster removal as done

`closed` · opened 2026-08-11

Docs-only. Adds a maintenance entry for the completed S5c stream (PRs #44/#45/#46): AI security tests relocated into the module, random-drop rehomed to settings.json, ~6.8k-line dead base AI cluster deleted. Newtonsoft stays (STJ later). No code/gate impact.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #48 — refactor(json): migrate 5 straightforward base files to System.Text.Json (cleanup 1a)

`closed` · opened 2026-08-11

﻿## Cleanup 1a — migrate the 5 straightforward files off Newtonsoft to System.Text.Json

First step of dropping Newtonsoft.Json from the base (STJ is in-box on .NET 10, no package needed). The hard file (`AppSettingsStore`) and the actual package removal are **PR 1b**; this migrates the five simple consumers so the base still builds with Newtonsoft present (kept until 1b).

### Changes
- **New `src/Portable/JsonRead.cs`** — lenient STJ readers (`Str`/`IntOrNull`/`BoolOrNull`) that mirror Newtonsoft's null-tolerant `JToken` casts: a missing key or wrong-kind value yields the fallback (`""`/`null`) instead of throwing, so one malformed field never aborts a whole parse.
- **`RemoteCatalog.cs`** — `JObject`/`JArray`/`JToken` DOM + `(string)`/`(int?)` casts → `JsonNode`/`JsonArray` + `JsonRead`; `catch (Newtonsoft.Json.JsonException)` → `System.Text.Json.JsonException`; guards null array elements.
- **`PackCollections.cs`** — embedded `collections.json` DOM parse → `JsonNode` + `JsonRead`.
- **`LocalData.cs`** — the legacy `ai-settings.json` random-drop migration read → `JsonNode` + `JsonRead`.
- **`Program.cs`** — the resource-churn result-marker `JObject` → `JsonObject` + `ToJsonString(WriteIndented)`.
- **`CompanionHost.cs`** — `JsonConvert.Serialize/Deserialize<Dictionary<string,string>>` → `JsonSerializer`.

### Verification (all green, local)
- `build.ps1 -Release` — 0 warnings / 0 errors (base + 3 modules).
- `--catalog-selftest` → **`catalog_parse=PASS`** (exercises `RemoteCatalog.Parse` + the malformed-JSON reject cases through the new `JsonException` catch).
- `--resource-churn-selftest` (the `Program.cs` marker) exit 0; `--module-host` / `--wpf-options` / `--hardening` / `--security` / `--aibrain` / `--fortunes` / `--fortunes-engine` / `--fullscreen` exit 0.
- CoreTests — 24 groups PASS; `runtime-hardening-selftest.ps1` exit 0.

Newtonsoft package stays until PR 1b.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #49 — refactor(json): AppSettingsStore -> System.Text.Json + drop Newtonsoft from the base (cleanup 1b)

`closed` · opened 2026-08-11

﻿## Cleanup 1b — migrate AppSettingsStore to System.Text.Json + drop Newtonsoft from the base

The last and hardest Newtonsoft consumer, then the package removal. STJ is in-box on .NET 10, so the base now ships **zero third-party JSON**.

### `AppSettingsStore.cs` (the versioned settings store — also recompiled into CoreTests)
- 22 doc fields + `CompanionCountEntry`/`CompanionSizeEntry`: `[JsonProperty("x",Order=n)]` → `[JsonPropertyName("x"), JsonPropertyOrder(n)]`. Public **fields** need `IncludeFields=true` (STJ ignores fields otherwise → a silent empty write), set on a shared `JsonSerializerOptions`.
- `[JsonExtensionData] IDictionary<string,JToken>` **field** → `Dictionary<string,JsonElement>` **property** (STJ requires a property); `Clone`'s `JToken.DeepClone` → `JsonElement.Clone`. The future-schema unknown-field round-trip is preserved.
- Read: `JsonTextReader{MaxDepth=32,DateParseHandling=None}` → `JsonSerializer.Deserialize(json, {MaxDepth=32})`. Write: `JsonConvert.SerializeObject(Formatting.Indented)` → `JsonSerializer.Serialize({WriteIndented, UnsafeRelaxedJsonEscaping})`. **Default null handling kept**, so the nullable absent-vs-null distinction (`suppressRepeats`/`randomDrop*`) is preserved. Output isn't byte-identical to Newtonsoft → a one-time settings-file rewrite (nothing hashes the bytes). Stays **C# 7.3-clean** for the CoreTests recompile.
- CoreTests harness (`Program.cs`) on-disk `JObject`/`JArray` verification → `JsonNode`/`JsonArray`.

### Package removal
Dropped Newtonsoft from the base + CoreTests PackageReferences, the base license `<Content>`, and the packaging manifests (`runtime-files.txt` / `legal-files.json` / `THIRD_PARTY_NOTICES.md`); both `packages.lock.json` regenerated.

> **Scope note:** the AiBrain plugin keeps its **own** Newtonsoft in its own AssemblyLoadContext (`modules/aibrain/`). A separate follow-up stream will migrate the modules so the whole product is Newtonsoft-free.

### Verification (all green, local)
- `build.ps1 -Release` — 0 warnings / 0 errors; the payload-manifest check confirms **no `Newtonsoft.Json.dll` in the base output**.
- CoreTests — **24 groups**, freshly built (defaults / one-time migration / atomic backup / corrupt-primary recovery / **future-schema preservation** / random-drop nullable-absent all pass).
- `--module-host` / `--wpf-options` / `--hardening` / `--security` / `--aibrain` / `--catalog` / `--fortunes` exit 0; `runtime-hardening-selftest.ps1` + `--resource-churn-selftest` exit 0.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #50 — refactor(json): migrate the AiBrain module off Newtonsoft (product Newtonsoft-free) (cleanup 1c)

`closed` · opened 2026-08-11

﻿## Cleanup 1c — migrate the AiBrain module off Newtonsoft (product now Newtonsoft-free)

Completes the Newtonsoft → System.Text.Json drop. The base went in #48/#49; the **AiBrain plugin** was the last Newtonsoft user (it carried its own copy in its AssemblyLoadContext). After this, **zero `Newtonsoft.Json.dll` anywhere in the Release tree**. Module-only change; STJ is in-box on .NET 10.

### Changes (`modules/AiBrain/`)
- **OllamaClient / OpenAiCompatBackend** — `JObject`/`JArray` request payloads → `JsonObject`/`JsonArray` + `ToJsonString`; response parse → `JsonNode` + a module-local lenient `JsonRead.Str`.
- **AiBrain.cs** — model-reply `JObject.Parse` → `JsonNode.Parse`; `{text,emotion}` read leniently.
- **ChatHistory.cs** — `[JsonIgnore]` → STJ; `JsonConvert` → `JsonSerializer` (runtime-type overload so it doesn't bind `object`→`{}`); on-disk envelope preserved.
- **AiSettings.cs (the DPAPI credential store)** — attributes → `[JsonPropertyName]`(+`Order`); public fields → `IncludeFields`; `[JsonExtensionData]` field → `Dictionary<string,JsonElement>` property; the stale-writer **merge engine** ported: `JObject.FromObject` → `JsonSerializer.SerializeToNode`, `JToken.DeepEquals` → **`JsonNode.DeepEquals`** (.NET 9), `DeepClone` before re-parenting, and the credential-scope merge **mutates `ApiKeysEnc` in place** when target already holds it (STJ throws on re-parenting an attached node). Default null handling kept.
- **AiEngineProbe.Security.cs** — the DPAPI-ciphertext-injection probe `JObject` → `JsonNode`.
- **AiBrain.csproj** — dropped the Newtonsoft PackageReference.

### Verification (all green, local)
- `build.ps1 -Release` — 0 warnings; **no `Newtonsoft.Json.dll` anywhere in the Release tree**.
- **`--aibrain-selftest` — RESULT=PASS, 80 / 0** — the ~50 relocated security assertions pass against the STJ merge: **provider-scoped + legacy ciphertext preserved byte-for-byte on DPAPI failure**, credential-scope merge, chat-history credential partitioning, and **no plaintext keys** on disk.
- `--module-host` / `--fortunes` / `--security` / `--wpf-options` / `--hardening` exit 0; CoreTests 24 groups; `runtime-hardening-selftest.ps1` exit 0.

The whole product is now Newtonsoft-free.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #51 — refactor(ui): move About + Help to the WPF shell; retire the WinForms dialogs (cleanup 2)

`closed` · opened 2026-08-11

﻿## Cleanup 2 — move About + Help to the WPF shell; retire the last auxiliary WinForms dialogs

The final cleanup stream. About + Help become themed WPF windows on the existing shell, so the only WinForms left is the **pet engine** (`FormCompanion`/`FormSpeech`) + the dev-only **FormDebug** console (kept). (WebView2 + the old `FormOptions` were already retired in S5b-3.)

### Changes
- **New `src/Portable/WebLinks.cs`** — one security-reviewed link helper shared by the WPF windows + the security self-test: `TryNormalizeHttpsLink` (HTTPS + non-empty host + no-userinfo + ≤2048, **copied verbatim** from the old AboutBox), `TryOpen` (any HTTPS), `TryOpenProjectDoc` (adds the `github.com/bigfnj/desktopPet` allowlist from FormHelp).
- **New `src/Portable/Wpf/AboutWindow.cs` + `HelpWindow.cs`** — programmatic WPF windows mirroring `OptionsWindow` (`WpfTheme` dark chrome, `ScrollViewer`, Close). About shows the version + the current pet's author/title/version/info (`[br]`/`[link:]` markup → WPF inlines/Hyperlinks) + the fixed repo/esheep links; Help reproduces the offline text + the allowlisted doc links.
- `OptionsShell.OpenAbout(author,title,version,info)` / `OpenHelp()` mirror `OptionsShell.Open()`.
- `ContextMenus` `About_Click`/`Help_Click` call the new entry points (re-entry guards + the `author/title/version/info` statics kept).
- **Deleted** `AboutBox` + `FormHelp` (`.cs`/`.designer.cs`/`.resx`) + their csproj entries.
- `SecuritySelfTest.CheckAboutLinkPolicy` → `WebLinks.TryNormalizeHttpsLink` (same assertions).
- resource-churn `RunCycle`: dropped the hidden AboutBox/FormHelp construction (+ the now-unused about/help counters); speech/pet/tray/menu churn intact.
- Doc cleanup: `BACKLOG.md` + `handoff.md` corrected (WebView2/FormOptions retired; About/Help now WPF).

### Verification (all green, local)
- `build.ps1 -Release` — 0 warnings (base + 3 modules).
- `--security-selftest` **PASS** — *"pet-supplied About links allow only HTTPS without userinfo"* (the relocated `WebLinks` policy).
- `--wpf-options` / `--module-host` / `--hardening` / `--aibrain` / `--fortunes` exit 0; CoreTests 24; `runtime-hardening-selftest.ps1` exit 0; `--resource-churn-selftest` exit 0.
- `AboutBox`/`FormHelp` no longer compile (only referenced in explanatory comments).

> ⚠ **Needs a human eyeball:** WPF rendering can't be checked headlessly. After merge, run the app and open **tray → About** (version in the title bar, pet author/title/version/info, working repo/esheep links, dark theme, Close) and **tray → Help** (offline text + clickable github doc links, dark theme, Close).

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #52 — docs(backlog): record the Newtonsoft drop + About/Help->WPF cleanup

`closed` · opened 2026-08-11

Docs-only maintenance entry for PRs #48/#49/#50/#51 (Newtonsoft dropped product-wide; About/Help moved to WPF). No code/gate impact.

### PR #53 — refactor(ui): fold Help into About + reorder the About window

`closed` · opened 2026-08-11

﻿## Fold Help into About + reorder the About window

Smoke-test feedback: one tray dialog, not two. The tray **Help** item + the WPF `HelpWindow` are gone; the tray now has a single **"About / Help"** entry and the usage/help content lives inside the About window.

**New About layout (top → bottom):**
1. **AI Edition concept & build by BigFN'j** + the .NET 10 modernization line + a short project paragraph + the project link.
2. **Using DesktopPet** — the usage bullets + the allowlisted github doc links (folded in from the retired `HelpWindow`).
3. **Original / Legacy** — the upstream credits (Nomura / Petrucci / Grunwaldt + NAudio + eSheep), moved down from the top and relabeled.
4. **Information about the current pet** — the author/title/version/info card, now at the **very bottom** (was near the top).

- Deleted `HelpWindow.cs` + its csproj entry; `OptionsShell.OpenHelp` removed; `ContextMenus` dropped the Help item + `Help_Click` + `isHelpLoaded`. `AboutWindow` widened to 560×640 (scrolls).

**Verified:** clean `-Release` (0 warnings); `--security` (About-link policy) / `--wpf-options` / `--module-host` / `--hardening` / `--aibrain` / `--fortunes` exit 0; CoreTests 24; hardening ps1; zero `HelpWindow` references remain. Reinstalled + smoke-tested locally (user-confirmed).

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #54 — feat(aibrain): add the Triumph insult-comic personality preset

`closed` · opened 2026-08-11

Adds a **"Triumph"** preset to `AiBrainModule.PersonalityPresets` (BACKLOG #12) — Triumph the Insult Comic Dog: mock-compliment then savage roast of whatever's on screen + the user, with the "for me to POOP on!" catchphrase.

It's a **personality** (tone), so it stacks with the existing **speech** styles: **Triumph personality + "Samuel" speech = a relentlessly profane roast**, the exact combination requested. Opt-in (default persona unchanged); the system prompt already backs a strong persona (*"commit to it fully… never merely polite"*), so no prompt change was needed.

**Verified:** clean `-Release` (0 warnings); `--aibrain-selftest` / `--wpf-options` / `--module-host` / `--hardening` exit 0; CoreTests 24; hardening ps1. BACKLOG #12 marked done.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #55 — feat(aibrain): Local + Cloud provider slots coexist (schema v2) — provider redesign PR A

`closed` · opened 2026-08-11

**PR A of the AI provider redesign (BACKLOG #13)** — make a Local provider and a Cloud provider coexist, cloud-primary when selected. Settings + migration + pane + routing only; the runtime cloud→local **fallback backend is PR B**.

### AiSettings (schema v1 → v2)
- `Provider` is now the **cloud selector** `{""|openai|openrouter|custom}` (`""` = local-only). The **local slot** is the fixed `Endpoint`/`TextModel`/`VisionModel` (Ollama). New `CloudTextModel`/`CloudVisionModel` + `UseLocalFallback` (default true).
- **One-time v1→v2 migration** (`Normalize`): an old cloud id keeps its slot and promotes the old `TextModel`/`VisionModel` into the cloud slot (local models reset to defaults) — the credential **scope hash is unchanged**, so the existing key stays valid; an old local id → `""`. The credential machinery (`ApiKeysEnc`/`BuildCredentialScope`/`TrySetApiKey`/32-scope cap) is mechanically unchanged. Future-schema (v99) docs aren't migrated and stay write-blocked.

### Pane
Split into **Local provider** (endpoint + local models + useVision), **Local server (Ollama)** (autostart/preload), **Cloud provider** (`(none)`/openai/openrouter/custom + cloud endpoint + API key + cloud models + consent), and **Fallback** (use-local-as-fallback). Load/Save round-trip both slots; the cloud key is set after the provider/endpoint so it targets the cloud scope.

### Routing
`CreateBrain`: `Provider==""` → local `OllamaClient`; else cloud `OpenAiCompatBackend`, using the active slot's models via a read-only `ActiveSlotSnapshot`. Exactly one backend — **no composite** (that's PR B).

### Verification
- `build.ps1 -Release` — 0 warnings.
- `--aibrain-selftest` — **82 PASS / 0 FAIL** (all existing security assertions + a new migration test that seeds a real DPAPI key and proves it still resolves post-migration + a cloud-slot round-trip).
- `--wpf-options` / `--module-host` / `--hardening` / `--security` / `--fortunes` exit 0; CoreTests 24; hardening ps1. Module-only diff.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #56 — feat(aibrain): cloud->local fallback backend — provider redesign PR B

`closed` · opened 2026-08-11

**PR B (final) of the AI provider redesign (BACKLOG #13)** — the runtime cloud→local fallback.

### What it does
When a cloud provider is primary and **Use local provider as fallback** is on, a *retryable* cloud failure fails over to the local Ollama model; a *deterministic* failure surfaces as-is.

- **New `engine/FallbackBackend.cs`** (`ICompanionBrainBackend` composite): `ChatAsync` runs the cloud primary; on a retryable failure (timeout / transient HTTP 408·429·5xx / transport) it retries once on the local backend with the **mapped local model** (the cloud vision model → the local vision model, else the local text model); a **deterministic** failure (non-transient 4xx/redirect — e.g. a bad key) rethrows **without** failing over. `IsAvailable` = either leg up; `EnsureServer` readies the local leg too; `WarmUp`/`Unload`/`Dispose` fan out.
- **Shared classifier:** extracted `AiEndpointPolicy.IsRetryable(ex, ct)` and refactored `AiBrain.ChatWithRetryForDiagnosticsAsync`'s four `catch`-`when` clauses to use it, so the brain's own retry and the fallback classify failures identically (behavior unchanged — the HTTP-status self-tests confirm it).
- **`CreateBrain`:** cloud primary + `UseLocalFallback` + a valid loopback local endpoint → wrap the cloud backend in `FallbackBackend`; otherwise cloud-only (or local-only when no cloud). `AiBrain` still sees one backend.
- **Probe:** `TransientFailBackend` + `RecordingBackend` doubles + `CheckFallbackBackend` — transient→local(text), vision→vision mapping, deterministic→surfaces (local untouched), available-when-local-up.

### Verification
- `build.ps1 -Release` — 0 warnings.
- `--aibrain-selftest` — **86 PASS / 0 FAIL** (all prior security assertions + the 4 new fallback ones).
- `--wpf-options` / `--module-host` / `--hardening` / `--security` / `--fortunes` exit 0; CoreTests 24; hardening ps1. Module-only diff.

Completes BACKLOG #13 (Local + Cloud coexist, cloud-primary, local fallback).

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #57 — docs(backlog): mark #13 (AI provider redesign) done

`closed` · opened 2026-08-11

Docs-only: mark BACKLOG #13 (Local + Cloud coexist, cloud-primary + local fallback) done (PRs #55/#56).

### PR #58 — fix(aibrain): local slot was hardcoded to Ollama; restore llama.cpp/LM Studio

`closed` · opened 2026-08-11

Fixes a regression the user caught while smoke-testing the provider redesign: the LOCAL slot had been hardcoded to `OllamaClient`, unconditionally.

### The regression
Before the provider redesign (#55/#56), the single `Provider` enum had `lmstudio`/`llamacpp` as valid **local** ids served by the generic `OpenAiCompatBackend` — llama.cpp's server and LM Studio both speak the same OpenAI-compatible `/v1` protocol Ollama doesn't use natively. When the local slot was rebuilt as fixed-Ollama, that capability was silently dropped. Ollama is also **not bundled** (confirmed — no `ollama.exe` in any packaging script); `OllamaPath` just autodetects it on PATH, so a user without Ollama installed had no local option left at all.

### The fix
- **`AiSettings`** — new `LocalBackendKind` field (`"ollama"` default | `"openai-compat"`), clamped in `Normalize`. **No schema bump needed** — it's a new optional field with a safe default, so an absent key in an old doc keeps `"ollama"` after deserialization (verified). The merge-on-save is fully generic (whole-object diff), so nothing else needed updating there.
- **`AiBrainModule.BuildLocalBackend`** — picks `OllamaClient` (native, gets the auto-start/warm-up/unload lifecycle) or `OpenAiCompatBackend(endpoint, "", timeout)` (generic `/v1`, no key needed — those lifecycle calls are already no-ops on it) based on `LocalBackendKind`. Used at all three local-backend construction sites: `TestConnectionAsync`, `CreateBrain`'s local-only path, and `CreateBrain`'s fallback local leg.
- **Pane** — new **"Local backend"** dropdown (Ollama (native) / Generic OpenAI-compatible) in the Local provider group; relabeled the endpoint field; renamed the autostart/preload group to **"Local server (Ollama only)"**.

### Verification
- `build.ps1 -Release` — 0 warnings.
- `--aibrain-selftest` — **88 PASS / 0 FAIL** (extended the Provider clamp assertion + 2 new checks: an old doc with no `LocalBackendKind` key defaults to `"ollama"`; `"openai-compat"` round-trips through save/reload).
- `--wpf-options` / `--module-host` / `--hardening` / `--security` / `--fortunes` exit 0; CoreTests 24; hardening ps1; resource-churn. Module-only diff.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #59 — docs(backlog): record local-backend fix + queue model dropdowns

`closed` · opened 2026-08-11

Docs-only: records PR #58 (local-slot Ollama lock-in fix) and queues the deferred capability-aware model dropdown work.

### PR #60 — feat(aibrain): capability-aware model dropdowns + uncensored tagging

`closed` · opened 2026-08-11

The AI Brain pane's four model fields (local/cloud text+vision) were free-text — a user could pick a non-vision model for the vision slot, and there was no easy way to find models that actually comply with the profane Samuel/Triumph personas (a heavily-RLHF'd model tends to soften or refuse the roast). Real model-picker dropdowns, capability-filtered, with uncensored-leaning models tagged and sorted to the top — never hidden, since other personas want ordinary models.

### Engine (`modules/AiBrain/engine/`)
- **New `ModelListing.cs`** — one list entry (`Id`, `Vision` as `bool?` — a **real** signal when the backend reports one, `null` when unknown so the caller falls back to the name heuristic).
- **`AiModelPolicy.LooksUncensored`** + `UncensoredModelMarkers` (`dolphin`, `uncensored`, `abliterated`, `unfiltered`), mirroring `LooksVisionCapable`'s exact idiom. Advisory only; empty/unknown → `false`.
- **`OllamaClient.ListModelsAsync`** — `GET /api/tags` (already probed for connectivity; now reads the body) — a **real** vision signal from the response's `"capabilities"` array (confirmed via Ollama's own docs via Context7) when present, else `null` (older server → heuristic).
- **`OpenAiCompatBackend.ListModelsAsync`** — `GET {base}/models` → ids only (no capability metadata generically) + a new test-only diagnostic constructor (injectable `HttpMessageHandler`, mirrors `OllamaClient`'s existing one).

### `AiBrainModule.cs`
- The four model `SettingField` objects are now **retained instance references** (`Kind` Text→Enum) so a refresh can mutate `.Options` in place — the pane's `Schema` is captured once; `PaneView` only re-reads `Options` fresh on a `PaneAction.ReloadPaneAfter` rebuild.
- Two new **"Refresh local/cloud models"** `PaneAction`s (`ReloadPaneAfter = true`).
- **`BuildModelOptions`**: text dropdown = every model (uncensored-tagged ones sorted first, label `"id"` / `"id · uncensored"`); vision dropdown = **only** vision-flagged models (real capability or heuristic) — a non-vision model can never be picked there. **Safety invariant**: the currently-saved value is always unioned into `Options` — the pane's `Enum` field is a **closed, non-editable** `ComboBox`, so a value missing from `Options` would show nothing selected and silently blank the field on save.
- `ModelLabelForId`/`ModelIdForLabel` — the label *is* the id plus a fixed suffix when tagged, so recovering the id is a plain suffix-strip, no lookup table.

### Self-tests
`LooksUncensored` assertions beside the vision ones; a new `FixedJsonResponseHandler` double + `CheckModelListing` proving (1) Ollama's real `capabilities` array is honored for both vision-true and explicitly-vision-false, (2) an absent `capabilities` key yields `Vision=null` (unknown, not a false claim), (3) the generic `/models` response parses ids with no capability metadata.

### Verification
- `build.ps1 -Release` — 0 warnings.
- `--aibrain-selftest` — **92 PASS / 0 FAIL** (up from 88, +4 new, zero existing assertions weakened).
- `--wpf-options` / `--module-host` / `--hardening` / `--security` / `--fortunes` exit 0; CoreTests 24; hardening ps1; resource-churn. Module-only diff.

> ⚠ Needs a manual smoke test with a real Ollama instance (click "Refresh local models", confirm the vision dropdown only offers vision-capable models and uncensored models like `dolphin3:8b` are tagged/sorted first, confirm re-saving after opening the pane never blanks a configured model).

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #61 — docs(backlog): record model dropdowns + uncensored tagging as done

`closed` · opened 2026-08-11

Docs-only: records PR #60 (capability-aware model dropdowns + uncensored tagging) done, flags the pending manual smoke test.

### PR #62 — feat(aibrain): show real Ollama model size (VRAM proxy) in model dropdowns

`closed` · opened 2026-08-11

Ollama's `/api/tags` already reports each model's on-disk size (bytes) — a solid proxy for its VRAM/weight footprint when loaded. Surfaces it in the dropdown label so "will this fit" is answerable at a glance.

> A "Browse for a local file" button was considered and explicitly dropped after discussion: it can't make a file usable by either backend — Ollama requires an import/registration step for a new local file, and a bare llama.cpp server's model is fixed at process launch, not swappable per request — so it would only add a cosmetic label with no functional effect. `ollama pull` + the existing "Refresh models" action already cover real usage.

### Changes
- **`ModelListing`** — new `SizeBytes` (`long?`) — a real value from Ollama's own `"size"` field, `null` when the backend has none (the generic OpenAI-compatible `/v1/models` response carries no size metadata at all).
- **`JsonRead.Int64OrNull`** — Ollama sizes are multi-gigabyte, past `Int32` range.
- **`OllamaClient.ListModelsAsync`** now also parses the response's `"size"` field.
- **Label formatting redesigned** — replaced the old suffix-strip `ModelLabelForId`/`ModelIdForLabel` with `FormatModelLabel`/`ResolveModelId` backed by a label→id dictionary. A variable-length size *prefix* (`"4.9GB · dolphin3:8b · uncensored"`) can't be reversed by a fixed string pattern the way the old uncensored-only *suffix* could; the dictionary is populated as a side effect every time a label is produced (Load's current-value label, and each listed model's label), and Load always runs before Save, so a lookup always succeeds for anything the user could have actually picked.
- **`FormatSize`** — whole MB under 1GB, one-decimal GB above.

### Verification
- `build.ps1 -Release` — 0 warnings.
- `--aibrain-selftest` — **93 PASS / 0 FAIL** (up from 92, +1 — the size parse, both real-value and absent-key-is-null cases; zero existing assertions weakened).
- `--wpf-options` / `--module-host` / `--hardening` / `--security` / `--fortunes` exit 0; CoreTests 24; hardening ps1; resource-churn. Module-only diff.

> ⚠ Still needs the same manual smoke test as the parent model-dropdown feature (PR #60) — open the AI Brain pane against a real Ollama instance and confirm sizes show up correctly in the label.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #63 — feat(aibrain): merge Personality+Speech into one curated Disposition

`closed` · opened 2026-08-11

## Summary
- Samuel's speech pattern re-targeted at Jules Winnfield specifically (profanity as a strong default reflex, not a per-remark hard requirement) — a named, heavily-documented character is a sharper style-transfer target than a generic "Samuel L. Jackson" descriptor.
- New "Jeff Ross" roast-comic personality, then folded into the merged catalog below with its own self-sufficient profanity-forward delivery.
- Raised the remark length cap to one-or-two sentences / ~20 words each (40 max) so a roast has room for a setup and a knockdown.
- Fixed a self-censoring bug: an example word list literally contained "motherf***er" with the censoring asterisks baked in, so the model copied that exact censored spelling.
- **Merged the Personality-preset + Speech-pattern axes into one curated 26-entry "Disposition" dropdown.** The two axes could combine into incoherent pairings (e.g. "Shy and sweet" + "Jules Winnfield"); a single named character per entry (Ted Lasso, Wednesday Addams, Uncle Iroh, Jules Winnfield, Jeff Ross, Etrigan, Foghorn Leghorn, etc.) is both more coherent and a sharper LLM style-transfer target than an abstract tone adjective. `AiSettings` schema v2→v3: `Disposition` (one field) replaces `Personality`+`SpeechPattern`; a legacy `SpeechPattern` id this schema's list absorbed under the same id (samuel/pirate/leet/rhyme/pun/yoda/valley) migrates onto that disposition directly, anything else falls back to the new default (Ted Lasso). New file `Dispositions.cs` replaces `Personas.cs` (its dead `Preset`/`Presets` struct, confirmed zero references, was deleted rather than carried forward).

## Test plan
- [x] `--aibrain-selftest` 93→96 PASS (new disposition-catalog assertions + 2 migration assertions)
- [x] `--wpf-options-selftest` / `--module-host-selftest` / `--hardening-selftest` / `--security-selftest` / `--fortunes-selftest` all exit 0
- [x] Migration verified live on the dev box: old doc (`Personality`+`SpeechPattern:"samuel"`) → `Disposition:"samuel"` (Jules Winnfield) after refresh, confirmed by reading the actual settings file
- [ ] Nothing in this whole AI-voice stream has been observed against a live running model yet — not the merged pane, not any of the 4 brand-new dispositions (The Dude, Drill Sergeant, Foghorn Leghorn, A Proper Butler)

### PR #64 — fix(aibrain): stop forcing the user's name into every remark

`closed` · opened 2026-08-11

## Summary
- User feedback: they don't mind the pet using their name, but didn't want it forced into every single remark.
- `AiBrain.BuildSystemPrompt` literally said "Always address them as <name>" — a hard per-remark requirement. Softened to "use their name only when it actually fits the remark, not in every single one," while keeping the existing safety guard (never invent a name, never read one off the screen/window titles).

## Test plan
- [x] `--aibrain-selftest` 96/96 PASS
- [x] Confirmed no self-test pins the old "Always address them as" wording
- [x] Dev install refreshed

### PR #65 — fix(aibrain): remove chat-memory feature (self-reinforcing repeat loop)

`closed` · opened 2026-08-11

## Summary
- `MemoryEnabled` ("Remember recent remarks") replayed the pet's own past remarks back into its own prompt -- this is exactly what caused the earlier repetition-loop bug this session (previously worked around by turning it off, not by fixing/removing the feature). User feedback: "this caused issues, remove it."
- Since there's nothing left to clear once the feature is gone, also removed the now-pointless "Clear chat history" pane action per follow-up user feedback.
- Deletes `ChatHistory.cs` (945 lines) and every setting/pane/self-test touchpoint. `AiSettings.MemoryEnabled` is gone; no migration needed since a stale key on an old doc is inert (never read again).
- Along the way, swapped two self-tests that used `MemoryEnabled`/`ChatHistory` purely as arbitrary test fixtures onto equivalent unrelated mechanisms (`UseVision` for the stale-writer-merge test; dropped the `partitionA` clause from the credential-scope test, keeping its still-valid plaintext-key assertions).

## Test plan
- [x] Clean build, 0 warnings
- [x] `--aibrain-selftest` all green (net -6 assertions: chat-history-specific ones removed; renamed one credential-scope assertion since it no longer covers "history identity")
- [x] `--wpf-options-selftest` / `--module-host-selftest` / `--hardening-selftest` / `--security-selftest` / `--fortunes-selftest` all exit 0
- [x] Confirmed zero remaining `ChatHistory`/`MemoryEnabled` references anywhere in `modules/AiBrain`
- [x] Dev install refreshed

### PR #66 — fix(tray): add missing icons on Remove a pet + Test Speech

`closed` · opened 2026-08-11

## Summary
- User screenshot showed "Add a pet", "Remove a pet", "Test Speech", "Disable AI", and "Ask about my screen" all missing icons in the tray context menu, while Options/About/Close had them.
- Confirmed with the user that "Add a pet" does show an icon (it already has one in code, kept as-is). The genuine gaps were "Remove a pet" and "Test Speech" -- both simply never had an `.Image` assignment.
- "Disable AI" and "Ask about my screen" are module-contributed via `DesktopPet.Contracts.TrayItem`, which has no icon property at all -- that's a separate, bigger ABI change, not touched here.

## Test plan
- [x] Clean build, 0 warnings
- [x] All 6 self-test gates green (`--aibrain-selftest`, `--wpf-options-selftest`, `--module-host-selftest`, `--hardening-selftest`, `--security-selftest`, `--fortunes-selftest`)
- [x] Confirmed no self-test pins the tray menu's item/image structure
- [x] Dev install refreshed (base runtime files, since this touches the base not a module)

### PR #67 — feat(tray): give every menu item its own distinct icon

`closed` · opened 2026-08-11

## Summary
- User: "I dont want the same icon repeated" -- Remove a pet and Test Speech were reusing the app icon / pet glyph from other items. Drew two new purpose-made icons: a red prohibition sign (Remove a pet) and a speech bubble (Test Speech).
- Disable AI and Ask about my screen (module-contributed via AiBrain) could never show an icon at all -- `DesktopPet.Contracts.TrayItem` had no icon property, by deliberate ABI design (framework-agnostic, no `System.Drawing`). Extended it with `byte[] IconPng` (raw PNG bytes, not a concrete image type) instead, decoded host-side.
- AiBrain now ships two purpose-made icons (red X, tiny monitor) as plain embedded resources, same pattern as Fortunes' `welcome.json`.
- Fixed a real GDI+ gotcha along the way: decoding straight from a `MemoryStream` and disposing the stream immediately can throw "A generic error occurred in GDI+" later, since GDI+ can lazily reference the source stream -- clone into an independent `Bitmap` before the stream disposes.
- The decoded module-icon `Bitmap` is disposed on every menu rebuild (the tray menu rebuilds module items on each open) so repeat opens don't leak.

## Test plan
- [x] Clean build, 0 warnings, all 4 projects (base + 3 modules) compile against the extended ABI
- [x] Confirmed both PNGs are actually embedded (`GetManifestResourceNames()` on the built AiBrain.dll)
- [x] All 6 self-test gates green
- [x] Dev install refreshed (base + AiBrain module)
- [ ] Have not visually confirmed the module-contributed icons render correctly in a live right-click menu yet -- worth a look

### PR #68 — feat(modules): S6 phase 1 -- in-app Modules catalog

`closed` · opened 2026-08-12

## Summary
Neither the MSI nor the portable ZIP has ever shipped Fortunes/AiBrain -- both ship the base pet engine only, with modules existing purely in dev/CI build output. This adds an in-app "Modules" pane as the way a lean host ever gets any, reusing the exact HTTPS/hash-pinned catalog mechanism pets and fortune packs already use rather than statically bundling modules into the installer (the originally-sketched plan). This also absorbs what would have been a separate later "signed catalog + consent" stream -- a catalog that downloads and activates code needs hash-pinning and a permissions-consent step regardless of when it's built.

- `RemoteCatalog` gains a third parallel list (`CatalogModule`, alongside `CatalogCompanion`/`CatalogPack`) carrying each module's declared `ModulePermissions` so the install prompt shows what it can do before any code runs.
- New **Modules** pane: fixed second in nav (after Preferences), installed list + "Check for modules online" + install/uninstall. Everything else (Pets today, any module pane) is now alphabetized in the tail instead of load order, so nav placement is predictable.
- Modules only load at startup, so install/uninstall restarts the app -- reusing `Program.cs`'s `RequestRestart`/`CompleteInstanceLifecycle`/`LaunchReplacement` chain, which existed but had zero real callers before this. Threaded an optional `--reopen-options=<pane>` argument through it so the relaunch reopens Settings back on the Modules pane.
- **Real bug caught in live testing**: Uninstall can't delete a module's DLL immediately since it's locked while loaded in the current process ("access denied"). `PendingModuleRemovals` marks the id instead; the next launch deletes it before `ModuleHost.LoadFrom` ever gets a chance to re-lock it.
- Packaging: `New-ModuleDistZip.ps1` zips a module's build output (excluding .pdb/.lib) into `modules-dist/<id>.zip` -- the exact shape the install flow extracts. `modules-dist/modules.json` carries catalog metadata; `New-ContentCatalog.ps1` now emits a `modules` array in `catalog.json` alongside pets/packs. Published Fortunes + AiBrain as the first two installable modules.

## Test plan
- [x] Clean build, 0 warnings, all 4 projects compile against the extended ABI (`TrayItem` already had a prior unrelated icon addition; `ModuleInfo`/`ModulePermissions` unchanged)
- [x] All 7 self-test gates green, including new assertions: catalog module-entry parsing (valid + a "bad permissions" reject case), restart-payload threading, and the new Preferences/Modules/alphabetized-tail nav ordering
- [x] `--catalog-parse-file=catalog.json` against the real file: `pets=22 packs=152 modules=2`, hashes verified against the actually-committed `modules-dist/*.zip` git blobs
- [x] Live end-to-end on a real dev install: installed modules show correct live name/version, Uninstall (after the fix) correctly restarts and removes the module, Settings reopens back on the Modules pane
- [ ] Live end-to-end "Check for modules online" -> Install -> restart -> confirm restored, against the real published catalog once this merges to master (the catalog fetch is branch-pinned to `master`, so it can't be tested pre-merge)

### PR #69 — feat: arbitrated poke reactions, Trigger Speech, and fortune-pack sourcing

`closed` · opened 2026-08-12

## Summary
Three connected gaps found while smoke-testing S6.

**Right-click did nothing** until the sass ladder kicked in. `CompanionPoked` was a plain broadcast event and only Fortunes acted on it; the AI brain tracked the poke but never spoke. Poke 1 of a session now runs an **arbitrated responder chain** (AI quip → fortune → nothing) on its own ~12s cooldown, deliberately independent of the 7s sass reset so a rich reaction can't fire on every brief pause. The cooldown only advances when something actually spoke, so a silent attempt doesn't leave the next poke mysteriously mute. The 3-4 ignore / 5-11 sass / 12 escape ladder is **untouched**.

**Which module speaks** is now a user choice: `RegisterPokeResponder` mirrors `RegisterDropResponder`, and a **"Trigger Speech"** dropdown (Preferences → Speech) picks the winner. *Default & Random* offers every responder in shuffled order; an explicit choice restricts to that one (declining = silence — a choice is a restriction, not a preference). The list is built from live registrations, so it grows/shrinks with installed modules with no base change. Stored keyed by pet id (`""` = all pets) so per-pet voices (BACKLOG #16) land without a settings migration.

**Fortune packs had no acquisition path at all** — 152 packs in the catalog, no way to get them. Added host-mediated catalog access to the ABI (`FetchCatalogItemsAsync`/`DownloadCatalogItemAsync`): the host keeps ownership of URL validation and SHA-256 verification, the module only decides what to keep and where. Any future module with downloadable content gets the same safe path. The Fortunes pane gains browse → tick → **Download selected** (ticking is an in-memory mark only, since `SetChecked` is synchronous by contract and must never do network work).

Also wired up **`FortuneFileImporter`**, which was fully built but dead code (only its own self-test called it). **"Import your own…"** now runs user files through its bounded, validated, per-file atomic path instead of a raw copy, and never silently overwrites an existing pack. Modules carry no UI framework, so file picking became a host service (`PickFilesToOpen`).

## Test plan
- [x] Clean build, 0 warnings, all 4 projects against the extended ABI
- [x] All 8 self-test gates green, incl. 7 new poke-arbitration assertions (priority order, explicit choice restricts, declining doesn't fall through, unresolvable choice is silent, random stops at first speaker, all offered before giving up, disposal unregisters)
- [x] CoreTests 24 → 25 groups (new trigger-speech settings round-trip: global + per-pet ids, duplicate collapse, absent key)
- [x] New Fortunes assertions: download-before-browse is refused, browse lists only missing packs and pre-selects nothing, download-nothing is refused, downloading writes + leaves the available list + joins the live pool without restart, import lands the file, cancelling the picker is a no-op
- [x] Live: right-click now produces a quip/fortune, sass ladder still escalates as before
- [ ] Live: "Download selected" against the real catalog — user was mid-download when the selection rework landed, so worth re-running on this build

## Known follow-up (not in this PR)
The installed-packs list is a flat, unsorted 152-row card. Fixing it properly means extending the `ListCard` ABI primitive with optional grouping + a filter box (the catalog already carries a `Group` per pack) so the host can render collapsible, searchable sections — benefiting both pack cards and any future module. Deliberately left as its own focused change.

### PR #70 — feat(fortunes): browsable pack picker, curated names, and a 128-pack ceiling fix

`closed` · opened 2026-08-12

## Summary
Follow-up to #69, all found by poking at the real UI.

**The pack list was unusable** — a flat, unsorted wall of 150+ checkboxes labelled with raw file stems (`lwall-quotes`, `rfc1925`, `off-knghtbrd`). Now grouped into collapsible collections with a filter box. Done at the **ABI level** rather than as a fortunes special-case: `ListItem.Group` renders sections, `ListCard.Filterable`/`CollapseGroups` opt in — so any module's list card gets it. Installed packs group by the same collection names the online catalog uses (the local scan knows only ids, so `packs/collections.json` is embedded in the module).

**Grouping bug caught in review:** keying off `SourceStat.Custom` was wrong — that flag is true for *anything* in the user's fortunes folder, which (since the module bundles nothing) is every pack including catalog downloads. All 128 collapsed into one "Your own packs" section. The curated map is the only reliable signal.

**Filter bug:** it matched the generated `Detail` text (`"964 lines · spicy"`), and since every row contains "**lin**es", a query like `lin` matched the entire list. Now matches identity only (label/group/id). Spice already has three dedicated controls directly above the list, so nothing useful was lost.

**Names:** `packs/pack-names.json` gives all 152 packs a name that says what they *are*, and the same file feeds `New-ContentCatalog.ps1` so the online card agrees. Several needed verifying rather than guessing — `stevenson` is **Adlai**, not Robert Louis.

**The real bug (#69 exposed it):** `FortunePackLoadPolicy.MaximumFiles` capped loading at **128 files**. Nobody could easily install more than a handful before, so it never bit — "download everything the catalog offers" walks straight into it. Files 129–152 alphabetically were dropped **silently**: `tv-simpsons` was on disk, absent from the picker, and could never be spoken. Raised to 512 in both copies (base validates catalog entries, module loads files off disk) to match the catalog's own per-kind entry cap. The genuine memory bounds — total bytes (7.7 MB of 16 MB) and total entries (50,860 of 100,000) — are unchanged and still have headroom.

**Also:** unreadable group headers (`Expander` had no implicit theme style, so it kept WPF's near-black foreground on a dark card), and stale docs claiming the ONNX model ships "beside the exe" — it ships *inside the Fortunes module package*, which is why that module is ~30 MB. The Readme now documents Modules as how features arrive at all, since it still described Fortunes as always-on and bundled.

## Test plan
- [x] Clean build, 0 warnings; 8 self-test gates + 25 CoreTests groups green
- [x] New assertions: a catalog pack groups by its curated collection while an unknown id falls back (guards the collapse bug); a known pack shows its curated name while an unknown falls back to its id; filter matches label/group/id but **not** the generated detail (`"lin"`/`"lines"`/`"spicy"` must not match); load cap never below the catalog entry cap
- [x] Fixed latent flakiness the new test pack introduced (it joined the shared pool, making "spoke a fortune from the pack" non-deterministic) — verified stable over 3 runs
- [x] Live: 152 packs now visible and grouped, `simp` finds The Simpsons, `lin` finds only Linux packs, headers readable
- [x] Real `catalog.json` re-parses: 22 pets / 152 packs / 2 modules

## Follow-up (not in this PR)
`modules-dist/fortunes.zip` is now stale — a catalog install would get a Fortunes build without any of this. Republishing costs another ~30 MB blob in git history, so it's deliberately a separate, deliberate release step.

### PR #71 — feat(aibrain): use Windows' built-in OCR when Tesseract is absent

`closed` · opened 2026-08-12

## Summary
Closes backlog #14's actual goal — screen reading works on a fresh box — without redistributing anything.

**Why not bundle Tesseract** (what #14 literally proposed): bundling, hosting, or CI-compiling it all make us the redistributor of a third-party binary, which means license notices, CVE patching duty, ~30MB of download, and in the compile case a second heavy build pipeline for Leptonica's dependency chain. Research also turned up that there *is* no official upstream Windows binary — the de-facto one is a community NSIS **installer**, not a portable payload, so "just download it" would mean running an installer we can't hash-pin.

**Windows already ships an OCR engine.** A throwaway spike (deleted; repo untouched) checked the three things that could have ruled it out:

| question | result |
|---|---|
| Reads a probe image with no install? | yes, first try |
| Works inside a **collectible** ALC (how modules load)? | yes |
| Does the **host** need the projection? | no — travels with the module |
| Download cost | **6.03 MB compressed** (24.8 MB on disk) |

That last row matters: the ~25MB figure that got WASAPI rejected is the *uncompressed* size. The projection is metadata-heavy and compresses ~4×, and this lands in an optional module rather than the base everyone downloads.

**Tesseract stays preferred** (better on dense/small text). Resolution is now: configured path → usual install locations → PATH → **Windows built-in**.

**Discoverability**, since a silent fallback is a bad experience:
- *Test OCR* now names whichever engine answered, and on the fallback says so and points at Tesseract.
- A **"Get Tesseract…"** button opens the official install guide. The standard installer lands in `%ProgramFiles%\Tesseract-OCR`, which auto-detect already checks — so afterwards Test OCR just goes green with nothing to configure.
- Opening a browser is a host concern (modules carry no UI), so this adds `IHost.OpenLink`, **gated on the calling module declaring `ModulePermissions.Network`** and validated by the existing security-reviewed `WebLinks` HTTPS policy (HTTPS, real host, no userinfo, length-bounded).

## Known caveat (verified, not assumed)
A WinRT-using module **pins its collectible ALC** — confirmed with a no-WinRT control that *did* unload. Harmless here: `Unload()` is only called at app shutdown or on load-failure paths, and module uninstall already forces a process restart (`PendingModuleRemovals`). Documented in the csproj next to the TFM bump.

## Test plan
- [x] All 8 self-test gates + 25 CoreTests groups green
- [x] New assertion **inside the module's own load context**: Windows OCR reads a probe image — this is the standing proof the projection resolves under the plugin loader, not just in a spike harness. Skip-passes where the OS has no recognizer for the user's languages (e.g. a CI runner with no language pack), same pattern as the DPAPI check
- [x] AiBrain module: 0.24 MB → 24.5 MB on disk (~6 MB compressed); base payload unchanged
- [ ] Live click-through of Get Tesseract… / Test OCR on the refreshed dev install

### PR #72 — feat(fortunes): one Content level, live pool count, preview, group toggles

`closed` · opened 2026-08-12

Closes backlog #9.

## Why

The Fortunes pane had four overlapping tone controls whose names did not describe what they did:

- "Edgy + NSFW" actually admitted general + edgy + nsfw, i.e. everything.
- "True NSFW only" kept tame lines while silently dropping the edgy tier.

Between them the three booleans had 16 combinations, several contradictory. Nothing on the pane told the user how many fortunes the current combination actually left in the pool, so an over-narrow filter looked exactly like a broken module.

## What changed

**One ordered choice.** `SpicyFortunes` + `SpicyTier` + `SpicyOnly` collapse into a single `ContentLevel`: clean / clean + edgy / everything / spicy only. `AllowedBySettings` is rewritten as an independent statement of the rule rather than a patch over the old flags.

**Migration is a pure function.** `MigrateContentLevel(stored, legacySpicy, legacySkipTame)` takes no dependencies, so the readings that matter are asserted directly: spicy-off lands on clean, skip-tame never widens on its own, and an already-migrated value beats the legacy keys. Getting this wrong changes what the pet is allowed to say in either direction, which is not something to leave to a manual check.

**`SettingKind.Info`** joins the ABI as a display-only field, used for a live pool count that warns when the current filters leave nothing to say.

**"Show me 5 examples"** makes the tone choice visible before it is saved.

**Group-level toggles.** Collapsible list-card group headers now carry a checkbox. Switching off a section (the 19 NSFW packs) was 19 individual clicks. It drives the child checkboxes through their own toggles, so the card's `SetChecked` still runs once per changed item and the module persists exactly as it would from individual clicks. A shortcut that only moved the boxes visually would have dropped the change on the floor.

**Pack ceiling 128 -> 512.** 152 packs ship; the old cap was dropping 24 of them without a word, which is how a search for "Simpsons" came back empty.

## Verification

All 8 self-test gates green, plus the 25 core regression groups. `--wpf-options-selftest` gained coverage for the group toggle: the indeterminate state on a partly-checked group, and that checking it calls `SetChecked` only for the item that actually changed while leaving other groups untouched. `--fortunes-engine-selftest` covers all five migration readings.

Manually exercised on the dev install: dropdowns, preview spacing, group toggles across the pack list.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #73 — fix(aibrain): OCR read as ANSI, not UTF-8 ("asÂ®") + a real module update path (1.4.2)

`closed` · opened 2026-08-14

## What the user saw

A speech bubble sneering at its own input: *"'asÂ®' my ass - that's just a fucking trademark trying to blindside me with its goddamn bold font."*

The model was not hallucinating. It was quoting mojibake we fed it.

## Root cause

`AiBrain.RunOcrAsync` redirected tesseract's stdout without setting `StandardOutputEncoding`. Unset, .NET takes the encoding from `GetConsoleOutputCP()`, which returns **0** in a GUI process with no console, and then decodes codepage 0 as **CP_ACP** — the system ANSI codepage (1252 on a typical box). Tesseract writes UTF-8, so `as®` (`61 73 C2 AE`) arrived as `asÂ®`.

Reproduced at the byte level against the real engine with the pet's exact `ProcessStartInfo`:

```
GetConsoleOutputCP() = 0
StandardOutput.CurrentEncoding = Codepage - 0 (cp 0)     <- CP_ACP == 1252
  [work identity redacted] Ventures as[U+00C2][U+00AE]                  <- the bubble, reproduced
  Windows[U+00C2][U+00AE] 11 Pro [U+00E2][U+20AC][U+201D] caf[U+00C3][U+00A9]
```

With the fix, same image, same engine: `as[U+00AE]`, `[U+2014]`, `caf[U+00E9]`.

`®` was the least of it. Every non-ASCII glyph on screen was corrupted on its way into the prompt (`—`→`â€"`, `’`→`â€™`, `©`→`Â©`, `™`→`â„¢`, `é`→`Ã©`), and curly apostrophes are on nearly every page, so the brain had been fed corrupted context routinely — wasted tokens, occasional derailment. Bytes with no CP1252 mapping landed on C1 control codepoints that `CleanOcr` then stripped, losing characters outright.

**Windows' built-in OCR was never affected** (WinRT strings), so only users with Tesseract ever saw this.

## The fix

Lenient UTF-8 pinned on stdout and stderr. Lenient rather than the strict `UTF8Encoding` this repo uses for durable files: strict throws mid-read, and `RunOcrAsync`'s catch turns any throw into `""`, so one bad byte would blind the pet to the whole screen. A replacement char costs one glyph.

Three guards, all negative-tested against the pre-fix code:

- the `Test OCR` probe image carries a `®` and the status goes red on a mis-decode (a *missed* `®` stays a pass — only a mis-decoded one fails)
- `--aibrain-selftest` asserts the extracted psi factory pins UTF-8 on both streams, so it holds on CI where no OCR engine exists
- `runtime-hardening-selftest.ps1` fails repo-wide if any `RedirectStandardOutput` lacks a paired `StandardOutputEncoding`

Verified through the shipped module against the real engine: `Test OCR` returns `✓ OCR working — using tesseract.exe` with no mis-decode.

## Why a host change came with it

The module republish alone could not have reached anyone who already had AI Brain. `ModulesPaneControl.DiffNew()` diffs the catalog **by id**, so an installed module vanishes from the available list permanently: no version was ever compared, nothing checks at startup, and the only remaining route was Uninstall — which deletes the module's settings, API keys and chat history.

An installed row whose live `Info.Version` is older than the fetched catalog's now offers **"Update to vX.Y.Z"**. A loaded module's DLL is locked, so the payload is verified, unpacked into `<baseDir>\module-staging\<id>.staged`, and swapped in by the next launch before `ModuleHost.LoadFrom` can lock anything — the same deferred trick `PendingModuleRemovals` uses for deletes.

Placement is deliberate: staging sits **outside** `modules\` because `LoadFrom` loads every subdirectory it finds and would load a half-written `aibrain.new` as a module, and under `BaseDirectory` rather than the data root so the swap is a same-volume `Directory.Move`. The swap moves the old copy aside first and rolls back on failure — deleting first and then failing would leave the user with no module at all. Unlike an uninstall, the module's **data directory is untouched**.

Removals are processed first so an uninstall that raced an update wins. An unparseable version on either side offers nothing rather than guessing, since a wrong guess is an Update button that never goes away.

**Known limitation:** updates surface only after the user clicks "Check for modules online" — this pane still never touches the network on its own. A startup or periodic check is a separate call.

## Gates run locally

- `build.ps1 -Release` clean, 0 warnings; payload manifest agrees
- CoreTests: 25 groups pass
- all 10 app self-test flags pass, including the 2 new encoding assertions and 4 new staged-update assertions
- `runtime-hardening-selftest.ps1` source invariants pass
- MSI builds ICE-clean at 1.4.2, major-upgrade rollback boundary verified
- `Test-ModulePublishFreshness.ps1` green; catalog SHA-256 verified to match the **committed** zip blob

## Publish notes

- `modules-dist/aibrain.zip` + `modules.json` republished at **1.1.1**; `catalog.json` regenerated in a separate commit *after* the zip commit, because the catalog hashes the committed blob.
- **Merging this publishes aibrain 1.1.1 to every user's catalog.** The host side needs a `v1.4.2` tag afterwards for the Update button to actually ship.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #74 — Pre-freeze sweep: ABI closed out, dead code gone, leak gate restored (1.4.3)

`closed` · opened 2026-08-14

Everything that had to happen **before** the host stops shipping. Two rules drove the scope: anything the ABI cannot express becomes permanently impossible, and anything the host gets wrong becomes permanently wrong. Version increments to **1.4.3**; the freeze call and the tag are the maintainer's.

## The one-way doors, now closed

**Removed two dead ABI events.** `CompanionIdle` and `AnimationStarted` were declared, bridged, and never raised by anything. A silent event in a final contract is a trap with no release left to fix it in. Raising them honestly was the alternative and cost more than it was worth: the host has no idle policy at all (the real predicate, a screen-change delta, lives in the AI-brain module, which rolls its own timer precisely because `CompanionIdle` never fired), and `AnimationInfo.AnimationId` is an index into one pet's own XML with no name field and no enumeration verb, so making it usable meant *adding* ABI.

**Added `ICompanionManager`** — 10 members: inspect, place, and author. The reverted S6p2 version had 15 and, notably, no way to spawn from an XML string; that verb is what makes a pet-authoring module possible at all. Reached through one new `IHost` member so eight pet verbs do not appear on the surface every trivial module sees. `ICompanion` gains `TypeId`, the only join between the event stream and these type-keyed verbs.

Deliberately excluded, documented so it is not re-litigated: no "use this pet" (it writes the XML into settings, closes every pet, resets the mix — the host's own pane owns it), and no per-type size/sound/voice (user preferences the Pets pane owns; a module writing them would fight it with no arbitration).

**Enforced `MinHostVersion`.** Every module has declared one since the ABI existed and the host never read it. It now gates at load time, *before* `Init`, so a module the host cannot satisfy never touches it: `module skipped: aibrain needs host 1.0.0 or newer (this host is 0.0.1)`.

## Preview pets, and the four ways they could have leaked

`SpawnPreview` runs an arbitrary XML through the same validator an installed pet takes, then spawns it as transient. It cannot reach `settings.json`, cannot survive a restart, cannot appear in the tray's Remove submenu, and does not raise `CompanionSpawned`/`CompanionPoked`/`CompanionLanded` — so an author re-previewing twenty times does not fire twenty welcome fortunes. All of that rests on one place: `DeriveOnScreenMix` skips transient entries, and both `PersistMix` and the tray read it. The synthetic `preview:<guid>` id carries a `:` as a second line of defence, since `IsAcceptablePetId` rejects it.

## Real bugs found on the way

- **`CompanionTypeRegistry` cross-eviction.** `Add` overwrote an id without considering the displaced entry and `DisposeEntry` removed by *key*, so once an id was staged twice, the old entry hitting zero references evicted the **new** one — a live pet's type vanished from the registry, the next spawn staged a third duplicate, and the displaced pair leaked. Negative-tested: restoring the key-based removal fails exactly one assertion.
- **31 MB unpacked on the UI thread**, in both the install and update paths. Now `ZipFile.ExtractToDirectoryAsync`, with a source invariant so it cannot regress.
- **A cold `dotnet build` did not work**: `DefineConstants` hid behind a `$(Platform)` condition, so without `-p:Platform=x64` the build lost `PORTABLE` and failed with ~20 misleading CS errors.
- **`global.json` did not pin anything** (`10.0.100` + `latestMinor` floats). Now exact, atomically with all three workflows, including the `setup-dotnet` step `publish-release.yml` never had.

## Gates restored and added

The **leak soak** is back. Only its driver had been deleted, as "referenced by nothing", three hours after CI stopped referencing it — the in-process harness has been shipping the whole time. Its counter list is now discovered rather than hardcoded, which is what killed it: a verbatim restore fails on a healthy build (`cycles: 100` vs `targetCycles: 15`). Baseline: handles +5, GDI −6, USER −6, private bytes +13.6 MB.

New: **`tests/run-gate.ps1`**, one command for the whole gate that **fails on a skip**. The module self-tests skip-pass when their folder is absent, so a build that produced no modules looked identical to a clean run — I hit that twice, once by deleting the folder and once because `Select-Object -First` short-circuits the pipeline and terminates `build.ps1` before the module builds.

Also: **module version parity** (source `ModuleInfo.Version` = `modules.json` = `catalog.json`, since the Update button compares them and a mismatch offers an update forever or never), and **two salvaged engine invariants** — a child edge is parent-gated, a probability-0 transition is not an edge — both negative-tested, since both *hide* a dead animation when broken.

## Removals

`Tools/` is gone: 105 files, 5.8 MB, the last .NET Framework island. PetEditor was upstream's 2019 IDE, never modified here, already disowned in-tree. PetTester could not build at all — it link-compiles a file that moved into the AiBrain module in S4. Its two worthwhile assertions moved into the host first. Plus ~600 lines of verified-dead code, including a type whose only user was a CoreTests group testing something that shipped to nobody.

## Regression pass

- **Published 1.1.1 modules, compiled against the pre-freeze ABI, load and pass every self-test on the new host.** The ABI change is binary-compatible with what is already out there; no republish needed.
- **MSI upgrade 1.4.2 → 1.4.3 on a real box**: exe *and* `Contracts.dll` refresh to 1.4.3.0, modules and module data survive, settings byte-identical. First time that refresh has been verified with an actual ABI change riding on it.
- **The real 1.18 MB `settings.json`** loads into an isolated root: schema unchanged, no keys lost, file byte-identical afterwards.
- **Leak soak on the swept build**: +7 / −6 / −5 / +16.1 MB, in line with baseline.
- Full gate green after every one of the 15 commits.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #75 — Pet Studio: the pet validator/preview tool, rebuilt as a module

`closed` · opened 2026-08-15

Stacked on **#74** — targets `freeze/host-1.5.0`, so merge that first. This is the payoff for the ABI work in #74: the first module that could not have existed before it.

## What it is

The replacement for the retired `Tools\PetTester`. Open a pet's `animations.xml`, see what the host would reject and which animations can never play, watch it run on your real desktop, then install it.

It reaches the engine through the ABI — `ICompanionManager.SpawnPreview` puts a **transient** pet on the desktop, so an author sees the real thing under real physics, and it is never saved, never joins the pet mix, and never survives closing the window.

## The source-link, and why it is tested rather than asserted

Pet Studio **source-links** the host's parser, validator and `AnimationReachability` instead of copying them. Normally that is how you get skew. Here it is backwards: the host is frozen, so those files stop moving, and the studio's verdict cannot drift from what the host will actually run.

That claim is the whole justification, so `--petstudio-selftest` tests it — the module's analyzer and the host's `CompanionXmlValidator` must reach the **same verdict** on the bundled pet, a DTD-bearing pet, junk, and empty input. A disagreement means the link has rotted, which is exactly how PetTester died: it link-compiled a file that moved into another module during S4, and nothing noticed for a week because CI never built it.

Analysis lives in `PetAnalyzer`, UI-free, with the window as layout plus wiring. That is the other lesson from PetTester, whose graph walk lived inside a WinForms form and so could be neither tested nor reused.

## First module with a window

Modules have been data + delegates with the host rendering everything, which is right for settings panes. An authoring canvas is not expressible as a schema, and nothing structural prevents it — a module is an ordinary assembly in-process, and AiBrain already pulls in WinForms. Worth a conscious nod since it sets a precedent.

## Deliberately NOT published

It declares `MinHostVersion 1.4.3`, so listing it in `modules.json` before that host ships would offer users a module their host correctly refuses. Built and CI-gated only; the publish steps (zip → **commit** → catalog) are recorded in `BACKLOG.md` for whenever you want it live.

## Verification

`--petstudio-selftest` (15 assertions) plus the full gate, now 12 self-test flags with no skips.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #76 — Pet Studio becomes an authoring tool, on a reopened host (1.4.6)

`closed` · opened 2026-08-18

Pet Studio grows from a validator into a real authoring surface, and the host is reopened by one additive ABI member to make it possible.

## Why the host moved

Pet Studio's file dialog should open where the author's pets already are, and the ABI could not express that: `CompanionTypeInfo` carries no path and `PickFilesToOpen` has no initial directory. So `ICompanionManager.CompanionsDirectory` is added — additive, implemented in both concrete managers, and carrying the product bump to **1.4.6** in the same commit, because a Windows Installer major upgrade skips refreshing a `Contracts.dll` whose version did not change.

The freeze was a good discipline, not a wall. The rules that were right still hold; the handoff's contract block is rewritten honestly in the SDK PR that follows.

## What Pet Studio does now

A three-column window: the pet's XML on the left, a report plus a colour-coded **reachability map** in the middle, and the selected animation on the right.

- **Editable XML** — debounced re-analyze (~750ms after typing settles), atomic save, and Preview/Install read the edited text.
- **Reachability map** — every animation as a chip, coloured root / reachable / never-plays, with **clickable legend filters** to isolate a category. It scales to the sheep's 268 animations.
- **Frame preview** — the real sprite frames for the selected animation, decoded from the base64 sheet with the pet's transparency colour keyed out, with playback and a clickable frame strip, plus its outgoing transitions (click one to jump).
- **Smart Open** — defaults to the pet library, then remembers the last folder browsed to.
- **It explains itself.** A fully transparent frame now says so ("the pet is invisible here") instead of looking broken, and an unreachable animation reports that it has frames and exits but nothing leads into it.
- Host-matching light/dark theme, and a tray icon.

## Pet content: the orphaned sheep animations

The shared 268-animation sheep set had four animations nothing reaches. Two are a genuine missing edge — `king_slamB_down` and `king_slamB_up`, where the up/down walks and jumps never slam onto the opposite surface unlike base/top — so six border transitions were added mirroring the wired directions. The two `king_jump_*_flip` animations are left orphaned **on purpose**: base/up jumps already rotate directly, so those flips were bypassed by design. A sheep therefore still reports 2 unreachable, correctly.

Note `Pets/` is served live from `master`, so merging publishes this fix to every user immediately.

## Verification

- Full local gate green: 0 warnings, core tests, 12 self-tests with no skips, invariants, payloads.
- `--petstudio-selftest` extended: frame indices inside the tile grid, the map's dead set equals `AnimationReachability.FindUnreachable`, the Open-directory policy, and the tray icon resolving.
- Audited before this PR: a code-review pass (which found a dark-mode scrollbar break, since the host's template is vertical-only and this window scrolls horizontally), and a purpose-built leak soak for the window — no rooted windows, flat handles, and a private-byte plateau after fixing a per-keystroke sprite re-decode.
- Live-verified on a real install, including Preview on the desktop.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #77 — A module SDK: ModuleKit, a dotnet new template, and a publish that cannot be done in the wrong order

`closed` · opened 2026-08-18

Four modules have been written one at a time, and it shows. Every one hand-crafts the same scaffold, re-copies the same helpers, and the publish path is a five-step sequence with two traps that have each already shipped a bug. This is the first-party SDK; third-party support is designed but deliberately not built.

## The duplication this removes

Measured, not guessed:

- `CrossSessionLock` + `AtomicFile` are **byte-for-byte identical** in `modules/Fortunes/engine/FileHelpers.cs` and `modules/AiBrain/engine/FileHelpers.cs` (bar AiBrain's extra `TryWriteAllText`).
- The "enumerate `GetManifestResourceNames()` and take the one ending with this file name" loop appears **four times**; two copies are byte-identical.
- `UnicodeTextProgress` reaches AiBrain by copy and Pet Studio by source-linking an entire host file.
- Every module self-test re-declares its own `RecordingHost`/`DenyingCompanionManager` and a byte-identical `Check(sb, name, cond)`.

## `src/DesktopPet.ModuleKit`

`AtomicFile`, `CrossSessionLock`, `EmbeddedResources`, `UnicodeTextProgress`, `ModulePaths`, `JsonSettingsStore<T>`, `SelfTestProbe`, plus a `Testing` namespace with `RecordingHost`, `DenyingCompanionManager`, `TempModuleStorage` and `FakeModuleSettings`.

**It is deliberately not the ABI.** `DesktopPet.Contracts` is referenced `Private="false"` and shared from the host's default context, frozen at `AssemblyVersion 1.0.0.0` forever. ModuleKit is referenced *normally*, so a copy ships inside each module's folder — each collectible load context gets its own, and two modules may use different ModuleKit versions. Putting these helpers in the contract would have made every one of them permanent and global.

## `dotnet new desktoppet-module`

Scaffolds a module that builds and passes its own self-test **as generated** — a tray item, a schema-declared settings pane whose values round-trip through `IModuleSettings`, a poke reaction, and a `SelfTest` built on `SelfTestProbe`. Its csproj encodes the four load-bearing facts a new module gets wrong (the `modules\<id>\` output path, the `Private="false"` contract reference, flat output, and the dependency-copy properties needed once a module references anything) with commented flavour blocks for the three shapes existing modules take: a window of its own, a native NuGet dependency, and WinRT.

A template is built by nothing, so it rots silently. `packaging/Test-ModuleTemplate.ps1` scaffolds a throwaway module, asserts every placeholder was substituted, builds it, asserts **ModuleKit shipped beside it while Contracts did not**, then removes it and uninstalls the template. Wired into both `run-gate.ps1` and CI.

## `--module-selftest=<id>`

A scaffolded module previously could not be run at all until its author edited `Program.cs`, which meant the template shipped a `SelfTest` nothing could invoke. Now any module exposing `public static bool SelfTest(out string detail)` is runnable with **no host edit**: the module is loaded through the *real* `ModuleHost`, so a pass also proves the loader accepts it, the `MinHostVersion` gate let it through and `Init` ran, and its `ModuleInfo` is checked for a name and a parseable version. An absent module SKIPs (which the gate treats as failure) and a module with no `SelfTest` **fails** rather than quietly passing.

The three pre-SDK modules keep their bespoke `*ModuleSelfTest.cs`, which assert host-integration specifics this cannot know about.

## `packaging/New-ModulePublish.ps1`

One command for build → zip → register → commit → catalog → verify, reading `Version` and `Permissions` out of the module's own source so the catalog cannot drift from the code, and **refusing to regenerate the catalog while the zip is uncommitted** — because `catalog.json` records the SHA-256 of the *committed* git blob, which is what raw.githubusercontent serves.

## Docs

`docs/module-authoring.md` is the guide the four existing modules never had. `docs/module-ecosystem-roadmap.md` records the third-party design (signing plus per-publisher consent on the VS Code model, a curated links page before a marketplace, NuGet-publishing the contract and ModuleKit) with its open questions, and argues honestly that the cheap steps make an ecosystem possible while the expensive ones should wait for evidence anyone wants one.

`handoff.md`'s freeze contract is rewritten: it asserted the host stops shipping, which 1.4.6 contradicted on purpose. The rules that were right are kept.

## Verification

- Pet Studio is migrated onto ModuleKit as the proof — safe because it was unpublished. Fortunes and AiBrain are published, so migrating them is a republish to every existing user and is queued in BACKLOG instead.
- CoreTests goes 30 → **37 groups**, covering durable writes, resource lookup, surrogate-pair boundaries, settings round-trip plus corrupt/BOM recovery, `ModulePaths`, the probe, and the recording host. Writing them found a real path-safety weakness in `ModulePaths`, now hardened and asserted against five hostile ids.
- Scaffold-to-passing-self-test verified end to end through a collectible load context with a shared contract.
- Full local gate green; CI now runs the template check too.

Correction worth recording: Pet Studio keeps source-linking `RuntimeGeometry.cs`, because the linked `Xml.cs` needs `DesktopGeometry` from it. Source-linking stays the right tool for host *engine* code that must not diverge; ModuleKit is for generic utilities.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #78 — Publish Pet Studio 1.1.0 to the module catalog

`closed` · opened 2026-08-18

Pet Studio has been built and CI-gated since 1.4.4 but never published, because it declares `MinHostVersion 1.4.6` and that host did not exist. **v1.4.6 is now released**, so it can ship.

Merging this is the publish: `modules-dist/` and `catalog.json` are served off `master` via raw.githubusercontent, so Pet Studio becomes installable from Settings → Modules for every existing user with no new app release.

- `modules-dist/petstudio.zip` — 3 entries (`PetStudio.dll`, `DesktopPet.ModuleKit.dll`, `PetStudio.deps.json`; no `.pdb`). `DesktopPet.Contracts.dll` is deliberately absent, since the host shares its own copy.
- `modules-dist/modules.json` — the catalog-facing name, description and permissions (`Pets, Storage`), with the version read out of `PetStudioModule.cs` so it cannot drift from the code.
- `catalog.json` — regenerated: 22 pets, 152 packs, **3 modules**. The recorded sha256 `b1bcfb23…` was verified against the *committed* git blob, which is what raw.githubusercontent actually serves.

Produced by `packaging/New-ModulePublish.ps1 -ModuleId petstudio -Commit`, and `Test-ModulePublishFreshness.ps1` passes for all three modules.

## Also: a fix to the publish tool itself

The first run of that script aborted halfway. git printed `warning: ... CRLF will be replaced by LF` to **stderr**, and with `$ErrorActionPreference='Stop'` PowerShell 5.1 turns any native stderr line into a terminating `NativeCommandError` — so the script died *after* `git add` had already succeeded, leaving exactly the half-finished state it exists to prevent. git calls now go through `Invoke-Git`, which makes errors non-terminating and judges git by its exit code instead of by whether it said anything.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #79 — Close two ABI gaps before re-freezing: IHost.IsDarkTheme and IHost.Log (1.4.7)

`closed` · opened 2026-08-18

The host is open on purpose and is meant to re-freeze once this arc is finished. These are the two gaps worth spending that window on, because after a freeze *anything the ABI cannot express is permanently impossible* — and both would be felt by every module written against the SDK that just shipped.

## `IHost.IsDarkTheme`

A module that owns a window — which `dotnet new desktoppet-module` now actively encourages — could not find out how the app is presenting itself. The user's choice is light / dark / **system**, and only the host knows which is set, so reading the OS theme directly is right for "system" and wrong the moment someone pins the opposite. Pet Studio's own theme file has been carrying this as a documented defect:

> the module reads the OS theme, not the host's own light/dark/system PREFERENCE … Exposing `IHost.IsDarkTheme` would close that gap.

The host now answers with the same resolution its own WPF windows use.

## `IHost.Log(moduleId, message)`

`IHost` had **no logging member at all**. A module's only way to report anything was `SayAll` — making the pet speak diagnostics at the user. Lines now land in the app's diagnostic log, tagged with the calling module's id.

Deliberately **not** behind a permission: it is strictly less capable than the per-module storage a module already has, and the alternative is every module inventing a private log file nobody knows to look at.

## Contract details

- Both are **best-effort and never throw**, and both are asserted against the **real `CompanionHost` with no `StartUp` behind it** — the host-not-running degradation path — because a theme query happens while building UI and a log call must never punish its caller.
- `ModuleKit.Testing.RecordingHost` gains a settable `IsDarkTheme` and a `LoggedLines` list, so an author can assert a window themes correctly both ways without touching the machine's OS setting.
- Carries the product bump to **1.4.7** in the same commit, as an ABI change must: a Windows Installer major upgrade skips refreshing a `Contracts.dll` whose version did not change.

## Deliberately not done here

Pet Studio does **not** adopt `IsDarkTheme` yet. Doing so would force `MinHostVersion 1.4.7` and make it refuse to load on the 1.4.6 currently installed. A pointer is left in its theme file for the next time it raises its version.

Full local gate green, including the two new assertions.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #80 — Make a failed module visible and repairable, and let a module be built outside the repo

`closed` · opened 2026-08-18

The two items that were standing between this arc and a clean ABI freeze.

## 1. A module that failed to load was invisible

`ModuleHost.LoadFrom` caught every failure and only logged it. Since the Modules pane decides what is installed by enumerating **folders**, a broken module counted as installed (so it was filtered out of "available"), reported no live version (so no update was ever offered), and displayed **"installed — restart to activate" forever**. The only exit was Uninstall — which deletes the module's settings and API keys. A destructive action to escape a state the user did not cause.

All four early-return paths (no module DLL, no `IModule` type, `MinHostVersion` refusal, any exception) now record a `ModuleLoadFailure` with the folder id and a reason, surfaced through `StartUp.ModuleFailures` — the same route as `LoadedModules`.

The pane renders **"failed to load — &lt;reason&gt;"** in red with a **Reinstall** button routed to the existing install flow, which replaces only the install folder and leaves the module's data alone, so a repair is non-destructive by construction. The button is disabled until a catalog has been fetched, since there must be something to reinstall from.

A `MinHostVersion` refusal is deliberately distinguished — amber **"needs a newer app"**, no Reinstall — because the module is fine and reinstalling would achieve nothing. Only updating the app helps.

`--module-host-selftest` drives three genuinely broken folders through the real loader (empty; a DLL implementing nothing; a file that is not an assembly) and asserts each is reported with a reason and none is mislabelled. Verified visually as well.

## 2. A module can now be built outside this repo

This was the only real barrier to third-party authoring — the module system enforces no signing and sideloading is unrestricted, so the friction was packaging, not policy. The template referenced the contract and ModuleKit **by project path**, so writing a module meant cloning this tree.

Both libraries are now packable with real metadata and nuget.org-facing readmes, and the template takes `--standalone`, which swaps the project references for `PackageReference`s. `ExcludeAssets="runtime"` on the contract is the package-world equivalent of `Private="false"`: compile against it, never copy it, because the host ships the one true copy and a second one stops the `IModule` types unifying.

**Proven, not assumed:** a module scaffolded in a temp folder outside the repo, built against a local feed, produced exactly the right payload (ModuleKit beside the module, Contracts absent), and the released app loaded it from a hand-copied folder with `--module-selftest=outsiderpet` passing 13/13.

### Deliberately not pushed to nuget.org

The packages are attached to each **GitHub release** instead, checksummed alongside the app downloads. That unblocks an author via a local package source without permanently claiming public package ids, and without committing to publish a new package on every host release even when the contract has not changed — the contract's package version tracks the product, so it would move constantly for an unchanged ABI. `packaging/New-NuGetPackages.ps1` packs, verifies (readme present, lib present, contract still dependency-free) and prints the push command without running it, so publishing stays one deliberate command away.

The docs also note the simplest route of all: the portable ZIP already ships `DesktopPet.Contracts.dll` beside the exe, and a plain `<Reference>` to it is enough to write a module.

Full local gate green.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #81 — 1.4.8: ship the failed-module fix and the author packages

`closed` · opened 2026-08-18

A version bump so the last two changes actually reach people.

`release.yml` packs `DesktopPet.Contracts` / `DesktopPet.ModuleKit` and attaches them to the GitHub release — but only on a **new tag**. Until this ships, a third-party author has nowhere to fetch them from, so the "build a module outside the repo" path is real in the repo and not yet real for anyone else. Same for the failed-module UI fix, which is user-visible and currently unreleased.

Also bumps the template's `minHostVersion` / `packageVersion` defaults to 1.4.8 so a newly scaffolded module targets the current host. The remaining 1.4.7 mentions in `BACKLOG.md` and `docs/module-authoring.md` are deliberately historical — they record which release introduced `IsDarkTheme` and `Log`.

No ABI change in this one (`ModuleLoadFailure` is internal to the app, not part of the contract).

Full local gate green. Tagging this will also be the first real exercise of the new `release.yml` packing step.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #82 — Build Fortunes and AiBrain on ModuleKit (-820 lines)

`closed` · opened 2026-08-18

The last of the deferred work. ModuleKit was extracted from these two modules' duplicated helpers, but they were still carrying their own copies — deferred because a republish reaches every existing user. The repo has **0 stars**, so that audience is hypothetical and the clean change wins.

**17 files changed, +37 / −820.**

## What went

- `modules/Fortunes/engine/FileHelpers.cs` (325) and `modules/AiBrain/engine/FileHelpers.cs` (388) held `CrossSessionLock` + `AtomicFile` with a **byte-identical core**; AiBrain's differed only by also carrying `TryWriteAllText`.
- `modules/AiBrain/engine/TextHelpers.cs` (39) was a copy of `UnicodeTextProgress`.
- Three hand-rolled "scan manifest resources by trailing name" loaders — AiBrain's `LoadIconResource`, Fortunes' `ReadEmbeddedText` and `LoadWelcomeCorpus` — collapse into `EmbeddedResources`.

## What deliberately stayed

**The wrappers, where their contract differs from ModuleKit's.** `ReadEmbeddedText` returns `null` rather than `""` because its callers branch on null, and `LoadWelcomeCorpus` still defaults to an empty array. Changing those quietly would be a behaviour change dressed as a refactor.

**`FortuneProvider`'s own resource read.** It decodes the bundled 10k-line corpus with **strict** UTF-8 (`throwOnInvalidBytes`) and distinguishes "resource missing" from "failed to parse" in its diagnostics; ModuleKit's loader is deliberately lenient and returns `""` for both. Consistency is not worth losing a real correctness signal. The one other `GetManifestResourceStream` call uses an exact `LogicalName`, not a scan, so it is already as specific as it can get.

## Republished

Both modules move to **1.1.2** — the payload genuinely changes, since `ModuleKit.dll` (~27 KB) now ships inside each module folder. `Contracts.dll` stays absent from all three, as it must: the host shares its single copy. All three modules verify current across source, `modules.json` and `catalog.json`.

## A publish-ordering trap, and a guardrail for it

I published the zips **before** committing the module source, and the freshness check correctly flagged both as `STALE` — it compares commit *recency*. The nasty part: the zip is deterministic, so re-zipping produces identical bytes and there is **no new commit available to repair the order**. The only exits are rewriting history or a dummy commit. I reset and recommitted as source-first, payload-second.

`New-ModulePublish.ps1` now **refuses to publish while a module's source is uncommitted**, naming the dirty files and saying why — verified by dirtying a file and watching it stop.

Full local gate green; all five module self-tests pass.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #83 — Stable, not frozen: replace the freeze with six rules

`closed` · opened 2026-08-18

`handoff.md` still opened by telling the next reader the host was being frozen. That framing has cost more than it bought, so it is gone rather than softened a second time.

## Why

**The freeze failed three times in three days.** Frozen at 1.4.4; reopened at 1.4.6 for `ICompanionManager.CompanionsDirectory`; 1.4.7 for `IHost.IsDarkTheme` and `IHost.Log`; then 1.4.8. Building *one* module plus the SDK surfaced *three* ABI gaps — that is what building reveals, not a lapse in foresight.

**It also distorted a real decision.** A module that failed to load was invisible, with Uninstall — which deletes settings and API keys — as the only escape. That sat in BACKLOG marked "post-freeze fix" because of the rule.

**And more modules are planned**, so the pattern would keep repeating.

## What replaces it

The property a freeze was reaching for is *"a module written today keeps working."* That comes from invariants, not from refusing to add:

1. `AssemblyVersion` stays `1.0.0.0`, forever — the binding identity.
2. **Additive only** — never remove a member or change what one means. This is the real permanent commitment, freeze or no freeze.
3. An ABI change bumps the product version in the same commit (or the installer ships a stale `Contracts.dll`).
4. Never declare an event you do not raise.
5. Raise `MinHostVersion` only when you actually call a newer member.
6. Do not move a source-linked engine file without re-running `--petstudio-selftest`'s parity assertion.

All six are already enforced by code or gates, so this documents reality rather than aspiration.

## Plus a gap worth knowing before it bites

`IHost` exposes `Volume` **read-only and no playback verb at all**, while the base owns a full DirectSound mixer (`AudioOutput.cs`). A **TTS/voice module — already planned — is impossible today.** BACKLOG now records it with the shape it would take (a `PlaySound` routed to the existing mixer, gated on a new `ModulePermissions.Audio` so it appears in the pre-install consent list) and the instruction to add it *with* that module rather than speculatively. Which is precisely the workflow a freeze would have forbidden.

Current state refreshed to 1.4.8, modules at fortunes 1.1.2 / aibrain 1.1.2 / petstudio 1.1.0, and the box now running everything installed through the catalog rather than hand-copied.

Full local gate green.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #84 — Session wrap-up: Readme, handoff START HERE, backlog accuracy, stale-file cleanup

`closed` · opened 2026-08-18

Housekeeping so the repo tells the truth about itself.

- **Readme** knew nothing about the last three releases. Adds **Pet Studio** (the reachability map, the sprite-frame preview, and what the tool is actually *for*) and a **"Writing your own module"** section — which matters because the whole point of the SDK is that someone can do it *without* cloning this repo, and the Readme is where they would look. States the deployment story plainly: no signing gate, no allowlist, build a DLL and drop the folder in.
- **handoff.md** gains a **START HERE** block. The useful thing to tell the next session is that nothing is half-finished, plus the two traps that cost real time here: publish a module's **source before its payload** (the freshness check compares commit recency, and a deterministic re-zip cannot repair a bad order), and verify `master` against `origin/master` rather than a local branch that may have no upstream.
- **BACKLOG** had gone stale in two places, now marked done: the Fortunes/AiBrain ModuleKit migration, and the failed-module-invisible bug. Still open with reasons: the audio gap, the overlay gap, `IsDarkTheme` adoption, the module-window leak soak, Phase B.
- **Deleted `src/packages`** — 38 MB of untracked net48-era NuGet leftovers, unreferenced by any project (only `src/packages.lock.json`, a different file, is referenced). Verified with a clean build afterwards.
- **Redacted a literal personal email** from two docs. It added nothing the public commit history does not already show.

### Security sweep (this repo is public)

| Check | Result |
|---|---|
| "[work identity redacted]" / work material | **clean** — no matches in tracked files or anywhere in history |
| Work identity in git history | **clean** — the earlier `filter-repo` scrub held; 13 of 14 authors are upstream OSS contributors |
| Credentials in tracked files | **clean** — the only matches are deliberate fake fixtures in a security self-test |
| Credential/key files | **none tracked**; real API keys live DPAPI-encrypted under `%LOCALAPPDATA%`, outside the repo |
| AI/agent leftovers | **none tracked or on disk**; `.gitignore` already covers `CLAUDE.md` and `.claude/` |

Remaining email addresses are third-party attribution lines inside the classic Linux/BSD fortune corpus (`packs/*.txt`) — upstream content, where removing them would strip credit.

Full local gate green.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #85 — Clear the open backlog: Pet Studio host theme, a module-window leak soak, and per-pet poke attribution

`closed` · opened 2026-08-19

Clears the three remaining open backlog items, plus one latent bug found while starting the per-pet speech work.

## Pet Studio 1.1.1 — theme from the host, not the OS registry

`PetStudioTheme` read `AppsUseLightTheme` directly, which is correct only while the host sits on its default
"system" setting and wrong the moment a user pins the opposite. `IHost.IsDarkTheme` has existed since 1.4.7.
`Current()` now takes the `IHost`; a null or throwing host falls back to light, matching the direction the
host's own resolver fails in.

The `DESKTOPPET_FORCE_THEME` env override goes with it — the settable `RecordingHost.IsDarkTheme` is a better
version of what it was for. `--petstudio-selftest` now drives the theme **both ways** plus the no-host case;
previously it asserted nothing about theming at all and its fake host hardcoded `IsDarkTheme => false`.

Non-obvious: `PetStudioWindow` built the theme in a *field initializer*, which runs before the constructor
assigns `_host`, so it had to move into the ctor.

## A committed leak soak for a module-owned window

`runtime-resource-soak.ps1` samples the shipped app from outside and its churn loop never opens a module
window, so a module's HWNDs, Bitmaps and decoded sprites were covered by nothing — the soak that found the
sprite re-decode bug existed only as prose in `handoff.md`.

`tests/DesktopPet.WindowSoak` is a separate `UseWPF` console exe that loads the module DLL at **runtime** and
drives it by reflection (`PetStudioWindow` is `internal sealed`, so a compile-time reference would buy
nothing). It reuses ModuleKit's `RecordingHost` rather than hand-rolling a fake that rots on every ABI
addition, and a missing reflected member is a hard FAIL, never a skip. Not in the blocking gate — leak soaks
flake on headless runners — so it is wired into `RELEASE-CHECKLIST.md` as a pre-tag step.

Pet Studio, 2 x 20 cycles: segment 2 handles +0, GDI +0, USER +0, private -7.8 MB.

**The trap, worth knowing before writing any WeakReference leak test:** exactly one window per segment looked
rooted, always the last (cycle 7 of 8, cycle 19 of 20). Not a leak and not `Application.MainWindow` — the
strong reference was *escaping the cycle method* into the caller's stack slot. Fixed by returning a
`WeakReference` and marking the cycle `NoInlining`. A displacer window was tried first, did nothing, and was
removed rather than left in looking meaningful.

Negative-tested rather than assumed: deliberately rooting each window fails it on two independent signals —
all cycles rooted instead of none, and segment-2 private bytes +31.4 MB instead of -9 MB.

## About window verified, and a stale doc corrected

Rendered `AboutWindow` to a PNG from a throwaway harness instead of deferring to "the next reinstall": dark
theme, all six allowlisted doc links, and the layout all confirmed good. The harness is deliberately not
committed.

`BACKLOG.md` said "the WPF About/**Help** windows" — there is no `HelpWindow`. Help was folded into
`AboutWindow` and the tray entry is a single "About / Help". The old wording sent readers hunting for a file
that does not exist.

## A poke is now attributed to the pet that was actually clicked

`FormCompanion` knew which pet the user right-clicked and threw it away: it called `OnPetPoked()` with no argument
and `StartUp` recovered "a" pet via `FirstPersistentPet()`. So poking pet #5 was reported to every module as a
poke on pet #1, and `PokeInfo.Pet` was wrong for every pet except the first.

Invisible today because every speaker broadcasts through `SayAll` anyway, and silently wrong the instant
anything reacts per pet — which is where the per-pet speech routing work is heading, so it lands first as the
foundation. Pinned in `runtime-hardening-selftest.ps1`, since dropping `this` again would restore the bug with
no test failing anywhere.

---

Gate green throughout: 0 warnings, 37 CoreTests groups, 12 self-tests with no skips, invariants, payload
freshness, template.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #86 — fix(ci): stop two workflows racing to publish the same tag

`closed` · opened 2026-08-19

Every tagged release was built and published **twice**. `release.yml` and `publish-release.yml` both triggered on `push: tags: v*`, both ran the full build, and both ran `gh release upload --clobber` against the same GitHub release. Whichever finished last won, so `SHA256SUMS.txt` listed the module-author nupkgs or not depending on who lost the race. **Every release was non-deterministic.**

`publish-release.yml`'s own header claimed `release.yml` was *manual-dispatch only*, which was factually wrong about its sibling's trigger, and is presumably how this survived.

Consolidated into `release.yml` (which already packed the nupkgs) and deleted `publish-release.yml`, folding in its two correctness properties, because `release.yml` had **neither**:

- **It checked out the tag.** `release.yml`'s checkout had no `ref:`, so a `workflow_dispatch` re-run built the **default branch** and uploaded those artifacts under the requested tag's release, publishing something that was never tagged.
- **It verified the tag against `ProductVersion.props`.** `release.yml` validated only the `vMAJOR.MINOR.PATCH` shape, so a tag disagreeing with the product version published happily. After an ABI change that ships a stale `Contracts.dll` no module can resolve.

Added a `concurrency` group too: two concurrent runs of the surviving workflow would reproduce the same clobbering.

Left alone deliberately: `release.yml` still runs `microsoft/setup-msbuild`, now vestigial since `build.ps1` no longer probes MSBuild and the MSI is built by the `wix` dotnet tool. It costs seconds, and the release path is the wrong place to discover an implicit dependency. Recorded in BACKLOG instead.

Found while preparing to tag `v1.5.0` — this would have fired on the first tag.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #87 — feat(abi)!: per-pet speech routing — a reaction belongs to one pet (1.5.0)

`closed` · opened 2026-08-19

Host side of per-pet speech. Fixes the reported bug that **every pet on screen says the same line at the same moment**, and lays the ABI for routing each pet to its own speech source.

Modules are unchanged here and still broadcast; migrating Fortunes and the AI brain is a follow-up PR, because a module declaring `MinHostVersion 1.5.0` must not reach the catalog before 1.5.0 has shipped.

## The ABI

```csharp
IDisposable RegisterCompanionDropResponder(int, Func<ICompanion,bool>);
IDisposable RegisterCompanionPokeResponder(string, int, Func<ICompanion,bool>);
bool IsCompanionAlive(ICompanion);
```

**New names, not overloads.** A parameterless `delegate { }` converts to both `Func<bool>` and `Func<ICompanion,bool>` with no better-conversion tie-breaker, so overloading would make `RegisterDropResponder(0, delegate { return true; })` fail as **CS0121** for anyone who recompiles. `LangVersion 7.3` means that spelling is everywhere here, and third-party modules will copy it. Binary compat would have survived; source compat would not.

**`IsCompanionAlive` on `IHost`, not `ICompanion`.** `ICompanion` has seven implementations here and ModuleKit *ships* `FakeCompanion : ICompanion` — adding a member there is the one way "additive" still breaks a module. Both registration styles share one priority list, so a migrated module and an unmigrated one still compete fairly.

## Three live bugs found while tracing

- **`SayAll` and `PlayAnimationOnAll` spoke and emoted through authoring previews.** Both walked `sheeps[]` directly, contradicting the documented "previews are invisible to modules" invariant. Added `PersistentPets()` as the single place that filter is stated — it was re-derived per call site, which is how it rotted.
- **`CompanionHost.Say` had no disposed guard and no `Safe` wrapper.** A module holds an `ICompanion` indefinitely (there is no `CompanionRemoved` event), so a pet removed mid-answer is normal; unguarded, `FormCompanion.Say` builds a `FormSpeech` on a disposed form and throws out of the module's call.
- **The poke-responder sort tie-breaker used `IndexOf` against the list being replaced** — correct only because the sort ran over a copy, O(n²), one refactor from silently reordering the "Default & Random" pick.

## Two regressions this would otherwise have introduced

- **The repeat guard would have silently died.** It lived in `StartUp.SayAll` as one global "last broadcast line", and `IHost.Say(pet, text)` bypasses `SayAll` entirely — so the moment modules address one pet, the user's suppress-repeats preference stops seeing the lines it exists for. Moved into `FormCompanion.Say`, per pet, where no path can route around it. It was also *wrong* globally: Pearl saying "X" should not silence Rick saying "X", while Pearl saying "X" twice is a genuine repeat.
- **Poke escalation was per-app.** `pokeCount`, the 7s reset and the 12s cooldown were shared fields: poke Pearl three times then Rick once and Rick answered at the sass tier; poking four pets in turn gave one reaction and three silences. Invisible while everything broadcast, obvious once sass is routed. Now per pet in a `ConditionalWeakTable`.

## The trap that would have shipped silently

`triggerSpeech` uses `""` to mean **global**, while the pet mix uses `""` to mean **the active pet**. Keying a real pet as `""` would rewrite the all-pets preference *and still look correct*, because the lookup falls back to global — every other pet type would test fine. `SpeechRoutingKey` resolves the active pet to its real type id, which is what `ICompanion.TypeId` and per-pet size/sound already use.

Related: `StartUp.TryPokeReaction` read the preference with a hard-coded `""` key, so a per-pet choice could never have applied even once the storage supported it. The host now resolves it from the subject, so the poke and drop chains cannot disagree.

## Also

Drops belong to one pet, chosen **round-robin** rather than uniformly at random — random lands on the same pet several times running often enough to read as "still broken". Base reactions routed: sass and the turn-away go to the poked pet; the bathtub escape stays global on purpose (every pet fleeing *is* the joke) and now says so in a comment.

ModuleKit's `RecordingHost` gains `SaidToCompanions`, `BroadcastLines` and a settable `CompanionAlivePredicate`. `Say` and `SayAll` both wrote only `SaidLines`, which made "did this route or broadcast?" — the exact distinction being introduced — impossible to assert. `SaidLines` stays as the union so third-party tests keep working.

Gate green: 0 warnings, 37 CoreTests groups, 12 self-tests with no skips, invariants, payloads, template.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

### PR #88 — feat(modules): speak to one pet, not all of them (fortunes + aibrain 1.2.0)

`closed` · opened 2026-08-19

feat(modules): speak to one pet, not all of them (fortunes + aibrain 1.2.0)

The module half of per-pet speech. The host shipped in 1.5.0; both modules now
declare MinHostVersion 1.5.0 and use the pet-aware responders, so a reaction
reaches the pet it belongs to instead of every pet on screen reciting it in
unison. This is the user-visible end of the reported bug.

FORTUNES 1.2.0
Drop and poke register pet-aware; SpeakFortune takes the subject and speaks it
with Say(pet, ...). CompanionLanded now speaks to the pet that landed -- previously
adding a fourth pet made all four say the same fortune the moment one touched
down, which was the second most visible face of the bug. The screen context is
captured from the subject too, so a contextual pick describes the window THAT
pet is standing on rather than another pet's.

The welcome deliberately stays SayAll, with a comment saying why: it is a
once-per-session greeting addressed to the USER, not a reaction belonging to a
pet, and it fires on first spawn when there is normally one pet anyway.

AI BRAIN 1.2.0
Ask takes the subject through to the async completion rather than re-reading
_lastPet there -- CompanionSpawned, CompanionLanded and CompanionPoked all move it, and a model
round trip is easily long enough for that to happen.

The thinking cue was a second instance of the same bug: PlayAnimationAll +
SayAll("...") made EVERY pet ponder a question only one of them was asked. Now
routed to the subject via TryPlayAnimation, which needs no new ABI because the
module already owns the emotion -> candidates mapping.

If the pet is gone when the answer arrives, the answer is DROPPED and logged,
not handed to another pet. A different pet answering a question it never asked,
having shown no "..." cue, is the same bug wearing a hat.

Noted but deliberately not fixed: session.RequestInProgress is one global flag,
so two pets cannot be asked concurrently. Correct for 1.5.0; per-pet concurrency
is BACKLOG #16(a).

TESTMODULE
OnPoked speaks to info.Pet. It is the reference module, so it should demonstrate
the policy rather than the bug.

Both self-test fakes capture BOTH registration styles behind FireDrop/FirePoke,
so the assertions survived this migration instead of needing to change in
lockstep with it.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>

### PR #89 — feat(abi)!: audio playback and speech interception for a voice module (1.6.0)

`closed` · opened 2026-08-20

feat(abi)!: audio playback and speech interception for a voice module (1.6.0)

The two gaps that made a TTS module impossible. Both additive.

  ModulePermissions.Audio, ModulePermissions.Voice
  bool PlaySound(string moduleId, byte[] audio, double volume)
  bool StopSound(string moduleId)
  IDisposable RegisterSpeechResponder(string, int, Func<SpeechRequest,bool>)

A byte[] CONTAINER, not raw PCM. A float[] would alias -- the mixer thread reads
it for the life of playback, so a module reusing its synthesis buffer would be
audible as a seam, and defending with a copy gives back the entire saving. It
would also commit the contract permanently to interleaving order, channel
semantics and range clamping, whereas a container commits to nothing and lets a
future codec be a host-side change. Every realistic engine already emits one;
ModuleKit's new WavAudio.FromPcm covers the exception.

TWO permission flags, not a reuse of Speech. Speech means "calls Say/SayAll"; a
voice module never calls Say, it reads and can SUPPRESS every line, which is a
different and privacy-relevant capability -- a speech responder sees every line
the AI brain generated from the user's screen. They are separable in practice
too: a sound-effects module wants playback without interception, a captions
module the reverse.

CLAIMING AND SUPPRESSING ARE SEPARATE. Returning true means "I own the output of
this line", which is not the same as "I spoke it"; SpeechRequest.SuppressBubble
carries the bubble decision. That split is what makes bubble-only, bubble+voice
and voice-instead-of-bubble expressible without overloading one bool.

SpeechRequest.ShowBubble is the load-bearing member. The responder is synchronous
and on the UI thread, so a module must decide whether to claim BEFORE it knows
whether synthesis will succeed. Handing the line back by calling Say/SayAll does
NOT work: SayAll compares against the last line said, and with the default
suppress-repeats preference on, the identical replay is swallowed and the line
vanishes. Only the host can bypass both the chain and that guard.

AudioOutput: a decode seam sniffed by magic bytes (RIFF/WAVE or ID3/MPEG sync),
resampled and upmixed through the path Decode already used, rejecting >2 channels
explicitly so the caller gets false rather than the mixer throwing into a silent
catch. Module audio NEVER enters _cache -- it is keyed by byte[] reference
identity and cleared only in Dispose, so caching speech would retain every line
the pet ever spoke plus a buffer ~7x larger. Pinned by an invariant.

Barge-in cuts by ramping out over ~10 ms and returning short, so NAudio drops the
input; muting a VolumeSampleProvider would leave a silent input occupying the
mixer for the utterance's full remaining length. The live-input registry has its
OWN lock: MixerInputEnded fires on the audio callback thread inside the mixer's
source lock while callers hold _sync and then take that same lock, so sharing one
would be an ABBA deadlock.

Shutdown reordered so modules shut down BEFORE the audio output is disposed --
previously a module calling StopSound during teardown was talking to a disposed,
then nulled, output. Safe only because PlaySound takes a byte[] the host decodes
into its own buffer, so no module-owned provider is ever in the mixer.

ALSO FIXES A LATENT CATALOG BUG. An unrecognised permission name made Parse throw
for the ENTIRE catalog, not the entry -- and because every catalog feature shares
one fetch, the first release to add a flag silently took the Modules pane, the
monthly update check, pack browsing AND the Pets gallery away from every older
host. It had already fired unnoticed: Pets shipped in 1.4.4, so a v1.4.2 host
cannot parse today's catalog at all. Unknown names are now dropped and the entry
kept; an empty or malformed list is still rejected. Publishing the Voice module
would otherwise have done this to every host below 1.6.0.

New --audio-selftest (13 assertions, deliberately device-independent so it runs
on a CI runner with no playback device): resample+upmix proven by frame count,
every rubbish input rejected rather than thrown, and the barge-in ramp terminating
across read sizes smaller than itself.

The cache invariant was negative-tested and FAILED to fail on the first attempt --
a brace-counting regex could not see past PlayOwned's inner lock block. Rewritten
to slice by position. That class of dud assertion only ever surfaces by trying it.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>

