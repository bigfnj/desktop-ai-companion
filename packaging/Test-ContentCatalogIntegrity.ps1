#requires -Version 5
<#
.SYNOPSIS
    Every asset catalog.json offers must still hash to what the catalog says.

.DESCRIPTION
    The gap this closes, found by an audit on 2026-09-17: nothing verified catalog.json's asset
    hashes or its membership. Test-ModulePublishFreshness.ps1 checks app.version and the six module
    VERSIONS, and that is all. So editing any `Companions\<id>\animations.xml` or `packs\<id>.txt`
    and committing without re-running New-ContentCatalog.ps1 left a recorded sha256 that no longer
    matches the blob raw.githubusercontent.com serves -- and then every user's download of that asset
    is REFUSED by the app's own hash check, while every gate stays green.

    That is the worst shape a failure can have here: the app is right to refuse, the catalog is
    wrong, and the only signal is a user reporting that a companion will not download.

    Three properties, all against the COMMITTED blob rather than the working tree, because that is
    what raw serves and a checkout has CRLF where git stores LF (see ContentCatalogAssets.ps1):

      1. every catalog entry's sha256 and byte count match the asset
      2. every catalog entry's asset exists at all
      3. the catalog's MEMBERSHIP matches what is on disk, in both directions -- an asset added and
         never catalogued is invisible to users, and an asset removed while still catalogued is a
         download that 404s

    Property 3 is why this counts rather than iterating: a per-entry loop over a catalog that lost
    half its entries passes every assertion it runs.

.PARAMETER RepoRoot
    Defaults to the parent of this script's directory.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}
. (Join-Path $PSScriptRoot 'ContentCatalogAssets.ps1')

# EXECUTE THE TIMEOUT PATH, once, before anything depends on it.
#
# Get-CatalogAsset's $TimeoutMs exists, in its own words, "so the timeout path below is REACHABLE in
# a test. A 60-second default cannot be provoked in a gate, and an error path nobody has ever
# executed is a guess, not a safeguard." No caller or test ever passed it, so the kill and the
# refusal had never run.
#
# THIS USED TO PROVOKE IT WITH -TimeoutMs 1 AND THE COMMENT "git cat-file cannot finish that fast".
# That was a timing assumption about somebody else's binary, and it was not even the right one:
# Process.Start runs BEFORE $stdout.Wait($TimeoutMs), so a child finishing inside that gap leaves the
# async copy already complete and Wait returns true however small the budget. git lost that race on
# the dev box and won it once in CI, turning a docs-only commit red. A flaky gate is worse than no
# gate: it trains people to re-run.
#
# So the child is now SUBSTITUTED rather than out-run. $StallChild points at a sleep that outlasts
# the budget by 20x, and the Wait-timeout, the Kill and the refusal all run for real with nothing
# racing. The assertion is on the MESSAGE, not merely that something threw -- a wrong path or an
# unreadable repo also throws, and would have made this look like a pass. The negative control
# matters as much: the same call at the real default, against real git, must SUCCEED, or this would
# pass on a file that simply cannot be read.
$timeoutProbeRel = 'packs/collections.json'
# From $RepoRoot, so a -RepoRoot run probes the repository it was asked about rather than the one
# this script happens to live in.
$timeoutProbeFull = Join-Path $RepoRoot 'packs\collections.json'
# A MISSING PROBE FILE IS A THROW, NOT A SKIP. The whole probe used to sit behind `if (Test-Path)` with
# no else, so a renamed collections.json turned the only execution of the timeout path into a silent
# pass, in a file that refuses on every other narrowed run (F221). The throw is the only gate now: the
# `if (Test-Path)` that F221 left standing beneath it could no longer be false, and a wrapper that reads
# like a skip is the shape a later "tidy" would restore the skip into (R-051).
if (-not (Test-Path -LiteralPath $timeoutProbeFull -PathType Leaf)) {
    throw "packs\collections.json is missing at $timeoutProbeFull, so the catalog-asset timeout path cannot be exercised."
}
$repoRootForProbe = $RepoRoot
$probeThrew = ''
$stallSeconds = 10
$stallBudgetMs = 500
$stall = @((Get-Process -Id $PID).Path, '-NoProfile', '-Command', "Start-Sleep -Seconds $stallSeconds")
$stallWatch = [Diagnostics.Stopwatch]::StartNew()
try {
    $null = Get-CatalogAsset $repoRootForProbe $timeoutProbeRel $timeoutProbeFull $stallBudgetMs $stall
    $probeThrew = '(did not throw)'
}
catch { $probeThrew = $_.Exception.Message }
$stallWatch.Stop()
# It must have GIVEN UP, not waited the child out. Without this the check would still pass if the
# timeout were removed and the call simply blocked for the full sleep.
if ($stallWatch.Elapsed.TotalSeconds -ge $stallSeconds) {
    throw ("Get-CatalogAsset waited {0:N1}s for a child told to sleep {1}s at a {2}ms budget. " -f
               $stallWatch.Elapsed.TotalSeconds, $stallSeconds, $stallBudgetMs) +
          'It rode the child out instead of timing out, so the bound is not doing anything.'
}
# BOTH halves of the message. The refusal ("Refusing to fall back") is the load-bearing half, and it was
# the only one asserted; since F209 EVERY failure kind ends in that sentence, so a stall child that died
# at once, answered garbage or never started satisfied the probe while the ReadAsync wait, the Kill and
# the timeout branch never ran (RA-194). The timeout branch alone says "did not return within <n>ms", so
# that clause proves WHICH path fired. The word "timeout" is still not matched: a detector looking for it
# once reported a correctly-firing path as broken, because the message never contained it.
if ($probeThrew -notmatch 'did not return within' -or $probeThrew -notmatch 'Refusing to fall back') {
    throw ("Get-CatalogAsset's timeout path did not report a timeout at -TimeoutMs $stallBudgetMs; it said: " +
           $probeThrew + ". That path is the only thing standing between a hung git and a silent " +
           "fallback to the worktree, so it must not be left unexecuted.")
}
# WITNESS for the clause above: a child that exits before answering is a DIFFERENT failure, refused
# through the generic branch, and must not read as the timeout. Drives the EOF path for real.
$deadThrew = ''
try {
    $null = Get-CatalogAsset $repoRootForProbe $timeoutProbeRel $timeoutProbeFull $stallBudgetMs @('cmd.exe', '/c', 'exit 1')
    $deadThrew = '(did not throw)'
}
catch { $deadThrew = $_.Exception.Message }
if ($deadThrew -notmatch 'exited or closed its output' -or $deadThrew -match 'did not return within' -or
    $deadThrew -notmatch 'Refusing to fall back') {
    throw ("Get-CatalogAsset did not refuse a child that exited before answering as a dead child; it said: " +
           $deadThrew + ". If this reads as a timeout, the two branches are no longer told apart.")
}

$catalogPath = Join-Path $RepoRoot 'catalog.json'

# The catalog records absolute raw URLs; the repo-relative path is everything after the branch. Taken
# from the URL rather than rebuilt from the id, so a wrong PATH in the catalog is caught too -- which
# a path rebuilt from the id by construction cannot be.
$rawPrefix = 'https://raw.githubusercontent.com/bigfnj/desktop-ai-companion/master/'

function Get-RelativePathFromUrl([string]$Url) {
    if ([string]::IsNullOrWhiteSpace($Url)) { return $null }
    if (-not $Url.StartsWith($rawPrefix, [StringComparison]::Ordinal)) { return $null }
    # FORWARD slashes, unchanged: this string goes to `git cat-file blob HEAD:<path>`. Converting it
    # to a Windows path made cat-file fail on every asset, so Get-CatalogAsset fell back to
    # CR-stripped working-tree bytes and this script reported five of the six module zips as
    # mismatched on its first run. Join-Path handles the separator for the on-disk check below.
    return $Url.Substring($rawPrefix.Length)
}

$checked = 0
$problems = New-Object 'System.Collections.Generic.List[string]'

# try/finally from the FIRST cached child to the end of the loop: Get-CatalogAsset keeps one
# `git cat-file --batch` child open across every asset (F208), and a throw anywhere while it is open
# must not leave it holding the pack files for the rest of the gate. The two probe children above are
# one-offs, closed by Get-CatalogAsset itself; the negative control below is the first call that CACHES
# a child, and it sat before this try with the catalog.json read between them, so a missing or
# malformed catalog left an orphaned git child pinning .git\objects\pack until the gate process exited
# (RA-195).
try {
    # Negative control: the same asset at the real default must read fine AND from the committed blob.
    # `$null -eq $control` could never be true (the reader returns an object or throws) and it accepted
    # the worktree fallback, which for a verifier is the wrong answer; the loop below refuses it per
    # asset, so the control refuses it too (RA-194).
    $control = Get-CatalogAsset $repoRootForProbe $timeoutProbeRel $timeoutProbeFull
    if ($control.Source -ne 'blob') {
        throw ("Get-CatalogAsset read packs/collections.json from the '$($control.Source)' source at the default " +
               "timeout; a verifier accepts the committed blob only.")
    }
    Write-Host ("  ok   the catalog-asset timeout path refuses rather than falling back (timeout and dead child " +
                "told apart; the default reads the committed blob)")

    if (-not (Test-Path -LiteralPath $catalogPath -PathType Leaf)) {
        throw "catalog.json is missing: $catalogPath"
    }
    $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json

foreach ($group in @(
        @{ Name = 'companion'; Items = @($catalog.companions) },
        @{ Name = 'pack';      Items = @($catalog.packs) },
        @{ Name = 'module';    Items = @($catalog.modules) })) {
    foreach ($item in $group.Items) {
        if ($null -eq $item) { continue }
        $id = [string]$item.id
        $relative = Get-RelativePathFromUrl ([string]$item.url)
        if ($null -eq $relative) {
            $problems.Add("$($group.Name) '$id': url is not a raw.githubusercontent path for this repo ($($item.url))")
            continue
        }
        $full = Join-Path $RepoRoot $relative
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            $problems.Add("$($group.Name) '$id': catalog offers $relative, which does not exist")
            continue
        }
        $asset = Get-CatalogAsset $RepoRoot $relative $full
        $checked++
        # A verifier must not accept the working-tree fallback. It exists for the generator's sake --
        # a brand-new asset is not committed yet -- and for a binary it yields a wrong hash, so
        # accepting it here would compare the catalog against bytes raw.githubusercontent.com never
        # serves.
        if ($asset.Source -ne 'blob') {
            $problems.Add(
                "$($group.Name) '$id': $relative is not committed, so the catalog's hash cannot be " +
                "verified against what raw.githubusercontent.com would serve. Commit it, then " +
                "re-run packaging\New-ContentCatalog.ps1.")
            continue
        }
        if ($asset.Sha256 -ne ([string]$item.sha256).ToLowerInvariant()) {
            $problems.Add(
                "$($group.Name) '$id': sha256 mismatch on $relative -- catalog says " +
                "$(([string]$item.sha256).Substring(0, 12))..., the committed blob is " +
                "$($asset.Sha256.Substring(0, 12))...")
        }
        elseif ([int]$item.bytes -ne [int]$asset.Bytes) {
            $problems.Add(
                "$($group.Name) '$id': byte count mismatch on $relative -- catalog says " +
                "$($item.bytes), the committed blob is $($asset.Bytes)")
        }
    }
}
}
finally { Stop-CatalogAssetBatch }

# ---- membership, both directions --------------------------------------------------------------
# A catalogued asset that no longer exists is caught above. This is the other half: an asset that
# exists and is NOT catalogued, which is invisible to every user and fails nothing.
$diskCompanions = @(
    Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'Companions') -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'animations.xml') -PathType Leaf } |
        ForEach-Object { $_.Name.ToLowerInvariant() })
$catalogCompanions = @(@($catalog.companions) | ForEach-Object { ([string]$_.id).ToLowerInvariant() })
$missingCompanions = @($diskCompanions | Where-Object { $catalogCompanions -notcontains $_ })
if ($missingCompanions.Count -gt 0) {
    $problems.Add(
        "$($missingCompanions.Count) companion(s) exist under Companions\ but are absent from " +
        "catalog.json, so no user is offered them: " + (($missingCompanions | Sort-Object) -join ', ') +
        ". Re-run packaging\New-ContentCatalog.ps1.")
}

$diskPacks = @(
    Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'packs') -Filter '*.txt' -File |
        ForEach-Object { $_.BaseName.ToLowerInvariant() })
$catalogPacks = @(@($catalog.packs) | ForEach-Object { ([string]$_.id).ToLowerInvariant() })
$missingPacks = @($diskPacks | Where-Object { $catalogPacks -notcontains $_ })
if ($missingPacks.Count -gt 0) {
    $problems.Add(
        "$($missingPacks.Count) fortune pack(s) exist under packs\ but are absent from catalog.json: " +
        (($missingPacks | Sort-Object) -join ', ') + ". Re-run packaging\New-ContentCatalog.ps1.")
}

# MODULES, the third direction, which was missing while the .DESCRIPTION above claimed all three.
# A zip sitting in modules-dist\ and absent from catalog.json is offered to nobody and fails nothing:
# the loop higher up catches a catalogued module whose zip has gone, but not the reverse. Note the
# publish path already refuses a modules.json entry with no zip (New-ContentCatalog.ps1:207) and a
# module source dir absent from modules.json (Test-ModulePublishFreshness.ps1:199) -- this closes the
# one remaining corner, a zip in neither manifest.
$diskModuleZips = @(
    Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'modules-dist') -Filter '*.zip' -File `
        -ErrorAction SilentlyContinue |
        ForEach-Object { $_.BaseName.ToLowerInvariant() })
$catalogModules = @(@($catalog.modules) | ForEach-Object { ([string]$_.id).ToLowerInvariant() })
$missingModules = @($diskModuleZips | Where-Object { $catalogModules -notcontains $_ })
if ($missingModules.Count -gt 0) {
    $problems.Add(
        "$($missingModules.Count) module zip(s) exist under modules-dist\ but are absent from " +
        "catalog.json, so no user is offered them: " + (($missingModules | Sort-Object) -join ', ') +
        ". Re-run packaging\New-ContentCatalog.ps1, or delete the stray zip.")
}

# The count floor. Without it, a catalog that lost every entry would pass every loop above by
# iterating nothing -- the failure this file exists to make impossible.
if ($checked -lt 1) {
    throw ("catalog.json produced no verifiable assets at all, so nothing above ran. " +
           "That is a broken catalog, not a clean run.")
}

if ($problems.Count -gt 0) {
    throw ("catalog.json does not describe the committed content:" + [Environment]::NewLine +
           ($problems -join [Environment]::NewLine))
}

Write-Host ("Catalog integrity OK: $checked asset(s) hash to their catalog entry, " +
            "$($diskCompanions.Count) companions and $($diskPacks.Count) packs all catalogued.") `
    -ForegroundColor DarkGray
