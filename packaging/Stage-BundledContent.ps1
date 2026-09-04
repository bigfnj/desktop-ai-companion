#requires -Version 5
<#
.SYNOPSIS
    Stage the offline content bundled into the portable ZIP (pets + fortune packs).

.DESCRIPTION
    Single source of truth for what the portable build carries offline. Copies
    every pet skin (each folder's animations.xml + optional icon.png, plus the
    pets.json author manifest) under <StagingRoot>\pets\<folder>\..., and the
    full fortune-pack set under <StagingRoot>\fortunes\<id>.txt.

    The caller then hands <StagingRoot>\pets and <StagingRoot>\fortunes to
    New-DeterministicPortableZip.ps1 as -ContentDirectories. The MSI never
    carries this content, so the installer stays lean; both build.ps1 and the
    release workflow stage through here so the two paths cannot diverge.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RepoRoot,
    [Parameter(Mandatory = $true)][string]$StagingRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$petsSource = Join-Path $RepoRoot 'Companions'
$packsSource = Join-Path $RepoRoot 'packs'
foreach ($required in @($petsSource, $packsSource)) {
    if (-not (Test-Path -LiteralPath $required -PathType Container)) {
        throw "Bundled-content source directory is missing: $required"
    }
}

$petsStage = Join-Path $StagingRoot 'pets'
$fortunesStage = Join-Path $StagingRoot 'fortunes'
New-Item -ItemType Directory -Path $petsStage, $fortunesStage -Force | Out-Null

# LEAN bundle: ship only a small curated set of pets beside the exe. Everything else -- most of the base pets
# and every other converted shimeji -- is download-on-demand from the catalog (they live under Companions\ so
# raw.githubusercontent serves them and New-ContentCatalog lists them, but they are not in the portable zip).
# The built-in default eSheep is EMBEDDED (not a folder), so it ships regardless of this list. Note esheep64 is
# deliberately absent: it duplicates the embedded default.
$bundledPets = @('fox', 'green_sheep', 'neko', 'ssj-goku', 'shimeji-brq51bkr', 'shimeji-uzi-doorman-ef5c7d')
foreach ($petDirectory in Get-ChildItem -LiteralPath $petsSource -Directory) {
    if ($bundledPets -notcontains $petDirectory.Name) {
        continue
    }
    $animations = Join-Path $petDirectory.FullName 'animations.xml'
    if (-not (Test-Path -LiteralPath $animations -PathType Leaf)) {
        continue
    }
    $petDestination = Join-Path $petsStage $petDirectory.Name
    New-Item -ItemType Directory -Path $petDestination -Force | Out-Null
    Copy-Item -LiteralPath $animations -Destination $petDestination
    $icon = Join-Path $petDirectory.FullName 'icon.png'
    if (Test-Path -LiteralPath $icon -PathType Leaf) {
        Copy-Item -LiteralPath $icon -Destination $petDestination
    }
}

$petsManifest = Join-Path $petsSource 'pets.json'
if (Test-Path -LiteralPath $petsManifest -PathType Leaf) {
    Copy-Item -LiteralPath $petsManifest -Destination $petsStage
}

foreach ($pack in
    Get-ChildItem -LiteralPath $packsSource -Filter '*.txt' -File) {
    Copy-Item -LiteralPath $pack.FullName -Destination $fortunesStage
}

$petCount = @(Get-ChildItem -LiteralPath $petsStage -Directory).Count
$packCount = @(
    Get-ChildItem -LiteralPath $fortunesStage -Filter '*.txt' -File).Count
if ($petCount -lt 1 -or $packCount -lt 1) {
    throw (
        "Bundled content staging produced no pets ($petCount) or " +
        "no fortune packs ($packCount).")
}
Write-Host (
    "Staged bundled content: $petCount pets, $packCount fortune packs." ) `
    -ForegroundColor DarkGray
