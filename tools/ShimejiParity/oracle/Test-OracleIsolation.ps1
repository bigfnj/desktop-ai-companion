#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$PythonPath = $env:TOOLBOX_PYTHON,
    [string]$ScratchRoot = [IO.Path]::GetTempPath(),
    [switch]$Mutate
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$oracleScratchParent = [IO.Path]::GetFullPath($ScratchRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$oracleTestRoot = [IO.Path]::GetFullPath((Join-Path $oracleScratchParent ('oracle-isolation-' + [Guid]::NewGuid().ToString('N'))))
if (-not $oracleTestRoot.StartsWith($oracleScratchParent, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ORACLE_ISOLATION_SCRATCH_OUTSIDE_ROOT'
}
[void][IO.Directory]::CreateDirectory($oracleTestRoot)
$oracleKeys = @('TEMP', 'TMP', 'DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT',
    'DESKTOP_AI_COMPANION_DATA_ROOT', 'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH')
$oracleSaved = @{}
foreach ($name in $oracleKeys) { $oracleSaved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$oracleUtf8 = New-Object Text.UTF8Encoding($false, $true)
$oracleWrapper = Join-Path $PSScriptRoot 'Invoke-Oracle.ps1'
$oracleWrapperBytes = [IO.File]::ReadAllBytes($oracleWrapper)
$oracleRuns = New-Object 'Collections.Generic.List[string]'
$oracleCopiedFiles = New-Object 'Collections.Generic.List[string]'
$oracleCodeDirectory = Join-Path $oracleTestRoot 'private-code'
$oracleMutationWitness = $null

function Assert-OracleOwnedPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -cne $oracleTestRoot -and -not $full.StartsWith($oracleTestRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw ('ORACLE_ISOLATION_CLEANUP_OUTSIDE_ROOT ' + $full)
    }
    if ([IO.File]::Exists($full) -or [IO.Directory]::Exists($full)) {
        if (([IO.File]::GetAttributes($full) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw ('ORACLE_ISOLATION_REPARSE_PATH ' + $full)
        }
    }
    return $full
}

function Assert-OracleParentIsolation([string]$WrapperPath, [string]$Label) {
    $run = Assert-OracleOwnedPath (Join-Path $oracleTestRoot $Label)
    [void][IO.Directory]::CreateDirectory($run)
    $oracleRuns.Add($run)
    $saved = @{}
    foreach ($name in $oracleKeys) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
    try {
        foreach ($name in $oracleKeys) { [Environment]::SetEnvironmentVariable($name, ('caller-marker-' + $name), 'Process') }
        $parameters = @{
            Action = 'SelfTest'; PythonPath = $PythonPath
            CacheRoot = (Join-Path $run 'cache')
            OutputRoot = (Join-Path $run 'evidence')
            JdkRoot = (Join-Path $run 'unused-jdk')
            DataRoot = (Join-Path $run 'data')
            TempRoot = (Join-Path $run 'temp')
        }
        # The wrapper runs in this very PowerShell process. Only its Python child is separate.
        $global:LASTEXITCODE = -771
        & $WrapperPath @parameters
        if ($LASTEXITCODE -ne 0) { throw 'ORACLE_ISOLATION_CHILD_FAILED' }
        foreach ($name in $oracleKeys) {
            if ([Environment]::GetEnvironmentVariable($name, 'Process') -cne ('caller-marker-' + $name)) {
                throw ('ORACLE_ISOLATION_PARENT_CHANGED ' + $name)
            }
        }
        Write-Output ('ORACLE_ISOLATION_PASS ' + $Label + ': child overrides verified; parent environment unchanged')
    }
    finally {
        foreach ($name in $oracleKeys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    }
}

try {
    Assert-OracleParentIsolation $oracleWrapper 'baseline'
    if ($Mutate) {
        [void][IO.Directory]::CreateDirectory($oracleCodeDirectory)
        # SelfTest uses only these two files and Python's standard library. No recursive repository copy.
        foreach ($leaf in @('Invoke-Oracle.ps1', 'oracle.py')) {
            $target = Assert-OracleOwnedPath (Join-Path $oracleCodeDirectory $leaf)
            [IO.File]::WriteAllBytes($target, [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot $leaf)))
            $oracleCopiedFiles.Add($target)
        }
        $privateWrapper = Join-Path $oracleCodeDirectory 'Invoke-Oracle.ps1'
        Assert-OracleParentIsolation $privateWrapper 'copied-baseline'
        $source = $oracleUtf8.GetString([IO.File]::ReadAllBytes($privateWrapper))
        $anchor = '$ErrorActionPreference = ''Stop'''
        $matchCount = [regex]::Matches($source, [regex]::Escape($anchor)).Count
        if ($matchCount -ne 1) { throw ('ORACLE_ISOLATION_MUTATION_ANCHOR_COUNT ' + $matchCount) }
        $replacement = $anchor + [Environment]::NewLine + '$env:TEMP = $TempRoot'
        [IO.File]::WriteAllBytes($privateWrapper, $oracleUtf8.GetBytes($source.Replace($anchor, $replacement)))
        try {
            Assert-OracleParentIsolation $privateWrapper 'parent-leak-mutant'
        }
        catch {
            if ($_.Exception.Message -cne 'ORACLE_ISOLATION_PARENT_CHANGED TEMP') { throw }
            $oracleMutationWitness = $_.Exception.Message
        }
        if ($oracleMutationWitness -cne 'ORACLE_ISOLATION_PARENT_CHANGED TEMP') {
            throw 'ORACLE_ISOLATION_MUTATION_SURVIVED'
        }
        Write-Output ('ORACLE_ISOLATION_MUTATION_FIRED ' + $oracleMutationWitness)
    }
}
finally {
    foreach ($name in $oracleKeys) { [Environment]::SetEnvironmentVariable($name, $oracleSaved[$name], 'Process') }
    foreach ($run in $oracleRuns) {
        foreach ($leaf in @('cache', 'evidence', 'data', 'temp')) {
            $directory = Assert-OracleOwnedPath (Join-Path $run $leaf)
            if ([IO.Directory]::Exists($directory)) { [IO.Directory]::Delete($directory, $false) }
        }
        [IO.Directory]::Delete((Assert-OracleOwnedPath $run), $false)
    }
    foreach ($path in $oracleCopiedFiles) { [IO.File]::Delete((Assert-OracleOwnedPath $path)) }
    if ([IO.Directory]::Exists($oracleCodeDirectory)) {
        [IO.Directory]::Delete((Assert-OracleOwnedPath $oracleCodeDirectory), $false)
    }
    [IO.Directory]::Delete((Assert-OracleOwnedPath $oracleTestRoot), $false)
    if ([IO.Directory]::Exists($oracleTestRoot)) { throw 'ORACLE_ISOLATION_SCRATCH_REMAINS' }
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($oracleWrapper)) -cne [Convert]::ToBase64String($oracleWrapperBytes)) {
        throw 'ORACLE_ISOLATION_TRACKED_WRAPPER_CHANGED'
    }
    foreach ($name in $oracleKeys) {
        if ([Environment]::GetEnvironmentVariable($name, 'Process') -cne $oracleSaved[$name]) {
            throw ('ORACLE_ISOLATION_RESTORE_FAILED ' + $name)
        }
    }
    Write-Output ('ORACLE_ISOLATION_CLEANUP_PASS absent: ' + $oracleTestRoot + '; tracked wrapper byte-identical; caller environment restored')
}
