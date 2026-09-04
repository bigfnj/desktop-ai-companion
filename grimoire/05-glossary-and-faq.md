# 05 — Glossary &amp; FAQ

Terminology and practical questions. Terms link to the fuller treatment in
[02 — Architecture](02-architecture.md) and [03 — Companion XML Format](03-companion-xml-format.md).

## Glossary

**animations.xml** — The single self-contained file that *is* a companion: identity, sprite sheet, sounds
(all base64), and the behaviour state machine. Portable between the desktop engine and the web port.
See [03](03-companion-xml-format.md).

**Sprite sheet** — One PNG image holding all of a companion's frames in a `tilesx × tilesy` grid of
equal-sized cells, base64-embedded in `<image><png>`. The engine slices it into individual frame bitmaps
at load. See [02 §4](02-architecture.md#4-loading-a-companion--xmlcs).

**Frame** — One cell of the sprite sheet, referenced by a **0-based, row-major index** (`<frame>`).
Index 0 = top-left cell.

**Transparency key** — The color drawn as transparent, default **Magenta `#FF00FF`** (`<transparency>`).
Implemented as the WinForms `TransparencyKey` on a borderless layered window, so magenta pixels aren't
painted. See [02 §6.1](02-architecture.md#61-rendering-a-magenta-keyed-layered-tool-window).

**Animation (state)** — A named `<animation id="…">`: a frame sequence plus movement and the transition
lists that decide what happens next. The companion is always "in" exactly one animation.

**Sequence** — The ordered `<frame>` list of an animation, with `repeat`/`repeatfrom` looping. One frame
is shown per timer step; total steps are precomputed.

**Step / interval** — One tick of the companion's timer. `<interval>` is the milliseconds between steps, and is
interpolated from `<start>` to `<end>` so an animation can speed up or slow down.

**Movement / velocity** — `<x>`/`<y>` are **per-step pixel velocities**, not absolute positions
(`+y` = down, `-x` = the sprite's facing direction). This is why a straight walk is a 2-frame sequence
with a large `repeat`.

**Expression** — The `<x>/<y>/<interval>/repeat` strings are arithmetic expressions evaluated at runtime
(`screenW`, `areaH`, `imageW`, `imageX`, `random`, `randS`, `scale`, …). See
[03 §8](03-companion-xml-format.md#8-the-expression-language).

**`random` vs `randS`** — `random` re-rolls 0–99 on **every** evaluation (jitter); `randS` is fixed for
the companion's life until its next spawn (a stable per-life value).

**next / transition** — A `<next>` edge (weighted by `probability`, filtered by `only`) to another
animation id. Its text content is the target id. See [03 §6](03-companion-xml-format.md#6-next-the-transitions).

**`only` flag** — The situation filter on a `<next>`: `none` (always), `taskbar`, `window`, `vertical`
(left/right screen edge), `horizontal` (top/bottom edge), `horizontal+` (horizontal or window). Only
`<next>` entries matching the current situation are eligible.

**Border animation** — The `<border>` transition list, consulted when the companion hits a screen edge, the
taskbar, or a window edge. See [02 §6.2](02-architecture.md#62-the-loop-play--timer1_tick--nextstep).

**Gravity animation** — The `<gravity>` transition list, consulted when nothing is underneath the companion
(so it should fall). If an animation has no `<gravity>`, the companion never falls out of it.

**Respawn rule** — If the eligible `<next>` list for the current situation is empty, the selector returns
`-1` and the companion starts over from a fresh `<spawn>`. The commonest authoring surprise. See
[03 §6](03-companion-xml-format.md#6-next-the-transitions).

**Spawn** — A weighted entry configuration (`<spawn>`): where a companion (re)appears and which animation it
starts. Chosen probabilistically by `Animations.GetRandomSpawn`.

**Child** — A second sprite spawned *by* an animation (`<child animationid="…">`), positioned relative to
its parent (`imageX`/`imageY`). Children can't be dragged, auto-close when their sequence ends, and can
nest up to 5 deep. Used for mates/props/effects. See [02 §7](02-architecture.md#7-multiple-companions--children).

**Magic names** — Four `<name>` values the engine hooks to lifecycle events: **`drag`** (picked up),
**`fall`** (released / gravity), **`kill`** (closing; fades out), **`sync`** (synchronise all companions). See
[03 §7](03-companion-xml-format.md#7-magic-animation-names).

**Sync** — Making all on-screen companions jump to their `sync` animation at once (triggered from the About box
/ `SyncSheeps`).

**Window-walking / FallDetect** — The physics that lets a falling companion land on and ride the title bar of
another application's window. `FallDetect` enumerates visible titled windows via `EnumWindows`; the companion
lands on the first suitable window top. See [02 §6.3](02-architecture.md#63-physics-via-win32--the-nativemethods-p-invokes).

**Full-screen detection** — When a foreground window covers the whole monitor (movie/game), the companion drops
its top-most flag and hides behind it (`CheckFullScreen`), restoring when the window closes.

**Layered / tool window** — The WinForms extended styles on each companion form: `WS_EX_LAYERED` (fast paint),
`WS_EX_TOOLWINDOW` (no Alt-Tab entry), `WS_EX_TOPMOST`, and `WS_EX_NOACTIVATE` for children.

**Portable vs installed** — The supported x64 runtime is `DesktopAICompanion.exe` plus the dependency files
listed in `packaging/runtime-files.txt`; it is not a single self-contained executable. A portable
marker selects file-based data beside the executable. The per-user MSI uses the same runtime files
and stores mutable data under `%LOCALAPPDATA%\DesktopAICompanion`. Historical UWP source is not shipped.

**gSheep** — The seven rainbow-colored sheep variants (blue/green/orange/pink/purple/red/yellow) shipped
as separate companions.

**eSheep** — The default sheep companion and a historical project/process name. The supported AI Edition
executable and running process are **`DesktopAICompanion.exe`**. See [01](01-history-and-lineage.md).

---

## FAQ

**Q: How do I try a downloaded companion without installing anything?**
Drag-and-drop its `animations.xml` onto a running companion (it hot-loads), or launch
`DesktopAICompanion.exe localxml=path\to\animations.xml`. Only a bounded, reparse-free local XML file is
accepted; `webxml=` and `install=` are rejected. A command-line load error stops startup. A rejected
drag-and-drop displays a warning and leaves the current companion unchanged. See
[02 §6.4](02-architecture.md#64-interaction--mouse--drag).

**Q: My custom companion keeps vanishing and reappearing somewhere else. Why?**
It's hitting the **respawn rule**: in some situation (edge/taskbar/window/sequence-end) no `<next>` is
eligible, so the engine respawns it. Add a `only="none"` fallback `<next>` to the relevant
`<sequence>`/`<border>`/`<gravity>` list. See [03 §6](03-companion-xml-format.md#6-next-the-transitions).

**Q: My companion won't fall / ignores gravity.**
An animation only falls if it has a `<gravity>` list with an eligible `<next>`. Add one (usually
`<next only="none">` pointing at your `fall` animation). Note the engine tolerates a 3px gap before
falling.

**Q: My companion won't climb windows.**
Window-walking happens during a **downward** animation (`y>0`) whose `<border>` handles `only="window"`.
The target window must be visible and have a **visible title bar**, and the companion must be >20px below the
screen top and horizontally over the window. Borderless/title-less windows aren't landable.
See [02 §6.3](02-architecture.md#63-physics-via-win32--the-nativemethods-p-invokes).

**Q: Half my companion shows on my second monitor / it looks clipped at the edge.**
That's intentional edge-clipping: when a companion walks off the screen it's clipped so it doesn't appear on an
adjacent monitor before respawning. If it looks wrong mid-screen, your `<x>/<y>` velocities or a missing
border `<next>` are pushing it off-screen unexpectedly.

**Q: How do frame indices map to the sheet?**
Row-major from the top-left, starting at 0. For a `16 × 11` sheet, index 15 ends row 1, 16 starts row 2,
etc. See [03 §3](03-companion-xml-format.md#4-spawns--where-a-companion-appears).

**Q: Why is everything magenta in my sheet still showing?**
The transparency key defaults to Magenta `#FF00FF` — use exactly that color for background/empty pixels,
and set `<transparency>` if you use a different key. See [03 §3](03-companion-xml-format.md).

**Q: How many companions can run at once?**
Up to **16** top-level companions (`MAX_SHEEPS`), plus their children (nesting capped at 5). Up to **2**
instances of the whole app can run (two named mutexes). See [02 §2](02-architecture.md#2-entry-point-and-process-model--programcs), [02 §7](02-architecture.md#7-multiple-companions--children).

**Q: How do I add a sound?**
Add a `<sound animationid="…">` under `<sounds>` with a `<probability>` and a base64 MP3 in `<base64>`;
it plays (probabilistically) when that animation starts. See [03 §10](03-companion-xml-format.md#10-sounds--sound--audio-optional).

**Q: Do my desktop companions work in a web page?**
Yes — the browser port [`web-esheep`](04-upstream-forks-ecosystem.md#3-the-javascript--web-port-adrianotigerweb-esheep)
uses the same `animations.xml` format. (Note it's GPL-3.0; the desktop engine is unlicensed.)

**Q: Where's the authoritative element-by-element spec?**
The upstream **wiki** (<https://github.com/Adrianotiger/desktopPet/wiki>) and, offline/code-verified,
[03 — Pet XML Format](03-companion-xml-format.md) here. The **online editor** at <https://esheep.petrucci.ch>
validates as you build.

**Q: Can I redistribute the engine or the sheep?**
Be careful. The upstream desktop repo has **no license** (effectively all-rights-reserved) and the
default sprite art is third-party (Nomura's *Stray Sheep* character). See the licensing note in
[04 §1](04-upstream-forks-ecosystem.md#license--important).

**Q: What are the special `drag` / `fall` / `kill` / `sync` names for?**
They wire an animation to picking the companion up, dropping it, closing the app, and syncing all companions. Omit
`kill` and the companion just closes instantly with no death animation. See
[03 §7](03-companion-xml-format.md#7-magic-animation-names).

**Q: Where do I look first in the code?**
`Program.Main` → `StartUp` (controller) → `Xml`/`Animations` (load + model) → `FormCompanion.NextStep` (the
loop and physics). Map in [02 §1](02-architecture.md#1-the-big-picture).

**Q: Where's the AI-Edition stuff documented?**
Start with the current [`Readme.md`](../Readme.md), [`PRIVACY.md`](../PRIVACY.md), and
[`docs/RELEASE-CHECKLIST.md`](../docs/RELEASE-CHECKLIST.md). This grimoire provides the detailed
engine, format, and history reference; [`handoff.md`](../handoff.md) and
[`BACKLOG.md`](../BACKLOG.md) are implementation/history records, not release authority.
