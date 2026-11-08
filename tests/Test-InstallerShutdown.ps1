#requires -Version 5
<#
.SYNOPSIS
    Exercise installer shutdown policy and its native process binding with owned synthetic children.
.EXAMPLE
    pwsh -NoProfile -File tests/Test-InstallerShutdown.ps1
.EXAMPLE
    powershell.exe -NoProfile -File tests/Test-InstallerShutdown.ps1 -SkipBuild
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = '',
    [switch]$SkipBuild,
    [Parameter(DontShow)][switch]$CheckPathsOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$buildRoot = [IO.Path]::GetFullPath((Join-Path $repo 'build'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $buildRoot 'scoped-shutdown'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($buildRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Native test output must be below this checkout build directory.'
}
. (Join-Path $repo 'installer\ScopedShutdown\PathSafety.ps1')
. (Join-Path $PSScriptRoot 'InstallerShutdown.Tests\NativeCheckRunner.ps1')
[void](Assert-ScopedShutdownPath -Path $output -TrustedRoot $repo)
$checks = Join-Path $output 'ShutdownChecks.exe'
$fixture = Join-Path $output 'SyntheticCompanion.exe'
foreach ($path in @($checks, $fixture)) { [void](Assert-ScopedShutdownPath -Path $path -TrustedRoot $repo) }
if ($CheckPathsOnly) { Write-Output 'NATIVE_TEST_PATHS_PASS'; return }
if (-not $SkipBuild) {
    & (Join-Path $repo 'installer\build-scoped-shutdown.ps1') -Tests -OutputDirectory $output
}
foreach ($path in @($checks, $fixture)) {
    [void](Assert-ScopedShutdownPath -Path $path -TrustedRoot $repo)
    if (-not [IO.File]::Exists($path)) { throw "Native check input is missing: $path" }
}
$scratch = Join-Path $buildRoot ('scoped-shutdown-tests\' + [Guid]::NewGuid().ToString('N'))
[void](Assert-ScopedShutdownPath -Path $scratch -TrustedRoot $repo)
[void][IO.Directory]::CreateDirectory($scratch)
$results = New-Object 'Collections.Generic.List[object]'
$children = New-Object 'Collections.Generic.List[Diagnostics.Process]'
$primaryFailure = $null

function Invoke-NativeCheck {
    param([string]$Name, [string[]]$Arguments)
    $checkResult = Invoke-ScopedNativeCheck -FilePath $checks -Arguments $Arguments
    $lines = $checkResult.Lines
    $code = $checkResult.ExitCode
    [IO.File]::WriteAllLines((Join-Path $scratch ($Name + '.txt')), [string[]]$lines,
        (New-Object Text.UTF8Encoding($false)))
    if ($checkResult.TimedOut) { throw "Native shutdown check timed out: $Name." }
    $expectedCounts = @{ unit = 62; graceful = 9; fallback = 9; 'legacy-refusal' = 8 }
    if (-not $expectedCounts.ContainsKey($Name)) { throw 'Unknown native shutdown scenario.' }
    $expectedCountLine = 'PASS ' + $expectedCounts[$Name] + ' checks'
    if ($code -ne 0 -or -not ($lines -contains 'RESULT:PASS') -or -not ($lines -contains $expectedCountLine)) {
        throw "Native shutdown check failed: $Name (exit $code)."
    }
    Write-Host ("PASS $Name")
    $results.Add([pscustomobject]@{ Name = $Name; ExitCode = $code; Result = 'PASS' })
}

function Start-OwnedCompanion {
    param([string]$Directory, [switch]$Hung)
    [void][IO.Directory]::CreateDirectory($Directory)
    $image = Join-Path $Directory 'DesktopAICompanion.exe'
    [IO.File]::Copy($fixture, $image, $false)
    $ready = Join-Path $Directory 'ready.txt'
    $arguments = '--ready "' + $ready + '"'
    if ($Hung) { $arguments += ' --hung' }
    $child = Start-Process -FilePath $image -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $children.Add($child)
    # Materialize and retain the process handle while it is still the child we started.
    [void]$child.SafeHandle
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not [IO.File]::Exists($ready)) {
        if ($child.HasExited) { throw 'An owned synthetic companion exited before readiness.' }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'An owned synthetic companion did not become ready.' }
        Start-Sleep -Milliseconds 25
    }
    return [pscustomobject]@{ Process = $child; Image = $image }
}

try {
    Invoke-NativeCheck -Name 'unit' -Arguments @()
    foreach ($mode in @('graceful', 'fallback', 'legacy-refusal')) {
        $caseRoot = Join-Path $scratch $mode
        $target = Start-OwnedCompanion -Directory (Join-Path $caseRoot 'installed') -Hung:($mode -eq 'fallback')
        $foreign = Start-OwnedCompanion -Directory (Join-Path $caseRoot 'installed-other')
        $arguments = @('--owned-processes', [string]$target.Process.Id, [string]$foreign.Process.Id, $target.Image)
        if ($mode -eq 'fallback') { $arguments += '--expect-fallback' }
        if ($mode -eq 'legacy-refusal') { $arguments += '--legacy-refusal' }
        Invoke-NativeCheck -Name $mode -Arguments $arguments
        if ($foreign.Process.HasExited) { throw 'Native shutdown affected the foreign synthetic child.' }
        if ($mode -eq 'legacy-refusal') {
            if ($target.Process.HasExited) { throw 'Legacy refusal affected the installed synthetic child.' }
        } elseif (-not $target.Process.WaitForExit(5000)) {
            throw 'The targeted synthetic child did not exit within its verification bound.'
        } elseif ($mode -eq 'graceful' -and $target.Process.ExitCode -ne 0) {
            throw 'The normal synthetic child did not follow its graceful exit path.'
        } elseif ($mode -eq 'fallback' -and $target.Process.ExitCode -eq 0) {
            throw 'The hung synthetic child did not follow its termination exit path.'
        }
    }
} catch {
    $primaryFailure = $_
} finally {
    $cleanupFailures = New-Object 'Collections.Generic.List[string]'
    foreach ($child in $children) {
        try {
            if (-not $child.HasExited) {
                $child.Kill()
                if (-not $child.WaitForExit(5000)) { throw 'Owned synthetic child cleanup timed out.' }
            }
        } catch { $cleanupFailures.Add($_.Exception.Message) }
        finally { $child.Dispose() }
    }
    if ($cleanupFailures.Count -gt 0) {
        if ($null -eq $primaryFailure) { $primaryFailure = 'Owned synthetic child cleanup failed: ' + ($cleanupFailures -join ', ') }
        else { Write-Warning 'Owned synthetic child cleanup also failed; preserving the primary failure.' }
    }
}
$report = [pscustomobject]@{
    Result = $(if ($null -eq $primaryFailure) { 'PASS' } else { 'FAIL' })
    Cases = @($results.ToArray())
    InstalledApplicationTouched = $false
    SystemWideProcessEnumeration = $false
    ScratchDirectory = $scratch
    ScratchRemoved = $false
}
$reportPath = Join-Path $scratch 'result.json'
if ($null -ne $primaryFailure) {
    # A failed run keeps its scratch tree and its report, as docs/test-artifact-retention.md keeps every failed run's
    # evidence, and says where they are.
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))
    Write-Warning "Installer shutdown checks failed; their scratch folder is kept for inspection: $scratch"
    throw $primaryFailure
}
# A PASSING RUN REMOVES ITS SCRATCH TREE (N-audit-1106-366). Each run left build\scoped-shutdown-tests\<guid> behind, six
# copies of the synthetic companion exe plus the checker logs, and nothing removed any of them. Only here, past the
# failure throw above, and after the finally above has seen every owned child exit, so no copied image is still running.
# The leaf is checked first, so nothing but this run's own <guid> folder is deleted recursively, and any reparse point
# inside refuses the delete rather than having the delete meet it. A removal that fails leaves the folder and its report
# and says so; it never turns this passing run into a failure, the rule docs/test-artifact-retention.md sets for the
# gate's own cache cleanup.
$scratchParent = Join-Path $buildRoot 'scoped-shutdown-tests'
if ([IO.Path]::GetDirectoryName($scratch) -ne $scratchParent -or (Split-Path -Leaf $scratch) -cnotmatch '^[0-9a-f]{32}$') {
    Write-Warning "the installer shutdown scratch folder is not a <guid> child of $scratchParent, so it is not deleted: $scratch"
}
elseif (@(Get-ChildItem -LiteralPath $scratch -Recurse -Force -Attributes ReparsePoint).Count -gt 0) {
    Write-Warning "the installer shutdown scratch folder holds a reparse point, so it is not deleted: $scratch"
}
else {
    try {
        [IO.Directory]::Delete($scratch, $true)
        $report.ScratchRemoved = -not [IO.Directory]::Exists($scratch)
    }
    catch { Write-Warning "the installer shutdown scratch folder could not be removed and is left for inspection: $scratch ($($_.Exception.Message))" }
}
if (-not $report.ScratchRemoved) {
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))
}
Write-Host 'Installer shutdown checks PASS. All owned synthetic children exited.'
$report
