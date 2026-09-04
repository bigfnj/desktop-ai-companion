# 02 — Architecture

Runtime architecture of the maintained WinForms product, grounded in the code under `src/dotNet/` and
`src/Portable/`. Claims are cited as `file:member`. For the companion data model this engine *consumes*, see
[03 — Companion XML Format](03-companion-xml-format.md). For user-facing AI-Edition behavior and current build
commands, see the repository [`Readme.md`](../Readme.md).

> **Reading note.** Line numbers drift as the file changes, so members are cited by name. The engine is
> C# 7.3 targeting .NET Framework 4.8. The maintained build is Windows x64
> (`src/DesktopAICompanion_Portable.csproj`); its assembly and executable are `DesktopAICompanion` / `DesktopAICompanion.exe`,
> so Task Manager and process APIs identify the running process as **`DesktopAICompanion`**.

## 1. The big picture

```
Program.Main                 (entry: mutex, path/bootstrap policy, arg parse)
  └─ ProcessIcon             (tray icon + menu host)
  └─ StartUp   "Mainthread"  (controller: owns Xml + Animations + FormCompanion[16])
        ├─ Xml               (deserialize animations.xml, decode base64, slice sprite sheet, eval expressions)
        ├─ Animations        (in-memory model + the animation STATE MACHINE + sound)
        └─ FormCompanion × N        (one borderless layered window per visible companion AND per child)
              └─ NativeMethods (Win32 P/Invoke: EnumWindows, GetWindowRect, title-bar info, …)
```

The visible animation engine is single-process, UI-thread, and event/timer driven. There is no game
loop thread; each companion owns a `System.Windows.Forms.Timer` whose `Tick` advances one animation step.
AI, download, cache, and validation work can use asynchronous/background operations without changing
that animation-loop model.

## 2. Entry point and process model — `Program.cs`

`Program.Main` (`Program.cs:Main`, the `#if PORTABLE` branch — the shipped one):

1. **Two cross-session instance slots.** `Program.TryAcquireInstanceSlot` attempts two leases rooted in
   `AppPaths.DataRoot`. Each lease combines a current-user `Global\` mutex with a same-directory lock
   file fallback (`CrossSessionLock`), so **up to two** instances can run across console/RDP sessions.
   A third shows *"Only 2 instances are allowed"* and exits before mutable settings are loaded.
2. **Locked runtime dependencies.** The supported build restores dependencies through locked
   `PackageReference` entries. NAudio 2.3.0 and the other managed runtime assemblies are shipped beside
   `DesktopAICompanion.exe` according to `packaging/runtime-files.txt`; the former embedded-assembly loader and
   vendored DLLs have been removed. The portable ZIP is self-contained as a directory, not as a single
   executable.
3. **Command-line policy** (`Program.cs:Main`): `localxml=<file>` accepts an existing `.xml` file only
   after a strict UTF-8, 4 MiB bounded read. Legacy `webxml=` and `install=` sources are rejected.
   Diagnostic switches (`--embed-selftest`, `--smart-selftest`, `--filter-selftest`,
   `--security-selftest`) run their bounded check and exit without starting the tray application.
4. **Tray + controller.** Creates a `ProcessIcon` (`pi.Display()`), then
   `Mainthread = new StartUp(pi)`, then `Application.Run()`.

Only the `PORTABLE` branch is part of the maintained product. It uses the `src/Portable/LocalData.cs`
facade and is built with Visual Studio/MSBuild plus locked `PackageReference` graphs. The old non-portable
UWP/classic projects are quarantined under `src/legacy/`; they are not built or packaged. `Tools/PetTester`
is the maintained companion-validation utility, while `Tools/PetEditor` is explicitly unsupported legacy
source.

`AppPaths.Resolve` is the single installed/portable mode rule. An installed executable lives in either
the legacy `%LOCALAPPDATA%\DesktopAICompanion` directory or the MSI directory
`%LOCALAPPDATA%\Programs\Desktop AI Companion`; installed mutable data lives in
`%LOCALAPPDATA%\DesktopAICompanion`. Any other executable directory is portable and uses `data\` beside the
executable. A `DesktopAICompanion.portable` marker forces portable behavior, and the absolute
`DESKTOP_AI_COMPANION_DATA_ROOT` override isolates smoke tests.

## 3. The controller — `StartUp.cs`

`StartUp` (aliased `Program.Mainthread`) is the per-run brain of the engine.

- **State.** `FormCompanion[] sheeps` with `MAX_SHEEPS = 16` (`StartUp.cs`), plus one shared `Xml` and one
  shared `Animations` instance. All companions share the same decoded sprite set and the same state-machine
  model — they differ only in position/phase.
- **Construction** (`StartUp.StartUp(ProcessIcon)`): chooses the bounded command-line override, persisted
  XML, or embedded default in that order, then `TryStageRuntime` validates and fully stages `Xml` plus
  `Animations`. A rejected configured companion falls back to the embedded esheep64 definition; a failure of
  that built-in definition is fatal. Only a complete staged runtime is activated and persisted. The
  tray metadata is then applied and a 1-second timer spawns the first sheep.
- **Explicit reload, not file watching.** `StartUp` still calls
  `Program.MyData.ListenOnXMLChanged` / `ListenOnOptionsChanged` for API compatibility, but both methods
  are intentional no-ops in `src/Portable/LocalData.cs`. Companion imports and option changes travel through
  explicit UI/command paths; `StartUp.LoadNewXMLFromString` stages a replacement, atomically persists
  its assets, and swaps it in only after every fallible activation step succeeds.
- **Spawning a companion** (`StartUp.AddSheep`): `new FormCompanion(animations, xml)`, copy every decoded frame in
  via `newSheep.AddImage(sprite)` for each `xml.sprites`, then `Show(spriteWidth, spriteHeight)`. A
  subsequent timer tick calls `Play(...)` to place and animate it.
- **Fleet ops.** `KillSheeps`, `KillSheep`, `TopMostSheeps`, `SyncSheeps` operate across the array.

> The AI-Edition additions live in this same class (`SayAll`, `AskAboutScreen`, `EmoteAll`,
> `EmotionAnimations`, `InitAiTriggers`, `ReloadAiSettings`, idle timer). They are **additive** and
> documented in [`handoff.md`](../handoff.md) — out of scope here.

## 4. Loading a companion — `Xml.cs`

`Xml` turns an `animations.xml` string into runtime objects.

- **Validate and deserialize** (`Xml.TryReadXml`): `CompanionXmlValidator.TryParse` performs the bounded,
  hardened XML/XSD and semantic checks first. Image decoding and sprite creation are staged in temporary
  objects, and the `Xml` instance publishes them only after the whole candidate succeeds. Startup owns
  the embedded-default fallback; `Xml` itself never silently substitutes a different companion.
- **Decode + slice** (`Xml.ReadImages` → `Xml.BuildSprites`): the base64 `<png>` sprite sheet is decoded
  to a `Bitmap`, then cut into `TilesX × TilesY` equal cells. `spriteWidth/spriteHeight` are the cell
  size; each cell becomes its own `Bitmap` in `Xml.sprites` (list order = row-major, top-left first).
  The 48×48 icon (`<header><icon>`) is decoded into `Xml.bitmapIcon`. **HD scaling:** frames are
  nearest-neighbour upscaled by `iScale`, but capped so a cell never exceeds 255 px (an `ImageList`
  limit) (`Xml.ReadImages` `while (... > 255) iScale--`).
- **Populate the model** (`Xml.LoadAnimations(Animations)`): iterates the deserialized nodes and fills
  the `Animations` dictionaries. Key detail — **four animation *names* are special** and are captured by
  name into fields used by the engine's lifecycle:

  | `<name>` | Field set | Used when |
  |----------|-----------|-----------|
  | `fall`   | `Animations.AnimationFall` | companion released after a drag |
  | `drag`   | `Animations.AnimationDrag` | user picks the companion up |
  | `kill`   | `Animations.AnimationKill` | app closing (death animation) |
  | `sync`   | `Animations.AnimationSync` | "sync all companions" (About-box cancel) |

  It also translates each `<next only="…">` string into the `TNextAnimation.TOnly` bit flag.

### 4.1 The expression language — `Xml.ParseValue`

XML coordinates/intervals are **strings evaluated as arithmetic expressions**, not just integers. This
is the mechanism that lets one companion definition adapt to any screen size. `Xml.ParseValue` passes the
original expression and an allowlisted variable resolver to `SafeExpression.Evaluate`. The dedicated
parser accepts decimal numbers, parentheses, unary signs, `+ - * / %`, and the exact legacy form
`Convert(value,System.Int32)`; it rejects unknown identifiers, divide-by-zero, non-finite/overflowing
results, expressions longer than 256 characters, and inputs with more than 128 primary tokens.

| Token | Replaced with |
|-------|---------------|
| `screenW` / `screenH` | monitor `Bounds.Width` / `Height` |
| `areaW` | working-area width |
| `areaH` | working-area height |
| `imageW` / `imageH` | sprite cell width / height (`imageW` is negated in a flipped-parent context) |
| `imageX` / `imageY` | parent companion's left/top (used to place **children** relative to the parent) |
| `random` | fresh random 0–99 **every evaluation** |
| `randS` | random 0–99 **fixed until the next spawn** (`iRandomSpawn`, chosen in the `Xml` ctor) |
| `scale` | current HD scale factor |

Whether an expression must be re-evaluated is precomputed into `TValue.IsDynamic` (contains `random`,
`randS`, `imageX`, or `imageY`) and `TValue.IsScreen` (contains `screen` or `area`) in
`Xml.GetXMLCompute`. `PushParentContext` supplies a child's parent coordinates and flip state for one
bounded evaluation scope. A rejected expression is logged and produces `0`; it is never delegated to
`DataTable.Compute` or another general-purpose expression engine.

## 5. The data model &amp; state machine — `Animations.cs`

`Animations` holds the whole companion as dictionaries keyed by integer id:

- `SheepAnimations : Dictionary<int, TAnimation>`
- `SheepSpawn : Dictionary<int, TSpawn>`
- `SheepChild : Dictionary<int, List<TChild>>` (one animation id can trigger several children)
- `SheepSound : Dictionary<int, TSound>` (keyed by the animation id the sound plays with)

The core structs (all in `Animations.cs`):

- **`TAnimation`** — `Start`/`End` movement (`TMovement`), `Sequence` (`TSequence`), and **three separate
  transition lists**: `EndAnimation` (sequence finished), `EndBorder` (hit a border), `EndGravity`
  (nothing underneath). Flags `Border`/`Gravity` say whether those lists exist.
- **`TMovement`** — `X`, `Y`, `Interval` (each a `TValue`, i.e. possibly a dynamic expression),
  `OffsetY`, `Opacity`. **X/Y are per-step pixel *velocities*, not absolute positions.**
- **`TSequence`** — `Frames` (list of sprite indices), `Repeat`, `RepeatFrom`, `Action`, and a
  precomputed `TotalSteps`.
- **`TNextAnimation`** — target `ID`, `Probability` (a relative weight), and `only` (a `TOnly` flag).
- **`TValue`** — `GetValue()` returns the cached int, or re-evaluates the expression if dynamic/screen.

### 5.1 How "what happens next" is chosen — `SetNextGeneralAnimation`

All three of `SetNextBorderAnimation`, `SetNextSequenceAnimation`, `SetNextGravityAnimation` funnel into
the private `Animations.SetNextGeneralAnimation(list, where)`:

1. Sum the `Probability` of every entry whose `only` flag is compatible with the current situation
   `where`. The compatibility test is bitwise: an entry is **skipped** if
   `anim.only != NONE && (anim.only & where) == 0`. `TOnly.NONE == 0x7F` therefore matches every
   situation, so `only="none"` entries are always eligible.
2. Pick a random number in `[1, sum]` and walk the cumulative weights to select an id.
3. Re-evaluate that animation's dynamic values (`UpdateAnimationValues`) and, if the chosen id has a
   registered sound, roll `Probability` and play it via `TSound.Play` (`SheepSound[id].Play`).
4. **If the eligible list is empty, return `-1`** — which the caller (`FormCompanion`) treats as *"respawn"*.

This is the single most important behavioural rule of the format: **an animation with no applicable
`next` for a situation causes the companion to respawn.** (`TNextAnimation` XML-doc remarks and
`SetNextGeneralAnimation` both state this.)

The `TOnly` flags (`Animations.cs`): `NONE=0x7F`, `TASKBAR=0x01`, `WINDOW=0x02`, `HORIZONTAL=0x04`,
`HORIZONTAL_=0x06` (= horizontal **or** window), `VERTICAL=0x08`.

### 5.2 Spawning — `GetRandomSpawn`

`Animations.GetRandomSpawn` does a probability-weighted pick over `SheepSpawn` to choose the companion's entry
position and its first animation (`TSpawn.Next`). If there are no spawns it falls back to a default at
(0,0) running the first animation.

## 6. The companion window &amp; the animation loop — `FormCompanion.cs`

Each visible companion (and each child) is one `FormCompanion` — a borderless, transparent, always-on-top WinForms
form containing a single `pictureBox1` that fills it and an `imageList1` holding every frame.

### 6.1 Rendering: a magenta-keyed layered tool window

- The form is `FormBorderStyle.None`, `ShowInTaskbar = false`, with `BackColor = Magenta` and
  **`TransparencyKey = Magenta`** (`FormCompanion.Designer.cs`). Any magenta (`#FF00FF`) pixel in the sprite is
  therefore not drawn — this is the transparency mechanism. The XML `<transparency>` element documents
  the key (default Magenta), matching this.
- Extended window styles are set in `FormCompanion.CreateParams`:
  `WS_EX_TOOLWINDOW (0x80)` removes it from Alt-Tab, `WS_EX_TOPMOST (0x08)` keeps it above other windows,
  `WS_EX_LAYERED (0x80000)` speeds up painting. Children additionally get `WS_EX_NOACTIVATE (0x8000000)`
  so spawning one never steals focus. `FormCompanion.ShowWithoutActivation` returns `true` for the same reason.
- `Show(w,h)` sizes the form/picture box and finds the taskbar thumbnail window
  (`FindWindowEx("TaskListThumbnailWnd")`) used later for taskbar interaction. `AddImage(Image)` appends
  a frame to the `ImageList`.

### 6.2 The loop: `Play` → `Timer1_Tick` → `NextStep`

- **`Play(first, forceSpawn)`** initialises a spawn: pick a `TSpawn`, set `Top/Left` from its
  expressions (mirrored horizontally if `!IsMovingLeft`), reset `PositionX/PositionY` (float accumulators
  the engine integrates into `Left/Top`), `SetNewAnimation(spawn.Next)`, make the form visible, enable
  `timer1`. On multi-monitor, a random `DisplayIndex` is chosen.
- **`SetNewAnimation(id)`**: if `id < 0` → `Play` (respawn). Otherwise load `CurrentAnimation`,
  `UpdateValues(DisplayIndex)`, spawn any children the id declares (see §7), and set `timer1.Interval`
  to the animation's start interval. If `id == AnimationKill`, no further animation is set.
- **`Timer1_Tick`**: disables the timer, calls `NextStep()`, increments `AnimationStep`, re-enables. Any
  exception surfaces a "Fatal Error" dialog with the current animation dump.
- **`NextStep`** — the heart of the engine. Per tick it:
  1. Picks the frame for `AnimationStep` (honouring `RepeatFrom`/`Repeat` via a modulo index).
  2. Interpolates `interval`, `Opacity`, `OffsetY`, and the per-step velocity `x,y` linearly from
     `Start` to `End` across `TotalSteps`.
  3. If dragging, snaps the companion to the cursor and returns.
  4. **Horizontal border checks** (when `x<0`/`x>0`): against the current window rect if standing on a
     window (`hwndWindow`), else against the screen working area (after `CheckFullScreen`). A hit calls
     `SetNextBorderAnimation(..., VERTICAL or WINDOW)`; if that yields no animation the companion is flagged
     `bLeavingScreen`.
  5. **Downward checks** (`y>0`): taskbar bottom → `SetNextBorderAnimation(..., TASKBAR)`; otherwise
     `FallDetect` to see if a window title bar is in the fall path → `... WINDOW`.
  6. **Upward check** (`y<0`): top of working area → `... HORIZONTAL`.
  7. **Sequence over** (`AnimationStep >= TotalSteps`): if `Action=="flip"`, mirror every frame and
     toggle `IsMovingLeft`; then `SetNextSequenceAnimation` (or respawn if the companion has wandered off
     screen). If the companion is a child with no next, it closes; the `kill` animation fades opacity to 0
     then `Close()`.
  8. **Gravity** (animation has `<gravity>` and companion not over a window with >3px of empty space beneath):
     `SetNextGravityAnimation`. If it *is* on a window, `CheckTopWindow`/`FollowWindow` keep it glued to
     that window (see §6.3).
  9. Integrate `PositionX/Y += x/y`, then either clip the sprite (if leaving the screen edge, so half a
     companion doesn't appear on an adjacent monitor) or set `Left/Top`.

### 6.3 Physics via Win32 — the `NativeMethods` P/Invokes

`FormCompanion.NativeMethods` (bottom of `FormCompanion.cs`) is the entire physics toolkit: `EnumWindows`,
`GetWindowRect`, `IsWindowVisible`, `GetWindowText`, `GetTitleBarInfo`, `GetTopWindow`, `GetWindow`,
`GetForegroundWindow`, `SetForegroundWindow`, `ShowWindow`, `FindWindowEx`, plus the `RECT` and
`TITLEBARINFO` structs.

- **`FallDetect(y)`** — `EnumWindows` collects visible, **titled** windows that have a **visible title
  bar** (`GetTitleBarInfo`, skipping the `0x8000` "invisible" state). For each, if the companion is directly
  above the window's top edge, will cross it this step, overlaps it horizontally, and is >20px below the
  screen top, that window becomes `hwndWindow` and the companion lands on `rct.Top`. `CheckTopWindow(false)`
  first confirms the window isn't covered by another window over the companion. Optionally the window is raised
  (`ShowWindow`+`SetForegroundWindow`) if the "bring window to foreground" option is on.
- **`CheckTopWindow(bCheck)`** — walks the Z-order from `GetTopWindow` via `GetWindow(...,2)` to decide
  whether the target window is still the topmost titled window under the companion (i.e. the companion isn't standing
  on something that got buried).
- **`FollowWindow()`** — when the window the companion stands on is moved/resized, translate (and horizontally
  rescale) the companion so it rides along. `NextStep` runs this in a short 16 ms poll loop while the window is
  moving.
- **`CheckFullScreen()`** — if the foreground window covers the whole monitor (a video/game),
  `TopMost` is dropped so the companion hides behind it; restored when the full-screen window goes away.

### 6.4 Interaction — mouse &amp; drag

`FormCompanion.PictureBox1_MouseDown`: **left-press picks the companion up** (`IsDragging = true`, plays the `drag`
animation); on `MouseUp` it drops and plays `fall`. **Double right-click** closes a single companion
(`pictureBox1_DoubleClick`). Right-click otherwise shows a greeting bubble (AI-Edition change) or, if the
app was started with Shift held, the debug menu. `Form2_DragEnter`/`DragDrop` accept a dropped
`animations.xml` to hot-swap the companion.

## 7. Multiple companions &amp; children

- **Multiple top-level companions:** `StartUp` holds up to `MAX_SHEEPS = 16` independent `FormCompanion`s, added via
  `AddSheep`/the tray menu; the changelog notes up to 16 companions can auto-start (`Changelog.txt` 1.0.6).
- **Children:** any animation id listed in `<childs>` spawns one or more child `FormCompanion`s **when that
  animation plays** (`FormCompanion.SetNewAnimation` → `Animations.HasAnimationChild`/`GetAnimationChild` →
  `child.PlayChild`). Children share the parent's `ImageList`, are positioned relative to the parent
  (via `imageX`/`imageY`), are named `child1..child5`, **cannot be dragged**, and **auto-close when their
  sequence ends** (children have no spawn). Nesting is capped at **5 levels**
  (`FormCompanion.SetNewAnimation`, `int.Parse(Name.Substring(5)) < 5`). Children are how the sheep interacts
  with a second sprite (a mate, flowers, a bath, etc.).

## 8. Audio — NAudio

`Animations.TSound` wraps NAudio: `Load(byte[])` builds an `Mp3FileReader` + `WaveOut`; `Play(loop)`
plays if `MyData.GetVolume() > 0`, seeking to start each time and re-playing on `PlaybackStopped` for
loops. Sounds are keyed by animation id and fired from `SetNextGeneralAnimation` with a probability roll
(§5.1). MP3 bytes come from base64 in `<sounds><sound><base64>` (a leading `;base64,` data-URI prefix is
stripped in `Animations.AddSound`). If audio fails, volume is forced to 0 and the error is recorded in
`ErrorMessages.AudioErrorMessage`.

## 9. Settings, tray, and lifecycle

- **Paths and settings:** `AppPaths` owns every mutable root (core settings, AI settings, chat history,
  fortunes, vectors, and catalog cache) without consulting the current working directory.
  `Program.MyData` is a facade over the schema-versioned `settings.json` managed by
  `AppSettingsStore`: cross-session locked reads/writes, normalization and legacy migration, atomic
  replacement with a backup, corrupt-file preservation, and future-schema write blocking. The generated
  `Properties.Settings` object is mirrored only for old extension code and is never the canonical save
  path. Portable file-watcher registration is intentionally a no-op; current option/import code applies
  changes explicitly (§3).
- **Tray icon &amp; menu:** `ProcessIcon.cs` owns the `NotifyIcon`; `ContextMenus.cs` builds the menu
  (add companion, options, kill all, about, and — in this fork — "Ask about my screen"). `ProcessIcon.SetIcon`
  is fed the decoded `bitmapIcon` and the `<header>` metadata.
- **Debug:** start the app with **Shift** held to open `FormDebug`, which streams
  `StartUp.AddDebugInfo(type, text)` messages (the info/warning/error log threaded throughout the engine).

## Cross-references

- Companion data model these classes consume → [03 — Companion XML Format](03-companion-xml-format.md).
- Where the sheep, the format, and the fork come from → [01 — History &amp; Lineage](01-history-and-lineage.md).
- Terminology (spawn, child, `only`, border/gravity, sync…) → [05 — Glossary &amp; FAQ](05-glossary-and-faq.md).
