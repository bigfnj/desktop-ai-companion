#requires -Version 5
<#
.SYNOPSIS
    Run the full local verification gate: build, core tests, every app self-test flag, the source-text
    invariants, and the module publish/version checks.

.DESCRIPTION
    One command so the gate is run the same way every time, and so a self-test cannot quietly report success
    without having run. That second point is not hypothetical: the module self-tests skip-PASS when their
    module folder is absent (correct behavior for a payload with no dev modules), which means a build that
    silently failed to produce modules/ looks identical to a clean run. This script fails on a SKIP.

    Mirrors .github/workflows/build.yml's flag list. The leak soak is deliberately NOT part of this gate --
    see docs/RELEASE-CHECKLIST.md; it is a pre-tag step because OS growth thresholds are too flaky to run
    on every change.

.EXAMPLE
    .\tests\run-gate.ps1
.EXAMPLE
    .\tests\run-gate.ps1 -SkipClean      # faster re-run when nothing structural changed
#>
[CmdletBinding()]
param(
    [switch]$SkipClean
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
Push-Location $repoRoot
try {
    $failures = New-Object 'Collections.Generic.List[string]'

    Write-Host '=== build (Release x64, host + modules)' -ForegroundColor Cyan
    # NOTE: never pipe build.ps1 through Select-Object -First; that short-circuits the upstream pipeline and
    # terminates the build partway, which silently skips the module builds.
    # Hashtable splat, not an array: array elements bind positionally, so '-Release' would arrive as a value
    # rather than a switch.
    $buildParams = @{ Release = $true }
    if (-not $SkipClean) { $buildParams['Clean'] = $true }
    # try/catch, NOT $LASTEXITCODE -- the same correction already applied to the three checks below,
    # for the same reason, which this call was left out of. build.ps1 sets $ErrorActionPreference='Stop'
    # and signals every failure by `throw`, so $LASTEXITCODE reads whatever the last NATIVE command left
    # behind and the branch below was dead. A build failure escaped as a raw PowerShell exception, the
    # GATE FAILED summary never printed, and no later check ran -- which is the one moment you most want
    # the summary, because it names what to look at.
    try { & (Join-Path $repoRoot 'build.ps1') @buildParams }
    catch { $failures.Add('build.ps1: ' + $_.Exception.Message) }

    $outputRoot = Join-Path $repoRoot 'build\DesktopAICompanionPortable\bin\Release\x64'
    $exe = Join-Path $outputRoot 'DesktopAICompanion.exe'
    # And this one, which had the identical defect and is reached by exactly the same failure. Fixing
    # only the call above would still let a failed build escape uncaught one line later.
    if (-not (Test-Path -LiteralPath $exe)) { $failures.Add("the built executable is missing: $exe") }
    if ($failures.Count -gt 0) {
        # Nothing below can mean anything without an executable, and running ten checks against a
        # missing exe buries the real failure under ten invented ones.
        Write-Host ''
        Write-Host 'GATE FAILED:' -ForegroundColor Red
        foreach ($failure in $failures) { Write-Host ("  - " + $failure) -ForegroundColor Red }
        exit 1
    }
    Write-Host '=== core regression tests' -ForegroundColor Cyan
    & dotnet build (Join-Path $repoRoot 'tests\DesktopAICompanion.CoreTests\DesktopAICompanion.CoreTests.csproj') `
        -c Release --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'CoreTests build failed.' }
    & (Join-Path $repoRoot 'tests\DesktopAICompanion.CoreTests\bin\Release\DesktopAICompanion.CoreTests.exe')
    if ($LASTEXITCODE -ne 0) { $failures.Add('CoreTests') }

    # The flag table, the marker map and the skip detection all live in one place now, called
    # by BOTH this gate and .github\workflows\build.yml. They used to be duplicated, under a
    # comment in build.yml telling a human to keep them in sync -- and they drifted: the flag
    # lists stayed equal while the marker map and the skip detection existed only here, so CI
    # checked nothing but exit codes and ten of the eighteen flags skip-PASSED there.
    #
    # -OutputRoot makes it assert every module folder is present, because a self-test whose
    # module is missing SKIPS and exits 0. Failures are COLLECTED rather than thrown, so one
    # missing module no longer hides every check after it.
    # Filtered on the sentinel rather than taking the whole pipeline: Write-Host collapses onto
    # stdout for any caller that is not PowerShell, so an unfiltered read turns progress lines
    # into failures. Explicit beats implicit here even though this particular caller IS
    # PowerShell and would have been fine.
    $selfTestOutput = @(& (Join-Path $repoRoot 'tests\Invoke-SelfTests.ps1') `
        -ExecutablePath $exe -OutputRoot $outputRoot)
    # The count comes back on stdout rather than being read out of the child's scope. It used to be
    # $SelfTestFlags.Count, which that script sets and this one cannot see -- see the comment beside
    # $CountPrefix there for why that broke only the PASSING run.
    $selfTestCount = 0
    foreach ($line in $selfTestOutput) {
        if ($line -like 'SELFTEST-FAILURE:*') {
            $failures.Add($line.Substring('SELFTEST-FAILURE: '.Length))
        }
        elseif ($line -like 'SELFTEST-COUNT:*') {
            $selfTestCount = [int]$line.Substring('SELFTEST-COUNT: '.Length)
        }
    }
    # A summary that can print "0 self-tests" is the same failure one level up: it would report a
    # green gate over a self-test runner that never ran anything.
    if ($selfTestCount -le 0) {
        $failures.Add('Invoke-SelfTests.ps1 reported no self-test count, so the runner did not run')
    }

    # try/catch, NOT $LASTEXITCODE, for these three. Corrected 2026-09-17 after an audit.
    # All three are .ps1 files that signal failure by `throw` (Assert-True in
    # runtime-hardening-selftest.ps1, plain throws in the two packaging checks), and
    # $LASTEXITCODE is set only by NATIVE commands or an explicit `exit`. So the old
    # `if ($LASTEXITCODE -ne 0)` form was wrong in BOTH directions:
    #
    #   On failure, the throw is terminating and $ErrorActionPreference='Stop' carried it
    #   out of this script with no catch -- so the $failures.Add never ran, the GATE FAILED
    #   summary never printed, and every later check was skipped. Those branches were dead.
    #
    #   On success, the condition read whatever the last NATIVE command left behind, which
    #   is DesktopAICompanion.CoreTests.exe (Start-Process -PassThru does not set it). So a
    #   CoreTests failure ALSO reported runtime-hardening-selftest.ps1 as failed, inventing
    #   a failure in a suite where every invariant had passed.
    Write-Host '=== source-text invariants' -ForegroundColor Cyan
    try { & (Join-Path $repoRoot 'tests\runtime-hardening-selftest.ps1') }
    catch { $failures.Add('runtime-hardening-selftest.ps1: ' + $_.Exception.Message) }

    Write-Host '=== published module payloads' -ForegroundColor Cyan
    try { & (Join-Path $repoRoot 'packaging\Test-ModulePublishFreshness.ps1') }
    catch { $failures.Add('Test-ModulePublishFreshness.ps1: ' + $_.Exception.Message) }

    # The freshness check above is about module VERSIONS. Nothing verified the catalog's asset
    # HASHES until 2026-09-17, so editing any companion or fortune pack without regenerating the
    # catalog left a recorded sha256 that no longer matched the blob raw.githubusercontent.com
    # serves -- and then the app correctly REFUSES every user's download of that asset while every
    # gate stays green. ~17s for 217 assets.
    # Five refusals in the atomic publish helper, each fed the input it exists to refuse, behind a
    # green baseline. It read 2 of its 11 parameters until 2026-09-17 while the installer claimed it
    # enforced the seal hash, so these are exactly the checks that have to be seen failing.
    Write-Host '=== atomic publish refusals' -ForegroundColor Cyan
    try { & (Join-Path $repoRoot 'packaging\Test-AtomicPublish.ps1') }
    catch { $failures.Add('Test-AtomicPublish.ps1: ' + $_.Exception.Message) }

    # The same defect, five more times, in the same file. Three functions declared a MANDATORY
    # -TrustedRoot and never read it -- two of them immediately ahead of Remove-Item -Recurse -Force
    # -- Copy-...ValidatedInputFile never read its mandatory -Root, and both -RejectHardLinks flags
    # were decoration. Wired in here in the SAME commit as the fix, because the MSI path this file
    # serves is not built by the gate: fixing it without a gate step would have reproduced the exact
    # defect class it closes. ~1s, all under %TEMP%.
    Write-Host '=== staging path-safety refusals' -ForegroundColor Cyan
    try { & (Join-Path $repoRoot 'packaging\Test-StagingPathSafety.ps1') }
    catch { $failures.Add('Test-StagingPathSafety.ps1: ' + $_.Exception.Message) }

    Write-Host '=== catalog integrity' -ForegroundColor Cyan
    try { & (Join-Path $repoRoot 'packaging\Test-ContentCatalogIntegrity.ps1') }
    catch { $failures.Add('Test-ContentCatalogIntegrity.ps1: ' + $_.Exception.Message) }

    # Backlog entries that state a machine-checkable closing criterion are evaluated here, because
    # three of them were found stale on 2026-09-21/22 -- each fixed by later work in the cycle that
    # filed it, none linking back. This fires in the same run that proves the fix works, while the
    # author is still looking at it.
    Write-Host '=== backlog closing criteria' -ForegroundColor Cyan
    try { & (Join-Path $repoRoot 'tests\Test-BacklogClosingCriteria.ps1') }
    catch { $failures.Add('Test-BacklogClosingCriteria.ps1: ' + $_.Exception.Message) }

    # The module template is built by nothing else, so it would rot unnoticed: this scaffolds a throwaway
    # module from it, builds it, and removes it again.
    Write-Host '=== module template' -ForegroundColor Cyan
    try { & (Join-Path $repoRoot 'packaging\Test-ModuleTemplate.ps1') -Configuration Release }
    catch { $failures.Add('Test-ModuleTemplate.ps1: ' + $_.Exception.Message) }

    # The Shimeji converter's output half: grade every shipped pet with the app's REAL validator (via the
    # source-linked ShimejiConvert.Engine) and round-trip it through the DTOs. This is the emitter's
    # regression net -- it must stay all-valid and all-round-trip before any Shimeji-side parsing is trusted.
    # Not built by build.ps1 (a dev/module-shared tool, not the one shipped product), so build it here.
    Write-Host '=== shimeji converter (verify + selftest)' -ForegroundColor Cyan
    & dotnet build (Join-Path $repoRoot 'tools\ShimejiConvert\ShimejiConvert.csproj') `
        -c Release --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) {
        $failures.Add('ShimejiConvert build')
    }
    else {
        $shimejiExe = Join-Path $repoRoot 'tools\ShimejiConvert\bin\Release\ShimejiConvert.exe'
        if (-not (Test-Path -LiteralPath $shimejiExe)) {
            $failures.Add('ShimejiConvert.exe missing after build')
        }
        else {
            # Output half: every shipped pet stays valid + round-trips.
            & $shimejiExe verify (Join-Path $repoRoot 'Companions')
            if ($LASTEXITCODE -ne 0) { $failures.Add("ShimejiConvert verify (exit $LASTEXITCODE)") }
            # Input half: the parser + Group 1/2/3 classifier on the committed synthetic fixture. (The
            # 91/53/32/6 census against the real gil/shimeji-ee config is a dev step -- that config is
            # copyrighted and must not live in this repo.)
            & $shimejiExe selftest
            if ($LASTEXITCODE -ne 0) { $failures.Add("ShimejiConvert selftest (exit $LASTEXITCODE)") }
        }
    }

    Write-Host ''
    if ($failures.Count -gt 0) {
        Write-Host "GATE FAILED:" -ForegroundColor Red
        foreach ($failure in $failures) { Write-Host "  - $failure" -ForegroundColor Red }
        exit 1
    }
    # Counted, not typed. This line said 18 while the table held 19, which is the same drift
    # the doc-count invariants now catch -- and a gate that miscounts its own coverage is the
    # least convincing place to have it.
    Write-Host ("GATE PASSED (build 0 warnings, core tests, $selfTestCount self-tests " +
        'with no skips, invariants, payloads, template, shimeji verify + selftest).') -ForegroundColor Green
}
finally {
    Pop-Location
}