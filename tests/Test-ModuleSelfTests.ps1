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

      COVERED   must report RESULT=PASS. These are the modules whose module class exposes
                `static bool SelfTest(out string detail)`, so the host runs their own assertions.

      UNCOVERED must fail, and fail for the EXACT known reasons. petstudio has a self-check in the
                assembly but not on the module class -- BehaviourChainSelfCheck.RunChecks needs a pet
                XML fixture the host supplies from its own resources -- and testmodule is a
                deliberately minimal ABI fixture. For both the host reports "the module exposes
                static bool SelfTest(out string detail)" and stops. Listing them as expected failures
                rather than skipping them means the day somebody adds a SelfTest, this gate goes RED
                and says to move the id into COVERED. A skip list would have quietly kept them
                uncovered forever, which is the failure mode this whole file is a response to.

                The lists are the authority, not this paragraph: it named aibrain and fortunes as
                uncovered for a day after both gained a module-class SelfTest and moved to COVERED,
                which is exactly the drift the counts elsewhere in the repo are asserted against.

    The union is also checked against the modules actually built, so a NEW module cannot arrive
    uncatalogued by either list.

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

$Covered = @('agentflow', 'aibrain', 'blinkingled', 'fortunes', 'reminder', 'remembrance')

# The EXACT set of failures each uncovered module is expected to report -- not merely "it failed",
# and not just its first failure. The first draft of this file matched the first FAIL line only, and
# testmodule promptly proved why that is not enough: its first failure is the missing SelfTest, and
# it has a SECOND unrelated one (a tray entry with no icon) that the loose match absorbed in silence.
# A list of expected failures that can swallow an unexpected one is the same defect as a skip list.
$Uncovered = [ordered]@{
    # PetStudio's in-module checks are BehaviourChainSelfCheck.RunChecks(fixturePetXml, out detail),
    # which needs a pet XML the host supplies from its OWN resources. The module does not carry one,
    # so covering it means embedding a fixture rather than adding a delegation -- unlike aibrain and
    # fortunes, which already had probes with the exact signature and only lacked the entry point.
    'petstudio'   = @('the module exposes static bool SelfTest(out string detail)')
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
    $marker = Join-Path $env:TEMP "dp-module-$Id-selftest.txt"

    # [IO.File]::Delete, not Remove-Item, and the result is CHECKED. Remove-Item performs ~
    # home-directory expansion even under -LiteralPath, so it fails outright when the temp path
    # contains a tilde -- the norm on Windows whenever the account name exceeds 8 characters and TEMP
    # holds the 8.3 short form -- and this call carried -ErrorAction SilentlyContinue, which swallowed
    # that plus any lock or ACL failure. Invoke-SelfTests.ps1 already removed exactly this call for
    # exactly this reason. Delete is a no-op on a missing file, so no Test-Path guard.
    try { [System.IO.File]::Delete($marker) } catch { }
    if (Test-Path -LiteralPath $marker -PathType Leaf) {
        return [pscustomobject]@{ Id = $Id; Result = 'STALEMARKER'; Lines = @()
                                  Reasons = @("the previous marker could not be deleted, so any verdict would be the previous run's: $marker") }
    }

    $process = Start-Process -FilePath $exe -ArgumentList "--module-selftest=$Id" -PassThru
    $exited = $process.WaitForExit($TimeoutSeconds * 1000)
    if (-not $exited) {
        try { $process.Kill() } catch { }
        return [pscustomobject]@{ Id = $Id; Result = 'TIMEOUT'; Lines = @(); Reasons = @("did not exit in ${TimeoutSeconds}s") }
    }
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
        # The marker is the ONLY evidence the module was actually loaded and exercised. An exit code
        # of 0 with no marker means the run did not happen, which must never read as a pass.
        return [pscustomobject]@{ Id = $Id; Result = 'NOMARKER'; Lines = @(); Reasons = @('no marker file was written') }
    }

    # NO TIMESTAMP CHECK HERE, deliberately, and this is the reasoning so nobody re-derives it.
    # A marker-age comparison reads like the obvious second line of defence, and it cannot fire once
    # the delete above is asserted: the only ways a run leaves no fresh marker are Finish's write
    # throwing (no file at all -> NOMARKER), the create-then-rename failing (temp file only ->
    # NOMARKER), or two runners racing on the same %TEMP% path, where BOTH files are recent so an age
    # test is blind to it anyway. A check no input can fail is the defect this repo keeps finding, so
    # it is not shipped. Asserting the delete is what closes the real hole -- Remove-Item failing
    # silently on a tilde-bearing TEMP, a lock, or an ACL, and a previous RESULT=PASS then being
    # graded as this run.
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
    return [pscustomobject]@{ Id = $Id; Result = $result; Lines = $lines; Reasons = $reasons }
}

$problems = @()

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

if ($problems.Count -gt 0) {
    throw ("Module self-tests: " + ($problems -join ' | '))
}

Write-Host ("Module self-tests OK: $($Covered.Count) covered module(s) passed, " +
            "$($Uncovered.Count) known gap(s) still gaps.")
