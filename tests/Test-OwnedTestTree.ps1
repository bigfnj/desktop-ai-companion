#requires -Version 5
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'OwnedTestTree.ps1')
$parent = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts'
$scratch = Join-Path $parent ('owned-tree-check-' + [guid]::NewGuid().ToString('N'))
[void](Assert-OwnedTestPath $scratch)
[void][IO.Directory]::CreateDirectory($scratch)
$checks = 0
$failed = $false
$link = $null
function Check([bool]$Value, [string]$Name) {
    if (-not $Value) { throw ('OwnedTestTree.ps1: ' + $Name) }
    $script:checks++
}
function Refuses([scriptblock]$Action, [string]$Code) {
    $refused = $false
    try { & $Action } catch { $refused = $_.Exception.Message.Contains($Code) }
    Check $refused $Code
}
try {
    $tree = Join-Path $scratch 'ordinary'
    [void][IO.Directory]::CreateDirectory((Join-Path $tree 'nested'))
    [IO.File]::WriteAllText((Join-Path $tree 'nested\unexpected.txt'), 'owned synthetic output')
    $readOnly = Join-Path $tree 'readonly.bin'
    [IO.File]::WriteAllText($readOnly, 'owned synthetic output')
    [IO.File]::SetAttributes($readOnly, [IO.FileAttributes]::ReadOnly)
    Refuses { Remove-OwnedTestTree $tree $tree } 'owned_test_parent_mismatch'
    Check ([IO.File]::Exists($readOnly)) 'PARENT_REFUSAL_PRECEDES_EFFECTS'
    Remove-OwnedTestTree $tree $scratch
    Check (-not (Test-Path -LiteralPath $tree)) 'UNEXPECTED_NESTED_OUTPUT_REMOVED'
    $tree = Join-Path $scratch 'linked'
    $outside = Join-Path $scratch 'outside'
    [void][IO.Directory]::CreateDirectory($tree)
    [void][IO.Directory]::CreateDirectory($outside)
    $beforeLink = Join-Path $tree 'ordinary.txt'
    $sentinel = Join-Path $outside 'sentinel.txt'
    [IO.File]::WriteAllText($beforeLink, 'preserve before refusal')
    [IO.File]::WriteAllText($sentinel, 'preserve outside target')
    $link = Join-Path $tree 'junction'
    $null = New-Item -ItemType Junction -Path $link -Target $outside
    Refuses { Remove-OwnedTestTree $tree $scratch } 'owned_test_reparse'
    Check ([IO.File]::ReadAllText($sentinel) -ceq 'preserve outside target') 'LINK_TARGET_PRESERVED'
    Check ([IO.File]::Exists($beforeLink)) 'LINK_PREFLIGHT_PRECEDES_ALL_DELETES'
    $warnings = @(Complete-OwnedTestTree $tree $scratch $true 3>&1)
    Check ($warnings.Count -eq 1 -and $warnings[0] -is [Management.Automation.WarningRecord]) 'ORIGINAL_FAILURE_PRESERVED_WITH_VISIBLE_CLEANUP_WARNING'
    [IO.Directory]::Delete($link, $false)
    $link = $null
    Remove-OwnedTestTree $tree $scratch
    Remove-OwnedTestTree $outside $scratch
    $replaced = Join-Path $scratch 'replaced'
    [IO.File]::WriteAllText($replaced, 'owned file')
    Refuses { Remove-OwnedTestTree $replaced $scratch } 'owned_test_root_replaced'
    Check ([IO.File]::ReadAllText($replaced) -ceq 'owned file') 'REPLACED_ROOT_PRESERVED'
} catch { $failed = $true; throw }
finally {
    if ($link -and (Test-Path -LiteralPath $link) -and
        (([IO.File]::GetAttributes($link) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { [IO.Directory]::Delete($link, $false) }
    Complete-OwnedTestTree $scratch $parent $failed
}
Check (-not (Test-Path -LiteralPath $scratch)) 'FIXTURE_ABSENT'
Write-Output "OwnedTreeChecks=$checks"
