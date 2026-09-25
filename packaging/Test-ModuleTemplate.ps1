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

# A default must not contradict its own description, which is the check that would have caught a
# real corruption on 2026-09-18 and did not exist.
#
# minHostVersion's description says "Leave it at 1.0.0 unless you actually call a newer ABI member".
# A mutation test of the block below mutated packageVersion to 1.0.0 and restored it with an
# unguarded string replace, which by then also matched minHostVersion -- so the template shipped
# telling authors to use 1.0.0 while defaulting to 1.1.4, and every scaffolded module would have
# demanded a host no older than 1.1.4 for no reason. Nothing failed, because 1.1.4 is not ABOVE the
# shipped host, which is the only thing the checks above look at.
#
# So: where a description states the value in the form "Leave it at X", X and the default must agree.
# This is self-consistency, not a hardcoded expectation -- change the description and the check
# follows it.
foreach ($symbolName in 'minHostVersion', 'packageVersion') {
    $symbol = $symbols.$symbolName
    $stated = [regex]::Match([string]$symbol.description, 'Leave it at ([0-9]+\.[0-9]+\.[0-9]+)')
    if (-not $stated.Success) {
        Write-Host ("OK   template $symbolName's description states no value to contradict")
        continue
    }
    $declared = [string]$symbol.defaultValue
    if ($stated.Groups[1].Value -cne $declared) {
        throw ("template.json's $symbolName defaults to '$declared' while its own description tells " +
               "the author to leave it at '$($stated.Groups[1].Value)'. One of the two is wrong, and " +
               "a scaffolded module gets the DEFAULT.")
    }
    Write-Host ("OK   template $symbolName default '$declared' agrees with its own description")
}

# packageVersion needs a second, sharper check, because "not above the host" is satisfied by every
# version that ever existed and by plenty that did not.
#
# The author packages are attached to GITHUB RELEASES rather than pushed to nuget.org, and
# release.yml prunes to the 3 most recent releases (tags are retained). So a default that drifts more
# than three releases behind names a package with no distribution point, and --standalone stops
# restoring for every third-party author -- silently, from their point of view, and with nothing in
# this repo failing. That is how it got to 1.1.3 with the host at 1.1.5.
#
# Tags are the local proxy for releases: every release is cut from one, and a pruned release keeps
# its tag. Needs full history, which CI has (fetch-depth: 0).
#
# IT FAILS rather than degrading, which is what the line below used to claim and did not do: it wrote a
# yellow DEGRADED and then exited 0, so on a shallow clone (`git clone --depth 1`, no tags) run-gate
# judged this script by whether it threw and reported the template OK. A control that can run degraded
# has to SAY so on every run in a way that stops the run, or it is a check that quietly stopped
# checking. The sibling Test-ModulePublishFreshness.ps1 already had this exact correction applied to it;
# this file did not get it.
$keptReleases = 3
$tagOutput = @(& git -C $repoRoot tag --list 'v*' 2>$null)
if ($LASTEXITCODE -ne 0 -or $tagOutput.Count -eq 0) {
    Write-Warning ("DEGRADED  no v* tags are reachable, so packageVersion could not be checked against " +
                   "the releases that still have assets (shallow clone?)")
    throw ("Coverage narrowed silently: the packageVersion release-window check could not run because no " +
           "v* tags are reachable. Fetch tags (CI uses fetch-depth: 0) and re-run. This refuses rather " +
           "than passing, because a template that names a package with no distribution point breaks " +
           "--standalone restore for every third-party author, silently, from their point of view.")
}
else {
    $tagVersions = @()
    foreach ($tag in $tagOutput) {
        $candidate = $null
        if ([version]::TryParse($tag.TrimStart('v'), [ref]$candidate)) { $tagVersions += $candidate }
    }
    $recent = @($tagVersions | Sort-Object -Descending | Select-Object -First $keptReleases)
    $packageDeclared = [version]([string]$symbols.packageVersion.defaultValue)
    if ($recent -notcontains $packageDeclared) {
        throw ("template.json's packageVersion default is '$packageDeclared', which is not among the " +
               "$keptReleases most recent releases (" + (($recent | ForEach-Object { "v$_" }) -join ', ') +
               "). release.yml prunes older releases, and the Contracts/ModuleKit nupkgs live on the " +
               "release rather than on nuget.org, so a scaffolded --standalone module would fail to " +
               "restore. Raise it to a released version inside that window.")
    }
    Write-Host ("OK   template packageVersion default 'v$packageDeclared' is still within the " +
                "$keptReleases releases that keep their assets")
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
