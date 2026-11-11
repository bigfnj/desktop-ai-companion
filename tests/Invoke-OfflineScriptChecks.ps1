#requires -Version 5
<#
.SYNOPSIS
    Run script checks with no product build, install, elevation, external network requests or real input.
    AgentBroker readiness opens loopback fixture listeners without sending requests. Collect every
    failure: the installer's fake-caller, path-safety, checker-timeout and log-observation checks, the tray icon record
    cleanup check, the public-commit privacy self-test, mutation-tooling self-tests, and AgentBroker readiness/provenance checks.

.DESCRIPTION
    N-installer-tests-02 (lane feature/followups, 2026-10-29). These five ran only by hand, so the reparse-tag,
    rollback-evidence and privacy-matcher checks added by lane installer-tests guarded nothing. ONE runner, called by
    tests\run-gate.ps1 and by .github\workflows\build.yml's "Validate native installer shutdown" step, so the gate and
    CI cannot list different checks: Invoke-SelfTests.ps1 is shared the same way because two hand-kept copies drifted.

    Measured 2026-10-29 under Windows PowerShell 5.1 and PowerShell 7, each in a fresh process: 0.6 to 4.3 s apiece.
    What each leaves behind is an evidence folder under build\ (gitignored). Along the way they create and remove a
    junction under build\ and a sentinel folder under artifacts\, start one hidden synthetic PowerShell child that the
    timeout check kills after 2.5 s, create and remove a synthetic git repository under build\privacy-selftest\, and
    read the Desktop and local application data folder PATHS without opening anything in them. Test-TrayIconRecords.ps1
    (added 2026-11-02) creates and removes a scratch key under HKCU\Software and never opens the real tray records. No
    MSI is executed and no native binary is built or launched. The mutation TEMP-budget test compiles the existing
    small C# fixture method with Add-Type; mutation restoration tests use disposable sources and fake builds.

    AgentBroker provenance checks read a fixed public source/ZIP fixture from local Git history and run current
    provenance APIs plus an in-memory metadata reader against owned copies. They require PowerShell 7 and the
    recorded baseline commit locally, attempt no fetch, and perform no SDK build, install or payload execution.

    The four installer checks signal failure by throwing, so they run in this process inside a try/catch. The privacy
    self-test is `#requires -Version 7`, which Windows PowerShell refuses to run in-process, so it always runs in a
    pwsh child and is judged by its exit code; a missing pwsh is a FAILURE that says so, never a skip. Every check runs
    whatever the one before it did, and the failures are thrown together at the end, which fails a CI step and is
    collected by the gate like its other .ps1 steps.

.EXAMPLE
    .\tests\Invoke-OfflineScriptChecks.ps1
#>
[CmdletBinding()]
param([string]$PythonExecutable = '')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$failures = New-Object 'Collections.Generic.List[string]'
$ran = 0

foreach ($name in @('Test-InstallerMsiCallerSafety.ps1', 'Test-InstallerNativeCheckTimeout.ps1',
                    'Test-InstallerPathSafety.ps1', 'Test-InstallerMsiLogObservation.ps1', 'Test-TrayIconRecords.ps1')) {
    Write-Host "=== $name"
    $ran++
    # Its result lines (the JSON verdict and the Evidence= folder) are shown, not returned: this runner's own output is
    # its verdict alone.
    try { & (Join-Path $PSScriptRoot $name) | ForEach-Object { Write-Host "$_" } }
    catch { $failures.Add($name + ': ' + $_.Exception.Message) }
}

$pwsh = @(Get-Command -Name 'pwsh' -CommandType Application -ErrorAction SilentlyContinue) | Select-Object -First 1
function Invoke-CheckedChild([string]$Name, [string]$Executable, [string[]]$Arguments, [string]$Summary) {
    Write-Host "=== $Name"
    $script:ran++
    if ([string]::IsNullOrWhiteSpace($Executable)) {
        $script:failures.Add($Name + ': required interpreter unavailable; did not run')
        return
    }
    $childExit = $null
    $output = @()
    $savedPreference = $ErrorActionPreference
    # Native stderr is nonterminating here on Windows PowerShell too, so its exit code remains available.
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& $Executable @Arguments)
        $childExit = $LASTEXITCODE
    }
    catch { $script:failures.Add($Name + ': could not start: ' + $_.Exception.GetType().Name) }
    finally { $ErrorActionPreference = $savedPreference }
    foreach ($line in $output) { Write-Host "$line" }
    if ($null -eq $childExit -or $childExit -ne 0) { $script:failures.Add("$Name (exit $childExit)") }
    elseif (@($output | Where-Object { "$_" -match $Summary }).Count -ne 1) {
        # A short plain discriminator survives either shell's formatted exception wrapping.
        Write-Output ('OFFLINE-SUMMARY-MISSING:' + $Name)
        $script:failures.Add($Name + ': missing or ambiguous passing summary; did not prove the check ran')
    }
}

$pwshPath = if ($null -eq $pwsh) { '' } else { $pwsh.Source }
if ([string]::IsNullOrWhiteSpace($PythonExecutable)) {
    $PythonExecutable = [Environment]::GetEnvironmentVariable('TOOLBOX_PYTHON', 'Process')
    if ([string]::IsNullOrWhiteSpace($PythonExecutable)) {
        $python = @(Get-Command -Name 'python' -CommandType Application -ErrorAction SilentlyContinue) | Select-Object -First 1
        $PythonExecutable = if ($null -eq $python) { '' } else { $python.Source }
    }
}
if (-not [string]::IsNullOrWhiteSpace($PythonExecutable)) {
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $PythonExecutable -B -c 'import sys; sys.exit(0 if sys.version_info >= (3, 10) else 1)'
        if ($LASTEXITCODE -ne 0) { $PythonExecutable = '' }
    }
    catch { $PythonExecutable = '' }
    finally { $ErrorActionPreference = $savedPreference }
}

$powerShellChecks = @(
    @{ Name = 'Test-AgentBrokerReleaseProvenanceRules.ps1'; Path = (Join-Path $PSScriptRoot 'Test-AgentBrokerReleaseProvenanceRules.ps1'); Args = @(); Summary = '^PROVENANCE_RULES=52 checks passed; no SDK build; owned fixture removed$' },
    @{ Name = 'Test-AgentBrokerFixtureReadiness.ps1'; Path = (Join-Path $PSScriptRoot 'Test-AgentBrokerFixtureReadiness.ps1'); Args = @(); Summary = '^PASS: 3 fixture readiness checks; owned children and fixture files cleaned\.$' },
    @{ Name = 'Test-ModulePublishCommit.ps1'; Path = (Join-Path $PSScriptRoot 'Test-ModulePublishCommit.ps1'); Args = @(); Summary = '^SUMMARY: Test-ModulePublishCommit passed [1-9][0-9]* checks; synthetic repository removed\.$' },
    @{ Name = 'Test-CatalogDeadChild.ps1'; Path = (Join-Path $PSScriptRoot 'Test-CatalogDeadChild.ps1'); Args = @(); Summary = '^SUMMARY: Test-CatalogDeadChild passed [1-9][0-9]* checks; owned children disposed\.$' },
    @{ Name = 'Test-GrantClaudeLocalMcpReadiness.ps1'; Path = (Join-Path $PSScriptRoot 'Test-GrantClaudeLocalMcpReadiness.ps1'); Args = @(); Summary = '^SUMMARY: Test-GrantClaudeLocalMcpReadiness passed [1-9][0-9]* checks; no live policy, CLI or settings access\.$' },
    @{ Name = 'Refresh-AgentBrokerStatus.ps1'; Path = (Join-Path $PSScriptRoot '..\tools\agentbroker\Refresh-AgentBrokerStatus.ps1'); Args = @('-SelfTest'); Summary = '^\{"SelfTestPassed":true,"Checks":[1-9][0-9]*\}$' },
    @{ Name = 'Test-LocalAgentBrokerHealth.ps1'; Path = (Join-Path $PSScriptRoot '..\tools\agentbroker\Test-LocalAgentBrokerHealth.ps1'); Args = @('-SelfTest'); Summary = '^PublicMetadataChecks=[1-9][0-9]*$' },
    @{ Name = 'Test-CompletedWorkerBinaryCleanup.ps1'; Path = (Join-Path $PSScriptRoot 'Test-CompletedWorkerBinaryCleanup.ps1'); Args = @(); Summary = '^\s*"Result":\s*"PASS",?$' },
    @{ Name = 'Test-BacklogCriteriaSources.ps1'; Path = (Join-Path $PSScriptRoot 'Test-BacklogCriteriaSources.ps1'); Args = @(); Summary = '^CriteriaSourceChecks=[1-9][0-9]*$' },
    @{ Name = 'Test-CodexEditorProbePreparation.ps1'; Path = (Join-Path $PSScriptRoot 'Test-CodexEditorProbePreparation.ps1'); Args = @(); Summary = '^PASS: [1-9][0-9]* preparation checks; no GUI launched\.$' },
    @{ Name = 'Test-OwnedTestTree.ps1'; Path = (Join-Path $PSScriptRoot 'Test-OwnedTestTree.ps1'); Args = @(); Summary = '^OwnedTreeChecks=[1-9][0-9]*$' },
    @{ Name = 'Test-GateDispatchContracts.ps1'; Path = (Join-Path $PSScriptRoot 'Test-GateDispatchContracts.ps1'); Args = @(); Summary = '^GateDispatchChecks=[1-9][0-9]*$' },
    @{ Name = 'Test-HarnessOutputContracts.ps1'; Path = (Join-Path $PSScriptRoot 'Test-HarnessOutputContracts.ps1'); Args = @(); Summary = '^HarnessOutputChecks=[1-9][0-9]*$' },
    @{ Name = 'Install-LocalAgentBroker.ps1'; Path = (Join-Path $PSScriptRoot '..\tools\agentbroker\Install-LocalAgentBroker.ps1'); Args = @('-SelfTest'); Summary = '^PathPolicyChecks=[1-9][0-9]*$' },
    @{ Name = 'Grant-ClaudeLocalMcpAllowance.ps1'; Path = (Join-Path $PSScriptRoot '..\tools\agentbroker\Grant-ClaudeLocalMcpAllowance.ps1'); Args = @('-SelfTest'); Summary = '^\{"SelfTestPassed":true,"Checks":[1-9][0-9]*,"LiveSettingsReadOrChanged":false,"FixtureCleaned":true\}$' },
    @{ Name = 'Inspect-ClaudeMcpPolicy.ps1'; Path = (Join-Path $PSScriptRoot '..\tools\agentbroker\Inspect-ClaudeMcpPolicy.ps1'); Args = @('-SelfTest'); Summary = '^\{"SelfTestPassed":true,"Checks":[1-9][0-9]*,"LivePolicyRead":false\}$' },
    @{ Name = 'Test-PublicCommitPrivacy.ps1'; Path = (Join-Path $PSScriptRoot 'Test-PublicCommitPrivacy.ps1'); Args = @('-SelfTest'); Summary = '^Public privacy self-test passed: [1-9][0-9]* cases ' },
    @{ Name = 'Inspect-CodexMcpExposure.ps1'; Path = (Join-Path $PSScriptRoot '..\tools\agentbroker\Inspect-CodexMcpExposure.ps1'); Args = @('-SelfTest'); Summary = '^PASS: [1-9][0-9]* exposure classification controls; no client or endpoint invoked\.$' },
    @{ Name = 'Invoke-MutationShards.ps1'; Path = (Join-Path $PSScriptRoot '..\tools\agentbroker\Invoke-MutationShards.ps1'); Args = @('-SelfTest'); Summary = '^SelfTestChecks=[1-9][0-9]*$' },
    @{ Name = 'Test-MutationShardRemainder.ps1'; Path = (Join-Path $PSScriptRoot 'Test-MutationShardRemainder.ps1'); Args = @(); Summary = '^RemainderSyntheticChecks=[1-9][0-9]*$' },
    @{ Name = 'Test-MutationTempBudget.ps1'; Path = (Join-Path $PSScriptRoot 'Test-MutationTempBudget.ps1'); Args = @(); Summary = '^Checks=[1-9][0-9]*$' },
    @{ Name = 'Test-IsolatedDotnetEnvironment.ps1'; Path = (Join-Path $PSScriptRoot 'Test-IsolatedDotnetEnvironment.ps1'); Args = @(); Summary = '^\{"Result":"PASS","Checks":[1-9][0-9]*,' },
    @{ Name = 'Test-OfflineScriptCheckDispatch.ps1'; Path = (Join-Path $PSScriptRoot 'Test-OfflineScriptCheckDispatch.ps1'); Args = @('-PythonExecutable', $PythonExecutable); Summary = '^OfflineDispatchChecks=[1-9][0-9]*$' }
)
foreach ($check in $powerShellChecks) {
    Invoke-CheckedChild $check.Name $pwshPath (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $check.Path) + $check.Args) $check.Summary
}
$node = @(Get-Command -Name 'node' -CommandType Application -ErrorAction SilentlyContinue) | Select-Object -First 1
$nodePath = if ($null -eq $node) { '' } else { $node.Source }
Invoke-CheckedChild 'Inspect-ClaudeMcpEntry.mjs' $nodePath `
    @((Join-Path $PSScriptRoot '..\tools\agentbroker\Inspect-ClaudeMcpEntry.mjs'), '--self-test') '^\{"status":"self_test_passed","checks":[1-9][0-9]*\}$'
Invoke-CheckedChild 'Probe-IsolatedClaudeMcpSetup.mjs' $nodePath `
    @((Join-Path $PSScriptRoot '..\tools\agentbroker\Probe-IsolatedClaudeMcpSetup.mjs'), '--self-test') '^\{"status":"self_test_passed","checks":[1-9][0-9]*,"fixtureRemoved":true,"publicFilesOpened":false,"cliLaunched":false\}$'
Invoke-CheckedChild 'run_installer_shutdown_mutations.py --self-test' $PythonExecutable `
    @('-B', '-X', 'utf8', (Join-Path $PSScriptRoot 'run_installer_shutdown_mutations.py'), '--self-test') '^PASS [1-9][0-9]* restoration cases '
Invoke-CheckedChild 'Test-MutationHarnessRestoration.py' $PythonExecutable `
    @('-B', '-X', 'utf8', (Join-Path $PSScriptRoot 'Test-MutationHarnessRestoration.py')) '^MutationRestorationChecks=[1-9][0-9]*$'
Invoke-CheckedChild 'run_mutation_capture_checks.py' $PythonExecutable `
    @('-B', '-X', 'utf8', (Join-Path $PSScriptRoot 'run_mutation_capture_checks.py')) '^MutationCaptureChecks=[1-9][0-9]*; Failures=0; Errors=0; Skipped=[0-9]+$'

if ($failures.Count -gt 0) {
    throw ("$($failures.Count) of $ran offline script check(s) failed: " + ($failures -join ' | '))
}
Write-Host "PASS: $ran offline script checks (installer, tray, privacy and mutation tooling)."
