#requires -Version 5.1
<#
.SYNOPSIS
    Build and run the deterministic broker, adapter, localhost TLS, and Worker harnesses.
.DESCRIPTION
    Uses synthetic sessions and fake native providers only. The Network and Worker harnesses
    create current-user nonexportable Windows KSP test keys and delete their exact owned keys
    in cleanup. No OS trust, firewall, certificate store, vendor credentials, or live turns change.
.EXAMPLE
    pwsh -NoProfile -File .\tests\Test-AgentBroker.ps1
#>
[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
Write-Host '=== AgentBroker payload source and packaging contracts'
& (Join-Path $PSScriptRoot 'Test-AgentBrokerPackaging.ps1') -RepoRoot $repoRoot
# The collaboration helper is PowerShell 7 only, so it runs in its own pwsh child even when this
# script runs under Windows PowerShell. Missing pwsh fails here; it is never skipped in silence.
Write-Host '=== AgentBroker collaboration helper validation'
$collaborationOutput = @(& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test-AgentBrokerCollaborationMcp.ps1'))
$collaborationExit = $LASTEXITCODE
foreach ($line in $collaborationOutput) { Write-Host $line }
$collaborationSummary = @($collaborationOutput | Where-Object { "$_" -match '^PASS: ([1-9][0-9]*) in-memory collaboration MCP checks;' })
if ($collaborationExit -ne 0 -or $collaborationSummary.Count -ne 1) {
    throw "AgentBroker collaboration helper validation failed (exit $collaborationExit)."
}
$collaborationChecks = [int]([regex]::Match([string]$collaborationSummary[0], '^PASS: ([0-9]+)').Groups[1].Value)
# The re-pin helper's fixture checks (broker-b's hand-back, lane feature/agentbroker-backlog-d). The helper supports
# Windows PowerShell 5.1, so it runs in a powershell.exe child whichever edition runs this script. It reads and writes
# only its own temporary fixture directory. A failed check, a missing summary line or a nonzero exit fails here.
Write-Host '=== AgentBroker re-pin helper self-test'
$repinHelper = Join-Path $repoRoot 'tools\agentbroker\Test-AgentBrokerRepin.ps1'
$repinOutput = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $repinHelper -SelfTest)
$repinExit = $LASTEXITCODE
foreach ($line in $repinOutput) { Write-Host $line }
$repinSummary = @($repinOutput | Where-Object { "$_" -match '^PASS: ([1-9][0-9]*) re-pin helper fixture checks$' })
if ($repinExit -ne 0 -or $repinSummary.Count -ne 1 -or @($repinOutput | Where-Object { "$_" -match '^(FAIL:|RESULT=FAIL)' }).Count -gt 0) {
    throw "AgentBroker re-pin helper self-test failed: tools/agentbroker/Test-AgentBrokerRepin.ps1 -SelfTest (exit $repinExit)."
}
$repinChecks = [int]([regex]::Match([string]$repinSummary[0], '^PASS: ([0-9]+)').Groups[1].Value)
# Lane broker-pins: the canonical manifest hash the helper ports from VsCodeManifestProfile needs System.Text.Json, so
# its checks run only under PowerShell 7; the same self-test runs again in a pwsh child for them.
Write-Host '=== AgentBroker re-pin helper self-test (PowerShell 7)'
$repin7Output = @(& pwsh -NoProfile -File $repinHelper -SelfTest)
$repin7Exit = $LASTEXITCODE
foreach ($line in $repin7Output) { Write-Host $line }
$repin7Summary = @($repin7Output | Where-Object { "$_" -match '^PASS: ([1-9][0-9]*) re-pin helper fixture checks$' })
if ($repin7Exit -ne 0 -or $repin7Summary.Count -ne 1 -or @($repin7Output | Where-Object { "$_" -match '^(FAIL:|RESULT=FAIL)' }).Count -gt 0 -or
    @($repin7Output | Where-Object { "$_" -ceq 'PASS: the ported canonical manifest hash reproduces the adapter test vector byte for byte' }).Count -ne 1) {
    throw "AgentBroker re-pin helper self-test under PowerShell 7 failed: tools/agentbroker/Test-AgentBrokerRepin.ps1 -SelfTest (exit $repin7Exit)."
}
$repin7Checks = [int]([regex]::Match([string]$repin7Summary[0], '^PASS: ([0-9]+)').Groups[1].Value)
$harnesses = @(
    @{ Name = 'Core'; Project = 'tests\AgentBroker.Core.Tests\AgentBroker.Core.Tests.csproj'
       Executable = "tests\AgentBroker.Core.Tests\bin\$Configuration\net10.0\AgentBroker.Core.Tests.exe"
       Summary = '^AgentBroker core smoke and regression: ([1-9][0-9]*) assertions passed\.$' },
    @{ Name = 'Relay'; Project = 'tests\AgentBroker.Relay.Tests\AgentBroker.Relay.Tests.csproj'
       Executable = "tests\AgentBroker.Relay.Tests\bin\$Configuration\AgentBroker.Relay.Tests.exe"
       Summary = '^RESULT=PASS assertions=([1-9][0-9]*)$' },
    @{ Name = 'Claude'; Project = 'tests\AgentBroker.Claude.Tests\AgentBroker.Claude.Tests.csproj'
       Executable = "tests\AgentBroker.Claude.Tests\bin\$Configuration\net10.0-windows\AgentBroker.Claude.Tests.exe"
       Summary = '^RESULT=PASS assertions=([1-9][0-9]*)$' },
    @{ Name = 'Desktop'; Project = 'tests\AgentBroker.Desktop.Tests\AgentBroker.Desktop.Tests.csproj'
       Executable = "tests\AgentBroker.Desktop.Tests\bin\$Configuration\net10.0-windows\AgentBroker.Desktop.Tests.exe"
       Summary = '^AgentBroker desktop synthetic checks passed: ([1-9][0-9]*)$' },
    @{ Name = 'Adapter'; Project = 'tests\AgentBroker.Adapter.Tests\AgentBroker.Adapter.Tests.csproj'
       Executable = "tests\AgentBroker.Adapter.Tests\bin\$Configuration\net10.0-windows\AgentBroker.Adapter.Tests.exe"
       Summary = '^AgentBroker native adapter regression: ([1-9][0-9]*) assertions passed\.$' },
    @{ Name = 'Network'; Project = 'tests\AgentBroker.Network.Tests\AgentBroker.Network.Tests.csproj'
       Executable = "tests\AgentBroker.Network.Tests\bin\$Configuration\net10.0-windows\AgentBroker.Network.Tests.exe"
       Summary = '^RESULT=PASS assertions=([1-9][0-9]*)$' },
    @{ Name = 'Worker'; Project = 'tests\AgentBroker.Worker.Tests\AgentBroker.Worker.Tests.csproj'
       Executable = "tests\AgentBroker.Worker.Tests\bin\$Configuration\net10.0-windows\AgentBroker.Worker.Tests.exe"
       Summary = '^RESULT=PASS assertions=([1-9][0-9]*)$' }
)
$failures = New-Object 'Collections.Generic.List[string]'
$assertions = 0
foreach ($harness in $harnesses) {
    Write-Host ("=== AgentBroker " + $harness.Name + ' deterministic harness')
    try {
        $project = Join-Path $repoRoot $harness.Project
        if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Project missing: $project" }
        & dotnet build $project -c $Configuration --nologo -v:minimal
        if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }
        $executable = Join-Path $repoRoot $harness.Executable
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Built executable missing: $executable" }
        $output = @(& $executable)
        $harnessExit = $LASTEXITCODE
        foreach ($line in $output) { Write-Host $line }
        if ($harnessExit -ne 0) { throw "Harness failed (exit $harnessExit)." }
        $summaries = @($output | Where-Object { "$_" -match $harness.Summary })
        if ($summaries.Count -ne 1 -or @($output | Where-Object { "$_" -match '^(FAIL:|RESULT=FAIL|SKIP:)' }).Count -gt 0) {
            throw 'Harness did not report exactly one nonempty passing assertion summary without failures/skips.'
        }
        $summaryMatch = [regex]::Match([string]$summaries[0], $harness.Summary)
        $assertions += [int]$summaryMatch.Groups[1].Value
    }
    catch { $failures.Add($harness.Name + ': ' + $_.Exception.Message) }
}
if ($failures.Count -gt 0) { throw ('AgentBroker checks failed: ' + ($failures -join '; ')) }
Write-Host ("AgentBroker deterministic harnesses PASS: $($harnesses.Count) harnesses, $assertions assertions; collaboration helper $collaborationChecks checks; re-pin helper $repinChecks checks (PowerShell 7: $repin7Checks).")
