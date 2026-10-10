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
pane they had left, which is the same surprise from the other side. Since 2026-09-30 (RA-329, RA-330, lane
burn/host-shell) the window answers only the VIEW that asked, through a per-build generation: the pane
object is shared by every view of that pane, so a continuation from a torn-down view of the same pane (the
user left and came back, or a faster action rebuilt it) still passed the identity test and rebuilt over the
fresh view, its edits gone and the stale stash shown in their place.

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
names the hotkey so the refusal is not a surprise. *Superseded 2026-10-07 for a companion in view (AI Brain 1.4.0,
`#### feature/fullscreen-per-monitor`): on two monitors the companion is not hidden, so the refusal is also said to
it; a hidden one is still told nothing, as decided here.*

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
to them. The contribution lists (TrayItems, OptionsPanes, the responder lists) stay live: the module appends
to them from the raise the test itself made, on the test's thread. (`PlayedSounds`, `StoppedSoundOwners` and
`PlayedAnimations` were listed here as live too; Reminder's F188 chime check appends `PlayedSounds` from a pool
thread, so lane burn/host-shell moved the first two into the locked, snapshot set on 2026-10-01, RA-215, and
`PlayedAnimations` the same day, N-reminder-05, so the fake has one rule for everything a module can append;
AgentFlow's three `PlayedAnimations.Clear()` resets became `ClearPlayedAnimations()`.)

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

#### burn/reminder

**The custom-chime single-flight guard is keyed on the file, not on the process (2026-09-30, R-045).** F188's
guard was one bit for every custom chime, so while one read sat on a dead share's SMB timeout every other
custom chime, for any file and any reminder, was dropped with no fall-back to the built-in chime. Two repairs
were on the table. Falling back to the default while the bit was held was rejected: the burst F188 was written
for, several reminders in one tick sharing one file, would then sound one custom chime plus N-1 defaults.
Chosen: a set of in-flight paths (trimmed, case-folded) under one lock, so the same file in a burst is still
one read and a chime for another file runs its own; the set is bounded by the distinct chimes configured, one
per slot plus the personal one.

**"Test this reminder" plays the chime with no companion on screen, and withholds only the bubble and the
reaction (2026-09-30, RA-176).** N-reminder-03 applied the F199 hold to the whole button. The hold exists
because `SayAll` drops its line with no pet out; `PlaySound` reaches the shared audio output regardless, and
the button is the only way to audition a chime file, which a tray-only user still configures. So the chime
plays first and the status says so (`NoCompanionChimedStatus`); with the slot's chime off the old answer
stands. The tick's hold in `CheckDue` is unchanged: a due reminder's chime is still held with its bubble, so
it is not chimed into nothing and chimed again when a pet appears.

**A snapshot with no per-slot health prunes nothing (2026-09-30, RA-177).** The 1.0.3 whole-snapshot rule
(prune every stale id when the combined Error is empty) survived F204 as the `SlotHealthy == null` branch of
`PruneFiredAgainstFeed`, and its only callers were six self-test snapshots: production fetches through
`AggregateCalendarSource`, which always fills `SlotHealthy`. Shown by mutation before the change: a guard
that stopped the per-slot prune on an empty feed passed the whole suite. The F204 rule for an unvouched id
is to keep it, and a snapshot that names no slot can vouch for nothing, so the branch now returns false, the
six checks run through a one-slot aggregate, and a seventh pins the null-health default. A snapshot without
per-slot health is a test double's or a future source's; if a future source needs pruning it fills
`SlotHealthy`, the way the aggregate does.

**A failed settings write outside Apply is retried by the tick, and the fake's disk is what the self-test
reads (2026-09-30, RA-178).** The module holds one `IModuleSettings` handle for its life, so under the shipped
host a failed `Save()` leaves every unsaved value in the handle's dictionary and one later `Save` persists
them all; `_savePending` makes `CheckDue` call `SaveFired` on the next tick for exactly that. ModuleKit's
`FakeModuleSettings` shows the disk after a failed save instead (N-blinkingled-02, recorded under
`#### fix/followups`), which the self-test works with rather than against: it seeds and saves its values
first and asserts on `Get` after the retry succeeds. The alternative, a retry timer of its own, was rejected:
the 20 s tick already runs, and a small JSON write per tick while the file is held is the cheapest recovery
there is.

**Held reminders burst when a companion returns, and the burst is accepted (2026-09-30, RA-173).** Under the
F199 hold every stale once-only personal reminder and the day's briefing fire together on the first tick a pet
appears (calendar reminders only inside their one-minute grace, so they do not join it). Capping the age of a
held once-only reminder, disabling one older than some hours with a log line, was considered and declined: a
held reminder is one the user typed and never saw, and disabling it unseen is the F199 defect by another
route. The burst is bounded by what the user asked for, chimes at most once per distinct configured file, and
the personal list card shows what fired. The finding's other observation stands as an observation: a future
Voice responder would be held with the bubble, because the hold asks whether a companion is on screen, not
whether anyone could voice the line; no Voice responder ships, and the question is the host's to answer when
one does.

**A reaction that no on-screen pet can play stays silent, and the module keeps `PlayAnimationAll`
(2026-09-30, RA-174).** `IHost.PlayAnimationAll` returns void, so the module cannot tell a miss from a play.
Detecting it module-side, by iterating `TryPlayAnimation` over `_seenPets`, was rejected: that list misses
pets a skin reload respawned without `CompanionSpawned` (R-047), so it would report misses that were plays,
and it moves per-pet candidate selection out of the host, which owns it for AiBrain and AgentFlow too. The
1.0.6 measurement (43 of 54 companions define a default candidate) and the comment at `React()` remain the
record of the accepted miss. The additive ABI change, `PlayAnimationAll` returning the number of pets that
played, is the host's, and is noted for it in the lane's report.

**The end-of-campaign republish is one step and it is the coordinator's (2026-09-30, R-046).** Every module
this campaign touched is still published at its pre-campaign version, so `Test-ModulePublishFreshness` is red
by design on every branch until the coordinator republishes the zips and both catalogs once at the end;
`modules-dist/` and `catalog.json` are out of bounds for every lane (LANE-RULES). The gate staying red until
then is the check working: it says the deployed artefact does not yet carry the fixes the record marks FIXED.

**Module self-tests stay in the module class, and ReminderModule.cs carries its own (2026-09-30, R-048).**
At `8eea13a` the file is 2,559 lines, of which the self-test, its helpers and eight nested doubles are lines
1526 to 2558 (measured with `wc -l` and the section markers); this lane's work moves both numbers up. The test
lives in the class because it reaches private state (`_source`, `_fired`, `_lastSnapshot`, `_slotSources`,
`CheckDue`, `CheckNowAsync`), and `--module-selftest` reflects on the module TYPE, so a `partial class` split
into a `ReminderModule.SelfTest.cs` (a name that must not end in `Module.cs`, which the freshness gate reserves
for the one version-bearing file) would keep both properties. It is not done here because the layout is the
convention every module uses (RA-162 records Remembrance's twin); splitting one module in a burn-down lane
would leave the repo with two conventions, and the split is mechanical enough to do for all of them at once
when the owner wants it.

**`RecordingHost.PlayedSounds` is appended from a pool thread since F188, so the N-remembrance-01 premise for
leaving it live no longer holds (2026-09-30, RA-179).** That entry (under `#### fix/followups`) kept
`PlayedAnimations`, `PlayedSounds` and the contribution lists live because "the module appends to them from the
raise the test itself made, on the test's thread". Reminder's `Chime.PlayCustom` calls `host.PlaySound` from
`Task.Run`, so a Count-only spin followed by an indexer read could observe `List<T>.Add`'s count before its
element and NRE, turning a FAIL into an EXC. The in-boundary half is done: the self-test reads an element only
once it is non-null and releases its two gated reads one at a time, so two pool threads never `Add`
concurrently. The ModuleKit half, `PlayedSounds` appended under `_recordSync` and handed out as a snapshot like
`SaidLines`, is outside this lane's boundary and is described in its report.

**Webex join links are matched by shape, and a shape not listed is a miss rather than a wrong link
(2026-09-30, RA-171).** The provider accepted any path on any host ending in `webex.com`, so a help article
ahead of the join link was what the tray opened. It now accepts the classic and event links (`/<site>/j.php?`,
`/<site>/e.php?`, the older `/<site>/onstage/g.php?`), a Personal Room (`/meet/`, `/join/`) and the Webex App's
own two (`/webappng/sites/`, `/wbxmjs/joinservice/`), with the host anchored to a label boundary (Zoom's is
anchored the same way). Precision over recall, deliberately: a join shape this list lacks costs the Join hint
and the tray row while the bubble still announces, where a wrong match sends the user to a help page at the
moment the meeting starts.
#### burn/blinkingled

**Switching the blinker on while Caps Lock is already on is refused, said and persisted, instead of started and
stopped in silence one dark gap later (2026-09-30, RA-088).** The tick's silence is written for a user who has just
pressed Caps Lock; it does not fit a user whose gesture was the enable, who heard "Keeping the lights on for you"
and then found the feature off with nothing said. Three shapes were on the table. Refusing while leaving
`enabled=true` was rejected: the tray and the pane would read "on" with nothing running and nothing that would ever
start it, since no timer runs while stopped and only the next settings change reaches `ApplyState`; that is the
disk/live drift the module's "disk wins" rule exists to avoid. Starting anyway and having the Caps Lock stop speak
once was rejected as two bubbles for one gesture. Chosen: no `Start()`, the off persisted through the same
Save()-checked path `OnCapsLockStop` uses (one log line either way), and one Caps-Lock-specific line in place of the
ON line. The refusal applies to the enable TRANSITION on a user gesture only: at startup (`ApplyState(false)`)
nothing was said, so there is nothing to correct, and the first tick stops it as before; the self-test pins that
gate. Caps Lock is read through a new `CapsLockReader` seam on the engine, the same shape as `ScrollLockReader`, and
`CapsLockStopsNow()` is the one predicate the tick and the enable path share.

**The cadence tick re-syncs its belief from the key before toggling (RA-094 (b), N-burn-blinkingled-02; declined by
this lane on 2026-09-30, taken by the coordinator on 2026-10-01).** The 09-30 reasoning, kept for the record: a
`ScrollLockReader()` read at every tick makes the whole cadence depend on `GetKeyState` from a background thread,
which is the open staleness question the F113 entry above records for the two reads the engine already makes at
`Start()` and `Stop()`. Those two run on a user gesture, when the process has just been foreground; a tick runs in
the background every few seconds for a whole session, and if the read is stale there, re-syncing from it every tick
turns one manual press into a cadence that toggles every `_onMs` and a `Stop()` that reads a dark key as lit. Not
measured on this box then or now: measuring means synthesizing a toggle-key press on the owner's keyboard and
reading it back from a background thread. The 10-01 decision: the manual press the finding describes was the
observed defect (belief and key inverted for the rest of the run, the LED left lit by the next `Stop()`), while the
stale read is a hypothesis, so the fix at the root is taken and the hypothesis stays named here as the risk to
re-check if the LED ever blinks on a `_onMs` beat. `Tick()` reads the key first (`keyLit`), sets the belief to it,
and then flips the belief on an accepted toggle alone, so the F116 rule stands and a refused tick leaves the belief
at the key's state; a reader that throws keeps the belief, as in `Start()`. The belief gate in `Stop()` (`_phaseOn`
AND the read) is untouched. Pinned in the module self-test with a fake key driver (a variable the fake press flips
and the fake reader reads): the manual press mid-cadence is re-synced on the next tick and `Stop()` then leaves the
key dark, with WITNESS checks that an undisturbed cadence still alternates lit, dark, lit, and that a refused tick
after the press re-syncs without flipping.

**`Shutdown()` clears the light only if this session ever pressed the key; the engine's `Stop()` keeps variant C
in full (2026-09-30, RA-090; a module-level rule for the owner to keep or overturn).** Since `Start()` adopts the
key (cf27664), every headless host that Inits blinkingled and shuts it down with no message loop (the convention
runner under `--module-selftest=blinkingled`, the three bundled-root loads under `--module-host-selftest`) adopted a
lit Scroll Lock and pressed it off on the way out, once per gate run, outside the suite's parity window; F113's
"leaves it OFF from OFF" could not see the ON side. Measured on this box on 2026-09-30 from a fresh
`powershell.exe` process (a new message queue, so not the stale in-process read): Num Lock ON throughout; Scroll
Lock OFF through the morning's runs and ON from 15:33, the state the lane's brief described, so from then on every
gate run here would have pressed it off. Three module-side shapes were weighed. Not starting when `GetSettings`
returned null covers the convention host alone and turns a third-party host's degraded path from always-on to
never-on. Deferring `Start()` to the first message-loop pass hides `Start()` from the very Init the convention
runner exists to exercise. Chosen: `Shutdown()` runs the corrective toggle only when `AttemptCount > 0`, that is,
when a tick, a "Blink once now" or a corrective toggle ever pressed the key in this session; a light that was only
adopted is exactly as the user had it, so there is nothing of ours to leave off. It covers all four headless hosts.
In the shipped app the difference is one lit interval wide: an exit within `_onMs` (0.5 s on Hyper, 4 s on
Glacial) of starting over a key the user had lit leaves that key lit, where variant C's `Stop()` switched it off;
after the first tick nothing changes, and a user's Off after enable still clears an adopted key through the
engine's `Stop()`, whose pinned "...and Stop() then clears it" stands. Pinned in the module self-test on a second
instance driven through the seams (adopted lit, never pressed: Shutdown presses nothing; WITNESS a light the
session drove is cleared with one more press). Because the F113 mutation case deliberately makes one unpaired
real press, and no later headless Init now clears it, the suite puts its own key back by count (an odd number of
ACCEPTED presses on its instance is followed by one more, after the FAIL is recorded); count-based, so not the
read-and-restore the F113 entry rejected. The runner-side read-before-and-after the finding asked for
(tests/Test-ModuleSelfTests.ps1, tests/Invoke-SelfTests.ps1) is outside this lane's boundary and is now
observability rather than the fix. One press survives, by construction: the mutation case that reinstates the
shipped `Shutdown()` makes the convention runner's own instance press a lit Scroll Lock off, once per harness
run, and nothing turns it back on (measured 2026-10-01: a whole `tests/mutate-selftest-guards.py` run that
started with the key ON ended with it OFF; the gate of 2026-09-30, which runs the unmutated module, read OFF
before and OFF after). The parity check also requires this instance's ACCEPTED toggles to be even and
says it speaks for this instance; the comment at the Init pin says what the pin does (Init adopts before it lands;
the pin makes every later `Stop()` drop the belief without pressing).

**The info items of lane burn/blinkingled were fixed where the fix was a comment, and are recorded here
(2026-09-30, RA-092, RA-093, ACCEPTED-RECORDED).**
- RA-092: the SelfTest summary describes the finder the host ships (the module type's own public static
  `SelfTest(out string)` first, an ambiguity reported rather than a first match taken), the "Helpers are named
  SelfCheck" sentence is gone (no helper in the file was), and the `out int` note on `AcceptedKeypress` gives the
  real reason, `KeypressDelivery`'s shape.
- RA-093: `Stop()`'s summary sits above `Stop()` again, with a one-line note of where it had been, and the 1.0.1
  changelog entry starts on its own line. The record's claim that a doc-file build would warn is dropped, as the
  finding itself asked.
- RA-089's BlinkingLed half is the same comment; the other five module comments and `handoff.md:1186` are
  outside this lane's boundary and are listed in its report.
#### burn/scripts-tests

**The window soak was RUN, not only built, and it could not pass at the merge (RA-341, 2026-09-30).** The
fix/gates entry above records F387 and F388 as fixed in the soak's source and built, not run, because the
coordinator owns `tests/module-window-soak.ps1`. Two lanes then changed the two sides of the soak's reflection
contract: fix/petstudio made `Analyze()` asynchronous and fix/gates made the soak read the node map right after
the call. Run directly at 8eea13a the exe printed the F387 refusal on cycle 0 and died on the module's
cross-thread render (exit 0xE0434352). The lane ran the exe itself because it opens no companion (its windows
sit at -32000) and the brief allowed exactly that; the wrapper script stays the coordinator's. The measured
floor the readable-counters check relies on is gdi 19 / user 45 after each segment with every window closed,
and the gate now compiles the soak and the stand-down probe (RA-340) so the compile half of that contract is
checked on every run.

**The invariants script's aliases stay until the read-once table lands, and that table's shape gained two
measured items (RA-361, F410, 2026-09-30, ACCEPTED-RECORDED).** F410 records the read-once raw/stripped table
keyed by relative path as the right shape and defers it while lanes append to the file. The re-audit measured
two variable reuses that belong on the same list: `$setIconBody` is assigned twice from different slicers (once
Remove-LineComments applied AFTER slicing the raw `$processIconSource` on a prefix, once sliced from the
pre-stripped `$processIconCodeHost` on the full signature), and `$fetchIndex` serves two subjects (the pets-pane
fetch and the app-update fetch). A single member-boundary stop list for Get-MethodBody is part of the same
refactor. Meanwhile the standing rule held: this lane retired the poke-sass ad hoc stripper and the two
raw-source order and presence checks the re-audit found (RA-362), each now read through Remove-LineComments.

**The seven consumer-less usings in `src/Portable/AppSettingsStore.cs` are the settings owner's to remove
(RA-336, 2026-09-30, ACCEPTED-RECORDED).** Lines 3, 4, 7, 8, 9, 10 and 15 (System.ComponentModel,
System.Diagnostics, System.Runtime.InteropServices, System.Security.AccessControl, System.Security.Cryptography,
System.Security.Principal, System.Threading) import nothing the file reads since F358 moved CrossSessionLock and
AtomicFile into ModuleKit; an unused using is not a compiler warning, so TreatWarningsAsErrors is indifferent and
nothing behaves differently. The file is outside lane burn/scripts-tests's boundary (`tests/**`), so the lane
corrected the two CoreTests.csproj comments that cited the same vanished APIs, after measuring what the project
needs the settings for (the harness compiles with UseWindowsForms removed, so that is reference-set parity with
the app; with the SupportedOSPlatform attribute removed, CA1416 fires on every call into ModuleKit.dll from
AppSettingsStore.cs and ModuleKitTests.cs), and left the usings to the owner of src/Portable.
#### burn/host-shell

**A redirect over a pane with a download in flight asks, on the terms a redirect over unsaved edits does
(2026-09-30, RA-328).** The F368 rule read the Apply button, and a custom pane has none, so the module-update
balloon could land on Modules over a Companions download and cancel it with the pane, its status line torn
down before it could say so. The two panes now report a downloads-in-flight count through the host-only
`IBusyPane`, `OptionsWindow.ShowPane(title)` refuses on `IsDirty || IsCurrentPaneBusy`, and the question names
the download ("Stop it and open <pane>?"). Letting the download finish and then redirecting was declined: the
redirect is the user's click, and a window that moves on its own seconds later is the F375 surprise from the
other side.

**A module marker write that fails is an error the pane reports; the launch path logs it (2026-09-30, RA-296,
RA-318).** `MarkForRemoval`, `Unmark` and `MarkForUpdate` swallowed a failed write of pending-module-removals.txt
or pending-module-updates.txt, so a marker held open by a sync client let the Modules pane announce the
uninstall or update, prompt for the restart, and lose the user's one action behind a success message; the
pane's "Couldn't uninstall/update" handlers existed and could never fire. The writes throw now and those
handlers report. The two `ProcessPending`s keep catching their launch-time rewrites and log instead, because a
launch has nobody to tell and the failure is benign there (finished ids are retried against folders that are
already gone). The install path is the one asymmetry: its `Unmark` runs after the module is in place, so a
failure there is appended to "installed" as a warning with the recovery step rather than reported as a failed
install. An unreadable update marker is a third state, not an empty one (RA-297): that launch swaps and sweeps
nothing.

**The Modules pane lists new modules on open, as the Companions pane lists new pets (2026-09-30, RA-317).** The
catalog was already fetched on open; only the update buttons were rendered from it, so a lean host's first visit
read "No modules installed yet." with nothing to install until the button was found. The header now says the pane
lists both on open and that the button re-checks now. The alternative, keeping the install list behind the
explicit press, was declined: the pane's own summary says it "is how a lean host ever gets any", and the
Companions pane settled the same question the other way in F286.

**A late speech bubble is stale per host, not per pet (2026-09-30, RA-278, ACCEPTED-RECORDED).**
`SpeechRequest.ShowBubble` draws nothing once any later utterance has been offered, whichever pet it was for:
`_speechGeneration` is one counter on the host. A voice module speaking a claimed line for pet A therefore
loses its bubble if pet B is offered a line during the synthesis. Left as it is, and written into the ABI
comment, because no shipped speech responder exists to observe it (F333 recorded the same), a per-target
counter needs FormCompanion-keyed state no headless check can drive, and the failure is the conservative
one: an old line never lands on screen after a newer one. Revisit with the first shipped voice module.

**`IHost.HostVersion` is live host surface with no module reader, by design (2026-09-30, RA-205).** The
loader's MinHostVersion gate reads it through the interface so a test double can inject a version; every
host-side fake sets it and ModuleKit's RecordingHost defaults it to a high sentinel. A sweep that counts
module callers finds none. It belongs beside the unexercised speech and audio surface above as a member
kept for the host's own use, not for out-of-tree modules.

**The Animation permission is declarative, and the pane says so where the set is shown (2026-09-30, the
"displayed as a control and gates nothing" item filed 2026-09-28).** The owner's decision is recorded above
(ModulePermissions.Animation stays declarative). What this lane added: the flag's comment in PluginApi.cs
states why it cannot be enforced (TryPlayAnimation and PlayAnimationAll carry no caller identity), and the
Modules pane's "wants:" line carries a tooltip naming the four flags the host enforces on its own verbs
(Audio, Network, Voice, Companions) and calling the rest, Animation included, statements the module makes.
The consent prompt for a widened set already said "Nothing in the app enforces these". A gating overload
taking a moduleId was declined with the owner's decision: the contract is frozen, and a member that gates
one caller while the old one gates none would be the "looks like it works" shape the freeze removed.

**The eager `OpenExeConfiguration` candidate is deleted, and the cost it carried was measured, not believed
(2026-09-30, N-deadcode-06, RA-309).** `LocalData.BuildLegacyCandidates` called
`ConfigurationManager.OpenExeConfiguration(PerUserRoamingAndLocal).FilePath` at every construction, for a
migration that runs only when settings.json and its backup are both unreadable, and the path it produced
(`<LocalAppData>\bigfnj\DesktopAICompanion_Url_<hash>\<version>\user.config`) is this process's own per-version
user.config, which no build ever wrote (nothing in the repository's history calls
`ApplicationSettingsBase.Save`) and which can never name the DesktopPet-era file the migration reads. Measured
cold, 2026-09-30, in fresh interleaved processes against a variant with the call removed (the probe is
`temp/burn-host-shell/cfgprobe`, one binary, variant by argument, each process timing only the call), 10
rounds each: on an idle box just after a reboot, median 36 ms with the call (31 to 41 ms) against 0.001 ms
without; with another checkout's gate running, median 226 ms (100 to 960 ms) against 0.002 ms. The audit's
"~40-50 ms" was right for an idle launch and low by 5x for a contended one, which is the launch a user with
an antivirus scan or a sync client gets. The call is gone rather than made lazy: a candidate that can never
match is not worth loading System.Configuration for on any path.

**The three 12 MiB caps stay one number until the pet payload leaves settings.json (2026-09-30, RA-302,
ACCEPTED-RECORDED).** `AppSettingsDocument.MaximumXmlBytes`, `CompanionXmlValidator.MaximumXmlBytes` and
`AppSettingsStore.MaximumSettingsFileBytes` are all 12 MiB, so a validator-accepted pet within a few hundred
KB of the cap stages, activates, and then cannot be persisted, surfacing as "Couldn't apply companion". The
largest catalogued pet is 9.72 MiB (shimeji-3g8t9v4e), 2.3 MiB under. Headroom on the file cap alone would move
the failure to the next larger pet; lowering the validator's cap would refuse pets the runtime handles. The
fix is the schema change F361 recorded for a host release of its own (the active pet's XML into a sibling file
keyed by id and hash), after which the settings file's size stops depending on the pet's at all.

**The host-side fakes' uncalled RaiseFullscreen copies and unpopulated PickedFiles stay with the F340 decision
(2026-09-30, RA-294, ACCEPTED-RECORDED).** Five of the six RaiseFullscreen bodies are never called and are the
CS0067 suppressors F350 exempted (deleting one fires CS0067 under warnings-as-errors in ModuleHostSelfTest,
PetStudioModuleSelfTest, FortunesModuleSelfTest and FortunesEngineSelfTest); three PickedFiles lists (not two:
ModuleHostSelfTest, FortunesModuleSelfTest, FortunesEngineSelfTest) are declared and returned but never
populated. Both are members of the six-way fake duplication F340 defers to a shared HeadlessHost base, and a
shared base removes the copies whole where trimming members in one fake leaves five siblings to drift.

**Cached companion icons keep their compressed source bytes for the session (2026-09-30, RA-316,
ACCEPTED-RECORDED).** `FromPng` decodes through `BitmapImage.StreamSource` over the PNG bytes, and WPF keeps that
stream reachable from the frozen image, so `_iconCache` holds up to 256 KB per bundled thumbnail and up to
512 KB per library header icon beside the decoded bitmap, per pet whose card was built, until the process
ends. Not fixed: nothing has measured it to matter (a library of forty pets is at most ~20 MB, and only after a
visit to the pane), wrapping in `CachedBitmap` would keep the source reachable anyway, and copying the decoded
pixels into a `WriteableBitmap` is a refactor of a cache introduced for a different cost (the per-card
re-parse, 2026-09-27). Recorded so the next pass starts from a measurement, not the idea.

**`IHost.PlayAnimationAll` keeps returning void (2026-10-01, N-reminder-06, ACCEPTED-RECORDED, coordinator
decision 35).** Changing its return type is binary-breaking for every module compiled against the void
signature, and an additive count member would ship this round with no consumer: Reminder, the one module that
wants it (RA-174), could adopt it only with a MinHostVersion raise to 1.2.7, which cannot be published before
host 1.2.7 exists. So it stays as it is. The shape to use when a module needs it, under Contracts' LangVersion
7.3 (no default interface members): a NEW capability interface beside IHost, `IAnimationPlayCount` with
`int PlayAnimationAllCounted(IReadOnlyList<string> animationCandidates)` answering the number of pets that
played (`StartUp.PlayAnimationOnAll` already has the per-pet bool), implemented by CompanionHost and
RecordingHost. A module asks `host as IAnimationPlayCount` and falls back to the void verb when the cast fails,
so a module compiled against the new Contracts still loads on an older host and raises MinHostVersion only if
it refuses to run without the count. Adding the member to IHost itself was rejected for the same reason the
return type was: any IHost implementation compiled against the old interface, ModuleKit's RecordingHost in
every published payload among them, fails to load with a TypeLoadException once the interface grows.
#### burn/petstudio

**A rejected re-parse keeps the last accepted graph, and the timeline drops nothing without a graph
(2026-09-30, RA-145).** `RenderMap` ran for every report and `Resync` dropped every step whose id the report's
node list lacked; a rejected text (an unclosed tag during the 750 ms typing pause, Open of a refused file, a
refused import) has an EMPTY node list, so one typo emptied the author's whole chain, with no undo, while the
F165 status said the companion did not have those animations. Two guards now stand, each enough on its own and
both asserted: `RenderMap` clears and rebuilds `_nodesById` and `_capabilities` only for a valid report (the map
is still emptied and the report pane says why; the timeline keeps the colours of the last accepted graph), and
`Resync(report.IsValid)` hands the validity through to `BehaviourChain.StepsToDrop`, the pure rule the module
self-test pins (no graph, nothing dropped; a known graph drops exactly what is gone). Considered and declined: a
document-level undo for the timeline, which would be a new feature standing in for a guard.

**A descent reads FALL, whatever its gravity node says and whether or not it drifts sideways (2026-09-30,
N-petstudio-02).** The rule is the mirror of JUMP's: a rise at either end is a jump before the horizontal travel
is read, and now a drop at either end is a fall before it, so MOVE means what its sentence says, travel along the
ground. Gravity is not consulted, because the three converted `fall_` animations (a source Fall that collided
with the emitted `fall`) carry a gravity node and drop 10 px per frame, and "Plays in place" was as wrong for
them as for the coloured sheep's gravity-less `fall fast`. Measured before shipping, the way F150 was, but on the
shipped code rather than a replica: a scratch console in the lane's TEMP drives the built `PetStudio.dll` by
reflection (`PetAnalyzer.Analyze` then `AnimCapabilities.ClassifyAll`) over the 54 `Companions/*/animations.xml`
and `src/Resources/animations.xml`, before and after. 3,643 animations; 206 labels change, in 35 of 55 pets; 203 of
them to FALL (140 from Idle, 63 from MOVE), and every one of the 203 descends at an end and rises at neither.
Per pet: the seven coloured sheep change 20 each (`fall fast`, `fall_die`, `chasebend`, `king_spawn`,
`king_fall_spawn`, `king_fall`, `king_fall_ded`, `bath_startB`, `jump_down_fail1/2`, `bloom_2..6_fall`,
`spawn_ship2` from Idle; `divea`, `diveb`, `king_jump_down`, `king_jumpB_down` from MOVE); the bundled sheep and
esheep64 6 each (`fall fast`, `jump_down2`, `fall_winb`, `fall_winc` from Idle; `batha`, `bathb`, the dive into the
bath, from MOVE); fox, mimiko, neko, pink_fox, pink_neko and yellow_neko 4 each (`fall_fast`, `fall_fastest`
from Idle; the diagonal `run_dl`, `run_dr` from MOVE, as their `run_ul`, `run_ur` already read JUMP); ten
converted shimeji their diagonal `jump_down` (MOVE to FALL) and three their `fall_` (Idle to FALL); negima
`fall_akira`, pingus `child1a` and `fall2b`, ssj-goku `Flying_Down`, `Invisible_Fall` and `fall_fast`. One
caveat the owner should know: blue_ham_ham, mareep, pikachu and shiny_sylveon float in eight directions, and
their `Walk Down Left/Right` now read FALL as their `Walk Up Left/Right` have read JUMP since the badges
existed; the vocabulary assumes a gravity-bound pet, and this change did not widen that assumption. The
remaining 20 pets (bbunny and 19 shimeji) are unchanged.

**ENGINE is the runtime's binding of fall/drag/kill/sync, read off the analyzer's staged Animations, not a
name match in the module (2026-09-30, RA-128, RA-139).** `IsEngineOwned` compared every name to the reserved
array with `OrdinalIgnoreCase`, which disagreed with the host both ways: `Xml.LoadAnimations` binds the exact
name only (last duplicate wins) and `ResolveMagicAnimations` then falls back for fall and drag alone. The audit's
fix, an `Ordinal` match, was declined: it fixes 'Kill' and 'Sync' (never bound, so ordinary animations) and a
'Fall' beside an exact `fall`, and breaks a lone 'Falling', which the runtime DOES bind as the fall. Copying the
fallback into the module was declined for the reason F155 recorded. `BuildNodes` already had the runtime's four
ids in hand for the roots; `AnimNode.IsEngineEntry` now carries them, and the ENGINE badge, `Holdable` and the
self-checks read that flag. Over the same corpus this moves three labels, all consistent with what the engine
runs: negima's `fall_asuna` and ssj-goku's `fall_short` are each pet's only fall-like name and the host binds
them as its fall (Idle to ENGINE, a correction); pingus declares no fall or drag at all, so the host binds its
lowest id, `walk`, as both, and `walk` reads ENGINE (MOVE to ENGINE). The last is the host's real behaviour,
and the badge saying so tells the author something true about their pet; the ENGINE sentence no longer claims
"not by choice", since the companion chooses `walk` too. The three self-check literals of the four names iterate
`PetGraph.ReservedEntryPointNames`, with one literal WITNESS pinning the array's contents (RA-139); the CLI's
`IsMagicName` in `tools/ShimejiConvert/Program.cs` is outside this lane's boundary and is left for its owner.

**Run chain builds its pet on a pool thread behind the single-flight gate, and a closed window refuses what
lands after it (2026-09-30, RA-133, RA-140).** `BuildDebugXml` is two validating parses (each an XSD compile
and a full GDI+ decode of the sheet) and a whole-document serialize, run in the click handler until now.
Rule-6 shape: `Interlocked.CompareExchange` gate, snapshots of the steps and checkboxes on the dispatcher,
`Task.Run`, the continuation on the dispatcher. No generation counter, because nothing overtakes a build; the
one stale result is a build landing in a closed window, and that is `_closed`, set first in the Closed handler
before the generation bump and the rerun clear, and tested by `BeginAnalyze`, `LoadConvertedIntoEditor` and
`RunDebugPet`. No timing is claimed: nothing was measured cold in interleaved processes; the property is that
the dispatcher parses, serializes and decodes nothing for a Run click.

**The strip counts plays, in the compiler's unit (2026-09-30, RA-148).** `MaxChainNodes` bounds the flattened
plays (one clone each) while `Insert` counted chips and `Bump` clamped one chip to `MaxRepeatPerStep` alone, so
three chips at x32 built on screen and were refused at Run as "96 steps". `Plays`, `CanAddStep` and
`MaxRepeatFor` are `BehaviourChain`'s and pinned at the edge (64 builds, 65 is refused in plays); the strip
refuses and clamps through them and says so when a click did less than it asked.

**Recorded, not worked (2026-09-30, ACCEPTED-RECORDED).**
- RA-143: in production `FindBundleRoot` skips a denied folder through `IgnoreInaccessible = true` on the
  enumerator, not through the F164 catch, which the injected lister and non-access faults reach (the verifier
  measured it on .NET 10.0.11); the `FindBundleRoot` doc now says so. No invariant pins the option: both routes
  give the same answer, and a pin on a redundancy is a gate line that guards nothing.
- RA-144: `BeginAnalyze`'s comment cited thread affinity for keeping the sheet decode on the dispatcher; only
  the once-per-sheet argument holds (`TryDecode` freezes both results), and the comment now gives that reason
  alone and says the decode could move. It stays where it is: one stall per sheet change, unmeasured.
- RA-146: `RenderFrames` scans every frame for blankness after `allBlank` is already false and `IsBlank`
  re-crops a tile the loop holds: N-1 scans and N extra `CroppedBitmap`s on the first selection of an N-frame
  animation over an uncached sheet, per selection, never per tick. Tiles are small; left for a pass that
  measures the studio.
- R-035: the F153 WITNESS literal (walk #1 to vertical_walk_up #37, only="vertical") runs on the embedded
  fixture and, through `--petstudio-selftest`, on the host's bundled `animations.xml`, so the gate pins the two
  as one graph. That pin is kept on purpose (nothing else notices the fixture drifting from the bundled pet);
  the label now says what it pins and what to refresh. A host-side parity check in
  `src/dotNet/Plugins/PetStudioModuleSelfTest.cs` would be the complete answer and is outside this boundary.
- RA-134: the `ReportFailure` wiring has one home, `PetStudioModule.SelfTest`; the chain check's copy, placed
  there when no module SelfTest existed, is gone, and the harness case that graded it through
  `--petstudio-selftest` is re-pointed at the module self-test.

**Observation filed as N-burn-petstudio-01 (2026-09-30).** In the same corpus pass, 131 gravity-less, purely
vertical DESCENTS read CLIMB: blue_sheep's `jump_down2` (14 to 18 px per frame, seeded at a wall by `chasew3`
only="vertical" and reached from the `hang` turn), `fall_winc` (seeded by `jump` only="vertical" and
`wall_slide` only="horizontal"), `fall_face`, `chasew4`. The F150 rule admits any travel purely along the wall's
axis whatever its speed, so a 2 px wall walk and a 16 px drop beside the wall read the same; a speed threshold
would be a new heuristic with no engine fact behind it, so this is filed for the owner rather than changed.

**A purely vertical descent beside a wall keeps reading CLIMB: no speed separates it from a wall walk without
un-badging the climbs (2026-10-01, N-burn-petstudio-01, DECLINED-MEASURED).** Measured on the built DLL with the
lane's corpus driver over the 55 shipped pets. 131 gravity-less, purely vertical descents read CLIMB, in 33 pets
(105 of them in the seven coloured sheep, 15 each); their peak speeds: 1 px per frame 7, 2 px 16, 6 px 24, 10 px
28, 14 px 28, 16 px 7, 18 px 14, 20 px 7, so 84 of 131 exceed 8 px. The 111 purely vertical ASCENTS the same rule
labels CLIMB span 2 to 30 px per frame: 2 px 16, 6 px 39, 10 px 42, 20 px 7, 30 px 7, so 95 of 111 exceed 2 px. A
threshold set anywhere that un-badges the fast drops un-badges most of the climbs, the coloured sheep's 10 px wall
walks included; only the bundled sheep's 2 px `vertical_walk_up` would survive it. The engine has no fact to
offer either: a gravity-less animation travelling along the wall's axis is handled the same whether the artist
meant a slide or a walk, until a border fires. So the badge keeps saying what the engine does, and the wording of
CLIMB ("holds a surface, and it travels along it") is true of both.

**A multi-skin archive is the author's pick, through a rule the self-test can drive (2026-10-01,
N-burn-tools-01).** `PetStudioWindow.SelectSkin(skins, picker)`: one skin converts unasked, several go to the
picker, a null answer converts nothing and the status says so; the window's picker is a modal list of the names
`SkinLayout.Detect` found (a skin without its own conf says it uses the bundled behaviour set), owned by the studio
window. Considered and declined: converting every skin of the archive in turn, which would need per-skin editor
state the window does not have, and a combo in the import-loss area, which would put a pre-conversion choice
after the conversion. No source invariant pins the one call site (`SelectSkin(skins, _skinPicker)` in the import
core): the rule is pinned with a recording picker and a mutation that restores `skins[0]`, and a hardening
invariant for it would cost the whole hardening harness a re-run for a low usability item.

**The import status carries the converter's round-trip diagnostic (2026-10-01, N-burn-tools-02, RA-372's
PetStudio half).** `ImportedStatusPrefix` takes `ConversionResult.Error` and appends it, in parentheses, to the
"does not round-trip" clause it explains, and nowhere else: a pet that round-trips shows nothing of Error (the
validator's refusal reaches the report pane by its own path), so F427's first-difference offset and window now
reaches the one PetStudio user who can act on it, in the sentence they were already reading.
#### burn/tools

**A mount prefix is declared, never inferred (2026-09-30, the "every converted climb replays its mount pose"
item).** A surface spoke repeats from past its intro only when the source's action block says two things: the
block plays ONCE (a classic Type="Animate", a bundle `loop: ONESHOT`) and its leading poses hold still on the
surface axis while later ones travel. Velocity alone was rejected on a measurement: the stock Shimeji-EE
ClimbWall and every export of it open with a still pose inside a two-beat hold-step-step-step rhythm the
reference player loops whole (base conf, Rick, Gakupo, Ralsei, Hornet, KinitoPET, Alipheese, Bugcat Capoo and
eleven more Android bundles), so a leading still pose inside a declared loop is a pause, not a mount. The skin that
surfaced the defect, brq51bkr, is not reachable by any data rule (26 uniform dy=-2 frames, ONESHOT, no other block
owning the turn sprites) and keeps its hand-set `repeatfrom="4"`, which `reclimb` now preserves. Re-conversion
was measured, not assumed: old (8eea13a) and new converters over all 31 shipped sources differ in no repeatfrom,
frame, velocity, sound or version, so no shipped pet was re-converted for it.

**RA-375 moves the hub weights of 24 shipped converted pets on re-conversion, and they were not re-converted
here (2026-09-30).** The same old-versus-new run found the Stand hub's edge probabilities differ, and nothing
else, on 24 pets: all 18 Android bundles (06n2wuu6, 08dkbwmb, 1l2yvz73, 36po5aw2, 3g8t9v4e, 3x56f4pl, 55atqs1b,
5xs0ld2m, 76xviks0, 7gb3ediv, 88f9sqb5, 8opqq9of, 8u2lojrb, 8vqm59ot, 9imr7z1s, 9qc0h184, brq51bkr, dqjd9s2d)
and capybara-albino, cyn, gengar, kinitopet, loona-hellhound, serial-designation-j; 7 are byte-identical
(alipheese, hornet, ralsei, rick, cartman, uzi, gakupo). The cause is the fix itself: a mirrored walk pair now
carries both behaviours' frequencies and a gaze carries its own. Companions/ is outside lane burn/tools'
boundary, two shipped pets carry hand edits a re-conversion would wipe (brq51bkr's repeatfrom, Hornet's frame
swap), and no migration rung can apply it (the loser's frequency is not in the emitted XML). The owner decides
whether to re-convert; the precedent is HubFloorBudgetPercent, where the affected companions were re-converted by
hand and no format version was bumped for the same reason.

**Per-skin conf resolution changes one precedence on purpose (2026-09-30, RA-386).** A pack that carries both a
root conf and img/<Skin>/conf converts against the override, as Shimeji-EE resolves an image set; and the
root-wide fallback never hands a sprite folder a conf that another sprite folder resolved as its own, which is
what makes a mixed pack (one character with a conf, one sprites-only) come out right rather than paired.

**The migration verbs share their tail, not their head (2026-09-30, N-deadcode-03).** Measured over the nine
verbs: reloop and restsplit each admit two format versions, rebalance gates on the author alone, undirect must
write when it renamed nothing, and reweight and rebalance skipped the reachability proof the other seven ran.
The serialise / re-validate / reachability / write tail is one helper (`CommitMigratedPet`); a shared head would
be four switches pretending to be one policy, so the head stays per verb. Reweight and rebalance now run the
reachability proof, which they cannot fail.

**SHIMEJICONVERT_FFMPEG names the transcoder (2026-09-30, RA-379).** An executable path, probed before
native\ffmpeg.exe and the PATH; a .cmd shim, the one way this box exposes ffmpeg, is invisible to a
UseShellExecute=false probe, and the .cmd route was declined because every clip path would then pass through
cmd.exe.

**Two properties, no numbers (2026-09-30, RA-389 and R-074).** The compositor's peak is one bitmap per distinct
picture rather than per distinct name, and the WebP decode holds one copy of the raw payload rather than two.
Neither was measured cold in interleaved processes, so neither claims a figure; the compositor's property is
asserted through `SpriteSheetBuilder.LastComposeBitmapCount`.

**Three closures by duplicate (2026-09-30).** RA-200 and RA-388 close with R-073, whose change F458 landed
(SpriteSheetBuilder.cs:52, 59, 64, 67). The remainders outside this lane's boundary are filed as
N-burn-tools-01 to -04 in BACKLOG.md.
#### burn/remembrance

**Remembrance keeps friendly names for its two device dropdowns (2026-09-30, N-deadcode-01, DECLINED-MEASURED).**
The case for endpoint ids rests on two ACTIVE endpoints sharing one FriendlyName, which the dropdown would then
show as one row. Measured on this box on 2026-09-30 from the registry's MMDevices store (Render and Capture, every
device state): 52 endpoints known, 9 composed friendly names shared by two to four registrations, and none shared
among the 15 in the active state. The duplicates are ghost registrations of one device re-plugged on another
port, and `AudioDevices.Enumerate` asks for `DeviceState.Active` only, so the dropdown never meets them. The cost
side: `SettingKind.Enum` stores the displayed option string, so an id-keyed selection needs a display-to-id map
with unique labels, and two live endpoints with one name can only be told apart positionally (" (2)"), a label
that moves as devices come and go; and `sysDevice` / `micDevice` hold names in every existing install, so ids
would need a second resolution rule for the legacy value. Names stay. If a user reports two same-named active
endpoints, this is the entry to reopen, and the whisperModelChoice display-to-id map is the shape to copy.

**RemembranceModule.cs keeps its self-test in the file, and this burn-down made it longer (2026-09-30, RA-162,
ACCEPTED-RECORDED).** The entry counted 1,015 of 2,518 lines as in-class test code; after this lane the file is
longer still, because eight of its items are checks. Moving the SelfCheck* methods and the three HTTP doubles to
a second internal file is the right shape and is the same decision ReminderModule.cs is waiting on (R-048), so
the two should move together, in a pass that touches no behaviour, when no lane is appending to either file —
not in the middle of a campaign where three lanes have edited this one in a week. The seams are the reason it is
bearable meanwhile: every check reaches production through a named delegate, so the test code is appended to
rather than interleaved with what it tests. The recorder's checks already live in `RecorderSelfCheck.cs`, which
is the model to copy.

**The silent keep-alive stream is an audio session, and its two side effects are documented rather than removed
(2026-09-30, R-036, ACCEPTED-RECORDED).** For the length of a recording the process is listed in the Volume Mixer,
silent, and the audio engine holds its "An audio stream is currently in use." power request, which
`powercfg /requests` shows under SYSTEM attributed to the audio driver rather than to this process, and which
blocks automatic sleep. Both are what a meeting recording wants, since sleep would end it. Both now sit beside
`RenderKeepAlive`'s summary with the observation recipe. The live alignment check (40 s with the system output
idle for 20 s, then a tone) still needs the console session `BLOCKED.md` T25 names; no number is claimed for it.

**A Whisper install that fails its run check is marked, not deleted (2026-09-30, RA-167).** `WhisperInstaller`
writes `check-failed.txt` into the install root when `TryVerify` fails, detection skips a root carrying it (exe
and model both), and a later passing check removes the file. Deleting the install instead was rejected: the
failing half may be the runtime rather than the bytes, and a 466 MB model is not re-downloaded on a guess.

**"Set up Whisper for me" follows the model dropdown once something is detected (2026-09-30, RA-158).** A
detected pair whose model is the chosen one is adopted as "already installed"; one whose model differs fetches
the chosen model beside the module's install, keeps the detected exe, and verifies the pair. Adopting whatever
was detected and ignoring the dropdown was the shipped behaviour and contradicted the dropdown's own label;
"Find an installed Whisper" remains the button for adopting an install without any download.

**The Ollama pull's idle bound is five minutes, not WhisperInstaller's sixty seconds (2026-09-30, RA-153).**
Ollama goes quiet between layers of its own accord ("verifying sha256 digest" on a 7 GB layer prints nothing
until a slow disk has read all of it), so the bound that fits raw download bytes would abandon a pull that was
working. The header bound is 30 s, as the release lookup's is.

**A second snapshot inside one wall-clock second gets a " (2)" suffix, and the suffix is a purge shape
(2026-09-30, RA-156).** Announcing "replaced the one taken this second" was the cheaper fix and was rejected
because it still loses the first capture. The suffix is parsed (" (N)", two to ninety-nine) exactly as the stamp
is, so a near-miss in a folder the user chose stays out of the purge's reach.

**The purge removes a capture folder it has emptied, once the folder is older than the window (2026-09-30,
R-037).** A failed start that kept a one-packet scratch left a folder the purge would later empty and never
remove. Name-parsed, empty only, and aged by the later of its creation and last-write times, so a folder
NewCapture made a moment ago for a recording whose first writer has not opened yet is never in reach.

#### feature/remembrance-2

The 2026-10-02 feature batch for Remembrance 2.0.0 (the owner asked for every item and approved the design; the
coordinator's addendum 1 corrected three parts of it).

**An Apply keeps what the module wrote in the background, and nothing else is diffed (2026-10-02, BUG-013).** Save
writes every field it is handed, as it always did, except a field the module wrote outside the pane after the pane
loaded (the pull's selection and model list, the first-open discovery's list, each recorded by `SetInBackground`)
whose value on screen is still what Load showed. Load clears the record. The brief's first form, writing only the
fields whose value differs from Load's snapshot, was refused: the pane shows DERIVED values (the summary dropdown
preselects a model when none is saved, the device rows show the default entry), an untouched Apply has always
persisted them, and the stop path reads only the saved `summaryModel`, so that rule would leave the pane naming a
model no summary uses, the saved-not-shown defect BUG-013 is. A mutation case keeps that rule out.

**The pending-aware actions set `InvokeWithPendingAsync` alone, with no `InvokeAsync` beside it (2026-10-02).** AI
Brain keeps both as a fallback shape. Here `MinHostVersion` is 1.2.5, so no host that loads the module calls the
saved-values delegate, and a second entry point would be a second thing to keep right.

**The three Browse buttons still open their dialog at the SAVED path (2026-10-02).** BUG-013's list does not name
them: the saved value only seeds where the dialog opens, and the pick is written and shown by the reload. A start
folder taken from the screen is a possible follow-up, not a defect left behind.

**Two buttons are called "Refresh local models" and two "Validate", one pair in each card, on purpose (2026-10-02,
the owner's naming).** "Refresh local models" is AI Brain's name for the same job, and one pair of words learned
once beats two invented pairs. The host carried an action's result across a rebuild keyed by label alone, so the
second card's row would have repeated the first's answer; that is the host's to fix (keyed by group and label), in
the coordinator's modules-update-all lane, and these labels stay as they are.

**The Summary card's Validate loads the model (2026-10-02).** Its third step sends one short request, which is a
model run with the cost the "Test the summarizer" button it replaces always had: "installed" is not "working", and
a model too big for the machine or damaged on disk shows itself only there. Steps one and two (the address answers,
the model is listed, an untagged name matching its ":latest") run first, so a missing server or model is named
without loading anything.

**The Transcription card's Refresh keeps a working pair and adopts only to fill a missing one (2026-10-02).** "Find an
installed Whisper", which the burn/remembrance entry above names as the button for adopting an install without any
download, is "Refresh local models" now, and it no longer adopts whatever detection finds: when both files on screen
exist they are kept, nothing is written, and the answer names any other model detection found ("also found: ...",
full paths) so the user can Browse to it. Only a pair with a missing file is replaced, by the whole detected pair; with
nothing detected it says which file is missing. The decision is `WhisperInstaller.PlanRefresh`, pure and pinned.

**The Transcription card's Validate is the install check, at its own five-minute cap (2026-10-02, addendum 1).** It
runs whisper-cli through `WhisperInstaller.TryVerify`, the one process path besides `Transcriber.RunWhisper`, which
already judges a pair by exit code 0 alone; the brief's first form, a run through RunWhisper under a short bound of
its own, would have added a third way to start the child. TryVerify gained an optional clip length (two seconds here;
the installer's own call passes none and is unchanged). The Validate neither writes nor clears `check-failed.txt`: that
marker is the install check's verdict on the module's own install, and this button checks whatever pair is on screen.

**"Create a folder per capture" is a two-option choice, and its OFF state migrates to by date (2026-10-02).** The
Radio row stores `folderLayout` = `capture` / `date`, never the option text. An install that has not chosen reads the
old `folderPerCapture` at use (`FolderLayout.Migrate`, AgentFlow's mode-migration shape): OFF filed captures flat in
the root, and by date is the nearest layout that writes nothing new there; ON or unset is per capture. The old key is
never written or deleted. By date files a capture in the folder for its LOCAL start day under the flat names, so a
file moved out still says what it is.

**A snapshot taken while nothing is recording goes into that day's folder, or into "Snapshots" (2026-10-02, the
owner's decision through the coordinator).** By date it lands in that day's `yyyy-MM-dd` folder, per capture in a
folder named exactly `Snapshots` under the storage root, named as before (`snap <stamp>.png`, the ` (2)` suffix
included). It used to land in the root, and the "snap" files already there are still purged as before.

**Day folders and the Snapshots folder are matched strictly and removed only when the same purge pass emptied them
(2026-10-02, addendum 1).** A day folder counts only when its name is exactly `yyyy-MM-dd` and a real date, and is
judged by the flat file rules; `Snapshots` counts only by that exact name, case included, and inside it only the
`snap <stamp>.png` shape. Neither is admitted through `IsCaptureFolderName`. Because a bare date or "Snapshots" is a
weak name, a folder of either kind is removed only when this pass deleted one of the module's files from it and left
it empty, never because it is empty and old: a user's own empty "2026-10-01" or "Snapshots" folder survives. A start
that fails removes only a folder its own `NewCapture` created.

**`remembrance.busy` carries the time of its latest publish, and where each span starts is chosen (2026-10-02,
addendum 1's contract, shared with the aibrain-standdown lane).** The value is `{"phase":...,"at":...}`, `at`
republished at every phase change and before every summary request (the map-reduce's progress report, which it
makes before each request it sends), so a live value is never older than one whisper run or one request. `at` is
taken when the module asks to republish, not when a posted publish happens to run. Spans are counted: the flag stays up
until the last ends and names the newest. A span that starts on the UI thread publishes synchronously there: the
stop (before its background work, and only when whisper-cli will really run, so a stop that writes the setup stub
raises nothing), "Transcribe a WAV file…" and "Summarize a transcript…". The two Validates raise it for their model
step alone, on the pool thread, so a refused path or an unreachable server is answered without the flag going up.
Shutdown and the host's shutdown clear it synchronously, never by a post, and close it so a span that ends later
publishes nothing. "Set up Whisper for me"'s one-off install check also runs whisper-cli and is not covered: neither
brief names it, and covering it would need a hook inside `WhisperInstaller.InstallAsync`.
#### burn/scripts-pack

**Markdown under a watched directory outside the module is excluded from the watch pathspecs the way the
module's own Markdown is, and a `None` item without `CopyToOutputDirectory` is not watched at all (RA-181,
2026-09-30).** The rule was already written beside `Get-ModuleWatchPathspecs` ("Markdown ... never reaches
the assembly") and applied to `modules/<Name>` only; the bundled ModuleKit directory sat in every module's
watch set with its packed README.md, so a README-only ModuleKit commit staled all seven zips and, once
F215 made the same set the publish guard, an uncommitted README edit refused every publish. The exclusion
now travels in the pathspec GROUP of the directory it belongs to (`ExternalPathspecGroups`), so the
freshness check's per-path culprit attribution excludes it too. Keyed on the copy setting rather than on
`Pack`, so a file that is both packed and copied stays watched. Measured against a scratch clone at HEAD:
an uncommitted ModuleKit README edit is seen by the old pathspecs and not by the new, and an edit to
ModulePaths.cs beside it is still seen.

**Module zips sort their entries ordinally, the same rule the portable zip has always used (RA-186,
2026-09-30).** `Sort-Object FullName` compared with the current culture's case-insensitive rules, so the
entry order, the zip bytes and the catalog hash depended on the publishing machine's culture: on a
nine-entry synthetic payload the ordinal order puts `Module.dll` and `ONNXRUNTIME_THIRD_PARTY_NOTICES.txt`
before the lowercase names where en-US `Sort-Object` interleaves them, and the fortunes payload already
sorted differently under tr-TR. The committed zips are in en-US order and reorder once at the republish
R-052 already requires; the F213 commit-body note still attributes a churn to the runtime, which after
this date is the only remaining source.

**Windows PowerShell writes a UTF-8 BOM into a redirected child's stdin when the console code page is
65001, and the catalog asset reader absorbs it (N-scripts-pack-01, 2026-09-30).** .NET Framework's
`Process.Start` wraps a redirected stdin in a `StreamWriter` over `Console.InputEncoding` and sets
`AutoFlush`, whose setter flushes at once and writes the encoding's preamble, so `git cat-file --batch`
received `EF BB BF` before this code wrote a byte (reply bytes `EF BB BF 48 45 41 44 3A`, measured
2026-09-30 under 5.1.26100 from a pwsh-hosted session; pwsh's own runtime strips the preamble). git read
the first request as `<BOM>HEAD:<path>`, answered `missing` for a committed file, and F209's refusal of an
unexpected reply failed the verifier and the generator on their first asset. The F208 measurements passed
under 5.1 because that console ran a legacy code page, whose encoding has no preamble; the defect is real
in every UTF-8 console. Chosen: the bytes are already on the pipe and cannot be unsent, so the reader
closes them off as a request of their own (one LF) and consumes the reply git owes for exactly those
bytes, `<BOM> missing`, refusing anything else. Rejected: setting `[Console]::InputEncoding` to a
preamble-free encoding before `Process.Start`, which changes the host console for the whole gate process
to fix one child. The substituted stall child is exempt: it is not git and exists to stall or die.

**The published-DLL build-path scan reads the embedded portable PDB's Document table, and the cure for what
it finds there is `DeterministicSourcePaths` in the projects that embed symbols (RA-197, 2026-09-30).**
`DebugType=embedded` keeps the CodeView record bare and ships the whole PDB inside the DLL, and every
ModuleKit.dll in every committed zip carried fifteen absolute `D:\...` source paths in that PDB's Document
table while the gate reported "no embedded build paths": the names are stored as shared parts joined by a
separator, so no regex over the image or the inflated blob can see them. `packaging/EmbeddedPortablePdb.ps1`
parses the PE debug directory, inflates the entry and reads the table by hand (Windows PowerShell 5.1 has no
System.Reflection.Metadata), and the scan refuses a drive-rooted or UNC document name the way it refuses a
CodeView path. Two guards keep the reader honest: an embedded PDB that parses to zero documents is refused,
and a ModuleKit.dll with no embedded PDB is refused because its csproj embeds symbols on purpose (the WITNESS
that turned the one mutation the scan could not see, a reader blind to the entry, into a red). Measured:
the committed zips at the audit commit refuse with 105 offenders; a ModuleKit built with
`-p:DeterministicSourcePaths=true` yields 15 documents rooted at `/_/src/...` and none. The build-side
half, that property in `src/DesktopAICompanion.ModuleKit/DesktopAICompanion.ModuleKit.csproj` (and in
Contracts', which embeds symbols for its nupkg), sits outside lane burn/scripts-pack and is handed to the
coordinator: without it the republish that R-052 requires turns this section red naming the paths, which is
what those payloads carry.

**The research harness scores Claude Code's paren-less rule semantics and says so whenever it loads one;
the shipped matcher is the copy to correct (RA-011, 2026-09-30).** A rule written without parentheses
(`Edit`, `Bash`, `WebSearch`) is the whole tool in Claude Code, and `agentflow_join.py` has always stored it
as pattern `*`; `modules/AgentFlow/PermissionRules.cs` compiles it to an anchored literal that matches no
`Tool(arg)`, so on a box whose settings.json allows `Edit` bare the harness scores an `Edit(path)` denial
would-allow (a miss) where the module would raise, and the README's 21/30 recall row, labelled "shipped
semantics" by F018, is the harness's number: the module would print 23/30 on the same corpus. Decided the
same way as F017 (`WebFetch(domain:...)`): the Python follows the documented semantics, pinned by two
self-test cases, and prints a note naming every paren-less rule it loaded so a figure is read with the
caveat; the divergence is closed on the C# side (NormalizeRuleUncached or RuleRegexSource treating a
parenthesis-less rule as `Tool(*)`, with a SelfCheck WITNESS), which is lane burn/agentflow's file.

**The harness reads the five rule files the module reads (RA-009, 2026-09-30).** `load_rules` takes the
three home tiers RuleLoader.cs takes (settings.json, remote-settings.json, settings.local.json), and
`walk()` merges the two project-scope files under each call's cwd the way RuleLoader.WithProjectRules does,
carrying `cwd` forward in file order like the permission mode. Nothing moved on this box (no project-scope
file holds a rule; the run says so), which is why the item was information; it was taken because the
first "for this project (just you)" grant would otherwise have made the harness score a call the module
leaves alone as would-prompt, in the functions the RA-011 and RA-012 work was already editing.

**F007 stays a recorded hole until both sides of the classifier move together (2026-09-30).** A read of
exactly `Yes, allow access to` is an incomplete read of the directory-grant "don't ask again" row and
presses approve-once through the `yes, allow ` template; the bare exact entry is redundant, not the cause.
The close is a guard in `classify()` AND `PromptOptions.KindOf` (a template prefix with nothing appended is
UNKNOWN), the truncation WITNESS extended, and tests/difftest-prompt-options.py's `table-bare-prefix` rows
pinned to Unknown, all in one commit, because the differential derives that row's expectation from the C#
table's exact entry and fails on either half alone. The C# and the difftest are lane burn/agentflow's
boundary; the hole is recorded in the classifier's table comment and README row, and now here.

**The WiX bootstrap's private mode stays, and build-installer.ps1 consumes it (F211, 2026-09-30).**
`Install-LockedWixToolchain.ps1 -ToolPath <dir>` without `-GlobalExtension` installed the tool and its
extensions privately and nothing in the repository could use the result: build-installer.ps1 resolved the
global dotnet tool root and the global extension cache only, so the mode was about seventy lines that
produced an installation the installer refused. The cheaper fix was deletion. Kept instead, because the
private mode is the one way to exercise the bootstrap on a box that already has a global wix (the
"Refusing to reuse a pre-existing global WiX executable" refusal is correct and makes the global mode
untestable there; F210 was measured through -ToolPath for that reason) and the one way to build the MSI
without touching the global tool root. build-installer.ps1 takes `-WixToolRoot` and `-WixExtensionRoot`,
both or neither, verifies tool and extensions from them against the same lock, and a private bootstrap ends
by printing the line to run. Measured on this box: a 12 s private bootstrap, then both installer shapes
built from it with the private roots printed. The first draft overwrote the two parameters with the global
roots through same-named locals (PowerShell variables are case-insensitive) and used the global copy while
printing the private one; the measurement caught it, and the locals carry a `resolved` prefix and a comment.

**The PackageRoot removal's containment roots are its own parent on purpose, and the absence-at-entry check is
the guard (RA-180, 2026-09-30).** A caller-chosen TEMP path has no natural trusted root, so the F210 removal's
`-AllowedRoot` and `-TrustedRoot` are the F023 tautology by construction; what protects the recursive delete is
that the root was refused if present at entry and created by this run's lease, so the only directory the
delete can reach is one the script made. Said at the call. Bounding the delete to the inner scratch would
leave the outer root behind and re-open F210's second-run refusal on the Readme recipe's fixed path.

**The release retention window has one copy, in release.yml, and the template check reads it from there
(RA-003, 2026-09-30).** `$keep = 3` in the prune step and `$keptReleases = 3` in Test-ModuleTemplate.ps1 were
held equal by prose, the drift shape this repository keeps correcting: a lower `$keep` would have let the
template name a pruned version with the check still green, a higher one would have refused a valid default.
The alternative, keeping both literals under a source invariant that asserts equality, was not taken: an
invariant would say the two agree, while reading the one from the other means there is nothing to agree. The
read asserts exactly one match and a floor of one, so a reworded prune step fails the check loudly instead of
the check defaulting to a number nobody re-measures. The chore the prune forces on the releaser (template.json's
packageVersion moves up when a release drops the oldest window entry) belongs in docs/RELEASE-CHECKLIST.md,
outside this lane; until it is there the script's own refusal carries the instruction.

**pingus keeps its unreachable `walkup` edge; the one-attribute fix waits for the owner and the publish round
(N-petstudio-03, 2026-09-30).** `walk` offers `walkup` on `only="horizontal"`, the top-of-screen edge, so a floor
walker never climbs and the fly/fall2 chain behind it never plays; the same shape as the 1,713 hand-authored
pairs accepted as they are on 2026-09-29, and the engine author's own art. Changing the edge to
`only="vertical"` would make the climb reachable at a wall and change Companions/pingus/animations.xml, whose
bytes catalog.json hashes, so the edit can only land with a catalog regeneration; it is written here for the
owner to take or leave at the publish round.

**The seven module republishes and the catalog are the coordinator's publish round, and three of this lane's
changes depend on its order (R-052, RA-196, 2026-09-30).** This lane never touches modules-dist/ or catalog.json.
RA-186 reorders every zip once (ordinal entry order), RA-189 puts the PowerShell version in each publish commit's
body, and RA-197's embedded-PDB scan turns the freshness check's build-path section red on any zip whose
ModuleKit.dll was built without `DeterministicSourcePaths`: the csproj change (src/, outside this lane) has to
land before the republish, or the republish is followed by a red gate that names the paths.

**The Fortunes label self-test tests the builder that exists (N-records-01, 2026-09-30).** `build-corpus.sh`
was rewritten to assemble the corpus from in-repo labeled inputs (its own header says why: the upstream text
cannot regenerate the labels), and `label-selftest.sh` kept 162 lines holding the retired builder to a
provenance contract it no longer has, failing at its first expectation on every run while no gate ran the
script. Chosen: the fixture follows the builder (inputs at both resolution paths, every refusal preserving the
prior output, a clean build, `--check` three ways); the retired contract is not re-tested, because testing a
contract the code does not make is a check that cannot fail for the right reason.
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

**Save judges on a copy and writes the live instance once (N-burn-aibrain-02, 2026-10-01).** RA-058 made a save
refuse a key it could not store, but `ApplyPaneValues` had already written every other typed value onto the live
`AiSettings` by the time it reached the key, so the refused save left the instance torn: the pane showed the typed
values, the file and the brain held the saved ones, and the instance held a mixture until the next Apply. The filed
suggestion was to apply onto a copy and swap on success. The copy is right; the swap is not: the session, the pane
and the brain factory hold the live reference, and `CloneForBrain` marks its copies unsaveable on purpose, so a swap
would hand them a stale instance or need a second kind of copy. Instead Save judges the values on the detached copy
`PendingSettings` already builds for the pending-aware actions and, only when that copy proves the key storable,
applies the same values onto the live instance, in one short-circuit expression. The live write's answer is still
read (a key refused between the two calls, a DPAPI failure microseconds apart, still refuses the save), and there is
one decision point, so one mutant can defeat it; that is what keeps the RA-058 case killable, since with two
independent refusal points no single-line mutant could make the save report success with the key dropped. The cost
is the pane mapping running twice on a successful save, microseconds.
#### burn/fortunes

**The bulk pack and genre actions save at once; nothing on the Fortunes pane waits for an Apply the host
cannot arm (2026-09-30, fortunes 1.0.12, RA-121, RA-122).** "Select all" and "Select none" on the Fortune
packs and Genres cards staged into the module's own map, like the individual ticks the host flushes at
Apply, so that a bulk change cost one write and one rebuild. That map had no discard signal: the host
throws its deferred ticks away on close and on a ReloadPaneAfter rebuild, the module heard nothing, so a
"Select none" followed by Cancel reappeared at the next open and rode the next Apply for any field; and
with no field edit pending the host greyed Apply out after the action's own reload, so "Apply to use it"
asked for a button nobody could press. Each press is now one `MergeDisabled` fold, one `Save` and one
synchronous `RebuildEngine` (the host re-runs Load the moment the action returns and reads the pool status
from the provider, the same reason Apply's rebuild is synchronous). Rejected: clearing the staged map on
the next pane Load, because a Rescan or an Import would then silently discard the bulk choice and Apply
would stay grey; and an ABI member that lets an action mark the pane dirty, because with nothing pending
there is nothing to mark. What still survives a Cancel is the batch a FAILED Apply retains for its retry
(1.0.4), by design: the host's own pending ticks are gone by then and that map is the only record the
user's clicks have left.

**The importer's overwrite, backup and rollback half stays unwired (2026-09-30, RA-103, ACCEPTED-RECORDED).**
`ImportPacksAsync` passes null approvals, so a same-named pack is reported as skipped and the branch that
replaces a file behind a backup has no production caller; its five self-tests and the N-tools-02 witnesses
cover that branch. It stays because the ABI has no confirmation prompt through which a module could obtain
overwrite consent (`PluginApi.cs` offers a file picker and a link opener, nothing that asks a question), so
wiring consent is a host change, and because the branch is the tested contract that consent will need, not
a shape production abandoned: deleting it in a lane about residue trades a working, tested path for a
smaller file. What this lane changed instead is what the coverage claim rested on: the commit's `File.Move`
branch, the one production reaches, has its transient classification pinned by code (RA-102), and the
overwrite branch no longer throws a torn replace's backup away (RA-104), so consent, when it arrives,
arrives on a safe path. The download path writes packs in place by design: `CacheMissingPacks` lists only
packs not on disk, so a download never replaces one.

**The per-file parse cache keeps a second copy of the custom tier, 2.7 MB at the full catalog (2026-09-30,
RA-105 DECLINED-MEASURED, RA-106 ACCEPTED-RECORDED).** Each `PackParse` holds its pack's entry list, the
unit a changed folder reuses, and the `CustomSnapshot` holds the flattened copy that `Select`, `Sources` and
`Genres` iterate; `FortuneEntry` is five references and two bools, 48 bytes on x64, so the duplicate is
55,783 x 48 B = 2.68 MB with all 158 catalog packs installed and nothing on a default install, beside a
vector index of about 90 MB at that scale. Folding them means either a flattened copy per rebuild, which is
the cost F125 removed, or a list-of-lists enumerator in every consumer, the importer's cache reads included.
Declined until a measurement says the 2.7 MB matters; the trade is written at the cache.

**A tagged row's text column is admitted trimmed (2026-09-30, R-030, ACCEPTED-RECORDED).** F132 decodes the
column before validating it and `DecodeScrapedText` trims, so a row whose text carries whitespace around it
enters the pool trimmed where 1.0.11 refused the row as untrimmed, in the loader and in the importer alike.
The friendlier admission stays: a hand-edited pack with a trailing space on one line is a pack the user
meant to load, and the shipped packs are machine-generated and unaffected. It is pinned in the probe and
named in the 1.0.12 changelog, so a return to the strict reading fails a check first.

**A downloaded pack is parsed twice, once on a pool thread for admission and once by the rebuild
(2026-09-30, RA-124, residue recorded).** The admission check left the UI thread; the rebuild that follows
parses the new file again through the per-file cache's miss for a file it has not seen. One file per
download, on a pool thread. Seeding the cache from the admission parse would need the file's length and
write time after the write and a second entry point into the cache; not done for one file.

**A superseded smart build stops before it constructs and again before it warms; a cancellable
construction was rejected (2026-09-30, fortunes 1.0.12, RA-118, RA-119, RA-110).** `BuildSmartPicker` asks
`SmartBuildSuperseded` after the dispose it waited on (up to 3 s) and after `new SmartFortunes()`, before
`Warm`: a rebuild scheduled in either window now costs at most the cache.bin parse, never a second session
or a warm that the successor's dispose cancels three seconds later. Rejected: making the construction
itself cancellable through the token SmartFortunes' constructors already threaded down to
`VectorCache.Load`. That token was dead (every caller passed None, RA-110), and a live one would need a
per-build source the module cancels under `_smartLock`, a catch that tells a cancelled construction from a
failed one so the "unavailable" log line is not written for a build that was merely overtaken, and a
token-bearing constructor F136 had just removed. The window it would close is the parse of cache.bin,
which the two checks already bound; the plumbing went instead. `VectorCache.Load` keeps its token for the
re-load a released instance does inside the warm.

**The Rebuild button's currency guard parses the folder on the UI thread for a complete index only
(2026-09-30, RA-126, ACCEPTED-RECORDED).** F127 left the guard's fresh provider on the UI thread deliberately
and did not say so; this records it. The guard answers "is the index already built for the folder as it is
now", which needs a parse of the folder when it changed; an index still warming or stood down is rebuilt
whatever the folder holds, so the guard now reads WarmProgress first and builds the provider only when the
index is complete, the case the F149 invariant slices and whose synchronous shape it pins. Moving that
remaining parse to a pool thread takes the comparison text `PoolSignature(fresh.PoolEntries())` out of the
synchronous method the invariant slices, so it is a fix/gates invariant re-point, named for the coordinator;
what it would remove is one parse of the changed files, the per-file cache making an unchanged folder a
cache hit.

**A warm that threw is not current; every other stand-down is (2026-09-30, fortunes 1.0.12, R-031).** F147
keeps a picker whose pool is unchanged, healthy or stood down, so an Apply that changes nothing about the
pool costs no rebuild. The catch-all's `WarmFailed` reason is the one stand-down a retry can change (an
out-of-memory, an invariant breach inside the cache), so `ScheduleSmartPicker` treats it as not current and
the next Apply rebuilds; a missing model, an oversized pool and an embedder that cannot load stay kept,
because a retry cannot change them and each attempt costs a session load or a parse.

**Cold-warm checkpoints rewrite the whole vector map once a minute; the magnitude is estimated, not
measured (2026-09-30, RA-112, ACCEPTED-RECORDED).** F138 set the checkpoint cadence to elapsed time so a crash
loses at most a minute of embedding; each checkpoint snapshots the map, sorts every key, writes every entry
and fsyncs under the Global mutex, so a cold warm of W minutes writes about W/2 times the final cache size
(about 94 MB at the full catalog): tens of megabytes a minute for the duration. The no-format-change
alternative, a checkpoint that appends a delta segment, changes the fingerprinted flat format
`TryReadCacheFile` assumes; a longer interval trades crash loss for I/O. Neither is done without a cold
full-catalog warm measured on this box, which has not been run; the property stands as F138 recorded it.

**Pick stays synchronous on the UI thread (2026-09-30, RA-113, ACCEPTED-RECORDED; RA-114 is its arithmetic
half).** A contextual fortune costs one ONNX query inference and one dot-product pass over the pool on the
thread that raised the land, poke or drop. Moving it off that thread needs the responders to answer
"handled" before knowing whether the smart pick lands (PluginApi's responders are synchronous) and changes
what the host's --fortunes-selftest asserts synchronously after each trigger; both are host-side contracts.
This lane did the arithmetic half instead: the scoring loop no longer re-validates every vector, which was
two thirds of its operations (RA-114). Vectorising the dot itself was left, because it changes the
summation order under the seeded diagnostics and no cold measurement says the gain is worth moving scores
by a rounding error.
#### burn/agentflow

**The CDP sweep stops at a press, and every refusal it read comes back in one line (2026-09-30, RA-047).**
`CdpApprover.Sweep` broke out on the first non-null note, and a refusal is a note, so a standing prompt the
module will not press on the first-listed webview (a plan prompt, an unrecognised option, a disabled row)
starved every later webview of its approvals and announcements, tick after tick. The handler now says
whether it pressed (`PromptHandler`); the sweep breaks on a press alone, and one click per tick stays the
cadence: a second webview's pressable prompt takes the next tick rather than a second click inside the same
pass, which would run with no budget check between the two clicks and double the wire time under the tick's
deadline. When nothing was pressed the refusals are joined, sorted, into the one returned note, so the log's
once-per-outcome guard (F029) sees the same set as one outcome whatever order `/json/list` listed the
targets in; a press note is returned alone, because a press is logged every time and the standing refusals
beside it would be re-logged with every press. Several unpressed prompts announce as one notice naming each
subject, keyed on the sorted set, so the once-per-prompt guard re-arms when the set changes. Announcing only
the first-listed prompt, the old behaviour, was the same starvation in the other channel and was not kept.
The string-only `Sweep` overloads survive for the assertions and read every note as a refusal.

**The card's fingerprint is the prompt's identity, and a confirmed click no longer clears the repeat guard
(2026-09-30, RA-022, RA-023, RA-049).** Both read expressions hash the card's visible text with the option
buttons removed (32-bit FNV-1a, eight hex digits, computed in the renderer) and the hash alone crosses the
wire; the press signature and the screen one-shot carry it. 1.4.0's clear-on-'clicked' was fixing three
different compound-command prompts signing alike (`Bash|Yes|No`) and latching the module off, but it did so
by reading a click that ran as proof the card went away, which made the loop the guard exists for (the
click runs, the card stays) unreachable: a wedged card was re-clicked and logged every ten seconds with no
stand-down. Identity is the signature's job, so the fingerprint carries it and the clear goes; the sweep
that finds nothing is the one clear left. Accepted cost, stated at both sites: three identical retries of
one command inside thirty seconds, each genuinely answered, read as one wedged card and stand the module
down until the switch moves (the refusal says how), and an empty sweep between them clears it. Rejected:
re-reading the card straight after the click to confirm it went, because the renderer re-renders
asynchronously and a read a few milliseconds later can still see the old card, which would count working
clicks as stuck. Not verified against a live editor in this lane (no GUI work here): should a card's text
change while it stands, the fingerprint changes with it and the guard degrades to 1.4.0's never-latch,
never to a wrong press; `docs/agentflow/agentflow_cdp_probe.py` replays both expressions with the
fingerprint in them, so the next live probe shows whether two ticks saw one card or two.

**A notice a channel was asked to carry and could not is HELD, whatever the channel (2026-09-30, R-004,
RA-025, RA-026, RA-048).** The rule already held for speech with no speaker (the swallowed first notice
after every launch); it now holds for the chime the app refused (`PlayNotificationSound` answers false when
the app's sounds are off, muted or without a device) and for an animation with no pet on screen, and for a
prompt seen on screen as well as one predicted from a transcript, through one `Deliver` both paths share.
The alternative, spending the one-shot and logging "recorded a notice about X (the chime was refused)", was
rejected: the user asked to be told and was not, and a one-shot spent on a tick that reached nobody is the
permanent version of the bug F034 fixed the wording of. What is kept from F034: with every channel OFF the
one-shot IS spent and the log says so, because there the user asked for nothing. Residue, stated: a chime
the app refuses permanently (notification sounds off in Preferences with the module's chime left on) holds
the notice for as long as the prompt stands, once in the log, exactly as app speech off already did. The
one-shot's Retain is fed every outstanding call of every live session, not the Blocked ones alone, so a
session that reads Working for one tick keeps its one-shot; the bound that remains is the fifteen-minute
window, and a call still outstanding when its session comes back after it is a new prompt to the budget.

**The first tick's approvals tally is neither deferred nor skipped (2026-09-30, RA-045, DECLINED-MEASURED).**
Measured cold, one measurement per fresh process, three runs per shape, the two shapes interleaved, on this
box: `BlockedDetector.ApprovedSince` over one session holding `CompletedCap` (2000) completed Bash calls
against 500 allow rules costs 636 / 662 / 811 ms when no rule matches any call (every call tried against
every rule, the rule regexes compiled inside the timed call, which is what the first tick pays) and 281 / 300
/ 348 ms when every call matches one. That runs on the pool worker inside the tick, once per session per
launch, so the UI thread pays nothing and the first `Apply` is late by that much; four such sessions are about
three seconds against a ten-second cadence. Skipping pre-launch completions would drop the approvals card's
history, which the detector stamps with the call's own time for exactly this fold (agentflow 1.4.7), and
posting the detections ahead of the tally would split one tick into two UI posts to save under a second once
per launch. The harness is a console project referencing the built module DLL, kept in the lane's TEMP
(`ra045/Program.cs`), not in the repo; the method is beside the number so it can be re-run.

**DEGRADED notes stay prose in this lane; the graded form is recorded for the host side (2026-09-30, RA-037,
ACCEPTED-RECORDED).** `SelfTestProbe.Note` writes two spaces and text, and neither runner reads it, so the
argv.json round trip and the ACL axis pass identically whether they ran or not. The in-boundary half is
already done: AgentFlow's three sites spell `DEGRADED:` at the head of the line. The fix is
`SelfTestProbe.Degraded(reason)` writing `DEGRADED: <reason>` and `tests/Invoke-SelfTests.ps1` plus
`tests/Test-ModuleSelfTests.ps1` counting and printing those lines beside the verdict without failing on them,
which lives in ModuleKit and `tests/`, outside this lane, and a ModuleKit change stales every published payload,
so it belongs in the publish round rather than in a module lane.

**The transcript walk's timing comment states the property, not the old number (2026-09-30, R-009,
ACCEPTED-RECORDED).** `TranscriptReader.ActiveTranscripts` said the 2026-09-21 measurement "holds" for the
per-directory walk F059 wrote, which was never re-timed. The comment now says what is still true (the write
time arrives with the enumeration; no per-file stat) and that the walk was not re-timed after F059; the
FileSystemWatcher decline that leans on the 11-12.6 ms figure is noted as leaning on a pre-F059 number. No new
figure: re-timing needs both walks in fresh interleaved processes, and nothing this lane decided rests on it.

**F050 stands as recorded under `#### fix/agentflow` (2026-09-30, ACCEPTED-RECORDED).** The rule regex keeps
compiling without a dotall flag until the JS original changes; the first three steps of the recorded order
of work are outside this module's boundary, and nothing in the burn-down changed that.

**The probe-flag clears keep their true reason (2026-09-30, RA-035, ACCEPTED-RECORDED).** Both clears in
`ToggleAutoApproveFromTray` stay. The comment there and the self-test's said the port is probed only while
auto-approve is on, a premise `ShouldProbePort` lost in 1.3.1 (probing follows `Scans(Mode)`); the true reason
is narrower and still holds: in Off mode `OnTick` returns before the probe, so nothing refreshes the two flags
while off, and a toggle from Off must not inherit a true left over from before. Both comments say that now.
**The retired-cursor existence sweep stays unbounded by age (2026-09-30, R-008, DECLINED-MEASURED).** F058's
retired set holds one offset and a 256-byte head per transcript that has left the window since launch, and
`SessionCache.Retain` stats each one every tick to drop the ones whose file is gone; on this box transcripts
persist for months, so that bound sits far above the live set. Measured: `File.Exists` over 200 present and 200
absent transcript paths, one measurement per fresh process, three runs: 26.8 / 30.4 / 27.3 us per path on a
quiet box (present 6.54 / 7.64 / 7.18 ms, absent 4.16 / 4.51 / 3.73 ms per 200), and 34-122 us per path earlier
the same night with nine other lanes building (present 17.37 / 11.64 / 24.32 ms, absent 11.30 / 8.32 / 6.76 ms
per 200). A day of a hundred sessions is therefore 3-12 ms per ten-second tick on the pool worker, which is the
cost of the bound the set already has. Expiring entries by age would reopen F058 for any session resumed after
the cut-off -- the defect this set exists to close -- and a shorter window means more entries, not fewer. The
harness is `ra045.exe exists 200` in the lane's TEMP, beside the RA-045 one.

**The label self-test's TERM stages assert the contract deterministically (N-scripts-pack-02, 2026-10-01,
coordinator decision).** The retired stages had a PATH `mv` send TERM to its own `$PPID` from inside the
rename and expected the labeling script to die of it: the right process (measured), but a race against the
script's completion that failed every run under 2026-09-30's ten-lane load and passed 4/4 the next morning.
Decided: a stage sends TERM itself, to the script's known PID, at a point the script is provably inside its
work (the wrapper's marker, written after the rename lands and before it blocks), and asserts the documented
outcome: the signal is held until the rename in flight returns, exit 143, the `ROLLBACK:` line, byte-for-byte
restoration naming any file left changed. The alternative, deleting the five stages, was for the case where
TERM handling was not a contract the scripts document; they document it (`trap 'exit 143' TERM` in all five,
a cleanup keyed on the commit flag, TAXONOMY.md's "signal-rollback" probe), so it was not taken. One
measurement shaped the stage: with the trap deleted, bash 5.3 dies at once and runs the EXIT trap under its
still-running child, so status, message and restored bytes are identical with or without the handler; only
"nothing rolled back before the release" tells them apart, and the stage checks that first.
#### burn/host-core

**A held instance slot is asked with no wait; the patient retry stays on the refusal path only (2026-09-30,
RA-250).** A mutex a live process owns is a definite answer, so the second allowed instance no longer sits
out the 1000 ms lease timeout on slot 1 before slot 2 is tried. The 1000 ms attempts are kept for what the
wait is FOR, a transient failure to open a free slot's lock file (a scanner holding it), and they now run
only when neither slot could be taken at once: a third launch still pays about two seconds before its
refusal, and that is the price of not reporting a transient as "already running". Stated as a property;
nothing was timed, because a cold measurement in fresh interleaved processes of a 1000 ms `WaitOne` is a
measurement of the constant.

**`--catalog-parse-file=` stays a hand diagnostic, and now says its verdict where it was typed (2026-09-30,
RA-246, ACCEPTED-RECORDED).** No gate, script or test consumes it, and none should be added as a drop-in:
the flag carries a path argument and reports in a `catalog_parse=PASS` vocabulary the self-test table does
not grade. The verdict line reaches stdout as well as the marker, the way the hardening and registry
self-tests already report (F295, F348); the exit code is unchanged. *Superseded in part on 2026-10-06:* the
flag now has consumers, `packaging/AppCatalogParser.ps1` for the gate and the publish scripts (BUG-014), which
grade its `catalog_parse=` vocabulary themselves; it is still not a row in the self-test table, for the reason
above. See `#### feature/catalog-insight`.

**A tick that throws is logged, the pet respawns, and the third fault of the session removes it (2026-09-30,
RA-234).** The catch showed a modal "Fatal Error" box over the desktop, left that pet's timer disabled for
the session and wrote nothing to the diagnostic log. It now writes the fault (exception, animation id and
name, step) to the log first, respawns the pet through Play() so the timer is re-armed, and on the third
fault closes the pet with a line saying so; a child is closed at once, since its parent's next step
decides whether another is spawned. Per session, not consecutive: a fault on every other tick would
otherwise respawn forever, and three faults in one session is a pet whose skin is broken.

**The kill fade seeds from the pet's current opacity and skips the discarded roll (2026-09-30, RA-235).**
Once AnimationStep passes a kill's last frame nothing replaces CurrentAnimation, so the end-of-animation
block ran on every fade tick: it rolled a next animation it then discarded (ten "no next animation found"
warnings per converted pet, ten "new animation" lines per sheep) and seeded the engine fade at 1.0 over a
kill whose own ramp had reached 0, so converted pets popped back to full and faded twice. The kill is
tested first in that block and the fade seeds from petOpacity: a kill that ended at 0 closes on the next
tick, esheep's 1.0 -> 1.0 kill keeps its ten-step fade. The arithmetic is KillFade.Seed/Advance in
RuntimeGeometry.cs so CoreTests can pin both cases.

**A window-grip release moves the pet by nothing that tick (2026-09-30, RA-236).** F263's comment claimed
the release tick no longer moved by the old velocity; bNewAnimation restored only the frame and the
interval. All five release sites zero x and y as the border transitions beside them always did, so the
comment is true and the F263 ratio invariant has a stricter twin that counts the zeroing.

**FormCompanion.Play has no `first` parameter (2026-09-30, RA-233, ACCEPTED-RECORDED).** Seven call
sites passed a value the body never read; two passed `true` believing it meant "first spawn of a
restored pet". The parameterless constructor stays and its comment says who reaches it
(RuntimeHardeningSelfTest, by reflection, to host the child-prune checks). F268's order pin was
re-pointed from `Play(false)` to `Play()`.

**ClearFullscreenStandDown is the clear-branch exit, and RelocateToDisplay repeats its steps inline on
purpose (2026-09-30, RA-239, ACCEPTED-RECORDED).** The shared method sets TopMost before anything moves
the pet, which on the still-blocked monitor would raise it over the game for the interval before Play()
moves it; relocation's TopMost and Visible have to be decided by Play() against the TARGET monitor. What
keeps the two exits in step is the F268 invariant pinning both bodies and the relocation's order, not a
shared body, and the summary now says so instead of claiming to be the one method for both.

**Comment drift closed in place, this lane's share (2026-09-30, RA-241, RA-244, RA-251, RA-217,
ACCEPTED-RECORDED).** SayWithDwell's constant-false `SetFullscreenSuppressed` argument is a literal
false with the reason (the stand-down guard above has returned); ProcessIcon's presence schedule names
nine probes over ~2.7 minutes and points at TrayIconPresence's constants instead of the retired
five-check / ~22 s figure; RemoteCatalog.SelfTest's scope says FortunePackLoadPolicy is one source-linked
definition since F124, so its comparison IS the governing cap against MaximumEntries; TSound.Data and
Animations.Dispose name AudioOutput and the SoundSink instead of the retired Sound module, AnimationSync's
summary says -1, and ProcessIcon.SetIcon no longer wears Display()'s summary. RA-217's four sites in
other lanes' files are listed in its BACKLOG line. The nine stacked `<summary>` pairs found across
FormCompanion, StartUp, AudioOutput, ContextMenus and Program (RA-238, RA-266) are unstacked, and a
source-invariant census over this lane's 35 host files holds the count at zero from now on; it cannot
see a summary duplicated onto a DIFFERENT member (the SetIcon case), which needed reading.

**The sprite sheet's proof stays a full decode (2026-10-01, N-petstudio-04, DECLINED-MEASURED).**
`CompanionXmlValidator.ValidateImage` proves the sheet with `Image.FromStream(stream, true, true)`, and a
header-and-CRC walk would replace that proof of decodability with a proof of integrity: a PNG whose chunks
and CRCs are intact but whose IDAT does not inflate would pass validation and fail later, at staging for a
companion or at preview in Studio, on paths that carry no validation message. The saving is bounded by
F318's measurement of this same decode, ~80 ms on the largest shipped pet, ~20 ms mid-size, ~1 ms for the
built-in, and since petstudio 1.1.18 Studio pays it once per analyze on a pool thread (BUG-012), so the UI
thread sees none of it. A pool-thread cost under 100 ms on a user-paced action does not buy a weaker
validator for downloaded content. The sound decode beside it (RA-271) was pure duplication with no proof
attached, and that one went.

**What is RUNNING and what was CHOSEN are two different pet ids, each with its own readers (2026-10-01,
RA-227, RA-265).** `StartUp.DefaultTypeId` answers the type running as the default (`animations.PetTypeId`,
which F305 rekeys to the built-in when the configured pet is rejected), and `PetTypeIdOf` prefers a pet's
own `PetTypeId` before anything else; the tray's "" mix entry reads the resolver, so a speech pick for the
sheep that is actually on screen is stored under the key the runtime reads back. `GetActivePetId()` stays
the PERSISTED choice and keeps its readers (the constructor, ReloadPetType, LoadNewXMLFromString, the
Companions pane's Active flag, `StartUp.ActivePetId`): on the fallback branch it deliberately names the pet
that was refused, so a later host that accepts it brings it back, which is the promise F305 made. The two
are not merged; they are told apart. CompanionHost.SpeechRoutingKey's no-pet fallback (lane burn/host-shell)
still reads the persisted id; with a pet in hand it reads the pet, so the tray and the runtime agree for
every pet on screen.

**DesktopWindows' live walk is a smoke print (2026-10-01, RA-230).** Its five live-walk assertions each
restated a line of `Snapshot` in the same class, and one (one foreground window) could not fail at all.
Firing the two that mean anything, the 64-window cap and the degenerate-rect filter, needs a desktop with
65 interesting windows or a zero-size top-level window present while the gate runs, so they were a wish
rather than a check and they are gone; the walk still runs, still prints eight rows, and still fails on a
throw. Making it testable means injecting the enumeration behind an internal delegate the self-test can
replace with synthetic records; that is a seam in a diagnostic with no production reader, recorded here
instead of built.

**The window-walk plumbing is one definition (2026-10-01, N-deadcode-07).** FullscreenScan carried a
verbatim copy of DesktopWindows' six P/Invokes, RECT, the enumeration delegate and both filters, kept
equal by hand. DesktopWindows keeps them (it is the general walk; the filters are the hard part F257
named) and FullscreenScan reads them through `using static`, which leaves the bare names in BlockedMonitors
that the N-host-04 invariant pins by text. FormCompanion.NativeMethods still declares GetWindowRect,
IsWindowVisible and EnumWindows of its own for the title-bar walk; that copy is F257's remaining third and
is recorded, not removed, because its signatures (HandleRef, CharSet.Auto) differ.

**The no-stage loader overload stays as a named seam (2026-10-01, RA-270, RA-272, ACCEPTED-RECORDED).**
`Xml.TryReadXml(xml, stageImages:false)`, the `!stageImages` branch and `Xml.ReadPngSize` have no
production caller: PetStudio's F155, the consumer F318 grew them for, adopted the validator's RootNode
instead. Deleting them is the register's own test-only-member rule and it crosses lanes in one commit: the
probe block at `src/dotNet/RuntimeHardeningSelfTest.cs:1080-1107` (lane burn/host-shell), the no-stage
clauses in `tests/runtime-hardening-selftest.ps1`'s fix/host block, the F318 case at
`tests/mutate-selftest-guards.py` ("the no-stage loader decodes the sprite sheet anyway") and the F155
payload beside it, which puts the overload back as its rejected shape and would stop compiling. Until then
the words are true (RA-269: the validator's proof decode of the sheet runs once per call; what the overload
skips is the loader's second decode and the tiling) and `ReadPngSize` says it is lenient BY CONTRACT
because the validator has already proved the container, and that a stricter reader breaks the probe.

**Every Modules pane restart goes through the save-then-restart helper (2026-10-01, RA-248, RA-249; corrected
2026-10-02 by lane feature/modules-update-all).** This entry used to record `Program.TryRequestRestartAfterSave`
as correct and unreachable, with the four `RestartToApply` sites calling it bare after marker writers that
swallowed their failure. Both halves were closed on 2026-10-01: the writers throw (RA-296, RA-318, the entry
under #### burn/host-shell above), and every restart site in `src/Portable/Wpf/ModulesPaneControl.cs` asks
through the helper, with the marker write (or, for a new install, the loadable-DLL check) as its `save`. There
are five sites since Update all (the row's update, the reinstall, the uninstall, the new install, and Update
all's single prompt, whose `save` is "at least one marker write succeeded"). `tests/runtime-hardening-selftest.ps1`
counts the helper calls against the restart sites and holds both at five, and `SecuritySelfTest.cs` still pins
the helper's own refusal (a `save` that answers false neither requests nor launches a restart). The alternative
the entry weighed, deleting the helper, is moot.

**UnicodeTextProgress is still two copies, deliberately for now (2026-10-01, RA-253, ACCEPTED-RECORDED).**
F358's mechanism applies unchanged: compile `src\DesktopAICompanion.ModuleKit\UnicodeTextProgress.cs` into
`src/DesktopAICompanion_Portable.csproj` beside AtomicFile and CrossSessionLock, delete
`RuntimeGeometry.cs`'s copy, add `using DesktopAICompanion.ModuleKit;` to FormSpeech.cs, Xml.cs,
Ai/ActiveWindow.cs and Portable/Wpf/AboutWindow.cs, and give CoreTests the same using or its KitUnicode
alias. The csproj and AboutWindow.cs belong to lane burn/host-shell, and half the change does not compile,
so it is recorded for one commit rather than half-done. The always-true `length < text.Length` term RA-254
removed from the host copy is still in the ModuleKit copy at `UnicodeTextProgress.cs:41`, same owner.

**RetiringValueRegistry.Count and FirstOrDefault are gone (2026-10-01, RA-252).** Test-only members under
the F289 rule; CoreTests' "Retiring pet runtime ownership" asserts exactly-once tracking through Add's
return values and the Drain snapshot, which is the production surface (StartUp reads Add, Remove and Drain
only). DesktopGeometry.SelectCaptureMonitor's foreground-overlap arm, named in the finding as the same
shape, stays as BUG-003(b) records.

**Four smaller shapes, decided (2026-10-01, RA-224, RA-226, RA-228, RA-237, RA-264).** `CompanionInfo.XmlPath`
went rather than becoming a TryReadPetXml argument, which would have flipped pet-XML resolution from the
pinned library-first order to AddFrom's bundled-first one. `CompanionTypeRegistry.Add` answers the existing
entry for a same-Xml re-add instead of copying its count, so the pair keeps one owner; no caller reaches
the branch today, and the runtime case belongs in CompanionTypeRegistrySelfTest (lane burn/host-shell's
file). ContextMenus.Dispose disposes the base items' Images, the bold Font and reads the icon under a using,
because F255/F256 set the bar at nothing held after Dispose. RA-237 (an alpha pet composited at the pre-move
position and then moved) belongs to F262's measured pass and stays with it, unmeasured and unchanged.
ReloadPetType's shuttingDown test sits in its first line, ahead of the staging, because the late block was
unreachable and would have declined after doing the work.

**The shipped ModuleKit and Contracts symbols name `/_/src/...`, never a machine (2026-10-01, RA-208).** Embedding
the PDB (the 2026-09-11 CodeView fix) removed the PDB's own path from the DLL and left the document table inside
the PDB naming every source file by the maintainer's absolute path, fifteen strings in each modules-dist zip, in
a compressed blob no byte-scan of the DLL can see. Both csproj files now set
`PathMap=$(MSBuildProjectDirectory)=/_/src/<project>`, the root a CI build's DeterministicSourcePaths would use,
so a release built here and one built in CI carry identical document names. The reader is CoreTests "ModuleKit
shipped symbols", which opens both built DLLs with System.Reflection.Metadata and fails on a drive-rooted name;
without that reader the PathMap line would be the F300 shape, a setting nothing can tell is missing. Mapping
in src/Directory.Build.props was declined: the host's Release build has no PDB at all (DebugType none), and the
tools and test projects do not ship.

**JsonSettingsStore's lease is re-entrant for the thread that holds it (2026-10-01, RA-212; RA-210 and RA-211
are the doc half).** CrossSessionLock's lease is a recursive Global mutex plus a FileShare.None handle on
`<path>.lock`, which is not recursive, while the store's `_processLock` Monitor is; so a `mutate` passed to Update
that called Load, Save or Update on the same store re-entered the monitor, waited the full 3 s for a file lease
its own thread held, and failed in silence. The store now records the lease owner's thread and a depth count
(touched only under the monitor), hands a nested scope back at once, and releases the real lease with the
outermost scope. The stated semantics: a nested Load sees the pre-mutation document; a nested Save is written
and then overwritten by Update's own save. Two doc corrections rode along: `LastLoadWasUnreadable` covers a file
that could not be read at that moment, not only a corrupt one, deliberately (a document this process could not
see is one it must not write defaults over), and `Save` writes over an unreadable document by design, because a
Save that refused would leave a module with a corrupt file no way to write fresh settings; `Update` is the verb
that refuses.

**RecordingHost registers, arbitrates and unregisters the way CompanionHost does (2026-10-01, RA-216, R-053,
RA-213, RA-215).** F233 restored priority arbitration for the drop and poke chains and left the speech chain
walking the public list in registration order with the priority discarded; it now sorts by priority with
registration order as the tie-break (one comparer shape for all three chains), skips the offer while
`SpeechEnabled` is off, treats a throwing responder as declined and logs it, walks a snapshot, and makes
`ShowBubble` one-shot. Every registration hands back a `Remover` whose Dispose removes the chain entry and the
public-list delegate, so a module Shutdown that forgets a Dispose is visible through the double for the first
time (RA-287 carries the same change to the three host-side fakes). `MemoryModuleSettings` and
`FakeModuleSettings` follow the host's two settings rules (ordinal keys, null stored as ""), which the
`IModuleSettings` contract comment now states. The principle under all four: a test double that is more
forgiving than the host it stands in for lets a module pass under the double and fail under the app, and the
ModuleKit doubles are the only host most module self-tests ever meet.

**A settings write from another thread during an open batch goes to disk through the batch's rollback point,
and an abandoned inner scope fails the outermost Commit (2026-10-01, RA-305, RA-306, RA-307, RA-308).** F361's
batch made N setters one durable write and documented a write-through from another thread as harmless and a
rollback as lossless. Neither held: the write-through saved the LIVE document, so the batch's uncommitted
setters reached disk ahead of their Commit (and stayed there behind a failed one), and the rollback restored
the pre-batch snapshot, so a pool-thread update stamp was lost from memory. `LocalData.Update` now applies such
a setter to the live document and to the rollback point, and writes the rollback point; a failed write restores
both. Deferring the other thread's write until the batch ends was declined: the setters' bools are read as
durable results, and a deferred write makes them lies, the same reason the batch is not a debounced writer. An
inner scope disposed without Commit now poisons the outermost (its Commit answers false and rolls everything
back), because the alternative, writing the outer's changes with half of the inner's, is a partial write no
caller asked for; no in-tree caller nests today, so the rule is cheap to state and expensive to discover later.

**Per-pet monitor pins are validated on load like every other persisted list (2026-10-01, RA-300).** The one
list `Normalize` left alone was the one read per spawn and per fullscreen tick. The display's UPPER bound is
still checked at read time only, on purpose: a pin to an unplugged monitor must survive the unplugging and
return with the screen, so the validator drops only what can never be a pin (an empty or unsafe id, a negative
display) and bounds the list.

**The host-side self-test fakes stop warming the Fortunes corpus, isolate their data root, and hand out
handles that unregister (2026-10-01; R-026 host half, RA-284, RA-283, RA-285, RA-259, RA-287, R-057).** An
empty settings store means smart picks ON, so every Fortunes load through FortunesEngineSelfTest,
FortunesModuleSelfTest and ModuleHostSelfTest warmed the whole corpus on every core, beside the engine probe's
own small warm; the three fakes' stores now seed `smartFortunes=false` and the self-tests assert the module's
smart status reads off after Init, through the status seam the pane reads (a state Init sets synchronously;
the fakes also record what modules log now, which they discarded before, but the "warming" log line is written
from the picker's pool thread after construction, so asserting its absence caught the mutation in one harness
run and missed it in the next, and that form lasted a morning). The one load
that still warms is ModuleHostSelfTest's PaneAttribution, which uses a REAL CompanionHost; seeding a real
module settings file under the isolated data root was declined as a format dependency for one load. The two
Fortunes flags and --hardening-selftest isolate `DESKTOP_AI_COMPANION_DATA_ROOT` the way F345 taught
--module-host-selftest to, each with the assertion that makes the override checkable; until then AiBrain's
Init-time migrator read the installed app's %LOCALAPPDATA% and the hardening freshness probe landed in a real
library. The Fortunes, AiBrain and Convention fakes hand back handles that unregister on Dispose, and each flag
asserts nothing remains registered after Shutdown: a double that cannot tell a forgotten Dispose from a kept
one is the same forgiving-double shape the ModuleKit entry above closes. Not changed (R-057): the shared scratch
root the fakes hand every module, left for F340's HeadlessHost base, where one GetStorage would carve
`<root>\modules\<id>` once rather than three fakes drifting on it.

**The embedded PetStudio fixture is held to the bundled graph from the host's side (2026-10-01, R-035 host
half).** The module's own self-test pins the bundled pet through a fixture it embeds, so the fixture and
src/Resources/animations.xml can drift apart with both suites green; --petstudio-selftest now analyses both
through the module's analyzer and requires the same animations and the same edges, naming every difference.

**The convention finder keeps no ReflectionTypeLoadException catch (2026-10-01, RA-289, ACCEPTED-RECORDED).**
A catch that NAMED the failure (no partial list adopted) was written for the host-exe fixture path and
withdrawn: F342's invariant pins the absence of any such catch, and the fixture types belong to the running
exe, whose assembly is already loaded whole, so the exception cannot arise on that path any more than on the
module path. The recorded rule stands unchanged.

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
matches `(FAIL|EXC|SKIP):` anywhere on a line and tests/mutate-selftest-guards.py grades only lines whose stripped form
STARTS with FAIL, EXC or SKIP, so a folded `rewarm_supersedes=FAIL` or `FILTER FAIL case=3` (no colon) was visible to a
person and to neither tool, and a folded `CUSTOM EXC: ...` was visible to the printer and not to the harness (RA-098,
lane burn/fortunes); the fold prefixes all of them (`name=FAIL`, `SUITE FAIL case`, `EXC:`, a suite-prefixed `EXC:`) with
`FAIL: `. A sub-report that wants its cases graded writes them in that vocabulary.

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

#### feature/modules-update-all

**Update all takes each module through the row's own path, asks every consent first, and asks to restart once
(2026-10-02, the owner's request).** "Update all (N)" sits in the footer beside "Check for modules online", the
pane-wide actions' row (the Companions pane's footer is the precedent), and is shown only while two or more
updates are ones a press would take; N counts exactly those. A press asks every consent the row's button would
ask (the same `PromptText`, the same caption) before the first byte of any module is fetched, then downloads,
verifies, stages and marks one module after another, each with the existing deadline and size cap, and asks to
restart once at the end through the same save-then-restart helper, whose `save` is "at least one marker write
succeeded". Interleaving each consent with its own download was declined: a question arriving halfway through
an unattended run is the one most likely to be clicked through. While the run goes, every Update, Reinstall,
Uninstall and Install button and the Check button are held (Check cancels the shared token; an install would ask
for a restart of its own), and Update all refuses to start beside a row's own download, an install or a check
in flight, because two stages of one id share a staging folder. A failure stops only its own module; what is
already marked stays marked and applies at the next start. Update all is a click and nothing else: no balloon,
no schedule and no restart reopen ever starts it, so the register's promise that nothing installs itself holds.

**No update path offers a build this host cannot run (2026-10-02, the coordinator's addendum).** Neither the row's
Update nor anything else asked `ModuleHostRequirement`, so an update declaring a MinHostVersion above this host
was downloaded, swapped in over a working copy and refused by the loader at the next start, leaving the module
unloadable with nothing to roll back to. The row now shows "vX needs a newer app: needs host Y or newer (this
host is Z)" in place of the button, and Update all neither counts nor fetches such an update and names it in its
result. One decision lives in `UpdateOfferFor` for both buttons, as the version rule lives in `ModuleUpdateScan`.
The weekly balloon still announces such an update (N-modules-update-all-01).

**A staged update shows as staged, and Update all leaves a module whose uninstall waits for the start alone
(2026-10-02).** A row whose update is staged (the marker names it and its payload is in the staging folder,
`PendingModuleUpdates.IsStaged`) says it applies at the next restart and offers no button; the row's own path
redraws the pane after staging so its button does not invite the same download twice, and Update all counts only
unstaged offers. A module with a pending uninstall (`PendingModuleRemovals.IsMarked`) is left out of Update all
and named, because its `MarkForUpdate` would unmark the removal (F352) and undo an uninstall the user asked for.
The row's own Update keeps doing exactly that on purpose: there the user picked the module, and the update winning
is F352's documented outcome.

**The pane's update path reaches past its window through `ModulesPaneSeams`, and only that path (2026-10-02).**
`--wpf-options-selftest` presses Update and Update all on panes built over fakes (module folders, staging and
markers under a scratch root, served downloads, answered questions, a headless pane counted as loaded), which is
the only way a press could be driven without a network or a window. `ModulesPaneSeams.Live` is the shipped
wiring and no running check can see it, so `tests/runtime-hardening-selftest.ps1` pins it by its arguments and
holds the update path to asking and downloading only through the seams. Install, Reinstall and Uninstall keep
their direct calls: no self-test drives them, and a seam nothing exercises would read as tested surface it is not.

**A carried action message belongs to its card and its label, and unsaved edits are measured against Load's own
answer (2026-10-02, the addendum's host fix).** `PaneView` keyed the result messages it carries across a
ReloadPaneAfter rebuild by label alone, so two cards each with a "Validate" showed each other's message
(Remembrance 2.0.0 has a "Validate" and a "Refresh local models" in both its Transcription and Summary groups);
the key is now the card ("group <name>" or "list <title>") and the label. Measured, not read, by
`--wpf-options-selftest` before the fix: a second ReloadPaneAfter action dropped the edit the first had put back
(the device field fell back to its stored value and Apply went grey), and an action after a ReloadOnChange
cascade on a Load-only pane dropped both of the cascade's edits. The cause was one value doing two jobs:
`_loaded` held what the build SHOWED, edits included, and served as the baseline the next action compared
against. `_stored` now keeps Load's answer from before the RA-331 overlay and the action merge, for
`HasUnsavedEdits` and for the baseline the stash hands on; `_loaded` still serves EnabledWhen. A LoadPending pane
has no such answer apart from its pending values, so the cascade-then-action case stays open there
(N-modules-update-all-02); no shipped module reaches it.
#### feature/aibrain-standdown

**Remembrance's busy flag is the stand-down's second reason, and it declines without releasing anything (2026-10-02,
aibrain 1.2.0).** The owner asked that AI Brain "falls back to off so it doesn't evacuate the Remembrance LLM on
accident in the middle of a transcription". While Remembrance publishes `remembrance.busy` (whisper or its Ollama
summary running) and the new switch "Stand down while Remembrance is transcribing or summarizing" is on, the drop, the
poke, the hotkey and the tray row are declined on the LOCAL slot exactly where the fullscreen guard declines them, so
Fortunes speaks instead. Unlike the fullscreen stand-down it sends no keep_alive:0: Addendum 1 of the lane's brief
withdrew the release, because AI Brain's default gemma3:4b on localhost:11434 is a tag Remembrance also offers on the
same server, so "release AI Brain's model" can mean "evict the model Remembrance is using", and the fullscreen guard's
per-check release would repeat that eviction before each chunk of Remembrance's map-reduce summary. A model AI Brain
loaded before the span therefore stays resident through it; that is the decision, not a leak. The fullscreen guard
(`FullscreenBlocked()`) and the bytes at its three call sites are unchanged: the Remembrance check is a second,
release-free predicate placed after it (`RemembranceBlockingPhase`).

**The flag is read at use, never subscribed to.** `ReadContext` at each decision, on the UI thread, which is the pull
the "IHost.ContextChanged is the push half" entry above settles. With no release nothing has to act on the
transition, so a subscription would only add a handler to detach and a thread to marshal (the host raises
ContextChanged on the publisher's thread). Reading at use also honours a flag published before AI Brain loaded. The
brief's first design (subscribe, release on the transition, unsubscribe in Shutdown) was refused for these reasons by
Addendum 1; the module self-test asserts the module never subscribes.

**While Remembrance is busy the fullscreen release is withheld as well.** A game starting during a transcription still
declines every remark, but `ReleaseModelForFullscreen` returns without unloading, for the reason the release was
withdrawn from the Remembrance reason: the id it would unload can be Remembrance's model. Neither reason lets AI Brain
send anything new, so what stays resident beside the game is only what was already there. The brief left the overlap
open; this reading of Addendum 1 was put to the owner and approved on 2026-10-02.

**An Apply while Remembrance is busy warms nothing and evicts nothing.** The rebuilt brain's preparation still starts
and probes the server (nothing loads) but skips the "keep" warm-up, and the retiring brain is disposed without its
eviction, including when AI is switched off mid-span. Decided when the Apply is issued (AiSessionManager's
`leaveModelsAlone`). A model the old brain kept resident is left to its own keep_alive: under "keep" that is
indefinitely, until something unloads it, because the next brain does not know the old brain's ids. The first ask
after the flag clears is a cold start.

**On a cloud slot nothing stands down; only a fallback from the cloud to the local slot waits.** The reason protects
the local GPU (Addendum 1), so a cloud provider's remarks, auditions and Test connection go ahead while Remembrance is
busy and the Status row reads "On.". The cloud+local composite asks before each fallover; while the UI thread's most
recent reading was busy it declines, the turn fails with the cloud's own error exactly as with the fallback switched
off, and the log says `fallback held back: Remembrance is using the local model`. "AI Brain kept talking to the cloud
while Remembrance was busy" is this decision. It differs from the fullscreen stand-down, which declines on every
provider. The fallover runs on a pool thread, where the flag is not read, so a turn that started before the flag rose
can fall over on the reading it started with: the in-flight rule.

**The explicit ask says why it was declined; the unprompted paths stay silent (owner decision, 2026-10-02).** The
hotkey and the tray row go through RA-060's explicit-path decline under a category of its own,
`ask declined: remembrance stand-down (<phase>)` ("busy" already means a turn in progress), and the companion also says
one line through the normal speech path: "Remembrance is using the model right now. Ask me again when it's done." The
drop and the poke stay silent and fall through to Fortunes, as they do for a fullscreen app. This differs from F067's
log-only rule for the fullscreen stand-down on purpose. F067 kept the hotkey silent because a companion stood down for a
fullscreen window defers a line and says it after the game has closed (FormCompanion.SayWithDwell, F278), and a bubble
over a game is the regression the watchlist names. Neither holds here: no fullscreen app needs protecting from a bubble,
and the companion stays on screen while Remembrance works, so a press that only logged read as broken. When both reasons
apply, the fullscreen refusal comes first and nothing is said. The lane first shipped this log-only, as Addendum 1 had
it; the owner overruled that when asked.

**A malformed or stale value fails open and is logged once per value.** The same value read at every later decision
adds no line; a recurrence after a clean reading is logged again. A phase the contract does not name is malformed,
not "busy with an unknown phase": the vocabulary is closed, a new phase is a contract change in both modules, and the
log line names the word. A value still carrying the superseded `since` in place of `at` fails open the same way, and so
does an `at` with no zone, since the contract says UTC.

**Freshness is eight hours either side of `at`.** The contract's bound is "an at older than 8 h is stale" (Remembrance
republishes at every phase change and before every summary request, and whisper's cap is 6 h). This reader also
treats an `at` more than 8 h AHEAD of its clock as stale, so a clock moved back after the publish, or a publisher's bug,
cannot keep AI Brain off for as long as the value stays ahead.

**An audition already running when Remembrance becomes busy finishes as it would have**, its end-of-run eviction under
"unload" included: in-flight work finishes (Addendum 1), and that eviction is decided on a pool thread where the flag
is not read. A press during the span is refused on the local slot with `⚠ Remembrance is using the local model right
now. Try again when it finishes.`, as is a local Test connection.

**Ungated on purpose: Refresh models (GET /api/tags or /models), the VRAM probe (GET /api/ps) and Shutdown's unload**
(Addendum 1). None of them loads a model, and Shutdown's unload is the one eviction the user's own exit asks for.

**The responders' Remembrance checks are pinned twice.** Ask carries its own copy, which also refuses a responder's
turn. While that refusal was log-only, deleting the drop's or the poke's own check changed nothing the module self-test
could observe (both mutations scored SURVIVED), so the order invariant under this lane's anchor in
tests/runtime-hardening-selftest.ps1 asserts each responder declines before it asks, the way the fullscreen invariant
pins `FullscreenBlocked()` in the same two bodies. Now that Ask's refusal speaks, a responder that lost its check makes
the pet say the explicit path's line instead of falling through in silence, so the module self-test's silent-drop check
sees it too, and both mutation cases are back in tests/mutate-selftest-guards.py.
#### feature/agentflow-hosts

**AgentFlow presses prompts, and observe-only was reversed at the owner's request (2026-09-18, recorded
2026-10-06).** AgentFlow 1.0.0 (2026-09-17) shipped the notify half alone, observe-only "by standing
decision", after four public auto-accept tools were read at source and every one pressed a wider grant than it
advertised. The owner then asked for the module to answer prompts, and the decision was reversed the next day in
four commits: 2c704d6 put the option classifier first, because the classifier is the safety mechanism; 555b83b
added the auto-approve switch and the three-state tray row; 3888b72 pressed the approve-once row over CDP, as
agentflow 1.0.2; b3e7190 pressed Codex prompts on 2026-09-21. This register never recorded the reversal, and
`docs/agentflow/README.md` went on saying "It does NOT answer it, by standing decision" until this lane corrected
it. What survived the reversal is the reasoning that produced observe-only: PromptOptions.Choose is an
allowlist that refuses a prompt it cannot fully read, presses by label and never by keystroke, and presses a
wider row only where the user has ticked one of the two opt-ins (`approveAllProjects` for Claude's "for all
projects" row, `approveSimilar` for Codex's "Allow similar commands").

**What the press does not check, and why that is not a defect.** `AgentFlowModule.Decide` reads the card's
labels and presses its narrowest approve-once row (or the opted-in wider row) on ANY prompt card it recognises,
whatever the user's permission rules say and whatever permission mode the session is in. The rules and the mode
are read only by the notify half, to predict whether a stalled call is a prompt. So in auto-approve mode it also
answers prompts that exist only because a managed `ask` rule requires a human to approve that call: a card is a
card. That is the switch's meaning, not a gap in it: "Approve prompts for me" delegates every recognised prompt
in the VS Code panels, the switch is off until the user turns it on, and the press budget and the repeat guard
are the brakes. A change that made the press consult the rules would be a new feature with a new decision
behind it, not a fix.

**The app a session runs in comes from an allowlist, and an unlisted value names nothing (agentflow 1.5.0).**
`AgentHosts` (`modules/AgentFlow/TranscriptReader.cs`) maps Claude's `entrypoint` (claude-desktop,
claude-vscode, cli) and Codex's `session_meta.originator` (Codex Desktop, codex_vscode, codex_exec) to six names
written in the module. A value it does not list, or a transcript that says nothing, keeps every line exactly as
it read before 1.5.0, which the self-test holds as a WITNESS. Codex's `source` is not a fallback: measured on
2026-10-06 the desktop app writes `source: "vscode"`, so a name taken from it would call the desktop app VS Code,
the one host the press reaches. Not measured, and worth knowing before trusting "in VS Code": `claude-vscode`
and `codex_vscode` name the EXTENSION, so the same extension in a VS Code fork would very likely write the same
value and read as "Claude Code in VS Code", reachable, while the press is pointed at one editor's argv.json and
debugging port. No fork transcript exists on this box to settle it; such a session would get the app prefix and
no "cannot answer" sentence, which is today's wording plus a name that may be the wrong editor.

**Codex's session_meta was never read before 1.5.0, and reading it changes a Codex bubble.** The record names
itself on the record's own `type`, and its payload carries none (all 71 rollouts on this box), but the reader
dispatched it on the payload's type, so the branch never ran. Fixing it for the app also delivers Codex's `cwd`
for the first time, so a Codex notice can now name its project folder. It is the same
`BlockedDetector.ShortProject` last segment a Claude notice has always carried, not a widening of what is said.

**"AgentFlow cannot answer prompts there" is said only to an auto-approve user, only about a listed app the press
cannot reach.** At HEAD before this lane, a blocked desktop session in auto-approve mode was announced with the
same generic quip as any other ("Your agent in demo is waiting on you"), so a user who had asked the module to
press saw no reason it had not. In auto-approve mode a session in Claude desktop, the Claude Code CLI, Codex
desktop or `codex exec` is now announced by name with the sentence that it is theirs to answer; a VS Code session
is not (the press reaches it, and a prompt it refused is announced from the screen with its own reason); in
Notify mode no app gets the sentence, because nothing is being pressed for anyone; an unlisted app keeps the old
quip, because a claim about where the press cannot reach needs a named place. The CLI and `codex exec` count as
beyond the press because the press reads cards in VS Code webviews and a terminal has none.

**A desktop session outside default mode stays as quiet as every other session there.** The stand-down that keeps
the notify half silent outside Claude's `default` mode and Codex's asking policies applies to the desktop apps
unchanged, and on this box that is most of them: all 47 Claude desktop transcripts on 2026-10-06 ended in `auto`
(46) or `acceptEdits` (1). Announcing them anyway would be wrong about 250 times per real prompt (the precision
`BlockedDetector` records), so this lane names the app in the stand-down log line and in the status line instead,
and leaves the supported route for those prompts to the `PermissionRequest` hook and `docs/IDEAS.md` idea 20.

**Where "the pane's status" names the app.** The module's status line (the tray's AgentFlow row) and the
'Check now' answer in the pane name the waiting apps; an app the scan could not name is counted as "and N more"
rather than dropped, so "2 agents waiting for you (Claude desktop, and 1 more)" cannot be read as two in the
desktop app. The pane's "Right now" row reports whether watching is active, not who is waiting, and is unchanged.
The tray's auto-approve row now reads "on, but cannot see the agent panel in VS Code" (its prefix kept, for the
capability log and the mutation case that pin it), because that row describes the press's reach and the desktop
app is not part of it. Decide's press note keeps its old wording: the lane leaves the press half's text alone,
and a prompt read off the screen is named through its subject instead.
#### feature/catalog-insight

**One bad catalog entry is refused alone; the rest of the catalog stays usable (2026-10-06, BUG-014).**
`RemoteCatalogClient.Parse` threw for the WHOLE catalog when any companion, pack or module entry broke a rule, and
every catalog feature shares that one parse, so the agentflow 1.5.0 description (1049 characters against a bound of
1024) took module updates, pack downloads and the companion gallery away from every installed app. An entry that
breaks a rule now goes into `RemoteCatalog.Rejected` with the rule, named by its id (or by its position when the id
is the problem), and into none of the lists, so it is never offered. The throw was not an integrity property, and
this was checked before changing it: the catalog is trusted because it comes over HTTPS from the project repository,
and every asset it lists is SHA-256-verified on download (`DownloadVerifiedAsync`); whoever can write catalog.json can
write a valid entry, so refusing an invalid entry's valid neighbours defends against nothing, tampering and downgrade
included. `TryParsePermissions` already records the same lesson one level down (an unknown flag once failed the whole
catalog). The ACCEPT SET is unchanged: each entry's rules are the clauses of the one condition the parser carried, in
its order, and an id refused for being unsafe or a duplicate still counts as seen. STRUCTURAL failures still refuse
the whole catalog, as a `CatalogRejectedException` naming the rule: not JSON, not UTF-8, not an object, a schema
version other than 1, a root `packLicense` past 256, a list longer than 512, a body past the 512 KB download cap. A
list that is present but is not an array is refused as a list (it used to read as empty, in silence) and the other
two lists are unaffected.

**The repository's catalog must still be refused by no entry at all, and the gate holds it there.** Every host
released before this change (v1.3.0 and older) refuses the whole catalog for one bad entry, and catalog.json on master
is read by all of them, so `--catalog-parse-file` exits 1 for ANY refused entry although the app itself now skips one.
The same reasoning makes LOOSENING a bound a compatibility change: the gate runs this tree's parser, so a bound raised
in `RemoteCatalog.cs` passes here at once while every released app still refuses an entry that uses it. Such a catalog
waits until the release carrying the new bound is the oldest one in use; `packaging/AppCatalogParser.ps1` says so.

**The check runs the app's own parser, never a copy of its rules.** `packaging/AppCatalogParser.ps1` runs the built exe
with `--catalog-parse-file`, which reads the file through `ParseBytes` (the door every fetched catalog takes) and the
launch check's refusing app-block read. Refused alternatives: a PowerShell mirror of the rules (two implementations of
one rule set is how a catalog and its checker end up both confident and both wrong, which is why
`ContentCatalogAssets.ps1` exists), and a CoreTests entry point (CoreTests recompiles listed sources, and the parser
pulls in `SecureDownload`, `CompanionXmlValidator`, `FortunePackLoadPolicy` and the Contracts enum). The price is a
built app: the helper compares the build's DLL with the parser's sources, the publish scripts rebuild the host when it
is older (`-BuildIfStale`), and the verifier refuses instead. It lives in `Test-ContentCatalogIntegrity.ps1`, which
both `tests/run-gate.ps1` and CI already run after their build, so CI gained the check with no workflow edit; each run
also proves the check can fail (a copy with a 1025-character description must be refused naming the module and the
rule, the same copy at 1024 must come out as the real catalog does). `New-ContentCatalog.ps1` judges the modules.json
entries before it hashes anything and the finished catalog in a scratch file before copying it over catalog.json;
`New-ModulePublish.ps1` judges the module's own entry before it builds, zips or commits. The early probes carry a
placeholder payload (the URL the generator writes, a 64-zero hash, one byte), so they refuse what the metadata decides
and leave the payload to the final check.

**The words.** A failed fetch is worded by its case (`CatalogText.FetchFailed`): a catalog reached and refused ("✗ The
catalog was reached at <time> but could not be read: <rule>. This is a fault in the published catalog, not in your
install; ..."), a server that answered an error status ("✗ Couldn't get the catalog at <time>: <status>. The server
answered, so your connection is working; ..."), and only a catalog never reached is "✗ Couldn't reach the catalog at
<time>: <reason>. Check your connection, ...". A TLS failure's "see inner exception" is replaced by the inner reason.
Everything a refusal echoes from the catalog goes through `CatalogText.Echo` (bounded, control, format, bidi and
zero-width characters as '?', a quote as an apostrophe), because the catalog is remote data shown in the app and
written to the log. The refusal note lists three entries and counts the rest; the log line (`[catalog] catalog read
with N entries refused ...`, once per parse) lists all of them.

**The Modules pane shows a problem panel, the owner's pick (mockup M2, approved 2026-10-06).** Its on-open fetch was
failure-silent on purpose ("a pane that cannot reach the network should render exactly as it did before"), and that
is the shape of the owner's report: on 2026-10-06 the pane showed nothing at all. Now a panel sits above the module
list while, and only while, something is wrong: red for a fetch that failed, amber when entries were skipped and the
rest works. Its title names the case, its rows give the entry (with the name and version the catalog gave it), the
rule, when ("Today at 14:02, when this pane opened", or which button was pressed) and what it means, whose last words
say whose fault ("Published wrong, not your install"); Try again is the Check button's own press and Copy details
copies the same words the log gets. In the one-entry-skipped case the affected module's row says "v1.5.0 is in the
catalog but its entry is invalid, so it is not offered (see above)", and the other updates still work. A clean read
takes the panel away; the footer line says "Checked today at <time>." in front of the counts, on open as well as on
Check. Placed differently from the mockup, on purpose: the refused case has no Entry row, because one bad entry is the
skipped case now and a whole-catalog refusal is structural (not JSON, a schema it does not read), so its Rule row
carries the reason; a fourth case, "Couldn't get the catalog", says the server answered with an error so the
connection works; the Rule row is the parser's own clause ("description is 1049 characters, the limit is 1024") so
the panel, the log and the gate say the same words; and the panel scrolls with the list rather than sitting in the
fixed header, so a long one never takes the list's room. The panel names only module entries (a refused pack or
companion is not this pane's business). The Companions pane keeps a status line, as the mockup's "elsewhere" view
has it: its on-open fetch stays silent on failure, as recorded there, but its Check line uses the same case wordings
and it names its own refused companion entries. The Fortunes "Available online" box builds its own text round the
host's message and is a module, outside this lane; N-catalog-insight-05 says what it should change.

**A weekly check is not stamped done while an entry of its kind was refused.** `RunPetUpdateCheckAsync` and
`EvaluateModuleUpdateCheck` used to reach their stamp only when the parse succeeded, because a bad entry threw into
their catch. With the entry refused alone they would have called the week's check complete without the refused entry,
which can be exactly the update the check exists to announce (it was agentflow 1.5.0 on 2026-10-06), and that offer
would then wait a week after the catalog was fixed. So each stamps only when no entry of its own kind was refused
(`RemoteCatalog.RefusedCount`); the module check still announces the offers it did find, so while a module entry stays
refused the balloon can repeat on each six-hourly tick. That is accepted: it lasts as long as the catalog is broken.
The launch APP-version check read "" from a catalog it could not parse and stamped "nothing newer" for a week; it
now uses a refusing read (`ParseAppVersion(json, true)`) and is not stamped, while an absent app block is still the
answer "" it always was. None of these paths caches a failure: the shared copy keeps a parse only when it succeeded.

**No last-good-catalog cache.** `docs/IDEAS.md` "Render the last known update offers with no network" records it as a
feature with two conditions (diff against what is installed now, show when it was seen), not a fix, so this lane did
not build it.

#### feature/settings-primitives

The host 1.4.0 settings primitives the approved layout mockups use (R2 Remembrance, AB2 AI Brain, F2 Fortunes;
`D:\tmp\dac-settings-mockups\settings-mockups.html`, section "Host primitives"). Every ABI member is inert when
unset; the members and their semantics are in `PluginApi.cs` and the table in `docs/module-authoring.md`.

**P1 (the action toolbar) was not built, on purpose.** The brief listed P0 to P7, but the page it names as the
spec says P1 is used by R3 and F3 only, both rejected (the mock's `bar:true` appears in `vR3` and `vF3` and
nowhere else), and the brief says the page wins where they differ. An ABI member no approved layout uses is
permanent surface with no adopter.

**Greying is opacity on the ONE element that is disabled, set by the theme.** A row greyed by `EnabledWhen`, or
a card body greyed by `CardEnabledWhen`, references `WpfTheme.DisabledOpacityKey`: 0.45 in dark (the mockup drew
0.42) and 0.6 in light, where Aero2's disabled editors already grey themselves and a stronger dim went faint.
Refused: a disabled trigger on each implicit style. It dims elements, not rows, so a radio option's TextBlock and
its RadioButton would multiply, and a ✓/✗ Info value with its own colour would not dim at all. A row greyed by
its own `EnabledWhen` inside a greyed card is dimmed ONCE (the row dims only while its card is live). With no
theme above it (a headless build) the reference resolves to nothing and the row stays at full opacity, which is
why the self-test draws its probes under `WpfTheme.AddDarkResources`/`AddLightResources` and reads the pixels.

**The dark Button has its own template, and that restyles every button in the dark settings window.** Aero2
hard-codes its state colours, so a disabled button was a pale F4F4F4 box (the Apply button whenever nothing is
unsaved, every action while it runs, every button in a greyed card) and a hovered one went light blue under
light text. The disabled values are the mockup's (surface, 777, 3A3A3D). The Modules and Companions panes' buttons
take the same template; they set only Width, Padding and Margin, which it honours.

**A disabled action button refuses a click in its handler.** Not "a disabled button cannot be clicked": a raised
event or an automation Invoke racing the greying must not run an action the card says is not used. It also
refuses a second run of an action still working, which the user could never trigger anyway.

**A collapsible card keeps the state it is showing across a rebuild, and a fresh open goes back to
`StartCollapsed`.** The state travels in a third one-slot stash beside the two rebuild stashes, filled before a
ReloadOnChange cascade, a ReloadPaneAfter action and the window's refresh after Apply, taken by the next Build of
the same pane, and taken back when a rebuild the view asked for is declined. The window's own refresh has no
decline branch: Apply runs synchronously on the view on screen, so it cannot be declined, and a branch no input
reaches is a guard no test can fail.

**A path field is not typed into, and it has a clear button the mockup did not draw.** The box shows a name, so
typing could only edit the name; a typed full path is the Text kind's job, and the two store the same string. The
clear button exists because a path once browsed to could otherwise never return to blank, which for a storage
folder means "the default". The box is a Border with a trimming TextBlock (colours from
`WpfTheme.FieldBackgroundKey`), not a read-only TextBox, because a TextBox cut a long name mid-letter.

**`ListCard.MasterToggle` acts on every item, the filtered-out ones included.** The row says "All" and counts
all of them; acting on a filtered subset would contradict its own count. It moves each item's own box, never
calls `SetChecked` itself, so a `DeferChanges` card stages its ticks exactly as for single clicks.

**P6 and P7 change what every list already renders, with nothing to opt into.** A `Detail` is a muted column on
the right (an item with none is built exactly as before), group headers read "12 of 18" instead of "(18)", and
an `EmptyHint` starting with ✓ or ✗ is coloured and boxed. Measured on master's own modules: no shipped
`EmptyHint` starts with either marker, so P7 changes no shipped pane; P6 changes the Fortunes and Reminder lists
that carry a Detail. The FullWidth fix likewise changes every full-width card's width: in the default window's
832 DIP pane a full-width card ended 72.9 DIPs past the last column before it (measured by the self-test's
rendered-card check with the fix mutated out), and AgentFlow's "Recently auto-approved" is one such card.

#### feature/cli-backend

**AI Brain and Remembrance can run on a coding-agent CLI, Claude Code or Codex (owner decision, 2026-10-06; aibrain
1.3.0, remembrance 2.1.0).** One at a time per module, chosen by a radio. When AI Brain's CLI option is chosen EVERY
AI Brain call goes through it: the drop, the poke, the hotkey, the tray row and both persona auditions, text and
vision alike. "If the feature is enabled, the user is well aware" is the owner's sentence, and it is why choosing the
CLI is the consent: the cloud-data consent switch gates the cloud provider slot only, and greys with it.

**One runner, in shared/CodingAgentCli, which both modules compile by source link.** The flags, the executable trust
rules and the failure wording are one policy; a copy per module would drift the first time a CLI moved a flag. It
lives outside both module folders because a module cannot reference another module (separate load contexts) and
src/ is the host's; a new folder under modules/ would read to the publish freshness check as an unpublished module.
Remembrance also links AI Brain's engine/AiExecutablePolicy.cs, the trust rules the runner applies, rather than
carrying its own. The freshness check follows both links, so a runner change makes both payloads stale: the truth,
since both DLLs change. A source invariant asserts the one copy and both links.

**The real binary, never the npm shim.** The npm installs are three shims in the global prefix (claude.cmd, .ps1 and
a sh script; codex the same); the runner resolves what they start, Claude Code's bin\claude.exe and the platform
package's vendor\...\codex.exe that codex.js spawns, and starts it with ProcessStartInfo.ArgumentList: no cmd.exe
quoting for a persona or a JSON settings argument to survive. Every candidate, the shims included, goes through
AiExecutablePolicy.ResolveConfigured (drive-qualified local paths, no reparse point on the way), so a relative,
UNC, mapped or linked location is neither probed nor run; a CLI installed under a junction reads as not installed,
and the Validate sentence says a linked folder is not trusted. A codex.exe started directly is given the two
environment values codex.js gives it (CODEX_MANAGED_BY_NPM, CODEX_MANAGED_PACKAGE_ROOT), so its own update and upgrade
hints behave as they do under the shim.

**Refused: a separate CODEX_HOME, or a copy of auth.json.** Codex refresh tokens are single-use (its binary carries
"your refresh token was already used. Please log out and sign in again"), so a copy that renews first signs the user
out of their own desktop, VS Code and CLI Codex. Codex runs on the user's existing login. A source invariant asserts
no code touches auth.json or sets CODEX_HOME.

**No model chooser.** Claude Code runs on its default (no --model). Codex is asked which models THIS installed CLI
may use: `codex debug models` renders the catalog its server hands it, with no model call, and the pick is the lowest
`priority` among entries whose `visibility` is "list", image-capable for a screenshot turn. Refused: reading
~/.codex/models_cache.json, which every Codex app on the machine rewrites with ITS version's list (a 0.160 desktop
app's list offered models the 0.145 CLI was refused). The pick is cached per installed binary, keyed by its path,
size and write time with its version beside it, in memory and in the module's folder, so the catalog is fetched once
per CLI version; a model the server refuses is forgotten and the next call asks again. `codex debug models` rewrites
that shared cache file as a side effect; that is the CLI's own behaviour, accepted, and it runs once per version.
*Superseded 2026-10-09 (AI Brain 1.5.0, Remembrance 2.3.0, `#### feature/cli-model-effort`): each module's Coding-agent
CLI card chooses a model and an effort for each CLI, with Claude Code's own default and Codex's automatic pick kept as
options, and the CLI row, AI Brain's Status card and Remembrance's Status line and summary header name them where they
said "its default model" or "on (Claude Code CLI)". The catalog rules in this paragraph stand.*

**The lean flags, and no session left behind.** The flags the brief measured (Claude Code 37,359 -> 6,262 input
tokens per screenshot question, Codex 21,521 -> 12,307, every answer still right), recorded at BuildArguments. Two
are pinned on every call shape by the runner's self-check: --no-session-persistence and --ephemeral, because AgentFlow
watches ~/.claude/projects and ~/.codex/sessions and would announce the companion's own calls as waiting sessions.
Refused: `claude --bare`, which wants an API key in place of the subscription login. Codex's global ~/.codex/AGENTS.md
cannot be dropped by any flag; the owner accepted that cost.

**Prompts always go on stdin.** A transcript is far larger than the 32k Windows command line, and stdin keeps a prompt
out of every process listing. Claude Code takes one stream-json user message (the image inline as a base64 block, so
there is no Read round trip; stream-json input requires stream-json output and --verbose); Codex takes "-" as its
prompt, with the screenshot and the instructions file in a per-call folder under the module's own folder, deleted
when the call ends. The stdin JSON uses the relaxed encoder: the default one escapes every '+' of a screenshot's
base64, a guard for JSON inside HTML, which a pipe is not.

**Every call runs in the module's own working folder** (<module data>\cli\work), so no project CLAUDE.md, .claude
settings or AGENTS.md from wherever the app was started is picked up, and Claude Code's per-folder record in
~/.claude.json gets one entry per module rather than one per call. The working folder is stable on purpose; the
per-call files are what is private and deleted.

**One model call at a time per module; an update only while nothing runs.** A probe (version, sign-in status, the
catalog) may run beside a call, never beside an update, because the update replaces the binary a running call
executes from. A refused call answers busy at once rather than queueing.

**Update CLI removes only npm's retired package folder, and only after the update has exited.** Measured 2026-10-06:
`codex update` cannot delete the codex.exe it runs from, so npm leaves its retired copy beside the live package (a
July one held 343 MB). The name is exactly @npmcli/arborist's retire-path.js shape, "." + the package folder + "-" +
eight letters and digits, matched exactly: the live package has no leading dot and can never match, nor can a near
miss. A folder that is a reparse point is skipped, never followed. An update stopped at its bound removes nothing,
because npm may still be working in that folder.

**Validate and the card's rows.** Validate makes one tiny call through the CLI shown on screen and answers in plain
words for each class the brief names (not installed, not signed in, sign-in expired with Codex's three refresh-token
cases, too old for its model meaning "press Update CLI", model refused, timed out). The classifier reads a newer-CLI
refusal first and an expired sign-in before a missing one, because the expired messages also say "sign in again". The
CLI card's rows are, in the order of the owner's approved mockup: CLI (name, version, "its default model" or Codex's
pick), Signed in as, Status (the last Validate with its time and duration), Goes through it. Signed in as is read off
the CLI: `claude auth status` JSON for Claude Code; for Codex `codex login status`, which names the method only
("Logged in using ChatGPT"). Refused: decoding ~/.codex/auth.json to name the Codex account: it is Codex's credential
store, not a file this module reads. The account is on screen only: no log line carries it, and the runner's log
lines are outcome words, exit codes, durations, token counts and model ids, never what a CLI said.

**AI Brain's radio has the mockup's four options (AB2): Local model, Cloud provider, Claude Code CLI, Codex CLI.** The
cloud dropdown lost "(none)", since the radio says whether the cloud is used; choosing Local model clears the cloud
primary as "(none)" did and remembers the provider in the new LastCloudProvider, so choosing Cloud provider again
restores it rather than defaulting. A pane caller that hands over no radio (an older one) still means local by
"(none)". A CLI choice leaves the slot underneath exactly as it was. An install that never chose has no CliBackend
key and reads its old slot. A first cut kept local and cloud as one option, to avoid a radio that could contradict
the dropdown's "(none)"; the approved mockup resolves that by removing "(none)", and that is what shipped.

**On a CLI nothing stands down for Remembrance.** The stand-down protects the local GPU, and a CLI loads nothing
there, so a CLI slot is neither local nor cloud and RemembranceBlockingPhase never applies; its switch greys. The
FULLSCREEN stand-down keeps working on a CLI and its switch stays live, though AB2 greys the whole Local server card:
while a game is fullscreen the pet is hidden, so a CLI call would be paid for a remark nobody sees, and a control that
is greyed while the module still reads it is the shape EnabledWhen's own comment rules out. *Superseded 2026-10-07
(AI Brain 1.4.0, `#### feature/fullscreen-per-monitor`): the pet is hidden on one monitor only, so a CLI now asks the
host whether the companion can be seen and no longer reads the switch, which greys with Local provider.*

**AI Brain's pane follows AB2 as far as this host renders it.** A full-width Status card pinned first; the AI brain
card (the switch and the radio) and the Coding-agent CLI card pinned beside it; Persona, Triggers, and "What it sees"
(Use vision moved there beside the OCR engine, because every engine takes the screenshot); then Local provider, Local
server (Ollama only) and Cloud provider, Fallback folded into it. The engine cards grey field by field (EnabledWhen):
AB2's whole-card greying, the visible disabled style and the path field for the OCR engine are host primitives that
do not exist yet, so "Choose OCR engine…" stays. A PaneAction has no EnabledWhen, so each one-engine button refuses in
words instead: Refresh local models and Test connection on a CLI, Refresh cloud models off the cloud ("✗ Not used
while the brain runs on Claude Code CLI.").

**AI Brain's Status card is one line, true when read** (owner, 2026-10-06): BrainStatusLine's answer, then what it
runs on (the CLI and its version, the cloud provider and model, or the local backend and model), vision on or off,
and this session's last remark with its time and duration, or why the last ask had no answer, by class. The brain's
LastFailure is a category, never an exception's message; a source invariant pins that and the record's place in
AskCoreAsync, which the module self-test cannot reach because its turns stop at AskSinkForDiagnostics.

**Remembrance's summary can go through a coding-agent CLI: the reversal of a shipped property (owner, 2026-10-06).**
"Local-only, no cloud STT or summary path, ever" was a shipped property of Remembrance (docs/IDEAS.md:77), and the
owner reversed it for the SUMMARY: the choice belongs to the end user. So it is an opt-in in Remembrance's own
settings ("Summary runs on": Local Ollama, Claude Code CLI, Codex CLI), off by default (an install with no such key
reads Local Ollama, and so does an id this version does not know), independent of AI Brain, and the CLI card says in
one sentence what leaves the machine. Transcription stays local Whisper: neither CLI accepts audio, and the recording
is the most sensitive thing the module holds. A CLI summary's file header names the CLI and the vendor the transcript
went to, where the local header says nothing left the machine. The coordinator edits IDEAS.md; PRIVACY.md needs the
same correction and is filed as N-cli-backend-01.

**One call, the single-shot prompt on stdin.** Measured 2026-10-06 (the lane's brief): both CLIs got every planted
decision, owner, open question and trap right on synthetic one- and three-hour meetings with Remembrance's own
BuildSingleShotPrompt, a decision reversed 2.5 hours later included. The map-reduce exists because a local model's
context is small; a CLI's is not, and one call sees the whole meeting.

**The one-call limit is 360 KB of UTF-8 transcript; over it, the local map-reduce summarizes instead, as before.** From
the brief's measurements, not a guess: the three-hour synthetic meeting was 162,039 bytes and cost Claude Code 60.3k
input tokens with about 6k of prompt overhead, about 2.95 bytes a token (Codex counted 45.6k, about 4). The binding
window is Claude Code's default model's, taken as 200k tokens; at a conservative 2.5 bytes a token 360 KB is about
144k tokens, leaving room for the overhead and the answer. At about 54 KB of transcript an hour that is more than six
hours of talk, and whisper's limit on one recording is six (Transcriber.MaximumWhisperTimeout), so a recording
practically always takes the one call and the fallback is for a long transcript summarized by hand. Bytes rather
than characters, because a tokenizer works on bytes. A transcript over the limit with no local model set is said in
the Status line (and refused by "Summarize a transcript…"), never dropped silently.

**remembrance.busy is NOT raised for a CLI summary.** The flag exists so AI Brain does not evict Remembrance's LOCAL
model; a CLI summary runs nothing on this machine's GPU, so keeping AI Brain stood down for the call would protect
nothing. On the stop path the transcribing span ends before the CLI call (whisper's part is over); "Summarize a
transcript…" through a CLI raises nothing; the local map-reduce, including the over-the-limit fallback, publishes
summarizing exactly as before.

**Remembrance's pane follows the approved mockup R2 as far as this host renders it.** The Status card pinned first at
full width, its summary part naming the engine ("summary: on (Claude Code CLI)", or the local model); Sources,
Hotkeys, Storage, Transcription; Summary (the switch and "Summary runs on"); Local Ollama, which is the old "Summary
(local AI)" renamed, its address, model and download choice greyed on a CLI; the Coding-agent CLI card, the same rows
as AI Brain's; and "Try it on a file", holding "Transcribe a WAV file…" and "Summarize a transcript…" under a Header
row (a card needs a field to exist). The radio option reads "Local Ollama", the mockup's text, not the brief's
"Local Ollama (default)". R2's path fields, its collapsible "Set up and check" cards and its whole-card greying need
host primitives that do not exist yet, so the Whisper buttons stay in Transcription, the four Ollama buttons stay in
Local Ollama and refuse in words on a CLI ("✗ Not used while Summary runs on Claude Code CLI."), and the mockup's
"Model to download if you have none" stays in Local Ollama rather than in a setup card.
#### feature/fortunes-index

**No manual control stayed, because no staleness trigger is left that the module cannot see (2026-10-06,
fortunes 1.1.0).** The owner asked why "Rebuild smart index" was a button and not an information block. Every
event that makes the index stale now rebuilds it on its own: a pack added, removed or edited through Import,
Rescan folder or Download selected (their own rebuild, as before), or outside the app while it runs (the new
folder watcher); a pack, genre, content-level, profanity or smart-picks change at Apply or a bulk Select
all/none (as before; the index depends on the pool, and the pool signature covers every one of them); a
failed build (retried once on its own, below); first run, an exit mid-warm, and a changed model or cache
format (Init's build, which resumes from the checkpointed vector cache, and the cache refuses a file whose
magic, dimension or asset fingerprint differ). The button's one remaining use, forcing a rebuild of an index
that stood down, cannot help in-process: a missing model, an oversized pool and an embedder that cannot load
are kept because a retry cannot change them (R-031's rule), and the pane names the action that does. Rescan
folder stays on the Fortune packs card, which this lane does not redesign: it is the answer for a folder that
cannot be watched (the Smart index line says when that is) and the user's way to ask a failed index to be
retried. Over an unchanged folder it builds nothing.

**The state is a line computed at every pane build, in two places (owner, 2026-10-06).** The Selection card's
"Smart index" row carries the full sentence; a Status card pinned full-width above every card (its first field
sets FullWidth and PinTop) carries the one-line summary the owner chose from mockup F2: "4,457 fortunes from 7
sources | smart index: up to date (built 14:02) | content: Clean + edgy". Both are Info fields, so the host
re-reads them on open, after Apply and after every ReloadPaneAfter action, and neither is a stored string the
way the button's result was (the host carried that message across rebuilds, so it went stale). An open pane is
not repainted when a rebuild finishes: that is the "module cannot push a live value into an open pane" gap
below, and the reason a line about work in progress says when it STARTED, so a reader can tell a snapshot from
a stall. The Status line opens with ✗ only when the companion would be silent (no engine, an empty pool); a
smart index that is off still leaves fortunes coming, so it is said in words. Not built here, by the
addendum: F2's pack-list changes (group counts, an "All" row), which need host primitives.

**The watcher waits for two quiet seconds and polls nothing (fortunes 1.1.0).** Every notification restarts
the window and one rebuild runs when it elapses; the rebuild builds nothing when the folder fingerprints as the
live provider read it, so a late or spurious notification costs a directory listing. Rejected: polling the
fingerprint on a timer (a listing for ever to learn what the watcher is told); a rebuild per notification
(nine copied packs raised nineteen notifications in the probe); a maximum wait, because a sync tool filling the
folder for a minute would then rebuild part-way and again at the end. Import and Download hold the watcher off
across their writes and their own rebuild, and a window that elapses meanwhile is re-armed rather than dropped,
so somebody else's change made during the action is still picked up after it. Single-flight: one folder rebuild
queued or running, and a window that elapses meanwhile marks one more pass. Only a real storage root is
watched; a host that hands the module no storage (the convention self-test's) watches nothing and says
nothing, because nothing writes packs into the TEMP fallback.

**A failed build is retried once, a minute later, and then waits for a change (fortunes 1.1.0).** A warm fails
today on an out-of-memory or an invariant breach inside the cache (R-031), a construction on whatever its
constructor throws. The first can pass once memory frees; the second fails again, and each attempt costs a
cache.bin parse and an ONNX session. Rejected: a backoff loop, which spends that on a deterministic failure
for ever. A build started for any other reason opens a new episode with its own one retry; Rescan folder over
an unchanged folder retries a failed index, a folder notification does not (it is not a request).

**Unchanged inputs build nothing at all (fortunes 1.1.0).** A provider records the settings it filtered with and
the folder fingerprint it parsed under; a Rescan, Import, Download or folder rebuild under equal settings over an
equal fingerprint builds no provider, no pool signature and no index, and logs nothing. Rejected: comparing the
new POOL with the old, which is what the button's guard did and needs the very parse this skips. Two blind spots
are recorded rather than fixed: an edit that keeps a file's length and last-write time is invisible to the
fingerprint (it was to the button too), and a model file replaced under a running app is not noticed, because
the asset fingerprint is computed once per process; model updates arrive as module updates, applied at restart.

**One gate per shutdown path, so each can be seen to fail (fortunes 1.1.0).** A rebuild that finished after
Shutdown used to publish a provider into the module and start a smart build nothing disposed; the watcher made
that reachable without a click. PublishEngine refuses once the module's token is cancelled, ScheduleSmartPicker
refuses once `_shuttingDown` is set (under `_smartLock`, the interleaving pinned in the invariant script), and
an elapsed quiet window starts nothing. The retry and failure paths carry no check of their own on purpose:
their outcome is already decided by the gates above and by Shutdown clearing the provider, and a second check
there could not be made to fail by any test.

**A failed catalog check is told apart by the exception's type NAME (2026-10-06, fortunes 1.1.0,
N-catalog-insight-05).** "Check online for packs" said "Couldn't reach the catalog" for every host error,
including a catalog that was reached and refused. CatalogRejectedException (host 1.4.0), InvalidDataException
(host 1.3.0) and JsonException now read as reached but unreadable, the published catalog's fault;
HttpRequestException, TimeoutException and the cancellations an HttpClient timeout raises read as no answer;
anything else reads as a failed check with no cause claimed. By name, not by `is`, because the module compiles
against the 1.0.0 ABI and a type check would need host 1.4.0's assembly: MinHostVersion stays at 1.0.0. One
blind spot is recorded: host 1.3.0 also throws InvalidDataException for a malformed catalog URL override, a local
setting, which this words as the publisher's fault.

#### feature/layout-aibrain

The owner's approved mockup AB2 for the AI Brain pane, built on host 1.4.0's settings primitives (aibrain 1.3.0,
same version, not yet published; MinHostVersion 1.4.0, so the catalog entry's minHostVersion moves with the
publish). The Local provider, Local server (Ollama only), Cloud provider and Coding-agent CLI cards each carry ONE
CardEnabledWhen on their first field and grey whole, buttons included, with the host's "Not used while ..." line.

**The fullscreen stand-down left the card AB2 drew it in.** AB2 puts it in Local server, which greys whole on a CLI,
and the module still reads it there: a remark during a game is declined on every engine, because the companion is
hidden and a CLI call would be paid for an answer nobody sees (feature/cli-backend kept it live for that reason). A
greyed card disables its whole body (`OptionsWindow.DressCard` sets the body's IsEnabled), so no row inside one can
stay live. It sits in Triggers, under the Ask hotkey its label names. Refused: Local server left ungated with a
per-row EnabledWhen on its other rows, which is AB1's shape and leaves the card's buttons pressable; and a host flag
for one live row in a greyed card, an ABI member for a single field and outside a module lane. Its label keeps
"releases VRAM", true on the local slot and the cloud's fallback; on a CLI nothing is resident to release.
*Superseded 2026-10-07 (AI Brain 1.4.0, `#### feature/fullscreen-per-monitor`): a CLI no longer reads the switch, so
it moved to Local provider, not back to AB2's Local server, which is Ollama's card.*

**Local provider has a Test connection of its own.** AB2, like AB0 and AB1, draws the one Test connection in Cloud
provider, and it tested whichever slot was active, so on the local model it was the local slot's only test. Greying
Cloud provider whole off the cloud would have taken it from every local install. Each slot card's button now tests
its own slot: Local provider's the local model (on the cloud, the fallback), Cloud provider's the cloud, refusing
off the cloud as Refresh cloud models does. The host keys a result by card and label (`OptionsWindow.cs:1219`,
`1800`), so the two share the label. The Remembrance hold reads the card (`localSlot`), not the settings: read from
the settings it would let the new button send a chat to the local model mid-transcription while the cloud is primary.

**The OCR engine is a path field, and "Choose OCR engine…" is gone (P4, which AB2 marks optional).** The 177 DIP
editor showed only the tail of a real Tesseract path; the field shows the file's name, its folder under it and the
whole path on hover, and its Browse is the same host dialog on .exe the button opened. The button was also the one
action in the pane that wrote the settings file behind Apply's back. What it did stays reachable: the pick is an
unsaved edit, Test OCR (pending-aware) answers for it before Apply, and Apply rebuilds the live brain, which
resolves the engine from the path it is built with (the module self-test pins all three). The cost: the box is not
typed into (the Open dialog takes a pasted path), and choosing no longer tests itself in the same click. The label
is "OCR engine (tesseract.exe)", not AB2's "(used when vision is off)": the poke reaction always reads text, and the
host titles the Browse dialog with the label. AB2 draws a blank path with a second muted line ("usual install
folders, then PATH") and a tooltip of its own; the host has one EmptyHint for the box and its tooltip and hides the
folder line while blank, so the hint is the mockup's box text, "(auto-detect)", which reads whole in the box where a
longer one would be cut.

**No row carries an EnabledWhen of its own; the card's gate is the only copy.** Refused: keeping 1.3.0's per-row
EnabledWhen beside the card's, harmless on screen and a second copy of the condition that can drift from the first,
the reason the primitives lane gave for one string per card. The buttons' refusals in words stay as the second line
of defence; the host also refuses a click on a disabled button.

**An existing settings file is checked byte for byte through the pane.** The module self-test writes a file with
the module's own serializer, every field the pane shows off its default, in three engine states (a CLI over a cloud
slot, the cloud, the local model with a cloud provider remembered), starts the module on it, hands Save exactly what
the host would (every editable field, greyed ones included; no Info or Header row; the blank secret absent), and
requires the same bytes. A path field stores what the Text kind stored, so the OCR engine needed no migration.
#### feature/layout-remembrance

**Remembrance's pane is the whole of mockup R2 on the host 1.4.0 primitives (remembrance 2.1.0, MinHostVersion 1.4.0).**
This supersedes the last two sentences of feature/cli-backend's "Remembrance's pane follows the approved mockup R2 as far
as this host renders it". The storage folder, the whisper-cli path and the Whisper model file are path fields; the setup
and one-off buttons sit in three collapsible cards after the settings ("Set up and check Whisper", "Set up and check
Ollama", "Try it on a file"); Local Ollama and Set up and check Ollama grey whole on a CLI, and the Coding-agent CLI card
greys whole off one. A settings file needs no migration: a path kind stores exactly what Text stored, every field keeps
its id, and the module self-test runs an existing 2.1.0 file through an untouched Apply and finds every key as it was.

**One condition per card, not one per row.** Under host 1.2.5 the Local Ollama rows and the CLI card's rows each
carried their own EnabledWhen. They are gone: the card's CardEnabledWhen greys every row and every button in it, and a
copy on each row would be a second statement of one rule, free to drift from the first. The buttons' own refusals
(CliRefusal's "✗ Not used while Summary runs on …", SummaryRoute.PickACliFirst) stay, as the brief asks, behind the
host's greying; only a caller past the host can reach them now, which is what the self-test does.

**The three "Browse for…" buttons are gone, and choosing a path no longer saves it at once.** "Browse for a storage
folder…", "Browse for whisper-cli…" and "Browse for a model…" opened the module's own dialog and saved the choice on
the spot. The path field's Browse opens the host's dialog, filtered the same way (.exe, .bin, a folder), and puts the
choice in the field as an unsaved edit that Apply saves, like any other edit; that is the primitive's design, recorded
under feature/settings-primitives, and not something a module can opt out of. Every answer that sent a stuck user to
those buttons (the Whisper Validate, Open the download pages, the installer's blocked, throttled, stalled and
no-Windows-build failures) names the field's "…" instead, through one phrase, WhisperInstaller.ChooseFilesByHand.

**A setup card opens itself only for what is missing, judged from what is saved.** Both start closed. Set up and check
Whisper opens while the saved whisper-cli and model are not both on disk (the Status line's "Whisper: not set up");
Set up and check Ollama opens while the summary is on, runs on Local Ollama, and the model dropdown has nothing but
its no-models placeholder. Refused: opening the Ollama card whenever no model is known, summary on or not, because
the summary is off by default and most installs have no Ollama; the card would open for nearly everyone. Refused:
asking Ollama at pane open to decide; Load already fires its one discovery probe, and this reads what that and the
saved settings already say. While the pane stays up the host keeps each card as it is showing.

**Placed differently from R2, and why.** A blank storage folder reads "Documents\Remembrance (the default)" on one
muted line, where R2 drew "Remembrance" in the box and "in Documents (the default)" under it: the host's EmptyHint is
one string, shown in the box and used as its tooltip. The two Whisper paths have a blank hint R2 does not draw (it
shows them filled): "Not set: "Set up Whisper for me…" fills it in". Set up and check Ollama takes R2's button order,
Get Ollama first, so "Download that model" with nothing answering now says Get Ollama is "in this card" where it said
"below". The ✕ clear button inside each path field is the host's (feature/settings-primitives), not R2's.
#### feature/layout-fortunes

The owner's layout F2 for the Fortunes pane (`D:\tmp\dac-settings-mockups\settings-mockups.html`, approved
2026-10-06) on the host 1.4.0 list primitives, fortunes 1.1.0, MinHostVersion 1.0.0 -> 1.4.0 for
`ListCard.MasterToggle`.

**Three "All" rows replace the six Select all / Select none buttons, and a bulk choice waits for Apply again
(2026-10-07).** Fortune packs and Available online open with "All packs", Genres with "All genres". The row moves
each item's own box (feature/settings-primitives), so the module hears a bulk choice exactly as it hears single
ticks: on the two DeferChanges cards, `SetChecked` once per changed item at Apply, folded into Apply's one write and
one rebuild; on Available online, `SetPackSelected` once per item at once. That reverses burn/fortunes' decision
above for the bulk case, on purpose. The buttons saved at once because the bulk choice they staged lived in the
MODULE, where Cancel could not discard it and the host could not arm Apply for it (RA-121, RA-122). A row click is
the HOST's pending edit: Apply lights up, Cancel and a ReloadPaneAfter rebuild discard it like any unapplied tick,
and the module never holds an unapplied bulk choice at all. One consequence reads as a defect and is not: an "All
packs" untick followed by Rescan folder or Import, before Apply, is thrown away, as a single untick and a field edit
are. Refused: committing the row's ticks at once (the host calls `SetChecked` per item, so that would be one write
and one rebuild per pack, the cost DeferChanges exists to avoid), and keeping the buttons beside the rows (two
controls for one thing, the six the owner asked to lose). The group headers' "12 of 18" and the muted line-count
column are the host's rendering of every grouped list; the module sets nothing for them.

**A failed "Check online for packs" empties Available online and shows itself there, as the card's red block
(2026-10-07).** The block is the card's `EmptyHint`, which host 1.4.0 colours and boxes when it starts with ✗ (P7):
four lines, the case ("✗ Couldn't reach the catalog", "✗ The catalog was reached but refused", "✗ The catalog check
failed"), the host's reason, whose fault it is in the Modules panel's words (`CatalogText.ProblemForFailure`), and
"Checked today at 14:02. “Check online for packs” tries again." Nothing is said beside the button, which sits right
under the block. The list is emptied because the host draws an `EmptyHint` only over an empty list, and because
what an earlier check listed is no longer known to be on offer: Download selected reads the same shared catalog
fetch (`CompanionHost.DownloadCatalogItemAsync`), so a stale list invites a download that fails the same way; the
ticks go with it. Refused: keeping the list with the failure in the button's line (the cramped sentence F2
replaced). The time is worded when the pane is DRAWN, in `LoadAvailablePackItems`, which the host calls right before
it reads the hint on every build (`OptionsWindow.BuildListCard`): a block left from yesterday says the date, never
"today". A list card has no other per-build hook; a host that read the hint first would be one build behind on the
date and never on the failure. A check that works puts the plain hint back.

**Where the pane differs from the drawing of F2, and why.** The full-width Status card stays pinned above every
card: the owner asked for it after F2 was drawn (feature/fortunes-index). The red block is one colour with no bold
title and no separate muted time line, because P7 colours a hint as one text; the four lines are separate lines.
The refused block names a rule and no "Entry:" line: F2 drew the 2026-10-06 outage, where one bad module entry
refused the whole catalog, and since feature/catalog-insight a bad entry is skipped alone, so a pack check that
fails as refused has only a whole-catalog reason, and the module sees nothing but the exception. The unreachable
block says "What failed:" with the host's own message rather than composing a sentence about the host name. Button
names inside the block are in the module's curly quotes, as every other status of this module. Screen & Stage is
not drawn open: the pack list keeps `CollapseGroups`, which F2's counts are what make bearable. Import your own…,
Open fortunes folder and Rescan folder stay on Fortune packs, as F2 has them (F1's "Your own packs" card is not
built).

#### feature/remembrance-delete-logging

**The rule (owner-approved 2026-10-07): every deletion of the user's data writes one metadata-only log line (what
kind, how many, and on failure the error's type), and a failure is never silently swallowed. Temp, cache and
self-test scratch cleanup is exempt.** Applied here to Remembrance (2.1.0, same version, MinHostVersion 1.4.0). It
came from a day the log could not answer a question: a test instance had been pointed at the owner's real
Documents\Remembrance, and whether its purge had deleted anything could be told only by inspecting the folder. The
five sites outside this module that delete user data with the same silence are BACKLOG N-remembrance-delete-logging-01
to -05, each with what it deletes and how it is silent today.

**The purge says what it did in one line, and says nothing when nothing was due.** `CaptureStore.Purge` returns a
`DeletionReport`: recording audio files, snapshots and empty folders removed, then files it could not delete, empty
folders it could not remove and folders it could not list, with the exception type names. `RunPurge` logs `purge:
removed 3 recording audio file(s) and 2 snapshot(s) older than 72 h, and 2 empty folder(s)`, followed after a
semicolon by any failures (`could not delete 1 file(s) (IOException); it is tried again at the next purge`), and a
purge that throws logs `purge failed: <ExceptionType>` where its catch was empty. A pass with nothing due writes
NOTHING. That silence can read as a gap and is the design: the timer fires a minute after start and then hourly, and
an hourly "nothing happened" buries the line that matters. A failure is never silent, so a storage root this process
may not list says so on every pass. RunPurge's catch is pinned by a source invariant as well as the self-test.

**By exception TYPE, never Message, and never a name.** A capture's folder and file names carry the meeting title
from the calendar, and an IOException's Message names the path it failed on, so `DeletionReport` keeps
`GetType().Name` and nothing else, and no line names a file or a folder. The storage location is already on the pane.
Refused: naming the capture beside the count. It would put meeting titles in diagnostics.log, the file SUPPORT.md
asks a user to attach to an issue. The self-test plants one title in every name it creates and checks every line
this lane writes against them.

**Where the recorder's scratch WAVs live, and why the purge is their backstop.** `AudioRecorder.Start` builds the two
per-source tracks beside the output WAV: `recording.system.wav` and `recording.mic.wav` in the capture's folder (per
capture), or `<base>.system.wav` and `<base>.mic.wav` in the day's folder (by date), under the storage root and never
in %TEMP%. Both are purge shapes (F171, `NamesThisModuleWrites`), so a track the stop could not delete goes with the
rest of that capture's audio at the 72-hour purge, and the line says so: `stop: could not delete 1 scratch track(s)
(UnauthorizedAccessException); it stays in the capture's folder until the 72-hour purge removes it`. No start-time
sweep was added, because the purge already reaches every place the recorder writes. The purge walks only the storage
location set NOW, though, so a scratch under a folder the user has since moved away from is reached by nothing, like
every recording there (N-remembrance-delete-logging-06).

**A recorder delete that WORKS writes nothing; one that fails is logged.** After the mix the two raw tracks are
deleted, and a header-only track is deleted by a stop that captured nothing and by a failed start. These are the
recording's own working files: the audio is in recording.wav, or there was none. Under the rule's temp exemption a
success is silent, where logging it would add a line to every stop. A failure is logged with its count and type,
because then raw audio stays on disk. The empty folder a failed start made seconds ago is treated the same way: its
removal is silent, and a removal that fails is logged (`start: could not remove the empty folder this start made
(IOException); it holds nothing`). The two stop paths' catches log the scratch line too, since a mix that throws can
follow a header-only delete that failed. Those two call sites are the one part the self-test does not drive (it
would need two fake sources with different packet gates, which FakeDevices does not offer); the line they write is
the tested builder.

**Every delete in the module, and what it does now.** The purge (`Purge`, `PurgeFiles`, `PurgeDayOrSnapshotFolder`,
`TryRemoveEmptyCaptureFolder`): one line per pass that has anything to say. `IsOlderThan`'s catch is a read, not a
delete, and a folder it cannot date is simply kept. The recorder (`MixToWhisperWav`, `DeleteIfEmptyRecording`) and
`StartRecording`'s folder removal: failures logged, successes exempt, as above. `Transcriber.RunWhisper`'s delete of
`<stem>.whisper.txt` (`Transcriber.cs:137`, an empty catch) is left as it is, as temp: it is whisper-cli's own output,
read whole into the transcript the module keeps for ever, so a failed delete leaves a second copy of a permanent file
and breaks no retention promise. `WhisperInstaller`'s deletes (a download's `.part`, the verify probe's scratch
folder) are download cache and temp, as are the shared CLI runner's per-call files. Self-test scratch is exempt.

#### feature/cli-token-and-locate

Two changes to the shared runner (`shared/CodingAgentCli`), shipped as aibrain 1.3.1 and remembrance 2.1.1 with no host
release: BUG-015 (the CLI card said "Claude Code is not installed" on the owner's other workstation, with Claude Code
installed) and the owner's request of the same day for a module-held sign-in token.

**The locator reads the installers' own folders, and never follows a link.** Both CLIs ship as WinGet portable
packages (manifests Anthropic.ClaudeCode 2.1.292 and OpenAI.Codex 0.161.0). WinGet unpacks the binary into
`%LOCALAPPDATA%\Microsoft\WinGet\Packages\<id>_<source>` (machine scope: `%ProgramFiles%\WinGet\Packages`) and reaches it
either through a symbolic link in `WinGet\Links`, which the trust rules refuse as a reparse point, or, where WinGet may
not make links, by adding the package folder to the saved user PATH, which an app started earlier never sees. Refused:
following the link. The trust rules exist so that no candidate is reached through a reparse point, and reading the
package folder directly needs no exception to them. Codex's binary there keeps its release name
(`codex-x86_64-pc-windows-msvc.exe`). The order is the process PATH, the PATH saved for the machine and the user now
(read from the registry each time, so a host started before an install still finds it), npm's prefix, Claude Code's
own installer, WinGet, and last the copy inside VS Code's Claude Code extension: the newest version this machine can
run, its own architecture first, never another platform's. That copy is a real Claude Code (measured 2026-10-07:
`--version` 2.1.292 and `auth status` answer headless from it, on the same sign-in as the CLI), and last because it is
the extension's.

**Update CLI leaves an install another program updates to that program.** On a WinGet install the CLI's own update
answers "up to date" whatever the version (the setup docs), and VS Code replaces its copy with the extension, so Update
CLI starts nothing there and says what does the job (`winget upgrade Anthropic.ClaudeCode`, run while no Claude Code is
running, since Windows locks a running program). The card's CLI row says where a binary came from when that decides
who updates it ("installed with WinGet", "the copy inside VS Code's Claude Code extension").

**The sign-in token goes to this module's Claude Code children and to nothing else.** Claude Code reads a `claude
setup-token` token from `CLAUDE_CODE_OAUTH_TOKEN`, so the runner sets it on the Claude Code children that need a
sign-in (model calls, and `auth status` so the card describes the sign-in the calls use; in 1.3.1 every probe and the
update carried it too, see below) and takes off the variables that outrank it
in Claude Code's credential order (`CLAUDE_CODE_USE_BEDROCK`, `_VERTEX`, `_FOUNDRY`, `ANTHROPIC_AUTH_TOKEN`,
`ANTHROPIC_API_KEY`), because a token saved here is the user's declared choice for these calls. An `apiKeyHelper` in
the user's settings also outranks it and cannot be taken off a child; the Signed in as row then names that method.
Refused: a user environment variable, which would outrank the user's own /login in every Claude Code on the machine;
and a private `CLAUDE_CONFIG_DIR` for the module, the "clone auth" sandbox the owner declined on 2026-10-06, which would
also step around an organisation's requirement that its managed settings load. A source invariant holds that
`ApplyClaudeToken` is the only writer and that nothing calls `SetEnvironmentVariable` with the name.

**Sealed where the runner works, never in a settings file.** The token is DPAPI-sealed for the current Windows user
(with an entropy string of its own) in `<module data>\cli\claude-token.dpapi`, written by `AtomicFile`; the settings
pane's Secret kind only controls how a value is shown (PluginApi.cs's IModuleSettings note), so a token handed to the
settings store would be cleartext JSON. Load hands back "set" or "", never the token. It is checked for what cannot
work and not for a prefix Anthropic may change: empty, a space inside, more than 4,096 characters, an Anthropic API
key, and since 1.3.2 a web address and any character RFC 6750 does not allow in a bearer token (letters, digits and
`- . _ ~ + /`, `=` at the end). That tightening followed the owner's 1.3.1 install, which saved a 46-character web
address as its token on 2026-10-07: every Claude Code start then failed on it, Update CLI included, while two
Validates of a token typed and not applied answered with a tick. A value saved before that check and not a token now
reads as `NotAToken`: the card and every call say so (`TokenNotAToken`) and nothing is sent with it. A token this
account cannot unseal stops a call before it starts (`TokenUnreadable`), because running on the CLI's own sign-in
instead would put the call on an account the user did not choose. Since 1.3.2 the token goes on the model calls and
`auth status` alone: `--version` and `update` need no sign-in, and a refused token stopped the update before it began.
**Since 1.3.3 Validate SAVES a typed token that answers**, and says so; one that does not answer is not saved. 1.3.2 said
"press Apply to keep it", which could not be followed: Validate rebuilds the pane (ReloadPaneAfter) so the card shows
what it learnt, the host's rebuild puts unsaved edits back but never a secret into its box (the Secret editor shows a
saved value as a tooltip only, and `Collect` skips a blank secret as "keep the stored one"), so the typed token was
gone and Apply had nothing to save (the owner, 2026-10-07: "apply did not become clickable after validate was
pressed"). The token is therefore the card's one value that does not wait for Apply, as Remove token already did not.
The host behaviour itself is BACKLOG N-cli-token-04. Validate tests a token typed and
not yet applied, as it tests the CLI chosen on screen. Remove token deletes the file at once and logs the delete either
way, under the delete-logging rule above. Each module keeps its own token, because a module cannot read another's
folder, so a user who runs both on Claude Code pastes it twice.

**What a token can and cannot tell the card.** Measured 2026-10-07 with a FAKE token on Claude Code 2.1.292: `auth
status` answers `authMethod: oauth_token` and names no account, so the Signed in as row says the card's token is in
use and that Validate is what proves it. A refused token ends a call with "Failed to authenticate. API Error: 401
OAuth access token is invalid."; on a machine whose organisation requires remote managed settings it ends sooner,
with "... could not be loaded. Run `claude auth login` to re-authenticate", which the runner now classes as an expired
sign-in. With a token in use, both say to replace or remove the token rather than to sign in with /login. A token does
not make `--bare` usable: bare mode never reads it (the authentication docs).

**One row gate narrower than its card, and the layout check that allows exactly that.** The token row carries its own
EnabledWhen (Claude Code CLI) inside the CLI card, whose gate is either CLI, because Codex never reads it and the
schema's rule is that a field is never live while nothing reads it. AI Brain's layout check refused every row
EnabledWhen, to stop a copy of a card's gate that could drift from it; it now refuses any that is not STRICTLY
NARROWER than its card's, on the same field, and a WITNESS names the token row as the one such row.

#### feature/remembrance-cloud

Remembrance 2.2.0, module-only. The owner, 2026-10-07: "it looks like we forgot the 'runs on' Local Model, cloud
provider, claude cli, codex cli box". Mockup R2 drew three engines for the summary (Local Ollama and the two CLIs), and
2.1.0 built those; AI Brain had four. This adds the fourth.

**AI Brain's four engines, in its words and its order.** "Summary runs on" offers Local model, Cloud provider, Claude
Code CLI, Codex CLI. "Local model" replaces R2's "Local Ollama" as the option's text only: the stored id stays "local", so
no settings file changes, and the card under it is still titled Local Ollama. Cloud provider is stored as "cloud". An
unknown id still reads as the local path, the conservative reading, since the other three send the transcript away.

**The transport is AI Brain's, linked, and the client is small and Remembrance's own.** `AiEndpointPolicy.cs` and
`JsonRead.cs` compile into Remembrance by link, the way it already links `AiExecutablePolicy.cs`: one policy for every
endpoint that can receive a key or text off the machine (https only to another computer, plain http only to loopback, no
redirect followed, every read and wait bounded, a provider's error text kept for the pane and never logged). Refused:
linking AI Brain's `OpenAiCompatBackend.cs`, which implements the brain's backend interfaces and would bring half its
engine with it. `CloudSummary.cs` holds the one chat call (no image, the summary's own ten-minute bound) and the model
listing (AI Brain's listing cap and thirty-second bound), and maps a failure to plain words (a 401 is "refused the API
key", with the provider's own text) and to a category, which is all the log gets.

**One call, as on a CLI.** The transcript goes in the single-shot prompt the CLI path sends, with the summarizer's
one-line system prompt, and a transcript over the 360 KB one-call limit is summarized by the local map-reduce instead,
or said in the Status line when no local model is set, exactly as for a CLI. "Summarize a transcript…" follows the
engine on screen. The summary file's header names the provider, the model and the host the transcript went to.

**Choosing it is the consent.** No separate cloud-data switch, the CLI's rule for the summary (owner, 2026-10-06: "if
the feature is enabled, then the user is well aware"); the card's "Goes through it" row names the host the transcript
goes to, and so does the header.

**remembrance.busy follows where the model runs, not which option was picked.** A remote provider runs nothing on this
machine, so the flag is cleared for its call, as for a CLI. "custom" at a loopback address is a local OpenAI-compatible
server (llama.cpp, LM Studio, Ollama's own /v1) that runs a local model, so the flag stays up, moved to summarizing, and
AI Brain stands down for it as it does for the local path.

**The key, and the presses that prove one.** One key, DPAPI-sealed for the current Windows user with an entropy string
of its own in `<module data>\cloud\api-key.dpapi`, never in settings.json (not one of PaneFieldIds; the layout check's
"Save stores it" rule names it as the second exception beside the CLI token), never logged, Load answering only "set".
Refused: one key per provider, as AI Brain keeps; a key that does not fit the provider chosen is what Validate and
Refresh say. Refresh cloud models and Validate both rebuild the pane, which empties a typed secret (BACKLOG
N-cli-token-04), so a typed key that a press PROVES (the provider answered with it) is saved by that press, and one the
provider refuses is not: the CLI token's 1.3.3 rule. The value "set", Load's own hint, is never taken for a typed key:
the self-test echoed Load's values back while 2.2.0 was written, sealed the word "set" over the real key, and every call
after it was refused. Remove key deletes the file and logs the delete either way.

**The model dropdown never picks for the user.** It offers the provider's list from the last Refresh behind a
placeholder that is stored as "", with the saved model always kept in it (a dropdown whose value is missing from its
options reads back blank, and the next Apply writes the blank: RefreshDynamicOptions' lesson). Unlike the local
dropdown, Refresh selects nothing: OpenRouter lists hundreds, and the first alphabetically is nobody's choice. The
placeholder reads "(press Refresh cloud models)" until a Refresh has listed models and "(pick a model)" after: the
real-app check on 2026-10-07 found the first wording still showing over a list the provider had just answered, which
reads as a Refresh that failed.

#### feature/fullscreen-per-monitor

App 1.5.0 and AI Brain 1.4.0. The owner, 2026-10-07: Ctrl+Alt+P gave no thinking dots on Claude Code while NieR:Automata
ran fullscreen on a third monitor (the log: `ask declined: fullscreen stand-down`, twice), AgentFlow kept talking
meanwhile, and the rule they chose over a preference was "if it's on a monitor that does NOT have a fullscreen app, it
is not suppressed, but if it's on a fullscreen in-use monitor it auto-suppresses".

**The stand-down is two questions, and only one of them has a switch.** AI Brain's single `FullscreenBlocked` declined
every remark on every engine whenever a fullscreen window existed on ANY monitor, resting on two premises: a model
loading beside a game can take it down, and the companion is hidden during a game. The first holds for the local model
only; the second holds for one monitor only, since a companion moves off a fullscreen monitor to a free one. So:
`GpuGuardBlocks` (the switch, local slot only, plus the cloud slot's fallback to it, which waits while a game runs) and
`CompanionStoodDown` (no switch, every engine: whether the user can see the companion this turn belongs to). Whether a
reply can be seen is not a preference, so it has no control; a user with a large graphics card who wants the local model
during games turns the switch off.

**This reverses two earlier decisions, on the owner's word.** F067 (2026-09-29, earlier in this file) made a
refused explicit ask log-only because "on a single monitor the companion is hidden while the game runs"; on the local
slot the refusal is now also SAID to a companion in view (`FullscreenGpuSpokenLine`), and to a hidden one still not,
which is the single-monitor case F067 was right about. And lane feature/cli-backend kept the switch live on a CLI
because "a remark during a game is declined on every engine, because the companion is hidden"; a CLI no longer reads
the switch, so it moved from Triggers to Local provider, live on the local and cloud slots (the card's
`OnLocalOrCloud`) and greyed on a CLI. Not Local server: that card is Ollama's, and llama.cpp or LM Studio behind the
OpenAI-compatible kind fill the graphics card just the same.

**The per-companion answer is a separate interface, `ICompanionStandDown`, not an `IHost` member.** RecordingHost
implements IHost and ships inside every module's zip (ModuleKit is bundled), so a new IHost member changes every
module's payload and forces every module to republish for nothing; the last addition (IsFullscreenActive, pre-rebase
1.9.9) did exactly that (BlinkingLed 1.0.4, Fortunes 1.2.7, "payload refresh only"). The host's IHost implements the
interface beside it; a module asks with `host as ICompanionStandDown` and declares MinHostVersion 1.5.0. AI Brain falls
back to `IsFullscreenActive` when the cast fails, which only a test double can make happen; its probe's
`StandDownHost` answers like the host, with the single-monitor case as its default so every check written before this
lane kept its meaning.

**One definition of "stood down".** `FormCompanion.IsStoodDownForFullscreen` (the marker or the hidden flag) is what
SayWithDwell holds a line on and what `CompanionHost.IsCompanionStoodDown` answers, so "the host held this companion's
line" and "a module was told it cannot be seen" are one fact. It is not `IsFullscreenBlocked`, the marker alone, which
answers the TopMost question: a companion spawned onto a blocked monitor is hidden with no marker set.

**The sound rule follows the companions, not the monitors.** While a fullscreen window is up and no companion is in
view, the app plays no sound of its own: a companion's animation sound, a module's PlaySound and a module's
PlayNotificationSound all ask `SoundHeldForFullscreen` first. Before, the bubble was held and the sound was not, so a
single-monitor game heard AgentFlow's chime and a hidden sheep's own animation sounds (a hidden companion keeps
ticking). A held sound is DROPPED, not replayed: a chime minutes late belongs to nothing, while the line it came with
is kept by the companion's own hold. The two sounds the user presses for (Preferences' Test sound, the notification
preview) are not held. With a game on one screen and a companion in view on another, sounds play, with the bubble they
belong to. The hold's start is logged once per episode, since a quiet app must say why.

**Refused: a preference** ("suppress whenever any app is fullscreen" / "never suppress"), which the owner proposed first
and then replaced with the per-companion rule. "Never suppress" cannot mean it on one monitor, since the host never
draws over a fullscreen window, and "suppress everywhere" silences a companion the user can see. **Not done, filed:**
the host's default speaker and drop subject do not prefer a companion in view, so with several companions a line can
go to one hidden on the game's monitor while another stands in view (BACKLOG N-fullscreen-per-monitor-01).

#### feature/pane-rebuild

Host only, app 1.5.0. A rebuild of the pane on screen keeps the user's place (BACKLOG N-rem-cloud-01) and a secret they
typed and have not applied (N-cli-token-04), and Apply stays lit for the edits a rebuild carried.

**The scroll offset rides the view-state hand-off, and the rebuilt view asks for it at build time, unclamped.** The
offset travels with the collapsible cards' open state in the one view-state slot (`PaneView.ViewState`), filled by all
three rebuilds of a pane that stays up and never by a fresh open, so a pane opened from the nav still starts at its top.
The brief asked for a restore after the rebuilt pane has laid out, on the grounds that a restore before layout does
nothing. Measured on .NET 10 (a scratch probe headless, then `--wpf-options-selftest` through a real settings window laid
out by hand): that holds only for a request clamped by hand, which clamps against a scrollable height of 0 and restores
to the top (a mutation case pins it). An unclamped `ScrollToVerticalOffset` made before layout is held by the
ScrollViewer and carried out once it has laid out, coerced to the new extent, so a place below the end of a pane that
came back shorter lands at its new bottom. Refused: a ScrollChanged handler that waits for a viewport, asks and
unsubscribes. It measured the same, and no input made its wait matter (a request asked from a TextBox's bubbled
ScrollChanged, before the pane had a viewport, still landed), so it was code no test could fail.

**A typed secret comes back in its box after a rebuild, masked.** `PluginApi.cs` says secrets are "never read back into
the UI", and that stays true of a secret the module holds. What comes back is a value the user typed on this screen and
has not applied, carried as an unsaved edit: a field whose shown value differs from Load's own answer (`_loaded` against
`_stored`). A Secret showing Load's answer is never refilled, the "A value is saved" hint reads Load's answer rather than
the box, and an action that wrote the secret changed Load's answer, so the merge keeps the module's and the box stays
empty under the hint.

**Where the host cannot see an action's write, the typed text comes back.** A module answers a presence hint ("set") for
a secret, not the value. When a key is already saved and the user types a new one that a Validate then saves, Load says
"set" before the press and after it, so the merge reads the field as untouched by the action and puts the typed text
back, masked, with Apply lit; Apply then saves the key the press already saved. Harmless, since it is the same value, and
the reason a module that wants its write seen should answer a hint that changes with the value. This follows from the
merge's per-field rule; it was not reproduced on screen.

**The window honours the unsaved-edit signal from the one view whose own rebuild it ran.** The view that asks for a
rebuild hands its edits to the view that replaces it and raises the signal again after the rebuild, because the rebuild
greys Apply at its end. The generation guard RA-329 and RA-330 added to NotifyDirty declined that, since by then the
view raising it is the replaced one, so Apply went grey after every rebuild that carried an edit, in the window only:
the self-test's rebuilding hosts have no such guard and passed. `OptionsWindow._rebuiltFrom` names that one view and
every other build resets it, so a view left behind by a nav switch still cannot light Apply. Two consequences read as
defects and are not. After an action that saved the only edit (a Validate that saved the typed key), Apply is lit over a
pane that holds nothing new, because the signal reflects what the replaced view held before the press; pressing it
saves what is on screen. And the signal is raised for a LoadPending pane's cascade too, where the rebuilt view itself
cannot tell an edit from its stored answer. Refused: having the window ask the rebuilt view whether it holds unsaved
edits. It needs no re-raise, but it reads every LoadPending pane as unchanged after a cascade (its stored baseline is its
pending answer, N-modules-update-all-02), so AgentFlow's pet dropdown would still leave Apply grey.

#### feature/cli-model-effort

AI Brain 1.5.0 and Remembrance 2.3.0, module-only; MinHostVersion unchanged. The owner, 2026-10-09: cheap calls on a
small model, heavy ones on a large one. Each module's Coding-agent CLI card gains "Claude Code model" and "Claude Code
effort", live only on Claude Code, and "Codex model" and "Codex effort", live only on Codex.

**This reverses "No model chooser" (`#### feature/cli-backend`, 2026-10-06), on a measurement.** 1.3.0 ran Claude Code
with no `--model`, so a call ran on whatever the user's own setup resolved. Measured 2026-10-09 by the coordinator (8
real calls, Claude Code 2.1.293): with no `--model` the child inherited the user's `ANTHROPIC_MODEL=opus` and resolved
to claude-opus-5-5, and the user's `~/.claude/settings.json` `modelSettings` raised its effort to xhigh, because the
companion's `--settings` adds to the user's settings rather than replacing them. One identical one-word call cost
$0.0407 there against $0.0011 with `--model haiku` (Claude Code's own list-price estimate; on a subscription it is
usage-limit draw). So every model call now names an effort, and a model unless the user chose "Claude Code's default",
which stays an option for a user who wants their terminal's setup and is now a choice rather than the only behaviour.
Codex keeps its automatic pick as the option "Automatic", so the catalog rules of that entry stand, and its effort,
fixed at low until now, is chosen too.

**Aliases, never full ids, for Claude Code.** The user's two Claude organisations serve different catalogs (one has no
claude-haiku-5-5, no claude-sonnet-5-5 and no max effort), and a saved sign-in token can land a call in either; an alias
resolves inside whichever organisation serves the call. The runner passes on exactly haiku, sonnet or opus, or no
`--model`; a Codex model must be a slug matching `^[a-z0-9][a-z0-9.\-]{0,63}$`; an effort must be exactly low, medium or
high. The values come from a settings file, so they are checked where the argument list is built
(`CodingAgentCli.CheckChoice`, which AskAsync runs before anything starts and BuildArguments runs again so that no
caller can build a command line around an unchecked value). Anything else is the outcome ChoiceRefused: nothing is
started, not even a version check, the pane names the value, and the log names only the class. No xhigh or max is
offered: every call these modules make is bounded (an AI Brain remark at TimeoutSeconds, 120 s by default, an audition
sample at 20 to 90 s, a summary under its ten-minute CallTimeout), one organisation serves no max at all, and the
spend is what the change exists to cut.

**A value this version does not offer is kept, shown and refused, never clamped.** A hand edit, or a later version's
alias after a downgrade, stays in the file, appears in its dropdown as itself (the saved value is always unioned into
the options, because a closed Enum blanks a missing value on save), and the runner refuses it by name. Clamping it to
the default would run a model the user did not choose, on their account, without a word. The conservative reading an
unknown "runs on" id gets does not carry over: an unknown CLI runs nothing, while a replaced model would run. Text a
row hands back that matches no option changes nothing, because the "" a closed Enum returns for an unmatched value
would otherwise be stored as "Claude Code's default".

**Existing installs move to the new defaults, and a later default reaches only files that never held the key.** An
install with no saved key reads its module's default: the behaviour the keys replace is the spend being cut, and the
owner chose the move. Each default is one named constant per module per CLI (`AiSettings.DefaultClaudeModel`,
`DefaultClaudeEffort`, `DefaultCodexModel` and `DefaultCodexEffort` in AI Brain, the same four names in Remembrance's
`SummaryRoute`), the only definitions: AI Brain's field initialisers and Normalize read them, Remembrance's
`SummaryRoute.ModelFor` and `EffortFor` do, and every self-test expectation is computed from them. They were set from
the coordinator's live eval of the same day (the next paragraph). An Apply writes all four keys
as it writes every other field, so a default changed in a later version reaches only a file that never held them.
Remembrance's are its own because the summary is its one heavy call (the three-hour synthetic meeting above cost
Claude Code 60.3k input tokens) where AI Brain's calls are short remarks. A blank model is a choice (Claude Code's
default, or Automatic); a blank effort means the module's default.

**Defaults chosen by measurement (2026-10-09).** The lane wrote placeholders first; the coordinator's live eval chose
the values the constants now hold: 1,736 arm calls and 140 judge calls, none failed, about $24 at Claude Code's
list-price estimate. The corpus is synthetic: for AI Brain, 14 screens made for the test, each asked four ways (an Ask
and a random drop on a screenshot, an Ask and a poke on the screen's OCR text), two repetitions, so 112 remarks per arm;
for Remembrance, 4 meetings of one to three hours, two repetitions each. Two blind judges, one from each vendor (Opus at
high effort and gpt-6.1-sol at high effort), scored every output 1 to 5 without knowing the arm, and a score is their
mean. Each arm was compared with its CLI's best on paired per-input differences. The deciding test is an exact
permutation test stratified by input, because 14 screens and 4 meetings are too few for a percentile bootstrap: over 4
meeting means that bootstrap fires about a quarter of the time on pure noise. "Not distinguishable" means not shown
worse at that n, not shown equal.

- AI Brain, Claude Code: **opus at medium effort** (the placeholder was haiku at low). Opus/medium 4.07, opus/low 3.70,
  sonnet/low 3.44, sonnet/medium 3.40, haiku/medium 2.88, haiku/low 2.74. 8% of opus/medium's remarks read a name or a
  private detail off the screen, against 17% of opus/low's, 21% of sonnet/low's and 25% of haiku/low's. Haiku also
  used the signed-in account holder's first name in 11 of its 224 remarks, a name no screen showed and the module never
  sends (it comes from Claude Code's own context, which no flag here removes), and a third of haiku/low's remarks ran
  past the length limit. Opus/medium's median is 6.1 s and its cost $0.0103 per remark at list price, against $0.0050
  for sonnet/low and $0.0003 for haiku/low. The runner-up, opus/low, lost on its failures alone: on the remarks neither
  judge flagged the two score the same (4.48), and opus/low read a name twice as often and made more hallucinations both
  judges flagged (5 against 2). The pick holds under the 95% interval rule and under a Holm-corrected exact permutation
  test over the 14 screens, and it is narrow: under the GPT judge alone sonnet/low ties it.
- AI Brain, Codex: **Automatic at low**, the behaviour before 1.5.0. The automatic pick was gpt-6.1-sol, which at low
  scored 4.46 with no remark reading a name, at a median of 4.4 s. Runner-up gpt-6-astra at low, also 4.46, lost on
  latency. Then astra/medium 4.43, sol/medium 4.42, gpt-5.6-terra 4.00 to 4.07, and gpt-6-luna 3.88 to 3.96, which broke
  the JSON reply format in 31 replies.
- Remembrance, Claude Code: **sonnet at medium effort**, the placeholder, confirmed. Every Claude arm recalled every
  planted item with 0 trap failures, so the judges decided: opus/medium 4.94, opus/low 4.92 ($0.29 a summary),
  sonnet/medium 4.81 ($0.15), sonnet/high 4.81, haiku/low 4.73 ($0.008, no material error, its deductions for polish).
  The first analysis picked opus/low on the percentile bootstrap. A skeptic's re-derivation refuted that: under the
  exact stratified permutation test sonnet/medium is not distinguishable from the best (one-sided p = 0.125), at half
  opus/low's cost, so opus/low is the runner-up and lost on cost. Haiku/low is the budget choice, left to the user in
  the card.
- Remembrance, Codex: **Automatic at low**, unchanged. gpt-6.1-sol/low 4.94 (median 8.6 s), astra/medium 4.94,
  terra/medium 4.92, luna 4.42 to 4.60 (it dropped a planted item in every summary of one meeting). The runner-up,
  astra/medium, tied on every measure and lost the last tie-break, which goes to the choice that changes nothing.

Caveats. One sign-in organisation, one machine, one afternoon: the user's other organisation serves no claude-haiku-5-5
and no claude-sonnet-5-5, so an alias can resolve there to a model that was not measured (the "(asked for ...)" note
above is how a user would see it). The corpus is synthetic and easier than real use: the long meetings are padded with
templated talk, recall sat at its ceiling for every Claude arm, and AI Brain ran one persona, the default, so the others
are untested. Codex's `--json` stream never names the model that answered, so for Codex only the slug asked for is known
and a server-side swap would not show; and Automatic is the runner's pick from Codex's own catalog, so a catalog change
can move it without a release. The dollar figures are Claude Code's list-price estimates, not what a subscription is
charged. The name-reading that decided AI Brain's Claude Code pick may respond to a stronger no-names rule in the
persona prompt (N-cli-model-effort-03); if that prompt changes, the Claude arms should be run again, because a cheaper
arm could then come within the interval. The eval's files stay outside the repository.

**The model-call levers on a Claude Code child.** `CLAUDE_CODE_DISABLE_ADVISOR_TOOL=1`, because every call the
coordinator measured attached a server-side advisor tool with claude-opus-5-5 as the advisor even on `--model haiku`,
which a long summary could spend the saving on; `CLAUDE_CODE_DISABLE_TERMINAL_TITLE=1`, which skips the extra request
Claude Code makes for a terminal title (the env-vars reference); and `CLAUDE_CODE_EFFORT_LEVEL` taken off, because it
outranks `--effort`. All three go on a model call's child alone, in `ApplyModelCallEnvironment`; a version check,
`auth status`, `debug models` and an update carry none of them and no model or effort. A source invariant holds that
nothing writes them to this process or from a module: a child inherits this process's environment and the self-tests
read each child's environment through the fake, so a process-wide write is the one shape they cannot see. This lane
made no model call; the coordinator's eval then checked both levers live, in the companion's exact call shape.
`CLAUDE_CODE_DISABLE_ADVISOR_TOOL=1` removes the Opus advisor. `CLAUDE_CODE_DISABLE_TERMINAL_TITLE=1` works, but in the
companion's real environment there is no title request to skip: Claude Code makes it only when
`CLAUDE_AGENT_SDK_VERSION`, an agent shell's variable, is set. It stays, because a companion started from such a shell
passes that variable on to every child, and the runner does not take it off.

**The model that answered is read from the stream, and said.** Claude Code's `assistant` events carry it in
`message.model`; the runner keeps the last usable one (an errored call carries `<synthetic>`). Refused: `system/init`'s
`model`, which echoes the request, an invalid id included, and `modelUsage`'s first key, where Claude Code's own side
request lands. A `system` event with subtype `model_fallback` is recorded, from and to. When the family that answered
differs from the alias asked for, Validate, both Status rows and Remembrance's header say "(asked for haiku)". That is
not an error: an organisation can serve an alias with another model, and the user should see it rather than be
refused. All of them word it through the runner's `RanOn`, so one answer is never said two ways.

**Claude Code's wrong-model words are a refused model, and a model the user chose is never swapped.** "There's an issue
with the selected model", `[claude-code:unrecognized_model]` and "is restricted by your organization's settings" were
labelled Failed; they are now ModelRefused, whose sentence names the model and, for one the user chose, says to choose
another in the CLI card.
Codex's automatic pick is still forgotten when the server refuses it, so the next call asks the catalog again, but only
the automatic pick: a refused slug the user chose is said by name, and the pick, which it did not come from, stays.
Before this change a Claude Code refusal also deleted Codex's cached pick, forcing a needless catalog fetch.

**Validate tests the choice on screen.** It maps the pending rows the way Apply would store them and passes them with
the typed token. It saves neither: they wait for Apply like every other row, the token being the one exception
(`#### feature/cli-token-and-locate`). Its tick names the model that answered and the effort.

**Fixed option lists, so the effort rows do not follow the model.** Haiku (fastest, lightest on usage), Sonnet, Opus
(most capable, heaviest on usage) and Claude Code's default; Low, Medium and High; for Codex, Automatic and the display
names its own catalog lists. Refused: a LoadPending or ReloadOnChange cascade that offered each model's own efforts,
because on these panes that path meets the open host bugs N-modules-update-all-02 and N-pane-rebuild-02. So the Codex
effort row offers all three for every model, though the catalog lists each model's supported levels and the runner
keeps them; what Codex does with an effort a model does not list is unmeasured (N-cli-model-effort-02).

**The Codex list comes from the catalog call the pick already makes, cached beside the pick.** From the same `codex
debug models` output, the entries whose visibility is "list" and whose slug a user could choose keep their display
name, supported efforts and whether they take images, in the pick's file under the same fingerprint and version rule.
A cache written by aibrain 1.3.0 to 1.4.0 has no list and is fetched again once. ~/.codex/models_cache.json stays
refused. The panes read the list from the cache alone (`CachedCodexModels`), so opening a pane starts no Codex, a
Claude Code user's pane included. Refused: `CachedDetails`, which starts a probe.

**AI Brain: a chosen Codex model that takes no images is read the screen as text.** With Use vision on, a slug whose
catalog entry lacks "image" gets the OCR text turn, decided before the capture (`AiBrain.SendsScreenshot`), and the CLI
row says so. Chosen over ending the turn on an advisory, `AiModelPolicy.ChooseModel`'s rule for a cloud primary's
blind model (R-022), because nothing is swapped: the model is still the one the user chose, and it is sent only what it
can read. The runner refuses such a screenshot as ModelCannotSee all the same, as the backstop for a catalog not cached
yet. A slug the catalog does not list is sent as chosen, since not knowing is not a no (F102). Remembrance's list
carries no such note, because a summary sends no image.

**AI Brain: the choice reaches its calls through CodingAgentBackend's constructor, not ChatAsync's model argument.** That
argument is the slot-model policy's id, the local or cloud slot's model on those paths, and was already ignored on a
CLI; teaching that policy aliases it never lists would have been the alternative. The model and effort are not part
of BackendFingerprint, because every Apply rebuilds the brain and a CLI holds nothing in VRAM.

**Remembrance: a recording's summary runs on the SAVED choice, read at the stop; Summarize a transcript and Validate run
on the choice on screen**, BUG-013's rule for a pane action. The rows go through Save's own `StoredFormOf`, now an
instance method because the Codex labels belong to this instance's cached catalog, so every press reads the rows the
way Apply stores them. The header names the model that answered and the effort, where 2.1.0 to 2.2.0 said "its
default model". The one-call limit of 360 KB took Claude Code's default model's window as 200k tokens; it is now the
chosen model's window, not re-measured per alias.

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
today. **The next one filed is BUG-016**, and it is filed in [`../BACKLOG.md`](../BACKLOG.md).

| bug | | fixed |
|---|---|---|
| BUG-015 | the CLI card said "Claude Code is not installed" on a machine where Claude Code was installed, because the runner looked only on the PATH the app started with, in npm's prefix and in Claude Code's own installer folder | aibrain 1.3.1 and remembrance 2.1.1 (feature/cli-token-and-locate): the PATH saved now, WinGet's package folders and VS Code's copy are searched too |
| BUG-014 | one module description 25 characters over its bound made every installed app refuse the whole catalog, behind "Couldn't reach the catalog: Catalog contains an invalid module entry." | the catalog by 4d1e228 (2026-10-06); the host on feature/catalog-insight: an entry is refused alone and by name, the gate and the publish scripts run the app's own parser over catalog.json, and the panes word the failure by its case |
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
