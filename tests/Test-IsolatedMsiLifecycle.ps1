#requires -Version 5
<#
.SYNOPSIS
    Build, or explicitly apply, production MSI authoring with an isolated native synthetic payload.
.DESCRIPTION
    Default mode only builds and reads MSI tables. It never installs or invokes a custom action.
    Apply requires a previously built fixture and fresh public-image safety evidence reviewed by
    the operator. The evidence is safety data, not an approval marker. This tests production WiX
    wiring and the native shutdown DLL, not the full .NET host's lifecycle qualification.
.EXAMPLE
    pwsh -NoProfile -File tests/Test-IsolatedMsiLifecycle.ps1 -IncludeLegacyFixture
.EXAMPLE
    pwsh -NoProfile -File tests/Test-IsolatedMsiLifecycle.ps1 -Apply -FixtureDirectory <printed-root> -PublicImagePreflightPath <build/preflight.json>
.EXAMPLE
    pwsh -NoProfile -File tests/Test-IsolatedMsiLifecycle.ps1 -Apply -ObserveMaintenanceLog -FixtureDirectory <never-applied-root> -PublicImagePreflightPath <fresh-build/preflight.json>
#>
[CmdletBinding()]
param(
    [string]$FixtureDirectory = '',
    [switch]$IncludeLegacyFixture,
    [switch]$Apply,
    [switch]$ObserveMaintenanceLog,
    [string]$CheckPathOnly = '',
    [string]$PublicImagePreflightPath = '',
    [string]$WixToolRoot = '',
    [string]$WixExtensionRoot = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($ObserveMaintenanceLog -and -not $Apply) { throw 'Maintenance observation requires an existing prebuilt fixture and explicit Apply.' }
$repo = Split-Path $PSScriptRoot -Parent
$buildRoot = [IO.Path]::GetFullPath((Join-Path $repo 'build'))
$fixtureParent = Join-Path $buildRoot 'isolated-msi'
$encoding = New-Object Text.UTF8Encoding($false)
. (Join-Path $repo 'packaging\StagingPathSafety.ps1')
. (Join-Path $repo 'packaging\WixToolchainPolicy.ps1')
. (Join-Path $PSScriptRoot 'InstallerShutdown.Tests\MsiCallerSafety.ps1')
. (Join-Path $repo 'installer\ScopedShutdown\PathSafety.ps1')
. (Join-Path $PSScriptRoot 'InstallerShutdown.Tests\MsiLogObservation.ps1')

function Assert-PlainPath {
    param([string]$Path)
    # Classify the complete textual path before any filesystem operation. A
    # protected component also protects every descendant beneath that component.
    $full = [IO.Path]::GetFullPath($Path)
    foreach ($part in ($full -split '[\\/]')) {
        if ($part -match '(?i)(\.env$|^\.env\.|\.pem$|\.key$|\.p12$|\.pfx$|\.keystore$|^privatekey|^\.npmrc$|^\.aws$|^\.ssh$|^\.kube$|^\.config$|^\.mozilla$|^\.git$|^credentials?$|^User Data$|^Firefox$|^Keychains$)') {
            throw 'fixture_protected_scope_refused'
        }
    }
    if (-not $full.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $full.IndexOf(':', 2) -ge 0 -or [IO.Path]::GetPathRoot($full) -notmatch '^[A-Za-z]:\\$') {
        throw 'fixture_path_outside_checkout'
    }
    $drive = [IO.Path]::GetPathRoot($full)
    $current = $drive
    foreach ($part in $full.Substring($drive.Length).Split('\')) {
        if ([string]::IsNullOrEmpty($part)) { continue }
        $current = Join-Path $current $part
        try { $attributes = [IO.File]::GetAttributes($current) }
        catch {
            $cause = $_.Exception
            while ($null -ne $cause.InnerException) { $cause = $cause.InnerException }
            if ($cause -is [IO.FileNotFoundException] -or $cause -is [IO.DirectoryNotFoundException]) {
                break # Remaining components do not exist yet and cannot contain a link.
            }
            throw 'fixture_path_metadata_unavailable'
        }
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'fixture_reparse_refused'
        }
    }
    return $full
}
if (-not [string]::IsNullOrWhiteSpace($CheckPathOnly)) {
    $checked = [IO.Path]::GetFullPath($CheckPathOnly)
    if (-not $checked.StartsWith($buildRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Path-only checks must remain below this checkout build directory.'
    }
    [void](Assert-PlainPath $checked)
    Write-Host 'PATH_GUARD_PASS'
    return
}

function Assert-Check {
    param([bool]$Condition, [string]$Description)
    if (-not $Condition) { throw $Description }
    Write-Host ("PASS $Description")
}
function Write-Utf8 {
    param([string]$Path, [string]$Text)
    [void](Assert-PlainPath $Path)
    [IO.File]::WriteAllText($Path, $Text, $encoding)
}
function Read-Utf8 {
    param([string]$Path)
    [void](Assert-PlainPath $Path)
    return [IO.File]::ReadAllText($Path)
}
function Hash-File {
    param([string]$Path)
    [void](Assert-PlainPath $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}
function New-PlainDirectory {
    param([string]$Path)
    [void](Assert-PlainPath $Path)
    [void][IO.Directory]::CreateDirectory($Path)
}
function Copy-PlainFile {
    param([string]$Source, [string]$Destination)
    [void](Assert-PlainPath $Source)
    [void](Assert-PlainPath $Destination)
    [IO.File]::Copy($Source, $Destination, $false)
}
function Release-Com {
    param($Value)
    if ($null -ne $Value -and [Runtime.InteropServices.Marshal]::IsComObject($Value)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($Value)
    }
}
function Msi-Rows {
    param([string]$Path, [string]$Query, [int]$Columns = 1)
    [void](Assert-PlainPath $Path)
    $installer = $null; $database = $null; $view = $null; $record = $null
    $rows = New-Object 'Collections.Generic.List[object]'
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($Path, 0)
        $view = $database.OpenView($Query)
        [void]$view.Execute()
        while ($true) {
            $record = $view.Fetch()
            if ($null -eq $record) { break }
            $values = @()
            for ($index = 1; $index -le $Columns; $index++) { $values += [string]$record.StringData($index) }
            $rows.Add($values)
            Release-Com $record; $record = $null
        }
        return ,$rows.ToArray()
    } finally {
        foreach ($value in @($record, $view, $database, $installer)) { Release-Com $value }
    }
}
function Msi-Property {
    param([string]$Path, [string]$Name)
    if ($Name -notmatch '^[A-Za-z0-9_]+$') { throw 'Invalid fixture metadata property.' }
    $rows = Msi-Rows -Path $Path -Query ("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='" + $Name + "'")
    if ($rows.Count -ne 1) { throw "Fixture property missing: $Name" }
    return [string]$rows[0][0]
}
function Assert-NativeBinary {
    param([string]$Msi, [string]$ExpectedHash)
    [void](Assert-PlainPath $Msi)
    $installer = $null; $database = $null; $view = $null; $record = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($Msi, 0)
        Assert-Check ([int]$database.TablePersistent('Wix4CloseApplication') -eq 2) (
            'native cached-package classification sees absent broad-close table as MSICONDITION_NONE')
        $view = $database.OpenView("SELECT ``Data`` FROM ``Binary`` WHERE ``Name``='ScopedShutdownCA'")
        [void]$view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) { throw 'Fixture native Binary row missing.' }
        $length = [int]$record.DataSize(1)
        if ($length -le 0 -or $length -gt 16MB) { throw 'Fixture native Binary size is invalid.' }
        $text = [string]$record.ReadStream(1, $length, 1)
        if ($text.Length -ne $length) { throw 'Fixture native Binary stream is incomplete.' }
        $bytes = New-Object byte[] $length
        for ($index = 0; $index -lt $length; $index++) {
            $value = [int][char]$text[$index]
            if ($value -gt 255) { throw 'Fixture native Binary stream encoding is invalid.' }
            $bytes[$index] = [byte]$value
        }
        $hash = [Security.Cryptography.SHA256]::Create()
        try { $actual = [BitConverter]::ToString($hash.ComputeHash($bytes)).Replace('-', '') }
        finally { $hash.Dispose() }
        Assert-Check ($actual -eq $ExpectedHash) 'built MSI embeds the exact staged production native DLL'
    } finally {
        foreach ($value in @($record, $view, $database, $installer)) { Release-Com $value }
    }
}
function Fixture-Path {
    param([string]$Relative)
    $path = [IO.Path]::GetFullPath((Join-Path $fixtureRoot $Relative))
    if (-not $path.StartsWith($fixtureRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Fixture path escaped its owned root.'
    }
    [void](Assert-PlainPath $path)
    return $path
}
function Fixture-Defaults {
    param($Family)
    return @{
        INSTALLFOLDER = (Fixture-Path ($Family.Name + '\installed'))
        DESKTOP_AI_COMPANION_TEST_DESKTOPFOLDER = (Fixture-Path ($Family.Name + '\desktop'))
        DESKTOP_AI_COMPANION_TEST_STARTMENUFOLDER = (Fixture-Path ($Family.Name + '\startmenu'))
    }
}
function Assert-FixtureDefaults {
    param([string]$Msi, $Family)
    $defaults = Fixture-Defaults $Family
    foreach ($name in $defaults.Keys) {
        Assert-Check ((Msi-Property $Msi $name) -ceq $defaults[$name]) ("cached MSI default $name stays inside its owned fixture")
    }
}
function Build-Family {
    param([string]$Name, [bool]$Legacy)
    $familyRoot = Fixture-Path $Name
    New-PlainDirectory $familyRoot
    $family = [ordered]@{
        Name = $Name
        UpgradeCode = [Guid]::NewGuid().ToString('B').ToUpperInvariant()
        ProductName = 'Desktop AI Companion Scoped Shutdown Test ' + $tag + ' ' + $Name
        RegistryRoot = 'Software\bigfnj\DACScopedShutdownTest\' + $tag + '\' + $Name
        InstallFolderStateComponentGuid = [Guid]::NewGuid().ToString('D').ToUpperInvariant()
        StartMenuFolderStateComponentGuid = [Guid]::NewGuid().ToString('D').ToUpperInvariant()
        Packages = @()
    }
    foreach ($version in @(1, 2)) {
        $versionRoot = Join-Path $familyRoot ('v' + $version)
        $runtime = Join-Path $versionRoot 'runtime'
        New-PlainDirectory $runtime
        Copy-PlainFile $stagedFixture (Join-Path $runtime 'DesktopAICompanion.exe')
        Write-Utf8 -Path (Join-Path $runtime 'generation.txt') -Text ([string]$version)
        $manifestPath = Join-Path $versionRoot 'runtime-files.txt'
        Write-Utf8 -Path $manifestPath -Text "DesktopAICompanion.exe`ngeneration.txt`n"
        $fragment = Join-Path $versionRoot 'RuntimeFiles.generated.wxs'
        & (Join-Path $repo 'installer\New-RuntimeWixFragment.ps1') -ManifestPath $manifestPath `
            -OutputPath $fragment -ComponentNamespace ('ScopedShutdownTest-' + $tag + '-' + $Name) | Out-Host
        $document = New-Object Xml.XmlDocument
        $document.PreserveWhitespace = $true
        $document.XmlResolver = $null
        $document.LoadXml($productionSource)
        $namespaces = New-Object Xml.XmlNamespaceManager($document.NameTable)
        $namespaces.AddNamespace('w', 'http://wixtoolset.org/schemas/v4/wxs')
        $package = $document.SelectSingleNode('/w:Wix/w:Package', $namespaces)
        if ($null -eq $package) { throw 'Production Package element missing.' }
        # /f repair uses cached MSI defaults rather than accepting every CLI
        # property override. Bake the same isolated paths into this COPY only.
        $defaults = Fixture-Defaults $family
        foreach ($propertyName in $defaults.Keys) {
            $property = $document.SelectSingleNode('//w:Property[@Id="' + $propertyName + '"]', $namespaces)
            if ($null -eq $property) { throw 'Production isolation property missing.' }
            $property.SetAttribute('Value', $defaults[$propertyName])
        }
        if ($version -eq 2) {
            $failure = $document.CreateElement('CustomActionRef', $package.NamespaceURI)
            $failure.SetAttribute('Id', 'Wix4FailWhenDeferred_X64')
            [void]$package.AppendChild($failure)
        }
        $isLegacy = $Legacy -and $version -eq 1
        if ($isLegacy) {
            $capability = $document.SelectSingleNode('//w:Property[@Id="DESKTOP_AI_COMPANION_SCOPED_SHUTDOWN_CAPABILITY"]', $namespaces)
            $close = $document.SelectSingleNode('//w:InstallExecuteSequence/w:Custom[@Action="CloseInstalledCompanion"]', $namespaces)
            if ($null -eq $capability -or $null -eq $close) { throw 'Production shutdown metadata missing.' }
            $capability.SetAttribute('Value', '0')
            [void]$close.ParentNode.RemoveChild($close)
        }
        $source = Join-Path $versionRoot 'ProductionAuthoring.fixture.wxs'
        $document.Save($source)
        $msi = Join-Path $versionRoot 'fixture.msi'
        $arguments = @('build', $source, $fragment, '-ext', $extensionPaths[0], '-ext', $extensionPaths[1],
            '-arch', 'x64', '-bindpath', $runtime, '-bindpath', (Join-Path $repo 'installer'),
            '-d', ('ProductName=' + $family.ProductName), '-d', 'Manufacturer=Desktop AI Companion Test',
            '-d', ('ProductVersion=0.0.' + $version), '-d', 'RepositoryUrl=https://github.com/bigfnj/desktop-ai-companion',
            '-d', 'ProductDescription=Isolated synthetic native shutdown lifecycle fixture',
            '-d', ('UpgradeCode=' + $family.UpgradeCode), '-d', ('RegistryRoot=' + $family.RegistryRoot),
            '-d', ('ScopedShutdownBinary=' + $stagedDll),
            '-d', ('InstallFolderStateComponentGuid=' + $family.InstallFolderStateComponentGuid),
            '-d', ('StartMenuFolderStateComponentGuid=' + $family.StartMenuFolderStateComponentGuid),
            '-pdbtype', 'none', '-o', $msi)
        & $wix @arguments | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Fixture WiX build failed: $Name v$version" }
        if (-not $isLegacy) { & (Join-Path $repo 'packaging\Test-MsiSurface.ps1') -MsiPath $msi | Out-Host }
        & (Join-Path $repo 'packaging\Test-MsiUpgradeSchedule.ps1') -MsiPath $msi | Out-Host
        Assert-NativeBinary -Msi $msi -ExpectedHash $nativeHash
        Assert-FixtureDefaults -Msi $msi -Family $family
        if ($isLegacy) {
            $rows = Msi-Rows -Path $msi -Query "SELECT ``Action`` FROM ``InstallExecuteSequence`` WHERE ``Action``='CloseInstalledCompanion'"
            Assert-Check ($rows.Count -eq 0) 'safe capability-zero fixture has no shutdown action scheduled'
        }
        $family.Packages += [pscustomobject]@{
            Version = $version; ProductCode = (Msi-Property $msi 'ProductCode')
            MsiSha256 = (Hash-File $msi); Capability = $(if ($isLegacy) { '0' } else { '1' })
        }
    }
    return [pscustomobject]$family
}

if ([string]::IsNullOrWhiteSpace($FixtureDirectory)) {
    if ($Apply) { throw 'Apply requires the FixtureDirectory printed by a prior BuildOnly run.' }
    $FixtureDirectory = Join-Path $fixtureParent ([Guid]::NewGuid().ToString('N'))
}
$fixtureRoot = [IO.Path]::GetFullPath($FixtureDirectory).TrimEnd('\')
if ((Split-Path $fixtureRoot -Parent) -ne $fixtureParent -or (Split-Path $fixtureRoot -Leaf) -notmatch '^[a-f0-9]{32}$') {
    throw 'FixtureDirectory must be a GUID-owned immediate child of this checkout build/isolated-msi.'
}
$tag = Split-Path $fixtureRoot -Leaf
[void](Assert-PlainPath $fixtureRoot)
[void](Assert-DesktopAICompanionPathChainSafe -Path $fixtureRoot -TrustedRoot $repo)
$manifestFile = Join-Path $fixtureRoot 'fixture.json'
$productionWxs = Join-Path $repo 'installer\DesktopAICompanion.wxs'
$nativeDll = Join-Path $buildRoot 'scoped-shutdown\ScopedShutdown.dll'
$nativeFixture = Join-Path $buildRoot 'scoped-shutdown\SyntheticCompanion.exe'
$stagedDll = Join-Path $fixtureRoot 'ScopedShutdown.dll'
$stagedFixture = Join-Path $fixtureRoot 'SyntheticCompanion.exe'
foreach ($path in @($manifestFile, $productionWxs, $nativeDll, $nativeFixture, $stagedDll, $stagedFixture)) {
    [void](Assert-PlainPath $path)
}

if (-not $Apply) {
    if ([IO.Directory]::Exists($fixtureRoot)) { throw 'BuildOnly requires a new fixture directory.' }
    foreach ($path in @($productionWxs, $nativeDll, $nativeFixture)) {
        if (-not [IO.File]::Exists($path)) { throw "Build input missing: $path" }
    }
    New-PlainDirectory $fixtureRoot
    $productionHash = Hash-File $productionWxs
    $productionSource = Read-Utf8 $productionWxs
    Copy-PlainFile $nativeDll $stagedDll
    Copy-PlainFile $nativeFixture $stagedFixture
    $nativeHash = Hash-File $stagedDll
    if ([string]::IsNullOrWhiteSpace($WixToolRoot)) { $WixToolRoot = Get-DesktopAICompanionDotnetGlobalToolRoot }
    if ([string]::IsNullOrWhiteSpace($WixExtensionRoot)) { $WixExtensionRoot = Get-DesktopAICompanionWixGlobalExtensionRoot }
    $lock = Join-Path $repo 'packaging\wix-toolchain-lock.json'
    $tool = Open-DesktopAICompanionLockedWixExecutable -LockPath $lock -ToolRoot $WixToolRoot
    $wix = [string]$tool.Path
    $extensionPaths = @()
    foreach ($id in @('WixToolset.UI.wixext', 'WixToolset.Util.wixext')) {
        $extension = Open-DesktopAICompanionLockedWixExtension -LockPath $lock -ExtensionRoot $WixExtensionRoot -ExtensionId $id
        $extensionPaths += [string]$extension.Path
    }
    $families = @((Build-Family -Name 'main' -Legacy $false))
    if ($IncludeLegacyFixture) { $families += Build-Family -Name 'legacy' -Legacy $true }
    Assert-Check ((Hash-File $productionWxs) -eq $productionHash -and (Hash-File $nativeDll) -eq $nativeHash) (
        'production authoring and native DLL remained unchanged while fixtures were built')
    $manifest = [ordered]@{
        SchemaVersion = 1; Tag = $tag; ProductionWxsSha256 = $productionHash; NativeDllSha256 = $nativeHash
        SyntheticCompanionSha256 = (Hash-File $stagedFixture); Families = $families
        Scope = 'Production WiX authoring/native DLL with synthetic payload; not full production host qualification.'
        MsiExecuted = $false
    }
    Write-Utf8 -Path $manifestFile -Text ($manifest | ConvertTo-Json -Depth 6)
    Write-Host 'BuildOnly PASS. No MSI installation or custom action was executed.'
    [pscustomobject]@{ Result = 'BUILD_ONLY_PASS'; FixtureDirectory = $fixtureRoot; Scope = $manifest.Scope }
    return
}

if (-not [IO.File]::Exists($manifestFile)) { throw 'Prebuilt fixture manifest missing.' }
$manifest = Read-Utf8 $manifestFile | ConvertFrom-Json
Assert-Check ($manifest.SchemaVersion -eq 1 -and $manifest.Tag -ceq $tag) 'prebuilt fixture identity matches its GUID-owned root'
Assert-Check ((Hash-File $productionWxs) -eq $manifest.ProductionWxsSha256 -and
    (Hash-File $nativeDll) -eq $manifest.NativeDllSha256 -and (Hash-File $stagedDll) -eq $manifest.NativeDllSha256 -and
    (Hash-File $stagedFixture) -eq $manifest.SyntheticCompanionSha256) 'prebuilt fixture still matches current production inputs'
foreach ($family in $manifest.Families) {
    if ($family.Name -notin @('main', 'legacy') -or $family.UpgradeCode -notmatch '^\{[A-F0-9-]{36}\}$' -or
        $family.UpgradeCode -in @('{327AF68D-A714-5C9B-B2D9-9C3404E03D38}', '{DBF8DDB3-C4AB-498C-9E55-4193A734C573}')) {
        throw 'Fixture family cannot target a production upgrade identity.'
    }
    $expectedName = 'Desktop AI Companion Scoped Shutdown Test ' + $tag + ' ' + $family.Name
    $expectedRegistry = 'Software\bigfnj\DACScopedShutdownTest\' + $tag + '\' + $family.Name
    Assert-Check ($family.ProductName -ceq $expectedName -and $family.RegistryRoot -ceq $expectedRegistry) 'fixture product and registry identities are isolated'
    foreach ($package in $family.Packages) {
        if ($package.Version -notin @(1, 2)) { throw 'Fixture version refused.' }
        $msi = Fixture-Path ($family.Name + '\v' + $package.Version + '\fixture.msi')
        Assert-Check ((Hash-File $msi) -eq $package.MsiSha256 -and (Msi-Property $msi 'ProductCode') -eq $package.ProductCode -and
            (Msi-Property $msi 'UpgradeCode') -eq $family.UpgradeCode -and (Msi-Property $msi 'ProductName') -ceq $expectedName -and
            (Msi-Property $msi 'DESKTOP_AI_COMPANION_REGISTRYROOT') -ceq $expectedRegistry) 'prebuilt MSI metadata and bytes remain bound to their fixture family'
        Assert-NativeBinary -Msi $msi -ExpectedHash $manifest.NativeDllSha256
        Assert-FixtureDefaults -Msi $msi -Family $family
    }
}
if ($ObserveMaintenanceLog) {
    if ([IO.File]::Exists((Fixture-Path 'lifecycle-result.json'))) { throw 'Observation requires a never-applied fixture; prior lifecycle evidence is preserved.' }
    foreach ($family in $manifest.Families) {
        foreach ($name in @('install-v1','rollback-v2','upgrade','reinstall','repair','uninstall','legacy-refusal','legacy-cleanup','cleanup-v1','cleanup-v2')) {
            foreach ($suffix in @('.log','.log.caller-command.txt','.log.caller.out.txt','.log.caller.err.txt')) {
                if ([IO.File]::Exists((Fixture-Path ($family.Name + '\' + $name + $suffix)))) { throw 'Observation refuses to overwrite a prior lifecycle log or caller sidecar.' }
            }
        }
    }
}
if ([string]::IsNullOrWhiteSpace($PublicImagePreflightPath)) { throw 'Apply requires separately reviewed public-image safety evidence.' }
$preflight = [IO.Path]::GetFullPath($PublicImagePreflightPath)
if (-not $preflight.StartsWith($buildRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetExtension($preflight) -ine '.json' -or
    $preflight -match '(?i)(^|[\\/])(?:privatekey[^\\/]*|\.env(?:\.[^\\/]*)?|\.npmrc|[^\\/]+\.(?:pem|key|p12|pfx|keystore))([\\/]|$)') {
    throw 'Safety evidence must be an ordinary JSON file below this checkout build directory.'
}
[void](Assert-PlainPath $preflight)
$evidence = Read-Utf8 $preflight | ConvertFrom-Json
$timestamp = $evidence.ObservedAtUtc
if ($timestamp -is [DateTimeOffset]) { $observed = $timestamp.UtcDateTime }
elseif ($timestamp -is [DateTime]) { $observed = $timestamp.ToUniversalTime() }
else { $observed = [DateTime]::Parse([string]$timestamp, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime() }
$age = ([DateTime]::UtcNow - $observed).TotalSeconds
Assert-Check ($evidence.ScopedShutdownSafe -eq $true -and $age -ge -15 -and $age -le 120) 'public-image safety evidence is positive and no older than two minutes'

$children = New-Object 'Collections.Generic.List[Diagnostics.Process]'
$operations = New-Object 'Collections.Generic.List[object]'
$primary = $null
$script:installerState = New-ScopedMsiCallerState
$script:unprovenChecks = New-Object 'Collections.Generic.List[string]'
function Product-State {
    param([string]$Code)
    $installer = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        return [int]$installer.ProductState($Code)
    } finally { Release-Com $installer }
}
function Common-Properties {
    param($Family)
    $defaults = Fixture-Defaults $Family
    return @(('INSTALLFOLDER=' + $defaults.INSTALLFOLDER),
        ('DESKTOP_AI_COMPANION_TEST_DESKTOPFOLDER=' + $defaults.DESKTOP_AI_COMPANION_TEST_DESKTOPFOLDER),
        ('DESKTOP_AI_COMPANION_TEST_STARTMENUFOLDER=' + $defaults.DESKTOP_AI_COMPANION_TEST_STARTMENUFOLDER),
        ('DESKTOP_AI_COMPANION_REGISTRYROOT=' + $Family.RegistryRoot),
        'WIXUI_EXITDIALOGOPTIONALCHECKBOX=', 'CLEANINSTALL=')
}
function Invoke-OwnedMsi {
    param([string]$Name, [string[]]$Action, $Family, [int]$ExpectedExit = 0, [switch]$ShutdownReceipt, [switch]$LegacyRefusal, [switch]$ObserveLog)
    Assert-ScopedMsiMayStart $script:installerState
    if ($ObserveLog -and ($Name -cne 'repair' -or $Family.Name -cne 'main')) { throw 'Log observation is confined to the main fixture repair.' }
    $log = Fixture-Path ($Family.Name + '\' + $Name + '.log')
    $arguments = @('/qn', '/norestart', '/l*v', $log) + @($Action) + @(Common-Properties $Family)
    $quoted = @($arguments | ForEach-Object {
        if ($_ -match '["\r\n]') { throw 'Fixture MSI argument contains an invalid character.' }
        $separator = $_.IndexOf('=')
        if ($_ -cin @('/i','/x','/famus','/qn','/norestart','/l*v')) { $_ }
        elseif ($_.StartsWith('/')) { throw 'Fixture MSI switch is not explicitly allowed.' }
        elseif ($separator -gt 0) {
            $propertyName = $_.Substring(0, $separator)
            if ($propertyName -notmatch '^[A-Z][A-Z0-9_]*$') { throw 'Fixture MSI property name is invalid.' }
            $propertyName + '="' + $_.Substring($separator + 1) + '"'
        } else { '"' + $_ + '"' }
    })
    $backend = 'msiexec'
    $caller = Join-Path ([Environment]::SystemDirectory) 'msiexec.exe'
    $callerArguments = $quoted -join ' '
    Write-Utf8 ($log + '.caller-command.txt') ($caller + ' ' + $callerArguments)
    $startArguments = @{ FilePath=$caller; ArgumentList=$callerArguments; WindowStyle='Hidden'; PassThru=$true }
    $startArguments.RedirectStandardOutput = $log + '.caller.out.txt'
    $startArguments.RedirectStandardError = $log + '.caller.err.txt'
    try {
        if ($ObserveLog) {
            $code = Invoke-ScopedMsiLogObservation -Directory (Fixture-Path 'main') -Repo $repo -Caller $caller -Arguments $callerArguments -State $script:installerState -StartCaller { Start-Process @startArguments }
        } else {
            $code = Invoke-ScopedMsiCaller -State $script:installerState -StartCaller { Start-Process @startArguments }
        }
    }
    catch {
        if ($script:installerState.Uncertain) {
            $result = if ($script:installerState.Reason -eq 'timeout') { 'TIMEOUT_UNCERTAIN' } else { 'CALLER_STATE_UNCERTAIN' }
            $operations.Add([pscustomobject]@{
                Name = $Name; Result = $result; Reason = $script:installerState.Reason
                CallerPid = $script:installerState.CallerPid
                CallerCreationFileTime = $script:installerState.CallerCreationFileTime
            })
        }
        throw
    }
    $operations.Add([pscustomobject]@{ Name = $Name; Backend = $backend; ExitCode = $code; ExpectedExit = $ExpectedExit })
    Assert-Check ($code -eq $ExpectedExit) ("owned MSI $Name returns its expected exit code")
    $text = Read-Utf8 $log
    if ($ShutdownReceipt) {
        Assert-Check ($text -match 'Installation-scoped companion shutdown: targets=1, graceful=1, terminated=0') (
            "owned MSI $Name records the exact installed synthetic target shutdown")
        Assert-Check ($text -notmatch 'legacy transition requires exclusive') ("owned MSI $Name recognizes scoped cached capability")
    }
    if ($LegacyRefusal) { Assert-Check ($text -match 'legacy_foreign_companion_present') 'actual cached capability-zero fixture triggers production legacy refusal' }
}
function Start-OwnedFixture {
    param([string]$Image, [string]$Name)
    [void](Assert-PlainPath $Image)
    $ready = Fixture-Path ($Name + '-' + [Guid]::NewGuid().ToString('N') + '.txt')
    $child = Start-Process -FilePath $Image -ArgumentList ('--ready "' + $ready + '"') -WindowStyle Hidden -PassThru
    $children.Add($child); [void]$child.SafeHandle
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not [IO.File]::Exists($ready)) {
        if ($child.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Owned lifecycle child readiness failed.' }
        Start-Sleep -Milliseconds 25
    }
    return $child
}
function Assert-Sentinels {
    param([string]$Generation, [string]$Installed, [string]$Data)
    Assert-Check ((Read-Utf8 (Join-Path $Installed 'generation.txt')) -ceq $Generation) 'installed generation matches the completed or rolled-back transaction'
    Assert-Check ((Read-Utf8 (Join-Path $Installed 'modules\settings.txt')) -ceq 'module-settings-preserved') 'unmanaged installed module settings survive maintenance'
    Assert-Check ((Read-Utf8 (Join-Path $Data 'settings.txt')) -ceq 'data-settings-preserved') 'separate synthetic data settings survive maintenance'
}
function Assert-NoDefaultFixtureArtifacts {
    param($Family)
    $expectedName = 'Desktop AI Companion Scoped Shutdown Test ' + $tag + ' ' + $Family.Name
    if ($Family.Name -notin @('main', 'legacy') -or $Family.ProductName -cne $expectedName) { throw 'Default-path check requires an exact synthetic fixture identity.' }
    # Metadata-only checks of these exact GUID-named candidates. Never enumerate
    # real user folders or inspect file contents outside the owned fixture.
    $defaultInstall = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Programs\' + $expectedName)
    $defaultMenu = Join-Path ([Environment]::GetFolderPath('Programs')) $expectedName
    $paths = @(
        @{ Path = $defaultInstall; Shortcut = $false; Label = 'appdata-directory' },
        @{ Path = (Join-Path $defaultInstall 'DesktopAICompanion.exe'); Shortcut = $false; Label = 'appdata-image' },
        @{ Path = (Join-Path $defaultInstall 'generation.txt'); Shortcut = $false; Label = 'appdata-generation' },
        @{ Path = (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) ($expectedName + '.lnk')); Shortcut = $true; Label = 'desktop-shortcut' },
        @{ Path = $defaultMenu; Shortcut = $true; Label = 'startmenu-directory' },
        @{ Path = (Join-Path $defaultMenu ($expectedName + '.lnk')); Shortcut = $true; Label = 'startmenu-shortcut' })
    foreach ($candidate in $paths) {
        $full = [IO.Path]::GetFullPath($candidate.Path)
        foreach ($part in ($full -split '[\\/]')) {
            if ($part -match '(?i)(\.env$|^\.env\.|\.pem$|\.key$|\.p12$|\.pfx$|\.keystore$|^privatekey|^\.npmrc$|^\.aws$|^\.ssh$|^\.kube$|^\.config$|^\.mozilla$|^\.git$|^credentials?$|^User Data$|^Firefox$|^Keychains$)') { throw 'fixture_protected_scope_refused' }
        }
        $current = [IO.Path]::GetPathRoot($full)
        if ($current -notmatch '^[A-Za-z]:\\$') { throw 'fixture_default_path_not_local' }
        $reparseRefused = $false
        foreach ($part in $full.Substring($current.Length).Split('\')) {
            $current = Join-Path $current $part
            try { $attributes = [IO.File]::GetAttributes($current) }
            catch {
                $cause = $_.Exception
                while ($null -ne $cause.InnerException) { $cause = $cause.InnerException }
                if ($cause -is [IO.FileNotFoundException] -or $cause -is [IO.DirectoryNotFoundException]) { break }
                throw 'fixture_default_path_metadata_unavailable'
            }
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                if (-not $candidate.Shortcut) { throw 'fixture_default_reparse_refused' }
                $reparseRefused = $true
                break # Never follow an external shortcut-folder link.
            }
        }
        if ($reparseRefused) {
            $code = $Family.Name + '-' + $candidate.Label + '-absence-unproven-reparse-refused'
            if (-not $script:unprovenChecks.Contains($code)) { $script:unprovenChecks.Add($code) }
            Write-Host ('UNPROVEN ' + $code)
            continue
        }
        Assert-Check (-not [IO.File]::Exists($full) -and -not [IO.Directory]::Exists($full)) 'exact GUID-named default user path contains no fixture artifact'
    }
}
function Assert-FixtureConfinement {
    param($Family)
    $defaults = Fixture-Defaults $Family
    Assert-Check ((Hash-File (Join-Path $defaults.INSTALLFOLDER 'DesktopAICompanion.exe')) -eq $manifest.SyntheticCompanionSha256) 'installed synthetic image remains inside its owned fixture'
    foreach ($directory in @($defaults.DESKTOP_AI_COMPANION_TEST_DESKTOPFOLDER, $defaults.DESKTOP_AI_COMPANION_TEST_STARTMENUFOLDER)) {
        $shortcut = Join-Path $directory ($Family.ProductName + '.lnk')
        [void](Assert-PlainPath $shortcut)
        Assert-Check ([IO.File]::Exists($shortcut)) 'fixture shortcut remains inside its owned directory'
    }
    Assert-NoDefaultFixtureArtifacts $Family
}
try {
    foreach ($family in $manifest.Families) {
        foreach ($package in $family.Packages) { Assert-Check ((Product-State $package.ProductCode) -eq -1) 'unique fixture product has no prior registration' }
        $one = @($family.Packages | Where-Object Version -eq 1)[0]
        $two = @($family.Packages | Where-Object Version -eq 2)[0]
        $v1 = Fixture-Path ($family.Name + '\v1\fixture.msi')
        $v2 = Fixture-Path ($family.Name + '\v2\fixture.msi')
        $installed = Fixture-Path ($family.Name + '\installed')
        $data = Fixture-Path ($family.Name + '\data')
        New-PlainDirectory $data
        Assert-NoDefaultFixtureArtifacts $family
        Invoke-OwnedMsi -Name 'install-v1' -Action @('/i', $v1) -Family $family
        Assert-Check ((Product-State $one.ProductCode) -eq 5) 'initial synthetic product is registered'
        Assert-FixtureConfinement $family
        New-PlainDirectory (Join-Path $installed 'modules')
        Write-Utf8 (Join-Path $installed 'modules\settings.txt') 'module-settings-preserved'
        Write-Utf8 (Join-Path $data 'settings.txt') 'data-settings-preserved'
        $foreignRoot = Fixture-Path ($family.Name + '\installed-other')
        New-PlainDirectory $foreignRoot
        $foreignImage = Join-Path $foreignRoot 'DesktopAICompanion.exe'
        Copy-PlainFile $stagedFixture $foreignImage
        $foreign = Start-OwnedFixture -Image $foreignImage -Name ($family.Name + '-foreign')
        $target = Start-OwnedFixture -Image (Join-Path $installed 'DesktopAICompanion.exe') -Name ($family.Name + '-target')
        if ($family.Name -eq 'legacy') {
            Invoke-OwnedMsi -Name 'legacy-refusal' -Action @('/i', $v2) -Family $family -ExpectedExit 1603 -LegacyRefusal
            Assert-Check (-not $foreign.HasExited -and -not $target.HasExited) 'legacy refusal leaves both owned synthetic children untouched'
            Assert-Sentinels -Generation '1' -Installed $installed -Data $data
            # Capability-zero has deliberately no shutdown action. Release only our
            # retained target before its cleanup, while the foreign child stays alive.
            $target.Kill()
            Assert-Check ($target.WaitForExit(5000)) 'owned capability-zero target exits before fixture cleanup'
            Invoke-OwnedMsi -Name 'legacy-cleanup' -Action @('/x', $v1) -Family $family
            Assert-Check (-not $foreign.HasExited) 'capability-zero fixture cleanup preserves the foreign child'
        } else {
            Invoke-OwnedMsi -Name 'rollback-v2' -Action @('/i', $v2, 'WIXFAILWHENDEFERRED=1') -Family $family -ExpectedExit 1603 -ShutdownReceipt
            Assert-Check ($target.WaitForExit(5000) -and -not $foreign.HasExited) 'rollback closes the installed child and preserves the foreign child'
            Assert-Check ((Product-State $one.ProductCode) -eq 5 -and (Product-State $two.ProductCode) -eq -1) 'failed upgrade restores the original product registration'
            Assert-Sentinels -Generation '1' -Installed $installed -Data $data
            foreach ($operation in @('upgrade', 'reinstall', 'repair', 'uninstall')) {
                $target = Start-OwnedFixture -Image (Join-Path $installed 'DesktopAICompanion.exe') -Name ('main-' + $operation)
                if ($operation -eq 'upgrade') { $action = @('/i', $v2) }
                elseif ($operation -eq 'reinstall') { $action = @('/i', $v2, 'REINSTALL=ALL') }
                elseif ($operation -eq 'repair') {
                    Write-Utf8 (Join-Path $installed 'generation.txt') 'corrupt'
                    $action = @('/famus', $v2)
                } else { $action = @('/x', $v2) }
                Invoke-OwnedMsi -Name $operation -Action $action -Family $family -ShutdownReceipt -ObserveLog:($ObserveMaintenanceLog -and $operation -eq 'repair')
                Assert-Check ($target.WaitForExit(5000) -and -not $foreign.HasExited) ("$operation closes its installed target and preserves the foreign child")
                if ($operation -ne 'uninstall') {
                    Assert-Sentinels -Generation '2' -Installed $installed -Data $data
                    Assert-FixtureConfinement $family
                }
                else { Assert-NoDefaultFixtureArtifacts $family }
            }
            Assert-Check (-not [IO.File]::Exists((Join-Path $installed 'DesktopAICompanion.exe')) -and
                -not [IO.File]::Exists((Join-Path $installed 'generation.txt'))) 'uninstall removes only the authored synthetic runtime files'
            Assert-Check ((Read-Utf8 (Join-Path $installed 'modules\settings.txt')) -ceq 'module-settings-preserved' -and
                (Read-Utf8 (Join-Path $data 'settings.txt')) -ceq 'data-settings-preserved') 'uninstall preserves both synthetic settings sentinels'
        }
        foreach ($package in $family.Packages) { Assert-Check ((Product-State $package.ProductCode) -eq -1) 'owned fixture product registration is removed' }
    }
} catch { $primary = $_ }
finally {
    $cleanup = New-Object 'Collections.Generic.List[string]'
    foreach ($child in $children) {
        try {
            if (-not $child.HasExited) {
                $child.Kill()
                if (-not $child.WaitForExit(5000)) { throw 'Owned lifecycle child cleanup timed out.' }
            }
        } catch { $cleanup.Add($_.Exception.Message) }
        finally { $child.Dispose() }
    }
    if (-not $script:installerState.Uncertain) {
        foreach ($family in $manifest.Families) {
            foreach ($package in $family.Packages) {
                try {
                    if (-not $script:installerState.Uncertain -and (Product-State $package.ProductCode) -ne -1) {
                        $cleanupMsi = Fixture-Path ($family.Name + '\v' + $package.Version + '\fixture.msi')
                        Invoke-OwnedMsi -Name ('cleanup-v' + $package.Version) -Action @('/x', $cleanupMsi) -Family $family
                    }
                } catch { $cleanup.Add($_.Exception.Message) }
            }
        }
    } else {
        Write-Warning 'Owned MSI caller state is uncertain. No cleanup MSI was queued; keep this fixture manifest and log for recovery.'
    }
    if ($cleanup.Count -gt 0) {
        if ($null -eq $primary) { $primary = 'Owned lifecycle cleanup failed: ' + ($cleanup -join ', ') }
        else { Write-Warning 'Owned lifecycle cleanup also failed; preserving the primary failure.' }
    }
}
$report = [ordered]@{
    Result = $(if ($null -eq $primary) { 'PASS' } else { 'FAIL' }); Scope = $manifest.Scope
    Operations = @($operations.ToArray()); InstalledApplicationTouched = $false
    LegacyFixtureIsHistoricalMsi = $false; FixtureDirectory = $fixtureRoot
    MaintenanceLogObservationEnabled = [bool]$ObserveMaintenanceLog
    InstallerTimedOut = ($script:installerState.Uncertain -and $script:installerState.Reason -eq 'timeout')
    InstallerTimedOutPid = $(if ($script:installerState.Reason -eq 'timeout') { $script:installerState.CallerPid } else { 0 })
    InstallerStateUncertain = $script:installerState.Uncertain
    InstallerUncertaintyReason = $script:installerState.Reason
    InstallerCallerPid = $script:installerState.CallerPid
    InstallerCallerCreationFileTime = $script:installerState.CallerCreationFileTime
    RecoveryManifest = $(if ($script:installerState.Uncertain) { $manifestFile } else { $null })
    UnprovenChecks = @($script:unprovenChecks.ToArray())
}
Write-Utf8 -Path (Join-Path $fixtureRoot 'lifecycle-result.json') -Text ($report | ConvertTo-Json -Depth 5)
if ($null -ne $primary) { throw $primary }
Write-Host 'Isolated synthetic-payload MSI lifecycle PASS. This does not qualify the full production host.'
[pscustomobject]$report
