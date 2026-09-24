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

## Open: AgentFlow post-publish audit (2026-09-21)

Twenty findings. **Twelve are fixed and shipped in agentflow 1.3.1 + host 1.2.3**; see the commit
`AgentFlow 1.3.1: fix nine defects the post-publish audit found` for what each one was and 7/7
mutation results. What is left is below. The audit was worth more than the feature it audited:
four defects were in code committed the same day, and two made a shipped version note false.

**The lesson worth keeping, above any individual item.** Two findings were *unreachable code behind
a shipped claim* -- the Notify-mode screen watch and, in a different way, the `sawPanel` report.
Neither a green suite nor a live smoke test found them, because both were reached only in a mode
nobody was exercising. Ask what input reaches a branch, not whether the branch looks right.

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

- 📌 **Two 1.3.1 fixes are defended by structure rather than by an assertion, and both say so in
  CLOSES-WHEN: file-exists modules/AgentFlow/FakeCdpServer.cs
  their own comments.** `sawPanel` reporting false after a successful press needs a fake CDP server
  to test -- worth building, because it would also cover `Interpret`, `Parse` and the Codex click
  template, which are currently only asserted against recorded strings. And the port probe that made
  looking imply pressing was a value the *caller* computed, not a predicate anything could call; it
  now lives in `ShouldProbePort()`, which takes no `autoApprove` argument, so reintroducing the bug
  means adding a parameter to a documented decision rather than dropping a word into a condition.

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

## 🚢 AgentFlow — PUBLISHED (notify half, 2026-09-17)

**Status: published as 1.0.0 and live in the catalog.** `modules/AgentFlow/` is built by `build.ps1`,
`tests/Invoke-SelfTests.ps1` fails if its folder is missing from the build output (`$RequiredModules`,
read by both the gate and CI), `--module-selftest=agentflow` runs in both, and
`modules-dist/agentflow.zip` plus `catalog.json` now offer it to every user.

Verified in the real install before publishing, which is a different claim from "the self-test
passes": host 1.1.5 installed over 1.1.4 by MSI, the module folder copied into the install's
`modules\agentflow\`, and the module found three concurrent live sessions in the maintainer's real
transcript store and stood down on all three because none was in `default` mode. 79 assertions pass
when the INSTALLED host loads it through the real loader.

**To see it speak you need a session in `default` permission mode**, blocked longer than the
threshold. In `auto`, `acceptEdits` or `plan` it stands down by design, because measured precision
outside `default` is ~0.4%.

This heading said "research only, nothing built" until 2026-09-17, six commits after the module
landed, while the same section already said "Built, 25/25 self-test" further down. Recorded because
status words in this file are load-bearing and that one contradicted itself -- and then it was wrong
in the other direction for half a day, reading "not published" after the publish.

Read [`docs/agentflow/README.md`](docs/agentflow/README.md) before proposing work — it carries the
measurements, and several obvious designs are ruled out by numbers rather than opinion. Five
runnable harnesses live beside it.

**What remains open, in order:**

- 📌 **Default-mode precision is unmeasured, and it is the only number still missing.** Recall is
  settled at 93% (28/30 against calls a permission rule actually blocked). Precision in `default`
  cannot be measured on this box: 120 transcripts contain **zero** rule-caused denials in that mode,
  because this machine runs `auto`. Generate it the only way it can be generated — work normally in
  default mode for an hour — then rerun `agentflow_join.py`, which now reports the split itself.
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

- ✅ **CLOSED 2026-09-22: done, and this entry outlived it by a day.** The closing criterion the
  entry set was "read `turn_context.approval_policy` and treat `never` as cannot-prompt". That
  shipped in 1.3.0: `TranscriptReader.cs:382` matches the `turn_context` record, `:388` reads
  `approval_policy`, and `BlockedDetector.cs:103`/`:179` stand down on anything that is not
  `on-request`. The entry's "do NOT map `collaboration_mode.mode`" warning is honoured and the
  reason is recorded in the code comment at `TranscriptReader.cs:384-386`.

  Third stale entry found in two days, all three fixed by later work in the cycle that filed them,
  none linking back. The others were the 1px sprite line and the host-event blind spots.

## Open: left by the 1.1.6 options-ABI cycle (2026-09-18/19)

The ABI additions, the shared notification sound and the AgentFlow pane rebuild each left something
that was flagged rather than fixed. None of these blocked the work; all of them are things a future
reader would otherwise have to rediscover.

- 📌 **`RevealsPath` containment is data-root wide, not module-storage narrow.** `PaneView` receives
  CLOSES-WHEN: grep-present src/dotNet/Plugins/CompanionHost.cs "ModuleOwningPane"
  an `OptionsPane` with no module identity, so the host can only enforce "inside the app data root".
  `CompanionHost.ModuleDataDir` builds every module's storage as `<dataRoot>\modules\<id>`, so today
  that is arithmetically the same rule — but it means module A can reveal a file sitting in module
  B's folder, or in the app's own settings folder. Narrowing it needs a module id on `OptionsPane`,
  which is a contract change and therefore a host release.
- ⬜ **`SchemaShellPane.RefreshAfterApply` scans for `Info` fields only, not `Header`.** A `Header`
  whose paragraph is derived from settings will show stale text after Apply until the pane is
  reopened. AgentFlow's three explanation headers are static prose, so nothing is wrong today; the
  first `Header` carrying live state will hit it.
- ✅ **CLOSED 2026-09-21: the owner listened and it is fine.** The measurements (0.75 s, peak
  0.7, silence at both ends) never could settle this one, because "pleasant" is not a property a
  test can assert. The only instrument for it was a person with ears, and that was always the
  cheapest item on this list to close.
- ⬜ **A junction part way along a path is now resolved, a symlink test still is not.** Containment
  uses `GetFinalPathNameByHandle`, which handles both — but the symlink assertion reports DEGRADED
  on an account that cannot create one, and this account cannot. The junction case covers the
  property; the symlink case is an extra that only runs where Developer Mode is on.

### Left open by the three parallel audits (2026-09-19)

Nine findings were fixed in the same cycle (the Audio permission, the second tray row, the poll
re-entrancy guard, `Log` mode, the version policy, the auto-mode greying, `EnabledWhen` trimming,
four resource-lifetime defects and three dead members). These are the ones left.

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
- ⬜ **The notification sound decodes on the UI thread, every time.** Up to 8 MiB read plus a decode
  into two ~21 MB LOH allocations, deliberately uncached (caching a large pick would be worse). A
  user who picks a 30-second WAV gets a UI stall per notice.
- ⬜ **`DescribeNotificationSound` does a `File.Exists` on the UI thread** on every Preferences open
  CLOSES-WHEN: grep-absent src/Portable/Wpf/OptionsShell.cs "System.IO.File.Exists(path)"
  and now after every Apply. Harmless for the default; blocks on a disconnected UNC path.
- ✅ **CLOSED 1.1.9.** `Load` is removed and the assertions drive `LoadPending`, which is what the
  host drives. `MinHostVersion` is 1.2.0, so no host that can load this module took the old path.
  A test exercising a path production does not take is worse than no test, because it reads like
  coverage.

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

## Open: findings from the v1.1.0 wrap-up audit (filed 2026-09-10)

Four parallel read-only audits ran over the tree at the v1.1.0 tag (credentials, PII/employer material,
stale files, readme accuracy). Credentials came back with **zero** findings and the working tree was
clean. One residual remains open; the rest are closed and in
[`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md).

### 📌 Nothing scans a shipped DLL for an embedded build path, so the fix has no regression net
  CLOSES-WHEN: grep-present packaging/Test-ModulePublishFreshness.ps1 "CodeView"

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
  CLOSES-WHEN: grep-present src/dotNet/AudioOutput.cs "DuckWhileBubbleUp"
  v1.6.0 module-audio ABI: that work added per-owner input tracking, and its own entry records why it
  stopped there — it "changes how the app sounds, so it wants its own decision and a setting". The
  full entry is in [`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md).

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

## Post-v1 backlog (added 2026-07-29)

### Open, found 2026-09-01 while chasing companion behaviour

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

  ⚠ **The 2026-09-21 scoping that called this a project was WRONG, in two specific ways, and both
  claims were load-bearing. Corrected 2026-09-22 after the owner said "I have seen Hornet jump
  monitors a few times".** The owner was right. Keep the corrections; do not restore the originals.

  **Wrong claim 1: the per-tick clamp is not a containment invariant.** The scoping said
  `FormCompanion.cs:1438-1449` "clamps the position into `workArea` every tick, so traversal means
  deleting the invariant the other 15 decisions are written against." That clamp allows **8192px of
  slack on every side** (`Animations.cs:14`, `:103-119`) and its own doc comment calls it an
  integer-overflow guard, not containment. It is also skipped entirely on drag and window-follow
  ticks, which `return` before reaching it. The real containment invariant is two `if` statements:
  `FormCompanion.cs:1077` and `:1128`, the border turns.

  **Wrong claim 2: the pet-XML contract is not a blocker, and it was named as THE blocker.**
  `screenW`/`screenH`/`areaW`/`areaH` are already resolved PER MONITOR, from
  `Screen.AllScreens[screenIndex]` (`Xml.cs:424-429`), and `CurrentAnimation.UpdateValues(DisplayIndex)`
  re-parses them whenever a pet is re-homed -- which `EndDrag` and `RelocateToDisplay` already do
  today. A handoff-at-the-boundary design keeps the contract exactly as it is: no ABI change, no
  third-party pack breakage, no second coordinate system. The concern was true of a
  virtual-desktop design and irrelevant to the one that fits this code.

  **What actually exists.** Walking between monitors does not. RELOCATION does, four ways, and two
  of them ignore the "Allow multiple screens" setting entirely (whose default is off) -- which is
  why the owner saw it with the setting off:

  | Path | Kind | Honours the setting? | Re-homes `DisplayIndex`? |
  |---|---|---|---|
  | Respawn re-rolls the screen (`Play`, `:615-629`) | teleport | yes | yes, randomly |
  | Drag and drop (`EndDrag`, `:2179-2200`) | move + re-home | **no**, gate commented out at `:2184` | yes, by pet centre |
  | Fullscreen stand-down (`RelocateToDisplay`, `:1938-1946`) | teleport | **no check at all** | yes, nearest free |
  | Window-follow (`FollowWindow`, `:1973-1974`) | **continuous** | **no** | **no** |

  Note the respawn trigger is live, not theoretical: `SetNewAnimationCore` calls `Play(false)` when
  the transition graph dead-ends (`:819-821`), which is the `no eligible positive-probability
  transition` burst recorded further down this file.

  **Owner decision 2026-09-22: the behaviour stays, the label gets fixed.** A drag is an explicit
  user action and fullscreen stand-down is a get-out-of-the-way safety behaviour; neither should be
  gated. `OptionsShell.cs:302` currently promises companions "stay on the one they appear on",
  which is false, and becomes a statement about where they SPAWN.

  **So this splits into four, none of them XL:**
  - **23a** Re-home `DisplayIndex` after `FollowWindow`, reusing the loop already in `EndDrag`. This
    is a LIVE BUG: a pet riding a window to monitor B keeps resolving every physics decision against
    monitor A's `workArea`, and the 8192px slack is the only reason it is not obviously broken.
    Note "a companion on the wrong monitor" is one of the four user-found bugs listed under the live
    smoke-test entry in this file.
  - **23b** Nothing to do, per the owner decision above.
  - **23c** Fix the false label at `OptionsShell.cs:302`.
  - **23d** Real traversal: an adjacency function in `DesktopGeometry` beside
    `ChooseRelocationTarget` (pure rectangle geometry, trivially testable), then hand off at
    `:1077`/`:1128` instead of turning -- set `DisplayIndex`, call `UpdateValues`, translate the
    position. The floor discontinuity on this box (3440x1440 beside 2560x1080, floors 360px apart)
    resolves by falling, and `AnimationFall` already exists.

- ⬜ **The pet XML validator's positive-probability guarantee does not survive the runtime
  eligibility filter, and live pets hit it.** `CompanionXmlValidator.cs:672` refuses any pet whose
  transition set sums to zero probability, which reads as a guarantee that a companion can always
  pick a next animation. `Animations.cs:1005-1018` then filters by `TNextAnimation.Eligible(anim.only,
  where)` BEFORE summing, so a state whose every transition is conditioned on a `where` the
  companion is not currently in has zero eligible weight, logs `no eligible positive-probability
  transition`, and returns -1. Found 2026-09-21 in the maintainer's own diagnostics: bursts of 2 to 3
  at 17:18, 17:54, 17:55 and 18:11 during ordinary use, with two pets on screen.
  **What is NOT known, and should be measured before fixing:** what the companion does after the -1,
  whether the bursts correlate with a specific pet or a specific transition (the log line carries
  neither, which is its own defect), and whether the user can see anything wrong. It may be
  invisible and harmless. The cheap first step is to put the pet type and the state id in that
  warning, then look again -- a warning that cannot identify its subject cannot be acted on.
  The validator could also be strengthened to require positive eligible weight per `where` bucket,
  which would reject some currently-accepted third-party pets, so that is a decision not a fix.

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

- 📌 **67 animation names remain unclassifiable. Most of them are correct.**
  CLOSES-WHEN: grep-present tools/ShimejiConvert/Program.cs "SkinLayout census"
  Filed 2026-09-22, then largely resolved the same day when the owner pointed at the harvested bundle
  corpus (`D:\.ai-work\shimeji-catalog`, 2778 archives). `reloop` now takes an optional bundles
  directory and builds an action-name -> Type census across it, which answered 1990 names the bundled
  conf does not carry and corrected 14 more animations across 7 pets, Hornet's `Grapple1` among them.
  Only UNANIMOUS names count; the corpus disputes 96 and those stay unresolved.
  ⚠ **The count went 17 -> 67 the same day, and that is the migration reaching FURTHER, not
  regressing.** A second defect was reported: `Bouncing` juggling two frames for ~11s with no
  self-edge at all, stretched by `restsplit`'s velocity-based idea of a "performance". So the entry
  condition widened from "self-loops" to "self-loops OR carries a repeat count", which surfaces
  every animation with a dwell whose name cannot be resolved -- mostly `Stay` holds and `Move`
  travel that are SUPPOSED to have one. The number is a reporting surface, not a defect count.
  What is left, and why none of it is urgent:
  - **13 names absent from the corpus** (`climb_ceiling` x18, `descend` x15, `jump_down` x10,
    `grab_wall`, `grab_ceiling`, `climb_wall`, `climb_wall_descend`, `walk_with_ie`, `walk_stick`,
    `happy_walk`, `fall_`, `motion_3`, `motion_9`). These come from generator-made skins whose confs are
    not in the harvest. Read them and they are almost all travel or holds, which are MEANT to loop.
  - **4 names the corpus disputes with itself** (`Shock` 7/13 Stay, `crawl` 7/13, `idle` 2/4, `ずりずり`
    634/656 Move). A majority is not evidence and acting on one would be guessing with extra steps.
  ⚠ **Two of this entry's earlier claims were wrong and are kept here because the corrections are the
  useful part.** First: `jump` x10 is NOT a jump-detection bug. Measured across all 20 `jump`/`jump_down`
  animations, not one rises (`jump` is vy0=0 travelling -10px/frame), so `Launches()` had nothing to
  detect; the likely cause is the documented flattening at `PetEmitter.cs:1266`. Second: the Japanese and
  English stock confs do NOT disagree about `転ぶ`. The engine's own vocabulary table maps `固定` to
  **Animate** (`静止` is Stay), so both call Tripping a performance, and Cartman's `転ぶ` was corrected
  along with every other one.
  A third route exists if the remaining 17 ever matter: `SkinLayout` could match a pet to its source
  archive structurally rather than by title, which resolved only 7 of 25 pets and is why the census
  exists at all.

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
### Module SDK follow-ups

- 📌 **`ModulePermissions` cannot disclose input monitoring or process
  CLOSES-WHEN: grep-present src/DesktopAICompanion.Contracts/PluginApi.cs "ProcessList"
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

### Feature ideas (queued, not yet scoped)

**Moved to [`docs/IDEAS.md`](docs/IDEAS.md) on 2026-09-17, numbers intact** (16 per-companion
speech personality, 17 the Remembrance listenable-output gap, 18 the tray-utility ports): they are
product ideas nobody has scoped, and this file holds work that can be picked up. Ideas 1 to 15 stay
closed in [`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md). One piece of idea 18 did NOT
move, because it is engineering debt rather than an idea: the `ModulePermissions` under-disclosure
in a shipped module, which is now filed under "Module SDK follow-ups" above.

---

## Open: converter fidelity across the shimeji corpus (filed 2026-09-24, scoped 2026-09-25)

⚠ **Filed first as a Zim-only item; a corpus census on 2026-09-25 showed it is not one.** The
residue report is written beside each converted `animations.xml` and was never kept, so nothing
here recorded what any of the 31 converted companions lost. Re-derived by re-running the real
converter against every original source bundle; the reports are kept at
`D:\.ai-work\shimeji-catalog\work\census\` (`census.md`, `census.json`, `residue\*.txt`).

**30 of 31 censused** (Gengar failed on a multi-skin archive, a harness limit, not a defect):

| source | companions | dropped | degraded | not attempted |
|---|---|---|---|---|
| shimeji.org Android bundles | 18 | 0 | 0 | 0 |
| desktop Shimeji-EE skins | 12 | 82 | 365 | 34 |

The 18 zeros are genuine and were checked as a distinct claim, because a clean sweep of zeros is
exactly what a broken harness produces — the first run of this census did precisely that by
invoking `convertroot`, which writes the XML but no residue file. Those bundles report **0
state-dependent conditions** against 14–23 for the desktop skins: shimeji.org flattens them
upstream, so there is nothing conditional left to lose. Every desktop-sourced companion lost
actions, 6–16 dropped and 26–34 degraded each.

**447 lost actions, but four root causes, and one dominates:**

| cluster | actions | note |
|---|---|---|
| window-relative navigation (`activeIE`) | 353 | 79% of the total; needs window geometry the runtime does not expose |
| cursor-position branching | 54 | needs `cursorX`/`selfX` conditions the format lacks |
| breed autonomous sibling | 24 | genuine format limit, `<child>` auto-closes |
| interact / transform / anchor | 16 | mostly two-shimeji `Interact` and `ScanMove` |

📌 **Do the converter-gap cluster first — it is the only one where the capability already
exists.** `Jumping` is unattempted in 11 of the 12 desktop skins and `Resisting` in 11; the
residue report names these "a converter gap rather than a format limit". One change adds roughly
22 animations across nearly every desktop companion. Everything else needs host or format work.

⚠ A hypothesis that did NOT survive, recorded so nobody re-runs it: the 18 Android companions
were NOT built from the poorer source while a richer desktop zip sat in the catalog. Only 2 of 18
have a findable desktop counterpart and neither is better (45 vs 41, 36 vs 37, both on shared
template configs). Weak evidence rather than proof — about half the 1786 desktop bundles ship
under a generic `img/Shimeji/` folder and carry no character name to match on.

### The Zim conversion specifically


📌 A Shimeji skin for Zim converted and was accepted on the first run: 26 animations, valid,
round-trips, 0 unreachable. Verified in the real app via `localxml=`, not just by the validator.
The converter reported **6 actions dropped and 33 degraded**, and those 39 are the work.

**They are four problems, not thirty-nine**, which is the part worth carrying into the estimate.
Parsed from the residue file rather than tallied by hand:

| cluster | count | tractability |
|---|---|---|
| window-relative navigation (`activeIE.*`) | 31 (27 degraded + 4 dropped) | needs host-side window geometry the format does not expose |
| cursor-position branching (`cursorX`/`selfX`) | 6, all degraded | needs condition support the format lacks; one is a near-miss |
| breeding an autonomous sibling (`Breed`) | 2, both dropped | genuine format limit — `<child>` auto-closes; treat as out of scope |
| the converter's own unattempted mappings | 4 (`Jumping`, `Falling2`, `Resisting`, `Resisting2`) | **start here** — the residue report itself calls this a converter gap, not a format limit |

⚠ Before writing "needs a host change" for cluster 1, grep `PluginApi.cs` for the verb, per the
convention above. That exact sentence already cost one planning cycle in this repo.

The full per-action lists, the reproduce commands, and the two fidelity caveats that are *not* in
the 39 (the converter's fixed ~48px jump height, and 60 actions using script-computed values) are in
`D:\.ai-work\shimeji-catalog\work\zim\HANDOFF.md`, beside the converted XML and its residue
report. Kept out of this repo on purpose: see below.

⚠ **Nothing about this is in the repo yet, and publishing is undecided.** The asset is a third
party's sprite art of a character its owners hold, sourced from shimeji.org, which records **no
author** for it — so the source-specific evidence `Companions/README.md` asks for (exact bytes,
authorship, licence, attribution, redistribution scope) cannot be assembled for these bytes. That is
a maintainer decision, not an engineering blocker. Fidelity work can proceed on the local copy
regardless of how it lands.

---

## Open: rightsize the "Jesus Our Lord" companion (filed 2026-09-24)

📌 `Companions/shimeji-brq51bkr` (`name` "Jesus Our Lord", author `shimeji.org`, source
<https://shimeji.org/u/brq51bkr>) needs rightsizing.

⚠ **Recorded as requested, with the intent NOT specified** — ask before acting rather than guessing.
It could mean on-screen scale, the sprite-sheet cell size, the 4.7 MB file, or the tray icon.

The geometry is already known, so do not go re-derive it. `tilesx` 9 and `tilesy` 8 are confirmed
read out of the file today. The downscale-bleed entry above (search `tile 88`) records the sheet as
2304x2048, which puts cells at exactly **256px — the `MaximumSpriteFrameDimension` cap**, and notes
that `ScalePolicy.FitFactorForFrameD` caps the factor at 1.0 for a 256px cell, so at 100% or above
this pet never takes the downscale path.

⚠ That matters for scoping: if the complaint is "too big" or "too small" on screen, it is therefore
**not** a sprite-resolution problem, because the art is already at the ceiling. It would be the
runtime staging cap or the scale policy, and raising the former costs up to 4x sprite memory per
companion and would also need `MaximumGeneratedBytes` raised — a change the shimeji work
deliberately deferred once already. Confirm which way the complaint points before touching either.
