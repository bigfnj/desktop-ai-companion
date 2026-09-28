#requires -Version 5
<#
.SYNOPSIS
    Bounded leak soak: churn the pet's real windows, speech bubbles, tray icon and menus, and assert that
    the process's OS-level resource usage does not grow.

.DESCRIPTION
    This is the only gate that can actually catch a leak. Every other check in the repo asserts an invariant
    inside the process; this one drives the app from OUTSIDE and watches the counters the operating system
    keeps: handle count, GDI objects, USER objects and private bytes, sampled via user32!GetGuiResources.
    A managed-memory check (GC.GetTotalMemory and friends) would miss exactly the failures that matter here,
    because an undisposed Bitmap, Font, Icon or HWND is native.

    Two legs. First, launch normally and require a responsive message loop (WaitForInputIdle) -- a plain
    "does it start" smoke test. Then relaunch with --resource-churn-selftest, which makes the app run its own
    in-process churn loop (Program.RuntimeResourceChurn) while this script samples it once a second, and
    finally publish resource-churn-result.json. Everything runs against an isolated DESKTOP_AI_COMPANION_DATA_ROOT in
    %TEMP% (enforced on both sides -- the exe refuses to churn outside that path) and the scratch tree is
    removed in the finally block.

    HISTORY, so this is not deleted a second time: the in-process harness ships in the product, but this
    driver was removed in 5d36ab3 as an unreferenced script three hours after CI stopped calling it, which
    left the app's only leak gate with no way to run. Restored for the v1.5.0 freeze. It is deliberately NOT
    in the blocking CI path: it needs a real window station and OS growth thresholds are the flakiest
    assertion available. Run it before tagging (see docs/RELEASE-CHECKLIST.md) or via the manual
    resource-soak workflow.

.EXAMPLE
    .\tests\runtime-resource-soak.ps1
.EXAMPLE
    .\tests\runtime-resource-soak.ps1 -DurationSeconds 60 -MaximumHandleGrowth 8
#>
[CmdletBinding()]
param(
    [string]$ExecutablePath,
    [ValidateRange(1, 30)][int]$EnabledStartupDeadlineSeconds = 10,
    [ValidateRange(1, 120)][int]$StabilizationSeconds = 10,
    # Any value here is valid: the growth bounds below scale with the number of settled intervals a
    # run produces, so a longer run is a longer OBSERVATION rather than a stricter test. That was not
    # true before 2026-09-27 and is the reason this comment exists.
    [ValidateRange(10, 600)][int]$DurationSeconds = 30,
    [ValidateRange(10, 300)][int]$CompletionGraceSeconds = 60,
    [ValidateRange(250, 10000)][int]$SampleIntervalMilliseconds = 1000,
    [ValidateRange(0, 1000)][int]$MaximumHandleGrowth = 16,
    [ValidateRange(0, 1000)][int]$MaximumGdiGrowth = 16,
    [ValidateRange(0, 1000)][int]$MaximumUserGrowth = 16,
    [ValidateRange(0, 1073741824)][long]$MaximumPrivateByteGrowth = 64MB
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $repoRoot (
        'build\DesktopAICompanionPortable\bin\Release\x64\DesktopAICompanion.exe')
}
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$scratchRoot = Join-Path $resolvedTemp (
    'DesktopAICompanion-ResourceSoak-' + [Guid]::NewGuid().ToString('N'))
$churnMarker = Join-Path $scratchRoot 'resource-churn-result.json'
$churnTargetSeconds = $StabilizationSeconds + $DurationSeconds
$churnIntervalMilliseconds = 250
$churnCycles = [Math]::Max(
    12,
    [Math]::Min(100, [Math]::Ceiling($DurationSeconds / 2)))
$churnMinimumDurationMilliseconds = $churnTargetSeconds * 1000
# The ceiling on the child's self-extension. The churn keeps cycling past its minimum duration until
# it has published two settled samples (RuntimeResourceChurn.CycleTimer_Tick says why), so this has
# to sit INSIDE the completion deadline below -- otherwise a slow box would trade a specific failure
# for the generic "did not publish its completion marker", which is the less useful of the two.
$churnMaximumDurationMilliseconds = [Math]::Max(
    $churnMinimumDurationMilliseconds,
    (($DurationSeconds + $CompletionGraceSeconds) * 1000) - 15000)
$churnExitDelayMilliseconds = [Math]::Min(
    30000,
    [Math]::Max(5000, ($SampleIntervalMilliseconds * 2) + 1000))
$originalDataRoot = $env:DESKTOP_AI_COMPANION_DATA_ROOT
$process = $null
$startupProcess = $null
$samples = New-Object 'Collections.Generic.List[object]'

if (-not ('DesktopAICompanion.ResourceSoak.NativeMethods' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace DesktopAICompanion.ResourceSoak
{
    public static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetGuiResources(IntPtr process, uint flags);
    }
}
'@
}

function Stop-TestProcess {
    param([Parameter(Mandatory = $true)][Diagnostics.Process]$Process)

    try {
        if ($Process.HasExited) { return }
    }
    catch {
        return
    }

    try {
        [void]$Process.CloseMainWindow()
        if ($Process.WaitForExit(2000)) { return }
    }
    catch {
    }

    $taskKill = Join-Path $env:SystemRoot 'System32\taskkill.exe'
    if (Test-Path -LiteralPath $taskKill -PathType Leaf) {
        # CAUGHT, because this function runs from the finally block. A native command writing to
        # stderr raises a terminating NativeCommandError under this script's ErrorActionPreference,
        # and an exception thrown in a finally REPLACES the one already in flight -- so a real soak
        # failure would surface as "taskkill.exe : ERROR: The process NNNN not found" instead. That
        # is not hypothetical: it swallowed two mutation-test verdicts on 2026-09-28, and taskkill
        # writes exactly that line whenever the process has already exited, which is the normal case
        # on the failure paths this cleanup exists to serve.
        try { & $taskKill /PID $Process.Id /T /F 2>&1 | Out-Null } catch { }
        try { [void]$Process.WaitForExit(5000) } catch { }
    }
    else {
        try {
            $Process.Kill()
            [void]$Process.WaitForExit(5000)
        }
        catch {
        }
    }
}

function Get-ResourceSample {
    param([Parameter(Mandatory = $true)][Diagnostics.Process]$Process)

    $Process.Refresh()
    if ($Process.HasExited) {
        throw "DesktopAICompanion exited during the resource soak with code $($Process.ExitCode)."
    }

    return [pscustomobject][ordered]@{
        ElapsedMilliseconds = $stopwatch.ElapsedMilliseconds
        Handles = $Process.HandleCount
        GdiObjects = [DesktopAICompanion.ResourceSoak.NativeMethods]::GetGuiResources(
            $Process.Handle, 0)
        UserObjects = [DesktopAICompanion.ResourceSoak.NativeMethods]::GetGuiResources(
            $Process.Handle, 1)
        PrivateBytes = $Process.PrivateMemorySize64
        WorkingSet = $Process.WorkingSet64
    }
}

try {
    $runningCopies = @(
        Get-Process -Name 'DesktopAICompanion' -ErrorAction SilentlyContinue |
            Where-Object {
                try {
                    [string]::Equals(
                        $_.Path,
                        $resolvedExecutable,
                        [StringComparison]::OrdinalIgnoreCase)
                }
                catch {
                    $false
                }
            }
    )
    if ($runningCopies.Count -gt 0) {
        throw 'Refusing the resource soak because this DesktopAICompanion executable is already running.'
    }

    New-Item -ItemType Directory -Path $scratchRoot -Force | Out-Null

    # No settings are seeded any more. This used to write an ai-settings.json with SmartFortunes /
    # AiBrainEnabled / IdleCommentaryEnabled to steer the run, but every one of those keys moved into a
    # module (S3d, S4b) and the base has not read them since. A fresh isolated data root is the correct
    # starting state: no modules configured, nothing enabled, defaults everywhere.
    $env:DESKTOP_AI_COMPANION_DATA_ROOT = $scratchRoot
    $startupInfo = New-Object Diagnostics.ProcessStartInfo
    $startupInfo.FileName = $resolvedExecutable
    $startupInfo.WorkingDirectory = Split-Path $resolvedExecutable -Parent
    $startupInfo.UseShellExecute = $false
    $startupInfo.CreateNoWindow = $true
    $startupInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $startupProcess = New-Object Diagnostics.Process
    $startupProcess.StartInfo = $startupInfo
    if (-not $startupProcess.Start()) {
        throw 'The startup process could not be started.'
    }
    if (-not $startupProcess.WaitForInputIdle(
            $EnabledStartupDeadlineSeconds * 1000)) {
        throw "Startup did not enter a responsive message loop within $EnabledStartupDeadlineSeconds seconds."
    }
    if ($startupProcess.HasExited) {
        throw "DesktopAICompanion exited during startup with code $($startupProcess.ExitCode)."
    }
    Stop-TestProcess -Process $startupProcess
    $startupProcess.Dispose()
    $startupProcess = $null

    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $resolvedExecutable
    $startInfo.Arguments = '--resource-churn-selftest'
    $startInfo.WorkingDirectory = Split-Path $resolvedExecutable -Parent
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.EnvironmentVariables['DESKTOP_AI_COMPANION_DATA_ROOT'] = $scratchRoot
    $startInfo.EnvironmentVariables['DESKTOPPET_RESOURCE_CHURN_CYCLES'] =
        [string]$churnCycles
    $startInfo.EnvironmentVariables['DESKTOPPET_RESOURCE_CHURN_INTERVAL_MS'] =
        [string]$churnIntervalMilliseconds
    $startInfo.EnvironmentVariables['DESKTOPPET_RESOURCE_CHURN_MIN_DURATION_MS'] =
        [string]$churnMinimumDurationMilliseconds
    $startInfo.EnvironmentVariables['DESKTOPPET_RESOURCE_CHURN_MAX_DURATION_MS'] =
        [string]$churnMaximumDurationMilliseconds
    $startInfo.EnvironmentVariables['DESKTOPPET_RESOURCE_CHURN_EXIT_DELAY_MS'] =
        [string]$churnExitDelayMilliseconds
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw 'The isolated DesktopAICompanion process could not be started.'
    }
    $env:DESKTOP_AI_COMPANION_DATA_ROOT = $originalDataRoot

    if ($process.WaitForExit($StabilizationSeconds * 1000)) {
        throw "DesktopAICompanion exited during stabilization with code $($process.ExitCode)."
    }

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $completionDeadline =
        ($DurationSeconds + $CompletionGraceSeconds) * 1000
    while ($stopwatch.ElapsedMilliseconds -lt $completionDeadline) {
        $samples.Add((Get-ResourceSample -Process $process))
        if (Test-Path -LiteralPath $churnMarker -PathType Leaf) {
            break
        }
        Start-Sleep -Milliseconds $SampleIntervalMilliseconds
    }
    if (-not (Test-Path -LiteralPath $churnMarker -PathType Leaf)) {
        throw "The dynamic resource churn did not publish its completion marker after $($stopwatch.Elapsed.TotalSeconds.ToString('F1')) seconds."
    }
    if (-not $process.HasExited) {
        $samples.Add((Get-ResourceSample -Process $process))
    }

    $churn = Get-Content -LiteralPath $churnMarker -Raw |
        ConvertFrom-Json
    # The counters are DISCOVERED, not listed. A hardcoded list is what broke this script: it demanded
    # optionsCycles / optionsCancellationCycles / aboutCycles / helpCycles, and those disappeared from the
    # harness when the WinForms FormOptions, AboutBox and FormHelp were retired for WPF (742b0ff, 343336d),
    # so a verbatim restore would fail on a healthy build. Enumerating every *Cycles field keeps the
    # anti-false-PASS property -- the run cannot claim PASS while a churn path silently did nothing -- and
    # picks up new counters automatically as the harness grows.
    #
    # 'cycles' and 'targetCycles' are the loop's own control values, asserted separately below, and are
    # excluded: the harness may legitimately run MORE cycles than requested, which would make an equality
    # check against targetCycles fail for the wrong reason.
    # The marker and this driver ship together, and -ExecutablePath makes it easy to point a current
    # driver at an older build. Without this the first missing field surfaces as a bare
    # PropertyNotFoundStrict from Set-StrictMode, which names a PowerShell rule rather than the cause
    # -- observed 2026-09-28 doing exactly that. Checked by NAME rather than by suffix because these
    # are verdict inputs, not the discovered per-step counters below.
    $markerFields = @($churn.PSObject.Properties | ForEach-Object { $_.Name })
    foreach ($field in @('settledSeries', 'minimumSettledSamples',
                         'maximumDurationMilliseconds', 'extendedForSettledSamples')) {
        if ($markerFields -notcontains $field) {
            throw ("The resource churn marker carries no '$field', so it was written by a build older " +
                   'than this driver and its verdict cannot be read. Rebuild, or point ' +
                   "-ExecutablePath at a current build: $resolvedExecutable")
        }
    }

    $controlFields = @('cycles', 'targetCycles')
    $requiredCounters = @(
        $churn.PSObject.Properties |
            Where-Object {
                $_.Name -like '*Cycles' -and $controlFields -notcontains $_.Name
            } |
            ForEach-Object { $_.Name }
    )
    foreach ($expected in @('speechAndPetCycles', 'trayAndMenuCycles')) {
        if ($requiredCounters -notcontains $expected) {
            throw "The resource churn marker is missing the '$expected' counter; the harness no longer exercises that path."
        }
    }
    $actualChurnCycles = [int]$churn.cycles
    if ($churn.result -ne 'PASS' -or
        $actualChurnCycles -lt $churnCycles -or
        [int]$churn.targetCycles -ne $churnCycles -or
        [long]$churn.elapsedMilliseconds -lt
            $churnMinimumDurationMilliseconds -or
        [long]$churn.minimumDurationMilliseconds -ne
            $churnMinimumDurationMilliseconds) {
        throw "The dynamic resource churn marker reported failure: $($churn | ConvertTo-Json -Compress)."
    }
    foreach ($counter in $requiredCounters) {
        if ([int]$churn.$counter -ne $actualChurnCycles) {
            throw "The dynamic resource churn counter '$counter' was $($churn.$counter), expected $actualChurnCycles."
        }
    }

    if (-not $process.WaitForExit($churnExitDelayMilliseconds + 5000)) {
        throw 'The dynamic resource churn did not exit after publishing its completion marker.'
    }
    if ($process.ExitCode -ne 0) {
        throw "The dynamic resource churn exited with code $($process.ExitCode)."
    }
    $stopwatch.Stop()

    if ($samples.Count -lt 2) {
        throw 'The resource soak produced too few samples.'
    }
    $first = $samples[0]
    $last = $samples[$samples.Count - 1]
    $sampledDurationMilliseconds =
        [long]$last.ElapsedMilliseconds -
        [long]$first.ElapsedMilliseconds
    if ($sampledDurationMilliseconds -lt ($DurationSeconds * 1000)) {
        throw "Dynamic resource churn was sampled for only $sampledDurationMilliseconds ms; expected at least $($DurationSeconds * 1000) ms."
    }
    $growth = [pscustomobject][ordered]@{
        Handles = [long]$last.Handles - [long]$first.Handles
        GdiObjects = [long]$last.GdiObjects - [long]$first.GdiObjects
        UserObjects = [long]$last.UserObjects - [long]$first.UserObjects
        PrivateBytes = [long]$last.PrivateBytes - [long]$first.PrivateBytes
        WorkingSet = [long]$last.WorkingSet - [long]$first.WorkingSet
    }

    if ($first.GdiObjects -lt 1 -or $first.UserObjects -lt 1) {
        throw 'Windows GUI resource counters were unavailable for the running process.'
    }

    # ---- the leak verdict: the SETTLED series, not the raw sawtooth (BUG-004) --------------------
    #
    # This used to assert raw first-vs-last GDI/USER/handle growth, and that is not a leak test.
    # Bitmap, Font, Icon and Form hold their native handles until a FINALIZER runs, so the raw
    # counters are a sawtooth and the verdict depended entirely on where the last sample happened to
    # land relative to a collection. Measured 2026-09-10 on one unchanged build: raw GDI growth came
    # out +81, +206, -22 and -3 on four runs of different lengths -- two FAILs and two PASSes for
    # identical code. The pre-1.0.0 baseline of "GDI -24" recorded in docs/HISTORY-pre-1.0.0.md was a
    # run that ended just after a collection, so it never demonstrated health either.
    #
    # The app now forces GC -> WaitForPendingFinalizers -> GC before sampling, periodically during the
    # run (RuntimeResourceChurn.SettleAndSample), and publishes the series. That answers the question
    # this gate exists to ask: after everything collectable HAS been collected, does the process keep
    # losing handles?
    #
    # Compared from the FIRST post-warm-up sample rather than from the cold baseline, deliberately.
    # The first pet legitimately and permanently costs a sprite decode, cached fonts and brushes, a
    # bubble region and the tray icon: measured at +31 GDI, reached by cycle 40 and then EXACTLY flat
    # (46, 46, 46 ...) for all 548 cycles of a three-minute run. Charging that one-time cost to a leak
    # is what made this gate cry wolf; charging it to nothing at all would let a real leak hide inside
    # the allowance. Warm-up is therefore excluded by construction, and anything growing after it
    # fails -- which is the same reasoning module-window-soak.ps1 already uses when it compares its
    # LAST segment against the previous one instead of against a cold start.
    $settled = @($churn.settledSeries)
    # A run that had to extend itself SAYS so, every time. The extension is invisible in the verdict
    # -- the growth bounds scale with the intervals observed -- so without this line a box that is
    # quietly too slow to hit the sample floor on schedule would look identical to a fast one.
    if ($churn.extendedForSettledSamples) {
        Write-Host ("  note: the churn extended past its $churnMinimumDurationMilliseconds ms minimum " +
                    "to reach $($settled.Count) settled sample(s), finishing at " +
                    "$([long]$churn.elapsedMilliseconds) ms.")
    }
    if ($settled.Count -lt [int]$churn.minimumSettledSamples) {
        throw ("The churn published $($settled.Count) settled sample(s) in " +
               "$([long]$churn.elapsedMilliseconds) ms; at least $([int]$churn.minimumSettledSamples) " +
               'are needed to tell a warming cache from a leak. The run already extends itself past ' +
               'its minimum duration to reach that floor, so this means the ceiling of ' +
               "$([long]$churn.maximumDurationMilliseconds) ms was hit first. Raise " +
               '-CompletionGraceSeconds, which is what the ceiling is derived from.')
    }
    $settledFirst = $settled[0]
    $settledLast = $settled[$settled.Count - 1]
    $settledGrowth = [pscustomobject][ordered]@{
        FromCycle   = [int]$settledFirst.cycle
        ToCycle     = [int]$settledLast.cycle
        GdiObjects  = [int]$settledLast.gdi - [int]$settledFirst.gdi
        UserObjects = [int]$settledLast.user - [int]$settledFirst.user
        Handles     = [int]$settledLast.handles - [int]$settledFirst.handles
    }
    if ($settledGrowth.ToCycle -le $settledGrowth.FromCycle) {
        throw 'The settled samples are not ordered by cycle; the churn marker is malformed.'
    }
    # THE BOUND IS A RATE, NOT A TOTAL, AND THAT IS A CORRECTION.
    #
    # These bounds used to be compared, as absolute counts, against growth measured from the first
    # settled sample to the last. Growth accumulates with cycle count, so the pass mark was set by
    # -DurationSeconds rather than by the code: a long enough run failed ANY build. Measured
    # 2026-09-25 -- v1.2.4, the SHIPPED release, passes a 180-second run once and fails the next at
    # +95, while the same build passes the documented 30-second default every time. That cost most of
    # an afternoon: a phantom regression was bisected across single samples and a good fix was
    # briefly reverted before the released build disproved it.
    #
    # The defaults are calibrated for ONE settled interval, which is what the default duration
    # produces (cycles 40 and 80). Scaling by the number of intervals actually observed keeps exactly
    # that tolerance -- the same permitted growth per interval -- while making every duration
    # comparable instead of quietly stricter. A leak faster than the calibrated rate still fails at
    # any length, which is the property worth having.
    #
    # Its sibling tests\module-window-soak.ps1 avoids this by comparing the LAST segment against the
    # previous one rather than against a cold start. Same idea, arrived at independently.
    $settledSpacing = [int]$settled[1].cycle - [int]$settled[0].cycle
    if ($settledSpacing -le 0) {
        throw 'The settled samples are not spaced by a positive number of cycles; the marker is malformed.'
    }
    $observedIntervals = [Math]::Max(
        1, [int][Math]::Round(($settledGrowth.ToCycle - $settledGrowth.FromCycle) / $settledSpacing))

    foreach ($counter in @('GdiObjects', 'UserObjects', 'Handles')) {
        $perInterval = switch ($counter) {
            'GdiObjects'  { $MaximumGdiGrowth }
            'UserObjects' { $MaximumUserGrowth }
            'Handles'     { $MaximumHandleGrowth }
        }
        $bound = $perInterval * $observedIntervals
        $value = $settledGrowth.$counter
        if ($value -gt $bound) {
            throw ("Post-finalization $counter growth exceeded the bound between cycle " +
                   "$($settledGrowth.FromCycle) and $($settledGrowth.ToCycle): $value > $bound " +
                   "($perInterval per settled interval x $observedIntervals observed). " +
                   'It survived a forced collection, so this is a real leak and not finalizer lag.')
        }
    }

    # n>1, PUBLISHED BESIDE THE NUMBER.
    #
    # A single settled delta from this harness is weak evidence in BOTH directions. Across identical
    # configurations on one unchanged build the handle figure came out +82, -39, -19, +95, -18, -32
    # and +1, while GDI and USER over those same runs sat at 0, 0, -6 and 2. Handles do not settle
    # the way the other two do, even after the forced GC + WaitForPendingFinalizers + GC that BUG-004
    # documents as the answer to exactly this. That asymmetry is how an afternoon went into bisecting
    # a handle regression that did not exist -- and it equally means a slow real leak can hide inside
    # the noise for several runs.
    #
    # The BOUND is deliberately left alone. Changing a pass criterion on a metric this noisy, without
    # data to set the new one, is the shape of the mistake this text exists to describe. What changes
    # is that the headline delta is no longer reported alone: the per-interval series ships with it,
    # so a reader can see the spread the single number came out of.
    $intervalSeries = @()
    for ($i = 1; $i -lt $settled.Count; $i++) {
        $intervalSeries += [pscustomobject][ordered]@{
            FromCycle   = [int]$settled[$i - 1].cycle
            ToCycle     = [int]$settled[$i].cycle
            GdiObjects  = [int]$settled[$i].gdi - [int]$settled[$i - 1].gdi
            UserObjects = [int]$settled[$i].user - [int]$settled[$i - 1].user
            Handles     = [int]$settled[$i].handles - [int]$settled[$i - 1].handles
        }
    }
    $medianOf = {
        param([int[]]$values)
        if ($values.Count -eq 0) { return $null }
        $sorted = @($values | Sort-Object)
        $mid = [int][Math]::Floor($sorted.Count / 2)
        if ($sorted.Count % 2 -eq 1) { return $sorted[$mid] }
        return [int][Math]::Round(($sorted[$mid - 1] + $sorted[$mid]) / 2)
    }
    $perInterval = [pscustomobject][ordered]@{
        Intervals = $intervalSeries.Count
        # A median needs three or more to mean anything. Below that this SAYS n=1 rather than
        # dressing one observation up as a summary statistic, which is the whole complaint.
        Confidence = $(if ($intervalSeries.Count -ge 3) {
                'median of the per-interval deltas'
            } else {
                "n=$($intervalSeries.Count) -- treat as a HINT; repeat the run before acting on a surprise"
            })
        MedianGdiObjects  = (& $medianOf @($intervalSeries | ForEach-Object { $_.GdiObjects }))
        MedianUserObjects = (& $medianOf @($intervalSeries | ForEach-Object { $_.UserObjects }))
        MedianHandles     = (& $medianOf @($intervalSeries | ForEach-Object { $_.Handles }))
        Series = $intervalSeries
    }

    # Private bytes are NOT finalizer-bound in the same way and stay on the raw samples.
    if ($growth.PrivateBytes -gt $MaximumPrivateByteGrowth) {
        throw "Private-byte growth exceeded the bound: $($growth.PrivateBytes) > $MaximumPrivateByteGrowth."
    }

    [pscustomobject][ordered]@{
        Result = 'PASS'
        ResponsiveStartup = 'PASS'
        CountersAsserted = $requiredCounters
        DynamicResourceChurn = $churn
        Samples = $samples.Count
        DurationSeconds = [Math]::Round(
            $sampledDurationMilliseconds / 1000,
            1)
        First = $first
        Last = $last
        Growth = $growth
        SettledGrowth = $settledGrowth
        # Ships WITH SettledGrowth, never instead of it: the point is that the headline delta is
        # read next to the spread it came from.
        SettledPerInterval = $perInterval
    } | ConvertTo-Json -Depth 6
}
finally {
    $env:DESKTOP_AI_COMPANION_DATA_ROOT = $originalDataRoot
    if ($null -ne $process) {
        Stop-TestProcess -Process $process
        $process.Dispose()
    }
    if ($null -ne $startupProcess) {
        Stop-TestProcess -Process $startupProcess
        $startupProcess.Dispose()
    }

    $resolvedScratch = [IO.Path]::GetFullPath($scratchRoot)
    if ($resolvedScratch.StartsWith(
            $resolvedTemp + '\DesktopAICompanion-ResourceSoak-',
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedScratch)) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
