#requires -Version 5
<#
.SYNOPSIS
Check bounded native-checker execution and the actual wrapper's child cleanup.
.EXAMPLE
pwsh -NoProfile -File tests/Test-InstallerNativeCheckTimeout.ps1
#>
[CmdletBinding()]
param([ValidateSet('none', 'hang')][string]$CheckerFixture = 'none')
if ($CheckerFixture -eq 'hang') {
    [Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
    [Console]::WriteLine('PASS 52 checks')
    [Console]::WriteLine('RESULT:PASS')
    [Console]::Error.WriteLine('owned synthetic checker is intentionally waiting')
    Start-Sleep -Seconds 60
    return
}
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repo 'installer\ScopedShutdown\PathSafety.ps1')
. (Join-Path $PSScriptRoot 'InstallerShutdown.Tests\NativeCheckRunner.ps1')
$script:runner = ${function:Invoke-ScopedNativeCheck}
$scratch = Join-Path $repo ('build\native-checker-timeout\' + [Guid]::NewGuid().ToString('N'))
[void](Assert-ScopedShutdownPath -Path $scratch -TrustedRoot $repo)
[void][IO.Directory]::CreateDirectory($scratch)
$assertions = 0
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw ('Test-InstallerNativeCheckTimeout.ps1: ' + $Message) }
    $script:assertions++
}
function New-FakeReader([string]$Text, [switch]$NeverCompletes) {
    $completion = New-Object 'Threading.Tasks.TaskCompletionSource[string]'
    if (-not $NeverCompletes) { $completion.SetResult($Text) }
    $reader = [pscustomobject]@{ Completion = $completion; Started = $false; Disposed = 0 }
    $reader | Add-Member ScriptMethod ReadToEndAsync { $this.Started = $true; return $this.Completion.Task }
    $reader | Add-Member ScriptMethod Dispose { $this.Disposed++; [void]$this.Completion.TrySetCanceled() }
    return $reader
}
function New-FakeChecker([string]$Mode) {
    $out = New-FakeReader "PASS 52 checks`r`nRESULT:PASS`r`nUnicode: $([char]0x2603)"
    $err = New-FakeReader 'stderr was drained concurrently' -NeverCompletes:($Mode -eq 'drain')
    $checker = [pscustomobject]@{
        Mode=$Mode; SafeHandle='owned-fake-handle'; Id=4321; ExitCode=0
        StandardOutput=$out; StandardError=$err; WaitCalls=0; KillCalls=0; DisposeCalls=0
        WaitBounds=(New-Object 'Collections.Generic.List[int]')
    }
    $checker | Add-Member ScriptMethod WaitForExit {
        param([int]$Milliseconds)
        if ($Milliseconds -le 0) { throw 'Fake checker refuses an unbounded wait.' }
        if (-not $this.StandardOutput.Started -or -not $this.StandardError.Started) { throw 'Both output drains must start before waiting.' }
        $this.WaitCalls++; $this.WaitBounds.Add($Milliseconds)
        if ($this.Mode -eq 'wait' -and $this.WaitCalls -eq 1) { throw 'injected_wait' }
        if ($this.Mode -eq 'cleanup') { return $false }
        if ($this.Mode -eq 'timeout' -and $this.WaitCalls -eq 1) { return $false }
        return $true
    }
    $checker | Add-Member ScriptMethod Kill { $this.KillCalls++ }
    $checker | Add-Member ScriptMethod Dispose { $this.DisposeCalls++ }
    return $checker
}
foreach ($mode in @('success', 'timeout', 'wait', 'cleanup', 'drain')) {
    $script:fakeChecker = New-FakeChecker $mode
    $caught = ''; $result = $null
    try {
        $result = & $runner -FilePath 'owned-fake.exe' -TimeoutMilliseconds 20 -CleanupMilliseconds 20 -DrainMilliseconds 20 -StartCaller {
            param($Info)
            $script:launchInfo = $Info
            return $script:fakeChecker
        }
    } catch { $caught = $_.Exception.Message }
    Check (-not $launchInfo.UseShellExecute -and $launchInfo.CreateNoWindow) ($mode + ': checker uses direct hidden child execution')
    Check ($launchInfo.RedirectStandardOutput -and $launchInfo.RedirectStandardError -and
        $launchInfo.StandardOutputEncoding.CodePage -eq 65001 -and $launchInfo.StandardErrorEncoding.CodePage -eq 65001) ($mode + ': both streams retain explicit UTF8 decoding')
    Check ($fakeChecker.DisposeCalls -eq 1 -and $fakeChecker.StandardOutput.Disposed -eq 1 -and
        $fakeChecker.StandardError.Disposed -eq 1) ($mode + ': caller and both readers are always disposed')
    Check (@($fakeChecker.WaitBounds | Where-Object { $_ -le 0 -or $_ -gt 20 }).Count -eq 0) ($mode + ': every process wait is bounded')
    if ($mode -in @('success', 'timeout')) {
        Check ($caught.Length -eq 0 -and $result.ExitObserved) ($mode + ': runner confirms caller exit')
        Check ($result.TimedOut -eq ($mode -eq 'timeout')) ($mode + ': timeout cannot be mistaken for marker success')
        Check ($result.Lines -contains ('Unicode: ' + [char]0x2603) -and $result.Lines -contains 'stderr was drained concurrently') ($mode + ': both stream contents are preserved')
    } else { Check ($caught.Length -gt 0) ($mode + ': bounded failure propagates') }
    Check ($fakeChecker.KillCalls -eq $(if ($mode -in @('timeout', 'wait', 'cleanup')) { 1 } else { 0 })) ($mode + ': termination affects only the retained checker and only when needed')
}

# Execute the real wrapper try/catch/finally. Only the checker itself is replaced;
# its fake/real timeout reaches the unchanged owned-synthetic-child cleanup.
$source = Join-Path $PSScriptRoot 'Test-InstallerShutdown.ps1'
[void](Assert-ScopedShutdownPath -Path $source -TrustedRoot $repo)
$tokens=$null; $errors=$null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'Actual native wrapper must parse.' }
$functions = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-NativeCheck'
}, $true))
$flows = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.TryStatementAst] -and $null -ne $node.Finally -and
    $node.Body.Extent.Text.Contains("Invoke-NativeCheck -Name 'unit'") -and $node.Finally.Extent.Text.Contains('$cleanupFailures')
}, $true))
if ($functions.Count -ne 1 -or $flows.Count -ne 1) { throw 'Actual native wrapper flow was not uniquely identified.' }
. ([scriptblock]::Create($functions[0].Extent.Text))
$actualFlow = [scriptblock]::Create($flows[0].Extent.Text)
function New-FakeCompanion {
    $child = [pscustomobject]@{ HasExited=$false; KillCalls=0; DisposeCalls=0; WaitCalls=0 }
    $child | Add-Member ScriptMethod Kill { $this.KillCalls++; $this.HasExited=$true }
    $child | Add-Member ScriptMethod WaitForExit { param([int]$Milliseconds); $this.WaitCalls++; return $Milliseconds -eq 5000 -and $this.HasExited }
    $child | Add-Member ScriptMethod Dispose { $this.DisposeCalls++ }
    return $child
}
function Invoke-ScopedNativeCheck {
    param([string]$FilePath, [string[]]$Arguments)
    if ($script:flowMode -eq 'fake') {
        $script:lastResult = & $script:runner -FilePath $FilePath -Arguments $Arguments -TimeoutMilliseconds 20 -CleanupMilliseconds 20 -DrainMilliseconds 20 -StartCaller { param($Info) return $script:fakeChecker }
    } else {
        $hostExe = if ($PSVersionTable.PSEdition -eq 'Core') { Join-Path $PSHOME 'pwsh.exe' } else { Join-Path $PSHOME 'powershell.exe' }
        $script:lastResult = & $script:runner -FilePath $hostExe -Arguments @('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-CheckerFixture','hang') -TimeoutMilliseconds 2500 -CleanupMilliseconds 2000 -DrainMilliseconds 1000
    }
    return $script:lastResult
}
foreach ($flowCase in @('fake', 'synthetic')) {
    $script:flowMode = $flowCase
    $script:fakeChecker = New-FakeChecker 'timeout'
    $script:checks = 'owned-test-checker.exe'
    $script:results = New-Object 'Collections.Generic.List[object]'
    $script:children = New-Object 'Collections.Generic.List[object]'
    $first = New-FakeCompanion; $second = New-FakeCompanion
    $children.Add($first); $children.Add($second)
    $script:primaryFailure = $null
    $clock = [Diagnostics.Stopwatch]::StartNew()
    . $actualFlow
    $clock.Stop()
    Check ($null -ne $primaryFailure -and $primaryFailure.Exception.Message -match 'Native shutdown check timed out: unit') ($flowCase + ': actual wrapper refuses successful markers from a timed-out checker')
    Check ($lastResult.TimedOut -and $lastResult.ExitObserved -and $lastResult.CallerPid -gt 0) ($flowCase + ': retained checker exit is confirmed after timeout cleanup')
    Check ($first.KillCalls -eq 1 -and $second.KillCalls -eq 1 -and $first.WaitCalls -eq 1 -and $second.WaitCalls -eq 1 -and
        $first.DisposeCalls -eq 1 -and $second.DisposeCalls -eq 1) ($flowCase + ': actual outer finally cleans every owned synthetic child')
    Check ($results.Count -eq 0) ($flowCase + ': timed-out checker is never recorded as a passing case')
    Check ($clock.ElapsedMilliseconds -lt 10000) ($flowCase + ': wrapper failure and child cleanup complete within a finite bound')
}
$report = [ordered]@{ Result='PASS'; Checks=$assertions; FakeCases=5; ActualWrapperTimeoutCases=2; SyntheticCheckerOnly=$true; MsiExecuted=$false; NativeBuildInvoked=$false }
[IO.File]::WriteAllText((Join-Path $scratch 'result.json'), ($report | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
$report | ConvertTo-Json -Compress
Write-Output ('Evidence=' + $scratch)
