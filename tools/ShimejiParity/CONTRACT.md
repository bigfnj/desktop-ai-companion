# Shimeji parity contract, version 1

Two runtimes run the same scenario and each writes a trace; the comparer decides whether they behaved the same.

- **Oracle:** the official Shimeji-EE 1.0.23 source, rebuilt on JDK 8 with a test-seam patch
  (`tools/ShimejiParity/oracle/`). The source is fetched and verified by script and never committed.
- **Host:** Desktop AI Companion running a converted pack in parity mode. The environment and clock seams are
  scenario-driven and the engine is stepped tick by tick.

This file is the contract both sides implement. A change to it bumps the schema version on every artefact it
touches. Field names are exact and case-sensitive.
Exception: the labelled v1.1 optional sourceNames map addition retains shimeji-parity-map/1; existing required fields and the trace/scenario schemas are unchanged.

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
- Neither runtime ever reads the real mouse, the real windows or the real displays during a scenario.

## 2. Random numbers

`rng.kind` is one of:

- `none`: the scenario is directed. Any draw is a failure, reported as `rng-unexpected-draw`.
- `tape`: `"values": [ ... ]`, doubles in [0, 1) consumed in order. Running past the end is a failure,
  `rng-tape-exhausted`.
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
  order. It is an ordinal, not a character offset: a runtime that records offsets converts them.
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
| `birth-refused` | `child` (sid or null), `attempt`, `reason` (`cap` / `toggle-off` / `missing-member` / `error`) |
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

A behaviour's `cause` follows the entry that was CHOSEN: `select` when it came from the root list, `next` when it came
from the ending behaviour's next list (even if a root behaviour shares its name); `forced` when it was entered by name
(a scenario start, a birth, a Transform, a hotspot, lost ground, leaving the screen, a seeker's own transition);
`partner` for a scan target moved by its seeker; `fallback` only for the zero-weight and disabled-by-name drop-ins.
`scan-target` is written at every scan, with a null target when no one offers (ScanInteract searches every tick it
has no target). Action changes have no event of their own in v1: they show in the state lines, and a transient
instant action that left no observable effect is not counted as covered by them.

Birth lineage: `attempt` counts a parent's birth attempts from 1, refused or not. `ordinal` counts only its
successful births from 1, and a child's sid is `<parentSid>.b<ordinal>`, so a refused attempt never consumes a sid.
A refused birth's `child` is null when the source allocated no child; neither runtime evaluates `BornCount` or the
birth offsets just to describe a child the source never made. Transform keeps the sid. Process ids and window
handles never appear.

**Last line, the footer:**
`{ "type": "footer", "ticks": 600, "status": "complete", "draws": 0, "states": 601, "events": 12 }`.
`ticks` is N; `draws`, `states` and `events` count the trace's `rng-draw` events, state lines and event lines. The
status is `complete`, `timeout`, `aborted` or `error`. A trace with no footer is `truncated`.

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

## 6. Comparison

A trace is checked before anything is compared, and a trace that fails a check is unjudgeable:

- The header, footer and every line carry their required fields (`missing-field`), and the build witness is
  complete and well-formed (`build-witness-missing`).
- `malformed`: two state lines for one tick and sid; ticks out of order or outside 0..N; footer counts that do not
  match the lines; no state at tick 0; a companion whose lines have a gap, or that stops before tick N without a
  `dispose` event; a `velocity` that is not the anchor change; a `refKey` that is not the last `actionStack` entry;
  `rng-draw` occurrences out of sequence.
- `rng-invalid`: a recorded draw value outside [0, 1), or, in a `tape` scenario, a recorded value that is not the
  tape's value at that occurrence. A trace that did not take the scenario's numbers as declared cannot be compared, so
  this is unjudgeable. (Drawing at all in a `none` scenario, or past the tape's end, is the runtime's BEHAVIOUR and is
  judged as `rng-unexpected-draw` / `rng-tape-exhausted`, section 2.)
- `runtime-mismatch`: the oracle trace is not labelled `oracle`, or the host trace `host`.
- `scenario-invalid`: the scenario breaks section 1 (a tick-0 button edge, a missing field, a start companion without
  a `behavior`: a directed start state names its behaviour, so no unplanned selection draw runs).
- `pack-invalid`: the pack preflight refused a link or a path outside the pack (section 3).
- `scenario-hash-mismatch` / `pack-hash-mismatch`: the traces disagree on the scenario or pack hash, or, when the
  comparer is given the scenario, either trace disagrees with the file or the pack on disk.
- `artifact-hash-mismatch`: the comparer was given the oracle JAR or the host executable and its hash is not the
  witness's.
- `tick-count-mismatch`: the traces ran a different number of steps, or not the scenario's.

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
- **Verdicts:** `exact`, `within-tolerance:<name>`, `time-share-pass` (free-running only), `policy:<id>`, `approximate`, `unsupported`,
  `not-exercised`, `pending` (the host primitive does not exist yet), `fail`, `unjudgeable`.
- **Behaviour failures:** `missing-event`, `extra-event`, `event-mismatch`, `entity-mismatch`, `state-mismatch`,
  `rng-unexpected-draw`, `rng-tape-exhausted`, `rng-draw-count`, `time-share`. **Run failures:** `truncated`,
  `timeout`, `aborted`.
- Every failure names the scenario, the first mismatching tick, the sid and the field. One tolerance never covers
  an unrelated field.

## 7. Policy divergences (expected; each carries its own verdict, never `exact`)

| id | divergence |
|---|---|
| `policy:window-off-plays` | With window actions off, the animation plays and nothing moves. The source ends the action. |
| `policy:birth-cap` | Births count toward the host's 16-companion cap. At the cap, a `birth-refused` event with reason `cap` is written. |
| `policy:session-only-births` | Bred companions are not saved into the user's companion mix. |
| `policy:scaninteract-precedence` | ScanInteract honours Condition and Duration. The source's operator-precedence slip lets animation time bypass them. |
| `policy:breed-parent-reposition` | A disabled born behaviour does not move the PARENT, as the source's fallback does. |
| `policy:complexmove-guarded` | ComplexMove's arrival is guarded when scanning is off. The source dereferences a null target. |

## 8. Evidence

A scenario id (`S-...`) is the public evidence id cited by `tools/ShimejiConvert/ACTIONS.md`. Synthetic packs and
scenarios live in `tools/ShimejiParity/scenarios/`. Traces, clips and corpus scenarios stay outside the repository.

## 9. The host's map back to the source

The host runs a converted pet, so it names source things through a parity map the converter writes beside the pet
when asked to (a test artefact, never shipped and never read outside parity mode):

- every frame index: the source image key it came from (`image`, and `imageRight` when it is a right-facing
  alternate), with the anchor and size the source draws that image with: the EFFECTIVE anchor of section 3's
  `poseAnchor` (the first-loaded pose's, for a cached pair). A pose whose geometry disagrees with its frame's map
  entry is refused before anything is traced. A pose's authored anchor, where it differs, stays in the converter's
  source IR, not in the map;
- every action node and every edge that carries parameter bindings: the refKey of the declaration or reference it
  was lowered from;
- every script site: its `script:<refKey>#<attribute>#<k>` name.
- (v1.1) Every behaviour and action node whose emitted name differs from its source-observable name: that source name, keyed by the existing member-qualified node or activation identity. Preserve an absent Name as JSON null and an explicitly empty Name as the empty string. A present null or empty value is distinct from a missing map entry.

The map is what lets the host write `pose.image`, `poseAnchor`, `refKey`, `actionStack`, `rng-draw.site` and (v1.1)
state `behavior` and `action` and every name-bearing event payload whose name conversion changed in the oracle's
terms. A host trace written without one is unjudgeable (`missing-field`).
