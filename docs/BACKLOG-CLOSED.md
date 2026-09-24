# Backlog — closed records

BACKLOG.md's own convention is that a closed item is deleted from it rather than marked done, because
2,560 lines of finished work is what stopped it being readable as a backlog. These sections had reached
the state that convention describes: every bullet under them is closed, so they were no longer telling a
reader what to do next.

They are kept rather than deleted because each one records HOW something was settled, and several carry
the measurement that settled it. Nothing here is open. If you are looking for work, you are in the wrong
file — read [`../BACKLOG.md`](../BACKLOG.md).

Moved out of BACKLOG.md on 2026-09-24, verbatim.

---

## Open: second full-repo audit, 2026-09-17 (release machinery + plugin ABI)

Two read-only audits ran over the release machinery and the plugin ABI after the first cycle's work
landed. Between them they raised 28 findings; 24 are fixed in this cycle and the reasoning is in the
commits. What is left is here, and the three that are DECISIONS rather than work are in
[`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md) instead.

- ✅ **`packaging/legal-files.json` is deleted (2026-09-18), and nothing was missing.** The
  question it raised was answerable rather than a judgement call, and the answer is that the file
  is a fossil of the self-contained era. Three of its eight notices ship, and they are exactly the
  three for components this app REDISTRIBUTES:

  | notice | ships? | why |
  |---|---|---|
  | `NAUDIO_LICENSE.txt` | yes, in the payload | four `NAudio.*.dll` are in `runtime-files.txt` |
  | `ONNXRUNTIME_LICENSE.txt` | yes, in `fortunes.zip` | the module bundles `onnxruntime.dll` |
  | `ONNXRUNTIME_THIRD_PARTY_NOTICES.txt` | yes, in `fortunes.zip` | same |
  | `DOTNET_RUNTIME_LICENSE.txt` | no, and correctly | `SelfContained=false`: the .NET 10 Desktop runtime is a PREREQUISITE the user installs |
  | `DOTNET_{5,6,8,10}_THIRD_PARTY_NOTICES.txt` | no, and correctly | same, and four of them are for runtime versions this app has never targeted |

  So the five that do not ship describe a runtime we do not convey, and shipping them would claim
  to pass on something we never hand over. The file dates from `0936658` ("Desktop AI Companion
  1.0.0") when the build was self-contained; nothing has read it since, which is why its eight
  digests could rot unnoticed. [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) is the
  human-facing inventory and ships in the payload, and it already says in its own words that it is
  not a rights clearance.

- ✅ **CLOSED 2026-09-22 in fortunes 1.0.3**, via the static `LogSink` pattern AiBrain and
  ScrollLockBlinker already use. Wired before `RebuildEngine` so the first stand-down is not
  the one that gets missed, and nulled in Shutdown. Original entry:
  **Fortunes could not report a smart picker that fails for the second reason.**
  CLOSES-WHEN: grep-present modules/Fortunes/engine/SmartFortunes.cs "LogSink"
  The instrumentation added `smart=on model=present|ABSENT`, which catches the shipping-level cause.
  It does not catch "model present, native onnxruntime fails to load": `SmartFortunes.WarmCore`
  returns silently when the embedder is not ready, `_ready` stays false for ever, and
  `SmartStatusFor` reports "indexing in the background" indefinitely. Needs a second static sink on
  `SmartFortunes`; judged under the "small number of genuinely useful lines" bar when the rest was
  written, and recorded rather than forgotten.

## CLOSED: full-repo audit, 2026-09-17 (all nineteen items)

Three read-only audits ran in parallel after the AgentFlow merge: dead code and calls that go
nowhere, resource leaks and regression risk, and checks that cannot fail. **All nineteen items are
closed as of 2026-09-17**, each re-verified against the tree rather than taken from its commit
message — which mattered, because four of my own "this is done" claims were measured FALSE on that
pass and one of them (item 5) was only half done.

The last four to close, and what closed them:

- **5** — the two write-only settings KEYS are gone, not just their getters, and they took a
  serialisation format and six assertions with them: `ModuleUpdateScan.Encode`/`Decode` existed
  solely to write the key nothing read.
- **16** — both optimizations MEASURED. `CheckFullScreen`'s walk is now shared per cycle and cached
  per monitor (53 walks/s to 3.3 at MAX_SHEEPS, stand-down latency same or better on every
  statistic). `DiagnosticLog.Write` was **declined with numbers**: 38x per line and it multiplies
  0.00 lines/s in the shipped configuration, and the item's premise that `LogCategory.Animation` is
  per-frame is measurably false — those lines fire per TRANSITION.
- **17** — `ContextChanged`'s raise is exercised, with the two assertions that make the recorded
  decision checkable: a late READER still gets the value, a late SUBSCRIBER gets nothing.
- **19** — the six modules were republished with version bumps and the gate is green. The item's own
  prediction came true first and is worth keeping: a permanently-red check camouflaged a day of real
  module fixes, and its attribution table was already stale at the commit it cited.

Where a closed item left knowledge worth consulting it went to
[`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md) rather than into a DONE annotation: item 14's
CS0414 measurement and the `--no-incremental` warning-count trap, the reason a ModuleKit addition
costs a seven-module republish (item 13), item 17's `ContextChanged` decision, and item 15's
make-the-code-count rule.

---

## CLOSED: every module now records what went wrong (filed 2026-09-10, closed 2026-09-17)

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

**Fortunes, PetStudio and BlinkingLed were the last three at zero, and they were instrumented on
2026-09-17.** Nine lines, every one a transition or a per-batch summary rather than a per-pick or
per-frame line, each mutation-tested by breaking the wiring and requiring the naming assertion to
fail. The plumbing needed nothing built: `IHost.Log(moduleId, message)` routes a module's line into
the same rotating diagnostic log the host writes, under `Modules`, with per-module muting already
keyed on the module id.

The value was lower than AI Brain's and for a real reason rather than a shrug: none of the three
fails silently by construction, so "it does nothing" already comes with a visible error, where AI
Brain's `catch { return null; }` made every failure look identical to normal quiet operation. What
the instrumentation found is that each of the three had exactly one exception to that -- a failure
the user CANNOT see -- and those are the lines that were written:

- Fortunes: `smart=on model=ABSENT` is a silent downgrade. With `bge-small` missing the smart picker
  can never become ready, every pick falls back to random, and the pane says "indexing in the
  background" for ever. Plus `catch { _provider = null; }`, which speaks nothing on every land, poke
  and drop and reported it nowhere.
- BlinkingLed: `SendInput` returning 0 (UIPI, a locked session, an elevated foreground window)
  leaves the LED dark immediately after the companion said "Keeping the lights on for you". And the
  Caps Lock stop, which is suppressed on purpose AND persisted, so a week later the module is off,
  the tray agrees, and nothing ever said Caps Lock did it.
- PetStudio: failing to OPEN the window was reported only through `SayAll`, which drops the message
  silently when no companion is on screen -- and both the tray entry and the Companions-pane deep
  link are reachable in that state.

One residual gap is deliberately NOT closed, and is the only thing left here: "model present but
the native onnxruntime fails to load" still leaves the smart picker permanently unready with no
line, because `SmartFortunes.WarmCore` returns silently when the embedder is not ready. Closing it
needs a second static sink on `SmartFortunes`, which was judged under the "small number of genuinely
useful lines" bar. Written down rather than forgotten.

| module | diagnostic lines it can emit | how it was counted |
|---|---:|---|
| **AiBrain** | **27** | 26 `Log(...)` call sites in `engine/AiBrain.cs` routed through the static `LogSink` that `AiBrainModule.cs:159` wires to `IHost.Log`, plus one direct `host.Log` at `AiBrainModule.cs:1080` |
| Remembrance | 8 | direct `_host.Log` call sites, all in `RemembranceModule.cs` |
| AgentFlow | 7 | 5 through a private `Log` wrapper + 2 through `Explain`, all reaching one `IHost.Log` at `AgentFlowModule.cs:519` |
| Reminder | 4 | direct `_host.Log` call sites, all in `ReminderModule.cs` |
| Fortunes | 4 | 4 `Log(...)` call sites in `FortunesModule.cs` (engine rebuild + its catch, pack download + its catch) through a private `Log` wrapper reaching one `IHost.Log` at `FortunesModule.cs:968` |
| BlinkingLed | 3 | 1 `Log(...)` in `ScrollLockBlinker.NoteDelivery`, reached from three toggle paths and routed through the static `LogSink` that `BlinkingLedModule.cs:74` wires to `IHost.Log`, plus 2 module-side call sites through a private `Log` wrapper (`BlinkingLedModule.cs:345`) |
| PetStudio | 2 | one `host.Log` inside `PetStudioModule.ReportFailure`, reached from the two catches that are its only entry points (`Open`, `OpenForImport`) |

**Counted 2026-09-17, then recounted the same day after the three were instrumented. If this table is
edited again, count the SINK as well as the direct calls.**
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

---

## MOSTLY CLOSED: the MSI upgrade path (done), the About window (one glance left)

**Was in [`docs/BLOCKED.md`](docs/BLOCKED.md) as T2 / T49; the blocker went on 2026-09-17 and the
work went on 2026-09-18.** It had sat there needing "a real reinstall of the MSI over a previous
install", which needed an installed build to upgrade over and a way to build an MSI here. Both
existed by then, so the upgrade ran: 1.1.4 → 1.1.5, verified below. What is left is one glance at a
window.

- ✅ **The UPGRADE path was exercised on 2026-09-17.** 1.1.4 → 1.1.5 by MSI, `msiexec` exit 0, and
  the installed `DesktopAICompanion.Contracts.dll` read back **FileVersion 1.1.5.0** carrying both
  new `ModulePermissions` members (verified by name in the installed binary, with a control string
  absent). That read is the exact failure host-contract rule 3 exists to prevent -- Windows
  Installer SKIPS a file whose `FileVersion` did not move -- and it had never once been performed
  here. The installed build then passed its own `--hardening-selftest`, 187 assertions.

  What is still unexercised: an upgrade that CROSSES a `ModulePermissions` addition with a module
  installed that declares the new flag, and an upgrade onto a machine where a module update is
  already staged in `PendingModuleUpdates`.

- ✅ **The upgrade path and section K are DONE (2026-09-18).** 1.1.4 → 1.1.5: `msiexec` exit 0,
  exactly one entry in Programs and Features, settings and the companion mix surviving, modules from
  the earlier install loading at their older versions and then updating, and the tray icon correct --
  reported by the maintainer and corroborated by `shellHasIt=True`, the SHELL's verdict, which is
  the half that was missing when BUG-001 hid behind a line reading `success=True`.

- ✅ **Sections H and I are DONE (2026-09-18), on the republished catalog.** Fortunes and AI Brain
  both notified and updated correctly: `updated module 'fortunes'`, then `aibrain 1.1.5` and
  `fortunes 1.0.2` on the next launch. That is the staged swap working, which is how an in-place
  module update has to happen while the old DLL is loaded and locked, and it is the end-to-end proof
  that bumping each module's version was the right call -- without it the fixed payloads would have
  reached new installs only.

- ✅ **Sections A-E were walked by the maintainer, at or before 1.1.4.** Undated, because nothing
  recorded it: `SMOKETEST.md` had 77 checkboxes and no way to say a pass had happened, so the walk
  left no trace and two backlog restructurings carried an item forward claiming it never occurred.
  The file now has a walk log, which is the actual fix.

  Coverage, not a re-walk request: **D**, **E** and **G** changed after that pass. D has automated
  cover (`tests/fullscreen-standdown-probe/`, run against 1.1.5 on two monitors). E and G are the
  two where a glance is the only evidence -- one poke past the fifth, one press of "Check for
  companions and updates".

- ✅ **CLOSED 2026-09-22: the owner looked.** Reported walked personally. Recording the instrument
  rather than a measurement, deliberately: "does this window look right" is not a property any test
  can assert, so a person opening it was always the only closing evidence available -- same as the
  notification-sound pleasantness item closed on 2026-09-21.

Both original entries in full, with the pre-tag verification that WAS performed beside the gap:
[`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md).

---


---

## Closed items removed from BACKLOG.md on 2026-09-24

Individual entries, verbatim, in the order they appeared. Each was already marked closed; the file's
own convention is that they should not still have been sitting in it.

- ✅ **CLOSED in 1.3.2: the pane's blocking UI-thread IO.** It called `VsCodeSetup.Inspect` on every
  open and every dropdown change. MEASURED, one call per fresh process: **30.7/30.1/30.7 ms** with
  the port listening, **273.3/284.6/279.3 ms** with it closed. The poll worker now calls `Inspect`
  in place of `Probe`, caches the report, and the pane renders the cache. The per-pet animation list
  was separately read and parsed from disk **twice per load** (once for the dropdown's options, once
  to pick its selection); memoised per pet, dropped on Save.

  ⚠ **And a measurement that stopped a bad "optimisation", recorded because the idea is seductive.**
  The 273 ms reads like a broken wait: a closed loopback port surely refuses instantly, so the
  250 ms timeout looks like ceremony. It is not. A **synchronous** connect to a closed port on this
  box takes **2,063 ms** to return `ConnectionRefused` (2063.1 / 2066.3 / 2066.1). The timeout is
  the only reason the probe answers in a quarter-second instead of two. Two further theories were
  measured and also wrong: that the host-string overload was taking a dual-stack multi-address path
  (explicit IPv4 `IPEndPoint`: ~275 ms, identical) and that `BeginConnect`'s wait handle was not
  signalled on refusal (`ConnectAsync` + `Task.Wait`: ~280 ms, identical). Three APIs agreeing to
  the millisecond is a timeout working, not a bug. All of it now lives on `VsCodeSetup.Probe`.

  **What remains, and was deliberately NOT done:** the tick still pays up to 250 ms on the worker
  when the port is closed, once per ten seconds. A backoff would cut that, and was rejected: it
  would delay noticing that VS Code has come up with the flag from 10 s to a minute, and "why did
  it not press" is exactly the complaint that started this work. 2.5% of one background core is the
  cheaper thing to spend.

- ✅ **CLOSED 2026-09-22 in agentflow 1.4.0.** `sources == 0` now emits `NoRuleFilesNote`
  once, deduped and re-armed by the caller. Tested BOTH directions -- it must appear with no
  settings file anywhere and stay quiet once one exists -- because a test that only proved it
  appears would pass against a scan that emitted it unconditionally. Original entry:
  **`RuleLoader.Load`'s `sources` count was read into a local and discarded** (`AgentFlowModule.cs`
  CLOSES-WHEN: grep-present modules/AgentFlow/AgentFlowModule.cs "NoRuleFilesNote"
  `Scan`). `sources == 0` means "no permission-rule file was found anywhere", which is a completely
  different state from "rules loaded, none matched" -- and it is the state a user gets when their
  rules live somewhere unexpected, with no explanation and every call reading Undecidable. Not fixed
  because surfacing it needs another `out` on `Scan`, which already carries eight parameters and two
  outs, plus a dedupe so it is not logged every ten seconds. Worth doing with the signature tidy-up
  rather than bolted on.

- ✅ **CLOSED in 1.3.2: the settings dictionary was read from the poll worker.** `Enabled` was the
  last one. Every other value the tick needs was already copied on the UI thread before `Task.Run`;
  this one was missed because it hides behind two predicates rather than being named inline, so the
  worker went and read the unsynchronised `Dictionary<string, string>` while `Apply` could be
  writing it. `ShouldProbePort` and `MayLookNow` now take it as an argument and are pure functions
  of their inputs.
  **Note what that cost the test, and what was added back.** Injecting `enabled` means the
  asymmetry assertion would pass even if no real mode ever supplied `true`, so a second assertion
  now pins the other half: `AgentMode.Scans(Notify)` is true and `Scans(Off)` is false. A predicate
  made pure is easier to test and easier to test VACUOUSLY.

- ✅ **CLOSED 2026-09-22 as not worth doing, with the numbers.** Kept rather than deleted because
  the idea is obvious enough to be re-proposed. A `FileSystemWatcher` here would need: one watcher
  per root with `IncludeSubdirectories`; a thread-safe set of touched paths, since events arrive on
  threadpool threads while the tick runs on its own worker; a forced full reconcile on the `Error`
  event, because a buffer overflow loses an unknown set of changes; a full enumeration at startup
  regardless, because a watcher knows nothing about files that existed before it started; disposal
  on `Shutdown`, in a module that has already shipped four resource-lifetime defects; **and the
  polling sweep kept anyway** as a reconciliation pass, because watchers miss events on network
  paths and some filesystems. What it buys, measured warm after the one-syscall-per-file change:
  **11.0-12.6 ms per ten-second tick**, about 0.1% of one core. That is a second ingest path which
  can go silently stale, for a tenth of a percent. Same reasoning that rejected the probe backoff.

  Original entry, for the measurements: **`ActiveTranscripts` is the dominant cost of a tick.** The fold went
  from 535 ms to 0.3 ms, so what is left is the directory sweep. MEASURED 2026-09-21 by calling
  `ActiveTranscripts` from a .NET harness, one call per fresh process, interleaved: **31.7/30.4/30.8
  ms** for the Claude root (705 files, skipping `subagents`) and **21.8/20.4/25.2 ms** for the Codex
  root (247 files), every ten seconds, which is around half a percent of one core.
  ⚠ These replace figures of 45 ms and 17 ms that were first committed here from a **Python
  `os.walk` proxy** rather than from the real method. The proxy was wrong in BOTH directions, over
  by 45% on one root and under by 30% on the other, and it hid the more interesting fact: cost is
  **not** linear in file count, because the Codex store nests a directory per day. A proxy for an
  fs benchmark has now been wrong here every time it has been used.
  Not urgent. The real fix is a `FileSystemWatcher` per
  root feeding the same `SessionCache`, which would take a quiet tick to near zero; the reason to
  wait is that a watcher has its own failure modes (buffer overflow, network paths, missed events on
  some filesystems) and would need the polling sweep kept as a reconciliation pass anyway.

- ✅ **CLOSED 2026-09-22: all four found and fixed.** The unlocated three were an empty list
  never passed to anything, a verbatim duplicate of the assertion above it, and a flag that no
  test could ever set. The fourth, worst one was `probe.Check("every logic group ran", ok)`,
  a tautology reading as "the suite ran"; it is replaced by a reflection tripwire that fails
  when a SelfCheck group is declared and never wired. Original entry:
  **The audit reported four self-test assertions that cannot fail; one was found and fixed, three
  CLOSES-WHEN: grep-present modules/AgentFlow/AgentFlowModule.cs "DeclaredSelfCheckMethods"
  are unlocated.** The fixed one asserted `candidates.Count > 1` twice in a row, the second time as
  though it were checking something else. The other three were not named in a form that survived the
  audit, and hunting them blind costs more than it returns; the right tool is a pass over every
  `probe.Check` in the module asking what input makes it false. Filed rather than guessed at,
  because a check that cannot fail is worse than no check: it reads as coverage.

- ✅ **DONE. `MinHostVersion` is `1.2.0`** (raised past 1.1.5 during the options-ABI cycle; the module has since published 1.1.6 and 1.1.7 against it, so the consent line names every permission bit it uses). Original entry below for the reasoning. It was published on
  2026-09-17 still declaring `1.0.0`, deliberately: raising it would have made the module
  uninstallable for everyone, because v1.1.5 has not been tagged. The cost of leaving it is that a
  host older than 1.1.5 has no name for permission bit 11, so
  `RemoteCatalog.TryParsePermissions` drops it and the consent line omits `AgentTranscripts`
  entirely. That gap is covered for now by the catalog DESCRIPTION, which every host renders and
  which states the transcript read in prose. Once v1.1.5 ships, raise the floor and the prose
  becomes belt-and-braces instead of the only disclosure.

- ✅ **CLOSED 2026-09-21: it has now pressed real prompts, for BOTH agents.** The closing criterion this entry set was an `auto-approve clicked` line in the diagnostic log, and there is one for Claude and one for Codex (`auto-approve clicked for a Codex command: pressing option 2, recognised as 'allow once'`), with the Codex transcript confirming the command then ran rather than being rejected. Original entry below. `CdpApprover.cs` reads the
  pending prompt out of the Claude Code webview and presses the approve-once row; `PressBudget.cs`
  is the death-loop guard this entry asked for (three identical presses, or ten in five minutes,
  and it stands down until the switch is toggled). Selectors were read out of the shipped bundle
  rather than guessed, the transport is the BROWSER endpoint plus `Target.attachToTarget` because
  the per-target socket answers 500 here, and the press re-checks the row's label so it cannot
  drift onto the permanent-grant row sitting next to it.
  **What is open is the evidence, not the code.** No permission prompt has been on screen since it
  was written, because `curl` no longer prompts in `auto` mode and this box runs `auto`. To close:
  tick auto-approve, work in a `default`-mode session until something prompts, and confirm the
  diagnostic log carries an `auto-approve clicked for <tool>` line. Until then the self-test's own
  doc comment says the press path is only exercised against a closed port.

- ✅ **DROPPED 2026-09-22, owner delegated the judgement.** The 240x separation is real, measured,
  and is not the problem: **attribution is**, and every route to it is closed or degenerate. Open
  file handles: measured negative, the agent appends and closes. `cwd`: three concurrent sessions
  shared one workspace slug. `--resume` on the command line: launch state only, and a fresh session
  carries none. Activity alignment is built and has never been WRONG across four gradings, but its
  coverage flips run to run -- 7/24, then 0, then 5/5 four minutes later -- and the 5/5 was
  arithmetic, not evidence, because only one transcript emitted a tool call in that window. That is
  the degenerate-axis failure this repo has already recorded once. On top of which none of it
  exists in the shipped C#: it lives in `agentflow_cpu.py`, so this is greenfield module work on an
  unsolved research problem, aimed at the modes where the shipped detector already stands down by
  design. This entry's own last sentence was the answer all along: the transcript-only detector
  needs none of it, which is why the module shipped without it.

The idea: the companion notices a coding agent (Claude Code, Codex) sitting blocked on a permission
prompt, says so, and optionally answers it. The pet framing is presence — noticing your agent has
been stuck for nine minutes is the part a dashboard cannot do.

**What the research settled:**

- ✅ **CLOSED 2026-09-22: done, and this entry outlived it by a day.** The closing criterion the
  entry set was "read `turn_context.approval_policy` and treat `never` as cannot-prompt". That
  shipped in 1.3.0: `TranscriptReader.cs:382` matches the `turn_context` record, `:388` reads
  `approval_policy`, and `BlockedDetector.cs:103`/`:179` stand down on anything that is not
  `on-request`. The entry's "do NOT map `collaboration_mode.mode`" warning is honoured and the
  reason is recorded in the code comment at `TranscriptReader.cs:384-386`.

  Third stale entry found in two days, all three fixed by later work in the cycle that filed them,
  none linking back. The others were the 1px sprite line and the host-event blind spots.

- ✅ **CLOSED 2026-09-21: the owner listened and it is fine.** The measurements (0.75 s, peak
  0.7, silence at both ends) never could settle this one, because "pleasant" is not a property a
  test can assert. The only instrument for it was a person with ears, and that was always the
  cheapest item on this list to close.

- ✅ **CLOSED: fixed during the 1.2.x cycle, and this entry outlived it.** `ContextChanged` is
  field-like in the fake host now, and both blind spots have `HasSubs` properties beside the other
  four, so the leak check covers six of six: `ModuleConventionSelfTest.cs:111-118` names each
  event, `:235-240` provides the properties, and `:249-250` raises the two that were previously
  unobservable. Original entry: `ContextChanged` was declared `add { } remove { }`, so a
  subscription was *discarded* rather than merely unobserved and a module leaking it could never be
  caught; `FullscreenChanged` kept its subscribers but nothing looked. The test exists because
  Remembrance once shipped exactly that bug on `HostShutdown`.

- ✅ **CLOSED 1.1.9. It is recorded and surfaced on the pane**, not logged: the discovery
  happens on a WORKER and `IHost` is UI-thread-only, so reporting a threading fault by
  committing another one would be its own joke.

- ✅ **CLOSED 1.1.9, and it was the real one on this list.** Stopping the timer prevents the
  NEXT poll and does nothing about the one already on a worker, and that poll ends in a CLICK --
  so the module could press a button on someone's behalf after they switched it off. A volatile
  flag is now Shutdown's first act, and the press goes through one gate both the worker and the
  self-test ask.

  **The guard took two attempts to prove, and the failed one is the lesson.** Shutdown also nulls
  `_host`, which makes the gate false on its own, so a test that called Shutdown and then asked
  could not tell the flag from the null: the mutation removing the flag SURVIVED. What the flag
  buys is the window between the start of Shutdown and the end of it, plus being volatile where
  `_host` is not. `BeginShutdown` exists so that window can be asserted.

- ✅ **CLOSED 1.1.9, differently for each.** `VsCodeRunning` is gone: written by `Inspect`, read
  by nothing, and the write was a process enumeration per pane open. `UnsafeDetail` is KEPT and
  finally read -- it exists so refusal text off someone's screen goes somewhere the diagnostic log
  never sees while the logged reason carries only counts, and nothing reading it meant that
  property was unenforced. It has an assertion now, mutation-proved by leaking the text into the
  logged line.

- ✅ **CLOSED 1.1.9.** `Load` is removed and the assertions drive `LoadPending`, which is what the
  host drives. `MinHostVersion` is 1.2.0, so no host that can load this module took the old path.
  A test exercising a path production does not take is worse than no test, because it reads like
  coverage.

- ✅ **CLOSED 2026-09-22, owner walked it.** (The glyph was left open when the closure was
  written, so it kept counting as open -- the same link-back slip, one line smaller.)
  Original entry: **The live smoke test had never been walked, across TEN releases (v1.9.4 → v1.9.13).** Everything
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
  maintainer the same day.

  ✅ **CLOSED 2026-09-22: the owner walked it.** Reported walked personally. Recording the
  instrument rather than a measurement: opening a window and looking at it is the one thing no gate
  in this repo can do, which is why the entry existed and why a person's report is the only
  evidence that could ever have closed it. The watchlist in `SMOKETEST.md` stays live for the next
  release; this entry was about the ten-release GAP, and the gap is closed.

- ✅ **CLOSED 2026-09-22 in host 1.2.4.** The host now asserts it, and on its FIRST run it
  failed the module template, which registered an icon-less row while its own comments taught
  the convention. Scope is narrower than this entry assumed: `--module-selftest` covers four
  of seven in-tree modules, plus every out-of-tree one. Submenu icons stay unenforced.
  Original entry: **CONVENTION: every tray entry carries its own unique icon — the check now exists in ModuleKit, and
  CLOSES-WHEN: grep-present src/dotNet/Plugins/ModuleConventionSelfTest.cs "EveryTrayEntryHasAUniqueIcon"
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

- ✅ **CLOSED 2026-09-21: already fixed, on the same day it was filed, by a later commit.**
  The entry was written by `f04edd649` ("logs the two findings that are NOT fixed"), and
  `bf983ff2f` ("Fix the dark rim on downscaled frames") landed after it and was never linked
  back. `docs/HISTORY-post-1.0.0.md:1243` still forwarded here as open, so the staleness
  propagated.

  **The diagnosis in the entry was also wrong about the mechanism.** It blamed runtime tile
  sampling, but sprites are pre-sliced into standalone `Bitmap`s at load (`src/dotNet/Xml.cs:621`)
  and the sheet is disposed at `:531`, so nothing can sample across a tile boundary at runtime.
  The draw path blits whole bitmaps 1:1 through `UpdateLayeredWindow`
  (`src/dotNet/FormCompanion.cs:401`) with no `DrawImage`, no interpolation and no source
  sub-rect. The real cause was at LOAD time, in the smooth-downscale path, and the shipped fix
  is extract-then-scale at `Xml.cs:657-689`: cut the tile 1:1 unfiltered, then scale that
  standalone bitmap, whose edges are real image edges.

  ⚠ **Do not re-propose `WrapMode.TileFlipXY`.** It is the textbook remedy and it was tried
  and MEASURED here: darkest edge pixel 236 without it, 237 with it, 254 with extract-then-scale
  (`Xml.cs:645-656`). The wrap applies to the image, not to a source sub-rectangle.

  Regression net exists: `src/dotNet/RuntimeHardeningSelfTest.cs:506-535` builds a 64x32 sheet
  with tile 0 black and tile 1 white, downscales tile 1, and asserts the darkest pixel is >= 250
  — any dark pixel could only have come from across the boundary. Runs in `--hardening-selftest`,
  which the gate runs.

  Two loose ends, recorded rather than actioned. The entry's geometry no longer matches the
  shipped pack: it cites tile 88 on a 2560x2560 sheet, and `Companions/shimeji-brq51bkr` now
  declares 9x8 tiles on a 2304x2048 PNG, so **tile 88 does not exist** — the pack was
  re-converted on 2026-09-09, after the report. And for a 256px cell `ScalePolicy.FitFactorForFrameD`
  caps the factor at 1.0, so at 100% or above this pet does not take the downscale path at all.
  **Nothing records whether the reporter re-observed the line after `bf983ff2f`**; if it is ever
  seen again it is a new bug, not this one.

- ✅ **CLOSED 2026-09-22 in host 1.2.4.** Resolved by identity instead: the loaded module's own
  type first, then other IModule implementations, then a deterministic scan that FAILS on more
  than one match. Public and static only, and `out string` exactly. Original entry:
  **`--module-selftest=<id>` picked the FIRST `bool SelfTest(out string)` in the assembly, which may not be
  CLOSES-WHEN: grep-present src/dotNet/Plugins/ModuleConventionSelfTest.cs "TryFindSelfTest"
  the module's own.** `ModuleConventionSelfTest.RunModuleSelfTest` reflects over every type and breaks on the
  first match, including non-public ones. Reminder had six pure helpers each exposing exactly that signature,
  so any of them could have won over `ReminderModule.SelfTest` — non-deterministically, by metadata order.
  Worked around module-side by renaming those six to `SelfCheck` (2026-08-27), but the sharp edge is still
  in the host and will catch the next module author, including third parties. Fix shape: prefer the type
  implementing `IModule`, then fall back to the scan. Host change, so it wants a release to be worth much.

- ✅ **ANSWERED same day: it is `ClimbCeiling`, and nothing is wrong with it.** Reported as a
  sideways flight pose; the owner then guessed the action and the surface, and was right on both.
  Hornet's animation set has 31 entries, of which **25 is `GrabCeiling` and 26 is `ClimbCeiling`**,
  alongside `GrabWall` and `ClimbWall`. Shimeji pets treat the underside of a window as a ceiling,
  so she was climbing along the bottom edge of an application, not crossing open space.

  The premise of the original filing was wrong, which is the part worth keeping: it asked "if this
  is flight the sprite should face down", and it is not flight. A ceiling climb hangs from the
  surface above, so the pose in the screenshot is what that action is supposed to look like.

  ⚠ **One question does survive**, and it is the converter one: nobody has checked that
  `tools/ShimejiConvert` mapped the dedicated ceiling sprites rather than reusing a ground pose.
  If it reused one, the action would still be correct and the ARTWORK would be wrong, which reads
  exactly like the original report. Cheap to settle by eye: spawn her, wait for a ceiling climb,
  and compare against the source pet's own `ClimbCeiling` frames. Left open as a note rather than
  as an item, because there is no evidence of a defect yet.
*(The closed entries from this section — and there are many, including four separate cases of an
absence check defeated by a comment describing the very thing it forbids — are in
[`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md).)*
