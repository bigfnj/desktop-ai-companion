# Shared result grading. Importing this file starts no process and reads no product data.
function Assert-HarnessSummary([string[]]$Output, [string]$Name, [string]$Summary, [int]$Minimum) {
    $summaries = @($Output | Where-Object { "$_" -cmatch $Summary })
    if ($Minimum -lt 1 -or $summaries.Count -ne 1 -or
        @($Output | Where-Object { "$_" -cmatch '^(FAIL:|RESULT=FAIL|SKIP:)' }).Count -gt 0) {
        throw "HarnessOutput.ps1: $Name did not report one passing summary without failures/skips."
    }
    $count = [int]([regex]::Match([string]$summaries[0], $Summary).Groups[1].Value)
    if ($count -lt $Minimum) {
        throw "HarnessOutput.ps1: $Name assertion inventory fell below its recorded floor ($count < $Minimum)."
    }
    return $count
}

function Get-ModuleAssertionCount([string[]]$Lines, [string]$Id, [int]$Minimum = 0) {
    # Host convention checks are deliberately excluded from the module's own evidence.
    $tag = '^\s*\[' + [regex]::Escape($Id) + '\]\s*'
    $verbose = @($Lines | Where-Object { "$_" -cmatch ($tag + 'PASS:') }).Count
    # ModuleConventionSelfTest prefixes the same detail lines in its marker and stdout.
    # Some modules publish each assertion; AgentBroker publishes its measured total instead.
    $pattern = $tag + '([0-9]+) checks passed\s*$'
    $summaries = @($Lines | Where-Object { "$_" -cmatch $pattern })
    if ($summaries.Count -gt 1) { throw "HarnessOutput.ps1: $Id reported multiple module assertion counters." }
    $count = $verbose
    if ($summaries.Count -eq 1) {
        $declared = 0
        if (-not [int]::TryParse([regex]::Match([string]$summaries[0], $pattern).Groups[1].Value, [ref]$declared) -or $declared -lt 1) {
            throw "HarnessOutput.ps1: $Id reported an invalid module assertion counter."
        }
        if ($verbose -ne 0 -and $verbose -ne $declared) {
            throw "HarnessOutput.ps1: $Id terse and verbose module assertion counts differ."
        }
        $count = $declared
    }
    if ($Minimum -lt 0 -or $count -lt $Minimum) {
        throw "HarnessOutput.ps1: $Id module assertion inventory fell below its recorded floor ($count < $Minimum)."
    }
    return $count
}

function Assert-CoreRegressionSummary([string[]]$Lines) {
    return Assert-HarnessSummary $Lines 'CoreTests' '^PASS: ([1-9][0-9]*) DesktopAICompanion core regression groups\.$' 149
}
