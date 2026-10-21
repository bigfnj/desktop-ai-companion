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
Write-Host ("AgentBroker deterministic harnesses PASS: $($harnesses.Count) harnesses, $assertions assertions.")
