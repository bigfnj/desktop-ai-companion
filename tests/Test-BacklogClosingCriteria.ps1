<#
.SYNOPSIS
  Fails when an OPEN backlog item's own closing criterion is already satisfied.

.DESCRIPTION
  Three entries in BACKLOG.md were found stale on 2026-09-21/22, all fixed by later work in the
  same cycle that filed them, none linking back. "Remember to update the backlog" is exactly the
  instruction that failed all three times, so this is the mechanism instead.

  The opening is that all three entries STATED their own closing criterion, and all three were
  machine-checkable:

    "To close: read turn_context.approval_policy"     -> that string present in a named file
    "ContextChanged is declared add { } remove { }"   -> that pattern absent from a named file

  So an item may carry a CLOSES-WHEN line. This evaluates every one on an OPEN item and fails
  listing any whose condition already holds, in the same gate run that proves the fix works, while
  the author is still looking at it.

  DELIBERATELY OPT-IN. Many items cannot have a criterion -- "walk the live smoke test", "look at
  the About window" -- and pretending otherwise would be worse than not checking. The report says
  how many items carry one and how many do not, so the coverage is a number you can see rather
  than an impression.

  IT CANNOT PRODUCE A FALSE "STILL OPEN". The only false alarm available to it is "this looks
  closeable", which a human resolves in seconds by closing the item or correcting the criterion.

  AND IT FAILS WHEN IT CANNOT RUN. A criterion naming a file that no longer exists would grep
  nothing, match nothing, and go quiet for ever -- a check that silently stops checking, which is
  the failure mode this repo has recorded more than once. A missing target is an ERROR, not a pass.

.PARAMETER Path
  BACKLOG.md. Defaults to the one beside the repo root.
#>
[CmdletBinding()]
param(
    [string] $Path = '',
    [string] $RepoRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolved in the BODY, not in the param defaults: $PSScriptRoot is not reliably populated there
# under powershell.exe, and the failure is a Split-Path on an empty string rather than anything
# that names the real problem.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrEmpty($RepoRoot)) { $RepoRoot = Split-Path -Parent $here }
if ([string]::IsNullOrEmpty($Path)) { $Path = Join-Path $RepoRoot 'BACKLOG.md' }

if (-not (Test-Path -LiteralPath $Path)) { throw "No BACKLOG.md at $Path" }

# Glyph patterns are built from CODE POINTS, not from regex escapes. PowerShell 5.1's engine
# rejects \u{...} outright ("Insufficient hexadecimal digits"), and the pin is U+1F4CC -- outside
# the BMP, so a surrogate pair that \uXXXX cannot express either. run-gate.ps1 runs under
# powershell.exe, so a pattern that only compiles under pwsh 7 is a script that works right up
# until the gate runs it. Caught exactly that way while writing this.
$glyphPin  = [char]::ConvertFromUtf32(0x1F4CC)
$glyphBox  = [char]::ConvertFromUtf32(0x2B1C)
$glyphTick = [char]::ConvertFromUtf32(0x2705)
$glyphStop = [char]::ConvertFromUtf32(0x1F6D1)

# The list marker is OPTIONAL. Requiring '- ' or '###' before the glyph is what this pattern used
# to do, and it made four open items invisible -- three of them the newest in the file, written as a
# bare pin at column zero. The report then said "14 open item(s)" with total confidence while the
# real number was 18, and the one item on the file's stale list that had been wrong since the day it
# was written was among the four it could not see.
$markerPart = '^\s*(?:[-*]\s*|#{1,6}\s*)?'
$openPattern = $markerPart + '(' + [regex]::Escape($glyphPin) + '|' + [regex]::Escape($glyphBox) + ')'
$closedPattern = $markerPart + '(' + [regex]::Escape($glyphTick) + '|' + [regex]::Escape($glyphStop) + ')'

# And a glyph the parser did not attribute to any item is now an ERROR rather than a silent skip.
# Widening the pattern fixes the four shapes that exist TODAY; this is what stops the next new shape
# from going quiet the same way, because a status glyph that reaches no item is either a parser gap
# or a malformed entry and both want a human. Exempt: the "How this file works" section, which
# explains what each glyph means and so necessarily writes them mid-sentence.
$anyGlyph = '(' + [regex]::Escape($glyphPin) + '|' + [regex]::Escape($glyphBox) + '|' +
            [regex]::Escape($glyphTick) + '|' + [regex]::Escape($glyphStop) + ')'

# -Encoding UTF8 EXPLICITLY. Without it, PowerShell 5.1 reads a BOM-less UTF-8 file as ANSI, the
# glyphs mangle, nothing matches the open-item pattern, and this reports "0 open items, OK" -- a
# silent pass on a file it could not read. Found by mutation-testing this very script against
# BOM-less fixtures, where all four cases including the must-fail one came back green.
$lines = Get-Content -LiteralPath $Path -Encoding UTF8
$open = 0
$withCriteria = 0
$satisfied = New-Object System.Collections.Generic.List[string]
$broken = New-Object System.Collections.Generic.List[string]

$currentIsOpen = $false
$currentTitle = ''
$currentLine = 0

function Test-Criterion {
    param([string] $Verb, [string] $Target, [string] $Needle, [string] $Where,
          [string] $Root, $BrokenList)

    $full = Join-Path $Root $Target
    $present = Test-Path -LiteralPath $full

    # The existence verbs ASK about the target, so a missing file is their answer, not a fault.
    # Only the grep verbs need it to be there -- for those, a missing file would match nothing,
    # report nothing and go quiet for ever, which is a check that has silently stopped checking.
    switch ($Verb) {
        'file-exists' { return $present }
        'file-absent' { return (-not $present) }
        'grep-present' {
            if (-not $present) {
                $BrokenList.Add("$Where : CLOSES-WHEN greps '$Target', which does not exist. Fix the path or use file-absent.")
                return $false
            }
            return [bool] (Select-String -LiteralPath $full -Pattern $Needle -SimpleMatch -Quiet)
        }
        'grep-absent' {
            if (-not $present) {
                $BrokenList.Add("$Where : CLOSES-WHEN greps '$Target', which does not exist. Fix the path or use file-absent.")
                return $false
            }
            return -not [bool] (Select-String -LiteralPath $full -Pattern $Needle -SimpleMatch -Quiet)
        }
        default {
            $BrokenList.Add("$Where : unknown CLOSES-WHEN verb '$Verb'. Use grep-present, grep-absent, file-exists or file-absent.")
            return $false
        }
    }
}

# Where the legend stops and the backlog proper starts: the SECOND '## ' heading. Everything from
# there on is entries, so a glyph there has to belong to one.
$firstEntryLine = $lines.Count
$seenHeadings = 0
for ($h = 0; $h -lt $lines.Count; $h++) {
    if ($lines[$h] -match '^##\s') {
        $seenHeadings++
        if ($seenHeadings -eq 2) { $firstEntryLine = $h; break }
    }
}

for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]

    # Orphan check first, so it sees the line whether or not the item patterns below claim it.
    if ($i -ge $firstEntryLine -and $line -match $anyGlyph -and
        -not ($line -match $openPattern) -and -not ($line -match $closedPattern)) {
        $broken.Add("BACKLOG.md:$($i + 1) : a status glyph that belongs to no item. Put it at the start of a bullet or heading, or this item is invisible to every count in this report.")
    }

    if ($line -match $openPattern) {
        $currentIsOpen = $true
        $currentLine = $i + 1
        $currentTitle = ($line -replace '^\s*(-|###)\s*\S+\s*', '').Trim()
        if ($currentTitle.Length -gt 90) { $currentTitle = $currentTitle.Substring(0, 90) + '...' }
        $open++
        continue
    }
    if ($line -match $closedPattern) { $currentIsOpen = $false; continue }

    if ($line -match '^\s*CLOSES-WHEN:\s*(\S+)\s+(\S+)(?:\s+"([^"]*)")?\s*$') {
        $verb = $Matches[1]
        $target = $Matches[2]
        $needle = ''
        if ($Matches.Count -ge 4) { $needle = $Matches[3] }
        $where = "BACKLOG.md:" + ($i + 1)

        if (-not $currentIsOpen) { continue }   # criteria on closed items are not this check's job
        $withCriteria++

        if (Test-Criterion -Verb $verb -Target $target -Needle $needle -Where $where `
                           -Root $RepoRoot -BrokenList $broken) {
            $satisfied.Add("$where : `"$currentTitle`" (filed at line $currentLine) -- its own CLOSES-WHEN is now satisfied.")
        }
    }
}

# ZERO OPEN ITEMS IS NOT A PASS. It is either a finished backlog or a file this could not parse,
# and the check cannot tell the two apart -- so it refuses to be the one that quietly says fine.
# The same reasoning as the missing-target case above: a control that can run degraded has to say
# so on every run rather than skip in silence.
if ($open -eq 0) {
    throw "Found NO open items in $Path. Either every item is closed, or the glyph pattern did not match and this check just did nothing. It will not report OK on that ambiguity."
}

$withoutCriteria = $open - $withCriteria
Write-Host ("backlog closing criteria: {0} open item(s), {1} carry a CLOSES-WHEN, {2} do not (not checkable here)" -f $open, $withCriteria, $withoutCriteria)

if ($broken.Count -gt 0) {
    Write-Host ''
    foreach ($b in $broken) { Write-Host "  BROKEN  $b" }
    throw "A CLOSES-WHEN criterion could not be evaluated. That is a check which has silently stopped checking, so it fails rather than passing quietly."
}

if ($satisfied.Count -gt 0) {
    Write-Host ''
    foreach ($s in $satisfied) { Write-Host "  CLOSEABLE  $s" }
    throw "$($satisfied.Count) open backlog item(s) meet their own closing criterion. Close them, or correct the criterion if the fix is not really done."
}

Write-Host 'OK   no open item meets its own closing criterion'
