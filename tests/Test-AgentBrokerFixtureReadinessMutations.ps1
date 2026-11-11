#requires -Version 7
<#
.SYNOPSIS
Qualify the four focused readiness/clock regressions in an isolated checkout.
.DESCRIPTION
Default is plan-only. Execute requires an already restored and built Worker test project.
Only test sources are temporarily changed; each is restored byte for byte before proceeding.
Keep this checkout idle while it runs. Raw captures and the summary remain under artifacts.
#>
[CmdletBinding()]
param(
    [ValidateSet('all','clock-early','ready-before-close','retry-omitted','diagnostic-omitted')][string]$Case='all',
    [switch]$Execute
)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$clock=Join-Path $PSScriptRoot 'AgentBroker.Worker.Tests/WaitLifecycleChecks.cs'
$publisher=Join-Path $PSScriptRoot 'agentbroker-fixture-ready.mjs'
$consumer=Join-Path $PSScriptRoot 'AgentBrokerFixtureStartup.ps1'
$positive=Join-Path $PSScriptRoot 'Test-AgentBrokerFixtureReadiness.ps1'
$project=Join-Path $PSScriptRoot 'AgentBroker.Worker.Tests/AgentBroker.Worker.Tests.csproj'
$harness=Join-Path $PSScriptRoot 'AgentBroker.Worker.Tests/bin/Release/net10.0-windows/AgentBroker.Worker.Tests.dll'
$utf8=[Text.UTF8Encoding]::new($false,$true)
$cases=@(
    @{name='clock-early';path=$clock;old='timer.Due <= timestamp';new='timer.Due <= timestamp + 1';label='QUIET_CLOCK_NOT_BEFORE_DUE'},
    @{name='ready-before-close';path=$publisher;old='io.writeFileSync(pendingReadyPath, JSON.stringify(value));';new='io.writeFileSync(readyPath, JSON.stringify(value));';label='READY_NOT_VISIBLE_UNTIL_CLOSED'},
    @{name='retry-omitted';path=$publisher;old="if (code !== 'OTHER' && retries < 20 && now() < retryUntil)";new="if (false && retries < 20 && now() < retryUntil)";label='READY_TRANSIENT_EPERM_RECOVERS'},
    @{name='diagnostic-omitted';path=$consumer;old='if ($sample -cmatch ';new='if ($false -and $sample -cmatch ';label='READY_PERSISTENT_CONSUMER_DIAGNOSTIC'}
)
if ($Case -ne 'all') { $cases=@($cases|Where-Object { $_.name -ceq $Case }) }
if (-not $Execute) { $cases|ForEach-Object { Write-Output ('PLAN: '+$_.name+' -> '+$_.label) }; Write-Output 'PLAN_ONLY: no files changed or children started.'; return }
function Need([bool]$Value,[string]$Label) { if (-not $Value) { throw $Label } }
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function ByteHash([byte[]]$Value) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Value)) }
Need ([IO.File]::Exists($harness)) 'READINESS_MUTATIONS_BUILD_TEST_PROJECT_FIRST'
$pwsh=@(Get-Command pwsh.exe -CommandType Application -ErrorAction Stop)[0].Source
$dotnet=@(Get-Command dotnet.exe -CommandType Application -ErrorAction Stop)[0].Source
$run=[IO.Path]::GetFullPath((Join-Path $root ('artifacts/readiness-mutations/'+[Guid]::NewGuid().ToString('N'))))
Need ($run.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) 'READINESS_MUTATIONS_OWNED_OUTPUT'
[void][IO.Directory]::CreateDirectory($run)
$captures=[Collections.Generic.List[object]]::new(); $results=[Collections.Generic.List[object]]::new()
function Capture([string]$Name,[string]$Exe,[string[]]$Arguments) {
    $start=[Diagnostics.ProcessStartInfo]::new($Exe); $start.WorkingDirectory=$root
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.StandardOutputEncoding=$utf8; $start.StandardErrorEncoding=$utf8
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$start
    $closed=$false; $started=[DateTime]::UtcNow
    try {
        Need ($process.Start()) 'READINESS_MUTATION_CHILD_START'
        $out=$process.StandardOutput.ReadToEndAsync(); $err=$process.StandardError.ReadToEndAsync()
        Need ($process.WaitForExit(120000)) 'READINESS_MUTATION_CHILD_TIMEOUT'
        Need ([Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($out,$err)).Wait(10000)) 'READINESS_MUTATION_DRAIN_TIMEOUT'
        $stdout=$out.GetAwaiter().GetResult(); $stderr=$err.GetAwaiter().GetResult(); $closed=$true
        $outPath=Join-Path $run ($Name+'.stdout.txt'); $errPath=Join-Path $run ($Name+'.stderr.txt')
        [IO.File]::WriteAllText($outPath,$stdout,$utf8); [IO.File]::WriteAllText($errPath,$stderr,$utf8)
        $row=[pscustomobject]@{name=$Name;nativeExit=$process.ExitCode;closed=$closed;startedUtc=$started.ToString('o');finishedUtc=[DateTime]::UtcNow.ToString('o');stdoutSha256=Hash $outPath;stderrSha256=Hash $errPath}
        $captures.Add($row)
        return [pscustomobject]@{nativeExit=$process.ExitCode;out=$stdout;err=$stderr;started=$started}
    } finally {
        if (-not $closed -and $process.Id -gt 0 -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(10000) }
        $process.Dispose()
    }
}
function Check-Positive($Observed,[bool]$ClockCase) {
    Need ($Observed.nativeExit -eq 0 -and [string]::IsNullOrWhiteSpace($Observed.err)) 'READINESS_HEALTHY_NATIVE_ZERO'
    if ($ClockCase) {
        Need ($Observed.out -cmatch '(?m)^RESULT=PASS assertions=28\r?$') 'READINESS_CLOCK_HEALTHY_28'
        Need ($Observed.out -cmatch ('(?m)^WORKER_HARNESS_SHA256='+(Hash $harness)+'\r?$')) 'READINESS_CLOCK_LOADED_HARNESS'
    } else {
        Need ($Observed.out -cmatch '(?m)^PASS: 3 fixture readiness checks; owned children and fixture files cleaned\.\r?$') 'READINESS_HEALTHY_THREE'
        Need ($Observed.out -cmatch ('(?m)^READY_HELPER_SHA256='+(Hash $publisher).ToLowerInvariant()+'\r?$') -and
              $Observed.out -cmatch ('(?m)^READY_CONSUMER_SHA256='+(Hash $consumer)+'\r?$')) 'READINESS_LOADED_HELPERS'
    }
}
function Build-Clock([string]$Name) {
    $before=Get-Item -LiteralPath $harness; $oldHash=Hash $harness; $oldTime=$before.LastWriteTimeUtc
    $built=Capture $Name $dotnet @('build',$project,'-c','Release','--no-restore','--no-incremental','--disable-build-servers','-p:BuildProjectReferences=false','-p:UseSharedCompilation=false','-p:NuGetAudit=false')
    Need ($built.nativeExit -eq 0) 'READINESS_CLOCK_REBUILD_FAILED'
    Need ((Get-Item -LiteralPath $harness).LastWriteTimeUtc -gt $oldTime -and (Get-Item -LiteralPath $harness).LastWriteTimeUtc -ge $built.started) 'READINESS_CLOCK_REBUILD_NOT_FRESH'
    return [pscustomobject]@{beforeSha256=$oldHash;afterSha256=Hash $harness;mtimeUtc=(Get-Item -LiteralPath $harness).LastWriteTimeUtc.ToString('o')}
}
$failure=$null
try {
    foreach ($item in $cases) {
        $isClock=$item.name -ceq 'clock-early'
        $exe=if ($isClock) { $dotnet } else { $pwsh }
        $arguments=if ($isClock) { @($harness,'--wait-lifecycle-core-only') } else { @('-NoProfile','-File',$positive) }
        Check-Positive (Capture ($item.name+'-healthy') $exe $arguments) $isClock
        $original=[IO.File]::ReadAllBytes($item.path); $originalHash=ByteHash $original
        $text=$utf8.GetString($original)
        Need (($text.Split($item.old,[StringSplitOptions]::None).Count-1) -eq 1) 'READINESS_MUTATION_ANCHOR_ONCE'
        $mutated=$utf8.GetBytes($text.Replace($item.old,$item.new)); $mutatedHash=ByteHash $mutated
        $build=$null; $restoredBuild=$null
        [IO.File]::WriteAllBytes($item.path,$mutated)
        try {
            Need ((Hash $item.path) -ceq $mutatedHash -and $mutatedHash -cne $originalHash) 'READINESS_MUTATED_SOURCE_PROOF'
            if ($isClock) { $build=Build-Clock ($item.name+'-build'); Need ($build.beforeSha256 -cne $build.afterSha256) 'READINESS_CHANGED_HARNESS_REQUIRED' }
            $observed=Capture ($item.name+'-mutant') $exe $arguments
            Need ($observed.nativeExit -eq 1) 'READINESS_MUTANT_NATIVE_ONE'
            if ($isClock) {
                $first=@($observed.err -split '\r?\n'|Where-Object { $_.Length -gt 0 })[0]
                Need ($first -ceq ('System.InvalidOperationException: '+$item.label)) 'READINESS_MUTANT_FIRST_CLOCK_LABEL'
                Need ($observed.out -cmatch ('(?m)^WORKER_HARNESS_SHA256='+(Hash $harness)+'\r?$')) 'READINESS_MUTANT_LOADED_HARNESS'
            } else {
                Need ($observed.err -cmatch ('\AFAIL: '+$item.label+'\r?\n\z') -and $observed.out -cnotmatch '(?m)^FAIL:') 'READINESS_MUTANT_SOLE_TERMINAL_LABEL'
            }
            Need ((Hash $item.path) -ceq $mutatedHash) 'READINESS_MUTANT_SOURCE_UNCHANGED'
        } finally {
            Need ((Hash $item.path) -ceq $mutatedHash) 'READINESS_RESTORE_REFUSED_CONCURRENT_CHANGE'
            [IO.File]::WriteAllBytes($item.path,$original)
            Need ((Hash $item.path) -ceq $originalHash) 'READINESS_SOURCE_RESTORATION'
            if ($isClock) { $restoredBuild=Build-Clock ($item.name+'-restore-build') }
        }
        Check-Positive (Capture ($item.name+'-restored') $exe $arguments) $isClock
        if ($isClock) { Need ($restoredBuild.afterSha256 -ceq $build.beforeSha256) 'READINESS_RESTORED_HARNESS_IDENTITY' }
        $results.Add([pscustomobject]@{name=$item.name;label=$item.label;status='FIRED';sourceBeforeSha256=$originalHash;sourceMutantSha256=$mutatedHash;sourceRestoredSha256=Hash $item.path;build=$build;restoredBuild=$restoredBuild})
        Write-Output ('FIRED: '+$item.name+' -> '+$item.label)
    }
} catch { $failure=$_ }
finally {
    $summary=[ordered]@{status=if ($null -eq $failure) { 'PASS' } else { 'HOLD' };callerSha256=Hash $PSCommandPath;cases=$results.ToArray();captures=$captures.ToArray()}
    if ($null -ne $failure) { $summary.failure=$failure.Exception.Message }
    [IO.File]::WriteAllText((Join-Path $run 'results.json'),($summary|ConvertTo-Json -Depth 10)+[char]10,$utf8)
    Write-Output ('READINESS_MUTATION_RESULTS='+$run)
}
if ($null -ne $failure) { throw $failure }
Write-Output ('PASS: '+$results.Count+' focused readiness mutations; original sources restored.')
