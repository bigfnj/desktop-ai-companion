#requires -Version 5
<#
.SYNOPSIS
    Build tools\ShimejiConvert, verify the shipped Companions corpus with it, and run its selftest against the suite
    floor. Every failure is collected and thrown together at the end.

.DESCRIPTION
    N-bd-w5-packaging-ci-1-02 (lane burndown/w8-packaging-ci-1, 2026-11-07). ONE runner for the converter checks, called
    by tests\run-gate.ps1, by .github\workflows\build.yml's "Convert and verify the shipped companion corpus" step and by
    .github\workflows\release.yml's "Test the build being released" step. The gate and build.yml each carried this block
    inline, under a hardening invariant that held their two copies of the floor equal, and release.yml carried none of
    it: a tag on a commit build.yml never ran published a ZIP and MSI whose Companions\ nothing had verified. A third
    inline copy would have been a third floor to keep equal, which is the drift Invoke-SelfTests.ps1 and
    Invoke-OfflineScriptChecks.ps1 were each made shared to end.

    The output half: every shipped pet is graded by the app's REAL validator (through the source-linked
    ShimejiConvert.Engine) and round-tripped through the DTOs. The input half: `selftest` runs the parser and Group
    1/2/3 classifier suites and the bundled-conf census (BundledConfSelfTest asserts 91 actions as 54 Group1 / 31 Group2
    / 6 Group3 against the BUNDLED conf; the code is the authority, see BundledConfSelfTest.cs). The converter is a
    dev and module-shared tool, not the shipped product, so build.ps1 does not build it and this runner does.

    The converter's own lines are shown (Write-Host), not returned. This runner's output is one line on a pass,
    `SHIMEJI-SUITE-COUNT: N`, which the gate reads for its summary. It builds into tools\ShimejiConvert\bin and obj, and
    writes nothing else; `verify` reads Companions\ and changes nothing in it.

.EXAMPLE
    .\tests\Invoke-ShimejiConvertChecks.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$failures = New-Object 'Collections.Generic.List[string]'

# try/catch around every NATIVE call (RA-359), not only $LASTEXITCODE: an image that ran and failed sets the exit code,
# but one that cannot START (locked by a concurrent build in the same checkout, a missing dotnet) is a terminating
# ApplicationFailedException out of `&` under Stop, and it would leave this runner before the collected failures are
# thrown. The build's lines are shown rather than returned, so a caller that captures this runner's output for the count
# line still sees the build in its log.
Write-Host '=== ShimejiConvert build (Release)'
$shimejiBuilt = $false
try {
    & dotnet build (Join-Path $repoRoot 'tools\ShimejiConvert\ShimejiConvert.csproj') -c Release --nologo -v:minimal |
        ForEach-Object { Write-Host "$_" }
    $shimejiBuilt = ($LASTEXITCODE -eq 0)
    if (-not $shimejiBuilt) { $failures.Add("ShimejiConvert build (exit $LASTEXITCODE)") }
}
catch { $failures.Add('ShimejiConvert build: ' + $_.Exception.Message) }

# Initialised ahead of the branch that sets it: the count line below reads it, and under Set-StrictMode an unassigned
# variable is a terminating error on the PASSING path only (the $CountPrefix lesson in Invoke-SelfTests.ps1).
$shimejiSuiteCount = 0
if ($shimejiBuilt) {
    # No TFM folder: the csproj sets AppendTargetFrameworkToOutputPath=false and OutputPath=bin\Release\. build.yml once
    # named bin\Release\net10.0-windows\, which has never existed, and with $LASTEXITCODE still 0 from the build its
    # guards could pass having verified nothing. Existence is asserted rather than assumed, so a future output-path
    # change fails loudly.
    $shimejiExe = Join-Path $repoRoot 'tools\ShimejiConvert\bin\Release\ShimejiConvert.exe'
    if (-not (Test-Path -LiteralPath $shimejiExe -PathType Leaf)) {
        $failures.Add("ShimejiConvert.exe missing after build: $shimejiExe")
    }
    else {
        Write-Host '=== ShimejiConvert verify Companions'
        try {
            & $shimejiExe verify (Join-Path $repoRoot 'Companions') | ForEach-Object { Write-Host "$_" }
            if ($LASTEXITCODE -ne 0) { $failures.Add("ShimejiConvert verify (exit $LASTEXITCODE)") }
        }
        catch { $failures.Add('ShimejiConvert verify: ' + $_.Exception.Message) }

        # THE COUNT IS READ (N-deadcode-04). F452 made the CLI print `SELFTEST-COUNT: N` after the detail, the sentinel
        # tests\Invoke-SelfTests.ps1 already emits, so that a deleted suite registration is a number that changed rather
        # than a quieter PASS; until the gate and build.yml read it, a registration deleted from EngineSelfTest.RunAll
        # still passed. The output is captured so the sentinel can be parsed, and re-emitted line by line so the log
        # keeps the detail; the exit code stays the verdict. stdout only, no 2>&1: under Windows PowerShell a native
        # stderr line arriving through a redirect while $ErrorActionPreference is Stop is a terminating error (RA-346),
        # and a stack trace on stderr is a nonzero exit the check below already reports.
        #
        # A FLOOR, pinned to the suites the selftest runs today, and the pin is the point (the same stance as the
        # hardening self-test's literal limits, F292): a registration deleted from RunAll fails here and has to be a
        # deliberate edit of this number, and so does adding one. `-le 0` alone would only catch the sentinel going
        # missing. 13 = RunAll's 12 registrations plus the CLI round-trip suite Program.SelfTest runs after them
        # (N-bd-codex-converter-1-03). The hardening self-test derives the registrations from the sources and holds
        # this number equal to them, and holds this the ONE copy of the floor: no caller may carry its own.
        $shimejiSuiteFloor = 13
        Write-Host '=== ShimejiConvert selftest'
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

if ($failures.Count -gt 0) {
    throw ("$($failures.Count) ShimejiConvert check failure(s): " + ($failures -join ' | '))
}
Write-Host "PASS: ShimejiConvert built, verify over Companions, selftest with $shimejiSuiteCount suites."
"SHIMEJI-SUITE-COUNT: $shimejiSuiteCount"
