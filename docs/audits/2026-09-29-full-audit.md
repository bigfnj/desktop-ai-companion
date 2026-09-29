# Desktop AI Companion: full code audit

Audited tree: `D:\.ai-work\projects\desktop-ai-companion` at commit `35725396` (master, 2026-09-28, product version 1.2.6). Audit date: 2026-09-29. Read-only: no file in the repository was changed.

## 1. Scope and method

The audit covered every source file in the repository: the .NET 10 WinForms host (`src/`), the plugin ABI and ModuleKit, the eight modules under `modules/`, the ShimejiConvert tool and its engine, the three C# test projects, the 27 PowerShell scripts, the Python harnesses, the CI workflows and the MSBuild files. That is about 99,000 lines of C#, 11,000 of PowerShell and 6,500 of Python.

Four passes were run, each independent of the last.

1. A mechanical pass. Every project was built on an isolated copy of the tree with the full Roslyn analyzer set enabled (all CA rules plus the IDE dead-code rules). The real checkout was not touched because it builds with warnings as errors. The build produced 3,467 distinct diagnostics. The 159 in the disposal, lifetime, dead-code and blocking-call families were each read in context; 10 became findings and 147 were recorded as false positives with the reason (for example, double-checked locking flagged as an always-false condition, and module classes whose `Shutdown` disposes the fields the analyzer says are never disposed).
2. A reading pass. Twenty-six readers were each given a slice of files and required to read every line and report which files they had read in full. Four more readers swept the whole tree through one lens each: threading and re-entrancy, resource lifetime, dead code and duplication, hot-path cost. A further agent digested `BACKLOG.md`, `docs/DESIGN-REGISTER.md`, `docs/BLOCKED.md` and `docs/IDEAS.md` into a list of items already open, already refuted and already decided, so that nothing already on record would be re-filed as new. The readers filed 497 reports; the analyzer triage added 10. After merging reports of the same defect from different agents, 462 distinct findings remained. Twenty-seven of them were reported independently by two or more agents.
3. A verification pass. Every finding was judged against the code rather than against the write-up. The 167 findings rated medium or above, plus every race, leak and security finding at any severity, each received two independent verifiers: a skeptic instructed to refute the claim by reading callers, callees, disposal paths and threads, and a reproducer instructed to trace the path from a real trigger to the effect and name the thread, the frequency and the configuration needed. Where the two disagreed a third reader judged. The 295 low and info findings each received one verifier with the same instruction to check the claim as stated. Every verifier also checked the finding against the known-items digest.
4. My own check. I read the code for all fifteen findings the readers rated high, and again for the four that survived verification at that severity.

The verification pass ran 155 agents and disagreed with the readers often enough to matter: 15 findings were refuted outright, 64 were lowered from medium to low, 11 from high to medium, and 4 were raised from low to medium. Nothing was left uncertain. Several refutations were settled by measurement rather than argument: one verifier decoded the IL of the NAudio assembly the module actually ships to confirm how it raises its stop event, another ran the registry call in question unelevated to show it throws rather than returns null, a third timed the process-name lookup on the runtime the product targets.

### Headline numbers

| Measure | Value |
|---|---:|
| Distinct findings after merging | 462 |
| Confirmed against the code | 447 |
| Refuted | 15 |
| Left uncertain | 0 |
| Confirmed and not already on the backlog | 437 |
| Confirmed: high | 4 |
| Confirmed: medium | 67 |
| Confirmed: low | 306 |
| Confirmed: info | 70 |
| Files assigned to readers and not read in full | 0 |
| Agents that failed or returned nothing | 0 |

Severity in this report is the verified severity. Where the two verifiers disagreed on severity and agreed on the verdict, the lower of the two was kept, so the counts above are conservative.

## 2. Verdict

The codebase is in good condition, and the two categories this audit was most asked about, leaks and races, came back with the least in them. No unbounded leak exists on any path a user reaches. Of the 22 confirmed leak findings, three are medium and all three are bounded growth or retention rather than a leak in the strict sense: a persisted set that stops being pruned while a calendar slot is in error, scratch recordings that a purge does not recognise, and a vector cache that keeps a second copy of every embedding. The other nineteen are one-object-per-event misses (an MP3 reader per chime, a form icon per spawn, menu children cleared without disposal) and self-test litter in `%TEMP%`. Of the 24 confirmed race findings, one is high, four are medium, and the remaining nineteen are latent: they need a thread that no shipped module uses, or a window of a few milliseconds that the code's own comments already describe.

The threading model the host promises, one UI thread with everything else marshalled back to it, holds across the tree. The disposal discipline holds across the tree. Validation is fail-closed wherever the code says it is. The readers graded every slice between B minus and B plus, and the two cross-cutting sweeps for threading and for resource lifetime graded the whole tree A minus. I concur with those grades.

The defects that remain sit at seams rather than in the middle of things:

- Between background work and the UI thread. A continuation lands after the state it was computed against has changed (the options pane rebuild, the PetStudio import, the Fortunes rebuild), or a wait on the UI thread blocks the very message the wait is for (the Remembrance stop).
- Between a module and the host contract. A cloud provider with no cloud model set sends the local default model name to the cloud; a permission flag exists in the ABI and no module declares it; a `SayAll` with no companion on screen drops a reminder that has already been marked as fired.
- Between the code and its own comments. Fourteen of the dead-code findings are members whose neighbouring comment says they are live. Two backlog closures describe a state the code does not show.
- Between a test and the thing it claims to test. Fifty-six confirmed findings are checks that would pass on the broken build they were written to catch. Two of them are in the CoreTests project that the gate and CI run on every change.

The largest single performance theme is synchronous file and decode work on the UI thread at module initialisation, on Apply, on pane open and on every settings write. None of it is on the animation tick, which is where the host has already spent its optimisation effort, and the tick findings that remain are low.

## 3. The four high findings

These four survived two verifiers each, and I re-read the code for all four.

### F168. Remembrance stops recording by waiting on an event that is posted to the thread doing the waiting

`modules/Remembrance/AudioRecorder.cs:113-131`. `Stop()` calls `StopRecording()` on each WASAPI capture and then waits up to 10 seconds per source for a `RecordingStopped` handler to set an event. NAudio's `WasapiCapture` captures `SynchronizationContext.Current` in its constructor and posts `RecordingStopped` through it. Recording is started from the hotkey or the tray item, both on the WinForms UI thread, so the capture holds the WinForms context and posts the event to the UI thread. `Stop()` also runs on the UI thread, blocked inside the wait, so the posted handler cannot run and every wait times out. With the default two sources a normal stop takes 20 seconds. On the shutdown path the consequence is worse: a session end or a Restart Manager restart during an app update kills the process inside the wait, before the scratch WAV headers are finalised, so the recording is lost, which is the loss the module's own 1.0.11 note set out to prevent. The verifier decoded the shipped `NAudio.Wasapi.dll` to confirm the constructor captures the context and the stop path posts through it.

Fix: construct the capture with no synchronization context current (save, set null, construct, restore in a `finally`), so the event is raised on the capture thread as the handler already assumes. Alternatively drop the wait and rely on `Capture.Dispose()`, which joins the capture thread. Then time `recorder.Stop()` on the shutdown path; it should read well under a second.

### F116 and F115. The Scroll Lock blinker's belief about the LED drifts from the LED

`modules/BlinkingLed/engine/ScrollLockBlinker.cs:139-224`. `Toggle()` calls `SendInput` and records a refusal in `LastWin32Error`, but both callers flip `_phaseOn` unconditionally afterwards. A refused injection (a locked session, a UAC prompt, an elevated foreground window) therefore advances the belief while the key does not move. After an odd number of refusals the flag is the inverse of the LED for the rest of the session: the cadence runs lit for the off interval and dark for the on interval, and `Stop()`'s corrective toggle, which is gated on `_phaseOn`, no longer fires when the LED is lit, so switching the feature off strands Scroll Lock on. F115 is the same drift from the other side: `Start()` sets `_phaseOn = false` without reconciling a key that `BlinkOnce()` has just lit, so the sequence off, "Blink once now", enable inverts the cadence. The 1.0.5 fix lives only in `Stop()`, which the enable path never calls. The self-test asserts neither behaviour (F114).

Fix: make `Toggle()` return whether the input was accepted and flip the flag only on success, at both call sites; have `Start()` read the hardware state once. Rework the two self-test checks so they hold on accepting and refusing machines alike.

### F066. An unprompted remark is a vision turn although the setting says vision is for explicit asks

`modules/AiBrain/AiBrainModule.cs:1083-1097`. `OnDrop`, the responder for the host's random-drop timer, calls `Ask(pet, true)`; the second argument is `allowVision`. With "Use vision" on, every unprompted drop captures the screen and sends an image, while the pane label, the settings comment and the brain's own comment all say vision is used for explicit asks (the hotkey and the poke) and that the drop takes the cheaper OCR path. The verifier checked the history and found no record of the reversal, only the 1.2.3 entry that folded the idle loop into the drop with no word on routing. The practical cost is a cold vision-model load and an image round-trip every fifteen minutes on the default schedule, plus VRAM use the user did not ask for.

Fix: `Ask(pet, false)` in `OnDrop`, matching the poke; or, if vision on drops is wanted, change the label and record the decision. A small seam is needed before the module self-test can assert the flag.

### F155. Companion Studio decodes the whole sprite sheet on the UI thread on every analyse

`modules/PetStudio/PetReport.cs:161-175`. To obtain the engine's view of the entry animations, the analyser stages the pet through the source-linked `Xml.TryReadXml`, which re-runs the XSD-validating parse (the analyser has already parsed once), base64-decodes the PNG, decodes it with GDI+, cuts it into up to 1,024 tile bitmaps and then disposes all of it. This runs on the dispatcher thread from the 750 ms re-analyse timer after every typing pause, from Open, from the installed picker and from the import continuation. The backlog records this as refuted ("PetAnalyzer.Analyze does not decode the sheet"); the refutation read the WPF decode path, which is cached, and missed the staging path, which is not. Both verifiers traced the staging path line by line.

Fix: resolve the four entry animations in the module without staging images (the reachability walk reads only `AnimationDrag`, `Fall`, `Kill` and `Sync`), or add a stage-without-images entry point to `Xml`, and reuse the first parse. Correct the backlog entry.

## 4. Findings by theme

### Races and threading (24 confirmed: 1 high, 4 medium, 15 low, 4 info)

The four medium items share one shape: a background completion is applied without re-checking the state it was computed against.

- F375 `src/Portable/Wpf/OptionsWindow.cs:1097`. A `ReloadPaneAfter` action that completes after the user has switched panes rebuilds its own pane into the content area, so the navigation and the content disagree and the other pane's unsaved edits are dropped.
- F158 `modules/PetStudio/PetStudioWindow.cs:712`. The `_importing` guard is set only after the extraction await, so a second Import or a Close during extraction deletes the tree the extractor is writing.
- F176 `modules/Remembrance/RemembranceModule.cs:358`. The save-and-mix task after a normal stop is untracked, so exiting inside that window truncates `recording.wav`.
- F186 `modules/Reminder/CachingCalendarSource.cs:45`. The refresh latch has no deadline, so an Outlook COM call that blocks on an Object Model Guard prompt freezes that slot until Outlook answers or the user hits Apply.

The low items are worth reading as a group because they document where the single-thread promise is thin rather than broken: module settings dictionaries read on pool threads in Reminder and AiBrain while the UI thread may be writing (F192, F100, F068), the `IsFullscreenActive` getter that scans and raises an event on whatever thread calls it (F309, F331), a responder list walked live while a callback may dispose its own registration (F332), and the two-second exit window in `StartUp` during which a drag-and-drop still reaches `LoadNewXMLFromString` (F312).

### Leaks and resource lifetime (22 confirmed: 3 medium, 18 low, 1 info)

- F204 `modules/Reminder/ReminderModule.cs:1293`. The fired-id set is pruned only when every slot succeeds, so a healthy slot beside a misconfigured one grows the persisted set without bound.
- F171 `modules/Remembrance/CaptureStore.cs:110`. The scratch WAVs (`recording.system.wav`, `recording.mic.wav`) and failed-start stubs match no purge shape, so raw audio from any abnormal end is kept forever against the 72-hour promise in `PRIVACY.md`.
- F135 `modules/Fortunes/engine/SmartFortunes.cs:21`. The vector cache keeps a raw copy of every pooled vector after the final save, doubling vector memory: about 5 MB by default and about 90 MB with the full pack catalog installed. Bounded, but the second copy has no reader.

Among the low items, three are per-event misses in the host that a long session accumulates: `DecodeModuleAudio` never disposes the `Mp3FileReaderBase` and its ACM stream, so every Reminder chime leaves one to the finalizer (F246); every `FormCompanion` deserialises a designer icon it never shows (F272); the host-owned tray submenus `Clear()` their children without `Dispose()` while the module-submenu path beside them documents why that is wrong (F256). The shift-launched debug list has no row cap (F273). The rest are self-test litter: `%TEMP%` directories, a registry key and lock files left by the hardening and module-host self-tests on every run (F290, F294, F338, F351).

### Dead code and duplication (76 confirmed, all low or info)

Seventy-six items across 99,000 lines is a low count, and it agrees with the maintainer's own record that dead members have been swept repeatedly. Nothing found is load-bearing. The items worth acting on first are the ones whose comments claim the opposite of the truth, because a reader trusts them:

- F359 `src/Portable/LocalData.cs:187`. `GetPetSizeLevelNoLock` is unused while the comment above it says the size-override paths use it.
- F076 `modules/AiBrain/engine/AiBrain.cs:1378`. `DescribeOcrEngine` has no caller; its doc and a neighbouring comment describe it as live.
- F238 `src/dotNet/Ai/ActiveWindow.cs:140`. `CaptureContext` computes a foreground rectangle it never uses; the comment says it feeds `ScreenContext.ForegroundWindowBounds`.
- F088 `modules/AiBrain/engine/AiPaths.cs:36`. `LegacyMigrationEnabled` is hard-coded false, and its branch would be skipped by its own guard even if enabled.

Structural dead code with a measurable cost: the legacy `Properties.Settings` object is written on every save and read by nothing, and its `.settings` machinery costs 40 to 50 ms at startup (F362); the `#else` branch of `Main` cannot compile (F283); `CompanionXmlValidator` carries a four-argument `TryParse` with fifteen cancellation checkpoints that no caller can reach (F253); `AppPaths` keeps a seven-member fortunes-migration cluster with zero production callers (F357); `AiSettings` persists seven Fortunes-era fields with no reader (F093).

Duplication is the larger maintenance risk. `AtomicFile` and `CrossSessionLock` exist in both the host and ModuleKit (F358), and the CoreTests project compiles the host copies into the same assembly it uses to test the ModuleKit copies, which is how F382 arises. Six hand-rolled `IHost` fakes and five `FakeCompanion` copies live in the host self-tests beside the ModuleKit.Testing versions (F349, F340). The Reminder module has three `HH:mm` parsers with divergent acceptance rules (F203). The converter CLI carries nine migrations that are ten copies of one skeleton (reader assessment, `tools/ShimejiConvert/Program.cs`).

### Performance (66 confirmed: 1 high, 10 medium, 55 low)

The pattern is synchronous work on the UI thread at moments the user is waiting.

- F361 `src/Portable/LocalData.cs:889`. Every changed settings field is a synchronous full-document durable rewrite of `settings.json`, and the active pet's XML is embedded in that document, so a slider tick costs about 130 ms at the default pet's 1.17 MB and about a second at the largest shipped pet. F308 is the same cost N+1 times on a pet-type reload.
- F127, F122, F123 `modules/Fortunes/engine/`. The corpus parse runs on the UI thread at startup and after every folder change (about 0.25 s by default, 1.2 to 1.4 s with the full catalog); Import runs the importer synchronously against its own worker-thread contract and re-parses every existing pack just to count rows, then `RebuildEngine` parses them again.
- F155 (above) and F318 `src/dotNet/Xml.cs:183`. The sprite sheet is decoded twice per staged pet, three times on a Companions-pane update.
- F200 `modules/Reminder/ReminderModule.cs:715`. Every Apply rebuilds every calendar source, discards each slot's last-good list and refetches every URL, so the tray reads "nothing upcoming" for the next 20 seconds.
- F138 `modules/Fortunes/engine/SmartFortunes.cs:359`. The cold-warm checkpoint rewrites the whole `cache.bin` every 2,048 entries, so checkpoint I/O grows with the square of the new-line count.
- F070, F105, F106 `modules/AiBrain/`. Under the default "unload" residency each audition sample is a separate cold model load; the fallback backend probes cloud then local sequentially with the 120-second generation deadline each, so a provider that drops traffic costs about 240 seconds of silence per ask; `WarmUpAsync` hard-codes a ten-minute keep-alive, so "Keep loaded while the app runs" expires after ten idle minutes.

On the animation tick itself the findings are low: the alpha path rebuilds an HBITMAP and calls `UpdateLayeredWindow` every tick even when the frame is unchanged (F262), `UpdateValues` runs twice per transition with the first result discarded (F241), and `CheckTopWindow(true)` re-walks the z-order every tick while a pet stands on a window (F269). The `FallDetect`/`RiseDetect` walk is already recorded as measured and declined; the new detail is that `FallDetect` reads a second window title it never uses (F266).

### Checks that cannot fail (56 confirmed: 6 medium, 50 low)

This category matters more than its severities suggest, because the repository's method depends on its gates.

- F382 `tests/DesktopAICompanion.CoreTests/ModuleKitTests.cs:20`. The two "ModuleKit" groups bind to the production `AtomicFile` and `UnicodeTextProgress` compiled into the test assembly, because a name in the enclosing namespace wins lookup over the `using` directive. The ModuleKit copies that ship inside every module have no direct regression test, and the project file's comment says the opposite.
- F384 `tests/DesktopAICompanion.CoreTests/Program.cs:428`. The corrupt-primary recovery group passes on a store that never consults the backup, because the backup holds the default volume.
- F149 `modules/Fortunes/FortunesModule.cs:1293`. The "Rebuild smart index" currency guard hashes the same immutable list its baseline came from, so once the index is complete the button is a no-op that reports "already built", including after the fortunes folder changed on disk, which is the case the button exists for.
- F114 `modules/BlinkingLed/BlinkingLedModule.cs:902`. `Stop()`'s corrective toggle is unasserted; deleting it leaves the suite green.
- F287 `src/dotNet/RemoteCatalog.cs:469`. The pack-file-cap check reads the host's `FortunePackLoadPolicy.MaximumFiles`, whose only reader is that check; the cap that governs loading is the module's separate copy.
- F403 `tests/mutate-diagnostics.py:184`. The harness never clears the shared `%TEMP%` marker and ignores exit code and timeout, so a run that dies is graded from the previous run's marker.
- F445 `tools/ShimejiConvert.Engine/Shimeji/BundleSelfTest.cs:17`. No self-test ever executes `dwebp`; the fixture ships PNG sprites, so a missing decoder passes `selftest` while every real bundle import fails.

### Correctness (144 confirmed: 2 high, 36 medium, 106 low or info)

The medium correctness items are listed per module in section 6. The ones with the widest user-visible effect:

- F245 `src/dotNet/AudioOutput.cs:267`. `DirectSoundOut` reports device failure asynchronously through `PlaybackStopped`, which nothing subscribes to. The default-device fallback and the `_unavailable` latch are unreachable, a dead output stays started for the session, and `PlayOwned`, `PlayNotification` and the Preferences preview report success into a mixer nobody reads. Device loss means silence until restart.
- F101 `modules/AiBrain/engine/AiSettings.cs:1039`. A cloud provider with no cloud model set passes `CanUse`, and the brain sends the local default `gemma3:4b` to the cloud endpoint: a 400 and silence with the local fallback on, or substitution to the provider's first-listed model with it off. F103 and F107 compound this: the fallback backend cannot enumerate models, so a bad cloud id never triggers the spoken advisory, and an answered 401 is logged as "unreachable".
- F130 `modules/Fortunes/engine/FortuneProvider.cs:1391`. An undeclared tagged pack with one malformed row is silently re-read as prose, so the companion recites the metadata columns. All 158 shipped packs are undeclared, and the module's own UI invites hand edits.
- F148 and F149 `modules/Fortunes/FortunesModule.cs:1287`. The rebuild button reports "Smart picks are off" while rebuilding, and never rebuilds after the first complete warm.
- F199 `modules/Reminder/ReminderModule.cs:440`. Reminders are marked fired before delivery is checked; with no companion on screen `SayAll` drops the bubble and the reminder is spent, though the chime plays.
- F061 `modules/AgentFlow/VsCodeSetup.cs:301`. `FixDanglingComma` maps a comma ordinal through comment-stripped text, so on an `argv.json` whose hand-appended port key is the last member, Disable deletes the wrong comma; VS Code then ignores the file at every launch and the module reports success. On the stock file the deleted comma lands inside a comment, which is why this was lowered from high.
- F226 `src/DesktopAICompanion.Contracts/PluginApi.cs:121`. `ModulePermissions.LaunchProcess` exists and no module declares it, although AiBrain, Remembrance and PetStudio spawn processes. `docs/BACKLOG-CLOSED.md:582-612` records this as closed with "three holders RIGHT NOW"; the flag was added to the enum and the declarations were never made, so the consent screen still does not mention process launch.
- F270 and F278 `src/dotNet/FormCompanion.cs:2108`, `FullscreenScan.cs:59`. Coverage detection rejects every occluder whose title bar lies above screen y=0, so it is disabled for pets on a monitor above the primary; and the fullscreen scan excludes only root pet handles, so a child pet or speech bubble over a monitor's centre keeps the pet visible over a game.
- F352 and F367 `src/dotNet/Plugins/PendingModuleRemovals.cs:36`, `ModulesPaneControl.cs:561`. A pending removal marker is cleared even when the delete threw, and a cancelled install leaves a half-populated module folder that the pane lists as installed.
- F372 `src/Portable/Wpf/OptionsWindow.cs:35`. The fixed 820 DIP initial height exceeds the work area of 1366x768 and 1080p-at-150% displays; the caption opens above the screen top.

### Security (3 confirmed, all low)

Certificate revocation checking is left at the .NET default (off) for the cloud provider slot and the whisper.cpp installer (F079); `release.yml` pastes a repository variable into two single-quoted `run` bodies instead of routing it through `env` (F002); the AgentFlow research harness prints the first 64 characters of `tool_input` on `--tiers` hit lines against the file's own never-printed rule (F016). No credential handling defect was found; the DPAPI path, the URL validation and the download hash checks held under review.

## 5. What did not survive, and what was already known

Fifteen findings were refuted (Appendix B). The pattern is instructive: most rested on a runtime premise that is true of .NET Framework and false of .NET 10 (`RegistryKey.OpenSubKey` throws rather than returns null on access denied; `Process.ProcessName` no longer takes a system-wide snapshot; `Process.GetCurrentProcess()` opens no handle until one is needed), or on a layout preference dressed as a defect. One medium finding about the release-prune ordering was refuted by this repository's own live releases.

Ten confirmed findings were already recorded (Appendix C): seven as deliberate decisions, two as closed, one as refuted. Two of those records now disagree with the code and should be corrected: the PetStudio "no second decode" refutation (F155, above) and the `LaunchProcess` closure (F226, above). The `TryFindSelfTest` determinism item (F339) is recorded as closed in host 1.2.4 while the verifier found the second stage still takes the first match; that one is low and worth a look rather than a re-open.

## 6. Grades by slice

The readers graded each slice they read in full; the two cross-cutting sweeps for threading and resource lifetime graded the whole tree. Full assessments with strengths and concerns are in the audit report outside the repository.

| Slice | Grade |
|---|---|
| agentflow-engine | B |
| agentflow-module | B |
| aibrain-backends | B- |
| aibrain-core | B |
| aibrain-selftests | B |
| fortunes-provider | B- |
| fortunes-smart | B- |
| host-engine | B |
| host-io-settings | B- |
| host-lifecycle | B |
| host-plugins-abi | B |
| host-selftests-security | B |
| host-selftests-ui-plugins | B |
| host-wpf-options | B |
| host-xml-speech | B |
| petstudio | B- |
| python-research | B- |
| remembrance | B- |
| reminder | B |
| scripts-build | B+ |
| scripts-tests | B+ |
| shimeji-emit | B |
| shimeji-parse-cli | B |
| shimeji-selftests | B |
| small-modules | B- |
| sweep-deadcode | B+ |
| sweep-perf | B |
| sweep-resources | A- |
| sweep-threads | A- |
| tests-csharp | B- |

## 7. Recommendations

In order.

1. Fix the four highs. Each is a contained change with a verified fix path: the Remembrance capture context (F168), the blinker's flip-on-success and start-time reconcile (F116, F115), the drop routing argument (F066), and the PetStudio staging path (F155). Verify each in the real app, per the repository's own gate: time the shutdown save, watch the LED through a refused injection, log the vision flag on a drop, and profile a typing pause in Companion Studio with a large converted skin.
2. Repair the gates before leaning on them. The check-cannot-fail items in CoreTests (F382, F384), the module self-tests (F114, F149, F150) and the mutation harnesses (F403, F445) are the checks a fix campaign will trust. Mutation-test each repair the way the repository already does: break the guarded thing, see exactly one failure, restore.
3. Move UI-thread I/O off the UI thread by owner. The repository already has the right pattern in two places (`AiBrainModule.BeginVramProbe`, `AgentFlowModule.BeginSetupProbe`: single-flight background work with a generation counter and a UI-thread continuation). Apply it to the settings store (F361, F308), the Fortunes corpus and importer (F127, F122, F123, F143, F146), the PetStudio analyser and deletes (F155, F159), the Reminder Apply (F200) and the Remembrance snapshot (F177, F181). Measure cold, in fresh processes, before claiming a number.
4. Close the four medium races with the same generation-counter idiom (F375, F158, F176, F186) and add a deadline to the calendar latch.
5. Bound the three retention items: prune the fired-id set per slot (F204), teach the purge the scratch shapes (F171), drop the vector cache's second copy after save (F135).
6. Remove the dead code, comment-contradicting items first (F359, F076, F238, F088), then the structural clusters (F362, F283, F253, F357, F093). Each is a small diff; the value is in the comments that stop lying.
7. Consolidate the duplicates that have already diverged or that mislead tests: `AtomicFile`/`CrossSessionLock` (F358), the host `IHost` fakes versus ModuleKit.Testing (F349, F340), the three `HH:mm` parsers (F203).
8. Correct the two records that disagree with the code (the PetStudio refutation and the `LaunchProcess` closure), and add the `LaunchProcess` declarations to the three modules that spawn processes.
9. Consider splitting the self-test out of `modules/AgentFlow/AgentFlowModule.cs`. At 6,589 lines mixing product code with 478 assertions, every reader named it as the file where defects hide. `StartUp.cs` (2,109 lines, two lifecycles on one timer) and `FormCompanion.cs` (2,988 lines) are the host equivalents. This is structural debt rather than a defect, and it is the reason the seam defects above survived nine earlier audits.
10. Two behaviours are decisions rather than fixes and should be recorded either way: whether unprompted drops may use vision (F066), and whether the stale vision-marker list on OpenAI-compatible backends should become a soft hint (F102, already recorded as deliberate; the finding adds that the same list is the sole gate in the dropdowns).

## 8. Audit decisions

Decisions I made without asking, so they can be reviewed.

| Decision | Choice | Why |
|---|---|---|
| Analyzer pass location | Built an isolated copy under the scratchpad, never the checkout | The checkout builds with warnings as errors; an `.editorconfig` there would have broken other sessions' builds |
| Verification depth | Two verifiers plus a judge for medium-and-up and for every race/leak/security finding; one verifier for low/info | 462 findings times two verifiers exceeded the per-workflow agent cap and would have doubled the wall time for the tier where the stakes are lowest |
| Severity on disagreement | The lower of the two verifiers' severities when they agreed on the verdict | A conservative count is easier to trust than an inflated one; the appendix keeps both values |
| "Already known" handling | Confirmed findings already on record are kept, marked, and listed separately | Two records turned out to disagree with the code; dropping known items would have hidden that |
| Report location | `D:\tmp\desktop-ai-companion-audit-2026-09-29\`, outside the repository | The tree is shared with other sessions and the audit was read-only; the findings can be moved into `BACKLOG.md` as part of the fix effort |
| Resume after the interrupted run | Resumed the same run id with identical arguments | The 26 finished groups replayed from cache; only the interrupted and unstarted agents re-ran |

## 9. Coverage and cost

| Pass | Agents | Subagent tokens | Wall time |
|---|---:|---:|---:|
| Analyzer build and triage | 1 | 0.34 M | 25 min |
| Reading (26 slices, 4 sweeps, 1 digest) | 31 | 9.65 M | 66 min |
| Verification (105 groups, 155 agents incl. 3 judges) | 155 | 23.1 M | 97 min plus the 22 min of the interrupted first attempt |

Every file assigned to a reader was reported as read in full, and the workflow's own coverage check found no gaps. No agent failed or returned an empty result. The analyzer build covered all twelve projects; its 3,467 diagnostics by rule are in `analyzer-summary-by-rule.txt` and the full list in `analyzer-diagnostics.txt`.


## 10. Where the rest is

The complete report (module assessments, every confirmed finding with mechanism and fix, the refuted and already-known lists) was delivered outside the repository at D:\tmp\desktop-ai-companion-audit-2026-09-29\; the CSV beside this file carries every finding with both verifiers' verdicts. The fix campaign that followed is recorded in BACKLOG.md under the section dated 2026-09-29 and, once closed, in docs/BACKLOG-CLOSED.md.

