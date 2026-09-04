# Shimeji -> animations.xml: what maps, what does not

Working notes for BACKLOG #4. Everything here was read off the two formats' own schemas and the shipped
sample data, not from memory. Where something is assumed rather than verified it says so.

Format reference used: `gil/shimeji-ee` (tracks Kilkakon v1.0.13), cloned OUTSIDE this repo. We ship the
converter, never the skins.

## The two ends

**Source** -- `conf/actions.xml` + `conf/behaviors.xml` + `img/<Skin>/shime1..N.png`, schema in
`conf/Mascot.xsd`:

- `Action{Name, Type, Class?, BorderType?, Loop?}` plus 19 optional `ActionArguments`
  (`Condition`, `Duration`, `TargetX/Y`, `InitialVX/VY`, `Gravity`, `BornBehavior`, ...).
  Contains **either** `Animation{Pose+}` **or** nested `ActionReference` / `Action` -- so actions form a
  tree, not a list.
- `Pose{Image, ImageAnchor="x,y", Velocity="x,y", Duration=int}` -- one sprite file per pose.
- `Behavior{Name, Frequency=int, Condition?, Hidden?}` with an optional
  `NextBehaviorList{Add}` of `BehaviorReference{Name, Frequency, Condition?}`.
- `Parameter` is `${...}` / `#{...}` (a JS-ish expression evaluated by the Java engine), a number, or a bool.

> **Trap: the vendor's XSD does not describe the vendor's own config.** `Mascot.xsd` restricts `Type` to
> `Embedded|Move|Pause|Fixed|Composite|Select`. The shipped `actions.xml` actually uses
> `Sequence` (64x), `Floor` (18x), `Embedded` (12x), `Stay` (10x), `Move` (6x), `Animate` (4x),
> `Select` (3x), `Wall` (2x), `Ceiling` (2x). Validating input against `Mascot.xsd` would reject the
> reference skin. **The parser must be tolerant and drive off observed values.**

**Target** -- `animations.xml`, schema in `src/Resources/animations.xsd`, enforced by
`src/dotNet/CompanionXmlValidator.cs`:

- ONE sprite **sheet**, base64 inside the XML, `tilesx`/`tilesy`; frames are tile indices.
- `animation{id, name, start, end, sequence, border?, gravity?}` where `start`/`end` are a single
  `MovingNode{x, y, offsety, opacity, interval}` and x/y/interval are **expression strings**.
- `next{value, probability, only}`; `only` is a closed set:
  `none|taskbar|window|horizontal|horizontal+|vertical`.
- Expressions accept exactly 11 identifiers (`src/dotNet/SafeExpression.cs`): `screenW`, `screenH`,
  `areaW`, `areaH`, `imageW`, `imageH`, `imageX`, `imageY`, `random`, `randS`, `scale`.
- Hard ceilings: **4 MiB total XML including base64**, 1024 tiles, 1024 animations, 256 transitions
  per set, 16384 frames total. For scale: the shipped sheep are 1.11 MiB, KuroShimeji's 46 sprites are
  480 KiB on disk before compositing.

## The mapping

| Shimeji | desktopPet | Fidelity |
|---|---|---|
| `Pose.Image` run | frame indices into a composited sheet | **clean** -- mechanical compositing |
| `Pose.Duration` (ticks) | `interval` | **clean** once the tick rate is pinned |
| `Behavior.Frequency` | `next/@probability` | **clean** -- both are integer weights |
| `BehaviorReference` | `next` element | **clean** -- same shape, name -> id |
| root `BehaviorList` | a synthesised `next` set on every terminal animation | **clean, and required** (below) |
| `Action Type=Sequence/Composite/Select` | several animations chained by `next` | **deterministic** tree-flattening |
| `Pose.Velocity` (per pose) | one `start`/`end` pair per animation | **lossy** -- varying velocity must be split or averaged |
| upward `Pose.Velocity` (a jump) | a SOLVED arc: launch, `fall`, landing | **replaced, not mapped** -- see below |
| `Pose.ImageAnchor` (x,y) | `offsety` (y only) | **lossy** -- no x offset exists |
| `BorderType` Wall | `only=vertical` | **clean** |
| `BorderType` Floor | `only=horizontal+` | **clean** |
| `BorderType` Ceiling | `only=horizontal` | **lossy** -- collides with Floor (below) |
| `Condition="${...}"` | *nothing* | **impossible** -- the target has no per-animation conditions |
| `Type=Embedded` + `Class=` | nearest built-in, or drop | **impossible in general** -- it names a Java class |
| `BornBehavior` / Breed | `childs` | **partial** |

### Jumps are the one place the converter REPLACES the source rather than mapping it

Every other row above is a translation. A jump is not, and the reason is arithmetic: the target format
interpolates linearly from `start` to `end` across the whole sequence, so the height an arc reaches depends
on how many STEPS it has, not just on its launch velocity (roughly `a²(N-1)/(2(a+b))` for a launch of `a` and
a descent of `b` over `N` steps). A Shimeji pose velocity carries `a`; `N` is whatever the source action's
frame count and the emitter's repeat budget happen to produce. Passing `a` through therefore does not
preserve anything a viewer can see. Measured across the 32 jumps in the shipped corpus, it gave 16 arcs under
20px and 16 at 72px.

So the emitter fixes the OBSERVABLE quantities and derives the velocities from them:

| Quantity | Value | Where it comes from |
|---|---|---|
| peak height | `JumpPeakPx` = 48px | `yellow_sheep`'s `jump`, measured (its authored 14-frame arc rises 48px) |
| airtime | `JumpTargetMs` = 1200ms | the same animation, measured |
| sideways span | `JumpSpanPx` = 150px | the same animation (10px/tick over 14 steps) |
| descent | `JumpDescentY` = +20 | the same animation's `end` y |
| launch | solved per animation | `SolveJumpLaunchY(steps)`, so the height holds at any frame count |

The source keeps: its frames, their order, the SIGN of its horizontal motion, and whether it is a jump at
all. A rise weaker than `JumpMinLaunchY` (-8) is not treated as a jump; its vertical component is flattened
to zero and the animation plays along the ground, which is reported in the residue. Structurally the jump is
three phases, matching the sheep: the arc, then `fall` if the arc outlives the drop, then an
`only="taskbar"` landing weighted toward re-jumping and locomotion so the companion arrives on its feet.

Observed `Embedded` classes, all unconvertible as code: `Breed`, `Dragged`, `Fall`, `FallWithIE`, `Jump`,
`Look`, `Offset`, `Regist`, `ThrowIE`, `WalkWithIE`. Two of them (`Dragged`, `Fall`) have host equivalents
via the reserved names below; `ThrowIE`/`WalkWithIE`/`FallWithIE` manipulate an Internet Explorer window
and have no target concept at all.

## What the repo already knew, and what this pass added

Most of the target-side behaviour here is **already documented** in `grimoire/03-companion-xml-format.md`. That
doc is the authority; this section only records what it means for a converter, plus the measurements.

**Already documented -- do not "rediscover" these.**
- **The four magic animation names** (`grimoire/03` §7): `fall`, `drag`, `kill`, `sync` are bound by the
  loader on animation *name* (`src/dotNet/Xml.cs:250-253`) to `AnimationFall/Drag/Kill/Sync`. Nothing
  reaches them through a `<next>` edge.
- **The `only` semantics** (`grimoire/03` §6), including that `horizontal` means "top **and** bottom" and
  `horizontal+` is `HORIZONTAL|WINDOW` (`0x06`).
- **The respawn rule** (`grimoire/03` §6): when no `<next>` is eligible the selector returns `-1` and the
  companion **respawns from a fresh `<spawn>`**. A dead-end animation is therefore legal and intentional, not a
  defect. This is the doc's own "most common authoring bug" note -- a missing `only="none"` fallback makes
  a companion vanish and reappear.

**What this pass added.**

1. **The measurement, and the converter obligation it implies.** Treating the four magic names as ordinary
   graph nodes makes **21 of the 22 shipped companions** look disconnected; treating them as roots drops that to
   7, and every remaining orphan is one of two dead animations (`king_jump_top_flip`,
   `king_jump_up_flip`) in the seven sheep recolours, which share a source file. So the converter must
   *emit* all four names -- and since `kill` and `sync` have no Shimeji equivalent, it has to synthesise
   them or the companion cannot be closed or synchronised at all.

2. **Reachability is a genuine gap in the validator, but dead ends are not.** `CompanionXmlValidator` proves
   referential integrity (every `next` target exists, probabilities are positive -- see `ValidateNextSet`)
   and never proves reachability, so a companion can validate with animations no spawn can lead to. That is worth
   checking, because a flattened behavior tree orphans everything downstream of a dropped action. But note
   the correction against §6: `PetGraph`'s **terminal** count is informational, NOT a defect count --
   terminals respawn by design. Emitting Shimeji's root `BehaviorList` as a `next` set on terminal
   animations is therefore a **fidelity** choice (Shimeji re-picks and continues; a respawn visibly
   teleports the companion), not a validity fix.

3. **The Shimeji-side mapping**, including the `Mascot.xsd`-vs-`actions.xml` divergence above, the
   `BorderType` -> `only` table, and the `Floor`/`Ceiling` collision that follows from §6's
   "top **and** bottom".

## Residue

Anything in the "impossible" rows is dropped and **listed**, never silently discarded. That list is the
converter's real output alongside the XML: it is what a reviewer checks, and it is the only place an
LLM-assisted repair pass would be worth pointing -- generating the other 80% with a model would be slower,
less reviewable and no more correct than the table above.

## Stage 1: the census is executable

The Group 1/2/3 taxonomy above is now implemented in `tools/ShimejiConvert.Engine/Shimeji/`
(`ShimejiParser` + `ActionClassifier`) and reproducible with `ShimejiConvert classify <conf-dir>` against an
external `gil/shimeji-ee` clone. On that reference config it reports **91 actions: 53 Group1 / 32 Group2 / 6
Group3**, and **24 behaviour conditions: 5 map cleanly (`only=`) / 19 need new state**. The Group2 bucket is
dominated by the dead IE-window subsystem; the genuinely worth-preserving Group2 items are cursor-following
(ChaseMouse / look-at-mouse). That is why v1 adds `cursorX`/`cursorY` **and** `selfX`/`selfY` to the companion
format in Stage 5: chase is expressed as arithmetic `(cursorX - selfX)/k`, and the companion's own position is not
otherwise reachable (`imageX`/`imageY` return -1 for a top-level, non-child companion -- see `src/dotNet/Xml.cs`).

`ShimejiConvert selftest` gates the parser + classifier on a committed synthetic fixture; the real config is
copyrighted and deliberately never enters this repo (clone it outside the tree for the `classify` dev check).
