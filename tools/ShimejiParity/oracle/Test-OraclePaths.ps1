#requires -Version 5.1
[CmdletBinding()]
param([string]$PythonPath = $env:TOOLBOX_PYTHON, [string]$ScratchRoot = [IO.Path]::GetTempPath(), [switch]$Mutate)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$oraclePathParent = [IO.Path]::GetFullPath($ScratchRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$oraclePathRoot = [IO.Path]::GetFullPath((Join-Path $oraclePathParent ('oracle-paths-' + [Guid]::NewGuid().ToString('N'))))
if (-not $oraclePathRoot.StartsWith($oraclePathParent, [StringComparison]::OrdinalIgnoreCase)) { throw 'ORACLE_PATHS_SCRATCH_ESCAPE' }
$oraclePathLocation = Join-Path $oraclePathRoot 'location'
$oracleNativeLocation = Join-Path $oraclePathRoot 'native-cwd'
$oraclePrivateCode = Join-Path $oraclePathRoot 'private-code'
foreach ($directory in @($oraclePathRoot, $oraclePathLocation, $oracleNativeLocation, $oraclePrivateCode)) {
    [void][IO.Directory]::CreateDirectory($directory)
}
$oraclePathUtf8 = New-Object Text.UTF8Encoding($false, $true)
$oraclePathWrapper = Join-Path $PSScriptRoot 'Invoke-Oracle.ps1'
$oracleOriginalWrapper = [IO.File]::ReadAllBytes($oraclePathWrapper)
$oraclePathFiles = New-Object 'Collections.Generic.List[string]'
$oracleCaller = Join-Path $oraclePathRoot 'caller.ps1'
$oraclePathResults = New-Object 'Collections.Generic.List[object]'
$oracleCallerBody = @'
param([string]$Wrapper, [string]$Python, [string]$PSLocation, [string]$Cache, [string]$Output)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSLocation
$parameters = @{
    Action = 'Preflight'; PythonPath = $Python
    CacheRoot = $Cache; OutputRoot = $Output; JdkRoot = 'jdk'
    SharedRoot = 'shared'; DataRoot = 'data'; TempRoot = 'temp'
}
& $Wrapper @parameters
exit $LASTEXITCODE
'@
[IO.File]::WriteAllText($oracleCaller, $oracleCallerBody, $oraclePathUtf8)
$oraclePathFiles.Add($oracleCaller)

function Quote-OraclePathArgument([string]$Value) {
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Invoke-OraclePathCase([string]$Wrapper, [string]$Cache, [string]$Output) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $PSHOME 'powershell.exe'
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $oracleCaller,
        '-Wrapper', $Wrapper, '-Python', $PythonPath, '-PSLocation', $oraclePathLocation, '-Cache', $Cache, '-Output', $Output)
    $start.Arguments = (($arguments | ForEach-Object { Quote-OraclePathArgument $_ }) -join ' ')
    $start.WorkingDirectory = $oracleNativeLocation
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $oraclePathUtf8
    $start.StandardErrorEncoding = $oraclePathUtf8
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'ORACLE_PATHS_CHILD_TIMEOUT' }
        if (-not $stdout.Wait(10000) -or -not $stderr.Wait(10000)) { throw 'ORACLE_PATHS_PIPE_TIMEOUT' }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $stdout.GetAwaiter().GetResult()
            Error = $stderr.GetAwaiter().GetResult(); Exited = $process.HasExited }
    }
    finally { $process.Dispose() }
}

function Assert-OraclePathResult($Result) {
    if ($Result.ExitCode -ne 0) { throw ('ORACLE_PATHS_CHILD_FAILED ' + $Result.Error) }
    $record = $Result.Output | ConvertFrom-Json
    if ($record.status -cne 'preflight-pass') { throw 'ORACLE_PATHS_PREFLIGHT_WITNESS' }
    $leaves = @{ cache_root = 'cache'; output_root = 'output'; jdk_root = 'jdk'; shared_root = 'shared'; data_root = 'data'; temp_root = 'temp' }
    foreach ($name in @('cache_root', 'output_root', 'jdk_root', 'shared_root', 'data_root', 'temp_root')) {
        $expected = Join-Path $oraclePathLocation $leaves[$name]
        if (-not [string]::Equals($record.paths.$name, $expected, [StringComparison]::OrdinalIgnoreCase)) {
            throw ('ORACLE_PATHS_PWD_WITNESS ' + $name)
        }
        if ([IO.Directory]::Exists($expected) -or [IO.File]::Exists($expected)) { throw ('ORACLE_PATHS_PREFLIGHT_WROTE ' + $name) }
    }
}

function Assert-OraclePathCleanup([string]$Value) {
    $path = [IO.Path]::GetFullPath($Value)
    if ($path -cne $oraclePathRoot -and -not $path.StartsWith($oraclePathRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) { throw 'ORACLE_PATHS_CLEANUP_ESCAPE' }
    if ([IO.File]::Exists($path) -or [IO.Directory]::Exists($path)) {
        if (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'ORACLE_PATHS_CLEANUP_REPARSE' }
    }
    return $path
}

try {
    $baseline = Invoke-OraclePathCase $oraclePathWrapper 'cache' 'output'
    Assert-OraclePathResult $baseline
    $oraclePathResults.Add([pscustomobject]@{ Case = 'relative-powershell-location'; Status = 'PASS'; Exited = $baseline.Exited })
    $repository = New-Object IO.DirectoryInfo($PSScriptRoot)
    while ($null -ne $repository -and -not ([IO.File]::Exists((Join-Path $repository.FullName '.git')) -or
            [IO.Directory]::Exists((Join-Path $repository.FullName '.git')))) { $repository = $repository.Parent }
    if ($null -eq $repository) { throw 'ORACLE_PATHS_REPOSITORY_MARKER_MISSING' }
    $forbidden = Join-Path $repository.FullName ('oracle-storage-must-not-exist-' + [Guid]::NewGuid().ToString('N'))
    foreach ($case in @('cache-root', 'output-root')) {
        if ($case -eq 'cache-root') { $result = Invoke-OraclePathCase $oraclePathWrapper $forbidden 'output' }
        else { $result = Invoke-OraclePathCase $oraclePathWrapper 'cache' $forbidden }
        $expected = 'ORACLE_REPOSITORY_STORAGE_REFUSAL ' + $case
        if ($result.ExitCode -eq 0 -or -not $result.Error.Contains($expected)) { throw ('ORACLE_PATHS_REPOSITORY_WITNESS ' + $case) }
        if ([IO.Directory]::Exists($forbidden) -or [IO.File]::Exists($forbidden)) { throw 'ORACLE_PATHS_REPOSITORY_WROTE' }
        $oraclePathResults.Add([pscustomobject]@{ Case = $case; Status = 'REFUSED'; Witness = $expected })
    }
    if ($Mutate) {
        foreach ($leaf in @('Invoke-Oracle.ps1', 'oracle.py')) {
            $copy = Join-Path $oraclePrivateCode $leaf
            [IO.File]::WriteAllBytes($copy, [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot $leaf)))
            $oraclePathFiles.Add($copy)
        }
        $privateWrapper = Join-Path $oraclePrivateCode 'Invoke-Oracle.ps1'
        $source = $oraclePathUtf8.GetString([IO.File]::ReadAllBytes($privateWrapper))
        $anchor = 'return [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Value))'
        $matchCount = [regex]::Matches($source, [regex]::Escape($anchor)).Count
        if ($matchCount -ne 1) { throw 'ORACLE_PATHS_MUTATION_ANCHOR_WITNESS' }
        [IO.File]::WriteAllBytes($privateWrapper, $oraclePathUtf8.GetBytes($source.Replace($anchor, 'return [IO.Path]::GetFullPath($Value)')))
        $mutant = Invoke-OraclePathCase $privateWrapper 'cache' 'output'
        $witness = $null
        try { Assert-OraclePathResult $mutant }
        catch { $witness = $_.Exception.Message }
        if ($witness -cne 'ORACLE_PATHS_PWD_WITNESS cache_root') { throw ('ORACLE_PATHS_MUTATION_DID_NOT_FIRE ' + $witness) }
        $oraclePathResults.Add([pscustomobject]@{ Case = 'relative-native-cwd-regression'; Status = 'FIRED'; Witness = $witness })
    }
}
finally {
    foreach ($file in $oraclePathFiles) { [IO.File]::Delete((Assert-OraclePathCleanup $file)) }
    foreach ($directory in @($oraclePrivateCode, $oraclePathLocation, $oracleNativeLocation, $oraclePathRoot)) {
        [IO.Directory]::Delete((Assert-OraclePathCleanup $directory), $false)
    }
    if ([IO.Directory]::Exists($oraclePathRoot)) { throw 'ORACLE_PATHS_SCRATCH_REMAINS' }
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($oraclePathWrapper)) -cne [Convert]::ToBase64String($oracleOriginalWrapper)) {
        throw 'ORACLE_PATHS_TRACKED_WRAPPER_CHANGED'
    }
    Write-Output ('ORACLE_PATHS_CLEANUP_PASS absent: ' + $oraclePathRoot)
}
$oraclePathResults | ConvertTo-Json -Depth 4
