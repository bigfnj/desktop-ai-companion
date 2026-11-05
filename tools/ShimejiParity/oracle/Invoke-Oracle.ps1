#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Preflight', 'Fetch', 'Build', 'Compare', 'Probe', 'Check', 'Draft', 'Bridge', 'Failures', 'Shared', 'SelfTest', 'Mutate', 'V1', 'V1Failures')]
    [string]$Action,
    # V1 only: a comma-separated list of scenario id substrings to run; all S-*.json under SharedRoot otherwise.
    [string]$Only,
    [string]$CacheRoot = $env:SHIMEJI_ORACLE_CACHE,
    [string]$OutputRoot,
    [string]$JdkRoot = $env:SHIMEJI_ORACLE_JDK8,
    [string]$PythonPath = $env:TOOLBOX_PYTHON,
    [ValidateSet('rebuilt', 'official')][string]$Runtime = 'rebuilt',
    [string]$SharedRoot,
    [string]$DataRoot,
    [string]$TempRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-OraclePath([string]$Value) {
    return [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Value))
}

function Get-OracleRepository {
    $directory = New-Object IO.DirectoryInfo($PSScriptRoot)
    while ($null -ne $directory) {
        $marker = Join-Path $directory.FullName '.git'
        if ([IO.File]::Exists($marker) -or [IO.Directory]::Exists($marker)) { return $directory.FullName }
        $directory = $directory.Parent
    }
    return $PSScriptRoot
}

function Assert-OracleExternalStorage([string]$Value, [string]$Label) {
    $repository = [IO.Path]::GetFullPath((Get-OracleRepository)).TrimEnd('\', '/')
    if ([string]::Equals($Value.TrimEnd('\', '/'), $repository, [StringComparison]::OrdinalIgnoreCase) -or
        $Value.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw ('ORACLE_REPOSITORY_STORAGE_REFUSAL ' + $Label)
    }
}

if ([string]::IsNullOrWhiteSpace($CacheRoot)) { throw 'Pass -CacheRoot or set SHIMEJI_ORACLE_CACHE to a private cache outside the checkout.' }
if ([string]::IsNullOrWhiteSpace($JdkRoot)) { throw 'Pass -JdkRoot or set SHIMEJI_ORACLE_JDK8 to a JDK 8 installation.' }
$CacheRoot = Resolve-OraclePath $CacheRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $CacheRoot 'evidence' }
$OutputRoot = Resolve-OraclePath $OutputRoot
if ([string]::IsNullOrWhiteSpace($DataRoot)) { $DataRoot = Join-Path $OutputRoot 'data' }
if ([string]::IsNullOrWhiteSpace($TempRoot)) { $TempRoot = Join-Path $OutputRoot 'temp' }
$DataRoot = Resolve-OraclePath $DataRoot
$TempRoot = Resolve-OraclePath $TempRoot
$JdkRoot = Resolve-OraclePath $JdkRoot
if (-not [string]::IsNullOrWhiteSpace($SharedRoot)) { $SharedRoot = Resolve-OraclePath $SharedRoot }
if (($Action -eq 'Shared' -or $Action -eq 'V1') -and [string]::IsNullOrWhiteSpace($SharedRoot)) { throw ($Action + ' needs -SharedRoot naming generated synthetic scenarios.') }
if ([string]::IsNullOrWhiteSpace($PythonPath)) { $PythonPath = (Get-Command python.exe -ErrorAction Stop).Source }
else {
    $candidate = Resolve-OraclePath $PythonPath
    if ([IO.File]::Exists($candidate)) { $PythonPath = $candidate }
    else { $PythonPath = (Get-Command $PythonPath -ErrorAction Stop).Source }
}
$PythonPath = Resolve-OraclePath $PythonPath
Assert-OracleExternalStorage $CacheRoot 'cache-root'
Assert-OracleExternalStorage $OutputRoot 'output-root'
Assert-OracleExternalStorage $DataRoot 'data-root'
Assert-OracleExternalStorage $TempRoot 'temp-root'
if (-not [string]::IsNullOrWhiteSpace($SharedRoot)) { Assert-OracleExternalStorage $SharedRoot 'shared-root' }

function Quote-OracleArgument([string]$Value) {
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

$oracleArguments = @('-I', '-B', '-X', 'utf8', (Join-Path $PSScriptRoot 'oracle.py'),
    $Action.ToLowerInvariant(), '--cache-root', $CacheRoot, '--output-root', $OutputRoot,
    '--jdk-root', $JdkRoot, '--runtime', $Runtime, '--temp-root', $TempRoot, '--data-root', $DataRoot)
if (-not [string]::IsNullOrWhiteSpace($SharedRoot)) { $oracleArguments += @('--shared-root', $SharedRoot) }
if (-not [string]::IsNullOrWhiteSpace($Only)) { $oracleArguments += @('--only', $Only) }
$oracleEnvironment = @{
    DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT = '1'
    DESKTOP_AI_COMPANION_DATA_ROOT = $DataRoot
    TEMP = $TempRoot
    TMP = $TempRoot
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
}
$oracleStart = New-Object Diagnostics.ProcessStartInfo
$oracleStart.FileName = $PythonPath
$oracleStart.Arguments = (($oracleArguments | ForEach-Object { Quote-OracleArgument $_ }) -join ' ')
# The child validates physical storage paths before creating any directory.
$oracleStart.WorkingDirectory = $PSScriptRoot
$oracleStart.UseShellExecute = $false
$oracleStart.CreateNoWindow = $true
$oracleStart.RedirectStandardOutput = $true
$oracleStart.RedirectStandardError = $true
$oracleStart.StandardOutputEncoding = New-Object Text.UTF8Encoding($false)
$oracleStart.StandardErrorEncoding = New-Object Text.UTF8Encoding($false)
foreach ($name in $oracleEnvironment.Keys) {
    $oracleStart.EnvironmentVariables[$name] = $oracleEnvironment[$name]
}
$oracleProcess = New-Object Diagnostics.Process
$oracleProcess.StartInfo = $oracleStart
try {
    [void]$oracleProcess.Start()
    $oracleStdout = $oracleProcess.StandardOutput.ReadToEndAsync()
    $oracleStderr = $oracleProcess.StandardError.ReadToEndAsync()
    $oracleProcess.WaitForExit()
    $oracleExitCode = $oracleProcess.ExitCode
    [Console]::Out.Write($oracleStdout.GetAwaiter().GetResult())
    [Console]::Error.Write($oracleStderr.GetAwaiter().GetResult())
}
finally { $oracleProcess.Dispose() }
$global:LASTEXITCODE = $oracleExitCode
if ($oracleExitCode -ne 0) { exit $oracleExitCode }
