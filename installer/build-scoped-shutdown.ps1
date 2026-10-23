#requires -Version 5
[CmdletBinding()]
param(
    [string]$OutputDirectory = '',
    [switch]$Tests,
    [switch]$ProbeOnly,
    [switch]$VerifyReproducibility,
    [Parameter(DontShow)][switch]$CheckPathsOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'ScopedShutdown\PathSafety.ps1')
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $repo 'build\scoped-shutdown' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$buildRoot = [IO.Path]::GetFullPath((Join-Path $repo 'build'))
if (-not $output.StartsWith($buildRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Native output must be below this checkout build directory.' }
[void](Assert-ScopedShutdownPath -Path $output -TrustedRoot $repo)
$source = Join-Path $PSScriptRoot 'ScopedShutdown'
foreach ($name in @('ShutdownPolicy.h','ShutdownPolicy.cpp','NativePlatform.cpp','MsiEntry.cpp','ScopedShutdown.def')) {
    [void](Assert-ScopedShutdownPath -Path (Join-Path $source $name) -TrustedRoot $repo)
}
if ($Tests) {
    foreach ($name in @('ShutdownChecks.cpp','SyntheticCompanion.cpp')) {
        [void](Assert-ScopedShutdownPath -Path (Join-Path $repo ('tests\InstallerShutdown.Tests\' + $name)) -TrustedRoot $repo)
    }
}
if ($CheckPathsOnly) { Write-Output 'NATIVE_BUILD_PATHS_PASS'; return }
if (-not [Environment]::Is64BitProcess) { throw 'The native installer helper requires a Windows x64 build process.' }
$discovery = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not [IO.File]::Exists($discovery)) { throw 'Existing MSVC discovery tool not found. No tools were installed.' }
$installations = @(& $discovery -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
if ($LASTEXITCODE -ne 0 -or $installations.Count -ne 1) { throw 'An existing MSVC x64 toolchain is required. No tools were installed.' }
$environmentScript = Join-Path $installations[0] 'Common7\Tools\VsDevCmd.bat'
if (-not [IO.File]::Exists($environmentScript)) { throw 'Existing MSVC environment setup was not found. No tools were installed.' }
$compilerVariables = @('PATH','INCLUDE','EXTERNAL_INCLUDE','LIB','LIBPATH','VCToolsVersion','WindowsSDKVersion','WindowsSdkDir','UniversalCRTSdkDir','UCRTVersion')
$callerEnvironment = @{}
foreach ($variable in $compilerVariables) { $callerEnvironment[$variable] = [Environment]::GetEnvironmentVariable($variable, 'Process') }
try {
    # VsDevCmd uses cmd expansion internally. An inherited PATH near cmd's 8191
    # character limit can break its own SDK discovery. Bound only the setup
    # child's seed, then import only the compiler values this build needs.
    # No registry PATH write or security/environment policy change is made.
    $setup = New-Object Diagnostics.ProcessStartInfo
    $setup.FileName = Join-Path ([Environment]::SystemDirectory) 'cmd.exe'
    $setup.UseShellExecute = $false
    $setup.CreateNoWindow = $true
    $setup.RedirectStandardOutput = $true
    $setup.RedirectStandardError = $true
    $setup.EnvironmentVariables['PATH'] = (Split-Path $discovery -Parent) + ';' + [Environment]::SystemDirectory + ';' + [Environment]::GetFolderPath('Windows')
    $setup.Arguments = '/d /s /c ""' + $environmentScript + '" -no_logo -arch=x64 -host_arch=x64 && for %v in (' + ($compilerVariables -join ' ') + ') do @set %v"'
    $setupProcess = New-Object Diagnostics.Process
    $setupProcess.StartInfo = $setup
    try {
        if (-not $setupProcess.Start()) { throw 'Existing MSVC environment setup could not start.' }
        $setupOutput = $setupProcess.StandardOutput.ReadToEndAsync()
        $setupError = $setupProcess.StandardError.ReadToEndAsync()
        if (-not $setupProcess.WaitForExit(60000)) {
            $setupProcess.Kill()
            throw 'Existing MSVC environment setup timed out.'
        }
        $environmentText = $setupOutput.GetAwaiter().GetResult()
        $errorText = $setupError.GetAwaiter().GetResult()
        if ($setupProcess.ExitCode -ne 0 -or -not [string]::IsNullOrWhiteSpace($errorText)) { throw 'Existing MSVC environment setup failed. No tools were installed.' }
    } finally { $setupProcess.Dispose() }
    $compilerEnvironment = @{}
    foreach ($line in ($environmentText -split "`r?`n")) {
        $equals = $line.IndexOf('=')
        if ($equals -lt 1) { continue }
        $variable = $line.Substring(0, $equals)
        if ($variable -in $compilerVariables) { $compilerEnvironment[$variable] = $line.Substring($equals + 1) }
    }
    foreach ($required in @('PATH','INCLUDE','LIB','VCToolsVersion','WindowsSDKVersion','WindowsSdkDir','UniversalCRTSdkDir','UCRTVersion')) {
        if (-not $compilerEnvironment.ContainsKey($required) -or [string]::IsNullOrWhiteSpace($compilerEnvironment[$required])) { throw ('Existing MSVC setup did not provide ' + $required + '. No tools were installed.') }
    }
    foreach ($variable in $compilerVariables) { [Environment]::SetEnvironmentVariable($variable, $compilerEnvironment[$variable], 'Process') }
    $compilerVersion = $env:VCToolsVersion.TrimEnd('\')
    $sdkVersion = $env:WindowsSDKVersion.TrimEnd('\')
    if ($ProbeOnly) { [pscustomobject]@{ ExistingMsvc = $true; InstallerRuntimeDependency = 'none' }; return }
[void](Assert-ScopedShutdownPath -Path $output -TrustedRoot $repo)
[void][IO.Directory]::CreateDirectory($output)
$common = @('/nologo','/std:c++17','/EHsc','/W4','/WX','/O2','/MT','/DUNICODE','/D_UNICODE','/DWIN32_LEAN_AND_MEAN','/DNOMINMAX',('/I' + $source),('/Fo' + $output + '\'))
$inputs = @((Join-Path $source 'ShutdownPolicy.cpp'), (Join-Path $source 'NativePlatform.cpp'))
& cl.exe @common /LD @inputs (Join-Path $source 'MsiEntry.cpp') /link /INCREMENTAL:NO /Brepro /DYNAMICBASE /NXCOMPAT (('/DEF:' + (Join-Path $source 'ScopedShutdown.def'))) (('/OUT:' + (Join-Path $output 'ScopedShutdown.dll'))) (('/IMPLIB:' + (Join-Path $output 'ScopedShutdown.lib'))) msi.lib user32.lib advapi32.lib
if ($LASTEXITCODE -ne 0) { throw 'Scoped shutdown custom action build failed.' }
$binary = Join-Path $output 'ScopedShutdown.dll'
$dependencies = @(& dumpbin.exe /nologo /dependents $binary)
if ($LASTEXITCODE -ne 0) { throw 'Native shutdown dependency inspection failed.' }
$imports = @($dependencies | ForEach-Object { if ($_ -match '^\s+([A-Za-z0-9_.-]+\.dll)\s*$') { $matches[1] } })
if ($imports.Count -lt 1 -or @($imports | Where-Object { $_ -notin @('KERNEL32.dll','USER32.dll','ADVAPI32.dll','msi.dll') }).Count -gt 0) {
    throw ('Native shutdown helper acquired an unexpected runtime dependency: ' + ($imports -join ', '))
}
$stream = [IO.File]::OpenRead($binary)
$reader = New-Object IO.BinaryReader($stream)
try {
    if ($reader.ReadUInt16() -ne 0x5a4d) { throw 'Native shutdown helper is not a PE image.' }
    $stream.Position = 0x3c; $header = $reader.ReadUInt32()
    if ($header -gt $stream.Length - 264) { throw 'Native shutdown PE header is outside the image.' }
    $stream.Position = $header
    if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw 'Native shutdown helper must be x64.' }
    $stream.Position = $header + 24
    if ($reader.ReadUInt16() -ne 0x20b) { throw 'Native shutdown helper must be PE32+.' }
    $stream.Position = $header + 24 + 112 + (14 * 8)
    if ($reader.ReadUInt64() -ne 0) { throw 'Native shutdown helper unexpectedly requires the CLR.' }
} finally { $reader.Dispose() }
$binaryHash = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
$binaryReport = [ordered]@{ Architecture='x64'; ClrHeader=$false; StaticCppRuntime=$true; Imports=$imports; Sha256=$binaryHash;
    MsvcVersion=$compilerVersion; WindowsSdkVersion=$sdkVersion }
if ($VerifyReproducibility) {
    $secondOutput = Join-Path $output 'repro-check'
    & $PSCommandPath -OutputDirectory $secondOutput | Out-Host
    $secondHash = (Get-FileHash -LiteralPath (Join-Path $secondOutput 'ScopedShutdown.dll') -Algorithm SHA256).Hash
    if ($secondHash -cne $binaryHash) { throw 'Native shutdown helper is not byte reproducible across separate output directories.' }
    $binaryReport.Reproducible = $true
}
[IO.File]::WriteAllText((Join-Path $output 'native-binary.json'), ($binaryReport | ConvertTo-Json -Depth 3), (New-Object Text.UTF8Encoding($false)))
if ($Tests) {
    & cl.exe @common @inputs (Join-Path $repo 'tests\InstallerShutdown.Tests\ShutdownChecks.cpp') /link /INCREMENTAL:NO /Brepro /DYNAMICBASE /NXCOMPAT (('/OUT:' + (Join-Path $output 'ShutdownChecks.exe'))) user32.lib advapi32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Scoped shutdown checks build failed.' }
    & cl.exe @common (Join-Path $repo 'tests\InstallerShutdown.Tests\SyntheticCompanion.cpp') /link /INCREMENTAL:NO /Brepro /DYNAMICBASE /NXCOMPAT /SUBSYSTEM:WINDOWS (('/OUT:' + (Join-Path $output 'SyntheticCompanion.exe'))) user32.lib shell32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic companion build failed.' }
}
[pscustomobject]@{ OutputDirectory = $output; InstallerRuntimeDependency = 'none'; Tests = [bool]$Tests }
} finally {
    foreach ($variable in $compilerVariables) {
        if ($null -eq $callerEnvironment[$variable]) { Remove-Item -LiteralPath ('Env:' + $variable) -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($variable, $callerEnvironment[$variable], 'Process') }
    }
}
