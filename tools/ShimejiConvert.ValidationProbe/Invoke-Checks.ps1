#requires -Version 5.1
[CmdletBinding()]
param([switch]$SelfTestOnly, [string]$ScratchRoot = [IO.Path]::GetTempPath())
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-MapReaderResult([int]$ExitCode, [object[]]$Lines) {
    # A PASS line cannot excuse a failing child, and exit zero alone proves no checks ran.
    if ($ExitCode -ne 0) { throw ('MAP_READERS_CHILD_FAILED: exit ' + $ExitCode) }
    if (@($Lines | Where-Object { [string]$_ -ceq 'MAP READERS VERIFY PASS (0 mutations fired)' }).Count -ne 1) {
        throw 'MAP_READERS_CHILD_WITNESS_MISSING'
    }
}
function Test-MapReaderResult {
    foreach ($case in @(
        @{ name='positive'; code=0; lines=@('MAP READERS VERIFY PASS (0 mutations fired)'); pass=$true },
        @{ name='failed-child-with-pass'; code=1; lines=@('MAP READERS VERIFY PASS (0 mutations fired)'); pass=$false },
        @{ name='missing-pass'; code=0; lines=@('unrelated output'); pass=$false },
        @{ name='duplicate-pass'; code=0; lines=@('MAP READERS VERIFY PASS (0 mutations fired)','MAP READERS VERIFY PASS (0 mutations fired)'); pass=$false }
    )) {
        $accepted=$true
        try { Assert-MapReaderResult $case.code $case.lines }
        catch {
            if (-not $_.Exception.Message.StartsWith('MAP_READERS_CHILD_', [StringComparison]::Ordinal)) { throw }
            $accepted=$false
        }
        if ($accepted -ne $case.pass) { throw ('MAP_READERS_CALLER_SELFTEST_FAILED: ' + $case.name) }
    }
    Write-Host 'MAP READERS CALLER SELFTEST PASS'
}
function Remove-MapReaderScratch([string]$Path, [string]$Parent) {
    # Delete only the caller-owned child, never its parent or a linked tree supplied by a child process.
    $full=[IO.Path]::GetFullPath($Path)
    $expectedParent=[IO.Path]::GetFullPath($Parent).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($full).TrimEnd('\') -ine $expectedParent -or
        [IO.Path]::GetFileName($full) -cnotmatch '^mr-[0-9a-f]{12}$') { throw 'MAP_READERS_CLEANUP_UNSAFE_PATH' }
    if (Test-Path -LiteralPath $full) {
        if (((Get-Item -LiteralPath $full -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            @(Get-ChildItem -LiteralPath $full -Force -Recurse | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -ne 0) { throw 'MAP_READERS_CLEANUP_LINKED_PATH' }
        Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction Stop
    }
    if (Test-Path -LiteralPath $full) { throw 'MAP_READERS_CLEANUP_FAILED' }
}
Test-MapReaderResult
if ($SelfTestOnly) { return }

# The verifier owns a private snapshot and changes its process environment. Keep that process separate from
# a caller's gate, and prevent a pwsh caller from handing 7-only module manifests to Windows PowerShell5.1.
$originalModulePath=$env:PSModulePath
$originalPreference=$ErrorActionPreference
$callerScratchParent=[IO.Path]::GetFullPath($ScratchRoot)
# Keep this owner name short: the verifier and its image fixtures create their own nested scratch paths.
$callerScratch=Join-Path $callerScratchParent ('mr-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
if (Test-Path -LiteralPath $callerScratch) { throw 'MAP_READERS_SCRATCH_ALREADY_EXISTS' }
[IO.Directory]::CreateDirectory($callerScratch) | Out-Null
$readerPassed=$false
try {
    $windowsPaths=@(($env:PSModulePath -split ';') | Where-Object { $_ -and $_ -match '(?i)windowspowershell' })
    foreach ($defaultPath in @((Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules'),
                              (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\Modules'))) {
        if (-not @($windowsPaths | Where-Object { $_ -ieq $defaultPath }).Count) { $windowsPaths += $defaultPath }
    }
    $env:PSModulePath=$windowsPaths -join ';'
    $ErrorActionPreference='Continue'
    $output=@(& powershell.exe -NoLogo -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'verify-map-readers.ps1') -ScratchRoot $callerScratch 2>&1)
    $childExit=$LASTEXITCODE
    $ErrorActionPreference=$originalPreference
    foreach ($line in $output) { Write-Host ([string]$line) }
    Assert-MapReaderResult $childExit $output
    $readerPassed=$true
}
finally {
    $ErrorActionPreference=$originalPreference
    $env:PSModulePath=$originalModulePath
    if ($readerPassed) {
        try { Remove-MapReaderScratch $callerScratch $callerScratchParent }
        catch { Write-Host ('Map reader cleanup failed; evidence retained: ' + $callerScratch); throw }
        Write-Host 'MAP READERS CLEANUP PASS'
    }
    else { Write-Host ('Map reader failure evidence retained: ' + $callerScratch) }
}
