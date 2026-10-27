<#
.SYNOPSIS
    Leak soak for a MODULE-OWNED WPF window (Pet Studio by default).

.DESCRIPTION
    runtime-resource-soak.ps1 drives the shipped app from outside, and its in-process churn loop
    (Program.RuntimeResourceChurn) only exercises pets/speech and the tray -- it never opens a window a module
    owns, and the host holds no compile-time reference to any module. So a module window's HWNDs, Bitmaps and
    decoded sprite sheets were covered by nothing until this.

    Like the other soak, this is deliberately NOT in the blocking gate: it needs a real window station and
    growth thresholds flake on a headless runner. Run it before tagging a release, and record the numbers in the
    release notes so the next release has something to compare against.

    Pass criteria, strongest first:
      1. every window is unreachable after an LOH-compacting GC;
      2. handles / GDI / USER are flat across the LAST segment;
      3. private bytes show no upward trend: the least-squares slope of the sample after each segment, over
         every segment, stays under 16 MiB a segment (the exe's PrivateBytesJudge, N-release-150-01).
    Segment 1's growth from the cold start is excluded from 2 and 3 on purpose: the first pass legitimately sets
    a high watermark while the sprite sheet decodes and caches fill. Comparing segments with each other, rather
    than with a cold start, is what makes the memory signal usable.

.PARAMETER Cycles
    Open/close cycles per segment. Default 20.

.PARAMETER Segments
    Number of segments. Default 5, at least 4: the private-byte judge needs a slope over three segments of
    change, and the handle/GDI/USER checks compare the last segment against the previous one.

.PARAMETER Module
    Path to the module DLL to drive. Defaults to the built Pet Studio.

.PARAMETER Pet
    Path to the companion XML to load. Defaults to Companions\blue_sheep\animations.xml, chosen because its ~1.1 MB sprite
    sheet is large enough for the memory signal to mean something -- and it is the pet the original sprite
    re-decode bug was found on.

.EXAMPLE
    .\tests\module-window-soak.ps1
#>
[CmdletBinding()]
param(
    # Floors at BINDING time, because the exe's own Options.Parse used to swap a non-positive count for its
    # default in silence: -Segments 0 ran two segments and PASSED while -Segments 1 ran a full wasted segment
    # before the exe's hard FAIL (F399, F390). The exe refuses such a value itself now; the wrapper refuses
    # it first, with PowerShell's own message naming the parameter.
    [ValidateRange(1, [int]::MaxValue)][int]$Cycles = 20,
    [ValidateRange(4, [int]::MaxValue)][int]$Segments = 5,
    [string]$Module,
    [string]$Pet,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot 'DesktopAICompanion.WindowSoak\DesktopAICompanion.WindowSoak.csproj'
$exe = Join-Path $PSScriptRoot "DesktopAICompanion.WindowSoak\bin\$Configuration\DesktopAICompanion.WindowSoak.exe"

if (-not $Module) {
    $Module = Join-Path $repoRoot "build\DesktopAICompanionPortable\bin\$Configuration\x64\modules\petstudio\PetStudio.dll"
}
if (-not (Test-Path -LiteralPath $Module)) {
    throw "No module DLL at $Module. Build it first: .\build.ps1 -$Configuration"
}

Write-Host '=== build the soak harness' -ForegroundColor Cyan
& dotnet build $project -c $Configuration --nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "harness build failed (exit $LASTEXITCODE)" }

# QUOTED. Start-Process joins -ArgumentList with spaces and quotes nothing, so a -Module or -Pet path with a
# space in it (a clone under a spaced directory, a module staged under a spaced folder) reached the exe as
# two argv tokens and it exited 2 with "unknown argument '<second half>'" (F399; measured under 5.1 and
# pwsh: 8 tokens unquoted, 6 quoted). Not ProcessStartInfo.ArgumentList, which does not exist on the .NET
# Framework that Windows PowerShell 5.1 runs on.
$soakArgs = @('--module', ('"{0}"' -f $Module), '--cycles', $Cycles, '--segments', $Segments)
if ($Pet) { $soakArgs += @('--pet', ('"{0}"' -f $Pet)) }

Write-Host '=== soak' -ForegroundColor Cyan
# Start-Process -Wait -PassThru so the exit code is read directly: piping a native exe's output masks it, which
# has previously made a stale result read as PASS.
$run = Start-Process -FilePath $exe -ArgumentList $soakArgs -Wait -PassThru -NoNewWindow
if ($run.ExitCode -ne 0) {
    Write-Host "MODULE WINDOW SOAK FAILED (exit $($run.ExitCode))" -ForegroundColor Red
    exit 1
}
Write-Host 'MODULE WINDOW SOAK PASSED.' -ForegroundColor Green