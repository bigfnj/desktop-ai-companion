# 06 — The Shimeji runtime extensions (companion format 1.1)

> **Status: DRAFT for review (2026-11-04).** It specifies what the host must run so a converted Shimeji-EE pack
> behaves like the official Shimeji-EE 1.0.23 runtime. Parity with that runtime is measured by
> `tools/ShimejiParity/` (see its `CONTRACT.md`). Source references below (`Breed.java:73`) point into the official
> source archive, which is fetched by the parity tooling and never committed.

A classic companion ([03](03-companion-xml-format.md)) is a graph of animations, each a run of frames at an interval,
joined by weighted `<next>` edges filtered by `only=`. A **Shimeji-mode** companion is the same document, carrying the
extensions below. The host runs it on the source's 40 ms logical tick, with the source's semantics. Every extension is
optional and declared. A host that predates them refuses such a companion at schema validation, so it is never
half-run.

The design stays **graph-lowered**. Shimeji actions, composites and behaviours become animation nodes and edges, so the
validator, the reachability walk and Companion Studio keep working. No second runtime and no script engine are
involved: expressions are a sandboxed subset (section 6).

---

## 1. Feature tokens

`<header><features>` holds a space-separated set. A companion must declare every extension it uses, and a host refuses
any token it does not know, by name: *"needs a newer Desktop AI Companion (feature 'x')"*. This header is read before
schema validation.

| token | enables |
|---|---|
| `tick` | the 40 ms logical tick: `<header><clock>`, `<pose>`, `<variants>`, tick-mode limits |
| `actions` | `<act>` action nodes, `<children>`, `<param>` and parameter bindings |
| `behaviours` | `<behaviour>` and `<route>` nodes |
| `expressions` | SafeExpression v2 (`when`, expression-valued params) |
| `packs` | `<pack>` and `<startup>`, cross-member references |
| `social` | affordances, scanning, interaction |
| `lifecycle` | breeding, transform, self-destruct |
| `windows` | window actions (they also need the user's per-type opt-in) |
| `hotspots` | `<hotspots>` |
| `variables` | per-instance variables (`mascot.variables`) and `<constants>` |

A token becomes "known" only in the host build whose runtime implements it, so no build accepts a companion it cannot
run.

---

## 2. Header additions

```xml
<header>
  ...classic children...
  <features>tick actions behaviours expressions packs lifecycle</features>
  <clock>tick</clock>
  <pack id="dragonet" member="Egg" role="egg" hidden="true"/>
  <startup member="Egg" behaviour="Laid"/>
  <constants><constant name="Speed" value="2"/></constants>
  <toggles><toggle id="Hatching" label="Hatching" default="true"/></toggles>
</header>
```

- **`<clock>tick</clock>`.** The companion runs on 40 ms logical ticks, and pose durations count ticks. Without it the
  classic interval timing applies.
- **`<pack>`.** `id` is the pack, `member` this member's key (unique in the pack, case-sensitive). `role` is `main`,
  `egg`, `form` or `helper`. `hidden="true"` keeps a member out of the user's Add-a-companion list; helper sets and
  egg stages are hidden. Members are installed as separate companion types and resolved by (`id`, `member`)
  (section 9).
- **`<startup>`.** Only on a `main` member. It gives the source's initial image set (`member`) and entry `behaviour`,
  which a new companion of this type starts as. Arriving as an egg is a separate per-type user preference, offered
  only when `<startup member>` names a member whose `role` is `egg`.
- **`<constants>`.** Named values bound into behaviour-selection conditions only (`Configuration.java:64-67, 184-188`).
- **`<toggles>`.** One entry per `Toggleable` source behaviour. The user can turn each on or off per type, as the source
  does: it keys every per-mascot setting by the mascot's current image set (`Main.java:1382-1389`, and
  `DisabledBehaviours.<imageSet>` from `:1420`), so a transformed companion takes the target type's settings.
  - A toggle's `default` (on when absent) is what its behaviours are until the user chooses; the source has no such
    attribute, and every Toggleable behaviour starts enabled there.
  - In the app, the Pets pane shows a tick companion's switches on its card: one per header toggle, plus Breeding and
    Transients when something the type runs breeds (BornTransient, as each reference passes it, decides which one
    governs a birth), Transformation when it transforms, and "Let it move windows" (section 8) when it runs a window
    action. A classic companion's card has none. A change applies to the companions on screen at their next decision.
  - The source's per-instance overrides are not reproduced: a choice applies to every companion of the type.

---

## 3. Image and pose additions

- **`<image><anchorx>`, `<anchory>`.** The type's default contact point (Shimeji's ImageAnchor) in cell pixels,
  before scaling, used by every pose that does not give its own (below). The default is bottom-centre, (cellW/2, cellH).
- **Per-pose geometry.** In the source every image has its OWN size and anchor (`ImagePairLoader.java:37-38`), and four
  things read them: the window rectangle, the off-screen drop-in test (`UserBehavior.java:252-255`, the image's left,
  right and top edges against the screen), a mirrored hotspot (`Hotspot.java:31`, the image's width) and Dragged's and
  Regist's offsets (`Dragged.java:82-83`, `Regist.java:51`, the image's anchor). 84% of image sets in the corpus mix
  sizes or anchors, so the format carries them per pose rather than padding frames to one canvas, which would move all
  four. The sheet's cells are the size of the largest image, each frame drawn at its cell's TOP-LEFT; a pose says how
  much of its cell is the image and where the image's anchor is:
  `<pose frame="12" right="40" ticks="6" ax="61" ay="118" w="100" h="120" rw="104" rh="120"/>`
  - `ax`, `ay`: this image's ImageAnchor, in the LEFT image's pixels, before scaling. Default: the type's anchor.
  - `w`, `h`: the left image's size inside its cell (1 to the cell size). Default: the cell size.
  - `rw`, `rh`: the explicit right image's size, when `right` is given. Default: `w`, `h`.
  - Facing left, the image is (`w`, `h`) with its anchor at (`ax`, `ay`). Facing right it is the right image, or the left
    mirrored: (`rw`, `rh`) with its anchor at (`rw - ax`, `ay`), as the source computes it (`ImagePairLoader.java:38`).
  - The companion's bounds, window, off-screen test, hotspot mirror and drag offsets use the CURRENT pose's image, as
    the source does; until a companion's first pose they use the type's defaults.
  - Scaling applies as the source's: the anchor is `round(a * s)` (Java rounding), the size is the sprite store's scaled
    size of that region (`round(w * s)`).
  - All six need feature `tick`; the validator keeps each inside its cell (`w`, `h`, `rw`, `rh` from 1 to the cell size)
    and the anchor within -4096..4096.
- **`<pose>`.** Replaces `<frame>` inside `<variant>`:
  `<pose frame="12" right="40" ticks="6" dx="-2" dy="0" sound="s3" volume="-6"/>` (plus the geometry above)
  - `frame` is a sheet index, or `-1` for an image-less pose. It HIDES the companion's window while the companion
    keeps its timing, motion, sound and count (`Pose.java:70-85`, `ImagePairs.java:61-65`, `Mascot.java:587-609`).
  - `right` is the right-facing frame (`ImageRight`). Omitted, the left frame is mirrored, as today.
  - `ticks` is the pose duration (1 to 100000). `dx`/`dy` is the pose's per-tick velocity; it is mirrored when
    facing right (`Pose.java:70-85`, right-facing subtracts).
  - `sound` names a `<sound id>`; `volume` is the pose's Volume, the source's unit: the clip's gain in DECIBELS
    (`AnimationBuilder.java:105`, default `0`; `SoundLoader.java:24` sets it as the clip's MASTER_GAIN). The host
    plays the clip at a linear factor of 10^(dB/20), so 0 plays it as recorded, -6 at about half and +6.02 at twice.
  - The pose for a given action age is chosen by age modulo the summed durations (`Animation.java:54-78`).
- **`<sound id="...">`.** A new optional attribute, so poses can name clips. `animationid` stays for classic use. In a
  tick-clock companion every sound is a named clip (an id of 1 to 64 characters, unique, no `animationid`), and its
  audio passes the same size limits and MP3 check as a classic sound. A named clip's `<loop>` and `<probability>` are
  not read: a clip plays once per start, as the source's does.
- **Playing a clip** (`Mascot.java:612-625`, `Sounds.java`, `SoundLoader.java`):
  - A clip is one sound at one volume. Every companion whose applied pose names the same clip of a type at the same
    volume shares it: one clip, one playing state. The same sound at another volume is another clip.
  - At every apply (the render, after the step), when the companion's Sounds setting is on (read for its CURRENT type)
    and its applied pose names a clip, the clip starts from its beginning unless it is still playing. So a clip that
    finishes while its pose still names it plays again at the next tick, and a second companion naming a clip that is
    playing does not restart it.
  - Changing pose never stops a clip, and neither does a companion's disposal: a clip plays out. Right after a
    Transform the clip of the pose still shown (the old type's) plays.
  - The host's rules then apply as they do to a classic companion's sound: the global companion-sound switch and the
    type's own mute are the source's Sounds setting; while sounds are held for a fullscreen window a clip does not
    start (it is asked again at the next apply); the level is the user's volume times the pose's gain, never above the
    user's volume (a pose above 0 dB plays at the user's level; the source can boost it, a recorded divergence:
    the volume slider is the user's ceiling for everything the app plays).
  - Divergences, recorded: a Volume outside the gain control's range (-80 to +6.02 dB) makes the source refuse to load
    the whole image set (the control throws); the host clamps it to the range. A clip in the source's shared `sound/`
    folder is ONE clip across image sets; here each type carries its own copy, so it plays and stops per type. A Mute
    name that runs on into the source's volume suffix (its cache key is path plus volume) is not matched. Two clips
    whose names differ only in case share one playing state (the app's audio owners compare that way; a converted pack
    comes from a case-insensitive file system and cannot hold two such files). Turning companion sounds off lets a clip
    in flight finish, as a classic sound does; the source's global menu stops every clip at once.

---

## 4. Node kinds

A tick-mode companion's `<animations>` holds three kinds of node, told apart by the child element they carry. Each is
still an `<animation id name>` with a `<sequence>`. In tick mode a sequence carries only `<next>` edges and no frames.

### 4.1 Action node, `<act>`: one Shimeji action

```xml
<animation id="40">
  <name>Walk</name>
  <start>...</start><end>...</end>
  <act type="Move" class="" border="Floor" loop="false">
    <param name="TargetX" value="mascot.environment.cursor.x" cache="action"/>
    <param name="Duration" value="250"/>
    <guard when="mascot.environment.floor.isOn(mascot.anchor)" duration="Duration" draggable="true"/>
    <offers><offer value="hug"/></offers>
    <variants>
      <variant when="" turn="false">
        <pose frame="2" ticks="6" dx="-2" dy="0"/>
        <pose frame="3" ticks="6" dx="-2" dy="0"/>
        <hotspots><hotspot shape="ellipse" origin="20,10" size="30,30" behaviour="Petted"/></hotspots>
      </variant>
    </variants>
    <children mode="sequence">
      <child animation="Stand"><bind name="Duration" value="40"/></child>
    </children>
  </act>
  <sequence repeat="0" repeatfrom="0"><next probability="100">41</next></sequence>
</animation>
```

**`<act>` attributes.**
- `type` is one of the source's six: `Stay`, `Move`, `Animate`, `Sequence`, `Select`, `Embedded`
  (`ActionBuilder.java:101-138`).
- `class` is the embedded class's short name, from the closed list in section 5. It is empty unless `type` is
  `Embedded`. An unknown class is refused by the validator; the converter keeps such an action and records it as
  unsupported.
- `border` is `Floor`, `Wall`, `Ceiling` or empty (`BorderedAction.java:39-61`). `loop` applies to Sequence only.

**Parameters.**
- `<param name value cache>` declares a parameter's default. `value` is a literal or an expression BODY (section 6):
  the source's `${...}` or `#{...}` wrapper is not written; it becomes `cache`, which is `action` (source `${}`),
  `frame` (source `#{}`) or `none` (a literal). Both caches are lazy: the value is computed at its first use and kept
  until the cache is cleared (section 6, Caching).
- A guard or variant `when` is evaluated at every check and has no cache of its own. A source Condition written with a
  cache wrapper is lowered to a cached parameter, and the `when` names that parameter, so a `${}` Condition holds its
  first value across both checks of a step as it does in the source.
- The `<guard>` attributes name parameters or hold expressions:
  - `when` is the source Condition. False ends the action, as `ActionBase.hasNext` does. It is checked exactly where
    the source checks it (4.5), which is more than once per step.
  - `duration` is in ticks. The action ends when its age reaches it.
  - `draggable` is queried only when the user presses on the companion. False refuses the drag; it never ends the
    action. A composite's draggability is its active child's.
- **Frames.** Every activation of an action gets a fresh parameter and cache frame, per companion. Definitions are
  immutable and shared. Locals the source publishes (`TargetX`, `TargetY`, `VelocityX`, `VelocityY`, `FootX`,
  `FootDX`) live in that frame.

**`<offers>`.** Affordance values, LITERAL strings, broadcast while this action runs. A source `Affordance` written as an
expression has no representation in this version; the converter reports such an action. Each step of an action
REPLACES the companion's offer list with its own offers (`ActionBase.java:88-101`): a composite steps first and its
active child after it, so the leaf decides, and a leaf with no offer leaves the list empty. The source declares one
`Affordance` per action, so a converted action has at most one offer; the format allows up to 8 for hand-written
companions, offered together.

**`<variants>`.** The source's `<Animation>` blocks, in order. Each tick the FIRST variant whose `when` holds plays
(`ActionBase.java:122-164`). A variant's `when`, like a guard attribute, is evaluated every time the variant is looked
up, and the source looks it up several times a step (its duration, its pose, its hotspots). The source caches an
Animation Condition by its `${}`/`#{}` kind instead (`Animation.java:39-47`), so the converter lowers a scripted
condition to a parameter with that cache and names the parameter in `when`; a literal condition needs no parameter. `turn="true"` marks an IsTurn animation (`Move.java:107-139`). `<hotspots>` belong to a
variant.

**`<children>`.** Only for `Sequence` and `Select`.
- Each `<child animation="name">` names another action node.
- `<bind name value>` overrides one of its parameters for this reference. Local bindings win over the incoming map
  (`ActionRef.java:62-65`).
- `mode="sequence"` runs children in order and wraps when `loop` is set (`Sequence.java:25-41`). `mode="select"` runs
  the FIRST eligible child, chosen once at activation, and ends when it ends (`ComplexAction.java:34-84`).
- A child with no poses that ends at once is an instant action (`InstantAction.java:24-37`). An empty composite is
  refused.

**No edges on an action node.** An action never chains on by itself: when a behaviour's action ends, the behaviour
layer selects what follows (4.3). An action node's `<sequence>` is empty, and the validator refuses an edge on one.

### 4.2 Behaviour node, `<behaviour>`: one Shimeji behaviour

```xml
<animation id="200">
  <name>beh:SitDown</name>
  <start>...</start><end>...</end>
  <behaviour name="SitDown" runs="SitDown" frequency="200" hidden="false" toggle="" add="false">
    <bind name="Duration" value="100"/>
  </behaviour>
  <sequence repeat="0" repeatfrom="0">
    <next probability="100" when="mascot.totalCount < 50">201</next>
  </sequence>
</animation>
```

- `runs` names the root action node; `Behavior Action=` is kept, and defaults to the behaviour's name
  (`BehaviorBuilder.java:51-53`).
- `<bind>` carries the behaviour's extra attributes as overrides of that action's parameters
  (`BehaviorBuilder.java:72-78`).
- `frequency` 0 means transition-only. `hidden` is menu visibility only: a hidden behaviour with a positive frequency
  is still selected automatically (`BehaviorBuilder.java:144-174`). `toggle` names a header toggle id.
- The `<sequence>` edges are this behaviour's `NextBehaviorList` entries, in order, each with its `Frequency` as the
  probability and the conjunction of its enclosing `<Condition>`s and its own as `when`. They point at behaviour nodes
  and never at the route node: selection (4.3) reads the route's list itself.
- `add` says whether, when this behaviour ends, the root candidates stay in the list ahead of its own edges. Absent
  means `true`, which is the source's reading of a behaviour with no `NextBehaviorList`. A `NextBehaviorList` whose
  `Add` is false OR MISSING is `add="false"`: the source parses a missing attribute as false
  (`BehaviorBuilder.java:80-91`). With several `NextBehaviorList` elements, their entries append in order and the
  last one's `Add` decides. A `<NextBehavior>` element is not read at all in 1.0.23 (only `NextBehaviorList` is,
  `BehaviorBuilder.java:82`), so a behaviour carrying one keeps the root list and has no edges of its own, even though the
  official default configuration uses that element.
- A next-list entry is a behaviour of its own. The source builds it from the `BehaviorReference` element (its Name,
  Action, Frequency, Condition and extra attributes), not from the root definition of that name
  (`BehaviorBuilder.java:102-120`), so the converter lowers each entry to its own behaviour node unless it is identical
  to the root one. When it ends, the next selection uses the ROOT definition's list, found by name
  (`Configuration.java:208-212`).
- **Names.** The route's behaviours (the root list) have unique names, and every name lookup (Fall, a born or
  transform target, a hotspot) finds one of them. A behaviour node off the route, a next-list entry, carries the name of
  the root behaviour it stands for: the source finds what follows it by that name, and a name with no root behaviour
  ends the companion there, so the validator refuses one.

### 4.3 Route node, `<route>`: the root behaviour list

```xml
<animation id="1"><name>route</name>...<route/>
  <sequence repeat="0" repeatfrom="0">
    <next probability="40" when="...">200</next>
    ...
  </sequence>
</animation>
```

One per companion, and never entered as an animation. Its edges are the root `BehaviorList` in declaration order:
EVERY behaviour, a frequency-0 (transition-only) one included at probability 0, each `when` the conjunction of every
enclosing `<Condition>` with its own (conditions are parsed afresh per check, `BehaviorBuilder.java:144-156`).

**Selecting a behaviour** is synchronous and costs no tick (`Configuration.buildNextBehavior`,
`Configuration.java:184-258`). When a behaviour ends (4.5), in this order:

1. Every route edge's `when` is evaluated, in order, then its toggle; those that hold are candidates. This happens even
   when the ending behaviour's `add` is false and they are about to be discarded, because evaluating a condition can
   draw a random number.
2. If the ending behaviour's `add` is false, the candidates are cleared.
3. The ending behaviour's own edges are evaluated the same way and appended, in order.
4. If the candidates weigh 0 in total, the companion drops in from above at a random x (`builtin:no-candidate-drop-x`)
   and enters `Fall`. The validator allows a tick-mode list that weighs 0 for this reason.
5. Otherwise there is ONE draw (`builtin:behavior-select`), even for a single candidate: r = draw times the total
   weight, and the first candidate in list order that takes r below 0 when its weight is subtracted wins.

A companion's first behaviour, when no start behaviour is given, is selected from the route edges alone. The selected
behaviour is initialised in the same step (4.5).

**Special behaviours.** The host enters the behaviours the source binds by name on its lifecycle events:

| event | behaviour entered |
|---|---|
| ground lost | `Fall` |
| drag started | `Dragged` |
| released after a drag | `Thrown` |

To be confirmed by the parity lane against `UserBehavior` before freeze.

### 4.4 Limits in tick mode

| item | classic | tick mode | why |
|---|---|---|---|
| edges per list | 256 | 1024 | the corpus maximum is 276 root behaviours |
| animations | 1024 | 2048 | the corpus maximum is 968 actions plus 207 behaviours |
| params per node | — | 64 | |
| poses per variant | — | 512 | the corpus maximum is 311 |
| variants per action | — | 32 | the corpus maximum is 15 |
| children per composite | — | 64 | the corpus maximum is 20 |
| expression length | 256 | 4096 characters | |

### 4.5 One step of one companion

Each logical tick, every active companion steps in manager order (`Mascot.tick`, `UserBehavior.next`,
`UserBehavior.java:203-308`). A step does exactly this, and the host reproduces the order, the repeated checks and the
cache boundaries rather than a tidier equivalent:

1. **Check, then act.** The action is checked (`hasNext`: its guard `when` holds and its age is below its duration;
   for a composite, its active child is checked too, and checking a Sequence can move it on to its next eligible child
   and initialise that child). If the check holds, the action steps (`next`): its frame caches are cleared, the offer
   list is replaced by its offers, hotspots are taken from its current variant, and its effects run. A composite's
   effects are its active child's step.
2. **Hotspots.** If the user is holding the button over a hotspot, a hotspot with a behaviour enters it now; one
   without holds (a head-pat).
3. **Check again.** Unless a hotspot entered a behaviour, the action is checked a second time, with the caches step 1
   left. If it still holds and the companion is wholly off the screen, it drops in at a random x
   (`builtin:out-of-bounds-drop-x`) and enters `Fall`. If it no longer holds, the next behaviour is selected (4.3) and
   initialised now: instant actions at its start apply at once, and its first pose waits for the next step.
4. The companion's time advances by one tick.

Losing the ground anywhere in step 1 ends the behaviour and enters `Fall`. A guard is therefore evaluated twice in an
ordinary step, and the first evaluation of a step sees the frame cache the previous step's second check left.

---

## 5. Embedded classes (closed list)

Each row's semantics are the official source's; its paths (`src/com/group_finity/mascot/action/<Class>.java`) point into
the archive the parity tooling fetches. Parameter names are the source's English schema names
(`conf/schema_en.properties`). The source reads ONE vocabulary per document: Japanese when the root has a
`動作リスト` or `行動リスト` child, English otherwise, with exact matching and the Japanese schema falling back to English for
the keys it lacks (`Configuration.java:50-62`). The converter resolves names the same way. British spellings
(`BehaviourList`, `BornBehaviour`) are not names the source reads, so an attribute or element spelled that way is ignored
by the source and stays unconverted. Every row is proven by a parity scenario before `tools/ShimejiConvert/ACTIONS.md`
may call it supported.

| class | base | parameters (default) | host behaviour |
|---|---|---|---|
| Fall | — | InitialVX 0, InitialVY 0, RegistanceX .05, RegistanceY .1, Gravity 2 | Per tick: vx *= 1-rx, vy = vy*(1-ry)+g, with fractional accumulation and incremental collision against the contextual floor or wall. It ends on attachment, faces by vx, and publishes VelocityX/Y. |
| Jump | — | TargetX 0, TargetY 0, VelocityParam 20, Arc true | Re-aims every tick at the target, with an upward bias of abs(dx)/2 when Arc is set. It snaps once within speed, moves in integer steps, and publishes VelocityX/Y before applying the pose. |
| Dragged | — | OffsetX 0, OffsetY 120, OffsetType ImageAnchor | Follows the cursor, facing left. The FootX/FootDX sway is damped. A horizontal move of 5 or more resets the action age. The regist timer is 250 plus a random extension. |
| Regist | — (ActionBase, not Animate) | OffsetX 0, OffsetType ImageAnchor (OffsetY is NOT read) | Continues while abs(cursor.x - anchor.x + OffsetX) < 5, the cursor still on the companion (Regist.java:54). On its last tick it randomises facing and throws ground-lost. |
| Look | instant | LookRight (default: the opposite of now) | Sets facing. |
| Turn | — | LookRight (default: the opposite) | Plays the turn variant only when facing changes. |
| Offset | instant | X 0, Y 0 | anchor += (X, Y) in screen pixels. |
| Mute | instant | Sound (null) | A named Sound stops every PLAYING clip of the initiator's current type whose name starts with it (the source matches by prefix), at any volume, wherever it plays: other companions share the clip. With no Sound, and the initiator's sound enabled, every clip of every type stops. Each stop of a playing clip is a `sound` event with op `stop`. |
| BringToFront | instant | — | Raises THIS companion's window. |
| SelfDestruct | Animate | — | Disposes the live companion on the final tick. |
| Breed | Animate | BornX 0, BornY 0, BornBehavior "", BornMascot "", BornCount 1, BornTransient false | At age duration-1: BornCount companions of BornMascot (else self), at parent anchor + (facing right ? -BornX : +BornX, BornY), scaled. Each copies facing and enters BornBehavior, initialised before any other behaviour runs. Activation comes at the next manager step. |
| BreedMove, BreedJump, BreedFall | Move, Jump, Fall | the Breed parameters + BornInterval 1 | Breeds after movement when age % BornInterval == 0. BreedMove skips this while turning. |
| Transform | Animate | TransformMascot "", TransformBehavior "" | At age duration-1 (with the source's one-tick case), in place: image set, then behaviour. Identity, manager order, anchor, facing, variables and offspring are kept. |
| ScanMove | Move | Affordance, Behavior, TargetBehavior, TargetLook false | Picks the FIRST offering companion in stable order, once. On EXACT anchor arrival: own Behavior, then the target's TargetBehavior, then TargetLook. Loss ends the action. |
| ScanJump | Jump | the ScanMove parameters + VelocityParam 20, Arc true | As ScanMove, travelling as a jump, and snapping on arrival. |
| ScanInteract | Animate | the ScanMove parameters + Look true | Remote, and reacquires the target. At the end, a non-blank own Behavior (which also gates the target's), then TargetBehavior and TargetLook. Its precedence slip is not reproduced (`policy:scaninteract-precedence`). |
| Interact | Animate | Behavior | True while ANY two or more live companions share this anchor. At the end, a non-blank Behavior changes only this companion. |
| ComplexMove, ComplexJump | Move, Jump | Characteristics (exact comma split: Breed, Scan) + both sets | Combined. Arrival is guarded when Scan is off (`policy:complexmove-guarded`). |
| MoveWithTurn | Move | — | Two or more variants; the last is the turn. |
| Broadcast, BroadcastStay, BroadcastMove, BroadcastJump | Animate, Stay, Move, Jump | Affordance | Deprecated aliases. |
| WalkWithIE, FallWithIE, JumpWithIE | Move, Fall, Jump | IeOffsetX 0, IeOffsetY 0 | Windows opt-in (section 8). Exact grip at the active window's lower corner; the window moves with the companion; ground-lost on grip loss. |
| ThrowIE | Animate | InitialVX 32, InitialVY -10, Gravity .5 | Windows opt-in. Moves the window each tick by facing-signed vx and InitialVY + int(age * g). |
| MinimizeIE | Animate | — | Windows opt-in. Minimizes the active window at the end; standing on it is not required. |

Stay loops its variant under its guard, and pose velocity still applies. Move faces TargetX, moves by pose velocities
with a per-axis overshoot clamp, and ends when EITHER axis reaches its target (`Move.java:44-47`). Animate plays one
pass.

---

## 6. SafeExpression v2

**Grammar.**
- Values: numbers (double), booleans, `null`, strings (in quotes).
- Operators: `? :`, `|| && !`, `== != < <= > >=`, `+ - * / %`, unary `-`, parentheses.
- Member access over the fixed object model below. Calls only to whitelisted methods.
- No statements, no assignment except the variables form below, no loops, no closures, no `new`.
- Limits: 4096 characters, 1024 tokens, depth 32.
- Parsed once per definition into an immutable tree, with every name resolved at parse time. An unknown name is a
  parse error the validator names, together with the attribute it came from.

**Object model** (the source's JavaBean surface, `Mascot.java:674-858`, `MascotEnvironment.java:29-218`):

| expression | meaning |
|---|---|
| `mascot.anchor.x`, `.y` | the contact point |
| `mascot.lookRight`, `mascot.dragging` | facing, and whether being dragged |
| `mascot.count` | live companions of this current type |
| `mascot.totalCount` | all live companions |
| `mascot.variables['k']`, `mascot.variables.get('k')`, `mascot.variables.put('k', v)` | per-instance variables; they survive actions and Transform |
| `mascot.environment.cursor.x / .y / .dx / .dy` | the cursor |
| `mascot.environment.workArea.*`, `mascot.environment.screen.*` | Area fields |
| `mascot.environment.activeIE.*` | Area fields of the active window, with `visible` |
| `mascot.environment.floor.isOn(p)`, `ceiling.isOn(p)`, `wall.isOn(p)` | exact boundary tests |
| Area fields | `left`, `top`, `right`, `bottom`, `width`, `height`, `dleft`, `dtop`, `dright`, `dbottom`, `leftBorder.isOn(p)`, `topBorder.isOn(p)`, `rightBorder.isOn(p)`, `bottomBorder.isOn(p)`, `contains(x, y)` |
| parameters and locals | by bare name: `TargetX`, `VelocityX`, any `<param>` of the running action |
| `<constants>` | by bare name, in behaviour selection only |
| `Math.random()` | one draw at that site, each evaluation |
| `Math.abs/min/max/floor/ceil/round/sqrt/pow/sin/cos/atan2/PI` | the usual |

**Caching.** Both kinds are lazy: an expression is evaluated the first time its value is used, and the result is kept
(`Script.java:46-70`). `cache="action"` (`${}`) is cleared when the action is initialised, so it holds for the
activation. `cache="frame"` (`#{}`) is also cleared at the start of each of the action's steps (`ActionBase.next`,
`initFrame`, 4.5 step 1). A null result is never kept: it is evaluated again at its next use. Behaviour-selection
conditions are parsed and evaluated afresh at every selection.

**Beyond the subset.** The converter translates a source script into this subset. If it cannot, it keeps the attribute's
literal fallback, records the attribute as `approximate` with the reason, and never promotes the action to supported.

**Cost.** Expressions are evaluated where, and as often as, the source evaluates them, and never more to observe them.
There is no once-per-tick rule: a guard runs each time a step checks it (4.5), subject to its own cache, and each
selection evaluates every root condition once (4.3).

---

## 7. Social and lifecycle (runtime)

- **Offers.** Each action step replaces the companion's offer list with that action's `<offers>` (4.1, 4.5), so a
  later companion in manager order sees an earlier one's new offer in the same tick.
- **Scanning.** A scan takes the FIRST companion, in stable manager order, whose current offers include the affordance
  exactly:
  - spawned companions are included;
  - dragged companions are still eligible if they offer;
  - several seekers may pick the same target;
  - there is no reservation.
- **Partner transitions.** Each is applied by name in the partner's OWN definition. A missing name is a diagnostic,
  and the partner keeps what it was doing.
- **Births.**
  - The parent's action creates the child entity, initialises its type, position, facing and entry behaviour, and
    queues it; it activates at the next tick's step 2.
  - At the cap (16 companions on screen in total) the birth is refused with an event (`policy:birth-cap`).
  - Bred companions are session-only and never enter the saved companion mix (`policy:session-only-births`).
  - `BornTransient` selects which user toggle governs the birth (Transients or Breeding) and nothing else.
- **Transform.** In place, on the action clock. Children (graphical `<child>` sprites) close; independent offspring
  never do. The module handle and the companion's id are kept, and no second CompanionSpawned is raised. Until the new
  type's first pose (the next step), the companion still shows the image the old type's last pose set, sized and
  anchored as the old type's; the real runtime does the same (found by the vertical slice).
- **SelfDestruct.** Disposes the live companion. The saved mix is untouched, so the companion returns at the next
  launch.

---

## 8. Windows (opt-in per type, off by default)

**When the user's per-type toggle is ON,** the source semantics run, under these safety rules (each a recorded
divergence):
- never act on fullscreen, maximized, minimized, cloaked, elevated, hung or tool windows, or on this app's own windows;
- never act on a window the user is moving or resizing;
- a moved or thrown window stays fully inside its monitor's work area and never crosses monitors;
- every move is asynchronous, so a hung application cannot stall the companion;
- each session keeps a record of the windows it moved, and "Put windows back" restores them. The tray item appears
  only while that record holds a window, and says how many went back.

The toggle is "Let it move windows" on the companion's card in the Pets pane, offered only to a type that runs a window
action.

**When the toggle is OFF,** the action's animation plays and no window moves (`policy:window-off-plays`). The source ends
the action instead. Which window is "active" follows the source's environment rule; it is to be confirmed by the parity
lane before freeze.

---

## 9. Packs: install and resolution

- **Installing.** A pack installs as one unit: one companion type per member, each with its `<pack>` header. Hidden
  members (egg stages, helpers) stay out of the user's Add-a-companion list; they arrive through their pack.
- **Names.** A TransformMascot or BornMascot names a member KEY, matched exactly inside the referring type's pack. A
  member's key is the source's image-set name, so the converter writes the key the source's references use. Two packs
  with the same key never meet. A type outside any pack resolves only other types outside a pack, by name.
- **Resolution.** In this order: an empty name is the current type (the source's default); a member already loaded;
  a reference already known to be missing (no second lookup); the host's pack index, which maps (pack id, member key)
  to an installed type, library first, then bundled, and stages it the way any added companion is staged.
  - Within a root, folders are taken in ordinal case-insensitive order; a duplicate is logged and never wins.
  - Each id is read where staging reads it: a library copy shadows the bundled copy of the same id.
  - Previews resolve only inside their own preview namespace (the preview's id plus the pack id).
  - A staged member whose header is not the member asked for, or whose transparency differs from the referring
    type's, is refused.
  - The source loads every literally named member when it loads a set (`Main.java:443-470`); the host stages a member
    the first time it is named, which gives the same answers.
- **A member missing at runtime** falls back the way the source does. A Transform or Breed whose target is missing
  uses the current type (`Transform.java:50`, `Breed.java:76`), with one diagnostic per (type, reference). The
  diagnostic is the host's log, not a trace event: the source says nothing.
- **Lifetime.** A member's sprites stay loaded while any companion runs as it or shows an image from it. After a
  Transform the companion shows the old type's last image until the new type's first pose, so the old type is let go
  one step later. A pending birth holds its type from the step that queued it.
- **Startup.** Spawning a main member starts it as `<startup member behaviour>`. "Arrive as an egg" (per main
  member, default on) is offered only when the startup member's role is `egg`; off starts the main member with the
  route's choice. A new companion is then placed as the source places one (off screen, dropped in by its first step).
- **Studio** warns at import about every unresolved reference, and refuses only what cannot load.
- **Settings.** Each member keeps its OWN per-type settings (size, sound, monitor pin, toggles), as the source keys
  every setting by the current image set (section 2, `<toggles>`): a transformed companion takes the target type's
  settings. A hatch can therefore change size if the user sized the two members differently; Studio's pack view sets
  members together.

---

## 10. What the validator checks

- **Features.** The features gate runs first (section 1).
- **Schema.** The new elements are validated against the schema (both copies byte-identical).
- **Per document:**
  - every `class` is from the closed list;
  - `<children>` only on `Sequence` and `Select`, and never empty;
  - every name referenced inside the document resolves;
  - pose and variant limits (4.4);
  - expressions parse;
  - `cache` values are legal;
  - a tick-mode list may weigh 0 in total (the source's drop-in, 4.3); a classic list may not;
  - route and behaviour edges lead to behaviour nodes, and action nodes have none (4.1);
  - route behaviours have unique names, and an off-route behaviour carries a route behaviour's name (4.2);
  - `<startup>` only on a `main` member;
  - an action with Breed or Transform keeps a fallback edge, so a missing member does not respawn the companion;
  - what a document USES is declared, judged from what it contains: a scripted binding (behaviour or composite child)
    needs `expressions`; an expression the parser finds reading or writing `mascot.variables` needs `variables`; a
    `BornMascot` or `TransformMascot`, as a parameter or a binding, needs `packs`; ComplexMove and ComplexJump need
    `lifecycle` and `social` by their Characteristics, split on commas and compared exactly as the runtime does, and
    both when Characteristics is scripted;
  - composites are finite: none contains itself, none nests more than 32 deep, and one activation builds at most 4096
    actions (the runtime builds a fresh child per reference at every start);
  - header toggle ids are distinct, and a behaviour's `toggle` names one of them;
  - a tick-clock companion's sounds are named clips (section 3).
- **Per build:** a feature joins the build's supported set only when its runtime runs every class and construct it
  gates, and every embedded class is also asked whether THIS build constructs it, so a class whose family has not
  landed is refused by name even with every feature supported. Only the app fills the set: Studio and the converter
  compile the validator without the runtime, keep it empty and refuse tick companions by feature name until their own
  copies of a document carry every field.
- **Pack-level (`PackValidator`, over every member's parsed document; Studio and the host can both call it):**
  - every member is in the same pack, with one `main`, and member keys unique;
  - references resolve, or are reported as warnings (a scripted one is reported as known only at run time);
  - every member uses the same transparency mode;
  - the startup names a member and one of its behaviours;
  - a declared egg startup transforms back to its main member, directly or through other members.

## 11. Open points to settle before freeze

1. The special-behaviour names and when the host enters them (4.3), from `UserBehavior`.
2. The active-window rule used for `activeIE` and window actions (section 8), from the Windows environment.
3. Hotspot input semantics: when a press triggers its behaviour, held versus released state, and a hotspot with no
   behaviour (a held click or head-pat).

Settled: selection costs no tick and draws once (4.3); the step order, the repeated check and the lazy cache
boundaries are the source's (4.5, section 6). Both came from the parity lane's review of the source, before any
oracle trace contradicted the draft.
