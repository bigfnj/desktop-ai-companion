#requires -Version 5
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'HarnessOutput.ps1')
$checks = 0
function Check([bool]$Value, [string]$Name) {
    if (-not $Value) { throw ('HarnessOutput.ps1: ' + $Name) }
    $script:checks++
}
function Refuses([scriptblock]$Action, [string]$Name) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $_.Exception.Message.Contains('HarnessOutput.ps1:') }
    Check $failed $Name
}
$pattern = '^RESULT=PASS assertions=([1-9][0-9]*)$'
Check ((Assert-HarnessSummary @('RESULT=PASS assertions=40') 'synthetic' $pattern 40) -eq 40) 'HARNESS_FLOOR_BOUNDARY'
Check ((Assert-HarnessSummary @('RESULT=PASS assertions=41') 'synthetic' $pattern 40) -eq 41) 'HARNESS_INVENTORY_CAN_GROW'
Refuses { Assert-HarnessSummary @('RESULT=PASS assertions=39') 'synthetic' $pattern 40 } 'HARNESS_REGISTRATION_LOSS_REFUSED'
Refuses { Assert-HarnessSummary @('RESULT=PASS assertions=0') 'synthetic' $pattern 40 } 'HARNESS_ZERO_REFUSED'
Refuses { Assert-HarnessSummary @('RESULT=PASS assertions=40','RESULT=PASS assertions=40') 'synthetic' $pattern 40 } 'HARNESS_DUPLICATE_REFUSED'
Refuses { Assert-HarnessSummary @('PASS: unrelated') 'synthetic' $pattern 40 } 'HARNESS_SUMMARY_REQUIRED'
foreach ($line in @('FAIL: synthetic','RESULT=FAIL','SKIP: synthetic')) {
    Refuses { Assert-HarnessSummary @('RESULT=PASS assertions=40',$line) 'synthetic' $pattern 40 } 'HARNESS_FAILURE_OR_SKIP_REFUSED'
}
$hostOnly = @('PASS: module loaded','PASS: registrations disposed','RESULT=PASS')
Check ((Get-ModuleAssertionCount $hostOnly 'agentbroker') -eq 0) 'MODULE_HOST_ASSERTIONS_DO_NOT_COUNT'
Check ((Get-ModuleAssertionCount ($hostOnly + '  [other] PASS: different module') 'agentbroker') -eq 0) 'MODULE_FOREIGN_ASSERTIONS_DO_NOT_COUNT'
Check ((Get-ModuleAssertionCount ($hostOnly + '  [agentbroker] PASS: actual module assertion') 'agentbroker') -eq 1) 'MODULE_OWN_ASSERTIONS_COUNT'
Check ((Get-ModuleAssertionCount @('[agentbroker] FAIL: defect','[agentbroker] SKIP: gap') 'agentbroker') -eq 0) 'MODULE_FAILURES_DO_NOT_COUNT'
Check ((Assert-CoreRegressionSummary @('PASS: 149 DesktopAICompanion core regression groups.')) -eq 149) 'CORE_INVENTORY_BOUNDARY'
Refuses { Assert-CoreRegressionSummary @('PASS: 148 DesktopAICompanion core regression groups.') } 'CORE_REGISTRATION_LOSS_REFUSED'
$brokerScript = Join-Path $PSScriptRoot 'Test-AgentBroker.ps1'
$tokens = $null; $errors = $null
$syntax = [Management.Automation.Language.Parser]::ParseFile($brokerScript, [ref]$tokens, [ref]$errors)
$floorNodes = @($syntax.FindAll({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and $node.Left.VariablePath.UserPath -ceq 'assertionFloors' }, $true))
Check ($errors.Count -eq 0 -and $floorNodes.Count -eq 1) 'BROKER_FLOORS_HAVE_ONE_AUTHORITY'
$tables = @($floorNodes[0].Right.FindAll({ param($node) $node -is [Management.Automation.Language.HashtableAst] }, $true))
$assertionFloors = $tables[0].SafeGetValue()
Check ($assertionFloors.Count -eq 7 -and $assertionFloors.Core -eq 517 -and $assertionFloors.Relay -eq 40 -and
    $assertionFloors.Claude -eq 212 -and $assertionFloors.Desktop -eq 594 -and $assertionFloors.Adapter -eq 84 -and
    $assertionFloors.Network -eq 250 -and $assertionFloors.Worker -eq 2004) 'BROKER_FINAL_INVENTORY_FLOORS'
$calls = @($syntax.FindAll({ param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
    @($node.Right.FindAll({ param($inner) $inner -is [Management.Automation.Language.CommandAst] -and
        $inner.GetCommandName() -ceq 'Assert-HarnessSummary' }, $true)).Count -eq 1
}, $true))
Check ($calls.Count -eq 1 -and $calls[0].Parent.Parent -is [Management.Automation.Language.TryStatementAst]) 'BROKER_DIRECT_SUMMARY_GUARD_REQUIRED'
# Exercise the actual public caller for both measured inventories; retained old counts are rejection inputs.
foreach ($harness in @(
    [pscustomobject]@{ Name='Core'; Summary='^AgentBroker core smoke and regression: ([1-9][0-9]*) assertions passed\.$'; PreviousActual=488 },
    [pscustomobject]@{ Name='Worker'; Summary='^RESULT=PASS assertions=([1-9][0-9]*)$'; PreviousActual=1624 }
)) {
    $floor = [int]$assertionFloors[$harness.Name]
    foreach ($count in @($floor,($floor + 1))) {
        if ($harness.Name -ceq 'Core') { $output = @("AgentBroker core smoke and regression: $count assertions passed.") }
        else { $output = @("RESULT=PASS assertions=$count") }
        $assertions = 0
        . ([scriptblock]::Create($calls[0].Extent.Text))
        Check ($assertions -eq $count) ('BROKER_ACTUAL_' + $harness.Name.ToUpperInvariant() + '_ACCEPTS_' + $count)
    }
    foreach ($count in @(($floor - 1),$harness.PreviousActual)) {
        Check ($count -lt $floor) ('BROKER_' + $harness.Name.ToUpperInvariant() + '_OLD_OUTPUT_BELOW_FINAL_FLOOR')
        if ($harness.Name -ceq 'Core') { $output = @("AgentBroker core smoke and regression: $count assertions passed.") }
        else { $output = @("RESULT=PASS assertions=$count") }
        $assertions = 0
        $failure = ''
        try { . ([scriptblock]::Create($calls[0].Extent.Text)) } catch { $failure = $_.Exception.Message }
        $expected = 'HarnessOutput.ps1: ' + $harness.Name + ' assertion inventory fell below its recorded floor (' + $count + ' < ' + $floor + ').'
        Check ($failure -ceq $expected) ('BROKER_ACTUAL_' + $harness.Name.ToUpperInvariant() + '_REFUSES_' + $count)
    }
}
$moduleScript = Join-Path $PSScriptRoot 'Test-ModuleSelfTests.ps1'
$moduleSyntax = [Management.Automation.Language.Parser]::ParseFile($moduleScript, [ref]$tokens, [ref]$errors)
$moduleCalls = @($moduleSyntax.FindAll({ param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
    $node.Left.VariablePath.UserPath -ceq 'assertions' -and $node.Right.Extent.Text.Contains('$r.Lines')
}, $true))
Check ($errors.Count -eq 0 -and $moduleCalls.Count -eq 1) 'MODULE_ACTUAL_COUNTER_HAS_ONE_AUTHORITY'
$r = [pscustomobject]@{ Lines = @('PASS: host convention','[other] PASS: foreign assertion','RESULT=PASS') }
$id = 'agentbroker'
$minimum = 0
$assertions = -1
. ([scriptblock]::Create($moduleCalls[0].Extent.Text))
Check ($assertions -eq 0) 'MODULE_ACTUAL_CALLER_EXCLUDES_HOST_AND_FOREIGN_ASSERTIONS'
$gateSyntax = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'run-gate.ps1'), [ref]$tokens, [ref]$errors)
$coreCalls = @($gateSyntax.FindAll({ param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
    @($node.Right.FindAll({ param($inner) $inner -is [Management.Automation.Language.CommandAst] -and
        $inner.GetCommandName() -ceq 'Assert-CoreRegressionSummary' }, $true)).Count -eq 1
}, $true))
Check ($errors.Count -eq 0 -and $coreCalls.Count -eq 1 -and
    $coreCalls[0].Parent.Parent -is [Management.Automation.Language.TryStatementAst]) 'CORE_GATE_DIRECT_GUARD_REQUIRED'
$coreOutput = @('PASS: 148 DesktopAICompanion core regression groups.')
Refuses { . ([scriptblock]::Create($coreCalls[0].Extent.Text)) } 'CORE_GATE_ACTUAL_CALLER_REFUSES_REGISTRATION_LOSS'
# Parse the actual workflow run block without executing its builds or product tests.
$workflow = [IO.File]::ReadAllText((Join-Path (Split-Path $PSScriptRoot -Parent) '.github\workflows\build.yml'))
$block = [regex]::Match($workflow, '(?ms)^      - name: Build and run core regression tests\r?\n.*?        run: \|\r?\n(?<body>(?:          [^\r\n]*\r?\n|\r?\n)+)')
$ciSyntax = [Management.Automation.Language.Parser]::ParseInput($block.Groups['body'].Value, [ref]$tokens, [ref]$errors)
$ciCalls = @($ciSyntax.FindAll({ param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
    @($node.Right.FindAll({ param($inner) $inner -is [Management.Automation.Language.CommandAst] -and
        $inner.GetCommandName() -ceq 'Assert-CoreRegressionSummary' }, $true)).Count -eq 1
}, $true))
Check ($block.Success -and $errors.Count -eq 0 -and $ciCalls.Count -eq 1 -and
    $ciCalls[0].Parent -is [Management.Automation.Language.NamedBlockAst]) 'CORE_CI_DIRECT_GUARD_REQUIRED'
Refuses { . ([scriptblock]::Create($ciCalls[0].Extent.Text)) } 'CORE_CI_ACTUAL_CALLER_REFUSES_REGISTRATION_LOSS'
& (Join-Path $PSScriptRoot 'Test-ModuleAssertionContracts.ps1')
Write-Output "HarnessOutputChecks=$checks"
