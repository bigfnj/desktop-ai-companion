#requires -Version 5
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'packaging\NuGetPackagePrivacy.ps1')
$utf8 = New-Object Text.UTF8Encoding($false)
$latin1 = [Text.Encoding]::GetEncoding(28591)
# Reuse only the existing synthetic byte builders, not the freshness test's execution body.
$builderPath = Join-Path $PSScriptRoot 'Test-ModulePublishFreshnessRules.ps1'
$tokens = $null; $parseErrors = $null
$builderAst = [Management.Automation.Language.Parser]::ParseFile($builderPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'NUGET_PRIVACY_RULES_FIXTURE_SOURCE' }
foreach ($name in @('Join-Bytes', 'ConvertTo-CompressedUInt', 'Write-UInt16', 'Write-UInt32', 'Complete-Alignment', 'Add-HeapBlob', 'New-SyntheticPortablePdb', 'New-SyntheticPeImage')) {
    $definitions = @($builderAst.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true))
    if ($definitions.Count -ne 1) { throw 'NUGET_PRIVACY_RULES_FIXTURE_FUNCTION' }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}

# Judge the actual producer's unconditional call sites. This parses source only; it never packs or builds.
function Get-NuGetPackWiringFailures([string]$Source) {
    $tokens = $null; $errors = $null
    $root = [Management.Automation.Language.Parser]::ParseInput($Source, [ref]$tokens, [ref]$errors)
    $failures = New-Object 'Collections.Generic.List[string]'
    if ($errors.Count -ne 0) { $failures.Add('PARSE'); return $failures.ToArray() }
    $banner = @($root.FindAll({ param($n) $n -is [Management.Automation.Language.StringConstantExpressionAst] -and $n.Value.Contains('dotnet nuget push') }, $true))
    if ($banner.Count -ne 1) { $failures.Add('PUSH_BANNER'); return $failures.ToArray() }
    function ArgumentText($Call, [string]$Name) {
        for ($i = 1; $i -lt $Call.CommandElements.Count - 1; $i++) {
            $part = $Call.CommandElements[$i]
            if ($part -is [Management.Automation.Language.CommandParameterAst] -and $part.ParameterName -ieq $Name) {
                return ($Call.CommandElements[$i + 1].Extent.Text -replace '\s', '')
            }
        }
        return ''
    }
    foreach ($entry in @(@('Assert-NuGetExpectedPackages', 'PACKAGE_SET'), @('Assert-NuGetOutputPrivacy', 'OUTPUT_PRIVACY'))) {
        $commandName = $entry[0]
        $expectedElements = if ($entry[1] -ceq 'PACKAGE_SET') { 7 } else { 3 }
        $calls = @($root.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq $commandName }, $true))
        if ($calls.Count -ne 1 -or -not [object]::ReferenceEquals($calls[0].Parent.Parent, $root.EndBlock) -or
            $calls[0].Extent.EndOffset -ge $banner[0].Extent.StartOffset -or
            $calls[0].CommandElements.Count -ne $expectedElements -or $calls[0].Redirections.Count -ne 0 -or
            $calls[0].Parent.PipelineElements.Count -ne 1 -or
            (ArgumentText $calls[0] 'OutputDirectory') -cne '$OutputDirectory' -or
            ($entry[1] -ceq 'PACKAGE_SET' -and ((ArgumentText $calls[0] 'Version') -cne '$version' -or
                (ArgumentText $calls[0] 'PackageIds') -cne '$packageIds'))) { $failures.Add($entry[1]) }
    }
    $reads = @($root.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Get-NuGetPackagePrivacySummary' }, $true))
    $valid = $reads.Count -eq 1
    if ($valid) {
        $read = $reads[0]; $assignment = $read.Parent.Parent; $loop = $assignment.Parent.Parent
        $valid = $assignment -is [Management.Automation.Language.AssignmentStatementAst] -and
            $assignment.Left.Extent.Text -ceq '$privacy' -and
            $loop -is [Management.Automation.Language.ForEachStatementAst] -and
            [object]::ReferenceEquals($loop.Parent, $root.EndBlock) -and
            [object]::ReferenceEquals($assignment.Parent, $loop.Body) -and
            $loop.Variable.VariablePath.UserPath -ceq 'id' -and $loop.Condition.Extent.Text -ceq '$packageIds' -and
            $loop.Extent.EndOffset -lt $banner[0].Extent.StartOffset -and
            (ArgumentText $read 'PackagePath') -ceq '$packagePath' -and
            (ArgumentText $read 'AssemblyName') -ceq "(`$id+'.dll')" -and
            (ArgumentText $read 'ExpectedImageSha256') -ceq '$freshBuilds[$id].ImageSha256' -and
            $read.CommandElements.Count -eq 7 -and $read.Redirections.Count -eq 0 -and
            $read.Parent.PipelineElements.Count -eq 1
    }
    if (-not $valid) { $failures.Add('PACKAGE_PRIVACY_READ') }
    return $failures.ToArray()
}
function Replace-WiringOnce([string]$Source, [string]$Before, [string]$After) {
    if ([regex]::Matches($Source, [regex]::Escape($Before)).Count -ne 1) { throw 'NUGET_PRIVACY_RULES_WIRING_ANCHOR' }
    return $Source.Replace($Before, $After)
}

$checks = 0
function Require([bool]$Condition, [string]$Label) {
    if (-not $Condition) { throw ('NUGET_PRIVACY_RULES_' + $Label) }
    $script:checks++
    Write-Output ('PASS NUGET_PRIVACY_RULES_' + $Label)
}
function Refuses([scriptblock]$Action, [string]$Expected) {
    try { & $Action | Out-Null; return $false }
    catch { return $_.Exception.Message -ceq $Expected }
}
function Read-Package([string]$Path, [string]$AssemblyName = 'DesktopAICompanion.Contracts.dll', [string]$Expected = ('0' * 64)) {
    return Get-NuGetPackagePrivacySummary -PackagePath $Path -AssemblyName $AssemblyName -ExpectedImageSha256 $Expected
}
function Facts([int]$Embedded = 1, [int]$Documents = 1, [int]$Absolute = 0, [int]$SourceLink = 0, [int]$EmbeddedSource = 0) {
    return [pscustomobject]@{ EmbeddedPdbCount = $Embedded; DocumentCount = $Documents; AbsoluteDocumentCount = $Absolute; SourceLinkPdbCount = $SourceLink; EmbeddedSourcePdbCount = $EmbeddedSource }
}
Assert-NuGetLibraryPrivacy (Facts)
Require $true 'HEALTHY'
Require (Refuses { Assert-NuGetLibraryPrivacy (Facts -Embedded 0) } 'NUGET_PRIVACY_EMBEDDED_PDB_REQUIRED') 'EMBEDDED_PDB'
Require (Refuses { Assert-NuGetLibraryPrivacy (Facts -Documents 0) } 'NUGET_PRIVACY_DOCUMENT_WITNESS_REQUIRED') 'DOCUMENT_WITNESS'
Require (Refuses { Assert-NuGetLibraryPrivacy (Facts -Absolute 1) } 'NUGET_PRIVACY_ABSOLUTE_DOCUMENTS') 'ABSOLUTE_DOCUMENTS'
Require (Refuses { Assert-NuGetLibraryPrivacy (Facts -SourceLink 1) } 'NUGET_PRIVACY_SOURCELINK') 'SOURCELINK'
Require (Refuses { Assert-NuGetLibraryPrivacy (Facts -EmbeddedSource 1) } 'NUGET_PRIVACY_EMBEDDED_SOURCE') 'EMBEDDED_SOURCE'

# Entry selection is exercised through actual small ZIPs. PE reading must never preempt these refusals.
$owned = Join-Path $PSScriptRoot ('nuget-privacy-rules-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($owned)
function Write-FixtureZip([string]$Path, [string[]]$Entries, [byte[]]$Image = $null) {
    $file = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $zip = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($name in $Entries) {
            $entry = $zip.CreateEntry($name)
            $entryStream = $entry.Open()
            try { if ($null -eq $Image) { $entryStream.WriteByte(0) } else { $entryStream.Write($Image, 0, $Image.Length) } } finally { $entryStream.Dispose() }
        }
    }
    finally { $zip.Dispose(); $file.Dispose() }
}
try {
    Add-Type -AssemblyName System.IO.Compression
    $missing = Join-Path $owned 'missing.nupkg'
    Write-FixtureZip $missing @('lib/net10.0-windows7.0/SomeOther.dll')
    Require (Refuses { Read-Package $missing } 'NUGET_PRIVACY_ENTRY') 'ENTRY_MISSING'
    $duplicate = Join-Path $owned 'duplicate.nupkg'
    Write-FixtureZip $duplicate @('lib/net10.0-windows/DesktopAICompanion.Contracts.dll', 'lib/net10.0-windows7.0/DesktopAICompanion.Contracts.dll')
    Require (Refuses { Read-Package $duplicate } 'NUGET_PRIVACY_ENTRY') 'ENTRY_DUPLICATE'
    $pdbSidecar = Join-Path $owned 'pdb-sidecar.nupkg'
    Write-FixtureZip $pdbSidecar @('lib/net10.0-windows/DesktopAICompanion.Contracts.dll', 'lib/net10.0-windows/DesktopAICompanion.Contracts.pdb')
    Require (Refuses { Read-Package $pdbSidecar } 'NUGET_PRIVACY_PACKAGE_PDB') 'PACKAGE_PDB'
    $sourceSidecar = Join-Path $owned 'source-sidecar.nupkg'
    Write-FixtureZip $sourceSidecar @('lib/net10.0-windows/DesktopAICompanion.Contracts.dll', 'src/Tiny.cs')
    Require (Refuses { Read-Package $sourceSidecar } 'NUGET_PRIVACY_PACKAGE_SOURCE') 'PACKAGE_SOURCE'
    # Every assembly other than the one owned library is refused, before any PE read.
    $ownedEntryName = 'lib/net10.0-windows/DesktopAICompanion.Contracts.dll'
    $extraBinaryCases = [ordered]@{
        'EXTRA_BINARY_OTHER_TFM'  = 'lib/net9.0/DesktopAICompanion.Contracts.dll'
        'EXTRA_BINARY_REF'        = 'ref/net10.0-windows/DesktopAICompanion.Contracts.dll'
        'EXTRA_BINARY_DEPENDENCY' = 'lib/net10.0-windows/Bundled.Dependency.dll'
        'EXTRA_BINARY_EXE'        = 'tools/net10.0/any/helper.exe'
        'EXTRA_BINARY_UPPERCASE'  = 'runtimes/win-x64/native/NATIVE.DLL'
    }
    foreach ($label in $extraBinaryCases.Keys) {
        $extraPath = Join-Path $owned ($label.ToLowerInvariant() + '.nupkg')
        Write-FixtureZip $extraPath @($ownedEntryName, $extraBinaryCases[$label])
        Require (Refuses { Read-Package $extraPath } 'NUGET_PRIVACY_UNEXPECTED_BINARY:DesktopAICompanion.Contracts.dll') $label
    }
    $symbolDirectory = Join-Path $owned 'symbols'; [void][IO.Directory]::CreateDirectory($symbolDirectory)
    foreach ($name in @('fixture.snupkg', 'fixture.symbols.nupkg')) {
        $path = Join-Path $symbolDirectory $name; [IO.File]::WriteAllText($path, 'synthetic')
        Require (Refuses { Assert-NuGetOutputPrivacy $symbolDirectory } 'NUGET_PRIVACY_SYMBOL_ARCHIVE') ('SYMBOL_ARCHIVE_' + $name)
        [IO.File]::Delete($path)
    }
    $path = Join-Path $symbolDirectory 'fixture.pdb'; [IO.File]::WriteAllText($path, 'synthetic')
    Require (Refuses { Assert-NuGetOutputPrivacy $symbolDirectory } 'NUGET_PRIVACY_PDB_SIDECAR') 'PDB_SIDECAR'
    [IO.File]::Delete($path)

    $entry = @('lib/net10.0-windows/DesktopAICompanion.Contracts.dll')
    $cleanImage = New-SyntheticPeImage -EmbeddedPdb (New-SyntheticPortablePdb -DocumentNames @('/_/fixture/Tiny.cs'))
    $clean = Join-Path $owned 'clean.nupkg'; Write-FixtureZip $clean $entry $cleanImage
    $cleanHash = Get-NuGetImageSha256 $cleanImage
    $summary = Read-Package $clean 'DesktopAICompanion.Contracts.dll' $cleanHash
    Require ($summary.EmbeddedPdbCount -eq 1 -and $summary.DocumentCount -eq 1 -and $summary.AbsoluteDocumentCount -eq 0 -and $summary.SourceLinkPdbCount -eq 0 -and $summary.EmbeddedSourcePdbCount -eq 0) 'SYNTHETIC_CLEAN'
    Require (Refuses { Read-Package $clean } 'NUGET_PRIVACY_BUILD_OUTPUT_MISMATCH:DesktopAICompanion.Contracts.dll') 'IMAGE_BINDING_MISMATCH'
    $withMetadata = Join-Path $owned 'clean-with-metadata.nupkg'
    Write-FixtureZip $withMetadata @($entry[0], 'DesktopAICompanion.Contracts.nuspec', 'README.md', '[Content_Types].xml', '_rels/.rels', 'package/services/metadata/core-properties/fixture.psmdcp') $cleanImage
    $metadataFailure = 'none'
    try { $metadataSummary = Read-Package $withMetadata 'DesktopAICompanion.Contracts.dll' $cleanHash } catch { $metadataFailure = $_.Exception.Message }
    Require ($metadataFailure -ceq 'none' -and $metadataSummary.EmbeddedPdbCount -eq 1) 'CLEAN_WITH_PACKAGE_METADATA'
    $builtImage = Join-Path $owned 'fresh.dll'; $from = [DateTime]::UtcNow
    [IO.File]::WriteAllBytes($builtImage, $cleanImage); $to = [DateTime]::UtcNow
    $identity = Get-NuGetBuildIdentity $builtImage $from $to
    Require ($identity.ImageSha256 -ceq $cleanHash -and @($identity.EmbeddedPdbSha256).Count -eq 1) 'BUILD_IDENTITY'
    [IO.File]::SetLastWriteTimeUtc($builtImage, $from.AddMinutes(-10))
    Require (Refuses { Get-NuGetBuildIdentity $builtImage $from $to } 'NUGET_PRIVACY_BUILD_OUTPUT_NOT_FRESH') 'BUILD_OUTPUT_FRESHNESS'
    [IO.File]::SetLastWriteTimeUtc($builtImage, $to.AddMinutes(10))
    Require (Refuses { Get-NuGetBuildIdentity $builtImage $from $to } 'NUGET_PRIVACY_BUILD_OUTPUT_NOT_FRESH') 'BUILD_OUTPUT_FUTURE'

    $sourceLinkPdb = New-SyntheticPortablePdb -DocumentNames @('/_/fixture/Tiny.cs') -SourceLink
    $sourceLinkImage = New-SyntheticPeImage -EmbeddedPdb $sourceLinkPdb
    $facts = Get-EmbeddedPortablePdbPrivacySummary $sourceLinkImage
    Require ($facts.SourceLinkPdbCount -eq 1 -and $facts.EmbeddedSourcePdbCount -eq 0) 'SYNTHETIC_SOURCELINK_KIND'
    $sourceLinkZip = Join-Path $owned 'source-link.nupkg'; Write-FixtureZip $sourceLinkZip $entry $sourceLinkImage
    Require (Refuses { Read-Package $sourceLinkZip 'DesktopAICompanion.Contracts.dll' (Get-NuGetImageSha256 $sourceLinkImage) } 'NUGET_PRIVACY_SOURCELINK:DesktopAICompanion.Contracts.dll') 'SYNTHETIC_SOURCELINK_REFUSED'

    # The shared builder's CDI row is kept; replace its one kind slot without decoding any value blob.
    [byte[]]$embeddedPdb = $sourceLinkPdb.Clone()
    $sourceKind = ([Guid]'CC110556-A091-4D38-9FEC-25AB9A351A6A').ToByteArray()
    $embeddedKind = ([Guid]'0E8A571B-6926-466E-B4AD-8AB04611F5FE').ToByteArray()
    $positions = @()
    for ($at = 0; $at -le $embeddedPdb.Length - 16; $at++) { if (Test-PortablePdbGuidAt $embeddedPdb $at $sourceKind) { $positions += $at } }
    if ($positions.Count -ne 1) { throw 'NUGET_PRIVACY_RULES_FIXTURE_KIND_ONCE' }
    [Array]::Copy($embeddedKind, 0, $embeddedPdb, $positions[0], 16)
    $embeddedImage = New-SyntheticPeImage -EmbeddedPdb $embeddedPdb
    $facts = Get-EmbeddedPortablePdbPrivacySummary $embeddedImage
    Require ($facts.SourceLinkPdbCount -eq 0 -and $facts.EmbeddedSourcePdbCount -eq 1) 'SYNTHETIC_EMBEDDED_SOURCE_KIND'
    $embeddedZip = Join-Path $owned 'embedded-source.nupkg'; Write-FixtureZip $embeddedZip $entry $embeddedImage
    Require (Refuses { Read-Package $embeddedZip 'DesktopAICompanion.Contracts.dll' (Get-NuGetImageSha256 $embeddedImage) } 'NUGET_PRIVACY_EMBEDDED_SOURCE:DesktopAICompanion.Contracts.dll') 'SYNTHETIC_EMBEDDED_SOURCE_REFUSED'

    # The banner advertises a wildcard push of the output directory, so it must hold exactly the packages checked.
    $setDirectory = Join-Path $owned 'package-set'; [void][IO.Directory]::CreateDirectory($setDirectory)
    $setIds = @('DesktopAICompanion.Contracts', 'DesktopAICompanion.ModuleKit')
    function Reset-PackageSet([string[]]$Names) {
        foreach ($file in [IO.Directory]::GetFiles($setDirectory)) { [IO.File]::Delete($file) }
        foreach ($name in $Names) { [IO.File]::WriteAllText((Join-Path $setDirectory $name), 'synthetic') }
    }
    Reset-PackageSet @('DesktopAICompanion.Contracts.1.2.3.nupkg', 'DesktopAICompanion.ModuleKit.1.2.3.nupkg', 'notes.txt')
    $setFailure = 'none'
    try { Assert-NuGetExpectedPackages -OutputDirectory $setDirectory -Version '1.2.3' -PackageIds $setIds } catch { $setFailure = $_.Exception.Message }
    Require ($setFailure -ceq 'none') 'PACKAGE_SET_EXACT'
    $packageSetCases = [ordered]@{
        'PACKAGE_SET_EXTRA'               = @('DesktopAICompanion.Contracts.1.2.3.nupkg', 'DesktopAICompanion.ModuleKit.1.2.3.nupkg', 'Stale.Package.1.0.0.nupkg')
        'PACKAGE_SET_MISSING'             = @('DesktopAICompanion.Contracts.1.2.3.nupkg')
        'PACKAGE_SET_WRONG_VERSION'       = @('DesktopAICompanion.Contracts.1.2.3.nupkg', 'DesktopAICompanion.ModuleKit.1.2.2.nupkg')
        'PACKAGE_SET_CASE'                = @('DesktopAICompanion.Contracts.1.2.3.nupkg', 'desktopaicompanion.modulekit.1.2.3.nupkg')
        'PACKAGE_SET_UPPERCASE_EXTENSION' = @('DesktopAICompanion.Contracts.1.2.3.nupkg', 'DesktopAICompanion.ModuleKit.1.2.3.nupkg', 'Stale.Package.1.0.0.NUPKG')
    }
    foreach ($label in $packageSetCases.Keys) {
        Reset-PackageSet $packageSetCases[$label]
        Require (Refuses { Assert-NuGetExpectedPackages -OutputDirectory $setDirectory -Version '1.2.3' -PackageIds $setIds } 'NUGET_PRIVACY_PACKAGE_SET') $label
    }

    # Wiring checks use the actual producer's AST, then source-only mutations prove each decision can fail.
    $packScript = Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'packaging') 'New-NuGetPackages.ps1'
    $packSource = [IO.File]::ReadAllText($packScript)
    Write-Output ('LOADED packaging/New-NuGetPackages.ps1 sha256 ' + (Get-NuGetImageSha256 ([IO.File]::ReadAllBytes($packScript))))
    $wiring = @(Get-NuGetPackWiringFailures $packSource)
    Require ($wiring -cnotcontains 'PACKAGE_SET' -and $wiring -cnotcontains 'PARSE' -and $wiring -cnotcontains 'PUSH_BANNER') 'PACKAGE_SET_WIRED_BEFORE_PUSH_BANNER'
    Require ($wiring -cnotcontains 'OUTPUT_PRIVACY' -and $wiring -cnotcontains 'PARSE' -and $wiring -cnotcontains 'PUSH_BANNER') 'OUTPUT_PRIVACY_WIRED_BEFORE_PUSH_BANNER'
    Require ($wiring -cnotcontains 'PACKAGE_PRIVACY_READ' -and $wiring -cnotcontains 'PARSE' -and $wiring -cnotcontains 'PUSH_BANNER') 'PACKAGE_PRIVACY_READ_WIRED_BEFORE_PUSH_BANNER'
    $outputCall = 'Assert-NuGetOutputPrivacy -OutputDirectory $OutputDirectory'
    $packageSetCall = 'Assert-NuGetExpectedPackages -OutputDirectory $OutputDirectory -Version $version -PackageIds $packageIds'
    foreach ($pin in @(@($outputCall, 'OUTPUT_PRIVACY'), @($packageSetCall, 'PACKAGE_SET'))) {
        foreach ($extra in @(@(' -ErrorAction SilentlyContinue', 'SUPPRESSED'), @(' 2>$null', 'REDIRECTED'))) {
            $variant = Replace-WiringOnce $packSource $pin[0] ($pin[0] + $extra[0])
            Require ((@(Get-NuGetPackWiringFailures $variant) -join ',') -ceq $pin[1]) ('WIRING_' + $pin[1] + '_' + $extra[1])
        }
    }
    foreach ($binding in @(@('-Version $version', '-Version $wrongVersion', 'VERSION'), @('-PackageIds $packageIds', '-PackageIds $wrongIds', 'PACKAGE_IDS'))) {
        $changedCall = Replace-WiringOnce $packageSetCall $binding[0] $binding[1]
        $variant = Replace-WiringOnce $packSource $packageSetCall $changedCall
        Require ((@(Get-NuGetPackWiringFailures $variant) -join ',') -ceq 'PACKAGE_SET') ('WIRING_PACKAGE_SET_' + $binding[2] + '_BINDING')
    }
    $readCall = '$privacy = Get-NuGetPackagePrivacySummary -PackagePath $packagePath -AssemblyName ($id + ''.dll'') -ExpectedImageSha256 $freshBuilds[$id].ImageSha256'
    $bannerAt = $packSource.IndexOf("Write-Host '  Not pushed.", [StringComparison]::Ordinal)
    if ($bannerAt -lt 0) { throw 'NUGET_PRIVACY_RULES_WIRING_BANNER_ANCHOR' }
    foreach ($kind in @('OMITTED', 'CONDITIONAL', 'LATE')) {
        $replacement = if ($kind -eq 'CONDITIONAL') { 'if ($false) { ' + $outputCall + ' }' } else { '' }
        $variant = Replace-WiringOnce $packSource $outputCall $replacement
        if ($kind -eq 'LATE') { $variant += "`n" + $outputCall }
        Require ((@(Get-NuGetPackWiringFailures $variant) -join ',') -ceq 'OUTPUT_PRIVACY') ('WIRING_OUTPUT_' + $kind)
        $replacement = if ($kind -eq 'CONDITIONAL') { 'if ($false) { ' + $readCall + ' }' } else { '' }
        $variant = Replace-WiringOnce $packSource $readCall $replacement
        if ($kind -eq 'LATE') { $variant += "`nforeach (`$id in `$packageIds) { " + $readCall + ' }' }
        Require ((@(Get-NuGetPackWiringFailures $variant) -join ',') -ceq 'PACKAGE_PRIVACY_READ') ('WIRING_READ_' + $kind)
    }
    $variant = Replace-WiringOnce $packSource '$freshBuilds[$id].ImageSha256' "('0' * 64)"
    Require ((@(Get-NuGetPackWiringFailures $variant) -join ',') -ceq 'PACKAGE_PRIVACY_READ') 'WIRING_READ_FRESH_IMAGE_BINDING'
}
finally {
    $full = [IO.Path]::GetFullPath($owned)
    $prefix = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\nuget-privacy-rules-'
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'NUGET_PRIVACY_RULES_CLEANUP_SCOPE' }
    [IO.Directory]::Delete($full, $true)
    if ([IO.Directory]::Exists($full)) { throw 'NUGET_PRIVACY_RULES_FIXTURE_REMAINS' }
}
Write-Output ('NUGET_PRIVACY_RULES=' + $checks + ' checks passed')
