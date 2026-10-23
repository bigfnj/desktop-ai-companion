#requires -Version 7
<#
.SYNOPSIS
Remove regenerable binary files from explicitly completed worker build directories.
.EXAMPLE
pwsh -NoProfile -File tools/agentbroker/Remove-CompletedWorkerBinaries.ps1 -PlanPath artifacts/completed-workers.json
.DESCRIPTION
The JSON plan is {"Worktrees":["artifacts/old-run/w1"]}. Runs must already be
known to have finished. Default mode is read-only. Apply preserves every directory
and every file except the six explicit binary extensions. Source, logs, JSON,
and names suggesting original/snapshot/backup/raw-target evidence are preserved.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PlanPath,
    [switch]$Apply,
    [ValidateRange(1,168)][int]$MinimumAgeHours = 2
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-WorkerBinaryPath([string]$Path, [string]$TrustedRoot) {
    $full = [IO.Path]::GetFullPath($Path)
    foreach ($part in $full.Split([char[]]'\/')) {
        $part = $part.Split(':')[0].TrimEnd(' ', '.')
        if ($part -match '(?i)(\.env$|^\.env\.|^\.npmrc$|\.pem$|\.key$|\.p12$|\.pfx$|\.keystore$|^privatekey|^\.git$|^\.aws$|^\.ssh$|^\.kube$|^\.config$|^\.mozilla$|^User Data$|^Firefox$|^Keychains$)') {
            throw 'worker_binary_protected_path'
        }
    }
    if ($full -cnotmatch '^[A-Za-z]:\\' -or $full.IndexOf(':',2) -ge 0 -or
        -not ($full.Equals($TrustedRoot,[StringComparison]::OrdinalIgnoreCase) -or
            $full.StartsWith($TrustedRoot + '\',[StringComparison]::OrdinalIgnoreCase))) {
        throw 'worker_binary_path_outside_repository'
    }
    $current = [IO.Path]::GetPathRoot($full)
    foreach ($part in $full.Substring($current.Length).Split('\')) {
        if (-not $part) { continue }
        $current = Join-Path $current $part
        try { $attributes = [IO.File]::GetAttributes($current) }
        catch [IO.FileNotFoundException] { continue }
        catch [IO.DirectoryNotFoundException] { continue }
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'worker_binary_reparse_path' }
    }
    return $full
}

function Invoke-WorkerBinaryGit([string]$Repository, [string[]]$Arguments) {
    $output = @(& git -c core.fsmonitor=false -C $Repository @Arguments)
    if ($LASTEXITCODE -ne 0) { throw 'worker_binary_git_metadata_failed' }
    return $output
}

function Get-WorkerBinaryShortPath([string]$Path) {
    if (-not ('WorkerBinaryPaths' -as [type])) {
        Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
using System.Text;
public static class WorkerBinaryPaths {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern uint GetShortPathNameW(string path, StringBuilder result, uint length);
}
'@
    }
    $buffer = [Text.StringBuilder]::new(32768)
    $length = [WorkerBinaryPaths]::GetShortPathNameW($Path,$buffer,$buffer.Capacity)
    if ($length -eq 0 -or $length -ge $buffer.Capacity) { throw 'worker_binary_alias_unavailable' }
    return $buffer.ToString().TrimEnd('\')
}

function Test-WorkerBinaryImage([string]$Image, [string]$LongRoot, [string]$ShortRoot) {
    if ($Image -match '^\\\\\?\\[A-Za-z]:\\') { $Image = $Image.Substring(4) }
    $imageParts = [IO.Path]::GetFullPath($Image).Split('\')
    $longParts = $LongRoot.Split('\'); $shortParts = $ShortRoot.Split('\')
    if ($longParts.Count -ne $shortParts.Count) { throw 'worker_binary_alias_shape' }
    $matches = $imageParts.Count -gt $longParts.Count
    for ($index=0; $matches -and $index -lt $longParts.Count; $index++) {
        $matches = $imageParts[$index].Equals($longParts[$index],[StringComparison]::OrdinalIgnoreCase) -or
            $imageParts[$index].Equals($shortParts[$index],[StringComparison]::OrdinalIgnoreCase)
    }
    return $matches
}

function Get-WorkerBinaryImages {
    # Kernel image metadata only. Never resolve or open a foreign executable.
    return @(Get-CimInstance -Query 'SELECT ProcessId, ExecutablePath FROM Win32_Process')
}

function Assert-WorkerBinaryFileCurrent($File, [string]$TrustedRoot) {
    [void](Assert-WorkerBinaryPath $File.Path $TrustedRoot)
    $attributes = [IO.File]::GetAttributes($File.Path)
    $current = [IO.FileInfo]::new($File.Path)
    if (($attributes -band [IO.FileAttributes]::Directory) -ne 0 -or
        $current.Length -ne $File.Bytes -or $current.LastWriteTimeUtc.Ticks -ne $File.Ticks) {
        throw 'worker_binary_changed_since_plan'
    }
    return $attributes
}

function Remove-WorkerBinaryFile($File, [string]$TrustedRoot) {
    $attributes = Assert-WorkerBinaryFileCurrent $File $TrustedRoot
    if (($attributes -band [IO.FileAttributes]::ReadOnly) -ne 0) {
        [IO.File]::SetAttributes($File.Path,$attributes -band (-bnot [IO.FileAttributes]::ReadOnly))
    }
    # File.Delete refuses a directory. No recursive deletion or wildcard removal.
    [IO.File]::Delete($File.Path)
}

function Write-WorkerBinaryReceipt([string]$Path, $Receipt, [string]$TrustedRoot, [switch]$Create) {
    [void](Assert-WorkerBinaryPath $Path $TrustedRoot)
    $mode = if ($Create) { [IO.FileMode]::CreateNew } else { [IO.FileMode]::Truncate }
    $stream = [IO.FileStream]::new($Path,$mode,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try {
        $writer = [IO.StreamWriter]::new($stream,[Text.UTF8Encoding]::new($false))
        try { $writer.Write(($Receipt | ConvertTo-Json -Depth 5)) }
        finally { $writer.Dispose() }
    } finally { $stream.Dispose() }
}

function Invoke-CompletedWorkerBinaryCleanup {
    param([string]$RepositoryRoot, [string]$InputPlan, [switch]$Apply, [int]$MinimumAgeHours = 2)
    $repo = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\')
    [void](Assert-WorkerBinaryPath $repo $repo)
    if (-not [IO.Path]::IsPathFullyQualified($InputPlan)) { $InputPlan = Join-Path $repo $InputPlan }
    $inputPath = Assert-WorkerBinaryPath $InputPlan $repo
    $parsedPlan = [IO.File]::ReadAllText($inputPath) | ConvertFrom-Json
    if ($null -eq $parsedPlan -or -not ($parsedPlan.PSObject.Properties.Name -contains 'Worktrees')) { throw 'worker_binary_plan_shape' }
    $requested = @($parsedPlan.Worktrees)
    if ($requested.Count -lt 1 -or $requested.Count -gt 32) { throw 'worker_binary_plan_count' }
    $registered = @(Invoke-WorkerBinaryGit $repo @('worktree','list','--porcelain') |
        Where-Object { $_.StartsWith('worktree ') } | ForEach-Object { [IO.Path]::GetFullPath($_.Substring(9)) })
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $plans = [Collections.Generic.List[object]]::new()
    $files = [Collections.Generic.List[object]]::new()
    $cutoff = [DateTime]::UtcNow.AddHours(-$MinimumAgeHours)
    [long]$bytes = 0; [long]$preservedBytes = 0; $preservedFiles = 0
    foreach ($relative in $requested) {
        if ($relative -isnot [string] -or $relative -notmatch '^artifacts[\\/]' -or
            $relative -match '(^|[\\/])\.\.?([\\/]|$)' -or [IO.Path]::IsPathFullyQualified($relative)) { throw 'worker_binary_plan_path' }
        $worktree = Assert-WorkerBinaryPath (Join-Path $repo $relative) $repo
        if (-not $worktree.StartsWith((Join-Path $repo 'artifacts') + '\',[StringComparison]::OrdinalIgnoreCase) -or
            -not $seen.Add($worktree) -or -not ($registered -contains $worktree)) { throw 'worker_binary_unregistered_or_duplicate_worktree' }
        $tracked = @(Invoke-WorkerBinaryGit $worktree @('ls-files','--','build'))
        if ($tracked.Count -ne 0) { throw 'worker_binary_tracked_build_path' }
        $short = Get-WorkerBinaryShortPath $worktree
        $plan = [pscustomobject]@{ Worktree=$relative; LongRoot=$worktree; ShortRoot=$short; Files=0; Bytes=[long]0 }
        $plans.Add($plan)
        $build = Assert-WorkerBinaryPath (Join-Path $worktree 'build') $repo
        if (-not [IO.Directory]::Exists($build)) { continue }
        $pending = [Collections.Generic.Stack[string]]::new(); $pending.Push($build)
        while ($pending.Count) {
            $path = $pending.Pop()
            [void](Assert-WorkerBinaryPath $path $repo)
            $attributes = [IO.File]::GetAttributes($path)
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                foreach ($child in [IO.Directory]::EnumerateFileSystemEntries($path)) { $pending.Push($child) }
            } else {
                $file = [IO.FileInfo]::new($path)
                if ($file.Extension.ToLowerInvariant() -notin @('.dll','.exe','.pdb','.obj','.lib','.exp') -or
                    $path.Substring($build.Length) -match '(?i)(original|snapshot|backup|rawtarget|target-bytes)') {
                    $preservedFiles++; $preservedBytes += $file.Length; continue
                }
                if ($file.LastWriteTimeUtc -gt $cutoff) { throw 'worker_binary_recent_write' }
                $files.Add([pscustomobject]@{ Path=$path; Bytes=$file.Length; Ticks=$file.LastWriteTimeUtc.Ticks })
                $plan.Files++; $plan.Bytes += $file.Length; $bytes += $file.Length
            }
        }
    }
    $unknownImages = 0
    foreach ($image in @(Get-WorkerBinaryImages)) {
        if ([string]::IsNullOrWhiteSpace($image.ExecutablePath)) { $unknownImages++; continue }
        foreach ($plan in $plans) {
            if (Test-WorkerBinaryImage $image.ExecutablePath $plan.LongRoot $plan.ShortRoot) { throw 'worker_binary_live_image' }
        }
    }
    # Validate every observed candidate before deleting the first file.
    foreach ($file in $files) { [void](Assert-WorkerBinaryFileCurrent $file $repo) }
    $receipt = [ordered]@{ State='planned'; Apply=[bool]$Apply; StartedUtc=[DateTimeOffset]::UtcNow.ToString('o')
        Scope='binary files only; every directory and nonbinary file preserved'; Worktrees=@($plans.ToArray() | Select-Object Worktree,Files,Bytes)
        CandidateFiles=$files.Count; CandidateBytes=$bytes; PreservedFiles=$preservedFiles; PreservedBytes=$preservedBytes
        UnqueryableImageCount=$unknownImages; RemovedFiles=0; RemovedBytes=[long]0; ReceiptPath=$null }
    if ($Apply) {
        $receiptPath = Join-Path $repo ('artifacts\worker-binary-cleanup-' + [Guid]::NewGuid().ToString('N') + '.json')
        $receipt.ReceiptPath = $receiptPath; $receipt.State = 'removing'
        Write-WorkerBinaryReceipt $receiptPath $receipt $repo -Create
        try {
            foreach ($file in $files) {
                Remove-WorkerBinaryFile $file $repo
                $receipt.RemovedFiles++; $receipt.RemovedBytes += $file.Bytes
            }
            $receipt.State = 'removed'
        } catch {
            $receipt.State = 'partial'; $receipt.FailureType = $_.Exception.GetType().Name
            try { Write-WorkerBinaryReceipt $receiptPath $receipt $repo } catch { Write-Warning 'Partial cleanup receipt update failed; initial removing receipt remains.' }
            throw
        }
        Write-WorkerBinaryReceipt $receiptPath $receipt $repo
    }
    return [pscustomobject]$receipt
}

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')).TrimEnd('\')
Invoke-CompletedWorkerBinaryCleanup -RepositoryRoot $repository -InputPlan $PlanPath -Apply:$Apply -MinimumAgeHours $MinimumAgeHours |
    ConvertTo-Json -Depth 5
