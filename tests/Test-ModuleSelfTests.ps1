#requires -Version 5
<#
.SYNOPSIS
    Run every built module's --module-selftest through the REAL host loader, and refuse to let the
    uncovered set grow in silence.

.DESCRIPTION
    Added 2026-09-27 because the harness existed, worked, and was run by nothing. BACKLOG.md said
    "--module-selftest=agentflow runs in both [the gate and CI]" -- no .ps1 and no .yml in the repo
    contained that string, so 684 module assertions across four modules were executed only when
    somebody typed the flag by hand. AgentFlow alone accounts for 478 of them, on the module the
    owner uses every day.

    Two halves, and the second is the one that keeps this honest:

      COVERED   must report RESULT=PASS, with at least one PASS: assertion line and NO SKIP: line.
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

    Cost: ~15s. Measured per module -- agentflow 7.5s, remembrance 3.5s, fortunes 1.6s, reminder
    1.0s, petstudio 0.7s, aibrain 0.8s, blinkingled 0.5s.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

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

$Covered = @('agentflow', 'aibrain', 'blinkingled', 'fortunes', 'petstudio', 'reminder', 'remembrance')

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
$unlisted = @($built | Where-Object { $known -notcontains $_ })
$missing = @($known | Where-Object { $built -notcontains $_ })
if ($unlisted.Count -gt 0) {
    throw ("Built module(s) appear in neither the COVERED nor the UNCOVERED list in this file: " +
           ($unlisted -join ', ') + ". Add a SelfTest and put it in COVERED, or record it in UNCOVERED.")
}
if ($missing.Count -gt 0) {
    throw ("This file lists module(s) that are not built: " + ($missing -join ', ') +
           '. Remove them, or find out why build.ps1 stopped producing them.')
}

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
        return [pscustomobject]@{ Id = $Id; Result = 'STALEMARKER'; Lines = @(); Skips = @()
                                  Reasons = @("the previous marker could not be deleted, so any verdict would be the previous run's: $marker") }
    }

    $process = Start-Process -FilePath $exe -ArgumentList "--module-selftest=$Id" -PassThru
    $exited = $process.WaitForExit($TimeoutSeconds * 1000)
    if (-not $exited) {
        try { $process.Kill() } catch { }
        return [pscustomobject]@{ Id = $Id; Result = 'TIMEOUT'; Lines = @(); Skips = @(); Reasons = @("did not exit in ${TimeoutSeconds}s") }
    }
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
        # The marker is the ONLY evidence the module was actually loaded and exercised. An exit code
        # of 0 with no marker means the run did not happen, which must never read as a pass.
        return [pscustomobject]@{ Id = $Id; Result = 'NOMARKER'; Lines = @(); Skips = @(); Reasons = @('no marker file was written') }
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
    # pattern is the one Invoke-SelfTests.ps1 settled on after '^SKIP:' missed both shapes.
    $skips = @($lines | Where-Object { $_ -match '^\s*(\[[^\]]*\]\s*)?SKIP:' } | ForEach-Object { $_.Trim() })
    return [pscustomobject]@{ Id = $Id; Result = $result; Lines = $lines; Reasons = $reasons; Skips = $skips }
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
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$runTemp = Join-Path $env:TEMP ('dp-msr-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
New-Item -ItemType Directory -Path $runTemp -Force | Out-Null
$env:TEMP = $runTemp
$env:TMP = $runTemp

$problems = @()
try {
    foreach ($id in $Covered) {
        $r = Invoke-ModuleSelfTest $id
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
        # An assertion line is `PASS:` with the colon, optionally behind a `[module]` tag;
        # `RESULT=PASS` has no colon and no longer counts.
        $assertions = @($r.Lines | Where-Object { $_ -match '^\s*(\[[^\]]*\]\s*)?PASS:' }).Count
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
        elseif ($assertions -eq 0) {
            # A PASS carrying no assertions is the shape this repo keeps finding: a check that ran
            # nothing and reported success.
            $problems += "$id : reported PASS with no assertion lines, so it proved nothing"
        }
        else {
            Write-Host ("  OK   {0,-12} PASS ({1} assertion line(s))" -f $id, $assertions)
        }
    }

    foreach ($id in $Uncovered.Keys) {
        $expected = @($Uncovered[$id])
        $r = Invoke-ModuleSelfTest $id
        if ($r.Result -eq 'PASS' -and $r.Skips.Count -gt 0) {
            # Neither the known gap nor a pass. The branch below would tell the maintainer to move the
            # module into COVERED, which is the wrong instruction for a suite that skipped part of itself.
            $problems += "$id : reports PASS with a SKIP line ($($r.Skips[0])), which is neither a pass nor its known gap"
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
}
finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    if ($problems.Count -gt 0) {
        # The markers are the only record of WHICH assertion failed beyond the one line quoted in the
        # throw below, so a red run keeps its directory and names it. Invoke-SelfTests.ps1 does the same.
        Write-Host ("  markers kept for inspection: " + $runTemp)
    }
    else {
        # Best-effort: every child has exited, so nothing maps the staged module copies any more. A
        # failure here is not a test failure, and a directory that survives carries the dp- prefix
        # Invoke-SelfTests.ps1's aged-root sweep collects.
        try { [System.IO.Directory]::Delete($runTemp, $true) } catch { }
    }
}

if ($problems.Count -gt 0) {
    throw ("Module self-tests: " + ($problems -join ' | '))
}

Write-Host ("Module self-tests OK: $($Covered.Count) covered module(s) passed, " +
            "$($Uncovered.Count) known gap(s) still gaps.")
