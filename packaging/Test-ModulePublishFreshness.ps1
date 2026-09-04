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

function Get-LastCommit([string]$path) {
    # -- separates the pathspec from revisions, so a path that looks like a ref cannot be mistaken
    # for one. Empty output = the path has no commits (untracked or never committed).
    $sha = & git -C $RepoRoot log -1 --format=%H -- $path 2>$null
    if ($LASTEXITCODE -ne 0) { throw "git log failed for '$path'." }
    if ([string]::IsNullOrWhiteSpace($sha)) { return $null }
    return $sha.Trim()
}

# Every path OUTSIDE modules\<Name>\ whose content still ends up inside <id>.zip. Watching only the module
# directory (which is all this script did until 2026-08-27) is blind to the two ways a module's payload
# changes without its own folder being touched, and BOTH are live in this repo:
#
#   * SOURCE-LINKED files. modules\PetStudio compiles 7 files out of src\ and 13 out of
#     tools\ShimejiConvert.Engine\ (PetStudio.csproj), so editing src\dotNet\CompanionXmlValidator.cs rebuilds
#     PetStudio.dll while this check stayed green -- exactly the bug class the script exists to catch,
#     arriving through shared sources instead of module sources.
#   * BUNDLED project references. ModuleKit is referenced WITHOUT Private="false", so its DLL is copied into
#     every module folder and ships in every zip; a ModuleKit edit staleness-es all five payloads.
#
# Derived from the csproj rather than hardcoded, so a module that starts linking something new is covered
# without editing this script. ProjectReferences are followed recursively and those marked Private="false"
# are skipped -- that is precisely the "the host owns the single shared copy" marker, so DesktopAICompanion.Contracts
# drops out on its own (a Contracts edit does not change the module payload) and no id needs special-casing.
#
# DELIBERATELY OUT OF SCOPE: ProductVersion.props. ModuleKit stamps its assembly Version from it, so a host
# version bump does change the bundled DLL's bytes -- but demanding all five modules be republished on every
# release, for a version field and no functional change, would make this gate hostile enough to be routed
# around. Source changes are what this watches.
function Get-ModuleWatchSet {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ModuleDirectory
    )

    $root = $RepoRoot.TrimEnd('\', '/')
    $moduleFull = [IO.Path]::GetFullPath($ModuleDirectory).TrimEnd('\')
    $external = New-Object 'Collections.Generic.List[string]'
    $degraded = New-Object 'Collections.Generic.List[string]'
    $seenProjects = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)

    $queue = New-Object 'Collections.Generic.Queue[string]'
    foreach ($proj in @(Get-ChildItem -LiteralPath $ModuleDirectory -Filter '*.csproj' -File)) {
        $queue.Enqueue($proj.FullName)
    }
    if ($queue.Count -eq 0) { $degraded.Add("no .csproj under $ModuleDirectory") }

    while ($queue.Count -gt 0) {
        $projectPath = $queue.Dequeue()
        if (-not $seenProjects.Add($projectPath)) { continue }
        if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
            $degraded.Add("referenced project is missing: $projectPath")
            continue
        }

        try { [xml]$document = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8 }
        catch { $degraded.Add("could not parse $projectPath : $($_.Exception.Message)"); continue }

        $projectDirectory = Split-Path -Parent $projectPath
        $nodes = $document.SelectNodes(
            '//*[local-name()="Compile" or local-name()="EmbeddedResource" or local-name()="None" ' +
            'or local-name()="Content" or local-name()="ProjectReference"]')

        foreach ($node in $nodes) {
            $include = [string]$node.GetAttribute('Include')
            if ([string]::IsNullOrWhiteSpace($include)) { continue }
            $isProjectReference = ($node.LocalName -eq 'ProjectReference')

            # Private="false" == the host supplies this assembly, so it is NOT in the payload.
            if ($isProjectReference -and ([string]$node.GetAttribute('Private')) -ieq 'false') { continue }

            # $(Pkg<PackageId>) is MSBuild's GeneratePathProperty convention: it always resolves into the
            # NuGet package folder, never into this repository, so it can never be a repo-source staleness.
            # Skipped without a warning because it is known-benign (Fortunes licenses two ONNX Runtime files
            # this way); anything ELSE unresolved is reported, because a watch set that quietly shrinks is
            # exactly how this check went blind to source-linked files in the first place.
            if ($include -match '\$\(Pkg') { continue }
            if ($include -match '\$\(') { $degraded.Add("unresolved MSBuild property in $($node.LocalName) '$include' ($projectPath)"); continue }

            # A wildcard names a set, not a file. Watch the deepest wildcard-free ancestor directory, which is
            # a superset of the glob and so can only ever over-report, never miss.
            $literal = $include
            if ($literal -match '[\*\?]') {
                $segments = $literal -split '[\\/]'
                $keep = @()
                foreach ($segment in $segments) {
                    if ($segment -match '[\*\?]') { break }
                    $keep += $segment
                }
                if ($keep.Count -eq 0) { $degraded.Add("un-anchorable wildcard in '$include' ($projectPath)"); continue }
                $literal = ($keep -join '\')
            }

            try { $full = [IO.Path]::GetFullPath((Join-Path $projectDirectory $literal)) }
            catch { $degraded.Add("could not resolve '$include' ($projectPath)"); continue }

            if ($isProjectReference) {
                $queue.Enqueue($full)
                # The referenced project's whole directory is watched: its own sources are what rebuild the
                # DLL that gets copied in. Its nested references are followed on the next pass.
                $full = (Split-Path -Parent $full)
            }

            if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
                $degraded.Add("outside the repository, not watched: '$include' ($projectPath)")
                continue
            }
            # Anything under the module's own folder is already covered by the module-directory watch.
            if ($full.TrimEnd('\').StartsWith($moduleFull, [StringComparison]::OrdinalIgnoreCase)) { continue }

            $relative = $full.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
            if ($relative) { $external.Add($relative) }
        }
    }

    return [pscustomobject]@{
        External = @($external | Sort-Object -Unique)
        Degraded = @($degraded | Sort-Object -Unique)
    }
}

$modulesJson = Join-Path $RepoRoot 'modules-dist\modules.json'
if (-not (Test-Path -LiteralPath $modulesJson)) {
    throw "modules.json not found at $modulesJson"
}

$ids = @()
foreach ($m in (Get-Content -LiteralPath $modulesJson -Raw | ConvertFrom-Json).modules) {
    $ids += $m.id
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
if (Test-Path -LiteralPath $catalogPath) {
    foreach ($m in (Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json).modules) {
        $catalogVersions[[string]$m.id] = [string]$m.version
    }
}

$stale = @()
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

    # Commits touching anything the published zip is built from that it cannot possibly contain. Markdown is
    # excluded because it never reaches the assembly -- modules\Fortunes\BACKLOG.md would otherwise
    # demand a 31 MB republish for a note. Everything else stays in scope on purpose: images and
    # welcome.json are embedded resources, and probe/self-test code compiles into the shipped DLL just
    # like anything else, so it genuinely does make the published payload stale.
    $sourceRelative = "modules/$($sourceDirectory.Name)"
    $modulePathspec = @($sourceRelative, ":(exclude)$sourceRelative/**/*.md", ":(exclude)$sourceRelative/*.md")

    $watch = Get-ModuleWatchSet -RepoRoot $RepoRoot -ModuleDirectory $sourceDirectory.FullName
    # Fail loudly rather than silently narrowing: a watch set that shrinks without saying so is how this
    # check was blind to source-linked files for months.
    foreach ($note in $watch.Degraded) { Write-Warning "$id -- watch set degraded: $note" }

    $watchedPathspecs = @($modulePathspec) + @($watch.External)
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
