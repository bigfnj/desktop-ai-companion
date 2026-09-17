# Desktop AI Companion — Backlog

> Fork of Adrianotiger/desktopPet. The original physics experience is preserved, while compatibility,
> correctness, validation, and security fixes do modify engine files where required.

## How this file works

**Open items only.** A closed item is deleted from here rather than marked done — git holds the
history, and 2,560 lines of finished work is what stopped this file being readable as a backlog.
Anything closed that still carries standing value was extracted rather than deleted:

| file | what it holds |
|---|---|
| [`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md) | the completed-work record from v1.0.0 on, with the estimates that turned out wrong kept beside their corrections |
| [`docs/ISSUES-post-1.0.0.md`](docs/ISSUES-post-1.0.0.md) | the BUG-001 to BUG-004 post-mortems, kept in full because two of them were wrong in instructive ways |
| [`docs/BLOCKED.md`](docs/BLOCKED.md) | items that cannot be actioned from here, each with its blocker named on its own line |
| [`docs/HISTORY-pre-1.0.0.md`](docs/HISTORY-pre-1.0.0.md), [`docs/ISSUES-pre-1.0.0.md`](docs/ISSUES-pre-1.0.0.md) | the same two records for the repository that preceded v1.0.0 |

**Conventions.**

- **Bugs are numbered `BUG-00N` and the number is never reused**, so a commit, a test or a code
  comment can cite one. `modules/AiBrain/`, `modules/PetStudio/`, `src/dotNet/`,
  `docs/RELEASE-CHECKLIST.md` and `handoff.md` all cite them today. The next one filed is BUG-005.
- **Status is prose plus a glyph, never a checkbox:** ✅ done · 📌 open, with the reasoning recorded ·
  ⬜ not started · ⚠ a caveat, or a claim that has not been observed. There are no markdown
  checkboxes anywhere in this file, and adding one would lose the reasoning a glyph sits next to.
- **An entry keeps the measurement that settled it.** Where a number decided something, the number
  stays in the entry — otherwise the next reader re-derives it, or guesses.
- **Before writing "needs a host change" here again, grep `PluginApi.cs` for the verb.** That
  sentence cost a planning cycle: the Reminder entry asserted the ABI could not drive a companion
  animation or move a companion, while `IHost.TryPlayAnimation` and `IHost.PlayAnimationAll` had
  existed since the emotion work and AiBrain had been using them all along. The full case is in
  [`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md).
- **A refused design is recorded, not forgotten.** Read "Settled decisions" at the foot of this file
  before proposing something that looks obviously missing.

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

- `modules/Fortunes/engine/FortuneProvider.cs:2107` `CustomCacheSelfTest()` — no caller.
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

### 📌 15. Stale numbers in documentation

| claim | where | reality |
|---|---|---|
| "61 source invariants" | `SMOKETEST.md:4` | 82 static `Assert-True` sites; ~118 PASS lines at runtime, 11 sites being inside loops |
| "All fifteen projects target net10.0-windows" | `Readme.md:479` | 16 csproj outside `bin`/`obj`/`build` |
| "all seven module projects" | `Readme.md:498` | 8 csproj under `modules/`, and `build.ps1` builds 8 |
| "seven module publishes" | `docs/HISTORY-post-1.0.0.md` (v1.1.4 entry) | the table under it has 6 rows; `modules.json` lists 6 |

`tests/DesktopAICompanion.CoreTests/Program.cs:87` is the model to copy: it **counts** its groups
rather than hardcoding them, with the drift that motivated it recorded in the comment. A number
nobody re-measures goes stale, which is why the two corrected in this session were both wrong.

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
- `CompanionsPaneControl.CheckButton_Click:495` calls `DiffStale()` **synchronously on the UI
  thread**, SHA-256-ing every installed catalog companion, while the on-open path at `:116-128`
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
- `modules/AgentFlow/PermissionRules.cs:234` `CacheStats(...)` has a doc comment saying the cache
  bound *"can be asserted rather than assumed"*. Nothing asserts it. The eviction-at-5000 behaviour
  is a wholesale `.Clear()` of both caches and is untested.

### 📌 18. Saving the AgentFlow pane re-arms the one-shot

`modules/AgentFlow/AgentFlowModule.cs` `SavePaneValues` replaces `_budget` with a new
`NotifyBudget` so a changed cooldown takes effect immediately, which discards `_announced`. So
saving the pane lets an already-announced prompt be announced once more. Not a leak; it contradicts
the one-shot invariant the module's own self-test pins. Fix: carry the announced set across, or
mutate the cooldown in place instead of replacing the object.

---

## 🐞 Known bugs (post-1.0.0)

**None open.** All four were fixed before the v1.1.0 tag (2026-09-10). The full post-mortems —
diagnosis, the wrong turns, the fix, and how each was verified — are in
[`docs/ISSUES-post-1.0.0.md`](docs/ISSUES-post-1.0.0.md).

| bug | | fixed |
|---|---|---|
| BUG-001 | the tray icon is missing after an MSI install that launches the app | host 1.1.1 → 1.1.3, across all four of its symptoms |
| BUG-002 | the vision feature does nothing, silently, when the configured model is not installed | 2026-09-10, all four items |
| BUG-003 | screen capture returns the wallpaper, and follows the wrong monitor | (b) fixed 2026-09-10; (a) resolved — the suspected cause was refuted by measurement |
| BUG-004 | the leak soak's verdict was a coin flip, and it is the only gate that can catch a leak | 2026-09-10 — there is no leak, and the gate now measures that |

Three of the four were found by the maintainer running a real install, and none by a gate. That is why
`SMOKETEST.md` exists and why the A-E walk is tracked in [`docs/BLOCKED.md`](docs/BLOCKED.md) rather
than dropped.

---

## Open: findings from the v1.1.0 wrap-up audit (filed 2026-09-10)

Four parallel read-only audits ran over the tree at the v1.1.0 tag (credentials, PII/employer material,
stale files, readme accuracy). Credentials came back with **zero** findings and the working tree was
clean. What was found and FIXED at the time is not repeated here; these are the items deliberately left
open, with the evidence, so none of them has to be rediscovered.

### 1. `ModuleKit.dll` still embeds the build path in every published module zip

`DesktopAICompanion.ModuleKit.dll` is copied into all six `modules-dist/*.zip`, and because it builds
from `src/` it still carries a CodeView record naming
`D:\...\src\DesktopAICompanion.ModuleKit\obj\Release\...pdb`. The six MODULE DLLs were
fixed by `modules/Directory.Build.props` (`DebugType=none`); this one cannot use the same fix, because
Contracts and ModuleKit deliberately set `IncludeSymbols` + `SymbolPackageFormat=snupkg` so module
authors can debug into the ABI.

Measured while attempting it, so nobody repeats the dead ends:

- **`PathMap` does not help.** It rewrites source paths recorded *inside* the PDB, not the output PDB
  path in the assembly's CodeView record. Verified: the absolute path survived unchanged.
- **`DebugType=embedded` DOES clean it and keeps symbols** (they move inside the DLL, +14 KB on
  ModuleKit), but `dotnet pack` then fails **NU5017**, "cannot create a package that has no dependencies
  nor content", because the symbol package has nothing left to carry.
- **The release does not publish `.snupkg` assets at all** - the v1.1.0 assets are two `.nupkg`, the
  portable ZIP, the MSI and `SHA256SUMS.txt`. So that setting currently produces an artifact no user
  receives, which is the thing to settle first: either publish the symbol packages, or switch to
  `embedded` and drop `IncludeSymbols`.
- Calibration before treating this as urgent: the vendored third-party DLLs in those same zips carry
  their own vendors' CI paths (`N:\_work\...` for onnxruntime, `C:\__w\1\s\...` for the
  Windows SDK projection, `D:\a\_work\...` for WinRT.Runtime). Embedded build paths in shipped
  binaries are normal practice; this one matters only because it names a personal machine rather than a
  hosted runner. It contains no username and no employer string.

**No gate looks for this.** `Test-ModulePublishFreshness.ps1` measures staleness and integrity, never
embedded paths. A scan over the zips' DLLs for `<drive>:\...\*.pdb` would be cheap to add and would
have caught it.

### 2. Nothing asserts that the two copies of `animations.xsd` stay in sync

- `Resources/animations.xsd` and `src/Resources/animations.xsd` are byte-identical duplicates and BOTH
  are live: the `src/` copy is embedded by three csproj files, the root copy is what the grimoire docs
  link. `handoff.md` already records that they must stay in sync, but **nothing asserts it**. A file-hash
  equality check in `tests/run-gate.ps1` is cheap now and prevents a silent drift later.

---

## PARTLY DONE: instrument the modules, AI Brain first (filed 2026-09-10)

BUG-002 was undiagnosable for a reason that is not specific to BUG-002, so it is worth its own item.

> **2026-09-10, done as part of the BUG-002/003 fixes:** items 1, 3 and 6 below, plus the
> capture-uniformity check. `AiBrain` gained a static `LogSink` wired to `IHost.Log`, and the AiBrain
> row of the table below is no longer 1. **The open design question is settled: AI lines stay under
> `Modules`** and rely on the existing per-module mute, as this item predicted would suffice — a new
> `LogCategory` was not justified by the volume these calls produce.
>
> **2026-09-11: items 2, 4 and 5 are now DONE too, so AI BRAIN is fully instrumented.** Fortunes,
> PetStudio and BlinkingLed are still at zero `IHost.Log` calls and remain open, which is why this
> section is still PARTLY DONE rather than closed. None of the three fails silently by construction,
> so they stay lower value.
>
> - **Item 2, availability transitions.** `NoteBackendAvailability` + `CheckBackendAvailableAsync` log on
>   the TRANSITION only, never per probe, because the ask path checks before every turn and logging the
>   state would write a line each idle tick. The first check logs (null to known is a transition) since
>   the launch answer is what a "it never speaks" report most needs. `CheckBackendAvailableAsync`
>   deliberately RETHROWS instead of returning false: both callers already have handlers, and swallowing
>   would take the exception away from them. `PrepareAsync`'s bare `catch { return false; }` now records
>   the category first, and the `EnsureServerAsync` branch is distinguished in the reason, because
>   "auto-start ran and it is still absent" is a different user problem from "it was never running".
> - **Item 4, request outcome.** Latency, attempt count and reply length on success; the retried-and-gave-up
>   case the item called invisible now logs both error categories. A DETERMINISTIC failure skips the retry
>   filter entirely and used to produce no outcome line at all, so it gained its own. Cancellation is
>   excluded explicitly, because `IsRetryable` returns false once the token is cancelled and an ordinary
>   cancel would otherwise be recorded as a failure it is not. `reply parse:` distinguishes empty-reply
>   from unusable-shape from ok, which is the Readme's "permanently, silently mute" captioner case.
> - **Item 5, the vision path.** `ocr engine:` records which engine actually ran and whether Tesseract was
>   configured or merely found (a companion reading through Windows OCR while the user believes they
>   installed Tesseract is a silent accuracy downgrade, not an error). `ResolveTesseract`'s throw is
>   captured rather than swallowed. Every one of the tesseract path's five `return ""` exits now says why:
>   process-did-not-start, timeout-8s, output-drain-timeout-2s, exit-N, or an error category. `vision
>   payload:` records cap width, shot size and PNG KB, so a disappointing remark can be checked against
>   the Readme's width/accuracy table.
>
> **Proven, not assumed.** `CheckRequestOutcomeInstrumentation` in `AiEngineProbe.Security.cs` drives the
> real retry helper with the real classifier and asserts the LINES. Mutation-tested by deleting the
> retried-and-failed `Log` call: exactly the two retry-log assertions failed, the behavioural assertions
> ("was attempted twice") still passed, and the line-count assertion independently caught the drop from
> three lines to two. That count assertion also caught its own author: it was first written `>= 4` and the
> true count is 3, because the cancellation case contributes nothing by design.
>
> The "never log" list was honoured and is now partly ASSERTED rather than trusted: a probe assertion
> fails if a window title reaches the prompt string, and the log records endpoint HOSTS rather than URLs
> because a base URL can carry a key in its query.

**The plumbing already exists and is barely used.** `IHost.Log(moduleId, message)`
(`PluginApi.cs:552`) routes a module's line into the same rotating diagnostic log the host writes,
under the `Modules` category, with per-module muting already keyed on the module id
(`DiagnosticLog.IsEnabled(category, moduleId)`). Nothing needs building for a module to be
diagnosable. Current usage:

| module | `IHost.Log` calls |
|---|---:|
| Remembrance | 8 |
| Reminder | 4 |
| **AiBrain** | **1** |
| Fortunes | 0 |
| PetStudio | 0 |
| BlinkingLed | 0 |

**AI Brain first, because it is the module that fails by design.** `AskAboutScreenAsync` ends in a
bare `catch { return null; }` and the caller treats null as "nothing to say", so every failure mode
looks identical to normal quiet operation. What to record, in rough value order:

1. **The swallowed exception itself** — model, endpoint host, and error category, at the catch in
   `AiBrain.cs:257-262`. This is the whole of BUG-002's diagnosability and should land first.
2. **Backend availability transitions** — `IsAvailableAsync` flipping, with the reason. Currently a
   `catch { return false; }`.
3. **Model resolution** — what was configured, what was normalized, and whether the backend actually
   offers it. This is where BUG-002 becomes obvious at a glance rather than after an investigation.
4. **Request outcome** — latency, retry count, whether a retry was attempted, and the parse result.
   `ChatWithRetryAsync` swallows retryable exceptions with `catch (Exception ex) when
   (AiEndpointPolicy.IsRetryable(ex, ct)) { }`, so a request that retried and gave up is invisible.
5. **Vision-specific path** — capture size, whether OCR or vision was used, and whether Tesseract was
   resolved. `ResolveTesseract` is wrapped in `catch { }`.
6. **Capture selection, for BUG-003** — the chosen monitor rect, whether the foreground window or the
   companion's own monitor won it, and a cheap uniformity check on the resulting bitmap. That last one
   turns "it captured the wallpaper" from a user report into a fact. This item now gates two bugs.

**Never log:** prompt text, screen-capture content, OCR output, model replies, or API keys. The
diagnostic log is explicitly "no message text" in the Preferences label and `SUPPORT.md` tells users
they can attach it to an issue. This is the one part of the item that must not be got wrong: the AI
module is the one place where careless logging would export the contents of the user's screen.

**Open design question.** Whether AI lines want their own `LogCategory` (currently App, Companions,
Modules, Tray, Network, Audio, Animation) or stay under `Modules` and rely on the existing per-module
mute. Per-module muting probably suffices, and a category is only worth adding if AI volume would bury
the rest -- in which case follow the `Animation` precedent and default it off. Decide before writing
the calls, not after.

Fortunes, PetStudio and BlinkingLed are at zero and should follow, but none of them fail silently by
construction, so they are lower value.

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

## Post-v1 backlog (added 2026-07-29)

### Open, found 2026-09-01 while chasing companion behaviour

- 📌 **A one-frame animation with `repeat="0"` is effectively invisible — and the fix this entry
  proposed is now ruled out by its own prerequisite measurement.** Hornet's `Grapple3` was the report: a
  single frame with no repeat renders for ONE tick (`TotalSteps` is 1, so `AnimationStep >= lastStep` fires
  on the first one) and cannot be seen. It is reachable and it "plays"; it just never appears, which is
  indistinguishable from a bug to a user and invisible to the reachability check that guards the corpus.
  **Measured 2026-09-17, which is what the entry asked for before choosing:** across all 31 shipping
  converted skins, **54 non-magic single-frame sequences sit under 500ms of on-screen time, across 26
  companions**. Method, so it is reproducible: for every `<animation>` with exactly one `<frame>` in its
  `<sequence>`, take `(1 + repeat) * <start><interval>`, and exclude the four magic names
  (`kill`/`sync`/`fall`/`drag`) — `sync` is legitimately a 100ms no-op and accounts for 25 more on its own.
  **That count refutes the proposed fix.** 25 of the 54 are the emitter's SYNTHETIC `turn` at 120ms, whose
  whole job is to be instantaneous, and most of the remainder are `*Blink`, `*Transit` and `*End` poses that
  are meant to be brief. A blanket minimum dwell would put a visible stall into every converted companion's
  facing change; refusing to emit would break facing outright. `Grapple3` does not appear in the measurement
  at all, so the original example has already been re-emitted away.
  **What is actually left is narrower:** the decision belongs to the pose ROLE, not the frame count. The
  emitter already gives rest poses a dwell; the open question is whether any other role wants one, and
  nothing in the measurement says one does. Do not spend on this without an observed case that is not a
  `turn` or a blink.

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
  no-users-until-10-stars rule, and I should have applied it before recommending a deferral -- the whole
  point of it is that blast-radius arguments are void at 0 stars.** Checked
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

### Shimeji conversion: the open remainder

Phases 0 and A to E all shipped in 2026-08, and every original estimate is kept beside its correction
in [`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md) — read that before re-scoring anything
here, because four of the five estimates were wrong in a way that is informative.
`ChaseMouse` / `ChaseMouse2` was the only item on that list never built and is now in
[`docs/BLOCKED.md`](docs/BLOCKED.md), because what it needs first is a judgement call rather than
code. What remains open:

- **Not a gap:** "moves the user's windows" (48 actions) is refused deliberately — desktopPet "cannot and
  should not move the user's windows". No work.

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


### Known ABI gaps (add when the module that needs them is written — see handoff.md's host contract)

- 📌 **A module cannot draw on or near the companion.** No ABI for overlay/decoration. Nothing planned needs it yet;
  noted so it is not mistaken for an oversight if something does.

- ⬜ **A module cannot push a live value into an open options pane or tray menu.** Found while porting the
  standalone app's "Next blink" countdown, which refreshed every 250ms because that app owned its own menu.
  A module ships DATA and the host renders it, so the best available is a snapshot: `TrayItem.DynamicText`
  is re-evaluated when the menu opens, and `SettingKind.Info` is read when the pane loads or a
  `PaneAction` with `ReloadPaneAfter` runs. Good enough for state that changes slowly, useless for a
  countdown. The readouts were dropped rather than shipped stale. If a live readout is ever wanted this
  needs an ABI addition (a push channel or a pane-refresh tick) and therefore a host release, which was not
  worth it for one diagnostic.

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

---

## Settled decisions — do not re-propose

A refused design that is not written down gets proposed again. These are closed by decision, not by
neglect.

- **No "Browse" button on the model fields.** Asked for, then declined by the maintainer on
  2026-08-11 after reconsidering: *"i think i mis-understood the 'browse' question, if ollama doesnt
  support custom pathing, then why are we adding it?"* Ollama cannot be pointed at an un-imported
  file through chat requests at all — that needs a `Modelfile` plus `ollama create`, a real
  registration step, confirmed against Ollama's own docs — and a bare llama.cpp server's model is
  fixed at launch (`--model <path>`), not swappable per request. An "informational" file picker would
  add a cosmetic, functionally inert control. **Don't re-propose one without this context:**
  `ollama pull` plus the existing "Refresh models" action already cover real usage. The full entry,
  including the label↔id dictionary the VRAM-size prefix forced, is in
  [`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md).
- **The AgentFlow detector is C# inside this module**, not a consumer of the node/JS sibling. Reasoning
  in the AgentFlow section at the top of this file.
- **Also settled, and recorded with the items they belong to:** `totalCount` is DO NOT BUILD — zero
  occurrences across the 31 shipping skins, now stated in `ActionClassifier`'s own reason text and pinned by
  a `ClassifierSelfTest` assertion that no classifier reason may promise unscheduled work; moving the user's
  windows is refused rather than missing ("Shimeji conversion" above);
  LightHost is NOT a fit for a microphone module, being a C++/JUCE effects host with zero capture code
  and GPLv3 besides (feature idea 18); module→module calls do not exist and nothing should be designed
  assuming them (feature idea 17); a browser Web Speech API transcription path was considered and
  rejected on three independent counts (feature idea 17). Third-party module code-signing (stream S7)
  and TTS as a feature were both dropped on 2026-08-13 and are recorded in
  [`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md).
