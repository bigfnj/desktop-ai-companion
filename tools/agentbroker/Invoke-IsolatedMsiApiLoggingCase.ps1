#requires -Version 7
<#
.SYNOPSIS
Plan or explicitly run one owned synthetic install/target/API-uninstall case.
.DESCRIPTION
Default validates a prebuilt main/v2 fixture through the existing probe and prints a plan.
Apply uses fresh public-image preflights and one retained install caller. It starts only
the hash-verified installed SyntheticCompanion, then runs the existing API probe once.
No retries, cleanup MSI, global settings, tool installs or unrelated process termination.
Only the retained synthetic child may be terminated during failure cleanup. A timed-out
MSI/probe caller is left running and its exact identity is recorded for recovery.
.EXAMPLE
pwsh -NoProfile -File tools/agentbroker/Invoke-IsolatedMsiApiLoggingCase.ps1 -FixtureDirectory '<exact-built-root>' -ExpectedProductCode '{exact-v2-product}' -ExpectedMsiSha256 '<reviewed-sha256>'
.EXAMPLE
pwsh -NoProfile -File tools/agentbroker/Invoke-IsolatedMsiApiLoggingCase.ps1 -FixtureDirectory '<same>' -ExpectedProductCode '{same}' -ExpectedMsiSha256 '<same>' -CaseDirectory '<printed-case-directory>' -Apply
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FixtureDirectory,
    [Parameter(Mandatory)][string]$ExpectedProductCode,
    [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$ExpectedMsiSha256,
    [string]$CaseDirectory,
    [switch]$Apply
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$buildRoot=Join-Path $repo 'build'
$utf8=[Text.UTF8Encoding]::new($false)
. (Join-Path $repo 'installer\ScopedShutdown\PathSafety.ps1')
. (Join-Path $repo 'tests\InstallerShutdown.Tests\MsiCallerSafety.ps1')
$probe=Join-Path $PSScriptRoot 'Test-IsolatedMsiApiLogging.ps1'

function Invoke-ApiLoggingCaseSequence($Services,$Context) {
    $steps=[Collections.Generic.List[string]]::new()
    $phase='PreflightBeforeInstall'; $failure=$null; $cleanupFailed=$false
    try {
        $null=&$Services.PreflightBeforeInstall $Context; $steps.Add($phase)
        $phase='Install'; $code=&$Services.Install $Context
        if($Context.InstallState.Uncertain -or $code -ne 0){throw 'install_not_confirmed'}; $steps.Add($phase)
        $phase='VerifyInstalled'; $null=&$Services.VerifyInstalled $Context; $steps.Add($phase)
        $phase='StartFixture'; $null=&$Services.StartFixture $Context; $steps.Add($phase)
        $phase='WaitReady'; $null=&$Services.WaitReady $Context; $steps.Add($phase)
        $phase='PreflightBeforeApi'; $null=&$Services.PreflightBeforeApi $Context; $steps.Add($phase)
        $phase='Probe'; $code=&$Services.Probe $Context
        if($Context.ProbeState.Uncertain -or $code -ne 0){throw 'probe_not_confirmed'}; $steps.Add($phase)
        $phase='WaitFixtureExit'; $null=&$Services.WaitFixtureExit $Context; $steps.Add($phase)
    } catch { $failure=$phase }
    finally {
        try{$null=&$Services.CleanupFixture $Context}catch{$cleanupFailed=$true}
    }
    return [ordered]@{Status=$(if($null-eq$failure-and-not$cleanupFailed){'PASS'}else{'FAIL'});
        CompletedSteps=$steps.ToArray();FailurePhase=$failure;OwnedChildCleanupFailed=$cleanupFailed;
        InstallCallerUncertain=[bool]$Context.InstallState.Uncertain;ProbeCallerUncertain=[bool]$Context.ProbeState.Uncertain}
}
function Assert-CasePath([string]$Path) { return Assert-ScopedShutdownPath -Path $Path -TrustedRoot $repo }
function Write-CaseNew([string]$Path,$Value) {
    $null=Assert-CasePath $Path
    $bytes=$utf8.GetBytes((ConvertTo-Json -InputObject $Value -Depth 12)+"`n")
    if($bytes.Length-gt65536){throw 'case_report_limit'}
    $file=[IO.File]::Open($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try{$file.Write($bytes,0,$bytes.Length)}finally{$file.Dispose()}
}
function Read-CaseJson([string]$Path) {
    $null=Assert-CasePath $Path
    $file=Get-Item -LiteralPath $Path -Force
    if($file.PSIsContainer-or$file.Length-gt65536){throw 'case_input_limit'}
    return ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($Path,$utf8)) -Depth 20
}
function Import-CaseMetadataFunctions {
    foreach($entry in @(
        @{Source=$probe;Names=@('Assert-ProbePath','Read-ProbeJson','Confirm-Preflight','Confirm-ExactFixtureVersion','Confirm-RegisteredMetadata')},
        @{Source=(Join-Path $repo 'tests\Test-IsolatedMsiLifecycle.ps1');Names=@('Release-Com','Product-State')})) {
        $null=Assert-CasePath $entry.Source
        $tokens=$null;$errors=$null
        $syntax=[Management.Automation.Language.Parser]::ParseFile($entry.Source,[ref]$tokens,[ref]$errors)
        if($errors.Count){throw 'metadata_source_parse_failed'}
        foreach($name in $entry.Names) {
            $node=$syntax.Find({param($item) $item-is[Management.Automation.Language.FunctionDefinitionAst]-and$item.Name-ceq$name},$true)
            if($null-eq$node){throw 'metadata_validator_missing'}
            $body=$node.Body.Extent.Text
            $definition=$body.Substring(1,$body.Length-2)
            if($null-ne$node.Parameters-and$node.Parameters.Count-gt0) {
                $definition='param('+ (($node.Parameters|ForEach-Object{$_.Extent.Text})-join',') +")`n"+$definition
            }
            Set-Item -LiteralPath ('Function:script:'+$name) -Value ([scriptblock]::Create($definition))
        }
    }
}
function Invoke-CaseCaller($Context,[string]$Name,[string]$Executable,[string[]]$Arguments,$State,[int]$Timeout) {
    foreach($value in $Arguments){if($value-match'["\r\n]'-or$value.EndsWith('\')){throw 'case_argument_refused'}}
    $code=Invoke-ScopedMsiCaller -State $State -TimeoutMilliseconds $Timeout -StartCaller {
        Start-CaseRetainedCaller $Context $Name $Executable (($Arguments|ForEach-Object{ '"'+$_+'"' })-join' ')
    }
    if($Context.EvidenceWriteFailed){throw 'case_caller_journal_failed'}
    return $code
}
function Start-CaseRetainedCaller($Context,[string]$Name,[string]$Executable,[string]$Line) {
    if($Name-cnotin@('install','api-probe','preflight-install','preflight-api')){throw 'case_caller_name_refused'}
    foreach($suffix in @('.caller.out.txt','.caller.err.txt','.started.json')) {
        $path=Join-Path $Context.CaseRoot ($Name+$suffix);$null=Assert-CasePath $path
        if([IO.File]::Exists($path)-or[IO.Directory]::Exists($path)){throw 'caller_evidence_reuse_refused'}
    }
    $created=Start-Process -FilePath $Executable -ArgumentList $Line -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $Context.CaseRoot ($Name+'.caller.out.txt')) -RedirectStandardError (Join-Path $Context.CaseRoot ($Name+'.caller.err.txt'))
    try {
        $null=$created.SafeHandle
        Write-CaseNew (Join-Path $Context.CaseRoot ($Name+'.started.json')) ([ordered]@{CallerPid=$created.Id;CallerCreationFileTime=$created.StartTime.ToFileTimeUtc()})
    } catch {
        # Still return the retained process to the bounded owner. Never lose it to a journal error.
        $Context.EvidenceWriteFailed=$true
    }
    return $created
}
function Invoke-CasePreflight($Context,[string]$Name) {
    $output=Join-Path $Context.CaseRoot ($Name+'.json')
    $null=Assert-CasePath $output
    $state=New-ScopedMsiCallerState
    $Context.PreflightState=$state
    $code=Invoke-CaseCaller $Context $Name $Context.PowerShell @('-NoProfile','-File',(Join-Path $repo 'tests\Test-InstallerPublicImagePreflight.ps1'),'-OutputPath',$output) $state 30000
    if($code-ne0-or$state.Uncertain){throw 'case_preflight_not_confirmed'}
    $value=Read-CaseJson $output
    if($value.ScopedShutdownSafe-ne$true){throw 'case_preflight_not_safe'}
    Confirm-Preflight $output
    return $output
}

# No installation or process launch occurs in the default plan.
$script:caseMsiMayHaveRun=$false
$context=$null
$caseRoot=$null
try {
$fixture=[IO.Path]::GetFullPath($FixtureDirectory).TrimEnd('\')
$fixtureParent=Join-Path $buildRoot 'isolated-msi'
if((Split-Path $fixture -Parent)-ne$fixtureParent-or(Split-Path $fixture -Leaf)-cnotmatch'^[a-f0-9]{32}$'){throw 'case_fixture_scope_refused'}
$null=Assert-CasePath $fixture
if([string]::IsNullOrWhiteSpace($CaseDirectory)) {
    if($Apply){throw 'separately_reviewed_case_required'}
    $CaseDirectory=Join-Path $fixture ('api-case-'+[guid]::NewGuid().ToString('N'))
}
$caseRoot=[IO.Path]::GetFullPath($CaseDirectory).TrimEnd('\')
$match=[regex]::Match((Split-Path $caseRoot -Leaf),'^api-case-([0-9a-f]{32})$')
if((Split-Path $caseRoot -Parent)-ne$fixture-or-not$match.Success){throw 'case_evidence_scope_refused'}
$null=Assert-CasePath $caseRoot
$apiRoot=Join-Path $fixture ('api-logging-'+$match.Groups[1].Value)
$null=Assert-CasePath $apiRoot
foreach($path in @($caseRoot,$apiRoot)){if([IO.File]::Exists($path)-or[IO.Directory]::Exists($path)){throw 'unused_case_required'}}
$null=Assert-CasePath $probe
$planned=&$probe -FixtureDirectory $fixture -Version 2 -ExpectedProductCode $ExpectedProductCode -Operation Uninstall -EvidenceDirectory $apiRoot
$plan=ConvertFrom-Json -InputObject ($planned-join"`n")
if($plan.Status-cne'planned_only'-or$plan.MsiExecuted-ne$false-or$plan.ProductCode-cne$ExpectedProductCode-or$plan.EvidenceDirectory-cne$apiRoot){throw 'probe_plan_refused'}
$msi=Join-Path $fixture 'main\v2\fixture.msi'
$null=Assert-CasePath $msi
if((Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash-cne$ExpectedMsiSha256){throw 'reviewed_msi_hash_mismatch'}
if(-not$Apply){[ordered]@{Status='planned_only';MsiExecuted=$false;CaseDirectory=$caseRoot;ApiEvidenceDirectory=$apiRoot;ProductCode=$ExpectedProductCode;MsiSha256=$ExpectedMsiSha256}|ConvertTo-Json;return}
if([Security.Principal.WindowsIdentity]::GetCurrent().Name-match'\\CodexSandbox(?:Offline|Online)$'){throw 'normal_user_required'}
$null=New-Item -Path $caseRoot -ItemType Directory
Write-CaseNew (Join-Path $caseRoot 'reservation.json') ([ordered]@{ProductCode=$ExpectedProductCode;MsiSha256=$ExpectedMsiSha256;ApiEvidenceDirectory=$apiRoot;MsiExecuted=$false})
Import-CaseMetadataFunctions
Add-Type -Path (Assert-CasePath (Join-Path $PSScriptRoot 'MsiApiLoggingProbe.cs'))
$manifest=Read-CaseJson (Join-Path $fixture 'fixture.json')
$family=@($manifest.Families|Where-Object Name -CEQ 'main')[0]
$installed=Join-Path $fixture 'main\installed'
$image=Join-Path $installed 'DesktopAICompanion.exe'
$powershell=[Environment]::ProcessPath
if([IO.Path]::GetFileName($powershell)-ine'pwsh.exe'){throw 'powershell7_caller_required'}
$context=[pscustomobject]@{CaseRoot=$caseRoot;ApiRoot=$apiRoot;PowerShell=$powershell;Product=$ExpectedProductCode;Msi=$msi;InstallRoot=$installed;Image=$image;
    InstallState=(New-ScopedMsiCallerState);ProbeState=(New-ScopedMsiCallerState);PreflightState=$null;SetupPreflight=$null;ApiPreflight=$null;
    Child=$null;ChildPid=0;ChildCreated=$null;ChildExited=$false;ChildForcedCleanup=$false;ApiResult=$null;EvidenceWriteFailed=$false}
$services=@{
    PreflightBeforeInstall={param($c)
        $c.SetupPreflight=Invoke-CasePreflight $c 'preflight-install'
        if((Product-State $c.Product)-ne-1){throw 'prior_fixture_registration_refused'}
    }
    Install={param($c)
        $log=Join-Path $c.CaseRoot 'install.log';$null=Assert-CasePath $log
        if([IO.File]::Exists($log)-or[IO.Directory]::Exists($log)){throw 'setup_log_reuse_refused'}
        # CLI switches remain bare. Quoted scalar values preserve boundaries without a shell.
        $caller=Join-Path ([Environment]::SystemDirectory) 'msiexec.exe'
        $line='/qn /norestart /l*v "'+$log+'" /i "'+$c.Msi+'" CLEANINSTALL="" WIXUI_EXITDIALOGOPTIONALCHECKBOX=""'
        $script:caseMsiMayHaveRun=$true
        $code=Invoke-ScopedMsiCaller -State $c.InstallState -TimeoutMilliseconds 180000 -StartCaller {Start-CaseRetainedCaller $c 'install' $caller $line}
        if($c.EvidenceWriteFailed){throw 'case_caller_journal_failed'}
        if(-not[IO.File]::Exists($log)-or(Get-Item -LiteralPath $log).Length-eq0){throw 'setup_log_receipt_missing'}
        return $code
    }
    VerifyInstalled={param($c)
        Confirm-RegisteredMetadata ([InstallerLoggingProbe.NativeInstallerApi]::RegisteredProperty($c.Product,'InstallLocation')) `
            ([InstallerLoggingProbe.NativeInstallerApi]::RegisteredProperty($c.Product,'ProductName')) `
            ([InstallerLoggingProbe.NativeInstallerApi]::RegisteredProperty($c.Product,'VersionString')) $c.InstallRoot $family.ProductName '0.0.2'
        $null=Assert-CasePath $c.Image
        if((Get-FileHash -LiteralPath $c.Image -Algorithm SHA256).Hash-cne$manifest.SyntheticCompanionSha256){throw 'installed_synthetic_hash_mismatch'}
    }
    StartFixture={param($c)
        $ready=Join-Path $c.CaseRoot 'fixture-ready.txt';$null=Assert-CasePath $ready
        if([IO.File]::Exists($ready)){throw 'ready_file_reuse_refused'}
        $c.Child=Start-Process -FilePath $c.Image -ArgumentList ('--ready "'+$ready+'"') -WindowStyle Hidden -PassThru
        $null=$c.Child.SafeHandle;$c.ChildPid=$c.Child.Id;$c.ChildCreated=$c.Child.StartTime.ToFileTimeUtc()
        Write-CaseNew (Join-Path $c.CaseRoot 'child-started.json') ([ordered]@{ChildPid=$c.ChildPid;ChildCreationFileTime=$c.ChildCreated})
    }
    WaitReady={param($c)
        $ready=Join-Path $c.CaseRoot 'fixture-ready.txt';$deadline=[DateTime]::UtcNow.AddSeconds(10)
        while(-not[IO.File]::Exists($ready)) {if($c.Child.HasExited-or[DateTime]::UtcNow-ge$deadline){throw 'owned_fixture_not_ready'};Start-Sleep -Milliseconds 25}
        $null=Assert-CasePath $ready
        if((Get-Item -LiteralPath $ready).Length-gt128-or$c.Child.HasExited){throw 'owned_fixture_not_ready'}
    }
    PreflightBeforeApi={param($c) $c.ApiPreflight=Invoke-CasePreflight $c 'preflight-api'}
    Probe={param($c)
        $code=Invoke-CaseCaller $c 'api-probe' $c.PowerShell @('-NoProfile','-File',$probe,'-FixtureDirectory',$fixture,'-Version','2',
            '-ExpectedProductCode',$c.Product,'-Operation','Uninstall','-EvidenceDirectory',$c.ApiRoot,'-PublicImagePreflightPath',$c.ApiPreflight,'-Apply') $c.ProbeState 240000
        if(-not$c.ProbeState.Uncertain) {
            $c.ApiResult=Read-CaseJson (Join-Path $c.ApiRoot 'result.json')
            if($c.ApiResult.CallerStateUncertain){$c.ProbeState.Uncertain=$true;$c.ProbeState.Reason='inner_api_caller_uncertain'}
            if($c.ApiResult.Status-cne'PASS'){throw 'api_receipt_not_passed'}
        }
        return $code
    }
    WaitFixtureExit={param($c)
        if(-not$c.Child.WaitForExit(5000)){throw 'owned_fixture_did_not_exit'}
        $c.ChildExited=$true
    }
    CleanupFixture={param($c)
        if($null-ne$c.Child) {
            try {
                if(-not$c.Child.HasExited) {
                    # Only the exact retained synthetic child; never an MSI/probe caller or PID lookup.
                    $c.ChildForcedCleanup=$true;$c.Child.Kill()
                    if(-not$c.Child.WaitForExit(5000)){throw 'owned_child_cleanup_timeout'}
                }
                $c.ChildExited=$c.Child.HasExited
            } finally {$c.Child.Dispose()}
        }
    }
}
$report=Invoke-ApiLoggingCaseSequence $services $context
$report['ChildPid']=$context.ChildPid;$report['ChildCreationFileTime']=$context.ChildCreated;$report['ChildExited']=$context.ChildExited
$report['ChildForcedCleanup']=$context.ChildForcedCleanup
if($context.ChildForcedCleanup){$report.Status='FAIL'}
$report['InstallCallerPid']=$context.InstallState.CallerPid;$report['InstallCallerCreationFileTime']=$context.InstallState.CallerCreationFileTime
$report['ProbeCallerPid']=$context.ProbeState.CallerPid;$report['ProbeCallerCreationFileTime']=$context.ProbeState.CallerCreationFileTime
$report['PreflightCallerUncertain']=($null-ne$context.PreflightState-and$context.PreflightState.Uncertain)
$report['PreflightCallerPid']=$(if($null-ne$context.PreflightState){$context.PreflightState.CallerPid}else{0})
$report['PreflightCallerCreationFileTime']=$(if($null-ne$context.PreflightState){$context.PreflightState.CallerCreationFileTime}else{$null})
$report['ApiResult']=$context.ApiResult;$report['AutomaticRetryOrCleanupMsiExecuted']=$false;$report['RawLogsExported']=$false
$report['CallerJournalFailed']=$context.EvidenceWriteFailed
Write-CaseNew (Join-Path $caseRoot 'result.json') $report
$report|ConvertTo-Json -Depth 12
if($report.Status-cne'PASS'){exit 1}
} catch {
    [ordered]@{Status='refused';FailureType=$_.Exception.GetType().Name;MsiMayHaveRun=$script:caseMsiMayHaveRun;
        CallerStateUncertain=($null-ne$context-and($context.InstallState.Uncertain-or$context.ProbeState.Uncertain));
        AutomaticRetryOrCleanupMsiExecuted=$false;RawLogsExported=$false}|ConvertTo-Json -Compress
    exit 1
}
