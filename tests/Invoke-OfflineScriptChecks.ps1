#requires -Version 5
<#
.SYNOPSIS
    Run script checks with no product build, install, elevation, external network requests or real input.
    AgentBroker readiness opens loopback fixture listeners without sending requests. Collect every
    failure: the installer's fake-caller, path-safety, checker-timeout and log-observation checks, the tray icon record
    cleanup check, the public-commit privacy self-test, mutation-tooling self-tests, AgentBroker readiness/provenance checks,
    and the Shimeji parity comparer and pack generator self-tests.

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
    MSI is executed and no native binary is built or launched. The mutation TEMP-budget test compiles the existing
    small C# fixture method with Add-Type; mutation restoration tests use disposable sources and fake builds.

    AgentBroker provenance checks read a fixed public source/ZIP fixture from local Git history and run current
    provenance APIs plus an in-memory metadata reader against owned copies. They require PowerShell 7 and the
    recorded baseline commit locally, attempt no fetch, and perform no SDK build, install or payload execution.

    The mutation tooling's own checks joined on 2026-11-07 (N-audit-1106-344, N-release-180-02): the agentflow and
    diagnostics harnesses' --check-patterns, the installer-shutdown and tolerant-note runners' --self-test, and the shard
    controller's -SelfTest, remainder and TEMP-budget checks, each in a child judged by its exit code. They read the
    repository and build nothing; the TEMP-budget check creates and removes synthetic folders under TEMP. The isolated
    MSI API logging probe's facade and orchestration suites joined that PowerShell 7 list on 2026-11-08
    (N-audit-1106-367, N-audit-1106-368): in-memory fakes only, no MSI, registry, process or GUI access. Since the merge
    of master 905c03b9, the installer-shutdown --self-test and the shard controller's three checks run as master's
    summary-checked rows instead, so the two exit-code lists no longer name them.

    The bundled fortune corpus joined the same day (N-audit-1106-137): src\Fortunes\build-corpus.sh --check, run in
    Git Bash located from git itself and judged by its exit code; it writes only a staging folder beside the corpus,
    which it removes.

    The four installer checks signal failure by throwing, so they run in this process inside a try/catch. The privacy
    self-test is `#requires -Version 7`, which Windows PowerShell refuses to run in-process, so it always runs in a
    pwsh child and is judged by its exit code; a missing pwsh is a FAILURE that says so, never a skip. Every check runs
    whatever the one before it did, and the failures are thrown together at the end, which fails a CI step and is
    collected by the gate like its other .ps1 steps. The parity comparer runs through Python and must both exit zero
    and print its exact SELF-TEST PASS line. The synthetic pack generator also runs its self-test and must exit zero
    with exactly one MAKE-PACKS SELF-TEST PASS line. The selected Python must have Pillow; absence is a failure,
    never an install or a skip. -ParityOnly runs these two checks alone, so the same runner can be checked without the
    installer fixtures. -PythonExecutable (alias -PythonPath) selects the interpreter explicitly; otherwise
    TOOLBOX_PYTHON, then python on PATH, is used, and one older than 3.10 counts as none.

.EXAMPLE
    .\tests\Invoke-OfflineScriptChecks.ps1
#>
[CmdletBinding()]
param([switch]$ParityOnly, [Alias('PythonPath')][string]$PythonExecutable = '')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$failures = New-Object 'Collections.Generic.List[string]'
$ran = 0

$pwsh = @(Get-Command -Name 'pwsh' -CommandType Application -ErrorAction SilentlyContinue) | Select-Object -First 1
function Invoke-CheckedChild([string]$Name, [string]$Executable, [string[]]$Arguments, [string]$Summary) {
    Write-Host "=== $Name"
    $script:ran++
    if ([string]::IsNullOrWhiteSpace($Executable)) {
        $script:failures.Add($Name + ': required interpreter unavailable; did not run')
        return
    }
    $childExit = $null
    $output = @()
    $savedPreference = $ErrorActionPreference
    # Native stderr is nonterminating here on Windows PowerShell too, so its exit code remains available.
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& $Executable @Arguments)
        $childExit = $LASTEXITCODE
    }
    catch { $script:failures.Add($Name + ': could not start: ' + $_.Exception.GetType().Name) }
    finally { $ErrorActionPreference = $savedPreference }
    foreach ($line in $output) { Write-Host "$line" }
    if ($null -eq $childExit -or $childExit -ne 0) { $script:failures.Add("$Name (exit $childExit)") }
    elseif (@($output | Where-Object { "$_" -match $Summary }).Count -ne 1) {
        # A short plain discriminator survives either shell's formatted exception wrapping.
        Write-Output ('OFFLINE-SUMMARY-MISSING:' + $Name)
        $script:failures.Add($Name + ': missing or ambiguous passing summary; did not prove the check ran')
    }
}

$pwshPath = if ($null -eq $pwsh) { '' } else { $pwsh.Source }
if ([string]::IsNullOrWhiteSpace($PythonExecutable)) {
    $PythonExecutable = [Environment]::GetEnvironmentVariable('TOOLBOX_PYTHON', 'Process')
    if ([string]::IsNullOrWhiteSpace($PythonExecutable)) {
        $python = @(Get-Command -Name 'python' -CommandType Application -ErrorAction SilentlyContinue) | Select-Object -First 1
        $PythonExecutable = if ($null -eq $python) { '' } else { $python.Source }
    }
}
if (-not [string]::IsNullOrWhiteSpace($PythonExecutable)) {
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $PythonExecutable -B -c 'import sys; sys.exit(0 if sys.version_info >= (3, 10) else 1)'
        if ($LASTEXITCODE -ne 0) { $PythonExecutable = '' }
    }
    catch { $PythonExecutable = '' }
    finally { $ErrorActionPreference = $savedPreference }
}

if (-not $ParityOnly) {
foreach ($name in @('Test-InstallerMsiCallerSafety.ps1', 'Test-InstallerNativeCheckTimeout.ps1',
                    'Test-InstallerPathSafety.ps1', 'Test-InstallerMsiLogObservation.ps1', 'Test-TrayIconRecords.ps1')) {
    Write-Host "=== $name"
    $ran++
    # Its result lines (the JSON verdict and the Evidence= folder) are shown, not returned: this runner's own output is
    # its verdict alone.
    try { & (Join-Path $PSScriptRoot $name) | ForEach-Object { Write-Host "$_" } }
    catch { $failures.Add($name + ': ' + $_.Exception.Message) }
}

# The bundled fortune corpus against its declared inputs (N-audit-1106-137). build-corpus.sh --check rebuilds
# src\Fortunes\fortunes.txt in a staging folder beside it and compares byte for byte, so a change to packs\dadjokes.txt
# or src\Fortunes\sources\ that nobody reassembled fails here instead of shipping a corpus that disagrees with its
# inputs. Until this leg nothing ran it: the script's header said "in CI" and no gate, workflow or test called it.
# Measured 2026-11-07 on this repository: 0.7 s. Git Bash is located from git itself (git --exec-path is
# <Git>\<mingw64|ucrt64>\libexec\git-core, and bash.exe sits in <Git>\bin), never as a bare "bash": from PowerShell
# that name can resolve to System32's WSL launcher, which runs a different distribution's shell or none at all. The
# script path goes over in forward slashes because the script finds its own folder with dirname, which splits on "/"
# only. A missing Git Bash is a FAILURE that says so, never a skip; judged by the EXIT CODE, with Continue for the call
# as for the children below (RA-346).
Write-Host '=== src\Fortunes\build-corpus.sh --check (in Git Bash)'
$ran++
$gitBash = $null
$git = @(Get-Command -Name 'git' -CommandType Application -ErrorAction SilentlyContinue) | Select-Object -First 1
if ($null -ne $git) {
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $gitExecPath = [string](@(& $git.Source --exec-path 2>$null) | Select-Object -First 1)
        if ($LASTEXITCODE -eq 0 -and $gitExecPath.Trim()) {
            $gitRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $gitExecPath.Trim()))
            $bashCandidate = Join-Path $gitRoot 'bin\bash.exe'
            if (Test-Path -LiteralPath $bashCandidate -PathType Leaf) { $gitBash = $bashCandidate }
        }
    }
    catch { $gitBash = $null }
    finally { $ErrorActionPreference = $savedPreference }
}
if ($null -eq $gitBash) {
    $failures.Add('build-corpus.sh --check: it needs Git Bash (bin\bash.exe in the Git install that git --exec-path names), and none was found, so it did not run')
}
else {
    $corpusScript = (Join-Path $PSScriptRoot '..\src\Fortunes\build-corpus.sh')
    $corpusScript = [IO.Path]::GetFullPath($corpusScript).Replace('\', '/')
    $corpusExit = $null
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $gitBash $corpusScript --check 2>&1 | ForEach-Object { Write-Host "$_" }
        $corpusExit = $LASTEXITCODE
    }
    catch { $failures.Add('build-corpus.sh --check could not be started: ' + $_.Exception.Message) }
    finally { $ErrorActionPreference = $savedPreference }
    if ($null -ne $corpusExit -and $corpusExit -ne 0) {
        $failures.Add("build-corpus.sh --check (exit $corpusExit): src\Fortunes\fortunes.txt failed its check against its declared inputs (its lines above say why)")
    }
}
}

Write-Host '=== Shimeji parity comparer --self-test'
$ran++
if ([string]::IsNullOrWhiteSpace($PythonExecutable)) {
    $failures.Add('Shimeji parity comparer self-test: Python was not found, so it did not run')
}
else {
    $parityExit = $null
    $parityOutput = @()
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $parityOutput = @(& $PythonExecutable -B (Join-Path $PSScriptRoot '..\tools\ShimejiParity\comparer\compare.py') --self-test 2>&1)
        $parityExit = $LASTEXITCODE
        foreach ($line in $parityOutput) { Write-Host "$line" }
    }
    catch { $failures.Add('Shimeji parity comparer self-test could not be started: ' + $_.Exception.Message) }
    finally { $ErrorActionPreference = $savedPreference }
    # A stale or unrelated PASS line cannot hide a failing child; a zero exit with no self-test witness cannot
    # silently turn this gate into a no-op (N-audit-1106-273). Require both independently.
    if ($null -ne $parityExit) {
        if ($parityExit -ne 0) { $failures.Add("Shimeji parity comparer self-test failed (exit $parityExit)") }
        if (@($parityOutput | Where-Object { [string]$_ -ceq 'SELF-TEST PASS' }).Count -ne 1) {
            $failures.Add('Shimeji parity comparer self-test did not print exactly one SELF-TEST PASS line')
        }
    }
}

Write-Host '=== Shimeji parity pack generator --self-test'
$ran++
if ([string]::IsNullOrWhiteSpace($PythonExecutable)) {
    $failures.Add('Shimeji parity pack generator self-test: Python was not found, so it did not run')
}
else {
    $generatorExit = $null
    $generatorOutput = @()
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $pillowOutput = @(& $PythonExecutable -B -c 'from PIL import Image, ImageDraw' 2>&1)
        $pillowExit = $LASTEXITCODE
        if ($pillowExit -ne 0) {
            foreach ($line in $pillowOutput) { Write-Host "$line" }
            $failures.Add('Shimeji parity pack generator self-test needs Pillow in the selected Python; it did not run. Select an interpreter with Pillow using -PythonPath.')
        }
        else {
            $generatorOutput = @(& $PythonExecutable -B (Join-Path $PSScriptRoot '..\tools\ShimejiParity\scenarios\make_packs.py') --self-test 2>&1)
            $generatorExit = $LASTEXITCODE
            foreach ($line in $generatorOutput) { Write-Host "$line" }
        }
    }
    catch { $failures.Add('Shimeji parity pack generator self-test could not be started: ' + $_.Exception.Message) }
    finally { $ErrorActionPreference = $savedPreference }
    if ($null -ne $generatorExit) {
        if ($generatorExit -ne 0) { $failures.Add("Shimeji parity pack generator self-test failed (exit $generatorExit)") }
        if (@($generatorOutput | Where-Object { [string]$_ -ceq 'MAKE-PACKS SELF-TEST PASS' }).Count -ne 1) {
            $failures.Add('Shimeji parity pack generator self-test did not print exactly one MAKE-PACKS SELF-TEST PASS line')
        }
    }
}

# The mutation tooling's own checks (N-audit-1106-344, N-release-180-02, lane burndown/w3-tests-harness-1). Each ran only
# when someone remembered it: the two harnesses' --check-patterns (a case whose pattern matches nothing guards nothing
# and shows only as NO-OP in a whole run), the two native mutation runners' --self-test, and the shard controller's three
# synthetic checks. Measured 2026-11-07, each in a fresh process: 0.4 to 3.2 s apiece. None builds, mutates or starts the
# app; the shard checks start no child and touch no worktree. Every one is judged by its EXIT CODE, with Continue for the
# call because a native stderr line is a terminating error under Stop while this script's output is redirected (RA-346).
# A missing python or pwsh is a FAILURE that says so, never a skip. Since the merge of master 905c03b9 the installer
# runner's --self-test, the shard controller's three checks and Test-IsolatedDotnetEnvironment.ps1 run in master's
# summary-checked rows below ($powerShellChecks and the Invoke-CheckedChild calls), so the two lists no longer name them.
# Not under -ParityOnly, which runs the two parity checks alone (N-bd-w5-contracts-1-01). This block was added below the
# parity checks, outside the -not $ParityOnly block above, so -ParityOnly ran it too and crashed under StrictMode on the
# $pwsh that only that block assigns. tests\test_shimeji_parity_scripts.py's OFFLINE_RUNNER_WITNESS runs -ParityOnly and
# fails on either half.
if (-not $ParityOnly) {
$powerShellChecks = @(
    @{ Name = 'Test-AgentBrokerReleaseProvenanceRules.ps1'; Path = (Join-Path $PSScriptRoot 'Test-AgentBrokerReleaseProvenanceRules.ps1'); Args = @(); Summary = '^PROVENANCE_RULES=52 checks passed; no SDK build; owned fixture removed$' },
    @{ Name = 'Test-AgentBrokerFixtureReadiness.ps1'; Path = (Join-Path $PSScriptRoot 'Test-AgentBrokerFixtureReadiness.ps1'); Args = @(); Summary = '^PASS: 3 fixture readiness checks; owned children and fixture files cleaned\.$' },
    @{ Name = 'Test-ModulePublishCommit.ps1'; Path = (Join-Path $PSScriptRoot 'Test-ModulePublishCommit.ps1'); Args = @(); Summary = '^SUMMARY: Test-ModulePublishCommit passed [1-9][0-9]* checks; synthetic repository removed\.$' },
    @{ Name = 'Test-CatalogDeadChild.ps1'; Path = (Join-Path $PSScriptRoot 'Test-CatalogDeadChild.ps1'); Args = @(); Summary = '^SUMMARY: Test-CatalogDeadChild passed [1-9][0-9]* checks; owned children disposed\.$' },
    @{ Name = 'Test-GrantClaudeLocalMcpReadiness.ps1'; Path = (Join-Path $PSScriptRoot 'Test-GrantClaudeLocalMcpReadiness.ps1'); Args = @(); Summary = '^SUMMARY: Test-GrantClaudeLocalMcpReadiness passed [1-9][0-9]* checks; no live policy, CLI or settings access\.$' },
    @{ Name = 'Refresh-AgentBrokerStatus.ps1'; Path = (Join-Path $PSScriptRoot '..\tools\agentbroker\Refresh-AgentBrokerStatus.ps1'); Args = @('-SelfTest'); Summary = '^\{"SelfTestPassed":true,"Checks":[1-9][0-9]*\}$' },
    @{ Name = 'Test-LocalAgentBrokerHealth.ps1'; Path = (Join-Path $PSScriptRoot '..\tools\agentbroker\Test-LocalAgentBrokerHealth.ps1'); Args = @('-SelfTest'); Summary = '^PublicMetadataChecks=[1-9][0-9]*$' },
    @{ Name = 'Test-CompletedWorkerBinaryCleanup.ps1'; Path = (Join-Path $PSScriptRoot 'Test-CompletedWorkerBinaryCleanup.ps1'); Args = @(); Summary = '^\s*"Result":\s*"PASS",?$' },
    @{ Name = 'Test-BacklogCriteriaSources.ps1'; Path = (Join-Path $PSScriptRoot 'Test-BacklogCriteriaSources.ps1'); Args = @(); Summary = '^CriteriaSourceChecks=[1-9][0-9]*$' },
    @{ Name = 'Test-CodexEditorProbePreparation.ps1'; Path = (Join-Path $PSScriptRoot 'Test-CodexEditorProbePreparation.ps1'); Args = @(); Summary = '^PASS: [1-9][0-9]* preparation checks; no GUI launched\.$' },
    @{ Name = 'Test-OwnedTestTree.ps1'; Path = (Join-Path $PSScriptRoot 'Test-OwnedTestTree.ps1'); Args = @(); Summary = '^OwnedTreeChecks=[1-9][0-9]*$' },
    @{ Name = 'Test-GateDispatchContracts.ps1'; Path = (Join-Path $PSScriptRoot 'Test-GateDispatchContracts.ps1'); Args = @(); Summary = '^GateDispatchChecks=[1-9][0-9]*$' },
    @{ Name = 'Test-HarnessOutputContracts.ps1'; Path = (Join-Path $PSScriptRoot 'Test-HarnessOutputContracts.ps1'); Args = @(); Summary = '^HarnessOutputChecks=[1-9][0-9]*$' },
    @{ Name = 'Install-LocalAgentBroker.ps1'; Path = (Join-Path $PSScriptRoot '..\tools\agentbroker\Install-LocalAgentBroker.ps1'); Args = @('-SelfTest'); Summary = '^PathPolicyChecks=[1-9][0-9]*$' },
    @{ Name = 'Grant-ClaudeLocalMcpAllowance.ps1'; Path = (Join-Path $PSScriptRoot '..\tools\agentbroker\Grant-ClaudeLocalMcpAllowance.ps1'); Args = @('-SelfTest'); Summary = '^\{"SelfTestPassed":true,"Checks":[1-9][0-9]*,"LiveSettingsReadOrChanged":false,"FixtureCleaned":true\}$' },
    @{ Name = 'Inspect-ClaudeMcpPolicy.ps1'; Path = (Join-Path $PSScriptRoot '..\tools\agentbroker\Inspect-ClaudeMcpPolicy.ps1'); Args = @('-SelfTest'); Summary = '^\{"SelfTestPassed":true,"Checks":[1-9][0-9]*,"LivePolicyRead":false\}$' },
    @{ Name = 'Test-PublicCommitPrivacy.ps1'; Path = (Join-Path $PSScriptRoot 'Test-PublicCommitPrivacy.ps1'); Args = @('-SelfTest'); Summary = '^Public privacy self-test passed: [1-9][0-9]* cases ' },
    @{ Name = 'Inspect-CodexMcpExposure.ps1'; Path = (Join-Path $PSScriptRoot '..\tools\agentbroker\Inspect-CodexMcpExposure.ps1'); Args = @('-SelfTest'); Summary = '^PASS: [1-9][0-9]* exposure classification controls; no client or endpoint invoked\.$' },
    @{ Name = 'Invoke-MutationShards.ps1'; Path = (Join-Path $PSScriptRoot '..\tools\agentbroker\Invoke-MutationShards.ps1'); Args = @('-SelfTest'); Summary = '^SelfTestChecks=[1-9][0-9]*$' },
    @{ Name = 'Test-MutationShardRemainder.ps1'; Path = (Join-Path $PSScriptRoot 'Test-MutationShardRemainder.ps1'); Args = @(); Summary = '^RemainderSyntheticChecks=[1-9][0-9]*$' },
    @{ Name = 'Test-MutationTempBudget.ps1'; Path = (Join-Path $PSScriptRoot 'Test-MutationTempBudget.ps1'); Args = @(); Summary = '^Checks=[1-9][0-9]*$' },
    @{ Name = 'Test-IsolatedDotnetEnvironment.ps1'; Path = (Join-Path $PSScriptRoot 'Test-IsolatedDotnetEnvironment.ps1'); Args = @(); Summary = '^\{"Result":"PASS","Checks":[1-9][0-9]*,' },
    @{ Name = 'Test-OfflineScriptCheckDispatch.ps1'; Path = (Join-Path $PSScriptRoot 'Test-OfflineScriptCheckDispatch.ps1'); Args = @('-PythonExecutable', $PythonExecutable); Summary = '^OfflineDispatchChecks=[1-9][0-9]*$' }
)
foreach ($check in $powerShellChecks) {
    Invoke-CheckedChild $check.Name $pwshPath (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $check.Path) + $check.Args) $check.Summary
}
$node = @(Get-Command -Name 'node' -CommandType Application -ErrorAction SilentlyContinue) | Select-Object -First 1
$nodePath = if ($null -eq $node) { '' } else { $node.Source }
Invoke-CheckedChild 'Inspect-ClaudeMcpEntry.mjs' $nodePath `
    @((Join-Path $PSScriptRoot '..\tools\agentbroker\Inspect-ClaudeMcpEntry.mjs'), '--self-test') '^\{"status":"self_test_passed","checks":[1-9][0-9]*\}$'
Invoke-CheckedChild 'Probe-IsolatedClaudeMcpSetup.mjs' $nodePath `
    @((Join-Path $PSScriptRoot '..\tools\agentbroker\Probe-IsolatedClaudeMcpSetup.mjs'), '--self-test') '^\{"status":"self_test_passed","checks":[1-9][0-9]*,"fixtureRemoved":true,"publicFilesOpened":false,"cliLaunched":false\}$'
# The burn-down's installer runner prints "PASS N restoration and plan cases" (abb3401a), master's "PASS N restoration
# cases"; both spellings are the one summary (RULINGS R-2).
Invoke-CheckedChild 'run_installer_shutdown_mutations.py --self-test' $PythonExecutable `
    @('-B', '-X', 'utf8', (Join-Path $PSScriptRoot 'run_installer_shutdown_mutations.py'), '--self-test') '^PASS [1-9][0-9]* restoration (?:and plan )?cases '
Invoke-CheckedChild 'Test-MutationHarnessRestoration.py' $PythonExecutable `
    @('-B', '-X', 'utf8', (Join-Path $PSScriptRoot 'Test-MutationHarnessRestoration.py')) '^MutationRestorationChecks=[1-9][0-9]*$'
Invoke-CheckedChild 'run_mutation_capture_checks.py' $PythonExecutable `
    @('-B', '-X', 'utf8', (Join-Path $PSScriptRoot 'run_mutation_capture_checks.py')) '^MutationCaptureChecks=[1-9][0-9]*; Failures=0; Errors=0; Skipped=[0-9]+$'
# Pattern staleness (AgentBroker 1.10.5 marathon, lane T). Every guards case and every hygiene witness anchor must match
# exactly as its harness will meet it, or the case guards nothing while its harness prints one NO-OP line among thousands;
# N-audit-1106-344 found the agentflow harness's check of this kind called by nothing. Read-only, about a second each.
Invoke-CheckedChild 'mutate-selftest-guards.py --check-patterns' $PythonExecutable `
    @('-B', '-X', 'utf8', (Join-Path $PSScriptRoot 'mutate-selftest-guards.py'), '--check-patterns') '^[1-9][0-9]* cases, 0 not matching exactly once\.$'
Invoke-CheckedChild 'run_agentbroker_hygiene_mutations.py --check' $PythonExecutable `
    @('-B', '-X', 'utf8', (Join-Path $PSScriptRoot 'run_agentbroker_hygiene_mutations.py'), '--check') '^[1-9][0-9]* witnesses, 0 stale$'

$repoTests = $PSScriptRoot
# tests\test_shimeji_parity_scripts.py's offline-runner leg shares the Python loop (N-bd-w9-tests-scripts-2-01): it runs
# THIS runner's -ParityOnly path in Windows PowerShell against synthetic comparer and generator children and requires
# the right verdict for each wrong outcome, the OFFLINE_RUNNER_WITNESS among them. Nothing ran it, so when the block
# below once ran under -ParityOnly too, that witness broke and nothing reported it. Here, outside -ParityOnly, because
# the leg itself runs -ParityOnly: inside that block it would start itself again. --only=offline-runner because its
# generator leg repeats the generator self-test above. It needs powershell.exe and Pillow in python, as the parity
# checks above do. Measured 2026-11-08 in a fresh process: 23.4 s (ten powershell.exe children).
$mutationPython = @(
    @('mutate-agentflow.py', '--check-patterns'),
    @('mutate-diagnostics.py', '--check-patterns'),
    @('run_codex_tolerant_note_mutations.py', '--self-test'),
    @('test_shimeji_parity_scripts.py', '--only=offline-runner'))
# The isolated MSI API logging probe's two in-memory suites share the PowerShell 7 loop (N-audit-1106-367,
# N-audit-1106-368): both are #requires 7 (the facade test compiles its fake with Add-Type), so they cannot run in this
# process, and nothing else called them, so their checks of tools\agentbroker\Test-IsolatedMsiApiLogging.ps1 and
# Invoke-IsolatedMsiApiLoggingCase.ps1 guarded nothing unless run by hand. Each parses its source and dot-sources the
# functions it tests; neither runs a probe entry point, an MSI, a registry write or a child process.
$mutationPwsh = @(
    @('Test-IsolatedMsiApiLogging.ps1', ''),
    @('Test-IsolatedMsiApiLoggingCase.ps1', ''))
foreach ($check in $mutationPython) {
    $label = ($check[0] + ' ' + $check[1])
    Write-Host "=== $label"
    $ran++
    if ([string]::IsNullOrWhiteSpace($PythonExecutable)) { $failures.Add("${label}: it needs Python 3.10 or newer, and none was found, so it did not run"); continue }
    $checkExit = $null
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $PythonExecutable -X utf8 (Join-Path $repoTests $check[0]) $check[1] 2>&1 | ForEach-Object { Write-Host "$_" }
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
}

if ($failures.Count -gt 0) {
    throw ("$($failures.Count) of $ran offline script check(s) failed: " + ($failures -join ' | '))
}
Write-Host "PASS: $ran offline script checks."
