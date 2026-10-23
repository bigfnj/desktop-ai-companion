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
    $refused = $false
    try { & $builder -OutputDirectory (Join-Path $repo 'installer') | Out-Null }
    catch {
        if ($_.Exception.Message -cnotmatch '^Native output must be below this checkout build directory\.$') { throw }
        $refused = $true
    }
    if (-not $refused) { throw 'Invalid native output path was not refused.' }
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
