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
`CapsLockStopsNow()` is the one predicate the tick and the enable path share. **Changed 2026-10-14 by the owner's
decision (Blinking LED 1.0.7):** with "Resume on application restart" ticked, its default, the refusal and the Caps
Lock stop save no off and last until the companion next starts, which is the shape rejected above; the record of
what changed, and how far the objection is answered (the tray half is, the pane half is accepted on purpose), is under
`#### feature/blinkingled-resume`. Unticked, this entry stands as written, and the off now carries its reason, so a box
ticked over it later resumes it at the next start.

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
down until the switch moves (the refusal says how), and an empty sweep between them clears it. (That cost
was the wrong size: every run of edits to one file signs alike, which is BUG-017, and since 1.9.1 the
Claude card's element is in the signature too; see `#### feature/agentflow-card-identity`.) Rejected:
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
Both modules read a saved value through one reader, the runner's `CodingAgentCli.SavedChoiceText` (review findings F3
and F4, the same day), where two copies had drifted: AI Brain dropped control characters and Remembrance kept them. It
drops control characters and the bidirectional controls and makes a line or paragraph separator a space, so the
dropdown, the Status rows and a refusal show the value that is checked. A value still over 96 characters, longer than
anything the runner passes on, is cut and ends in "…", which no alias, effort or slug holds, so it is refused like any
other value this version does not offer. Refused, not trimmed again after the cut: the readers used to cut without
trimming, and the runner's own trim then turned `opus` padded past the cut with spaces and junk into "opus", a call on a
model nobody chose. What holds is that only an allowlisted value reaches a command line, and that a cut never makes one.

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
**Accepted cost of the move (review finding F14): a default the user's organisation or plan does not serve stops every
call.** An install that never chose now sends its module's default, as the constants stand `--model opus` (AI Brain)
or `--model sonnet` (Remembrance); where an
organisation's managed settings restrict that model, or a plan does not include it, every remark or summary is
ModelRefused until the user chooses another, and until 1.5.0 the same install ran on whatever its own setup resolved.
Accepted as the cost of the owner's "existing installs move to the new defaults", because the alternative, falling
back to no `--model` for an untouched default, would need the default kept unsaved until its row is touched, and an
Apply writes all four keys, after which a saved default cannot be told from a choice. What makes it findable: a
refusal of the module's own default ends with a sentence saying so and that Claude Code's default (Automatic on Codex)
is a choice too (`CodingAgentCliText.Describe` with the module's default, in Validate, an AI Brain audition sample and
a Remembrance summary's failure). It is judged by the value, for the same reason, so a user who chose that model hears
a true sentence. A remark that fails says only "model refused" on the Status card, as every failed remark says its class
there; Validate says the rest.

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
The child's environment is not the last word, though (review finding F1, the same day): Claude Code writes every entry
of a settings file's `env` block into its own process environment, so a `"CLAUDE_CODE_EFFORT_LEVEL": "xhigh"` in the
user's `~/.claude/settings.json` came back inside the child after the runner took it off, and a
`"CLAUDE_CODE_DISABLE_ADVISOR_TOOL": "0"` there would bring the advisor back. So each model call's `--settings` is built
for that call (`ClaudeModelCallSettings`, with the JSON serializer, from the checked effort and fixed text alone): the
CLAUDE.md exclusions, and an `env` block that sets `CLAUDE_CODE_EFFORT_LEVEL` to the call's effort and both levers to
`1`. `--settings` ranks above the user's, the project's and the local settings files and below managed settings alone,
and an `env` entry follows that order variable by variable, so the user's other entries stay (Claude Code's settings and
env-vars docs). The effort lever is set rather than removed because a settings file cannot remove a variable; the
removal from the child's environment and `--effort` stay too. A managed settings file still wins, by design. Probes
carry no `--settings` at all, as before.
**Measured live the same evening (the coordinator's check, Claude Code 2.1.293, one call per arm, the companion's own
call shape).** With `{"env":{"CLAUDE_CODE_EFFORT_LEVEL":"xhigh","CLAUDE_CODE_DISABLE_ADVISOR_TOOL":"0"}}` planted in
the child's working-directory `.claude\settings.local.json`, the build before this fix ran at an applied effort of
xhigh with the Opus advisor attached (Claude Code's own `get_settings` answer, and its debug log's "[AdvisorTool]
Server-side tool enabled with claude-opus-5-5 as the advisor model"), while Remembrance's header still said medium,
the effort asked for. The fixed build, the same plant in place, ran at medium with no advisor: `get_settings` showed the
plant under `localSettings` and the call's own values under `flagSettings`, which won. A plant in the project's
`.claude\settings.json` was beaten the same way, in probes that made no model call. The user's own
`~/.claude/settings.json` was not planted, by rule; it ranks below both of those files, so what beat them beats it. The
evidence stays outside the repository, with the eval's files.

**The model that answered is read from the stream, and said.** Claude Code's `assistant` events carry it in
`message.model`; the runner keeps the last usable one (an errored call carries `<synthetic>`). Refused: `system/init`'s
`model`, which echoes the request, an invalid id included, and `modelUsage`'s first key, where Claude Code's own side
request lands. A `system` event with subtype `model_fallback` is recorded, from and to. When the family that answered
differs from the alias asked for, Validate, both modules' CLI rows and Status rows and Remembrance's header say "(asked
for haiku)". That is not an error: an organisation can serve an alias with another model, and the user should see it
rather than be refused. All of them word it through the runner's `RanOn` (the rows by way of `LastAnsweredOn`, below),
so one answer is never said two ways.
The model said is the one the stream named, else the one Claude Code reported falling back to, and never the alias
asked for (review findings F5 and F12): when the stream named neither, "haiku" used to be said as if haiku had
answered, and a fallback note then named the alias as the model fallen back to. Such an answer now reads "on a model
it did not name (asked for haiku) at low effort", and Remembrance's header "a model it did not name (asked for
sonnet) at medium effort"; "its default model" is left for a call that asked for none. The fallback note says "fell
back to it" only of the model it names, and otherwise names both ends. `RanOnShort` is RanOn without the effort, the
form a Status row says after "last answered". Both modules' rows say what last answered through
`CodingAgentCliText.LastAnsweredOn`, RanOn in the CLI row and RanOnShort in the Status row (F10: both Status rows had
built a phrase of their own that dropped the fallback note). It is said beside the SAVED choice, and the answer kept can
be another request's: an Apply since, an audition of a choice on screen, or Remembrance's Summarize a transcript, which
runs on the screen's choice. "Haiku at low effort, last answered on claude-opus-5-5" then read as a model swap, so when
that call asked for another model or effort than the one saved, both rows say "(that call asked for opus at medium
effort)" in the parenthesis the other notes share, its effort included, and neither adds the effort after it (F13).
**What last answered is a real call's, never a Validate's (round 2, the first on-screen walk).** The kept answer was any
call's until then, Validate's included, so after an unapplied Validate of gpt-5.6-luna AI Brain's Status card read
"runs on: Codex CLI, its automatic pick at low effort, last answered on gpt-5.6-luna", as if Luna were the pick. A
remark, an audition sample and a summary are kept (`CliRequest.Validation` marks Validate's call, and AskAsync keeps
no answer of it); a Validate's answer belongs to the card's own Status row, which names the model it answered on. The
F13 note stays, for a real call made on another choice than the one saved. A token typed in the card reaches a call
only through Validate, so the clause that kept such an answer out is this one now; and a Validate that saves the typed
token no longer puts its own answer back as the last one, so after that save the rows name no model until a real call.

**Text from outside is made displayable before a pane or the log shows it (F3).** What a CLI said, a catalog's
display name and a settings value quoted back in a refusal go through `CodingAgentCli.Displayable` (by way of
`OneLine`): C0 and C1 controls and the bidirectional controls dropped, every line break and the tab made a space.
Nothing is ever passed to a command line this way: the allowlist (`CheckChoice`) refuses such values first. A saved
model or effort is cleaned the same way when it is read (`SavedChoiceText`, above), so its own dropdown and Status row
show it as it is checked.

**A reading taken on a sign-in that changed is not recorded (F6).** A summary still running on organisation A's saved
token when the user pressed Remove token, or applied B's, finished and wrote A's model back as the one that last
answered, under B. Each token save or removal now moves a sign-in generation on, under the lock the readings are
written under, and a call, a Validate and the card's details read record what they learnt only if it has not moved
since they began. Validate's deliberate keep after saving a typed token records under the generation that save began.
An answer on a typed token that was not saved is not recorded as the last answer either: it describes no sign-in the
module holds. The card's own details read, which runs single-flight, is followed by one more when the generation moved
while it ran (round 2, the fix pass's skeptic): the stale read was dropped, and the pane's rebuild after Remove token
had found it in flight and started none, so the card said "Checking…" until the pane was opened again.

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

**The card's Status row is the most recent Validate of either CLI, and names that CLI (round 2).** It was the SAVED
CLI's last Validate, so on the first on-screen walk a Validate of Codex chosen on screen and not applied answered
beside the button while the row under it kept the older Claude Code tick: two green ticks naming different CLIs. The
runner keeps each CLI's line with the order it was recorded in (`LatestValidation`), and each line begins with its CLI
("✓ Codex: answered at 18:41:01, in 5.1 s, on gpt-6.1-sol at low effort"), so whichever CLI the radio shows, the row
says what the last press tested. A sign-in change still drops Claude Code's line alone, and the row then shows Codex's
if one ran.

**One "signed in as" row and one "Goes through" row per CLI (round 2).** Load describes the SAVED choice and cannot
know the radio, so with Codex chosen and not applied AI Brain's card still said "Every remark goes to Anthropic" and
Remembrance's "The transcript's text goes to Anthropic", in the privacy row, beside live Codex rows. Each card now has
"Claude Code signed in as" and "Codex signed in as", "Goes through Claude Code" and "Goes through Codex", each with its
CLI's `EnabledWhen` on the radio, strictly narrower than the card's gate, so the pair for the CLI on screen is live and
the other greyed, the way the model and effort rows already were. Refused: one row whose text follows the radio
through a LoadPending or ReloadOnChange cascade, the path that meets N-modules-update-all-02 and N-pane-rebuild-02.
The saved CLI's account is read as before (`CachedDetails`, refreshed behind); the other CLI's is what was last read
of it (`KeptDetails`), and a pane open starts no read for it, so that opening the pane starts no CLI the user did not
choose (the rule the Codex list already keeps). Until something has read it, the row says "Not checked yet. Press
Validate.", which reads like a fault and is not one: a Validate now reads the details of the CLI it tested when none are
kept fresh, after its call and before its answer, and the rebuild the press asks for shows them. Awaited, so nothing of
Validate's is still running when it answers and an Update CLI pressed next is not refused for it. Refused: reading both
CLIs' details on every pane open, which would start Codex for every Claude Code user, a local user's two CLIs included.
AI Brain's Codex privacy row still describes the SAVED Codex model's image support (a chosen model that takes no images
is sent the screen's text), since the model row on screen has no reader at Load either.

**Plain model labels (round 2, the owner, 2026-10-09).** "For the model names, you dont need to explain them, just
Haiku, Sonnet, Opus is fine for the label, the user will know what they are", and for Codex "simple and short is fine".
The first build's "Haiku (fastest, lightest on usage)" and "Opus (most capable, heaviest on usage)" were also cut off in
the 177 DIP editor column on the first walk. AI Brain's Codex list dropped its "(takes no images)" note with them: the
image rule stays the runner's and the brain's, the CLI row and "Goes through Codex" say what it does, and a refusal is
explained when one happens. The stored values did not change (aliases and slugs), and a value handed back under a
former label still maps to its alias or slug, though no list offers it.

**Fixed option lists, so the effort rows do not follow the model.** Haiku, Sonnet, Opus and Claude Code's default;
Low, Medium and High; for Codex, Automatic and the display names its own catalog lists. Refused: a LoadPending or
ReloadOnChange cascade that offered each model's own efforts, because on these panes that path meets the open host
bugs N-modules-update-all-02 and N-pane-rebuild-02. So the Codex
effort row offers all three for every model, though the catalog lists each model's supported levels. Since the review
(F8, F17) the runner reads them: a pair the model's own entry does not list is refused before anything starts, as
ChoiceRefused naming the model and the effort, for a chosen slug and the automatic pick (`CodexEffortProblem`), chosen
over sending it because the catalog is Codex's own word for this binary and a refusal after the start would read as a
refused model and, on the automatic pick, forget the pick before every call; and a Codex refusal whose words are about
the effort keeps the pick and points at the effort row (`RefusalNamesEffort`). What Codex itself does with such a pair,
and how its refusal words it, are unmeasured (N-cli-model-effort-02).
"The automatic pick" had a hole the fix pass's skeptic found (round 2): the pick takes any slug `IsUsableSlug` passes
(an "Org/Upper_Case" one too) while the checks' facts kept only slugs a user could type (`IsCodexModelName`), so such
a pick had no entry and an effort it does not list was sent. Of the two ways out, admitting the pick's own slugs to the
facts or narrowing the sentence to the picks a user could type, the first was taken (`MergeCatalog`'s pick slugs, read
back from the cache the same way): the refusal before the start is the better outcome for the pick as for a choice, and
the slug already goes on the command line as the pick, so it reaches nothing it did not reach before.

**The Codex list comes from the catalog call the pick already makes, cached beside the pick.** From the same `codex
debug models` output, the entries whose visibility is "list" and whose slug a user could choose keep their display
name, supported efforts and whether they take images, in the pick's file under the same fingerprint and version rule.
A cache written by aibrain 1.3.0 to 1.4.0 has no list and is fetched again once. ~/.codex/models_cache.json stays
refused. The panes read the list from the cache alone (`CachedCodexModels`), so opening a pane starts no Codex, a
Claude Code user's pane included. Refused: `CachedDetails`, which starts a probe. Two review findings of the same day
shaped what the checks read. F2: the list is the dropdown's, visible entries capped at 64, and a model the user chose
while it was listed, which a later catalog keeps but hides, read as unknown, so a text-only one was sent the screenshot;
so every entry whose slug a user could name keeps its efforts and image support beside the list, any visibility, no cap
(`CodexPick.Catalog`, a listed entry being the list's own), and a cache without them is fetched again once. F7: the
pane's list and AI Brain's check before a capture read the cache without the fingerprint rule, so after a Codex update
they still answered from the old catalog; both now go through `CurrentCachedPick`, which holds the cache to the
installed binary's fingerprint by file probes alone and reads a mismatch as unknown (Automatic and the saved slug on
the pane, the capture as for an unlisted slug) until the next call or card read fetches the new catalog.

**AI Brain: a chosen Codex model that takes no images is read the screen as text.** With Use vision on, a slug whose
catalog entry lacks "image" gets the OCR text turn, decided before the capture (`AiBrain.SendsScreenshot`), and the CLI
row says so, as does the card's Goes through row since the review (F15: it said a screenshot goes; "Goes through Codex"
since round 2). Chosen over ending the turn on an advisory, `AiModelPolicy.ChooseModel`'s rule for a cloud primary's
blind model (R-022), because nothing is swapped: the model is still the one the user chose, and it is sent only what it
can read. The runner refuses such a screenshot as ModelCannotSee all the same, as the backstop for a catalog not cached
yet. A slug the catalog does not list is sent as chosen, since not knowing is not a no (F102). Neither module's Codex
list says it in a label since round 2 (the plain labels above); Remembrance's never did, because a summary sends no
image.

**AI Brain: the choice reaches its calls through CodingAgentBackend's constructor, not ChatAsync's model argument.** That
argument is the slot-model policy's id, the local or cloud slot's model on those paths, and was already ignored on a
CLI; teaching that policy aliases it never lists would have been the alternative. The model and effort are not part
of BackendFingerprint, because every Apply rebuilds the brain and a CLI holds nothing in VRAM.

**AI Brain: the persona audition's header names the CLI and the choice its samples asked for (review finding F16).**
Its header ends "· Claude Code CLI, haiku at low effort · made-up scenes · 5 calls", where it named the brain's own id,
"claude-code-cli", so two auditions on two models read alike. The choice asked for,
not the model that answered: five samples can answer on different models, and what last answered is the CLI row's to
say.

**Remembrance: a recording's summary runs on the SAVED choice, read at the stop; Summarize a transcript and Validate run
on the choice on screen**, BUG-013's rule for a pane action. The rows go through Save's own `StoredFormOf`, now an
instance method because the Codex labels belong to this instance's cached catalog, so every press reads the rows the
way Apply stores them. The header names the model that answered and the effort, where 2.1.0 to 2.2.0 said "its
default model". The one-call limit of 360 KB took Claude Code's default model's window as 200k tokens; it is now the
chosen model's window, not re-measured per alias.

**Round 3 (2026-10-10, the review of round 2 and the second on-screen walk).**

**A Validate's tick is forgotten when an Apply saves another choice for its CLI.** The second walk applied GPT-6-Astra at
medium on Codex and the card's Status row still showed the green tick for gpt-6.1-sol at low, through a close and a
reopen, as if the new choice had been tested. The runner now keeps the model and effort each Validate tested beside its
line, and both modules' Apply calls `ForgetValidationUnlessTested` for each CLI with the choice just saved: a tested
choice that differs from the saved one, by model or by effort, drops that CLI's line, and the other CLI's stays. Compared
the way `CheckChoice` sends them (trimmed, a blank effort the runner's floor), so a choice validated on screen and then
applied unchanged keeps its tick. Refused: forgetting every Validate on every Apply, which would drop a tick the user just
earned for the very choice they applied.

**The signed-in row of a CLI not read yet names the step that reads it.** "Not checked yet. Press Validate." stood in
Codex's greyed row while Claude Code was chosen, and pressing Validate then checked Claude Code. The row now reads "Not
checked yet. Choose Codex CLI under "Brain runs on", then press Validate." (Remembrance: "Summary runs on"), true in every
radio state. "Under" the radio and not "above": AI Brain's radio sits in the left column, beside the CLI card, not over
it. A Validate on a CLI that turns out not to be installed fills that row too, with the not-installed sentence.

**One phrasing for Codex's automatic pick.** The CLI row said "gpt-6.1-sol, the first model this Codex lists, at low
effort" and the Status rows "its automatic pick at low effort". Every row of both modules now says "automatic pick
(gpt-6.1-sol) at low effort" where the pick is known and "automatic pick at low effort" where it is not (no details read
yet, a catalog that listed nothing, or the audition header, which reads none). With Use vision on and two picks, AI
Brain says "automatic pick (text-slug, and vision-slug for a screenshot)". No clause explains what the pick is: the
owner's plain-voice rule of round 2.

**Remembrance's Status line drops its outer parentheses on a CLI.** "summary: on (Claude Code CLI, sonnet at medium
effort, last answered on claude-haiku-5-5 (that call asked for haiku at low effort))" ended in a doubled "))", and
"automatic pick (gpt-6.1-sol)" would have nested a third pair. On a CLI the summary part now reads "summary: on Claude
Code CLI, sonnet at medium effort, last answered on claude-haiku-5-5 (that call asked for haiku at low effort)", the
shape AI Brain's Status card already had; the local and cloud shapes keep theirs ("on (qwen3:8b)", "on (Cloud provider:
gpt-5-mini)"), whose parentheses hold one value. Refused: keeping "on (Claude Code CLI, ...)" and moving what last
answered after it, which still nests the pick's slug inside the pair.

**AI Brain's Status card describes one call: the last remark, and the model it ran on.** It paired "last answered on X"
(any real call, an audition sample included) with "last remark 22:40 (4.5 s)" from another call, and on the local slot
named a remark the CLI had made. The card's remark field now carries the model from the same record ("last remark 22:40
(4.5 s, on claude-opus-5-5)"), and the separate "last answered" part is gone from the card; the CLI row keeps "last
answered on ..." for any real call. The model is the one the brain's backend says answered, carried on the response
(`BrainResponse.AnsweredOn`): on a CLI the runner's reading of the stream (`CodingAgentBackend.LastAnsweredModel`), on
the cloud-with-local-fallback composite the slot that served (`FallbackBackend.LastServedModel`), else the id sent. So a
CLI remark still names its CLI model after Local model is applied. Refused: reading the runner's `LastAnswered` when the
remark is recorded, which an audition sample finishing in between would have answered for.

#### feature/cli-home-files

AI Brain 1.5.1 and Remembrance 2.3.1, module-only, a privacy and cost fix; MinHostVersion unchanged. Measured 2026-10-10
by the investigation under `temp/setup-isolation` (Claude Code 2.1.293); this lane made no model call.

**The leak was the folder, not the flags.** An installed module runs Claude Code in its own folder under
`%LOCALAPPDATA%`, and Claude Code reads every CLAUDE.md from the working folder up to the drive root, so the user's
`%USERPROFILE%\CLAUDE.md`, and `%USERPROFILE%\AGENTS.md` through the built-in `cc-plugin-agents-md`, went with every
remark and summary: about 4,100 to 4,200 extra input tokens a call on the maintainer's machine. The eval and every
on-screen walk ran from folders on D:, which have no such ancestor, so none of them could see it. The children the
second walk saw (N-cli-model-effort-12) were Claude Code's own and start whatever the settings.

**Minimal and organisation-safe, by the coordinator's ruling.** A companion call leaves the USER's own choices out and
switches off none of the controls an organisation manages, and it does not change where Claude Code reads its sign-in or
settings, because the installed modules here run on a saved sign-in token, a path no isolation measurement covered. So
each model call's `--settings` gains three exclusions (`**/CLAUDE.md`, `**/CLAUDE.local.md`, `**/AGENTS.md`; all three
are needed, since excluding CLAUDE.md alone made agents-md load AGENTS.md in its place, 5,095 tokens against 919),
`"disableAllHooks": true` and `"disableClaudeAiConnectors": true`. What still applies, and must: managed settings,
managed instructions (a managed CLAUDE.md is added with no claudeMdExcludes check, read from the binary, not measured),
and managed hooks, which `disableAllHooks` outside managed settings cannot stop and which still see the prompt and the
answer. That reads like a gap and is the ruling: an organisation that oversees Claude Code with a hook oversees the
companion's calls too, as long as the hook comes through managed settings or a managed plugin; hooks in plugins synced
from a claude.ai organisation are off too, because Claude Code does not count synced plugins as managed (none of the
synced plugins here carries hooks). One deliberate exception, recorded so it is not mistaken for a slip: a `true` for
`disableClaudeAiConnectors` holds even over a managed `false` (the settings reference's exceptions table), so connectors
an organisation configured are not fetched either. A call has no tools (`--tools ""`), so it could not use them, and the
fetch is the cost being removed. The probes (version, auth status, update) still carry no `--settings`.

**A hook's events are data, never the answer.** With the user's hooks off, an organisation's still run, and their
`system`/`hook_started` and `hook_response` events still arrive in the companion's own stdout carrying what the hook
printed. The stream reader already skipped every system event other than `model_fallback`; it is now pinned through
the fake CLI: never the answer (a stream with no result line after a hook's output is NoAnswer), never in what a failed
call says (Said reads the result line and stderr alone), never in the log, and never the end of the reading.

**Refused outright, pinned by `cli-home-files: no refused lever` in `tests/runtime-hardening-selftest.ps1`** (the runner
and both modules, comment-stripped; the self-check is left out because it names `--bare` to assert it is absent):
`--safe-mode` and `CLAUDE_CODE_SAFE_MODE` (they narrow far past the user's setup), `--bare` (it wants an API key in
place of the sign-in), a private `CLAUDE_CONFIG_DIR` (it moves where the sign-in and settings are read),
`syncClaudeAiPlugins` (the user's switch, and a `false` is not a harmless opt-out), `DISABLE_TELEMETRY`,
`CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC`, `CLAUDE_CODE_ENABLE_TELEMETRY` and `OTEL_*` (the CLI's own reporting is the
user's and the organisation's to set, and PRIVACY.md now says the companion leaves it on),
`CLAUDE_CODE_DISABLE_CLAUDE_MDS` (it drops the managed instructions too), `allowManagedHooksOnly` (a managed-settings
key), an `enabledPlugins` list (it would stand in for the user's own), and `--setting-sources`. The last is the real
alternative and is not refused on its merits: full isolation is an owner decision (N-cli-home-files-01), with its 204
tokens of the user's settings file against what a user or employer keeps there and the untested saved-token path.

**Codex is unchanged.** Its docs (`codex-rs/core/src/agents_md.rs`) find a project root by walking up to `.git` and read
only the working folder when there is none, and no ancestor of the installed folder here holds one, so the docs do not
make a leak certain; the levers, the live check's measurement and the proposed next fix are in N-cli-home-files-05.

#### feature/codex-project-docs

AI Brain 1.5.2 and Remembrance 2.3.2, module-only, a privacy fix (BACKLOG N-cli-home-files-05); MinHostVersion
unchanged. This lane made no model call: the measurement is the coordinator's live check of 2026-10-11 (Codex 0.160.1,
the runner's own arguments, evidence under `temp/setup-isolation/verify-lane`), and the live check of this build is the
coordinator's too.

**The leak was a `.git` above the folder.** Codex sends every AGENTS.md from the nearest folder holding a `.git` (its
default `project_root_markers`) down to its working folder, and reads only the working folder when it finds no marker
(`codex-rs/core/src/agents_md.rs`). No ancestor of the installed module's folder holds one on the maintainer's
machine, so nothing went there; on a machine whose profile is kept in Git, `%USERPROFILE%\AGENTS.md` and any between
went with every remark, audition, summary and Validate. The live check planted an empty `.git` above a work folder with
a canary AGENTS.md: read and obeyed, 10,644 input tokens against 10,529 without it. Each Codex model call now passes
`-c project_doc_max_bytes=0`, which in the same shape removed exactly the canary (10,529 again).

**Why a zero budget and not `project_root_markers=[]`.** The zero budget ends `load_project_instructions` before any
project file is read, and it is the lever that was measured. An empty marker list ("disables parent traversal") was
never measured, and by the same file it still reads an AGENTS.md in the working folder itself, so it would leave one
path open where the budget leaves none. Neither reaches `~/.codex/AGENTS.md`, the user's global file, which Codex reads
by a separate path and adds ahead of the budget; no setting removes it, and PRIVACY.md says it is sent.

**What reads like a gap and is the ruling.** The `-c` does not override what an organisation enforces, which is
feature/cli-home-files' ruling carried over: a companion call leaves the user's own choices out and an organisation's
in. On Windows, the only platform the companion runs on, that holds because nothing an organisation enforces there
names this key, not because a managed value wins. Codex's loader ranks the legacy `managed_config.toml` (a file or
MDM-delivered) above command-line `-c` overrides on macOS and Linux only. On Windows Codex 0.160.1 ignores the file,
in its own words "CODEX_HOME/managed_config.toml is no longer supported on Windows. Use
%ProgramData%\OpenAI\Codex\requirements.toml for enforced settings or config.toml for defaults", and the MDM layer is
macOS-only, so nothing an organisation can deploy on Windows outranks this `-c`. What enforces on Windows is
`requirements.toml`, and its fields in 0.160.1 (login methods, approval policies, sandbox modes, feature requirements,
rules, models, `additional_developer_instructions` among others) include no `project_doc_max_bytes`. Those
developer instructions reach the model as a developer-role message built from the requirements, not through
`load_project_instructions`, so the zero budget does not drop them. The `-c` does outrank two layers an organisation
can deploy, a cloud-managed config bundle and the system `config.toml`, but the user's own `config.toml` outranks both
of them too, so they set defaults rather than enforce (the warning above calls `config.toml` the place for defaults),
and the companion setting this one key over them is what the user's own file would do. It also outranks a project's
`.codex/config.toml`, which ranks above the user's own file; whether the user's own `.codex` folder can come back as
that project layer on a profile kept in Git is BACKLOG N-codex-project-docs-01. Read, not measured (no machine here
has a managed file): the layering from `codex-rs/config/src/loader/README.md`, `config_layer_source.rs` and the
loader's `mod.rs`, the developer-message path from `config_requirements.rs` and `session/world_state.rs`, through
Context7 on 2026-10-12, and the Windows warning and the requirements field names from the strings in the installed
0.160.1 `codex.exe`. This lane's first commit said a managed `project_doc_max_bytes` still wins over the companion's;
that is true only off Windows, and the pre-release review found the warning that says so.

**Model calls only.** The probes (`--version`, `login status`, `debug models`, `update`) read no instructions and carry
no `-c` at all, which CheckModelAndEffort already held for `-c`; the new group reads them for the key in any form, a
`--config` one included.

#### feature/codex-levers

*Superseded 2026-10-13 (AI Brain 1.5.4, Remembrance 2.3.4, `#### feature/codex-revert-levers`): the seven levers below
are gone by the owner's decision, and every Codex model call sends 1.5.2's argument list again. The measurements here
stay as the record of what the levers saved and how they were measured; do not re-add them.*

AI Brain 1.5.3 and Remembrance 2.3.3, module-only, a cost fix (BACKLOG N-codex-overhead-01); MinHostVersion unchanged.
This lane made no model call. The measurement is the coordinator's of 2026-10-12 (Codex 0.160.1, gpt-6.1-sol at low,
the runner's own arguments from a profile-rooted folder with no `.git` above it, evidence under
`temp/codex-overhead/measure`), and so is the live pre-check of web search on the shipped 1.5.2 build (two calls,
`temp/codex-levers/pre`) and the live check of this build.

**The decision: seven levers after the `--disable` list, on every Codex model call and on no probe.** Each was measured
alone at that place against a 10,529-token Validate, and the site in `CodingAgentCli.BuildArguments` carries each
one's saving: `-c agents.enabled=false` 1,570, `-c skills.include_instructions=false` 526,
`-c tools.experimental_request_user_input.enabled=false` 186, `-c include_collaboration_mode_instructions=false` 178,
`--disable view_image` 146, `--disable sleep_tool` 87, and `-c web_search="disabled"` 2,456 (the pre-check). All seven
together, live: 5,375 input tokens, 5,154 fewer (49%), 5 under the 5,380 the single deltas predicted, the answer right
and nothing on stderr or in the stream but the four standard events. A screenshot question with the six other than web
search still read its image, which went as `-i` input; `view_image` is the tool that reads an image file from disk.

**Why `--disable multi_agent` did not already do the largest part.** gpt-6.1-sol takes its multi-agent v2 setting from
its own catalog entry, so the feature flag is never consulted for it; `agents.enabled=false` is what removes the
`<multi_agent_role>` and `<multi_agent_mode>` developer messages and the six collaboration tools. `--disable
skill_search` likewise leaves the skills list in the developer message. Both flags stay in `CodexDisabledFeatures`:
on another model they may still be what decides.

**Kept out of `CodexDisabledFeatures`, on purpose.** The brief allowed `view_image` and `sleep_tool` to join the list.
They sit beside the five `-c` levers instead, so each lever has its own measured saving at its own line, the
list stays the sixteen of 2026-10-06 that `Codex disables the sixteen features the brief measured` pins, and the
self-check holds the seven from a table of its own (`CodexScaffoldingLevers`), so a lever dropped from the runner
cannot drop out of the check with it.

**What an organisation can set, and what is still open.** None of the seven keys is a top-level `requirements.toml` key
in 0.160.1, but an organisation's `[features]` table can pin `view_image` and `sleep_tool`, and its
`allowed_web_search_modes` governs `web_search`: an organisation can restrict web search modes in `requirements.toml`,
and turning web search off is stricter than any such list. What Codex 0.160.1 does when such a pin or list conflicts with
the companion's setting was not measured: `managed_features.rs` validates configured features against the pins and may
refuse the configuration, which would fail every companion Codex call, not only cost the lever's saving (BACKLOG
N-codex-levers-01). Whether a `multi_agent_v2` pin outranks `agents.enabled=false`, which is not a feature key, was not
read either. Measured live on 2026-10-12: Codex 0.160.1 accepted `-c web_search="disabled"` and the input fell by
2,456, which shows the setting took effect. The bare `-c tools.experimental_request_user_input=false` is a config error
in 0.160.1; the table's `enabled` key is the form Codex reads, and it is the one measured.

**What stays, and why nothing here removes it.** The code-mode tool frame (about 1,130 tokens) and the catalog-only
tools (about 400) come from the model's catalog entry; only a private model catalog shipped with the companion could
remove them, which is a larger change than this lane's and was not taken. The user's own `~/.codex/AGENTS.md` goes
with every Codex call and no setting removes it (about 243 tokens per KB; a 15 KB file measured about 3,700), which
PRIVACY.md says. The sixteen `--disable` features, the permission and environment preambles and the zero
`project_doc_max_bytes` are unchanged.

**Model calls only.** `--version`, `login status`, `debug models` and `update` carry none of the seven, held by
`CheckCodexLeversCarried` reading every probe for each lever's key in any form, beside a WITNESS that the same
reading finds all seven on each model call.

#### feature/codex-revert-levers

AI Brain 1.5.4 and Remembrance 2.3.4, module-only, an owner decision; MinHostVersion unchanged. This lane made no model
call. It takes out the seven levers `#### feature/codex-levers` added, so every Codex model call's argument list is
again exactly what 1.5.2 sent: `CodingAgentCli.BuildArguments` differs from 1.5.2's only by the comment at the site
that says why, and the exact-argument pin in CheckFlags is 1.5.2's list again.

**The decision.** Asked whether the seven were worth a fallback (a retry without them when Codex refused its
configuration, the fix BACKLOG N-codex-levers-01 proposed), the owner said: "if we are doing things to cut down on
tokens, that may later cause it to fail, i would rather just use more tokens". The fallback was built in a lane and
discarded unmerged, and the seven went instead. The rule: a token cut that may make a call fail later loses to spending
the tokens, and a privacy or safety control is judged separately, on what it protects. Do not re-add the seven, or a
lever like them whose only case is the tokens it saves, without a new decision from the owner. Since 2026-10-13 that
covers six: `--disable view_image` is back on every Codex model call, as a privacy control by the owner's decision and
not as a token lever (AI Brain 1.5.5, `#### feature/codex-feature-switches`). Since AI Brain 1.5.6 it covers five:
`-c skills.include_instructions=false` is back the same way, a privacy control, because the skills section's
skill-roots table names the user's profile folder (`#### feature/codex-config-refusal`).

**Why they could fail, measured offline.** Codex 0.160.1, a throwaway CODEX_HOME, no model call (2026-10-12): an
unknown `--disable <feature>` exits 1 with "Unknown feature flag", which is how `--disable view_image` or
`--disable sleep_tool` would fail on a Codex that renamed or dropped the feature; a bad key inside a typed table
(`agents.*`, `tools.*`) exits 1 with "invalid type ... in `<path>`", the tables `agents.enabled=false` and
`tools.experimental_request_user_input.enabled=false` lived in; an unknown top-level `-c` key, or `features.<name>`, is
ignored. An exit 1 at configuration comes before any request is sent, so on that machine every remark, audition,
summary and Validate would fail.

**The saving forgone.** 5,154 input tokens a call on gpt-6.1-sol, the automatic pick and the one model measured: a
Validate at 10,529 input tokens with 1.5.2's arguments against 5,375 with the seven, and a Remembrance summary at
12,619 against 7,465 (the coordinator's live measurements of 2026-10-12). Those measurements stay under
`#### feature/codex-levers` and in BACKLOG N-codex-overhead-01 as the record.

**Web search is Codex's default again.** The companion passes no `web_search` setting, so Codex uses its default,
`cached`, as in 1.5.2. `-c web_search="disabled"` was the largest of the seven (2,456 tokens) and was shipped as a token
cut like the other six. No sentence in Readme.md or PRIVACY.md said web search or those tools were off, so neither file
changes. `--disable view_image` comes off with the rest, so Codex's image-file tool is back as in 1.5.2: under the
read-only sandbox it can attach an image file the user can read, and that image would go to OpenAI, the vendor that
already receives the screenshot. 1.5.2 had the same exposure and the owner approved going back to it; whether it
counts as a privacy control, and so joins the sixteen features with their failure shape, is part of the open
question below. Answered 2026-10-13: it does, and it is off again with `--disable view_image`, failing closed like the
shell and browser switches (`#### feature/codex-feature-switches`).

**What stays, and why.** Everything 1.5.2 sent: `exec --skip-git-repo-check --ephemeral`, `-s read-only`,
`--ignore-user-config`, `--ignore-rules`, the effort, `-c include_permissions_instructions=false`,
`-c include_environment_context=false`, `-c project_doc_max_bytes=0`, the sixteen `--disable` features,
`model_instructions_file`, the model, the screenshot and `--json -`. The read-only sandbox and the `--disable` features
that take away the shell, the browsers, computer use, hooks, apps and plugins keep a question about the screen from
acting on the machine; `--ignore-user-config`, `--ignore-rules` and the zero `project_doc_max_bytes` keep the user's own
Codex setup and every project `AGENTS.md` out of the call; the effort and the instructions file are the companion's own
choice. Several of these cut tokens as well (the 2026-10-06 measurement, Codex 21,521 to 12,307), and not every one of
the sixteen features is a safety control in itself. This lane changed none of them, as its brief directed; whether the
owner's rule reaches any of them is the owner's call, and this record leaves it open. **Answered 2026-10-13** (AI
Brain 1.5.5, Remembrance 2.3.5, `#### feature/codex-feature-switches`): eleven of the sixteen, with `view_image`
added, stay `--disable` and fail closed; the five that only cut tokens (`multi_agent`, `skill_search`, `tool_suggest`,
`goals`, `mentions_v2`) go as `-c features.<name>=false`, which Codex 0.160.1 ignores rather than refuses when it
does not know the name (measured offline), so on that version's behaviour a rename costs tokens rather than failing
the call; a later Codex could tighten this, as 0.160.1 already refuses a bad key inside the `agents.*` and `tools.*`
tables. Two facts for that call, as this lane recorded them. The
failure shape applies to the sixteen too: a Codex that drops or renames one of those features exits 1 on every call,
filed as BACKLOG N-codex-revert-levers-02 with a named failure sentence proposed, not a retry and not a flag dropped,
so a call that fails says why and no safety flag is given up to make it succeed. The `-c` keys kept are all top-level,
which Codex 0.160.1 ignores when it does not know them, so on that version's behaviour a rename there would not
fail a call; it would lose the key's effect instead, which for `project_doc_max_bytes` would be a privacy loss nothing
on the user's machine would notice. A known key given a value of the wrong type was not measured.

**The guard.** `CheckCodexLeversGone`, in both modules' self-tests through the shared runner: one check per lever, over
every Codex model call shape built (8) and through the runner's fake CLI (a remark, a screenshot question, a summary and
Validate), reading each argument for the lever's key in any form (`agents.`, `skills.`, `request_user_input`,
`collaboration_mode`, `view_image`, `sleep_tool`, `web_search`) with the `-i` image and the instructions file left
out, so a folder's name cannot read as a lever; every probe (`--version`, `login status`, `debug models`, `update`)
read the same way; a WITNESS that types 1.5.2's
flags rather than reading them from the runner, so a feature dropped from `CodexDisabledFeatures` is seen, and finds
every one on every call; and a WITNESS that the lever reader finds each of the seven in the form 1.5.3 sent it and
none inside the two path values, so a reader that sees nothing cannot pass the per-lever checks. The project-docs
group keeps the helpers 1.5.3 split out for both groups (`CodexModelCallShapes`, `CodexCallsThroughRunner`, `ProbeKindsRan`). The twelve `codex-levers:` mutation cases went
with the code they mutated. Since AI Brain 1.5.5 the table holds six: its `view_image` row, and the two mutation cases
graded on it, left with `#### feature/codex-feature-switches`, whose check requires the image-file tool off, and the
WITNESS types the flags 1.5.5 requires. Since AI Brain 1.5.6 it holds five: its skills row, and the mutation case graded
on it, left with `#### feature/codex-config-refusal`, whose check requires the skills section off.

**Filed, not done here.** BACKLOG N-codex-revert-levers-01: Codex sends each `-i` screenshot's full local path to the
model as text, seen in the coordinator's live check of the discarded fallback build. N-codex-revert-levers-02: the
failure sentence above. Both fixed in AI Brain 1.5.6 and Remembrance 2.3.6 (`#### feature/codex-config-refusal`).

#### feature/codex-feature-switches

AI Brain 1.5.5 and Remembrance 2.3.5, module-only, an owner decision; MinHostVersion unchanged. This lane made no model
call. The evidence is an offline measure run of 2026-10-13 under `.dac-worktrees/temp/codex-feature-switches/measure/out`
(local, not committed): Codex 0.160.1, the vendor `codex.exe` the companion starts, a throwaway CODEX_HOME that never
held a sign-in, every child's proxy pointed at a closed port, and a loopback stub that captured each request.

**The decision.** `#### feature/codex-revert-levers` left open which of 1.5.2's sixteen `--disable` features is a
safety or privacy control and which a token cut. Asked about the five that only cut tokens (`multi_agent`,
`skill_search`, `tool_suggest`, `goals`, `mentions_v2`), the owner first chose to remove them, then, on seeing that
`codex features list` shows all five on by Codex's default, wrote: "Interesting, lets then change to "Ignore-if-unknown
form" then, thank you". The option they took read: "Send the five as -c features.<name>=false. Token use stays as it
is today, and a Codex rename would cost tokens instead of failing calls." Asked about Codex's image-file tool
(`view_image`, back since 1.5.4), they chose "Off, as a privacy control": "Back to --disable view_image, alongside the
shell and browser switches. If a Codex update renames it, calls fail rather than quietly turning it back on, the same
trade the other safety switches already make." The standing rule behind both answers: "if we are doing things to cut
down on tokens, that may later cause it to fail, i would rather just use more tokens". `image_generation` and
`worktrees` were not part of the question and stay `--disable`.

**Two forms, and why each.** A safety or privacy switch must fail closed, and on Codex 0.160.1 `--disable <feature>`
does: an unknown name exits 1 ("Unknown feature flag") before any request is sent, so on that version's behaviour a
Codex that renamed one stops the call rather than quietly turning the feature back on. Twelve go that way
(`CodexDisabledFeatures`), the ten safety or privacy switches and the two the question left as 1.5.2 sent them: `apps`,
`plugins`, `browser_use`, `browser_use_external`, `computer_use`, `image_generation`, `hooks`, `in_app_browser`,
`worktrees`, `shell_tool`, `unified_exec` and `view_image`, in 1.5.4's order with `view_image` last. A token-only switch
must never fail a call, and on Codex 0.160.1 `-c features.<name>=false` does not: an unknown or misspelt key exits 0 and
is ignored (measured offline), so on that version's behaviour a rename costs tokens rather than failing the call. A
later Codex could tighten this; 0.160.1 already refuses a bad key inside the `agents.*` and `tools.*` tables. The five
go that way, right after the `--disable` list (`CodexFeatureKeysOff`). From Codex's source (`codex-rs/cli/src/main.rs`,
`FeatureToggles::to_overrides`, read through Context7, not measured), `--disable X` checks the name and then becomes
this same `features.X=false` override in the same session-flags layer, so the two forms differ only in what an unknown
name does. A Validate's argument list goes from 53 arguments to 55, and nothing else in it changed. The probes carry
neither form, as they carried no `--disable` before.

**The offline evidence** (paths under `measure/out`). `codex features list` with no flags, with 1.5.4's sixteen
`--disable` and with the new list: each of the five reads true, false, false, and `view_image` true, true, false; the
new list's reading is identical to 1.5.4's plus `--disable view_image` (`features-*.txt`, `features-check.json`).
Unknown and misspelt keys (`features.zz_not_a_feature`, `features.mentions_v3`, `features.multi_agnet`) exit 0 and
change nothing, on `features list` and on `exec`, where the request is identical to the known-key one (`cap/unk-*`);
the control, `--disable` with an unknown name, exits 1, on `exec` in 0.04 s with no request reaching the stub
(`cap/ctl-*`). On each of the eight models `codex debug models` lists under a throwaway home, the rendered request
differs from 1.5.4's only by the image-file tool: the `### view_image` section of the code-mode `exec` tool on the seven
code-mode models (655 characters, 174 o200k tokens offline) and gpt-5.5's top-level `view_image` tool (108); input
messages, every other top-level field and an `-i` screenshot (one `input_image`, the same data URL) are unchanged, and
none of what the five control comes back (`diff/`, `analysis.json`). A second renderer, `codex debug prompt-input`,
agrees, beside a control that shows it can differ (`pi-check.json`, `pi-control.json`). After normalisation the new
request is identical to the one the coordinator measured live on 2026-10-12 at 10,383 input tokens (1.5.4's arguments
plus `--disable view_image`), so a Validate is predicted at 10,383 through the runner against 1.5.4's 10,529, and a
Remembrance summary of corpus m00 at about 12,473 against 12,619. Both were then measured on this lane's build
(2026-10-13, Codex 0.160.1): a Validate at 10,383 through the runner and 10,384 in the app, and the m00 summary at
12,473 in the app, each exactly the prediction; a question about a synthetic screenshot was answered with the word
it showed, at 10,709 input tokens, so the attached image still reaches the model with the image-file tool off.

**What a rename now costs instead, on 0.160.1.** Codex 0.160.1 says so and carries on: two `item.completed` items of
type `error` lead the `--json` stream ("Codex is ignoring 1 unrecognized configuration setting ... `features.<name>` is
ignored."), and the turn answers. `ParseCodexStream` reads only `agent_message` items, so the call answers and the
warning is in neither the answer nor what a failed call says; `CheckCodexFeatureSwitches` feeds the runner that stream.
Each feature left at Codex's default, measured offline: `goals` adds 664 tokens on the seven code-mode models and 645 on
gpt-5.5, `multi_agent` 175 on gpt-5.5 only, and `skill_search`, `tool_suggest` and `mentions_v2` none, in a home with no
skills, apps or plugins, which may understate what they add on a real one.

**Not measured.** An organisation's `[features]` pin: Codex 0.160.1 on Windows reads neither `CODEX_HOME\managed_config.toml` nor
`CODEX_HOME\requirements.toml` (an unparseable canary in either exits 0, the same canary in `config.toml` exits 1), the
file it enforces is `%ProgramData%\OpenAI\Codex\requirements.toml`, and testing that would mean writing outside a
throwaway home; by the source both forms put the same key in the same layer, so a pin should act on both alike. The
account's own model catalog: the throwaway home lists 11 models, 8 visible, which include the four the eval named.
One switch that does not read as off: `unified_exec` reads `stable true` in `codex features list` under
`--disable unified_exec` on 0.160.1 for Windows, in 1.5.4 and now alike (BACKLOG N-codex-feature-switches-01).

**The guard.** `CheckCodexFeatureSwitches`, in both modules' self-tests through the shared runner, one check per
switch over every Codex model call shape built (8) and through the runner's fake CLI (a remark, a screenshot, a summary
and Validate): each of the five named exactly once, as `-c features.<name>=false`, and never as `--disable <name>`;
`view_image` named exactly once, as `--disable view_image`, and never only as a features key; no probe carrying a switch
in either form; a WITNESS that the form reader tells the forms apart, sees a second mention and skips the two path
values; and the 0.160.1 ignored-key stream still answering, beside a WITNESS that the same stream ended by
`turn.failed` fails. The exact-argument pin in CheckFlags now types the twelve and the five by hand rather than reading
the runner's list, and so does the required-flags WITNESS of `CheckCodexLeversGone`, whose table lost its `view_image`
row. Two `codex-revert-levers:` mutation cases went with that row, "a Codex model call disables the image tool again"
and "the image tool comes back off as a features key": the intent they held, the image-file tool left on, is the one
this decision reverses, and the `codex-feature-switches:` cases hold the opposite.

**Readme.md and PRIVACY.md.** Neither changes. No sentence in either says what a Codex call's tools can or cannot do:
PRIVACY.md's Codex lines (27 to 43) say what reaches OpenAI and which instruction files go, and its one sentence about
a call having no tools (line 55) is about Claude Code. Readme.md's Codex lines (129 to 153, 391 to 397) say the same
of the model, the effort and `AGENTS.md`.

#### feature/codex-config-refusal

AI Brain 1.5.6 and Remembrance 2.3.6, module-only; MinHostVersion unchanged. Three things in one release, the owner's
choice ("One release (Recommended)": "Add what you chose above to the 1.5.6 / 2.3.6 lane alongside the review fixes for
the clearer message. Test once (offline, then live screenshot questions and a Validate), then ask you before
publishing."): a sentence for a setting Codex refuses (BACKLOG N-codex-revert-levers-02, the owner's "for the clearer
message, yes lets tackle that"), the screenshot named by its file name (N-codex-revert-levers-01), and Codex's skills
section turned off as a privacy control. This lane made no model call. The evidence is offline, by the method of
`#### feature/codex-feature-switches` (Codex 0.160.1's vendor `codex.exe`, a throwaway Codex home that never held a
sign-in, every child's proxy on the closed port 127.0.0.1:9, a loopback stub only where a run had to answer), local and
not committed: the refusal captures under `.dac-worktrees/temp/codex-config-refusal/capture/out`, `.../dropflag/out`
and, for the fix round after the second review, `.../fix/measure/out`; the image-path study under
`.dac-worktrees/temp/codex-image-path` (its saved requests hold the real profile path only in unredacted hash files);
the offline proof on the first 1.5.6 / 2.3.6 build (086d6c2), whose Codex command line and start folder this build
keeps byte for byte, `.dac-worktrees/temp/codex-config-refusal/proof/evidence/proof-summary.json` (24 request shapes
through the runner's own `AskAsync` and `ValidateAsync`, both modules, 0 requests off the machine); and the real-codex
re-check of the second round's build, `.dac-worktrees/temp/codex-config-refusal/recheck` (each refusal through the real
`codex.exe` and the runner, 0 requests off the machine), whose probe runs' stderr is the third round's fixture.

**Codex's command line, before and after.** Every Codex MODEL call gains `-c skills.include_instructions=false`, right
after `-c project_doc_max_bytes=0`, so a Validate's list goes from 55 arguments to 57. A Codex call that carries a
screenshot now starts in its own call folder (`modules\<id>\cli\calls\<32-hex id>`, the folder the PNG and the
instructions file are written to) where it started in the work folder (`modules\<id>\cli\work`), and names the image
`-i screen.png` where it named the PNG's full path. Nothing else moves: Claude Code calls, Codex text calls (remarks,
summaries, Validate) and every probe of either CLI start in the work folder as before, `model_instructions_file` keeps
its full path, and no call carries `-C`.

**A sentence, never a retry.** A Codex that refuses one of the settings the runner starts it with is
`CliOutcome.ConfigRefused`, said in one sentence (`CodingAgentCliText.ConfigRefusedSentence`); nothing is retried and no
setting is dropped. Ten of the twelve `--disable` switches are safety or privacy controls that fail closed by the
owner's decision in the entry above, so a retry without the refused one is the very call that decision forbids, and a
retry with the same arguments pays a second call for the same refusal.

**What Codex 0.160.1 writes.** An unknown `--disable` name exits 1 in 0.03 s with `Error: Unknown feature flag: <name>`
on stderr and nothing on stdout; a name swapped in the list (`view_image_v2` for `view_image`, a rename as the companion
would meet it) the same. One of the call's own keys given a value of another type exits 1 in 0.05 s with
`Error loading config.toml: invalid type: <what>, expected <type>` and, on the next line, `in `<key>``
(`project_doc_max_bytes=false`, `include_environment_context=3`, and in the image-path study
`skills.include_instructions="no"`), which settles what N-codex-levers-01 left unmeasured: a retyped key fails the call,
as a bad key in the `agents.*` and `tools.*` tables does. A command-line flag Codex does not take exits 2 in 0.03 s with
clap's `error: unexpected argument '<flag>' found` on its own line, then a tip and the usage (`--zz-not-a-flag` added,
`--ignore-rules` renamed `--ignore-rulez` and `--ephemeral` renamed `--ephemeral-zz`, a short `-Z`; `--zz-not-a-flag=1`
is named without its value), which is how a Codex that dropped one of the call's own flags would refuse it. A value
clap will not take for one of the call's flags exits 2 in 0.04 s with `error: invalid value '<value>' for '<flag>
<VALUE>'`, the flag by its long name even when the call passed the short one, then the possible values, a tip and the
usage (`-s read-onlyx` and `--sandbox read-onlyx` alike read `error: invalid value 'read-onlyx' for '--sandbox
<SANDBOX_MODE>'`), which is how a Codex that renamed `read-only` would refuse the call's `-s`; `codex exec --help`
gives the long names of the call's short flags (`-s`, `-c`, `-m`, `-i`), and possible values for `-s` alone of them;
`-m` takes any value, an empty one included (that run went on to "No models are available"). Measured in the same
fix round: one of the
call's `model_*` keys given a value of another type (`model_reasoning_effort=3`, `model_instructions_file=3`) exits 1
with the config-load words and nothing Classify reads as a refused model; an effort name Codex does not know
(`lowx`) is not refused at config load at all, the run went on to the closed proxy; and one of the call's own
`features.<name>` keys given a value of another type (`features.multi_agent="nope"`) is named by its table,
``in `features` ``. No KNOWN feature name is refused another way: `--disable` of a name Codex lists as removed
(`js_repl`, `collaboration_modes`) is taken in silence, one it lists as deprecated (`transcript_v2`) with a "deprecated
and ignored" item in the stream, and the call answers either way. With no credentials at all, `codex exec` did not
refuse: it retried the closed proxy for the whole 300 s bound, so the not-signed-in control is a stub that answers 401.
With its home under `%TEMP%`, Codex 0.160.1 writes a WARNING line ahead of any of these refusals that names that folder
and the home's ("WARNING: proceeding, even though we could not create PATH aliases: Refusing to create helper binaries
under temporary dir ..."; the real-codex re-check), so a refusal can start on a line after the first.

**Four phrases, each only for a setting the call passed, each anchored on Codex's words at a line's start.**
`Error: Unknown feature flag: <name>` counts only when the call disabled that name (`CallPassesSetting`); the first build
took that on trust, on the ground that only `--enable` and `--disable` produce the phrase, and the pre-release review
showed the cost: a later Codex that kept the phrase as a warning ("warning: Unknown feature flag: zz (ignored)") and
then failed for another reason would have sent a user who was only signed out to the Modules pane. clap's
`error: unexpected argument '<flag>' found` counts only when the flag is an argument of the call's own, so the item's
third case ("or refuses another flag the call needs") is the same outcome now; clap's `error: invalid value '<value>'
for '<flag> ...'` counts only for a flag whose value is a fixed setting of the module's (`CodexFixedValueFlags`: `-s`,
whose value is `read-only`, and `--disable`, whose values are the twelve names) and only when that value is the one the
call passes right after the flag, never an argument that starts with `-`, and the setting named is the flag as clap
names it (`--sandbox`), never the value. Until the second review a refused value was the generic failure this item set
out to replace; the re-review then found the family reaching too far: `-m` and `-i` were in it, so a refused model or
screenshot file read as a module setting to update, and any long flag was paired with the argument after it (its probe:
`--ephemeral`, which takes no value, with the `-s` after it). Now a refused `-m` value is a plain failure: the model is
the user's or the automatic pick's, so its remedy is the model row and not a module update, and Codex 0.160.1 has no
refusal of a model value to read as `ModelRefused` (`-m` takes any string). A refused `-i` value is about the
screenshot's file, which the missing-image check covers before the start. `-c` stays out too: its values carry the
user's effort and the instructions file's path, and Codex refuses a `-c` value of the wrong type at config load in its
own words, which the next phrase reads by its key. That a Codex which checked `--disable`'s value itself would write `error: invalid
value '<name>' for '--disable <FEATURE>'` is inferred from the `-s` capture's form and `--help`, not measured: 0.160.1
takes any `--disable` value and refuses an unknown one in its own words, the first phrase.
`Error loading config.toml ... in `<key>`` reads the key from the line
right after the phrase's own, where Codex writes it (the first build took the first such line anywhere after it, so a
key two lines down was read as the one refused), and counts only when the call set that very key (`CallSetsKey`): the
same key in a throwaway home's `config.toml`, run without `--ignore-user-config`, writes byte for byte the same stderr,
and a layer an organisation deploys can still load beside the call's `-c` keys (the comment at `project_doc_max_bytes`
in BuildArguments), where "one of the settings AI Brain starts Codex with, check for an update" would be false and no
update would help; 1.5.3's `tools.experimental_request_user_input` and `agents.enabled` refusals are exactly that
today. A name or key counts only where Codex ends it (whitespace after a feature name, the closing backtick after a key,
`' found` after a flag, a space or the closing quote after a flag whose value was refused).

**A table Codex names is never one of the call's settings.** The first build's `CallSetsKey` also took a key inside
one the call set (dead: the call sets leaves, never a table, which the second review's mutation of that clause showed
by surviving) and the table around a key the call set, so ``in `features` `` read as "refused features, one of the
settings AI Brain starts it with". Both clauses went. A table is a plain failure in Codex's own words, though Codex
0.160.1 names the table for one of the call's own `features.<name>` keys given a value it will not take (measured), so
a later Codex that retyped one of the five would get the plain failure too: the same words come from a layer that broke
`[features]`, where the Modules pane would be the wrong place to send the user, and the plain failure still shows
Codex's words. That is the lane's choice and not the owner's, so it is filed for the owner as BACKLOG
N-codex-config-refusal-05, with the measured shape and that reason (at 086d6c2, the first build, the same stderr read
"refused features"). Refused: the exit code alone (exit 1 is every failure), every config-load error as the module's, a table
as the module's, and anchors wider than the measured wording (a later Codex that words it otherwise gets the plain
failure, which is where this lane started).

**The refusal is read before Classify.** Classify reads "model" anywhere beside a refusal word anywhere, and the name
of each `model_*` key the call sets brings the first half, so a refusal of one worded with "not supported" or "not
found" would be a refused model and send the user to the model row. Codex 0.160.1's own words for those keys hold no
such word (measured), so the self-check holds the order with a shape it names as not a 0.160.1 wording
(`CodexRefusalShapes.UnsupportedEffortStderr`) and first checks that Classify alone does misread it.

**An older Codex is told to update itself; that it refuses in the same words is inferred.** `CodexSettingsMeasuredOn`
(0.160.1) is the version the settings were last measured on: from a newer Codex the sentence says "and this Codex is
newer than the one this AI Brain was made for (0.160.1)" and sends the user to the Modules pane or another engine; from
that version, or one whose `--version` did not answer, it says the same without "newer"; from an older one it says "and
this Codex is older than 0.160.1, the one this AI Brain was made for: press Update CLI, then Validate again". That an
older Codex which has not met one of the settings refuses it in the same words is INFERRED from 0.160.1's wording, not
measured: no older Codex was run. So the sentence states the two versions and no cause (the first build's "because"
went with the review). The constant moves with `CodexDisabledFeatures` and `CodexFeatureKeysOff` when they are
measured again.

**The module's name is each module's own constant.** The one runner says "AI Brain" and "Brain runs on" in AI Brain,
"Remembrance" and "Summary runs on" in Remembrance, from `CodingAgentModuleWords`, a partial class each module defines in
its own folder; each module's self-test holds the two to its `ModuleInfo.Name` and its radio's label. Refused: a name
handed to the runner's constructor, because every module self-test injects its own runner (`CliForDiagnostics`), so the
module's real construction never runs under a test and a missing name would go unseen; and the static sentences, the
card's Status row and `CodingAgentCliException`'s message would each have needed it passed. A module that leaves the
constants out does not build.

**Codex's own words stay on the pane; the log gets the setting's name.** The sentence ends with Codex's words in
brackets, as the saved-token sentence quotes Claude Code's: in every phrase the line or lines the refusal was read
from, the phrase's own line (and for a config-load error the ``in `<key>` `` line after it), built from the line the
phrase matched and not from the first `error: ` line, and nothing Codex wrote before or after them. What follows clap's
line, a tip and the usage, is about 200 characters that say nothing of the setting (the offline proof's wording note,
2026-10-13), and the second round cut it for clap's two phrases alone; the real-codex re-check then found what a line
before the refusal can carry: with its home under `%TEMP%`, Codex 0.160.1 writes a WARNING line naming that folder and
the home's ahead of its error, so a refused feature's or key's brackets showed the folders and the bound cut the refusal
off. The self-check's fixtures for it are that run's own stderr, the two folders put as a stand-in. A plain failure
still quotes stderr whole, a warning included (BACKLOG N-codex-config-refusal-06). They never
reached the log before (`CliAnswer.Said`: what a CLI said "can carry an account or an organisation name, so it never
reaches the log"), and a refusal of `model_instructions_file` would quote its path, which names the Windows account. So
the log line names the class (`config-refused`) and the setting (` refused=<name>`), the runner's own word and never
Codex's, and since the classifier names only a setting the call passed, the log needs no check of its own (the first
build's, in the log line, was the half no check could see).

**Where the sentence reaches, and the short forms.** Validate's answer in both modules, AI Brain's audition samples,
Remembrance's summary failure ("Transcript (summary failed: ...)") and the message of the exception AI Brain's backend
throws all carry the sentence (`Describe`). AI Brain's Status card carries the short form, "refused view_image, one of
the settings AI Brain starts Codex with: check the Modules pane for an update" (`Brief(CliAnswer)`, so
`AskAboutScreenAsync`'s invariant in `runtime-hardening-selftest.ps1` reads the answer's brief, still never an
exception's message). The CLI card's Status row says it in two halves around its time: "✗ Codex: refused view_image at
10:55:15, in 0.3 s; it is one of the settings AI Brain starts Codex with: check the Modules pane for an update". The
first build ran the advice into the time ("check the Modules pane for an update at 10:55:15", an update at that hour),
and called the setting "one of AI Brain's settings", which sent a reader looking for a `view_image` row on AI Brain's
pane; the review caught both.

**Claude Code is unchanged.** `RefusalNamesEffort` reads a server's refusal of the effort on either CLI and points at the
effort row; that is not a refusal of the CLI's own settings, Claude Code has no outcome for one, and this lane adds
none.

**The screenshot goes by its file name, from its own folder (N-codex-revert-levers-01).** The owner, asked how to
shorten the label, chose "Shorten it (Recommended)": "Start Codex in the call's own folder with -i screen.png, plus the
missing-image check. Measured offline: the image is unchanged and no folder name stays in the label." Why, from the
study: Codex 0.160.1 labels every `-i` image with the path exactly as given, `<image name=[Image #1] path="...">` ahead
of the picture (`codex-rs/protocol/src/models.rs`, `local_image_open_tag_text_with_path`, read at tag `rust-v0.160.1`),
and no setting removes the label, so the absolute path named the module's folder under `%LOCALAPPDATA%` on an installed
copy, the Windows account with it, and paid about 50 to 70 o200k tokens for it on an installed path, by the length of
the account name (48 to 61 on this box's installed path, mean 54.3 over 2,000 call ids, the offline proof; the study's
69 was its placeholder path's). The label is not needed to see the image, which travels as a data URL beside it; it
lets a coding session's tools reopen the file, and the companion's call has none (`view_image`, the shell and
`unified_exec` are off). Measured offline: with the call folder as the working folder and `-i screen.png` the label
reads `path="screen.png"` (13 tokens), the data URL is byte for byte the one the absolute path sent, and nothing Codex
adds names a folder, the call id or the product (the zero `include_environment_context` keeps the working folder out);
the offline proof saw the same on the first 1.5.6 / 2.3.6 build (086d6c2), whose Codex command line and start folder
this build keeps byte for byte, on all 10 of its screenshot shapes. A relative `-i` is read
against Codex's PROCESS working folder, not `-C`: `-C` with the old working folder read no image at all (arm d2), and
`-C` with the right one changed
nothing (arm d), so there is no `-C`. The same change ends a defect the study found on the way: `-i` splits its value on
commas, so an absolute path with ", " in a folder (a portable copy's data folder, or a data-root override) sent two
"could not read" placeholders and no image. Refused: `-C` alone (it does not resolve `-i`); `-i .\screen.png` (one token
longer, nothing gained); moving every Codex call to the call folder (only an image call needs it, and the work folder is
the one the project-docs measurement above was made in). Risks the study checked: the read-only sandbox
reads the image in-process before the request; `project_doc_max_bytes=0` still stops every project read from the deeper
folder; the call folder is deleted in the call's `finally` (1-2 ms after Codex exits in 3 of 3 runs) with the one-hour
sweep in `NewCallDirectory` behind it; an `--ephemeral` call leaves no record of the folder in the Codex home.

**The missing-image check.** When Codex 0.160.1 cannot read an `-i` file it sends the model the text "Codex could not
read the local image at `screen.png`" in its place, exits 0, and the model answers without the picture (arm d2), so a
wrong folder would be a remark about a screen nobody saw with nothing saying so. A Codex call that carries a screenshot
is therefore not started unless the image exists at the start info's working folder joined to the `-i` value, both read
off what the child would be given (`CodexImageReadable`), and answers `CliOutcome.ImageNotFound`: "✗ Codex was not
started: the screenshot was not in the folder Codex reads it from, so Codex would have answered without seeing the
screen." It covers the folder and the file's existence at the start; Codex failing to read the file for another reason
afterwards is still silent (BACKLOG N-codex-config-refusal-03).

**The skills section is off, as a privacy control.** Every Codex model call, text or screenshot, in both modules, sends
a skills section whose "### Skill roots" table names `<the user's Codex home>\skills\.system`, under `%USERPROFILE%`
because the runner keeps the user's own Codex home on purpose (the runner's file header says why), and
`%USERPROFILE%\.agents\skills` wherever that folder holds a skill (`ext/skills/src/host_roots.rs`,
`roots_from_layer_stack`). So once the label was short, the account name still went with every call. The owner chose
"Turn it off (Recommended)": "Send -c skills.include_instructions=false on every Codex call as a privacy control,
guarded by a self-check. Measured offline: no profile path anywhere in the request, screenshot still sent." Measured
offline (arm s-off): the whole section is gone, roots table included, the request held no path of any kind (its prompt
named none), and the screenshot is still sent. `skills.bundled.enabled=false` is no substitute: the `.agents` root
stays (arm s-bund). The key
was one of 1.5.3's token levers that 1.5.4 took out by the owner's rule ("if we are doing things to cut down on tokens,
that may later cause it to fail, i would rather just use more tokens"); it comes back by this decision as a privacy
control, as `--disable view_image` did in 1.5.5, and the "do not re-add" in `#### feature/codex-revert-levers`, BACKLOG
N-codex-levers-01 and N-codex-overhead-01 now covers the other five: `agents.enabled`,
`tools.experimental_request_user_input.enabled`, `include_collaboration_mode_instructions`, `--disable sleep_tool` and
`web_search`. It also saves tokens (526 input tokens a call, measured live 2026-10-12), which is not why it is here. Its
failure shape on 0.160.1 (arms s-unk, s-type): a renamed key inside `skills.*` is ignored, exit 0 with an "ignoring 1
unrecognized configuration setting" notice, so a Codex that renamed this one would send the section, and the name, again
with nothing failing (BACKLOG N-codex-config-refusal-04); a value of the wrong type exits 1 before any request, which the
refusal sentence names because the call sets the key.

**What could still name a folder.** The claim is narrower than "a Codex call names no folder", which the first build's
headline said and the second review refused: on Codex 0.160.1, what Codex itself adds to a call, apart from the user's
own `~/.codex/AGENTS.md`, names no folder of the user's. Measured by the offline proof on the first 1.5.6 / 2.3.6 build
(086d6c2), whose Codex command line and start folder this build keeps byte for byte
(`.dac-worktrees/temp/codex-config-refusal/proof/evidence/proof-summary.json`: 24 request shapes, both modules, this
box's real `.agents` skills root present; no stand-in, profile path, call id, product folder, `Skill roots` or
absolute path in any body or header, where the 1.5.5 control's bodies held the stand-in, the profile path, `Skill
roots` and absolute paths, and its screenshot labels the call id and the product folder), after the image-path
study's arms b and s-off. What the call carries of the user's own still can: a screen question's front window title and
the title of the window the companion stands on ("You are standing on the window: <title>"), the OCR text or the
screenshot's own content (`modules/AiBrain/engine/AiBrain.cs:512-513`, `DescribeScreenContext`), and a summary's
transcript; the proof's prompts named no folder, so it does not speak to them. A later Codex that renamed
`skills.include_instructions` would send the skill-roots list again with nothing failing (BACKLOG
N-codex-config-refusal-04). Not studied: what a Claude Code call's request carries of the module's work folder under
`%LOCALAPPDATA%` (BACKLOG N-codex-config-refusal-02), and any Codex but 0.160.1. The user's own `~/.codex/AGENTS.md`
still goes, with whatever it says, as PRIVACY.md has said since 1.5.2; the proof put a canary `AGENTS.md` in its
throwaway Codex home for one shape (AI Brain's text call), where it went with the call and named no folder, so the
proof does not speak to the user's own file, and the claim leaves it out by name (the re-review: the second round's headline did not).

**The guards.** In both modules' self-tests through the shared runner's fake CLI, which since this lane reads a relative
`-i` in the folder a call starts in, as Codex does (a WITNESS shows it reads nothing in a folder without the file).
`CheckCodexConfigRefusal`, over the verbatim captures in `CodexRefusalCaptures` and the shapes in `CodexRefusalShapes`
(Codex 0.160.1's measured words around a name the call passes, inferred for a Codex that dropped it, since no 0.160.1
run can produce them): each family's refusal of a setting the call passes is `ConfigRefused` and said in the exact
sentence; a refused name the call did not pass is a plain failure, beside a WITNESS that each capture is refused on the
command line it was captured on; the phrase only as Codex writes it ("Error: " at a line's start), beside a WITNESS on a
line after another; a name or key only where Codex ends it, beside a WITNESS; the sentence's words, Validate's answer,
the Status row in its two halves, the Status card's brief and the thrown message; the older-Codex sentence beside a
WITNESS for the measured and an unknown version; keys the call does not set stay plain failures, beside a WITNESS on
1.5.3's command line; the 401 and 500 controls; Claude Code's failure and a Codex that exits 0, each its own check so each
gate has a case; three answered streams relabelled as a WITNESS of the Ok path (Interpret takes them before the
classifier, so no change to it can fail that check, which the first build's label hid); and the log. Added after the
second review: the refused-value phrase over its capture and its shape; a flag with a value joined on and one named
only by its start, beside the ended forms; the config-load key from the next line only, beside a CRLF WITNESS; a table
never the call's (the measured `features` and the review's `skills` probe); clap's refusal quoted by its error line
alone, beside a WITNESS that a config-load refusal keeps both its lines; the refusal read before Classify; and the
default-model check over a Validate that chose the module's default, so its one gate fails it. Added in the third
round: every phrase's brackets over the re-check's own stderr (`CodexRefusalCaptures.TempOverHome*`: Codex's WARNING
line, its two folders put as a stand-in, then a capture byte for byte), and a flag's from the line its phrase matched,
not an earlier `error: ` line; a refused `-m` or `-i` value a plain failure; each pairing guard on its own (a flag that
takes no value, an argument that starts with `-`); and `--disable`'s value as a refused setting.
`CheckCodexImagePath`: every Codex screenshot call names its image by file name alone and starts in its own call
folder, where Codex reads the bytes; every other start runs in the work folder; the instructions file keeps its full
path and no call carries `-C`; a call whose image is not there is not started and says so, beside a WITNESS that the same
call with its image in place starts (the self-check takes the image away through `CallFolderReady`, the one test seam
added). `CheckCodexSkillRootsLeftOut`: the key once on every Codex model call shape and call, and on no probe; the
required-flags WITNESS of `CheckCodexLeversGone` and the exact-argument pin type it too, and `CheckCodexLeversGone` holds
five levers off. Each module checks its own words and wiring: AI Brain's Validate pane action, Remembrance's summary
failure and its Validate. The mutation cases are under `# ---- lane feature/codex-config-refusal ----` in
`tests/mutate-selftest-guards.py` and `tests/mutate-hardening-guards.py`; the skills-lever case of
`#### feature/codex-revert-levers` went, as its two `view_image` cases went in 1.5.5, because the decision reverses its
intent.

**Readme.md and PRIVACY.md.** PRIVACY.md gains a bullet saying that since these versions what Codex itself adds to a
call, apart from the user's own `~/.codex/AGENTS.md` (which goes with every call, as PRIVACY.md has said since 1.5.2),
names no folder of the user's, on Codex 0.160.1: the screenshot goes as `screen.png`, the skills section is off, and
nothing else Codex adds to the requests built offline holds a folder or profile path; it also says what the call still
carries of the user's own (the front window's title and the title of the window the companion stands on, the screen's
text or picture, a summary's transcript) and that a Codex that renamed the skills key would send that section again.
Readme.md's AI Brain and Remembrance paragraphs say the same in a sentence each, scoped the same way. The first build's
headline, "a Codex call names no folder of yours", promised more than was measured; the second review refused it, and
the re-review found the second round's headline without the `AGENTS.md` carve-out. The re-review also found the
standing-on window's title missing from each list of what the AI brain sends, PRIVACY.md's older lists included, which
named the front window's alone; the third round added it to each.
SMOKETEST.md gains no step: a person cannot bring a refusal or a missing image about on this machine, and both modules'
self-tests assert the sentences word for word.

#### feature/blinkingled-resume

Blinking LED 1.0.7, module-only; MinHostVersion stays 1.0.0. No new check sends real input: each drives the module
through its key-reader and keypress seams. The lane's first build did press for real: its self-test runs (by hand, the
mutation harness's baseline, its 38 mutation runs and the gate) each made the suite's one pair of real Scroll Lock
presses, F113 above, in the owner's session. The first fix round's runs made none of those, through the opt-out below:
each printed its SKIPPED lines, and the real-press counter read 0 in every one but the mutation that makes the engine
count its fake presses as real. That counter saw only the self-test's own instances. The convention runner's own
instance, which the loader shuts down after the self-test has returned, kept the real sender, so a Shutdown that pressed
would have pressed for real with the variable set; the stock Shutdown presses nothing on an instance that never pressed
(RA-090), and the round did not run the one case that puts such a press back. The second fix round closed that in the
engine (below) and added a record of the process's counts after every Shutdown; in its last full mutation run (its
baseline and every case on Blinking LED's flag but the two it refused, below) every --module-selftest=blinkingled run
left that record, and all read
realKeypresses=0 except the counter's own case, which counts fake presses as real by design. In the fourth fix round's
mutation run all 31 --module-selftest=blinkingled runs (its baseline and the 30 cases on that flag) printed both SKIPPED
lines and left "realKeypresses=0 withheld=0 started=35 shutDown=35".

**The finding and the owner's decision (2026-10-14).** A read-only diagnosis (its workflow journal, labels `diagnose` and
`verify:*`; local, not committed) of the owner's report that the displays no longer stayed on after the switch from the
standalone tray app to the module found that one
tick that finds Caps Lock on stopped the module and SAVED `enabled=false`, after which every companion start loaded it
and left it off, with no log line after the host's "module loaded" and a tray row that read "Blinking LED: off" like a
user's Off. The standalone tray app it replaced only quit on Caps Lock and saved nothing, so its next launch blinked
again. The owner confirmed it: "You were right, I had hit capslock and it was not running". Asked how Caps Lock should
behave, they answered: "Can you make it a toggle, so we would add "Resume on application restart" checkbox." What the
owner was told the box does, and what was built: ticked, Caps Lock still stops the blinking, but only until the
companion next starts, and nothing is saved; unticked, 1.0.6's behaviour, a Caps Lock stop stays off through restarts;
ticked by default, so an accidental Caps Lock cannot leave it off for good; it covers the Caps Lock stop only, and a
deliberate Off from the tray or the pane stays off as before; and the module logs its state at every start and says in
the tray row when Caps Lock has stopped it.

**What changed against RA-088, and why.** RA-088 (`#### burn/blinkingled`) persisted the off when a switch-on was refused
under Caps Lock, and rejected refusing while leaving `enabled=true`, because the tray and the pane would read "on" with
nothing running and nothing that would ever start it, the disk/live drift the module's "disk wins" rule avoids. With the
box ticked that rejected shape is now the behaviour, for the refusal and for the tick's stop alike
(`BlinkingLedModule.OnCapsLockStop` and `RefuseEnableUnderCapsLock`). The tray half of the objection is answered. The
tray row no longer reads "on": while Caps Lock has it stopped it reads "Blinking LED: stopped by Caps Lock (resumes at
next start)", or "(off until you pick a speed)" when the off was saved, chosen from what is SAVED, so a failed write of
an unticked box's off reads as resuming, which is what the next start will do (`TrayToggleText`). And something does
start it: the next companion start, or the user's own next tray pick, or a pane Apply that changes on/off or the speed.
The pane half is accepted on purpose, not answered: the pane's "Blink the Scroll Lock light" box keeps reading ticked
during such a stop, because it shows the saved choice, and a Load that reported off would let an Apply made for any
other reason save the off, the very thing the new box exists to prevent. The rest of "disk wins" stands: everything the
user chooses is saved and is what the next start acts on, and the Caps Lock stop is the one state that lives only in
the running instance, by the owner's decision, shown in the tray row.

**How the session stop works.** One instance field, `_stoppedByCaps`, set by the two Caps Lock paths whichever way their
write goes and cleared by the user's next choice: `ApplyState` clears it when `announce` is true, and only a tray pick or
a pane Apply passes true. It is neither saved nor static, because a start is a new instance in a new process.
`ApplyState` does not consult it to keep the stop in force: no timer runs while the blinker is stopped, and nothing in a
session calls `ApplyState` but Init and those user gestures. A stop with the box ticked leaves the speed ticked in the
tray (the saved on), so the two tray handlers no longer skip a pick of what is already saved while the flag is set:
picking that speed starts it again. A switch-on still refused because Caps Lock is still on stops it again and says its
one line, as before. Speech is unchanged: no new line is spoken anywhere.

**A pane Apply during the stop (the fix round).** The pane sends every box on Apply and, during a ticked stop, shows the
saved on, so the first build ended the stop on every Apply: one that only switched "announce" restarted the blinker, and
with Caps Lock still on any Apply spoke the refusal, and one that had unticked "Resume on application restart" then saved
the very off the box exists to prevent. Now `SavePaneValues` reads what Load shows before writing anything and reaches
`ApplyState` during a stop only when the submitted on/off or speed differs from it; an Apply that changes only the other
boxes saves them and leaves the stop alone. Those boxes take effect at the next choice or start. The first fix round
counted unticking "Stop when Caps Lock is on" as one of those other boxes, so the blinker stayed stopped and the tray row
still said Caps Lock had stopped it, right after the user had said Caps Lock should not. The second fix round counts it
as a choice that ends the stop (a stop needs the box ticked, so a change to it during one is an untick): with the
"Resume on application restart" box ticked the blinker starts, even with Caps Lock still on, and the pet says its ON
line; with that box unticked the off the stop saved stays saved and named until a speed is picked, since unticking
"Stop when Caps Lock is on" picks no speed. Once that box is ticked over such an off, the untick still leaves it off
until the next start (the fourth fix round, below).

**A tray pick whose write fails during the stop (the fix round).** Both tray handlers now return before `ApplyState(true)`
when Save() fails while the flag is set. With the box ticked the disk still reads on, so reaching `ApplyState` ended the
stop and started the blinker from the disk, an Off click included, under a log line saying the live state was
unchanged. Outside a stop a failed pick still reaches `ApplyState`, which re-reads the unchanged disk and changes
nothing, as before. The pane's Save had the same shape and kept it through the first fix round: a pane Off whose write
failed during a ticked stop started the blinker and spoke "Keeping the lights on for you." while the pane said the
settings could not be saved. The second fix round gives `SavePaneValues` the same rule, a return before
`ApplyState(true)` when Save() fails during a stop. The pane logs nothing for it: its Save answers false, which the
options window already turns into "These settings could not be saved."

**The saved reason (the fix round).** With the box unticked the off is saved as in 1.0.6, and now its reason beside it,
`stoppedBy=capsLock`, in the same Save(), so the two land together or not at all. A restart then still names the stop:
the tray row reads "Blinking LED: stopped by Caps Lock (off until you pick a speed)" and the startup line "starting: off
(saved by a Caps Lock stop), ...". Without it the first build's row after a restart read a plain "Blinking LED: off",
the same invisible off the owner had reported, for anyone who unticked the box. The user's own on/off clears it: a tray
speed pick, a tray Off (over such an off that is now a write, so the row and the next start read a plain off), or a pane
Apply that changes "Blink the Scroll Lock light". A pane Apply that changes anything else, the speed included, keeps it,
with the box ticked or not, because the feature stays off and the off is still Caps Lock's (the fourth fix round, below,
says why a ticked box does not change that). The reason is read only beside a saved off
(`SavedOffIsCapsLocks`), so one left beside an on means nothing.

**Ticking the box over a saved Caps Lock off (the second fix round).** In the first fix round, ticking "Resume on
application restart" over an off a Caps Lock stop had saved left it off at every later start, with a startup line that
read as if the box had failed. The box says a Caps Lock stop lasts until the companion next starts, and the saved reason
says this off is a Caps Lock stop's, so the next start now resumes it: Init switches it on and clears the reason in one
Save(), before `ApplyState(false)` (`ResumeSavedCapsLockOff`), and the startup line reads "starting: on (resumed from an
off a Caps Lock stop saved), ...". Until that start the tray row says "Blinking LED: stopped by Caps Lock (resumes at
next start)", which is what will happen. A plain saved off, a user's Off or a file 1.0.6 wrote with no reason, is never
touched, whatever the box says. The disk still wins: when that write fails the feature stays off, the line reads
"starting: off (saved by a Caps Lock stop; the resume was NOT persisted), ..." and the row still says it resumes at the
next start, which tries again.

**The same off before that start: the pane does not end it (the fourth fix round).** With the box ticked over such an
off, the second fix round left it off until the next start whatever the user chose in the pane, under a Readme sentence
saying that changing the speed or unticking "Stop when Caps Lock is on" starts it. The final round (the third fix round)
made that sentence true: a pane Apply that changed the speed or unticked "Stop when Caps Lock is on" switched such an off
back on and cleared its reason in its own Save(), as those choices end a session stop. Its review found what that did to
the user's next Apply. The options window re-reads a pane after Apply only when the schema has an Info or Header row
(`RefreshAfterApply`, `src/Portable/Wpf/OptionsWindow.cs:436-440` and `:487-500`), and this schema has neither, so the
pane went on showing the off it had loaded. The next Apply from it, picking another speed or changing only the announce
box, sent that off; `SavePaneValues` compares the submitted on/off with the disk's, which now read on, took it for the
user's Off, cleared the reason and saved a plain off the user never picked. That is the failure this version exists to
end, reached through a path the lane had added, and the review reproduced it (the OFF line spoken, or nothing with
announce unticked, and "Blinking LED: off" in the row). Of the review's three fixes the coordinator chose the first: the
pane half goes. A pane Apply now writes the on/off it submitted and nothing else, so over such an off only ticking "Blink
the Scroll Lock light" switches it on from the pane, and every other change there leaves the off and its reason as they
were. Kept: a tray speed pick still ends it at once, since the tray menu is built afresh on every open (`BuildChildren`,
`src/DesktopAICompanion.Contracts/PluginApi.cs:280`) and a pick never acts on a stale view; and the next start still
resumes it while the box is ticked. Refused: an Info row so that the window rebuilds the pane after Apply, which would
rest the module's rule on when a host chooses to re-read a pane, something the module ABI does not state (it names "the
refresh after Apply" in passing, `PluginApi.cs:390`, and not when one happens), for a row that would exist only for that
side effect; and comparing an Apply with what the pane last loaded instead of the disk, which keeps a second copy of the
pane's state beside the disk, the kind of drift "disk wins" exists to avoid. A session stop with the box ticked never
had the hazard: it saves nothing, so the pane loaded the saved on, and an Apply from it, however long ago it loaded,
sends an off only when the user unticks the light there; the Applies `SelfCheckPaneApplyDuringStop` makes are one such
pane's, never re-read. The Readme now says only what holds: picking a speed in the tray starts it again at once; ticking
the box switches such an off back on at the next start; and until then the options leave it off whatever is changed there but
"Blink the Scroll Lock light". A plain saved off stays off through every pane choice but that one, whatever the box
says, and with the box unticked so does a Caps Lock off: there it lasts until the user switches it on, as 1.0.6's did.

**Not greyed.** The box has no effect while "Stop when Caps Lock is on" is unticked, since nothing then stops the
blinker. Greying it in that state would take `SettingField.EnabledWhen`, which arrived in host 1.1.6, and so would raise
MinHostVersion from 1.0.0; nobody has read which host the owner's other workstation runs, and that is the machine this
fix is for. The schema comment beside the field says so.

**Visibility.** Every start logs one line from Init, after its quiet `ApplyState(false)`, in the module's own vocabulary:
"starting: on, rate Normal, Caps Lock stops it: yes, resume on application restart: yes", "starting: on (resumed from an
off a Caps Lock stop saved), ...", "starting: off (saved by a Caps Lock stop), ...", "starting: off (saved by a Caps Lock
stop; the resume was NOT persisted), ..." or "starting: off (saved), ..." with the same three settings. In Init rather than inside
`ApplyState(false)`, because the self-test calls that directly as its startup probe and its WITNESS pins that the probe
logs nothing. The Caps Lock lines say what the next start will do: "caps lock is on: stopped blinking for this session
and saved nothing, so it resumes at the next start"; unticked, "caps lock is on: stopped blinking and saved it as off, so
it stays off at the next start", or, when that write fails, "caps lock is on: stopped blinking but the off was NOT
persisted, so it resumes at the next start"; the refusal's three are "caps lock is on: did not start blinking and kept
your pick, saving no off, so the next start tries again" (the first build said "saved nothing", which read as if the
pick had been lost, though the pick's own Save() had written it), "caps lock is on: did not start blinking and saved it
as off, so it stays off at the next start" and "caps lock is on: did not start blinking but the off was NOT persisted, so
the next start tries again".

**Settings 1.0.6 wrote.** A settings file holding `"enabled": "false"` keeps meaning off. 1.0.6 saved a Caps Lock stop
and a user's Off as the same value and saved no reason, so 1.0.7 cannot tell them apart, and turning such a file back on
would overrule every user who had switched the feature off on purpose. Its first start says "starting: off (saved)" and
its tray row "Blinking LED: off". So on the owner's other workstation, once 1.0.7 is installed there, switch it on once:
pick a speed in the tray's Blinking LED submenu, or tick "Blink the Scroll Lock light" and Apply. From then on, with the
box at its default, a Caps Lock stop lasts until the next start. The Readme says so in one sentence.

**No real input in agent runs (both fix rounds, the owner's decision of 2026-10-14).** The module self-test makes one pair
of real Scroll Lock presses per run (F113), and the first build ran it about forty times in the owner's session. Asked,
the owner chose "Skip on agent runs": agent runs on the maintainer's box skip the real press and say so loudly, so nothing is
pressed in the owner's session, while CI and normal runs still press, so the real path stays tested. Agent runs are to
set the variable; nothing in the repo sets it for them, and a run without it presses as before. With
`DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT=1` the self-test hands its main instance's keypress to its own fake sender,
so the log-wiring check still runs; each of the two checks that are about the real press ("the real SendInput call
completes without throwing" and the pairing check) prints one line in its place, "SKIPPED: real input disabled by
DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT=1; the real Scroll Lock press was not made, so this check did not run: ...",
on every such run; and the run's last check asserts that the engine's real-press counter (`RealKeypressCount`, which
advances before SendInput answers) stayed 0. Unset, the run is as before. The decision takes an injected environment
reader (`RealInputDisabled`) and is checked on fakes before anything could press, keyed on the variable's literal name,
so a typo in the constant fails it; it fails closed, so a decision that gets its own cases wrong is never trusted to
allow a press, and its mutation cases send nothing. "1" with spaces around it counts (cmd's `set X=1 && ...` keeps the
space); anything else, "true" included, keeps the press. `tests/Test-ModuleSelfTests.ps1` accepts that one SKIPPED shape,
printed beside the module's OK, only when the variable is 1 in its own process; the same line in a run that did not ask
is a problem, and any other line starting SKIPPED: is graded as the SKIP it is, so the new word is no way round the SKIP:
rule. The runner passes each module's skipped checks on as a `SELFTEST-NOT-RUN:` line, and `tests/run-gate.ps1` lists
them, under "NOT RUN, BY REQUEST", ahead of both GATE FAILED and GATE PASSED whenever the variable is 1. Three source
invariants pin both rules, because the runner's refusal can be seen failing only in a run with the variable unset, which
presses. Refused: `SelfTestProbe.Skip()`, whose SKIP: line fails every runner by design and would turn every agent gate
red; and no real press by default, which would leave the real path untested in CI. The counter's own case cannot take a
pinned sender away, which would press, so it makes the engine count every press as real, fakes included, which moves
the counter with no SendInput.

**What the first round left open, and the second closed.** The pins reach only the instances the self-test makes. The
convention runner's own instance, which the real loader Init'd and shuts down at ShutdownAll after the self-test has
returned, kept the real sender, and the counter check, run before that, could not see it; the gate's header read "no
real input was sent" whenever the variable was 1, which nothing measured; and a module self-test that ignored the
variable passed the runner as a plain OK. So:

**The engine withholds.** `ScrollLockBlinker.SendScrollLockKeypress`, the one place SendInput is called, asks
`RealInputWithheld` first, and in a process whose command line runs a self-test (`--module-selftest=<id>` in any case,
or any `--<name>-selftest`, read the way Program.cs dispatches them) with the variable at 1 it counts the press as
withheld, answers it refused with win32=-2, logs "real input withheld: a Scroll Lock press was not sent, because this
self-test process runs with DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT=1" when a log sink is wired, and returns before
the real-press counter and the call. The convention runner's own instance shuts down after the self-test's last
Shutdown has dropped the engine's static sink, so a press withheld there logs no line; the process record counts it
either way (withheld=N, below). That covers every instance at once, the loader's included. A normal start carries no self-test flag, so
the variable cannot stop the shipped module blinking. The first round had refused this switch because no check could
prove it without a real call; three things prove it now without one: the decision (`WithholdsRealInput`) is checked on
fake environments and command lines, both ways, inside the fail-closed opt-out check; the self-test asserts that THIS
process withholds, through the engine's own cached reading; and a source invariant pins the order (the withhold check
returns before the counter and the call) and that the engine has no second SendInput call, since removing the check
could be seen at run time only by the press it prevents.

**A reading after every Shutdown.** In its own `--module-selftest=blinkingled` process, every Shutdown rewrites
`dp-module-blinkingled-realinput.txt` in TEMP with the process's counts, "realKeypresses=N withheld=N started=N
shutDown=N" (`RecordRealInputAfterShutdown`). The last Shutdown is the loader's, so the record it leaves is the reading
taken after every instance has shut down, and started equal to shutDown says so. It is written whatever the engine
decided about withholding: a first cut wrote it only when the engine withheld, and the three mutation cases that break
that decision then left no record at all, the runs where the reading matters most. The self-test's own
counter check keeps its place and its label now says what it can see: "with real input withheld, the engine's
real-press counter stayed 0 through every instance this self-test ran". A host change was considered (the convention
runner reporting what a module logs during ShutdownAll) and not taken: it would add a convention to the host for one
module, where a record the module writes and the runner reads needs neither.

**The runner checks it, and only a measurement says nothing was pressed.** With the variable at 1,
`tests/Test-ModuleSelfTests.ps1` requires, for each module in its `$realInputModules` table (Blinking LED alone),
exactly the listed real-input SKIPPED lines, the counter's PASS line, and the record reading realKeypresses=0 with
started equal to shutDown; a module not in the table may skip nothing for real input. Each rule is graded on its own
and whatever the module's verdict, and a failure is a problem, never an OK. Only when all three hold does it print the
measured sentence, as a `SELFTEST-NO-REAL-INPUT:` line: "no real input was sent: its self-test measured ... ; and after
all N of its instances shut down, the convention runner's own included, the process's real-press counter read 0". The
gate's header now says what was requested, "NOT RUN, BY REQUEST (DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT=1: real
input withheld by request):", and lists those measured lines under it as "measured: ...", or "no module measured
whether it sent real input". Before it throws, the runner also prints each problem on its own "PROBLEM:" line, since
a thrown message is wrapped at the console's width. The gate runs the runner under `Set-StrictMode -Version Latest`,
which a script invoked with `&` inherits, and the first cut of these rules threw there on every module ("The property
'Count' cannot be found on this object": an if-expression handed back an empty array unrolled, as `$null`), while every
run of the runner on its own, and the mutation harness's runner flag, which started it with `-File`, passed. The
round's first gate run failed on it. `tests/mutate-selftest-guards.py` now starts the runner under strict mode as the
gate does; that flag fails the first cut's shape with the gate's own error and passes the fix.

**What the runner says (the final round).** Its `SELFTEST-NOT-RUN:` line quoted only the first real-input SKIPPED line,
so the runner's summary and the gate said two checks did not run and named one; it now names every skipped check, taken
from each SKIPPED line and joined with " / ". And a run with the variable not 1 now prints, before the first module
runs, "REAL INPUT: DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT is not 1, so blinkingled's self-test makes one pair of
real Scroll Lock presses", as the mutation harness says at its start; until then a run without the variable pressed
with no line about it. Two source invariants pin them: the NOT-RUN line's shape, since no rule grades the runner's own output,
and the REAL INPUT line's condition and order, since it prints only in a run that presses.

**The cases.** "burn: Shutdown clears a light this session never drove again" ran in the second round with the variable
set, fired on its own label, and pressed nothing: its press is the loader's instance's at ShutdownAll, which the engine
now withholds, and its run's record read realKeypresses=0. Scroll Lock read dark on this box during that run, so the
instance had nothing to press there; a neutralised variant showed the press reaching the check (the SendInput call
removed and its import pointed at a DLL that does not exist, the burn mutation applied, the engine's real Scroll Lock
reader answering lit), with a record of realKeypresses=0 withheld=1 started=30 shutDown=30, before both sources were
restored byte-identical (`demo_burn_withheld.py` in the lane's temp folder, local). Two older cases test the real press itself and cannot fire
with the variable set, because the check each breaks is skipped then: "the SendInput P/Invoke resolves against a DLL
that does not exist" and "the self-test's pairing keypress is deleted". `tests/mutate-selftest-guards.py` refuses
both on their own line when the variable is 1 (`NEEDS_REAL_INPUT`), counts them apart from the cases that ran, and says
at its start which way the run goes; unset, they run and press as before. The first round's record gave the wrong
reason for not running the burn case: it can fire with the variable set (its check runs on fakes), and before this round
it could also press with it set.

**Not taken.** The diagnosis also offered resuming by itself once Caps Lock reads off again, which the owner did not
ask for, and which a stale Caps Lock read could turn into a blinker that flaps on and off; and an hourly heartbeat line
with the press counts, which is diagnostics beyond this decision. Whether a Caps Lock read on the companion's thread
can be stale is still unmeasured (measuring it means pressing a toggle key in the owner's session); the fix limits any
such stop to a session, and BACKLOG N-blinkingled-resume-02 keeps the question. The reviews' optional wording items were
left: the startup line's capital "Caps Lock stops it" beside the lowercase "caps lock is on:" lines, and an Info row under
the box saying it covers only a Caps Lock stop and that picking a speed starts it now.

**The guards.** `SelfCheckCapsLockResume`, in the module self-test, models a restart as a fresh RecordingHost seeded
with what the previous session saved and a fresh module Init'd over it, with the Caps Lock stop driven through the
engine's own `Tick()` and both key readers and the keypress pinned: the box sits right after "Stop when Caps Lock is
on" with the owner's label, defaults to ticked, and a saved untick loads unticked (WITNESS); the startup line names the
state and follows an unticked box (WITNESS); with the box ticked a Caps Lock stop saves nothing (beside the WITNESS that
the same stop with it unticked moves the save count), its line and the tray row say it resumes, and a fresh instance
blinks; unticked, the off is saved, the row says "off until you pick a speed" and a fresh instance stays off, its
startup line says "off (saved by a Caps Lock stop)" and its row still names the stop; a tray Off over that off makes it
the user's (the next start reads a plain off), a tray speed pick and a pane Apply that ticks the light clear the reason,
a pane Apply that changes only another box keeps it (WITNESS), and a file 1.0.6 left at off reads as a plain saved off;
a tray Off and a pane Off are saved and stay off after a restart with the box either way; a switch-on refused under Caps
Lock saves nothing beyond the pick with the box ticked, says it kept the pick, still speaks its one line, is named in the
tray and blinks at the next start, and with it unticked saves the off (WITNESS) and its reason; picking the still-ticked
speed after a stop starts it again, and the row then reads "Blinking LED: on" (WITNESS that the row check can fail).
`SelfCheckPaneApplyDuringStop` drives a ticked stop through the pane: an Apply that changes only another box leaves it
stopped, silent and named with Caps Lock off, and with Caps Lock on speaks no refusal and saves no off even when it
unticks the box; a changed speed is refused again under Caps Lock and starts it with Caps Lock off (WITNESSes); a pane
Off ends the stop, and so does an Apply that unticks "Stop when Caps Lock is on", which starts it with Caps Lock still on.
`SelfCheckFailedTrayPickDuringStop` fails the write of a tray speed pick, a tray Off, a pane Off and a pane speed change
during a ticked stop, and the blinker stays stopped and named; the same tray pick with a write that lands starts it
(WITNESS). Ticking the box over a saved Caps Lock off keeps it off for the session with a row that says it resumes, the
next start resumes it, clears the reason and says so, and with that write failing it stays off and its line says the
resume was not persisted; a plain saved off stays off with the box ticked (the 1.0.6-file and tray-Off checks above).
Before that start, with the box ticked over such an off, a tray pick of its speed starts it, saves it on with the reason
cleared, speaks the ON line and leaves the row at "Blinking LED: on". Two Applies from one pane that is never re-read
between them, the box ticked in the first, leave the off and its reason exactly as saved after each, with nothing
blinking, no ON or OFF line said and a row that says it resumes at the next start: once for a speed change followed by
the old speed, and once, under Caps Lock, for an untick of "Stop when Caps Lock is on" followed by an announce-only
change (the fourth fix round, in place of the final round's two checks that the pane ended the off; the tray pick, on
the same off, is their positive control). A plain saved off with the box ticked, and a Caps Lock off with the box
unticked, stay off through a pane speed change and untick (WITNESSes).
The opt-out's decision and the engine's are checked both ways on fakes, this process is checked to withhold, and the
counter at 0. The RA-088 and F110 sections above keep pinning the saved off, now with the box unticked through the pane,
and their labels say so. Every check the lane added to the module self-test or relabelled there, and every source
invariant it added, has a killing mutation: the module's under `# ---- lane feature/blinkingled-resume ----` in
`tests/mutate-selftest-guards.py`, the invariants' under the same anchor in `tests/mutate-hardening-guards.py`. So do
the runner's real-input rules, through that first harness's `MODULE_SELFTESTS` flag, which runs
`tests/Test-ModuleSelfTests.ps1` itself (the exact SKIPPED set, a line missing and a line not expected, the counter's
PASS line, and the record's presence, shape, instance counts and real-press count), except three that no Blinking LED
run can reach: a record the runner could not delete, and a real-input SKIPPED line from an uncovered module or from a
module outside its table. Every case under that anchor FIRED on its own label when it last ran, but not every one last
ran on the committed tree. Asked whether to re-run every Blinking LED case on the final code (about two hours), the
owner chose "Only the affected ones". So the fourth fix round ran, on the committed tree and with the variable at 1,
31 cases of `tests/mutate-selftest-guards.py`: the three it added, the two it re-pointed, the one it renamed, the tray
pick's case (its check's code changed), and every case whose target sits in or beside code that round changed, one older
case among them ("a failed tray Off is silent again"); all 31 FIRED, and the commit's MUTATION block names them. The
rest last FIRED on an earlier tree, on target bytes this round did not change: 12 lane cases on the final round's (the
counter's own case, the refusal's "..., seen by the next start", the two F110 cases and the eight runner-flag cases other
than the renamed one), and 43 lane cases and 22 older Blinking LED cases (the `burn:`, `followups:` and earlier ones)
on the second fix round's, whose run took every case then on Blinking LED's flag. The 19 cases under the anchor in
`tests/mutate-hardening-guards.py` last FIRED in the final round, on files this round left byte-identical. The two
cases that test the real press itself (`NEEDS_REAL_INPUT`) have not run in this lane: they are refused with the variable
at 1, and run, and press, only where it is unset.

#### feature/stale-pet-xml

App 1.5.1, host only, BACKLOG N-stale-pet-xml-01, BUG-016. The owner, 2026-10-14, about bursts of `no eligible
positive-probability transition: pet 'pink_sheep' border state 173, where=TASKBAR, 6 candidate(s) declared and none
eligible` in their log: "look into it, lets have a clean release". Built in one round and corrected in a second, after
the review of its first commit (73e8cc3); this section describes the result and says where the second round changed it.

**Root cause: the default companion ran a copy of its file saved in settings.json, and nothing refreshed the copy from
the file.** The "" slot of the mix was built at launch from the `localxml=` file, else settings.json's "xml", else the
embedded default (`src/dotNet/StartUp.cs:202-206` at da7f7ed), and `:231` saved the same text back, so a `localxml=`
file or the embedded default became the copy too; the companion's own file was never read for it. Otherwise the copy
was written by `LoadNewXMLFromString` (`:1535`), which "Use this companion" and a file dropped on a companion
(`FormCompanion.cs:2454`) both reach. A Companions-pane Update or a Companion Studio install rewrote only the file, the
pane then said "Restart to see the change (this is your default companion)", and the restart read the same copy again.
The weekly check and the Update badge hash the file (`CompanionFreshness.cs`), so once the file was current the stale
copy was reported up to date and never offered again. Measured on copies of the owner's data, their folder untouched:
the copy was byte-identical to `Companions/pink_sheep/animations.xml` at 0936658 (1.0.0, sha256 89452d68...), the
installed file was 1d48936's fix (3d33ff50..., the catalog's hash, installed 2026-09-25), and the two differed by
exactly the two `only="taskbar"` edges 1d48936 added to states 173 and 182. Every one of the day's 127 warnings named
state 173 with 6 declared candidates, the copy's count; the file has 7. 1d48936's real-app confirmation ran the fixed
XML through `localxml=`, which bypasses the copy, so it could not have seen this. The shape came from upstream
desktopPet, which kept the whole active pet in user settings but had no library and no catalog updates for it to fall
behind; this repo added both in its first commit and kept the copy as the default companion's only source. Who could
meet it: anyone whose default companion is a catalog companion updated after they picked it (32 companion files have
changed since v1.0.0, pink_sheep and 31 Shimeji), anyone who installed over their default with Companion Studio, and
portable-zip users whose default is a bundled companion an app upgrade replaced. Companions added by id never were: each
staging reads the file.

**The decision: the file wins over a copy OF it, a hand-loaded XML is never replaced, and the copy says which it is.**
`ActiveCompanionSource.Resolve` (new, `src/dotNet/ActiveCompanionSource.cs`) decides at launch; the constructor stages
what it returns and hands it to `ActiveCompanionSource.Commit`, which writes it back with its marker. A copy that is byte
for byte the file is a copy of it whatever its marker says; that test comes FIRST (the second round moved it ahead of
the custom test), so a 1.5.1 drop of the companion's own file, which the drop declares custom, is marked a copy at the
next launch instead of being pinned as hand-loaded and denied the file's next update. A copy declared a copy of the
file, or an undeclared one of the same companion, that differs from the file is replaced by it. A copy declared
hand-loaded is kept whatever the file says. `localxml=` still comes first. With no file the copy runs, as before; with
no copy the file runs; with neither the built-in does. If the file does not stage, the copy runs instead and nothing is
written, so a broken file is never worse than 1.5.0. Every writer declares the source in a new key beside the copy,
"xmlSource": `pack:<sha256>` from "Use this companion" (`OptionsController.UsePet` passes `fromPack: true` to
`LoadNewXMLFromString`), `custom:<sha256>` from a drop (`FormCompanion.Form2_DragDrop`, `false`) and from `localxml=`.
The hash is that of the xml the marker describes, so a marker an older build carried through untouched while changing
the xml (the document's extension data round-trips an unknown key) describes nothing and is ignored. The key is merged
and cloned with the document, and `SetXml` and `TrySetPetAssets` write it in the same write as the copy.

**Why a declaration and not an inference.** The text alone cannot say whether it is a copy of a file. The obvious rule,
"a file exists for the default's id and differs from the copy, so the file wins", is wrong for a drop: a drop does not
change the default companion's id, so a companion XML dropped on Pearl sits under `pink_sheep` beside pink_sheep's file
and differs from it, and that rule would throw the user's XML away at the next launch. The callers know which they are,
so they say it.

**Undeclared copies, which is every 1.5.0 user, are judged by the header name.** For those the copy counts as a copy of
the file when both carry the same header name: the petname, else the title less a legacy " (converted)", read by
`CompanionCatalog.HeaderNameFromText`, the rule every card and menu uses, factored out of `ReadHeaderNameUncached`
unchanged. Checked against every committed version of all 32 changed companions: each has had one petname throughout,
while 31 titles lost " (converted)" on 2026-09-09. A copy naming a different companion is kept and marked custom, so the
question is asked once.

**The cost that remains, and the side file that makes it recoverable.** The first round recorded the cost as "a
hand-edited copy of the SAME companion", which understated it, and its review said so: the header name is not unique.
The catalog ships three pairs that share one (the built-in eSheep and esheep64, both "eSheep"; fox and pink_fox, both
"fox mate"; red_sheep and the Shimeji Rick, both "Rick"), and a hand-edit keeps its name. So ANY undeclared copy whose
header name equals the file's is replaced on the first 1.5.1 launch: a hand-edit of the same companion, or a different
companion of the same name dropped on it under 1.5.0 or earlier (a drop keeps the default's id). Left at that, the copy
would live on only in settings.json.bak, which `AppSettingsStore` rotates on every save, so the next settings write of
any kind would take it. So `Commit` first writes a copy replaced on its header name alone (`Choice.KeepReplacedCopy`,
true only when the copy carried no usable marker) to `replaced-default-companion-<sha8>.xml` in the data folder,
beside settings.json (`KeepCopyAside`): one file per distinct copy, so the same copy again finds its file and writes
nothing; a file of that name holding other text is never overwritten (the copy then goes under its whole hash); and the
write is read back by hash before it counts. If the side file cannot be written, nothing is saved: the file runs this
launch, the copy stays in settings, and the next launch tries again. The launch line names the side file. A declared
copy of the file (`pack:`) is a verbatim copy of an older file, so it is not kept. What remains is that the namesake or
the hand-edit is no longer what runs on screen, and is recoverable from the side file. That is the lane's decision on the
review's finding; the owner has not been asked to accept it, and that question is the coordinator's.

**Two cases the name rule gets wrong the other way, recorded rather than fixed.** A 1.5.0 copy whose companion a
Companion Studio install renamed over its id before the first 1.5.1 launch names a different companion, so it is kept
and marked custom, and the renamed file does not reach the default. The Companions pane offers Use only on a companion
that is not active (`src/Portable/Wpf/CompanionsPaneControl.cs:336`), and the default is the active one, so the way back
is Use on another companion, then Use on it again, which resets the companions on screen and declares it a copy of the
file again. And a 1.5.1 drop of the companion's own file is declared custom by the drop and re-marked a copy at the next
launch because it equals the file, unless an Update of that companion lands in the same session first: the copy then no
longer equals the file and stays pinned as hand-loaded (a drop keeps the default's id, so it is still the active
companion), and the same two Uses undo that. Making the drop
itself compare against the file would close that window; it was not done, because it adds a second reader of the
companion file to the drop path for a window of one session.

**The launch line says only what is true, and only after the write.** `Resolve` proves that the copy differs from the
file, never that it is older (a hand-edit, a namesake or a file that went backwards all differ too), so the line says
"the copy saved in settings differed from it and has been replaced", with ", and that copy is kept in <path>" when
there is a side file. The first round said "was an older version of it", and logged the line BEFORE the write: when the
write failed, the line claimed a replacement that the next launch would announce again, and the warning beside it ("the
previous pet will return next launch") was false on that path, because the next launch resolves to the file again. Now
the line comes after `Commit` and reports a replacement only when the write succeeded, and `Commit`'s warning for a
launch that ran the companion file reads "The active pet could not be persisted; it runs from its companion file again
at the next launch."; the old sentence stays for a pet that is not its companion file, where it is true. The write and
its order live in `Commit` rather than in the constructor so that `--hardening-selftest` can hold the ORDER as behaviour
(with the side file impossible, the save is never called), where a source invariant could only hold the text.

**Refused: dropping the copy and always reading the file.** A hand-loaded XML has no file, and the copy is what keeps a
companion whose file went missing running (an MSI install carries no bundled companions). Moving the payload out of
settings.json altogether is the schema change F361 and RA-302 already scheduled for a host release of its own; this keeps
the copy and adds one key, so 1.5.0 reads what 1.5.1 writes. **Refused: swapping the default companion in place after an
Update.** The only in-process path that restages it, `LoadNewXMLFromString`, closes every companion of every type and
resets the mix, which is why `ReloadPetType` asks for a restart (its doc comment); the restart is now true.

**Two status lines made true.** `ReloadPetType` decides once whether the file is the default companion's
(`targetIsDefault`) and answers NeedsRestart on both paths, so with copies of the default also added by id it reloads
those and still asks for the restart; before, it answered Reloaded and the pane said "The one on screen was reloaded."
while the default kept the old definition. The pane now says "The copy you added was reloaded; restart to see the change
on your default companion." (or "The N copies you added were reloaded; ..."). A default running a hand-loaded XML
(`activeSlotIsCustom`, set where the default is staged) is not the file's, so an Update of that id reloads only the
copies added by id and asks for no restart that would show nothing.

**Nothing to run now warns on every launch, recorded rather than changed.** With no companion file and no copy for an
id other than the built-in (`Kind.None`), the launch logs "Configured pet rejected: there is no companion file (...)
and no copy in settings" at warning level, runs the built-in keyed as eSheep (F305) and saves nothing, so the warning
comes back on every launch; 1.5.0 ran the built-in quietly under the configured id and saved it. It needs an empty
"xml", which every launch otherwise fills, so it is close to unreachable, and the new keying is the one F305 asks for.

**Guards.** `--hardening-selftest`'s `CheckActiveCompanionSource` drives with fakes every outcome of `Resolve` (not
its catch for a reader that throws), `Commit` with its side file written, reused, moved to its whole-hash name and
impossible, and its refused save of a companion file, and the launch line for a companion file (a replacement saved
with its side file, a replacement not saved, nothing replaced) and for a settings copy. It does not drive the
built-in's wording ("the built-in default") in `Commit` or the line, `Commit`'s warning for a pet that is not its
companion file ("the previous pet will return next launch"), the line for `localxml=`, for nothing to run or for no
choice at all, or `KeepCopyAside`'s other three refusals (no copy, no data folder, both names taken). It then goes
through the real library under the run's isolated data root and the real settings store, written and reopened: a
stale copy loses to a newer file and the settings file then holds the file's text and its
marker (through Merge, and through Clone on the recovery path); the copy it replaced is in its side file, named by its
hash; with the side file impossible nothing is saved; the same copy again writes no second file and a side file holding
other text is left alone; the next launch replaces nothing; and a custom XML with no file runs and stays as it is. Seven
source invariants in `tests/runtime-hardening-selftest.ps1` pin what no self-test can reach (the constructor's resolve,
its fallback, its one `Commit` handed the store's `SetXml` and the data folder and then its one launch line handed
`Commit`'s answer, the callers' declarations, the reload's answer, the pane's sentence, and that a companion added by
id is staged from its file), and the F305 persist invariant is re-pointed at the `Commit` call. Mutation: the
`stale-pet-xml:` cases in `tests/mutate-selftest-guards.py` and `tests/mutate-hardening-guards.py`, each FIRED on its own
label (the commit names them and their counts), among them one for each check the first round left without a case and
the review's surviving mutant (`Source = ""` on a first launch). Measured with a copy of the built assembly, driven by reflection over copies of the owner's settings.json and pink_sheep file (no launch): the first launch's resolve ran the file (3d33ff50..., 7 border nexts at state 173 where the copy had 6), `Commit` kept the 1.0.0 copy in `replaced-default-companion-89452d68.xml` (its sha256 equal to the copy's), then saved the file's text with `pack:3d33ff50...`, and the line said so and named that file; the second resolve replaced nothing and wrote no second side file. Not done here: the real-app launch
over a stale copy, which is the coordinator's.

#### feature/tray-questions

App 1.6.0, host and Contracts. The owner, 2026-10-15, starting from "I turn on meeting recording and forget to turn it off"
and asking for "selectable or clickable buttons" a module could put to the user. Remembrance and Reminder are the first
consumers (lane feature/meeting-end); this section is the host half.

**A question is a card by the clock, never the speech bubble (owner, 2026-10-15).** The first proposal put buttons in the
bubble, stopped the companion while it asked and walked it to the middle of the taskbar. The owner chose "tray only
notifications ... not messing with the movement engine": a bubble follows a companion that walks at a median 100 px/s
across the 53 bundled companions with a level walk (25 px/s at the slowest, measured from their XML), so its buttons
would be a moving target, and host-directed walking would cut into the engine with the most regression history
(monitor pinning broke three times). So `FormSpeech`, `FormCompanion` and `Animations` are untouched. The optional bubble
heads-up is a Preferences box, off by default (owner), said on a question's FIRST SHOW, not when it is posted, which
could be mid-presentation, and it carries no part of the question.

**An app-owned card, not a Windows notification (owner, 2026-10-15, on effort and sustainability).** A toast with buttons
needs a registered AUMID (the MSI creates no shortcut; the portable ZIP cannot register one cleanly), a Windows-SDK
target framework and either the Windows App SDK runtime or the unmaintained Community Toolkit compat library, plus
activation after a restart; Microsoft has changed the recommended API three times, policy can switch toasts off, and
Do Not Disturb hides them, which is wrong for "your recording stops in two minutes". The card is all in-repo, testable
by the self-test and UI Automation, and identical in the MSI and the ZIP. Its cost, accepted: no Notification Center
history, and the presenting/fullscreen check is the app's own.

**Hidden from screen capture (owner, 2026-10-15).** Teams and Zoom screen sharing are not "presenting" to Windows, so the
presenting hold does not cover them, and a meeting title on a card must not appear in a share.
`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` (WDA_MONITOR before Windows 10 2004). The owner accepted that it is
missing from their own screenshots and recordings too; `--trayquestions-selftest` renders the card's contents to
`%TEMP%\dp-trayquestions-card.png` for a visual check instead, since a screenshot shows nothing where it sits.

**A separate interface, and no soft fallback.** `ITrayQuestions`, queried as `host as ITrayQuestions`, for
ICompanionStandDown's reason: an IHost member changes RecordingHost and so every module's payload. A module that uses
it declares MinHostVersion 1.6.0; the types live in Contracts, so code naming them fails to load on an older host
whatever the cast would say. Module self-tests carry their own double (a RecordingHost subclass); ModuleKit carries
none, so no other module republishes.

**The host applies nothing.** A timeout answers with the module's TimeoutChoiceId exactly as if that button had been
pressed, and the module acts; so every no-answer behaviour lives in the module, where its tests are. The Task never
faults (every ending is an Outcome, Refused included) and completes on the UI thread with continuations run
asynchronously, so module code never runs inside the host's click handler or timer tick. The question is copied at
ask time, and refused rather than trimmed when it breaks a limit (2-4 choices, labels 1-24 characters and unique
ignoring case, text 1-240, timeout 15 s-60 min, deadline 5 s-24 h).

**Held while presenting, fullscreen on the tray's monitor, or locked.** The shell's `SHQueryUserNotificationState`
(the call Reminder's PresentationState makes) plus a fullscreen window on the TRAY's monitor, per the owner's per-monitor
rule of 2026-10-07; QUNS_NOT_PRESENT (locked, screensaver) holds too, because nobody can answer a locked screen. A held
question's countdown stands still unless RunsWhileHeld (its no-answer outcome is safe unseen: "stop and save the
recording"), and it gets at least 30 s when shown again. QUNS_QUIET_TIME is Windows' post-setup quiet hour and is held
as "quiet"; Windows 11's Do Not Disturb may not reach the shell call at all (to be verified on a real machine; Reminder's
PresentationState comment calls QUIET_TIME Do Not Disturb, which it is not).

**One card at a time; urgent first.** Urgent = RunsWhileHeld or a Deadline: it goes first and may take the card from an
ordinary question, but never from another urgent one. A click within 500 ms of a card appearing or changing is
ignored, so a click aimed at the previous card cannot answer the new one. A gap of more than 5 s between ticks (the
machine slept) costs a visible card one second, not the night.

**The card never takes the keyboard on its own.** Shown without activation, `WS_EX_NOACTIVATE`, `WM_MOUSEACTIVATE` answered
`MA_NOACTIVATE`: a card appearing while the user types does not swallow a keystroke, and a button press answers
without moving focus. Clicking the card's body is the user asking for the keyboard (the no-answer choice is focused,
the safe one); Esc or an answer hands focus back. `WM_ENABLE(false)` from a modal Options or About is undone, or the
buttons would be dead while the countdown ran. No close button: one of the choices always ends a question.

**Placed without messaging Explorer.** `SHAppBarMessage` is a synchronous SendMessage to Explorer and hangs the UI thread
when Explorer does; `FindWindow("Shell_TrayWnd")`, `GetWindowRect`, `GetMonitorInfo` and the `TrayNotifyWnd` child read the
window manager's state instead (TrayAnchor). Primary taskbar only, where the notification area is.

**The log never carries the question.** Module id, the module's Key ([A-Za-z0-9._-], so safe verbatim), outcome and
choice id; never the text, title or a label, which can carry a meeting title in a file users attach to issues.
Asserted on the captured LINES by the self-test, with sentinels, and on the statements by a source invariant.

**The tray.** "Answer: <text> (<module>)" at the top of the menu shows the current question even through a hold and gives
it the keyboard; "No answer (n)" lists the session's unanswered questions with the choice that was applied (memory
only, newest ten); the tooltip says a question is waiting. Every icon add and re-add now goes out as TrayDisplayName,
because Windows 11 files the text of an icon's add as its permanent name in the notification-area settings, and
`ReassertIcon` used to pass the live tooltip to `TrayPromotion.PromoteOnce`.

**DESKTOP_AI_COMPANION_DEBUG_WINDOW=1** opens the debug window as SHIFT does, for `tests\tray-question-smoke.ps1`: a
synthesised SHIFT was released before a slow first start read the keys, and an agent's shell on the owner's machine
dropped synthetic input altogether (GetAsyncKeyState never saw it, 2026-10-15).

Verification: `--trayquestions-selftest` (the rules, the queue against a fake clock, placement on 4 edges x auto-hide x
4 DPIs x either tray end, a real card window, the host entry point, the log), ten source invariants under the lane's
heading with ten mutation cases (`--only=tray-questions:`, 10/10 FIRED), and the hand-run smoke test, 17/17 on the
owner's machine at 150 % DPI (card 12 px in from the work area's corner, never foreground, typing elsewhere unaffected,
press / click-and-Enter / timeout, the tray's "No answer (1)", no sentinel in the log). Not done here, and in
SMOKETEST.md: a PowerPoint slideshow and a fullscreen video holding the card, its absence from a Teams screen share,
Narrator, the taskbar on another edge.

#### feature/meeting-end

Remembrance 2.4.0, Reminder 1.1.0 and `shared/MeetingApps`, the module half of feature/tray-questions; both modules
declare MinHostVersion 1.6.0. The owner's decisions of 2026-10-15, from "I turn on meeting recording and forget to turn
it off".

**Why it was needed, measured.** Nothing watched a recording after it started: the calendar meeting was read at start
and its end ignored, there was no timer, no meter and no length limit. A forgotten recording kept capturing the room
(its transcript may go to a CLI or cloud summary), kept the machine awake through the silent keep-alive stream, and on
a 48 kHz float stereo device stopped saving after about 3.1 hours: raw tracks were written in the device format (about
1.3 GB an hour, measured), NAudio refuses a WAV write past 4 GB, and `AudioRecorder`'s DataAvailable handler swallowed
the exception.

**Microphone use comes from Windows' record, not from audio.** `shared/MeetingApps` reads the key Windows keeps for its
microphone indicator (HKCU `...\CapabilityAccessManager\ConsentStore\microphone`, packaged and `NonPackaged`): an app
holds the microphone when its `LastUsedTimeStart` is set and its `LastUsedTimeStop` is zero. Meeting apps are Teams and
Zoom (owner), matched by subkey name before the key is opened, so no other app's times are read. An absent or
unreadable record is "not available" with a reason, never "nothing in use". Source-linked into both modules with its
self-check, as `shared/CodingAgentCli` is, so one copy cannot drift from the other.

**Four questions while recording, one at a time.** R3 Longest recording (1-8 h, default 3, owner: "3 hours will be
fine"); R1 the meeting looks over, when Teams or Zoom lets go of the microphone or nobody talks for 5 minutes; R4 a
later calendar meeting has started; R2 the calendar end has passed. R3 > R1 > R4 > R2, a higher one withdraws a lower,
and a withdrawn question is not spent. R1 and R3 are RunsWhileHeld: their no-answer outcome, stop and save, is safe to
apply unseen, and a recording that runs on through a presentation is the failure being fixed. "When the meeting ends:
Off" asks only R3, the length limit, which applies in every mode. Every threshold is in `WatchTuning`, because the
silence numbers need real audio to tune.

**Silence means 15 minutes, not 5, while Teams or Zoom still holds the microphone (owner).** A call where everyone
listens is quiet but not over. Silence alone never asks before somebody has talked or the calendar end has passed, so a
lobby waiting for the host is not ended. App release needs the app to have held the microphone for 60 s during this
recording, released it for 15 s, no other meeting app on it and the system track quiet for 20 s, so a mute (which does
not release) or a device switch does not end a call.

**Trim only when nobody answers (owner).** An unanswered R1 under "Warn, then stop" stops and saves the recording cut to
the detected end plus 30 s; Stop now saves everything, because the person who pressed it saw the card and knows what
they recorded. "Remind only" keeps the whole recording on no answer. The cut audio is deleted for good, which
PRIVACY.md says.

**Offer to record is off by default (owner).** R5, Teams or Zoom on the microphone for 20 s during a current calendar
meeting with nothing recording, asks once a meeting; nothing is read for it while the box is off.

**Tracks converted as they arrive, to 16 kHz mono 16-bit (owner).** About 115 MB an hour a track instead of 1.3 GB, the
format Whisper is fed anyway, through the decoder, downmix and WDL resampler the offline mix used. The resampler is
driven directly rather than through `WdlResamplingSampleProvider`, which reads a short feed as the end of the stream and
zero-pads (10 s of 48 kHz stereo came out as 320 samples). Held to the old offline chain within 1 LSB and 2 samples. A
format with no decoder is written as before and converted at the stop. Writers are RF64-enabled as a backstop, and a
failed write now stops that track, is logged once and shows a warning on the Status line.

**A storage folder inside OneDrive is warned about, never moved (owner).** Documents is often redirected into OneDrive,
and the default storage folder with it: every recording uploads, and audio deleted by the 72-hour purge or a trim stays
in OneDrive's recycle bin. The Status line names it; where recordings live stays the user's call.

**Reminder: join when the meeting starts, not before.** The lead-time line no longer says "Join link is in the tray": a
reminder minutes before a meeting is not the moment to join (owner). A meeting with a join link asks Join / Dismiss from
its start until 3 minutes past (owner), withdrawn once Teams or Zoom holds the microphone, and not asked of somebody
already in a call. A one-off personal reminder asks Done / Snooze 10 min / Snooze 1 hour (no answer snoozes ten minutes
once, then marks it missed); a recurring one asks Done / Snooze 10 min (no answer dismisses). A snooze is its own state,
never written into LastFired, so a daily reminder answered inside its window does not fire twice.

Verification: Remembrance's self-test 1013 PASS (861 before) through the real loader, Reminder's with the shared
self-check and the questions, both on a `QuestionsHost` double that refuses what the ABI refuses; mutation cases
`--only=meeting-end-remembrance:` in `tests/mutate-selftest-guards.py`, each applied alone, built and run once, fires.
Not done here, and in SMOKETEST.md H7b, H7c and H8d-H8g: a real recording's track lengths against the wall clock, real
Teams and Zoom calls (when each lets go of the microphone, and that mute does not), the card absent from a share, and a
run past three hours.

#### feature/agentflow-session-limit

AgentFlow 1.6.0. The owner, 2026-10-15: "a toggle to Send a pause & resume command when it detects its over 90%. So when
it hits around 90% usage and we have the reset time, it will auto tell the agent to find a good place to pause and to
set itself up to resume when the reset timer has cleared."

**Not the status line: the VS Code panel never runs one (measured).** The approved plan read usage through a Claude Code
`statusLine` command, whose JSON carries `rate_limits`, with a settings edit to install it. A temporary entry ran zero
times over about ten turns of a VS Code panel session, and extension 2.1.300 names `statusLine` only in its settings
schema; the panel's "statusLine" hits are CSS class names. The settings file was restored byte for byte. So the plan's
fallback applies, with no write to the user's Claude settings at all.

**The figures come from the panel, two ways.** The CLI sends a `rate_limit_event` when the limit's STATUS changes, and the
extension forwards it to the webview as `{type: "from-extension", message: {type: "io_message", message: <event>}}`; it
carries `utilization` (a fraction) and `resetsAt` (epoch seconds) for the session and weekly windows. A
`panel_usage_update` request carries the same windows after sign-in. Neither is stored where a later reader can find
it, and none arrived during an ordinary session at low usage, so AgentFlow leaves a listener in the panel that keeps
only the newest usage-shaped message (a few numbers; every other message is dropped past its type). Because a
listener installed after the event has nothing, AgentFlow also reads the banner the panel draws once usage is at least
70 % and the status is not plain "allowed" ("You've used 91% of your session limit · resets in 2h"), found through the
banner's own "View usage" button. Never through its words alone: a conversation can hold the same sentence, and this
feature's design session did. The banner floors its time ("in 2h" is two to three hours), so a banner reset is taken
as its LATEST value, keeping the resume after the reset.

**Working panels only, once per window, never over a prompt or a draft.** "Working" is the panel's Stop button, or
(since 1.6.1) its agent map's "Agents are working": the owner's first press of the button found nobody working while a
session plainly was, because it had handed its work to three subagents and its own chat box sat idle. An idle session
spends nothing; subagents and background tasks do, so the message asks for them to be brought to a safe point too. The message is typed into the chat box and sent with Enter, which a working panel queues for
the agent. Enter is never sent with a permission card up, because it must never be what answers a prompt, and never
over the user's own unsent text. Neither of those counts as a failed attempt, so the panel is tried again on the next
read. Whether the panel took the message is checked (an accepted message leaves the box); one it did not take is
removed, and after four failures the panel is left alone for that window. Never Codex: its panel takes no keyboard event
from this module, because Escape there is Deny. The weekly limit is said and not acted on, since its reset is days away.

**The resume is the session's own CronCreate, five minutes after the reset.** CronCreate jobs live as long as their
session, so the message asks for a resume note first; the note is what to pick up from if the session closes. Sessions
in the Claude desktop app or a terminal cannot be typed into, and the companion names how many there are instead.

**Off by default, 80-98 %, default 90 (owner).** It runs in Notify and auto-approve, never in Off, and only while VS
Code's debugging port answers. Once a minute rather than every 10 s tick, because usage moves slower than prompts
(30 s until 1.8.1; the owner chose 60 s on measured costs of 26-114 ms a read for four panels, 2-7 ms inside each, and
the listener catches usage events between reads either way).

**Untested against a live panel by the agent that built it.** Typing into another agent's chat box from an agent
session was refused by the session's own permission check, which is the right call for an agent and leaves SMOKETEST
H15b as the only proof that the panel takes a message typed this way. The self-test drives the reading, the decision,
the message and the wire against a fake CDP server, and the confirm step makes a failure visible in the log rather
than silent. The owner pressed **Pause working sessions now** on 2026-10-16 with 1.6.1: both working panels, one
delegating to three subagents, took the message (`aaba8485 (subagents) accepted, 79a4ca14 accepted`), so a message typed
this way and sent with a synthetic Enter IS taken by the panel, idle-box or mid-turn.

**The Status card reads the panels without the pause switch (1.7.0, the owner's request of 2026-10-16).** The owner
asked for "Connection to VSCode with a green check or red x", the sessions running and what is known about the session
limit, across the top like the other modules. The panel and limit rows need the panel read, so it now runs once a minute
whenever AgentFlow watches and the port answers; the TYPING still needs the switch, and a mutation case pins that a
read with the switch off types into nothing at 95 %. The ✓ needs a readable panel as well as an answering port,
because a port that answers into nothing readable is the state this module once shipped looking healthy in
(2026-09-18). One Info row per fact, because the host colours a whole Info row by its leading ✓ or ✗.

#### feature/join-record

Reminder 1.2.0 and Remembrance 2.5.0. The owner, 2026-10-16: "the tray popup that allowed me to join a meeting, can that
include a checkbox to ALSO enable recording if we have the module installed, so when I hit join, it starts the
transcription?"

**A third button, not a checkbox (owner, 2026-10-16).** The card host 1.6.0 draws takes two to four buttons and nothing
else, so a checkbox meant an app release (TrayQuestion gaining a toggle, TrayAnswer reporting it) and both modules'
floors raised to it. Offered both, the owner chose **Join & record** beside Join and Dismiss: one press does both, and it
shipped as two module updates with no app release. Dismiss stays last and stays the no-answer choice.

**Remember the last choice (owner).** Join & record goes first on the next card when it was picked the last time it was
offered. A Join on a card that did not offer recording moves nothing, since it says nothing about what the user would
have picked.

**Over the shared context, with no host change.** Modules load in separate contexts and cannot call each other, so the
handshake is two keys on PublishContext / ReadContext, in one source file both modules compile
(`shared/MeetingApps/RecordingHandshake.cs`, beside the microphone detector they already share). Remembrance publishes
"remembrance.recorder" (accepts, recording) at Init, at every start and stop and every Apply, and "" at Shutdown;
Reminder shows the button only while it says accepts and not recording, so the button never promises a recording that
cannot start. Reminder publishes "meeting.record-request" (an id, the moment, the meeting in meeting.current's shape)
BEFORE it opens the link, so recording starts while the meeting app is still opening.

**Read at use, on the idle tick.** Remembrance reads the request on its 5-second idle watch tick rather than
subscribing to ContextChanged, keeping the 2026-09-17 read-at-use decision: five seconds is inside the time Teams takes
to open. It acts once per id; never on a request older than 60 s, nor on one already in the channel when the instance
starts (a reload must not start a recording nobody just asked for), nor while recording; and its Meetings card has
**Let Reminder's Join & record start a recording**, on by default because the request only ever comes from a button
the user pressed. The log says when a request was ignored and why, and never names the meeting.

#### feature/agentflow-card-identity

AgentFlow 1.9.1, BUG-017. The owner, 2026-10-20, about a VS Code window "waiting on a permission and it has not fired
the approval": "see if there is something broken we need to fix or if its a one-off", then "go ahead and build it".

**A card is its ELEMENT as well as its text.** The repeat guard keyed on the card's text (RA-023), and an Edit card
carries none worth the name: `permissionRequest` for Edit renders "Make this edit to", the path and "?", and the diff
opens in an editor tab (webview/index.js 2.1.301). So every edit to one file signed alike, and with a queue there is
never an empty sweep to clear the counter. What does tell two requests apart is in the same bundle: the card is
rendered `F(rk0,{request:O,...},O.id)`, keyed by the request's id, so React mounts a NEW element for each request and
keeps the same one for as long as a request stands. The Claude reader therefore mints an id for each element it reads
and the press signature carries it beside the text: different if either differs, so a reused element with new text is
still a new prompt, and only the same element with the same text after three presses is the stuck card.

**Random, in a WeakMap on the page's window, minted once per element.** Never an attribute or property on the element,
which belongs to React; a WeakMap drops an entry when its card unmounts. Random rather than a counter, because a webview
that reloads starts its counter again and would hand the next card the last one's number. Eight hex digits, so two
cards in a row collide with odds of one in four billion. The id carries nothing, crosses the wire like the fingerprint
and is never logged.

**Codex keeps the text identity.** Its card is a shared component (the same one draws Codex's settings confirmation),
and whether its parent keys it per request was not established from the compiled bundle. The two ways of being wrong
are not equal: an element that stays across requests only gives today's behaviour, while one REMOUNTED under a request
that still stands would make every tick a new prompt and the repeat guard unreachable, which is the RA-049 loop. A
Codex card also carries its command, so different commands already sign apart. BACKLOG N-agentflow-card-identity-01.

**A card on its FIRST sight with a row disabled is not announced on that sight.** The same component starts with its
buttons disabled (`[O,w]=S(!0)`) and enables them from a 500 ms timer, so a sweep that lands in that window reads a
prompt nothing pressed, and on 2026-10-20 the companion announced one, 29 ms after refusing it. The next sweep reads the
same element, no longer a first sight, and announces it then if it is still unpressed. The refusal is still logged. The
rule is first sight of a DISABLED card, so a card that stays disabled (a modal over the panel, an AskUserQuestion with
no answers yet) costs one sweep, ten seconds, and a card read enabled is announced at once as before.

**Rejected.** A transcript's tool_result as the proof that a press landed: it would need each card matched to a session,
and several sessions share one panel, which is the attribution problem that kept CPU out of v1. The diff's text in the
fingerprint: the card does not hold it, and reading the diff tab would reach into the editor's content. Clearing the
counter on a 'clicked' answer: RA-049 rejected it, for the reason above.

**The cost that remains, stated.** An element remounted under a request that still stands (a webview that reloads every
few seconds, say) is a new prompt each time, so a stuck card that also remounts never trips the repeat guard and is
pressed once a sweep until it goes; the press limit in the options is the backstop for that, as for anything else. The
live probe (`agentflow_cdp_probe.py`) mints into the same map, so a card it reads first is no longer a first sight to
the module.

#### feature/agentflow-codex-limits

AgentFlow 1.10.0. The owner, 2026-10-20, having asked whether the session limit separates Claude's and Codex's: "can we
also assess session limits for codex", and "when we fire the pause command at 88%, we need to ensure its contained to
only claude sessions until when or if we can assess codex and fire the pause at each of them individually based on
limits"; then "for now, lets put the scaffolding to read codex, when or if limits are imposed it will accelerate the
full buildout, and we can do more testing on the other workstation with my personal account."

**Claude Code panels only, by construction rather than by one filter.** Until 1.10.0 the only thing between the pause and
a Codex panel was `ReadUsage` skipping Codex targets; `SendPause` typed into whatever id it was handed, and the watch and
the button each carried their own copy of the "is this panel askable" test. So the first change to read Codex panels for
their figures would have started typing into them. Now each reading carries its agent from the target list, one rule
(`SessionLimit.Pausable`) decides for both callers and ALLOWS Claude Code rather than refusing Codex, so a panel the read
could not place is not asked either, and `SendPause` reads the target list again and refuses anything not listed as a
Claude Code panel before it opens a connection ("not-claude", or "gone" for an id no longer listed). One extra request
per send; sends are rare.

**Codex's figures come from its rollouts, not its panel.** Every `token_count` event carries `rate_limits`
(`limit_id`, `plan_type`, `primary`, `secondary`, `credits`, `rate_limit_reached_type`, ...), and the transcript reader
already tails those files, so the fold keeps the newest one per session (last writer wins, as the mode does; a null
`rate_limits` leaves the last reading standing). That makes the figure per SESSION and per account, which is the shape
"fire the pause at each of them individually" needs, and it needs no debugging port. Only the limit fields are read;
the token counts beside them are not.

**What there was to read, measured over this box's 34 October rollouts.** The Business workspace (22 sessions in VS
Code, 2 in the desktop app) sends `primary` and `secondary` as null and `has_credits` with no balance: no window, so no
figure to hold 88 % against, and the only signal is `rate_limit_reached_type`, which arrives once a limit is already
hit. The personal Pro account in the desktop app (10 sessions, 2026-10-02 to 2026-10-19) sends ONE 10080-minute window,
in the `primary` slot (57 % on 2026-10-02, 1 % on 2026-10-20). No rollout carried a 300-minute window. Hence two rules:
a window is filed by its LENGTH, never its slot, or the Pro account's weekly window would be read as its session
window; and `used_percent` is already a percentage (1.0 is 1 %), so Claude's fraction reader, which takes anything up
to 1.5 as a fraction, is never used on it.

**Shown, never acted on.** The Status card gains a **Codex usage** row: each app and plan's newest figure ("2 sessions
in Codex in VS Code on Business: no usage window reported; 1 session in Codex desktop on Pro: weekly 1 % · resets
2026-10-27 09:14"), sessions that have not reported yet, and "Read only: AgentFlow pauses Claude Code sessions only."
`CodexLimits.OverThreshold` is the full buildout's decision, a session's OWN five-hour window past the threshold with
its reset known, written and tested now; with pause & resume on, the row says when a session is past it and that it was
not paused. Nothing calls it to send.

**What the full buildout still needs, stated so it is not mistaken for done.** (1) An account that sends a five-hour
window; none here does. (2) A way to put the request into a Codex session: the Codex panel takes no keyboard event from
this module because Escape there is Deny, and the desktop app, where the Pro account's sessions run, is not reachable
at all. (3) A resume: the Claude message asks for CronCreate, a Claude Code tool, and a Codex session needs its own
mechanism. BACKLOG N-agentflow-codex-pause-01.

#### feature/agentflow-enhancements

AgentFlow 1.11.0. The owner, 2026-10-21: "I would like to tackle that backlog and add "multiple" approvals per "sweep". So
we should increase our duplicate detection to say 5 (make sense?), and I would like a way to increase the frequency of
the check so a box for input that can allow say, 1 to 30 seconds per check, and default at 10."

**The repeat guard counts per panel and card, and that came first (BUG-018).** It was one counter for the module, keyed
on the last signature pressed, which holds only while one prompt is on screen at a time. With a stuck card in one panel
and a working prompt in another the signatures alternated: after three presses the stuck card was refused, the sweep went
on and pressed the other panel, that signature reset the counter, and the next sweep pressed the stuck card again as new,
three ticks in every four for as long as it stood. Pressing every panel in one sweep would have made that every tick, so the count
moved first. Each (CDP target id, signature) keeps its own count. `PressBudget.EndSweep` drops a count when the sweep READ
its panel and did not see its card, or when its panel is no longer listed; a panel that was listed and not read (an attach
that failed, a page that answered nothing, a card this build cannot read) keeps its counts, because not seeing a card is
not seeing it gone. `SweepPass.Handle` marks every card it reads as seen, pressed or not, so a stood-down card that reads
disabled for one sweep keeps its count. A stand-down now holds one card, and the other panels go on being pressed.
`NotePromptCleared`, which cleared everything when a whole sweep found nothing, stays for the assertions only.

**Every panel in one sweep, one press per panel.** RA-047 kept a press as the end of the pass on the ground that one click
per tick was the cadence the budget and the log were built around. With the count per card that ground is gone, so the
sweep reads every panel and presses the waiting prompt in each: three panels waiting are three presses on one sweep. One
per PANEL still, because each target is read once a sweep and the next card in a panel's queue mounts with its buttons
disabled for 500 ms; a shorter interval, not a second press, is what drains a queue faster. Each press is logged on its
own line (F029: a press is an action taken on the user's behalf, and each one is written down); the sweep's answer joins
them for its other callers, and a standing refusal still reaches the log only on a sweep that pressed nothing.

**Five presses, not three (the owner's number).** The guard counts presses, not seconds, so at a one-second interval three
presses were three seconds of a card that may only have been slow to leave after a click it took. Two more clicks on the
approve-once row of a card that really is stuck are the price. Considered and not built: a guard measured in seconds of
one card, which would mean the same at every interval but costs a stuck card more presses at a short one.

**The interval: 1 to 30 seconds, default 10, and it paces the panels, not the transcripts.** It sets the timer, so it is
how soon a prompt on screen is pressed or announced, in every mode that looks at the panels (auto-approve, Notify, Log;
the box is greyed in Off). Apply hands it to the running timer only when it changed, since setting a WinForms timer's
Interval restarts its countdown. The transcripts stay on a ten-second beat (`TranscriptEvery`, less 500 ms of slack so a
ten-second timer that fires a little early still reads on every tick): the notice they feed waits 30 s by default, and the
read sweeps both agents' folders, about 53 ms a tick in the .NET measurement of 2026-09-21. Explained to the owner before
it was built. The session-limit read keeps its own 60 s clock, and a tick between transcript reads hands it the last
read's detections, so its notice still counts the sessions outside VS Code. At a short interval a sweep can outlast the
tick; the scan guard skips the tick that would overlap, so the real floor is a sweep's own length. The read clamps to
1-30 before anything reaches the timer, which refuses an interval of 0.

**Codex cards are told apart by their element (N-agentflow-card-identity-01).** Read live on 2026-10-21 in the owner's
VS Code (openai.chatgpt 26.1007.21434, two `curl` approvals in an `on-request` chat), with the probe minting into the
card container and its form: the first card kept one card id and one form id over three reads while its request stood,
and after Allow once the second request was a new element with new ids, which it kept: the Claude shape. So the Codex
reader mints into the same WeakMap as the Claude reader, keyed on the card container, and Codex reads carry the
first-sight flag too (a Codex card read disabled on first sight now waits one sweep; both live first sights read enabled).
The limit, stated: one pair of cards, read seconds to about a minute apart, so a card remounted under a request that still
stands (the RA-049 loop) is ruled out only for that span; the press limit is the backstop, as it is for Claude.

**Three mutation cases had been guarding nothing.** A pattern count of every case in `tests/mutate-agentflow.py` against
its file, which takes a second, found three that matched nothing on master: "burn: the pass drops an unpressed prompt from
the announcement" and "burn: RA-038 the Codex reader abandons a prompt without a dropdown, respelled", since 1.9.1 put a
comment and the Settling test on their lines, and "session-limit: an idle panel is asked too", since 1.10.0 moved its test
into `SessionLimit.Pausable`. A full run prints such a case as NO-OP among the rest, and a `--only` run never reaches it,
which is how a lane runs. The first and third were re-pointed at their lines as they read; the second matches again
because 1.11.0 removed the comment between its two lines. The harness can now be asked for that count on its own
(`--check-patterns`), which fails on master's three.

**Verified in the real host** with `docs/agentflow/agentflow_card_queue_check.py`, two new two-panel scenarios included:
22 of 22 on the lane build, 23 of 23 at a two-second interval (presses a median 2.0 s apart), and the published 1.10.0
held to its own behaviour, 18 of 18, which is the run that shows the check can fail (two stuck panels pressed `[2, 8]`
times past a cap of three). The figures are under BUG-018 in [`ISSUES-post-1.0.0.md`](ISSUES-post-1.0.0.md).

**Not covered, stated:** nothing drives OnTick's use of the transcript beat, BACKLOG N-agentflow-enhancements-01; and two
real Claude Code panels with prompts, SMOKETEST H15f.

#### agentflow/codex-card-queue

AgentFlow 1.12.0. The owner, 2026-10-21, on 1.11.0: "It is still not clearing multiple Codex requests in a single pass."

**Measured before anything was built.** The box ran 1.11.0 (`module loaded: agentflow 1.11.0`), Codex in VS Code over the
debugging port, an approval mode that asks. Its log held 184 Codex presses since load and never two in one second, with
129 of the gaps between them exactly the five-second interval: a press on every sweep, one a sweep. That fits a queue in
one panel or a press missing in another, so the panel was counted: `docs/agentflow/agentflow_codex_card_count.py`, new
here, reads every Codex webview every 250 ms and reports per card a form, buttons, enabled buttons, visibility and ids,
never text, and presses nothing. It keeps its own WeakMap so it never makes a card look seen to the module. Three Codex
targets, one with prompts, and it showed BOTH shapes in that one panel:

- QUEUED. One card at a time, a new element per request, the next card in the DOM 26 to 240 ms after AgentFlow's press
  (the press's log stamp against the probe's 250 ms samples), its buttons enabled on the first sample. One read per panel
  per sweep therefore cleared a queue of N in N sweeps.
- STACKED. Two cards at once, both enabled and visible. The reader took `querySelector`'s first match, the first slot
  refilled after every press, and the second card stood 74 s through fifteen sweeps, pressed only on the one sweep that
  found the first slot empty. A 25-minute watch after it counted 176 cards, none without a form, and ten such second
  cards, the longest on screen from 17:21:57 to past 17:24:57, about three minutes behind a first slot pressed every five
  seconds. Both cards carry `data-codex-approval-surface` under
  the same `data-codex-composer-root`, so the second is another approval surface in the composer, not another kind of
  prompt. Whose request it is stays open: BACKLOG N-agentflow-codex-queue-01.

The bundle agrees about the queue: openai.chatgpt 26.1007.21434's `sLt` returns ONE pending request per thread, and
`vLt` the first exec or patch approval of a turn, so Codex itself holds the rest back until the shown one is answered.

**Every card, each by its element.** The Codex reader walks every `@container/approval-card` in the document and answers
`{ cards: [...], blind: N }`, each entry minted and shaped as the single card was, N the cards it could not read; 'blind'
still means none could be read and 'none' that there are none. `CdpApprover.ParseCards` takes that or the one-card object
every other reply is, and the sweep hands each card to the pass in document order. The click carries the card's id
(`__CARD__`) and finds its card by looking each one up in the reader's WeakMap, because with several cards on screen the
first is no longer necessarily the one decided on: two cards both offering "Allow once" pass the label re-check on
either, and the other may carry a row the classifier would refuse. With no id (a mint that threw) it takes a card only
when exactly one is on screen. Read and click still count the same buttons (RA-038's no-return rule, now also no
`continue` or `break`, since the read is a loop).

**The queue behind a press, followed inside the sweep.** After a press in a Codex panel, `CdpApprover.FollowQueue` reads
the same panel again every 100 ms and hands on any card whose element id it has not handed on this sweep. That rule is
the guard the owner named: the same card is never pressed twice in one sweep, so a card only slow to unmount is waited
out, and the repeat guard still counts one press of a stuck card per sweep, five sweeps to the stand-down, never five
inside one. A card with no id is never new, since nothing could tell it from itself on the next read, and a press of
one is not followed. A new card with a disabled row waits for the next read; one still disabled when the window closes
is left for the next sweep, which announces it then, the cadence BUG-017 set, since the follow's read was its first
sight. Bounds: 800 ms after the last press with no new card (three times the slowest mount measured), 3 s a sweep from
its start, across every panel (and never more than twice the sweep timeout, so the follow ends well inside the sweep
connection's six-second life), and 10 presses a panel a sweep. A round that hands on new cards and presses none ends it:
a refusal is not a queue moving. Every press still goes through `SweepPass.Handle`, so it re-checks the switch
(`StillArmed`), counts against the approval limit, and is logged on its own line. The note is unchanged and carries no
command text.

**Claude Code panels are still read once a sweep.** Their next card enables 500 ms after it mounts and has not been
measured queued, so the follow is Codex's alone (`FollowsQueue`); BACKLOG N-agentflow-claude-queue-01 says what would
turn it on.

**Considered and not built.** A shorter default interval, which drains a queue faster without reading it twice but
presses no stacked second card at all, and is the owner's setting to choose. The handoff's interim answer, a check
interval of 1 or 2 seconds, still works and still costs a queue of N about N intervals; 1.12.0 is about one.

**Verified.** The module self-test carries 26 new checks over `FakeCdpServer`, which gained `AfterClicks` (what the page
becomes after each press, on a timer if asked, so a mount gap, a slow unmount and a settling card can be scripted) and
`ClickedCards` (which card each Codex click named). In the real host, `docs/agentflow/agentflow_card_queue_check.py`
gained a copy of the Codex card and two scenarios: 34 of 34 on the lane build, six queued Codex cards cleared 1.1 s
from first press to last and the standing card of a stack pressed 0.0 s after the first; and the published 1.11.0 held
to its own behaviour, 12 of 12, the run that shows the check can fail: the same six took 49.85 s, one a sweep, and the
standing card waited 30.0 s for the first slot to empty. `tests/mutate-agentflow.py --only="(1.12.0)"`: 17 of 17 fire.
Two of them SURVIVED the first run and each showed a hole: "the sweep hands on a panel's first card only" lived because
the follow found a pressed first card's neighbour anyway, so the check that kills it is a stack whose first card is
REFUSED (which 1.11.0 starved for as long as it stood); and "the follow has no cap of its own" lived because two cap
checks covered each other, so the follow has one per case now, at entry and where a press reaches the cap. Six older
cases whose lines this moved were re-pointed at them with the same break, and all six fire; `--check-patterns` reads
292 cases, every one matching once.

**Not covered, stated:** the live panel, SMOKETEST H15g; whose request a second stacked card is, BACKLOG
N-agentflow-codex-queue-01; a Claude Code panel's queue, BACKLOG N-agentflow-claude-queue-01.

#### feature/remembrance-time-first-names

Remembrance 2.6.0. The owner, 2026-10-20: "the naming convention of the filenames seems to be meetingname, date, time.
Can we change that so its in time order and use military time", giving "<meeting> - 2026-10-20
14-08-25.summary.txt" becoming "14:08 10-20-2026 <meeting>.summary.txt" (a real title in the request), "this way when I open the folder, the
meetings are in the order I had them in". Offered the points below, the owner chose: the date-first form, no seconds,
and rename the existing recordings.

**"2026-10-20 14-08 Design Review", not the example as written.** A colon cannot appear in a Windows file name, so
the time is "14-08". Time first sorts by the meeting's start only within one day: in the per-capture layout the folders
of every day share the root, and there "14-08 ..." would list every day's 09:00 before any day's 10:00; month-day-year
does not sort across months either. Date first then a 24-hour time sorts the same inside a day folder and correctly
everywhere else. The base is the stamp alone when there is no meeting name.

**To the minute, with " (2)" (owner).** Seconds made a clash all but impossible, so NewCapture never checked; a per-capture
folder that already existed was simply reused. Now FreeBaseName takes the first free of the name, " (2)" .. " (99)": per
capture against the folder, by date against every file the capture would write under that name. Past (99) the start
fails with a message, rather than overwrite. Snapshots keep their own stamp, to the second.

**The purge reads the new names only where this module writes them.** The time-first shape is looser than the old one
("&lt;date&gt; &lt;time&gt; &lt;anything&gt;" against "&lt;anything&gt; - &lt;date&gt; &lt;time-with-seconds&gt;"), and the purge deletes
by name in a folder the user chose. So it is ours in exactly two places: as a capture folder's name in the root, and as
a file in a day folder whose date is the day at the front of the name. Loose in the root it is never ours, because
nothing has been written flat there since 2.0.0. Every old shape is still read where it was.

**The rename is a button, not a migration (proposed to the owner, who did not object).** The module is published, and an
update that renamed every user's recordings at its first start would act on files nobody asked it to touch. The Storage
card's **Older recordings** line counts what would move (names only, on pane open), and **Rename older recordings to
the new names** moves it. It renames per-capture folders whole (their files are "recording.wav" and "snap ...", which
carry no title) and, in day folders, every file of one capture together. Oldest first, so of two captures of one
meeting in one minute the earlier keeps the plain name. A move that fails while its source is still there and its
target still free is tried five times, 200 ms apart: in the first mutation run's baseline on 2026-10-20 one folder made a
moment before failed to move and moved on every run after, the shape a scanner or the indexer briefly holding a file
inside it gives. A capture one of whose files still fails is put back, newest move first; File.Move and Directory.Move
refuse an existing target, and that refusal is never retried, so nothing is overwritten even by a file that appeared
after the plan. It refuses while recording, while a stop's save, transcription or summary is in flight (a new counter,
_stopsInFlight, from the stop's Task.Run to its finally), during any model work, and while a snapshot is saving, and
when the storage folder on screen has not been applied. The purge and the rename share a lock. One log line of counts
and exception types; the names carry meeting titles.

**Left alone, and said.** Files loose in the root from before 2.0.0 (renamed there, the purge would never read them
again), a capture in a day folder for another day (moved there by hand; renamed, the date rule would keep the purge off
it), and anything not in a shape this module writes.

**One button in Storage, which R2 had kept empty.** The layout lane's check said "Storage and Transcription hold no
button at all", to keep the three removed Browse buttons out. The rename chooses no path, so the check now allows it
and no other, rather than a new card changing the approved card order.

**Measured on the owner's box, names only:** the storage folder (inside OneDrive) held 2 day folders with 3 recordings
under the old names, 8 files (3 transcripts, 3 summaries, 2 recordings' audio; the third's audio already purged).

#### feature/host-catalog

Host, 2026-10-26: the four catalog and download items lane feature/catalog-insight filed (N-catalog-insight-01 to
-04), in `src/dotNet/SecureDownload.cs` and `RemoteCatalogClient.DownloadVerifiedAsync`, checked by
`--security-selftest`'s CheckSecureDownloadReliability through fake handlers and streams.

**One retry, of a request that got no response, and nothing else is retried.** A failure with no HTTP status (DNS, TLS,
a reset connection) is sent once more, as a fresh copy of the GET, after 500 ms and inside the same deadline. A
response with any status is the server's answer and is never retried: the panes already tell a 429 or a 5xx to try
again in a few minutes. The retry wraps the send alone, not the whole download, because the refused 3xx throws an
HttpRequestException that carries no status and a wider retry would resend it; and nothing after the response began
is retried, so a body is never read twice. Once, not more: the panes refetch on open and the weekly checks retry later.

**The catalog keeps its one 60-second deadline; an asset gets a stall timeout and a cap sized by its length.** Every
download through DownloadVerifiedAsync (modules, and pets and packs with them, since they share that door) fails after
30 seconds with no data, and otherwise may run for its declared Content-Length at 256 KiB/s, never under 60 seconds.
The length is the response's, not the catalog's "bytes", because it is what is actually being sent, the previous
file included in the minutes after a publish. With no Content-Length the cap is the one for the caller's maximum (400
seconds for a module). fortunes.zip's 30,481,345 bytes get 117 seconds, so it now installs on a link down to about 2.1
Mbit/s instead of 4.1; slower than that it still fails, now in words of its own ("The download took longer than the
117 seconds allowed for it and was stopped."), and the rate is the one constant to move if that floor proves too
high. The rate is the brief's example, not a measurement of any link.

**A hash mismatch is refused exactly as before; only its reason changed.** The reason now says a catalog published in
the last five minutes can be served out of step with its files and to try again shortly (raw.githubusercontent.com's
max-age=300, measured by lane feature/catalog-insight). Refused: commit-pinned asset URLs, which close the window only
while the stamped commit survives the merge, and a squash or rebase would break every asset at once.

**Proxy credentials go to the proxy only.** The handler sets `DefaultProxyCredentials` to the user's default
credentials so an authenticating proxy's 407 is answered; `UseDefaultCredentials`, which would offer them to the server
itself, stays false. No authenticating proxy was reachable, so the setting is asserted and the exchange is unverified.

**Not guarded at run time: which limits each production call passes.** The catalog fetch reaches CatalogLimits
through the three-argument `DownloadBytesAsync`, and DownloadVerifiedAsync passes AssetLimits; both build the real
handler, so no offline check can see the choice. A source invariant would pin it.

#### feature/host-runtime

Host-only follow-ups of the module-update, stand-down and fullscreen lanes (campaign of 2026-10-26). Four choices a
later reader could take for defects:

**The default speaker prefers a companion in view over the chosen type** (N-fullscreen-per-monitor-01). With the chosen
type pinned behind a game and another companion in view, the other one says the SayAll line. The choice names a voice,
and a line held until the game ends, while a companion the user can see says nothing, is not that voice speaking. With
none in view the choice is exactly the old one, and its line is held as before. The random drop's subject follows the
same rule (`StartUp.InViewOrAll`); the in-view test is `StartUp.IsInView`, the one `AnyCompanionInView` asks.

**An update marker that cannot be read fails the mark** (N-modules-update-all-03). `MarkForUpdate` reads it again up to
three times, 50 ms apart, and then throws with the file untouched; the pane says that module could not be updated and
discards its staging folder, and every id already marked still goes at the next start. Refused: writing the one id
anyway (the defect: the earlier ids are lost), and waiting longer (the pane calls this on the UI thread).

**ContextChanged from a pool thread lands later, on the UI thread** (N-aibrain-standdown-01). A publish off the UI thread
is posted, so the handlers run after `PublishContext` has returned to that publisher; `ReadContext` answers the new value
at once. A publish on the UI thread is raised inline, as before.

**Only the click-through pair passes the fullscreen scan** (N-fullscreen-per-monitor-02). A window that is both
WS_EX_LAYERED and WS_EX_TRANSPARENT never decides a monitor; layered alone (every per-pixel-alpha window, the companions'
own among them) and transparent alone still decide as they always have.

#### feature/host-options

Host only (`src/Portable/Wpf/OptionsWindow.cs`, `CompanionsPaneControl.cs`), asserted in `--wpf-options-selftest`; no ABI
change. The options-window backlog items of campaign 2026-10-26.

**A LoadPending pane's stored baseline is a read of its own, asked FIRST.** Whenever a build is handed anything (a
ReloadOnChange cascade, a ReloadPaneAfter rebuild) the host asks `LoadPending` with an empty dictionary for the stored
answer, which the contract already defines as "answer from what is stored", and then asks with the screen. The order is
the point: AgentFlow builds its schema inside `LoadPending`, and the host reads `Schema` after the last call, so the stored
read second would rebuild the second dropdown for the stored pet. A ReloadPaneAfter rebuild of such a pane asks the
module with the user's edits the action did NOT write (`PaneView.EditsToCarry`, the merge's own decision), never the whole
screen: handed the pet it replaced, a "reset to defaults" would be shown the value it had just undone. The cost is one more
`LoadPending` call per cascade and per action rebuild; a first open still makes one.

**Apply is asked of the view on screen, not lit by the signal.** `NotifyDirty` now means "an edit happened"; the window
sets Apply to `ShellPane.HasEditsToApply`, which a schema pane answers from the view it holds (an unsaved edit against
Load's own answer, a staged tick, a key Load left out against what its editor first showed). feature/pane-rebuild refused
exactly this because a LoadPending pane's rebuilt view could not tell an edit from its stored answer; the separate stored
read above removed that reason. Two consequences of the old signal that read as defects are gone with it: Apply lit over a
pane whose only edit an action had just saved, and Apply lit after a radio flipped away and back (N-cli-model-effort-06).
A secret's box counts whenever it holds text, whatever presence hint Load answered, because the box is write-only; and a
Bool is compared by meaning, so a module answering "True" is not read as edited.

**Info rows show a hyphen inside a word as U+2011, for display only.** A WPF TextBlock offers no selection and no copy,
so the substituted character can never reach a clipboard; the module's text is unchanged in `_loaded` (the EnabledWhen
fallback) and is what UI Automation reads. Refused: the modules writing U+2011, which a user would then copy out of the
module's own messages into a CLI that does not take it; and "wrap at spaces only", which a TextBlock cannot be told except
by changing the characters it may break at, which is this. Measured in process: at the width where the ordinary sentence
splits "gpt-6.1-" / "sol", the displayed one keeps the id whole and wraps at the space before it; Segoe UI carries the glyph.

**The Settings window's UI Automation gap was unnamed controls, not missing peers.** Walked in process the way
UIElementAutomationPeer walks it, the window already exposed its nav entries as named ListItems and its labels as Text;
what it did not do was name a single editor: every CheckBox, Edit, ComboBox and PasswordBox had an empty name and no
LabeledBy, the path buttons were named "…" and "✕", the collapsible and list-group Expanders, the whole-group box, the
filter box and the Companions size slider were nameless, and a card was no element at all (a Border has no peer). Each
editor is now named by its row's label and labelled by it, its hint as help text; each card is a named Group
(`SettingsCard`). The earlier out-of-process run that saw 0 ListItems and 0 Text had opened Settings with a UI Automation
Invoke of the tray's Options item, which runs the modal window inside that Invoke call; that is the likelier reason it saw
nothing, and it is for the outside check to confirm, by opening the window another way.

**A blank render names itself.** While the session is disconnected, and it appears while its Remote Desktop window is
minimized, `RenderTargetBitmap` hands back a bitmap in which not one pixel shows the frame's own background; every pixel
check then fails alike. The checks still FAIL (nothing was judged), and their line now ends "[render blank: is the Windows
session disconnected, or its Remote Desktop window minimized?]".

#### feature/modules-small

Campaign 2026-10-26, lane modules-small: backlog items in Fortunes, Companion Studio, AgentFlow, Reminder and Blinking
LED. Module-only; no MinHostVersion moves.

**Blinking LED: a pane Apply writes only what the user changed on that pane (N-blinkingled-resume-03; the owner's
decision of 2026-10-26: module-side).** `SavePaneValues` compares each submitted value with what that pane shows, which
is what it last loaded or, after an Apply whose write landed, what that Apply submitted over it, and writes only the
values that differ. A tray choice made while the options window is open therefore stands: the final review's probe S9
(a tray speed pick over a Caps Lock off, then an Apply that changed only "announce") no longer saves a plain off, and a
tray Off or a tray speed pick is not put back by the window's stale on/off or speed. This reverses
feature/blinkingled-resume's refusal of "comparing an Apply with what the pane last loaded instead of the disk", whose
reason was drift between a second copy and the disk. The copy kept is the pane's own view, and an Apply is a change to
that view, so the disk still wins for every field the user did not touch there. A failed write leaves the view where it
was, so the same Apply sent again still writes; a pane no host has loaded (a direct Save) is compared with the disk, as
before. Refused, the other half of the owner question: the host re-reading every module pane after Apply or after a tray
change. That is a host change reaching every module, for a hazard one module's schema has, and the module ABI does not
state when a host re-reads a pane.

**Companion Studio's extraction trees are in scope of the delete rule (N-remembrance-delete-logging-01, -02).** The rule
of 2026-10-07 exempts temp as written, and the tree is `%TEMP%\petstudio-shimeji-<guid>`; the campaign took it in scope
because the tree is a copy of the user's own skin. Each delete of a window's tree writes one line ("deleted this window's
extracted skin folder", or the failure's exception type and that the sweep will collect it); each orphan sweep that
removed or failed anything writes one line with the counts and types, a folder it cannot list included, and a sweep with
nothing due writes nothing, as Remembrance's purge does. The sweep reaches the log through a static host, the newest
window's, rather than a host argument: every window has the module's one host, and the F159/F160 source invariants pin
`BeginOrphanSweep()` and its call of the sweep as they are. Not driven by the module self-test, which builds no window:
the constructor's assignment of that host, the parameterless sweep that reads it, and `DeleteTree`'s one call of
`DeleteExtractionTree(path, _host)`. The delete, the sweep and their lines are driven over fixture folders.

**Fortunes' backup line has no path from the pane today (N-remembrance-delete-logging-04).** The pane's import approves no
overwrite, so it never makes a backup; the importer counts the two backup deletes for whatever approves one, and the line
is driven through `LogImportDeletes` with the importer's counts handed in. The backup `RestoreTornReplace` deletes after
a refused replace can be the zero-byte reservation the commit made before the replace failed; it is counted as a backup
all the same, because it is the file reserved for the pack's earlier version and the rule counts deletes, not bytes. The
staging files and the lock file stay silent, as temp. The rollback line (-03) has a pane path, and the module self-test
drives it: a folder holding the second pack's name makes the commit fail after the first pack landed.

**AgentFlow's inline tick is a seam, not a mode (N-agentflow-enhancements-01).** `RunTickInlineForSelfTest` and
`TickClockForSelfTest` are set by `SelfCheckTickBeat` alone; everywhere else the worker runs on the pool and the beat
reads `Environment.TickCount64`, as before.

**Reminder: the Do Not Disturb half is unverified (N-tray-questions-03).** `QUNS_QUIET_TIME` is Windows' quiet period
after setup or a feature update, and the comments now say so. The pane's label "Stay quiet while presenting or in Do Not
Disturb" is left as it is until the live check is read (Do Not Disturb on, a test question from the debug window, the
host's `[question] held` line), which is a GUI action this lane did not take.

#### feature/test-infra

Test infrastructure and records (campaign of 2026-10-26). No module, host or published payload changes, so no version
moves. Each paragraph is a choice that could read as a defect, or as work left half done.

**The module-window soak judges private bytes by their slope, not by one step (N-release-150-01).** It judged the last
segment against the one before under 24 MiB, and that sat inside its own noise: the v1.5.0 cut failed once at +38.7 MB
on a Companion Studio byte-identical to 1.4.0's, and four 4-segment reruns on both builds swung by up to 37 MB a segment
without ever growing every segment. That is a GC watermark, a level that swings; a leak adds about the same amount every
segment. `PrivateBytesJudge` in `tests/DesktopAICompanion.WindowSoak/Program.cs` fits a least-squares line through the
sample taken after each segment and bounds its slope at 16 MiB a segment, needing at least four segments. The bound
comes from the recorded numbers, worked through in the class's comment: a swing of the recorded size moves the slope by
at most 15.5 MB a segment over four segments and 11.6 over five, while the recorded every-window-rooted leak (+31.4 MB a
segment) still judges at least 15.9 and 19.8. Four segments is the floor, where the two worst cases nearly meet, and
five is the default. Refused: the median of the steps (a level alternating +37, -37, +37 has a median of +37, a watermark
judged as a leak), also requiring growth in every segment (a real leak under one -37 MB swing would pass), and the median
of several runs (one run is one process, and nothing re-runs it). Handles, GDI and USER are counts that do not swing and
are judged as before. The judge runs on its own as `--judge-selftest` (no window, no module), which
`tests/mutate-selftest-guards.py` grades as WINDOWSOAK-JUDGE, and every soak runs it before its first window. **Until
`tests/module-window-soak.ps1` changes (outside this lane), its default `-Segments 2` makes the soak FAIL with "NOT
JUDGED, a slope needs at least 4 segments and 2 ran"**: run it with `-Segments 5`.

**Fortunes has its own module self-test limit of 300 s (N-release-150-02).** Its self-test took 187 s and 174 s under load
on 2026-10-07 against `tests/Test-ModuleSelfTests.ps1`'s shared 180 s, and 32.4 to 34.5 s in three fresh runs on
2026-10-26 on a busy box. 300 s is 1.6 times the slowest recorded run. The other modules keep 180 s although agentflow,
aibrain and remembrance measured 37 to 43 s in the same day's run, because only Fortunes has been recorded near the limit;
every OK line now prints the module's wall time against its limit, so the next one to approach it is visible first. A
TIMEOUT names the module, its limit and whose limit that was. A run that fails keeps `environment.txt` beside its markers.

**The template cleanup names what holds a folder it cannot delete, through an open-handle query rather than the Restart
Manager alone (N-template-cleanup-01).** The release failures were an EMPTY scratch folder refusing deletion, and the
holder had gone by the time anyone looked. Measured on this box: the Restart Manager named a file's holder and refused a
folder with error 5, while `NtQueryInformationFile` class 47 named a process whose current directory was the empty
folder, which reproduces the refusal exactly. So `packaging/Test-ModuleTemplate.ps1` asks the open-handle query about
every folder and file left and keeps the Restart Manager over the files as a second opinion. Its fixtures, a file this
process holds and a folder a `ping.exe` child sits in, now run on every template check, because nothing had run
`-CleanupSelfTest` since it was written. This is instrumentation: the earlier lock's cause is still unknown, and the next
occurrence will carry it.

**The Fortunes import probe did not fail again (N-fortunes-import-probe-01, DECLINED-MEASURED).** Ten fresh
`--fortunes-engine-selftest` processes, each with its own short TEMP and data root and real input withheld, all passed
with `custom_import=PASS` (30.3 to 69.9 s), and so did the three `--module-selftest=fortunes` runs that contain the same
probe: 0 of 10 plus the 3 earlier. The assertion is unchanged. If it recurs under `tests/Test-ModuleSelfTests.ps1`, the
kept run now holds the children's TEMP, its length and whether it is an 8.3 alias, the data root, the host's build,
every module's time and what the children left. Not kept, because keeping it is a Fortunes change:
`FortuneFileImporter.RunSelfTest` deletes its own scratch even on a failure, and `TestPortableReplacementFallback`
reports its five conditions as one line.

**A `\\.\pipe\LOCAL\cc-msg-<id>` pipe in a trace of a companion call is Claude Code's own (N-cli-home-files-04,
ACCEPTED-RECORDED).** Each Claude Code call AI Brain or Remembrance makes through `shared/CodingAgentCli` starts a
Claude Code process, and that process opens a messaging pipe per session and names it in its stream's init line as
`messaging_socket_path` (lane feature/cli-home-files' census, 2026-10-10, Claude Code 2.1.293). The companion neither
creates, reads nor passes it: `ReadClaudeStream` skips the init line whole. The census saw it local to the session and
gone with the process. It is accepted as the CLI's behaviour, with no lever sought, so a reader who finds one in a handle
or process trace knows it belongs to that call's Claude Code child, one per call.

#### feature/agentbroker-bus

AgentBroker agent-to-agent bus: durable generic mailboxes, blocking waits, a field-compared `send`, and MCP
server instructions. The design constraints were fixed in advance: no new property on any federation record
(a 1.0.4 peer deserializes with unmapped members disallowed and does not catch the failure), no change to the
persisted state schema (rollback must still read it), and no wait while holding the store lock.

**A mailbox whose agent is Away still accepts messages.** That is the feature, not a lapsed-lease leak. A
generic MCP session stays addressable for 24 hours after its owner's last call; the presence lease still lapses
after a minute, and only presence is ever shown to a peer (`Directory(forPeer: true)` and
`/federation/directory` filter on a live lease exactly as before). Native sessions are never retained: a lapsed
native lease means the vendor session is gone.

**Retention is process-local, on purpose.** Persisting registrations would change the `State` record that a
rolled-back 1.0.4 worker must still read. A restart therefore drops every mailbox, messages persist, and the same
`clientId` and incarnation find them again. Refused alternative: deriving retention from `LeaseUntil`, which a
direct store registration can set up to two minutes ahead; a separate last-activity time is exact.

**`wait_for_reply` returns at most 16 replies** although several are legal. Sixteen worst-case escaped receipts
are what the pagination checks proved fit the stdio bridge's 8 MiB response bound; more are read with
`read_inbox`.

**A wait on an unreachable mailbox is refused (`session_unavailable`) instead of returning empty.** After a
restart every wait would otherwise return empty until the agent gave up, while senders already get
`recipient_unavailable`. Waits are local operations, so a paired broker cannot occupy the sixteen slots.

**`send` caps a lifetime above 24 hours instead of refusing it, and refuses a negative one.** The receipt shows
the applied `ExpiresAt`. A retry ignores `ttlMinutes`, because the stored message is the one being recovered.

**A privacy or pause choice on a retained mailbox applies in place.** Registering needs a live lease, and the
owner's choice is not the agent's activity, so presence and retention are left as they were.

**The installer reads the package version from the module's IL.** The post-install probe pins the exact MCP
tool set of the package being installed, so rolling back to a 1.0.4 package through the current script expects
seven tools and a 1.1.0 package expects ten. The module DLL's FileVersion is 1.0.0.0 and the ZIP carries no
manifest, so the only version the package declares is the `ldstr` that feeds `ModuleInfo.set_Version`. It is read
with System.Reflection.Metadata, nothing is loaded, and anything other than exactly one such literal is refused.

#### feature/remembrance-backlog

Remembrance's six backlog items of the 2026-10-26 campaign (N-cli-model-effort-07, -11 and -15 for Remembrance's half,
N-layout-remembrance-02, N-remembrance-delete-logging-06, N-meeting-end-02). Version and floor are the coordinator's:
the live refresh needs MinHostVersion 1.6.1, for `OptionsPane.NotifyInfoChanged`.

**An open pane is told when a summary or a Validate has finished, and Load's bookkeeping survives that (N-cli-model-effort-07).**
The module calls `NotifyInfoChanged` after a recording's transcription, once the whole stop is over, at the end of
every "Try it on a file" press and after the CLI and cloud Validates. The host answers by calling Load again for the
Info rows alone, which Remembrance's Load was never written for: it cleared BUG-013's record of background writes and
took what it showed as the baseline Save compares with. A refresh between the pull's selection and an Apply would have
let the untouched screen write the old model back over the download. Load now keeps a background write's record, with
the baseline from before it, when it shows that field otherwise than the baseline did, and a successful Apply settles
every record. Refused: telling the refresh from a build by a flag set before each notification. The host coalesces
notifications, drops a queued refresh on a rebuild and ignores one while the pane is closed, so a flag would outlive
the refresh it was meant for and mislabel the next build. The cost of the rule kept: after a REBUILD that followed a
background write, a user who picks the old value again before any Apply has that one Apply keep the background write,
and the pane it rebuilds shows so.

**Apply stores a field only when it changed, except the summary model (N-cli-model-effort-11).** The feature/remembrance-2
entry refused writing only the fields that differ from Load, because the summary dropdown preselects a model when none
is saved and the stop path reads only the saved one. That reason holds for that one field and no other: every other
field's Load value is what its readers use with nothing saved. So the diff applies to every field but `summaryModel`
(`StoredEvenWhenUntouched`), an untouched Apply on a fresh install adds only `summaryModel`, and the WITNESS that keeps
the refused rule out still fires on it. The detached copy the actions read keeps taking every field.

**One CLI row per CLI (N-cli-model-effort-15).** "Claude Code CLI" and "Codex CLI" rows, each gated by its CLI's
`EnabledWhen` like the signed-in rows, the first carrying the card's condition. The saved CLI's row reads the runner's
cache as before; the other's shows what was last read of it and starts no read. Refused: a "Saved CLI" label on the one
row, which would still show the other CLI's model beside the radio. The "No CLI in use" sentence went with the single
row; the greyed card's reason line says it.

**The purge covers earlier storage roots, by the same strict shapes (N-remembrance-delete-logging-06).** When an Apply
moves the storage folder, the folder left is remembered in `earlierStorageLocations` (newest first, 8 at most) if it
exists, and every purge pass walks each remembered folder through the same `CaptureStore.Purge` as the current one.
One line for all of them ("purge, earlier storage folders: ..."), counts and exception types only. A remembered folder
that is missing or unreadable is said once per session while it stays so and kept, since a drive comes back. Refused:
seeding the list with folders used before this version, which nothing recorded; a user who moved away before 2.7.0 has
their earlier folder outside the purge until they point the storage folder there and back. A folder that did not exist
when it was left is not remembered: it holds nothing, and a fresh install's never-made `Documents\Remembrance` would
otherwise be reported missing at every start. Past the bound the oldest folder leaves the purge, and the move's log
line says so.

**A fresh install whose Documents is inside OneDrive records to `<data root>\Remembrance` (N-meeting-end-02, the owner's
decision of 2026-10-26).** "Fresh" is the storage location never saved; saved blank keeps `Documents\Remembrance`, which
blank always meant, and so does an install with `Documents\Remembrance` already on disk. The folder is the APP's data
root, beside its `modules` folder, never inside the module's own folder: `PendingModuleRemovals.ProcessPending` deletes
that folder whole when the module is uninstalled, and with it every transcript, which this module keeps for ever. The
data root is found from the module's folder's layout (`<data root>\modules\remembrance`) and the rule stands down when
that layout is not there or the data root is itself inside OneDrive. The folder is saved as the storage location at the
first capture, so a `Documents\Remembrance` that appears later (OneDrive bringing one from another computer) does not
move new recordings into OneDrive. The Status line adds "(on this computer, outside OneDrive)" and the blank box's hint
names the folder.

#### feature/cli-runner-aibrain

The shared coding-agent runner (`shared/CodingAgentCli`, compiled into AI Brain and Remembrance) and AI Brain's own
items of the 2026-10-26 campaign; versions are the coordinator's, set at publish. This lane made no model call. The
evidence is offline: Codex 0.160.1's ignored-key notice, captured by this lane against a loopback stub with a throwaway
Codex home and every proxy on the closed port 127.0.0.1:9 (arms `ign-top`, `ign-two` under the lane's TEMP, beside the
image-path study's arm `s-unk`); the strings of the installed Claude Code 2.1.293 binary, read and never run; and lane
measure-offline's permission-mode measurement (a throwaway Claude Code config folder and a loopback stub, 2.1.293 and
2.1.310). All of it local and not committed.

**A setting Codex ignored fails the call closed (BACKLOG N-codex-config-refusal-04, the owner's decision: "fail
closed").** Codex 0.160.1 does not refuse a `-c` key it does not know; it ignores it, says so in two `item.completed`
items of type `error` ahead of the turn ("Codex is ignoring 1 unrecognized configuration setting. Check for typos or
deprecated settings." then "  session-flags: `<key>` is ignored.", one such line per key, "settings" in the plural for
two), and answers. So a Codex that renamed `skills.include_instructions` would send the skills section and its
skill-roots table naming the profile folder again, and one that renamed `project_doc_max_bytes` every project `AGENTS.md`,
with nothing failing. Now such a call is `ConfigRefused` with `SettingIgnored`, naming the key, and its answer is not
used: "✗ Codex 0.160.1 ignored skills.include_instructions, one of the settings AI Brain starts it with, so its answer
was not used: check the Modules pane for an update ...". What it buys is that the change cannot go unnoticed: each call
still goes out with the setting ignored, and each one fails and says why. Counted only on a `session-flags:` line, the
layer the call's `-c` overrides are, only for a key the call set (`CallSetsKey`), and only where Codex ends it with "`
is ignored.". Not for the five token-only `features.<name>` keys (`CodexFeatureKeysOff`): the owner chose for those the
form whose rename "would cost tokens instead of failing calls" (`#### feature/codex-feature-switches`), and failing the
call on their notice would undo that decision. Refused: the notice's count alone (a key of anyone's), a key named anywhere
in the message, an `agent_message` that happens to hold the words, and "refused" for a setting Codex in fact ran
without, which would send a reader looking for a refusal that never happened. That a Codex which renamed one of the
call's own keys writes the measured words around it is inferred from the captures, not measured: 0.160.1 knows every key
the call sets.

**A retyped `features.<name>` key stays a plain failure (BACKLOG N-codex-config-refusal-05, the owner's decision: keep
the plain failure).** ACCEPTED-RECORDED. Codex 0.160.1 names the TABLE for one of the call's own feature keys given a
value of another type (``in `features` ``), and the same words come from a layer that broke `[features]`, which an
organisation can deploy beside the call's `-c` keys and where "check the Modules pane for an update" would be wrong. So
the ruling of `#### feature/codex-config-refusal` stands as the owner's: a table Codex names is never one of the call's
settings, and that failure shows Codex's own words. Not a defect; do not "fix" it without a new decision.

**A plain Codex failure quotes its stderr without Codex's leading WARNING lines (BACKLOG N-codex-config-refusal-06,
decision: fix).** With its home under `%TEMP%`, Codex 0.160.1 opens stderr with a WARNING line naming that folder and the
home's; a refusal's brackets left it out since the third round of `#### feature/codex-config-refusal`, and now so does
the plain "✗ Codex failed (exit N): ..." sentence. Leading lines only, and Codex only: a warning after Codex's error is
part of what it said, and Claude Code's stderr is quoted as written. The refusal reader still reads the whole stderr,
anchored on its own words.

**Every Claude Code model call names the permission mode `default` (BACKLOG N-codex-config-refusal-02, a privacy
control).** Lane measure-offline found that in auto mode, which a user's own settings can select, each request carries a
`safeguards` `classifier_context` block for the auto-mode classifier with 13 more copies of the module's work folder, the
home folder and `user_identity`, the Windows user name: 8,860 bytes on 2.1.293 with no setting at all, 8,908 on 2.1.310
with `permissions.defaultMode` "auto", and 2,041 on both with `--permission-mode default`. An unknown mode exits 1
before any request ("error: option '--permission-mode <mode>' argument '...' is invalid"), so a Claude Code that dropped
the value stops the call rather than quietly sending the block: it fails closed, like the Codex privacy switches.
`default` and not `manual`: the CLI reference gives `manual` as an alias of `default` that needs 2.1.200 or later, and
`default` as the mode's own value; both versions measured take it. The call has no tools (`--tools ""`), so the mode
changes nothing it can do. What remains, and is recorded so it is not taken for a slip: the env block's "Primary working
directory" line, one copy of the work folder, in the 2,041 bytes; no flag removes it, and the work folder is the
module's own. The probes carry no mode. The exact-argument pins in the self-check type it.

**`unified_exec` stays on whatever the switch (BACKLOG N-codex-feature-switches-01, closed by a source reading).** Lane
measure-offline read why `codex features list` shows it on under `--disable unified_exec`: `managed_features.rs`,
`normalize_candidate` (:157-161), turns `unified_exec` back on unless a `requirements.toml` pins it, on every platform;
and the shell the model could reach is governed by `shell_tool` (`spec_plan.rs:1079-1088`), which the call turns off. The
comment at `CodexDisabledFeatures` says so; the switch stays, failing closed like the others and costing nothing.

**An organisation's managed MCP configuration is its own outcome (BACKLOG N-cli-home-files-06).** Claude Code's
managed-MCP docs say a `--strict-mcp-config` run exits at startup while a `managed-mcp.json` is deployed, and every
companion model call passes that flag. The words are the 2.1.293 binary's, "You cannot use --strict-mcp-config when an
enterprise MCP config is present", read from its strings and never seen in a run: no machine here has such a file, and
none was deployed to see one. Matched without regard to case or a prefix, for a Claude Code call that passed the flag,
as `CliOutcome.McpPolicyRefused`: "✗ Your organisation's MCP policy refused this Claude Code call: a managed MCP
configuration on this machine keeps control of Claude Code's MCP servers, and AI Brain starts Claude Code with none of
them (--strict-mcp-config). Choose another engine under Brain runs on." Refused: dropping `--strict-mcp-config` where such
a file is found, which would load the user's own MCP servers into a companion call.

**Keep my Claude Code settings out of companion calls: an opt-in, default off (BACKLOG N-cli-home-files-01, the owner's
decision; -02 and -03 covered by the same switch).** With it on, each Claude Code model call carries `--setting-sources`
with an empty list, so none of the user's, the project's or the local settings files is read (204 tokens of the user's
own `settings.json` on the maintainer's machine, its permission rules and folder names, N-cli-home-files-02; and the
synced-plugin sync round that writes under the profile, N-cli-home-files-03). Off by default because it also drops a
sign-in helper (`apiKeyHelper`), `forceLoginMethod`, and a proxy or any other variable in an `env` block, which a call
can depend on, and the saved-token path was never measured with it. Managed settings and the call's own `--settings`
still apply. `tests/runtime-hardening-selftest.ps1` took `--setting-sources` off the `cli-home-files` refused list and
allows it in BuildArguments' Claude Code branch alone, once, behind `CliRequest.LeaveOutOwnClaudeSettings`. Validate
tests the switch as the card shows it, like the model and the effort. Remembrance compiles the same runner and gains
the switch when its pane row is wired (another lane's boundary).

**Smaller ones.** A call that started and was stopped logs `no-exit` in place of the `exit=0` it never had
(N-cli-model-effort-04). A Validate still running when an Apply saves another choice for its CLI records no tick, by a
per-CLI saved-choice generation that `ForgetValidationUnlessTested` moves on and `RecordValidation` checks, the way
`SameSignIn` guards a sign-in change; an Apply of the very choice tested keeps it (N-cli-model-effort-13). The effort
refusal says "the automatic pick (gpt-6.1-sol)", the words every row says it in (N-cli-model-effort-14). Remove token
tries the delete again up to five times, 100 ms apart, when the file is held open, and reports the failure as before if
the last try fails (N-codex-config-refusal-01; the holder Remembrance's self-check met was never identified).

**AI Brain: the switch on the pane.** "Keep my Claude Code settings out of companion calls" is a Bool row in the CLI
card, live only while Claude Code is the CLI on screen (an `EnabledWhen` strictly narrower than the card's gate, as the
token row's is), saved as `CliLeaveOutClaudeSettings`, false in a file without the key. The live brain and the auditions
take it through `CodingAgentBackend`'s constructor, as they take the model and effort; Validate takes the value on screen.
"Goes through Claude Code" adds a sentence naming what it drops while it is saved on.

**AI Brain: one CLI row per CLI (BACKLOG N-cli-model-effort-15).** The one "CLI" row described the saved CLI in live text
while every row under it followed the radio. Now "Claude Code CLI" and "Codex CLI" rows each carry their CLI's
`EnabledWhen`. The Claude Code row is the card's first field, so it also carries `PinTop` and `CardEnabledWhen`. The saved
CLI's row reads the runner's cache, refreshed behind it; the other row shows what was last read of that CLI and starts no
read, the rule the signed-in rows keep. Until something reads it, it says which CLI to choose before Validate. The brief's
fallback, a "Saved CLI" label, was not needed. "No CLI in use" went with the one row: on the local slot both rows are
greyed with the card.

**AI Brain: the pane hears about a remark, an audition and a Validate (BACKLOG N-cli-model-effort-07, AI Brain's half).**
`OptionsPane.NotifyInfoChanged`, added in host 1.6.1, asks the host to reload an open pane's Info rows. AI Brain keeps its
pane and calls it after each of the three, so the Status card's last remark and the CLI rows' "last answered" show what
just happened. The register's "A module cannot push a live value into an open options pane" (Known ABI gaps) was written
before that member existed; for a pane it no longer holds, while the tray-menu half stands. Using the member raises the
module's floor to host 1.6.1; the coordinator sets MinHostVersion with the version. Load stays what a pane open always
ran: cached reads, plus the background refreshes CachedDetails and VramStatusLine start when theirs are stale. So a
reload never waits on a child process or the network, and nothing a reload starts notifies again. Remembrance's half is
another lane's.

**AI Brain: smaller ones.** "When OCR is used" says what each engine does (N-cli-model-effort-01): on the local slot a
model reported blind is swapped for one that can see, and a cloud primary ends the turn saying the model can't see images;
only a CLI turn for a Codex model that takes no images reads the screen as text. A Local model Apply stores
`LastCloudProvider` only when the dropdown shows something other than what Load showed (N-cli-model-effort-11, AI Brain's
half), so an install that never chose a provider no longer gets "openai" written. That is one field's fix, not the general
"store what changed" rule: Apply still writes the four model and effort keys as it always has, by the ruling of
`#### feature/cli-model-effort`. The corrupt-settings recovery names the delete failure's exception type
(N-remembrance-delete-logging-05).

**AI Brain: the stronger no-names rule (BACKLOG N-cli-model-effort-03, part 1 of 2).** A CLI turn's persona adds a
sentence: never use the signed-in person's name, username, email or organisation, or any name from the assistant's own
context, and never repeat a person's name, handle, email, phone number or other private detail seen on the screen. The
name the user typed in the pane stays the one allowed. It goes on both CLIs and on neither slot, whose prompt is byte for
byte as before. Whether it lowers the 8 to 25% the eval measured on Claude Code is for the Claude arms run again, in
another lane, with the owner's approval. Until then the item is part 1 of 2. The default choice of opus at medium rested
partly on that rate, so the eval may move it too.

#### feature/agentbroker-backlog-a

Per-client onboarding states (N-agentbroker-client-onboarding-01), the LAN network profile
(N-agentbroker-lan-profile-01) and the coordination benchmark (O-agentbroker-02). No federation record, persisted
state, tool, limit, `BrokerStore.cs` or `BrokerTools.cs` changed.

**The client configuration check runs only when asked.** `claude mcp get` starts the vendor CLI and makes it connect to
the configured entry, and Codex's get starts its CLI too. Running that on the two-second status poll, or at every
companion start, would start a vendor process per poll. A client row therefore says "not checked" until the person
chooses **Check client setup**, a Connect action or the tray item, and a settings restart clears it, because the check
compared the entry with the previous port and paths.

**Most inboxes show as carrying no client identity, and that is the evidence, not a defect.** An inbox counts for
Claude Code or Codex only when its own successful `register_agent` request carried that client's identity: its
initialize, or 2026 per-request metadata. Current clients mostly send stateless tool calls without either, so their
inboxes are listed as unidentified. Refused alternatives: the session label (self-reported), the most recent
initialize on the endpoint (another client's), and the existence of the client's MCP traffic (a connection is not an
inbox). Clients that send per-request identity are attributed with no change here.

**The companion never launches a script shim for a vendor CLI.** `Process.Start` with a bare name finds only an
`.exe`, so a CLI installed as an npm `claude.cmd` or `codex.cmd` shows "CLI not found". Running a `.cmd` means cmd.exe
parsing of the worker path and data directory; resolving the shim to the package's bundled executable would pin the
package's internal layout. Left for an owner decision as N-broker-a-01.

**A 1.0.4 module cannot read a 1.1.0 snapshot that carries the new members.** The control protocol disallows
unmapped members. The new `BrokerSnapshot.Lan` and `McpStatusView.Inboxes` are omitted while null, but a LAN reading
is present whenever LAN is enabled, so an older companion attached as standby to a newer worker reports its worker
status unavailable until it is updated. That is the shape the 1.0.3 `Mcp` member already had. The module and worker
ship in one package, so it takes two companions of different versions for one user.

**Several networks on one adapter resolve to the most restrictive category** (Public, then Private, then Domain),
because that is the profile Windows Firewall applies. The Network List Manager interfaces declare vtable slots only
up to `INetwork.GetCategory`: `SetCategory` follows it and is not declared, so this code cannot call it.

**Observed LAN reachability has no positive check on one workstation.** The worker records an HTTPS request on the
LAN listener whose source is a private address other than loopback and other than the local address. Producing one
needs a second PC, so the observation logic is checked with synthetic addresses and the one-line hook in the request
pipeline is left to the physical LAN acceptance.

**O-agentbroker-02: the snapshot store is not what limits sustained coordination (DECLINED-MEASURED for a journal
or SQLite).** Measured 2026-10-27 with `tests/Measure-AgentBrokerCoordination.ps1` against the unmodified store:
five fresh processes per configuration, interleaved round-robin with the start rotated per repetition, Stopwatch
around the operation inside the process, state on the SSD volume the installed module's data uses, on a workstation
shared with other builds. Cold is the first operation in a process; warm is the median of the rest across all five.

| Measured | Cold median | Warm median (max) |
|---|---|---|
| Accept + Commit, 100 retained, 512 B (146 KB state) | 25 ms | 16 ms (19) |
| Accept + Commit, 1,000 retained, 512 B (1.5 MB) | 45 ms | 39 ms (44) |
| Accept + Commit, 4,096 retained, 512 B (6.0 MB) | 121 ms | 115 ms (130) |
| Accept + Commit, 1,000 retained, 16 KiB (17 MB) | 263 ms | 184 ms (278) |
| Accept + Commit, 4,096 retained, 16 KiB (71 MB) | 683 ms | 667 ms (926) |
| Accept + Commit, 4,096 retained, 16 KiB of escaped characters (407 MB, the serializer's worst case) | 2,637 ms | 2,688 ms (3,060) |
| `wait_for_messages` wake, send issued to waiter's reply, HTTP MCP | 44 ms | 18 ms (108) |
| Federated `list_sessions` page, 8 peers of 16 sessions | 269 ms | 24 ms (33) |
| Federated `list_sessions` page, 32 peers of 16 sessions | 579 ms | 324 ms (375) |
| Federated `list_sessions` page, 32 peers of 256 sessions (all 8,192 returned by the runtime) | 595 ms | 352 ms (396) |

The peers were real workers with mutual TLS on loopback in one process, so a LAN adds its round trips to every
handshake; every page opened a new connection to every peer and still finished inside the five-second directory
budget with every session present. At ten messages a minute, about twenty commits a minute (each message and its
acknowledgement), the measured warm cost keeps the store busy about 4% of the time at 512-byte messages with all
4,096 retained, about 22% at 16 KiB, and about 90% in the escaped worst case. That last case also holds the store lock
for about 2.7 s per commit, close to the module's three-second status deadline (`ControlProtocol.ClientTimeout`).

What breaks first is the retained-message bound, not storage cost. A receipt stays until its expiry plus one day
(`BrokerStore.Expire`), and Accept refuses `message_limit` at 4,096 retained (`BrokerStore.Accept`), so ten messages a
minute reaches the bound after 410 minutes whatever the lifetime. By the same arithmetic the sustainable rate is
4,096 per lifetime plus a day: about 1.4 a minute at the default 24-hour lifetime. No limit was changed in this lane;
filed as N-broker-a-02 for an owner decision.

#### feature/agentbroker-backlog-b

AgentBroker Codex discovery trust and the opt-in update-tolerant delivery policy. Strict exact pins stay the
default; the per-vendor contract is in `docs/agentbroker-client-compatibility.md`.

**An unpinned Codex router is listed, yet nothing can be sent to it.** That is observation, not a broken wake
switch. A router that fails its exact pin but passes Windows-verified publisher trust yields bindings marked
`PublisherSigned`; their rows have no capabilities, and both the worker adapter (`codex_observation_only`) and the
adapter library (`router_observation_only`) refuse before any frame. Refused alternative: dropping such sessions,
which is what strict discovery did, so a desktop package update hid every Codex session until the next re-pin.

**Update-tolerant Codex rows advertise `NativeIdleGuard`, the same as exact rows.** The submission path and its
runtime checks are the same code, and the owner's own `supportsUntrustedAppInput` is checked before each send.
What an unreviewed build does not re-prove (settings inheritance, the double idle check, the busy error's meaning,
unsigned extension JavaScript) is the gap the owner accepts by opting in, which is why the switch is off by
default and status names the mode and the failed pin on every pass. Refused alternative: withholding the guard,
which makes Unknown-activity sessions unreachable and the switch inert in practice.

**Claude has no update-tolerant switch.** Its delivery boundary (`NativePeerAdapter`) checks `EngineProfile` for
the target and the connected image, and no bounded runtime check can show how an unreviewed build classifies a
peer token. An unpinned Claude build stays observation-only and is re-pinned exactly, with the read-only
`tools/agentbroker/Test-AgentBrokerRepin.ps1` drafting the pin.

**`IDesktopConnection.Trust` has no default member, and `DesktopOwnerBinding.RouterTrust` defaults to
`Unverified`.** A test double or a new connection type must state its trust, and a binding nobody stamped grants
nothing. The adapter refuses a binding whose trust differs from its connection (`stale_binding`).

**The module sends `--codex-update-tolerant-delivery` only when the switch is on.** The default launch is the
argument contract every existing worker accepts; an older worker given the opt-in fails to start visibly rather
than ignoring it.

#### feature/agentbroker-backlog-c

The opt-in Claude Code channel bridge (`--mcp-channel`, `tools/AgentBroker.Worker/McpChannelBridge.cs`). It is
a client of the existing broker surface (the stdio bridge's validated loopback connection plus the management
pipe's status snapshot) and changes no store, tool, federation or persisted record.

**A pushed message stays Accepted.** Claude Code never acknowledges `notifications/claude/channel` and drops it
silently when the session did not opt the server in, so a push proves nothing about the recipient. Only the
session's `acknowledge` call records a receipt. Refused alternative: letting the recipient record `Submitted`
after a push, which would also stop waits returning pushed messages. It changes `acknowledge_message`'s outcome
set and the store's receipt transitions, which this lane does not own.

**Pushes come from the oldest 16 unacknowledged messages, with a 2-second poll while any are outstanding.**
`wait_for_messages` returns at once while anything is Accepted and returns the oldest 16 by creation time, so the
bridge remembers what it pushed, pauses between waits that bring nothing new, and sees no newer message until the
session acknowledges older ones. That is backpressure, not a stall. The bound is 16 only while senders' clocks
agree: a paired broker's message stamped earlier than the window can still enter it.

**Mute defers; an unpaired sender and a paired sender to a private inbox are refused.** The broker already refuses
both at admission and again in its delivery loop, but a wait blocked across a revocation or privacy change can
return the message first, so the bridge re-reads the snapshot after each wait. Mute has no broker-side meaning for
a generic inbox (a polling agent pulls), so for a pushed channel the bridge is the only place it can apply.

**The bridge registers again between waits once 20 seconds have passed, instead of trusting waits to renew it.**
A wait on a mailbox the broker no longer knows
(worker restart) still returns its pending messages, renews nothing and never reports `session_unavailable` while
anything is pending. A bridge holding one unacknowledged push would otherwise stay unreachable for good; the
synthetic check that drops the registration found exactly that before the timer existed.

**One clientId and incarnation per bridge process.** Claude Code starts one bridge per session, so each session
gets its own inbox. Owner preferences are keyed by session ID and so do not survive a bridge restart. Refused
alternative: a persisted clientId, which would let two concurrent sessions share one inbox and race its waits.

**`reply` answers only messages addressed to the bridge's own inbox.** The broker authenticates brokers, not
sessions, so `reply_to_message` alone would let the channel session answer as any local session that received a
message. The reply ID is derived from the incarnation, the original ID and the exact text, so the model never
invents an ID and an identical retry returns the original receipt.

**Revision 2026-07-28 is answered with 2025-11-25, and `claude/channel/permission` is absent rather than false.**
Claude Code's v2 runtime does not register a channel on the newer revision. Claude Code before 2.1.234 treated
`false` as declared, and a declared permission relay would let any agent able to message the inbox answer the
session's tool-approval prompts.

**Not wired into Connect Claude MCP.** Custom channels are a research preview that needs an interactive
development flag per session and, in Team and Enterprise organizations, an organization switch. A button would
present that as a supported route.

#### feature/agentbroker-backlog-d

The loopback listener's localhost authority (N-bus-04), waits on a restart-lost mailbox (N-broker-c-02), client setup
finding npm-installed CLIs (N-broker-a-01) and broker-b's hand-backs.

**The loopback listener answers `localhost:<port>` as well as `127.0.0.1:<port>`, and nothing else.** The org's Claude
Code policy admits local MCP only as `serverUrl` `http://localhost*`, so a `claude -p` session given the literal URL
had no tools (owner decision 2026-10-27, "Accept localhost"). The DNS-rebinding guard keeps its point: any other name
can be pointed at 127.0.0.1 by whoever controls it, and still gets 403. The port must be the listener's own, because
another port's authority is not this listener; a Host without a port means port 80. Refused alternative: ASP.NET
Core's HostFiltering, whose `AllowedHosts` excludes ports.

**No `[::1]` listener: measured unnecessary.** On 2026-10-27 two fresh `claude -p` sessions (Claude Code 2.1.293, the
npm build), given `http://localhost:<port>/mcp` through `--mcp-config` with `--strict-mcp-config`, registered, sent,
waited, acknowledged and replied through a worker listening on 127.0.0.1 alone (`Get-NetTCPConnection`: 127.0.0.1
only), on a workstation whose resolver returns `::1` before 127.0.0.1 for localhost. The client falls back to IPv4 by
itself, so binding `[::1]` would add a second socket for no measured client.

**Client setup writes the literal URL first and tries localhost once, after an allowlist miss only.** Claude Code's add
handler refuses an allowlist miss with "not allowed by enterprise policy" and an explicit deny with "server is
explicitly blocked by enterprise policy" (read in the npm build's public `claude.exe`, 2026-10-27), before it writes
anything. Only the first gets the one `http://localhost:<port>/mcp` add; a deny, exclusive or plugin-only control and
every other failure are final, and stdio is never tried instead. Writing localhost first was refused: the literal URL
keeps working where no policy applies and is what the worker reports. Either spelling for the worker's port is the
exact entry when setup is checked; another port, or a non-canonical spelling such as `LOCALHOST`, is a different entry.

**Client setup finds the CLIs by a copy of the model calls' locator, not a link to it.** The rules are
`CodingAgentLocator.Locate` in `shared/CodingAgentCli/CodingAgentCli.cs` and `AiExecutablePolicy.ResolveConfigured`,
rung for rung, in `modules/AgentBroker/AgentCliLocator.cs`. Linking the shared file would compile the whole
nullable-oblivious model-call runner into the one module built with nullable warnings as errors, and make every runner
change stale this payload. So a change to the shared locator's rules has to be made in both places. This reverses
broker-a's "never resolve a shim to the package's binary": that pins npm's package layout, but the model calls already
depend on the same layout, so a layout change now breaks both together and visibly instead of setup alone. A shim is
still never run, and the located binary starts once.

**A wait checks addressability before it returns anything.** A wait on a mailbox the broker no longer knows (worker
restart) returned its pending messages and said `session_unavailable` only when there were none, while
`acknowledge_message` refused those same messages as `recipient_unavailable`. An agent then saw messages it could not
acknowledge, and no refusal telling it to register again. The channel bridge's 20-second re-registration timer stays
as a second path, so its broker-c witness (`broker-c-channel-renew-only-by-wait`) now removes both ways back.

#### feature/abi-pane-actions

Two additive ABI members for host 1.6.2 (`src/DesktopAICompanion.Contracts/PluginApi.cs`), each adopted by the module that
asked for it, and one refused. A module that uses either declares `MinHostVersion` 1.6.2.

**`PaneAction.EnabledWhen` is read by the fields' own parser, and `CardEnabledWhen` keeps its "one string per card".** That
comment refused an `EnabledWhen` per button because a greyed card greys all of its buttons together, and that still holds:
nothing moved off the card's condition. The new member is for the one shape the card's cannot say, a button inside a card
that is LIVE which serves only one state of it. AI Brain's and Remembrance's `Remove token` deletes the Claude Code sign-in
token and stayed pressable with Codex CLI chosen while the token row above it greyed (N-cli-model-effort-05). Both now carry
the token row's own condition. The host reads it through `PaneView.IsSatisfied` and the same updater list as a field row,
so it is settled at the end of the build and after every edit, can name a field in a later card or a display-only one, and
is dimmed once inside a greyed card. It greys the button's whole row, its result line included, and the button refuses
through the row's `IsEnabled`, the check its click handler already makes for a card greyed whole.

**A greyed action keeps the message it wrote.** After `Remove token` answers "✓ Removed." and the user moves the CLI to
Codex, the row greys with that answer still beside the button, which can read as a stale line. It is the only record on
screen of a delete the user made, and greying says the button does not apply now, not that its last answer was wrong.
Refused: clearing a greyed row's message.

**A module is told a refused catalog entry's CATEGORY, never the host's rule.** `ICatalogListing.FetchCatalogListingAsync`
(N-catalog-insight-05) answers, from one read of the shared copy, the items `FetchCatalogItemsAsync` answers plus the
entries of that kind the parser refused, each as `RefusedCatalogItem`: an id that passed the safe-id rule ("" when the id
was the fault), its 1-based position, and a `CatalogRefusalReason`. The rule ("description is 1049 characters, the limit is
1024"), the name and the version stay in the host: each holds the catalog's own text, which the panes put through
`CatalogText.Echo`, and the diagnostic log already carries the rule whole. Refused: handing modules the sanitised rule,
which would make a module a second renderer of remote text and the host's rule wording an ABI no clause could be reworded
under. The category is set where each rule is chosen (an `out` beside each clause of `IdRule`, `PetRule` and `PackRule`),
never parsed back out of the words, because a refused id is echoed into them. Module entries keep `Other` for their own
rules: no module browses that kind.

**A sibling interface, not an `IHost` member and not a flag on `CatalogItem`.** Refused: listing refused entries inside
`FetchCatalogItemsAsync` with a "refused" field, which every Fortunes before this one would have shown as packs to download;
and a new `IHost` member, which changes `ModuleKit.Testing.RecordingHost` and so republishes every module, the reason
`ICompanionStandDown` and `ITrayQuestions` give. One call answers both lists so they can never describe two catalogs, as
two calls straddling the 90-second shared copy could.

**Fortunes names a refused pack in the check's answer, not as a row in the list.** "Check online for packs" now answers,
under its usual line, "⚠ Not offered: “<pack>” (<why>). Nothing on this PC needs fixing.", naming up to three and counting
the rest, and a pack already on disk is left out because Available online never lists one. A list row was the first
choice and is refused: a `ListItem` is always a checkbox, so a refused pack there would tick, count in "All packs" and
download nothing, and a row that cannot be ticked is a further ABI addition this lane was not asked for. The Companions
pane says its refused entries in its check line too. The note does not say "Published wrong" as the red block does: a
pack in a data format this app does not read may be a newer format, which is no publishing fault, and "nothing on this PC
needs fixing" is true of every category. N-layout-fortunes-01's red block and its wording are unchanged.

**N-cli-token-02 DECLINED: no host-held credential store shared across modules (owner ruling, campaign 2026-10-26).** What
was refused: a host-side store, an ABI addition, from which AI Brain and Remembrance would read one Claude sign-in token,
so a user running both on Claude Code pastes it once instead of twice. Why: today each module's token is sealed in its own
CLI runner folder (`shared/CodingAgentCli/CodingAgentCli.cs`, `ClaudeTokenPath`, DPAPI for the current user) and no host
verb hands it to anyone; a shared store would give every module that can reach it the token another module's calls run
on, widening each module's access to another's sign-in to save one paste, which the user makes once per module. Modules are
not contained (see `docs/module-ecosystem-roadmap.md`), so this is about what the ABI offers, not a sandbox it would
breach. What would change it: a store whose every read is a per-module, per-credential grant the user sees on the consent
screen, or the host making the Claude Code calls itself so that no module ever holds the token; either is a design of its
own, not this one, and the owner's say-so.

#### feature/agentbroker-backlog-e

Tombstone on acknowledgement (N-broker-a-02, owner decision 2026-10-27, "Tombstone on ack"). `BrokerStore.cs` and
`BrokerLimits.MaximumTombstones` changed, plus the receipts route's lookup in `PeerFederation.cs` and four tool
descriptions. No federation record, schema-1 state member, tool, parameter or limit a peer sees changed.

**A message from a paired broker keeps its text after it is acknowledged.** The federation wire forces that; it is
not an oversight. The sending broker settles a message only by fetching `/federation/receipts/{id}` and comparing the
whole message with its own copy, text included (`BrokerRuntime.ReconcilePeersAsync`, unchanged since 1.0.4). An empty
`Text` passes a 1.0.4 peer's strict reader, because only null is refused, and that comparison then ignores the receipt
on every pass. A 404 is ignored the same way. Either one leaves a delivered message at Submitted, and later Uncertain,
on the sender for good. So only messages this broker sent become tombstones, and the route answers a tombstoned ID with
404, as it answers an unknown one (`FindReceiptWithText`). Nothing legitimate asks for one: 1.0.4 and this version
query only the receipts of messages they sent. Refused alternative: tombstoning an inbound message once its sender
has fetched a final receipt. That turns a GET into a write and strands the sender at Uncertain whenever that one
response is lost.

**Held keeps its text and still counts toward `message_limit`.** Held is not final: it can still become Delivered,
Refused or Expired, the recipient that held it may read it again, and a paired sender keeps polling a Held receipt and
compares it whole. **Expired keeps its text as before**: it was never acknowledged, and its recipient can still read
it until the record goes.

**The grace after expiry stays one day, for tombstones too.** It is the window for a late reply (a parent's expiry
bounds delivery, not the work) and for retry de-duplication. A tombstone is small enough not to need less, so the
shorter grace broker-d offered as option D (the message's own lifetime, capped at 24 hours) is not needed; it would
have shortened the reply window of every short-lived message.

**Tombstones are an append-only journal beside the snapshot, not part of it.** `broker-state.json` keeps schema 1
exactly and holds only messages with text: a 1.0.4 loader disallows unmapped members, refuses more than 4,096
messages and refuses an empty `Text`, so tombstones could not live there. Rewriting a tombstone file on every
acknowledgement would cost what the snapshot does, about 105 ms per commit at 4,096 records of 512 B (below), so
each acknowledgement appends one write-through line to `broker-tombstones.jsonl`. The tombstone is appended before
the snapshot drops the message. A crash between the two leaves the message in both files, the snapshot wins at load,
and the worst outcome is an acknowledgement the recipient repeats; the other order could lose both. A torn last line
is the interrupted append it looks like: it is ignored and the next append truncates it. Any other malformed line
refuses to load and is preserved, as the snapshot is. Expiry prunes only memory, so a read never writes; the journal
is compacted once its dead lines outnumber live ones by 4,096. A failed compaction is not reported as a failed
commit, because the old journal is still complete. The first line names the broker identity, and a journal from
another identity (a replaced `broker-state.json`) is replaced at the next append. Opening a state an older worker
wrote moves its acknowledged messages to tombstones at once.

**`acknowledge_message` answers with the tombstone, empty text included.** The answer is what the store now keeps,
so a first and a repeated acknowledgement read alike. The text already came back from the wait that delivered it.
Refused alternative: echoing up to 16 KiB of text once more on the call that removes it.

**`read_inbox`, `get_message_receipt` and `wait_for_reply` show an acknowledged message with its status and an empty
`text` rather than omitting it.** Keyset paging and cursors stay as they were, and an agent can still see what it
acknowledged. Empty text is unambiguous: admission refuses empty and whitespace text.

**A rollback to 1.0.4 loads, and loses only the tombstoned IDs.** Checked against the published 1.0.4 store itself
(`tests/AgentBroker.Worker.Tests/Rollback104/`, the 1.0.4 sources byte for byte except the namespace), not a model
of it. For each tombstoned ID still within its retention, a 1.0.4 worker reports an unknown receipt, admits a retry as
a new message and delivers it again, refuses `reply_to_message` and `wait_for_reply` on it, and omits it from
`read_inbox`. That is more than de-duplication: receipt lookup and reply routing go with it. Pending, Held, Expired
and paired-broker messages are untouched. Messages 1.0.4 acknowledges meanwhile keep their text until this version
starts again and moves them; an ID 1.0.4 admitted again is its full record, over the older tombstone.

**`MaximumTombstones` is 65,536, sized from a measurement.** Only messages with text count toward `MaximumMessages`
(4,096); the new cap bounds tombstones and those messages together, so an acknowledgement always has room and a send
past it is refused as `message_limit` instead of dropping a tombstone and with it a retry's de-duplication. Measured
2026-10-27 with `tests/Measure-AgentBrokerCoordination.ps1 -Scenario tombstone`: five fresh processes per row and
build, interleaved round-robin with the start rotated per repetition. The control is the base store (371407b) built
with this lane's benchmark driver copied in (the same file, SHA-256 checked), so both builds run identical arguments.
Stopwatch inside the process around the operation only; state on the SSD volume the installed module's data uses;
the workstation was shared with other builds and a model evaluation. Warm is the median of the five operations after
the first, across all five processes. "Acknowledged" rows seed the snapshot with Delivered 512-byte or 16 KiB records,
the shape the current store keeps; the tombstone store moves them to tombstones as it opens, which its open time
includes. "Tombstones" rows seed the journal directly, which the control cannot hold.

| Measured (warm median unless stated) | Current store | Tombstone store |
|---|---|---|
| Send, 4,096 unacknowledged 512 B (the old bound, nothing acknowledged) | 107 ms (cold 124) | 110 ms (cold 116) |
| Send, 4,090 acknowledged 512 B | 106 ms | 11.2 ms |
| Acknowledgement, 4,090 acknowledged 512 B | 102 ms | 8.4 ms |
| Send, 4,090 acknowledged 16 KiB | 629 ms | 11.3 ms |
| Acknowledgement, 4,090 acknowledged 16 KiB | 616 ms | 8.3 ms |
| Open, 4,090 acknowledged 512 B / 16 KiB | 117 / 530 ms | 186 / 650 ms |
| Store heap after open, 4,090 acknowledged 16 KiB | 137 MB | 3.9 MB |
| Send / acknowledgement, empty store | 11.6 / 11.5 ms | 11.3 / 8.6 ms (first acknowledgement 23 ms cold, it creates the journal; 12 ms before) |

| Tombstone store, live tombstones (512 B messages) | Journal | Open | Heap | History read | First acknowledgement |
|---|---|---|---|---|---|
| 16,384 | 12 MB | 384 ms | 14 MB | 5.9 ms | 26 ms |
| 65,512 (the cap) | 50 MB | 1,153 ms | 56 MB | 28 ms | 41 ms |
| 131,060 (cap raised) | 99 MB | 1,911 ms | 112 MB | 58 ms | 61 ms |
| 262,132 (cap raised) | 198 MB | 2,960 ms | 225 MB | 131 ms | 93 ms |
| 65,512 plus 69,700 dead lines (that acknowledgement compacts) | 50 MB after | 1,913 ms | 71 MB | 23 ms | 346 ms (max 446) |
| 262,132 plus 266,300 dead lines (compacts) | 198 MB after | 5,581 ms | 272 MB | 101 ms | 1,407 ms |

Warm send and acknowledgement stay at 11 and 8 ms at every size: an append does not grow with the journal. History
read is `store.Messages` for the session every record involves, the list each `read_inbox` page and wait pass builds.
The compaction rows warm the code paths on a scratch store first, so their first acknowledgement is the compaction, not
the JIT. Load, heap, history read and compaction grow linearly. At 65,536 the worst compaction (446 ms) holds the store
lock well inside the module's three-second status deadline (`ControlProtocol.ClientTimeout`), and the worst open
(1.9 s with a full set of dead lines) is far inside the 15-second startup grace. At 262,144 a compaction holds the lock
1.4 s and the heap passes 200 MB. A tombstone costs about 850 bytes of heap.

Sustained rate at the default 24-hour lifetime: a tombstone lives 48 hours (lifetime plus the one-day grace), so 65,536
sustains 65,536 / 2,880 minutes, about 22.7 acknowledged messages a minute, against about 1.4 before (4,096 over the
same 48 hours). At that rate the measured warm cost (11.2 ms send plus 8.3 ms acknowledgement) keeps the store busy
about 0.44 s a minute, plus one compaction per (live tombstones + 4,096) acknowledgements, about 0.35 s at the cap. Ten messages a minute,
which met the old bound after 410 minutes, now needs about 28,800 tombstones at most. Messages nobody acknowledges
still meet the 4,096 bound, as before.

#### feature/cli-followups

The shared runner's follow-ups from lanes measure-model and cli-runner, and two owner decisions of 2026-10-27, for AI Brain
1.6.0 and Remembrance 2.7.0 (versions are the coordinator's, set at publish). This lane made no model call; its evidence is
lane measure-model's captures of that morning (Codex 0.160.1, local and not committed).

**Every Codex model call turns Codex's own analytics off (BACKLOG N-cli-home-files-07, the owner's decision: "Always off
for companion calls").** `-c analytics.enabled=false`, beside `include_environment_context=false`. The call starts Codex
with `--ignore-user-config`, so a choice in the user's own `config.toml` never reached it. Measured by measure-model with
four real calls, the TLS server names of the Codex process tree captured: without the key each call reached chatgpt.com and
ab.chatgpt.com; with it chatgpt.com alone, the answer came back and Codex reported no key ignored. What either host was sent
was not read. A Codex that later ignores the key fails the call closed naming it, by the path every `-c` key the call sets
already takes (`CodexIgnoredSetting`); the self-check holds that path for this key. The probes carry no analytics key.

**A Codex server error is quoted as its message and parameter; the classifier still reads it whole (BACKLOG
N-measure-model-01).** Codex passes the server's JSON error on as the message text, laid out over several lines with
`"param": "reasoning.effort"` last, past the 240 characters a failure quotes. So `RefusalNamesEffort`, which reads that
excerpt, missed Codex's real effort refusal, the sentence sent the user to the model row, and the automatic pick was
forgotten for a refusal that was not about it. `CodexErrorWords` reduces such a document to "message (param)", and a message
Codex gives twice (an error event and again in turn.failed) is quoted once. Only what is quoted changes: `Classify` reads
the messages as Codex wrote them, the document's status and code included, so no failure changes class by this. Refused:
deciding on the whole text and carrying the result on the answer, which would leave the pane quoting raw JSON and the
sentence "Its words are about the effort" about words the excerpt cut off. The self-check's F17 reading now runs on the real
capture (`CodexServerCaptures.MinimalEffortStdout`), with the thread id and the five managed-requirement items, which named
an organisation's requirement layers and a group id, taken out.

**"Selected model is at capacity" is its own outcome, `ModelAtCapacity` (BACKLOG N-measure-model-02).** Two of
measure-model's judge calls met it and both answered when asked again, so it is said as passing, with ⚠ as Busy is: "⚠
Codex's server has the model X at capacity right now: try again shortly, or choose another model in the CLI card." Log word
`model-at-capacity`. The automatic pick is kept, since the model was not refused. Refused: retrying the call by itself, which
is a second call's usage for an answer the user did not ask twice for, and which AI Brain's retry predicate deliberately
never does for a CLI failure.

**AI Brain warns on the Haiku choice in an Info row gated on the model row (BACKLOG N-measure-model-03, the owner's
decision: "Warn on the Haiku choice").** "About Haiku", right under the Claude Code model row, live only while that row
shows Haiku (`EnabledWhen = "cliClaudeModel=Haiku"`), with the measured figures: under the no-names rule both judges found
a name or private detail read off the screen in 14 of 112 Haiku remarks, 0 for Opus and 1 for Sonnet. Not the option's
text: the owner ruled the options plain names on 2026-10-09, the first build's longer labels were cut off in the 177 DIP
editor column, and no label that fits holds the figure. What one condition per row costs, and is not a defect: with Codex
CLI on screen and Haiku still showing in the greyed Claude Code model row, the note stays live, and what it says is still
true of that row. The layout check's rule that a row's own gate narrows its card's on the same field gains this one
exception, a display-only row gated on another row of its own card (`IsNoteOnARowOfItsCard`). Remembrance is left out on
purpose: its summaries are meant to name the meeting's participants.

**Remembrance's card has the settings switch (lane feature/cli-runner-aibrain's hand-off).** "Keep my Claude Code settings
out of companion calls" after the Claude Code effort row, live only while Claude Code is the CLI on screen, stored as
`cliClaudeSettingsOut` and false without the key. A recording's summary reads it from the saved settings at the stop with
the model and effort; Validate and "Summarize a transcript…" read it from the screen. "Goes through Claude Code" adds AI
Brain's sentence while it is saved on. No code in the module names the flag: `tests/runtime-hardening-selftest.ps1` counts
it in the modules' code (N-cli-runner-03), so the module's checks read a command line through `FakeCliCall`.

**A details read the cache started tells an open pane when it lands (BACKLOG N-remembrance-backlog-01).**
`CodingAgentCli.DetailsRead`, which both modules hand `OptionsPane.NotifyInfoChanged`, is called after a read
`BeginDetailsRefresh` started, and only when that reading is the one kept and still fresh, so the reload it causes finds it
fresh and starts no read: a notification never starts another. The saved CLI's row says "checking…" until then, where it
asked the user to reopen the pane. The register's AI Brain entry above said "nothing a reload starts notifies again"; that
is now "notifies once, and starts nothing". The freshness half of the guard has no self-check of its own: it covers a read
that outlasts the 60-second freshness, which the fake CLI cannot produce without a clock seam, and without it such a read
would cost one more read per minute while the pane stays open, never a tight loop.

**The Readme says what the switch and the permission mode do (BACKLOG N-cli-runner-01).** AI Brain's paragraph says the
user's own Claude Code settings files are read unless the switch is on, and that every Claude Code model call names the
permission mode `default`; Remembrance's paragraph names its switch too.

#### feature/remembrance-next

The owner's three requests of 2026-10-27, for Remembrance 2.8.0 and host 1.7.1 (versions are the coordinator's, set at
publish). The bounded model calls this lane was allowed ran on synthetic transcripts only, through the module's own
runner and arguments; their outputs stay under the lane's TEMP and are not committed.

**The summary's prompt is a file the user keeps, not text in the pane (the owner's design: "I can point it at a txt file
and update the prompt on my own but new users have a nice built-in prompt already").** "Summary prompt" chooses "Use the
built-in prompt" (the default) or "Use my own prompt file", whose `.txt` path row is live only while it is chosen. The
file is read for every summary at the moment its prompt is built, so an edit reaches the next summary with no Apply. A
file that is missing, unreadable, empty or over 16,000 characters is not used: the summary goes out on the built-in
prompt, the Status line ends "(⚠ the built-in prompt was used: ...)", and the log says why in one word, never the path
or the text. An empty prompt is never sent. "Save the built-in prompt to a file…" writes the built-in one to a file the
user picks (a WinForms save dialog: the host offers modules an open dialog and no save one) and changes no setting.
What is not a defect: the text in a pane row was refused because the host's Text row is a single-line WPF TextBox whose
paste filter keeps only the first line (measured on .NET 10's WPF, `TextEditor._FilterText` with `AcceptsReturn` false);
a host multi-line kind would have raised the module's floor; this lane's first build, a module-owned editor window, was
replaced by the owner's design the same day. The follow-up's prompt is a file by the same mechanism, with no built-in
one, so with no usable file no follow-up runs, and that is said.

**What kept summaries brief, and what was lifted.** The built-in asks were "Summary (3-6 bullets), Decisions, Action items
(owner where named), Open questions", and the CLI's system prompt asked for "plain text", against the table an action
list wants. Both changed: the built-in prompt asks, generically, for an overview, a part per topic with its specifics,
who contributed what, decisions with their reasoning and the alternatives weighed, concerns and how they were answered,
a table of action items (Owner, Action, Details, Due, Follow up with) and open questions, and an ending never cut
mid-sentence; its first line is still "Summarize this meeting transcript.". A cloud provider that stopped an answer at
its own output limit (`finish_reason` "length") had the cut answer saved as a whole summary; it is now saved with a line
saying it ends early. Refused: discarding the cut answer, or asking again, which is a second call's cost for the same
limit. Not binding and left: the runner's 8 MB output read, Claude Code's own output ceiling (an error, not a cut), the
360 KB one-call transcript limit, the ten-minute bound, the cloud's 1 MB response bound; the local map-reduce's part and
merge sizes are a small local model's and its merge already says what it did not cover.

**The follow-up after the summary (the owner: "yes, build the ability to chain please").** Off by default, in a card of
its own after the CLI card, never greyed: it runs after a summary from any engine, and "Run the follow-up on the latest
summary" works with the switch off. Codex unless the user picks Claude Code. It sends the summary with its header left
off, and the transcript only when "The follow-up reads" says so. Its model, effort and Claude Code settings switch are the
ones the Coding-agent CLI card saves for that CLI; refused: four more model and effort rows in this card that would have
to be kept in step with the CLI card's. The answer is written to `<summary's name> - follow-up.md` beside the summary
through a temporary file, and nothing in the follow-up writes the summary's file, so a failed follow-up leaves the
summary and any earlier follow-up as they were. The latest summary is the newest of the one this session wrote and the
newest `.summary.txt` in the storage folder and the folders one level under it. A follow-up file is not one of the names
"Rename older recordings" moves, so one made for a recording still under its pre-2.6.0 name keeps that name when the
recording is renamed.

**The question card's height follows its content (host 1.7.1; the owner: "the prompt is too low, a refresh puts it in
the correct place").** Reproduced on the owner's box (one monitor, 2560x1440 at 100%, the taskbar at the bottom) under
`--trayquestions-selftest`: the pre-show measure was right (132 DIP measured, 131.84 laid out), but a question queued
behind the card brings in "+1 more waiting", the content grew to 150.5 DIP inside a window whose height was fixed before
it was shown, and the row of buttons ended 5.5 DIP past the card's bottom edge. The stack is now laid out at its own
height, the window takes that height whenever it changes, every change of the window's size places it again in the
tray's corner, and a work-area change (`WM_SETTINGCHANGE` with `SPI_SETWORKAREA`, or `WM_DISPLAYCHANGE`) places it again
too. The pre-show measure stays as the estimate the first placement uses.

#### feature/agentbroker-pin-automation

AgentBroker's vendor re-pins as data, a scheduled check that drafts them, and the first re-pin (lane broker-pins, owner
decision 2026-10-27: automation detects, checks and DRAFTS; the owner's review stays one merge, because for Claude the
pin is the only check that a new build still treats broker input as untrusted).

**One pin document, embedded in both adapter assemblies.** `tools/AgentBroker.Pins/agentbroker-pins.json` is an
embedded resource of `AgentBroker.ClaudeAdapter` and `AgentBroker.CodexDesktopAdapter`, and both compile the same
loader, `PinDocument.cs`, as internal types. Each adapter validates the whole document, so the two cannot disagree, and
an assembly that references both (the worker) sees neither copy. Refused alternatives: a third `AgentBroker.Pins`
assembly, which would add a payload file to the publish and packaging lists for no extra guarantee; and a loose file
beside the worker, which any process running as the user could edit. The pin is inside the payload the catalog hashes,
so a re-pin is a module update and nothing else.

**A refused document fails closed, and differently per vendor, on purpose.** Codex refuses with
`pin_document_invalid`, which `DesktopCompatibilityProfile.IsExactPinMismatch` does not list, so a broken document
cannot be mistaken for "an unreviewed build" and fall through to publisher evidence or update-tolerant delivery; the
cost is that Codex sessions are not even observed until a fixed module arrives. Claude keeps its publisher-signed,
read-only observation, because that path never consulted a pin, and no Claude build can be woken. Both say why in
the Native status on every pass.

**Each pin carries exactly what the adapters compared, and no more.** A Claude pin is a version and a hash, with no
field naming the client it was reviewed in (the compatibility document says which); adding one would be a member the
loader must validate and no check reads. The loader's own bindings (package name and VS Code folder derived from the
version, uppercase hex, one Claude pin per image hash) refuse documents the adapters could load but never match.

**UNPROVEN now means "a check could not run"; REVIEW and LIVE are their own states.** The helper used to mark the
person-only facts (Claude peer-token admission, the Codex idle-check order, the renderer-bridge bundle) and every live
acceptance check UNPROVEN. The brief's rule for the scheduled check, never a pull request for a build with any FAIL or
UNPROVEN check, would then have refused every build forever. So a fact only a person can establish by re-reading the
new build is REVIEW, and becomes the pull request's checklist; live acceptance is LIVE, after publish; UNPROVEN is kept
for an offline check that did not run here (Node.js absent, PowerShell 5.1 for the manifest hash, no published
integrity), and it does block the automated pull request. None of the three is ever PASS.

**The drafted Codex bundle set is a superset of the reviewed one, on purpose.** Marker holders alone drafted three of
the five bundles a person pinned for 26.1007.21434 (measured on its marketplace VSIX, 2026-10-27). Every marker holder
plus every `app-main-*` and `app-initial-*` bundle drafts six, all five reviewed ones among them. Hashing more bundles
can only refuse more builds; the REVIEW item still asks the reviewer which bundle owns the renderer bridge.

**The manifest hash is ported, not re-implemented.** The helper computes `VsCodeManifestProfile.ComputeSha256` by calling
the same System.Text.Json types under PowerShell 7 (the byte-span parse overload, the default writer, ordinal sort, the
root `__metadata` exclusion and the whole-tree duplicate check). Checked against a real reviewed value, not only
vectors: on 26.1007.21434's VSIX it reproduces the pinned hash. Refused alternative: a hidden adapter command-line flag
that prints the hash, which would make the helper build and run the adapter.

**"Newer than every pin" leads the status text, and keeps the existing code.** The pane's per-client Native line shows
the first 160 characters of its adapter's status (`AgentBrokerModule.NativeLine`), so the client's name, its version
and "a reviewed re-pin arrives as a module update" come first and end within them (asserted), and the verified list
follows for the full Native adapters field. The codes stay `unsupported_public_version`, `unsupported_desktop_version`
and so on, so nothing that keys on a code changes; raising the module's 160 was refused as a module change for a
worker-side sentence. It looks nothing up: the app makes no network call, and the comparison is reporting only.

**The scheduled check drafts and never decides.** Pull requests are opened as drafts on `agentbroker-repin/<build>`
(version plus the first 12 hex digits of the image hash), one per build: an npm CLI and a VS Code extension with the
same `claude.exe` are one build, because two pull requests adding one hash would make the second a repeat the loader
refuses. A build is skipped only while an open pull request has its branch, as the brief says, so a pull request the
owner closes without merging is drafted again by the next run; making a decline stick would mean also skipping
closed pull requests, which is the owner's call. The checkout keeps no token while downloaded packages are read
(`persist-credentials: false`), and only the pull request step authenticates git, through `gh auth setup-git`.

**The first re-pin pins Claude and holds Codex back, on the same evidence rule.** For each new Claude build, five
peer-classification and admission code sites compare token for token (minified names renamed) with a reviewed image,
so the REVIEW item has offline evidence. For Codex 26.1021.22027 the code around the reviewed markers changed from the
pinned 26.1007.21434, so its REVIEW items are open, and an exact pin would let the wake switch alone deliver to builds
that today need the separate `codexUpdateTolerantDelivery` opt-in, whose accepted gaps are exactly those items. Their
entries are drafted for a person's re-read instead.

#### feature/broker-batch-2

AgentBroker 1.5.0, from the owner's backlog decisions of 2026-10-29.

**Update-tolerant Codex delivery is the default (the owner's decision of 2026-10-29).** `codexUpdateTolerantDelivery`
defaults on, so once Codex delivery (`codexPeerMessages`, still off by default) is on, a publisher-verified updated
Codex build is woken through the same runtime checks as before, without waiting for a re-pin. The owner accepts by
default the gap `#### feature/agentbroker-backlog-b` names (what an unreviewed build does not re-prove: settings
inheritance, the double idle check, the busy error's meaning, unsigned extension JavaScript), which is what that
section's "why the switch is off by default" referred to. Turning the switch off is the strict mode, exact pins only.
The status line still names the mode and the failed pin on every pass. The worker's own default stays strict: the
module passes `--codex-update-tolerant-delivery true` while the switch is on, and a worker started without it (by hand)
admits exact pins only. An install that has saved the AgentBroker pane keeps the value it saved, since the pane saves
every switch; one that never saved it gets the new default. Claude is unchanged: it has no update-tolerant mode.

#### feature/broker-next-160

AgentBroker 1.6.0, the 2026-10-29 evening campaign's broker lane.

**A malformed signer subject reads differently for Claude and for Codex, on purpose (N-broker-b-02).** The WinVerifyTrust
call, the signer-subject parse and the held-path check now live once, in `tools/AgentBroker.Pins/WindowsAuthenticode.cs`,
compiled as linked source into the Codex desktop adapter and the worker (no shared project fits: AgentBroker.Core is not a
Windows target). The parse throws on malformed DER and each wrapper keeps the reading it had before the move: the Codex
check refuses it as `UnexpectedPublisher`, and the Claude check lets the exception reach `Observe`, which reports
`Unavailable`. Both refuse; neither grants anything. The difference is only which word a diagnostic shows, and unifying it
would change one adapter's observable behaviour in a refactor that promised none, so it is kept and pinned by a check in
each suite (`CodexObservationChecks.cs`, `ClaudeObservationChecks.cs`).

**The collaboration helper may send as the caller's own Codex row (N-agentbroker-helper-native-sender-01; the owner's
decision of 2026-10-29, design reviewed by Codex 01a1855c).** `Invoke-AgentBrokerCollaborationMcp.ps1` admits a native
sender only as `send`'s `sender`, equal to `codex:` plus the canonical lowercase GUID in `CODEX_THREAD_ID` (a missing,
empty, zero or non-canonical value refuses; nothing is repaired), and only after the helper itself finds, across every
`list_sessions` page, exactly one fresh `codex-native` row with that session ID, the request's broker ID and incarnation,
and `Receive` and `WakeIdle`. It is a routing convenience inside the existing same-user model, NOT per-thread
authentication: no trusted per-thread operating-system marker exists, and `CODEX_THREAD_ID` and `CODEX_SESSION_ID` are
self-asserted, so any process of the same user could claim any thread. That is the same reach such a process already has
through the broker's unauthenticated loopback endpoint. The helper cannot learn the local broker ID without registering,
so its "local" test is the directory's: the row names the broker ID the request names, and no other row anywhere has that
session ID (a paired broker advertising one makes it ambiguous, which refuses); the broker's own `sender_mismatch` and
`sender_unavailable` checks in `BrokerStore.Accept` still refuse a sender from another broker or one no longer
addressable when the send arrives. The ambiguity rule is also a veto, recorded so it is not mistaken for a safety
property alone: a paired broker sees this broker's directory, so it can learn `codex:<thread>` and advertise the same
session ID, and that chat's native sends through the helper then refuse `native_sender_ambiguous` for as long as it does
(locally a new incarnation displaces the old row, `BrokerStore.Register`, so only a peer can make the second row). It
denies only the convenience: the chat's generic mailbox still works, and unpairing ends it. Kept because counting only
the request's broker would let the scan stop proving that nothing else claims the ID (independent review of 1.6.0, P3-4).
The page cursor is built from the JSON values as sent (`ConvertFrom-McpJsonElement`), so a date-shaped session ID from a
peer cannot come back as a different cursor; 64 pages of 16 cover this broker and three full paired brokers, and a larger
federated directory refuses `native_sender_directory_limit` (P3-3). Refused alternatives: trusting `whoami` (a Codex caller always gets
`no_native_session`, because one Codex server hosts many chats), and rewriting a refused native send to the caller's
mailbox (a retry must keep its `messageId` and sender, or the broker answers `message_id_collision`). `wait_for_reply` and
`wait_for_receipt` take only a message ID and the broker waits as the original's sender, which would reach a native
session's wait past every address guard, so the helper reads the original with `get_message_receipt` first and refuses
unless its sender is a generic mailbox.

**A slow Codex owner lookup no longer fails the whole pass (N-agentbroker-native-flicker-01).** On 2026-10-29
at about 10:30, under heavy disk load, `send` to a live Codex chat answered `recipient_unavailable` and `list_sessions`
showed no `codex:` row at all while the chat kept working. Two things made one slow lookup cost every row. The runtime
ran its adapters one after another in one loop, so a slow pass in one postponed every other adapter's renewal; it now
runs one loop per adapter (`BrokerRuntime.DiscoverLoopAsync`). And `CodexNativeAdapter.DiscoverAsync` looked owners up
one conversation at a time, so a single lookup outliving the ten-second pass deadline (a pinned router may spend its
twelve-second discovery timeout, `DesktopProtocol.cs`) failed the whole pass and no Codex row was renewed. Now a lookup
that times out or fails with an IO error skips that conversation alone; past the pass deadline the pass returns what it
resolved; the slow conversation goes last on the next pass; and the status names how many were skipped
(`codex_owner_lookup_skipped`) on every pass that skipped one. The router's error answer for one conversation
(`owner_discovery_rejected`) skips that row the same way (independent review of 1.6.0, P3-5); every other
`DesktopAdapterException` still fails the pass: it is the router's or the profile's verdict, not one row. What this does
NOT achieve: after a timed-out lookup the provider drops the owner connection, so the next conversation's lookup
reconnects with full router verification inside what is left of the ten-second deadline, and under load the rest of
that pass is skipped too (N-broker-next-03). A skipped conversation keeps its earlier binding for as long as it keeps
being skipped, whatever its lease (harmless: `BrokerStore.Ready` claims nothing for a lapsed row, and submission
re-resolves the owner), and is not renewed. An exception outside the discovery loop's list no longer ends that
adapter's loop in silence: it is recorded as `adapter_failed` and the loop tries again (P3-7). Refused: renewing a skipped row on its old binding (it would
advertise an incarnation nobody verified this pass); a pass deadline longer than the router's discovery timeout (leases
are registered when a pass ends, so a longer pass delays every row it resolved); and keeping the owner connection after a
cancelled lookup (the adapter library closes the protocol on any failed request, because a late answer would be read as
the next request's, `DesktopAdapter.RequestAsync`), so the next lookup still reconnects.

**A Codex check the broker could not complete defers; a verdict still refuses (N-agentbroker-refusal-transience-01, half
2).** On 2026-10-29 at 10:22, under heavy disk load, a send to a live, busy Codex chat was Refused 17 seconds later with
`unsupported_capability`, decided before any turn frame; a busy chat should defer. Two broker-side states made the
provider answer `UnsupportedCapability` (or, in strict mode, `native_delivery_disabled` and
`native_client_profile_unverified`) for an owner that does advertise the native capability: the engine check failing
with an IO or access error, and the owner connection being down after a failed lookup (`NativeIdleGuard` reads the live
connection), which is what the slow lookups of N-agentbroker-native-flicker-01 leave behind. Neither says anything
about the client. `CodexDeliveryAuthority.VerificationUnavailable` names that state, and such a message defers at once
with `native_verification_unavailable`, WITHOUT connecting: the discovery loop owns the reconnect. (The first version of
this change reconnected inside the delivery, under the adapter's lock and with nothing able to cancel the router
verification; a discovery pass waiting on that lock outlived its deadline and every Codex row lapsed, the flicker above.
Independent review of 1.6.0, P2-1; TRANSIENT_DELIVERY_NEVER_HOLDS_THE_LOCK.) The provider's own fresh check at
submission that cannot complete defers with the same reason. A deferred message holds the same sender's later messages
to that recipient behind it (`BrokerStore.BeginDelivery`'s per-pair order) until it delivers or expires; today the next
discovery pass usually ends the state, but if N-broker-next-02 is closed so rows keep their capabilities, that
head-of-line wait could last the message's lifetime (24 hours by default) and wants a bound then. Verdicts stay refused: an
owner that does not advertise `supportsUntrustedAppInput` (`unsupported_capability`), a verified profile mismatch, a
publisher refusal. Not changed here: a discovery pass whose engine check could not complete still lists the row without
capabilities, and `BrokerStore.BeginDelivery` refuses an Accepted message to such a row as `recipient_cannot_receive`
(filed as N-broker-next-02).

**Half 1 (`no-client-found` after the start-turn frame) stays a final refusal for now.** A read-only audit of the
INSTALLED router builds (Codex VS Code extension 26.1021.22027 `out/extension.js`, the desktop app 26.1023.6123.0
`app.asar`) found one producer of `no-client-found` for a targeted request: the router's `findClientForRequest` catch,
reached when the target is unknown or its `client-discovery-request` probe answers `canHandle:false`, disconnects or times
out, all before any `request` frame is forwarded; after a forward, failures answer `client-disconnected` or
`request-timeout` instead. The probe does carry the whole request, turn input included, to the target, which only reads
its role from it. That would make the answer provably unsubmitted, but none of the PINNED builds (extension 26.1007.21434,
Code 1.143.x, desktop 26.1016.x) is installed on this workstation to confirm it, and delivery trust follows the pinned
build, so `DesktopAdapter.cs` keeps the refusal. Next step: read the same router function in a pinned image; if it
matches, defer it on the external-peer path as the discovery miss already does.

**N-broker-e-02's other half, `read_inbox` and `wait_for_reply`, is DECLINED-MEASURED.** 867801f moved only the
`wait_for_messages` pass to the bounded `AcceptedIncoming` query. A `read_inbox` page and each `wait_for_reply` pass still
build the participant's whole history through `Messages`, tombstones included: `messagesMs` in the coordination
benchmark's tombstone scenario at the 65,512-tombstone bound, each sample a fresh process, medians of three interleaved
runs of six: 24.18, 25.59 and 27.91 ms. `read_inbox` must list acknowledged history (that is its contract), so only
`wait_for_reply` could take a bounded query, and a reply wait wakes on store changes, not on a timer; at about 25 ms per
pass at a bound no install has reached, it was left as it is (independent review of 1.6.0, P3-9).

**The worker start reads about half as much, and no trust decision moved (O-agentbroker-02).** Two changes. The Code.exe
version resource is read before its 238 MB hash and only ever refuses: a version no pin names refuses at once (with that
version, as before), and a version a pin names must still match that pin's hash. The pin document is checked first, so
`pin_document_invalid` keeps its precedence. And the directory connection, which is observation only
(`DesktopAdapterOptions.ObservationOnlyConnection`, refused together with any delivery switch), takes the running engines'
publisher result the discovery pass established for the same engine processes (`VerifyRunningClientsByPublisherForDiscovery`)
instead of running Authenticode over every codex.exe again; the router image itself is still verified. The owner (delivery)
connection is unchanged: fresh router and engine checks, nothing reused. Measured on this workstation, each sample a fresh
isolated worker (own data directory and port, LAN and Claude discovery off, Codex discovery, delivery and update tolerance
on), three interleaved rounds per build, reads from the process's ReadTransferCount once they stopped growing for five
seconds: 1,168.7 MB per start before (each of three samples), 548.5 MB after (each of three), CPU 3.08 to 3.70 s before,
2.20 to 2.47 s after. Time to the end of the reads varied from 3 to 19 s in both builds under the other lanes' disk load,
so no time figure is claimed. Refused: reusing anything on the owner connection, which carries model input.

**Tombstones share their addresses and reason; the text hash stays a string (N-broker-e-01).** Measured with the
coordination benchmark's tombstone scenario (`tombstone 0 65512 0 512 0`: 65,512 tombstones, the bound), each sample a
fresh process, six interleaved repetitions per build, `storeHeapBytes` = the heap the open store keeps after a full
collection: 55,611,656 to 55,640,088 bytes before (about 849 per tombstone), 31,004,744 to 31,017,080 after (about 473),
a saving of about 24.6 MB at the bound; load time 1,154 ms against 1,196 ms (medians), inside the run's noise. Equal
participant addresses share one instance per participant (released with its last tombstone), and reasons share one
string (at most 64 distinct ones are kept). The journal format is unchanged. Not done: keeping the SHA-256 as bytes. It is
unique per message, so only a different in-memory type could shrink it (about 120 of the remaining bytes), and that type
would have to be converted on every journal write and on compaction, the store's durability path, for a further 7.9 MB
at most; declined on that trade.

**A session started while the broker was down is told how to reconnect, rather than the gap being closed
(N-agentbroker-client-reconnect-01).** Every companion restart stops the worker it supervises and starts a new one, and
on 2026-10-29 neither Claude Code (`ECONNREFUSED` on resume) nor a Codex chat in VS Code (`unknown MCP server`) recovered
its broker tools until the session was restarted or reconnected (N-agentbroker-client-reconnect-01's observation); no
client's retry policy was read for this. The worker's own share of the gap
is small: a fresh isolated worker accepted its first loopback connection 1,034 to 1,054 ms after its process started
(five samples, polled every 25 ms); the supervisor's stop (up to 4 s, then a forced stop) and the companion's own exit,
relaunch and module load come on top and were not measured without the real app. Since no length of gap is safe for a
session that starts inside it, the Client connection text now ends with one sentence: a session started while the broker
was down needs its MCP server reconnected (Claude Code: /mcp) or a restart. Keeping the worker serving across a
companion restart would change the supervisor's ownership model (a worker outliving its parent is what the parent-PID
watch prevents) and stays a backlog option.

#### feature/broker-161

AgentBroker 1.6.1, the 2026-10-29 evening campaign's follow-up broker lane.

**A Codex row whose engine check could not complete keeps Receive alone, for five minutes (N-broker-next-02).** Listed
without capabilities, such a row made `BrokerStore.BeginDelivery` refuse its Accepted messages `recipient_cannot_receive`:
a final refusal for the transience 1.6.0 defers in `SubmitAsync`. `CodexNativeAdapter.DiscoverAsync` now lists it with
`Receive` and nothing else, only when the provider's verdict for that binding is `VerificationUnavailable` (delivery on,
the owner advertising the native capability, no mismatch or publisher refusal found). `Ready` needs `WakeIdle`, so the
store neither claims nor refuses: the message waits, and the first pass that completes the check restores the full
capabilities and the message is claimed. Nothing unverified is advertised (no wake, no idle guard) and every delivery
still runs the fresh checks. The wait is bounded because a waiting message holds its sender's later messages to that row
behind it (the per-pair order), so an engine that can never be read would hold them for the message lifetime:
`CodexNativeAdapter.VerificationPendingBound`, five minutes of unbroken incomplete checks for that address, after which the
row is listed without capabilities and its queue refuses as 1.6.0 did. A pass that skipped the row keeps its start time,
so a skip never restarts the bound. Five minutes is a judgement, not a measurement: ten 30-second leases, long enough for
a disk-load stall of the kind seen on 2026-10-29, short enough that a sender learns of a check that never completes well
inside a working session. Refused: keeping the earlier pass's verified capabilities (it would advertise a wake nobody
verified this pass, and the row would be claimed every five seconds, two state writes each, only for `SubmitAsync` to
defer it); and a new capability flag for the state (`SessionCapabilities` travels to paired brokers as a number). Because a
generic mailbox is the other row with `Receive` and no `WakeIdle`, `send`'s mailbox advisory now also requires the row's
adapter to be `mcp`.

**A waiting message's receipt names the wait, derived when it is read (N-broker-next-01).** `BeginDelivery` skips a
message whose native recipient is not `Ready` without touching it, so a message to a busy Claude session, or to a Codex
chat whose directory reports it busy, waited Accepted with no reason. Only a Codex chat of Unknown activity (its native
idle guard makes it Ready) was claimed and came back `native_context_busy`, which is the live receipt of 2026-10-29.
`BrokerStore.FindReceipt` (so `get_message_receipt` and `wait_for_receipt`) now fills the reason of an Accepted message
that was never attempted from the recipient's present state: `recipient_busy`, `recipient_awaiting_approval`,
`recipient_interrupted`, `recipient_activity_unknown`, `recipient_wake_unavailable` (Receive without WakeIdle, the pending
Codex row above), or `recipient_unavailable` (its row lapsed or is gone). It is never stored: a delivery pass makes no
state write for it, it cannot go stale, nothing is claimed and the pick order is untouched. A message that was tried keeps
its deferral's own reason, and a present generic mailbox's receipt is unchanged (Accepted there already means unread, which
`send`'s advisory says). Not done: when the recipient was last Ready. The store does not track it, and carrying it would
need a field in the receipt, which is the journal's and the paired brokers' format; `UpdatedAt` still says when the message
was accepted or last deferred.

**A reconnect no longer costs every pass after it (N-broker-next-03).** Reproduced offline before any change
(`CODEX_RECONNECT_KEPT`, `RuntimeLifecycleChecks.cs`): the real `CodexNativeProvider` with a connect that takes longer than
the pass deadline and ignores cancellation, as the router verification does, then honours the token it was given, as the
initialize request after it does. Scaled down (400 ms pass deadline, 700 ms reconnect), 1.6.0 renewed the row on 0 of 4
passes and connected 4 times; with the change, 3 of 4 and once. The count is deterministic for the fake; no production
timing is claimed. The cause: the reconnect ran on the pass token, so one that outlasted the deadline failed at its
initialize request and was thrown away, and the next pass paid for it again; while a reconnect cost more than a pass, no
pass renewed a row. `ResolveOwnerAsync` now connects on its own budget (`OwnerReconnectBudget`, 30 s; the library's own
five-second timeouts still bound the pipe connect and the initialize request, and the verification between them takes no
token, as before), and keeps a connection no request has failed on when the pass ran out meanwhile. A lookup that fails
still discards its connection (the library closed its protocol), so with a conversation whose lookup times out on every
pass and a reconnect longer than the pass, the other rows are renewed on every other pass rather than never. No
verification is skipped or reused: every new connection is verified in full. Cost: a pass that is reconnecting at shutdown
now finishes the reconnect (bounded by those timeouts and the verification) instead of having its pipe connect or
initialize request cancelled.

## Known ABI gaps

Add the verb when the module that needs it is written — see `handoff.md`'s host contract. Neither of
these is scheduled, and neither is an oversight.

- 📌 **A module cannot draw on or near the companion.** No ABI for overlay/decoration. Nothing
  planned needs it yet; noted so it is not mistaken for an oversight if something does.

- ⬜ **A module cannot push a live value into an open tray menu.** Found while porting the standalone
  app's "Next blink" countdown, which refreshed every 250ms because that app owned its own menu. This
  entry said "an open options pane or tray menu" until 2026-10-26; the pane half has existed since host
  1.6.1. `OptionsPane.NotifyInfoChanged()` and its `InfoChanged` event (`src/DesktopAICompanion.Contracts/PluginApi.cs`)
  let a module say that a cached Info value changed: the host coalesces the notifications onto its UI
  thread and reloads only the pane's existing `SettingKind.Info` rows, keeping the editable controls,
  the unapplied edits and the layout. It is callable from any thread, inert while the pane is closed,
  and nothing polls; a changed schema still needs a pane reload. AgentBroker (`MinHostVersion` 1.6.1)
  is the module that uses it. The tray menu still has only a snapshot: `TrayItem.DynamicText`,
  `Visible` and `BuildChildren` are evaluated when the menu opens, and nothing updates the text of a
  menu that is already open. So a countdown can now live in a pane (a module timer calling
  `NotifyInfoChanged`, with `MinHostVersion` 1.6.1), while one in the tray menu still needs an ABI
  addition (a push channel for an open menu) and therefore a host release. Blinking LED's readouts
  were dropped rather than shipped stale, and nothing has brought them back.

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
today. **The next one filed is BUG-019**, and it is filed in [`../BACKLOG.md`](../BACKLOG.md).

| bug | | fixed |
|---|---|---|
| BUG-018 | AgentFlow's repeat guard was one counter reset by any different card, so a stuck card in one VS Code panel beside a working prompt in another was pressed three ticks in every four for as long as it stood, and never stayed stood down | agentflow 1.11.0 (feature/agentflow-enhancements): the count is per panel and card, and a sweep drops one only when it read the panel and the card was gone |
| BUG-017 | AgentFlow's auto-approve stood itself down on a run of edits to one file, every press having landed, because an Edit card shows only its path and the repeat guard told cards apart by their text | agentflow 1.9.1 (feature/agentflow-card-identity): the Claude reader tells cards apart by their element as well, and a card read before its buttons enable is not announced on that read |
| BUG-016 | the default companion ran a copy of its file saved in settings.json and never the file itself, so an Update, a Companion Studio install or an upgrade never reached it, from 1.0.0 to 1.5.0 | app 1.5.1 (feature/stale-pet-xml): the launch resolves the default from its companion file, the copy declares its source, and a 1.5.0 copy replaced on its header name is first kept in a side file |
| BUG-015 | the CLI card said "Claude Code is not installed" on a machine where Claude Code was installed, because the runner looked only on the PATH the app started with, in npm's prefix and in Claude Code's own installer folder | aibrain 1.3.1 and remembrance 2.1.1 (feature/cli-token-and-locate): the PATH saved now, WinGet's package folders and VS Code's copy are searched too |
| BUG-014 | one module description 25 characters over its bound made every installed app refuse the whole catalog, behind "Couldn't reach the catalog: Catalog contains an invalid module entry." | the catalog by 4d1e228 (2026-10-06); the host on feature/catalog-insight: an entry is refused alone and by name, the gate and the publish scripts run the app's own parser over catalog.json, and the panes word the failure by its case |
| BUG-013 | Remembrance's "Download that model" pulled the model last applied rather than the one picked on screen, answered only "Downloading <id> in the background", and the next Apply put back a selection the pull had written; every pane action that reads a setting the pane edits read the saved value | remembrance 2.0.0 (feature/remembrance-2, 2026-10-02): the actions act on what is on screen (`InvokeWithPendingAsync`, so `MinHostVersion` 1.2.5), the download waits up to 15 s and answers in the pane, an Apply keeps a write that landed in the background, and the pull's start and success are logged |
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
