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
      tombstone  lane broker-e (N-broker-a-02): six rounds of store.Send then the recipient's acknowledgement, with
                 acknowledged history in the snapshot, tombstones in the journal, or both, plus the journal compaction
                 and a full store.Messages history read. The old bound (4,096 retained) runs on both builds.

    -ControlExecutable names a second build of the same test executable, compiled from the store being replaced with
    this driver copied in: the purpose-built control. Configurations marked for it then run on both builds, and the two
    builds take turns inside the same rotation, so neither one gets the quieter half of the run.

    The state directories go under -WorkRoot. Put it on the volume the installed module's data uses (the user's
    LocalAppData volume) when the numbers are meant to describe the product: a commit flushes to disk, so the volume is
    part of the measurement. Every run directory and CNG key the benchmark creates is deleted by the benchmark.
    Writes results.jsonl (one line per sample) and summary.json to -OutputDirectory and prints the summary.
.EXAMPLE
    pwsh -NoProfile -File tests/Measure-AgentBrokerCoordination.ps1 -WorkRoot $env:LOCALAPPDATA\Temp\agentbroker-bench -OutputDirectory .\artifacts\coordination-benchmark
.EXAMPLE
    pwsh -NoProfile -File tests/Measure-AgentBrokerCoordination.ps1 -Scenario tombstone -ControlExecutable <control build>\AgentBroker.Worker.Tests.exe -WorkRoot $env:LOCALAPPDATA\Temp\agentbroker-bench -OutputDirectory .\artifacts\tombstone-benchmark
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$WorkRoot,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateRange(1, 20)][int]$Repetitions = 5,
    [ValidateSet('all', 'commit', 'wake', 'directory', 'tombstone')][string]$Scenario = 'all',
    [string]$ControlExecutable,
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
$executables = @{ candidate = $executable }
if ($ControlExecutable) {
    $ControlExecutable = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ControlExecutable)
    if (-not (Test-Path -LiteralPath $ControlExecutable -PathType Leaf)) { throw "Control executable missing: $ControlExecutable" }
    $executables['control'] = $ControlExecutable
}
# Resolved against this session's location: [IO.File] and the child process use the process directory instead.
$WorkRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkRoot)
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$results = Join-Path $OutputDirectory 'results.jsonl'
if (Test-Path -LiteralPath $results) { throw "Refusing to append to an existing results file: $results" }
# Files are written through [IO.File] with one explicit encoding, the same bytes under 5.1 and pwsh.
$utf8 = New-Object System.Text.UTF8Encoding($false)

# Each configuration is its argument list and whether the control build runs it too.
$configurations = New-Object 'Collections.Generic.List[object]'
function Add-Configuration([string[]]$Arguments, [bool]$Control) {
    $configurations.Add([pscustomobject]@{ Arguments = $Arguments; Control = $Control })
}
if ($Scenario -in @('all', 'commit')) {
    foreach ($retained in @(100, 1000, 4096)) {
        foreach ($bytes in @(512, 16384)) { Add-Configuration @('commit', "$retained", "$bytes", 'ascii') $false }
    }
    Add-Configuration @('commit', '4096', '16384', 'escaped') $false
}
if ($Scenario -in @('all', 'wake')) { Add-Configuration @('wake', '10') $false }
if ($Scenario -in @('all', 'directory')) {
    foreach ($shape in @(@('1', '16'), @('8', '16'), @('32', '16'), @('32', '256'))) {
        Add-Configuration @('directory', $shape[0], $shape[1], '12') $false
    }
}
if ($Scenario -in @('all', 'tombstone')) {
    # tombstone <acknowledged in snapshot> <live tombstones> <dead journal lines> <text bytes> <cap override, 0 = default>
    # Both builds: the old bound with no acknowledged history, with acknowledged 512-byte and 16 KiB history, and empty.
    Add-Configuration @('commit', '4096', '512', 'ascii') $true
    Add-Configuration @('tombstone', '0', '0', '0', '512', '0') $true
    Add-Configuration @('tombstone', '4090', '0', '0', '512', '0') $true
    Add-Configuration @('tombstone', '4090', '0', '0', '16384', '0') $true
    # This build only: sizing the cap (65,536 is the default; the larger two override it), and compaction at two caps.
    Add-Configuration @('tombstone', '0', '16384', '0', '512', '0') $false
    Add-Configuration @('tombstone', '0', '65512', '0', '512', '0') $false
    Add-Configuration @('tombstone', '0', '131060', '0', '512', '131072') $false
    Add-Configuration @('tombstone', '0', '262132', '0', '512', '262144') $false
    Add-Configuration @('tombstone', '0', '65512', '69700', '512', '0') $false
    Add-Configuration @('tombstone', '0', '262132', '266300', '512', '262144') $false
}
$runs = New-Object 'Collections.Generic.List[object]'
foreach ($configuration in $configurations) {
    $runs.Add([pscustomobject]@{ Arguments = $configuration.Arguments; Build = 'candidate' })
    if ($configuration.Control -and $executables.ContainsKey('control')) {
        $runs.Add([pscustomobject]@{ Arguments = $configuration.Arguments; Build = 'control' })
    }
}

function Invoke-Sample([object]$Sample, [int]$Repetition, [int]$Order) {
    $Configuration = $Sample.Arguments
    $runDirectory = Join-Path $WorkRoot ('run-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
    $arguments = @('--coordination-benchmark') + $Configuration[0]
    switch ($Configuration[0]) {
        'commit' { $arguments += @($Configuration[1], $Configuration[2], $Configuration[3], $runDirectory) }
        'wake' { $arguments += @($runDirectory, $Configuration[1]) }
        'directory' { $arguments += @($Configuration[1], $Configuration[2], $runDirectory, $Configuration[3]) }
        'tombstone' { $arguments += @($Configuration[1], $Configuration[2], $Configuration[3], $Configuration[4], $Configuration[5], $runDirectory) }
    }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    # Windows PowerShell 5.1 turns a native command's stderr line into a terminating error under Stop.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = @(& $executables[$Sample.Build] @arguments 2>&1); $exit = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous }
    $line = @($output | Where-Object { "$_" -like 'BENCHMARK *' } | Select-Object -Last 1)
    $record = [ordered]@{ repetition = $Repetition; order = $Order; build = $Sample.Build; configuration = ($Configuration -join ' '); exitCode = $exit
        processSeconds = [math]::Round($watch.Elapsed.TotalSeconds, 2); utc = [DateTime]::UtcNow.ToString('o') }
    if ($exit -eq 0 -and $line.Count -eq 1) { $record['result'] = ("$($line[0])".Substring(10) | ConvertFrom-Json) }
    else { $record['failure'] = (@($output | Select-Object -Last 6) -join ' | ') }
    [IO.File]::AppendAllText($results, ($record | ConvertTo-Json -Depth 6 -Compress) + "`n", $utf8)
    if (Test-Path -LiteralPath $runDirectory) { Write-Warning "Benchmark left its run directory behind: $runDirectory" }
    return $record
}

function Get-Median([double[]]$Values) {
    if ($Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $middle = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return [math]::Round($sorted[$middle], 2) }
    return [math]::Round(($sorted[$middle - 1] + $sorted[$middle]) / 2, 2)
}

function Get-Warm([object[]]$Samples, [string]$Property) {
    return @($Samples | ForEach-Object { $_.$Property | Select-Object -Skip 1 } | ForEach-Object { [double]$_ })
}

$all = New-Object 'Collections.Generic.List[object]'
for ($repetition = 0; $repetition -lt $Repetitions; $repetition++) {
    for ($step = 0; $step -lt $runs.Count; $step++) {
        $run = $runs[($step + $repetition) % $runs.Count]
        $record = Invoke-Sample $run $repetition $step
        $all.Add($record)
        $state = if ($record.Contains('result')) { 'ok' } else { 'FAILED' }
        Write-Host ("rep {0} {1,-9} {2,-36} {3} ({4} s)" -f $repetition, $record.build, $record.configuration, $state, $record.processSeconds)
    }
}

$summary = New-Object 'Collections.Generic.List[object]'
foreach ($run in $runs) {
    $name = $run.Arguments -join ' '
    $mine = @($all | Where-Object { $_.configuration -eq $name -and $_.build -eq $run.Build })
    $samples = @($mine | Where-Object { $_.Contains('result') } | ForEach-Object { $_.result })
    $failed = @($mine | Where-Object { -not $_.Contains('result') }).Count
    $row = [ordered]@{ configuration = $name; build = $run.Build; processes = $samples.Count; failedProcesses = $failed }
    if ($samples.Count -gt 0) {
        switch ($run.Arguments[0]) {
            'commit' {
                $row['stateBytes'] = ($samples | ForEach-Object { $_.stateBytes } | Measure-Object -Maximum).Maximum
                $row['coldSendMedianMs'] = Get-Median @($samples | ForEach-Object { [double]$_.sendMs[0] })
                $row['warmSendMedianMs'] = Get-Median (Get-Warm $samples 'sendMs')
                $row['warmSendMaxMs'] = [math]::Round(((Get-Warm $samples 'sendMs') | Measure-Object -Maximum).Maximum, 2)
                $row['loadMedianMs'] = Get-Median @($samples | ForEach-Object { [double]$_.loadMs })
            }
            'wake' {
                $row['coldWakeMedianMs'] = Get-Median @($samples | ForEach-Object { [double]$_.wakeMs[0] })
                $row['warmWakeMedianMs'] = Get-Median (Get-Warm $samples 'wakeMs')
                $row['warmWakeMaxMs'] = [math]::Round(((Get-Warm $samples 'wakeMs') | Measure-Object -Maximum).Maximum, 2)
                $row['warmSendMedianMs'] = Get-Median (Get-Warm $samples 'sendMs')
            }
            'directory' {
                $row['expectedSessions'] = $samples[0].expected
                $row['federatedSessionsMin'] = ($samples | ForEach-Object { $_.federated } | Measure-Object -Minimum).Minimum
                $row['runtimeDirectoryColdMedianMs'] = Get-Median @($samples | ForEach-Object { [double]$_.runtimeMs[0] })
                $row['runtimeDirectoryWarmMedianMs'] = Get-Median (Get-Warm $samples 'runtimeMs')
                $row['coldPageMedianMs'] = Get-Median @($samples | ForEach-Object { [double]$_.pageMs[0] })
                $row['warmPageMedianMs'] = Get-Median (Get-Warm $samples 'pageMs')
                $row['warmPageMaxMs'] = [math]::Round(((Get-Warm $samples 'pageMs') | Measure-Object -Maximum).Maximum, 2)
            }
            'tombstone' {
                $row['tombstoneStore'] = $samples[0].tombstoneStore
                $row['listed'] = $samples[0].listed
                $row['stateBytes'] = ($samples | ForEach-Object { $_.stateBytes } | Measure-Object -Maximum).Maximum
                $row['journalBytes'] = ($samples | ForEach-Object { $_.journalBytes } | Measure-Object -Maximum).Maximum
                $row['storeHeapMedianBytes'] = Get-Median @($samples | ForEach-Object { [double]$_.storeHeapBytes })
                $row['loadMedianMs'] = Get-Median @($samples | ForEach-Object { [double]$_.loadMs })
                $row['coldSendMedianMs'] = Get-Median @($samples | ForEach-Object { [double]$_.sendMs[0] })
                $row['warmSendMedianMs'] = Get-Median (Get-Warm $samples 'sendMs')
                $row['warmSendMaxMs'] = [math]::Round(((Get-Warm $samples 'sendMs') | Measure-Object -Maximum).Maximum, 2)
                $row['firstAckMedianMs'] = Get-Median @($samples | ForEach-Object { [double]$_.ackMs[0] })
                $row['firstAckMaxMs'] = [math]::Round((@($samples | ForEach-Object { [double]$_.ackMs[0] }) | Measure-Object -Maximum).Maximum, 2)
                $row['warmAckMedianMs'] = Get-Median (Get-Warm $samples 'ackMs')
                $row['warmAckMaxMs'] = [math]::Round(((Get-Warm $samples 'ackMs') | Measure-Object -Maximum).Maximum, 2)
                $row['messagesMedianMs'] = Get-Median @($samples | ForEach-Object { [double]$_.messagesMs })
            }
        }
    }
    $summary.Add($row)
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'summary.json'), ($summary | ConvertTo-Json -Depth 4) + "`n", $utf8)
$summary | ForEach-Object { [pscustomobject]$_ } | Format-List | Out-String -Width 160 | Write-Host
if (@($all | Where-Object { -not $_.Contains('result') }).Count -gt 0) { exit 1 }
