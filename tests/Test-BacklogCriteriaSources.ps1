#requires -Version 5
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'OwnedTestTree.ps1')
$parent = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts'
$scratch = Join-Path $parent ('criteria-sources-' + [guid]::NewGuid().ToString('N'))
[void](Assert-OwnedTestPath $scratch)
[void][IO.Directory]::CreateDirectory((Join-Path $scratch 'docs'))
$failed = $false
$checks = 0
$utf8 = New-Object Text.UTF8Encoding($false)
$checker = Join-Path $PSScriptRoot 'Test-BacklogClosingCriteria.ps1'
function Check([bool]$Value, [string]$Name) {
    if (-not $Value) { throw ('Test-BacklogClosingCriteria.ps1: ' + $Name) }
    $script:checks++
}
function Run-Criteria([bool]$Explicit = $false) {
    $childArguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$checker,'-RepoRoot',$scratch)
    if ($Explicit) { $childArguments += @('-Path',(Join-Path $scratch 'BACKLOG.md')) }
    $preference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = @(& powershell.exe @childArguments 2>&1); $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $preference }
    return [pscustomobject]@{ Code=$code; Text=($output -join "`n") }
}
try {
    $pin = [char]::ConvertFromUtf32(0x1f4cc)
    [IO.File]::WriteAllText((Join-Path $scratch 'BACKLOG.md'), ("# Fixture`n## Legend`nlegend`n## Open`n- $pin synthetic open item`n"), $utf8)
    $blocked = Join-Path $scratch 'docs\BLOCKED.md'
    [IO.File]::WriteAllText($blocked, "# Blocked fixture`n## Synthetic blocked item`nCLOSES-WHEN: file-exists marker.txt`n", $utf8)
    $result = Run-Criteria
    Check ($result.Code -eq 0 -and $result.Text.Contains('1 live CLOSES-WHEN')) 'BLOCKED_UNSATISFIED_CRITERION_IS_COUNTED'
    [IO.File]::WriteAllText((Join-Path $scratch 'marker.txt'), 'synthetic')
    $result = Run-Criteria
    Check ($result.Code -ne 0 -and $result.Text.Contains('docs/BLOCKED.md:3') -and $result.Text.Contains('CLOSEABLE')) 'BLOCKED_LIVE_CRITERION_IS_EVALUATED'
    Check ((Run-Criteria $true).Code -eq 0) 'EXPLICIT_SINGLE_FILE_MODE_PRESERVED'
    [IO.File]::WriteAllText($blocked, "# Blocked fixture`n## Synthetic blocked item`nCLOSES-WHEN: malformed`n", $utf8)
    $result = Run-Criteria
    Check ($result.Code -ne 0 -and $result.Text.Contains('docs/BLOCKED.md:3') -and $result.Text.Contains('does not parse')) 'BLOCKED_MALFORMED_CRITERION_REFUSED'
    [IO.File]::Delete($blocked)
    Check ((Run-Criteria).Code -ne 0) 'DEFAULT_SOURCE_MISSING_IS_NOT_SUCCESS'
} catch { $failed = $true; throw }
finally { Complete-OwnedTestTree $scratch $parent $failed }
Check (-not (Test-Path -LiteralPath $scratch)) 'CRITERIA_FIXTURE_REMOVED'
Write-Output "CriteriaSourceChecks=$checks"
