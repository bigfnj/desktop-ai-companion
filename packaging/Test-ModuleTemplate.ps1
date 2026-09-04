#requires -Version 5
<#
.SYNOPSIS
    Prove the module template still scaffolds a module that compiles.

.DESCRIPTION
    A project template rots silently: it is not built by anything, so a rename in the ABI or ModuleKit
    leaves it broken and nobody finds out until someone tries to start a module. This scaffolds a throwaway
    module from templates\desktop-ai-companion-module into modules\, builds it, checks no placeholder token survived
    substitution, and removes it again.

    The template is installed and uninstalled around the run, so the machine is left as it was found.

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

    Write-Host ''
    Write-Host 'TEMPLATE OK (scaffolds, substitutes, builds, and packages correctly).' -ForegroundColor Green
}
finally {
    Remove-Sample
    if ($installed) {
        & dotnet new uninstall $templateDir 2>&1 | Out-Null
    }
}
