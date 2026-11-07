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
Test-MapReaderResult
if ($SelfTestOnly) { return }

# The verifier owns a private snapshot and changes its process environment. Keep that process separate from
# a caller's gate, and prevent a pwsh caller from handing 7-only module manifests to Windows PowerShell5.1.
$originalModulePath=$env:PSModulePath
$originalPreference=$ErrorActionPreference
try {
    $windowsPaths=@(($env:PSModulePath -split ';') | Where-Object { $_ -and $_ -match '(?i)windowspowershell' })
    foreach ($defaultPath in @((Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules'),
                              (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\Modules'))) {
        if (-not @($windowsPaths | Where-Object { $_ -ieq $defaultPath }).Count) { $windowsPaths += $defaultPath }
    }
    $env:PSModulePath=$windowsPaths -join ';'
    $ErrorActionPreference='Continue'
    $output=@(& powershell.exe -NoLogo -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'verify-map-readers.ps1') -ScratchRoot $ScratchRoot 2>&1)
    $childExit=$LASTEXITCODE
    $ErrorActionPreference=$originalPreference
    foreach ($line in $output) { Write-Host ([string]$line) }
    Assert-MapReaderResult $childExit $output
}
finally {
    $ErrorActionPreference=$originalPreference
    $env:PSModulePath=$originalModulePath
}
