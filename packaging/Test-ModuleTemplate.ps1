#requires -Version 5
<#
.SYNOPSIS
    Prove the module template still scaffolds a module that compiles.

.DESCRIPTION
    A project template rots silently: it is not built by anything, so a rename in the ABI or ModuleKit
    leaves it broken and nobody finds out until someone tries to start a module. This scaffolds a throwaway
    module from templates\desktop-ai-companion-module into modules\, builds it, checks no placeholder token survived
    substitution, LOADS IT THROUGH THE REAL HOST, and removes it again.

    The template is installed and uninstalled around the run, so the machine is left as it was found.

    BUILDS is not the same as WORKS, and the gap was real. Until 2026-09-11 the template defaulted
    minHostVersion to 1.4.8 -- pre-rebase numbering, and therefore ABOVE the shipped host -- so every
    scaffolded module built perfectly and was then refused by ModuleHost at load, breaking the Readme's own
    module quick-start. This script passed throughout, because it asserted substitution and compilation and
    never asked the host for an opinion. It now does both: the version defaults are checked against
    ProductVersion.props, and the built module is run through the host's real loader via
    --module-selftest, which enforces MinHostVersion before Init.

    Requires a built host (build.ps1 -Release), because the loader check needs the real exe. It fails
    rather than skipping when that is absent: a skip here is indistinguishable from a pass, which is the
    class of hole this script exists to close.

.EXAMPLE
    .\packaging\Test-ModuleTemplate.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$templateDir = Join-Path $repoRoot 'templates\desktop-ai-companion-module'
# A name no real module would take, so a leftover from a crashed run is obvious.
$sampleName = 'TemplateCheck'
$sampleId = 'templatecheck'
$sampleDir = Join-Path $repoRoot ("modules\" + $sampleName)
$outputDir = Join-Path $repoRoot ("build\DesktopAICompanionPortable\bin\$Configuration\x64\modules\" + $sampleId)

function Remove-Sample {
    foreach ($path in @($sampleDir, $outputDir)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

if (-not (Test-Path -LiteralPath $templateDir)) { throw "The template is missing: $templateDir" }

# ---- the template's version defaults must name a host that EXISTS ----
# Checked before scaffolding, because a bad default here produces a module that compiles and is then
# refused at load, and the compile is what this script used to measure. Both values are bounded by the
# product version: MinHostVersion above it is refused by ModuleHost forever, and a packageVersion above it
# names a NuGet package that was never released (they are attached to GitHub releases, not pushed to
# nuget.org, so only a released version can be restored).
$templateJson = Join-Path $templateDir '.template.config\template.json'
if (-not (Test-Path -LiteralPath $templateJson)) { throw "The template config is missing: $templateJson" }
$symbols = (Get-Content -LiteralPath $templateJson -Raw | ConvertFrom-Json).symbols

[xml]$versionPropsXml = Get-Content -LiteralPath (Join-Path $repoRoot 'ProductVersion.props')
$productVersion = ([string]$versionPropsXml.Project.PropertyGroup.DesktopAICompanionVersion).Trim()
if ([string]::IsNullOrWhiteSpace($productVersion)) {
    throw 'Could not read <DesktopAICompanionVersion> from ProductVersion.props.'
}
$productParsed = [version]$productVersion

foreach ($symbolName in 'minHostVersion', 'packageVersion') {
    $declared = [string]$symbols.$symbolName.defaultValue
    $parsed = $null
    if (-not [version]::TryParse($declared, [ref]$parsed)) {
        throw "template.json's $symbolName default '$declared' is not a version."
    }
    if ($parsed -gt $productParsed) {
        throw ("template.json's $symbolName default is '$declared', which is NEWER than the shipped host " +
               "'$productVersion'. A scaffolded module would build and then be refused at load " +
               "(minHostVersion), or fail to restore against a package that was never released " +
               "(packageVersion). Lower it to a version that exists.")
    }
    Write-Host ("OK   template $symbolName default '$declared' is not above the host '$productVersion'")
}

# The loader check below needs the real host. Fail loudly rather than skipping: this script's whole
# history is of passing while the thing it guards was broken.
$hostExe = Join-Path $repoRoot ("build\DesktopAICompanionPortable\bin\$Configuration\x64\DesktopAICompanion.exe")
if (-not (Test-Path -LiteralPath $hostExe)) {
    throw ("The host executable is missing, so the scaffolded module cannot be run through the real " +
           "loader: $hostExe`nBuild it first (.\build.ps1 -Release). This check is not skippable -- a " +
           "template that compiles but is refused at load is exactly the failure it exists to catch.")
}

$installed = $false
try {
    Remove-Sample

    Write-Host '=== install the template' -ForegroundColor Cyan
    & dotnet new install $templateDir --force | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet new install failed (exit $LASTEXITCODE)." }
    $installed = $true

    Write-Host '=== scaffold a module' -ForegroundColor Cyan
    & dotnet new desktop-ai-companion-module -n $sampleName --moduleId $sampleId --displayName 'Template Check' -o $sampleDir | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet new desktop-ai-companion-module failed (exit $LASTEXITCODE)." }

    $csproj = Join-Path $sampleDir ($sampleName + '.csproj')
    $source = Join-Path $sampleDir ($sampleName + '.cs')
    foreach ($path in @($csproj, $source)) {
        if (-not (Test-Path -LiteralPath $path)) { throw "The template did not produce $path." }
    }

    Write-Host '=== check every placeholder was substituted' -ForegroundColor Cyan
    # A surviving token means a symbol was renamed in template.json but not in the content (or vice versa),
    # which produces a module that compiles but is named after the template.
    $leftovers = Select-String -Path @($csproj, $source) -Pattern 'SAMPLE_|SampleModule|samplemodule'
    if ($leftovers) {
        foreach ($leftover in $leftovers) { Write-Host ("  " + $leftover.Line.Trim()) -ForegroundColor Red }
        throw 'A template placeholder survived substitution.'
    }

    Write-Host '=== build the scaffolded module' -ForegroundColor Cyan
    & dotnet build $csproj -c $Configuration -v minimal --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The scaffolded module did not build.' }

    $dll = Join-Path $outputDir ($sampleName + '.dll')
    if (-not (Test-Path -LiteralPath $dll)) { throw "The module did not land where the loader looks: $dll" }
    # ModuleKit must travel WITH the module; the contract must not (the host shares its own copy).
    if (-not (Test-Path -LiteralPath (Join-Path $outputDir 'DesktopAICompanion.ModuleKit.dll'))) {
        throw 'DesktopAICompanion.ModuleKit.dll did not ship beside the module.'
    }
    if (Test-Path -LiteralPath (Join-Path $outputDir 'DesktopAICompanion.Contracts.dll')) {
        throw 'DesktopAICompanion.Contracts.dll shipped with the module; the reference must stay Private="false".'
    }

    # ---- the host's real loader must ACCEPT it ----
    # --module-selftest=<id> goes through ModuleHost.LoadFrom, so MinHostVersion is enforced (before Init),
    # the ALC resolves Contracts from the default context, and the scaffolded SelfTest actually runs. This is
    # the only assertion here that would have failed on the 1.4.8 default; everything above it passed.
    Write-Host '=== load the scaffolded module through the real host' -ForegroundColor Cyan
    $loaderLog = Join-Path $env:TEMP 'dp-template-module-selftest.log'
    [System.IO.File]::Delete($loaderLog)
    $loader = Start-Process -FilePath $hostExe -ArgumentList "--module-selftest=$sampleId" `
        -Wait -PassThru -NoNewWindow -RedirectStandardOutput $loaderLog -RedirectStandardError "$loaderLog.err"
    if ($loader.ExitCode -ne 0) {
        foreach ($logPath in @($loaderLog, "$loaderLog.err")) {
            if (Test-Path -LiteralPath $logPath) {
                Get-Content -LiteralPath $logPath | Select-Object -Last 30 | ForEach-Object { Write-Host "        $_" }
            }
        }
        throw ("The host refused or failed the scaffolded module (--module-selftest=$sampleId exited " +
               "$($loader.ExitCode)). It compiled, so this is a LOAD-time rejection: check template.json's " +
               "minHostVersion against ProductVersion.props, and the Private=`"false`" contract reference.")
    }
    Write-Host ("OK   the host loaded and self-tested the scaffolded module")

    Write-Host ''
    Write-Host 'TEMPLATE OK (scaffolds, substitutes, builds, packages, and LOADS in the real host).' -ForegroundColor Green
}
finally {
    Remove-Sample
    if ($installed) {
        & dotnet new uninstall $templateDir 2>&1 | Out-Null
    }
}
