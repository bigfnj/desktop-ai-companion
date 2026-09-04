# 03 — The Companion XML Format (`animations.xml`)

**This is the reference document for authoring pets.** Every desktopPet pet is a single
`animations.xml` file that embeds its own sprite sheet, icon, and sounds (as base64) and describes its
behaviour as a state machine. The same format is used by the WinForms engine *and* the browser port
[`web-esheep`](04-upstream-forks-ecosystem.md), so a well-formed companion is portable between them.

- **Schema:** [`Resources/animations.xsd`](../Resources/animations.xsd) (target namespace
  `https://esheep.petrucci.ch/`).
- **Worked example:** [`Companions/esheep64/animations.xml`](../Companions/esheep64/animations.xml) (the default
  sheep) and [`Companions/neko/animations.xml`](../Companions/neko/animations.xml) (has sounds).
- **How the runtime consumes each element:** [02 — Architecture](02-architecture.md).
- **Historical upstream reference:** the wiki pages at
  <https://github.com/Adrianotiger/desktopPet/wiki> (Introduction, Structure, Header, Image, Spawn,
  Animation, Child, Coordinate, Next). The current XSD, application validator, and runtime behavior
  described here are authoritative for this fork.

---

## 1. Document shape

The root `<animations>` element (namespace `https://esheep.petrucci.ch/`) contains a **fixed, ordered
sequence** of children (per the XSD `xsd:sequence`):

```
<animations xmlns="https://esheep.petrucci.ch/" ...>
  <header>   ... </header>     <!-- companion identity + icon           (required) -->
  <image>    ... </image>      <!-- sprite sheet + grid + key      (required) -->
  <spawns>   ... </spawns>     <!-- entry positions                (required) -->
  <animations> ... </animations> <!-- the animation state machine  (required, 1..n) -->
  <childs>   ... </childs>     <!-- companion sprites              (required element, 0..n children) -->
  <sounds>   ... </sounds>     <!-- MP3s keyed to animations       (optional) -->
</animations>
```

> **Gotcha:** the root element and the `<animations>` list element have the **same tag name**. The inner
> one is the container for `<animation>` entries.

Numbers written where the schema type is `xsd:string` (all `<x>`, `<y>`, `<interval>`, `repeat`) are
**expressions**, not plain integers — see [§8](#8-the-expression-language). Binary blobs
(`<icon>`, `<png>`, `<base64>`) are base64, usually wrapped in `<![CDATA[ ... ]]>`.

---

## 2. `<header>` — companion identity

`xsd:all` (order-free). All elements required unless noted.

| Element | Type | Meaning |
|---------|------|---------|
| `<author>` | string | Author name. |
| `<title>` | string | Title shown on the webpage/gallery. |
| `<petname>` | string | Pet name shown in the context menu. **Truncated to 16 chars** by the engine (`Xml.ReadXML`). |
| `<version>` | string | Companion version (e.g. `1.8`). "Once published you can't change it" (XSD note). |
| `<info>` | string | Free text: credits, email, links. Supports `[br]` (line break) and one `[link:https://...]` About link. The user must select the link before DesktopAICompanion asks the default browser to open it; authored About links must use absolute HTTPS URLs. |
| `<application>` | integer | **Must be `1`** — the only format version (XSD note). |
| `<icon>` | string (base64) | A **48×48 ICO**, base64-encoded, in CDATA. Becomes the tray/taskbar icon. |

Example (from esheep64):

```xml
<header>
  <author>Adriano</author>
  <title>eSheep 64bit</title>
  <petname>eSheep</petname>
  <version>1.8</version>
  <info>Open source project for the lovely eSheep.[br] For more info, visit my webpage [link:https://esheep.petrucci.ch] [br]Image rip by LiL_Stenly</info>
  <application>1</application>
  <icon><![CDATA[AAABAAEAMDAAAAEAIA...==]]></icon>
</header>
```

---

## 3. `<image>` — the sprite sheet

`xsd:all`. Defines the sheet and how it is diced into frames.

| Element | Type | Meaning |
|---------|------|---------|
| `<tilesx>` | integer | Number of columns of cells in the sheet. |
| `<tilesy>` | integer | Number of rows of cells. |
| `<png>` | string (base64) | The whole sprite sheet as a base64 PNG, in CDATA. |
| `<transparency>` | string | Color key treated as transparent. **Default `Magenta`** (`#FF00FF`). |

The engine decodes the PNG and slices it into `tilesx × tilesy` **equal-sized** cells; cell size =
`sheetWidth/tilesx` by `sheetHeight/tilesy` (`Xml.ReadImages`). **Every cell must be the same size** and
the grid must be exact. esheep64 is `16 × 11 = 176` frames.

```xml
<image>
  <tilesx>16</tilesx>
  <tilesy>11</tilesy>
  <png><![CDATA[iVBORw0KGgoAAAANSUhEUgAA...]]></png>
  <transparency>Magenta</transparency>
</image>
```

### Frame indexing (critical)

Frames are numbered **row-major starting at 0**: index 0 is the top-left cell, index `tilesx-1` ends the
first row, index `tilesx` starts the second row, and so on. Every `<frame>` in an animation references
one of these indices. Any pixel equal to the transparency color is not painted (the window uses a
magenta `TransparencyKey` — see [02 §6.1](02-architecture.md#61-rendering-a-magenta-keyed-layered-tool-window)).

> **Direction convention.** Draw the companion facing one direction; the engine mirrors the whole sheet at
> runtime with the `flip` action (see [§5.3](#53-action)). By convention the base sprites face such that
> the default `walk` moves left (esheep64 `walk` uses `<x>-2</x>`).

---

## 4. `<spawns>` — where a companion appears

One or more `<spawn>` entries. When a companion needs to (re)appear, the engine picks one **weighted by
`probability`** (`Animations.GetRandomSpawn`), places the companion at the spawn's `<x>,<y>`, and starts the
animation named by `<next>`.

`<spawn>` attributes: `id` (int), `probability` (int, a relative weight).
`<spawn>` children (`xsd:all`): `<x>`, `<y>` (expressions), `<next>` (target animation id; carries an
optional `probability` attribute).

```xml
<spawns>
  <spawn id="1" probability="20">          <!-- 20% of spawns: walk in from the right, on the taskbar -->
    <x>screenW+10</x>
    <y>areaH-imageH</y>
    <next probability="100">1</next>
  </spawn>
  <spawn id="2" probability="80">          <!-- 80%: drop in from above at a random x -->
    <x>random*(screenW-imageW-50)/100+25</x>
    <y>-imageH-20</y>
    <next probability="100">1</next>
  </spawn>
</spawns>
```

`areaH-imageH` puts the companion's feet on the taskbar; `-imageH-20` starts it just above the top edge so it
falls into view. Probabilities are relative weights, not required to total 100.

---

## 5. `<animations>` / `<animation>` — the state machine

The container `<animations>` holds one or more `<animation>` nodes. **Each `<animation>` is a state**;
`<next>` edges (weighted, situation-filtered) are the transitions.

`<animation>` attribute: `id` (int, unique).
`<animation>` children (`xsd:all`):

| Element | Req. | Meaning |
|---------|------|---------|
| `<name>` | yes | Human name. Four names are **magic**: `fall`, `drag`, `kill`, `sync` (see [§7](#7-magic-animation-names)). Others are free (`walk`, `sleep1a`, `jump`, …). |
| `<start>` | yes | Movement/appearance at the first step. A **step** group. |
| `<end>` | yes | Movement/appearance at the last step. Values are **interpolated** `start → end` across the sequence. Use the same values as `<start>` when no interpolation is wanted. |
| `<sequence>` | yes | The frames to play + the "sequence finished" transitions. |
| `<border>` | no | Transitions taken when a **border** is hit. Presence sets the `Border` flag. |
| `<gravity>` | no | Transitions taken when there's **nothing underneath**. Presence sets the `Gravity` flag. |

### 5.1 The `step` group (`<start>` / `<end>`)

`xsd:all`:

| Element | Type | Default | Meaning |
|---------|------|---------|---------|
| `<x>` | string (expr) | — | Horizontal **velocity** in px per step (negative = the sprite's facing direction). |
| `<y>` | string (expr) | — | Vertical velocity in px per step (positive = down). |
| `<offsety>` | integer | `0` | Vertical *image* offset — shifts the drawn sprite without moving the collision position (used for climbing/peeking). |
| `<opacity>` | double | `1.0` | 0.0 transparent … 1.0 opaque. Interpolated for fades. |
| `<interval>` | string (expr) | — | Milliseconds between steps (the timer interval). Interpolated `start → end`, so an animation can accelerate (see `fall`). |

Because `<x>/<y>` are **velocities**, a long straight walk is a 2-frame sequence with a big `repeat`,
not hundreds of frames.

### 5.2 `<sequence>` — frames, repeat, and "next"

`<sequence>` attributes: `repeat` (string/expr — how many extra times to loop), `repeatfrom` (int — the
0-based frame to loop back to). Its children are a `choice` (any order/count) of:

- `<frame>` (int) — a sprite index to show, in order. One per step.
- `<action>` (string) — a sequence-level action (see [§5.3](#53-action)).
- `<next>` — a transition taken when the sequence **finishes** (see [§6](#6-next-the-transitions)).

Total steps played = `Frames.Count + (Frames.Count - repeatfrom) * repeat`
(`TSequence.CalculateTotalSteps`). So `repeat="20" repeatfrom="0"` on a 2-frame walk plays 42 steps
before the sequence-finished `<next>` fires.

```xml
<animation id="1">
  <name>walk</name>
  <start><x>-2</x><y>0</y><interval>200</interval><offsety>0</offsety><opacity>1.0</opacity></start>
  <end>  <x>-2</x><y>0</y><interval>200</interval><offsety>0</offsety><opacity>1.0</opacity></end>
  <sequence repeat="20" repeatfrom="0">
    <frame>2</frame>
    <frame>3</frame>
    <next probability="2"  only="window">11</next>
    <next probability="10" only="taskbar">35</next>
    <next probability="90" only="none">1</next>
    <next probability="6"  only="none">15</next>
    <next probability="50" only="taskbar">50</next>
    <next probability="50" only="window">49</next>
  </sequence>
  <border>
    <next probability="100" only="none">2</next>
    <next probability="2"   only="vertical">37</next>
    <next probability="20"  only="window">43</next>
  </border>
  <gravity>
    <next probability="100" only="none">5</next>
  </gravity>
</animation>
```

Reading that: the sheep walks left (`x=-2`) looping its 2 frames ~20 times; when the loop ends it picks
a next state weighted by where it is (`only`) — usually keep walking (`id 1`, weight 90) but sometimes a
window/taskbar-specific behaviour. If it hits a **vertical border** (screen left/right) it plays `id 2`
(turn around). If there's **nothing under it** (`gravity`) it plays `id 5` (`fall`).

### 5.3 `<action>`

A sequence-level tag. Only **`flip`** is implemented by the engine (`FormCompanion.NextStep`): when the
sequence finishes, every frame bitmap is mirrored horizontally and `IsMovingLeft` toggles — this is how
the companion turns around (see esheep64 `rotate1a`, which flips then transitions to `rotate1b`). `<action>none</action>`
(used throughout the neko companion) is an explicit no-op. Omit `<action>` when no action is needed. The
current validator accepts only the literal actions `flip` and `none`; any other non-empty value rejects
the companion before it is loaded.

```xml
<animation id="2">
  <name>rotate1a</name>
  <start><x>0</x><y>0</y><interval>200</interval></start>
  <end>  <x>0</x><y>0</y><interval>200</interval></end>
  <sequence repeat="0" repeatfrom="0">
    <frame>3</frame><frame>9</frame><frame>10</frame>
    <next probability="100">3</next>
    <action>flip</action>
  </sequence>
</animation>
```

---

## 6. `<next>` — the transitions

`<next>` is the single most important element. It appears inside `<sequence>`, `<border>`, `<gravity>`,
`<spawn>`, and `<child>`. Its **text content is the target animation id**; its attributes are:

| Attribute | Type | Meaning |
|-----------|------|---------|
| `probability` | integer | **Relative weight** among the eligible `<next>` entries in the same list (not a percentage; they need not sum to 100). |
| `only` | enum | Situation filter. One of: `none`, `taskbar`, `window`, `horizontal`, `horizontal+`, `vertical`. |

### How selection works (see [02 §5.1](02-architecture.md#51-how-what-happens-next-is-chosen--setnextgeneralanimation))

When a list is evaluated, the engine is told the current *situation* (`where`). It keeps only the
`<next>` entries whose `only` matches, sums their weights, and rolls a weighted random choice.

- `only="none"` matches **every** situation (the `NONE` flag is `0x7F`) — a safe default/fallback.
- `only="taskbar"` — only when the companion is on the taskbar.
- `only="window"` — only when standing on another window's title bar.
- `only="vertical"` — only at the left/right **screen** border.
- `only="horizontal"` — only at the top (and bottom) screen border.
- `only="horizontal+"` — horizontal **or** window (`0x06`).

The three lists differ only in *when* they're consulted:

| List | Consulted when | Situation passed |
|------|----------------|------------------|
| `<sequence>`'s `<next>` | the frame sequence finishes | `TASKBAR` if the companion is at the bottom, else `NONE` |
| `<border>`'s `<next>` | a border is detected | `VERTICAL` / `HORIZONTAL` / `TASKBAR` / `WINDOW` |
| `<gravity>`'s `<next>` | nothing is underneath | `NONE` or `WINDOW` |

> **The respawn rule.** If, in the relevant situation, **no `<next>` is eligible**, the selector returns
> `-1` and the companion **respawns** (a fresh `<spawn>`). This is intentional and is how walking off the edge
> or finishing a terminal animation loops the companion. It's also the most common authoring bug: forgetting a
> `only="none"` fallback makes a companion vanish and reappear unexpectedly. (`Animations.SetNextGeneralAnimation`;
> the `TNextAnimation` remarks say so explicitly.)

`<border>` and `<gravity>` may each hold up to **256** `<next>` entries (XSD
`maxOccurs="256"`).

---

## 7. Magic animation names

Four `<name>` values are wired to engine lifecycle events (`Xml.LoadAnimations`; used in
`FormCompanion`/`Animations`):

| `<name>` | Triggered when | Field |
|----------|----------------|-------|
| `drag` | user left-presses/holds the companion | `AnimationDrag` |
| `fall` | companion is released after a drag, or gravity fires (via a `<gravity><next>`) | `AnimationFall` |
| `kill` | the app is closing — the companion plays this then fades to opacity 0 and closes | `AnimationKill` |
| `sync` | "synchronise all companions" (About-box cancel / `SyncSheeps`) | `AnimationSync` |

If there is **no** `kill` animation, the companion closes immediately (`Changelog.txt` 0.9.3). Give a companion a
`drag`/`fall` pair if you want it to react to being picked up.

---

## 8. The expression language

Every `<x>`, `<y>`, `<interval>`, and the `repeat` attribute is a string **arithmetic expression**
evaluated by the runtime's restricted `SafeExpression` parser (`Xml.GetXMLCompute`). It supports
numeric literals, known variables, parentheses, unary `+`/`-`, binary `+ - * / %`, and the single
conversion form `Convert(value,System.Int32)`, with normal arithmetic precedence. Unknown variables,
functions, or other .NET expression syntax are rejected. Substituted tokens:

| Token | Value |
|-------|-------|
| `screenW`, `screenH` | monitor width / height (full bounds) |
| `areaW` | working-area width |
| `areaH` | usable bottom = `WorkingArea.Height + WorkingArea.Y` (top of the taskbar) |
| `imageW`, `imageH` | one sprite cell's width / height |
| `imageX`, `imageY` | the **parent** companion's left/top — for placing **children** relative to the parent |
| `random` | fresh integer 0–99 **on every evaluation** |
| `randS` | integer 0–99 **fixed until the next spawn** (per-companion spawn seed) |
| `scale` | current HD scale factor |

Notes:
- Using `imageX`, `imageY`, `random`, or `randS` marks the value **dynamic** — it is recomputed each time
  the animation starts (`TValue.IsDynamic`). Using `screen…`/`area…` marks it **screen-dependent**
  (recomputed per monitor on multi-screen).
- `random` changing every step vs `randS` staying fixed is deliberate: use `randS` when you want a value
  that's consistent for one "life" of the companion (e.g. a random landing height chosen at spawn), `random`
  for jitter.
- Example: `random*(screenW-imageW-50)/100+25` → a random x within the screen, 25px inset from the edges.

---

## 9. `<childs>` / `<child>` — companion sprites

A `<child>` links an **animation id** to a second sprite that the companion spawns *when that animation plays*.
This is how the sheep produces a mate, flowers, a bath, a second interacting sprite, etc.

`<child>` attribute: `animationid` (int — the parent animation whose start spawns this child).
`<child>` children (`xsd:all`): `<x>`, `<y>` (expressions, usually relative to `imageX`/`imageY`),
`<next>` (the animation the child itself starts with).

```xml
<childs>
  <child animationid="21">
    <x>screenW+10-areaH/2-(randS*areaH/2)/120</x>
    <y>areaH-imageH</y>
    <next>23</next>
  </child>
  <child animationid="26">
    <x>imageX-imageW*0.9</x>   <!-- placed just left of the parent -->
    <y>imageY</y>
    <next>27</next>
  </child>
</childs>
```

Runtime behaviour (see [02 §7](02-architecture.md#7-multiple-companions--children)): a child shares the
parent's sprite set, **can't be picked up**, **auto-closes when its sequence ends** (children have no
spawn), and nesting is capped at **5 levels**. Multiple `<child>` entries may share one `animationid`
(all spawn together).

---

## 10. `<sounds>` / `<sound>` — audio (optional)

Optional. Each `<sound>` ties an MP3 to an animation id; when that animation starts the engine rolls
`probability`% and, on success, plays it (looped `loop` extra times) at the current volume
(`Animations.AddSound` / `TSound`).

`<sound>` attribute: `animationid` (int).
`<sound>` children (`xsd:all`): `<probability>` (int, %), `<loop>` (int, optional — extra repeats),
`<base64>` (base64 MP3; a leading `data:...;base64,` prefix is stripped automatically).

```xml
<sounds>
  <sound animationid="6">
    <probability>2</probability>
    <loop>2</loop>
    <base64>SUQzBAAAAAAA...</base64>
  </sound>
  <sound animationid="2">
    <probability>1</probability>
    <base64>SUQzBAAAAAAA...</base64>
  </sound>
</sounds>
```

Multiple sounds may target the same `animationid` (see neko). If audio init fails the engine silently
disables volume rather than crashing.

---

## 11. How to author a new companion — walkthrough

1. **Make the sprite sheet.** A grid of equal cells, every frame the companion needs (walk, turn, fall, sleep,
   special actions, and any child sprites). Use the transparency color — **magenta `#FF00FF`** — for all
   background/empty pixels. Draw the companion facing one direction (the engine mirrors with `flip`). Note the
   `tilesx × tilesy` grid dimensions.
2. **Make a 48×48 icon**, convert PNG→ICO, then base64-encode it. (Petrucci's site has converters:
   <https://esheep.petrucci.ch/?p=tools&s=icon> and `?s=base64`, referenced in the XSD annotations.)
3. **Base64-encode the sprite sheet PNG** and the sounds (MP3) you want.
4. **Write `<header>` and `<image>`** with the correct `tilesx`/`tilesy` and the base64 blobs.
5. **Define `<spawns>`** — at least one entry position and its first animation.
6. **Define `<animations>`.** Reference frames by 0-based row-major index. For each state wire:
   - `<sequence>` frames + `repeat`,
   - sequence-finished `<next>` (always include a `only="none"` fallback unless you *want* a respawn),
   - `<border>` (turn around / react) and `<gravity>` (fall) if the state can hit an edge or empty space.
   Give it `drag`, `fall`, and ideally `kill`/`sync` animations by those exact names.
7. **Add the required `<childs>` container.** It may contain zero or more `<child>` entries; use
   `<childs />` when the companion has none. Add the optional `<sounds>` container only when audio is needed.
8. **Validate.** Validate against [`Resources/animations.xsd`](../Resources/animations.xsd), then test
   with the current application validator. [`Tools/PetTester`](../Tools/PetTester) supplies additional
   diagnostics, some of which are stricter authoring recommendations rather than runtime requirements.
   `Tools/PetEditor` is retained as unsupported legacy source and must not be used as the authority for
   current-format validity. The upstream online editor is likewise a legacy aid and may not enforce the
   current application's semantic and resource limits.
9. **Test live.** Run a companion and **drag-and-drop your `animations.xml` onto it** — the engine hot-loads it
   (`FormCompanion.Form2_DragDrop`); on a parse error it falls back to the default sheep and shows the error.
   Or launch with `DesktopAICompanion.exe localxml=yourpet.xml`.
10. **Publish (optional, upstream).** Add a folder under [`Companions/`](../Companions) containing `animations.xml`,
    `README.md` (your "about" text, shown in-app), and `icon.png`; then add an entry (`folder`, `author`,
    `lastupdate`) to [`Companions/companions.json`](../Companions/companions.json). See [`Companions/README.md`](../Companions/README.md).

---

## 12. Quick element index

| Element | Parent | Key attrs | Purpose |
|---------|--------|-----------|---------|
| `animations` (root) | — | — | document root, namespaced |
| `header` | root | — | identity + icon |
| `image` | root | — | sprite sheet + grid + transparency key |
| `spawns` / `spawn` | root | `id`,`probability` | weighted entry positions |
| `animations` (list) | root | — | container for states |
| `animation` | animations | `id` | one state |
| `name` | animation | — | free name; `fall`/`drag`/`kill`/`sync` are magic |
| `start` / `end` | animation | — | movement/appearance (interpolated) |
| `x`/`y`/`interval` | start/end/spawn/child | — | **expressions** (velocity / ms) |
| `offsety`/`opacity` | start/end | — | draw offset / fade |
| `sequence` | animation | `repeat`,`repeatfrom` | frames + finish transitions |
| `frame` | sequence | — | 0-based sprite index |
| `action` | sequence | — | `flip` (mirror + turn) or `none` |
| `border` | animation | — | transitions on a border hit (≤256 next) |
| `gravity` | animation | — | transitions when unsupported (≤256 next) |
| `next` | sequence/border/gravity/spawn/child | `probability`,`only` | weighted, situation-filtered transition; text = target id |
| `childs` / `child` | root | `animationid` | companion sprite spawned by an animation |
| `sounds` / `sound` | root | `animationid` | MP3 keyed to an animation (optional) |
| `probability`/`loop`/`base64` | sound | — | %, extra repeats, base64 MP3 |

See also: [02 — Architecture](02-architecture.md) · [05 — Glossary &amp; FAQ](05-glossary-and-faq.md).
