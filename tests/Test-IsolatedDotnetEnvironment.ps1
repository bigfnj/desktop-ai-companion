#requires -Version 5.1
<#
.SYNOPSIS
Exercise the actual isolated CLI environments without invoking the .NET SDK.
.EXAMPLE
pwsh -NoProfile -File tests/Test-IsolatedDotnetEnvironment.ps1
.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Test-IsolatedDotnetEnvironment.ps1 -Case Gate
.DESCRIPTION
Only disposable PowerShell children run. They refuse a missing PATH-write guard
before any SDK invocation. No registry or persistent PATH cleanup is performed.
#>
[CmdletBinding()]
param([ValidateSet('All', 'Gate', 'Controller')][string]$Case = 'All')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Case -ne 'Gate' -and $PSVersionTable.PSVersion.Major -lt 7) { throw 'Controller checks require PowerShell 7.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$utf8 = New-Object Text.UTF8Encoding($false, $true)
$checks = 0; $gateScenarios = 0; $controllerScenarios = 0
function Check([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw ('Test-IsolatedDotnetEnvironment.ps1: ' + $Description) }
    $script:checks++
}
function Parse-PublicScript([string]$Path) {
    $tokens = $null; $errors = $null
    $parsed = [Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    Check ($errors.Count -eq 0) ('public script parses: ' + [IO.Path]::GetFileName($Path))
    return $parsed
}
function Get-ActualFunctions($Ast, [string[]]$Names) {
    $text = ''
    foreach ($name in $Names) {
        $found = @($Ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true) |
            Where-Object Name -CEQ $name)
        Check ($found.Count -eq 1) ('one actual function: ' + $name)
        $text += $found[0].Extent.Text + [Environment]::NewLine
    }
    return $text
}
function Set-ProcessValue([string]$Name, [AllowNull()]$Value) {
    if ($null -eq $Value) { Remove-Item -LiteralPath ('Env:' + $Name) -ErrorAction SilentlyContinue }
    else { [Environment]::SetEnvironmentVariable($Name, [string]$Value, 'Process') }
}
function Same-Value($Actual, $Expected) {
    if ($null -eq $Actual -or $null -eq $Expected) { return $null -eq $Actual -and $null -eq $Expected }
    return [string]::Equals([string]$Actual, [string]$Expected, [StringComparison]::Ordinal)
}

$gateAst = Parse-PublicScript (Join-Path $repo 'tests\Invoke-IsolatedGate.ps1')
. ([scriptblock]::Create((Get-ActualFunctions $gateAst @('New-IsolatedGateEnvironment', 'ConvertTo-WindowsProcessArgument'))))
# Retain the production environment setup and finally verbatim. Replace ONLY
# the SDK/test dispatch switch with a child probe, so the healthy and negative
# witnesses cannot reach dotnet, package installation, registry tests or input.
$gateTries = @($gateAst.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] })
Check ($gateTries.Count -eq 1 -and $null -ne $gateTries[0].Finally) 'one actual gate try/finally'
$gateTry = $gateTries[0]
Check ($gateTry.Body.Statements.Count -eq 2 -and $gateTry.Body.Statements[0] -is [Management.Automation.Language.ForEachStatementAst]) 'gate has only environment setup before replaced dispatch'
$dispatch = @($gateTry.Body.Statements | Where-Object { $_ -is [Management.Automation.Language.SwitchStatementAst] })
Check ($dispatch.Count -eq 1 -and $dispatch[0].Condition.Extent.Text -ceq '$Check') 'one actual gate dispatch switch'
$offset = $dispatch[0].Extent.StartOffset - $gateTry.Extent.StartOffset
$safeTry = $gateTry.Extent.Text.Remove($offset, $dispatch[0].Extent.Text.Length).Insert($offset, '& $ProbeAction')
$gateProbe = [scriptblock]::Create($safeTry)
if ($Case -ne 'Gate') {
    $controllerAst = Parse-PublicScript (Join-Path $repo 'tools\agentbroker\Invoke-MutationShards.ps1')
    . ([scriptblock]::Create((Get-ActualFunctions $controllerAst @('Assert-Fixed', 'New-ShardEnvironment', 'Invoke-Captured'))))
}

$artifacts = Join-Path $repo 'artifacts'
$scratch = Join-Path $artifacts ('dotnet-env-' + [guid]::NewGuid().ToString('N'))
if ([IO.Directory]::Exists($scratch) -or [IO.File]::Exists($scratch)) { throw 'probe_root_already_exists' }
[void][IO.Directory]::CreateDirectory($scratch)
$probePath = Join-Path $scratch 'child-probe.ps1'
$probeSource = @'
#requires -Version 5.1
param([string]$ExpectedHome, [ValidateSet('Success','Failure')][string]$Outcome)
$guard = [Environment]::GetEnvironmentVariable('DOTNET_ADD_GLOBAL_TOOLS_TO_PATH', 'Process')
$cliHome = [Environment]::GetEnvironmentVariable('DOTNET_CLI_HOME', 'Process')
if ($guard -cne '0') { Write-Output 'GuardRefusedBeforeSdk=True'; exit 74 }
if (-not [string]::Equals($cliHome, $ExpectedHome, [StringComparison]::Ordinal)) { Write-Output 'WrongCliHome=True'; exit 75 }
Write-Output 'Guard=0;ExpectedCliHome=True;SdkCommandsExecuted=0;PersistentPathWrites=0'
if ($Outcome -ceq 'Failure') { exit 17 }
exit 0
'@
[IO.File]::WriteAllText($probePath, $probeSource, $utf8)
$shellName = if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh.exe' } else { 'powershell.exe' }
$shell = Join-Path $PSHOME $shellName
function Invoke-Probe([string]$CliHome, [string]$Outcome) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $shell
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $info.Arguments = (@('-NoProfile', '-File', $probePath, '-ExpectedHome', $CliHome, '-Outcome', $Outcome) |
        ForEach-Object { ConvertTo-WindowsProcessArgument $_ }) -join ' '
    $child = New-Object Diagnostics.Process
    $child.StartInfo = $info
    try {
        Check ($child.Start()) 'owned probe started'
        $output = $child.StandardOutput.ReadToEndAsync(); $errorOutput = $child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit(15000)) {
            $child.Kill()
            if (-not $child.WaitForExit(5000)) { throw 'owned_probe_cleanup_timeout' }
            throw 'owned_probe_timeout'
        }
        $text = $output.GetAwaiter().GetResult(); $errorText = $errorOutput.GetAwaiter().GetResult()
        Check ([string]::IsNullOrWhiteSpace($errorText)) 'owned probe stderr empty'
        return @{ Code = $child.ExitCode; Text = $text }
    }
    finally { $child.Dispose() }
}
function Assert-ProbeResult($Result, [string]$Outcome, [string]$Lane) {
    $expected = if ($Outcome -ceq 'Failure') { 17 } else { 0 }
    Check ($Result.Code -eq $expected) ($Lane + ' child refuses unsafe environment before SDK, actual exit ' + $Result.Code)
    Check ($Result.Text.Trim() -ceq 'Guard=0;ExpectedCliHome=True;SdkCommandsExecuted=0;PersistentPathWrites=0') ($Lane + ' child observed guard and isolated home')
}

$caller = @{}
foreach ($name in @('DOTNET_ADD_GLOBAL_TOOLS_TO_PATH', 'DOTNET_CLI_HOME', 'PATH')) {
    $caller[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
    foreach ($seed in @('Absent', 'Enabled', 'Disabled')) {
        $seedGuard = if ($seed -ceq 'Absent') { $null } elseif ($seed -ceq 'Enabled') { '1' } else { '0' }
        $seedHome = if ($seed -ceq 'Absent') { $null } else { Join-Path $scratch ('caller-' + $seed) }
        foreach ($outcome in @('Success', 'Failure')) {
            Set-ProcessValue 'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH' $seedGuard
            Set-ProcessValue 'DOTNET_CLI_HOME' $seedHome
            if ($Case -ne 'Controller') {
                $overrides = New-IsolatedGateEnvironment (Join-Path $scratch ('gate-' + $seed + '-' + $outcome))
                $previous = @{}; $record = @{}; $message = ''
                $ProbeAction = {
                    $record.Child = Invoke-Probe $overrides.DOTNET_CLI_HOME $outcome
                    if ($record.Child.Code -ne 0) { throw 'owned_probe_requested_failure' }
                }
                try { & $gateProbe } catch { $message = $_.Exception.Message }
                Check ($record.ContainsKey('Child')) ('Gate executed owned child: ' + $message)
                Assert-ProbeResult $record.Child $outcome 'Gate'
                Check ($message -ceq $(if ($outcome -ceq 'Failure') { 'owned_probe_requested_failure' } else { '' })) 'Gate preserved success or failure'
                Check (Same-Value ([Environment]::GetEnvironmentVariable('DOTNET_ADD_GLOBAL_TOOLS_TO_PATH', 'Process')) $seedGuard) 'Gate restored caller guard including absence'
                Check (Same-Value ([Environment]::GetEnvironmentVariable('DOTNET_CLI_HOME', 'Process')) $seedHome) 'Gate restored caller home including absence'
                Check (Same-Value ([Environment]::GetEnvironmentVariable('PATH', 'Process')) $caller.PATH) 'Gate preserved caller process PATH'
                $gateScenarios++
            }
            if ($Case -ne 'Gate') {
                $pool = Join-Path $scratch ('pool-' + $seed + '-' + $outcome)
                $environment = New-ShardEnvironment $pool 2 @('C:\probe-tool-one', 'C:\probe-tool-two') $caller.PATH
                Check ($environment.PATH -ceq ('C:\probe-tool-one;C:\probe-tool-two;' + $caller.PATH)) 'Controller retains tool and inherited PATH order'
                Check ([IO.Directory]::Exists($environment.DOTNET_CLI_HOME)) 'Controller owns private CLI home'
                $result = Invoke-Captured $shell @('-NoProfile', '-File', $probePath, '-ExpectedHome', $environment.DOTNET_CLI_HOME, '-Outcome', $outcome) $scratch $environment 15
                Assert-ProbeResult $result $outcome 'Controller'
                Check (Same-Value ([Environment]::GetEnvironmentVariable('DOTNET_ADD_GLOBAL_TOOLS_TO_PATH', 'Process')) $seedGuard) 'Controller never mutates caller guard'
                Check (Same-Value ([Environment]::GetEnvironmentVariable('DOTNET_CLI_HOME', 'Process')) $seedHome) 'Controller never mutates caller home'
                Check (Same-Value ([Environment]::GetEnvironmentVariable('PATH', 'Process')) $caller.PATH) 'Controller never mutates caller process PATH'
                $controllerScenarios++
            }
        }
    }
}
finally {
    foreach ($name in $caller.Keys) { Set-ProcessValue $name $caller[$name] }
}
$result = [ordered]@{ Result = 'PASS'; Checks = $checks; GateScenarios = $gateScenarios; ControllerScenarios = $controllerScenarios;
    PowerShellMajor = $PSVersionTable.PSVersion.Major; RealSdkInvoked = $false; PersistentPathCleanupPerformed = $false }
[IO.File]::WriteAllText((Join-Path $scratch 'result.json'), ($result | ConvertTo-Json), $utf8)
$result | ConvertTo-Json -Compress
Write-Output ('Evidence=' + $scratch)
