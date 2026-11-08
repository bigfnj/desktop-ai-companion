#requires -Version 5
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$builder = Join-Path $repo 'installer\build-scoped-shutdown.ps1'
$variables = @('PATH','INCLUDE','EXTERNAL_INCLUDE','LIB','LIBPATH','VCToolsVersion','WindowsSDKVersion','WindowsSdkDir','UniversalCRTSdkDir','UCRTVersion','NoDefaultCurrentDirectoryInExePath')
$original = @{}
foreach ($variable in $variables) { $original[$variable] = [Environment]::GetEnvironmentVariable($variable, 'Process') }
try {
    # Reproduce the cmd expansion boundary in this process only. Vendor setup
    # must succeed using its bounded child seed and leave our caller untouched.
    $env:PATH = $original['PATH'] + ';' + ('C:\Windows;' * 850)
    $env:NoDefaultCurrentDirectoryInExePath = '1'
    $requested = @{}
    foreach ($variable in $variables) { $requested[$variable] = [Environment]::GetEnvironmentVariable($variable, 'Process') }
    & $builder -ProbeOnly | Out-Null
    foreach ($variable in $variables) {
        if ([Environment]::GetEnvironmentVariable($variable, 'Process') -cne $requested[$variable]) { throw ('Compiler probe changed caller ' + $variable) }
    }
    # A FAILURE AFTER THE IMPORT (N-audit-1106-364). This used to ask for an output under installer\, which the
    # builder refuses on its first lines, before its try and before it touches the environment, so the restore
    # assertion below could not fail. The output path is now an ordinary FILE below build\: it passes both path
    # checks, so the builder runs vendor setup and imports the compiler values into this process, and then fails
    # creating the output directory, inside the try whose finally must put the caller's values back. The message is
    # asserted so a refusal from anywhere earlier cannot stand in for it.
    $collision = Join-Path $repo ('build\compiler-environment-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory((Split-Path $collision -Parent))
    [IO.File]::WriteAllText($collision, 'an ordinary file where the native output directory would go')
    $failedLate = $false
    try {
        try { & $builder -OutputDirectory $collision | Out-Null }
        catch {
            # By the Win32 code (ERROR_ALREADY_EXISTS, 183) on the innermost exception, not by message text, which is
            # localised.
            $cause = $_.Exception
            while ($null -ne $cause.InnerException) { $cause = $cause.InnerException }
            if (-not ($cause -is [IO.IOException] -and ($cause.HResult -band 0xFFFF) -eq 183)) { throw }
            $failedLate = $true
        }
    } finally { [IO.File]::Delete($collision) }
    if (-not $failedLate) { throw 'The builder did not fail creating its output directory after the compiler import.' }
    foreach ($variable in $variables) {
        if ([Environment]::GetEnvironmentVariable($variable, 'Process') -cne $requested[$variable]) { throw ('Failed compiler build changed caller ' + $variable) }
    }
    Write-Output 'PASS compiler setup accepts long caller PATH and restores environment on success and failure'
} finally {
    foreach ($variable in $variables) {
        if ($null -eq $original[$variable]) { Remove-Item -LiteralPath ('Env:' + $variable) -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($variable, $original[$variable], 'Process') }
    }
}
