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
#
# Usage (both arms are BUILT exes; the baseline is a build of the parent commit, e.g. `git worktree add <tmp>
# HEAD~1` then `.\build.ps1 -Release` there):
#   .\standdown-ab.ps1 -Baseline <parent-build>\DesktopAICompanion.exe -Changed <this-build>\DesktopAICompanion.exe
# The probe defaults to this folder's own Release build of walkcount.csproj; build it first. Until F394 the two
# arms, the probe and the scratch root were hardcoded to one session's temp folder and a removed worktree, so
# the script threw on every machine including the one it was written on.
param(
    [int[]] $Phases = @(0, 40, 80, 120, 160, 200, 240, 280, 320, 360),
    [Parameter(Mandatory = $true)] [string] $Baseline,
    [Parameter(Mandatory = $true)] [string] $Changed,
    [string] $Probe = '',
    [string] $WorkRoot = (Join-Path ([IO.Path]::GetTempPath()) ("standdown-ab-" + $PID))
)
$ErrorActionPreference = 'Stop'   # a failed Remove-Item or New-Item on a data root stops the run, never samples a dirty root

# Resolved in the body, not as the parameter's default: Windows PowerShell 5.1 evaluates a script's parameter
# defaults before $PSScriptRoot is set when the script is run with -File, and Join-Path then refuses the empty
# string (measured 2026-09-30).
if ([string]::IsNullOrEmpty($Probe)) { $Probe = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\walkcount.exe' }
if (-not (Test-Path -LiteralPath $Probe)) {
    throw "missing probe at $Probe -- build it first: dotnet build tests\fullscreen-standdown-probe\walkcount.csproj -c Release"
}
$arms = [ordered]@{
    BASELINE = $Baseline
    CHANGED  = $Changed
}
foreach ($arm in $arms.Keys) { if (-not (Test-Path -LiteralPath $arms[$arm])) { throw "missing $arm at $($arms[$arm])" } }
New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null

$reloc = @{ BASELINE = @(); CHANGED = @() }
$hide = @{ BASELINE = @(); CHANGED = @() }
$fails = @{ BASELINE = 0; CHANGED = 0 }
$inconc = @{ BASELINE = 0; CHANGED = 0 }

foreach ($phase in $Phases) {
    foreach ($arm in $arms.Keys) {
        $root = Join-Path $WorkRoot ("dataroot-" + $arm)
        if (Test-Path $root) { Remove-Item -Recurse -Force $root }
        New-Item -ItemType Directory -Path $root | Out-Null
        $text = (& $Probe standdown $arms[$arm] $root $phase 2>&1) -join "`n"
        # The labels StandDown.cs prints today; the old pattern predated the committed probe and would have read
        # every relocate latency as 0 ms ([int]'' is 0), so a PASS with no matching line is a parse failure here.
        $r = [regex]::Match($text, 'step3 (?:relocated to a free monitor|off the blocked monitor)=(\w+) after (\d+)ms')
        $h = [regex]::Match($text, 'step5 every companion hidden=(\w+) after (\d+)ms')
        if ($text -match 'RESULT=INCONCLUSIVE') {
            $inconc[$arm]++
            Write-Host ("phase {0,4} {1,-8} INCONCLUSIVE" -f $phase, $arm)
            continue
        }
        if ($text -match 'RESULT=PASS' -and (-not $r.Success -or -not $h.Success)) {
            $fails[$arm]++
            Write-Host ("phase {0,4} {1,-8} PARSE-FAIL  the probe passed but printed no step3/step5 latency line this driver recognises" -f $phase, $arm)
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
