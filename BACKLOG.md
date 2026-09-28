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





- ✅ **FIXED 2026-09-27 (agentflow 1.4.9).** `modules/AgentFlow/FakeCdpServer.cs` is a loopback CDP
  endpoint speaking only what `CdpApprover` asks for: `/json/list`, `/json/version`,
  `Target.attach/detachFromTarget` and `Runtime.evaluate`. Eleven WIRE assertions now drive `Sweep`
  end to end, covering the part that recorded strings cannot reach — target discovery, the
  handshake, and what a whole pass concludes.

  **`sawPanel` is proven, not argued.** Reverting the fix (`{ pressed = note; break; }` back to
  `return note;`) and rebuilding gives *"FAIL: WIRE sawPanel stays TRUE after a successful press"*;
  with the fix, PASS. That was the item's specific ask, and it is a control-flow property AFTER a
  press, so no recorded-string test could have reached it.

  ⚠ Two of my assertions were wrong on the first run, in the dangerous direction: I asserted that
  an unreachable target makes `Sweep` return null — silence. It does not. It returns a note saying
  it cannot see inside the panel AND that this differs from nothing waiting, which is BUG-006's whole
  subject. My version would have passed on a build that went quiet again. Corrected to assert the
  note.

  The fake sends an EVENT before every reply on purpose: the real socket carries target lifecycle
  notifications, and `CdpSession` matches replies by id precisely because a reader taking the next
  message would sometimes read an event. A fake that only replied would let that back in silently.
**Status: published as 1.0.0 and live in the catalog.** `modules/AgentFlow/` is built by `build.ps1`,
`tests/Invoke-SelfTests.ps1` fails if its folder is missing from the build output (`$RequiredModules`,
read by both the gate and CI), and `modules-dist/agentflow.zip` plus `catalog.json` now offer it to
every user.

⚠ **This paragraph used to claim `--module-selftest=agentflow` "runs in both".** It ran in neither:
no `.ps1` and no `.yml` in the repo contained that string, so AgentFlow's 478 assertions executed
only when someone typed the flag by hand. Corrected and made true for the GATE on 2026-09-27 by
`tests/Test-ModuleSelfTests.ps1`, which runs all eight modules (~15s) and is wired into
`run-gate.ps1`. **CI still does not run it** — CI calls the underlying scripts rather than
`run-gate.ps1`, so wiring it there is a separate change, and claiming it before making it is how
this line went wrong the first time. Filed below.

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


- ✅ **PARTLY COVERED 2026-09-27, and the rest DECLINED with the reason.** Two source invariants
  now assert the properties that actually regress, by ORDER: the Run button is wired to `Run()`, and
  a chain that will not build RETURNS before `_runDebugPet` is reached. Mutation-tested —
  disconnecting the button and moving the spawn above the guard each exit 1 naming the assertion;
  the unmutated file exits 0.

  ⚠ **This is not the click-through test the entry asked for, and does not pretend to be.** It
  catches the button being disconnected and the failure path spawning anyway. It cannot catch
  anything that only shows up on screen.

  Driving the real button was declined rather than attempted a fourth time. The three obstacles this
  entry recorded are still true — no way to drive the tray from a test, previews auto-hide under a
  fullscreen foreground window, and an isolated `DESKTOP_AI_COMPANION_DATA_ROOT` falling back to
  eSheep — and a WPF pane needs an STA thread and a Dispatcher inside the module self-test, which is
  the flakiness those three attempts already hit. A deterministic check of the two real failure modes
  is worth more than a fourth flaky one.
- ✅ **CLOSED 2026-09-27. Three of its four parts were already done or decided; the fourth is a
  feature and has moved to `docs/IDEAS.md`.**

  - **23a — DONE, and it was the live bug in this entry.** `FormCompanion.cs:1455` calls
    `AdoptScreenUnderPet()` after a successful `FollowWindow()`, so a pet riding a window to another
    monitor no longer keeps resolving its physics against the old screen's `workArea`. Fixed
    2026-09-22 in `27ab8e8` ("Four backlog fixes: the monitor desync, a false label, two silent
    give-ups"); the entry was never updated. Verified by reading the code, not the commit message.
  - **23b — nothing to do**, per the owner decision of 2026-09-22: a drag is an explicit user
    action and fullscreen stand-down is a get-out-of-the-way safety behaviour, so neither should be
    gated by the setting.
  - **23c — DONE**, same commit. `OptionsShell.cs:377` now reads "Let companions spawn on any
    screen", and the false parenthetical "(they stay on the one they appear on)" is gone, with the
    reason recorded beside it.
  - **23d — MOVED to `docs/IDEAS.md`** ("A companion should be able to WALK between monitors").
    Real traversal is wanted and scoped, but nothing is broken without it, which is the line this
    repo draws between the backlog and IDEAS. The two refuted blockers moved with it so they are not
    re-raised.
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

### Blocking IO, pipe deadlocks, and measured cost

- ✅ **DECLINED-MEASURED 2026-09-27. The cost is real and small; the proposed fix does not transfer
  from its precedent.**

  **Measured on this box**, replicating what the detectors do per call (EnumWindows +
  `IsWindowVisible` + `GetWindowText` + `GetWindowRect` on each visible window, 40 reps warm):
  **664 top-level windows, 44 visible, 0.60 ms per walk.** So the cost is 0.60 ms per falling or
  rising pet per tick — not per pet per tick generally, since neither detector runs outside a
  fall or a rise.

  **Why the precedent does not carry over.** `BlockedMonitorsForStandDown` shares one scan behind a
  **300 ms** window, and that is safe because fullscreen state changes slowly. A window RECT does
  not: 300 ms of staleness during a window drag puts a pet's landing line where the window used to
  be. The window short enough to be safe for landing detection is roughly one frame, and pets do not
  tick together — every `FormCompanion` owns its own `timer1` — so a frame-length window collapses
  only the pets that happen to tick inside it, not all of them.

  **What would make it worth revisiting**, so the next session has the number rather than deriving it
  again: a measured frame-time problem with many pets falling at once. At 0.60 ms a walk and a ~40 ms
  tick, one falling pet costs about 1.5% of a core; sixteen cost about 24%. Below that this is
  trading landing accuracy for a fraction of a millisecond, and nothing has been reported.

  ⚠ One detail worth keeping if anyone does build it: each detector excludes its OWN window
  (`if (hWnd == Handle) return true;`) but deliberately KEEPS other pets — *"Sheep windows doesn't
  have a title bar, but we want detect if another pet is present"*. A shared snapshot must therefore
  include every pet window and let each consumer skip its own handle. Getting that backwards lets a
  pet land on itself.
- ✅ **FIXED 2026-09-27, and half of this item had already gone stale.** `New-ContentCatalog.ps1`
  copies `minHostVersion` now, `RemoteCatalog.cs` has the field and bounds it like `Version` beside
  it, and `BuildAvailableRow` asks the SAME predicate the loader uses —
  `ModuleHostRequirement.IsSatisfied` — so the pane and the loader cannot drift into disagreeing
  about one module. An unrunnable module keeps its card and its permissions line; the Install button
  is disabled and carries the reason ("Needs a newer app: needs host 1.2.0 or newer (this host is
  1.1.4)"), because a greyed control with no explanation reads as a bug.

  ⚠ **Stale half, not re-fixed because it is no longer true:** the item said "the aibrain and
  reminder entries have no key" and that source had drifted. Measured 2026-09-27: all 7 entries in
  `modules.json` carry the key and all 7 match their source declaration. Only the catalog COPY was
  missing.

  Absent `minHostVersion` stays supported — an empty requirement is satisfied by every host, so an
  older catalog behaves exactly as before, and the helper is permissive for an unparseable host
  version too. Checked that this does not disable Install for everyone: host `ProductVersion` is
  `"1.2.5"`, parseable, and above agentflow's 1.2.0.

  Three source invariants assert the BINDING rather than the call, because a file that asks
  `IsSatisfied`, ignores the answer and sets `IsEnabled = true` would satisfy any presence check
  while behaving exactly as before. Mutation-tested: "ignore the answer" and "never ask" both exit 1.
### VS Code setup, AgentFlow

### Smaller, verified, grouped

- ✅ **CLOSED-VERIFIED 2026-09-27: already gone, and not by this item.** The 64-slot top-K
  insertion no longer exists. It was removed by the relevance-band change (the owner chose "relevance
  band, not a fixed count" over a fixed `TopK = 64`), and the replacement says so in its own comment:
  *"This also drops the old top-K insertion, which rescanned all K slots for the current minimum on
  every one of the n candidates: O(n*K) comparisons on top of the n dot products. One array of n
  floats is 31 KB at the owner's pool size and makes the pass O(n), so a wider candidate set is now
  CHEAPER than the narrow one it replaces."* Verified by reading `Pick`, not by trusting that comment.
- ✅ **FIXED 2026-09-27 (fortunes 1.0.10).** `SmartFortunes.StoodDown` records the stand-down in
  STATE, cleared at the start of every warm so a rebuild after the runtime is fixed reports progress
  again rather than a stale stand-down. `SmartStatusFor` takes it and answers "Smart picks are
  unavailable on this machine — the text engine could not start, so fortunes are chosen at random.
  Everything else works normally.", checked BEFORE the progress lines because a stand-down is
  terminal and they are not.

  Worth naming why the existing `Say()` was not already the fix: it writes to the diagnostic log,
  which the person looking at the Fortunes pane is not reading. They saw a progress message and
  waited. Asserted on both halves — the status must say what is wrong AND must not still claim work
  is happening — because a message that merely mentions the problem while reading "Indexing" would
  pass a looser check. Positive control with the branch removed: *"FAIL: a stood-down smart index
  says so instead of claiming to be indexing"*.
- ✅ **PARTLY FIXED, MOSTLY REFUTED 2026-09-27 (petstudio 1.1.10).** One real finding in three.

  **FIXED — the double classification.** `RenderMap` classified the whole pet so each chip's badge
  could read the result, then `RenderCensus` → `Census` classified it again. Twice per analyze, on a
  ~750 ms debounce while typing. `Census` now takes an optional precomputed map.

  **REFUTED — `PetAnalyzer.Analyze` does not decode the sheet.** It copies the base64 STRING into
  the report; the decode is in `PetSprite.TryDecode`, which the `SpriteKey` cache already guards. The
  entry had this backwards, describing the cache as guarding a small decode while a large one ran
  unguarded beside it. There is no second decode.

  **DECLINED-MEASURED — the `XmlSchemaSet` compile.** Real, but it lives in
  `CompanionXmlValidator.TryParse` (host code, every caller), not in PetStudio, and it costs
  **0.95 ms per call** measured warm over 30 reps against `Resources/animations.xsd`. Caching it means
  sharing an `XmlSchemaSet` — which Microsoft documents as not thread-safe — across every
  pet-loading path in the app, to save under a millisecond on a 750 ms debounce.
- ✅ **FIXED 2026-09-27 (aibrain 1.1.11), and this entry UNDERSTATED it.** "Up to ~2 s" reads as a
  worst case. Measured with the mechanism `OllamaClient` actually uses (HttpClient, 2 s deadline,
  literal `127.0.0.1` so no DNS): server running **5–56 ms**, server **REFUSED 2005–2008 ms**, host
  unreachable **2010 ms**. A refused localhost connection does not fail fast — it burns the entire
  deadline and returns `TaskCanceledException`. So the 2 seconds was not the rare hung-server case;
  it was what every user WITHOUT Ollama running paid, on every pane open, with three pane actions
  that rebuild the pane.

  The pane serves the last known answer and refreshes behind it, showing "Checking what is
  resident…" on the first open rather than freezing while it finds out. At most one probe in flight,
  so three rebuilds cannot open three sockets that each sit for two seconds against a server that is
  not there. The fresh-`HttpClient`-per-call half of this entry falls out of the same change: one
  client per 5 s instead of one per pane open.
- ✅ **FIXED 2026-09-27 (aibrain 1.1.10).** The three UI call sites pass
  `AiSettings.UiSaveBudgetMilliseconds` (1500 ms) — far beyond an uncontended save, which is
  immediate, and beyond two instances overlapping, while staying inside what reads as a responsive
  click. `ToggleEnabled` also stops swallowing the outcome: it was
  `try { s.Save(); } catch { }`, so a toggle that did not persist looked exactly like one that did
  and reverted on the next launch. It logs the failure now, which matters more with a bounded budget
  where a contended save can legitimately return false.

  The existing "rejects a held lock promptly" check could not have caught this: it passes a literal
  125 ms, so it passed for months while every caller used 10,000. The new assertion is tied to the
  CONSTANT the callers pass. Positive control with it set back to 10000: *"FAIL: the UI save budget
  gives up fast enough to keep the settings window responsive"*.
- ✅ **FIXED 2026-09-27 (aibrain 1.1.9).** `_localModels`, `_cloudModels` and `_modelIdByLabel` are
  guarded by `_modelsLock`. Each refresh holds it around "replace the list, then rebuild the options
  from it", so that update is atomic rather than merely thread-safe in pieces; Monitor is reentrant,
  so the finer locks in `FormatModelLabel` and `ResolveModelId` nest safely.

  ⚠ `LoadPaneValues` deliberately does NOT hold the lock across its whole span. The first attempt
  did, which put `VramStatusLine`'s blocking model query (up to ~2 s against a local Ollama) inside
  the critical section, where a pane open would stall both refresh actions — a worse bug than the
  race. It snapshots both lists under the lock and formats outside it.

  No automated regression test, deliberately: reproducing the race needs two live endpoints and the
  methods are private, and a source-text check that the fields "appear near a lock" is the kind that
  cannot meaningfully fail. Verified by build (0 warnings) and `--aibrain-selftest` RESULT=PASS.
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

- ✅ **CLOSED 2026-09-26, and the item was WRONG about what it found.** These four
  (`jump_down` 44, `blastoffb` 114, `chaseb2` 81, `king_walk` 122) do lack an `only="window"` edge,
  but the host does NOT treat that as a fault and never did. `where=130` is
  `WINDOW | WINDOW_BOTTOM`, a pet rising into a window's underside, and `FormCompanion.cs:1292`
  answers a -1 by giving the handle back so the pet carries on rising, with the comment "Nothing
  wants to hang there." `where=18` is `WINDOW | WINDOW_LEFT`, and `:1126` answers a -1 by releasing
  the grip or dropping the handle so gravity takes over. Both outcomes are designed, documented and
  correct. Adding edges would have CHANGED behaviour -- making the pet hang where it currently
  passes through -- on the strength of a log line that was not reporting a defect.

  The real defect was the WARNING. It logged at `warning` for a designed outcome, which is why this
  one line was **36% of a 16-hour diagnostic log** and why the 269 occurrences that genuinely
  mattered (the taskbar pair, since fixed) sat in the same undifferentiated pile as the 66 that did
  not. `SetNextBorderAnimation` now takes `absenceIsNormal`, because only the caller knows what a -1
  costs: at the taskbar it means `bLeavingScreen` and the pet walks off the screen; at a window edge
  it means nobody wanted to grip there. The three window-grip sites pass `true` and log at `info`
  with the reason attached.

  ⚠ The two `WINDOW_TOP` sites are equally graceful but still log at `warning`. They accounted for
  **zero** of the measured occurrences, and they use the two-argument overload, so marking them would
  have meant a third overload written on speculation. Left deliberately.
### Filed 2026-09-25 by the four parallel re-audits

Verified before filing. The three code audits raised 39 findings between them; what is here is what
survived a second check, minus the 14 fixed on the day in `e31c5bb`.

**Host, user-visible**

**Modules**

- ✅ **FIXED 2026-09-27 (remembrance 1.0.12).** The warning now says the device lists refresh
  every time the pane opens and that reopening on the console is enough, with no restart. Re-verified
  against the code before changing the text rather than trusting the item: `Load` at
  `RemembranceModule.cs:661-664` runs `AutoDetectWhisperOnce`, `AutoDiscoverModelsOnce` and
  `RefreshDynamicOptions` before returning values, and the host calls `Load` on every pane build.
  Telling a user to restart an app that does not need restarting is a worse defect than saying
  nothing.
- ✅ **FIXED 2026-09-27 (fortunes 1.0.9).** The `ms.Set("spicyTier", "")` is gone. Re-measured
  first: `spicyTier` appeared exactly once in the entire repo, at that write, and
  `MigrateContentLevel` reads only `spicyFortunes` and `spicyOnly`. Clearing a key that never existed
  migrates nothing, so the comment justifying it ("a stale value can never be re-migrated") described
  a hazard that could not occur.
**Converter and build**

- ✅ **FIXED 2026-09-27 (petstudio 1.1.8).** `turn` is added to the name set and built as a node
  only when some spoke is locomotion, and both sites are gated together — emitting the node while
  leaving it out of the name set would be the same unreachable-state defect from the other direction.
  Fixed the way the emitter already handles the structurally identical ceiling case at `:140`, which
  clears `ceilingSpokes` when nothing will climb: do not emit a region nothing can enter.

  Covered by a second fixture, `NoLocomotionActionsXml` — three actions, none of them
  `Type="Move"`. The main fixture could not catch this (every shipped pet carries a Walk), which is
  exactly why it needed one of its own. Positive control with the fix reverted and rebuilt: *"a skin
  with no Move action left unreachable animations: 7"*, *"was not accepted, so a valid playable pet
  fails conversion"*, *"still emitted a `turn` animation"*, exit 1. With the fix, SELFTEST PASS.
  Shipped output unchanged: all 54 companions still verify with 0 invalid, 0 round-trip failures and
  the same 7 hand-authored unreachable.
- ✅ **FIXED 2026-09-27 (petstudio 1.1.7).** `PetEmitter.Has` is `Ordinal` now, matching
  `ActionClassifier.Has`. Ordinal is the correct reading rather than merely the consistent one: every
  token either helper tests for is a case-sensitive Shimeji identifier (`activeIE`, `totalCount`,
  `TargetX`, `Math.random`), and `cursor` comes from `mascot.environment.cursor`.

  Covered by a new synthetic action in the emitter self-test, `RestNearCursor` — capital C in the
  name, no condition, no expression, no reference to `mascot.environment`, and its own art so a
  direction collapse cannot merge it into a neighbour and hide the answer. The existing gaze cases
  test the true positive; nothing tested the false one. Positive control run BEFORE the fix: *"FAIL
  an action was tagged faceCursor for having "Cursor" in its NAME, with no cursor condition
  anywhere"*, exit 1. After: SELFTEST PASS, and `verify` over all 54 shipped companions unchanged —
  0 invalid, 0 round-trip failures, the same 7 hand-authored unreachable. The open question this item
  raised ("whether any of the 31 shipped conversions hit this") is therefore answered for the
  OUTPUT: nothing shipped changes.
- ✅ **FIXED 2026-09-27 (petstudio 1.1.7).** The stdout read is `CopyToAsync` with a bounded
  wait, then a short second bound for teardown once stdout is at EOF, and `Kill(true)` takes the tree
  to match `Engine.cs`. Exactly the defect fixed the same night in `ContentCatalogAssets.ps1`: a
  synchronous drain blocks until the child exits, so any `WaitForExit(timeout)` after it is
  unreachable until the thing it was meant to bound has already resolved itself.

  The check that should have caught it was pinned to three named files and this was the fourth site,
  so the repo-wide redirect loop now ALSO bans synchronous reads of a redirected stream
  (`.ReadToEnd()` and `.BaseStream.CopyTo(`), folded into the existing offender list so the
  Assert-True self-count is unchanged. `WebPLoader.cs:118` was the only such read in the repo.
  Mutation-tested both forms against HEAD's copy of the check: HEAD stayed green on WebPLoader (the
  hole) and caught Transcriber (already pinned), which is the argument for making it repo-wide.
- ✅ **FIXED 2026-09-27 (petstudio 1.1.9).** The zip extraction, `SkinLayout.Detect`,
  `BundleConverter.ConvertBundle` and `ShimejiEngine.ConvertSkin` all run under `Task.Run` now, with
  the `out` parameters captured into locals; the editor, analysis and import-loss panel still update
  on the UI thread after the await.

  A re-entrancy guard comes with it, released in a `finally`. The window is responsive now, which
  means it is also clickable — two conversions writing the editor and the loss panel at once is not
  a state this window has an answer for, so a second Import is refused rather than queued. The
  `finally` is the part that matters: a conversion that throws must not leave the window refusing
  every later import.

  The invariant that named `ModulesPaneControl.cs` only now covers `PetStudioWindow.cs`, and asserts
  the WRAPPING rather than the presence of `Task.Run`: `ConvertSkin` and `ConvertBundle` have no
  Async overload, so a check looking merely for `Task.Run` somewhere in the file would pass on a
  version that wrapped something else and still converted inline. Mutation-tested both halves —
  un-wrapping the extraction and un-wrapping the conversion each exit 1. Source-invariant count
  138 → 139.
- ✅ **FIXED 2026-09-27.** `build.ps1` keeps `#requires -Version 5`, because the ordinary build
  genuinely runs under either edition and that had to stay true. The floor now sits on the `-Zip`
  path alone, and it runs BEFORE the build rather than after it. `New-DeterministicPortableZip.ps1`
  keeps its `-Version 7`: that is measured, not stylistic, since deflate output differs by edition
  (63,580 bytes under 5.1 vs 64,271 under pwsh 7.6.5 for one payload, different SHA-256) and those
  archives are committed and hashed in `catalog.json`. Verified both directions: `-Release -Zip`
  under 5.1 now fails in 4 s -- shell startup, never reaching the compiler -- naming the host and the
  reason; a plain 5.1 build still succeeds, exit 0, 0 warnings.
  ⚠ `New-ModulePublish.ps1` and `New-ModuleDistZip.ps1` were named as "same transitively" and are
  NOT fixed: both are already `-Version 7` themselves, so `#requires` stops them at their own entry
  point rather than after someone else's long build. That is the behaviour this item wanted.

### Re-audit 2026-09-27 (three parallel read-only passes, after the backlog reached 1)

Seven findings were fixed in the same session and are not listed here: the unfalsifiable assertion
counter, the PetStudio import-guard ordering, the half-cached companion thumbnail, `UsePet`'s missing
`activePetId` rollback, the Remembrance standalone-snapshot purge, and its two missing
`NamesThisModuleWrites` assertions. What follows is what was NOT fixed. Each was verified against the
source before filing; the auditors' own refuted candidates are recorded at the end so nobody re-files
them.

- ✅ **FIXED 2026-09-27 in `d0dff9a`.** Only the RANDOM arm is gated now, so a pin is honoured at
  spawn whatever "Let companions spawn on any screen" says — which is what both doc blocks always
  promised. Verified in the real app rather than the suite: the fullscreen stand-down probe passes
  all 6 steps across 3 monitors. Pinned by a source invariant asserting the STRUCTURE (a check that
  merely found `PinnedDisplay` in `Play` would pass on the broken version, which read it one line
  inside the gate); mutation-tested by putting the pin back inside the gate, which exits 1.

  ⚠ **Filed open for one push after it was already fixed, and its CLOSES-WHEN could not have caught
  that.** The criterion was `grep-absent src/dotNet/FormCompanion.cs "int pinned = PinnedDisplay;"`
  — but the fix KEEPS that exact line and moves it out of the gate, so the string never goes absent
  and the check would have reported the item open forever. A criterion has to track the PROPERTY, not
  a string that happens to be near it. Caught by re-reading the open list against the diff, which is
  the burn-down audit this repo's own gate asks for.
- ✅ **FIXED 2026-09-27.** `build.yml` now runs `tests\Test-BacklogClosingCriteria.ps1` and
  `tests\companion-border-invariants.ps1`, closing the last of the gate/CI split its own comments
  claimed twice to have closed. Both signal failure by `throw`, so each is a bare call under
  `shell: pwsh` with no `$LASTEXITCODE` guard — that guard would be the dead branch this repo has
  already had to correct twice.

  Added only after the PREVIOUS CI step was proven on the runner rather than assumed: the
  module-self-test step added earlier the same day shows `OK agentflow PASS (484 assertion line(s))`,
  `OK aibrain PASS (216)` and `Module self-tests OK: 6 covered module(s) passed` in the log of run
  `510295d`, which also settles that `HttpListener` binds unelevated there — the risk flagged when
  `FakeCdpServer` landed. Stacking a second untested CI step on an unverified first is how a CI
  change becomes two problems.

  ⚠ Closing this is itself the loop working: adding the steps made
  `Test-BacklogClosingCriteria.ps1` report this entry CLOSEABLE and fail the run, which is precisely
  what it exists to do — fire in the same run that proves the fix works.
- ✅ **FIXED 2026-09-28 (petstudio 1.1.12).** `Emit` is now a thin guarded wrapper over
  `EmitCore`, refusing a re-entrant call with an `InvalidOperationException` that names the three
  fields. A wrapper rather than a try/finally around the ~150-line body, which has many exit paths.
  It throws rather than serialising: a second concurrent conversion is a caller bug, and quietly
  queueing it would hide the bug while making the caller slow for a reason it cannot see.

  PROVEN, not assumed. A temporary probe called `Emit` from inside `Emit` via the sprite loader
  — which `Emit` invokes while the flag is held — and the exception
  arrived. Probe removed; converter self-test passes without it, and `verify` over all 54 shipped
  companions is unchanged.

  This is the item that was "defended by a caller, not by the type". It is defended by the type
  now.
- ✅ **FIXED 2026-09-28 (remembrance 1.0.15).** Fire and forget, no `Wait`, same shape as
  `AiBrainModule.BeginVramProbe`. The answer lands in settings and the next pane build shows it
  — which is exactly what happened before whenever the 3 s cap expired, minus the
  freeze. The continuation writes through `PersistOnUi` rather than touching `_settings` from a pool
  thread; the module already routes background writes that way for the "Collection was modified"
  reason recorded on that helper.

  The doc comment said "Bounded and off the UI thread", which was half true and the dangerous half:
  `Task.Run` moved the HTTP call, and `probe.Wait(3s)` then blocked the UI thread anyway. Its
  justification — *"a refused connection is immediate"* — is the premise
  this repo measured false the day before: refused localhost burns 2005-2008 ms. One detail the
  entry understated: the gate is on the models cache, NOT on `summaryOn`, so users who never enabled
  the summary feature paid the freeze too.
- ✅ **FIXED 2026-09-28 (remembrance 1.0.15).** It names `"Transcribe a WAV file..."`, which
  exists, and says which file to pick. The old sentence had exactly one grep hit in the repo
  — itself — because no "Re-transcribe" action was ever built, and it is
  the only sentence a user reads on the one path where transcription has already failed.
- ✅ **FIXED 2026-09-28 (petstudio 1.1.12).** `FindBundleRoot` and `ReadBundleName` both run
  under `Task.Run` now. `ReadBundleName` rides along deliberately: it is the same class of work and
  sits between the two, so leaving it would only move the stall one line down. No
  `ConfigureAwait(false)` anywhere in that method — everything after the awaits is
  UI-affine and depends on resuming on the captured context.
- ✅ **FIXED 2026-09-28.** The control names the message it expects (`'Unsafe major-upgrade
  schedule'`) and reports a throw that is not it, instead of scoring any exception as proof. That is
  the correction `Test-StagingPathSafety.ps1:21-23` already carried and this file never got.
  Positive control against the real `dist/DesktopAICompanion.msi`: with the correct expectation both
  cases pass, exit 0; with it pointed at a message the guard never emits it exits 1 saying *"threw
  something OTHER than the schedule guard, so it proves nothing about the boundary"*. File restored
  byte-identical.
- ✅ **FIXED 2026-09-28.** Both dead guards removed, premise verified rather than assumed:
  `New-ContentCatalog.ps1` contains zero `exit` statements and `Test-ModulePublishFreshness.ps1`
  signals all 19 of its failures by `throw`, so `$LASTEXITCODE` there held whatever the last `git`
  call inside the callee left. `$ErrorActionPreference = 'Stop'` means a callee throw escapes on its
  own, which is the house form at `tests/run-gate.ps1:160-161`.
- ✅ **FIXED 2026-09-28.** The phantom assertion is gone; the direction check (`launch >= 0`)
  stays. There is no launch clamp to assert — `BuildSpoke` sets
  `vy0 = SolveJumpLaunchY(jumpSteps)` and discards the source velocity, and that solver searches
  `JumpLaunchMinMag`(4)..`JumpLaunchMaxMag`(40), so -40 is a legitimate answer.

  Worth recording why it never fired: the fixture's 2-pose BigJump gives 14 steps, for which the
  solver returns exactly -15, so `launch < -15` was false **by one unit, with zero margin**. Any
  change to `JumpPeakPx`, `JumpDescentY`, `JumpArcSteps` or the fixture's frame count would have
  reddened it with a diagnosis naming a clamp that is not in the code. The real property is asserted
  45 lines below against `PetEmitter.ArcRisePx`. SELFTEST PASS after removal.
- ✅ **FIXED 2026-09-28.** `MAPPING.md` and the `run-gate.ps1` comment both say 54/31/6 now,
  matching what `BundledConfSelfTest.cs:34` asserts on every gate run. The gate comment was wrong a
  second way the entry did not mention: it called the census *"a dev step — that config
  is copyrighted and must not live in this repo"*, three lines above the `selftest` call that runs it
  against the bundled conf. Both corrected, with the 2026-08-28 reason (ClimbWall stopped being
  reported as needing selfX/selfY) recorded beside each.
- ✅ **FIXED 2026-09-28.** Parameter and invocation both removed. One detail the entry did not
  flag: the hook was the LAST parameter, so removing it also had to drop the trailing comma on
  `$ContentDirectories` — the patch script asserted that and refused to write until it
  was handled. Parse-checked afterwards: 0 errors.
- ✅ **FIXED 2026-09-28.** The modules direction now mirrors the two blocks above it, so the
  `.DESCRIPTION`'s claim of membership "in both directions" is true for all three groups.
  Mutation-tested: dropping a real zip into `modules-dist/` as `strayghost.zip` exits 1 with *"1
  module zip(s) exist under modules-dist\ but are absent from catalog.json"*; the clean
  tree exits 0; the stray was removed afterwards.
**Smaller, verified, grouped — re-audit 2026-09-27**

- ✅ **FIXED 2026-09-28, and there were TWO of them.** The filed site is guarded; so is the one
  the entry did not name, at `:142`, which is worse. That one sits INSIDE a catch handler, so a
  module with a null `Info` that also threw from `Shutdown` raised an NRE from the handler itself
  — escaping `ShutdownAll` and skipping `Alc.Unload()` for that module AND every module
  after it in the list. A handler that can throw is not a handler.

  Both now read `Info` into a local and fall back to `"(no ModuleInfo)"`. Still not reachable with
  any in-repo module — all eight declare `Info` — so this is the
  third-party path, which is exactly the path the class's isolation promise exists for.
- ✅ **FIXED 2026-09-28.** A local now. Its four siblings (`addPet`, `removePet`, `syncPets`,
  `petSpeech`) are all re-read later to toggle enablement or rebuild submenus; this one's every use
  was the construction block. The build caught the leftovers for me: removing the writes left the
  field declaration and its `Dispose` null, and warnings-as-errors turned "assigned but never used"
  into a build failure rather than a slow rot.
- ✅ **FIXED 2026-09-28.** Field and assignment both gone. `Loaded` is private to `ModuleHost`
  and its only consumers are `ShutdownAll` (`Module`, `Alc`) and the `Modules` projection
  (`Module`), so nothing could have read it.
- ✅ **FIXED 2026-09-28.** Parameter dropped and the always-taken branch inlined; both call
  sites updated (`ProcessIcon.cs:339`, `ContextMenus.cs:609`). The reason for removing rather than
  leaving it is recorded in a `<remarks>`: with `false` the method still ran `pi.Dispose()` above,
  leaving the app alive with no tray icon and no timer armed — BUG-001's "running but
  unreachable" state, reachable only by passing an argument nobody had reason to pass.
- ✅ **FIXED 2026-09-28, on the success path only.** The timeout path deliberately does NOT
  dispose: the worker is abandoned rather than cancelled and still holds the handle, so disposing
  there races its `Set()` — which is why that `finally` swallows
  `ObjectDisposedException`. On the success path the worker has signalled and cannot touch it again,
  and that is where the `Dispose` was missing entirely.
- ✅ **FIXED 2026-09-28.** Both guards added, so it matches its four neighbours. Needed a new
  `FormCompanion.IsFullscreenBlocked` accessor, because `hwndFullscreenWindow` is private and
  `TopMostSheeps` lives in `StartUp` — reaching into private state would have been the
  wrong fix.

  The null guard is parity rather than a known crash, as the entry said: the slots are compacted on
  kill and zeroed on mass-kill, so no null inside `[0, iSheeps)` could be constructed. Both guards
  are cheap, and an asymmetry like this outlives the reason for it.
- ✅ **FIXED 2026-09-28 (remembrance 1.0.15), by wiring it rather than deleting it.**
  `RefreshDynamicOptions` calls it first, so each pane OPEN starts from a fresh WASAPI enumeration
  — what someone who just plugged in a headset expects — while the
  collapse from four enumerations to one still happens within the build, after that line.

  Deleting it would have left the record wrong in the other direction: the entry that closed the
  device-caching work cites this method as the escape hatch ("the window is short on purpose and
  `ForgetCachedDevices()` drops it explicitly"), and that safety property is now true of the shipped
  code rather than only of the note.
- ✅ **FIXED 2026-09-28 (agentflow 1.4.10).** Deleted. `SelfCheckCapabilityLog` constructs a
  fresh `AgentFlowModule` and relies on `_lastLoggedCapability` starting null, so it never needed the
  reset. AgentFlow's five other `...ForSelfTest` seams carry 3-6 references each; this was a seam for
  a test that was never written that way.
- ✅ **FIXED 2026-09-28, and the obvious fix would have broken the gate.** The entry suggested
  "a `$deps.Count -eq 0` failure would cost nothing". It would have reddened the gate immediately:
  `TestModule.csproj:16` sets `GenerateDependencyFile=false` and `testmodule` IS in
  `$RequiredModules`, so the loop legitimately no-ops on one of the eight every run.

  So the check is per-module aware. A module with no deps file fails UNLESS it is named in
  `$depsOptOut`, and a module in `$depsOptOut` that DOES emit one also fails — so the
  exemption cannot go stale and hide a check that could run. Both directions mutation-tested against
  the real runner: emptying the opt-out gives *"module 'testmodule' has no *.deps.json in the build
  output"*; adding `agentflow` to it gives *"is listed in $depsOptOut but DOES emit
  AgentFlow.deps.json"*. Clean run: 19 flags, no failures. The two adjacent comments that were false
  for testmodule (it carries no ModuleKit.dll either) are corrected.
**Checked and REFUTED by the re-audit — do not re-file**

- `SpriteBounds`'s cache keyed on `Image` alone, ignoring `transparencyKey` (`SpriteBounds.cs:28-36`).
  Unreachable: `TransparencyKey` is only ever `Color.Magenta` or `Color.Empty`, each `Xml` owns its
  own frame bitmaps, and every form sharing an `Xml` has the same transparency mode.
- `AudioOutput.TryStart` leaving `MixerInputEnded` subscribed on an abandoned mixer
  (`AudioOutput.cs:283-297`). The mixer is the PUBLISHER and is unreferenced after the failure, so the
  handler roots nothing.
- `FormSpeech` timers not unsubscribed in `Dispose` (`FormSpeech.cs:450-459`). Both are owned fields
  and are disposed there; the subscriptions die with them.
- `_netCts.Dispose()` while a download holds the token (`ModulesPaneControl.cs:405`,
  `CompanionsPaneControl.cs:573`). `Cancel()` precedes `Dispose()` in both, the documented-safe order.
- ~20 members that look dead and are not, all grepped and found alive, including
  `SpriteFrameForDiagnostics`, `AddDebugInfoWindowOnly`, `SyncSheeps`, `GetNextSpawns`,
  `CaptureScreenBounds`, `DeriveOnScreenMix`, `StopAllModuleSound`, `IsAtMaxPets`, `SpawnPreviewPet`,
  `TryPlayAnimation`, `EscapeToBath` and `ReassertSequence`.
- `PressBudget.TryPress`'s repeat arithmetic (permits exactly 3 identical presses, refuses the 4th,
  matching its doc) and `CdpSession.Send`'s `RootElement.Clone()` inside a `using` (documented to
  outlive its source document). Both chased hard and both correct.
- `turn` being emitted unreachably after the no-locomotion fix: guarded correctly at
  `PetEmitter.cs:200-201`, and set-piece steps cannot take that path because `ExpandSetPieces:2199`
  gives even the last step a non-null `ChainNext`.

**Checks that cannot fail (the category this repo keeps finding)**

- ✅ **FIXED 2026-09-27.** `.github/workflows/build.yml` runs `tests\Test-ModuleSelfTests.ps1`,
  so the 684 module assertions now run on a push as well as in the gate. Safe to add because the step
  above it already launches the same exe from the same path — only the flag was new — and verified
  by running the CI command verbatim from the repo root.

  ⚠ **Closing this exposed a check that could not fail, and it was mine.** The `CLOSES-WHEN` I filed
  with this item had its arguments backwards (`grep-present <needle> <path>` instead of
  `grep-present <path> "<needle>"`). The verb pattern wants a QUOTED needle second, so the whole line
  failed to match and was skipped in silence: the report said "3 carry a CLOSES-WHEN" while the file
  contained 5, and this one had already been satisfied. `Test-BacklogClosingCriteria.ps1` now reports
  any line beginning `CLOSES-WHEN:` that does not parse, so a criterion cannot go quiet by typo.
- ✅ **MOSTLY FIXED 2026-09-27 (fortunes 1.0.11, aibrain 1.1.12), and I was wrong about it TWICE.**

  First I filed it saying the host "cannot run their assertions" — false; all three have dedicated
  host-side self-tests the gate already ran. Then I corrected that to "real work per module rather
  than a delegation" — also false, for two of the three. `FortuneEngineProbe.Run` and
  `AiEngineProbe.Run` are both `public static bool (out string)`, the exact signature the convention
  wants. Only the entry point on the MODULE TYPE was missing, so each is one method.

  Coverage went from 4 modules / 684 assertions to **6 modules / 982** (agentflow 489, aibrain 217,
  remembrance 114, fortunes 70, blinkingled 56, reminder 36).

  The gate demanded the bookkeeping itself, which is the loop working as designed: adding the methods
  turned `Test-ModuleSelfTests.ps1` RED with *"now PASSES its module self-test. That is good news —
  move it from UNCOVERED to COVERED"*.

  ⚠ **PetStudio remains a recorded gap**, and the reason is now in the file rather than in my head:
  `BehaviourChainSelfCheck.RunChecks` takes a `fixturePetXml` that the host supplies from its OWN
  resources, so covering it means embedding a fixture in the module, not adding a delegation.

  One stale comment found on the way and left for a future pass: `BehaviourChainSelfCheck`'s header
  still warns that `--module-selftest` takes the FIRST `bool SelfTest(out string)` anywhere in the
  assembly. That was fixed — `ModuleConventionSelfTest` resolves against the module TYPE — so the
  warning now describes a hazard that no longer exists.
**Dead code, verified across `src/`, `modules/` and `tools/`**

- ✅ **FIXED 2026-09-27, 237 lines removed — and FOUR of this item's claims were WRONG.**
  Every member was re-checked for callers across all 205 `.cs` files before removal rather than
  taken from the list, which is how the four were caught.

  **Deleted, verified dead:** `WindowTheme` minus `IsDark()` (~150 lines: two P/Invokes, immersive
  dark title bars, the recursive control walk, uxtheme DarkMode_CFD / DarkMode_Explorer, seven
  colours — all serving the WinForms tray dialogs retired in S5b-3);
  `CompanionHost.SpeechSourceModuleIds`; `LocalData.GetScale()`, `.GetPetSizeLevel(string)` and
  `.GetEffectivePetScaleFactor(string)` (the `NoLock` form behind the second is kept and still
  used); `LocalData.SetXml`'s unread `folder` parameter, which also made
  `externalCandidate ? "external" : ""` dead at its only call site; the 2-argument
  `ProcessIcon.TaskbarWatcher` overload (every call site passes 3 or 4); `OptionsWindow._dirty`
  (assigned, never read — the button was always driven by the parameter); and the `AppPaths`
  vector-cache cluster, superseded because the Fortunes module owns that storage now, so the base
  migration could never run for anyone. Plus, with a publish each, `NotifyBudget.Forget`
  (agentflow 1.4.8, superseded by `Retain`) and `SmartFortunes.LastCandidateCount`
  (fortunes 1.0.8).

  ⚠ **NOT dead, left alone, item refuted:** `AppSettingsStore.FilePath` is read at
  `LocalData.cs:969`; `.BackupPath` at `CoreTests/Program.cs:419` and `:422`; `.IsReadOnlyFallback`
  at `:938` and `:948`; `.LastRecoveryFile` at `:444`–`:449` and `:472`. The item specifically
  claimed the last of these was "never read, so the corrupt-file preservation path records where it
  put the file and nothing can report it" — the tests read it. Deleting the four would have
  broken CoreTests, which is what caught the claim.

  One decision recorded rather than made silently: `LastCandidateCount` could have been ASSERTED
  instead of deleted, since its doc says its narrowness "WAS the bug". Rejected — the only
  invariants available are `<= MaximumCandidates` (512) and `<= LastBandCount`, and the self-test
  pool is 131 lines, so the cap never applies and neither could fail. Closing a dead-code item by
  adding a check that cannot fail trades one defect for a worse one.
- ✅ **FIXED 2026-09-27 (petstudio 1.1.7).** Collapsed to one method with nearest-rounding.
  The doc was worse than the dead branch: its `<param>` claimed `roundUp` was "used for rests, where
  undershooting is the thing that reads as wrong", while the rest call site passed `false` under a
  comment saying the opposite — "nearest rather than up, so a long performance lands inside the
  9-12s band instead of overshooting". Documentation describing a policy the code had decided
  against, for a caller that did not exist. The decision is now recorded in a `<remarks>` so the
  parameter is not reintroduced. Build 0 warnings; SELFTEST PASS; all 54 companions verify
  unchanged.
- ✅ **FIXED 2026-09-27.** Deleted. Confirmed unreachable before removal: the check eight lines
  above returns false whenever `rctO.Bottom <= rctO.Top`, and `0 <= 0` satisfies that, so a
  zero-height rect never reached the line. The closed-window case its comment described is already
  covered by the `GetWindowRect` failure and the degenerate-rect test.
**Optimisation, costs named rather than timed**

- ✅ **FIXED 2026-09-27.** `_iconCache` mirrors `_statsCache` beside it — same lifetime, same key —
  and `ForgetStats` clears both, because the icon comes out of the same rewritten `animations.xml` as
  the counts and goes stale at the same moment. Forgetting one without the other would leave a card
  showing the new name and counts beside the old picture.

  Measured before fixing (warm, 20 reps, `ReadAllText` + `XDocument.Parse` + the icon lookup, which
  is exactly what the miss path did): **esheep64 155 KB → 4.2 ms per card**, **pink_sheep 1133 KB
  → 20.4 ms per card**. Per card, per rebuild, on the UI thread.

  Misses are cached too, as a null value: a pet whose XML carries no icon, or whose folder has gone,
  would otherwise re-parse its entire `animations.xml` on every rebuild forever — the expensive
  case, cached for nothing.
- ✅ **FIXED 2026-09-27 (remembrance 1.0.13) — one enumeration, not the two this entry asked for.**
  A short snapshot behind both accessors means the four call sites inside one `Load` closure share a
  single enumeration per flow.

  No speed figure is quoted, because none was measured: the claim here is a COUNT (four calls become
  one per 1500 ms window), which is structural and checkable by reading the code, not a timing
  saving.

  It also closes a consistency bug the entry did not mention: the dropdown options and the
  "devices: N output, M mic" count came from SEPARATE enumerations, so a device appearing or
  disappearing between them produced a status line that disagreed with the list directly above it.
  The window is short on purpose — a reopened pane must not show a stale list after a headset is
  plugged in — and `ForgetCachedDevices()` drops it explicitly.
### Filed 2026-09-25 — the leak soak's verdict depends on its duration

- ✅ **FIXED 2026-09-27: the bound is a RATE now.** `MaximumHandleGrowth` and its two siblings are
  per SETTLED INTERVAL, scaled by however many intervals a run actually observes. The defaults were
  always calibrated for one interval, which is what the default duration produces (cycles 40 and 80),
  so the calibrated tolerance is unchanged and every duration is now comparable instead of quietly
  stricter. Verified: the 180-second run that used to fail at +84 and +90 now passes (+82 against a
  scaled 144), and the documented default still passes. Mutation-tested by inflating the measured
  growth: `9996 > 16 (16 per settled interval x 1 observed)`, exit 1.

- ✅ **FIXED 2026-09-27.** The report now ships `SettledPerInterval` beside `SettledGrowth`:
  the per-interval series, a median, and an explicit confidence string. Below three intervals it says
  `n=1` outright rather than dressing one observation up as a summary statistic. The BOUND is
  deliberately unchanged -- changing a pass criterion on a metric this noisy, without data to set the
  new one, is the shape of the mistake this item came from.

  Both paths were exercised rather than assumed, and the long one made the case better than the
  original write-up did. Default 30 s: 1 interval, "treat as a HINT". A 150 s run: 8 intervals,
  median handles **2**, containing one interval at **+93** and another at **-9** -- all inside a
  single PASS. A reader handed only the end-to-end delta would have drawn a conclusion from that
  spike; the spread is now printed next to it.
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
