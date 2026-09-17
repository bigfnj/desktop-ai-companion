# Desktop AI Companion — Backlog

> Fork of Adrianotiger/desktopPet. The original physics experience is preserved, while compatibility,
> correctness, validation, and security fixes do modify engine files where required.

## How this file works

**Open items only.** A closed item is deleted from here rather than marked done — git holds the
history, and 2,560 lines of finished work is what stopped this file being readable as a backlog.
Anything closed that still carries standing value was extracted rather than deleted:

| file | what it holds |
|---|---|
| [`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md) | closed knowledge written to be CONSULTED: refused designs, the two noted-not-scheduled ABI gaps, behaviours that read as defects and are not, the closed bug log |
| [`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md) | the completed-work record from v1.0.0 on, with the estimates that turned out wrong kept beside their corrections |
| [`docs/ISSUES-post-1.0.0.md`](docs/ISSUES-post-1.0.0.md) | the BUG-001 to BUG-004 post-mortems, kept in full because two of them were wrong in instructive ways |
| [`docs/BLOCKED.md`](docs/BLOCKED.md) | items that cannot be actioned from here, each with its blocker named on its own line |
| [`docs/HISTORY-pre-1.0.0.md`](docs/HISTORY-pre-1.0.0.md), [`docs/ISSUES-pre-1.0.0.md`](docs/ISSUES-pre-1.0.0.md) | the same two records for the repository that preceded v1.0.0 |

**Conventions.**

- **Bugs are numbered `BUG-00N` and the number is never reused**, so a commit, a test or a code
  comment can cite one. `modules/AiBrain/`, `modules/PetStudio/`, `src/dotNet/`,
  `docs/RELEASE-CHECKLIST.md` and `handoff.md` all cite them today. The next one filed is BUG-005.
  **No bug is open right now** — the closed BUG-001 to BUG-004 register moved to
  [`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md), because "none open" is not a TODO.
- **Status is prose plus a glyph, never a checkbox:** ✅ done · 📌 open, with the reasoning recorded ·
  ⬜ not started · ⚠ a caveat, or a claim that has not been observed. There are no markdown
  checkboxes anywhere in this file, and adding one would lose the reasoning a glyph sits next to.
- **An entry keeps the measurement that settled it.** Where a number decided something, the number
  stays in the entry — otherwise the next reader re-derives it, or guesses. **A number that merely
  DESCRIBES the repo is the opposite case: it goes stale and nobody notices.** Say what to count and
  where, or make the code count it — see "Numbers in documentation" in
  [`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md), which exists because one such figure was
  wrong three times running.
- **Before writing "needs a host change" here again, grep `PluginApi.cs` for the verb.** That
  sentence cost a planning cycle: the Reminder entry asserted the ABI could not drive a companion
  animation or move a companion, while `IHost.TryPlayAnimation` and `IHost.PlayAnimationAll` had
  existed since the emotion work and AiBrain had been using them all along. The full case is in
  [`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md).
- **A refused design is recorded, not forgotten.** Read the "Settled decisions" section of
  [`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md) before proposing something that looks
  obviously missing — and its "Decisions that read as defects" before filing one as a bug.

---

## 🚧 AgentFlow — BUILT, not published (notify half, 2026-09-17)

**Status: the module exists and is gated. It is NOT published.** `modules/AgentFlow/` is built by
`build.ps1`, `tests/run-gate.ps1` throws if its folder is missing from the build output, and
`--module-selftest=agentflow` runs in both the gate and CI. `modules-dist/` and `catalog.json` are
deliberately untouched, so no existing user is offered it — the same arrangement `TestModule` has.

This heading said "research only, nothing built" until 2026-09-17, six commits after the module
landed, while the same section already said "Built, 25/25 self-test" further down. Recorded because
status words in this file are load-bearing and that one contradicted itself.

Read [`docs/agentflow/README.md`](docs/agentflow/README.md) before proposing work — it carries the
measurements, and several obvious designs are ruled out by numbers rather than opinion. Five
runnable harnesses live beside it.

**What remains open, in order:**

- 📌 **Default-mode precision is unmeasured, and it is the only number still missing.** Recall is
  settled at 93% (28/30 against calls a permission rule actually blocked). Precision in `default`
  cannot be measured on this box: 120 transcripts contain **zero** rule-caused denials in that mode,
  because this machine runs `auto`. Generate it the only way it can be generated — work normally in
  default mode for an hour — then rerun `agentflow_join.py`, which now reports the split itself.
- 📌 **`MinHostVersion` must be raised from `1.0.0` before this module is ever published,** and
  `ProductVersion.props` bumped with it. See the ABI item below; the two are one decision.
- ⬜ The answering half. Four actuation channels, none of which needs synthetic input, and a
  death-loop guard belongs in whichever version first presses anything. Not scoped.
- ⬜ CPU as a second discriminator. Per-tree CPU separates 240x and is mode-independent, but it is
  blocked on attributing a process to a session: activity alignment is never WRONG (0 of 4
  gradings) and its coverage flips run to run. The transcript-only detector needs none of this,
  which is why the module shipped without it.

The idea: the companion notices a coding agent (Claude Code, Codex) sitting blocked on a permission
prompt, says so, and optionally answers it. The pet framing is presence — noticing your agent has
been stuck for nine minutes is the part a dashboard cannot do.

**What the research settled:**

- **No VS Code extension needed, and no per-IDE work for the notify half.** Both agents write
  append-only JSONL transcripts pairing a tool call with its result by id, so "blocked" is readable
  from a file. It is agent-keyed, not IDE-keyed: the same transcript appears whether the agent runs
  in VS Code, a JetBrains terminal, Antigravity or a bare shell. That killed the two-artifact design
  and with it the Antigravity marketplace and JetBrains plugin questions.
- **A stall threshold alone is NOT a detector.** Measured over 27,967 paired calls: prompts median
  86.2s vs 1.6s for ordinary completions, but a 20s threshold still yields ~450 false alarms per
  real prompt, and half the prompts are answered in under 20s anyway.
- **The permission-rule join works in default mode and is useless in auto mode.** ~19% precision in
  `default` (≈4 false per real, usable alongside a threshold) against 0.09% in `auto`, where a
  model-side classifier sits in front of the rules and approves nearly everything. **AgentFlow is
  therefore a default-mode feature**, and in auto mode it should say so and stand down rather than
  fire constantly — the MAX-card idiom of refusing and explaining.
- **Answering a prompt needs a classifier, and it is the safety mechanism, not a nicety.** The agent
  bundle ships ten distinct `Yes*` strings; only three mean "approve this one call". The rest grant
  a session, write a permanent rule, or change the permission mode — one sets auto mode as the
  user's persistent default. A prefix match would eventually press that. Built, 25/25 self-test,
  audit clean against the installed bundle, 5/5 mutations fired.
- **Four public tools already do the answering half. All four press a wider grant.** Reviewed at
  source 2026-09-16 because every README understates what the code does; the comparison table and
  per-tool detail are in the doc. `Munkhin/auto-accept-agent` clicks on substring match including
  `always allow`. `sudoghut/llm-auto-confirm` targets Claude Code, advertises `response: "1"`, then
  discards it and sends a **bare Enter** on whichever row the TUI cursor rests on.
  `nextcortex/antigravity-auto-accept` fires eight accept commands every 800 ms with no detection
  at all. `nockasdd/domyh-auto-accept` is genuinely well built (layered, tested, five IDE adapters,
  anchored regex matching) and uses that precision to rank `AcceptAll` as priority 1. Four authors,
  four architectures, same destination: it is where the category goes, not a gap better
  engineering closes. Strongest external support the allowlist design has.
- **Nobody else needs a detector, and that says what the notify half is worth.** Three of the four
  simply poll and fire, because an accept command that no-ops when nothing is pending is safe to
  attempt against nothing. The 450:1 false-alarm rate that kills a stall threshold is a cost of
  **notifying a human**, which none of them do. The detector is a requirement of the notify
  feature, not the answering feature, so notify is the hard half, the differentiated half, and it
  should not wait on answering.
- **Actuation splits four ways, not two.** Terminal Shell Integration, a host-published VS Code
  command, CDP into the renderer, and Windows UI Automation. `domyh`'s `VSCodeCopilotAdapter`
  confirms CDP reaches **stock VS Code** webview targets, so fork kinship transfers the transport.
  It does not transfer the vocabulary: that adapter's commands are all `github.copilot.*`, and its
  selectors still carry an Antigravity Tailwind class. Every pattern is ours to write.
- **Take the death-loop guard.** `domyh`'s is a sliding window of retry timestamps with a pause and
  cooldown. Anything that presses Retry eventually presses it against a recurring error. Take the
  idea, not the code: its cooldown clears all state and auto-resumes, so a persistent loop cycles
  rather than stopping.

**ABI consequence if the input half is ever built, revised 2026-09-16.** The prior-art review found
four actuation channels and **not one of them needs synthetic input**: Terminal Shell Integration
(`terminal.sendText()`), a host-published VS Code command (`executeCommand`), CDP into the renderer
(reaches stock VS Code webview targets, at the cost of a debugging port and a launch-flag rewrite),
and Windows UI Automation (`InvokePattern.Invoke()` on a button found by tree walk, which is not
`SendInput`). None requires foregrounding, focus restore or an idle gate, because none touches the
focused window. So `ModulePermissions` very likely needs no `InputSynthesis` for AgentFlow at all,
and the host-owned foregrounding machinery is not on this module's critical path. The
`InputSynthesis` gap is still real and still additive-safe: it is the same one filed under the
tray-app port assessment further down this file, where Blinking LED P/Invokes `SendInput` while
declaring only `Speech | Storage`. Decide the permission per channel, not once for the module.

**Next step:** the `default`-mode sample is n=85 with 6 real prompts, so 19% is promising rather
than measured. Generate real default-mode data (work an hour out of auto mode) and rerun
`agentflow_join.py`. Two known-incomplete threads: the compound-command splitter is naive and caused
most of the recall failure — the sibling `permission-wildcarding` project already has a correct one
to reuse — and process CPU was never tested as a discriminator despite being free and working on
occluded and minimised windows. On the answering side, ship the notify half behind observe-only
first, since it is the half nobody else has and the only one that needs the detector; then treat
each of the four actuation channels as its own decision, with a death-loop guard in whichever
version first presses something.

**DECIDED (T7): the detector is C# inside this module.** The alternative was shipping it inside the
sibling `permission-wildcarding` project — which already reads both agents' history and owns the rule
matcher — with this module as a thin consumer. That project is node/JS, and an MSI desktop app must
not acquire a node runtime dependency in order to read an append-only JSONL file. What is worth taking
from the sibling is the transcript parsing and the compound-command splitter, ported; not the runtime.

---

## Open: full-repo audit, 2026-09-17

Three read-only audits run in parallel after the AgentFlow merge: dead code and calls that go
nowhere, resource leaks and regression risk, and checks that cannot fail. What was cheap and
unambiguous was fixed in the same session and is not listed here. What remains is below, worst
first. Each entry says what input would make the thing fail, because "no such input" is the finding.

**Numbers are not reused, so a gap means an item closed.** Item 15 (stale numbers in documentation)
is gone: all four of its claims are now correct, and the durable half — make the code count rather
than hardcode a literal — is in [`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md). Item 19 was
added after the others and is not in worst-first order; it is the one check in the gate that is
failing today.

### 📌 1. CI cannot see a module-folder skip, and that is the largest blast radius left

`tests/run-gate.ps1:54-59` throws when a module is missing from the build output, and `:105-152`
reads a marker file and greps for a skip. `.github/workflows/build.yml:54-84` does **neither** — it
checks `$process.ExitCode` only. Ten of the eighteen flags skip-PASS in CI if their module folder is
absent, and `build.ps1:229` builds a module only `if (Test-Path $moduleProject)`, so a renamed or
deleted csproj is skipped silently. This is the exact failure `run-gate.ps1`'s own preamble was
written to close: closed locally, still open in the one place that gates every pull request.

CI also never runs the ShimejiConvert `verify` + `selftest` that the local gate does, so a
regression in the shipped Companions corpus or in the converter is invisible on every PR.

**Fix:** port the presence loop and the marker/skip read into `build.yml`. Not done here because it
changes the file that decides whether every PR is green, and that wants its own change.

### 📌 2. Seven self-tests write a marker the gate does not read

`run-gate.ps1`'s flag table maps these to `$null` while the marker demonstrably exists:
`--catalog-selftest` (`RemoteCatalog.cs:545`), `--fullscreen-selftest` (`FullscreenScan.cs:122`),
`--hardening-selftest` (`RuntimeHardeningSelfTest.cs:1063`), `--wpf-options-selftest`
(`WpfOptionsSelfTest.cs:385`), and `--module-selftest=`{`reminder`, `remembrance`, `blinkingled`}
(`ModuleConventionSelfTest.cs:169`). Cost of closing each: one string. `--security-selftest` and
`--traywatcher-selftest` write no marker at all and have no skip path, so they are the only two
legitimately exit-code-only.

`agentflow` is registered WITH its marker, which is the documented shape per
`docs/module-authoring.md`; the others predate that convention.

### 📌 3. `Test-ModulePublishFreshness.ps1` warns where its own comment says it fails

`:307-309` — the comment reads *"Fail loudly rather than silently narrowing: a watch set that
shrinks without saying so is how this check was blind to source-linked files for months"*, and the
code is `Write-Warning`. That sets no exit code, throws nothing, and is never added to `$stale`.
Both callers judge the script by failure only, so the watch set **can** silently narrow — a
`$(Property)` in an `Include`, an out-of-repo link, an unparseable csproj, or a missing referenced
project all degrade coverage and still print `OK <id> is current`. **Input that makes it fail:
none.** Fix: add the degraded notes to `$stale`, or to a third bucket that throws.

### 📌 4. Nothing reconciles `modules/` against `modules.json`, so a new module is unchecked on day one

`Test-ModulePublishFreshness.ps1:175-178` derives its id list from `modules-dist/modules.json` (6
entries). `modules/` holds 8 csproj. So `agentflow` and `testmodule` are outside the freshness check
entirely: their version parity and payload freshness are unchecked, and **any** module added the
same way inherits the blind spot. For `agentflow` that is currently correct-by-intent (it is
deliberately unpublished) but the check cannot tell intent from omission. **Input that makes it
notice: none.**

### 📌 5. Two settings keys are written on every update check and never read back

`AppSettingsStore.cs:193-194` `moduleUpdateOffers` (written `LocalData.cs:656`, from
`StartUp.cs:1691` and `:1703`) and `:205-206` `companionUpdateStaleIds` (written `LocalData.cs:783`,
from `StartUp.cs:1627`). Each has a public getter — `GetModuleUpdateOffers()` at `LocalData.cs:637`,
`GetPetUpdateStaleIds()` at `:764` — with **zero call sites**, while both of their `*LastCheckUtc`
twins are read. Both keys are visibly populated in the user's settings JSON and look like the data
source for an update badge. They are write-only, and the schema-merge machinery carries them for
nothing. Either wire them or drop both the key and the getter.

### 📌 6. `SecureDownload.ResolveContainedFile` is security-shaped dead code

`src/dotNet/SecureDownload.cs:163` — the only one of that type's eleven members with no caller. It
throws *"Catalog destination escapes the data directory."*, so a reviewer reads it and concludes
download destinations are containment-checked. They are not: the live paths are built by hand, e.g.
`CompanionHost.cs:951` writes `Path.Combine(directory, "animations.xml")` after an `IsSafeId` check
only, never through the escape check at `:170-172`. **Wire it or delete it** — a false assurance in
a security helper is worse than no helper.

### 📌 7. Three dead test hooks that assert coverage which does not exist

- `modules/Fortunes/engine/FortuneProvider.cs:2112` `CustomCacheSelfTest()` — no caller.
  `docs/HISTORY-pre-1.0.0.md:10836` records it as *deleted*; it came back with the S3d relocation
  and was never re-wired. Three sources disagree about whether the custom-corpus cache is covered.
- `modules/Fortunes/engine/SmartFortunes.cs:123` `HoldEmbedLockForDiagnostics(...)` — no caller. It
  exists only to let a test wedge `_embedLock` open, so its presence asserts such a test exists.
  None does. Its sibling `EmbedderDisposalCountForDiagnostics` (`:118`) is also unread, which makes
  the `Interlocked` increment at `:644` pure overhead maintaining an unobservable counter.
- `modules/Fortunes/FortunesModule.cs:1190` `WelcomeCorpusCount()` — doc comment says "self-test
  hook"; no self-test calls it. `--fortunes-selftest` asserts the welcome *fires*; nothing asserts
  the 116-line corpus *loaded*, which is the shipped-payload failure class this hook was built for.

### 📌 8. `--desktopwindows-selftest` has no caller anywhere

`src/dotNet/Program.cs:143` → `DesktopWindows.cs:293`. Absent from `run-gate.ps1`, `build.yml`,
`release.yml`, `docs/RELEASE-CHECKLIST.md` and `SMOKETEST.md`. Unlike `--online-selftest`, which is
documented as excluded at `Readme.md:487`, there is no written decision here, so it reads as
covered. The code under test is live in production (`DesktopWindows.Snapshot` is called from
`CompanionHost.cs:254` and feeds the module ABI), and the self-test holds eight pure assertions on
monitor attribution. This is verbatim the failure already recorded for
`--fortunes-smart-progress-selftest`, which *"sat orphaned with no caller at all"* for months.

### 📌 9. `tests/mutate-agentflow.py` has zero inbound references

The only script under `tests/`, `packaging/`, `tools/` or `installer/` with no reference of any kind
— not the gate, not CI, not the release checklist, not this file until now. It is the only thing
that verifies `--module-selftest=agentflow` is not a rubber stamp (18/18 mutations fired when last
run). `tests/runtime-resource-soak.ps1` was once **deleted as "an unreferenced script"** three hours
after CI stopped calling it, leaving the only leak gate unrunnable. Reference it from
`docs/RELEASE-CHECKLIST.md` or from a comment in `run-gate.ps1` before that repeats.

### 📌 10. Four assertions that cannot fail

- `tests/runtime-hardening-selftest.ps1:778` asserts the source does **not** match the English
  phrase `'seed the month WITHOUT checking'`. The only input that fails it is someone re-adding a
  comment with that exact wording; re-introducing the actual defect with any other comment passes.
  This file's own comments warn about this class four times.
- `:918-925` loops over `installer/*.wxs` with no count assertion. There is exactly one `.wxs`
  today, so moving or generating it elsewhere reduces the loop to zero assertions and a pass.
  Contrast `:951`, which does assert a floor — that is the pattern this one is missing.
- `:838-849` seeds `$darkest = 255` and asserts `$darkest -ge 216`. If the sample window is empty
  the loop body never runs and the seed **is** the pass value.
- `src/dotNet/RuntimeHardeningSelfTest.cs:358-360` — `Check("exact sprite pixel budget accepted",
  true)`. The literal `true`. Delete the line above it and this prints PASS forever. Fix: a
  `CheckAccepts` helper mirroring the existing `CheckRejects`.
- `src/dotNet/Plugins/AiBrainModuleSelfTest.cs:188-203` — the empty-combo half of *"valid combo +
  empty combo both return safe handles"* is not asserted at all; `b == null` and `b != null` both
  reach `true`. Its `catch (Exception)` also converts a genuine `NullReferenceException` regression
  inside `RegisterHotkey` into a skip-pass.

### 📌 11. The fullscreen stand-down has three silent no-op exits

`src/dotNet/FormCompanion.cs:1776`, `:1785`, `:1786` — `if (screens.Length == 0) return;`,
`catch { return; }`, and a length-mismatch guard. Each leaves a companion visible over a fullscreen
game and records **nothing**, with `DiagnosticLog` available. *"Anything visible over a fullscreen
game, especially the UFO"* is item 9 on `SMOKETEST.md`'s watchlist, i.e. a bug that reached users.
`runtime-hardening-selftest.ps1:429-433` asserts the enforcement is not latched behind a flag, which
is true and cannot see that the enforcement can be skipped wholesale. This is the standing rule that
a control which can run degraded must SAY so, unapplied in the product rather than in a test.

### 📌 12. Resource leaks, ranked

- **Fortunes mutates its pane collections from a thread-pool thread.**
  `modules/Fortunes/FortunesModule.cs:723-724` and `:760-771` — after
  `.ConfigureAwait(false)`, `CacheMissingPacks` does `_availablePacks.Clear()/.Add()` and
  `_selectedPacks.RemoveWhere()` on collections the UI thread reads and writes from the ListCard.
  Presents as `InvalidOperationException: Collection was modified` in the settings window, or
  silently lost pack rows, if the user touches the packs card while a check or download is in
  flight. Same block calls `IHost.GetSettings` from that worker, which the ABI forbids at
  `PluginApi.cs:462-463`.
- **Remembrance leaks an `MMDevice` per recording.** `AudioRecorder.cs:51-57` hands the device into
  `WasapiLoopbackCapture`/`WasapiCapture` and never disposes it, against the explicit contract in
  `AudioDevices.cs:44-45` (*"The caller owns the returned device and must dispose it"*). One or two
  stranded COM wrappers per start/stop cycle, on a hotkey the user presses repeatedly.
- **Remembrance leaks the whole capture when the WAV writer fails.** `AudioRecorder.cs:69-82`
  constructs the `WaveFileWriter` **before** `_sources.Add(s)`, so a throw (the storage location is
  a free-text field the user types) leaves an opened native capture unreachable by both
  `CleanupCaptures()` and `Dispose()`. The leak is proportional to a user retrying a failing action.
- **Remembrance subscribes `HostShutdown` and never unsubscribes.** `RemembranceModule.cs:98` vs
  `:101-111`. `CompanionHost` then holds a delegate over the module instance, which makes
  `Alc.Unload()` on a **collectible** load context a permanent no-op: the module assembly and its
  NAudio dependencies stay mapped and the DLL stays file-locked, which is why an in-place module
  update cannot replace it. This is verbatim the leak `ReminderModule.cs:155-156` documents and
  guards against. The crash half is currently hidden by ordering luck: `StartUp.Dispose` raises
  `HostShutdown` at `:409` before `moduleHost.Dispose()` at `:410`.
- **`RebuildModuleSubmenu` has no Image disposal.** `src/dotNet/ContextMenus.cs:159-181` clears
  `DropDownItems` without disposing their Images, while the sibling `ModuleTray_Opening:74-81`
  disposes explicitly and says why. Latent only because no module currently sets `IconPng` on a
  CHILD tray item — and the shared convention check requires every tray entry to carry a unique
  icon, which is exactly the habit that will put one there. The soak could not measure it either:
  BUG-004 records that GDI+ `Bitmap` and `Font` are not necessarily counted by `GetGuiResources`.
- **`Reminder._seenPets` only ever grows.** `ReminderModule.cs:40-42`, declared *"deliberately never
  pruned"*. `ResolveSpeaker` filters with `IsCompanionAlive` at speak time but never removes the
  dead entry, and there is no `CompanionRemoved` event, so the bound is spawns-per-session rather
  than companions-on-screen. `modules/AgentFlow/AgentFlowModule.cs:278-290` does the same job
  correctly and is three lines.

### 📌 13. Nothing asserts that a module's `Shutdown` unsubscribes, which is why item 12 shipped

`ModuleConventionSelfTest.Run` calls `loader.ShutdownAll(...)` at `:98` and asserts **nothing** about
it. Unsubscribe assertions exist only in three bespoke host-side self-tests, each with its own
private fake host, so **reminder, remembrance, blinkingled and agentflow have none**. The structural
cause: `ModuleKit.Testing.RecordingHost` raises events but exposes no `…HasSubs` property, so a
module author *cannot* write that assertion with the shipped test double. Adding one property to
`RecordingHost` plus one assertion in `ModuleConventionSelfTest` would cover all six modules and any
third-party one, including those whose self-tests never construct a host.

### 📌 14. CS0414 does not fire for a write-only field assigned from a non-constant

Measured 2026-09-17, and it corrects two earlier explanations including one in a commit message.
`private int _probeNeverRead = 1;` (constant initializer, never read) **does** warn CS0414 in a
module build. `private int _blockedCount;` assigned only as `_blockedCount = blocked;` (a local)
and never read **does not warn**, in a full `--no-incremental` rebuild.

So the earlier claim that modules escape this because `modules/Directory.Build.props` omits
`TreatWarningsAsErrors` was wrong, and so was the claim that an incremental build hid it. The real
answer is worse: **write-only fields of that shape are invisible to the compiler everywhere in this
repo, including `src/` where warnings are errors.** That is where the false comfort is.

Separately and still worth doing: `modules/Directory.Build.props` sets `LangVersion`, `Deterministic`,
`ContinuousIntegrationBuild` and the Release `DebugType`, but not `WarningLevel` or
`TreatWarningsAsErrors`, which `src/Directory.Build.props` does set. All module projects currently
emit 0 warnings on a forced full recompile, so the two properties can be added for free — and the
same props file's own comment explains why the gap existed (*"modules/ sat outside src/ … so no
module project inherited any of the shared settings"*), a reason that was applied to the debug-record
leak and not to the warning settings.

### 📌 16. Optimization, worth measuring rather than assumed

Framed as measurements to take, per the standing rule that a performance claim needs a cold
purpose-built baseline. None of these is a claimed saving.

- `DiagnosticLog.Write` (`:142-171`) does a `FileInfo` stat plus an open/write/close **per line**,
  inside a global lock. `LogCategory.Animation` is documented as per-frame, and
  `runtime-hardening-selftest.ps1:534-540` *enforces* that this runs first on all 57
  `AddDebugInfo` call sites. Worth measuring: a retained writer, cold, with Animation on at
  MAX_SHEEPS.
- `CheckFullScreen` runs a full `EnumWindows` walk **per companion** — the 300ms throttle at
  `FormCompanion.cs:1772` is per-instance, not global — while `StartUp.cs:734` already holds a 2s
  shared cache one call away and its comment already says *"Called by every pet; the first one each
  cycle sets the value and the rest agree with it."* The redundancy is acknowledged, not collapsed.
- `src/Portable/Wpf/CompanionsPaneControl.cs`'s `CheckButton_Click` calls `DiffStale()` at `:495`
  **synchronously on the UI thread**, SHA-256-ing every installed catalog companion, while
  `RefreshCatalogOnOpen` at `:120-130`
  deliberately does it off-thread and says why. And `BuildUpdateCard:712` re-hashes each stale
  companion that `DiffStale` already classified at `:695` — that second hash is pure duplicated I/O
  and is the one genuinely free win here.

### 📌 17. Two ABI-shaped observations

- `ContextChanged` (`PluginApi.cs:648`, raised `CompanionHost.cs:709`) has **zero subscribers** in
  the repo. The one publisher is Reminder; the one consumer, Remembrance, **polls** `ReadContext`
  instead. Not a rule violation — it is raised — but the push half of the channel has never been
  exercised by real code. Decide whether it is for out-of-tree modules (then say so in
  `PluginApi.cs` and exercise the raise once in `ModuleHostSelfTest`) or whether Remembrance should
  subscribe (then the polling is the bug, not the event).
- `modules/AgentFlow/PermissionRules.cs:247` `CacheStats(...)` has a doc comment saying the cache
  bound *"can be asserted rather than assumed"*. Nothing asserts it. The eviction-at-5000 behaviour
  is a wholesale `.Clear()` of both caches and is untested.

### 📌 18. Saving the AgentFlow pane re-arms the one-shot

`modules/AgentFlow/AgentFlowModule.cs` `SavePaneValues` replaces `_budget` with a new
`NotifyBudget` so a changed cooldown takes effect immediately, which discards `_announced`. So
saving the pane lets an already-announced prompt be announced once more. Not a leak; it contradicts
the one-shot invariant the module's own self-test pins. Fix: carry the announced set across, or
mutate the cooldown in place instead of replacing the object.

### 📌 19. The publish-freshness check is red for all six modules, and the recorded reason is only half of it

**The gate has one failing check right now** and it is this one: `Test-ModulePublishFreshness.ps1`
reports fortunes, aibrain, petstudio, reminder, remembrance and blinkingled all behind their source.
`d780812`'s commit message and every note since attribute that entirely to `TrayConventions.cs` being
added to ModuleKit (`8083d52`), which every module bundles as a `ProjectReference`. **That is the
sufficient cause for all six and the SOLE cause for only two.** Measured per module at HEAD, by
running the check and reading its own culprit attribution:

| module | culprit paths → commits |
|---|---|
| fortunes | `modules/Fortunes` → `d780812`; ModuleKit → `8083d52` |
| aibrain | `modules/AiBrain` → `8083d52` **and `79ddfd3`**; ModuleKit → `8083d52` |
| petstudio | `modules/PetStudio` → **`1178331`**; `tools/ShimejiConvert.Engine/Shimeji/ActionClassifier.cs` → **`79ddfd3`**; ModuleKit → `8083d52` |
| reminder | ModuleKit → `8083d52` ONLY |
| remembrance | ModuleKit → `8083d52` ONLY |
| blinkingled | `modules/BlinkingLed` → `d780812`, `8083d52`; ModuleKit → `8083d52` |

**Measured again at `8083d52~1`, i.e. before `TrayConventions.cs` existed: aibrain and petstudio were
ALREADY stale, both from `79ddfd3`** — aibrain through its own module directory, petstudio through the
source-linked `ActionClassifier.cs` in its external watch set. `1178331` and `d780812` landed *after*
`8083d52`, so they are additional drift the red check has been absorbing since, not the original
cause. **A permanently-red check camouflages exactly the real drift it exists to catch**, which is the
whole argument against "accept a red freshness check on master until the next deliberate publish":
the next reader sees one known-and-explained failure and stops reading, and petstudio's classifier
change is invisible inside it.

**Reverting cannot clear it, and this closes off one of the three options that were recorded.** The
check compares **commit EXISTENCE** in `zipCommit..HEAD` (`packaging/Test-ModulePublishFreshness.ps1`,
the `git log --format='%h %s' "$zipCommit..HEAD" -- @watchedPathspecs` call), never bytes. A forward
commit deleting `TrayConventions.cs` is itself a commit touching the watched ModuleKit directory, so
it makes the count worse — two commits where there was one — and leaves aibrain and petstudio red
regardless. Only a history rewrite would clear it. **So the options are republish, or accept it with
this table in hand; revert is not one of them.** Republishing is outward-facing (merging
`modules-dist/` to master IS the publish, and it reaches every existing user via
`raw.githubusercontent`), which is why it has not been done from a work session.

---

## Open: findings from the v1.1.0 wrap-up audit (filed 2026-09-10)

Four parallel read-only audits ran over the tree at the v1.1.0 tag (credentials, PII/employer material,
stale files, readme accuracy). Credentials came back with **zero** findings and the working tree was
clean. One residual remains open; the rest are closed and in
[`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md).

### 📌 Nothing scans a shipped DLL for an embedded build path, so the fix has no regression net

The defect is fixed. `DebugType=embedded` is set in both
`src/DesktopAICompanion.ModuleKit/DesktopAICompanion.ModuleKit.csproj:60` and
`src/DesktopAICompanion.Contracts/DesktopAICompanion.Contracts.csproj:62` (each with a comment warning
against re-adding `IncludeSymbols`, which is what made `dotnet pack` fail NU5017 on the first
attempt), and the six zips were rebuilt and republished. **Re-verified 2026-09-17:** scanning every
DLL inside every `modules-dist/*.zip` for `<drive>:\…\*.pdb` finds **zero** matches in
`ModuleKit.dll` or `Contracts.dll`.

**What is open is the net, not the fix.** `packaging/Test-ModulePublishFreshness.ps1` measures
staleness and integrity, never embedded paths, so nothing would notice the setting being reverted or a
new shipped assembly arriving without it. Two things a scan has to get right, both measured rather
than assumed:

- **It cannot be a bare drive-letter grep.** The same scan over the same zips returns **7 hits from
  vendored third-party DLLs** — `N:\_work\1\…` in `Microsoft.ML.OnnxRuntime.dll`,
  `onnxruntime.dll` and `onnxruntime_providers_shared.dll`, `C:\__w\1\s\…` in
  `Microsoft.Windows.SDK.NET.dll`, `D:\a\_work\1\s\…` in `WinRT.Runtime.dll`. Those are hosted-runner
  paths from other vendors' CI and are normal practice; a check that flags them is a check somebody
  will disable. Scope it to the assemblies this repo builds.
- **The reason this one mattered is narrow and should be written into the check**, or the next reader
  will over-scope it: it named a personal machine rather than a hosted runner. It carried no username
  and no employer string.

---

## Open: instrument the three modules still at zero (filed 2026-09-10)

BUG-002 was undiagnosable for a reason that is not specific to BUG-002, so it is worth its own item.

**AI Brain is DONE and this section no longer asks for anything from it.** All six items of the
original "what to record" list shipped (1, 3 and 6 with the BUG-002/003 fixes on 2026-09-10; 2, 4 and
5 on 2026-09-11), plus the capture-uniformity check, plus the retry-outcome instrumentation that
`CheckRequestOutcomeInstrumentation` in `AiEngineProbe.Security.cs` mutation-tests by driving the real
retry helper and asserting the LINES. What was recorded, the four design calls inside it (log the
TRANSITION not the probe, rethrow rather than swallow, distinguish auto-start-ran from never-running,
exclude cancellation), and the assertion that caught its own author are all in
[`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md). **The design question this section used to
carry is settled: AI lines stay under `Modules`** and rely on the existing per-module mute; a new
`LogCategory` was not justified by the volume these calls produce.

**What is left is three modules at zero.** The plumbing needs nothing built:
`IHost.Log(moduleId, message)` (`PluginApi.cs:552`) routes a module's line into the same rotating
diagnostic log the host writes, under the `Modules` category, with per-module muting already keyed on
the module id (`DiagnosticLog.IsEnabled(category, moduleId)`).

- 📌 **Fortunes, PetStudio and BlinkingLed emit nothing — no `IHost.Log`, no sink.** They should
  follow AI Brain's pattern. Lower value than AI Brain was, and for a real reason rather than a
  shrug: none of the three fails silently by construction, so "it does nothing" from a user of one of
  these already comes with a visible error, where AI Brain's `catch { return null; }` made every
  failure look identical to normal quiet operation.

| module | diagnostic lines it can emit | how it was counted |
|---|---:|---|
| **AiBrain** | **27** | 26 `Log(...)` call sites in `engine/AiBrain.cs` routed through the static `LogSink` that `AiBrainModule.cs:159` wires to `IHost.Log`, plus one direct `host.Log` at `AiBrainModule.cs:1080` |
| Remembrance | 8 | direct `_host.Log` call sites, all in `RemembranceModule.cs` |
| AgentFlow | 7 | 5 through a private `Log` wrapper + 2 through `Explain`, all reaching one `IHost.Log` at `AgentFlowModule.cs:519` |
| Reminder | 4 | direct `_host.Log` call sites, all in `ReminderModule.cs` |
| Fortunes | 0 | no `IHost.Log`, no sink |
| PetStudio | 0 | no `IHost.Log`, no sink |
| BlinkingLed | 0 | no `IHost.Log`, no sink |

**Counted 2026-09-17. If this table is edited again, count the SINK as well as the direct calls.**
The AiBrain row read `1` for a week after the module was fully instrumented, and it contradicted the
paragraph directly above it, because a raw grep for `IHost.Log` call sites in `modules/AiBrain/` finds
**2** — one of which is the sink wiring that carries all 26 engine lines. Two of the four modules that
log anything route their lines through a helper, so a call-site grep is the wrong instrument here.

**Never log:** prompt text, screen-capture content, OCR output, model replies, or API keys. The
diagnostic log is explicitly "no message text" in the Preferences label and `SUPPORT.md` tells users
they can attach it to an issue. AI Brain's version of this rule is now partly ASSERTED rather than
trusted — a probe assertion fails if a window title reaches the prompt string, and the log records
endpoint HOSTS rather than URLs because a base URL can carry a key in its query. Copy that shape, not
just the rule.

---

## Open: follow-ups from the Disposition audition (aibrain 2026-09-11)

The feature shipped — "Show me 5 examples" and "5 about my screen" beside the Disposition dropdown,
verified end to end against a live `gemma3:4b` by driving the real pane through UI Automation. That
record, and how the five predicted constraints were answered, is in
[`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md). Two threads it left open:

### Still open: the pane cannot preview an UNAPPLIED dropdown value

`PaneAction.InvokeAsync` takes no arguments, so a module action sees only saved settings. Every
"preview what I just chose" affordance in any module hits this, and it is the one part of this item that
was worked around rather than solved. The additive fix is a member carrying the pane's pending values,
e.g. `Func<IReadOnlyDictionary<string,string>, Task<string>> InvokeWithPendingAsync`, which the host
already has to hand (it passes the same dictionary to `Save` on Apply).

Per THE HOST CONTRACT that means: additive only, `AssemblyVersion` stays `1.0.0.0`, and the product
version bumps in the SAME commit. Sequencing cost is the real reason this was not done today — the host
release has to ship before aibrain can declare `MinHostVersion` for it, so it is a host release plus a
module publish, not a module publish.

### Nice-to-have, unchanged

Sample the whole catalog rather than one entry, so the dropdown can be chosen by reading voices side by
side. Bigger UI than a `PaneAction`, and it should not gate the simple version that now exists.

---

## Open: threads left by the .NET 10 + plugin re-architecture

The re-architecture itself is finished — the .NET 10 migration, streams S1 to S7, the in-app Modules
catalog, and the Reminder, Remembrance and Blinking LED modules. That record, with the version
numbers as they stood at the time, is in
[`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md). These are the threads it left open.

### Moving a companion still needs new ABI

Reminder 1.7.0 made the companion physically react when a reminder fires with **no host change at all**,
because `IHost.TryPlayAnimation` and `IHost.PlayAnimationAll` already existed. What does not exist:

- **Still genuinely missing (deferred by decision, 2026-08-27):** MOVING a companion ("walk to centre screen").
  That does need new ABI, and it is bigger than it sounds: companion position is driven by animation velocity
  expressions rather than set directly, so a "move to point" verb would fight the engine rather than sit
  beside it. Not attempted.

### Remembrance follow-ups

Diarization (speaker labels) is deliberately deferred to a follow-up.
**Follow-up ideas:** refresh the device dropdowns without an app restart (the ABI builds the schema once at
load, so this one genuinely does need a host change).

The live recording smoke test this module still needs is in
[`docs/BLOCKED.md`](docs/BLOCKED.md): it requires a machine's local console, and a Remote Desktop
session presents no mic or speakers.

### Sound: duck a companion's SFX while a speech bubble is up

Filed under the TTS/voice module, which was dropped on 2026-08-13. That entry proposed ducking a
companion's SFX automatically — the "automatic SFX-ducking above" the bullet below refers to — and
the module going away does not close this, because the base owns `AudioOutput`, so the mute can hook
there.

  - **UX (user request 2026-08-25):** add a user-facing "silence companion sounds" checkbox under Audio, so a companion's
    embedded `<sound>` SFX (e.g. a companion "yelling") don't fire while a speech bubble is up waiting to be read.
    This is a manual toggle alongside the automatic SFX-ducking above — some users simply want the companion quiet
    when it "talks". Wire it when the TTS/voice module lands (the base already owns `AudioOutput`, so the mute
    can hook there). Now relevant because converted shimeji can carry real `<sound>` SFX as of v1.8.0.
    **2026-08-26:** the manual half shipped as the global **companion sounds** toggle in Preferences → Sound; the
    automatic duck-while-a-bubble-is-up idea is the part that remains open.

- ⬜ **Automatic ducking is still not implemented**, and the groundwork for it shipped with the
  v1.6.0 module-audio ABI: that work added per-owner input tracking, and its own entry records why it
  stopped there — it "changes how the app sounds, so it wants its own decision and a setting". The
  full entry is in [`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md).

---

## Open: the MSI upgrade path, and the installed build's About window (unblocked 2026-09-17)

**Was in [`docs/BLOCKED.md`](docs/BLOCKED.md) as T2 / T49; the blocker is gone.** It sat there needing
"a real reinstall of the MSI over a previous install", and both halves of that now exist on this box:
the app **is** installed at **1.1.4** under `%LOCALAPPDATA%\Programs\Desktop AI Companion\`
(`DesktopAICompanion.exe` reports FileVersion `1.1.4.0`), so there is a previous install to upgrade
over, and **WiX 5.0.2 is installed as a global dotnet tool**, so an MSI can be built here. Verified
2026-09-17. Two verification gaps, both actionable now:

- 📌 **The UPGRADE path has never been exercised.** The v1.1.4 install was onto a machine with no
  registered install, so it tested first-install only, and `SMOKETEST.md` is explicit that the
  upgrade path is the one users take. Section K of [`SMOKETEST.md`](SMOKETEST.md) is the script.
  Install-over-1.1.4 is the case: upgrade code honoured, no second entry in Programs and Features,
  settings and installed companions surviving, the tray icon appearing on the FIRST add (BUG-001's
  own repro), and modules left by the earlier install still loading at their older versions.
- 📌 **The full A-E walk has never been walked on an installed build.** It covers speech routing, the
  poke ladder, drag, multi-monitor pinning and fullscreen stand-down, none of which was touched at
  the v1.1.4 tag. This is the same gap the live-smoke-test item below records; the upgrade install is
  the natural occasion to do both in one sitting.
- 📌 **The About / Help window has only ever been eyeballed as a rendered PNG.** The WPF rebuild was
  verified by rendering the window to an image, not by opening it from the tray on an installed
  build, and the capture followed this box's dark OS setting, so the light-theme variant is
  unobserved. Worth a glance on the next reinstall.

Both original entries in full, with the pre-tag verification that WAS performed beside the gap:
[`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md).

---

## Post-v1 backlog (added 2026-07-29)

### Open, found 2026-09-01 while chasing companion behaviour

- 📌 **The live smoke test has never been walked, across TEN releases (v1.9.4 → v1.9.13).** Everything
  shipped in that span rests on the gate, the behaviour soaks and the mutation suites — none of which opens a
  window and looks at it.
  **This is no longer theoretical.** Four of those ten releases exist only because the USER ran the app and
  saw something: a UFO over a fullscreen game, a companion on the wrong monitor, a companion walking in place. Every one
  was a first-thirty-seconds-of-looking bug that the whole automated suite passed straight over. The gate
  proves the code does what it says; nothing yet proves the code says the right thing.
  **Written out properly on 2026-09-02 as [`SMOKETEST.md`](SMOKETEST.md)** — lettered sections with a
  12-minute Core pass, and a regression watchlist mapping each bug that reached users to the row that would
  have caught it. Read the counts off that file rather than from here; the figures that used to sit in this
  sentence were both stale within two weeks of being written.
  The ten-row table in `docs/RELEASE-CHECKLIST.md` that it replaces had not grown with the
  product since before companions could climb — part of why walking it never felt worth the time. Handed to the
  maintainer the same day; **still unwalked until a report comes back.**

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

### Shimeji conversion: the open remainder

Phases 0 and A to E all shipped in 2026-08, and every original estimate is kept beside its correction
in [`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md) — read that before re-scoring anything
here, because four of the five estimates were wrong in a way that is informative.
`ChaseMouse` / `ChaseMouse2` was the only item on that list never built and is now in
[`docs/BLOCKED.md`](docs/BLOCKED.md), because what it needs first is a judgement call rather than
code. What remains open:

- **Not a gap:** "moves the user's windows" (48 actions) is refused deliberately — desktopPet "cannot and
  should not move the user's windows". No work. Kept here so it is not re-scoped as missing coverage;
  the decision itself is in [`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md).

- ⬜ **CONVENTION: every tray entry carries its own unique icon — the check now exists in ModuleKit, and
  the one edit that would enforce it everywhere is in the HOST, not in five modules.** The tray is shared
  by the host and six modules, so an icon-less row reads as a rendering bug beside its neighbours and two
  rows with the same glyph look like duplicates. 32x32 ARGB PNG, shipped as an `EmbeddedResource`, read
  with `EmbeddedResources.LoadBytes`; recorded in the module template.
  **Done 2026-09-17:** the check is `ModuleKit.Testing.TrayConventions`, lifted out of `BlinkingLedModule`
  (which was the only module asserting it) and improved on the way — it returns WHICH row broke the
  convention, because a bare `false` in a six-module menu does not say which one. `CheckTrayIcons(probe,
  items)` is a one-line call. BlinkingLed uses it; both failure branches are asserted from `AiEngineProbe`
  on synthetic entries, since a real module reaches at most one of them.
  **The finding that changes the remaining work:** do NOT wire this into the other five module self-tests
  one at a time. `ModuleConventionSelfTest` already loads every module through the real loader and calls
  `Init` with its own `ConventionHost`, so ONE call there covers all six modules and every future
  third-party one, including modules whose self-test does not construct a host (Reminder and Remembrance
  build their tray items inline inside `Init`, so a per-module assertion would have to stand up a host
  first). That makes this a host change rather than five module changes — cheaper, and it is the only
  version that holds for a module this repo does not own.
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

### Module SDK follow-ups

- 📌 **Third-party module ecosystem (Phase B).** Signing + per-publisher consent, a signed third-party index
  (or a curated links page first), and NuGet-publishing Contracts/ModuleKit/the template so a module can live
  outside this repo. Designed but deliberately unbuilt — see `docs/module-ecosystem-roadmap.md`, which also
  records the open questions and argues the cheap steps first.

*(Everything else from this section is closed and in
[`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md), including the committed module-window
leak soak and the `WeakReference` trap that cost the most time in building it.)*

### Bugs & maintenance

- 📌 **`--module-selftest=<id>` picks the FIRST `bool SelfTest(out string)` in the assembly, which may not be
  the module's own.** `ModuleConventionSelfTest.RunModuleSelfTest` reflects over every type and breaks on the
  first match, including non-public ones. Reminder had six pure helpers each exposing exactly that signature,
  so any of them could have won over `ReminderModule.SelfTest` — non-deterministically, by metadata order.
  Worked around module-side by renaming those six to `SelfCheck` (2026-08-27), but the sharp edge is still
  in the host and will catch the next module author, including third parties. Fix shape: prefer the type
  implementing `IModule`, then fall back to the scan. Host change, so it wants a release to be worth much.

- 📌 **Module projects are not held to warnings-as-errors, unlike host projects — and the measurement
  says turning it on is free.** `src/Directory.Build.props:19-20` sets `WarningLevel 4` +
  `TreatWarningsAsErrors true`; `modules/Directory.Build.props` sets `LangVersion`, `Deterministic`,
  `ContinuousIntegrationBuild` and the Release `DebugType none`, and NEITHER warning property. So every
  module compiles under a looser bar than the host, and the seven published modules have never been held
  to the standard the rest of the tree is.
  **Measured 2026-09-17, because enabling it blind could redden seven modules at once:** a forced full
  recompile (`--no-incremental`, `-p:WarningLevel=4`) of all seven module projects emits **0 warnings**.
  AiBrain, BlinkingLed, Fortunes, PetStudio, Remembrance, Reminder, TestModule: zero each. Adding the two
  properties would therefore break nothing today. NOT measured: `modules/AgentFlow`, which does not exist
  on this branch.
  **The diagnosis this measurement corrects, and it matters more than the item:** a module build DOES
  report this class of warning. An injected `private int _probeNeverRead = 1;` in `AiSettings` produced
  `warning CS0414 ... 1 Warning(s)` from a plain `dotnet build`, and `error CS0414 ... 1 Error(s)` with
  `-warnaserror`. So a module build reporting `0 Warning(s)` after a code change is almost always an
  **up-to-date incremental build that never ran the compiler** — the same defect class as a log line that
  cannot fail. Pass `--no-incremental` before believing a warning count, in this repo or any other.

- 📌 **Three `--module-selftest=<id>` entries in `tests/run-gate.ps1` map to `$null`, so they cannot
  fail when the module is absent.** `reminder`, `remembrance` and `blinkingled` are registered at
  `run-gate.ps1:88-90` with no marker file. The marker is the half of that table that catches a skip: the gate
  fails on a missing marker and fails on a `^SKIP:` line (`run-gate.ps1:123-135`). With `$null` it checks the
  exit code only — and `ModuleConventionSelfTest` returns **true** for a missing module folder
  (`src/dotNet/Plugins/ModuleConventionSelfTest.cs:61-64`: `SKIP: no bundled module at ...` then
  `Finish(..., true)`), so a module that never loaded scores identically to one that passed. The marker file is
  already written on that path, and `docs/module-authoring.md:248` tells a new module author to register
  `dp-module-<id>-selftest.txt`, so the documented shape is the marker and these three are the exception to
  it. The fix is three table values, not code. **This is the failure mode `run-gate.ps1`'s own preamble was
  written about, sitting in `run-gate.ps1`.** Filed rather than fixed: that file is owned by the AgentFlow
  track, which registered the marker for its own `agentflow` entry and deliberately left these three so a
  gate edit and a module change do not land in one commit.

- 📌 **`release.yml` still runs `microsoft/setup-msbuild`, which is vestigial.** `build.ps1:48-54` states it no
  longer probes MSBuild/VS, and the MSI is built by the `wix` dotnet tool, so nothing consumes it. Left in
  deliberately rather than removed in the same change: it costs seconds, and the release path is the wrong
  place to find out you were wrong about an implicit dependency. Drop it the next time the release workflow is
  touched for another reason.


*(The closed entries from this section — and there are many, including four separate cases of an
absence check defeated by a comment describing the very thing it forbids — are in
[`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md).)*

### Feature ideas (queued, not yet scoped)

Items **1 to 15 are closed** and live in
[`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md) with their numbers intact, because
`handoff.md` and the pre-1.0.0 history cite several of them by number. The numbering continues here
rather than restarting, for the same reason.

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

17. 📌 **SUPERSEDED by the Remembrance module — all three phases shipped, one gap left.** The want was: click a tray
    item, record the mic **and** system/loopback audio, click again to stop, then transcribe and
    summarize. Checked at source 2026-09-17 rather than assumed, because this entry was wrong about
    its own residuals once already:
    - **P1, trigger + capture.** `modules/Remembrance/AudioRecorder.cs` records both directions
      (`WasapiLoopbackCapture` at `:52`, `WasapiCapture` at `:57`) with per-direction device pickers.
      `RemembranceModule.cs:700` contributes the one-click tray entry, `Click = ToggleRecording`, whose
      `DynamicText` reads "Start recording a meeting" and then "● Recording: … (click to stop)" — that
      is both the click-to-stop trigger and the recording-in-progress indicator this entry asked for.
      A hotkey exists as well, and `ModulePermissions` gained the honest flags: `Microphone` and
      `SystemAudio`, declared by `remembrance` in `modules.json`.
    - **P2, transcription.** Local Whisper via `Transcriber.cs` + `WhisperInstaller.cs`. The download
      and the whisper-cli run are verified live on the dev box — see
      [`docs/BLOCKED.md`](docs/BLOCKED.md), which records that independently of this entry.
    - **P3, summary.** `OllamaSummarizer.SummarizeAsync` at `:246`, writing the `.summary.txt` path
      `CaptureStore.cs:48` builds. The map-reduce is verified live too.

    **ONE gap remains against the original wording**, deliberate in Remembrance and real only if the
    goal is *listening* rather than transcribing: the output is **two WAV files downmixed to mono
    16 kHz** (`.mic.wav` and `.system.wav`, tuned for Whisper — the `StereoToMonoSampleProvider` +
    `WdlResamplingSampleProvider(…, 16000)` chain at `AudioRecorder.cs:128-132`), not one MP3 at
    listenable quality. **Reduced scope if wanted: an output-format choice on Remembrance, not a new
    module.**

    ⚠ **The second residual this entry used to name was already closed and the entry had not
    noticed** — it said "a hotkey rather than a one-click tray entry" while
    `BuildRecordTrayItem` had shipped. Measured 2026-09-17. A superseded entry keeps rotting after the
    thing that superseded it moves on, which is the argument for deleting rather than annotating.

    *(The design constraints this entry accumulated are settled and now live in
    [`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md): module→module calls do not exist and nothing
    should be designed assuming them, and the browser Web Speech API path was rejected on three
    independent counts. Local-only — no cloud STT or summary path, ever — is a shipped property of
    Remembrance, not a queued requirement.)*

18. **Consolidate standalone tray utilities into companion modules — one candidate left** (2026-08-20,
    not scoped). The companion is an always-on tray host with a plugin ABI, so it is a natural home
    for the small single-purpose tray apps in this account. Three were assessed against the module
    model (in-proc .NET 10 C# `IModule` in its own ALC, talking only to `IHost`; user surface = tray
    items + declarative pane, no self-shipped WinForms/WPF). **Two of the three are resolved:**
    blinkingLED was ported and ships as `modules/BlinkingLed/`, gated by
    `--module-selftest=blinkingled` in both `tests/run-gate.ps1` and `.github/workflows/build.yml`;
    LightHost was refused and its reasoning is in
    [`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md).

    - **IdleLauncherTray (`bigfnj/IdleLauncherTray`) — port-with-work, but licensing gates it, so the
      item itself is in [`docs/BLOCKED.md`](docs/BLOCKED.md) (T58).** The technical assessment is kept
      here because BLOCKED.md points at it: the idle engine (`PhysicalIdle`: global
      `WH_KEYBOARD_LL`/`WH_MOUSE_LL` hooks reading the `LLKHF_INJECTED` flag to tell physical input
      from `SendKeys`/automation, `GetTickCount64` monotonic timing, XInput gamepad poll,
      `GetLastInputInfo` fail-safe) is dependency-free P/Invoke and drops straight into a module
      timer; config → pane, target-file chooser → `IHost.PickFilesToOpen` (host owns the dialog).
      **Biggest technical care: the low-level hook is global but injection-free, so an ALC-loaded lib
      CAN install it on the host UI thread — but it MUST be `UnhookWindowsHookEx`'d in `Shutdown()`
      or an ALC unload leaks a dangling hook.** Companion framing: the sheep sleeps after N
      genuine-idle minutes (not fooled by anti-idle jiggles), launches your target on wake/poke, and
      locks the PC when it closes.

    **The cross-cutting finding, half of it now closed:**

    - 📌 **`ModulePermissions` still cannot disclose synthetic input, input monitoring or process
      launch.** The audio half of this finding is DONE — `Microphone = 1 << 9` and
      `SystemAudio = 1 << 10` were added for Remembrance, splitting what this entry called
      `AudioCapture` into the two things a user actually consents to separately, and
      `AgentTranscripts = 1 << 11` followed the same pattern for AgentFlow. What is left is real and
      live in a SHIPPED module: `BlinkingLedModule.cs:59` declares
      `ModulePermissions.Speech | ModulePermissions.Storage` while
      `engine/ScrollLockBlinker.cs` P/Invokes `SendInput`, and `:57` says so in a comment — *"There is
      no ModulePermissions flag for synthesizing input, so the consent screen cannot state it."* So
      the consent screen under-discloses today, in the one place it can be checked. Add
      `InputSynthesis`, and `InputMonitoring` / `LaunchProcess` if IdleLauncherTray is ever
      unblocked. Additive to the enum, so safe; per the enum's own comment these are DISCLOSURE flags
      rather than gates, and `docs/module-ecosystem-roadmap.md` settles why containment would be
      security theatre. **Decide the flag per channel, not once for a module** — the AgentFlow
      section at the top of this file reaches the same conclusion from the other direction.
    - **Licensing is a recurring gate**, and it is what is left of this item: IdleLauncherTray is
      GPLv2 against an MIT companion, and it is bigfnj-owned so the relicense is the maintainer's to
      do. blinkingLED was unlicensed and got MIT before bundling. LightHost is GPLv3 from JUCE, which
      is *not* ours to relicense — hence the refusal rather than a deferral.

    **Reusable port recipe (for any same-stack tray app), validated by the blinkingLED port:** keep
    the dependency-free engine, discard the WinForms shell
    (`Program`/`Main`/single-instance/`NotifyIcon`/`OpenFileDialog`/`MessageBox`/custom Forms),
    rebuild the surface as tray items + a declarative pane, and be disciplined about tearing down
    OS-global state on ALC unload (hooks, Scroll-Lock state, audio devices).

