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
| [`docs/IDEAS.md`](docs/IDEAS.md) | product ideas nobody has scoped, with their reasoning: wanted, unscheduled, and not engineering debt |
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
`build.ps1`, `tests/Invoke-SelfTests.ps1` fails if its folder is missing from the build output
(`$RequiredModules`, read by both the gate and CI), and `--module-selftest=agentflow` runs in both.
`modules-dist/` and `catalog.json` are deliberately untouched, so no existing user is offered it —
the same arrangement `TestModule` has, and `packaging/Test-ModulePublishFreshness.ps1` now names
both in `$deliberatelyUnpublished` so the omission is declared rather than silent.

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
`InputSynthesis` gap is still real and still additive-safe: it is the same one filed under "Module
SDK follow-ups" further down this file, where Blinking LED P/Invokes `SendInput` while declaring
only `Speech | Storage`. Decide the permission per channel, not once for the module.

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

**Numbers are not reused, so a gap means an item closed.** Nineteen were filed; four are still open,
and the four below are them. Everything else was fixed the same day and deleted — each one
re-verified against the tree on 2026-09-17 rather than taken from its commit message. Where a closed
item left knowledge worth consulting it went to
[`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md) rather than into a DONE annotation: item 14's
CS0414 measurement and the `--no-incremental` warning-count trap (under "Measurements that corrected
an explanation"), the reason a ModuleKit addition costs a six-module republish (item 13), item 17's
`ContextChanged` decision, and item 15's make-the-code-count rule. Item 19 was added after the
others and is not in worst-first order; it is the one check in the gate that is failing today.

### 📌 5. Two settings keys are still written on every update check and read by nothing

**The dangerous half is closed.** The two public getters with zero call sites are gone, and
`src/Portable/LocalData.cs:636` and `:763` carry the reason in their place: each read as the data
source for an update badge while the pane and the notification both use the scan's live result, so
anyone debugging "why doesn't the badge show the offer" would have chased them.

**What is left is the keys.** `AppSettingsStore.cs:193` `moduleUpdateOffers` and `:205`
`companionUpdateStaleIds` are still written on every check by `SetModuleUpdateResult` /
`SetPetUpdateResult`, and the only thing that reads either is its own change comparison, so the
value is persisted for no reader. Drop both keys, or state in `AppSettingsStore.cs` why a persisted
value with no reader is deliberate — it is a schema change either way, which is why deleting the
getters was split from it.

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

### 📌 17. `ContextChanged` is raised by the host and exercised by nothing

**The decision this item asked for is made**, and it is recorded under "Settled decisions" in
[`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md): the event is the push half for a reader that
must act ON a change, Remembrance's read-at-use `ReadContext` call is the correct pattern for what
Remembrance does and is not the bug, and `PluginApi.cs` now says so in the channel's own comment.

**The work left is one assertion.** `CompanionHost.PublishContext`'s raise
(`src/dotNet/Plugins/CompanionHost.cs:709-710`) has never run in a test or under any shipped module,
because no module subscribes. `ModuleHostSelfTest` already stands up the real `CompanionHost`
(`src/dotNet/Plugins/ModuleHostSelfTest.cs:201`), so subscribe, publish, and assert the key arrives
— a push channel nothing has ever pushed through is a channel nobody has proved delivers.

### 📌 19. The publish-freshness check is red for all six modules, and the recorded reason is only half of it

**The gate has one failing check right now** and it is this one: `Test-ModulePublishFreshness.ps1`
reports fortunes, aibrain, petstudio, reminder, remembrance and blinkingled all behind their source.
`d780812`'s commit message and every note since attribute that entirely to `TrayConventions.cs` being
added to ModuleKit (`8083d52`), which every published module bundles as a `ProjectReference`
**without** `Private="false"` — that is what puts ModuleKit in the watch set, and the cost of an
addition there is now recorded in
[`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md). **It is the sufficient cause for all six and
the SOLE cause for only two.** Measured per module with HEAD at `d780812`, by running the check and
reading its own culprit attribution:

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

**Re-measured at `ab1cd0c`, and that camouflage has now actually happened.** The table above is a
snapshot of `d780812` and two of its rows are already false: `reminder` and `remembrance` are no
longer ModuleKit-only. `cb1505d` (Reminder's `_seenPets` bound) and `fe6ed35` (Remembrance's four
capture-path leaks) are real changes to shipped payloads, and fortunes took two more on top of
`d780812` (`66f3186`, `01dd84a`). So a day of module fixes landed inside a check that was already
red for a reason nobody was re-reading — which is the argument, demonstrated rather than predicted.
**Read the attribution off the check, not off this table**: `Test-ModulePublishFreshness.ps1` prints
its own culprit paths and commit counts per module, and a hand-copied table of them goes stale in
hours.

**Reverting cannot clear it, and this closes off one of the three options that were recorded.** The
check compares **commit EXISTENCE** in `zipCommit..HEAD` (`packaging/Test-ModulePublishFreshness.ps1`,
the `git log --format='%h %s' "$zipCommit..HEAD" -- @watchedPathspecs` call), never bytes. A forward
commit deleting `TrayConventions.cs` is itself a commit touching the watched ModuleKit directory, so
it makes the count worse — two commits where there was one — and leaves aibrain and petstudio red
regardless. Only a history rewrite would clear it. **So the options are republish, or accept it with
the check's own attribution in hand; revert is not one of them.** Republishing is outward-facing (merging
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
| Fortunes | 4 | 4 `Log(...)` call sites in `FortunesModule.cs` (engine rebuild + its catch, pack download + its catch) through a private `Log` wrapper reaching one `IHost.Log` at `FortunesModule.cs:968` |
| BlinkingLed | 3 | 1 `Log(...)` in `ScrollLockBlinker.NoteDelivery`, reached from three toggle paths and routed through the static `LogSink` that `BlinkingLedModule.cs:74` wires to `IHost.Log`, plus 2 module-side call sites through a private `Log` wrapper (`BlinkingLedModule.cs:345`) |
| PetStudio | 2 | one `host.Log` inside `PetStudioModule.ReportFailure`, reached from the two catches that are its only entry points (`Open`, `OpenForImport`) |

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
code. Two decisions that used to sit here are in
[`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md) instead, because neither asks for work:
"moves the user's windows" (48 actions) is refused deliberately, and a blank frame is legitimate so
"no blank tiles" cannot be a corpus-wide gate. What remains open:

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
  **That route is now proven, which is the cheapest this item will ever be:** the Shutdown-unsubscribe
  assertion took exactly it on 2026-09-17 and sits in `ModuleConventionSelfTest.Run` beside
  `loader.ShutdownAll`. `TrayConventions.CheckTrayIcons` is already IN ModuleKit, so calling it from
  the host adds no ModuleKit member and therefore costs no republish — see the ModuleKit entry in
  [`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md) for why that distinction decides the design.
- ⬜ **A dark 1px line on the left edge of Jesus Our Lord's fall frame.** NOT a conversion artifact: the
  baked tile (88) and its left neighbour (87) were both rendered out of the shipped sheet and are clean,
  and the sheet is 2560x2560 with exact 256px tiles, so there is no rounding slop in the compositor.
  That leaves runtime tile sampling in the host (bilinear filtering picking up a column from the
  neighbouring tile when the companion is scaled). Fix is host-side, either sampling with a half-pixel inset or
  clamping, so it needs a release. Reported 2026-08-28.

### Module SDK follow-ups

- 📌 **`ModulePermissions` still cannot disclose synthetic input, input monitoring or process
  launch, and a SHIPPED module under-discloses because of it.** `BlinkingLedModule.cs:59` declares
  `ModulePermissions.Speech | ModulePermissions.Storage` while `engine/ScrollLockBlinker.cs`
  P/Invokes `SendInput`, and `:57` says so in a comment — *"There is no ModulePermissions flag for
  synthesizing input, so the consent screen cannot state it."* So the consent screen
  under-discloses today, in the one place it can be checked. Add `InputSynthesis`, and
  `InputMonitoring` / `LaunchProcess` if IdleLauncherTray is ever unblocked
  ([`docs/BLOCKED.md`](docs/BLOCKED.md) T58). Additive to the enum, so safe; per the enum's own
  comment these are DISCLOSURE flags rather than gates, and `docs/module-ecosystem-roadmap.md`
  settles why containment would be security theatre. **Decide the flag per channel, not once for a
  module** — the AgentFlow section at the top of this file reaches the same conclusion from the
  other direction, having found four actuation channels of which none needs synthetic input.
  The audio half of this finding is closed: `Microphone`, `SystemAudio` and `AgentTranscripts` were
  added for Remembrance and AgentFlow. The port assessment it came out of is
  [`docs/IDEAS.md`](docs/IDEAS.md) idea 18.

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

*(The closed entries from this section — and there are many, including four separate cases of an
absence check defeated by a comment describing the very thing it forbids — are in
[`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md).)*

### Feature ideas (queued, not yet scoped)

**Moved to [`docs/IDEAS.md`](docs/IDEAS.md) on 2026-09-17, numbers intact** (16 per-companion
speech personality, 17 the Remembrance listenable-output gap, 18 the tray-utility ports): they are
product ideas nobody has scoped, and this file holds work that can be picked up. Ideas 1 to 15 stay
closed in [`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md). One piece of idea 18 did NOT
move, because it is engineering debt rather than an idea: the `ModulePermissions` under-disclosure
in a shipped module, which is now filed under "Module SDK follow-ups" above.
