# Fullscreen stand-down probe

End-to-end check of the fullscreen stand-down in the **real app**, read through the window manager
rather than from a screenshot. It is the only thing in this repo that catches
[`SMOKETEST.md`](../../SMOKETEST.md)'s regression watchlist row 9 -- *"anything visible over a
fullscreen game, especially the UFO"* -- automatically, and it catches it in **both** directions.

## Why it is not in the gate

It needs an interactive desktop, takes about 20 seconds, and puts a 2%-opacity fullscreen window up
while it runs. Those are the same reasons the two soaks are out of the fast gate, so it lives beside
them: run it from [`docs/RELEASE-CHECKLIST.md`](../../docs/RELEASE-CHECKLIST.md), not from
`tests/run-gate.ps1`.

It is in the repo rather than a scratch directory for one reason: `tests/runtime-resource-soak.ps1`
was once deleted as "an unreferenced script" three hours after CI stopped calling it, leaving the
only leak gate unrunnable. An unreferenced probe on a temp drive is the same story with a shorter
fuse.

## The invariant is PER MONITOR

Stating it as "the companion hides" is wrong, and that was the first version of `StandDown.cs`. What
the app promises is that **no companion is visible on a blocked monitor**. With one monitor free the
correct behaviour is to RELOCATE there; only when every monitor is blocked must it hide. So a
hide-only check passes a build that hides when it should have moved -- measured, not theorised:

```
MUTATION B, the cache collapses per-monitor to "a game is running somewhere"
    step3 relocated to a free monitor=False  ['Sheep' mon0 hid]
```

That build HID instead of moving, and a hide-only probe called it correct.

## Running it

```powershell
dotnet run --project tests\fullscreen-standdown-probe\walkcount.csproj -c Release -- standdown
```

`walkcount.csproj` also carries the EnumWindows walk counter used to measure the shared-scan change
(679 callbacks / 781 user32 calls when the early exit cannot fire), and `standdown-ab.ps1` is the
phase-swept A/B driver. The phase sweep is not optional: a fixed delay reported a 150 ms regression
that did not exist, because the probe was phase-locked to a scan cycle anchored on companion spawn.

## What it does NOT cover

A companion clipped to a few pixels at a screen edge still reads as `IsWindowVisible`. The first
version of the probe required 24x24 px to count a window as a companion and therefore passed for the
wrong reason -- a companion walking off the top edge is clipped to 40x2, so "nothing visible" was
true because the sprite was thin. It now captures HWNDs up front and reads their own visibility,
which is the window's state rather than an inference from its shape.
