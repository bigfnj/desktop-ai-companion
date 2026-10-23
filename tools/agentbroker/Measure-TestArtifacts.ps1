#requires -Version 7
<#
.SYNOPSIS
    Measure generated test artifacts without opening file contents or following links.
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Measure-TestArtifacts.ps1
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Measure-TestArtifacts.ps1 -Root artifacts/ms-example
.DESCRIPTION
    Read-only. Protected paths and Git metadata are excluded before attributes are
    queried. Results contain logical file sizes, not allocated disk usage. An
    incomplete bucket is never evidence that it is safe to delete recursively.
#>
[CmdletBinding()]
param([string]$Root = '', [ValidateRange(5, 300)][int]$MaximumSeconds = 180,
    [ValidateRange(1, 1000)][int]$Top = 25)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$workspace = Split-Path $repo -Parent
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Join-Path $repo 'artifacts' }
elseif (-not [IO.Path]::IsPathFullyQualified($Root)) { $Root = Join-Path $repo $Root }
$rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$allowedRoots = @((Join-Path $repo 'artifacts'), (Join-Path $workspace 'gi\build'),
    (Join-Path $workspace 'gp\artifacts'), (Join-Path $workspace 'gt\artifacts'))
if (-not @($allowedRoots | Where-Object {
    $rootPath.Equals($_, [StringComparison]::OrdinalIgnoreCase) -or
    $rootPath.StartsWith($_ + '\', [StringComparison]::OrdinalIgnoreCase)
}).Count) { throw 'Only the explicitly scoped inactive-lane artifact roots may be measured.' }

function Test-ExcludedPath([string]$Path) {
    foreach ($part in $Path.Split([char[]]'\/')) {
        $part = $part.Split(':')[0].TrimEnd(' ', '.')
        if ($part -match '(?i)(\.env$|^\.env\.|^\.npmrc$|\.pem$|\.key$|\.p12$|\.pfx$|\.keystore$|^privatekey|^\.git$|^\.aws$|^\.ssh$|^\.kube$|^User Data$|^Firefox$|^Keychains$)') {
            return $true
        }
    }
    return $false
}

if (Test-ExcludedPath $rootPath) { throw 'Protected artifact scope refused before access.' }
$current = [IO.Path]::GetPathRoot($rootPath)
foreach ($part in $rootPath.Substring($current.Length).Split('\')) {
    if (-not $part) { continue }
    $current = Join-Path $current $part
    if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Linked artifact scope refused before traversal.'
    }
}
$clock = [Diagnostics.Stopwatch]::StartNew()
$buckets = [Collections.Generic.List[object]]::new()
foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($rootPath)) {
    if ($clock.Elapsed.TotalSeconds -ge $MaximumSeconds) { break }
    $row = [ordered]@{ Name = [IO.Path]::GetFileName($entry); Bytes = [long]0; Files = 0;
        ProtectedSkipped = 0; LinksSkipped = 0; Errors = 0; Complete = $true; LatestWriteUtc = $null }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($entry)
    while ($pending.Count) {
        if ($clock.Elapsed.TotalSeconds -ge $MaximumSeconds) { $row.Complete = $false; break }
        $path = $pending.Pop()
        if (Test-ExcludedPath $path) { $row.ProtectedSkipped++; continue }
        try {
            $attributes = [IO.File]::GetAttributes($path)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $row.LinksSkipped++; continue }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                foreach ($child in [IO.Directory]::EnumerateFileSystemEntries($path)) { $pending.Push($child) }
            } else {
                $file = [IO.FileInfo]::new($path)
                $row.Bytes += $file.Length
                $row.Files++
                $stamp = $file.LastWriteTimeUtc.ToString('o')
                if ($null -eq $row.LatestWriteUtc -or $stamp -gt $row.LatestWriteUtc) { $row.LatestWriteUtc = $stamp }
            }
        } catch { $row.Errors++ }
    }
    $row.Complete = $row.Complete -and $row.ProtectedSkipped -eq 0 -and $row.LinksSkipped -eq 0 -and $row.Errors -eq 0
    $buckets.Add([pscustomobject]$row)
}
$clock.Stop()
[ordered]@{ Root = $rootPath; ReadOnly = $true; FileContentsOpened = $false;
    ElapsedSeconds = [math]::Round($clock.Elapsed.TotalSeconds, 2);
    TimeLimitReached = $clock.Elapsed.TotalSeconds -ge $MaximumSeconds;
    LogicalGiB = [math]::Round(($buckets | Measure-Object -Property Bytes -Sum).Sum / 1GB, 3);
    TotalBuckets = $buckets.Count;
    Buckets = @($buckets | Sort-Object Bytes -Descending | Select-Object -First $Top) } | ConvertTo-Json -Depth 4
