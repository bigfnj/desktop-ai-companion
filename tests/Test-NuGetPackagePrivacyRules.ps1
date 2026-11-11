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

    # Wiring: the pack script calls the package-set check unconditionally, at top level, before it prints the push command.
    $packScript = Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'packaging') 'New-NuGetPackages.ps1'
    $packTokens = $null; $packErrors = $null
    $packAst = [Management.Automation.Language.Parser]::ParseFile($packScript, [ref]$packTokens, [ref]$packErrors)
    $setCalls = @($packAst.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Assert-NuGetExpectedPackages' }, $true))
    $pushBanner = @($packAst.FindAll({ param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst] -and $node.Value.Contains('dotnet nuget push') }, $true))
    Require ($packErrors.Count -eq 0 -and $setCalls.Count -eq 1 -and $pushBanner.Count -eq 1 -and
        [object]::ReferenceEquals($setCalls[0].Parent.Parent, $packAst.EndBlock) -and
        $setCalls[0].Extent.StartOffset -lt $pushBanner[0].Extent.StartOffset) 'PACKAGE_SET_WIRED_BEFORE_PUSH_BANNER'
}
finally {
    $full = [IO.Path]::GetFullPath($owned)
    $prefix = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\nuget-privacy-rules-'
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'NUGET_PRIVACY_RULES_CLEANUP_SCOPE' }
    [IO.Directory]::Delete($full, $true)
    if ([IO.Directory]::Exists($full)) { throw 'NUGET_PRIVACY_RULES_FIXTURE_REMAINS' }
}
Write-Output ('NUGET_PRIVACY_RULES=' + $checks + ' checks passed')
