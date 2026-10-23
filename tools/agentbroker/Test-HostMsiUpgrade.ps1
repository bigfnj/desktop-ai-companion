#requires -Version 7
<#
.SYNOPSIS
Validate the pinned per-user 1.6.1 upgrade. Installation requires explicit -Apply.
.EXAMPLE
pwsh -NoProfile -File tools/agentbroker/Test-HostMsiUpgrade.ps1 -ValidateOnly
.DESCRIPTION
Before -Apply, the caller must run the host-projection probe, obtain exact platform
approval, and finish every mutation/portable host test. WiX closes companions by
basename, so any noncanonical DesktopAICompanion process blocks installation.
No elevation, force termination, editor restart, or credential/identity-file reads.
#>
[CmdletBinding()]
param([switch]$ValidateOnly, [switch]$Apply, [switch]$SelfTest,
    [ValidateRange(30, 1800)][int]$TimeoutSeconds = 900)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifact = Join-Path $repo 'artifacts\ci-host161-2f83c69-39721239129'
$msiPath = Join-Path $artifact 'DesktopAICompanion.msi'
$zipPath = Join-Path $artifact 'DesktopAICompanion-Portable.zip'
$msiHash = 'EE54A1C1830A2ABB9787E68B024AE7E4398FF1A86E540FCD1FBF225DD8C2B873'
$zipHash = '6A7751FBA1B5895033ECA100331149B7B2449322F9E9BA950D137D222C14EFB7'
$upgradeCode = '{327AF68D-A714-5C9B-B2D9-9C3404E03D38}'
$targetVersion = '1.6.1'
$utf8 = [Text.UTF8Encoding]::new($false, $true)

function Require([bool]$Condition, [string]$Code) { if (-not $Condition) { throw $Code } }
function Assert-NoProtectedDirectories([string]$Path) {
    $normalized = $Path.Replace('\', '/')
    Require ($normalized -notmatch '(?i)(?:^|/)(?:\.git/credentials|\.mozilla|\.config/(?:google-chrome|chromium)|Mozilla/Firefox)(?:/|$)') 'protected_directory_refused'
}
function Assert-PublicPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    Assert-NoProtectedDirectories $full
    Require ($full -cmatch '^[A-Za-z]:\\' -and $full -notmatch '[\x00-\x1f\x7f]') 'path_refused'
    foreach ($part in $full.Substring(3).Split('\')) {
        Require ($part -notmatch '(?i)(\.env$|^\.env\.|^\.npmrc$|\.(pem|key|p12|pfx|keystore)$|^privatekey)' -and
            $part -notmatch '[:]|[. ]$' -and $part -notin @('.', '..')) 'protected_or_unsafe_path_refused'
    }
    return $full
}
function Assert-NoReparse([string]$Path) {
    $full = Assert-PublicPath $Path
    $current = $full.Substring(0, 3)
    foreach ($part in $full.Substring(3).Split('\', [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $part
        try { $attributes = [IO.File]::GetAttributes($current) }
        catch [IO.FileNotFoundException] { continue }
        catch [IO.DirectoryNotFoundException] { continue }
        Require (($attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'reparse_path_refused'
    }
}
function Assert-ArchiveName([string]$Name) {
    Assert-NoProtectedDirectories $Name
    Require (-not [string]::IsNullOrWhiteSpace($Name) -and -not [IO.Path]::IsPathRooted($Name) -and
        $Name -notmatch '[\\:\x00-\x1f\x7f]') 'archive_path_refused'
    foreach ($part in $Name.TrimEnd('/').Split('/')) {
        Require ($part -notin @('', '.', '..') -and $part -notmatch '[. ]$' -and
            $part -notmatch '(?i)(\.env$|^\.env\.|^\.npmrc$|\.(pem|key|p12|pfx|keystore)$|^privatekey)' -and
            $part -notmatch '(?i)^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)') 'protected_or_unsafe_archive_entry'
    }
}
function Assert-MsiMetadata($Metadata) {
    Require ($Metadata['ProductVersion'] -ceq '1.6.1' -and
        $Metadata['UpgradeCode'] -ieq $upgradeCode -and $Metadata['ProductName'] -ceq 'Desktop AI Companion') 'msi_product_metadata_mismatch'
    # https://learn.microsoft.com/windows/win32/msi/word-count-summary
    Require ([string]::IsNullOrEmpty($Metadata['ALLUSERS']) -and
        ([int]$Metadata['WordCount'] -band 8) -ne 0) 'msi_not_limited_per_user'
    Require ([string]::IsNullOrEmpty($Metadata['CLEANINSTALL'])) 'clean_install_default_refused'
}
function Assert-ProcessSet($Before, $After) {
    Require ($After.ForeignCount -eq 0) 'foreign_companion_process_present'
    Require ($Before.Lifetimes.Count -eq $After.Lifetimes.Count) 'companion_process_set_changed'
    foreach ($key in $Before.Lifetimes.Keys) {
        Require ($After.Lifetimes.ContainsKey($key) -and $After.Lifetimes[$key] -ceq $Before.Lifetimes[$key]) 'companion_process_set_changed'
    }
}
function Assert-Snapshot($Before, $After, [string]$Code) {
    Require ($Before.Count -eq $After.Count) $Code
    foreach ($key in $Before.Keys) { Require ($After.ContainsKey($key) -and $Before[$key] -ceq $After[$key]) $Code }
}
function Initialize-Native {
    if ('HostMsiUpgradeNative' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
public static class HostMsiUpgradeNative {
  [StructLayout(LayoutKind.Sequential)] struct FileInfo { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME A,B,C; public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow; }
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInfo information);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,StringBuilder path,uint size,uint flags);
  [DllImport("kernel32.dll", SetLastError=true)] static extern uint GetFileType(SafeFileHandle handle);
  [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiOpenDatabase(string path,IntPtr mode,out uint database);
  [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiDatabaseOpenView(uint database,string sql,out uint view);
  [DllImport("msi.dll")] static extern uint MsiViewExecute(uint view,uint record);
  [DllImport("msi.dll")] static extern uint MsiViewFetch(uint view,out uint record);
  [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiRecordGetString(uint record,uint field,StringBuilder value,ref uint size);
  [DllImport("msi.dll")] static extern uint MsiCloseHandle(uint handle);
  [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiGetSummaryInformation(uint database,string path,uint updateCount,out uint summary);
  [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiSummaryInfoGetProperty(uint summary,uint property,out uint type,out int value,out System.Runtime.InteropServices.ComTypes.FILETIME time,StringBuilder text,ref uint size);
  [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiEnumRelatedProducts(string upgrade,uint reserved,uint index,StringBuilder product);
  [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiGetProductInfo(string product,string property,StringBuilder value,ref uint size);
  [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetProcessTimes(SafeProcessHandle process,out long created,out long exited,out long kernel,out long user);
  delegate bool WindowCallback(IntPtr window,IntPtr state);
  [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback,IntPtr state);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
  [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SendMessageTimeout(IntPtr window,uint message,IntPtr wparam,IntPtr lparam,uint flags,uint timeout,out IntPtr result);
  public static FileStream OpenPublic(string path,long maximum) {
    var handle=CreateFile(path,0x80000000,1,IntPtr.Zero,3,0x00200000,IntPtr.Zero);
    if(handle.IsInvalid) { handle.Dispose(); throw new IOException("public_file_unavailable"); }
    try {
      var final=new StringBuilder(32768); uint length=GetFinalPathNameByHandle(handle,final,(uint)final.Capacity,0);
      string actual=final.ToString(); if(actual.StartsWith(@"\\?\",StringComparison.Ordinal)) actual=actual.Substring(4);
      if(!GetFileInformationByHandle(handle,out var info) || (info.Attributes&0x410)!=0 || info.Links!=1 ||
         GetFileType(handle)!=1 || length==0 || length>=final.Capacity || !string.Equals(actual,Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase) ||
         (((ulong)info.SizeHigh<<32)|info.SizeLow)>(ulong)maximum) throw new IOException("public_file_identity_refused");
      return new FileStream(handle,FileAccess.Read);
    } catch { handle.Dispose(); throw; }
  }
  public static string Hash(Stream stream) { stream.Position=0; var result=Convert.ToHexString(SHA256.HashData(stream)); stream.Position=0; return result; }
  static string Property(uint db,string name) {
    uint view=0,record=0;
    try {
      if(MsiDatabaseOpenView(db,"SELECT `Value` FROM `Property` WHERE `Property`='"+name+"'",out view)!=0 || MsiViewExecute(view,0)!=0) throw new IOException("msi_query_failed");
      uint code=MsiViewFetch(view,out record); if(code==259) return "";
      var text=new StringBuilder(4096); uint size=(uint)text.Capacity;
      if(code!=0 || MsiRecordGetString(record,1,text,ref size)!=0) throw new IOException("msi_query_failed");
      return text.ToString();
    } finally { if(record!=0) MsiCloseHandle(record); if(view!=0) MsiCloseHandle(view); }
  }
  public static Dictionary<string,string> Metadata(string path) {
    uint db=0,summary=0;
    try {
      if(MsiOpenDatabase(path,IntPtr.Zero,out db)!=0) throw new IOException("msi_database_refused");
      var result=new Dictionary<string,string>(StringComparer.Ordinal);
      foreach(var name in new[]{"ProductVersion","ProductName","UpgradeCode","ALLUSERS","CLEANINSTALL"}) result[name]=Property(db,name);
      uint length=0; if(MsiGetSummaryInformation(db,null,0,out summary)!=0 || MsiSummaryInfoGetProperty(summary,15,out _,out int word,out _,null,ref length)!=0) throw new IOException("msi_summary_refused");
      result["WordCount"]=word.ToString(System.Globalization.CultureInfo.InvariantCulture); return result;
    } finally { if(summary!=0) MsiCloseHandle(summary); if(db!=0) MsiCloseHandle(db); }
  }
  public static List<Dictionary<string,string>> Registered(string upgrade) {
    var result=new List<Dictionary<string,string>>();
    for(uint index=0;index<32;index++) {
      var product=new StringBuilder(39); uint code=MsiEnumRelatedProducts(upgrade,0,index,product);
      if(code==259) return result; if(code!=0) throw new IOException("registration_query_failed");
      var row=new Dictionary<string,string>();
      foreach(var name in new[]{"VersionString","InstallLocation","AssignmentType","ProductName"}) {
        var value=new StringBuilder(32768); uint size=(uint)value.Capacity;
        if(MsiGetProductInfo(product.ToString(),name,value,ref size)!=0) throw new IOException("registration_query_failed");
        row[name]=value.ToString();
      }
      result.Add(row);
    }
    throw new IOException("registration_bound");
  }
  public static long Created(Process process) {
    if(!GetProcessTimes(process.SafeHandle,out long created,out _,out _,out _)) throw new IOException("process_identity_unavailable");
    return created;
  }
  public static void CloseLifetime(Process process,long expectedCreation) {
    if(process.HasExited) return;
    if(Created(process)!=expectedCreation) throw new IOException("companion_lifetime_changed");
    IntPtr target=IntPtr.Zero; int count=0;
    EnumWindows((window,_)=>{
      GetWindowThreadProcessId(window,out uint pid);
      if(pid==process.Id) { var title=new StringBuilder(256); GetWindowText(window,title,title.Capacity);
        if(title.ToString()=="DesktopAICompanion") { target=window; count++; } }
      return true;
    },IntPtr.Zero);
    if(count!=1) throw new IOException("companion_lifetime_window_not_unique");
    GetWindowThreadProcessId(target,out uint owner);
    if(owner!=process.Id || Created(process)!=expectedCreation) throw new IOException("companion_lifetime_changed");
    // Destruction may beat the acknowledgement. Original retained-process exit
    // is authoritative, even when SendMessageTimeout returns zero.
    SendMessageTimeout(target,0x0010,IntPtr.Zero,IntPtr.Zero,3,5000,out _);
    if(!process.WaitForExit(15000)) throw new IOException("companion_graceful_close_timeout");
  }
}
'@
}
function File-Hash([string]$Path, [long]$Maximum = 64MB) {
    Assert-NoReparse $Path
    $stream = [HostMsiUpgradeNative]::OpenPublic($Path, $Maximum)
    try { return [HostMsiUpgradeNative]::Hash($stream) } finally { $stream.Dispose() }
}
function Get-Companions([string]$Canonical) {
    $handles = [Collections.Generic.List[Diagnostics.Process]]::new()
    $lifetimes = @{}; $foreign = 0
    try {
        foreach ($process in [Diagnostics.Process]::GetProcessesByName('DesktopAICompanion')) {
            $handles.Add($process)
            try {
                $null = $process.SafeHandle
                if ($process.HasExited) { continue }
                $image = $process.MainModule.FileName
                $created = [HostMsiUpgradeNative]::Created($process)
                if ($image -ine $Canonical) { $foreign++ }
                $lifetimes[[string]$process.Id] = [string]$created
            }
            catch { if (-not $process.HasExited) { throw 'companion_identity_unavailable' } }
        }
        return @{ Handles = $handles; Lifetimes = $lifetimes; ForeignCount = $foreign }
    }
    catch { foreach ($process in $handles) { $process.Dispose() }; throw }
}
function Close-CompanionHandles($Set) { if ($Set) { foreach ($process in $Set.Handles) { $process.Dispose() } } }
function Close-ExactCompanions($Set, [scriptblock]$Close = {
    param($Process, $Created) [HostMsiUpgradeNative]::CloseLifetime($Process, $Created)
}) {
    foreach ($process in $Set.Handles) {
        if (-not $process.HasExited) { & $Close $process ([long]$Set.Lifetimes[[string]$process.Id]) }
    }
}
function Start-CanonicalCompanion([string]$Executable) {
    Assert-NoReparse $Executable
    $launch = [Diagnostics.ProcessStartInfo]::new($Executable)
    $launch.UseShellExecute = $false; $launch.CreateNoWindow = $true; $launch.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $launched = [Diagnostics.Process]::Start($launch)
    try { Require ($null -ne $launched) 'companion_relaunch_failed' } finally { if ($launched) { $launched.Dispose() } }
}
function Get-ModuleBinaries([string]$Root) {
    $result = @{}; $queue = [Collections.Generic.Queue[string]]::new(); $queue.Enqueue($Root)
    $entries = 0; $bytes = 0L
    Assert-NoReparse $Root
    if (-not [IO.Directory]::Exists($Root)) { return $result }
    while ($queue.Count) {
        $directory = $queue.Dequeue(); Assert-NoReparse $directory
        foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $entries++; Require ($entries -le 10000) 'module_entry_bound'
            Assert-NoReparse $path
            $attributes = [IO.File]::GetAttributes($path)
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { $queue.Enqueue($path); continue }
            if ([IO.Path]::GetExtension($path) -notin @('.dll', '.exe')) { continue }
            $length = [IO.FileInfo]::new($path).Length; $bytes += $length
            Require ($bytes -le 1GB -and $result.Count -lt 4096) 'module_binary_bound'
            $result[$path.Substring($Root.Length).TrimStart('\')] = File-Hash $path
        }
    }
    return $result
}
function Get-SettingsHashes([string]$DataRoot) {
    $result = @{}
    # AppPaths.SettingsFile and CompanionHost.GetSettings -> ModuleDataDir("agentbroker")
    # use these two literal names. No other module data or identity files are traversed.
    foreach ($relative in @('settings.json', 'modules\agentbroker\settings.json')) {
        $path = Join-Path $DataRoot $relative; Assert-NoReparse $path
        $result[$relative] = if ([IO.File]::Exists($path)) { File-Hash $path 12MB } else { 'ABSENT' }
    }
    return $result
}
function Get-ZipRuntimeHashes([IO.Stream]$Stream) {
    $archive = [IO.Compression.ZipArchive]::new($Stream, [IO.Compression.ZipArchiveMode]::Read, $true)
    try {
        Require ($archive.Entries.Count -gt 0 -and $archive.Entries.Count -le 20000) 'archive_entry_bound'
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) { Assert-ArchiveName $entry.FullName; Require ($names.Add($entry.FullName)) 'duplicate_archive_entry' }
        $hashes = @{}
        foreach ($name in @('DesktopAICompanion.exe', 'DesktopAICompanion.dll', 'DesktopAICompanion.Contracts.dll',
            'NAudio.Core.dll', 'NAudio.Dmo.dll', 'NAudio.Midi.dll', 'NAudio.WinMM.dll', 'System.Numerics.Tensors.dll')) {
            $entry = $archive.GetEntry($name)
            Require ($null -ne $entry -and $entry.Length -gt 0 -and $entry.Length -le 64MB) 'portable_runtime_missing'
            $input = $entry.Open()
            try { $hashes[$name] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($input)) } finally { $input.Dispose() }
        }
        return $hashes
    }
    finally { $archive.Dispose() }
}
function Write-NewReport([string]$Path, $Report) {
    Assert-NoReparse (Split-Path $Path -Parent)
    $bytes = $utf8.GetBytes(($Report | ConvertTo-Json -Compress))
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}
function Run-SelfTest {
    $checks = 0
    $metadata = @{ ProductVersion = '1.6.1'; UpgradeCode = $upgradeCode; ProductName = 'Desktop AI Companion'; ALLUSERS = ''; WordCount = '10'; CLEANINSTALL = '' }
    Assert-MsiMetadata $metadata; $checks++
    foreach ($change in @(@('ALLUSERS','1'), @('ALLUSERS','2'), @('WordCount','2'), @('CLEANINSTALL','1'),
        @('ProductVersion','1.6.0'), @('UpgradeCode','{00000000-0000-0000-0000-000000000000}'))) {
        $copy = $metadata.Clone(); $copy[$change[0]] = $change[1]
        $refused = $false; try { Assert-MsiMetadata $copy } catch { $refused = $true }
        Require $refused 'selftest_msi_policy_failed'; $checks++
    }
    foreach ($name in @('../outside.dll', '/absolute.dll', 'privatekey-data/a.txt', 'a/client.pfx', '.env.local/data.json', 'a/NUL.txt')) {
        $refused = $false; try { Assert-ArchiveName $name } catch { $refused = $true }
        Require $refused 'selftest_archive_policy_failed'; $checks++
    }
    foreach ($name in @('.git/credentials', 'nested/.git/credentials/token.txt', '.mozilla/profile.txt',
        'nested/.config/google-chrome/profile.txt', 'nested/.config/chromium/profile.txt', 'Mozilla/Firefox/profile.txt')) {
        $refused = $false; try { Assert-ArchiveName $name } catch { $refused = $true }
        Require $refused 'selftest_archive_directory_policy_failed'; $checks++
        $refused = $false; try { $null = Assert-PublicPath ('C:\synthetic\' + $name.Replace('/', '\')) } catch { $refused = $true }
        Require $refused 'selftest_directory_path_policy_failed'; $checks++
    }
    $before = @{ Lifetimes = @{ '100' = '1234' }; ForeignCount = 0 }
    Assert-ProcessSet $before @{ Lifetimes = @{ '100' = '1234' }; ForeignCount = 0 }; $checks++
    foreach ($after in @(@{ Lifetimes = @{ '100' = '1234' }; ForeignCount = 1 },
        @{ Lifetimes = @{ '100' = '5678' }; ForeignCount = 0 }, @{ Lifetimes = @{}; ForeignCount = 0 })) {
        $refused = $false; try { Assert-ProcessSet $before $after } catch { $refused = $true }
        Require $refused 'selftest_process_policy_failed'; $checks++
    }
    $closed = [Collections.Generic.List[long]]::new()
    $fake = @{ Handles = @([pscustomobject]@{ Id = 100; HasExited = $false }, [pscustomobject]@{ Id = 101; HasExited = $true }); Lifetimes = @{ '100' = '1234'; '101' = '5678' } }
    Close-ExactCompanions $fake { param($Process, $Created) $closed.Add($Created) }
    Require ($closed.Count -eq 1 -and $closed[0] -eq 1234) 'selftest_graceful_scope_failed'; $checks++
    $refused = $false
    try { Close-ExactCompanions $fake { param($Process, $Created) throw 'companion_graceful_close_timeout' } } catch { $refused = $true }
    Require $refused 'selftest_graceful_timeout_failed'; $checks++
    Assert-Snapshot @{ 'known.dll' = 'a' } @{ 'known.dll' = 'a' } 'synthetic'; $checks++
    foreach ($after in @(@{ 'known.dll' = 'b' }, @{}, @{ 'different.dll' = 'a' })) {
        $refused = $false; try { Assert-Snapshot @{ 'known.dll' = 'a' } $after 'synthetic' } catch { $refused = $true }
        Require $refused 'selftest_preservation_failed'; $checks++
    }
    @{ Result = 'PASS'; Checks = $checks; InstallerStarted = $false; UserDataRead = $false } | ConvertTo-Json -Compress
}

$sourceMsi = $null; $sourceZip = $null; $companions = $null; $recheck = $null; $installer = $null
$report = $null; $evidence = $null
$wasRunning = $false; $originalExeHash = $null; $exe = $null
try {
    Require (-not ($Apply -and $ValidateOnly) -and -not ($SelfTest -and ($Apply -or $ValidateOnly))) 'arguments_refused'
    if ($SelfTest) { Run-SelfTest; exit 0 }
    Require ($env:OS -eq 'Windows_NT' -and [Environment]::Is64BitProcess) 'windows_x64_required'
    Require ([string]::IsNullOrWhiteSpace($env:DESKTOP_AI_COMPANION_DATA_ROOT)) 'data_root_override_refused'
    Initialize-Native
    $local = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    $installRoot = Join-Path $local 'Programs\Desktop AI Companion'
    $exe = Join-Path $installRoot 'DesktopAICompanion.exe'
    $dataRoot = Join-Path $local 'DesktopAICompanion'
    foreach ($path in @($msiPath, $zipPath, $installRoot, $dataRoot)) { Assert-NoReparse $path }
    $sourceMsi = [HostMsiUpgradeNative]::OpenPublic($msiPath, 64MB)
    $sourceZip = [HostMsiUpgradeNative]::OpenPublic($zipPath, 128MB)
    Require ([HostMsiUpgradeNative]::Hash($sourceMsi) -ceq $msiHash -and [HostMsiUpgradeNative]::Hash($sourceZip) -ceq $zipHash) 'public_artifact_hash_mismatch'
    Assert-MsiMetadata ([HostMsiUpgradeNative]::Metadata($msiPath))
    $expectedRuntime = Get-ZipRuntimeHashes $sourceZip
    $registered = @([HostMsiUpgradeNative]::Registered($upgradeCode))
    Require ($registered.Count -eq 1 -and $registered[0]['ProductName'] -ceq 'Desktop AI Companion' -and
        $registered[0]['AssignmentType'] -ceq '0' -and
        [IO.Path]::GetFullPath($registered[0]['InstallLocation']).TrimEnd('\') -ieq $installRoot.TrimEnd('\') -and
        $registered[0]['VersionString'] -cin @('1.6.0', '1.6.1')) 'installed_registration_mismatch'
    $companions = Get-Companions $exe
    $upgradeNeeded = $registered[0]['VersionString'] -ceq '1.6.0'
    $report = @{ Mode = $(if ($Apply) { 'Apply' } else { 'ValidateOnly' }); PackageVerified = $true; PerUser = $true;
        TargetVersion = $targetVersion; UpgradeNeeded = $upgradeNeeded; RuntimeBinaryCount = $expectedRuntime.Count;
        CompanionCount = $companions.Lifetimes.Count; ForeignCompanionCount = $companions.ForeignCount;
        ProcessSnapshotClear = $companions.ForeignCount -eq 0; CallerMustFinishAllHostWork = $true;
        InstallerStarted = $false; Ready = $false }
    if (-not $Apply) { $report | ConvertTo-Json -Compress; exit 0 }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        Require ($identity.User.Value -cne 'S-1-5-18' -and -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) 'standard_user_required'
    } finally { $identity.Dispose() }
    Require $upgradeNeeded 'upgrade_not_needed'
    Require ($companions.ForeignCount -eq 0) 'foreign_companion_process_present'
    $wasRunning = $companions.Lifetimes.Count -gt 0
    $originalExeHash = File-Hash $exe
    $recheck = Get-Companions $exe; Assert-ProcessSet $companions $recheck
    # Program's AppLifetime close returns through StartUp.Dispose, which raises
    # module shutdown and disposes module hosts. Snapshot only after that exit so
    # legitimate shutdown persistence cannot be mistaken for installer damage.
    Close-ExactCompanions $companions
    Close-CompanionHandles $recheck; $recheck = Get-Companions $exe
    Require ($recheck.Lifetimes.Count -eq 0) 'companion_restarted_before_install'
    $beforeModules = Get-ModuleBinaries (Join-Path $installRoot 'modules')
    $beforeSettings = Get-SettingsHashes $dataRoot
    $evidence = Join-Path (Join-Path $repo 'artifacts') ('host-msi-upgrade-' + [guid]::NewGuid().ToString('N'))
    Assert-NoReparse $evidence
    New-Item -ItemType Directory -Path $evidence -ErrorAction Stop | Out-Null
    $info = [Diagnostics.ProcessStartInfo]::new((Join-Path ([Environment]::SystemDirectory) 'msiexec.exe'))
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in @('/i', $msiPath, '/qn', '/norestart', '/Lwe', (Join-Path $evidence 'installer.log'))) { $info.ArgumentList.Add($argument) }
    # Re-enumerate immediately before invoking the basename-wide WiX close action.
    Close-CompanionHandles $recheck; $recheck = Get-Companions $exe
    Require ($recheck.Lifetimes.Count -eq 0) 'companion_restarted_before_install'
    $installer = [Diagnostics.Process]::Start($info); $null = $installer.SafeHandle
    $report.InstallerStarted = $true; $report.InstallerProcessId = $installer.Id
    $report.InstallerCreated = [HostMsiUpgradeNative]::Created($installer)
    Write-NewReport (Join-Path $evidence 'started.json') $report
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    while (-not $installer.WaitForExit(1000) -and $elapsed.Elapsed.TotalSeconds -lt $TimeoutSeconds) {}
    $report.ElapsedSeconds = [Math]::Round($elapsed.Elapsed.TotalSeconds, 1)
    if (-not $installer.HasExited) {
        $report.TimedOut = $true; $report.InstallerStillRunning = $true
        Write-NewReport (Join-Path $evidence 'result.json') $report
        $report | ConvertTo-Json -Compress
        exit 1
    }
    $report.InstallerExitCode = $installer.ExitCode
    Require ($installer.ExitCode -eq 0) 'installer_nonzero_exit'
    $afterRuntime = @{}
    foreach ($name in $expectedRuntime.Keys) { $afterRuntime[$name] = File-Hash (Join-Path $installRoot $name) }
    Assert-Snapshot $expectedRuntime $afterRuntime 'installed_runtime_hash_mismatch'
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $installRoot 'DesktopAICompanion.dll')).FileVersion
    Require ([version]$version -eq [version]'1.6.1.0') 'installed_host_version_mismatch'
    $afterRegistration = @([HostMsiUpgradeNative]::Registered($upgradeCode))
    Require ($afterRegistration.Count -eq 1 -and $afterRegistration[0]['VersionString'] -ceq '1.6.1' -and
        $afterRegistration[0]['AssignmentType'] -ceq '0') 'installed_registration_mismatch'
    Assert-Snapshot $beforeModules (Get-ModuleBinaries (Join-Path $installRoot 'modules')) 'module_binaries_changed'
    Assert-Snapshot $beforeSettings (Get-SettingsHashes $dataRoot) 'settings_changed'
    $report.RuntimeHashesMatch = $true; $report.ModuleBinaryHashesPreserved = $true; $report.SettingsPreservedBeforeRelaunch = $true
    $report.ModuleBinaryCount = $beforeModules.Count; $report.Ready = $true
    if ($wasRunning) {
        Start-CanonicalCompanion $exe
        $report.CompanionRelaunched = $true
    }
    Write-NewReport (Join-Path $evidence 'result.json') $report
    $report | ConvertTo-Json -Compress
}
catch {
    $code = $_.Exception.Message
    if ($code -cnotmatch '^[a-z][a-z0-9_]{2,80}$') { $code = 'operation_unavailable' }
    $failure = @{ Ready = $false; Code = $code; InstallerStarted = $null -ne $installer }
    if ($wasRunning -and -not $installer) {
        # Before MSI launch, restore the exact original public image if a later
        # validation failed. Never relaunch while an installer may still run.
        $restoreCheck = $null
        try {
            $restoreCheck = Get-Companions $exe
            if ($restoreCheck.Lifetimes.Count -eq 0 -and (File-Hash $exe) -ceq $originalExeHash) {
                Start-CanonicalCompanion $exe; $failure.OriginalCompanionRestored = $true
            }
        } catch { $failure.OriginalCompanionRestored = $false }
        finally { Close-CompanionHandles $restoreCheck }
    }
    if ($installer) {
        try {
            $failure.InstallerStillRunning = -not $installer.HasExited
            $failure.InstallerProcessId = $installer.Id
            $failure.InstallerCreated = [HostMsiUpgradeNative]::Created($installer)
            if ($installer.HasExited) { $failure.InstallerExitCode = $installer.ExitCode }
        } catch {}
    }
    if ($evidence) { try { Write-NewReport (Join-Path $evidence 'result.json') $failure } catch {} }
    $failure | ConvertTo-Json -Compress
    exit 1
}
finally {
    # Disposing a retained process handle never stops that process, including a
    # timed-out msiexec. There is deliberately no Kill/Stop-Process path.
    if ($installer) { $installer.Dispose() }
    Close-CompanionHandles $recheck; Close-CompanionHandles $companions
    if ($sourceZip) { $sourceZip.Dispose() }; if ($sourceMsi) { $sourceMsi.Dispose() }
}
