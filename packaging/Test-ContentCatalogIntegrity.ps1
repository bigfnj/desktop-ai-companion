#requires -Version 5
<#
.SYNOPSIS
    Every asset catalog.json offers must still hash to what the catalog says.

.DESCRIPTION
    The gap this closes, found by an audit on 2026-09-17: nothing verified catalog.json's asset
    hashes or its membership. Test-ModulePublishFreshness.ps1 checks app.version and the six module
    VERSIONS, and that is all. So editing any `Companions\<id>\animations.xml` or `packs\<id>.txt`
    and committing without re-running New-ContentCatalog.ps1 left a recorded sha256 that no longer
    matches the blob raw.githubusercontent.com serves -- and then every user's download of that asset
    is REFUSED by the app's own hash check, while every gate stays green.

    That is the worst shape a failure can have here: the app is right to refuse, the catalog is
    wrong, and the only signal is a user reporting that a companion will not download.

    Three properties, all against the COMMITTED blob rather than the working tree, because that is
    what raw serves and a checkout has CRLF where git stores LF (see ContentCatalogAssets.ps1):

      1. every catalog entry's sha256 and byte count match the asset
      2. every catalog entry's asset exists at all
      3. the catalog's MEMBERSHIP matches what is on disk, in both directions -- an asset added and
         never catalogued is invisible to users, and an asset removed while still catalogued is a
         download that 404s

    Property 3 is why this counts rather than iterating: a per-entry loop over a catalog that lost
    half its entries passes every assertion it runs.

.PARAMETER RepoRoot
    Defaults to the parent of this script's directory.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}
. (Join-Path $PSScriptRoot 'ContentCatalogAssets.ps1')

$catalogPath = Join-Path $RepoRoot 'catalog.json'
if (-not (Test-Path -LiteralPath $catalogPath -PathType Leaf)) {
    throw "catalog.json is missing: $catalogPath"
}
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json

# The catalog records absolute raw URLs; the repo-relative path is everything after the branch. Taken
# from the URL rather than rebuilt from the id, so a wrong PATH in the catalog is caught too -- which
# a path rebuilt from the id by construction cannot be.
$rawPrefix = 'https://raw.githubusercontent.com/bigfnj/desktop-ai-companion/master/'

function Get-RelativePathFromUrl([string]$Url) {
    if ([string]::IsNullOrWhiteSpace($Url)) { return $null }
    if (-not $Url.StartsWith($rawPrefix, [StringComparison]::Ordinal)) { return $null }
    # FORWARD slashes, unchanged: this string goes to `git cat-file blob HEAD:<path>`. Converting it
    # to a Windows path made cat-file fail on every asset, so Get-CatalogAsset fell back to
    # CR-stripped working-tree bytes and this script reported five of the six module zips as
    # mismatched on its first run. Join-Path handles the separator for the on-disk check below.
    return $Url.Substring($rawPrefix.Length)
}

$checked = 0
$problems = New-Object 'System.Collections.Generic.List[string]'

foreach ($group in @(
        @{ Name = 'companion'; Items = @($catalog.companions) },
        @{ Name = 'pack';      Items = @($catalog.packs) },
        @{ Name = 'module';    Items = @($catalog.modules) })) {
    foreach ($item in $group.Items) {
        if ($null -eq $item) { continue }
        $id = [string]$item.id
        $relative = Get-RelativePathFromUrl ([string]$item.url)
        if ($null -eq $relative) {
            $problems.Add("$($group.Name) '$id': url is not a raw.githubusercontent path for this repo ($($item.url))")
            continue
        }
        $full = Join-Path $RepoRoot $relative
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            $problems.Add("$($group.Name) '$id': catalog offers $relative, which does not exist")
            continue
        }
        $asset = Get-CatalogAsset $RepoRoot $relative $full
        $checked++
        # A verifier must not accept the working-tree fallback. It exists for the generator's sake --
        # a brand-new asset is not committed yet -- and for a binary it yields a wrong hash, so
        # accepting it here would compare the catalog against bytes raw.githubusercontent.com never
        # serves.
        if ($asset.Source -ne 'blob') {
            $problems.Add(
                "$($group.Name) '$id': $relative is not committed, so the catalog's hash cannot be " +
                "verified against what raw.githubusercontent.com would serve. Commit it, then " +
                "re-run packaging\New-ContentCatalog.ps1.")
            continue
        }
        if ($asset.Sha256 -ne ([string]$item.sha256).ToLowerInvariant()) {
            $problems.Add(
                "$($group.Name) '$id': sha256 mismatch on $relative -- catalog says " +
                "$(([string]$item.sha256).Substring(0, 12))..., the committed blob is " +
                "$($asset.Sha256.Substring(0, 12))...")
        }
        elseif ([int]$item.bytes -ne [int]$asset.Bytes) {
            $problems.Add(
                "$($group.Name) '$id': byte count mismatch on $relative -- catalog says " +
                "$($item.bytes), the committed blob is $($asset.Bytes)")
        }
    }
}

# ---- membership, both directions --------------------------------------------------------------
# A catalogued asset that no longer exists is caught above. This is the other half: an asset that
# exists and is NOT catalogued, which is invisible to every user and fails nothing.
$diskCompanions = @(
    Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'Companions') -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'animations.xml') -PathType Leaf } |
        ForEach-Object { $_.Name.ToLowerInvariant() })
$catalogCompanions = @(@($catalog.companions) | ForEach-Object { ([string]$_.id).ToLowerInvariant() })
$missingCompanions = @($diskCompanions | Where-Object { $catalogCompanions -notcontains $_ })
if ($missingCompanions.Count -gt 0) {
    $problems.Add(
        "$($missingCompanions.Count) companion(s) exist under Companions\ but are absent from " +
        "catalog.json, so no user is offered them: " + (($missingCompanions | Sort-Object) -join ', ') +
        ". Re-run packaging\New-ContentCatalog.ps1.")
}

$diskPacks = @(
    Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'packs') -Filter '*.txt' -File |
        ForEach-Object { $_.BaseName.ToLowerInvariant() })
$catalogPacks = @(@($catalog.packs) | ForEach-Object { ([string]$_.id).ToLowerInvariant() })
$missingPacks = @($diskPacks | Where-Object { $catalogPacks -notcontains $_ })
if ($missingPacks.Count -gt 0) {
    $problems.Add(
        "$($missingPacks.Count) fortune pack(s) exist under packs\ but are absent from catalog.json: " +
        (($missingPacks | Sort-Object) -join ', ') + ". Re-run packaging\New-ContentCatalog.ps1.")
}

# The count floor. Without it, a catalog that lost every entry would pass every loop above by
# iterating nothing -- the failure this file exists to make impossible.
if ($checked -lt 1) {
    throw ("catalog.json produced no verifiable assets at all, so nothing above ran. " +
           "That is a broken catalog, not a clean run.")
}

if ($problems.Count -gt 0) {
    throw ("catalog.json does not describe the committed content:" + [Environment]::NewLine +
           ($problems -join [Environment]::NewLine))
}

Write-Host ("Catalog integrity OK: $checked asset(s) hash to their catalog entry, " +
            "$($diskCompanions.Count) companions and $($diskPacks.Count) packs all catalogued.") `
    -ForegroundColor DarkGray
