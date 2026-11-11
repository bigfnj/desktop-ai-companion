#requires -Version 7
<# Readiness checks exercise the same publisher and startup classifier as the real process fixture. #>
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'AgentBrokerFixtureStartup.ps1')
$node=@(Get-Command node.exe -CommandType Application -ErrorAction Stop)[0].Source
$runner=Join-Path $PSScriptRoot 'agentbroker-fixture-ready.test.mjs'
$helperWitness='READY_HELPER_SHA256='+((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'agentbroker-fixture-ready.mjs') -Algorithm SHA256).Hash.ToLowerInvariant())
$workspace=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$ownedRoot=[IO.Path]::GetFullPath((Join-Path $workspace ('artifacts\fixture-readiness\'+[Guid]::NewGuid().ToString('N'))))
if (-not $ownedRoot.StartsWith($workspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Readiness scratch escaped workspace.' }
[IO.Directory]::CreateDirectory($ownedRoot) | Out-Null
$checks=0; $label='READY_NOT_VISIBLE_UNTIL_CLOSED'; $failure=$null
function Start-ReadinessChild([string]$Mode,[string]$Root) {
    $start=[Diagnostics.ProcessStartInfo]::new($node)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.StandardOutputEncoding=[Text.UTF8Encoding]::new($false,$true); $start.StandardErrorEncoding=[Text.UTF8Encoding]::new($false,$true)
    foreach ($argument in @($runner,$Mode,$Root)) { $start.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$start
    if (-not $process.Start()) { throw 'Readiness child did not start.' }
    return $process
}
try {
    $baseline=Join-Path $ownedRoot 'baseline'; [IO.Directory]::CreateDirectory($baseline) | Out-Null
    $process=$null
    try {
        $process=Start-ReadinessChild '--baseline' $baseline
        $out=$process.StandardOutput.ReadToEndAsync(); $err=$process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(10000)) { throw $label }
        $text=$out.GetAwaiter().GetResult(); $errors=$err.GetAwaiter().GetResult()
        $lines=@($text -split '\r?\n' | Where-Object { $_.Length -gt 0 })
        if ($process.ExitCode -ne 0) {
            if ($errors -cmatch '\AFAIL: (READY_NOT_VISIBLE_UNTIL_CLOSED|READY_TRANSIENT_EPERM_RECOVERS)\r?\n?\z') { $label=$Matches[1] }
            throw $label
        }
        if (-not [string]::IsNullOrWhiteSpace($errors) -or $lines.Count -ne 4 -or
            $lines[0] -cne $helperWitness -or
            $lines[1] -cne 'PASS: READY_NOT_VISIBLE_UNTIL_CLOSED' -or
            $lines[2] -cne 'PASS: READY_TRANSIENT_EPERM_RECOVERS' -or
            $lines[3] -cne 'READY_NODE_SELFTEST=PASS checks=2') { throw $label }
        Write-Output $lines[0]
        Write-Output $lines[1]; Write-Output $lines[2]; $checks+=2
    } finally {
        if ($null -ne $process) { if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(5000) | Out-Null }; if (-not $process.HasExited) { throw 'Readiness child survived cleanup.' }; $process.Dispose() }
    }
    $label='READY_PERSISTENT_CONSUMER_DIAGNOSTIC'
    $persistent=Join-Path $ownedRoot 'persistent'; [IO.Directory]::CreateDirectory($persistent) | Out-Null
    $process=$null
    try {
        $process=Start-ReadinessChild '--persistent' $persistent
        $out=$process.StandardOutput.ReadToEndAsync()
        if (-not $process.WaitForExit(10000)) { throw $label }
        $diagnostic=Get-AgentBrokerFixtureStartupFailure $process 'persistent'
        $lines=@($out.GetAwaiter().GetResult() -split '\r?\n' | Where-Object { $_.Length -gt 0 })
        if ($process.ExitCode -ne 3 -or $diagnostic -cne 'Fixture failed to start: persistent; exit=3; stderr=FIXTURE_READY_PUBLISH_FAILED EPERM' -or
            $lines.Count -ne 1 -or $lines[0] -cne $helperWitness -or
            [IO.File]::Exists((Join-Path $persistent 'ready.json'))) { throw $label }
        Write-Output ('READY_CONSUMER_SHA256='+(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'AgentBrokerFixtureStartup.ps1') -Algorithm SHA256).Hash)
        Write-Output ('PASS: '+$label); $checks++
    } finally {
        if ($null -ne $process) { if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(5000) | Out-Null }; if (-not $process.HasExited) { throw 'Readiness child survived cleanup.' }; $process.Dispose() }
    }
} catch { $failure=$label }
finally {
    if (-not $ownedRoot.StartsWith($workspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Refused readiness cleanup outside workspace.' }
    if (Test-Path -LiteralPath $ownedRoot) { Remove-Item -LiteralPath $ownedRoot -Recurse -Force }
    if (Test-Path -LiteralPath $ownedRoot) { throw 'Readiness scratch survived cleanup.' }
}
if ($null -ne $failure) { [Console]::Error.WriteLine('FAIL: '+$failure); exit 1 }
Write-Output ('PASS: '+$checks+' fixture readiness checks; owned children and fixture files cleaned.')
