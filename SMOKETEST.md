# Live smoke test

**What this is.** The checks that require a human to open the app and look at it. Everything else in this
repo (the gate, 61 source invariants, 16 self-tests, two soaks, the mutation suites) proves the code does
what it says. Nothing in it proves the code says the right thing.

**Why it exists.** Five of the eleven releases v1.9.4 through v1.9.14 shipped a bug that the full automated
suite passed straight over, and every one of those bugs was visible in the first thirty seconds of use:

| shipped in | what a user saw | caught by |
|---|---|---|
| v1.9.8 | the app never noticed a new version, however often you restarted | user, same day |
| v1.9.10 | a UFO drew on top of a fullscreen game | user, smoke testing |
| v1.9.11 | a companion pinned to monitor 2 spawned on monitor 1 | user, within the hour |
| v1.9.12 | a small companion played its walk animation without moving | user, smoke testing |
| v1.9.13 | the tray listed companions by folder id, so "Remove a companion" offered "Shimeji 3x56f4pl" | user, browsing companions |
| v1.9.15 | a fresh install put the companion on screen with no tray icon in sight | user, first launch |

None of these needed a debugger. They needed someone to look.

**How long.** About 35 minutes for the full pass. The **Core** sections (A through E) are 12 minutes and
catch the class of bug above; do at least those before every release. Sections F through K are worth a full
pass when the release touches them, and once a month regardless. Section K only matters when the release
touches the installer, but when it does it matters a lot: the installer is the one component whose failures
are invisible to every automated check in this repo.

**Before you start**

- [ ] Install the built MSI **over the previous version**. Never test a clean-machine install only: the
      upgrade path is the one users actually take, and it is the path that skips refreshing a file whose
      version did not change.
- [ ] Note the version you are testing and the version you upgraded from. Both go in the report.
- [ ] Have at least two monitors attached for section C. If you only have one, say so in the report rather
      than marking those rows passed.

Record anything odd even if it is not in a row here. The rows are the failures we already know about; the
useful ones are the failures we do not.

---

## A. Launch and baseline (2 min)

- [ ] **A1. The app starts and your companions appear.** Your saved mix restores, not a default single sheep, and
      not a different count than you left.
- [ ] **A2. Every companion is fully drawn.** No half-sprite, no black rectangle in a corner of a frame, no companion
      floating above the taskbar or sunk into it. A companion standing on the floor has its feet on the taskbar's
      top edge.
- [ ] **A3. Nothing is stuck to the cursor.** Move the mouse across the desktop without clicking.
- [ ] **A4. The tray icon is in the VISIBLE tray, not the hidden-icons flyout**, and its menu opens.
      Look at the taskbar without clicking the `^` chevron first. "The icon exists somewhere" is not the
      check: the v1.9.15 regression was an icon that registered perfectly and worked perfectly from inside
      the flyout, one of thirty, which the user reasonably read as "there is no tray icon". On a **fresh
      install** especially, since that is the case Windows 11 hides by default.
- [ ] **A5. The icon's label reads "Desktop AI Companion"**, not "eSheep Desktop Pet" and not a companion's
      name. Hover it, and check the flyout listing too: Windows caches the label from the first icon it ever
      accepted from that install path, so a stale one is sticky and is what a user scans for when hunting
      the icon. It is a constant app name on purpose, because Windows keys that entry on the executable and
      several companions can be on screen at once, so naming one of them there described whichever happened
      to be the default.

## B. Companion motion (5 min, the highest-value section)

This is where the walk-in-place bug lived, and it is invisible to every test we have.

- [ ] **B1. A walking companion actually travels.** Watch one companion walk left and one walk right. The companion must
      CHANGE POSITION on screen, not merely cycle its legs. This is the v1.9.12 regression: the animation
      played perfectly while the companion stayed put.
- [ ] **B2. Repeat B1 at a small companion size.** Set a companion to 25% in the Companions pane and watch it walk. Small
      sizes are where velocity rounding fails first, and 100% can look fine while 25% is frozen.
- [ ] **B3. A companion falls and lands.** Drag one to mid-screen and drop it. It falls, lands on the taskbar or
      a window, and does not sink through or hover above.
- [ ] **B4. A companion climbs a wall and reaches the ceiling.** Watch for a few minutes, or drag a companion to the
      left or right screen edge. It should grip, climb, and cross onto the ceiling rather than grabbing the
      wall and hanging motionless.
      > **Do not go looking for a `walk_right` to trigger this from Companion Studio's timeline: converted companions
      > do not have one, by design.** A Shimeji skin draws `walk_left` and `walk_right` from the *same*
      > sprites with only the direction reversed, so the converter keeps one copy and mirrors it at runtime.
      > The mirror is the `turn` animation, a single frame carrying `<action>flip</action>`, which flips
      > every sprite and negates the x-velocities. To drive a rightward walk and a right-hand wall climb by
      > hand, chain **`turn` then `walk_left` then `climb_left`**. Every `_left` name in the reachability
      > map is really "this pose, in whichever direction the companion is currently facing".
- [ ] **B5. A companion on the ceiling looks deliberate.** Converted Shimeji companions draw the ceiling pose lying
      flat rather than upside down. That is the source art and it is not a bug. What IS a bug: art cut off
      at the cell edge, or a companion drawn 60px away from the surface it is supposed to be touching.
      > A converted companion has exactly **one** ceiling animation, named `climb_ceiling_*`, and it will often
      > read as "a wall climb facing the other way" because the artist rotated the figure about 90 degrees
      > instead of inverting it. Before calling that a mislabel, check the source declaration rather than
      > the picture: an Android-Shimeji bundle's `animation.json` gives every action an explicit
      > `type` (`GROUND` / `WALL` / `CEILING` / `AIR` / `USER`) and `subtype` (`CLIMB`, `DESCEND`, `HANG`).
      > Luffy's `climb_ceiling_left` is declared `CEILING` / `HANG`, moves horizontally (`x=-6, y=0`), is
      > entered from a wall climb hitting a `horizontal` border and exits to `descend` on a `vertical` one.
      > Four signals, all agreeing. **Judging this from the art alone once produced a confidently wrong
      > "fix" that had to be reverted**, so the source declaration is the thing to look at.
- [ ] **B6. A companion jumps.** The arc rises and falls smoothly and the landing is on a surface, not in mid-air.
- [ ] **B7. Drag a companion around and drop it.** It swings from the cursor while held, and releases cleanly.
      Then move the mouse: **the companion must not follow the cursor.** A companion welded to the mouse was a real
      shipped bug.
- [ ] **B8. Drop a companion onto a window's title bar.** It stands on it, walks along it, and can grip a side or
      hang underneath. Critically, **a companion hanging under a window must be able to let go** and fall.
- [ ] **B9. Leave a companion idle and watch a full rest.** A rest animation (sitting, reading, sleeping) should
      play for roughly 9 to 12 seconds, long enough to watch. A rest that flashes past in under a second is
      the bug.
- [ ] **B10. Move a companion toward a screen edge.** It walks all the way to the edge, not to a stop some
      distance inland.

## C. Multiple monitors (4 min, needs 2+ screens)

- [ ] **C1. The per-companion screen dropdown appears.** Companions pane, under a companion's size row, a small `screen`
      dropdown listing `Any screen` plus each monitor with its resolution. It is deliberately hidden on a
      single-monitor machine.
- [ ] **C2. Pin a companion to a non-primary monitor, then Add it.** It spawns on THAT monitor. This is the
      v1.9.11 regression: the pin was read before the companion was registered and fell back to whichever companion was
      selected, so everything landed on screen 1.
- [ ] **C3. The pin survives a restart.** Close the app, reopen it, the companion is still on its monitor and the
      dropdown still shows the choice.
- [ ] **C4. A pinned companion stays put.** Watch it for a few minutes; it never crosses to another screen.
- [ ] **C5. Set the companion back to `Any screen` and Add it a few times.** With `Let companions spawn on any screen`
      ON, it lands on different monitors across spawns. With it OFF, always the primary.
- [ ] **C6. Known limitation, confirm it still reads honestly.** An unpinned companion does NOT walk between
      monitors; it spawns on one and lives there. The setting is labelled `Let companions spawn on any screen
      (they stay on the one they appear on)` precisely so this is not a surprise. If the label ever implies
      traversal again, that is a regression in the wording.

## D. Fullscreen applications (4 min)

Use a real fullscreen or borderless-fullscreen game if you have one. A fullscreen video player is a weaker
but usable substitute.

- [ ] **D1. Go fullscreen. Every companion disappears.** Not "most companions", not "after a while".
- [ ] **D2. Stay fullscreen for several minutes.** Nothing reappears. This is the v1.9.10 regression: the
      hide ran once per transition, so anything that made a companion visible afterwards won permanently.
- [ ] **D3. Specifically watch for the UFO.** The sheep's `spawn_ship` respawn animation drew over a
      fullscreen game because it made a companion visible outside the normal spawn path. Give it long enough for a
      respawn to happen.
- [ ] **D4. Alt-tab out. Companions come back.** All of them, in sensible positions, not stacked in a corner.
- [ ] **D5. AI Brain, if configured.** With `Stand down while a fullscreen app is running` ON, going
      fullscreen releases the model. Confirm with `ollama ps` from a terminal: the model should disappear
      from the list rather than sitting in VRAM. Fortunes speak in its place.
- [ ] **D6. A pinned companion hides rather than moving.** A companion pinned to the monitor a fullscreen app takes over
      should vanish, not relocate to a free screen.

## E. Speech and bubbles (2 min)

- [ ] **E1. Right-click a companion.** It speaks. The bubble is sized to the text, not clipped and not enormous.
- [ ] **E2. Wait for an unprompted fortune.** One arrives on the drop interval.
- [ ] **E3. With several companions on screen, trigger an app-wide message.** Exactly ONE companion speaks, not all of
      them. Preferences names which: `Companion that speaks for the app (reminders, fortunes)`.
- [ ] **E4. Check quoted or non-ASCII text renders.** No mojibake, no boxes.

---

## K. The installer itself (4 min, only when the release touches it)

Run the MSI **over a running app** — that is the path that used to fail.

- [ ] **K1. A "Start fresh?" page appears before the licence, with the box UNTICKED.** It was authored once
      as a dialog MSI could never reach, so it existed in the package and never rendered.
- [ ] **K2. The page hands off to the licence cleanly.** No dialog flashing up and vanishing. Every stock
      WixUI dialog shares one background image; a page with different chrome makes that hand-off visible.
- [ ] **K3. No "files in use" prompt, and the running companion closes by itself.** With the app running, the
      installer should neither stop on "unable to automatically close all requested applications" nor leave
      you with companions on screen and no tray icon.
- [ ] **K4. "Launch Desktop AI Companion" is ticked on the finish page, and the companion actually starts.**
      Then re-run **A4**: an install is the one path that gets a brand-new Windows tray entry, and a
      brand-new entry is the one Windows 11 hides.
- [ ] **K5. Repair works.** Delete a DLL from the install folder, then run the MSI and choose Repair. The
      file must come back. Repair used to be greyed out entirely.
- [ ] **K6. Tick "clear all settings and modules" ONLY when you mean it.** Everything goes: settings, companions,
      fortunes, every module and its configuration. Verify afterwards that the app starts as a first run.
      **Back up `%LOCALAPPDATA%\DesktopAICompanion` and `<install>\modules` first.**

## F. Preferences and panes (4 min)

- [ ] **F1. Every pane opens without throwing.** Click through all of them.
- [ ] **F2. Change a value, Apply, close, reopen.** It persisted.
- [ ] **F3. The footer shows the version.** `v1.9.13` when current.
- [ ] **F4. When an update exists, the footer reads `1.9.13 → 1.9.14` and is clickable**, opening the
      GitHub releases page.

## G. Companions pane (4 min)

- [ ] **G1. Add a companion and Remove a companion.** The mix changes on screen immediately.
- [ ] **G2. The mix survives a restart.**
- [ ] **G3. Change a companion's size.** It redraws at the new size, and (see B2) it still MOVES at small sizes.
- [ ] **G4. Open the Companions pane and WAIT, without pressing anything.** New companions and updates should appear
      on their own within a few seconds. That is the point of the weekly check: the pane renders the last
      known answer immediately and refreshes itself on open. Pressing `Check for companions and updates` should
      still work, and should re-check rather than showing you a cached answer.
- [ ] **G5. Update a companion that is ON SCREEN.** It should be closed and respawned on the new skin, and the
      status line should say so. Previously the old skin kept walking around, and removing and re-adding the
      companion by hand brought the OLD skin back, silently, because the parse was cached.
- [ ] **G6. Update the ACTIVE (default) companion.** This one asks you to restart instead, deliberately: its live
      definition lives in settings.json rather than the library folder, so a swap cannot work.
- [ ] **G7. Download a new companion and add it.** It appears in the gallery with a thumbnail, downloads, spawns.

## H. Modules (6 min)

- [ ] **H1. Open the Modules pane and WAIT.** An available update should show up on its own, without
      pressing `Check online`. Installed modules show their versions either way.
- [ ] **H2. Install a module.** It restarts cleanly and its settings appear.
- [ ] **H3. Update a module.** It restarts cleanly and **keeps its settings**.
- [ ] **H4. Uninstall a module.** Clean removal, no orphaned pane.
- [ ] **H5. Fortunes.** Speaks on right-click and on the idle interval.
- [ ] **H6. AI Brain, if configured.** Ask a question: one answer, correctly sized bubble, no mojibake.
- [ ] **H7. Reminder.** Add a calendar, set its `Reminder companion (which companion speaks this calendar)` to a
      specific companion, and confirm THAT companion announces. The dropdown should list only companions currently active.
- [ ] **H8. Remembrance.** Record something and confirm it captures and transcribes.
- [ ] **H9. Companion Studio.** Open a companion, edit the XML, preview it on the desktop. Then the behaviour timeline:
      drag animations into a chain and press **Run**. This button has no automated coverage at all.
- [ ] **H10. Blinking LED.** Toggle it, confirm the Scroll Lock light blinks and the companion comments.

## I. Update check (2 min)

- [ ] **I1. Launch the app when a newer version exists.** Within a minute or so the footer offers it.
- [ ] **I2. Open Preferences.** The check refreshes on open, which is the only way a long-running instance
      notices at all.
- [ ] **I3. Turn the check off.** No network call, footer shows the plain version.

## J. Housekeeping (2 min)

- [ ] **J1. `%TEMP%` is not filling up.** Run this after a session and expect a small number, not hundreds:
      ```powershell
      (Get-ChildItem $env:TEMP -Filter 'dp-*' -Directory).Count
      ```
- [ ] **J2. After an ABI change only.** The installed `DesktopAICompanion.Contracts.dll` FileVersion matches the new
      product version. Windows Installer skips refreshing a file whose version did not change, so an ABI
      change shipped without a `ProductVersion.props` bump installs a stale DLL and every module fails to
      resolve the new types.
- [ ] **J3. Uninstall cleanly, if you are testing the installer.** Then reinstall and confirm your data
      survived.

---

## Regression watchlist

Every one of these shipped. Each is one glance, and each is the reason a row above exists.

| # | Signature | Row |
|---|---|---|
| 1 | companion animates a walk but does not move, worse at small sizes | B1, B2 |
| 2 | companion follows the cursor after you release a drag | B7 |
| 3 | companion hanging under a window can never let go | B8 |
| 4 | companion hovers above the taskbar, or a black blob in a sprite corner | A2 |
| 5 | rest animation flashes past in under a second | B9 |
| 6 | companion grabs a wall and hangs motionless instead of climbing | B4 |
| 7 | companion stops walking short of the screen edge | B10 |
| 8 | pinned companion spawns on the wrong monitor | C2 |
| 9 | anything visible over a fullscreen game, especially the UFO | D1, D2, D3 |
| 10 | every companion speaks the same app message at once | E3 |
| 11 | app never notices a new version across restarts | I1, I2 |
| 12 | "you already have every available companion" when a companion was corrected | G4 |
| 13 | hundreds of `dp-*` directories in `%TEMP%` | J1 |

## Reporting

For each failure, the useful report is three lines: what you did, what you expected, what happened. A
screenshot beats a description for anything visual. If a companion is involved, name it and give its size
percentage, because several of these bugs only appear away from 100%.

If a section could not be tested (no second monitor, no game installed, AI Brain not configured), say so
explicitly. A skipped row reported as skipped is useful. A skipped row reported as passed is worse than not
running the test at all.
