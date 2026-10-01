#requires -Version 5
<#
.SYNOPSIS
    The per-module WATCH SET: every path outside modules\<Name>\ whose content still ends up inside
    modules-dist\<id>.zip, derived from the module's project files.

.DESCRIPTION
    Two callers, one derivation. Test-ModulePublishFreshness.ps1 judges a published zip STALE when a
    watched path has a commit newer than the zip's; New-ModulePublish.ps1 refuses to build a zip while
    a watched path is DIRTY, because a payload committed ahead of the source it was compiled from reads
    as stale for ever and a deterministic re-zip cannot produce a new commit to repair the order.

    Until 2026-09-30 only the freshness check derived this set. The publish guard looked at
    modules\<Name> alone, while the set the check judges against also holds ModuleKit (bundled into
    every zip) and the twenty files PetStudio compiles out of src\ and tools\ -- so an uncommitted
    ModuleKit edit passed the guard, was compiled into a committed zip, and turned the gate red the
    moment it was committed itself (F215). Dot-sourcing one helper rather than keeping a copy per
    script is the house pattern: ContentCatalogAssets.ps1 keeps the catalog generator and its verifier
    on one definition of a hash for the same reason.

    Deliberately 5.1-compatible: the freshness check runs inside the gate under both shells.
#>

# Every path OUTSIDE modules\<Name>\ whose content still ends up inside <id>.zip. Watching only the module
# directory (which is all the freshness check did until 2026-08-27) is blind to the two ways a module's
# payload changes without its own folder being touched, and BOTH are live in this repo:
#
#   * SOURCE-LINKED files. modules\PetStudio compiles 7 files out of src\ and 13 out of
#     tools\ShimejiConvert.Engine\ (PetStudio.csproj), so editing src\dotNet\CompanionXmlValidator.cs rebuilds
#     PetStudio.dll while the check stayed green -- exactly the bug class the script exists to catch,
#     arriving through shared sources instead of module sources.
#   * BUNDLED project references. ModuleKit is referenced WITHOUT Private="false", so its DLL is copied into
#     every module folder and ships in every zip; a ModuleKit edit staleness-es all five payloads.
#
# Derived from the csproj rather than hardcoded, so a module that starts linking something new is covered
# without editing this script. ProjectReferences are followed recursively and those marked Private="false"
# are skipped -- that is precisely the "the host owns the single shared copy" marker, so DesktopAICompanion.Contracts
# drops out on its own (a Contracts edit does not change the module payload) and no id needs special-casing.
#
# IMPLICIT MSBUILD INPUTS ARE WATCHED TOO, since 2026-09-30. Directory.Build.props, Directory.Build.targets
# and Directory.Packages.props are imported by every project below them without appearing in any Include,
# and global.json pins the compiler; all four change the compiled bytes of every module they govern
# (modules\Directory.Build.props sets Deterministic, TreatWarningsAsErrors and the Release DebugType;
# src\Directory.Build.props governs the bundled ModuleKit). Until then a commit to any of them left every
# zip reported current while every zip had been built by the old toolchain or settings (F220). The cost is
# accepted deliberately: an SDK bump or a props edit now demands a republish of every module, which is the
# truthful answer, because the payloads really do differ. They are collected by walking UP from each
# project directory to the repository root, so a props file added anywhere on that path is picked up.
#
# DELIBERATELY OUT OF SCOPE: ProductVersion.props. ModuleKit stamps its assembly Version from it, so a host
# version bump does change the bundled DLL's bytes -- but demanding all five modules be republished on every
# release, for a version field and no functional change, would make this gate hostile enough to be routed
# around. Source changes are what this watches. The distinction from the props files above is that a
# version stamp is the only thing ProductVersion.props changes.
function Get-ModuleWatchSet {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ModuleDirectory
    )

    $root = [IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/')
    $moduleFull = [IO.Path]::GetFullPath($ModuleDirectory).TrimEnd('\')
    $external = New-Object 'Collections.Generic.List[string]'
    $degraded = New-Object 'Collections.Generic.List[string]'
    $seenProjects = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $implicitInputNames = @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')

    $queue = New-Object 'Collections.Generic.Queue[string]'
    foreach ($proj in @(Get-ChildItem -LiteralPath $ModuleDirectory -Filter '*.csproj' -File)) {
        $queue.Enqueue($proj.FullName)
    }
    if ($queue.Count -eq 0) { $degraded.Add("no .csproj under $ModuleDirectory") }

    # global.json sits at the root and governs every project, so it is watched whenever anything is.
    $globalJson = Join-Path $root 'global.json'
    if ($queue.Count -gt 0 -and (Test-Path -LiteralPath $globalJson -PathType Leaf)) { $external.Add('global.json') }

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

        # The implicit imports, from the project's own directory up to (and including) the repo root.
        # Anything under the module's own folder is covered by the module-directory watch already; the
        # ones that matter sit above it (modules\Directory.Build.props) or beside a bundled reference
        # (src\Directory.Build.props for ModuleKit).
        $walk = [IO.Path]::GetFullPath($projectDirectory).TrimEnd('\')
        while ($walk -and $walk.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
            foreach ($implicitName in $implicitInputNames) {
                $implicitFull = Join-Path $walk $implicitName
                if (-not (Test-Path -LiteralPath $implicitFull -PathType Leaf)) { continue }
                if ($implicitFull.StartsWith($moduleFull + '\', [StringComparison]::OrdinalIgnoreCase)) { continue }
                $external.Add($implicitFull.Substring($root.Length).TrimStart('\', '/').Replace('\', '/'))
            }
            if ($walk.Equals($root, [StringComparison]::OrdinalIgnoreCase)) { break }
            $walk = Split-Path -Parent $walk
        }

        $nodes = $document.SelectNodes(
            '//*[local-name()="Compile" or local-name()="EmbeddedResource" or local-name()="None" ' +
            'or local-name()="Content" or local-name()="ProjectReference"]')

        foreach ($node in $nodes) {
            $include = [string]$node.GetAttribute('Include')
            if ([string]::IsNullOrWhiteSpace($include)) { continue }
            $isProjectReference = ($node.LocalName -eq 'ProjectReference')

            # Private="false" == the host supplies this assembly, so it is NOT in the payload.
            if ($isProjectReference -and ([string]$node.GetAttribute('Private')) -ieq 'false') { continue }

            # A None item reaches the module folder only through CopyToOutputDirectory (an attribute or a
            # child element; "Never" is the same as absent). Pack="true" alone makes it a NUPKG input:
            # ModuleKit's `<None Include="README.md" Pack="true" />` is packed by dotnet pack and copied
            # nowhere, and no committed zip carries a README entry, so by this file's own rule ("never
            # reaches the assembly") it is not watched. Keyed on the copy setting rather than on Pack, so
            # a file that is both packed and copied stays watched (RA-181).
            if ($node.LocalName -eq 'None') {
                $copyToOutput = [string]$node.GetAttribute('CopyToOutputDirectory')
                if ([string]::IsNullOrWhiteSpace($copyToOutput)) {
                    $copyChild = $node.SelectSingleNode('*[local-name()="CopyToOutputDirectory"]')
                    if ($null -ne $copyChild) { $copyToOutput = [string]$copyChild.InnerText }
                }
                if ([string]::IsNullOrWhiteSpace($copyToOutput) -or $copyToOutput.Trim() -ieq 'Never') { continue }
            }

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
            # WITH the separator, as the implicit-input walk above has it: a bare StartsWith($moduleFull)
            # also matched a SIBLING whose name merely begins with the module's (modules\FortunesExtras
            # beside modules\Fortunes), so a file source-linked from such a sibling was dropped from the
            # External set, and so from both callers' pathspecs, with Degraded empty (RA-182). The
            # equality test keeps the module directory itself out, as before.
            $fullTrimmed = $full.TrimEnd('\')
            if ($fullTrimmed.Equals($moduleFull, [StringComparison]::OrdinalIgnoreCase) -or
                $fullTrimmed.StartsWith($moduleFull + '\', [StringComparison]::OrdinalIgnoreCase)) { continue }

            $relative = $full.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
            if ($relative) { $external.Add($relative) }
        }
    }

    return [pscustomobject]@{
        External = @($external | Sort-Object -Unique)
        Degraded = @($degraded | Sort-Object -Unique)
    }
}

# The pathspecs BOTH callers hand to git for one module: its own directory less its Markdown, plus the
# watch set. Markdown under the module directory is excluded because it never reaches the assembly --
# modules\Fortunes\BACKLOG.md would otherwise demand a 31 MB republish for a note. Everything else stays
# in scope on purpose: images and welcome.json are embedded resources, and probe/self-test code compiles
# into the shipped DLL just like anything else, so it genuinely does make the published payload stale.
# Built here so the guard and the check cannot disagree about which paths count.
#
# The same Markdown rule applies to every watched DIRECTORY outside the module (today the bundled
# ModuleKit's): its README.md is a nupkg input and reaches no zip, yet a README-only ModuleKit commit
# marked all seven zips stale and, once F215 made this set the publish guard, an uncommitted README edit
# refused every publish (RA-181). The exclusion rides in the pathspec GROUP of the directory it belongs
# to, so the freshness check's per-path culprit attribution excludes it too.
function Get-ModuleWatchPathspecs {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][System.IO.DirectoryInfo]$ModuleDirectory
    )

    $sourceRelative = "modules/$($ModuleDirectory.Name)"
    $watch = Get-ModuleWatchSet -RepoRoot $RepoRoot -ModuleDirectory $ModuleDirectory.FullName
    $modulePathspec = @($sourceRelative, ":(exclude)$sourceRelative/**/*.md", ":(exclude)$sourceRelative/*.md")
    $externalGroups = @()
    foreach ($entry in @($watch.External)) {
        $group = @($entry)
        if (Test-Path -LiteralPath (Join-Path $RepoRoot ($entry -replace '/', '\')) -PathType Container) {
            $group += ":(exclude)$entry/**/*.md"
            $group += ":(exclude)$entry/*.md"
        }
        $externalGroups += , $group
    }
    $pathspecs = @($modulePathspec)
    foreach ($group in $externalGroups) { $pathspecs += $group }
    return [pscustomobject]@{
        SourceRelative         = $sourceRelative
        ModulePathspec         = $modulePathspec
        External               = $watch.External
        # One pathspec array per External entry: the entry itself plus, for a directory, its Markdown
        # exclusions. The freshness check attributes staleness per group.
        ExternalPathspecGroups = $externalGroups
        Degraded               = $watch.Degraded
        Pathspecs              = $pathspecs
    }
}
