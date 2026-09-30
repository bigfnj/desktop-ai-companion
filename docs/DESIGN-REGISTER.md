# Design register — consult before proposing

**What this is.** Closed knowledge that exists to be READ, not done. Every entry here came out of
[`../BACKLOG.md`](../BACKLOG.md) because it is a register rather than a queue: a refused design kept
beside the reasoning that refused it, an ABI gap noted so it is not mistaken for an oversight, a
behaviour that reads like a defect until you know why it is not, a measurement that refuted the
explanation everyone had written down, and a bug log whose entries are all closed.

**[`../BACKLOG.md`](../BACKLOG.md) holds only open work.** Nothing in this file needs doing, and
nothing in it should be deleted either — the knowledge is the point. It was extracted rather than
dropped under the maintainer's own rule: *"you can of course remove items from the backlog and the
history is stored in git. If you think there is value in something in there we should break it out to
its own document."*

**How to use it.** Before writing "the app is obviously missing X" or "this looks like a bug", search
this file for X. Several entries exist because the thing WAS proposed twice, or because a plausible
fix was attempted and had to be reverted.

| you are about to... | read |
|---|---|
| propose a design that looks obviously missing | [Settled decisions](#settled-decisions--do-not-re-propose) |
| write "a module cannot do X, so the ABI needs a verb" | [Known ABI gaps](#known-abi-gaps), then grep `PluginApi.cs` for the verb |
| file a converted companion's look as a bug | [Decisions that read as defects](#decisions-that-read-as-defects) |
| cite a bug number, or file a new one | [Known bugs (post-1.0.0)](#known-bugs-post-100) — the next number filed is BUG-009 |
| write a count into a document | [Numbers in documentation](#numbers-in-documentation) |
| explain why a warning did not fire, or trust a warning count | [Measurements that corrected an explanation](#measurements-that-corrected-an-explanation) |

The completed-work record is [`HISTORY-post-1.0.0.md`](HISTORY-post-1.0.0.md); the post-mortems are
[`ISSUES-post-1.0.0.md`](ISSUES-post-1.0.0.md); what cannot be actioned from this machine is
[`BLOCKED.md`](BLOCKED.md); product ideas that are not engineering debt are [`IDEAS.md`](IDEAS.md).

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
  [`HISTORY-post-1.0.0.md`](HISTORY-post-1.0.0.md).
- **The AgentFlow detector is C# inside the AgentFlow module**, not a consumer of the node/JS sibling
  `permission-wildcarding`. That project already reads both agents' history and owns the rule matcher,
  which is exactly why it was considered — but it is node/JS, and an MSI desktop app must not acquire
  a node runtime dependency in order to read an append-only JSONL file. What is worth taking from the
  sibling is the transcript parsing and the compound-command splitter, ported; not the runtime.
  Fork, submodule and daemon-naming were each considered and rejected. The measurements are in
  [`agentflow/README.md`](agentflow/README.md).
- **`totalCount` is DO NOT BUILD.** Zero occurrences across the 31 shipping skins. Now stated in
  `ActionClassifier`'s own reason text and pinned by a `ClassifierSelfTest` assertion that no
  classifier reason may promise unscheduled work.
- **Moving the user's windows is refused, not missing.** 48 Shimeji actions ask for it; desktopPet
  "cannot and should not move the user's windows". No work.
- **LightHost (`bigfnj/LightHost`) is NOT a fit for a microphone module.** It is a C++/JUCE realtime
  VST/VST3 *effects host* (routes device-in → plugin graph → device-out live); grep-confirmed it has
  **zero capture/record/encode code** — no `AudioFormatWriter`, no WAV/MP3, and it doesn't do
  system/loopback at all. It cannot be an in-proc C# module (C++ app, no DLL/C ABI), and as a
  separate process it emits nothing to record. Also GPLv3 via bundled JUCE + VST SDK, which would
  infect the MIT companion. Mic capture → **NAudio (already in the base)** does mic + WASAPI-loopback
  natively. Only revisit LightHost if realtime VST mic-cleanup (noise-suppression/EQ before
  transcription) ever becomes a hard requirement, and then as a separate GPL-isolated process, never
  in-proc.
- **Module→module calls do not exist, and nothing should be designed assuming them.** Each module
  runs in its own `AssemblyLoadContext` and talks only to `IHost`. There is no summarize/LLM verb on
  `IHost` — grep-verified across `PluginApi.cs`, where the only "brain" mentions are comments. The AI
  brain is itself a MODULE that consumes host events, not a service other modules can call. A module
  needing generation carries its own client (Remembrance's `OllamaSummarizer.cs` is the shipped
  pattern); the alternative, a host-level text-generation service on the ABI, is a deliberate ABI
  extension and a new "modules share a service" pattern, not a wiring job.
- **A browser Web Speech API transcription path was considered and REJECTED (2026-08-20)** on three
  independent counts: (1) it transcribes a **live mic only**, not a file or the system-loopback
  stream, so it cannot ingest a recorded mix or hear far-end participants — disqualifying alone for a
  meeting recorder; (2) classic mode is **cloud (Google)** and `webkitSpeechRecognition` works only
  in Google-branded Chrome — Electron/WebView2 throw a `network` error because Google restricts the
  endpoint, so an embedded browser cannot use it; (3) it would **re-add the WebView2 engine S5b-3
  deliberately removed**. Chrome 139's on-device mode (`processLocally` + `install()` language packs,
  Aug 2025) fixes the cloud/privacy count but not the mic-only or browser-dependency counts, and was
  flaky at release. Native `Windows.Media.SpeechRecognition` is local + browser-free but
  dictation-grade and live/stream-oriented — weak on a long multi-speaker call. **Whisper-class on
  the recorded file stays the pick**, and is what shipped in Remembrance.
- **Third-party module code-signing (stream S7) and TTS as a feature** were both dropped on
  2026-08-13; the reasoning is in [`HISTORY-post-1.0.0.md`](HISTORY-post-1.0.0.md).
- **`IHost.ContextChanged` is the push half for a reader that must REACT to a change, and
  Remembrance reading `ReadContext` instead is NOT the bug.** Decided 2026-09-17, after an audit
  found the event has zero subscribers anywhere in the repo. Reminder publishes `meeting.current`
  (`modules/Reminder/ReminderModule.cs:929`); Remembrance, the only consumer, calls
  `ReadContext` (`modules/Remembrance/RemembranceModule.cs:175`). The two readings on the table were
  "it exists for out-of-tree modules" and "Remembrance should subscribe, so the polling is the bug".
  It is the first, and the deciding detail is that Remembrance does **not poll**: it reads once,
  inside `StartRecording()`, at the instant the value is used. Three reasons that pull is the
  stronger pattern for this consumer, each readable in the code rather than argued:
  1. The host RETAINS the value. `CompanionHost._context`
     (`src/dotNet/Plugins/CompanionHost.cs:699`) is a dictionary that outlives every raise, so a
     reader arriving late still gets the current value, while a late SUBSCRIBER gets nothing until
     the next publish.
  2. The publisher publishes **only on change** (`ReminderModule.cs:927` returns early when the
     JSON is unchanged). One meeting publishes once, possibly an hour before the user presses
     Record, so "wait for the next event" is not a substitute for "read the value now".
  3. A subscription is one more handler `Shutdown` has to detach, and a handler that outlived
     `Shutdown` is the exact defect Remembrance shipped with on `HostShutdown` and fixed in
     `fe6ed35`. Taking on that hazard to obtain a value it can already read for free is a bad
     trade.
  So the channel's push half is for a module that must act ON THE CHANGE, which no module in this
  repo does yet, and `PluginApi.cs` now says so in the channel's own comment. **What the decision
  leaves open** is the raise itself: `CompanionHost.PublishContext`'s raise has never run in a test
  or under any shipped module, and that one assertion is the only part still filed in
  [`../BACKLOG.md`](../BACKLOG.md). The shipped test double already mirrors the raise
  (`ModuleKit.Testing.RecordingHost.PublishContext`), so an out-of-tree author can test a subscriber
  today; it is the host's own raise that nothing exercises.
- **A test-observation member that only the host needs goes on the HOST's fake, never on
  `ModuleKit.Testing.RecordingHost`.** Measured cost, 2026-09-17: all seven modules that reference
  ModuleKit do so **without** `Private="false"`, on purpose, so its DLL is copied into each module
  folder and ships in every `modules-dist/*.zip` (only TestModule references no ModuleKit at all;
  `modules/AiBrain/AiBrain.csproj:41-42` states the reason: "the host
  shares one contract, but the support library ships inside this module's folder so each load
  context carries its own copy"). `packaging/Test-ModulePublishFreshness.ps1` watches every
  ProjectReference that is not `Private="false"`, so **one added member in ModuleKit marks all six
  published payloads stale** and costs a six-module republish. That is precisely what adding
  `TrayConventions.cs` did, and it is why the unsubscribe assertion added to
  `ModuleConventionSelfTest` put its four `…HasSubs` properties on that file's own host-side fake
  instead. The contract assembly is the free one to edit: `DesktopAICompanion.Contracts` IS
  `Private="false"` in every module csproj, so the watch-set builder skips it and a change there
  marks nothing stale.

---


### Velocity is never scaled DOWN, and pet speed at minimum scale is the reason (2026-09-28)

`ScalePolicy.ScaleVelocity` multiplied velocity by the size factor while the animation INTERVAL was
only clamped and never scaled, so absolute speed fell with size. The screen does not shrink with the
pet, which makes that the wrong trade: a 25% companion had four times as far to travel in its own
body-lengths AND covered a quarter of the pixels per tick.

Measured on `shimeji-brq51bkr` at the 25% it was configured at, against a 1080px screen:

| animation | px/s before | crossing | px/s after | crossing |
|---|---|---|---|---|
| `fall` | 50 | 21.6s | 250 | 4.3s |
| `climb` / `descend` | 20 | 54.0s | 60 | 18.0s |
| `jump` | 58 | 18.6s | 233 | 4.6s |
| `walk` | 5 | 216.0s | 10 | 108.0s |

**The number that decided it is body-lengths per second, which was roughly PRESERVED** across the
old scaling (`fall` 1.16 → 0.93, `jump` 1.08 → 1.08). That is the proof the pet was never moving
wrong relative to itself; the world was four times larger relative to it. Somebody shrinks a desktop
pet to make it less obtrusive, not lethargic.

**Do not "fix" this per animation.** It was reached that way once, by shortening this pet's `walk`
interval from 320ms, and that was legitimate only because 320 was the corpus outlier against a
typical 240. `fall` is `10px / 40ms` on **all 52 pets that have one**, so there is nothing
pet-specific to correct, and patching it would have left `climb` at 54 seconds and `walk` at 216.

**ABOVE 1:1 the scaling STAYS, and removing it there is the thing not to re-propose.** It swaps one
defect for another: at 400% the pet is about 860px tall and an unscaled jump still rises the
emitter's fixed `JumpPeakPx = 48`, i.e. 6% of its own height, which reads as a twitch. Scaled, it
rises ~192px and keeps the 0.22 body-lengths the art was drawn for. Measured: every animation at 400%
is byte-identical before and after this change.

A POSITION still scales in both directions. `Animations.UpdateValues` uses `ScaleD` for `OffsetY`
and `ScaleVelocity` for the four X/Y velocities, and those must not be collapsed into one call.

The one-pixel floor `ScaleVelocity` used to carry is GONE rather than kept defensively. It existed
because a walk of 2 at 25% is 0.5 and banker's rounding takes that to exactly 0, freezing the pet in
place (reported on a 25% Luffy). Nothing is multiplied below 1:1 now, so that rounding is
unreachable, and a branch no input can reach is decoration rather than a guard. `--hardening-selftest`
asserts the freeze is unreachable instead, as a PAIR (`ScaleD(2, 0.25) == 0` and
`ScaleVelocity(2, 0.25) == 2`) so that neither reverting the policy nor restoring the floor passes.

Mutation-tested both directions, because a rule asserted one way accepts the other: scaling down
again exits 1, never scaling at all exits 1.

### ModuleKit carries helpers no shipped module uses, and they stay (2026-09-17)

`JsonSettingsStore<T>` (128 lines) has no module consumer at all -- its only reference in the repo
is the host's test project -- and `ModulePaths` has none either: only the template uses it, and the
template assigns `_paths` without reading it. Both are copied into all six published module zips.

They stay, for a reason that is not sentiment. ModuleKit is the module AUTHOR's surface, and an
out-of-tree author cannot be surveyed: "no in-tree consumer" is not "no consumer". Removing them
would also mark all six payloads stale for a saving of a few KB in a zip that already carries an
ONNX runtime in one case. The asymmetry recorded below is what makes this cheap to reconsider: if
they ever DO need to go, it costs a republish, so it goes in the same batch as something else.

What does not stay is a member that lies about what it does. `JsonSettingsStore.Path_` is unread by
anything and was left alone for the same reason as the rest.

### The unexercised speech and audio surface is for out-of-tree modules (2026-09-17)

`RegisterSpeechResponder`, `SpeechRequest` with its `ShowBubble`/`SuppressBubble`,
`ModulePermissions.Voice` (declared by nobody), `IHost.StopSound` (no caller anywhere) and
`IHost.Volume` (read by no module) are all host-implemented with zero in-tree consumers. Same for
`ICompanionManager.SpawnOne`/`RemoveOne`/`UninstallType`/`MaxCompanions`/`IsAtMax`,
`PokeInfo.PokeCount`, `ICompanion.IsBusy` and `CatalogKinds.Pet`.

Kept, and the case rests on being unexercised rather than on anything being wrong. The obvious
holder is the TTS module `handoff.md` predicts, and the audit that found this also found the defect
that WOULD have bitten it: the poke sass bypassed the responder chain entirely, so a voice module
would have gone silent on one line while a bubble appeared anyway. That is fixed and pinned by an
order invariant. The surface being quiet is not the same as the surface being broken, and one of
those two was true.

### Fortunes does not declare Network, and that is correct (2026-09-17)

Fortunes triggers real HTTPS catalog fetches and pack downloads (`FortunesModule.cs:736` and `:777`)
while its shipped consent line reads "Speech, ScreenContext, Storage". Raised by the ABI audit as a
possible under-declaration, ranked low by it, and settled here as NOT one.

The flag gates `IHost.OpenLink` and nothing else. The catalog verbs are the HOST's: the host owns the
fetch, the size bound, the hash verification and the write, and neither `PluginApi.cs` nor
`docs/module-authoring.md` asks for a flag to call them. If they did, every module with a Download
button would declare Network and the flag would stop distinguishing anything. The line to hold is
the one already written down: declare what YOU do, not what you ask the host to do on your behalf.
Contrast Remembrance, which genuinely declares it, because it reaches GitHub and a loopback Ollama
with its own client.

### Decisions from the 2026-09-29 audit campaign

The campaign that worked the 447 findings of the 2026-09-29 audit ran as parallel lanes, one per area, so each
lane records its decisions under its own sub-heading here rather than at the end of the section, where eight
lanes would collide on the same line. A sub-heading with nothing under it at the end of the campaign is removed.
The owner decisions taken before the campaign started are recorded first.

**Vision applies to every remark when it is enabled, unprompted drops included (decided by the owner
2026-09-29).** The audit (F066) found that the drop responder passes llowVision: true while the setting's label,
the settings comment and the brain's comment said vision was for explicit asks. The owner's decision: "it should
always use vision, because how else would it know what is on the screen to react to?" So the code stands and the
words change. The OCR path stays as the fallback for vision off and for text-only models.

**The 1,713 hand-authored (state, situation) pairs with zero eligible transition weight are accepted as they are.**
They are the original sheep authors' art (1,701 of them are the seven eSheep colour variants at 243 each); the
owner did not ask for them to be re-weighted when the question was put on 2026-09-29.

**ModulePermissions.Animation stays declarative.** It is displayed on the consent screen and gates nothing, like
the other disclosure flags; the register entry its CLOSES-WHEN line asked for is this one: the Animation permission
is declarative.

#### fix/gates

**F151 is deferred to F150, deliberately (2026-09-29).** `AnimCapabilitySelfCheck.AgreesWithTheFixture` asserts only
the census's shape, and the named assertions that would make it bite (`top_walk2` is a CLIMB, `vertical_walk_down`
is a CLIMB, `boing` is a JUMP) FAIL on the classifier as it stands: the finding's own verifier measured them. They
must land together with the F150 growth-loop fix in lane fix/petstudio, not before it, or the gate goes red on a
build nobody changed. The one line that is an identity of Census over ClassifyAll ("the census covers every
animation exactly once") stays until then, so the file is touched once, by the lane that owns the classifier.
Nothing else about F151 is disputed.

**The prompt-options differential stays table-only; the Codex keyboard-hint assertions are not folded into it
(2026-09-29).** F391's second part asked for `_KINDOF` in tests/difftest-prompt-options.py to also parse the
`PromptOptions.Classify("Allow once ⏎", out matched) == OptionKind.ApproveOnce` shape, which would make the
harness red at once: the Python reference has no port of `StripKeyboardHint` and classifies "Allow once ⏎" as
unknown. Porting a Codex-only normaliser into the reference widens what the reference IS, and that is the
AgentFlow lane's call. What this lane did instead: every string the harness listed without an expectation is
now asserted in `SelfCheckPromptOptions`, the harness REFUSES if one of them is not, its count line reports
compared-vs-listed, and its docstring no longer claims to parse `Choose(...)`. The Codex hint shape is exercised
by `SelfCheckCodexOptions` through the module's own self-test, in the gate, which is where its expectation lives.

**F387 and F388 were fixed in the window soak's source and BUILT, not RUN (2026-09-29).** The coordinator owns
`tests/module-window-soak.ps1`. The mutation that proves F387 is a run with `--pet` pointing at a file the validator
rejects: it must exit 1 on cycle 1 naming "Analyze() produced no animation nodes"; the default run must still
print RESULT=PASS with a new "GUI resource counters readable" PASS line per segment.

#### fix/host

(none yet)

#### fix/tools

(none yet)

#### fix/remembrance

(none yet)

#### fix/blinkingled

(none yet)

#### fix/aibrain

(none yet)

#### fix/fortunes

(none yet)

#### fix/petstudio

(none yet)

#### fix/reminder

(none yet)

#### fix/agentflow

**The prompt-options differential is not widened to the Codex keyboard-hint shape, and the Python reference
grows no StripKeyboardHint port (2026-09-29).** The gates lane left this call here (see `#### fix/gates`). Declined
on the merits, not only on boundary: `docs/agentflow/agentflow_classifier.py` is the transcription of the Claude
Code bundle plus an `--audit` that re-derives the option set from whatever Claude Code is installed. The Codex
rows in the C# table ("Allow once", "Allow similar commands", "Deny") were read off a LIVE Codex prompt over CDP,
and the keyboard hint Codex renders inside the label ("Allow once ⏎", "Deny Esc") is a property of Codex's
webview that no bundle on this machine can audit. A Python copy of the stripper would be a third normaliser with
no audit to keep it honest, and the reference would then claim to describe a bundle it never reads. The Codex hint
shape is pinned where its evidence lives: `SelfCheckCodexOptions` asserts the button spelling, the menu spelling
and the stripper's edge cases through `--module-selftest=agentflow`, in the gate. The harness stays table-only and
its docstring says so.

**F030's argv.json read stays per tick (2026-09-29).** The rules half is fixed through the RuleCache (F055). Inspect
still reads argv.json on every tick beside the port probe. The probe is the cost of that call and is already a
recorded decision (`docs/BACKLOG-CLOSED.md`, "AgentFlow tick pays up to 250 ms on the worker when the VS Code port
is closed, once per 10 s; backoff rejected"); the file read next to it is one small read per tick on the worker.
Caching it by stat would save a read while the probe it accompanies stays, and it was not measured cold, so no
saving is claimed and no code is added for it.

#### fix/deadcode

(none yet)

#### fix/scripts

(none yet)
## Known ABI gaps

Add the verb when the module that needs it is written — see `handoff.md`'s host contract. Neither of
these is scheduled, and neither is an oversight.

- 📌 **A module cannot draw on or near the companion.** No ABI for overlay/decoration. Nothing
  planned needs it yet; noted so it is not mistaken for an oversight if something does.

- ⬜ **A module cannot push a live value into an open options pane or tray menu.** Found while
  porting the standalone app's "Next blink" countdown, which refreshed every 250ms because that app
  owned its own menu. A module ships DATA and the host renders it, so the best available is a
  snapshot: `TrayItem.DynamicText` is re-evaluated when the menu opens, and `SettingKind.Info` is
  read when the pane loads or a `PaneAction` with `ReloadPaneAfter` runs. Good enough for state that
  changes slowly, useless for a countdown. The readouts were dropped rather than shipped stale. If a
  live readout is ever wanted this needs an ABI addition (a push channel or a pane-refresh tick) and
  therefore a host release, which was not worth it for one diagnostic.

**Before writing "needs a host change" about anything else, grep `PluginApi.cs` for the verb.** That
sentence cost a planning cycle: a Reminder entry asserted the ABI could not drive a companion
animation or move a companion, while `IHost.TryPlayAnimation` and `IHost.PlayAnimationAll` had
existed since the emotion work and AiBrain had been using them all along. The full case is in
[`HISTORY-post-1.0.0.md`](HISTORY-post-1.0.0.md). Genuinely-missing ABI that somebody is waiting on
stays in [`../BACKLOG.md`](../BACKLOG.md) — moving a companion, and the pane's unapplied-value
preview, are both there.

---

## Decisions that read as defects

Each of these was filed as a bug, or would have been. None is one, and one of them records a fix
that had to be reverted.

- **A composited tile is shorter than its source sprite ON PURPOSE, and the missing rows are the
  ones below the ground line.** Filed 2026-09-25 while publishing Zim: 55 source sprites at 130x130,
  an emitted tile of 162x128, and `shime41.png` losing 43 of 6669 non-transparent pixels (0.64%) off
  the bottom of the boots. It reads exactly like an off-by-2 crop.

  **Measured 2026-09-28, and it is not one.** A synthetic frame settles it without needing any
  copyrighted source: one 130x130 sprite, `ImageAnchor` at y=128, with an opaque block spanning rows
  120-129 so that 8 rows sit ABOVE the anchor and 2 BELOW. Composited result:

      srcAbove=560  srcBelow=140  cellOpaque=560  cellH=128  scale=1

  The cell keeps exactly the above-anchor pixels and drops exactly the below-anchor ones. Nothing
  above the anchor is lost, and scale is 1.0 so no resampling is involved either.

  That is `SpriteSheetBuilder.cs:186` doing what `:169-176` says it does: `cellH = max(AnchorY)`,
  with the anchor placed on the cell's BOTTOM edge because the Shimeji `ImageAnchor` is the mascot's
  ground-contact point and the host stands a pet by putting its window's bottom edge on the floor.
  Reserving a band below the anchor is what the code used to do, and it lifted every pet off the
  ground by that much -- Hornet's standing frame sat 14px clear of the taskbar while a hand-authored
  sheep stood on it correctly. Anything drawn below a frame's own anchor is below the floor line.

  **So the backlog entry's closing condition was unsatisfiable by construction.** It asked for "a
  converted pet's per-frame alpha-pixel count matches its source within 0", which can only be met by
  reversing the floor-contact fix. Do not re-file it, and do not re-convert the 32 shipped
  `shimeji-*` companions to chase it. If a future pet genuinely loses pixels ABOVE its anchor, that
  IS a bug -- and the measurement above is the way to tell, in about a minute.

  ⚠ **"Do not re-convert" is scoped to THIS entry, and it was mis-cited once.** It says do not
  re-convert to chase a non-defect. It is not a general rule against fixing shipped converted pets:
  on 2026-09-28 I quoted it as one to justify leaving a real, already-fixed defect in 14 of them, and
  the correct answer was a FORMAT-LADDER RUNG, which upgrades shipped pets in place and needs no
  source skins. The skins are not in this repo anyway, so "they will be re-converted eventually" is
  not a disposition available to anybody. When a converted pet ships with something fixable, the
  question is which rung fixes it, not whether to re-convert.

- **A blank frame in a converted companion is legitimate, so "no blank tiles" cannot be a
  corpus-wide gate.** A sweep of all 50 companions found intentional transparent frames in
  hand-authored ones: `ssj-goku`'s `Instant_Transmission`, `alipheese`'s
  `TeleportStart`/`TeleportEnd`, the seven sheep's `bathd`, `negima`'s `fall`, `pingus`'s `fall2c`.
  They are how a companion goes invisible. The blank-tile assertion therefore lives on the
  SYNTHETIC fixture only. If a corpus-wide check is ever wanted it needs an allowlist keyed by
  animation name, and the allowlist is the whole cost.

- **A one-frame animation with `repeat="0"` is effectively invisible — and the fix once proposed for
  it is ruled out by its own prerequisite measurement.** Hornet's `Grapple3` was the report: a single
  frame with no repeat renders for ONE tick (`TotalSteps` is 1, so `AnimationStep >= lastStep` fires
  on the first one) and cannot be seen. It is reachable and it "plays"; it just never appears, which
  is indistinguishable from a bug to a user and invisible to the reachability check that guards the
  corpus.
  **Measured 2026-09-17, which is what the entry asked for before choosing:** across all 31 shipping
  converted skins, **54 non-magic single-frame sequences sit under 500ms of on-screen time, across 26
  companions**. Method, so it is reproducible: for every `<animation>` with exactly one `<frame>` in
  its `<sequence>`, take `(1 + repeat) * <start><interval>`, and exclude the four magic names
  (`kill`/`sync`/`fall`/`drag`) — `sync` is legitimately a 100ms no-op and accounts for 25 more on
  its own.
  **That count REFUTES the proposed fix.** 25 of the 54 are the emitter's SYNTHETIC `turn` at 120ms,
  whose whole job is to be instantaneous, and most of the remainder are `*Blink`, `*Transit` and
  `*End` poses that are meant to be brief. A blanket minimum dwell would put a visible stall into
  every converted companion's facing change; refusing to emit would break facing outright.
  `Grapple3` does not appear in the measurement at all, so the original example has already been
  re-emitted away.
  **The only question the measurement leaves open** is whether any pose ROLE other than rest wants a
  dwell — the emitter already gives rest poses one, and nothing in the measurement says another role
  does. **Do not spend on this without an observed case that is not a `turn` or a blink.**

- **A converted companion's ceiling art can read as "standing sideways in mid-air", and it is not a
  bug.** Hornet's skin draws its ceiling cling as a body lying flat against the ceiling (rotated 90
  degrees, top-anchored) rather than upside down. The original Shimeji shows the same thing; it only
  became visible once a climb could actually reach a ceiling. An attempt to "fix" it by swapping the
  wall and ceiling frame sets was WRONG and was reverted — the anchoring proves the mapping: ceiling
  art is composited flush to the cell TOP (it hangs), wall/floor art flush to the BOTTOM (it stands),
  so moving indices between regions moves art into a cell position it was never aligned for, and the
  companion floats 60px above its own feet.
  **The lesson, worth keeping:** a sprite's ROTATION says which surface it was drawn for, and its
  ANCHOR says the same thing independently. Consulting only one made it possible to be confidently
  wrong. Options if the look is ever judged unacceptable: rotate ceiling art in the compositor, or
  drop the ceiling region for skins whose ceiling art reads badly. **Not a defect to fix by moving
  pixels between regions.**

---

## Known bugs (post-1.0.0)

**Four open: BUG-009 to BUG-012, filed 2026-09-29 by the full audit, each closing with its lane's fix in the campaign recorded in `../BACKLOG.md`.** The full post-mortems —
diagnosis, the wrong turns, the fix, and how each was verified — are in
[`ISSUES-post-1.0.0.md`](ISSUES-post-1.0.0.md). Bugs are numbered `BUG-00N` and the number is never
reused, so a commit, a test or a code comment can cite one; `modules/AiBrain/`,
`modules/PetStudio/`, `src/dotNet/`, `docs/RELEASE-CHECKLIST.md` and `handoff.md` all cite them
today. **The next one filed is BUG-013**, and it is filed in [`../BACKLOG.md`](../BACKLOG.md).

| bug | | fixed |
|---|---|---|
| BUG-006 | auto-approve could not see most Codex prompts: the reader anchored on a control Codex only sometimes renders | agentflow 1.4.2, 2026-09-23; verified by pressing the still-open prompt that found it. Its second defect — a missed selector being silent — fixed in 1.4.3 |
| BUG-007 | nine of the fourteen prompt shapes logged as "an unrecognised prompt", including the commonest one | agentflow 1.4.3, 2026-09-23; table re-derived from bundle 2.1.280 |
| BUG-008 | the option table went stale against bundle 2.1.280 and the audit was blind to it, so a refusal blamed the screen capture instead of naming the real reason | agentflow 1.4.4, 2026-09-23 — the only one here found by a gate rather than by the maintainer |
| BUG-001 | the tray icon is missing after an MSI install that launches the app | host 1.1.1 → 1.1.3, across all four of its symptoms |
| BUG-005 | a converted companion stutters: a performance replayed itself, or held two frames for ~11s | 2026-09-22, both halves; one root cause with three sites, one of them a test |
| BUG-002 | the vision feature does nothing, silently, when the configured model is not installed | 2026-09-10, all four items |
| BUG-003 | screen capture returns the wallpaper, and follows the wrong monitor | (b) fixed 2026-09-10; (a) resolved — the suspected cause was refuted by measurement |
| BUG-004 | the leak soak's verdict was a coin flip, and it is the only gate that can catch a leak | 2026-09-10 — there is no leak, and the gate now measures that |

Three of the four were found by the maintainer running a real install, and none by a gate. That is
why [`../SMOKETEST.md`](../SMOKETEST.md) exists, and why the A-E walk is tracked as open work in
[`../BACKLOG.md`](../BACKLOG.md) rather than dropped.

---

## Measurements that corrected an explanation

Two facts about the C# compiler in this repo, measured 2026-09-17 because an audit item was about to
be closed on the wrong reason. Both correct explanations that had already been written down, one of
them in a commit message.

- **CS0414 does not fire for a write-only field assigned from a non-constant, anywhere in this repo
  -- `src/` included, where warnings are already errors.** `private int _probeNeverRead = 1;` (a
  constant initializer, never read) **does** warn in a module build. `private int _blockedCount;`
  assigned only as `_blockedCount = blocked;` from a local, and never read, **does not**, in a full
  `--no-incremental` rebuild. So the earlier claim that modules escaped this class because
  `modules/Directory.Build.props` omitted `TreatWarningsAsErrors` was wrong, and so was the claim
  that an incremental build hid it. The compiler simply does not report that shape. Both warning
  properties were added to `modules/Directory.Build.props` anyway on 2026-09-17 (a forced full
  recompile of all eight module projects emitted 0 warnings, so it was free), and that file's own
  comment carries this correction beside them. **The false comfort to avoid: do not treat a clean
  warning build as evidence that no field is write-only.**
- **A module build reporting `0 Warning(s)` right after a code change is almost always an
  up-to-date incremental build that never ran the compiler.** An injected
  `private int _probeNeverRead = 1;` in `AiSettings` produced `warning CS0414 ... 1 Warning(s)` from
  a plain `dotnet build` and `error CS0414 ... 1 Error(s)` with `-warnaserror`, so a module build
  genuinely does report the class. Pass `--no-incremental` before believing a warning count, in
  this repo or any other. Same defect shape as a log line that cannot fail.

---

## Numbers in documentation

**A number nobody re-measures goes stale, so make the code count it.**
`tests/DesktopAICompanion.CoreTests/Program.cs:87` is the in-repo model: it **counts** its groups
rather than hardcoding them, and the comment beside it records the drift that motivated it — *"the
literal in the summary line had drifted five groups behind reality, so it reported 26 while 31 groups
ran."*

Where a document cannot count, it should describe what it counts rather than assert how many. The
`SMOKETEST.md` invariant figure was wrong **three separate times** (61, then 82, then 84) before the
sentence was rewritten to stop carrying a literal at all. The trap that produced the last two: a bare
`grep -c 'Assert-True'` over `tests/runtime-hardening-selftest.ps1` counts the function definition
and a comment mentioning the helper, and ten of the real call sites sit inside `foreach` loops, so
the static site count and the runtime PASS count are different numbers and neither is the raw grep.
