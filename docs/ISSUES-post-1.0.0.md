# Bug post-mortems from v1.0.0 on

The numbered bugs found after the v1.0.0 rebase, extracted from `BACKLOG.md` so that file can
hold only open work. **Every one of them is fixed**, so nothing here is a work item —
[`../BACKLOG.md`](../BACKLOG.md) is the backlog. The one-line "none open" summary and the
fixed-in table live in [`DESIGN-REGISTER.md`](DESIGN-REGISTER.md), which is where a reader goes to
check whether a bug number is taken; the post-mortems themselves stay here. The pre-1.0.0 equivalent
is [`ISSUES-pre-1.0.0.md`](ISSUES-pre-1.0.0.md).

**The diagnosis text is kept in full, including the parts that turned out to be WRONG** — the
strikethrough in BUG-001 included. Two of them were wrong in instructive ways: BUG-003(a)'s suspected
cause was refuted by measurement, and BUG-001's mechanism was mis-attributed once before it was traced
properly. BUG-001's own entry says why that is preserved rather than tidied away:

> Recorded rather than deleted, because the wrong turn is the lesson: the log line that "proved" the
> app was doing everything right (`success=True`) was the thing that was broken, and two
> investigations in a row built theories on top of it instead of questioning it.

Each entry ends with what was actually changed and how it was verified.

BUG-001 to BUG-004 are cited by number from code comments in `modules/AiBrain/`, `modules/PetStudio/`
and `src/dotNet/`, from [`RELEASE-CHECKLIST.md`](RELEASE-CHECKLIST.md), from
[`../handoff.md`](../handoff.md) and from `.github/workflows/build.yml`. The numbers are never reused;
the next bug filed in `BACKLOG.md` is BUG-013 (BUG-009 to BUG-012 were filed on 2026-09-29 by the full audit).

| | |
|---|---|
| Bugs | BUG-001 to BUG-012. The gate counts the `BUG-nnn` headings below and holds the register's "next one filed" to the highest of them (`tests/runtime-hardening-selftest.ps1`, the next-bug block), so this row describes rather than counts |
| Found | BUG-001 to BUG-003 on 2026-09-10 by the maintainer using the shipped build, BUG-004 by the release checklist's own leak soak; BUG-005 (2026-09-22), BUG-006 and BUG-007 (2026-09-23) by the maintainer using the shipped build; BUG-008 (2026-09-23) by `agentflow_classifier.py --audit`; BUG-009 to BUG-012 (2026-09-29) by the full code audit |
| All fixed by | BUG-001 to BUG-004: the v1.1.0 tag (2026-09-10; BUG-001 took host 1.1.1 → 1.1.3). BUG-005 to BUG-008: by 2026-09-23 (the emitter change plus the `reloop` migration; agentflow 1.4.2 to 1.4.4). BUG-009 to BUG-012: the 2026-09-29 campaign lanes, remembrance 1.0.17, blinkingled 1.0.6, aibrain 1.1.14 (a decision pinned by a test) and petstudio 1.1.18 |

---

## 🐞 Known bugs (post-1.0.0)

Numbered so they can be cited. BUG-001 to BUG-003 were found by the maintainer using the shipped build,
not by a gate; BUG-004 was found by the release checklist's own leak soak. The entries below run newest
first, except that BUG-006 sits ahead of the four 2026-09-29 entries (the campaign scaffolding pre-filed
them below it, commit 40b0734).

**All four of those were fixed before the v1.1.0 tag (2026-09-10).** The diagnosis text is kept in full below,
including the parts that turned out to be WRONG, because two of them were wrong in instructive ways: the
suspected cause of BUG-003(a) was refuted by measurement, and BUG-001's mechanism was mis-attributed once
before being traced properly. Each entry ends with what was actually changed and how it was verified.

### BUG-006 — auto-approve could not see most Codex prompts, and said nothing about it

| | |
|---|---|
| Bugs | BUG-006 the Codex reader anchored on an optional control |
| Found | 2026-09-23, by the maintainer leaving an unanswered prompt on screen and asking why nothing pressed it |
| Fixed by | agentflow 1.4.2 — re-anchored on the card, six assertions, and a live-editor probe |

**The reader's first line decided there was no prompt.** `CdpApprover.CodexReadExpression` opened
with `querySelector('button[aria-label="Approval options"]')` and returned `'none'` when it found
nothing. That selector is the split button that opens Codex's approval-scope menu — and Codex
renders it only when it has a wider grant to offer. Verbatim from the shipped bundle
(`openai.chatgpt 26.917.62051`, `webview/assets/app-initial-*.js`):

```js
p = c.scopedApproveAction
N = p == null ? jsxs(XS,  { ..., type: 'submit', ... })            // plain button
              : jsxs(Pgi, { ..., secondaryAriaLabel: 'Approval options', ... })
```

So a prompt with nothing scoped to offer — a language runtime, anything Codex will not generalise
into "allow similar commands" — renders the plain branch, carries no such element, and was
invisible. That is not an edge case; it is most prompts.

**The anchor was chosen for the right reason and was still wrong.** The 2026-09-21 note in the
source argued that every class on that card is a Tailwind layout atom (`ms-auto flex min-w-0
items-center gap-2 ...`) and that the aria-label was therefore the only stable hook. Both halves
were true of the prompt in front of it. What was missed is that the hook belongs to a control the
card only sometimes renders, so "stable" was being measured on the wrong axis: it was stable across
*builds* and absent across *prompts*.

**Nothing in the log could have said so, and that is the second defect.** The reader has exactly
one way to say "no prompt" and the sweep treats it as the normal idle case, so a missed selector and
an editor with nothing on screen produce the identical silence. This is the same conflation as the
2026-09-18 `Unreachable`-vs-`NoPrompt` bug in the same file, one level further down — and the signal
that would have separated them was right there in the DOM: `forms=1` with two buttons in it. The
diagnostic log for the whole episode reads as a healthy module.

**The self-test asserted the bug.** `SelfCheckCodexTransport` carried
`probe.Check("the reader anchors on the split button's aria-label", ...)`. It was true, it was the
defect, and it passed — the same failure mode as BUG-005's third site, where a test encoded the
wrong discriminator and would have blocked the fix.

**Fixed** by anchoring on the card's own Tailwind container name, `@container/approval-card`, which
is what the authors called the thing rather than how they laid it out, and from which every
responsive class on the card (`@max-md/approval-card:...`) is derived — so it cannot be renamed
quietly. Options stay scoped to the enclosing `<form>`, which groups the actions and excludes the
header and command body. The dropdown trigger is now optional and, when present, still excluded from
the options by identity. The clicker was re-anchored identically, because a read and a click that
count different buttons make an index mean two different rows.

**Verified in the installed app, not only by the self-test.** The same unanswered prompt was read
by both expressions over CDP: the old anchor answered `none`, the new one answered
`{"options":["Deny Esc","Allow once ⏎"],"disabled":[false,false]}`. The module was then built,
installed to `%LOCALAPPDATA%\Programs\Desktop AI Companion\modules\agentflow\` and launched against
that still-open prompt:

```
12:44:20.290  [module] module loaded: agentflow 1.4.2
12:44:22.698  [agentflow] auto-approve clicked for a Codex command: pressing option 2,
              recognised as 'allow once' (approve-once); declined 0 wider or mode option(s)
```

Pressed 2.4 seconds after load. Re-probed afterwards: card gone, `forms=0`, reader `none` — so the
new anchor does not fire on an idle panel either. Six assertions replace the one that asserted the
bug, including two that fail if either expression ever again abandons a prompt for want of a
dropdown. `docs/agentflow/agentflow_cdp_probe.py` replays both reads against a live editor and warns
when its copies have drifted from the module.

**The second defect was fixed separately, in 1.4.3.** The silence above is not a consequence of the
selector — it is its own gap, and it would hide the next selector change just as well as it hid this
one. There is now a fifth `ReadOutcome`, `Blind`: the top-level anchor missing still answers `none`,
but anything structural BELOW it answers `blind`, and the sweep reports that as a build problem
rather than as an idle editor. The companion says it out loud, because the whole failure mode is
that an approver which has gone blind is indistinguishable from one with nothing to do.

It is a reporting outcome and never an actionable one: nothing is pressed on a `blind` read, since
not being able to read the options is precisely the state in which pressing would be a guess.

Cost, measured rather than assumed — `agentflow_cdp_probe.py --time 40` against the live editor,
median of 40 round trips per expression per target: **no extra CDP round trips and no extra DOM
queries**, because `blind` is a different return value from a call the sweep already made. The read
expressions grew 49 characters (Claude) and 2 (Codex), and the measured evaluate cost was unchanged
at 0.3–0.8 ms per target. A whole sweep of four webview targets costs a few milliseconds once every
ten seconds.

Verified for the failure that would actually hurt — a false alarm every ten seconds: across six
minutes of live polling over four webview targets with no prompt on screen, `blind` was never once
reported. The `blind` branch itself has NOT been seen in the wild, and cannot be until a shipped
build changes its markup; it rests on the assertions, not on an observation.

### BUG-012 — Companion Studio decodes and tiles the whole sprite sheet on the UI thread on every analyse

| | |
|---|---|
| Bugs | BUG-012 the analyser stages the pet through Xml.TryReadXml, which re-parses, base64-decodes and GDI+-decodes the sheet into up to 1,024 tile bitmaps per analyse, on the dispatcher thread, after every typing pause |
| Found | 2026-09-29, by the full code audit (finding F155); the backlog had recorded the second decode as refuted, and the refutation read the cached WPF path rather than the staging path |
| Fixed by | petstudio 1.1.18 -- `PetAnalyzer.Analyze` adopts the validator's parsed graph for the reachability walk (one parse, no sprite staged), and `PetStudioWindow.BeginAnalyze` runs the analysis on a pool thread and renders it on the dispatcher behind a generation check; the module self-test asserts the staged frame count and the adopted parse on every gate |

**Found by reading, against a record that said it was not there.** `PetAnalyzer.Analyze` ran the host's
validator once to get the parsed `RootNode`, and then, to learn which four animations the engine can
enter (drag / fall / kill / sync), built `new Xml(1)` and `new Animations(xml)` and called
`xml.TryReadXml(animationsXml, ...)`. That method is the host's whole pet loader. It parsed and
XSD-validated the text a second time; the validator proved the sheet, which is a full GDI+ decode
(`Image.FromStream(stream, true, true)`); `ReadImages` base64-decoded the sheet again, decoded it with
GDI+ a second time and cut it into up to 1,024 `Format32bppPArgb` tile bitmaps; a `SpriteFrameStore` took
ownership; `LoadAnimations` read the four ids; and the `using` block disposed everything. Every caller of
`Analyze()` was on the dispatcher: the 750 ms `DispatcherTimer` after each typing pause, the Re-analyze
button, Open, the installed picker, and the continuation that lands a finished import. The window's own
comment beside `SpriteKey` called the sheet decode "by far the window's largest allocation" and cached it
for that reason, while this path defeated the cache once per pause. The audit's verifier timed the shipped
1.1.17 DLL warm, in-process, six reps: 30-39 ms per analyze on the bundled sheep, 373-428 ms and 204 MiB
of managed allocation on shimeji-1l2yvz73 (6.9 MB of XML, a 3328-pixel sheet), 613-634 ms and 294 MiB on
shimeji-3g8t9v4e (9.9 MB, 3584 pixels), of which `TryReadXml` alone was 449-510 ms. Those are warm
in-process figures from the audit, not a cold measurement, and they are quoted for the shape of the
cost, not as the saving.

**Why the record said otherwise.** The 1.1.10 entry that fixed the double classification also refuted
the claim "`PetAnalyzer.Analyze` decodes the sprite sheet a second time". It read lines 144-150, where
the base64 STRING is copied into the report, and the window's `SpriteKey`-guarded `PetSprite.TryDecode`,
both correctly, and wrote "there is no second decode" without reading the staging block fifteen lines
further down. That refutation then went into the audit's known-items digest as "refuted, do not
refile", and it took two independent verifiers, each reading the staging block, to reopen it. The lesson
is about refutations rather than decodes: one that names what it read and not what it did not read
cannot be checked by the next reader, and this one was carried for two days on its confidence alone.

**Why the suite did not see it.** Nothing asserted what the staged `Xml` held. The stage built its frames
and disposed them before `Analyze` returned, so no report field, no self-test and no invariant could
count them, and the analyzer's header ("Deliberately UI-free") was read as a statement about cost. A
check that cannot see the frames cannot count them, which is why the fix records what the stage did on
the report itself.

**The fix, 1.1.18.** Two halves. The stage now ADOPTS the parse: `xml.AnimationXML = root;
xml.LoadAnimations(animations);` over the `RootNode` the analyzer's own `TryParse` had already
produced, so the host's `LoadAnimations` and its `ResolveMagicAnimations` (exact name, then a name
containing the word, then the lowest id) still supply the engine's exact entry ids, from one parse, with
no base64 pass and no bitmap; `SpriteCount` stays 0. The host lane's `TryReadXml(text, stageImages:
false)` overload, grown for this finding, was declined because it parses and validates the text a second
time and the validator's proof is itself a GDI+ decode of the sheet; mirroring the magic-name rule in the
module was declined for the drift source-linking exists to prevent (both under `#### fix/petstudio` in
`docs/DESIGN-REGISTER.md`). And the analysis leaves the UI thread: `PetStudioWindow.BeginAnalyze` runs
`PetAnalyzer.Analyze` under `Task.Run` behind an `Interlocked` single-flight gate and a generation
counter, the same shape as `AiBrainModule.BeginVramProbe`, and its dispatcher continuation renders only
a result no newer request has overtaken; a request that arrives mid-flight is remembered and run when
the flight lands, because the text it describes is newer. The WPF `PetSprite.TryDecode` stays on the UI
thread, once per sheet, behind `SpriteKey`. What runs per analyze now, on a pool thread: one validating
parse (the XSD deserialize, the base64 of sheet and icon, the validator's GDI+ proof decode),
`LoadAnimations`, the reachability walk and `BuildNodes`. What runs per analyze on the dispatcher: the
report, the map and the status. No timing is claimed, because none was measured cold in interleaved
fresh processes; the property is the claim. Making the status a single continuation also fixed F165 (the
timeline's dropped-step note was written and overwritten inside the old synchronous `Analyze`) and the
PetStudio half of F429 (an import announced "the host would reject it" for a pet the validator had
accepted).

**How it was verified.** `Analyze` records `StagedSpriteFrames` (the staged `Xml`'s `SpriteCount`) and
`StagedFromParsedGraph` (`ReferenceEquals(xml.AnimationXML, root)`) on the report, and
`PetStudioModule.SelfTest` asserts 0 and true on the embedded fixture, beside two WITNESS lines: the
fixture's sheet has 176 tiles to decode, so 0 is a choice and not an empty sheet, and the fixture's
drag, fall, kill and sync still come back as roots, so the adopted graph was loaded. Two source
invariants in `tests/runtime-hardening-selftest.ps1` pin the thread and the ordering: `PetAnalyzer.Analyze`
is called exactly once in the window and inside the awaited `Task.Run`, and the generation is compared
before `RenderAnalysis`. Mutation-tested in `tests/mutate-selftest-guards.py` and
`tests/mutate-hardening-guards.py` under `# ---- lane fix/petstudio ----`: the 1.1.17 `TryReadXml(text)`
put back FIRED on "decodes no sprite frame"; `TryReadXml(text, false)` put in FIRED on "adopts the
validator's parsed graph"; the analysis moved back inline FIRED; the generation compare removed FIRED.
Both `--module-selftest=petstudio` and the host's `--petstudio-selftest` stay green, with the fixture
census unchanged by this half of the release (the classifier change is F150's, and has its own record).

### BUG-011 — the Scroll Lock blinker's belief about the LED drifts from the LED

| | |
|---|---|
| Bugs | BUG-011 a refused SendInput still flips _phaseOn (finding F116), and Start() zeroes it against a key BlinkOnce() lit (F115); after an odd refusal the cadence runs inverted and Stop()'s corrective toggle no longer fires when the LED is lit |
| Found | 2026-09-29, by the full code audit |
| Fixed by | blinkingled 1.0.6 -- `Toggle()` reports whether Windows accepted the keypress and every writer of `_phaseOn` (`BlinkOnce`, the cadence tick, `Stop`, `Start`) moves it only with the key; the module self-test drives acceptance and refusal through a keypress seam, on any machine. On 2026-09-30 `Start()` moved from reconciling the belief to adopting the key (N-blinkingled-01, variant C, lane fix/followups, still 1.0.6); the bracketed notes below say where |

**Found by reading, and it could only have been found by reading.** `_phaseOn` is the blinker's one record of
whether the Scroll Lock LED is lit, and nothing ever re-read the hardware into it. Four places wrote it, and
three of them wrote it without asking the key.

The first is the one that needs no user at all (F116). `Toggle()` was `void`, and both of its callers did
`Toggle(); _phaseOn = !_phaseOn;`. Windows declines a synthesized keypress by returning 0 from `SendInput`
(UIPI against an elevated foreground window, a UAC prompt on the secure desktop, a locked session), the
key does not move, and the flag flipped anyway. After an odd number of refusals the flag is the inverse of
the LED for the rest of the session: the interval assignment then keeps the LED lit for the DARK gap and
dark for the LIT one (on Glacial, lit four minutes and dark four seconds), and `Stop()`'s gate,
`if (_phaseOn)`, is false exactly when the LED is lit, so a tray Off, a Caps Lock stop or exit leaves
Scroll Lock stranded on. A Win+L lock with the pet running was a coin flip per lock, which is the shape of
the intermittent "sometimes stuck lit" report two releases had already been cut for.

The second needs the diagnostic button (F115). "Blink once now" is live while the feature is off, and it
leaves the key lit with `_phaseOn` true and `_running` false. Enabling the feature afterwards, from the
pane's Apply or a speed in the tray, reaches `Start()` and never `Stop()`, and `Start()` wrote
`_phaseOn = false` against the lit key: inverted cadence, disarmed `Stop()`. The 1.0.5 changelog said
"ticking the feature on afterwards ran the whole cadence inverted" and presented it as fixed; the 1.0.5 fix
removed `if (!_running) return;` from `Stop()`, which repaired blink-then-off and nothing on the enable path.

The third was `Stop()` itself: `_phaseOn = false` unconditionally after a corrective toggle that could have
been refused, which re-armed the second bug through the next `Start()`.

**Why the suite did not see any of it.** The check "blink-once flips the phase it records" asserted the flip
whether or not Windows had moved the key, so on a runner that refuses synthesized input it passed BECAUSE
of the defect. The stop probe covered BlinkOnce then Stop and never BlinkOnce then Start, so the 1.0.5 claim
had no check behind it. And on this box, which accepts every synthesized keypress, no real `SendInput`
could ever show the refused branch, so nothing here could have been asserted through the hardware at all.

**The fix, 1.0.6.** `Toggle()` returns `sent != 0`, and delivers through a `KeypressSender` seam whose default
is the real `SendInput`. `BlinkOnce()` and `Tick()` flip only on true. `Stop()` drops the belief only when the
key reads dark or the clearing toggle was accepted; after a refused one the belief stands and the next
`Stop()` or `Start()` retries. `Start()` reconciles instead of zeroing: `_phaseOn = _phaseOn && reader()`,
with the first interval taken from the phase, so the blink the user just made becomes the first lit phase
of the cadence (the two alternatives, clearing the key first and adopting whatever the key reads, are
weighed under `#### fix/blinkingled` in `docs/DESIGN-REGISTER.md`). *[Superseded on 2026-09-30: the
coordinator chose the adopting alternative (variant C, N-blinkingled-01, lane fix/followups). `Start()`
now reads the key into the belief, `_phaseOn = ScrollLockReader()` (`engine/ScrollLockBlinker.cs:200`),
so a Scroll Lock the user had lit before enabling runs the cadence from its lit phase instead of
inverted, and `Stop()` then clears it (`ScrollLockBlinker.cs:232-238`), which is what makes the Readme's
"stopping always leaves the light off" hold for a key the module did not light. A key the user lit is
still left alone while the blinker was never started. The decision is recorded under
`#### fix/blinkingled` in `docs/DESIGN-REGISTER.md`.]*

**How it was verified.** The module self-test constructs blinkers whose keypress is a fixed acceptance or a
fixed refusal and whose key reader is a fixed answer, so every branch runs on any machine: a refused
blink-once and a refused cadence tick leave the phase where it was (WITNESS: accepted ones move it and arm
the lit interval); `Start()` after a blink-once keeps the belief, arms the LIT interval and presses nothing
(WITNESS: a key that no longer reads lit is dropped; a lit key never lit is not adopted) *[since
2026-09-30 that last WITNESS reads the other way: `Start() adopts a lit key it never lit, and arms the
LIT interval` (`BlinkingLedModule.cs:1007`), WITNESS a dark key it never lit adopts dark and arms the
dark gap (`:1021`), WITNESS a key the module lit is still cleared by `Stop()` (`:1040`)]*; a refused
corrective toggle keeps the belief and the retry clears it. Ten mutation cases in
`tests/mutate-selftest-guards.py`, eight new under `# ---- lane fix/blinkingled ----` and the two F114 cases
re-pointed at the new `Stop()`, each put one shipped shape back and each FIRED naming its own assertion.
*[2026-09-30: two of the eight were re-pointed at the adopting `Start()`
(`tests/mutate-selftest-guards.py:1046-1074`, one of them inverted into "Start() stops adopting a lit key
it never lit (the 1.0.6 first cut)"), and lane fix/followups added "Start() adopts the inverse of what the
key reads" under its own anchor (`:2297-2299`).]*
Measured on this box as well: the 1.0.5 suite left Scroll Lock ON from OFF and ON from ON (F113); the
1.0.6 suite makes two real keypresses, asserts they are paired, and leaves the key OFF from OFF.

**What was not verified.** A refusal on a real install. Producing one needs an elevated foreground window or
a locked session and would toggle the user's own key, so the chain from `sent == 0` to the flag is read from
the code and driven through the seam, not observed at the keyboard. The remaining trust point is the
hardware read itself, `Control.IsKeyLocked(Keys.Scroll)` from a background process, which `Stop()` has
relied on since 1.0.4 and which the audit's probe on this box found immediate.

### BUG-010 — an unprompted remark is a vision turn although the setting says vision is for explicit asks

| | |
|---|---|
| Bugs | BUG-010 OnDrop calls Ask(pet, true), so with "Use vision" on every random drop captures the screen and sends an image, while the label, the settings comment and the brain's own comment say vision is for the hotkey and the poke (finding F066) |
| Found | 2026-09-29, by the full code audit |
| Fixed by | aibrain 1.1.14 (lane fix/aibrain, 2026-09-29), as a DECISION rather than a code change: the owner ruled that vision, when enabled, applies to every remark, unprompted drops included. The code stood; the label, the settings comment and the brain's routing comment changed to say what the code does, and the module self-test now pins the drop's `allowVision: true` so the next change to that argument is a visible decision rather than a drift |

**The code and the record disagreed from aibrain 1.2.3 onward, and the code was the one running.**
`AiBrain.AskAboutScreenAsync` routes a turn with `useVisionPath = _useVision && allowVision`. `OnDrop` has passed
`allowVision: true` since the 1.0.0 snapshot (`0936658`, line 723 of the file as it was then), while `OnPokeReaction`
passes `false`. Against that stood four sentences saying vision was for explicit asks only: the pane label ("Use
vision on explicit asks"), the comment beside it, the `UseVision` field's own doc in `AiSettings.cs`, and the
routing comment in `AiBrain.cs` citing "backlog 6.2". Nothing asserted either version.

**How the disagreement arose.** The 6.2 rule (`82580b612`, 2026-07-27) was written for the module's OWN idle loop,
when a full-screen glance on gemma3:4b took about 68 seconds and every unprompted vision ask timed out; forcing
that loop onto the text path was the fix, and the 896 px downscale that landed in the same change is what made
vision fast enough to be unprompted at all. aibrain 1.2.3 (`896dd688d`, 2026-08-27) then deleted the loop and made
the host's global drop the only unprompted schedule. Its commit message decides the schedule, the cooldown and the
fate of the screen-change gate, and says nothing about routing, so the drop kept the `true` it had always carried
while the sentences written for the dead loop went on describing a path that no longer existed.

**Why the audit rated it high, and why that was fair.** With a cloud provider and consent granted, a screenshot of
the foreground window left the machine every 15 +/- 3 minutes under a label that promised images on explicit asks
only. Locally the cost was latency: a cold vision load per drop, about 11 s against about 5 s on the text path by
the module's own measurement. The consent screen and the label are the two things a user reads before enabling a
feature, and both said the wrong thing.

**The decision.** Asked on 2026-09-29 whether the drop should go back to the text path, the owner said: "it should
always use vision, because how else would it know what is on the screen to react to?" So vision, when enabled,
applies to every remark about the screen: the hotkey, the tray row and the unprompted drop alike. OCR stays as the
fallback for vision off and for a model that cannot see (`ChooseModel` substitutes or declines). The poke reaction
is the one entry point that stays on the text path, because "the code stands" includes `OnPokeReaction`'s `false`
and its reason still holds: a vision glance is too slow to feel like a reaction to a click.

**What changed.** The label now reads "Use vision (send a screenshot, not OCR text, with each remark)"; the
`UseVision` doc comment and the routing comment in `AiBrain.AskAboutScreenAsync` describe the decision and cite this
entry; `OnDrop` says at the call why its `true` is deliberate; the owner's decision is recorded at the top of the
campaign section in [`DESIGN-REGISTER.md`](DESIGN-REGISTER.md), with the poke exception under `#### fix/aibrain`.
`PRIVACY.md` needed no change: it already says an image of the screen is sent "for supported requests" without
naming a trigger. The one sentence outside this lane's boundary, "routed hotkey-only" at
`HISTORY-post-1.0.0.md:45`, is a dated record of what Phase 6 did and is left as history *[with a
bracketed pointer to this entry beside it since 2026-09-30]*.

**How it is pinned.** `--module-selftest=aibrain` now drives a real `AiBrainModule` through ModuleKit's
`RecordingHost` (`engine/AiEngineProbe.Module.cs`). A one-field seam, `AskSinkForDiagnostics`, receives a started
turn in place of `AskCoreAsync`, so the routing decision every entry point makes is recorded BEFORE anything needs
a screen, a model or a network: the drop asks with vision allowed, the poke without, the tray row with. The seeded
settings turn the brain on with auto-start off and the local slot on the OpenAI-compatible protocol, so building
and retiring the brain touches nothing on the machine.

**Verified:** `--module-selftest=aibrain` RESULT=PASS with the new lines; the mutation `return Ask(pet, true);`
to `return Ask(pet, false);` in `OnDrop` FIRED on "BUG-010: the unprompted drop asks WITH vision allowed"
(`tests/mutate-selftest-guards.py`, "the unprompted drop stops allowing vision (the fix the owner declined)"),
source restored byte-identical.

**The lesson is the one BUG-008 taught from the other side.** A behaviour described in four places and asserted in
none is described by whichever sentence the reader happens to open. The label is what the user reads, the code is
what runs, and only an assertion makes the two move together.

### BUG-009 — Remembrance stops recording by waiting on an event that is posted to the thread doing the waiting

| | |
|---|---|
| Bugs | BUG-009 AudioRecorder.Stop waits up to 10 s per source for RecordingStopped, which NAudio posts through the WinForms synchronization context captured at construction on the UI thread, the same thread that is blocked in the wait; every stop costs 20 s and a Restart Manager or session-end exit kills the process before the WAV headers are finalised (finding F168) |
| Found | 2026-09-29, by the full code audit; both verifiers traced it, one by decoding the shipped NAudio.Wasapi.dll |
| Fixed by | remembrance 1.0.17 — every capture is constructed with no SynchronizationContext current, so RecordingStopped is raised on NAudio's capture thread; the stop path logs its own timing |

**The mechanism, in one sentence:** NAudio's `WasapiCapture` reads `SynchronizationContext.Current` once, in its
constructor, and raises `RecordingStopped` through it ever after. Recording starts from a hotkey or a tray click,
on the WinForms UI thread, whose context is a `WindowsFormsSynchronizationContext`, so the event was delivered by
`Control.BeginInvoke` to that thread. `AudioRecorder.Stop` then waited for the event on that same thread, with
`ManualResetEventSlim.Wait(10 s)`, which does not pump messages. On the normal stop path this never showed,
because Stop ran on a pool thread while the UI thread kept pumping. On the one path 1.0.11 built the synchronous
save for — shutdown, after `Application.Run` returned — there was no pump at all, so every wait ran to its bound:
10 s per source, 20 s with the default microphone-plus-system, deterministically, with the tray icon and the pets
already gone. A session end or a Restart Manager exit kills the process inside that window, before `DisposeSource`
has patched the RIFF headers: no mixed WAV, no transcript, two unfinalised scratch files that (F171) nothing ever
purged. The 1.0.11 note and the StopRecording comment both described the 10 s as an upper bound. On the path the
flag existed for, it was the cost.

**Reproduced before fixing, on this box's real render endpoint**, with a `pwsh -STA` probe that installs a
WinForms context, constructs a `WasapiLoopbackCapture`, starts, stops, and waits with no message loop:

| construction | `syncContext` NAudio kept | RecordingStopped signalled | wait |
|---|---|---|---|
| context current (1.0.16) | WindowsFormsSynchronizationContext | no | 10 008 ms |
| context nulled around the `new` (1.0.17) | null | yes, on the capture thread | 33 ms |

**The fix** is `AudioRecorder.OpenCapture`: read `SynchronizationContext.Current`, set it null, construct, put it
back in a finally. The handler then runs on the capture thread — the thread that was writing the WAV anyway — and
the wait is signalled within about one buffer period. The wait itself stays, as a ceiling for a wedged capture
thread: the other fix the finding offered, dropping it and trusting `Capture.Dispose()` to join, was rejected
because `WasapiCapture` nulls its thread field before it raises the event, so a Dispose landing in that window
skips the Join and the handler would still be disposing the writer while DisposeSource did the same. The silent
keep-alive stream added for F169 is built inside the same window, since `WasapiOut` captures the context the same
way for `PlaybackStopped`.

**Two things around it changed with it.** The shutdown branch of `StopRecording` no longer announces — there is
no loop left to speak through, and the `Post` sat before the save as the one unguarded call on that path (F175) —
and it logs `stopped on shutdown: audio saved as <name> in N ms (capture stop N ms, mix N ms)`, so the trade
1.0.11 made can be read off a real exit rather than assumed. A save left running by a NORMAL stop is now tracked
in `_pendingSave` and waited for at shutdown, bounded, with the wait logged (F176).

**Verified** by a self-test that could not have existed before: `RecorderSelfCheck.FakeCapture` reproduces NAudio's
threading exactly — context read once in its constructor, `RecordingStopped` posted through it when there is one
and raised inline on its capture thread when there is not — and is driven through the real `AudioRecorder.Start`
and `Stop` under a context whose posts never run, which is what the UI thread is from the shutdown path. The
marker reads `captures are constructed with NO SynchronizationContext current (BUG-009)` and `RecordingStopped is
delivered on the capture thread, not posted to a dead message loop (Stop took 192 ms; capture stop 46 ms)`.
MUTATION: delete the `SetSynchronizationContext(null)` line and the fake posts to the dead loop, both lines FAIL
and Stop runs to the full bound per source; the harness case is `remembrance: captures are built with the UI
context current (BUG-009)` in `tests/mutate-selftest-guards.py`. A fake that raised the event from anywhere
convenient would have passed with the defect in place, which is why the fake's fidelity is the whole test.

**Not verified here:** a real recording on a console session, which is what `BLOCKED.md` T25 has always needed.
The check for whoever has one: record with the microphone and the system output both on, exit from the tray, and
read `stopped on shutdown: audio saved as ... (capture stop N ms, mix N ms)` in the diagnostic log. The
capture-stop figure should be well under a second for two sources; 1.0.16 would have spent twenty there, and
nothing in its log said so.
### BUG-008 — the option table went stale against 2.1.280, and the audit could not see it

| | |
|---|---|
| Bugs | BUG-008 two option labels shipped in 2.1.280 that the table did not know, and an audit blind to both |
| Found | 2026-09-23, by `agentflow_classifier.py --audit`, while building the equivalent audit for headers |
| Fixed by | agentflow 1.4.4 — both labels classified, and the audit scoped so it could see them at all |

**Found by a gate, which is the only entry in this file that can say so.** Every other bug here was
found by the maintainer using the shipped build. This one had no symptom anybody had noticed.

Claude Code 2.1.280 labels the prompt's buttons from a pair of ternaries:

```js
G  = ($.toolName === "ExitPlanMode");   V = ($.toolName === "AskUserQuestion")
_0 = "Yes";  if (G) _0 = q === "auto" ? "Yes, and use auto mode" : "Yes, and auto-accept";
             else if (V) _0 = "Submit answers";
m5 = "No";   if (G) m5 = H ? "Send feedback and keep planning" : "No, keep planning";
```

`Yes, and use auto mode` and `Send feedback and keep planning` were both new, and the table — last
verified at 2.1.274 — had neither. One unrecognised option refuses the whole prompt, by design, so
the prompt was declined with the log saying the capture had probably misread the screen. It had not.
The table was six releases stale.

**THE FIRST VERSION OF THIS ENTRY CLAIMED MORE THAN THAT, AND WAS WRONG.** It said auto-approve
"had silently stopped working on every plan prompt". It had not, because it never worked on plan
prompts and must not: `G` is `toolName === "ExitPlanMode"`, and on that prompt the primary row is a
mode change, the second row is `Yes, and manually approve edits` — also a mode change — and the
third declines. **There is no approve-once row on a plan prompt at all.** So the prompt was refused
before the fix and is refused after it. What changed is WHICH refusal gets reported, not whether
anything is pressed. The claim was written from the severity of the mechanism rather than from the
prompt it actually applies to, and it was caught by being asked "so do we have a BUG-008?" rather
than by any check here.

So the defect is real and its blast radius is the LOG, which puts it in the same family as BUG-007:

* the refusal said *"either the capture misread the prompt or the agent shipped a new option"*, when
  the truthful answer was "this prompt offers nothing I am allowed to press". Both sentences decline
  the prompt; only one of them is true, and the false one sends its reader looking for a broken
  screen capture.
* the companion said it out loud, once per prompt, for the same wrong reason.
* no such refusal appears anywhere in the maintainer's diagnostic logs, so it is not observed in the
  wild either — recorded because "found by a gate" and "never actually happened" are both true and
  the second one is easy to leave out.

**The latent hazard is the part worth keeping.** The option that was being masked is a PERMANENT
MODE CHANGE, and unrecognised-option noise is exactly the pressure that talks somebody into
loosening the matcher — a prefix on `"yes, and "` would have made every one of these press. The
allowlist held because it fails closed. What failed was the maintenance, which is what an audit is
for, and the audit was blind.

**That blindness was the second defect and the more interesting one.** The pattern was `"Yes..."` or
`"No, ..."` across the whole bundle, so `Send feedback and keep planning` and `Submit answers` were
invisible: an audit reporting OK while missing three labels is worse than no audit, because it is
trusted. Widening the verb set across 5 MB of minified editor produced **174** hits — "Allow",
"Continue" and friends are ordinary UI words. The fix needed both halves: SCOPE the scan to a window
around the prompt component's own style class, and only inside it widen the verbs. Region plus
narrow verbs yields exactly the ten real labels and nothing else. If that class is ever renamed the
scan falls back to the whole bundle and says so, because a scoped search that matches nothing would
otherwise report a clean audit — the same failure this entry is about, one level up.

**`Submit answers` is deliberately still unclassified**, and the audit prints that decision with its
reason on every run rather than burying it in an ignore list. It is the approve row of an
`AskUserQuestion` prompt: pressing it would submit whatever answers happen to be selected, which is
answering for the user, not approving a call they asked for. No `OptionKind` means "recognised, and
never ours to press", so leaving it Unknown gets the right behaviour through a message that blames
the capture — the same wrong sentence described above, now knowingly. A `NeverPress` kind would fix
the wording without changing a single press. That is an open design question, not an oversight.

**The wrong sentence was then fixed properly, which is the part that actually helps.** Classifying
the two labels turned *"the capture misread the prompt"* into *"no approve-once option present"* —
accurate, and still the wrong shape, because it is filed under `refused:` alongside genuine faults.
A prompt that is simply not this module's to answer is not a malfunction. `Decide` now falls back to
the notification the notify half would have given:

```
a prompt is waiting for a plan, and nothing on it approves a single call -- left for you
```

The companion already said so out loud; this is the log catching up with it. The split is carried by
a `RefusalKind` value rather than by the wording, because a caller branching on prose is a caller
waiting to break — and the existing assertion for this case DID branch on the wording, matched
`"no approve-once"`, and broke. It now asserts the `pressed` out-param instead, which is the thing
that must never change.

**Verified** by the difftest (78 cases, C# and the Python reference agreeing), `--mutate` (5/5
fired), and assertions that the whole 2.1.280 plan prompt is now understood and still not pressed —
recognising both rows must not make a prompt whose only approve-shaped option changes the permission
mode suddenly pressable — that a genuine unrecognised option is STILL reported as a refusal, so
softening the benign case did not soften everything, and that all four refusal kinds are
distinguishable as values.

**One more gate had quietly stopped checking, found on the way out.** `difftest-prompt-options.py`
verifies that every `OptionKind` the C# defines is covered by its mapping, and it found them by
scanning the whole of `PromptOptions.cs` for `Name = <n>,`. Adding a second enum to that file made
it report `RefusalKind.None` as an unmapped `OptionKind` and fail a differential with nothing wrong
in it. Now scoped to the `OptionKind` block, and it fails loudly if that block cannot be found —
an unscoped scan standing in for a scoped one, for the third time in this episode.

### BUG-007 — nine of the fourteen prompt shapes logged as "an unrecognised prompt"

| | |
|---|---|
| Bugs | BUG-007 the prompt-header table was four rows against fourteen shapes |
| Found | 2026-09-23, in the maintainer's own log, while verifying the BUG-006 fix |
| Fixed by | agentflow 1.4.3 — table re-derived from bundle 2.1.280, longest-match, plus the template row |

**Cosmetic, and worth a number anyway**, because what it corrupted was the log — the one artefact
this module asks users to attach to an issue, and the thing every diagnosis in this file was built
from. A prompt that was recognised, classified correctly and pressed correctly wrote itself down as:

```
auto-approve clicked for an unrecognised prompt: pressing option 1, recognised as 'yes'
```

which reads as the safety net firing at the exact moment it worked perfectly. The same sentence is
what a genuine classifier refusal would produce.

`DescribeSubject` falls back to a header table when a prompt names no tool in a `<strong>`. The
comment above that table said the bundle had "FIVE shapes". Re-reading every `permissionRequestHeader`
render site in 2.1.280 found **fourteen**, of which only the generic fallback uses `<strong>`. Four
were listed, so nine were unnamed — including the shell prompt, which is the commonest of all and
which could never have been a row because it is a template: the bundle renders
`["Allow this ", commandLabel, " command?"]`, with the tool's own word for itself in the middle.

**Fixed** by re-deriving the table, adding the shell template as an explicit prefix+suffix check
anchored at BOTH ends (the prefix alone also covers "Allow this glob command" and "Allow this
search", which are different tools with their own rows), and switching from first-match to
**longest-match**. That last one is not tidiness: "allow searching in &lt;path&gt;?" is `Search` and
"allow searching for this query?" is `WebSearch`, and under first-match-wins over a prefix table the
answer depends on which row happens to be declared first. Whitespace is collapsed before matching,
because removing the path span leaves a double space where it used to be.

The allowlist property is unchanged and is the reason this is safe: every value on the right-hand
side is written in the source file, so an unrecognised header is still named rather than echoed, and
no text from the screen reaches the log.

**Verified** by eleven assertions covering all fourteen shapes, the template's three near-misses,
the prefix collision and the double-space case; 450 assertions, `RESULT=PASS`.

**And then against a live render, by accident.** Deliberate attempts to raise one had all failed:
a Claude permission prompt needs a session in `default` mode and every session on the box was in
`auto`, which is the mode that does not prompt. Four crafted commands were all allowed outright.
The prompt that settled it arrived unbidden, from the publish of this very fix:

```
13:11:43.634  [agentflow] auto-approve clicked for a shell command: pressing option 1,
              recognised as 'yes' (approve-once); declined 0 wider or mode option(s)
```

`a shell command`, off the real header, where every previous line of its kind had read
`an unrecognised prompt`. Worth recording that the first draft of this entry claimed the live check
had not happened, and was committed saying so — the evidence landed in the log four minutes later.

What is still missing is the maintenance story, not the verification. The option table in
`PromptOptions` is justified by `agentflow_classifier.py --audit`, which re-derives it from whatever
bundle is installed and is how it survived four releases. This table has no equivalent, so it will
rot silently the next time the bundle adds a shape. That is the obvious next thing.

### BUG-005 — a converted companion stutters: the same short animation replayed, or two frames held for eleven seconds

| | |
|---|---|
| Bugs | BUG-005(a) the self-looping performance, BUG-005(b) the inflated performance dwell |
| Found | 2026-09-22, by the maintainer watching the shipped build. (b) was reported AFTER (a) was fixed and read as "the fix didn't work" |
| Fixed by | host-side emitter change plus the `reloop` migration; 25 animations for (a), 85 for (b), across 26 of the 31 converted companions |

**One root cause with three independent sites.** A Shimeji action DECLARES its intent:
`Type="Move"` is travel, `"Stay"` is a hold, `"Animate"` is a performance played through once.
`PetEmitter.IsRestingPose` read that attribute and its own comment states the principle outright:
"Type is the right discriminator because it is the source's own statement of intent." Three
neighbours inferred it from VELOCITY instead, and each produced a different visible defect.

**(a) `IsLocomotion` judged travel by velocity alone.** A trip moves the companion 8px a frame along
the ground, so it was indistinguishable from a walk and was handed the walk's edge set: "65% keep
going, 35% re-decide". That is 2.9 plays on average and five or more in 18% of runs. Reported as
Rick move #15 and Hornet move #19, and confirmed against the INSTALLED artefact (Hornet's `Tripping`
is animation id 19) rather than only the repo. Fixed by reading Type first; the change can only ever
REMOVE the locomotion classification, never grant it, so the climbs and grabs kept their loops.

**(b) `restsplit` inflated a performance into an idle dwell.** `Bouncing` re-entered nothing — its
single edge already went to the hub — and still played two frames at a flat 160ms with
`repeat="33"`, about eleven seconds. The format 0.6 → 0.7 migration decided what counted as a
lingering "performance" by velocity and hub-reachability, so a stationary Animate got a 9-12s rest
budget. The source says 2 poses of 4 ticks: a 320ms one-shot.

**The third site is a TEST, and it would have blocked the fix.** The dwell assertion in
`EmitterSelfTest` selected "idle rest" by zero velocity and required 9-12s from it. Adding a
`Type="Move"` fixture that never moves made it fail immediately. It now asks the parsed config for
the source Type and only skips when the source positively says otherwise, so no existing coverage
was lost.

**Two diagnoses of mine were WRONG and are kept here, because both changed a conclusion.**

- I filed the ten self-looping `jump` animations as a jump-detection bug and called it the next thing
  worth doing. Measured across all twenty `jump`/`jump_down` animations: not one rises. `jump` is
  `vy0 = 0` travelling -10px per frame. `Launches()` had nothing to detect, and the likely cause is
  the documented flattening of a too-weak rise. Retracted the same day.
- I reported that the Japanese and English stock confs disagree about `転ぶ`, the Japanese Tripping,
  because a regex census read its type as `固定`. The engine's own vocabulary table maps `固定` to
  **Animate**, not Stay. The confs agree. This is why the migration parses confs with
  `ShimejiParser.ParseActionsXml` and not a regex: the same switch also revealed that a regex counts
  nested `<Action>` references inside `Sequence` blocks, which is what made `jump` look disputed.

**Why a migration and not a re-conversion, with the number that settles it.** The source archives
existed but re-converting would regenerate identical pixels and silently discard hand edits, which
`rejump`'s comment had already warned about. After migrating, **28 of Hornet's 32 animations agree
exactly with a fresh conversion**; of the four that differ, two differ only in frame count with
identical repeat (its hand-edited `fall` / `Grapple3` swap) and one is a renamed action, meaning the
archive on disk is a slightly different revision of the skin than the one originally converted.

**Verification.** Mutation tested 4/4 FIRED against a green baseline with the built artefact's
timestamp asserted to advance: removing the Type check names `Stumble`, removing the velocity check
names `Brace`, restoring the idle-dwell path names `Bounce`, and weakening the fixture-count guard
reports that the rule is not being tested across both the travelling and stationary kinds. That last
case exists because the new whole-graph assertion was a ONE-case test until a stationary `Bounce` was
added beside the travelling `Stumble` — a fix handling only the moving kind would have passed. The
migration is idempotent by measurement, not by claim: a second run reports `changed 0`. Verified in
the real installed app, and against the bytes raw.githubusercontent actually serves.

---

### BUG-001 — the tray icon is missing after an MSI install that launches the app

**Reproduces every time on a fresh install.** Run the MSI, leave "launch when finished" ticked, and the
notification-area icon is **not present**. Restarting the app puts it there. This is the intermittent
fault the diagnostic log was built for in Phase 4b; it is now a reliable repro, which it never was before.

**What the evidence rules out.** The app is not failing to create the icon and is not being overruled by
the user's shell preference:

```
Tray  tray icon set: success=True icon=True visible=True text='Desktop AI Companion'
Tray  tray icon: visibility left as the user set it
```

`Shell_NotifyIcon` reported success, the icon object was non-null, and `HKCU\Control Panel\NotifyIconSettings` holds `IsPromoted=1` for the exact installed executable path — so
`TrayPromotion.ShouldPromote` correctly declined to touch an explicit user choice. The process is alive
and responding, and the installed binary is the expected one. Everything the app controls is right.

**Most likely mechanism.** The MSI launches the app from an **immediate** custom action, so the process
is started by `msiexec` rather than by the shell, and `NIM_ADD` lands while the taskbar is not in a state
to keep it. `Shell_NotifyIcon` returning TRUE does not guarantee the shell retained the icon. The classic
fix is to handle the `TaskbarCreated` registered window message and re-add the icon when it arrives —
which also covers an Explorer restart, a case this build does not handle either. Worth checking whether
`ProcessIcon` subscribes to it at all before designing anything more elaborate.

**MECHANISM ~~CONFIRMED~~ WRONG (2026-09-10, first attempt).** This entry previously claimed the
cause was the terminate path: that `util:CloseApplication`'s `TerminateProcess="1"` force-killed the
app, the `using` around `ProcessIcon` never unwound, `NIM_DELETE` was never sent, and the shell was
left holding a slot for a dead owner. **That was reasoned, not measured, and the maintainer refuted it
in one sentence:** they closed the sheep *manually* from the tray, then installed the release, and the
icon was still missing. A manual close runs `KillSheeps(true)`, which disposes the icon as its FIRST
action, so `NIM_DELETE` was sent and no slot was stale -- and with no process running, the installer's
`CloseApplication` never fired at all. Neither half of the terminate theory was in play.

Recorded rather than deleted, because the wrong turn is the lesson: the log line that "proved" the app
was doing everything right (`success=True`) was the thing that was broken, and two investigations in a
row built theories on top of it instead of questioning it.

**ROOT CAUSE, measured 2026-09-10 on the live installed build.** The `NIM_ADD` is dropped by the shell,
and the app cannot tell:

- `HKCU\Control Panel\NotifyIconSettings` for the exact installed exe held `IsPromoted=1`, so the shell
  was being told to show it. Promotion was never the problem.
- Greenshot, installed to the same `%LOCALAPPDATA%\Programs` root with the same `UID=1`,
  `IsPromoted=1`, `InitialTooltip` and `IconSnapshot`, was **visible**. So the registry entry is not the
  differentiator; the difference is at runtime.
- A UI Automation walk of `Shell_TrayWnd` found 39 buttons and **no companion icon**.
- Sending the shipped `TaskbarCreated` handler to the running process made it **appear immediately** --
  40 buttons.

So the icon was genuinely never in the shell, and a re-add fixed it. The reason nothing caught this:
`ProcessIcon.SetIcon` sets its `success` flag false ONLY when the `try` block throws. It never captured
`Shell_NotifyIcon`'s return value, and `NotifyIcon.Visible = true` does not report whether the shell
accepted the add. **A dropped add and a working one logged byte-identical lines.**

**It is INTERMITTENT, and the entry's "reproduces every time" was also wrong.** Measured across three
starts of the same binary: one msiexec-launched start dropped it, a direct launch was fine, and a later
msiexec-launched start -- real installer, licence accepted, "Launch" ticked, app parented to msiexec --
logged `shellHasIt=True` on the first add. The launch method correlates but does not determine it.

#### FIXED in host 1.1.1 (2026-09-10) -- verify the add, and repair it

**`TrayIconPresence` asks the shell the question the app could not.**
`Shell_NotifyIcon(NIM_MODIFY)` returns FALSE when the shell holds no icon for a given
`(hWnd, uID)`. WinForms keeps both private, so they are read by reflection (`_window`, `_id`), cached
once, and reported as **unknown** rather than guessed at if a future runtime renames them. The
primitive was validated BEFORE anything was built on it -- shown icon answers True, hidden icon
answers False -- because a check that always succeeds would have been worse than no check.

**`ProcessIcon` now verifies after startup and repairs.** A backed-off schedule
(1.5s, 3s, 6s, 12s, 20s) re-adds the icon on a definite "absent", and deliberately:

- **never repairs on `unknown`** -- that would re-add on every tick of every healthy run, which is the
  failure mode of a blind retry loop;
- **does not stop at the first success** -- the shell can accept an icon at 1.5s and drop it at 6s
  while it is still settling after an install, so the whole schedule runs. Five `NIM_MODIFY` calls
  cost nothing measurable and a healthy run logs nothing at all.

**The log line is now honest**, which matters more than the repair: it reads
`noThrow=... shellHasIt=True|False|unknown` instead of a `success=True` that only ever meant "no
exception was thrown". That single word is what hid this bug through two investigations.

**Proven by FAULT INJECTION, not by waiting for a bad run.** Since the natural failure is
intermittent, the exact broken state is manufactured instead: `NIM_DELETE` behind WinForms' back
leaves the shell holding no icon while the app still believes it is shown -- precisely what a dropped
`NIM_ADD` leaves. Verified live, from outside the process, against the real app:

```
INJECT: delete the icon behind the app's back
  icon in tray: False        <- fault took hold
wait for the app to notice (schedule runs to ~22s)
  icon in tray: True         <- repaired itself
```

**Verification:** `--traywatcher-selftest`, 30 assertions, wired into `tests\run-gate.ps1` and
`build.yml`. The chain that matters: `WinForms still believes the icon is visible` (the blind spot) ->
`the check DETECTS the dropped icon` -> `the repair puts the icon BACK in the shell`. Also asserts the
reflection seam still exists, so a future .NET rename fails loudly instead of silently disabling the
fix.

#### CONFIRMED IN THE WILD, and re-tuned in 1.1.2 (2026-09-10)

The maintainer installed v1.1.1 fresh from GitHub with "Launch" ticked. **The fault reproduced, and
for the first time the instrumentation caught it:**

```
12:26:58.642  tray icon set: ... shellHasIt=False        <- the initial add WAS dropped
12:26:59.858  tray icon MISSING from the shell (check 1); re-adding
12:27:02.863  tray icon MISSING from the shell (check 2); re-adding
12:27:08.863  tray icon MISSING from the shell (check 3); re-adding
12:27:20.862  tray icon MISSING from the shell (check 4); re-adding
12:27:40.878  tray icon recovered after 4 repair attempt(s)
```

This settles the diagnosis by direct measurement rather than inference: `shellHasIt=False` on the very
first add, four rejected re-adds, then recovery. The icon the user saw was put there by the repair.

**Two defects the capture exposed, both fixed in 1.1.2:**

1. **The refusal window is far longer than assumed, and recovery landed on the LAST attempt.** The
   shell refused at 1.5s, 3s, 6s, 12s and 20s; the icon only returned on the check after that, about
   42 seconds after start. The original five-step schedule ended at exactly that point, so a slightly
   more stubborn shell would have exhausted it and left the user with no icon and a "giving up" line.
   `MaximumAttempts` is now 9, running out to roughly 2.7 minutes. The cost on a healthy machine is
   four extra `NIM_MODIFY` calls and no log output.

2. **The re-add line misattributed its own cause.** All four repairs logged "tray icon re-added after
   TaskbarCreated" when no shell restart had occurred -- the presence check had triggered them.
   `ReassertIcon` now takes a reason and logs it (`presence check 1`, `TaskbarCreated`, ...). Worth
   calling out rather than quietly fixing: a log line that misstates why it fired is the same defect
   class as the `success=True` line that hid this bug for two sessions, and it was introduced by the
   fix for that very bug.

**What this does NOT establish.** The trigger is still unexplained. The launch method correlates
(msiexec-launched starts are where it has been seen) but does not determine it -- an earlier
msiexec-launched start accepted the icon first try. The fix is deliberately mechanism-agnostic: it
verifies and repairs regardless of why the shell refused, which is why it worked here without the
cause being known.

#### The INSTALLER side, fixed in 1.1.3 (2026-09-10) -- and it was never WM_CLOSE

Testing 1.1.2 on a real upgrade surfaced a second, separate symptom the maintainer had seen before:

> "The setup was unable to automatically close all requested applications." ... "it appears to hang when
> its done, but eventually closes."

**The 1.1.0 belt aimed at the wrong message.** An MSI verbose log settles who closes the app during an
install:

```
13:52:23.379  RESTART MANAGER: Will attempt to shut down and restart applications in no UI modes.
13:52:23.534  RESTART MANAGER: Successfully shut down all applications that held files in use.
13:52:24.079  Doing action: Wix4CloseApplications_X64      <- WiX runs half a second LATER
```

**Restart Manager gets there first**, and RM speaks the SESSION-END protocol
(`WM_QUERYENDSESSION` / `WM_ENDSESSION`) -- never `WM_CLOSE`. So the `util:CloseApplication` belt added
in 1.1.0 was listening for a message no installer sends, and the app was always force-killed by
`TerminateProcess`. Its self-test passed throughout, because it proved the WIRING (send WM_CLOSE, get an
orderly exit) and never that the installer would send that message.

The backlog already had the other half of the answer and it went unused: WinForms *does* answer
`WM_QUERYENDSESSION`, but on a hidden broadcast window on a BACKGROUND thread that owns no forms, so it
agrees and nothing shuts down. The `TaskbarWatcher` is top-level and on the UI thread, which is why the
message can be acted on there.

**Two rounds were needed, and the first was wrong in an instructive way.** Handling
`WM_QUERYENDSESSION` by answering TRUE and waiting for `WM_ENDSESSION` produced this, measured on a real
repair:

```
14:08:47.298  session end queried (installer or shutdown); agreeing to close
              ... and nothing further. WM_ENDSESSION never arrived.
```

RM took "yes" as an undertaking to exit, waited, and terminated the process. **Answering yes and then
doing nothing is worse than never answering**, because RM believes it has an agreement, and that wait is
exactly the "hang" the maintainer described. The app now exits on the QUERY, posted through the UI
synchronization context so the answer reaches RM before the message loop stops. Measured after:

```
14:14:02.822  session end queried (installer or shutdown); agreeing to close
14:14:02.823  session end: removing the tray icon and exiting immediately
```

One millisecond apart; a direct probe measured the whole exit at **0.29s**, with the tray icon gone
afterwards, i.e. `NIM_DELETE` sent rather than a slot left behind. Deliberately NOT `KillSheeps(true)`:
that path lingers about a second for the farewell animations, which is charming when the user chose to
quit and fatal against RM's deadline.

**A repair now relaunches the pet.** Reported twice: "the sheep again did not re-launch after a repair".
The launch was gated on `NOT Installed`, true for any maintenance run, and a repair is the case where it
matters most -- RM closes the app so the files can be replaced, so the old condition left the user with
no companion, no tray icon, and nothing to show the repair had finished: strictly worse off than before
they started. Now gated on `REMOVE<>"ALL"`, which still never launches an exe it has just deleted. Two
surface assertions pin both halves.

**VERIFIED END TO END on 1.1.3 (2026-09-10).** Maintainer ran a real repair: no "unable to close"
dialog, no hang, and the pet came back on its own. The log confirms the incoming 1.1.3 instance took its
icon on the first add (`shellHasIt=True`), and the outgoing one shut itself down cleanly one millisecond
after agreeing to. **BUG-001 is closed**, across all four of its symptoms: the dropped tray icon, the
retry schedule that recovered on its last attempt, the Restart Manager dialog with its hang, and the
repair that left the user with nothing.

Every one of those four was found by the maintainer running a real install, and none by a gate. The
instrumentation is the reason each was diagnosable rather than merely reportable, and the sequence
1.1.0 -> 1.1.3 is a record of what happens when a fix is reasoned about instead of measured.

#### The two 1.1.0 belts, kept -- but they are NOT what fixes this

Written for the refuted terminate-path theory, and retained because each covers a real case this
does not:

- **`TaskbarCreated`** re-adds the icon when the shell rebuilds its notification area, which covers an
  Explorer restart. That was a genuine second defect: the message was not observed anywhere in the
  codebase before 1.1.0.
- **`WM_CLOSE` -> orderly exit** routes the installer's close request into `KillSheeps(true)`, the same
  path the tray menu uses, so the icon is torn down on the way out. Needed no `.wxs` change, because
  `util:CloseApplication` already posts `WM_CLOSE` and nothing had ever turned it into a shutdown.
  Verified live this session: posting `WM_CLOSE` to the watcher exits the app cleanly.
  `TerminateProcess="1"` stays as the backstop for a wedged process and for the first upgrade hop,
  where the exe being closed is the old one without the handler.

### BUG-002 — the vision feature does nothing, silently, when the configured model is not installed

**Repro.** Set the vision model to something not present in Ollama (the shipped default `gemma3:4b` is
exactly this on a machine that has only Gemma 4), then trigger a screen reaction. The thinking dots
appear and then nothing happens, forever, with no error and no log line.

**Root cause, confirmed.** `AiSettings.cs:86` defaults `VisionModel = "gemma3:4b"`, and
`AiBrain.cs:136` falls back to the same string. When that model is absent the backend errors, and
`AiBrain.AskAboutScreenAsync` ends in a bare `catch { return null; }` at `AiBrain.cs:257-262`, commented
"never crash the app over the AI layer". The caller then "simply stays silent without special-casing
exceptions", exactly as the method's own summary says. So a missing model is indistinguishable from
"nothing interesting to say".

**Why it "worked once".** Maintainer report, 2026-09-10: using **Refresh local models** removed
`gemma3:4b` from the list. So the setting can hold a model that was valid when chosen and is silently
no longer offered afterwards, which matches the observed sequence exactly (worked, changed model to
smoke-test, changed back, never worked again). That makes the real defect broader than a bad default:
**a saved model id is never re-validated against what the backend currently has.** A refresh that drops
a model should say so, or clear the setting, rather than leaving it pointing at nothing.

**The part that makes it undiagnosable:** modules reach the log through `IHost.Log`, and all of
`modules/AiBrain/` contains **one** such call. The log added in Phase 4b to make invisible faults
visible has effectively no instrumentation in the one subsystem that fails by returning null on
purpose. **That was true when this was written and is no longer:** AI Brain is fully instrumented as
of 2026-09-11 (27 diagnostic lines, routed through a static `LogSink`), and what remains of that work
item — Fortunes, PetStudio and BlinkingLed, all still at zero — is "instrument the three modules
still at zero" in [`../BACKLOG.md`](../BACKLOG.md).

**Fix, in order of value:**

1. Log the swallowed failure. Keep returning null, but record the model, the endpoint and the error
   category first. Nothing else here is diagnosable until this exists.
2. Say something the user can act on when the configured model is not in the backend's list — the pane
   already enumerates installed models, so "gemma3:4b is not installed" is available at the point of
   failure.
3. Stop defaulting to a hard-coded model id that may not be present. Prefer the first vision-capable
   model the backend actually reports, and only fall back to a literal when the list is empty.
4. Add `gemma4` / `gemma-4` to `VisionModelMarkers` (`AiSettings.cs:1443`). Low priority and **not** the
   cause of this bug: Ollama reports a real `capabilities` array which the module already honours, so the
   marker list is only the fallback for backends that report nothing. It has `llama4` but not `gemma4`,
   so it will mis-advise on such a backend.

**Not a bug:** every model currently installed here (`gemma4:12b`, `gemma4:26b`, `qwen3.6:27b`,
`mistral-small3.2:24b`) declares `vision` in `ollama show`, and `gemma4:12b` additionally declares
`audio`. Nothing needs pulling; the default just points at a model that is not there.

#### ✅ FIXED 2026-09-10 — all four items

1. **The swallowed failure is logged.** `AiBrain` gained a static `LogSink` that `AiBrainModule` points
   at `IHost.Log`, cleared on shutdown because it is a static holding a delegate over the instance. The
   bare `catch { return null; }` still returns null — a silent companion on a broken backend is correct —
   but now records the error CATEGORY, the model, and the endpoint **host**. Never the full URL: a base
   URL can carry a key in its query.
2. **The user is told, once.** `AiModelPolicy.ChooseModel` re-validates the configured id against what
   the backend reports and returns an actionable line the companion speaks through the existing
   `IHost.Say` — no new ABI member, so no further `MinHostVersion` bump. De-duplicated via
   `AdvisoryOnce`, because a companion repeating "gemma3:4b isn't available" every thirty seconds would
   be a worse bug than the one being fixed.
3. **No more trusting a hard-coded id.** When the configured model is absent, the first backend-reported
   model that can actually do the job is substituted. Critically, an **empty or absent** model list is
   treated as *unknown*, not as proof of absence — otherwise every offline start would accuse a perfectly
   good configuration. Resolution happens BEFORE the screen capture, so a request that cannot be sent no
   longer pays for a screenshot.
4. **`gemma4` / `gemma-4` added to `VisionModelMarkers`.**

The inventory is refreshed in `PrepareAsync`, i.e. while the app is already talking to the backend, via
an injected `ModelLister` wired to the same listing call the Options pane uses — so the pane and the
brain can never disagree about what is installed.

**Verified:** 18 new assertions in `AiEngineProbe`. Mutation-tested **7/7 FIRED**. The first run of that
harness reported 0/7, which was a false result, not a pass: it rebuilt the HOST project while the code
under test compiles into `AiBrain.dll`. The harness now asserts the module DLL's timestamp advanced and
that a witness assertion label appears in the probe's report before it trusts any PASS/FAIL.

---

### BUG-003 — screen capture returns the wallpaper, and follows the wrong monitor

**Reported 2026-09-10.** Two symptoms, and they have different causes:

**(a) The capture shows only the desktop wallpaper, not the foreground windows.**

**(b) Moving the companion to the second monitor still captures the first.**

**Symptom (b) is explained, and it may be a design decision rather than a defect.** Capture deliberately
follows the **foreground window's** monitor, not the companion's. `ActiveWindow.CaptureContext`
(`src/dotNet/Ai/ActiveWindow.cs:120`) reads `GetForegroundWindow`, takes its rect, and calls
`DesktopGeometry.SelectCaptureMonitor(foregroundBounds, fallback, monitors)`; the companion's own
monitor arrives only as `fallback`, and its own doc comment says "if no usable foreground window exists,
fall back to the monitor containing the pet". `FormCompanion.CaptureScreenBounds` correctly resolves the
companion's monitor via `Screen.FromRectangle(Bounds).Bounds`, so the input is right and the preference
is what surprises.

So with the companion on monitor 2 and the active window on monitor 1, capturing monitor 1 is the code
working as written. Two defensible readings: react to *what the user is looking at* (current behaviour)
or *what is around me* (the reported expectation). **Pick one deliberately.** A reasonable resolution is
to prefer the companion's monitor when the foreground window is on a different one, since a companion
commenting on a screen it is not standing on reads as broken regardless of which is more useful.
Note `IsOwnWindow` already blanks the title and ignores the bounds when one of our own windows is
foreground, so an open Options window correctly falls through to the companion's monitor.

**Symptom (a) is not yet explained, but several plausible causes are eliminated.** Verified correct:

- the blit **does** pass `CAPTUREBLT` — `StretchBlt(..., Srccopy | Captureblt)` at
  `modules/AiBrain/engine/AiBrain.cs:374`, so this is not the classic missing-flag bug;
- the source is the screen DC, `GetDC(IntPtr.Zero)`, not a window DC or the desktop window;
- DPI is **not** the problem: `PerMonitorV2` is declared in both `src/Properties/app.manifest:31-32`
  and `Application.SetHighDpiMode` at `src/dotNet/Program.cs:46`, so `Screen.Bounds` is in physical
  pixels and cannot be virtualised out of alignment with the screen DC;
- bounds selection is sane, and symptom (b) shows the rect is a real monitor rect, not a degenerate one.

Leading remaining candidate: **GDI cannot reliably read DWM-composited content.** `BitBlt`/`StretchBlt`
from the screen DC is the legacy path; GPU-rendered and hardware-overlay window content is not
guaranteed to be present in it, and the wallpaper is what remains when it is not. The supported modern
answers are DXGI Desktop Duplication or `Windows.Graphics.Capture`. Confirm before rewriting anything:
if it is this, a plain Notepad window will capture fine while a browser or a video will not.

**Both are blocked on instrumentation, for the same reason as BUG-002.** Nothing records the chosen
rect, which monitor won, or whether the resulting bitmap was uniform, so this is currently unfalsifiable
from a user report — log the selected bounds, the foreground-vs-companion decision, and a cheap
uniformity check on the bitmap. **Since shipped**, with the rest of the AI Brain instrumentation; the
surviving half of that work item is "instrument the three modules still at zero" in
[`../BACKLOG.md`](../BACKLOG.md).

**Privacy constraint on any diagnostic here:** never write capture content, OCR text or a bitmap into
the diagnostic log. Users attach it to issues. If a visual dump is needed to diagnose this, it must be a
separate, explicit, opt-in, one-shot action that says where it wrote the file.

#### ✅ (b) FIXED 2026-09-10 — the companion's monitor wins

The maintainer picked "react to what is around me" over "react to what the user is looking at", because
it is also the reading that makes a per-monitor watcher possible. New
`DesktopGeometry.SelectCompanionMonitor` replaces `SelectCaptureMonitor` at the one call site in
`ActiveWindow.CaptureContext`. The foreground window is NOT discarded: it still supplies the title and
`ScreenContext.ForegroundWindowBounds`, so a front window that happens to be on the companion's monitor
is still the preferred capture *subject* within it. The two decisions compose — which monitor, then which
rect inside it — and `ChooseCaptureBounds` already falls back to the whole monitor when the window does
not overlap it, so a front window on another display cannot drag the subject away.

The companion's cached rect is still snapped through the selector rather than used verbatim: it can be
stale after a resolution change or a display being unplugged, and capturing a rect that is no longer a
monitor reads black.

**Follow-on defect this exposed, also fixed:** `AiBrain.DescribeOtherWindows` says "Also open on this
screen" but keyed its filter on the FOREGROUND window's `MonitorIndex`. Once capture followed the
companion instead, the phrase and the pixels could refer to two different displays — the model was handed
a list of windows the user could not see, next to a picture of somewhere else. Now keyed on
`ScreenContext.MonitorBounds` by intersection. It had **no test coverage at all** before this.

**Verified:** 4 new assertions in `CoreTests`, 11 in `AiEngineProbe` (including that a window TITLE never
reaches the prompt, which was previously trusted rather than asserted). Mutation-tested **7/7 FIRED**
across both runners.

#### ✅ (a) RESOLVED 2026-09-10 — the suspected cause was WRONG, measured

The leading candidate above — "GDI cannot reliably read DWM-composited content", implying a DXGI or
`Windows.Graphics.Capture` rewrite — is **refuted**. Measured on this machine over every visible
top-level window, comparing the product's own path (`StretchBlt` from the screen DC with
`SRCCOPY | CAPTUREBLT`) against `PrintWindow` with `PW_RENDERFULLCONTENT`:

| window | screen-DC (what we ship) | PrintWindow |
|---|---|---|
| Code, OUTLOOK, ONENOTE | content, 70-99 edges/kpx | content |
| msedgewebview2, WhatsApp | **content**, 76-78 edges/kpx | content |
| cmd | content, 122 edges/kpx | **blank** (returned false) |
| TextInputHost | content, 74 edges/kpx | **blank** |
| XboxPcApp, ApplicationFrameHost | content | 98% one colour |

The GDI path reads GPU-composited windows perfectly; `PrintWindow` is the path that returns blank
surfaces. **No capture-API rewrite is warranted**, and the backlog's proposed discriminator ("a browser
will fail where Notepad succeeds") does not hold here.

The most likely explanation for the original report is that (a) was **(b) in disguise**: capture followed
the foreground window's monitor, and a monitor the user was not working on is mostly wallpaper. That is
now fixed.

Because that is an inference rather than a reproduction, the instrumentation the entry asked for was
added anyway, so a recurrence is falsifiable instead of a matter of opinion:
`AiBrain.UniformityPercent` reports the share of a sparse sample taken by the single most common colour,
and the capture log line records the chosen rect, whether the window or the monitor won it, and that one
number. Geometry and a statistic only — never pixels, never OCR text, honouring the privacy constraint
above. 3 assertions cover it.

---

### BUG-004 — the leak soak's verdict was a coin flip, and it is the only gate that can catch a leak

**Found 2026-09-10 by running the [`RELEASE-CHECKLIST`](RELEASE-CHECKLIST.md) leak-soak step before
the v1.1.0 tag** — the first time either soak had been run since v1.0.0. `runtime-resource-soak.ps1` failed with
`GDI object growth exceeded the bound: 81 > 16`, and USER was over too (+57).

**Not a v1.1.0 regression, mechanically confirmed.** The churn path is
`RunResourceChurnPetCycle` + `RefreshTrayIconForResourceChurn` + `ContextMenus.RefreshSpeechMenuItem`.
`git diff v1.0.0..HEAD -- src/` touches only `PluginApi.cs`, `DesktopWindows.cs` (new), 
`CompanionHost.cs`, the csproj and `Program.cs` — and the entire `Program.cs` change is one early-exit
`--desktopwindows-selftest` branch that cannot execute during a churn run. v1.0.0 shipped this too; it
was simply never measured, because step 3 was skipped.

**The real defect is the MEASUREMENT.** The gate compares the FIRST sample against the LAST, and native
handles held by `Bitmap`/`Font`/`Icon`/`Form` finalizers are released only when the GC runs — so the
counter is a sawtooth and the verdict depends entirely on where the last sample happened to land.
Measured on one unchanged build:

| run | raw first-vs-last GDI | verdict it produced |
|---|---|---|
| 30 s | **+81** | FAIL |
| 60 s | **+206** | FAIL |
| 90 s | **−22** | PASS |

Within a single 60 s trace, GDI climbed 91 → 193, dropped to 58 in one sample, then climbed to 265. The
historical baseline recorded in `docs/HISTORY-pre-1.0.0.md` ("GDI −24") was a run that ended just after
a collection, so it was never evidence of health either.

**Per-step attribution** (added to the churn marker, since reading the code found nothing — every
explicit GDI site in the path disposes correctly):

| step | GDI net / 190 cycles | USER net | per cycle |
|---|---|---|---|
| pet + speech | +255 | +270 | +1.34 GDI, +1.42 USER |
| tray icon | −24 | −8 | clean |
| menu refresh | 0 | 0 | clean |

Ablating `Say`, `PaintSpeechForResourceChurn` and `DrawToBitmap` one at a time did **not** eliminate it,
so it is not one missing `Dispose` — it is spread across creating and destroying a top-level window plus
a speech bubble twice a second, which is not a workload any user generates.

**What settles it:** `SettleAndSample` forces `GC.Collect` → `WaitForPendingFinalizers` → `GC.Collect`
before reading the counters, at the start and end of churning. That converts an unanswerable question
into an answerable one: after everything collectable HAS been collected, did the process permanently
lose handles? Over 319 cycles the post-finalization growth was **+31 GDI, +21 USER — 0.097 and 0.066 per
cycle**, i.e. two orders of magnitude below the per-cycle allocation rate, which is the signature of a
warming cache rather than a linear leak. A periodic settled series was added to confirm the shape
directly rather than by inference from endpoints.

#### ✅ FIXED 2026-09-10 — there is no leak, and the gate now measures that

The settled series is conclusive. Post-finalization GDI over a 548-cycle, three-minute run:

| cycle | 40 | 80 | 120 | 160 | 200 | 240 | 280 | 320 | 360 | 400 | 440 | 480 | 520 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| GDI | 46 | 46 | 46 | 46 | 46 | 46 | 46 | 46 | 46 | 46 | 46 | 46 | 46 |

**Exactly flat**, for 500 cycles. The +31 from the cold baseline is a one-time warm-up — the first
sprite decode, cached fonts and brushes, the bubble's region, the tray icon — paid before cycle 40 and
never paid again. `Handles` and `USER` behave the same. **The app does not leak GDI, USER or kernel
handles**; every explicit disposal in the churn path was already correct, which is why reading the code
found nothing to fix.

`runtime-resource-soak.ps1` now asserts **post-finalization flatness** — the first post-warm-up settled
sample against the last — instead of raw first-vs-last. Warm-up is excluded by construction rather than
by a generous allowance, which is the same reasoning `module-window-soak.ps1` already used when it
compared its LAST segment against the previous one. `PrivateBytes` stays on the raw samples; it is not
finalizer-bound in the same way.

**Changing an assertion so that it passes is worthless unless it can still fail, so that was tested
directly** by injecting genuinely ROOTED leaks (held in a field, so no finalizer can reclaim them):

| injected leak | result |
|---|---|
| `CreateCompatibleDC` per cycle, never `DeleteDC` | **caught** — post-finalization GdiObjects over bound |
| rooted `Form` HWND per cycle | **caught** — trips GdiObjects first, since an HWND carries GDI objects too |
| *(control)* unmodified build | passes, `SettledGrowth` = GDI 0, USER −4, Handles −1 |

One instructive false negative found while building that check: a rooted **`System.Drawing.Font`** per
cycle sailed straight through. GDI+ `Font` and `Bitmap` are user-mode objects and do **not** necessarily
consume a handle `GetGuiResources` counts, so they are useless as test leaks — use a raw GDI handle
(`CreateCompatibleDC`, `GetHbitmap`) when writing one. This is worth remembering before concluding from
this gate that "nothing leaks": it measures OS handle counts, not GDI+ memory.

Per-step attribution, the settled series and the settled counters all remain in the churn marker, so the
next occurrence starts from data instead of from a code read.
