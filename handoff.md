# desktopPet AI Edition — Session Handoff

> Working notes for picking this up later. Last updated: **2026-09-03** (eighth session).
> Fork of Adrianotiger/desktopPet. Clone it wherever you like -- nothing here depends on the
> checkout path, and this file is public, so no machine paths go in it.
> `origin` = **git@github.com:bigfnj/desktop-ai-companion.git** (`upstream` = Adrianotiger — never push there).
> Also read the persistent memory note `project-desktoppet` in the auto-memory index (has the fine detail).
> Feature backlog: **[`BACKLOG.md`](BACKLOG.md)**.

---

## START HERE (2026-09-03) — v1.9.16 cut, one tray bug fixed from the v1.9.15 smoke report

**The v1.9.15 smoke report came back with one defect:** on a fresh install the companion appeared with no tray
icon. `v1.9.16` fixes it and is tagged. Tree clean, gate green, both soaks pass.

The icon was never missing. Windows 11 files every tray icon under
`HKCU\Control Panel\NotifyIconSettings` and **hides any entry whose `IsPromoted` value is absent**, so a
first launch put a fully working icon behind the chevron as one of thirty. `TrayPromotion` now promotes it
once, and only when that value is absent — Windows writes `0` when the user drags the icon back into the
flyout, so absent means "never chose" and `0` means "hidden on purpose", which has to stand. Self-limiting,
so it needs no setting of its own. HKCU only, no elevation, and the shell honours it live (measured: out of
the flyout in about three seconds, with no app or explorer restart).

Second half of the same bug: `SetIcon` assigned `ni.Icon` before `ni.Text`. WinForms only issues the
`NIM_ADD` once an icon exists (`Display()` sets `Visible` with a null `Icon`, which adds nothing), and
Windows permanently caches the tooltip from that first ADD — so every companion was labelled "eSheep Desktop Pet"
forever, which is what a user reads when hunting the flyout. Order swapped, and the cached label is now
corrected independently of the promotion decision, since unlike visibility there is no user preference there
to overrule.

**Smoke test A4 used to read "the tray icon is there and its menu opens" — which this bug passes.** It now
says VISIBLE tray, not the flyout, with A5 for the label. That is the whole lesson: the check was true and
useless.

Ruled out before any of that, each by measurement rather than reasoning, so nobody re-opens them:
`Application.Run` does not return early, `AppLifetime` does not tear the app down, `ProcessIcon` is not
disposed, and `bitmapIcon`/`SetIcon` succeed in both the portable and the installed layout. The 316ms
files-in-use result from v1.9.15 stands.

### What shipped

| Thread | |
|---|---|
| Installer | reset checkbox, closes a running companion, launch-on-finish, working Repair, light sidebar panel |
| The files-in-use prompt | an APP bug: `Application.Run()` had no main form. 32s → 316ms |
| Update checks | weekly for modules and companions, surfaced when a pane opens; two latent bugs fixed |
| Companion auto-reload | extra types swap in place; the active companion asks for a restart |
| Signing | scaffolding only, inert, opt-in on a thumbprint |

### Still not done

- **The live smoke test.** Sections C through K unwalked; A and B1-B3 pass. K is new: six installer rows,
  each one a bug that shipped or nearly did. `SMOKETEST.md` is 67 checks in eleven sections.
- **A real certificate.** The scaffolding is waiting on it. Add `SIGNING_PFX_BASE64` and
  `SIGNING_PFX_PASSWORD` and it signs with no code change — but dry-run it through `workflow_dispatch` on a
  throwaway tag first, because a `v*` tag must not be the first execution of that path. Two decisions are
  deliberately still open: whether to timestamp (it outlives the cert but forfeits the MSI
  byte-reproducibility `Normalize-MsiDeterminism.ps1` exists to preserve), and the publisher-name mismatch
  if the cert's CN is not `bigfnj`.
- ⚠ **Do NOT re-run the release workflow on the `v1.9.16` tag.** That tag predates the LF fix to
  `SHA256SUMS.txt`, and `workflow_dispatch` checks out the TAG, so a re-run would regenerate the CRLF file
  and clobber the corrected asset that is published now. The published file was replaced by hand after the
  fact (hash values byte-identical, line endings only; `sha256sum -c` verified at exit 0 on a fresh
  download). From `v1.9.17` on, the workflow produces LF itself and this note stops mattering.
- **A TESTBUILD product may still be installed** side by side (`Desktop AI Companion TESTBUILD`). It is a
  separate product with its own UpgradeCode and registry root, harmless, but uninstall it when done.

### The one lesson worth carrying, because it happened four times

An absence check that matches a BARE IDENTIFIER gets defeated by a comment describing the thing it forbids.
`Application.Run()`, `LoadNewXMLFromString`, `Invoke-Signtool.ps1` and a `TerminateProcess` value each did
it, and each time the guard failed against CORRECT code, which is the confusing direction. Assert the code
form: the semicolon, the parenthesis, the surrounding call text. Related: a mutation that only edits a
comment reports SILENT and looks exactly like a missing guard.

### Three measurement traps from the tray bug

- **On Windows 11 the classic `ToolbarWindow32` tray probe returns zero icons for every application.** The
  notification area is a XAML island; the icons are real UI Automation elements (`Shell_TrayWnd` for the
  visible strip, `TopLevelWindowForOverflowXamlIsland` for the flyout), and UIA is the only probe that
  reflects what the user sees. A probe that finds nothing for *any* app is not evidence that this app has no
  icon — it is a broken probe, and trusting it would have sent the investigation the wrong way entirely.
- **Swapping only `DesktopAICompanion.exe` into an install directory changes nothing.** The exe is a .NET apphost;
  the code lives in `DesktopAICompanion.dll`. The first "reproduction" was the old build running unchanged, and it
  read as a perfect repro. Copy the dll, and kill the running process first or the copy silently fails.
- **`gh` resolves a fork to `upstream` by default.** `gh release list` and `gh run list` cheerfully reported
  Adrianotiger's releases and workflows, which made it look like none of the v1.9.x releases or CI runs
  existed. Pass `-R bigfnj/desktop-ai-companion` on every `gh` call in this repo.

### Two measurement traps from the previous batch

- **The portable build does not reproduce the Restart Manager hang.** Neither does a portable build pointed
  at a copy of the real data root. Only the INSTALLED layout, with modules beside the exe, does. Two
  attempts at that fix looked useless because of this, and one was reverted before the third succeeded.
- **A side-by-side test MSI ships no modules**, so the first comparison loaded 0 of them and proved nothing
  until they were copied in by hand. Use `-UpgradeCodeOverride` + `-ProductNameSuffix` to build one.

---

## Previous START HERE (2026-09-02) — fullscreen, monitors, scaling; v1.9.11 to v1.9.13

**Released v1.9.11, v1.9.12 and v1.9.13.** Gate green (33 source invariants, 16 self-tests, 53 companions verified,
0 unreachable in all 31 converted companions).

> **Every one of these three releases exists because the USER opened the app and looked at it.** Not one was
> found by the gate. A UFO drew over a fullscreen game; a companion pinned to monitor 2 spawned on monitor 1; a
> small companion played its walk cycle on the spot. All three are visible in the first thirty seconds of use, and
> the whole automated suite passed straight over them. The gate proves the code does what it says. Nothing
> yet proves the code says the right thing.
>
> **[`SMOKETEST.md`](SMOKETEST.md) now exists and is the answer**: 58 checks, ten sections, a 12-minute Core
> pass (A-E) aimed squarely at that class of bug. It was handed to the maintainer on 2026-09-02.
> **First thing next session: ask whether it has been walked, and act on the report before building
> anything.** The old ten-row table it replaced had not grown since before companions could climb, which is very
> likely why nobody walked it: a checklist that cannot fail is not worth the half hour.

### What shipped

| version | fix |
|---|---|
| 1.9.11 | fullscreen hide was LATCHED, so a respawn / child / `spawn_ship` UFO could win permanently and stay over a game. Enforced on every scan now |
| 1.9.12 | pin a companion to one monitor (per-companion row in the Companions pane, shown only with 2+ screens); relabel "allow multiple screens", which never meant traversal |
| 1.9.13 | a small companion walked IN PLACE (`ScaleD(-2, 0.25)` = `Math.Round(-0.5)` = 0, banker's rounding) → new `ScalePolicy.ScaleVelocity`; and v1.9.12's own pin read a registry that `AddSheepCore` populates AFTER `Play()` |

### Two traps this session, both already seen once

1. **A correct function nobody wires up.** `ScaleVelocity` was unit-tested and right, and the mutation sweep
   still came back SILENT for "the walk X velocity goes back through `ScaleD`" — nothing asserted
   `Animations.cs` CALLED it. Second occurrence in two sessions. **When you add a helper to fix a bug, the
   invariant that matters is the CALL SITE, not the helper.**
2. **An "absence" assertion that matched a sibling.** The first offset guard checked
   `-match 'ScaleD\((Start|End)\.UnscaledOffsetY'` — switching `Start` to `ScaleVelocity` still matched via
   `End`, and it passed. It names both offsets now and asserts the ABSENCE of `ScaleVelocity` on either. An
   alternation in a positive match is a hole whenever the thing you fear is a PARTIAL change.

### Do not be surprised by

- **Local `master` is stale at 1.7.0 and unused.** Work happens on `feat/reminder-and-fixes`; the push is
  `git push origin HEAD:master`. Check `git branch --show-current` before every push anyway.
- **A GUI exe does not block PowerShell**, so `& $exe; $LASTEXITCODE` is meaningless — four mutations once
  falsely reported "silent" this way. `tests/run-gate.ps1` uses `Start-Process -Wait -PassThru`.
- **`dotnet build <csproj>` does not refresh module DLLs.** Use `build.ps1 -Release`.
- The gate script is `tests/run-gate.ps1`, not repo-root.

---

## Previous session (closed 2026-09-01 — companion pacing, speech routing, VRAM; v1.9.8 → v1.9.10)

**Released v1.9.8, v1.9.9 and v1.9.10. All three MSI hashes verified against `SHA256SUMS.txt`. Tree clean,
gate green (30 source invariants, 16 self-tests, 53 companions verified).**

> **v1.9.10 exists because v1.9.8's own update check was broken and the user caught it the same day.** It
> stamped "I looked" even on a negative answer, with a 24h interval — and the first check after any install
> IS negative, so a fresh install went blind for 24h, exactly when someone restarts expecting to be told.
> Interval is now 1h, plus a refresh when Preferences opens (the only surface the answer appears on, and the
> only way a long-running instance ever notices). **An install on 1.9.8/1.9.9 still carries the old logic**,
> so it improves future updates, not the one it announces.

### What shipped

| area | change |
|---|---|
| companion pacing | ceiling made reachable (climb crosses the wall in one sequence); rest dwell SPLIT by role — brief hub, 9-12s performances |
| speech | `SayAll` reaches ONE companion, not all; "Companion that speaks for the app" in Preferences |
| Reminder 1.8.1 | per-calendar "Reminder companion", live companions only, falls back rather than going silent |
| AI Brain 1.4.0 | "Model residency" (one choice, defaults to unloading); stand down for a fullscreen app, releasing what is already resident |
| host 1.9.9 ABI | `IHost.IsFullscreenActive` + `FullscreenChanged` (Contracts stays at AssemblyVersion 1.0.0.0) |
| update check | once/24h at launch, notify-only, off-switchable; footer becomes a link. Reads `app.version` from `catalog.json` |
| gate | a CONVERTED companion stranding an animation now FAILS; scratch sweep no longer keyed to a naming convention |

### Four mistakes worth not repeating

1. **The wall/ceiling art "fix" was wrong and was reverted.** I judged the source skin mislabelled from the
   art's ROTATION alone. The ANCHOR says the same thing independently and disagreed: ceiling art is
   composited flush to the cell TOP (it hangs), wall/floor art flush to the BOTTOM (it stands). Swapping
   frame indices moved art without its alignment, so a wall climb drew 60px above its own feet. **Check both
   signals.** PetStudio 1.4.8's own changelog line already said "ceiling poses anchor to the cell top".
2. **Three ad-hoc corpus audits were wrong in one pass**, all by applying converter-only rules to
   hand-authored companions ("absence of `<gravity>` IS the cling" is TRUE for emitter output and NOT general). My
   hand-rolled reachability walk reported 944 stranded animations; the real analyser says 14. **Use
   `ShimejiEngine.Analyze`, never a fresh graph walk.**
3. **Rest dwell was tuned three times** (9s → 1.2s → role-split). Each was measured, but the first two
   optimised one number for the whole corpus. The resolution was that a rest is TWO things: the hub the companion
   returns to (must be brief) and a performance the user wants to watch (must linger).
4. **A throttle that stamps on a NEGATIVE answer goes blind — ask what the interval is bounding.** The
   update check's interval bounds NETWORK TRAFFIC when the answer is "nothing newer"; it is not a claim about
   how fresh the answer must be. A day was the wrong number for that job, and the blind window landed on a
   fresh install. If a notice has exactly ONE surface, also refresh when that surface opens.

### Mutation testing keeps finding the same trap

Several guards were SILENT because a distance-bounded regex matched a helper's DEFINITION instead of its
CALL — the helpers sit immediately after their callers. Fixed with a `Get-MethodBody` slicer in
`tests/runtime-hardening-selftest.ps1`; use it for any new "X calls Y" invariant. Also: an exemption needs
mutating too — my first hand-authored-companion probe removed ZERO edges and "passed" without testing anything.

### Open, and the two that matter most are process not code

* **The live smoke script has never been walked, v1.9.4 → v1.9.9 (six releases).** Everything rests on the
  gate, the soaks and the mutation suites, none of which opens a window and looks at it.
* **Companion Studio's timeline Run button has no coverage** (the chain compiler does).
* A one-frame `repeat="0"` animation is invisible (~0.1s; Hornet's `Grapple3`).
* Hornet's ceiling art reads as "sideways" — the skin's own art, used correctly. Taste call; user chose to
  keep the ceiling region. See mistake #1 before touching it.
* See `BACKLOG.md` for the rest (6 open items; several stale entries were corrected this session).

### Behaviour numbers, if you need a baseline

Hornet, simulated: ceiling visit every ~9 min lasting ~6.4s median; wall every ~3 min; Pearl (`pink_sheep`,
hand-authored) is the reference at 9.0% ceiling / 12.3% wall and 86% of its time in motion. The simulators
live in the session scratch dir and are not committed — rebuild from `sim.py` if needed.

---

## Previous session (closed 2026-08-31 — the jump landing, released as v1.9.6 then v1.9.7)

### v1.9.7: correcting 31 companions exposed that there was no way to DELIVER a companion correction

Asked "if we correct a companion, how does an existing user get it?" — and the answer was that they did not. The
Companions pane diffed the catalog **by id alone**, so a companion already installed was filtered out of "available to
download" however much its content had changed, and the pane cheerfully reported *"you already have every
available companion"*. The jump fix would have reached new downloads only.

**This is the shape of gap worth looking for: a distribution channel that carries content but no notion of a
content REVISION.** Modules had a version field and an Update button; companions had neither, and nothing in any
gate had an opinion because nothing was broken — the feature simply did not exist.

Fixed by hashing, not by adding a version field. The catalog already records the SHA-256 of the exact bytes it
serves and the installer writes those bytes verbatim, so `CompanionProvenance` compares the installed file's hash to
the catalog's. **Verified against the live catalog before writing any of it**, because the obvious worry was
line endings: raw.githubusercontent serves the committed git blob and `New-ContentCatalog.ps1` hashes that same
blob, so they agree exactly. (Do NOT check this by hashing the working-tree file — a checkout is CRLF, git
stores LF, and the mismatch looks like a bug in the comparison.)

A `catalog.sha256` stamp beside each companion separates "the catalog moved on" from "you edited this", and an absent
stamp is deliberately NOT assumed safe. Consequence to expect: **every companion installed before 1.9.7 warns once**
on its first update. Backfilling the stamp was rejected — it would assert the file is unmodified, which is the
one thing the stamp exists to avoid guessing.

A live report ("Hornet seems to land in one of the sit poses; shouldn't she land on her feet?") turned into
four converter fixes, a migration, a behaviour debugger, and one **engine** fix that the first change flushed
out. Header format **1.2 → 1.3**, applied by the new `ShimejiConvert rejump <PetsDir>` migration to 31 companions
(30 jumps re-arced, 2 weak rises flattened). Companion Studio 1.4.17 → **1.5.0**. Full detail in
[`BACKLOG.md`](BACKLOG.md) under PHASE 0.

### The one that matters most: fixing a jump exposed a companion trap in yesterday's release

**A companion hanging under a window could never let go, and ~99% of window-underside grabs ended that way.** Phase
E shipped in v1.9.5; this is a bug in it, not in the jump work, but the jump work is what made it reachable.

- `ReleaseWindowGrip` implements "let go" BY playing the fall animation. Nothing did the inverse, so a graph
  that reached `fall` through its own `<next>` edge kept the grip.
- The `WindowGrip.Bottom` branch pins `y` to 0 (the pin IS the follow) and **both** of its release conditions
  test `y`. With `y` zeroed neither can ever fire. The trap is structural, not a missed case.
- Every converted companion's ceiling poses offer that edge at weight 25 of 105 on every pass, against an escape
  (crawl to a window corner) that covers 32px per 12.8s. `0.656^12` ≈ 1%.

**The lesson, and it is the same one twice in one session: a change that widens a REACH re-prices every edge
that reach can now meet.** The jump went from 15px to 46px, which tripled how often a companion could touch a
window's underside, and nothing in the graph, the validator, the reachability walk or the gate had an opinion
about that. Before widening a physical capability, list what it can now collide with.

Fixed by `FormCompanion.GripMustRelease` + one call site, guarded TWO ways on purpose: pure assertions in
`--hardening-selftest` catch a wrong predicate, and a new source-text invariant catches a predicate nobody
calls. Mutation testing confirmed the split cleanly — the unit assertions caught all four predicate mutations
and none of the three call-site ones; the invariant caught all three call-site ones and none of the predicate
ones. Neither alone was sufficient.

**The one thing to internalise: the acceptance bar is a bar on the GRAPH, and all four defects were in the
NUMBERS.** Every broken jump validated, round-tripped and was fully reachable. Reachability proved the jump
could play; nothing proved it looked like a jump. So:

- **Where the converter SYNTHESISES a physical quantity, assert the quantity, not the wiring.** The jump's
  peak height, its pace and its horizontal span are all emitted by the converter rather than read from the
  source, and all three were wrong in shipped companions while every structural check stayed green.
- **A "bounded" arc is not a bounded arc.** The height of a linear start→end ramp is about
  `a²(N-1)/(2(a+b))`, so clamping the launch `a` does nothing while the step count `N` comes from the source
  skin. Measured over the 32 shipped jumps: 16 under 20px, 16 at 72px, none in between.
- **Fixing one pass-through exposes the next.** Capping the vertical arc made Grapple4's inherited
  -100px/tick horizontal dash matter for the first time: the jump then crossed 1500px and met a SIDE border
  before the ground, so 16 of 18 jumps never reached the landing edge that had just been added.
- **A fixture can pass by luck.** `BigJump` (2 poses at 4 ticks) makes the old locomotion budget pick exactly
  the 14 steps the solved arc wants, so a pass-through launch satisfied every height assertion. Three more
  fixtures were needed — `PullUp` (the 72px fling), `HopUp` (the 11px twitch plus the violent x) and
  `LongLeap` (more frames than the step budget, which is the only case a fixed launch cannot serve) — and
  each was chosen because mutation testing showed the guard was silent without it.

**Prefer a MIGRATION to a re-conversion whenever no new sprite frame is involved.** `rejump` needs no source
skins, cannot regenerate 25 sheets into identical pixels, and does not wipe Hornet's hand-edited
`fall`/`Grapple3` frame swap. Every policy value it uses is a `public const` on `PetEmitter`, so the
migration and a fresh conversion cannot drift.

---

## Previous session (closed 2026-08-28 — jumps, drag swing, gaze, all four window edges; released as v1.9.5)

Finished the whole cursor/window condition plan in one sitting: Phase 0 (jumps), A (drag swing), B (gaze),
C (window-edge vocabulary), D (window side cling), E (window underside). **Catalog is 53 companions / 6 modules.**
Host at **v1.9.5**; Companion Studio walked 1.4.12 → **1.4.17**, one publish per phase because it source-links the
converter engine, `Xml.cs` and the validator.

**What a converted companion can do now that it could not before:** jump; swing from your hand while dragged
instead of hanging in one frozen pose; sit and look at your pointer; and use **all four edges of a window**
— stand on the top, grip a side and climb down the frame, jump into the underside and hang from it, and
swing round the corner between side and underside. All 31 converted companions gained the window edges (237
window-left, 237 window-right, 32 window-top, 32 window-bottom).

### The thing to internalise before touching this area again

**No new art was needed for any of it.** Every converted companion already shipped wall and ceiling poses that
could only ever be used at the two SCREEN edges. A window has four more edges, and that — not fidelity to
the source skins' `activeIE` actions — is what the window phases actually bought.

**Because the `activeIE` count was a trap.** The plan justified the window work with "184 actions gated on
window geometry". Survey all 12 desktop skins and you find 392 actions mentioning `activeIE` and **zero
carrying a sprite**: they are `Sequence`/`Select` wrappers choreographing Walk, Stand, Sit, Jumping and
GrabCeiling ("walk to a point 100-400px right of the window's left edge, then stand, then sit"). And no
converted companion carried a window edge at all — all 955 belong to the hand-authored sheep. Phase C on its own
shipped nothing observable. If a future plan quotes a residue action count as a payoff, check whether those
actions have sprites first.

### Traps this session, in the order they will bite again

- **Emitting an animation is two steps, not one.** Admitting an action to the spoke list is half of it; its
  poses must ALSO be in `PosesToComposite`, or the sheet never gets the frames, `FramesOf` finds no key, and
  the spoke is silently dropped for having zero frames — one step before whatever you were trying to add.
  This cost the first gaze attempt a whole build cycle with "0 emitted" and no error anywhere.
- **A cascade's variants are not interchangeable.** For a gaze, the one to emit is the UNCONDITIONAL
  fallback, not `Animations[0]`. Across all seven skins that ship one, the first variant is
  `cursor.y < screen.height/2.5` — "pointer near the top of the screen" — so `Animations[0]` is a companion
  permanently craning upward. A median pick is also wrong: Serial Designation J's seven variants split on
  cursor.x as well, so the middle is "up and to the left".
- **`hwndWindow` means "standing on the TOP"** everywhere it is read, and means it geometrically:
  `CheckTopWindow`'s coverage test compares candidates against `rctO.Top`, `FollowWindow` re-pins to the
  top. A side or underside grip needs its own state. `hwndWindow` is now a PROPERTY that clears the grip,
  because nine sites drop that handle for their own reasons and any one forgetting strands the companion.
- **Opt in by EXACT match on the discriminator, never a bit test.** 955 `only="window"` edges ship in the
  hand-authored companions. A bit test recruits every one of them into a behaviour their authors never asked for.
  `SetNextBorderAnimation` has an overload reporting which condition the chosen edge declared, precisely so
  intent does not have to be inferred from the chosen animation's shape.
- **A maximised window's bottom edge sits on the work area**, i.e. directly over a companion standing on the
  taskbar. Without a clearance test the companion grabs the underside on the first tick of every jump it makes.
- **A jump has no `<gravity>` node** (gravity would cut the arc off at frame one), so "carries no gravity"
  is NOT a usable test for "is not a floor animation". Hub reachability is. This broke one of my own
  assertions, which rejected the fixture's own jump.
- **`--hardening-selftest` writes to `%TEMP%\dp-hardening-selftest.txt` and prints nothing** — it is a
  GUI-subsystem exe. Grade it from the file, and delete the file first or you grade the previous run.
- **The XSD has two copies** (`src/Resources/animations.xsd` and `Resources/animations.xsd`) and they must
  stay byte-identical. A new `only=` value that misses one fails the emitter self-test with an enumeration
  error that reads like a converter bug.

### Two checks that were WRONG and had to be fixed by the mutation run

Worth reading if you write a source-text invariant, because both passed against broken code:

1. **"the grip is dropped with the handle"** asserted only that the property body contains
   `windowGrip = WindowGrip.None`. Disable the `if (value == (IntPtr)0)` guard and the statement is still
   there, just unreachable — assertion still green. It now asserts the condition.
2. **"the underside is checked before the screen top"** asserted index ordering. A `RiseDetect` call that
   appears first but is gated behind something unreachable satisfies that. The load-bearing property is
   that the screen-top test is CHAINED off the underside result (`else if`), so both cannot fire on one
   tick.

Also recorded rather than papered over: the `deltaY >= 0` early-out in `CrossesAscendingBoundary` is
redundant (a non-negative step cannot satisfy the inequality pair) and its mutation survives. It is kept for
symmetry and NaN screening, and the comment now says it is intent, not a guard. The same is true of the
`deltaY <= 0` in the pre-existing `CrossesDescendingBoundary`.

### Mutation testing, per phase

39 mutations across the four phases, every one caught and naming the right symptom: B 7, C 8, D 13, E 11.
The scripts live in the session scratchpad, not the repo. Their shape is worth recreating: a table of
`{file, exact old string, new string, expected failure text}`, an assertion that the old string occurs
**exactly once** (a mangled pattern must fail loudly, not no-op into a false green), build + run the right
suite, restore in a `finally`. Watch for mutations that will not compile — `if (false) return x;` trips
CS0162 with warnings-as-errors; change the returned VALUE instead.

### Still open

- **Live smoke of the window behaviours has not happened.** Nothing automated can watch a companion hold a real
  window. Check: grip and climb down a browser window's side; drag that window while the companion holds it;
  minimise it while the companion holds it; jump under a FLOATING window and catch the underside; walk to the
  corner and swing onto the side; confirm a companion on the taskbar under a MAXIMISED window jumps normally and
  grabs nothing.
- **`ChaseMouse` is the only thing left in that backlog section**, and the question it was deferred on is
  now answerable: does a gaze that aims on entry and re-enters every few seconds already scratch the
  "companions follow your mouse" itch? Watch a companion before building a per-tick movement mode.
- **Two commit messages in the public history** (`6d35260`, `aa80652`) cite "California all-party-consent /
  FERPA" as the rationale for BACKLOG #17 being local-only. Benign engineering rationale, no names or
  personal data, and rewriting public history costs a force-push. Left alone deliberately; noted so it is
  not rediscovered as a surprise.

---

## Previous session (2026-08-28 — ceiling, borders, drag; released as v1.9.4)

Went backwards through what the v1.9.3 smoke test left open, then did two module passes.
**Catalog is now 53 companions / 6 modules.** Host at **v1.9.4**; modules at fortunes 1.2.4, aibrain 1.2.3,
petstudio 1.4.8, reminder 1.7.0, remembrance 1.1.0, blinkingled 1.0.2.

**Know this before planning any module work:** the MSI and ZIP bundle NO modules
(`installer/DesktopAICompanion.wxs` has no `modules` reference; the v1.9.4 release assets are just the MSI, the
ZIP, two nupkgs and SHA256SUMS). Merging to `master` IS the module publish, because `modules-dist/` is
served off master. A host release is only ever needed for engine code inside the exe. Three modules shipped
after the v1.9.4 tag with no release at all.

### The two module passes (after the release)

7. **AiBrain 1.2.3 removed its own idle timer.** The options window offered two ways to make the companion talk
   unprompted and they largely duplicated: a module-owned 90-150s "Idle commentary" loop AND the host's
   global drop, both ending in the same `Ask()` and the same bubble with no shared cooldown. Idle fired ~8x
   more often, so the drop was statistically invisible. The timer and its three settings are gone; the
   global schedule is the only one. **The label also lied**: there is no `GetLastInputInfo` in the repo, so
   "idle" gated on SCREEN CHANGE, not idleness. That gate could not survive the move (the drop responder
   must answer synchronously so Fortunes can take the tick; the comparison is async), so on a static screen
   the companion now comments anyway. `AiBrain.ScreenChanged` is kept, unused and labelled, as the primitive a
   future "only when something changed" option would need.
8. **New module: Blinking LED** (see BACKLOG). The interesting part is what a port does NOT need to carry.

### Traps from the module passes

- **The freshness check fires on CI, after you commit, not in your local gate run.** Companion Studio source-links
  the converter engine, so the ceiling work made `modules-dist/petstudio.zip` stale and master went red
  while the release was green. Expect this on ANY converter change. The publish script also refuses to run
  with the module source uncommitted, which is correct, so the version bump has to be its own commit first.
- **`_lastInteractionUtc` nearly became write-only** when the idle loop went. If you delete a loop, grep for
  what only IT read. It now guards `OnDrop` instead, which is where it earns its keep.
- **A test assertion can be too weak to catch the bug it exists for.** BlinkingLed's "the picker varies its
  line" originally required only "more than one distinct line" — which a picker hardwired to `pool[0]` would
  have PASSED, because the no-repeat guard bounces it between indexes 0 and 1. Only the mutation test
  exposed that. It now requires the whole pool.

1. **A companion could get welded to the cursor.** `PictureBox1_MouseUp` was the ONLY thing clearing
   `IsDragging`, and `NextStep` re-snaps to `Cursor.Position` every tick while it is set, so anything that
   steals mouse capture mid-drag (the reported case was a delayed Greenshot capture; a lock screen or UAC
   prompt does the same) ate the release and the companion followed the cursor forever. `NextStep` now polls the
   GLOBAL `Control.MouseButtons`, which needs no capture, and releases through a shared `EndDrag()`.
2. **Borders now contact the CHARACTER, not the window.** A converted shimeji floats inside a padded cell
   (Hornet's standing frame is x=176..233 of a 256px cell), so she turned around while still visibly
   inland. The four horizontal border sites use the current frame's visible-pixel box, factored out of
   `GetSpeechAnchor` as `GetSpriteInsets`. The drag grab is centred the same way. Hand-authored companions fill
   their frame, get zero insets, and are untouched.
3. **Ceiling walking converted.** Every source skin already had the animations; the blocker was that
   ceiling sprites anchor at 64,48 instead of 64,128, so under the floor convention they hang from their
   feet. They now composite anchored to the cell TOP (which is where the engine pins the window at a
   horizontal border) and the band above the anchor is skipped at the source. Because the ceiling anchor is
   SMALLER than the floor anchor it cannot raise `max(AnchorY)`, so the cell does not grow. Entry is an
   `only="horizontal"` edge on the wall CLIMB spoke ONLY, winning about 2 in 3.
4. **The self-test `%TEMP%` leak.** Six self-tests staged modules into `%TEMP%` and all six swallowed their
   delete; the four using a collectible ALC could never succeed, because unload is async and the DLL is
   still mapped. ~380 directories had piled up. `SelfTestScratch` defers cleanup to the next run's sweep and
   REPORTS a delete it could not do. `%TEMP%` went to 1.
5. **Sonic dropped, three skins added.** Sonic had a single stub wall and ceiling action and produced zero
   wall spokes. A scan of all 3165 catalog rows put three skins tied at 179 animations; one turned out to be
   a re-upload of Uzi Doorman (already shipped) and one a two-variant Capybara pack. In go Capybara (Brown),
   Capybara (Albino) and Serial Designation J (175 animations, the most ceiling content of any candidate).
6. **Blurbs reached the download cards.** They existed for all companions but were wired only into the INSTALLED
   card, so the gallery showed name and author until after you downloaded.

### Traps found this session

- **A test heuristic can rot silently.** `EmitterSelfTest`'s "the hub is whatever fans out most" stopped
  identifying the FLOOR hub the moment the wall region had two spokes, and reported the hub selecting a
  wall animation when what it had actually found WAS the wall. It now selects on the presence of
  `<gravity>`, which is the real discriminator.
- **Assert the mechanism, not a proxy.** The first ceiling geometry assertion (cell height did not grow)
  could not fail: the cell is `max(AnchorY)` and the ceiling anchor is smaller either way. The assertion
  that has teeth is pixel-level, and it needs BOTH ends of the tile, because the two anchor conventions put
  the sprite in exactly opposite halves.
- **Do not pass a multiline commit message through PowerShell here-strings.** Quotes inside get re-split by
  native-arg handling and git reads the fragments as pathspecs. Write the message to a file, `git commit -F`.

---

## Previous session (2026-08-27, evening — companion quality pass, released as v1.9.3)

A live smoke-test session: the maintainer watched real companions and reported what looked wrong, and each
report turned into a converter fix. **Catalog is 51 companions / 5 modules.** `master` clean, gate green.

**Four fixes, in the order they were found. Three of them were bugs I introduced earlier the same day,
so read this before assuming the converter is settled.**

1. **Hub weighting was catastrophically skewed** (first find, from "why does Hornet's Grapple3 never
   play"). 368 of 582 animation options sat below 1% of their hub's pool; the worst needed ~54 minutes of
   idling to appear once. Damped with a square root, then floored at 1.5%. Now 0 below the floor.
2. **Wall climbing added** (converted companions were floor-only). The engine always supported it — 17 of the 22
   hand-authored companions use wall/ceiling/window edges. **The cling is the ABSENCE of `<gravity>`.**
3. **Companions hovered above the taskbar.** The compositor reserved a band *under* the anchor, but the host
   stands a companion by putting its WINDOW's bottom edge on the floor, and the window is one cell. Anchor now
   sits on the cell's bottom edge. 6 companions hovering -> 1, worst 20px -> 1px.
4. **Then that fix caused a black blob** in the corner of the drag frame: the cell got shorter but the
   blitter still drew the whole sprite, so frames bled into the neighbouring tile. `BlitOpaque` now clips
   to the tile.
5. **Rests were far too short** ("the Knight read a book for 4 seconds, should be 10"). Two causes: every
   non-locomotion animation was emitted `repeat="0"` (one pass), and a single-frame hold could only reach
   MULTIPLES OF THE 4s INTERVAL CAP, so a 10s pose landed on 8. A single-frame rest now picks the fewest
   passes that keep each interval under the cap and divides the target evenly (10s = 3 x 3333ms).

### The traps worth knowing before touching the emitter again

- **Never pick a FIXED repeat count.** It has now been the bug twice: a fixed 3 on Hornet's 32-frame climb
  produced a **51-second** wall sequence, which is the same mistake `TargetLocoMs` was created to prevent.
  Budget the TIME (`RepeatCountForBudget`) and let the frame rate decide the count.
- **The interval is also the animation's tick.** A single 10s frame would mean 10s before the companion notices
  it should fall, which is why long rests are split into several shorter passes rather than one long one.
- **Rests round UP, walking rounds to nearest.** Undershooting a rest reads as a twitch; overshooting a
  walk means gliding past where you expected it to stop.
- **Wall poses share the floor anchor (64,128); CEILING poses do not (64,48).** That is the whole reason
  wall climbing was safe to add and ceiling still is not — admitting ceiling poses pads the cell and floats
  every floor companion again. Ceiling needs anchor normalisation plus a per-animation `<offsety>`.
- **The wall region takes Group1 AND Group2.** Group2 means the selection CONDITION needs host state we
  lack, not that the animation is unconvertible. A Group1-only filter silently produced a companion that grabs a
  wall and hangs there motionless.
- **Re-converting is the only way to change companion ART or FRAMES**; a migration over the shipped XML can only
  touch numbers (that is how the reweight worked). Every source skin is local, so no downloads:
  `shimeji-catalog\data\catalog.csv` maps `source_item_id` -> `blob_path` (blobs sharded by first two hex
  chars), plus named zips at that root and the Shimeji-EE bundle.
- **Hornet carries a hand edit**: its `fall` and `Grapple3` frame lists are swapped by request. Re-conversion
  wipes it, so re-apply BY NAME (frame indices change with the sheet).
- **Two batch-harness gotchas:** `ZipFile.ExtractToDirectory` refuses skins containing an entry named `/`;
  and Shimeji-EE allows a PER-SKIN conf at `img\<Skin>\conf`, so pairing a top-level conf with another
  skin's sprites fails (Gengar).

### Still open

- **Horizontal inset.** Hornet's standing frame sits 176px into a 256px cell, so at a screen edge the
  visible character looks inland — reported as "climbing not at the edge". Entry really is screen-edge-only
  (verified against all six `SetNextBorderAnimation` call sites). Not trivially fixable: the cell cannot be
  trimmed (across all frames the content fills it) and the compositor bakes the x offset into pixels because
  `<offsety>` is y-only.
- **A companion can get stuck to the mouse.** Reported once, not reproduced. The companion graph and the engine's
  mouse-up path both look correct, so the suspicion is lost mouse capture — pre-existing, not from this work.
- **Ceiling behaviour**, per the anchor note above.
- **The self-test flags leak GBs of `%TEMP%\dp-*` scratch.** 3.2 GB found and cleaned; it returns every run.

## START HERE (session closed 2026-08-27, earlier)

**Goal of this session: make the two newest modules testable by OTHER PEOPLE.** No host release, no `v*`
tag — everything is module-only or repo tooling, so the host binary is unchanged from `v1.9.2` and testers
pick all of it up from the in-app Modules pane. `master` at `b5d2254`, tree clean, full gate green
(16 self-tests, no skips). Live catalog verified: raw serves the new versions and a served zip's SHA-256
matches the catalog entry.

Catalog now: **fortunes 1.2.4, aibrain 1.2.2, petstudio 1.4.1, reminder 1.7.0, remembrance 1.1.0.**

1. **Remembrance 1.1.0 — the setup friction is gone, which was the actual blocker.** It used to take two
   file paths and offer no way to obtain what they point at, so a tester had to install a C++ binary and a
   141 MB model by hand. Now "Set up Whisper for me…" detects an existing install (including the DevToolbox
   layout `install-whisper.ps1` produces) or fetches whisper.cpp + the chosen model from upstream, then
   PROVES the pair runs. Plus backlog #17's P3: an optional local-Ollama summary beside the transcript,
   map-reduced so a long meeting fits a small context window. Local only, forever. Gains `Network`.
2. **Reminder 1.7.0 — the companion visibly reacts when a reminder fires.** BACKLOG.md claimed this needed a host
   release; it did not, and that claim is now corrected in place. `IHost.PlayAnimationAll` has existed all
   along. Only MOVING a companion would need new ABI, and that is deferred on purpose (position is driven by
   animation velocity expressions, so a move verb fights the engine).
3. **The publish-freshness check was half-blind, and fixing it found real rot.** It only watched
   `modules/<Id>/`, so it could not see source-linked files or the bundled ModuleKit. Widened to a
   csproj-derived watch set; its first run found fortunes, aibrain AND petstudio all shipping a ModuleKit
   3-4 commits stale. All five modules republished to clear it. Mutation-tested with a negative control.
4. **Both new modules now have real self-tests**, wired into `run-gate.ps1` and `build.yml`
   (`--module-selftest=reminder`, `--module-selftest=remembrance`). Reminder's six pure helpers already had
   internal checks that NOTHING ran; they are aggregated now.

### Three traps this session hit that will bite again

- **`git` is not on PATH in a PowerShell agent shell**, and the box's only git is a full HKLM-registered Git
  for Windows at an unusual location (`C:\Anthropic\.Git`). Do NOT winget-install `Git.Git` to "fix" it: the
  winget version is older, so the installer aborts as a downgrade. Its `cmd` dir is now on the User PATH, so
  new sessions are fine; inside an already-running session prepend it. Everything that shells out to bare
  `git` depends on this (`run-gate.ps1`, `Test-ModulePublishFreshness.ps1`, `New-ModulePublish.ps1`).
- **A host may hand a module a NULL settings store, and that killed both modules at load.** The app's own
  `--module-selftest` harness returns null from `GetSettings` AND `GetStorage`. Anything reading settings
  during `Init` (building an options SCHEMA whose dropdown depends on a saved value; a legacy migration)
  then dies as an unexplained "module did not load: NullReferenceException". Use
  `host.GetSettings(Id) ?? new MemoryModuleSettings()` (new in ModuleKit).
- **`--module-selftest=<id>` runs the FIRST `bool SelfTest(out string)` it finds in the assembly**, over all
  types including non-public. A helper class sharing that exact name can silently win over the module's own
  entry point. Filed in BACKLOG.md; worked around here by renaming helpers to `SelfCheck`.

### What is still NOT verified

- **Live audio capture.** This dev box is an RDP session, which presents no real microphone (0 capture
  devices, only "Remote Audio"), so start/stop of real recording is untested. It needs a physical console —
  that is what the maintainer is smoke-testing on other workstations. The module warns about it in its
  status line.
- **A tester's cold first-run download** on a box with no DevToolbox and no prior Whisper. The download was
  verified into a throwaway temp root here, not cold on someone else's machine.
- **The reaction animation eyeballed on screen** with live companions.

---

## START HERE (session closed 2026-08-26) — superseded by the run above

**Shipped v1.8.0's feature payload: a fourth catalog module (Reminder), a module-owned styled-speech
platform, and two global audio toggles — on `feat/reminder-and-fixes`, pushed to `master`, Reminder in the
catalog.** ProductVersion is `1.8.0`; host ABI grew (additively) to `1.8.0`. Full detail in BACKLOG.md
("v1.8.0 — shipped") and the `project_desktoppet` memory note. In one breath:

> **The Reminder module has since grown to 1.5.0, all catalog-only (no host release needed).** 1.3.0 = up to
> five calendar slots each with its own name + speech style; 1.4.x = a browsable, per-calendar chime with a
> per-calendar on/off; **1.5.0 = seven more (join-the-meeting links + a Join tray entry, on-demand agenda, a
> daily morning briefing, skip declined/all-day, a per-slot Test button, typed personal reminders, and
> hush-while-presenting).** `master` at `598d821`. The pure helpers have internal SelfTests, run green under a
> net10 host (Windows PowerShell 5.1 can't load a net10 dll — use `dotnet run`); the WinForms/COM/tray paths
> are review + build-green only. A "companion physically reacts to an event" idea is backlogged (needs a host change).
>
> **`v1.8.1` was tagged + released (2026-08-26) at the maintainer's request for a fresh CI build to smoke-test.**
> The host binary is byte-unchanged from 1.8.0 (this session was all module work); ProductVersion was bumped
> only so the tag matches. The Reminder features are NOT in the release artifacts — modules are catalog-delivered
> — so testing them means updating Reminder to 1.5.0 from the in-app Modules pane. Same redistribution caveat as
> v1.8.0 applies to the published binaries.
>
> **Then a NEW module, Remembrance (meeting recorder), + host ABI 1.9.0 + `v1.9.0` released (2026-08-26).**
> Spec + build status in `REMEMBRANCE-PLAN.md`. Host ABI grew to 1.9.0: `IHost.PublishContext/ReadContext/
> ContextChanged` (a shared key/value channel between modules) + `ModulePermissions.Microphone`/`SystemAudio`.
> `Reminder 1.6.0` captures attendees + publishes `meeting.current` (in the catalog; needs 1.9.0). `modules/
> Remembrance 1.0.0` records mic + system loopback (NAudio classic WASAPI), offline whisper.cpp transcription,
> calendar naming/roster, snapshot hotkey, 72h purge. **NOT published to the catalog** (untested audio).
> **Whisper is installed** on this box via `scripts-utilities\scripts\install-whisper.ps1` (whisper.cpp +
> ggml-base.en) at `%LOCALAPPDATA%\DevToolbox\whisper\`; the whisper-cli invocation was verified on a test
> clip, but the live capture/mix path is unrun. **RDP LIMITATION:** a Remote Desktop session presents no real
> mic/speakers (only "Remote Audio", 0 mics), so Remembrance can't be recorded/tested under RDP — needs the
> machine's local CONSOLE session. `v1.9.0` release re-publishes the same redistribution-blocked binaries.
> Module TFM is `net10.0-windows10.0.19041.0` (NAudio.Wasapi floor).
>
> **`v1.9.1` released (2026-08-26): speech bubble anchors over the visible sprite, not the frame.** A shimeji
> floats inside a padded/transparent cell, so anchoring to the frame put the bubble out in empty padding
> (detached from the character). New `src/dotNet/SpriteBounds.cs` finds the frame's visible-pixel bbox (colour-
> key or alpha, cached per frame image) and `FormCompanion.GetSpeechAnchor` anchors to that. Built-ins unaffected.
> User-confirmed working. Also fixed this session: `New-ContentCatalog.ps1` read source JSON as ANSI, mangling
> non-ASCII names ("Коро", "Kurt Gödel") in catalog.json — now reads UTF-8.

- **Reminder module** (`modules/Reminder`, v1.2.0, `MinHostVersion 1.8.0`, perms `Speech|Storage|Network|
  Audio`): the companion announces upcoming calendar events. Three sources — a local JSON feed, a **Calendar URL /
  ICS** (iCal.Net 5.2.3 + NodaTime; Google / published Outlook / M365 / iCloud, with recurrence + time
  zones), and a **running desktop Outlook over late-bound COM** (attaches only to an already-running
  OUTLOOK.EXE, never launches, never quits). Multiple **lead times** (fires e.g. 15 + 5 min before via
  `DueNowMulti`/fired-key `eventId@lead`), **quiet hours** (overnight-aware), an optional **chime** (embedded
  MP3 via `IHost.PlaySound`), the **event location** in the announcement, and per-module speech styling.
  `CachingCalendarSource` keeps last-good on failure + throttled STA background refresh.
- **Module-owned styled speech** (the reusable platform behind it): `SpeechStyle` on the ABI +
  `IHost.Say/SayAll(text, style)`; the bubble (`FormSpeech`) is now a dumb renderer honoring family/size/
  bold/italic/underline/color; `DesktopAICompanion.ModuleKit.SpeechStyleSettings` gives any module the setting fields
  + load/save + `ToStyle` in ~2 lines. Other modules can adopt it later.
- **Two global Sound master switches** (Preferences → Sound): **companion sounds** (embedded `<sound>` SFX) and
  **notification sounds** (module `PlaySound`, e.g. the chime), independent, both default-on
  (`AppSettingsStore` nullable-bool pattern; `StartUp` gates SoundSink + PlayModuleSound).
- Also landed earlier in the session on `master` (commit `abdd594`): the shimeji converter's
  frequency-weighted behaviour + WAV→MP3 sound capture (all 27 shipped companions re-converted, companions.json = 49),
  Companion Studio 1.4.0's **"Analyze installed companion" dropdown** (host `ICompanionManager.TryReadTypeXml`), and the
  Fortunes smart-picker repeat fix.

### What is NOT done -- read this before picking anything up

- **v1.8.0 IS released (2026-08-26), with the maintainer's informed go-ahead on the blockers below.** The
  GitHub release carries `DesktopAICompanion.msi`, `DesktopAICompanion-Portable.zip`, both author nupkgs, and
  `SHA256SUMS.txt`. The first tag attempt (at `ee07a1c`) FAILED the MSI step on a latent `WIX0104`: two XML
  comments in `installer/DesktopAICompanion.wxs` carried a `--` in the body (added after v1.7.0). Fixed in `9c6239d`
  (em-dash separators), re-pointed the tag, re-released green. **Lesson: the `.wxs` is exercised ONLY by an
  actual `v*` release, never by `run-gate.ps1` or the normal CI build, so a `--` in a comment there is
  invisible until you tag.** If you touch the installer, sanity-check it stays valid XML before tagging.
- **A `v*` tag auto-publishes binaries — standing caution, not a one-off.** `release.yml` triggers on
  `push: tags: v*` and publishes the ZIP + MSI to a public GitHub release. Those binaries bundle exactly what
  `THIRD_PARTY_NOTICES.md` lists (top of file) as **unresolved redistribution blockers**: the unlicensed
  upstream WinForms engine (Adrianotiger, no license grant), sprites without a complete redistribution grant,
  and the mixed/copyrighted fortune corpus. The maintainer accepted these for v1.8.0. If they ever need
  pulling back: `gh release delete v1.8.0` then `git push origin --delete v1.8.0`. Weigh the blockers again
  before the next `v*` tag.
- **The 12 MiB companions require the new app build.** `RemoteCatalog.Parse` throws out the WHOLE catalog if any
  companion exceeds the app's `MaximumXmlBytes`; some shimeji exceed the old 4 MiB, so any app still on 4 MiB
  breaks on the new catalog (loses all "Check for new companions"). The maintainer chose this (keep quality,
  require app update) over reverting the budget.
- **Content-rating pass** on the catalog before it is genuinely public (shimeji.org content is unrated).
- **Reminder module manual eyeball still light.** Outlook-COM path was tested live this session (0 events
  was a genuinely all-past/expired-recurrence calendar; a past-window `Restrict` returned 46, proving the
  filter). The ICS path and the double-lead/quiet-hours/chime timing were unit-self-tested, not watched
  end-to-end against a real feed with an event a few minutes out. Worth one live eyeball.

### Four things worth knowing before you touch this

1. **`grimoire/03-companion-xml-format.md` is the authority on the companion XML format.** Read §6 (the `only` enum, and
   **the respawn rule** -- no eligible `<next>` means the companion respawns, so dead ends are intentional) and §7
   (the four magic names `fall`/`drag`/`kill`/`sync`) BEFORE concluding you have discovered anything. I
   wrote both up as findings this session and had to correct it.
2. **Do not add a shared source file under `src/` and register it in three csprojs.** `EnableDefaultItems`
   is false everywhere, so a new file must be added to the app, `modules/PetStudio` and any tool that
   compiles it -- and touching `modules/PetStudio/PetStudio.csproj` marks `petstudio.zip` stale, forcing a
   version bump and an in-app update prompt for nothing. Put shared helpers in a file the consumers already
   compile. That is exactly why `Mp3Format` lives inside `CompanionXmlValidator.cs` rather than its own file.
3. **The Shimeji format reference is not in this repo and must not be.** Clone `gil/shimeji-ee` (tracks
   Kilkakon v1.0.13) OUTSIDE the tree. On Windows the checkout fails on a macOS `Icon
` file -- the clone
   still succeeds, so `git restore --source=HEAD conf/ img/` gets what you need: `conf/actions.xml`,
   `conf/behaviors.xml`, `conf/Mascot.xsd`, and two sample skins.
4. **`run-gate.ps1` is the verification.** One command, fails on a SKIP. It caught every mistake below.

### Two bugs fixed that were not mine, both latent for a reason

- **`New-ModulePublish.ps1`** passed git two pathspecs, not one: in PowerShell
  `@('status','--porcelain','--','modules/' + $x)` builds a FIVE-element array, so git saw `modules/` and
  `AiBrain` separately. The guard therefore tested "is anything under `modules/` dirty" and then blamed the
  module being published. It refused to publish aibrain because `PetStudio.csproj` was dirty.
- **`run-gate.ps1`** deleted self-test markers with `Remove-Item -LiteralPath`, which still performs `~`
  home-directory expansion. Windows uses the 8.3 short form for `TEMP` when the account name exceeds 8
  characters, and that contains a `~`. **Latent because run one has no marker to delete** -- a fresh box
  passes first and fails second, and CI never sees it because the runner's profile is short.

### Three mistakes I made and corrected

- Extracted `Mp3Format` into its own file. The gate caught the PetStudio build break; then I realised the
  csproj edit would force a pointless `petstudio` republish and folded it into `CompanionXmlValidator.cs` instead.
- Claimed the four magic names and the `only` semantics as findings. `grimoire/03` §6-§7 already had both.
  `MAPPING.md` now separates "already documented" from "what this pass added" so it cannot happen again.
- Treated terminal animations as needing graph closure. §6's respawn rule makes them deliberate;
  `PetGraph.Terminal` is now labelled informational, and only *unreachable* animations are a signal.

### Decisions taken (review if you disagree)

- Converter is a **console tool under `tools/`**, not a module -- BACKLOG #4's own workflow is a dev
  workflow, and a CLI iterates far faster. The engine stays separable so a module can wrap it later.
- Acceptance bar is **machine-checkable only**: validates, reachable, frames index real tiles, under 4 MiB.
  Anything about whether it *looks* right is reported for a human, never enforced.
- `aibrain` got a **version bump rather than an in-place republish**, so existing installs are actually
  offered the fix and `1.2.0` keeps meaning one payload.
- **Commit identity is set repo-locally** (`git config --local user.name` / `user.email`) to match the
  author on every existing commit. Worth doing because this repo is PUBLIC and a machine's default git
  identity may be a work account -- git will happily derive one from the hostname and publish it. Check
  `git log -1 --format=%an` after cloning: repo-local config does NOT travel with the clone.

---

## START HERE (session closed 2026-08-20) -- superseded by the run above

**Two releases shipped: `v1.5.0` and `v1.6.0`.** Everything is merged to `master`, CI-green, tagged, published
and installed on this box. Tree clean, nothing half-finished.

### What is NOT done — read this before picking anything up

The session was planned as A→F. **A, AA and B shipped. C, D, E and F were never started** — there is no code
for any of them, only the design in the plan. Do not go looking for a half-built Voice module; there isn't one.

| Part | State |
|---|---|
| C — Voice module (Windows WinRT engine, speech modes) | **Not started.** Design is solid; start with the spike below |
| D — reminders (JSON/XML/line formats, scheduler) | Not started |
| E — Kokoro engine | Not started, and may never be — see the licence risk |
| F — Personality module (quotes, timers) | Not started |

**Start Part C with the spike, not with code.** Nobody has proven that WinRT
`Windows.Media.SpeechSynthesis` works from an **unpackaged Win32 process**; Microsoft's docs only describe UWP
use. AiBrain proved `Windows.Media.Ocr` works there, which is encouraging but is not the same API. Documented
fallback if the spike fails: `System.Speech` (SAPI 5), which definitely works unpackaged but cannot reach
Windows 11's natural voices. This box has David/Mark (male) and **Zira (female)** as OneCore voices, so the
"prefer a female voice" default is satisfiable here.

**Kokoro may be undeliverable, and that is an acceptable outcome.** It needs eSpeak-NG for phonemes, eSpeak-NG
is GPLv3, and we neither bundle nor mirror it. If arms-length use (a child process, never linked) does not work
cleanly, drop it, keep the Windows engine, and record why — the same call this repo already made twice, for
Tesseract bundling and for TTS itself. Do not let sunk design cost force a licence decision.

The **host ABI it all needs already exists and is released**, so Part C needs no further host work:
`PlaySound` / `StopSound` / `RegisterSpeechResponder` / `Audio` + `Voice` permissions, all in 1.6.0. A Voice
module declares `MinHostVersion 1.6.0`.

### What shipped

| | |
|---|---|
| **v1.5.0** | per-companion speech routing: a reaction belongs to ONE companion, plus the Companion Speech tray cascade |
| **v1.6.0** | the audio + speech-interception ABI a voice module needs (`PlaySound`, `StopSound`, `RegisterSpeechResponder`, `Audio`/`Voice` permissions) |
| **fortunes 1.2.0, aibrain 1.2.0** | live in the catalog; both require host 1.5.0 |
| **petstudio 1.1.1** | themes from `IHost.IsDarkTheme` instead of the OS registry |
| PRs | #85 backlog, #86 CI fix, #87 host ABI + tray, #88 modules, #89 audio ABI |

Both releases hash-verified and installed here; `Contracts.dll` refreshed to match each time, which is
release-checklist row 10 and the failure that silently breaks every module.

**A second latent bug fixed in 1.6.0, worth knowing:** an unrecognised permission name made the catalog parser
throw for the **entire catalog**, not the entry. Since every catalog feature shares one fetch, the first
release to add a flag would have taken the Modules pane, the monthly update check, pack browsing *and* the
Companions gallery away from every older host. It had already fired unnoticed — `Companions` shipped in 1.4.4, so a v1.4.2
host cannot parse today's catalog at all. Publishing the Voice module would have done it again, at scale.

### The bug that started it

*"When the same companion is chosen, it speaks at the same time, and the same saying."* Correct, and it was **all**
companions, not just duplicates: `StartUp.SayAll` fanned one string to every companion and everything spoke through it.
Fixed by making a reaction belong to one companion. **Routing is the feature; `Say(companion, …)` is the fix** — per-type
routing alone would not have fixed it, because two Pearls share a routing key.

### Four things worth knowing before you touch this

1. **`triggerSpeech` uses `""` for GLOBAL; the companion mix uses `""` for the ACTIVE companion.** Keying a real companion by its
   raw mix id rewrites the all-companions preference *and looks like it worked*, because the lookup falls back to
   global — every other companion type would test fine. `SpeechRoutingKey` exists for this and an invariant pins it.
2. **The companion-aware responders are new NAMES, not overloads.** A parameterless `delegate { }` converts to both
   `Func<bool>` and `Func<ICompanion,bool>`, so overloading would be CS0121 for anyone recompiling.
3. **`IsCompanionAlive` is on `IHost`, not `ICompanion`** — `ICompanion` has seven implementations and ModuleKit ships
   `FakeCompanion : ICompanion`, so adding there breaks modules on recompile.
4. **Both leak soaks and `--wpf-options-selftest` need a real window station.** Keep the machine logged in.

### Decisions taken unattended (review these)

| # | Decision | Why | Reversible? |
|---|---|---|---|
| 1 | **Per-INSTANCE companion identity deferred; shipped per-TYPE** | Reverses an explicit choice. Pricing it found schema v3, replacing `DeriveOnScreenMix` (which the whole preview-safety invariant rests on), three rewritten CoreTests groups, two permanent removal models, and a nickname feature that does not exist — and two Pearls would *still* share one AI disposition. Types already have curated names (Pearl, Rick, Ben), so the menu reads as pictured | Yes, own release |
| 2 | **Consolidated the two release workflows, deleted `publish-release.yml`** | Both fired on `v*` and clobbered the same release, so SHA256SUMS listed the nupkgs or not depending on who lost the race. Verified fixed: the v1.5.0 tag fired exactly one workflow | Yes |
| 3 | **Poke escalation made per-companion in the same release** | Not in the plan, but shipping routed sass on shared `pokeCount` means poking Pearl three times then Rick gives Rick the sass tier. Same class of bug | Yes |
| 4 | **Repeat guard moved into `FormCompanion.Say`** | It was in `SayAll`, which `IHost.Say` bypasses, so routing would have silently killed the user's suppress-repeats preference | Yes |
| 5 | **Drop subject is round-robin, not random** | Uniform random repeats the same companion often enough to read as "still broken" | Yes |
| 6 | **Bathtub escape stays global** | Every companion fleeing *is* the joke, unlike sass which answers "you poked me". Now commented as a decision | Yes |
| 7 | **PetStudio left declaring `Speech` it does not have** | It calls `SayAll` for a user-visible error without declaring `Speech`. Changing to `Log` would hide a real error; declaring `Speech` is a permission widening needing the update-row consent delta, which is Part B | Yes, in BACKLOG |
| 8 | **`setup-msbuild` left in `release.yml`** | Vestigial, but the release path is the wrong place to discover an implicit dependency | Yes, in BACKLOG |

### Two mistakes I made and corrected

- **I corrupted `AiBrainModule.cs`** with a PowerShell `Get-Content -Raw` / `Set-Content -Encoding UTF8`
  round-trip: it read UTF-8 as ANSI and re-encoded, producing 25 mojibake sequences. Caught it, reverted the
  file, redid the edits with the editor. **Never round-trip a `.cs` file through PowerShell here.**
- **The window-soak reported a false leak** (one rooted window per segment, always the last). Not a leak — the
  strong reference escaped the cycle method into the caller's stack slot. See the BACKLOG entry.

---

## START HERE (written 2026-08-18, at the end of a long session) — superseded by the run above

**Nothing is half-finished.** `master` is clean and pushed, `v1.4.8` is released, all three modules are
published and current, and every deferred item from the previous sessions is closed. If you are looking for
"what was I doing", the honest answer is: nothing — pick something from BACKLOG.

Three things to read before you change anything:

1. **THE HOST CONTRACT below — there is no freeze, and do not reinstate one.** It was tried and it failed
   three times in three days. The six rules replace it and are already enforced by gates.
2. **`docs/module-authoring.md`** is the entry point for anything module-shaped, including your own.
   `dotnet new desktop-ai-companion-module` scaffolds a module that builds and self-tests as generated.
3. **`tests\run-gate.ps1` is the whole local gate in one command**, and it fails on a *skipped* self-test on
   purpose. Run it before you believe anything.

Two traps that cost real time here, both now guarded but worth knowing:

- **Publishing a module: commit the SOURCE first, the payload second.** The freshness check compares commit
  *recency*, and because the zip is deterministic, re-zipping after a bad order produces identical bytes — so
  there is no new commit available to fix it. `New-ModulePublish.ps1` now refuses to start with uncommitted
  module source.
- **`master` had no upstream tracking**, so a `git checkout master` silently landed on a stale 1.4.4 tree and
  `git pull` errored. It is fixed now, but verify with `git log --oneline origin/master` rather than trusting
  a local branch.

The likeliest next module is **TTS/voice**, and it will immediately hit the audio gap recorded at the top of
BACKLOG: `IHost` has no playback verb at all. Add it *with* that module, per rule 3.

---

## THE HOST CONTRACT: stable, not frozen (read this before touching the ABI)

**There is no freeze. Do not reinstate one.** The host was frozen at 1.4.4 and that rule failed three times in
three days: reopened at 1.4.6 for `ICompanionManager.CompanionsDirectory`, then 1.4.7 for `IHost.IsDarkTheme` and
`IHost.Log`, then 1.4.8. Building **one** module plus the SDK surfaced **three** ABI gaps, which is not a
failure of foresight — it is what building reveals. A freeze would have made all three permanently impossible,
and it had already pushed a real UX defect (a failed module being invisible) into BACKLOG as a "post-freeze
fix" while its only escape route deleted the user's settings.

What you actually want from a freeze is *"a module written today keeps working."* That is delivered by the six
rules below, not by refusing to add anything. Adding is cheap; the rules are what make it safe.

**1. `AssemblyVersion` stays `1.0.0.0`, forever.** It is the binding identity every built module references
(`DesktopAICompanion.Contracts, Version=1.0.0.0`). Move it and every existing module fails to load. `FileVersion`, by
contrast, tracks the product deliberately.

**2. Additive only.** Never remove a member, and never change what one means. This is the *real* permanent
commitment, and it holds whether or not anyone calls it a freeze. Adding a member cannot break an existing
module; removing or redefining one breaks all of them silently.

**3. An ABI change bumps the product version in the same commit.** `DesktopAICompanion.Contracts` stamps its
`FileVersion` from `ProductVersion.props`, and a Windows Installer major upgrade skips refreshing a file whose
version did not change — shipping an ABI change without the bump installs a stale `Contracts.dll` that cannot
resolve the new types (the failure `9009133` fixed).

**4. Never declare an event you do not raise.** `CompanionIdle` and `AnimationStarted` were deleted for exactly
that: a declared-but-silent event is a trap that looks like a feature. Wire the raise in the same change.

**5. Raise `MinHostVersion` only when you actually call a newer member.** `ModuleHost.LoadFrom` enforces it
(`ModuleHostRequirement.IsSatisfied`) *before* `Init`, refusing a too-new module with a legible log line
instead of letting it die at its first missing member. A module declaring a version above the *shipped* host
is refused until that host ships — so publish the host first, then the module (Companion Studio 1.1.0 declares
1.4.6 for this reason, and is why it was published after that release rather than with it).

**6. Do not move a source-linked engine file without re-running the parity self-test.** Companion Studio compiles
the host's own parser/validator/reachability rather than copying them, so a reshuffle under `src/dotNet/`
can silently change its verdict. `--petstudio-selftest` asserts the module's verdict equals
`CompanionXmlValidator`'s on every fixture; that assertion is the guard, not a freeze.

**Two invariants that are about behaviour rather than shape:**

**Previews are invisible to modules.** A transient preview companion (`ICompanionManager.SpawnPreview`) never reaches
`settings.json`, never survives a restart, never appears in the tray's Remove submenu, and never raises
`CompanionSpawned` / `CompanionPoked` / `CompanionLanded`. That rests on one place: `StartUp.DeriveOnScreenMix` skips transient
registry entries, and both `PersistMix` and the tray read it. Anything that must ignore previews should read
that list rather than walking the companion array.

**Deliberate ABI exclusions, so they are not re-litigated.** No "use this companion" verb: it writes the XML into
settings, closes every companion and resets the mix, and the host's own Companions pane owns it. No per-type size, sound
or voice: those are user preferences the Companions pane owns, and a module writing them would fight it with no
arbitration. These are decisions, not gaps — unlike the audio gap in BACKLOG, which is a real one.

**Gates.** `tests\run-gate.ps1` runs the whole local gate in one command and **fails on a skip** — the module
self-tests skip-pass when their folder is absent, so a build that silently produced no modules used to look
identical to a clean run. `tests\runtime-resource-soak.ps1` is the only committed check that can catch a leak
(OS handle/GDI/USER/private-byte growth, sampled from outside the process); it is a pre-tag step, not a CI gate.
Baseline: handles +5, GDI −6, USER −6, private bytes +13.6 MB, all well inside their bounds. It does **not**
cover the Companion Studio window — see the leak-soak method below.

## Current state (2026-08-18)

**Latest public release: `v1.4.8`.** Three releases landed in one day — 1.4.6, 1.4.7, 1.4.8 — each with MSI +
portable ZIP + SHA256SUMS on its GitHub release. **The live catalog serves 3 modules: fortunes 1.1.2,
aibrain 1.1.2, petstudio 1.1.0.** Both catalog paths a user actually takes are verified end to end on a real
install: **installing** Companion Studio from the catalog, and **updating** fortunes/aibrain 1.1.1 → 1.1.2 with the
module's data directory preserved (fortunes kept 155 files including downloaded packs).

What each release added, newest first:

- **1.4.8** — a module that fails to load is no longer invisible: it reports the reason with a non-destructive
  **Reinstall**, and a `MinHostVersion` refusal says "needs a newer app" instead. This release also **attaches
  `DesktopAICompanion.Contracts.nupkg` and `DesktopAICompanion.ModuleKit.nupkg` as release assets**, which is what makes
  writing a module outside this repo possible (see `docs/module-authoring.md`). They are deliberately NOT on
  nuget.org: the contract's package version tracks the product, so publishing would mean a new public package
  on every release even when the ABI is byte-identical.
- **1.4.7** — `IHost.IsDarkTheme` (a module-owned window can match the app; only the host knows whether the
  user's light/dark/**system** choice resolves to dark) and `IHost.Log` (before it, a module's only way to
  report anything was to make the companion *say* it).
- **1.4.6** — Companion Studio 1.1.0 + `ICompanionManager.CompanionsDirectory`, plus the sheep `king_slamB` fix.

1.4.6 in more detail, since it carried the most:

1. **`ICompanionManager.CompanionsDirectory`** — one additive ABI member, so a module can open a file dialog in the user's
   companion library instead of guessing the host's folder layout. This is why the version moved.
2. **Companion Studio 1.1.0** — published to the catalog (it declares `MinHostVersion 1.4.6`). A three-column
   authoring window: an editable XML pane (debounced re-analyze, atomic save) feeding preview/install, a
   colour-coded **reachability map** with clickable legend filters, and a detail panel rendering the selected
   animation's real sprite frames with playback plus its outgoing transitions. Its Open dialog defaults to the
   companion library and remembers the last folder browsed to. Blank (fully transparent) frames and orphaned
   animations now explain themselves rather than looking broken.
3. **The module SDK** — see `docs/module-authoring.md`, which is now the entry point for writing a module:
   - **`src/DesktopAICompanion.ModuleKit`** — the helpers each module had hand-copied (`AtomicFile`,
     `CrossSessionLock`, `EmbeddedResources`, `UnicodeTextProgress`, `ModulePaths`, `JsonSettingsStore<T>`,
     `SelfTestProbe`) plus a `Testing` namespace with the `RecordingHost`/fakes every self-test reinvented.
     **It is not the ABI:** Contracts is `Private="false"` and shared from the host; ModuleKit is referenced
     normally and ships *inside* each module's folder, so modules can move versions independently.
   - **`dotnet new desktop-ai-companion-module`** (`templates/desktop-ai-companion-module`) scaffolds a module that builds and
     passes its own self-test as generated. Guarded against rot by `packaging\Test-ModuleTemplate.ps1`.
   - **`--module-selftest=<id>`** runs any module's own `public static bool SelfTest(out string)` through the
     real loader, so a new module needs **no host edit** to be testable. Absent module = SKIP (which the gate
     treats as failure); no `SelfTest` = FAIL.
   - **`packaging\New-ModulePublish.ps1`** does the whole publish sequence and refuses to regenerate the
     catalog while the zip is uncommitted.

Also in this release: **the seven sheep's orphaned `king_slamB_down`/`king_slamB_up` animations are wired**
(the up/down walks and jumps never slammed onto the opposite surface, unlike base/top). The two
`king_jump_*_flip` animations are left unreachable **on purpose** — base/up jumps already rotate directly, so
those flips were bypassed by design. A sheep therefore still reports 2 unreachable, correctly.

**Leak-soak method for the Companion Studio window** (not committed; `runtime-resource-soak.ps1` cannot reach it).
A throwaway net10 WPF exe referencing the built `PetStudio.dll` + `DesktopAICompanion.Contracts.dll` constructs the
window by reflection with a fake `IHost`, analyzes a companion, selects a node, shows and closes it, and samples
`HandleCount` / `GetGuiResources(GDI,USER)` / `PrivateMemorySize64` from outside. Run **two** segments of 20
cycles: the pass criteria are zero windows still alive as `WeakReference`s after an LOH-compacting GC, flat OS
handles, and **segment 2 barely growing** — the first segment legitimately sets a high private-byte watermark
because the sheep's sprite sheet is large. That last signal is what found the re-decode bug: the debounced
re-analyze was decoding a ~15 MB sheet on every keystroke-settle, now cached on an `<image>` fingerprint.

**Historical — the OCR + module-update work now shipped as `v1.4.2`:** the companion quoted `asÂ®` off the screen.
Root cause was not the
model: `AiBrain.RunOcrAsync` redirected tesseract's stdout without setting `StandardOutputEncoding`, and an
unset encoding is taken from `GetConsoleOutputCP()`, which returns **0** in a GUI process with no console —
.NET then decodes codepage 0 as **CP_ACP**, the system ANSI codepage (1252 here). Tesseract writes UTF-8, so
every non-ASCII glyph on screen entered the prompt as mojibake (`as®`→`asÂ®`, `—`→`â€"`, `’`→`â€™`, `é`→`Ã©`)
and the model quoted the garbage back. Reproduced and fixed at the byte level, then verified through the real
module: `Test OCR` returns ✓ on the live engine. **Windows built-in OCR was never affected** (WinRT strings),
so this only ever hit users who have Tesseract — the reporter's box has it configured.

Three guards now hold it: the probe image in `SelfTestOcrAsync` carries a `®` and the status goes RED on a
mis-decode (a MISSED `®` is not a failure — only a mis-decoded one); `--aibrain-selftest` asserts the psi
factory pins UTF-8 on stdout AND stderr (runs on CI, where no OCR engine exists); and
`tests\runtime-hardening-selftest.ps1` fails repo-wide if any `RedirectStandardOutput` lacks a paired
`StandardOutputEncoding`. That last one was negative-tested against the pre-fix file.

**Why a host release came with it:** the module republish alone could never have reached anyone who already
had AI Brain. `ModulesPaneControl.DiffNew()` diffed the catalog **by id only**, so an installed module vanished
from the list forever, no version was ever compared, and the only route left was Uninstall — which deletes the
module's settings, keys and history. So the pane now offers **"Update to vX.Y.Z"** on an installed row whose
live version is older than the catalog's, and `PendingModuleUpdates` applies it: verified download → unpack to
`<baseDir>\module-staging\<id>.staged` → marker → next launch swaps it in before `ModuleHost.LoadFrom` can lock
anything, **leaving the module's data directory alone**. Staging sits OUTSIDE `modules\` on purpose (the loader
loads every subdirectory it finds, and would have loaded a half-written `aibrain.new` as a module) and under
`BaseDirectory` rather than the data root so the swap is a same-volume `Directory.Move`. The swap moves the old
copy aside and rolls back on failure: deleting first and then failing would leave the user with no module at
all, which is worse than the stale one they were replacing.

**The check also runs itself now, monthly.** `ModuleUpdateSchedule` stores the month a check last *succeeded*
and becomes due when the calendar month moves on, rather than firing on the 1st — a companion that was switched off
that day would otherwise skip the month entirely. Stamped only after a successful fetch (offline costs a retry,
not a month), seeded without checking on a fresh install, skipped with no modules installed, and evaluated two
minutes after launch then six-hourly (a cadence for noticing the month flip, not a polling rate). A hit raises a
tray notification that opens Settings → Modules; nothing self-installs. It is the app's only unprompted network
request, hence a Preferences toggle (default on, absent-in-older-doc reads as on) and a PRIVACY.md paragraph.
The version rule lives in one shared `ModuleUpdateScan` so the pane's button and the notification can't disagree.

**Earlier releases (historical):** `v1.4.1` (2026-08-14, a packaging fix); `v1.4.0` (2026-08-13) fixed the companion
reading its OWN "Sheep"-titled window as screen context (a sheep-joke loop; fixed in `ActiveWindow`) and the
Genres filter being a no-op for downloaded packs. `v1.4.2` (2026-08-14) shipped the OCR mojibake fix + the
module-update path + the monthly auto-check above.

**History was scrubbed (2026-08-13):** a personal work email on the 10 fork-day commits was removed via
`git filter-repo --mailmap` (→ `bigfnj` (personal identity)); master + the v1.2.1/1.2.2/1.2.3 tags were
force-pushed. **Residual:** GitHub's immutable `refs/pull/*/head` refs still hold the old commits — a
force-push can't remove them; fully purging needs a GitHub Support "remove sensitive data" request (in BACKLOG).

**S6p2 (Companions-as-a-module) was built, then FULLY REVERTED (2026-08-14).** The whole stream — an `ICompanionManager`
ABI + CompanionHost bridge, a `modules/Companions` plugin owning the Options→Companions pane + tray, per-row action buttons,
per-type settings, and a per-companion "voice" picker — shipped gated + pushed, but on the live eyeball the user
disliked the module UI (lost tray icons, then the pane itself), so it was reverted to the pre-S6p2 state
(`890f76d`). Design + code are preserved in git history (`feat(s6p2)` commits `53912a6`..`520aada`).
**Lesson: eyeball a UI-heavy direction EARLY, before building four phases of it.**

**Kept from that cycle (genuine, module-independent):** the `DesktopAICompanion.Contracts` **FileVersion now tracks
the product** (`9009133`). It had a fixed `FileVersion=1.0.0.0`, so a Windows Installer major upgrade SKIPPED
refreshing the ABI dll when its content changed but the version didn't — shipping a stale Contracts.dll that
couldn't resolve new ABI types (hit live during the eyeball install). `AssemblyVersion` stays `1.0.0.0` (the
ABI binding version modules reference). **Any future ABI change now refreshes on upgrade.**

**The box** runs the **published `v1.4.8` MSI** (hash-verified against `SHA256SUMS.txt`), with all three
modules installed **through the catalog rather than by hand** — Companion Studio via a fresh install, fortunes and
aibrain via the in-app update to 1.1.2. `DesktopAICompanion.Contracts.dll` refreshed with each upgrade (1.4.6.0 →
1.4.7.0 → 1.4.8.0), which is the FileVersion-tracks-product rule proving itself against real ABI changes.

---

## Big picture (2026-08-12) — historical

**Released as `v1.2.3` (2026-08-12).** Backlog #9 (Fortunes clarity) plus three real bugs it turned up.
Read the two OPEN items at the top of BACKLOG.md's "Bugs & maintenance" before the next release — both
are decisions waiting on the user, not work waiting on a keyboard.

**The one thing to internalise from this session:** `modules-dist/<id>.zip` is a **committed artifact
that the live catalog serves from `master`**, and nothing rebuilds it for you. Merging to master *is*
the module publish — no tag, no release, no upload step. Both modules had silently drifted from their
source, and the drift was invisible because the failure paths are quiet:

- Fortunes shipped with **no built-in corpus at all** (the S3 move dropped the EmbeddedResource from
  the base and the module never picked it up), so a lean install had nothing to say. The lookup failure
  went into `_embeddedError`, which only ever appends to a diagnostics string nothing reads.
- AI Brain shipped a release behind PR #71, so catalog installs had no Windows OCR and therefore no
  screen reading unless the user happened to have Tesseract.

`packaging\Test-ModulePublishFreshness.ps1` now fails CI on that drift. **Practical consequence: any PR
touching `modules/<Id>/` needs a republish commit before CI passes** — rebuild, `New-ModuleDistZip.ps1`,
**commit the zip**, then `New-ContentCatalog.ps1`, in that order, because the catalog hashes the
*committed* blob. Markdown is excluded so a BACKLOG note doesn't demand a 31 MB republish.

Also worth knowing: two self-tests (`SmartFortunes.SelfTest`, `ProgressiveSelfTest`) had sat with **zero
callers** since the same S3d move, and both fail on an empty pool — they would have caught the corpus bug
on day one. If you relocate code between the base and a module, check what stopped being invoked.

**Previously released as `v1.2.2`.** `v1.2.1` bundled the whole net10 migration + plugin re-architecture below
through **S5c/d/e** (base AI-cluster removal, Newtonsoft dropped product-wide, About/Help → themed WPF),
plus the AI provider redesign (local+cloud+fallback), capability-aware model dropdowns with a VRAM-size
hint, and the Personality+Speech-style merge into one curated 26-entry **Disposition** catalog
(`AiSettings` schema v3).

**`v1.2.2` is the S6 release: the app now ships LEAN and features arrive as installable modules.** An
in-app **Options → Modules** catalog (HTTPS + SHA-256-pinned, permissions shown before download, restart
to activate) replaces the original "bundle modules into the installer" plan and absorbs what would have
been S7's signed-catalog/consent stream. On top of it: arbitrated poke reactions with a **Trigger Speech**
picker, fortune-pack browse/download/import, and a grouped+filterable pack picker with curated names for
all 152 packs. **Next up: S6 phase 2** (Companions itself becomes a pre-installed module — needs new `IHost`
spawn/remove verbs) — see BACKLOG.md for the full queue.

1. **`.NET 4.8 → .NET 10 (LTS) migration` — DONE + on `master`.** The app is `net10.0-windows`, SDK-style,
   framework-dependent (needs the .NET 10 Desktop runtime). Version bumped to **1.1.0**. Behavior parity.
2. **Plugin re-architecture — IN PROGRESS.** Turning the monolith into a lean **plugin host**: the base is
   a companion engine + skin downloader, and every capability (sound, fortunes, AI brain) becomes a **module**
   (a separate DLL loaded in its own collectible `AssemblyLoadContext`) that subscribes to lifecycle events
   and contributes UI. Planned as streams **S1–S7**. Modules are **NOT in the installer yet** (that's S6) —
   they build into the runtime `modules\<id>\` folder for local runs + self-tests only.

### Re-architecture status
- **S1 — plugin host foundation (MERGED, PR #2):** `DesktopAICompanion.Contracts` ABI (`IModule`/`IHost`/`ICompanion` +
  lifecycle events + host services + declarative options schema + tray contributions); the `ModuleHost`
  loader (per-module collectible ALC, shares the single `DesktopAICompanion.Contracts` from the default context so
  types unify); the live `CompanionHost` bridge (StartUp raises spawn/poke/land/shutdown at the real hook points).
- **S2 — Sound module (MERGED, PR #3):** NAudio left the base entirely (csproj + payload manifest + lock).
  The base parses `<sound>`, carries the raw MP3 bytes, and raises `AnimationStarted` with them; the
  `modules/Sound` plugin decodes + plays via NAudio **in its own load context**. `--sound-selftest`.
  **(Superseded: the S2 Sound module was RETIRED in B4 — the base owns audio playback now; see the "B" audio arc below.)**
- **S3 part 1 — Fortunes module boundary + welcome starter (MERGED, PR #4):** `modules/Fortunes` (id
  `fortunes`). On the first companion spawn it speaks a **personalized welcome** — a sheep-themed line with the
  **Windows username** (`Environment.UserName`) filled into a `{name}` slot; the 116-line `welcome.json` is
  adapted from ai-platform's DeskPet welcome quips. `--fortunes-selftest`.
- **S3 — Fortunes fully extracted (MERGED, PRs #4/#5/#6, `db0d6dd`).** The engine (`FortuneProvider` /
  `FortuneFileImporter` / `SmartFortunes` / `Embedder`) lives in `modules/Fortunes/engine/`; the module is
  the live fortune source and the base is **ONNX-free**. Residual in the base: the *dumb* `FortuneProvider`
  + corpus + the disconnected fortunes Options tab (retired in S5).
- **S4 — AI-brain module (MERGED, PR #7).** The optional
  screen-commentary LLM now lives entirely in `modules/AiBrain` and OWNS the ask/hotkey/idle/drop/emote flow
  through host services; the base is runtime-disconnected (drop → arbitrated tick; `ApplyAiBrainState`
  neutered; AI tray items removed). OFF by default; reachable via its own setting/hotkey until the S5 UI
  rebuild (accept-the-gap). Two additive ABI additions: `IHost.PlayAnimationAll` +
  `ScreenContext.WindowUnderCompanion`; the real global-hotkey registrar now lives in `CompanionHost`. A non-destructive
  migrator copies the base `ai-settings.json` (incl. DPAPI keys) into the module store on first run.
  **Deferred to S5 (like S3d deferred the fortune UI/engine):** deleting the 8 base AI-brain files, removing
  the FormOptions AI tab, and trimming the SecuritySelfTest AI tests — they're entangled with `AiSettings`'
  DPAPI credential machinery, so they're cut with the AiSettings split + WPF Options rebuild. `--aibrain-selftest`.
- **S5 — WPF shell + Companions features (MERGED, PRs #8-21).** The WPF module-manager window
  (`src/Portable/Wpf/OptionsWindow.cs` + `OptionsShell.cs`, shown from the WinForms UI thread) with host-built
  **Preferences** + **Companions** panes and each module's schema pane; the tray merges module contributions. Companions
  features: enriched cards (unique quips + "N animations · M sounds"), all 22 companions bundled (dev + ZIP; MSI
  bundling deferred to S6), a **"Check for new companions"** online button, per-companion **size** (inline clickable
  1/2/3), and per-companion **sound** on/off. Window default **1050×820** (Companions 3-across), OS-following **theme**
  (light/dark/system, no visible toggle), mouse-wheel scroll fix, dark scrollbar.
- **"B" audio arc — Option B host-owned audio (MERGED, PRs #22-25); user-confirmed audible.** The base OWNS
  playback now via `src/dotNet/AudioOutput.cs` — one shared mixer + **DirectSound** output; companion MP3s decoded
  once (ACM/OS codec, no shipped native) + cached; per-sound volume + overlap; graceful no-device. The engine
  `<sound>` path (`Animations.SoundSink`, now `(petTypeId,animId,data,loop)`) plays directly. A **device
  picker + Test-sound button** in Preferences route to any playback device (`DirectSoundOut.Devices`; setting
  `audioDeviceId`). **NAudio is back in the base** (3.0.0-preview.6: Core/WinMM/Dmo + transitive Midi +
  System.Numerics.Tensors) — **WASAPI was rejected** (its pkg needs a `net10.0-windows10.0.19041` TFM that
  drags `Microsoft.Windows.SDK.NET.dll` ~25 MB into the payload). The **S2 Sound module was RETIRED** in B4
  (inert once the base owned playback). **TTS is a backlogged future module** on this shared output.
- **NEXT — entangled, plan before building:** **S5b-2(d) Fortunes pane**, then **S5c/d/e** — the
  **AiSettings split**, delete the residual base fortune/AI code +
  Options tabs, Newtonsoft→System.Text.Json. The Fortunes module contributes **no** pane yet and the base's
  `FortuneProvider` is residual/disconnected, so these overlap. Then **S6** (bare host + package first-party
  modules into the installer, MSI-bundles-companions, migration, **2.0.0**) and **S7** (signed catalog + consent).
  (Already done: **FormOptions / FortunesWebView + WebView2 are retired**, and **About/Help are now themed
  WPF windows** — the companion engine (FormCompanion/FormSpeech) + the dev-only FormDebug console are the only WinForms left.)
- **Open follow-ups:** (a) per-companion size + sound key the ACTIVE/default companion as `""`, so a companion's card toggle
  doesn't bite while it's the *active* one — key the active companion by its real id (shared fix for both). (b) The
  schema panes (Preferences / AI Brain) aren't columnized for the wide window — awaiting the user's read on
  whether they feel too empty. (c) Optional theme polish: live re-theme on Apply (currently applies on reopen)
  + a dark ComboBox dropdown template.

### Locked design decisions
- **Fortunes module ships the ENGINE, not the content.** Both dumb (random) + smart (ONNX/bge-small) live
  in the module; it bundles **no fortune packs**. A fresh module is silent except the personalized welcome
  until the user adds a pack. The ~486KB `fortunes.txt` becomes the importable/downloadable "starter pack"
  (S7 catalog). The bge-small ONNX model is *engine* and travels with the module (like NAudio for Sound).
- **Deployment:** framework-dependent (.NET 10 runtime prompt). **Ecosystem:** open third-party modules,
  gated by code-signing + consent (S7). **Host UI:** native WPF (WebView2 dropped in S5). **Editions:** bare
  host only. **Stream 3 (post-S7)** = a module ecosystem (SDK/template + docs + in-app marketplace).
- Working model: per-phase branch → **local self-test verification** → merge (user authorized *"commit and
  merge as you go"* while GitHub Actions was globally down). **No reinstall/release without explicit go-ahead.**

## Extraction pattern — expand/contract (S3 + S4)

Both feature extractions use **expand/contract**: copy the engine into the module + rebind to the ABI
(dormant, base untouched) → flip the module live + disconnect the base → delete the dead base code. Rebind
template (reused for both): `AppPaths`→a module path provider (host-storage-backed), settings→a module
settings class, `AtomicFile`+`CrossSessionLock`+`UnicodeTextProgress` copied into module helper files,
logging→no-op, screen context→`host.CaptureScreenContext`.

- **S3 (fortunes) — DONE + MERGED.** Engine relocated (dumb + smart + native-`onnxruntime.dll`-in-ALC), flipped
  live, base ONNX-free. The load-bearing detail: native `onnxruntime.dll` in a plugin ALC (see the gotcha below).
- **S4 (AI brain) — functional flip DONE (branch `stream2/s4-aibrain`).** Engine relocated to
  `modules/AiBrain/engine/` (AiBrain/AiSessionManager/backends/ChatHistory/Personas/AiEndpointPolicy/settings),
  rebound to the ABI (`AiPaths`, `ScreenContext`, module Newtonsoft dep). The module owns AI live; the base is
  runtime-disconnected. The "contract" step (delete the 8 base AI files, remove the FormOptions AI tab, trim
  the SecuritySelfTest AI tests) is **deferred to S5** because those consumers are entangled with `AiSettings`'
  DPAPI credential machinery (which needs `Personas`/`AiEndpointPolicy`/`AiProviders` until the AiSettings
  split). So the base still compiles + its AI defensive tests still pass — it just never runs the brain.

The precise rebind detail is in the `project-desktoppet` memory note.

## Build / verify / release

- **Build:** `pwsh build.ps1 -Release [-Zip]` → base + all modules into `build\DesktopAICompanionPortable\bin\
  Release\x64\` (modules under `modules\<id>\`). `installer\build-installer.ps1 -Config Release` → MSI (WiX
  5.0.2). Root `global.json` accepts any installed **.NET 10.x** SDK (`version 10.0.100` + `rollForward
  latestMinor` — relaxed from the old exact 10.0.201 pin after that SDK was uninstalled here, leaving only
  10.0.302; CI still sets up 10.0.201 via setup-dotnet, so it keeps using that).
- **Self-tests:** the app takes `--*-selftest` flags (in-process, no external host), e.g.
  `--module-host-selftest`, `--fortunes-selftest`, `--fortunes-engine-selftest`, `--wpf-options-selftest`,
  `--security-selftest`, `--hardening-selftest`, `--fortunecache-selftest`, … (`--sound-selftest` was removed
  when the Sound module was retired in B4). The smart-engine flags went with the S3d move to the Fortunes
  module and **left their tests with no callers at all** — `SmartFortunes.SelfTest` now runs inside
  `--fortunes-engine-selftest`, and the slow cold-cache one came back as
  **`--fortunes-smart-progress-selftest`** (~18s; CI runs it, the local default loop does not).
  **`build.yml` is the source of truth for the current set**; CI runs the flag loop +
  `runtime-hardening-selftest.ps1` + `packaging\Test-ModulePublishFreshness.ps1` + MSI.
- **Resource-churn soak** (`--resource-churn-selftest`): **REQUIRES** env `DESKTOP_AI_COMPANION_DATA_ROOT` = an
  absolute dir under `%TEMP%\DesktopAICompanion-ResourceSoak-*` (else it exits 2); tune with
  `DESKTOPPET_RESOURCE_CHURN_CYCLES` / `_MIN_DURATION_MS`. Run it via `Start-Process -Wait -PassThru` and
  read `.ExitCode` — **a `| tail` pipe masks the exe's exit code** (this bit me: a stale result file read
  as PASS). Result JSON lands in the data-root dir.
- **Releasing** (when asked): bump `ProductVersion.props` (**both** `DesktopAICompanionVersion` and
  `DesktopAICompanionAssemblyVersion`; `publish-release.yml` verifies the tag matches), push a `vX.Y.Z` tag →
  `release.yml` publishes the unsigned portable ZIP + MSI + `SHA256SUMS`. Fully automated: nothing is
  built or uploaded by hand. See `docs/RELEASE-CHECKLIST.md`.
- **Tagging will fight you**: upstream tagged **v1.2.3–v1.3.2** in 2019-21 and those refs are in any clone
  with `upstream` as a remote, so `git tag v1.2.3` fails as "already exists". `origin` has none of them.
  Delete the stale local ref and re-tag (reversible via `git fetch upstream --tags`). See the OPEN backlog
  item — the durable fix is to move our series past v1.3.2.
- **Modules do NOT ship with releases.** They are served from `master` via
  `raw.githubusercontent.com/bigfnj/desktop-ai-companion/master/modules-dist/` + `catalog.json`, so **merging to
  master publishes them to every existing user immediately**, independent of any tag. Same for companions and
  packs. Treat a merge that touches `modules-dist/` as a publish.

## Durable gotchas

- **Installed process = `DesktopAICompanion`** (older dev builds = `eSheep`). Kill with
  `Get-Process -Name eSheep,DesktopAICompanion -ErrorAction SilentlyContinue | Stop-Process -Force` (never
  `-ErrorAction Stop` — it throws on the missing name and leaves the exe locked → MSB3027).
- **Where code lives:** engine `src/dotNet/*`, tray UI `src/Portable/*`, plugin host `src/dotNet/Plugins/*`,
  ABI `src/DesktopAICompanion.Contracts/*`, modules `modules/<Name>/`. New base `.cs` must be added to
  `src/DesktopAICompanion_Portable.csproj` (`<Compile Include>`; `EnableDefaultItems=false`). Modules use SDK
  default globbing.
- **Modules keep the host's contract:** a module references `DesktopAICompanion.Contracts` with `Private="false"`
  so it binds the host's single shared copy (the loader's `Load` returns null for it → default context). A
  module with its own NuGet deps needs `<GenerateDependencyFile>true</GenerateDependencyFile>` +
  `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` so the dep dlls land beside it (a
  *library* project doesn't copy NuGet deps to output by default — that's why Sound needed it and the
  contract-only TestModule didn't).
- **Native deps in a module ALC (onnxruntime):** the loader's `ModuleLoadContext` overrides
  `LoadUnmanagedDll` — it resolves via the module's deps.json (`_resolver.ResolveUnmanagedDllToPath`, with an
  existence check) and then **falls back to probing the module's own folder**. That fallback is essential:
  the onnxruntime NuGet build targets **flatten** the native `onnxruntime.dll` beside the module dll instead
  of under `runtimes\win-x64\native\` (even though deps.json still points there), and it must resolve on an
  installed machine that has no NuGet cache. The Fortunes module pins `win-x64` (framework-dependent) to pull
  the native assets. NAudio was pure-managed and never needed any of this.
- **`AnimationInfo.Companion` is null on the engine-raised sound path** (the shared per-type `Animations` engine
  has no per-companion identity; sound is global). Real per-companion identity is future work S4's AI reactions want.
- **`net10-windows` in-box packages:** ConfigurationManager / ProtectedData (DPAPI) / System.Drawing /
  System.Text.Json are provided by the Windows Desktop framework — do NOT add them as PackageReferences
  (NU1510, and `TreatWarningsAsErrors` makes it fatal). `GenerateAssemblyInfo=false` strips the SDK platform
  attribute → add `[assembly: SupportedOSPlatform("windows7.0")]` to avoid CA1416 spam.
- **The active companion is persisted as its raw `animations.xml`** (not an id); downloaded companions read via
  `UTF8.GetString`, so a leading BOM survives — `CompanionXmlValidator.TryParse` strips it.
- `TreatWarningsAsErrors=true` — a build failure is often just a newly-orphaned member; the compiler points
  right at it. `src/packages/*` are untracked net48-era NuGet leftovers (the SDK build uses the global
  cache) — ignore them; a future cleanup could delete them.
- **CI note (2026-08-06):** GitHub Actions was globally down; S2, S3.1, and the S3c engine relocation were
  merged on the strength of the full local self-test suite + the resource-churn soak. Re-run CI on `master`
  once Actions is back to confirm green.
