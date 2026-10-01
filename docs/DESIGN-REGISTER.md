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
| cite a bug number, or file a new one | [Known bugs (post-1.0.0)](#known-bugs-post-100), which names the next number to use (the gate holds it to the post-mortems) |
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

**A borderless window that spans the whole virtual screen IS fullscreen on every monitor it covers, whatever
program owns it (2026-09-29, N-host-01).** MatrixDesktop's "Matrix Digital Rain", a WinForms form sized to
`SystemInformation.VirtualScreen`, launched at 15 minutes of idle and closed by the first keystroke, hid every
companion, and the report read it as a wallpaper behind the desktop icons. It is not: its source has no
SetParent, WorkerW or HWND_BOTTOM, so it sits in front of the desktop the way a screensaver does, and a TopMost
companion would draw over it. `FullscreenScan` decides by geometry (`DesktopGeometry.IsFullscreenOnMonitor`),
not by what a window is for, and that stays: an idle animation shaped like a screensaver is exactly the kind of
window the companions must not sit on. They return within one 300 ms scan cycle of it closing.

**A companion stood down for a fullscreen window defers its latest line and says it when its monitor clears
(2026-09-29, N-host-02).** Dropping the line loses a reminder that fired mid-game; queueing replays several
stale announcements in a row when the game ends. One line, the most recent, replaces any earlier one.

**F265's per-tick detector walk stays declined (0.60 ms per walk, measured; BACKLOG record). F267's per-child
spawn scan stays as the recorded un-cached spawn check: shipped content spawns at most two children per
animation, so at most three walks per spawn event, never per tick.**

**A redirect into an open settings window asks before it discards edits (2026-09-29, F368).** A nav click is
the user leaving a pane, and it discards, as it always has. A module-update balloon or the restart reopen
timer is not the user leaving: `OptionsWindow.ShowPane(title)` refuses while Apply is lit, and
`OptionsShell.RedirectOpenWindow` puts the question ("Discard them and open <pane>?"); only Yes discards.

**A pane reload that arrives late is declined, not applied (2026-09-29, F375).** The window answers a
`ReloadPaneAfter` only for the pane on screen and only while it is open; a continuation from a pane the user
left drops its stash and its message rather than rebuilding under a nav that lights something else. The
alternative (driving the rebuild through the nav so the two cannot disagree) would move the user back to a
pane they had left, which is the same surprise from the other side.

**Reset to defaults touches only what the page shows (2026-09-29, F371).** `themeMode` has been dormant since
the Theme dropdown went (2026-08-07, "the window follows the OS"); the reset no longer writes it, and no
Theme control returns as a side effect of this item. A hand edit of settings.json is the only way to hold a
non-default value, and a button that promises to restore "the settings shown here" leaves it alone.

**A new module installs through the same staging folder an update uses (2026-09-29, F367).** One same-volume
`Directory.Move` makes `modules/<id>` whole-or-absent; what an interrupted or killed unpack can strand is a
staging folder beside `modules/`, which the loader never scans, not a half module the pane lists as installed.

**Reveal containment compares resolved against resolved (2026-09-29, F376).** The permitted root goes through
the same `GetFinalPathNameByHandle` as the file, so a data root behind a junction, SUBST, mapped drive or
UNC path allows its own files and a junction inside the root still cannot lead out. A root that cannot be
resolved refuses, on the same fail-closed grounds as the file.

**The loader is fail-closed on a partial type load (2026-09-29, F342).** `ModuleHost.LoadFrom` calls
`Assembly.GetTypes()` and refuses the module whole when any type in it fails to load, rather than searching
the partial list for the `IModule` type and letting the broken type throw when first touched. Safer while
every module is first-party; a module that degrades on load is a decision for the day a third-party module
needs it, and the convention runner no longer carries a partial-load catch that implied the other policy.

**A module whose Init throws holds nothing in the host (2026-09-29, F344).** The isolation promise at the
top of `ModuleHost` ("one bad module can never take the host down") covered the host and not the module's
own leftovers: a tray item, a pane, a responder or a subscription registered before the throw stayed live
behind a "failed to load" row. `CompanionHost` records what an Init registers and rolls it back when Init
does not return. The six module-facing events became explicit accessors over backing fields for this; the
ABI did not move.

**A removal that cannot finish stays marked and is retried (2026-09-29, F352).** A locked module folder
used to cost the user their uninstall: the marker went whatever happened. Now the id stays, the launch that
hit the lock does not load the folder either, and the next launch tries again; a reinstall or update of
the same id forgets the pending removal so the two cannot fight over the folder. What no design fixes: a
sibling instance still running the module loses its assets under it whichever way the delete happens.

**`IHost.GetStorage` and `GetSettings` never return null from the shipped host (2026-09-29, F341).** The
contract says so now, and says that test doubles may; the host-side convention host keeps returning null
on purpose, as the one gate exercise of every module's null tolerance.

**`IHost.GetSettings` stays a fresh parse per call (2026-09-29, F330).** Memoising would share one mutable
dictionary across the independent handles the contract documents, some read off the UI thread. The cost is
user-paced and unmeasured; the modules that fetch repeatedly can hold one handle if a measurement ever asks.

**The self-test scratch sweep may take another program's `dp-*` directory (2026-09-29, F356).** Accepted,
and now said at the site: the sweep runs only under a self-test flag on developer and CI boxes, and a
longer prefix would re-couple cleanup to a naming convention across some twenty creators, which is the
orphan failure the sweep fixed.

**Settings writes coalesce per user action, and the setters' results stay durable (2026-09-29, F361, F308).**
`LocalData.BeginBatch` lets one Apply or one Reset be one write instead of twenty-two; a setter outside a
batch still writes at once, and a setter's `true` still means "on disk". A debounced background writer was
declined because those bools are shown to the user as saved-or-not. What this does NOT fix is the per-write
COST: settings.json embeds the active pet's XML (1.17 MB for the default pet, ~12 MB at the validator's
cap), so every write is a full-document rewrite. Moving the payload to a sibling file keyed by id and hash
is a schema change with a LoadCore migration and belongs to a host release of its own; the on-disk format
did not move in this lane, and an installed 1.2.6 reads what 1.2.7 writes.

**Four unmeasured performance items stay as they are (2026-09-29, F262, F269, F276, F297).** Each finding's
own first step was a cold measurement in fresh interleaved processes against a variant with the change
removed, and this lane made none; a saving that is not measured that way has been wrong every time it was
believed in this repo. The alpha-pet push-skip, the CheckTopWindow throttle, the bubble DPI cache and the
pooled HttpClient are recorded here so the next reader starts from the measurement, not the idea.

**The loader has a graph-only overload, and the validator hands its decoded bytes on (2026-09-30, F318,
F317, F155).** `Xml.TryReadXml(string xmlText, bool stageImages, out string error)` is additive: with
`stageImages` false the definition is validated and adopted (AnimationXML, icon, frame size from the PNG
IHDR, scale factor) and no bitmap is decoded, SpriteCount is 0, and the instance must never be handed to a
running companion. `CompanionXmlValidator.TryParse(xml, out root, out spriteBytes, out iconBytes, out
error)` is the other additive shape: the loader reads the bytes the validator decoded and proved, so the
second base64 pass is gone rather than made faster. Both files are source-linked by PetStudio, which is
why the existing signatures did not move. The alpha flag moved into the commit block; the only observable
was a violated contract, and the contract is what the invariant asserts.

**The XSD schema set stays uncached (2026-09-30, F254, DECLINED-MEASURED).** 0.95 ms per TryParse, warm,
30 reps. TryParse runs per install, download, drop or Studio analyze; a cached `XmlSchemaSet` would save
under a millisecond on a user-paced path and add a shared mutable object to a stateless class.

**The chooser evaluates nothing; a counter says so (2026-09-30, F241, F242).** `TAnimation.EvaluationCount`
is a counter seam like `AppSettingsStore.DurableWrites`: the self-test asserts a chooser call adds nothing
and the consumer's `UpdateValues(DisplayIndex)` adds one, stated as a count and never as a timing. The
store-back into `SheepAnimations` went with the evaluation because `TAnimation` is a struct and nothing read
an evaluated value out of the dictionary, only names and edge lists.

**The drop read is sized from the file, with the sentinel kept (2026-09-30, F271).** The buffer is the
stream's length plus one byte, clamped to one over the limit, floor 4 KB, and grows if the file grows while
it is read; the over-limit test on the bytes read is unchanged. The probe COUNTS allocation through
`GC.GetAllocatedBytesForCurrentThread` rather than timing anything: a 2 KB drop under 1 MiB where the old
buffer was 12 MiB.

**`JsonSettingsStore.Update` holds its lease across the mutation (2026-09-30, F230, F231).** One lease
for read, mutate and write closes the two-lease window; the mutation therefore runs under a cross-session
lock and must stay short, which the doc comment says. An `Unreadable` document is refused rather than
written over, and `LastLoadWasUnreadable` lets a module say so; the backup is `<path>.bak`, as in the two
stores the class was distilled from. No in-tree consumer yet, so the CoreTests group is the only caller.

**The companion form's Icon is removed, not disposed (2026-09-30, F272).** Nothing reads the property
(border None, ShowIcon false, tool window, no reader in `src/`), so not creating the HICON beats creating
and disposing one per spawn. The `ComponentResourceManager` line went with it: the icon was its only use.

**The debug window hands text over by file, not by automating Notepad (2026-09-30, F274).** On Windows 11
notepad.exe is a launcher stub, so the WM_SETTEXT handoff wrote into the wrong window or none and swallowed
the failure. One fixed `dp-debug-<kind>.txt` per kind under %TEMP%, overwritten, opened through the shell;
this is the user's default editor rather than Notepad by name, and a failure is an error row in the window.

**The module template holds one settings handle (2026-09-30, F330 follow-up).** F330 stays
ACCEPTED-RECORDED for the host, but the template is this lane's own, and it is the sample every new module
is copied from, so it now memoises the handle its own comment tells authors to hold.

**The corpus classifier decides its layout once per file (2026-09-30, F321, F323).** A per-row field count
turned a tab inside a text into a reinterpreted, truncated row; now row 1 sets the layout and a later row
that differs is an error naming the line. Both transforms carry a `--selfcheck` that `label-selftest.sh`
runs; the gate does not run that script, so the selfchecks' hand mutations are recorded in the commit and
the source-text invariants are what the gate sees.

**The fullscreen scan's z-order rule stands, and MatrixDesktop is its worked example (2026-09-30,
N-host-04).** The coordinator read a visible Sheep over the rain window on the campaign build, and no Sheep
on the pre-campaign build, as a stand-down the campaign lost. Three measurements said otherwise: the scan
sources are byte-identical between the two builds; the installed 1.2.6 showed three visible, TopMost Sheep
over the same window at the same moment; and a read-only replay of the scan's own walk named the cause,
normal windows above the rain window holding both monitor centres, so both monitors were decided clear
before the enumeration reached it. That is the rule FullscreenScan's summary has stated since the scan was
introduced: the topmost real window at a monitor's centre decides it, so a fullscreen app under the active
window does not count and a normal window over a game does not hide it. "Fullscreen-sized anywhere in the
z-order blocks" was declined: an idle animation sized to the virtual screen sits under the user's working
windows for hours (this one from 01:42), and that rule would hide every companion for the whole of it. The
pre-campaign instance's "no Sheep window at all" is not a stand-down either -- a stood-down companion keeps
its window, hidden -- but an instance with no companions, which its 0-pet mix of 2026-09-29 already said.
The decision half of BlockedMonitors is now `FullscreenScan.MonitorDecider`, fed one window at a time by the
enumeration, so the rule is pinned in `--fullscreen-selftest` against described windows rather than against
whatever the desktop holds when the gate runs. N-host-01's sentence gained the z-order qualifier it lacked.

#### fix/tools

**No format-ladder rung for F425, F428, F429 or F431 (2026-09-29).** Each changes emitter output only on inputs
no shipped converted pet has. Every one of the 32 carries a Walk, so the wall region was always reachable (F429);
the synthesised-climb path needs a wall skin with no real climb, which only KinitoPET once was and no longer is
(F425); and names counted over `Companions/` on 2026-09-29 found 0 multi-frame `faceCursor` animations (F428)
and 0 `<seq>_<n>_<member>` chain steps (F431). The last two could not have been repaired by a rung anyway: a
gaze's catch-all timing and a chain's provenance are not recoverable from emitted XML without the source skin,
so for those the only repair would be re-conversion, and nothing shipped needs it.

**The audio budget lives with the embeddings, not in SoundBaker (2026-09-29, F435 and F426).** SoundBaker's caps bound
how much DISTINCT audio one conversion transcodes; the room a clip has is decided in the emitter's sound loop, per
embedding, against the sheet the compositor produced and the validator's audio caps. Two alternatives were
rejected: a retry-without-sounds when the validator refuses the size, because it hides the cause from the residue;
and deduplicating embeddings by source action, because the format ties a sound to an animation, so a sounded
set-piece member is meant to sound on every chain step that plays it.

**F439 is fixed without a self-test (2026-09-29).** The timeout branch of the ffmpeg transcode is reached only by a
child that hangs for 30 s. The gate cannot pay that, and no argument-shaped stand-in makes a real process hang while
holding the output file open, so the fix is the documented Kill -> WaitForExit sequence and the audit record's own
measurement (plain Kill: 20/20 partial files left behind; a wait after the kill: 0/20) is the evidence for it.

**No rung for F444, F459 or F460 either (2026-09-29).** F444's five disagreeing bundles are unshipped and a rung cannot
move pixels; the shipped converted sheets never engaged the sheet clamp (the largest is 14x14 tiles at 3584 px, read
from the PNG headers of all 32 by the audit), so F459 changes no shipped sheet; F460 changes how a sprite is decoded,
not what is emitted. F459's rounding half keeps `Math.Round` for every sheet inside the cap and re-fits only a sheet
that would land past it: switching to Floor would shave a pixel off the six shipped fractional-scale pets on
re-conversion, download churn for no defect.

**F460's saving is a property, not a number (2026-09-29).** For a WebP sprite the PNG deflate inside dwebp and the WIC
PNG decode on this side no longer run. The audit measured 7-10 ms per cartoon sprite and about 35 ms per noisy one on
the dwebp half alone (fresh processes, 30 runs in rotation); that figure is the record's, not re-measured cold here,
so nothing is claimed for the converter as a whole.

**RoundTrips on emitted output is a fixed-point check, not a second validation (2026-09-29, F427).** The other option,
`RoundTrips = Valid`, was rejected: the CLI's convert verbs print the field and `Accepted` reads it, and a value that
cannot differ from `Valid` claims a check that did not happen. Determinism can fail on its own (a DTO member that
does not round-trip; an element the text omits that the DTO reads back as its default and writes out) and the
mutation that stripped `<offsety>0</offsety>` from the text showed the check naming the offset. `ShimejiEngine.RoundTrips`
(serialize, then validate again) keeps its meaning for hand-authored pets in the `verify` verb.

**PetGraph mirrors the host's magic-name resolution rather than sharing it (2026-09-29, F440).** The runtime's copy
lives on a staged `Animations` the graph pass does not build, so the rules are restated in `PetGraph` and
`PetGraphSelfTest` pins each one. Scope: the CLI's verify and migration verbs and the emitter's own acceptance
verdict. PetStudio's map runs `AnimationReachability` over the real loader and never had the defect.

**F451 declined on a measurement (2026-09-29).** `EmitterSelfTest.TileIsPainted` decodes the fixture sheet once per
frame reference. The whole `selftest` verb, nine sub-tests after this campaign's fixtures, takes 1.45 s wall-clock on
this box (Measure-Command around the Release exe, one fresh process; 1.49 s before the campaign), so there is nothing
to recover. Not a cold interleaved comparison, and none is claimed: the number is the cost of the whole verb, stated
as the reason not to spend on it.

#### fix/remembrance

**BUG-009 is fixed at the construction, and the wait stays (2026-09-29).** `AudioRecorder.OpenCapture` builds
every capture with no SynchronizationContext current, so NAudio raises RecordingStopped on its capture thread
instead of posting it to the UI thread that is blocked waiting for it. The alternative in the finding, dropping the
wait and trusting `Capture.Dispose()` to join the capture thread, was rejected: `WasapiCapture` nulls its thread
field BEFORE it raises the event, so a Dispose landing in that window skips the Join and the handler would still be
disposing the writer while DisposeSource did the same. The 10 s bound stays as a ceiling for a wedged capture
thread. Measured on this box's real render endpoint with a `pwsh -STA` probe of the shipped shape: context
captured, the wait ran to 10 008 ms and the handler never ran; context nulled, the handler ran on the capture
thread 33 ms after StopRecording while the constructing thread sat blocked with no message loop.

**F169 is fixed with a silent render stream, not by clock-padding (2026-09-29).** `RenderKeepAlive` plays a
`SilenceProvider` through `WasapiOut` on the loopback endpoint for the length of the capture, the workaround
NAudio's own WasapiLoopbackCapture doc names. Padding gaps from a Stopwatch in DataAvailable was rejected: it
has to reconcile the device clock with the wall clock over an hour, pick a threshold that drift never crosses but
a real gap always does, and pad the tail at Stop as well; the audio engine already does exactly the mixing that is
wanted. Best-effort: an endpoint that refuses a second stream records as every earlier version did and the refusal
is logged. The property is asserted through the recorder's seams; the live alignment check needs the console
session `BLOCKED.md` T25 already needs.

**F173 gets a coverage marker, not a hierarchical reduce (2026-09-29).** The early break at MaxReduceCharacters
stays, because a truncated-but-real set of notes beats abandoning the summary; what changed is that the result
says which parts it covers, appended to the file and returned as the message the caller logs. A second-level
merge would add one or two model calls per long meeting and lower the resolution of every part to represent all
of them. The finding's complaint was the silence, and that is what was fixed; the merge stays available as an
option if a user wants every part represented.

**F176's shutdown wait is bounded at two minutes (2026-09-29).** The in-flight save (1.0.11) has no bound at all;
this one waits for a save a NORMAL stop left running, which is the capture stop plus the mix, tens of seconds for
a 45-minute two-source capture. Two minutes covers a two-hour meeting with margin; past it the process is leaving
anyway and the log says recording.wav may be incomplete. Transcription is deliberately not waited for.

**F178 is a deletion, not background work (2026-09-29).** The lane rules ask background items to copy the
single-flight pattern; this one needed none, because the two Init-time WASAPI walks produced Options arrays the
host never shows (Load rebuilds them before Schema is read on every pane build). Removing the work is the root
fix. The pane-open enumeration stays synchronous on the UI thread: a dropdown built from a stale list is worse than
the milliseconds one walk costs, and 1.0.13 already collapsed four walks per open into one.

**F180: the first purge moves to the purge timer's first tick, 60 s after Init (2026-09-29).** A headless
self-test process has no message loop and never reaches it; the app does. Against a 72-hour retention the minute
is immaterial, and it takes the purge out of every test that loads the module without seeding a storage root.

**F182's factor is 4x the audio length, floor 30 minutes, ceiling 6 hours (2026-09-29).** Sized from one number
measured here, not the slowest supported machine: base.en on a Ryzen 9 3900X, 967.6 s of speech in 110.2 s (8.8x
real time). small.en is roughly three times the compute and a four-core laptop roughly a third of the throughput,
so 4x covers the slowest pairing the dropdown offers with margin. The stop path now logs audio length beside wall
time for every real transcription, so the factor can be re-read off real runs.

#### fix/blinkingled

**Start() continues the cadence from a key it lit rather than clearing it, and does not adopt a key it never
lit (2026-09-29, F115).** Three shapes were on the table for reconciling `_phaseOn` on enable. (A) Mirror
`Stop()` and clear the key first: one extra SendInput on enable, which can itself be refused and would then
leave flag and key disagreeing again, the drift this release removes. (B) `_phaseOn = _phaseOn && reader()`
and arm the first interval from the phase: no keypress, and the blink the user just made becomes the first lit
phase of the cadence. Chosen. (C) `_phaseOn = reader()`, adopting whatever the key reads: also fixes a Scroll
Lock the USER had lit before enabling (N-blinkingled-01), but it lets `Stop()` clear a key the module did not
light, which the `Stop()` comment forbids. That is the owner's rule to change, not a lane's, so (C) is filed
rather than taken.

**Variant (C) was chosen on 2026-09-30 by the coordinator (N-blinkingled-01, implemented by lane fix/followups).**
`Start()` adopts the key's state (`_phaseOn = reader()`), so a Scroll Lock the user had lit before enabling
runs the cadence from its lit phase rather than inverted, and `Stop()` clears it: the Readme's "stopping
always leaves the light off" (Readme.md, Blinking LED) now holds for a key the module did not light too. The
reason: a user who switches the blinker on has asked for the light to be driven, and "off when it stops" is
the promise the Readme makes; (B) kept that promise only for a key the module lit. A key the user lit is still
left alone while the blinker was never started (startup with the feature off), which is the `Stop()` rule's
remaining scope.

**Stop() keeps the belief when its corrective toggle is refused (2026-09-29, F116).** `_phaseOn` is the object's
claim that it is holding the key lit; after a refused clearing press that claim is still true, so it stands and
the next `Stop()` or `Start()` retries. Zeroing it regardless was the third write of the flag that ignored the
hardware, and it re-created the F115 inversion through the next `Start()`.

**The module self-test makes exactly two real keypresses and asserts their parity (2026-09-29, F113).** Every
other probe delivers through the engine's `KeypressSender` seam and never reaches Windows. A hardware
read-and-restore at the end of the suite was considered and rejected: it depends on the same `GetKeyState` read
whose staleness in a background process is the open question, and a wrong read would ADD an unpaired press.
Pairing by construction plus an even-count assertion (`RealKeypressCount`) needs no read. The mutation case
"the self-test's pairing keypress is deleted" makes an odd number of real presses, which is the defect, yet
measured on this box it still leaves the key where it was (OFF before, OFF after): the unpaired press is the
module blinker's own, so Shutdown's `Stop()` finds the belief true and the key lit and clears it. That is the
1.0.4 corrective toggle doing its job, and the one place in the suite where the hardware read is still relied on.

#### fix/aibrain

**The poke reaction stays on the text path under the vision-for-every-remark decision (2026-09-29).** "The code
stands" in the owner's BUG-010 ruling includes `OnPokeReaction`'s `allowVision: false`: a poke is a reaction to a
click, and a vision glance is too slow to read as one (the module's own figure is about 11 s cold against about 5 s
on the text path, `AiBrainModule.VramStatusLine`). The hotkey, the tray row and the unprompted drop all allow
vision; the module self-test pins all four routings (`engine/AiEngineProbe.Module.cs`). Changing the poke is a
separate decision to take on purpose, not a drift to make in passing.

**The explicit ask declines under the fullscreen stand-down and says so in the log only (F067, 2026-09-29).** A
refused hotkey has no responder chain behind it, so unlike the drop and the poke nothing speaks in its place. A
log line was chosen over a canned spoken line: on a single monitor the companion is hidden while the game runs and
a bubble would land behind it, and the log is the file SUPPORT.md asks users to attach. The setting's label now
names the hotkey so the refusal is not a surprise.

**The settings load stays on Init's thread with the full 10 s cross-session budget; what changed is that giving up
is no longer silent (F096, 2026-09-29).** The audit's own verifier made the case against shortening it: a save that
returns false is retried by the next click, but a load that gives up returns defaults with every write blocked
until restart, so a shorter budget would make the worse outcome more likely under a transient stall, and the worst
case (a peer hung inside the lock) is not the two-instances-start-together case, which costs milliseconds. Moving
the load off-thread would mean ApplyState, the tray rows and the hotkey all starting from settings that arrive
later; not worth it for a stall that needs a hung peer. The timeout path, the future-schema path and a host that
gives no storage now each carry a `LoadWarning` that Init logs once.

**A host that gives this module no storage gets defaults and no persistence, never a temp folder (N-gates-02,
2026-09-29).** `AiPaths` used to fall back to `%TEMP%\DesktopAICompanion.AiBrain` when nothing had set a root,
which is where `--module-selftest=aibrain` left an ai-settings.json and its .lock on every by-hand run. The shipped
host always provisions a storage directory, so the fallback served only the headless convention host, and serving
it by writing into a directory nobody owned was the wrong answer. ModuleKit's `ModulePaths.FromStorage` still
carries the same fallback for every other module; that is the ModuleKit owner's call and is noted, not changed.

**The vision dropdowns keep the name-marker filter for backends that report no capabilities; the ask does not
(F102, 2026-09-29).** `ChooseModel` now gates a vision ask only on a REPORTED `Vision == false`: a listed model the
marker list does not know is used as configured and named `configured-unverified-vision` in the log, because the
list is maintained by hand and had gone stale (no gpt-5, gemini-3 or grok-4 on 2026-09-29), and a miss used to
reroute a working configured model to another vendor's with an advisory that said it "isn't available" while it
sat in the list. `BuildModelOptions(visionOnly)` keeps filtering `/v1` lists by the same markers, deliberately: on
OpenRouter that list is 460 ids and an unfiltered vision dropdown defeats the filter's purpose, and with the ask
no longer punishing a miss, a model the dropdown hides can still be configured by file or migration and will run.
The marker list was refreshed with the families that had shipped; refreshing it again is the maintenance this
records, not a design change. Reached only when the composite can enumerate at all (F103), which is why the two
landed together.

**A cloud endpoint that answers is reachable, whatever it answers (F107, 2026-09-29).** `OpenAiCompatBackend.
IsAvailableAsync` used to return false on a 401, so "Test connection" told an OpenAI user with a mistyped key that
api.openai.com was not reachable, and the cloud+local composite treated a keyed-out cloud as down. Now only a
redirect and a transport failure or timeout are "not reachable", and the status surfaces from the chat request,
where the recorded no-fallover-on-a-bad-key decision wants it (`FallbackBackend`, HISTORY-pre-1.0.0.md 53d130b87).
The cost, stated: a cloud answering 5xx during an outage now counts as reachable, so each ask pays one failed
cloud request before `IsRetryable` fails it over to the local leg, where the probe used to short-circuit that.
`OllamaClient` keeps the SUCCESS probe, because `EnsureServerAsync` uses its answer to decide whether to launch
`ollama serve`, and a foreign server answering 404 on :11434 must not suppress the launch.

**A substitution is announced before it is proven (F072, 2026-09-29).** The old order generated a remark on the
substitute first and spoke the advisory instead of it, so the substitute was known to work before it was named;
the price was a capture, an OCR pass and a generation (a cold load under the default residency) thrown away, plus
a remark remembered as spoken that nobody heard. The advisory is spoken from `ResolveBeforeCapture` now; if the
substitute then fails, the failure line names it and the next ask stays quiet, which is the same silence with one
more line in the log.

**Certificate revocation checking stays off on the cloud slot's TLS handler (F079, 2026-09-29): an owner-decision
candidate, not a lane decision.** It is the .NET default. Turning `CheckCertificateRevocationList` on for the
non-loopback handler makes an unreachable OCSP or CRL responder a hard failure ("The SSL connection could not be
established") of every cloud ask and of "Test connection" on the networks most likely to be hostile, and the
exploit it closes needs an on-path attacker holding a revoked-but-unexpired certificate for the provider host.
The host's own `src/dotNet/SecureDownload.cs` and Remembrance's `WhisperInstaller` carry the same posture, so a
flip belongs to all three at once, with `DescribeChain`-style text that shows the revocation cause, or to none.
If the owner wants it: `AiEndpointPolicy.CreateNoRedirectHandler(bool remote)` setting the flag when the
normalized base is not loopback is the whole module-side change.

**The credential caps stay as they are (F092, 2026-09-29).** 32 scopes x 16 KB ciphertext exceeds the 256 KB file
cap in arithmetic only: it needs about 24 keys of 8 KB each, and real provider keys are 50-200 characters (32 of
them serialize to about 20 KB). If a real key ever approaches 8 KB, the useful half is a size reason on the save
refusal, not smaller caps.

**Every Apply still rebuilds the brain; what a same-backend Apply no longer does is evict its model (F065, F095,
2026-09-29).** The audit proposed skipping the rebuild behind a fingerprint of the settings the brain reads. The
brain reads the persona (CompanionName, UserName, Disposition) from its own settings clone on every prompt, so
that fingerprint would have to name every such field and would silently stop an edit reaching the brain the day
it missed one; the rebuild is cheap (a new HttpClient, a listing) and is kept as the one path every settings
change takes. What cost 5-11 s was the eviction on retire, and that is now gated on `BackendFingerprint`, which
names only what decides which model is resident where. Under "unload" the eviction on retire stays, because the
model is gone after each remark anyway and the unload is free. Round 2 (R-011, 2026-09-30): UseVision joined the
fingerprint, because it decides which of the two models is resident; a vision toggle under "keep" now costs one
cold reload, which is the eviction it needs.

**The repeat-guard retry keeps its possible second cold load under "unload" residency (F070, 2026-09-29).** The
audition, a burst of five requests the module itself issues, now holds its model for a minute between samples
and evicts when the run ends. The live retry is left alone: it fires only after a model has repeated itself,
which is the case the guard exists for; giving its first request a longer keep_alive than its second needs a
per-request keep_alive on the backend interface that every backend would have to carry; and the repo's one
recorded audition timing under the same eviction (5498/3803/419/380/385 ms) shows the race against Ollama's
asynchronous eviction is won more often than lost, so the doubled load is a possibility, not a rule.

**Reachability probes are bounded at ten seconds, and the composite asks both legs at once (F105, 2026-09-29).**
Ten is a chosen number, not a measured one: far beyond any server that is answering (a running Ollama answers
/api/tags in 5-56 ms, measured 2026-09-27 for the VRAM line) and short enough that a cloud whose traffic is
silently dropped costs an ask ten seconds rather than two minutes of probe before the local leg is asked. The
chat's own deadline is the user's timeout setting and is untouched. The audit's third suggestion, remembering a
recent primary timeout and trying local first for a while, was not taken: the concurrent probe already answers
from the local leg as soon as it is up.

**The model inventory follows reachability; there is no periodic re-list (F071, 2026-09-29).** The audit's
"bounded to once every few minutes" was not taken: a timer re-lists a backend that has not changed on every tick
of its schedule, and the two events that change what is installed are visible without one. A restarted server is
a down-then-up transition and is re-listed on the way back; a model pulled while the server stays up is followed
by the user pressing Refresh in the pane, which now reaches the live brain. The gap that remains is a pull with
neither: the brain substitutes until the next Apply, and says so once.

**The "ocr engine:" log line still costs one OcrEngine creation before the read (F077, 2026-09-29).**
`RunOcrAsync` words that line from `WindowsOcr.IsAvailable` before `RecognizeAsync` creates its own engine. Kept:
the line is written BEFORE the read on purpose, so a hung or crashing recognizer still leaves the engine's name in
the log, and caching availability per brain would hide a language pack removed mid-session. Its cost is unmeasured
and the finding gave no figure for it; what was fixed is the PATH walk's per-entry exception and the per-ask
re-resolution, which were the two costs the finding did name.

**Windows OCR states a property, not a saving (F109, 2026-09-29).** The finding measured 45-60 ms against 1-2 ms
in a warm loop. Under the measurement rule this campaign works to, a warm-loop delta is not a number to publish,
so neither the code comment nor the disposition carries it. The claim is that no codec pass runs on the ask path,
which the self-test asserts through two route counters, and that the PNG route survives as the fallback for a copy
that throws rather than being deleted.

**A model listing is bounded at thirty seconds, and the transition re-list stays on the ask path (R-014,
2026-09-30).** The F071 re-list put a second network call to a possibly hung cloud primary on the ask path, under
the chat deadline F105 had just taken off the probe. The listing now runs under its own bound, thirty seconds:
sized for the 8 MiB listing cap over a slow link rather than for a reply (OpenRouter's 760 KB catalogue lists in
seconds), and above the 10 s probe bound because a listing is a body, not a handshake. The audit's alternative,
not awaiting the re-list, was not taken: the inventory it feeds decides the advisory spoken BEFORE the capture
(F072), and an un-awaited listing would have the first ask after a transition proceed on the old inventory. An
empty listing, which is what a tripped bound yields, keeps the previous inventory rather than replacing it.

**The ask trusts a reported blind model, the dropdown does not, and the substitute is never a reported-blind
model (R-020, 2026-09-30).** F102 hardened the ask's gate on a reported `Vision == false` while the dropdown kept
the union, and the register never reconciled the two with the 2026-09-10 measurement that Ollama's /api/tags
under-reported Gemma. Re-measured 2026-09-30 on this box (Ollama 0.34.4, /api/tags and /api/show, neither loads
a model): the two agree for gemma3:4b, gemma4:12b and gemma4:26b, so the under-report belongs to older servers.
The policies now differ on purpose and say so: the dropdown keeps the union because an older server may still be
in use and a hidden model is the worse failure; the ask trusts the report because a blind model on the vision
path fails silently, and a current server reports right. The substitution loop applies the gate's rule, so the
one inconsistency that was reachable, an advisory naming the model it had just rejected, is gone.

**Substitution is a local courtesy, never a cloud purchase (R-022, 2026-09-30).** BUG-002's first-listed
substitution was written for a local Ollama, where the first listed model is free. F103 made the composite
enumerable and so extended it to the default cloud shape, where the substitute is billed on a model the user
never chose, the outcome F101 had already called a defect for the blank id. The brain now sets the policy from
its primary slot: local substitutes, cloud does not, and a cloud id the provider lacks ends the turn on an
advisory naming the host, once. Enumeration stays, because it is what makes that advisory fire at all.

#### fix/fortunes

**Apply keeps a smart index whose pool did not change; "Rebuild smart index" always rebuilds one that is
not complete (2026-09-29, fortunes 1.0.12).** F147 asked for the keep-or-rebuild decision to live in
RebuildEngine, and F149 (fixed by lane fix/gates) made the button's own "already built" guard compare
against a freshly built pool. The two compose like this: every rebuild computes the new pool's signature
(text AND topic, since Pick's route bonus reads the topic and dedupe can swap which same-text entry
survives) and keeps the current picker, built or still building, when smart is still on, the signature is
unchanged and the build did not fail; the button passes `force`, so a picker that stood down or is mid-warm
is rebuilt on request, and only a COMPLETE index over an unchanged pool answers "already built". A failed
Save no longer rebuilds at all: the persisted settings did not change. Rejected: deriving "smart enabled"
for the status from `LoadFortuneSettings(_host)` per press (F148's alternative), which re-reads the
settings file to learn a value RebuildEngine already had in hand; the setting is recorded in a field when
the engine is rebuilt.

**The startup parse and Apply's rebuild stay on the calling thread; the pane actions that change the folder
parse off it (2026-09-29, fortunes 1.0.12).** F127 asked for the corpus parse to leave the UI thread
everywhere. Two callers keep it. Init, because IModule's contract is a usable module when Init returns:
the host's own `--fortunes-selftest` raises CompanionLanded straight after LoadFrom and expects speech, and
at startup the parse runs inside the StartUp constructor before the message loop exists, so there is no
window to freeze, only the first pet to delay (the audit's 0.25 s on a default install). Apply, because the
Fortunes pane carries an Info field, so the window re-runs Load the moment Apply returns and reads the pool
count from the provider (a background publish would have shown the stale count), and Apply is a cache hit
unless the folder changed underneath it. Rescan, Import, Download and the Rebuild button, whose purpose IS
a changed folder, parse on a pool thread and publish on the UI-thread continuation behind an engine
generation, the shape AiBrainModule.BeginVramProbe and the smart build already use; the per-file parse
cache makes a changed folder cost only the files that changed. No timing is quoted: the property (what no
longer runs on the UI thread) is stated instead.

**A host that hands the module no settings store gets smart picks OFF (2026-09-29, fortunes 1.0.12).** The
default is ON for the real host, which always returns a store. A null store means the module cannot
persist the user turning the expensive default off, so it does not assume the expensive default. In
practice the only such host is the convention self-test's, whose Init used to start an unobserved embed of
the whole corpus into the TEMP fallback root on every `--module-selftest=fortunes` (F121, N-gates-02). The
engine self-test's host hands an EMPTY store instead; seeding `smartFortunes=false` there is the host
lane's half.

**FortunePaths keeps its TEMP fallback root (2026-09-29).** N-gates-02 asked for `FortunePaths.cs:29` to be
dispositioned. The fallback stays: a module loaded by a host that hands it no storage still needs somewhere
to put a vector cache if smart picks are on. What changed is that nothing reaches it by accident: reading
the fortunes folder no longer creates it (`CustomDir` is the path, not the created folder), the module's
own SelfTest runs the probe under a scratch root it removes and asserts the fallback root gained nothing,
and a no-settings host gets smart picks off.

#### fix/petstudio

**The analyzer adopts the validator's parse rather than calling the host's `TryReadXml(stageImages: false)`
(2026-09-29, F155 / BUG-012).** The host lane grew that overload for this finding and it is the smaller
change; it was declined on a reading of what it still runs. `CompanionXmlValidator.TryParse` proves the
sheet with `Image.FromStream(stream, true, true)`, a full GDI+ decode, and `TryReadXml` calls `TryParse`
again on the same text, so the overload would have left two validating parses and two GDI+ decodes of
the sheet per analyze where the analyzer needs one graph. Assigning the RootNode the analyzer's own
`TryParse` returned to `Xml.AnimationXML` and calling the host's `LoadAnimations` (which runs
`ResolveMagicAnimations`) gives the walk the engine's exact entry ids from one parse and no bitmap; the
field is public in the source-linked `Xml.cs`, so a host that makes it private breaks this module's
compile rather than its behaviour. Mirroring the three-step magic-name resolution in the module was
declined for the drift the source-link exists to prevent. What still runs per analyze, on a pool thread:
one `TryParse` (the XSD deserialize, the base64 of sheet and icon, the GDI+ proof decode),
`LoadAnimations` (which also base64-decodes each `<sound>`), the reachability walk and `BuildNodes`. No
timing is claimed, because nothing here was measured cold in interleaved fresh processes; the property is
that the dispatcher thread parses, decodes and tiles nothing per analyze and decodes the sheet for the
preview once per sheet.

**`BeginAnalyze` is the rule-6 shape plus a remembered rerun (2026-09-29, F155).**
`AiBrainModule.BeginVramProbe` and `AgentFlowModule.BeginSetupProbe` DROP a request that arrives while a
probe is in flight, which is right for a cache the next tick refreshes anyway. An analysis has no next
tick: a request that arrives mid-flight describes newer text, and dropping it would leave the editor
showing a verdict for text the author has already changed until the next pause. So the gate is kept (one
analysis in flight, ever) and a request the gate refuses sets a UI-thread-only rerun flag, honoured after
the flight lands and its result has been judged by the generation. `FortunesModule.RebuildEngineAsync` is
the same generation-then-publish shape without the gate.

**An import's status defers to the analysis verdict; "the host would reject it" is said only when the
validator refused (2026-09-29, the PetStudio half of the tools lane's F429 note, and F165).**
`LoadConvertedIntoEditor` announced "but the host would reject it" whenever `ConversionResult.Accepted`
was false, and `Accepted` is the CONVERTER's bar: Valid AND RoundTrips AND no unreachable animation. A
valid pet with one unreachable animation was reported as rejected by a host that had accepted it. With
the analysis asynchronous there is one writer of the status, `RenderAnalysis`, so the import supplies a
prefix (`ImportedStatusPrefix`: the name, the skin count, and the one converter fact the analyzer cannot
see, a pet whose XML does not round-trip through the host's serializer) and the analyzer's verdict
follows: "runs, but N animation(s) will never play" for the unreachable case, "would reject" only for the
validator's refusal. The "Preview or install." call to action went with it; "good to go" is the same
statement. The timeline's dropped-step note (F165) rides the same sentence, which is what lets it render.

**Surface poses know which surface they hold, and the growth passes through flip turns (2026-09-29, F150).**
The first cut of the rule -- pass through gravity-less flips, apply the axis test at the seed only -- was
measured over the shipped corpus with a Python replication of BuildNodes + SurfacePoses + Of (validated
against the fixture census the shipped self-test prints, Jump=2 Climb=2 Move=13 Idle=29 Engine=8) and
rejected: it badged the coloured sheep's `fall fast` CLIMB through the `hang` turn, and admitting any
vertical component at a wall badged `wall_kickjump` (x -15, y -5..20) and ssj-goku's three `Flying_*`
glides CLIMB. The rule that ships carries the surface kind through the growth and demands travel purely
along it (a wall pose keeps x at 0, a ceiling pose keeps y at 0), so on the same corpus every label change
is a correction: the 32 converted shimeji-* companions are unchanged; the bundled sheep and esheep64 change
exactly #8 boing CLIMB->MOVE, #39 top_walk2 MOVE->CLIMB, #41 vertical_walk_down Idle->CLIMB; the seven
coloured sheep each lose CLIMB on `jump`, `jump_down`, `blastoffb/c`, `king_jump*` and `king_jumpB*` (now
JUMP or MOVE), on `fall_die`, `chasebend`, `king_fall*` and `jump_down_fail1/2` (now unbadged), on
`alienchaseend`, `chasewend`, `bsheepchaseend`, `shipchaseend` and `walk_death` (now MOVE) and on
`king_slam`, and gain it on `walk_down` and CLING on the two `king_rotateB` turn halves; fox, mimiko, neko,
pink_fox, pink_neko and yellow_neko stop badging their four diagonal `run_ul/ur/dl/dr` glides CLIMB (now
JUMP or MOVE); pingus loses CLING on `fly` and `fall2a/c`, CLIMB on `fall2b/d`, and CLIMB on `walkup`,
whose entry edge is flagged only="horizontal" (the engine's top-of-screen edge, which the floor walk that
carries the edge can never hit) while its motion is vertical, so it falls back to the physics reading,
JUMP. The engine's flags were checked rather than assumed: FormCompanion fires VERTICAL at the left and
right edges of the work area and HORIZONTAL at its top. The label vocabulary has no FALL, so a gravity-less
drop reads "Plays in place" once it leaves the surface set; that predates this change and is not widened
here.

**Typing during an import is still replaced when the conversion lands; Open and the picker are refused
instead (2026-09-29, F163).** The finding offered a document generation -- a counter bumped by
SetEditorText, Open, the picker and TextChanged, compared by LoadConvertedIntoEditor, with a "conversion
finished" affordance to load the result by hand when it moved. Declined: the two document-SWAP paths are
closed by the same guard the two imports already used, which costs one line each and no state; the
generation protects only edits typed into the editor during the seconds a conversion takes, adds a fifth
writer to a counter and a second way for a conversion to end, and the window has no place to show a
finished conversion that is not in the editor. If that case ever matters, the generation is the design.

**`FindBundleRoot` skips a folder it cannot list rather than failing the import (2026-09-29, F164).** The
finding offered the other repair too: let the exception reach `ImportSkinFromRootCoreAsync`'s catch so the
status reads "Import failed: Access to the path ... is denied". Declined: `SkinLayout.Detect`, which runs
next on the same tree, already skips such a folder and would have converted the desktop skin beside it,
and a bundle one level down should not be lost to an unrelated sibling the process cannot read. The walk
is breadth-first through an injected lister so the self-test can deny a folder without an ACL; the
production lister skips reparse points and a visited set guards a junction cycle, neither of which the
AllDirectories enumerator promised.

**F151, re-closed with F150 (2026-09-29).** Done: `AgreesWithTheFixture` asserts five named labels on the
fixture (#37, #39, #41 CLIMB; #8 MOVE; #42 unbadged), by id and name, each of which failed on the 1.1.17
classifier, and the identity line it opened with is gone; the gates lane's deferral under `#### fix/gates`
is discharged.

#### fix/reminder

**A reminder is held while no companion is on screen; speech switched off does not hold it (2026-09-29, F199).**
`ReminderModule.CheckDue` skips a due calendar reminder, a due personal reminder and the daily briefing without
marking them fired when `AnyCompanionOnScreen()` is false, exactly as it already skipped them for quiet hours,
because `IHost.SayAll` drops its line with no persistent pet out and the three commitments (the fired id, the
once-only `Enabled`/`LastFired`, `briefingLast`) were being written before that drop. The finding also proposed
holding on `IHost.SpeechEnabled == false`. Declined: the chime and the reaction still reach the user with speech
off, AgentFlow's recorded decision in `AgentFlowModule.Apply` ("these gate the SPEECH ONLY") is that speech-off
must not withhold them, and a reminder held on speech-off would re-chime on every 20 s tick until speech came
back. The self-test pins both halves (`WITNESS speech switched off does not hold a reminder`).

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

**F050 (the rule regex does not cross a newline) is left as it is until the JS original changes (2026-09-29).**
`PermissionRules` is a port of `ai-acolyte/src/permission-match.js`, which is the canonical matcher behind the
wildcarding pass, and `docs/agentflow/agentflow_join.py` is its second port; all three compile `.*` without a
dotall flag, so a command segment holding a quoted newline evaluates WouldPrompt in every one of them. Putting
`RegexOptions.Singleline` into the C# alone would make the port disagree with its reference on precisely the case
no differential varies, which is the drift the "changing one means changing all three" rule exists to forbid. The
order of work is: confirm against Claude Code with both shapes (a plain quoted newline, and the heredoc inside
`$( )`, which Claude Code may prompt on regardless of allow rules); change the JS original; then both ports; then
add both shapes to the shared corpus. The first three steps are outside this module's lane.

#### fix/followups

**The MoveFileEx fallback keeps its P/Invoke and gains the extended-length form; it does not move to
File.Move(overwrite: true) (2026-09-30, N-gates-01).** Both `AtomicFile` twins (ModuleKit, and the host's in
`AppSettingsStore.cs`) now hand `MoveFileEx` `\\?\`-prefixed paths (`\\?\UNC\` for a network path), normalised
first because the prefix switches Win32's own normalisation off. `File.Move(overwrite)` handles long paths
by itself and was rejected: it asks for `MOVEFILE_COPY_ALLOWED` without `MOVEFILE_WRITE_THROUGH`, so it may
degrade to a copy-and-delete and returns before the rename is on disk, and the write-through rename is the
durability the fallback exists to give. The app has no `longPathAware` manifest entry, so the registry's
`LongPathsEnabled` does not reach a raw P/Invoke; .NET's own file APIs add the prefix themselves, which is
why `File.Replace` and `File.Copy` on the neighbouring lines never failed. Pinned in CoreTests through the
`replaceFile` seam at a 400-character path, with the same forced fallback at a short path as the WITNESS.

**RecordingHost hands out snapshots of what it recorded, and `List<string>` stays their type (2026-09-30,
N-remembrance-01).** `SaidLines`, `LoggedLines`, `OpenedLinks`, `BroadcastLines` and `SaidToCompanions` are
appended under one lock and every property read is a copy taken under it. Two alternatives were rejected: a
thread-safe collection type, which changes the type every module self-test compiles against and loses the
`List<T>` members some of them call; and locking the writers alone, which leaves the reader side exactly as
racy as before unless every test learns to take the lock. The price is that a `Clear()` on a property clears
a copy, so `ClearSaidLines`/`ClearLoggedLines`/`ClearOpenedLinks` exist and Remembrance's two call sites moved
to them. `PlayedAnimations`, `PlayedSounds` and the contribution lists stay live: the module appends to them
from the raise the test itself made, on the test's thread.

**A failed `Save()` on the fake settings shows the disk, not the handle (2026-09-30, N-blinkingled-02).** The
host hands a fresh instance loaded from disk to every `GetSettings`, so a module that re-reads after a failed
write sees the old values, and the fake now puts them back. Modelling the host exactly (a fresh handle per
`GetSettings` over one shared store) was rejected: every module self-test seeds through `SettingsFor` without
a `Save()`, and those seeds would never reach a module's handle. The residue is stated at the site: a module
holding one handle across a failed write keeps its own edits under the shipped host, which the fake cannot
show at the same time as the disk; both current `FailSaves` users (BlinkingLed, Fortunes) re-fetch.

**AgentFlow's reveal button asks for its own settings file, and the pane names the log's path (2026-09-30,
N-host-03).** The host's rule that an owned pane reveals only inside the module's own storage stands
(F376's narrowing, its recorded intent being to stop reveals in the app's own settings folder), so the
module side moved. Three alternatives were declined: a second permitted root for the app's top-level log
(the host lane's call, and against that intent); a new `IHost` verb for the app's log (an ABI member for one
button); and removing the button (it is the only shipped `RevealsPath` consumer, so the containment
machinery would have no user, and a module's data folder is worth a click for support). What the old button
was really for, the log's location, is stated as an Info row in the same section through the two-levels-up
arithmetic the module already asserted; the walk step H15 in `SMOKETEST.md` presses the real reveal, which
no automated check does.

**A chain step for a collapsed member plays the survivor's poses under the member's own name (2026-09-30,
N-tools-01).** Only the poses move: the step keeps `<seq>_<n>_<member>` with the member the sequence declared,
because that name is what the residue's chain accounting and a reader of the emitted XML look for, and the
survivor's frames are the member's by the collapse rule. Renaming the step after the survivor was rejected
as making two members of one run read as the same animation. The converter gained no format-ladder rung
for this, for the reason `#### fix/tools` gives for F431: a chain step's provenance is not recoverable from
emitted XML, and no shipped pet carries a chain step at all.

**`ModulePaths` degrades to "no root" rather than throwing from `FromStorage` or falling back to `%TEMP%`
(2026-09-30, N-aibrain-02).** Three shapes were on the table. The old one, a stable `%TEMP%\DesktopAICompanion.<id>`
folder, wrote into a directory nobody owned or swept on every headless self-test run (the N-gates-02 leak).
Throwing from `FromStorage` fails a module's Init under the app's own convention self-test host, which hands
no storage on purpose as the gate's exercise of every module's null tolerance (F341), so it would have made
`ModulePaths` unusable in the very test the repo runs. Chosen: `HasRoot` false and a `Warning` to log, with
every path member throwing that warning, which is AiBrain's N-gates-02 shape (defaults, nothing persisted,
said once in the log) generalised. The template shows the check; the only in-tree caller was the template.

#### burn/aibrain

**The pane's actions read what is on screen, and the module's floor is host 1.2.5 for it (RA-055, 2026-09-30).**
"Show me 5 examples", "5 about my screen", "Test OCR" and "Test connection" set `PaneAction.InvokeWithPendingAsync`,
the member host 1.2.5 added and F227 recorded as having no adopter; the host prefers it when set, so the audition
is of the disposition in the dropdown, not of the last Apply, and the header says "as shown in the pane, not yet
applied". The values reach the action as a detached copy of the live settings (CloneForBrain) with the pane's own
Save mapping applied (`ApplyPaneValues`), so an action can neither save nor see a stale instance; Test OCR resets the
live brain's Tesseract cache with the SAVED path only, because a typed path is a proposal until Apply. The cost is
`MinHostVersion = "1.2.5"`, which the lane rules allow when a module starts calling a member a newer host introduced;
the shipping host is 1.2.6, so the sequencing rule (never offer a module its host refuses) already holds. The
saved-values delegates stay set beside the pending ones as the documented fallback shape.

**A key that cannot be stored refuses the whole save (RA-058, 2026-09-30).** `TrySetApiKey` answers false with a
reason for a key typed with no cloud provider or endpoint to scope it to and for a DPAPI refusal, and Save used to
discard both and report success while the pane's "set" hint described a key that did not exist. Now nothing is
saved and nothing is applied, the host says the pane could not be saved (true: the file was not touched), and the
reason goes to the log, the only channel the pane's bool has. The alternative, saving the other fields and returning
false, was rejected because the host's message would then be untrue for the fields it did save. What this leaves: the
live `AiSettings` was written through before the key was judged, so it carries the pane's other typed values,
unsaved and unapplied, until the next Apply or restart; the pane keeps showing what was typed and the brain runs the
saved configuration (N-burn-aibrain-02 records the torn state and the copy-and-swap shape that would remove it).

**The brain says whether it started, on the pane and in the tray (R-013, 2026-09-30).** CanUse's refusal (an invalid
endpoint, consent missing, a cloud slot with no model) reached the user only as a diagnostic-log line while Save
reported success and the tray offered an inert "Ask about my screen". The pane has a "Status" Info row
(`BrainStatusLine`: Off / Not started: <reason> / On), read from the settings rather than the session so it is right
the moment the pane reloads after Apply, and the tray row's Visible is `_session.Enabled`, the brain's actual state,
not the stored switch; the toggle row keeps reading the switch it flips. Save still answers true: the file was saved.

**Every refusal on the explicit path is logged; the responders stay quiet (RA-060, 2026-09-30).** F067 logged one of
`Ask`'s early returns, the fullscreen stand-down, with a rationale that applies to every other one on the hotkey and
tray path: nothing speaks in a declined hotkey's place, so a refusal that is not logged is silent to the user and to
the file SUPPORT.md asks for. All five say their category now (`ask declined: brain off | speech off | busy |
no companion | capture failed | fullscreen stand-down`), gated on the path having no subject, because a declined drop
or poke falls through to Fortunes and the user hears a fortune, which is the log line.

**The inventory follows reachability, and is re-listed while it is unknown; the composite cannot enumerate while its
primary is down (RA-062, 2026-09-30; amends F071).** F071's rule, no periodic re-list, stands for a KNOWN inventory.
Its gap was the cloud+local composite: reachability is the OR of two legs, so a cloud primary that comes back while
local Ollama stayed up is never a transition of the whole, and a retired cloud id then failed every turn with
http-400 (deterministic, no fallover) until the next Apply or pane Refresh. Three changes close it: the composite
remembers its primary's own probe answer and answers "cannot enumerate" (null, no request) while that was "down";
the brain re-lists on the transition to up and while its inventory is still unknown; and an empty listing is never
stored, so unknown stays unknown until a listing answers. The cost, named: while nothing is known, one listing per
reachability check, which against a down primary is no request at all and against a local Ollama with nothing
pulled is one GET /api/tags per ask (a configuration whose asks fail anyway). A known inventory is still never
re-listed per ask, and the pane's Refresh still reaches the live brain.

**The session owns the backend fingerprint and decides the eviction at retire time (R-012, RA-061, RA-079,
2026-09-30; extends F065/F095).** The module recorded the fingerprint when it ISSUED an Apply, so it described the
brain the session would build, not the one it had: an Apply cancelled while queued behind an ask (the whole
in-flight remark or "keep" warm-up, not a sub-second window) left its fingerprint behind, the next same-fingerprint
Apply retired the OLDER brain without eviction, and under "keep" that model carried keep_alive -1 and outlived the
process. `AiSessionManager.ReconfigureForBackendAsync` takes the fingerprint and whether the residency keeps;
`_liveFingerprint` is written when a brain is built (the reconfigure and the lazy build), cleared when one is
retired, and compared under the gate at retire time. A fresh brain superseded between its factory and the currency
check has loaded nothing of its own, so it is retired against what comes next, the superseding generation's
fingerprint and residency, rather than evicted unconditionally (RA-079); the same fingerprint under "keep" keeps
the model the previous brain left resident, a different one evicts it.

**A settings file held open by another process is not corruption (R-018, 2026-09-30; extends F098).** A sharing
violation or an access denial on the read says nothing about the document, yet `TryRead` filed both as Unreadable
and the recovery copied the intact file aside as `ai-settings.corrupt-*.json`, restored the backup over it (which
failed on the held file) and blocked writes for the session with a warning that asserted two things that had not
happened. The read now answers `Locked`: defaults for the session, every write blocked so a file this process never
read is not overwritten blind, and a warning naming the holder; the next launch reads the file as it was. The
"held" verdict is for `IOException` other than the path kinds (FileNotFound, DirectoryNotFound, PathTooLong) and
for `UnauthorizedAccessException`; a JsonException, a decoder fault and an oversize file stay corruption. Beside
it, `PreserveCorruptPrimary` splits the copy from the delete: a copy that succeeded is kept whether or not the
primary could be removed, and the warning says which.

**The audition applies the ask's rules and is bounded like a retirement (RA-063, RA-056, RA-065, RA-073, 2026-09-30).**
The persona audition is a brain the module builds and throws away, and three of the ask path's decisions had not
reached it: a cloud primary never substitutes (R-022) now holds for the audition too, so a retired cloud id is one
"(not run)" sample with the advisory and nothing billed; the substitute a turn does send is remembered on the brain,
per path, and released with the configured ids, so the fullscreen stand-down, a retirement and shutdown evict it
under "keep"; and the audition-end eviction runs under the same 2 s bound a retirement's does (a chosen figure,
RetireBrainAsync's, not a measured one), because on a cloud composite it round-trips to local Ollama for a model
the canned samples never loaded and used to hold the pane for the chat deadline. The three call sites the audition
decides at (the keep_alive window, no warm-up, the eviction) are pinned through a factory seam on the module,
`AuditionBrainFactoryForDiagnostics`, the shape of AskSinkForDiagnostics.

**The composite warms the local model for the PATH, and the backend interface gained a default member for it
(RA-087, R-021, 2026-09-30; extends F104).** F104 made the fallover choose the local model from the request (an
image means vision) because with one cloud id in both slots the id says nothing; the warm-up still went through the
id mapping, so a "keep" launch with vision OFF pinned the local VISION model (keep_alive -1) that no text fallover
would use, and the text model then loaded beside it on the first fallover. `ICompanionBrainBackend.WarmUpAsync(model,
visionPath, ct)` is a default interface member (modules compile with LangVersion latest) that forwards to the id-only
warm-up; only `FallbackBackend` overrides it, warming the local model for the path and remembering what it warmed so
the release covers it. A default member rather than a second optional interface, because the brain already holds
the backend as this type, and every double keeps compiling untouched.

**One observe-fault continuation in the engine, and an invariant counts it (RA-086, 2026-09-30; extends F064).**
F064's "one helper" was true of the two copies it named; FallbackBackend and OllamaClient carried two more,
byte-equivalent. Both call `AiEndpointPolicy.ObserveTaskFailure` now, and `tests/runtime-hardening-selftest.ps1`
requires the OnlyOnFaulted continuation in `AiEndpointPolicy.cs` alone over the comment-stripped engine sources,
with the qualified call count as its positive control, so the next private copy fails the gate by name.

**The probe skips nothing, guards every check, and pins its own seed (RA-069, RA-070, R-017, RA-074, RA-072, RA-071,
RA-076, RA-077, 2026-09-30).** Both runners grade a SKIP: line as a failure of the whole run, so the "skip-pass" the
OCR recognizer branch promised (and CheckCloneForBrain's DPAPI SKIP repeated) never existed; both are FAIL lines
naming the check, F083's shape, and an invariant keeps every `"SKIP:` literal out of the probe. `GuardedCheck` wraps
each check in every group, so one throw is a FAIL naming the check and the group's later checks still run, where the
group guard (F082) hid them. Every module instance the probe builds seeds through one function with the
`127.0.0.1:9` pin, and CheckModuleEntryPoints, which wrote its own seed without it, asserts the pin. The Test OCR
verdict's tautological `!= null` is a shape check; the UI save-budget ceiling is derived from the constant with the
product ceiling (under two seconds) stated once as its own check; the two write-only counters on the doubles are
read where they pin a request count.

**`AiBrain.ScreenChanged`, `ComputeSignature` and `_lastFrameSignature` stay, unused (RA-066, 2026-09-30,
ACCEPTED-RECORDED).** The module's own idle timer was their one caller and went in aibrain 1.2.3, when unprompted
commentary moved onto the host's global drop schedule (HISTORY-post-1.0.0.md, and the comment in AiSessionManager
where the generation-guarded wrapper used to be). They are kept as the primitive a future "only speak when
something on screen actually changed" option would be built on: self-contained, a 16x16 luma signature and a
threshold, and the capture path they sit beside is the one that would feed them. No test reads them, so they can
drift from that path unnoticed; the day the option is built, the first thing it needs is a check that they still
agree with CaptureScreen. Deleting them and re-deriving later was the alternative; the recorded choice since 1.2.3
has been to keep them, and this entry is where that choice now lives.

**The after-retire callback is a seam, named as one (RA-078, 2026-09-30, ACCEPTED-RECORDED).** ReconfigureAsync's
callback parameter and the `_pendingAfterRetire` queue are reached only by the four F089/F091 checks, which observe
retire order and serialization through them, a property nothing else can reach. Under the rule above (a member
whose only reader is a test is not dead, and what the test pins decides) they stay, as `afterRetireForDiagnostics`
with the queue's summary saying what it is; the production entry point (`ReconfigureForBackendAsync`) has no such
parameter, so no production caller passes a null for it any more.

**A mutant's self-test run is bounded by its own baseline (tests/mutate-selftest-guards.py, 2026-10-01).** The
harness ran every mutant with the 1800 s backstop its baselines get. During this lane's `--only` run on 2026-09-30
an exe from this worktree's build was seen holding about ten cores for minutes while the user was at the machine,
and a spinning mutant would have run unattended for half an hour. Measured on the clean tree the next day: the F071
mutant being scored does not spin (3.9 CPU seconds, 11 s under the harness), while the baseline phase, which runs
every graded flag before anything is mutated, `--only` or not, includes `--fortunes-engine-selftest` at 334-374 CPU
seconds over a dozen threads and `--module-selftest=fortunes` at 53: the shape that was seen, a clean-build cost
that belongs to burn/fortunes. The bound stays regardless. `selftest()` times the first (baseline) run of each flag
and gives every later run four times that, never under 300 s and never over the backstop; `subprocess.run` kills
the child at the bound and the verdict reads `did not exit in Ns (killed; its unmutated run took Bs)`, scored
BROKEN rather than FIRED or SURVIVED, because a hang proves nothing about the assertion. Proved by mutating the
bound itself (floor 5 s, factor 0.01): the mutant exe was killed at 5 s and the case read BROKEN by name; the
constants were restored before the commit. The alternative, a per-case timeout, would have changed the nine-field
case shape of all 266 cases to serve one; a bound derived from the baseline the harness already runs needs no new
data and cannot drift from it.

#### fix/deadcode

**A member whose only reader is a test is not dead, and what the test pins decides what happens to it
(2026-09-30; applied to F289, F296, F357; kept and named as seams: F076, F087).** The 2026-09-27 refutation ("NOT dead because CoreTests read
them") stands as a rule of evidence: a test reader is a reader, and nothing a test names is deleted on the
strength of a grep. The lane then asks what the test pins. When the member is a shape production has
abandoned (the integer ScalePolicy helpers behind the fractional path, the commit-pinned URL validator behind
the branch-pinned catalog, the roaming-root copy utility behind a module that owns its storage), the pin
protects nothing shipped, so the test moves onto the production path or goes with the member: F289's
CoreTests scale pins now hold FitFactorForFrameD and ScaleD, F296's two checks are covered by the catalog
self-test's bad-host reject case that already exercised the branch validator, and F357's
TestBoundedDataMigration went with TryMigrateFilesOnce. When the member is a seam a probe needs to observe
a property it could not otherwise reach, it stays and is named as one, with the `...ForDiagnostics` suffix
the AiBrain engine already uses.

**`--security-selftest` is graded by `tests/mutate-selftest-guards.py` through the SECURITY pseudo-flag
(2026-09-30, F298, F300).** The flag writes no marker: SecuritySelfTest.Check prints `[PASS]`/`[FAIL]` lines
and Program exits `Run() ? 0 : 1`, so until this campaign no security assertion had a mutation case anywhere.
The channel reshapes that stdout into the `FAIL:` / `RESULT=` vocabulary the ladder already grades, the way
the CORETESTS pseudo-flag does, and an unhandled exception (the shape F298 removed) grades as BROKEN (no
verdict), never as a firing. Two cases ride it; the baseline runs it with the rest.

**On the AI path the consent IS the disposition: no profanity switch may ever gate a persona the user chose
by name (2026-09-30, F093; the decision predates the campaign, this is where it now lives).** Nobody flips a
profanity switch: they pick Jules Winnfield, or Jeff Ross, or the Drill Sergeant, from a list that says exactly
who those characters are, and that choice is the acceptance. Gating it behind a second toggle would hand a user
who chose a foul-mouthed character a sanitised one with no idea why, the same mistake in reverse as a model
self-censoring to "f***". Fortunes needs its NoProfanity filter because its content arrives unchosen from 158
packs; a persona is chosen by name. This paragraph used to sit on a dead `NoProfanity` field in AiSettings,
one of seven Fortunes-era fields that class persisted and nothing in the module read; the fields went, the
decision stays here.

**The hardening self-test keeps its runtime limits as literals (2026-09-30, F292, ACCEPTED-RECORDED).**
`spriteCount <= 1024`, the 16 MiB pixel bound and the two loops of 32 mirror SpriteFrameStore.MaximumFrames /
MaximumOriginalPixels and FormCompanion's per-root and process child caps. Deriving them from those constants
would make the checks follow a change they exist to flag; the literal is the pin. The defect next to them was
the WITNESS that compared CompanionXmlValidator.MaximumSpriteTiles to the constant it is defined as (F300),
which now pins the literal on both sides and names the converter's two literal copies.

**Remembrance keeps friendly names, not endpoint ids, and MeetingContext.Location stays parsed ahead of a
consumer (2026-09-30, F167, F172).** The `AudioDevice.Id` field was write-only from the day it shipped (the design
began with stable ids and shipped with names), so it went rather than gaining a display-to-id map in a lane about dead
code; the collapse of two same-named endpoints onto one row is filed as N-deadcode-01 for the module's owner, who
decides between ids and names. `MeetingContext.Location` is parsed and read only by the self-test, which is the point:
modules/Reminder/CALENDAR-FEED.md promises the producer that `.location` is safe to include, and the check keeps the
consumer side honest until something reads it.

**The Reminder, Fortunes and host migration bridges stay, each with the condition that retires it (2026-09-30,
F196, ACCEPTED-RECORDED).** `ReminderModule.MigrateLegacy` and the single-lead fallback in `Leads()` go together when the
module's settings gain a schema version that no pre-slot install can carry (nothing observable today says every install
has run the `migratedSlots` marker, so by inspection they stay). `FortuneProvider.TryMapLegacyCategory` goes only when
the tagged parser stops accepting 5-field rows, and the RunParserSelfTest cases that pin the mapping go with it in the
same commit. `LocalData.MigrateRandomDropIfAbsent` goes when an upgrader's disk can no longer hold a pre-1.0
`ai-settings.json`, which is a release-support decision, not a code one. `LegacySettingsReader` and its CoreTests are
one pre-1.0 upgrade-support decision and go together or not at all. The `RegisterDropResponder` /
`RegisterPokeResponder` pair is frozen ABI surface for out-of-tree modules (see the unexercised-ABI entry), not a
bridge, and does not belong on this list.

**A downloaded pack is validated by the loader's own validator before it is written, and a refusal is a download
cause of its own (2026-09-30, F131).** The host verifies URL, hash and size; the CONTENT check is the module's, and it is
`TryValidateCustomPackBytes`, the validator the folder loader and the importer already use (tagged or plain), never the
strict tagged oracle the parser self-test keeps. A refusal is `malformed=N` in the one download log line and a failed pack
in the pane's status, so "Downloaded 1 pack" can no longer be true of a file the loader then skips.

**A folded sub-report line that reports a failure is re-emitted as a `FAIL: ` verdict line (2026-09-30, F120).** The
Fortunes probe folds three sub-reports into its output so a red run says which case failed. The gate's failure printer
and tests/mutate-selftest-guards.py read only lines whose stripped form STARTS with FAIL, so a folded `rewarm_supersedes=FAIL`
or `FILTER FAIL case=3` was visible to a person and to neither tool; the fold prefixes such lines (`name=FAIL`,
`SUITE FAIL case`, `EXC:`) with `FAIL: `. A sub-report that wants its cases graded writes them in that vocabulary.

**EmitterSelfTest's duplicated hub heuristic stays (2026-09-30, F450, ACCEPTED-RECORDED).** HubSequenceTargets and
HubId carry the same gravity-plus-fan-out loop and the hub is rediscovered at eight call sites; both copies agree and the
suite is green. Consolidating them is a refactor of a test with no production property behind it, deferred until the
fixture changes and the two could disagree.

**The converter's self-test is proven by hand until a harness grades it (2026-09-30, F452, F461).** No mutate-*.py
covers tools/ShimejiConvert, so the per-suite guard (a throwing suite is a named FAIL line and the rest still run) and
the csproj's reordered Exists() error were each mutation-tested by hand and the runs recorded in the commit message;
N-deadcode-05 asks for the harness, N-deadcode-04 for the gate to read the SELFTEST-COUNT sentinel the CLI now prints.

**The six host-side IHost fakes stay until a shared HeadlessHost base is designed (2026-09-30, F340, F349,
ACCEPTED-RECORDED).** Six private `IHost` implementations and five `FakeCompanion` copies live in src/dotNet/Plugins,
roughly 600 lines. They are duplication, not dead code, and their deltas are load-bearing: ConventionHost returns null
storage and exposes `...HasSubs` observers a subclass of ModuleKit's field-like events could not read, AiBrain's fake
carries `ClickTray`, Fortunes' carries `PaneNamed`, and DESIGN-REGISTER's existing rule keeps host-only observers off
`ModuleKit.Testing.RecordingHost` (a ModuleKit change stales every published payload). The right shape is an internal
abstract base in the host with virtual services and a scratch storage default, done in one pass after the storage
defaults (F338, F341, F351) settle; this lane deleted the fakes' write-only recorders (F350) and wrote the routing
assertion they were added for, and left the copies.

**The second-tier info items of lane fix/deadcode are recorded, not worked (2026-09-30, ACCEPTED-RECORDED).**
- F112: the tautological IsKnownRate check inside BlinkingLed's loop is one line whose comment explains itself.
- F117: the inert LangVersion in modules/Directory.Build.props costs nothing while every module sets its own.
- F227: three ABI members without a consumer stay as frozen contract surface (register: unexercised ABI).
- F228: IHost.Log's thread contract is a documentation question for the ABI's owner.
- F232: ModuleKit RecordingHost's unread observation hooks are out-of-tree surface (register).
- F257: the triplicated window-walk P/Invokes cross a file fix/host moved this campaign (N-deadcode-07).
- F259: DiagnosticLog.CurrentPath and the redundant WasNamed condition are two lines in a logger fix/host owns.
- F261: pictureBox1.Tag and the empty designer click handler are designer residue with no behaviour.
- F275: the designer placeholder label1 is a gap in a debug toolbar.
- F277: the second Region dispose is a no-op verified on .NET 10.
- F280: the WM_CLOSE survival assertion is a property the owner chose to pin.
- F281: the duplicate RegisterWindowMessageW P/Invoke is one declaration.
- F303: the HTTP stub comments cite .NET Framework transport behaviour; a comment-only edit in a security self-test the host lane owns.
- F306: the redundant MyData null-checks and the double IsDisposed test are harmless guards.
- F320: four staging helpers duplicated across two hand-run corpus scripts.
- F370: the ProbeBounded comment's kernel-handle claim is wrong and the code is right.
Each is real and each is below the line this campaign drew: no behaviour, no gate, and in five cases a file another
lane moved this week. They stay pinned in BACKLOG.md for the next sweep with this paragraph as their reason.

**Tier B of lane fix/deadcode: what stays and why (2026-09-30, F154, F223, F410, F015, ACCEPTED-RECORDED).**
PetReport.Describe() is asserted by the gate while PetStudioWindow renders its own text (F154): the fix is one
rendering for both, and it lives in a file fix/petstudio rewrote this week, so it waits. WiX 5.0.2 is a literal at
six check sites in three packaging scripts beside the lock that names it (F223): one $script:LockedWixVersion in
WixToolchainPolicy.ps1 is the shape, and Install-LockedWixToolchain.ps1, which carries three of the six, moved under
fix/scripts. tests/runtime-hardening-selftest.ps1 reads nine source files under different names, 21 Get-Content
calls, with two comment strippers (F410): a read-once table is right, and it is a merge hazard while three lanes
append invariants to the file, so the rule meanwhile is that a NEW invariant reads through Remove-LineComments (or
an existing stripped variable) and never slices a raw copy; the poke-sass check's ad hoc stripper is the one to
retire first. agentflow_join.py's rule loop and call decomposition exist twice (F015) in a research script that
grew by 329 lines on master this campaign; recorded for the next pass over that folder.

#### fix/scripts

**The implicit MSBuild inputs are part of every module's freshness watch set, and an SDK bump therefore
costs a seven-module republish (F220, 2026-09-30).** `Directory.Build.props`, `Directory.Build.targets`,
`Directory.Packages.props` (walked up from each project directory to the root) and `global.json` change
the compiled bytes of every module they govern without appearing in any `Include`, so a commit to one of
them left every zip reported current while every zip had been built by the old toolchain or settings.
The trade-off is the one `Test-ModulePublishFreshness.ps1` made the other way for `ProductVersion.props`,
and it is decided the other way here on purpose: a version stamp is the only thing that file changes,
whereas these change what the assembly does. Recorded beside `Get-ModuleWatchSet` in
`packaging/ModuleWatchSet.ps1`. Measured in the scratch clone at the audit commit: a commit touching
`modules/Directory.Build.props` marks all seven zips stale naming that path; the previous watch set
reported all seven current on the same tree.

**`#requires -Version 7` on the zip scripts is a floor, and the publish commit records the exact version
(F213, info, 2026-09-30).** The deflate bytes differ between .NET 8, 9 and 10, so two machines that both
satisfy the floor zip the same payload differently. Correctness is unaffected because the catalog hashes
whatever blob is committed; the header of both zip scripts now says so, and `New-ModulePublish.ps1` puts
`$PSVersionTable.PSVersion` in the publish commit body so a hash churn with no content change is
attributable to the runtime.

**Catalog assets are read through one `git cat-file --batch` child (F208).** Whole verifier
(`Test-ContentCatalogIntegrity.ps1`, 219 assets), fresh interleaved processes, three runs each, worktree
at HEAD, 2026-09-30: Windows PowerShell 5.1 16.0-16.7 s before vs 7.2-7.5 s after; pwsh 7.6.5
12.5-12.7 s vs 3.8-4.0 s. The remaining time is PowerShell start-up, the deliberate 500 ms stall probe
and the catalog parse. The generator's hashes are unchanged: a catalog regenerated in the scratch clone
matched the committed one on all 54 companions, 158 packs and 7 modules.

**The AgentFlow research harness scores the SHIPPED predictor's semantics, and its recall figure carries
the date of the rule files (F018, 2026-09-30).** `agentflow_join.py` used to evaluate an argument-less
non-command call against the bare tool name (fall-through: would-prompt) where the module's
`EvaluateCall` returns Undecidable and never raises; the README's 93% (28/30) recall was that
harness-only predictor's. Decided: the Python follows the C# here, because the C# side is the
deliberate, WITNESS-pinned decision ("a call the rules cannot address is NOT reported as blocked"),
and every count in the harness goes through one `fires()` predicate. The corrected figure recorded in
the README is 25/30 (83%) on the audit corpus (2026-09-29 re-measurement; the three ExitPlanMode
positives are undecidable). A rerun on 2026-09-30 gives 21/30 (70%) with six further would-allow
misses (Edit 2, cd 1, git 2, docker 1) that trace to the CURRENT `settings.json` (changed 01:03 that
day; allow rules rooted at git and cd present), so the README says the table is a function of the
rule files at run time and quotes the shipped-semantics figure as the one that decides shippability.

**`WebFetch(domain:...)` rules are honoured by the Python reference and not yet by the shipped C#
matcher (F017, 2026-09-30).** Claude Code matches such a rule against the request HOST; both copies
compiled `domain:x` as a literal against the URL, so every such rule was inert. The Python has the
documented semantics now, pinned by nine self-test cases; the C# (`modules/AgentFlow/PermissionRules.cs`)
is outside lane fix/scripts and is left for its owner, and the harness prints a note whenever it loads
such a rule so a WebFetch number is read with that caveat. No such rule exists on this box, so nothing
published moved. This is the one place the reference is deliberately ahead of the port; it is a
divergence to close on the C# side, not to undo here.

**Module zips are read through entry streams, not expanded to TEMP (F222).** Whole freshness check
(`Test-ModulePublishFreshness.ps1`), scratch clone at the audit commit where every zip was current, fresh
interleaved processes, three runs each, 2026-09-30: Windows PowerShell 5.1 9.5-9.8 s before vs
2.9-3.1 s after; pwsh 7.6.5 4.6-5.2 s vs 1.6-1.8 s; zero files left under TEMP either way, but the new
form writes none to begin with (about 150 MB per run before). Same counts printed by both versions
(159 mappings, 14 first-party DLLs across 7 zips).

**The WiX bootstrap removes its PackageRoot only after a SUCCESSFUL run, and a cleanup that fails
after a successful install is a warning, not a failed install (F210, 2026-09-30).** The three sibling
scripts that open a scratch the same way delete it in every finally and rethrow a cleanup error when
there was no primary error. `Install-LockedWixToolchain.ps1` differs on both counts on purpose. A
.nupkg that failed its length, digest or signature check is the evidence of what nuget.org served, so
a failed run keeps the root and its warning names the path (the "must be absent" refusal on the next
run says the same). And the product of a successful run, the installed tool, exists whether or not
6 MB of temp could be deleted afterwards, which is what every run left behind before the cleanup
existed, so that is a warning with the path in it. Measured with the real script under 5.1 into
scratch directories: a `schemaVersion` 2 lock keeps the root and warns; a real bootstrap into a private
`-ToolPath` removes it, and `wix --version` still answers from the tool path afterwards.

**The Shimeji behaviour soak mirrors the engine's border situations and re-reads them from the engine's
source before every run (F415, 2026-09-30).** The instrument raised TASKBAR|HORIZONTAL at the floor, the
host option the owner rejected on 2026-09-25 (recorded in BACKLOG.md: it would have made every
`only="horizontal"` edge in every companion eligible at the taskbar), and mapped `horizontal+` to NONE.
Since the converter's `reground` rung put weight-100 `horizontal`->fall edges beside weight-3 `taskbar`
landings, that sent about 94% of converted jump landings into `fall` where the engine lands 50/50, so
the play-share table the emitter's acceptance decisions quote was 30-80% off on every converted pet with
a jump, while the climb and ceiling rates the release notes read moved 0-4 points. Two self-checks now
gate every run: one reads the TOnly enum, the ParseOnlyFlag switch and the screen-border call sites out
of `src/dotNet` and refuses on drift, the other drives `simulate` over two-edge pets and refuses if a
border takes the edge the engine never takes; an unknown `only=` value refuses rather than widening to
"everywhere". Band, re-measured over all 54 pets at 200 runs x 30 minutes (2026-09-30): the 32
converted pets run 344-827 transitions per run (29 within 344-605), so the 2026-09-24 band of 338-597
over 13 pets survives the correction; cartman 533.0 -> 536.0, hornet 520.3 -> 513.7. "Most-entered
animations" figures remembered from earlier runs shift (Walk up about 40%, fall down 50-80%); that is the
correction, not a regression.

**The stand-down probe's walk-mode allocation window stays as it is, and says what it measures (F393,
2026-09-30).** The bytes include the replica's List/ToArray adapter (harness-only) and an empty HashSet
where the shipped scan allocates a populated one, so the figure sits within about 50-65 bytes of the
production per-scan cost and 220-240 bytes above the method body alone. Re-plumbing it to the method
body would drop the HashSet the shipped path really pays for, and no figure from this mode has been
published; the `decide` mode, whose 176-byte figure is published, has no adapter. The comment above the
window and the printed label (`replica window`) carry this.

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

**None open.** BUG-009 to BUG-012 were filed on 2026-09-29 by the full audit and closed by the campaign lanes
(remembrance 1.0.17, blinkingled 1.0.6, aibrain 1.1.14, petstudio 1.1.18; the dispositions are the F168,
F116/F115, F066 and F155 lines in `../BACKLOG.md`). The full post-mortems —
diagnosis, the wrong turns, the fix, and how each was verified — are in
[`ISSUES-post-1.0.0.md`](ISSUES-post-1.0.0.md). Bugs are numbered `BUG-00N` and the number is never
reused, so a commit, a test or a code comment can cite one; `modules/AiBrain/`,
`modules/PetStudio/`, `src/dotNet/`, `docs/RELEASE-CHECKLIST.md` and `handoff.md` all cite them
today. **The next one filed is BUG-013**, and it is filed in [`../BACKLOG.md`](../BACKLOG.md).

| bug | | fixed |
|---|---|---|
| BUG-012 | Companion Studio decoded and tiled the whole sprite sheet on the UI thread on every analyse, against a record that said it did not | petstudio 1.1.18, 2026-09-29 (F155): the analyser adopts the validator's parse and the analysis runs on a pool thread |
| BUG-011 | the Scroll Lock blinker's belief about the LED drifted from the LED: a refused keypress still flipped the flag, and enabling zeroed it against a lit key | blinkingled 1.0.6, 2026-09-29 (F116, F115): the flag moves only with the key; since 2026-09-30 `Start()` adopts the key (N-blinkingled-01) |
| BUG-010 | an unprompted remark was a vision turn while the label said vision was for explicit asks | aibrain 1.1.14, 2026-09-29 (F066): an owner decision, not a code change; the label and comments changed and the module self-test pins the drop's routing |
| BUG-009 | Remembrance stopped recording by waiting on an event posted to the thread doing the waiting: 10 s per source, and a killed exit lost the recording | remembrance 1.0.17, 2026-09-29 (F168): every capture is constructed with no SynchronizationContext current |
| BUG-006 | auto-approve could not see most Codex prompts: the reader anchored on a control Codex only sometimes renders | agentflow 1.4.2, 2026-09-23; verified by pressing the still-open prompt that found it. Its second defect — a missed selector being silent — fixed in 1.4.3 |
| BUG-007 | nine of the fourteen prompt shapes logged as "an unrecognised prompt", including the commonest one | agentflow 1.4.3, 2026-09-23; table re-derived from bundle 2.1.280 |
| BUG-008 | the option table went stale against bundle 2.1.280 and the audit was blind to it, so a refusal blamed the screen capture instead of naming the real reason | agentflow 1.4.4, 2026-09-23 — the only one here found by a gate rather than by the maintainer |
| BUG-001 | the tray icon is missing after an MSI install that launches the app | host 1.1.1 → 1.1.3, across all four of its symptoms |
| BUG-005 | a converted companion stutters: a performance replayed itself, or held two frames for ~11s | 2026-09-22, both halves; one root cause with three sites, one of them a test |
| BUG-002 | the vision feature does nothing, silently, when the configured model is not installed | 2026-09-10, all four items |
| BUG-003 | screen capture returns the wallpaper, and follows the wrong monitor | (b) fixed 2026-09-10; (a) resolved — the suspected cause was refuted by measurement |
| BUG-004 | the leak soak's verdict was a coin flip, and it is the only gate that can catch a leak | 2026-09-10 — there is no leak, and the gate now measures that |

Three of the first four were found by the maintainer running a real install, and none by a gate; of
the eight since, BUG-008 is the one a gate found and BUG-009 to BUG-012 came from reading the code.
That is why [`../SMOKETEST.md`](../SMOKETEST.md) exists, and why its walk log records each completed
pass against the build it was walked on (the A-E walk used to be tracked as open work in
[`../BACKLOG.md`](../BACKLOG.md); the log replaced that on 2026-09-18).

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
