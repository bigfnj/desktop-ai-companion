#requires -Version 7
# In-memory API facade and identity tests. Never run the probe entry point, native API or child process.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=Split-Path $PSScriptRoot -Parent
$source=Join-Path $repo 'tools\agentbroker\Test-IsolatedMsiApiLogging.ps1'
$tokens=$null; $errors=$null
$syntax=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw 'Test-IsolatedMsiApiLogging.ps1: probe parse failed' }
foreach($functionName in @('Confirm-ProbeIdentity','Confirm-ExactFixtureVersion','Confirm-RegisteredMetadata','Import-FixtureMetadataFunctions')) {
    $definition=$syntax.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $functionName},$true)
    . ([scriptblock]::Create($definition.Extent.Text))
}
$fakeSource=@'
public sealed class FakeInstallerLoggingApi : InstallerLoggingProbe.IInstallerApi
{
    public readonly List<string> Calls = new List<string>();
    public uint UiPrevious = 5, HandlerCode, EnableCode, DisableCode, RestoreCode, MaintainCode;
    public bool FailUiRestore, Overflow, NoMarker, FormatFailureAfterMarker;
    public string ThrowAt;
    public uint SeenMask, SeenAttributes;
    public string SeenOperation, SeenProduct;
    Action<uint,string> callback;
    void MaybeThrow(string where) { if(ThrowAt==where) throw new InvalidOperationException("SYNTHETIC_PRIVATE_ERROR"); }
    public uint SetInternalUi(uint level)
    {
        Calls.Add("ui:"+level); MaybeThrow(level==2?"ui":"restore-ui");
        return level==2?UiPrevious:FailUiRestore?0u:2u;
    }
    public uint SetRecordHandler(Action<uint,string> value)
    {
        Calls.Add("handler"); MaybeThrow("handler"); callback=value; return HandlerCode;
    }
    public uint RestoreRecordHandler() { Calls.Add("restore-handler"); MaybeThrow("restore-handler"); return RestoreCode; }
    public uint EnableLog(uint mode,string path,uint attributes)
    {
        Calls.Add(mode==0?"disable-log":"enable-log"); MaybeThrow(mode==0?"disable-log":"enable-log");
        if(mode==0) return DisableCode;
        SeenMask=mode; SeenAttributes=attributes; return EnableCode;
    }
    public uint Maintain(string operation,string product)
    {
        Calls.Add("maintain"); SeenOperation=operation; SeenProduct=product; MaybeThrow("maintain");
        if(!NoMarker) callback(0x04000000,InstallerLoggingProbe.Probe.Marker);
        if(FormatFailureAfterMarker) callback(InstallerLoggingProbe.Probe.CallbackFailure,null);
        callback(0x04000000,"SYNTHETIC_PRIVATE_VALUE C:\\Synthetic\\path token=not-for-output");
        callback(0x08000000,"unretained-action"); callback(0x01000000,null);
        if(Overflow) for(int i=0;i<100001;i++) callback(0x99000000,"not-retained");
        return MaintainCode;
    }
}
'@
Add-Type -TypeDefinition ([IO.File]::ReadAllText((Join-Path $repo 'tools\agentbroker\MsiApiLoggingProbe.cs')) + "`n" + $fakeSource)
$checks=0
function Check([bool]$Value,[string]$Label) { if(-not $Value){throw ('Test-IsolatedMsiApiLogging.ps1: '+$Label)}; $script:checks++ }
function Reject([scriptblock]$Action,[string]$Label) { $failed=$false; try{$null=&$Action}catch{$failed=$true}; Check $failed $Label }
function Run-Fake($Api) { return [InstallerLoggingProbe.Probe]::Run($Api,'Uninstall','{11111111-1111-4111-8111-111111111111}','C:\Synthetic\probe.log') }
$fake=[FakeInstallerLoggingApi]::new(); $result=Run-Fake $fake
Check ($result.Status -ceq 'operation_returned_success' -and $result.OperationCompleted -and $result.OperationCode -eq 0) 'same fake API completes exactly one uninstall'
Check (($fake.Calls -join ',') -ceq 'ui:2,handler,enable-log,maintain,disable-log,restore-handler,ui:5') 'logging setup precedes maintenance and all owned state restores afterward'
Check ($fake.SeenMask -eq 0x1FDF -and ($fake.SeenMask -band 0x4000) -eq 0) 'explicit named logging flags exclude LOGONLYONERROR'
Check ($fake.SeenAttributes -eq 2) 'FLUSHEACHLINE is an attribute rather than a log-mode bit'
Check ($result.LogMode -eq 0x1FDF -and $result.LogAttributes -eq 2 -and -not $result.LogOnlyOnErrorIncluded) 'receipt records exact logging settings'
Check ($fake.SeenOperation -ceq 'Uninstall' -and $fake.SeenProduct -ceq '{11111111-1111-4111-8111-111111111111}') 'exact operation and product passed to same API facade'
Check ($result.KnownShutdownMarkerSeen -and $result.MessageCounts.Info -eq 2 -and $result.MessageCounts.ActionStart -eq 1) 'known marker and finite callback categories are observed'
Check (-not $result.RawMessagesRecorded -and (ConvertTo-Json $result -Depth 8) -notmatch 'SYNTHETIC_PRIVATE|not-for-output|unretained-action|Synthetic') 'arbitrary callback data and paths never enter result'
Check ($result.DisableLogCode -eq 0 -and $result.RestoreHandlerCode -eq 0 -and $result.RestoreInternalUiSucceeded) 'logging, handler and internal UI restoration are checked'
foreach($phase in @('ui','handler','enable-log','maintain')) {
    $fake=[FakeInstallerLoggingApi]::new(); $fake.ThrowAt=$phase; $result=Run-Fake $fake
    Check ($result.ApiException -and $result.Status -ceq 'api_exception') ('setup/operation exception is explicit: '+$phase)
    Check (($phase -eq 'maintain') -eq $result.OperationAttempted) ('no operation after earlier setup exception: '+$phase)
    Check ((ConvertTo-Json $result -Depth 8) -notmatch 'SYNTHETIC_PRIVATE') ('exception details stay private: '+$phase)
}
foreach($phase in @('disable-log','restore-handler','restore-ui')) {
    $fake=[FakeInstallerLoggingApi]::new(); $fake.ThrowAt=$phase; $result=Run-Fake $fake
    Check ($result.Status -ceq 'restoration_failed' -and $result.RestorationException) ('restoration exception fails result: '+$phase)
    Check ($fake.Calls.Contains('restore-handler') -and $fake.Calls.Contains('ui:5')) ('remaining restoration attempted after error: '+$phase)
}
$fake=[FakeInstallerLoggingApi]::new(); $fake.UiPrevious=0; $result=Run-Fake $fake
Check ($result.Status -ceq 'ui_setup_failed' -and -not $result.OperationAttempted) 'invalid internal UI setup refuses before operation'
$fake=[FakeInstallerLoggingApi]::new(); $fake.HandlerCode=120; $result=Run-Fake $fake
Check ($result.Status -ceq 'handler_setup_failed' -and -not $result.OperationAttempted -and $result.RestoreInternalUiSucceeded) 'handler setup failure refuses operation and restores UI'
$fake=[FakeInstallerLoggingApi]::new(); $fake.EnableCode=87; $result=Run-Fake $fake
Check ($result.Status -ceq 'logging_setup_failed' -and -not $result.OperationAttempted -and $result.RestoreHandlerCode -eq 0) 'MsiEnableLog nonzero refuses operation and restores handler'
foreach($field in @('DisableCode','RestoreCode','FailUiRestore')) {
    $fake=[FakeInstallerLoggingApi]::new(); if($field -ceq 'FailUiRestore'){$fake.$field=$true}else{$fake.$field=87}; $result=Run-Fake $fake
    Check ($result.Status -ceq 'restoration_failed') ('nonzero restoration cannot pass: '+$field)
}
$fake=[FakeInstallerLoggingApi]::new(); $fake.MaintainCode=1603; $result=Run-Fake $fake
Check ($result.Status -ceq 'operation_returned_error' -and $result.OperationCode -eq 1603) 'operation return failure remains a failure'
$fake=[FakeInstallerLoggingApi]::new(); $fake.NoMarker=$true; $result=Run-Fake $fake
Check (-not $result.KnownShutdownMarkerSeen) 'arbitrary INFO never substitutes for exact known shutdown marker'
$fake=[FakeInstallerLoggingApi]::new(); $fake.FormatFailureAfterMarker=$true; $result=Run-Fake $fake
Check ($result.KnownShutdownMarkerSeen -and $result.CallbackFailed -and $result.Status -ceq 'callback_failed') 'native formatting failure cannot pass after an earlier valid marker'
Check ((ConvertTo-Json $result -Depth 8) -notmatch 'SYNTHETIC_PRIVATE') 'callback failure exports no native record or exception text'
$fake=[FakeInstallerLoggingApi]::new(); $fake.Overflow=$true; $result=Run-Fake $fake
Check ($result.CallbackLimitReached -and ($result.MessageCounts.Values | Measure-Object -Sum).Sum -eq 100000) 'callback counts are bounded and truncation explicit'
Reject { [InstallerLoggingProbe.Probe]::Run([FakeInstallerLoggingApi]::new(),'Repair','synthetic','synthetic') } 'unsupported maintenance operation refuses before API dispatch'
$tag='a'*32; $product='{11111111-1111-4111-8111-111111111111}'
$family=[pscustomobject]@{Name='main';UpgradeCode='{22222222-2222-4222-8222-222222222222}';ProductName=('Desktop AI Companion Scoped Shutdown Test '+$tag+' main');RegistryRoot=('Software\bigfnj\DACScopedShutdownTest\'+$tag+'\main');Packages=@([pscustomobject]@{Version=2;ProductCode=$product;Capability='1'})}
$manifest=[pscustomobject]@{SchemaVersion=1;Tag=$tag;Families=@($family)}
$accepted=Confirm-ProbeIdentity $manifest $tag 2 $product
Check ($accepted.Package.ProductCode -ceq $product -and $accepted.ProductVersion -ceq '0.0.2') 'actual identity predicate binds exact selected fixture version'
Confirm-ExactFixtureVersion '0.0.2' $accepted.ProductVersion
Check $true 'actual shared MSI version predicate accepts exact selected package version'
Reject { Confirm-ExactFixtureVersion '0.0.1' $accepted.ProductVersion } 'actual shared MSI version predicate refuses mismatching package version'
Confirm-RegisteredMetadata 'C:\Synthetic\installed' 'Synthetic fixture' '0.0.2' 'C:\Synthetic\installed' 'Synthetic fixture' $accepted.ProductVersion
Check $true 'actual registered metadata predicate accepts exact version and location'
Reject { Confirm-RegisteredMetadata 'C:\Synthetic\installed' 'Synthetic fixture' '0.0.1' 'C:\Synthetic\installed' 'Synthetic fixture' $accepted.ProductVersion } 'wrong registered version refuses before API operation'
Reject { Confirm-RegisteredMetadata 'C:\Synthetic\elsewhere' 'Synthetic fixture' '0.0.2' 'C:\Synthetic\installed' 'Synthetic fixture' $accepted.ProductVersion } 'wrong registered install location refuses'
Reject { Confirm-RegisteredMetadata 'C:\Synthetic\installed' 'Another fixture' '0.0.2' 'C:\Synthetic\installed' 'Synthetic fixture' $accepted.ProductVersion } 'wrong registered product name refuses'
Reject { Confirm-ProbeIdentity $manifest $tag 2 '{33333333-3333-4333-8333-333333333333}' } 'explicit expected product mismatch refuses'
$family.UpgradeCode='{327AF68D-A714-5C9B-B2D9-9C3404E03D38}'; Reject { Confirm-ProbeIdentity $manifest $tag 2 $product } 'production upgrade family refuses'
$family.UpgradeCode='{22222222-2222-4222-8222-222222222222}'; $family.Packages[0].Capability='0'; Reject { Confirm-ProbeIdentity $manifest $tag 2 $product } 'legacy capability refuses'
$family.Packages[0].Capability='1'; $family.RegistryRoot='Software\foreign'; Reject { Confirm-ProbeIdentity $manifest $tag 2 $product } 'foreign registry family refuses'
& {
    # Execute the actual whitelist importer. Stub only its lexical path classifier and
    # the eventual metadata reader, so no MSI database, registry or live path is opened.
    function Assert-ProbePath([string]$Path) { return $Path }
    Import-FixtureMetadataFunctions
    $script:fakeMetadataCalls=0
    function Msi-Rows([string]$Path,[string]$Query) {
        $script:fakeMetadataCalls++
        $rows=[Collections.Generic.List[object]]::new()
        $rows.Add([string[]]@('0.0.2'))
        return ,$rows.ToArray()
    }
    Check ((Msi-Property 'C:\Synthetic\fixture.msi' 'ProductVersion') -ceq '0.0.2' -and $script:fakeMetadataCalls -eq 1) 'actual imported metadata function resolves harmless fake reader in correct scope'
}
Write-Output ('PASS: '+$checks+' in-memory MSI API logging checks; no MSI, registry, process, GUI, configuration or protected fixture access.')
