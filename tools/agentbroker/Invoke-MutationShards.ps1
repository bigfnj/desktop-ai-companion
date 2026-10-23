#requires -Version 7
<#
.SYNOPSIS
Run a complete guard or AgentFlow mutation inventory in isolated, pinned worktrees.
.EXAMPLE
pwsh -NoProfile -File tools/agentbroker/Invoke-MutationShards.ps1 -SelfTest
.EXAMPLE
pwsh -NoProfile -File tools/agentbroker/Invoke-MutationShards.ps1 -Commit <full-commit> -Shards 4 -MaxParallel 2
.EXAMPLE
pwsh -NoProfile -File tools/agentbroker/Invoke-MutationShards.ps1 -Commit <full-commit> -Suite AgentFlow -Shards 4 -MaxParallel 2
.EXAMPLE
pwsh -NoProfile -File tools/agentbroker/Invoke-MutationShards.ps1 -Commit <same-full-commit> -RemainderPool <absolute-owned-pool> -CompletedShards 1 -Shards 8 -RunShards '2,3,4,6,7,8' -MaxParallel 2
.DESCRIPTION
Each shard runs its full baseline and final strict rebuild. No actual keyboard
input is permitted. Logs and worktrees are retained, including failed runs.
This controller never installs tools, changes machine settings, or removes trees.
Remainder mode is read-only unless -ApplyRemainder is explicit. It imports only
fully verified completed shards, requires a separately retired-pool handoff and
the expected controller PID/creation FILETIME before starting fresh worktrees.
It never terminates the original controller. Handoff evidence is not permission
to stop a process. All imported and scheduled cases must cover the same pinned
inventory exactly once; /4 shard 1 plus /8 shards 2,3,4,6,7,8 is one valid union.
These options affect the new parent scheduler only. Each worker runs the original
controller from its own pinned worktree, including that commit's unchanged build,
baseline, mutation and restoration code. The active pool's files are not edited.
#>
[CmdletBinding()]
param(
    [string]$Commit,
    [ValidateSet('Guards', 'AgentFlow')][string]$Suite = 'Guards',
    [ValidateRange(1, 32)][int]$Shards = 4,
    [ValidateRange(1, 2)][int]$MaxParallel = 2,
    [ValidateRange(1, 1440)][int]$ShardTimeoutMinutes = 360,
    [switch]$SelfTest,
    [switch]$CheckCheckoutPolicy,
    [string]$RemainderPool,
    [string]$CompletedShards = '',
    [string]$RunShards = '',
    [switch]$ApplyRemainder,
    [ValidateRange(0, 2147483647)][int]$HandoffControllerId = 0,
    [string]$HandoffControllerCreationFileTime,
    [Parameter(DontShow)][switch]$LibraryOnly,
    [Parameter(DontShow)][switch]$Worker,
    [Parameter(DontShow)][ValidateRange(0, 32)][int]$ShardIndex = 0
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$guardWithheldIds = @(
    'the SendInput P/Invoke resolves against a DLL that does not exist',
    "the self-test's pairing keypress is deleted"
)
function Get-SuitePolicy([ValidateSet('Guards', 'AgentFlow')][string]$Name) {
    if ($Name -eq 'AgentFlow') { return @{ Harness = 'tests/mutate-agentflow.py'; WithheldIds = @() } }
    return @{ Harness = 'tests/mutate-selftest-guards.py'; WithheldIds = $guardWithheldIds }
}
$policy = Get-SuitePolicy $Suite
$harness = $policy.Harness
$withheldIds = $policy.WithheldIds
$utf8 = [Text.UTF8Encoding]::new($false, $true)

function Assert-Fixed([bool]$Condition, [string]$Code) { if (-not $Condition) { throw $Code } }
function Fixed-FailureCode($Failure) {
    $message = $Failure.Exception.Message
    if ($message -cmatch '^[a-z][a-z0-9_]{2,80}$') { return $message }
    return 'controller_exception'
}
function Assert-InertFilterConfiguration([string]$Text, [int]$ExitCode) {
    if ($ExitCode -eq 1) { Assert-Fixed ($Text.Length -eq 0) 'checkout_configuration_invalid'; return 0 }
    Assert-Fixed ($ExitCode -eq 0) 'checkout_configuration_unavailable'
    $standard = @{
        'filter.lfs.clean' = 'git-lfs clean -- %f'
        'filter.lfs.smudge' = 'git-lfs smudge -- %f'
        'filter.lfs.process' = 'git-lfs filter-process'
        'filter.lfs.required' = 'true'
    }
    $entries = @($Text.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries))
    Assert-Fixed ($entries.Count -gt 0 -and $entries.Count -le 32 -and $Text.EndsWith([string][char]0, [StringComparison]::Ordinal)) 'checkout_configuration_invalid'
    foreach ($entry in $entries) {
        $split = $entry.IndexOf("`n", [StringComparison]::Ordinal)
        Assert-Fixed ($split -gt 0) 'checkout_configuration_invalid'
        $key = $entry.Substring(0, $split)
        $value = $entry.Substring($split + 1)
        Assert-Fixed ($standard.ContainsKey($key) -and $key -ceq $key.ToLowerInvariant() -and
            $value -ceq $standard[$key]) 'custom_checkout_configuration_refused'
    }
    return $entries.Count
}
function Assert-NoFilterAttributes([string]$Text) {
    Assert-Fixed ($Text.Length -le 1MB) 'attribute_blob_oversized'
    foreach ($line in $Text.TrimStart([char]0xfeff).Split("`n")) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#', [StringComparison]::Ordinal)) { continue }
        # Fail closed on quoting/escaping rather than approximating Git's C-style
        # quoted-pattern grammar. Ordinary wildcard patterns remain supported.
        Assert-Fixed ($trimmed -notmatch '["\\\x00-\x08\x0b\x0c\x0e-\x1f\x7f]') 'ambiguous_attributes_refused'
        $tokens = [regex]::Split($trimmed, '[ \t]+')
        foreach ($attribute in @($tokens | Select-Object -Skip 1)) {
            # This also covers macro definitions, wildcard patterns, unsetting,
            # unspecified values and filters activated via a nested attribute file.
            Assert-Fixed ($attribute -notmatch '^(?:[-!])?filter(?:=|$)') 'checkout_filter_refused'
        }
    }
}
function Assert-PinnedAttributeBlobs([string[]]$Names, [scriptblock]$ReadBlob) {
    $checked = 0
    foreach ($attribute in $Names.Where({ (Split-Path $_ -Leaf) -eq '.gitattributes' })) {
        Assert-PublicRelativePath $attribute
        Assert-NoFilterAttributes (& $ReadBlob $attribute)
        $checked++
    }
    return $checked
}
function Assert-PublicRelativePath([string]$Name) {
    Assert-Fixed (-not [string]::IsNullOrWhiteSpace($Name)) 'tracked_name_empty'
    $parts = $Name.Replace('\', '/').Split('/')
    Assert-Fixed (-not [IO.Path]::IsPathRooted($Name) -and $Name -notmatch '[:\x00-\x1f\x7f]' -and
        -not ($parts | Where-Object { $_ -in @('', '.', '..') -or $_ -match '[. ]$' })) 'tracked_name_unsafe'
    foreach ($part in $parts) {
        Assert-Fixed ($part -notmatch '(?i)(\.env$|^\.env\.|^\.npmrc$|\.(pem|key|p12|pfx|keystore)$|^privatekey)') 'protected_name_refused'
        Assert-Fixed ($part -notmatch '(?i)^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)') 'tracked_name_device'
    }
    Assert-Fixed ($Name.Replace('\', '/') -notmatch '(?i)(^|/)\.git/credentials(?:/|$)') 'protected_name_refused'
}
function Assert-NoReparse([string]$Path, [scriptblock]$ReadAttributes = { param($Target) [IO.File]::GetAttributes($Target) }) {
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    Assert-Fixed ($root -cmatch '^[A-Za-z]:\\$') 'nonlocal_path_refused'
    Assert-PublicRelativePath ($full.Substring($root.Length).Replace('\', '/'))
    $current = $root
    foreach ($part in $full.Substring($root.Length).Split([IO.Path]::DirectorySeparatorChar, [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $part
        # Exists() follows a link and can report false for a dangling junction.
        # Inspect the component itself; only genuine absence permits a new path.
        try { $attributes = & $ReadAttributes $current }
        catch [IO.FileNotFoundException] { continue }
        catch [IO.DirectoryNotFoundException] { continue }
        Assert-Fixed (($attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'reparse_path_refused'
    }
}
function Assert-SuiteTempBudget([string]$ShortTemp, [string]$ExpandedTemp,
    [ValidateSet('Guards', 'AgentFlow')][string]$SuiteName = 'Guards') {
    Assert-Fixed (-not [string]::IsNullOrWhiteSpace($ShortTemp) -and
        -not [string]::IsNullOrWhiteSpace($ExpandedTemp)) 'isolated_temp_path_missing'
    Assert-Fixed ($ShortTemp.Length -le 85) 'isolated_temp_path_too_long'
    if ($SuiteName -ne 'Guards') { return }
    # .NET expands an existing 8.3 path in GetFullPath. Guard MODULE-SELFTESTS
    # nests dp-msg-<12 hex>, then dp-msr-<12 hex>; FortuneFileImporter appends
    # its 32-hex scratch leaf and needs at least one child character to reach
    # an exact 208-character destination. A short alias alone cannot prove it.
    $guardRun = Join-Path $ExpandedTemp ('dp-msg-' + ('0' * 12))
    $moduleRun = Join-Path $guardRun ('dp-msr-' + ('0' * 12))
    $importRoot = Join-Path $moduleRun ('DesktopAICompanion-fortune-import-' + ('0' * 32))
    $fixtureSegment = 208 - $importRoot.Length - 1
    Assert-Fixed ($fixtureSegment -ge 1 -and $fixtureSegment -le 240) 'isolated_temp_fixture_path_too_long'
}
function New-ShardEnvironment([string]$PoolRoot, [int]$Index, [string[]]$ToolDirectories, [string]$InheritedPath) {
    $environment = @{}
    foreach ($pair in @(@('TEMP', 't'), @('TMP', 't'), @('APPDATA', 'a'), @('LOCALAPPDATA', 'l'),
        @('DOTNET_CLI_HOME', 'n'), @('DESKTOP_AI_COMPANION_DATA_ROOT', 'd'), @('NUGET_PACKAGES', 'p'),
        @('NUGET_HTTP_CACHE_PATH', 'h'), @('NUGET_SCRATCH', 's'), @('NUGET_PLUGINS_CACHE_PATH', 'c'))) {
        $path = Join-Path $PoolRoot ($pair[1] + $Index)
        if (-not [IO.Directory]::Exists($path)) { New-Item -Path $path -ItemType Directory -ErrorAction Stop | Out-Null }
        $environment[$pair[0]] = $path
    }
    $environment['PATH'] = ($ToolDirectories -join ';') + ';' + $InheritedPath
    $environment['PYTHONUNBUFFERED'] = '1'; $environment['PYTHONDONTWRITEBYTECODE'] = '1'
    # NOLOGO and the obsolete SKIP_FIRST_TIME flag do not prevent User PATH writes.
    $environment['DOTNET_ADD_GLOBAL_TOOLS_TO_PATH'] = '0'
    $environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'; $environment['DOTNET_NOLOGO'] = '1'
    $environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'; $environment['DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE'] = 'true'
    $environment['DOTNET_CLI_USE_MSBUILD_SERVER'] = '0'; $environment['MSBUILDDISABLENODEREUSE'] = '1'
    $environment['UseSharedCompilation'] = 'false'; $environment['DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT'] = '1'
    return $environment
}
function Write-NewUtf8([string]$Path, [string]$Text) {
    Assert-NoReparse (Split-Path $Path -Parent)
    $bytes = $utf8.GetBytes($Text)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}
function Resolve-CanonicalTool([string[]]$Candidates) {
    foreach ($candidate in $Candidates) {
        Assert-NoReparse $candidate
        if ([IO.File]::Exists($candidate)) { return [IO.Path]::GetFullPath($candidate) }
    }
    throw 'canonical_tool_missing'
}
function Invoke-Captured([string]$Exe, [string[]]$Arguments, [string]$Directory, [hashtable]$Environment = @{}, [int]$Seconds = 60) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Exe; $info.WorkingDirectory = $Directory
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = $utf8; $info.StandardErrorEncoding = $utf8
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) { $info.Environment[$key] = $Environment[$key] }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $null = $process.SafeHandle
        $output = $process.StandardOutput.ReadToEndAsync(); $errorOutput = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($Seconds * 1000)) {
            $process.Kill(); $process.WaitForExit(5000) | Out-Null
            throw 'metadata_process_timeout'
        }
        $text = $output.GetAwaiter().GetResult(); $errorText = $errorOutput.GetAwaiter().GetResult()
        Assert-Fixed ($text.Length -le 8MB -and $errorText.Length -le 1MB) 'metadata_output_oversized'
        return @{ Code = $process.ExitCode; Text = $text }
    }
    finally { $process.Dispose() }
}
function Git-Result([string]$Directory, [string[]]$Arguments) {
    return Invoke-Captured $script:git (@('-c', 'core.quotepath=false', '-C', $Directory) + $Arguments) $Directory
}
function Assert-Source([string]$Directory) {
    Assert-NoReparse $Directory
    $head = Git-Result $Directory @('rev-parse', '--verify', 'HEAD')
    Assert-Fixed ($head.Code -eq 0 -and $head.Text.Trim() -ceq $Commit) 'source_commit_changed'
    foreach ($arguments in @(
        @('diff', '--quiet', '--no-ext-diff', '--no-textconv', 'HEAD', '--'),
        @('diff', '--cached', '--quiet', '--no-ext-diff', '--no-textconv', 'HEAD', '--')
    )) {
        Assert-Fixed ((Git-Result $Directory $arguments).Code -eq 0) 'tracked_source_changed'
    }
}
function Get-TargetSnapshot([string]$Directory, [string[]]$Targets) {
    $snapshot = [Collections.Generic.List[object]]::new()
    foreach ($target in $Targets) {
        Assert-PublicRelativePath $target
        $path = Join-Path $Directory $target
        Assert-NoReparse $path
        $snapshot.Add(@{ Path = $target; Sha256 = [MutationShardJob]::HashPublicFile($path) })
    }
    return ,$snapshot.ToArray()
}
function Assert-TargetSnapshot([string]$Directory, [object[]]$Snapshot,
    [scriptblock]$ReadHash = { param($Target) Assert-NoReparse $Target; [MutationShardJob]::HashPublicFile($Target) }) {
    foreach ($entry in $Snapshot) {
        Assert-PublicRelativePath $entry.Path
        Assert-Fixed ((& $ReadHash (Join-Path $Directory $entry.Path)) -ceq $entry.Sha256) 'mutation_target_bytes_changed'
    }
}
function Test-Integer($Value) { return $Value -is [int] -or $Value -is [long] }
function Assert-PlanCore($Plan, [string]$Pinned, [string]$Digest = '') {
    Assert-Fixed ($Plan.schemaVersion -eq 1 -and $Plan.sourceCommit -ceq $Pinned -and
        $Plan.inventorySha256 -cmatch '^[0-9a-f]{64}$') 'plan_header_invalid'
    Assert-Fixed ((Test-Integer $Plan.inventoryCount) -and (Test-Integer $Plan.selectedCount) -and
        $Plan.inventoryCount -gt 0 -and $Plan.inventoryCount -le 100000 -and
        $Plan.selectedCount -gt 0 -and @($Plan.cases).Count -eq $Plan.selectedCount) 'plan_count_invalid'
    if ($Digest) { Assert-Fixed ($Plan.inventorySha256 -ceq $Digest) 'inventory_digest_changed' }
    $seen = [Collections.Generic.HashSet[int]]::new()
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($case in $Plan.cases) {
        Assert-Fixed ((Test-Integer $case.index) -and $case.index -gt 0 -and $case.index -le $Plan.inventoryCount -and $seen.Add([int]$case.index)) 'case_index_invalid'
        Assert-Fixed ($case.id -is [string] -and $case.id.Length -gt 0 -and $case.id.Length -le 1024 -and
            $case.id -ceq $case.id.Trim() -and $case.id -notmatch '[\x00-\x1f\x7f]' -and $ids.Add($case.id)) 'case_id_invalid'
    }
}
function Assert-CompletePlan($Whole, [object[]]$Plans, [string]$Pinned, [int]$Count) {
    Assert-PlanCore $Whole $Pinned
    Assert-Fixed ($Whole.selection.kind -ceq 'all' -and $Whole.selectedCount -eq $Whole.inventoryCount -and $Plans.Count -eq $Count) 'whole_plan_incomplete'
    $original = @{}
    foreach ($case in $Whole.cases) { $original[[int]$case.index] = [string]$case.id }
    $union = [Collections.Generic.HashSet[int]]::new()
    for ($i = 1; $i -le $Count; $i++) {
        $plan = $Plans[$i - 1]
        Assert-PlanCore $plan $Pinned $Whole.inventorySha256
        Assert-Fixed ($plan.inventoryCount -eq $Whole.inventoryCount -and $plan.selection.kind -ceq 'shard' -and
            $plan.selection.index -eq $i -and $plan.selection.count -eq $Count) 'shard_selection_invalid'
        foreach ($case in $plan.cases) {
            Assert-Fixed (([int]$case.index - 1) % $Count -eq $i - 1 -and $union.Add([int]$case.index) -and
                $original[[int]$case.index] -ceq $case.id) 'shard_union_invalid'
        }
    }
    Assert-Fixed ($union.Count -eq $Whole.inventoryCount) 'shard_union_incomplete'
}
function Test-ShardVerdict([string]$Text, $Plan, [int]$ExitCode,
    [ValidateSet('Guards', 'AgentFlow')][string]$SuiteName = 'Guards') {
    Assert-Fixed ($ExitCode -eq 0) 'shard_process_failed'
    $buildStart = @([regex]::Matches($Text, '(?m)^PREBUILD START\r?$'))
    $buildExit = @([regex]::Matches($Text, '(?m)^PREBUILD EXIT=0\r?$'))
    Assert-Fixed ($buildStart.Count -eq 1 -and $buildExit.Count -eq 1 -and $buildStart[0].Index -lt $buildExit[0].Index) 'complete_prebuild_unproven'
    $runtimeRows = @([regex]::Matches($Text, '(?m)^CASEPLAN ([^\r\n]+)\r?$'))
    Assert-Fixed ($runtimeRows.Count -eq 1) 'runtime_plan_missing'
    Assert-Fixed ($buildExit[0].Index -lt $runtimeRows[0].Index) 'complete_prebuild_order_invalid'
    $runtime = $runtimeRows[0].Groups[1].Value | ConvertFrom-Json -Depth 20
    Assert-Fixed ($runtime.sourceCommit -ceq $Plan.sourceCommit -and $runtime.inventorySha256 -ceq $Plan.inventorySha256 -and
        $runtime.inventoryCount -eq $Plan.inventoryCount -and $runtime.selectedCount -eq $Plan.selectedCount -and
        $runtime.selection.kind -ceq 'shard' -and $runtime.selection.index -eq $Plan.selection.index -and
        $runtime.selection.count -eq $Plan.selection.count -and
        (@($runtime.selectedIndices) -join ',') -ceq (@($Plan.cases.index) -join ',')) 'runtime_plan_changed'
    if ($SuiteName -eq 'Guards') {
        Assert-Fixed ($Text -match '(?m)^  baseline clean\r?$' -and $Text -match '(?m)^restoring and rebuilding the clean tree\r?$' -and
            $Text -notmatch '(?m)^THE CLEAN REBUILD FAILED') 'baseline_or_restoration_unproven'
    }
    else {
        Assert-Fixed ($Text -match '(?m)^  baseline PASS, [1-9][0-9]* witness assertions present\r?$' -and
            $Text -notmatch '(?m)^THE STRICT REBUILD OF THE RESTORED TREE FAILED') 'baseline_or_restoration_unproven'
        $strictStart = @([regex]::Matches($Text, '(?m)^FINAL_STRICT_BUILD START\r?$'))
        $strictExit = @([regex]::Matches($Text, '(?m)^FINAL_STRICT_BUILD EXIT=0\r?$'))
        Assert-Fixed ($strictStart.Count -eq 1 -and $strictExit.Count -eq 1 -and
            $strictStart[0].Index -lt $strictExit[0].Index) 'final_strict_build_unproven'
    }
    $allowedWithheld = (Get-SuitePolicy $SuiteName).WithheldIds
    $expected = [Collections.Generic.Dictionary[string, bool]]::new([StringComparer]::Ordinal)
    foreach ($case in $Plan.cases) { $expected.Add($case.id, $true) }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $fired = 0; $withheld = 0
    $rows = [regex]::Matches($Text, '(?m)^  (?<id>\S[^\r\n]*?) +(?<outcome>FIRED|NOT RUN|NO-OP|BROKEN|WRONG|SURVIVED)(?<details>[^\r\n]*)\r?$')
    foreach ($row in $rows) {
        $id = $row.Groups['id'].Value
        Assert-Fixed ($expected.ContainsKey($id) -and $seen.Add($id)) 'unexpected_or_duplicate_verdict'
        if ($row.Groups['outcome'].Value -ceq 'FIRED') {
            $suffix = $row.Groups['details'].Value.Trim()
            Assert-Fixed ($suffix.Length -eq 0 -or ($SuiteName -eq 'AgentFlow' -and
                $suffix -cmatch '^\(\+[1-9][0-9]* other failures\)$')) 'unexpected_verdict_suffix'
            $fired++
        }
        elseif ($row.Groups['outcome'].Value -ceq 'NOT RUN' -and $id -cin $allowedWithheld) {
            Assert-Fixed ($row.Groups['details'].Value.Contains('DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT=1')) 'withheld_reason_invalid'
            $withheld++
        }
        else { throw 'mutation_verdict_failed' }
    }
    Assert-Fixed ($seen.Count -eq $Plan.selectedCount) 'case_verdict_missing'
    $footer = @([regex]::Matches($Text, '(?m)^(\d+)/(\d+) fired\.\r?$'))
    Assert-Fixed ($footer.Count -eq 1 -and [int]$footer[0].Groups[1].Value -eq $fired -and
        [int]$footer[0].Groups[2].Value -eq $fired -and $fired + $withheld -eq $Plan.selectedCount) 'verdict_totals_invalid'
    if ($SuiteName -eq 'AgentFlow') {
        Assert-Fixed ($footer[0].Index -lt $strictStart[0].Index) 'final_strict_build_order_invalid'
    }
    return @{ Fired = $fired; Withheld = $withheld }
}
function Initialize-NativeJob {
    if ('MutationShardJob' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
public sealed class MutationShardJob : IDisposable {
  readonly SafeFileHandle job;
  [StructLayout(LayoutKind.Sequential)] struct Basic { public long A,B; public uint Flags; public UIntPtr C,D; public uint Active; public UIntPtr E; public uint F,G; }
  [StructLayout(LayoutKind.Sequential)] struct Io { public ulong A,B,C,D,E,F; }
  [StructLayout(LayoutKind.Sequential)] struct Extended { public Basic Basic; public Io Io; public UIntPtr A,B,C,D; }
  [StructLayout(LayoutKind.Sequential)] struct FileInfo { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME A,B,C; public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow; }
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref Extended information, uint length);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern uint GetShortPathName(string path, StringBuilder output, uint size);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfo information);
  public MutationShardJob() {
    job = CreateJobObject(IntPtr.Zero, null);
    if (job.IsInvalid) { job.Dispose(); throw new Win32Exception(); }
    var information = new Extended { Basic = new Basic { Flags = 0x2008, Active = 64 } };
    if (!SetInformationJobObject(job, 9, ref information, (uint)Marshal.SizeOf<Extended>())) { job.Dispose(); throw new Win32Exception(); }
  }
  public void Assign(Process process) { if (!AssignProcessToJobObject(job, process.SafeHandle)) throw new Win32Exception(); }
  public static string ShortPath(string path) { var result = new StringBuilder(32768); uint n = GetShortPathName(path, result, (uint)result.Capacity); return n > 0 && n < result.Capacity ? result.ToString() : path; }
  public static string HashPublicFile(string path) {
    using var stream = OpenPublicFile(path);
    return Convert.ToHexString(SHA256.HashData(stream));
  }
  static FileStream OpenPublicFile(string path) {
    var handle = CreateFile(path, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
    if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(); }
    try {
      if (!GetFileInformationByHandle(handle, out var information)) throw new Win32Exception();
      if ((information.Attributes & 0x410) != 0 || information.Links != 1) throw new IOException("source_file_identity_refused");
      return new FileStream(handle, FileAccess.Read);
    }
    catch { handle.Dispose(); throw; }
  }
  public static string ReadPublicText(string path) {
    using var stream = OpenPublicFile(path);
    if (stream.Length > 1048576) throw new IOException("attribute_blob_oversized");
    using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
    return reader.ReadToEnd();
  }
  public static string ReadPublicLog(string path) {
    using var stream = OpenPublicFile(path);
    if (stream.Length > 67108864) throw new IOException("shard_log_oversized");
    using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
    return reader.ReadToEnd();
  }
  public void Dispose() { job.Dispose(); }
}
'@
}
function Start-OwnedShard($Item, [string]$Pool) {
    $outFile = $null; $errFile = $null; $job = $null; $process = $null
    $outputCopy = $null; $errorCopy = $null; $retained = $null; $completed = $false
    try {
        Assert-Source $Item.Directory
        $info = [Diagnostics.ProcessStartInfo]::new()
        $info.FileName = $pwsh; $info.WorkingDirectory = $Item.Directory
        $info.UseShellExecute = $false; $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
        foreach ($argument in @('-NoProfile', '-NonInteractive', '-File', (Join-Path $Item.Directory 'tools\agentbroker\Invoke-MutationShards.ps1'),
            '-Worker', '-Commit', $Commit, '-Suite', $Suite, '-Shards', [string]$Shards, '-ShardIndex', [string]$Item.Index)) { $info.ArgumentList.Add($argument) }
        foreach ($key in $Item.Environment.Keys) { $info.Environment[$key] = $Item.Environment[$key] }
        $outPath = Join-Path $Pool "shard-$($Item.Index).out"
        $outFile = [IO.File]::Open($outPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        $errFile = [IO.File]::Open((Join-Path $Pool "shard-$($Item.Index).err"), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        $job = [MutationShardJob]::new()
        $process = [Diagnostics.Process]::Start($info)
        $retained = $process.SafeHandle
        $job.Assign($process)
        $outputCopy = $process.StandardOutput.BaseStream.CopyToAsync($outFile)
        $errorCopy = $process.StandardError.BaseStream.CopyToAsync($errFile)
        $Item.Process = $process; $Item.Job = $job; $Item.OutFile = $outFile; $Item.ErrFile = $errFile; $Item.OutPath = $outPath
        $Item.OutputCopy = $outputCopy; $Item.ErrorCopy = $errorCopy
        $Item.Elapsed = [Diagnostics.Stopwatch]::StartNew(); $Item.TimedOut = $false
        $completed = $true
        return $Item
    }
    finally {
        if (-not $completed) {
            if ($job) { $job.Dispose() }
            if ($process) {
                # This process has not passed its gate. Retain its original handle
                # before termination; never re-find it by PID after an exception.
                if ($retained -and -not $process.HasExited) { $process.Kill() }
                $process.WaitForExit(35000) | Out-Null
                if ($outputCopy) { $outputCopy.Wait(5000) | Out-Null }
                if ($errorCopy) { $errorCopy.Wait(5000) | Out-Null }
                $process.Dispose()
            }
            if ($outFile) { $outFile.Dispose() }
            if ($errFile) { $errFile.Dispose() }
        }
    }
}
function Run-SelfTest {
    $checks = 0
    Assert-SuiteTempBudget ('C:\' + ('s' * 82)) ('C:\' + ('e' * 96)) 'Guards'; $checks++
    Assert-SuiteTempBudget ('C:\' + ('s' * 79)) ('C:\' + ('e' * 114)) 'AgentFlow'; $checks++
    foreach ($budget in @(
        @(('C:\' + ('s' * 83)), ('C:\' + ('e' * 96)), 'isolated_temp_path_too_long'),
        @(('C:\' + ('s' * 79)), ('C:\' + ('e' * 97)), 'isolated_temp_fixture_path_too_long'),
        @(('C:\' + ('s' * 79)), ('C:\' + ('e' * 114)), 'isolated_temp_fixture_path_too_long'))) {
        $failure = ''
        try { Assert-SuiteTempBudget $budget[0] $budget[1] 'Guards' } catch { $failure = $_.Exception.Message }
        Assert-Fixed ($failure -ceq $budget[2]) 'selftest_temp_budget_refusal_failed'; $checks++
    }
    $lfsConfig = "filter.lfs.clean`ngit-lfs clean -- %f`0filter.lfs.smudge`ngit-lfs smudge -- %f`0filter.lfs.process`ngit-lfs filter-process`0filter.lfs.required`ntrue`0"
    Assert-Fixed ((Assert-InertFilterConfiguration $lfsConfig 0) -eq 4) 'selftest_standard_lfs_failed'; $checks++
    Assert-Fixed ((Assert-InertFilterConfiguration '' 1) -eq 0) 'selftest_no_filter_config_failed'; $checks++
    foreach ($badConfig in @("filter.custom.clean`ncustom-clean %f`0", "filter.lfs.clean`ngit-lfs clean -- %f --custom`0",
        "filter.lfs.required`nfalse`0", "core.hooksPath`n.hooks`0", "core.attributesFile`nattributes.txt`0",
        "filter.LFS.clean`ngit-lfs clean -- %f`0", $lfsConfig.TrimEnd([char]0), ($lfsConfig + "filter.other.process`ncustom-filter`0"))) {
        $refused = $false; try { $null = Assert-InertFilterConfiguration $badConfig 0 } catch { $refused = $true }
        Assert-Fixed $refused 'selftest_custom_config_refusal_failed'; $checks++
    }
    Assert-NoFilterAttributes "# filter=lfs in a comment is inert`n* text=auto eol=lf`n*.zip -text`n[attr]public text eol=lf`n**/*.cs public`n"
    $checks++
    foreach ($badAttributes in @('*.bin filter=lfs', '**/* filter=lfs', '[attr]named filter=lfs',
        '*.cs filter', '*.cs -filter', '*.cs !filter', 'nested/* text filter=lfs',
        '"space pattern" filter=lfs', 'space\ pattern filter=lfs')) {
        $refused = $false; try { Assert-NoFilterAttributes $badAttributes } catch { $refused = $true }
        Assert-Fixed $refused 'selftest_filter_attributes_refusal_failed'; $checks++
    }
    $attributePaths = [Collections.Generic.List[string]]::new()
    $count = Assert-PinnedAttributeBlobs @('.gitattributes', 'nested/.gitattributes', 'src/ordinary.cs') {
        param($Path) $attributePaths.Add($Path); return '* text=auto'
    }
    Assert-Fixed ($count -eq 2 -and $attributePaths.Count -eq 2 -and $attributePaths[1] -ceq 'nested/.gitattributes') 'selftest_nested_attributes_coverage_failed'; $checks++
    $refused = $false
    try { $null = Assert-PinnedAttributeBlobs @('.gitattributes', 'nested/.gitattributes') {
        param($Path) if ($Path -ceq 'nested/.gitattributes') { return '**/* filter=lfs' }; return '* text=auto'
    } } catch { $refused = $true }
    Assert-Fixed $refused 'selftest_nested_filter_refusal_failed'; $checks++
    $guardPolicy = Get-SuitePolicy 'Guards'; $flowPolicy = Get-SuitePolicy 'AgentFlow'
    Assert-Fixed ($guardPolicy.Harness -ceq 'tests/mutate-selftest-guards.py' -and $guardPolicy.WithheldIds.Count -eq 2) 'selftest_guard_policy_failed'; $checks++
    Assert-Fixed ($flowPolicy.Harness -ceq 'tests/mutate-agentflow.py' -and $flowPolicy.WithheldIds.Count -eq 0) 'selftest_flow_policy_failed'; $checks++
    foreach ($name in @('src/Core.cs', 'docs/readme.md', 'assets/icon.png')) { Assert-PublicRelativePath $name; $checks++ }
    foreach ($name in @('x.env', '.env.local/a.cs', 'x/.npmrc', 'x/server.pem', 'x/a.key', 'x/a.p12', 'x/a.pfx',
        'x/a.keystore', 'x/privatekey-copy/a.cs', '.git/credentials', '../escape.cs', 'C:/outside.cs', 'x/NUL.txt')) {
        $refused = $false; try { Assert-PublicRelativePath $name } catch { $refused = $true }
        Assert-Fixed $refused 'selftest_path_refusal_failed'; $checks++
    }
    $refused = $false
    try { Assert-NoReparse 'C:\synthetic-mutation-link\child' { param($Target)
        if ($Target.EndsWith('synthetic-mutation-link', [StringComparison]::Ordinal)) { return [IO.FileAttributes]::ReparsePoint }
        return [IO.FileAttributes]::Directory
    } } catch { $refused = $true }
    Assert-Fixed $refused 'selftest_dangling_reparse_refusal_failed'; $checks++
    $refused = $false
    try { Assert-NoReparse 'C:\synthetic-mutation-denied' { param($Target) throw [UnauthorizedAccessException]::new('synthetic') } } catch { $refused = $true }
    Assert-Fixed $refused 'selftest_metadata_denial_refusal_failed'; $checks++
    Assert-NoReparse 'C:\synthetic-mutation-missing\child' { param($Target) throw [IO.FileNotFoundException]::new('synthetic') }
    $checks++
    $snapshot = @(@{ Path = 'source/known.cs'; Sha256 = 'a' * 64 })
    Assert-TargetSnapshot 'C:\synthetic-mutation-source' $snapshot { param($Target) return 'a' * 64 }; $checks++
    $refused = $false
    try { Assert-TargetSnapshot 'C:\synthetic-mutation-source' $snapshot { param($Target) return 'b' * 64 } } catch { $refused = $true }
    Assert-Fixed $refused 'selftest_changed_bytes_refusal_failed'; $checks++
    $pin = 'a' * 40; $digest = 'b' * 64
    $allCases = @(@{ index = 1L; id = 'first synthetic case' }, @{ index = 2L; id = $guardWithheldIds[0] },
        @{ index = 3L; id = $guardWithheldIds[1] }, @{ index = 4L; id = 'last synthetic case' })
    function Plan($Cases, $Selection) { [pscustomobject]@{ schemaVersion = 1; sourceCommit = $pin; inventorySha256 = $digest;
        inventoryCount = 4L; selectedCount = $Cases.Count; cases = $Cases; selection = $Selection } }
    $whole = Plan $allCases @{ kind = 'all' }
    $first = Plan @($allCases[0], $allCases[2]) @{ kind = 'shard'; index = 1; count = 2 }
    $second = Plan @($allCases[1], $allCases[3]) @{ kind = 'shard'; index = 2; count = 2 }
    Assert-CompletePlan $whole @($first, $second) $pin 2; $checks++
    foreach ($change in @('digest', 'commit', 'gap', 'duplicate', 'id', 'selection', 'fractional_index')) {
        $bad = $second | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        switch ($change) {
            'digest' { $bad.inventorySha256 = 'c' * 64 }
            'commit' { $bad.sourceCommit = 'd' * 40 }
            'gap' { $bad.cases = @($bad.cases[0]); $bad.selectedCount = 1 }
            'duplicate' { $bad.cases[1].index = 2 }
            'id' { $bad.cases[1].id = 'wrong identity' }
            'selection' { $bad.selection.kind = 'only' }
            'fractional_index' { $bad.cases[0].index = 2.5 }
        }
        $refused = $false; try { Assert-CompletePlan $whole @($first, $bad) $pin 2 } catch { $refused = $true }
        Assert-Fixed $refused 'selftest_plan_refusal_failed'; $checks++
    }
    $runtime = @{ schemaVersion = 1; sourceCommit = $pin; inventorySha256 = $digest; inventoryCount = 4; selectedCount = 2;
        selection = $first.selection; selectedIndices = @(1, 3) } | ConvertTo-Json -Compress
    $log = "PREBUILD START`nPREBUILD EXIT=0`nCASEPLAN $runtime`n  baseline clean`n  first synthetic case                                 FIRED`n  $($guardWithheldIds[1]) NOT RUN (DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT=1)`nrestoring and rebuilding the clean tree`n1/1 fired.`n"
    $result = Test-ShardVerdict $log $first 0
    Assert-Fixed ($result.Fired -eq 1 -and $result.Withheld -eq 1) 'selftest_safe_verdict_failed'; $checks++
    foreach ($badLog in @($log.Replace('FIRED', 'SURVIVED'), $log.Replace('FIRED', 'NO-OP'), $log.Replace('FIRED', 'BROKEN'),
        $log.Replace('1/1 fired.', '0/1 fired.'), $log.Replace('  baseline clean', 'baseline missing'),
        $log.Replace('first synthetic case', 'unselected synthetic case'), $log.Replace('NOT RUN', 'WRONG'),
        $log.Replace('first synthetic case', 'First synthetic case'), $log.Replace($digest, ('c' * 64)),
        ($log + '1/1 fired.'), ($log + "  first synthetic case FIRED`n"),
        $log.Replace('PREBUILD START', 'PREBUILD MISSING'), $log.Replace('PREBUILD EXIT=0', 'PREBUILD EXIT=1'),
        ($log.Replace("PREBUILD EXIT=0`n", '') + "PREBUILD EXIT=0`n"))) {
        $refused = $false; try { $null = Test-ShardVerdict $badLog $first 0 } catch { $refused = $true }
        Assert-Fixed $refused 'selftest_verdict_refusal_failed'; $checks++
    }
    $refused = $false; try { $null = Test-ShardVerdict $log $first 1 } catch { $refused = $true }
    Assert-Fixed $refused 'selftest_exit_refusal_failed'; $checks++
    $flowLog = "PREBUILD START`nPREBUILD EXIT=0`nCASEPLAN $runtime`n  baseline PASS, 7 witness assertions present`n  first synthetic case FIRED (+2 other failures)`n  $($guardWithheldIds[1]) FIRED`n2/2 fired.`nFINAL_STRICT_BUILD START`nFINAL_STRICT_BUILD EXIT=0`n"
    $result = Test-ShardVerdict $flowLog $first 0 'AgentFlow'
    Assert-Fixed ($result.Fired -eq 2 -and $result.Withheld -eq 0) 'selftest_flow_verdict_failed'; $checks++
    foreach ($badFlow in @($flowLog.Replace('FIRED (+2 other failures)', 'FIRED (+0 other failures)'),
        $flowLog.Replace('FIRED (+2 other failures)', 'FIRED (arbitrary suffix)'),
        $flowLog.Replace("$($guardWithheldIds[1]) FIRED", "$($guardWithheldIds[1]) NOT RUN (DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT=1)"),
        $flowLog.Replace('FINAL_STRICT_BUILD START', 'BUILD MISSING'),
        $flowLog.Replace('FINAL_STRICT_BUILD EXIT=0', 'FINAL_STRICT_BUILD EXIT=1'),
        $flowLog.Replace('baseline PASS, 7 witness', 'baseline PASS, 0 witness'),
        $flowLog.Replace('baseline PASS, 7 witness assertions present', 'baseline clean'),
        $flowLog.Replace("2/2 fired.`n", '').Replace('FINAL_STRICT_BUILD START', "2/2 fired.`nFINAL_STRICT_BUILD START").Replace('FINAL_STRICT_BUILD EXIT=0', "FINAL_STRICT_BUILD EXIT=0`n2/2 fired."),
        ($flowLog + 'THE STRICT REBUILD OF THE RESTORED TREE FAILED'), $log)) {
        $refused = $false; try { $null = Test-ShardVerdict $badFlow $first 0 'AgentFlow' } catch { $refused = $true }
        Assert-Fixed $refused 'selftest_flow_refusal_failed'; $checks++
    }
    $earlyBuild = $flowLog.Replace("FINAL_STRICT_BUILD START`nFINAL_STRICT_BUILD EXIT=0`n", '')
    $earlyBuild = "FINAL_STRICT_BUILD START`nFINAL_STRICT_BUILD EXIT=0`n" + $earlyBuild
    $refused = $false; try { $null = Test-ShardVerdict $earlyBuild $first 0 'AgentFlow' } catch { $refused = $true }
    Assert-Fixed $refused 'selftest_early_final_build_refusal_failed'; $checks++
    $refused = $false; try { $null = Test-ShardVerdict $flowLog $first 0 'Guards' } catch { $refused = $true }
    Assert-Fixed $refused 'selftest_cross_suite_refusal_failed'; $checks++
    $refused = $false; try { $null = Test-ShardVerdict $log.Replace(' FIRED', ' FIRED (+2 other failures)') $first 0 'Guards' } catch { $refused = $true }
    Assert-Fixed $refused 'selftest_guard_suffix_refusal_failed'; $checks++
    Write-Output ("SelfTestChecks=$checks`nGitInvoked=False`nWorktreesCreated=False`nProcessesStarted=False`nProtectedFixturesOpened=False")
}

. (Join-Path $PSScriptRoot 'MutationShardRemainder.ps1')
if ($LibraryOnly) { return }
if ($SelfTest) { Run-SelfTest; exit 0 }
$completedIndices = @(Convert-RemainderIndices $CompletedShards)
$scheduledIndices = @(Convert-RemainderIndices $RunShards)
Assert-RemainderOptions $RemainderPool $completedIndices $scheduledIndices $Shards ([bool]$ApplyRemainder) ([bool]$Worker)
if ($ApplyRemainder) { Assert-Fixed ($HandoffControllerId -gt 0 -and $HandoffControllerCreationFileTime -cmatch '^[1-9][0-9]{0,19}$') 'expected_handoff_identity_required' }
Assert-Fixed ($Commit -cmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') 'full_pinned_commit_required'
Assert-Fixed ($env:OS -eq 'Windows_NT') 'windows_required'
# Use real installed images. A WindowsApps alias or another PATH entry is never
# a substitute, and per-shard APPDATA/LOCALAPPDATA cannot redirect resolution.
$userLocal = Join-Path $env:USERPROFILE 'AppData\Local'
$git = Resolve-CanonicalTool @((Join-Path $userLocal 'Programs\Git\cmd\git.exe'), (Join-Path $env:ProgramFiles 'Git\cmd\git.exe'))
$python = Resolve-CanonicalTool @((Join-Path $userLocal 'Programs\Python\Python314\python.exe'))
$pwsh = Resolve-CanonicalTool @((Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'))
$dotnet = Resolve-CanonicalTool @((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))

if ($Worker) {
    Assert-Fixed ($ShardIndex -gt 0 -and $ShardIndex -le $Shards -and (Split-Path $repo -Leaf) -ceq "w$ShardIndex") 'worker_path_invalid'
    $pool = Split-Path $repo -Parent
    Assert-Fixed ((Split-Path $pool -Leaf) -cmatch '^ms-[0-9a-f]{8}$') 'worker_pool_invalid'
    Assert-NoReparse $pool
    $gateFile = Join-Path $pool "g$ShardIndex"
    $wait = [Diagnostics.Stopwatch]::StartNew()
    while (-not [IO.File]::Exists($gateFile) -and $wait.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 50 }
    Assert-Fixed ([IO.File]::Exists($gateFile)) 'worker_gate_timeout'
    Assert-Source $repo
    Write-Output 'PREBUILD START'
    & $pwsh -NoProfile -NonInteractive -File (Join-Path $repo 'build.ps1') -Release
    $prebuildCode = $LASTEXITCODE
    Write-Output "PREBUILD EXIT=$prebuildCode"
    Assert-Source $repo
    if ($prebuildCode -ne 0) { exit $prebuildCode }
    & $python -X utf8 -u (Join-Path $repo $harness) --shard "$ShardIndex/$Shards"
    $workerCode = $LASTEXITCODE
    Assert-Source $repo
    if ($workerCode -eq 0 -and $Suite -eq 'AgentFlow') {
        # The Flow harness checks its own strict restore build but prints only
        # failures. Record a separate positive strict-build result for the pool.
        Write-Output 'FINAL_STRICT_BUILD START'
        & $dotnet build (Join-Path $repo 'modules\AgentFlow\AgentFlow.csproj') -c Release --nologo -v:quiet
        $workerCode = $LASTEXITCODE
        Write-Output "FINAL_STRICT_BUILD EXIT=$workerCode"
        Assert-Source $repo
    }
    exit $workerCode
}

$active = [Collections.Generic.List[object]]::new()
$results = [Collections.Generic.List[object]]::new()
$stage = 'commit_preflight'
try {
    Assert-NoReparse $repo
    $verified = Git-Result $repo @('rev-parse', '--verify', ($Commit + '^{commit}'))
    Assert-Fixed ($verified.Code -eq 0 -and $verified.Text.Trim() -ceq $Commit) 'pinned_commit_unavailable'
    $stage = 'tracked_name_preflight'
    $tree = Git-Result $repo @('ls-tree', '-r', '-z', '--full-tree', $Commit)
    Assert-Fixed ($tree.Code -eq 0) 'tracked_manifest_unavailable'
    $names = [Collections.Generic.List[string]]::new()
    foreach ($entry in $tree.Text.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
        Assert-Fixed ($entry -cmatch '^(100644|100755) blob [0-9a-f]+\t(.+)$') 'tracked_link_or_special_entry_refused'
        $name = $Matches[2]; Assert-PublicRelativePath $name; $names.Add($name)
    }
    Assert-Fixed ($names.Count -gt 0 -and $names.Contains($harness)) 'pinned_inventory_missing'
    if (-not $CheckCheckoutPolicy) {
        Assert-Fixed ($names.Contains('tests/mutation_caseplan.py') -and
            $names.Contains('tools/agentbroker/Invoke-MutationShards.ps1')) 'pinned_infrastructure_missing'
    }
    $stage = 'checkout_configuration'
    # Refuse an execution-capable checkout configuration. Existing hooks and
    # filters are never disabled or overridden to make this gate run.
    $configured = Git-Result $repo @('config', '--null', '--get-regexp', '^(core\.hooksPath|core\.attributesFile|filter\.)')
    $inertFilterEntries = Assert-InertFilterConfiguration $configured.Text $configured.Code
    $attributeFilesChecked = Assert-PinnedAttributeBlobs $names.ToArray() {
        param($attribute)
        $content = Git-Result $repo @('show', ($Commit + ':' + $attribute))
        Assert-Fixed ($content.Code -eq 0) 'attribute_blob_unavailable'
        return $content.Text
    }
    $common = Git-Result $repo @('rev-parse', '--path-format=absolute', '--git-common-dir')
    Assert-Fixed ($common.Code -eq 0) 'git_common_directory_unavailable'
    Assert-NoReparse $common.Text.Trim()
    $infoAttributes = Join-Path $common.Text.Trim() 'info\attributes'
    Assert-NoReparse $infoAttributes
    Assert-Fixed (-not [IO.File]::Exists($infoAttributes)) 'git_info_attributes_refused'
    $hookDirectory = Join-Path $common.Text.Trim() 'hooks'
    Assert-NoReparse $hookDirectory
    if ([IO.Directory]::Exists($hookDirectory)) {
        foreach ($entry in Get-ChildItem -LiteralPath $hookDirectory -File) {
            Assert-PublicRelativePath ('hooks/' + $entry.Name)
            Assert-Fixed ($entry.Name.EndsWith('.sample', [StringComparison]::OrdinalIgnoreCase)) 'active_git_hook_refused'
        }
    }
    Assert-Fixed ([string]::IsNullOrWhiteSpace($env:XDG_CONFIG_HOME)) 'custom_global_attributes_scope_refused'
    $globalAttributes = Join-Path $env:USERPROFILE '.config\git\attributes'
    Assert-NoReparse $globalAttributes
    Assert-Fixed (-not [IO.File]::Exists($globalAttributes)) 'global_attributes_refused'
    foreach ($name in @('GIT_ATTR_SOURCE', 'GIT_ATTR_SYSTEM', 'GIT_ATTR_GLOBAL')) {
        Assert-Fixed ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name, 'Process'))) 'custom_attributes_environment_refused'
    }
    # Git for Windows can also read installation-level attributes. Inspect only
    # these known public files; no configuration or attribute scope is disabled.
    $gitRoot = Split-Path (Split-Path $git -Parent) -Parent
    foreach ($relative in @('etc\gitattributes', 'mingw64\etc\gitattributes', 'mingw32\etc\gitattributes')) {
        $systemAttributes = Join-Path $gitRoot $relative
        Assert-NoReparse $systemAttributes
        if ([IO.File]::Exists($systemAttributes)) {
            Initialize-NativeJob
            Assert-NoFilterAttributes ([MutationShardJob]::ReadPublicText($systemAttributes))
            $attributeFilesChecked++
        }
    }
    if ($CheckCheckoutPolicy) {
        Write-Output 'CheckoutPolicyPassed=True'
        Write-Output "StandardLfsEntries=$inertFilterEntries"
        Write-Output "AttributeFilesChecked=$attributeFilesChecked"
        Write-Output 'WorktreesCreated=False'
        Write-Output 'SecurityControlsChanged=False'
        exit 0
    }

    $imported = @(); $remainderMetadata = $null
    if ($RemainderPool) {
        $stage = 'verify_remainder_import'
        $RemainderPool = Assert-RemainderPoolPath $RemainderPool $repo
        $source = Join-Path $RemainderPool "w$($completedIndices[0])"
        $remainderMetadata = Get-RemainderSourceMetadata $source
        $imported = @($completedIndices | ForEach-Object {
            Read-VerifiedImportedShard $RemainderPool $_ $remainderMetadata.Whole $remainderMetadata.Targets
        })
        $scheduledPlans = @($scheduledIndices | ForEach-Object { Get-RemainderShardPlan $source $_ $Shards })
        Assert-MixedShardCoverage $remainderMetadata.Whole @($imported.Plan) $scheduledPlans $Commit
        foreach ($target in $remainderMetadata.Targets) { Assert-Fixed ($names.Contains($target)) 'untracked_mutation_target_refused' }
        if (-not $ApplyRemainder) {
            @{ Result = 'REMAINDER_PLAN_VERIFIED'; SourceCommit = $Commit; InventorySha256 = $remainderMetadata.Whole.inventorySha256;
                InventoryCount = $remainderMetadata.Whole.inventoryCount; CompletedShards = $completedIndices; RunShards = $scheduledIndices;
                Shards = $Shards; MaxParallel = $MaxParallel; ImportedCases = [int]($imported.Plan | Measure-Object selectedCount -Sum).Sum;
                ApplyRequired = $true; HandoffRequired = $true; WorktreesCreated = $false; MutationWorkersStarted = $false } | ConvertTo-Json -Compress
            exit 0
        }
        $proof = Convert-RemainderJson (Read-RemainderText (Join-Path $RemainderPool 'handoff.json'))
        Assert-RemainderHandoff $proof $RemainderPool $remainderMetadata.Whole $completedIndices $HandoffControllerId $HandoffControllerCreationFileTime
    }

    $stage = 'create_pool'
    $artifacts = Join-Path $repo 'artifacts'; Assert-NoReparse $artifacts
    if (-not [IO.Directory]::Exists($artifacts)) { New-Item -Path $artifacts -ItemType Directory -ErrorAction Stop | Out-Null }
    $pool = Join-Path $artifacts ('ms-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -Path $pool -ItemType Directory -ErrorAction Stop | Out-Null
    Assert-NoReparse $pool
    Initialize-NativeJob
    $shortPool = [MutationShardJob]::ShortPath($pool)
    $expandedPool = [IO.Path]::GetFullPath($shortPool)
    Assert-Fixed ([string]::Equals($expandedPool, $pool, [StringComparison]::OrdinalIgnoreCase)) 'isolated_temp_alias_mismatch'
    foreach ($i in 1..$Shards) {
        Assert-SuiteTempBudget (Join-Path $shortPool "t$i") (Join-Path $expandedPool "t$i") $Suite
    }
    $jobs = [Collections.Generic.List[object]]::new()
    $plans = [Collections.Generic.List[object]]::new()
    $whole = if ($remainderMetadata) { $remainderMetadata.Whole } else { $null }
    $targets = if ($remainderMetadata) { $remainderMetadata.Targets } else { @() }
    $prepareIndices = if ($RemainderPool) { $scheduledIndices } else { @(1..$Shards) }
    $stage = 'prepare_worktrees'
    foreach ($i in $prepareIndices) {
        $worktree = Join-Path $pool "w$i"
        Assert-Fixed (-not [IO.Directory]::Exists($worktree) -and -not [IO.File]::Exists($worktree)) 'worktree_destination_exists'
        $added = Git-Result $repo @('worktree', 'add', '--detach', $worktree, $Commit)
        Assert-Fixed ($added.Code -eq 0) 'worktree_checkout_failed'
        Assert-Source $worktree
        $environment = New-ShardEnvironment $shortPool $i @([IO.Path]::GetDirectoryName($git),
            [IO.Path]::GetDirectoryName($python), [IO.Path]::GetDirectoryName($dotnet), [IO.Path]::GetDirectoryName($pwsh)) $env:PATH
        # Isolate NuGet configuration and writes. A public package cache is a read-only
        # fallback; ordinary user feed settings and credential providers are not loaded.
        $cache = Join-Path $env:USERPROFILE '.nuget\packages'; Assert-NoReparse $cache
        $fallback = ''
        if ([IO.Directory]::Exists($cache)) {
            $environment['RestoreFallbackFolders'] = $cache
            $fallback = '<add key="cached-public-packages" value="' + [Security.SecurityElement]::Escape($cache) + '" />'
        }
        $config = Join-Path $pool "nuget-$i.config"
        Write-NewUtf8 $config ('<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>' +
            '<fallbackPackageFolders><clear/>' + $fallback + '</fallbackPackageFolders></configuration>')
        $environment['RestoreConfigFile'] = $config
        $environment['NUGET_CREDENTIALPROVIDER_SESSIONTOKENCACHE_ENABLED'] = 'false'
        if ($null -eq $whole) {
            $listed = Invoke-Captured $python @('-X', 'utf8', (Join-Path $worktree $harness), '--list-cases-json') $worktree $environment
            Assert-Fixed ($listed.Code -eq 0) 'whole_plan_unavailable'
            $whole = $listed.Text | ConvertFrom-Json -Depth 30
            Write-NewUtf8 (Join-Path $pool 'inventory.json') $listed.Text
            # Import the same already-reviewed module metadata as --list-cases-json;
            # no target content is read by this enumeration or executed as a command.
            $targetCode = @'
import json,os,runpy,sys
root=sys.argv[1]
sys.path.insert(0,os.path.join(root,"tests"))
inventory=runpy.run_path(os.path.join(root,sys.argv[2]),run_name="mutation_target_metadata")["CASES"]
print(json.dumps(sorted({os.path.relpath(case[1],root).replace(os.sep,"/") for case in inventory}),ensure_ascii=True))
'@
            $targetResult = Invoke-Captured $python @('-X', 'utf8', '-c', $targetCode, $worktree, $harness) $worktree $environment
            Assert-Fixed ($targetResult.Code -eq 0) 'mutation_targets_unavailable'
            $targets = @($targetResult.Text | ConvertFrom-Json)
            Assert-Fixed ($targets.Count -gt 0 -and $targets.Count -le 10000) 'mutation_targets_invalid'
            foreach ($target in $targets) { Assert-PublicRelativePath $target; Assert-Fixed ($names.Contains($target)) 'untracked_mutation_target_refused' }
        }
        $listed = Invoke-Captured $python @('-X', 'utf8', (Join-Path $worktree $harness), '--shard', "$i/$Shards", '--list-cases-json') $worktree $environment
        Assert-Fixed ($listed.Code -eq 0) 'shard_plan_unavailable'
        $plan = $listed.Text | ConvertFrom-Json -Depth 30
        $plans.Add($plan); Write-NewUtf8 (Join-Path $pool "plan-$i.json") $listed.Text
        $snapshot = Get-TargetSnapshot $worktree $targets
        if ($RemainderPool) { Assert-RemainderSnapshotEqual $snapshot $imported[0].Snapshot }
        Write-NewUtf8 (Join-Path $pool "source-$i.json") ($snapshot | ConvertTo-Json -Depth 5 -Compress)
        $jobs.Add(@{ Index = $i; Directory = $worktree; Environment = $environment; Plan = $plan; Snapshot = $snapshot })
    }
    $stage = 'validate_inventory'
    if ($RemainderPool) {
        Assert-MixedShardCoverage $whole @($imported.Plan) $plans.ToArray() $Commit
        Write-NewUtf8 (Join-Path $pool 'inventory.json') ($whole | ConvertTo-Json -Depth 30 -Compress)
        Write-NewUtf8 (Join-Path $pool 'imports.json') (@{ Pool = $RemainderPool; CompletedShards = $completedIndices } | ConvertTo-Json -Compress)
        foreach ($item in $imported) { $results.Add($item.Record) }
    }
    else { Assert-CompletePlan $whole $plans.ToArray() $Commit $Shards }
    foreach ($id in $withheldIds) { Assert-Fixed (@($whole.cases.Where({ $_.id -ceq $id })).Count -eq 1) 'withheld_inventory_changed' }
    Write-Output ("Pool=$pool`nSuite=$Suite`nSourceCommit=$Commit`nInventoryCount=$($whole.inventoryCount)`nMaxParallel=$MaxParallel`nRealInputAllowed=False")
    $stage = 'run_shards'
    $next = 0
    while ($next -lt $jobs.Count -or $active.Count -gt 0) {
        while ($next -lt $jobs.Count -and $active.Count -lt $MaxParallel) {
            $item = $jobs[$next++]
            Assert-RemainderLaunchGate (Join-Path $pool "g$($item.Index)")
            Assert-TargetSnapshot $item.Directory $item.Snapshot
            $item = Start-OwnedShard $item $pool
            $active.Add($item)
            Write-NewUtf8 (Join-Path $pool "g$($item.Index)") 'assigned'
            Write-Output "Shard$($item.Index)=STARTED"
        }
        foreach ($item in $active.ToArray()) {
            if (-not $item.Process.HasExited -and $item.Elapsed.Elapsed.TotalMinutes -ge $ShardTimeoutMinutes) {
                $item.TimedOut = $true; $item.Job.Dispose()
                Assert-Fixed ($item.Process.WaitForExit(10000)) 'owned_worker_stop_timeout'
            }
            if (-not $item.Process.HasExited) { continue }
            $item.Job.Dispose()
            Assert-Fixed ($item.OutputCopy.Wait(10000) -and $item.ErrorCopy.Wait(10000)) 'owned_output_drain_timeout'
            $item.OutFile.Dispose(); $item.ErrFile.Dispose()
            $code = $item.Process.ExitCode; $item.Process.Dispose()
            $record = @{ Index = $item.Index; Success = $false; Code = $code; Fired = 0; Withheld = 0; SourceRestored = $false; TargetBytesRestored = $false }
            try {
                Assert-Source $item.Directory; $record.SourceRestored = $true
                Assert-TargetSnapshot $item.Directory $item.Snapshot; $record.TargetBytesRestored = $true
                Assert-Fixed (-not $item.TimedOut) 'shard_deadline_exceeded'
                Assert-Fixed (([IO.FileInfo]::new($item.OutPath)).Length -le 64MB) 'shard_log_oversized'
                $verdict = Test-ShardVerdict ([IO.File]::ReadAllText($item.OutPath, $utf8)) $item.Plan $code $Suite
                $record.Fired = $verdict.Fired; $record.Withheld = $verdict.Withheld; $record.Success = $true
            }
            catch { $record.Failure = Fixed-FailureCode $_ }
            $results.Add($record); $active.Remove($item) | Out-Null
            Write-NewUtf8 (Join-Path $pool "result-$($item.Index).json") ($record | ConvertTo-Json -Compress)
            Write-Output "Shard$($item.Index)=$(if ($record.Success) { 'VERIFIED' } else { 'FAILED' })"
        }
        if ($active.Count -gt 0) { Start-Sleep -Milliseconds 500 }
    }
    $stage = 'aggregate_verdicts'
    if ($RemainderPool) {
        Assert-RemainderHandoff (Convert-RemainderJson (Read-RemainderText (Join-Path $RemainderPool 'handoff.json'))) $RemainderPool $whole $completedIndices $HandoffControllerId $HandoffControllerCreationFileTime
        foreach ($index in $completedIndices) { $null = Read-VerifiedImportedShard $RemainderPool $index $whole $targets }
        Assert-MixedShardCoverage $whole @($imported.Plan) $plans.ToArray() $Commit
    }
    Assert-Fixed ($results.Count -eq $jobs.Count + $imported.Count -and @($results.Where({ -not $_.Success })).Count -eq 0) 'one_or_more_shards_failed'
    $fired = [int]($results | Measure-Object Fired -Sum).Sum
    $withheld = [int]($results | Measure-Object Withheld -Sum).Sum
    Assert-Fixed ($withheld -eq $withheldIds.Count -and $fired + $withheld -eq $whole.inventoryCount) 'complete_inventory_verdict_unproven'
    $summary = @{ Result = $(if ($withheld -eq 0) { 'PASS' } else { 'PASS_WITH_WITHHELD' }); Suite = $Suite; SourceCommit = $Commit; InventorySha256 = $whole.inventorySha256;
        InventoryCount = $whole.inventoryCount; Fired = $fired; Withheld = $withheld; RealInputAllowed = $false; SourcesRestored = $true; TargetBytesRestored = $true }
    Write-NewUtf8 (Join-Path $pool 'summary.json') ($summary | ConvertTo-Json -Compress)
    $summary | ConvertTo-Json -Compress
    exit 0
}
catch { Write-Output 'Result=FAIL'; Write-Output "FailureStage=$stage"; Write-Output ('Code=' + (Fixed-FailureCode $_)); Write-Output 'EvidenceRetained=True'; exit 1 }
finally {
    foreach ($item in $active) {
        $item.Job.Dispose()
        if (-not $item.Process.HasExited) { $item.Process.WaitForExit(10000) | Out-Null }
        $item.OutputCopy.Wait(10000) | Out-Null; $item.ErrorCopy.Wait(10000) | Out-Null
        $item.OutFile.Dispose(); $item.ErrFile.Dispose(); $item.Process.Dispose()
    }
}
