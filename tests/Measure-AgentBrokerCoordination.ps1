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
      waiting-mail  ReadWaitingMail at 0/16/4096 pending across 65 sessions, separately with empty or freshly busy waits.
                 Six calls per process, 500 ms gaps; call zero separate, then median of each process's five paced calls.
                 Five fresh interleaved processes per build/shape supply the outer median and full-summary comparison.
      tombstone  lane broker-e (N-broker-a-02): six rounds of store.Send then the recipient's acknowledgement, with
                 acknowledged history in the snapshot, tombstones in the journal, or both, plus the journal compaction
                 and a full store.Messages history read. The old bound (4,096 retained) runs on both builds.
      busy-idle  lane T (A9): a real runtime with one Busy native row and N waiting messages, idle for 20 s: the
                 process CPU share and the recipient_busy directory observations per second. Only when named.

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
    [ValidateSet('all', 'commit', 'wake', 'directory', 'tombstone', 'busy-idle', 'waiting-mail')][string]$Scenario = 'all',
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
# busy-idle <pending> <seconds>: AgentBroker 1.10.5 marathon, lane T (A9). A report, not a gate: the CPU share and the
# recipient_busy directory observation rate while N messages wait on one Busy native row for 20 s. Both builds (the control
# is the build from before A8's observation interval). Not part of 'all': it holds each process for its full 20 s.
# The driver takes 3..120 s and refuses anything else (lane T round 2, T2-17): the seeded messages live 60 MINUTES
# (store.Send's lifetime argument is in minutes), so no window it accepts can outlast them, and with messages waiting a sample
# with fewer observations than messages fails as BENCHMARK-FAILED instead of reading as 0/s (T2-12).
if ($Scenario -eq 'busy-idle') {
    foreach ($pending in @(0, 16, 64)) { Add-Configuration @('busy-idle', "$pending", '20') $true }
}
# A7 cohorts remain separate; both builds receive identical requested shapes.
if ($Scenario -in @('all', 'waiting-mail')) {
    foreach ($cohort in @('pending-only', 'fresh-busy')) {
        foreach ($pending in @(0, 16, 4096)) { Add-Configuration @('waiting-mail', "$pending", $cohort) $true }
    }
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
        'busy-idle' { $arguments += @($Configuration[1], $Configuration[2], $runDirectory) }
        'waiting-mail' { $arguments += @($Configuration[1], $Configuration[2], $runDirectory) }
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

# A7 keeps full precision and takes each process's paced median before the outer median.
function Get-WaitingMailMedian([double[]]$Values) {
    if ($Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $middle = [int][math]::Floor($sorted.Count / 2)
    if ($Values.Count % 2 -eq 1) { return [double]$sorted[$middle] }
    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0
}

function Get-WaitingMailSummary([object[]]$Records, [string]$Build, [int]$Pending, [string]$Cohort,
    [int]$ExpectedProcesses, [bool]$HasControl) {
    $issues = New-Object 'Collections.Generic.List[string]'
    $mine = New-Object 'Collections.Generic.List[object]'
    $expectedBuilds = @('candidate')
    if ($HasControl) { $expectedBuilds += 'control' }
    foreach ($side in $expectedBuilds) {
        $sideRecords = @($Records | Where-Object { $_.build -eq $side })
        $repetitions = @($sideRecords | ForEach-Object { [int]$_.repetition } | Sort-Object)
        if ($sideRecords.Count -ne $ExpectedProcesses -or
            (($repetitions -join ',') -cne ((0..($ExpectedProcesses - 1)) -join ','))) {
            $issues.Add("WAITING_PROCESS_INVENTORY: $side")
        }
    }
    $referenceBase64 = $null
    $referenceSha256 = $null
    foreach ($record in $Records) {
        if (-not $record.Contains('result')) {
            $issues.Add("WAITING_PROCESS_FAILED: $($record.build)/$($record.repetition)")
            continue
        }
        $sample = $record.result
        $missing = @(@('scenario', 'pending', 'cohort', 'sessions', 'samples', 'gapMilliseconds', 'firstQuerySample',
            'queryMs', 'summaryUtf8Base64', 'summarySha256ByCall', 'expectedSummarySha256') |
            Where-Object { $null -eq $sample.PSObject.Properties[$_] })
        if ($missing.Count -gt 0) {
            $issues.Add("WAITING_RESULT_SHAPE: $($record.build)/$($record.repetition)")
            continue
        }
        if ($sample.scenario -cne 'waiting-mail' -or $sample.pending -ne $Pending -or $sample.cohort -cne $Cohort -or
            $sample.sessions -ne 65 -or $sample.samples -ne 6 -or $sample.gapMilliseconds -ne 500 -or
            $sample.firstQuerySample -ne 0 -or @($sample.queryMs).Count -ne 6 -or @($sample.summarySha256ByCall).Count -ne 6) {
            $issues.Add("WAITING_RESULT_SHAPE: $($record.build)/$($record.repetition)")
            continue
        }
        $times = @()
        $timesValid = $true
        foreach ($rawTime in @($sample.queryMs)) {
            if ($null -eq $rawTime) { $timesValid = $false; break }
            try { $value = [double]$rawTime }
            catch { $timesValid = $false; break }
            if ([double]::IsNaN($value) -or [double]::IsInfinity($value) -or $value -lt 0) {
                $timesValid = $false
                break
            }
            $times += $value
        }
        if (-not $timesValid) {
            $issues.Add("WAITING_TIMES_INVALID: $($record.build)/$($record.repetition)")
            continue
        }
        try {
            $bytes = [Convert]::FromBase64String([string]$sample.summaryUtf8Base64)
            if ($bytes.Length -eq 0) { throw 'Empty summary bytes.' }
            $canonicalBase64 = [Convert]::ToBase64String($bytes)
            $digest = [Security.Cryptography.SHA256]::Create()
            try { $actualSha256 = ([BitConverter]::ToString($digest.ComputeHash($bytes))).Replace('-', '') }
            finally { $digest.Dispose() }
        } catch {
            $issues.Add("WAITING_SUMMARY_BYTES_INVALID: $($record.build)/$($record.repetition)")
            continue
        }
        $hashesAgree = [string]::Equals([string]$sample.expectedSummarySha256, $actualSha256, [StringComparison]::OrdinalIgnoreCase)
        foreach ($hash in @($sample.summarySha256ByCall)) {
            $hashesAgree = $hashesAgree -and [string]::Equals([string]$hash, $actualSha256, [StringComparison]::OrdinalIgnoreCase)
        }
        if (-not $hashesAgree) { $issues.Add("WAITING_SUMMARY_HASH_MISMATCH: $($record.build)/$($record.repetition)") }
        if ($null -eq $referenceBase64) {
            $referenceBase64 = $canonicalBase64
            $referenceSha256 = $actualSha256
        } elseif (-not [string]::Equals($canonicalBase64, $referenceBase64, [StringComparison]::Ordinal)) {
            $issues.Add("WAITING_SUMMARY_BYTES_DIFFER: $($record.build)/$($record.repetition)")
        }
        if ($record.build -eq $Build) {
            $mine.Add([pscustomobject]@{ cold = [double]$times[0]
                paced = (Get-WaitingMailMedian ([double[]]$times[1..5])) })
        }
    }
    $cold = Get-WaitingMailMedian @($mine | ForEach-Object { [double]$_.cold })
    $processMedians = @($mine | ForEach-Object { [double]$_.paced })
    $paced = Get-WaitingMailMedian ([double[]]$processMedians)
    $valid = $issues.Count -eq 0 -and $null -ne $referenceBase64 -and $mine.Count -eq $ExpectedProcesses
    $eligible = $valid -and $HasControl -and $ExpectedProcesses -eq 5
    $withinTarget = $null
    if ($eligible -and $Pending -eq 4096) { $withinTarget = [double]$paced -le 5.0 }
    $comparison = 'candidate-only; control unavailable'
    $crossBuildEqual = $null
    if ($HasControl) { $comparison = 'candidate/control and all repetitions'; $crossBuildEqual = $valid }
    return [ordered]@{ cohort = $Cohort; pending = $Pending; coldQueryMedianMs = $cold
        processPacedMediansMs = $processMedians; pacedQueryMedianMs = $paced
        completeSummaryBytesEqual = $valid; summaryComparisonScope = $comparison
        candidateControlSummaryBytesEqual = $crossBuildEqual; summarySha256 = $referenceSha256
        acceptanceEligible = $eligible; candidate4096Within5Ms = $(if ($Build -eq 'candidate') { $withinTarget } else { $null })
        validationFailures = @($issues.ToArray()) }
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

$waitingMailValidationFailed = $false
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
            'waiting-mail' {
                $shapeRecords = @($all | Where-Object { $_.configuration -eq $name })
                $waiting = Get-WaitingMailSummary -Records $shapeRecords -Build $run.Build -Pending ([int]$run.Arguments[1]) -Cohort $run.Arguments[2] -ExpectedProcesses $Repetitions -HasControl ($executables.ContainsKey('control'))
                foreach ($key in $waiting.Keys) { $row[$key] = $waiting[$key] }
                if (@($waiting.validationFailures).Count -gt 0) { $waitingMailValidationFailed = $true }
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
            'busy-idle' {
                $row['pending'] = $samples[0].pending
                $row['cpuShareMedian'] = Get-Median @($samples | ForEach-Object { [double]$_.cpuShare * 100 })
                $row['cpuMsMedian'] = Get-Median @($samples | ForEach-Object { [double]$_.cpuMs })
                $row['directoryObservationsPerSecondMedian'] = Get-Median @($samples | ForEach-Object { [double]$_.observationsPerSecond })
                $row['adapterSubmitsMax'] = ($samples | ForEach-Object { $_.adapterSubmits } | Measure-Object -Maximum).Maximum
            }
        }
    }
    $summary.Add($row)
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'summary.json'), ($summary | ConvertTo-Json -Depth 4) + "`n", $utf8)
$summary | ForEach-Object { [pscustomobject]$_ } | Format-List | Out-String -Width 160 | Write-Host
if ($waitingMailValidationFailed) { exit 1 }
if (@($all | Where-Object { -not $_.Contains('result') }).Count -gt 0) { exit 1 }
