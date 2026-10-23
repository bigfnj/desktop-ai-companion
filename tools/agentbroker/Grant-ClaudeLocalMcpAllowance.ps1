#Requires -Version 7.5
<#
Dry-run by default. Appends only {"serverUrl":"http://127.0.0.1:7432/mcp"}
to the current user's canonical ~/.claude/settings.json allowedMcpServers.
This is the documented user extension of an unlocked (soft) MCP allowlist:
https://code.claude.com/docs/en/managed-mcp#policy-based-control-with-allowlists-and-denylists
Pinned CLI 2.1.292 confirms W1e -> unlocked dt()/Jg()/C_r, whose s4
merger unions arrays. f6t/YKe switches to managed-only entries if any managed
lock is true. jz calls Qse/G1e (merged denials) before the allowlist check.

  pwsh -NoProfile -File tools/agentbroker/Grant-ClaudeLocalMcpAllowance.ps1 -SelfTest
  pwsh -NoProfile -File tools/agentbroker/Grant-ClaudeLocalMcpAllowance.ps1
  pwsh -NoProfile -File tools/agentbroker/Grant-ClaudeLocalMcpAllowance.ps1 -Apply

Review and authorize the exact live -Apply operation separately. This script
never changes a managed file/cache, registry, denylist, lock or server definition.
It does not register a server or claim it is authorized effectively. Claude's
normal CLI remains the final policy authority. No retries or policy bypasses.
Unrelated JSON values are preserved semantically; formatting may change.
An existing file gets a uniquely named backup readable only by this user/SYSTEM.
The snapshot's file ID and digest are rechecked just before an atomic replace.
Independent editors can still race the final recheck/rename, so this is not an
OS-wide compare-and-swap. No automatic rollback overwrites a subsequent edit.
#>
[CmdletBinding()]
param([switch] $Apply, [switch] $SelfTest)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Apply -and $SelfTest) { throw 'Choose either the synthetic self-test or the live operation.' }

# DefinitionsOnly imports the reviewed read-only helpers without reading any file.
& (Join-Path $PSScriptRoot 'Inspect-ClaudeMcpPolicy.ps1') -DefinitionsOnly

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

public sealed class ClaudeUserGrantPlan {
    internal string Path, Stamp;
    internal byte[] Original, Updated;
    internal bool Exists;
    public bool ChangeNeeded { get; internal set; }
    public int ExistingAllowedRules { get; internal set; }
    public bool AllOtherValuesPreserved { get; internal set; }
}
public sealed class ClaudeUserGrantResult {
    public bool Applied { get; set; }
    public bool Verified { get; set; }
    public bool PrivateBackupCreated { get; set; }
    public string BackupFileName { get; set; }
}
public static class ClaudeUserGrant {
    public const string Url="http://127.0.0.1:7432/mcp";
    const string Pin="EB95BB65955F8B1702E800815F9A2C0388A5DE0F196C354C7EE1DD5CB9A2BA23";
    [StructLayout(LayoutKind.Sequential)] struct Info {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes {
        public int Length; public IntPtr Descriptor; public int Inherit;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,
        uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CreateFileW")]
    static extern SafeFileHandle CreatePrivate(string path,uint access,uint share,ref SecurityAttributes security,
        uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle handle,out Info info);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle,StringBuilder path,uint length,uint flags);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl,uint version,out IntPtr descriptor,out uint size);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    static readonly JsonDocumentOptions Options=new JsonDocumentOptions {
        AllowTrailingCommas=true,CommentHandling=JsonCommentHandling.Skip,MaxDepth=64 };

    static void OrdinaryPath(string path) {
        string full=System.IO.Path.GetFullPath(path);
        if (full.Length<3 || full[1]!=':' || full[2]!='\\' || full.Substring(2).Contains(':'))
            throw new IOException("unsafe_path");
        for (string p=full;p!=null;p=System.IO.Path.GetDirectoryName(p)) {
            try { if ((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0) throw new IOException("linked_path"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
            if (p==System.IO.Path.GetPathRoot(p)) break;
        }
    }
    static void VerifyHandle(SafeFileHandle handle,string path,ulong maximum,out Info info) {
        if (handle.IsInvalid || !GetFileInformationByHandle(handle,out info) ||
            (info.Attributes&(0x10u|0x400u|0x40u))!=0 || info.Links!=1 ||
            (((ulong)info.SizeHigh<<32)|info.SizeLow)>maximum) throw new IOException("nonordinary_file");
        var actual=new StringBuilder(32768);
        uint count=GetFinalPathNameByHandleW(handle,actual,(uint)actual.Capacity,0);
        if (count==0 || count>=actual.Capacity || !actual.ToString().Equals(@"\\?\"+System.IO.Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase))
            throw new IOException("resolved_path_mismatch");
    }
    static (bool Exists,byte[] Bytes,string Stamp) Snapshot(string path) {
        OrdinaryPath(path);
        using (var handle=CreateFileW(path,0x80000000,1,IntPtr.Zero,3,0x00200000,IntPtr.Zero)) {
            if (handle.IsInvalid) {
                int code=Marshal.GetLastWin32Error();
                if (code==2 || code==3) return (false,Array.Empty<byte>(),"absent");
                throw new IOException("open_refused");
            }
            VerifyHandle(handle,path,2097152,out var info);
            using (var input=new FileStream(handle,FileAccess.Read)) {
                byte[] bytes=new byte[(int)info.SizeLow]; input.ReadExactly(bytes);
                return (true,bytes,$"{info.Volume}:{info.IndexHigh}:{info.IndexLow}:{Convert.ToHexString(SHA256.HashData(bytes))}");
            }
        }
    }
    static string Decode(byte[] bytes) {
        int skip=bytes.Length>=3 && bytes[0]==239 && bytes[1]==187 && bytes[2]==191?3:0;
        return new UTF8Encoding(false,true).GetString(bytes,skip,bytes.Length-skip);
    }
    static bool Duplicate(JsonElement value) {
        if (value.ValueKind==JsonValueKind.Object) {
            var keys=new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject()) if (!keys.Add(p.Name) || Duplicate(p.Value)) return true;
        } else if (value.ValueKind==JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (Duplicate(item)) return true;
        return false;
    }
    static bool UnsupportedOrLocked(JsonElement root) {
        if (root.TryGetProperty("allowManagedMcpServersOnly",out var v) && v.ValueKind!=JsonValueKind.False) return true;
        if (root.TryGetProperty("strictPluginOnlyCustomization",out v)) {
            if (v.ValueKind==JsonValueKind.True) return true;
            if (v.ValueKind!=JsonValueKind.False && v.ValueKind!=JsonValueKind.Array) return true;
            if (v.ValueKind==JsonValueKind.Array && v.EnumerateArray().Any(x=>x.ValueKind!=JsonValueKind.String ||
                !new[]{"skills","agents","hooks"}.Contains(x.GetString(),StringComparer.Ordinal))) return true;
        }
        return false;
    }
    static ClaudeUserGrantPlan PrepareAt(string path) {
        var snapshot=Snapshot(path);
        using (var document=JsonDocument.Parse(snapshot.Exists?Decode(snapshot.Bytes):"{}",Options)) {
            var root=document.RootElement;
            if (root.ValueKind!=JsonValueKind.Object || Duplicate(root) || UnsupportedOrLocked(root))
                throw new IOException("unsupported_settings");
            bool has=root.TryGetProperty("allowedMcpServers",out var rules);
            if (has && rules.ValueKind!=JsonValueKind.Array) throw new IOException("unsupported_allowlist");
            if (has && rules.GetArrayLength()>4096) throw new IOException("oversize_allowlist");
            bool present=has && rules.EnumerateArray().Any(x=>x.ValueKind==JsonValueKind.Object &&
                x.EnumerateObject().Count()==1 && x.TryGetProperty("serverUrl",out var u) &&
                u.ValueKind==JsonValueKind.String && u.GetString()==Url);
            byte[] updated=snapshot.Bytes;
            if (!present) {
                using (var output=new MemoryStream()) {
                    using (var writer=new Utf8JsonWriter(output,new JsonWriterOptions { Indented=true })) {
                        writer.WriteStartObject();
                        foreach (var property in root.EnumerateObject()) {
                            if (property.Name!="allowedMcpServers") { property.WriteTo(writer); continue; }
                            writer.WritePropertyName(property.Name); writer.WriteStartArray();
                            foreach (var rule in rules.EnumerateArray()) rule.WriteTo(writer);
                            writer.WriteStartObject(); writer.WriteString("serverUrl",Url); writer.WriteEndObject();
                            writer.WriteEndArray();
                        }
                        if (!has) {
                            writer.WritePropertyName("allowedMcpServers"); writer.WriteStartArray();
                            writer.WriteStartObject(); writer.WriteString("serverUrl",Url); writer.WriteEndObject(); writer.WriteEndArray();
                        }
                        writer.WriteEndObject();
                    }
                    updated=output.ToArray();
                }
                if (updated.Length>2097152 || !Preserved(snapshot.Exists?Decode(snapshot.Bytes):"{}",Decode(updated)))
                    throw new IOException("preservation_check_failed");
            }
            return new ClaudeUserGrantPlan { Path=path, Stamp=snapshot.Stamp, Original=snapshot.Bytes, Updated=updated,
                Exists=snapshot.Exists, ChangeNeeded=!present, ExistingAllowedRules=has?rules.GetArrayLength():0, AllOtherValuesPreserved=true };
        }
    }
    public static bool Preserved(string before,string after) {
        using (var old=JsonDocument.Parse(before,Options)) using (var next=JsonDocument.Parse(after,Options)) {
            var a=old.RootElement; var b=next.RootElement;
            var previous=a.EnumerateObject().Where(x=>x.Name!="allowedMcpServers").ToArray();
            var current=b.EnumerateObject().Where(x=>x.Name!="allowedMcpServers").ToArray();
            if (previous.Length!=current.Length) return false;
            for (int i=0;i<previous.Length;i++) if (previous[i].Name!=current[i].Name ||
                !JsonElement.DeepEquals(previous[i].Value,current[i].Value)) return false;
            var oldRules=a.TryGetProperty("allowedMcpServers",out var v)?v.EnumerateArray().ToArray():Array.Empty<JsonElement>();
            var newRules=b.GetProperty("allowedMcpServers").EnumerateArray().ToArray();
            if (newRules.Length!=oldRules.Length+1) return false;
            for (int i=0;i<oldRules.Length;i++) if (!JsonElement.DeepEquals(oldRules[i],newRules[i])) return false;
            return newRules[^1].EnumerateObject().Count()==1 && newRules[^1].GetProperty("serverUrl").GetString()==Url;
        }
    }
    public static ClaudeUserGrantPlan PrepareCanonical() {
        return PrepareAt(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".claude","settings.json"));
    }
    public static bool VerifyPinnedCli() {
        string path=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".local","bin","claude.exe");
        OrdinaryPath(path);
        using (var handle=CreateFileW(path,0x80000000,1,IntPtr.Zero,3,0x00200000,IntPtr.Zero)) {
            VerifyHandle(handle,path,536870912,out var _);
            using (var input=new FileStream(handle,FileAccess.Read)) return Convert.ToHexString(SHA256.HashData(input))==Pin;
        }
    }
    static void WritePrivate(string path,byte[] bytes) {
        if (bytes.Length>2097152) throw new IOException("oversize_write");
        OrdinaryPath(path);
        string sid=WindowsIdentity.GetCurrent().User.Value;
        IntPtr descriptor=IntPtr.Zero;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P(A;;FA;;;"+sid+")(A;;FA;;;SY)",1,out descriptor,out var _))
            throw new IOException("private_acl_failed");
        bool created=false;
        try {
            var attributes=new SecurityAttributes { Length=Marshal.SizeOf<SecurityAttributes>(),Descriptor=descriptor };
            using (var handle=CreatePrivate(path,0xC0000000,0,ref attributes,1,0x00200000,IntPtr.Zero)) {
                created=!handle.IsInvalid;
                VerifyHandle(handle,path,2097152,out var _);
                using (var output=new FileStream(handle,FileAccess.Write)) { output.Write(bytes); output.Flush(true); }
            }
        } catch {
            if (created) File.Delete(path);
            throw;
        } finally { LocalFree(descriptor); }
    }
    public static ClaudeUserGrantResult Publish(ClaudeUserGrantPlan plan) {
        var result=new ClaudeUserGrantResult();
        if (!plan.ChangeNeeded) {
            var unchanged=Snapshot(plan.Path);
            if (unchanged.Exists!=plan.Exists || unchanged.Stamp!=plan.Stamp) throw new IOException("concurrent_modification");
            result.Verified=true; return result;
        }
        string directory=System.IO.Path.GetDirectoryName(plan.Path);
        if (!Directory.Exists(directory)) throw new IOException("canonical_directory_absent");
        string id=Guid.NewGuid().ToString("N"), stage=System.IO.Path.Combine(directory,"settings.agentbroker-stage-"+id+".json"),
            backup=System.IO.Path.Combine(directory,"settings.agentbroker-backup-"+id+".json");
        bool stageCreated=false, backupCreated=false;
        try {
            WritePrivate(stage,plan.Updated); stageCreated=true;
            if (plan.Exists) { WritePrivate(backup,plan.Original); backupCreated=true; }
            var current=Snapshot(plan.Path);
            if (current.Exists!=plan.Exists || current.Stamp!=plan.Stamp) throw new IOException("concurrent_modification");
            OrdinaryPath(plan.Path);
            if (plan.Exists) File.Replace(stage,plan.Path,null);
            else File.Move(stage,plan.Path,false);
            stageCreated=false; result.Applied=true; result.PrivateBackupCreated=backupCreated;
            result.BackupFileName=backupCreated?System.IO.Path.GetFileName(backup):null;
            try { result.Verified=Snapshot(plan.Path).Bytes.AsSpan().SequenceEqual(plan.Updated); } catch { result.Verified=false; }
            return result;
        } finally {
            if (stageCreated) File.Delete(stage);
            if (backupCreated && !result.Applied) File.Delete(backup);
        }
    }
    public static int SelfTest(string root) {
        string path=System.IO.Path.Combine(root,"synthetic-settings.json");
        int checks=0;
        Action<bool> check=value=>{if(!value)throw new IOException("synthetic_check_"+checks);checks++;};
        string original="{\"allowedMcpServers\":[{\"serverUrl\":\"https://synthetic.invalid/*\"},{\"serverCommand\":[\"synthetic\",\"--argument\"]}],\"deniedMcpServers\":[{\"serverName\":\"synthetic-denied\"}],\"unrelated\":{\"numeric\":123456789012345678901234567890,\"unicode\":\"雪\",\"nested\":[null,false,{\"secret\":\"synthetic-only\"}]},\"allowManagedMcpServersOnly\":false}";
        File.WriteAllText(path,original,new UTF8Encoding(false));
        var plan=PrepareAt(path);
        check(plan.ChangeNeeded && plan.ExistingAllowedRules==2 && plan.AllOtherValuesPreserved);
        check(Preserved(original,Decode(plan.Updated)));
        check(File.ReadAllText(path)==original); // Prepare is a real dry run.
        File.WriteAllText(path,original+" ",new UTF8Encoding(false));
        bool refused=false;try{Publish(plan);}catch(IOException){refused=true;}
        check(refused); check(File.ReadAllText(path)==original+" ");
        check(Directory.GetFiles(root).Length==1); // Refused writes clean up only their own stage/backup.
        plan=PrepareAt(path); var result=Publish(plan);
        check(result.Applied && result.Verified && result.PrivateBackupCreated);
        check(File.ReadAllText(System.IO.Path.Combine(root,result.BackupFileName))==original+" ");
        check(!PrepareAt(path).ChangeNeeded);
        check(!Publish(PrepareAt(path)).Applied);
        check(Preserved(original,File.ReadAllText(path)));
        var noChange=PrepareAt(path);
        File.WriteAllText(path,original);
        refused=false;try{Publish(noChange);}catch(IOException){refused=true;}check(refused);
        File.Delete(System.IO.Path.Combine(root,result.BackupFileName)); File.Delete(path);
        plan=PrepareAt(path);
        File.WriteAllText(path,"{\"unrelated\":\"concurrent-creation\"}");
        refused=false;try{Publish(plan);}catch(IOException){refused=true;}check(refused);
        check(File.ReadAllText(path)=="{\"unrelated\":\"concurrent-creation\"}"); File.Delete(path);
        plan=PrepareAt(path); result=Publish(plan);
        check(result.Applied && result.Verified && !result.PrivateBackupCreated); File.Delete(path);
        File.WriteAllText(path,"{\"allowedMcpServers\":[],\"allowManagedMcpServersOnly\":true}");
        refused=false;try{PrepareAt(path);}catch(IOException){refused=true;}check(refused);
        File.WriteAllText(path,"{\"strictPluginOnlyCustomization\":[\"mcp\"]}");
        refused=false;try{PrepareAt(path);}catch(IOException){refused=true;}check(refused);
        File.WriteAllText(path,"{\"allowManagedMcpServersOnly\":\"false\"}");
        refused=false;try{PrepareAt(path);}catch(IOException){refused=true;}check(refused);
        File.WriteAllText(path,"{\"allowedMcpServers\":[],\"allowedMcpServers\":[]}");
        refused=false;try{PrepareAt(path);}catch(IOException){refused=true;}check(refused);
        File.WriteAllText(path,"{\"allowedMcpServers\":null}");
        refused=false;try{PrepareAt(path);}catch(IOException){refused=true;}check(refused);
        File.Delete(path);
        return checks;
    }
}
'@

if ($SelfTest) {
    $fixtureRoot = [IO.Path]::Combine($PSScriptRoot, 'user-grant-selftest-' + [Guid]::NewGuid().ToString('N'))
    [void] [IO.Directory]::CreateDirectory($fixtureRoot)
    try {
        $count = [ClaudeUserGrant]::SelfTest($fixtureRoot)
        [pscustomobject]@{ SelfTestPassed = $true; Checks = $count; LiveSettingsReadOrChanged = $false } | ConvertTo-Json -Compress
    } finally {
        # Empty-only removal, never recurse over a computed or foreign directory.
        [IO.Directory]::Delete($fixtureRoot, $false)
    }
    return
}

if (-not $IsWindows -or -not [Environment]::Is64BitProcess) { throw '64-bit Windows PowerShell 7 is required.' }
$profileRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
$managedRoot = 'C:\Program Files\ClaudeCode'
$admin = @(
    [ClaudePolicyInspector]::ReadFile('RemoteCacheUnverified', [IO.Path]::Combine($profileRoot, '.claude', 'remote-settings.json'), 8388608)
    [ClaudePolicyInspector]::ReadRegistry($true)
    [ClaudePolicyInspector]::ReadFile('ManagedFile', [IO.Path]::Combine($managedRoot, 'managed-settings.json'), 2097152)
    [ClaudePolicyInspector]::ReadDropIns([IO.Path]::Combine($managedRoot, 'managed-settings.d'))
    [ClaudePolicyInspector]::ReadRegistry($false)
)
# HKCU is inspected conservatively even if it is currently an inactive fallback.
if (@($admin | Where-Object {
    $_.AllowManagedMcpServersOnly -or $_.StrictPluginOnlyMcp -or $_.PolicyHelperPresent -or
    $_.UnsupportedControlValues -or $_.Denied.ExactAgentBrokerNameMatches -or $_.Denied.Candidate7432MatchesLiteralLocalUrl -or
    ($_.Present -and $_.ReadStatus -notin @('read', 'empty')) -or
    -not $_.Allowed.Valid -or -not $_.Denied.Valid
}).Count -gt 0) { throw 'A selected policy source is locked, unsupported or unreadable. No settings changed.' }
try {
    [void] [IO.File]::GetAttributes([IO.Path]::Combine($managedRoot, 'managed-mcp.json'))
    throw 'Exclusive managed MCP configuration is present. No settings changed.'
} catch [IO.FileNotFoundException] { } catch [IO.DirectoryNotFoundException] { }

$userSummary = [ClaudePolicyInspector]::ReadFile('UserSettings', [IO.Path]::Combine($profileRoot, '.claude', 'settings.json'), 2097152)
if (($userSummary.Present -and -not $userSummary.Parsed) -or -not $userSummary.Allowed.Valid -or -not $userSummary.Denied.Valid -or
    $userSummary.UnsupportedControlValues -or $userSummary.Denied.ExactAgentBrokerNameMatches -or $userSummary.Denied.Candidate7432MatchesLiteralLocalUrl) {
    throw 'Canonical user settings are unsupported or unreadable. No settings changed.'
}
try {
    $plan = [ClaudeUserGrant]::PrepareCanonical()
    if ($Apply) {
        if (-not [ClaudeUserGrant]::VerifyPinnedCli()) { throw 'cli_pin_mismatch' }
        $result = [ClaudeUserGrant]::Publish($plan)
        [pscustomobject]@{
            Applied = $result.Applied
            Verified = $result.Verified
            PrivateBackupCreated = $result.PrivateBackupCreated
            BackupFileName = $result.BackupFileName
            AddedExactUrl = [ClaudeUserGrant]::Url
            ManagedPolicyChanged = $false
            DenylistChanged = $false
            EffectiveAuthorizationDetermined = $false
        } | ConvertTo-Json -Compress
    } else {
        [pscustomobject]@{
            DryRun = $true
            ChangeNeeded = $plan.ChangeNeeded
            ExistingAllowedRules = $plan.ExistingAllowedRules
            AllOtherValuesPreserved = $plan.AllOtherValuesPreserved
            ExactUrlToAppend = [ClaudeUserGrant]::Url
            SettingsChanged = $false
            EffectiveAuthorizationDetermined = $false
        } | ConvertTo-Json -Compress
    }
} catch { throw 'The exact user allowance operation was refused. No automatic retry or rollback was attempted.' }
