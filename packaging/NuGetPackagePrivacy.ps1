#requires -Version 5

# Uses the shared counts-only reader. SourceLink JSON and document names never leave that reader.
. (Join-Path $PSScriptRoot 'EmbeddedPortablePdb.ps1')

function Get-NuGetImageSha256([byte[]]$Image) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Image)).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Get-NuGetEmbeddedPdbHashes([byte[]]$Image) {
    return @(@(Read-PeDebugDirectoryEntries -Image $Image) | Where-Object { $_.Type -eq 17 } |
        ForEach-Object { Get-NuGetImageSha256 (Expand-EmbeddedPortablePdb -Image $Image -Entry $_) })
}

function Get-NuGetBuildIdentity {
    param([Parameter(Mandatory = $true)][string]$AssemblyPath,
        [Parameter(Mandatory = $true)][DateTime]$BuildStartedUtc,
        [Parameter(Mandatory = $true)][DateTime]$BuildCompletedUtc)
    try { $image = [IO.File]::ReadAllBytes($AssemblyPath); $writtenUtc = [IO.File]::GetLastWriteTimeUtc($AssemblyPath) }
    catch { throw 'NUGET_PRIVACY_BUILD_OUTPUT_READ_REFUSED' }
    if ($writtenUtc -lt $BuildStartedUtc -or $writtenUtc -gt $BuildCompletedUtc) { throw 'NUGET_PRIVACY_BUILD_OUTPUT_NOT_FRESH' }
    try { $pdbHashes = @(Get-NuGetEmbeddedPdbHashes $image) }
    catch { throw 'NUGET_PRIVACY_BUILD_PDB_REFUSED' }
    return [pscustomobject]@{ ImageSha256 = (Get-NuGetImageSha256 $image); EmbeddedPdbSha256 = $pdbHashes; WrittenUtc = $writtenUtc }
}

function Assert-NuGetLibraryPrivacy {
    param([Parameter(Mandatory = $true)]$Summary, [string]$AssemblyName = '')

    $suffix = if ($AssemblyName) { ':' + $AssemblyName } else { '' }
    if ($Summary.EmbeddedPdbCount -lt 1) { throw ('NUGET_PRIVACY_EMBEDDED_PDB_REQUIRED' + $suffix) }
    if ($Summary.DocumentCount -lt 1) { throw ('NUGET_PRIVACY_DOCUMENT_WITNESS_REQUIRED' + $suffix) }
    if ($Summary.AbsoluteDocumentCount -ne 0) { throw ('NUGET_PRIVACY_ABSOLUTE_DOCUMENTS' + $suffix) }
    if ($Summary.SourceLinkPdbCount -ne 0) { throw ('NUGET_PRIVACY_SOURCELINK' + $suffix) }
    if ($Summary.EmbeddedSourcePdbCount -ne 0) { throw ('NUGET_PRIVACY_EMBEDDED_SOURCE' + $suffix) }
}

function Assert-NuGetOutputPrivacy {
    param([Parameter(Mandatory = $true)][string]$OutputDirectory)
    try { $files = [IO.Directory]::GetFiles($OutputDirectory) }
    catch { throw 'NUGET_PRIVACY_OUTPUT_READ_REFUSED' }
    $symbolArchives = @($files | Where-Object { $_.EndsWith('.snupkg', [StringComparison]::OrdinalIgnoreCase) -or $_.EndsWith('.symbols.nupkg', [StringComparison]::OrdinalIgnoreCase) })
    if ($symbolArchives.Count -ne 0) { throw 'NUGET_PRIVACY_SYMBOL_ARCHIVE' }
    $pdbSidecars = @($files | Where-Object { $_.EndsWith('.pdb', [StringComparison]::OrdinalIgnoreCase) })
    if ($pdbSidecars.Count -ne 0) { throw 'NUGET_PRIVACY_PDB_SIDECAR' }
}

function Assert-NuGetExpectedPackages {
    param([Parameter(Mandatory = $true)][string]$OutputDirectory,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string[]]$PackageIds)
    # The success banner advertises a wildcard push of this directory, so every .nupkg in it must be one this run
    # produced and checked: exactly one per expected id at this version, and nothing else.
    try { $files = [IO.Directory]::GetFiles($OutputDirectory) }
    catch { throw 'NUGET_PRIVACY_OUTPUT_READ_REFUSED' }
    $names = @($files | ForEach-Object { [IO.Path]::GetFileName($_) } |
        Where-Object { $_.EndsWith('.nupkg', [StringComparison]::OrdinalIgnoreCase) })
    $expected = @($PackageIds | ForEach-Object { $_ + '.' + $Version + '.nupkg' })
    if ($names.Count -ne $expected.Count) { throw 'NUGET_PRIVACY_PACKAGE_SET' }
    foreach ($name in $expected) {
        $matching = @($names | Where-Object { [string]::Equals($_, $name, [StringComparison]::Ordinal) })
        if ($matching.Count -ne 1) { throw 'NUGET_PRIVACY_PACKAGE_SET' }
    }
}

function Get-NuGetPackagePrivacySummary {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)]
        [ValidateSet('DesktopAICompanion.Contracts.dll', 'DesktopAICompanion.ModuleKit.dll')]
        [string]$AssemblyName,
        [Parameter(Mandatory = $true)][ValidatePattern('^[A-F0-9]{64}\z')][string]$ExpectedImageSha256
    )

    Add-Type -AssemblyName System.IO.Compression
    # Hash and inspect one immutable in-memory copy of the newly created package.
    try { $packageBytes = [IO.File]::ReadAllBytes($PackagePath) }
    catch { throw 'NUGET_PRIVACY_PACKAGE_READ_REFUSED' }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $packageSha256 = [BitConverter]::ToString($sha.ComputeHash($packageBytes)).Replace('-', '') }
    finally { $sha.Dispose() }
    $stream = [IO.MemoryStream]::new($packageBytes, $false)
    $zip = $null
    try {
        try { $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $false) }
        catch { throw 'NUGET_PRIVACY_PACKAGE_FORMAT_REFUSED' }
        # SDK NuGet may normalize this TFM to net10.0-windows7.0. Exactly one owned library is required.
        $entryPattern = '^lib/net10[.]0-windows[^/]*/' + [regex]::Escape($AssemblyName) + '\z'
        $ownedEntries = @($zip.Entries | Where-Object { [regex]::IsMatch($_.FullName, $entryPattern) })
        if ($ownedEntries.Count -ne 1) { throw 'NUGET_PRIVACY_ENTRY' }
        # Only the owned library gets the privacy read below, so every other assembly is refused before any PE read:
        # another TFM folder, ref/, runtimes/, tools/, analyzers/ or a bundled dependency would otherwise ship unread.
        $ownedName = $ownedEntries[0].FullName
        $otherBinaries = @($zip.Entries | Where-Object {
            $isBinary = $_.FullName.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase) -or $_.FullName.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)
            $isBinary -and -not [string]::Equals($_.FullName, $ownedName, [StringComparison]::Ordinal) })
        if ($otherBinaries.Count -ne 0) { throw ('NUGET_PRIVACY_UNEXPECTED_BINARY:' + $AssemblyName) }
        $sidecars = @($zip.Entries | Where-Object { $_.FullName.EndsWith('.pdb', [StringComparison]::OrdinalIgnoreCase) })
        if ($sidecars.Count -ne 0) { throw 'NUGET_PRIVACY_PACKAGE_PDB' }
        $sources = @($zip.Entries | Where-Object { $_.FullName.StartsWith('src/', [StringComparison]::OrdinalIgnoreCase) })
        if ($sources.Count -ne 0) { throw 'NUGET_PRIVACY_PACKAGE_SOURCE' }
        try {
            $imageStream = $ownedEntries[0].Open()
            $imageCopy = [IO.MemoryStream]::new()
            try { $imageStream.CopyTo($imageCopy); $image = $imageCopy.ToArray() }
            finally { $imageCopy.Dispose(); $imageStream.Dispose() }
            $summary = Get-EmbeddedPortablePdbPrivacySummary -Image $image
        }
        catch { throw 'NUGET_PRIVACY_READER_REFUSED' }
        $imageSha256 = Get-NuGetImageSha256 $image
        if ($imageSha256 -cne $ExpectedImageSha256) { throw ('NUGET_PRIVACY_BUILD_OUTPUT_MISMATCH:' + $AssemblyName) }
        Assert-NuGetLibraryPrivacy -Summary $summary -AssemblyName $AssemblyName
        try { $pdbHashes = @(Get-NuGetEmbeddedPdbHashes $image) }
        catch { throw 'NUGET_PRIVACY_PDB_HASH_REFUSED' }
        return [pscustomobject]@{
            AssemblyName = $AssemblyName
            PackageSha256 = $packageSha256
            PackageBytes = $packageBytes.LongLength
            ImageSha256 = $imageSha256
            EmbeddedPdbSha256 = $pdbHashes
            EmbeddedPdbCount = $summary.EmbeddedPdbCount
            DocumentCount = $summary.DocumentCount
            AbsoluteDocumentCount = $summary.AbsoluteDocumentCount
            SourceLinkPdbCount = $summary.SourceLinkPdbCount
            EmbeddedSourcePdbCount = $summary.EmbeddedSourcePdbCount
        }
    }
    finally { if ($null -ne $zip) { $zip.Dispose() }; $stream.Dispose() }
}
