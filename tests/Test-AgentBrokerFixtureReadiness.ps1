#requires -Version 7
<# Atomic and transient families pair helper contracts with the process fixture; persistent checks the real fixture and classifier. #>
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$checks=0; $failure=$null; $ownedRoot=$null
$utf8=[Text.UTF8Encoding]::new($false,$true)
$known='READY_NOT_VISIBLE_UNTIL_CLOSED|READY_TRANSIENT_EPERM_RECOVERS|READY_PERSISTENT_CONSUMER_DIAGNOSTIC'
function Assert-Readiness([bool]$Value,[string]$Label) { if (-not $Value) { throw ('READINESS_ASSERT:'+$Label) } }
function Start-ReadinessChild([string]$Runner,[string]$Mode,[string]$Root) {
    $start=[Diagnostics.ProcessStartInfo]::new($node)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.StandardOutputEncoding=$utf8; $start.StandardErrorEncoding=$utf8
    foreach ($argument in @($Runner,$Mode,$Root)) { $start.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$start
    if (-not $process.Start()) { $process.Dispose(); throw 'READY_CHILD_START_FAILED' }
    return $process
}
function Stop-ReadinessChild($Process) {
    if ($null -eq $Process) { return }
    try {
        if (-not $Process.HasExited) { $Process.Kill($true); [void]$Process.WaitForExit(5000) }
        if (-not $Process.HasExited) { throw 'READY_CHILD_CLEANUP_FAILED' }
    } finally { $Process.Dispose() }
}
function Assert-ChildExit($Process,[string]$Errors,[int]$Expected) {
    if ($Process.ExitCode -eq $Expected) { return }
    if ($Errors -cmatch ('\AFAIL: ('+$known+')\r?\n?\z')) { throw ('READINESS_ASSERT:'+$Matches[1]) }
    throw 'READY_CHILD_UNEXPECTED_EXIT'
}
try {
    . (Join-Path $PSScriptRoot 'AgentBrokerFixtureStartup.ps1')
    $node=@(Get-Command node.exe -CommandType Application -ErrorAction Stop)[0].Source
    $unit=Join-Path $PSScriptRoot 'agentbroker-fixture-ready.test.mjs'
    $integration=Join-Path $PSScriptRoot 'agentbroker-fixture-ready.integration.mjs'
    $helperWitness='READY_HELPER_SHA256='+((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'agentbroker-fixture-ready.mjs') -Algorithm SHA256).Hash.ToLowerInvariant())
    $fixtureWitness='READY_FIXTURE_SHA256='+((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'agentbroker-collaboration-process-fixture.mjs') -Algorithm SHA256).Hash.ToLowerInvariant())
    $workspace=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
    $ownedRoot=[IO.Path]::GetFullPath((Join-Path $workspace ('artifacts\fixture-readiness\'+[Guid]::NewGuid().ToString('N'))))
    if (-not $ownedRoot.StartsWith($workspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'READY_SCRATCH_SCOPE' }
    [void][IO.Directory]::CreateDirectory($ownedRoot)
    $process=$null
    try {
        $process=Start-ReadinessChild $unit '--baseline' $ownedRoot
        $out=$process.StandardOutput.ReadToEndAsync(); $err=$process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(10000)) { throw 'READY_CHILD_TIMEOUT' }
        $text=$out.GetAwaiter().GetResult(); $errors=$err.GetAwaiter().GetResult()
        $lines=@($text -split '\r?\n' | Where-Object { $_.Length -gt 0 })
        if ($lines.Count -gt 0 -and $lines[0] -ceq $helperWitness) { Write-Output $helperWitness }
        Assert-ChildExit $process $errors 0
        Assert-Readiness ([string]::IsNullOrWhiteSpace($errors) -and $lines.Count -eq 4 -and
            $lines[0] -ceq $helperWitness -and $lines[1] -ceq 'PASS: READY_NOT_VISIBLE_UNTIL_CLOSED' -and
            $lines[2] -ceq 'PASS: READY_TRANSIENT_EPERM_RECOVERS' -and $lines[3] -ceq 'READY_NODE_SELFTEST=PASS checks=2') 'READY_NOT_VISIBLE_UNTIL_CLOSED'
    } finally { Stop-ReadinessChild $process }
    foreach ($case in @(@{mode='--atomic';label='READY_NOT_VISIBLE_UNTIL_CLOSED'},@{mode='--transient';label='READY_TRANSIENT_EPERM_RECOVERS'})) {
        $directory=Join-Path $ownedRoot $case.mode.Substring(2); [void][IO.Directory]::CreateDirectory($directory)
        $process=$null
        try {
            $process=Start-ReadinessChild $integration $case.mode $directory
            $out=$process.StandardOutput.ReadToEndAsync(); $err=$process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(10000)) { throw 'READY_CHILD_TIMEOUT' }
            $text=$out.GetAwaiter().GetResult(); $errors=$err.GetAwaiter().GetResult()
            $lines=@($text -split '\r?\n' | Where-Object { $_.Length -gt 0 })
            if ($checks -eq 0 -and $lines.Count -gt 1 -and $lines[0] -ceq $helperWitness -and $lines[1] -ceq $fixtureWitness) { Write-Output $fixtureWitness }
            Assert-ChildExit $process $errors 0
            Assert-Readiness ([string]::IsNullOrWhiteSpace($errors) -and $lines.Count -eq 3 -and
                $lines[0] -ceq $helperWitness -and $lines[1] -ceq $fixtureWitness -and $lines[2] -ceq ('PASS: '+$case.label)) $case.label
            Write-Output ('PASS: '+$case.label); $checks++
        } finally { Stop-ReadinessChild $process }
    }
    $persistent=Join-Path $ownedRoot 'persistent'; [void][IO.Directory]::CreateDirectory($persistent)
    $process=$null
    try {
        $process=Start-ReadinessChild $integration '--persistent' $persistent
        $out=$process.StandardOutput.ReadToEndAsync()
        if (-not $process.WaitForExit(10000)) { throw 'READY_CHILD_TIMEOUT' }
        if ($process.ExitCode -ne 3) { Assert-ChildExit $process ($process.StandardError.ReadToEnd()) 3 }
        $diagnostic=Get-AgentBrokerFixtureStartupFailure $process 'persistent'
        $lines=@($out.GetAwaiter().GetResult() -split '\r?\n' | Where-Object { $_.Length -gt 0 })
        Assert-Readiness ($diagnostic -ceq 'Fixture failed to start: persistent; exit=3; stderr=FIXTURE_READY_PUBLISH_FAILED EPERM' -and
            $lines.Count -eq 2 -and $lines[0] -ceq $helperWitness -and $lines[1] -ceq $fixtureWitness -and
            -not [IO.File]::Exists((Join-Path $persistent 'ready.json'))) 'READY_PERSISTENT_CONSUMER_DIAGNOSTIC'
        Write-Output ('READY_CONSUMER_SHA256='+(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'AgentBrokerFixtureStartup.ps1') -Algorithm SHA256).Hash)
        Write-Output 'PASS: READY_PERSISTENT_CONSUMER_DIAGNOSTIC'; $checks++
    } finally { Stop-ReadinessChild $process }
} catch {
    $message=$_.Exception.Message
    $failure=if ($message -cmatch ('\AREADINESS_ASSERT:('+$known+')\z')) { $Matches[1] } else { 'READY_UNEXPECTED_EXECUTION' }
} finally {
    try {
        if ($null -ne $ownedRoot) {
            if (-not $ownedRoot.StartsWith($workspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'READY_CLEANUP_SCOPE' }
            if (Test-Path -LiteralPath $ownedRoot) { Remove-Item -LiteralPath $ownedRoot -Recurse -Force }
            if (Test-Path -LiteralPath $ownedRoot) { throw 'READY_SCRATCH_REMAINS' }
        }
    } catch { $failure='READY_UNEXPECTED_EXECUTION' }
}
if ($null -ne $failure) { [Console]::Error.WriteLine('FAIL: '+$failure); exit 1 }
Write-Output ('PASS: '+$checks+' fixture readiness checks; owned children and fixture files cleaned.')
