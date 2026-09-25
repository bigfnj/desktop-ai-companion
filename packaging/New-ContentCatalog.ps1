# PowerShell 7+, and it is not a preference: ConvertTo-Json formats differently under Desktop 5.1,
# which rewrites all 2,234 lines of a file that is SERVED TO EVERY USER from master. 5.1 indents with
# four spaces and puts two after each colon; 7 uses two and one. Nothing verifies catalog.json's
# formatting, so the churn is invisible until it buries the one line that actually changed in a
# review -- measured 2026-09-17, bumping app.version from 1.1.4 to 1.1.5: under powershell.exe the
# diff was 2234 insertions / 2234 deletions, under pwsh it was exactly one line.
#
# Normalizing the text afterwards was the other option and is worse: pet and pack names are free
# text that can contain runs of spaces, so a regex over serialized JSON could corrupt a VALUE in a
# file whose hashes users verify downloads against. Refusing to run is the honest guard, and #requires
# makes 5.1 itself say so before the script starts.
#
# This is the one place in the repo where the usual rule inverts. Every other .ps1 that writes a file
# must be exercised under powershell.exe, because that is what 5.1-only CI and double-click use.
#requires -Version 7
<#
.SYNOPSIS
    Regenerate catalog.json, the runtime-fetched content catalog for online pet /
    fortune-pack / plugin-module downloads.

.DESCRIPTION
    Lists every companion skin (Companions\<id>\animations.xml), every fortune pack
    (packs\<id>.txt), and every plugin module (modules-dist\<id>.zip) with a
    branch-pinned raw.githubusercontent.com URL plus the SHA-256 and byte size of
    the current file. The app fetches this over HTTPS and verifies every download
    against the recorded hash before install, so content added to the repo appears
    live without shipping a new build.

    Text files are LF-normalized (.gitattributes eol=lf); module zips are pure
    binary (.gitattributes -text) and hashed exactly as committed. Either way the
    working-tree hash equals the git blob raw.githubusercontent.com serves, PROVIDED
    the asset is already committed (a brand-new zip that isn't committed yet falls
    back to a text-oriented CRLF-normalized read, which corrupts a binary hash --
    commit modules-dist\<id>.zip before regenerating the catalog, never after).
    Run this whenever you add or change a pet, pack, or module, then commit
    catalog.json alongside the files. Pack collection/group metadata (name/desc/
    license) is reused from packs\collections.json; pet authors from Companions\companions.json;
    module name/desc/version/permissions from modules-dist\modules.json.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$Branch = 'master',
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $RepoRoot 'catalog.json'
}
if ($Branch -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$') {
    throw "Unsafe branch ref: '$Branch'"
}
$owner = 'bigfnj'
$repo = 'desktop-ai-companion'
$rawBase = "https://raw.githubusercontent.com/$owner/$repo/$Branch"

# The asset hashing lives in ContentCatalogAssets.ps1, shared with
# Test-ContentCatalogIntegrity.ps1 so the generator and the check cannot disagree about which bytes
# a recorded sha256 describes.
. (Join-Path $PSScriptRoot 'ContentCatalogAssets.ps1')

function Get-PrettyName([string]$Id) {
    $parts = @($Id -split '[_-]' | Where-Object { $_ })
    (($parts | ForEach-Object {
        $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1)
    }) -join ' ')
}

# --- pets --------------------------------------------------------------------
$petsRoot = Join-Path $RepoRoot 'Companions'
$authors = @{}
$names = @{}
$petsJson = Join-Path $petsRoot 'companions.json'
# HARD FAIL, not a skip. Without this manifest every entry silently falls back to the title-cased folder id
# with no author -- so all 53 companions come out named "Shimeji 88f9sqb5" instead of "skeleton Halloween",
# and the catalog looks plausible rather than broken. That is exactly what happened when the directory was
# renamed from Pets to Companions: this path is composed at runtime, so a search-and-replace over the
# composed string "Pets/pets.json" never touched it, Test-Path quietly returned false, and the published
# catalog lost every real name and every author at once.
if (-not (Test-Path -LiteralPath $petsJson -PathType Leaf)) {
    throw ("Companion manifest not found: $petsJson. Without it every companion would be named after its " +
           "folder id and lose its author, which is a plausible-looking catalog rather than an obvious " +
           "failure -- so this refuses to write one.")
}
if ($true) {
    foreach ($p in (Get-Content -LiteralPath $petsJson -Raw -Encoding UTF8 | ConvertFrom-Json).pets) {
        $authors[[string]$p.folder] = [string]$p.author
        # An optional explicit display name (converted skins carry their character name here); pets without
        # one fall back to the title-cased folder id, so the base pets are unchanged.
        if ($p.PSObject.Properties['name'] -and $p.name) { $names[[string]$p.folder] = [string]$p.name }
    }
}
$pets = @()
foreach ($dir in (Get-ChildItem -LiteralPath $petsRoot -Directory | Sort-Object Name)) {
    $xml = Join-Path $dir.FullName 'animations.xml'
    if (-not (Test-Path -LiteralPath $xml -PathType Leaf)) { continue }
    $id = $dir.Name
    $asset = Get-CatalogAsset $RepoRoot "Companions/$id/animations.xml" $xml
    # Animation and sound counts, so a card in "Available to download" can say what the pet CONTAINS and not
    # only how many KB it is. The app cannot work these out for itself before downloading: its own GetStats
    # reads the installed animations.xml, which is exactly the file the user has not got yet.
    #
    # Counted with the same two patterns CompanionsPaneControl.GetStats uses, deliberately, so an installed
    # card and an available card of the same pet never disagree. Both are counts of opening tags, which is
    # why `<animation ` carries its trailing space (it must not match `<animations>`) and `<sound` uses a
    # word boundary.
    $petText = Get-Content -LiteralPath $xml -Raw -Encoding UTF8
    $animationCount = ([regex]::Matches($petText, '<animation\s')).Count
    $soundCount = ([regex]::Matches($petText, '<sound\b')).Count
    if ($animationCount -lt 1) {
        throw ("Companion '$id' counted $animationCount animations. Every pet has at least one, so this is " +
               "the count being wrong rather than the pet being empty, and a catalog that ships it would " +
               "tell every user this companion is empty.")
    }
    $pets += [ordered]@{
        id         = $id
        name       = if ($names.ContainsKey($id)) { $names[$id] } else { Get-PrettyName $id }
        author     = if ($authors.ContainsKey($id)) { $authors[$id] } else { '' }
        url        = "$rawBase/Companions/$id/animations.xml"
        sha256     = $asset.Sha256
        bytes      = $asset.Bytes
        animations = $animationCount
        sounds     = $soundCount
    }
}

# A count that is zero for EVERY companion is a broken pattern, not a silent corpus. This check exists
# because the sound pattern shipped mangled once: a backslash was eaten on the way into this file, ``
# became a literal backspace byte, and every pet reported 0 sounds while the app's own card showed 35 for
# the same skin. Animations were fine, so a per-pet guard on that number saw nothing wrong. Zero sounds is
# legitimate for an individual pet, so the only version of this check that can fail is the corpus one.
$withSounds = @($pets | Where-Object { $_.sounds -gt 0 }).Count
if ($withSounds -lt 1) {
    throw ("Not one of $($pets.Count) companions reported a single sound. Some of them certainly have " +
           "sounds, so this is the counting pattern being wrong rather than the corpus being silent.")
}

# --- packs (per-source; collection metadata from packs\collections.json) -----
$packsRoot = Join-Path $RepoRoot 'packs'
$collectionsDoc = Get-Content -LiteralPath (Join-Path $packsRoot 'collections.json') -Raw -Encoding UTF8 |
    ConvertFrom-Json
$sourceCollection = @{}
foreach ($c in $collectionsDoc.collections) {
    foreach ($src in $c.sources) {
        $sourceCollection[[string]$src] = $c
    }
}
# One licence for the whole pack library, written to the catalog root instead of copied onto every
# entry. Stamping it per pack meant 158 copies of one fact, and 158 places for it to drift.
if (-not $collectionsDoc.PSObject.Properties['packLicense'] -or
    [string]::IsNullOrWhiteSpace($collectionsDoc.packLicense)) {
    throw ("packs\collections.json has no 'packLicense'. The catalog would then advertise no licence " +
           "at all for any pack, which reads as an oversight rather than the deliberate NOASSERTION " +
           "the notices file requires.")
}
$packLicense = [string]$collectionsDoc.packLicense
# Curated display names (packs\pack-names.json). Pack ids are raw file stems ("lwall-quotes",
# "rfc1925"), so the title-cased id is a poor label; fall back to it only for an unnamed pack.
$packNames = @{}
$packNamesPath = Join-Path $packsRoot 'pack-names.json'
if (Test-Path -LiteralPath $packNamesPath) {
    $namesJson = (Get-Content -LiteralPath $packNamesPath -Raw -Encoding UTF8 | ConvertFrom-Json).names
    foreach ($property in $namesJson.PSObject.Properties) {
        $packNames[$property.Name] = [string]$property.Value
    }
}

$packs = @()
foreach ($file in
    (Get-ChildItem -LiteralPath $packsRoot -Filter '*.txt' -File | Sort-Object Name)) {
    $id = [IO.Path]::GetFileNameWithoutExtension($file.Name)
    if (-not $sourceCollection.ContainsKey($id)) {
        throw "Pack '$id' has no collection in collections.json (add an entry for it there)."
    }
    $collection = $sourceCollection[$id]
    $lineCount = @(Get-Content -LiteralPath $file.FullName).Count
    $asset = Get-CatalogAsset $RepoRoot "packs/$id.txt" $file.FullName
    $packs += [ordered]@{
        id         = $id
        name       = if ($packNames.ContainsKey($id)) { $packNames[$id] } else { Get-PrettyName $id }
        group      = [string]$collection.name
        desc       = ''
        url        = "$rawBase/packs/$id.txt"
        sha256     = $asset.Sha256
        bytes      = $asset.Bytes
        count      = $lineCount
        dataSchema = 2
    }
}

# --- modules (metadata from modules-dist\modules.json; payload = modules-dist\<id>.zip) ---
$modulesDistRoot = Join-Path $RepoRoot 'modules-dist'
$modules = @()
$modulesJsonPath = Join-Path $modulesDistRoot 'modules.json'
if (Test-Path -LiteralPath $modulesJsonPath) {
    foreach ($m in (Get-Content -LiteralPath $modulesJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json).modules) {
        $id = [string]$m.id
        $zipRelPath = "modules-dist/$id.zip"
        $zipFullPath = Join-Path $modulesDistRoot "$id.zip"
        if (-not (Test-Path -LiteralPath $zipFullPath -PathType Leaf)) {
            throw "Module '$id' is listed in modules.json but '$id.zip' is missing from modules-dist\ (build + New-ModuleDistZip.ps1 it, then commit before regenerating the catalog)."
        }
        $asset = Get-CatalogAsset $RepoRoot $zipRelPath $zipFullPath
        $modules += [ordered]@{
            id          = $id
            name        = [string]$m.name
            desc        = [string]$m.desc
            version     = [string]$m.version
            url         = "$rawBase/$zipRelPath"
            sha256      = $asset.Sha256
            bytes       = $asset.Bytes
            permissions = [string]$m.permissions
        }
    }
}

# The published APP version, read from the one canonical place rather than restated here. This is what the
# launch update check reads: it compares this to the running build and, when newer, the Preferences footer
# offers a link to the releases page. Notify-only -- no asset URL or hash belongs here, because nothing about
# the app itself is ever downloaded automatically.
#
# The NEWEST RELEASE, not the newest build.
#
# This used to stamp <DesktopAICompanionVersion> from ProductVersion.props, which is the version that
# has been BUILT. Those are legitimately different between an ABI bump and the release that ships it,
# and catalog.json is fetched live from master by every installed app -- so publishing the built
# version told every user an update existed and sent them to a releases page that did not have it.
# Committing a catalog was enough to do that; no release required.
#
# The newest v* tag is what a user can actually download, so that is what goes in. The build version
# still bounds it: Test-ModulePublishFreshness.ps1 refuses a catalog ahead of ProductVersion.props,
# and refuses one that trails the newest tag -- the latter being the 1.1.0 incident, where the catalog
# sat still through v1.1.1, v1.1.2 and v1.1.3 and nobody was offered the tray-icon fix.
$repoRootForVersion = Split-Path -Parent $PSScriptRoot
$productVersionProps = Join-Path $repoRootForVersion 'ProductVersion.props'
$builtVersion = ''
if (Test-Path $productVersionProps) {
    $m = [regex]::Match(
        [IO.File]::ReadAllText($productVersionProps),
        '<DesktopAICompanionVersion>\s*([^<\s]+)\s*</DesktopAICompanionVersion>')
    if ($m.Success) { $builtVersion = $m.Groups[1].Value }
}
if (-not $builtVersion) {
    throw "Could not read <DesktopAICompanionVersion> from $productVersionProps. It is the ceiling for the catalog's app.version, so a blank one would remove the only check that a published catalog is buildable."
}

$catalogTags = @(& git -C $repoRootForVersion tag --list 'v*' 2>$null)
if ($LASTEXITCODE -ne 0 -or $catalogTags.Count -eq 0) {
    Write-Warning "DEGRADED  no v* tags are reachable, so the newest release could not be determined"
    throw "Refusing to write a catalog without knowing the newest release: app.version is what every installed app reads to decide an update exists, and guessing it from the build would advertise a download that does not exist. Fetch tags (CI uses fetch-depth: 0) and re-run."
}
$appVersionParsed = $null
foreach ($catalogTag in $catalogTags) {
    $parsedCatalogTag = $null
    if ([version]::TryParse($catalogTag.TrimStart('v'), [ref]$parsedCatalogTag)) {
        if ($null -eq $appVersionParsed -or $parsedCatalogTag -gt $appVersionParsed) { $appVersionParsed = $parsedCatalogTag }
    }
}
if ($null -eq $appVersionParsed) {
    throw "No v* tag parsed as a version number, so the newest release could not be determined."
}
$appVersion = $appVersionParsed.ToString()
$builtVersionParsed = $null
if ([version]::TryParse($builtVersion, [ref]$builtVersionParsed) -and $appVersionParsed -gt $builtVersionParsed) {
    throw "The newest release is 'v$appVersion' but ProductVersion.props only says '$builtVersion'. A catalog cannot advertise a version this tree cannot build."
}
if ($appVersion -ne $builtVersion) {
    Write-Host "  app.version $appVersion (newest release); ProductVersion.props is ahead at $builtVersion, not released yet"
}

# Force arrays so a single entry still serializes as a JSON array.
$catalog = [ordered]@{
    version     = 1
    app         = [ordered]@{ version = $appVersion; releases = "https://github.com/bigfnj/desktop-ai-companion/releases" }
    packLicense = $packLicense
    companions  = @($pets)
    packs       = @($packs)
    modules     = @($modules)
}
$json = $catalog | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText(
    $OutputPath,
    $json,
    (New-Object Text.UTF8Encoding($false)))
Write-Host (
    "Wrote $($pets.Count) pets + $($packs.Count) packs + $($modules.Count) modules to $OutputPath" ) `
    -ForegroundColor Green
