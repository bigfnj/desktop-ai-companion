#requires -Version 5
<#
.SYNOPSIS
    Build and optionally package the supported DesktopAICompanion Windows x64 application.

.DESCRIPTION
    The supported product is src\DesktopAICompanion_Portable.csproj, built as x64. Product
    identity comes from ProductVersion.props and package contents come from
    packaging\runtime-files.txt.

    The script never terminates running processes. If an existing DesktopAICompanion instance
    has locked a build output, close that instance and run the build again.

.EXAMPLE
    .\build.ps1
.EXAMPLE
    .\build.ps1 -Release -Zip
#>
[CmdletBinding()]
param(
    [switch]$Run,
    [switch]$Release,
    [switch]$Clean,
    [switch]$Zip,
    # Authenticode-sign the payload binaries with this certificate. Empty (the default) signs nothing and
    # leaves the build byte-identical to one from before signing existed, which build.yml depends on: it runs
    # this script on every pull request and has no certificate. See packaging\Invoke-Signtool.ps1.
    [string]$SigningCertThumbprint = '',
    # RFC3161 timestamp server. A timestamp outlives the certificate but forfeits MSI byte-reproducibility,
    # so it is a conscious choice rather than a default.
    [string]$SignTimestampUrl = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$sourceRoot = Join-Path $repoRoot 'src'
$projectPath = Join-Path $sourceRoot 'DesktopAICompanion_Portable.csproj'
$productPropsPath = Join-Path $repoRoot 'ProductVersion.props'
$runtimeManifestPath = Join-Path $repoRoot 'packaging\runtime-files.txt'
$configuration = if ($Release) { 'Release' } else { 'Debug' }
$outputDirectory = Join-Path $repoRoot "build\DesktopAICompanionPortable\bin\$configuration\x64"
$executablePath = Join-Path $outputDirectory 'DesktopAICompanion.exe'

if ($Zip -and -not $Release) {
    throw 'Production portable packaging requires -Release.'
}

$stagingPathSafety =
    Join-Path $repoRoot 'packaging\StagingPathSafety.ps1'
if (-not (Test-Path -LiteralPath $stagingPathSafety -PathType Leaf)) {
    throw "Staging path-safety policy is missing: $stagingPathSafety"
}
. $stagingPathSafety

function Resolve-DotnetCli {
    # .NET 10 build uses the `dotnet` CLI (SDK pinned by global.json). No Visual Studio / MSBuild.exe
    # probing is needed any more.
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command -and $command.Source) { return $command.Source }
    throw 'The dotnet CLI was not found on PATH. Install the .NET 10 SDK (see global.json).'
}

function Get-CanonicalProductVersion {
    if (-not (Test-Path -LiteralPath $productPropsPath)) {
        throw "Canonical product metadata is missing: $productPropsPath"
    }
    [xml]$props = Get-Content -LiteralPath $productPropsPath -Raw
    $value = [string]$props.Project.PropertyGroup.DesktopAICompanionVersion
    if ($value -notmatch '^\d+\.\d+\.\d+$') {
        throw "DesktopAICompanionVersion must be a three-part numeric version; found '$value'."
    }
    return $value
}

function Get-RuntimeManifest {
    if (-not (Test-Path -LiteralPath $runtimeManifestPath)) {
        throw "Runtime payload manifest is missing: $runtimeManifestPath"
    }

    $entries = @(
        Get-Content -LiteralPath $runtimeManifestPath |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ -and -not $_.StartsWith('#') }
    )
    if ($entries.Count -eq 0) { throw 'Runtime payload manifest is empty.' }

    $duplicates = @($entries | Group-Object | Where-Object Count -gt 1)
    if ($duplicates.Count -gt 0) {
        throw "Runtime payload manifest contains duplicate entries: $($duplicates.Name -join ', ')"
    }

    foreach ($entry in $entries) {
        if (-not (Test-DesktopAICompanionWindowsLeafName -Name $entry)) {
            throw "Runtime payload entries must be plain file names: '$entry'"
        }
    }
    return $entries
}

function Assert-RuntimeOutput {
    param([string[]]$Manifest)

    foreach ($name in $Manifest) {
        $path = Join-Path $outputDirectory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required runtime payload is missing: $path"
        }
    }

    $excludedDeveloperExtensions = @('.pdb', '.xml', '.lib', '.exp')
    $actualRuntimeFiles = @(
        Get-ChildItem -LiteralPath $outputDirectory -File |
            Where-Object { $excludedDeveloperExtensions -notcontains $_.Extension.ToLowerInvariant() } |
            Select-Object -ExpandProperty Name |
            Sort-Object
    )
    $expectedRuntimeFiles = @($Manifest | Sort-Object)
    $difference = @(Compare-Object $expectedRuntimeFiles $actualRuntimeFiles)
    if ($difference.Count -gt 0) {
        $detail = ($difference | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }) -join '; '
        throw "Build output and packaging\runtime-files.txt disagree: $detail"
    }
}

$productVersion = Get-CanonicalProductVersion
$runtimeManifest = @(Get-RuntimeManifest)

Write-Host "Product : Desktop AI Companion $productVersion" -ForegroundColor DarkGray
Write-Host "Project : $projectPath" -ForegroundColor DarkGray

$dotnet = Resolve-DotnetCli
$commonArguments = @(
    '-c', $configuration,
    '-p:Platform=x64',
    '--nologo',
    '-v:minimal'
)
$dotnetVersion = (& $dotnet --version 2>&1 | Select-Object -Last 1).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($dotnetVersion)) {
    throw "Unable to determine the .NET SDK version from '$dotnet'."
}
Write-Host "dotnet  : $dotnet" -ForegroundColor DarkGray
Write-Host "SDK     : $dotnetVersion" -ForegroundColor DarkGray

if ($Clean) {
    Write-Host 'Cleaning supported x64 output...' -ForegroundColor Cyan
    & $dotnet clean $projectPath @commonArguments
    if ($LASTEXITCODE -ne 0) { throw "clean failed (exit $LASTEXITCODE)" }

    # MSBuild's Clean target knows only about declared outputs. A previous
    # portable launch can leave mutable data (for example data\settings.json)
    # beside the executable, and removed project content can linger there as
    # well. Reset the configuration output under the guarded build root so a
    # clean build can never inherit or test against stale runtime state.
    # On a fresh tree the configuration output does not exist yet: there is no stale runtime
    # state to clear. Only reset when the output directory is actually present; a first build
    # then creates it normally.
    if (Test-Path -LiteralPath $outputDirectory -PathType Container) {
        Reset-DesktopAICompanionStagingDirectory `
            -Path $outputDirectory `
            -AllowedRoot (Join-Path $repoRoot 'build') `
            -TrustedRoot $repoRoot
    }
}

Write-Host 'Restoring NuGet packages...' -ForegroundColor Cyan
& $dotnet restore $projectPath '-p:Platform=x64' '--nologo' '-v:minimal'
if ($LASTEXITCODE -ne 0) { throw "restore failed (exit $LASTEXITCODE)" }

Write-Host "Building $configuration|x64..." -ForegroundColor Cyan
& $dotnet build $projectPath @commonArguments '--no-restore'
if ($LASTEXITCODE -ne 0) {
    throw "build failed (exit $LASTEXITCODE). If DesktopAICompanion.exe is locked, close the running application and retry."
}

if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "The expected executable was not produced: $executablePath"
}

Assert-RuntimeOutput -Manifest $runtimeManifest
Write-Host "Runtime output OK -> $executablePath" -ForegroundColor Green

# Sign the payload HERE, after the set-equality check and before anything packages it. This one point covers
# both consumers: New-DeterministicPortableZip streams these same files into the ZIP, and
# build-installer.ps1 stages them for the MSI cabinet. Signing in either of those places instead would leave
# the other one unsigned.
if (-not [string]::IsNullOrWhiteSpace($SigningCertThumbprint)) {
    $signablePayload = @(
        Get-ChildItem -LiteralPath $outputDirectory -File |
            Where-Object { $_.Extension -in @('.exe', '.dll') } |
            ForEach-Object { $_.FullName })
    if ($signablePayload.Count -gt 0) {
        Write-Host "Signing $($signablePayload.Count) payload binaries..." -ForegroundColor Cyan
        & (Join-Path $repoRoot 'packaging\Invoke-Signtool.ps1') `
            -Path $signablePayload `
            -Thumbprint $SigningCertThumbprint `
            -TimestampUrl $SignTimestampUrl
    }
}

# Build the plugin modules into the runtime modules\<id>\ folders. These live in a subfolder, not the
# root payload manifest (which is root-only), so they do not affect the runtime set-equality check.
# Bundling first-party modules into the ZIP/MSI installer payload is a later phase (S6).
#   - TestModule: a throwaway S1 plugin-pipeline proof (dev/self-test only).
#   - (The S2 Sound module was retired in B4: the base owns audio playback via AudioOutput now.)
#   - Fortunes: the fortune engine + welcome (S3) — carries ONNX/bge-small.
#   - AiBrain: the optional screen-commentary LLM (S4) — dormant scaffold until the S4b flip.
$moduleProjects = @(
    (Join-Path $repoRoot 'modules\TestModule\TestModule.csproj'),
    (Join-Path $repoRoot 'modules\Fortunes\Fortunes.csproj'),
    (Join-Path $repoRoot 'modules\AiBrain\AiBrain.csproj'),
    #   - PetStudio: the pet validator/preview studio (replaces the retired Tools\PetTester). Also owns the
    #     Shimeji skin import/convert flow (the ShimejiConvert.Engine is source-linked into it).
    (Join-Path $repoRoot 'modules\PetStudio\PetStudio.csproj'),
    #   - Reminder: reads a calendar feed and has the pet announce events a few minutes before they start.
    (Join-Path $repoRoot 'modules\Reminder\Reminder.csproj'),
    #   - Remembrance: records a meeting (mic + system loopback), transcribes it offline with a local Whisper,
    #     names it from the calendar (Reminder's meeting.current), and purges the audio after 72h.
    (Join-Path $repoRoot 'modules\Remembrance\Remembrance.csproj'),
    #   - BlinkingLed: blinks the keyboard's Scroll Lock light so the machine reads as active. A port of the
    #     standalone BlinkingLED tray app; the host supplies the tray item, options pane and settings.
    (Join-Path $repoRoot 'modules\BlinkingLed\BlinkingLed.csproj')
)
foreach ($moduleProject in $moduleProjects) {
    if (Test-Path -LiteralPath $moduleProject) {
        Write-Host "Building plugin module: $([System.IO.Path]::GetFileNameWithoutExtension($moduleProject))..." -ForegroundColor Cyan
        & $dotnet build $moduleProject -c $configuration '--nologo' '-v:minimal'
        if ($LASTEXITCODE -ne 0) { throw "module build failed ($moduleProject) (exit $LASTEXITCODE)" }
    }
}

if ($Zip) {
    $distributionDirectory = Join-Path $repoRoot 'dist'
    New-Item -ItemType Directory -Path $distributionDirectory -Force | Out-Null
    $zipPath = Join-Path $distributionDirectory 'DesktopAICompanion-Portable.zip'

    # Stage the bundled offline content (portable zip only) through the shared
    # helper so build.ps1 and the release workflow bundle an identical set. The
    # MSI never carries this content, so the installer stays lean.
    $contentStaging = Join-Path $distributionDirectory (
        '.content-' + [Guid]::NewGuid().ToString('N'))
    try {
        & (Join-Path $repoRoot 'packaging\Stage-BundledContent.ps1') `
            -RepoRoot $repoRoot `
            -StagingRoot $contentStaging

        & (Join-Path $repoRoot 'packaging\New-DeterministicPortableZip.ps1') `
            -RuntimeRoot $outputDirectory `
            -DestinationPath $zipPath `
            -ManifestPath $runtimeManifestPath `
            -ContentDirectories @(
                @{ Prefix = 'pets'; Source = (Join-Path $contentStaging 'pets') }
                @{ Prefix = 'fortunes'; Source = (Join-Path $contentStaging 'fortunes') }
            )
    }
    finally {
        if (Test-Path -LiteralPath $contentStaging) {
            Remove-Item -LiteralPath $contentStaging -Recurse -Force
        }
    }

    Write-Host (
        "Portable ZIP -> {0} ({1:N1} MB; bundled pets + fortunes)" -f
        $zipPath,
        ((Get-Item -LiteralPath $zipPath).Length / 1MB)
    ) -ForegroundColor Green
}

if ($Run) {
    Write-Host 'Launching DesktopAICompanion...' -ForegroundColor Cyan
    Start-Process -FilePath $executablePath -WorkingDirectory $outputDirectory
}