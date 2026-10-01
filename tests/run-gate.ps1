#requires -Version 5
<#
.SYNOPSIS
    Run the full local verification gate: build, core tests, every app self-test flag, the source-text
    invariants, and the module publish/version checks.

.DESCRIPTION
    One command so the gate is run the same way every time, and so a self-test cannot quietly report success
    without having run. That second point is not hypothetical: the module self-tests skip-PASS when their
    module folder is absent (correct behavior for a payload with no dev modules), which means a build that
    silently failed to produce modules/ looks identical to a clean run. This script fails on a SKIP.

    Mirrors .github/workflows/build.yml's flag list. The leak soak is deliberately NOT part of this gate --
    see docs/RELEASE-CHECKLIST.md; it is a pre-tag step because OS growth thresholds are too flaky to run
    on every change.

.EXAMPLE
    .\tests\run-gate.ps1
.EXAMPLE
    .\tests\run-gate.ps1 -SkipClean      # faster re-run when nothing structural changed
#>
[CmdletBinding()]
param(
    [switch]$SkipClean
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# WINDOWS POWERSHELL MUST NOT AUTOLOAD PWSH'S MODULES. A powershell.exe descended from a pwsh 7 process
# inherits pwsh's PSModulePath with the PowerShell 7 module folders FIRST, and the 5.1 engine then autoloads
# Get-FileHash from the 7-only Microsoft.PowerShell.Utility manifest it cannot run. This gate, launched
# through a shell that pwsh had started, reported `runtime-hardening-selftest.ps1: The term 'Get-FileHash'
# is not recognized` (2026-09-30): the N-fortunes-01 trap reaching the gate itself, after the mutation
# harnesses had already learnt to hand their powershell.exe children a Windows PowerShell module path.
# Which shell started the gate must not decide whether it can run, so under the Desktop edition the
# process keeps only the WindowsPowerShell entries it inherited and is guaranteed the two system defaults.
# Under pwsh this does nothing. Same block in tests/runtime-hardening-selftest.ps1, which also runs alone.
#
# RESTORED IN THE FINALLY BELOW (RA-358). `$env:` is process environment, not script scope, and the gate is
# documented as `.\tests\run-gate.ps1`, in the caller's process: a Windows PowerShell console that ran it
# kept the stripped path -- only the WindowsPowerShell folders, a developer's own module folder gone --
# for the rest of the session, with nothing connecting a later "module not found" to the gate. The
# finally runs on the `exit 1` paths as well (measured under 5.1 and 7.6: `try { exit 3 } finally {}`
# reaches the finally).
$originalModulePath = $env:PSModulePath
if ($PSVersionTable.PSEdition -eq 'Desktop') {
    $windowsModulePaths = @(($env:PSModulePath -split ';') | Where-Object { $_ -and $_ -match '(?i)windowspowershell' })
    foreach ($defaultModulePath in @((Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules'),
                                     (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\Modules'))) {
        if (-not @($windowsModulePaths | Where-Object { $_ -ieq $defaultModulePath }).Count) {
            $windowsModulePaths += $defaultModulePath
        }
    }
    $env:PSModulePath = ($windowsModulePaths -join ';')
}

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
Push-Location $repoRoot
try {
    $failures = New-Object 'Collections.Generic.List[string]'

    Write-Host '=== build (Release x64, host + modules)' -ForegroundColor Cyan
    # NOTE: never pipe build.ps1 through Select-Object -First; that short-circuits the upstream pipeline and
    # terminates the build partway, which silently skips the module builds.
    # Hashtable splat, not an array: array elements bind positionally, so '-Release' would arrive as a value
    # rather than a switch.
    $buildParams = @{ Release = $true }
    if (-not $SkipClean) { $buildParams['Clean'] = $true }
    # try/catch, NOT $LASTEXITCODE -- the same correction already applied to the three checks below,
    # for the same reason, which this call was left out of. build.ps1 sets $ErrorActionPreference='Stop'
    # and signals every failure by `throw`, so $LASTEXITCODE reads whatever the last NATIVE command left
    # behind and the branch below was dead. A build failure escaped as a raw PowerShell exception, the
    # GATE FAILED summary never printed, and no later check ran -- which is the one moment you most want
    # the summary, because it names what to look at.
    try { & (Join-Path $repoRoot 'build.ps1') @buildParams }
    catch { $failures.Add('build.ps1: ' + $_.Exception.Message) }

    $outputRoot = Join-Path $repoRoot 'build\DesktopAICompanionPortable\bin\Release\x64'
    $exe = Join-Path $outputRoot 'DesktopAICompanion.exe'
    # And this one, which had the identical defect and is reached by exactly the same failure. Fixing
    # only the call above would still let a failed build escape uncaught one line later.
    if (-not (Test-Path -LiteralPath $exe)) { $failures.Add("the built executable is missing: $exe") }
    if ($failures.Count -gt 0) {
        # Nothing below can mean anything without an executable, and running ten checks against a
        # missing exe buries the real failure under ten invented ones.
        Write-Host ''
        Write-Host 'GATE FAILED:' -ForegroundColor Red
        foreach ($failure in $failures) { Write-Host ("  - " + $failure) -ForegroundColor Red }
        exit 1
    }
    Write-Host '=== core regression tests' -ForegroundColor Cyan
    # try/catch around the NATIVE calls too (RA-359). $LASTEXITCODE covers an image that ran and failed; an
    # image that cannot START -- locked by a concurrent build in the same checkout (the F407 scenario the
    # self-test runner names), a missing dotnet -- is a terminating ApplicationFailedException out of `&`
    # under the Stop above, and it left this try through its finally with no GATE FAILED summary and every
    # later section skipped (measured under 5.1 and 7.6 by the re-audit). Five native invocations had that
    # shape: this build, the CoreTests run, the ShimejiConvert build and its two verbs; each now collects.
    $coreTestsBuilt = $false
    try {
        & dotnet build (Join-Path $repoRoot 'tests\DesktopAICompanion.CoreTests\DesktopAICompanion.CoreTests.csproj') `
            -c Release --nologo -v:minimal
        # COLLECTED, not thrown. This was the one explicit uncaught throw left in the file, inside a
        # try/finally with no catch, so a compile error in CoreTests escaped the script: the GATE FAILED
        # summary never printed and the nine checks after this point -- source invariants, module
        # freshness, atomic publish, staging safety, catalog integrity, backlog criteria, module template,
        # shimeji verify and shimeji selftest -- never ran at all. A CoreTests RUN failure below was
        # already collected; only the BUILD failure was not.
        $coreTestsBuilt = ($LASTEXITCODE -eq 0)
        if (-not $coreTestsBuilt) { $failures.Add("CoreTests build (exit $LASTEXITCODE)") }
    }
    catch { $failures.Add('CoreTests build: ' + $_.Exception.Message) }
    $coreTestsExe = Join-Path $repoRoot 'tests\DesktopAICompanion.CoreTests\bin\Release\DesktopAICompanion.CoreTests.exe'
    if ($coreTestsBuilt -and (Test-Path -LiteralPath $coreTestsExe)) {
        try {
            & $coreTestsExe
            if ($LASTEXITCODE -ne 0) { $failures.Add("CoreTests (exit $LASTEXITCODE)") }
        }
        catch { $failures.Add('CoreTests: ' + $_.Exception.Message) }
    }
    elseif ($coreTestsBuilt) { $failures.Add('CoreTests binary missing after a successful build') }

    # The two harnesses this gate never RUNS are COMPILED here (RA-340). tests\module-window-soak.ps1 and the
    # fullscreen stand-down probe need a window station and an interactive desktop, which is why they are
    # release-checklist steps rather than gate steps -- but nothing built them either: not build.ps1, not
    # build.yml, not the product .sln. So a Contracts or ModuleKit change that broke their compile was found
    # on the day they were needed, and this campaign changed the soak and the window it drives in two
    # lanes that each recorded "built, not run" while the merge was red on first use (RA-341). Compiling
    # catches the ABI drift. It does NOT catch a renamed PetStudio member: the soak resolves those by
    # reflection at run time, and its WindowDriver.Load fails loudly for that when it is run.
    Write-Host '=== out-of-gate harnesses compile (window soak, stand-down probe)' -ForegroundColor Cyan
    foreach ($harnessProject in @('tests\DesktopAICompanion.WindowSoak\DesktopAICompanion.WindowSoak.csproj',
                                  'tests\fullscreen-standdown-probe\walkcount.csproj')) {
        try {
            & dotnet build (Join-Path $repoRoot $harnessProject) -c Release --nologo -v:minimal
            if ($LASTEXITCODE -ne 0) { $failures.Add("$harnessProject build (exit $LASTEXITCODE)") }
        }
        catch { $failures.Add("$harnessProject build: " + $_.Exception.Message) }
    }

    # The flag table, the marker map and the skip detection all live in one place now, called
    # by BOTH this gate and .github\workflows\build.yml. They used to be duplicated, under a
    # comment in build.yml telling a human to keep them in sync -- and they drifted: the flag
    # lists stayed equal while the marker map and the skip detection existed only here, so CI
    # checked nothing but exit codes and ten of the eighteen flags skip-PASSED there.
    #
    # -OutputRoot makes it assert every module folder is present, because a self-test whose
    # module is missing SKIPS and exits 0. Failures are COLLECTED rather than thrown, so one
    # missing module no longer hides every check after it.
    # Filtered on the sentinel rather than taking the whole pipeline: Write-Host collapses onto
    # stdout for any caller that is not PowerShell, so an unfiltered read turns progress lines
    # into failures. Explicit beats implicit here even though this particular caller IS
    # PowerShell and would have been fine.
    # try/catch like every other .ps1 step in this file, which this call alone lacked (F407). The
    # runner reports self-test FAILURES on stdout, but its own infrastructure errors -- the exe
    # locked by a concurrent rebuild, a marker or redirect log held open -- are terminating under the
    # 'Stop' set above and escaped through `&` as a raw exception: no GATE FAILED summary, and the ten
    # sections below never ran. $selfTestOutput is initialised BEFORE the try because under
    # Set-StrictMode -Version Latest reading it unassigned below would itself throw, the trap the
    # runner's own $CountPrefix comment records.
    $selfTestOutput = @()
    $runnerAborted = $false
    try {
        $selfTestOutput = @(& (Join-Path $repoRoot 'tests\Invoke-SelfTests.ps1') `
            -ExecutablePath $exe -OutputRoot $outputRoot)
    }
    catch {
        $runnerAborted = $true
        $failures.Add('Invoke-SelfTests.ps1 aborted midway (earlier per-flag results lost): ' + $_.Exception.Message)
    }
    # The count comes back on stdout rather than being read out of the child's scope. It used to be
    # $SelfTestFlags.Count, which that script sets and this one cannot see -- see the comment beside
    # $CountPrefix there for why that broke only the PASSING run.
    $selfTestCount = 0
    foreach ($line in $selfTestOutput) {
        if ($line -like 'SELFTEST-FAILURE:*') {
            $failures.Add($line.Substring('SELFTEST-FAILURE: '.Length))
        }
        elseif ($line -like 'SELFTEST-COUNT:*') {
            $selfTestCount = [int]$line.Substring('SELFTEST-COUNT: '.Length)
        }
    }
    # A summary that can print "0 self-tests" is the same failure one level up: it would report a
    # green gate over a self-test runner that never ran anything. Not doubled up on an abort, which is
    # already in the list with its cause.
    if (-not $runnerAborted -and $selfTestCount -le 0) {
        $failures.Add('Invoke-SelfTests.ps1 reported no self-test count, so the runner did not run')
    }

    # try/catch, NOT $LASTEXITCODE, for these three. Corrected 2026-09-17 after an audit.
    # All three are .ps1 files that signal failure by `throw` (Assert-True in
    # runtime-hardening-selftest.ps1, plain throws in the two packaging checks), and
    # $LASTEXITCODE is set only by NATIVE commands or an explicit `exit`. So the old
    # `if ($LASTEXITCODE -ne 0)` form was wrong in BOTH directions:
    #
    #   On failure, the throw is terminating and $ErrorActionPreference='Stop' carried it
    #   out of this script with no catch -- so the $failures.Add never ran, the GATE FAILED
    #   summary never printed, and every later check was skipped. Those branches were dead.
    #
    #   On success, the condition read whatever the last NATIVE command left behind, which
    #   is DesktopAICompanion.CoreTests.exe (Start-Process -PassThru does not set it). So a
    #   CoreTests failure ALSO reported runtime-hardening-selftest.ps1 as failed, inventing
    #   a failure in a suite where every invariant had passed.
    Write-Host '=== source-text invariants' -ForegroundColor Cyan
    try { & (Join-Path $repoRoot 'tests\runtime-hardening-selftest.ps1') }
    catch { $failures.Add('runtime-hardening-selftest.ps1: ' + $_.Exception.Message) }

    Write-Host '=== published module payloads' -ForegroundColor Cyan
    try { & (Join-Path $repoRoot 'packaging\Test-ModulePublishFreshness.ps1') }
    catch { $failures.Add('Test-ModulePublishFreshness.ps1: ' + $_.Exception.Message) }

    # The freshness check above is about module VERSIONS. Nothing verified the catalog's asset
    # HASHES until 2026-09-17, so editing any companion or fortune pack without regenerating the
    # catalog left a recorded sha256 that no longer matched the blob raw.githubusercontent.com
    # serves -- and then the app correctly REFUSES every user's download of that asset while every
    # gate stays green. ~17s for 217 assets.
    # Every refusal in the atomic publish helper, each fed the input it exists to refuse, behind a
    # green baseline (five cases when this step was written on 2026-09-17; the suite prints one REFUSED
    # line per case, so the count lives there and not here, RA-193). It read 2 of its 11 parameters
    # until 2026-09-17 while the installer claimed it
    # enforced the seal hash, so these are exactly the checks that have to be seen failing.
    Write-Host '=== atomic publish refusals' -ForegroundColor Cyan
    try { & (Join-Path $repoRoot 'packaging\Test-AtomicPublish.ps1') }
    catch { $failures.Add('Test-AtomicPublish.ps1: ' + $_.Exception.Message) }

    # The same defect class in the same file (five more sites on 2026-09-17; the suite has grown since,
    # RA-193). Three functions declared a MANDATORY
    # -TrustedRoot and never read it -- two of them immediately ahead of Remove-Item -Recurse -Force
    # -- Copy-...ValidatedInputFile never read its mandatory -Root, and both -RejectHardLinks flags
    # were decoration. Wired in here in the SAME commit as the fix, because the MSI path this file
    # serves is not built by the gate: fixing it without a gate step would have reproduced the exact
    # defect class it closes. ~1s, all under %TEMP%.
    Write-Host '=== staging path-safety refusals' -ForegroundColor Cyan
    try { & (Join-Path $repoRoot 'packaging\Test-StagingPathSafety.ps1') }
    catch { $failures.Add('Test-StagingPathSafety.ps1: ' + $_.Exception.Message) }

    Write-Host '=== catalog integrity' -ForegroundColor Cyan
    try { & (Join-Path $repoRoot 'packaging\Test-ContentCatalogIntegrity.ps1') }
    catch { $failures.Add('Test-ContentCatalogIntegrity.ps1: ' + $_.Exception.Message) }

    # Backlog entries that state a machine-checkable closing criterion are evaluated here, because
    # three of them were found stale on 2026-09-21/22 -- each fixed by later work in the cycle that
    # filed it, none linking back. This fires in the same run that proves the fix works, while the
    # author is still looking at it.
    Write-Host '=== backlog closing criteria' -ForegroundColor Cyan
    try { & (Join-Path $repoRoot 'tests\Test-BacklogClosingCriteria.ps1') }
    catch { $failures.Add('Test-BacklogClosingCriteria.ps1: ' + $_.Exception.Message) }

    # Content invariants for shipped companions. try/catch like its neighbours: it signals
    # failure by throw, so $LASTEXITCODE would stay 0 and the gate would pass over it.
    try { & (Join-Path $repoRoot 'tests\companion-border-invariants.ps1') }
    catch { $failures.Add('companion-border-invariants.ps1: ' + $_.Exception.Message) }

    # The module template is built by nothing else, so it would rot unnoticed: this scaffolds a throwaway
    # module from it, builds it, and removes it again.
    Write-Host '=== module template' -ForegroundColor Cyan
    try { & (Join-Path $repoRoot 'packaging\Test-ModuleTemplate.ps1') -Configuration Release }
    catch { $failures.Add('Test-ModuleTemplate.ps1: ' + $_.Exception.Message) }

    # Each module's OWN self-test, through the real host loader and a real AssemblyLoadContext, for
    # all seven covered modules plus the exact known gaps of the uncovered one. This is the ONLY place
    # the module self-tests run since 2026-09-30: Invoke-SelfTests.ps1 above carried four of them as
    # well from 2026-09-17 (its comment used to claim no .ps1 or .yml contained the flag, which git
    # shows was false), so agentflow, remembrance, reminder and blinkingled ran twice per gate and per
    # push for ~12.5 s of duplicated work (F397). ~15s for all eight modules.
    Write-Host '=== module self-tests' -ForegroundColor Cyan
    try { & (Join-Path $repoRoot 'tests\Test-ModuleSelfTests.ps1') }
    catch { $failures.Add('Test-ModuleSelfTests.ps1: ' + $_.Exception.Message) }

    # The Shimeji converter's output half: grade every shipped pet with the app's REAL validator (via the
    # source-linked ShimejiConvert.Engine) and round-trip it through the DTOs. This is the emitter's
    # regression net -- it must stay all-valid and all-round-trip before any Shimeji-side parsing is trusted.
    # Not built by build.ps1 (a dev/module-shared tool, not the one shipped product), so build it here.
    Write-Host '=== shimeji converter (verify + selftest)' -ForegroundColor Cyan
    $shimejiBuilt = $false
    try {
        & dotnet build (Join-Path $repoRoot 'tools\ShimejiConvert\ShimejiConvert.csproj') `
            -c Release --nologo -v:minimal
        $shimejiBuilt = ($LASTEXITCODE -eq 0)
        if (-not $shimejiBuilt) { $failures.Add("ShimejiConvert build (exit $LASTEXITCODE)") }
    }
    catch { $failures.Add('ShimejiConvert build: ' + $_.Exception.Message) }
    # Initialised ahead of the branch that sets it: the summary line reads it, and under Set-StrictMode
    # an unassigned variable is a terminating error on the PASSING path only (the $CountPrefix lesson).
    $shimejiSuiteCount = 0
    if ($shimejiBuilt) {
        $shimejiExe = Join-Path $repoRoot 'tools\ShimejiConvert\bin\Release\ShimejiConvert.exe'
        if (-not (Test-Path -LiteralPath $shimejiExe)) {
            $failures.Add('ShimejiConvert.exe missing after build')
        }
        else {
            # Output half: every shipped pet stays valid + round-trips.
            try {
                & $shimejiExe verify (Join-Path $repoRoot 'Companions')
                if ($LASTEXITCODE -ne 0) { $failures.Add("ShimejiConvert verify (exit $LASTEXITCODE)") }
            }
            catch { $failures.Add('ShimejiConvert verify: ' + $_.Exception.Message) }
            # Input half: the parser + Group 1/2/3 classifier, AND the bundled-conf census. Both run
            # under `selftest` below -- BundledConfSelfTest asserts 91 actions as 54 Group1 / 31 Group2 /
            # 6 Group3 against the BUNDLED conf, which is the gil/shimeji-ee reference set.
            #
            # This comment used to say the census "is a dev step -- that config is copyrighted and must
            # not live in this repo", three lines above the call that runs it, and quoted the superseded
            # 53/32/6 split. It moved to 54/31/6 on 2026-08-28 when ClimbWall stopped being reported as
            # needing selfX/selfY. The code is the authority; see BundledConfSelfTest.cs:34.
            #
            # THE COUNT IS READ (N-deadcode-04). F452 made the CLI print `SELFTEST-COUNT: N` after the
            # detail, the sentinel tests\Invoke-SelfTests.ps1 already emits, so that a deleted suite
            # registration is a number that changed rather than a quieter PASS -- and then nothing read
            # the number: this gate and build.yml checked the exit code alone, so a registration deleted
            # from EngineSelfTest.RunAll still passed. The output is captured so the sentinel can be
            # parsed and re-emitted line by line so the log keeps the detail; the exit code stays the
            # verdict. stdout only, no 2>&1: under Windows PowerShell a native stderr line arriving
            # through a redirect while $ErrorActionPreference is Stop is a terminating error (RA-346),
            # and a stack trace on stderr is a nonzero exit the line above already reports.
            #
            # A FLOOR, pinned to the registrations RunAll carries today, and the pin is the point (the
            # same stance as the hardening self-test's literal limits, F292): a registration deleted from
            # RunAll fails here and has to be a deliberate edit of this number, while adding one costs
            # nothing. `-le 0` alone would only catch the sentinel going missing.
            $shimejiSuiteFloor = 9
            try {
                $shimejiSelfTestOutput = @(& $shimejiExe selftest | ForEach-Object { "$_" })
                $shimejiSelfTestExit = $LASTEXITCODE
                foreach ($line in $shimejiSelfTestOutput) { Write-Host $line }
                if ($shimejiSelfTestExit -ne 0) { $failures.Add("ShimejiConvert selftest (exit $shimejiSelfTestExit)") }
                foreach ($line in $shimejiSelfTestOutput) {
                    if ($line -like 'SELFTEST-COUNT:*') { $shimejiSuiteCount = [int]$line.Substring('SELFTEST-COUNT: '.Length) }
                }
                if ($shimejiSuiteCount -lt $shimejiSuiteFloor) {
                    $failures.Add("ShimejiConvert selftest ran $shimejiSuiteCount suite(s), below the $shimejiSuiteFloor registered in EngineSelfTest.RunAll" +
                        $(if ($shimejiSuiteCount -le 0) { ' (no SELFTEST-COUNT line at all: the suites did not run, or the sentinel moved)' } else { '; a registration was deleted, or this floor needs a deliberate edit' }))
                }
            }
            catch { $failures.Add('ShimejiConvert selftest: ' + $_.Exception.Message) }
        }
    }

    Write-Host ''
    if ($failures.Count -gt 0) {
        Write-Host "GATE FAILED:" -ForegroundColor Red
        foreach ($failure in $failures) { Write-Host "  - $failure" -ForegroundColor Red }
        exit 1
    }
    # Counted, not typed. This line said 18 while the table held 19, which is the same drift
    # the doc-count invariants now catch -- and a gate that miscounts its own coverage is the
    # least convincing place to have it.
    Write-Host ("GATE PASSED (build 0 warnings, core tests, harness compiles, $selfTestCount self-tests " +
        'with no skips, module self-tests, invariants, payloads, template, shimeji verify + ' +
        "selftest with $shimejiSuiteCount suites).") -ForegroundColor Green
}
finally {
    Pop-Location
    # See the PSModulePath block at the top (RA-358): the process keeps whatever this script put there
    # unless it is put back, and this is the one exit every path takes.
    $env:PSModulePath = $originalModulePath
}