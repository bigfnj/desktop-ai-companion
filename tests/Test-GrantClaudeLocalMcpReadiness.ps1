#requires -Version 5
param([string]$RepoRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools\agentbroker\Grant-ClaudeLocalMcpAllowance.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'GRANT_READINESS_SOURCE: grant script does not parse.' }
$tries = @($ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] })
if ($tries.Count -lt 1) { throw 'GRANT_READINESS_SOURCE: live controller was not found.' }
$controllerAst = $tries[-1]
$controllerText = $controllerAst.Extent.Text
$apiTypes = @($controllerAst.FindAll({ param($node) $node -is [Management.Automation.Language.TypeExpressionAst] -and
    $node.TypeName.FullName -ieq 'ClaudeUserGrant' }, $true) | Sort-Object { $_.Extent.StartOffset } -Descending)
if ($apiTypes.Count -eq 0) { throw 'GRANT_READINESS_SOURCE: grant API calls were not found.' }
foreach ($type in $apiTypes) {
    $offset = $type.Extent.StartOffset - $controllerAst.Extent.StartOffset
    $controllerText = $controllerText.Remove($offset, $type.Extent.EndOffset - $type.Extent.StartOffset).Insert($offset, '[AgentBrokerLocalMcpReadinessFixture]')
}
$controller = [scriptblock]::Create($controllerText)
# Only the actual final controller is executed, with a synthetic API. No policy, CLI,
# canonical settings, native file API or script-level live entry point is invoked.
if ($null -eq ('AgentBrokerLocalMcpReadinessFixture' -as [type])) {
Add-Type -TypeDefinition @'
using System;
public sealed class AgentBrokerLocalReadinessPlanFixture {
    public bool ChangeNeeded { get { return true; } }
    public int ExistingAllowedRules { get { return 2; } }
    public bool AllOtherValuesPreserved { get { return true; } }
}
public static class AgentBrokerLocalMcpReadinessFixture {
    public const string Url="http://127.0.0.1:7432/mcp";
    public static int VerifyCalls,PublishCalls;
    public static AgentBrokerLocalReadinessPlanFixture PrepareCanonical() { return new AgentBrokerLocalReadinessPlanFixture(); }
    public static bool VerifyPinnedCli() { VerifyCalls++; return false; }
    public static object Publish(AgentBrokerLocalReadinessPlanFixture plan) { PublishCalls++; throw new InvalidOperationException("synthetic_publish_must_not_run"); }
    public static string RefusalReason(Exception error) { return error.GetBaseException().Message; }
}
'@
}
[AgentBrokerLocalMcpReadinessFixture]::VerifyCalls = 0
[AgentBrokerLocalMcpReadinessFixture]::PublishCalls = 0
$Apply = $false
$output = & $controller
$result = $output | ConvertFrom-Json
if ([AgentBrokerLocalMcpReadinessFixture]::VerifyCalls -ne 1 -or $result.CliPinVerified -or
    $result.CliPinStatus -cne 'cli_pin_mismatch' -or -not $result.DryRun -or -not $result.ChangeNeeded -or $result.SettingsChanged) {
    throw 'GRANT_DRY_RUN_PIN_PROBED: dry run did not distinguish a needed change from an unverified CLI.'
}
$Apply = $true
$message = ''
try { $null = & $controller } catch { $message = $_.Exception.Message }
if ([AgentBrokerLocalMcpReadinessFixture]::VerifyCalls -ne 2 -or [AgentBrokerLocalMcpReadinessFixture]::PublishCalls -ne 0 -or
    -not $message.Contains('refused: cli_pin_mismatch.')) {
    throw 'GRANT_APPLY_PIN_REQUIRED: failed CLI verification did not refuse before publication with its fixed cause.'
}
Write-Output 'SUMMARY: Test-GrantClaudeLocalMcpReadiness passed 2 checks; no live policy, CLI or settings access.'
