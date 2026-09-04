#requires -Version 5
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RuntimeRoot,
    [Parameter(Mandatory = $true)][string]$DestinationPath,
    [string]$ManifestPath,
    [string]$MarkerPath,
    # Optional read-only content subtrees bundled beside the runtime payload
    # (portable zip only). Each entry is a hashtable @{ Prefix = 'pets'; Source =
    # '<dir>' }; every file under Source is added deterministically as
    # '<Prefix>/<relative/path>'.
    [hashtable[]]$ContentDirectories = @(),
    # Optional caller policy runs against the completed private archive before
    # publication.
    [scriptblock]$AdditionalStagedArchiveValidation
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDirectory = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    $PSScriptRoot
}
else {
    Split-Path -Parent $MyInvocation.MyCommand.Path
}
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $scriptDirectory 'runtime-files.txt'
}
if ([string]::IsNullOrWhiteSpace($MarkerPath)) {
    $MarkerPath = Join-Path $scriptDirectory 'DesktopAICompanion.portable'
}
$pathSafety = Join-Path $scriptDirectory 'StagingPathSafety.ps1'
if (-not (Test-Path -LiteralPath $pathSafety -PathType Leaf)) {
    throw "Packaging path-safety policy is missing: $pathSafety"
}
. $pathSafety
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

foreach ($path in @($RuntimeRoot, $ManifestPath, $MarkerPath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Portable-package input not found: $path"
    }
}
if (-not (Test-Path -LiteralPath $RuntimeRoot -PathType Container)) {
    throw "Runtime root is not a directory: $RuntimeRoot"
}

$runtimeRootFull = [IO.Path]::GetFullPath($RuntimeRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$destinationFull = [IO.Path]::GetFullPath($DestinationPath)
$manifestFull = [IO.Path]::GetFullPath($ManifestPath)
$markerFull = [IO.Path]::GetFullPath($MarkerPath)

$validatedInputs = New-Object 'Collections.Generic.List[IDisposable]'
$maximumManifestBytes = 1MB
try {
$manifestInput = Open-DesktopAICompanionValidatedInputFile `
    -Path $manifestFull `
    -Root (Split-Path -Parent $manifestFull)
$validatedInputs.Add($manifestInput)
$markerInput = Open-DesktopAICompanionValidatedInputFile `
    -Path $markerFull `
    -Root (Split-Path -Parent $markerFull)
$validatedInputs.Add($markerInput)

$runtimeFiles = @(
    $manifestInput.ReadAllTextUtf8($maximumManifestBytes) -split '\r?\n' |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') }
)
if ($runtimeFiles.Count -eq 0) {
    throw 'Runtime payload manifest is empty.'
}
if (@($runtimeFiles | Group-Object | Where-Object Count -gt 1).Count -gt 0) {
    throw 'Runtime payload manifest contains duplicate entries.'
}

$entrySources = @{}
foreach ($name in $runtimeFiles) {
    if (-not (Test-DesktopAICompanionWindowsLeafName -Name $name) -or
        [string]::Equals(
            $name,
            'DesktopAICompanion.portable',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Runtime payload entry is unsafe or reserved: '$name'"
    }
    $source = Join-Path $runtimeRootFull $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Runtime payload file not found: $source"
    }
    $sourceInput = Open-DesktopAICompanionValidatedInputFile `
        -Path $source `
        -Root $runtimeRootFull
    $validatedInputs.Add($sourceInput)
    $entrySources.Add($name, $sourceInput)
}
$entrySources.Add('DesktopAICompanion.portable', $markerInput)

# Optional bundled content subtrees (portable zip only). Every file below each
# declared Source is added as '<Prefix>/<relative/path>' with forward slashes,
# each path segment revalidated as a safe Windows leaf name. These join the same
# sorted entry set, so deterministic creation covers them exactly like the flat
# runtime payload.
foreach ($contentDirectory in $ContentDirectories) {
    if ($null -eq $contentDirectory) {
        throw 'A content directory specification is null.'
    }
    $prefix = [string]$contentDirectory['Prefix']
    $contentSource = [string]$contentDirectory['Source']
    if (-not (Test-DesktopAICompanionWindowsLeafName -Name $prefix)) {
        throw "Content directory prefix is unsafe: '$prefix'"
    }
    if ([string]::IsNullOrWhiteSpace($contentSource) -or
        -not (Test-Path -LiteralPath $contentSource -PathType Container)) {
        throw "Content directory source is not a directory: '$contentSource'"
    }
    $contentSourceFull = [IO.Path]::GetFullPath($contentSource).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $contentFiles = @(
        Get-ChildItem -LiteralPath $contentSourceFull -File -Recurse |
            Sort-Object FullName
    )
    if ($contentFiles.Count -eq 0) {
        throw "Content directory contributes no files: '$contentSourceFull'"
    }
    foreach ($contentFile in $contentFiles) {
        $relative = $contentFile.FullName.Substring(
            $contentSourceFull.Length).TrimStart(
                [IO.Path]::DirectorySeparatorChar,
                [IO.Path]::AltDirectorySeparatorChar)
        $segments = @($relative -split '[\\/]')
        foreach ($segment in $segments) {
            if (-not (Test-DesktopAICompanionWindowsLeafName -Name $segment)) {
                throw (
                    "Content payload has an unsafe path segment '$segment' " +
                    "in '$relative'.")
            }
        }
        $entryName = $prefix + '/' + ($segments -join '/')
        if ($entrySources.ContainsKey($entryName)) {
            throw "Content payload entry is a duplicate: '$entryName'"
        }
        $contentInput = Open-DesktopAICompanionValidatedInputFile `
            -Path $contentFile.FullName `
            -Root $contentSourceFull
        $validatedInputs.Add($contentInput)
        $entrySources.Add($entryName, $contentInput)
    }
}

# Ordinal-sorted entry order keeps the archive byte-reproducible.
$entryNames = [string[]]$entrySources.Keys
[Array]::Sort($entryNames, [StringComparer]::Ordinal)

$destinationParent = Split-Path -Parent $destinationFull
if ([string]::IsNullOrWhiteSpace($destinationParent) -or
    -not (Test-Path -LiteralPath $destinationParent -PathType Container)) {
    throw "Destination parent must already exist: $destinationParent"
}
$destinationFull = Assert-DesktopAICompanionOutputFileSafe `
    -Path $destinationFull `
    -TrustedRoot $destinationParent `
    -ProtectedPaths @($manifestFull, $markerFull) `
    -ProtectedDirectories @($runtimeRootFull)
if ((Test-Path -LiteralPath $destinationFull) -and
    -not (Test-Path -LiteralPath $destinationFull -PathType Leaf)) {
    throw "Portable ZIP destination is not a regular file: $destinationFull"
}

$temporaryDirectory = Join-Path $destinationParent (
    '.DesktopAICompanion-zip-' + [Guid]::NewGuid().ToString('N'))
$temporaryDirectoryLease = $null
$zipPrimaryError = $null
try {
    $temporaryDirectoryLease = Open-DesktopAICompanionNewScratchDirectory `
        -Path $temporaryDirectory `
        -AllowedRoot $destinationParent `
        -TrustedRoot $destinationParent `
        -ProtectedPaths @($manifestFull, $markerFull, $destinationFull) `
        -ProtectedDirectories @($runtimeRootFull)
    $temporaryPath = Join-Path $temporaryDirectory (
        [IO.Path]::GetFileName($destinationFull) + '.tmp')
    $temporaryPath = Assert-DesktopAICompanionOutputFileSafe `
        -Path $temporaryPath `
        -TrustedRoot $temporaryDirectory `
        -ProtectedPaths @($manifestFull, $markerFull, $destinationFull) `
        -ProtectedDirectories @($runtimeRootFull)
    # Fixed 1980-01-01 entry timestamps keep the archive byte-reproducible.
    $normalizedTimestamp =
        New-Object DateTimeOffset 1980, 1, 1, 0, 0, 0, ([TimeSpan]::Zero)

    $output = New-Object IO.FileStream(
        $temporaryPath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None,
        65536,
        [IO.FileOptions]::WriteThrough)
    try {
        $archive = New-Object IO.Compression.ZipArchive(
            $output,
            [IO.Compression.ZipArchiveMode]::Create,
            $true)
        try {
            foreach ($name in $entryNames) {
                $entry = $archive.CreateEntry(
                    $name,
                    [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $normalizedTimestamp
                # Zeroed external attributes keep the archive byte-reproducible.
                $entry.ExternalAttributes = 0

                $entryStream = $entry.Open()
                try {
                    $entrySources[$name].CopyTo($entryStream)
                }
                finally {
                    $entryStream.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
        $output.Flush($true)
    }
    finally {
        $output.Dispose()
    }

    if ($null -ne $AdditionalStagedArchiveValidation) {
        $null = & $AdditionalStagedArchiveValidation $temporaryPath
    }

    $destinationFull = Publish-DesktopAICompanionAtomicFile `
        -TemporaryPath $temporaryPath `
        -DestinationPath $destinationFull `
        -TrustedRoot $destinationParent `
        -ProtectedPaths @($manifestFull, $markerFull) `
        -ProtectedDirectories @($runtimeRootFull)
}
catch {
    $zipPrimaryError = $_
    throw
}
finally {
    if ($null -ne $temporaryDirectoryLease) {
        $temporaryDirectoryLease.Dispose()
        $temporaryDirectoryLease = $null
    }
    if (Test-Path -LiteralPath $temporaryDirectory) {
        try {
            Remove-DesktopAICompanionSafeDirectory `
                -Path $temporaryDirectory `
                -AllowedRoot $destinationParent `
                -TrustedRoot $destinationParent
        }
        catch {
            if ($null -eq $zipPrimaryError) {
                throw
            }
            Write-Warning (
                'Portable ZIP scratch cleanup also failed; preserving the ' +
                "primary error. Cleanup error: $($_.Exception.Message)")
        }
    }
}

Write-Host (
    "Deterministic portable ZIP created: {0} ({1} entries)." -f
    $destinationFull,
    $entryNames.Count) -ForegroundColor Green
}
finally {
    foreach ($validatedInput in $validatedInputs) {
        $validatedInput.Dispose()
    }
}