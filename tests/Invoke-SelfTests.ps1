#requires -Version 5
<#
.SYNOPSIS
    Run every product self-test flag against a built executable, and report which ones did not
    actually run. Shared by tests\run-gate.ps1 and .github\workflows\build.yml.

.DESCRIPTION
    THIS FILE EXISTS TO KILL A DRIFT. The flag list, the marker map and the skip detection used to
    live in run-gate.ps1, with build.yml carrying its own copy of the flag list under a comment
    reading "Keep in sync with tests\run-gate.ps1". That is an instruction to a human, and it
    drifted exactly as you would expect: the two flag LISTS stayed equal, while the marker map and
    the skip detection existed only in the gate. So CI ran all eighteen flags and checked nothing
    but their exit codes -- and a self-test whose module folder is missing SKIPS and exits 0, which
    reads as success. Ten of the eighteen were in that category.

    That is the precise failure run-gate.ps1's own doc block was written about, closed locally and
    left open in the one place that gates every pull request.

    Dot-sourcing a shared helper rather than duplicating a table is the existing house pattern:
    packaging\StagingPathSafety.ps1 and packaging\WixToolchainPolicy.ps1 each have several callers.

.PARAMETER ExecutablePath
    The built DesktopAICompanion.exe.

.PARAMETER OutputRoot
    The build output root holding modules\<id>. When supplied, every module named in
    $RequiredModules must be present, because a self-test whose module folder is absent skip-PASSES
    and a build that silently produced no modules would otherwise look identical to a clean run.

.PARAMETER LogDirectory
    Where to put captured child output. Defaults to $env:TEMP.

.OUTPUTS
    One line per failure, each prefixed with the literal SELFTEST-FAILURE: sentinel. No failures
    means no such line. Progress is written with Write-Host.

    The sentinel exists because "progress on the host stream, failures on the pipeline" is an
    IMPLICIT contract that only holds while the caller is PowerShell and can tell the two streams
    apart. Invoke it from anything else -- a python harness, a CI shell step that pipes -- and
    Write-Host collapses onto stdout, so every progress line reads as a failure. That happened on
    the first attempt to test this script and made a clean baseline look like nineteen failures.
    A sentinel is unambiguous from any caller and in any redirection.

    $failures = @(& .\Invoke-SelfTests.ps1 ... | Where-Object { $_ -like 'SELFTEST-FAILURE:*' })

.EXAMPLE
    $failures = @(& tests\Invoke-SelfTests.ps1 -ExecutablePath $exe -OutputRoot $outputRoot |
        Where-Object { $_ -like 'SELFTEST-FAILURE:*' })
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [string]$OutputRoot,
    [string]$LogDirectory
)

Set-StrictMode -Version Latest

# Every failure line carries this. See .OUTPUTS for why a sentinel rather than the pipeline.
$FailurePrefix = 'SELFTEST-FAILURE: '
# How many self-tests this table registers, reported to the CALLER on stdout.
#
# It exists because a caller cannot see $SelfTestFlags. This script is invoked with & (a child
# scope), so run-gate.ps1's summary line read an unset variable -- and under
# Set-StrictMode -Version Latest that is a TERMINATING error, not an empty string. The gate
# therefore worked on failure, where it exits before the summary, and threw on SUCCESS. It shipped
# that way for hours because the gate is red for an unrelated reason (module payload freshness), so
# the success path was never reached. Found by an audit, not by a run.
#
# Emitted BEFORE any work, so it is present even on the early return below: a caller reporting
# "0 self-tests" instead of failing would be the same class of bug one level up.
$CountPrefix = 'SELFTEST-COUNT: '

if (-not $LogDirectory) { $LogDirectory = $env:TEMP }

# Every module whose self-test can skip-PASS when its folder is absent. blinkingled and agentflow
# were added 2026-09-17; reminder and remembrance were absent until 2026-08-27, so either could have
# vanished from the build unnoticed for months.
$RequiredModules = @(
    'testmodule', 'fortunes', 'aibrain', 'petstudio',
    'reminder', 'remembrance', 'blinkingled', 'agentflow'
)

# Flag -> the marker file it writes, or $null when it writes none.
#
# A marker is what lets a SKIP be detected: the runner deletes it first, then requires it to exist
# afterwards and to carry no SKIP line. Every entry below was verified against the source that
# writes it before being registered here, because registering a marker for a self-test that writes
# none turns the gate red on "wrote no marker".
#
# $null is correct for exactly two of them: --security-selftest writes to stdout only and
# --traywatcher-selftest writes nothing, and neither has a skip path.
# --desktopwindows-selftest likewise writes no marker; it is in this table at all because it had NO
# CALLER ANYWHERE until 2026-09-17, while the monitor-attribution code it covers is live and feeds
# the module ABI through DesktopWindows.Snapshot.
$SelfTestFlags = [ordered]@{
    '--security-selftest'                = $null
    '--catalog-selftest'                 = 'dp-catalog-selftest.txt'
    '--fullscreen-selftest'              = 'dp-fullscreen-selftest.txt'
    '--desktopwindows-selftest'          = $null
    '--pettyperegistry-selftest'         = 'dp-pettyperegistry-selftest.txt'
    '--hardening-selftest'               = 'dp-hardening-selftest.txt'
    '--audio-selftest'                   = 'dp-audio-selftest.txt'
    '--module-host-selftest'             = 'dp-module-host-selftest.txt'
    '--fortunes-selftest'                = 'dp-fortunes-selftest.txt'
    '--fortunes-engine-selftest'         = 'dp-fortunes-engine-selftest.txt'
    '--aibrain-selftest'                 = 'dp-aibrain-selftest.txt'
    '--petstudio-selftest'               = 'dp-petstudio-selftest.txt'
    '--wpf-options-selftest'             = 'dp-wpf-options-selftest.txt'
    # BUG-001's recovery wiring: the TaskbarCreated listener and the WM_CLOSE orderly exit. Both
    # fail SILENTLY when got wrong (a message-only window is never sent the shell broadcast), so
    # they are asserted rather than eyeballed.
    '--traywatcher-selftest'             = $null
    '--fortunes-smart-progress-selftest' = 'dp-fortunes-smart-progress-selftest.txt'
    # Convention-based (--module-selftest=<id>): loads the module through the REAL loader and calls
    # its public static bool SelfTest(out string), so a new module needs no host edit. All four now
    # carry their marker; three of them did not until 2026-09-17, which meant that for exactly the
    # checks which load a module through the real loader, only the exit code was tested -- and
    # ModuleConventionSelfTest returns true for a missing module folder.
    '--module-selftest=reminder'         = 'dp-module-reminder-selftest.txt'
    '--module-selftest=remembrance'      = 'dp-module-remembrance-selftest.txt'
    '--module-selftest=blinkingled'      = 'dp-module-blinkingled-selftest.txt'
    '--module-selftest=agentflow'        = 'dp-module-agentflow-selftest.txt'
}

Write-Output ($CountPrefix + $SelfTestFlags.Count)

if (-not (Test-Path -LiteralPath $ExecutablePath)) {
    Write-Output ($FailurePrefix + "the executable is missing: $ExecutablePath")
    return
}

if ($OutputRoot) {
    foreach ($moduleId in $RequiredModules) {
        $moduleDirectory = Join-Path $OutputRoot (Join-Path 'modules' $moduleId)
        if (-not (Test-Path -LiteralPath $moduleDirectory)) {
            Write-Output ($FailurePrefix + "module '$moduleId' is missing from the build output; its self-test would skip-pass")
        }
    }
}

Write-Host ('=== app self-tests ({0} flags)' -f $SelfTestFlags.Count) -ForegroundColor Cyan
foreach ($flag in $SelfTestFlags.Keys) {
    $marker = $SelfTestFlags[$flag]
    $markerPath = $null
    if ($marker) {
        $markerPath = Join-Path $LogDirectory $marker
        # [IO.File]::Delete rather than Remove-Item, and no Test-Path guard (Delete is a no-op on a
        # missing file). Remove-Item still performs ~ home-directory expansion even under
        # -LiteralPath, so it fails outright when the temp path contains a tilde -- the norm on
        # Windows whenever the account name exceeds 8 characters and TEMP holds the 8.3 short form.
        # It reported "An object at the specified path does not exist" for a path Test-Path had just
        # confirmed existed. Latent until the SECOND run on such a box, because run one has no
        # marker to delete, which is why it survived unnoticed.
        [System.IO.File]::Delete($markerPath)
    }

    # A GUI-subsystem exe does not block PowerShell, so wait explicitly: `& $exe` returns
    # immediately with no exit code. Child output is captured rather than inherited, because these
    # self-tests print hundreds of PASS lines each and would bury the summary.
    $log = Join-Path $LogDirectory ('dp-gate-' + $flag.Trim('-') + '.log')
    $process = Start-Process -FilePath $ExecutablePath -ArgumentList $flag -Wait -PassThru -NoNewWindow `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"

    if ($process.ExitCode -ne 0) {
        Write-Output ($FailurePrefix + ('{0} (exit {1})' -f $flag, $process.ExitCode))
        Write-Host ('  FAIL  {0}' -f $flag) -ForegroundColor Red
        foreach ($logPath in @($log, "$log.err")) {
            if (Test-Path -LiteralPath $logPath) {
                Get-Content -LiteralPath $logPath | Select-Object -Last 40 |
                    ForEach-Object { Write-Host "        $_" }
            }
        }
        continue
    }

    if ($markerPath) {
        if (-not (Test-Path -LiteralPath $markerPath)) {
            Write-Output ($FailurePrefix + ('{0} (wrote no marker file)' -f $flag))
            Write-Host ('  FAIL  {0} -- no marker' -f $flag) -ForegroundColor Red
            continue
        }
        # Leading whitespace AND an optional '[<id>] ' tag, because two report writers reshape the
        # lines they re-emit: AiBrainModuleSelfTest indents the engine probe's report by four
        # spaces, and ModuleConventionSelfTest prefixes every module line with '  [<id>] '
        # (ModuleConventionSelfTest.cs:170). SelfTestProbe.Skip() writes a bare 'SKIP: ' with no
        # indent of its own, so whatever the re-emitter puts in front of it is all that stands
        # between a skip and this pattern.
        #
        # History, because the second half of it is the interesting part. Until 2026-09-17 this was
        # '^SKIP:', which missed BOTH shapes: AiEngineProbe's two real skips -- the DPAPI round-trip
        # and the Windows OCR recognizer its own comment calls "the standing proof that the WinRT
        # projection resolves there" -- were invisible, so a machine lacking either ran fewer
        # assertions and printed ok. That day's fix to '^\s*SKIP:' recovered the four-space case and
        # its comment claimed the '[<id>] ' case too. It did not: after \s* the next character is
        # '[', not 'S'. So for the FOUR convention-based flags -- reminder, remembrance,
        # blinkingled, agentflow, i.e. every module that reaches the gate through the real loader --
        # SelfTestProbe.Skip()'s promise that the gate fails on a skip stayed false for another day.
        # Caught 2026-09-18 by running the pattern against the three real line shapes instead of
        # re-reading the comment. A regex is worth about as much as the input you tested it on.
        $skips = @(Select-String -LiteralPath $markerPath -Pattern '^\s*(\[[^\]]*\]\s*)?SKIP:')
        if ($skips.Count -gt 0) {
            Write-Output ($FailurePrefix + ('{0} (SKIPPED: {1})' -f $flag, $skips[0].Line.Trim()))
            Write-Host ('  FAIL  {0} -- skipped, did not actually run' -f $flag) -ForegroundColor Red
            continue
        }
    }

    Write-Host ('  ok    {0}' -f $flag) -ForegroundColor DarkGray
}
