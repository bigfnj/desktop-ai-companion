# Releasing

Desktop AI Companion ships **unsigned** Windows x64 builds. To cut a release:

1. Bump `DesktopAICompanionVersion` (and `DesktopAICompanionAssemblyVersion`) in
   [`ProductVersion.props`](../ProductVersion.props).
2. Commit and push to `master`; confirm [`build.yml`](../.github/workflows/build.yml) is green.
3. **Run the leak soak locally** and check the growth numbers:
   `.\tests\runtime-resource-soak.ps1` → expect `"Result": "PASS"` with `Handles`/`GdiObjects`/`UserObjects`
   growth inside their bounds (16 each) and `PrivateBytes` under 64 MB. This is the only gate that catches an
   undisposed HWND, Bitmap, Font or Icon: it drives the app from outside and watches the OS counters, so no
   in-process self-test substitutes for it. It is deliberately not in the blocking CI path (it needs a real
   window station, and growth thresholds flake on a headless runner) — run it here, or trigger the
   **resource-soak** job via workflow dispatch. Record the numbers in the release notes so the next release
   has something to compare against.
   Then, if any module owns a window, **run the module-window soak too**:
   `.\tests\module-window-soak.ps1` → expect `RESULT=PASS`. The soak above cannot reach a module window at
   all — it drives the shipped app from outside and the app's churn loop never opens one — so this is the only
   check covering a module's own HWNDs, Bitmaps and decoded sprites. It compares the LAST segment against the
   previous one rather than against a cold start, because the first pass legitimately sets a high private-byte
   watermark while a sprite sheet decodes. Record these numbers too.
4. **Walk the live smoke script** below. Everything above is a self-test: it proves invariants, not that the
   app still works. This is the class of check that caught the S6p2 UI, a stale install being debugged as if
   it were current, and the OCR mojibake — none of which any automated gate noticed.
5. Tag and push: `git tag vX.Y.Z && git push origin vX.Y.Z`.

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
