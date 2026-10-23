#requires -Version 7
<#
.SYNOPSIS
Prove the Guard TEMP preflight against the actual Fortunes fixture method.
.EXAMPLE
pwsh -NoProfile -File tests/Test-MutationTempBudget.ps1
.DESCRIPTION
Compiles only the existing path-fixture method from public source, then uses
owned synthetic directories. No product build, registry or real input occurs.
#>
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$controller = Join-Path $repo 'tools\agentbroker\Invoke-MutationShards.ps1'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($controller, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'controller_parse_failed' }
foreach ($name in @('Assert-Fixed', 'Assert-SuiteTempBudget')) {
    $definitions = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true) |
        Where-Object Name -CEQ $name)
    if ($definitions.Count -ne 1) { throw 'budget_function_unavailable' }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
$checks = 0
function Check([bool]$Condition, [string]$Code) {
    if (-not $Condition) { throw $Code }
    $script:checks++
}
function Refuses([scriptblock]$Action, [string]$Expected) {
    $message = ''
    try { & $Action } catch { $message = $_.Exception.Message }
    Check ($message -ceq $Expected) 'unexpected_budget_refusal'
}

$guardSource = [IO.File]::ReadAllText((Join-Path $repo 'tests\mutate-selftest-guards.py'))
$moduleSource = [IO.File]::ReadAllText((Join-Path $repo 'tests\Test-ModuleSelfTests.ps1'))
$importer = [IO.File]::ReadAllText((Join-Path $repo 'modules\Fortunes\engine\FortuneFileImporter.cs'))
Check ($guardSource.Contains('RUN_TEMP = os.path.join(TEMP, "dp-msg-" + uuid.uuid4().hex[:12])')) 'guard_temp_shape_changed'
Check ($moduleSource.Contains("('dp-msr-' + [guid]::NewGuid().ToString('N').Substring(0, 12))")) 'module_temp_shape_changed'
Check ($importer.Contains('"DesktopAICompanion-fortune-import-" + Guid.NewGuid().ToString("N")')) 'import_temp_shape_changed'
Check ($importer.Contains('const int destinationRootCharacters = 208;')) 'fixture_destination_changed'

# Compile this exact existing method body, without changing its bounds or
# substituting an imitation. Only its visibility changes in this test class.
$match = [regex]::Match($importer, '(?s)        private static string CreateDirectoryWithExactPathLength\(.*?\n        \}')
Check $match.Success 'actual_fixture_method_missing'
$actualMethod = $match.Value.Replace('private static string CreateDirectoryWithExactPathLength', 'public static string CreateDirectoryWithExactPathLength')
Add-Type -TypeDefinition ("using System; using System.IO; public static class GuardActualPathFixture {`n" + $actualMethod + "`n}")

$short85 = 'C:\' + ('s' * 82)
$expanded99 = 'C:\' + ('e' * 96)
Assert-SuiteTempBudget $short85 $expanded99 'Guards'; $checks++
Refuses { Assert-SuiteTempBudget ('C:\' + ('s' * 83)) $expanded99 'Guards' } 'isolated_temp_path_too_long'
Refuses { Assert-SuiteTempBudget $short85 ('C:\' + ('e' * 97)) 'Guards' } 'isolated_temp_fixture_path_too_long'
# This is the measured failed pool shape: short TEMP 82, expanded TEMP 117.
Refuses { Assert-SuiteTempBudget ('C:\' + ('s' * 79)) ('C:\' + ('e' * 114)) 'Guards' } 'isolated_temp_fixture_path_too_long'
Assert-SuiteTempBudget ('C:\' + ('s' * 79)) ('C:\' + ('e' * 114)) 'AgentFlow'; $checks++
Refuses { Assert-SuiteTempBudget '' $expanded99 'Guards' } 'isolated_temp_path_missing'

$artifacts = Join-Path $repo 'artifacts'
if (-not [IO.Directory]::Exists($artifacts)) { [IO.Directory]::CreateDirectory($artifacts) | Out-Null }
$scratch = Join-Path $artifacts ('temp-budget-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
if ([IO.Directory]::Exists($scratch) -or [IO.File]::Exists($scratch)) { throw 'scratch_already_exists' }
[IO.Directory]::CreateDirectory($scratch) | Out-Null
try {
    foreach ($parentLength in @(206, 207)) {
        $parent = Join-Path $scratch (('p' * ($parentLength - $scratch.Length - 1)))
        Check ($parent.Length -eq $parentLength) 'fixture_parent_length_wrong'
        [IO.Directory]::CreateDirectory($parent) | Out-Null
        $failed = $false
        $destination = $null
        try {
            $destination = [GuardActualPathFixture]::CreateDirectoryWithExactPathLength($parent, 208)
        }
        catch {
            $inner = $_.Exception
            while ($inner.InnerException) { $inner = $inner.InnerException }
            $failed = $inner -is [InvalidOperationException] -and $inner.Message -ceq 'The self-test root cannot form the requested path length.'
            if (-not $failed) { throw }
        }
        Check ($failed -eq ($parentLength -eq 207)) 'actual_fixture_rejection_wrong'
        if ($parentLength -eq 206) {
            Check ($destination.Length -eq 208 -and [IO.Directory]::Exists($destination)) 'actual_fixture_boundary_wrong'
        }
    }
}
finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    if (-not $resolved.StartsWith(([IO.Path]::GetFullPath($artifacts).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -cnotmatch '^temp-budget-[0-9a-f]{12}$') { throw 'scratch_cleanup_boundary_invalid' }
    [IO.Directory]::Delete($resolved, $true)
}
Write-Output "Checks=$checks`nActualFortunesFixtureMethod=True`nRegistryTouched=False`nRealInputAllowed=False`nProductBuilt=False"
