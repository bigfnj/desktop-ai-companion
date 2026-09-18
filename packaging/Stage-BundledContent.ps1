#requires -Version 5
<#
.SYNOPSIS
    Stage the offline content bundled into the portable ZIP (pets + fortune packs).

.DESCRIPTION
    Single source of truth for what the portable build carries offline. Copies
    every pet skin (each folder's animations.xml + optional icon.png, plus the
    companions.json author manifest) under <StagingRoot>\companions\<folder>\..., and the
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

$petsStage = Join-Path $StagingRoot 'companions'
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

$petsManifest = Join-Path $petsSource 'companions.json'
if (-not (Test-Path -LiteralPath $petsManifest -PathType Leaf)) {
    throw ("Companion manifest not found: $petsManifest. It carries every bundled companion's author, " +
           "so a portable ZIP without it ships the art with no attribution. This was a silent skip for " +
           "as long as the guard looked for the pre-rename 'pets.json'.")
}
Copy-Item -LiteralPath $petsManifest -Destination $petsStage

foreach ($pack in
    Get-ChildItem -LiteralPath $packsSource -Filter '*.txt' -File) {
    Copy-Item -LiteralPath $pack.FullName -Destination $fortunesStage
}

$petCount = @(Get-ChildItem -LiteralPath $petsStage -Directory).Count
$packCount = @(
    Get-ChildItem -LiteralPath $fortunesStage -Filter '*.txt' -File).Count
# EVERY name in $bundledPets, not "at least one". The floor was -lt 1 against a hand-maintained list
# of six, so renaming or deleting five of the six folders passed: the `continue` above skips a missing
# name in silence and one survivor satisfied the check. The user would have opened the portable
# build's companion picker and found a single pet.
#
# Not hypothetical in this script. Its own comments at the pet and pack loops record the Pets ->
# Companions rename defeating two other guards here, which is the same failure: a name-matched copy
# whose misses are invisible.
$stagedPetNames = @(Get-ChildItem -LiteralPath $petsStage -Directory | ForEach-Object { $_.Name })
$missingPets = @($bundledPets | Where-Object { $stagedPetNames -notcontains $_ })
if ($missingPets.Count -gt 0) {
    throw (
        "Bundled content staging is missing " + $missingPets.Count + " of the " + $bundledPets.Count +
        " curated pets: " + ($missingPets -join ', ') + ". Either the folder was renamed or removed " +
        "under Companions\, or it has no animations.xml. Fix the folder, or remove the name from " +
        "`$bundledPets in packaging\Stage-BundledContent.ps1 -- deliberately shipping fewer has to be " +
        "said out loud, because the portable build's companion picker is what the user sees.")
}
if ($petCount -lt 1 -or $packCount -lt 1) {
    throw (
        "Bundled content staging produced no pets ($petCount) or " +
        "no fortune packs ($packCount).")
}
Write-Host (
    "Staged bundled content: $petCount pets, $packCount fortune packs." ) `
    -ForegroundColor DarkGray
