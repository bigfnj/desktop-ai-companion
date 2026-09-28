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

- ✅ **FIXED 2026-09-28. The run extends itself, and the root cause was not the duration.**

  A settled sample lands every 40 cycles (`Program.cs`, `SettledSampleEveryCycles`), but the churn
  loop terminated on WALL CLOCK. Nothing tied the two together: the documented default asks for 15
  target cycles while the FIRST settled sample is at cycle 40, so every sample this soak has ever
  published came from the minimum-duration overrun rather than from the cycle target. Cycle rate
  falls on a loaded box, fewer than 2 land, and the driver threw.

  The loop now keeps cycling until the verdict has its two samples, bounded by
  `DESKTOPPET_RESOURCE_CHURN_MAX_DURATION_MS`, which the driver derives from its own completion
  deadline so a box too slow to reach a verdict still publishes a marker and earns a specific
  failure. An idle box pays nothing -- it is past cycle 80 well before the minimum elapses.

  **Mutation-tested across three legs**, with the degraded rate reproduced deterministically (churn
  interval 600 ms instead of 250, landing ~66 cycles inside the 40 s minimum -- past the sample at
  cycle 40, short of the one at 80):

  | leg | driver + binary | result |
  |---|---|---|
  | A | old + old | the original *"at least 2 are needed ... Increase -DurationSeconds"* -- the degraded scenario is real, not theoretical |
  | B | new + old | names the stale build; no `PropertyNotFoundStrict` leak |
  | C | new + new | PASS, extended to 2 settled samples, finishing at 61036 ms |

  ⚠ **Two further defects were found BY the mutation test, and both are fixed here.**

  *The driver leaked a PowerShell rule name instead of a cause.* Pointed at a build older than
  itself -- which `-ExecutablePath` makes easy to do on purpose -- it died with a bare
  `PropertyNotFoundStrict` from `Set-StrictMode`. It now checks the marker carries its verdict
  fields by name and says which one is missing and why.

  *The cleanup replaced the failure it was cleaning up after.* `Stop-TestProcess` runs from the
  `finally` block, and `taskkill` is the only call in it without a `try`/`catch`. A native command
  writing to stderr raises a TERMINATING `NativeCommandError` under this script's
  `$ErrorActionPreference = 'Stop'`, and an exception thrown in a `finally` REPLACES the one already
  in flight. So a real soak failure surfaced as *"taskkill.exe : ERROR: The process NNNN not
  found"* -- which taskkill writes whenever the process has already exited, i.e. on exactly the
  failure paths this cleanup exists to serve. It swallowed two mutation verdicts before I found it.
  The proof is the before/after on one unchanged run: only taskkill noise before, the real message
  after.

  CLOSES-WHEN: grep-present tests/runtime-resource-soak.ps1 "older than this driver"
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

## Open: three read-only audits after v1.2.6 (filed 2026-09-28)

`src/`, `modules/` and `tools/`+CI, audited in parallel at `1976843`, each briefed to verify before
filing and to state empty categories as real results. 24 findings. **Nothing here is a regression
from the 1.2.6 campaign except where it says so** -- two entries are residue of fixes made that day
and are marked.

Three categories came back genuinely empty and are recorded so nobody re-spends the time: memory and
handle leaks across both `src/` and `modules/` (beyond the one static field in H6), flags assigned
and never read, and PowerShell 5.1-vs-7 runtime divergence. Each auditor also listed candidates it
killed during verification; those are in the transcript, not here.

**Verification status is stated per entry.** Items marked VERIFIED I re-checked against the source
myself before filing. The rest carry the auditor's cited evidence and have NOT been independently
re-checked -- treat the first step of acting on one as confirming it still reproduces.

### User-visible

- ✅ **FIXED 2026-09-28.** `keep = 1` means one file and it is the CURRENT one, so there is no
  archive slot to shift into and the current file has to be deleted outright; every `keep >= 2` got a
  fresh file as a side effect of the shift, and `keep == 1` had to be told. One line, plus the reason
  beside it.

  **Rotation now has coverage, which it had never had** -- the audit's own note was that grepping
  `RotateNoLock` across `src/`, `tests/` and `docs/` returned only the source itself. Four assertions
  in `--wpf-options-selftest`: the keep=1 current file is removed, the keep=2 current file is shifted
  into the archive slot, an archive past the keep count is dropped, and the surviving archive is the
  one just rotated out. The last two assert on CONTENT, so a rotation that moved the wrong file still
  fails.

  To make that testable the rotation became a function of the two values it depends on
  (`RotateIn(directory, keep)`) rather than of module statics, with `RotateNoLock()` delegating.
  That is the OPPOSITE of the `$TimeoutMs` shape filed elsewhere in this file: no unused parameter
  was added for a test, the parameters it already needed stopped being globals. It matters because
  these self-test flags run against the user's REAL data root -- there is no isolated
  `DESKTOP_AI_COMPANION_DATA_ROOT` around them -- so a rotation test that wrote 512 KB into the
  user's own log would be worse than no test. It writes to a scratch directory instead.

  MUTATION: removing the `keep <= 1` line fails exactly one assertion, *"at keep=1 the over-cap
  current file is removed, not left to grow"*. The baseline run was checked for the new labels
  first, because a clean pass proves nothing if the assertions never executed, and the built exe's
  timestamp was asserted to advance between legs.

  ⚠ **The real-app smoke then found a SECOND defect the suite could not, and it is the bigger
  one.** `Start()` rotates before `Configure` has ever run -- it has to, because the rotation must
  precede anything worth recording and the settings store is not loaded that early -- so it rotates
  with the FIELD DEFAULT of 2. Every launch therefore recreated `diagnostics.1.log` whatever the
  user had chosen, and at `keep = 1` that archive survived until the next cap-driven rotation, which
  on a quiet app can be hours away. **"Keep 1" never actually held across a launch.** `Configure`
  now drops the archives a lowered setting no longer allows, which needs no change to the launch
  order. Measured on the shipping binary with an isolated data root: 1 archive before, 0 after, live
  file intact.

  The suite could not have caught it: the suite tests the rotation, and this was about WHEN the
  rotation is asked for. Two more assertions cover the trim, including the property rotation does not
  have -- that it leaves the LIVE file alone, because lowering a setting is not a rotation. The
  WIRING is a source invariant scoped to `Configure`'s body, because the smoke is not in the gate;
  MUTATION: renaming the call site alone fails it while the method DECLARATION, which contains the
  same text, stays in the file (`declarationStillInFile=1`), so a file-wide grep would have passed.
  SMOKETEST.md 145 -> 147.

  ⚠ **The first version of that smoke test was worthless and printed a verdict anyway.** It
  round-tripped `settings.json` through `ConvertTo-Json`; the app silently discarded the result and
  came back with `keep=2 / cap=512`, the defaults. So it measured the default configuration twice and
  reported "NOT bounded" for a reason that had nothing to do with the code. It now edits in place by
  key and **refuses to report a verdict unless the edit survived the next launch** -- a smoke test
  that cannot tell "the setting did not apply" from "the code is broken" is worse than none. Same
  family as the `taskkill` masking closed yesterday: the check ran, and its answer was about
  something else.
- ✅ **FIXED 2026-09-28.** The reset calls `DiagnosticLog.Configure` with the values read back
  from the store, exactly as Save does and for the reason Save states. Read back from `data` rather
  than from `def` so the clamps in the setters are the single source of what the logger is told.
- ✅ **FIXED 2026-09-28 (remembrance 1.0.16). THREE of the four branches were loose, not one.**

  `NamesThisModuleWrites` decides what `File.Delete` -- not the recycle bin -- removes from a folder
  the user chose, on Init and then hourly. Its own doc already stated the rule: *"'snapshot of my
  cat.png' must not qualify. Only the exact stamp this module writes does."* Only one branch held
  that line.

  | branch | shipped | matched |
  |---|---|---|
  | in a capture folder | `StartsWith("snap") && EndsWith(".png")` | any `snap*.png` |
  | root | `EndsWith(".wav")` | **any** `.wav` |
  | root | `Contains(" - snap") && EndsWith(".png")` | `my holiday - snapshot.png` |
  | root | `IsRootSnapshotName` | the exact stamp -- correct |

  The `.wav` one is the widest thing this purge ever matched and was not in the audit: a user who
  pointed `storageLocation` at a folder holding their own audio lost every file in it past the
  retention window, permanently, with no prompt. An extension is not a shape.

  Every branch is now a PARSED shape built from the same strings `NewCapture` and `TakeSnapshot`
  write, with the timestamp format in one place:

  | | audio | snapshots |
  |---|---|---|
  | in a capture folder | `recording.wav` | `snap <stamp>.png` |
  | in the root | `<stamp>.wav`, `<meeting> - <stamp>.wav` | `snap <stamp>.png`, `<baseName> - snap <stamp>.png` |

  **Three of the old fixtures asserted TRUE for names nothing produces** -- `"snap 1.png"`,
  `"sprint review - snap 1.png"` and `"sprint review.wav"` -- because `"1"` is not a timestamp and a
  flat recording is always `<meeting> - <stamp>.wav`. That is what made three loose branches look
  tested. Positives are real names now, and the `insideCaptureFolder` branch has negatives for the
  first time: 6 positives and 9 negatives, up from 4 and 3.

  MUTATION, and each mutation is literally the code that shipped rather than a hypothetical, so this
  also answers per branch "would the new tests have caught it":

  | restored loose branch | failures | which |
  |---|---|---|
  | in-folder `snap*.png` | 2 | both new subfolder negatives |
  | root bare `.wav` | 3 | a user's own wav, a bad-stamp wav, a root `recording.wav` |
  | root `Contains(" - snap")` | 1 | `holiday - snapshot.png` |

  No cross-talk, baseline and restored both `RESULT=PASS`, and every leg asserted the rebuilt DLL's
  timestamp advanced and the marker postdated it.

  ⚠ **I hit the stale-marker defect filed in this same audit while doing this**, which is worth
  recording as a live sighting rather than a code reading: the first read of
  `dp-module-remembrance-selftest.txt` showed the OLD assertion labels against a freshly built DLL,
  and it took a timestamp comparison to notice. Every module self-test run in this closure therefore
  deletes the marker first, asserts it is gone, and asserts the new one postdates the DLL. The
  underlying fix to `tests/Test-ModuleSelfTests.ps1` is still open below.
- ✅ **FIXED 2026-09-28 (aibrain 1.1.13).** I re-measured rather than taking the numbers, and
  they reproduced exactly: across all 54 `Companions/*/animations.xml`, case-insensitively, happy
  18/54, excited 35/54, sad 8/54, thinking 8/54, confused 8/54. `boing` is on exactly one companion
  and `flower` on one.

  | emotion | shipped | reached | with the fallback |
  |---|---|---|---|
  | happy | flower, jump, boing | 18/54 | **42/54** |
  | excited | run, jump, boing | 35/54 | **42/54** |
  | sad | sleep1a, sleep2a | 8/54 | **39/54** |
  | thinking | sleep1a | 8/54 | **40/54** |
  | confused | rotate1a, boing | 8/54 | **43/54** |

  **The original names stay FIRST in every list.** That is the part worth stating: no companion that
  already reacted changes what it plays, so this adds a reaction for the 46 of 54 that silently did
  nothing rather than altering the ones that worked.

  **Nothing reaches all 54, and no list can.** The intersection across this corpus is EMPTY, and the
  only names present on 50+ pets are the engine's reserved lifecycle animations -- `fall` 53, `drag`
  52, `kill` 51, `sync` 50 -- so a list built for coverage alone would offer to play the dying
  animation. That is why the measurement is recorded on the method itself, the way
  `AgentFlow/PetAnimations.cs` records its own.

  Coverage cannot be asserted inside the module (it has no access to `Companions/`), so the self-test
  holds the two properties a future edit CAN break, and both are edits somebody would plausibly make
  while believing they were improving things:

  | mutation | caught by |
  |---|---|
  | reorder a list, e.g. sorting by corpus frequency | *"'happy' still leads with flower"* |
  | append a reserved lifecycle name for coverage | *"'confused' offers no reserved lifecycle animation"* |

  MUTATION: both fire, exactly one failure each, naming the right assertion; baseline and restored
  both `RESULT=PASS`. aibrain's assertion count 216 -> 233.
- ✅ **FIXED 2026-09-28 (reminder 1.0.6).** Re-measured: `boing,jump,run,flower` reaches **35 of
  54**, missing 19 -- `bbunny`, `blue_ham_ham`, `fox`, `mareep`, `mimiko`, `negima`, `neko`,
  `pikachu`, `pingus`, `pink_fox`, `pink_neko`, `shiny_sylveon`, `yellow_neko` and six converted
  shimeji. With the fallback tail: **43/54**. `reactOn` defaults to true, so those 19 users had a
  feature switched on that did nothing.

  The historical four stay first and IN ORDER, so no companion that already reacted changes what it
  plays. The old doc comment was half-right -- it said these are names the shipped pets define and
  that a converted shimeji uses different ones, which is the reason an ordered list exists, but the
  list never actually reached the converted ones.

  ⚠ **The three assertions that pinned the literal string are gone, replaced by properties.**
  `Count == 4`, `[0] == "boing"` and `[3] == "flower"` tested the string rather than anything about
  behaviour, which is exactly why the audit predicted that improving the default would redden the
  gate. What stands there now:

  | assertion | the edit it catches |
  |---|---|
  | the historical four still lead, in order | a reorder, e.g. sorting by corpus frequency |
  | there is a fallback beyond the historical four | reverting to the 35/54 list |
  | no reserved lifecycle animation is offered | appending `fall`/`kill`/`sync` for coverage |

  MUTATION: a reorder and an appended `fall` each fire exactly one failure, naming the right
  assertion; baseline and restored both `RESULT=PASS`.
- ✅ **FIXED 2026-09-28.** `data.SetDefaultSpeakingPet(def.DefaultSpeakingPet ?? "")`, next to
  the per-pet `triggerSpeech` exclusion that does have a stated reason.

  ⚠ **Both reset gaps are now guarded, and the guard's SCOPE is the whole point.** A file-wide
  grep for `DiagnosticLog.Configure(` is satisfied by the save path's call one screen away, so it
  would have passed against the shipped defect -- which is how this survived in the first place.
  `runtime-hardening-selftest.ps1` slices `ResetToDefaultSettings` first and asserts inside that
  body only. MUTATION: renaming each call inside the reset region alone fails the matching assertion
  while the save path's copy stays in the file, confirmed by the harness reporting
  `copiesLeftElsewhere=1` on both legs. Source restored byte-identically. SMOKETEST.md 142 -> 145.
### Checks that cannot fail, or fail for the wrong reason

- ✅ **FIXED 2026-09-28, and I hit it live rather than reading it.** The delete is
  `[System.IO.File]::Delete` and its RESULT IS CHECKED: a marker that survives now returns
  `STALEMARKER`, naming the path. `Remove-Item` performs `~` home-directory expansion even under
  `-LiteralPath`, and `-ErrorAction SilentlyContinue` swallowed that plus any lock or ACL failure --
  the sibling runner had already removed exactly this call for exactly this reason.

  MUTATION, reproducing the hazard directly instead of simulating a tilde path: hold an exclusive
  write lock on the marker with a stale `RESULT=PASS` inside it. The run is now reported as
  `STALEMARKER` and, the column that matters, **it is no longer graded** -- the harness confirmed the
  module did not appear as `OK`. Before the fix that stale PASS was the verdict.

  **The reasons now name the assertion.** The pattern was `'^\s*FAIL: '`, which matches only the
  host-level line *"FAIL: the module's own self-test passed"*; a module's own failures arrive as
  `  [<id>] FAIL: <assertion>` and were dropped, so the gate named the module and never the thing to
  fix. MUTATION: breaking a real Remembrance assertion now reports *"'<name> - snapshot.png' is NOT
  ours; the separator is part of the shape"* alongside the generic line. Safe for the expected-failure
  lists, which hold host-level reasons for two modules that have no `SelfTest` to emit module-level
  ones.

  ⚠ **I added a marker-age guard and then removed it, because nothing can reach it.** Once the
  delete is asserted the file is gone, and the only ways a run leaves no fresh marker are Finish's
  write throwing (no file at all, so `NOMARKER`), the create-then-rename failing (temp file only, so
  `NOMARKER`), or two runners racing on the same `%TEMP%` path -- where both files are RECENT, so an
  age test is blind to it anyway. Shipping it would have added a check no input can fail, which is
  the defect this section exists for. The diagnosis is kept as a comment at the site so the next
  person does not re-derive it.
  CLOSES-WHEN: grep-present tests/Test-ModuleSelfTests.ps1 "STALEMARKER"
- ✅ **FIXED 2026-09-28.** The step deletes the marker, asserts the delete, requires the marker
  to exist, requires `RESULT=PASS`, and **fails on a SKIP** -- which is what
  `templates/.../SampleModule.cs` already promised (*"Never SKIP silently -- the gate fails on a
  SKIP"*) and what `ModuleConventionSelfTest.Run` returning true on one had made false. It also
  reports the assertion count, so a template whose self-test shrinks to nothing is visible.

  MUTATION, using the exact latent input the audit named rather than a stand-in: replacing a
  `probe.Check` in the scaffolded `SelfTest` with `probe.Skip(...)` now fails with *"the scaffolded
  module's self-test SKIPPED"*. Baseline and restored both `TEMPLATE OK`. Deleted with
  `[IO.File]::Delete`, which does not perform `~` expansion; `Remove-Item` does.
- ✅ **FIXED 2026-09-28, structurally.** I inspected the DLL instead of guessing at a fix:
  `collections.json` is embedded VERBATIM as a resource, so the check now extracts that JSON, parses
  it, and compares collection -> sources against `packs/collections.json` exactly. A failed
  extraction THROWS rather than falling back to the old id-only search -- a check that quietly
  downgrades itself to a weaker one is the shape this section is full of.

  MUTATION, and the decisive leg is the one the old check provably could not see:

  | mutation | old check | new check |
  |---|---|---|
  | MOVE a pack between collections | passed | caught, names the collection and both source lists |
  | RENAME a collection | passed | caught, "absent from the shipped DLL" |
  | DROP a source | caught | caught |

  Baseline and restored both pass, and `packs/collections.json` was restored byte-identically.
- ✅ **FIXED 2026-09-28.** Wrapped in `if ($launchPublish.Count -eq 1)`, matching every other
  block in the file. Nothing is hidden by skipping them: the `Count` assertion above has already
  failed and recorded itself.

  MUTATION: pointing the `ControlEvent` query at a dialog that does not exist -- which is the
  condition the block exists to detect -- now reports the assertion as a FAIL instead of dying with
  *"Index was outside the bounds of the array"*. The harness also had to be corrected twice before it
  measured anything: a backtick inside a double-quoted PowerShell string is the ESCAPE character, so
  the MSI query anchor silently lost its quoting, and `-MsiPath` is `Mandatory`, so omitting it made
  the leg fail before reaching the guarded block -- which reads exactly like "the check said
  nothing".
- ✅ **FIXED 2026-09-28.** `throw`, matching the file's own other failure. `exit` from a child
  `.ps1` raises no terminating error, so the `try`/`catch` in both callers never fired and the gate
  continued green over a failed baseline -- and every refusal in that file proves nothing if the
  baseline did not land, which makes it the one failure that must not be silent.

  MUTATION: forcing the baseline branch to be taken now reaches the caller. The reachability caveat
  the audit stated still holds -- `Publish-DesktopAICompanionAtomicFile` either throws or lands the
  file -- so this was the failure CHANNEL rather than a live escape, and it cost one line.
- ✅ **FIXED 2026-09-28.** The grep verbs require a needle and say so through the same reporter
  as every other malformed criterion. The needle stays optional in the regex because `file-exists` and
  `file-absent` legitimately take none.

  MUTATION: appending `CLOSES-WHEN: grep-present <path>` with no needle to a copy of `BACKLOG.md` now
  reports *"uses 'grep-present' with no quoted needle"* instead of *"Cannot bind argument to
  parameter 'Pattern' because it is an empty string"*. Third variant of this family closed; the first
  two were mine.
### Correctness and dead code

- ✅ **FIXED 2026-09-28 (petstudio 1.1.13).** The `Closed` handler now calls
  `CleanupExtracted()` only when no import is in flight; during one it drops the reference WITHOUT
  deleting. Deliberately the safe side of an uncertainty I did not reproduce: not deleting costs a
  temp directory, deleting costs an opaque IO failure in the conversion the user is waiting on. The
  second-Import guard's own comment already recorded why this path exists -- *"It only became
  reachable when the conversion moved off the UI thread: a responsive window is one you can click
  again"* -- and a responsive window is also one you can CLOSE, which `PetStudioModule.Shutdown`
  does too, so app exit during an import arrived here as well.

  ⚠ **I wrote a comment claiming an orphan sweep that did not exist, then had to build it.** The
  first version said the leftover directory is *"exactly what the orphan sweep on the next Import
  looks for"* and named `ForgetOrphanedExtractions`. Neither existed. Left alone that would have been
  a leak introduced while fixing one: every close-during-import abandoning a
  `petstudio-shimeji-*` tree for ever. `SweepOrphanedExtractions` is real now, runs at the start of
  an Import, and carries three narrowings because it deletes directories: `%TEMP%` plus this module's
  exact prefix with a suffix that must parse as the 32-char GUID it generates; only trees older than
  6 hours, so a second instance mid-conversion is never robbed; best-effort per directory.

  VERIFIED against the REAL compiled method by reflection, not by reading it, and not by
  re-implementing the predicate -- which would only have tested my copy. Seven decoys, all correct:

  | decoy | expected | result |
  |---|---|---|
  | ours, stale | delete | deleted |
  | ours, fresh | keep | kept |
  | right prefix, suffix not a GUID | keep | kept |
  | right prefix, wrong length | keep | kept |
  | prefix with no suffix | keep | kept |
  | prefix not at the start | keep | kept |
  | unrelated folder | keep | kept |

  This verification is a one-off probe, not a gate step: PetStudio has no module-class `SelfTest`
  (its checks need a pet-XML fixture the host supplies), which is the recorded gap below.
- 📌 **AgentFlow reads the host's unsynchronised settings dictionary from a pool thread, at
  two remaining sites.** `modules/AgentFlow/AgentFlowModule.cs`, both reaching `ArgvPath` =
  `_settings.Get(...)`. `CompanionHost.ModuleSettings` is a bare `Dictionary<string,string>` with no
  lock, written on the UI thread by `SavePaneValues` on every Apply. This is precisely the defect the
  tick's own comment says was closed -- *"Enabled was the one setting the worker still went and
  fetched for itself ... Every other value on this beat was already copied across the boundary"* --
  and `ArgvPath` is the counter-example to "every other value". Blast radius is bounded (a torn read
  gives a wrong path for one probe), so this is hygiene rather than a crash. Nothing pins it.
  Needs an `agentflow` republish.

- ✅ **FIXED 2026-09-28.** Deleted, and the behaviour was already correct: **the comment directly
  above the branch asks for exactly what the unreachable branch would have prevented.** It says
  *"NOTHING TO RENAME IS NOT NOTHING TO DO. The version records the standard a pet MEETS, not whether
  this run edited it ... Falling through stamps the rung and re-validates."* Always falling through is
  the documented intent, so the leftover bail contradicted its own paragraph and happened to be
  unreachable.

  Confirmed against the ladder rather than inferred: `NextFormatVersionAfter` short-circuits only for
  `ConvertedFormatVersion`, otherwise walks `FormatLadder`, whose `0.8` rung returns `1.0`. The gate 20
  lines above admits only `0.8`, so the equality was `"0.8" == "1.0"` for every pet the verb accepts.
  `reloop`'s identical guard stays, because its gate also admits `ConvertedFormatVersion` -- which is
  what made this a copy-paste divergence rather than a style choice.
- ✅ **FIXED 2026-09-28. One writer for all eight verbs.** `reloop` had it right; the other seven
  wrote `new UTF8Encoding(false)` unconditionally. Its logic is now
  `WritePetXmlPreservingEncoding`, which reads the file it is about to replace -- still the
  unmodified original at that point -- so no verb's read path had to change.

  **PROVEN, and it took three attempts because the first two were degenerate.** Worth recording, because
  both failures looked like results:

  | attempt | what it reported | what was actually true |
  |---|---|---|
  | 1 | *"33 files changed, 0 BOMs lost"* | **zero** files were written; the 33 was my regex matching skip-report TEXT |
  | 2 | `filesRewritten=0` for all seven verbs | correct, and it kills the approach: the corpus is fully migrated, so every verb is a no-op today |
  | 3 | BOM preserved / BOM STRIPPED | the property, on an input a verb admits |

  Attempt 3 constructs the input: take a BOM-bearing converted pet (`shimeji-06n2wuu6`), roll its
  `<version>` back to `0.8` -- the format `undirect` admits -- writing it back WITH its BOM, then run
  the verb. MUTATION, with the axis checked in both legs rather than assumed:

  | leg | verb wrote the file | BOM kept |
  |---|---|---|
  | helper in place | yes | **yes** |
  | `UTF8Encoding(hadBom)` -> `UTF8Encoding(false)` | yes | **no** |

  ⚠ **So the defect is LATENT, exactly as the audit said, and that is the reason to fix it now.**
  No current verb rewrites anything, so nothing is broken today; the hazard is the next migration rung
  somebody adds in the majority style, which would rewrite 18 blobs for no functional change and
  invalidate their `catalog.json` sha256 -- git normalises CRLF via `.gitattributes` but does not
  normalise a BOM.
- ✅ **FIXED 2026-09-28.** `AnimationSync = -1`, and the three `> 1` guards in `FormCompanion`
  became `> 0`. Ids are positive by validator rule and -1 is the "not declared" sentinel, so `> 1`
  excluded exactly one legal value in each case: a `sync` at id 1 (tray row hidden, `Sync()` a no-op)
  and a `kill` at id 1 (death animation skipped silently).

  `AnimationKill` already defaulted to -1, so its guard was wrong on its own terms -- with a -1
  sentinel, `> 1` rejects nothing except id 1, which is the only value it could ever wrongly exclude.
  That is what made `AnimationSync` the odd one out rather than the pattern.

  **Still latent, and measured rather than assumed:** across all 54 shipped companions none declares
  `sync` or `kill` at id 0 or 1, and the 44 that declare `sync` use ids 12, 13, 22, 23, 26, 32, 55,
  71, 77 and 78. Reachable only by a hand-authored or drag-and-dropped `animations.xml`, which is a
  shipped feature gated only by "unique positive ids". No repair in `ResolveMagicAnimations` is needed
  once the sentinel and the guards agree.
- ✅ **FIXED 2026-09-28 (aibrain 1.1.13).** Deleted, both the field and `int gen =
  ++_generation;`. Generation serialisation is owned by `AiSessionManager`, which is what the comment
  three lines below already pointed at. No CS0414 fires because the initialiser is not a constant --
  the exact compiler behaviour `docs/DESIGN-REGISTER.md` measured and warned about, which is why this
  was found by grep rather than by a clean warning build.
- ✅ **FIXED 2026-09-28.** All five. Not a use-after-dispose, as the entry said -- the only
  reader of `syncPetsMenuItem` hangs off the `ContextMenuStrip` disposed three lines earlier, and
  `RefreshSpeechMenuItem` touches only the speech pair -- but an asymmetry with no reason behind it,
  in a `Dispose` whose whole job is to leave nothing held.
### Optimisation, with the cost named

- ✅ **FIXED 2026-09-28 for the call sites that threw the work away.** `LocalPetIds` discards
  every `DisplayName` and still paid a `File.Exists`, a `StreamReader` and a 32768-char `ReadBlock`
  per installed companion -- roughly 54 opens and 1.7 MB of decoded reads over the shipped corpus, on
  the UI thread, on pane open, on every Check, after every download and after every uninstall.
  `EnumerateLocalIds()` skips the header read; four call sites use it.

  **No duration is claimed.** The saving is avoided I/O, which is a count; this repo's rule is that a
  timing measured in a warm loop has been wrong every time, twice with the conclusion inverted.

  Both entry points delegate to one `EnumerateFrom(bundledRoot, libraryRoot, readDisplayNames)`, so
  the id set is identical by construction rather than by my reading of it. Writing a second traversal
  would have been the easy way to make the two answers drift.

  ⚠ **The first version of the test for that could not fail, and I only found out by mutating
  it.** It compared the two enumerations on the INSTALLED corpus -- and there is no `companions/`
  directory beside the exe on a dev build or in CI, because the packaging step creates it, so both
  returned zero ids and agreed vacuously. Pointing the fast path at a deliberately divergent
  traversal still reported PASS. It now runs on a synthetic corpus, which is also the only way to
  cover an id present in BOTH roots -- the precedence case this entry flagged, which never occurs in
  the shipped corpus.

  Three mutations, each caught by at least one check:

  | mutation | caught by |
  |---|---|
  | the ids path skips the bundled root | the source invariant (the synthetic test passes its own roots, so it is blind to this) |
  | traversal order flipped | `--catalog-selftest`: 3 pets -> 2 |
  | `seen`-dedup dropped | `--catalog-selftest`: 3 pets -> 4 |

  A fourth mutation was invalid and is recorded as such: `if (false) continue;` is rejected by the
  compiler (CS0162, warnings as errors), so it never tested anything.

  **The precedence inconsistency itself is REAL and is now pinned rather than fixed.**
  `AddFrom` walks bundled-then-library with a `seen` set, so the BUNDLED copy wins a duplicated id,
  while `DisplayNameForId` resolves library-then-bundled. The self-test asserts the bundled winner so
  the behaviour cannot drift silently, and the disagreement is documented on `EnumerateFrom` as the
  reason the header-name cache is NOT shared between the two. Sharing it without reconciling them
  would change `EnumerateLocal`'s answer, which is what this entry warned about.

  SMOKETEST.md 147 -> 149.
- 📌 **AgentFlow's cold setup-status path still inspects on the UI thread, and in Off mode
  the tick never warms the cache.** `modules/AgentFlow/AgentFlowModule.cs`. `SetupStatusLine`'s doc
  claims *"Cold only in the window between Init and the first tick completing"*, but `OnTick`
  returns early when not `Enabled`, which is false in Off mode -- so for an Off-mode user the first
  pane open pays it synchronously. The module's own measurement: **30.1-30.7 ms with the port
  listening, 273.3-284.6 ms with it closed.** Once per process, so this is residue of the
  quarter-second freeze that comment describes rather than a repeat of it. `BrowseForArgvAsync`
  calls `Inspect` outside any `Task.Run` too. Needs an `agentflow` republish.

### Residue of the 1.2.6 campaign itself

- ✅ **FIXED 2026-09-28.** Removed, with the reasoning left at the site: `LoadThumb` owns the
  cache, has already taken it and MISSED on this key before calling down, and both run on the WPF UI
  thread only -- so the inner lookup could never hit and the inner store was immediately overwritten
  with the same reference. Residue of that day's fix, and it contradicted the doc written for it
  (*"CACHED HERE, not one level down"*).
- ✅ **FIXED 2026-09-28 by writing the test, not by deleting the parameter** -- the comment's
  argument was right, and the path had still never executed. `Test-ContentCatalogIntegrity.ps1` calls
  `Get-CatalogAsset ... 1`, and one millisecond provokes it because `git cat-file` cannot finish that
  fast. The kill and the refusal both ran for the first time, and the run prints *"the catalog-asset
  timeout path refuses rather than falling back"*.

  Asserted on the MESSAGE, with a negative control: a wrong path or an unreadable repo also throws
  and would have made this look like a pass, so the same asset is also read at the default timeout
  and must succeed.

  ⚠ **My first detector reported the correctly-firing path as broken.** It matched
  `timed out|timeout`; the real message is *"git cat-file did not return within 1ms ... Refusing to
  fall back to the working-tree bytes"*. It matches the REFUSAL now, which is the load-bearing half
  -- the whole point of the path is that it does not silently hash the worktree instead of what
  raw.githubusercontent serves.
- ✅ **FIXED 2026-09-28.** Both now describe petstudio and testmodule, and both say the
  script's own lists are the authority rather than the prose. `Test-ModuleSelfTests.ps1` also records
  WHY each is uncovered -- petstudio's `BehaviourChainSelfCheck.RunChecks` needs a pet XML fixture the
  host supplies from its own resources, testmodule is a deliberately minimal ABI fixture -- so the
  next reader does not have to guess whether a gap is expected.
### Doc rot the repo does not assert

- ✅ **FIXED 2026-09-28, and ASSERTED rather than just corrected.** Both said 31 against a
  measured 32. Correcting a number nobody re-measures only resets the clock, which is exactly why the
  Readme project count and the self-test count are assertions -- so `runtime-hardening-selftest.ps1`
  now counts the companions whose author is `Converted from a Shimeji skin` and requires both
  `MAPPING.md` and `Program.cs` to agree with the corpus.

  MUTATION: changing `MAPPING.md` to 33 fails with *"it says 33, there are 32"*, naming both numbers.
  SMOKETEST.md 149 -> 153.

  The "future date" half needed nothing: `MAPPING.md` dated a change to 2026-09-28, which was one day
  ahead when the audit read it and is today's date now.
### Recorded, deliberately not filed as defects

- ✅ **`release.yml` leaves the signing PFX on disk if the import throws** -- and the scrub step
  then exits early, because it keys on a thumbprint output that was never written. The step's own
  rationale is *"A hosted runner is torn down anyway; a self-hosted one is not, and this is the
  difference between the two"*, and a private key on disk is precisely what a self-hosted runner
  retains. **Not actionable here and recorded rather than filed:** this repo's release workflow runs
  on GitHub-hosted runners, where the runner is destroyed after the job, and no signing certificate
  is configured -- `build.yml` runs the build on every PR with no certificate at all. It becomes
  real the moment either of those changes. `runtime-hardening-selftest.ps1` asserts only that
  `if: always()` sits on the step, so the assertion passes while the property it names does not hold
  on the failure path.

- ✅ **`FormCompanion.Play()` does not set `hwndFullscreenWindow` on the blocked-monitor path**,
  which is what the new `IsFullscreenBlocked` accessor reads. The auditor could not turn it into a
  user-visible symptom -- the pet is `Visible = false` there so it cannot paint or be clicked,
  `Play`'s own scan feeds `NoteFullscreenScan` so the next tick's 300 ms-cached answer agrees, and
  animation intervals are far below 300 ms. Recorded because it sits directly on top of that day's
  change, not because it is a bug.
