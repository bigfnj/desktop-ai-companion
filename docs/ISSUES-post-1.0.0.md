# Bug post-mortems from v1.0.0 on

The four numbered bugs found after the v1.0.0 rebase, extracted from `BACKLOG.md` so that file can
hold only open work. **All four are fixed**, so nothing here is a work item —
[`../BACKLOG.md`](../BACKLOG.md) is the backlog. The one-line "none open" summary and the
fixed-in table live in [`DESIGN-REGISTER.md`](DESIGN-REGISTER.md), which is where a reader goes to
check whether a bug number is taken; the post-mortems themselves stay here. The pre-1.0.0 equivalent
is [`ISSUES-pre-1.0.0.md`](ISSUES-pre-1.0.0.md).

**The diagnosis text is kept in full, including the parts that turned out to be WRONG** — the
strikethrough in BUG-001 included. Two of them were wrong in instructive ways: BUG-003(a)'s suspected
cause was refuted by measurement, and BUG-001's mechanism was mis-attributed once before it was traced
properly. BUG-001's own entry says why that is preserved rather than tidied away:

> Recorded rather than deleted, because the wrong turn is the lesson: the log line that "proved" the
> app was doing everything right (`success=True`) was the thing that was broken, and two
> investigations in a row built theories on top of it instead of questioning it.

Each entry ends with what was actually changed and how it was verified.

BUG-001 to BUG-004 are cited by number from code comments in `modules/AiBrain/`, `modules/PetStudio/`
and `src/dotNet/`, from [`RELEASE-CHECKLIST.md`](RELEASE-CHECKLIST.md), from
[`../handoff.md`](../handoff.md) and from `.github/workflows/build.yml`. The numbers are never reused;
the next bug filed in `BACKLOG.md` is BUG-006.

| | |
|---|---|
| Bugs | BUG-001 to BUG-004 |
| Found | 2026-09-10 — BUG-001 to BUG-003 by the maintainer using the shipped build, BUG-004 by the release checklist's own leak soak |
| All fixed by | the v1.1.0 tag (2026-09-10); BUG-001 took host 1.1.1 → 1.1.3 |

---

## 🐞 Known bugs (post-1.0.0)

Numbered so they can be cited. BUG-001 to BUG-003 were found by the maintainer using the shipped build,
not by a gate; BUG-004 was found by the release checklist's own leak soak.

**All four were fixed before the v1.1.0 tag (2026-09-10).** The diagnosis text is kept in full below,
including the parts that turned out to be WRONG, because two of them were wrong in instructive ways: the
suspected cause of BUG-003(a) was refuted by measurement, and BUG-001's mechanism was mis-attributed once
before being traced properly. Each entry ends with what was actually changed and how it was verified.

### BUG-005 — a converted companion stutters: the same short animation replayed, or two frames held for eleven seconds

| | |
|---|---|
| Bugs | BUG-005(a) the self-looping performance, BUG-005(b) the inflated performance dwell |
| Found | 2026-09-22, by the maintainer watching the shipped build. (b) was reported AFTER (a) was fixed and read as "the fix didn't work" |
| Fixed by | host-side emitter change plus the `reloop` migration; 25 animations for (a), 85 for (b), across 26 of the 31 converted companions |

**One root cause with three independent sites.** A Shimeji action DECLARES its intent:
`Type="Move"` is travel, `"Stay"` is a hold, `"Animate"` is a performance played through once.
`PetEmitter.IsRestingPose` read that attribute and its own comment states the principle outright:
"Type is the right discriminator because it is the source's own statement of intent." Three
neighbours inferred it from VELOCITY instead, and each produced a different visible defect.

**(a) `IsLocomotion` judged travel by velocity alone.** A trip moves the companion 8px a frame along
the ground, so it was indistinguishable from a walk and was handed the walk's edge set: "65% keep
going, 35% re-decide". That is 2.9 plays on average and five or more in 18% of runs. Reported as
Rick move #15 and Hornet move #19, and confirmed against the INSTALLED artefact (Hornet's `Tripping`
is animation id 19) rather than only the repo. Fixed by reading Type first; the change can only ever
REMOVE the locomotion classification, never grant it, so the climbs and grabs kept their loops.

**(b) `restsplit` inflated a performance into an idle dwell.** `Bouncing` re-entered nothing — its
single edge already went to the hub — and still played two frames at a flat 160ms with
`repeat="33"`, about eleven seconds. The format 0.6 → 0.7 migration decided what counted as a
lingering "performance" by velocity and hub-reachability, so a stationary Animate got a 9-12s rest
budget. The source says 2 poses of 4 ticks: a 320ms one-shot.

**The third site is a TEST, and it would have blocked the fix.** The dwell assertion in
`EmitterSelfTest` selected "idle rest" by zero velocity and required 9-12s from it. Adding a
`Type="Move"` fixture that never moves made it fail immediately. It now asks the parsed config for
the source Type and only skips when the source positively says otherwise, so no existing coverage
was lost.

**Two diagnoses of mine were WRONG and are kept here, because both changed a conclusion.**

- I filed the ten self-looping `jump` animations as a jump-detection bug and called it the next thing
  worth doing. Measured across all twenty `jump`/`jump_down` animations: not one rises. `jump` is
  `vy0 = 0` travelling -10px per frame. `Launches()` had nothing to detect, and the likely cause is
  the documented flattening of a too-weak rise. Retracted the same day.
- I reported that the Japanese and English stock confs disagree about `転ぶ`, the Japanese Tripping,
  because a regex census read its type as `固定`. The engine's own vocabulary table maps `固定` to
  **Animate**, not Stay. The confs agree. This is why the migration parses confs with
  `ShimejiParser.ParseActionsXml` and not a regex: the same switch also revealed that a regex counts
  nested `<Action>` references inside `Sequence` blocks, which is what made `jump` look disputed.

**Why a migration and not a re-conversion, with the number that settles it.** The source archives
existed but re-converting would regenerate identical pixels and silently discard hand edits, which
`rejump`'s comment had already warned about. After migrating, **28 of Hornet's 32 animations agree
exactly with a fresh conversion**; of the four that differ, two differ only in frame count with
identical repeat (its hand-edited `fall` / `Grapple3` swap) and one is a renamed action, meaning the
archive on disk is a slightly different revision of the skin than the one originally converted.

**Verification.** Mutation tested 4/4 FIRED against a green baseline with the built artefact's
timestamp asserted to advance: removing the Type check names `Stumble`, removing the velocity check
names `Brace`, restoring the idle-dwell path names `Bounce`, and weakening the fixture-count guard
reports that the rule is not being tested across both the travelling and stationary kinds. That last
case exists because the new whole-graph assertion was a ONE-case test until a stationary `Bounce` was
added beside the travelling `Stumble` — a fix handling only the moving kind would have passed. The
migration is idempotent by measurement, not by claim: a second run reports `changed 0`. Verified in
the real installed app, and against the bytes raw.githubusercontent actually serves.

---

### BUG-001 — the tray icon is missing after an MSI install that launches the app

**Reproduces every time on a fresh install.** Run the MSI, leave "launch when finished" ticked, and the
notification-area icon is **not present**. Restarting the app puts it there. This is the intermittent
fault the diagnostic log was built for in Phase 4b; it is now a reliable repro, which it never was before.

**What the evidence rules out.** The app is not failing to create the icon and is not being overruled by
the user's shell preference:

```
Tray  tray icon set: success=True icon=True visible=True text='Desktop AI Companion'
Tray  tray icon: visibility left as the user set it
```

`Shell_NotifyIcon` reported success, the icon object was non-null, and `HKCU\Control Panel\NotifyIconSettings` holds `IsPromoted=1` for the exact installed executable path — so
`TrayPromotion.ShouldPromote` correctly declined to touch an explicit user choice. The process is alive
and responding, and the installed binary is the expected one. Everything the app controls is right.

**Most likely mechanism.** The MSI launches the app from an **immediate** custom action, so the process
is started by `msiexec` rather than by the shell, and `NIM_ADD` lands while the taskbar is not in a state
to keep it. `Shell_NotifyIcon` returning TRUE does not guarantee the shell retained the icon. The classic
fix is to handle the `TaskbarCreated` registered window message and re-add the icon when it arrives —
which also covers an Explorer restart, a case this build does not handle either. Worth checking whether
`ProcessIcon` subscribes to it at all before designing anything more elaborate.

**MECHANISM ~~CONFIRMED~~ WRONG (2026-09-10, first attempt).** This entry previously claimed the
cause was the terminate path: that `util:CloseApplication`'s `TerminateProcess="1"` force-killed the
app, the `using` around `ProcessIcon` never unwound, `NIM_DELETE` was never sent, and the shell was
left holding a slot for a dead owner. **That was reasoned, not measured, and the maintainer refuted it
in one sentence:** they closed the sheep *manually* from the tray, then installed the release, and the
icon was still missing. A manual close runs `KillSheeps(true)`, which disposes the icon as its FIRST
action, so `NIM_DELETE` was sent and no slot was stale -- and with no process running, the installer's
`CloseApplication` never fired at all. Neither half of the terminate theory was in play.

Recorded rather than deleted, because the wrong turn is the lesson: the log line that "proved" the app
was doing everything right (`success=True`) was the thing that was broken, and two investigations in a
row built theories on top of it instead of questioning it.

**ROOT CAUSE, measured 2026-09-10 on the live installed build.** The `NIM_ADD` is dropped by the shell,
and the app cannot tell:

- `HKCU\Control Panel\NotifyIconSettings` for the exact installed exe held `IsPromoted=1`, so the shell
  was being told to show it. Promotion was never the problem.
- Greenshot, installed to the same `%LOCALAPPDATA%\Programs` root with the same `UID=1`,
  `IsPromoted=1`, `InitialTooltip` and `IconSnapshot`, was **visible**. So the registry entry is not the
  differentiator; the difference is at runtime.
- A UI Automation walk of `Shell_TrayWnd` found 39 buttons and **no companion icon**.
- Sending the shipped `TaskbarCreated` handler to the running process made it **appear immediately** --
  40 buttons.

So the icon was genuinely never in the shell, and a re-add fixed it. The reason nothing caught this:
`ProcessIcon.SetIcon` sets its `success` flag false ONLY when the `try` block throws. It never captured
`Shell_NotifyIcon`'s return value, and `NotifyIcon.Visible = true` does not report whether the shell
accepted the add. **A dropped add and a working one logged byte-identical lines.**

**It is INTERMITTENT, and the entry's "reproduces every time" was also wrong.** Measured across three
starts of the same binary: one msiexec-launched start dropped it, a direct launch was fine, and a later
msiexec-launched start -- real installer, licence accepted, "Launch" ticked, app parented to msiexec --
logged `shellHasIt=True` on the first add. The launch method correlates but does not determine it.

#### FIXED in host 1.1.1 (2026-09-10) -- verify the add, and repair it

**`TrayIconPresence` asks the shell the question the app could not.**
`Shell_NotifyIcon(NIM_MODIFY)` returns FALSE when the shell holds no icon for a given
`(hWnd, uID)`. WinForms keeps both private, so they are read by reflection (`_window`, `_id`), cached
once, and reported as **unknown** rather than guessed at if a future runtime renames them. The
primitive was validated BEFORE anything was built on it -- shown icon answers True, hidden icon
answers False -- because a check that always succeeds would have been worse than no check.

**`ProcessIcon` now verifies after startup and repairs.** A backed-off schedule
(1.5s, 3s, 6s, 12s, 20s) re-adds the icon on a definite "absent", and deliberately:

- **never repairs on `unknown`** -- that would re-add on every tick of every healthy run, which is the
  failure mode of a blind retry loop;
- **does not stop at the first success** -- the shell can accept an icon at 1.5s and drop it at 6s
  while it is still settling after an install, so the whole schedule runs. Five `NIM_MODIFY` calls
  cost nothing measurable and a healthy run logs nothing at all.

**The log line is now honest**, which matters more than the repair: it reads
`noThrow=... shellHasIt=True|False|unknown` instead of a `success=True` that only ever meant "no
exception was thrown". That single word is what hid this bug through two investigations.

**Proven by FAULT INJECTION, not by waiting for a bad run.** Since the natural failure is
intermittent, the exact broken state is manufactured instead: `NIM_DELETE` behind WinForms' back
leaves the shell holding no icon while the app still believes it is shown -- precisely what a dropped
`NIM_ADD` leaves. Verified live, from outside the process, against the real app:

```
INJECT: delete the icon behind the app's back
  icon in tray: False        <- fault took hold
wait for the app to notice (schedule runs to ~22s)
  icon in tray: True         <- repaired itself
```

**Verification:** `--traywatcher-selftest`, 30 assertions, wired into `tests\run-gate.ps1` and
`build.yml`. The chain that matters: `WinForms still believes the icon is visible` (the blind spot) ->
`the check DETECTS the dropped icon` -> `the repair puts the icon BACK in the shell`. Also asserts the
reflection seam still exists, so a future .NET rename fails loudly instead of silently disabling the
fix.

#### CONFIRMED IN THE WILD, and re-tuned in 1.1.2 (2026-09-10)

The maintainer installed v1.1.1 fresh from GitHub with "Launch" ticked. **The fault reproduced, and
for the first time the instrumentation caught it:**

```
12:26:58.642  tray icon set: ... shellHasIt=False        <- the initial add WAS dropped
12:26:59.858  tray icon MISSING from the shell (check 1); re-adding
12:27:02.863  tray icon MISSING from the shell (check 2); re-adding
12:27:08.863  tray icon MISSING from the shell (check 3); re-adding
12:27:20.862  tray icon MISSING from the shell (check 4); re-adding
12:27:40.878  tray icon recovered after 4 repair attempt(s)
```

This settles the diagnosis by direct measurement rather than inference: `shellHasIt=False` on the very
first add, four rejected re-adds, then recovery. The icon the user saw was put there by the repair.

**Two defects the capture exposed, both fixed in 1.1.2:**

1. **The refusal window is far longer than assumed, and recovery landed on the LAST attempt.** The
   shell refused at 1.5s, 3s, 6s, 12s and 20s; the icon only returned on the check after that, about
   42 seconds after start. The original five-step schedule ended at exactly that point, so a slightly
   more stubborn shell would have exhausted it and left the user with no icon and a "giving up" line.
   `MaximumAttempts` is now 9, running out to roughly 2.7 minutes. The cost on a healthy machine is
   four extra `NIM_MODIFY` calls and no log output.

2. **The re-add line misattributed its own cause.** All four repairs logged "tray icon re-added after
   TaskbarCreated" when no shell restart had occurred -- the presence check had triggered them.
   `ReassertIcon` now takes a reason and logs it (`presence check 1`, `TaskbarCreated`, ...). Worth
   calling out rather than quietly fixing: a log line that misstates why it fired is the same defect
   class as the `success=True` line that hid this bug for two sessions, and it was introduced by the
   fix for that very bug.

**What this does NOT establish.** The trigger is still unexplained. The launch method correlates
(msiexec-launched starts are where it has been seen) but does not determine it -- an earlier
msiexec-launched start accepted the icon first try. The fix is deliberately mechanism-agnostic: it
verifies and repairs regardless of why the shell refused, which is why it worked here without the
cause being known.

#### The INSTALLER side, fixed in 1.1.3 (2026-09-10) -- and it was never WM_CLOSE

Testing 1.1.2 on a real upgrade surfaced a second, separate symptom the maintainer had seen before:

> "The setup was unable to automatically close all requested applications." ... "it appears to hang when
> its done, but eventually closes."

**The 1.1.0 belt aimed at the wrong message.** An MSI verbose log settles who closes the app during an
install:

```
13:52:23.379  RESTART MANAGER: Will attempt to shut down and restart applications in no UI modes.
13:52:23.534  RESTART MANAGER: Successfully shut down all applications that held files in use.
13:52:24.079  Doing action: Wix4CloseApplications_X64      <- WiX runs half a second LATER
```

**Restart Manager gets there first**, and RM speaks the SESSION-END protocol
(`WM_QUERYENDSESSION` / `WM_ENDSESSION`) -- never `WM_CLOSE`. So the `util:CloseApplication` belt added
in 1.1.0 was listening for a message no installer sends, and the app was always force-killed by
`TerminateProcess`. Its self-test passed throughout, because it proved the WIRING (send WM_CLOSE, get an
orderly exit) and never that the installer would send that message.

The backlog already had the other half of the answer and it went unused: WinForms *does* answer
`WM_QUERYENDSESSION`, but on a hidden broadcast window on a BACKGROUND thread that owns no forms, so it
agrees and nothing shuts down. The `TaskbarWatcher` is top-level and on the UI thread, which is why the
message can be acted on there.

**Two rounds were needed, and the first was wrong in an instructive way.** Handling
`WM_QUERYENDSESSION` by answering TRUE and waiting for `WM_ENDSESSION` produced this, measured on a real
repair:

```
14:08:47.298  session end queried (installer or shutdown); agreeing to close
              ... and nothing further. WM_ENDSESSION never arrived.
```

RM took "yes" as an undertaking to exit, waited, and terminated the process. **Answering yes and then
doing nothing is worse than never answering**, because RM believes it has an agreement, and that wait is
exactly the "hang" the maintainer described. The app now exits on the QUERY, posted through the UI
synchronization context so the answer reaches RM before the message loop stops. Measured after:

```
14:14:02.822  session end queried (installer or shutdown); agreeing to close
14:14:02.823  session end: removing the tray icon and exiting immediately
```

One millisecond apart; a direct probe measured the whole exit at **0.29s**, with the tray icon gone
afterwards, i.e. `NIM_DELETE` sent rather than a slot left behind. Deliberately NOT `KillSheeps(true)`:
that path lingers about a second for the farewell animations, which is charming when the user chose to
quit and fatal against RM's deadline.

**A repair now relaunches the pet.** Reported twice: "the sheep again did not re-launch after a repair".
The launch was gated on `NOT Installed`, true for any maintenance run, and a repair is the case where it
matters most -- RM closes the app so the files can be replaced, so the old condition left the user with
no companion, no tray icon, and nothing to show the repair had finished: strictly worse off than before
they started. Now gated on `REMOVE<>"ALL"`, which still never launches an exe it has just deleted. Two
surface assertions pin both halves.

**VERIFIED END TO END on 1.1.3 (2026-09-10).** Maintainer ran a real repair: no "unable to close"
dialog, no hang, and the pet came back on its own. The log confirms the incoming 1.1.3 instance took its
icon on the first add (`shellHasIt=True`), and the outgoing one shut itself down cleanly one millisecond
after agreeing to. **BUG-001 is closed**, across all four of its symptoms: the dropped tray icon, the
retry schedule that recovered on its last attempt, the Restart Manager dialog with its hang, and the
repair that left the user with nothing.

Every one of those four was found by the maintainer running a real install, and none by a gate. The
instrumentation is the reason each was diagnosable rather than merely reportable, and the sequence
1.1.0 -> 1.1.3 is a record of what happens when a fix is reasoned about instead of measured.

#### The two 1.1.0 belts, kept -- but they are NOT what fixes this

Written for the refuted terminate-path theory, and retained because each covers a real case this
does not:

- **`TaskbarCreated`** re-adds the icon when the shell rebuilds its notification area, which covers an
  Explorer restart. That was a genuine second defect: the message was not observed anywhere in the
  codebase before 1.1.0.
- **`WM_CLOSE` -> orderly exit** routes the installer's close request into `KillSheeps(true)`, the same
  path the tray menu uses, so the icon is torn down on the way out. Needed no `.wxs` change, because
  `util:CloseApplication` already posts `WM_CLOSE` and nothing had ever turned it into a shutdown.
  Verified live this session: posting `WM_CLOSE` to the watcher exits the app cleanly.
  `TerminateProcess="1"` stays as the backstop for a wedged process and for the first upgrade hop,
  where the exe being closed is the old one without the handler.

### BUG-002 — the vision feature does nothing, silently, when the configured model is not installed

**Repro.** Set the vision model to something not present in Ollama (the shipped default `gemma3:4b` is
exactly this on a machine that has only Gemma 4), then trigger a screen reaction. The thinking dots
appear and then nothing happens, forever, with no error and no log line.

**Root cause, confirmed.** `AiSettings.cs:86` defaults `VisionModel = "gemma3:4b"`, and
`AiBrain.cs:136` falls back to the same string. When that model is absent the backend errors, and
`AiBrain.AskAboutScreenAsync` ends in a bare `catch { return null; }` at `AiBrain.cs:257-262`, commented
"never crash the app over the AI layer". The caller then "simply stays silent without special-casing
exceptions", exactly as the method's own summary says. So a missing model is indistinguishable from
"nothing interesting to say".

**Why it "worked once".** Maintainer report, 2026-09-10: using **Refresh local models** removed
`gemma3:4b` from the list. So the setting can hold a model that was valid when chosen and is silently
no longer offered afterwards, which matches the observed sequence exactly (worked, changed model to
smoke-test, changed back, never worked again). That makes the real defect broader than a bad default:
**a saved model id is never re-validated against what the backend currently has.** A refresh that drops
a model should say so, or clear the setting, rather than leaving it pointing at nothing.

**The part that makes it undiagnosable:** modules reach the log through `IHost.Log`, and all of
`modules/AiBrain/` contains **one** such call. The log added in Phase 4b to make invisible faults
visible has effectively no instrumentation in the one subsystem that fails by returning null on
purpose. **That was true when this was written and is no longer:** AI Brain is fully instrumented as
of 2026-09-11 (27 diagnostic lines, routed through a static `LogSink`), and what remains of that work
item — Fortunes, PetStudio and BlinkingLed, all still at zero — is "instrument the three modules
still at zero" in [`../BACKLOG.md`](../BACKLOG.md).

**Fix, in order of value:**

1. Log the swallowed failure. Keep returning null, but record the model, the endpoint and the error
   category first. Nothing else here is diagnosable until this exists.
2. Say something the user can act on when the configured model is not in the backend's list — the pane
   already enumerates installed models, so "gemma3:4b is not installed" is available at the point of
   failure.
3. Stop defaulting to a hard-coded model id that may not be present. Prefer the first vision-capable
   model the backend actually reports, and only fall back to a literal when the list is empty.
4. Add `gemma4` / `gemma-4` to `VisionModelMarkers` (`AiSettings.cs:1443`). Low priority and **not** the
   cause of this bug: Ollama reports a real `capabilities` array which the module already honours, so the
   marker list is only the fallback for backends that report nothing. It has `llama4` but not `gemma4`,
   so it will mis-advise on such a backend.

**Not a bug:** every model currently installed here (`gemma4:12b`, `gemma4:26b`, `qwen3.6:27b`,
`mistral-small3.2:24b`) declares `vision` in `ollama show`, and `gemma4:12b` additionally declares
`audio`. Nothing needs pulling; the default just points at a model that is not there.

#### ✅ FIXED 2026-09-10 — all four items

1. **The swallowed failure is logged.** `AiBrain` gained a static `LogSink` that `AiBrainModule` points
   at `IHost.Log`, cleared on shutdown because it is a static holding a delegate over the instance. The
   bare `catch { return null; }` still returns null — a silent companion on a broken backend is correct —
   but now records the error CATEGORY, the model, and the endpoint **host**. Never the full URL: a base
   URL can carry a key in its query.
2. **The user is told, once.** `AiModelPolicy.ChooseModel` re-validates the configured id against what
   the backend reports and returns an actionable line the companion speaks through the existing
   `IHost.Say` — no new ABI member, so no further `MinHostVersion` bump. De-duplicated via
   `AdvisoryOnce`, because a companion repeating "gemma3:4b isn't available" every thirty seconds would
   be a worse bug than the one being fixed.
3. **No more trusting a hard-coded id.** When the configured model is absent, the first backend-reported
   model that can actually do the job is substituted. Critically, an **empty or absent** model list is
   treated as *unknown*, not as proof of absence — otherwise every offline start would accuse a perfectly
   good configuration. Resolution happens BEFORE the screen capture, so a request that cannot be sent no
   longer pays for a screenshot.
4. **`gemma4` / `gemma-4` added to `VisionModelMarkers`.**

The inventory is refreshed in `PrepareAsync`, i.e. while the app is already talking to the backend, via
an injected `ModelLister` wired to the same listing call the Options pane uses — so the pane and the
brain can never disagree about what is installed.

**Verified:** 18 new assertions in `AiEngineProbe`. Mutation-tested **7/7 FIRED**. The first run of that
harness reported 0/7, which was a false result, not a pass: it rebuilt the HOST project while the code
under test compiles into `AiBrain.dll`. The harness now asserts the module DLL's timestamp advanced and
that a witness assertion label appears in the probe's report before it trusts any PASS/FAIL.

---

### BUG-003 — screen capture returns the wallpaper, and follows the wrong monitor

**Reported 2026-09-10.** Two symptoms, and they have different causes:

**(a) The capture shows only the desktop wallpaper, not the foreground windows.**

**(b) Moving the companion to the second monitor still captures the first.**

**Symptom (b) is explained, and it may be a design decision rather than a defect.** Capture deliberately
follows the **foreground window's** monitor, not the companion's. `ActiveWindow.CaptureContext`
(`src/dotNet/Ai/ActiveWindow.cs:120`) reads `GetForegroundWindow`, takes its rect, and calls
`DesktopGeometry.SelectCaptureMonitor(foregroundBounds, fallback, monitors)`; the companion's own
monitor arrives only as `fallback`, and its own doc comment says "if no usable foreground window exists,
fall back to the monitor containing the pet". `FormCompanion.CaptureScreenBounds` correctly resolves the
companion's monitor via `Screen.FromRectangle(Bounds).Bounds`, so the input is right and the preference
is what surprises.

So with the companion on monitor 2 and the active window on monitor 1, capturing monitor 1 is the code
working as written. Two defensible readings: react to *what the user is looking at* (current behaviour)
or *what is around me* (the reported expectation). **Pick one deliberately.** A reasonable resolution is
to prefer the companion's monitor when the foreground window is on a different one, since a companion
commenting on a screen it is not standing on reads as broken regardless of which is more useful.
Note `IsOwnWindow` already blanks the title and ignores the bounds when one of our own windows is
foreground, so an open Options window correctly falls through to the companion's monitor.

**Symptom (a) is not yet explained, but several plausible causes are eliminated.** Verified correct:

- the blit **does** pass `CAPTUREBLT` — `StretchBlt(..., Srccopy | Captureblt)` at
  `modules/AiBrain/engine/AiBrain.cs:374`, so this is not the classic missing-flag bug;
- the source is the screen DC, `GetDC(IntPtr.Zero)`, not a window DC or the desktop window;
- DPI is **not** the problem: `PerMonitorV2` is declared in both `src/Properties/app.manifest:31-32`
  and `Application.SetHighDpiMode` at `src/dotNet/Program.cs:46`, so `Screen.Bounds` is in physical
  pixels and cannot be virtualised out of alignment with the screen DC;
- bounds selection is sane, and symptom (b) shows the rect is a real monitor rect, not a degenerate one.

Leading remaining candidate: **GDI cannot reliably read DWM-composited content.** `BitBlt`/`StretchBlt`
from the screen DC is the legacy path; GPU-rendered and hardware-overlay window content is not
guaranteed to be present in it, and the wallpaper is what remains when it is not. The supported modern
answers are DXGI Desktop Duplication or `Windows.Graphics.Capture`. Confirm before rewriting anything:
if it is this, a plain Notepad window will capture fine while a browser or a video will not.

**Both are blocked on instrumentation, for the same reason as BUG-002.** Nothing records the chosen
rect, which monitor won, or whether the resulting bitmap was uniform, so this is currently unfalsifiable
from a user report — log the selected bounds, the foreground-vs-companion decision, and a cheap
uniformity check on the bitmap. **Since shipped**, with the rest of the AI Brain instrumentation; the
surviving half of that work item is "instrument the three modules still at zero" in
[`../BACKLOG.md`](../BACKLOG.md).

**Privacy constraint on any diagnostic here:** never write capture content, OCR text or a bitmap into
the diagnostic log. Users attach it to issues. If a visual dump is needed to diagnose this, it must be a
separate, explicit, opt-in, one-shot action that says where it wrote the file.

#### ✅ (b) FIXED 2026-09-10 — the companion's monitor wins

The maintainer picked "react to what is around me" over "react to what the user is looking at", because
it is also the reading that makes a per-monitor watcher possible. New
`DesktopGeometry.SelectCompanionMonitor` replaces `SelectCaptureMonitor` at the one call site in
`ActiveWindow.CaptureContext`. The foreground window is NOT discarded: it still supplies the title and
`ScreenContext.ForegroundWindowBounds`, so a front window that happens to be on the companion's monitor
is still the preferred capture *subject* within it. The two decisions compose — which monitor, then which
rect inside it — and `ChooseCaptureBounds` already falls back to the whole monitor when the window does
not overlap it, so a front window on another display cannot drag the subject away.

The companion's cached rect is still snapped through the selector rather than used verbatim: it can be
stale after a resolution change or a display being unplugged, and capturing a rect that is no longer a
monitor reads black.

**Follow-on defect this exposed, also fixed:** `AiBrain.DescribeOtherWindows` says "Also open on this
screen" but keyed its filter on the FOREGROUND window's `MonitorIndex`. Once capture followed the
companion instead, the phrase and the pixels could refer to two different displays — the model was handed
a list of windows the user could not see, next to a picture of somewhere else. Now keyed on
`ScreenContext.MonitorBounds` by intersection. It had **no test coverage at all** before this.

**Verified:** 4 new assertions in `CoreTests`, 11 in `AiEngineProbe` (including that a window TITLE never
reaches the prompt, which was previously trusted rather than asserted). Mutation-tested **7/7 FIRED**
across both runners.

#### ✅ (a) RESOLVED 2026-09-10 — the suspected cause was WRONG, measured

The leading candidate above — "GDI cannot reliably read DWM-composited content", implying a DXGI or
`Windows.Graphics.Capture` rewrite — is **refuted**. Measured on this machine over every visible
top-level window, comparing the product's own path (`StretchBlt` from the screen DC with
`SRCCOPY | CAPTUREBLT`) against `PrintWindow` with `PW_RENDERFULLCONTENT`:

| window | screen-DC (what we ship) | PrintWindow |
|---|---|---|
| Code, OUTLOOK, ONENOTE | content, 70-99 edges/kpx | content |
| msedgewebview2, WhatsApp | **content**, 76-78 edges/kpx | content |
| cmd | content, 122 edges/kpx | **blank** (returned false) |
| TextInputHost | content, 74 edges/kpx | **blank** |
| XboxPcApp, ApplicationFrameHost | content | 98% one colour |

The GDI path reads GPU-composited windows perfectly; `PrintWindow` is the path that returns blank
surfaces. **No capture-API rewrite is warranted**, and the backlog's proposed discriminator ("a browser
will fail where Notepad succeeds") does not hold here.

The most likely explanation for the original report is that (a) was **(b) in disguise**: capture followed
the foreground window's monitor, and a monitor the user was not working on is mostly wallpaper. That is
now fixed.

Because that is an inference rather than a reproduction, the instrumentation the entry asked for was
added anyway, so a recurrence is falsifiable instead of a matter of opinion:
`AiBrain.UniformityPercent` reports the share of a sparse sample taken by the single most common colour,
and the capture log line records the chosen rect, whether the window or the monitor won it, and that one
number. Geometry and a statistic only — never pixels, never OCR text, honouring the privacy constraint
above. 3 assertions cover it.

---

### BUG-004 — the leak soak's verdict was a coin flip, and it is the only gate that can catch a leak

**Found 2026-09-10 by running the [`RELEASE-CHECKLIST`](RELEASE-CHECKLIST.md) leak-soak step before
the v1.1.0 tag** — the first time either soak had been run since v1.0.0. `runtime-resource-soak.ps1` failed with
`GDI object growth exceeded the bound: 81 > 16`, and USER was over too (+57).

**Not a v1.1.0 regression, mechanically confirmed.** The churn path is
`RunResourceChurnPetCycle` + `RefreshTrayIconForResourceChurn` + `ContextMenus.RefreshSpeechMenuItem`.
`git diff v1.0.0..HEAD -- src/` touches only `PluginApi.cs`, `DesktopWindows.cs` (new), 
`CompanionHost.cs`, the csproj and `Program.cs` — and the entire `Program.cs` change is one early-exit
`--desktopwindows-selftest` branch that cannot execute during a churn run. v1.0.0 shipped this too; it
was simply never measured, because step 3 was skipped.

**The real defect is the MEASUREMENT.** The gate compares the FIRST sample against the LAST, and native
handles held by `Bitmap`/`Font`/`Icon`/`Form` finalizers are released only when the GC runs — so the
counter is a sawtooth and the verdict depends entirely on where the last sample happened to land.
Measured on one unchanged build:

| run | raw first-vs-last GDI | verdict it produced |
|---|---|---|
| 30 s | **+81** | FAIL |
| 60 s | **+206** | FAIL |
| 90 s | **−22** | PASS |

Within a single 60 s trace, GDI climbed 91 → 193, dropped to 58 in one sample, then climbed to 265. The
historical baseline recorded in `docs/HISTORY-pre-1.0.0.md` ("GDI −24") was a run that ended just after
a collection, so it was never evidence of health either.

**Per-step attribution** (added to the churn marker, since reading the code found nothing — every
explicit GDI site in the path disposes correctly):

| step | GDI net / 190 cycles | USER net | per cycle |
|---|---|---|---|
| pet + speech | +255 | +270 | +1.34 GDI, +1.42 USER |
| tray icon | −24 | −8 | clean |
| menu refresh | 0 | 0 | clean |

Ablating `Say`, `PaintSpeechForResourceChurn` and `DrawToBitmap` one at a time did **not** eliminate it,
so it is not one missing `Dispose` — it is spread across creating and destroying a top-level window plus
a speech bubble twice a second, which is not a workload any user generates.

**What settles it:** `SettleAndSample` forces `GC.Collect` → `WaitForPendingFinalizers` → `GC.Collect`
before reading the counters, at the start and end of churning. That converts an unanswerable question
into an answerable one: after everything collectable HAS been collected, did the process permanently
lose handles? Over 319 cycles the post-finalization growth was **+31 GDI, +21 USER — 0.097 and 0.066 per
cycle**, i.e. two orders of magnitude below the per-cycle allocation rate, which is the signature of a
warming cache rather than a linear leak. A periodic settled series was added to confirm the shape
directly rather than by inference from endpoints.

#### ✅ FIXED 2026-09-10 — there is no leak, and the gate now measures that

The settled series is conclusive. Post-finalization GDI over a 548-cycle, three-minute run:

| cycle | 40 | 80 | 120 | 160 | 200 | 240 | 280 | 320 | 360 | 400 | 440 | 480 | 520 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| GDI | 46 | 46 | 46 | 46 | 46 | 46 | 46 | 46 | 46 | 46 | 46 | 46 | 46 |

**Exactly flat**, for 500 cycles. The +31 from the cold baseline is a one-time warm-up — the first
sprite decode, cached fonts and brushes, the bubble's region, the tray icon — paid before cycle 40 and
never paid again. `Handles` and `USER` behave the same. **The app does not leak GDI, USER or kernel
handles**; every explicit disposal in the churn path was already correct, which is why reading the code
found nothing to fix.

`runtime-resource-soak.ps1` now asserts **post-finalization flatness** — the first post-warm-up settled
sample against the last — instead of raw first-vs-last. Warm-up is excluded by construction rather than
by a generous allowance, which is the same reasoning `module-window-soak.ps1` already used when it
compared its LAST segment against the previous one. `PrivateBytes` stays on the raw samples; it is not
finalizer-bound in the same way.

**Changing an assertion so that it passes is worthless unless it can still fail, so that was tested
directly** by injecting genuinely ROOTED leaks (held in a field, so no finalizer can reclaim them):

| injected leak | result |
|---|---|
| `CreateCompatibleDC` per cycle, never `DeleteDC` | **caught** — post-finalization GdiObjects over bound |
| rooted `Form` HWND per cycle | **caught** — trips GdiObjects first, since an HWND carries GDI objects too |
| *(control)* unmodified build | passes, `SettledGrowth` = GDI 0, USER −4, Handles −1 |

One instructive false negative found while building that check: a rooted **`System.Drawing.Font`** per
cycle sailed straight through. GDI+ `Font` and `Bitmap` are user-mode objects and do **not** necessarily
consume a handle `GetGuiResources` counts, so they are useless as test leaks — use a raw GDI handle
(`CreateCompatibleDC`, `GetHbitmap`) when writing one. This is worth remembering before concluding from
this gate that "nothing leaks": it measures OS handle counts, not GDI+ memory.

Per-step attribution, the settled series and the settled counters all remain in the churn marker, so the
next occurrence starts from data instead of from a code read.
