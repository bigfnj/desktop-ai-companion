#Requires -Version 7.5
<#
Read-only, redacted policy inventory for the pinned Windows Claude Code CLI.
  pwsh -NoProfile -File tools/agentbroker/Inspect-ClaudeMcpPolicy.ps1 -SelfTest
  pwsh -NoProfile -File tools/agentbroker/Inspect-ClaudeMcpPolicy.ps1

The live mode reads only the canonical policy JSON locations and the Settings
value at the two canonical policy registry keys. It never runs Claude, a policy
helper, or environment expansion. No configuration is changed. Review this
script before live use. Its output is an inventory, not an authorization verdict:
remote-cache eligibility/freshness, inherited host policy, config-home overrides,
helper output, and user/project/local settings are deliberately not inspected.

Sources checked 2026-10-21:
https://code.claude.com/docs/en/managed-mcp
https://code.claude.com/docs/en/managed-settings
Public CLI 2.1.292 (EB95BB65955F8B1702E800815F9A2C0388A5DE0F196C354C7EE1DD5CB9A2BA23).
The CLI queries the native 64-bit registry view with expansion disabled. A
remote cache is a top-level settings document. Its public reader caps it at
8 MiB; registry/base/drop-in diagnostics here use a stricter 2 MiB bound.
#>
[CmdletBinding()]
param([switch] $SelfTest, [switch] $UserSettingsOnly, [switch] $DefinitionsOnly)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

public sealed class ClaudePolicyRuleSummary {
    public bool Present { get; set; }
    public bool Valid { get; set; } = true;
    public int Count { get; set; }
    public int Names { get; set; }
    public int Commands { get; set; }
    public int Urls { get; set; }
    public int Invalid { get; set; }
    public int UnexpandedUrlRules { get; set; }
    public int SuppressedOtherUrlRules { get; set; }
    public int SuppressedOpaqueLocalPathRules { get; set; }
    public bool ExactAgentBrokerNameMatches { get; set; }
    public bool Candidate7432MatchesLiteralLocalUrl { get; set; }
    public string[] LiteralLocalUrlPatterns { get; set; } = Array.Empty<string>();
}

public sealed class ClaudePolicySummary {
    public string Source { get; set; }
    public string ReadStatus { get; set; }
    public bool Present { get; set; }
    public bool Parsed { get; set; }
    public bool PolicyDocumentPresent { get; set; }
    public bool AllowManagedMcpServersOnly { get; set; }
    public bool StrictPluginOnlyMcp { get; set; }
    public bool PolicyHelperPresent { get; set; }
    public bool ManagedSourcesMerge { get; set; }
    public bool ManagedSourcesBehaviorPresent { get; set; }
    public bool UnsupportedControlValues { get; set; }
    public bool ManagedMcpServersPresent { get; set; }
    public int ManagedMcpServerCount { get; set; }
    public ClaudePolicyRuleSummary Allowed { get; set; } = new ClaudePolicyRuleSummary();
    public ClaudePolicyRuleSummary Denied { get; set; } = new ClaudePolicyRuleSummary();
}

public static class ClaudePolicyInspector {
    [StructLayout(LayoutKind.Sequential)] struct FileInformation {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path,
        uint length, uint flags);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode)]
    static extern int RegQueryValueExW(SafeRegistryHandle key, string name, IntPtr reserved,
        out uint kind, byte[] data, ref uint length);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern bool CreateHardLinkW(string link,string existing,IntPtr reserved);

    static readonly string Candidate = "http://127.0.0.1:7432/mcp";
    static readonly Regex LocalRule = new Regex(
        @"^(?<scheme>https?)://(?<host>127\.0\.0\.1|localhost)\.?(?::(?<port>\*|[0-9]{1,5}))?(?<path>/[A-Za-z0-9_./~%*+,:;=@!$&()\-]*)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    static ClaudePolicySummary Empty(string source, string status, bool present) {
        return new ClaudePolicySummary { Source=source, ReadStatus=status, Present=present };
    }
    public static bool IsProtectedName(string path) {
        string name=Path.GetFileName(path).TrimEnd(' ', '.');
        return name.Equals(".npmrc", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("privatekey", StringComparison.OrdinalIgnoreCase) ||
            new[]{".env", ".pem", ".key", ".p12", ".pfx", ".keystore"}
                .Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    }
    static void VerifyOrdinaryAncestors(string path) {
        string full=Path.GetFullPath(path);
        if (!Regex.IsMatch(full, @"^[A-Za-z]:\\") || full.Substring(2).Contains(':'))
            throw new IOException("unsafe_path");
        for (string cursor=full; cursor!=null; cursor=Path.GetDirectoryName(cursor)) {
            if (IsProtectedName(cursor)) throw new IOException("protected_name");
            try {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint)!=0)
                    throw new IOException("linked_path");
            } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
            if (cursor==Path.GetPathRoot(cursor)) break;
        }
    }
    public static string ReadOrdinaryFile(string path, int maximum) {
        VerifyOrdinaryAncestors(path);
        string full=Path.GetFullPath(path);
        using (var handle=CreateFileW(full, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero)) {
            if (handle.IsInvalid) {
                int code=Marshal.GetLastWin32Error();
                if (code==2 || code==3) throw new FileNotFoundException();
                throw new IOException("open_refused");
            }
            if (!GetFileInformationByHandle(handle, out var info) ||
                (info.Attributes & (0x10u|0x400u|0x40u))!=0 || info.Links!=1)
                throw new IOException("nonordinary_file");
            var actual=new StringBuilder(32768);
            uint n=GetFinalPathNameByHandleW(handle,actual,(uint)actual.Capacity,0);
            if (n==0 || n>=actual.Capacity ||
                !actual.ToString().Equals(@"\\?\"+full,StringComparison.OrdinalIgnoreCase))
                throw new IOException("resolved_path_mismatch");
            ulong length=((ulong)info.SizeHigh<<32)|info.SizeLow;
            if (length>(ulong)maximum) throw new IOException("oversize_file");
            using (var stream=new FileStream(handle,FileAccess.Read)) {
                byte[] bytes=new byte[(int)length];
                stream.ReadExactly(bytes);
                int skip=bytes.Length>=3 && bytes[0]==239 && bytes[1]==187 && bytes[2]==191 ? 3 : 0;
                return new UTF8Encoding(false,true).GetString(bytes,skip,bytes.Length-skip);
            }
        }
    }
    public static ClaudePolicySummary ReadFile(string source,string path,int maximum) {
        try { return Summarize(source,ReadOrdinaryFile(path,maximum)); }
        catch (FileNotFoundException) { return Empty(source,"absent",false); }
        catch { return Empty(source,"refused_or_unreadable",true); }
    }
    public static ClaudePolicySummary ReadRegistry(bool machine) {
        string source=machine?"Hklm64":"Hkcu64Fallback";
        try {
            using (var hive=RegistryKey.OpenBaseKey(machine?RegistryHive.LocalMachine:RegistryHive.CurrentUser,
                       RegistryView.Registry64))
            using (var key=hive.OpenSubKey(@"SOFTWARE\Policies\ClaudeCode",false)) {
                if (key==null) return Empty(source,"absent",false);
                uint length=0,kind;
                int code=RegQueryValueExW(key.Handle,"Settings",IntPtr.Zero,out kind,null,ref length);
                if (code==2) return Empty(source,"absent",false);
                if (code!=0 || (kind!=1 && kind!=2) || length>2097152 || (length&1)!=0)
                    return Empty(source,"refused_or_unreadable",true);
                byte[] bytes=new byte[length];
                code=RegQueryValueExW(key.Handle,"Settings",IntPtr.Zero,out kind,bytes,ref length);
                if (code!=0 || (kind!=1 && kind!=2) || length>bytes.Length || (length&1)!=0)
                    return Empty(source,"refused_or_unreadable",true);
                string value=new UnicodeEncoding(false,false,true).GetString(bytes,0,(int)length).TrimEnd('\0');
                return Summarize(source,value);
            }
        } catch { return Empty(source,"refused_or_unreadable",true); }
    }
    public static ClaudePolicySummary[] ReadDropIns(string directory) {
        try {
            VerifyOrdinaryAncestors(directory);
            if (!Directory.Exists(directory)) return new[]{Empty("ManagedDropIns","absent",false)};
            var files=new DirectoryInfo(directory).EnumerateFiles("*",SearchOption.TopDirectoryOnly)
                .Take(257).ToArray();
            if (files.Length>256) return new[]{Empty("ManagedDropIns","entry_bound_exceeded",true)};
            var eligible=files.Where(x=>!IsProtectedName(x.Name) &&
                x.Name.EndsWith(".json",StringComparison.OrdinalIgnoreCase) &&
                (x.Attributes & FileAttributes.Hidden)==0).OrderBy(x=>x.Name,StringComparer.Ordinal).ToArray();
            if (eligible.Length>32) return new[]{Empty("ManagedDropIns","entry_bound_exceeded",true)};
            if (eligible.Length==0) return new[]{Empty("ManagedDropIns","empty",true)};
            var result=new List<ClaudePolicySummary>();
            for (int i=0;i<eligible.Length;i++)
                result.Add(ReadFile("ManagedDropIn"+(i+1),eligible[i].FullName,2097152));
            return result.ToArray();
        } catch { return new[]{Empty("ManagedDropIns","refused_or_unreadable",true)}; }
    }
    static bool Has(JsonElement root,string key,out JsonElement value) {
        return root.TryGetProperty(key,out value) && value.ValueKind!=JsonValueKind.Null;
    }
    static bool DuplicateKey(JsonElement element) {
        if (element.ValueKind==JsonValueKind.Object) {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in element.EnumerateObject())
                if (!names.Add(p.Name) || DuplicateKey(p.Value)) return true;
        } else if (element.ValueKind==JsonValueKind.Array)
            foreach (var v in element.EnumerateArray()) if (DuplicateKey(v)) return true;
        return false;
    }
    public static ClaudePolicySummary Summarize(string source,string json) {
        var result=Empty(source,"invalid_json",true);
        try {
            using (var doc=JsonDocument.Parse(json,new JsonDocumentOptions {
                AllowTrailingCommas=true,CommentHandling=JsonCommentHandling.Skip,MaxDepth=64 })) {
                var root=doc.RootElement;
                if (root.ValueKind!=JsonValueKind.Object || DuplicateKey(root)) return result;
                result.Parsed=true; result.ReadStatus="read";
                result.PolicyDocumentPresent=root.EnumerateObject().Any(p=>
                    p.Name!="managedSourcesBehavior" && p.Name!="wslInheritsWindowsSettings" &&
                    !p.Name.StartsWith("$",StringComparison.Ordinal) && p.Value.ValueKind!=JsonValueKind.Null);
                result.AllowManagedMcpServersOnly=Has(root,"allowManagedMcpServersOnly",out var v) &&
                    v.ValueKind==JsonValueKind.True;
                result.UnsupportedControlValues=root.TryGetProperty("allowManagedMcpServersOnly",out var control) &&
                    control.ValueKind!=JsonValueKind.True && control.ValueKind!=JsonValueKind.False;
                if (root.TryGetProperty("strictPluginOnlyCustomization",out control))
                    result.UnsupportedControlValues|=control.ValueKind!=JsonValueKind.True &&
                        control.ValueKind!=JsonValueKind.False &&
                        (control.ValueKind!=JsonValueKind.Array || control.EnumerateArray().Any(x=>x.ValueKind!=JsonValueKind.String ||
                            !new[]{"skills","agents","hooks","mcp"}.Contains(x.GetString(),StringComparer.Ordinal)));
                result.StrictPluginOnlyMcp=Has(root,"strictPluginOnlyCustomization",out v) &&
                    (v.ValueKind==JsonValueKind.True || v.ValueKind==JsonValueKind.Array &&
                     v.EnumerateArray().Any(x=>x.ValueKind==JsonValueKind.String && x.GetString()=="mcp"));
                result.PolicyHelperPresent=Has(root,"policyHelper",out v) || Has(root,"policyHelpers",out v);
                result.ManagedSourcesBehaviorPresent=Has(root,"managedSourcesBehavior",out v);
                result.ManagedSourcesMerge=result.ManagedSourcesBehaviorPresent &&
                    v.ValueKind==JsonValueKind.String && v.GetString()=="merge";
                result.UnsupportedControlValues|=result.ManagedSourcesBehaviorPresent &&
                    (v.ValueKind!=JsonValueKind.String || v.GetString()!="merge" && v.GetString()!="first-wins");
                result.ManagedMcpServersPresent=Has(root,"managedMcpServers",out v);
                if (result.ManagedMcpServersPresent && v.ValueKind==JsonValueKind.Object)
                    result.ManagedMcpServerCount=v.EnumerateObject().Count();
                result.Allowed=Rules(root,"allowedMcpServers"); result.Denied=Rules(root,"deniedMcpServers");
                return result;
            }
        } catch { return result; }
    }
    static ClaudePolicyRuleSummary Rules(JsonElement root,string key) {
        var result=new ClaudePolicyRuleSummary();
        if (!Has(root,key,out var list)) return result;
        result.Present=true;
        if (list.ValueKind!=JsonValueKind.Array || list.GetArrayLength()>4096) {
            result.Valid=false; result.Invalid=1; return result;
        }
        var local=new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in list.EnumerateArray()) {
            result.Count++;
            if (rule.ValueKind!=JsonValueKind.Object) { result.Invalid++; continue; }
            bool name=Has(rule,"serverName",out var n), command=Has(rule,"serverCommand",out var c),
                url=Has(rule,"serverUrl",out var u);
            if ((name?1:0)+(command?1:0)+(url?1:0)!=1) { result.Invalid++; continue; }
            if (name && n.ValueKind==JsonValueKind.String && !String.IsNullOrWhiteSpace(n.GetString()) &&
                       n.GetString()==n.GetString().Trim()) {
                result.Names++;
                result.ExactAgentBrokerNameMatches|=n.GetString()=="desktop-ai-companion-agentbroker";
            } else if (command && c.ValueKind==JsonValueKind.Array && c.GetArrayLength()>0 &&
                       c.EnumerateArray().All(x=>x.ValueKind==JsonValueKind.String)) result.Commands++;
            else if (url && u.ValueKind==JsonValueKind.String) {
                result.Urls++;
                string text=u.GetString();
                if (text.Contains("${",StringComparison.Ordinal)) result.UnexpandedUrlRules++;
                else if (IsLiteralLocalRule(text)) {
                    result.Candidate7432MatchesLiteralLocalUrl|=MatchesLiteralLocal(text,Candidate);
                    string path=LocalRule.Match(text).Groups["path"].Value;
                    // An opaque localhost path can contain a live credential.
                    // Keep comparisons in memory and only disclose known MCP paths.
                    if (new[]{"", "/", "/*", "/mcp", "/mcp/", "/mcp/*", "/sse", "/sse/", "/sse/*"}
                            .Contains(path,StringComparer.Ordinal)) {
                        if (local.Count<64) local.Add(text);
                    } else result.SuppressedOpaqueLocalPathRules++;
                } else result.SuppressedOtherUrlRules++;
            } else result.Invalid++;
        }
        result.Valid=result.Invalid==0;
        result.LiteralLocalUrlPatterns=local.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        return result;
    }
    public static bool IsLiteralLocalRule(string text) {
        if (text==null || text.Length>512 || text.Contains("${",StringComparison.Ordinal) ||
            text.Any(c=>Char.IsControl(c))) return false;
        var m=LocalRule.Match(text);
        if (!m.Success) return false;
        string port=m.Groups["port"].Value;
        return port=="" || port=="*" || UInt32.TryParse(port,out uint n) && n>0 && n<=65535;
    }
    public static bool MatchesLiteralLocal(string pattern,string candidate) {
        if (!IsLiteralLocalRule(pattern) || !Uri.TryCreate(candidate,UriKind.Absolute,out var uri)) return false;
        var m=LocalRule.Match(pattern);
        if (!m.Groups["scheme"].Value.Equals(uri.Scheme,StringComparison.OrdinalIgnoreCase) ||
            !m.Groups["host"].Value.Equals(uri.Host.TrimEnd('.'),StringComparison.OrdinalIgnoreCase)) return false;
        string port=m.Groups["port"].Value;
        if (port!="*" && uri.Port!=(port=="" ? (uri.Scheme=="https"?443:80) : Int32.Parse(port))) return false;
        string path=m.Groups["path"].Value;
        return path=="" || Regex.IsMatch(uri.AbsolutePath,
            "^"+Regex.Escape(path).Replace("\\*",".*")+"$",RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
    }
}
'@

if ($DefinitionsOnly) { return }

if ($SelfTest) {
    $checks = 0
    function Assert-PolicyCheck([bool] $Condition) {
        if (-not $Condition) { throw "Policy inspector self-test failed at check $script:checks." }
        $script:checks++
    }
    $sample = [ClaudePolicyInspector]::Summarize('Synthetic', '{"allowedMcpServers":[{"serverUrl":"http://127.0.0.1:*/*"},{"serverUrl":"https://do-not-output.invalid/private"},{"serverCommand":["do-not-output-command","do-not-output-argument"]},{"serverName":"do-not-output-name"},{"serverUrl":"http://127.0.0.1:7432/do-not-output-secret-path"}],"deniedMcpServers":[{"serverName":"desktop-ai-companion-agentbroker"}],"env":{"DO_NOT_OUTPUT":"do-not-output-value"},"strictPluginOnlyCustomization":["mcp"],"allowManagedMcpServersOnly":true}')
    Assert-PolicyCheck ($sample.Parsed -and $sample.PolicyDocumentPresent)
    Assert-PolicyCheck ($sample.Allowed.Count -eq 5 -and $sample.Allowed.Urls -eq 3 -and $sample.Allowed.Commands -eq 1 -and $sample.Allowed.Names -eq 1)
    Assert-PolicyCheck ($sample.Allowed.Candidate7432MatchesLiteralLocalUrl -and $sample.Denied.ExactAgentBrokerNameMatches)
    Assert-PolicyCheck ($sample.StrictPluginOnlyMcp -and $sample.AllowManagedMcpServersOnly)
    $redacted = ConvertTo-Json -InputObject $sample -Depth 8 -Compress
    Assert-PolicyCheck (-not $redacted.Contains('do-not-output'))
    Assert-PolicyCheck ($sample.Allowed.LiteralLocalUrlPatterns.Count -eq 1)
    Assert-PolicyCheck ($sample.Allowed.SuppressedOpaqueLocalPathRules -eq 1)
    Assert-PolicyCheck ([ClaudePolicyInspector]::Summarize('Synthetic','{"allowedMcpServers":[{"serverName":"desktop-ai-companion-agentbroker"}]}').Allowed.ExactAgentBrokerNameMatches)
    Assert-PolicyCheck (-not [ClaudePolicyInspector]::Summarize('Synthetic','{"allowedMcpServers":[{"serverName":"agentbroker"}]}').Allowed.ExactAgentBrokerNameMatches)
    Assert-PolicyCheck ([ClaudePolicyInspector]::MatchesLiteralLocal('http://127.0.0.1', 'http://127.0.0.1/other'))
    Assert-PolicyCheck (-not [ClaudePolicyInspector]::MatchesLiteralLocal('http://127.0.0.1', 'http://127.0.0.1:7432/mcp'))
    Assert-PolicyCheck ([ClaudePolicyInspector]::MatchesLiteralLocal('HTTP://LOCALHOST.:*/*', 'http://localhost:7432/mcp'))
    Assert-PolicyCheck (-not [ClaudePolicyInspector]::MatchesLiteralLocal('http://localhost:*/*', 'http://127.0.0.1:7432/mcp'))
    Assert-PolicyCheck ([ClaudePolicyInspector]::MatchesLiteralLocal('http://127.0.0.1:7432/mcp', 'http://127.0.0.1:7432/mcp'))
    Assert-PolicyCheck (-not [ClaudePolicyInspector]::MatchesLiteralLocal('http://127.0.0.1:7432/MCP', 'http://127.0.0.1:7432/mcp'))
    Assert-PolicyCheck (-not [ClaudePolicyInspector]::MatchesLiteralLocal('https://127.0.0.1:7432/mcp', 'http://127.0.0.1:7432/mcp'))
    Assert-PolicyCheck (-not [ClaudePolicyInspector]::IsLiteralLocalRule('http://127.0.0.1.evil.invalid:*/*'))
    Assert-PolicyCheck (-not [ClaudePolicyInspector]::IsLiteralLocalRule('http://localhost@evil.invalid/*'))
    Assert-PolicyCheck (-not [ClaudePolicyInspector]::IsLiteralLocalRule('http://127.0.0.1:*/*?token=value'))
    Assert-PolicyCheck (-not [ClaudePolicyInspector]::IsLiteralLocalRule('http://127.0.0.1:65536/*'))
    Assert-PolicyCheck (-not [ClaudePolicyInspector]::IsLiteralLocalRule('http://${HOST}:*/*'))
    Assert-PolicyCheck ([ClaudePolicyInspector]::IsProtectedName('fixture.key'))
    Assert-PolicyCheck ([ClaudePolicyInspector]::IsProtectedName('.env.json'))
    Assert-PolicyCheck (-not [ClaudePolicyInspector]::IsProtectedName('managed-settings.json'))
    Assert-PolicyCheck (-not [ClaudePolicyInspector]::Summarize('Synthetic','{"allowedMcpServers":[],"allowedMcpServers":[{"serverUrl":"http://127.0.0.1/*"}]}').Parsed)
    Assert-PolicyCheck (-not [ClaudePolicyInspector]::Summarize('Synthetic','[]').Parsed)
    Assert-PolicyCheck ([ClaudePolicyInspector]::Summarize('Synthetic','{"managedSourcesBehavior":"merge","wslInheritsWindowsSettings":true}').PolicyDocumentPresent -eq $false)
    Assert-PolicyCheck ([ClaudePolicyInspector]::Summarize('Synthetic','{"managedSourcesBehavior":"merge"}').ManagedSourcesMerge)
    Assert-PolicyCheck ([ClaudePolicyInspector]::Summarize('Synthetic','{"allowedMcpServers":[]}').Allowed.Present)
    Assert-PolicyCheck ([ClaudePolicyInspector]::Summarize('Synthetic','{}').Allowed.Present -eq $false)
    Assert-PolicyCheck (-not [ClaudePolicyInspector]::Summarize('Synthetic','{"allowedMcpServers":[{"serverName":"x","serverUrl":"http://127.0.0.1/*"}]}').Allowed.Valid)
    Assert-PolicyCheck ([ClaudePolicyInspector]::Summarize('Synthetic','{"allowedMcpServers":[{"serverUrl":"http://${HOST}/*"}]}').Allowed.UnexpandedUrlRules -eq 1)
    Assert-PolicyCheck ([ClaudePolicyInspector]::Summarize('Synthetic','{"allowManagedMcpServersOnly":"false"}').UnsupportedControlValues)
    Assert-PolicyCheck ([ClaudePolicyInspector]::Summarize('Synthetic','{"strictPluginOnlyCustomization":[false]}').UnsupportedControlValues)
    Assert-PolicyCheck ([ClaudePolicyInspector]::Summarize('Synthetic','{"managedSourcesBehavior":"unknown"}').UnsupportedControlValues)
    Assert-PolicyCheck ([ClaudePolicyInspector]::Summarize('Synthetic','{"strictPluginOnlyCustomization":["unknown-surface"]}').UnsupportedControlValues)
    if ($IsWindows) {
        $fixtureRoot = [IO.Path]::Combine($PSScriptRoot, 'policy-inspector-selftest-' + [Guid]::NewGuid().ToString('N'))
        $fixturePath = [IO.Path]::Combine($fixtureRoot, 'synthetic-policy.json')
        $hardLinkPath = [IO.Path]::Combine($fixtureRoot, 'synthetic-policy-link.json')
        try {
            [void] [IO.Directory]::CreateDirectory($fixtureRoot)
            [IO.File]::WriteAllText($fixturePath, '{"allowedMcpServers":[]}', [Text.UTF8Encoding]::new($false))
            Assert-PolicyCheck ([ClaudePolicyInspector]::ReadFile('Synthetic', $fixturePath, 4096).Parsed)
            Assert-PolicyCheck ([ClaudePolicyInspector]::ReadFile('Synthetic', $fixturePath, 4).ReadStatus -eq 'refused_or_unreadable')
            Assert-PolicyCheck ([ClaudePolicyInspector]::ReadFile('Synthetic', [IO.Path]::Combine($fixtureRoot, 'absent.json'), 4096).ReadStatus -eq 'absent')
            Assert-PolicyCheck ([ClaudePolicyInspector]::CreateHardLinkW($hardLinkPath, $fixturePath, [IntPtr]::Zero))
            Assert-PolicyCheck ([ClaudePolicyInspector]::ReadFile('Synthetic', $fixturePath, 4096).ReadStatus -eq 'refused_or_unreadable')
            Assert-PolicyCheck ([ClaudePolicyInspector]::ReadFile('Synthetic', $hardLinkPath, 4096).ReadStatus -eq 'refused_or_unreadable')
            [IO.File]::Delete($hardLinkPath)
            [IO.File]::WriteAllBytes($fixturePath, [byte[]] @(0xFF, 0xFE, 0x80))
            Assert-PolicyCheck ([ClaudePolicyInspector]::ReadFile('Synthetic', $fixturePath, 4096).ReadStatus -eq 'refused_or_unreadable')
            [IO.File]::WriteAllText($fixturePath, '{"allowedMcpServers":[]}', [Text.UTF8Encoding]::new($true))
            Assert-PolicyCheck ([ClaudePolicyInspector]::ReadFile('Synthetic', $fixturePath, 4096).Parsed)
        } finally {
            # Only these two exclusively created synthetic files can be removed.
            # Never recurse or delete a computed parent directory.
            [IO.File]::Delete($hardLinkPath)
            [IO.File]::Delete($fixturePath)
            [IO.Directory]::Delete($fixtureRoot, $false)
        }
    }
    [pscustomobject]@{ SelfTestPassed = $true; Checks = $checks; LivePolicyRead = $false } | ConvertTo-Json -Compress
    exit 0
}

if (-not $IsWindows -or -not [Environment]::Is64BitProcess) {
    throw 'The live inspector requires 64-bit PowerShell 7 on Windows.'
}

# These exact paths are intentionally fixed. Do not follow environment/config
# overrides to arbitrary files. No project configuration or credential file is read.
$canonicalHome = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
$managedRoot = 'C:\Program Files\ClaudeCode'
if ($UserSettingsOnly) {
    [pscustomobject]@{
        ReadOnly = $true
        EffectiveAuthorizationDetermined = $false
        Source = [ClaudePolicyInspector]::ReadFile('UserSettings', [IO.Path]::Combine($canonicalHome, '.claude', 'settings.json'), 2097152)
    } | ConvertTo-Json -Depth 10
    return
}
$sources = @(
    [ClaudePolicyInspector]::ReadFile('RemoteCacheUnverified', [IO.Path]::Combine($canonicalHome, '.claude', 'remote-settings.json'), 8388608)
    [ClaudePolicyInspector]::ReadRegistry($true)
    [ClaudePolicyInspector]::ReadFile('ManagedFile', [IO.Path]::Combine($managedRoot, 'managed-settings.json'), 2097152)
    [ClaudePolicyInspector]::ReadDropIns([IO.Path]::Combine($managedRoot, 'managed-settings.d'))
    [ClaudePolicyInspector]::ReadRegistry($false)
)

[pscustomobject]@{
    ReadOnly = $true
    EffectiveAuthorizationDetermined = $false
    LiteralLocalRulesAreSourceInventory = $true
    RemoteCacheEligibilityOrFreshnessVerified = $false
    HostParentPolicyInspected = $false
    PolicyHelpersExecuted = $false
    EnvironmentExpanded = $false
    ConfigOverridesInspected = $false
    UserProjectLocalSettingsInspected = $false
    HkcuIsFallbackOnly = $true
    DenialsAcrossAdminSourcesMustBeApplied = $true
    Sources = $sources
} | ConvertTo-Json -Depth 10
