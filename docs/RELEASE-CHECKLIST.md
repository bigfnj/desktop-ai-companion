# Releasing

Desktop AI Companion ships **unsigned** Windows x64 builds. To cut a release:

1. Bump `DesktopAICompanionVersion` (and `DesktopAICompanionAssemblyVersion`) in
   [`ProductVersion.props`](../ProductVersion.props).
2. **Regenerate the catalog in the same commit as the bump**: `.\packaging\New-ContentCatalog.ps1`. This
   is the step that tells existing users the release exists.

   `catalog.json`'s `app.version` is where the launch update check reads the latest version from, and
   nothing else in a release touches it — `release.yml` does not, and `New-ContentCatalog.ps1` otherwise
   runs only during a *module* publish. So a run of host releases with no module publish between them
   leaves the number behind, and a stale number does not disable the check, it inverts it: every user is
   told they are current. **This happened.** The catalog was generated while the props said 1.1.0, then
   v1.1.1, v1.1.2 and v1.1.3 shipped, and nobody on 1.1.0 was ever offered the tray-icon fix.

   `Test-ModulePublishFreshness.ps1` now fails when the two disagree, so `build.yml` will catch a
   forgotten regeneration. That is also why this belongs in the *same commit* as the bump rather than
   after the tag: the alternative leaves `master` red until the release lands. The cost is a window of a
   few minutes where the catalog names a version whose assets are still building, and the footer is a
   link to the releases page rather than a download, so the worst case is a user seeing the previous
   release for a moment.
3. Commit and push to `master`; confirm [`build.yml`](../.github/workflows/build.yml) is green.

   **Exception, when this release is the one a module has been waiting for.** If a module's source
   declares a `MinHostVersion` equal to the version being cut, `build.yml` **cannot** be green yet:
   `Test-ModulePublishFreshness.ps1` fails with a version mismatch until the module is published, and the
   "Modules are a separate publish" rule below forbids publishing it until this host release has shipped.
   The two rules genuinely contradict each other, and the resolution is that
   [`release.yml`](../.github/workflows/release.yml) does **not** run the freshness gate — only
   `build.yml` does. So the working order is: push the host → tag → publish the module → CI goes green.
   Confirm the freshness mismatch is the ONLY failure before tagging (`.\tests\run-gate.ps1` locally),
   because that exception is otherwise an excellent way to tag over a real break. Hit for real on
   2026-09-10 cutting v1.1.0 with aibrain 1.1.0 waiting on it.
4. **Run the leak soak locally** and check the growth numbers:
   `.\tests\runtime-resource-soak.ps1` → expect `"Result": "PASS"`. The figure that
   matters is **`SettledGrowth`**, not `Growth`: `Handles`/`GdiObjects`/`UserObjects` measured after a
   forced `GC` → `WaitForPendingFinalizers` → `GC`, compared between the first post-warm-up sample
   and the last (bounds 16 each). `PrivateBytes` is still judged on the raw samples (under 64 MB).

   Record `SettledGrowth` in the release notes, **not** `Growth`. The raw `Growth` numbers are a
   sawtooth — `Bitmap`, `Font`, `Icon` and `Form` release their native handles only when a finalizer
   runs — and on one unchanged build GDI came out +81, +206, −22 and −3 depending purely on run
   length. See BUG-004 in [`ISSUES-post-1.0.0.md`](ISSUES-post-1.0.0.md); the pre-1.0.0 "GDI −24" baseline in
   [`HISTORY-pre-1.0.0.md`](HISTORY-pre-1.0.0.md) is one of those coin flips, not a target. This is the only gate that catches an
   undisposed HWND, Bitmap, Font or Icon: it drives the app from outside and watches the OS counters, so no
   in-process self-test substitutes for it. It is deliberately not in the blocking CI path (it needs a real
   window station, and growth thresholds flake on a headless runner) — run it here, or trigger the
   **resource-soak** job via workflow dispatch. Record the numbers in the release notes so the next release
   has something to compare against.
   Then **run the fullscreen stand-down probe**, which belongs here for the same reasons as the
   soaks (an interactive desktop, ~20s, and it puts a fullscreen window up):
   `dotnet run --project testsullscreen-standdown-probe\walkcount.csproj -c Release -- standdown`
   It is the only automatic end-to-end check on regression watchlist row 9, "anything visible over a
   fullscreen game", and it checks the invariant that actually holds -- no companion VISIBLE on a
   blocked monitor -- rather than "the companion hides", which passes a build that hides when it
   should have relocated to a free monitor. See its
   [README](../tests/fullscreen-standdown-probe/README.md) for why that distinction is measured
   rather than theoretical.

   Then, if any module owns a window, **run the module-window soak too**:
   `.\tests\module-window-soak.ps1` → expect `RESULT=PASS`. The soak above cannot reach a module window at
   all — it drives the shipped app from outside and the app's churn loop never opens one — so this is the only
   check covering a module's own HWNDs, Bitmaps and decoded sprites. It compares the LAST segment against the
   previous one rather than against a cold start, because the first pass legitimately sets a high private-byte
   watermark while a sprite sheet decodes. Record these numbers too.
5. **Re-run the mutation harnesses if any assertion or guard changed since the last release.** They are
   the only thing that distinguishes a passing gate from a gate that cannot fail, and none of them runs
   in CI (each rebuilds the tree several times and edits source in place, so a shared runner is the wrong
   place for them):

   | harness | proves | expect |
   |---|---|---|
   | [`tests/mutate-agentflow.py`](../tests/mutate-agentflow.py) | `--module-selftest=agentflow` is not a rubber stamp | `23/23 fired.` |
   | [`tests/mutate-selftest-guards.py`](../tests/mutate-selftest-guards.py) | the host self-test assertions that were previously unfailable | `6/6 fired.` |
   | [`tests/mutate-hardening-guards.py`](../tests/mutate-hardening-guards.py) | the source invariants in `runtime-hardening-selftest.ps1` | `7/7 fired.` |
   | [`tests/mutate-diagnostics.py`](../tests/mutate-diagnostics.py) | the diagnostic-log guards | `20/20 fired.` |

   A clean `0/N fired` is a red flag and never a result — it usually means the harness rebuilt the wrong
   project, which is why each case names its own csproj and asserts the artifact's timestamp advanced.
   Three of these four had **zero inbound references from anywhere in the repo** until 2026-09-17, which is
   how `tests/runtime-resource-soak.ps1` once got deleted as "an unreferenced script" three hours after CI
   stopped calling it, leaving the only leak gate unrunnable. This table is the reference.

   Every count above was measured on 2026-09-17, and running them is what found the reason to write this
   step down: `mutate-diagnostics.py` came back **19/20**, because one case had searched for a log line
   that was rewritten in 1.1.1. It printed `NO-OP pattern matched 0 times` every run, honestly, to nobody.
   A mutation suite that is not itself run rots into a suite that reports more coverage than it has.
6. **Walk the live smoke script** below. Everything above is a self-test: it proves invariants, not that the
   app still works. This is the class of check that caught the S6p2 UI, a stale install being debugged as if
   it were current, and the OCR mojibake — none of which any automated gate noticed.
7. Tag and push: `git tag vX.Y.Z && git push origin vX.Y.Z`.

## Live smoke script

**It lives in [`SMOKETEST.md`](../SMOKETEST.md).** Install the built MSI over the previous version (never
onto a clean machine only, since the upgrade path is the one users take), then walk it.

Sections A through E are the 12-minute Core pass and catch the class of bug that has actually shipped;
do at least those before every tag. The rest is worth a full pass when the release touches those areas.

The ten-row table that used to sit here was replaced in 2026-09-02 because it had not grown with the
product: it predated companions climbing, jumping, gripping windows, multi-monitor pinning, fullscreen
stand-down, per-companion speech routing and the update check, so a green pass over it said almost nothing about
a modern release. `SMOKETEST.md` also carries a regression watchlist naming each bug that reached users and
the row that would have caught it.

[`release.yml`](../.github/workflows/release.yml) then builds the portable ZIP + MSI, writes
`SHA256SUMS.txt`, and publishes them on the GitHub release for that tag.

## Modules are a separate publish

Modules do **not** ship with a release. `modules-dist/` is served off `master` via raw.githubusercontent, so
**merging to master is the module publish** — it reaches every existing user with no tag involved. Use:

```powershell
.\packaging\New-ModulePublish.ps1 -ModuleId <id> -Commit
```

It builds, zips, updates `modules-dist/modules.json`, commits, regenerates `catalog.json` and verifies, in the
one order that works: the catalog records the SHA-256 of the **committed** git blob, so the zip must be
committed before the catalog is generated. It refuses to continue otherwise.

Sequencing that matters when a module needs a new host: publish the module only **after** the host release it
declares in `MinHostVersion` has shipped, or the catalog offers users a module their host correctly refuses.
That is why Companion Studio 1.1.0 was published after `v1.4.6`, not with it.

> The former enterprise release process — reproducible double-builds, SBOM/SPDX, code signing, and
> source-rights / pack-rights evidence gates — was retired in favor of this lean hobby-grade flow.
> Provenance for bundled third-party content is documented in
> [`../THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md), not gated.
