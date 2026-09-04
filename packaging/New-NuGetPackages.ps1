#requires -Version 5
<#
.SYNOPSIS
    Pack DesktopAICompanion.Contracts + DesktopAICompanion.ModuleKit for NuGet, so a module can be built outside this repo.

.DESCRIPTION
    Until these are published, writing a module means cloning this repository, because the template
    references the two libraries by project path. Publishing them is the whole difference between "you can
    write a module" and "you can write a module if you clone our tree".

    Nothing here needs a decision or a certificate: the contract is already package-shaped (no dependencies,
    AssemblyVersion frozen at 1.0.0.0 so a module built against any package version still binds to the copy
    the host ships), and the module system enforces no signing.

    This packs and verifies. It does NOT push -- pushing needs an API key and is a deliberate, irreversible
    act, so the command is printed for you to run.

.EXAMPLE
    .\packaging\New-NuGetPackages.ps1
.EXAMPLE
    .\packaging\New-NuGetPackages.ps1 -OutputDirectory dist\nuget
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $repoRoot 'dist\nuget' }
if (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path (Get-Location).ProviderPath $OutputDirectory
}

# The product version, which is also the package version (see the csproj comments for why).
[xml]$versionProps = Get-Content -LiteralPath (Join-Path $repoRoot 'ProductVersion.props')
$version = ([string]$versionProps.Project.PropertyGroup.DesktopAICompanionVersion).Trim()
if ([string]::IsNullOrWhiteSpace($version)) { throw 'Could not read DesktopAICompanionVersion from ProductVersion.props.' }
Write-Host ("version : {0}" -f $version)
Write-Host ("output  : {0}" -f $OutputDirectory)

if (Test-Path -LiteralPath $OutputDirectory) {
    Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.*nupkg' | Remove-Item -Force
} else {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

$projects = @(
    (Join-Path $repoRoot 'src\DesktopAICompanion.Contracts\DesktopAICompanion.Contracts.csproj'),
    (Join-Path $repoRoot 'src\DesktopAICompanion.ModuleKit\DesktopAICompanion.ModuleKit.csproj')
)

foreach ($project in $projects) {
    Write-Host ''
    Write-Host ("=== pack {0}" -f [IO.Path]::GetFileNameWithoutExtension($project)) -ForegroundColor Cyan
    & dotnet pack $project -c $Configuration -o $OutputDirectory --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed for $project (exit $LASTEXITCODE)." }
}

Write-Host ''
Write-Host '=== verify' -ForegroundColor Cyan
$packages = @(Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.nupkg' |
    Where-Object { $_.Name -notlike '*.symbols.nupkg' })
if ($packages.Count -lt 2) { throw "Expected 2 packages, found $($packages.Count)." }

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($package in $packages) {
    $zip = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $entries = @($zip.Entries | ForEach-Object { $_.FullName })
        # A package with no readme shows a blank page on nuget.org, which is the first thing an author sees.
        $hasReadme = @($entries | Where-Object { $_ -eq 'README.md' }).Count -eq 1
        # The whole point is a usable reference assembly for net10.0-windows.
        $hasLib = @($entries | Where-Object { $_ -like 'lib/net10.0-windows*/*.dll' }).Count -ge 1
        Write-Host ("  {0}" -f $package.Name)
        Write-Host ("     readme: {0}   lib: {1}   ({2:N0} bytes)" -f $hasReadme, $hasLib, $package.Length)
        if (-not $hasReadme) { throw "$($package.Name) has no README.md." }
        if (-not $hasLib) { throw "$($package.Name) has no lib\net10.0-windows assembly." }
    }
    finally { $zip.Dispose() }
}

# The contract must stay dependency-free: anything it drags in becomes a constraint on every module ever
# written against it, and it exists precisely to be a small stable surface.
$contracts = @($packages | Where-Object { $_.Name -like 'DesktopAICompanion.Contracts.*' })[0]
$zip = [IO.Compression.ZipFile]::OpenRead($contracts.FullName)
try {
    $nuspecEntry = @($zip.Entries | Where-Object { $_.FullName -like '*.nuspec' })[0]
    $reader = New-Object IO.StreamReader($nuspecEntry.Open())
    try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $deps = $nuspec.package.metadata.dependencies
    $depCount = 0
    if ($deps) { $depCount = @($deps.SelectNodes('//*[local-name()="dependency"]')).Count }
    Write-Host ("  DesktopAICompanion.Contracts dependencies: {0}" -f $depCount)
    if ($depCount -ne 0) { throw 'DesktopAICompanion.Contracts must stay dependency-free.' }
}
finally { $zip.Dispose() }

Write-Host ''
Write-Host ("PACKED {0} package(s) at {1}." -f $packages.Count, $version) -ForegroundColor Green
Write-Host '  Not pushed. To publish (needs an API key, and is irreversible for a given version):'
Write-Host ('    dotnet nuget push "{0}\*.nupkg" --source https://api.nuget.org/v3/index.json --api-key <KEY> --skip-duplicate' -f $OutputDirectory)
Write-Host '  A published version can never be replaced, only unlisted -- so pack, inspect, then push.'