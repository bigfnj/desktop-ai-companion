#requires -Version 7
<#
.SYNOPSIS
    Install a verified AgentBroker ZIP for the current user and restart only its installed companion.
.DESCRIPTION
    Run ValidateOnly first. Actual installation requires the caller's approval for the displayed
    script invocation, the current user's application folders, companion restart and broker identity
    creation. First installation refuses existing module/settings paths. Explicit StageUpdate uses
    the host's staged-update mechanism, preserving all existing module data and settings. It never changes client MCP
    configuration, enables native delivery, changes firewall rules, or restarts an editor.
    HostRuntimeDirectory optionally upgrades only the canonical public host runtime after graceful
    shutdown, retaining host runtime rollback files. ArchivePreviousCopy archives an already
    existing replaced-module remnant before the update. The host deletes the newly replaced module
    after a successful swap; module rollback requires a separately verified known-good ZIP.
    Before actual installation, run scripts-utilities/scripts/test-host-projection.ps1 and use
    an unprojected shell if that check reports projection.
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Install-LocalAgentBroker.ps1 -ZipPath modules-dist/agentbroker.zip -ExpectedSha256 <catalog SHA256> -ValidateOnly
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Install-LocalAgentBroker.ps1 -ZipPath modules-dist/agentbroker.zip -ExpectedSha256 <catalog SHA256> -CompanionProcessId <exact PID>
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Install-LocalAgentBroker.ps1 -ZipPath modules-dist/agentbroker.zip -ExpectedSha256 <catalog SHA256> -CompanionProcessId <exact PID> -StageUpdate
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Install-LocalAgentBroker.ps1 -ZipPath modules-dist/agentbroker.zip -ExpectedSha256 <catalog SHA256> -CompanionProcessId <exact PID> -StageUpdate -HostRuntimeDirectory build/DesktopAICompanionPortable/bin/Release/x64 -ArchivePreviousCopy
#>
[CmdletBinding()]
param(
    [string]$ZipPath,
    [string]$ExpectedSha256,
    [int]$CompanionProcessId,
    [switch]$ValidateOnly,
    [switch]$StageUpdate,
    [string]$HostRuntimeDirectory,
    [switch]$ArchivePreviousCopy,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Self-test seam (lane T round 8), null outside -SelfTest: called with (record, staged source, backup or $null) in place of
# nothing, immediately before a host runtime file's replace or move; it may change files as a failing operation would, and a
# non-null result is thrown as that operation's failure (a Win32 1177 partial replacement, or a failure reported after landing).
$script:HostRuntimeOperationForSelfTest = $null
# Lane T round 8 (2/2, S-010, DECISIONS 118): a replace or move that a transient lock refuses (an indexer or scanner holding the
# file) is tried again, at most ReplaceAttempts times, ReplaceRetryDelayMs apart, and only for the two Win32 errors that leave
# every file where it was. Self-test seam, null outside -SelfTest: called (name, attempt, failure) after a transient failure and
# before the delay, so a fixture releases a hold on an OBSERVED retry, never on the clock (X1b T-r3 finding 4). Round 8 (3/3, X3's
# T8-X3-1): a second seam, also null outside -SelfTest, called (name, attempt) at the END of the delay, after the sleep and before
# the next attempt's guard, so a fixture's concurrent write lands inside the wait itself, where only a guard that runs after the
# wait can see it (a write made in the first seam is seen by a guard moved to just before the sleep, too).
$script:ReplaceAttempts = 5
$script:ReplaceRetryDelayMs = 100
$script:ReplaceRetryForSelfTest = $null
$script:ReplaceRetryWaitForSelfTest = $null

function Initialize-InstallerNative {
    if ('AgentBrokerLocalInstallNative' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

public static class AgentBrokerLocalInstallNative {
    public delegate bool WindowCallback(IntPtr hwnd, IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", SetLastError=true)] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileIdentity identity);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateHardLink(string link, string existing, IntPtr security);
    [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr table, ref uint length, bool order, int family, int tableClass, uint reserved);
    [StructLayout(LayoutKind.Sequential)] struct TcpRow { public uint State, Address, Port, RemoteAddress, RemotePort, Pid; }
    [StructLayout(LayoutKind.Sequential)] struct FileIdentity {
        public uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, WrittenLow, WrittenHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    public sealed class Listener { public int Port; public int Pid; public string Address = ""; }
    public sealed class Probe { public bool Initialized; public int ToolCount; public bool CleanExit; }

    public static string FileStamp(FileStream file) {
        if (!GetFileInformationByHandle(file.SafeFileHandle, out var identity)) throw new IOException("Archive file identity unavailable.");
        if (identity.Links != 1 || (identity.Attributes & (0x10 | 0x400)) != 0)
            throw new IOException("Archive input must be one ordinary file with one link.");
        return $"{identity.Volume:X8}:{identity.IndexHigh:X8}:{identity.IndexLow:X8}";
    }
    public static void VerifySingleLink(FileStream file) { FileStamp(file); }

    // The package's own ModuleInfo.Version: the string loaded immediately before a call to ModuleInfo's
    // set_Version. The module DLL's file version stays 1.0.0.0 and the ZIP carries no manifest, so this
    // literal is the only version the package itself declares. Read from metadata; nothing is loaded.
    public static string DeclaredModuleVersion(byte[] assembly) {
        using var stream = new MemoryStream(assembly, false);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) throw new InvalidDataException("The package module has no metadata.");
        var metadata = pe.GetMetadataReader();
        var setters = new HashSet<int>();
        foreach (var handle in metadata.MemberReferences) {
            var reference = metadata.GetMemberReference(handle);
            if (metadata.StringComparer.Equals(reference.Name, "set_Version") && reference.Parent.Kind == HandleKind.TypeReference &&
                metadata.StringComparer.Equals(metadata.GetTypeReference((TypeReferenceHandle)reference.Parent).Name, "ModuleInfo"))
                setters.Add(MetadataTokens.GetToken(handle));
        }
        foreach (var handle in metadata.MethodDefinitions) {
            var method = metadata.GetMethodDefinition(handle);
            if (metadata.StringComparer.Equals(method.Name, "set_Version") &&
                metadata.StringComparer.Equals(metadata.GetTypeDefinition(method.GetDeclaringType()).Name, "ModuleInfo"))
                setters.Add(MetadataTokens.GetToken(handle));
        }
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in metadata.MethodDefinitions) {
            int rva = metadata.GetMethodDefinition(handle).RelativeVirtualAddress;
            if (rva == 0) continue;
            byte[] il = pe.GetMethodBody(rva).GetILBytes() ?? Array.Empty<byte>();
            // ldstr <user string> then call/callvirt <ModuleInfo::set_Version>.
            for (int i = 0; i + 10 <= il.Length; i++) {
                if (il[i] != 0x72 || il[i + 4] != 0x70 || il[i + 5] != 0x6F && il[i + 5] != 0x28) continue;
                if (!setters.Contains(BitConverter.ToInt32(il, i + 6))) continue;
                found.Add(metadata.GetUserString(MetadataTokens.UserStringHandle(BitConverter.ToInt32(il, i + 1) & 0xFFFFFF)));
            }
        }
        if (found.Count != 1) throw new InvalidDataException("The package module did not declare exactly one version.");
        foreach (string version in found) return version;
        throw new InvalidDataException("The package module version was unavailable.");
    }

    // The exact MCP tool set each published package serves. A rollback through this script installs an
    // older package, so the probe pins THAT package's contract, never this script's newest one.
    public static string[] ExpectedTools(string declaredVersion) {
        if (!Version.TryParse(declaredVersion, out var version) || version.Build < 0 || version.Revision >= 0)
            throw new InvalidDataException("The package module version is not major.minor.patch.");
        if (version < new Version(1, 0, 0)) throw new InvalidDataException("This helper does not know that package's MCP tool contract.");
        var tools = new List<string> { "list_sessions", "register_agent", "send_message", "read_inbox",
            "acknowledge_message", "get_message_receipt", "reply_to_message" };
        // 1.1.0: the agent-to-agent bus (durable mailboxes, blocking waits, field-compared send).
        if (version >= new Version(1, 1, 0)) tools.AddRange(new[] { "send", "wait_for_messages", "wait_for_reply" });
        // 1.3.0: a sender waits for its message's receipt instead of polling get_message_receipt.
        if (version >= new Version(1, 3, 0)) tools.Add("wait_for_receipt");
        // 1.4.0: an agent asks the broker which native directory row is its own.
        if (version >= new Version(1, 4, 0)) tools.Add("whoami");
        // 1.8.0: local active-turn inbox, report-only recovery, hints and optional custom titles.
        if (version >= new Version(1, 8, 0)) tools.AddRange(new[] { "read_inbox_page", "read_native_recovery", "set_session_declaration", "set_session_title" });
        // 1.10.5: file attachments between paired brokers (listed whether or not the owner turned them on).
        if (version >= new Version(1, 10, 5)) tools.AddRange(new[] { "send_with_attachments", "fetch_attachment" });
        return tools.ToArray();
    }
    public static void CreateTestHardLink(string link, string existing) {
        if (!CreateHardLink(link, existing, IntPtr.Zero)) throw new Win32Exception();
    }
    public static void PublishMarkerFile(string temporary, string destination, bool replace) {
        // Pass a real null backup name; PowerShell converts a null string argument into an empty path.
        if (replace) File.Replace(temporary, destination, null);
        else File.Move(temporary, destination, false);
    }

    public static long Created(Process process) {
        if (!GetProcessTimes(process.SafeHandle, out long created, out _, out _, out _)) throw new Win32Exception();
        return created;
    }
    public static string Image(Process process) {
        var text = new StringBuilder(32768); uint size = (uint)text.Capacity;
        if (!QueryFullProcessImageName(process.SafeHandle, 0, text, ref size)) throw new Win32Exception();
        return Path.GetFullPath(text.ToString());
    }
    public static void CloseLifetime(Process process, long expectedCreation) {
        if (Created(process) != expectedCreation || process.HasExited) throw new IOException("Companion identity changed.");
        IntPtr target = IntPtr.Zero; int count = 0;
        EnumWindows((window, _) => {
            GetWindowThreadProcessId(window, out uint pid);
            if (pid == process.Id) { var title = new StringBuilder(256); GetWindowText(window, title, title.Capacity);
                if (title.ToString() == "DesktopAICompanion") { target = window; count++; } }
            return true;
        }, IntPtr.Zero);
        if (count != 1) throw new IOException("Exact companion lifetime window was not unique.");
        GetWindowThreadProcessId(target, out uint owner);
        if (owner != process.Id || Created(process) != expectedCreation) throw new IOException("Companion window changed.");
        // A successfully closing form can destroy its HWND before the acknowledgement returns.
        // Retained-process exit is authoritative even if SendMessageTimeout reports zero.
        SendMessageTimeout(target, 0x0010, IntPtr.Zero, IntPtr.Zero, 3, 5000, out _);
        if (!process.WaitForExit(15000)) throw new IOException("Companion did not exit gracefully; no force was used.");
    }
    public sealed class ListenerPlan {
        public int LoopbackPort = 7432, LanPort = 7433;
        public bool EnableLan = true;
        public int ExpectedCount => EnableLan ? 2 : 1;
        public bool Includes(int port) => port == LoopbackPort || EnableLan && port == LanPort;
    }
    public static ListenerPlan ListenerSettings(string json) {
        var values = JsonSerializer.Deserialize<Dictionary<string,string>>(json,
            new JsonSerializerOptions { MaxDepth = 16 }) ?? throw new IOException("AgentBroker listener settings are not an object.");
        var plan = new ListenerPlan();
        if (values.TryGetValue("loopbackPort", out var local) && int.TryParse(local, out var localPort)) plan.LoopbackPort = localPort;
        if (values.TryGetValue("lanPort", out var lan) && int.TryParse(lan, out var lanPort)) plan.LanPort = lanPort;
        if (values.TryGetValue("enableLan", out var enabled) && bool.TryParse(enabled, out var useLan)) plan.EnableLan = useLan;
        if (plan.LoopbackPort < 1 || plan.LoopbackPort > 65535 || plan.LanPort < 1 || plan.LanPort > 65535 ||
            plan.EnableLan && plan.LoopbackPort == plan.LanPort)
            throw new IOException("AgentBroker loopbackPort, lanPort or enableLan settings are invalid.");
        return plan;
    }
    public static Listener[] Listeners(ListenerPlan plan) {
        uint size = 0; uint status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        if (status != 122 && status != 0 || size > 1024 * 1024) throw new IOException("TCP owner table unavailable.");
        IntPtr memory = Marshal.AllocHGlobal((int)Math.Max(size, 4));
        try {
            if (GetExtendedTcpTable(memory, ref size, false, 2, 3, 0) != 0) throw new IOException("TCP owner table changed.");
            int count = Marshal.ReadInt32(memory), rowSize = Marshal.SizeOf<TcpRow>();
            if (count < 0 || 4L + (long)count * rowSize > size) throw new IOException("Invalid TCP owner table.");
            var found = new List<Listener>();
            for (int index = 0; index < count; index++) {
                var row = Marshal.PtrToStructure<TcpRow>(IntPtr.Add(memory, 4 + index * rowSize));
                int port = (int)(((row.Port & 255) << 8) | ((row.Port >> 8) & 255));
                if (plan.Includes(port)) found.Add(new Listener { Port = port, Pid = (int)row.Pid, Address = new IPAddress(row.Address).ToString() });
            }
            return found.ToArray();
        } finally { Marshal.FreeHGlobal(memory); }
    }
    static async Task<string> FrameAsync(StreamReader reader, CancellationToken token) {
        var text = new StringBuilder(); char[] one = new char[1];
        while (await reader.ReadAsync(one.AsMemory(), token) != 0) {
            if (one[0] == '\n') return text.ToString();
            if (text.Length >= 512 * 1024) throw new IOException("MCP probe response exceeded its bound.");
            text.Append(one[0]);
        }
        throw new IOException("MCP probe response closed.");
    }
    static async Task DrainErrorsAsync(StreamReader reader, CancellationToken token) {
        char[] buffer = new char[2048]; int total = 0, read;
        try { while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) != 0) {
            total += read; if (total > 65536) throw new IOException("MCP probe diagnostics exceeded their bound.");
        } } finally { Array.Clear(buffer); }
    }
    public static async Task<Probe> ProbeAsync(string worker, string data, string[] expectedTools) {
        if (expectedTools is null || expectedTools.Length == 0) throw new ArgumentException("The package's MCP tool contract is required.");
        var start = new ProcessStartInfo(worker) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true) };
        foreach (string argument in new[] { "--mcp-stdio", "--data", data }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("MCP probe did not start.");
        SafeProcessHandle ownedHandle = process.SafeHandle;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task errors = DrainErrorsAsync(process.StandardError, deadline.Token);
        try {
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"agentbroker-install-check\",\"version\":\"1.0.0\"}}}".AsMemory(), deadline.Token);
            await process.StandardInput.FlushAsync(deadline.Token);
            using var initialized = JsonDocument.Parse(await FrameAsync(process.StandardOutput, deadline.Token), new JsonDocumentOptions { MaxDepth = 32 });
            var first = initialized.RootElement;
            if (first.GetProperty("id").GetInt32() != 1 || first.TryGetProperty("error", out _) ||
                !first.GetProperty("result").TryGetProperty("protocolVersion", out _)) throw new IOException("MCP initialization failed.");
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}".AsMemory(), deadline.Token);
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}".AsMemory(), deadline.Token);
            await process.StandardInput.FlushAsync(deadline.Token);
            using var listed = JsonDocument.Parse(await FrameAsync(process.StandardOutput, deadline.Token), new JsonDocumentOptions { MaxDepth = 32 });
            var second = listed.RootElement;
            if (second.GetProperty("id").GetInt32() != 2 || second.TryGetProperty("error", out _)) throw new IOException("MCP tool listing failed.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in second.GetProperty("result").GetProperty("tools").EnumerateArray())
                if (!names.Add(tool.GetProperty("name").GetString() ?? "")) throw new IOException("Duplicate MCP probe tool.");
            if (!names.SetEquals(expectedTools)) throw new IOException("MCP tool contract differed.");
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            await errors;
            if (process.ExitCode != 0) throw new IOException("MCP probe did not exit cleanly.");
            return new Probe { Initialized = true, ToolCount = names.Count, CleanExit = true };
        } finally {
            try { process.StandardInput.Close(); } catch { }
            if (!process.HasExited && !process.WaitForExit(3000)) {
                // Only our retained probe handle. The companion and broker are never force-stopped.
                TerminateProcess(ownedHandle, 1); process.WaitForExit(3000);
            }
            deadline.Cancel();
            try { await errors; } catch { }
        }
    }
}
'@
}

function Get-ProtectedEntryPattern([string]$Name) {
    $previous = ''
    foreach ($raw in $Name.Replace('\', '/').Split('/')) {
        $part = $raw.Split(':')[0].TrimEnd(' ', '.')
        if ($part.EndsWith('.env', [StringComparison]::OrdinalIgnoreCase)) { return 'W01' }
        if ($part.StartsWith('.env.', [StringComparison]::OrdinalIgnoreCase)) { return 'W02' }
        if ($part.Equals('.npmrc', [StringComparison]::OrdinalIgnoreCase)) { return 'W03' }
        if ($previous.Equals('.git', [StringComparison]::OrdinalIgnoreCase) -and
            $part.Equals('credentials', [StringComparison]::OrdinalIgnoreCase)) { return 'W04' }
        foreach ($pair in @(@('.pem', 'W05'), @('.key', 'W06'), @('.p12', 'W07'),
                            @('.pfx', 'W08'), @('.keystore', 'W09'))) {
            if ($part.EndsWith($pair[0], [StringComparison]::OrdinalIgnoreCase)) { return $pair[1] }
        }
        if ($part.StartsWith('privatekey', [StringComparison]::OrdinalIgnoreCase)) { return 'W10' }
        if ($part.Equals('.mozilla', [StringComparison]::OrdinalIgnoreCase)) { return 'H29' }
        if ($previous.Equals('.config', [StringComparison]::OrdinalIgnoreCase) -and
            $part.Equals('google-chrome', [StringComparison]::OrdinalIgnoreCase)) { return 'H30' }
        if ($previous.Equals('.config', [StringComparison]::OrdinalIgnoreCase) -and
            $part.Equals('chromium', [StringComparison]::OrdinalIgnoreCase)) { return 'H31' }
        if ($previous.Equals('Mozilla', [StringComparison]::OrdinalIgnoreCase) -and
            $part.Equals('Firefox', [StringComparison]::OrdinalIgnoreCase)) { return 'H26' }
        $previous = $part
    }
    return $null
}

function Assert-EntryName([string]$Name, [int]$ExternalAttributes = 0) {
    $protected = Get-ProtectedEntryPattern $Name
    if ($protected) { throw "Archive metadata contains a protected $protected path. No entry contents were read." }
    if ([string]::IsNullOrWhiteSpace($Name) -or $Name.Length -gt 512 -or $Name.Contains('\') -or
        $Name.StartsWith('/') -or $Name.Contains(':') -or $Name.IndexOfAny([char[]]"`0`r`n<>`"|?*") -ge 0) {
        throw 'Archive entry name is unsafe.'
    }
    $trimmed = $Name.TrimEnd('/')
    foreach ($part in $trimmed.Split('/')) {
        if ($part.Length -eq 0 -or $part -in @('.', '..') -or $part -ne $part.TrimEnd(' ', '.') -or
            $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)' -or
            $part.ToCharArray().Where({ [char]::IsControl($_) }).Count -gt 0) { throw 'Archive entry name is unsafe.' }
    }
    $attributes = [uint32]([int64]$ExternalAttributes -band 0xffffffffL)
    if (($attributes -band 0x400) -ne 0 -or (($attributes -shr 16) -band 0xf000) -eq 0xa000) {
        throw 'Archive links and reparse entries are forbidden.'
    }
    return $trimmed
}

function Assert-ContainedPath([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path)
    $boundary = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root)) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'An installation path escapes its explicit root.'
    }
    return $full
}

function Assert-NoReparseComponents([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if ([IO.File]::Exists($cursor) -or [IO.Directory]::Exists($cursor)) {
            if (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Installation paths must not contain reparse components.'
            }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

function Read-UpdateMarker([string]$Path) {
    Assert-NoReparseComponents $Path
    if (-not [IO.File]::Exists($Path)) {
        if (Test-Path -LiteralPath $Path) { throw 'The update marker is not an ordinary file.' }
        return [pscustomobject]@{ Exists = $false; Ids = [string[]]@(); Stamp = ''; Digest = '' }
    }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $stamp = [AgentBrokerLocalInstallNative]::FileStamp($stream)
        if ($stream.Length -gt 65536) { throw 'The update marker exceeds its size bound.' }
        $bytes = [byte[]]::new([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -eq 0) { throw 'The update marker was incomplete.' }
            $offset += $read
        }
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
        $ids = [Collections.Generic.List[string]]::new()
        $known = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($line in $text.Split("`n")) {
            $id = $line.Trim()
            if ($id.Length -eq 0) { continue }
            if ($id -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$') { throw 'The update marker contains an invalid module ID.' }
            if ($known.Add($id)) { $ids.Add($id) }
            if ($ids.Count -gt 256) { throw 'The update marker exceeds its module-count bound.' }
        }
        return [pscustomobject]@{ Exists = $true; Ids = $ids.ToArray(); Stamp = $stamp;
            Digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) }
    } finally { $stream.Dispose() }
}

function Publish-UpdateMarker([string]$Path, $Snapshot) {
    $parent = [IO.Path]::GetDirectoryName($Path)
    $temporary = Assert-ContainedPath (Join-Path $parent ('agentbroker-update-' + [guid]::NewGuid().ToString('N') + '.tmp')) $parent
    $ids = [Collections.Generic.List[string]]::new()
    foreach ($id in $Snapshot.Ids) { $ids.Add($id) }
    if (-not @($ids.Where({ $_.Equals('agentbroker', [StringComparison]::OrdinalIgnoreCase) })).Count) {
        if ($ids.Count -ge 256) { throw 'The update marker has no capacity for another module.' }
        $ids.Add('agentbroker')
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(([string]::Join("`n", $ids) + "`n"))
    Assert-NoReparseComponents $temporary
    $temporaryCreated = $false
    try {
        $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $temporaryCreated = $true
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        $current = Read-UpdateMarker $Path
        if ($current.Exists -ne $Snapshot.Exists -or $current.Stamp -cne $Snapshot.Stamp -or $current.Digest -cne $Snapshot.Digest) {
            throw 'The update marker changed during staging; its existing IDs were not replaced.'
        }
        # Round 8 (2/2): retried on a transient lock; the marker and the staged copy are re-read after each delay, so a module
        # ID added while waiting is never overwritten (X1b T-r3 finding 1's marker counterexample).
        $stagedDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
        Invoke-WithTransientRetry 'the update marker' {
            param($attempt)
            [AgentBrokerLocalInstallNative]::PublishMarkerFile($temporary, $Path, $Snapshot.Exists)
        } {
            $again = Read-UpdateMarker $Path
            if ($again.Exists -ne $Snapshot.Exists -or $again.Stamp -cne $Snapshot.Stamp -or $again.Digest -cne $Snapshot.Digest) { 'the update marker changed' }
            elseif ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($temporary))) -cne $stagedDigest) { 'its staged copy changed' }
            else { $null }
        }
    }
    finally {
        [void](Assert-ContainedPath $temporary $parent)
        Assert-NoReparseComponents $temporary
        if ($temporaryCreated -and [IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

function Publish-NewSettingsStage([string]$Source, [string]$Destination, [string]$Parent) {
    foreach ($path in @($Source, $Destination)) {
        [void](Assert-ContainedPath $path $Parent)
        Assert-NoReparseComponents $path
    }
    if (Test-Path -LiteralPath $Destination) { throw 'Settings destination already exists; it was preserved.' }
    [IO.Directory]::Move($Source, $Destination)
}

function Get-RequiredListenerOwner([object[]]$Listeners, $Plan) {
    if ($Listeners.Count -ne $Plan.ExpectedCount -or $Listeners.Count -lt 1) { return $null }
    $owner = [int]$Listeners[0].Pid
    if ($owner -le 1 -or @($Listeners | Where-Object { $_.Pid -ne $owner }).Count -ne 0) { return $null }
    if (@($Listeners | Where-Object { $_.Port -eq $Plan.LoopbackPort -and $_.Address -ceq '127.0.0.1' }).Count -ne 1) { return $null }
    if ($Plan.EnableLan -and @($Listeners | Where-Object { $_.Port -eq $Plan.LanPort }).Count -ne 1) { return $null }
    return $owner
}

function Publish-UpdateStage([string]$Source, [string]$Destination, [string]$Replaced, [string]$Root) {
    foreach ($path in @($Source, $Destination, $Replaced)) {
        [void](Assert-ContainedPath $path $Root)
        Assert-NoReparseComponents $path
    }
    if (Test-Path -LiteralPath $Destination) { throw 'An AgentBroker staged update already exists; it was preserved.' }
    if (Test-Path -LiteralPath $Replaced) { throw 'An AgentBroker replaced copy already exists; it was preserved.' }
    [IO.Directory]::Move($Source, $Destination)
}

function Get-PublicPayloadSnapshot([string]$Directory) {
    $digests = @{}
    foreach ($relative in @('AgentBroker.dll', 'worker\AgentBroker.Worker.dll')) {
        $path = Assert-ContainedPath (Join-Path $Directory $relative) $Directory
        if (Get-ProtectedEntryPattern $path) { throw 'A public payload path matched a protected component.' }
        Assert-NoReparseComponents $path
        $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            [AgentBrokerLocalInstallNative]::VerifySingleLink($stream)
            if ($stream.Length -gt 128MB) { throw 'A public payload file exceeded its validated size limit.' }
            $digests[$relative] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))
        } finally { $stream.Dispose() }
    }
    return $digests
}

function Test-UpdateApplication([string]$Directory, $Expected, [string]$Stage, [string]$Marker) {
    $payloadMatches = $false
    try {
        $actual = Get-PublicPayloadSnapshot $Directory
        $payloadMatches = $actual['AgentBroker.dll'] -ceq $Expected['AgentBroker.dll'] -and
            $actual['worker\AgentBroker.Worker.dll'] -ceq $Expected['worker\AgentBroker.Worker.dll']
    } catch { $payloadMatches = $false }
    Assert-NoReparseComponents $Stage
    $stageProcessed = -not (Test-Path -LiteralPath $Stage)
    $pending = Read-UpdateMarker $Marker
    $markerProcessed = @($pending.Ids.Where({ $_.Equals('agentbroker', [StringComparison]::OrdinalIgnoreCase) })).Count -eq 0
    return [pscustomobject]@{ PayloadMatches = $payloadMatches; StageProcessed = $stageProcessed;
        MarkerProcessed = $markerProcessed; Applied = ($payloadMatches -and $stageProcessed -and $markerProcessed) }
}

function Read-PublicInstallerText([string]$Path, [int]$Limit = 65536) {
    if (Get-ProtectedEntryPattern $Path) { throw 'A public installer input matched a protected path.' }
    Assert-NoReparseComponents $Path
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        [AgentBrokerLocalInstallNative]::VerifySingleLink($stream)
        if ($stream.Length -gt $Limit) { throw 'A public installer text input exceeded its bound.' }
        $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false, $true), $true, 4096, $true)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}

function Get-HostRuntimeContract([string]$Repository) {
    $names = [Collections.Generic.List[string]]::new()
    $known = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in (Read-PublicInstallerText (Join-Path $Repository 'packaging\runtime-files.txt')).Split("`n")) {
        $name = $line.Trim()
        if (-not $name -or $name.StartsWith('#')) { continue }
        [void](Assert-EntryName $name)
        if ($name.Contains('/') -or [IO.Path]::GetFileName($name) -cne $name -or -not $known.Add($name)) {
            throw 'The canonical runtime list must contain unique ordinary leaf names.'
        }
        $names.Add($name)
        if ($names.Count -gt 128) { throw 'The canonical runtime list exceeded its file bound.' }
    }
    foreach ($required in @('DesktopAICompanion.exe', 'DesktopAICompanion.dll', 'DesktopAICompanion.Contracts.dll')) {
        if (-not $known.Contains($required)) { throw 'The canonical runtime list lacks a versioned host component.' }
    }
    $document = [Xml.XmlDocument]::new(); $document.XmlResolver = $null
    $document.LoadXml((Read-PublicInstallerText (Join-Path $Repository 'ProductVersion.props')))
    $version = [version]$document.Project.PropertyGroup.DesktopAICompanionAssemblyVersion
    if ($version -lt [version]'1.6.1.0') { throw 'This runtime update requires the live-options host ABI or later.' }
    return [pscustomobject]@{ Names = $names.ToArray(); Version = $version }
}

function Get-AgentBrokerHostFloor([string]$Repository) {
    $catalog = (Read-PublicInstallerText (Join-Path $Repository 'modules-dist\modules.json')) | ConvertFrom-Json -Depth 32
    $entries = @($catalog.modules)
    if ($entries.Count -gt 128) { throw 'The public module catalog exceeded its entry bound.' }
    $matches = @($entries.Where({ $_.id -ceq 'agentbroker' }))
    if ($matches.Count -ne 1) { throw 'The published AgentBroker host requirement was not unique.' }
    return [version]$matches[0].minHostVersion
}

function Assert-EffectiveHostVersion([version]$Actual, [version]$Minimum) {
    if ($null -eq $Actual -or $null -eq $Minimum -or $Actual -lt $Minimum) {
        throw 'The effective host runtime is older than the published AgentBroker minimum; supply a verified compatible HostRuntimeDirectory before staging.'
    }
}

function Get-HostRuntimeFileSnapshot([string]$Path) {
    if (Get-ProtectedEntryPattern $Path) { throw 'A public runtime path matched a protected component.' }
    Assert-NoReparseComponents $Path
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $stamp = [AgentBrokerLocalInstallNative]::FileStamp($stream)
        if ($stream.Length -gt 128MB) { throw 'A public runtime file exceeded its size bound.' }
        return [pscustomobject]@{ Stamp = $stamp; Hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
    } finally { $stream.Dispose() }
}

function Get-PublicHostVersion([string]$Path) {
    if (Get-ProtectedEntryPattern $Path) { throw 'A versioned host path matched a protected component.' }
    Assert-NoReparseComponents $Path
    Initialize-InstallerNative
    # GetVersionInfo parses file contents through its own path-based open. Keep
    # the validated ordinary input pinned against writes/replacement until it returns.
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        [AgentBrokerLocalInstallNative]::VerifySingleLink($stream)
        if ($stream.Length -gt 128MB) { throw 'A versioned public host file exceeded its size bound.' }
        return [version][Diagnostics.FileVersionInfo]::GetVersionInfo($Path).FileVersion
    } finally { $stream.Dispose() }
}

function Assert-HostRuntimeVersion([string]$Directory, [version]$Expected) {
    foreach ($name in @('DesktopAICompanion.exe', 'DesktopAICompanion.dll', 'DesktopAICompanion.Contracts.dll')) {
        $path = Assert-ContainedPath (Join-Path $Directory $name) $Directory
        if (Get-ProtectedEntryPattern $path) { throw 'A versioned host path matched a protected component.' }
        Assert-NoReparseComponents $path
        if ((Get-PublicHostVersion $path) -ne $Expected) {
            throw 'The public host runtime file version differs from ProductVersion.props.'
        }
    }
}

function New-HostRuntimePlan([string]$Source, [string]$Destination, [string[]]$Names, [version]$ExpectedVersion) {
    Assert-NoReparseComponents $Source
    Assert-NoReparseComponents $Destination
    if (-not [IO.Directory]::Exists($Source)) { throw 'The supplied public host runtime directory is unavailable.' }
    if ([IO.Path]::GetFullPath($Source).Equals([IO.Path]::GetFullPath($Destination), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The public host runtime source must differ from the installed destination.'
    }
    $records = [Collections.Generic.List[object]]::new()
    $plan = [pscustomobject]@{ Destination = $Destination; Records = $records; ExpectedVersion = $ExpectedVersion; Stage = ''; New = ''; Previous = '';
        Changed = [Collections.Generic.List[object]]::new(); InFlight = $null; Applied = $false; RollbackIncomplete = $false }
    try {
        $total = 0L; $oldTotal = 0L
        foreach ($name in $Names) {
            [void](Assert-EntryName $name)
            if ($name.Contains('/') -or [IO.Path]::GetFileName($name) -cne $name) { throw 'A host runtime name is not a flat leaf.' }
            $inputPath = Assert-ContainedPath (Join-Path $Source $name) $Source
            $target = Assert-ContainedPath (Join-Path $Destination $name) $Destination
            if ((Get-ProtectedEntryPattern $inputPath) -or (Get-ProtectedEntryPattern $target)) {
                throw 'A public host runtime path matched a protected component.'
            }
            foreach ($path in @($inputPath, $target)) { Assert-NoReparseComponents $path }
            $record = [pscustomobject]@{ Name = $name; Source = $null; Target = $target; Old = $null;
                OldExists = $false; OldStamp = ''; OldHash = ''; Hash = '' }
            $records.Add($record)
            $record.Source = [IO.File]::Open($inputPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            [AgentBrokerLocalInstallNative]::VerifySingleLink($record.Source)
            $total += $record.Source.Length
            if ($record.Source.Length -gt 128MB -or $total -gt 300MB) { throw 'Public runtime inputs exceeded their size bound.' }
            $record.Hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($record.Source))
            if ([IO.File]::Exists($target)) {
                $record.Old = [IO.File]::Open($target, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                $record.OldStamp = [AgentBrokerLocalInstallNative]::FileStamp($record.Old)
                $oldTotal += $record.Old.Length
                if ($record.Old.Length -gt 128MB -or $oldTotal -gt 300MB) { throw 'Installed public runtime files exceeded their size bound.' }
                $record.OldHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($record.Old))
                $record.OldExists = $true
            } elseif (Test-Path -LiteralPath $target) { throw 'A host runtime destination is not an ordinary file.' }
        }
        if ($null -ne $ExpectedVersion) { Assert-HostRuntimeVersion $Source $ExpectedVersion }
        return $plan
    } catch { Close-HostRuntimePlan $plan; throw }
}

function Close-HostRuntimePlan($Plan) {
    if ($null -eq $Plan) { return }
    foreach ($record in $Plan.Records) {
        if ($record.Old) { $record.Old.Dispose(); $record.Old = $null }
        if ($record.Source) { $record.Source.Dispose(); $record.Source = $null }
    }
}

function Stage-HostRuntimePlan($Plan) {
    $Plan.Stage = Assert-ContainedPath (Join-Path $Plan.Destination ('agentbroker-host-runtime-' + [guid]::NewGuid().ToString('N'))) $Plan.Destination
    $Plan.New = Assert-ContainedPath (Join-Path $Plan.Stage 'new') $Plan.Stage
    $Plan.Previous = Assert-ContainedPath (Join-Path $Plan.Stage 'previous') $Plan.Stage
    foreach ($path in @($Plan.Stage, $Plan.New, $Plan.Previous)) {
        Assert-NoReparseComponents $path
        if (Test-Path -LiteralPath $path) { throw 'A unique host runtime stage unexpectedly exists.' }
        [void][IO.Directory]::CreateDirectory($path)
    }
    foreach ($record in $Plan.Records) {
        $target = Assert-ContainedPath (Join-Path $Plan.New $record.Name) $Plan.New
        $output = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $record.Source.Position = 0; $record.Source.CopyTo($output, 65536); $output.Flush($true) } finally { $output.Dispose() }
        if ((Get-HostRuntimeFileSnapshot $target).Hash -cne $record.Hash) { throw 'A staged public host runtime copy failed verification.' }
    }
}

function Restore-HostRuntimePlan($Plan) {
    # Lane T round 8 (S-009, X1b T-r3 finding 2): the record whose replace or move was in flight when publication failed is
    # reconciled FIRST, by what exists on disk. A replace always passes its backup, so ReplaceFile's documented outcomes are:
    # target present and no backup = nothing landed (1175, 1176 with a backup, an error before the rename); target and backup
    # present = the replace landed (it joins Changed and is rolled back below, content-checked there); target missing with a
    # backup = a partial replacement (Win32 1177: the old file already renamed to the backup, the new one not placed), put back
    # from the backup only after the backup's digest matches the original, then verified at the target. A new file's move landed
    # when its target exists. Any other state throws, which Publish-HostRuntimePlan turns into RollbackIncomplete. Before round 8
    # the in-flight record was outside Changed and ignored, so a 1177 left a runtime file missing with recovery permitted.
    $inFlight = $Plan.InFlight
    if ($null -ne $inFlight) {
        $targetExists = [IO.File]::Exists($inFlight.Target)
        if ($inFlight.OldExists) {
            $inFlightBackup = Assert-ContainedPath (Join-Path $Plan.Previous $inFlight.Name) $Plan.Previous
            $backupExists = [IO.File]::Exists($inFlightBackup)
            if ($targetExists -and $backupExists) { $Plan.Changed.Add($inFlight) }
            elseif (-not $targetExists -and $backupExists) {
                if ((Get-HostRuntimeFileSnapshot $inFlightBackup).Hash -cne $inFlight.OldHash) {
                    throw 'An in-flight host runtime file is missing and its backup cannot be verified.'
                }
                # A rename keeps the verified bytes; the recovery gate re-verifies every planned file (Test-HostRuntimePlanSettled).
                [IO.File]::Move($inFlightBackup, $inFlight.Target, $false)
            } elseif (-not $targetExists) { throw 'An in-flight host runtime file and its backup are both missing.' }
        } elseif ($targetExists) { $Plan.Changed.Add($inFlight) }
        $Plan.InFlight = $null
    }
    for ($index = $Plan.Changed.Count - 1; $index -ge 0; $index--) {
        $record = $Plan.Changed[$index]
        $current = Get-HostRuntimeFileSnapshot $record.Target
        if ($current.Hash -cne $record.Hash) { throw 'A changed host runtime destination cannot be safely rolled back automatically.' }
        if ($record.OldExists) {
            $backup = Assert-ContainedPath (Join-Path $Plan.Previous $record.Name) $Plan.Previous
            if ((Get-HostRuntimeFileSnapshot $backup).Hash -cne $record.OldHash) { throw 'A runtime rollback copy failed verification.' }
            # Round 8 (2/2): the rollback's replace is retried like publication's, its backup and target re-read after each delay.
            Invoke-WithTransientRetry ('rollback of ' + $record.Name) {
                param($attempt)
                [AgentBrokerLocalInstallNative]::PublishMarkerFile($backup, $record.Target, $true)
            } {
                if ((Get-HostRuntimeFileSnapshot $backup).Hash -cne $record.OldHash) { 'its rollback copy changed' }
                elseif ((Get-HostRuntimeFileSnapshot $record.Target).Hash -cne $record.Hash) { 'the published file changed' }
                else { $null }
            }
        } else { [IO.File]::Delete($record.Target) }
    }
    $Plan.Changed.Clear()
    $Plan.Applied = $false
}

function Test-TransientReplaceFailure($Failure) {
    $inner = $Failure
    while ($null -ne $inner -and $inner -isnot [IO.IOException] -and $null -ne $inner.InnerException) { $inner = $inner.InnerException }
    # Win32 1175 (ERROR_UNABLE_TO_REMOVE_REPLACED) and 32 (ERROR_SHARING_VIOLATION): both leave every file where it was.
    return $inner -is [IO.IOException] -and ($inner.HResult -eq -2147023721 -or $inner.HResult -eq -2147024864)
}

# Round 8 (2/2): runs Operation (given the attempt number) until it succeeds, a failure is not transient, or ReplaceAttempts
# attempts failed. Before every REPEATED attempt, so AFTER each delay, Guard re-reads the starting state and returns $null or why
# it changed (X1b T-r3 finding 1: the parked design checked before the delay, so a change made while waiting went unseen); a
# refused retry throws an IOException naming the change, with the first transient failure as its inner exception.
function Invoke-WithTransientRetry([string]$Name, [scriptblock]$Operation, [scriptblock]$Guard) {
    $first = $null
    for ($attempt = 1; ; $attempt++) {
        if ($attempt -gt 1) {
            $refusal = & $Guard
            if ($null -ne $refusal) { throw [IO.IOException]::new("$Name was not retried: $refusal.", $first) }
        }
        try { & $Operation $attempt; return }
        catch {
            $failure = $_.Exception
            if ($attempt -ge $script:ReplaceAttempts -or -not (Test-TransientReplaceFailure $failure)) { throw }
            if ($null -eq $first) { $first = $failure }
            if ($null -ne $script:ReplaceRetryForSelfTest) { & $script:ReplaceRetryForSelfTest $Name $attempt $failure }
            Start-Sleep -Milliseconds $script:ReplaceRetryDelayMs
            if ($null -ne $script:ReplaceRetryWaitForSelfTest) { & $script:ReplaceRetryWaitForSelfTest $Name $attempt }
        }
    }
}

# Round 8 (2/2): the starting state a repeated host runtime operation needs, read again after the delay: the staged source by
# digest, no backup yet, and the installed file by file identity AND digest (a by-name swap for identical bytes, and a same-length
# rewrite that keeps its timestamp, are each refused; X1b T-r3 findings 1 and 3). A move needs its destination still absent.
function Get-HostRuntimeRetryRefusal($Record, [string]$Source, $Backup) {
    try {
        if ((Get-HostRuntimeFileSnapshot $Source).Hash -cne $Record.Hash) { return 'its staged source changed' }
        if ($Record.OldExists) {
            if (Test-Path -LiteralPath $Backup) { return 'its backup appeared' }
            $current = Get-HostRuntimeFileSnapshot $Record.Target
            if ($current.Stamp -cne $Record.OldStamp) { return 'the installed file was replaced by another file' }
            if ($current.Hash -cne $Record.OldHash) { return 'the installed file changed' }
        } elseif (Test-Path -LiteralPath $Record.Target) { return 'its destination appeared' }
        return $null
    } catch { return 'its starting state could not be read (' + $_.Exception.Message + ')' }
}

function Invoke-HostRuntimeOperation($Record, [string]$Source, $Backup) {
    # The single replace-or-move of one host runtime file, under the bounded retry (S-010).
    Invoke-WithTransientRetry $Record.Name {
        param($attempt)
        if ($null -ne $script:HostRuntimeOperationForSelfTest) {
            $fault = & $script:HostRuntimeOperationForSelfTest $Record $Source $Backup $attempt
            if ($null -ne $fault) { throw $fault }
        }
        if ($Record.OldExists) { [IO.File]::Replace($Source, $Record.Target, $Backup) }
        else { [IO.File]::Move($Source, $Record.Target, $false) }
    } { Get-HostRuntimeRetryRefusal $Record $Source $Backup }
}

# Round 8: true only when every planned record's installed file is verified on disk in ONE consistent state: the new runtime
# after a verified publication, or the original runtime after a rollback (each original file by its digest, each added file
# absent). A plan with an unreconciled in-flight record or an incomplete rollback is never settled. Any read failure is false.
function Test-HostRuntimePlanSettled($Plan) {
    if ($null -eq $Plan) { return $true }
    if ($Plan.RollbackIncomplete -or $null -ne $Plan.InFlight) { return $false }
    try {
        foreach ($record in $Plan.Records) {
            if ($Plan.Applied) {
                if ((Get-HostRuntimeFileSnapshot $record.Target).Hash -cne $record.Hash) { return $false }
            } elseif ($record.OldExists) {
                if ((Get-HostRuntimeFileSnapshot $record.Target).Hash -cne $record.OldHash) { return $false }
            } elseif (Test-Path -LiteralPath $record.Target) { return $false }
        }
        return $true
    } catch { return $false }
}

# Round 8: the companion is restarted after a failed run only when it is safe to launch, and with a host runtime plan only
# when that plan is settled (Test-HostRuntimePlanSettled), not merely when no rollback reported itself incomplete.
function Test-RecoveryLaunchPermitted([bool]$ReadyCompleted, [bool]$CompanionStopped, [bool]$RuntimeLaunchSafe, $RuntimePlan, $Relaunched) {
    return -not $ReadyCompleted -and $CompanionStopped -and $RuntimeLaunchSafe -and (Test-HostRuntimePlanSettled $RuntimePlan) -and
        ($null -eq $Relaunched -or $Relaunched.HasExited)
}

function Publish-HostRuntimePlan($Plan, [int]$FailAfter = -1) {
    # Called only after the retained companion and its exact owned worker have exited.
    foreach ($record in $Plan.Records) { if ($record.Old) { $record.Old.Dispose(); $record.Old = $null } }
    try {
        foreach ($record in $Plan.Records) {
            if ($record.OldExists) {
                $old = Get-HostRuntimeFileSnapshot $record.Target
                if ($old.Stamp -cne $record.OldStamp -or $old.Hash -cne $record.OldHash) { throw 'An installed runtime file changed during staging.' }
            } elseif (Test-Path -LiteralPath $record.Target) { throw 'A host runtime destination appeared during staging.' }
            $source = Assert-ContainedPath (Join-Path $Plan.New $record.Name) $Plan.New
            Assert-NoReparseComponents $source
            if ((Get-HostRuntimeFileSnapshot $source).Hash -cne $record.Hash) { throw 'A staged host runtime file changed before publication.' }
            $backup = $null
            if ($record.OldExists) {
                $backup = Assert-ContainedPath (Join-Path $Plan.Previous $record.Name) $Plan.Previous
                Assert-NoReparseComponents $backup
                if (Test-Path -LiteralPath $backup) { throw 'A unique host runtime rollback destination unexpectedly exists.' }
            }
            # Round 8: in flight from here until it is in Changed, so a failure inside the operation is reconciled by Restore.
            $Plan.InFlight = $record
            Invoke-HostRuntimeOperation $record $source $backup
            $Plan.Changed.Add($record)
            $Plan.InFlight = $null
            if ($FailAfter -ge 0 -and $Plan.Changed.Count -eq $FailAfter) { throw 'Synthetic host runtime publication failure.' }
        }
        foreach ($record in $Plan.Records) {
            if ((Get-HostRuntimeFileSnapshot $record.Target).Hash -cne $record.Hash) { throw 'Published host runtime verification failed.' }
        }
        if ($null -ne $Plan.ExpectedVersion) { Assert-HostRuntimeVersion $Plan.Destination $Plan.ExpectedVersion }
        $Plan.Applied = $true
    } catch {
        try { Restore-HostRuntimePlan $Plan }
        catch { $Plan.RollbackIncomplete = $true; throw 'Host runtime publication failed and automatic rollback is incomplete; retained backups require review before launch.' }
        throw
    }
}

function Assert-PreviousCopyMetadata([string]$Path, [string]$Root) {
    [void](Assert-ContainedPath $Path $Root)
    Assert-NoReparseComponents $Path
    if (-not [IO.Directory]::Exists($Path)) { throw 'The previous AgentBroker copy is not an ordinary directory.' }
    $directories = [Collections.Generic.Queue[string]]::new(); $directories.Enqueue($Path)
    $files = [Collections.Generic.List[string]]::new(); $count = 0
    while ($directories.Count -gt 0) {
        $directory = $directories.Dequeue()
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            [void](Assert-ContainedPath $entry $Path)
            if (Get-ProtectedEntryPattern $entry) { throw 'The retained previous copy contains a protected path; it was not moved.' }
            Assert-NoReparseComponents $entry
            if (++$count -gt 8192) { throw 'The previous-copy metadata exceeded its entry bound.' }
            if ([IO.Directory]::Exists($entry)) { $directories.Enqueue($entry) }
            elseif ([IO.File]::Exists($entry)) { $files.Add($entry) }
            else { throw 'The previous copy contains an unsupported filesystem entry.' }
        }
    }
    # All names are accepted before opening handles for link-count metadata. No contents read.
    foreach ($file in $files) {
        $stream = [IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try { [AgentBrokerLocalInstallNative]::VerifySingleLink($stream) } finally { $stream.Dispose() }
    }
}

function Archive-PreviousModuleCopy([string]$Path, [string]$Root) {
    Assert-PreviousCopyMetadata $Path $Root
    $parent = [IO.Path]::GetDirectoryName($Path)
    $destination = Assert-ContainedPath (Join-Path $parent ('agentbroker.previous-' + [guid]::NewGuid().ToString('N'))) $Root
    Assert-NoReparseComponents $destination
    if (Test-Path -LiteralPath $destination) { throw 'A unique retained previous-copy destination unexpectedly exists.' }
    [IO.Directory]::Move($Path, $destination)
    return $destination
}

function Test-HostRuntimeTransaction([string]$Parent) {
    $root = Assert-ContainedPath (Join-Path $Parent 'host-runtime-transaction') $Parent
    $source = Assert-ContainedPath (Join-Path $root 'source') $root
    $destination = Assert-ContainedPath (Join-Path $root 'installed') $root
    $previous = Assert-ContainedPath (Join-Path $root 'agentbroker.replaced') $root
    $archived = $null
    $plans = [Collections.Generic.List[object]]::new()
    $names = [string[]]@('existing.bin', 'added.bin', 'last.bin')
    $checks = 0
    $transactionFailed = $false
    foreach ($folder in @($root, $source, $destination, $previous)) {
        Assert-NoReparseComponents $folder
        if (Test-Path -LiteralPath $folder) { throw 'Unique runtime transaction test already exists.' }
        [void][IO.Directory]::CreateDirectory($folder)
    }
    try {
        foreach ($name in $names) { [IO.File]::WriteAllText((Join-Path $source $name), ('new-' + $name)) }
        foreach ($name in @('existing.bin', 'last.bin')) { [IO.File]::WriteAllText((Join-Path $destination $name), ('old-' + $name)) }
        [IO.File]::WriteAllText((Join-Path $destination 'settings.json'), '{"synthetic":"preserved"}')
        $plan = New-HostRuntimePlan $source $destination $names $null; $plans.Add($plan)
        $refused = $false
        try { [IO.File]::WriteAllText((Join-Path $source 'existing.bin'), 'changed') } catch { $refused = $true }
        if (-not $refused) { throw 'Retained public runtime input allowed mutation before publication.' }
        $checks++
        $refused = $false
        try { [IO.File]::WriteAllText((Join-Path $destination 'existing.bin'), 'changed') } catch { $refused = $true }
        if (-not $refused) { throw 'Retained installed runtime input allowed mutation before publication.' }
        $checks++
        Stage-HostRuntimePlan $plan
        if ([IO.File]::ReadAllText((Join-Path $destination 'existing.bin')) -cne 'old-existing.bin' -or
            [IO.File]::Exists((Join-Path $destination 'added.bin'))) { throw 'Staging changed the installed host before shutdown.' }
        $checks++
        $failed = $false
        try { Publish-HostRuntimePlan $plan -FailAfter 2 } catch { $failed = $true }
        if (-not $failed -or $plan.RollbackIncomplete -or $plan.Applied -or $plan.Changed.Count -ne 0 -or
            [IO.File]::ReadAllText((Join-Path $destination 'existing.bin')) -cne 'old-existing.bin' -or
            [IO.File]::ReadAllText((Join-Path $destination 'last.bin')) -cne 'old-last.bin' -or
            [IO.File]::Exists((Join-Path $destination 'added.bin'))) { throw 'Partial runtime publication was not fully rolled back.' }
        $checks++
        if ([IO.File]::ReadAllText((Join-Path $destination 'settings.json')) -cne '{"synthetic":"preserved"}') {
            throw 'Runtime rollback changed unrelated user settings.'
        }
        $checks++
        Close-HostRuntimePlan $plan

        $refused = $false
        try { Assert-EffectiveHostVersion ([version]'1.6.0.0') ([version]'1.6.1') } catch { $refused = $true }
        if (-not $refused) { throw 'An installed host below the module ABI floor was accepted.' }
        $checks++
        Assert-EffectiveHostVersion ([version]'1.6.1.0') ([version]'1.6.1')
        Assert-EffectiveHostVersion ([version]'1.6.2.0') ([version]'1.6.1')
        $checks++
        $plan = New-HostRuntimePlan $source $destination $names $null; $plans.Add($plan)
        Stage-HostRuntimePlan $plan
        Publish-HostRuntimePlan $plan
        foreach ($name in $names) {
            if ([IO.File]::ReadAllText((Join-Path $destination $name)) -cne ('new-' + $name)) { throw 'Runtime publication omitted a declared public file.' }
        }
        if (-not $plan.Applied -or [IO.File]::ReadAllText((Join-Path $plan.Previous 'existing.bin')) -cne 'old-existing.bin') {
            throw 'Successful runtime publication lost its retained previous copy.'
        }
        $checks++
        Restore-HostRuntimePlan $plan
        if ([IO.File]::ReadAllText((Join-Path $destination 'existing.bin')) -cne 'old-existing.bin' -or
            [IO.File]::Exists((Join-Path $destination 'added.bin'))) { throw 'Explicit runtime rollback did not restore the original file set.' }
        $checks++
        Close-HostRuntimePlan $plan

        # Lane T round 8 (S-009, X1b T-r3 finding 2): the record in flight when publication fails is reconciled by what is on
        # disk, and recovery waits for every planned file to verify. Each fixture injects its failure through the operation seam.
        $readDestination = { param($name) $path = Join-Path $destination $name; if ([IO.File]::Exists($path)) { [IO.File]::ReadAllText($path) } else { '<missing>' } }
        $allOld = { (& $readDestination 'existing.bin') -ceq 'old-existing.bin' -and (& $readDestination 'last.bin') -ceq 'old-last.bin' -and
            (& $readDestination 'added.bin') -ceq '<missing>' }
        $plan = New-HostRuntimePlan $source $destination $names $null; $plans.Add($plan)
        Stage-HostRuntimePlan $plan
        $script:HostRuntimeOperationForSelfTest = {
            param($record, $staged, $backup)
            # Win32 1177 as documented for ReplaceFile: the replaced file already renamed to the backup, the replacement not placed.
            if ($record.Name -ceq 'existing.bin') {
                [IO.File]::Move($record.Target, $backup, $false)
                return [IO.IOException]::new('synthetic ERROR_UNABLE_TO_MOVE_REPLACEMENT_2 (1177)', -2147023719)
            }
            return $null
        }
        $raised = $null
        try { Publish-HostRuntimePlan $plan } catch { $raised = $_.Exception } finally { $script:HostRuntimeOperationForSelfTest = $null }
        if ($null -eq $raised -or $plan.RollbackIncomplete -or $plan.Applied -or $null -ne $plan.InFlight -or $plan.Changed.Count -ne 0 -or
            -not (& $allOld) -or -not (Test-HostRuntimePlanSettled $plan)) {
            throw "INSTALL_ROLLBACK_RECONCILES_PARTIAL_REPLACE: a 1177 partial replacement is put back from its verified backup and the plan settles (raised $($null -ne $raised), rollback incomplete $($plan.RollbackIncomplete), existing.bin $(& $readDestination 'existing.bin'))."
        }
        $checks++
        Close-HostRuntimePlan $plan

        $landedWrong = @()
        foreach ($landed in @('existing.bin', 'added.bin')) {
            $plan = New-HostRuntimePlan $source $destination $names $null; $plans.Add($plan)
            Stage-HostRuntimePlan $plan
            $landedName = $landed
            $script:HostRuntimeOperationForSelfTest = {
                param($record, $staged, $backup)
                # The operation lands, then its failure is reported (as an I/O error surfacing after the rename would be).
                if ($record.Name -ceq $landedName) {
                    if ($record.OldExists) { [IO.File]::Replace($staged, $record.Target, $backup) } else { [IO.File]::Move($staged, $record.Target, $false) }
                    return [IO.IOException]::new('synthetic failure reported after the operation landed', -2147024891)
                }
                return $null
            }.GetNewClosure()
            $raised = $null
            try { Publish-HostRuntimePlan $plan } catch { $raised = $_.Exception } finally { $script:HostRuntimeOperationForSelfTest = $null }
            if ($null -eq $raised -or $plan.RollbackIncomplete -or $plan.Applied -or $null -ne $plan.InFlight -or -not (& $allOld) -or
                -not (Test-HostRuntimePlanSettled $plan)) {
                $landedWrong += "$landed (rollback incomplete $($plan.RollbackIncomplete), existing.bin $(& $readDestination 'existing.bin'), added.bin $(& $readDestination 'added.bin'))"
            }
            Close-HostRuntimePlan $plan
        }
        if ($landedWrong.Count -ne 0) {
            throw "INSTALL_ROLLBACK_RECONCILES_LANDED_OPERATION: a replace or move that landed before its failure was reported is rolled back (wrong: $($landedWrong -join '; '))."
        }
        $checks++

        # Recovery needs a settled plan, not merely one whose rollback did not report itself incomplete: the state 88f3a9b2 left
        # behind (target renamed to its backup, nothing in Changed, RollbackIncomplete false) must not permit a launch.
        $plan = New-HostRuntimePlan $source $destination $names $null; $plans.Add($plan)
        Stage-HostRuntimePlan $plan
        $settledBefore = Test-RecoveryLaunchPermitted $false $true $true $plan $null
        foreach ($record in $plan.Records) { if ($record.Old) { $record.Old.Dispose(); $record.Old = $null } }
        $unsettledBackup = Join-Path $plan.Previous 'existing.bin'
        [IO.File]::Move((Join-Path $destination 'existing.bin'), $unsettledBackup, $false)
        $unsettled = Test-RecoveryLaunchPermitted $false $true $true $plan $null
        [IO.File]::Move($unsettledBackup, (Join-Path $destination 'existing.bin'), $false)
        $plan.RollbackIncomplete = $true
        $incomplete = Test-RecoveryLaunchPermitted $false $true $true $plan $null
        if (-not $settledBefore -or $unsettled -or $incomplete -or -not (Test-RecoveryLaunchPermitted $false $true $true $null $null) -or
            (Test-RecoveryLaunchPermitted $true $true $true $null $null)) {
            throw "INSTALL_RECOVERY_REQUIRES_SETTLED_RUNTIME: recovery launches only when every planned runtime file verifies (settled $settledBefore, missing file $unsettled, rollback incomplete $incomplete)."
        }
        $checks++
        Close-HostRuntimePlan $plan

        # The launch decision in the installer's own finally block is exactly that function: the CONDITION of the if that starts
        # the recovery companion, read from this script's syntax tree (not the mere presence of a call somewhere).
        $installerAst = [Management.Automation.Language.Parser]::ParseFile($PSCommandPath, [ref]$null, [ref]$null)
        $launches = @($installerAst.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and
            $node.Extent.Text -ceq "Write-Host 'CompanionRecoveryStarted=True'" }, $true))
        $gate = "$($launches.Count) recovery launch reports"
        if ($launches.Count -eq 1) {
            # The nearest enclosing if, and the clause whose body holds the launch: that clause's condition is the gate.
            $node = $launches[0].Parent
            while ($null -ne $node -and $node -isnot [Management.Automation.Language.IfStatementAst]) { $node = $node.Parent }
            $gate = 'no enclosing if'
            if ($null -ne $node) {
                foreach ($clause in $node.Clauses) {
                    if ($clause.Item2.Extent.StartOffset -le $launches[0].Extent.StartOffset -and $launches[0].Extent.EndOffset -le $clause.Item2.Extent.EndOffset) {
                        $gate = ($clause.Item1.Extent.Text -replace '\s+', ' ').Trim()
                    }
                }
            }
        }
        if ($gate -cne 'Test-RecoveryLaunchPermitted $readyCompleted $companionStopped $runtimeLaunchSafe $runtimePlan $relaunched') {
            throw "INSTALL_RECOVERY_GATE_WIRED: the one recovery launch is conditioned on Test-RecoveryLaunchPermitted with the run's own state (found: $gate)."
        }
        $checks++

        # A partial replacement whose backup cannot be verified, or whose backup is missing too, is NOT put back: the rollback
        # reports itself incomplete, the unverifiable backup stays where it is, and recovery is refused.
        $incompleteWrong = @()
        foreach ($damage in @('tampered', 'missing')) {
            $plan = New-HostRuntimePlan $source $destination $names $null; $plans.Add($plan)
            Stage-HostRuntimePlan $plan
            $damageKind = $damage
            $aside = Join-Path $root ('aside-' + [guid]::NewGuid().ToString('N') + '.bin')
            $script:HostRuntimeOperationForSelfTest = {
                param($record, $staged, $backup)
                if ($record.Name -ceq 'existing.bin') {
                    if ($damageKind -ceq 'tampered') { [IO.File]::Move($record.Target, $backup, $false); [IO.File]::WriteAllText($backup, 'tampered-backup') }
                    else { [IO.File]::Move($record.Target, $aside, $false) }
                    return [IO.IOException]::new('synthetic ERROR_UNABLE_TO_MOVE_REPLACEMENT_2 (1177)', -2147023719)
                }
                return $null
            }.GetNewClosure()
            $raised = $null
            try { Publish-HostRuntimePlan $plan } catch { $raised = $_.Exception } finally { $script:HostRuntimeOperationForSelfTest = $null }
            $damagedBackup = Join-Path $plan.Previous 'existing.bin'
            $kept = if ($damage -ceq 'tampered') { [IO.File]::Exists($damagedBackup) -and [IO.File]::ReadAllText($damagedBackup) -ceq 'tampered-backup' } else { $true }
            if ($null -eq $raised -or -not $plan.RollbackIncomplete -or $plan.Applied -or (Test-HostRuntimePlanSettled $plan) -or -not $kept -or
                (& $readDestination 'existing.bin') -cne '<missing>' -or (Test-RecoveryLaunchPermitted $false $true $true $plan $null)) {
                $incompleteWrong += "$damage (rollback incomplete $($plan.RollbackIncomplete), backup kept $kept, existing.bin $(& $readDestination 'existing.bin'))"
            }
            # Put the fixture back for the checks that follow.
            if ([IO.File]::Exists($damagedBackup)) { [IO.File]::Delete($damagedBackup) }
            if ([IO.File]::Exists($aside)) { [IO.File]::Delete($aside) }
            if (-not [IO.File]::Exists((Join-Path $destination 'existing.bin'))) { [IO.File]::WriteAllText((Join-Path $destination 'existing.bin'), 'old-existing.bin') }
            Close-HostRuntimePlan $plan
        }
        if ($incompleteWrong.Count -ne 0) {
            throw "INSTALL_ROLLBACK_UNVERIFIABLE_BACKUP_INCOMPLETE: a partial replacement whose backup is tampered with or missing reports an incomplete rollback, keeps the backup and refuses recovery (wrong: $($incompleteWrong -join '; '))."
        }
        $checks++

        # Lane T round 8 (2/2, S-010, DECISIONS 118): the bounded retry. A hold is a real FileStream sharing read but not delete,
        # which ReplaceFile and a rename need (measured: sharing violation 0x80070020); the retry seam releases it when it OBSERVES
        # the retry, so no fixture depends on timing (X1b T-r3 finding 4). The watchdog releases the bound fixture's hold if a
        # retry ever runs past the bound, so a broken bound fails instead of hanging.
        $script:RetryWitness = @{}; $script:ReleaseOnRetry = @{}; $script:WatchdogHold = $null; $script:DuringDelayWrite = $null
        $script:ReplaceRetryForSelfTest = {
            param($name, $attempt, $failure)
            $script:RetryWitness[$name] = 1 + [int]$script:RetryWitness[$name]
            if ($script:ReleaseOnRetry.ContainsKey($name)) { $script:ReleaseOnRetry[$name].Dispose(); [void]$script:ReleaseOnRetry.Remove($name) }
            if ($null -ne $script:DuringDelayWrite) { & $script:DuringDelayWrite; $script:DuringDelayWrite = $null }
            if ($attempt -ge $script:ReplaceAttempts -and $null -ne $script:WatchdogHold) { $script:WatchdogHold.Dispose(); $script:WatchdogHold = $null }
        }
        $hold = { param($path) [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read) }
        $retries = { param($name) [int]$script:RetryWitness[$name] }
        $allNew = { (& $readDestination 'existing.bin') -ceq 'new-existing.bin' -and (& $readDestination 'added.bin') -ceq 'new-added.bin' -and
            (& $readDestination 'last.bin') -ceq 'new-last.bin' }
        $resetDestination = {
            if ((& $readDestination 'existing.bin') -cne 'old-existing.bin') {
                if ([IO.File]::Exists((Join-Path $destination 'existing.bin'))) { [IO.File]::Delete((Join-Path $destination 'existing.bin')) }
                [IO.File]::WriteAllText((Join-Path $destination 'existing.bin'), 'old-existing.bin')
            }
        }
        $newPlan = { $next = New-HostRuntimePlan $source $destination $names $null; $plans.Add($next); Stage-HostRuntimePlan $next; $script:RetryWitness = @{}; $next }
        try {
            # A destination held for one delay is replaced after one retry.
            $plan = & $newPlan
            $script:ReleaseOnRetry['existing.bin'] = & $hold (Join-Path $destination 'existing.bin')
            $raised = $null
            try { Publish-HostRuntimePlan $plan } catch { $raised = $_.Exception }
            if ($null -ne $raised -or -not $plan.Applied -or (& $retries 'existing.bin') -ne 1 -or -not (& $allNew)) {
                throw "INSTALL_REPLACE_RETRIES_TRANSIENT_LOCK: a destination held for one delay is replaced after exactly one retry (raised $(if ($raised) { $raised.Message } else { 'none' }), retries $(& $retries 'existing.bin'), applied $($plan.Applied))."
            }
            Restore-HostRuntimePlan $plan
            if (-not (& $allOld)) { throw 'The retry fixture could not restore its original runtime files.' }
            $checks++
            Close-HostRuntimePlan $plan

            # A new file's staged source held for one delay is moved after one retry.
            $plan = & $newPlan
            $script:ReleaseOnRetry['added.bin'] = & $hold (Join-Path $plan.New 'added.bin')
            $raised = $null
            try { Publish-HostRuntimePlan $plan } catch { $raised = $_.Exception }
            if ($null -ne $raised -or -not $plan.Applied -or (& $retries 'added.bin') -ne 1 -or -not (& $allNew)) {
                throw "INSTALL_MOVE_RETRIES_TRANSIENT_LOCK: a new file's staged source held for one delay is moved after exactly one retry (raised $(if ($raised) { $raised.Message } else { 'none' }), retries $(& $retries 'added.bin'))."
            }
            Restore-HostRuntimePlan $plan
            if (-not (& $allOld)) { throw 'The retry fixture could not restore its original runtime files.' }
            $checks++
            Close-HostRuntimePlan $plan

            # The rollback's own replace: existing.bin, already published, is held when the rollback reaches it.
            $plan = & $newPlan
            $script:HostRuntimeOperationForSelfTest = {
                param($record, $staged, $backup, $attempt)
                # Not a closure: $script: must stay this installer's scope (the hold table the retry seam reads), and $hold and
                # $destination resolve through the call stack.
                if ($record.Name -ceq 'added.bin' -and -not $script:ReleaseOnRetry.ContainsKey('rollback of existing.bin')) {
                    $script:ReleaseOnRetry['rollback of existing.bin'] = & $hold (Join-Path $destination 'existing.bin')
                }
                return $null
            }
            $raised = $null
            try { Publish-HostRuntimePlan $plan -FailAfter 2 } catch { $raised = $_.Exception } finally { $script:HostRuntimeOperationForSelfTest = $null }
            if ($null -eq $raised -or $plan.RollbackIncomplete -or $plan.Applied -or (& $retries 'rollback of existing.bin') -ne 1 -or -not (& $allOld)) {
                throw "INSTALL_ROLLBACK_REPLACE_RETRIES_TRANSIENT_LOCK: a rollback whose destination is held for one delay completes after exactly one retry (rollback incomplete $($plan.RollbackIncomplete), retries $(& $retries 'rollback of existing.bin'), existing.bin $(& $readDestination 'existing.bin'))."
            }
            $checks++
            Close-HostRuntimePlan $plan

            # The update marker held for one delay is replaced after one retry, keeping its existing ID.
            $retryMarker = Assert-ContainedPath (Join-Path $root 'retry-update-marker.txt') $root
            [IO.File]::WriteAllText($retryMarker, "alpha`n", [Text.UTF8Encoding]::new($false))
            $markerSnapshot = Read-UpdateMarker $retryMarker
            $script:RetryWitness = @{}
            $script:ReleaseOnRetry['the update marker'] = & $hold $retryMarker
            $raised = $null
            try { Publish-UpdateMarker $retryMarker $markerSnapshot } catch { $raised = $_.Exception }
            if ($null -ne $raised -or (& $retries 'the update marker') -ne 1 -or [IO.File]::ReadAllText($retryMarker) -cne "alpha`nagentbroker`n") {
                throw "INSTALL_MARKER_REPLACE_RETRIES_TRANSIENT_LOCK: an update marker held for one delay is replaced after exactly one retry and keeps its IDs (raised $(if ($raised) { $raised.Message } else { 'none' }), retries $(& $retries 'the update marker'))."
            }
            $checks++

            # Only the two transient errors are retried: an injected 1175 once is retried; another I/O error never is.
            $plan = & $newPlan
            $script:HostRuntimeOperationForSelfTest = {
                param($record, $staged, $backup, $attempt)
                if ($record.Name -ceq 'existing.bin' -and $attempt -eq 1) { return [IO.IOException]::new('synthetic ERROR_UNABLE_TO_REMOVE_REPLACED (1175)', -2147023721) }
                return $null
            }
            $raised = $null
            try { Publish-HostRuntimePlan $plan } catch { $raised = $_.Exception } finally { $script:HostRuntimeOperationForSelfTest = $null }
            $transientRetried = $null -eq $raised -and $plan.Applied -and (& $retries 'existing.bin') -eq 1 -and (& $allNew)
            if ($plan.Applied) { Restore-HostRuntimePlan $plan }
            Close-HostRuntimePlan $plan
            & $resetDestination
            $plan = & $newPlan
            $script:HostRuntimeOperationForSelfTest = {
                param($record, $staged, $backup, $attempt)
                if ($record.Name -ceq 'existing.bin') { return [IO.IOException]::new('synthetic non-transient I/O failure', -2147024891) }
                return $null
            }
            $raised = $null
            try { Publish-HostRuntimePlan $plan } catch { $raised = $_.Exception } finally { $script:HostRuntimeOperationForSelfTest = $null }
            $otherNotRetried = $null -ne $raised -and -not $plan.Applied -and -not $plan.RollbackIncomplete -and (& $retries 'existing.bin') -eq 0 -and (& $allOld)
            if (-not $transientRetried -or -not $otherNotRetried) {
                throw "INSTALL_REPLACE_RETRY_ONLY_TRANSIENT_ERRORS: Win32 1175 is retried and a non-transient I/O failure is not (1175 retried $transientRetried, other not retried $otherNotRetried)."
            }
            $checks++
            Close-HostRuntimePlan $plan

            # A destination held past the bound: five attempts, four retries, its own sharing violation, and a rollback that is
            # consistent (nothing landed for the held file) and settled once the hold ends.
            $plan = & $newPlan
            $script:WatchdogHold = & $hold (Join-Path $destination 'existing.bin')
            $raised = $null
            try { Publish-HostRuntimePlan $plan } catch { $raised = $_.Exception }
            if ($null -ne $script:WatchdogHold) { $script:WatchdogHold.Dispose(); $script:WatchdogHold = $null }
            $inner = $raised; while ($null -ne $inner -and $inner -isnot [IO.IOException] -and $null -ne $inner.InnerException) { $inner = $inner.InnerException }
            if ($null -eq $raised -or $null -eq $inner -or $inner.HResult -ne -2147024864 -or (& $retries 'existing.bin') -ne 4 -or $plan.Applied -or
                $plan.RollbackIncomplete -or -not (& $allOld) -or -not (Test-HostRuntimePlanSettled $plan) -or -not (Test-RecoveryLaunchPermitted $false $true $true $plan $null)) {
                throw "INSTALL_REPLACE_GIVES_UP_AFTER_BOUND: a destination held past the bound fails with its own sharing violation after 5 attempts and leaves a settled rollback (raised $(if ($inner) { $inner.HResult } else { 'none' }), retries $(& $retries 'existing.bin'), applied $($plan.Applied), rollback incomplete $($plan.RollbackIncomplete))."
            }
            $checks++
            Close-HostRuntimePlan $plan

            # The starting state is re-read AFTER each delay, by digest and file identity, for the source and the backup too: each
            # change made after the first transient failure refuses the retry and is kept. The 'during-delay' case writes in the first
            # seam, BEFORE the sleep (its name predates round 8 3/3); a write inside the wait itself is the order check below.
            $revalidateWrong = @()
            foreach ($change in @('content', 'same-length-same-time', 'identity', 'source', 'backup', 'during-delay')) {
                $plan = & $newPlan
                $changeKind = $change
                $installedPath = Join-Path $destination 'existing.bin'
                $stagedPath = Join-Path $plan.New 'existing.bin'
                $backupPath = Join-Path $plan.Previous 'existing.bin'
                if ($change -ceq 'during-delay') { $script:DuringDelayWrite = { [IO.File]::WriteAllText($installedPath, 'written during the delay') }.GetNewClosure() }
                # Witness: a refused retry never reaches the operation again (a [ref], because the closure has its own scope).
                $reachedAgain = [ref]$false
                $script:HostRuntimeOperationForSelfTest = {
                    param($record, $staged, $backup, $attempt)
                    if ($record.Name -ceq 'existing.bin' -and $attempt -ge 2) { $reachedAgain.Value = $true }
                    if ($record.Name -cne 'existing.bin' -or $attempt -ne 1) { return $null }
                    switch ($changeKind) {
                        'content' { [IO.File]::WriteAllText($installedPath, 'a concurrent writer rewrote this') }
                        'same-length-same-time' {
                            $time = [IO.File]::GetLastWriteTimeUtc($installedPath)
                            [IO.File]::WriteAllText($installedPath, 'OLD-existing.bin'); [IO.File]::SetLastWriteTimeUtc($installedPath, $time)
                        }
                        'identity' { [IO.File]::Delete($installedPath); [IO.File]::WriteAllText($installedPath, 'old-existing.bin') }
                        'source' { [IO.File]::WriteAllText($stagedPath, 'a changed staged source') }
                        'backup' { [IO.File]::WriteAllText($backupPath, 'a planted backup') }
                    }
                    return [IO.IOException]::new('synthetic sharing violation', -2147024864)
                }.GetNewClosure()
                $raised = $null
                try { Publish-HostRuntimePlan $plan } catch { $raised = $_.Exception } finally { $script:HostRuntimeOperationForSelfTest = $null; $script:DuringDelayWrite = $null }
                $why = @{ 'content' = 'the installed file changed'; 'same-length-same-time' = 'the installed file changed'
                    'identity' = 'the installed file was replaced by another file'; 'source' = 'its staged source changed'
                    # A planted backup beside an unchanged target cannot be told from a landed replace, so the rollback reports
                    # itself incomplete and keeps the planted file; the witness below shows the retry itself was refused.
                    'backup' = 'automatic rollback is incomplete'; 'during-delay' = 'the installed file changed' }[$change]
                $kept = switch ($change) {
                    'content' { (& $readDestination 'existing.bin') -ceq 'a concurrent writer rewrote this' }
                    'same-length-same-time' { (& $readDestination 'existing.bin') -ceq 'OLD-existing.bin' }
                    'during-delay' { (& $readDestination 'existing.bin') -ceq 'written during the delay' }
                    'identity' { (& $readDestination 'existing.bin') -ceq 'old-existing.bin' }
                    'source' { (& $readDestination 'existing.bin') -ceq 'old-existing.bin' }
                    'backup' { [IO.File]::Exists($backupPath) -and [IO.File]::ReadAllText($backupPath) -ceq 'a planted backup' -and $plan.RollbackIncomplete }
                }
                $message = if ($null -ne $raised) { $raised.Message } else { '' }
                if ($null -eq $raised -or -not $message.Contains($why) -or (& $retries 'existing.bin') -ne 1 -or $plan.Applied -or -not $kept -or
                    $reachedAgain.Value) {
                    $revalidateWrong += "$change (raised '$message', retries $(& $retries 'existing.bin'), operation reached again $($reachedAgain.Value), existing.bin $(& $readDestination 'existing.bin'))"
                }
                Close-HostRuntimePlan $plan
                if ([IO.File]::Exists($backupPath)) { [IO.File]::Delete($backupPath) }
                & $resetDestination
            }
            if ($revalidateWrong.Count -ne 0) {
                throw "INSTALL_REPLACE_RETRY_REVALIDATES_AFTER_DELAY: a retry re-reads the source, backup and destination (digest and identity) after the delay and keeps any change (wrong: $($revalidateWrong -join '; '))."
            }
            $checks++

            # Lane T round 8 (3/3, X3's T8-X3-1 and T8-X3-2, DECISIONS 128): writes that land INSIDE the wait. The wait seam runs
            # after the sleep and before the next attempt's guard, and makes each write below once, keyed by the retry's name; the
            # WaitWritten witness proves it ran, so a seam that never fires fails here instead of passing. These checks follow
            # REVALIDATES_AFTER_DELAY on purpose: that check owns the shared guard (skipped, or moved before the first seam), and
            # this self-test stops at its first failure, so each check below owns exactly one narrower defect.
            $script:DuringWaitWrite = @{}; $script:WaitWritten = @{}
            $script:ReplaceRetryWaitForSelfTest = {
                param($name, $attempt)
                # Not a closure: $script: must stay this installer's scope (the tables above).
                if ($script:DuringWaitWrite.ContainsKey($name)) {
                    $write = $script:DuringWaitWrite[$name]; [void]$script:DuringWaitWrite.Remove($name)
                    & $write
                    $script:WaitWritten[$name] = $true
                }
            }
            $holdOnRollback = {
                param($record, $staged, $backup, $attempt)
                # As the rollback-replace check above: hold existing.bin when added.bin is published, so the rollback's own replace
                # of existing.bin meets a sharing violation and waits. Not a closure, for the same reason.
                if ($record.Name -ceq 'added.bin' -and -not $script:ReleaseOnRetry.ContainsKey('rollback of existing.bin')) {
                    $script:ReleaseOnRetry['rollback of existing.bin'] = & $hold (Join-Path $destination 'existing.bin')
                }
                return $null
            }

            # The order (T8-X3-1): a destination rewritten during the wait refuses the retry and is kept. A guard moved to just before
            # the sleep (after the first seam) passes REVALIDATES_AFTER_DELAY, whose write is made in the first seam, and fails here.
            $plan = & $newPlan
            $script:WaitWritten = @{}
            $installedPath = Join-Path $destination 'existing.bin'
            $script:DuringWaitWrite['existing.bin'] = { [IO.File]::WriteAllText($installedPath, 'written during the wait') }.GetNewClosure()
            $reachedAgain = [ref]$false
            $script:HostRuntimeOperationForSelfTest = {
                param($record, $staged, $backup, $attempt)
                if ($record.Name -ceq 'existing.bin' -and $attempt -ge 2) { $reachedAgain.Value = $true }
                if ($record.Name -ceq 'existing.bin' -and $attempt -eq 1) { return [IO.IOException]::new('synthetic sharing violation', -2147024864) }
                return $null
            }.GetNewClosure()
            $raised = $null
            try { Publish-HostRuntimePlan $plan } catch { $raised = $_.Exception } finally { $script:HostRuntimeOperationForSelfTest = $null }
            $message = if ($null -ne $raised) { $raised.Message } else { '' }
            if (-not $script:WaitWritten['existing.bin'] -or -not $message.Contains('the installed file changed') -or (& $retries 'existing.bin') -ne 1 -or
                $plan.Applied -or (& $readDestination 'existing.bin') -cne 'written during the wait' -or $reachedAgain.Value) {
                throw "INSTALL_RETRY_GUARD_RUNS_AFTER_THE_WAIT: a destination rewritten during the wait itself (after the sleep) refuses the retry and is kept (written $([bool]$script:WaitWritten['existing.bin']), raised '$message', retries $(& $retries 'existing.bin'), operation reached again $($reachedAgain.Value), existing.bin $(& $readDestination 'existing.bin'))."
            }
            $checks++
            Close-HostRuntimePlan $plan
            & $resetDestination

            # Marker state (T8-X3-2, Publish-UpdateMarker's first predicate): a module ID added to the marker during the wait refuses
            # the repeat publication and the concurrent marker is kept.
            [IO.File]::WriteAllText($retryMarker, "alpha`n", [Text.UTF8Encoding]::new($false))
            $markerSnapshot = Read-UpdateMarker $retryMarker
            $script:RetryWitness = @{}; $script:WaitWritten = @{}
            $script:ReleaseOnRetry['the update marker'] = & $hold $retryMarker
            $script:DuringWaitWrite['the update marker'] = { [IO.File]::WriteAllText($retryMarker, "alpha`nbeta`n", [Text.UTF8Encoding]::new($false)) }.GetNewClosure()
            $raised = $null
            try { Publish-UpdateMarker $retryMarker $markerSnapshot } catch { $raised = $_.Exception }
            $message = if ($null -ne $raised) { $raised.Message } else { '' }
            $markerNow = [IO.File]::ReadAllText($retryMarker)
            if (-not $script:WaitWritten['the update marker'] -or -not $message.Contains('the update marker changed') -or
                (& $retries 'the update marker') -ne 1 -or $markerNow -cne "alpha`nbeta`n") {
                throw "INSTALL_MARKER_RETRY_REFUSES_CHANGED_MARKER: an update marker changed during the retry wait (an ID added) refuses the repeat publication and keeps the concurrent marker (written $([bool]$script:WaitWritten['the update marker']), raised '$message', retries $(& $retries 'the update marker'), marker '$($markerNow.Replace("`n", '|'))')."
            }
            $checks++

            # Marker staged digest (T8-X3-2, the second predicate): the staged copy rewritten during the wait refuses the repeat
            # publication and the marker is left as it was.
            [IO.File]::WriteAllText($retryMarker, "alpha`n", [Text.UTF8Encoding]::new($false))
            $markerSnapshot = Read-UpdateMarker $retryMarker
            $script:RetryWitness = @{}; $script:WaitWritten = @{}
            $script:ReleaseOnRetry['the update marker'] = & $hold $retryMarker
            $markerParent = Split-Path -Parent $retryMarker
            $plantedStages = [ref]0
            $script:DuringWaitWrite['the update marker'] = {
                foreach ($staged in [IO.Directory]::GetFiles($markerParent, 'agentbroker-update-*.tmp')) {
                    [IO.File]::WriteAllText($staged, "alpha`nplanted`n"); $plantedStages.Value++
                }
            }.GetNewClosure()
            $raised = $null
            try { Publish-UpdateMarker $retryMarker $markerSnapshot } catch { $raised = $_.Exception }
            $message = if ($null -ne $raised) { $raised.Message } else { '' }
            $markerNow = [IO.File]::ReadAllText($retryMarker)
            if (-not $script:WaitWritten['the update marker'] -or $plantedStages.Value -ne 1 -or -not $message.Contains('its staged copy changed') -or
                (& $retries 'the update marker') -ne 1 -or $markerNow -cne "alpha`n") {
                throw "INSTALL_MARKER_RETRY_REFUSES_CHANGED_STAGE: an update marker whose staged copy changed during the retry wait is not published (written $([bool]$script:WaitWritten['the update marker']), staged copies rewritten $($plantedStages.Value), raised '$message', retries $(& $retries 'the update marker'), marker '$($markerNow.Replace("`n", '|'))')."
            }
            $checks++

            # Rollback backup digest (T8-X3-2, the rollback guard's first predicate): the rollback copy rewritten during the rollback's
            # wait refuses the rollback; the published file is kept, the rollback reports itself incomplete, recovery stays refused.
            $plan = & $newPlan
            $script:WaitWritten = @{}
            $rollbackBackup = Join-Path $plan.Previous 'existing.bin'
            $script:DuringWaitWrite['rollback of existing.bin'] = { [IO.File]::WriteAllText($rollbackBackup, 'a rollback copy rewritten during the wait') }.GetNewClosure()
            $script:HostRuntimeOperationForSelfTest = $holdOnRollback
            $raised = $null
            try { Publish-HostRuntimePlan $plan -FailAfter 2 } catch { $raised = $_.Exception } finally { $script:HostRuntimeOperationForSelfTest = $null }
            $message = if ($null -ne $raised) { $raised.Message } else { '' }
            $backupNow = if ([IO.File]::Exists($rollbackBackup)) { [IO.File]::ReadAllText($rollbackBackup) } else { '<absent>' }
            if (-not $script:WaitWritten['rollback of existing.bin'] -or -not $message.Contains('automatic rollback is incomplete') -or
                -not $plan.RollbackIncomplete -or (& $retries 'rollback of existing.bin') -ne 1 -or (& $readDestination 'existing.bin') -cne 'new-existing.bin' -or
                $backupNow -cne 'a rollback copy rewritten during the wait' -or (Test-RecoveryLaunchPermitted $false $true $true $plan $null)) {
                throw "INSTALL_ROLLBACK_RETRY_REFUSES_CHANGED_BACKUP: a rollback whose copy changed during its retry wait is refused, keeps the published file, reports itself incomplete and refuses recovery (written $([bool]$script:WaitWritten['rollback of existing.bin']), raised '$message', rollback incomplete $($plan.RollbackIncomplete), retries $(& $retries 'rollback of existing.bin'), existing.bin $(& $readDestination 'existing.bin'), backup '$backupNow')."
            }
            $checks++
            Close-HostRuntimePlan $plan
            if ([IO.File]::Exists($rollbackBackup)) { [IO.File]::Delete($rollbackBackup) }
            & $resetDestination

            # Rollback target digest (T8-X3-2, the second predicate): the published file rewritten during the rollback's wait is not
            # overwritten by the rollback; it reports itself incomplete and recovery stays refused.
            $plan = & $newPlan
            $script:WaitWritten = @{}
            $rollbackBackup = Join-Path $plan.Previous 'existing.bin'
            $rollbackTarget = Join-Path $destination 'existing.bin'
            $script:DuringWaitWrite['rollback of existing.bin'] = { [IO.File]::WriteAllText($rollbackTarget, 'a published file rewritten during the wait') }.GetNewClosure()
            $script:HostRuntimeOperationForSelfTest = $holdOnRollback
            $raised = $null
            try { Publish-HostRuntimePlan $plan -FailAfter 2 } catch { $raised = $_.Exception } finally { $script:HostRuntimeOperationForSelfTest = $null }
            $message = if ($null -ne $raised) { $raised.Message } else { '' }
            $backupNow = if ([IO.File]::Exists($rollbackBackup)) { [IO.File]::ReadAllText($rollbackBackup) } else { '<absent>' }
            if (-not $script:WaitWritten['rollback of existing.bin'] -or -not $message.Contains('automatic rollback is incomplete') -or
                -not $plan.RollbackIncomplete -or (& $retries 'rollback of existing.bin') -ne 1 -or
                (& $readDestination 'existing.bin') -cne 'a published file rewritten during the wait' -or $backupNow -cne 'old-existing.bin' -or
                (Test-RecoveryLaunchPermitted $false $true $true $plan $null)) {
                throw "INSTALL_ROLLBACK_RETRY_REFUSES_CHANGED_TARGET: a rollback whose target changed during its retry wait does not overwrite it, reports itself incomplete and refuses recovery (written $([bool]$script:WaitWritten['rollback of existing.bin']), raised '$message', rollback incomplete $($plan.RollbackIncomplete), retries $(& $retries 'rollback of existing.bin'), existing.bin $(& $readDestination 'existing.bin'), backup '$backupNow')."
            }
            $checks++
            Close-HostRuntimePlan $plan
            if ([IO.File]::Exists($rollbackBackup)) { [IO.File]::Delete($rollbackBackup) }
            & $resetDestination
        } finally {
            foreach ($open in @($script:ReleaseOnRetry.Values)) { $open.Dispose() }
            $script:ReleaseOnRetry = @{}
            if ($null -ne $script:WatchdogHold) { $script:WatchdogHold.Dispose(); $script:WatchdogHold = $null }
            $script:ReplaceRetryForSelfTest = $null; $script:HostRuntimeOperationForSelfTest = $null; $script:DuringDelayWrite = $null
            $script:ReplaceRetryWaitForSelfTest = $null; $script:DuringWaitWrite = @{}; $script:WaitWritten = @{}
        }

        $sourceLink = Assert-ContainedPath (Join-Path $root 'source-linked.bin') $root
        [AgentBrokerLocalInstallNative]::CreateTestHardLink($sourceLink, (Join-Path $source 'existing.bin'))
        try {
            $refused = $false
            try { [void](New-HostRuntimePlan $source $destination $names $null) } catch { $refused = $true }
            if (-not $refused) { throw 'A hard-linked runtime source passed retained-input validation.' }
            $checks++
        } finally { [IO.File]::Delete($sourceLink) }
        $destinationLink = Assert-ContainedPath (Join-Path $root 'destination-linked.bin') $root
        [AgentBrokerLocalInstallNative]::CreateTestHardLink($destinationLink, (Join-Path $destination 'existing.bin'))
        try {
            $refused = $false
            try { [void](New-HostRuntimePlan $source $destination $names $null) } catch { $refused = $true }
            if (-not $refused) { throw 'A hard-linked installed runtime file passed retained-input validation.' }
            $checks++
        } finally { [IO.File]::Delete($destinationLink) }

        [IO.File]::WriteAllText((Join-Path $previous 'preserved.bin'), 'retained previous module')
        $archived = Archive-PreviousModuleCopy $previous $root
        if ([IO.Directory]::Exists($previous) -or [IO.File]::ReadAllText((Join-Path $archived 'preserved.bin')) -cne 'retained previous module') {
            throw 'Opt-in previous-copy archival did not preserve its exact content.'
        }
        $checks++
        return $checks
    } catch { $transactionFailed = $true; throw }
    finally {
        try {
            foreach ($plan in $plans) { Close-HostRuntimePlan $plan }
            Remove-OwnedTestTree $root $Parent
        } catch {
            if (-not $transactionFailed) { throw }
            Write-Warning ('Runtime fixture cleanup refused; original failure preserved: ' + $root)
        }
    }
}

if ($SelfTest) {
    . (Join-Path $PSScriptRoot '..\..\tests\OwnedTestTree.ps1')
    $checks = 0
    foreach ($name in @('AgentBroker.dll', 'worker/AgentBroker.Worker.exe', 'worker/licenses/THIRD-PARTY-NOTICES.txt')) {
        [void](Assert-EntryName $name); $checks++
    }
    foreach ($name in @('../escape.dll', '/absolute.dll', 'worker//bad.dll', 'worker/name. ', 'worker/CON.txt',
                        'worker/file:stream', 'worker\wrong.dll', '.env.settings', 'worker/a.env', '.npmrc',
                        '.git/credentials', 'a.pem', 'a.key', 'a.p12', 'a.pfx', 'a.keystore', 'PrivateKeyFixture/code.cs',
                        '.mozilla/profile.txt', '.config/google-chrome/profile.txt', '.config/chromium/profile.txt', 'Mozilla/Firefox/profile.txt')) {
        $refused = $false
        try { [void](Assert-EntryName $name) } catch { $refused = $true }
        if (-not $refused) { throw 'Archive path-policy self-test failed.' }
        $checks++
    }
    foreach ($attributes in @(0x400, [int]0xa0000000)) {
        $refused = $false
        try { [void](Assert-EntryName 'worker/link' $attributes) } catch { $refused = $true }
        if (-not $refused) { throw 'Archive link-policy self-test failed.' }
        $checks++
    }
    # Compile the embedded helper without invoking any process/window/network operations.
    $parseTokens = $null; $parseErrors = $null
    $syntax = [Management.Automation.Language.Parser]::ParseFile($PSCommandPath, [ref]$parseTokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw 'Installer syntax self-test failed.' }
    $nativeSources = @($syntax.FindAll({ param($node)
        $node -is [Management.Automation.Language.StringConstantExpressionAst] -and
        $node.StringConstantType -eq [Management.Automation.Language.StringConstantType]::SingleQuotedHereString -and
        $node.Value.Contains('public static class AgentBrokerLocalInstallNative')
    }, $true))
    if ($nativeSources.Count -ne 1) { throw 'Installer native probe source was not unique.' }
    Initialize-InstallerNative
    $checks++
    $testRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $testArtifacts = Assert-ContainedPath (Join-Path $testRepo 'artifacts') $testRepo
    $testRoot = Assert-ContainedPath (Join-Path $testArtifacts ('agentbroker-install-policy-' + [guid]::NewGuid().ToString('N'))) $testArtifacts
    Assert-NoReparseComponents $testRoot
    if (Test-Path -LiteralPath $testRoot) { throw 'Unique installer policy test already exists.' }
    [void][IO.Directory]::CreateDirectory($testRoot)
    $original = Join-Path $testRoot 'original.bin'
    $hardLink = Join-Path $testRoot 'linked.bin'
    $versioned = Join-Path $testRoot 'synthetic-versioned-host.dll'
    $newSettings = Join-Path $testRoot 'new-settings'
    $publishedData = Join-Path $testRoot 'published-data'
    $collidingSettings = Join-Path $testRoot 'colliding-settings'
    $occupiedData = Join-Path $testRoot 'occupied-data'
    $marker = Join-Path $testRoot 'pending-module-updates.txt'
    $markerLink = Join-Path $testRoot 'linked-marker.txt'
    $publishedStage = Join-Path $testRoot 'agentbroker.staged'
    $replacedStage = Join-Path $testRoot 'agentbroker.replaced'
    $expectedPayload = Join-Path $testRoot 'expected-payload'
    $installedPayload = Join-Path $testRoot 'installed-payload'
    $applicationStage = Join-Path $testRoot 'application.staged'
    $payloadLink = Join-Path $testRoot 'linked-payload.dll'
    $contractsFixture = Join-Path $testRoot 'Synthetic.Contracts.dll'
    $legacyModuleFixture = Join-Path $testRoot 'Synthetic.Module.1.0.4.dll'
    $busModuleFixture = Join-Path $testRoot 'Synthetic.Module.1.1.0.dll'
    $fixtureFailed = $false
    try {
        $lanOff = [AgentBrokerLocalInstallNative]::ListenerSettings('{"loopbackPort":"17432","lanPort":"17433","enableLan":"false"}')
        $lanOn = [AgentBrokerLocalInstallNative]::ListenerSettings('{"loopbackPort":"17432","lanPort":"17433","enableLan":"true"}')
        $local = [pscustomobject]@{ Port=17432; Address='127.0.0.1'; Pid=42 }
        $lan = [pscustomobject]@{ Port=17433; Address='0.0.0.0'; Pid=42 }
        if ($lanOff.ExpectedCount -ne 1 -or $lanOff.Includes(17433) -or -not $lanOff.Includes(17432) -or
            (Get-RequiredListenerOwner @($local) $lanOff) -ne 42) { throw 'INSTALLER_LAN_OFF_LISTENER_CONTRACT' }
        $checks++
        if ($lanOn.ExpectedCount -ne 2 -or -not $lanOn.Includes(17433) -or $lanOn.Includes(7432) -or
            (Get-RequiredListenerOwner @($local,$lan) $lanOn) -ne 42) { throw 'INSTALLER_CUSTOM_PORT_LISTENER_CONTRACT' }
        $checks++
        foreach ($rows in @(@($local), @($local,$local), @($local,[pscustomobject]@{ Port=17433; Address='0.0.0.0'; Pid=43 }),
                            @([pscustomobject]@{ Port=17432; Address='0.0.0.0'; Pid=42 },$lan))) {
            if ($null -ne (Get-RequiredListenerOwner $rows $lanOn)) { throw 'INSTALLER_LISTENER_OWNER_REFUSAL' }
            $checks++
        }
        foreach ($json in @('{"loopbackPort":"0"}', '{"lanPort":"70000"}', '{"loopbackPort":"7433"}')) {
            $refused = $false
            try { [void][AgentBrokerLocalInstallNative]::ListenerSettings($json) } catch { $refused = $true }
            if (-not $refused) { throw 'INSTALLER_INVALID_LISTENER_SETTINGS' }
            $checks++
        }
        [IO.File]::WriteAllBytes($original, [byte[]]@(1, 2, 3))
        $testInput = [IO.File]::Open($original, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try { [AgentBrokerLocalInstallNative]::VerifySingleLink($testInput) } finally { $testInput.Dispose() }
        $checks++
        [AgentBrokerLocalInstallNative]::CreateTestHardLink($hardLink, $original)
        $testInput = [IO.File]::Open($original, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $refused = $false
        try { [AgentBrokerLocalInstallNative]::VerifySingleLink($testInput) } catch { $refused = $true } finally { $testInput.Dispose() }
        if (-not $refused) { throw 'Retained-file hard-link refusal test failed.' }
        $checks++
        $refusedByFileGuard = $false
        try { [void](Get-PublicHostVersion $hardLink) }
        catch { $refusedByFileGuard = $_.Exception.Message.Contains('one ordinary file with one link') }
        if (-not $refusedByFileGuard) { throw 'Host version parsing did not refuse a linked input at the ordinary-file guard.' }
        $checks++
        [IO.File]::WriteAllBytes($versioned, [byte[]]@(1, 2, 3))
        if ($null -ne (Get-PublicHostVersion $versioned)) { throw 'An ordinary unversioned fixture invented a host version.' }
        $checks++

        [void][IO.Directory]::CreateDirectory($newSettings)
        [IO.File]::WriteAllText((Join-Path $newSettings 'settings.json'), '{"enabled":"true"}', [Text.UTF8Encoding]::new($false))
        Publish-NewSettingsStage $newSettings $publishedData $testRoot
        if ([IO.Directory]::Exists($newSettings) -or -not [IO.File]::Exists((Join-Path $publishedData 'settings.json'))) {
            throw 'Atomic new-settings publication test failed.'
        }
        $checks++
        [void][IO.Directory]::CreateDirectory($collidingSettings)
        [void][IO.Directory]::CreateDirectory($occupiedData)
        [IO.File]::WriteAllText((Join-Path $collidingSettings 'settings.json'), '{"enabled":"true"}', [Text.UTF8Encoding]::new($false))
        $refused = $false
        try { Publish-NewSettingsStage $collidingSettings $occupiedData $testRoot } catch { $refused = $true }
        if (-not $refused -or -not [IO.File]::Exists((Join-Path $collidingSettings 'settings.json')) -or
            [IO.File]::Exists((Join-Path $occupiedData 'settings.json'))) { throw 'Atomic settings collision refusal test failed.' }
        $checks++

        $snapshot = Read-UpdateMarker $marker
        Publish-UpdateMarker $marker $snapshot
        if ([string]::Join(',', (Read-UpdateMarker $marker).Ids) -cne 'agentbroker') { throw 'New update marker publication failed.' }
        $checks++
        [IO.File]::WriteAllText($marker, "alpha`nbeta`n", [Text.UTF8Encoding]::new($false))
        Publish-UpdateMarker $marker (Read-UpdateMarker $marker)
        if ([string]::Join(',', (Read-UpdateMarker $marker).Ids) -cne 'alpha,beta,agentbroker') { throw 'Unrelated update marker IDs were lost.' }
        $checks++
        $snapshot = Read-UpdateMarker $marker
        [IO.File]::WriteAllText($marker, "changed`n", [Text.UTF8Encoding]::new($false))
        $refused = $false
        try { Publish-UpdateMarker $marker $snapshot } catch { $refused = $true }
        if (-not $refused -or [string]::Join(',', (Read-UpdateMarker $marker).Ids) -cne 'changed') { throw 'Changed update marker was overwritten.' }
        $checks++
        [AgentBrokerLocalInstallNative]::CreateTestHardLink($markerLink, $marker)
        $refused = $false
        try { [void](Read-UpdateMarker $marker) } catch { $refused = $true }
        if (-not $refused) { throw 'Hard-linked marker contents were not refused.' }
        [IO.File]::Delete($markerLink)
        $checks++
        [IO.File]::WriteAllBytes($marker, [byte[]]@(255))
        $refused = $false
        try { [void](Read-UpdateMarker $marker) } catch { $refused = $true }
        if (-not $refused) { throw 'Invalid UTF-8 marker contents were accepted.' }
        $checks++
        [IO.File]::WriteAllBytes($marker, [byte[]]::new(65537))
        $refused = $false
        try { [void](Read-UpdateMarker $marker) } catch { $refused = $true }
        if (-not $refused) { throw 'Oversized marker contents were accepted.' }
        $checks++
        Publish-UpdateStage $collidingSettings $publishedStage $replacedStage $testRoot
        if (-not [IO.File]::Exists((Join-Path $publishedStage 'settings.json'))) { throw 'Verified update stage was not published.' }
        [void][IO.Directory]::CreateDirectory($collidingSettings)
        $refused = $false
        try { Publish-UpdateStage $collidingSettings $publishedStage $replacedStage $testRoot } catch { $refused = $true }
        if (-not $refused -or -not [IO.File]::Exists((Join-Path $publishedStage 'settings.json')) -or
            -not [IO.Directory]::Exists($collidingSettings)) { throw 'Existing valid staged payload was changed by a collision.' }
        $checks++
        [void][IO.Directory]::CreateDirectory($replacedStage)
        $refused = $false
        try { Publish-UpdateStage $collidingSettings (Join-Path $testRoot 'other.staged') $replacedStage $testRoot } catch { $refused = $true }
        if (-not $refused -or -not [IO.Directory]::Exists($replacedStage)) { throw 'Existing replaced payload was not preserved.' }
        $checks++

        foreach ($directory in @($expectedPayload, $installedPayload)) { [void][IO.Directory]::CreateDirectory((Join-Path $directory 'worker')) }
        [IO.File]::WriteAllText((Join-Path $expectedPayload 'AgentBroker.dll'), 'new module')
        [IO.File]::WriteAllText((Join-Path $expectedPayload 'worker\AgentBroker.Worker.dll'), 'new worker')
        [IO.File]::WriteAllText((Join-Path $installedPayload 'AgentBroker.dll'), 'old module')
        [IO.File]::WriteAllText((Join-Path $installedPayload 'worker\AgentBroker.Worker.dll'), 'old worker')
        [IO.File]::WriteAllText($marker, '', [Text.UTF8Encoding]::new($false))
        $expected = Get-PublicPayloadSnapshot $expectedPayload
        $transportHasSevenTools = $true
        $application = Test-UpdateApplication $installedPayload $expected $applicationStage $marker
        if ($transportHasSevenTools -and $application.Applied) { throw 'Old payload with healthy transport incorrectly proved update application.' }
        $checks++
        [IO.File]::WriteAllText((Join-Path $installedPayload 'AgentBroker.dll'), 'new module')
        if ((Test-UpdateApplication $installedPayload $expected $applicationStage $marker).Applied) { throw 'An old worker with the new module passed application proof.' }
        $checks++
        [IO.File]::WriteAllText((Join-Path $installedPayload 'AgentBroker.dll'), 'old module')
        [IO.File]::WriteAllText((Join-Path $installedPayload 'worker\AgentBroker.Worker.dll'), 'new worker')
        if ((Test-UpdateApplication $installedPayload $expected $applicationStage $marker).Applied) { throw 'An old module with the new worker passed application proof.' }
        $checks++
        [IO.File]::WriteAllText((Join-Path $installedPayload 'AgentBroker.dll'), 'new module')
        if (-not (Test-UpdateApplication $installedPayload $expected $applicationStage $marker).Applied -or
            -not [IO.Directory]::Exists($replacedStage)) { throw 'New payload with a harmless retained rollback copy was refused.' }
        $checks++
        [void][IO.Directory]::CreateDirectory($applicationStage)
        [IO.File]::WriteAllText((Join-Path $applicationStage 'payload.bin'), 'preserved verified payload')
        [IO.File]::WriteAllText($marker, "agentbroker`n", [Text.UTF8Encoding]::new($false))
        $application = Test-UpdateApplication $installedPayload $expected $applicationStage $marker
        if ($application.Applied -or $application.StageProcessed -or $application.MarkerProcessed) {
            throw 'A preserved pending payload/marker incorrectly proved completed application.'
        }
        $checks++
        [IO.File]::Delete((Join-Path $applicationStage 'payload.bin')); [IO.Directory]::Delete($applicationStage, $false)
        if ((Test-UpdateApplication $installedPayload $expected $applicationStage $marker).Applied) { throw 'A pending marker was incorrectly considered processed.' }
        $checks++
        [IO.File]::WriteAllText($marker, "alpha`n", [Text.UTF8Encoding]::new($false))
        if (-not (Test-UpdateApplication $installedPayload $expected $applicationStage $marker).Applied) { throw 'An unrelated pending update blocked valid application proof.' }
        $checks++
        [AgentBrokerLocalInstallNative]::CreateTestHardLink($payloadLink, (Join-Path $installedPayload 'AgentBroker.dll'))
        if ((Test-UpdateApplication $installedPayload $expected $applicationStage $marker).Applied) { throw 'A hard-linked public payload passed the application proof.' }
        $checks++
        $checks += Test-HostRuntimeTransaction $testRoot

        # Package-version tool contracts. Synthetic module assemblies call a ModuleInfo setter in a separate
        # contracts assembly, as the real module does, so the version read crosses a member reference.
        Add-Type -TypeDefinition 'namespace SyntheticContracts { public sealed class ModuleInfo { public string Id { get; set; } public string Version { get; set; } public string MinHostVersion { get; set; } } }' `
            -OutputAssembly $contractsFixture -OutputType Library
        foreach ($fixture in @(@{ Path = $legacyModuleFixture; Version = '1.0.4' }, @{ Path = $busModuleFixture; Version = '1.1.0' })) {
            $suffix = $fixture.Version.Replace('.', '_')
            Add-Type -TypeDefinition ('namespace SyntheticModule' + $suffix + ' { public sealed class Module { public SyntheticContracts.ModuleInfo Info => ' +
                'new SyntheticContracts.ModuleInfo { Id = "agentbroker", Version = "' + $fixture.Version + '", MinHostVersion = "1.6.1" }; } }') `
                -ReferencedAssemblies $contractsFixture -OutputAssembly $fixture.Path -OutputType Library
        }
        $sevenTools = @('list_sessions', 'register_agent', 'send_message', 'read_inbox', 'acknowledge_message', 'get_message_receipt', 'reply_to_message')
        $legacyVersion = [AgentBrokerLocalInstallNative]::DeclaredModuleVersion([IO.File]::ReadAllBytes($legacyModuleFixture))
        $legacyTools = [AgentBrokerLocalInstallNative]::ExpectedTools($legacyVersion)
        if ($legacyVersion -cne '1.0.4' -or $legacyTools.Count -ne 7 -or @(Compare-Object $legacyTools $sevenTools -CaseSensitive).Count -ne 0) {
            throw 'A 1.0.4 package did not keep its seven-tool MCP contract.'
        }
        $checks++
        $busVersion = [AgentBrokerLocalInstallNative]::DeclaredModuleVersion([IO.File]::ReadAllBytes($busModuleFixture))
        $busTools = [AgentBrokerLocalInstallNative]::ExpectedTools($busVersion)
        if ($busVersion -cne '1.1.0' -or $busTools.Count -ne 10 -or
            @(Compare-Object $busTools ($sevenTools + @('send', 'wait_for_messages', 'wait_for_reply')) -CaseSensitive).Count -ne 0) {
            throw 'A 1.1.0 package did not require all ten MCP tools.'
        }
        $checks++
        # 1.3.0 added wait_for_receipt; a 1.2.0 package must not be required to serve it.
        $tenTools = $sevenTools + @('send', 'wait_for_messages', 'wait_for_reply')
        $before = [AgentBrokerLocalInstallNative]::ExpectedTools('1.2.0')
        $receiptTools = [AgentBrokerLocalInstallNative]::ExpectedTools('1.3.0')
        if ($before.Count -ne 10 -or @(Compare-Object $before $tenTools -CaseSensitive).Count -ne 0 -or
            $receiptTools.Count -ne 11 -or @(Compare-Object $receiptTools ($tenTools + @('wait_for_receipt')) -CaseSensitive).Count -ne 0) {
            throw 'A 1.3.0 package did not require wait_for_receipt beside the ten 1.2.0 MCP tools.'
        }
        $checks++
        # 1.4.0 added whoami; a 1.3.0 package must not be required to serve it.
        $whoTools = [AgentBrokerLocalInstallNative]::ExpectedTools('1.4.0')
        if ($whoTools.Count -ne 12 -or @(Compare-Object $whoTools ($tenTools + @('wait_for_receipt', 'whoami')) -CaseSensitive).Count -ne 0 -or
            [AgentBrokerLocalInstallNative]::ExpectedTools('1.3.0') -ccontains 'whoami') {
            throw 'A 1.4.0 package did not require whoami beside the eleven 1.3.0 MCP tools.'
        }
        $checks++
        $beforeRecovery = [AgentBrokerLocalInstallNative]::ExpectedTools('1.7.2')
        $recoveryTools = [AgentBrokerLocalInstallNative]::ExpectedTools('1.8.0')
        if ($beforeRecovery.Count -ne 12 -or $recoveryTools.Count -ne 16 -or
            @(Compare-Object $recoveryTools ($whoTools + @('read_inbox_page', 'read_native_recovery', 'set_session_declaration', 'set_session_title')) -CaseSensitive).Count -ne 0) {
            throw 'PACKAGE_RECOVERY_TOOL_CONTRACT: 1.8.0 requires its four additive tools while 1.7.2 keeps twelve.'
        }
        $checks++
        $beforeAttachments = [AgentBrokerLocalInstallNative]::ExpectedTools('1.10.4')
        $attachmentTools = [AgentBrokerLocalInstallNative]::ExpectedTools('1.10.5')
        if ($beforeAttachments.Count -ne 16 -or $attachmentTools.Count -ne 18 -or
            @(Compare-Object $attachmentTools ($recoveryTools + @('send_with_attachments', 'fetch_attachment')) -CaseSensitive).Count -ne 0) {
            throw 'PACKAGE_ATTACHMENT_TOOL_CONTRACT: 1.10.5 requires its two attachment tools while 1.10.4 keeps sixteen.'
        }
        $checks++
        $refused = $false
        try { [void][AgentBrokerLocalInstallNative]::DeclaredModuleVersion([IO.File]::ReadAllBytes($contractsFixture)) } catch { $refused = $true }
        if (-not $refused) { throw 'An assembly that declares no module version invented one.' }
        $checks++
        foreach ($unknown in @('0.9.0', '1.1', '1.1.0.0', 'one')) {
            $refused = $false
            try { [void][AgentBrokerLocalInstallNative]::ExpectedTools($unknown) } catch { $refused = $true }
            if (-not $refused) { throw 'An unknown or malformed package version received a tool contract.' }
            $checks++
        }
    } catch { $fixtureFailed = $true; throw }
    finally { Complete-OwnedTestTree $testRoot $testArtifacts $fixtureFailed }
    if (Test-Path -LiteralPath $testRoot) { throw 'INSTALLER_FIXTURE_CLEANED' }
    Write-Host "PathPolicyChecks=$checks"
    Write-Host 'ReleasePayloadFilesOpened=False'
    Write-Host 'SyntheticFixturesCleaned=True'
    return
}

if (-not $IsWindows) { throw 'This helper supports Windows user installations only.' }
if (($HostRuntimeDirectory -or $ArchivePreviousCopy) -and -not $StageUpdate) {
    throw 'HostRuntimeDirectory and ArchivePreviousCopy require explicit StageUpdate mode.'
}
if (-not $ZipPath -or $ExpectedSha256 -notmatch '^[a-fA-F0-9]{64}$') {
    throw 'Provide an AgentBroker ZIP and its explicit expected catalog SHA256.'
}
$zipFull = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ZipPath))
if ([IO.Path]::GetExtension($zipFull) -ine '.zip') { throw 'The input must be a ZIP archive.' }
if (Get-ProtectedEntryPattern $zipFull) { throw 'The archive path is protected; no archive was opened.' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$allowedZip = Assert-ContainedPath (Join-Path $repoRoot 'modules-dist\agentbroker.zip') $repoRoot
if (-not $zipFull.Equals($allowedZip, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'This local development helper accepts only this checkout''s modules-dist/agentbroker.zip.'
}
if (-not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable('DESKTOP_AI_COMPANION_DATA_ROOT'))) {
    throw 'Remove the companion data-root override before using this installed-host helper.'
}
Assert-NoReparseComponents $zipFull
$zipInfo = Get-Item -LiteralPath $zipFull
if ($zipInfo.PSIsContainer -or $zipInfo.Length -le 0 -or $zipInfo.Length -gt 100MB) { throw 'Archive size is outside the module limit.' }

$installRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\Desktop AI Companion'))
$modulesRoot = Assert-ContainedPath (Join-Path $installRoot 'modules') $installRoot
$moduleDirectory = Assert-ContainedPath (Join-Path $modulesRoot 'agentbroker') $modulesRoot
$dataRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'DesktopAICompanion'))
$moduleData = Assert-ContainedPath (Join-Path $dataRoot 'modules\agentbroker') $dataRoot
$dataParent = [IO.Path]::GetDirectoryName($moduleData)
$settingsPath = Assert-ContainedPath (Join-Path $moduleData 'settings.json') $moduleData
$companionExe = Assert-ContainedPath (Join-Path $installRoot 'DesktopAICompanion.exe') $installRoot
$workerExe = Assert-ContainedPath (Join-Path $moduleDirectory 'worker\AgentBroker.Worker.exe') $moduleDirectory
$updateRoot = Assert-ContainedPath (Join-Path $installRoot 'module-staging') $installRoot
$updateStage = Assert-ContainedPath (Join-Path $updateRoot 'agentbroker.staged') $updateRoot
$updateReplaced = Assert-ContainedPath (Join-Path $updateRoot 'agentbroker.replaced') $updateRoot
$updateMarker = Assert-ContainedPath (Join-Path $dataRoot 'pending-module-updates.txt') $dataRoot
$removalMarker = Assert-ContainedPath (Join-Path $dataRoot 'pending-module-removals.txt') $dataRoot
foreach ($target in @($moduleDirectory, $moduleData, $companionExe)) { Assert-NoReparseComponents $target }
if (-not [IO.File]::Exists($companionExe) -or [IO.File]::Exists((Join-Path $installRoot 'DesktopAICompanion.portable'))) {
    throw 'The supported current-user installed companion was not found.'
}
$hostVersion = Get-PublicHostVersion $companionExe
if ($hostVersion -lt [version]'1.6.0.0') { throw 'AgentBroker requires installed host 1.6.0 or later.' }
if ($StageUpdate) {
    if (-not [IO.Directory]::Exists($moduleDirectory) -or -not [IO.Directory]::Exists($moduleData) -or
        -not [IO.File]::Exists($settingsPath) -or -not [IO.File]::Exists($workerExe)) {
        throw 'StageUpdate requires an existing installed AgentBroker and its unchanged data/settings.'
    }
    foreach ($path in @($updateStage, $updateReplaced, $updateMarker, $removalMarker)) { Assert-NoReparseComponents $path }
    if ((Test-Path -LiteralPath $updateStage) -or ((Test-Path -LiteralPath $updateReplaced) -and -not $ArchivePreviousCopy)) {
        throw 'An AgentBroker staged/replaced copy already exists; no existing payload was changed.'
    }
    if (Test-Path -LiteralPath $removalMarker) { throw 'A module removal is pending; resolve it before staging this update.' }
}
else {
    if (Test-Path -LiteralPath $moduleDirectory) { throw 'AgentBroker is already installed; no existing installation was changed.' }
    if (Test-Path -LiteralPath $settingsPath) { throw 'AgentBroker settings already exist; no existing settings were read or changed.' }
    if (Test-Path -LiteralPath $moduleData) { throw 'AgentBroker data already exists; this first-install helper does not alter it.' }
}

$zipStream = [IO.File]::Open($zipFull, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
$archive = $null
$runtimePlan = $null
try {
    Initialize-InstallerNative
    [AgentBrokerLocalInstallNative]::VerifySingleLink($zipStream)
    $archive = [IO.Compression.ZipArchive]::new($zipStream, [IO.Compression.ZipArchiveMode]::Read, $true)
    if ($archive.Entries.Count -eq 0 -or $archive.Entries.Count -gt 4096) { throw 'Archive entry count is outside the module limit.' }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $files = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $expanded = 0L
    foreach ($entry in $archive.Entries) {
        $name = Assert-EntryName $entry.FullName $entry.ExternalAttributes
        if (-not $names.Add($name)) { throw 'Archive contains a duplicate or case-colliding entry.' }
        if ($entry.Length -lt 0 -or $entry.Length -gt 128MB) { throw 'Archive entry exceeds its expanded size limit.' }
        $expanded += $entry.Length
        if ($expanded -gt 300MB) { throw 'Archive exceeds the expanded module size limit.' }
        if (-not $entry.FullName.EndsWith('/')) { [void]$files.Add($name) }
    }
    foreach ($name in $names) {
        $parent = $name
        while ($parent.Contains('/')) {
            $parent = $parent.Substring(0, $parent.LastIndexOf('/'))
            if ($files.Contains($parent)) { throw 'Archive file and directory paths collide.' }
        }
    }
    foreach ($required in @('AgentBroker.dll', 'AgentBroker.deps.json', 'DesktopAICompanion.ModuleKit.dll',
                            'worker/AgentBroker.Worker.exe', 'worker/AgentBroker.Worker.dll',
                            'worker/AgentBroker.Worker.runtimeconfig.json', 'worker/coreclr.dll',
                            'worker/hostfxr.dll', 'worker/hostpolicy.dll', 'worker/System.Private.CoreLib.dll',
                            'worker/licenses/THIRD-PARTY-NOTICES.txt', 'worker/licenses/versions.json',
                            'worker/licenses/mcp-sdk-license.txt', 'worker/licenses/microsoft-extensions-ai-license.txt',
                            'worker/licenses/dotnet-license.txt', 'worker/licenses/dotnet-third-party-notices.txt',
                            'worker/licenses/aspnetcore-license.txt', 'worker/licenses/aspnetcore-third-party-notices.txt')) {
        if (-not $files.Contains($required)) { throw 'Archive lacks a required self-contained AgentBroker payload file.' }
    }
    # All names were refused/accepted before hashing or opening any entry contents.
    $zipStream.Position = 0
    $actualSha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($zipStream))
    if (-not $actualSha.Equals($ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive SHA256 differs from the explicit expected digest.' }
    # The readiness probe expects the tool set of THIS package's declared version, read only after the
    # digest matched, so installing an older package (a rollback) is checked against its own contract.
    $moduleEntries = @($archive.Entries.Where({ $_.FullName -ieq 'AgentBroker.dll' }))
    if ($moduleEntries.Count -ne 1) { throw 'Archive module assembly was not unique.' }
    $moduleBytes = [IO.MemoryStream]::new()
    $moduleRead = $moduleEntries[0].Open()
    try { $moduleRead.CopyTo($moduleBytes); if ($moduleBytes.Length -gt 128MB) { throw 'Archive module assembly exceeds its size limit.' } }
    finally { $moduleRead.Dispose() }
    $packageVersion = [AgentBrokerLocalInstallNative]::DeclaredModuleVersion($moduleBytes.ToArray())
    $moduleBytes.Dispose()
    $expectedTools = [AgentBrokerLocalInstallNative]::ExpectedTools($packageVersion)
    if ($ArchivePreviousCopy -and (Test-Path -LiteralPath $updateReplaced)) {
        Assert-PreviousCopyMetadata $updateReplaced $installRoot
    }
    if ($HostRuntimeDirectory) {
        $runtimeContract = Get-HostRuntimeContract $repoRoot
        $runtimeSource = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($HostRuntimeDirectory))
        $allowedRuntime = Assert-ContainedPath (Join-Path $repoRoot 'build\DesktopAICompanionPortable\bin\Release\x64') $repoRoot
        if (-not $runtimeSource.Equals($allowedRuntime, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The host runtime source must be this repository''s canonical Release x64 output.'
        }
        $runtimePlan = New-HostRuntimePlan $runtimeSource $installRoot $runtimeContract.Names $runtimeContract.Version
    }
    $minimumHostVersion = Get-AgentBrokerHostFloor $repoRoot
    $effectiveHostVersion = if ($runtimePlan) { $runtimePlan.ExpectedVersion } else { $hostVersion }
    Assert-EffectiveHostVersion $effectiveHostVersion $minimumHostVersion
    if ($ValidateOnly) {
        Write-Host 'ArchiveVerified=True'
        Write-Host "ArchiveEntryCount=$($archive.Entries.Count)"
        Write-Host "ExpandedBytes=$expanded"
        Write-Host 'HostVersionVerified=True'
        Write-Host 'PublishedModuleHostFloorVerified=True'
        Write-Host "PackageModuleVersion=$packageVersion"
        Write-Host "ExpectedMcpToolCount=$($expectedTools.Count)"
        Write-Host "StageUpdateMode=$([bool]$StageUpdate)"
        Write-Host "HostRuntimeInputsVerified=$([bool]($null -ne $runtimePlan))"
        Write-Host "ArchivePreviousCopyRequested=$([bool]$ArchivePreviousCopy)"
        if ($StageUpdate) { Write-Host 'ExistingDataUnopened=True'; Write-Host "UpdateDestinationsAbsent=$(-not (Test-Path -LiteralPath $updateReplaced))" }
        else { Write-Host 'DestinationAbsent=True'; Write-Host 'SettingsAbsent=True' }
        Write-Host 'ExternalWrites=False'
        return
    }
    if ($CompanionProcessId -le 1) { throw 'Actual installation requires the exact running companion PID.' }
    $listenerPlan = if ($StageUpdate) {
        [AgentBrokerLocalInstallNative]::ListenerSettings((Read-PublicInstallerText $settingsPath))
    } else { [AgentBrokerLocalInstallNative]::ListenerSettings('{}') }
    Write-Host ("RequiredListeners=$($listenerPlan.ExpectedCount); loopbackPort=$($listenerPlan.LoopbackPort); lanPort=$($listenerPlan.LanPort); enableLan=$($listenerPlan.EnableLan)")

    $companion = [Diagnostics.Process]::GetProcessById($CompanionProcessId)
    $createdFiles = [Collections.Generic.List[string]]::new()
    $createdDirectories = [Collections.Generic.List[string]]::new()
    $stage = Assert-ContainedPath (Join-Path $installRoot ('agentbroker-install-' + [guid]::NewGuid().ToString('N'))) $installRoot
    $dataStage = Assert-ContainedPath (Join-Path $dataParent ('agentbroker-install-' + [guid]::NewGuid().ToString('N'))) $dataParent
    $stagedSettings = Assert-ContainedPath (Join-Path $dataStage 'settings.json') $dataStage
    $dataStageCreated = $false
    $dataInstalled = $false
    $installed = $false
    $readyCompleted = $false
    $companionStopped = $false
    $relaunched = $null
    $relaunchedCreated = 0L
    $previousWorker = $null
    $runtimeLaunchSafe = $true
    try {
        if (-not [AgentBrokerLocalInstallNative]::Image($companion).Equals($companionExe, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The supplied PID does not own the supported installed companion executable.'
        }
        $companionCreated = [AgentBrokerLocalInstallNative]::Created($companion)
        $initialListeners = [AgentBrokerLocalInstallNative]::Listeners($listenerPlan)
        if ($StageUpdate) {
            $siblings = [Diagnostics.Process]::GetProcessesByName('DesktopAICompanion')
            try {
                foreach ($sibling in $siblings) {
                    if ($sibling.Id -ne $companion.Id -and
                        [AgentBrokerLocalInstallNative]::Image($sibling).Equals($companionExe, [StringComparison]::OrdinalIgnoreCase)) {
                        throw 'Another installed companion is running; this update cannot exclusively preserve its shared marker.'
                    }
                }
            } finally { foreach ($sibling in $siblings) { $sibling.Dispose() } }
            $previousOwner = Get-RequiredListenerOwner $initialListeners $listenerPlan
            if ($null -eq $previousOwner) { throw 'Existing listeners do not match preserved loopbackPort, lanPort and enableLan settings.' }
            $previousWorker = [Diagnostics.Process]::GetProcessById($previousOwner)
            if (-not [AgentBrokerLocalInstallNative]::Image($previousWorker).Equals($workerExe, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'The existing listener owner was not the installed AgentBroker worker.'
            }
            [void][AgentBrokerLocalInstallNative]::Created($previousWorker)
        }
        elseif ($initialListeners.Length -ne 0) { throw 'A required broker port is already listening; nothing was installed.' }
        if (Test-Path -LiteralPath $stage) { throw 'Unique installation staging already exists.' }
        [void][IO.Directory]::CreateDirectory($stage); $createdDirectories.Add($stage)
        $buffer = [byte[]]::new(65536)
        foreach ($entry in $archive.Entries) {
            $relative = $entry.FullName.Replace('/', [IO.Path]::DirectorySeparatorChar)
            $target = Assert-ContainedPath (Join-Path $stage $relative) $stage
            $folder = if ($entry.FullName.EndsWith('/')) { $target } else { [IO.Path]::GetDirectoryName($target) }
            $missing = [Collections.Generic.List[string]]::new()
            $cursor = $folder
            while (-not [IO.Directory]::Exists($cursor)) { $missing.Add($cursor); $cursor = [IO.Path]::GetDirectoryName($cursor) }
            for ($index = $missing.Count - 1; $index -ge 0; $index--) {
                [void](Assert-ContainedPath $missing[$index] $stage)
                Assert-NoReparseComponents $missing[$index]
                [void][IO.Directory]::CreateDirectory($missing[$index]); $createdDirectories.Add($missing[$index])
            }
            if ($entry.FullName.EndsWith('/')) { continue }
            Assert-NoReparseComponents $target
            $output = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $createdFiles.Add($target)
            try {
                $input = $entry.Open()
                try {
                    $written = 0L
                    while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        $written += $read
                        if ($written -gt $entry.Length) { throw 'Expanded entry differed from its validated length.' }
                        $output.Write($buffer, 0, $read)
                    }
                    if ($written -ne $entry.Length) { throw 'Expanded entry was incomplete.' }
                } finally { $input.Dispose() }
            } finally { $output.Dispose() }
        }
        $expectedPayload = Get-PublicPayloadSnapshot $stage
        if ($runtimePlan) {
            Stage-HostRuntimePlan $runtimePlan
            Write-Host 'HostRuntimeStaged=True'
        }
        if ($StageUpdate) {
            try { [AgentBrokerLocalInstallNative]::CloseLifetime($companion, $companionCreated) }
            finally { $companionStopped = $companion.HasExited }
            $release = [Diagnostics.Stopwatch]::StartNew()
            while ($release.Elapsed -lt [TimeSpan]::FromSeconds(15)) {
                if ($previousWorker.HasExited -and [AgentBrokerLocalInstallNative]::Listeners($listenerPlan).Length -eq 0) { break }
                Start-Sleep -Milliseconds 200
            }
            if (-not $previousWorker.HasExited -or [AgentBrokerLocalInstallNative]::Listeners($listenerPlan).Length -ne 0) {
                throw 'The owned worker/listeners did not stop within the bounded graceful shutdown; no force was used.'
            }
            Write-Host 'PreviousWorkerExited=True'
            if ($ArchivePreviousCopy -and (Test-Path -LiteralPath $updateReplaced)) {
                [void](Archive-PreviousModuleCopy $updateReplaced $installRoot)
                Write-Host 'PreviousCopyArchived=True'
            }
            if ($runtimePlan) {
                Publish-HostRuntimePlan $runtimePlan
                Write-Host 'HostRuntimeUpdated=True'
                Write-Host 'HostRuntimeRollbackCopyRetained=True'
            }
            Assert-NoReparseComponents $updateRoot
            [void][IO.Directory]::CreateDirectory($updateRoot)
            if (Test-Path -LiteralPath $removalMarker) { throw 'A module removal appeared during staging; it was not changed.' }
            Publish-UpdateStage $stage $updateStage $updateReplaced $installRoot
            $installed = $true  # the verified published stage now belongs to host startup, even if marking fails
            Write-Host 'UpdatePayloadPreserved=True'
            $markerSnapshot = Read-UpdateMarker $updateMarker
            Publish-UpdateMarker $updateMarker $markerSnapshot
            Write-Host 'UpdateStaged=True'
            Write-Host 'ExistingDataPreserved=True'
            Write-Host 'ExistingSettingsPreserved=True'
        }
        else {
        # All installation targets are still absent immediately before the same-volume rename.
        foreach ($target in @($moduleDirectory, $moduleData, $settingsPath)) {
            Assert-NoReparseComponents $target
            if (Test-Path -LiteralPath $target) { throw 'An AgentBroker destination appeared during validation; no existing data was changed.' }
        }
        Assert-NoReparseComponents $dataStage
        if (Test-Path -LiteralPath $dataStage) { throw 'Unique settings staging already exists.' }
        [void][IO.Directory]::CreateDirectory($dataStage)
        $dataStageCreated = $true
        $settings = [ordered]@{ enabled = 'true'; name = 'AgentBroker'; loopbackPort = '7432'; lanPort = '7433';
            enableLan = 'true'; noAuthLoopback = 'true'; codexDiscovery = 'true'; claudeDiscovery = 'true';
            codexPeerMessages = 'false'; claudePeerCredentials = 'false' }
        $settingsBytes = [Text.UTF8Encoding]::new($false).GetBytes(($settings | ConvertTo-Json -Compress))
        $settingsFile = [IO.File]::Open($stagedSettings, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $settingsFile.Write($settingsBytes, 0, $settingsBytes.Length); $settingsFile.Flush($true) } finally { $settingsFile.Dispose() }
        [void][IO.Directory]::CreateDirectory($modulesRoot)
        foreach ($target in @($moduleDirectory, $moduleData, $settingsPath)) {
            Assert-NoReparseComponents $target
            if (Test-Path -LiteralPath $target) { throw 'An AgentBroker destination appeared before publication; no existing data was changed.' }
        }
        [void](Assert-ContainedPath $stage $installRoot)
        [void](Assert-ContainedPath $moduleDirectory $modulesRoot)
        [IO.Directory]::Move($stage, $moduleDirectory)
        $installed = $true
        # A competing data destination makes Move fail; never merge a newly enabled config into it.
        [void](Assert-ContainedPath $dataStage $dataParent)
        [void](Assert-ContainedPath $moduleData $dataParent)
        Publish-NewSettingsStage $dataStage $moduleData $dataParent
        $dataInstalled = $true
        Write-Host 'ModuleInstalled=True'
        Write-Host 'SettingsCreated=True'
        Write-Host 'NativeDeliveryEnabled=False'
        try { [AgentBrokerLocalInstallNative]::CloseLifetime($companion, $companionCreated) }
        finally { $companionStopped = $companion.HasExited }
        }
        Assert-NoReparseComponents $companionExe
        if ($runtimePlan -and $runtimePlan.Applied) {
            $runtimeLaunchSafe = $false
            foreach ($record in $runtimePlan.Records) {
                if ((Get-HostRuntimeFileSnapshot $record.Target).Hash -cne $record.Hash) { throw 'The public host runtime changed before launch.' }
            }
            Assert-HostRuntimeVersion $installRoot $runtimePlan.ExpectedVersion
            $runtimeLaunchSafe = $true
            Write-Host 'HostRuntimeBeforeLaunchVerified=True'
        }
        $relaunched = Start-Process -FilePath $companionExe -WorkingDirectory $installRoot -WindowStyle Hidden -PassThru
        if (-not [AgentBrokerLocalInstallNative]::Image($relaunched).Equals($companionExe, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The restarted process did not own the verified installed companion image.'
        }
        $relaunchedCreated = [AgentBrokerLocalInstallNative]::Created($relaunched)
        Write-Host 'CompanionRestarted=True'

        $timer = [Diagnostics.Stopwatch]::StartNew()
        $worker = $null
        while ($timer.Elapsed -lt [TimeSpan]::FromSeconds(45)) {
            $listeners = [AgentBrokerLocalInstallNative]::Listeners($listenerPlan)
            $listenerOwner = Get-RequiredListenerOwner $listeners $listenerPlan
            if ($null -ne $listenerOwner) {
                $candidate = [Diagnostics.Process]::GetProcessById($listenerOwner)
                try {
                    if ([AgentBrokerLocalInstallNative]::Image($candidate).Equals($workerExe, [StringComparison]::OrdinalIgnoreCase)) { $worker = $candidate; break }
                } finally { if ($null -eq $worker) { $candidate.Dispose() } }
                throw 'Required listener ownership differed from the installed worker.'
            }
            Start-Sleep -Milliseconds 500
        }
        if ($null -eq $worker) { throw 'The installed worker did not own the listeners required by loopbackPort, lanPort and enableLan within the readiness deadline.' }
        try {
            $workerCreated = [AgentBrokerLocalInstallNative]::Created($worker)
            $probe = [AgentBrokerLocalInstallNative]::ProbeAsync($workerExe, $moduleData, $expectedTools).GetAwaiter().GetResult()
            $listeners = [AgentBrokerLocalInstallNative]::Listeners($listenerPlan)
            if ($worker.HasExited -or [AgentBrokerLocalInstallNative]::Created($worker) -ne $workerCreated -or
                (Get-RequiredListenerOwner $listeners $listenerPlan) -ne $worker.Id) {
                throw 'Worker ownership changed for the preserved loopbackPort, lanPort or enableLan contract during MCP readiness.'
            }
            if ($relaunched.HasExited -or [AgentBrokerLocalInstallNative]::Created($relaunched) -ne $relaunchedCreated -or
                -not [AgentBrokerLocalInstallNative]::Image($relaunched).Equals($companionExe, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'The exact restarted companion did not remain alive through readiness verification.'
            }
            if ($runtimePlan -and $runtimePlan.Applied) {
                foreach ($record in $runtimePlan.Records) {
                    if ((Get-HostRuntimeFileSnapshot $record.Target).Hash -cne $record.Hash) { throw 'The restarted host runtime differs from the staged public payload.' }
                }
                Assert-HostRuntimeVersion $installRoot $runtimePlan.ExpectedVersion
                Write-Host 'RestartedHostRuntimeVerified=True'
            }
            Write-Host 'ListenerOwnersVerified=True'
            Write-Host "McpInitialized=$($probe.Initialized)"
            Write-Host "McpToolCount=$($probe.ToolCount)"
            Write-Host "ProbeExitedCleanly=$($probe.CleanExit)"
            if ($StageUpdate) {
                $application = Test-UpdateApplication $moduleDirectory $expectedPayload $updateStage $updateMarker
                Write-Host "UpdatedPublicPayloadMatches=$($application.PayloadMatches)"
                Write-Host "UpdateStageProcessed=$($application.StageProcessed)"
                Write-Host "UpdateMarkerProcessed=$($application.MarkerProcessed)"
                Write-Host "PreviousCopyRetained=$([bool](Test-Path -LiteralPath $updateReplaced))"
                if (-not $application.Applied) { throw 'The staged update was not fully applied; transport health alone does not prove application.' }
            }
            Write-Host 'Ready=True'
            $readyCompleted = $true
        } finally { $worker.Dispose() }
    }
    finally {
        # Do not leave the user's companion closed after a marker, swap, or launch error.
        # Round 8: with a host runtime plan, only when every planned file is verified in one consistent state (S-009).
        if (Test-RecoveryLaunchPermitted $readyCompleted $companionStopped $runtimeLaunchSafe $runtimePlan $relaunched) {
            try {
                Assert-NoReparseComponents $companionExe
                $recovery = Start-Process -FilePath $companionExe -WorkingDirectory $installRoot -WindowStyle Hidden -PassThru
                $recovery.Dispose()
                Write-Host 'CompanionRecoveryStarted=True'
            } catch { Write-Host 'CompanionRecoveryStarted=False' }
        } elseif (-not $readyCompleted -and $companionStopped -and -not (Test-HostRuntimePlanSettled $runtimePlan)) {
            # Said, never silent: the companion stays closed because its runtime files are not verified in one state.
            Write-Host 'CompanionRecoveryWithheld=HostRuntimeNotVerified'
        }
        if ($relaunched) { $relaunched.Dispose() }
        if ($previousWorker) { $previousWorker.Dispose() }
        $companion.Dispose()
        if ($dataStageCreated -and -not $dataInstalled) {
            [void](Assert-ContainedPath $dataStage $dataParent)
            [void](Assert-ContainedPath $stagedSettings $dataStage)
            Assert-NoReparseComponents $stagedSettings
            if ([IO.File]::Exists($stagedSettings)) { [IO.File]::Delete($stagedSettings) }
            Assert-NoReparseComponents $dataStage
            if ([IO.Directory]::Exists($dataStage)) { [IO.Directory]::Delete($dataStage, $false) }
        }
        if (-not $installed) {
            # Delete only files this invocation created, and only inside its checked unique staging root.
            foreach ($file in $createdFiles) {
                [void](Assert-ContainedPath $file $stage)
                Assert-NoReparseComponents $file
                if ([IO.File]::Exists($file)) { [IO.File]::Delete($file) }
            }
            for ($index = $createdDirectories.Count - 1; $index -ge 0; $index--) {
                $folder = $createdDirectories[$index]
                if ($folder -ne $stage) { [void](Assert-ContainedPath $folder $stage) }
                [void](Assert-ContainedPath $folder $installRoot)
                Assert-NoReparseComponents $folder
                if ([IO.Directory]::Exists($folder)) { [IO.Directory]::Delete($folder, $false) }
            }
        }
    }
}
finally {
    Close-HostRuntimePlan $runtimePlan
    if ($archive) { $archive.Dispose() }
    $zipStream.Dispose()
}
