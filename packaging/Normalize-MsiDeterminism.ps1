#requires -Version 5
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9.-]+$')]
    [string]$IdentityNamespace
)

$ErrorActionPreference = 'Stop'

$scriptDirectory = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    $PSScriptRoot
}
else {
    Split-Path -Parent $MyInvocation.MyCommand.Path
}
. (Join-Path $scriptDirectory 'StagingPathSafety.ps1')

function Get-DeterministicGuid {
    param([Parameter(Mandatory = $true)][string]$Name)

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($Name))
    }
    finally {
        $sha256.Dispose()
    }

    $guidBytes = New-Object byte[] 16
    [Array]::Copy($hash, $guidBytes, 16)
    # Mark the value as a name-derived RFC 4122 UUID. Guid(byte[]) stores the
    # first three fields little-endian, so the version nibble is byte 7.
    $guidBytes[7] = ($guidBytes[7] -band 0x0f) -bor 0x50
    $guidBytes[8] = ($guidBytes[8] -band 0x3f) -bor 0x80
    return '{' + (New-Object Guid (,$guidBytes)).ToString().ToUpperInvariant() + '}'
}

function Release-ComObject {
    param($InputObject)
    if ($null -ne $InputObject -and
        [Runtime.InteropServices.Marshal]::IsComObject($InputObject)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($InputObject)
    }
}

function Get-MsiScalar {
    param(
        [Parameter(Mandatory = $true)]$Database,
        [Parameter(Mandatory = $true)][string]$Query
    )

    $view = $null
    $record = $null
    try {
        $view = $Database.OpenView($Query)
        $view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) {
            return $null
        }
        $value = [string]$record.StringData(1)
        return $value
    }
    finally {
        Release-ComObject $record
        Release-ComObject $view
    }
}

function Set-MsiProductCodeAndSummary {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ProductCode,
        [Parameter(Mandatory = $true)][string]$PackageCode,
        [Parameter(Mandatory = $true)][DateTime]$Timestamp
    )

    $installer = $null
    $database = $null
    $view = $null
    $summary = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        # 1 is msiOpenDatabaseModeTransact.
        $database = $installer.OpenDatabase($Path, 1)
        $view = $database.OpenView(
            "UPDATE ``Property`` SET ``Value``='$ProductCode' " +
            "WHERE ``Property``='ProductCode'")
        $view.Execute()

        # The package code and build timestamps live in the Summary Information
        # stream rather than MSI tables. Persist the stream before committing the
        # database; reversing that order silently discards the summary changes.
        $summary = $database.SummaryInformation(20)
        $summary.Property(9) = $PackageCode
        $summary.Property(12) = $Timestamp
        $summary.Property(13) = $Timestamp
        $summary.Persist()
        $database.Commit()
    }
    finally {
        Release-ComObject $summary
        Release-ComObject $view
        Release-ComObject $database
        Release-ComObject $installer
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
    }
}

function Clear-CompoundFileRootTimestamps {
    param([Parameter(Mandatory = $true)][string]$Path)

    # MSI databases are OLE Compound Files. Windows Installer rewrites the root
    # storage modification time whenever the database is committed, even when all
    # logical content is identical. Normalize only the two root-entry FILETIME
    # fields after validating the CFB header and directory entry structure.
    $bytes = [IO.File]::ReadAllBytes($Path)
    $signature = [byte[]]@(0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1)
    if ($bytes.Length -lt 512) {
        throw "MSI compound file is too small: $Path"
    }
    for ($index = 0; $index -lt $signature.Length; $index++) {
        if ($bytes[$index] -ne $signature[$index]) {
            throw "MSI does not have a valid OLE Compound File signature: $Path"
        }
    }
    if ([BitConverter]::ToUInt16($bytes, 28) -ne 0xfffe) {
        throw "MSI compound file has an unsupported byte order: $Path"
    }

    $sectorShift = [BitConverter]::ToUInt16($bytes, 30)
    if ($sectorShift -ne 9 -and $sectorShift -ne 12) {
        throw "MSI compound file has an unsupported sector shift '$sectorShift': $Path"
    }
    $sectorSize = [int64]1 -shl $sectorShift
    $directorySector = [BitConverter]::ToInt32($bytes, 48)
    if ($directorySector -lt 0) {
        throw "MSI compound file has no root directory sector: $Path"
    }

    $rootOffset = ([int64]$directorySector + 1) * $sectorSize
    if ($rootOffset -lt 512 -or $rootOffset + 128 -gt $bytes.LongLength) {
        throw "MSI compound file root directory is outside the file: $Path"
    }
    $rootOffset32 = [int]$rootOffset
    $nameLength = [BitConverter]::ToUInt16($bytes, $rootOffset32 + 64)
    if ($nameLength -lt 2 -or $nameLength -gt 64 -or
        $bytes[$rootOffset32 + 66] -ne 5) {
        throw "MSI compound file root directory entry is invalid: $Path"
    }
    $rootName = [Text.Encoding]::Unicode.GetString(
        $bytes, $rootOffset32, $nameLength - 2)
    if ($rootName -cne 'Root Entry') {
        throw "MSI compound file has an unexpected root entry '$rootName': $Path"
    }

    for ($offset = 100; $offset -lt 116; $offset++) {
        $bytes[$rootOffset32 + $offset] = 0
    }
    [IO.File]::WriteAllBytes($Path, $bytes)
}

$stagingDirectory = $null
$stagingDirectoryLease = $null
$temporaryMsi = $null
$temporaryMsiLease = $null
$sealedTemporaryMsi = $null
$temporaryMsiHash = $null
$validationMsi = $null
$validationMsiInput = $null
$normalizationPrimaryError = $null
try {
    if (-not (Test-Path -LiteralPath $MsiPath -PathType Leaf)) {
        throw "MSI not found: $MsiPath"
    }
    $destinationMsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
    if ([IO.Path]::GetExtension($destinationMsiPath) -cne '.msi') {
        throw (
            'Deterministic normalization accepts only an .msi file: ' +
            $destinationMsiPath)
    }

    $msiParent = Split-Path -Parent $destinationMsiPath
    [void](Assert-DesktopAICompanionPathChainSafe `
        -Path $msiParent `
        -TrustedRoot $msiParent)
    $stagingDirectory = Join-Path $msiParent (
        '.DesktopAICompanion-msi-normalize-' + [Guid]::NewGuid().ToString('N'))
    $stagingDirectoryLease = Open-DesktopAICompanionNewScratchDirectory `
        -Path $stagingDirectory `
        -AllowedRoot $msiParent `
        -TrustedRoot $msiParent `
        -ProtectedPaths @($destinationMsiPath)
    $temporaryMsi = Join-Path $stagingDirectory (
        [IO.Path]::GetFileName($destinationMsiPath))
    $temporaryMsi = Assert-DesktopAICompanionOutputFileSafe `
        -Path $temporaryMsi `
        -TrustedRoot $stagingDirectory `
        -ProtectedPaths @($destinationMsiPath)

    $sourceInput = Open-DesktopAICompanionValidatedInputFile `
        -Path $destinationMsiPath `
        -Root $msiParent
    try {
        $originalHash = $sourceInput.ComputeHash('SHA256')
        $sourceInput.CopyToFile($temporaryMsi)
    }
    finally {
        $sourceInput.Dispose()
    }
    $temporaryMsiLease = Open-DesktopAICompanionValidatedMutableFile `
        -Path $temporaryMsi `
        -Root $stagingDirectory
    $resolvedMsiPath = $temporaryMsi

$signatureStatus = Get-AuthenticodeSignature -LiteralPath $resolvedMsiPath
if ($signatureStatus.Status -ne [Management.Automation.SignatureStatus]::NotSigned) {
    throw (
        "Refusing to rewrite an MSI with Authenticode status " +
        "'$($signatureStatus.Status)': $resolvedMsiPath")
}

$installer = $null
$database = $null
$summary = $null
try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    # 0 is msiOpenDatabaseModeReadOnly.
    $database = $installer.OpenDatabase($resolvedMsiPath, 0)
    $productName = Get-MsiScalar $database (
        "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductName'")
    $productVersion = Get-MsiScalar $database (
        "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductVersion'")
    $upgradeCode = Get-MsiScalar $database (
        'SELECT `UpgradeCode` FROM `Upgrade`')
    $summary = $database.SummaryInformation(0)
    $template = [string]$summary.Property(7)
}
finally {
    Release-ComObject $summary
    Release-ComObject $database
    Release-ComObject $installer
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}

$identityValues = @{
    ProductName = $productName
    ProductVersion = $productVersion
    UpgradeCode = $upgradeCode
    Template = $template
}
foreach ($requiredValue in $identityValues.GetEnumerator()) {
    if ([string]::IsNullOrWhiteSpace([string]$requiredValue.Value)) {
        throw (
            "MSI identity metadata '$($requiredValue.Key)' is missing: " +
            $resolvedMsiPath)
    }
}
if ($upgradeCode -notmatch '^\{[0-9A-Fa-f-]{36}\}$') {
    throw "MSI UpgradeCode is invalid: '$upgradeCode'"
}

$placeholderCode = '{00000000-0000-0000-0000-000000000000}'
$fixedTimestamp = [DateTime]::SpecifyKind(
    [DateTime]'2000-01-01T00:00:00',
    [DateTimeKind]::Utc)

# First strip ALL WiX-generated identity (ProductCode and PackageCode) and time
# entropy to a fixed placeholder, then hash. This base hash reflects only the MSI
# payload and authoring -- no generated GUID -- so it is stable for equal inputs
# yet changes whenever the installed content changes.
Set-MsiProductCodeAndSummary `
    -Path $resolvedMsiPath `
    -ProductCode $placeholderCode `
    -PackageCode $placeholderCode `
    -Timestamp $fixedTimestamp
Clear-CompoundFileRootTimestamps -Path $resolvedMsiPath
$baseHash = (Get-FileHash -LiteralPath $resolvedMsiPath -Algorithm SHA256).Hash

# Fold that content hash into the ProductCode seed. Windows Installer refuses to
# install a package whose ProductCode is already installed but whose PackageCode
# differs (error 1638), so a version-only ProductCode makes any rebuilt same
# version uninstallable over the prior one. Deriving the ProductCode from the
# content too gives a changed same-version build a distinct ProductCode, so it
# installs as a same-version major upgrade (DesktopAICompanion.wxs sets
# AllowSameVersionUpgrades). Released versions always bump, so this never alters
# the release-to-release upgrade path; it only unblocks reinstalling a rebuilt
# same version. Determinism holds: equal payload -> equal base hash -> equal code.
$productSeed = @(
    $IdentityNamespace
    'msi-product'
    ([string]$upgradeCode).ToUpperInvariant()
    $productName
    $productVersion
    $template
    $baseHash
) -join '|'
$productCode = Get-DeterministicGuid $productSeed

# Set the real ProductCode, then re-hash so the PackageCode stays sensitive to
# the complete package (ProductCode included), exactly as before.
Set-MsiProductCodeAndSummary `
    -Path $resolvedMsiPath `
    -ProductCode $productCode `
    -PackageCode $placeholderCode `
    -Timestamp $fixedTimestamp
Clear-CompoundFileRootTimestamps -Path $resolvedMsiPath
$contentHash = (Get-FileHash -LiteralPath $resolvedMsiPath -Algorithm SHA256).Hash
$packageCode = Get-DeterministicGuid (
    "$IdentityNamespace|msi-package|$contentHash")

Set-MsiProductCodeAndSummary `
    -Path $resolvedMsiPath `
    -ProductCode $productCode `
    -PackageCode $packageCode `
    -Timestamp $fixedTimestamp
Clear-CompoundFileRootTimestamps -Path $resolvedMsiPath

$sealedTemporaryMsi = $temporaryMsiLease.Seal()
$temporaryMsiLease = $null
$temporaryMsiHash =
    $sealedTemporaryMsi.ComputeHash('SHA256')
$validationMsi = Join-Path $stagingDirectory (
    '.validation-' + [Guid]::NewGuid().ToString('N') + '.msi')
$sealedTemporaryMsi.CopyToFile($validationMsi)
$validationMsiInput = Open-DesktopAICompanionValidatedInputFile `
    -Path $validationMsi `
    -Root $stagingDirectory
if ($validationMsiInput.ComputeHash('SHA256') -cne
    $temporaryMsiHash) {
    throw 'MSI sealed validation copy differs from the exact staged MSI.'
}

$installer = $null
$database = $null
$summary = $null
try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.OpenDatabase($validationMsi, 0)
    $actualProductCode = Get-MsiScalar $database (
        "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductCode'")
    $summary = $database.SummaryInformation(0)
    $actualPackageCode = [string]$summary.Property(9)
    $created = [DateTime]$summary.Property(12)
    $lastSaved = [DateTime]$summary.Property(13)
}
finally {
    Release-ComObject $summary
    Release-ComObject $database
    Release-ComObject $installer
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}

if ($actualProductCode -cne $productCode -or
    $actualPackageCode -cne $packageCode) {
    throw "MSI deterministic identity verification failed: $resolvedMsiPath"
}
# Windows Installer Automation returns FILETIME values with DateTimeKind
# Unspecified. Compare the authored wall-clock ticks so the host time zone
# cannot affect verification.
if ($created.Ticks -ne $fixedTimestamp.Ticks -or
    $lastSaved.Ticks -ne $fixedTimestamp.Ticks) {
    throw "MSI deterministic timestamp verification failed: $resolvedMsiPath"
}

[void](Publish-DesktopAICompanionAtomicFile `
    -TemporaryPath $temporaryMsi `
    -DestinationPath $destinationMsiPath `
    -TrustedRoot $msiParent `
    -SealedTemporaryFile $sealedTemporaryMsi `
    -ExpectedTemporarySha256 $temporaryMsiHash `
    -ExpectedDestinationSha256 $originalHash)
$temporaryMsi = $null
$finalHash = $sealedTemporaryMsi.ComputeHash('SHA256')
Write-Host (
    "Deterministic MSI normalized: ProductCode={0}, PackageCode={1}, SHA-256={2}" -f
    $productCode,
    $packageCode,
    $finalHash
) -ForegroundColor DarkGray
}
catch {
    $normalizationPrimaryError = $_
    throw
}
finally {
    if ($null -ne $validationMsiInput) {
        $validationMsiInput.Dispose()
        $validationMsiInput = $null
    }
    if ($null -ne $sealedTemporaryMsi) {
        $sealedTemporaryMsi.Dispose()
        $sealedTemporaryMsi = $null
    }
    if ($null -ne $temporaryMsiLease) {
        $temporaryMsiLease.Dispose()
        $temporaryMsiLease = $null
    }
    if ($null -ne $stagingDirectoryLease) {
        $stagingDirectoryLease.Dispose()
        $stagingDirectoryLease = $null
    }
    if ($null -ne $stagingDirectory -and
        (Test-Path -LiteralPath $stagingDirectory)) {
        $msiParent = Split-Path -Parent $stagingDirectory
        try {
            Remove-DesktopAICompanionSafeDirectory `
                -Path $stagingDirectory `
                -AllowedRoot $msiParent `
                -TrustedRoot $msiParent
        }
        catch {
            if ($null -eq $normalizationPrimaryError) {
                throw
            }
            Write-Warning (
                'MSI normalization scratch cleanup also failed; preserving ' +
                "the primary error. Cleanup error: $($_.Exception.Message)")
        }
    }
}
