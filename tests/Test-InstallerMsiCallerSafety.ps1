#requires -Version 5
<#
.SYNOPSIS
Exercise the actual isolated-MSI dispatch and cleanup with fake callers only.
.EXAMPLE
pwsh -NoProfile -File tests/Test-InstallerMsiCallerSafety.ps1
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repo 'installer\ScopedShutdown\PathSafety.ps1')
. (Join-Path $PSScriptRoot 'InstallerShutdown.Tests\MsiCallerSafety.ps1')
$harness = Join-Path $PSScriptRoot 'Test-IsolatedMsiLifecycle.ps1'
[void](Assert-ScopedShutdownPath -Path $harness -TrustedRoot $repo)
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($harness, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'The actual lifecycle harness must parse before fake-caller tests.' }
$dispatch = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-OwnedMsi'
}, $true))
$cleanup = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.TryStatementAst] -and $null -ne $node.Finally -and
    $node.Finally.Extent.Text.Contains('$cleanup = New-Object') -and $node.Finally.Extent.Text.Contains('$manifest.Families')
}, $true))
if ($dispatch.Count -ne 1 -or $cleanup.Count -ne 1) { throw 'Actual lifecycle dispatch/cleanup could not be identified uniquely.' }
. ([scriptblock]::Create($dispatch[0].Extent.Text))
$cleanupText = $cleanup[0].Finally.Extent.Text
$actualCleanup = [scriptblock]::Create($cleanupText.Substring(1, $cleanupText.Length - 2))
$checks = 0
$cases = New-Object 'Collections.Generic.List[string]'
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw ('Test-InstallerMsiCallerSafety.ps1: ' + $Message) }
    $script:checks++
}

# All effectful dependencies of the extracted real function are fake. No OS caller
# is created, and the real script's package validation/install flow is never run.
function Fixture-Path([string]$Relative) { return 'C:\owned-fake\' + $Relative }
function Common-Properties($Family) { return @('CLEANINSTALL=') }
function Write-Utf8([string]$Path, [string]$Text) { $script:writeCalls++ }
function Read-Utf8([string]$Path) { return 'Installation-scoped companion shutdown: targets=1, graceful=1, terminated=0' }
function Assert-Check([bool]$Condition, [string]$Description) { if (-not $Condition) { throw $Description } }
function Product-State([string]$Code) { $script:stateCalls++; return 5 }
function Start-Process {
    param($FilePath, $ArgumentList, $WindowStyle, [switch]$PassThru, $RedirectStandardOutput, $RedirectStandardError)
    $script:startCalls++
    if ($script:failure -eq 'launch') { throw 'injected_launch' }
    return $script:fake
}
function New-FakeCaller([string]$Failure, [int]$ExitCode = 0, [bool]$WaitResult = $true) {
    $value = [pscustomobject]@{
        Failure = $Failure; ExitValue = $ExitCode; WaitResult = $WaitResult
        WaitCalls = 0; WaitMilliseconds = 0; DisposeCalls = 0; KillCalls = 0
    }
    $value | Add-Member ScriptProperty Id {
        if ($this.Failure -eq 'process_id') { throw 'injected_process_id' }
        return 4321
    }
    $value | Add-Member ScriptProperty SafeHandle {
        if ($this.Failure -eq 'retain_handle') { throw 'injected_retain_handle' }
        return 'fake-retained-handle'
    }
    $value | Add-Member ScriptProperty StartTime {
        if ($this.Failure -eq 'creation_time') { throw 'injected_creation_time' }
        return [DateTime]::SpecifyKind([DateTime]'2026-01-02T03:04:05', [DateTimeKind]::Utc)
    }
    $value | Add-Member ScriptProperty ExitCode {
        if ($this.Failure -eq 'exit_code') { throw 'injected_exit_code' }
        return $this.ExitValue
    }
    $value | Add-Member ScriptMethod WaitForExit {
        param([int]$Milliseconds)
        $this.WaitCalls++; $this.WaitMilliseconds = $Milliseconds
        if ($this.Failure -in @('wait', 'wait-dispose')) { throw 'injected_wait' }
        return $this.WaitResult
    }
    $value | Add-Member ScriptMethod Dispose {
        $this.DisposeCalls++
        if ($this.Failure -in @('dispose', 'wait-dispose')) { throw 'injected_dispose' }
    }
    $value | Add-Member ScriptMethod Kill { $this.KillCalls++; throw 'Fake caller must never be terminated.' }
    return $value
}
function Reset-Case([string]$Failure, [int]$ExitCode = 0, [bool]$WaitResult = $true) {
    $script:failure = $Failure
    $script:fake = New-FakeCaller $Failure $ExitCode $WaitResult
    $script:installerState = New-ScopedMsiCallerState
    $script:operations = New-Object 'Collections.Generic.List[object]'
    $script:children = New-Object 'Collections.Generic.List[object]'
    $script:startCalls = 0; $script:stateCalls = 0; $script:writeCalls = 0
    $script:family = [pscustomobject]@{ Name = 'main'; Packages = @([pscustomobject]@{ Version = 1; ProductCode = '{FAKE}' }) }
    $script:manifest = [pscustomobject]@{ Families = @($script:family) }
    $script:primary = $null
}
foreach ($scenario in @(
    @{ Name='launch'; Reason='launch'; Pid=0; Created=$false; Wait=$true },
    @{ Name='process_id'; Reason='process_id'; Pid=0; Created=$false; Wait=$true },
    @{ Name='retain_handle'; Reason='retain_handle'; Pid=4321; Created=$false; Wait=$true },
    @{ Name='creation_time'; Reason='creation_time'; Pid=4321; Created=$false; Wait=$true },
    @{ Name='wait'; Reason='wait'; Pid=4321; Created=$true; Wait=$true },
    @{ Name='wait-dispose'; Reason='wait'; Pid=4321; Created=$true; Wait=$true },
    @{ Name='timeout'; Reason='timeout'; Pid=4321; Created=$true; Wait=$false }
)) {
    Reset-Case $scenario.Name 0 $scenario.Wait
    $message = ''
    try { Invoke-OwnedMsi -Name 'fake-operation' -Action @('/i', 'C:\owned-fake\fixture.msi') -Family $family }
    catch { $message = $_.Exception.Message; $script:primary = $_ }
    Check ($message.Length -gt 0) ($scenario.Name + ': the injected caller failure propagates')
    Check ($installerState.Uncertain -and $installerState.Reason -ceq $scenario.Reason) ($scenario.Name + ': unconfirmed exit stays uncertain with its exact phase')
    Check ($installerState.CallerPid -eq $scenario.Pid) ($scenario.Name + ': only the known caller PID is retained')
    Check (($null -ne $installerState.CallerCreationFileTime) -eq $scenario.Created) ($scenario.Name + ': creation metadata is retained only when obtained')
    if ($scenario.Created) {
        $expectedCreated = ([DateTime]::SpecifyKind([DateTime]'2026-01-02T03:04:05', [DateTimeKind]::Utc)).ToFileTimeUtc()
        Check ($installerState.CallerCreationFileTime -eq $expectedCreated) ($scenario.Name + ': retained creation time is exact')
    }
    Check ($operations.Count -eq 1 -and $operations[0].Reason -ceq $scenario.Reason) ($scenario.Name + ': actual dispatch records uncertainty')
    Check ($fake.DisposeCalls -eq $(if ($scenario.Name -eq 'launch') { 0 } else { 1 })) ($scenario.Name + ': every returned caller is disposed once')
    Check ($fake.KillCalls -eq 0) ($scenario.Name + ': caller is never terminated')
    if ($scenario.Name -eq 'wait-dispose') { Check ($message -match 'injected_wait') 'disposal failure preserves the primary wait failure' }
    $writesBefore = $writeCalls
    try { Invoke-OwnedMsi -Name 'forbidden-followup' -Action @('/x', 'C:\owned-fake\fixture.msi') -Family $family }
    catch { }
    Check ($startCalls -eq 1 -and $writeCalls -eq $writesBefore) ($scenario.Name + ': actual dispatch refuses subsequent MSI before effects')
    $priorWarning = $WarningPreference
    try { $WarningPreference = 'SilentlyContinue'; . $actualCleanup }
    finally { $WarningPreference = $priorWarning }
    Check ($startCalls -eq 1 -and $stateCalls -eq 0) ($scenario.Name + ': actual outer cleanup skips every MSI after uncertain exit')
    $cases.Add($scenario.Name)
}
foreach ($scenario in @('success', 'nonzero', 'exit_code', 'dispose')) {
    $exit = if ($scenario -eq 'nonzero') { 1603 } else { 0 }
    Reset-Case $scenario $exit
    $message = ''
    try { Invoke-OwnedMsi -Name 'confirmed-exit' -Action @('/i', 'C:\owned-fake\fixture.msi') -Family $family -ExpectedExit $exit -ShutdownReceipt }
    catch { $message = $_.Exception.Message; $script:primary = $_ }
    Check (-not $installerState.Uncertain -and $null -eq $installerState.Reason) ($scenario + ': observed exit clears uncertainty')
    Check ($fake.WaitCalls -eq 1 -and $fake.WaitMilliseconds -eq 180000 -and $fake.DisposeCalls -eq 1) ($scenario + ': finite wait and disposal are preserved')
    Check (($message.Length -gt 0) -eq ($scenario -in @('exit_code', 'dispose'))) ($scenario + ': post-exit accessor errors propagate without inventing a running caller')
    $script:failure = ''; $script:fake = New-FakeCaller ''
    . $actualCleanup
    Check ($startCalls -eq 2 -and $stateCalls -eq 1) ($scenario + ': actual outer cleanup may run after confirmed exit')
    Check (-not $installerState.Uncertain -and $fake.KillCalls -eq 0) ($scenario + ': cleanup remains a confirmed fake caller without termination')
    $cases.Add($scenario)
}

# DEFAULT-PATH PROBE THROUGH REPARSE POINTS (N-measure-offline-03). The actual
# Assert-NoDefaultFixtureArtifacts walks a FAKE file system here: every attribute, tag and
# existence read is a fake keyed by path, so no real user folder is read. A OneDrive
# Desktop is a cloud placeholder (0x9000x01A), which used to leave the fixture shortcut's
# absence UNPROVEN; links (name surrogates) and unknown tags must stay refused.
$probe = @{}
foreach ($name in @('Assert-NoDefaultFixtureArtifacts', 'Test-FixtureCloudPlaceholderTag', 'Get-FixtureReparseTag',
    'Test-FixturePathPresent')) {
    $found = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true))
    if ($found.Count -ne 1) { throw "Actual lifecycle function was not identified uniquely: $name" }
    $probe[$name] = $found[0]
}
$walkText = $probe['Assert-NoDefaultFixtureArtifacts'].Extent.Text
Check ($walkText.Contains('Test-FixturePathPresent $full') -and $walkText.Contains('Get-FixtureReparseTag $current')) 'WITNESS the default-path walk reads through its injectable metadata helpers'
Check ($walkText -cnotmatch 'Get-ChildItem|Enumerate|GetFiles|GetDirectories|GetFileSystemEntries|\[IO\.(File|Directory)\]::') 'the default-path walk never lists a folder or reads the file system around its helpers'
foreach ($name in @('Assert-NoDefaultFixtureArtifacts', 'Test-FixtureCloudPlaceholderTag')) {
    . ([scriptblock]::Create($probe[$name].Extent.Text))
}
$realTagReader = $probe['Get-FixtureReparseTag'].Body.GetScriptBlock()
$realPresence = $probe['Test-FixturePathPresent'].Body.GetScriptBlock()
function Tag([string]$Hex) { return [Convert]::ToUInt32($Hex, 16) }
function Get-FixturePathAttributes([string]$Path) {
    if ($script:fakeAttributes.ContainsKey($Path)) { return $script:fakeAttributes[$Path] }
    return $null
}
function Get-FixtureReparseTag([string]$Path) {
    if (-not $script:fakeTags.ContainsKey($Path)) { throw 'Fake tag read for an entry that is not a reparse point.' }
    return [uint32]$script:fakeTags[$Path]
}
function Test-FixturePathPresent([string]$Path) { $script:presenceAsked.Add($Path); return $script:fakeAttributes.ContainsKey($Path) }
$tag = '0123456789abcdef0123456789abcdef'
$probeFamily = [pscustomobject]@{ Name = 'main'; ProductName = 'Desktop AI Companion Scoped Shutdown Test ' + $tag + ' main' }
$desktop = [Environment]::GetFolderPath('DesktopDirectory')
$localData = [Environment]::GetFolderPath('LocalApplicationData')
if ([string]::IsNullOrEmpty($desktop) -or [string]::IsNullOrEmpty($localData)) { throw 'The default-path probe needs a user profile with Desktop and local application data folders.' }
$desktop = [IO.Path]::GetFullPath($desktop); $localData = [IO.Path]::GetFullPath($localData)
$desktopShortcut = Join-Path $desktop ($probeFamily.ProductName + '.lnk')
function Reset-Probe([string]$Folder, [uint32]$FolderTag, [switch]$ShortcutPresent) {
    $script:fakeAttributes = @{}; $script:fakeTags = @{}
    $script:presenceAsked = New-Object 'Collections.Generic.List[string]'
    $script:unprovenChecks = New-Object 'Collections.Generic.List[string]'
    $script:cloudPlaceholderWalks = New-Object 'Collections.Generic.List[string]'
    # Every ancestor of the folder is an ordinary directory; the folder carries the tag.
    $current = [IO.Path]::GetPathRoot($Folder)
    foreach ($part in $Folder.Substring($current.Length).Split('\')) {
        $current = Join-Path $current $part
        $script:fakeAttributes[$current] = [IO.FileAttributes]::Directory
    }
    $script:fakeAttributes[$Folder] = [IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint
    $script:fakeTags[$Folder] = $FolderTag
    if ($ShortcutPresent) { $script:fakeAttributes[$desktopShortcut] = [IO.FileAttributes]::Archive }
}
function Invoke-Probe {
    try { Assert-NoDefaultFixtureArtifacts $probeFamily; return '' } catch { return $_.Exception.Message }
}
foreach ($cloud in @('9000001A', '9000701A', '9000601A')) {
    Reset-Probe $desktop (Tag $cloud)
    $message = Invoke-Probe
    Check ($message.Length -eq 0 -and $unprovenChecks.Count -eq 0) ("cloud placeholder: the Desktop shortcut absence is proven, not left unproven (0x$cloud)")
    Check ($presenceAsked.Contains($desktopShortcut) -and $cloudPlaceholderWalks.Contains('main-desktop-shortcut-through-cloud-placeholder')) (
        "cloud placeholder: the walk continues below it to an existence check of the exact shortcut (0x$cloud)")
}
Reset-Probe $desktop (Tag '9000701A') -ShortcutPresent
Check ((Invoke-Probe) -ceq 'exact GUID-named default user path contains no fixture artifact') 'a fixture shortcut present below a cloud placeholder fails the absence check'
foreach ($refused in @('A0000003', 'A000000C', 'B000701A', '2000ABCD', '80000013', '00000000')) {
    Reset-Probe $desktop (Tag $refused)
    $message = Invoke-Probe
    Check ($message.Length -eq 0 -and $unprovenChecks.Contains('main-desktop-shortcut-absence-unproven-reparse-refused') -and $cloudPlaceholderWalks.Count -eq 0) (
        "a link or unknown reparse point leaves the Desktop shortcut absence unproven (0x$refused)")
    Check (-not $presenceAsked.Contains($desktopShortcut)) ("nothing below a refused reparse point is probed (0x$refused)")
}
Reset-Probe $localData (Tag 'A0000003')
Check ((Invoke-Probe) -ceq 'fixture_default_reparse_refused') 'a linked application-data folder still refuses the non-shortcut probe'
Reset-Probe $localData (Tag '9000001A')
Check ((Invoke-Probe).Length -eq 0 -and $cloudPlaceholderWalks.Contains('main-appdata-directory-through-cloud-placeholder')) 'a cloud placeholder application-data folder is walked, not refused'
$cases.Add('default-path-reparse')

# The REAL tag reader and existence probe, on entries this test owns: a junction it
# creates (no privilege needed) and an ordinary folder and file beside it.
$reparseScratch = Join-Path $repo ('build\msi-caller-safety\' + [Guid]::NewGuid().ToString('N') + '-reparse')
[void](Assert-ScopedShutdownPath -Path $reparseScratch -TrustedRoot $repo)
$reparseTarget = Join-Path $reparseScratch 'target'
$reparseJunction = Join-Path $reparseScratch 'junction'
$reparseFile = Join-Path $reparseScratch 'plain.txt'
[void][IO.Directory]::CreateDirectory($reparseTarget)
[IO.File]::WriteAllText($reparseFile, 'plain')
try {
    [void](New-Item -ItemType Junction -Path $reparseJunction -Value $reparseTarget)
    $junctionTag = & $realTagReader -Path $reparseJunction
    Check ($junctionTag -eq (Tag 'A0000003')) 'the real FindFirstFileExW reader reports a junction tag without following it'
    Check (-not (Test-FixtureCloudPlaceholderTag $junctionTag)) 'the real junction tag is not walked as a cloud placeholder'
    Check ((& $realTagReader -Path $reparseTarget) -eq 0) 'WITNESS the real reader reports no tag for an ordinary folder'
    Check ((& $realPresence -Path $reparseFile) -and (& $realPresence -Path $reparseTarget) -and
        -not (& $realPresence -Path (Join-Path $reparseScratch 'absent.lnk'))) 'the real existence probe sees a present file and folder and an absent name'
} finally {
    if ([IO.Directory]::Exists($reparseJunction)) { [IO.Directory]::Delete($reparseJunction) }
    if ([IO.File]::Exists($reparseFile)) { [IO.File]::Delete($reparseFile) }
    if ([IO.Directory]::Exists($reparseTarget)) { [IO.Directory]::Delete($reparseTarget) }
    if ([IO.Directory]::Exists($reparseScratch)) { [IO.Directory]::Delete($reparseScratch) }
}
$cases.Add('real-reparse-reader')

$scratch = Join-Path $repo ('build\msi-caller-safety\' + [Guid]::NewGuid().ToString('N'))
[void](Assert-ScopedShutdownPath -Path $scratch -TrustedRoot $repo)
[void][IO.Directory]::CreateDirectory($scratch)
$report = [ordered]@{ Result='PASS'; Checks=$checks; Cases=@($cases.ToArray()); MsiExecuted=$false; ProcessLaunched=$false; ActualDispatchAndCleanup=$true }
[IO.File]::WriteAllText((Join-Path $scratch 'result.json'), ($report | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
$report | ConvertTo-Json -Compress
Write-Output ('Evidence=' + $scratch)
