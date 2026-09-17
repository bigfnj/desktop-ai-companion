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
    & (Join-Path $repoRoot 'build.ps1') @buildParams
    if ($LASTEXITCODE -ne 0) { throw "build.ps1 failed (exit $LASTEXITCODE)." }

    $outputRoot = Join-Path $repoRoot 'build\DesktopAICompanionPortable\bin\Release\x64'
    $exe = Join-Path $outputRoot 'DesktopAICompanion.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "The built executable is missing: $exe" }
    # Every module with a self-test below must be listed here, or a build that silently failed to produce it
    # looks identical to a clean run (the self-test skip-PASSES on a missing folder, which is correct for a
    # payload with no dev modules and useless as a gate). reminder + remembrance were absent from this list
    # until 2026-08-27, so either could have vanished from the build unnoticed.
    # blinkingled was missing from this list until 2026-09-17 and was the ONE module whose self-test
    # could skip-pass unnoticed, for the same reason reminder and remembrance could before 2026-08-27.
    foreach ($moduleId in 'testmodule', 'fortunes', 'aibrain', 'petstudio', 'reminder', 'remembrance',
                          'blinkingled', 'agentflow') {
        if (-not (Test-Path -LiteralPath (Join-Path $outputRoot "modules\$moduleId"))) {
            throw "Module '$moduleId' is missing from the build output; its self-test would skip-pass."
        }
    }

    Write-Host '=== core regression tests' -ForegroundColor Cyan
    & dotnet build (Join-Path $repoRoot 'tests\DesktopAICompanion.CoreTests\DesktopAICompanion.CoreTests.csproj') `
        -c Release --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'CoreTests build failed.' }
    & (Join-Path $repoRoot 'tests\DesktopAICompanion.CoreTests\bin\Release\DesktopAICompanion.CoreTests.exe')
    if ($LASTEXITCODE -ne 0) { $failures.Add('CoreTests') }

    # Flag -> the marker file it writes, so a SKIP can be detected. Keep in sync with build.yml.
    $flags = [ordered]@{
        '--security-selftest'                = $null
        '--catalog-selftest'                 = $null
        '--fullscreen-selftest'              = $null
        '--pettyperegistry-selftest'         = 'dp-pettyperegistry-selftest.txt'
        '--hardening-selftest'               = $null
        '--audio-selftest'                   = 'dp-audio-selftest.txt'
        '--module-host-selftest'             = 'dp-module-host-selftest.txt'
        '--fortunes-selftest'                = 'dp-fortunes-selftest.txt'
        '--fortunes-engine-selftest'         = 'dp-fortunes-engine-selftest.txt'
        '--aibrain-selftest'                 = 'dp-aibrain-selftest.txt'
        '--petstudio-selftest'               = 'dp-petstudio-selftest.txt'
        '--wpf-options-selftest'             = $null
        # BUG-001's recovery wiring: the TaskbarCreated listener and the WM_CLOSE orderly exit.
        # Both fail SILENTLY when got wrong (a message-only window is never sent the shell
        # broadcast), so they are asserted rather than eyeballed.
        '--traywatcher-selftest'             = $null
        '--fortunes-smart-progress-selftest' = 'dp-fortunes-smart-progress-selftest.txt'
        # Convention-based (--module-selftest=<id>): loads the module through the REAL loader and calls its
        # public static bool SelfTest(out string). Needs no host edit per module. Both of these modules
        # shipped to the catalog with NO self-test at all; Reminder in particular had six pure helpers whose
        # internal checks nothing ever ran, which is indistinguishable from having none.
        '--module-selftest=reminder'         = $null
        '--module-selftest=remembrance'      = $null
        '--module-selftest=blinkingled'      = $null
        # Registered WITH its marker file, which the three above are not. ModuleConventionSelfTest.cs
        # writes dp-module-<id>-selftest.txt and docs\module-authoring.md tells a new module author to
        # register it, so this is the documented shape; the $null entries above predate that and mean
        # the gate checks only their exit code, which a missing-folder SKIP also satisfies.
        # Retrofitting those three is filed in BACKLOG.md rather than done here.
        '--module-selftest=agentflow'        = 'dp-module-agentflow-selftest.txt'
    }

    Write-Host '=== app self-tests' -ForegroundColor Cyan
    foreach ($flag in $flags.Keys) {
        $marker = $flags[$flag]
        if ($marker) {
            $markerPath = Join-Path $env:TEMP $marker
            # [IO.File]::Delete rather than Remove-Item, and no Test-Path guard (Delete is a no-op on a
            # missing file). Remove-Item still performs ~ home-directory expansion even under -LiteralPath,
            # so it fails outright when $env:TEMP holds a path containing a tilde -- which is the norm on
            # Windows whenever the account name exceeds 8 characters and TEMP is set to the 8.3 short form.
            # It reported "An object at the specified path ... does not exist" for a path Test-Path had just
            # confirmed existed. Latent until the second gate run on such a box, because run one has no
            # marker to delete -- which is why this survived unnoticed.
            [System.IO.File]::Delete($markerPath)
        }
        # A GUI exe does not block PowerShell, so wait explicitly. Child output is captured rather than
        # inherited: these self-tests print hundreds of PASS lines each, which buries the summary. The log is
        # echoed only when something fails.
        $log = Join-Path $env:TEMP ("dp-gate-" + $flag.Trim('-') + ".log")
        $process = Start-Process -FilePath $exe -ArgumentList $flag -Wait -PassThru -NoNewWindow `
            -RedirectStandardOutput $log -RedirectStandardError "$log.err"
        if ($process.ExitCode -ne 0) {
            $failures.Add("$flag (exit $($process.ExitCode))")
            Write-Host ("  FAIL  {0}" -f $flag) -ForegroundColor Red
            foreach ($logPath in @($log, "$log.err")) {
                if (Test-Path -LiteralPath $logPath) {
                    Get-Content -LiteralPath $logPath | Select-Object -Last 40 | ForEach-Object { Write-Host "        $_" }
                }
            }
            continue
        }
        if ($marker) {
            $markerPath = Join-Path $env:TEMP $marker
            if (-not (Test-Path -LiteralPath $markerPath)) {
                $failures.Add("$flag (wrote no marker file)")
                Write-Host ("  FAIL  {0} -- no marker" -f $flag) -ForegroundColor Red
                continue
            }
            # '^\s*SKIP:' and NOT '^SKIP:', which is what this was until 2026-09-17.
        # Two report writers indent the lines they re-emit: ModuleConventionSelfTest.cs
        # prefixes every module line with '  [<id>] ', and AiBrainModuleSelfTest.cs indents the
        # engine probe's report by four spaces. So AiEngineProbe's two real skips -- the DPAPI
        # round-trip and the Windows OCR recognizer, the latter called 'the standing proof that
        # the WinRT projection resolves there' in its own comment -- were INVISIBLE to this
        # grep, and a machine lacking either silently ran fewer assertions and printed ok.
        # SelfTestProbe.Skip()'s doc comment promises the gate fails on a SKIP; with the old
        # anchor that promise was false for every module, since none of their lines start at
        # column zero.
        $skips = @(Select-String -LiteralPath $markerPath -Pattern '^\s*SKIP:')
            if ($skips.Count -gt 0) {
                $failures.Add("$flag (SKIPPED: $($skips[0].Line.Trim()))")
                Write-Host ("  FAIL  {0} -- skipped, did not actually run" -f $flag) -ForegroundColor Red
                continue
            }
        }
        Write-Host ("  ok    {0}" -f $flag) -ForegroundColor DarkGray
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
    Write-Host 'GATE PASSED (build 0 warnings, core tests, 18 self-tests with no skips, invariants, payloads, template, shimeji verify + selftest).' -ForegroundColor Green
}
finally {
    Pop-Location
}