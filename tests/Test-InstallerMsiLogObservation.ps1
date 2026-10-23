#requires -Version 5
<#
.SYNOPSIS
Prove exact-name log observation using harmless controls and fake MSI callers.
.EXAMPLE
pwsh -NoProfile -File tests/Test-InstallerMsiLogObservation.ps1
#>
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repo 'installer\ScopedShutdown\PathSafety.ps1')
. (Join-Path $PSScriptRoot 'InstallerShutdown.Tests\MsiCallerSafety.ps1')
. (Join-Path $PSScriptRoot 'InstallerShutdown.Tests\MsiLogObservation.ps1')
$root=Join-Path $repo ('build\msi-log-observation-tests\' + [Guid]::NewGuid().ToString('N'))
[void](Assert-ScopedShutdownPath -Path $root -TrustedRoot $repo)
[void][IO.Directory]::CreateDirectory($root)
$checks=0
function Check([bool]$Condition,[string]$Message) {
    if (-not $Condition) { throw ('Test-InstallerMsiLogObservation.ps1: ' + $Message) }
    $script:checks++
}
function New-FakeMsiCaller([switch]$WaitFailure) {
    $child=[pscustomobject]@{ Id=4321; SafeHandle='fake-retained'; StartTime=([DateTime]::SpecifyKind([DateTime]'2026-01-02T03:04:05',[DateTimeKind]::Utc)); ExitCode=0; WaitFailure=[bool]$WaitFailure; Disposed=0 }
    $child | Add-Member ScriptMethod WaitForExit { param([int]$Milliseconds); [Threading.Thread]::Sleep(100); if ($this.WaitFailure) { throw 'injected_wait' }; return $Milliseconds -eq 180000 }
    $child | Add-Member ScriptMethod Dispose { $this.Disposed++ }
    return $child
}
foreach ($case in @('missing-first','empty-first','prior-target','no-target','create-delete-rename','wait-failure')) {
    $family=Join-Path $root $case
    $directory=Join-Path $family 'main'
    [void][IO.Directory]::CreateDirectory($directory)
    $script:directory=$directory
    $first=Join-Path $directory 'install-v1.log'
    if ($case -ne 'missing-first') { [IO.File]::WriteAllText($first,$(if ($case -eq 'empty-first') { '' } else { 'actual synthetic first-install receipt' })) }
    if ($case -eq 'prior-target') { [IO.File]::WriteAllText((Join-Path $directory 'repair.log'),'preserve prior target') }
    $state=New-ScopedMsiCallerState
    $script:started=0
    $script:case=$case
    $script:fake=New-FakeMsiCaller -WaitFailure:($case -eq 'wait-failure')
    $caught=''; $code=$null
    try {
        $code=Invoke-ScopedMsiLogObservation -Directory $directory -Repo $repo -Caller 'owned-fake.exe' -Arguments 'owned synthetic arguments' -State $state -StartCaller {
            $script:started++
            if ($script:case -eq 'create-delete-rename') {
                $target=Join-Path $script:directory 'repair.log'
                $alternate=Join-Path $script:directory 'repair-renamed.log'
                [IO.File]::WriteAllText($target,'owned synthetic repair log')
                [IO.File]::Move($target,$alternate)
                [IO.File]::Move($alternate,$target)
                [IO.File]::Delete($target)
            }
            return $script:fake
        }
    } catch { $caught=$_.Exception.Message }
    if ($case -in @('missing-first','empty-first','prior-target')) {
        Check ($caught.Length -gt 0 -and $started -eq 0) ($case+': precondition refuses before any caller launch')
        if ($case -eq 'prior-target') { Check ([IO.File]::ReadAllText((Join-Path $directory 'repair.log')) -ceq 'preserve prior target') 'prior target bytes are never overwritten or deleted' }
        continue
    }
    Check ($started -eq 1 -and $fake.Disposed -eq 1) ($case+': fake caller runs once and is disposed')
    $reports=@(Get-ChildItem -LiteralPath $family -File -Filter 'maintenance-observation-*.json')
    Check ($reports.Count -eq 1) ($case+': exactly one bounded observation report is retained')
    $report=[IO.File]::ReadAllText($reports[0].FullName) | ConvertFrom-Json
    Check ($report.Control.Result -ceq 'PASS' -and -not $report.Control.Incomplete -and
        @($report.Control.Events | Where-Object Kind -eq 'Created').Count -ge 1 -and
        @($report.Control.Events | Where-Object Kind -eq 'Renamed').Count -ge 2 -and
        @($report.Control.Events | Where-Object Kind -eq 'Deleted').Count -ge 1) ($case+': real harmless control proves create/rename/delete')
    Check ($report.FirstInstallPreserved -and $report.FirstInstallBefore.Sha256 -ceq $report.FirstInstallAfterControl.Sha256) ($case+': real first-install receipt survives precondition and observation')
    Check ($report.Filter -ceq 'repair.log' -and -not $report.IncludeSubdirectories -and -not $report.Incomplete) ($case+': watcher is confined to one exact filename and directory')
    Check ($report.CallerPid -eq 4321 -and $null -ne $report.CallerCreationFileTime -and
        $report.Arguments -ceq 'owned synthetic arguments' -and -not $report.EventsIdentifyActor -and $report.MsiReceiptRequirementsUnchanged) ($case+': capture retains caller metadata without attributing event actors')
    if ($case -eq 'wait-failure') {
        Check ($caught -match 'injected_wait' -and $report.CallerStateUncertain -and $state.Uncertain) 'unknown caller state and primary failure are retained'
    } else { Check ($caught.Length -eq 0 -and $code -eq 0 -and -not $report.CallerStateUncertain) ($case+': confirmed fake caller exit is retained') }
    if ($case -eq 'no-target') { Check ($report.Events.Count -eq 0 -and -not $report.TargetAfter.Exists) 'absence is observed without fabricating a log receipt' }
    if ($case -eq 'create-delete-rename') {
        Check (@($report.Events | Where-Object Kind -eq 'Created').Count -ge 1 -and
            @($report.Events | Where-Object Kind -eq 'Renamed').Count -ge 2 -and
            @($report.Events | Where-Object Kind -eq 'Deleted').Count -ge 1 -and -not $report.TargetAfter.Exists) 'transient target creation/rename/deletion survives the blocking caller wait'
        $sequence=@($report.Events | Select-Object -ExpandProperty Sequence)
        Check (($sequence -join ',') -ceq (@($sequence | Sort-Object) -join ',') -and
            $report.Events[0].Kind -ceq 'Created' -and $report.Events[-1].Kind -ceq 'Deleted') 'captured events retain session sequence rather than eventkind grouping'
    }
}
$watch=New-ScopedLogWatch -Directory (Join-Path $root 'no-target\main') -FileName 'error-control.log' -Repo $repo
try {
    $id=@($watch.Identifiers | Where-Object { $_.EndsWith('.Error') })[0]
    [void](New-Event -SourceIdentifier $id)
    Receive-ScopedLogEvents $watch
    Check $watch.Incomplete 'watcher errors invalidate a no-event conclusion'
} finally { Close-ScopedLogWatch $watch }
Check ($watch.Closed) 'watcher is disposed after errors'
foreach ($id in $watch.Identifiers) {
    Check (@(Get-EventSubscriber -SourceIdentifier $id -ErrorAction SilentlyContinue).Count -eq 0 -and
        @(Get-Event -SourceIdentifier $id -ErrorAction SilentlyContinue).Count -eq 0) 'only owned event registrations and queued events are removed'
}
$report=[ordered]@{ Result='PASS'; Checks=$checks; RealControlWatchers=$true; FakeMsiCallersOnly=$true; MsiExecuted=$false; FixtureBuildInvoked=$false }
[IO.File]::WriteAllText((Join-Path $root 'result.json'),($report | ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
$report | ConvertTo-Json -Compress
Write-Output ('Evidence='+$root)
