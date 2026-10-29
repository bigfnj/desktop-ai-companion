#requires -Version 5
<#
.SYNOPSIS
    Run the script-level checks that need no compiler, no install, no elevation and no network, and collect every
    failure: the installer's fake-caller, path-safety, checker-timeout and log-observation checks, and the
    public-commit privacy self-test.

.DESCRIPTION
    N-installer-tests-02 (lane feature/followups, 2026-10-29). These five ran only by hand, so the reparse-tag,
    rollback-evidence and privacy-matcher checks added by lane installer-tests guarded nothing. ONE runner, called by
    tests\run-gate.ps1 and by .github\workflows\build.yml's "Validate native installer shutdown" step, so the gate and
    CI cannot list different checks: Invoke-SelfTests.ps1 is shared the same way because two hand-kept copies drifted.

    Measured 2026-10-29 under Windows PowerShell 5.1 and PowerShell 7, each in a fresh process: 0.6 to 4.3 s apiece.
    What each leaves behind is an evidence folder under build\ (gitignored). Along the way they create and remove a
    junction under build\ and a sentinel folder under artifacts\, start one hidden synthetic PowerShell child that the
    timeout check kills after 2.5 s, create and remove a synthetic git repository under build\privacy-selftest\, and
    read the Desktop and local application data folder PATHS without opening anything in them. No MSI is executed and
    no native binary is built or launched.

    The four installer checks signal failure by throwing, so they run in this process inside a try/catch. The privacy
    self-test is `#requires -Version 7`, which Windows PowerShell refuses to run in-process, so it always runs in a
    pwsh child and is judged by its exit code; a missing pwsh is a FAILURE that says so, never a skip. Every check runs
    whatever the one before it did, and the failures are thrown together at the end, which fails a CI step and is
    collected by the gate like its other .ps1 steps.

.EXAMPLE
    .\tests\Invoke-OfflineScriptChecks.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$failures = New-Object 'Collections.Generic.List[string]'
$ran = 0

foreach ($name in @('Test-InstallerMsiCallerSafety.ps1', 'Test-InstallerNativeCheckTimeout.ps1',
                    'Test-InstallerPathSafety.ps1', 'Test-InstallerMsiLogObservation.ps1')) {
    Write-Host "=== $name"
    $ran++
    # Its result lines (the JSON verdict and the Evidence= folder) are shown, not returned: this runner's own output is
    # its verdict alone.
    try { & (Join-Path $PSScriptRoot $name) | ForEach-Object { Write-Host "$_" } }
    catch { $failures.Add($name + ': ' + $_.Exception.Message) }
}

Write-Host '=== Test-PublicCommitPrivacy.ps1 -SelfTest (in PowerShell 7)'
$ran++
$pwsh = @(Get-Command -Name 'pwsh' -CommandType Application -ErrorAction SilentlyContinue) | Select-Object -First 1
if ($null -eq $pwsh) {
    $failures.Add('Test-PublicCommitPrivacy.ps1 -SelfTest: it needs PowerShell 7, and pwsh is not on PATH, so it did not run')
}
else {
    # Judged by its EXIT CODE alone. Under Windows PowerShell a native stderr line is a terminating error while Stop is
    # set and this script's output is redirected (RA-346), as it is in a gate run detached to a log, so with Stop the
    # verdict's wording would depend on how the gate was started. Continue for this one call; restored after it.
    $privacyExit = $null
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $pwsh.Source -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-PublicCommitPrivacy.ps1') -SelfTest
        $privacyExit = $LASTEXITCODE
    }
    catch { $failures.Add('Test-PublicCommitPrivacy.ps1 -SelfTest could not be started: ' + $_.Exception.Message) }
    finally { $ErrorActionPreference = $savedPreference }
    if ($null -ne $privacyExit -and $privacyExit -ne 0) { $failures.Add("Test-PublicCommitPrivacy.ps1 -SelfTest (exit $privacyExit)") }
}

if ($failures.Count -gt 0) {
    throw ("$($failures.Count) of $ran offline script check(s) failed: " + ($failures -join ' | '))
}
Write-Host "PASS: $ran offline script checks (installer fake callers and paths, privacy self-test)."
