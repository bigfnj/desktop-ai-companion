#requires -Version 5
# Output and actual caller contracts. Artifact fixtures contain synthetic bytes; no real host, module or installed data is opened.
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
$terse = '  [agentbroker] 520 checks passed'
$verbose = @(1..520 | ForEach-Object { "  [agentbroker] PASS: synthetic assertion $_" })
$hostLines = @('PASS: the real loader accepted the module', 'RESULT=PASS')
Check ((Get-ModuleAssertionCount ($hostLines + $terse) 'agentbroker' 520) -eq 520) 'MODULE_TERSE_ACCEPTED'
Check ((Get-ModuleAssertionCount ($hostLines + $verbose) 'agentbroker' 520) -eq 520) 'MODULE_VERBOSE_ACCEPTED'
Check ((Get-ModuleAssertionCount ($verbose + $terse) 'agentbroker' 520) -eq 520) 'MODULE_MATCHING_COUNTER_RECONCILED'
Check ((Get-ModuleAssertionCount @('[agentbroker] 521 checks passed') 'agentbroker' 520) -eq 521) 'MODULE_COUNTER_CAN_GROW'
Check ((Get-ModuleAssertionCount @('[other] 520 checks passed', '[other] PASS: foreign') 'agentbroker') -eq 0) 'MODULE_FOREIGN_TOTAL_EXCLUDED'
Check ((Get-ModuleAssertionCount $hostLines 'agentbroker') -eq 0) 'MODULE_HOST_TOTAL_EXCLUDED'
Check ((Get-ModuleAssertionCount @('[other] PASS: ordinary assertion') 'other' 1) -eq 1) 'MODULE_OTHER_VERBOSE_FLOOR_UNCHANGED'
Refuses { Get-ModuleAssertionCount @('[agentbroker] 519 checks passed') 'agentbroker' 520 } 'MODULE_LOWERED_COUNTER_REFUSED'
Refuses { Get-ModuleAssertionCount $hostLines 'agentbroker' 520 } 'MODULE_MISSING_COUNTER_REFUSED'
Refuses { Get-ModuleAssertionCount @('[other] 520 checks passed') 'agentbroker' 520 } 'MODULE_FOREIGN_COUNTER_CANNOT_SATISFY_FLOOR'
Refuses { Get-ModuleAssertionCount ($terse, $terse) 'agentbroker' } 'MODULE_DUPLICATE_COUNTER_REFUSED'
Refuses { Get-ModuleAssertionCount ($verbose + '[agentbroker] 521 checks passed') 'agentbroker' 520 } 'MODULE_CONTRADICTORY_COUNTER_REFUSED'
Refuses { Get-ModuleAssertionCount @('[agentbroker] 0 checks passed') 'agentbroker' } 'MODULE_ZERO_COUNTER_REFUSED'
Refuses { Get-ModuleAssertionCount @('[agentbroker] 999999999999999999999 checks passed') 'agentbroker' } 'MODULE_OVERFLOW_COUNTER_REFUSED'
Refuses { Get-ModuleAssertionCount @('noise [agentbroker] 520 checks passed') 'agentbroker' 520 } 'MODULE_UNANCHORED_COUNTER_REFUSED'
Refuses { Get-ModuleAssertionCount @('[agentbroker] 520 checks passed trailing') 'agentbroker' 520 } 'MODULE_COUNTER_SUFFIX_REFUSED'

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
Check ($moduleAssertionFloors.Count -eq 1 -and $moduleAssertionFloors.agentbroker -eq 520) 'MODULE_RECORDED_FLOOR_520'
$id = 'agentbroker'
$r = [pscustomobject]@{ Result = 'PASS'; Lines = @($terse) }
. ([scriptblock]::Create($minimumCode))
. ([scriptblock]::Create($counterCode))
Check ($assertions -eq 520) 'MODULE_ACTUAL_CALLER_TERSE'
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

# Execute the actual witness parser and its actual caller assignment, not a parallel regex in this test.
foreach ($name in @('Get-AgentBrokerArtifactSnapshot', 'Get-ModuleArtifactWitnessLines', 'Assert-ModuleArtifactsUnchanged')) {
    $functions = @($syntax.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
    }, $true))
    Check ($functions.Count -eq 1) ('MODULE_ARTIFACT_SINGLE_' + $name)
    . ([scriptblock]::Create($functions[0].Extent.Text))
}
function RefusesArtifact([scriptblock]$Action, [string]$Expected, [string]$Name) {
    $failure = ''
    try { & $Action | Out-Null } catch { $failure = $_.Exception.Message }
    Check ($failure.StartsWith(('Test-ModuleSelfTests.ps1: ' + $Expected + ':'), [StringComparison]::Ordinal)) $Name
}
$witnessCode = Assignment 'artifactWitnessLines'
$expectedArtifacts = [pscustomobject]@{ LOADED = ('A' * 64); WORKER_PAYLOAD = ('B' * 64) }
$loadedLine = 'WITNESS: MODULE_SELFTEST_LOADED_SHA256 agentbroker ' + $expectedArtifacts.LOADED
$payloadLine = 'WITNESS: MODULE_SELFTEST_WORKER_PAYLOAD_SHA256 agentbroker ' + $expectedArtifacts.WORKER_PAYLOAD
$id = 'agentbroker'; $artifactBefore = $expectedArtifacts
$r = [pscustomobject]@{ Result = 'PASS'; Lines = @($loadedLine, $payloadLine) }
. ([scriptblock]::Create($witnessCode))
Check ($artifactWitnessLines.Count -eq 2 -and $artifactWitnessLines[0] -ceq $loadedLine -and $artifactWitnessLines[1] -ceq $payloadLine) 'MODULE_WITNESS_ACTUAL_CALLER_ACCEPTED'
foreach ($kind in @('LOADED', 'WORKER_PAYLOAD')) {
    $line = if ($kind -ceq 'LOADED') { $loadedLine } else { $payloadLine }
    $other = if ($kind -ceq 'LOADED') { $payloadLine } else { $loadedLine }
    $r.Lines = @($other)
    RefusesArtifact { . ([scriptblock]::Create($witnessCode)) } ('MODULE_ARTIFACT_WITNESS_' + $kind + '_COUNT') ('MODULE_WITNESS_MISSING_' + $kind)
    $r.Lines = @($line, $line, $other)
    RefusesArtifact { . ([scriptblock]::Create($witnessCode)) } ('MODULE_ARTIFACT_WITNESS_' + $kind + '_COUNT') ('MODULE_WITNESS_DUPLICATE_' + $kind)
    $r.Lines = @($line, ($line + ' malformed'), $other)
    RefusesArtifact { . ([scriptblock]::Create($witnessCode)) } ('MODULE_ARTIFACT_WITNESS_' + $kind + '_COUNT') ('MODULE_WITNESS_VALID_PLUS_MALFORMED_' + $kind)
    $r.Lines = @($line.Replace(' agentbroker ', ' other '), $other)
    RefusesArtifact { . ([scriptblock]::Create($witnessCode)) } ('MODULE_ARTIFACT_WITNESS_' + $kind + '_FORMAT') ('MODULE_WITNESS_FOREIGN_' + $kind)
    $r.Lines = @($line.Substring(0, $line.Length - 1), $other)
    RefusesArtifact { . ([scriptblock]::Create($witnessCode)) } ('MODULE_ARTIFACT_WITNESS_' + $kind + '_FORMAT') ('MODULE_WITNESS_SHORT_HASH_' + $kind)
    $r.Lines = @(($line + ' suffix'), $other)
    RefusesArtifact { . ([scriptblock]::Create($witnessCode)) } ('MODULE_ARTIFACT_WITNESS_' + $kind + '_FORMAT') ('MODULE_WITNESS_SUFFIX_' + $kind)
    $r.Lines = @(($line.Substring(0, $line.Length - 64) + ('C' * 64)), $other)
    RefusesArtifact { . ([scriptblock]::Create($witnessCode)) } ('MODULE_ARTIFACT_WITNESS_' + $kind + '_MISMATCH') ('MODULE_WITNESS_MISMATCH_' + $kind)
}
$r.Result = 'FAIL'; $r.Lines = @()
. ([scriptblock]::Create($witnessCode))
Check ($artifactWitnessLines.Count -eq 0) 'MODULE_WITNESS_FAILED_MODULE_KEEPS_ORIGINAL_FAILURE'
$id = 'other'; $r.Result = 'PASS'; $r.Lines = @($loadedLine, 'WITNESS: MODULE_SELFTEST_WORKER_PAYLOAD_SHA256 malformed'); $artifactBefore = $null
. ([scriptblock]::Create($witnessCode))
Check ($artifactWitnessLines.Count -eq 0) 'MODULE_WITNESS_OTHER_MODULE_UNCHANGED'

$beforeCondition = '$id -ceq ''agentbroker'''
$afterCondition = '$id -ceq ''agentbroker'' -and $r.Result -ceq ''PASS'''
$beforeBranches = @($syntax.FindAll({ param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and $node.ElseClause -eq $null -and
    $node.Clauses[0].Item1.Extent.Text -ceq $beforeCondition
}, $true))
$afterBranches = @($syntax.FindAll({ param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
    $node.Clauses[0].Item1.Extent.Text -ceq $afterCondition
}, $true))
Check ($beforeBranches.Count -eq 1 -and $afterBranches.Count -eq 1) 'MODULE_ARTIFACT_CALLER_SNAPSHOT_BRANCHES'

# Presence alone does not make a guard reachable. All guarded steps must be direct statements of the
# actual Covered-loop try block, in the order in which their evidence becomes valid.
$coveredLoops = @($syntax.FindAll({ param($node)
    $node -is [Management.Automation.Language.ForEachStatementAst] -and
    $node.Variable.VariablePath.UserPath -ceq 'id' -and $node.Condition.Extent.Text -ceq '$Covered'
}, $true))
Check ($coveredLoops.Count -eq 1) 'MODULE_ARTIFACT_COVERED_CALLER_SINGLE'
$moduleTries = @($coveredLoops[0].Body.Statements | Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] })
Check ($moduleTries.Count -eq 1) 'MODULE_ARTIFACT_COVERED_TRY_SINGLE'
$moduleTry = $moduleTries[0]
$invokeAssignments = @($moduleTry.Body.Statements | Where-Object {
    $_ -is [Management.Automation.Language.AssignmentStatementAst] -and
    $_.Left -is [Management.Automation.Language.VariableExpressionAst] -and
    $_.Left.VariablePath.UserPath -ceq 'r' -and $_.Right.Extent.Text -ceq 'Invoke-ModuleSelfTest $id'
})
$witnessAssignments = @($moduleTry.Body.Statements | Where-Object {
    $_ -is [Management.Automation.Language.AssignmentStatementAst] -and
    $_.Left -is [Management.Automation.Language.VariableExpressionAst] -and
    $_.Left.VariablePath.UserPath -ceq 'artifactWitnessLines'
})
Check ($invokeAssignments.Count -eq 1 -and $witnessAssignments.Count -eq 1 -and
    $beforeBranches[0].Parent -eq $moduleTry.Body -and $afterBranches[0].Parent -eq $moduleTry.Body) 'MODULE_ARTIFACT_CALLER_DIRECT_GUARDS'
Check ($beforeBranches[0].Extent.StartOffset -lt $invokeAssignments[0].Extent.StartOffset -and
    $invokeAssignments[0].Extent.StartOffset -lt $afterBranches[0].Extent.StartOffset -and
    $afterBranches[0].Extent.StartOffset -lt $witnessAssignments[0].Extent.StartOffset) 'MODULE_ARTIFACT_CALLER_ORDER'
$coveredLoopCode = $coveredLoops[0].Extent.Text

# Run the actual foreach and parser against synthetic bytes. Only the module process invocation is
# substituted, so catch/continue and failure priority are exercised at their real caller.
function Invoke-SyntheticCoveredArtifactCaller([string]$Mode, [string]$FixtureModules) {
    $modulesRoot = $FixtureModules
    $assemblyPath = Join-Path $modulesRoot 'agentbroker/AgentBroker.dll'
    $originalBytes = [IO.File]::ReadAllBytes($assemblyPath)
    $state = [pscustomobject]@{ Mode = $Mode; Calls = (New-Object 'System.Collections.Generic.List[string]') }
    $Covered = @('agentbroker', 'other')
    $watchParent = $null; $realRemembranceWatched = @(); $noRealInputRequested = $false
    $moduleTimings = @{}; $problems = @(); $uncaught = ''; $hostOutput = @()
    function Invoke-ModuleSelfTest([string]$Id) {
        [void]$state.Calls.Add($Id)
        $moduleTimings[$Id] = [pscustomobject]@{ Seconds = 0.001; Limit = 1 }
        $result = 'PASS'; $reasons = @(); $lines = @('[other] PASS: synthetic actual caller assertion')
        if ($Id -ceq 'agentbroker') {
            $lines = @()
            if ($state.Mode -ceq 'plain-exception') { throw 'synthetic-non-artifact-failure' }
            elseif ($state.Mode -ceq 'failed') { $result = 'FAIL'; $reasons = @('original-module-failure') }
            elseif ($state.Mode -ceq 'pass') {
                $snapshot = Get-AgentBrokerArtifactSnapshot $modulesRoot
                $lines = @('[agentbroker] 520 checks passed',
                    ('WITNESS: MODULE_SELFTEST_LOADED_SHA256 agentbroker ' + $snapshot.LOADED),
                    ('WITNESS: MODULE_SELFTEST_WORKER_PAYLOAD_SHA256 agentbroker ' + $snapshot.WORKER_PAYLOAD))
            }
            elseif ($state.Mode -ceq 'changed') {
                [IO.File]::WriteAllBytes($assemblyPath, [Text.Encoding]::ASCII.GetBytes('changed-during-synthetic-invocation'))
                $changed = Get-AgentBrokerArtifactSnapshot $modulesRoot
                $lines = @('[agentbroker] 520 checks passed',
                    ('WITNESS: MODULE_SELFTEST_LOADED_SHA256 agentbroker ' + $changed.LOADED),
                    ('WITNESS: MODULE_SELFTEST_WORKER_PAYLOAD_SHA256 agentbroker ' + $changed.WORKER_PAYLOAD))
            }
        }
        return [pscustomobject]@{ Result = $result; Lines = $lines; Reasons = $reasons; Skips = @(); RealInputSkips = @(); Exceptions = @() }
    }
    try { $hostOutput = @(. ([scriptblock]::Create($coveredLoopCode)) 6>&1) }
    catch { $uncaught = $_.Exception.Message }
    finally { [IO.File]::WriteAllBytes($assemblyPath, $originalBytes) }
    return [pscustomobject]@{ Calls = @($state.Calls.ToArray()); Problems = @($problems); Uncaught = $uncaught;
        HostOutput = @($hostOutput | ForEach-Object { [string]$_ }) }
}

# Fixed independent golden manifest: ordering is ordinal (Z before a), paths use '/', names include UTF-8,
# each path is followed by NUL, uppercase content SHA and LF. The bytes are never a loadable module or Worker.
$fixtureName = 'dp-module-artifact-contract-' + [Guid]::NewGuid().ToString('N')
$fixtureParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixture = [IO.Path]::GetFullPath((Join-Path $fixtureParent $fixtureName))
$fixturePrefix = $fixtureParent.TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)) + [IO.Path]::DirectorySeparatorChar
Check ($fixture.StartsWith($fixturePrefix, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($fixture) -ceq $fixtureName -and -not [IO.Directory]::Exists($fixture)) 'MODULE_ARTIFACT_FIXTURE_OWNED'
try {
    $modulesRoot = Join-Path $fixture 'modules'
    $moduleRoot = Join-Path $modulesRoot 'agentbroker'
    $workerRoot = Join-Path $moduleRoot 'worker'
    [void][IO.Directory]::CreateDirectory((Join-Path $workerRoot 'sub'))
    $assembly = Join-Path $moduleRoot 'AgentBroker.dll'
    [IO.File]::WriteAllBytes($assembly, [Text.Encoding]::ASCII.GetBytes('synthetic-module'))
    $files = @{
        'AgentBroker.Worker.exe' = 'synthetic-exe'; 'AgentBroker.Worker.dll' = 'synthetic-worker'
        'Z-first.dat' = 'upper'; 'a-second.dat' = 'lower'; 'sub/note.txt' = 'nested'
    }
    $files[([string][char]0xE9 + '.txt')] = 'unicode-name'
    foreach ($name in $files.Keys) { [IO.File]::WriteAllBytes((Join-Path $workerRoot $name), [Text.Encoding]::ASCII.GetBytes($files[$name])) }
    $id = 'agentbroker'
    . ([scriptblock]::Create($beforeBranches[0].Extent.Text))
    Check ($artifactBefore.LOADED -ceq '9EFA96D1F81F837E3C18710C6B9358081A50F4F2A0E55D60FC456014C14C8134') 'MODULE_ARTIFACT_MODULE_BYTES_GOLDEN'
    Check ($artifactBefore.WORKER_PAYLOAD -ceq '527F03465B02F8FF524FFB114B5977BE112B843CA2171555F0954FA3D2D08948') 'MODULE_ARTIFACT_PAYLOAD_MANIFEST_GOLDEN'
    $r = [pscustomobject]@{ Result = 'PASS'; Lines = @(('WITNESS: MODULE_SELFTEST_LOADED_SHA256 agentbroker ' + $artifactBefore.LOADED),
        ('WITNESS: MODULE_SELFTEST_WORKER_PAYLOAD_SHA256 agentbroker ' + $artifactBefore.WORKER_PAYLOAD)) }
    . ([scriptblock]::Create($witnessCode))
    . ([scriptblock]::Create($afterBranches[0].Extent.Text))
    Check ($artifactWitnessLines.Count -eq 2) 'MODULE_ARTIFACT_REAL_SNAPSHOT_CALLER_ACCEPTED'
    $continued = Invoke-SyntheticCoveredArtifactCaller 'missing' $modulesRoot
    Check ($continued.Uncaught -ceq '' -and ($continued.Calls -join ',') -ceq 'agentbroker,other' -and
        $continued.Problems.Count -eq 1 -and $continued.Problems[0].StartsWith('agentbroker : Test-ModuleSelfTests.ps1: MODULE_ARTIFACT_WITNESS_LOADED_COUNT:', [StringComparison]::Ordinal)) 'MODULE_ARTIFACT_CALLER_FAILURE_CONTINUES'
    $failedModule = Invoke-SyntheticCoveredArtifactCaller 'failed' $modulesRoot
    Check ($failedModule.Uncaught -ceq '' -and ($failedModule.Calls -join ',') -ceq 'agentbroker,other' -and
        $failedModule.Problems.Count -eq 1 -and $failedModule.Problems[0] -ceq 'agentbroker : expected PASS, got FAIL -- original-module-failure') 'MODULE_ARTIFACT_CALLER_FAILED_RESULT_PRESERVED'
    $changedInput = Invoke-SyntheticCoveredArtifactCaller 'changed' $modulesRoot
    Check ($changedInput.Uncaught -ceq '' -and ($changedInput.Calls -join ',') -ceq 'agentbroker,other' -and
        $changedInput.Problems.Count -eq 1 -and $changedInput.Problems[0].StartsWith('agentbroker : Test-ModuleSelfTests.ps1: MODULE_ARTIFACT_INPUT_CHANGED:', [StringComparison]::Ordinal)) 'MODULE_ARTIFACT_CALLER_CHANGED_INPUT_PRIORITY'
    $plainFailure = Invoke-SyntheticCoveredArtifactCaller 'plain-exception' $modulesRoot
    Check ($plainFailure.Uncaught -ceq 'synthetic-non-artifact-failure' -and ($plainFailure.Calls -join ',') -ceq 'agentbroker' -and
        $plainFailure.Problems.Count -eq 0) 'MODULE_ARTIFACT_CALLER_NON_ARTIFACT_EXCEPTION_RETHROWN'
    $passed = Invoke-SyntheticCoveredArtifactCaller 'pass' $modulesRoot
    Check ($passed.Uncaught -ceq '' -and ($passed.Calls -join ',') -ceq 'agentbroker,other' -and
        $passed.Problems.Count -eq 0) 'MODULE_ARTIFACT_CALLER_PASS_COMPLETES'
    $expectedLoadedLine = 'WITNESS: MODULE_SELFTEST_LOADED_SHA256 agentbroker ' + $artifactBefore.LOADED
    $expectedPayloadLine = 'WITNESS: MODULE_SELFTEST_WORKER_PAYLOAD_SHA256 agentbroker ' + $artifactBefore.WORKER_PAYLOAD
    Check (@($passed.HostOutput | Where-Object { $_ -ceq $expectedLoadedLine }).Count -eq 1) 'MODULE_ARTIFACT_CALLER_LOADED_WITNESS_PRINTED_ONCE'
    Check (@($passed.HostOutput | Where-Object { $_ -ceq $expectedPayloadLine }).Count -eq 1) 'MODULE_ARTIFACT_CALLER_PAYLOAD_WITNESS_PRINTED_ONCE'
    [IO.File]::WriteAllBytes($assembly, [Text.Encoding]::ASCII.GetBytes('changed-module'))
    RefusesArtifact { . ([scriptblock]::Create($afterBranches[0].Extent.Text)) } 'MODULE_ARTIFACT_INPUT_CHANGED' 'MODULE_ARTIFACT_CALLER_CHANGED_MODULE'
    [IO.File]::WriteAllBytes($assembly, [Text.Encoding]::ASCII.GetBytes('synthetic-module'))
    $extra = Join-Path $workerRoot 'new.dat'
    [IO.File]::WriteAllBytes($extra, [Text.Encoding]::ASCII.GetBytes('new-worker-file'))
    RefusesArtifact { . ([scriptblock]::Create($afterBranches[0].Extent.Text)) } 'MODULE_ARTIFACT_INPUT_CHANGED' 'MODULE_ARTIFACT_CALLER_CHANGED_PAYLOAD'
    [IO.File]::Delete($extra)
    foreach ($name in @('AgentBroker.Worker.exe', 'AgentBroker.Worker.dll')) {
        $missing = Join-Path $workerRoot $name
        [IO.File]::Delete($missing)
        RefusesArtifact { Get-AgentBrokerArtifactSnapshot $modulesRoot } 'MODULE_ARTIFACT_INPUT_MISSING' ('MODULE_ARTIFACT_MISSING_' + $name)
        [IO.File]::WriteAllBytes($missing, [Text.Encoding]::ASCII.GetBytes($files[$name]))
    }
    [IO.File]::Delete($assembly)
    RefusesArtifact { Get-AgentBrokerArtifactSnapshot $modulesRoot } 'MODULE_ARTIFACT_INPUT_MISSING' 'MODULE_ARTIFACT_MISSING_MODULE'
    $emptyModules = Join-Path $fixture 'empty-modules'
    $emptyModule = Join-Path $emptyModules 'agentbroker'
    [void][IO.Directory]::CreateDirectory((Join-Path $emptyModule 'worker'))
    [IO.File]::WriteAllBytes((Join-Path $emptyModule 'AgentBroker.dll'), [Text.Encoding]::ASCII.GetBytes('synthetic-module'))
    RefusesArtifact { Get-AgentBrokerArtifactSnapshot $emptyModules } 'MODULE_ARTIFACT_INPUT_MISSING' 'MODULE_ARTIFACT_EMPTY_PAYLOAD_REFUSED'
    $id = 'other'; $modulesRoot = Join-Path $fixture 'not-built'; $artifactBefore = $null
    . ([scriptblock]::Create($beforeBranches[0].Extent.Text))
    . ([scriptblock]::Create($afterBranches[0].Extent.Text))
    Check ($null -eq $artifactBefore) 'MODULE_ARTIFACT_OTHER_MODULE_OPENS_NO_ARTIFACTS'
}
finally {
    # Only the fresh named fixture is removed; this check precedes recursive deletion.
    if ($fixture.StartsWith($fixturePrefix, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($fixture) -ceq $fixtureName -and [IO.Directory]::Exists($fixture)) {
        [IO.Directory]::Delete($fixture, $true)
    }
}
Check (-not [IO.Directory]::Exists($fixture)) 'MODULE_ARTIFACT_FIXTURE_REMOVED'
Write-Output "ModuleAssertionChecks=$checks"
