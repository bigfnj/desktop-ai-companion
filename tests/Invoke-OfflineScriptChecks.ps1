#requires -Version 5
<#
.SYNOPSIS
    Run the script-level checks that need no compiler, no install, no elevation and no network, and collect every
    failure: the installer's fake-caller, path-safety, checker-timeout and log-observation checks, the tray icon record
    cleanup check, and the public-commit privacy self-test.

.DESCRIPTION
    N-installer-tests-02 (lane feature/followups, 2026-10-29). These five ran only by hand, so the reparse-tag,
    rollback-evidence and privacy-matcher checks added by lane installer-tests guarded nothing. ONE runner, called by
    tests\run-gate.ps1 and by .github\workflows\build.yml's "Validate native installer shutdown" step, so the gate and
    CI cannot list different checks: Invoke-SelfTests.ps1 is shared the same way because two hand-kept copies drifted.

    Measured 2026-10-29 under Windows PowerShell 5.1 and PowerShell 7, each in a fresh process: 0.6 to 4.3 s apiece.
    What each leaves behind is an evidence folder under build\ (gitignored). Along the way they create and remove a
    junction under build\ and a sentinel folder under artifacts\, start one hidden synthetic PowerShell child that the
    timeout check kills after 2.5 s, create and remove a synthetic git repository under build\privacy-selftest\, and
    read the Desktop and local application data folder PATHS without opening anything in them. Test-TrayIconRecords.ps1
    (added 2026-11-02) creates and removes a scratch key under HKCU\Software and never opens the real tray records. No
    MSI is executed and no native binary is built or launched.

    The mutation tooling's own checks joined on 2026-11-07 (N-audit-1106-344, N-release-180-02): the agentflow and
    diagnostics harnesses' --check-patterns, the installer-shutdown and tolerant-note runners' --self-test, and the shard
    controller's -SelfTest, remainder and TEMP-budget checks, each in a child judged by its exit code. They read the
    repository and build nothing; the TEMP-budget check creates and removes synthetic folders under TEMP.

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
                    'Test-InstallerPathSafety.ps1', 'Test-InstallerMsiLogObservation.ps1', 'Test-TrayIconRecords.ps1')) {
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

# The mutation tooling's own checks (N-audit-1106-344, N-release-180-02, lane burndown/w3-tests-harness-1). Each ran only
# when someone remembered it: the two harnesses' --check-patterns (a case whose pattern matches nothing guards nothing
# and shows only as NO-OP in a whole run), the two native mutation runners' --self-test, and the shard controller's three
# synthetic checks. Measured 2026-11-07, each in a fresh process: 0.4 to 3.2 s apiece. None builds, mutates or starts the
# app; the shard checks start no child and touch no worktree. Every one is judged by its EXIT CODE, with Continue for the
# call because a native stderr line is a terminating error under Stop while this script's output is redirected (RA-346).
# A missing python or pwsh is a FAILURE that says so, never a skip. Test-IsolatedDotnetEnvironment.ps1 is not here:
# build.yml's "Check test environment and artifact cleanup safety" step runs it.
$repoTests = $PSScriptRoot
$mutationPython = @(
    @('mutate-agentflow.py', '--check-patterns'),
    @('mutate-diagnostics.py', '--check-patterns'),
    @('run_installer_shutdown_mutations.py', '--self-test'),
    @('run_codex_tolerant_note_mutations.py', '--self-test'))
$mutationPwsh = @(
    @('..\tools\agentbroker\Invoke-MutationShards.ps1', '-SelfTest'),
    @('Test-MutationShardRemainder.ps1', ''),
    @('Test-MutationTempBudget.ps1', ''))
$python = @(Get-Command -Name 'python' -CommandType Application -ErrorAction SilentlyContinue) | Select-Object -First 1
foreach ($check in $mutationPython) {
    $label = ($check[0] + ' ' + $check[1])
    Write-Host "=== $label"
    $ran++
    if ($null -eq $python) { $failures.Add("${label}: it needs Python 3, and python is not on PATH, so it did not run"); continue }
    $checkExit = $null
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $python.Source -X utf8 (Join-Path $repoTests $check[0]) $check[1] 2>&1 | ForEach-Object { Write-Host "$_" }
        $checkExit = $LASTEXITCODE
    }
    catch { $failures.Add("$label could not be started: " + $_.Exception.Message) }
    finally { $ErrorActionPreference = $savedPreference }
    if ($null -ne $checkExit -and $checkExit -ne 0) { $failures.Add("$label (exit $checkExit)") }
}
foreach ($check in $mutationPwsh) {
    $label = ((Split-Path -Leaf $check[0]) + ' ' + $check[1]).Trim()
    Write-Host "=== $label (in PowerShell 7)"
    $ran++
    if ($null -eq $pwsh) { $failures.Add("${label}: it needs PowerShell 7, and pwsh is not on PATH, so it did not run"); continue }
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repoTests $check[0]))
    if ($check[1]) { $arguments += $check[1] }
    $checkExit = $null
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $pwsh.Source @arguments 2>&1 | ForEach-Object { Write-Host "$_" }
        $checkExit = $LASTEXITCODE
    }
    catch { $failures.Add("$label could not be started: " + $_.Exception.Message) }
    finally { $ErrorActionPreference = $savedPreference }
    if ($null -ne $checkExit -and $checkExit -ne 0) { $failures.Add("$label (exit $checkExit)") }
}

if ($failures.Count -gt 0) {
    throw ("$($failures.Count) of $ran offline script check(s) failed: " + ($failures -join ' | '))
}
Write-Host "PASS: $ran offline script checks (installer fake callers and paths, tray icon records, privacy self-test, mutation tooling)."
