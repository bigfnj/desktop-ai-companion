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
  "mascots": [{ "sid": "m1", "imageSet": "Alpha", "behavior": "Stand", "anchor": [640, 680], "lookRight": false }],
  "activeWindow": [{ "tick": 0, "visible": false }],
  "input": [{ "tick": 0, "cursor": [100, 100] }]
}
```

- `pack` is relative to the scenario file. It is a Shimeji-EE layout (`conf/actions.xml`, `conf/behaviors.xml`,
  `img/<Set>/*.png`). The host side converts the same pack with the converter under test.
- `ticks` counts logical ticks of 40 ms.
- `mascots` is the start state. It is applied BEFORE behaviour initialisation, so no unintended initial random
  selection runs. `sid` values are the stable ids every trace line uses.
- `activeWindow` and `input` are timelines. An entry takes effect at the START of its tick and holds until the
  next entry.
  - Window fields: `visible`, `rect` `[left, top, right, bottom]`, `title`.
  - Input fields: `cursor` `[x, y]`, `press` and `release` (`"left"` or `"right"`), `click` (shorthand for press
    and release in the same tick).
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
`Math.random`, and the host's equivalents. Every draw is logged (section 3, `rng-draw`). In a directed scenario a
different draw COUNT is a failure, never something a tolerance absorbs.

## 3. Trace (`trace.jsonl`, schema `shimeji-parity-trace/1`)

One JSON object per line.

**Line 1, the header:**

```json
{ "type": "header", "schema": "shimeji-parity-trace/1", "runtime": "oracle",
  "scenario": { "id": "S-breed-basic-01", "sha256": "..." },
  "build": { "sourceZipSha256": "...", "patchSha256": "...", "jdk": "1.8.0_504", "deps": { "jna.jar": "sha256" },
             "artifactSha256": "..." },
  "rng": { "kind": "none" }, "tickMs": 40, "dpiScale": 1.0, "runId": "uuid", "startedUtc": "..." }
```

For the host, `build` carries `{ "commit": "...", "exeSha256": "...", "engineSha256": "...", "converterSha256": "..." }`.

**State lines.** After every step, one line per live entity, in stable manager order.

```json
{ "type": "state", "tick": 12, "sid": "m1", "imageSet": "Alpha", "behavior": "Walk", "action": "Walk",
  "actionClass": "Move", "actionStack": ["Walk"], "refKey": "Walk@behaviors:3", "actionAge": 7,
  "pose": { "image": "/shime2.png", "imageRight": null, "index": 1 }, "visible": true,
  "anchor": [622, 680], "origin": [590, 552], "lookRight": false, "velocity": [-2, 0],
  "affordances": [], "target": null, "dragging": false, "pending": false, "vars": {} }
```

- `tick` 0 is the state after initialisation, before the first step.
- `actionAge` is the action's time in ticks.
- `anchor` is the contact point in screen pixels. `origin` is the rendered image's top-left.
- `visible` is false while an image-less pose hides the companion.
- `pending` is true for a birth that has been requested but not yet activated.
- `vars` holds stored scalar per-instance variables only.
- `refKey` identifies the declaration or reference that started the action, so two references to one action
  can be told apart.

**Event lines** come at the point they happen inside a step.
`{ "type": "event", "tick": 12, "sid": "m1", "kind": "...", ... }` with these kinds and payloads:

| kind | payload |
|---|---|
| `behavior` | `from`, `to`, `cause` (`select` / `next` / `forced` / `partner` / `fallback`) |
| `action` | `from`, `to`, `cause` (`sequence` / `select` / `guard` / `duration` / `arrival` / `lost` / `instant`) |
| `transform` | `fromImageSet`, `toImageSet`, `behavior` |
| `birth-request` | `child` (sid), `imageSet`, `behavior`, `anchor`, `lookRight`, `ordinal` |
| `birth-activate` | `child` |
| `birth-refused` | `child`, `reason` (`cap` / `toggle-off` / `missing-member` / `error`) |
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

Birth lineage: a child's sid is `<parentSid>.b<n>`, where `n` counts that parent's births from 1. Transform keeps
the sid. Process ids and window handles never appear.

**Last line, the footer:**
`{ "type": "footer", "ticks": 600, "status": "complete", "draws": 0 }`. The status is `complete`, `timeout`,
`aborted` or `error`. A trace with no footer is `truncated`.

## 4. Update order, both runtimes

Each tick, in this order:

1. The environment applies this tick's scenario entries (cursor, presses, active window).
2. Births requested in the previous tick are activated.
3. Disposals requested in the previous tick are removed.
4. Every live mascot steps, in stable manager order: guard checks, action effects, pose movement, transitions.
   Peers update sequentially, so a later mascot sees an earlier one's new offer and anchor in the same tick.
5. Every mascot applies, which means it renders.
6. State lines are written, in the same stable order.

A birth that is requested during step 4 appears with `pending: true` until step 2 of the next tick.

## 5. Observation without side effects

Tracing never evaluates a lazy variable, a script or a condition in order to observe it, and never reruns one.
Values come from the real evaluation sites, or from state already stored. Turning tracing on must not change
behaviour; each runtime proves this once per build with a trace-on/trace-off pair over a directed scenario.

## 6. Comparison

- **Directed scenarios** (`rng.kind` is `none` or `tape`):
  - Event sequences must match exactly on tick, sid, kind and payload.
  - State lines must match exactly on `imageSet`, `behavior`, `action`, `pose.image` (or `pose.imageRight`),
    `visible`, `lookRight`, `dragging`, `pending` and `target`.
  - `anchor` must match exactly unless the scenario declares a named rounding boundary.
  - `origin` is compared after anchor normalisation, which uses each pose's authored anchor, the facing and
    `dpiScale`. Origin differences are reported separately from behaviour differences.
- **Free-running scenarios** (`splitmix64`) make a separate claim: behaviour time-share and draw counts, with the
  tolerance printed beside the result.
- **Verdicts:** `exact`, `within-tolerance:<name>`, `policy:<id>`, `approximate`, `unsupported`,
  `not-exercised`, `pending` (the host primitive does not exist yet), `fail`.
- **Distinct failures:** `missing-field`, `truncated`, `timeout`, `aborted`, `build-witness-missing`,
  `scenario-hash-mismatch`, `rng-unexpected-draw`, `rng-tape-exhausted`, `rng-draw-count`.
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
