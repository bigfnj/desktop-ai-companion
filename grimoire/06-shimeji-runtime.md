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

---

## 3. Image and pose additions

- **`<image><anchorx>`, `<anchory>`.** The contact point (Shimeji's ImageAnchor) in cell pixels, before scaling. The
  converter bakes every pose so its anchor lands on this one point. The default is bottom-centre, (cellW/2, cellH).
- **`<pose>`.** Replaces `<frame>` inside `<variant>`:
  `<pose frame="12" right="40" ticks="6" dx="-2" dy="0" sound="s3" volume="-6"/>`
  - `frame` is a sheet index, or `-1` for an image-less pose. It HIDES the companion's window while the companion
    keeps its timing, motion, sound and count (`Pose.java:70-85`, `ImagePairs.java:61-65`, `Mascot.java:587-609`).
  - `right` is the right-facing frame (`ImageRight`). Omitted, the left frame is mirrored, as today.
  - `ticks` is the pose duration (1 to 100000). `dx`/`dy` is the pose's per-tick velocity; it is mirrored when
    facing right (`Pose.java:70-85`, right-facing subtracts).
  - `sound` names a `<sound id>`; `volume` is the pose's Volume.
  - The pose for a given action age is chosen by age modulo the summed durations (`Animation.java:54-78`).
- **`<sound id="...">`.** A new optional attribute, so poses can name clips. `animationid` stays for classic use.

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
    <param name="TargetX" value="${mascot.environment.cursor.x}" cache="action"/>
    <param name="Duration" value="250"/>
    <guard when="#{mascot.environment.floor.isOn(mascot.anchor)}" duration="Duration" draggable="true"/>
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
- `<param name value cache>` declares a parameter's default. `value` is a literal or an expression (section 6).
  `cache` is `action` (source `${}`: evaluated once per action activation), `frame` (source `#{}`: once per tick) or
  `none` (a literal).
- The `<guard>` attributes name parameters or hold expressions:
  - `when` is the source Condition. False ends the action, as `ActionBase.hasNext` does.
  - `duration` is in ticks. The action ends when its age reaches it.
  - `draggable` is queried only when the user presses on the companion. False refuses the drag; it never ends the
    action. A composite's draggability is its active child's.
- **Frames.** Every activation of an action gets a fresh parameter and cache frame, per companion. Definitions are
  immutable and shared. Locals the source publishes (`TargetX`, `TargetY`, `VelocityX`, `VelocityY`, `FootX`,
  `FootDX`) live in that frame.

**`<offers>`.** Up to 8 affordance values (literal or expression) broadcast while this action runs. The list is rebuilt
every tick (`ActionBase.java:88-101`).

**`<variants>`.** The source's `<Animation>` blocks, in order. Each tick the FIRST variant whose `when` holds plays
(`ActionBase.java:122-164`). `turn="true"` marks an IsTurn animation (`Move.java:107-139`). `<hotspots>` belong to a
variant.

**`<children>`.** Only for `Sequence` and `Select`.
- Each `<child animation="name">` names another action node.
- `<bind name value>` overrides one of its parameters for this reference. Local bindings win over the incoming map
  (`ActionRef.java:62-65`).
- `mode="sequence"` runs children in order and wraps when `loop` is set (`Sequence.java:25-41`). `mode="select"` runs
  the FIRST eligible child, chosen once at activation, and ends when it ends (`ComplexAction.java:34-84`).
- A child with no poses that ends at once is an instant action (`InstantAction.java:24-37`). An empty composite is
  refused.

**`<sequence><next>` on an action node.** These edges are taken only when the action is a behaviour's root and ends
without the behaviour layer redirecting it. Normally the behaviour layer (4.2) decides what follows.

### 4.2 Behaviour node, `<behaviour>`: one Shimeji behaviour

```xml
<animation id="200">
  <name>beh:SitDown</name>
  <start>...</start><end>...</end>
  <behaviour name="SitDown" runs="SitDown" frequency="200" hidden="false" toggle="">
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
- The `<sequence>` edges are this behaviour's `NextBehaviorList`, each with its `frequency` as the probability and its
  condition as `when`. `Add="true"` is written as one more edge, to the route node. `Add="false"` means no such edge,
  so the list replaces the root set.

### 4.3 Route node, `<route>`: the root behaviour list

```xml
<animation id="1"><name>route</name>...<route/>
  <sequence repeat="0" repeatfrom="0">
    <next probability="40" when="...">200</next>
    ...
  </sequence>
</animation>
```

One per companion. Its edges are the root `BehaviorList`: each behaviour with a positive frequency, its condition
being the conjunction of every enclosing `<Condition>` with its own (conditions are parsed afresh per check,
`BehaviorBuilder.java:144-156`). Selection weighs only the edges whose `when` holds, evaluated once per selection;
each condition is evaluated exactly once, and the result is reused for the weighting.

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

---

## 5. Embedded classes (closed list)

Each row's semantics are the official source's; its paths (`src/com/group_finity/mascot/action/<Class>.java`) point into
the archive the parity tooling fetches. Parameter names are the source's English schema names
(`conf/schema_en.properties`); the converter maps the Japanese and British aliases to them. Every row is proven by a
parity scenario before `tools/ShimejiConvert/ACTIONS.md` may call it supported.

| class | base | parameters (default) | host behaviour |
|---|---|---|---|
| Fall | — | InitialVX 0, InitialVY 0, RegistanceX .05, RegistanceY .1, Gravity 2 | Per tick: vx *= 1-rx, vy = vy*(1-ry)+g, with fractional accumulation and incremental collision against the contextual floor or wall. It ends on attachment, faces by vx, and publishes VelocityX/Y. |
| Jump | — | TargetX 0, TargetY 0, VelocityParam 20, Arc true | Re-aims every tick at the target, with an upward bias of abs(dx)/2 when Arc is set. It snaps once within speed, moves in integer steps, and publishes VelocityX/Y before applying the pose. |
| Dragged | — | OffsetX 0, OffsetY 120, OffsetType ImageAnchor | Follows the cursor, facing left. The FootX/FootDX sway is damped. A horizontal move of 5 or more resets the action age. The regist timer is 250 plus a random extension. |
| Regist | Animate | OffsetX 0, OffsetType ImageAnchor (OffsetY is NOT read) | Continues while the cursor's dx < 5. At the end it randomises facing and throws ground-lost. |
| Look | instant | LookRight (default: the opposite of now) | Sets facing. |
| Turn | — | LookRight (default: the opposite) | Plays the turn variant only when facing changes. |
| Offset | instant | X 0, Y 0 | anchor += (X, Y) in screen pixels. |
| Mute | instant | Sound (null) | A named Sound stops the matching shared or per-type clips, which may belong to other companions. With no Sound, and the initiator's sound enabled, every cached clip stops. |
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

**Caching.** `cache="action"` (`${}`) is evaluated at activation and kept for the activation. A null result is evaluated
again the next time (`Script.java:46-70`). `cache="frame"` (`#{}`) is evaluated once per tick.

**Beyond the subset.** The converter translates a source script into this subset. If it cannot, it keeps the attribute's
literal fallback, records the attribute as `approximate` with the reason, and never promotes the action to supported.

**Cost.** Conditions are evaluated only where the source evaluates them. Each guard runs once per tick per active action,
and selection conditions run once per selection.

---

## 7. Social and lifecycle (runtime)

- **Offers.** Each companion's offer list is rebuilt from its active action's `<offers>` every tick.
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
  never do. The module handle and the companion's id are kept, and no second CompanionSpawned is raised.
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
- each session keeps a record of the windows it moved, and "Put windows back" restores them.

**When the toggle is OFF,** the action's animation plays and no window moves (`policy:window-off-plays`). The source ends
the action instead. Which window is "active" follows the source's environment rule; it is to be confirmed by the parity
lane before freeze.

---

## 9. Packs: install and resolution

- **Installing.** A pack installs as one unit: one companion type per member, each with its `<pack>` header.
- **Resolution.** The host's pack index maps (pack id, member key) to an installed type, library first, then bundled.
  - Duplicates resolve deterministically and are logged.
  - Previews resolve only inside their own preview namespace.
- **A member missing at runtime** falls back the way the source does. A Transform or Breed whose target is missing
  uses the current type (`Transform.java`, `Breed.java:73-113`), with one diagnostic per (type, reference).
- **Studio** warns at import about every unresolved reference, and refuses only what cannot load.
- **Settings sharing.** Members share the main member's per-type settings (size, sound, monitor pin, toggles), so a
  hatch does not visibly resize or change its settings.

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
  - `<startup>` only on a `main` member;
  - an action with Breed or Transform keeps a fallback edge, so a missing member does not respawn the companion.
- **Pack-level (`PackValidator`):**
  - one `main`, and member keys unique;
  - references resolve, or are reported;
  - every member uses the same transparency mode;
  - a declared egg startup transforms back to its main member.

## 11. Open points to settle before freeze

1. The special-behaviour names and when the host enters them (4.3), from `UserBehavior`.
2. The active-window rule used for `activeIE` and window actions (section 8), from the Windows environment.
3. Hotspot input semantics: when a press triggers its behaviour, held versus released state, and a hotspot with no
   behaviour (a held click or head-pat).
4. Whether a routing tick costs a logical tick (route node evaluation timing), measured against the oracle.
