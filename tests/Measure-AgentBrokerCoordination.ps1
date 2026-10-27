#requires -Version 5.1
<#
.SYNOPSIS
    O-agentbroker-02: measure sustained-coordination costs of the current AgentBroker store, cold, one fresh process per
    sample, interleaved across configurations.
.DESCRIPTION
    Each sample is one fresh AgentBroker.Worker.Tests.exe process running --coordination-benchmark (see
    tests/AgentBroker.Worker.Tests/CoordinationBenchmark.cs). Repetition r runs every configuration once, in an order
    rotated by r, so slow drift on a shared machine spreads across configurations instead of landing on one. Timings are
    taken inside the process with Stopwatch around the measured operation only; process start, JIT of the driver and the
    seeding of state are excluded. The first operation in each process is reported separately as cold.

      commit     store.Send (Accept + Commit, the snapshot rewrite) at a retained-message count, ASCII or worst-case
                 escaped text. Six sends per process: the first is cold, the next five warm.
      wake       wait_for_messages wake latency over the real HTTP MCP endpoint, ten samples per process.
      directory  a federated list_sessions page with N paired loopback brokers of S present sessions each.

    The state directories go under -WorkRoot. Put it on the volume the installed module's data uses (the user's
    LocalAppData volume) when the numbers are meant to describe the product: a commit flushes to disk, so the volume is
    part of the measurement. Every run directory and CNG key the benchmark creates is deleted by the benchmark.
    Writes results.jsonl (one line per sample) and summary.json to -OutputDirectory and prints the summary.
.EXAMPLE
    pwsh -NoProfile -File tests/Measure-AgentBrokerCoordination.ps1 -WorkRoot $env:LOCALAPPDATA\Temp\agentbroker-bench -OutputDirectory .\artifacts\coordination-benchmark
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$WorkRoot,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateRange(1, 20)][int]$Repetitions = 5,
    [ValidateSet('all', 'commit', 'wake', 'directory')][string]$Scenario = 'all',
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$project = Join-Path $repoRoot 'tests\AgentBroker.Worker.Tests\AgentBroker.Worker.Tests.csproj'
$executable = Join-Path $repoRoot 'tests\AgentBroker.Worker.Tests\bin\Release\net10.0-windows\AgentBroker.Worker.Tests.exe'
if (-not $NoBuild) {
    & dotnet build $project -c Release --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Benchmark build failed (exit $LASTEXITCODE)." }
}
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Benchmark executable missing: $executable" }
# Resolved against this session's location: [IO.File] and the child process use the process directory instead.
$WorkRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkRoot)
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$results = Join-Path $OutputDirectory 'results.jsonl'
if (Test-Path -LiteralPath $results) { throw "Refusing to append to an existing results file: $results" }
# Files are written through [IO.File] with one explicit encoding, the same bytes under 5.1 and pwsh.
$utf8 = New-Object System.Text.UTF8Encoding($false)

$configurations = New-Object 'Collections.Generic.List[object]'
if ($Scenario -in @('all', 'commit')) {
    foreach ($retained in @(100, 1000, 4096)) {
        foreach ($bytes in @(512, 16384)) { $configurations.Add(@('commit', "$retained", "$bytes", 'ascii')) }
    }
    $configurations.Add(@('commit', '4096', '16384', 'escaped'))
}
if ($Scenario -in @('all', 'wake')) { $configurations.Add(@('wake', '10')) }
if ($Scenario -in @('all', 'directory')) {
    foreach ($shape in @(@('1', '16'), @('8', '16'), @('32', '16'), @('32', '256'))) {
        $configurations.Add(@('directory', $shape[0], $shape[1], '12'))
    }
}

function Invoke-Sample([object[]]$Configuration, [int]$Repetition, [int]$Order) {
    $run = Join-Path $WorkRoot ('run-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
    $arguments = @('--coordination-benchmark') + $Configuration[0]
    switch ($Configuration[0]) {
        'commit' { $arguments += @($Configuration[1], $Configuration[2], $Configuration[3], $run) }
        'wake' { $arguments += @($run, $Configuration[1]) }
        'directory' { $arguments += @($Configuration[1], $Configuration[2], $run, $Configuration[3]) }
    }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    # Windows PowerShell 5.1 turns a native command's stderr line into a terminating error under Stop.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = @(& $executable @arguments 2>&1); $exit = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous }
    $line = @($output | Where-Object { "$_" -like 'BENCHMARK *' } | Select-Object -Last 1)
    $record = [ordered]@{ repetition = $Repetition; order = $Order; configuration = ($Configuration -join ' '); exitCode = $exit
        processSeconds = [math]::Round($watch.Elapsed.TotalSeconds, 2); utc = [DateTime]::UtcNow.ToString('o') }
    if ($exit -eq 0 -and $line.Count -eq 1) { $record['result'] = ("$($line[0])".Substring(10) | ConvertFrom-Json) }
    else { $record['failure'] = (@($output | Select-Object -Last 6) -join ' | ') }
    [IO.File]::AppendAllText($results, ($record | ConvertTo-Json -Depth 6 -Compress) + "`n", $utf8)
    if (Test-Path -LiteralPath $run) { Write-Warning "Benchmark left its run directory behind: $run" }
    return $record
}

function Get-Median([double[]]$Values) {
    if ($Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $middle = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return [math]::Round($sorted[$middle], 2) }
    return [math]::Round(($sorted[$middle - 1] + $sorted[$middle]) / 2, 2)
}

$all = New-Object 'Collections.Generic.List[object]'
for ($repetition = 0; $repetition -lt $Repetitions; $repetition++) {
    for ($step = 0; $step -lt $configurations.Count; $step++) {
        $configuration = $configurations[($step + $repetition) % $configurations.Count]
        $record = Invoke-Sample $configuration $repetition $step
        $all.Add($record)
        $state = if ($record.Contains('result')) { 'ok' } else { 'FAILED' }
        Write-Host ("rep {0} {1,-28} {2} ({3} s)" -f $repetition, $record.configuration, $state, $record.processSeconds)
    }
}

$summary = New-Object 'Collections.Generic.List[object]'
foreach ($configuration in $configurations) {
    $name = $configuration -join ' '
    $samples = @($all | Where-Object { $_.configuration -eq $name -and $_.Contains('result') } | ForEach-Object { $_.result })
    $failed = @($all | Where-Object { $_.configuration -eq $name -and -not $_.Contains('result') }).Count
    $row = [ordered]@{ configuration = $name; processes = $samples.Count; failedProcesses = $failed }
    if ($samples.Count -gt 0) {
        switch ($configuration[0]) {
            'commit' {
                $row['stateBytes'] = ($samples | ForEach-Object { $_.stateBytes } | Measure-Object -Maximum).Maximum
                $row['coldSendMedianMs'] = Get-Median @($samples | ForEach-Object { [double]$_.sendMs[0] })
                $row['warmSendMedianMs'] = Get-Median @($samples | ForEach-Object { $_.sendMs | Select-Object -Skip 1 } | ForEach-Object { [double]$_ })
                $row['warmSendMaxMs'] = [math]::Round((@($samples | ForEach-Object { $_.sendMs | Select-Object -Skip 1 } | ForEach-Object { [double]$_ }) | Measure-Object -Maximum).Maximum, 2)
                $row['loadMedianMs'] = Get-Median @($samples | ForEach-Object { [double]$_.loadMs })
            }
            'wake' {
                $row['coldWakeMedianMs'] = Get-Median @($samples | ForEach-Object { [double]$_.wakeMs[0] })
                $row['warmWakeMedianMs'] = Get-Median @($samples | ForEach-Object { $_.wakeMs | Select-Object -Skip 1 } | ForEach-Object { [double]$_ })
                $row['warmWakeMaxMs'] = [math]::Round((@($samples | ForEach-Object { $_.wakeMs | Select-Object -Skip 1 } | ForEach-Object { [double]$_ }) | Measure-Object -Maximum).Maximum, 2)
                $row['warmSendMedianMs'] = Get-Median @($samples | ForEach-Object { $_.sendMs | Select-Object -Skip 1 } | ForEach-Object { [double]$_ })
            }
            'directory' {
                $row['expectedSessions'] = $samples[0].expected
                $row['federatedSessionsMin'] = ($samples | ForEach-Object { $_.federated } | Measure-Object -Minimum).Minimum
                $row['runtimeDirectoryColdMedianMs'] = Get-Median @($samples | ForEach-Object { [double]$_.runtimeMs[0] })
                $row['runtimeDirectoryWarmMedianMs'] = Get-Median @($samples | ForEach-Object { $_.runtimeMs | Select-Object -Skip 1 } | ForEach-Object { [double]$_ })
                $row['coldPageMedianMs'] = Get-Median @($samples | ForEach-Object { [double]$_.pageMs[0] })
                $row['warmPageMedianMs'] = Get-Median @($samples | ForEach-Object { $_.pageMs | Select-Object -Skip 1 } | ForEach-Object { [double]$_ })
                $row['warmPageMaxMs'] = [math]::Round((@($samples | ForEach-Object { $_.pageMs | Select-Object -Skip 1 } | ForEach-Object { [double]$_ }) | Measure-Object -Maximum).Maximum, 2)
            }
        }
    }
    $summary.Add($row)
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'summary.json'), ($summary | ConvertTo-Json -Depth 4) + "`n", $utf8)
$summary | ForEach-Object { [pscustomobject]$_ } | Format-List | Out-String -Width 160 | Write-Host
if (@($all | Where-Object { -not $_.Contains('result') }).Count -gt 0) { exit 1 }
