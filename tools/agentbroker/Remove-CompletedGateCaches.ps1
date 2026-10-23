#requires -Version 7
<#
.SYNOPSIS
    Reclaim SDK caches from explicitly identified completed isolated gate runs.
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Remove-CompletedGateCaches.ps1 -CompletedRunIds 1234abcd,5678abcd
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Remove-CompletedGateCaches.ps1 -CompletedRunIds 1234abcd -Apply
.DESCRIPTION
    The caller must establish that the selected gate runs have finished. Age alone
    does not establish this. Default mode measures only. Apply removes only each
    selected run's dotnet-home directory, preserving its other artifacts. All
    candidates are validated before any deletion. Protected paths, links, recent
    writes and running images inside a selected run cause a refusal.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{8}(,[a-f0-9]{8})*$')][string]$CompletedRunIds,
    [switch]$Apply,
    [ValidateRange(0, 168)][int]$MinimumAgeHours = 2
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$gateRoot = Join-Path $repo 'artifacts\isolated-gate'
$cutoff = [DateTime]::UtcNow.AddHours(-$MinimumAgeHours)
$ids = @($CompletedRunIds.Split(',') | Select-Object -Unique)
if ($ids.Count -gt 64) { throw 'A cleanup is bounded to 64 explicitly completed runs.' }

function Assert-OrdinaryPath([string]$Path, [bool]$AllowMissingLeaf = $false) {
    $full = [IO.Path]::GetFullPath($Path)
    foreach ($part in $full.Split([char[]]'\/')) {
        $part = $part.Split(':')[0].TrimEnd(' ', '.')
        if ($part -match '(?i)(\.env$|^\.env\.|^\.npmrc$|\.pem$|\.key$|\.p12$|\.pfx$|\.keystore$|^privatekey|^\.git$|^\.aws$|^\.ssh$|^\.kube$|^User Data$|^Firefox$|^Keychains$)') {
            throw 'Protected path refused before access; no candidate was removed.'
        }
    }
    if (-not $full.StartsWith($gateRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Cleanup path escaped the isolated gate root.'
    }
    $current = [IO.Path]::GetPathRoot($full)
    foreach ($part in $full.Substring($current.Length).Split('\')) {
        if (-not $part) { continue }
        $current = Join-Path $current $part
        try { $attributes = [IO.File]::GetAttributes($current) }
        catch [IO.FileNotFoundException] {
            if ($AllowMissingLeaf -and $current -ceq $full) { return }
            throw
        }
        catch [IO.DirectoryNotFoundException] {
            if ($AllowMissingLeaf -and $current -ceq $full) { return }
            throw
        }
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Linked cache path refused; no candidate was removed.'
        }
    }
}

function Test-RunImagePath([string]$ImagePath, [string]$LongRunRoot, [string]$ShortRunRoot) {
    if ($ImagePath -match '^\\\\\?\\[A-Za-z]:\\') { $ImagePath = $ImagePath.Substring(4) }
    $imageParts = [IO.Path]::GetFullPath($ImagePath).Split('\')
    $longParts = $LongRunRoot.Split('\')
    $shortParts = $ShortRunRoot.Split('\')
    if ($longParts.Count -ne $shortParts.Count) { throw 'Run alias shape could not be verified.' }
    $matches = $imageParts.Count -gt $longParts.Count
    for ($index = 0; $matches -and $index -lt $longParts.Count; $index++) {
        $matches = $imageParts[$index].Equals($longParts[$index], [StringComparison]::OrdinalIgnoreCase) -or
            $imageParts[$index].Equals($shortParts[$index], [StringComparison]::OrdinalIgnoreCase)
    }
    return $matches
}

$plans = [Collections.Generic.List[object]]::new()
foreach ($id in $ids) {
    $runRoot = Join-Path $gateRoot $id
    $candidate = Join-Path $runRoot 'dotnet-home'
    Assert-OrdinaryPath $runRoot
    Assert-OrdinaryPath $candidate $true
    if (-not [IO.Directory]::Exists($candidate)) { continue }
    $files = [Collections.Generic.List[object]]::new()
    $directories = [Collections.Generic.List[string]]::new()
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($candidate)
    [long]$bytes = 0
    while ($pending.Count) {
        $path = $pending.Pop()
        Assert-OrdinaryPath $path
        $attributes = [IO.File]::GetAttributes($path)
        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
            $directories.Add($path)
            foreach ($child in [IO.Directory]::EnumerateFileSystemEntries($path)) { $pending.Push($child) }
        } else {
            $file = [IO.FileInfo]::new($path)
            if ($file.LastWriteTimeUtc -gt $cutoff) { throw 'Recent cache write refused; no candidate was removed.' }
            $files.Add([pscustomobject]@{ Path = $path; Length = $file.Length; Ticks = $file.LastWriteTimeUtc.Ticks })
            $bytes += $file.Length
        }
    }
    $plans.Add([pscustomobject]@{ RunId = $id; RunRoot = $runRoot; Candidate = $candidate;
        Bytes = $bytes; Files = $files; Directories = $directories })
}

if ($plans.Count -eq 0) {
    [ordered]@{ Apply = [bool]$Apply; Runs = @(); LogicalGiB = 0 } | ConvertTo-Json -Depth 4
    return
}

# Kernel metadata only. No command lines, environment, history or profile reads.
Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
using System.Text;
public static class GateCachePaths {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern uint GetShortPathNameW(string path, StringBuilder result, uint length);
}
'@
foreach ($plan in $plans) {
    $short = [Text.StringBuilder]::new(32768)
    $length = [GateCachePaths]::GetShortPathNameW($plan.RunRoot, $short, $short.Capacity)
    if ($length -eq 0 -or $length -ge $short.Capacity) { throw 'Could not verify the selected run path alias.' }
    $plan | Add-Member -NotePropertyName ShortRunRoot -NotePropertyValue $short.ToString().TrimEnd('\')
    if ($Apply) { Assert-OrdinaryPath (Join-Path $plan.RunRoot 'cache-cleanup.json') $true }
}
$images = @(Get-CimInstance -Query 'SELECT ProcessId, ExecutablePath FROM Win32_Process')
foreach ($image in $images) {
    if ([string]::IsNullOrWhiteSpace($image.ExecutablePath)) { continue }
    foreach ($plan in $plans) {
        # Each component may use its long or 8.3 spelling independently. Compare
        # metadata strings only; never resolve or open a foreign image's path.
        if (Test-RunImagePath $image.ExecutablePath $plan.RunRoot $plan.ShortRunRoot) {
            throw 'A selected run still has a running image; no candidate was removed.'
        }
    }
}
foreach ($plan in $plans) {
    # Revalidate the complete observed file set before mutating any candidate.
    foreach ($file in $plan.Files) {
        Assert-OrdinaryPath $file.Path
        $now = [IO.FileInfo]::new($file.Path)
        if ($now.Length -ne $file.Length -or $now.LastWriteTimeUtc.Ticks -ne $file.Ticks) {
            throw 'Cache changed during planning; no candidate was removed.'
        }
    }
}
$results = [Collections.Generic.List[object]]::new()
foreach ($plan in $plans) {
    if ($Apply) {
        $evidence = Join-Path $plan.RunRoot 'cache-cleanup.json'
        [IO.File]::WriteAllText($evidence, ([ordered]@{ RunId = $plan.RunId;
            StartedUtc = [DateTimeOffset]::UtcNow.ToString('o'); Bytes = $plan.Bytes;
            Files = $plan.Files.Count; Scope = 'dotnet-home only'; State = 'removing' } | ConvertTo-Json),
            [Text.UTF8Encoding]::new($false))
        # Delete only prevalidated ordinary files; a new/unexpected child makes
        # nonrecursive directory deletion fail rather than expanding the scope.
        foreach ($file in $plan.Files) {
            Assert-OrdinaryPath $file.Path
            $attributes = [IO.File]::GetAttributes($file.Path)
            $now = [IO.FileInfo]::new($file.Path)
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0 -or
                $now.Length -ne $file.Length -or $now.LastWriteTimeUtc.Ticks -ne $file.Ticks) {
                throw 'Cache file changed before removal; remaining files were preserved.'
            }
            if (($attributes -band [IO.FileAttributes]::ReadOnly) -ne 0) {
                [IO.File]::SetAttributes($file.Path, $attributes -band (-bnot [IO.FileAttributes]::ReadOnly))
            }
            [IO.File]::Delete($file.Path)
        }
        foreach ($directory in ($plan.Directories | Sort-Object Length -Descending)) {
            Assert-OrdinaryPath $directory
            [IO.Directory]::Delete($directory, $false)
        }
        [IO.File]::WriteAllText($evidence, ([ordered]@{ RunId = $plan.RunId;
            CompletedUtc = [DateTimeOffset]::UtcNow.ToString('o'); Bytes = $plan.Bytes;
            Files = $plan.Files.Count; Scope = 'dotnet-home only'; State = 'removed' } | ConvertTo-Json),
            [Text.UTF8Encoding]::new($false))
    }
    $results.Add([pscustomobject]@{ RunId = $plan.RunId; Bytes = $plan.Bytes;
        Files = $plan.Files.Count; Removed = [bool]$Apply })
}
[ordered]@{ Apply = [bool]$Apply; Runs = @($results);
    LogicalGiB = [math]::Round(($results | Measure-Object Bytes -Sum).Sum / 1GB, 3) } | ConvertTo-Json -Depth 4
