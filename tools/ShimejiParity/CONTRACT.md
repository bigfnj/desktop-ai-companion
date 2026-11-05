# Shimeji parity contract, version 1

Two runtimes run the same scenario and each writes a trace; the comparer decides whether they behaved the same.

- **Oracle:** the official Shimeji-EE 1.0.23 source, rebuilt on JDK 8 with a test-seam patch
  (`tools/ShimejiParity/oracle/`). The source is fetched and verified by script and never committed.
- **Host:** Desktop AI Companion running a converted pack in parity mode. The environment and clock seams are
  scenario-driven and the engine is stepped tick by tick.

This file is the contract both sides implement. A change to it bumps the schema version on every artefact it
touches. Field names are exact and case-sensitive.

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
    and release in the same tick).
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
  expression, and k, the 1-based position of the `Math.random` call in the expression text.

| site | what draws (source location, for the oracle) |
|---|---|
| `builtin:create-imageset` | a companion created without a named image set picks one (Main.java:1290) |
| `builtin:create-facing` | a created companion's initial facing (Main.java:1308) |
| `builtin:behavior-select` | choosing the next behaviour: ONE draw over the candidate list, even for a single candidate (Configuration.java:248) |
| `builtin:no-candidate-drop-x` | no behaviour is eligible: the companion drops in at a random x (Configuration.java:237/242) |
| `builtin:missing-behavior-drop-x` | a named behaviour does not exist: same drop-in (Configuration.java:274/279) |
| `builtin:out-of-bounds-drop-x` | the companion left the screen: same drop-in (UserBehavior.java:261/266) |
| `builtin:dragged-regist` | Dragged's 90% chance of resisting at regist time (Dragged.java:104) |
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
  line ending in `\n`, sorted by path in ordinal order; `packSha256` is the SHA-256 of that text in UTF-8.
- The oracle's `build` requires `sourceZipSha256`, `patchSha256`, `jdk`, `deps` and `artifactSha256`. The host's
  requires `commit`, `exeSha256`, `engineSha256` and `converterSha256`. Every `...Sha256` value is 64 lowercase hex
  characters. A missing or malformed witness is `build-witness-missing`.

**State lines.** Tick 0 is the startup snapshot; then one snapshot after each step. Within a tick the lines list the
active companions in manager order, then the pending births in add-queue order.

```json
{ "type": "state", "tick": 12, "sid": "m1", "imageSet": "Alpha", "behavior": "Walk", "action": "Walk",
  "actionClass": "Move", "actionStack": ["Alpha|conf/behaviors.xml|/Mascot[1]/BehaviorList[1]/Behavior[3]",
  "Alpha|conf/actions.xml|/Mascot[1]/ActionList[1]/Action[2]"],
  "refKey": "Alpha|conf/actions.xml|/Mascot[1]/ActionList[1]/Action[2]", "actionAge": 7,
  "pose": { "image": "/shime2.png", "imageRight": null, "index": 1 }, "poseAnchor": [32, 128], "visible": true,
  "anchor": [622, 680], "origin": [590, 552], "lookRight": false, "velocity": [-2, 0],
  "affordances": [], "target": null, "dragging": false, "pending": false, "disposing": false, "vars": {} }
```

| field | meaning | may be null |
|---|---|---|
| `behavior` | the current behaviour's name | yes, before one is chosen |
| `action` | the leaf action's name | yes, before evaluation or for an unnamed action |
| `actionClass` | `Sequence`, `Select`, `Stay`, `Move`, `Animate`, or the short class name of an `Embedded` action | yes, with `action` |
| `actionStack` | the refKeys from the behaviour's root action to the leaf; `[]` when there is none | no |
| `refKey` | the declaration or reference that started the leaf action; equals the last `actionStack` entry | yes, when the stack is empty |
| `actionAge` | the leaf action's time in ticks | yes, before evaluation |
| `pose` | the current pose's source image keys and its index in the animation | yes, before evaluation |
| `poseAnchor` | the offset from `origin` to the rendered image's authored anchor, after facing and `dpiScale` | yes, with `pose` |
| `visible` | false while an image-less pose hides the companion, and once it is disposing | no |
| `anchor` | the contact point in screen pixels | no |
| `origin` | the rendered image's top-left in screen pixels | yes, with `pose` |
| `velocity` | the committed anchor change over this step: this tick's `anchor` minus the previous tick's; `[0, 0]` on a companion's first line | no |
| `affordances` | the offers made this tick, in the order they were made | no |
| `target` | the chosen scan target's sid | yes |
| `pending` | a birth that was requested and not yet activated; a pending companion never steps | no |
| `disposing` | disposal was requested this tick; the companion is removed at the next tick's step 3 | no |
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
| `behavior` | `from`, `to`, `cause` (`select` / `next` / `forced` / `partner` / `fallback`) |
| `action` | `from`, `to`, `cause` (`sequence` / `select` / `guard` / `duration` / `arrival` / `lost` / `instant`) |
| `transform` | `fromImageSet`, `toImageSet`, `behavior` |
| `birth-request` | `child` (sid), `imageSet`, `behavior`, `anchor`, `lookRight`, `ordinal`, `attempt` |
| `birth-activate` | `child` |
| `birth-refused` | `child` (sid or null), `attempt`, `reason` (`cap` / `toggle-off` / `missing-member` / `error`) |
| `scan-target` | `affordance`, `target` (sid or null) |
| `interact` | `with` (sid list sharing the anchor) |
| `dispose-request` | `reason` |
| `dispose` | none |
| `window-move` | `from` and `to` rect |
| `window-minimize` | none |
| `sound` | `clip`, `op` (`start` / `stop`) |
| `mute` | `clip` or null |
| `rng-draw` | `site`, `occurrence`, `value`, `purpose` |
| `drag` | `op` (`start` / `end`) |
| `hotspot` | `op` (`press` / `release`), `behavior` |
| `error` | `message` |

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
2. Each `mascots` entry is created in order, its start state applied, then its behaviour initialised.
3. Tick-0 state lines are written.

Each step t = 1 to N, in this order:

1. The environment applies tick t's scenario entries (cursor, presses, active window).
2. Births requested in the previous tick are activated.
3. Disposals requested in the previous tick are removed.
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
- `runtime-mismatch`: the oracle trace is not labelled `oracle`, or the host trace `host`.
- `scenario-invalid`: the scenario breaks section 1 (a tick-0 button edge, a missing field).
- `scenario-hash-mismatch` / `pack-hash-mismatch`: the traces disagree on the scenario or pack hash, or, when the
  comparer is given the scenario, either trace disagrees with the file or the pack on disk.
- `artifact-hash-mismatch`: the comparer was given the oracle JAR or the host executable and its hash is not the
  witness's.
- `tick-count-mismatch`: the traces ran a different number of steps, or not the scenario's.

Then:

- **Directed scenarios** (`rng.kind` is `none` or `tape`):
  - Event sequences match exactly on tick, sid, kind and every payload field, `rng-draw` site included.
  - Each tick lists the same companions in the same order (`entity-mismatch`).
  - State lines match exactly on every field except `anchor`, `origin`, `poseAnchor` and `velocity`. Velocity is
    checked against the anchor inside each trace, so comparing anchors compares it.
  - `anchor` matches exactly unless the scenario declares a named rounding boundary.
  - `origin` is compared normalised, as `origin + poseAnchor`: the point where the image's authored anchor was drawn.
    The two runtimes may pad their images differently; that point may not differ.
- **Free-running scenarios** (`splitmix64`) make a separate claim: behaviour time-share, with the tolerance and both
  draw counts printed beside the result.
- **Verdicts:** `exact`, `within-tolerance:<name>`, `policy:<id>`, `approximate`, `unsupported`,
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
  alternate), with that image's authored anchor and size;
- every action node and every edge that carries parameter bindings: the refKey of the declaration or reference it
  was lowered from;
- every script site: its `script:<refKey>#<attribute>#<k>` name.

The map is what lets the host write `pose.image`, `poseAnchor`, `refKey`, `actionStack` and `rng-draw.site` in the
oracle's terms. A host trace written without one is unjudgeable (`missing-field`).
