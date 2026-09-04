# AI Desktop Companion — Backlog

> Fork of Adrianotiger/desktopPet. The original physics experience is preserved, while compatibility,
> correctness, validation, and security fixes do modify engine files where required.

---

## ▶ Current major work — .NET 10 + plugin re-architecture (2026-08-06)

The active effort is **not** in the feature list below. Two things, both now **released** — the public line
reached **v1.2.3 (2026-08-12)**, and modules ship separately through the in-app catalog at **1.1.1**:

1. **`.NET 4.8 → .NET 10 (LTS)` migration — DONE, on `master`** (v1.1.0, framework-dependent, behavior parity).
2. **Plugin re-architecture (streams S1–S7) — IN PROGRESS** — turn the monolith into a **plugin host**; each
   capability becomes a module (own `AssemblyLoadContext`). **Done:** S1 host foundation (`DesktopAICompanion.Contracts`
   ABI + loader + `CompanionHost`), S2 **Sound** module (NAudio out of the base), S3 part 1 **Fortunes** module
   boundary + a personalized Windows-username **welcome starter**, and **S3c** — the fortune **engine
   relocation**. **S3 is DONE + MERGED** (PRs #4/#5/#6): the Fortunes module is the live fortune source and
   the base is ONNX-free. **S4 (AI-brain module) — MERGED (PR #7):** the
   optional screen-commentary LLM now lives entirely in `modules/AiBrain` and owns the ask/hotkey/idle/drop
   flow; the base is runtime-disconnected (it never runs the brain). Off by default. The base's now-dead AI
   files + Options AI tab are removed in S5 (entangled with the AiSettings split), mirroring how S3d left the
   fortune UI/engine for S5. **S5 (WPF shell) + Companions features + the "B" audio arc — DONE + MERGED
   (PRs #8-25):** the WPF module-manager shell (Preferences/Companions/module panes; tray from contributions); Companions
   enrichment/bundle/check-for-new + per-companion **size** + per-companion **sound**; window 1050×820, OS-following theme,
   scroll + dark-scrollbar fixes; and the base now OWNS audio playback (host `AudioOutput`, **DirectSound**
   device picker + Test-sound button, **NAudio 3** back in the base — **WASAPI rejected** for a ~25 MB
   SDK-projection payload cost), which let the **S2 Sound module be retired**. **Next:** S5b-2(d) Fortunes
   pane → S5b-3 (FormOptions/FortunesWebView + WebView2 retired; About/Help now themed WPF windows) → S5c/d/e (AiSettings split + delete the
   residual base fortune/AI code + Options tabs + Newtonsoft→System.Text.Json) — **all DONE + MERGED.**
   **S6 phase 1 (bare host + in-app Modules catalog) — DONE + MERGED (PR #68, 2026-08-11), detail below.**
   **Next: S6 phase 2** (Companions becomes a module too, pre-installed by default) — full plan in
   [`S6P2-PETS-MODULE-PLAN.md`](S6P2-PETS-MODULE-PLAN.md), which folds in the old #16 (per-companion
   personality/voice). **S7 (third-party module code-signing + consent) is DROPPED (2026-08-13):** real
   signing isn't coming any time soon, and S6 phase 1's hash-pinning + permissions-consent already covers
   the in-catalog case — revisit if/when third-party signing is actually on the table. **TTS was DROPPED as
   a feature (2026-08-13):** not ready to build.

**v1.8.0 (2026-08-26) — shipped:** a fourth catalog module, **Reminder** (the companion announces calendar
events before they start; sources: a local JSON feed, a Calendar URL / ICS via iCal.Net for Google /
published Outlook / M365 / iCloud with recurrence + time zones, and a running desktop **Outlook over COM**;
multiple lead times, quiet hours, an optional chime, the event location, and module-owned speech styling).
Plus the **module-owned styled-speech** platform (`SpeechStyle` on the ABI + `IHost.Say/SayAll(text, style)`,
the bubble a dumb renderer, ModuleKit `SpeechStyleSettings` so any module gets the controls in ~2 lines);
two global **Sound** master switches (**companion sounds** vs **notification sounds**); Companion Studio's **"Analyze
installed companion"** dropdown backed by the new `ICompanionManager.TryReadTypeXml`; the shimeji converter's
frequency-weighted behaviour + WAV→MP3 sound capture (all shipped companions re-converted); and the Fortunes
smart-picker repeat fix. **Still deferred:** the MSI `util:CloseApplication` (needs a second hash-pinned
WiX extension + a local MSI build to verify — pins recorded in `installer/DesktopAICompanion.wxs`).

**Reminder module — companion physically reacts to certain events — ✅ DONE (reminder 1.7.0, 2026-08-27), and it
needed NO host change.** When a reminder fires the companion now plays an attention animation
(`reactOn` default on, `reactAnimations` default `boing,jump,run,flower`), fired before the bubble and also
from the per-slot Test button. **The claim this entry used to make was wrong, and it cost a planning cycle:**
it said "the plugin ABI does not let a module drive a specific companion animation or move a companion today, so this is a
host-release item". `IHost.TryPlayAnimation(ICompanion, name)` and `IHost.PlayAnimationAll(candidates)` have existed
since the emotion work and are wired in `CompanionHost` (`:216`, `:231`) — AiBrain has been using them for its
emotion map all along. The module owns the candidate list and the host picks the first name each companion's XML
actually defines, so no new verb was needed. *Before writing "needs a host change" in this file again, grep
`PluginApi.cs` for the verb.*
- **Still genuinely missing (deferred by decision, 2026-08-27):** MOVING a companion ("walk to centre screen").
  That does need new ABI, and it is bigger than it sounds: companion position is driven by animation velocity
  expressions rather than set directly, so a "move to point" verb would fight the engine rather than sit
  beside it. Not attempted.
- All the other Reminder feature work (join links, agenda, briefing, filters, per-slot test, typed
  reminders, hush-while-presenting) is module-only and ships through the catalog without a release.

**Remembrance module (meeting recorder) — BUILT + PUBLISHED to the catalog 1.0.0, host v1.9.0 released
(2026-08-26).** Full spec + build status in [`REMEMBRANCE-PLAN.md`](REMEMBRANCE-PLAN.md). Records mic +
system loopback, offline Whisper transcription, calendar naming/roster, snapshot hotkey, 72h purge (keeps
transcript). Host ABI grew to 1.9.0 (shared-context channel + Microphone/SystemAudio permissions); Reminder
1.6.0 publishes `meeting.current`. Both modules are in the catalog and need the v1.9.0 host.
**Remembrance 1.1.0 (2026-08-27) — one-click Whisper setup + the local AI summary.** The setup friction was
the real barrier to anyone else testing this module, not any missing feature: it took two file paths and gave
no way to obtain what they point at. Now `WhisperInstaller` detects an existing install first (including the
`%LOCALAPPDATA%\DevToolbox\whisper` layout `install-whisper.ps1` produces) and otherwise fetches
whisper-bin-x64.zip from the whisper.cpp GitHub release (SHA-256 checked against the asset digest) plus the
chosen GGML model from Hugging Face, then proves the pair runs by transcribing generated silence (exit code
0 is the assertion, not transcript text — silence legitimately produces none). Model choice
tiny.en/base.en/small.en. **And #17's P3 landed:** `OllamaSummarizer` writes `<capture>.summary.txt` beside
the transcript, off by default, map-reducing long transcripts so a small local context window holds. Local
only, permanently — no cloud provider, no key field. Costs the module the `Network` permission.
**Still to verify:** a real recording smoke test on a machine's LOCAL CONSOLE — a Remote Desktop session
presents no mic/speakers, so capture cannot be tested under RDP (the user is testing on separate
workstations). The live WASAPI capture + mix remain build-verified only; the download, the whisper-cli run
and the summary map-reduce are all now verified live on the dev box.
Diarization (speaker labels) is deliberately deferred to a follow-up.
**Follow-up ideas:** refresh the device dropdowns without an app restart (the ABI builds the schema once at
load, so this one genuinely does need a host change).

Full status, the expand/contract plan, and gotchas live in **[`handoff.md`](handoff.md)** and the
`project-desktoppet` memory note. **Feature item #9 below (Fortunes tab overhaul) is subsumed by this work**
— the fortunes UI is rebuilt in S5 (WPF, driven by the module's schema), not tweaked in place.

**Converted-companion ANIMATION TIMING — ✅ FIXED (2026-08-27 evening). Never pick a fixed repeat count.**
Found by live smoke test ("the Knight read a book for 4 seconds, it should be 10"). Two causes:
- Every non-locomotion animation was emitted `repeat="0"`, i.e. ONE pass. Shimeji holds a `Stay` action for
  as long as the BEHAVIOUR that ran it says to, and the behaviour layer is exactly what this converter does
  not reproduce, so the dwell has to be supplied at conversion. Hornet's Sprawl ran 2.4s, its BePet 0.2s.
- A single-frame hold has ONE interval, capped at `MaxInterval` (4s), so repeating it could only reach
  MULTIPLES OF 4. The reference conf authors rest poses as `Duration=250` = exactly 10s, and 8s was the
  nearest reachable value. Now a single-frame rest picks the fewest passes that keep each interval under the
  cap and divides the target evenly: 10s = 3 passes of 3333ms. Splitting matters because the interval is
  also the animation's TICK -- one 10s frame would mean 10s before the companion noticed it should fall.
- **Rests round UP, walking rounds to NEAREST.** Undershooting a rest reads as a twitch; overshooting a walk
  means gliding past where you expected it to stop.
- **The standing rule: never pick a fixed repeat count.** It has been the bug twice now -- a fixed 3 on
  Hornet's 32-frame climb produced a 51-SECOND wall sequence, the same failure `TargetLocoMs` was created to
  prevent. Everything goes through `RepeatCountForBudget` now.

**Companions hovered above the floor, then bled between tiles — ✅ BOTH FIXED (2026-08-27 evening).**
- The compositor sized each cell as `oy + below`, reserving a band UNDER the anchor. But the Shimeji
  ImageAnchor is the ground-contact point and the host stands a companion by putting its WINDOW's bottom edge on
  the floor -- and the window is one cell. Every converted companion floated by whatever `below` was. Anchor now
  sits on the cell's bottom edge; 6 companions hovering -> 1, worst 20px -> 1px, and the sheets got smaller.
- That immediately caused a **black blob** in the corner of frames: the cell got shorter but `BlitOpaque`
  still drew the WHOLE sprite, so a frame taller than its tile bled into the neighbour. It now clips to the
  room remaining inside the tile. Verified by extracting the drag tile as a PNG before and after.
- ✅ **DONE (guarded 2026-09-01) — horizontal inset.** The engine already contacted the border with the
  CHARACTER rather than the window (`ins.Left`/`ins.Right` on both the detection and the resting position, at
  both screen edges and both window edges), so the behaviour was right — but nothing tested it, which is why
  it read as still open. Measured on the shipped corpus: hand-authored companions are cropped tight (0px both
  sides); converted companions are not, because the compositor sizes one cell to fit the largest pose. Hornet's
  walk sits **175px** from the left of its 256px cell and **22px** from the right. The asymmetry is the trap —
  correcting one side looks correct on one wall and wrong on the other, and a companion that never walks left would
  never show it. Now pinned by a source invariant over all four sites, mutation tested 4 ways. Original note: Hornet's standing frame sits 176px into a 256px cell, so at a screen
  edge the visible character looks inland (reported as "climbing not at the edge"; entry really is
  screen-edge-only, verified against all six `SetNextBorderAnimation` call sites). The cell cannot simply be
  trimmed -- across all frames the content fills it -- and the compositor bakes the x offset into pixels
  because the format's `<offsety>` is y-only. Needs its own design.
- ✅ **DONE — a companion can get stuck to the mouse.** Fixed; confirmed by the user 2026-09-01. Original note: Reported once, not reproduced. The companion graph and the
  engine's mouse-up path (which sets the fall animation and clears `IsDragging`) both look correct, so the
  suspicion is lost mouse capture. Pre-existing rather than from the converter work.

**Wall climbing for converted companions — ✅ DONE (2026-08-27). Ceiling is the remaining half.**
Converted companions stayed on the floor and the residue said wall/ceiling "are not represented", which reads as a
format limit. It is not one: **17 of the 22 hand-authored companions use wall/ceiling/window transitions** (the seven
Oliver B. sheep each carry 153 `only="vertical"`, 48 `only="horizontal"`, 135 `only="window"`). Only the
converted companions lacked it.
- **The cling is the ABSENCE of `<gravity>`.** Presence of that element is what makes the engine drop an
  unsupported companion, so omitting it keeps a companion on a wall. Climbing is just negative Y velocity. Both read off
  `yellow_sheep`/`pink_sheep`, not guessed.
- Implemented as a second REGION (`IsWallAction`), unreachable from the floor hub so a wall-cling can never
  play mid-screen; entry is a weighted `only="vertical"` border edge on locomotion (climb wins 1 in 3);
  exit is the existing `fall` magic animation. The floor region's `VelY < 0` guard stays (there it launches the
  companion off-screen); the wall region lifts it, because there it IS the behaviour.
- **The wall region accepts Group1 AND Group2.** Group2 means the selection CONDITION needs host state we lack,
  not that the animation is unconvertible, and this region replaces Shimeji's conditional selection anyway.
  A Group1-only filter took `GrabWall` but not `ClimbWall` (Group2, its condition reads `mascot.anchor`) and
  produced a companion that grabs a wall and hangs there motionless.
- **29 companions re-converted from local sources** (a migration is impossible: new sprite frames must be baked into
  the sheet). 28 gained climbing; `2l6qm2v5`'s source skin has none. Sources: `shimeji-catalog/data/catalog.csv`
  maps `source_item_id` -> `blob_path` for 24, named zips for 3, the Shimeji-EE bundle for 2.
- **Cell geometry verified UNCHANGED for all 29** (256x256 before and after). That was the real risk: wall
  poses share the floor anchor (64,128) so the cell stays tight, whereas CEILING poses anchor at 64,48 and
  would pad it, floating every floor companion. Growth is frames only, and content went 48.1 -> 62.6 MB.
- **Still to do — the ceiling.** Needs the anchor normalised to the floor convention plus a per-animation
  `<offsety>` ("shifts the drawn sprite without moving the collision position", which the format reference
  says is for climbing/peeking). Entry must come ONLY from the wall region, which is what defuses
  `only="horizontal"` firing at both top and bottom. Watch the size budget: `3g8t9v4e` is already 9.4 MB of
  12 MiB. Jump stays out.
- **Not possible, so do not promise it:** climbing the SIDE of an application window. `only="vertical"` is the
  screen border; the window-aware filters (`only="window"`, `only="taskbar"`) mean standing ON a title bar or
  the taskbar.

**Converted companions' hub weighting — ✅ FIXED (2026-08-27). Read this before touching `HubWeightFor`.**
Every converted companion had animations that were reachable in theory and invisible in practice. The emitter set
each hub transition to `HubBaseWeight(4) + accumulated frequency`, and `BuildSpokeWeights` SUMS a frequency
per referencing behaviour, so locomotion reached ~1100 while a one-off pose stayed at 4. Across the 27
shipped companions: **368 of 582 animation options below 1%** of their hub's pool, worst 0.03% — one appearance per
~54 minutes of idling at Hornet's real cadence. This is the earlier "shuffles animations but never goes
anywhere" fix having over-corrected from flat to extremely peaked.
- Fixed by damping (`HubWeightFromFrequency`, `4 + round(3*sqrt(f))`, preserves ordering) then flooring
  (`ApplyMinimumShare`, nothing below **1.5%** of the pool). Corpus after: **0 options under 1%**, worst
  1.51%, ratio 326x → 22x, mean top-3 share 66% → 47%. Curve chosen by simulating four candidates against
  the real committed companions, not by taste.
- **The hub's own re-selection edge is excluded from the floor** and must stay excluded: it is every spoke's
  RETURN target, so lifting it makes the companion loiter instead of acting. Also why tooling reports the rarest
  *animation* rather than the rarest edge.
- Migration is `ShimejiConvert reweight <PetsDir>`. It needs no source skins (frequency is recoverable as
  `probability - HubBaseWeight`), which is exactly why it is **gated on the companion's header version** (1.0 →
  1.1) — running it twice would re-curve an already-curved weight. Second gate is the converted-author
  string, so hand-authored companions are untouchable.
- Pinned by `HubWeightSelfTest` (14 assertions) inside `ShimejiConvert selftest`.
- Reachability analysis will never catch this class of bug: it proves an animation CAN play, not that it ever
  does. If weighting changes shape again, bump `ConvertedFormatVersion` in the same commit.

**Shimeji import + catalog (BACKLOG #4) — DONE (2026-08-25) and LIVE on master since then.** (This entry
said "awaiting master" until 2026-08-27; it was already merged. `catalog.json` serves 27 shimeji companions out of
49 total.)
Two converters: desktop Shimeji-EE (`actions.xml` + PNG, folder or zip) and Android JSON+WebP bundles, both in
the shared `tools/ShimejiConvert.Engine` (CLI verbs `verify`/`convert`/`convertroot`/`convertbundle`). **Companion
Studio 1.3.0** imports both formats (folder or zip) → convert → residue report → preview → install, per-pixel
alpha preserved (host renders `<transparency>Alpha` via `UpdateLayeredWindow`). WebP alpha is decoded by a
bundled libwebp `dwebp.exe` (Windows WIC decodes WebP to opaque BGR32 and drops it). The standalone catalog
module was **retired**; the converted skins ship as ordinary download-on-demand catalog companions under
`Companions/shimeji-<id>/` (26 so far — 21 shimeji.org + 5 shimejis-xyz, real names/authors/sources in `companions.json`,
excluded from the portable bundle, thumbnails in `companion-thumbnails.zip`). Companion XML budget raised 4→12 MiB for
frame-heavy skins; the runtime still caps on-screen frames at 256 px (the memory guard is unchanged). **Not
done:** merge/push to master (the publish); a content-rating pass before the catalog is genuinely public; and
the 12 MiB companions require the new app build — an old 4 MiB app rejects the WHOLE catalog (`RemoteCatalog.Parse`
is all-or-nothing on any over-limit entry).

**S6 phase 1 — bare host + in-app Modules catalog — DONE + MERGED (PR #68, 2026-08-11).** Root problem:
neither the MSI nor the portable ZIP had ever shipped Fortunes/AiBrain — both ship the base companion engine only,
with modules only ever existing in raw dev/CI build output. Everyone who's downloaded a release got the base
engine, none of the actual product story. Original plan (`enchanted-sniffing-swing.md`) was to statically
bundle modules into the installer; discussed with the user and pivoted to something better — an in-app
**Modules** pane that fetches modules the same way companions/fortune packs already do (HTTPS + SHA-256-pinned
catalog fetch, user picks, downloaded on demand), which also quietly absorbs what was going to be a separate
later S7 stream ("signed catalog + consent") since a catalog that downloads and runs code needs hash-pinning
and a permissions-consent step regardless of when it's built.
- **`RemoteCatalog`** gains a third parallel list, `CatalogModule` (mirrors `CatalogCompanion`/`CatalogPack`
  exactly), carrying each module's declared `ModulePermissions` so the install prompt shows what a module
  *will* be able to do before its code is ever downloaded or run.
- **New Modules pane** (`ModulesPaneControl.cs`), fixed second in nav after Preferences. `OptionsShell.
  CollectPanes()` changed from load-order to: Preferences fixed first, Modules fixed second, then
  **everything else alphabetized** (Companions today, plus any module-contributed pane) — so install order never
  affects where a pane lands.
- **Modules only load at startup** (no hot-load — that was explicitly scoped OUT after discussion: a
  same-process reload would need to wire tray items/options panes/lifecycle events into an already-running
  app, real extra complexity for marginal UX gain over "restart and reopen where you were"), so install/
  uninstall restarts the app. This reuses `Program.cs`'s `RequestRestart`/`TryRequestRestartAfterSave`/
  `CompleteInstanceLifecycle`/`LaunchReplacement` chain — which existed, fully self-tested, with **zero real
  callers** until this PR. Threaded an optional `--reopen-options=<pane>` argument through the whole chain so
  the relaunch reopens Settings back on the Modules pane (not literally the same window instance — that
  would be hot-load — but a fast enough bounce + auto-reopen that it reads as continuous).
- **Real bug caught in live testing, not by any self-test:** the first live Uninstall attempt failed with
  "Access to the path 'Fortunes.dll' is denied" — a module's DLL is locked by the OS for as long as its
  `AssemblyLoadContext` is loaded in the current process, so deleting it immediately can never work. Fixed
  with `PendingModuleRemovals`: Uninstall marks the id (a small file under `AppPaths.DataRoot`, deliberately
  NOT inside `modules/` itself so it's never mistaken for a module by the loader's directory scan) and
  restarts; the *next* process deletes both the install folder and the module's data folder before
  `ModuleHost.LoadFrom` ever gets a chance to re-lock them.
- **Packaging:** `New-ModuleDistZip.ps1` (new) zips a module's build output — excluding `.pdb`/`.lib`,
  matching the base's own lean-manifest convention — into `modules-dist/<id>.zip`, the exact shape the
  install flow extracts straight into `modules/<id>/`. `modules-dist/modules.json` carries the catalog
  metadata (name/desc/version/permissions) a bare zip can't self-describe. `New-ContentCatalog.ps1` extended
  to emit a `modules` array in `catalog.json` alongside companions/packs, hashing each zip as the actually-committed
  git blob (sequencing matters here: the zip must be committed *before* regenerating the catalog, or the
  generator's CRLF-normalizing text fallback corrupts a binary hash — `*.zip -text` in `.gitattributes`
  already guarantees the committed bytes are exact, but only once they exist as a commit to hash).
- **Fully verified live, not just gated:** built and published Fortunes + AiBrain as the first two real
  catalog modules; the user ran the complete loop for real — Uninstall Fortunes (hit the DLL-lock bug, fixed
  it) → restart → confirmed gone → **Check for modules online** against the real published `catalog.json` →
  Fortunes surfaced as available → Install → restart → confirmed restored. User: "it works."
- **Not done (S6 phase 2, separate stream):** Companions becoming a module too (pre-installed by default, unlike
  Fortunes/AiBrain) — needs new `IHost` ABI verbs for spawn/remove/mix (today's multi-companion orchestration in
  `StartUp.cs` reaches `FormCompanion`/`CompanionTypeRegistry` directly, which a real module can't do), scoped at lower
  detail in the plan file since it's genuinely bigger than a file move.

**DROPPED (2026-08-13) — TTS / speech module.** Not ready to build; parked, revisit later if TTS becomes
worth it. *(Original note kept for reference:)* the "B" audio arc made the base own a shared
audio output (host-owned `AudioOutput`, DirectSound, device-selectable) and retired the S2 Sound module. A
future **text-to-speech module** can then speak calendar events / appointments through the same mixer,
ducking companion SFX. Needs its own plan: which TTS engine (local `System.Speech` / `Windows.Media.SpeechSynthesis`
vs a cloud/LLM TTS), what triggers it, and an ABI `Speak`/`PlaySound` host service so the module produces
audio through the shared output. Deferred per the user 2026-08-07 ("another module entirely").

  - **UX (user request 2026-08-25):** add a user-facing "silence companion sounds" checkbox under Audio, so a companion's
    embedded `<sound>` SFX (e.g. a companion "yelling") don't fire while a speech bubble is up waiting to be read.
    This is a manual toggle alongside the automatic SFX-ducking above — some users simply want the companion quiet
    when it "talks". Wire it when the TTS/voice module lands (the base already owns `AudioOutput`, so the mute
    can hook there). Now relevant because converted shimeji can carry real `<sound>` SFX as of v1.8.0.
    **2026-08-26:** the manual half shipped as the global **companion sounds** toggle in Preferences → Sound; the
    automatic duck-while-a-bubble-is-up idea is the part that remains open.

---

## Status (2026-07-27)

- ✅ **Phase 1** — speech bubble (`FormSpeech`) shipped.
- ✅ **Phase 2** — Ollama brain (`dotNet/Ai/`): capture → OCR/vision → `/api/chat` → `{text,emotion}`.
- ✅ **Phase 3** — triggers: global hotkey (`Ctrl+Alt+P`), idle-commentary loop + gate.
- ✅ **2.8** — emotion → animation mapping (`FormCompanion.TryPlayAnimation` + `StartUp.EmoteAll`).
- ✅ **3.6** — "thinking" animation cue while the model responds.
- ✅ **Phase 4** — AI settings tab in the tray Options dialog (`src/Portable/FormOptions.cs`), applied live via `StartUp.ReloadAiSettings()`.
- ✅ Launch warmup + Ollama server auto-start.
- ✅ **Phase-1 Speech tab** ported into the compiled `src/Portable/FormOptions.cs` (`3c3393e`) + AI-aware greeting.
- ✅ **Phase 5** — context & memory (active-window + screen-zone, time-of-day, persona, rolling `chat-history.json`).
- ✅ **Phase 6** — vision path tested + fixed (routed hotkey-only, 896px image, timeout 120, sane defaults).
- ✅ **MIT license** for the fork's additions + **Phase 7.1 per-user WiX MSI** installer (`installer/`).

---

## Post-v1 backlog (added 2026-07-29)

### ✅ DONE (2026-09-01, petstudio 1.5.0) — Behaviour debugger: drive a live pet's animations by hand

Shipped as Companion Studio's behaviour timeline: drag animations from the reachability map into a chain,
colour-coded by whether the companion's own graph offers each join, with a per-step repeat count, run on a
throwaway companion whose animations are cloned and wired nose-to-tail — so the ENGINE runs the chain with its own
timing and physics rather than a sequencer guessing durations. Still untested end-to-end: the Run button has
no automated coverage (there is no way to drive the tray from a test), which is recorded below.

Original request follows.

**A debug window that sends animation commands to any live companion, so a behaviour can be watched on demand
instead of waited for.** Build a chain of that companion's actions by drag and drop, colour-coded by whether each
step is a transition the companion's own graph actually offers or one we are forcing for the test, then trigger the
chain, optionally N times over ("10x jump back to back").

**Why this is worth doing, from the session that needed it.** Fixing the jump landing (PHASE 0, above) needed
three separate workarounds because there is no way to make a companion do something:

- The arc had to be verified by re-implementing the engine's interpolation in a throwaway script and
  replaying it over the emitted XML. That is a *model* of the engine, not the engine, and its fidelity was
  never checked against the real thing.
- Watching it live meant cranking a copy of the companion's hub weights to ~99% jump in an isolated
  `DESKTOP_AI_COMPANION_DATA_ROOT`. So what got watched was a modified companion.
- Hornet jumps roughly **once every three to five minutes** at her real weights, which makes "just watch it"
  useless as a verification step. Landing behaviour is a distribution over weighted edges: 26 samples took 2.3
  simulated hours to collect. A trigger button collects them in a minute.

**Most of it needs no host change.** `IHost.TryPlayAnimation(ICompanion, name)` and `IHost.PlayAnimationAll(...)`
already exist (`PluginApi.cs:425,428`, wired in `CompanionHost.cs:216,231`, and `FormCompanion.TryPlayAnimation` at
`FormCompanion.cs:2359`); AiBrain and Reminder both use them. `ICompanionManager.TryReadTypeXml` already lets a module
read a live companion type's XML, which is where the animation list and the edge set come from. **So this belongs in
Companion Studio**, which already source-links the converter engine, `Xml.cs` and the validator, and already has a
window — no host release, ships through the catalog.

**The colour coding is the interesting half, and it is free.** "Natural" vs "artificial" is exactly the edge
set the emitted XML already carries: a step is natural if the previous animation lists it under `<next>`
(sequence end), `<border>` or `<gravity>`, and the badge should say WHICH, because they are not
interchangeable. Three shades, not two: sequence-end, border-only (the companion must be touching something), and
forced. Border-only matters more than it sounds — see the trap below.

**The one part that does need thought: knowing when an animation has finished.** Nothing in the ABI reports
it, so a chain has to either wait a computed duration or gain a new callback.

> **Do not compute the duration as the declared sequence length. That is the trap this feature exists to
> expose.** A jump does not end at its sequence end; it ends when it hits the taskbar, which on the old
> Grapple1 was step 12 of 28 — 16 steps never played, and 57% of the declared duration never elapsed. Any
> border-terminated animation (jump, fall, wall climb) behaves this way. A duration-based sequencer would fire
> the next step while the previous one was still running and quietly produce a different chain than the one on
> screen, which is worse than not having the tool.
>
> Two honest options: (a) poll the companion's current animation through a small read-only ABI addition
> (`ICompanion.CurrentAnimationName`) and advance on change, which needs a host release but is a two-line one; or
> (b) fire the chain one step at a time from a manual "next" button and make the automatic N-times mode wait
> on (a). Option (a) is the one worth having, and it is also what would let the window show a live trace of
> what a companion is doing on its own, which is the other thing this session had no way to see.

**Also worth putting in the same window, since the data is already there:** the reachability report and the
hub weights, so "this animation plays once every 54 minutes" is visible rather than something that has to be
simulated. The rarest-animation number has already been a shipped bug twice.

### ✅ DONE (2026-09-01, aibrain 1.3.0 + 1.4.0, host 1.9.9) — AI Brain: give the user back their VRAM

Both halves shipped, and BOTH changed shape on review:

* **Residency** became ONE setting, not two. The first attempt was an eject-after-N-seconds field beside the
  existing "Preload model on launch"; those can contradict each other (preload pins keep_alive to 10m), so the
  pane needed a paragraph explaining the interaction. Replaced by a single "Model residency" choice — unload
  after each remark (the default) / keep loaded / leave it to Ollama. A single choice cannot disagree with
  itself and needs no conditional UI. That merge also exposed a latent bug: -1 had been used as the "send
  nothing" sentinel, but -1 is a real instruction to Ollama meaning "resident for ever".
* **The pane states fact, not a documented default.** It reads GET /api/ps and reports what is resident now
  (model, GB, seconds to eviction). The documented default is 5 minutes, but OLLAMA_KEEP_ALIVE overrides it
  machine-wide, so printing it would be wrong on exactly the tuned machines.
* **Fullscreen stand-down** needed the ABI gap below closing: IHost.IsFullscreenActive + FullscreenChanged
  (host 1.9.9), exposing the existing FullscreenScan rather than adding a second detector. The spec gained a
  step the original plan missed — it EVICTS what is already resident on detection, because a model loaded
  before the game started is not helped by declining to load.

Original request follows.

Two settings, same motive: a local model sitting in VRAM between quips is a cost the user did not agree to,
and on a gaming machine it is a risk rather than a cost.

**1. "Unload the model N seconds after a quip", with the default shown.**

The quip path sends **no `keep_alive` at all** (`modules/AiBrain/engine/OllamaClient.cs`, the `/api/chat`
payload), so Ollama's own default applies and the model sits in VRAM after every remark.

> **The default is 5 minutes, not 30.** Confirmed against Ollama's own FAQ, not from memory: "By default,
> Ollama keeps models in memory for 5 minutes." `keep_alive` takes a duration string, a number of seconds, `0`
> to unload as soon as the response is done, or a negative value to keep it resident indefinitely.

**Do not hardcode "5 minutes" in the label**, because it can be wrong on the user's own machine:
`OLLAMA_KEEP_ALIVE` sets a server-wide default that overrides it. `GET /api/ps` returns `expires_at`,
`size_vram` and `context_length` per running model, which the module does not call today. Showing the LIVE
residency and VRAM figure in the pane beats claiming a default, and it is the honest version of what was asked
for ("the text should say whatever the default is").

**Implementation is one field, not a timer.** Put `keep_alive: <seconds>` on the chat request. Ollama then
evicts N seconds after the response with no further traffic, and it still evicts if the app is closed in the
meantime. The alternative (fire the existing `AiBrain.UnloadAsync` from a timer N seconds later) needs a timer,
races with a second quip arriving inside the window, and leaves the model resident if the app exits first.
`UnloadAsync` already exists and already sends `keep_alive: 0`, so keep it for the explicit "unload now"
button and do not build the chain on it.

**Two things the pane has to say, or the setting will read as broken.** A short `keep_alive` and the existing
**"warm up on launch"** setting actively fight each other: `WarmUpAsync` pins `keep_alive` to `10m`, so a
warmed model outlives a 5-second eject setting until the next quip re-stamps it. And the reload cost is real
and is paid per quip, so the pane should state it rather than let the user discover it as lag.

**2. "Don't load a model while a fullscreen app is running" (fall back to fortunes).**

The reasoning is sound and it is a crash risk, not a politeness one: a model claiming several GB of VRAM while
a game already owns it can take the game down.

Both halves already exist and only need joining:

- **The detector is built and tested.** `FullscreenScan.BlockedMonitors` plus
  `DesktopGeometry.IsFullscreenOnMonitor` is what already stops a companion covering a fullscreen window;
  `StartUp.CheckFullScreen` polls it every 300ms and `--fullscreen-selftest` covers it. Note it tests
  *fullscreen*, not *maximised*: a maximised window leaves the taskbar visible and does not count, which is
  the behaviour wanted here too (an alt-tabbed game usually still owns its VRAM, so consider whether the
  predicate should be "fullscreen" or "a fullscreen window exists on any monitor, foreground or not").
- **The fallback path already exists.** The unprompted-remark responder returns a bool, and declining already
  means a local fortune speaks instead (`modules/AiBrain/AiBrainModule.cs`, the drop responder). So the change
  is an early `return false`, not a new code path.

**The one real cost is the ABI.** `IHost` exposes `IsDarkTheme` but no fullscreen predicate, so a module
cannot ask. The options are a small additive `IHost.IsFullscreenActive` (needs a host release and a
`MinHostVersion` bump on the module, the same pattern `CompanionsDirectory` at 1.4.6 and `TryReadTypeXml` at 1.8.0
used) or the module re-implementing `EnumWindows` itself. **Prefer the ABI addition:** duplicating it would put
a second implementation of one policy in the tree, which is the thing source-linking exists to prevent, and
the host's copy is the one with the self-test.

Worth pairing with the eject setting in the same release, since both are "the model should not be resident
when I am not being quipped at" and they share a pane.

### ✅ DONE (2026-09-01, v1.9.10) — the launch update check blinded a fresh install for 24 hours

Shipped in v1.9.8 and reported the same day: closed 1.9.8, reopened, no flag for 1.9.9. The app was correct;
the design was not. The check stamps "I looked" even when the answer is "nothing newer" (right — an offline
machine must back off), but the interval was a DAY, and the first check after ANY install is negative because
you just installed the newest build. So every fresh install went blind for its first 24h — the exact window in
which someone restarts and expects to be told.

Interval is now **one hour** (it bounds network traffic when the answer is "no"; it was never a claim about
freshness), plus a **refresh when Preferences opens** on a 1-minute floor — the footer is the only surface the
answer ever appears on, and it is the only thing that lets a long-running instance notice at all, since a
process left open for days never re-runs its launch check.

Four mutations, all firing, the first restoring the reported bug exactly.

**Carries a known limitation:** an install on 1.9.8 or 1.9.9 still has the old 24h logic baked in, so this
improves future updates rather than the one it announces. Clearing `appUpdateLastCheckUtc` in `settings.json`
with the app closed makes an older build re-check immediately.

### ✅ DONE (2026-09-02, v1.9.11 → v1.9.13) — the fullscreen, monitor and scaling round

Four smoke-test reports from the user, four fixes, four releases in one morning.

- **v1.9.11** — a hidden companion could respawn back over a fullscreen game and stay there. The hide was LATCHED
  behind `!_fullscreenHidden`, so it ran once per fullscreen transition; anything that made a companion visible
  afterwards (respawn, a spawned child, `spawn_ship`'s UFO) won permanently. Enforcement now runs on every
  scan, and `Play()`, the child show path and the drag re-topmost path each consult the current state.
- **v1.9.12** — pin a companion to one monitor (`PetMonitors` in settings, a per-companion row in the Companions pane that
  only appears with 2+ screens), and relabel "allow multiple screens", which never meant traversal.
- **v1.9.13** — two bugs in one commit:
  - A small companion played its walk cycle **on the spot**. `ScaleD` rounds a scaled velocity to an int, so a walk
    of -2 px/step at 25% is -0.5, and banker's rounding makes that exactly 0. Reported on a 25% Luffy, but it
    hit any companion whose walk is 1-2 px/step, which is most of them. `ScalePolicy.ScaleVelocity` now keeps the
    sign and floors the magnitude at one pixel. Zero still stays zero, so a still pose is never given motion.
  - A companion pinned to monitor 2 **spawned on monitor 1**, an ordering bug in v1.9.12's own code:
    `AddSheepCore` calls `Play()` inside its initialize callback and registers the companion only afterwards, so
    `PinnedDisplay`'s registry lookup missed and fell back to the ACTIVE companion's id. It reads
    `FormCompanion.PetTypeId` now, which is populated before the form is constructed.

**The guard gap worth remembering:** `ScaleVelocity` was unit-tested and correct, and the first mutation
sweep still came back SILENT for "the walk X velocity goes back through ScaleD" — nothing asserted
`Animations.cs` actually CALLED it. A correct function nobody wires up, for the second time in two sessions.
The invariant's first form was also too loose: it matched one `ScaleD(...UnscaledOffsetY)` and passed while
the other had already been switched, so it now names both offsets and asserts the ABSENCE of `ScaleVelocity`
on either.

### ✅ DONE (2026-09-03, RELEASED as v1.9.16) — the tray icon a fresh install could not show

From the v1.9.15 smoke report: "on a fresh install, the companion is there, there is NO tray icon."

The icon was registered, working, and clickable the whole time — sitting in the Windows 11 hidden-icons
flyout as one of thirty. Windows files every tray icon under `HKCU\Control Panel\NotifyIconSettings` and
hides any entry whose `IsPromoted` value is **absent**, which is the state of every brand-new install. There
is no API for an app to promote its own icon, so `TrayPromotion` writes that value directly, **only when it
is absent**: Windows writes `0` when the user drags the icon back into the flyout, so absent means "never
chose" while `0` means "hidden on purpose" and has to stand. That makes it self-limiting and needs no
setting of its own. HKCU, no elevation, and the shell honours it live.

Same bug's second half: `SetIcon` assigned `ni.Icon` before `ni.Text`. WinForms only issues the `NIM_ADD`
once an icon exists, and Windows permanently caches the tooltip from that first ADD — so every companion was
labelled "eSheep Desktop Pet" forever, which is exactly what a user reads when scanning the flyout for their
companion. The order is now an asserted invariant, compared by POSITION rather than presence, because both
statements are there either way.

Path matching is exact on purpose: the development machine held eight `NotifyIconSettings` entries whose
executable is named `DesktopAICompanion.exe`, and a filename match would promote another copy's icon.

**Smoke test A4 was "the tray icon is there and its menu opens" — which this bug passes.** Rewritten to
require the VISIBLE tray, with A5 for the label and a pointer from K4, since an install is the one path that
creates a brand-new Windows entry.

### ✅ DONE (2026-09-02, RELEASED as v1.9.15) — the in-flight batch: installer, update checks, companion reload, signing

Five threads closed in one pass, held back from a release until all of them were resolved.

- **Installer.** A "clear all settings and modules" checkbox (off by default, driving
  `DesktopAICompanion.exe --factory-reset`), `util:CloseApplication` so a running companion is closed rather than
  prompted about, launch-on-finish ticked by default, a working **Repair**, and the sidebar given the light
  content panel WixUI's layout assumes. 19 assertions in `packaging/Test-MsiSurface.ps1` now run inside
  every installer build, and `-UpgradeCodeOverride` + `-ProductNameSuffix` produce a side-by-side test MSI
  so the UI can be exercised on a machine already running the shipped build. **Verified interactively.**
- **The files-in-use prompt was an APP bug.** `Application.Run()` was called with no main form, so the loop
  had nothing that could end it: Restart Manager asked, got nothing, and had already closed the windows it
  could reach — taking the tray icon with them and leaving companions on screen with no way to quit. Measured on
  the installed layout with 18 module DLLs loaded: shipped build STILL RUNNING after 32s, fixed build exits
  in 316ms. `util:CloseApplication` is kept as the belt to that braces, since it cannot beat the prompt
  itself (it runs at sequence 3999; the prompt is raised at `InstallValidate` 1400, and a deferred action
  cannot run before `InstallInitialize` 1500).
- **Weekly update checks for modules and companions**, surfaced when a pane opens. Both panes already rendered
  updates correctly and failed only because their catalog was null on open. Fixed two bugs on the way: the
  module check was armed inside the module `try/catch` and behind `loadedModules > 0`, and it seeded its
  stamp on a fresh install WITHOUT checking, so a new install stayed blind for a month.
  `ModuleUpdateSchedule` is retired. Three `catalog.json` downloads collapsed to one shared copy.
- **A companion on screen reloads when its skin updates.** The naive remove-then-add respawns the OLD skin,
  silently, because `KillSheep` frees the slot immediately while `registry.Decrement` waits on `FormClosed`,
  so the cached parse is still there. `CompanionTypeRegistry.Add` displaces it safely. The ACTIVE companion asks for a
  restart instead, deliberately: its live definition is in settings.json, not the library folder.
- **Signing scaffolding, inert.** Opt-in on a thumbprint at both entry points, so a normal build is
  byte-identical to one from before it existed. The MSI signature has exactly one legal position (after
  `Normalize-MsiDeterminism`, which refuses to run on a signed MSI, and before the hash seal). CI imports
  from a secret, warns and continues unsigned when there is none, and scrubs the key under `if: always()`.
  Smoke-tested with a real certificate then rebuilt unsigned.

**The recurring lesson from this batch, four separate times:** an absence check in
`runtime-hardening-selftest.ps1` that matches a bare identifier gets defeated by a COMMENT describing the
very thing it forbids. `Application.Run()`, `LoadNewXMLFromString`, `Invoke-Signtool.ps1` and a
`TerminateProcess` value all did it. Assert the CODE form (with its semicolon, its parenthesis, its
surrounding call text), never the name. A second, cheaper lesson: a mutation that only edits a comment
reports SILENT and is indistinguishable from a missing guard.

### Open, found 2026-09-01 while chasing companion behaviour

- 📌 **A one-frame animation with `repeat="0"` is effectively invisible.** Hornet's `Grapple3` is a single
  frame with no repeat, so it renders for ONE tick (~0.1s measured) and cannot be seen. It is reachable and
  it "plays" — it just never appears, which is indistinguishable from a bug to a user and invisible to the
  reachability check that now guards the corpus. Emitter fix shape: give a single-frame non-magic animation a
  minimum on-screen time the way rests get one, or refuse to emit it and put it in the residue report. Worth
  measuring how many companions have one before choosing.

- 📌 **A converted companion's ceiling art can read as "standing sideways in mid-air", and it is not a bug.**
  Hornet's skin draws its ceiling cling as a body lying flat against the ceiling (rotated 90 degrees,
  top-anchored) rather than upside down. The original Shimeji shows the same thing; it only became visible
  once a climb could actually reach a ceiling. An attempt to "fix" it by swapping the wall and ceiling frame
  sets was WRONG and was reverted — the anchoring proves the mapping: ceiling art is composited flush to the
  cell TOP (it hangs), wall/floor art flush to the BOTTOM (it stands), so moving indices between regions
  moves art into a cell position it was never aligned for, and the companion floats 60px above its own feet.
  **The lesson, worth keeping:** a sprite's ROTATION says which surface it was drawn for, and its ANCHOR says
  the same thing independently. Consulting only one made it possible to be confidently wrong. Options if the
  look is ever judged unacceptable: rotate ceiling art in the compositor, or drop the ceiling region for
  skins whose ceiling art reads badly. Not a defect to fix by moving pixels between regions.

- 📌 **The live smoke test has never been walked, across TEN releases (v1.9.4 → v1.9.13).** Everything
  shipped in that span rests on the gate, the behaviour soaks and the mutation suites — none of which opens a
  window and looks at it.
  **This is no longer theoretical.** Four of those ten releases exist only because the USER ran the app and
  saw something: a UFO over a fullscreen game, a companion on the wrong monitor, a companion walking in place. Every one
  was a first-thirty-seconds-of-looking bug that the whole automated suite passed straight over. The gate
  proves the code does what it says; nothing yet proves the code says the right thing.
  **Written out properly on 2026-09-02 as [`SMOKETEST.md`](SMOKETEST.md)** (66 checks in eleven sections, a
  12-minute Core pass, and a regression watchlist mapping each bug that reached users to the row that would
  have caught it). The ten-row table in `docs/RELEASE-CHECKLIST.md` that it replaces had not grown with the
  product since before companions could climb — part of why walking it never felt worth the time. Handed to the
  maintainer the same day; **still unwalked until a report comes back.**

- 📌 **Companion Studio's timeline preview always runs facing LEFT, and should offer a direction toggle.**
  Asked 2026-09-02: does Run pick a random direction? No. `FormCompanion.IsMovingLeft` is initialised to `true`
  and nothing randomises it; the only things that change facing are `<action>flip</action>` at the end of a
  sequence, facing the pointer, and a child inheriting from its parent. The field's own comment explains
  why ("the original eSheep was a Japanese application, so it was normal to see something right to left").
  So a previewed chain containing `walk` always walks LEFT, and you only see rightward motion if the chain
  happens to include the companion's flip animation. That is confusing in exactly the way the `_left` names were.
  **Fix shape, and it needs no ABI and no engine change:** the timeline already COMPILES a throwaway companion, so
  a "start facing right" toggle just injects a synthetic first animation of one frame carrying
  `<action>flip</action>`. Do NOT implement it by prepending the companion's own `turn`, because a hand-authored
  companion may not have one and the names differ per skin; the compiler controls the XML it emits, so a synthetic
  flip works for every companion. Cheap, and it makes a rightward walk directly checkable, which is smoke row B1.

- 📌 **Companion Studio's behaviour-timeline Run button has no automated coverage.** There is no way to drive the
  tray from a test, previews auto-hide under a fullscreen foreground window, and an isolated
  `DESKTOP_AI_COMPANION_DATA_ROOT` kept falling back to eSheep. The chain COMPILER is covered
  (`BehaviourChainSelfCheck`); pressing the button is not.

- 📌 **A companion cannot WALK between monitors, and the setting that sounds like it can does not do it.** "Allow
  multiple screens" only widens the pool a companion is randomly ASSIGNED from at spawn and respawn; once placed, a
  companion lives inside one `Screen.Bounds` for its whole life. The user asked for traversal directly ("if not
  bound then a companion should absolutely be able to traverse monitors") and it does not exist — v1.9.12 relabelled
  the setting to stop it implying otherwise, which is honest but not the feature.
  **Why it is not a small change:** every border, gravity and respawn decision resolves against a single
  screen rectangle. Real traversal needs the walk to resolve against the continuous VIRTUAL desktop, with a
  per-monitor floor and taskbar map, and an edge-crossing rule for the case this box actually has —
  3440×1440 beside 2560×1080, where the shorter screen's floor is 360px above its neighbour's and the union
  is not a rectangle. A companion crossing at floor level would walk into empty space. Handing off mid-animation
  across a DPI change is the second hazard.
  Pinning (v1.9.12) is the escape hatch meanwhile: a pinned companion stays put by construction.

- 📌 **The converter emits a sprite cell per frame REFERENCE, not per unique image, so 26 of 31 converted
  companions carry duplicate cells.** Measured 2026-09-02 by hashing every cell of every sheet. Deduping and
  re-encoding the whole corpus saves **7.0 MB of 48.4 MB (14.6%)**, and it is heavily concentrated: eleven
  companions save 17-29%, the other twenty save under 6% and six save nothing.
  **Two causes, and only one is ours.**
  1. *Ours, and it affects every future import.* A reversed sequence is emitted as fresh cells.
     `shimeji-brq51bkr`'s `descend_left` uses frames 62-87, which are frames 61-36 (its `climb_left`) in
     exact reverse: 26 duplicated cells, 1.08 MB, to express "play the climb backwards". `<sequence>`
     already accepts an arbitrary frame list, so a reversed list costs ZERO cells. Same palindromic
     signature in `06n2wuu6`, `1l2yvz73`, `88f9sqb5`, `kinitopet`.
  2. *The source's.* Seven companions (`08dkbwmb`, `36po5aw2`, `3x56f4pl`, `55atqs1b`, `7gb3ediv`, `9qc0h184`,
     `dqjd9s2d`) have a byte-identical duplicate structure, so they came from one Android-Shimeji template
     that ships duplicate sprite FILES. Luffy's source sprites 52-59 are byte-identical to its climb set.
     The converter faithfully gave each source index its own cell.
  **✅ DONE the same day (2026-09-02, format 1.6 -> 1.8, master at `65eb2c0`).** I first recommended NOT
  re-migrating, on the grounds that changing every `sha256` makes existing users re-download ~40 MB to save
  7 MB. The maintainer overruled it in one line: there are no users but them. **That is the
  [no-users-until-10-stars gate](CLAUDE.local.md) and I should have applied it before recommending a
  deferral -- the whole point of that gate is that blast-radius arguments are void at 0 stars.** Checked
  after the fact: 0 stars, 0 forks.
  Shipped as two migrations plus the emitter fixes that stop both causes recurring: `dedupe` (1.6 -> 1.7)
  collapses cells by CONTENT and re-grids, `undirect` (1.7 -> 1.8) drops the suffix. Catalog companion content
  80.1 MB -> 69.3 MB (13.5% across all 53, 20.0% across the 31 converted); 559 cells dropped, 232 renames,
  0 refused. `SpriteSheetBuilder` now keys cells on content rather than image name, so a fresh import
  matches the migrated corpus. No app release: the host does not reference the converter.
  **Three things worth keeping from doing it:**
  * **Re-gridding can compress WORSE.** `gengar` came out 1 KB bigger, and five companions whose only duplicates
    are blank cells save nothing because the grid does not shrink. Each companion keeps its original sheet unless
    the new one actually wins.
  * **`Graphics.DrawImage` resamples even a 1:1 blit** -- the default `InterpolationMode` is bilinear -- so
    the first pack altered edge pixels and the equivalence check rejected all 31 companions. That was the guard
    working before it had anything real to guard. Raw row copy instead.
  * **A migration that can legitimately no-op must still stamp the version**, or it strands the companion for
    every later migration. `3g8t9v4e` has no duplicate cells and 8 names wanting renaming, and `dedupe`
    left it at 1.6 so `undirect` skipped it. The version marks "has been through the pass", not "was
    changed by it". The five older migrations happen to always change something, so this never bit before.

### Shimeji conditions: what is left, what it buys, what it costs (measured 2026-08-28)

Every action the converter loses or simplifies across all 31 converted skins, counted from the classifier's
own residue reports rather than estimated:

| cause | actions | companions | verdict |
|---|---|---|---|
| `activeIE.*` (window geometry) | **335** | 13 | ✅ shipped, but see the correction below — the count was misleading |
| `cursor.*` (pointer position) | **58** | 13 | ✅ gaze shipped (8 of these); the rest are chases |
| moves the user's windows | 48 | 12 | refused on purpose, not a gap |
| multi-companion (breed / pairing) | 40 | 13 | different, much harder problem |
| `mascot.anchor.*` (self position) | 13 → **1** | 13 | ✅ DONE, was a reporting bug |
| Transform (skin swap) | 1 | 1 | ignore |
| unrecognised embedded class | 1 | 1 | ignore |

- ✅ **DONE — `mascot.anchor.*` was mostly a REPORTING bug, not lost capability.** A target-relative gate
  (`#{TargetY < mascot.anchor.y}` on ClimbWall) is a loop-continuation test: "am I still short of where I am
  heading?" The emitter already replaces Shimeji's conditional selection with its own border-driven graph and
  a time-budgeted repeat, and that ANSWERS the same question — the companion climbs until it hits the top border,
  which is exactly what the condition said. Calling it "needs selfX/selfY" told the reader a host change was
  required to recover something that already converts. Now classified Group1 with that reasoning; 12 of 13
  reports resolved, animation counts unchanged.

- ✅ **`activeIE.*` — window-relative behaviour. 335 actions across 13 companions. Best payoff per unit of work on
  this list, and CHEAPER than a first read suggests. Split into three phases; only the last is big.**

  > **Shipped 2026-08-28, and the headline count was misleading.** Surveying all 12 desktop skins: 392
  > actions mention `activeIE` and **not one carries a sprite of its own**. They are `Sequence` and `Select`
  > wrappers choreographing actions that already convert. "335 actions lost" therefore never meant 335
  > animations a companion could not play; it meant 335 pieces of choreography over animations it already had.
  > What the three phases actually delivered is a different and better thing — every converted companion can now
  > use all four edges of a window with wall and ceiling art it already shipped. Read the phase notes
  > below rather than this count.

  The correction that matters: the engine is ALREADY window-aware and already knows which edge was hit. The
  hand-authored sheep use `only="window"` heavily, and `FormCompanion` has three separate detections --
  `:849` left edge of a window, `:893` right edge, `:939` landing on a window top -- every one of which
  passes the same `TOnly.WINDOW`. The information is computed and then discarded. This is missing
  DISCRIMINATION, not missing detection, which is why the earlier "LARGE, multi-session" estimate for the
  whole thing was wrong.

  Split by what each action actually needs (counted from the 65 distinct names):

  **Phase 1 -- discriminate the window edge. 184 actions. MODEST.** `SitOnTheLeftEdgeOfIE`,
  `JumpFromRightEdgeOfIE`, `WalkLeftAlongIEAndSit` and friends. Add window-left / window-right / window-top
  to the `only=` enum, pass the specific value at the three sites that already compute it (one line each),
  and keep plain `window` as a WILDCARD matching any of them so all 22 hand-authored companions keep working
  untouched. Then validator + XSD + converter mapping. Additive format bump, host release.

  **Phase 2 -- window side cling. 36 actions. SMALL, once Phase 1 exists.** `ClimbIEWall`, `HoldOntoIEWall`.
  A window's left/right edge is just a wall whose x comes from `GetWindowRect` instead of the screen, and
  clinging is the ABSENCE of `<gravity>` -- the exact mechanism the wall/ceiling region already uses. Mostly
  retargeting the existing wall region.

  **Phase 3 -- window underside. 60 actions. LARGE, and this is the part that deserves its own piece.**
  `WalkAlongIECeiling`, `ClimbIEBottom`, `CrawlAlongIECeiling`, `DashIeCeilingLeftEdgeFromJump`. Genuinely
  new detection: nothing today tests "hit the bottom of a window from below", because `FallDetect` only
  looks for window TOPS to land on.
  **The risk I originally attached to the whole feature belongs HERE.** Window tracking is the most fragile
  part of the physics (it already carries a "rejects collapsed rectangles" invariant), and an underside
  contact multiplies the states that must behave when a window moves, minimises or closes mid-animation.
  Discriminating a flag the engine already computes carries none of that.

#### Sequencing, cheapest first

> **ALL SHIPPED 2026-08-28** (Phase 0 → E). Left below as written, with a ✅ and a note on each, because
> what the estimates got wrong is the useful part. Summary of the corrections:
>
> - **Phase C's 184-action justification was wrong.** All 392 `activeIE` actions across the 12 desktop
>   skins carry ZERO sprites — they are `Sequence`/`Select` wrappers choreographing Walk, Stand, Sit,
>   Jumping and GrabCeiling ("walk to a point 100-400px right of the window's left edge, then sit"). And no
>   converted companion carried a window edge at all; all 955 belonged to the hand-authored sheep. C on its own
>   shipped nothing a user could see.
> - **What D and E actually bought is not source fidelity.** It is that every converted companion already ships
>   wall and ceiling art that could only ever be used at the two SCREEN edges, and a window has four more.
>   All 31 gained them, not the 13 `activeIE` predicted.
> - **Phase B was 8 actions across 8 companions, not ~18 across 13.** The larger figure counted moving and
>   composite cursor actions that are chases, not gazes.
> - **Phase 0 was the prerequisite it was billed as**, and Phase E did become worth building once it landed.

- ✅ **PHASE 0 — JUMPS. 81 occurrences across 27 companions. CONVERTER-ONLY. Do this before anything else.**
  Found by asking what a window underside would actually buy (2026-08-28), and it turned out to be the
  broadest and cheapest item on this entire list — more companions than `activeIE`'s 13.
  `jumping` (15 companions), `jump_up_left` / `jump_up_right` (10 each), `Resisting` (12), `PullUpShimeji2` (6),
  `Launching` (3), plus the Japanese ジャンプ / 抵抗する.

  **The format and the engine already do this.** yellow_sheep and blue_sheep each carry 22 animations with
  an upward start velocity. Nothing needs adding. The ONLY reason converted companions never jump is a converter
  guard in `IsFloorAction`, which rejects any pose with `VelY < 0` because an unbounded upward velocity on
  the floor would launch the companion off the top of the screen.

  **Work:** admit upward-velocity floor actions when the arc can be BOUNDED — emit start upward, end
  downward, and keep `<gravity>` so the companion arcs back — instead of rejecting the family outright. Clamp the
  launch velocity so a pathological `Launching` cannot fling the companion off-screen; the engine's existing
  `bLeavingScreen` path is the backstop. No format change, no engine change, no host release, ships through
  the catalog like any other companion content.

  **Why it is first:** widest reach, lowest cost, and it is a hard PREREQUISITE for Phase E (below).

  > **✅ FINISHED 2026-08-31 — "bounded" was not enough, and a live report is what showed it.** The maintainer
  > watched Hornet jump and said she seemed to land in a sit pose. She did not (the graph went
  > `turn` → 9.4s of `Stand` → a ~30% chance of a sit), but the report was right about the symptom: there was
  > **no landing at all**, and measuring the shipped corpus turned up three more defects the acceptance bar
  > could never have caught, because every one of them produces a valid, reachable, round-tripping companion.
  >
  > - **The height was an accident of the STEP COUNT, not of the launch velocity.** With a linear start→end
  >   ramp the rise is roughly `a²(N-1)/(2(a+b))`, so clamping `a` fixes nothing while `N` comes from the
  >   source. Replaying the engine's interpolation over the 32 shipped jumps: **16 peaked under 20px** (a
  >   twitch) and **16 at 72px** (a fling, `MaxLocoRepeats` padding 3 frames to 21 steps). Nothing between.
  >   Fixed by making the PEAK the invariant and solving the launch for it (`SolveJumpLaunchY`), with a
  >   jump-specific repeat budget instead of the walk budget.
  > - **The interval was inherited, and one source ramped it 80ms → 4000ms.** Hornet's Grapple4 hung
  >   motionless 12px off the ground for two of its three steps. An arc must not change pace: flat now.
  > - **The `65%` locomotion self-edge on a jump was dead code.** The taskbar border fires long before the
  >   sequence ends (12 steps of 28 on Grapple1), so a converted companion could never chain hops. Re-jumping had to
  >   move to the LANDING edge, where the sheep has it at weight 30.
  > - **The horizontal velocity was passed through too, and fixing the arc EXPOSED it.** Grapple4 dashes at
  >   -100px/tick; once the arc lasted a proper 15 steps it crossed 1500px, so 16 of 18 jumps ended at a side
  >   border and the new landing set almost never fired. Capped at yellow_sheep's own 150px span.
  >
  > Shape is now the sheep's: solved arc → `fall` if the arc outlives the drop → a landing weighted toward
  > re-jumping and running. Hornet went from 30-of-31 landings into `turn` to 18-of-26 into motion, and now
  > chains hops. Two actions that only LOOKED like jumps (Grapple1 and `fly`, both at -5) are flattened rather
  > than dropped, and reported. Shipped to the 25 affected companions by the **`rejump`** migration, not a
  > re-conversion: no new sprite frame is involved, so 25 sheets would have been regenerated to identical
  > pixels and Hornet's hand-edited frame swap would have been wiped. Header format 1.2 → 1.3.
  >
  > **The lesson worth keeping:** the acceptance bar (valid + round-trips + reachable) is a bar on the GRAPH,
  > and every one of these was a bar on the NUMBERS. Reachability proved the jump could play; nothing proved it
  > looked like a jump. Where a converter synthesises a physical quantity, assert the quantity.

  **Still open, found while measuring and deliberately not fixed here:**
  - **An animation with one frame and `repeat="0"` is invisible.** `TotalSteps` is 1, so `AnimationStep >=
    lastStep` on the very first tick and it hands straight on. Hornet's `Grapple3` shows for one tick. Whether
    this affects anything other than one-frame `Animate` actions is unmeasured.
  - **Hornet jumps about once every 5 minutes.** Grapple4's hub weight is 20 of 664 and the hub itself dwells
    9.4s per visit. That is the hub weighting, not the jump, so it is a separate question from this entry.

- ⚠️ **THE SCREEN CEILING IS UNREACHABLE FOR CONVERTED PETS, and it is the same defect class as the jump was.
  Measured 2026-08-31 after "I have never seen Hornet reach the ceiling".** Correct: she effectively cannot.
  The wall region was shipped as working, the ceiling region is reachable on paper, and the acceptance bar
  passes — because again the bar is on the graph and the problem is in the numbers.

  | | px per pass | sec per pass | px/sec | passes to climb 940px |
  |---|---|---|---|---|
  | Hornet `ClimbWall` | 32 | 12.8 | **2.5** | 30 |
  | Uzi Doorman `ClimbWall` | 45 | 4.0 | 11.4 | 21 |
  | yellow_sheep `wall_slide` | 66 | 1.0 | 66.7 | 15 |

  Hornet climbs **26x slower than the slowest hand-authored wall move**, so the top of a 1440p screen is
  **6.4 minutes of unbroken climbing** away. But `ClimbWall`'s sequence end offers climb 60 / grab 20 /
  **fall 25**, so every pass boundary is a 23.8% chance of letting go. Monte Carlo over 3,000,000 wall entries
  on the emitted weights: **9 reached the top, 1 in 333,000.** At one wall entry every ~13 minutes that is
  **about 8 years of uptime per ceiling visit**. 34% of wall entries climb exactly one pass; the mean is 2.9
  passes, 93px, a tenth of the way up. A 47-hour behaviour simulation gave 215 wall entries, **zero** ceiling
  visits, median climb 62px, and one lucky run that stalled at 928px — 12px short.

  The ceiling POSES are not wasted: they are also reached by jumping into a window's underside
  (`only="window-bottom"` → `GrabCeiling`, weight 100), which needs no climb. That route got better for Hornet
  with the jump fix (Grapple4 rises 46px now, not 15px) and slightly worse for the companions whose jumps used to
  overshoot to 72px.

  **Fix, when it is picked up:** the same move the jump just had. Budget the climb by DISTANCE, not time.
  `TargetWallMs = 5000` is a time budget, and Hornet's single 12.8s pass already overshoots it, so
  `RepeatCountForBudget` returns 0 and the pass covers whatever 32 frames at the source's -2px/tick happens to
  cover. Assert the observable quantity instead: one pass should climb a stated fraction of the screen, which
  means overriding the source's climb velocity the way the jump now overrides its launch. Lowering the
  let-go weight is the smaller, weaker half of the fix and should not be done alone — at 2.5px/s the climb is
  visibly wrong even when it succeeds.

---

Ordered by cost, not by action count. **A and B are cursor work and are cheaper than any of the window
phases**, so they follow Phase 0 even though the window work has 6x the actions.

Ordered by cost, not by action count. **A and B are cursor work and are cheaper than any of the window
phases**, so they go first even though the window work has 6x the actions. A needs no format change at all;
B introduces one new sequence action, which is a far smaller precedent than new `only=` values. Both deliver
something a user notices immediately (the companion reacting to their mouse) for a fraction of C's work.

A and B can ship in one host release; A alone would not need one, but it may as well ride along.

- ✅ **PHASE A — drag reactions. ~26 actions, 12 companions. CONVERTER-ONLY, no format change, lowest risk on this
  whole list.** `Pinched`, `Thrown`, and the Japanese equivalents (投げられる / つままれる). These fire while
  the cursor is holding the companion, and the host ALREADY knows that: `FormCompanion.IsDragging`, the `drag` magic
  animation, and `EndDrag`. The condition is answerable today, so the work is mapping the family onto the
  existing drag path instead of emitting them as unconditional floor spokes. Biggest cursor slice, cheapest
  fix, and it needs no engine change to land.

- ✅ **PHASE B — gaze. ~18 actions, 13 companions. ONE new sequence action. Self-contained.**
  *(Shipped as 8 animations across 8 companions. The ~18/13 figure counted moving and composite cursor actions,
  which are chases. Two things the estimate missed: the gaze also had to be added to the sprite-sheet
  compositor, or its frames were never drawn and the spoke was dropped for having none; and the variant to
  emit is the UNCONDITIONAL fallback, not `Animations[0]`, which across all seven skins is "pointer near
  the top of the screen" and would have pinned every companion permanently craning upward.)*
  `SitAndFaceMouse`, `SitAndLookAtMouse` (+ 座ってマウスのほうを見る / 座ってマウスを見上げる). Needs only a
  BINARY test: is the cursor left or right of the companion. The engine already has `IsMovingLeft` and the mirror,
  and the format already has a sequence action that flips — `flip`, which the synthetic `turn` uses. So this
  is one more action of exactly that shape, `faceCursor`, setting facing from the cursor's side on entry. No
  steering, no new movement mode, no border work, and no new `only=` value.
  Today these animations DO convert and their frames DO play; what is lost is the aiming, so the companion sits and
  looks somewhere arbitrary. That is the whole gap, and it closes with one sequence action.

- ✅ **PHASE C — discriminate the window edge. 184 actions. MODEST.** (Detail above: the three detections
  already exist and already know which edge was hit.)
  *(Modest was right; the 184 was not. See the correction at the top of this section. Shipped as
  `window-left` / `window-right` / `window-top` `only=` values with `window` kept as the wildcard, so the
  955 window edges in the hand-authored companions are untouched. Ships nothing observable on its own — it is
  the vocabulary D and E needed.)*

- ✅ **PHASE D — window side cling. 36 actions. SMALL once C exists.**
  *(Not small. The host's `hwndWindow` means "standing on the TOP" everywhere it is read — `CheckTopWindow`
  compares candidates against `rctO.Top`, `FollowWindow` re-pins to the top — so the grip needed its own
  state, its own release conditions, and `hwndWindow` had to become a property that clears it, because nine
  sites drop that handle for their own reasons. The opt-in is an EXACT match on the discriminator, never a
  bit test: a bit test would have recruited all 955 legacy `only="window"` edges into the behaviour.)*

- ✅ **PHASE E — window underside. 60 actions. LARGE, and BLOCKED ON PHASE 0. Re-evaluate before building.**
  *(Re-scored after Phase 0 and built. `RiseDetect` is a separate walk from `FallDetect`, not a parameter on
  it: opposite edge, opposite crossing direction, and a different z-order question, since a window in front
  of the companion does not stop it being underneath. The trap the estimate did not see: a maximised window's
  bottom edge sits on the work area, directly over a companion on the taskbar, so without a clearance test the companion
  grabs the underside on the first tick of every jump it makes.)*

  <details><summary>Original estimate, kept for the record</summary>
  `WalkAlongIECeiling`, `ClimbIEBottom`, `CrawlAlongIECeiling`, `DashIeCeilingLeftEdgeFromJump`.

  **Every entry point is a jump.** `DashIeCeilingLeftEdgeFromJump` / `...RightEdgeFromJump` (12 companions each,
  plus 2 more) are how a Shimeji REACHES a window's underside: it jumps up and catches it. The
  `...AlongIECeiling` actions are only the traversal once already hanging there. So without Phase 0 this
  phase builds hanging logic the companion has no way to arrive at — 60 actions that convert and can never play.

  Unlike the screen ceiling, which is reached by climbing a wall the companion is already touching, a window
  underside floats in mid-screen with nothing beneath it to climb. Phase D's side-cling does not help: that
  climbs UP a window's side to its top, not around to its underside.

  **So: do Phase 0, then re-score this.** Once the companion can jump, these 60 become reachable and their value
  goes up; until then the honest answer is that this phase buys nothing. This is also where the real risk
  sits — window tracking already carries a "rejects collapsed rectangles" invariant, and an underside
  contact multiplies the states that must behave when a window moves, minimises or closes mid-animation.
  </details>

  *(That last paragraph was right. The grip re-reads the window rect every tick and releases on a
  degenerate one, which is exactly what a minimised window reports.)*

- ⬜ **NEXT, and the only thing left in this section — `ChaseMouse` / `ChaseMouse2` / マウスの周りに集まる. ~14 actions, 12 companions.
  This is the "companions follow your mouse" behaviour people remember, and it is the one thing here that a
  conditional transition CANNOT express.** The format gives each animation a fixed start/end velocity and
  interpolates between them; chasing a cursor means recomputing velocity every tick toward a target that
  keeps moving. That is a new MOVEMENT MODE in the engine (a `seekCursor` sequence action the engine
  implements by overriding per-tick velocity), and it has to behave against gravity, the border/turn logic,
  an active drag, and multi-monitor coordinates. Closer to Phase E in risk than to A or B despite the small
  count — the cost is in the interactions, not the lines. Deliberately deferred: do A and B first and see
  whether pointer-aware gaze already scratches the itch.

  **A and B have now shipped, so that question is live.** Answer it by watching a companion before building
  anything: a gaze aims on entry and re-enters every few seconds, so the companion glances at you rather than
  tracking you. If that reads as enough, this stays deferred permanently.

  **When B or the chase is built:** `SafeExpression` (which already resolves screenW / imageX / random) is
  the natural home for cursorX/cursorY/selfX/selfY, and a `<next>` carrying a condition is the natural gate.
  That machinery exists. The per-tick movement mode does not.

- ⬜ **`totalCount` — DO NOT BUILD.** Zero occurrences across all 31 shipping skins. It survives in the
  classifier only because the reference conf mentions it. The 40 multi-companion actions we do lose are Group3
  (Breed / pairing needs independent sibling companions, which `<child>` cannot be), a different and much harder
  problem. Action: reword the rule's "added in Stage 5" promise so it stops implying work is planned.

- **Not a gap:** "moves the user's windows" (48 actions) is refused deliberately — desktopPet "cannot and
  should not move the user's windows". No work.

- ⬜ **CONVENTION: every tray entry carries its own unique icon.** The tray is shared by the host and six
  modules, so an icon-less row reads as a rendering bug beside its neighbours and two rows with the same
  glyph look like duplicates. 32x32 ARGB PNG, shipped as an `EmbeddedResource`, read with
  `EmbeddedResources.LoadBytes`. Recorded in the module template; `BlinkingLedModule` asserts it in its
  self-test (`EveryTrayEntryHasAUniqueIcon`). **Not yet enforced for Reminder / Remembrance / AiBrain /
  PetStudio** -- they now all have icons, but only BlinkingLed has the assertion. Lift that helper into
  ModuleKit so every module's self-test can call it.
- ⬜ **A dark 1px line on the left edge of Jesus Our Lord's fall frame.** NOT a conversion artifact: the
  baked tile (88) and its left neighbour (87) were both rendered out of the shipped sheet and are clean,
  and the sheet is 2560x2560 with exact 256px tiles, so there is no rounding slop in the compositor.
  That leaves runtime tile sampling in the host (bilinear filtering picking up a column from the
  neighbouring tile when the companion is scaled). Fix is host-side, either sampling with a half-pixel inset or
  clamping, so it needs a release. Reported 2026-08-28.
- ⬜ **Blank frames are legitimate, so "no blank tiles" cannot be a corpus-wide gate.** A sweep of all 50
  companions found intentional transparent frames in hand-authored ones: `ssj-goku`'s `Instant_Transmission`,
  `alipheese`'s `TeleportStart`/`TeleportEnd`, the seven sheep's `bathd`, `negima`'s `fall`, `pingus`'s
  `fall2c`. They are how a companion goes invisible. The blank-tile assertion therefore lives on the SYNTHETIC
  fixture only. If a corpus-wide check is ever wanted it needs an allowlist keyed by animation name.

- ✅ **DONE (blinkingled 1.0.2, 2026-08-28) — SIXTH catalog module, Blinking LED.** A port of the
  standalone BlinkingLED tray app: blinks the keyboard's Scroll Lock light on a two-phase timer via Win32
  `SendInput`, six rate presets at their original durations, tray toggle plus a rate submenu. Needed NO host
  release and NO new permission: the module P/Invokes `SendInput` itself so nothing goes through the host,
  and it declares only Speech + Storage. Worth knowing what the port actually deleted: the tray plumbing,
  options window, config file, single-instance guard and start-with-Windows were most of the original's
  ~1000 lines, and every one of them is supplied by being a module.
- ⬜ **A module cannot push a live value into an open options pane or tray menu.** Found while porting the
  standalone app's "Next blink" countdown, which refreshed every 250ms because that app owned its own menu.
  A module ships DATA and the host renders it, so the best available is a snapshot: `TrayItem.DynamicText`
  is re-evaluated when the menu opens, and `SettingKind.Info` is read when the pane loads or a
  `PaneAction` with `ReloadPaneAfter` runs. Good enough for state that changes slowly, useless for a
  countdown. The readouts were dropped rather than shipped stale. If a live readout is ever wanted this
  needs an ABI addition (a push channel or a pane-refresh tick) and therefore a host release, which was not
  worth it for one diagnostic.

- ✅ **DONE (aibrain 1.2.3) — "Idle commentary" is gone, and the duplication with it.** The label lied:
  there is no `GetLastInputInfo` anywhere in the repo, so the loop gated on SCREEN CHANGE, not idleness.
  Rather than rename it, the whole timer and its three settings (Idle commentary / min / max) were removed
  and unprompted commentary now rides the host's global "Randomly drop a fortune / insight" schedule via
  the drop responder. One control, one schedule. The Ask hotkey stays, being the only trigger the module
  genuinely owns. **The screen-change gate did NOT survive**: the drop responder must answer synchronously
  (its bool is what lets Fortunes take the tick), and the comparison is async, so keeping it meant a
  background sampler, i.e. the timer we were deleting. On a static screen the companion now comments anyway;
  `AiBrain.ScreenChanged` is kept, unused and labelled, as the primitive for a future "only when something
  changed" option.
- ⬜ **`AiSettings` carries orphan `RandomDropEnabled` / `RandomDropMinutes` / `RandomDropJitterMinutes`
  fields** (`modules/AiBrain/engine/AiSettings.cs:171-180`, clamped at `:529-530`) that nothing in
  `AiBrainModule.cs` reads. Left over from before the drop moved to the host, where the live values now come
  from `AppSettingsStore`. Harmless but actively confusing: they are part of why the two trigger groups look
  duplicated in the settings file. Delete them.
- ✅ **DONE (aibrain 1.2.3) — the two trigger groups no longer overlap**, because there is only one left.
  `_lastInteractionUtc` survives as the guard that actually earns its keep: `OnDrop` declines when the AI
  spoke in the last 30s, so a hotkey ask landing just before a drop yields a fortune instead of two model
  answers back to back. Declining beats going silent, because the responder chain falls through to Fortunes.
- ✅ **DONE (aibrain 1.2.3) — OCR scratch files are swept.** Only the Tesseract path writes a screenshot to
  disk (the vision image is in-memory base64; Windows OCR is memory-only). It was deleted in a `finally`,
  which covers the normal and cancelled paths but not the TIMEOUT path: the process tree is killed and the
  delete runs immediately after, so a child still dying holds the handle, `File.Delete` throws and the
  `catch` swallows it. Full screenshots, so repeated timeouts leaked megabytes. Now swept on the next call
  at an hour old, same reasoning as `SelfTestScratch`.

Fortune Sheep is feature-complete — Phases **A–C** below all shipped (bundled corpus + poke-escalation,
offline bge-small **smart fortunes**, and the OpenAI-compatible multi-provider **AI brain** behind a
default-off master switch + tray Load/Unload + DPAPI keys). A pre-release **cleanup pass** landed
2026-07-29 (dead-code trim, correctness fixes incl. the sound self-mute, .NET 4.8 retarget, CI/release
workflows — see [`handoff.md`](handoff.md)). **v1.0.1 shipped 2026-08-04** via a lean hobby-grade CI
(the never-green enterprise gate/SBOM/signing/rights pipeline was stripped, ~50 scripts deleted);
releasing is now `git tag vX.Y.Z` (see [`docs/RELEASE-CHECKLIST.md`](docs/RELEASE-CHECKLIST.md)).

- ✅ **DONE (1.4.6) — `modules/PetStudio` is PUBLISHED** (Companion Studio 1.1.0). Grew from a validator into a
  three-column authoring window: an editable XML pane (debounced re-analyze + atomic save) feeding
  preview/install, a colour-coded reachability map with clickable legend filters, and a detail panel rendering
  the selected animation's real sprite frames with playback plus its outgoing transitions. Its Open dialog
  defaults to the companion library (the additive `ICompanionManager.CompanionsDirectory` that moved the host version) and
  remembers the last folder browsed to. Blank transparent frames and orphaned-but-complete animations now
  explain themselves instead of looking broken. It still **source-links** the host's parser, validator and
  `AnimationReachability`; `--petstudio-selftest` pins that the module's verdict and `CompanionXmlValidator`'s agree
  on every fixture, and now also that the map's dead set equals `AnimationReachability.FindUnreachable`.

### Known ABI gaps (add when the module that needs them is written — see handoff.md's host contract)

- ✅ **DONE (v1.6.0) — a module can play audio, and can speak instead of showing a bubble.**
  `IHost.PlaySound(moduleId, audio, volume)` routes a WAV or MP3 container through the existing shared mixer
  and device, gated on the new `ModulePermissions.Audio`; `StopSound` cuts that module's own audio for
  barge-in (by ramping out over ~10 ms and returning short, so NAudio drops the input — muting would leave a
  silent input occupying the mixer for the rest of the utterance). `RegisterSpeechResponder` +
  `ModulePermissions.Voice` offer every line before its bubble is drawn, with **claiming and suppressing as
  two separate knobs** so bubble-only, bubble+voice and voice-instead-of-bubble are all expressible.
  A **container, not raw PCM**: a `float[]` would alias the mixer thread's read, and would freeze
  interleaving order and channel semantics into the ABI forever. `ModuleKit.WavAudio.FromPcm` covers engines
  that emit bare samples. Module audio never enters the decode cache (reference-keyed and never evicted, so
  caching speech would retain every line the companion ever spoke) — pinned by a source invariant that was itself
  negative-tested after the first version silently passed with the misuse injected.
  **Ducking is still not implemented**, but the per-owner input tracking this added is the groundwork; it
  changes how the app sounds, so it wants its own decision and a setting.
  *(Original gap below, for the reasoning.)*
- ~~📌 A module cannot play audio.~~ `IHost` exposes `Volume` read-only and no playback verb at all, while the
  base owns a full mixer (`src/dotNet/AudioOutput.cs`, DirectSound, per-sound volume + overlap, device picker).
  The engine's `<sound>` path reaches `AudioOutput` directly, which is what left `SoundData`/`SoundLoop`
  unreachable when the Sound module was retired. **A TTS/voice module — already a planned future module — is
  therefore impossible today.** Shape when it is needed: something like
  `bool PlaySound(string moduleId, byte[] audio, double volume)` routed to the existing mixer, gated on a new
  `ModulePermissions.Audio` so it shows up in the pre-install consent list. Small and additive; add it *with*
  that module rather than speculatively, and remember rule 3 (product bump in the same commit).
- 📌 **A module cannot draw on or near the companion.** No ABI for overlay/decoration. Nothing planned needs it yet;
  noted so it is not mistaken for an oversight if something does.

### Module SDK follow-ups

- ✅ **DONE (petstudio 1.1.1) — Pet Studio's window theme comes from `IHost.IsDarkTheme`, not the OS registry.**
  It used to read `AppsUseLightTheme` directly, which is right only while the host sits on its default "system"
  setting and wrong the moment a user pins the opposite — the host's actual preference was invisible to modules
  until `IHost.IsDarkTheme` landed in 1.4.7. `PetStudioTheme.Current()` now takes the `IHost` and asks it;
  a null host (or a host that throws) falls back to light, the same direction the host's own resolver fails in.
  The `DESKTOP_AI_COMPANION_FORCE_THEME` env override went with it, because the settable `RecordingHost.IsDarkTheme` is a
  better version of what it was for: `--petstudio-selftest` now drives the theme **both ways** plus the no-host
  case, where before it asserted nothing about theming at all and its fake host hardcoded `IsDarkTheme => false`.
  **The one non-obvious edit:** `PetStudioWindow` built the theme in a *field initializer*, which runs before the
  constructor body assigns `_host` — so it had to move into the constructor. `MinHostVersion` 1.4.6 → **1.4.7**.

- ✅ **DONE (2026-08-18, fortunes + aibrain 1.1.2) — both modules now build on `DesktopAICompanion.ModuleKit`.**
  Deleted 752 lines: the two byte-identical `CrossSessionLock`/`AtomicFile` copies, AiBrain's own
  `UnicodeTextProgress`, and three hand-rolled embedded-resource loaders. Net −820/+37.
  **Deliberately kept, so nobody "finishes" it later:** `FortuneProvider` still reads its corpus itself,
  because it decodes with *strict* UTF-8 (`throwOnInvalidBytes`) and distinguishes "resource missing" from
  "failed to parse", while `EmbeddedResources.LoadText` is deliberately lenient and returns `""` for both.
  The thin wrappers also stayed where the contract differs (`ReadEmbeddedText` returns **null**, not `""`,
  because its callers branch on null). Held back before only because a republish reaches existing users — and
  the repo has 0 stars, so that audience was hypothetical.
- ✅ **DONE — a committed leak soak for a module-owned window** (`tests\DesktopAICompanion.WindowSoak` +
  `tests\module-window-soak.ps1`). `runtime-resource-soak.ps1` samples the shipped app from outside and its
  churn loop (`Program.RuntimeResourceChurn`) only drives companions/speech and the tray, so a module's window was
  covered by nothing; the soak that found the sprite re-decode bug existed only as prose in `handoff.md`.
  A separate `UseWPF` console exe (CoreTests is `UseWindowsForms`, so it could not live there) **loads the
  module DLL at runtime and reflects** — `PetStudioWindow` is `internal sealed`, so a compile-time reference
  would buy nothing, and leaving it out keeps the project free of build-order coupling. It reuses ModuleKit's
  `RecordingHost` rather than hand-rolling a fake that rots on every ABI addition, and a missing reflected
  member is a hard FAIL, never a skip. **Not in the blocking gate** (`run-gate.ps1:12-15` excludes leak soaks
  as too flaky for CI); it is a pre-tag step in `docs/RELEASE-CHECKLIST.md`.
  Current numbers for Companion Studio, 2 × 20 cycles: segment 2 handles +0, GDI +0, USER +0, private **−7.8 MB**.
  - **⚠ The trap that cost the most time here, worth knowing before writing any WeakReference leak test:**
    exactly one window per segment looked rooted, always the last one (cycle 7 of 8, cycle 19 of 20). It was
    not a leak and not `Application.MainWindow` — it was the strong reference *escaping the cycle method* and
    sitting in the caller's stack slot until overwritten. Fixed by having the cycle return a `WeakReference`
    rather than the window, and marking it `NoInlining`, so the only strong reference lives in a frame that is
    guaranteed to be torn down. A displacer window was tried first and did nothing; it was removed rather than
    left in place looking meaningful.
  - **Negative-tested, not assumed:** deliberately rooting each window in a static list makes it fail on two
    independent signals — all cycles rooted instead of none, and segment-2 private bytes **+31.4 MB** instead
    of −9 MB. Reporting *which* cycles are rooted is what separates a real leak (all of them) from the
    framework artifact above (only the last).
- 📌 **Third-party module ecosystem (Phase B).** Signing + per-publisher consent, a signed third-party index
  (or a curated links page first), and NuGet-publishing Contracts/ModuleKit/the template so a module can live
  outside this repo. Designed but deliberately unbuilt — see `docs/module-ecosystem-roadmap.md`, which also
  records the open questions and argues the cheap steps first.

### Bugs & maintenance

- ✅ **DONE (2026-08-27) — `Test-ModulePublishFreshness` now sees shared-source and bundled changes.** It
  used to compare commits against `modules/<Id>/` alone, so a payload could go stale invisibly. New
  `Get-ModuleWatchSet` derives each module's watch set from its csproj (`Compile`/`EmbeddedResource`/`None`
  includes that resolve outside the module folder, plus every `ProjectReference` not marked
  `Private="false"`, followed recursively), and the failure names WHICH watched path carries the newer
  commit. `DesktopAICompanion.Contracts` drops out on its own because it IS `Private="false"` — the host owns that
  copy, so a Contracts edit does not change the payload.
  - **The old entry undercounted the exposure.** It said PetStudio compiles "four files out of `src/`"; it
    is **7 from `src/` and 13 from `tools/ShimejiConvert.Engine/`** plus embedded resources and a native
    `dwebp.exe`. The check now watches 27 paths for petstudio, 8 for fortunes, 2 for the rest.
  - **The real staleness was not source-linking at all — it was ModuleKit.** It ships INSIDE every module
    (its ProjectReference is deliberately not `Private="false"`), so one ModuleKit edit stales every
    payload. The widened check's first run found **fortunes, aibrain and petstudio all shipping a ModuleKit
    3-4 commits behind**, which nothing had reported. All five modules were republished to clear it.
  - Deliberately out of scope: `ProductVersion.props`. ModuleKit stamps its assembly Version from it, so a
    host bump does change the bundled DLL's bytes, but demanding five republishes per release for a version
    field and no functional change would make the gate hostile enough to be routed around.
  - Mutation-tested with a negative control (see the commit), because a green run proves nothing here.

- ✅ **DONE (2026-09-01) — the self-test flags no longer leak their `%TEMP%` scratch.** `SelfTestScratch`
  already swept on the NEXT run (the PendingModuleRemovals trick) and reported failures instead of swallowing
  them, so most of the pile was already gone. What remained was a narrower bug: the sweep matched
  `dp-*-selftest-*`, which coupled cleanup to a NAMING CONVENTION — and the convention moved. 61 orphaned
  roots survived a month, including `dp-petmgr-<guid>` directories whose creating code no longer exists
  anywhere in the tree; they could never be collected because they did not carry the marker. The sweep now
  matches on the `dp-` prefix alone, because age is the only safe question to ask about a transient scratch
  directory. Cleaning 0.58 GB / 61 dirs happened in-flight during the mutation runs (every Create sweeps).
  A new assertion covers a root that does NOT follow the current naming — every existing scratch assertion
  used NameFor(), so all of them passed while this leaked. Mutation tested 3 ways. Original note:
  Measured on the dev box 2026-08-27: **3.2 GB across 387 orphaned `%TEMP%\dp-*` directories**, dominated by
  `dp-aibrain-selftest-*` (179), `dp-petstudio-selftest-*` (95) and `dp-modulefail-selftest-*` (72). Cleaned
  by hand (348 dirs, 2.87 GB freed), but it will simply come back: every gate run adds more.
  The mechanism is clearest in `ModuleConventionSelfTest`, which copies the module folder to
  `%TEMP%\dp-module-selftest-<guid>` and deletes it in a `finally` (`:104`) that cannot succeed, because the
  collectible `AssemblyLoadContext` still holds the DLL when it runs; the exception is swallowed. The other
  prefixes are the same shape in their own harnesses.
  Fix shape: delete on the NEXT run rather than this one (the trick `PendingModuleRemovals` already uses for
  the identical DLL-lock problem), and stop swallowing the failure. Cheap, and worth doing before a
  contributor's disk fills up. Dev/CI only, so no user impact; CI runners are ephemeral, which is exactly why
  nobody noticed.

- 📌 **`--module-selftest=<id>` picks the FIRST `bool SelfTest(out string)` in the assembly, which may not be
  the module's own.** `ModuleConventionSelfTest.RunModuleSelfTest` reflects over every type and breaks on the
  first match, including non-public ones. Reminder had six pure helpers each exposing exactly that signature,
  so any of them could have won over `ReminderModule.SelfTest` — non-deterministically, by metadata order.
  Worked around module-side by renaming those six to `SelfCheck` (2026-08-27), but the sharp edge is still
  in the host and will catch the next module author, including third parties. Fix shape: prefer the type
  implementing `IModule`, then fall back to the scan. Host change, so it wants a release to be worth much.

- ✅ **DONE (v1.5.0) — every companion on screen no longer speaks the same line at the same moment.** A reaction now
  belongs to ONE companion: the poked companion, the companion that landed, or the companion a drop was routed to (round-robin, because
  uniform random repeats the same companion often enough to read as still-broken). `Say(companion, …)` is the fix;
  per-companion *routing* (Tray → Companion Speech) is the feature built on top of it. Fortunes and AI Brain 1.2.0 carry
  the module half. Three things fell out of it that were invisible while everything broadcast:
  **`FormCompanion` knew which companion you clicked and threw it away** (the host recovered "a" companion, so poking companion #5 was
  reported as companion #1 to every module); **poke escalation was per-app** (poke Pearl three times then Rick, and
  Rick got the sass tier); and **`SayAll` spoke through authoring previews**, contradicting the documented
  previews-are-invisible invariant. The repeat guard moved into `FormCompanion.Say` because `IHost.Say` bypasses
  `SayAll` entirely — leaving it where it was would have silently killed the suppress-repeats preference.
  *(Original report below.)*
- ~~📌 OPEN (reported 2026-08-19) — every companion on screen speaks the SAME line at the SAME moment.~~ Reported as
  "when the same companion is chosen, it speaks at the same time, and the same saying", with the reporter's own
  hunch that it is probably *all* companions rather than only duplicates of one type. **That hunch is correct, and
  the cause is not subtle:** `StartUp.SayAll` (`src/dotNet/StartUp.cs:1152-1171`) takes one string and fans it
  out to every live companion in a single loop (`sheeps[i].Say(text)`), and essentially everything speaks through it
  — the base's poke sass (`:1357`), the tray's Test Speech (`ContextMenus.cs:224`), Fortunes
  (`FortunesModule.cs:166`) and the AI brain (`AiBrainModule.cs:701`). Nothing picks a companion, and nothing
  staggers. So four companions means four identical bubbles appearing simultaneously. Companion *type* is irrelevant.
  - Worth scoping deliberately rather than patching, because "what should happen instead" is a product
    question with at least three defensible answers: **one companion speaks** (chosen at random, or the poked one —
    already available via `IHost.Say(companion, text)`, which exists and bypasses `SayAll` entirely); **all speak but
    staggered** by a short jitter so it reads as chatter rather than a chorus; or **all speak but with
    different lines**, which is much bigger because the fortune/AI callers produce one string, not N.
  - Note `CompanionHost.RaisePokeReaction` already resolves *which* module answers a poke, and `PokeInfo.Companion`
    already carries the specific companion, so the plumbing to speak to just one companion is present and unused on this
    path — the fix is likely a caller change, not new ABI.
  - **Relates to #16 (per-companion personality/voice)** and to the Voice module: a voice engine must speak a
    broadcast line **once**, not once per companion, so whatever lands here should not assume one utterance equals
    one companion.
- ✅ **DONE — every tagged release was published by TWO racing workflows.** `release.yml` and
  `publish-release.yml` both triggered on `push: tags: v*`, both ran the whole build, and both did
  `gh release upload --clobber` against the same GitHub release. Whichever finished last won, so
  **`SHA256SUMS.txt` listed the author nupkgs or not depending on who lost the race** — every release was
  non-deterministic, and `publish-release.yml`'s own header comment asserted release.yml was
  "manual-dispatch only", which was factually wrong about its own sibling.
  Consolidated into `release.yml` (which already packed the nupkgs) and **deleted `publish-release.yml`**,
  folding in its two correctness properties: it checked out the **tag** rather than the default branch, and it
  **verified the tag against `ProductVersion.props`**. `release.yml` had neither — so a `workflow_dispatch`
  re-run built the default branch and published it under the tag's release, and a tag that disagreed with the
  product version was published without complaint. Added a `concurrency` group too, since two concurrent runs
  of the surviving workflow would reproduce the same clobbering.
- 📌 **`release.yml` still runs `microsoft/setup-msbuild`, which is vestigial.** `build.ps1:48-54` states it no
  longer probes MSBuild/VS, and the MSI is built by the `wix` dotnet tool, so nothing consumes it. Left in
  deliberately rather than removed in the same change: it costs seconds, and the release path is the wrong
  place to find out you were wrong about an implicit dependency. Drop it the next time the release workflow is
  touched for another reason.

- ✅ **DONE (2026-08-18, 1.4.8) — a module that fails to load is no longer invisible.** It used to count as
  installed (the pane enumerates folders), report no live version so no update was ever offered, and show
  "installed — restart to activate" forever, leaving Uninstall — which deletes the module's settings and API
  keys — as the only escape from a state the user did not cause. `ModuleHost.LoadFrom` already caught every
  failure and only logged it; all four early-return paths (no module DLL, no `IModule` type, `MinHostVersion`
  refusal, any exception) now record a `ModuleLoadFailure` surfaced through `StartUp.ModuleFailures`. The pane
  renders "failed to load — &lt;reason&gt;" with a **Reinstall** routed to the existing install flow, which
  replaces only the install folder and leaves module data alone. A `MinHostVersion` refusal is distinguished
  as "needs a newer app" with no Reinstall, because the module is fine and only updating the app helps.

- ✅ **DONE (2026-08-14, aibrain 1.1.1) — the AI brain read the screen through the ANSI codepage, so the companion
  quoted mojibake back at the user** (reported as a bubble sneering at `asÂ®`). `AiBrain.RunOcrAsync` set
  `RedirectStandardOutput` without `StandardOutputEncoding`; unset, .NET takes the encoding from
  `GetConsoleOutputCP()`, which is **0** in a GUI process with no console, and decodes codepage 0 as CP_ACP —
  the system ANSI codepage. Tesseract emits UTF-8, so `as®` (`61 73 C2 AE`) arrived as `asÂ®`, and with it
  `—`→`â€"`, `’`→`â€™`, `©`→`Â©`, `™`→`â„¢`, `é`→`Ã©`. Curly apostrophes are on nearly every page, so the brain
  had been fed corrupted context routinely, wasting tokens and occasionally derailing on the garbage; bytes
  with no CP1252 mapping landed on C1 control codepoints that `CleanOcr` then stripped, quietly losing
  characters too. Fixed by pinning lenient UTF-8 (a replacement char costs one glyph; strict would throw and
  blind the companion to the whole screen) on stdout AND stderr. **Windows' built-in OCR was never affected** (WinRT
  strings), so only Tesseract users ever saw it. Guarded three ways: a `®` in the `Test OCR` probe image with a
  red status on mis-decode, a `--aibrain-selftest` assertion on the psi factory (CI has no OCR engine, so the
  assertion is on the psi rather than a real run), and a repo-wide source guard in
  `tests\runtime-hardening-selftest.ps1` pairing every `RedirectStandardOutput` with an explicit encoding.
- ✅ **DONE (2026-08-14, 1.4.2) — installed modules could never be updated, only uninstalled.**
  `ModulesPaneControl.DiffNew()` diffed the catalog **by id**, so an installed module disappeared from the
  available list permanently: no version was compared, nothing checked at startup, and a module bugfix could
  reach only people who had not installed it yet. The lone workaround was Uninstall + reinstall, and uninstall
  deletes the module's data (settings, API keys, chat history). Now an installed row whose live `Info.Version`
  is older than the catalog's offers **"Update to vX.Y.Z"**, and `PendingModuleUpdates` applies it across a
  restart the way `PendingModuleRemovals` handles deletes (a loaded module's DLL is locked, so it cannot be
  overwritten in place): verified download → unpack into `<baseDir>\module-staging\<id>.staged` → marker in the
  data root → next launch swaps it in *before* `ModuleHost.LoadFrom`, then leaves the module's data directory
  untouched. Staging deliberately sits outside `modules\` (the loader loads every subdirectory it finds) and
  under `BaseDirectory` (same volume as the install, so the swap is a `Directory.Move`), and the swap moves the
  old copy aside first so a failed move rolls back instead of leaving no module at all. Unparseable versions
  offer nothing rather than guessing, and removals are processed first so an uninstall that raced an update
  wins. Covered by four new `--module-host-selftest` assertions on throwaway directories.
- ✅ **DONE (2026-08-14, 1.4.2) — the update check runs itself, once a month.** Requested right after the
  update path landed: an Update button nobody clicks "Check for modules online" to reveal is not much better
  than no update path. **A literal 1st-of-the-month alarm was deliberately NOT built** — a desktop companion is not
  always running on a given date, so that design silently skips any month the app happened to be off that day.
  Instead `ModuleUpdateSchedule` stores the month a check last SUCCEEDED (`yyyy-MM`, a marker file in the data
  root, not a settings field — machine state with no user meaning should not drag the settings schema and its
  migrations along) and a check becomes due as soon as the calendar month moves on: a companion started on the 5th
  having missed the 1st still checks, one left running a year checks twelve times. The month is stamped only
  after a **successful** fetch, so being offline costs nothing but a retry. A fresh install is seeded without
  checking (first check lands next month), and with no modules installed it stamps and skips the network
  entirely. `StartUp` evaluates two minutes after launch, then every six hours — that cadence is how it notices
  a month rolling over, NOT how often it hits the network. A hit shows a **tray notification** that opens
  Settings → Modules when clicked; nothing downloads or installs itself, so consent stays exactly where S6 put
  it. The version rule moved into a shared `ModuleUpdateScan` used by both the pane and the check, because a
  badge and a notification that disagree about what an update is would be worse than either alone. This is the
  only unprompted network request in the app, so it is a documented Preferences toggle (default on, absent in
  an older doc reads as on) and a new PRIVACY.md paragraph. 13 new self-test assertions cover the due-ness
  rule (same month, missed 1st, year boundary, clock moved backwards, unparseable stamp) and the version rule;
  a new CoreTests group pins the nullable-bool contract and the cross-process merge.
- ✅ **DONE (2026-08-13, v1.4.0) — release tags no longer collide with upstream's.** Upstream (Adrianotiger)
  tagged v1.2.3–v1.3.2 (2019–2021), so the fork's 1.2.x series ran into that range. Resolved by jumping the
  fork clear of it: the next release was cut as **v1.4.0** (not 1.2.4), so no tag collides. Releases continue
  from 1.4.x.
- ✅ **DONE (2026-08-13) — scrubbed a personal work email from the first 10 fork commits.** The fork's
  day-one commits (2026-06-24) were authored/committed under a work address rather than the project's
  `bigfnj` (personal identity) identity. Rewrote history with `git filter-repo --mailmap` to map those 10
  commits onto the `bigfnj` identity (0 commits now carry the old address; the HEAD tree stayed byte-identical,
  so no file content changed), then force-pushed master + the re-pointed release tags. Tracked file content was
  already clean. **Residual:** GitHub keeps the original commits in immutable `refs/pull/*/head` refs that a
  force-push can't remove — fully purging them needs a GitHub Support "remove sensitive data" request (or
  delete+recreate the repo). Left as a known, low-exposure residual.

- ✅ **DONE (2026-08-14) — S6p2 (Companions-as-a-module) built then fully REVERTED per user.** The whole stream
  (an `ICompanionManager` ABI + CompanionHost bridge, a `modules/Companions` plugin owning the Options→Companions pane + tray, per-row
  action buttons, per-type settings scoping, and a per-companion "voice" picker) shipped gated + pushed, but on the
  live eyeball the user disliked the module UI (lost tray icons, then the pane itself), so it was reverted to
  the pre-S6p2 state (`890f76d`). The original host Companions gallery + icon'd tray are restored. Design + code are
  preserved in git history (`feat(s6p2)` commits `53912a6`..`520aada`) if the direction is ever revisited.
- ✅ **DONE (2026-08-13, 1.4.1) — `DesktopAICompanion.Contracts` FileVersion tracks the product** (`9009133`). A fixed
  `FileVersion=1.0.0.0` made a Windows Installer major upgrade SKIP refreshing the ABI dll whenever its content
  changed but the version didn't — shipping a stale Contracts.dll that couldn't resolve new ABI types (hit live
  during the S6p2 eyeball install). Now FileVersion follows the product; `AssemblyVersion` stays `1.0.0.0` (the
  ABI binding version modules reference). Latent fix — matters whenever the plugin ABI changes.
- ✅ **DONE (2026-08-13, v1.4.0) — the companion read its OWN window as screen context ("sheep jokes on a loop").**
  The primary companion form is titled "Sheep" and — unlike child sheep — carries no `WS_EX_NOACTIVATE`, so a poke
  (right-click) or drag ACTIVATES it, making "Sheep" the foreground window. The context-aware fortune picker
  then embedded "Sheep" as the on-screen context, which (verified against the live 37,857-line index) puts
  **24 of the top-32 candidates in the sheep/wool cluster** — so the same memorable sheep/knitting jokes
  recurred several times a day whenever the user touched the companion. Fixed in `ActiveWindow`
  (`src/dotNet/Ai/ActiveWindow.cs`): screen-context capture now ignores foreground windows owned by the companion's
  own process (blanks the title + drops its bounds), so the picker falls back to a plain random fortune. Fails
  open. Base change, no module republish. *(How it was found: parsed the live 62 MB vector cache + re-ran the
  bge-small query path in Python to prove the joke was NOT a generic hub — rank ~15,800/37,857 for normal
  titles — but jumped to top-32 only for a "Sheep"-dominated title.)*
- ✅ **DONE (2026-08-13, v1.4.0) — the Genres filter was a no-op for downloaded packs.** Every plain
  (untagged) pack line was hardcoded `Topic="life"/Genre="quip"`, so disabling "tv-quote" or "fact" removed
  nothing — the ~150 downloaded tv-*/fact packs were all "quip". New `FortuneClassifier.ClassifyGenre(source)`
  derives a taxonomy genre from the pack id (tv-* → tv-quote, *fact* → fact, limerick/songs-poems → verse,
  dadjokes/yo-mama/riddles → joke, else quip), used by `TryParsePlainContent`. Coarse but honest (a plain pack
  is homogeneous in delivery style). **Behavior note:** a user who had tv-quote/fact disabled now really loses
  those packs from the pool on update — the intended effect. Module change → fortunes.zip republished + catalog
  regenerated.
- ✅ **DONE (2026-08-12) — the Fortunes module never had its own corpus** (PR #72). The S3 move dropped
  the `fortunes.txt` EmbeddedResource from the base csproj with a comment saying it had moved to
  `modules/Fortunes` — but `Fortunes.csproj` never picked it up, so `EmbeddedCorpus()` failed into
  `_embeddedError` (a diagnostics string nothing reads) on every build since, and a lean install had
  nothing to say. 10,310 lines restored; 7 of its 26 sources exist in no pack file, so it was never
  duplicate content. The two self-tests that would have caught it — `SmartFortunes.SelfTest` and
  `ProgressiveSelfTest`, both of which fail on an empty pool — had been orphaned by the same move and
  are now wired (the latter to `--fortunes-smart-progress-selftest`, run in CI).
- ✅ **DONE (2026-08-12) — "Rebuild smart index" always claimed the pool was empty** (PR #72). `Warm()`
  starts a background task and leaves `ready=false`; `WarmProgress` gates `total` on `ready`, so `total`
  is 0 for exactly the moment after a rebuild — which is when the button asked. The status now takes
  pool size from the provider (known synchronously) and lets the index's counters answer only "how far
  along". An empty pool with packs installed now blames the filters instead of saying "add a pack".
- ✅ **DONE (2026-08-12) — a checkbox tick cost a full engine rebuild** (PR #72). Each pack/genre toggle
  wrote settings AND re-read every pack file and re-warmed the ONNX index, so the new group toggle meant
  19 of those back to back. New `ListCard.DeferChanges`: the box moves at once, the pane goes dirty, and
  `SetChecked` replays once per CHANGED item at Apply, so any number of ticks costs one write and one
  rebuild.
- ✅ **DONE (2026-08-12) — published module payloads silently rotted.** `modules-dist\<id>.zip` is a
  committed artifact the catalog serves and nothing rebuilds it automatically. Both modules had drifted
  (see the corpus bug above; aibrain.zip was a release behind PR #71's Windows OCR, so catalog installs
  had no screen reading without Tesseract). `packaging\Test-ModulePublishFreshness.ps1` now fails CI when
  `modules/<Id>/` has commits newer than the newest touching its zip. Git-based rather than
  rebuild-and-compare-hashes, because hash equality would require byte-identical module builds across
  SDK versions and checkout paths — a stronger promise than this repo makes. Markdown is excluded
  (`modules/Fortunes/BACKLOG.md` would otherwise demand a 31 MB republish for a note).
- ✅ **DONE (2026-08-05) — Oversized speech bubble under Remote Desktop.** `FormSpeech` measured its text
  box at `GetDpiForMonitor(anchor)` but painted with the window's own DC; under RDP those diverge (the
  session virtualizes DPI), so the box was reserved for a higher DPI than the text was drawn at. Now
  measures at the window's own DPI (`GetDpiForWindow`, == the paint DPI), re-checked each follow tick so a
  reconnect self-heals. Fixed at the physical console previously; this closes the RDP case.
- ✅ **DONE (2026-08-04) — Dark theme colorization.** Every white-on-dark offender fixed: `NumericUpDown`
  edit fills (`DarkNumericUpDown` answering `WM_CTLCOLOREDIT` with a dark brush), the owner-drawn left
  `TabControl` strip + gutter (`DarkTabControl` filling the client on `WM_ERASEBKGND`), combos/trees/lists/
  edits/scrollbars (`SetWindowTheme` dark styles), companion-card thumbnails on solid-dark, the restored eSheep
  icon, and muted-hint contrast. Follows the Windows light/dark setting. (En route, fixed a crash from an
  earlier `SetWindowTheme(" "," ")` theme-strip attempt.)
- ✅ **DONE (2026-08-04) — Codebase optimization after the security cleanup.** ~4,870 lines removed across
  a build-warning-clean, full-suite-green audit: dead methods/overloads, 2 orphaned source files, 4 unused
  framework references, 2 dead packaging scripts, two write-only `StartUp` clusters, and — the big one — the
  ~2,530-line embedded C# `FinalPathResolver` in `StagingPathSafety.ps1` collapsed to plain PowerShell
  (4235→645 lines) with all function contracts preserved, plus the dead MutationTestHook (~42 sites) + unused
  build params. Release pipeline re-validated end-to-end (deterministic ZIP + MSI + ICE + all self-tests).
  Deliberately kept: `#if !PORTABLE` dual-build branches, `src/legacy/` quarantine, the `OllamaClient` 8-arg
  test-seam ctor, `AppPaths.CatalogCacheDirectory`.
- ✅ **DONE (2026-08-04, v1.0.6) — Mimiko companion cannot be downloaded / applied — UTF-8 BOM.** `Companions/mimiko/animations.xml`
  begins with a UTF-8 BOM (`EF BB BF`) before `<?xml`; the other companions in its shared cat/fox set
  (neko / fox / pink_neko) don't. The download itself verifies (the catalog SHA-256 is taken over the
  BOM'd git blob, so it matches), but applying it throws **"There is an error in XML document (1, 1)"**
  because `CompanionXmlValidator.TryParse` passes the decoded string — leading `U+FEFF` intact — straight into
  `XmlSerializer.Deserialize(new StringReader(xml))` (`src/dotNet/CompanionXmlValidator.cs:399`). **Fix (robust,
  recommended):** strip a leading `﻿` (and any leading whitespace) before the `StringReader`, so any
  BOM'd companion or user-dropped `.xml` parses — protects every companion, not just Mimiko. **Alt (data-only):** re-save
  `Companions/mimiko/animations.xml` without a BOM and regenerate the catalog hash.

- ✅ **DONE (2026-08-10) — Post-conversion cleanup audit** (PRs #39/#40/#41/#42, ~9,400 lines net removed,
  build-warning-clean + full-suite-green, four gated buckets). After the plugin re-architecture, a
  dead-code/leak sweep: **#39** deleted the FormOptions-only `DarkTabControl`/`DarkNumericUpDown` (0 consumers)
  + `src/legacy/` (old net48/UWP tree, in no build) and fixed two `CancellationTokenSource` leaks
  (`AiBrainModule._lifetime`, `CompanionsPaneControl._netCts` — cancelled but never disposed); **#40** stripped the
  base's dead dumb-fortune engine (`FortuneProvider`/`FortuneFileImporter`, extracting the one live type
  `FortunePackLoadPolicy`), the `OptionsController` façade + Preferences/Fortunes/self-test seams (kept
  `CompanionsController`), and dead StartUp AI members; **#41** collapsed `ContextMenus.cs` to PORTABLE-only (removed
  the dead `#if !PORTABLE` UWP branches + `OpenOptionWindow` shim + first-boot no-op) and added a **FormHelp
  re-entry guard** (was modeless + undisposed); **#42** shipped the **ONNX Runtime license + notices** with the
  Fortunes module (it redistributes `onnxruntime.dll` but carried no license). **Deliberately NOT done — surfaced
  for a decision:** the base `src/dotNet/Ai/` AI cluster (~4,900 LOC) is dead-but-anchored and fully duplicated
  by the live `modules/AiBrain/engine/` copies; removing it is the planned **S5c/d/e "AiSettings split"** (it
  rips ~57 SecuritySelfTest references + needs a user-settings migration), and the full Newtonsoft→System.Text.Json
  drop is broader than the cluster (11 base files use Newtonsoft, only 5 in the cluster). Do the cluster removal
  as its own deliberate stream, not a "safe delete."
- ✅ **DONE (2026-08-11) — S5c: base AI-brain cluster removed ("AiSettings split")** (PRs #44/#45/#46,
  ~6.8k lines net removed, expand→contract, each PR gated green on CI). The dead base AI code
  (`src/dotNet/Ai/*` — a full duplicate of the live `modules/AiBrain` plugin) is gone. **#44** relocated
  the ~50 base-only AI **security** assertions into the module's `--aibrain-selftest` (`AiEngineProbe`) so
  they exercise the shipping engine — **zero coverage loss** (SSRF/endpoint reject, no-plaintext-key +
  DPAPI-failure ciphertext preservation, credential scoping, executable allow-list, response
  sanitize/bounds, deadlines, HTTP-retry, session-lifecycle races). **#45** rehomed the one non-AI setting,
  the random-drop cadence, into `settings.json` (`AppSettingsDocument` + `LocalData` + a self-contained
  one-time migration from the legacy `ai-settings.json`; new CoreTests group → 24). **#46** deleted the 12
  brain files + trimmed `StartUp` (retire machinery, `ApplyAiBrainState`, the uncalled `ClearAiHistory`;
  `InitAiTriggers`→`InitDropTriggers`) and `SecuritySelfTest` (AI methods + AI-only doubles), keeping
  `ActiveWindow`/`HotkeyListener` (host services), `PokeReactions`, `FortunePackLoadPolicy`, and every
  non-AI test. **Newtonsoft stays** — 6 non-AI base files still use it; the System.Text.Json migration is a
  separate, later pass (`AppSettingsStore` is still 100% Newtonsoft). Verified every phase: clean `-Release`
  (0 warnings) + all self-tests + CoreTests (24) + hardening ps1 + resource-churn soak, locally and on CI.
- ✅ **DONE (2026-08-11) — Newtonsoft.Json dropped product-wide + About/Help moved to WPF** (PRs #48/#49/#50/#51,
  each gated green on CI). Two cleanups to lean the base before feature work. **(1) Newtonsoft → in-box
  System.Text.Json, everywhere.** #48 migrated the 5 straightforward base files (Program marker, CompanionHost dict,
  LocalData legacy read, PackCollections + RemoteCatalog DOM) behind a new lenient `src/Portable/JsonRead.cs`
  (`Str`/`IntOrNull`/`BoolOrNull`, mirroring Newtonsoft's null-tolerant `JToken` casts). #49 did the gnarly
  `AppSettingsStore` (public **fields** need `IncludeFields=true`; `[JsonExtensionData]` field→`Dictionary<string,
  JsonElement>` property; `JToken.DeepClone`→`JsonElement.Clone`; kept default null handling so the nullable
  absent-vs-null settings survive; `UnsafeRelaxedJsonEscaping`+`WriteIndented`) and dropped the base + CoreTests
  Newtonsoft PackageReferences + packaging manifests. #50 migrated the **AiBrain module** engine (its stale-writer
  merge ported to `SerializeToNode` + `JsonNode.DeepEquals`/.NET 9 + `DeepClone`-before-reparent), preserving the
  DPAPI/credential-scope/no-plaintext-key invariants (`--aibrain-selftest` 80/0). **Result: zero `Newtonsoft.Json.dll`
  anywhere in the Release tree.** (The Fortunes module never used Newtonsoft.) **(2) About + Help → WPF.** #51
  rebuilt both as themed WPF windows on the existing shell (`OptionsShell.OpenAbout`/`OpenHelp` → `AboutWindow`/
  `HelpWindow`, `WpfTheme`), added a shared security-reviewed `src/Portable/WebLinks.cs` (relocated the About-link
  HTTPS validator + a github.com/bigfnj/desktop-ai-companion doc allowlist), rewired the tray, and **deleted the WinForms
  `AboutBox` + `FormHelp`**. So the only WinForms left is the companion engine (`FormCompanion`/`FormSpeech`) + the dev-only
  `FormDebug` console (kept). *(WebView2 + the old `FormOptions` were already retired earlier in S5b-3 — the
  cleanup only had to correct stale docs.)* **✅ Eyeballed 2026-08-19 — the window renders correctly.** Captured
  by rendering `AboutWindow` to a PNG from a throwaway harness (reflection over the `(author, title, version,
  info)` constructor → `RenderTargetBitmap` → `PngBitmapEncoder`). Confirmed: dark theme applied to background,
  text and chrome; all **six** allowlisted documentation links present and legible in the dark link colour
  (`#6CB6FF`); the modernization blurb, "Using DesktopAICompanion" bullets and Original/Legacy credits all lay out
  correctly; Close anchored bottom-right. Content measured 524×581 inside the 560×640 window, so the companion-info
  card sits below the fold and scrolls, which is by design. *(Text looks slightly dim in a `RenderTargetBitmap`
  capture — grayscale antialiasing — and is crisp on screen; do not chase that as a bug.)*
  **Still worth a glance on the next reinstall:** the live tray → About / Help path on the installed MSI, and
  the light-theme variant (the capture followed this box's dark OS setting). The harness was **not committed**:
  a permanent render harness for one static window is not worth the machinery, and nothing else needs it.
  *(Correction, since the earlier wording sent readers hunting: there is no separate `HelpWindow`. Help was
  folded INTO `AboutWindow` and the tray entry is a single "About / Help". Nothing asserts this window —
  `--wpf-options-selftest` covers `CollectPanes` and `PaneView` only.)*

### Feature ideas (queued, not yet scoped)

1. ✅ **DONE (2026-08) — Fortunes-selection UX.** The flat source list is now a grouped `TreeView`
   (collection → sources, tri-state) with a filter box and a live "N of M sources · L lines" total; the
   fortune-packs section is a matching grouped download tree.
2. **AI-voice bundle** (one cohesive change — all touch `AiSettings` + `AiBrain.BuildSystemPrompt` + the AI tab):
   - **Personality presets** (~9 + Custom): a dropdown that writes a canned string into
     `AiSettings.Personality` (already injected into the system prompt in `AiBrain.BuildSystemPrompt`).
   - **Speech patterns** (pirate / l33t / rhyme / puns…): a *separate* "Speaking style" line appended to
     the system prompt (a structural constraint vs. personality = tone). Dropdown + Custom.
   - **Model-capability validation**: for Ollama, query `/api/show` capabilities to filter the Vision
     dropdown + assert on Test-connection; for generic `/v1` (no metadata) fall back to a name heuristic +
     a probe on Test. Never hard-block (let power users override).
3. **UI modernization** (Options looks dated). **Tier 1 SHIPPED + polished (2026-08)** — system-following
   dark title bar + fully dark controls (`WindowTheme.cs` + `DarkNumericUpDown` + `DarkTabControl`) on
   Options/About/Help, and the **Companions → Get more companions** gallery is now a 4-across grid of preview tiles
   (bundled icons, `CompanionThumbnails`) with aligned local-companion cards. Tier 2: Krypton Toolkit. Tier 3
   (superseded) — Options/About/Help are now native **WPF** windows (`src\Portable\Wpf\`), so the old
   WebView2/`FormOptions` HTML-settings-page idea is moot.
4. **Shimeji → animations.xml converter** — **SHIPPED; 31 converted companions are in `Companions/` and on the
   catalog (as of 2026-09-01).** The line below is the 2026-08-21 status and is kept for the research it
   records, not as a current statement. Since then: conversion, sprite compositing, the jump arc, the wall/
   ceiling regions, the surface-reach budget, the role-split rest dwell, five numeric migrations (reweight /
   rejump / reclimb / restdwell / restsplit) and a gate check that FAILS when a converted companion strands an
   animation. **Original 2026-08-21 status:** harness + research landed, no conversion yet. Unlocks the huge Shimeji skin library. Best-effort, offline-first (convert → hand-check
   → commit to our `Companions/` mirror); ship the *converter*, not copies (IP). Built as a console tool under
   `tools/ShimejiConvert`, **not** a module: the stated workflow is a dev workflow, and a CLI iterates far
   faster than a tray app. The engine is separable, so a module could wrap it later unchanged.
   - **Shipped this pass:** `ShimejiConvert verify <PetsDir>`, which grades companions with the app's REAL rules
     by recompiling `CompanionXmlValidator.cs` (source-included, the same trick `tests/DesktopAICompanion.CoreTests` uses)
     rather than reimplementing them where they could drift. Current result on the shipped corpus:
     **22/22 valid, 22/22 survive a DTO round-trip, 7 with unreachable animations** (all seven are sheep
     recolours sharing two dead animations). That proves the *output* half before a single Shimeji file is
     parsed — the 22 companions in `Companions/` are a free correctness corpus.
   - **`PetGraph`** adds the reachability pass the validator genuinely lacks (`CompanionXmlValidator` proves
     referential integrity, never reachability). **Terminal animations are NOT defects** — `grimoire/03`
     §6's respawn rule makes a dead end intentional; only *unreachable* animations matter.
   - **Mapping research is written up in `tools/ShimejiConvert/MAPPING.md`**, which also records what was
     already documented in `grimoire/03` (the four magic names §7, the `only` semantics and respawn rule
     §6) versus what this pass added, so the next session does not re-derive it.
   - **Two traps worth knowing before touching the parser.** Shimeji's own `conf/Mascot.xsd` restricts
     `Type` to six values while its shipped `conf/actions.xml` uses nine (`Sequence` 64×, `Floor` 18×,
     `Stay`, `Animate`, `Wall`, `Ceiling`…) — validating input against the vendor schema rejects the
     vendor's own reference skin, so the parser must be tolerant. And `Type="Embedded"` names a Java class
     (`com.group_finity.mascot.action.*`: `Breed`, `Dragged`, `Fall`, `Jump`, `ThrowIE`…), which is code and
     simply does not convert — those go in the residue report, never into a plausible-looking attribute.
   - **Next slice (deterministic, no model needed):** parse `actions.xml`/`behaviors.xml`, composite the
     per-pose PNGs into one sheet within the 4 MiB base64 budget (shipped sheep sit at 1.11 MiB;
     KuroShimeji's 46 sprites are 480 KiB on disk), flatten the action tree into `<next>` edges, emit the
     four magic names (synthesising `kill`/`sync`), and print the residue. An LLM repair loop over the
     residue is optional sugar — `ValidateXml` is the oracle that makes it safe, but the 80% is table-driven
     and a model would be slower and less reviewable.
5. ✅ **DONE (2026-08) — Secure online companion + pack downloads, plus offline bundling.** Shipped a
   runtime-fetched, HTTPS-trusted `catalog.json` (per-asset SHA-256; `SecureDownload.TryValidateBranchRawGitHubUrl`;
   `src/dotNet/RemoteCatalog.cs` loader; `packaging/New-ContentCatalog.ps1` generator that hashes the committed
   git blob so hashes match what raw serves). The Options **Companions** tab is now a live gallery — built-in +
   bundled + downloaded companions, each validated via `CompanionXmlValidator` and installed to `<DataRoot>\companions`; the
   **Fortunes** packs section gained "Check online for new packs" (verify → import to CustomDir). New content
   pushed to the repo + a regenerated catalog appears live, no rebuild. Also shipped **offline bundling**: the
   portable ZIP carries all 22 companions + 12 fortune packs beside the exe (`AppPaths.Bundled*Directory`; deterministic
   ZIP `-ContentDirectories`; shared `packaging/Stage-BundledContent.ps1`), while the MSI stays lean and pulls
   on demand. Verified end-to-end including a live GitHub fetch (all 34 assets hash-match raw; app
   `--online-selftest` PASS). Diagnostics: `--catalog-selftest`, `--catalog-parse-file=<path>`, `--online-selftest`.
6. ✅ **DONE (2026-08) — Granular per-source fortune packs (grouped tree).** The 12 monolithic packs
   were split by their column-1 source tag into **152 per-source packs** (`packs/<source>.txt`), grouped
   by a new embedded `packs/collections.json` (12 named collections). Content is byte-identical to the
   originals (50,860 lines) and all 152 display names are curated. The embedded pack catalog was fully
   retired (`packs.json`, `TrustedPackCatalog`, and the per-pack rights gate) in favor of the runtime
   `catalog.json`; the Fortunes tab gained a grouped tri-state **download tree** (mirror of the Sources
   tree) so a user can expand a collection and check individual shows/authors, with per-file SHA-256
   verification on download.
7. ✅ **DONE (2026-08-05) — Multiple _different_ companions on screen at once** (phases ①+② + tray). A
   `CompanionTypeRegistry` (`src/dotNet/CompanionTypeRegistry.cs`) holds each loaded type's `(Xml, Animations)`
   with a reference count, disposed when its last companion closes; `StartUp` keeps the active/default type
   as before and spawns extra types via `AddSheep(string id)` (loaded on demand through the new shared
   `CompanionCatalog`). The on-screen mix persists in settings **schema v2** (`companions: [{id,count}]`, migrated
   from the single count) and is restored on launch; the tray gained **Add a companion ▸** / **Remove a companion ▸**
   submenus. Verified live (a seeded 2 + 2 mix restores 4 companions incl. an extra type) + a `CompanionTypeRegistry`
   self-test (CI) + 3 CoreTests groups (migration/validation/merge). Original notes below.

   **Multiple _different_ companions on screen at once** (queued 2026-08-04) — e.g. Pearl **and** Rick together,
   not just N copies of one companion. Today every instance shares one global animation set
   (`StartUp.animations`/`xml`); "Use this companion" swaps that set and reloads all sheep, and the active companion is
   persisted as a single `animations.xml` blob (`LocalData.GetXml`). **Key enabler:** `FormCompanion` already
   takes its `Animations`/`Xml` _per instance_ (`new FormCompanion(animations, xml)`) and children inherit their
   parent's set, so rendering, physics, window-climbing, child-spawning, fortunes, the AI brain, speech
   bubbles, and fullscreen handling are all companion-type-agnostic already. This is wiring, not a rewrite. Work:
   (a) a small **registry of loaded companion types** (each its own `Animations`+`Xml`, lazy-loaded, disposed when
   its last instance closes) replacing the single global pair; (b) an **"add alongside"** spawn path that
   leaves existing companions untouched (`AddSheep` already takes the set as a parameter); (c) **UI** — an
   "＋ Add" button beside "Use this companion" (Use = replace all, Add = add one), optionally an on-screen roster
   ("Pearl ×1, Rick ×2"); (d) **persistence** — save a _list_ of companion ids + counts instead of one XML, with a
   migration from the single-companion format; (e) decide **tray / auto-start** semantics ("Add new sheep" and the
   auto-start-N-companions setting both assume one type). Cost: each type loads its own sprite sheet (a few MB) —
   trivial for 2–3 types. **Phased plan:** ① runtime-only "Add" (the mix resets to one companion on restart) to
   prove the engine handles it — small, low risk; ② persist the mix across restarts (list format +
   migration); ③ polish — per-type add/remove, roster, counts. Relates to historical **7.4** (which framed
   multi-companion as per-companion AI personalities — a natural follow-on once types can coexist).
8. ✅ **DONE (2026-08-05) — Responsive local companion grid + Options default width.** The "your companions" list now
   flows into fixed-width cards, **2 columns by default, scaling to 3–4** as the window widens
   (`ApplyLocalPetColumns` in `src/Portable/FormOptions.cs`), and the Options window sizes itself so the
   widest tab (Companions) shows its default 2-column layout with **no horizontal scrollbar**
   (`FitLocalGridToTwoColumns`, measured at runtime). Original notes below.

   **Two-column local companion list** (queued 2026-08-04) — the top "your companions" list in **Options → Companions** is a
   single vertical column (`BuildPetGallery` adds each `BuildPetCard` straight into the TopDown
   `flowLayoutPanel1`), so with several companions downloaded it gets tall and scrolly. Show it as **2 columns**
   once there are enough companions. The "Get more companions" catalog grid already wraps multi-column via a
   `LeftToRight` + `WrapContents` panel, so reuse that pattern for the local cards. Watch: keep the
   "Use this companion" / "✓ Active" buttons aligned inside the narrower cards, and decide whether the built-in
   default spans full width or joins the grid.
9. ✅ **DONE (2026-08-12) — Fortunes tab clarity** (PR #72). Rescoped the same day it was built: the
   original entry was written against the old WinForms tab and asked for a "complete overhaul", but PRs
   #69/#70 had already delivered most of it (grouped collapsible collections + filter, browse → tick →
   download, curated names for all 152 packs, an import path for your own files). What actually shipped
   was the three specific remaining items, plus two things found while building:
   - **(a) Live pool count.** `SettingKind.Info` joined the ABI as a display-only field, backing an
     "N fortunes from M packs" line that turns into a warning at zero. Content + source filters are hard
     constraints by design, so an impossible combination used to stop the companion talking with nothing on
     screen explaining why (a live box was found sitting on `spicyOnly=true`, exactly the setting that
     does it).
   - **(b) One ordered Content level** — Clean / Clean + edgy / Everything / Spicy only — replacing
     `SpicyFortunes` + `SpicyTier` + `SpicyOnly` (16 combinations, several contradictory). The old names
     lied: "Edgy + NSFW" admitted general + edgy + nsfw, i.e. everything, and "True NSFW only" kept tame
     lines while dropping the edgy tier. **Filter profanity** stayed separate, being a word filter rather
     than a tier. Migration is a pure function (`FortunesModule.MigrateContentLevel`) so all five readings
     are asserted in `--fortunes-engine-selftest` rather than checked by hand.
   - **(c) "Show me 5 examples"** prints five lines the current selection would actually produce.
   - **Group-level toggles** on collapsible list-card headers. Switching off a section (19 NSFW packs) was
     19 clicks. The header checkbox drives the child checkboxes through their own toggles, so the card's
     `SetChecked` still fires per changed item and the module persists identically — covered by new
     `--wpf-options-selftest` assertions (tri-state, per-item `SetChecked`, other groups untouched).
   - **Pack ceiling 128 → 512.** 152 packs ship; the old cap silently dropped 24 of them, which is why a
     search for "Simpsons" came back empty.
10. ❌ **REMOVED (2026-08-13) — "About" tab showing the README.** Dropped per the user. Superseded anyway:
    About + Help are now themed WPF windows (`AboutWindow`/`HelpWindow`, PR #51), so a README-rendering
    Options tab is moot.
11. ✅ **DONE (2026-08-05) — Version stamp in the Options window (bottom-left).** A muted
    `v<Application.ProductVersion>` label anchored bottom-left in `FormOptions` (`BuildVersionStamp`),
    sourced from `ProductVersion.props` at build time. Original notes below.

    **Version stamp in the Options window (bottom-left)** (queued 2026-08-04) — show the running build's
    version (`Application.ProductVersion`, sourced from `ProductVersion.props`) in the bottom-left corner of
    Options so "which version am I running?" is answerable at a glance. **Directly prevents the stale-build
    confusion that cost real time this session** (the box was on v1.0.1 while fixes shipped in v1.0.2+).
    Cheap — one `Label`; pairs naturally with the About tab (#10).
12. ✅ **DONE (2026-08-11) — "Triumph" insult-comic personality.** Added a **"Triumph"** preset to
    `AiBrainModule.PersonalityPresets` (modeled on Triumph the Insult Comic Dog: mock-compliment then savage
    put-down of whatever's on screen + the user, with the "for me to POOP on!" catchphrase). It's a
    *personality* (tone), so it stacks with the existing *speech* styles — notably **Triumph + "Samuel"
    speech = a relentlessly profane roast**, exactly the requested combination. Opt-in (default persona
    unchanged); the system prompt already backs it ("commit fully… never merely polite"). One-line data add,
    gated green.
    - ✅ **DONE (2026-08-11) — "Jeff Ross" personality + strengthened the "Samuel" speech instruction.** User
      reported Samuel wasn't swearing enough even on an uncensored model (`dolphin3:latest`, already configured
      locally). Root cause wasn't a code filter — `SanitizeResponseText` only strips control chars/length, no
      lexical filtering anywhere in the AI-remark path (`NoProfanity` is a *fortunes*-corpus filter, unrelated).
      It was that `Personas.cs`'s Samuel instruction was an abstract style adjective ("swear hard with real,
      unfiltered profanity") — small local models under-follow abstract style asks, especially while also
      juggling the 15-word cap, in-character constraint, and strict JSON output in the same system prompt.
      Fixed by making it a concrete, checkable requirement: "work at least one real, strong curse word... into
      every single remark. A remark with zero profanity in it has failed." Also added a **"Jeff Ross"** preset
      to `PersonalityPresets` (the Roastmaster General: filthy, below-the-belt roast jokes mixing affection with
      savage personal put-downs, funny first/mean second), paired the same way Triumph pairs with Samuel speech.
      `--aibrain-selftest` still 93/93 (no assertion pins the exact wording). **⚠ Still needs a live smoke test**
      with dolphin3 to confirm the concrete-requirement phrasing actually lands more profanity than the old
      abstract phrasing did — not yet observed running.
    - **Correction (2026-08-11): the "gate hygiene bug" noted earlier this session was a false alarm, not a
      product bug.** I'd run `--wpf-options`/`--module-host`/`--hardening`/`--security`/`--fortunes` — every one
      missing the required `-selftest` suffix (`Program.cs` only recognizes `--wpf-options-selftest`,
      `--module-host-selftest`, `--hardening-selftest`, `--security-selftest`, `--fortunes-selftest`). An
      unrecognized flag falls straight through to normal app startup: the first bad call grabbed the app's free
      second-instance slot and launched a real second companion instance (the "orphaned process"); every call after
      that just hit the built-in "Application is already running! Only 2 instances are allowed." MessageBox and
      returned — none of the five self-test suites actually ran. Re-ran with the correct flag names: all five
      pass clean, exit 0, no dialogs, no lingering process. **No fix needed; the flags work correctly as-is.**
    - ✅ **DONE (2026-08-11) — raised the remark length cap: 1-2 sentences, ~20 words each (40 max), room for
      a roast's setup + knockdown.** The old cap ("under 15 words," one sentence) sat at the bottom of English
      readability's 15-20-word "sweet spot" (per readability research: 14 words ≈ 90% comprehension; 15-20 is
      the general-purpose recommended range; NIH plain-language guidance caps at 20) and left too little room
      for a full sentence, let alone a curse word + a specific on-screen detail + in-character voice all at
      once — a likely contributor to Samuel under-swearing (word-budget competition, see the entry above).
      `AiBrain.BuildSystemPrompt` (`AiBrain.cs:99`) now reads: "Keep it to one or two sentences, about 20 words
      each (40 words at most) — for a roast or insult-comic personality, a short setup followed by the
      knockdown lands well; otherwise one sentence is often enough." Verified no downstream truncation risk:
      the speech bubble (`FormSpeech.cs`) measures + auto-sizes on the actual text (no fixed height cap), and
      `MaximumResponseCharacters` (512) comfortably exceeds a 40-word remark (~250 chars). All 6 gates green
      (this time with the CORRECT `-selftest` flag names).
    - ✅ **DONE (2026-08-11) — "Samuel" speech pattern re-targeted at Jules Winnfield specifically.** User's
      live smoke test (Jeff Ross + Samuel speech) roasted hard but still leaned light on profanity; asked to
      push it further WITHOUT making profanity mandatory again (a walk-back from the "hard requirement" wording
      two entries up), and floated switching the reference from generic "Samuel L. Jackson" to his specific
      Pulp Fiction character, Jules Winnfield. Agreed — a specific, heavily-documented character is a much
      sharper style-transfer target for an LLM than a vague "sassy and swears" actor descriptor, and Jules's
      actual delivery (curses landing as rhetorical emphasis inside a half-sermon, half-threat cadence, not
      uniform density) is exactly the "lean hard, not mandatory" shape asked for. `Personas.SpeechPatterns`'s
      `Id = "samuel"` entry **kept its Id unchanged** (so this box's already-saved `SpeechPattern: "samuel"`
      keeps resolving with zero migration) but its `Name` → **"Jules Winnfield"** and `Instruction` rewritten:
      profanity framed as "your default reflex, not a checkbox to tick," reach for it whenever it lands harder
      than a clean word (most of the time, with this voice), but it doesn't have to land in literally every
      remark. Confirmed no code/self-test pins the old "Samuel" display string. `--aibrain-selftest` still
      93/93. Dev install refreshed. **⚠ Not yet observed live** — same open gap as the two entries above,
      reasoned + gated offline only; this is the one to actually eyeball next.
    - ✅ **DONE (2026-08-11) — merged Personality + Speech style into one curated "Disposition" dropdown**
      (schema v2→v3). User's insight: the two axes stacking freely let incoherent pairings through (e.g. "Shy
      and sweet" + "Jules Winnfield"), and a single well-known character per entry is a sharper style-transfer
      target than an abstract adjective blurb (already proven true by the Jules Winnfield rename above) — so
      curate ONE list where tone + delivery are baked into a single instruction per entry, never mixed
      incoherently. User supplied an initial 24-character list; researched several via web search to verify
      accuracy (Brittany Broski's actual mechanism is tangential rambling, not "sassy" — removed her; Jeff
      Dunham's Walter, Anthony Jeselnik's cold arrogant one-liners vs. Jeff Ross's warm roasts, DC's Etrigan's
      menacing rhyme confirmed via Ollama-docs-style source verification, not guessed). Iterated with the user
      to drop Triumph (redundant with Jeff Ross/Jules Winnfield) and Shakespeare/Monty Python (neither cleared
      the "is this actually funny/distinct" bar), and to add 4 more "VERY CLEAR" archetypes at the same bar as
      Beavis & Butthead: **The Dude**, **Drill Sergeant**, **Foghorn Leghorn** (verified his actual "I say, I
      say"/"boy" tics via search), and **A Proper Butler**. Final roster: **26 dispositions**
      (`modules/AiBrain/engine/Dispositions.cs`, new file, replaces the old `Personas.cs` — the dead
      `Presets`/`Preset` struct, confirmed zero references, was deleted rather than carried forward). Kept 7
      ids identical to the old speech-pattern ids they absorbed (samuel/pirate/leet/rhyme/pun/yoda/valley) so
      those specific migrate cleanly; the other 19 are fresh slugs. Default is **Ted Lasso** (closest in spirit
      to the old default blurb). `AiSettings.Disposition` (single field) replaces `Personality` (free-text
      blurb) + `SpeechPattern` (id) entirely — `MigrateDispositionFromV2` reads the retired `SpeechPattern` key
      out of `ExtensionData` (STJ routes an unmatched JSON key there once the field is gone from the class),
      carries it over verbatim when it names one of the 7 absorbed ids, else falls back to the default (the
      free-text Personality blurb can't be reliably reversed onto a curated id, so it's discarded, not
      consulted). `AiBrainModule`'s two pane fields ("personality"+"speechStyle") collapsed into one
      "disposition" Enum field; `AiBrain.BuildSystemPrompt` now emits one "Disposition:" clause instead of
      stacking "Your personality:"+"Speech style:". **Real tradeoff worth remembering: merging removes the
      ability to STACK a tone preset with a speech style** (e.g. the old "Triumph personality + Samuel speech"
      combo) — every disposition instruction had to become self-sufficient (Jeff Ross's now bakes in its own
      profanity-forward delivery directly, no longer leaning on a separately-selected Jules Winnfield speech).
      Migrated THIS box live: old doc had `Personality:"sassy, brash..."` + `SpeechPattern:"samuel"` → landed
      on `Disposition:"samuel"` (Jules Winnfield) post-migration, confirmed by reading the actual file after a
      refresh — Jeff Ross is no longer active and needs re-picking from the new dropdown if wanted (its own
      instruction now carries the profanity itself, no pairing needed). `--aibrain-selftest` 93→96 (+3: catalog
      knows/rejects an id + instruction non-empty, replacing the old persona/speech-pattern trio; +2 new
      migration assertions: a carried-over id migrates cleanly, a retired id — e.g. old "uwu"/"shakespeare" —
      falls back to default). All 6 gates green. **⚠ Not yet observed live** — same open gap as everything
      else in this AI-voice stream; the whole merged pane + the new dispositions (Drill Sergeant,
      Foghorn Leghorn, The Dude, Butler) are **smoke-tested live and working — user-confirmed 2026-08-13.**
    - ✅ **DONE (2026-08-11, post-v1.2.1) — removed the chat-memory feature entirely** (PR #65). User: "this
      caused issues, remove it," referring to `MemoryEnabled` ("Remember recent remarks") — the SAME setting
      whose self-reinforcing replay (feeding the model its own last remark back into its own prompt) caused
      the repetition-loop bug worked around earlier this project by turning it off (dolphin3 + memory OFF),
      never actually fixed. Rather than re-litigate that fix, removed the feature outright: deleted
      `ChatHistory.cs` (945 lines) + every setting/pane/self-test touchpoint; `AiSettings.MemoryEnabled` is
      gone (no migration needed — a stale key on an old doc is inert). Follow-up user catch: with the feature
      gone, the "Clear chat history" pane action had nothing left to clear — removed that too. Two self-tests
      had used `MemoryEnabled`/`ChatHistory` purely as convenient test fixtures (not testing memory itself);
      swapped to `UseVision` for the stale-writer-merge test and dropped the now-undefined `partitionA` clause
      from the credential-scope test (its still-valid plaintext-key assertions kept, renamed). Net -1,110
      lines. All 6 gates green.
    - ✅ **DONE (2026-08-11, post-v1.2.1) — the companion no longer forces the user's name into every remark**
      (PR #64). User: "I dont mind that it says my name but are you forcing it for every dialogue? ... just
      when it makes sense." `AiBrain.BuildSystemPrompt` literally said "Always address them as &lt;name&gt;" —
      a hard per-remark requirement. Softened to "use their name only when it actually fits the remark, not
      in every single one," keeping the existing guard against inventing a name or reading one off the screen
      (window titles/paths were previously mistaken for the user's name — that protection is untouched).
    - ✅ **DONE (2026-08-11, post-v1.2.1) — fixed two missing tray-menu icons** (PR #66). User screenshot
      showed "Add a companion", "Remove a companion", "Test Speech", "Disable AI", and "Ask about my screen" all missing
      icons next to Options/About/Close, which had them. Turned out to be two different things: **"Remove a
      companion" and "Test Speech" simply never had an `.Image` assignment** in `ContextMenus.cs` (confirmed via
      code read, then confirmed with the user that "Add a companion" DOES show its icon — only these two were
      genuinely blank) — fixed by reusing the same app icon "Add a companion" defaults to (`Resources.icon`) for
      Remove, and the companion glyph (`Resources.esheep`) for Test Speech. **"Disable AI" and "Ask about my
      screen" can't show an icon at all — not a bug, a real ABI gap:** they're module-contributed via
      `DesktopAICompanion.Contracts.TrayItem` (`PluginApi.cs:73`), which has **no icon property whatsoever**. Fixing
      that means extending the plugin ABI (every module gets the capability, not just AiBrain) + picking real
      icon assets for AiBrain's tray items — queued as #15 below, not built.
    - ✅ **DONE (2026-08-11, post-v1.2.1) — #15 built same-day: every tray item now has its OWN distinct
      icon** (PR #67). User: "I dont want the 'same' icon repeated" — drew two new purpose-made icons (red
      prohibition sign for Remove a companion, speech bubble for Test Speech) instead of reusing Add-a-companion's/the
      companion glyph. For the module-contributed pair, actually built what #15 above queued: extended
      `TrayItem` (`PluginApi.cs`) with `byte[] IconPng` — raw PNG bytes, not a concrete image type, so the
      ABI stays framework-agnostic (no `System.Drawing`) per its own stated design goal — decoded host-side
      in `ContextMenus.BuildModuleMenuItem`. AiBrain ships a red X + tiny monitor as plain embedded resources
      (same pattern as Fortunes' `welcome.json`). **Real GDI+ gotcha hit and fixed:** decoding straight from
      a `MemoryStream` and disposing it immediately can throw "A generic error occurred in GDI+" later,
      since GDI+ can lazily reference the source stream — fixed by cloning into an independent `Bitmap`
      before the stream disposes. Also disposes the decoded module-icon Bitmap on every tray-menu rebuild
      (it rebuilds on each open) so repeat opens don't leak. Confirmed the icons actually embed
      (`GetManifestResourceNames()` on the built DLL) and confirmed live in the running dev install —
      user: "its perfect!"
    *(Original idea below.)* The AI-voice
    work this session shipped a **Personality** dropdown (12 canned presets incl. a profane **"Samuel"** =
    Samuel L. Jackson persona) and firm **Speech-style** patterns — both fed to `AiBrain.BuildSystemPrompt`
    (personality = tone, speech = delivery), so they **stack into emergent characters**. **Idea:** add
    insult-comic dispositions, starting with a **"Triumph"** personality modeled on *Triumph the Insult Comic
    Dog* — every remark a setup for a put-down ("…for me to poop on!"), roasting whatever's on screen and the
    user. Because personality and speech combine, e.g. **Triumph personality + "Samuel" speech = a relentlessly
    profane insult act that only roasts you.** Scope: one or two roast-oriented personality blurbs (and maybe a
    dedicated "insult everything" speech instruction); keep it opt-in (default persona stays friendly); note a
    small local model tends to soften the roast (per the dolphin-mistral-7B → dolphin3-8B testing this session).
    Build sites: `modules/AiBrain/AiBrainModule.cs` (`PersonalityPresets`) + `modules/AiBrain/engine/Personas.cs`
    (`SpeechPatterns`). Validates the persona blurb + speech-instruction prompt design.

13. ✅ **DONE (2026-08-11) — AI provider redesign: Local + Cloud coexist, cloud-primary with local fallback**
    (PRs #55/#56). `AiSettings` schema v1→v2: `Provider` is now the cloud selector `{""|openai|openrouter|
    custom}` and the local slot is the fixed `Endpoint`/`TextModel`/`VisionModel` (Ollama); new
    `CloudTextModel`/`CloudVisionModel`/`UseLocalFallback`. One-time migration preserves an old cloud user's
    scoped DPAPI key (scope hash unchanged). The AI Brain pane split into **Local provider** / **Local server
    (Ollama)** / **Cloud provider** (dropdown + endpoint + key + cloud models + consent) / **Fallback**. New
    `FallbackBackend` composite: a retryable cloud failure fails over once to the local Ollama model (mapped
    text/vision); a deterministic 4xx surfaces without falling over (shared `AiEndpointPolicy.IsRetryable`
    classifier). `--aibrain-selftest` 86 PASS/0 FAIL (migration + cloud-slot + 4 fallback assertions added;
    all credential-security assertions still green).
    - ✅ **DONE (2026-08-11) — fix: the local slot had been hardcoded to Ollama** (PR #58, user-caught during
      smoke-test). Before the redesign, `lmstudio`/`llamacpp` were valid local ids served by the generic
      OpenAI-compatible backend (llama.cpp/LM Studio speak the same `/v1` protocol); that was lost when the
      local slot became fixed-Ollama, and Ollama isn't bundled (confirmed — no `ollama.exe` in packaging),
      so a user without it installed had no local option at all. New `AiSettings.LocalBackendKind`
      (`"ollama"`|`"openai-compat"`, no schema bump — safe default for an absent key) +
      `AiBrainModule.BuildLocalBackend` picks the backend accordingly; a "Local backend" pane dropdown.
      `--aibrain-selftest` → 88 PASS/0 FAIL.
    - ✅ **DONE (2026-08-11) — capability-aware model dropdowns + uncensored tagging** (PR #60). The four
      model fields (local/cloud text+vision) are real dropdowns now, populated by two new "Refresh local/
      cloud models" pane actions. `OllamaClient.ListModelsAsync` reads `GET /api/tags`'s `"capabilities"`
      array (confirmed via Ollama's own docs — a genuine per-model signal on current servers, e.g.
      `["completion","vision"]`) for REAL vision detection; falls back to the (previously dead-code)
      `AiModelPolicy.LooksVisionCapable` heuristic when absent or for any generic `/v1` endpoint
      (`OpenAiCompatBackend.ListModelsAsync`, no capability metadata available). The vision dropdown only
      ever offers vision-flagged models. New `AiModelPolicy.LooksUncensored` (dolphin/uncensored/
      abliterated/unfiltered markers) tags and sorts matching models to the top of both dropdowns —
      **tagged, never hidden**, so other personas still see the full list; only Samuel/Triumph benefit from
      easy discovery. **Safety invariant (the design's load-bearing point):** the pane's Enum dropdown is a
      strictly closed, non-editable ComboBox — the currently-saved model is unconditionally unioned into
      every dropdown's options so opening+saving the pane can never silently blank a configured model, even
      before a refresh or with the model server unreachable. `--aibrain-selftest` 88→92 (0 FAIL). **✅
      Smoke-tested live and working — user-confirmed 2026-08-13:** the vision filter, the uncensored
      tag/sort, and the safety invariant all behave against a real Ollama instance.
    - ✅ **DONE (2026-08-11) — VRAM-size hint in the model label** (PR #62). User also asked for a "Browse"
      button to point a model field at an arbitrary local file. On the size ask: Ollama's `/api/tags` already
      carries a real `"size"` field (bytes on disk, a solid proxy for VRAM/weight footprint) that
      `ListModelsAsync` wasn't reading; added `ModelListing.SizeBytes` (long?, null for backends with no such
      metadata) + `JsonRead.Int64OrNull` + parsing in `OllamaClient.ListModelsAsync`. Labels now read
      `"4.9GB · dolphin3:8b · uncensored"` (size first — most scannable for "will it fit"). On Browse: **declined
      by the user after reconsidering** ("i think i mis-understood the 'browse' question, if ollama doesnt support
      custom pathing, then why are we adding it?") — Ollama can't be pointed at an un-imported file via chat
      requests at all (needs a `Modelfile` + `ollama create`, a real registration step, confirmed via Ollama's own
      docs, not a pointer), and a bare llama.cpp server's model is fixed at launch (`--model <path>`), not
      swappable per-request — so an "informational" file picker would only add a cosmetic, functionally-inert
      control. **No Browse button was built; don't re-propose one without this context** — `ollama pull` +
      the existing "Refresh models" action already cover real usage. Forced one design fix: a variable-length
      size PREFIX broke the old fixed-suffix label↔id strip trick, replaced with a proper `_modelIdByLabel`
      dictionary (`FormatModelLabel` registers label→id as a side effect; `ResolveModelId` looks it up on save;
      relies on the pane's Load-always-before-Save lifecycle). `--aibrain-selftest` 92→93. **The combined PR #60
      + #62 model-dropdown feature is smoke-tested live and working — user-confirmed 2026-08-13.**
      *(Original idea below.)* The AI Brain pane
    currently exposes one provider block. Rework into two: rename the existing block **"Local provider"**
    (Ollama/LM Studio on `localhost`), add a **"Cloud provider"** section (an OpenAI-compatible endpoint +
    DPAPI-encrypted key — the `OpenAiCompatBackend` already exists), and a **"use local provider as fallback"**
    toggle so a cloud failure/timeout falls back to the local model. Pairs with the existing "use cloud model"
    checkbox that swaps the model dropdown for a free-text field. Build site: `modules/AiBrain` (AiSettings +
    the pane schema in `AiBrainModule` + backend selection in `AiSessionManager`/`AiBrain`).
14. ✅ **DONE (2026-08-12) — screen reading works on a fresh box, via Windows' built-in OCR** (PR #71).
    The goal was "a fresh box has no OCR, so screen reading silently degrades." This entry proposed bundling
    a portable Tesseract; that was **not** what shipped, deliberately. Bundling, hosting, or CI-compiling
    Tesseract all make us the redistributor of a third-party binary — license notices, CVE patching duty,
    ~30 MB of download, and (for the compile route) a second heavy pipeline for Leptonica's dependency
    chain. Research also found there is **no official upstream Windows binary**: the de-facto one is a
    community NSIS *installer*, not a portable payload, so "just download it" would mean running an
    installer we can't hash-pin.
    **`Windows.Media.Ocr` ships with the OS.** A throwaway spike settled the three risks: it reads a probe
    image with no install step, it resolves inside the module's own collectible ALC, and the HOST does not
    need the projection (it travels with the module). Cost is **~6 MB compressed, in this module only** —
    the ~24 MB projection DLL is metadata-heavy and compresses ~4×, which is the detail the earlier WASAPI
    rejection (a ~25 MB *uncompressed* figure, against the base payload) obscured.
    Tesseract stays preferred (sharper on dense text): resolution is configured path → usual install
    locations → PATH → Windows built-in. **Test OCR names whichever engine answered** — a silent fallback
    would otherwise never tell the user the better engine exists — and a **"Get Tesseract…"** button opens
    the official guide; the standard installer lands where auto-detect already looks, so afterwards Test OCR
    just goes green with nothing to configure. Also added the **"Choose OCR engine…"** picker and an OCR
    path field (this entry claimed the picker had shipped; it had not).
    New ABI: **`IHost.OpenLink`**, gated on the calling module declaring `ModulePermissions.Network` and
    validated by the existing security-reviewed `WebLinks` HTTPS policy.
    **Caveat, verified against a no-WinRT control:** a WinRT-using module pins its collectible ALC, so it
    never unloads. Harmless — `Unload()` only runs at app shutdown or on load-failure paths, and module
    uninstall already forces a restart (`PendingModuleRemovals`). Noted in AiBrain.csproj beside the TFM.
    A self-test asserts Windows OCR reads a probe image **inside the module's load context** (skip-passing
    where the OS has no recognizer, e.g. a CI runner with no language pack), so the projection resolving
    under the real plugin loader is pinned, not just spike-verified.
15. ✅ **DONE (2026-08-11, same day) — extended the plugin ABI's `TrayItem` with an optional icon** (PR #67).
    `TrayItem` gained `byte[] IconPng` (raw bytes, not a concrete image type, keeping the ABI
    framework-agnostic); `ContextMenus.BuildModuleMenuItem` decodes it defensively. Any module can now ship
    a tray icon this way, not just AiBrain. Detail above in the AI-voice section (post-v1.2.1 tray-icon entry).
16. **Per-companion speech personality/preference** (queued 2026-08-11, unscoped, user's own caveat: "this may be
    complicated"). Today every on-screen companion shares the SAME global voice config — one `AiSettings.
    Disposition`, one (still-being-designed, not yet built) "Trigger Speech" source preference. The idea:
    let each companion TYPE carry its own — e.g. one sheep is AI Brain running the "Wednesday Addams" disposition,
    another is Fortunes tuned toward dad-joke-leaning packs, a third is AI Brain again but on "Jules
    Winnfield." Multi-companion-type coexistence already exists (`CompanionTypeRegistry`, backlog #7, DONE), so the
    on-screen mechanics for "more than one distinct companion at once" are already solved — what's NOT solved is
    that voice/personality config is a single global `AiSettings`/`FortuneSettings` blob, not keyed per companion
    type. Real complexity to scope later: (a) the AI brain's settings (disposition, model, provider) would
    need to become per-companion-type rather than one shared `AiSettings` document; (b) which companion a given
    poke/drop/AI-ask event is "for" already resolves through `ICompanion`/`CompanionHandle` in the ABI, so the plumbing
    to know WHICH companion triggered a reaction may already be there — needs verifying, not assuming; (c) whatever
    "Trigger Speech" setting design lands (still an open discussion as of this note) should be built with
    this in mind from the start — a global-only setting now that has to be retrofitted to per-companion later is
    much more painful than designing the storage key as companion-type-aware from day one, even if the UI stays
    global-only for its first cut.

17. ✅ **MOSTLY SUPERSEDED (2026-09-01) by the Remembrance module** — checked, not assumed. Remembrance
    already records BOTH directions (`WasapiLoopbackCapture` for system output + `WasapiCapture` for the mic,
    `modules/Remembrance/AudioRecorder.cs`), with per-direction device pickers and a start/stop hotkey. TWO
    gaps remain against the original wording, both deliberate in Remembrance and both real if the goal is
    *listening* rather than transcribing: the output is **WAV, downmixed to mono 16 kHz** (tuned for Whisper —
    see the `StereoToMonoSampleProvider` + `WdlResamplingSampleProvider(…, 16000)` chain), not MP3 at
    listenable quality; and it is a hotkey rather than a one-click tray entry. Reduced scope if wanted: an
    output-format choice on Remembrance, not a new module. Original note:
    scoped; came out of an audio-capture research pass). The want: click a tray item, it records the mic
    **and** system/loopback audio to a single MP3 (a meeting, a call), click again to stop. Filed here
    because desktopPet is already the .NET 10 tray app with the pieces to reuse — a tray-contribution ABI
    (`TrayItem`), a module loader with its own `AssemblyLoadContext`, an `Audio` permission, and **NAudio 3
    already in the base** for `AudioOutput`. Could ship as a module (`modules/Recorder`) or, honestly, as
    its own standalone tray app — a meeting recorder isn't "companion" behaviour, so decide that before building;
    the reuse argument is the tray/module/audio scaffolding, not a conceptual fit with a desktop companion.
    Real things to scope, not assume:
    - **Capture is two streams.** Mic = `WaveInEvent`/`WasapiCapture`; system output = `WasapiLoopbackCapture`
      (WASAPI loopback, no "Stereo Mix" needed). Mix to one file via a `MixingSampleProvider`, or record two
      tracks and mix on stop. **Watch the format mismatch** — loopback runs at the render device's rate/channels
      and the mic at its own; resample both to a common `WaveFormat` before mixing.
    - **The WASAPI payload question is already on file.** The base **rejected WASAPI for _playback_** over a
      ~25 MB SDK-projection payload cost (see the S5 note up top; DirectSound won, NAudio 3 stayed). Capture is
      the other direction — confirm whether NAudio 3's `WasapiLoopbackCapture`/`WasapiCapture` pull in that same
      projection cost before committing, since that was the deciding factor last time.
    - **Silence stalls loopback.** `WasapiLoopbackCapture.DataAvailable` doesn't fire while nothing is playing;
      the standard fix is to play silence through the device for the recording's duration.
    - **MP3 encoding.** NAudio can go WAV → MP3 via `MediaFoundationEncoder`, or shell out to the **ffmpeg
      already in the DevToolbox** (`WAV → -codec:a libmp3lame`). Record WAV, encode on stop.
    - **A new, more sensitive permission.** The existing `ModulePermissions.Audio` is for _playback_. Recording
      the user's mic + everything they hear is categorically different — a distinct capture/record permission
      with a **visible recording-in-progress indicator** (tray state), not a silent grant.
    - **Legal constraint, not a nicety.** Recordings can contain confidential or consent-regulated audio — many
      jurisdictions require all-party consent, and meeting/call content is often privileged — so this must be
      **local-only** (no cloud upload path, ever) and should make "you are recording" obvious. This rules out the cloud note-taker design
      entirely and is a first-class requirement, not a later polish. Off-the-shelf alternatives evaluated in the
      same research: Meetily (local, OSS, pairs with the box's Ollama) and Bandicam (paid) — this item is the
      build-it-ourselves option.

    **Fuller vision (2026-08-20 discussion) — the companion as a record → transcribe → summarize orchestrator.**
    The real pitch isn't "a recorder that happens to live near a companion"; it's that the companion is the always-on
    interface and trigger, and on stop it runs a pipeline: capture → auto-transcribe to a file → optionally an
    AI-brain summary file. The companion framing is genuinely supported by the ABI, and it also gives a status surface
    a plain tray app doesn't — but two things in the code make "just use its AI brain" more than a wiring job:
    - **The companion-as-trigger part is real and already expressible.** `IHost.RegisterPokeResponder` /
      `RegisterCompanionPokeResponder` (poke the sheep to start/stop), `RegisterHotkey` (a global "record now" combo,
      Hotkey permission), and `AddTrayItems` (a tray entry) all exist today. And the companion earns its keep beyond a
      launcher: it already has **speech bubbles** (Speech/Voice) + **animations**, so it can show "🔴 recording",
      "transcribing", "summary ready" ambiently — that's the actual argument for doing this in the companion.
    - **FRICTION 1 (the important one): modules are isolated and there is NO summarize/LLM verb on `IHost`**
      (grep-verified across `PluginApi.cs` — the only "brain" mentions are comments; the AI brain is itself a
      MODULE that consumes host events, not a service other modules can call, and each module runs in its own
      `AssemblyLoadContext`). So a recorder module cannot hand a transcript to "the brain." Two clean paths:
      **(a)** the recorder carries its **own Ollama call to `localhost:11434`** (the box already runs it; AiBrain's
      `OllamaClient.cs` is the pattern) — self-contained, zero cross-module coupling, the right v1; or **(b)** add a
      host-level text-generation service to the ABI so one brain config serves every module — cleaner long-term but
      a deliberate ABI extension and a new "modules share a service" pattern. **Do not design assuming
      module→module calls; they don't exist.**
    - **FRICTION 2: there is NO speech-to-text anywhere in the repo** (grep-verified — the only "whisper" hits are
      fortune-pack text). Transcription is the biggest new dependency, bigger than capture or summary. Windows'
      built-in speech (what #14 used for OCR) is dictation-grade and weak on multi-speaker meeting audio, so
      realistically a Whisper-class engine (whisper.cpp / faster-whisper — also Meetily's choice), shipped as a
      model-beside-the-exe like the bundled bge-small ONNX.
      - *Browser Web Speech API — considered + REJECTED (2026-08-20).* Clever but wrong for this on three
        independent counts: (1) it transcribes a **live mic only**, not a file or the system-loopback stream, so
        it can't ingest the recorded mix or hear the far-end participants — disqualifying alone for a meeting
        recorder; (2) classic mode is **cloud (Google)** and `webkitSpeechRecognition` works only in
        Google-branded Chrome — Electron/WebView2 throw a `network` error because Google restricts the endpoint,
        so an embedded browser can't use it; (3) it would **re-add the WebView2 engine S5b-3 deliberately
        removed**. Chrome 139's on-device mode (`processLocally` + `install()` language packs, Aug 2025) fixes the
        cloud/privacy count but not the mic-only or browser-dependency counts, and was flaky at release. Native
        `Windows.Media.SpeechRecognition` (the OS STT twin of the #14 OCR pattern) is local + browser-free but
        dictation-grade and live/stream-oriented — weak on a long multi-speaker call. **Whisper-class on the
        recorded file stays the pick.**
    - **Phase it — four subsystems (trigger/UI, capture, STT, summarize), built in independently-useful slices:**
      **P1** poke/tray/hotkey → capture mic+system → MP3 + recording indicator (the item above);
      **P2** on stop → local Whisper → transcript file beside the MP3;
      **P3** → local Ollama → summary file. Ship P1 first; it proves the capture stack and is useful alone.
    - Everything stays **local-only** (consent-regulated / privileged audio) — no cloud STT or summary path, ever.

18. **Consolidate standalone tray utilities into companion modules — candidate evaluation** (2026-08-20, not
    scoped). The companion is an always-on tray host with a plugin ABI, so it's a natural home for the small
    single-purpose tray apps in this account. Three were assessed against the module model (in-proc .NET 10
    C# `IModule` in its own ALC, talking only to `IHost`; user surface = tray items + declarative pane, no
    self-shipped WinForms/WPF):
    - **LightHost (`bigfnj/LightHost`) — NOT a fit for the Microphone module.** It's a C++/JUCE realtime
      VST/VST3 *effects host* (routes device-in → plugin graph → device-out live); grep-confirmed it has
      **zero capture/record/encode code** — no `AudioFormatWriter`, no WAV/MP3, and it doesn't do
      system/loopback at all. Can't be an in-proc C# module (C++ app, no DLL/C ABI), and as a separate
      process it emits nothing to record. Also GPLv3 via bundled JUCE + VST SDK (would infect the MIT companion).
      Mic capture → **NAudio (already in the base)** does mic + WASAPI-loopback natively. Only revisit
      LightHost if realtime VST mic-cleanup (noise-suppression/EQ before transcription) ever becomes a hard
      requirement, and then as a separate GPL-isolated process, never in-proc. (Relates to #17.)
    - **blinkingLED (`bigfnj/blinkingLED`) — port-with-work.** Same stack. Blink `Forms.Timer` loop stays in
      the module; rate presets + on/off ms → declarative pane; enable/pause → a tray item. Its Win32
      (`SendInput` VK_SCROLL, `IsKeyLocked`) is plain P/Invoke, ALC-safe. Work = flip EXE→Library, drop
      `Program.Main`/single-instance/DPI (host owns those), strip self-shipped UI (icon-picker
      `OpenFileDialog`, uninstall `MessageBox`, balloons, Start-with-Windows reg key), and re-base
      "quit when Caps ON" → "pause when Caps ON" (a module can't quit the host). **No LICENSE file — add MIT
      before bundling.** Companion framing: the Scroll-Lock LED as a heartbeat tell ("I'm awake and watching").
    - **IdleLauncherTray (`bigfnj/IdleLauncherTray`) — port-with-work, but licensing gates it.** The idle
      engine (`PhysicalIdle`: global `WH_KEYBOARD_LL`/`WH_MOUSE_LL` hooks reading the `LLKHF_INJECTED` flag to
      tell physical input from `SendKeys`/automation, `GetTickCount64` monotonic timing, XInput gamepad poll,
      `GetLastInputInfo` fail-safe) is dependency-free P/Invoke and drops straight into a module timer; config
      → pane, target-file chooser → `IHost.PickFilesToOpen` (host owns the dialog). **Biggest technical care:
      the low-level hook is global but injection-free, so an ALC-loaded lib CAN install it on the host UI
      thread — but it MUST be `UnhookWindowsHookEx`'d in `Shutdown()` or an ALC unload leaks a dangling hook.**
      **Biggest blocker is licensing: it's GPLv2, the companion is MIT — relicense (bigfnj-owned) before any
      engineering.** Companion framing: the sheep sleeps after N genuine-idle minutes (not fooled by anti-idle
      jiggles), launches your target on wake/poke, and locks the PC when it closes.

    **Two cross-cutting findings (these matter more than any single port):**
    - **The permission enum needs new capability flags — and this gates #17 too.** `ModulePermissions`
      (Speech/Animation/ScreenContext/Network/Hotkey/Storage/Companions/Audio/Voice) has NO flag for what these
      modules actually do: audio **capture** (today's `Audio` is playback-only), **synthetic keyboard input**
      (blinkingLED), **global input monitoring** + **arbitrary process launch** (IdleLauncherTray). Modules run
      in-process at full privilege with no sandbox, so they'd all *work* — but the consent screen would
      silently under-disclose. If the companion becomes a utility suite, add flags along the lines of
      `AudioCapture` / `InputSynthesis` / `InputMonitoring` / `LaunchProcess` so consent stays honest. Likely
      its own work item; additive to the enum (safe).
    - **Licensing is a recurring gate.** Companion is MIT. LightHost = GPLv3 (from JUCE — *not* ours to relicense →
      don't bundle). IdleLauncherTray = GPLv2, blinkingLED = unlicensed — both bigfnj-owned, so both need a
      deliberate MIT relicense before shipping as modules.

    **Reusable port recipe (for any same-stack tray app):** keep the dependency-free engine, discard the
    WinForms shell (`Program`/`Main`/single-instance/`NotifyIcon`/`OpenFileDialog`/`MessageBox`/custom Forms),
    rebuild the surface as tray items + a declarative pane, and be disciplined about tearing down OS-global
    state on ALC unload (hooks, Scroll-Lock state, audio devices).

### Smart-fortune topic routing — ✅ DONE (2026-08-05)

The original note here was stale (it predated the taxonomy rework). Current, verified state:

- **Taxonomy already built out** (`FortuneTaxonomy`, schema v2, `TaxonomyVersion 2026-07-31`): 12 topics
  (tech/science/work-money/love/family/faith/society/food/nature/arts/health-body/life) × 12 genres
  (tv-quote/observation/joke/pun/quip/aphorism/wisdom/fact/insult/verse/dark/uplifting) × 3 levels. Every
  fortune is tagged on all three axes, enforced at parse time.
- **Corpus coverage measured** (embedded baseline = 10,310 entries): all 12 topics populated, skewed
  toward `life` (49%) with `family`/`health-body`/`love`/`faith` thin, but nothing empty — so **no
  re-tagging was needed**.
- **Router replaced with prototype-embedding routing** (`SmartFortunes.Router` + `RouteByContext`): the
  old hardcoded process-name→topic table (5 app families, only 6 of 12 topics) is gone. Now one short
  prototype sentence per topic is embedded at warm and the on-screen context routes to the nearest
  topic(s) by centered cosine similarity — reuses bge-small, covers all 12 topics, routes on the actual
  context not the exe name, no app list. Still a soft score bonus. Verified by `--smart-selftest`
  behavioral asserts (a code context → `tech`, a recipe context → `food`).

Possible future polish (not queued): rebalance the corpus toward the thin topics; tune the prototype
sentences; make the route bonus context-strength-weighted.

### Deferred audit items (low priority)

- ✅ **#17** — resolved: `src/app.config` carries no manual binding redirects (`AutoGenerateBindingRedirects` covers it).
- ✅ **#12 DONE (2026-08-05)** — `VectorCache.Save` now prunes proactively: with an active pool set it no
  longer backfills non-active on-disk keys, so the file stays near the active-pool size instead of drifting
  toward the 100k cap (active keys from other processes are still merged; the no-active-pool diagnostics
  path is unchanged). Verified by `--smart-selftest` (incl. the saturated disjoint active-pool case).
- ✅ **#15 DONE (2026-08-05)** — `AiBrain.ComputeSignature` now reads its 16×16 frame via a single `LockBits`
  + `Marshal.Copy` pass instead of 256 `GetPixel` calls (identical luma output; the `ComputeSignature`/
  `CaptureScreen(...,1280)` source invariants the hardening harness greps for are preserved).
- ✅ **Land greeting timing** — resolved: `StartUp.LandTimer_Tick` polls the companion's fall (250 ms) and speaks only once it settles (~0.5 s of no descent, ~10 s safety cap), not a fixed 3 s.
- ✅ **Sass lines** — expanded from a 12-line seed to ~35 in `Ai/PokeReactions.cs`.

---

## Historical planning archive (obsolete)

> Everything from this heading through the end of the file is retained as an implementation-history
> snapshot. Its unchecked boxes, paths, packaging assumptions, and "locked" decisions do **not**
> describe the current product or backlog. Use the post-v1 backlog above for feature ideas and
> [`docs/RELEASE-CHECKLIST.md`](docs/RELEASE-CHECKLIST.md) for current release gates.

### Former "Fortune Sheep" v2 plan

The original full plan is in **[`FORTUNE-SHEEP-PLAN.md`](FORTUNE-SHEEP-PLAN.md)**. Its status snapshot
at the time was:

**✅ Phase A — DONE** (`243c085`, 2026-07-27): bundled corpus (`src/Fortunes/`, 13.7k SFW + 26.1k
Spicy, public domain), `Ai/FortuneProvider.cs`, right-click **poke-escalation** (1‑2 fortune → 3‑4
ignore/turn‑away → 5‑11 sass → 12 **bathtub escape**), land-fortune, Spicy toggle. The sheep is a
working, offline, zero-setup fortune machine.

**⬜ Phase B — contextual fortunes (the smart default).** In-process ONNX **bge‑small** embedder
(`Microsoft.ML.OnnxRuntime` + a BERT tokenizer, .NET 4.8), pre-computed corpus vectors (build-time),
embed the screen (active window + OCR) → **top‑k‑then‑random** match. Model delivered via **first-run
onboarding** (download‑now vs use‑random). ⚠️ **Validate the ONNX‑in‑single‑exe packaging FIRST** (native
runtime DLLs vs the embedded‑assembly trick) — the biggest risk in the plan.

**⬜ Phase C — AI insight tier + One Interface.** Replace `OllamaClient` with an `OpenAiCompatBackend`
(`/v1/chat/completions`) behind `ICompanionBrainBackend`; provider config/detection (none / Ollama / LM Studio
/ **OpenRouter** / **OpenAI**); **DPAPI‑encrypt** the cloud key; wire **insight into poke‑1** when a brain
is configured + "peek" on (Companion is the default preset). Vision routing (Phase 6) carries over.

**⬜ Phase D — presets + polish.** Fortune Teller / Companion / Quiet presets; idle ambient via the
embedder's semantic gate (replaces the luma gate); options‑tab pass (corpus, preset, idle freq, provider,
peek, model‑download button).

**⬜ Phase E — release.** Installer wiring for the first‑run model download; version bump; GitHub Release
with the MSI; README/grimoire updates.

**Open verification (eyeball):** the 12th‑poke **bathtub escape** and the **land fortune** are coded
(they reuse verified engine paths) but weren't cleanly screenshotted under automation — confirm by
spam‑clicking the sheep ~12×.

**Small TODOs discovered:** sass lines in `Ai/PokeReactions.cs` are a seed set (extend freely, or move to
a bundled `sass.txt`); the corpus is a first‑pass curation — refine SFW/Spicy anytime via
`src/Fortunes/build-corpus.sh`; land‑fortune fires ~3s post‑launch regardless of the actual landing moment.

**Deferred / backlog:** 6.4 PII scrubbing; 7.3 AI‑state companion art; 7.4 per‑companion AI; 7.5 .NET/WPF port.

---

## Historical Phase 1 — Speech Layer (no AI dependency)

Goal: get a speech bubble rendering on screen that tracks the companion. No LLM involved yet. Proves the rendering approach before wiring in the brain.

| # | Item | Notes |
|---|------|-------|
| 1.1 | **`FormSpeech.cs`** — borderless WinForms follow-window | Tracks `FormCompanion.Left/Top`; renders above the companion; transparent background |
| 1.2 | Speech bubble shape | Custom-painted rounded rect + tail pointer; no WPF, pure GDI+ `Graphics.FillPath` |
| 1.3 | Typewriter text effect | `Timer`-driven character reveal at ~30ms/char |
| 1.4 | Auto-dismiss | Bubble fades/closes after configurable N seconds (default 6s) |
| 1.5 | `FormCompanion` integration | `FormCompanion.Say(string text)` public method; wires up `FormSpeech` instance |
| 1.6 | "Test Speech" context menu item | Fires a hardcoded line to verify rendering |
| 1.7 | Multi-monitor positioning | Speech bubble stays on same screen as companion; clamp to working area |

---

## Historical Phase 2 — Ollama AI Brain

Goal: connect to a locally running Ollama instance and generate responses from screen context.

| # | Item | Notes |
|---|------|-------|
| 2.1 | **`OllamaClient.cs`** | `HttpClient` wrapper for `POST /api/chat`; streaming response via `ReadLineAsync`; configurable endpoint + model |
| 2.2 | **`AiBrain.cs`** — orchestrator | Owns the capture → OCR → prompt → response pipeline |
| 2.3 | Screen capture | `Graphics.CopyFromScreen` for full desktop; downscale to 1280×720 before sending |
| 2.4 | OCR text extraction | Shell out to `tesseract` exe; parse stdout; strip non-printable chars |
| 2.5 | Change detection gate | Frame diff (sum of pixel delta); skip LLM call if screen unchanged by > threshold |
| 2.6 | Prompt design | System prompt establishes companion persona + emotion vocabulary; user prompt = OCR text; optional base64 image for vision model |
| 2.7 | Response parsing | Expect JSON `{ "text": "...", "emotion": "happy" }`; fall back to plain text with neutral emotion |
| 2.8 | Emotion → animation mapping | Table: `happy→walk/jump`, `sad→fall`, `thinking→scratch`, `excited→run`, `confused→look-around`; map to animation IDs from `animations.xml` |
| 2.9 | Error handling | Ollama not running → companion stays silent (no crash); timeout 8s; retry once |

---

## Historical Phase 3 — Triggers

Goal: give the user explicit ways to invoke the AI, plus opt-in proactive behavior.

| # | Item | Notes |
|---|------|-------|
| 3.1 | **Global hotkey** — `RegisterHotKey` P/Invoke | Default `Ctrl+Alt+P`; configurable in settings; triggers reactive ask |
| 3.2 | "Ask [companion name]" context menu item | Same as hotkey but via right-click menu |
| 3.3 | Reactive ask flow | Capture screen → OCR → send to Ollama with "what do I see?" prompt → companion speaks + emotes |
| 3.4 | **Idle commentary loop** (opt-in) | Every 90–150s if screen changed meaningfully, companion makes an unprompted short remark |
| 3.5 | Idle gate | Skip idle commentary if `FormCompanion.State != Passive` or if last interaction < 30s ago |
| 3.6 | "Listening" animation | Trigger a named animation (e.g. `look`) while waiting for Ollama response; cancel on response |

---

## Historical Phase 4 — Configuration

Goal: make the AI layer configurable without recompiling.

| # | Item | Notes |
|---|------|-------|
| 4.1 | **`AiSettings.cs`** | JSON settings file in `%APPDATA%\DesktopAICompanion\ai-settings.json`; persist on change |
| 4.2 | Ollama endpoint | Default `http://localhost:11434`; editable in options dialog |
| 4.3 | Model selector | Separate text model and vision model; populate from `GET /api/tags` response |
| 4.4 | Hotkey configuration | UI to remap the global hotkey |
| 4.5 | Idle commentary toggle | On/off + frequency slider (30s–300s) |
| 4.6 | Speech bubble style | Font size, display duration, max character width |
| 4.7 | Extend `FormOptions` | Add "AI" tab to the existing options dialog |

---

## Historical Phase 5 — Context & Memory

Goal: make the companion smarter about its surroundings and consistent across sessions.

| # | Item | Notes |
|---|------|-------|
| 5.1 | Active window title tracking | `GetForegroundWindow` + `GetWindowText`; include in prompt ("user is in VS Code") |
| 5.2 | Time-of-day persona | Morning/afternoon/evening tweaks to system prompt tone |
| 5.3 | Rolling conversation history | Last N exchanges kept in memory and included in Ollama context window |
| 5.4 | Persist history | Save/load from `%APPDATA%\DesktopAICompanion\chat-history.json`; rolling 20-message window |
| 5.5 | Companion name personalization | `GhostConfig`-style JSON: companion name, user name, personality blurb → injected into system prompt |
| 5.6 | Screen zone awareness | Detect which app is under the companion (title bar overlap) and comment on it |

---

## Historical Phase 6 — Vision (optional upgrade path)

Goal: use a local vision-language model for richer screen understanding when the user wants it.

| # | Item | Notes |
|---|------|-------|
| 6.1 | Vision model toggle | Option to send a downscaled screenshot (base64) alongside the OCR text |
| 6.2 | Model routing | Text-only call for idle commentary; vision call only on hotkey ask (more expensive) |
| 6.3 | Recommended models | `llava`, `qwen2.5vl`, `moondream` — document in README |
| 6.4 | PII scrubbing | Blur/redact sensitive regions before sending (password fields, etc.) — P/Invoke `FindWindow` to identify input fields |

---

## Historical Phase 7 — Polish & Distribution

| # | Item | Notes |
|---|------|-------|
| 7.1 | Installer (NSIS or WiX) | Bundles the EXE; optionally bundles Ollama installer check |
| 7.2 | First-run onboarding | Detect if Ollama is not running; show setup dialog with model pull instructions |
| 7.3 | Custom companion XML for AI states | Add new animation IDs to `animations.xml` for AI-specific emotions (thinking, excited, confused) |
| 7.4 | Multiple companion support | AI brain per-companion; each companion has its own personality JSON |
| 7.5 | Upgrade path to .NET 10 WPF | Long-term option once physics engine is fully understood; not for v1 |

---

## Historical reference implementations

| Phase | Primary reference | Specific files |
|-------|------------------|---------------|
| 1 (speech) | bigfnj/Ghostpet-Prototype | `Controls/SpeakPanel.xaml`, `Controls/SpeakPanel.xaml.cs` |
| 2 (Ollama client) | — | `OllamaClient.cs` is greenfield; Ollama REST docs at `http://localhost:11434` |
| 2 (screen + OCR) | mediar-ai/screenpipe | `crates/screenpipe-vision` capture loop, change detection gate |
| 3 (reactions→animations) | alvinunreal/openpets | `src/reaction-animation-mapping.ts`, `src/local-ipc-protocol.ts` |
| 4 (settings) | bigfnj/Ghostpet-Prototype | `AppSettings.cs` |
| 5 (window tracking) | alvinunreal/openpets | `src/window-tracker.ts`, `src/terminal-focus.ts` |
| 6 (vision routing) | alvinunreal/openpets | `src/plugin-ai-gateway.ts` |

---

## Superseded decisions recorded by the original plan

- **Keep .NET Framework 4.8 WinForms** — the physics engine is deeply WinForms-native (Win32 P/Invoke, WinForms.Timer, ImageList). Porting to WPF would break the product without improving anything visible.
- **Ollama only (no cloud APIs)** — all inference runs locally. No API keys, no data leaves the machine.
- **AI layer is additive** — `FormCompanion.cs` and `Animations.cs` are not modified. The speech bubble and brain are separate classes that observe and call into the existing API.
- **Emotion hint is a string, not an enum** — keeps the prompt contract loose so new emotions can be added without recompiling.
