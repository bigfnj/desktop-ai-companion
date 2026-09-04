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
      3. the last segment's private bytes barely move.
    Segment 1 is excluded from 2 and 3 on purpose: the first pass legitimately sets a high watermark while the
    sprite sheet decodes and caches fill. Comparing the last segment against the previous one, rather than
    against a cold start, is what makes the memory signal usable.

.PARAMETER Cycles
    Open/close cycles per segment. Default 20.

.PARAMETER Segments
    Number of segments. Must be at least 2, because the growth checks compare the last against the previous.

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
    [int]$Cycles = 20,
    [int]$Segments = 2,
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

$soakArgs = @('--module', $Module, '--cycles', $Cycles, '--segments', $Segments)
if ($Pet) { $soakArgs += @('--pet', $Pet) }

Write-Host '=== soak' -ForegroundColor Cyan
# Start-Process -Wait -PassThru so the exit code is read directly: piping a native exe's output masks it, which
# has previously made a stale result read as PASS.
$run = Start-Process -FilePath $exe -ArgumentList $soakArgs -Wait -PassThru -NoNewWindow
if ($run.ExitCode -ne 0) {
    Write-Host "MODULE WINDOW SOAK FAILED (exit $($run.ExitCode))" -ForegroundColor Red
    exit 1
}
Write-Host 'MODULE WINDOW SOAK PASSED.' -ForegroundColor Green