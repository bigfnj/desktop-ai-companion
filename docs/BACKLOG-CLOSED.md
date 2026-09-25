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

---

## Closed 2026-09-24 in host 1.2.5 — the three ABI-gated items

All three were blocked on "this needs a contract change and therefore a host release". Two of them
really did. **The first one did not, and that is the part worth reading.**

### `RevealsPath` was narrowed WITHOUT touching the contract

The entry below concluded: *"Narrowing it needs a module id on `OptionsPane`, which is a contract
change and therefore a host release."* That was true of the PANE and false of the HOST. `IHost.AddOptionsPane`
is documented to be called from `Init` ("---- contributions (register in Init) ----", `PluginApi.cs`),
and `ModuleHost` knows exactly which module it is initialising at that moment — `module.Info.Id` sits
on the line after `module.Init(host)`. So `CompanionHost` now records `pane -> moduleId` on its own
side of the wire, in the shape it already used for `Responder.ModuleId`, and `OptionsPane` is untouched.

The reason this matters beyond one item: the blocker had been restated in a doc comment in
`OptionsWindow.cs` as settled fact, and it is the kind of claim nothing ever re-tests. It sat for a
release cycle. Verified by measurement, not by re-reading the comment:
`--wpf-options-selftest` now asserts that a module may NOT reveal a file inside another module's
storage, with a WITNESS asserting the old data-root-wide rule DID allow exactly that.

One real caveat, recorded rather than hidden: an out-of-tree module could stash the `IHost` and call
`AddOptionsPane` after `Init` returns. Nothing enforces the documented timing. Such a pane gets no
owner and falls back to the data-root-wide rule, which is the rule it would have had anyway.

Mutation-tested: 7 mutations, 7 fired. The one that initially SURVIVED was
`PermittedRevealRoot` discarding the owner it had just looked up — three lines of wiring between two
well-covered ends, needing a live `Program.Mainthread` that a headless self-test does not have. That
gap is now closed by a source invariant asserting the ARGUMENT (`RevealRootFor(owner)`, never
`RevealRootFor(null)`), because a check for the call's mere presence would have passed the survivor.

- 📌 **`RevealsPath` containment is data-root wide, not module-storage narrow.** `PaneView` receives
  CLOSES-WHEN: grep-present src/dotNet/Plugins/CompanionHost.cs "ModuleOwningPane"
  an `OptionsPane` with no module identity, so the host can only enforce "inside the app data root".
  `CompanionHost.ModuleDataDir` builds every module's storage as `<dataRoot>\modules\<id>`, so today
  that is arithmetically the same rule — but it means module A can reveal a file sitting in module
  B's folder, or in the app's own settings folder. Narrowing it needs a module id on `OptionsPane`,
  which is a contract change and therefore a host release.

### `PaneAction.InvokeWithPendingAsync`

Added as specified, and inert for every module that does not set it. The host already had the
dictionary to hand: `PaneView.Collect()` was being called twenty-four lines below the invocation, in
the same closure, to stash on-screen values for a rebuild.

Two guards had to widen with it — both read "no `InvokeAsync` means there is no button", so an action
carrying only the new delegate would have rendered nothing at all.

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

### `ModulePermissions.InputMonitoring` and `.LaunchProcess`

Both added; they are not equally live, and the enum says so rather than leaving a reader to find out.

`LaunchProcess` has three holders RIGHT NOW and no flag had ever said so — AiBrain spawns the ollama
runtime, Remembrance spawns whisper.cpp and its installer probe, PetStudio spawns ffmpeg/ffprobe
through the conversion engine. That is the same finding that produced `InputSynthesis`: a shipped
module doing something the consent screen never mentioned, beside a pane that prints "wants: Speech,
Storage" as an affirmative claim. Shell-opening a path or URL the user asked for (Fortunes revealing
its packs folder, Reminder opening an event URL) is deliberately EXCLUDED — that is the user's own
action taking effect, and folding it in would put the flag on seven of eight modules and make it
mean nothing.

`InputMonitoring` has no holder. Searched across `modules\` and `src\` for `GetAsyncKeyState`,
`GetKeyState`, `SetWindowsHookEx`, `GetLastInputInfo` and the raw-input registrations: zero hits. It
exists for `docs/BLOCKED.md` T58.

- 📌 **`ModulePermissions` cannot disclose input MONITORING or process launch.**
  CLOSES-WHEN: grep-present src/DesktopAICompanion.Contracts/PluginApi.cs "InputMonitoring"
  The `InputSynthesis` half of this entry is DONE and the under-disclosure it described is gone:
  the flag is declared at `src/DesktopAICompanion.Contracts/PluginApi.cs:104`, BlinkingLed declares it
  at `modules/BlinkingLed/BlinkingLedModule.cs:73` and AgentFlow at
  `modules/AgentFlow/AgentFlowModule.cs:339` (commits 601e944, 21aaebf). The old criterion grepped for
  "ProcessList", a string that has never appeared in `PluginApi.cs`, so this item could not have closed
  itself however much of it was fixed. Corrected 2026-09-24. What is left is
  `InputMonitoring` / `LaunchProcess` if IdleLauncherTray is ever unblocked
  ([`docs/BLOCKED.md`](docs/BLOCKED.md) T58). Additive to the enum, so safe; per the enum's own
  comment these are DISCLOSURE flags rather than gates, and `docs/module-ecosystem-roadmap.md`
  settles why containment would be security theatre. **Decide the flag per channel, not once for a
  module** — the AgentFlow section at the top of this file reaches the same conclusion from the
  other direction, having found four actuation channels of which none needs synthetic input.
  The audio half of this finding is closed: `Microphone`, `SystemAudio` and `AgentTranscripts` were
  added for Remembrance and AgentFlow. The port assessment it came out of is
  [`docs/IDEAS.md`](docs/IDEAS.md) idea 18.

---

## Closed 2026-09-24 — the fourteen guards that could not fail

Filed as one cluster because they share a shape: each reported a property nobody was testing. They
were taken FIRST, ahead of every other item, on the reasoning that a gate which cannot fail makes
the rest of a campaign unfalsifiable — and that turned out to be right twice over, because fixing
them immediately caught two live defects (`watchIntro`/`watchState` leaking prose into settings.json,
and a catalog advertising a version nobody could download).

Every one was mutation-tested: break the thing it guards, confirm exactly one failure naming the
right file, restore. Where a mutation SURVIVED, that is recorded below rather than quietly fixed.

- 📌 **Five safety parameters in `packaging/StagingPathSafety.ps1` are mandatory and never read.**
  `Remove-DesktopAICompanionSafeDirectory` (`:674-698`) declares `[Parameter(Mandatory)]$TrustedRoot`
  and reads only `$Path` and `$AllowedRoot` before `Remove-Item -Recurse -Force`. Same in
  `Remove-...SafeFile` (`:648`), `Reset-...StagingDirectory` (`:700`) and `Open-...NewScratchDirectory`
  (`:618`, which also discards `$ProtectedPaths` and `$ProtectedDirectories`).
  `Open-...ValidatedInputFile` (`:385`) and `Copy-...ValidatedInputFile` (`:476`) declare
  `[bool]$RejectHardLinks = $true` and never read it; `Copy-...` also never reads its mandatory
  `$Root`. So a copy whose `-Path` sits outside the declared `-Root` succeeds, and a junction inside
  the build output passes `-RejectHardLinks $true` and ships in the MSI. Real callers pass these
  expecting enforcement. The file's OWN docstring at `:509-522` records this exact defect being found
  and fixed in `Publish-...AtomicFile` ("took ELEVEN parameters and read TWO of them"); five instances
  survived in the same file. Supporting: `[uint32]$LinkCount` at `:68` is hardcoded to 1 in the
  constructor, and `Get-DesktopAICompanionFinalPath` (`:282`), the only link resolver in the tree, has
  zero callers.

- 📌 **The signing guard is proven by POSITION only, so the mutation it was rewritten to stop passes.**
  `tests/runtime-hardening-selftest.ps1:991-1010` asserts the guard exists, the call exists, and
  `guard.Index < callAt`. Nothing locates the guard's CLOSING brace. Close the
  `if (-not [string]::IsNullOrWhiteSpace($SigningCertThumbprint))` block early and move the signtool
  call below it: guard.Index 6191, callAt 6285, all three assertions pass over a build.ps1 that signs
  unconditionally. `$callAt` is also `IndexOf`, so a second unguarded call added later is invisible.

- 📌 **The PowerShell-7-only token list omits ternary and `??=`, the two most likely to be written.**
  `tests/runtime-hardening-selftest.ps1:1105` lists `AndAnd, OrOr, QuestionQuestion, QuestionDot,
  QuestionLBracket`. Tokenised under the real 7.6.5 parser, ternary `? :` emits **QuestionMark** and
  `??=` emits **QuestionQuestionEquals**; neither is listed. `QuestionDot`/`QuestionLBracket` do not
  fire for the usual `$var?.Prop` spelling either, because the parser folds the `?` into the Variable
  token. Add `$configuration = $Release ? 'Release' : 'Debug'` to build.ps1: CI runs this gate with
  `shell: pwsh`, both assertions pass, and build.ps1 is a hard parse error under 5.1. The comment at
  `:1103` claims "the gate catches them whichever host it runs on", which is false for both.

- 📌 **The shell-parity set is scraped out of workflow YAML as text, so it covers 11 of ~20 scripts.**
  `tests/runtime-hardening-selftest.ps1:1084-1099`. Excluded: `installer/New-RuntimeWixFragment.ps1`
  (invoked on every CI MSI build), `packaging/StagingPathSafety.ps1` and
  `packaging/WixToolchainPolicy.ps1` (dot-sourced by build.ps1 and build-installer.ps1), and seven
  other packaging scripts. `run-gate.ps1` is in the set only because build.yml mentions it inside four
  COMMENTS, so a comment cleanup drops the repo's own gate script and the `-ge 5` floor still passes.

- 📌 **The consent-before-download order check reads raw C#, so a comment satisfies it.**
  `tests/runtime-hardening-selftest.ps1:1202-1216` does `IndexOf` on unmodified source, 30 lines below
  a structurally identical check (`:1182`) that strips comments precisely because the unstripped
  version survived a revert, with the note at `:1177` recording it. Move the consent block below the
  download call and phrase the comment above it as "ModulePermissionConsent.NewlyRequested is
  consulted here": the ordering assertion holds while consent happens after the bytes are on disk.
  `Remove-LineComments` is defined at `:345` and is also not applied at `:477-505`, `:527-539` or
  `:460-467`.

- 📌 **`if: always()` is asserted as a bare presence, unbound to the step it belongs on.**
  `tests/runtime-hardening-selftest.ps1:1042-1045` matches the scrub step's NAME and matches
  `if: always()` anywhere, with nothing associating the two. release.yml has exactly one today, so it
  works by luck. Move it to the artifact-upload step and both assertions still pass while a failed
  build leaves the imported PFX on the runner.

- 📌 **The redirect-encoding invariant is per-FILE, stdout-only, and has no floor.**
  `tests/runtime-hardening-selftest.ps1:67-78`. One `StandardOutputEncoding` anywhere clears a file
  however many redirect sites it has: `tools/ShimejiConvert.Engine/Engine.cs` has two (`:250`/`:254`,
  `:305`/`:307`), so deleting the second pin leaves the check green with the live CP_ACP mojibake bug.
  `RedirectStandardError` without `StandardErrorEncoding` is not checked at all, and a moved tree
  yields zero files and passes on no evidence — which the same file explicitly defends against for the
  .wxs loop at `:1057`.

- 📌 **The atomic-publish suite's fifth refusal accepts ANY exception as proof.**
  `packaging/Test-AtomicPublish.ps1:121` passes `''` as the expected message, so the match at `:46`
  becomes `-like "**"`. Cases 1-4 each name their string. The guard case 5 exercises throws "Trusted
  staging root is missing or is not a directory" BEFORE the containment test, so if `$scratch` has
  gone, case 5 reports REFUSED on the wrong exception and the escape guard is never exercised.

- 📌 **The module-template release-window check degrades and then passes.**
  `packaging/Test-ModuleTemplate.ps1:126-130` writes a yellow "DEGRADED" line when `git tag --list`
  returns nothing and continues to exit 0, despite the comment at `:124` claiming it "Degrades
  LOUDLY". On a shallow clone the gate judges this script by whether it throws
  (`run-gate.ps1:137`) and reports the template OK. The identical pattern was found and hard-failed in
  the sibling `Test-ModulePublishFreshness.ps1:333-344`; the fix was not carried across.

- 📌 **`run-gate.ps1:43` checks `$LASTEXITCODE` after a .ps1 that signals failure by `throw`.**
  build.ps1 sets `$ErrorActionPreference='Stop'` and throws on every failure path, so the branch is
  dead. Lines `:88-101` of the same file spell out why this is wrong and apply try/catch to the three
  checks below it; the build call above them kept the old form. A build failure escapes as a raw
  exception instead of the `GATE FAILED:` summary, and no later check runs.

- 📌 **One self-test failure skips all source invariants in CI.**
  `.github/workflows/build.yml:62-69` throws at `:64-67` and invokes the invariants at `:69`, in the
  same step, below the throw. `run-gate.ps1:53-132` deliberately COLLECTS instead, with the comment
  "one missing module no longer hides every check after it". CI kept the old shape. Related: the gate
  fails on `SELFTEST-COUNT <= 0`; build.yml ignores the count.

- 📌 **A module whose .csproj path moves is silently not built, and the build exits 0.**
  `build.ps1:231-237` wraps the build in `if (Test-Path $moduleProject)` with no else and no assertion
  that the eight hardcoded paths at `:208-230` resolve. Rename
  `modules/BlinkingLed/BlinkingLed.csproj` and build.ps1 prints nothing;
  `Test-ModulePublishFreshness.ps1` globs `*.csproj` so it is happy; `Invoke-SelfTests.ps1:136` only
  `Test-Path`s the output DIRECTORY, so under the documented `run-gate.ps1 -SkipClean` the previous
  build's DLL is still there, loads, and the gate prints GATE PASSED. `Invoke-SelfTests.ps1:76-78`
  describes this hole without closing it.

- 📌 **Three module self-test assertions cannot fail, two of them mutation-proven.**
  `modules/BlinkingLed/BlinkingLedModule.cs:668` names "speaks when the user switches it off" but by
  that line the blinker is already stopped and a RATE quip is what gets recorded — delete the
  `Announce(...)` at `:207` and every check still passes, so the two strings the class doc calls the
  only thing this module ever says have no falsifiable coverage. `:647-659` builds `everyRateKnown`
  from two tautologies under a comment about typo'd names: rename `RateNames[3]` to "Normall" and the
  suite stays green while the pane offers an option that no longer resolves through the switch.
  `modules/AgentFlow/AgentFlowModule.cs:2451` asserts on "aboutAnswering", a string that occurs
  exactly once in the whole repo — inside the assertion — and is not a field id; it also asserts
  against `LoadPending`, which deliberately DOES contain Info rows, so naming a real id would fail.

- 📌 **`FortuneEngineProbe` asserts an Ordinal match against a culture-formatted number.**
  `modules/Fortunes/engine/FortuneEngineProbe.cs:186` looks for the literal `"12,345"` in a string
  `FortunesModule.Count` formats with `"N0"` and `CultureInfo.CurrentCulture`. On a de-DE machine that
  is "12.345", the assertion fails, and `--fortunes-engine-selftest` fails the whole gate for a reason
  unrelated to the code under test.

---

## Closed 2026-09-24 — ten module defects

- 📌 **Fortunes clears its staged pack selection before knowing whether the save worked.**
  `modules/Fortunes/FortunesModule.cs:648` calls `_stagedDisabled.Clear()` ahead of the caller seeing
  `ms.Save()`'s result, and nothing else holds the batch: `CompanionHost.GetSettings` returns a fresh
  `ModuleSettings` per call. Click "Select none" (158 ids staged) and Apply while an AV scanner holds
  settings.json: Save returns false, the dialog says so, and both staging maps are already empty.
  Apply again — nothing staged, nothing written, Save succeeds, `ok == true`, no warning, and all 158
  packs are still enabled. The user was told the second Apply worked.

- 📌 **BlinkingLed's "Blink once now" leaves the LED stuck lit with the feature switched off.**
  `_phaseOn` is the only record of whether the light is on, and `Stop()` relies on it
  (`modules/BlinkingLed/engine/ScrollLockBlinker.cs:155-174`). `BlinkOnce()` calls `Toggle()` and does
  not update it, so from the next tick the flag is the inverse of reality. Press it during a dark gap,
  then untick "Blink the Scroll Lock light" and Save: `Stop()` sees `_phaseOn == false`, skips its
  corrective toggle, and leaves the LED on — the exact state `Stop()`'s doc comment exists to prevent.
  At Glacial the dark window is 240s of 244, so it is near-certain rather than a coin flip. `BlinkOnce`
  is also not gated on `_running`.

- 📌 **Remembrance writes module settings from a thread-pool thread while Apply serialises them.**
  `modules/Remembrance/RemembranceModule.cs:915-933` fires `DownloadRecommendedModelAsync` and forgets
  it; its continuation calls `_settings.Set(...)` twice and `_settings.Save()` off the pool. The host's
  `ModuleSettings` (`src/dotNet/Plugins/CompanionHost.cs:825-845`) is a bare unsynchronised
  `Dictionary<string,string>`. Start a 14 GB model pull, edit the whisper-cli path, press Apply, and
  let the pull land during it: the UI thread is inside `Serialize(_d)` while the pull thread does
  `_d[key] = value`, the serializer throws "Collection was modified", `Save` swallows it and returns
  false, and Apply reports failure with the user's edits unpersisted. A concurrent write across a
  dictionary resize is the worse interleaving. `IcsUrlSource.FetchCore:47` has a milder version.

- 📌 **AiBrain's "Test connection" proves only that nothing threw.**
  `modules/AiBrain/AiBrainModule.cs:504-506` awaits `backend.ChatAsync(...)`, discards the result and
  prints `"connected · " + model + " OK"`. Both backends return `""` rather than throwing on a
  well-formed 200 that lacks the expected node — `OpenAiCompatBackend.ChatAsync:152-158` returns `""`
  when `choices` is null or empty. A moderated or quota-exhausted endpoint answering `{"choices":[]}`
  gets a green `connected · gpt-4o OK 0.9s` while every real ask returns nothing and the companion is
  mute. Assert the response is non-empty.

- 📌 **A saved vision model the filter rejects is silently blanked.**
  `modules/AiBrain/AiBrainModule.cs:813` adds every listed id to `seenIds` BEFORE the vision filter
  drops it at `:833`, so the union-it-back-in guard at `:839` returns false and does not. The doc
  comment above the method states the opposite as a "SAFETY INVARIANT". With OpenRouter and
  `CloudVisionModel = "google/gemini-flash-1.5"`, pressing "Refresh cloud models" renders the box
  empty; the next Apply for any unrelated field writes `CloudVisionModel = ""` (`:643` has no
  non-empty guard, unlike its local twin at `:616`), the constructor substitutes `"gemma3:4b"`, and
  every vision ask now names a model the provider does not have.

- 📌 **AiBrain's drop and poke responders claim the turn even when they decline it.**
  `modules/AiBrain/AiBrainModule.cs:968` and `:1019` call the fire-and-forget `Ask(...)` then
  `return true` unconditionally, while `Ask` has four further early returns they cannot see
  (`:1029-1043`). `PluginApi.cs:660` defines the chain as "highest priority first until one handler
  returns true", and this module registers at priority 10 to outrank Fortunes. A cold vision model
  takes ~40s (measured; `AiBrain.cs:189` records 52s); at t+35s the drop tick fires, the 30s cooldown
  has expired, `Ask` returns on `RequestInProgress`, `OnDrop` returns true, Fortunes is suppressed and
  the pet says nothing. Both responders carry comments saying declining beats going silent.

- 📌 **Every Remembrance transcription failure is reported as "Whisper is not configured".**
  `modules/Remembrance/Transcriber.cs:31-36` and `:71-88`: `RunWhisper` returns false for a non-zero
  exit, a missing output file, a missing WAV or any exception, and the captured stderr at `:77` is
  read and discarded, destroying the real reason. Adopt a truncated `ggml-base.en.bin` through "Browse
  for a model" (no size check there, unlike the installer's `MinimumModelBytes` gate); whisper-cli
  exits non-zero with "failed to load model"; the transcript says to configure Whisper while the same
  pane's Status line says "Whisper: configured", because `StatusLine` only tests `File.Exists`.

- 📌 **AgentFlow logs "spoke about X" when it did not speak, every 10 seconds, forever.**
  `modules/AgentFlow/AgentFlowModule.cs:1306-1336`. The line at `:1334` is written unconditionally
  after three notification channels, none of which must have fired; in Log mode nothing is spoken at
  all. The comment at `:1332` asserts the opposite, and `AnnounceScreenPrompt:1049` gets it right.
  Worse: the early returns at `:1306` and `:1312` gate the CHIME and ANIMATION on the SPEECH
  preconditions, so a user who turns app speech off but ticks "Play the notification sound" never gets
  one — contradicting `:1322` — and because the budget is deliberately not consumed on that path and
  `NotifyBudget.ShouldAnnounce` skips its cooldown while `_lastNotifyUtc == DateTime.MinValue`, the
  "deferred a notice" line repeats every 10s for as long as a prompt sits blocked.

- 📌 **AgentFlow's tray "Watching" row silently switches auto-approve off.**
  `modules/AgentFlow/AgentFlowModule.cs:2107-2117` renders the tick whenever `Enabled`, which
  `AgentMode.Scans` makes true for Notify, Log AND AutoApprove. Clicking the already-ticked row runs
  `SetEnabledFromTray(true)`, which writes `AgentMode.Notify` unconditionally (`:2269-2281`) — no log
  line and no spoken line, unlike `ToggleAutoApproveFromTray` which does both. From Log mode the same
  click silently starts the companion talking.

- 📌 **`LocalJsonSource` parses up to 2 MiB on the UI thread, 180 times an hour.**
  `modules/Reminder/LocalJsonSource.cs:19` implements `ICalendarSource` directly instead of deriving
  from `CachingCalendarSource`, which exists so a slow fetch never runs on the caller's thread;
  `IcsUrlSource` and `OutlookComSource` both derive from it. `CALENDAR-FEED.md` describes the path as
  a work-side exporter's output, i.e. typically a share, so when the VPN drops `File.ReadAllText`
  blocks on the SMB timeout with the pets frozen, once every 20 seconds. The same doc's claim that "a
  parse failure is non-fatal, the companion keeps the last good feed" is false here: last-good
  retention lives only in `CachingCalendarSource.DoRefresh`.

---

## Closed 2026-09-25 — five converter defects

Four were one-liners that had survived because nothing in the converter's own suite asked the
question they answer. The fifth was a licensing claim in source that was false in all three of its
halves, which matters more than its size in a repo whose redistribution posture rests on such
claims.

Two things are deliberately NOT done and are recorded here rather than left implied. The 31 shipped
pets were not re-converted, so they keep their current sprite sheets and the 36 wasted interior
tiles until somebody decides to re-convert; that changes 31 companion assets and their catalog
hashes, and is a download-churn decision rather than a side effect of a compositor fix. And the
`NextBehaviour` item shipped without a real-world British skin to demonstrate prevalence on, exactly
as its own entry warned: the code shape is unambiguous and now tested, the frequency in the wild is
still unmeasured.

- 📌 **One child-process drain is still the textbook deadlock.**
  `tools/ShimejiConvert.Engine/Engine.cs:269-272` does `ReadToEnd()` on stdout then stderr, then
  `WaitForExit(timeout)`. stdout only reaches EOF at exit, so the timeout is always called on a
  finished process and can never fire; once the child's stderr crosses the 4 KB pipe default while
  the parent blocks on stdout, both stop forever. `Engine.ProbeFfmpeg:310` additionally leaves the
  process running when its wait returns false.
  The Remembrance pair (`Transcriber.cs`, `WhisperInstaller.cs:713`) was fixed on 2026-09-24 and is
  covered by two source invariants in `tests/runtime-hardening-selftest.ps1`; this one belongs with
  the converter work and has not been done.

- 📌 **`PosesToComposite` draws every drag frame; `DragSwingFramesOf` references only `Poses[0]`.**
  `tools/ShimejiConvert.Engine/Emit/PetEmitter.cs:1008-1014` vs `:1052-1058`. Measured across the 31
  shipped converted pets: 174 unreferenced tiles, 138 of them legitimate grid-tail padding and 36
  interior waste in contiguous runs consistent with this (worst four pets at 4 each). Small, but it
  counts against the 1024-tile cap and the 12 MiB budget. Larger in kind for an Android bundle, where
  `BundleParser` builds one animation per action so a multi-frame drag collapses to a single frozen
  frame while all its other frames are still drawn into the sheet.

- 📌 **The vocabulary alias table carries four British spellings but not `NextBehaviour`.**
  `tools/ShimejiConvert.Engine/Shimeji/ShimejiParser.cs:105-125` aliases Behaviour,
  BehaviourReference, BehaviourList and NextBehaviourList. `NextBehavior` SINGULAR is the element that
  exists (7 occurrences in base-conf/behaviors.xml, zero `NextBehaviorList`), and the exclusion at
  `:259` checks for both forms — so the missing alias is the one that matters. A British-spelled conf
  inlining `<Behaviour Frequency="N">` inside `<NextBehaviour>` would have its transition-only weights
  counted as root selection frequencies, skewing every hub weight for that skin. Lower confidence than
  the rest: the code shape is unambiguous, but no such skin was available to demonstrate prevalence.

- 📌 **One diagnostic prints a BEL byte.** `tools/ShimejiConvert/Program.cs:179` writes the "Found no
  animations.xml under" message with a single backslash before `animations.xml`, and `\a` is the C#
  alert escape, so it emits BEL followed by "nimations.xml". All eight sibling call sites escape it
  correctly.

- 📌 **`SkinLayout.cs:93-96`'s docstring says this repo does not ship the base conf. It does.**
  Both halves of "cannot be converted here... is copyrighted and this repo does not ship it" are
  false: `base-conf/actions.xml` and `base-conf/behaviors.xml` are in the tree, `ParseBundledConf`
  loads them, and `Detect` returns skins with `UsesBundledConf = true`. Worth correcting given this
  repo's licensing sensitivity, because it is an in-source claim about what is and is not
  redistributed.
