# 7+, because this script CALLS New-ContentCatalog.ps1 (see the reasoning in its header) and does so
# near the end -- after the zips are written and staged. Under 5.1 the publish would therefore produce
# artifacts and then refuse at the last step. Fail before anything is written instead.
#requires -Version 7
<#
.SYNOPSIS
    Publish one module: build it, zip it, register it in modules.json, and regenerate catalog.json — in the
    order that actually works.

.DESCRIPTION
    Publishing a module is a five-step sequence with two traps, and both have shipped bugs before:

      * catalog.json records the SHA-256 of the COMMITTED git blob, because that is the byte stream
        raw.githubusercontent.com serves. Regenerate the catalog before committing the zip and it records a
        hash for content nobody can download. So: zip -> COMMIT -> catalog, never zip -> catalog -> commit.
      * modules.json carries the version the in-app Update button compares against. If it lags the module's
        own ModuleInfo.Version the update is never offered; if it leads, it is offered forever.

    This script does the whole sequence, reads the version and permissions out of the module's source so they
    cannot disagree with the code, and refuses to regenerate the catalog until the zip is committed. Merging
    the result to master IS the publish — modules-dist/ is served straight off raw.githubusercontent.com.

.PARAMETER ModuleId
    The module's id (lowercase), e.g. petstudio.

.PARAMETER Name
    Display name for a module not yet in modules.json. Required the first time only.

.PARAMETER Description
    Catalog description for a module not yet in modules.json. Required the first time only.

.PARAMETER Commit
    Commit the zip and modules.json, then regenerate the catalog. Without it the script stops after the zip
    and prints the git command to run.

.PARAMETER SkipBuild
    Use the existing build output instead of rebuilding.

.EXAMPLE
    .\packaging\New-ModulePublish.ps1 -ModuleId petstudio -Name 'Pet Studio' -Description 'Check a pet...'
.EXAMPLE
    .\packaging\New-ModulePublish.ps1 -ModuleId fortunes -Commit
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ModuleId,
    [string]$Name,
    [string]$Description,
    [switch]$Commit,
    [switch]$SkipBuild,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))

# git writes ordinary notices to stderr -- "warning: ... CRLF will be replaced by LF" being the one that
# matters here -- and with $ErrorActionPreference='Stop' PowerShell 5.1 turns any native stderr line into a
# terminating NativeCommandError. That aborted this script mid-publish AFTER `git add` had already succeeded.
# So run git with errors non-terminating and judge it the only way that is actually reliable: its exit code.
function Invoke-Git([string[]]$GitArgs, [string]$What) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & git @GitArgs 2>&1
        if ($LASTEXITCODE -ne 0) {
            $output | ForEach-Object { Write-Host ("  " + $_) -ForegroundColor Red }
            throw ("{0} failed (exit {1})." -f $What, $LASTEXITCODE)
        }
        # Hand back only real stdout lines; drop the ErrorRecords stderr arrives as.
        return @($output | Where-Object { $_ -isnot [Management.Automation.ErrorRecord] })
    }
    finally { $ErrorActionPreference = $previous }
}
$moduleId = $ModuleId.ToLowerInvariant()
$distDir = Join-Path $repoRoot 'modules-dist'
$zipPath = Join-Path $distDir ($moduleId + '.zip')
$zipRelPath = 'modules-dist/' + $moduleId + '.zip'
$manifestPath = Join-Path $distDir 'modules.json'
$outputDir = Join-Path $repoRoot ("build\DesktopAICompanionPortable\bin\$Configuration\x64\modules\" + $moduleId)

# ---- locate the module's source folder (PascalCase on disk, lowercase id) ----
$moduleDir = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'modules') -Directory |
    Where-Object { $_.Name.ToLowerInvariant() -eq $moduleId } |
    Select-Object -First 1
if (-not $moduleDir) { throw "No module source folder matches id '$moduleId' under modules\." }

$moduleSource = Get-ChildItem -LiteralPath $moduleDir.FullName -Filter '*Module.cs' -File |
    Select-Object -First 1
if (-not $moduleSource) { throw "No *Module.cs in $($moduleDir.FullName); cannot read its version." }

# ---- read version + permissions from the source, so the catalog cannot drift from the code ----
$sourceText = Get-Content -LiteralPath $moduleSource.FullName -Raw

# Anchored so it cannot match MinHostVersion.
$versionMatch = [regex]::Match($sourceText, '(?m)^\s*Version\s*=\s*"([^"]+)"')
if (-not $versionMatch.Success) { throw "Could not read ModuleInfo.Version from $($moduleSource.Name)." }
$version = $versionMatch.Groups[1].Value

# COMMENTS ARE STRIPPED FIRST. The old pattern was [^,;]+?, which cannot cross a comma -- and a
# multi-line Permissions expression with an explanatory // comment in it contains commas in prose.
# The match then failed, $permissions fell back to '', and the block below treated that as
# "nothing to change" and kept whatever modules.json already said. AgentFlow shipped a catalog
# entry claiming four permissions while the code declared eight, including InputSynthesis and
# Audio -- an UNDERSTATED consent screen, which is the one direction that matters.
#
# This file's own synopsis promises the catalog "cannot disagree with the code". It could, and did,
# so an unreadable declaration is now a hard failure rather than a silent carry-forward.
$codeOnly = ($sourceText -split "`n" | ForEach-Object { $_ -replace '//.*$', '' }) -join "`n"
# THE EXPRESSION ITSELF, not "everything up to the next semicolon". The capture used to be `(.+?);`,
# which ran to the `;` closing the whole ModuleInfo initialiser and was then scrubbed to letters per
# `|`-separated piece. That worked only while Permissions was the LAST field: any field after it --
# `Homepage = "https://..."` once the comment stripper had eaten the URL's `//`, or a plain string --
# was swallowed into the final flag, producing a token such as `NetworkHomepagehttps`, which the host
# then IGNORED (RemoteCatalog.TryParsePermissions drops a name it does not know), so the consent
# screen lost the module's last real flag: the understated direction again (F214). Now the match is a
# run of `ModulePermissions.<Name>` joined by `|`, and every name is checked against the enum in the
# Contracts source, because the host's forward-compatibility rule means the catalog will never reject
# an invented name on its own. The validation is the load-bearing half.
$permissionsMatch = [regex]::Match($codeOnly, 'Permissions\s*=\s*((?:ModulePermissions\.[A-Za-z_][A-Za-z0-9_]*\s*(?:\|\s*)?)+)')
if (-not $permissionsMatch.Success) {
    throw "Could not read ModuleInfo.Permissions from $($moduleSource.Name). Refusing to publish a" +
          " catalog entry that would silently keep the previous permission list."
}
$permissionEnumSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\DesktopAICompanion.Contracts\PluginApi.cs') -Raw
$permissionEnumBody = [regex]::Match($permissionEnumSource, '(?s)enum ModulePermissions\s*\{(.*?)\r?\n    \}')
if (-not $permissionEnumBody.Success) { throw 'Could not locate the ModulePermissions enum in src\DesktopAICompanion.Contracts\PluginApi.cs.' }
$knownPermissions = @([regex]::Matches(
    (($permissionEnumBody.Groups[1].Value -split "`n" | ForEach-Object { $_ -replace '//.*$', '' }) -join "`n"),
    '(?m)^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=') | ForEach-Object { $_.Groups[1].Value })
if ($knownPermissions.Count -lt 2) { throw 'The ModulePermissions enum parsed to fewer than two members; the validator below would refuse everything.' }
# "ModulePermissions.Pets | ModulePermissions.Storage" -> "Pets, Storage"
$permissionTokens = @($permissionsMatch.Groups[1].Value -split '\|' |
    ForEach-Object { ($_ -replace 'ModulePermissions\.', '').Trim() } |
    Where-Object { $_ })
$unknownPermissions = @($permissionTokens | Where-Object { $knownPermissions -cnotcontains $_ })
if ($unknownPermissions.Count -gt 0) {
    throw (("ModuleInfo.Permissions in $($moduleSource.Name) names {0}, which is not a member of ModulePermissions " +
            "(known: {1}). Refusing to publish a flag the host would silently drop from the consent screen.") -f
            ($unknownPermissions -join ', '), ($knownPermissions -join ', '))
}
$permissions = @($permissionTokens | Where-Object { $_ -ne 'None' }) -join ', '
if (-not $permissions) {
    throw "ModuleInfo.Permissions in $($moduleSource.Name) parsed to nothing. Refusing to publish."
}

# The host floor, which nothing read before. A module that needs a newer host than the user has is
# refused at LOAD time by its own ModuleInfo -- but without this in the catalog the user is offered
# the download first and told why only afterwards, which is the failure the sequencing rule in
# docs/RELEASE-CHECKLIST.md exists to prevent.
$minHostMatch = [regex]::Match($codeOnly, '(?m)^\s*MinHostVersion\s*=\s*"([^"]+)"')
$minHostVersion = if ($minHostMatch.Success) { $minHostMatch.Groups[1].Value } else { '' }

# Publish AFTER the module's source is committed, never before. Test-ModulePublishFreshness compares commit
# RECENCY, so a zip committed ahead of the source it was built from reads as stale even though its bytes are
# correct -- and re-zipping under the SAME PowerShell then produces identical bytes, leaving no new commit
# available to fix the ordering. The only ways out are rewriting history or a dummy commit, so refuse up
# front instead. (This bit me publishing the ModuleKit migration.)
#
# 'the zip is deterministic' used to be stated without the qualifier and it is not true across editions:
# measured 2026-09-17, the same payload zips to 63,580 bytes under 5.1 and 64,271 under pwsh 7. That is why
# both zip scripts now require 7, and why this reads 'the same PowerShell' rather than 'deterministic'.
# 7 is a floor, not the whole promise: the bytes also differ between .NET 8, 9 and 10 (pwsh 7.4, 7.5 and
# 7.6), so the publish commit below records the exact PowerShell version (F213).
#
# THE WHOLE WATCH SET, not modules\<Name> alone. Test-ModulePublishFreshness.ps1 judges staleness against
# the module directory PLUS every path outside it that ends up in the zip -- ModuleKit, the src\ and tools\
# files PetStudio source-links, the implicit MSBuild inputs -- while this guard read modules\<Name> only.
# So an uncommitted ModuleKit edit passed here, was compiled into the zip, and the commit that later
# carried the edit made every payload stale, with no re-zip able to produce a new commit (F215): the
# exact trap the paragraph above describes, arriving through the path the guard did not look at. The
# pathspecs come from the helper the freshness check dot-sources, so the two lists cannot drift apart
# again. A DEGRADED watch set (an unparseable csproj, an unresolved $(Property)) is refused as well: a
# guard that quietly examines less than the check would let through exactly the dirt the check reports.
. (Join-Path $PSScriptRoot 'ModuleWatchSet.ps1')
$watch = Get-ModuleWatchPathspecs -RepoRoot $repoRoot -ModuleDirectory $moduleDir
if ($watch.Degraded.Count -gt 0) {
    throw ("The watch set for modules/{0} is degraded, so this guard cannot see everything the zip is built from: {1}" -f
           $moduleDir.Name, ($watch.Degraded -join '; '))
}
Push-Location $repoRoot
# ONE array, built before the call. The pathspec used to be written inline as 'modules/' + $moduleDir.Name,
# which PowerShell split into TWO array elements, so git received `modules/` and `AiBrain` instead of
# `modules/AiBrain`. That made this guard fire on an uncommitted change in ANY module and then blame it
# on the one being published -- publishing aibrain refused because modules/PetStudio/PetStudio.csproj
# was dirty, reported as "modules/AiBrain has uncommitted changes".
$guardPathspecs = @('status', '--porcelain', '--') + @($watch.Pathspecs)
try { $uncommittedSource = @(Invoke-Git $guardPathspecs 'git status') }
finally { Pop-Location }
if ($uncommittedSource.Count -gt 0) {
    Write-Host ''
    Write-Host ("modules/{0}, or a path its payload is built from, has uncommitted changes:" -f $moduleDir.Name) -ForegroundColor Yellow
    foreach ($line in $uncommittedSource) { Write-Host ("    " + $line) }
    Write-Host ("  (watched beyond the module directory: {0})" -f (@($watch.External) -join ', ')) -ForegroundColor DarkGray
    throw ("Commit the module source BEFORE publishing it. The freshness check compares commit order, so a " +
           "payload committed ahead of its source reads as stale and a deterministic re-zip cannot fix it.")
}

Write-Host ("module  : {0} ({1})" -f $moduleId, $moduleDir.Name)
Write-Host ("version : {0}   (from {1})" -f $version, $moduleSource.Name)
Write-Host ("perms   : {0}" -f $(if ($permissions) { $permissions } else { '(none)' }))

# ---- 1. build ----
if (-not $SkipBuild) {
    Write-Host ''
    Write-Host '=== build' -ForegroundColor Cyan
    $csproj = Get-ChildItem -LiteralPath $moduleDir.FullName -Filter '*.csproj' -File | Select-Object -First 1
    if (-not $csproj) { throw "No .csproj in $($moduleDir.FullName)." }
    & dotnet build $csproj.FullName -c $Configuration -v minimal --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The module did not build.' }
}
if (-not (Test-Path -LiteralPath $outputDir -PathType Container)) {
    throw "No build output at $outputDir. Build first (or drop -SkipBuild)."
}

# ---- 2. zip ----
Write-Host ''
Write-Host '=== zip the payload' -ForegroundColor Cyan
$zip = & (Join-Path $PSScriptRoot 'New-ModuleDistZip.ps1') `
    -ModuleId $moduleId -SourceDirectory $outputDir -DestinationPath $zipPath
Write-Host ("  {0}  ({1:N0} bytes, sha256 {2})" -f $zipRelPath, $zip.Bytes, $zip.Sha256.Substring(0, 16))

# ---- 3. register in modules.json ----
Write-Host ''
Write-Host '=== modules.json' -ForegroundColor Cyan
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$entries = @($manifest.modules)
$existing = $entries | Where-Object { $_.id -eq $moduleId } | Select-Object -First 1

if ($existing) {
    if ([string]$existing.version -ne $version) {
        Write-Host ("  version {0} -> {1}" -f $existing.version, $version)
    } else {
        Write-Host ("  version {0} (unchanged)" -f $version)
    }
    $existing.version = $version
    if ($permissions) { $existing.permissions = $permissions }
    # Read a few lines above and, until now, never assigned -- so the floor stayed absent from
    # the manifest while the module declared one. Add-Member because the property does not exist
    # on an entry published before this script knew about it.
    if ($minHostVersion) {
        if ($existing.PSObject.Properties.Name -contains 'minHostVersion') {
            $existing.minHostVersion = $minHostVersion
        } else {
            $existing | Add-Member -NotePropertyName 'minHostVersion' -NotePropertyValue $minHostVersion
        }
    }
    if ($Name) { $existing.name = $Name }
    if ($Description) { $existing.desc = $Description }
} else {
    # A first publish needs the catalog-facing copy, which no compiled DLL can supply.
    if (-not $Name -or -not $Description) {
        throw ("'$moduleId' is not in modules.json yet. A first publish needs -Name and -Description " +
               '(they are shown in the Modules pane before download and cannot be read from the DLL).')
    }
    Write-Host ("  adding a new entry for {0}" -f $moduleId)
    # minHostVersion INCLUDED. The existing-entry branch above assigns it and this one did not, so a
    # module's very first publish dropped the floor its code declares; the strict-mode catalog generator
    # then threw on the absent key, after the commit had landed (F216). The writer below omits the key
    # when the value is empty, so a module that declares no floor is written exactly as before.
    $entries += [pscustomobject][ordered]@{
        id             = $moduleId
        name           = $Name
        desc           = $Description
        version        = $version
        permissions    = $permissions
        minHostVersion = $minHostVersion
    }
}

# Written by hand rather than ConvertTo-Json to keep the file's existing 4-space shape, so the diff shows
# the change and not a reformat of every line. The string escaper is hand-rolled for the same reason:
# PowerShell 5.1's ConvertTo-Json escapes an apostrophe as ', which would rewrite every existing
# description that contains one and bury the real change in noise.
function ConvertTo-JsonString([string]$value) {
    if ($null -eq $value) { return '""' }
    $builder = New-Object Text.StringBuilder
    [void]$builder.Append('"')
    foreach ($char in $value.ToCharArray()) {
        switch ($char) {
            '"'      { [void]$builder.Append('\"');  continue }
            '\'      { [void]$builder.Append('\\');  continue }
            "`b"     { [void]$builder.Append('\b');  continue }
            "`f"     { [void]$builder.Append('\f');  continue }
            "`n"     { [void]$builder.Append('\n');  continue }
            "`r"     { [void]$builder.Append('\r');  continue }
            "`t"     { [void]$builder.Append('\t');  continue }
            default  {
                if ([int]$char -lt 0x20) { [void]$builder.Append('\u{0:x4}' -f [int]$char) }
                else { [void]$builder.Append($char) }
                continue
            }
        }
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

$lines = New-Object 'Collections.Generic.List[string]'
$lines.Add('{')
$lines.Add('    "modules": [')
for ($i = 0; $i -lt $entries.Count; $i++) {
    $entry = $entries[$i]
    $lines.Add('        {')
    $lines.Add('            "id": ' + (ConvertTo-JsonString ([string]$entry.id)) + ',')
    $lines.Add('            "name": ' + (ConvertTo-JsonString ([string]$entry.name)) + ',')
    $lines.Add('            "desc": ' + (ConvertTo-JsonString ([string]$entry.desc)) + ',')
    $lines.Add('            "version": ' + (ConvertTo-JsonString ([string]$entry.version)) + ',')
    $lines.Add('            "permissions": ' + (ConvertTo-JsonString ([string]$entry.permissions)) +
               $(if ($entry.PSObject.Properties.Name -contains 'minHostVersion' -and $entry.minHostVersion) { ',' } else { '' }))
    if ($entry.PSObject.Properties.Name -contains 'minHostVersion' -and $entry.minHostVersion) {
        $lines.Add('            "minHostVersion": ' + (ConvertTo-JsonString ([string]$entry.minHostVersion)))
    }
    if ($i -lt $entries.Count - 1) { $lines.Add('        },') } else { $lines.Add('        }') }
}
$lines.Add('    ]')
$lines.Add('}')
# No BOM: a BOM in a JSON asset has broken this repo's own readers before.
[IO.File]::WriteAllText($manifestPath, ($lines -join "`r`n") + "`r`n", (New-Object Text.UTF8Encoding($false)))

# READ BACK what was written and hold it against the source, BEFORE anything is committed. A source-text
# check that the hashtable above mentions minHostVersion would not prove the manifest carries it; the
# file does. This is the assertion that makes F216 -- a first publish silently dropping the floor the
# code declares -- a refusal at publish time rather than a generator throw after the commit, and it pins
# version and permissions the same way, in the script whose synopsis promises the catalog "cannot
# disagree with the code".
$writtenEntries = @((Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).modules | Where-Object { $_.id -eq $moduleId })
if ($writtenEntries.Count -ne 1) {
    throw "modules.json holds $($writtenEntries.Count) entries for '$moduleId' after the rewrite; expected exactly one."
}
$writtenEntry = $writtenEntries[0]
$writtenFloor = if ($writtenEntry.PSObject.Properties['minHostVersion']) { [string]$writtenEntry.minHostVersion } else { '' }
if ([string]$writtenEntry.version -ne $version -or [string]$writtenEntry.permissions -ne $permissions -or $writtenFloor -ne $minHostVersion) {
    throw (("modules.json was rewritten but does not say what {0} says: version '{1}' vs source '{2}', permissions '{3}' vs " +
            "source '{4}', minHostVersion '{5}' vs source '{6}'. Refusing to commit a manifest that disagrees with the code.") -f
            $moduleSource.Name, [string]$writtenEntry.version, $version, [string]$writtenEntry.permissions, $permissions,
            $writtenFloor, $minHostVersion)
}
Write-Host ("  read back: version {0}, permissions {1}, minHostVersion {2}" -f $version, $permissions,
            $(if ($minHostVersion) { $minHostVersion } else { '(none declared)' }))

# ---- 4. commit the zip (the catalog hashes the COMMITTED blob) ----
Write-Host ''
Write-Host '=== commit the payload' -ForegroundColor Cyan
Push-Location $repoRoot
try {
    if ($Commit) {
        Invoke-Git @('add', '--', $zipRelPath, 'modules-dist/modules.json') 'git add'
        # The exact PowerShell version goes in the body, so a catalog hash that moves on a republish with
        # no content change can be attributed to the runtime: System.IO.Compression's deflate output
        # differs between .NET 8, 9 and 10, i.e. between pwsh 7.4, 7.5 and 7.6 (F213). The subject keeps
        # its shape; nothing parses it, but the history reads the same as before.
        Invoke-Git @('commit', '-q', '-m', ("chore(modules): publish {0} {1}" -f $moduleId, $version),
                     '-m', ("Zipped under PowerShell {0}. The deflate bytes differ per .NET major, so a hash " +
                            "churn on a republish with no content change is the runtime, not the content." -f
                            $PSVersionTable.PSVersion)) 'git commit'
        Write-Host '  committed.'
    }

    # Whether or not we committed, the catalog may only be generated from a committed, up-to-date blob.
    $status = Invoke-Git @('status', '--porcelain', '--', $zipRelPath) 'git status'
    if ($status) {
        Write-Host ''
        Write-Host 'STOPPING BEFORE THE CATALOG.' -ForegroundColor Yellow
        Write-Host ("  {0} is not committed, and catalog.json records the hash of the COMMITTED blob." -f $zipRelPath)
        Write-Host '  Commit it, then regenerate the catalog:' -ForegroundColor Yellow
        Write-Host ("    git add {0} modules-dist/modules.json" -f $zipRelPath)
        Write-Host ("    git commit -m ""chore(modules): publish {0} {1}""" -f $moduleId, $version)
        Write-Host '    .\packaging\New-ContentCatalog.ps1'
        Write-Host '  (or re-run this script with -Commit to do all three.)'
        exit 3
    }

    # ---- 5. catalog ----
    Write-Host ''
    Write-Host '=== catalog.json' -ForegroundColor Cyan
    # NO $LASTEXITCODE GUARD, because it could never fire. $LASTEXITCODE is set by native commands
    # and by an explicit `exit`; New-ContentCatalog.ps1 contains zero `exit` statements and signals
    # every failure by throw, so the guard that used to sit here read whatever the last `git` call
    # INSIDE the callee happened to leave. $ErrorActionPreference = 'Stop' is set above, so a callee
    # throw escapes on its own -- the house form at tests\run-gate.ps1:160-161.
    & (Join-Path $PSScriptRoot 'New-ContentCatalog.ps1')

    Write-Host ''
    Write-Host '=== verify' -ForegroundColor Cyan
    # Same: Test-ModulePublishFreshness.ps1 carries 19 throw sites and no `exit`.
    & (Join-Path $PSScriptRoot 'Test-ModulePublishFreshness.ps1')

    Write-Host ''
    Write-Host ("PUBLISHED LOCALLY: {0} {1}." -f $moduleId, $version) -ForegroundColor Green
    Write-Host '  Commit catalog.json, then MERGE TO MASTER -- that is what makes it live for every user.'
}
finally {
    Pop-Location
}
