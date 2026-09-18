# Interleaved A/B of the fullscreen stand-down: BASELINE (this commit's PARENT, i.e. the change
# removed) vs CHANGED. Fresh app process per sample, alternating, so a drift in machine load lands
# on both arms.
#
# The PHASE is swept, not left to chance. The app's scan cycle is anchored to when the companion
# spawned and the probe raises its window a fixed interval later, so a fixed delay samples one
# point of the cycle over and over -- the first version produced five samples inside a 30ms band,
# which a latency uniform over a 300ms cycle cannot do. Both arms see the SAME sweep.
#
# Two latencies per sample, because the invariant is per monitor:
#   relocate  one monitor covered -> the companion is off it (moved, or hidden)
#   hide      every monitor covered -> the companion is not visible at all
param(
    [int[]] $Phases = @(0, 40, 80, 120, 160, 200, 240, 280, 320, 360),
    [string] $Baseline = 'C:\Users\Admin\AppData\Local\Temp\claude\d---ai-work\d5d95e35-1b61-45ed-92e3-76dd5d60d9ab\scratchpad\baseline2\build\DesktopAICompanionPortable\bin\Release\x64\DesktopAICompanion.exe'
)

$scratch = 'C:\Users\Admin\AppData\Local\Temp\claude\d---ai-work\d5d95e35-1b61-45ed-92e3-76dd5d60d9ab\scratchpad'
$probe = Join-Path $scratch 'walkcount\bin\Release\net10.0-windows\walkcount.exe'
$arms = [ordered]@{
    BASELINE = $Baseline
    CHANGED  = 'D:\.ai-work\projects\desktop-ai-companion\.claude\worktrees\agent-ae441f4a40aad1cde\build\DesktopAICompanionPortable\bin\Release\x64\DesktopAICompanion.exe'
}
foreach ($arm in $arms.Keys) { if (-not (Test-Path $arms[$arm])) { throw "missing $arm at $($arms[$arm])" } }

$reloc = @{ BASELINE = @(); CHANGED = @() }
$hide = @{ BASELINE = @(); CHANGED = @() }
$fails = @{ BASELINE = 0; CHANGED = 0 }
$inconc = @{ BASELINE = 0; CHANGED = 0 }

foreach ($phase in $Phases) {
    foreach ($arm in $arms.Keys) {
        $root = Join-Path $scratch ("dataroot-ab2-" + $arm)
        if (Test-Path $root) { Remove-Item -Recurse -Force $root }
        New-Item -ItemType Directory -Path $root | Out-Null
        $text = (& $probe standdown $arms[$arm] $root $phase 2>&1) -join "`n"
        $r = [regex]::Match($text, 'step3 nothing on a blocked monitor=(\w+) after (\d+)ms')
        $h = [regex]::Match($text, 'step5 every companion hidden=(\w+) after (\d+)ms')
        if ($text -match 'RESULT=INCONCLUSIVE') {
            $inconc[$arm]++
            Write-Host ("phase {0,4} {1,-8} INCONCLUSIVE" -f $phase, $arm)
            continue
        }
        if ($text -match 'RESULT=PASS') {
            $reloc[$arm] += [int] $r.Groups[2].Value
            $hide[$arm] += [int] $h.Groups[2].Value
            Write-Host ("phase {0,4} {1,-8} pass  off-blocked-monitor={2}ms  all-hidden={3}ms" -f `
                $phase, $arm, $r.Groups[2].Value, $h.Groups[2].Value)
        } else {
            $fails[$arm]++
            Write-Host ("phase {0,4} {1,-8} FAIL  {2}" -f $phase, $arm,
                (($text -split "`n" | Select-String -Pattern 'RESULT=').Line))
        }
    }
}

function Spread($label, $v) {
    if ($v.Count -eq 0) { Write-Host "$label no samples"; return }
    $s = $v | Sort-Object
    $n = $s.Count
    $median = if ($n % 2 -eq 1) { $s[[int](($n - 1) / 2)] } else { ($s[$n / 2 - 1] + $s[$n / 2]) / 2 }
    Write-Host ("{0,-34} n={1,-3} min={2,4}ms median={3,6}ms MAX={4,4}ms   samples: {5}" -f `
        $label, $n, $s[0], $median, $s[$n - 1], ($s -join ' '))
}

Write-Host ''
foreach ($arm in $arms.Keys) {
    Spread "$arm off-blocked-monitor" $reloc[$arm]
    Spread "$arm all-monitors-hidden" $hide[$arm]
    Write-Host ("{0,-34} failures={1} inconclusive={2}" -f $arm, $fails[$arm], $inconc[$arm])
}
