#requires -Version 5
<#
.SYNOPSIS
    Fail when a module's published zip is older than the module's source.

.DESCRIPTION
    modules-dist\<id>.zip is the payload the in-app catalog downloads and installs, and it is a
    committed artifact: nothing rebuilds it automatically, so it silently rots whenever module
    source lands without someone remembering to re-run New-ModuleDistZip.ps1 + New-ContentCatalog.ps1.

    That is not hypothetical. Twice in one day:
      * fortunes.zip shipped without the built-in fortune corpus, because the S3 move dropped the
        EmbeddedResource from the base csproj and the module never picked it up. A lean install had
        nothing to say, and nothing anywhere reported it.
      * aibrain.zip sat one release behind PR #71, so every catalog install got an AI Brain with no
        Windows OCR fallback -- i.e. no screen reading at all without a separate Tesseract install.

    The check is deliberately git-based rather than a rebuild-and-compare-hashes: comparing hashes
    would need the module DLLs to build byte-identically across SDK versions and checkout paths,
    which is a stronger promise than this repo makes today. Commit ordering is exact, cheap, and
    environment-independent -- if a path the payload is built FROM has a commit newer than the newest
    commit touching modules-dist/<id>.zip, the published payload cannot contain that change.

    "A path the payload is built from" is a per-module WATCH SET, not just modules/<Id>/. Until
    2026-08-27 it was just the module directory, which was blind to the two ways a payload changes
    without that directory being touched -- source-linked files out of src/ and tools/, and bundled
    ProjectReferences like ModuleKit. Both are live here, and the day the watch set was widened it
    immediately found fortunes, aibrain and petstudio all shipping a ModuleKit 3-4 commits stale.
    See Get-ModuleWatchSet for how the set is derived and what is deliberately excluded.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of this script's directory.

.PARAMETER ModuleId
    Check only this module. Defaults to every id listed in modules-dist\modules.json.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$ModuleId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).ProviderPath

# git's EXIT CODE is the only reliable verdict from a native command in this file. Under Windows
# PowerShell 5.1 a redirected stderr line -- `2>$null` included -- becomes an ErrorRecord that honours
# $ErrorActionPreference = 'Stop', so the very case each exit-code branch was written for, git saying
# on stderr WHY it failed, terminated the script on the redirect before the branch was reached: a
# `git log` on a path outside the repository died as a raw 'fatal: ... is outside repository' instead
# of this script's own message (F212, reproduced under 5.1.26100; pwsh 7 is unaffected). Same shape
# as New-ModulePublish.ps1's Invoke-Git: errors non-terminating for the one call, judged by exit code,
# preference restored in the finally. Returns the stdout lines; the exit code lands in $script:GitExit.
$script:GitExit = 0
function Invoke-GitLines([string[]]$GitArgs) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& git -C $RepoRoot @GitArgs 2>&1)
        $script:GitExit = $LASTEXITCODE
        return @($output | Where-Object { $_ -isnot [Management.Automation.ErrorRecord] } | ForEach-Object { [string]$_ })
    }
    finally { $ErrorActionPreference = $previous }
}

function Get-LastCommit([string]$path) {
    # -- separates the pathspec from revisions, so a path that looks like a ref cannot be mistaken
    # for one. Empty output = the path has no commits (untracked or never committed).
    $sha = (@(Invoke-GitLines @('log', '-1', '--format=%H', '--', $path)) -join '')
    if ($script:GitExit -ne 0) { throw "git log failed for '$path'." }
    if ([string]::IsNullOrWhiteSpace($sha)) { return $null }
    return $sha.Trim()
}

# The per-module WATCH SET -- every path outside modules\<Name>\ whose content still ends up inside the
# zip -- is derived in packaging\ModuleWatchSet.ps1, shared with New-ModulePublish.ps1 since 2026-09-30
# so the publish guard refuses exactly the dirt this check would later call stale (F215). What it watches,
# what it deliberately excludes (ProductVersion.props) and why the implicit MSBuild inputs joined it
# (F220) is recorded there, once.
. (Join-Path $PSScriptRoot 'ModuleWatchSet.ps1')

# One or more entries out of a zip, by entry NAME (case-insensitive), copied through the archive's own
# entry streams and never through a scratch directory. [IO.Compression.ZipFile] is not loaded by default
# under 5.1, hence the Add-Type; WixToolchainPolicy.ps1 hashes entries the same way.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Read-ZipEntryBytes {
    param(
        [Parameter(Mandatory = $true)][string]$ZipPath,
        [Parameter(Mandatory = $true)][string[]]$EntryNames
    )
    $wanted = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entryName in $EntryNames) { [void]$wanted.Add($entryName) }
    $found = New-Object 'Collections.Generic.List[object]'
    $archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        foreach ($entry in $archive.Entries) {
            if (-not $wanted.Contains($entry.Name)) { continue }
            $entryStream = $entry.Open()
            try {
                $memory = New-Object IO.MemoryStream
                try {
                    $entryStream.CopyTo($memory)
                    $found.Add([pscustomobject]@{ Name = $entry.Name; FullName = $entry.FullName; Bytes = $memory.ToArray() })
                }
                finally { $memory.Dispose() }
            }
            finally { $entryStream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    # The bare list, enumerated on output; callers wrap the call in @(). `return @($found)` throws
    # 'Argument types do not match' under Windows PowerShell 5.1 for a List[object] of custom objects
    # (measured 2026-09-30), and pwsh 7 does the same on this shape.
    return $found
}

$modulesJson = Join-Path $RepoRoot 'modules-dist\modules.json'
if (-not (Test-Path -LiteralPath $modulesJson)) {
    throw "modules.json not found at $modulesJson"
}

$ids = @()
foreach ($m in (Get-Content -LiteralPath $modulesJson -Raw | ConvertFrom-Json).modules) {
    $ids += $m.id
}

# Deliberately-unpublished modules. This check derives its id list from modules.json, so a module
# absent from that file produces ZERO findings -- its version parity and payload freshness are simply
# never examined. That is correct for a module nobody ships and wrong for one somebody forgot, and
# until this list existed the check could not tell the two apart. Adding a module to modules/ without
# publishing it now requires saying so here, once.
#
#   testmodule  a throwaway plugin-pipeline proof, dev and self-test only, never catalogued
#
# agentflow was on this list until 2026-09-17 and came off it when it was published, which is the
# point of keeping the list short: an entry here is a claim that nobody ships the module, and a stale
# one would quietly exempt a PUBLISHED module from every freshness and parity check in this file.
$deliberatelyUnpublished = @('testmodule')

$sourceIds = @()
foreach ($proj in @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'modules') -Directory)) {
    $sourceIds += $proj.Name.ToLowerInvariant()
}
$unaccounted = @($sourceIds | Where-Object { ($ids -notcontains $_) -and ($deliberatelyUnpublished -notcontains $_) })
if ($unaccounted.Count -gt 0) {
    throw ("These modules exist under modules\ but are neither listed in modules.json nor declared " +
           "deliberately unpublished, so nothing checks their version parity or payload freshness: " +
           ($unaccounted -join ', ') + ". Publish them, or add them to " +
           "`$deliberatelyUnpublished in packaging\Test-ModulePublishFreshness.ps1 with a reason.")
}
if ($ModuleId) {
    if ($ids -notcontains $ModuleId) { throw "Module '$ModuleId' is not listed in modules.json." }
    $ids = @($ModuleId)
}

# modules.json's version per id, for the parity check below.
$declaredVersions = @{}
foreach ($m in (Get-Content -LiteralPath $modulesJson -Raw | ConvertFrom-Json).modules) {
    $declaredVersions[[string]$m.id] = [string]$m.version
}

# catalog.json's version per id. The catalog is generated FROM modules.json, but it is a separately
# committed artifact that the app actually fetches from master, so it can be stale on its own -- and it is
# the file ModuleUpdateScan compares against to decide whether to offer an update.
$catalogVersions = @{}
$catalogPath = Join-Path $RepoRoot 'catalog.json'
$catalogAppVersion = $null
if (Test-Path -LiteralPath $catalogPath) {
    $catalogObject = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
    foreach ($m in $catalogObject.modules) {
        $catalogVersions[[string]$m.id] = [string]$m.version
    }
    if ($catalogObject.PSObject.Properties['app'] -and $catalogObject.app.PSObject.Properties['version']) {
        $catalogAppVersion = [string]$catalogObject.app.version
    }
}

# ---- catalog.json's app.version must equal ProductVersion.props ----
# This is the version the LAUNCH UPDATE CHECK compares against. AppUpdateCheck reads it from the catalog
# on master rather than the GitHub releases API (deliberately -- see the class comment there), so a stale
# app.version does not degrade the check, it SILENTLY INVERTS it: every user is told they are current.
#
# It rots because nothing in a host release touches the catalog. release.yml never regenerates it, and
# New-ContentCatalog.ps1 runs only as part of a MODULE publish, so a run of host releases with no module
# publish between them leaves the number behind. That happened for real: the catalog was last generated
# while ProductVersion.props said 1.1.0, then v1.1.1, v1.1.2 and v1.1.3 shipped -- three releases, the
# last of which fixed the tray icon -- and no user on 1.1.0 was ever offered any of them.
#
# New-ContentCatalog.ps1 already refuses to write a BLANK app.version for this reason. A blank one
# disables the check; a stale one is worse, because the check still runs and still answers "no".
$productVersionProps = Join-Path $RepoRoot 'ProductVersion.props'
[xml]$versionPropsXml = Get-Content -LiteralPath $productVersionProps
$productVersion = ([string]$versionPropsXml.Project.PropertyGroup.DesktopAICompanionVersion).Trim()
if ([string]::IsNullOrWhiteSpace($productVersion)) {
    throw "Could not read <DesktopAICompanionVersion> from $productVersionProps."
}
if ($null -eq $catalogAppVersion) {
    throw ("catalog.json has no app.version. The launch update check reads the latest version from there, " +
           "so without it the app can never tell a user an update exists. Regenerate it: " +
           "packaging\New-ContentCatalog.ps1")
}
#
# The rule is NOT "catalog.json equals ProductVersion.props", which is what it used to be. Those two
# answer different questions: props is the newest version that has been BUILT, catalog.json is the
# newest version a user can actually DOWNLOAD, and between an ABI bump and the release that ships it
# they are legitimately different. The old rule forced them together, which left only two ways to
# land an ABI change -- cut a release immediately, or publish a catalog advertising a version that
# does not exist on the releases page. The second is the worse one and the old rule made it the path
# of least resistance.
#
# So it is now two checks, and neither is weaker than what it replaced:
#
#   1. The catalog must not EXCEED props. A catalog ahead of the build offers every user an update
#      that cannot be downloaded.
#   2. The catalog must MATCH THE NEWEST RELEASED TAG. This is the original protection, stated
#      against the thing that actually determines it. The incident described above -- the catalog
#      stuck at 1.1.0 while v1.1.1, v1.1.2 and v1.1.3 shipped, so nobody on 1.1.0 was offered the
#      tray-icon fix -- fails check 2 exactly as it failed the old one. What no longer fails is an
#      unreleased build sitting in the tree, which was never the defect.
$catalogVersionParsed = $null
$productVersionParsed = $null
if (-not [version]::TryParse($catalogAppVersion, [ref]$catalogVersionParsed)) {
    throw "catalog.json app.version '$catalogAppVersion' is not a version number."
}
if (-not [version]::TryParse($productVersion, [ref]$productVersionParsed)) {
    throw "ProductVersion.props <DesktopAICompanionVersion> '$productVersion' is not a version number."
}
if ($catalogVersionParsed -gt $productVersionParsed) {
    throw ("catalog.json app.version is '$catalogAppVersion' but ProductVersion.props only says " +
           "'$productVersion'. The catalog is what every installed app reads to decide an update " +
           "exists, so this offers every user a version that has not been built and cannot be " +
           "downloaded. Regenerate the catalog (packaging\New-ContentCatalog.ps1) and commit it.")
}

# Check 2 needs tags. It FAILS rather than degrading, for the reason spelled out in the sibling
# Test-ModuleTemplate.ps1: a control that can run degraded has to say so in a way that stops the run,
# or it is a check that quietly stopped checking.
$releaseTags = @(Invoke-GitLines @('tag', '--list', 'v*'))
if ($script:GitExit -ne 0 -or $releaseTags.Count -eq 0) {
    Write-Warning ("DEGRADED  no v* tags are reachable, so catalog.json app.version could not be " +
                   "checked against the newest release (shallow clone?)")
    throw ("Coverage narrowed silently: the catalog-versus-newest-release check could not run because " +
           "no v* tags are reachable. Fetch tags (CI uses fetch-depth: 0) and re-run. This refuses " +
           "rather than passing, because a catalog that trails the newest release means no user is " +
           "ever offered it, which is the failure this check exists for.")
}
$taggedVersions = @()
foreach ($releaseTag in $releaseTags) {
    $parsedTag = $null
    if ([version]::TryParse($releaseTag.TrimStart('v'), [ref]$parsedTag)) { $taggedVersions += $parsedTag }
}
if ($taggedVersions.Count -eq 0) {
    throw "No v* tag parsed as a version number, so the newest release could not be determined."
}
$newestRelease = @($taggedVersions | Sort-Object -Descending | Select-Object -First 1)[0]
if ($catalogVersionParsed -ne $newestRelease) {
    throw ("catalog.json app.version is '$catalogAppVersion' but the newest release is " +
           "'v$newestRelease'. The launch update check reads the catalog, so while it trails the " +
           "newest release every user at or above '$catalogAppVersion' is told they are up to date. " +
           "Regenerate the catalog (packaging\New-ContentCatalog.ps1) and commit it -- merging to " +
           "master is what publishes it.")
}
if ($catalogVersionParsed -lt $productVersionParsed) {
    Write-Host ("OK   app -- catalog.json app.version $catalogAppVersion matches the newest release " +
                "v$newestRelease; ProductVersion.props is ahead at $productVersion (built, not released yet)")
}
else {
    Write-Host "OK   app -- catalog.json app.version $catalogAppVersion matches the newest release and the built version"
}

$stale = @()
$degraded = @()
$mismatched = @()
foreach ($id in $ids) {
    # The module's source directory is capitalized (modules\Fortunes) while its id and zip are not;
    # resolve the real directory rather than assuming either casing survives a case-sensitive host.
    $sourceDirectory = Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'modules') -Directory |
        Where-Object { $_.Name -ieq $id } |
        Select-Object -First 1
    if (-not $sourceDirectory) {
        throw "Module '$id' is listed in modules.json but has no source directory under modules\."
    }

    # ---- version parity: source ModuleInfo.Version == modules.json == catalog.json ----
    # The in-app Update button compares the module's LIVE ModuleInfo.Version against the catalog's, so a
    # mismatch is not cosmetic: publish a catalog version below the shipped one and no update is ever
    # offered; publish one above it and the update is offered forever, surviving every install.
    $moduleClass = @(Get-ChildItem -LiteralPath $sourceDirectory.FullName -Filter '*Module.cs' -File)
    if ($moduleClass.Count -ne 1) {
        $mismatched += [pscustomobject]@{
            Id = $id
            Reason = "expected exactly one *Module.cs in $($sourceDirectory.Name), found $($moduleClass.Count)"
        }
    } else {
        $moduleSource = Get-Content -LiteralPath $moduleClass[0].FullName -Raw
        # Anchored to the start of the line: an unanchored 'Version\s*=' also matches MinHostVersion, which
        # sits two lines below it in every module.
        $versionMatches = @([regex]::Matches($moduleSource, '(?m)^\s*Version\s*=\s*"([^"]+)"'))
        if ($versionMatches.Count -ne 1) {
            $mismatched += [pscustomobject]@{
                Id = $id
                Reason = "found $($versionMatches.Count) ModuleInfo.Version declarations in $($moduleClass[0].Name); expected exactly 1"
            }
        } else {
            $sourceVersion = $versionMatches[0].Groups[1].Value
            $jsonVersion = $declaredVersions[$id]
            $catalogVersion = if ($catalogVersions.ContainsKey($id)) { $catalogVersions[$id] } else { '(absent)' }
            if ($sourceVersion -ne $jsonVersion -or $sourceVersion -ne $catalogVersion) {
                $mismatched += [pscustomobject]@{
                    Id = $id
                    Reason = "version mismatch -- source $sourceVersion, modules.json $jsonVersion, catalog.json $catalogVersion"
                }
            } else {
                Write-Host "OK   $id -- version $sourceVersion agrees across source, modules.json and catalog.json"
            }
        }
    }

    $zipRelative = "modules-dist/$id.zip"
    if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot $zipRelative))) {
        $stale += [pscustomobject]@{ Id = $id; Reason = "$zipRelative is missing" }
        continue
    }

    $zipCommit = Get-LastCommit $zipRelative
    if (-not $zipCommit) {
        $stale += [pscustomobject]@{ Id = $id; Reason = "$zipRelative is not committed" }
        continue
    }

    # Commits touching anything the published zip is built from that it cannot possibly contain. The
    # pathspecs come from the shared helper (the module directory less its Markdown, plus the watch set)
    # so this check and the publish guard judge one and the same list; the Markdown exclusion and its
    # reason are recorded beside Get-ModuleWatchPathspecs.
    $watch = Get-ModuleWatchPathspecs -RepoRoot $RepoRoot -ModuleDirectory $sourceDirectory
    $sourceRelative = $watch.SourceRelative
    $modulePathspec = $watch.ModulePathspec
    # Fail loudly rather than silently narrowing: a watch set that shrinks without saying so is how this
    # check was blind to source-linked files for months.
    #
    # This said exactly that and then called Write-Warning, which sets no exit code, throws nothing,
    # is unaffected by $ErrorActionPreference and never reached $stale. Both callers judge this script
    # by failure alone, so the watch set COULD silently narrow -- a $(Property) in an Include, an
    # out-of-repo link, an unparseable csproj or a missing referenced project all degrade coverage and
    # still printed OK. The comment was right and the code did the opposite of it.
    foreach ($note in $watch.Degraded) {
        Write-Warning "$id -- watch set degraded: $note"
        $degraded += "$id -- watch set degraded: $note"
    }

    $watchedPathspecs = @($watch.Pathspecs)
    $newer = @(& git -C $RepoRoot log --format='%h %s' "$zipCommit..HEAD" -- @watchedPathspecs)
    if ($LASTEXITCODE -ne 0) { throw "git log failed comparing '$sourceRelative' against $zipCommit." }

    if ($newer.Count -gt 0) {
        # Attribute the staleness to the specific watched path(s), so the fix is obvious. Only done on the
        # failure path, so the common case stays one git call per module.
        $culprits = New-Object 'Collections.Generic.List[string]'
        foreach ($candidate in (@(, $modulePathspec) + @($watch.External | ForEach-Object { , @($_) }))) {
            $hits = @(& git -C $RepoRoot log --format='%h' "$zipCommit..HEAD" -- @candidate)
            if ($hits.Count -gt 0) { $culprits.Add("$($candidate[0]) ($($hits.Count))") }
        }
        $stale += [pscustomobject]@{
            Id     = $id
            Reason = "$($newer.Count) commit(s) newer than $zipRelative touch: " + ($culprits -join ', ')
            Detail = $newer
        }
    } else {
        $extra = if ($watch.External.Count -gt 0) { " (+ $($watch.External.Count) linked/bundled path(s))" } else { '' }
        Write-Host "OK   $id -- $zipRelative is current with $sourceRelative$extra"
    }
}

if ($mismatched.Count -gt 0) {
    Write-Host ''
    foreach ($m in $mismatched) { Write-Host "MISMATCH $($m.Id) -- $($m.Reason)" }
    Write-Host ''
    throw ("Module version(s) disagree across source, modules-dist\modules.json and catalog.json: " +
           ($mismatched.Id -join ', ') +
           ". Bump modules-dist\modules.json to match the module's ModuleInfo.Version, then regenerate " +
           "the catalog (packaging\New-ContentCatalog.ps1). The in-app Update button compares these, so a " +
           "mismatch either offers an update forever or never offers one at all.")
}

# A narrowed watch set is a failure in its own right, not a note. It means this check is now
# examining LESS than it believes it is, which is the state it was written to prevent.
if ($degraded.Count -gt 0) {
    Write-Host ''
    Write-Host 'Watch set degraded -- this check is examining less than it should:' -ForegroundColor Red
    foreach ($note in $degraded) { Write-Host "  - $note" -ForegroundColor Red }
    throw ("$($degraded.Count) module watch set(s) degraded. Coverage narrowed silently, which is " +
           "how this check was blind to source-linked files for months.")
}

if ($stale.Count -gt 0) {
    Write-Host ''
    foreach ($s in $stale) {
        Write-Host "STALE $($s.Id) -- $($s.Reason)"
        if ($s.PSObject.Properties.Name -contains 'Detail') {
            foreach ($line in $s.Detail) { Write-Host "        $line" }
        }
    }
    Write-Host ''
    throw ("Published module payload(s) are behind their source: " +
           ($stale.Id -join ', ') +
           ". Rebuild (build.ps1 -Release), re-zip (packaging\New-ModuleDistZip.ps1), COMMIT the zip, " +
           "then regenerate the catalog (packaging\New-ContentCatalog.ps1) -- in that order, because " +
           "the catalog hashes the committed blob.")
}

Write-Host "All $($ids.Count) published module payload(s) are current with their source."

# ---------------------------------------------------------------------------------------------------
# CONTENT check, not a recency check. Everything above compares COMMIT ORDER: is the zip's commit newer
# than the commits touching its sources. That is blind to a zip built from stale bits and committed
# afterwards, which is exactly what shipped: fortunes.zip was committed FIVE SECONDS after the commit
# that fixed the pack grouping, so every recency test called it current, while the DLL inside it had
# been built before the fix and was short by seven pack-to-collection mappings. The user saw them land
# in the fallback "More packs" group -- a regression that had already been fixed in the repo.
#
# Generalising this to every embedded resource means reading .NET manifest resources properly, which is
# more machinery than it is worth here. collections.json is checked because it is the one that broke,
# it is small, and it is pure data the emitter copies verbatim, so a substring search over the DLL is
# sufficient and cannot false-negative: if the current bytes are absent, the zip is stale.
$collectionsPath = Join-Path $repoRoot 'packs\collections.json'
$fortunesZip = Join-Path $repoRoot 'modules-dist\fortunes.zip'
# A MISSING INPUT IS A THROW, NOT A SKIP. This block sat behind `if (both exist)` with no else, so a
# renamed collections.json would have turned the one guard against a stale-built zip into a silent
# pass, in a file that refuses on every other narrowed run (F221). collections.json is a build input
# the Fortunes module embeds and fortunes.zip is a committed artifact, so neither has a legitimate
# absent case; the condition never consulted -ModuleId, so it was only ever a skip.
if (-not (Test-Path -LiteralPath $collectionsPath -PathType Leaf)) {
    throw "packs\collections.json is missing at $collectionsPath, so the pack-to-collection content check cannot run."
}
if (-not (Test-Path -LiteralPath $fortunesZip -PathType Leaf)) {
    throw "modules-dist\fortunes.zip is missing at $fortunesZip, so the pack-to-collection content check cannot run."
}
$expected = (Get-Content -LiteralPath $collectionsPath -Raw | ConvertFrom-Json)
$expectedPairs = [System.Collections.Generic.List[string]]::new()
foreach ($c in $expected.collections) {
    foreach ($s in $c.sources) { $expectedPairs.Add("$($c.name)`t$s") }
}

# ENTRY STREAMS, not Expand-Archive. This expanded the whole 49 MB payload -- the ONNX model and the
# runtime included -- into a scratch directory under TEMP to read one sub-megabyte DLL, and the CodeView
# scan below did the same for every zip: about 150 MB written and deleted per gate run, fortunes twice
# (F222). Read-ZipEntryBytes copies only the named entry through the archive's own stream. No scratch
# directory, so the best-effort Remove-Item that used to follow, and the orphan it could leave, are gone.
$shippedFortunes = @(Read-ZipEntryBytes -ZipPath $fortunesZip -EntryNames @('Fortunes.dll'))
if ($shippedFortunes.Count -eq 0) { throw "fortunes.zip contains no Fortunes.dll to inspect." }
$shipped = [Text.Encoding]::UTF8.GetString($shippedFortunes[0].Bytes)

# THE MAPPING, STRUCTURALLY. This used to search for each source id ON ITS OWN and use
# $expectedPairs only for counts, so a pack MOVED between collections left every id present
# and passed -- while the success line claimed all the mappings were embedded. Only an ADDED
# id was ever caught, which is what the original incident happened to be. The old comment
# said the search "cannot false-negative"; true of ids, false of mappings.
#
# collections.json is embedded verbatim as a resource, so the real thing is available: find
# it, parse it, and compare collection -> sources exactly. A move, a rename, a reorder
# between collections and a dropped source all fail now.
$embedded = $null
$atCollections = $shipped.IndexOf('"collections"', [StringComparison]::Ordinal)
while ($atCollections -ge 0 -and $null -eq $embedded) {
    # Walk back to the object that owns the key, then forward to its matching brace, counting
    # depth and skipping anything inside a string so a brace in a description cannot fool it.
    $start = $shipped.LastIndexOf('{', $atCollections)
    if ($start -lt 0) { break }
    $depth = 0; $inString = $false; $escaped = $false; $end = -1
    for ($i = $start; $i -lt $shipped.Length; $i++) {
        $ch = $shipped[$i]
        if ($escaped) { $escaped = $false; continue }
        if ($ch -eq '\') { $escaped = $true; continue }
        if ($ch -eq '"') { $inString = -not $inString; continue }
        if ($inString) { continue }
        if ($ch -eq '{') { $depth++ }
        elseif ($ch -eq '}') { $depth--; if ($depth -eq 0) { $end = $i; break } }
    }
    if ($end -gt $start) {
        try { $embedded = $shipped.Substring($start, $end - $start + 1) | ConvertFrom-Json }
        catch { $embedded = $null }
        if ($null -ne $embedded -and -not $embedded.PSObject.Properties['collections']) { $embedded = $null }
    }
    $atCollections = $shipped.IndexOf('"collections"', $atCollections + 1, [StringComparison]::Ordinal)
}
# THROWS rather than falling back to the old id-only search. A check that quietly downgrades
# itself to a weaker one is exactly the failure mode this file keeps correcting.
if ($null -eq $embedded) {
    throw ("Could not locate the embedded collections.json inside the shipped Fortunes.dll, so " +
           "the pack-to-collection mapping cannot be verified. If the module stopped embedding " +
           "it verbatim, this check needs rewriting rather than skipping.")
}

function Get-CollectionMap($doc) {
    $map = @{}
    foreach ($c in $doc.collections) {
        $src = @()
        if ($c.PSObject.Properties['sources'] -and $null -ne $c.sources) { $src = @($c.sources) }
        $map[[string]$c.name] = (($src | Sort-Object) -join '|')
    }
    return $map
}

$wantMap = Get-CollectionMap $expected
$gotMap = Get-CollectionMap $embedded
$problems = [System.Collections.Generic.List[string]]::new()
foreach ($name in $wantMap.Keys) {
    if (-not $gotMap.ContainsKey($name)) { $problems.Add("collection '$name' is absent from the shipped DLL"); continue }
    if ($wantMap[$name] -ne $gotMap[$name]) {
        $problems.Add("collection '$name' has different sources: expected [" +
                      ($wantMap[$name] -replace '\|', ', ') + "], shipped [" +
                      ($gotMap[$name] -replace '\|', ', ') + "]")
    }
}
foreach ($name in $gotMap.Keys) {
    if (-not $wantMap.ContainsKey($name)) { $problems.Add("collection '$name' is in the shipped DLL but not in packs/collections.json") }
}
if ($problems.Count -gt 0) {
    Write-Host ''
    foreach ($m in $problems | Select-Object -First 12) { Write-Host "  $m" }
    if ($problems.Count -gt 12) { Write-Host "  ... and $($problems.Count - 12) more" }
    throw ("modules-dist/fortunes.zip was built before the current packs/collections.json: " +
           "$($problems.Count) pack-to-collection difference(s) between the shipped Fortunes.dll " +
           "and packs/collections.json. Affected packs fall into the fallback 'More packs' group " +
           "for every user. REBUILD the module (New-ModulePublish.ps1 WITHOUT -SkipBuild) rather " +
           "than re-zipping, then commit the zip and regenerate the catalog.")
}
Write-Host ("fortunes.zip embeds all $($expectedPairs.Count) current pack-to-collection mappings " +
            "across $($wantMap.Count) collection(s), compared structurally.")

# ---------------------------------------------------------------------------------------------------
# NO SHIPPED DLL MAY CARRY AN ABSOLUTE BUILD PATH.
#
# An embedded PDB path names the machine, the account and the directory layout of whoever built the
# assembly. DebugType=embedded is what keeps it out, set in ModuleKit.csproj and Contracts.csproj with
# a comment on each warning against re-adding IncludeSymbols (which fails dotnet pack with NU5017).
# The setting was fixed and the zips rebuilt on 2026-09-17; what did not exist until now is anything
# that would NOTICE it being reverted, so the fix had no regression net.
#
# A path reaches a DLL through the CodeView entry of the debug directory, which stores it as a plain
# NUL-terminated string in the image -- so a byte scan finds it and no PE parser is needed.
#
# EVERY DLL IN EVERY ZIP, deliberately, not the two files that were once wrong. The failure this
# guards is a NEW assembly arriving without the setting, and a check scoped to where the problem
# already was is exactly the kind that cannot fail.
$buildPathZips = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'modules-dist') -Filter '*.zip' -ErrorAction SilentlyContinue)
if ($buildPathZips.Count -eq 0) { throw 'No modules-dist/*.zip to scan for embedded build paths.' }

$buildPathPattern = [regex] '[A-Za-z]:\\[^\x00<>|]{0,200}?\.pdb'
$buildPathOffenders = [System.Collections.Generic.List[string]]::new()
$buildPathDlls = 0

# SCOPED TO ASSEMBLIES THIS REPO BUILDS, and derived from the .csproj files rather than a hardcoded
# list -- so a NEW project of ours arriving without the setting is caught automatically, which is the
# whole property the backlog entry wanted.
#
# The first run of this check found 7 paths and every one belonged to a prebuilt third-party package:
# Microsoft.Windows.SDK.NET and WinRT.Runtime from Microsoft's agents (C:\__w\1\s, D:\a\_work\1\s)
# and onnxruntime from ONNX's (N:\_work\1). We cannot set DebugType on a NuGet binary, those paths
# leak somebody else's CI layout rather than this user's machine, and a check nobody can make pass is
# its own defect.
$ourAssemblies = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($proj in Get-ChildItem -LiteralPath $repoRoot -Recurse -Filter '*.csproj' -ErrorAction SilentlyContinue) {
    if ($proj.FullName -like '*\obj\*' -or $proj.FullName -like '*\bin\*') { continue }
    # AssemblyName when declared, otherwise the project file name, which is msbuild's own default.
    $declared = $null
    try {
        $m = [regex]::Match((Get-Content -LiteralPath $proj.FullName -Raw), '<AssemblyName>\s*([^<]+?)\s*</AssemblyName>')
        if ($m.Success) { $declared = $m.Groups[1].Value }
    }
    catch { }
    # if/else, not a ternary: `?:` is PowerShell 7 syntax and a PARSE error under 5.1, which every
    # tracked script in this repo is checked against.
    $assemblyName = $declared
    if ([string]::IsNullOrWhiteSpace($assemblyName)) {
        $assemblyName = [IO.Path]::GetFileNameWithoutExtension($proj.Name)
    }
    $null = $ourAssemblies.Add($assemblyName + '.dll')
}
if ($ourAssemblies.Count -eq 0) { throw 'No .csproj found, so the build-path scan would check nothing.' }

# ENTRY STREAMS, not Expand-Archive. Every zip used to be expanded whole into a scratch directory under
# TEMP -- the 32 MB ONNX model, the 15 MB runtime and the 24 MB WinRT projection included -- to read the
# handful of sub-megabyte first-party DLLs this scan is scoped to; with the fortunes block above doing
# the same for one DLL, about 150 MB was written and deleted per gate run (F222). Only the named entries
# are copied now, and there is no scratch directory left to remove or to orphan.
foreach ($bpZip in $buildPathZips) {
    foreach ($bpDll in @(Read-ZipEntryBytes -ZipPath $bpZip.FullName -EntryNames @($ourAssemblies))) {
        $buildPathDlls++
        # Latin-1, so every byte maps to exactly one char. A UTF-8 decode can merge or drop bytes
        # and a path could slip through the gap.
        $bpText = [Text.Encoding]::GetEncoding(28591).GetString($bpDll.Bytes)
        foreach ($bpMatch in $buildPathPattern.Matches($bpText)) {
            $buildPathOffenders.Add($bpZip.Name + " -> " + $bpDll.Name + " : " + $bpMatch.Value)
        }
    }
}

if ($buildPathOffenders.Count -gt 0) {
    Write-Host ''
    foreach ($bpO in $buildPathOffenders | Select-Object -Unique | Select-Object -First 12) {
        Write-Host "  embedded build path: $bpO"
    }
    throw ("$($buildPathOffenders.Count) embedded build path(s) found in published DLLs, which names the " +
           "machine, account and directory of whoever built them. Set <DebugType>embedded</DebugType> in " +
           "the offending project -- and do NOT add IncludeSymbols, which fails dotnet pack with NU5017 -- " +
           "then rebuild, re-zip, COMMIT the zip and regenerate the catalog.")
}
if ($buildPathDlls -eq 0) {
    throw ('The build-path scan examined ZERO DLLs, so it proved nothing. Either the zips contain none ' +
           'of this repo''s own assemblies, or the .csproj-derived name set stopped matching what is ' +
           'shipped. Fix the scope rather than accepting a silent pass.')
}
Write-Host "no embedded build paths in $buildPathDlls of our own published DLL(s) across $($buildPathZips.Count) zip(s)." 
