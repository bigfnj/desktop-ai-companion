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

    Every child runs with a PRIVATE TEMP: the log directory below is handed to the exe as TEMP/TMP.
    The marker names are fixed by the flag and the exe writes them under Path.GetTempPath(), so two
    same-user runners on one box -- the gate in the main checkout and a worktree's gate or a mutation
    harness -- shared one path and could grade each other's build in the ~10-35 ms between the
    exe's write and this script's read (F418, measured 2026-09-29). The wider overlaps already
    failed safe as "wrote no marker file"; this closes the narrow one.

.PARAMETER ExecutablePath
    The built DesktopAICompanion.exe.

.PARAMETER OutputRoot
    The build output root holding modules\<id>. When supplied, every module named in
    $RequiredModules must be present, because a self-test whose module folder is absent skip-PASSES
    and a build that silently produced no modules would otherwise look identical to a clean run.

.PARAMETER LogDirectory
    Where to put captured child output and where the child writes its markers: it is ALSO the
    child's TEMP for the duration of the run. Defaults to a fresh dp-selftests-run-<guid> directory
    under $env:TEMP, removed at the end when every flag passed and kept (and named) when one did not.

.PARAMETER TimeoutSeconds
    How long one flag may run before it is killed and reported as a failure. Default 180.

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
    [string]$LogDirectory,
    # Per flag. The wait was unbounded until 2026-09-30, so a self-test that hung -- a modal dialog
    # reached by regression in --wpf-options-selftest, a device open that never returns in
    # --audio-selftest -- blocked the local gate for ever and CI until its 30-minute job timeout, with
    # no failure line naming the flag (F398). Test-ModuleSelfTests.ps1 already carried this bound.
    [int]$TimeoutSeconds = 180
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

# One directory per run, and it is ALSO the child's TEMP (set below, before the loop). The markers
# are written by the exe under Path.GetTempPath() and read from $LogDirectory, two things that only
# ever agreed because both defaulted to %TEMP%: a caller's -LogDirectory used to be silently ignored
# by the exe, which then wrote its markers somewhere this script never looked. The dp- prefix means an
# aged leftover -- a run killed half-way, a red run's kept directory -- is collected by the sweep at
# the end of the next run.
#
# SHORT NAME, DELIBERATELY. Test-ModuleSelfTests.ps1 went red under a 'dp-module-selftests-run-<32 hex>'
# directory: the fortunes VectorCache replace-fallback probe builds a temp path ~150 characters below
# TEMP and ends in a MoveFileEx P/Invoke with no long-path prefix, so a TEMP of 107 characters put it
# at MAX_PATH and the save was lost (measured 2026-09-29; filed as a Fortunes/ModuleKit item). A
# runner must not fail a self-test on a path length it never sees in production.
$autoLogDirectory = [string]::IsNullOrWhiteSpace($LogDirectory)
if ($autoLogDirectory) {
    $LogDirectory = Join-Path $env:TEMP ('dp-str-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
}

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
    # NO --module-selftest=<id> ROWS. The convention-based module self-tests (the host loading a
    # module through the REAL loader and calling its public static bool SelfTest) ran here for four
    # modules from 2026-09-17 and, from 2026-09-27, ran AGAIN for all seven covered modules in
    # tests\Test-ModuleSelfTests.ps1, which both the gate and CI call right after this script. That
    # was ~12.5 s of duplicated work per run and two chances for one flake to redden a gate (F397).
    # The second runner is the stronger one -- it grades SKIP lines, a PASS with no assertions, a
    # marker that could not be deleted, a timeout, and the exact failure set of every uncovered
    # module -- so the rows came out of here. $RequiredModules above still guards every module
    # folder, because the flags that remain load modules too.
}

Write-Output ($CountPrefix + $SelfTestFlags.Count)

if (-not (Test-Path -LiteralPath $ExecutablePath)) {
    Write-Output ($FailurePrefix + "the executable is missing: $ExecutablePath")
    return
}

if ($OutputRoot) {
    foreach ($moduleId in $RequiredModules) {
        # The DLL, not the folder. Checking the directory is what let a stale build keep the gate
        # green: under the documented `run-gate.ps1 -SkipClean` the previous build's output is still
        # on disk, so a module whose .csproj moved -- and which therefore was not compiled at all --
        # still had its folder, still loaded its OLD DLL, and still printed ok. build.ps1 now refuses
        # to start with a missing project; this is the second half, for the case where the project
        # exists but its output did not land.
        $moduleDirectory = Join-Path $OutputRoot (Join-Path 'modules' $moduleId)
        if (-not (Test-Path -LiteralPath $moduleDirectory)) {
            Write-Output ($FailurePrefix + "module '$moduleId' is missing from the build output; its self-test would skip-pass")
            continue
        }
        # Anchored on the .deps.json, which names the module's OWN assembly. Counting *.dll is not
        # enough: a module folder normally also carries DesktopAICompanion.ModuleKit.dll, so a folder
        # whose module DLL is gone still holds one and an any-DLL count passes.
        #
        # AN EMPTY SET IS AN ANSWER, NOT A SKIP. This loop used to contribute nothing at all when a
        # folder held no *.deps.json -- which is the "a check that ran nothing reported success"
        # shape this whole file exists to kill, sitting inside it.
        #
        # And it fires for real: modules\TestModule\TestModule.csproj sets
        # GenerateDependencyFile=false and testmodule IS in $RequiredModules, so the loop no-opped on
        # one of the eight on every run. That is also why a blanket "no deps file = failure" floor is
        # the WRONG fix -- it would red the gate immediately on a module that is correct. The opt-out
        # is named here instead, so adding a second one is a deliberate edit rather than a silent
        # gap. testmodule also carries no ModuleKit.dll (it references only Contracts, with
        # Private="false"), which is why the comment above says "normally".
        $depsOptOut = @('testmodule')
        $depsFiles = @(Get-ChildItem -LiteralPath $moduleDirectory -Filter '*.deps.json' -File -ErrorAction SilentlyContinue)
        if ($depsFiles.Count -eq 0) {
            if ($depsOptOut -notcontains $moduleId) {
                Write-Output ($FailurePrefix + "module '$moduleId' has no *.deps.json in the build output, so nothing here can confirm its own assembly was built; add it to `$depsOptOut only if the project sets GenerateDependencyFile=false on purpose")
            }
        }
        elseif ($depsOptOut -contains $moduleId) {
            # The opt-out went stale: the project now emits a deps file, so the exemption is hiding a
            # check that could run.
            Write-Output ($FailurePrefix + "module '$moduleId' is listed in `$depsOptOut but DOES emit $($depsFiles[0].Name); remove it from the opt-out so its assembly is checked")
        }
        foreach ($deps in $depsFiles) {
            $assemblyName = $deps.Name -replace '\.deps\.json$', ''
            if (-not (Test-Path -LiteralPath (Join-Path $moduleDirectory ($assemblyName + '.dll')))) {
                Write-Output ($FailurePrefix + "module '$moduleId' has $($deps.Name) but no $assemblyName.dll; it was not built into the output and its self-test would load nothing or a stale copy")
            }
        }
    }
}

Write-Host ('=== app self-tests ({0} flags)' -f $SelfTestFlags.Count) -ForegroundColor Cyan
# Start-Process has no -Environment under Windows PowerShell 5.1 (this file is #requires -Version 5),
# so the child's TEMP/TMP are set on THIS process, inherited by every child, and restored in the
# finally. Path.GetTempPath() reads TMP first, then TEMP, so both move: the markers, the
# SelfTestScratch roots and ModuleKit's temp fallbacks all land in $LogDirectory.
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
$env:TEMP = $LogDirectory
$env:TMP = $LogDirectory
$anyFailure = $false
try {
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
            #
            # CAUGHT, then ASSERTED, the shape Test-ModuleSelfTests.ps1 settled on. An unguarded Delete
            # under the caller's $ErrorActionPreference = 'Stop' escaped this script as a raw exception
            # and took run-gate.ps1 down with it -- no GATE FAILED summary, every later section skipped
            # -- while an ad-hoc run under the default preference sailed past the failed delete and
            # graded the PREVIOUS run's marker as this one's (F407). The Test-Path after the catch is
            # the half that matters: swallowing the exception alone would turn the abort into that
            # silent stale grading.
            try { [System.IO.File]::Delete($markerPath) } catch { }
            if (Test-Path -LiteralPath $markerPath) {
                $anyFailure = $true
                Write-Output ($FailurePrefix + ('{0} (stale marker could not be deleted, so any verdict would be the previous run''s: {1})' -f $flag, $markerPath))
                Write-Host ('  FAIL  {0} -- stale marker' -f $flag) -ForegroundColor Red
                continue
            }
        }

        # A GUI-subsystem exe does not block PowerShell, so wait explicitly: `& $exe` returns
        # immediately with no exit code. Child output is captured rather than inherited, because these
        # self-tests print hundreds of PASS lines each and would bury the summary.
        #
        # BOUNDED. -PassThru without -Wait, then WaitForExit with the budget: a self-test that never
        # returns is killed and reported as its own failure line naming the flag, instead of holding
        # the gate until someone notices (F398). $process.Kill(), not Kill($true): the entire-tree
        # overload does not exist on the .NET Framework that Windows PowerShell 5.1 runs on. The
        # redirects stay on files, so the repo's synchronous-drain rule is untouched. A launch that
        # cannot start at all (exe locked by a concurrent rebuild, a redirect target held open) is a
        # failure line too, not an escape (F407).
        $log = Join-Path $LogDirectory ('dp-gate-' + $flag.Trim('-') + '.log')
        $process = $null
        try {
            $process = Start-Process -FilePath $ExecutablePath -ArgumentList $flag -PassThru -NoNewWindow `
                -RedirectStandardOutput $log -RedirectStandardError "$log.err"
        }
        catch {
            $anyFailure = $true
            Write-Output ($FailurePrefix + ('{0} (could not start: {1})' -f $flag, $_.Exception.Message))
            Write-Host ('  FAIL  {0} -- could not start' -f $flag) -ForegroundColor Red
            continue
        }
        # Cache the handle NOW, while the child is alive. Without -Wait, the Process that -PassThru
        # returns reads ExitCode as $null once the child has gone unless its handle was touched first:
        # the first run of this shape graded all fifteen flags "(exit )" (measured 2026-09-30).
        $null = $process.Handle
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill() } catch { }
            try { [void]$process.WaitForExit(5000) } catch { }
            $anyFailure = $true
            Write-Output ($FailurePrefix + ('{0} (did not exit in {1} s; killed)' -f $flag, $TimeoutSeconds))
            Write-Host ('  FAIL  {0} -- did not exit in {1} s' -f $flag, $TimeoutSeconds) -ForegroundColor Red
            foreach ($logPath in @($log, "$log.err")) {
                if (-not (Test-Path -LiteralPath $logPath)) { continue }
                Get-Content -LiteralPath $logPath | Select-Object -Last 20 | ForEach-Object { Write-Host "        $_" }
            }
            continue
        }

        if ($process.ExitCode -ne 0) {
            $anyFailure = $true
            Write-Output ($FailurePrefix + ('{0} (exit {1})' -f $flag, $process.ExitCode))
            Write-Host ('  FAIL  {0}' -f $flag) -ForegroundColor Red
            # THE FAILING LINES FIRST, then the tail.
            #
            # The tail alone is not a diagnosis. SelfTestProbe appends each result WHERE IT
            # HAPPENS, so in a suite of a hundred-odd assertions the one that failed is usually
            # nowhere near the end -- and the last 40 lines are then forty passes and a summary
            # saying something failed, without saying what. That is exactly what CI reported for
            # --module-selftest=agentflow on 2026-09-21: RESULT=FAIL over a window containing no
            # FAIL line at all, which is unactionable from a log nobody can re-run locally.
            #
            # Capped, because a module that fails every assertion must not print a thousand
            # lines into a CI log; the tail still follows for context.
            foreach ($logPath in @($log, "$log.err")) {
                if (-not (Test-Path -LiteralPath $logPath)) { continue }
                $lines = @(Get-Content -LiteralPath $logPath)
                $bad = @($lines | Select-String -Pattern "(FAIL|EXC|SKIP):" -SimpleMatch:$false)
                if ($bad.Count -gt 0) {
                    Write-Host ("        ---- {0} failing line(s) ----" -f $bad.Count)
                    foreach ($line in ($bad | Select-Object -First 25)) {
                        Write-Host ("        {0}" -f $line.Line)
                    }
                    if ($bad.Count -gt 25) {
                        Write-Host ("        ... and {0} more" -f ($bad.Count - 25))
                    }
                    Write-Host "        ---- tail ----"
                }
                $lines | Select-Object -Last 40 | ForEach-Object { Write-Host "        $_" }
            }
            continue
        }

        if ($markerPath) {
            if (-not (Test-Path -LiteralPath $markerPath)) {
                $anyFailure = $true
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
                $anyFailure = $true
                Write-Output ($FailurePrefix + ('{0} (SKIPPED: {1})' -f $flag, $skips[0].Line.Trim()))
                Write-Host ('  FAIL  {0} -- skipped, did not actually run' -f $flag) -ForegroundColor Red
                continue
            }
        }

        Write-Host ('  ok    {0}' -f $flag) -ForegroundColor DarkGray
    }
}
finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    if ($autoLogDirectory) {
        if ($anyFailure) {
            # The failing lines and the tail were printed above; the whole log stays for the case where
            # forty lines were not enough. Named, so it can be found and so its removal is deliberate.
            Write-Host ('  child logs and markers kept for inspection: ' + $LogDirectory)
        }
        else {
            # Every child has exited, so nothing maps the staged module copies any more; the delete
            # succeeds where SelfTestScratch.TryRelease inside the child could not. Best-effort: a
            # directory that survives carries the dp- prefix the sweep below collects next time.
            try { [System.IO.Directory]::Delete($LogDirectory, $true) } catch { }
        }
    }
    # The children's SelfTestScratch sweep now runs inside the per-run directory, so aged dp-* roots
    # in the REAL %TEMP% -- a self-test flag run by hand, a runner killed mid-way, a red run's kept
    # directory -- would never be collected again without this. Same rule as
    # SelfTestScratch.SweepOldRoots: dp- prefix, directories only, older than an hour (nothing that
    # old belongs to a live run), best-effort per directory.
    try {
        $cutoff = (Get-Date).ToUniversalTime().AddHours(-1)
        foreach ($aged in @(Get-ChildItem -LiteralPath $previousTemp -Directory -Filter 'dp-*' -ErrorAction SilentlyContinue)) {
            if ($aged.LastWriteTimeUtc -gt $cutoff) { continue }
            try { [System.IO.Directory]::Delete($aged.FullName, $true) } catch { }
        }
    }
    catch { }
}
