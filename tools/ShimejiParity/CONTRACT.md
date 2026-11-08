# Shimeji parity contract, version 1.2

Signed 2026-11-07 by Codex (proposal author) and Claude (host review). Version 1.1 replaces version 1, which was frozen
and signed: v1's text is `git show 2d9c6f5:tools/ShimejiParity/CONTRACT.md`. Every passage v1.1 adds or changes is
marked **(v1.1)**; unmarked text is v1's. The proposal this came from, `CONTRACT-v1.1-proposal.md`, is kept beside it,
marked superseded; the host review's eight edits are applied here.

**(v1.2)** Version 1.2 adds section 10, the trace schema `shimeji-parity-trace/2`, signed 2026-11-07 by Codex
(oracle and comparer) and Claude (host producer). Sections 1 to 9 are v1.1's, unchanged, apart from three pointers
to section 10 marked **(v1.2)**. A trace/1 pair is still judged under v1.1 exactly as before. The signed text,
`CONTRACT-v1.2-trace2-consolidated.md`, is kept beside this file unchanged, with its change table, host-code checks,
control matrix and open items; the v1.2 signature record at the end of this file says how section 10 differs from it.

Two runtimes run the same scenario and each writes a trace; the comparer decides whether they behaved the same.

- **Oracle:** the official Shimeji-EE 1.0.23 source, rebuilt on JDK 8 with a test-seam patch
  (`tools/ShimejiParity/oracle/`). The source is fetched and verified by script and never committed.
- **Host:** Desktop AI Companion running a converted pack in parity mode. The environment and clock seams are
  scenario-driven and the engine is stepped tick by tick.

This file is the contract both sides implement. A change to it bumps the schema version on every artefact it
touches. Field names are exact and case-sensitive.
**(v1.1)** Version 1.1 requires shimeji-parity-map/2, including the source pose channel and the optional sourceNames
channel. The source meaning of pose.index is unchanged. Explicit exceptions, each keeping its schema version. These
retain shimeji-parity-trace/1: the oracle-only footer extensions (sections 3 and 5); the birth-refused payload narrowed
to child null and reason cap or error, with toggle-off and missing-member no longer written; the tentative child's
provisional sid; and the input-event rules of section 3, including drag/start after a successful Dragged build. This
retains shimeji-parity-scenario/1, whose fields are unchanged: the inactive button-target rule of section 1. A trace or
verdict made before v1.1 is judged under v1 and is not re-judged under v1.1 without fresh traces.
Comparer result metadata in section 6 is not a trace or scenario field.

**(Screens clarification, 2026-11-08; adopted jointly by Codex, a7b3e049, and Claude, review of 2026-11-08)** The non-empty screen-list rule in section 1 is a
forward input-validation exception to the trace/1 grade freeze above and in 10.1. A newly requested comparison
with a supplied scenario rejects empty or non-list `screens` as `scenario-invalid`, including a trace/1 pair.
Archived verdicts and comparisons without a supplied scenario are not re-graded; valid non-empty lists keep
their existing behavior. This clarification retains scenario/1 and both trace schemas and changes no screen
element or rectangle rule. It does not reorder the CLI's existing source-pack preflight.

## 1. Scenario (`scenario.json`, schema `shimeji-parity-scenario/1`)

```json
{
  "schema": "shimeji-parity-scenario/1",
  "id": "S-breed-basic-01",
  "description": "one sentence a reader can check against the clip",
  "pack": "packs/breed-basic",
  "screens": [{ "x": 0, "y": 0, "w": 1280, "h": 720 }],
  "workArea": { "x": 0, "y": 0, "w": 1280, "h": 680 },
  "dpiScale": 1.0,
  "ticks": 600,
  "rng": { "kind": "none" },
  "settings": { "breeding": true, "transients": true, "transformation": true, "throwing": false,
                "sounds": false, "multiscreen": false },
  "typeSettings": { "Beta": { "breeding": false } },
  "mascots": [{ "sid": "m1", "imageSet": "Alpha", "behavior": "Stand", "anchor": [640, 680], "lookRight": false }],
  "activeWindow": [{ "tick": 0, "visible": false }],
  "input": [{ "tick": 0, "cursor": [100, 100] }]
}
```

- `pack` is relative to the scenario file. It is a Shimeji-EE layout (`conf/actions.xml`, `conf/behaviors.xml`,
  `img/<Set>/*.png`). The host side converts the same pack with the converter under test.
- **(Screens clarification)** `screens` must be a list of one or more screens; anything else is `scenario-invalid`.
- `ticks` is N, the number of manager steps of 40 ms. A trace has the startup snapshot at tick 0 and one snapshot
  after each step, ticks 1 to N.
- `settings` applies to every image set. `typeSettings` overrides it per image set, as the source keys its settings
  by image set; it is optional.
- `mascots` is the start state, applied BEFORE behaviour initialisation, so no unintended initial random selection
  runs. `sid` values are the stable ids every trace line uses.
- `activeWindow` and `input` are timelines. An entry takes effect at the START of its tick and holds until the next
  entry.
  - Window fields: `visible`, `rect` `[left, top, right, bottom]`, `title`.
  - Input fields: `cursor` `[x, y]`, `press` and `release` (`"left"` or `"right"`), `click` (shorthand for press
    and release in the same tick). A button entry names the companion whose window receives it, `"sid": "m1"`, as a
    window system would after its hit test (which window is under the pointer is the window system's business, not
    the runtime's); the point it carries is the cursor, made window-local against where that window was last drawn.
    While a press is held, every cursor change also reaches the holding companion as a move, before the tick's own
    edges. Only the left button reaches the runtime; the right one opens the menu.
  - A tick-0 entry sets state only (cursor position, window). A `press`, `release` or `click` at tick 0 is a
    scenario error: button edges start at tick 1, when the companions exist.
  - **(v1.1)** A left-button `press`, `release` or `click` entry naming a companion that is not active at dispatch
    (pending, removed or unknown) is a scenario error, classified scenario-invalid. Birth activation is after input
    dispatch, so a pending birth is not a valid target even on the tick at which it will activate; a disposing companion
    not yet removed is still active, and the edge does nothing. A runtime that meets one dispatches nothing more, writes
    no state lines for that tick and ends with footer status `aborted`. A right-button entry reaches neither runtime and
    is not checked.
- Neither runtime ever reads the real mouse, the real windows or the real displays during a scenario.

## 2. Random numbers

`rng.kind` is one of:

- `none`: the scenario is directed. Any draw is a failure, reported as `rng-unexpected-draw`.
- `tape`: `"values": [ ... ]`, doubles in [0, 1) consumed in order. Running past the end is a failure,
  `rng-tape-exhausted`. **(v1.2)** In trace/2 a draw past the end yields `0.5` and the run goes on; the
  footer's `tapeExhausted` records it (10.4, 10.5).
- `splitmix64`: `"seed": "0x0123456789abcdef"`. Each draw:
  `z = (state += 0x9E3779B97F4A7C15)`, `z = (z ^ (z >>> 30)) * 0xBF58476D1CE4E5B9`,
  `z = (z ^ (z >>> 27)) * 0x94D049BB133111EB`, `z = z ^ (z >>> 31)`, `value = (z >>> 11) * 2^-53`.
  All arithmetic is unsigned 64-bit and wraps.

Every consumer draws from the one stream: the oracle's Java `Math.random` call sites, its script-level
`Math.random`, and the host's equivalents. Every draw is logged (section 3, `rng-draw`) with:

- `occurrence`: the draw's 1-based ordinal over the whole run, all companions together.
- `site`: what drew it, named the same way by both runtimes. A built-in site is one of the table below. A script
  site is `script:<refKey>#<attribute>#<k>`: the element's `refKey` (section 3), the attribute holding the
  expression, and k, the 1-based ordinal of that `Math.random` call among the expression's direct calls, in source
  order. It is an ordinal, not a character offset: a runtime that records offsets converts them. **(v1.1)** Map/2
  does not yet let the host write this form (section 9).
- `purpose` is informational (what the draw was for, in the runtime's own words). It is written but never compared.

| site | what draws (source location, for the oracle) |
|---|---|
| `builtin:create-imageset` | a companion created without a named image set picks one (Main.java:1290) |
| `builtin:create-facing` | a created companion's initial facing (Main.java:1308) |
| `builtin:behavior-select` | choosing the next behaviour: ONE draw over the candidate list, even for a single candidate (Configuration.java:248) |
| `builtin:no-candidate-drop-x` | no behaviour is eligible: the companion drops in at a random x (Configuration.java:237/242) |
| `builtin:disabled-behavior-drop-x` | a behaviour entered by name is turned off: the companion it is built for drops in instead, which for a birth is the PARENT (Configuration.java:274/279; a name that does not exist throws instead) |
| `builtin:out-of-bounds-drop-x` | the companion left the screen: same drop-in (UserBehavior.java:261/266) |
| `builtin:dragged-regist` | Dragged's draw at age `timeToRegist - 1`: a value of 0.1 or more adds one tick to `timeToRegist`, so the drag goes on a tick longer; below 0.1 the regist time arrives (Dragged.java:104) |
| `builtin:regist-facing` | Regist's random facing (Regist.java:69) |

The screen and work-area variants of a drop-in are one site; the Multiscreen setting picks the range, not the site.
In a directed scenario a different draw COUNT is a failure, never something a tolerance absorbs.

## 3. Trace (`trace.jsonl`, schema `shimeji-parity-trace/1`)

One JSON object per line.

**Line 1, the header:**

```json
{ "type": "header", "schema": "shimeji-parity-trace/1", "runtime": "oracle",
  "scenario": { "id": "S-breed-basic-01", "sha256": "...", "packSha256": "..." },
  "build": { "sourceZipSha256": "...", "patchSha256": "...", "jdk": "1.8.0_504", "deps": { "jna.jar": "sha256" },
             "artifactSha256": "..." },
  "rng": { "kind": "none" }, "tickMs": 40, "dpiScale": 1.0, "runId": "uuid", "startedUtc": "..." }
```

- `runtime` is `oracle` or `host`.
- `scenario.sha256` is the SHA-256 of the scenario file's bytes. `scenario.packSha256` binds the pack's content:
  list every file under the pack directory as `<sha256 of its bytes>  <relative path with forward slashes>`, one per
  line ending in `\n`, sorted by the paths' UTF-8 BYTES, lexicographically (not by UTF-16 code units, which order
  supplementary characters differently); `packSha256` is the SHA-256 of that text in UTF-8. Before reading any file,
  the pack is preflighted: a link, junction or other reparse point anywhere in it, the pack directory included, is
  refused (`pack-invalid`) and nothing behind it is opened. Only a link can lead a pack path outside the pack.
- The oracle's `build` requires `sourceZipSha256`, `patchSha256`, `jdk`, `deps` and `artifactSha256`. The host's
  requires `commit`, `exeSha256`, `engineSha256` and `converterSha256`. `converterSha256` binds the converter's
  CODE: the SHA-256 of a manifest, in the pack-manifest form above, over its managed assemblies (or its script), never
  an apphost, whose bytes do not change with the code. Every `...Sha256` value, `deps` values included, is 64
  lowercase hex characters, and `startedUtc` and `runId` are required. A missing or malformed witness is
  `build-witness-missing`.

**State lines.** Tick 0 is the startup snapshot; then one snapshot after each step. Within a tick the lines list the
active companions in manager order, then the pending births in add-queue order.

```json
{ "type": "state", "tick": 12, "sid": "m1", "imageSet": "Alpha", "behavior": "Walk", "action": "Walk",
  "actionClass": "Move", "actionStack": ["Alpha|conf/behaviors.xml|/Mascot[1]/BehaviorList[1]/Behavior[3]",
  "Alpha|conf/actions.xml|/Mascot[1]/ActionList[1]/Action[2]"],
  "refKey": "Alpha|conf/actions.xml|/Mascot[1]/ActionList[1]/Action[2]", "actionAge": 7,
  "pose": { "image": "img/Alpha/shime2.png", "imageRight": null, "index": 1 }, "poseAnchor": [32, 128], "visible": true,
  "anchor": [622, 680], "origin": [590, 552], "lookRight": false, "velocity": [-2, 0],
  "affordances": [], "target": null, "dragging": false, "pending": false, "disposing": false, "vars": {} }
```

| field | meaning | may be null |
|---|---|---|
| `behavior` | the current behaviour's name | yes, before one is chosen |
| `action` | the leaf action's name | yes, before evaluation or for an unnamed action |
| `actionClass` | `Sequence`, `Select`, `Stay`, `Move`, `Animate`, or the short class name of an `Embedded` action; known for an unnamed action too | yes, before evaluation |
| `actionStack` | refKeys, outermost first: the `Behavior` or `BehaviorReference` element the behaviour came from, then its root action's declaration, then at each depth the active child's `ActionReference` or nested `Action`. An `ActionReference` entry stands for that activation; its target declaration is not added after it. `[]` when there is no behaviour | no |
| `refKey` | the declaration or reference that started the leaf action; equals the last `actionStack` entry | yes, when the stack is empty |
| `actionAge` | the leaf action's time in ticks | yes, before evaluation |
| `pose` | the image the companion shows, taken from the stored APPLIED pose, never from the current image set: `image` and `imageRight` are pack-relative paths with forward slashes (`img/Egg/shime3.png`), so they name the member the image belongs to, which after a Transform is the OLD member's until the new type's first pose is applied; `index` is the pose's position in its animation. A pose without an image has `image` and `imageRight` null and its `index` | yes, before any pose |
| `poseAnchor` | the offset from `origin` to the anchor the image is drawn with, after facing and `dpiScale`: its EFFECTIVE anchor. An image pair the source has already loaded keeps the anchor of the pose that loaded it first (the pair is cached by its paths, `ImagePairLoader.java:30-38`), so a later pose's authored anchor is not it. Null while the applied pose has no image | yes, with `pose`, and while the applied pose has no image |
| `visible` | false while an image-less pose hides the companion, and once it is disposing | no |
| `anchor` | the contact point in screen pixels | no |
| `origin` | the companion's bounds top-left in screen pixels, as `Mascot.getBounds` gives it: with an image, `anchor` minus `poseAnchor`; while the applied pose has no image, the bounds the window last had, which do not follow `anchor` while the companion is hidden (`Mascot.java:714-729`) | yes, with `pose` |
| `velocity` | the committed anchor change over this step: this tick's `anchor` minus the previous tick's; `[0, 0]` on a companion's first line | no |
| `affordances` | the companion's STORED offer list after the step, in order. An action's step replaces the list when that step runs (`ActionBase.next`); initialisation does not, so a companion whose action did not step this tick (paused, or between behaviours) keeps the list it had | no |
| `target` | the chosen scan target's sid, set where a scan runs and cleared when an action initialises | yes |
| `pending` | a birth that was requested and not yet activated; a pending companion never steps | no |
| `disposing` | disposal was requested: the companion is hidden at once (`visible` false), its offers are cleared, and it skips its own tick and apply from then on. It leaves at the next QUEUE DRAIN (section 4, step 3): a request made during step 4 is removed at step 3 of the next tick, one made before step 3 (by step-1 input) in the same tick. Its own `count` and `totalCount` read 0 at once; peers still count it until it leaves | no |
| `vars` | stored scalar per-instance variables only | no |

Every field is present on every state line, null or not. A pending companion's action and pose fields hold what
initialisation stored, which may be null.

**refKey** names one element of the pack's configuration, the same way in both runtimes:
`<imageSet>|<configuration file, relative to the pack, forward slashes>|<element path>`. The element path runs from
the document root, every step as `LocalName[i]` with i the 1-based index among same-named siblings, local names as
written in the file. A reference's key is the `ActionReference` element's, so two references to one action are told
apart. The converter preserves these keys through lowering (section 9).

**Event lines** come at the point they happen. Startup events carry tick 0; events during a step carry that step's
tick. `{ "type": "event", "tick": 12, "sid": "m1", "kind": "...", ... }` with these kinds and payloads:

| kind | payload |
|---|---|
| `behavior` | `from`, `to`, `cause` (`select` / `next` / `forced` / `partner` / `fallback`, see below) |
| `transform` | `fromImageSet`, `toImageSet`, `behavior` |
| `birth-request` | `child` (sid), `imageSet`, `behavior`, `anchor`, `lookRight`, `ordinal`, `attempt` |
| `birth-activate` | `child` |
| `birth-refused` | **(v1.1)** `child` (always null), `attempt`, `reason` (`cap` / `error`) |
| `scan-target` | `affordance`, `target` (sid or null) |
| `interact` | `with`: the OTHER active companions on this one's anchor, in manager order (once per Interact step, at the start of its tick) |
| `dispose-request` | `reason` |
| `dispose` | none |
| `window-move` | `from` and `to`, each `[left, top, right, bottom]` as the scenario writes a window |
| `window-minimize` | none |
| `sound` | `clip`, `op` (`start` / `stop`) |
| `mute` | `clip` or null |
| `rng-draw` | `site`, `occurrence`, `value`, `purpose` |
| `drag` | `op` (`start` / `end`) |
| `hotspot` | `op` (`press` / `release`), `behavior` |
| `error` | `message` |

**(v1.1)** Input events are emitted at dispatch before the behavior entry they cause. A left press emits hotspot/press
for the first hotspot of the current variant, in order, that contains the window-local point and whose behavior exists
and is enabled (both looked up in the companion's current image set; the press then builds the behaviour from the
configuration of the behaviour that holds the hotspot). Its behavior is that name, never null; a hotspot with no behavior, or a disabled one, does not take
the press. Otherwise, if the leaf action permits dragging, the press builds Dragged by name without the enabled
check and emits drag/start after the build succeeds and before entering it. A press whose Dragged cannot be built
writes no drag/start. Releasing a held hotspot emits hotspot/release with behavior null. Releasing a dragging
companion emits drag/end, then builds Thrown by name without the enabled check and enters it; drag/end is written even
when Thrown cannot be built. When one
release does both, hotspot/release precedes drag/end. A press and release in one tick, before a Dragged step sets
the dragging flag, emits no drag/end. Pointer moves alone emit neither event.

A behaviour's `cause` follows the entry that was CHOSEN: `select` when it came from the root list, `next` when it came
from the ending behaviour's next list (even if a root behaviour shares its name); `forced` when it was entered by name
(a scenario start, a birth, a Transform, a hotspot, lost ground, leaving the screen, a seeker's own transition);
`partner` for a scan target moved by its seeker; `fallback` only for the zero-weight and disabled-by-name drop-ins.
`scan-target` is written at every scan, with a null target when no one offers (ScanInteract searches every tick it
has no target). Action changes have no event of their own in v1: they show in the state lines, and a transient
instant action that left no observable effect is not counted as covered by them.

**(v1.1)** Birth lineage: attempt counts a parent's birth attempts from 1, refused or not. Ordinal counts only successful
births from 1; a successful child's sid is `<parentSid>.b<ordinal>`. A refusal has child null, including an error
after a tentative child was allocated. Events that tentative child writes before it fails (its behavior entry,
any rng-draw) carry provisional sid `<parentSid>.b<ordinal + 1>`, which the parent's next successful birth reuses.
The attempt counter advances, the successful-birth ordinal does not; no birth-activate is written for the failed
child. Reason cap is the host policy refusal before allocation; reason error is failure before the child joins
the add queue. On the host's cap refusal, BornX, BornY and BornBehavior are not evaluated for that attempt;
the oracle has no host birth cap and may proceed normally. Turning off
the applicable Breeding or Transients setting prevents the attempt and emits no birth-refused event. An unavailable
BornMascot member falls back to the parent's type instead of a missing-member refusal. Transform keeps the sid.
Process ids and window handles never appear.

**Last line, the footer:**
`{ "type": "footer", "ticks": 600, "status": "complete", "draws": 0, "states": 601, "events": 12 }`.
`ticks` is N; `draws`, `states` and `events` count the trace's `rng-draw` events, state lines and event lines. The
status is `complete`, `timeout`, `aborted` or `error`. A trace with no footer is `truncated`.

**(v1.1)** Oracle-only trace/1 footer extensions: every oracle trace carries eventSequenceSha256. A trace-off observation
run also carries unobservedDraws and unobservedEvents, the actual totals withheld from its ordinary event lines.
tapeExhausted is an optional boolean, true when the tape was exhausted. These optional extensions retain trace/1;
ordinary host footers need none of them. Unknown footer extension fields are ignored by cross-runtime comparison.
eventSequenceSha256 is required for the oracle's observation proof, with the definition and scope in section 5.
**(v1.2)** Trace/2 makes `tapeExhausted` a required footer boolean on both runtimes, adds a typed `error` object to
every `error` event, and makes a trace-off artifact proof-only (section 10).

## 4. Update order, both runtimes

Startup (tick 0):

1. The environment applies the tick-0 timeline entries.
2. Each `mascots` entry is created in order, its start state applied, then its behaviour initialised. Every entry is
   initialised before any is added, so counts during initialisation are 0.
3. The start companions are then added to the manager in creation order WITHOUT a step: no environment, time, action
   or render advances, and no birth event is written. This is the test driver's normalisation of the source's queued
   `Main.createMascot` (which would leave them pending until the first manager tick); both runtimes apply it, so start
   companions are listed active, `pending: false`, at tick 0.
4. Tick-0 state lines are written.

Each step t = 1 to N, in this order:

1. The environment applies tick t's scenario entries (cursor, presses, active window).
2. Births requested since the last drain are activated.
3. Disposals requested since the last drain are removed (the queue drain): those requested during the previous tick's
   step 4, and any requested by this tick's step-1 input.
4. Every active companion steps, in stable manager order. Peers update sequentially, so a later companion sees an
   earlier one's new offer and anchor in the same tick. Inside a step the source's own check order and cache
   boundaries hold (grimoire/06): there is no added once-per-tick rule.
5. Every companion applies, which means it renders.
6. State lines for tick t are written.

A birth requested during step 4 has a `pending: true` line at that tick and is activated at step 2 of the next.

## 5. Observation without side effects

Tracing never evaluates a lazy variable, a script or a condition in order to observe it, and never reruns one.
Values come from the real evaluation sites, or from state already stored. Turning tracing on must not change
behaviour; each runtime proves this once per build with a trace-on/trace-off pair over a directed scenario.

**(v1.1)** eventSequenceSha256 is one running SHA-256, rendered as lowercase hexadecimal, over the concatenation in final
output order of every event record's UTF-8 serialized line bytes, each followed by exactly one LF byte, with no
BOM. The trace-off run uses the same serialization and flush order for stored event records, without writing
the event lines. Empty event sequences hash the empty byte string. Hashing reads stored values only. The on-run
digest must also equal a recomputation from the event lines it actually wrote. The proof compares final states,
draw/event totals and this digest only between one runtime's trace-on and trace-off runs of one build. It never
compares an oracle digest with a host digest: serialization number spelling and key order can differ.
A host proof uses the same digest over the host's own serialization: its trace-off run forms the event records and
withholds the lines, as the oracle's does, and the digest need not appear in an ordinary host footer.

## 6. Comparison

A trace is checked before anything is compared, and a trace that fails a check is unjudgeable:

- The header, footer and every line carry their required fields (`missing-field`), and the build witness is
  complete and well-formed (`build-witness-missing`).
- `malformed`: two state lines for one tick and sid; ticks out of order or outside 0..N; footer counts that do not
  match the lines; no state at tick 0; a companion whose lines have a gap, or that stops before tick N without a
  `dispose` event; a `velocity` that is not the anchor change; a `refKey` that is not the last `actionStack` entry;
  `rng-draw` occurrences out of sequence. **(v1.1 clarification)** A state line for a sid at or after that sid's own `dispose` tick is `malformed`.
- `rng-invalid`: a recorded draw value outside [0, 1), or, in a `tape` scenario, a recorded value that is not the
  tape's value at that occurrence. A trace that did not take the scenario's numbers as declared cannot be compared, so
  this is unjudgeable. (Drawing at all in a `none` scenario, or past the tape's end, is the runtime's BEHAVIOUR and is
  judged as `rng-unexpected-draw` / `rng-tape-exhausted`, section 2.)
- `runtime-mismatch`: the oracle trace is not labelled `oracle`, or the host trace `host`.
- `scenario-invalid`: the scenario breaks section 1 (a tick-0 button edge, a missing field, a start companion without
  a `behavior`: a directed start state names its behaviour, so no unplanned selection draw runs; **(v1.1)** or, judged
  from either trace with the scenario supplied, a left-button edge at tick t whose sid has no `pending: false` state
  line at tick t-1). This class is decided before the run-failure classes, so a trace aborted by that refusal is
  scenario-invalid, not `aborted`.
- `pack-invalid`: the pack preflight refused a link or a path outside the pack (section 3).
- `scenario-hash-mismatch` / `pack-hash-mismatch`: the traces disagree on the scenario or pack hash, or, when the
  comparer is given the scenario, either trace disagrees with the file or the pack on disk.
- `artifact-hash-mismatch`: the comparer was given the oracle JAR or the host executable and its hash is not the
  witness's.
- `tick-count-mismatch`: the traces ran a different number of steps, or not the scenario's.
- **(v1.1)** `source-pin-mismatch`: the oracle build's sourceZipSha256 is not the repository's shared source archive pin.
- **(v1.1)** `scenario-start-mismatch`: after scenario/pack/rng/tick binding succeeds, a trace's active tick-0 sids differ from
  the supplied scenario's mascots in creation order, or any starter is pending. Only checked with a supplied scenario.

Then:

- **Directed scenarios** (`rng.kind` is `none` or `tape`):
  - Event sequences match exactly on tick, sid, kind and every payload field, `rng-draw` site included.
  - Each tick lists the same companions in the same order (`entity-mismatch`).
  - State lines match exactly on every field except `anchor`, `origin` and `velocity`. Velocity is checked against
    the anchor inside each trace, so comparing anchors compares it.
  - `anchor` matches exactly unless the scenario declares a named rounding boundary.
  - `origin` and `poseAnchor` are each compared. Both runtimes draw the source's image unpadded from its top-left
    (per-pose geometry), so the drawn image's top-left and its effective anchor both match exactly. A named `origin`
    rounding boundary, when the scenario declares one, covers `origin` only. Their sum is not compared: it would
    pass an image drawn somewhere else whose anchor offset moved the other way by the same amount.
- **Free-running scenarios** (`splitmix64`) make a separate, weaker claim: behaviour time-share, with the tolerance
  and both draw counts printed beside the result. Their verdict is `time-share` pass or fail, never `exact`, whatever
  the state lines show.
- **Verdicts:** `exact`, **(v1.1)** `within-tolerance:<name>[,<name>...]`, `time-share-pass` (free-running only), `policy:<id>`, `approximate`, `unsupported`,
  `not-exercised`, `pending` (the host primitive does not exist yet), `fail`, `unjudgeable`.
- **Behaviour failures:** `missing-event`, `extra-event`, `event-mismatch`, `entity-mismatch`, `state-mismatch`,
  `rng-unexpected-draw`, `rng-tape-exhausted`, `rng-draw-count`, `time-share`. **Run failures:** `truncated`,
  `timeout`, `aborted`.
- **(v1.2)** Trace/2 adds three unjudgeable classes, each with CLI exit 2: `trace-version-mismatch`,
  `trace-off-not-comparable` and `error-unclassified`. Section 10 gives their rules and the trace/2 precedence
  (10.7).
- Every failure names the scenario, the first mismatching tick, the sid and the field. One tolerance never covers
  an unrelated field.

**(v1.1)** Comparer report metadata: buildWitness has oracle and host objects, each with artifactBound (boolean) and
verification (artifact-bound or shape-only). Artifact-bound means a supplied artifact's bytes matched the trace
header hash; it does not by itself prove its build provenance. shape-only means the header shape was validated
without supplied artifact bytes. tolerancesUsed lists all used tolerance names, sorted and unique. A successful
within-tolerance verdict joins those names with commas in the same order. No tolerance absorbs another field.

## 7. Policy divergences (expected; each carries its own verdict, never `exact`)

| id | divergence |
|---|---|
| `policy:window-off-plays` | **(v1.1)** With the host-only WindowActions off, the base animation/action plays and no window effect occurs. The source has no such switch and moves or minimizes the window. Both runtimes retain the source Throwing setting, which ends the action when off; the real host pins Throwing on. |
| `policy:birth-cap` | Births count toward the host's 16-companion cap. At the cap, a `birth-refused` event with reason `cap` is written. |
| `policy:session-only-births` | Bred companions are not saved into the user's companion mix. |
| `policy:scaninteract-precedence` | ScanInteract honours Condition and Duration. The source's operator-precedence slip lets animation time bypass them. |
| `policy:breed-parent-reposition` | A disabled born behaviour does not move the PARENT, as the source's fallback does. |
| `policy:complexmove-guarded` | ComplexMove's arrival is guarded when scanning is off. The source dereferences a null target. |
| `policy:startup-after-arrival` | **(v1.1)** The host defers configured startup behavior until source-style arrival ends, retries after an interruption until it ends of itself, and cancels it on Transform or disable. The source has no host pack-startup behavior. No parity run reaches this policy: only TickHost.SpawnLikeSource configures it. |
| `policy:animate-no-variant` | **(v1.1)** An Animate-based host action with no eligible variant ends without its end effect; the source faults while dereferencing the missing animation. TickAnimate.HasNext defines the host behavior. |

## 8. Evidence

A scenario id (`S-...`) is the public evidence id cited by `tools/ShimejiConvert/ACTIONS.md`. Synthetic packs and
scenarios live in `tools/ShimejiParity/scenarios/`. Traces, clips and corpus scenarios stay outside the repository.

## 9. The host's map back to the source

The host runs a converted pet, so it names source things through a parity map the converter writes beside the pet
when asked to (a test artefact, never shipped and never read outside parity mode). **(v1.1)** A host parity run
reads shimeji-parity-map/2 only; map/1 is refused before the run with a regenerate message. A map/2 object has these
top-level properties: `schema` (`shimeji-parity-map/2`), `imageSet`, `frames` (an array of
`{frame, image, anchor, size}`), `actions` (`{<nodeId>: refKey}`), `children` (`{<nodeId>:<childIndex>: refKey}`),
`behaviours` (`{<nodeId>: refKey}`), optional `sourceNames` (`{<nodeId>: name or null}`), and `poses`, which must be an
object:

```json
{ "poses": { "2:0:0": { "refKey": "Alpha|conf/actions.xml|/Mascot[1]/ActionList[1]/Action[1]/Animation[1]/Pose[2]", "index": 1 } } }
```

The map contains:

- every frame index: the source image key it came from (`image`, and `imageRight` when it is a right-facing
  alternate), with the anchor and size the source draws that image with: the EFFECTIVE anchor of section 3's
  `poseAnchor` (the first-loaded pose's, for a cached pair). A pose whose geometry disagrees with its frame's map
  entry is refused before anything is traced. A pose's authored anchor, where it differs, stays in the converter's
  source IR, not in the map;
- **(v1.1)** every emitted pose except the exact decision-68 placeholder: its source Pose refKey and zero-based source index.
  The poses key is actionNodeId:emittedVariantIndex:emittedPoseIndex, each canonical nonnegative decimal within
  Int32 range, matching ^(0|[1-9][0-9]*):(0|[1-9][0-9]*):(0|[1-9][0-9]*)$. Positions refer to final emitted order
  after omissions. Duration-0 source poses still consume their source index; Hotspot children do not.
  Every other emitted pose needs exactly one mapping, including image-less poses, false-condition variants,
  and variants of instant actions the host never plays. A mapping for an unplayed emitted pose is required,
  not surplus. A missing mapping is permitted only for the decision-68 placeholder shape: one pose (frame -1,
  ticks 1, dx 0, dy 0) of the sole variant, whose emitted `when` attribute is exactly the text `false`, of a node with type `Animate`, or
  type `Embedded` and class `Interact`.
  The converter omits the mapping for its synthetic placeholder. If that shape instead has a supplied source
  mapping, validate it normally; an authored false variant can have the same shape. A false condition alone is no exemption:
  MoveWithTurn can apply its last variant while turning without checking that condition.
  Duplicate means a repeated poses property key. Surplus means a key names no emitted action node, variant or
  pose. Malformed means a noncanonical/out-of-range key, or a value other than
  an object with exactly the unique properties refKey (non-empty string) and index (nonnegative JSON-integer
  token within Int32 range). The refKey has exactly three pipe-separated parts; its first part equals imageSet.
  Its terminal source path step is Name[k], where Name retains the source vocabulary and k is a canonical positive
  integer within Int32 range; index equals k-1. Original-element existence is a separate original-pack check.
  Missing, duplicate, surplus or malformed mappings refuse the map before the run. The loader binds by the
  emitted pose object's identity. After Transform, an applied pose is resolved through the map of the member
  whose configuration defines it (the type that built the action playing it), never the companion's current
  image set. A missing binding during tracing is a provenance fault; never fall back to emitted indices;
- every action node and every edge that carries parameter bindings: the refKey of the declaration or reference it
  was lowered from;
- **(v1.1 clarification)** no script-site table yet. The host cannot name a script draw in section 2's form from map/2,
  because conversion merges and moves script expressions. This clarification was approved by Codex and Claude on
  2026-11-07. Until a later map revision adds a table from the host's (node, attribute, ordinal) to the source site,
  unresolved attribution prevents grading the remaining directed parity claim; attribution-dependent site/event
  matching therefore yields pending in an otherwise valid comparison. The schema, input, artifact-binding and
  run-status checks of section 6 apply first. Independent RNG failures retain their
  declared classes: a draw with `rng.kind` set to `none` is `rng-unexpected-draw`, tape exhaustion is `rng-tape-exhausted`,
  and a draw-count disagreement is `rng-draw-count`. After those checks succeed, a comparison in which either trace
  has an rng-draw whose site starts `script:` has verdict `pending` with class `script-site-unmapped`.
  The current oracle's `V1_RNG_SCRIPT_SITE_UNATTRIBUTED` refusal occurs before a draw record and remains
  `unjudgeable` with class `aborted` until an attribution channel exists; it is not converted to `pending`.
- (v1.1) Every behaviour and action node whose emitted name differs from its source-observable name: that source name, keyed by its node id, as `actions` and `behaviours` are (each map is one member's). Preserve an absent Name as JSON null and an explicitly empty Name as the empty string. A present null or empty value is distinct from a missing map entry.

The map is what lets the host write `pose.image`, **(v1.1)** `pose.index`, `poseAnchor`, `refKey`, `actionStack` and (v1.1)
state `behavior` and `action` and every name-bearing event payload whose name conversion changed in the oracle's
terms. A host trace written without one is unjudgeable (`missing-field`).

## 10. Trace schema `shimeji-parity-trace/2` (v1.2)

### 10.1 What changes

Trace/2 is trace/1 (section 3, with the v1.1 rules) plus exactly these changes:

1. the header declares `"schema": "shimeji-parity-trace/2"`;
2. every `error` event carries a typed `error` object beside its `message` (10.3);
3. `message` on an `error` event is validated but no longer compared (10.3.5);
4. every footer carries a required boolean `tapeExhausted`, and drawing past a tape's end is no longer a run failure
   on either side (10.5);
5. a trace/2 trace-off artifact is proof-only: cross-runtime comparison refuses it (10.5.3);
6. three unjudgeable classes are added, each with CLI exit 2: `trace-version-mismatch` (10.2),
   `trace-off-not-comparable` (10.5.3) and `error-unclassified` (10.3.5);
7. startup faults and an interrupted startup snapshot are judged by 10.6.

Everything else in sections 1 to 9 applies to trace/2 unchanged. A trace/1 pair is judged under v1.1 exactly as
before, including the exact comparison of error-event messages; it is never re-judged under v1.2 without fresh
traces. Both runtimes regenerate their traces to obtain a trace/2 verdict.

### 10.2 Version declaration, mixed pairs and dispatch

- The header's `schema` is the only version declaration. No other header field changes.
- A comparer recognises `shimeji-parity-trace/1` and `shimeji-parity-trace/2`. Any other value, or a first line
  that is not a header, stays `missing-field`.
- Once both headers have been read and both schemas recognised, a pair whose schemas differ is `unjudgeable`
  with class `trace-version-mismatch`. This is decided before any other check of either trace (build witness,
  body validation, binding, run status), because the two versions are validated by different rules and a mixed
  pair can never be compared. The CLI exit is 2.
- Each trace is then validated by the rules of its own declared version.
- The new header/schema preflight dispatches recognized trace/1 pairs to their v1.1 validation and comparison rules.
  Trace/2 additions do not reorder the old pair's message, input or failure semantics. Recognized mixed versions are
  refused before version-specific body grading.

### 10.3 The `error` event

#### 10.3.1 Shape and wire types

`{ "type": "event", "tick": t, "sid": s, "kind": "error", "message": "...", "error": { "code": "...",
"lookup": { ... } | null, "via": [ ... ] } }`

- `tick`, `sid` and `kind` keep their section 3 meaning. The sid is the companion the runtime reports the fault
  on: for a failed birth, the PARENT; for a fault a tentative child raises before it fails, its provisional sid
  (section 3, v1.1 birth lineage).
- `message` is written in the runtime's own words.
- `error` is required, an object with exactly the properties `code`, `lookup` and `via`.

Each error object has exactly one occurrence of each property code, lookup and via, with no other properties. code is
a string in the closed table. Each typed lookup has exactly one occurrence of each property in that code's operand set
and no other properties. Wrong types, unknown codes/labels, duplicate properties and wrong property sets are
missing-field.

imageSet is a non-empty string. behavior and action are string or JSON null; empty, whitespace and null remain
distinct. Capture those names before null-to-empty normalization or any name rewriting. refKey is a non-empty valid
source refKey in section 3's format. parameter is a non-empty string with the meaning below. via is an array of strings
from the closed wrapper vocabulary, in outermost-first order; an empty array is valid. A producer adds only wrapper
context actually carried by this fault, never context reconstructed from a later state.

message is a required non-empty string but is not used as a classifier or compared as error meaning in trace/2.

#### 10.3.2 Codes (closed set)

| code | what failed | `lookup` (exactly these properties) |
|---|---|---|
| `behavior-missing` | a behaviour requested by name is not a root behaviour of the member searched | `imageSet`, `behavior` |
| `action-missing` | a behaviour or reference names an action the member does not declare | `imageSet`, `action` |
| `variant-missing` | an action has no animation variant it can play: none whose condition holds, none for its turn state, or fewer than its class requires | `refKey` |
| `parameter-invalid` | a parameter or guard attribute read by an action is null where a value is required, not of the required type, or outside the range the action accepts | `refKey`, `parameter` |
| `scan-target-missing` | an action needs its scan target and has none | `refKey` |
| `unclassified` | anything the runtime does not type with one of the codes above | none: `lookup` is `null` |

A code outside this table, a `lookup` whose property set is not the code's, a non-null `lookup` for
`unclassified`, or a `null` one for any other code, is `missing-field` (the trace is unjudgeable), as an unknown
event kind is today. Adding a code is a contract change. Reserving a code does not assert that both current seams can
attribute every fault in that class.

#### 10.3.3 Lookup operands and attribution

- `imageSet` is the image-set name of the member that was SEARCHED, which is not always the reporting
  companion's: for a birth, the child's member after the BornMascot fallback of section 3; for a Transform, the
  member transformed into; for an arrival's TargetBehavior, the target's member.
- `behavior` and `action` are the requested name exactly as the runtime looked it up, after evaluation and
  before anything else: not trimmed, not translated through `sourceNames` (a name that was not found has no node
  to translate). A blank name is written as the empty string or as the whitespace it holds; a name that evaluated
  to null is JSON `null`. `""`, `" "` and `null` are three different operands.
- `refKey` is the refKey (section 3) that the failing action's `actionStack` entry has, or would have had when
  the failure is in its construction: the reference's key when the action was reached through an
  `ActionReference`, else its declaration's.
- For a known built-in parameter, parameter is its canonical source schema key. A custom parameter retains its exact
  source name. A synthetic lowering parameter is never exposed as if it were an authored source parameter: type it
  only when explicit source attribution identifies the actual source parameter and refKey; otherwise use
  unclassified.

A producer emits a typed code only when the cause and every required operand are available from stored provenance at
the actual failed operation. It must not evaluate a parameter, guard, condition or script again to obtain them. An
initialized action can use its stored identity; a constructor failure requires explicit builder/reference context
carried into that construction.

If the optional fault-attribution channel cannot supply those operands, emit unclassified with lookup null. That
absence alone is not a provenance abort. A missing or invalid binding already REQUIRED by section 9 remains a
provenance fault; it must not be hidden as unclassified.

#### 10.3.4 Classification and `via`

- A runtime classifies a fault where its typed cause arises (the failed lookup or read), not by reading message
  text. Neither runtime and no comparer ever derives a code from a message.
- Each producer records its actual faults and classifies a known cause independently of the peer. Classification
  neither creates a fault event the runtime did not produce nor erases or downgrades an event to align traces. An
  absent peer event remains a mismatch or a specifically established declared policy divergence; it is not a reason
  to write unclassified.
- `via` lists the wrapper sites that caught the failure and chose its consequence, OUTERMOST FIRST, the order
  section 3 already uses for `actionStack`. The companion's own step is the implicit root and is never listed, so a
  fault caught by the step itself has `via: []`. A fault reported where it arises, with nothing catching it, also
  has `via: []`.
- Do not enforce a guessed causal stack when the fault was already caught/reported by an inner site.
- The wrapper vocabulary is closed:

| entry | the wrapper |
|---|---|
| `startup` | creating a start companion (section 4, startup step 2) |
| `press` | a left-button press dispatched to a companion |
| `release` | a left-button release dispatched to a companion |
| `birth` | entering a born child's first behaviour |
| `transform` | entering the Transform's behaviour in the new member |
| `arrive` | a scan or complex action's arrival transitions (the seeker's own Behavior, then the target's TargetBehavior) |
| `interact` | Interact's closing Behavior |

- Each wrapper's consequence, for a fault it catches:
  - `startup`: startup identifies a fault during creation/initialization of a scenario starter. The existing
    per-runtime outcome is retained. The host can dispose the starter before joining the manager; the current oracle
    runner can instead terminate with an aborted/error footer. Error typing does not normalize these outcomes or
    manufacture a startup snapshot.
  - `press`: the companion is disposed.
  - `release`: the companion is disposed.
  - `birth`: the attempt is refused with `birth-refused` reason `error`, after this event.
  - `transform`: The fault is reported without disposing the companion. The new image set and all writes already
    performed remain; there is no rollback. The previous behavior survives only when failure occurred before the new
    behavior was assigned.
  - `arrive`: the transitions not yet run are skipped; the seeker carries on. Earlier successful transitions and
    state writes remain. Only transitions not yet performed are skipped; no rollback is implied.
  - `interact`: the companion carries on.
- A wrapper that catches and does not rethrow ends the list. Today no wrapper rethrows, so `via` holds at most one
  entry; the list form is kept so a nested wrapper can be added without a schema change.

#### 10.3.5 Comparison

- In a directed trace/2 comparison an `error` event matches on `tick`, `sid`, `kind` and the whole `error`
  object (`code`, every `lookup` operand, and `via` in order), by JSON equality. `message` is not compared.

In a directed comparison, an unclassified code makes only the cause code and lookup operands inconclusive, and only
between paired error events with equal tick, sid, kind and via. Event presence, order, timing, sid and wrapper context
remain exact. A missing/extra error or a different tick, sid, kind or via keeps its ordinary failure class, even when
an error is unclassified.

After encountering such an aligned unknown cause, continue checking all other events and states. Any independently
established failure retains its class and verdict. If those checks find no failure, but an aligned error cause
remains unclassified, return unjudgeable with class error-unclassified and CLI exit 2. Do not return exact or
within-tolerance.

This uncertainty rule applies to directed semantic success. Free-running comparison keeps section 6's separate
time-share claim and its existing failure/pass path, with typed-error shape validation still required. It never
becomes a directed exact claim.

### 10.4 Random numbers (edits section 2)

- A draw in a `none` scenario, and a draw past the end of a `tape`, both yield exactly `0.5`, and the run goes
  on. Its `rng-draw` event records that value at its occurrence.
- In trace/2 a recorded draw past the tape's end whose value is not `0.5` is `rng-invalid` (unjudgeable), as a
  draw within the tape that does not hold the tape's value is today.

### 10.5 Tape exhaustion (edits sections 3, 5 and 6)

#### 10.5.1 The signal

- Drawing past the tape's end is BEHAVIOUR (section 6) and never a run failure: a runtime that exhausts its tape
  keeps stepping to tick N, and exhaustion does not set its footer status, which is normally `complete`.
- Every trace/2 footer carries `tapeExhausted`, a required boolean.

A producer footer reports whether its actual draw count exceeded the declared tape length. For none and splitmix64 the
flag is false. Within-tape values must match the tape; observed past-tape values must be exactly 0.5. The flag and
draw records must agree.

A harness timeout footer describes only the complete recorded prefix. Its exhaustion flag is derived from that
prefix's recorded draws; it makes no claim about unwritten work. It does not repair a malformed or partial JSON record
or overwrite an existing footer.

- The draw records are the event-level evidence: the first `rng-draw` whose occurrence exceeds the tape's length
  is the first exhausted draw. No new event kind is added, so event sequences and the section 5 digest gain
  nothing to disagree on.

Exhaustion alone does not set a run-failure status. If a genuine run failure coexists with proven exhaustion, the
run-failure class still wins; exhaustion may be a secondary finding only when the available evidence proves it.

#### 10.5.2 Consistency (validity, before anything is compared)

- In a traced run of a `tape` scenario, `tapeExhausted` must equal whether the trace records more draws than the tape
  holds; in a traced `none` or `splitmix64` run it must be false.
- Any disagreement is `malformed`.
- The v1.1 oracle-only optional `tapeExhausted` extension is subsumed: in trace/2 the field is required of both
  runtimes.

#### 10.5.3 Trace-off artifacts and the observation proof

- In trace/2, a footer that carries `unobservedDraws` or `unobservedEvents` identifies a trace-off artifact. No
  header field is added.

Trace-off artifacts are inputs to the dedicated observation-proof validator, not ordinary cross-runtime parity
inputs. Their unobservedDraws/unobservedEvents counters describe withheld observations. The public comparer always
validates against recorded rng-draw events and must not switch modes because unobservedDraws is present. Require the
trace-off counters on an off footer and refuse such proof-only artifacts as unjudgeable:trace-off-not-comparable when
submitted to cross-runtime comparison.

A trace-off timeout has suppressed draw lines and generally has no final durable unobserved tally. Do not infer
tapeExhausted=false from those absent lines or construct a successful observation proof. Without a durable tally,
retain the incomplete artifact and report an unverified/run-failure proof result. An ordinary traced timeout can still
receive the prefix footer above.

The observation proof compares the actual trace-on draw count with the trace-off unobservedDraws, both exhaustion
flags, final states and event digest. It requires successful completion; timeout/aborted/error runs cannot satisfy it.

### 10.6 Startup faults (edits section 6)

Startup runtime faults do not make an otherwise valid scenario scenario-invalid. A failed run with no startup state
remains subject to the existing aborted/error rule. A complete trace claiming a startup snapshot must meet its
validity and starter-binding rules. When a snapshot was interrupted, only an otherwise valid active proper prefix of
the expected starters, in order, with no later record proving completion is inconclusive. An already observed wrong
sid/order/pending flag remains provable evidence of mismatch.

### 10.7 Precedence (trace/2 pairs)

The comparer decides in this order and reports the first class that applies:

1. header readability and recognised schema (`missing-field`), then mixed versions (`trace-version-mismatch`); a
   recognised trace/1 pair leaves this list here for its v1.1 rules (10.2); then a trace/2 trace-off artifact on
   either side (`trace-off-not-comparable`, 10.5.3);
2. each trace's own validity under its version: `missing-field` (including the error object of 10.3.1 and
   10.3.2), `build-witness-missing`, `malformed` (including 10.5.2), `rng-invalid` (including 10.4 and 10.5.1),
   `runtime-mismatch`;
3. input and binding, unchanged from v1.1: `scenario-invalid`, `pack-invalid`, `scenario-hash-mismatch`,
   `pack-hash-mismatch`, `artifact-hash-mismatch`, `tick-count-mismatch`, `source-pin-mismatch`,
   `scenario-start-mismatch` (with 10.6);
4. run failures, unchanged from v1.1 and NOT waived for exhaustion: `truncated`, `timeout`, `aborted` (a footer
   status `error` is graded `aborted`). If a genuine run failure coexists with proven exhaustion, the run-failure
   class still wins; exhaustion may be a secondary finding only when the available evidence proves it;
5. RNG behaviour, as today: `rng-unexpected-draw` (kind `none`), `rng-tape-exhausted` (kind `tape`, either side
   drew past the end), then `rng-draw-count`;
6. `pending` with class `script-site-unmapped` (section 9);
7. events in order, then states (10.3.5); in a directed comparison an aligned unknown cause does not end the walk,
   and the first independently established failure gives the class;
8. directed comparisons only: `unjudgeable` with class `error-unclassified`, CLI exit 2, when nothing above applied
   and an aligned error cause remains unclassified (10.3.5);
9. `exact` or `within-tolerance:<name>[,<name>...]`, or, for a free-running scenario, the time-share verdict of
   section 6.

Steps 2 to 4 group the v1.1 classes and do not reorder them: those checks keep the relative order the v1.1 comparer
already gives them, and each trace/2 check named in step 2 joins the v1.1 check of the same class. In that order a
scenario or pack refused before either trace is read (`scenario-invalid` from the scenario alone, `pack-invalid`) is
decided before step 1, `source-pin-mismatch` is one of the oracle trace's own validity checks, and a `scenario-invalid`
judged from the traces (an inactive button target, section 1) follows `scenario-start-mismatch`.

### 10.8 Producer obligations

Both producers:

- write `shimeji-parity-trace/2` in the header and `tapeExhausted` in every footer, trace-off included;
- classify by 10.3.3 and 10.3.4, and write `unclassified` wherever no typed cause with its operands is available;
- continue past a tape's end on `0.5` (10.4, 10.5.1);
- Instrumentation must preserve existing exception type/catch behavior and state-write order, except the explicitly
  agreed RNG continuation rule.

Host (`src/dotNet/Shimeji`):

- types each failure where it is thrown or reported, adds the wrapper entry at each of the seven catch sites of
  10.3.4, and writes `unclassified` for every exception it has not typed, including its catch-all for exceptions
  that are not its own fault class;
- keeps every message byte-identical to today's, so the product log and its fault throttle are unaffected;
- continues past a tape's end on `0.5` without setting its footer status for it, and still logs
  `rng-tape-exhausted` as an informational line;
- types a code only from stored provenance (10.3.3); a binding that section 9 already requires stays a provenance
  fault when it is missing or invalid, and ends the run in error;
- runs its section 5 observation proof by 10.5.3.

Oracle (Codex): writes trace/2; classifies through hooks that carry stored action identity (an initialized action's
own identity, and explicit builder/reference context for a constructor fault), and writes `unclassified` where that
context is absent; labels the same seven wrapper sites where its runtime catches a fault; keeps its current startup
outcome (10.3.4, 10.6); writes `tapeExhausted` in every footer and keeps `unobservedDraws` and `unobservedEvents` on
trace-off footers.

Comparer (Codex): 10.2, the shape rules of 10.3.1 and 10.3.2 (duplicate properties included), 10.3.5, 10.4, 10.5,
10.6 and 10.7, the trace-off refusal, and unchanged trace/1 and free-running dispatch, with self-test cases that each
fail with exactly their own class.

### 10.9 Closure

- N-parity-1107-01 and -02 close when fresh paired trace/2 runs of S-breed-blankbehavior-01 and
  S-transform-blankbehavior-01 compare `exact`, each with an `error` event of code `behavior-missing`, `imageSet`
  the member searched (10.3.3), `behavior` `""` and `via` `["birth"]` and `["transform"]` respectively.
- N-parity-1107-04 closes when a fresh paired trace/2 run of a scenario whose tape is one draw short grades
  `fail` with class `rng-tape-exhausted`, with both footers `complete` and `tapeExhausted` true.
- Historical v1 and v1.1 verdicts keep their input snapshot and are not re-graded.
- Do not add an unvalidated broad policy exemption while N-parity-1107-03 remains open.

## v1.1 integration record

Both halves landed together. The converter writes map/2 (`TickPackEmitter`) and its readers require it; the host
(`src/dotNet/Shimeji/ParityRun.cs`) reads map/2 only and refuses map/1, checks every mapping as section 9 says before
the trace file is opened (exit 2, no trace), binds each emitted pose by identity, traces pose.index through the map of
the member that defines the applied pose (a provenance fault, never an emitted index, when it has none), writes
drag/start only after Dragged is built, and stops a run at a left-button edge for a companion that is not active
(footer `aborted`, exit 2). The host's trace-on/trace-off proof (section 5) runs in its window self-test. The oracle
classifies only an aborted run carrying V1_REFUSAL V1_INPUT_TARGET_ABSENT as scenario-invalid in its result record and
keeps the physical trace/1 aborted footer.

S-slice-breed-01 describes the child behind the parent. That description changes the regenerated scenario hash, so
fresh host traces are required for the new snapshot; historical comparisons keep their original input snapshot.

Error-message text remains an exact `error` payload under v1.1. The two historical blank-behaviour mismatches (one
birth, one Transform) differ only in that text and stay open until a separately reviewed rule or policy resolves them;
v1.1 does not exempt them.

The section 6 clarification for N-parity-1107-07 (a state line at or after the sid's own `dispose` tick is
`malformed`) was proposed and signed by Codex on 2026-11-08 and signed by Claude the same day. It writes down a rule
the comparer has applied since ff0fd55. `dispose` is the companion's removal from the manager, which a failed
tentative child or a failed starter never reaches, so their provisional or reused sids are unaffected.

## v1.2 signature record

Section 10 is Part A of `CONTRACT-v1.2-trace2-consolidated.md` (SHA-256
`a20d47183355f9c651e86a0f1b167f2875ff44e58817369f6cf01bfc017c323a`), signed at that hash by Codex on 2026-11-08 05:46:53 UTC
and by Claude, with one editorial deletion Codex authorised at signature: the last sentence of 10.3.5, which
referred to a "step-8 pending rule" that an earlier draft had and the signed text does not. Section 10 as
pasted here, from its heading to its last line, has SHA-256 `c2a5fd6a73feea4d1b5eb03bc35b1c9d57340ecec5072f89b791fa4343032bb6`.

Confirmed at signature (Part E of the signed text): Interact's "the companion carries on" promises neither rollback
nor the old behaviour; an otherwise valid interrupted startup prefix leaves starter binding silent, so `timeout` or
`truncated` decides, and a partial `aborted` or `error` trace gets no prefix exemption; an in-range draw in a
`none` scenario still fails `rng-unexpected-draw` while the producer returns `0.5`; a press or release exception
that is not the host's own fault class keeps the existing `error` footer and writes no `error` event.

Agreed with the signature, no contract change: the host build witness's `commit` is provenance, not a binding (the
three hashes bind the artefacts), and the host writes the full source revision from dedicated build metadata read
only by the parity run, never the product version.

This is a contract approval, not an implementation verdict. Both producers and the comparer still have to land,
and the three closure fixtures of 10.9 are fresh, joint, paired runs; a v1.2 integration record follows when both
halves have landed.
