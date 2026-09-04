#requires -Version 5

function Get-DesktopAICompanionDotnetGlobalToolRoot {
    [CmdletBinding()]
    param(
        [AllowEmptyString()][string]$DotnetCliHome =
            [Environment]::GetEnvironmentVariable(
                'DOTNET_CLI_HOME',
                'Process'),
        [AllowEmptyString()][string]$UserProfile =
            [Environment]::GetEnvironmentVariable(
                'USERPROFILE',
                'Process')
    )

    $toolHome = $DotnetCliHome
    if ([string]::IsNullOrWhiteSpace($toolHome)) {
        $toolHome = $UserProfile
    }
    if ([string]::IsNullOrWhiteSpace($toolHome)) {
        throw (
            'Neither DOTNET_CLI_HOME nor USERPROFILE identifies the .NET ' +
            'global-tool home.')
    }
    if (-not [IO.Path]::IsPathRooted($toolHome)) {
        throw ".NET global-tool home must be an absolute path: '$toolHome'."
    }

    $resolvedHome = [IO.Path]::GetFullPath($toolHome)
    return Join-Path $resolvedHome '.dotnet\tools'
}

function Get-DesktopAICompanionWixGlobalExtensionRoot {
    [CmdletBinding()]
    param(
        [AllowEmptyString()][string]$UserProfile =
            [Environment]::GetEnvironmentVariable(
                'USERPROFILE',
                'Process')
    )

    if ([string]::IsNullOrWhiteSpace($UserProfile) -or
        -not [IO.Path]::IsPathRooted($UserProfile)) {
        throw (
            'USERPROFILE must identify an absolute WiX global-extension ' +
            "home: '$UserProfile'.")
    }
    return Join-Path (
        [IO.Path]::GetFullPath($UserProfile)) '.wix\extensions'
}

function Get-DesktopAICompanionWixToolPayload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$PackageInput
    )

    if (-not (Get-Command `
            'Test-DesktopAICompanionWindowsLeafName' `
            -ErrorAction SilentlyContinue)) {
        throw (
            'WixToolchainPolicy requires StagingPathSafety.ps1 to be ' +
            'dot-sourced first; missing command ' +
            "'Test-DesktopAICompanionWindowsLeafName'.")
    }

    Add-Type -AssemblyName System.IO.Compression
    $packageBytes = New-Object IO.MemoryStream
    try {
        $PackageInput.CopyTo($packageBytes)
        $packageBytes.Position = 0
        $archive = New-Object IO.Compression.ZipArchive(
            $packageBytes,
            [IO.Compression.ZipArchiveMode]::Read,
            $true)
        try {
            $entries = @(
                $archive.Entries |
                    Where-Object {
                        $_.FullName -cmatch
                            '^tools/[^/]+/any/wix\.exe$'
                    }
            )
            if ($entries.Count -ne 1) {
                throw (
                    'The locked WiX tool package must contain exactly one ' +
                    'tools/<tfm>/any/wix.exe payload; found ' +
                    "$($entries.Count).")
            }

            $executableEntry = $entries[0]
            $subtreeRelativePath =
                [string]$executableEntry.FullName.Substring(
                    0,
                    $executableEntry.FullName.LastIndexOf('/'))
            $subtreePrefix = $subtreeRelativePath + '/'
            $manifestByPath =
                New-Object `
                    'Collections.Generic.Dictionary[string,object]' `
                    ([StringComparer]::OrdinalIgnoreCase)

            foreach ($entry in $archive.Entries) {
                $relativePath = [string]$entry.FullName
                if (-not $relativePath.StartsWith(
                        $subtreePrefix,
                        [StringComparison]::Ordinal)) {
                    continue
                }
                if ($relativePath.EndsWith(
                        '/',
                        [StringComparison]::Ordinal)) {
                    continue
                }

                $subtreePath =
                    $relativePath.Substring($subtreePrefix.Length)
                if ([string]::IsNullOrWhiteSpace($subtreePath) -or
                    $relativePath.IndexOf('\') -ge 0) {
                    throw (
                        'The locked WiX tool package contains an unsafe ' +
                        "payload path: '$relativePath'.")
                }
                $segments = @($subtreePath.Split([char]'/', [StringSplitOptions]::None))
                foreach ($segment in $segments) {
                    if (-not (Test-DesktopAICompanionWindowsLeafName -Name $segment)) {
                        throw (
                            'The locked WiX tool package contains an unsafe ' +
                            "payload path: '$relativePath'.")
                    }
                }

                $externalAttributes = [BitConverter]::ToUInt32(
                    [BitConverter]::GetBytes(
                        [int]$entry.ExternalAttributes),
                    0)
                $unixFileType =
                    ($externalAttributes -shr 16) -band 0xF000
                if (($externalAttributes -band
                        [uint32][IO.FileAttributes]::ReparsePoint) -ne 0 -or
                    ($unixFileType -ne 0 -and
                        $unixFileType -ne 0x8000)) {
                    throw (
                        'The locked WiX tool package contains a non-regular ' +
                        "payload entry: '$relativePath'.")
                }
                if ($manifestByPath.ContainsKey($relativePath)) {
                    throw (
                        'The locked WiX tool package contains duplicate or ' +
                        'case-colliding payload paths: ' +
                        "'$relativePath'.")
                }

                $stream = $entry.Open()
                $sha256 = [Security.Cryptography.SHA256]::Create()
                try {
                    $hash = ([BitConverter]::ToString(
                        $sha256.ComputeHash($stream))).Replace('-', '')
                }
                finally {
                    $sha256.Dispose()
                    $stream.Dispose()
                }
                $manifestByPath.Add(
                    $relativePath,
                    [pscustomobject][ordered]@{
                        RelativePath = $relativePath
                        Length = [long]$entry.Length
                        Sha256 = $hash
                    })
            }

            $executableRelativePath =
                [string]$executableEntry.FullName
            if (-not $manifestByPath.ContainsKey(
                    $executableRelativePath)) {
                throw (
                    'The locked WiX executable is not a regular file in ' +
                    'the selected tool payload subtree.')
            }
            $manifestPaths = [string[]]@($manifestByPath.Keys)
            [Array]::Sort(
                $manifestPaths,
                [StringComparer]::Ordinal)
            $manifest = New-Object object[] $manifestPaths.Count
            for ($index = 0;
                $index -lt $manifestPaths.Count;
                $index++) {
                $manifest[$index] =
                    $manifestByPath[$manifestPaths[$index]]
            }
            $executable =
                $manifestByPath[$executableRelativePath]

            return [pscustomobject][ordered]@{
                RelativePath = $executableRelativePath
                Length = [long]$executable.Length
                Sha256 = [string]$executable.Sha256
                SubtreeRelativePath = $subtreeRelativePath
                Files = [object[]]$manifest
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $packageBytes.Dispose()
    }
}

function Get-DesktopAICompanionInstalledWixPayloadInventory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$PayloadRoot,
        [Parameter(Mandatory = $true)][string]$StoreRoot
    )

    $resolvedPayloadRoot = [IO.Path]::GetFullPath($PayloadRoot)
    $resolvedStoreRoot = [IO.Path]::GetFullPath($StoreRoot)
    if (-not (Test-Path `
            -LiteralPath $resolvedPayloadRoot `
            -PathType Container)) {
        throw "The installed WiX payload subtree is missing: $resolvedPayloadRoot"
    }
    [void](Assert-DesktopAICompanionPathChainSafe `
        -Path $resolvedPayloadRoot `
        -TrustedRoot $resolvedStoreRoot)

    $storePrefix =
        $resolvedStoreRoot.TrimEnd('\') + '\'
    $inventory =
        New-Object `
            'Collections.Generic.Dictionary[string,object]' `
            ([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in @(
            Get-ChildItem `
                -LiteralPath $resolvedPayloadRoot `
                -Recurse `
                -Force `
                -ErrorAction Stop)) {
        if (($item.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw (
                'The installed WiX payload contains a reparse point: ' +
                $item.FullName)
        }
        if ($item.PSIsContainer) {
            continue
        }

        $fullPath = [IO.Path]::GetFullPath($item.FullName)
        if (-not $fullPath.StartsWith(
                $storePrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw (
                'The installed WiX payload escaped its store root: ' +
                $fullPath)
        }
        $relativePath =
            $fullPath.Substring($storePrefix.Length).Replace('\', '/')
        if ($inventory.ContainsKey($relativePath)) {
            throw (
                'The installed WiX payload contains duplicate or ' +
                'case-colliding file paths: ' +
                "'$relativePath'.")
        }
        $inventory.Add(
            $relativePath,
            [pscustomobject][ordered]@{
                RelativePath = $relativePath
                Path = $fullPath
            })
    }
    return ,$inventory
}

function Assert-DesktopAICompanionInstalledWixPayloadFileSet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$ExpectedFiles,
        [Parameter(Mandatory = $true)]$ObservedFiles
    )

    $expectedByPath =
        New-Object `
            'Collections.Generic.Dictionary[string,object]' `
            ([StringComparer]::OrdinalIgnoreCase)
    foreach ($expected in @($ExpectedFiles)) {
        $relativePath = [string]$expected.RelativePath
        if ($expectedByPath.ContainsKey($relativePath)) {
            throw (
                'The locked WiX payload manifest contains duplicate or ' +
                'case-colliding paths: ' +
                "'$relativePath'.")
        }
        $expectedByPath.Add($relativePath, $expected)
    }

    foreach ($expected in @($ExpectedFiles)) {
        $relativePath = [string]$expected.RelativePath
        if (-not $ObservedFiles.ContainsKey($relativePath)) {
            throw (
                'The installed WiX payload is missing locked package ' +
                "file '$relativePath'.")
        }
        if ([string]$ObservedFiles[$relativePath].RelativePath -cne
            $relativePath) {
            throw (
                'The installed WiX payload path casing differs from the ' +
                "locked package: '$relativePath'.")
        }
    }

    $observedPaths = [string[]]@($ObservedFiles.Keys)
    [Array]::Sort(
        $observedPaths,
        [StringComparer]::Ordinal)
    foreach ($relativePath in $observedPaths) {
        if (-not $expectedByPath.ContainsKey($relativePath)) {
            throw (
                'The installed WiX payload contains unexpected file ' +
                "'$relativePath'.")
        }
    }
}

function Open-DesktopAICompanionLockedWixExecutable {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$LockPath,
        [Parameter(Mandatory = $true)][string]$ToolRoot
    )

    foreach ($requiredCommand in @(
            'Open-DesktopAICompanionValidatedInputFile',
            'Test-DesktopAICompanionWindowsLeafName',
            'Assert-DesktopAICompanionPathChainSafe')) {
        if (-not (Get-Command $requiredCommand -ErrorAction SilentlyContinue)) {
            throw (
                'WixToolchainPolicy requires StagingPathSafety.ps1 to be ' +
                "dot-sourced first; missing command '$requiredCommand'.")
        }
    }

    $resolvedLock = [IO.Path]::GetFullPath($LockPath)
    $resolvedToolRoot = [IO.Path]::GetFullPath($ToolRoot)
    if (-not (Test-Path -LiteralPath $resolvedToolRoot -PathType Container)) {
        throw "Locked WiX tool root is missing: $resolvedToolRoot"
    }
    $lockInput = Open-DesktopAICompanionValidatedInputFile `
        -Path $resolvedLock `
        -Root (Split-Path -Parent $resolvedLock)
    try {
        $lock = $lockInput.ReadAllTextUtf8(1MB) | ConvertFrom-Json
    }
    finally {
        $lockInput.Dispose()
    }
    if ([int]$lock.schemaVersion -ne 1 -or
        [string]$lock.wixVersion -cne '5.0.2') {
        throw 'The locked WiX policy must pin schema 1 and WiX 5.0.2.'
    }
    $toolPackages = @(
        $lock.packages |
            Where-Object { [string]$_.id -ceq 'wix' }
    )
    if ($toolPackages.Count -ne 1) {
        throw 'The locked WiX policy must contain exactly one wix tool package.'
    }
    $toolPackage = $toolPackages[0]
    $version = [string]$toolPackage.version
    $fileName = [string]$toolPackage.fileName
    if ($version -cne [string]$lock.wixVersion -or
        -not (Test-DesktopAICompanionWindowsLeafName -Name $fileName) -or
        $fileName -cne "wix.$version.nupkg" -or
        [long]$toolPackage.size -le 0 -or
        [string]$toolPackage.sha256 -notmatch '^[0-9a-f]{64}$') {
        throw 'The locked WiX tool package metadata is invalid.'
    }

    $storeRoot = Join-Path $resolvedToolRoot (
        ".store\wix\$version\wix\$version")
    $packagePath = Join-Path $storeRoot $fileName
    $packageInput = Open-DesktopAICompanionValidatedInputFile `
        -Path $packagePath `
        -Root $resolvedToolRoot
    try {
        if ([long]$packageInput.Length -ne [long]$toolPackage.size -or
            $packageInput.ComputeHash('SHA256').ToLowerInvariant() -cne
                [string]$toolPackage.sha256) {
            throw (
                'The installed WiX package differs from the repository ' +
                'digest lock.')
        }
        $payload = Get-DesktopAICompanionWixToolPayload `
            -PackageInput $packageInput
    }
    finally {
        $packageInput.Dispose()
    }

    $payloadRoot = Join-Path $storeRoot (
        [string]$payload.SubtreeRelativePath -replace '/', '\')
    $observedFiles =
        Get-DesktopAICompanionInstalledWixPayloadInventory `
            -PayloadRoot $payloadRoot `
            -StoreRoot $storeRoot
    Assert-DesktopAICompanionInstalledWixPayloadFileSet `
        -ExpectedFiles $payload.Files `
        -ObservedFiles $observedFiles

    $inputs = New-Object 'Collections.Generic.List[object]'
    $executableInput = $null
    $resultReturned = $false
    try {
        foreach ($expected in @($payload.Files)) {
            $relativePath = [string]$expected.RelativePath
            $installedPath = Join-Path $storeRoot (
                $relativePath -replace '/', '\')
            $input = Open-DesktopAICompanionValidatedInputFile `
                -Path $installedPath `
                -Root $storeRoot
            $inputs.Add($input)
            if ([long]$input.Length -ne [long]$expected.Length -or
                $input.ComputeHash('SHA256') -cne
                    [string]$expected.Sha256) {
                throw (
                    'The installed WiX payload file differs from the ' +
                    'digest-locked package: ' +
                    "'$relativePath'.")
            }
            if ($relativePath -ceq [string]$payload.RelativePath) {
                $executableInput = $input
            }
        }
        if ($null -eq $executableInput) {
            throw (
                'The retained WiX payload inputs do not contain the ' +
                'locked executable.')
        }

        # Re-enumerate after every expected file has been pinned. This closes
        # the validation window for an injected file that appeared while the
        # retained input set was being opened.
        $observedFiles =
            Get-DesktopAICompanionInstalledWixPayloadInventory `
                -PayloadRoot $payloadRoot `
                -StoreRoot $storeRoot
        Assert-DesktopAICompanionInstalledWixPayloadFileSet `
            -ExpectedFiles $payload.Files `
            -ObservedFiles $observedFiles

        $executablePath = Join-Path $storeRoot (
            [string]$payload.RelativePath -replace '/', '\')
        $result = [pscustomobject][ordered]@{
            Path = $executablePath
            Version = $version
            Input = $executableInput
            Inputs = [object[]]$inputs.ToArray()
        }
        $resultReturned = $true
        return $result
    }
    finally {
        if (-not $resultReturned) {
            for ($index = $inputs.Count - 1;
                $index -ge 0;
                $index--) {
                $inputs[$index].Dispose()
            }
        }
    }
}

function Open-DesktopAICompanionLockedWixExtension {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$LockPath,
        [Parameter(Mandatory = $true)][string]$ExtensionRoot,
        # Which locked extension to verify. Was hardcoded to the UI extension until the Util extension
        # was added for util:CloseApplication; naming it keeps one verification path for both rather
        # than a copy that can drift.
        [Parameter(Mandatory = $true)][string]$ExtensionId
    )

    foreach ($requiredCommand in @(
            'Open-DesktopAICompanionValidatedInputFile',
            'Test-DesktopAICompanionWindowsLeafName',
            'Assert-DesktopAICompanionPathChainSafe')) {
        if (-not (Get-Command $requiredCommand -ErrorAction SilentlyContinue)) {
            throw (
                'WixToolchainPolicy requires StagingPathSafety.ps1 to be ' +
                "dot-sourced first; missing command '$requiredCommand'.")
        }
    }

    $resolvedLock = [IO.Path]::GetFullPath($LockPath)
    $resolvedExtensionRoot = [IO.Path]::GetFullPath($ExtensionRoot)
    if (-not (Test-Path `
            -LiteralPath $resolvedExtensionRoot `
            -PathType Container)) {
        throw (
            'Locked WiX extension root is missing: ' +
            $resolvedExtensionRoot)
    }

    $lockInput = Open-DesktopAICompanionValidatedInputFile `
        -Path $resolvedLock `
        -Root (Split-Path -Parent $resolvedLock)
    try {
        $lock = $lockInput.ReadAllTextUtf8(1MB) | ConvertFrom-Json
    }
    finally {
        $lockInput.Dispose()
    }
    if ([int]$lock.schemaVersion -ne 1 -or
        [string]$lock.wixVersion -cne '5.0.2') {
        throw 'The locked WiX policy must pin schema 1 and WiX 5.0.2.'
    }

    $extensionPackages = @(
        $lock.packages |
            Where-Object {
                [string]$_.id -ceq $ExtensionId
            }
    )
    if ($extensionPackages.Count -ne 1) {
        throw (
            'The locked WiX policy must contain exactly one ' +
            "$ExtensionId package.")
    }
    $extensionPackage = $extensionPackages[0]
    $version = [string]$extensionPackage.version
    $payload = $extensionPackage.installedPayload
    $relativePath = [string]$payload.relativePath
    $expectedLength = [long]$payload.length
    $expectedSha256 = [string]$payload.sha256
    if ($version -cne [string]$lock.wixVersion -or
        $relativePath -cne ('wixext5/{0}.dll' -f $ExtensionId) -or
        $expectedLength -le 0 -or
        $expectedSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "The locked $ExtensionId payload metadata is invalid."
    }

    $versionRoot = Join-Path $resolvedExtensionRoot (
        '{0}\{1}' -f
        [string]$extensionPackage.id,
        $version)
    $payloadPath = Join-Path $versionRoot (
        $relativePath -replace '/', '\')
    $expectedFiles = @(
        [pscustomobject][ordered]@{
            RelativePath = $relativePath
            Length = $expectedLength
            Sha256 = $expectedSha256.ToUpperInvariant()
        }
    )
    $observedFiles =
        Get-DesktopAICompanionInstalledWixPayloadInventory `
            -PayloadRoot $versionRoot `
            -StoreRoot $versionRoot
    Assert-DesktopAICompanionInstalledWixPayloadFileSet `
        -ExpectedFiles $expectedFiles `
        -ObservedFiles $observedFiles

    $extensionInput = $null
    $resultReturned = $false
    try {
        $extensionInput = Open-DesktopAICompanionValidatedInputFile `
            -Path $payloadPath `
            -Root $versionRoot
        if ([long]$extensionInput.Length -ne $expectedLength -or
            $extensionInput.ComputeHash('SHA256') -cne
                $expectedSha256.ToUpperInvariant()) {
            throw (
                "The installed $ExtensionId differs from the exact " +
                'payload digest lock.')
        }

        $observedFiles =
            Get-DesktopAICompanionInstalledWixPayloadInventory `
                -PayloadRoot $versionRoot `
                -StoreRoot $versionRoot
        Assert-DesktopAICompanionInstalledWixPayloadFileSet `
            -ExpectedFiles $expectedFiles `
            -ObservedFiles $observedFiles

        $result = [pscustomobject][ordered]@{
            Path = [IO.Path]::GetFullPath($payloadPath)
            Version = $version
            Input = $extensionInput
            Inputs = [object[]]@($extensionInput)
        }
        $resultReturned = $true
        return $result
    }
    finally {
        if (-not $resultReturned -and
            $null -ne $extensionInput) {
            $extensionInput.Dispose()
        }
    }
}
