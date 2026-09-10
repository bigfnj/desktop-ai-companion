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
        & $taskKill /PID $Process.Id /T /F 2>&1 | Out-Null
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
    if ($settled.Count -lt 2) {
        throw ("The churn published $($settled.Count) settled sample(s); at least 2 are needed to " +
               'tell a warming cache from a leak. Increase -DurationSeconds.')
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
    foreach ($counter in @('GdiObjects', 'UserObjects', 'Handles')) {
        $bound = switch ($counter) {
            'GdiObjects'  { $MaximumGdiGrowth }
            'UserObjects' { $MaximumUserGrowth }
            'Handles'     { $MaximumHandleGrowth }
        }
        $value = $settledGrowth.$counter
        if ($value -gt $bound) {
            throw ("Post-finalization $counter growth exceeded the bound between cycle " +
                   "$($settledGrowth.FromCycle) and $($settledGrowth.ToCycle): $value > $bound. " +
                   'Handles survived a forced collection, so this is a real leak and not finalizer lag.')
        }
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
    } | ConvertTo-Json -Depth 5
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
