#requires -Version 5
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

$permissionsMatch = [regex]::Match($sourceText, 'Permissions\s*=\s*([^,;]+?)\s*,?\s*(?:\r?\n\s*\})')
if (-not $permissionsMatch.Success) {
    $permissionsMatch = [regex]::Match($sourceText, 'Permissions\s*=\s*([^,;]+?)\s*,\s*\r?\n')
}
$permissions = ''
if ($permissionsMatch.Success) {
    # "ModulePermissions.Pets | ModulePermissions.Storage" -> "Pets, Storage"
    $permissions = (($permissionsMatch.Groups[1].Value -replace 'ModulePermissions\.', '') -split '\|' |
        ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -ne 'None' }) -join ', '
}

# Publish AFTER the module's source is committed, never before. Test-ModulePublishFreshness compares commit
# RECENCY, so a zip committed ahead of the source it was built from reads as stale even though its bytes are
# correct -- and because the zip is deterministic, re-zipping then produces identical bytes, leaving no new
# commit available to fix the ordering. The only ways out are rewriting history or a dummy commit, so refuse
# up front instead. (This bit me publishing the ModuleKit migration.)
Push-Location $repoRoot
# The parentheses around the concatenation are load-bearing: without them PowerShell splits
# 'modules/' + $moduleDir.Name into TWO array elements, so git received the pathspecs `modules/` and
# `AiBrain` instead of `modules/AiBrain`. That made this guard fire on an uncommitted change in ANY
# module and then blame it on the one being published -- publishing aibrain refused because
# modules/PetStudio/PetStudio.csproj was dirty, reported as "modules/AiBrain has uncommitted changes".
try { $uncommittedSource = @(Invoke-Git @('status', '--porcelain', '--', ('modules/' + $moduleDir.Name)) 'git status') }
finally { Pop-Location }
if ($uncommittedSource.Count -gt 0) {
    Write-Host ''
    Write-Host ("modules/{0} has uncommitted changes:" -f $moduleDir.Name) -ForegroundColor Yellow
    foreach ($line in $uncommittedSource) { Write-Host ("    " + $line) }
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
    if ($Name) { $existing.name = $Name }
    if ($Description) { $existing.desc = $Description }
} else {
    # A first publish needs the catalog-facing copy, which no compiled DLL can supply.
    if (-not $Name -or -not $Description) {
        throw ("'$moduleId' is not in modules.json yet. A first publish needs -Name and -Description " +
               '(they are shown in the Modules pane before download and cannot be read from the DLL).')
    }
    Write-Host ("  adding a new entry for {0}" -f $moduleId)
    $entries += [pscustomobject][ordered]@{
        id          = $moduleId
        name        = $Name
        desc        = $Description
        version     = $version
        permissions = $permissions
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
    $lines.Add('            "permissions": ' + (ConvertTo-JsonString ([string]$entry.permissions)))
    if ($i -lt $entries.Count - 1) { $lines.Add('        },') } else { $lines.Add('        }') }
}
$lines.Add('    ]')
$lines.Add('}')
# No BOM: a BOM in a JSON asset has broken this repo's own readers before.
[IO.File]::WriteAllText($manifestPath, ($lines -join "`r`n") + "`r`n", (New-Object Text.UTF8Encoding($false)))

# ---- 4. commit the zip (the catalog hashes the COMMITTED blob) ----
Write-Host ''
Write-Host '=== commit the payload' -ForegroundColor Cyan
Push-Location $repoRoot
try {
    if ($Commit) {
        Invoke-Git @('add', '--', $zipRelPath, 'modules-dist/modules.json') 'git add'
        Invoke-Git @('commit', '-q', '-m', ("chore(modules): publish {0} {1}" -f $moduleId, $version)) 'git commit'
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
    & (Join-Path $PSScriptRoot 'New-ContentCatalog.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'The catalog generator failed.' }

    Write-Host ''
    Write-Host '=== verify' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'Test-ModulePublishFreshness.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'The publish-freshness check failed.' }

    Write-Host ''
    Write-Host ("PUBLISHED LOCALLY: {0} {1}." -f $moduleId, $version) -ForegroundColor Green
    Write-Host '  Commit catalog.json, then MERGE TO MASTER -- that is what makes it live for every user.'
}
finally {
    Pop-Location
}
