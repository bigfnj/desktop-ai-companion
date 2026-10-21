#requires -Version 5
<#
.SYNOPSIS
    Prove the module template still scaffolds a module that compiles.

.DESCRIPTION
    A project template rots silently: it is not built by anything, so a rename in the ABI or ModuleKit
    leaves it broken and nobody finds out until someone tries to start a module. This scaffolds a throwaway
    module from templates\desktop-ai-companion-module outside modules\, builds it, checks no placeholder token survived
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
    [string]$Configuration = 'Release',
    [switch]$CleanupSelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$templateDir = Join-Path $repoRoot 'templates\desktop-ai-companion-module'
# A name no real module would take, so a leftover from a crashed run is obvious.
$sampleName = 'TemplateCheck'
$sampleId = 'templatecheck'
$sampleRoot = Join-Path ([IO.Path]::GetFullPath($env:TEMP)) ('dp-template-source-' + [guid]::NewGuid().ToString('N'))
$sampleDir = Join-Path $sampleRoot ("modules\" + $sampleName)
$inRepoSampleDir = Join-Path $repoRoot ("modules\" + $sampleName)
$expectedOutputDir = Join-Path $repoRoot ("build\DesktopAICompanionPortable\bin\$Configuration\x64\modules\" + $sampleId)
$loaderRoot = Join-Path $sampleRoot 'loader'
$outputDir = Join-Path $loaderRoot ("modules\" + $sampleId)
. (Join-Path $PSScriptRoot 'StagingPathSafety.ps1')

function Remove-TemplateScratch([string]$Path, [string]$AllowedRoot, [string]$TrustedRoot) {
    # A transient loader/indexer handle may delay removal. Never report a successful template
    # check while leaving its folder for the next incremental gate to mistake for a broken module.
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try {
            Assert-DesktopAICompanionUnlinkedChain -Path $Path -Root $TrustedRoot -Description 'template cleanup'
            Remove-DesktopAICompanionSafeDirectory -Path $Path -AllowedRoot $AllowedRoot -TrustedRoot $TrustedRoot
            if (-not (Test-Path -LiteralPath $Path)) { return }
        }
        catch {
            if ($attempt -eq 4) { throw "Template cleanup could not remove '$Path': $($_.Exception.Message)" }
        }
        if ($attempt -lt 4) { Start-Sleep -Milliseconds 200 }
    }
    throw "Template cleanup left its directory behind: $Path"
}

if ($CleanupSelfTest) {
    $fixture = Join-Path ([IO.Path]::GetFullPath($env:TEMP)) ('dp-template-cleanup-' + [guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($fixture)
    $payload = Join-Path $fixture 'payload.bin'
    [IO.File]::WriteAllText($payload, 'Synthetic cleanup fixture.')
    try {
        $held = [IO.File]::Open($payload, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            $refused = $false
            try { Remove-TemplateScratch $fixture $env:TEMP $env:TEMP }
            catch { $refused = $_.Exception.Message.StartsWith('Template cleanup could not remove', [StringComparison]::Ordinal) }
            if (-not $refused -or -not [IO.File]::Exists($payload)) {
                throw 'Template cleanup did not visibly refuse its locked fixture.'
            }
        }
        finally { $held.Dispose() }
        Remove-TemplateScratch $fixture $env:TEMP $env:TEMP
        if (Test-Path -LiteralPath $fixture) { throw 'Template cleanup left its released fixture behind.' }
    }
    finally {
        if (Test-Path -LiteralPath $fixture) { Remove-TemplateScratch $fixture $env:TEMP $env:TEMP }
    }
    Write-Host 'TEMPLATE CLEANUP OK (locked output fails visibly; released output is completely removed).'
    return
}

function Remove-Sample {
    # Both roots are fixed above, never derived from scaffolded project content. Verify containment
    # before recursive cleanup, including on a failure before the scaffold exists.
    if (-not $sampleRoot.StartsWith([IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The template staging root escaped TEMP.'
    }
    if (-not ([IO.Path]::GetFullPath($outputDir)).StartsWith([IO.Path]::GetFullPath($sampleRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The template output escaped its private staging directory.'
    }
    Remove-TemplateScratch $sampleRoot ([IO.Path]::GetFullPath($env:TEMP)) ([IO.Path]::GetFullPath($env:TEMP))
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
# release.yml prunes to the `$keep` most recent releases (tags are retained). So a default that drifts
# more than that many releases behind names a package with no distribution point, and --standalone
# stops restoring for every third-party author -- silently, from their point of view, and with nothing
# in this repo failing. That is how it got to 1.1.3 with the host at 1.1.5.
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
#
# ONE copy of the window. release.yml's prune step is where the number lives (`$keep = 3`), and this
# file restated it as `$keptReleases = 3` with prose the only link between the two (RA-003): a lower
# `$keep` would have let the template name a pruned version and this check keep passing, a higher one
# would have refused a valid default, and nothing asserted they agreed. Read out of the workflow,
# exactly one match with a floor of one, so a reworded prune step fails here loudly instead of this
# check defaulting to a number nobody re-measures. The chore the prune forces -- cutting a release
# drops the oldest from the window, and template.json's packageVersion has to move up before the next
# push goes green -- is what the throw below says; docs/RELEASE-CHECKLIST.md is where it belongs as a
# step and is outside this script.
$releaseWorkflow = Get-Content -LiteralPath (Join-Path $repoRoot '.github\workflows\release.yml') -Raw
$keepMatches = @([regex]::Matches($releaseWorkflow, '(?m)^\s*\$keep = (\d+)\s*$'))
if ($keepMatches.Count -ne 1) {
    throw ("release.yml declares '`$keep = <n>' $($keepMatches.Count) time(s); this check reads the release " +
           "retention window from that line and needs exactly one. If the prune step was reworded, point " +
           "this regex at the new shape rather than restating the number here.")
}
$keptReleases = [int]$keepMatches[0].Groups[1].Value
if ($keptReleases -lt 1) {
    throw "release.yml keeps $keptReleases release(s); the retention window must keep at least one, or every package has gone."
}
# Judged by git's EXIT CODE with the preference relaxed for the one call, not by the redirect alone.
# Under Windows PowerShell 5.1 a redirected stderr line -- `2>$null` included -- is an ErrorRecord that
# honours $ErrorActionPreference = 'Stop', so a git that said on stderr WHY it had no tags terminated
# this script on the redirect and the DEGRADED branch below was never reached (F212; pwsh 7 is not
# affected). Same shape as New-ModulePublish.ps1's Invoke-Git.
$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $tagOutput = @(& git -C $repoRoot tag --list 'v*' 2>&1 |
        Where-Object { $_ -isnot [Management.Automation.ErrorRecord] } | ForEach-Object { [string]$_ })
    $tagExit = $LASTEXITCODE
}
finally { $ErrorActionPreference = $previousPreference }
if ($tagExit -ne 0 -or $tagOutput.Count -eq 0) {
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
                "$keptReleases releases that keep their assets (window read from release.yml)")
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

    # The template targets modules/<name> in this repository. Validate that layout before adapting
    # this generated copy to an external staging directory, so a broken template reference still fails.
    # An editor can recreate obj/ after cleanup under modules/, confusing freshness (N-tray-questions-01).
    [xml]$generatedProject = Get-Content -LiteralPath $csproj -Raw
    $references = @($generatedProject.SelectNodes('//ProjectReference'))
    if ($references.Count -eq 0) { throw 'The in-repo template produced no project references.' }
    foreach ($reference in $references) {
        $target = [IO.Path]::GetFullPath((Join-Path $inRepoSampleDir $reference.GetAttribute('Include')))
        if (-not $target.StartsWith($repoRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetExtension($target) -ne '.csproj' -or -not (Test-Path -LiteralPath $target -PathType Leaf)) {
            throw "The template project reference does not resolve inside this repository: $target"
        }
        $reference.SetAttribute('Include', $target)
    }
    $outputPaths = @($generatedProject.SelectNodes('//OutputPath'))
    if ($outputPaths.Count -ne 1) { throw 'The in-repo template must declare one output path.' }
    $originalOutput = $outputPaths[0].InnerText.Replace('$(Configuration)', $Configuration)
    $resolvedOutput = [IO.Path]::GetFullPath((Join-Path $inRepoSampleDir $originalOutput)).TrimEnd('\')
    if (-not $resolvedOutput.Equals($expectedOutputDir.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "The template output would not land where the loader looks: $resolvedOutput"
    }
    $outputPaths[0].InnerText = $outputDir + '\'
    $generatedProject.Save($csproj)

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

    # Keep the generated project's TargetDir out of the shared runtime module tree. A background
    # project evaluator can recreate a deleted TargetDir after cleanup, leaving an empty module
    # folder that correctly fails the next host/coverage test. Use the same built host in a private
    # flat runtime layout instead; no test-only loader API or production folder exemption is needed.
    $runtimeFiles = @(Get-Content -LiteralPath (Join-Path $repoRoot 'packaging\runtime-files.txt') |
        ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
    foreach ($name in $runtimeFiles) {
        if (-not (Test-DesktopAICompanionWindowsLeafName $name) -or
            $name -match '(?i)(?:\.env$|^\.env\.|^\.npmrc$|\.(?:pem|key|p12|pfx|keystore)$|^privatekey)') {
            throw 'The host runtime manifest contains an unsafe or protected filename; no file was copied.'
        }
    }
    foreach ($name in $runtimeFiles) {
        $runtimeFile = Join-Path (Split-Path -Parent $hostExe) $name
        Assert-DesktopAICompanionUnlinkedChain -Path $runtimeFile -Root $repoRoot -Description 'template loader runtime'
        Copy-Item -LiteralPath $runtimeFile -Destination (Join-Path $loaderRoot $name)
    }
    $loaderExe = Join-Path $loaderRoot 'DesktopAICompanion.exe'

    # ---- the host's real loader must ACCEPT it ----
    # --module-selftest=<id> goes through ModuleHost.LoadFrom, so MinHostVersion is enforced (before Init),
    # the ALC resolves Contracts from the default context, and the scaffolded SelfTest actually runs. This is
    # the only assertion here that would have failed on the 1.4.8 default; everything above it passed.
    Write-Host '=== load the scaffolded module through the real host' -ForegroundColor Cyan
    # A PRIVATE TEMP for the child and for this script's own files, the F418 shape from
    # tests\Test-ModuleSelfTests.ps1. The host writes its marker under Path.GetTempPath(), which reads
    # TMP then TEMP, so both move for the Start-Process below (5.1 has no -Environment) and are restored
    # in the finally. This was the dp-*.txt marker reader F418 missed: the log and the marker sat in the
    # shared %TEMP% under fixed names, so two gates on one box (the main checkout and a worktree) could
    # delete each other's marker between write and read, grade each other's verdict, or fail to open a
    # log the other's child still held, and the unguarded File.Delete escaped as a raw exception
    # (RA-198). Short name on purpose: N-gates-01 measured a 107-character TEMP tripping the MoveFileEx
    # fallback in a module's own probe. Kept on failure for inspection, removed on success.
    $previousTemp = $env:TEMP
    $previousTmp = $env:TMP
    $runTemp = Join-Path $env:TEMP ('dp-tpl-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
    New-Item -ItemType Directory -Path $runTemp -Force | Out-Null
    $env:TEMP = $runTemp
    $env:TMP = $runTemp
    $runTempKept = $false
    try {
        $loaderLog = Join-Path $runTemp 'dp-template-module-selftest.log'
        # THE MARKER, not just the exit code. ModuleConventionSelfTest.Run returns TRUE on
        # "SKIP: no bundled module" and Finish swallows a failed marker write, so exit 0 does not mean
        # the module was loaded and exercised. Invoke-SelfTests.ps1 closed this exact gap for the four
        # --module-selftest= flags on 2026-09-17 -- "for exactly the checks which load a module through
        # the real loader, only the exit code was tested" -- and it was never carried across to here,
        # while the template's own SampleModule.cs promises "Never SKIP silently -- the gate fails on a
        # SKIP". The directory is this run's own, so a marker already there is another writer's, and
        # that is a refusal rather than a delete.
        $loaderMarker = Join-Path $runTemp "dp-module-$sampleId-selftest.txt"
        if (Test-Path -LiteralPath $loaderMarker) {
            throw ("A marker already exists in this run's private TEMP, so something else writes there and " +
                   "this run's verdict could be its: $loaderMarker")
        }
        $loader = Start-Process -FilePath $loaderExe -ArgumentList "--module-selftest=$sampleId" `
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
        # The marker is the only evidence the module was actually loaded and exercised.
        if (-not (Test-Path -LiteralPath $loaderMarker -PathType Leaf)) {
            throw ("The host exited 0 but wrote no self-test marker ($loaderMarker), so the scaffolded " +
                   "module was never loaded and exercised. An exit code alone cannot tell that apart " +
                   "from a pass.")
        }
        $loaderLines = @(Get-Content -LiteralPath $loaderMarker -Encoding UTF8)
        $loaderVerdict = @($loaderLines | Where-Object { $_ -match '^RESULT=' } | Select-Object -Last 1)
        $loaderResult = if ($loaderVerdict.Count) { ($loaderVerdict[0] -replace '^RESULT=', '').Trim() } else { 'NOVERDICT' }
        if ($loaderResult -ne 'PASS') {
            $loaderLines | Select-Object -Last 30 | ForEach-Object { Write-Host "        $_" }
            throw "The scaffolded module's self-test reported RESULT=$loaderResult."
        }
        # A SKIP is a FAILURE here, which is what the template's own doc block promises. Run() returns
        # true on one, so without this the gate would pass a template whose SelfTest does nothing.
        $loaderSkips = @($loaderLines | Where-Object { $_ -match '(^|\s)SKIP\b' })
        if ($loaderSkips.Count -gt 0) {
            $loaderSkips | ForEach-Object { Write-Host "        $_" }
            throw ("The scaffolded module's self-test SKIPPED (" + $loaderSkips.Count + " line(s)). The " +
                   "template documents 'Never SKIP silently -- the gate fails on a SKIP', so a skip is a " +
                   "template defect rather than an acceptable outcome.")
        }
        Write-Host ("OK   the host loaded and self-tested the scaffolded module (RESULT=PASS, no skips, " +
                    (@($loaderLines | Where-Object { $_ -match 'PASS:' })).Count + " assertion line(s); " +
                    "marker read from $loaderMarker)")
    }
    catch {
        $runTempKept = $true
        throw
    }
    finally {
        $env:TEMP = $previousTemp
        $env:TMP = $previousTmp
        if ($runTempKept) {
            Write-Host "  (this run's private TEMP is kept for inspection: $runTemp)" -ForegroundColor Yellow
        }
        else {
            Remove-Item -LiteralPath $runTemp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host ''
    Write-Host 'TEMPLATE OK (scaffolds, substitutes, builds, packages, and LOADS in the real host).' -ForegroundColor Green
}
finally {
    Remove-Sample
    if ($installed) {
        # Best-effort cleanup inside a finally, so its stderr must not become a terminating error that
        # REPLACES the failure already in flight: under Windows PowerShell 5.1 a redirected stderr line
        # is exactly that under 'Stop', and `dotnet new uninstall` writes one whenever the template is
        # already gone (F212). Nothing here judges the result, as before.
        $previousPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try { & dotnet new uninstall $templateDir 2>&1 | Out-Null }
        finally { $ErrorActionPreference = $previousPreference }
    }
}
