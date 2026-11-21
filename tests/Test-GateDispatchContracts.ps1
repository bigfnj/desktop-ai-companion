#requires -Version 5
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$gate = Join-Path $PSScriptRoot 'run-gate.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($gate, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'run-gate.ps1: GATE_PARSE' }
$checks = 0
function Check([bool]$Value, [string]$Name) {
    if (-not $Value) { throw ('run-gate.ps1: ' + $Name) }
    $script:checks++
}
function Direct-Try([string]$Target) {
    return @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.TryStatementAst] -and
        @($node.Body.Statements | Where-Object {
            $_ -is [Management.Automation.Language.PipelineAst] -and $_.Extent.Text.Contains($Target)
        }).Count -eq 1
    }, $true))
}
$discovery = @(Direct-Try 'Test-ModuleSourceDiscovery.ps1')
$freshness = @(Direct-Try 'Test-ModulePublishFreshness.ps1')
Check ($discovery.Count -eq 1 -and $freshness.Count -eq 1 -and
    $discovery[0].Extent.StartOffset -ne $freshness[0].Extent.StartOffset) 'MODULE_CHECKS_COLLECT_INDEPENDENTLY'
$broker = @(Direct-Try 'Test-AgentBroker.ps1')
Check ($broker.Count -eq 1 -and $broker[0].Body.Statements[0].Extent.Text -match '\s-CheckPayload(?:\s|$)') 'SHIPPED_BROKER_PAYLOAD_IS_GATED'
$parent = Join-Path $repo 'artifacts'
$scratch = Join-Path $parent ('gate-dispatch-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Join-Path $scratch 'tests'))
[void][IO.Directory]::CreateDirectory((Join-Path $scratch 'packaging'))
$discoveryFile = Join-Path $scratch 'tests\Test-ModuleSourceDiscovery.ps1'
$freshnessFile = Join-Path $scratch 'packaging\Test-ModulePublishFreshness.ps1'
$utf8 = New-Object Text.UTF8Encoding($false)
$previousTrace = Get-Variable -Name gateDispatchFixtureTrace -Scope Global -ErrorAction SilentlyContinue
try {
    [IO.File]::WriteAllText($discoveryFile, '$global:gateDispatchFixtureTrace.Add("discovery"); throw "synthetic_discovery_failure"', $utf8)
    [IO.File]::WriteAllText($freshnessFile, '$global:gateDispatchFixtureTrace.Add("freshness")', $utf8)
    $global:gateDispatchFixtureTrace = New-Object 'Collections.Generic.List[string]'
    $failures = New-Object 'Collections.Generic.List[string]'
    $repoRoot = $scratch
    . ([scriptblock]::Create($discovery[0].Extent.Text))
    . ([scriptblock]::Create($freshness[0].Extent.Text))
    Check (($global:gateDispatchFixtureTrace -join ',') -ceq 'discovery,freshness') 'FRESHNESS_RUNS_AFTER_DISCOVERY_FAILURE'
    Check ($failures.Count -eq 1 -and $failures[0] -ceq 'Test-ModuleSourceDiscovery.ps1: synthetic_discovery_failure') 'DISCOVERY_FAILURE_NAMES_ITS_SOURCE'
} finally {
    if ($null -ne $previousTrace) { $global:gateDispatchFixtureTrace = $previousTrace.Value }
    else { Remove-Variable -Name gateDispatchFixtureTrace -Scope Global -ErrorAction SilentlyContinue }
    $full = [IO.Path]::GetFullPath($scratch)
    if ([IO.Path]::GetDirectoryName($full) -cne [IO.Path]::GetFullPath($parent) -or
        [IO.Path]::GetFileName($full) -cnotmatch '^gate-dispatch-[0-9a-f]{32}$') { throw 'Gate fixture cleanup scope changed.' }
    foreach ($path in @($discoveryFile, $freshnessFile)) { [IO.File]::Delete($path) }
    [IO.Directory]::Delete((Join-Path $scratch 'tests'), $false)
    [IO.Directory]::Delete((Join-Path $scratch 'packaging'), $false)
    [IO.Directory]::Delete($scratch, $false)
}
Check (-not [IO.Directory]::Exists($scratch)) 'GATE_FIXTURE_REMOVED'
Write-Output "GateDispatchChecks=$checks"
