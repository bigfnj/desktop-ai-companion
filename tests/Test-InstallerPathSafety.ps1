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
    Check ((Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -ceq $before) 'ordinary outside target bytes remain unchanged'
    Check (-not [IO.File]::Exists((Join-Path $outside 'ScopedShutdown.dll')) -and
        -not [IO.Directory]::Exists((Join-Path $outside 'new-output'))) 'no compiler output or descendant directory reaches the outside target'
}
finally {
    # Remove only this retained fixture's link itself, never its target tree.
    if ([IO.Directory]::Exists($junction)) { [IO.Directory]::Delete($junction, $false) }
    if ((Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -cne $before) { throw 'Owned target changed during path verification.' }
    [IO.File]::Delete($sentinel)
    [IO.Directory]::Delete($outside, $false)
}
$report = [ordered]@{ Result='PASS'; Checks=$checks; CompilerInvoked=$false; NativeBinaryLaunched=$false; OutsideTargetUnchanged=$true }
[IO.File]::WriteAllText((Join-Path $scratch 'result.json'), ($report | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
$report | ConvertTo-Json -Compress
Write-Output ('Evidence=' + $scratch)
