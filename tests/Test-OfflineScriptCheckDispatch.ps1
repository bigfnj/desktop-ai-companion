#requires -Version 5.1
<#
.SYNOPSIS
    Run the real offline runner against disposable script fixtures and prove each new dispatch can fail.
#>
[CmdletBinding()]
param([string]$PythonExecutable = '', [switch]$HealthyOnly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$pwsh = @(Get-Command -Name 'pwsh' -CommandType Application -ErrorAction SilentlyContinue) | Select-Object -First 1
if ($null -eq $pwsh) { throw 'Dispatch fixtures require PowerShell 7.' }
if ([string]::IsNullOrWhiteSpace($PythonExecutable)) {
    $PythonExecutable = [Environment]::GetEnvironmentVariable('TOOLBOX_PYTHON', 'Process')
    if ([string]::IsNullOrWhiteSpace($PythonExecutable)) {
        $python = @(Get-Command -Name 'python' -CommandType Application -ErrorAction SilentlyContinue) | Select-Object -First 1
        if ($null -eq $python) { throw 'Dispatch fixtures require Python 3.10 or newer.' }
        $PythonExecutable = $python.Source
    }
}

# This expected contract is independent of the runner's registry. Removing a route from that registry
# must fail the healthy fixture too, even though the remaining children all succeed.
$checks = @(
    @{ Name = 'Test-AgentBrokerReleaseProvenanceRules.ps1'; Relative = 'tests/Test-AgentBrokerReleaseProvenanceRules.ps1'; Summary = 'PROVENANCE_RULES=52 checks passed; no SDK build; owned fixture removed'; New = $true },
    @{ Name = 'Test-AgentBrokerFixtureReadiness.ps1'; Relative = 'tests/Test-AgentBrokerFixtureReadiness.ps1'; Summary = 'PASS: 3 fixture readiness checks; owned children and fixture files cleaned.'; New = $true },
    @{ Name = 'Test-ModulePublishCommit.ps1'; Relative = 'tests/Test-ModulePublishCommit.ps1'; Summary = 'SUMMARY: Test-ModulePublishCommit passed 1 checks; synthetic repository removed.'; New = $true },
    @{ Name = 'Test-CatalogDeadChild.ps1'; Relative = 'tests/Test-CatalogDeadChild.ps1'; Summary = 'SUMMARY: Test-CatalogDeadChild passed 1 checks; owned children disposed.'; New = $true },
    @{ Name = 'Test-GrantClaudeLocalMcpReadiness.ps1'; Relative = 'tests/Test-GrantClaudeLocalMcpReadiness.ps1'; Summary = 'SUMMARY: Test-GrantClaudeLocalMcpReadiness passed 1 checks; no live policy, CLI or settings access.'; New = $true },
    @{ Name = 'Refresh-AgentBrokerStatus.ps1'; Relative = 'tools/agentbroker/Refresh-AgentBrokerStatus.ps1'; Summary = '{"SelfTestPassed":true,"Checks":1}'; SelfTest = $true; New = $true },
    @{ Name = 'Test-LocalAgentBrokerHealth.ps1'; Relative = 'tools/agentbroker/Test-LocalAgentBrokerHealth.ps1'; Summary = 'PublicMetadataChecks=1'; SelfTest = $true; New = $true },
    @{ Name = 'Test-CompletedWorkerBinaryCleanup.ps1'; Relative = 'tests/Test-CompletedWorkerBinaryCleanup.ps1'; Summary = '  "Result": "PASS",'; New = $true },
    @{ Name = 'Test-BacklogCriteriaSources.ps1'; Relative = 'tests/Test-BacklogCriteriaSources.ps1'; Summary = 'CriteriaSourceChecks=1'; New = $true },
    @{ Name = 'Test-CodexEditorProbePreparation.ps1'; Relative = 'tests/Test-CodexEditorProbePreparation.ps1'; Summary = 'PASS: 1 preparation checks; no GUI launched.'; New = $true },
    @{ Name = 'Test-OwnedTestTree.ps1'; Relative = 'tests/Test-OwnedTestTree.ps1'; Summary = 'OwnedTreeChecks=1'; New = $true },
    @{ Name = 'Test-GateDispatchContracts.ps1'; Relative = 'tests/Test-GateDispatchContracts.ps1'; Summary = 'GateDispatchChecks=1'; New = $true },
    @{ Name = 'Test-HarnessOutputContracts.ps1'; Relative = 'tests/Test-HarnessOutputContracts.ps1'; Summary = 'HarnessOutputChecks=1'; New = $true },
    @{ Name = 'Install-LocalAgentBroker.ps1'; Relative = 'tools/agentbroker/Install-LocalAgentBroker.ps1'; Summary = 'PathPolicyChecks=1'; SelfTest = $true; New = $true },
    @{ Name = 'Grant-ClaudeLocalMcpAllowance.ps1'; Relative = 'tools/agentbroker/Grant-ClaudeLocalMcpAllowance.ps1'; Summary = '{"SelfTestPassed":true,"Checks":1,"LiveSettingsReadOrChanged":false,"FixtureCleaned":true}'; SelfTest = $true; New = $true },
    @{ Name = 'Inspect-ClaudeMcpPolicy.ps1'; Relative = 'tools/agentbroker/Inspect-ClaudeMcpPolicy.ps1'; Summary = '{"SelfTestPassed":true,"Checks":1,"LivePolicyRead":false}'; SelfTest = $true; New = $true },
    @{ Name = 'Inspect-ClaudeMcpEntry.mjs'; Relative = 'tools/agentbroker/Inspect-ClaudeMcpEntry.mjs'; Summary = '{"status":"self_test_passed","checks":1}'; SelfTest = $true; New = $true },
    @{ Name = 'Probe-IsolatedClaudeMcpSetup.mjs'; Relative = 'tools/agentbroker/Probe-IsolatedClaudeMcpSetup.mjs'; Summary = '{"status":"self_test_passed","checks":1,"fixtureRemoved":true,"publicFilesOpened":false,"cliLaunched":false}'; SelfTest = $true; New = $true },
    @{ Name = 'Test-InstallerMsiCallerSafety.ps1'; Relative = 'tests/Test-InstallerMsiCallerSafety.ps1'; Summary = ''; New = $false },
    @{ Name = 'Test-InstallerNativeCheckTimeout.ps1'; Relative = 'tests/Test-InstallerNativeCheckTimeout.ps1'; Summary = ''; New = $false },
    @{ Name = 'Test-InstallerPathSafety.ps1'; Relative = 'tests/Test-InstallerPathSafety.ps1'; Summary = ''; New = $false },
    @{ Name = 'Test-InstallerMsiLogObservation.ps1'; Relative = 'tests/Test-InstallerMsiLogObservation.ps1'; Summary = ''; New = $false },
    @{ Name = 'Test-TrayIconRecords.ps1'; Relative = 'tests/Test-TrayIconRecords.ps1'; Summary = ''; New = $false },
    @{ Name = 'Test-PublicCommitPrivacy.ps1'; Relative = 'tests/Test-PublicCommitPrivacy.ps1'; Summary = 'Public privacy self-test passed: 1 cases (synthetic).'; SelfTest = $true; New = $true },
    @{ Name = 'Inspect-CodexMcpExposure.ps1'; Relative = 'tools/agentbroker/Inspect-CodexMcpExposure.ps1'; Summary = 'PASS: 1 exposure classification controls; no client or endpoint invoked.'; SelfTest = $true; New = $true },
    @{ Name = 'Invoke-MutationShards.ps1'; Relative = 'tools/agentbroker/Invoke-MutationShards.ps1'; Summary = 'SelfTestChecks=1'; SelfTest = $true; New = $true },
    @{ Name = 'Test-MutationShardRemainder.ps1'; Relative = 'tests/Test-MutationShardRemainder.ps1'; Summary = 'RemainderSyntheticChecks=1'; New = $true },
    @{ Name = 'Test-MutationTempBudget.ps1'; Relative = 'tests/Test-MutationTempBudget.ps1'; Summary = 'Checks=1'; New = $true },
    @{ Name = 'Test-IsolatedDotnetEnvironment.ps1'; Relative = 'tests/Test-IsolatedDotnetEnvironment.ps1'; Summary = '{"Result":"PASS","Checks":1,"Synthetic":true}'; New = $true },
    @{ Name = 'Test-OfflineScriptCheckDispatch.ps1'; Relative = 'tests/Test-OfflineScriptCheckDispatch.ps1'; Summary = 'OfflineDispatchChecks=1'; New = $true },
    @{ Name = 'run_installer_shutdown_mutations.py --self-test'; Relative = 'tests/run_installer_shutdown_mutations.py'; Summary = 'PASS 1 restoration cases (synthetic)'; SelfTest = $true; New = $true },
    @{ Name = 'Test-MutationHarnessRestoration.py'; Relative = 'tests/Test-MutationHarnessRestoration.py'; Summary = 'MutationRestorationChecks=1'; New = $true },
    @{ Name = 'run_mutation_capture_checks.py'; Relative = 'tests/run_mutation_capture_checks.py'; Summary = 'MutationCaptureChecks=1; Failures=0; Errors=0; Skipped=0'; New = $true }
)
$scratchBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = Join-Path $scratchBase ('offline-dispatch-' + [guid]::NewGuid().ToString('N'))
$utf8 = New-Object Text.UTF8Encoding($false)
$oldFailure = [Environment]::GetEnvironmentVariable('OFFLINE_DISPATCH_FAILURE', 'Process')
$oldSilent = [Environment]::GetEnvironmentVariable('OFFLINE_DISPATCH_SILENT', 'Process')
$count = 0
try {
    [void][IO.Directory]::CreateDirectory((Join-Path $scratch 'tests'))
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Invoke-OfflineScriptChecks.ps1') -Destination (Join-Path $scratch 'tests/Invoke-OfflineScriptChecks.ps1')
    foreach ($check in $checks) {
        $path = Join-Path $scratch $check.Relative
        [void][IO.Directory]::CreateDirectory((Split-Path $path -Parent))
        $requiresSelfTest = $check.ContainsKey('SelfTest') -and $check.SelfTest
        if ($path.EndsWith('.py', [StringComparison]::Ordinal)) {
            $stub = @'
import os, sys
name = NAME
if sys.argv[1:] != ARGS:
    raise SystemExit(23)
print("DISPATCH-WITNESS:" + name, flush=True)
if os.environ.get("OFFLINE_DISPATCH_FAILURE") == name:
    raise SystemExit(17)
if os.environ.get("OFFLINE_DISPATCH_SILENT") != name:
    print(SUMMARY)
'@
            $stub = $stub.Replace('NAME', ($check.Name | ConvertTo-Json -Compress)).Replace('ARGS', $(if ($requiresSelfTest) { '["--self-test"]' } else { '[]' })).Replace('SUMMARY', ($check.Summary | ConvertTo-Json -Compress))
        }
        elseif ($path.EndsWith('.mjs', [StringComparison]::Ordinal)) {
            $stub = @'
const name = NAME;
if (JSON.stringify(process.argv.slice(2)) !== '["--self-test"]') process.exit(23);
console.log('DISPATCH-WITNESS:' + name);
if (process.env.OFFLINE_DISPATCH_FAILURE === name) process.exit(17);
if (process.env.OFFLINE_DISPATCH_SILENT !== name) console.log(SUMMARY);
'@
            $stub = $stub.Replace('NAME', ($check.Name | ConvertTo-Json -Compress)).Replace('SUMMARY', ($check.Summary | ConvertTo-Json -Compress))
        }
        else {
            $stub = @'
[CmdletBinding()]
param([switch]$SelfTest, [string]$PythonExecutable = '')
$name = 'NAME'
if ($SelfTest.IsPresent -ne EXPECT_SELF_TEST) { throw 'fixture_arguments_wrong' }
Write-Output ('DISPATCH-WITNESS:' + $name)
if ([Environment]::GetEnvironmentVariable('OFFLINE_DISPATCH_FAILURE', 'Process') -ceq $name) { throw 'dispatch_synthetic_failure' }
if ([Environment]::GetEnvironmentVariable('OFFLINE_DISPATCH_SILENT', 'Process') -cne $name) { Write-Output 'SUMMARY' }
'@
            $stub = $stub.Replace('NAME', $check.Name).Replace('SUMMARY', $check.Summary).Replace('EXPECT_SELF_TEST', $(if ($requiresSelfTest) { '$true' } else { '$false' }))
        }
        [IO.File]::WriteAllText($path, $stub, $utf8)
    }

    $scenarios = @(@{ Name = 'healthy'; Failure = ''; Silent = '' })
    foreach ($check in $checks) {
        if ($check.New) {
            $scenarios += @{ Name = $check.Name; Failure = $check.Name; Silent = '' }
            $scenarios += @{ Name = ('missing summary: ' + $check.Name); Failure = ''; Silent = $check.Name }
        }
    }
    if ($HealthyOnly) { $scenarios = @($scenarios[0]) }
    foreach ($scenario in $scenarios) {
        [Environment]::SetEnvironmentVariable('OFFLINE_DISPATCH_FAILURE', $scenario.Failure, 'Process')
        [Environment]::SetEnvironmentVariable('OFFLINE_DISPATCH_SILENT', $scenario.Silent, 'Process')
        $savedPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $output = @(& $pwsh.Source -NoProfile -File (Join-Path $scratch 'tests/Invoke-OfflineScriptChecks.ps1') -PythonExecutable $PythonExecutable 2>&1)
            $code = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $savedPreference }
        $text = ($output | ForEach-Object { "$_" }) -join "`n"
        foreach ($check in $checks) {
            if (@($output | Where-Object { "$_" -ceq ('DISPATCH-WITNESS:' + $check.Name) }).Count -ne 1) {
                throw ('Dispatch fixture did not run exactly once: ' + $check.Name + ' in ' + $scenario.Name)
            }
        }
        if ($scenario.Name -ceq 'healthy') {
            if ($code -ne 0 -or -not $text.Contains("PASS: $($checks.Count) offline script checks")) {
                throw ('Healthy dispatch fixture failed: ' + $text)
            }
        }
        else {
            $target = if ($scenario.Silent) { $scenario.Silent } else { $scenario.Failure }
            if ($code -eq 0 -or -not $text.Contains("1 of $($checks.Count) offline script check(s) failed: " + $target)) {
                throw ('Dispatch failure did not name exactly its one check: ' + $target)
            }
            if ($scenario.Silent) {
                $signals = @($output | Where-Object { "$_" -cmatch '^OFFLINE-SUMMARY-MISSING:' })
                if ($signals.Count -ne 1 -or "$($signals[0])" -cne ('OFFLINE-SUMMARY-MISSING:' + $target)) {
                    throw ('Summary-missing signal did not name exactly its check: ' + $target)
                }
            }
        }
        if (-not $scenario.Silent -and @($output | Where-Object { "$_" -cmatch '^OFFLINE-SUMMARY-MISSING:' }).Count -ne 0) {
            throw ('Unexpected summary-missing signal: ' + $scenario.Name)
        }
        $count++
        Write-Output ('PASS dispatch fixture: ' + $scenario.Name)
    }
}
finally {
    [Environment]::SetEnvironmentVariable('OFFLINE_DISPATCH_FAILURE', $oldFailure, 'Process')
    [Environment]::SetEnvironmentVariable('OFFLINE_DISPATCH_SILENT', $oldSilent, 'Process')
    $resolved = [IO.Path]::GetFullPath($scratch)
    if (-not $resolved.StartsWith(($scratchBase.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -cnotmatch '^offline-dispatch-[0-9a-f]{32}$') { throw 'Dispatch fixture cleanup boundary invalid.' }
    if ([IO.Directory]::Exists($resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
Write-Output "OfflineDispatchChecks=$count"
