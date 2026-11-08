#requires -Version 5
<#
.SYNOPSIS
Check native build/test confinement without compiling or launching native binaries.
.EXAMPLE
pwsh -NoProfile -File tests/Test-InstallerPathSafety.ps1
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repo 'installer\ScopedShutdown\PathSafety.ps1')
$tag = [Guid]::NewGuid().ToString('N')
$scratch = Join-Path $repo ('build\native-path-safety\' + $tag)
$outside = Join-Path $repo ('artifacts\native-path-target-' + $tag)
[void](Assert-ScopedShutdownPath -Path $scratch -TrustedRoot $repo)
[void](Assert-ScopedShutdownPath -Path $outside -TrustedRoot $repo)
[void][IO.Directory]::CreateDirectory($scratch)
[void][IO.Directory]::CreateDirectory($outside)
$sentinel = Join-Path $outside 'unchanged.txt'
[IO.File]::WriteAllText($sentinel, 'owned ordinary junction target must remain unchanged', (New-Object Text.UTF8Encoding($false)))
$before = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
$junction = Join-Path $scratch 'redirect'
$checks = 0
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw ('Test-InstallerPathSafety.ps1: ' + $Message) }
    $script:checks++
}
function Refuses([scriptblock]$Action) {
    $message = ''
    try { & $Action | Out-Null } catch { $message = $_.Exception.Message }
    Check ($message -ceq 'native_output_reparse_refused') 'actual entry point refuses the junction before effects'
}
$bodyPassed = $false
try {
    $ordinary = Join-Path $scratch 'not-created'
    Check ((Assert-ScopedShutdownPath -Path $ordinary -TrustedRoot $repo) -ceq $ordinary) 'ordinary output path is accepted'
    foreach ($protected in @('alias.key. ', '.env.hidden')) {
        $message = ''
        try { [void](Assert-ScopedShutdownPath -Path (Join-Path $scratch $protected) -TrustedRoot $repo) }
        catch { $message = $_.Exception.Message }
        Check ($message -ceq 'native_output_protected_scope_refused') 'protected textual aliases are refused without filesystem access'
    }
    & (Join-Path $repo 'installer\build-scoped-shutdown.ps1') -OutputDirectory $ordinary -CheckPathsOnly | Out-Null
    $checks++
    & (Join-Path $repo 'tests\Test-InstallerShutdown.ps1') -OutputDirectory $ordinary -SkipBuild -CheckPathsOnly | Out-Null
    $checks++
    Check (-not [IO.Directory]::Exists($ordinary)) 'path-only positive probes do not create an output directory'
    [void](New-Item -ItemType Junction -Path $junction -Target $outside)
    Refuses { Assert-ScopedShutdownPath -Path (Join-Path $junction 'new-output') -TrustedRoot $repo }
    Refuses { & (Join-Path $repo 'installer\build-scoped-shutdown.ps1') -OutputDirectory $junction -CheckPathsOnly }
    Refuses { & (Join-Path $repo 'tests\Test-InstallerShutdown.ps1') -OutputDirectory $junction -SkipBuild -CheckPathsOnly }
    # The other half (N-audit-1106-261): the same junction used AS the trusted root is accepted, because the root is the
    # caller's boundary and only what lies below it is judged. The walk used to start at the drive root and refused this.
    $linkedRootOutput = Join-Path $junction 'new-output'
    $linkedRootRefusal = ''
    $linkedRootAccepted = $null
    try { $linkedRootAccepted = Assert-ScopedShutdownPath -Path $linkedRootOutput -TrustedRoot $junction }
    catch { $linkedRootRefusal = $_.Exception.Message }
    Check ($linkedRootAccepted -ceq $linkedRootOutput) "a junction used as the trusted root is accepted, and only what lies below it is judged ($linkedRootRefusal)"
    Check ((Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -ceq $before) 'ordinary outside target bytes remain unchanged'
    Check (-not [IO.File]::Exists((Join-Path $outside 'ScopedShutdown.dll')) -and
        -not [IO.Directory]::Exists((Join-Path $outside 'new-output'))) 'no compiler output or descendant directory reaches the outside target'
    $bodyPassed = $true
}
finally {
    # CLEANUP NEVER REPLACES THE FAILURE IN FLIGHT (N-audit-1106-365). This was four bare statements, and a throw from
    # a finally replaces the exception already propagating: exactly when the guard under test had failed and let
    # something into the outside target, the non-recursive delete of that target threw "not empty" and surfaced
    # instead of the check that named the escape, and the target stayed behind unreported. Each step is now caught.
    # A target that is not exactly as this run made it (sentinel changed or gone, or anything beside it) is KEPT,
    # since what reached it is the evidence, and its path is said. Problems only throw when the checks themselves
    # passed, because then they are the only failure there is.
    $cleanupProblems = New-Object System.Collections.Generic.List[string]
    # Remove only this retained fixture's link itself, never its target tree.
    try { if ([IO.Directory]::Exists($junction)) { [IO.Directory]::Delete($junction, $false) } }
    catch { $cleanupProblems.Add("the junction could not be removed: $junction ($($_.Exception.Message))") }
    $sentinelIntact = $false
    try { $sentinelIntact = ((Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -ceq $before) } catch { $sentinelIntact = $false }
    if (-not $sentinelIntact) {
        $cleanupProblems.Add("Owned target changed during path verification; it is kept for inspection: $outside")
    }
    else {
        try {
            [IO.File]::Delete($sentinel)
            [IO.Directory]::Delete($outside, $false)
        }
        catch { $cleanupProblems.Add("the outside target holds more than its sentinel and is kept for inspection: $outside ($($_.Exception.Message))") }
    }
    if ($cleanupProblems.Count -gt 0) {
        if ($bodyPassed) { throw ('Test-InstallerPathSafety.ps1: ' + ($cleanupProblems -join '; ')) }
        foreach ($problem in $cleanupProblems) { Write-Warning ('Test-InstallerPathSafety.ps1: ' + $problem) }
    }
}
$report = [ordered]@{ Result='PASS'; Checks=$checks; CompilerInvoked=$false; NativeBinaryLaunched=$false; OutsideTargetUnchanged=$true }
[IO.File]::WriteAllText((Join-Path $scratch 'result.json'), ($report | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
$report | ConvertTo-Json -Compress
Write-Output ('Evidence=' + $scratch)
