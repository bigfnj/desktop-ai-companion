# Shimeji parity contract, version 1.4

Signed 2026-11-07 by Codex (proposal author) and Claude (host review). Version 1.1 replaces version 1, which was frozen
and signed: v1's text is `git show 2d9c6f5:tools/ShimejiParity/CONTRACT.md`. Every passage v1.1 adds or changes is
marked **(v1.1)**; unmarked text is v1's. The proposal this came from, `CONTRACT-v1.1-proposal.md`, is kept beside it,
marked superseded; the host review's eight edits are applied here.

**(v1.2)** Version 1.2 adds section 10, the trace schema `shimeji-parity-trace/2`, signed 2026-11-07 by Codex
(oracle and comparer) and Claude (host producer). Sections 1 to 9 are v1.1's, unchanged, apart from three pointers
to section 10 marked **(v1.2)**. A trace/1 pair is still judged under v1.1 exactly as before. The signed text,
`CONTRACT-v1.2-trace2-consolidated.md`, is kept beside this file unchanged, with its change table, host-code checks,
control matrix and open items; the v1.2 signature record at the end of this file says how section 10 differs from it.

**(v1.3)** Version 1.3 adds section 11's finite registered scenario/2 exception. It deliberately retains trace/2 and map/2 wire versions while amending dispatch, optional engine binding, policy grading, metadata and proof obligations only as section 11 specifies. (v1.4) Superseded for new host parity runs by map/3 (9.1). Historical legacy results without the new engine option or a registered scenario/2 claim remain unchanged. The bounded CLI input-error handling below is an invocation-diagnostic exception, not a new legacy policy grade. The frozen H declaration is retained as tools/ShimejiParity/CONTRACT-v1.3-policy-H.md at SHA-256 c3c261fdd41c984d437a4b3e84c4128f53503425eb4cff297605f64969631886.

**(v1.3)** The v1.2 statement above that sections 1 to 9 are unchanged describes its historical snapshot. The separately adopted screens clarification, the explicitly marked passages of v1.3 and later versions, and section 11 below, are the prospective amendments to that snapshot; all earlier signature records retain their historical meaning. **(v1.4)** Version 1.4 also amends sections 2, 3 and 9 of that snapshot.

(v1.4) Version 1.4 requires shimeji-parity-map/3 (section 9) and adds Mute read, observation and host failure-channel obligations to 10.8. Explicit exceptions, each retaining its schema version: trace/2 is retained and mute.clip in trace/2 has the source-intent meaning of 9.2; scenario/1 and scenario/2 are retained with fields unchanged. This version defines no trace/1 Mute meaning. Pre-v1.4 host trace/2 artifacts retain their archived meaning and are not regraded. Map/2 retention is superseded prospectively for new host parity runs only. The signed v1.4 Mute proposal, CONTRACT-v1.4-mute-proposal.md, is kept beside this file unchanged; its Required directed controls table and its Ownership and review evidence section bind by reference, and its SHA-256 is in the v1.4 signature record.

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

**(v1.3)** The ordinary schema and rules below remain scenario/1. Section 11.1 adds only the four byte-admitted scenario/2 inputs, each with its closed policy declaration. Its scanner/admission and exact per-reader legacy routes apply before interpreting those inputs; labels or a caller-asserted digest do not authorize admission. The adopted non-empty-screen clarification above and below is retained. A host run whose reconciliation already failed keeps footer status error under 11.5 and S01 when it later meets an inactive target; comparer classification is unchanged.

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
  order. It is an ordinal, not a character offset: a runtime that records offsets converts them. **(v1.1)** (v1.4) Map/2 and map/3 do not yet let the host write this form (section 9).
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

**(v1.3)** Section 11 retains the trace wire fields and the recognized trace/1 and trace/2 schemas. Its finite dispatch, primary binding, policy grading, metadata and producer-proof exceptions add no trace member or event kind. Converted pet/map bytes are closure evidence, not a new trace channel.

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
| `mute` | `clip` or null; (v1.4) In trace/2, clip has the meaning of 9.2. |
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

**(v1.3)** Section 11.3 supplies the complete precedence for an identified registered scenario/2 claim, including static input admission, the three named scenario-invalid placements, primary artifact requirements and footprint-first qualification. Section 11.6 defines its required result metadata/admissibility. Its footprint-first result is an explicit exception to the first-mismatching-tick and independent-failure promises below; secondary raw evidence preserves the actual original-body outcome. Other legacy paths retain their existing order and outcomes subject only to section 11's explicitly opted-in engine/usage-input exceptions.

**(v1.3)** The CLI exit table is:

| result | exit |
|---|---|
| Existing exact, valid within-tolerance or time-share-pass on legacy paths | 0, unchanged |
| Canonical policy:<id> established by this invocation with the signed registry/digest and qualified=true | 0 |
| fail, pending, approximate, unsupported, not-exercised or any other non-success result | 1 |
| unjudgeable, including requirement classes, scenario-version-incompatible and error-unclassified | 2 |
| Named CLI usage/input error, no fabricated grade | 2; a crash/traceback is an implementation defect, never evidence of fail |

**(v1.3)** Canonical policy success requires the matched registry/digest/entry and qualified=true; a verdict string prefix is not success. These input diagnostics do not invent comparer grades.

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

**(v1.3)** The new optional supplied host-engine bytes are also compared whenever supplied, under section 11.2. Admitted scenario/2 requires oracle JAR, host EXE and host engine. Host artifactBound then requires both EXE and engine matched; unchanged apphost bytes alone do not witness implementation. Omission of the new option adds no legacy requirement.
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

**(v1.3)** For identified registered scenario/2 only, section 11.3's footprint-first exception and section 11.6 metadata control the primary class and truthful secondary raw result, including firstDivergence null where no raw mismatch is established. The ordinary promise above remains for other paths.

**(v1.1)** Comparer report metadata: buildWitness has oracle and host objects, each with artifactBound (boolean) and
verification (artifact-bound or shape-only). Artifact-bound means a supplied artifact's bytes matched the trace
header hash; it does not by itself prove its build provenance. shape-only means the header shape was validated
without supplied artifact bytes. tolerancesUsed lists all used tolerance names, sorted and unique. A successful
within-tolerance verdict joins those names with commas in the same order. No tolerance absorbs another field.

## 7. Policy divergences (expected; each carries its own verdict, never `exact`)

**(v1.3)** Section 11 permits a qualified policy verdict, and feature qualification by reviewed closure disposition, only for the four registered cases: birth-cap/cap-at, window-off-plays/window-carry-off, window-off-plays/window-throw-off and window-off-plays/window-minimize-off at their exact admitted input identities. Its positive footprint, residual, binding and producer-proof obligations apply. Other policy names in the historical table acquire no new exemption. A missing positive divergence is policy-not-qualified, never declaration-only success.

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

**(v1.3)** The four retained policy scenario/2 files and their packs are the exact inputs under tools/ShimejiParity/policy/inputs/, keeping their relative packs/... references. They are distinct from the ordinary generator output directory. Section 11.10 identifies the final payload and the required signature/closure separation. No private corpus, source text or runtime trace is installed as an input by v1.3.

A scenario id (`S-...`) is the public evidence id cited by `tools/ShimejiConvert/ACTIONS.md`. Synthetic packs and
scenarios live in `tools/ShimejiParity/scenarios/`. Traces, clips and corpus scenarios stay outside the repository.

## 9. The host's map back to the source

**(v1.3)** H retains map/2 and the section 9 mapping fields as written in v1.2. (v1.4) Superseded for new host parity runs by map/3 (9.1). Its section 11 proof obligations bind the converted pet/map inventory as evidence without creating a new trace channel. A later separately reviewed Mute map/3 adoption is outside v1.3 and does not rewrite historical H or legacy artifacts.

The host runs a converted pet, so it names source things through a parity map the converter writes beside the pet
when asked to (a test artefact, never shipped and never read outside parity mode). (v1.4) A new host parity run reads shimeji-parity-map/3 only (9.1). A map/2 object has these
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
- **(v1.1 clarification)** (v1.4) no script-site table yet. The host cannot name a script draw in section 2's form from map/2 or map/3,
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

The map is what lets the host write `pose.image`, **(v1.1)** `pose.index`, `poseAnchor`, `refKey`, `actionStack`, (v1.4) mute.clip and (v1.1)
state `behavior` and `action` and every name-bearing event payload whose name conversion changed in the oracle's
terms. A host trace written without one is unjudgeable (`missing-field`).

### 9.1 Map/3

**(v1.4) P1-B Row shape.** A map/3 object has `schema` exactly `shimeji-parity-map/3`, retains every other map/2 property with its map/2 meaning, and adds required top-level `soundParameters`, an array. It is empty when the converted member has no emitted Sound parameter or binding.

Each row has exactly the following properties. Field names and string comparisons are ordinal and case-sensitive.

| Property | Meaning and type |
|---|---|
| `kind` | String, exactly `action`, `behaviour` or `child` |
| `nodeId` | Canonical nonnegative JSON integer within Int32, read from its raw token; the emitted owning node's id |
| `childIndex` | Present for `child` only; canonical nonnegative JSON integer within Int32, read from its raw token; position in final emitted child order |
| `sourceRefKey` | String; the source element defining the parameter or binding, under P1-D |
| `emittedValue` | String; exact decoded `value` attribute of the emitted Sound param or bind, including a lowered script body rather than its original wrapper |
| `emittedCache` | String, exactly `none`, `action` or `frame`; exact emitted `cache` attribute |
| `mode` | String, exactly `literal` or `evaluated` |
| `sourceValue` | Required for `literal` only, forbidden for `evaluated`; string containing the exact decoded authored Sound attribute, including empty or unchanged text |
| `sourceKind` | Required for `literal` only, forbidden for `evaluated`; string, exactly `string`, `number` or `boolean`, computed under P1-C |

Canonical integer tokens have no negative sign, negative zero, fraction, exponent, whitespace or leading zero except `0` itself. A literal row has `emittedCache` `none`. An evaluated row has `emittedCache` `action` or `frame`. An evaluated definition does not require a non-null compiled Program: an empty evaluated body can compile to no program.

**(v1.4) P1-C Source constant classification.** Compute `sourceKind` from `sourceValue` without evaluating a script. The script-envelope check uses the untrimmed decoded sourceValue:

0. A literal row whose sourceValue has length at least 3, ends with `}` and starts with `${` or `#{` is a source script, not a constant; refuse the map as a source-kind disagreement.

1. Exactly `true` or `false`, before any trimming or case conversion, is `boolean`.
2. Otherwise, a value accepted by Java's `Double.parseDouble` after Java's `String.trim` is `number`.
3. Everything else is `string`, including `null` and the empty string.

Java trim removes only U+0000 through U+0020 at the ends. It does not remove U+0085, U+00A0, U+2003 or U+3000. Boolean equality is exact before trimming: padded `true` is not classified as a Boolean by this rule. The Java number language includes its accepted signed decimal and hexadecimal floating forms, NaN and Infinity, and the accepted f/F/d/D suffix forms; a suffix is not accepted merely because one of those letters appears. Java acceptance, rather than the host's general `.NET string.Trim` or `double.TryParse` behavior, defines the result.

The converter records the kind and the host independently recomputes it. A disagreement refuses the map. `TickType.ParseLiteral` or its current `TryParseJavaDouble` cannot be reused unmodified for this classification: their number path uses .NET trim.

Literal restoration is qualified by `sourceKind` `string` alone. Neither emitted text nor the compiled host kind qualifies or disqualifies it. Source String literals `/1`, `/true`, `TRUE`, `False`, `/01` and `/0X1P1` can become numeric or Boolean host constants; their exact source text is still restored for raw intent. Actual TickSound arguments and effects remain unchanged and outside that restoration claim.

**(v1.4) P1-D Host binding.** Each row binds to the compiled definition under the ordinal parameter name `Sound` at this location:

| `kind` | Compiled location | Required source map entry |
|---|---|---|
| `action` | `ActionsById[nodeId].Params["Sound"]` | `actions[nodeId]` |
| `behaviour` | `BehavioursById[nodeId].Binds["Sound"]` | `behaviours[nodeId]` |
| `child` | `ActionsById[nodeId].Children[childIndex].Binds["Sound"]` | `children["nodeId:childIndex"]` |

Hold the binding in a map-owned dictionary keyed by `TickParamDef` reference identity, never value equality. `emittedValue` and `emittedCache` must equal the actual validated XML node's decoded `value` and `cache` attributes as exact strings. The `cache` attribute must be present. Parsed constant-value equality is insufficient because distinct XML strings can compile to the same Number. Reading a script's stored text is passive, but it never substitutes for the XML attribute comparison above; evaluating a script to validate a map is forbidden.

`mode` matches the compiled definition: `literal` exactly when its cache is none; otherwise `evaluated`. `sourceRefKey` equals the required action, behaviour or child map entry at that location, and its first pipe-separated field equals the map's `imageSet`. An absent required entry refuses the map, including an absent `children` entry for a child with a Sound binding. `sourceRefKey` need not be unique across rows.

**(v1.4) P1-E Coverage and refusals.** Rows form a bijection with every compiled Sound definition in the three container kinds, including unchanged, unselected, unreachable and non-Mute sites. Inventory by node id includes definitions shadowed by name lookup. No row represents absent Sound.

Before opening the trace, refuse with exit 2 and no trace for each of the following:

- `soundParameters` absent or not an array, or a row that is not an object;
- repeated, unknown or missing row properties;
- `childIndex` present on a non-child row or absent on a child row;
- `sourceValue` or `sourceKind` present on an evaluated row or absent on a literal row;
- any required string field that is not a string;
- a noncanonical, negative or out-of-range `nodeId` or `childIndex`;
- `kind`, `mode`, `emittedCache` or `sourceKind` outside its closed set, or a mode/cache inconsistency;
- duplicate locations, counting `(kind, nodeId, childIndex when applicable)`;
- a location naming no compiled container or one without Sound, which is surplus;
- a compiled Sound definition consumed by no row, which is missing;
- emitted value/cache, source refKey, image-set or source-kind disagreement;
- an unknown top-level map/3 property.

The allowed top-level map/3 properties are the map/2 properties `schema`, `imageSet`, `frames`, `actions`, `children`, `behaviours`, optional `sourceNames`, and `poses`, plus `soundParameters`. `schema` must be exactly `shimeji-parity-map/3`; existing map/2 requirements govern the presence and contents of the other retained properties. Unknown-property refusal is new for map/3; duplicate-root checks already exist.

**(v1.4) P1-F Retirement and readers.** A new host parity run reads map/3 only. Refuse map/1 or map/2 before opening a trace, exit 2 and no trace, with exactly the following diagnostic, where N is the refused file's map version:

`the map is shimeji-parity-map/N; regenerate it with the current converter (map/3 required)`

For an admitted scenario/2 candidate this is a LoadPets refusal after scenario and pack admission. Regeneration runs the converter on the source pack into a new output folder; hand-editing a map/2 file, including changing its schema string, is not migration. Archived traces, verdicts, revision H's P2 calibration packet and other frozen packets retain their map/2 manifests and are reproducible only with their original build. Every map reader, including validation probes and self-test readers, moves in one integration change; no map reader accepts two map versions. The comparer's grading code is unchanged; P3-D adds one pair to compare.py's self_test, changing its bytes and hash.

**(v1.4) P1-G Product neutrality.** Emitted animations.xml is byte-identical to the base converter's output for every existing converter self-test input and every make_packs pack that the base converter converts; without an injected defect the new converter refuses none of them. Sound provenance adds no PackConversion refusal when IncludeParityMaps is false. A converter provenance defect is confined to conversions requesting maps: the public API then returns a refused member naming the defect, with no EmittedXml, and ConvertPack refuses before writing any output. No-map conversion, underlying XML generation and TickSound arguments remain unchanged.

The existing TICK_SOUND_MUTE_OBSERVED conversion refusal remains. Controls whose input reads Sound in a script use members with no normalizing Mute literal; normalizing-literal controls use members whose scripts do not read Sound. C15 separately proves the no-map path and the actual public API's map-defect disposition.

### 9.2 Mute source-intent payload

**(v1.4) P3-A Intent payload.** P3 is an explicit version exception recorded in the adopted preamble; its date and signatures belong to that version's signature record. Retain trace/2 and scenario/1, and scenario/2, with fields unchanged. mute.clip is a name-bearing payload under section 9. For a qualified P2-C read it is the String returned by the source Sound accessor for that activation, in source spelling, including the empty string; a qualified literal read records that String from the bound sourceValue. For evaluated String, the host writes its own evaluation of the lowered script; equality with the source accessor is what the fresh pair tests, not what the host asserts. Null is written only for the actual all-clips branch with current image-set Sounds on.

This is intent, not cache-key, tier or stop evidence. Non-qualified values preserve the actual host result under P2-C without a source-intent claim and are graded ordinarily. No policy exception hides a mismatch.

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

8. **(v1.3)** Section 11 adds a finite registered scenario/2 exception to dispatch, optional engine binding, policy grading, result metadata and proof obligations, deliberately retaining trace/2 and map/2 wire versions. (v1.4) Superseded for new host parity runs by map/3 (9.1). This exception does not fold policy fields into the trace/2 wire definition.

9. (v1.4) mute.clip has the source-intent meaning of 9.2, for trace/2 only; a pre-v1.4 host trace/2 artifact retains its archived meaning.

(v1.4) Everything else in sections 1 to 9, as amended, applies to trace/2 unchanged. A trace/1 pair is judged under v1.1 exactly as
before, including the exact comparison of error-event messages; it is never re-judged under v1.2 without fresh
traces. Both runtimes regenerate their traces to obtain a trace/2 verdict.

**(v1.3)** The preceding unchanged-sections and trace/1 statements are subject to section 11's explicit finite exceptions, the separately adopted screens clarification and any later explicitly marked amendment; valid historical legacy calls without the new engine option or a registered scenario/2 claim retain their ordinary results.

### 10.2 Version declaration, mixed pairs and dispatch

**(v1.3)** In addition to ordinary schema recognition below, section 11.2 recognizes a registered scenario SHA in either readable recognized trace/1 or trace/2 header. This is registry recognition, not a new trace-version field. Section 11.3's slot 2 refuses an identified scenario/2 claim carried by a trace/1 pair as scenario-invalid/scenario-version-incompatible. Missing-file and artifact requirements follow its ordered slots. Non-identified legacy dispatch remains unchanged.

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

**(v1.3)** For registered scenario/2, section 11.3's footprint-first primary-class exception precedes the residual event/state walk and aligned-unknown rule below. It removes only the exact individually proved observations in the admitted model, keeps every error and all common state, and retains the original-body outcome as secondary raw evidence when required. Other comparisons keep this section unchanged.

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

**(v1.3)** The ordinary order below remains for non-identified legacy calls. Section 11.3 is the complete order for an identified registered scenario/2 branch, with the explicit all-primary read/usage exceptions of 11.2. Static scenario admission is at slot 1, version incompatibility at slot 2, and trace-derived inactive-target validation at slot 11. Trace/1 identification cannot evade the version refusal. Earlier readable artifact mismatches retain priority over deferred unavailable-artifact requirements, and footprint-first is a narrow primary-class exception rather than a global reordering of existing loader checks.

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

**(v1.3)** Section 11 adds policy-blind run-owned window/population reconciliation, actual loaded-engine and write-recorded birth-configuration witnesses, self-hash refusal handling, single retained scenario/verified pack-copy obligations, finite admission and registry-integrity controls. They run in both trace modes as specified and retain the distinction between producer diagnostics, trace records and comparer results. Section 11's named producer refusals, reconciliation footer status and write-recorded cap limit are explicit exceptions to the instrumentation rule below.

**(v1.3)** Section 11 ownership: Host owns the strict interpreting reader/admission integration, exact embedded registry resource and drift proof, loaded engine and birth witnesses, A14/A15 ledgers and host controls. Codex owns the registry/schema/input authoring, shared scanner vectors, oracle byte-admission/verified-copy/typed-refusal changes, comparer engine binding/policy grading and closure checker. Registry finalization and final signatures are joint. No shipped test-only admission-table override is permitted. The full obligations and required producer controls are in 11.1, 11.5, 11.7 and 11.8; the ordinary typed-error rules below otherwise remain.

Both producers:

- write `shimeji-parity-trace/2` in the header and `tapeExhausted` in every footer, trace-off included;
- classify by 10.3.3 and 10.3.4, and write `unclassified` wherever no typed cause with its operands is available;
- continue past a tape's end on `0.5` (10.4, 10.5.1);
- Instrumentation must preserve existing exception type/catch behavior and state-write order, except the explicitly agreed RNG continuation rule and (v1.4) the host's private observation-only fault counter (P2-A) and its observation catches (FC-2), which also cover the existing Mute writer.

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

**(v1.4) P2-A Single read and passive capture.** TickMute.Apply reads Sound through one helper preserving EvalString's engine steps: Present, followed by one Read when present. Alongside the unchanged engine string, retain from that same read the selection (absent when Present is false, local, or the exact winning TickParamDef from merged own-then-incoming definitions), and the ScriptValue kind before ToDisplayString.

Also retain whether a fault was reported through the reading action's own script context between the start of Present and the end of Read. Use a private observation-only counter for faults reported through that context; the host chooses its location. That counter is the one permitted added state write and changes no engine-visible state. Faults reported before Present, including during Condition, never mark a later read as faulted. Faults reported through other contexts do not mark this read as faulted. Snapshot operations themselves are observation code; a snapshot exception is handled under FC-2, without skipping or catching the engine's Present/Read.

Selection uses a passive view of the same TickVars state, no second Get or Has. The helper adds no evaluation, cache write, settings read, draw, script call site, Clip call or TickSound call. Observer delivery follows P2-B; no second callback is added on a branch that already delivers Muted. Engine-side observation code may consult one test-only injection point per FC-2 site. It is null in every product and parity build unless a host self-test installs it, does no engine work, and exists only so C18 can raise an exception inside that site. Cached and fresh reads keep their existing cache behavior. A Read that throws is not a completed read and propagates exactly as before; no observation catch surrounds engine Present/Read.

**(v1.4) P2-B Activation, owner and per-read delivery.** The Mute action object is the activation. Use its retained `OwnerType` for provenance, never the companion's current Type or PoseType. Correlation is per read, not per object: a looping Sequence can reinitialize the same child object.

Each completed Mute read reaches the observer exactly once, correlated with its synchronous emission when there is one. The existing named and null-with-Sounds-on Muted callbacks carry the read record on event branches; ITickObserver.MuteReadNoEvent carries it on the Sounds-off null branch. Delivery validates identity/provenance and records per-read information without creating a public event on the no-event branch. The existing actual Muted call emits its public Mute line, if any, using that same read record. Process each read once, with no stale record carried into the next initialization, and add no settings read.

Only the existing named branch and null-with-Sounds-on branch request a public Mute event. Their Muted observer callbacks remain before their respective TickSound call. No callback or information record turns a no-event branch into an event. TickHost's Muted and MuteReadNoEvent observer behavior remains a no-op.

**(v1.4) P2-C Disposition of each Mute read.** First validate selection and its required binding for every completed read. Absent means Present was false; missing OwnerType or owner map alone is not a defect for this selection, which follows its existing null branch. For a non-absent selection, local, missing OwnerType/map, an unbound winning definition or an inconsistent retained row invokes FC-2: no public Mute line, only provenance disposition, while the engine still performs its unchanged read and branch.

An inconsistent row has mode disagreeing with the winner's cache (literal with action/frame, evaluated with none), or a literal row lacking retained sourceValue/sourceKind. SourceKind versus compiled literal kind, or emittedValue versus display text, is never an inconsistency.

A qualified read has valid selection, no fault reported during it, and either a literal winner with sourceKind string or an evaluated winner of kind String. Other valid-selection reads are non-qualified; absent/Null reads make no String claim. Payload handling follows the actual branch; information can apply even when no Mute event is emitted. Exactly one table row applies to each valid-selection read; the fault rule below is orthogonal to the rows.

| Read | Payload and information |
|---|---|
| absent | Existing null branch: mute null only with current-type Sounds on; no Mute line with it off |
| literal sourceKind string | Exact sourceValue, regardless of compiled host kind |
| literal sourceKind number or boolean | Engine display text; MUTE_VALUE_NOT_STRING with suffix `literal number` or `literal boolean` |
| evaluated kind String | Actual evaluated lowered-script text, including a value cached by an earlier read in the same activation |
| evaluated kind Null | Existing null branch |
| evaluated Number, Boolean or Point | Engine display text; MUTE_VALUE_NOT_STRING suffix `evaluated number`, `evaluated boolean` or `evaluated point` |

For a valid-selection read with a reported fault, preserve its actual result and branch and log `MUTE_VALUE_FAULTED tick <t> <sid>` once. Also log NOT_STRING when the evaluated non-null result is Number, Boolean or Point. Literal NOT_STRING is instead determined by sourceKind: a lowered String for numeric Infinity logs it, while source String /1 compiled as Number does not. A fault flag never forces null.

A read handled under FC-2, whether for a P2-C provenance defect or an observation exception, records only provenance, with no MUTE_VALUE_FAULTED or MUTE_VALUE_NOT_STRING information, because its classification inputs are not trusted. Engine fault events remain unchanged. Only provenance defects invoke FC-2/FC-3. Neither non-String nor faulted values set ProvenanceFault, status or exit code. No qualified source String is replaced by canonical text and no named value is replaced by null. These records preserve host behavior; they do not claim that product coercion was fixed.

**(v1.4) P2-D Witness corrections and product leads.** The oracle's failed script or cast reaches no successful Mute intent hook. A host script fault can return Null and, with Sounds on, produce the actual error event then mute null and MuteAll. Preserve this divergence rather than turning it into a producer failure or erasing its event; when no earlier section-10.7 rule applies, ordinary comparison grades it fail at that tick, recorded as L3. Sounds-off null writes no Mute line. Two absent Mute lines do not establish successful Mute intent.

Zero added evaluations is measured against b107727c on the same scenario and tape, for full lifecycle draw and state sequences and for event sequences apart from mute payloads that 9.2 changes, in both observation modes. The proposed null expression is predicted to draw twice during Init (Condition then Apply), not twice over the full activation: later HasNext polls can read Condition again. Measure and retain the complete baseline rather than hard-coding a full-activation count. C7 is host-only because script sites remain unmapped for paired grading. It does not mean every Apply advances an evaluation counter once: Condition may have read Sound first, action/frame caching may reuse it, and a null value is not cached.

Retain the product leads separately: L1 is display-prefix drift after literal normalization; L2 is EvalString's non-String coercion; L3 is a faulted null read reaching MuteAll; L4 is the stale converter hex-literal comment; L5 is Unicode whitespace accepted by the host's general .NET trim path. This wire records those behaviors and closes none of them.

**(v1.4) P2-E Product behavior.** Capture is passive. TickSound receives exactly the previous argument; evaluation count, exceptions, cache effects and state-write order are unchanged apart from the private observation-only fault counter in P2-A. Restoration changes parity observation only. TickHost.Muted and TickHost.MuteReadNoEvent remain no-ops. This is neither a product audio repair nor a sound-stop equivalence claim.

**(v1.4) FC-1 Static refusals and precedence.** New P1-D/P1-E Sound row shape, unknown-top-level, coverage, binding and sourceKind faults are InvalidDataException map refusals inside LoadPets, after the existing pose and geometry checks. They log `refused <map path>: <reason>`, exit 2 and leave no trace file.

Preserve the existing parser, JSON syntax, duplicate-root and retained-field checks and their current precedence. The schema check keeps its position and changes only as P1-F states. Preserve the legacy pose/geometry order. New validation (Sound rows and the unknown-top-level check) is deferred until those existing checks have succeeded; retaining the raw Sound JSON for that later validation is permitted. This ordering does not move an existing parser or schema refusal behind Sound validation. An admitted scenario/2 slot order is unchanged.

**(v1.4) FC-2 Runtime provenance faults.** For a completed read with a P2-C provenance defect, including a no-event read:

1. Write no Mute line for that read: no canonical value, source guess or null.
2. Retain `MUTE_PROVENANCE_MISSING tick <t> <sid>: <reason>` as the writer's first Mute fault if none is retained yet.
3. Set ParityTraceWriter.ProvenanceFault to it only if that property is null.

The engine still performs the unchanged Present/Read and TickSound branch and continues. No exception raised by observation code, including either fault-count snapshot, passive selection, callback, identity lookup, source-value lookup or Mute writer, may leave observation code. Catch it where it arises and apply steps 1-3 with reason `observer exception <type>: <message>`. If it occurs before the engine read, retain the defect and still perform that read and branch unchanged. Never catch an exception from the engine's own Present or Read; it propagates as before and is not a completed observation.

An observation exception reaching TickMascot.Tick or the startup wrapper would create an internal-fault product event and dispose the companion while status could stay complete; that is forbidden. In a parity run, a caught observation exception without a retained defect is also forbidden. Under TickHost's no-op observer, an exception caught in engine-side observation code (either snapshot or passive selection) is discarded and changes no product behavior. A read handled under FC-2 emits neither information token (P2-C).

**(v1.4) FC-3 Finishing both trace modes.** ParityRun.Finish remains the only ending in both modes. Non-null ProvenanceFault sets footer status error and logs `parity trace: <ProvenanceFault>`. If the first Mute fault differs from ProvenanceFault because an earlier fault won, also log `parity trace: <first Mute fault>`. Then emit the information lines under FC-6, write the footer and return exit 1 for the provenance error. The first provenance fault and first Mute provenance fault are both visible, not every later fault of a kind already logged. Any provenance fault makes status error. On a mid-run scenario-invalid path the footer then says error and the run still exits 2.

**(v1.4) FC-4 Existing wire only.** Add no exit code, footer member, trace field, event kind or section-10.3.2 error code. MUTE_PROVENANCE_MISSING is a host log token only, never a footer member or extension at any status. Unknown footer extensions are ignored by comparison and one beside complete would read as success. It is never an error event: producers do not manufacture product fault events.

**(v1.4) FC-5 Ordering and monotonicity.** Map refusals precede the trace and retain exit 2. Scenario-invalid retains exit 2. A run in error remains in error; nothing changes it back to complete. The unchanged comparer grades status error as `unjudgeable` / `aborted` before event comparison. Required provenance faults are not hidden as unclassified typed errors.

**(v1.4) FC-6 Information occurrences.** For valid-selection reads not handled under FC-2, retain MUTE_VALUE_NOT_STRING and MUTE_VALUE_FAULTED once per applicable occurrence with tick and sid, in occurrence order, including no-event reads. A read handled under FC-2 retains only provenance, and any information already formed for it is discarded. The exact NOT_STRING suffix set is `literal number`, `literal boolean`, `evaluated number`, `evaluated boolean`, `evaluated point`. Each line is exactly `MUTE_VALUE_NOT_STRING tick <t> <sid>: <suffix>` or `MUTE_VALUE_FAULTED tick <t> <sid>`.

Finish logs each retained information record as `<line> (information)`, after all applicable `parity trace:` lines and the existing rng-tape-exhausted information line, and before the footer, on every ending and both trace modes. Information never changes status, exit code or ProvenanceFault. Trace-off forms the same passive checks and information records while withholding public event lines.

Oracle (Codex): writes trace/2; classifies through hooks that carry stored action identity (an initialized action's
own identity, and explicit builder/reference context for a constructor fault), and writes `unclassified` where that
context is absent; labels the same seven wrapper sites where its runtime catches a fault; keeps its current startup
outcome (10.3.4, 10.6); writes `tapeExhausted` in every footer and keeps `unobservedDraws` and `unobservedEvents` on
trace-off footers.

**(v1.4) P3-B No new diagnostic events.** The existing `sound` event row is unchanged. Neither producer writes sound events from Mute in this step. Optional O2-after through O5 diagnostics, O8 completion, observer-defect ledgers and MUTE_* information belong in producer logs or result records. They never become trace lines, footer members or event kinds. An observer defect ends its producer with status aborted or error under the existing rules, including for trace-off output.

Required oracle hooks are O1, O2-before, O6, O7 and O8. O2-before observes named intent using the already evaluated local, including empty string. O7 observes null only after the Sounds guard admits the all-clips branch. O8 proves completion, including a Sounds-off null no-op. Attribute by exact activation VariableMap identity, never a second parameter evaluation, a current-member guess or a Clip-method call. New hooks must be non-throwing and record named defects that are checked before any successful footer, including exceptional source exits. The oracle logs each such defect with the token `MUTE_OBSERVER_DEFECT` and its reason, writes footer status aborted or error, and exits 1, its existing non-success exit. Lift only the supported Mute-entry refusal as these seams and controls land; retain the independent Sounds-on pose-audio refusal.

Hook O1-O8 definitions are the Mute seam anchors: ANCHORS.json SHA-256 db96982b3684e8f751746988c29479ffcf96af18f0dc826c4bf43c8011ca5d5b (authoritative), with ANCHORS.md SHA-256 8dea913a3b0f8d15c88648ea00c64fa0eb5ab8d62b08b2effa2123566db3e8e6, Part B, or a later revision both owners explicitly name by hash. O1 captures the evaluated local; O2-before observes named intent; O2-after/O3/O4 diagnose tiers 1/2/3; O5 diagnoses the guarded stop; O6 marks null-branch entry before Sounds; O7 marks its admitted all-stop; O8 marks normal completion. Control ids W1-W8 below are not hook ids.

Comparer (Codex): 10.2, the shape rules of 10.3.1 and 10.3.2 (duplicate properties included), 10.3.5, 10.4, 10.5,
10.6 and 10.7, the trace-off refusal, and unchanged trace/1 and free-running dispatch, with self-test cases that each
fail with exactly their own class.

**(v1.4) P3-D No regrade or policy.** Keep compare.py's grading code unchanged; only self_test gains one pair that grades fail with class `event-mismatch` for `/TO` versus `to`. That changes compare.py's bytes/hash, bound as actually loaded in closure evidence.

### 10.9 Closure

**(v1.3)** The finite registered policy exception and its fresh scenario/2 closure evidence in section 11 are separate from the three trace/2 fixtures below. N-parity-1107-03, O-SCENARIO-BYTES, O-PACK-BYTES and the inherited tooling follow-ups remain open until their respective named evidence/dispositions exist. For section 11 policy closure, a signature agrees specification bytes; neither it nor an admissible comparer result alone proves producer execution. The three trace/2 fixtures below keep their v1.2 closure rule. No old trace is regraded or counted as new closure evidence.

- N-parity-1107-01 and -02 close when fresh paired trace/2 runs of S-breed-blankbehavior-01 and
  S-transform-blankbehavior-01 compare `exact`, each with an `error` event of code `behavior-missing`, `imageSet`
  the member searched (10.3.3), `behavior` `""` and `via` `["birth"]` and `["transform"]` respectively.
- N-parity-1107-04 closes when a fresh paired trace/2 run of a scenario whose tape is one draw short grades
  `fail` with class `rng-tape-exhausted`, with both footers `complete` and `tapeExhausted` true.
- Historical v1 and v1.1 verdicts keep their input snapshot and are not re-graded.
- Do not add an unvalidated broad policy exemption while N-parity-1107-03 remains open.

**(v1.4) P3-C Fresh evidence.** Mute-intent evidence is citable only from a fresh trace/2 pair whose comparison supplies and matches the host EXE, host engine and oracle JAR. `buildWitness.host.artifactBound` must be true under H's engine-inclusive meaning for a call supplying the engine, and `buildWitness.oracle.artifactBound` must be true; EXE-only host binding does not qualify. Producer evidence names the actually loaded DLL path and hash, equal to the graded engineSha256, for the build containing adopted map/3 and P2-A through P2-E. The oracle JAR contains required O1/O2-before/O6/O7/O8 hooks. This requirement is prospective on H's engine-binding adoption; it cannot be satisfied by the base comparer saying only EXE-bound.

A pre-adoption host trace/2 keeps its archived meaning, is never regraded and is not Mute-intent evidence. The host writes trace/2 only; no host trace/1 meaning is defined and the oracle trace/1 Mute refusal stays.

**(v1.4) P3-D No regrade or policy.** Require fresh trace/2 pairs meeting P3-C from new scenario/1 Mute fixtures with no policy member. No archive is regraded and no policy/exemption is created, consistent with 10.9's N-parity-1107-03 rule.

**(v1.4) P3-E Registry identity.** Adding Mute sections to make_packs.py must leave the four registered scenario/pack byte sets and all five calibration-control digests byte-identical on regeneration. Demonstrate that by hash comparison. Change no registry bytes. The frozen generator reference and policy packets remain historical artifacts; a new generator build does not rewrite them.

## 11. Registered policy scenarios (v1.3)

Adoption of this section is specification agreement. A comparer report with qualified=true and exit 0 is a per-invocation grading verdict. Feature qualification of a case is a reviewed closure disposition binding the evidence named here; adoption qualifies no case.

**Source notice.** The inherited H clauses below are copied without prose compression. Their subsection headings are numbered, and the one declared F11 editorial replacement changes H:112 from saying the four pairs qualify to saying they are eligible for qualification. The frozen revision H declaration, SHA-256 c3c261fdd41c984d437a4b3e84c4128f53503425eb4cff297605f64969631886, is kept unchanged beside this file as CONTRACT-v1.3-policy-H.md and preserves the complete source, including its original status/placement history; in section 11, H:n means line n of that file. Source-local relative paths and historical pending/status statements refer to that frozen H declaration unless an exact repository path is stated; 11.10 below identifies the installed payload. This does not silently promote historical P1/P2 calibration into execution or closure.

### 11.1 Declaration, registry and input admission

Scenario schema is shimeji-parity-scenario/2. Add one required policy object with exactly three string members, defined by schema/policy-declaration.schema.json:

```json
{"registry":"shimeji-parity-policy/1","id":"birth-cap","case":"cap-at"}
```

| id | case | evidence label |
|---|---|---|
| birth-cap | cap-at | S-cap-at-01 |
| window-off-plays | window-carry-off | S-window-carry-off-01 |
| window-off-plays | window-throw-off | S-window-throw-off-01 |
| window-off-plays | window-minimize-off | S-window-minimize-off-01 |

One policy per scenario; no arrays of policies, masks, selectors, caller expected traces, exemption counts, tolerance or free-running policy case. Description and S-id are labels, never admission authority.

Only row.scenarioSha256 and row.packSha256 are admission/binding digests. legacyScenarioSha256, generatorSha256 and sourceRevision are provenance, never recognition keys. The entire scenario byte file is hashed, including schema and policy; no reserialization or excluded fields. Serialize scenario first, then place its digest in the registry. The scenario contains a registry version string, not the registry digest. The registry digest is in the signed packet, compiled-table proof and result metadata; there is no digest cycle.

Registry id/case selects the signed footprint model only after digest admission. scenarioId, pack and ticks describe the retained inputs and are checked against their decoded contents at registry build time; they are not alternate recognition keys. Existing header-versus-decoded-scenario id, pack and tick bindings remain required. qualificationStatus records readiness and never grants qualification. Changing any registry status/provenance bytes changes its signed digest and needs reviewed finalization.

Input admission order, shared by comparer and both producer readers:

Candidate scanning and hash admission precede the INTERPRETING Parse/ReadScenario and execution. A passive, non-routing parse of the SAME retained bytes may precede scanning solely for protective input/pack/output-path discovery. Its result, last-wins behavior or failure cannot select a schema, authorize admission, erase the scanner's completed marker, mutate inputs or become the execution document. A passive parse failure does not prevent the named scanner from running. In particular, a scalar-decoding failure on a lexically valid escaped-unpaired-surrogate pack string means no usable pack folder for protective discovery, not a refusal or an excuse to skip the candidate scanner. Handle that narrow getter failure at the protective parse boundary; unrelated internal exceptions remain defects. Output order is per producer: the host's initial existing-output claim precedes the drift test and scenario admission; its exclusive CreateNew open after admission is a second protection leg with the same refusal. The oracle's run-folder claim follows scenario and pack admission and precedes copying or writing case outputs. Both producers preserve every pre-existing output object and exit 2 when their occupied-output guard refuses. Output-path protection and the host command prerequisites may refuse before admission as specified below; these are producer diagnostics, not policy grades.

1. Read scenario bytes once. Compute the full digest and retain those same bytes for interpretation and execution setup. Never hash one scenario read and execute a later read. The verified execution-copy binding for packs is the separate named obligation O-PACK-BYTES below.
2. Use the shared CANDIDATE-SCAN profile below, inspecting immediate root-object members incrementally without collapsing duplicates. Remember a candidate only when the decoded key is exactly schema and its completed STRING value token is exactly shimeji-parity-scenario/2, ordinal and case-sensitive. No following delimiter or closing root brace is needed: a completed marker followed by EOF is a candidate, but an unfinished value string is not. Stop at the first profile failure, including encoding failure, and never recover into later text. Earlier completed markers survive later errors. Nested/object-valued/string-description lookalikes do not trigger it. A non-object root never produces a candidate, but is scanned under the same profile to determine its stop facts.
3. No marker: retain the legacy semantic reader, including its existing duplicate/last-wins, extensions, depth behavior and unsupported-input handling. A scanner error alone is NOT a new rejection of a legacy file the legacy reader accepts. Formerly exceptional scenario syntax/read/decode failures use a named usage/input diagnostic, exit 2, without a fabricated comparer grade. On the oracle these no-candidate input failures take the typed input-refused route below; the batch records the affected case and continues to later selected cases while retaining final exit 2. Existing oracle semantic validation failures already represented by compare.Unjudgeable keep their per-case status/detail row with trace null and the existing batch aggregation; they are not newly typed exit-2 refusals. Existing producer text such as scenario-invalid: the scenario is not JSON may remain for its applicable syntax failure; it is diagnostic text, not a section-6 comparer class. Actual trace-record JSON errors keep their existing missing-field route. No-candidate scan facts must not be mislabeled scenario-not-registered. The legacy semantic reader keeps its scenario/1-only schema test. If it decodes shimeji-parity-scenario/2 after a scanner stop or through last-wins, no producer trace, ordinary body comparison or runtime execution follows: the comparer keeps its unjudgeable scenario-invalid report and exit 2; the host keeps its scenario-invalid refusal and exit 2; the oracle keeps its per-case row status scenario-invalid with trace null (oracle.py:2422,2786-2787), with batch exit unchanged under existing aggregation. A host eager schema-string decoding failure instead gets a named usage/input diagnostic identifying that schema-string failure, exit 2 and no trace, never an unhandled InvalidOperationException. Only step 4's successful admission context permits the interpreting reader to accept scenario/2. These are the per-reader legacy outcomes referenced by the controls below; no ordinary legacy grade or no-candidate oracle row is relabeled as a new policy refusal.
4. Candidate: admit by the full scenario SHA FIRST. A miss is unjudgeable/scenario-invalid with predicate scenario-not-registered, exit 2. Both duplicate-schema orders (v2/v1 and v1/v2) take this path and fail the hash admission; neither can collapse back onto legacy dispatch. Do not select by an untrusted policy.id or S-id.
5. The admitted row identifies the exact approved bytes and tuple. Grammar and duplicate freedom are established by the unit/build integrity proof below. Interpret those bytes normally for setup, with the successful admission context allowing scenario/2; do not widen the no-candidate legacy schema test or add a defensive strict runtime grammar refusal behind the full-SHA gate. An impossible failure to interpret admitted bytes is an internal implementation/table defect, not scenario-invalid/declaration-grammar and not a claimed killable user-input guard.
6. For admitted candidates, run physical pack preflight, safe source-pack digest and registered-pack admission BEFORE LoadPets or runtime execution setup. The host may interpret the admitted scenario with its non-I/O ReadScenario reader to resolve the pack path; that interpretation performs no pet loading or engine execution. Links, containment and inventory defects retain pack-invalid. Compute the safe source digest once. A readable different pack gives unjudgeable/scenario-invalid, predicate pack-not-registered, exit 2, before trace reading/production. For an absent pack the comparer keeps its existing scenario-invalid input refusal; host and oracle retain their pack-invalid diagnostic spelling, with exit 2 on the new typed producer-input route and no section-6 grade. This does not claim that the old oracle batch already returned 2 for that case. The registered file plus changed pack never reaches footprint grading. Non-identified legacy load/pack ordering and unrelated batch aggregation remain unchanged.

There is one authoritative scenario admission and one pack admission site. Later trace-to-admitted-input comparisons are scenario-hash-mismatch and pack-hash-mismatch. Registered file B supplied for trace A is scenario-hash-mismatch, not scenario-not-registered. Changed description/formatting needs a reviewed registry row.

Shared scan profile: the lexical grammar is RFC 8259 JSON, over UTF-8 without a leading BOM. No comments, trailing commas or bare NaN/Infinity/-Infinity tokens; numbers are recognized lexically, without float conversion or magnitude limits (1e100000 is a valid number token). Escapes follow the RFC code-unit grammar: a well-formed escaped unpaired surrogate is lexically accepted in names and values, including an immediate root member name, and is not rejected by an implementation's eager Unicode-scalar conversion. Decode escapes for the ASCII marker comparison without Unicode normalization; direct code-unit/ASCII comparison is preferred and no exception-based architecture is required. A decoded name containing an unpaired surrogate compares unequal to schema; a decoded value containing one compares unequal to the ASCII marker. Only an eager Unicode-scalar decoding failure caused by an already lexically validated, well-formed unpaired-surrogate escape is unequal rather than a scan stop. Malformed escapes still give the defined lexical stop; unrelated parser or internal exceptions remain implementation defects and must not be swallowed as unequal. These grammar facts follow [RFC 8259, sections 6-9](https://www.rfc-editor.org/rfc/rfc8259); the scanner-specific limits and routing below are explicit choices.

The chosen UTF-8 rule is LEXICAL-AT-OFFSET, not whole-file decode-first. Consume bytes in input order and validate every encountered raw UTF-8 scalar, including inside unrelated strings and names, before using that scalar in the JSON grammar or marker comparison. Do not silently replace ill-formed bytes or rely on lazy library decoding. Do not inspect later bytes after an earlier known JSON-profile failure merely to find a different encoding fault. Earlier completed markers survive later encoding errors. A root that is not an object is still scanned for stop facts. Count open arrays/objects with the root container at depth 1; allow at most 64. A bracket opens a container only where the JSON grammar permits a value to begin; opening container 65 there is a depth failure, while a bracket in any other position is lexical regardless of depth. Strings/names add no depth. These are scanner choices, not retroactive global legacy-validation limits. In Python, neither json.loads on bytes nor a whole-file bytes.decode supplies these incremental facts.

A scan result has exactly one reason: bom, utf8, lexical, depth, eof or none. Offsets are absolute zero-based UTF-8 byte offsets, never character indices or runtime token-start defaults. The following rules define the reason and offset, with the explicit encoding/BOM/depth rules taking precedence over the general lexical-offset description at the same consumed position. An initial complete EF BB BF sequence is bom at 0. An encountered ill-formed raw UTF-8 subsequence is utf8 at the first byte of its first maximal subpart (Unicode section 3.9, D93b), the byte where decoding of the current scalar began; Table 3-7 defines the well-formed sequences. This includes encoded surrogates, overlongs, values above U+10FFFF and a multibyte sequence truncated at EOF. The utf8 offset is that subpart start even if rejection becomes knowable only at a continuation byte or EOF. Decoding the scalar that begins at a consumed position, through its continuation bytes or EOF, is part of consuming that position, not inspecting text beyond an earlier stop. It is deliberately not the longest-extendable-prefix offset. A well-formed non-ASCII scalar in a position where JSON requires an ASCII syntax/token character is lexical at that scalar's first byte. Opening container 65 is depth at its opening byte only where the grammar permits a value to begin. A [ or { in any other position is lexical at that byte, whatever the current depth; no forbidden bracket opens or increments a container. For all other lexical/structural failures, report lexical at the first byte that cannot extend the consumed prefix to a document valid under this profile: NaN at N, -Infinity at I, and 01 at the second digit. If otherwise well-formed UTF-8 ends before a complete root value, including empty or whitespace-only input, or with a JSON token/container still incomplete, report eof at input length. A complete document followed only by allowed JSON whitespace reports none at input length, including that whitespace. After a complete root value, validate the next encountered scalar as above: ill-formed bytes report utf8; a well-formed non-whitespace scalar reports lexical at its first byte. No later candidate can be discovered after either stop.

The shared test seam returns candidate, stop reason and byte offset; these are internal facts, not trace fields or new comparer classes. Python/.NET parser defaults are not that seam's specification. Controls assert identical scan facts across all readers. With valid outer prerequisites, unoccupied guarded producer outputs and an intact registry/table, candidate integration cases assert full-byte scenario-not-registered. Under those same otherwise-valid conditions, no-candidate integration cases assert no policy-admission predicate and each reader's applicable legacy result/diagnostic; old human text need not match across runtimes. Earlier output-protection or registry-integrity refusals retain their specified outcomes and have separate controls. Where a reader decodes a scenario/2 schema after the scanner stopped, require its exact per-reader legacy schema outcome listed in step 3, including the oracle's trace-null per-case row and unchanged batch aggregation. No producer trace or ordinary body/runtime execution follows. A legacy scenario/1 document accepted previously stays accepted even if this candidate scanner stops on a NaN extension or deep unused value. Required scanner facts include raw invalid bytes after a complete root, truncated UTF-8 both at EOF and before a non-continuation byte, empty/whitespace-only EOF, grammar-forbidden brackets at the depth boundary, and the explicit none/input-length result for a complete document with trailing whitespace.

Grammar proof choice A2: unit/build integrity, explicitly not CLI mutation coverage. Behind full-SHA-first, permissive grammar mutations are subsumed by scenario-not-registered. Predicate names do not make that unreachable code execute. The pure grammar checks reject missing/extra/duplicate members at every depth, wrong types and crossed tuples. A source-bound registry build test strictly parses all four retained files, verifies their base/declaration grammar and tuple uniqueness, recomputes actual byte/pack hashes, and compares every compiled admission row and registry digest. Mutate each grammar guard against its named unit input; mutate a table hex digit, case, extra row and accidental legacy-hash recognition against named table tests. No test-only admission override ships. Only scenario-not-registered and pack-not-registered are new runtime static-admission predicates; declaration-grammar is a unit/build label. The registry build proof also sends all four exact admitted documents through the host's ordinary interpreting reader under admission context, exercising its additional screens and anchor requirements. An unexpected refusal of those fixed approved bytes is a named internal-defect failure, not a user-input scenario-invalid grade or a purported runtime grammar mutation kill.

The pure compare API must receive exact scenario bytes or an internal context derived from them, with an unambiguous pack path. A parsed dict plus a caller-asserted SHA is insufficient for scenario/2; do not reserialize it and call that the original hash. Legacy dict-based calls remain unchanged.

Named oracle obligation O-SCENARIO-BYTES (F5): Python retains the bytes it hashes. Before decode/parse, trace opening, hooks or runtime setup, Java reads its scenario byte array ONCE, hashes those bytes and compares with Python's retained expected digest passed as a bound invocation argument or through an independently verified immutable snapshot. Reading an unchecked mutable header.json is not authority for that expected digest. After a match Java interprets that SAME array, never a later scenario read. A private snapshot is allowed only if write/read-back and the Java-read bytes are verified. A mismatch is the exact scenario-input-changed producer-input refusal with no trace. Python must recognize it before v1_trace_status can mask absent output as missing/truncated. The selected producer invocation returns 2 and records the input-refused phase/predicate; unrelated source refusals keep their existing meaning. The control swaps valid same-id/pack bytes changing ONLY description after Python hashes but before Java reads. A hash-bypass or later-reread mutant must fail the named witness, rather than being killed by a schema/id/pack guard. This is an unimplemented named obligation, not a claim of a current guard.

Named oracle obligation O-PACK-BYTES (G8): before claiming the case run folder, physical preflight and the safe source-pack digest establish the admitted manifest. After scenario and pack admission, exclusively claim the run folder, copy the preflighted pack and verify its complete path/byte inventory. Independently hash that verified execution copy and require equality with the already admitted packSha256 BEFORE Java, hooks or trace creation. Java then uses only that verified copy, never the live source pack. This permits the current copy helper's later source reads only behind the complete-copy equality check, not as unchecked execution bytes. A mismatch is the exact pack-input-changed producer-input refusal, exit 2, recorded with its phase/predicate before missing/truncated output handling, and no trace. The control changes a source-pack file after admission hashing but before copying while all other prerequisites pass; bypassing the copy-manifest equality must fail the named witness. This is an open implementation obligation, not an existing guard or a completed mutation run.

### 11.2 Artifact binding and unchanged trace wire

Scenario/2 requires three primary byte inputs: --oracle-artifact for build.artifactSha256; --host-exe for build.exeSha256; --host-engine for build.engineSha256. Preserve existing API artifacts keys oracle and host, and add host-engine. Never reinterpret host as a nested object. Engine presence is required only for admitted scenario/2, but compare its bytes whenever the new option/API key is explicitly supplied, including a non-candidate legacy call. Omission adds no legacy requirement. No existing caller supplied this new option.

Collect read outcomes for EVERY supplied primary: oracle JAR, host EXE and host engine, on identified and non-identified calls. Compare every readable artifact at slots 4/5 even if another is unreadable. Engine follows EXE; oracle-before-host order stays. Unreadable bytes are unavailable, never a mismatching hash or silently matched bytes. Earlier readable mismatches and checks through slot 8 retain priority. Compatible identified no-file calls then give slot 9; admitted scenario/2 calls with unavailable primaries give slot 10. On non-identified calls, remaining supplied read failures give named artifact-read-failed usage/input diagnostic, exit 2, after checks through slot 8 and before startup/run/body grading. Report all unavailable primaries in fixed oracle-JAR, host-EXE, host-engine order and name the first as primary. Thus unreadable EXE plus wrong readable engine is artifact-hash-mismatch at slot 5; unreadable EXE plus unreadable engine names EXE first; unreadable JAR plus wrong readable EXE still gives the earlier proved EXE mismatch. Omitted legacy artifacts create no read failure or new requirement. This extends the bounded diagnostic exception to formerly crashing legacy JAR/EXE reads, not valid graded results. Crashes remain comparer defects and inadmissible evidence.

The host implementation/rebuild witness is engineSha256, not the apphost hash or commit string. On scenario/2 results host artifactBound requires BOTH EXE and engine matched; oracle requires its JAR. On a legacy call with the new engine option, host artifactBound also requires EXE and engine matched; engine-only cannot imply EXE binding. Legacy calls omitting engine retain their old buildWitness meaning. Include per-artifact supplied/readable/matched fields, distinguishing omission from read failure. This binds bytes, not freshness or proof execution. Regrading frozen legacy evidence preserves verdict, class, first divergence, exit and the existing buildWitness field meanings/values; additive per-artifact metadata need not preserve serialized report bytes. Producer evidence must identify the actually loaded engine path/hash and executed named witness matching the graded engineSha256. Keep converter aggregate, runner/dependencies and converted pets/map manifests separately in closure evidence.

The host command's own EXE and loaded-engine self-hash reads are producer operations, separate from the comparer's supplied-artifact rule. A missing/unreadable own EXE or engine path, including null ProcessPath or empty Assembly.Location, gives a named build-witness-unreadable producer refusal, exit 2 and no trace, rather than an unhandled exception. Self-test runs log the actual loaded engine path/hash separately from their supplied synthetic build witness, label that witness synthetic, and are never closure evidence. No equality-or-refuse check against synthetic header hashes is claimed.

Trace/2 fields do not change. A registered scenario SHA in EITHER readable recognized trace/1 OR trace/2 header identifies the entry. Use SHA alone so changed pack headers cannot evade recognition. A trace/1 pair reaches slot 2's version refusal, not raw exact or the missing-file slot. A compatible trace/2 pair requires the scenario file at slot 9. Qualification still requires both input admissions and all trace bindings. Unregistered hashes never earn policy verdicts. The existing header cannot identify arbitrary forged/unregistered scenario versions; broader admission needs a new wire-version decision. Header-SHA recognition is a registry consultation and therefore requires the process-local drift self-test below before its first lookup, including calls without a supplied scenario file.

### 11.3 Complete scenario/2 precedence

This is an identified-scenario branch with the explicit all-primary read/usage exceptions above, not a global cleanup of legacy grading. Non-identified legacy loader order remains. Expected scenario input failures use the legacy semantic reader's accepted/refused distinction and named exit-2 diagnostics; trace-record syntax errors retain missing-field. Producer output-path protection is outside the graded slots. The host's initial occupied-output claim runs before drift/admission, and its post-admission CreateNew open is a second refusal leg; the oracle's exclusive run-folder claim follows scenario and pack admission. No deletion, truncation or replacement of a pre-existing producer output object is permitted. This trace-output rule does not cover the comparer's --json report, whose existing overwrite behavior is unchanged. Other output-path protection (an input folder at the trace path or a data-stream name) and host command prerequisites (source stamp, data root and converter) may refuse before admission, with no admission predicate asserted. Admission controls use a fresh trace path and valid prerequisites.

| slot | check and result |
|---|---|
| 1 | Supplied candidate bytes: scenario-invalid/scenario-not-registered; existing physical pack failures pack-invalid; readable registered-pack mismatch scenario-invalid/pack-not-registered. For an absent pack scenario-invalid is the comparer's existing input class; producer diagnostics retain their pack-invalid spelling and the new typed producer-input route exits 2 without a grade. Before trace reads. Grammar proof has the boundary stated above. |
| 2 | Header readability/recognized schemas missing-field; recognized mixed pair trace-version-mismatch; trace/2 trace-off artifact trace-off-not-comparable. Then, BOTH headers trace/1 AND either admitted supplied scenario/2 OR a registered SHA in either header => unjudgeable/scenario-invalid, predicate scenario-version-incompatible, exit 2. This applies whether header SHAs equal each other or the admitted file. No policy grading on trace/1. |
| 3 | Original own validity, oracle then host, with deferred run failures and each loader's internal order unchanged. source-pin-mismatch remains inside oracle validity. Validate original counters and records; this is not a newly sorted class-rank list. |
| 4 | Oracle supplied bindings: scenario SHA vs the supplied file's digest, admitted or not => scenario-hash-mismatch; pack SHA vs supplied digest => pack-hash-mismatch; supplied JAR vs header => artifact-hash-mismatch. |
| 5 | Host supplied bindings: scenario SHA; pack SHA; EXE; then new engine, with the corresponding existing hash-mismatch classes. Preserve the oracle-before-host loop. |
| 6 | Cross-trace scenario SHA or S-id disagreement => scenario-hash-mismatch; pack disagreement => pack-hash-mismatch. |
| 7 | Oracle/host RNG-spec disagreement or supplied-scenario RNG disagreement => scenario-hash-mismatch. This is input identity, not a draw-count failure. |
| 8 | Known footer tick disagreement or supplied N disagreement => tick-count-mismatch; retain the existing footerless proven range bound => malformed. |
| 9 | Registered SHA in either trace, no exact scenario input => policy-scenario-required, exit 2. Earlier supplied-artifact/cross-input/RNG/tick mismatches remain provable without a file. Provenance/legacy digests do not trigger this. |
| 10 | Admitted scenario/2, omitted or unreadable JAR, EXE or engine bytes => artifact-binding-required, exit 2. List every unavailable primary in oracle-JAR, host-EXE, host-engine order with its supplied/readable/matched states, distinguishing omission from read failure. Supplied readable wrong bytes have already failed. This precedes starter checks. |
| 11 | scenario-start-mismatch with 10.6 prefix rules, then trace-derived scenario-invalid for inactive input targets. Requires admitted scenario expectations. |
| 12 | Existing run failures truncated, timeout, aborted; status error maps to aborted. Exhaustion does not waive them. Keep the existing proven secondary exhaustion diagnostic. |
| 13 | rng-unexpected-draw, rng-tape-exhausted, rng-draw-count, then script-site-unmapped. No draw projection or occurrence renumbering. |
| 14 | Registry-owned delta footprint, excluding error presence alone. Any failed footprint => fail/policy-not-qualified (1), regardless of the tick/class of an event/state/error mismatch. No partial projection. |
| 15 | Valid footprint: project only its proved observations, retain every error and run the ordinary ordered residual event/state walk. Independent mismatch retains its class/exit. Otherwise aligned unknown => error-unclassified (2); otherwise any identical known errors => policy-not-qualified (1); otherwise positive divergence and exact residual => qualified policy success (0). |

Two new unjudgeable classes occupy slots 9 and 10 within section 10.7 step 3. scenario-invalid has three named placements: slot 1 static admission, slot 2 scenario-version-incompatible, and slot 11 trace-derived inactive-target validation. Static A3 refusal is never footprint grading. Bindings at slots 4-8 keep their relative order, including oracle artifact mismatch before host supplied-file mismatch. Legacy pack preflight remains unchanged on the legacy semantic branch. Wrong readable artifacts fail at slots 4/5; unavailable primaries follow deferred slot 9/10 or opted-in usage handling above.

Footprint-first explicitly amends the section 6 first-mismatching-tick promise and 10.3.5's independent-failure class promise for scenario/2 only. A policy-not-qualified report also carries the ordinary ORIGINAL-body result as secondary raw evidence. If raw comparison has a first divergence, retain its actual class/tick/sid/field; if raw bodies agree or are only inconclusive on an unknown, report that outcome with firstDivergence null. Never invent a raw mismatch tick. No external disable-policy/mask flag is introduced for this diagnostic.

### 11.4 Footprint models and wire types

Only the four retained byte/pack pairs are eligible for qualification. All are directed tape cases with one Alpha starter m1. The signed birth-cap model declares parity-engine MaximumCompanions 16 and classicPets 0, outside the product's shared classic/tick budget; this configuration is not observed in a trace. In this finite model classicPets 0 means no classic contribution to this parity world's birth budget, not a census of unrelated desktop pets. The required evidence binds the actual direct-world/no-override route and the values each birth decision used, as specified below. P2 calibrated an effective limit of 16 counting the starter and pending children: cap-below admits 15 births, and cap-at refuses attempts 16 and 17 while m1 plus 15 pending children are recorded. Successful birth-cap metadata records capScope {declared: {maximumCompanions: 16, classicPets: 0}, observed: "attempts 16 and 17 refused at population 16 (m1 plus 15 pending)", evidence: "producer-proof-required"}. Cap scope is a declared property of the signed model, not a comparer-certified producer configuration.

Cap candidate model: 20 ticks, LaySeventeen at [640,680], facing false. At tick 6, attempts/ordinals 1..15 each have forced child Sit entry then parent birth-request with the exact child sid, Alpha/Sit, [640,680] and false facing. Host then has two consecutive cap-refusal events after attempt 15's birth-request and before tick 6's state lines. Each has exactly the common event envelope type="event", tick=6, sid="m1", kind="birth-refused", plus child=null, attempt=16 then 17 and reason="cap"; there is no ordinal or other payload member. Source instead has the complete successful b16/b17 subtrees. Every accepted child is pending at 6, activates once at 7 and remains active through 20. Calibrated totals are 246/276 states and 50/54 events (host/oracle); there is exactly one m1 builtin:behavior-select draw at tick 7, value 0.5, occurrence 1, then next Sit, with both runs complete/nonexhausted. P1 statically supports source order, RNG and clone facts, and P2 confirms these emitted payloads and totals. No sorting exception.

The graded cap claim is no attributed child behavior/request/activation/state for refused host attempts, plus the separately bound producer proof of no hidden active/queued membership. It does NOT claim absence of tentative allocation, unsuccessful/uneventful parameter evaluation or arbitrary constructor activity. BornMascot/BornCount already occur before the cap guard. No unrelated birth, retry, disposal, descendant or non-error event is part of the admitted footprint.

P2 calibrates every accepted child's tick-6 state as pending=true, visible=false, pose=null, poseAnchor=null, origin=null, actionAge=0, action=Sit, actionClass=Stay, anchor=[640,680], lookRight=false, velocity=[0,0], disposing=false and vars={}. At tick 7 it is pending=false and visible=true, with pose image img/Alpha/shime6.png, imageRight=null, index=0, poseAnchor=[32,64], origin=[608,616] and actionAge=1. These are calibrated facts, not replacements for the grading rule: validate all comparable state fields of source b16/b17 against same-age source b1, except sid, including pending fields as actually observed. "Validated b1" means its original trace passed own validity, not yet cross-runtime comparison. Common b1 and all common companions remain in the residual. Every error stays outside the removable subtree. Only the exact two host refusals and individually proved source-only entry/request/activation records and 30 states may be projected. Error presence alone cannot invalidate the footprint; an unpaired error still has its ordinary residual presence/alignment failure.

Footprint tick, attempt, ordinal and required birth-request coordinates are JSON integers excluding bool and float spellings such as 16.0. Only window-move from/to coordinates follow the JSON integer rectangle model; window-minimize has no payload member. These are narrow qualification constraints; ordinary comparable fields retain their existing rules. A tick-float input already rejected by slot 3 is subsumed there, not counted as an independent footprint guard kill. Use a bool tick that reaches footprint validation for that guard's named killing input.

Window candidate models, all Throwing=true and host WindowActions=false, no type overrides/later timeline changes/input edges:

| case | required positive source effects | host and residual obligations |
|---|---|---|
| carry | 70 m1 moves, ticks 1..70, start [400,380,640,680], every from/to delta [-2,0,-2,0], first to [398,380,638,680], final [260,380,500,680]; the 70th source move is at tick 70 before that tick's draw and Carry-to-Stand event | full Move to x500; Carry states on ticks 0..69 (entered at tick 0, 70 states on both sides); Stand at 70; every companion field and all other events exact |
| throw | 10 m1 moves, ticks 1..10, start [300,200,600,500], each horizontal edge -32; vertical deltas -10,-10,-9,-9,-8,-8,-7,-7,-6,-6; final [-20,120,280,420] | ten animation ticks, stationary anchor [640,680], Stand at 11; exact residual |
| minimize | exactly one source event {type:"event", tick:6, sid:"m1", kind:"window-minimize"}, no payload member and no window-move; the visible active window [300,200,600,500] from tick 0 is bound scenario input activeWindow[0].rect, checked at registry build, not a record field | six animation ticks, stationary [900,680], Stand at 7; exact residual; any minimize payload member fails the footprint |

Each has one behavior-select draw at the respective Stand tick, occurrence 1/value 0.5; P1 supports model timing and P2 confirms the actual trace timing. Require complete exact positive source chains and zero host formed window records, then remove only those individually proved SOURCE effects. Remove no host record or companion state; no action-class/tolerance normalization. No positive divergence means policy-not-qualified even when raw bodies agree. Zero formed records is not proof of absence of silent modeled-window mutation; the independent producer proof remains required. Source has no host WindowActions switch.

S-window-throwing-off-01 is an unregistered calibration control: Throwing=false, WindowActions=true, carry pack, window [400,380,640,680]. These settings are bound by scenario SHA-256 36da6f2cdd19f1981b8b188864aa32e41c8787341a3468937546a62b914fc53a, independently regenerated from make_packs.py at 60480d4b7b221ebbfa8b9bf4ba5d34f975ce0362 (generator SHA-256 d47781d0a3ae9342e499466fdcc8609f26a0542d425cea3585c170094b1b96f8) and retained here as calibration-inputs/S-window-throwing-off-01.json. Both fresh P2 trace headers name that scenario digest; OBSERVATIONS.json does not itself contain the settings. This retained calibration input is provenance only, not an admission row or a new legacy baseline; no input or trace from the interrupted run is imported. Both sides enter Carry (forced) at tick 0, draw once at tick 0 (builtin:behavior-select, occurrence 1, value 0.5), and select Stand (next) at tick 0, with no window record; its ordinary raw result is exact. It controls only the WalkWithIE Throwing-off branch. The ThrowIE and MinimizeIE Throwing-off branches are not calibrated.

### 11.5 Policy-blind host proof obligations

The host proof runs inside ParityRun on every run, traced and trace-off, independent of policy declarations. All successful startup/step checkpoints, including tick 0 and N, are covered. Failure logs the first tick and sets footer status error; no new trace event or field is invented. Existing own-validity/run-failure priorities prevent success. A proof that never ran is not inferred merely from an absent log message.

Window reconciliation: read the private live TickDesktopWindow Shown, Minimised and Rect through an internal accessor, NEVER next-tick ActiveWindow. Keep a separate replay of timeline updates plus the exact window event records committed to EventSink; apply a timeline replacement only when an entry becomes effective, otherwise carry prior effects forward. After each checkpoint live state equals that replay. Independent per-tick/per-kind successful environment Move/Minimize counters, incremented at the actual success boundary, equal the corresponding committed-record counts. Do not derive environment counts from ITickObserver, serialized records or the callback being deleted. The observer-side ledger consumes the exact committed record and payload, not callback entry or a separate reconstruction of its arguments. Trace-off uses the same EventSink with physical output disabled, never footer.events (zero). This proves successful modeled effects were reconciled, not that failed calls were never attempted or state was never read.

Required named producer mutants: direct successful off-branch move without callback, including final-tick-only; removed successful move callback (TickWindowFamily at reviewed tip 20ca0682, lines 54/60 for move/minimize); suppressed actual window record formation/commit while retaining callback entry and successful environment mutation; two unobserved moves restoring the rectangle. A missing-window failed-call twin stays valid and increments no success counter. Before applying each committed move's to, its from must equal the replayed rectangle; preserve initial/default rectangle and Hide-keeps-Rect semantics. Independent successful Move/Minimize counters are separate from Show/Hide and observer callbacks. Payload replay catches wrong geometry; independent counts catch unobserved canceling effects. The rebuilt and actually loaded engine hash must match the graded engineSha256. Comparer metadata states a proof requirement, never producer-proved from a footer, missing error or absent record.

Population reconciliation has two independent sides at every completed startup/step checkpoint, including 0 and N, on scenario/1 and scenario/2 alike. The actual side reads PRIVATE active/pending containers through an internal snapshot accessor never used by the writer, retaining the (sid,pending) multiset and duplicate defects. The recorded side seeds exact unique starter SIDs from the RUN'S VALIDATED SCENARIO and replays actual lifecycle records COMMITTED TO EventSink in both modes. It never fabricates lifecycle records by enumerating States/world lists. EventSink remains active with physical output disabled. Checkpoints compare both multisets and uniqueness.

Startup seeds each run scenario row's exact SID, excluding an attempt-scoped tick-0 startup dispose-request for a failed starter that never joined; successful startup is active without invented birth-activate. Never derive IDs from successful-start count. A birth-request adds pending membership; birth-activate moves that existing pending SID active. A dispose-request on pending removes it immediately, with no later dispose required; on active it records an active-disposal request but leaves membership until dispose. An unknown/provisional dispose-request is a no-op with no tombstone, allowing provisional child SID reuse. Other error/behavior/birth-refused events create no member. Named inconsistencies are birth-request for an existing member, birth-activate for a non-pending SID, and dispose without an active member carrying an earlier active-disposal request. Do not fail an unknown dispose-request under the stated provisional rule.

In traced runs also compare the exact state records committed at each checkpoint, by (sid, pending) multiset, with lifecycle replay and private actual snapshot. Trace-off forms no intermediate state lines: lifecycle replay still runs at every checkpoint, and at N its actually emitted final-state records are also compared. A second enumeration of the public Mascots/Pending getters, footer counts or final counts alone is not an independent ledger.

Required both-mode mutants: queue a refused child while suppressing only lifecycle events, writer unchanged, including last-tick-only; filter a legitimate SID actually present at N in a public Mascots/Pending getter, keeping that filter effective through N and the private accessor unchanged; suppress/substitute a committed state SID/pending flag at the mode's emitted-state checkpoint; hide a last-tick pending child. Trace-off final-state comparison kills the persistent-through-N getter filter. An INTERMEDIATE-ONLY getter filter is a separate traced per-checkpoint or paired on/off case, not a promised per-run trace-off failure. Positive controls retain failed/successful arbitrary startup SIDs, pending disposal without manager dispose, active disposal waiting for dispose and provisional failed-child reuse. Duplicate startup rows remain input-invalid. Deriving startup IDs from successful count must fail. A14/A15 share the committed-record hook but retain independent obligations and witness names.

Existing on/off proof still recounts actual traced lines, withheld counters, final states/digest/exhaustion flags and requires complete paired runs. Every run logs the actually loaded engine path/hash inside ParityRun.Run, including self-tests; self-tests separately label their synthetic build witness and do not certify closure. A positive reconciliation witness line names executed proofs and checkpoint counts (N+1 on complete runs) from the ledger owned by ParityRun.Run, never merely from Finish or a supplied writer. Failure logs the first checkpoint and ends status error. Silence or byte matching does not establish proof execution.

### 11.6 Metadata, admissibility and CLI exits

Whenever supplied admitted bytes or either readable recognized trace/1 OR trace/2 header identifies a registered entry, include a policy block with registry name/digest, per-side candidate id/case and agreed entry when available, including slot-2 version refusals. Conflicting registered candidates certify refusal only. Legacy/provenance digests and S-ids confer no policy meaning. Before footprint evaluation: footprintEvaluated=false, footprintValid=false, qualified=false. Failed footprint: true/false/false. Valid footprint with residual/error failure: true/true/false. Successful canonical policy: true/true/true. qualified is true only for the actual canonical policy verdict.

The block is required on all identified-input graded outcomes through the slots above, including static and version refusals. If neither exact supplied input nor a readable recognized header identifies an entry, absent metadata is no policy claim. Unregistered-file static predicate details do not invent a registry row. Do not attempt body comparison for raw diagnostics on earlier invalid inputs. Usage diagnostics and implementation crashes are not fabricated grading results.

For registered scenario SHAs, policy-feature evidence requires the correct registry/digest/candidates/status fields; raw exact/exit 0 from older grading is inadmissible. Cross-candidate results certify refusal only. Success includes input digests, artifact facts, footprint findings, residualVerdict exact and hostProof: {noEffect: "producer-proof-required", engineSha256: <matched hash>, scope: ["window-effects", "active-pending-membership"]}; birth-cap additionally includes "cap-configuration" in scope and the declared/observed/proof-required capScope metadata defined in its model. This metadata is not a trace field or proof-execution assertion. No-effect/membership evidence additionally needs a closure record binding trace/input hashes, actually loaded engine, executed reconciliation witnesses/checkpoint line, and the positive complete INPUT-ADMITTED line with registry digest/id/case/scenarioSha256/packSha256. Closure also binds the positive registry-integrity line from the same process as its admission line, naming the registry digest and table-holding artifact hash; for the oracle bind oracle.py's tool hash and compare.py as actually loaded by the oracle beside the JAR, including whichever artifact holds its table or admission logic. Birth-cap closure further binds a configuration witness emitted by the same ParityRun.Run invocation as the loaded-engine line whose hash equals the graded engineSha256, not a trace field. TickWorld holds no provider: at the reviewed source its comparison limit MaximumCompanions is a public field initialized to 16, and any code can assign it, as TickHost.Step does (CompanionBudget declared at TickHost.cs:305, applied at :345) and unit tests do directly. The implementation makes the limit write-recorded: it becomes a property, and every assignment after construction is recorded with its value and writer context, including an assignment of the same value 16. Only the constructor's default initialization to 16 is exempt. If an explicit writer context is supplied, record it; otherwise record a direct/opaque writer rather than infer a caller or omit the write. A writer that exposes base and classic deduction separately also records them; an opaque provider such as the product's Func<int> CompanionBudget marks them unavailable. The write record belongs to the actual run world from construction and is not cleared at Run entry or shortened to its last write. At each birth decision, capture the limit, private active count and private pending count once as the operands of the actual HasRoomForBirth comparison. Associate those same operands and result with tick, parent sid and attempt at the breed-loop call site (TickRuntime.cs:993-994), rather than recomputing a population through public getters. The witness includes that decision identity, the compared operands/result and the limit's write record. Direct-world/no-override means zero recorded post-construction writes and the constructor default 16. Any recorded write fails the declared birth-cap closure scope, whatever its value and whether or not base and deduction are exposed. The product path folds classic pets before the comparison: TickHost.CompanionBudget (declared TickHost.cs:305, applied at :345) receives MAX_SHEEPS minus ClassicPetCount() (StartUp.cs:735). The compared limit alone cannot reveal those upstream inputs. ParityRun instead constructs TickWorld directly and installs no override. Evidence of that absence is the run's empty limit-write record; it is the model's classicPets 0 evidence, not a nonexistent classic count or a declared constant. The witness is logged on every run, policy-blind like the engine line; only birth-cap closure requires it to match direct-world/no-override, compared limit 16 and the expected progression below. The future test-only variant applies a budget-writer result with base 18 and classic deduction 2, leaving compared limit 16 and the observable cap footprint unchanged. That rebuilt and actually loaded variant must fail configuration evidence through its recorded limit write, and report base 18 and deduction 2 where its writer exposes them. A logger-only synthetic value is insufficient; a compared-limit-only witness mutant must fail the dedicated control. For S-cap-at-01 the expected progression is exactly 17 decisions, all at tick 6 for parent m1. Attempt k for k = 1..15 compares population k (1 active plus k-1 pending) against 16 and admits. Attempts 16 and 17 compare population 16 (1 active plus 15 pending) and refuse. No other birth decision occurs in ticks 0..20. The population logged is the value the comparison used, not a recount from public getters. This adds no production classic-pet support and claims no presently reachable CLI input. Missing execution/admission/integrity/configuration evidence leaves the corresponding claims unproved even when grading qualifies. On policy-not-qualified retain its predicate and raw outcome. Input-refused producer phase rows and registry-integrity internal-defect diagnostics are not fabricated comparer grades.

| result | exit |
|---|---|
| Existing exact, valid within-tolerance or time-share-pass on legacy paths | 0, unchanged |
| Canonical policy:<id> established by this invocation with the signed registry/digest and qualified=true | 0 |
| fail, pending, approximate, unsupported, not-exercised or any other non-success result | 1 |
| unjudgeable, including requirement classes, scenario-version-incompatible and error-unclassified | 2 |
| Named CLI usage/input error, no fabricated grade | 2; a crash/traceback is an implementation defect, never evidence of fail |

Policy success is not startswith("policy:"). Require the canonical matched entry and completed qualification; false/absent/mismatched policy metadata is never a success gate. The four registered cases cannot end exact/tolerance/time-share: trace/1 reaches version refusal, compatible trace/2 reaches qualification and a missing delta is policy-not-qualified. Update section 6 and the CLI docstring/exit implementation together. Legacy calls without the new engine option or a registered claim remain unchanged.

### 11.7 Required controls and killing mutations

These are future implementation obligations, not claimed executions. Unit-model checks in this packet prove only their explicitly labeled seam.

| controlled defects, with prior checks otherwise passing | required primary outcome / separate mutant |
|---|---|
| Admitted scenario/2 + both trace/1 headers, same SHA or different SHA | scenario-invalid/scenario-version-incompatible (2); v1.1 fallback and equal-SHA-only gate mutants |
| No file; registered SHA in oracle only, host only or both trace/1 headers | scenario-version-incompatible (2), required policy metadata; trace2-only/one-sided recognition mutants |
| Registered claim + mixed versions; or unreadable header + registered peer | trace-version-mismatch or missing-field respectively (2); version-refusal priority swap |
| Ordinary legacy pair + new correct/wrong engine; engine-only; omitted engine | ordinary result with accurate binding; wrong engine artifact-hash-mismatch (2); engine-only host artifactBound false; omission frozen; candidate-only engine comparison mutant |
| Non-identified call with unreadable JAR/EXE/engine singly and combined, otherwise valid | artifact-read-failed input diagnostic (2) after slot8/before startup; all unavailable reported in JAR/EXE/engine order; bare-open/early-error/silent-omission mutants |
| Unreadable EXE + wrong readable engine; unreadable JAR + wrong readable EXE; wrong cross binding + unavailable artifacts | artifact-hash-mismatch or earlier binding, not deferred I/O; short-circuit-read-failure mutant |
| Existing producer output with sentinel and valid admitted input; separate absent-output admission case; combined sentinel plus unregistered candidate | output-exists harness label (2) with unchanged sentinel; versus exact admission predicate (2), output absent. Combined case: host occupied-output refusal only, before drift/admission; oracle scenario-not-registered only, before its output guard. No case requires both predicates. Delete/overwrite and per-producer order-swap mutants must fail their respective witnesses. |
| Python-retained admitted scenario, description-only same-id/pack swap before Java read | scenario-input-changed producer refusal (2), phase/predicate recorded, no trace; skip Java-byte-hash/later-reread mutant |
| Source-pack file changes after admission digest but before execution copy | pack-input-changed producer refusal (2), selected phase/predicate, no trace; bypass copy-manifest equality mutant |
| Registry/table/resource drift before first scenario or header-SHA consultation; process A passes, then process B starts with an independently altered table/resource while an old positive cache is available | named registry-integrity internal-defect refusal (2), no grade/trace or scenario-not-registered; skip-test, lookup-before-test and cross-process-cache mutants. An intact new process reruns and positively logs its own check, rather than refusing. |
| Host own EXE/engine witness path missing or unreadable | build-witness-unreadable (2), no trace; bare-read/unhandled-self-hash mutant |
| Otherwise valid self-test supplies synthetic build hashes | normal self-test result with synthetic marker separate from the actual loaded-engine log; closure evidence remains ineligible; synthetic-as-real witness mutant |
| Rebuilt and loaded variant using admitted cap-at input with installed budget override base 18, classic deduction 2 and compared limit 16 | footprint remains unchanged; configuration evidence fails the declared direct-world/no-override 16/0 scope; compared-limit-only witness mutant must fail, through the recorded limit write; the variant's writer records base 18 and deduction 2 |
| Rebuilt and loaded variant that assigns MaximumCompanions = 18 - 2 directly in ParityRun.Run after construction, with no provider object, using admitted cap-at input | footprint remains unchanged; configuration evidence fails through the recorded limit write, including an equal-value 16 assignment; a construction-only, provider-object-only or equality-short-circuit witness mutant must fail |
| Registered trace/2 headers, unavailable engine + no file; admitted file + unavailable engine; readable wrong EXE as well | slot 9; slot 10; earlier artifact-hash-mismatch respectively (2); immediate-read-failure or silent-match mutant |
| Scan NaN/Infinity/-Infinity BEFORE marker; same tokens AFTER completed marker; lexical number 01 | lexical at N/I/I respectively (for -Infinity the I, not the minus); 01 at the second digit. No candidate before, remembered candidate after. No-candidate integration names each reader's exact applicable legacy outcome from step 3, including its schema outcome where it decodes scenario/2; widened-legacy-schema, permissive-dialect and whole-parse-only mutants |
| Scan raw invalid UTF-8 in unrelated string/key BEFORE versus AFTER marker; BOM prefix; raw encoding fault versus later bad escape; bytes after complete root | utf8 at the first byte of the first maximal subpart of the ill-formed subsequence (Unicode D93b), including encoded-surrogate/overlong/above-range/truncated sequences; no candidate before, remembered candidate after. Initial complete BOM gives bom/0. The mid-string bytes 7b2278223a22e282227d give utf8 at 6, not 8. After complete root, ill-formed bytes give utf8 and well-formed non-whitespace gives lexical. Preserve the earlier stop, never whole-file-decode; lazy/replacement/encoding-order/trailing-root mutants. No-candidate integration names the exact schema/decode/syntax outcome from step 3; the oracle's formerly uncaught decode/syntax input exception takes the typed exit-2 route below, while no-candidate compare.Unjudgeable rows keep their existing aggregation |
| Scan maximum container depth 64; value-position opening 65 before versus after marker; forbidden bracket at depth 64 | depth64 allowed; value-position opening65 gives depth at that opening byte; marker remembered only if earlier. Root container counts1. 64 open arrays followed by 1[ gives lexical at 65; 63 arrays followed by {[ gives lexical at 64 because the bracket is in member-name position. 64 arrays followed by another [ in value position gives depth at 64. No-candidate integration names each reader's exact applicable legacy result from step 3, including its schema outcome where it decodes scenario/2; depth-before-grammar/default-depth/uncapped/offs-by-one/widened-legacy-schema mutants |
| Complete marker value then EOF or junk; unfinished marker string; empty/whitespace-only input; complete document plus trailing whitespace | completed marker remains candidate through eof/input-length or the first later lexical/utf8 stop; unfinished marker string alone gives no candidate and eof/input-length. Empty input gives no candidate/eof at 0; two spaces (2020) give no candidate/eof at 2. A complete document including its trailing allowed whitespace gives none/input-length. No marker is discovered after an earlier stop; delimiter-required/parse-success-required/recover-past-error/empty-EOF/none-offset mutants |
| Huge valid exponent before marker; escaped unpaired surrogate in names and values, including equal-raw-length comparisons | lexical number/code-unit strings accepted. Root key "\uD800" has six raw bytes like schema: before a later marker it is unequal without stopping, and that marker yields candidate/scenario-not-registered. Schema value "shimeji-parity-scen\uD800" has the marker's 25 raw bytes: it yields no candidate and no stop. Cover both equal-length and different-length key/value cases; no float-range/eager-scalar-conversion stop; range/eager-key-decoding/eager-value-decoding/length-short-circuit-only mutants. No-candidate integration for the 25-raw-byte surrogate value requires the comparer's unjudgeable scenario-invalid report/exit 2, the oracle's per-case status scenario-invalid with trace null and unchanged batch aggregation, and the host's named schema-string usage/input decoding diagnostic/exit 2 with no trace, never an unhandled InvalidOperationException. A candidate document whose pack value is a well-formed escaped unpaired surrogate still reaches full-byte scenario-not-registered: protective pack-path parsing yields no usable folder and cannot skip the scanner. Add separate unhandled-eager-decoding mutants at the passive pack getter and the legacy schema getter. |
| Legacy scenario/1 accepted extension/deep unused value on a no-candidate route; decoded scenario/2 reached by that same legacy reader | accepted scenario/1's old semantic result unchanged; decoded scenario/2 keeps each reader's exact schema outcome listed in step 3, with no producer trace or ordinary body/runtime execution. The oracle keeps its trace-null per-case scenario-invalid row and existing batch aggregation. Making scanner profile a global legacy validator or widening the legacy schema test must fail |
| Malformed actual trace record versus a non-candidate scenario file rejected as syntax by its legacy reader | existing missing-field versus named usage/input error (2); blanket JSON-error handler mutant; neither may become traceback/graded fail |
| Changed declaration grammar bytes | CLI scenario-invalid/scenario-not-registered (2); separate pure grammar guard mutations, explicitly subsumed at CLI |
| Registered file + wrong readable disk pack + wrong EXE | static scenario-invalid/pack-not-registered (2); skip only registered pack admission |
| Pack link | pack-invalid (2); bypass physical preflight |
| No scenario file; registered SHA in both trace/2 headers; correct EXE/wrong engine, or wrong EXE | artifact-hash-mismatch (2); skip engine/EXE comparison separately |
| No scenario + cross scenario/pack/RNG-spec/tick disagreement | respective scenario-hash/pack-hash/scenario-hash/tick-count mismatch (2); move missing-file guard too early |
| No scenario + otherwise equal registered claims, artifacts absent too | policy-scenario-required (2); missing-file fallback |
| Supplied admitted scenario + each omitted JAR/EXE/engine + wrong starter | artifact-binding-required (2); allow shape-only or move absence after startup |
| Bound artifacts + wrong starter + timeout | scenario-start-mismatch (2); move run failure first |
| Bound startup + timeout or exhaustion + bad footprint | timeout (2) or rng-tape-exhausted (1); apply footprint too early |
| Bad footprint + earlier OR later typed/unknown/residual mismatch | policy-not-qualified (1), actual raw divergence secondary; prefer earliest raw tick/class |
| Valid cap footprint + aligned unclassified m1 error at tick 6 + exact residual | error-unclassified (2); count error presence as footprint defect |
| Valid footprint + identical known errors + exact residual | policy-not-qualified (1); skip zero-error eligibility |
| Valid footprint + identical known errors + independent residual mismatch | ordinary mismatch (1); return early on identical errors |
| Valid footprint + aligned unknown + independent later residual mismatch | ordinary mismatch (1); early-return unknown |
| No-divergence equal bodies | policy-not-qualified (1), raw exact/firstDivergence null; grant declaration-only success |
| Correct footprint + unrelated common state/event defect after delta | ordinary mismatch (1); bypass residual or blanket-filter source extra children/window kinds |
| Positive complete footprint + zero errors + exact residual | canonical qualified policy (0); unconditional failure or metadata mismatch |
| Legacy scenario/1 trace/2 SHA = legacyScenarioSha256, no file | frozen ordinary result; recognize a provenance digest |
| Trace/2 pair whose SHA is one of the five calibration-control digests, no file and no registered claim | frozen ordinary result; accidental calibration-control recognition mutant |
| Duplicate root schema orders, escaped key/value, nested/string lookalikes | exact candidate/legacy routing described above; collapsed, recursive or regex detector mutants |
| Forged policy prefix or absent/false/wrong-entry block | non-success and inadmissible; string-prefix success mutant |

Producer controls retain separate purposes. OUTPUT-PRESERVATION suite: otherwise valid admitted input, seed the producer's guarded output outside inputs/pack with known sentinel bytes. On the host seed the actual trace object named by the supplied trace-path argument and require the existing AlreadyThere diagnostic naming that argument as supplied, exit 2 and byte-identical contents. On the oracle seed the derived case run folder or its trace.jsonl and require the typed occupied-run-folder refusal naming that folder, exit 2 and preservation of every pre-existing sentinel. output-exists is the normalized harness/phase label, not a required literal token in the host product line. Host early preservation also asserts no drift, admission or INPUT-ADMITTED line. Cover its late CreateNew leg with an occupant introduced after admission but before open; that leg may follow INPUT-ADMITTED but writes no new header and preserves the occupant. Bind closure INPUT-ADMITTED only to a trace header written by that same process. ADMISSION suite: output initially absent, cover unregistered candidate, duplicate schema orders, escaped marker and registered input plus readable wrong pack; require exit 2, correct scenario-not-registered/pack-not-registered predicate and no new trace. Last-wins and skipped-pack mutants must fail their own predicate legs, not pass through another refusal. COMBINED ORDER suite: pre-seed output plus an unregistered candidate, with valid outer prerequisites and passive path discovery only where needed. Through the same producer caller path the host gives only its occupied-output refusal before drift/admission, while the oracle gives only scenario-not-registered before its run-folder guard; the sentinel is unchanged in both. These cases kill each producer's order-swap mutant. For admitted candidates pack hashing/admission precedes LoadPets. The concrete host control is wrong readable pack plus converted pets containing animations.xml but no parity-map.json, otherwise valid outer prerequisites; require pack-not-registered before the missing-map diagnostic. A LoadPets-before-pack mutant fails that witness. Remove the undefined producer wrong-primary leg; comparer artifact controls remain. The comparer's --json report is outside producer trace-preservation scope.

Oracle T8 obligation: it has no arbitrary --trace option. Capture/fix the newly allocated per-batch v1-<8 hex> root before the selected case. The guarded case output is <root>/<validated S-id> for traced mode and <root>/<validated S-id>-off for trace-off, through the real v1_run_directory helper; trace.jsonl, header.json and pack live beneath that case folder. After scenario and pack admission, claim this folder exclusively before copying the pack or writing header/trace; any existing occupant is a typed output refusal and is never silently reused. Creating case files must not replace an object that appeared after the claim. Seed the folder or its trace.jsonl for preservation. The harness supplies the full S-id to --only, remembers that --only is a substring filter, and asserts the exact selected/requested set; it does not assume the observation-proof or document-witness phases are disabled. No-trace assertions refer to the selected case's derived traced/off paths and mean no new trace, with pre-existing sentinels checked separately. Use an existing CLI selection plus a test seam, not a shipped admission override. Add a narrow typed refusal route from the first affected validation/registry phase through v1_run where applicable and the phase/result recorder to CLI exit 2 for scenario-not-registered, pack-not-registered, admitted-route pack-invalid (absent pack, link, containment or inventory), occupied-run-folder, scenario-input-changed and pack-input-changed refusals, for the no-candidate scenario syntax/read/decode input diagnostic (formerly an uncaught UnicodeDecodeError, JSONDecodeError or scenario-input read failure from v1_run, oracle.py:2420-2421), and for the registry-integrity internal-defect refusal. Record input cases as input-refused with their normalized predicate; record registry-integrity as an internal-defect diagnostic, never a fabricated grade. After a typed per-case input refusal, create no new trace for that case and continue to later selected cases; retain exit 2 through the remaining batch. Recognize these named routes before generic Refusal or missing/truncated-status handling. A recorded typed refusal keeps the batch's exit 2 even if a later observation-proof, document-witness or resource phase raises an unrelated Refusal. No-candidate oracle semantic rows described in step 3 are deliberately outside this new typed route and keep their existing aggregation. Other source/build/resource refusal semantics and aggregation remain unchanged. These are future producer controls with fresh artifact/witness evidence; the current existing-folder reuse and generic exit-1 Refusal behavior are not described as already fixed.

Per-footprint mutations also cover every cap attempt/reason/null child/ordinal/int type and exact refusal envelope/order; extra-child entry/activation and every pending/first-active pose, poseAnchor, origin, visible, actionAge and vars target above, while retaining full comparable-field b1 validation; and every window tick/sid, each move's from/to/count, the minimize's empty payload, unexpected host formed effect and skipped base action. Each actual CLI case asserts verdict/class/exit and the specific predicate/field. Producer mutants bind changed loaded engine bytes and named witness; reader table drift tests bind actual rebuilt binaries. A guard that is subsumed at CLI is labeled as such, never counted FIRED because another hash guard refused the input.

### 11.8 Host reader and evidence commitments

Host work lands on the integration lineage, at a common tip including the reviewed closure changes and adopted input clarifications. Old-reader controls are tip-specific: 35d9d1d's host silently accepts an unrecognized scenario schema; 31de0705's reader refuses it before trace output. Do not claim the former enforced a schema it never read.

Host computes/stores the scenario digest at the single retained byte read and reuses it for the header. Protection-only passive parsing/output claiming may precede routing scan; no interpreting Parse/ReadScenario or pets/runtime execution precedes required scenario admission. For admitted candidates the pure ReadScenario interpretation may resolve the pack path, then physical pack preflight, PackSha256 and registered-pack admission precede LoadPets. The host's initial occupied-output claim precedes drift/admission and preserves the object; its late CreateNew open after admission is the second protection leg with the same refusal and no replacement. The oracle's exclusive case-folder claim follows scenario and pack admission. The no-delete host guard lineage is 17f34447, already contained in 60480d4b; implementation still waits for the coordinator's named common tip after the wave-14 host lane that rewrites the claim merges. No deletion-based release design or older review snapshot is authorized.

Signed registry path remains tools/ShimejiParity/policy/REGISTRY.json; fixed host resource is DesktopAICompanion.ShimejiParity.PolicyRegistry.json. Reviewed final bytes are committed/embedded on joint adoption, with no runtime checkout dependency or arbitrary registry flag. Strict compiled-row/resource/digest tests and external signed-file equality remain required, including resource/path redirection mutants. Each reader process (host engine, oracle.py and comparer) runs the drift self-test before its first registry consultation of any kind, including scenario admission and trace-header-SHA recognition. A positive registry-integrity line names the registry digest and the hash of the table-holding artifact: engine assembly, oracle.py or compare.py. A successful result may be reused only within that process and for that same loaded table/artifact; no cross-process cache or missing line establishes execution. An actual integrity defect gives a named registry-integrity internal-defect refusal, exit 2, no trace and no fabricated grade, never scenario-not-registered or a legacy result. Correct intact readers retain their ordinary legacy outcomes. After BOTH scenario and pack admission succeed, emit INPUT-ADMITTED with registry digest, id, case, scenarioSha256 and packSha256. Do not log complete admission after scenario lookup alone. Closure binds the positive integrity and complete admission lines from the same process to its input/trace and table-holding artifact; the oracle binds oracle.py's tool hash and compare.py as actually loaded by the oracle beside the JAR, including whichever artifact holds its table or admission logic. Integrity/admission diagnostics use a diagnostic log, stderr or dedicated phase-evidence channel separate from trace JSONL and the comparer graded JSON report; they add no trace members and do not alter the meaning of existing buildWitness fields. These lines do not substitute for reconciliation execution witnesses. Draft registry/status bytes remain frozen pending reviewed finalization.

P2 calibration and eventual closure retain conversion command, converter aggregate, converted pets-folder and map manifest digests before runs, JAR/runner/dependency hashes, host EXE/engine hashes and actual loaded build paths. Birth-cap scope is the declared model and separate producer-configuration requirement above, not a trace-certified default. P1 static support for source container/order, clone facts, RNG and window arithmetic remains accepted with its reported scope. P2 at frozen 60480d4b7b221ebbfa8b9bf4ba5d34f975ce0362 calibrated emitted behavior with 139 CONFIRMED, 0 CONTRADICTED and 2 NOT-OBSERVABLE items (packet MANIFEST 2c39e3a008aeba4656bc88d393c7da4eb2cf4cdbc661908604a02d608c8d5891, VERIFY.md and OBSERVATIONS.md). Its four raw recipe outcomes remain failures: cap event-mismatch at tick 6 on m1.b16; carry-off and throw-off missing-event at tick 1 on m1; minimize-off missing-event at tick 6 on m1. The five controls are raw exact.

P2 used the four recipes at their retained legacy hashes plus five unregistered controls bound by the registry-pinned generator and registered-pack equality:

| calibration control | scenario SHA-256 |
|---|---|
| S-cap-below-01 | 894d2a68e3c911048018e0f0fd592296635eb540180b0b88c93b9e128e6bb99d |
| S-window-carry-on-01 | fe6c28bd2fc2d2bbae7ecb62f265537b13e6b274af5d654cbe03ee5fc033aefc |
| S-window-throw-on-01 | 222fd1f2eac45d6b15acd484dc66b77d314a1e028367ad919b4d9bc24a3917c8 |
| S-window-minimize-on-01 | 412ea463c3b06eef8cfaed7d829af64aa84902cf024521aaa791daa252d5571b |
| S-window-throwing-off-01 | 36da6f2cdd19f1981b8b188864aa32e41c8787341a3468937546a62b914fc53a |

These five controls were not retained as legacy baselines in E; that historical comparison is unavailable and is not a pass. G additionally retains the independently regenerated Throwing-off scenario as calibration provenance only. These digests are calibration provenance only, never registry rows or recognition keys; REGISTRY.draft.json and all four qualificationStatus values remain unchanged. Each registered scenario/2 input equals its legacy twin after removing schema and policy, as independently checked for all four pairs, so the finite model calibration carries over. Their actual byte digests differ; only fresh signed scenario/2 pairs plus mutation/on-off/ledger proofs close the feature.

P2 supplied --oracle-artifact and --host-exe only. Its host artifact-bound result means EXE binding, while retained engine equality was a separate manual check. P2 does not execute --host-engine, slot-5 engine comparison or scenario/2 artifactBound; those remain future controls. This finite evidence does not prove private membership, actual cap/classic configuration or absence of silent modeled-window changes.

### 11.9 Review disposition and remaining work

A1-A15/T1-T10/N1-N6 remain with F1-F5 and notes a-f refinements, consolidated with G1-G18 and the H1-H7 corrections recorded in AMENDMENTS.md. Output preservation, absent-output admission and combined order controls have separate witnesses. Policy-blind proofs seed from the run's scenario; exact replay failures and trace-off filter persistence remain required. O-SCENARIO-BYTES and O-PACK-BYTES are named unimplemented oracle obligations. No future control is counted as a run.

Revision B's supporting dispatch JSON, cap/PROPOSAL.md and window/WINDOW-POLICY-NOTES.md remain superseded and outside the normative set; prior packets stay frozen. Only the final consolidated text plus named final registry/schema/models form the prospective signed set. P1 static support and accepted P2 finite-fixture calibration are recorded separately; reviewed registry finalization and final joint signatures remain required, followed by fresh scenario/2 closure evidence. N-parity-1107-03 and both test-only follow-ups remain open, with no historical closure evidence rewritten.

### 11.10 Final payload and historical source context

**(v1.3 installation rule)** Codex installs the exact reviewed payload bytes at policy/REGISTRY.json, policy/schema/policy-declaration.schema.json, policy/inputs/<scenarioId>.json with its packs/... subtree, and policy/SCANNER-CONTROLS.json, all relative to tools/ShimejiParity. Each installed SHA-256 must equal its row in the signed payload manifest named in the v1.3 signature record. A pack path resolves from its own scenario folder joined with that document's pack value. The schema is also checked against its signed hash by closure tooling or the C8 equality control. Preserve the initial scanner file's CRLF bytes with the exact .gitattributes line `tools/ShimejiParity/policy/SCANNER-CONTROLS.json -text`. The C8 or closure external-equality test also asserts the installed SCANNER-CONTROLS SHA-256 7e8cea721dec72e69811110f9c1d507bc47675960f4d64400c2121fbc119d0b1 on a fresh checkout. In the same Codex install commit, copy the frozen H declaration byte-identically to tools/ShimejiParity/CONTRACT-v1.3-policy-H.md at c3c261fdd41c984d437a4b3e84c4128f53503425eb4cff297605f64969631886.

Codex commits those exact bytes on its own lane after both final signatures and subject to all owner verification rules. The coordinator names that commit as the base for host-policy-1's C8 and merges it before the C1-C8 wave; the host references the files and never edits them. The C8 EmbeddedResource Include points to tools/ShimejiParity/policy/REGISTRY.json with LogicalName DesktopAICompanion.ShimejiParity.PolicyRegistry.json, with no second copy. Its compiled digest constant is the jointly signed registry digest, not a premature constant declared signed. The models extract is not installed: the models enter sections 7 and 11, and the final signature binds those passage hashes and the unchanged fa059237d10a1d755c42c5e6628acfb903526f46f070e2254b869749ebc6f3b2 extract identity.

**(v1.3)** The final registry is tools/ShimejiParity/policy/REGISTRY.json at SHA-256 954776734f2ab70a4a96645e15e188d2d936e935b89e703e488c9e5de76f1442, with permanent status SPECIFICATION-ONLY; NOT-QUALIFICATION-EVIDENCE. Its four qualificationStatus values remain SPECIFICATION-AND-FRESH-PAIRED-WITNESSES-REQUIRED. The declaration schema is tools/ShimejiParity/policy/schema/policy-declaration.schema.json at SHA-256 f76a30eedb8720acdba9c352522fbfe9bbb1391d9cec191fe710eafb7a438335, logical id urn:shimeji-parity:policy-declaration:1. Only the registry status and schema id/title differ from H's frozen metadata; declaration validation, models, tuples, scenario/pack identities and provenance remain unchanged. The source-local schema/policy-declaration.schema.json reference in 11.1 identifies that same declaration schema, installed at this policy/schema path.

| id | case | scenario SHA-256 | pack SHA-256 |
|---|---|---|---|
| birth-cap | cap-at | 4914fc647a79d33b698b68dbc87a55edef1c196e4a418b68494ef2554aaa5f6f | 711d36cffa52f016a4eb880e69e28f4fa5afe09253c0a21419701dc1e6da8065 |
| window-off-plays | window-carry-off | 5471944eaa634fcb75857d4790c18fa361db4e9954c1b9de367fd9d2c1d639c6 | f5d4ac604929126542b75e53040c162fab8afcd6e81951f3daf6d47d9ff429e0 |
| window-off-plays | window-throw-off | edc9dec2edd0a9e8edad09a8a0518fd494944195d13d7793daf57ba7ffb779e9 | 8b0d4b77a722ed515e2e94fae0f99c2ec2c1d262d211cbf5a9611c8a8673ac9a |
| window-off-plays | window-minimize-off | 1477c1718d24a1b372f36ca26b9b87967b9e0345fa21f081f7876fb94b3ae41d | 28785fabfddcb1e6c466cdbcb0af946deb36168d7b1d071168211f1d02f42074 |

The historical H draft, pending and implementation-status wording in 11.1 (O-SCENARIO-BYTES, O-PACK-BYTES), 11.4, 11.7, 11.8 and 11.9 records the frozen source snapshot and its then-pending finalization. It does not override any unperformed execution obligation. Historical source files and calibration evidence remain immutable.

### 11.11 Interpretation supplement

The following paragraphs form the readings of section 11 bound by the joint approval identified in the v1.3 signature record. That record binds each paragraph's SHA-256.

#### S01-CONTINUED-RECONCILIATION Continued reconciliation

After a reconciliation mismatch the run continues to N, retains the first failing completed checkpoint and ends in status error monotonically. The reconciliation diagnostic reports the checkpoints actually completed. A genuine existing runtime termination still limits those checkpoints; no stopped execution or invalid input dispatch is resumed, and no checkpoint is fabricated. A prior reconciliation failure followed by scenario-invalid retains footer error, the scenario-invalid diagnostic and exit 2; with no prior reconciliation or provenance failure the ordinary aborted/exit-2 path remains unchanged. A failure record and an actual incomplete-coverage record carry different facts and are both retained when applicable; neither is a positive complete witness. Comparer validation precedence remains separate and unchanged.

#### S02-POST-MAP3-CLOSURE Post-map/3 closure

H closure evidence produced on the reproducible post-map/3 build under the separately adopted Mute v1.4 satisfies section 11 when it meets all of section 11's obligations and binds the map/3 converted-map bytes and manifests required by H's artifact/evidence rules. H v1.3 final signature and adoption precede v1.4 placement and the coordinated map/3 wave. This permits map/3 closure evidence prospectively; it does not rewrite H's historical map/2 text or regrade archived runs.

#### S03-GENERATOR-PROVENANCE Generator provenance

generatorSha256 names the generator that produced the retained legacy twins, packs and calibration controls at sourceRevision 35d9d1d05161a22fe4e95b4f2b4455d1819ca98f. Its value remains d47781d0a3ae9342e499466fdcc8609f26a0542d425cea3585c170094b1b96f8. No reader, build test or closure checker compares that provenance value with the working-tree generator. H's 'registry-pinned generator' phrase describes P2's historical calibration only. Later Mute and receiver generator additions land after H finalization in the coordinated map/3 wave and do not change finalized registry bytes.

#### S04-GENERATOR-IDENTITY-EVIDENCE Generator-change identity evidence

After any make_packs.py change, run it once into a fresh empty folder, never tools/ShimejiParity/policy/inputs. Require thirteen digests equal: the four regenerated legacy twins equal their registry legacyScenarioSha256 values; the four regenerated packs equal their registry packSha256 values under the canonical pack digest; and S-cap-below-01, S-window-carry-on-01, S-window-throw-on-01, S-window-minimize-on-01 and S-window-throwing-off-01 equal the five calibration digests retained in H. For each registered case parse its retained scenario/2 document and its actually newly regenerated legacy twin, remove only the top-level schema and policy members from both parsed documents, and require recursive type-strict equality of the remaining values. The retained scenario/2 documents are never regenerated; their exact identity remains the source-bound build-proof obligation. A one-byte change to one generated pack member, and separately to one regenerated legacy twin, must each make the identity check fail and name the changed file. Preserve the failing evidence and original digests; do not satisfy the check by comparing a retained file with itself. These are future evidence obligations, not runs performed by this supplement. This is also the regeneration evidence Mute v1.4 P3-E requires: P3-E's four registered scenario/pack byte sets are shown by the four regenerated legacy twins, the four regenerated packs and the twin equality above, never by regenerating or self-comparing a retained scenario/2 document.

#### S05-K5 K5 producer-input diagnostic

Use the exact producer-input token scenario-input-unreadable with detail read, syntax, schema-string or string. Classify at the existing operation boundary: read names scenario file I/O and UTF-8 decoding failures at an existing byte-read/decode step; syntax names failures of the existing JSON parse route. A raw invalid byte does not by itself select read, and no new decoder is added merely to obtain that detail. schema-string denotes the narrow eager failure obtaining the already-String root schema value. string denotes the formerly escaping decoding failures of already-String rng.kind, start sid or start behavior values, with the actual field retained in the human diagnostic. These detail names introduce no whole-file strict UTF-8 prepass, new eager string read or new rejection of a no-candidate legacy input its old semantic reader accepts. Classify the actual reader failure without changing earlier protection/scanner/admission ordering or relabeling already handled semantic scenario-invalid outcomes. The applicable pre-header producer-input refusal uses exit 2, no new trace and no fabricated comparer grade. Oracle typed input-refused handling continues later selected cases and retains final batch exit 2 for its actual named input refusals; previously handled semantic rows keep their old aggregation and other readers gain no new rejection rule. Actual trace-record JSON errors retain their existing missing-field route.

#### S06-PERMANENT-LABELS Permanent registry labels

The registry's /status and every row's qualificationStatus are permanent labels on the adopted bytes. They never transition to record execution or qualification; reviewed closure and disposition records carry that state and bind the registry digest. The adopted /status is SPECIFICATION-ONLY; NOT-QUALIFICATION-EVIDENCE. The four row qualificationStatus strings remain SPECIFICATION-AND-FRESH-PAIRED-WITNESSES-REQUIRED as permanent specification labels, not statements that evidence can never later exist. Specification agreement never changes qualificationStatus; qualification is recorded only in closure and disposition records. 11.1's phrase 'qualificationStatus records readiness' means the label names the readiness requirement, not a reached state.

#### S07-STATUS-FIELDS Policy status fields

H:158 (11.6, the paragraph beginning 'For registered scenario SHAs') uses 'status fields' to name the policy block's footprintEvaluated, footprintValid and qualified fields defined at H:154 (11.6, first paragraph). Registry /status and row qualificationStatus are not success gates. No reader, comparer or closure checker tests their label values for execution, readiness or success; their integrity role is inside the signed registry digest. Specification adoption qualifies no case. A comparer report with qualified=true and exit 0 is a per-invocation grading verdict; feature qualification of a case is a reviewed closure disposition binding the required evidence.

#### S08-SCHEMA-IDENTITY Schema identity

urn:shimeji-parity:policy-declaration:1 names exactly the declaration-validation content bound by final schema SHA-256 f76a30eedb8720acdba9c352522fbfe9bbb1391d9cec191fe710eafb7a438335. Any validation-keyword change, including either id or case enums or their permitted pairings, requires a new $id version and a reviewed amendment. The signed SHA-256, not the $id, identifies the schema bytes.

#### S09-REGISTRY-SHAPE Registry shape and registry name

Readers verify the registry digest before parsing. The registry document has exactly the members cases, generatorSha256, registry, schema, sourceRevision and status. registry is shimeji-parity-policy/1; schema is shimeji-parity-policy-registry/1; sourceRevision is forty lowercase hexadecimal characters. cases has exactly four rows, each with exactly the nine members case, id, legacyScenarioSha256, pack, packSha256, qualificationStatus, scenarioId, scenarioSha256 and ticks. Every SHA field is sixty-four lowercase hexadecimal characters and ticks is a JSON integer, not a boolean or float spelling. 'Registry name' in result metadata is the /registry value. Registry shape, exact signed-byte integrity and input admission remain distinct from qualification. Compiled-row comparison covers at least id, case, scenarioId, pack, ticks, scenarioSha256 and packSha256; a reader may also compare the provenance and label members, but only scenarioSha256 and packSha256 are admission keys and no label value gates an outcome.

#### S10-SCANNER-GOVERNANCE Scanner controls governance

The initial shared SCANNER-CONTROLS.json is the exact 33-vector H file at SHA-256 7e8cea721dec72e69811110f9c1d507bc47675960f4d64400c2121fbc119d0b1, with its CRLF bytes preserved by the exact .gitattributes line 'tools/ShimejiParity/policy/SCANNER-CONTROLS.json -text'. Before comparer or oracle scanner coverage is claimed, Codex publishes a reviewed successor adding the host's eighteen further H-cited vectors, each citing its H line, and rewrites scope/status text to describe the installed file. Any disagreement goes through the coordinator and the host re-pins in the same coordinated change. Host, comparer and oracle tests consume the same inputHex, candidate, stopReason and offset expectations. On a conflict between a vector and H or CONTRACT text, the text governs; vector changes are reviewed test-data changes. No successor file or scanner execution is claimed by this supplement.

#### S11-RECORDED-READINGS Recorded readings consistent with H

Record pure scan facts at the single retained-byte read before interpreting Parse/ReadScenario, while initial output protection and command prerequisites keep their existing precedence. Pure scan facts do not prematurely emit an admission refusal; drift runs before the first registry consultation and admission follows each producer's specified claim/prerequisite order. Bind C3 diagnostics to runId, the originating process invocation and actual artifact, outside trace JSONL and graded comparer JSON; unscoped concatenated text or an absent failure is not proof execution. No shipped admission-table override, including an internal parameter intended for self-tests, is permitted. H:150's 'every run' includes an invocation that has entered ParityRun.Run with its own EXE and engine already readable: it allocates one runId there, logs the loaded-engine line with it even when a later validation or output-open refusal prevents a header, and reuses that runId if a header is written. Such a runId asserts no trace, and a headerless refusal is never positive closure evidence. These readings do not move engine reads or logging ahead of the earlier output-protection and prerequisite gates or H:80's build-witness-unreadable refusal, and do not exempt readable pre-header refusals.

### 11.12 Historical records

**(v1.3)** The v1.1 integration record and v1.2 signature record below are preserved verbatim from the exact v1.2 base. Their descriptions of what was pasted or signed, and their hashes, refer to those historical snapshots, not to section 10 as amended by later explicitly marked passages. The preamble's pointer to the v1.2 signature record 'at the end of this file' likewise refers to the v1.2 snapshot; later records follow it. No historical signature wording or hash is altered.

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

## v1.3 signature record

Base: b107727cb18914c9909417d4f6e3ae32c0f48dad, tools/ShimejiParity/CONTRACT.md Git blob ced5f6862aeefe53395fd1426cd1ec910cc3cfc0. The frozen H declaration is tools/ShimejiParity/CONTRACT-v1.3-policy-H.md, SHA-256 c3c261fdd41c984d437a4b3e84c4128f53503425eb4cff297605f64969631886, with H manifest 5e0b3294fcc2c2a62d9449776f9eff41ee0cb75804433619a32207a8421c3580.

The signed set binds registry 954776734f2ab70a4a96645e15e188d2d936e935b89e703e488c9e5de76f1442, declaration schema f76a30eedb8720acdba9c352522fbfe9bbb1391d9cec191fe710eafb7a438335, payload manifest PAYLOAD-MANIFEST.json at eaa9c41da2d481b59d21b5dc10984c1a858cadda663fe3b8abe89c6efdfbe930, scanner controls 7e8cea721dec72e69811110f9c1d507bc47675960f4d64400c2121fbc119d0b1, and models extract fa059237d10a1d755c42c5e6628acfb903526f46f070e2254b869749ebc6f3b2. The four scenario and pack identities are the exact 11.10 table entries. The frozen declaration/archive and all payload identities retain their historical bytes; no signature proves execution or qualification.

The interpretation paragraphs of 11.11 are bound individually as UTF-8 paragraph text, without a trailing newline:

| supplement paragraph | SHA-256 of paragraph text |
|---|---|
| S01-CONTINUED-RECONCILIATION | ceb1f74c759dd4e6834c058e78b4289e13bf3be6484cc9e225b003ea42581074 |
| S02-POST-MAP3-CLOSURE | 6a7053317216b5fc1e2eb358112ffd7e28967166305f35ed99df0ce7693c3c08 |
| S03-GENERATOR-PROVENANCE | af990c6863dcb86e75c5bbda18b0ed90d697f816e105d4d72df4a94a3145df8a |
| S04-GENERATOR-IDENTITY-EVIDENCE | 591f773f5bb183e08e1d13ee1c077e8f255d2c4981d9f0a9ecc8ad7a5b997d23 |
| S05-K5 | b4acee38704d57f503ff0148b43ffc8e9138751bfdd3bfbdadc6f4b5090e5d68 |
| S06-PERMANENT-LABELS | a8ab22833fea39bf9d9c6c716462b2506c459daf53dfe20d9414d041ded546cc |
| S07-STATUS-FIELDS | 60597da3bed43045d89a91acfe396dbe5977b8dcccdfd8fd92ce7c228f6b0b9e |
| S08-SCHEMA-IDENTITY | ccf461409bf189ac4465fe41afd3c737bea5a70938a1e45c8b76841f6aca865c |
| S09-REGISTRY-SHAPE | d3beac7e84860a6783b5b7447e3e5fec6a76fd701ecfef5fdbb3eff7542a1c49 |
| S10-SCANNER-GOVERNANCE | d200beec4f53ee4a5ceb3a36735ff5dc76f60f9de51d77446499e3568971e9ad |
| S11-RECORDED-READINGS | 19d2ea96fdaa505413b8e885b8c9363bc1427e602e8a8c62444f05c791af8756 |

Passage hashes in the external approval/replay record name nested final-byte ranges and may include adjacent base anchors. Intermediate operation hashes are distinct from final passage hashes; deleted or superseded operations are identified rather than presented as surviving passages. The whole-file CONTRACT hash stays in the external approval record, never in this file or in a signature file whose hash this file embeds.

Mute v1.4 placement readings: the preamble recipe targets the v1.3 paragraph beginning 'The v1.2 statement above'; the historical v1.2 paragraph is not edited. The three map/2 wire statement sites are the v1.3 finite-exception preamble, the v1.3 paragraph at section 9 and section 10.1's v1.3 numbered item. A v1.4 'Superseded' addition at any of them covers map/2 only, not the retained trace/2 wire version. The canonical Host paragraph for the recipe remains 'Host (`src/dotNet/Shimeji`):'; the marked Section 11 ownership paragraph is separate. The recipe's 'prior exception paragraphs' are the version paragraphs at the head of the file, and its v1.4 paragraph is placed immediately after the v1.3 paragraph beginning 'The v1.2 statement above'. Section 9's 'map/2-only paragraph' is the marked v1.1 sentence stating that a host parity run reads shimeji-parity-map/2 only and refuses map/1; the surrounding map description and the map/2 property list stay. Any later signature record, including a Mute v1.4 record, is placed after this record.

External approval identifier: 2026-11-13-codex-policy-H-final-approval/APPROVAL.json.
Codex signer: Codex (codex:01a1d5a3-9ebb-79f3-8abb-31fd0efcc40a); signature date UTC: 2026-11-13.
Claude signer: Claude (claude:16ff5de4-f918-433c-8a96-0468a1060198); signature date UTC: 2026-11-13.
N1 recorded result identifier: 2026-11-13-claude-N1-record/n1-result.json.
N1 recorded outcome: all four exact scenario/2 inputs accepted under admission context.
N1 tested source revision and overlay diff SHA-256: 9f7290c6339d51cf8f6dd4bfb934fe4c76050af9; overlay 633ae7da00a6c5b89bc3b068a7919e950f5a576dd331e218df351d487ca5cc9c.
N1 built engine and four tested input identities: engine 7b190b492e3f43a9ebbaeb7ba1142ef515e0969fa51c3783e7c29adcaadb1471; inputs S-cap-at-01 4914fc647a79d33b698b68dbc87a55edef1c196e4a418b68494ef2554aaa5f6f, S-window-carry-off-01 5471944eaa634fcb75857d4790c18fa361db4e9954c1b9de367fd9d2c1d639c6, S-window-throw-off-01 edc9dec2edd0a9e8edad09a8a0518fd494944195d13d7793daf57ba7ffb779e9, S-window-minimize-off-01 1477c1718d24a1b372f36ca26b9b87967b9e0345fa21f081f7876fb94b3ae41d.

These record fields are completed only from actual approval and N1 evidence, and the completed bytes are signed only after renewed exact review. The N1 fields identify the pre-signature ordinary interpreting-reader proof on the four exact scenario/2 inputs under admission context, run on a coordinator-named prospective C8 source revision with, where needed, an uncommitted overlay limited to the admission-context schema acceptance that C8 commits, with no shipped-path admission override or table seam, under the host lane's owned TEMP and without a commit. It is a pre-signature reader check, not implementation, closure or qualification evidence. A refusal is investigated before signing, a changed input identity is re-reviewed, and N1 does not depend on the post-signature install commit.

Approval scope: This is a contract approval, not an implementation verdict.

## v1.4 signature record

Base: adopted CONTRACT v1.3 bytes SHA-256 ce75fa26e63605b9196ee716b072f6987d51564cacc0f4b2bcb4b796f4f0af9e, Git blob 53432054e1ff532080539fbddbb96bbfada39b33.
The unchanged kept proposal is CONTRACT-v1.4-mute-proposal.md, SHA-256 62b30e08de3be7946e631ecaaa69b779415a9577189a51172912972d5a1003ca. Its Required directed controls table and Ownership and review evidence section are bound by the v1.4 preamble reference.
The separate S8 naming amendment is source/ADOPTION-NAMING.json, SHA-256 3a09800554c3f101c16d2dffd0e2cdc8fad7dcfcc416f0ff11233db2215aee3c. Proposal-review records are retained unchanged as source/PROPOSAL-CODEX-SIGNATURE.md, SHA-256 bb1cd065640195bcdb8ccd60c9c2abc893ca283f3221a17d98a75ca71bc957fa, source/PROPOSAL-HOST-SIGNATURE.md, SHA-256 0c256fcc73973cdbd65e9a66a8a1322881ea648666bac38b87d65186ba68dd1b, and source/PROPOSAL-CODEX-NOTES.json, SHA-256 c52f6fb3f3e27c62fb5b936fe35a651f2e48ff907354d0b40d08f3d653eb61c5. Paths beginning source/ in this record are relative to the root of the immutable v1.4 adoption evidence packet; they are not repository paths and impose no installation requirement. The external v1.4 approval record binds that packet's root and the SHA-256 of its inventory. CONTRACT-v1.4-mute-proposal.md is not such a path: the v1.4 preamble requires it to be kept unchanged beside this file.

The following hashes bind exact UTF-8 final passage text, including placement wrappers where present and excluding a trailing newline. The v1.4 record itself and the whole current CONTRACT hash are excluded; the latter belongs only in the external approval record.

| passage | SHA-256 |
|---|---|
| P1-B | 6af6ec65e06ae17d14d7fc4229f63ab21b84cf1dcd210cc2bce4e74268a2c71a |
| P1-C | 5e71100331e3060fde5cbe1cad7cf41fdcd128a71c93eaaf3f382d38bd4c6e70 |
| P1-D | 8d142042114b804ea4ee1a2bf91cce23c88d046971cb55b04d1bec21bc14c2cd |
| P1-E | 14a4ddc7c5145bd8561969c1f3674b8e8aeb726f2d263d703b6b6ece181fefad |
| P1-F | 0a980df2b4ba9cb862941e36ffca6fc9c306b627b9233f1b990b4b352007c202 |
| P1-G | c97f53fc499e50106d18e28acf202eab181b1f8e9336f8b90d84ba6195dfae1a |
| P3-A | 59caf80eae552bcdda733a41cec3926bdc707264a3785e2a96059718ad373ecb |
| P2-A | 50ae3965ae541ea6f4f6ad5ec0696a9f63937613642266b089405cbcaca6ed88 |
| P2-B | 1f0cfec72f76206926cf2a69978130219159bf7bac26e571f7efc5cc63f57131 |
| P2-C | cf57604701d6bae868a7e6badf2381cfc8b467d1824b13460ded5a2646622244 |
| P2-D | edc22b1b1bfc64fca158f2961ae69ef2e88f6ff6313d83571f531c7792ad6879 |
| P2-E | 10555a514e2f5c8467e9b0dc2f447e95a1985e7bc55807d8c45724ffaa1baa78 |
| FC-1 | b81cc0d904f35eb4fe390438698a5681da18e9a62690e7860ce7defd1f6bd9cb |
| FC-2 | e3cd8fcd139847604430ed2833b2a7884b9405bcdce53608e81ff299db167a1a |
| FC-3 | e3771498d29b827221ce91f0c6977deb91f36bd0c1092461852e1a224153c3b3 |
| FC-4 | f3326d75f0abc7ef43c85f9cdbfbd22f51a4acf8cd395a618d186302850d8f88 |
| FC-5 | ca4e294aaeba3604efc32d5461ac54a7417f664c0c4935091fd627b4c606ca07 |
| FC-6 | 86dad6ae6d011ff532f54adbea1c0aab543ef773f3dc0859480843664259e7a4 |
| P3-B | 5052bedb07d27d0fb1fa97439a538c8e4947f50b0fcacb7781acfc6fa1d3ce0f |
| P3-D-COMPARER | f59acec7b8e2958b5576f7b1b9dae18fffd73ab774ae95f0204cb22704e68062 |
| P3-C | f5193b06775382a30b9d4906d5b7c7d42905e0f37b0b6bfb243efd553b5e95ee |
| P3-D-CLOSURE | 1ac011efa44ae09f9cd328b3aea2a55fda4fff5ab8d83961fe64f2bdd7936b7e |
| P3-E | 5f7a81dc09b9136654a5dc640af26b3e39c9a3b35e61c4e1194d46b669181925 |
| V14-TITLE | b5a9ffaaa2396bdbb53581f5db588a422cc3b10d56796696f7fc8b2006985223 |
| V14-SUPERSEDE-PREAMBLE | c7e76ca1e9e5f35f82722434209b15b453e0fadbd4627d09f7ea3b0f400de406 |
| V14-SUPERSEDE-SECTION9 | e39530505ddb329d58e52269c168619f6785f49504415249465decfa43e7d7a6 |
| V14-SUPERSEDE-ITEM8 | adc123fc57a6477d596fd4a3551067a578cc8e3cc97b0b5aaacb15d2e6b72293 |
| V14-QUALIFICATION | f653376c0867a586b88c7b8dd9ccdccf9aec6d98ae868a0ae0cbb74cfb22dfae |
| V14-PREAMBLE-S8 | 5057106a72aa029c1a864153b933a1002d1b983f523f6cb64c13eb021fc0b0e7 |
| V14-SCRIPT-SITE-SECTION2 | 2eec13fbb9b9ba8b4f10bed253294c3c73be075bdc22dc6ebb81a4f82901f42e |
| V14-MUTE-ROW | 1c943910885417cd40b01ab5b2812ebf63acb3ed94cbc41e6cba307084256b81 |
| V14-SECTION9-READER | 0e37e559ccc21a6355f9c8dd553039924bbd0bb6f77fe013d026440c3f827481 |
| V14-SCRIPT-SITE-SECTION9 | cd51665b1b385e1c8d2764b2a39984b5790a7eb6658ba8e8651b862c863103e7 |
| V14-PAYLOAD-LIST | dab6c1b40947241561d7f2b27eab218023aacddb5126560a475e330187c599aa |
| V14-HEADING-9.1 | 48a2edb37d8d8381524e48029593e7710f475229571b6fbe7e85eff22da6847b |
| V14-HEADING-9.2 | e404f712bc2c4d65a64467177be87eeb4537534ece662364beb46efdc32fcae8 |
| V14-ITEM9-CONTINUATION | b71aca43aeeeed63944715c164a4aa76b9bb94d718649be790a5ab6b148c1590 |
| V14-INSTRUMENTATION | 1e12d9939b9cc78c662bdce8679e46215d80feca772e96d10ee7ab355f0e4ebf |

Permitted H-adoption conditional deletions, listed separately:
- HC-1: S8-amended preamble, delete exactly ` where section 11 is adopted`.
- HC-2: P1-F, delete exactly `, once H is adopted`.
- HC-3: P3-A, delete exactly ` where H is adopted`.

S8 is an explicit extra naming amendment, not one of those deletions: before-paragraph SHA-256 f46ae8c1d99b5ff3604c8a4a371ca0b1d02118f06510680de2b18f57c38d15f3, exact after-paragraph SHA-256 before HC-1 015d3059afa413c9ded43c42136bffd3161441ce29cde525a34cd1bd58a5989c, final preamble after HC-1 SHA-256 5057106a72aa029c1a864153b933a1002d1b983f523f6cb64c13eb021fc0b0e7.

Mute v1.4 adoption readings: the kept proposal's status line and its statement that the file contains no signatures record its drafting state and do not make version 1.4 a draft; its signatures are the proposal-review records named above. A control id (C, W or Q) inside a pasted v1.4 passage, including 'Control ids W1-W8 below' in P3-B, refers to the kept proposal's Required directed controls table, and P2-D's 'proposed null expression' is control C7's action-cached Sound expression.

External approval identifier: 2026-11-13-codex-mute-v14-final-approval/APPROVAL.json.
Codex signer: Codex (codex:01a1d5a3-9ebb-79f3-8abb-31fd0efcc40a); signature date UTC: 2026-11-14.
Claude signer: Claude (claude:16ff5de4-f918-433c-8a96-0468a1060198); signature date UTC: 2026-11-14.
C16 recorded result identifier: desktop-ai-companion audit-evidence packet 2026-11-13-codex-C16-run-5dc469a0-dfa3-48a2-a077-130946c0d9ad (C16 probe run 5dc469a0-dfa3-48a2-a077-130946c0d9ad, preparation revision 2 MANIFEST.json SHA-256 dbaaf40df8da06089dd8df22262d2562266f5734490da76d6bde056a36f33108), core inventory CORE-RESULTS-MANIFEST.json, SHA-256 9042c2a1964b2cbd4a5bebb801fc8f4cab0eff1453b26db60f11b0aad35c4310.
C16 recorded outcome: Probe only, not P3-C evidence: in fresh run 5dc469a0 the own-Sound literal-text null pair with Sounds on recorded a Mute clip equal to the four-character String "null" on both sides, the oracle taking the named branch at O2-before with no all-clips branch (no O6 or O7), O8 done, one mute event and no error event, and the host converting the literal to a map/3 row with sourceValue String "null" and sourceKind string and emitting one tick-0 m1 mute with clip String "null"; the launcher's post-driver descendant check failed after every observation had been written, and independent review found that this failure does not affect the observations, so P1-C stands.
C16 fixture, converted map/3, host EXE and engine, and oracle JAR identities: pack manifest SHA-256 c831074889c92ed6068f05629d2fc747717d57289c0851d1073d702b011fec3d (fixture manifest SHA-256 e1a89558ac58977c1e3e957c34dde1057301a1ff51c99a55cb825668c04f706e); scenario S-C16-own-null-sounds-on-01 SHA-256 f0af9067919daae6c8ee0790cfa56fbce945165a3e3857bbd2f9bc73cac25a41; converter source b107727cb18914c9909417d4f6e3ae32c0f48dad with overlay manifest SHA-256 8536b508a3a17b39fccfdf5c4c74cf71291c24f03a01fcba04f60d3800af6f82 and converter DLL manifest SHA-256 1e8a1b2eaeea30018a7121075df8b0ad8b01b4f2614b9641326c3d90a7ed3b40; converted map/3 Alpha/parity-map.json SHA-256 b323d7618ea9f3f50e6a2f70645aa87013793cfb0385c1847f811a7c9fed5bb2; host source 516b222eecc280e04377a3b28645677822c7e944, host EXE DesktopAICompanion.exe SHA-256 5a786708cdd77e4ade3733a952074a1b01c501b2dfce2e92a4545dbd539f69a2 and engine DLL DesktopAICompanion.dll SHA-256 1250ca9d62b994876895115233a6c3cf7de28e8b79be153086c6a1891c6bc57f as hashed from typeof(TickWorld).Assembly.Location (accepted limitation, C16 only and not P3-C: the literal loaded path is not emitted and post-load image integrity is not proven); oracle source-action JAR Shimeji-draft.jar SHA-256 c17bebd69d7b1b380beb366bfd5d1bf6a45e61027e81a7ba936b8b2a9fa23a05 built from edit-spec SHA-256 0121538fe92170ad4aba5f92aed1f9f11e6932a82d242f2b156c2ee40d648599, runner JAR v1-runner.jar SHA-256 a493be14f7e5969dd011a574613677c9bf29ab42752a4f740f8b741b73b1ac14, bootstrap JAR c16-entry.jar SHA-256 1787e6e35817a0271e4e4d3b908d87406311145c05c4f2f41d8ddc1ff7d83a33; host trace SHA-256 d4a5afe27a6d4a78b262e616901d37e32a2624220a9d8b959db01f3334a72c25, oracle trace SHA-256 2f25e93c5db1accecb3622b7c42aee76d62447a2178e5e31ed8c1550cbb69639.

These record fields are completed only from actual approval and C16 evidence, and the completed bytes are signed only after renewed exact review. The C16 fields identify the pre-placement probe that the kept proposal's C16 row requires: one fresh own-Sound literal-text `null` pair, Sounds on, observed at O2-before on both sides. It is a probe, not P3-C evidence, and establishes no implementation, closure or qualification. If the oracle instead takes all-clips, P1-C is recorded and reopened before placement, and this record is not signed until that is jointly dispositioned.

Approval scope: specification text and its bound kept proposal, directed controls and ownership; no implementation, execution, qualification or closure is established by a signature.
