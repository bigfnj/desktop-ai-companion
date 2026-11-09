#requires -Version 5
<#
.SYNOPSIS
    Run every built module's --module-selftest through the REAL host loader, and refuse to let the
    uncovered set grow in silence.

.DESCRIPTION
    Added 2026-09-27 to run EVERY built module's self-test and to refuse a growing uncovered set.
    (Its first header said no .ps1 or .yml in the repo contained "--module-selftest=agentflow" and
    that 684 module assertions ran only by hand. git says otherwise: Invoke-SelfTests.ps1 had carried
    four --module-selftest rows since 2026-09-17, so those four ran in the gate and CI already, and
    then ran a second time through this file, ~12.5 s of duplicated work per run. The rows left
    Invoke-SelfTests.ps1 on 2026-09-30 and this file is the one runner for them; F397.) AgentFlow
    alone accounts for 478 of the assertions, on the module the owner uses every day.

    Two halves, and the second is the one that keeps this honest:

      COVERED   must report RESULT=PASS, with tagged assertion evidence and NO SKIP: line.
                Evidence is individual PASS: lines or the module's own "N checks passed" total.
                Recorded module-specific floors also refuse an assertion inventory that shrank.
                These are the modules whose module class exposes
                `static bool SelfTest(out string detail)`, so the host runs their own assertions.
                All three rules are graded: a PASS carrying no assertions is a check that ran nothing,
                and a PASS carrying a SKIP ran less than it claims. SelfTestProbe.Skip() promises "the
                gate FAILS on a SKIP line"; until 2026-09-29 this runner did not, while AiBrain's probe
                already writes SKIP: for a missing DPAPI store or OCR recognizer (F419).

      UNCOVERED must fail, and fail for the EXACT known reasons. testmodule is a deliberately
                minimal ABI fixture: the host reports "the module exposes static bool SelfTest(out
                string detail)" and stops. Listing it as an expected failure rather than skipping it
                means the day somebody adds a SelfTest, this gate goes RED and says to move the id
                into COVERED. A skip list would have quietly kept it uncovered forever, which is the
                failure mode this whole file is a response to.

                The lists are the authority, not this paragraph: it named aibrain and fortunes as
                uncovered for a day after both gained a module-class SelfTest and moved to COVERED,
                and petstudio for two days after its checks needed only a fixture the module could
                carry itself (embedded 2026-09-29), which is exactly the drift the counts elsewhere
                in the repo are asserted against.

    The union is also checked against the modules actually built, so a NEW module cannot arrive
    uncatalogued by either list.

    Every child runs with a PRIVATE TEMP. The marker's name is fixed by the flag and the exe writes it
    under Path.GetTempPath(), so two same-user runners on one box -- the gate in the main checkout and
    a worktree's gate or a mutation harness -- shared one path and could grade each other's build in
    the ~10-35 ms between the exe's write and this script's read (F418, measured 2026-09-29). This
    script creates one directory per run, hands it to the child as TEMP/TMP, reads the marker from it
    and removes it at the end.

    Cost: each module's wall time is printed beside its OK line, so the figure is re-measured on every
    run instead of being restated here (the per-module list this header carried said fortunes 1.6 s
    while its self-test had grown to over half a minute). A run that fails keeps its private TEMP
    and writes environment.txt beside the markers (see Save-RunEnvironment).
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    # The limit for every module that has none of its own in $ModuleTimeoutSeconds below.
    [int]$TimeoutSeconds = 180,
    # All built ids must remain catalogued; a scoped run requires only its selected ids built.
    [string[]]$Only = @()
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'HarnessOutput.ps1')

# Resolved AFTER the param block, not as a default. Under Windows PowerShell 5.1 $PSScriptRoot is
# empty while parameter defaults are being bound, so `Split-Path -Parent $PSScriptRoot` there throws
# on an empty string -- the script ran fine under pwsh 7 and died immediately under 5.1, which is
# exactly the divergence the shell-parity checks exist to catch and cannot see from a parse.
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $scriptDirectory = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        $PSScriptRoot
    }
    else {
        Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    $RepoRoot = Split-Path -Parent $scriptDirectory
}

$Covered = @('agentbroker', 'agentflow', 'aibrain', 'blinkingled', 'fortunes', 'petstudio', 'reminder', 'remembrance')

# A MODULE'S OWN LIMIT, where the shared one is too close to its measured time (N-release-150-02). Fortunes' self-test
# runs the whole engine probe (FortuneEngineProbe.Run: the embedder, the smart index, the filter and importer suites) and is
# CPU-bound over a dozen threads, so its wall time is whatever the rest of the machine leaves it. Measured: 187 s on the
# 1.5.0 branch and 174 s on master's 1.4.0 build, single runs on 2026-10-07 while a game ran, and the first timed this
# runner out at 180 s; on 2026-10-26, three fresh runs of --module-selftest=fortunes took 34.3, 32.4 and 34.5 s (102-106 CPU
# seconds each, the box 50-70% busy with other work), and one of ten --fortunes-engine-selftest runs beside them took 69.9 s
# against 30-36 s for the other nine. 300 s is 1.6 times the slowest recorded run. Every other module keeps the shared
# limit, which is not a claim that they are quick: in the same day's whole run of this file, on the same busy box,
# agentflow took 36.7 s, aibrain 39.8, remembrance 42.5 and fortunes 39.5, but only Fortunes has been recorded near the
# limit. The OK line prints each module's time against its limit on every run, so the next one to approach it is seen
# before it times out. A caller's larger -TimeoutSeconds still wins (Get-ModuleTimeoutSeconds), so this can only lengthen
# a module's limit, never shorten it below what the caller asked for.
$ModuleTimeoutSeconds = @{ 'fortunes' = 300 }
$moduleAssertionFloors = @{ 'agentbroker' = 484 }

# The EXACT set of failures each uncovered module is expected to report -- not merely "it failed",
# and not just its first failure. The first draft of this file matched the first FAIL line only, and
# testmodule promptly proved why that is not enough: its first failure is the missing SelfTest, and
# it has a SECOND unrelated one (a tray entry with no icon) that the loose match absorbed in silence.
# A list of expected failures that can swallow an unexpected one is the same defect as a skip list.
$Uncovered = [ordered]@{
    # petstudio left this list on 2026-09-29. Its in-module checks take a pet XML the host used to
    # supply from its own resources, so covering it meant EMBEDDING a fixture
    # (modules/PetStudio/Resources/selftest-companion.xml, the bundled graph with a placeholder
    # sheet) rather than adding a delegation -- unlike aibrain and fortunes, which already had probes
    # with the exact signature and only lacked the entry point.
    # Dev-only, never published, and deliberately minimal: it exists to exercise the ABI, so its
    # unadorned tray entry is the fixture behaving as designed rather than a defect to chase.
    'testmodule'  = @('the module exposes static bool SelfTest(out string detail)',
                      "every tray entry it registered has its own unique icon -- tray entry 0 ('TestModule OK') has no icon")
}

# REAL INPUT, ON REQUEST (2026-10-14, the owner's decision "Skip on agent runs"). With this variable set to 1, the
# Blinking LED self-test makes no real Scroll Lock press: each check that would press prints one line starting
# "SKIPPED: real input disabled by DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT=1;" instead, and its run asserts that
# the engine's real-press counter stayed 0. Such a line is the one skip this runner accepts, and only when THIS run
# asked for it: it is printed beside the module's OK and passed on to the gate's summary as a SELFTEST-NOT-RUN: line,
# the way a known gap is visible without being a failure. The same line in a run that did NOT ask is a module skipping
# real input on its own, which is a problem; and any other line starting SKIPPED: is graded as the SKIP it is, so the
# new word is not a way round the SKIP: rule. Unset (CI, a normal run), the real press is made and tested.
$noRealInputVariable = 'DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT'
$noRealInputRequested = ("$([Environment]::GetEnvironmentVariable($noRealInputVariable))".Trim() -ceq '1')
$realInputSkipPattern = '^\s*(\[[^\]]*\]\s*)?SKIPPED: real input disabled by DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT=1;'
$notRunCount = 0
# LOUD AND CHECKED, not merely accepted (the second fix round). With the variable at 1, each module that presses a real key
# must show, on this run, EXACTLY the real-input SKIPPED lines listed here, the PASS line of its own real-press counter, and
# a record of the process's counts that it left after every one of its instances had shut down, the convention runner's
# own included, which the loader shuts down after the module's self-test has returned. A module not listed skips nothing
# for real input. "no real input was sent" is printed only from those three, measured (Get-RealInputMeasurement), so a
# module that stops honouring the variable reads as the problem it is, not as a pass. Blinking LED's record is
# BlinkingLedModule.RecordRealInputAfterShutdown: "realKeypresses=N withheld=N started=N shutDown=N".
$realInputModules = @{
    'blinkingled' = [pscustomobject]@{
        Skipped = @('the real SendInput call completes without throwing',
                    "the suite's real keypresses are paired, so this instance leaves Scroll Lock where the machine had it")
        Counter = "with real input withheld, the engine's real-press counter stayed 0 through every instance this self-test ran"
    }
}
$realInputRecordPattern = '^realKeypresses=(\d+) withheld=(\d+) started=(\d+) shutDown=(\d+)$'
$noRealInputMeasured = @()

function Select-ModuleTestIds([string[]]$Requested, [string[]]$Known) {
    $selected = @($Requested | ForEach-Object {
        if ([string]::IsNullOrWhiteSpace($_)) { throw 'Test-ModuleSelfTests.ps1: module selection contains an empty id.' }
        $_.ToLowerInvariant()
    } | Sort-Object -Unique)
    if ($selected.Count -eq 0 -or @($selected | Where-Object { $Known -notcontains $_ }).Count -gt 0) {
        throw 'Test-ModuleSelfTests.ps1: module selection must name known built modules.'
    }
    return $selected
}

$exe = Join-Path $RepoRoot 'build\DesktopAICompanionPortable\bin\Release\x64\DesktopAICompanion.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "The Release host is not built, so no module self-test can run: $exe"
}

$modulesRoot = Join-Path $RepoRoot 'build\DesktopAICompanionPortable\bin\Release\x64\modules'
$built = @(Get-ChildItem -LiteralPath $modulesRoot -Directory -ErrorAction SilentlyContinue |
           ForEach-Object { $_.Name.ToLowerInvariant() } | Sort-Object)
if ($built.Count -eq 0) { throw "No built modules found under $modulesRoot" }

# A NEW module must land in one list or the other. Without this the file would keep passing while
# quietly testing a shrinking fraction of what ships.
$known = @($Covered + @($Uncovered.Keys) | Sort-Object)
$selected = if ($Only.Count -gt 0) { @(Select-ModuleTestIds $Only $known) } else { $known }
$unlisted = @($built | Where-Object { $known -notcontains $_ })
$missing = @($selected | Where-Object { $built -notcontains $_ })
if ($unlisted.Count -gt 0) {
    throw ("Built module(s) appear in neither the COVERED nor the UNCOVERED list in this file: " +
           ($unlisted -join ', ') + ". Add a SelfTest and put it in COVERED, or record it in UNCOVERED.")
}
if ($missing.Count -gt 0) {
    throw ("This file lists module(s) that are not built: " + ($missing -join ', ') +
           '. Remove them, or find out why build.ps1 stopped producing them.')
}

if ($Only.Count -gt 0) {
    $Covered = @($Covered | Where-Object { $selected -contains $_ })
    $selectedUncovered = [ordered]@{}
    foreach ($id in $Uncovered.Keys) {
        if ($selected -contains $id) { $selectedUncovered[$id] = $Uncovered[$id] }
    }
    $Uncovered = $selectedUncovered
    Write-Host ('MODULE SELECTION: ' + ($selected -join ', ') + '; built ids are catalogued and all selected modules are built.')
}

function Get-ModuleTimeoutSeconds([string]$Id) {
    if ($ModuleTimeoutSeconds.ContainsKey($Id)) { return [Math]::Max([int]$ModuleTimeoutSeconds[$Id], $TimeoutSeconds) }
    return $TimeoutSeconds
}

function Get-AgentBrokerArtifactSnapshot([string]$ModulesRoot) {
    $moduleRoot = Join-Path $ModulesRoot 'agentbroker'
    $assembly = Join-Path $moduleRoot 'AgentBroker.dll'
    $workerRoot = Join-Path $moduleRoot 'worker'
    if (-not [IO.File]::Exists($assembly) -or
        -not [IO.File]::Exists((Join-Path $workerRoot 'AgentBroker.Worker.exe')) -or
        -not [IO.File]::Exists((Join-Path $workerRoot 'AgentBroker.Worker.dll'))) {
        throw 'Test-ModuleSelfTests.ps1: MODULE_ARTIFACT_INPUT_MISSING: AgentBroker and its complete Worker payload must be built.'
    }
    # Match ModuleConventionSelfTest.WorkerPayloadSha256 exactly. This binds the copied payload witness,
    # not Worker execution: ordinal full-path order, relative slash path, NUL, uppercase file SHA, LF, UTF-8.
    $prefix = [IO.Path]::GetFullPath($workerRoot).TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)) + [IO.Path]::DirectorySeparatorChar
    [string[]]$files = [IO.Directory]::GetFiles($prefix, '*', [IO.SearchOption]::AllDirectories)
    [Array]::Sort($files, [StringComparer]::Ordinal)
    $manifest = New-Object Text.StringBuilder
    foreach ($file in $files) {
        $relative = $file.Substring($prefix.Length).Replace([IO.Path]::DirectorySeparatorChar, [char]'/')
        [void]$manifest.Append($relative).Append([char]0).Append((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash).Append([char]10)
    }
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $payloadHash = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($manifest.ToString()))).Replace('-', '') }
    finally { $hash.Dispose() }
    return [pscustomobject]@{ LOADED = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash; WORKER_PAYLOAD = $payloadHash }
}

function Get-ModuleArtifactWitnessLines([string]$Id, [string]$Result, [string[]]$Lines, $Expected) {
    # A module that already failed keeps its original failure. Other module IDs have no new witness contract.
    if ($Id -cne 'agentbroker' -or $Result -cne 'PASS') { return }
    foreach ($kind in @('LOADED', 'WORKER_PAYLOAD')) {
        $prefix = 'WITNESS: MODULE_SELFTEST_' + $kind + '_SHA256'
        $rows = @($Lines | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) })
        if ($rows.Count -ne 1) {
            throw ('Test-ModuleSelfTests.ps1: MODULE_ARTIFACT_WITNESS_' + $kind + '_COUNT: expected exactly one artifact witness.')
        }
        $parsed = [regex]::Match($rows[0], ('^' + [regex]::Escape($prefix) + ' agentbroker ([A-F0-9]{64})\z'), [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        if (-not $parsed.Success) {
            throw ('Test-ModuleSelfTests.ps1: MODULE_ARTIFACT_WITNESS_' + $kind + '_FORMAT: expected the exact AgentBroker SHA-256 witness.')
        }
        if ($parsed.Groups[1].Value -cne $Expected.$kind) {
            throw ('Test-ModuleSelfTests.ps1: MODULE_ARTIFACT_WITNESS_' + $kind + '_MISMATCH: the self-test did not identify the expected built artifact.')
        }
        Write-Output $rows[0]
    }
}

function Assert-ModuleArtifactsUnchanged($Before, $After) {
    foreach ($kind in @('LOADED', 'WORKER_PAYLOAD')) {
        if ($Before.$kind -cne $After.$kind) {
            throw ('Test-ModuleSelfTests.ps1: MODULE_ARTIFACT_INPUT_CHANGED: ' + $kind + ' changed while the self-test ran.')
        }
    }
}

# Each module's wall time and limit, by id, for its OK line and for environment.txt on a failure.
$moduleTimings = [ordered]@{}

function Invoke-ModuleSelfTest([string]$Id) {
    # $env:TEMP is this run's private directory by the time this runs (see below), and the child
    # inherits it, so the exe's Path.GetTempPath() and this path agree.
    $marker = Join-Path $env:TEMP "dp-module-$Id-selftest.txt"

    # [IO.File]::Delete, not Remove-Item, and the result is CHECKED. Remove-Item performs ~
    # home-directory expansion even under -LiteralPath, so it fails outright when the temp path
    # contains a tilde -- the norm on Windows whenever the account name exceeds 8 characters and TEMP
    # holds the 8.3 short form -- and this call carried -ErrorAction SilentlyContinue, which swallowed
    # that plus any lock or ACL failure. Invoke-SelfTests.ps1 already removed exactly this call for
    # exactly this reason. Delete is a no-op on a missing file, so no Test-Path guard.
    try { [System.IO.File]::Delete($marker) } catch { }
    if (Test-Path -LiteralPath $marker -PathType Leaf) {
        return [pscustomobject]@{ Id = $Id; Result = 'STALEMARKER'; Lines = @(); Skips = @(); RealInputSkips = @(); RealInputRecord = $null
                                  Reasons = @("the previous marker could not be deleted, so any verdict would be the previous run's: $marker") }
    }
    # The module's real-input record (see $realInputModules), deleted the same way and for the same reason: a record left
    # by an earlier run would be read as this one's.
    $record = Join-Path $env:TEMP "dp-module-$Id-realinput.txt"
    try { [System.IO.File]::Delete($record) } catch { }
    if (Test-Path -LiteralPath $record -PathType Leaf) {
        return [pscustomobject]@{ Id = $Id; Result = 'STALEMARKER'; Lines = @(); Skips = @(); RealInputSkips = @(); RealInputRecord = $null
                                  Reasons = @("the previous real-input record could not be deleted, so it would be read as this run's: $record") }
    }

    # The module's own limit when it has one, the shared one otherwise; timed, so the OK line and a kept run's
    # environment.txt say how close a module came to it.
    $limit = Get-ModuleTimeoutSeconds $Id
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $exe -ArgumentList "--module-selftest=$Id" -PassThru -WindowStyle Hidden
    $exited = $process.WaitForExit($limit * 1000)
    $watch.Stop()
    $moduleTimings[$Id] = [pscustomobject]@{ Seconds = [Math]::Round($watch.Elapsed.TotalSeconds, 1); Limit = $limit; Exited = $exited }
    if (-not $exited) {
        try { $process.Kill() } catch { }
        # The module AND its limit, and whose limit it is: a timeout under the shared limit and one under a module's own
        # are read differently (the first may be load, the second is past a margin that was measured for it).
        $whose = if ($ModuleTimeoutSeconds.ContainsKey($Id)) { "its own limit; the shared one is ${TimeoutSeconds}s" } else { 'the shared limit' }
        return [pscustomobject]@{ Id = $Id; Result = 'TIMEOUT'; Lines = @(); Skips = @(); RealInputSkips = @(); RealInputRecord = $null
                                  Reasons = @("$Id did not exit within its ${limit}s limit ($whose)") }
    }
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
        # The marker is the ONLY evidence the module was actually loaded and exercised. An exit code
        # of 0 with no marker means the run did not happen, which must never read as a pass.
        return [pscustomobject]@{ Id = $Id; Result = 'NOMARKER'; Lines = @(); Skips = @(); RealInputSkips = @(); RealInputRecord = $null; Reasons = @('no marker file was written') }
    }

    # NO TIMESTAMP CHECK HERE, deliberately, and this is the reasoning so nobody re-derives it.
    # A marker-age comparison reads like the obvious second line of defence, and it cannot fire once
    # the delete above is asserted: the only ways a run leaves no fresh marker are Finish's write
    # throwing (no file at all -> NOMARKER; the writer is a plain File.WriteAllText inside a
    # swallowing catch, not a create-then-rename, so a sharing violation leaves nothing behind
    # either), or two runners racing on the same TEMP path, where BOTH files are recent so an age
    # test is blind to it anyway -- which is why the runner now owns a private TEMP per run instead.
    # A check no input can fail is the defect this repo keeps finding, so it is not shipped.
    # Asserting the delete is what closes the real hole -- Remove-Item failing silently on a
    # tilde-bearing TEMP, a lock, or an ACL, and a previous RESULT=PASS then being graded as this run.
    $lines = @(Get-Content -LiteralPath $marker -Encoding UTF8)
    $verdict = @($lines | Where-Object { $_ -match '^RESULT=' } | Select-Object -Last 1)
    $result = if ($verdict.Count) { ($verdict[0] -replace '^RESULT=', '').Trim() } else { 'NOVERDICT' }
    # EVERY failure, not the first. See the note on $Uncovered.
    #
    # BOTH SHAPES. The host writes "FAIL: <check>" for its own convention checks, and a module's own
    # assertions arrive as "  [<id>] FAIL: <assertion>". Matching only the first meant a module
    # failure was reported as the generic "the module's own self-test passed" while the assertion
    # that actually failed -- the only line that tells anyone what to fix -- was dropped.
    $reasons = @($lines | Where-Object { $_ -match '^\s*(\[[^\]]*\]\s*)?FAIL: ' } |
                 ForEach-Object { ($_ -replace '^\s*(\[[^\]]*\]\s*)?FAIL: ', '').Trim() })
    # Same two shapes for a skip: the host's bare 'SKIP: ' and a module's '  [<id>] SKIP: '. The
    # pattern is the one Invoke-SelfTests.ps1 settled on after '^SKIP:' missed both shapes. 'SKIPPED:' too, less the
    # real-input skip, which is collected on its own below and graded against this run's request.
    $realInputSkips = @($lines | Where-Object { $_ -cmatch $realInputSkipPattern } | ForEach-Object { $_.Trim() })
    $skips = @($lines | Where-Object { $_ -match '^\s*(\[[^\]]*\]\s*)?SKIP(PED)?:' -and $_ -cnotmatch $realInputSkipPattern } |
               ForEach-Object { $_.Trim() })
    # And a thrown probe ('EXC: <type>: <message>', same two shapes), which is a verdict on nothing.
    $exceptions = @($lines | Where-Object { $_ -match '^\s*(\[[^\]]*\]\s*)?EXC:' } | ForEach-Object { $_.Trim() })
    # Read after the process has exited, so after the loader's ShutdownAll: the one reading of the process's counts that
    # includes the convention runner's own instance.
    $recordText = $null
    if (Test-Path -LiteralPath $record -PathType Leaf) { $recordText = "$(Get-Content -LiteralPath $record -Raw)".Trim() }
    return [pscustomobject]@{ Id = $Id; Result = $result; Lines = $lines; Reasons = $reasons; Skips = $skips
                              RealInputSkips = $realInputSkips; Exceptions = $exceptions; RealInputRecord = $recordText }
}

# Why a run with the variable at 1 did not show, for module $Id, what $realInputModules says it must, or $null when it did.
# Graded whatever the module's verdict, because it is about the owner's keyboard, not about the suite, and each of the three
# rules on its own, so a run that fails one still reports what the others found (a press the process record saw is named
# even when the module's own counter check failed first).
function Get-RealInputProblem([string]$Id, $r) {
    $found = @()
    $expected = $realInputModules[$Id]
    # Assigned, not taken from an if-expression: an if hands back its output unrolled, so an empty array came back as
    # $null, and $null.Count throws under the gate's Set-StrictMode -Version Latest, which this script inherits there.
    $want = @()
    if ($expected) { $want = @($expected.Skipped) }
    $skipped = @($r.RealInputSkips | ForEach-Object { $_ -replace '^.*?so this check did not run: ', '' })
    $missing = @($want | Where-Object { $skipped -cnotcontains $_ })
    $extra = @($skipped | Where-Object { $want -cnotcontains $_ })
    if ($skipped.Count -ne $want.Count -or $missing.Count -gt 0 -or $extra.Count -gt 0) {
        $found += ("with $noRealInputVariable=1 its marker must carry exactly its $($want.Count) real-input SKIPPED line(s), and it " +
                   "carried $($skipped.Count)" + $(if ($missing.Count) { "; missing: " + ($missing -join ' / ') } else { '' }) +
                   $(if ($extra.Count) { "; not expected: " + ($extra -join ' / ') } else { '' }))
    }
    if ($expected) {
        $counterPattern = '^\s*(\[[^\]]*\]\s*)?PASS: ' + [regex]::Escape($expected.Counter)
        if (@($r.Lines | Where-Object { $_ -cmatch $counterPattern }).Count -ne 1) {
            $found += ("with $noRealInputVariable=1 its marker carries no PASS line for its real-press counter ('$($expected.Counter)'), " +
                       'so nothing measured that its own instances pressed nothing')
        }
        $m = [regex]::Match("$($r.RealInputRecord)", $realInputRecordPattern)
        if (-not $r.RealInputRecord) {
            $found += ("with $noRealInputVariable=1 it left no record of the process's real-press count after its instances shut down " +
                       "(dp-module-$Id-realinput.txt), so nothing measured the convention runner's own instance")
        }
        elseif (-not $m.Success) { $found += "its real-input record is not in the expected shape: '$($r.RealInputRecord)'" }
        elseif ([int]$m.Groups[3].Value -lt 1 -or $m.Groups[3].Value -ne $m.Groups[4].Value) {
            $found += ("its real-input record was written before every instance had shut down ($($r.RealInputRecord)), so the " +
                       "convention runner's own instance, the last to shut down, is not in it")
        }
        elseif ([long]$m.Groups[1].Value -ne 0) {
            $found += ("with $noRealInputVariable=1 the process's real-press counter read $($m.Groups[1].Value) after all " +
                       "$($m.Groups[3].Value) of its instances shut down, so real input WAS sent ($($r.RealInputRecord))")
        }
    }
    if ($found.Count -eq 0) { return $null }
    return ($found -join '; and ')
}

# The one sentence that says no real input was sent, built only after Get-RealInputProblem has found nothing, from what was
# measured. $null for a module that presses nothing, and for any run that did not show all three.
function Get-RealInputMeasurement([string]$Id, $r) {
    if (-not $realInputModules.ContainsKey($Id)) { return $null }
    if (Get-RealInputProblem $Id $r) { return $null }
    $m = [regex]::Match($r.RealInputRecord, $realInputRecordPattern)
    $counterLine = @($r.Lines | Where-Object { $_ -cmatch ('^\s*(\[[^\]]*\]\s*)?PASS: ' + [regex]::Escape($realInputModules[$Id].Counter)) })[0].Trim()
    return ("no real input was sent: its self-test measured $counterLine; and after all $($m.Groups[3].Value) of its " +
            "instances shut down, the convention runner's own included, the process's real-press counter read 0 " +
            "($($m.Groups[2].Value) press(es) withheld before SendInput)")
}

# One TEMP per run, handed to every child through the environment block it inherits. Start-Process
# has no -Environment under Windows PowerShell 5.1 (this file is #requires -Version 5), so the
# variables are set on THIS process and restored in the finally. Path.GetTempPath() reads TMP first,
# then TEMP, so both move; the SelfTestScratch roots the child stages module copies into move with
# them and are unmapped by the time the directory is removed, so the removal succeeds where
# SelfTestScratch.TryRelease inside the child cannot. dp- prefix, so an aged leftover from a killed
# run is collected by Invoke-SelfTests.ps1's sweep of the real %TEMP%.
#
# SHORT NAME, DELIBERATELY. The first draft used 'dp-module-selftests-run-<32 hex>' and the fortunes
# self-test went red under it: SmartFortunes' VectorCache replace-fallback probe builds a temp path
# ~150 characters below TEMP, and the MoveFileEx P/Invoke that fallback ends in has no long-path
# prefix, so once TEMP itself reached 107 characters the path hit MAX_PATH and the save was lost
# (measured 2026-09-29: TEMP of 74 characters passes, 107 fails; filed as a Fortunes/ModuleKit item).
# A runner must not fail a module on a path length the module never sees in production, so this
# stays under twenty characters.
# THE EXACT ENVIRONMENT, KEPT BESIDE THE MARKERS (N-fortunes-import-probe-01). A red run already kept its markers, but
# not what the children ran under, and the one unexplained Fortunes refusal (its portable replacement/rollback fallback,
# once, in a healthy baseline) could not be re-examined for want of it: that probe has failed before on the LENGTH of
# TEMP (the MAX_PATH note below) and on a handle a scanner held for a moment, so the TEMP the children saw, its length,
# whether it is an 8.3 alias, and what they left behind are the evidence. Only named variables are written, never the
# whole environment block, which can hold a token. Best-effort: a failure to write it is said, and never replaces the
# run's own verdict.
function Save-RunEnvironment([string]$Directory, [string]$OriginalTemp, [string[]]$Problems) {
    try {
        $text = New-Object 'Collections.Generic.List[string]'
        $text.Add('written: ' + (Get-Date).ToString('o'))
        $text.Add("runner: tests\Test-ModuleSelfTests.ps1 under PowerShell $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition))")
        $text.Add("children's TEMP and TMP (this run's own): $Directory ($($Directory.Length) characters; 8.3 alias segment: " +
                  $(if ($Directory.Contains('~')) { 'yes' } else { 'no' }) + ')')
        $text.Add("TEMP before this run: $OriginalTemp ($("$OriginalTemp".Length) characters)")
        foreach ($name in 'DESKTOP_AI_COMPANION_DATA_ROOT', $noRealInputVariable) {
            $value = [Environment]::GetEnvironmentVariable($name)
            $text.Add("${name}: " + $(if ($null -eq $value) { '(unset)' } else { $value }))
        }
        $hostFile = Get-Item -LiteralPath $exe
        $text.Add("host: $exe ($($hostFile.LastWriteTime.ToString('o')), $($hostFile.Length) bytes)")
        $text.Add("os: $([Environment]::OSVersion.VersionString); logical processors: $([Environment]::ProcessorCount)")
        $text.Add('modules (wall seconds, limit, exited):')
        foreach ($entry in $moduleTimings.GetEnumerator()) {
            $text.Add("  $($entry.Key): $($entry.Value.Seconds) s of $($entry.Value.Limit) s, exited=$($entry.Value.Exited)")
        }
        $text.Add('problems:')
        foreach ($problem in @($Problems)) { $text.Add("  $problem") }
        $text.Add('files left under the children''s TEMP (first 200):')
        $left = @(Get-ChildItem -LiteralPath $Directory -Recurse -Force -ErrorAction SilentlyContinue | Select-Object -First 200)
        foreach ($item in $left) {
            $size = if ($item.PSIsContainer) { '<dir>' } else { "$($item.Length) bytes" }
            $text.Add('  ' + $item.FullName.Substring($Directory.Length).TrimStart('\') + "  $size")
        }
        $path = Join-Path $Directory 'environment.txt'
        [IO.File]::WriteAllLines($path, $text.ToArray(), (New-Object Text.UTF8Encoding($false)))
        Write-Host ("  environment kept beside them: " + $path)
    }
    catch { Write-Host ("  the run's environment could not be recorded: " + $_.Exception.Message) }
}

$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$runTemp = Join-Path $env:TEMP ('dp-msr-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
New-Item -ItemType Directory -Path $runTemp -Force | Out-Null
$env:TEMP = $runTemp
$env:TMP = $runTemp
# Set true as the last statement of the run's try: a run that threw before it is kept like a red one (see the finally).
$runFinished = $false

# THE REAL Documents\Remembrance, WATCHED FROM OUTSIDE (N-remembrance-test-documents-01, lane feature/modules-fixes,
# 2026-10-29). A blank storage location records into Documents\Remembrance, the Documents known folder, which neither
# DESKTOP_AI_COMPANION_DATA_ROOT nor TEMP redirects and which is often inside OneDrive, and 46 synthetic files from a test
# run were found in a real one. Remembrance's SelfTest now points both of its seams at a per-run scratch folder; this is the
# check that does not depend on the module going through them.
#
# AN EVENT WATCH FOR THE WHOLE RUN, THEN THE LISTING. The first cut compared a footprint (exists, counts, newest write time)
# before and after, and a review proved it blind twice (2026-10-29): a run that creates the folder, writes a file and
# deletes both reads "absent" both times, and an in-place overwrite under a future-dated file leaves the counts and the
# newest time alone. So a watch is subscribed before the child starts and read after it exits: one FileSystemWatcher on
# Documents itself, not recursive, for the folder's own creation, deletion or rename, and, when the folder exists, one
# inside it, recursive, for anything at all. Any event latches. A watcher error (a buffer overflow among them) is a
# FAILED watch, never a clean one. The listing stays as the second check, with the counts and a signature over each
# entry's path, size and write time (names and contents are never recorded), and a listing that could not be read whole
# is a failure too: two partial listings that agree prove nothing. Both halves are checked on scratch folders at the start
# of every run (Test-DocumentsWatch), so a watch that cannot see is a problem before any module runs. Said on every run
# with the path, and said as well when the known folder cannot be resolved, so a run that watched nothing does not read
# like one that saw nothing change.
$documentsFolder = [Environment]::GetFolderPath('MyDocuments')
$watchParent = $null
$watchName = 'Remembrance'
if (-not [string]::IsNullOrWhiteSpace($documentsFolder)) { $watchParent = $documentsFolder }
$realRemembranceWatched = @('remembrance')
# How long after the child exits the watch is read: events are delivered on the thread pool, and the self-check below,
# which reads its own writes through the same Stop-DocumentsWatch, is what shows this is long enough on the machine.
$documentsWatchSettleMilliseconds = 1000

$documentsWatchSource = @'
using System;
using System.IO;
using System.Threading;

public sealed class DpDocumentsWatch : IDisposable
{
    private readonly string _name;
    private readonly FileSystemWatcher _parent;
    private readonly FileSystemWatcher _inside;
    private int _events;
    private string _first;
    private string _failure;

    public DpDocumentsWatch(string parentFolder, string name)
    {
        _name = name;
        // The parent first, so a folder created between the two is seen as created.
        _parent = Watcher(parentFolder, false);
        _parent.Created += OnParentEvent;
        _parent.Changed += OnParentEvent;
        _parent.Deleted += OnParentEvent;
        _parent.Renamed += OnParentRenamed;
        _parent.EnableRaisingEvents = true;
        string target = Path.Combine(parentFolder, name);
        if (Directory.Exists(target))
        {
            _inside = Watcher(target, true);
            _inside.Created += OnInsideEvent;
            _inside.Changed += OnInsideEvent;
            _inside.Deleted += OnInsideEvent;
            _inside.Renamed += OnInsideRenamed;
            _inside.EnableRaisingEvents = true;
        }
    }

    private FileSystemWatcher Watcher(string folder, bool recursive)
    {
        var watcher = new FileSystemWatcher(folder);
        watcher.IncludeSubdirectories = recursive;
        watcher.InternalBufferSize = 65536;
        watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes |
                               NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Security;
        watcher.Error += OnError;
        return watcher;
    }

    private void Latch(string what)
    {
        Interlocked.Increment(ref _events);
        Interlocked.CompareExchange(ref _first, what, null);
    }

    private void OnParentEvent(object sender, FileSystemEventArgs e)
    {
        if (string.Equals(e.Name, _name, StringComparison.OrdinalIgnoreCase)) Latch("the folder itself " + e.ChangeType);
    }

    private void OnParentRenamed(object sender, RenamedEventArgs e)
    {
        if (string.Equals(e.Name, _name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.OldName, _name, StringComparison.OrdinalIgnoreCase)) Latch("the folder itself Renamed");
    }

    private void OnInsideEvent(object sender, FileSystemEventArgs e) { Latch("inside it " + e.ChangeType); }

    private void OnInsideRenamed(object sender, RenamedEventArgs e) { Latch("inside it Renamed"); }

    private void OnError(object sender, ErrorEventArgs e)
    {
        Exception error = e.GetException();
        Interlocked.CompareExchange(ref _failure, error == null ? "an unnamed watcher error" : error.GetType().Name, null);
    }

    public int Events { get { return Interlocked.CompareExchange(ref _events, 0, 0); } }
    public string First { get { return Interlocked.CompareExchange(ref _first, null, null); } }
    public string Failure { get { return Interlocked.CompareExchange(ref _failure, null, null); } }

    public void Dispose()
    {
        _parent.EnableRaisingEvents = false;
        _parent.Dispose();
        if (_inside != null) { _inside.EnableRaisingEvents = false; _inside.Dispose(); }
    }
}
'@
if (-not ('DpDocumentsWatch' -as [type])) { Add-Type -TypeDefinition $documentsWatchSource -Language CSharp }

# The folder's listing: Complete says whether every entry could be read, and Text is 'absent', the counts with a signature,
# or why it could not be read. Inaccessible is never 'absent': GetAttributes throws FileNotFound or DirectoryNotFound for a
# folder that is not there and something else for one that cannot be reached, and the listing stops at the first entry it
# cannot read.
function Get-FolderFootprint([string]$Path) {
    $attributes = $null
    try { $attributes = [IO.File]::GetAttributes($Path) }
    catch {
        $base = $_.Exception.GetBaseException()
        if ($base -is [IO.FileNotFoundException] -or $base -is [IO.DirectoryNotFoundException]) {
            return [pscustomobject]@{ Complete = $true; Text = 'absent' }
        }
        return [pscustomobject]@{ Complete = $false; Text = "could not be read ($($base.GetType().Name))" }
    }
    if (($attributes -band [IO.FileAttributes]::Directory) -eq 0) {
        $file = Get-Item -LiteralPath $Path -Force
        return [pscustomobject]@{ Complete = $true; Text = "present as a file of $($file.Length) bytes, written $($file.LastWriteTimeUtc.Ticks)" }
    }
    $root = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $entries = $null
    try { $entries = @(Get-ChildItem -LiteralPath $root -Recurse -Force -ErrorAction Stop) }
    catch { return [pscustomobject]@{ Complete = $false; Text = "could not be listed whole ($($_.Exception.GetBaseException().GetType().Name))" } }
    $files = 0
    $lines = New-Object 'Collections.Generic.List[string]'
    foreach ($entry in $entries) {
        $relative = $entry.FullName.Substring($root.Length)
        if ($entry.PSIsContainer) { $lines.Add("$relative|dir|$($entry.LastWriteTimeUtc.Ticks)") }
        else { $files++; $lines.Add("$relative|$($entry.Length)|$($entry.LastWriteTimeUtc.Ticks)") }
    }
    $sorted = $lines.ToArray()
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    $sha = [Security.Cryptography.SHA256]::Create()
    $digest = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($sorted -join "`n")))
    $signature = (($digest[0..7] | ForEach-Object { $_.ToString('x2') }) -join '')
    return [pscustomobject]@{ Complete = $true
                              Text = "present, $files file(s) and $($entries.Count - $files) folder(s), signature $signature" }
}

# Subscribed BEFORE the child starts; the listing is taken first, so a change between the two is in the events.
function Start-DocumentsWatch([string]$Parent, [string]$Name) {
    $target = Join-Path $Parent $Name
    $state = [pscustomobject]@{ Target = $target; Before = (Get-FolderFootprint $target); Watch = $null; StartFailure = $null }
    try { $state.Watch = New-Object DpDocumentsWatch -ArgumentList $Parent, $Name }
    catch { $state.StartFailure = $_.Exception.GetBaseException().GetType().Name }
    return $state
}

# $null when nothing changed and everything could be seen; otherwise every reason it cannot be called clean.
function Stop-DocumentsWatch($State) {
    Start-Sleep -Milliseconds $documentsWatchSettleMilliseconds
    $found = @()
    if ($State.StartFailure) { $found += "the watch could not start ($($State.StartFailure)), so a change could have gone unseen" }
    else {
        try {
            if ($State.Watch.Failure) { $found += "the watch failed ($($State.Watch.Failure)), so a change could have gone unseen" }
            if ($State.Watch.Events -gt 0) { $found += "a change was seen ($($State.Watch.Events) event(s), the first: $($State.Watch.First))" }
        }
        finally { $State.Watch.Dispose() }
    }
    $after = Get-FolderFootprint $State.Target
    if (-not $State.Before.Complete -or -not $after.Complete) {
        $found += "the folder could not be listed whole (before: $($State.Before.Text); after: $($after.Text)), and two partial listings prove nothing"
    }
    elseif ($after.Text -cne $State.Before.Text) { $found += "its listing changed (before: $($State.Before.Text); after: $($after.Text))" }
    if ($found.Count -eq 0) { return $null }
    return ($found -join '; ')
}

# The watch's own check, on scratch folders under this run's TEMP and never the real Documents: the two inputs the
# footprint missed, a folder that cannot be listed, and two quiet runs that must stay clean. Returns the failures.
function Test-DocumentsWatch([string]$Scratch) {
    $failed = @()
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $cases = @(
        @{ Label = 'create, write and delete in a folder that was absent'; Expect = 'a change was seen'; Prepare = { param($d) }
           Act = { param($d) $r = Join-Path $d 'Remembrance'; [void][IO.Directory]::CreateDirectory($r)
                   [IO.File]::WriteAllText((Join-Path $r 'synthetic.dat'), 'a synthetic recording'); [IO.File]::Delete((Join-Path $r 'synthetic.dat'))
                   [IO.Directory]::Delete($r) } },
        @{ Label = 'an in-place overwrite under a future-dated file, its write time put back'; Expect = 'a change was seen'
           Prepare = { param($d) $r = Join-Path $d 'Remembrance'; [void][IO.Directory]::CreateDirectory($r)
                       [IO.File]::WriteAllText((Join-Path $r 'older.dat'), 'old text'); [IO.File]::SetLastWriteTimeUtc((Join-Path $r 'older.dat'), [DateTime]::UtcNow.AddDays(-1))
                       [IO.File]::WriteAllText((Join-Path $r 'future.dat'), 'future'); [IO.File]::SetLastWriteTimeUtc((Join-Path $r 'future.dat'), [DateTime]::UtcNow.AddDays(365)) }
           Act = { param($d) $f = Join-Path (Join-Path $d 'Remembrance') 'older.dat'; $t = [IO.File]::GetLastWriteTimeUtc($f)
                   [IO.File]::WriteAllText($f, 'new text'); [IO.File]::SetLastWriteTimeUtc($f, $t) } },
        @{ Label = 'a subfolder that cannot be listed'; Expect = 'could not be listed whole'
           Prepare = { param($d) $l = Join-Path (Join-Path $d 'Remembrance') 'locked'; [void][IO.Directory]::CreateDirectory($l)
                       [IO.File]::WriteAllText((Join-Path $l 'inside.dat'), 'unseen')
                       & icacls $l /deny "*${sid}:(RD)" | Out-Null
                       if ($LASTEXITCODE -ne 0) { throw "icacls could not deny listing on the scratch subfolder (exit $LASTEXITCODE)" } }
           Act = { param($d) } },
        @{ Label = 'WITNESS nothing happens'; Expect = $null
           Prepare = { param($d) $r = Join-Path $d 'Remembrance'; [void][IO.Directory]::CreateDirectory($r); [IO.File]::WriteAllText((Join-Path $r 'kept.dat'), 'kept') }
           Act = { param($d) } },
        @{ Label = 'WITNESS a change beside the folder, not in it'; Expect = $null; Prepare = { param($d) }
           Act = { param($d) $o = Join-Path $d 'Other'; [void][IO.Directory]::CreateDirectory($o); [IO.File]::WriteAllText((Join-Path $o 'theirs.dat'), 'theirs') } }
    )
    $index = 0
    foreach ($case in $cases) {
        $index++
        $documents = Join-Path $Scratch "case$index\Documents"
        try {
            [void][IO.Directory]::CreateDirectory($documents)
            & $case.Prepare $documents
            $state = Start-DocumentsWatch $documents 'Remembrance'
            & $case.Act $documents
            $result = Stop-DocumentsWatch $state
            $ok = if ($null -eq $case.Expect) { $null -eq $result } else { $null -ne $result -and $result.Contains($case.Expect) }
            if (-not $ok) { $failed += "$($case.Label): expected $(if ($case.Expect) { "'$($case.Expect)'" } else { 'a clean watch' }), got $(if ($result) { $result } else { 'a clean watch' })" }
        }
        catch { $failed += "$($case.Label): the case threw $($_.Exception.GetBaseException().GetType().Name): $($_.Exception.GetBaseException().Message)" }
        finally {
            $locked = Join-Path $documents 'Remembrance\locked'
            if (Test-Path -LiteralPath $locked) { & icacls $locked /remove:d "*$sid" | Out-Null }
        }
    }
    try { [IO.Directory]::Delete($Scratch, $true) } catch { }
    return $failed
}

if ($watchParent) {
    $realStart = Get-FolderFootprint (Join-Path $watchParent $watchName)
    Write-Host ("DOCUMENTS WATCH: $(Join-Path $watchParent $watchName) is watched for any change from before the " +
                "$($realRemembranceWatched -join ', ') self-test starts until after it exits, then listed again; it reads $($realStart.Text) now")
}
else {
    Write-Host ('DOCUMENTS WATCH: NOT WATCHED: the Documents known folder could not be resolved, so a self-test writing into a ' +
                'real Documents\Remembrance would go unseen in this run')
}
$documentsWatchSelfCheck = @(Test-DocumentsWatch (Join-Path $runTemp 'dp-watch-check'))
if ($documentsWatchSelfCheck.Count -eq 0) { Write-Host 'DOCUMENTS WATCH SELF-CHECK: 5 case(s) on scratch folders behaved as they must' }

# Said on every run that presses, before the first module runs, as tests\mutate-selftest-guards.py says it at its start:
# a run without the variable makes Blinking LED's real presses, and until the final round of lane
# feature/blinkingled-resume nothing here said so (with it at 1 the OK line, SELFTEST-NOT-RUN: and the summary say so).
if (-not $noRealInputRequested -and $Covered -contains 'blinkingled') {
    Write-Host "REAL INPUT: DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT is not 1, so blinkingled's self-test makes one pair of real Scroll Lock presses"
}

$problems = @()
try {
    foreach ($id in $Covered) {
        $documentsWatch = $null
        if ($watchParent -and $realRemembranceWatched -contains $id) { $documentsWatch = Start-DocumentsWatch $watchParent $watchName }
        $r = $null
        try {
            $artifactBefore = $null
            if ($id -ceq 'agentbroker') { $artifactBefore = Get-AgentBrokerArtifactSnapshot $modulesRoot }
            $r = Invoke-ModuleSelfTest $id
            if ($id -ceq 'agentbroker' -and $r.Result -ceq 'PASS') {
                Assert-ModuleArtifactsUnchanged $artifactBefore (Get-AgentBrokerArtifactSnapshot $modulesRoot)
            }
            $artifactWitnessLines = @(Get-ModuleArtifactWitnessLines $id $r.Result $r.Lines $artifactBefore)
            foreach ($line in $artifactWitnessLines) { Write-Host $line }
        }
        catch {
            if (-not $_.Exception.Message.StartsWith('Test-ModuleSelfTests.ps1: MODULE_ARTIFACT_', [StringComparison]::Ordinal)) { throw }
            # Keep this module's artifact failure gradable and let the remaining modules run.
            $problems += "$id : $($_.Exception.Message)"
            continue
        }
        finally {
            # Graded whatever the module's verdict, like the real-input rules below: it is about the owner's files, not the suite.
            $documentsProblem = $null
            if ($null -ne $documentsWatch) {
                $documentsChange = Stop-DocumentsWatch $documentsWatch
                if ($documentsChange) {
                    $documentsProblem = ("the real Documents\Remembrance changed during its self-test, or could not be watched " +
                                         "($(Join-Path $watchParent $watchName): $documentsChange): a check or a mutant recorded " +
                                         'outside its scratch folder, or the installed app recorded there during the run')
                    $problems += "$id : $documentsProblem"
                }
            }
        }
        # ANCHORED ON THE ASSERTION SHAPE, not on the substring 'PASS'.
        #
        # This counted `$_ -match 'PASS'`, and the marker's own `RESULT=PASS` line matches that. So
        # inside the elseif below $assertions was ALWAYS at least 1, the "reported PASS with no
        # assertion lines" branch was unreachable for every possible input, and every count printed was
        # inflated by one -- "1 assertion line" was the real zero.
        #
        # That guard is the one written specifically against "a check that ran nothing and reported
        # success", in the file built to catch exactly that. It was that. Found by an audit the same
        # night it was written.
        #
        # Only this module's tagged assertions count. Host convention PASS lines also exist for an empty SelfTest.
        $minimum = if ($r.Result -ne 'PASS') { 0 } elseif ($moduleAssertionFloors.ContainsKey($id)) { [int]$moduleAssertionFloors[$id] } else { 1 }
        $assertions = Get-ModuleAssertionCount $r.Lines $id $minimum
        # The real-input rules first and on their own: a run that sent real input, or cannot show it sent none, is a problem
        # whatever else it reports, and is never also printed as OK.
        $realInputProblem = $null
        if ($noRealInputRequested) { $realInputProblem = Get-RealInputProblem $id $r }
        if ($realInputProblem) { $problems += "$id : $realInputProblem" }
        if ($r.Result -ne 'PASS') {
            $problems += "$id : expected PASS, got $($r.Result)$(if ($r.Reasons.Count) { ' -- ' + ($r.Reasons -join '; ') })"
        }
        elseif ($r.Skips.Count -gt 0) {
            # A SKIP is not a pass. The module's probe reports RESULT=PASS over the assertions it DID
            # run, so without this line a suite that skipped its DPAPI round-trip or its OCR check on a
            # CI image printed OK here with those assertions untested. Checked BEFORE the PASS branch,
            # because a report with a positive PASS: count and a SKIP: line satisfies every rule below.
            $problems += "$id : reported PASS but skipped part of its suite -- $($r.Skips[0])"
        }
        elseif ($r.RealInputSkips.Count -gt 0 -and -not $noRealInputRequested) {
            # A module skipping its real-input checks in a run that did not ask for it: the real press went untested
            # with nothing having requested that, which is a skip like any other (see the block above Invoke-ModuleSelfTest).
            $problems += ("$id : skipped its real-input check(s) although $noRealInputVariable is not 1 in this run -- " +
                          $r.RealInputSkips[0])
        }
        elseif ($r.Reasons.Count -gt 0 -or $r.Exceptions.Count -gt 0) {
            # A FAIL or EXC line under RESULT=PASS is not a pass either (R-063): it is a Check() whose
            # bool was never folded into the result, or a probe that threw after the verdict was
            # decided, and the exit code the gate and CI read is green over it. Until 2026-09-30 this
            # branch did not exist, so such a marker printed OK here with its assertion count, and only
            # the mutation harness -- which is not part of the gate -- could have seen the FAIL line.
            # Measured with a stub exe whose every marker carries exactly that shape: seven covered
            # modules printed OK before this line, and name the line after it.
            $reason = if ($r.Reasons.Count -gt 0) { $r.Reasons[0] } else { $r.Exceptions[0] }
            $problems += "$id : reported PASS with a FAIL or EXC line ($reason), so a verdict was never folded into the result"
        }
        elseif ($assertions -eq 0) {
            # A PASS carrying no assertions is the shape this repo keeps finding: a check that ran
            # nothing and reported success.
            $problems += "$id : reported PASS with no assertion lines, so it proved nothing"
        }
        elseif (-not $realInputProblem -and -not $documentsProblem) {
            $notRunNote = ''
            if ($r.RealInputSkips.Count -gt 0) {
                # Visible, not a failure: beside the OK, and once more on stdout for the gate's summary.
                $notRunCount += $r.RealInputSkips.Count
                $notRunNote = "; $($r.RealInputSkips.Count) real-input check(s) NOT RUN ($noRealInputVariable=1)"
                # EVERY skipped check by name, not the first line alone: the summary and the gate say how many did not run,
                # and until the final round of lane feature/blinkingled-resume they named only one of the two.
                $notRunChecks = @($r.RealInputSkips | ForEach-Object { $_ -replace '^.*?so this check did not run: ', '' })
                Write-Output ("SELFTEST-NOT-RUN: $id : $($r.RealInputSkips.Count) check(s) that press a real key did not run, " +
                              "because $noRealInputVariable=1 -- " + ($notRunChecks -join ' / '))
            }
            $measured = $null
            if ($noRealInputRequested) { $measured = Get-RealInputMeasurement $id $r }
            if ($measured) {
                # The measured sentence, for the gate's summary: the only place "no real input was sent" comes from.
                $notRunNote += '; measured: it sent no real input'
                $noRealInputMeasured += $id
                Write-Output "SELFTEST-NO-REAL-INPUT: $id : $measured"
            }
            $timing = $moduleTimings[$id]
            Write-Host ("  OK   {0,-12} PASS ({1} assertion(s)) in {2} s of its {3} s limit{4}" -f $id, $assertions,
                        $timing.Seconds.ToString('0.0', [Globalization.CultureInfo]::InvariantCulture), $timing.Limit, $notRunNote)
        }
    }

    foreach ($id in $Uncovered.Keys) {
        $expected = @($Uncovered[$id])
        $r = Invoke-ModuleSelfTest $id
        if ($r.Result -eq 'PASS' -and ($r.Skips.Count -gt 0 -or $r.RealInputSkips.Count -gt 0)) {
            # Neither the known gap nor a pass. The branch below would tell the maintainer to move the
            # module into COVERED, which is the wrong instruction for a suite that skipped part of itself.
            $firstSkip = if ($r.Skips.Count -gt 0) { $r.Skips[0] } else { $r.RealInputSkips[0] }
            $problems += "$id : reports PASS with a SKIP line ($firstSkip), which is neither a pass nor its known gap"
            continue
        }
        if ($r.Result -eq 'PASS' -and ($r.Reasons.Count -gt 0 -or $r.Exceptions.Count -gt 0)) {
            # Same for a FAIL or EXC line under a PASS verdict (R-063): the instruction below would be wrong.
            $reason = if ($r.Reasons.Count -gt 0) { $r.Reasons[0] } else { $r.Exceptions[0] }
            $problems += "$id : reports PASS with a FAIL or EXC line ($reason), which is neither a pass nor its known gap"
            continue
        }
        if ($r.Result -eq 'PASS') {
            $problems += ("$id : now PASSES its module self-test. That is good news -- move it from " +
                          'UNCOVERED to COVERED in tests\Test-ModuleSelfTests.ps1 so it stays covered.')
            continue
        }
        # Set equality in both directions. A NEW failure must break this, and a FIXED one must too, so
        # the list cannot drift away from what the modules actually do.
        $unexpected = @($r.Reasons | Where-Object { $expected -notcontains $_ })
        $gone = @($expected | Where-Object { $r.Reasons -notcontains $_ })
        if ($unexpected.Count -gt 0) {
            $problems += "$id : unexpected failure(s) -- $($unexpected -join '; ')"
        }
        if ($gone.Count -gt 0) {
            $problems += ("$id : no longer reports $($gone -join '; '). Remove it from the expected list " +
                          'in tests\Test-ModuleSelfTests.ps1 rather than leaving a stale expectation.')
        }
        if ($unexpected.Count -eq 0 -and $gone.Count -eq 0) {
            Write-Host ("  gap  {0,-12} {1} known gap(s), unchanged" -f $id, $expected.Count)
        }
    }
    # A watch that failed its own check (Test-DocumentsWatch, run before the first module) cannot vouch for the real folder,
    # whatever it read during the run.
    foreach ($selfCheckFailure in $documentsWatchSelfCheck) { $problems += "the Documents watch failed its own check: $selfCheckFailure" }
    $runFinished = $true
}
finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    # A run that threw before it finished is kept too: it has no verdict, and its markers are all there is to read.
    if ($problems.Count -gt 0 -or -not $runFinished) {
        # The markers are the only record of WHICH assertion failed beyond the one line quoted in the
        # throw below, so a red run keeps its directory and names it. Invoke-SelfTests.ps1 does the same.
        Write-Host ("  markers kept for inspection: " + $runTemp)
        Save-RunEnvironment $runTemp $previousTemp $problems
    }
    else {
        # Best-effort: every child has exited, so nothing maps the staged module copies any more. A
        # failure here is not a test failure, and a directory that survives carries the dp- prefix
        # Invoke-SelfTests.ps1's aged-root sweep collects.
        try { [System.IO.Directory]::Delete($runTemp, $true) } catch { }
    }
}

if ($problems.Count -gt 0) {
    # One line each as well as the throw: a thrown message is wrapped at the console's width when it is rendered, and a
    # reader, the gate's log or tests\mutate-selftest-guards.py's runner flag, needs each problem whole.
    foreach ($problem in $problems) { Write-Host "  PROBLEM: $problem" }
    throw ("Module self-tests: " + ($problems -join ' | '))
}

Write-Host ("Module self-tests OK: $($Covered.Count) covered module(s) passed, " +
            "$($Uncovered.Count) known gap(s) still gaps." +
            $(if ($noRealInputRequested) {
                " Real input withheld by request ($noRealInputVariable=1): $notRunCount check(s) that press a real key did not run" +
                " (listed above); measured as sending none: " +
                $(if ($noRealInputMeasured.Count) { $noRealInputMeasured -join ', ' } else { '(no module)' }) + '.'
            } else { '' }))
