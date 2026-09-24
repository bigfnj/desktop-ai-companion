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

### Module SDK follow-ups

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

### Data loss or a crash, on a path that runs

- 📌 **`ScalePercent` is the one persisted field missing from both `Clone` and `MergeChangedFields`.**
  CLOSES-WHEN: grep-present src/Portable/AppSettingsStore.cs "ScalePercent = source.ScalePercent"
  Declared `src/Portable/AppSettingsStore.cs:48`, read as the global size fallback at
  `LocalData.cs:266` and `:284`. All 41 other persisted fields appear in both methods; the comment at
  `AppSettingsStore.cs:1124` documents exactly this failure mode — "a field not listed is silently
  dropped and Save still returns true". It survives today only because `SaveMerged` usually writes
  onto the document it just read back. Delete or corrupt settings.json (factory reset, or the
  corrupt-primary recovery at `LoadCore`) so `TryRead` does not return `Loaded`, and
  `target = Clone(settings)` drops it: every pet without a per-pet override silently reverts to 100%,
  and `Save` returns true. Same loss in `Load()`'s catch, which returns `Clone(_baseline)`.

- 📌 **Reminder wipes its fired-event set on every empty feed, so a restart re-nags.**
  `modules/Reminder/ReminderModule.cs:244` runs `_fired.RemoveWhere(...)` unconditionally, BEFORE any
  check on `snap.Error` or `events.Count`, and `:272` persists the emptied set. An empty feed is
  routine, not exceptional: `CachingCalendarSource.Fetch` returns empty with `Error: LoadingMessage`
  whenever `_cache` is null, which is its first call, and `Init` calls `CheckDue()` directly at `:146`.
  So it happens on the first tick of every launch, and again after every options Apply (`:669`
  rebuilds the source). A 10:00 meeting with lead 15 fires at 09:45; restart at 09:50; the set is
  wiped; the feed loads 20s later; `now` is still inside `DueNow`'s `[start-15, start+1]` window and
  the same meeting announces again with chime, animation and bubble. The module's own header at `:15`
  claims "remembers which fired so a restart never re-nags".

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

### A feature reports success while doing nothing

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

### Guards and checks that cannot fail

Ranked ahead of the rest because a check that cannot fail is worse than no check: it reports a
property nobody is testing. Each was verified by constructing the input that should break it.

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

### Blocking IO, pipe deadlocks, and measured cost

- 📌 **Two child-process drains are the textbook deadlock, and each makes its own timeout unreachable.**
  `tools/ShimejiConvert.Engine/Engine.cs:269-272` and `modules/Remembrance/Transcriber.cs:76-79` both
  do `ReadToEnd()` on stdout then stderr, then `WaitForExit(timeout)`. stdout only reaches EOF at
  exit, so the timeout is always called on a finished process and can never fire. Once the child's
  stderr crosses the 4 KB pipe default while the parent blocks on stdout, both stop forever: a long
  corrupt WAV emitting per-frame ffmpeg errors, or whisper.cpp's model-load banner. Remembrance's
  30-minute kill switch and `WhisperInstaller.TryVerify:713`'s 5-minute cap are both dead.
  `Engine.ProbeFfmpeg:310` additionally leaves the process running when its wait returns false.

- 📌 **Dragging the per-companion size slider rewrites a 1.17 MB settings.json per 25% step, on the UI
  thread.** `src/Portable/Wpf/CompanionsPaneControl.cs:409` → `StartUp.cs:1061` → `LocalData.cs:301` →
  `AppSettingsStore.SaveCore:1053` → `AtomicFile.TryWriteAllText`. The document embeds the active
  pet's animations.xml as base64 (`AppSettingsStore.cs:69`); measured on this box, the real
  settings.json is 1,167,948 bytes. Timing the durable-write half exactly as `AtomicFile` shapes it
  (WriteThrough + `Flush(true)` + `File.Replace` with backup), 10 iterations on C:, same volume as the
  real file: median 129.7 ms (min 116.9, max 143.6); the read + deserialize + serialize half is on top
  and was not measured. The slider snaps every 25 from 25 to 400, so one drag crosses 15 positions:
  roughly 2 s of blocked UI thread and ~35 MB of write-through traffic for one gesture.
  `ReloadPetType` does 5 full writes to reload 4 pets of one type.

- 📌 **`LocalJsonSource` parses up to 2 MiB on the UI thread, 180 times an hour.**
  `modules/Reminder/LocalJsonSource.cs:19` implements `ICalendarSource` directly instead of deriving
  from `CachingCalendarSource`, which exists so a slow fetch never runs on the caller's thread;
  `IcsUrlSource` and `OutlookComSource` both derive from it. `CALENDAR-FEED.md` describes the path as
  a work-side exporter's output, i.e. typically a share, so when the VPN drops `File.ReadAllText`
  blocks on the SMB timeout with the pets frozen, once every 20 seconds. The same doc's claim that "a
  parse failure is non-fatal, the companion keeps the last good feed" is false here: last-good
  retention lives only in `CachingCalendarSource.DoRefresh`.

- 📌 **Fortunes rebuilds its vector cache synchronously on the UI thread, on start and every Apply.**
  `modules/Fortunes/FortunesModule.cs:156` backgrounds only `Warm`; the `SmartFortunes` constructor is
  synchronous and its `VectorCache` ctor ends in `Load(...)`, which takes a Global mutex plus a
  `.lock` lease and then deserialises `cache.bin` with one `ReadSingle` per float and a
  `new float[384]` per entry. With all 161 catalog packs at "Everything" that file reaches ~94 MB:
  ~22.6M `ReadSingle` calls and ~59,000 allocations before the pet appears. `RebuildEngine` is reached
  from `Init`, `SavePaneValues`, `RescanAsync`, `ImportPacksAsync`, `DownloadPacksAsync` and
  `RebuildSmartIndexAsync`.

- 📌 **`FallDetect`/`RiseDetect` enumerate every top-level window, per pet, per tick.**
  `src/dotNet/FormCompanion.cs:1571` and `:1661` each allocate a dictionary, a fresh
  `EnumWindowsProc`, and a `StringBuilder(128)` + `GetWindowText` + `GetTitleBarInfo` per visible
  window. With 16 pets falling at once that is 16 identical, pet-independent enumerations per tick.
  The precedent is in the neighbouring file: `StartUp.BlockedMonitorsForStandDown:791` exists because
  the fullscreen z-order walk used to be per-pet, and its comment carries the measurement ("679
  top-level windows... 53 walks/s became 3.3"). `DesktopWindows.Snapshot` is the shared enumerator
  these two never adopted.

- 📌 **Opening the Companions pane re-reads and full-string-compares every installed pet's XML.**
  `src/Portable/Options/OptionsController.cs:48-77`. `IsActive` calls
  `CompanionCatalog.TryReadPetXml` (a full read; 158 KB for esheep64, 406 KB for hornet) then
  `string.Equals(xml, activeXml, Ordinal)`. Nothing is cached, unlike `GetStats` and
  `DisplayNameForId` which both are, and `Reload()` runs in the control's constructor, which is
  rebuilt on every pane selection and after every button press. A user with the full 54-companion
  catalog pays ~10 MB of synchronous reads per click.

- 📌 **`FormSpeech` rebuilds its window region, font and brushes every tick while a bubble follows a
  walking pet.** `src/dotNet/FormSpeech.cs`: the no-op guard at `:222` only fires when the pet has not
  moved, so `UpdateRegion` (`:339`, fresh `GraphicsPath` + `Region`) and `OnPaint` (`:386`, another
  path plus a `Pen`, a `Font` via `CreateFontIndirect`, a `SolidBrush` and a `StringFormat`) run 30-60
  times per 6 s bubble. Everything is disposed, so this is cost rather than a leak; `_style` and
  `_measuredDpi` already say exactly when the font must be rebuilt.

- 📌 **Audio clip resolution runs before the cache lookup and never negative-caches.**
  `tools/ShimejiConvert.Engine/Engine.cs:206-218`: `Resolve` does a recursive `EnumerateFiles` over
  the whole skin root BEFORE the cache is consulted at `:209`, and the failure paths at `:213-214`
  never populate it. 12 sounded actions sharing 2 oversize WAVs spawn 12 ffmpeg processes (each up to
  the 30 s wait) and 12 full recursive scans instead of 2.

### Converter correctness


- 📌 **`restdwell` gives the HUB the performance dwell, then stamps a version `restsplit` skips.**
  `tools/ShimejiConvert/Program.cs:1065` and `:1079` pass `RestDwellTargetMs` (11000) with no hub
  exception, while `RestSplit:1156` correctly picks `HubDwellTargetMs` (2000). `:1085` then stamps
  `ConvertedFormatVersion` ("1.1"), and `RestSplit` gates on "0.6". So a pet at format 0.5 gets an
  11 s hub dwell — 5.5x intended — and `restsplit` prints "skip (already at format 1.1)" with no
  migration able to reach it. The console line also says "N rest(s) shortened" while every one was
  lengthened. Latent today: all 31 shipped converted pets are already at 1.1. Broader shape: `rejump`,
  `reclimb`, `restdwell`, `restsplit` and `undirect` all stamp 1.1 rather than their own version, and
  nothing gates on 0.2, so a pet taken to 0.2 by `reweight` can never reach `rejump`.


- 📌 **`minHostVersion` is parsed, written to modules.json, then dropped from the catalog.**
  `packaging/New-ContentCatalog.ps1:181-190` copies id/name/desc/version/url/sha256/bytes/permissions
  and not `minHostVersion`; `grep -c minHostVersion catalog.json` is 0 against 5 in modules.json, and
  `src/dotNet/RemoteCatalog.cs:42-53` has no such field, so a catalog carrying it would be ignored
  anyway. AgentFlow declares `MinHostVersion = "1.2.0"`; every user still on 1.1.x is offered it in
  the Modules pane, downloads the payload, and `ModuleHost.cs:79-86` refuses it at load.
  `New-ModulePublish.ps1:128-133` carries the comment naming this outcome. Its first-publish branch
  (`:218-224`) never sets the key, and nothing checks parity with source, which has already drifted
  (`AiBrainModule.cs:140` declares "1.1.0"; the aibrain and reminder entries have no key).

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

### Dead code and unreachable branches

- 📌 **Verified to have no callers anywhere in `src/` or `modules/`.** Grouped because none is urgent
  and together they are one afternoon. `StartUp.SyncSheeps()` (`src/dotNet/StartUp.cs:2089`) and its
  only callee `FormCompanion.Sync()` — `AboutWindow.cs:21` confirms the behaviour was intentionally
  dropped, so the `sync` magic animation is parsed and never used. `StartUp.OnPetPoked()` no-arg
  overload (`:1848`). `StartUp.SetPetSize(string,int)` (`:1047`) and `LocalData.SetPetSizeLevel`
  (`LocalData.cs:227`) — worth knowing because the two size setters are destructive of each other,
  each doing `RemoveAll` then adding an entry carrying only its own dimension.
  `CompanionThumbnails.Get` (`:26`). `GenerationAwareIdleSchedule` (`StartUp.cs:2103`) has exactly one
  consumer, `SecuritySelfTest.cs:959`, so that self-test asserts the behaviour of a class nothing
  ships against. In tools: `PetEmitter.RestRepeatCount` (`:2425`, and it would be wrong if used — it
  hard-codes `RestDwellMs` so it cannot express the hub/performance split), the 6-arg `BlitOpaque`
  (`SpriteSheetBuilder.cs:395`), the 1-arg `Reloop` (`Program.cs:1655`). In modules:
  `AiSessionManager.DisposeWithin` (`:227`), `AiSettings.CredentialIdentity` (`:974`, which also
  decrypts the API key on every call), four in `FortuneProvider.cs`, `AnimCapability.cs:511`,
  `TimelinePane.cs:239`, `PetSprite.cs:27`.

- 📌 **Branches that can never be taken.** `ActionClassifier.cs:262`'s `&& !Has(cond, "activeIE")` —
  `:257` already returned Group2 for any condition containing it. `PetEmitter.cs:2209` and `:2210`'s
  `&& a.Animations[0].Poses.Count > 0` — `IsWallAction`/`IsCeilingAction` reject an empty pose list at
  `:409` and `:660`. `PetEmitter.cs:1501`'s `e != fall` — `fall` is added only to `all`, never to
  `spokes` or `chainSteps`, which are `BuildSpoke`'s only two sources.
  `modules/AgentFlow/BlockedDetector.cs:232`'s `isCodex` is always false there, because every Codex
  path returns inside the block at `:176-198`; the Codex arm of the ternary and its reason string are
  unreachable and the comment at `:229` explains a behaviour that cannot happen.
  `modules/Reminder/ReminderScheduler.cs:20` `DueNow` has zero callers and is a trap: it tests
  `firedIds.Contains(e.Id)` while the live set holds composite `"<id>@<lead>"` keys, so wiring it back
  in yields a scheduler that re-fires every event on every tick.

- 📌 **The settings/XML file-change callbacks are empty, so a second instance never sees the first's
  writes.** `src/Portable/LocalData.cs:942` and `:947` have empty bodies ("Not implemented in the
  portable build"), and PORTABLE is defined in both configurations with `Portable/LocalData.cs` the
  only `LocalData` compiled. That makes `StartUp.XmlFileChanged:383`, `OptionFileChanged:389`, the
  `isRealoadingSettings` field and `LocalData.LoadXML()` all unreachable.
  `Program.TryAcquireInstanceSlot` deliberately allows two concurrent instances, so changing the
  volume in instance A leaves instance B permanently stale. The data is not corrupted —
  `MergeChangedFields` only writes fields this process changed — so this is staleness, not loss.

- 📌 **The debug right-click menu throws on .NET 10 instead of opening.**
  `src/dotNet/FormCompanion.cs:2104` constructs `System.Windows.Forms.ContextMenu`, which .NET 9+
  ships only as a binary-compatibility stub: on the runtime this project targets
  (`Microsoft.WindowsDesktop.App/10.0.10`) the type carries `[Obsolete(DiagnosticId="WFDEV006")]` and
  `Activator.CreateInstance` throws `PlatformNotSupportedException`. Launch with Shift held
  (`StartUp.cs:182`) so `IsDebugActive()` is true, right-click a companion, and line 2104 throws
  before `timer1.Enabled = false` at `:2153` — the exception escapes into the message pump and the
  user gets the unhandled-exception dialog while the pet keeps animating.
  `DesktopAICompanion_Portable.csproj` suppresses WFDEV006 with the comment "kept for behavior parity
  during the migration"; there is no parity, the code cannot execute.

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
- 📌 `modules/Fortunes/engine/FortuneProvider.cs:1313` — `totalBytes += chargedBytes` runs before the
  read and the parse, so rejected files spend the 16 MB budget and starve valid packs later in name
  order, with no diagnostic.
- 📌 `modules/PetStudio/AnimCapability.cs:205` — the MOVE description reads only `StartX` while the
  classification at `:161` accepts `StartX` or `EndX`, so `blue_sheep`'s `fall_wind` renders as
  "travels 0px per frame". Four shipped skins carry animations of this shape.
- 📌 `modules/PetStudio/BehaviourChainSelfCheck.cs:309` and `:421` — the label is built from
  `hostError` before the `TryParse` that fills it, so a validator rejection always prints with its
  reason discarded.
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
- 📌 `modules/AiBrain/engine/FallbackBackend.cs:89` — `UnloadAsync` ignores its `model` argument for
  the local leg and always passes `_localTextModel`, though `ChatAsync` maps correctly via
  `LocalModelFor`. Under a cloud-primary fallback that loaded local `llava:13b` (~8 GB), the
  fullscreen release unloads the local TEXT model and the vision model holds its VRAM for the whole
  game — the outcome the setting's own comment calls "a crash guard, not a courtesy".
- 📌 `packaging/Normalize-MsiDeterminism.ps1:410` — the safe-delete guard is handed
  `Split-Path -Parent $stagingDirectory` as BOTH `-AllowedRoot` and `-TrustedRoot`, and a path is
  always strictly below its own parent, so the refusal before `Remove-Item -Recurse -Force` cannot
  fire. `installer/build-installer.ps1:223` passes an independent root and is real.
  `New-DeterministicPortableZip.ps1:268` has the same shape, mitigated by an independent validation.
- 📌 `tools/ShimejiConvert/Program.cs:1368` — the dedupe migration recomputes `CellHash` for both
  sheets once per animation FRAME, though every source-cell hash was computed at `:1316`. For 60
  animations averaging 20 frames that is 2400 full-cell hashes and LockBits pairs where under 400
  would do.

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
