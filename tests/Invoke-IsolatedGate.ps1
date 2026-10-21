#requires -Version 5.1
<#
.SYNOPSIS
    Run the existing gate without real keyboard input or the installed app's data.
.EXAMPLE
    pwsh -NoProfile -File .\tests\Invoke-IsolatedGate.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipClean,
    [ValidateSet('Gate', 'Host', 'Modules', 'BrokerModule', 'Template', 'Mutation', 'Broker', 'Invariants', 'Freshness')][string]$Check = 'Gate'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$runRoot = Join-Path $repoRoot ('artifacts\isolated-gate\' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$overrides = @{
    DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT = '1'
    DESKTOP_AI_COMPANION_DATA_ROOT = (Join-Path $runRoot 'data')
    DOTNET_CLI_HOME = (Join-Path $runRoot 'dotnet-home')
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_NOLOGO = '1'
    PYTHONUNBUFFERED = '1'
    TEMP = (Join-Path $runRoot 'tmp')
    TMP = (Join-Path $runRoot 'tmp')
}
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
New-Item -ItemType Directory -Path $overrides.TEMP -Force | Out-Null
$previous = @{}
foreach ($name in $overrides.Keys) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $overrides[$name], 'Process')
}
Write-Host "Isolated gate artifacts: $runRoot"

function ConvertTo-WindowsProcessArgument([string]$Value) {
    # Start-Process joins ArgumentList into a command line. Quote every scalar explicitly so
    # whitespace and literal quote characters reach the executable's actual GetArgs unchanged.
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Start-IsolatedHostProbe([string]$Executable, [string]$CommandLine) {
    # Retain the actual Process.Start result. Windows PowerShell's Start-Process can lose the
    # exit code of a quick child, while its output callbacks can also normalize its scalar text.
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $Executable
    $info.Arguments = $CommandLine
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardError = $true
    return [Diagnostics.Process]::Start($info)
}

$gateExit = 1
try {
    switch ($Check) {
        'Broker' {
            & (Join-Path $PSScriptRoot 'Test-AgentBroker.ps1')
            $gateExit = 0
        }
        'Modules' {
            & (Join-Path $PSScriptRoot 'Test-ModuleSelfTests.ps1') -RepoRoot $repoRoot
            $gateExit = 0
        }
        'BrokerModule' {
            $exe = Join-Path $repoRoot 'build\DesktopAICompanionPortable\bin\Release\x64\DesktopAICompanion.exe'
            $test = Start-Process -FilePath $exe -ArgumentList '--module-selftest=agentbroker' -PassThru -WindowStyle Hidden `
                -RedirectStandardOutput (Join-Path $runRoot 'agentbroker.out') -RedirectStandardError (Join-Path $runRoot 'agentbroker.err')
            if (-not $test.WaitForExit(60000)) {
                $test.Kill()
                $test.WaitForExit()
                throw 'AgentBroker loader self-test exceeded 60 seconds.'
            }
            $marker = Join-Path $overrides.TEMP 'dp-module-agentbroker-selftest.txt'
            if ($test.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $marker) -or
                (Get-Content -LiteralPath $marker -Raw -Encoding UTF8) -notmatch '(?m)^RESULT=PASS\s*$') {
                throw 'AgentBroker loader self-test did not pass. Inspect the isolated marker and logs.'
            }
            Get-Content -LiteralPath $marker -Encoding UTF8
            $gateExit = 0
        }
        'Invariants' {
            & (Join-Path $PSScriptRoot 'runtime-hardening-selftest.ps1')
            $gateExit = 0
        }
        'Freshness' {
            & (Join-Path $PSScriptRoot 'Test-ModuleSourceDiscovery.ps1')
            & (Join-Path $repoRoot 'packaging\Test-ModulePublishFreshness.ps1')
            $gateExit = 0
        }
        'Gate' {
            $gateParameters = @{}
            if ($SkipClean) { $gateParameters.SkipClean = $true }
            & (Join-Path $PSScriptRoot 'run-gate.ps1') @gateParameters
            $gateExit = $LASTEXITCODE
        }
        'Template' {
            & (Join-Path $repoRoot 'packaging\Test-ModuleTemplate.ps1')
            $gateExit = 0
        }
        'Host' {
            & dotnet build (Join-Path $repoRoot 'src\DesktopAICompanion_Portable.csproj') -c Release --no-restore --nologo -v:minimal
            if ($LASTEXITCODE -ne 0) { throw 'Host build failed.' }
            $exe = Join-Path $repoRoot 'build\DesktopAICompanionPortable\bin\Release\x64\DesktopAICompanion.exe'
            # A valid self-test accompanies each malformed scalar. Removing the refusal can
            # fail the check, but can never launch the normal app even against this isolated data.
            $refusals = @('--hardening-selftes', '--module-selftest=', '--module-selftest=   ',
                "--module-selftest=`t", '--module-selftest="', '--module-selftest=""',
                '--module-selftest=  ""  ', '--module-selftest=" "')
            $refusalIndex = 0
            foreach ($argument in $refusals) {
                $errorLog = Join-Path $runRoot ("unknown-switch-$refusalIndex.err")
                $commandLine = (ConvertTo-WindowsProcessArgument '--hardening-selftest') + ' ' +
                    (ConvertTo-WindowsProcessArgument $argument)
                $probe = Start-IsolatedHostProbe $exe $commandLine
                $errorTask = $probe.StandardError.ReadToEndAsync()
                try {
                    if (-not $probe.WaitForExit(10000)) {
                        $probe.Kill()
                        $probe.WaitForExit()
                        throw 'A malformed diagnostic did not exit within 10 seconds.'
                    }
                    $errorText = $errorTask.GetAwaiter().GetResult()
                    [IO.File]::WriteAllText($errorLog, $errorText)
                    $expectedFirstLine = 'Unknown command-line switch: ' + $argument + [Environment]::NewLine
                    if ($probe.ExitCode -ne 2 -or (Test-Path -LiteralPath $overrides.DESKTOP_AI_COMPANION_DATA_ROOT) -or
                        -not $errorText.StartsWith($expectedFirstLine, [StringComparison]::Ordinal)) {
                        throw 'A malformed diagnostic did not preserve its argument and refuse before app data or diagnostic dispatch.'
                    }
                }
                finally { $probe.Dispose() }
                $refusalIndex++
            }
            Write-Host "EarlyRefusalCases=$refusalIndex ActualArgumentsVerified=True DataRootCreated=False"
            $hardening = Start-IsolatedHostProbe $exe '--hardening-selftest'
            $hardeningErrors = $hardening.StandardError.ReadToEndAsync()
            try {
                if (-not $hardening.WaitForExit(60000)) {
                    $hardening.Kill()
                    $hardening.WaitForExit()
                    throw 'Host hardening self-test exceeded 60 seconds.'
                }
                [IO.File]::WriteAllText((Join-Path $runRoot 'hardening.err'), $hardeningErrors.GetAwaiter().GetResult())
                if ($hardening.ExitCode -ne 0) { throw 'Host hardening self-test failed.' }
                $marker = Join-Path $overrides.TEMP 'dp-hardening-selftest.txt'
                if (-not (Test-Path -LiteralPath $marker) -or
                    (Get-Content -LiteralPath $marker -Raw) -notmatch '(?m)^RESULT=PASS\s*$') {
                    throw 'The host hardening self-test did not write its PASS marker.'
                }
            }
            finally { $hardening.Dispose() }
            $gateExit = 0
        }
        'Mutation' {
            & python -X utf8 (Join-Path $PSScriptRoot 'mutate-selftest-guards.py') '--only=layout-aibrain: a row carries an EnabledWhen of its own again'
            $gateExit = $LASTEXITCODE
        }
    }
}
finally {
    foreach ($name in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
    }
}
exit $gateExit
