#requires -Version 5.1
<#
Content invariants for shipped companions, checked against the XML in Companions/.

WHY THIS IS A SOURCE CHECK AND NOT A SOAK. The defect it guards fired about 21 times an hour on the
owner's machine, which sounds frequent until you try to reproduce it: a 12-minute run expects about
3 events, and observing 0 is perfectly ordinary. A soak at that length is not evidence either way,
so it was dropped in favour of asserting the PROPERTY, which is deterministic.

THE PROPERTY. Animations.TNextAnimation.Eligible(only, where) is `(only & where) != 0`, with
TASKBAR = 0x01, WINDOW = 0x02, HORIZONTAL = 0x04, VERTICAL = 0x08 and NONE treated as "matches
everything". FormCompanion asks for the next border animation with a bare TOnly.TASKBAR when a pet
crosses the bottom of the work area. If every <next> in that animation's <border> declares some
other situation, the lookup returns -1, and FormCompanion.cs sets bLeavingScreen = true: the pet
does not land on the taskbar, it carries on off the bottom of the screen and is respawned.

SCOPE, stated honestly. This asserts the two states that were measured doing it, not the general
class. Deciding statically WHICH animations a pet can occupy while crossing the taskbar line needs
the reachability walk the host does at runtime, and plenty of states legitimately cannot be there
(pink_sheep's own walk_task family is already ON the taskbar, so its borders are the screen sides
and windows). The general check is BACKLOG item 387, the pet-XML validator's positive-probability
rule. This one exists so the specific fix cannot silently regress.
#>

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

# pet folder -> animation ids that MUST be able to answer a taskbar border.
# pink_sheep 173 (king_jump_top) and 182 (king_jumpB_top): measured 269 failures in ~16 hours of
# real use, 80% of all of them. Both already answered only="window" with king_slamB (166), and
# king_walk_top (167) in the same family already answered the taskbar with it, so the fix was the
# pet's own convention rather than an invention.
$required = @{
    'pink_sheep' = @('173', '182')
}

# only= values that satisfy a bare TOnly.TASKBAR (0x01). "none" is NONE, which matches everything;
# a <next> with no only= attribute at all defaults to NONE the same way.
$eligible = @('taskbar', 'none', 'unset')

$failures = New-Object System.Collections.ArrayList
$checked = 0

foreach ($pet in $required.Keys) {
    $path = Join-Path $repo (Join-Path 'Companions' (Join-Path $pet 'animations.xml'))
    if (-not (Test-Path -LiteralPath $path)) {
        [void]$failures.Add("$pet : animations.xml is missing at $path")
        continue
    }
    $xml = [xml](Get-Content -LiteralPath $path -Raw)
    # THE DEFAULT NAMESPACE IS NOT OPTIONAL. Every shipped animations.xml carries
    # xmlns="https://esheep.petrucci.ch/", so an unprefixed XPath matches nothing at all -- the first
    # version of this file silently selected zero nodes and only the "examined 0" guard below caught
    # that it was checking nothing.
    $ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
    $ns.AddNamespace('p', $xml.DocumentElement.NamespaceURI)

    foreach ($id in $required[$pet]) {
        $anim = $xml.SelectSingleNode("//p:animation[@id='$id']", $ns)
        if ($null -eq $anim) {
            [void]$failures.Add("$pet : animation id $id not found")
            continue
        }
        $borders = $anim.SelectNodes('p:border/p:next', $ns)
        if ($null -eq $borders -or $borders.Count -eq 0) {
            [void]$failures.Add("$pet id ${id}: no border edges at all")
            continue
        }
        $onlys = @()
        foreach ($n in $borders) {
            $v = $n.GetAttribute('only')
            if ([string]::IsNullOrWhiteSpace($v)) { $onlys += 'unset' } else { $onlys += $v }
        }
        $ok = @($onlys | Where-Object { $eligible -contains $_ }).Count -gt 0
        $checked++
        if ($ok) {
            Write-Host ("  ok   {0} id {1} can answer a taskbar border  [{2}]" -f $pet, $id, ($onlys -join ', '))
        } else {
            [void]$failures.Add(
                "$pet id ${id}: every border edge is ineligible at the taskbar [" + ($onlys -join ', ') +
                "] -- the pet will walk off the bottom of the screen and respawn instead of landing")
        }
    }
}

# Failures FIRST. The "examined 0" guard below used to throw ahead of this, so when the XPath was
# silently matching nothing, the one message that would have explained it was collected and never
# printed. A guard that hides its own cause is barely better than no guard.
if ($failures.Count -gt 0) {
    foreach ($f in $failures) { Write-Host ("  FAIL " + $f) -ForegroundColor Red }
    throw ("companion border invariants failed: " + $failures.Count + " failure(s), " + $checked + " checked")
}

# A check that examined nothing must not report success. If the table above is emptied, or every pet
# folder is renamed, this is the difference between "passed" and "never ran".
if ($checked -eq 0) {
    throw 'companion-border-invariants: examined 0 animations, so this proved nothing'
}

Write-Host ("companion border invariants: {0} animation(s) checked, all can answer a taskbar border" -f $checked)
