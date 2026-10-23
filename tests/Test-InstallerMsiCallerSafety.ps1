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
$scratch = Join-Path $repo ('build\msi-caller-safety\' + [Guid]::NewGuid().ToString('N'))
[void](Assert-ScopedShutdownPath -Path $scratch -TrustedRoot $repo)
[void][IO.Directory]::CreateDirectory($scratch)
$report = [ordered]@{ Result='PASS'; Checks=$checks; Cases=@($cases.ToArray()); MsiExecuted=$false; ProcessLaunched=$false; ActualDispatchAndCleanup=$true }
[IO.File]::WriteAllText((Join-Path $scratch 'result.json'), ($report | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
$report | ConvertTo-Json -Compress
Write-Output ('Evidence=' + $scratch)
