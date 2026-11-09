#requires -Version 5
# Pure output and actual caller contracts. No host, module or installed data is opened.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'HarnessOutput.ps1')
$checks = 0
function Check([bool]$Value, [string]$Name) {
    if (-not $Value) { throw ('ModuleAssertionContracts: ' + $Name) }
    $script:checks++
}
function Refuses([scriptblock]$Action, [string]$Name) {
    $failed = $false
    try { & $Action | Out-Null } catch {
        $failed = $_.Exception.Message.StartsWith('HarnessOutput.ps1:') -or
            $_.Exception.Message.StartsWith('Test-ModuleSelfTests.ps1:') -or
            $_.Exception.Message.StartsWith('This file lists module(s) that are not built:') -or
            $_.Exception.Message.StartsWith('Built module(s) appear in neither the COVERED nor the UNCOVERED list')
    }
    Check $failed $Name
}
$terse = '  [agentbroker] 408 checks passed'
$verbose = @(1..408 | ForEach-Object { "  [agentbroker] PASS: synthetic assertion $_" })
$hostLines = @('PASS: the real loader accepted the module', 'RESULT=PASS')
Check ((Get-ModuleAssertionCount ($hostLines + $terse) 'agentbroker' 408) -eq 408) 'MODULE_TERSE_ACCEPTED'
Check ((Get-ModuleAssertionCount ($hostLines + $verbose) 'agentbroker' 408) -eq 408) 'MODULE_VERBOSE_ACCEPTED'
Check ((Get-ModuleAssertionCount ($verbose + $terse) 'agentbroker' 408) -eq 408) 'MODULE_MATCHING_COUNTER_RECONCILED'
Check ((Get-ModuleAssertionCount @('[agentbroker] 409 checks passed') 'agentbroker' 408) -eq 409) 'MODULE_COUNTER_CAN_GROW'
Check ((Get-ModuleAssertionCount @('[other] 408 checks passed', '[other] PASS: foreign') 'agentbroker') -eq 0) 'MODULE_FOREIGN_TOTAL_EXCLUDED'
Check ((Get-ModuleAssertionCount $hostLines 'agentbroker') -eq 0) 'MODULE_HOST_TOTAL_EXCLUDED'
Check ((Get-ModuleAssertionCount @('[other] PASS: ordinary assertion') 'other' 1) -eq 1) 'MODULE_OTHER_VERBOSE_FLOOR_UNCHANGED'
Refuses { Get-ModuleAssertionCount @('[agentbroker] 407 checks passed') 'agentbroker' 408 } 'MODULE_LOWERED_COUNTER_REFUSED'
Refuses { Get-ModuleAssertionCount $hostLines 'agentbroker' 408 } 'MODULE_MISSING_COUNTER_REFUSED'
Refuses { Get-ModuleAssertionCount @('[other] 408 checks passed') 'agentbroker' 408 } 'MODULE_FOREIGN_COUNTER_CANNOT_SATISFY_FLOOR'
Refuses { Get-ModuleAssertionCount ($terse, $terse) 'agentbroker' } 'MODULE_DUPLICATE_COUNTER_REFUSED'
Refuses { Get-ModuleAssertionCount ($verbose + '[agentbroker] 409 checks passed') 'agentbroker' 408 } 'MODULE_CONTRADICTORY_COUNTER_REFUSED'
Refuses { Get-ModuleAssertionCount @('[agentbroker] 0 checks passed') 'agentbroker' } 'MODULE_ZERO_COUNTER_REFUSED'
Refuses { Get-ModuleAssertionCount @('[agentbroker] 999999999999999999999 checks passed') 'agentbroker' } 'MODULE_OVERFLOW_COUNTER_REFUSED'
Refuses { Get-ModuleAssertionCount @('noise [agentbroker] 408 checks passed') 'agentbroker' 408 } 'MODULE_UNANCHORED_COUNTER_REFUSED'
Refuses { Get-ModuleAssertionCount @('[agentbroker] 408 checks passed trailing') 'agentbroker' 408 } 'MODULE_COUNTER_SUFFIX_REFUSED'

# Execute the actual public caller's floor assignment and grading assignment with synthetic rows.
$tokens = $null; $errors = $null
$syntax = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-ModuleSelfTests.ps1'), [ref]$tokens, [ref]$errors)
Check ($errors.Count -eq 0) 'MODULE_RUNNER_PARSES'
function Assignment([string]$Name) {
    $nodes = @($syntax.FindAll({ param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
        $node.Left.VariablePath.UserPath -ceq $Name
    }, $true))
    Check ($nodes.Count -eq 1) ('MODULE_CALLER_SINGLE_' + $Name)
    return $nodes[0].Extent.Text
}
$floorsCode = Assignment 'moduleAssertionFloors'
$minimumCode = Assignment 'minimum'
$counterCode = Assignment 'assertions'
. ([scriptblock]::Create($floorsCode))
Check ($moduleAssertionFloors.Count -eq 1 -and $moduleAssertionFloors.agentbroker -eq 408) 'MODULE_RECORDED_FLOOR_408'
$id = 'agentbroker'
$r = [pscustomobject]@{ Result = 'PASS'; Lines = @($terse) }
. ([scriptblock]::Create($minimumCode))
. ([scriptblock]::Create($counterCode))
Check ($assertions -eq 408) 'MODULE_ACTUAL_CALLER_TERSE'
$r.Lines = @('[agentbroker] 385 checks passed')
Refuses { . ([scriptblock]::Create($counterCode)) } 'MODULE_ACTUAL_CALLER_REFUSES_REGISTRATION_LOSS'
$id = 'other'
$r.Lines = @('[other] PASS: actual assertion')
. ([scriptblock]::Create($minimumCode))
. ([scriptblock]::Create($counterCode))
Check ($assertions -eq 1) 'MODULE_ACTUAL_CALLER_OTHER_FLOOR_ONE'

$selectors = @($syntax.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Select-ModuleTestIds'
}, $true))
Check ($selectors.Count -eq 1) 'MODULE_SELECTION_SINGLE_AUTHORITY'
. ([scriptblock]::Create($selectors[0].Extent.Text))
$known = @('agentbroker', 'other')
Check ((@(Select-ModuleTestIds @('AGENTBROKER', 'agentbroker') $known) -join ',') -ceq 'agentbroker') 'MODULE_SELECTION_NORMALIZED_AND_DEDUPLICATED'
Refuses { Select-ModuleTestIds @('not-built') $known } 'MODULE_SELECTION_UNKNOWN_REFUSED'
Refuses { Select-ModuleTestIds @(' ') $known } 'MODULE_SELECTION_EMPTY_REFUSED'
Refuses { Select-ModuleTestIds @() $known } 'MODULE_SELECTION_EMPTY_SET_REFUSED'

# Exercise the actual catalog assignments and selection branch, without reaching any child launch.
$catalog = @{}
foreach ($name in @('selected', 'missing', 'unlisted')) {
    $nodes = @($syntax.EndBlock.Statements | Where-Object {
        $_ -is [Management.Automation.Language.AssignmentStatementAst] -and
        $_.Left -is [Management.Automation.Language.VariableExpressionAst] -and $_.Left.VariablePath.UserPath -ceq $name
    })
    if ($nodes.Count -eq 1) { $catalog[$name] = $nodes[0].Extent.Text }
}
$selectionBranches = @($syntax.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.IfStatementAst] -and $_.Clauses[0].Item1.Extent.Text -ceq '$Only.Count -gt 0'
})
$missingBranches = @($syntax.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.IfStatementAst] -and $_.Clauses[0].Item1.Extent.Text -ceq '$missing.Count -gt 0'
})
$unlistedBranches = @($syntax.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.IfStatementAst] -and $_.Clauses[0].Item1.Extent.Text -ceq '$unlisted.Count -gt 0'
})
Check ($catalog.Count -eq 3 -and $selectionBranches.Count -eq 1 -and $missingBranches.Count -eq 1 -and $unlistedBranches.Count -eq 1) 'MODULE_CATALOG_ACTUAL_ASSIGNMENTS'
$known = @('agentbroker', 'other', 'gap')
$Only = @(); $built = @('agentbroker')
. ([scriptblock]::Create($catalog.selected))
. ([scriptblock]::Create($catalog.missing))
Check ($missing.Count -eq 2 -and $missing -contains 'other' -and $missing -contains 'gap') 'MODULE_DEFAULT_REQUIRES_COMPLETE_CATALOG'
Refuses { . ([scriptblock]::Create($missingBranches[0].Extent.Text)) } 'MODULE_DEFAULT_MISSING_BUILD_REFUSED'
$Only = @('agentbroker')
. ([scriptblock]::Create($catalog.selected))
. ([scriptblock]::Create($catalog.missing))
Check ($missing.Count -eq 0 -and @($selected).Count -eq 1 -and $selected -contains 'agentbroker') 'MODULE_SCOPED_REQUIRES_SELECTED_BUILD_ONLY'
$Only = @('other')
. ([scriptblock]::Create($catalog.selected))
. ([scriptblock]::Create($catalog.missing))
Check ($missing.Count -eq 1 -and $missing -contains 'other') 'MODULE_SCOPED_MISSING_BUILD_DETECTED'
$Only = @('not-built')
Refuses { . ([scriptblock]::Create($catalog.selected)) } 'MODULE_ACTUAL_SELECTION_UNKNOWN_REFUSED'
$built = @('agentbroker', 'foreign')
. ([scriptblock]::Create($catalog.unlisted))
Check ($unlisted.Count -eq 1 -and $unlisted -contains 'foreign') 'MODULE_SCOPED_UNKNOWN_BUILT_ID_DETECTED'
Refuses { . ([scriptblock]::Create($unlistedBranches[0].Extent.Text)) } 'MODULE_SCOPED_UNKNOWN_BUILT_ID_REFUSED'
$Only = @('agentbroker'); $selected = @('agentbroker'); $Covered = @('agentbroker', 'other'); $Uncovered = [ordered]@{ gap = @('known gap') }
. ([scriptblock]::Create($selectionBranches[0].Extent.Text))
Check ($Covered.Count -eq 1 -and $Covered[0] -ceq 'agentbroker' -and $Uncovered.Count -eq 0) 'MODULE_SCOPED_EXECUTION_EXCLUDES_UNSELECTED_IDS'
$Only = @(); $Covered = @('agentbroker', 'other'); $Uncovered = [ordered]@{ gap = @('known gap') }
. ([scriptblock]::Create($selectionBranches[0].Extent.Text))
Check ($Covered.Count -eq 2 -and $Uncovered.Count -eq 1) 'MODULE_DEFAULT_EXECUTION_COVERAGE_UNCHANGED'
Write-Output "ModuleAssertionChecks=$checks"
