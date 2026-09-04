#requires -Version 5
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [ValidatePattern('^[A-Za-z0-9.-]+$')]
    [string]$ComponentNamespace = 'DesktopAICompanion'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$maximumManifestBytes = 1MB
$pathSafety = Join-Path $repoRoot 'packaging\StagingPathSafety.ps1'
if (-not (Test-Path -LiteralPath $pathSafety -PathType Leaf)) {
    throw "Packaging path-safety policy is missing: $pathSafety"
}
. $pathSafety

function Get-DeterministicComponentGuid {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Namespace
    )

    # Component GUIDs must remain stable across builds so major upgrades can service
    # the same resources. Derive one from a fixed product namespace plus the
    # case-normalized manifest entry instead of persisting a second hand-written list.
    $inputBytes = [Text.Encoding]::UTF8.GetBytes(
        "$Namespace/runtime/" + $Name.ToLowerInvariant())
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash($inputBytes)
    }
    finally {
        $sha256.Dispose()
    }

    $guidBytes = New-Object byte[] 16
    [Array]::Copy($hash, $guidBytes, 16)
    # Mark the value as an RFC 4122 name-derived UUID and set the RFC variant.
    # Guid(byte[]) stores the first three fields little-endian, hence byte 7.
    $guidBytes[7] = ($guidBytes[7] -band 0x0f) -bor 0x50
    $guidBytes[8] = ($guidBytes[8] -band 0x3f) -bor 0x80
    return (New-Object Guid (,$guidBytes)).ToString().ToUpperInvariant()
}

if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "Runtime payload manifest not found: $ManifestPath"
}
$manifestFull = (Resolve-Path -LiteralPath $ManifestPath).Path
$manifestInput = Open-DesktopAICompanionValidatedInputFile `
    -Path $manifestFull `
    -Root (Split-Path -Parent $manifestFull)
try {
$outputFull = [IO.Path]::GetFullPath($OutputPath)
$outputParent = Split-Path -Parent $outputFull
if ([string]::IsNullOrWhiteSpace($outputParent) -or
    -not (Test-Path -LiteralPath $outputParent -PathType Container)) {
    throw "WiX fragment output parent must already exist: $outputParent"
}
$outputFull = Assert-DesktopAICompanionOutputFileSafe `
    -Path $outputFull `
    -TrustedRoot $outputParent `
    -ProtectedPaths @($manifestFull)
$fragmentDestinationExists = $false
$fragmentDestinationSha256 = $null
if (Test-Path -LiteralPath $outputFull -PathType Leaf) {
    $fragmentDestinationInput = Open-DesktopAICompanionValidatedInputFile `
        -Path $outputFull `
        -Root $outputParent
    try {
        $fragmentDestinationSha256 =
            $fragmentDestinationInput.ComputeHash('SHA256')
        $fragmentDestinationExists = $true
    }
    finally {
        $fragmentDestinationInput.Dispose()
    }
}
elseif (Test-Path -LiteralPath $outputFull) {
    throw "WiX fragment destination is not a regular file: $outputFull"
}

$files = @(
    $manifestInput.ReadAllTextUtf8($maximumManifestBytes) -split '\r?\n' |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') }
)
if ($files.Count -eq 0) { throw 'Runtime payload manifest is empty.' }
if (@($files | Group-Object | Where-Object Count -gt 1).Count -gt 0) {
    throw 'Runtime payload manifest contains duplicate entries.'
}
if ($files -notcontains 'DesktopAICompanion.exe') {
    throw 'Runtime payload manifest must contain DesktopAICompanion.exe.'
}

$identifierByFile =
    New-Object 'Collections.Generic.Dictionary[string,string]' (
        [StringComparer]::OrdinalIgnoreCase)
$fileByIdentifier =
    New-Object 'Collections.Generic.Dictionary[string,string]' (
        [StringComparer]::OrdinalIgnoreCase)
foreach ($file in $files) {
    if (-not (Test-DesktopAICompanionWindowsLeafName -Name $file)) {
        throw "Runtime payload entries must be plain file names: '$file'"
    }
    $identifier =
        'Runtime_' + [regex]::Replace($file, '[^A-Za-z0-9_]', '_')
    if ($fileByIdentifier.ContainsKey($identifier)) {
        throw (
            "Runtime payload entries '$($fileByIdentifier[$identifier])' " +
            "and '$file' normalize to duplicate WiX identifier " +
            "'$identifier'.")
    }
    $identifierByFile.Add($file, $identifier)
    $fileByIdentifier.Add($identifier, $file)
}

$builder = New-Object Text.StringBuilder
[void]$builder.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
[void]$builder.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$builder.AppendLine('  <!-- Generated from packaging/runtime-files.txt. Do not edit this build artifact. -->')
[void]$builder.AppendLine('  <Fragment>')
[void]$builder.AppendLine('    <ComponentGroup Id="RuntimeComponents" Directory="INSTALLFOLDER">')

foreach ($file in $files) {
    $escapedFile = [Security.SecurityElement]::Escape($file)
    $identifier = $identifierByFile[$file]
    $componentGuid = Get-DeterministicComponentGuid `
        -Name $file `
        -Namespace $ComponentNamespace
    [void]$builder.AppendLine("      <Component Id=`"Cmp_$identifier`" Guid=`"$componentGuid`">")

    if ($file -eq 'DesktopAICompanion.exe') {
        [void]$builder.AppendLine('        <File Id="DesktopAICompanionExe" Source="DesktopAICompanion.exe" KeyPath="no">')
        [void]$builder.AppendLine('          <Shortcut Id="StartMenuShortcut" Directory="AppMenuFolder" Name="$(var.ProductName)"')
        [void]$builder.AppendLine('                    Description="A desktop pet with offline smart fortunes and an optional AI brain"')
        [void]$builder.AppendLine('                    WorkingDirectory="INSTALLFOLDER" />')
        [void]$builder.AppendLine('          <Shortcut Id="DesktopShortcut" Directory="DesktopFolder" Name="$(var.ProductName)"')
        [void]$builder.AppendLine('                    Description="A desktop pet with offline smart fortunes and an optional AI brain"')
        [void]$builder.AppendLine('                    WorkingDirectory="INSTALLFOLDER" />')
        [void]$builder.AppendLine('        </File>')
    }
    else {
        [void]$builder.AppendLine("        <File Id=`"File_$identifier`" Source=`"$escapedFile`" KeyPath=`"no`" />")
    }

    [void]$builder.AppendLine("        <RegistryValue Root=`"HKCU`" Key=`"[DESKTOP_AI_COMPANION_REGISTRYROOT]\Components`"")
    [void]$builder.AppendLine("                       Name=`"$identifier`" Type=`"integer`" Value=`"1`" KeyPath=`"yes`" />")
    [void]$builder.AppendLine('      </Component>')
}

[void]$builder.AppendLine('    </ComponentGroup>')
[void]$builder.AppendLine('  </Fragment>')
[void]$builder.AppendLine('</Wix>')

$temporaryDirectory = Join-Path $outputParent (
    '.DesktopAICompanion-wix-fragment-' + [Guid]::NewGuid().ToString('N'))
$temporaryDirectoryCreated = $false
$temporaryDirectoryLease = $null
$fragmentSealedFile = $null
$fragmentSha256 = $null
$fragmentPrimaryError = $null
try {
    $temporaryDirectoryLease = Open-DesktopAICompanionNewScratchDirectory `
        -Path $temporaryDirectory `
        -AllowedRoot $outputParent `
        -TrustedRoot $outputParent `
        -ProtectedPaths @($manifestFull, $outputFull)
    $temporaryDirectoryCreated = $true
    $temporaryPath = Join-Path $temporaryDirectory (
        [IO.Path]::GetFileName($outputFull) + '.tmp')
    $temporaryPath = Assert-DesktopAICompanionOutputFileSafe `
        -Path $temporaryPath `
        -TrustedRoot $temporaryDirectory `
        -ProtectedPaths @($manifestFull, $outputFull)
    $fragmentBytes = (New-Object Text.UTF8Encoding($false)).GetBytes(
        $builder.ToString())
    $fragmentHasher = [Security.Cryptography.SHA256]::Create()
    try {
        $expectedFragmentSha256 = ([BitConverter]::ToString(
            $fragmentHasher.ComputeHash($fragmentBytes))).Replace('-', '')
    }
    finally {
        $fragmentHasher.Dispose()
    }
    $fragmentStream = New-Object IO.FileStream(
        $temporaryPath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None,
        65536,
        [IO.FileOptions]::WriteThrough)
    try {
        $fragmentStream.Write($fragmentBytes, 0, $fragmentBytes.Length)
        $fragmentStream.Flush($true)
    }
    finally {
        $fragmentStream.Dispose()
    }
    $fragmentSealedFile = Open-DesktopAICompanionSealedStagedFile `
        -Path $temporaryPath `
        -Root $temporaryDirectory
    [xml]$validatedXml =
        $fragmentSealedFile.ReadAllTextUtf8(16MB)
    if ($null -eq $validatedXml.DocumentElement) {
        throw 'Generated WiX fragment XML has no document element.'
    }
    $fragmentSha256 = $fragmentSealedFile.ComputeHash('SHA256')
    if ($fragmentSha256 -cne $expectedFragmentSha256) {
        throw (
            'Generated WiX fragment bytes differ from the exact in-memory ' +
            'authoring output.')
    }
    $publishFragmentParameters = @{
        TemporaryPath = $temporaryPath
        DestinationPath = $outputFull
        TrustedRoot = $outputParent
        ProtectedPaths = @($manifestFull)
        SealedTemporaryFile = $fragmentSealedFile
        ExpectedTemporarySha256 = $fragmentSha256
    }
    if ($fragmentDestinationExists) {
        $publishFragmentParameters.ExpectedDestinationSha256 =
            $fragmentDestinationSha256
    }
    else {
        $publishFragmentParameters.DestinationMustBeAbsent = $true
    }
    $outputFull =
        Publish-DesktopAICompanionAtomicFile @publishFragmentParameters
}
catch {
    $fragmentPrimaryError = $_
    throw
}
finally {
    if ($null -ne $fragmentSealedFile) {
        $fragmentSealedFile.Dispose()
        $fragmentSealedFile = $null
    }
    if ($null -ne $temporaryDirectoryLease) {
        $temporaryDirectoryLease.Dispose()
        $temporaryDirectoryLease = $null
    }
    if ($temporaryDirectoryCreated -and
        (Test-Path -LiteralPath $temporaryDirectory)) {
        try {
            Remove-DesktopAICompanionSafeDirectory `
                -Path $temporaryDirectory `
                -AllowedRoot $outputParent `
                -TrustedRoot $outputParent
        }
        catch {
            if ($null -eq $fragmentPrimaryError) {
                throw
            }
            Write-Warning (
                'WiX fragment scratch cleanup also failed; preserving the ' +
                "primary error. Cleanup error: $($_.Exception.Message)")
        }
    }
}
}
finally {
    $manifestInput.Dispose()
}
