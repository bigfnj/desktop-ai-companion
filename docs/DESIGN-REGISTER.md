# Design register — consult before proposing

**What this is.** Closed knowledge that exists to be READ, not done. Every entry here came out of
[`../BACKLOG.md`](../BACKLOG.md) because it is a register rather than a queue: a refused design kept
beside the reasoning that refused it, an ABI gap noted so it is not mistaken for an oversight, a
behaviour that reads like a defect until you know why it is not, and a bug log whose entries are all
closed.

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
| cite a bug number, or file a new one | [Known bugs (post-1.0.0)](#known-bugs-post-100) — the next number filed is BUG-005 |
| write a count into a document | [Numbers in documentation](#numbers-in-documentation) |

The completed-work record is [`HISTORY-post-1.0.0.md`](HISTORY-post-1.0.0.md); the post-mortems are
[`ISSUES-post-1.0.0.md`](ISSUES-post-1.0.0.md); what cannot be actioned from this machine is
[`BLOCKED.md`](BLOCKED.md).

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

---

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

Both of these were filed as bugs. Neither is one, and one of them records a fix that had to be
reverted.

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

**None open.** All four were fixed before the v1.1.0 tag (2026-09-10). The full post-mortems —
diagnosis, the wrong turns, the fix, and how each was verified — are in
[`ISSUES-post-1.0.0.md`](ISSUES-post-1.0.0.md). Bugs are numbered `BUG-00N` and the number is never
reused, so a commit, a test or a code comment can cite one; `modules/AiBrain/`,
`modules/PetStudio/`, `src/dotNet/`, `docs/RELEASE-CHECKLIST.md` and `handoff.md` all cite them
today. **The next one filed is BUG-005**, and it is filed in [`../BACKLOG.md`](../BACKLOG.md).

| bug | | fixed |
|---|---|---|
| BUG-001 | the tray icon is missing after an MSI install that launches the app | host 1.1.1 → 1.1.3, across all four of its symptoms |
| BUG-002 | the vision feature does nothing, silently, when the configured model is not installed | 2026-09-10, all four items |
| BUG-003 | screen capture returns the wallpaper, and follows the wrong monitor | (b) fixed 2026-09-10; (a) resolved — the suspected cause was refuted by measurement |
| BUG-004 | the leak soak's verdict was a coin flip, and it is the only gate that can catch a leak | 2026-09-10 — there is no leak, and the gate now measures that |

Three of the four were found by the maintainer running a real install, and none by a gate. That is
why [`../SMOKETEST.md`](../SMOKETEST.md) exists, and why the A-E walk is tracked as open work in
[`../BACKLOG.md`](../BACKLOG.md) rather than dropped.

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
