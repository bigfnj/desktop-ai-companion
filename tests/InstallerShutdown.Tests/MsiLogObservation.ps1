#requires -Version 5
# Diagnostic only. A filesystem event establishes timing, not the identity of its actor.
function New-ScopedLogWatch {
    param([string]$Directory, [string]$FileName, [string]$Repo)
    if ($FileName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,120}\.(log|txt)$') { throw 'Observation requires one ordinary exact filename.' }
    [void](Assert-ScopedShutdownPath -Path (Join-Path $Directory $FileName) -TrustedRoot $Repo)
    if (-not [IO.Directory]::Exists($Directory)) { throw 'Observation directory must already exist.' }
    $watcher = New-Object IO.FileSystemWatcher
    $watcher.Path = $Directory
    $watcher.Filter = $FileName
    $watcher.IncludeSubdirectories = $false
    $watcher.NotifyFilter = [IO.NotifyFilters]::FileName
    $watcher.InternalBufferSize = 4096
    $watch = [pscustomobject]@{
        Watcher=$watcher; FileName=$FileName; Identifiers=(New-Object 'Collections.Generic.List[string]')
        Events=(New-Object 'Collections.Generic.List[object]'); Incomplete=$false; Closed=$false
    }
    try {
        $prefix = 'dac.log-observation.' + [Guid]::NewGuid().ToString('N')
        foreach ($kind in @('Created','Deleted','Renamed','Error')) {
            $id = $prefix + '.' + $kind
            [void](Register-ObjectEvent -InputObject $watcher -EventName $kind -SourceIdentifier $id -MaxTriggerCount 1025)
            $watch.Identifiers.Add($id)
        }
        $watcher.EnableRaisingEvents = $true
        return $watch
    } catch { Close-ScopedLogWatch $watch; throw }
}
function Receive-ScopedLogEvents {
    param($Watch)
    foreach ($id in $Watch.Identifiers) {
        foreach ($event in @(Get-Event -SourceIdentifier $id -ErrorAction SilentlyContinue)) {
            try {
                $kind = $id.Substring($id.LastIndexOf('.') + 1)
                if ($kind -eq 'Error' -or $Watch.Events.Count -ge 1024) { $Watch.Incomplete=$true }
                if ($Watch.Events.Count -lt 1024) {
                    $name = $null; $oldName = $null
                    if ($kind -ne 'Error') {
                        $name = if ([string]$event.SourceEventArgs.Name -ieq $Watch.FileName) { $Watch.FileName } else { '<other-name>' }
                        if ($kind -eq 'Renamed') {
                            $oldName = if ([string]$event.SourceEventArgs.OldName -ieq $Watch.FileName) { $Watch.FileName } else { '<other-name>' }
                        }
                    }
                    $Watch.Events.Add([pscustomobject]@{ Sequence=$event.EventIdentifier; Kind=$kind; AtUtc=$event.TimeGenerated.ToUniversalTime().ToString('o'); Name=$name; OldName=$oldName })
                }
            } finally { Remove-Event -EventIdentifier $event.EventIdentifier -ErrorAction SilentlyContinue }
        }
    }
}
function Close-ScopedLogWatch {
    param($Watch)
    if ($null -eq $Watch -or $Watch.Closed) { return }
    try { $Watch.Watcher.EnableRaisingEvents=$false; Receive-ScopedLogEvents $Watch }
    finally {
        foreach ($id in $Watch.Identifiers) {
            Unregister-Event -SourceIdentifier $id -ErrorAction SilentlyContinue
            foreach ($event in @(Get-Event -SourceIdentifier $id -ErrorAction SilentlyContinue)) {
                Remove-Event -EventIdentifier $event.EventIdentifier -ErrorAction SilentlyContinue
            }
        }
        $Watch.Watcher.Dispose()
        $Watch.Closed=$true
    }
}
function Get-ScopedLogReceipt {
    param([string]$Path, [string]$Repo)
    [void](Assert-ScopedShutdownPath -Path $Path -TrustedRoot $Repo)
    if (-not [IO.File]::Exists($Path)) { return [pscustomobject]@{ Exists=$false; Length=0; Sha256=$null } }
    return [pscustomobject]@{ Exists=$true; Length=(Get-Item -LiteralPath $Path).Length; Sha256=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
}
function Test-ScopedLogReceiptEqual {
    param($Before, $After)
    return $Before.Exists -and $After.Exists -and $Before.Length -gt 0 -and $Before.Length -eq $After.Length -and $Before.Sha256 -ceq $After.Sha256
}
function Wait-ScopedLogEvent {
    param($Watch, [string]$Kind, [int]$Count=1)
    $deadline=[DateTime]::UtcNow.AddSeconds(5)
    do {
        Receive-ScopedLogEvents $Watch
        if ($Watch.Incomplete) { throw 'Observation control watcher reported incomplete events.' }
        if (@($Watch.Events | Where-Object Kind -eq $Kind).Count -ge $Count) { return }
        Start-Sleep -Milliseconds 20
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ('Observation control did not receive its bounded ' + $Kind + ' event.')
}
function Test-ScopedLogWatchControl {
    param([string]$Directory, [string]$Repo)
    $stem='msi-observation-control-' + [Guid]::NewGuid().ToString('N')
    $path=Join-Path $Directory ($stem + '.txt')
    $renamed=Join-Path $Directory ($stem + '-renamed.txt')
    foreach ($candidate in @($path,$renamed)) { [void](Assert-ScopedShutdownPath -Path $candidate -TrustedRoot $Repo) }
    $watch=$null; $expected=$null
    try {
        $watch=New-ScopedLogWatch -Directory $Directory -FileName ($stem+'.txt') -Repo $Repo
        $stream=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
        try {
            $bytes=[Text.Encoding]::UTF8.GetBytes('owned harmless log watcher control')
            $stream.Write($bytes,0,$bytes.Length)
        } finally { $stream.Dispose() }
        $expected=Get-ScopedLogReceipt -Path $path -Repo $Repo
        Wait-ScopedLogEvent $watch 'Created'
        [IO.File]::Move($path,$renamed)
        Wait-ScopedLogEvent $watch 'Renamed'
        [IO.File]::Move($renamed,$path)
        Wait-ScopedLogEvent $watch 'Renamed' 2
        if (-not (Test-ScopedLogReceiptEqual $expected (Get-ScopedLogReceipt -Path $path -Repo $Repo))) { throw 'Observation control contents changed.' }
        [IO.File]::Delete($path)
        Wait-ScopedLogEvent $watch 'Deleted'
        return [pscustomobject]@{ Result='PASS'; Events=@($watch.Events.ToArray() | Sort-Object Sequence); Incomplete=$watch.Incomplete }
    } finally {
        Close-ScopedLogWatch $watch
        foreach ($candidate in @($path,$renamed)) {
            $receipt=Get-ScopedLogReceipt -Path $candidate -Repo $Repo
            if ($receipt.Exists) {
                if ($null -eq $expected -or -not (Test-ScopedLogReceiptEqual $expected $receipt)) { throw 'Observation control cleanup refused changed contents.' }
                [IO.File]::Delete($candidate)
            }
        }
    }
}
function Invoke-ScopedMsiLogObservation {
    param([string]$Directory, [string]$Repo, [string]$Caller, [string]$Arguments, $State, [scriptblock]$StartCaller)
    $first=Join-Path $Directory 'install-v1.log'
    $target=Join-Path $Directory 'repair.log'
    $firstBefore=Get-ScopedLogReceipt -Path $first -Repo $Repo
    if (-not $firstBefore.Exists -or $firstBefore.Length -le 0) { throw 'Observation requires the actual first-install log before repair.' }
    if ((Get-ScopedLogReceipt -Path $target -Repo $Repo).Exists) { throw 'Observation refuses to overwrite a prior repair log.' }
    $output=Join-Path (Split-Path $Directory -Parent) ('maintenance-observation-' + [Guid]::NewGuid().ToString('N') + '.json')
    [void](Assert-ScopedShutdownPath -Path $output -TrustedRoot $Repo)
    $watch=$null; $primary=$null; $code=$null; $control=$null; $firstAfterControl=$null
    $startedAt=[DateTime]::UtcNow.ToString('o')
    try {
        $watch=New-ScopedLogWatch -Directory $Directory -FileName 'repair.log' -Repo $Repo
        $control=Test-ScopedLogWatchControl -Directory $Directory -Repo $Repo
        $firstAfterControl=Get-ScopedLogReceipt -Path $first -Repo $Repo
        if (-not (Test-ScopedLogReceiptEqual $firstBefore $firstAfterControl)) { throw 'First-install log changed before the repair precondition.' }
        $code=Invoke-ScopedMsiCaller -State $State -StartCaller $StartCaller
    } catch { $primary=$_ }
    finally {
        if ($null -ne $watch) {
            try {
                try {
                    $deadline=[DateTime]::UtcNow.AddMilliseconds(500)
                    do { Receive-ScopedLogEvents $watch; Start-Sleep -Milliseconds 25 } while ([DateTime]::UtcNow -lt $deadline)
                } finally { Close-ScopedLogWatch $watch }
                $firstAfter=Get-ScopedLogReceipt -Path $first -Repo $Repo
                $report=[ordered]@{
                SchemaVersion=1; Target=$target; Filter='repair.log'; IncludeSubdirectories=$false
                StartedAtUtc=$startedAt; CompletedAtUtc=[DateTime]::UtcNow.ToString('o')
                Control=$control; Events=@($watch.Events.ToArray() | Sort-Object Sequence); Incomplete=$watch.Incomplete
                FirstInstallBefore=$firstBefore; FirstInstallAfterControl=$firstAfterControl; FirstInstallAfter=$firstAfter
                FirstInstallPreserved=(Test-ScopedLogReceiptEqual $firstBefore $firstAfter)
                TargetAfter=(Get-ScopedLogReceipt -Path $target -Repo $Repo)
                Caller=$Caller; Arguments=$Arguments; CallerPid=$State.CallerPid; CallerCreationFileTime=$State.CallerCreationFileTime
                ExitCode=$code; CallerStateUncertain=$State.Uncertain; CallerUncertaintyReason=$State.Reason
                EventsIdentifyActor=$false; MsiReceiptRequirementsUnchanged=$true
                }
                $json=$report | ConvertTo-Json -Depth 7
                $stream=[IO.File]::Open($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
                try { $bytes=(New-Object Text.UTF8Encoding($false)).GetBytes($json); $stream.Write($bytes,0,$bytes.Length) }
                finally { $stream.Dispose() }
                Write-Host ('MAINTENANCE_LOG_OBSERVATION=' + $output)
                if ($watch.Incomplete -or -not $report.FirstInstallPreserved) { throw 'Maintenance observation is incomplete; preserve its report.' }
            } catch {
                if ($null -eq $primary) { $primary=$_ }
                else { Write-Warning 'Maintenance observation also failed; preserving the primary caller error.' }
            }
        }
    }
    if ($null -ne $primary) { throw $primary }
    return $code
}
