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

function Get-ModuleAssertionCount([string[]]$Lines, [string]$Id) {
    # Host convention checks are deliberately excluded from the module's own evidence.
    $pattern = '^\s*\[' + [regex]::Escape($Id) + '\]\s*PASS:'
    return @($Lines | Where-Object { "$_" -cmatch $pattern }).Count
}

function Assert-CoreRegressionSummary([string[]]$Lines) {
    return Assert-HarnessSummary $Lines 'CoreTests' '^PASS: ([1-9][0-9]*) DesktopAICompanion core regression groups\.$' 149
}
