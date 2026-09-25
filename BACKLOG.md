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
| [`docs/ISSUES-post-1.0.0.md`](docs/ISSUES-post-1.0.0.md) | every closed bug post-mortem, kept in full because several were wrong in instructive ways. It is the file that says how many there are; do not restate the count here |
| [`docs/BLOCKED.md`](docs/BLOCKED.md) | items that cannot be actioned from here, each with its blocker named on its own line |
| [`docs/IDEAS.md`](docs/IDEAS.md) | product ideas nobody has scoped, with their reasoning: wanted, unscheduled, and not engineering debt |
| [`docs/HISTORY-pre-1.0.0.md`](docs/HISTORY-pre-1.0.0.md), [`docs/ISSUES-pre-1.0.0.md`](docs/ISSUES-pre-1.0.0.md) | the same two records for the repository that preceded v1.0.0 |

**Conventions.**

- **Bugs are numbered `BUG-00N` and the number is never reused**, so a commit, a test or a code
  comment can cite one. `modules/AiBrain/`, `modules/PetStudio/`, `src/dotNet/`,
  `docs/RELEASE-CHECKLIST.md` and `handoff.md` all cite them today. **The next number to use is the
  one [`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md) names**, which is the single place it is
  written down; the gate asserts the two agree, because this line said BUG-005 for weeks after the
  register had moved on to BUG-009 and nothing noticed.
  **No bug is open right now** — the closed register moved to
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





- 📌 **Two 1.3.1 fixes are defended by structure rather than by an assertion, and both say so in
  CLOSES-WHEN: file-exists modules/AgentFlow/FakeCdpServer.cs
  their own comments.** `sawPanel` reporting false after a successful press needs a fake CDP server
  to test -- worth building, because it would also cover `Interpret`, `Parse` and the Codex click
  template, which are currently only asserted against recorded strings. And the port probe that made
  looking imply pressing was a value the *caller* computed, not a predicate anything could call; it
  now lives in `ShouldProbePort()`, which takes no `autoApprove` argument, so reintroducing the bug
  means adding a parameter to a documented decision rather than dropping a word into a condition.


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


## Open: left by the 1.1.6 options-ABI cycle (2026-09-18/19)

The ABI additions, the shared notification sound and the AgentFlow pane rebuild each left something
that was flagged rather than fixed. None of these blocked the work; all of them are things a future
reader would otherwise have to rediscover.

- ⬜ **`SchemaShellPane.RefreshAfterApply` scans for `Info` fields only, not `Header`.** A `Header`
  whose paragraph is derived from settings will show stale text after Apply until the pane is
  reopened. AgentFlow's three explanation headers are static prose, so nothing is wrong today; the
  first `Header` carrying live state will hit it.
- ⬜ **A junction part way along a path is now resolved, a symlink test still is not.** Containment
  uses `GetFinalPathNameByHandle`, which handles both — but the symlink assertion reports DEGRADED
  on an account that cannot create one, and this account cannot. The junction case covers the
  property; the symlink case is an extra that only runs where Developer Mode is on.

### Left open by the three parallel audits (2026-09-19)

Nine findings were fixed in the same cycle (the Audio permission, the second tray row, the poll
re-entrancy guard, `Log` mode, the version policy, the auto-mode greying, `EnabledWhen` trimming,
four resource-lifetime defects and three dead members). These are the ones left.

- ⬜ **The notification sound decodes on the UI thread, every time.** Up to 8 MiB read plus a decode
  into two ~21 MB LOH allocations, deliberately uncached (caching a large pick would be worse). A
  user who picks a 30-second WAV gets a UI stall per notice.

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

## Open: follow-ups from the Disposition audition (aibrain 2026-09-11)

The feature shipped — "Show me 5 examples" and "5 about my screen" beside the Disposition dropdown,
verified end to end against a live `gemma3:4b` by driving the real pane through UI Automation. That
record, and how the five predicted constraints were answered, is in
[`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md). Two threads it left open:

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

## Post-v1 backlog (added 2026-07-29)

### Open, found 2026-09-01 while chasing companion behaviour


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

- 📌 **17 animation names remain unclassifiable, and no data here can settle them.**
  NO CLOSES-WHEN, deliberately: nothing in this repo can answer it. 13 of the 17 are absent from the
  2778-archive harvest and 4 are names the corpus disputes with itself, so closing this needs source
  confs that do not exist here. It had carried `grep-present tools/ShimejiConvert/Program.cs
  "SkinLayout census"` since the day it was filed -- a string that has never appeared in that file, or
  anywhere in the repo except this entry -- so the criterion could not fire whatever happened to the
  code. Replacing it with a grep that DOES match only moved the lie: the gate immediately reported the
  item closeable while the 17 names were still unresolved. An item that cannot be machine-checked says
  so. Corrected 2026-09-24.
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

**Where this stands as of 2026-09-24.** The converter-gap cluster that used to head this section is
done, and the "not attempted" column with it. It said
`Jumping` was unattempted in 11 of the 12 desktop skins and `Resisting` in 11. Measured 2026-09-24
from the residue reports of a fresh conversion: across the 13 desktop pets, 1592 source actions
account as 538 emitted, 89 dropped, 374 degraded and **0 not attempted**. The column was 34 before
83d5eaf admitted the frame-playing embedded classes, 5 after it, and 0 once set-pieces converted as
chains. Nothing in this table is a converter gap any more; what remains genuinely needs host or
format work.

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
| the converter's own unattempted mappings | 0 — superseded | the 2026-09-25 census measures the not-attempted column at **zero** across all 13 desktop pets; the four names below are what the OLD residue report said, kept only so the earlier figure is traceable |

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

---

## Open: four read-only audits of the whole tree (filed 2026-09-24)

Four agents read `src/` (~38k lines), `modules/` (~43k), `tools/` (~9k) and the 27 PowerShell
scripts, with one rule: verify before filing, because this repo's own history says roughly a quarter
of previously filed items were already fixed. That rule earned itself — see the refuted list near the
end of this section.

**Seven findings were fixed in the same cycle and are not listed here**: the class-based jump landing
edges (45213e0), the drag/fall magic ids (b8c3b04), the Remembrance purge (9ad10cf), both Companion
Studio save targets (43ee2cb), the settings-window `File.Exists` (47d0872), and the backlog gate's own
open-item blindness plus the bug-number drift.

### A feature reports success while doing nothing

- 📌 **The composited tile is 2 rows shorter than the tallest source sprite, so its bottom is lost.**
  Measured while publishing Zim on 2026-09-25: all 55 source sprites are 130x130, the emitted tile is
  162x128, and the tallest frames lose their bottom 2 rows. On `shime41.png` that is 43 of 6669
  non-transparent pixels, **0.64%** -- the very bottom of the boots. Sub-perceptual at any size the pet
  is actually drawn, which is why it is filed rather than fixed, and why it should not be fixed by
  eyeballing: the tile width (162) is larger than the source (130), so the height is not a naive crop
  but falls out of the anchor-aligned bounding box, and whatever is off by 2 there will be off for
  every pet. CLOSES-WHEN: a converted pet's per-frame alpha-pixel count matches its source within 0.
  Do NOT re-convert the shipped pets to collect this alone. Scope, since two entries disagreed on
  it: `Companions/` holds **32** `shimeji-*` directories, and the catalog lists **54** companions in
  total; a re-convert touches the converted ones, so it is 32 assets and their catalog hashes for
  0.64%, not 54 and not 31.

### Blocking IO, pipe deadlocks, and measured cost

- 📌 **`FallDetect`/`RiseDetect` enumerate every top-level window, per pet, per tick.**
  `src/dotNet/FormCompanion.cs:1571` and `:1661` each allocate a dictionary, a fresh
  `EnumWindowsProc`, and a `StringBuilder(128)` + `GetWindowText` + `GetTitleBarInfo` per visible
  window. With 16 pets falling at once that is 16 identical, pet-independent enumerations per tick.
  The precedent is in the neighbouring file: `StartUp.BlockedMonitorsForStandDown:791` exists because
  the fullscreen z-order walk used to be per-pet, and its comment carries the measurement ("679
  top-level windows... 53 walks/s became 3.3").

  ⚠ **`DesktopWindows.Snapshot` is NOT the drop-in this entry used to claim** (checked 2026-09-25,
  before attempting it). Three semantic differences, each of which would change pet physics rather
  than just speed it up:
  it EXCLUDES pet handles, and `FallDetect` deliberately wants them (`"Sheep windows doesn't have a
  title bar, but we want detect if another pet is present"`); it truncates at `MaximumWindows`,
  where a missed window means a pet falls through a surface it should land on; and it reports
  `VisualBounds` (the DWM extended frame) where these two use `GetWindowRect`, so landing lines
  would shift by the shadow margin.
  The shape that does work is the one `BlockedMonitorsForStandDown` already uses: keep this
  enumeration's own semantics exactly, and share ONE pass per tick across pets behind a timestamp,
  with each pet skipping its own handle at consumption instead of during the walk. Deliberately not
  attempted at the tail of a long session: it is core fall/rise physics, and verifying it needs
  several pets falling at once, which is not something a self-test can stage.

- 📌 **`minHostVersion` is parsed, written to modules.json, then dropped from the catalog.**
  `packaging/New-ContentCatalog.ps1:181-190` copies id/name/desc/version/url/sha256/bytes/permissions
  and not `minHostVersion`; `grep -c minHostVersion catalog.json` is 0 against 5 in modules.json, and
  `src/dotNet/RemoteCatalog.cs:42-53` has no such field, so a catalog carrying it would be ignored
  anyway. AgentFlow declares `MinHostVersion = "1.2.0"`; every user still on 1.1.x is offered it in
  the Modules pane, downloads the payload, and `ModuleHost.cs:79-86` refuses it at load.
  `New-ModulePublish.ps1:128-133` carries the comment naming this outcome. Its first-publish branch
  (`:218-224`) never sets the key, and nothing checks parity with source, which has already drifted
  (`AiBrainModule.cs:140` declares "1.1.0"; the aibrain and reminder entries have no key).

### VS Code setup, AgentFlow

- 📌 **`ReadPort` accepts an unquoted JSON number, which VS Code ignores.**
  `modules/AgentFlow/VsCodeSetup.cs:113-131`. VS Code's handler takes only `true`/`"true"`/a non-empty
  string, so `"remote-debugging-port": 9321` is valid JSON, read as a valid port, and silently ignored
  at launch. The class doc at `:57-61` names exactly this as "the kind of failure that looks like
  success". Nothing detects it, so the pane says "argv.json asks for port 9321, but nothing is
  answering there... it needs a restart" forever.

- 📌 **"Disable (undo changes)" can delete a commented-out key and report the live one removed.**
  `modules/AgentFlow/VsCodeSetup.cs:167-186` and `:189-203`: `FindKeySpan` searches the
  comment-STRIPPED text but returns only the needle, and `WithPort`/`WithoutPort` then locate it with
  `IndexOf` on the ORIGINAL. `IndexOfTopLevelBrace` does the careful offset-mapping a few lines away,
  which is what makes this an oversight. With a commented-out `remote-debugging-port` line above the
  live key, undo deletes the comment, sees the text change, and reports success while the
  unauthenticated loopback port keeps opening on every launch.

- 📌 **`WatchCodex` defaults to false on a rationale the 1.3.0 work made false.**
  `modules/AgentFlow/AgentFlowModule.cs:1505-1527`'s comment says `ReadCodex` "never looks at
  `turn_context`. So Mode stays null, every Codex session resolves as `unknown`". `FoldCodexRecord`
  now dispatches `turn_context` and reads `approval_policy`, and `BlockedDetector` has a dedicated
  Codex branch keyed on `on-request`. The pane's own `aboutCodex` text already describes the fixed
  behaviour, so the pane advertises a capability that ships switched off for a reason that is gone.

- 📌 **Approval timestamps are stamped with the scan time, not the call's.**
  `modules/AgentFlow/BlockedDetector.cs:337` sets `WhenLocal = DateTime.Now`. On the first tick after
  launch the whole transcript is folded at once, so the pane's "Last ten, newest first" card shows ten
  entries all bearing the launch minute for commands that ran up to 15 minutes earlier.
  `OutstandingCall.StartedUtc` is already carried and would be accurate.

### Smaller, verified, grouped

- 📌 `modules/Fortunes/engine/SmartFortunes.cs:382` — `Pick` rescans the 64-slot top-K array per pool
  entry without carrying the running minimum, on the UI thread beside a full ONNX inference.
- 📌 `modules/Fortunes/FortunesModule.cs:1272-1280` — when `Embedder.IsReady` is false, `WarmCore`
  returns with all counters zero and `SmartStatusFor` has no way to say "stood down", so the picker
  reads "Indexing N fortunes in the background" forever.
- 📌 `modules/PetStudio/PetStudioWindow.cs:809` — the `SpriteKey` cache guards the small decode while
  `PetAnalyzer.Analyze` unconditionally base64-decodes the sheet and builds `tilesX * tilesY` GDI+
  bitmaps plus a fresh `XmlSchemaSet` compile on every 750 ms debounce (~850 KB and 304 bitmaps for
  `blue_sheep`). `RenderCensus` also re-runs `ClassifyAll` over the list `RenderMap` just cached.
- 📌 `modules/AiBrain/AiBrainModule.cs:587` — `LoadPaneValues` fills the display-only `vramStatus` via
  `RunningModelsAsync(...).GetAwaiter().GetResult()`, blocking the WPF UI thread up to ~2 s per pane
  open, and three pane actions carry `ReloadPaneAfter = true`. Also builds a fresh `HttpClient` per
  call.
- 📌 `modules/AiBrain/engine/AiSettings.cs:384` — `Save()` uses the 10,000 ms cross-session budget;
  the bounded `SaveWithin` its own doc says "UI callers use" has exactly one caller, a self-test.
- 📌 `modules/AiBrain/AiBrainModule.cs:904` and `:928` — the two "Refresh models" actions can be in
  flight together (the host disables only the clicked button) and both mutate `_localModels`,
  `_cloudModels` and `_modelIdByLabel` from pool threads with no synchronisation.
### Measured 2026-09-25: the positive-probability warning IS user-visible

- ✅ **FIXED 2026-09-25 in content.** `Companions/pink_sheep/animations.xml` states 173
  (`king_jump_top`) and 182 (`king_jumpB_top`) each gained
  `<next probability="100" only="taskbar">166</next>`, which is the pet's own convention twice over:
  `king_walk_top` (167), the same `_top` family, already answers the taskbar with exactly that line,
  and both 173 and 182 already answered `only="window"` with the same target, `king_slamB` (166). A
  window top and the taskbar are both horizontal surfaces this pet lands on, and it already knew what
  to do on one of them.

  The host option was NOT taken. Raising `TASKBAR | HORIZONTAL` at the call site would have fixed all
  six states and every pet at once, and made every `only="horizontal"` edge in all 54 companions newly
  eligible at the taskbar, changing landing behaviour for pets that are not broken. The enum's own
  `HORIZONTAL_ = 0x06` (WINDOW|HORIZONTAL, deliberately excluding TASKBAR) says the distinction was
  intended.

  The measurement that drove it, kept because it is the only evidence that exists: the owner's
  installed 1.2.4 log carried **335 occurrences over ~16 hours**, about 21/hour and 36% of the whole
  diagnostic log, **all** from `pink_sheep` and **269 of them** these two states at the taskbar. The
  two converted pets installed alongside produced **zero**, so this was never a converter problem.
  Consequence at `FormCompanion.cs:1246`: no eligible transition sets `bLeavingScreen = true`, and a
  sprite fully outside the monitor is respawned, so the pet walked off the bottom and reappeared.

  **Confirmed in the real app, 2026-09-25, with a positive control.** A 45-minute run of the FIXED
  pet recorded **3** warnings, and all three were `state 44, where=130` -- one of the states this
  change does not touch. States 173 and 182 at the taskbar recorded **zero**. The control is the
  part that makes the zero mean something: the detector demonstrably fired three times during that
  very run, so a zero for the fixed pair is a real zero rather than a dead harness. At the measured
  rate of about 17/hour for those two states, 45 minutes expected roughly 13.

  An earlier 12-minute run of the UNFIXED pet recorded zero and was discarded rather than reported:
  at ~21/hour a 12-minute window expects about three events and observing none is ordinary, so it
  was evidence of nothing either way. That is why `tests/companion-border-invariants.ps1` asserts
  the property instead, and why it is the check in the gate rather than a soak.

  (The soak logs the pet as `eSheep` because `localxml=` sets no folder id. It is pink_sheep's XML:
  state 44 with 7 declared candidates at `where=130` matches the owner's log entry exactly.)

  ⚠ **The remaining 66 occurrences are NOT fixed and are a different shape**: states 44, 114, 81
  and 122 at `where=130` and `where=18`, which are window-edge situations rather than the taskbar.
  Filed below rather than left implied. The soak above saw state 44 three times in 45 minutes,
  about 4/hour, against 2.3/hour in the owner's 16-hour log: the same order, still happening.

- 📌 **Four `pink_sheep` states cannot answer a border at `where=130` or `where=18`.** The
  residue of the 407 measurement above: 37 at state 44, 25 at state 114, 3 at state 81, 1 at state
  122, over the same 16 hours. Together they are 66 of the 335, about 4/hour. Not investigated beyond
  counting, because the taskbar pair was 80% of the volume and is the one with a named, visible
  consequence. Worth doing the same analysis: name each state, check whether a sibling in its own
  family already declares the missing situation, and prefer the content fix if one does.
  CLOSES-WHEN: a soak of `pink_sheep` records zero `no eligible positive-probability` warnings.
### Filed 2026-09-25 by the four parallel re-audits

Verified before filing. The three code audits raised 39 findings between them; what is here is what
survived a second check, minus the 14 fixed on the day in `e31c5bb`.

**Host, user-visible**

- 📌 **Pressing Apply in Preferences wipes the diagnostic-log mute for any module that is
  installed but not loaded.** `src/Portable/Wpf/OptionsShell.cs:464` calls `CollectMutedModules`
  unconditionally, and that rebuilds the whole string from `LoadedModules` only. A module whose `Init`
  throws is on disk, not loaded, and contributes nothing, so its stored mute is dropped. The same
  method takes explicit care NOT to do this for `defaultSpeakingCompanion` (`:477`) and `triggerSpeech`
  (`:485`), both commented as leaving the saved choice alone.
- 📌 **Two Preferences windows can edit one settings.json, and the later Apply wins.**
  `src/dotNet/ContextMenus.cs:593` writes `isOptionLoaded` and nothing reads it (`:570`, in
  `About_Click`, is the only read). So About-while-About, About-while-Options and Options-while-About
  are blocked and Options-while-Options is not. `ShowDialog` does not stop the tray callback or the
  module-update balloon (`StartUp.cs:1799`), and each window Applies from a `values` dictionary
  captured when its pane was built.
- 📌 **A future-schema settings file blocks every write for the session in silence, while the
  sibling failure state warns.** `src/Portable/AppSettingsStore.cs:938-943`: the read-only-fallback path
  sets `LastLoadWarning` and `Program.cs:251` surfaces it; the `FutureSchema` path sets
  `_writesBlockedByFutureSchema` and returns without a warning, yet `SaveMerged` then returns false for
  every write. Compounding it, several immediate-persist controls discard that false and report success
  anyway: `OptionsShell.cs:644-646` and `CompanionsPaneControl.cs:370`, `:426`, `:496`. The sibling
  `ApplyNotificationSoundChoice` (`:854`) reads the value back precisely to avoid this.
- 📌 **"Reset to default settings" leaves most of the page it claims to reset untouched, and says
  nothing.** `src/Portable/Wpf/OptionsShell.cs:864-912` against the schema at `:297-354`: never reset are
  `monthlyModuleUpdateCheck`, `companionUpdateCheck`, `appUpdateCheck`, `diagLog`, `diagLogKb`,
  `diagLogKeep`, every generated `diagCat_*` and `diagMod_*`, and `defaultSpeakingCompanion`. The prompt
  says "Reset all preferences on this page to their defaults?" and the status line is left blank.
- 📌 **"Fetch the catalog when the pane opens" is discarded on every open but the first in any 90
  second window.** `ModulesPaneControl.cs:104` and `CompanionsPaneControl.cs:128` call
  `RefreshCatalogOnOpen()` from the CONSTRUCTOR; on a warm shared catalog `FetchSharedAsync` completes
  synchronously, so the continuation runs inline and immediately hits `if (... || !IsLoaded) return;`,
  which the constructor guarantees is false. No update button appears for any module that has one until
  the user presses "Check for modules online".
- 📌 **The gravity branch respawns the pet mid-tick and then lets the rest of the tick clobber it.**
  `src/dotNet/FormCompanion.cs:1416` and `:1440` set `bNewAnimation = true` and fall through after
  `SetNextGravityAnimation` returned -1 and `Play(false)` already picked a fresh spawn position and
  possibly a different `DisplayIndex`. `monitorBounds`/`workArea` were captured from the OLD monitor at
  the top of the method, so the clip block computes against the wrong work area. The sequence-end path
  at `:1398` does `Play(false); return;` for exactly this reason; the gravity path has no return.
- 📌 **The pet sound decode cache is never evicted.** `src/dotNet/AudioOutput.cs:34` is a
  `Dictionary<byte[], float[]>` keyed by reference identity, cleared only in `Dispose` (`:383`). Add then
  Remove a companion type repeatedly and each staging produces fresh arrays whose cache entries can never
  be hit again and are never freed, holding both the MP3 and the decoded 44.1 kHz stereo float buffer
  (about 7x the MP3). `PlayOwned`'s own doc at `:119-121` states this retention as the reason module
  audio is deliberately not cached; the pet path has the same property and no bound.

**Modules**

- 📌 **AgentFlow decodes WebSocket frames one chunk at a time.**
  `modules/AgentFlow/CdpApprover.cs:806` calls `Encoding.UTF8.GetString(buffer, 0, result.Count)` per
  `ReceiveAsync` with a 16 KB buffer, so a multi-byte sequence straddling the boundary decodes to U+FFFD
  on both sides; a `System.Text.Decoder` carried across the loop is the fix. Corruption inside an option
  label makes `PromptOptions.Classify` see an unrecognised option, and one unknown option refuses the
  whole prompt (`PromptOptions.cs:479-489`). Same method: `if (builder.Length > Cap) break;` at `:810`
  abandons the rest of the message in the socket, so every later reply on that session is offset by one.
  Caveat from the audit: the read expressions themselves return compact JSON, so reachability depends on
  CDP events rather than on the replies this file asks for; the decoding defect is unconditional.
- 📌 **AgentFlow's setup cache is never invalidated by the three actions that change what it
  describes.** `AgentFlowModule.cs:1957-1966` inspects only when the cache is null, and
  `EnableCdpAsync` (`:1991`), `DisableCdpAsync` (`:2044`) and `BrowseForArgvAsync` (`:2077`) never clear
  it. Its doc says "the cache is refreshed by the tick", which holds in every mode except Off, where
  `OnTick` returns at `:521` before the probe. So in Off mode "Check now" keeps reporting the pre-write
  answer for the rest of the session.
- 📌 **Remembrance's Remote Desktop warning describes behaviour the code no longer has.**
  `RemembranceModule.cs:692` ends "(Device dropdowns are read at startup; restart there to populate
  them.)" — false since `RefreshDynamicOptions` was wired into the pane's `Load` (`:486-495`, `:609-612`),
  which the host re-runs on every pane build. Reopening the options pane is enough.
- 📌 **Fortunes clears a settings key nothing has ever read.** `FortunesModule.cs:1211` does
  `ms.Set("spicyTier", "")` so "a stale value can never be re-migrated", but `MigrateContentLevel`
  (`:399`) reads only `spicyFortunes` and `spicyOnly`, and `spicyTier` appears exactly once in the repo:
  at this write.

**Converter and build**

- 📌 **A skin with no `Type="Move"` action always fails the converter's own acceptance bar.**
  `tools/ShimejiConvert.Engine/Emit/PetEmitter.cs:186` emits the synthesised `turn` unconditionally, and
  `:1567` is its only inbound edge, guarded by `if (loco)`. `ConversionResult.Accepted` requires
  `Graph.Unreachable.Count == 0`. Measured by the audit on a three-action fixture: `unreachable=1`
  (`turn`), `accepted=False`, CLI exit 1, on a pet that is otherwise valid and playable. The emitter
  already handles the structurally identical ceiling case at `:140` and the wall case via
  `SynthesiseClimbIfNeeded`; `turn` got no equivalent. Bites hand-trimmed and single-pose skins, not the
  shipped corpus, which always carries a Walk.
- 📌 **Two copies of the same `Has(blob, token)` helper disagree on case, so an action merely NAMED
  with a capital "Cursor" is emitted as a gaze.** `ActionClassifier.cs:18` uses `StringComparison.Ordinal`;
  `PetEmitter.cs:627` uses `OrdinalIgnoreCase`; both run over the same `SubtreeBlob` (which includes the
  action's `Name`) with the same `"cursor"` literal. Measured: `classify` reports `LookAtCursor` as
  Group1 with no cursor state seen, and the emitted XML still carries `<action>faceCursor</action>`, with
  `0 dropped, 0 degraded` in the residue. Two knock-ons beyond the stray tag: `VariantFor` switches to the
  last-unconditional-variant rule instead of `Animations[0]`, and `CollapseDirectionPairs` refuses to
  merge it with an identical non-gaze sibling because `IsGaze` is in the match key. Whether any of the 31
  shipped conversions hit this is unknown: the source confs are deliberately not in the repo.
- 📌 **The dwebp 30 second timeout cannot fire for the case it exists to catch.**
  `tools/ShimejiConvert.Engine/Shimeji/WebPLoader.cs:264-269` does
  `p.StandardOutput.BaseStream.CopyTo(outBytes)` before `p.WaitForExit(30000)`, and the copy blocks until
  stdout hits EOF, which for dwebp means process exit. A dwebp that hangs without closing stdout hangs
  the converter permanently. Same site uses `p.Kill()` rather than `Kill(true)`. The hardening self-test
  pins drain ORDER for three named files only, and its repo-wide loop checks encoding, never order.
- 📌 **PetStudio runs the whole conversion on the WPF UI thread.**
  `modules/PetStudio/PetStudioWindow.cs:667`, `:699`, `:720` call `ZipFile.ExtractToDirectory`,
  `BundleConverter.ConvertBundle` and `ShimejiEngine.ConvertSkin` inline from the click handler. Named
  costs: `SpriteSheetBuilder.Build` can run up to 8 full composite + PNG-encode + base64 passes over a
  sheet as large as 4096x4096 before giving up on the 12 MiB budget, and with ffmpeg on PATH `SoundBaker`
  spawns one ffmpeg per unique clip (30 s cap each, up to 64) plus one recursive `EnumerateFiles` of the
  skin root per distinct clip name. `runtime-hardening-selftest.ps1:110-115` asserts the "never extract on
  the UI thread" rule against `ModulesPaneControl.cs` only, so this site is outside every check.
- 📌 **`build.ps1` declares `#requires -Version 5` and its `-Zip` path calls a script that
  declares `-Version 7`.** `packaging/New-DeterministicPortableZip.ps1:12`, reached from `build.ps1:271`.
  Under 5.1 the full Release build and all eight module builds complete, then packaging dies on a
  `#requires` error. `Readme.md:511` documents `.\build.ps1 -Release -Zip` with no shell requirement. The
  parity block checks each script in isolation and by construction cannot see a cross-script requirement.
  Same transitively for `New-ModulePublish.ps1` and `New-ModuleDistZip.ps1`.

**Checks that cannot fail (the category this repo keeps finding)**

- 📌 **The `-Encoding` parity check does not assert the property its own failure message claims.**
  `tests/runtime-hardening-selftest.ps1:1336-1338` says it "pins -Encoding on every file write, so both
  shells emit the same bytes", and only asserts that a parameter matching `Enc*` is present.
  `-Encoding UTF8` is UTF-8 WITH BOM on 5.1 and WITHOUT on 7, so it passes and still emits different
  bytes; measured on this box, `powershell.exe ... -Encoding UTF8` produced `239,187,191,97`. Secondary:
  no tracked `.ps1` currently calls `Set-Content`/`Add-Content`/`Out-File` at all, so today it iterates an
  empty command set across all 31 scripts. Stronger assertion: require `utf8NoBOM`/`utf8BOM`/`ascii` and
  reject bare `UTF8`.
- 📌 **The repo-wide redirect scan is blind to any redirect not written as an object initialiser.**
  `tests/runtime-hardening-selftest.ps1:87-96` admits a file on a text match, then slices only
  `new ... ProcessStartInfo(.*?)\}\s*;`. A file assigning `psi.RedirectStandardOutput = true;` outside an
  initialiser contributes zero sites and zero offenders. Currently latent — all 13 redirect assignments
  are inside initialisers and the `-ge 6` floor catches total collapse — but it is the same hole one level
  out from the per-FILE one the file's own comment describes fixing.
- 📌 **`release.yml`'s prune has a catch that cannot catch and a success line that cannot fail.**
  `.github/workflows/release.yml:234-237`: `$ErrorActionPreference = 'Continue'` at `:232` means a
  non-zero `gh release list` is not terminating, so the `catch` never runs; `$releases` is empty, the loop
  body never executes, and `:243` prints "Kept the 3 most recent releases." over a prune that did nothing.
  Benign in effect, indistinguishable in the log.
- 📌 **`ContentCatalogAssets.ps1:190-192` drains stdout to EOF before reading stderr, has no wait
  timeout, and leaks a `Process` per asset.** Practically unreachable as a deadlock (`git cat-file`'s error
  output is one short line), so this is shape rather than a live bug — but the gate calls it once per
  catalog asset, 219 today, and neither the `Process` nor the `MemoryStream` is disposed.

**Dead code, verified across `src/`, `modules/` and `tools/`**

- 📌 **`src/dotNet/WindowTheme.cs` is ~140 dead lines.** Only `IsDark()` (`:46`) has a caller, from
  `WpfTheme.cs:42`. `Apply`, `ApplyTitleBar`, `ThemeTree`, `ThemeControl`, `DarkenNativeControl`, both
  P/Invokes and six of the seven colour constants have none — residue of the WinForms dialogs retired in
  S5b-3. Also dead: `CompanionHost.SpeechSourceModuleIds` (`:430-439`, one repo-wide hit, its own
  declaration), the two-argument `ProcessIcon.TaskbarWatcher` overload (`:396`), `LocalData.GetScale()`
  (`:68`), `LocalData.GetPetSizeLevel(string)` (`:195`), `LocalData.GetEffectivePetScaleFactor(string)`
  (`:215`), the unread `folder` parameter of `LocalData.SetXml` (`:857`, and `StartUp.cs:216-218` computes
  a value to pass to it), `AppSettingsStore.FilePath`/`BackupPath`/`IsReadOnlyFallback`/`LastRecoveryFile`
  (`:832-836`, the last assigned at `:1294` and never read, so the corrupt-file preservation path records
  where it put the file and nothing can report it), the `AppPaths` vector-cache cluster (`:64`, `:113`,
  `:125`, `:142`), `OptionsWindow._dirty` (`:25`), `NotifyBudget.Forget`
  (`modules/AgentFlow/NotifyBudget.cs:177-182`, superseded by `Retain`), and
  `SmartFortunes.LastCandidateCount` (`:116`, `:126`, written on every contextual pick at `:511` and never
  read — its own doc says it exists "because a number nobody can read is a number nobody checks").
  ⚠ Not dead, do not remove: `AiBrain.ScreenChanged` (`AiBrain.cs:1106-1121`, ~45 lines with
  `ComputeSignature` and `_lastFrameSignature`) has no callers but is DECLARED kept —
  `AiSessionManager.cs:215-220` says the idle timer that used it is gone and the primitive is deliberately
  retained for a future change-detection option.
- 📌 **`PetEmitter`'s `roundUp: true` branch is unreachable and its doc describes a caller that does
  not exist.** `:2588-2597`; all four call sites pass `false` (`:1264`, `:1356` via the 3-arg overload that
  hard-codes it at `:2581`, `:1489`, and `tools/ShimejiConvert/Program.cs:1084`). The `<param>` doc says
  "Used for rests, where undershooting is the thing that reads as wrong", and the rest call site passes
  `false` with a comment that contradicts it. Same family as the three deletions in `1b65d64`.
- 📌 **`FormCompanion.cs:1983`'s `if (rctO.Top == 0 && rctO.Bottom == 0) return false;` is
  unreachable** — the guard eight lines above returns false when `rctO.Bottom <= rctO.Top`, which subsumes
  it.

**Optimisation, costs named rather than timed**

- 📌 **A full XML DOM parse per companion card, per pane rebuild, on the UI thread.**
  `src/Portable/Wpf/CompanionsPaneControl.cs:910-935` (`LoadPetHeaderIcon`, reached per card at `:269`).
  `CompanionThumbnails.GetPng` caches the bundled zip, but its MISS path is not cached, and every imported
  Shimeji skin, converted pet and locally authored pet misses — each costing a `File.ReadAllText` plus a
  full `XDocument.Parse` of `animations.xml` (hornet is 406 KB, esheep64 158 KB). `Reload()` runs from the
  constructor and after every Use/Add/Remove/Download/Uninstall, and the control is rebuilt on every pane
  selection. `GetStats` in the same file keeps `_statsCache` for exactly this reason.
- 📌 **Remembrance enumerates the WASAPI endpoint list four times per options-pane open where two
  would do.** `RemembranceModule.cs:488-491` and `:680-681`, both from the same `Load` closure at
  `:609-632`: `RefreshDynamicOptions` calls `RenderDevices()` and `CaptureDevices()`, then `StatusLine`
  calls both again purely to count them. Each constructs an `MMDeviceEnumerator`, enumerates active
  endpoints and reads `FriendlyName` off every device's property store, on the UI thread.

### Checked and REFUTED — do not re-file

Recorded so the next audit does not spend the time again. AgentFlow's shell-header template does NOT
swallow the glob/grep rows: the real bundle string is "Allow this glob command" with no trailing
question mark, extracted from the installed extension's `webview/index.js`, so the `EndsWith`
alternative is false and the table row wins. `SavePaneValues` skipping only `about`/`hdr` prefixes is
belt-and-braces: the host registers no reader for Info or Header, so `Collect()` never sends them.
`RuleLoader.DefaultPaths` is not missing a managed tier — `~/.claude/remote-settings.json` IS it.
Fortunes does NOT pay a 34 MB SHA-256 per Apply; `Embedder.AssetFingerprint` is a `Lazy<string>` with
`ExecutionAndPublication`, so it is once per process. Module RELOAD leaks (tray items and options
panes cannot be unregistered; AiBrain's stale handler would throw off a disposed `_lifetime`) are real
against the ABI but latent: `ModuleHost.LoadFrom` is called once (`StartUp.cs:269`) and `ShutdownAll`
only from `Dispose`, so the shipped host has no runtime reload path. `FormCompanion.cs:2057` compares
a window title to lowercase "sheep" while the form's `Text` is "Sheep", so that disjunct can never be
true — but the first disjunct covers the same case and no behavioural difference exists.
PowerShell 5.1/7 divergence is clean as SOURCE: all 27 scripts parse under 5.1.26100, no
`-AsHashtable`, no `Get-WmiObject`, no `-Encoding Byte`, and the three scripts whose output genuinely
differs carry `#requires -Version 7`. No unguarded destructive operation exists in any of the 27;
every `Remove-Item -Recurse -Force` is GUID-scoped scratch or routed through the safe-delete helpers.

### One thread left open

- ⬜ **Can a converted pet express a repeat that never terminates?**
  `modules/PetStudio/BehaviourChain.cs:362` copies the source animation's `Sequence.RepeatCount`
  verbatim, and `RepeatCount` is an evaluated expression (`src/dotNet/Xml.cs:292`, `GetXMLCompute`)
  rather than a plain integer. If a pet can express an unbounded or negative result, a chain would
  stall on that step. Settling it needs a read of the repeat countdown in `Animations.cs` plus
  `CompanionXmlValidator.ValidateExpression:528` to see what the XSD permits.
