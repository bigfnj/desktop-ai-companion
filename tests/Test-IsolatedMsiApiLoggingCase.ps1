#requires -Version 7
# Actual orchestration sequence with in-memory service fakes; no entry point or live action.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$source=Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\agentbroker\Invoke-IsolatedMsiApiLoggingCase.ps1'
$tokens=$null;$errors=$null
$syntax=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Test-IsolatedMsiApiLoggingCase.ps1: source parse failed'}
$node=$syntax.Find({param($x) $x-is[Management.Automation.Language.FunctionDefinitionAst]-and$x.Name-ceq'Invoke-ApiLoggingCaseSequence'},$true)
. ([scriptblock]::Create($node.Extent.Text))
$checks=0
function Check([bool]$Condition,[string]$Label){if(-not$Condition){throw ('Test-IsolatedMsiApiLoggingCase.ps1: '+$Label)};$script:checks++}
function Reject([scriptblock]$Action,[string]$Label){$refused=$false;try{&$Action}catch{$refused=$true};Check $refused $Label}
function Fake-Context {
    return [pscustomobject]@{Calls=[Collections.Generic.List[string]]::new();FailAt='';InstallExit=0;ProbeExit=0;
        InstallState=[pscustomobject]@{Uncertain=$false};ProbeState=[pscustomobject]@{Uncertain=$false};Child=$null;
        ForceInstallUncertainty=$false;ForceProbeUncertainty=$false;Cleaned=$false}
}
$services=@{}
foreach($name in @('PreflightBeforeInstall','Install','VerifyInstalled','StartFixture','WaitReady','PreflightBeforeApi','Probe','WaitFixtureExit','CleanupFixture')) {
    $step=$name
    $services[$name]={param($c)
        $c.Calls.Add($step)
        if($step-ceq'CleanupFixture'){$c.Cleaned=$true}
        if($step-ceq'StartFixture'){$c.Child='retained-synthetic-only'}
        if($c.FailAt-ceq$step){throw 'SYNTHETIC_PRIVATE_DETAILS'}
        if($step-ceq'Install'){$c.InstallState.Uncertain=$c.ForceInstallUncertainty;return $c.InstallExit}
        if($step-ceq'Probe'){$c.ProbeState.Uncertain=$c.ForceProbeUncertainty;return $c.ProbeExit}
    }.GetNewClosure()
}
$c=Fake-Context;$r=Invoke-ApiLoggingCaseSequence $services $c
Check ($r.Status-ceq'PASS') 'actual full sequence succeeds with valid fake receipts'
$expected='PreflightBeforeInstall,Install,VerifyInstalled,StartFixture,WaitReady,PreflightBeforeApi,Probe,WaitFixtureExit,CleanupFixture'
Check (($c.Calls-join',')-ceq$expected) 'actual order is preflight/install/verify/own-target/preflight/one-probe/exit/cleanup'
Check ($c.Cleaned-and$r.CompletedSteps.Count-eq8) 'owned cleanup always follows eight successful phases'
$phases=@('PreflightBeforeInstall','Install','VerifyInstalled','StartFixture','WaitReady','PreflightBeforeApi','Probe','WaitFixtureExit')
for($i=0;$i-lt$phases.Count;$i++) {
    $c=Fake-Context;$c.FailAt=$phases[$i];$r=Invoke-ApiLoggingCaseSequence $services $c
    Check ($r.Status-ceq'FAIL'-and$r.FailurePhase-ceq$phases[$i]) ('failure phase is fixed and exact: '+$phases[$i])
    Check ($c.Cleaned-and$c.Calls[$c.Calls.Count-1]-ceq'CleanupFixture') ('cleanup remains reachable: '+$phases[$i])
    Check ($c.Calls.Count-eq$i+2) ('no later action or retry follows failure: '+$phases[$i])
    Check ((ConvertTo-Json $r -Depth 5)-notmatch'SYNTHETIC_PRIVATE') ('raw failure is not exported: '+$phases[$i])
}
foreach($phase in @('Install','Probe')) {
    $c=Fake-Context;if($phase-ceq'Install'){$c.InstallExit=1603}else{$c.ProbeExit=1};$r=Invoke-ApiLoggingCaseSequence $services $c
    Check ($r.Status-ceq'FAIL'-and$r.FailurePhase-ceq$phase) ('nonzero exit prevents later acceptance: '+$phase)
    Check ($c.Calls.FindAll([Predicate[string]]{param($v)$v-eq$phase}).Count-eq1) ('failed caller never retried: '+$phase)
    $c=Fake-Context;if($phase-ceq'Install'){$c.ForceInstallUncertainty=$true}else{$c.ForceProbeUncertainty=$true};$r=Invoke-ApiLoggingCaseSequence $services $c
    Check ($r.Status-ceq'FAIL'-and$r.FailurePhase-ceq$phase) ('uncertain caller blocks following phases: '+$phase)
    Check ($c.Cleaned-and-not$c.Calls.Contains('Uninstall')) ('uncertainty does not add cleanup MSI: '+$phase)
}
$c=Fake-Context;$c.FailAt='CleanupFixture';$r=Invoke-ApiLoggingCaseSequence $services $c
Check ($r.Status-ceq'FAIL'-and$r.OwnedChildCleanupFailed) 'cleanup failure cannot report success'
$repo=Split-Path $PSScriptRoot -Parent
$probe=Join-Path $repo 'tools\agentbroker\Test-IsolatedMsiApiLogging.ps1'
$buildRoot=Join-Path $repo 'build'
$node=$syntax.Find({param($x) $x-is[Management.Automation.Language.FunctionDefinitionAst]-and$x.Name-ceq'Import-CaseMetadataFunctions'},$true)
. ([scriptblock]::Create($node.Extent.Text))
# Run the actual importer against public source. Its path classifier and eventual
# JSON reader are the only fakes, so this exercises real parameter binding without IO.
function Assert-CasePath([string]$Path){return $Path}
Import-CaseMetadataFunctions
function Assert-ScopedShutdownPath([string]$Path,[string]$TrustedRoot){return $Path}
Check ((Assert-ProbePath 'C:\Synthetic\metadata.json')-ceq'C:\Synthetic\metadata.json') 'actual imported external path parameter remains bound'
Confirm-ExactFixtureVersion '0.0.2' '0.0.2'
Check $true 'actual imported version validator accepts exact version parameters'
Reject {Confirm-ExactFixtureVersion '0.0.1' '0.0.2'} 'actual imported version validator rejects wrong installed version'
Confirm-RegisteredMetadata 'C:\Synthetic\installed' 'Synthetic Fixture' '0.0.2' 'C:\Synthetic\installed' 'Synthetic Fixture' '0.0.2'
Check $true 'actual imported metadata validator binds all six accepted parameters'
Reject {Confirm-RegisteredMetadata 'C:\Synthetic\foreign' 'Synthetic Fixture' '0.0.2' 'C:\Synthetic\installed' 'Synthetic Fixture' '0.0.2'} 'actual imported metadata validator rejects foreign location'
Reject {Confirm-RegisteredMetadata 'C:\Synthetic\installed' 'Foreign Fixture' '0.0.2' 'C:\Synthetic\installed' 'Synthetic Fixture' '0.0.2'} 'actual imported metadata validator rejects foreign name'
Reject {Confirm-RegisteredMetadata 'C:\Synthetic\installed' 'Synthetic Fixture' '0.0.1' 'C:\Synthetic\installed' 'Synthetic Fixture' '0.0.2'} 'actual imported metadata validator rejects wrong registered version'
function Read-ProbeJson([string]$Path){return [pscustomobject]@{ScopedShutdownSafe=$true;ObservedAtUtc=[DateTime]::UtcNow}}
Confirm-Preflight (Join-Path $buildRoot 'synthetic-preflight.json')
Check $true 'actual imported preflight validator binds its path before harmless fake JSON reader'
Write-Output ('PASS: '+$checks+' in-memory orchestration checks; no install, process, MSI, registry, GUI, configuration or protected fixtures accessed.')
