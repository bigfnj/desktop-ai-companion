#requires -Version 7
<#
.SYNOPSIS
Review or stop only the exact owned Guards pool for a verified scheduler handoff.
.DESCRIPTION
Default mode is read-only. Apply is deliberately fixed to one previously reviewed
controller lifetime. It never explicitly terminates a worker or descendant.
#>
[CmdletBinding()]
param([switch]$Apply, [switch]$SelfTest, [switch]$NativeFileSelfTest)
$ErrorActionPreference = 'Stop'
$handoffRepository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$handoffPool = Join-Path $handoffRepository 'artifacts\ms-a3a58735'
$handoffCommit = '1cbddbb4b61c8c6e87c74d7521edf51c55a17499'
$ownedHandoffControllerId = 39072
$handoffControllerBirth = [long]134371098438469636
$handoffDigest = 'db56d0c7cbf2f1e368772e236658cd33fca759563dae3da0cdf948260da97601'

function Assert-Handoff([bool]$Value, [string]$Code) { if (-not $Value) { throw $Code } }
function Assert-HandoffIdentity($Actual, $Expected) {
    Assert-Handoff ($Actual.ProcessId -eq $Expected.ProcessId -and
        $Actual.CreationFileTime -eq $Expected.CreationFileTime -and
        $Actual.Image.Equals($Expected.Image, [StringComparison]::OrdinalIgnoreCase) -and
        $Actual.UserSid -ceq $Expected.UserSid) 'controller_incarnation_refused'
}
function Assert-HandoffArguments([string[]]$Actual, [string[]]$Expected) {
    Assert-Handoff ($Actual.Count -eq $Expected.Count) 'owned_command_refused'
    for ($i = 0; $i -lt $Actual.Count; $i++) {
        Assert-Handoff ($Actual[$i].Equals($Expected[$i], [StringComparison]::OrdinalIgnoreCase)) 'owned_command_refused'
    }
}
function Assert-HandoffChild($Child, $Parent, [string]$UserSid) {
    Assert-Handoff ($Child.ProcessId -gt 0 -and $Child.ProcessId -ne $Parent.ProcessId -and
        $Child.ParentProcessId -eq $Parent.ProcessId -and $Child.CreationFileTime -ge $Parent.CreationFileTime -and
        ([long]$Parent.ExitFileTime -eq 0 -or $Child.CreationFileTime -le [long]$Parent.ExitFileTime) -and
        $Child.UserSid -ceq $UserSid -and $Child.InJob -is [bool] -and $Child.InJob) 'owned_descendant_refused'
}
function Test-HandoffChildBirth([long]$Birth, [long]$ParentBirth, [long]$ParentExit) {
    # CIM rounds native timestamps down to a microsecond. A boundary ambiguity is
    # excluded here before opening a possible reused-PID child or reading args.
    return $Birth -ge $ParentBirth -and ($ParentExit -eq 0 -or $Birth + 9 -le $ParentExit)
}
function Assert-HandoffOriginalBytes([string]$Expected, [byte[]]$Original) {
    Assert-Handoff ($null -ne $Original -and $Original.Length -le 1MB -and
        $Expected -cmatch '^[0-9A-F]{64}$' -and
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Original)) -ceq $Expected) 'restore_original_bytes_refused'
}
function Assert-HandoffRestoreEntry([string]$Expected, [byte[]]$Original, [string]$StoppedHash, [bool]$Stopped) {
    Assert-Handoff $Stopped 'restore_before_stop_refused'
    Assert-HandoffOriginalBytes $Expected $Original
    Assert-Handoff ($StoppedHash -cmatch '^[0-9A-F]{64}$') 'stopped_hash_invalid'
}
function Read-HandoffOriginalBuffers($Completed,
    [scriptblock]$ReadBytes = { param($Path) Assert-NoReparse $Path; return ,([MutationHandoffProcess]::ReadPublicSource($Path)) }) {
    $buffers = @{}
    foreach ($entry in $Completed.Snapshot) {
        $bytes = & $ReadBytes (Join-Path $Completed.Directory $entry.Path)
        Assert-HandoffOriginalBytes $entry.Sha256 $bytes
        $buffers.Add($entry.Path, [byte[]]$bytes)
    }
    return $buffers
}
function Assert-HandoffScopedIdentity($Actual, [hashtable]$Known, [string]$UserSid, [bool]$RequireStopped) {
    Assert-Handoff ($Actual.UserSid -ceq $UserSid) 'foreign_pool_process_refused'
    Assert-Handoff ($Known.ContainsKey([int]$Actual.ProcessId) -and
        $Known[[int]$Actual.ProcessId].CreationFileTime -eq $Actual.CreationFileTime) 'orphan_pool_process_refused'
    Assert-Handoff (-not $RequireStopped) 'old_pool_process_still_alive'
}
function Get-HandoffPoolPrefixes([string]$LongPath,
    [scriptblock]$ReadShort = { param($Path) [MutationShardJob]::ShortPath($Path) }) {
    $prefixes = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [void]$prefixes.Add($LongPath.TrimEnd([IO.Path]::DirectorySeparatorChar))
    $short = & $ReadShort $LongPath
    Assert-Handoff (-not [string]::IsNullOrWhiteSpace($short) -and [IO.Path]::IsPathFullyQualified($short)) 'pool_alias_unavailable'
    [void]$prefixes.Add($short.TrimEnd([IO.Path]::DirectorySeparatorChar))
    return $prefixes
}
function Invoke-HandoffTransition([bool]$DoApply, [scriptblock]$Stop, [scriptblock]$Wait,
    [scriptblock]$Restore, [scriptblock]$Publish) {
    if (-not $DoApply) { return 'READ_ONLY' }
    & $Stop
    Assert-Handoff (& $Wait) 'owned_process_exit_timeout'
    & $Restore
    & $Publish
    return 'HANDOFF_READY'
}
function Test-HandoffPure {
    $checks = 0
    $expected = @{ ProcessId = 17; CreationFileTime = [long]123; Image = 'C:\synthetic\pwsh.exe'; UserSid = 'S-1-5-21-123' }
    Assert-HandoffIdentity $expected $expected; $checks++
    foreach ($key in @('ProcessId', 'CreationFileTime', 'Image', 'UserSid')) {
        $bad = $expected.Clone()
        $bad[$key] = $(if ($key -in @('ProcessId', 'CreationFileTime')) { 999 } else { 'different' })
        $refused = $false
        try { Assert-HandoffIdentity $bad $expected } catch { $refused = $true }
        Assert-Handoff $refused 'selftest_identity_refusal'; $checks++
    }
    $arguments = @('pwsh.exe', '-File', 'owned.ps1', '-Commit', 'known', '-Worker')
    Assert-HandoffArguments $arguments $arguments; $checks++
    foreach ($badArguments in @(@('pwsh.exe', '-File', 'other.ps1', '-Commit', 'known', '-Worker'),
        @('pwsh.exe', '-File', 'owned.ps1', '-Commit', 'different', '-Worker'),
        @('pwsh.exe', '-File', 'owned.ps1', '-Commit', 'known', '-Worker', '-Command', 'unexpected'))) {
        $refused = $false
        try { Assert-HandoffArguments $badArguments $arguments } catch { $refused = $true }
        Assert-Handoff $refused 'selftest_command_refusal'; $checks++
    }
    $child = @{ ProcessId = 18; ParentProcessId = 17; CreationFileTime = [long]124; UserSid = $expected.UserSid; InJob = $true }
    Assert-HandoffChild $child $expected $expected.UserSid; $checks++
    foreach ($change in @(@{ ParentProcessId = 99 }, @{ CreationFileTime = 1 }, @{ UserSid = 'other' }, @{ InJob = $false }, @{ InJob = 'true' })) {
        $bad = $child.Clone()
        foreach ($key in $change.Keys) { $bad[$key] = $change[$key] }
        $refused = $false
        try { Assert-HandoffChild $bad $expected $expected.UserSid } catch { $refused = $true }
        Assert-Handoff $refused 'selftest_descendant_refusal'; $checks++
    }
    $deadParent = $expected.Clone(); $deadParent.ExitFileTime = [long]125
    $reusedChild = $child.Clone(); $reusedChild.CreationFileTime = [long]126
    $refused = $false
    try { Assert-HandoffChild $reusedChild $deadParent $expected.UserSid } catch { $refused = $true }
    Assert-Handoff $refused 'selftest_reused_parent_refusal'; $checks++
    Assert-Handoff (-not (Test-HandoffChildBirth 126 123 125) -and -not (Test-HandoffChildBirth 124 123 125) -and
        (Test-HandoffChildBirth 124 123 0) -and (Test-HandoffChildBirth 124 123 140)) 'selftest_birth_window'; $checks++
    $scope = @{ 18 = $child }
    Assert-HandoffScopedIdentity $child $scope $expected.UserSid $false; $checks++
    foreach ($entry in @(@{ Value = $child; Known = @{}; Sid = $expected.UserSid; Stopped = $false },
        @{ Value = $child; Known = $scope; Sid = 'different'; Stopped = $false },
        @{ Value = $child; Known = $scope; Sid = $expected.UserSid; Stopped = $true })) {
        $refused = $false
        try { Assert-HandoffScopedIdentity $entry.Value $entry.Known $entry.Sid $entry.Stopped } catch { $refused = $true }
        Assert-Handoff $refused 'selftest_orphan_or_live_refusal'; $checks++
    }
    $prefixes = @(Get-HandoffPoolPrefixes 'C:\synthetic pool' { param($Path) 'C:\SYNTHE~1' })
    Assert-Handoff ($prefixes.Count -eq 2 -and $prefixes -ccontains 'C:\synthetic pool' -and $prefixes -ccontains 'C:\SYNTHE~1') 'selftest_short_alias_missing'; $checks++
    $prefixes = @(Get-HandoffPoolPrefixes 'C:\synthetic' { param($Path) 'c:\SYNTHETIC' })
    Assert-Handoff ($prefixes.Count -eq 1) 'selftest_alias_duplicate'; $checks++
    $original = [Text.Encoding]::UTF8.GetBytes('pristine public source')
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($original))
    Assert-HandoffRestoreEntry $hash $original $hash $true; $checks++
    $script:handoffPreflightStops = 0
    $refused = $false
    try {
        $syntheticCompleted = @{ Directory = 'C:\synthetic'; Snapshot = @(@{ Path = 'ordinary.cs'; Sha256 = $hash }) }
        Read-HandoffOriginalBuffers $syntheticCompleted { param($Path) return ,([byte[]]::new(1048577)) } | Out-Null
        $script:handoffPreflightStops++
    } catch { $refused = $true }
    Assert-Handoff ($refused -and $script:handoffPreflightStops -eq 0) 'selftest_preflight_before_stop'; $checks++
    foreach ($attempt in @(@{ Hash = $hash; Bytes = $original; Stopped = $false },
        @{ Hash = ('A' * 64); Bytes = $original; Stopped = $true },
        @{ Hash = $hash; Bytes = [byte[]]@(1, 2); Stopped = $true })) {
        $refused = $false
        try { Assert-HandoffRestoreEntry $attempt.Hash $attempt.Bytes $hash $attempt.Stopped } catch { $refused = $true }
        Assert-Handoff $refused 'selftest_restore_refusal'; $checks++
    }
    $script:handoffTestTrace = [Collections.Generic.List[string]]::new()
    $stop = { $script:handoffTestTrace.Add('stop') }
    $wait = { $script:handoffTestTrace.Add('wait'); return $true }
    $restore = { $script:handoffTestTrace.Add('restore') }
    $publish = { $script:handoffTestTrace.Add('publish') }
    Assert-Handoff ((Invoke-HandoffTransition $false $stop $wait $restore $publish) -ceq 'READ_ONLY' -and $script:handoffTestTrace.Count -eq 0) 'selftest_readonly_mutated'; $checks++
    Assert-Handoff ((Invoke-HandoffTransition $true $stop $wait $restore $publish) -ceq 'HANDOFF_READY' -and
        ($script:handoffTestTrace -join ',') -ceq 'stop,wait,restore,publish') 'selftest_ordering'; $checks++
    $script:handoffTestTrace.Clear()
    $refused = $false
    try { Invoke-HandoffTransition $true $stop { $script:handoffTestTrace.Add('wait'); return $false } $restore $publish | Out-Null } catch { $refused = $true }
    Assert-Handoff ($refused -and ($script:handoffTestTrace -join ',') -ceq 'stop,wait') 'selftest_early_restore'; $checks++
    $script:handoffTestTrace.Clear()
    $refused = $false
    try { Invoke-HandoffTransition $true $stop $wait { throw 'synthetic_restore_failure' } $publish | Out-Null } catch { $refused = $true }
    Assert-Handoff ($refused -and ($script:handoffTestTrace -join ',') -ceq 'stop,wait') 'selftest_failed_restore_published'; $checks++
    $script:handoffTestTrace.Clear()
    $refused = $false
    try { Invoke-HandoffTransition $true { throw 'synthetic_stop_failure' } $wait $restore $publish | Out-Null } catch { $refused = $true }
    Assert-Handoff ($refused -and $script:handoffTestTrace.Count -eq 0) 'selftest_failed_stop_continued'; $checks++
    Write-Output "HandoffSelfTest=$checks PASS"
}
if ($SelfTest) { Assert-Handoff (-not $Apply -and -not $NativeFileSelfTest) 'selftest_apply_refused'; Test-HandoffPure; exit 0 }
Assert-Handoff (-not ($NativeFileSelfTest -and $Apply)) 'selftest_apply_refused'

# LibraryOnly defines reviewed pure validators. It performs no worktree or process action.
$preLibraryHandoffIdentity = @{ ProcessId = $ownedHandoffControllerId; Birth = $handoffControllerBirth; Pool = $handoffPool; Commit = $handoffCommit; Apply = [bool]$Apply }
. (Join-Path $PSScriptRoot 'Invoke-MutationShards.ps1') -LibraryOnly -Commit $handoffCommit -Suite Guards
Assert-Handoff ($ownedHandoffControllerId -eq $preLibraryHandoffIdentity.ProcessId -and
    $handoffControllerBirth -eq $preLibraryHandoffIdentity.Birth -and $handoffPool -ceq $preLibraryHandoffIdentity.Pool -and
    $handoffCommit -ceq $preLibraryHandoffIdentity.Commit -and [bool]$Apply -eq $preLibraryHandoffIdentity.Apply) 'library_changed_handoff_identity'
if (-not $NativeFileSelfTest) {
    $localTools = [Environment]::GetFolderPath('LocalApplicationData')
    $git = Resolve-CanonicalTool @((Join-Path $localTools 'Programs\Git\cmd\git.exe'), (Join-Path $env:ProgramFiles 'Git\cmd\git.exe'))
    $python = Resolve-CanonicalTool @((Join-Path $localTools 'Programs\Python\Python314\python.exe'))
    $pwsh = Resolve-CanonicalTool @((Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'))
    $dotnet = Resolve-CanonicalTool @((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))
}
$handoffPool = Assert-RemainderPoolPath $handoffPool $handoffRepository
Initialize-NativeJob
if (-not ('MutationHandoffProcess' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
public sealed class MutationHandoffProcess : IDisposable {
  sealed class ExitedDuringInspection : Exception { }
  readonly SafeProcessHandle handle;
  readonly bool mayTerminate;
  [StructLayout(LayoutKind.Sequential)] struct FileInformation { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME A,B,C; public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow; }
  public readonly int ProcessId;
  public readonly long CreationFileTime;
  public readonly string Image;
  public readonly string UserSid;
  [DllImport("kernel32.dll", SetLastError=true)] static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder result, ref int length);
  [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool IsProcessInJob(SafeProcessHandle process, IntPtr job, out bool result);
  [DllImport("kernel32.dll", SetLastError=true)] static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateProcess(SafeProcessHandle process, uint code);
  [DllImport("shell32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CommandLineToArgvW(string command, out int count);
  [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder result, uint length, uint flags);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateHardLink(string link, string existing, IntPtr attributes);
  public static void LinkSyntheticPublicFile(string link, string existing) { if (!CreateHardLink(link, existing, IntPtr.Zero)) throw new Win32Exception(); }
  public static MutationHandoffProcess TryOpen(int id, bool terminate) {
    try { return new MutationHandoffProcess(id, terminate); }
    catch (ExitedDuringInspection) { return null; }
    catch (Win32Exception error) when (error.NativeErrorCode == 87 || error.NativeErrorCode == 1168) { return null; }
  }
  public MutationHandoffProcess(int id, bool terminate) {
    ProcessId = id; mayTerminate = terminate;
    handle = OpenProcess(0x1000 | 0x100000 | (terminate ? 1u : 0u), false, id);
    if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(); }
    try {
      if (!GetProcessTimes(handle, out long created, out _, out _, out _)) throw new Win32Exception();
      CreationFileTime = created;
      var image = new StringBuilder(32768); int size = image.Capacity;
      if (!QueryFullProcessImageName(handle, 0, image, ref size)) throw new Win32Exception();
      Image = image.ToString();
      if (!OpenProcessToken(handle, 8, out var token)) throw new Win32Exception();
      using (token) using (var identity = new WindowsIdentity(token.DangerousGetHandle())) { UserSid = identity.User.Value; }
    } catch { bool exited = WaitForSingleObject(handle, 0) == 0; handle.Dispose(); if (exited) throw new ExitedDuringInspection(); throw; }
  }
  public bool Exited { get { uint result = WaitForSingleObject(handle, 0); if (result == 0) return true; if (result == 258) return false; throw new Win32Exception(); } }
  public long ExitFileTime { get { if (!GetProcessTimes(handle, out _, out long exited, out _, out _)) throw new Win32Exception(); return exited; } }
  public bool InJob { get { if (!IsProcessInJob(handle, IntPtr.Zero, out bool result)) throw new Win32Exception(); return result; } }
  public void StopController() { if (!mayTerminate) throw new InvalidOperationException("descendant_termination_refused"); if (!TerminateProcess(handle, 1)) throw new Win32Exception(); }
  public static string[] Arguments(string command) {
    IntPtr memory = CommandLineToArgvW(command, out int count);
    if (memory == IntPtr.Zero) throw new Win32Exception();
    try { if (count < 1 || count > 64) throw new InvalidOperationException("argument_count_refused"); var result = new string[count]; for (int i=0;i<count;i++) result[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory,i*IntPtr.Size)); return result; }
    finally { LocalFree(memory); }
  }
  static FileStream OpenPublicSource(string path, bool write) {
    var handle = CreateFile(path, write ? 0xc0000000u : 0x80000000u, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
    if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(); }
    try {
      if (!GetFileInformationByHandle(handle, out var information)) throw new Win32Exception();
      if ((information.Attributes & 0x410) != 0 || information.Links != 1) throw new IOException("public_source_identity_refused");
      var final = new StringBuilder(32768); uint count = GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 0);
      if (count == 0 || count >= final.Capacity || !final.ToString().Equals("\\\\?\\" + Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) throw new IOException("public_source_path_refused");
      var stream = new FileStream(handle, write ? FileAccess.ReadWrite : FileAccess.Read);
      if (stream.Length > 1048576) { stream.Dispose(); throw new IOException("public_source_oversized"); }
      return stream;
    } catch { handle.Dispose(); throw; }
  }
  public static byte[] ReadPublicSource(string path) { using var stream = OpenPublicSource(path, false); using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray(); }
  public static void RestorePublicSource(string path, byte[] original, string expectedOriginalHash, string expectedStoppedHash) {
    if (original.Length > 1048576 || Convert.ToHexString(SHA256.HashData(original)) != expectedOriginalHash) throw new IOException("restore_original_bytes_refused");
    using var stream = OpenPublicSource(path, true);
    if (Convert.ToHexString(SHA256.HashData(stream)) != expectedStoppedHash) throw new IOException("stopped_source_changed");
    stream.Position=0; stream.SetLength(original.Length); stream.Write(original); stream.Flush(true);
  }
  public void Dispose() { handle.Dispose(); }
}
'@
}

if ($NativeFileSelfTest) {
    # The real library import above must preserve the exact stop target and mode.
    $checks = 1
    $syntheticRoot = Join-Path $handoffRepository ('artifacts\handoff-files-' + [guid]::NewGuid().ToString('N'))
    Assert-NoReparse $syntheticRoot
    $syntheticRoot = [IO.Directory]::CreateDirectory($syntheticRoot).FullName
    $ordinary = Join-Path $syntheticRoot 'ordinary.txt'
    $alias = Join-Path $syntheticRoot 'alias.txt'
    try {
        $original = [byte[]]@(239, 187, 191, 97, 13, 10, 195, 169, 13)
        $mutant = [Text.Encoding]::UTF8.GetBytes('synthetic mutant')
        $originalHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($original))
        $mutantHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($mutant))
        [IO.File]::WriteAllBytes($ordinary, $original)
        $read = [MutationHandoffProcess]::ReadPublicSource($ordinary)
        Assert-Handoff ([Convert]::ToBase64String($read) -ceq [Convert]::ToBase64String($original)) 'native_selftest_roundtrip'; $checks++
        [IO.File]::WriteAllBytes($ordinary, $mutant)
        $refused = $false
        try { [MutationHandoffProcess]::RestorePublicSource($ordinary, $original, $originalHash, $originalHash) } catch { $refused = $true }
        Assert-Handoff ($refused -and [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([MutationHandoffProcess]::ReadPublicSource($ordinary))) -ceq $mutantHash) 'native_selftest_concurrent_refusal'; $checks++
        $refused = $false
        try { [MutationHandoffProcess]::RestorePublicSource($ordinary, $original, ('A' * 64), $mutantHash) } catch { $refused = $true }
        Assert-Handoff ($refused -and [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([MutationHandoffProcess]::ReadPublicSource($ordinary))) -ceq $mutantHash) 'native_selftest_original_refusal'; $checks++
        [MutationHandoffProcess]::RestorePublicSource($ordinary, $original, $originalHash, $mutantHash)
        Assert-Handoff ([Convert]::ToBase64String([MutationHandoffProcess]::ReadPublicSource($ordinary)) -ceq [Convert]::ToBase64String($original)) 'native_selftest_exact_restore'; $checks++
        [MutationHandoffProcess]::LinkSyntheticPublicFile($alias, $ordinary)
        $refused = $false
        try { [MutationHandoffProcess]::ReadPublicSource($alias) | Out-Null } catch { $refused = $true }
        Assert-Handoff $refused 'native_selftest_multilink_read'; $checks++
        $refused = $false
        try { [MutationHandoffProcess]::RestorePublicSource($alias, $mutant, $mutantHash, $originalHash) } catch { $refused = $true }
        Assert-Handoff $refused 'native_selftest_multilink_write'; $checks++
        [IO.File]::Delete($alias)
        Assert-Handoff ([Convert]::ToBase64String([MutationHandoffProcess]::ReadPublicSource($ordinary)) -ceq [Convert]::ToBase64String($original)) 'native_selftest_link_preserved'; $checks++
        Assert-HandoffArguments ([MutationHandoffProcess]::Arguments('"C:\synthetic tools\pwsh.exe" -File "C:\synthetic tools\owned.ps1"')) @('C:\synthetic tools\pwsh.exe', '-File', 'C:\synthetic tools\owned.ps1'); $checks++
        Write-Output "HandoffNativeFileSelfTest=$checks PASS"
    }
    finally {
        Assert-NoReparse $syntheticRoot
        Assert-Handoff ((Split-Path $syntheticRoot -Parent).Equals((Join-Path $handoffRepository 'artifacts'), [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path $syntheticRoot -Leaf) -cmatch '^handoff-files-[a-f0-9]{32}$') 'synthetic_cleanup_path_refused'
        foreach ($path in @($alias, $ordinary)) { if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) } }
        [IO.Directory]::Delete($syntheticRoot, $false)
    }
    exit 0
}

function Read-HandoffProcess([int]$ProcessId, [bool]$MayTerminate) {
    $row = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -Property ProcessId,ParentProcessId,CreationDate
    if (-not $row) { return $null }
    $retained = [MutationHandoffProcess]::TryOpen($ProcessId, $MayTerminate)
    if (-not $retained) { return $null }
    try {
        Assert-Handoff ([math]::Abs($retained.CreationFileTime - $row.CreationDate.ToUniversalTime().ToFileTimeUtc()) -lt 10) 'process_metadata_raced'
        if ($retained.Exited) { $retained.Dispose(); return $null }
        return @{ ProcessId = $ProcessId; ParentProcessId = [int]$row.ParentProcessId; CreationFileTime = $retained.CreationFileTime; ExitFileTime = $retained.ExitFileTime;
            Image = $retained.Image; UserSid = $retained.UserSid; InJob = $retained.InJob; Retained = $retained }
    }
    catch { $retained.Dispose(); throw }
}
function Read-HandoffArguments($Identity) {
    Assert-Handoff (-not $Identity.Retained.Exited) 'command_owner_already_exited'
    $utc = [datetime]::FromFileTimeUtc($Identity.CreationFileTime)
    $birth = $utc.ToString('yyyyMMddHHmmss.ffffff', [Globalization.CultureInfo]::InvariantCulture) + '+000'
    # A reused numeric PID cannot make its argv eligible for this read.
    $row = Get-CimInstance Win32_Process -Filter "ProcessId=$($Identity.ProcessId) AND CreationDate='$birth'" -Property CreationDate,CommandLine
    Assert-Handoff ($null -ne $row -and [math]::Abs($Identity.CreationFileTime - $row.CreationDate.ToUniversalTime().ToFileTimeUtc()) -lt 10) 'owned_command_metadata_unavailable'
    return ,([MutationHandoffProcess]::Arguments($row.CommandLine))
}
function Find-HandoffDescendants($Controller, [hashtable]$Known) {
    $queue = [Collections.Generic.Queue[object]]::new()
    foreach ($identity in @($Known.Values)) { $queue.Enqueue($identity) }
    $visited = [Collections.Generic.HashSet[int]]::new()
    while ($queue.Count -gt 0) {
        $parent = $queue.Dequeue()
        if (-not $visited.Add([int]$parent.ProcessId)) { continue }
        $parent.ExitFileTime = $parent.Retained.ExitFileTime
        $rows = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($parent.ProcessId)" -Property ProcessId,ParentProcessId,CreationDate)
        foreach ($row in $rows) {
            $id = [int]$row.ProcessId
            if ($Known.ContainsKey($id)) { $queue.Enqueue($Known[$id]); continue }
            if (-not (Test-HandoffChildBirth $row.CreationDate.ToUniversalTime().ToFileTimeUtc() $parent.CreationFileTime $parent.ExitFileTime)) { continue }
            Assert-Handoff ($Known.Count -lt 128) 'owned_tree_limit'
            $parent.ExitFileTime = $parent.Retained.ExitFileTime
            if (-not (Test-HandoffChildBirth $row.CreationDate.ToUniversalTime().ToFileTimeUtc() $parent.CreationFileTime $parent.ExitFileTime)) { continue }
            $node = Read-HandoffProcess $id $false
            if (-not $node) { continue }
            try {
                $parent.ExitFileTime = $parent.Retained.ExitFileTime
                Assert-HandoffChild $node $parent $Controller.UserSid
                if ($parent.ProcessId -eq $Controller.ProcessId) {
                    Assert-Handoff ($node.Image.Equals($pwsh, [StringComparison]::OrdinalIgnoreCase)) 'owned_worker_image_refused'
                    $args = Read-HandoffArguments $node
                    $index = 0
                    if ($args.Count -gt 0) { [void][int]::TryParse($args[-1], [ref]$index) }
                    Assert-Handoff ($index -ge 1 -and $index -le 4) 'owned_worker_index_refused'
                    $expectedArgs = @($pwsh, '-NoProfile', '-NonInteractive', '-File',
                        (Join-Path $handoffPool "w$index\tools\agentbroker\Invoke-MutationShards.ps1"),
                        '-Worker', '-Commit', $handoffCommit, '-Suite', 'Guards', '-Shards', '4', '-ShardIndex', [string]$index)
                    Assert-HandoffArguments $args $expectedArgs
                    $gate = Read-RemainderText (Join-Path $handoffPool "g$index")
                    Assert-Handoff ($gate -ceq 'assigned') 'worker_gate_unproven'
                }
                $Known.Add($id, $node); $queue.Enqueue($node)
            }
            catch { $node.Retained.Dispose(); throw }
        }
    }
}
function Assert-HandoffPoolProcesses([hashtable]$Known, [string]$UserSid, [bool]$RequireStopped) {
    # The provider filters on this one exact owned pool. No other process argv is
    # returned or inspected. This catches an orphaned source-writing harness even
    # when its original parent had already exited before ancestry capture.
    $poolPrefixes = @(Get-HandoffPoolPrefixes $handoffPool)
    $predicates = foreach ($prefix in $poolPrefixes) {
        $literal = $prefix.Replace('\', '\\').Replace("'", "\'")
        $pattern = "%$literal\\%"
        "(ExecutablePath LIKE '$pattern' OR ((Name='python.exe' OR Name='pwsh.exe' OR Name='dotnet.exe' OR Name='cmd.exe') AND CommandLine LIKE '$pattern'))"
    }
    $filter = $predicates -join ' OR '
    $rows = @(Get-CimInstance Win32_Process -Filter $filter -Property ProcessId,ParentProcessId,CreationDate)
    Assert-Handoff ($rows.Count -le 128) 'scoped_pool_process_limit'
    $seen = [Collections.Generic.HashSet[int]]::new()
    foreach ($row in $rows) {
        if (-not $seen.Add([int]$row.ProcessId)) { continue }
        $node = Read-HandoffProcess ([int]$row.ProcessId) $false
        if (-not $node) { continue }
        try {
            $insidePool = @($poolPrefixes.Where({ $node.Image.StartsWith($_ + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) })).Count -gt 0
            $canonical = $insidePool -or $node.Image.Equals($pwsh, [StringComparison]::OrdinalIgnoreCase) -or
                $node.Image.Equals($python, [StringComparison]::OrdinalIgnoreCase) -or $node.Image.Equals($dotnet, [StringComparison]::OrdinalIgnoreCase) -or
                $node.Image.Equals((Join-Path ([Environment]::GetFolderPath('System')) 'cmd.exe'), [StringComparison]::OrdinalIgnoreCase)
            Assert-Handoff $canonical 'scoped_pool_image_refused'
            Assert-HandoffScopedIdentity $node $Known $UserSid $RequireStopped
        }
        finally { $node.Retained.Dispose() }
    }
}

$known = @{}
try {
    $controller = Read-HandoffProcess $ownedHandoffControllerId $true
    Assert-Handoff ($null -ne $controller) 'owned_controller_absent'
    $known.Add($ownedHandoffControllerId, $controller)
    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { $sid = $currentUser.User.Value } finally { $currentUser.Dispose() }
    Assert-HandoffIdentity $controller @{ ProcessId = $ownedHandoffControllerId; CreationFileTime = $handoffControllerBirth; Image = $pwsh; UserSid = $sid }
    Assert-HandoffArguments (Read-HandoffArguments $controller) @($pwsh, '-NoProfile', '-File',
        'tools/agentbroker/Invoke-MutationShards.ps1', '-Commit', $handoffCommit, '-Suite', 'Guards', '-Shards', '4', '-MaxParallel', '1')
    Find-HandoffDescendants $controller $known
    Assert-Handoff ($known.Count -gt 1) 'owned_worker_unproven'
    Assert-HandoffPoolProcesses $known $sid $false
    $completedPath = Join-Path $handoffPool 'result-1.json'
    if (-not [IO.File]::Exists($completedPath)) {
        Assert-Handoff (-not $Apply) 'completed_shard_missing'
        @{ Mode = 'READ_ONLY'; ReadyForApply = $false; Reason = 'completed_shard_missing'; ControllerVerified = $true; OwnedProcessCount = $known.Count } | ConvertTo-Json -Compress
        exit 0
    }
    $metadata = Get-RemainderSourceMetadata (Join-Path $handoffPool 'w1')
    Assert-Handoff ($metadata.Whole.inventoryCount -eq 1313 -and $metadata.Whole.inventorySha256 -ceq $handoffDigest -and $metadata.Targets.Count -eq 116) 'known_inventory_changed'
    $completed = Read-VerifiedImportedShard $handoffPool 1 $metadata.Whole $metadata.Targets
    Assert-Handoff ($completed.Plan.selection.count -eq 4) 'completed_shard_layout_changed'
    $pinnedWorker = Read-RemainderText (Join-Path $completed.Directory 'tools\agentbroker\Invoke-MutationShards.ps1')
    Assert-Handoff ($pinnedWorker.Contains('Flags = 0x2008, Active = 64', [StringComparison]::Ordinal) -and
        $pinnedWorker.Contains('$job.Assign($process)', [StringComparison]::Ordinal) -and
        $pinnedWorker.Contains('while (-not [IO.File]::Exists($gateFile)', [StringComparison]::Ordinal) -and
        $pinnedWorker.Contains('Write-NewUtf8 (Join-Path $pool "g$($item.Index)") ''assigned''', [StringComparison]::Ordinal)) 'pinned_job_contract_unproven'
    # Read and size/hash every original before Apply can stop a running writer.
    $originalBytes = Read-HandoffOriginalBuffers $completed
    $handoffPath = Join-Path $handoffPool 'handoff.json'
    Assert-RemainderLaunchGate $handoffPath
    if (-not $Apply) {
        @{ Mode = 'READ_ONLY'; ReadyForApply = $true; ControllerVerified = $true; OwnedProcessCount = $known.Count;
            CompletedShardIndices = @(1); WouldStopControllerOnly = $true; SourcesWritten = $false } | ConvertTo-Json -Compress
        exit 0
    }
    # Stop only this retained controller. Closing its verified job handles ends its
    # own gated workers and their descendants. No other process receives Kill.
    $stop = {
        Assert-HandoffIdentity $controller @{ ProcessId = $ownedHandoffControllerId; CreationFileTime = $handoffControllerBirth; Image = $pwsh; UserSid = $sid }
        Assert-Handoff (-not $controller.Retained.Exited) 'controller_already_exited'
        Find-HandoffDescendants $controller $known
        Assert-HandoffPoolProcesses $known $sid $false
        $controller.Retained.StopController()
    }
    $wait = {
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        $quiet = 0
        while ($deadline.Elapsed.TotalSeconds -lt 30) {
            Find-HandoffDescendants $controller $known
            $alive = @($known.Values.Where({ -not $_.Retained.Exited })).Count
            if ($alive -eq 0) { $quiet++ } else { $quiet = 0 }
            if ($quiet -ge 2) { Assert-HandoffPoolProcesses $known $sid $true; return $true }
            Start-Sleep -Milliseconds 100
        }
        return $false
    }
    $restoredIndices = [Collections.Generic.List[int]]::new()
    $restore = {
        # The original bytes come only from the now-verified completed same-pin
        # shard, never a guessed checkout or a currently running source writer.
        Assert-Source $completed.Directory
        Assert-TargetSnapshot $completed.Directory $completed.Snapshot
        Assert-HandoffPoolProcesses $known $sid $true
        for ($index = 1; $index -le 4; $index++) {
            $directory = Join-Path $handoffPool "w$index"
            $snapshot = @(Convert-RemainderJson (Read-RemainderText (Join-Path $handoffPool "source-$index.json")))
            Assert-RemainderSnapshotEqual $snapshot $completed.Snapshot
            $changed = $false
            foreach ($entry in $snapshot) {
                $path = Join-Path $directory $entry.Path
                Assert-NoReparse $path
                $currentHash = [MutationShardJob]::HashPublicFile($path)
                if ($currentHash -ceq $entry.Sha256) { continue }
                Assert-Handoff ($index -ne 1) 'completed_source_changed_after_verification'
                $bytes = $originalBytes[$entry.Path]
                Assert-HandoffRestoreEntry $entry.Sha256 $bytes $currentHash $true
                [MutationHandoffProcess]::RestorePublicSource($path, $bytes, $entry.Sha256, $currentHash)
                $changed = $true
            }
            if ($changed) { $restoredIndices.Add($index) }
            Assert-Source $directory
            Assert-TargetSnapshot $directory $snapshot
        }
    }
    $publish = {
        $identities = @($known.Values | ForEach-Object { @{ ProcessId = $_.ProcessId; CreationFileTime = $_.CreationFileTime.ToString([Globalization.CultureInfo]::InvariantCulture) } })
        $proof = @{ SchemaVersion = 1; Pool = $handoffPool; SourceCommit = $handoffCommit; InventorySha256 = $handoffDigest;
            CompletedShardIndices = @(1); Controller = @{ ProcessId = $ownedHandoffControllerId; CreationFileTime = $handoffControllerBirth.ToString([Globalization.CultureInfo]::InvariantCulture) };
            Processes = $identities; AllStopped = $true; SourcesRestored = $true; TargetBytesRestored = $true;
            RestoredPartialShardIndices = $restoredIndices.ToArray() }
        Assert-RemainderHandoff $proof $handoffPool $metadata.Whole @(1) $ownedHandoffControllerId $handoffControllerBirth.ToString([Globalization.CultureInfo]::InvariantCulture)
        Write-NewUtf8 $handoffPath ($proof | ConvertTo-Json -Depth 8 -Compress)
    }
    $outcome = Invoke-HandoffTransition $true $stop $wait $restore $publish
    @{ Result = $outcome; ControllerStopped = $true; DescendantsExplicitlyTerminated = 0; SourcesRestored = $true; HandoffPath = $handoffPath } | ConvertTo-Json -Compress
}
finally { foreach ($item in $known.Values) { $item.Retained.Dispose() } }
