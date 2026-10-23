#requires -Version 7
<#
.SYNOPSIS
Plan one exact synthetic MSI maintenance logging probe. Default performs no installation.
.DESCRIPTION
This does not replace or relax Test-IsolatedMsiLifecycle.ps1. Only the main family of a
prebuilt GUID-owned fixture is allowed. Apply requires an already installed exact fixture,
explicit product/operation/evidence path and fresh public-image preflight. It never installs,
starts/stops a fixture process itself, retries, cleans registrations or kills an MSI caller.
The disposable worker configures logging and maintenance in the SAME process. Callback
output is finite counts and one exact known shutdown-marker boolean, never arbitrary text.
An absent verbose receipt still fails even when the operation returns success.
.EXAMPLE
pwsh -NoProfile -File tools/agentbroker/Test-IsolatedMsiApiLogging.ps1 -FixtureDirectory '<build/isolated-msi/32hex>' -Version 2 -ExpectedProductCode '{EXACT-FIXTURE-PRODUCT-GUID}' -Operation Uninstall
.EXAMPLE
pwsh -NoProfile -File tools/agentbroker/Test-IsolatedMsiApiLogging.ps1 -FixtureDirectory '<same>' -Version 2 -ExpectedProductCode '{same}' -Operation Uninstall -EvidenceDirectory '<printed-evidence-directory>' -PublicImagePreflightPath '<fresh-build-preflight.json>' -Apply
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FixtureDirectory,
    [Parameter(Mandatory)][ValidateSet(1,2)][int]$Version,
    [Parameter(Mandatory)][string]$ExpectedProductCode,
    [Parameter(Mandatory)][ValidateSet('Uninstall')][string]$Operation,
    [string]$EvidenceDirectory,
    [string]$PublicImagePreflightPath,
    [switch]$Apply,
    [switch]$Worker
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$buildRoot = Join-Path $repo 'build'
. (Join-Path $repo 'installer\ScopedShutdown\PathSafety.ps1')
. (Join-Path $repo 'tests\InstallerShutdown.Tests\MsiCallerSafety.ps1')
$utf8 = [Text.UTF8Encoding]::new($false)

function Assert-ProbePath([string]$Path) {
    return Assert-ScopedShutdownPath -Path $Path -TrustedRoot $repo
}
function Read-ProbeJson([string]$Path) {
    $null = Assert-ProbePath $Path
    $file = Get-Item -LiteralPath $Path -Force
    if ($file.PSIsContainer -or $file.Length -gt 65536) { throw 'bounded_json_required' }
    return ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($Path, $utf8)) -Depth 20
}
function Write-ProbeNew([string]$Path, $Value) {
    $null = Assert-ProbePath $Path
    $bytes = $utf8.GetBytes((ConvertTo-Json -InputObject $Value -Depth 12) + "`n")
    if ($bytes.Length -gt 65536) { throw 'bounded_report_required' }
    $stream = [IO.File]::Open($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try { $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
}
function Import-FixtureMetadataFunctions {
    # Import only existing read-only validation functions, not the lifecycle entry point.
    $source = Assert-ProbePath (Join-Path $repo 'tests\Test-IsolatedMsiLifecycle.ps1')
    $tokens = $null; $errors = $null
    $syntax = [Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$errors)
    if ($errors.Count) { throw 'fixture_validator_parse_failed' }
    foreach ($name in @('Assert-PlainPath','Assert-Check','Hash-File','Release-Com','Msi-Rows','Msi-Property',
        'Assert-NativeBinary','Fixture-Path','Fixture-Defaults','Assert-FixtureDefaults')) {
        $nodes = @($syntax.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name },$true))
        if ($nodes.Count -ne 1) { throw 'fixture_validator_missing' }
        $body = $nodes[0].Body.Extent.Text
        Set-Item -LiteralPath ('Function:script:' + $name) -Value ([scriptblock]::Create($body.Substring(1,$body.Length-2)))
    }
}
function Confirm-ProbeIdentity($Manifest, [string]$Tag, [int]$SelectedVersion, [string]$Product) {
    $parsed = [guid]::Empty
    if (-not [guid]::TryParseExact($Product,'B',[ref]$parsed) -or $parsed -eq [guid]::Empty -or
        $Product -cne $parsed.ToString('B').ToUpperInvariant() -or $Manifest.SchemaVersion -ne 1 -or $Manifest.Tag -cne $Tag) { throw 'fixture_identity_refused' }
    $families = @($Manifest.Families | Where-Object Name -CEQ 'main')
    if ($families.Count -ne 1) { throw 'main_fixture_required' }
    $family = $families[0]
    $upgrade = [guid]::Empty
    if (-not [guid]::TryParseExact([string]$family.UpgradeCode,'B',[ref]$upgrade) -or $upgrade -eq [guid]::Empty -or
        $family.UpgradeCode -in @('{327AF68D-A714-5C9B-B2D9-9C3404E03D38}','{DBF8DDB3-C4AB-498C-9E55-4193A734C573}') -or
        $family.ProductName -cne ('Desktop AI Companion Scoped Shutdown Test ' + $Tag + ' main') -or
        $family.RegistryRoot -cne ('Software\bigfnj\DACScopedShutdownTest\' + $Tag + '\main')) { throw 'isolated_family_required' }
    $packages = @($family.Packages | Where-Object Version -EQ $SelectedVersion)
    if ($packages.Count -ne 1 -or $packages[0].ProductCode -cne $Product -or $packages[0].Capability -cne '1') { throw 'exact_scoped_package_required' }
    return [pscustomobject]@{ Family=$family; Package=$packages[0]; ProductVersion=('0.0.'+$SelectedVersion) }
}
function Confirm-ExactFixtureVersion([string]$Actual,[string]$Expected) {
    if ($Expected -cnotmatch '^0\.0\.[12]$' -or $Actual -cne $Expected) { throw 'fixture_version_mismatch' }
}
function Confirm-RegisteredMetadata([string]$Location,[string]$Name,[string]$VersionString,
    [string]$ExpectedLocation,[string]$ExpectedName,[string]$ExpectedVersion) {
    if ([string]::IsNullOrWhiteSpace($Location) -or
        -not [string]::Equals([IO.Path]::GetFullPath($Location).TrimEnd('\'),$ExpectedLocation,[StringComparison]::OrdinalIgnoreCase) -or
        $Name -cne $ExpectedName) { throw 'registered_fixture_mismatch' }
    Confirm-ExactFixtureVersion $VersionString $ExpectedVersion
}
function Confirm-Preflight([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'fresh_preflight_required' }
    $full = Assert-ProbePath $Path
    if (-not $full.StartsWith($buildRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetExtension($full) -cne '.json') { throw 'preflight_scope_refused' }
    $value = Read-ProbeJson $full
    if ($value.ScopedShutdownSafe -ne $true) { throw 'preflight_not_safe' }
    $stamp = $value.ObservedAtUtc
    if ($stamp -is [DateTimeOffset]) { $observed=$stamp.UtcDateTime }
    elseif ($stamp -is [DateTime]) { $observed=$stamp.ToUniversalTime() }
    else { $observed=[DateTime]::Parse([string]$stamp,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime() }
    $age=([DateTime]::UtcNow-$observed).TotalSeconds
    if ($age -lt -15 -or $age -gt 120) { throw 'fresh_preflight_required' }
}
function Confirm-RegisteredFixture([string]$Product, [string]$Name, [string]$InstallRoot, [string]$ExpectedVersion) {
    $registered = [InstallerLoggingProbe.NativeInstallerApi]::RegisteredProperty($Product,'InstallLocation')
    Confirm-RegisteredMetadata $registered ([InstallerLoggingProbe.NativeInstallerApi]::RegisteredProperty($Product,'ProductName')) `
        ([InstallerLoggingProbe.NativeInstallerApi]::RegisteredProperty($Product,'VersionString')) $InstallRoot $Name $ExpectedVersion
    $null = Assert-ProbePath $InstallRoot
}
function Read-LogReceipt([string]$Path) {
    $null = Assert-ProbePath $Path
    if (-not [IO.File]::Exists($Path)) { return [ordered]@{ Exists=$false; Length=0; MarkerSeen=$false } }
    $file=Get-Item -LiteralPath $Path -Force
    if ($file.Length -gt 4194304) { throw 'log_receipt_size_refused' }
    $text=[IO.File]::ReadAllText($Path)
    return [ordered]@{ Exists=$true; Length=$file.Length; MarkerSeen=$text.Contains([InstallerLoggingProbe.Probe]::Marker) }
}

# No MSI install/configure/reinstall API is reached above or by the default plan.
$fixtureParent=Join-Path $buildRoot 'isolated-msi'
$script:fixtureRoot=[IO.Path]::GetFullPath($FixtureDirectory).TrimEnd('\')
if ((Split-Path $fixtureRoot -Parent) -ne $fixtureParent -or (Split-Path $fixtureRoot -Leaf) -cnotmatch '^[0-9a-f]{32}$') { throw 'exact_fixture_scope_required' }
$null=Assert-ProbePath $fixtureRoot
$tag=Split-Path $fixtureRoot -Leaf
$manifest=Read-ProbeJson (Join-Path $fixtureRoot 'fixture.json')
$identity=Confirm-ProbeIdentity $manifest $tag $Version $ExpectedProductCode
Import-FixtureMetadataFunctions
$msi=Fixture-Path ('main\v'+$Version+'\fixture.msi')
foreach ($pair in @(
    @((Join-Path $repo 'installer\DesktopAICompanion.wxs'),$manifest.ProductionWxsSha256),
    @((Join-Path $buildRoot 'scoped-shutdown\ScopedShutdown.dll'),$manifest.NativeDllSha256),
    @((Join-Path $fixtureRoot 'ScopedShutdown.dll'),$manifest.NativeDllSha256),
    @((Join-Path $fixtureRoot 'SyntheticCompanion.exe'),$manifest.SyntheticCompanionSha256),
    @($msi,$identity.Package.MsiSha256))) {
    $null=Assert-ProbePath $pair[0]
    if ((Get-Item -LiteralPath $pair[0] -Force).Length -gt 67108864 -or (Hash-File $pair[0]) -cne $pair[1]) { throw 'fixture_source_hash_mismatch' }
}
Confirm-ExactFixtureVersion (Msi-Property $msi 'ProductVersion') $identity.ProductVersion
foreach ($pair in @(@('ProductCode',$ExpectedProductCode),@('UpgradeCode',$identity.Family.UpgradeCode),@('ProductName',$identity.Family.ProductName),
    @('DESKTOP_AI_COMPANION_REGISTRYROOT',$identity.Family.RegistryRoot),@('DESKTOP_AI_COMPANION_SCOPED_SHUTDOWN_CAPABILITY','1'))) {
    if ((Msi-Property $msi $pair[0]) -cne $pair[1]) { throw 'package_identity_mismatch' }
}
Assert-NativeBinary $msi $manifest.NativeDllSha256
Assert-FixtureDefaults $msi $identity.Family
if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    if ($Apply -or $Worker) { throw 'separately_reviewed_evidence_path_required' }
    $EvidenceDirectory=Join-Path $fixtureRoot ('api-logging-'+[guid]::NewGuid().ToString('N'))
}
$evidence=[IO.Path]::GetFullPath($EvidenceDirectory).TrimEnd('\')
if ((Split-Path $evidence -Parent) -ne $fixtureRoot -or (Split-Path $evidence -Leaf) -cnotmatch '^api-logging-[0-9a-f]{32}$') { throw 'exact_evidence_scope_required' }
$null=Assert-ProbePath $evidence
if (-not $Apply) {
    if ($Worker -or [IO.File]::Exists($evidence) -or [IO.Directory]::Exists($evidence)) { throw 'unused_evidence_required' }
    [ordered]@{ Status='planned_only'; MsiExecuted=$false; Operation=$Operation; Version=$Version; ProductCode=$ExpectedProductCode; EvidenceDirectory=$evidence } | ConvertTo-Json
    return
}
if ([Security.Principal.WindowsIdentity]::GetCurrent().Name -match '\\CodexSandbox(?:Offline|Online)$') { throw 'normal_user_required' }
Confirm-Preflight $PublicImagePreflightPath
$source=Assert-ProbePath (Join-Path $PSScriptRoot 'MsiApiLoggingProbe.cs')
Add-Type -Path $source
$installRoot=Fixture-Path 'main\installed'
Confirm-RegisteredFixture $ExpectedProductCode $identity.Family.ProductName $installRoot $identity.ProductVersion
$installedImage=Join-Path $installRoot 'DesktopAICompanion.exe'
$null=Assert-ProbePath $installedImage
if ((Get-Item -LiteralPath $installedImage -Force).Length -gt 67108864 -or
    (Hash-File $installedImage) -cne $manifest.SyntheticCompanionSha256) { throw 'installed_synthetic_image_mismatch' }
$log=Join-Path $evidence 'maintenance.log'
$resultPath=Join-Path $evidence 'worker-result.json'
$reservationPath=Join-Path $evidence 'reservation.json'
if ($Worker) {
    $reservation=Read-ProbeJson $reservationPath
    if ($reservation.Tag -cne $tag -or $reservation.ProductCode -cne $ExpectedProductCode -or $reservation.Operation -cne $Operation -or $reservation.Version -ne $Version) { throw 'reservation_mismatch' }
    foreach ($path in @($log,$resultPath,(Join-Path $evidence 'worker-started.json'))) { $null=Assert-ProbePath $path; if ([IO.File]::Exists($path) -or [IO.Directory]::Exists($path)) { throw 'worker_reuse_refused' } }
    Write-ProbeNew (Join-Path $evidence 'worker-started.json') ([ordered]@{ Started=$true; MsiExecuted=$false })
    # Both logging and maintenance occur here, never in separate processes.
    $result=[InstallerLoggingProbe.Probe]::Run([InstallerLoggingProbe.NativeInstallerApi]::new(),$Operation,$ExpectedProductCode,$log)
    Write-ProbeNew $resultPath $result
    exit $(if ($result.Status -ceq 'operation_returned_success') { 0 } else { 1 })
}
if ([IO.File]::Exists($evidence) -or [IO.Directory]::Exists($evidence)) { throw 'unused_evidence_required' }
$null=[IO.Directory]::CreateDirectory($evidence)
Write-ProbeNew $reservationPath ([ordered]@{ Tag=$tag; ProductCode=$ExpectedProductCode; Version=$Version; Operation=$Operation; MsiExecuted=$false })
$arguments=@('-NoProfile','-File',$PSCommandPath,'-FixtureDirectory',$fixtureRoot,'-Version',[string]$Version,
    '-ExpectedProductCode',$ExpectedProductCode,'-Operation',$Operation,'-EvidenceDirectory',$evidence,
    '-PublicImagePreflightPath',[IO.Path]::GetFullPath($PublicImagePreflightPath),'-Apply','-Worker')
foreach ($value in $arguments) { if ($value -match '["\r\n]' -or $value.EndsWith('\')) { throw 'caller_argument_refused' } }
$caller=[Environment]::ProcessPath
if ([IO.Path]::GetFileName($caller) -ine 'pwsh.exe') { throw 'powershell7_caller_required' }
$state=New-ScopedMsiCallerState
$primary=$null; $exitCode=$null
try {
    $exitCode=Invoke-ScopedMsiCaller -State $state -TimeoutMilliseconds 180000 -StartCaller {
        Start-Process -FilePath $caller -ArgumentList (($arguments | ForEach-Object { '"'+$_+'"' }) -join ' ') -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $evidence 'caller.out.txt') -RedirectStandardError (Join-Path $evidence 'caller.err.txt')
    }
} catch { $primary=$_.Exception.GetType().Name }
$report=[ordered]@{ Status='FAIL'; CallerStateUncertain=$state.Uncertain; CallerPid=$state.CallerPid; CallerCreationFileTime=$state.CallerCreationFileTime;
    CallerExitCode=$exitCode; CallerFailureType=$primary; ApiResult=$null; LogReceipt=$null; RawCallbackMessagesRecorded=$false; ExistingLifecycleGateChanged=$false }
if (-not $state.Uncertain -and [IO.File]::Exists($resultPath)) {
    $report.ApiResult=Read-ProbeJson $resultPath
    $report.LogReceipt=Read-LogReceipt $log
    if ($exitCode -eq 0 -and $report.ApiResult.Status -ceq 'operation_returned_success' -and $report.ApiResult.OperationCompleted -and
        $report.ApiResult.KnownShutdownMarkerSeen -and -not $report.ApiResult.CallbackFailed -and -not $report.ApiResult.CallbackLimitReached -and
        $report.LogReceipt.Exists -and $report.LogReceipt.MarkerSeen) { $report.Status='PASS' }
}
Write-ProbeNew (Join-Path $evidence 'result.json') $report
$report | ConvertTo-Json -Depth 12
if ($report.Status -cne 'PASS') { exit 1 }
