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
    shutdown, retaining rollback files. ArchivePreviousCopy explicitly retains an older replaced
    module under a unique name so the host can retain the newly replaced copy too.
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

function Initialize-InstallerNative {
    if ('AgentBrokerLocalInstallNative' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
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
    public static Listener[] Listeners() {
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
                if (port == 7432 || port == 7433) found.Add(new Listener { Port = port, Pid = (int)row.Pid, Address = new IPAddress(row.Address).ToString() });
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
    public static async Task<Probe> ProbeAsync(string worker, string data) {
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
            if (!names.SetEquals(new[] { "list_sessions", "register_agent", "send_message", "read_inbox",
                "acknowledge_message", "get_message_receipt", "reply_to_message" })) throw new IOException("MCP tool contract differed.");
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
        [AgentBrokerLocalInstallNative]::PublishMarkerFile($temporary, $Path, $Snapshot.Exists)
    }
    finally {
        [void](Assert-ContainedPath $temporary $parent)
        Assert-NoReparseComponents $temporary
        if ($temporaryCreated -and [IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
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

function Assert-HostRuntimeVersion([string]$Directory, [version]$Expected) {
    foreach ($name in @('DesktopAICompanion.exe', 'DesktopAICompanion.dll', 'DesktopAICompanion.Contracts.dll')) {
        $path = Assert-ContainedPath (Join-Path $Directory $name) $Directory
        if (Get-ProtectedEntryPattern $path) { throw 'A versioned host path matched a protected component.' }
        Assert-NoReparseComponents $path
        if ([version][Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion -ne $Expected) {
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
        Changed = [Collections.Generic.List[object]]::new(); Applied = $false; RollbackIncomplete = $false }
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
    for ($index = $Plan.Changed.Count - 1; $index -ge 0; $index--) {
        $record = $Plan.Changed[$index]
        $current = Get-HostRuntimeFileSnapshot $record.Target
        if ($current.Hash -cne $record.Hash) { throw 'A changed host runtime destination cannot be safely rolled back automatically.' }
        if ($record.OldExists) {
            $backup = Assert-ContainedPath (Join-Path $Plan.Previous $record.Name) $Plan.Previous
            if ((Get-HostRuntimeFileSnapshot $backup).Hash -cne $record.OldHash) { throw 'A runtime rollback copy failed verification.' }
            [AgentBrokerLocalInstallNative]::PublishMarkerFile($backup, $record.Target, $true)
        } else { [IO.File]::Delete($record.Target) }
    }
    $Plan.Changed.Clear()
    $Plan.Applied = $false
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
            if ($record.OldExists) {
                $backup = Assert-ContainedPath (Join-Path $Plan.Previous $record.Name) $Plan.Previous
                Assert-NoReparseComponents $backup
                if (Test-Path -LiteralPath $backup) { throw 'A unique host runtime rollback destination unexpectedly exists.' }
                [IO.File]::Replace($source, $record.Target, $backup)
            } else { [IO.File]::Move($source, $record.Target, $false) }
            $Plan.Changed.Add($record)
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
    } finally {
        foreach ($plan in $plans) { Close-HostRuntimePlan $plan }
        $files = [Collections.Generic.List[string]]::new()
        foreach ($name in $names) { $files.Add((Join-Path $source $name)); $files.Add((Join-Path $destination $name)) }
        $files.Add((Join-Path $destination 'settings.json')); $files.Add((Join-Path $previous 'preserved.bin'))
        if ($archived) { $files.Add((Join-Path $archived 'preserved.bin')) }
        foreach ($plan in $plans) {
            if ($plan.Stage) { foreach ($name in $names) { $files.Add((Join-Path $plan.New $name)); $files.Add((Join-Path $plan.Previous $name)) } }
        }
        foreach ($file in $files) {
            [void](Assert-ContainedPath $file $root); Assert-NoReparseComponents $file
            if ([IO.File]::Exists($file)) { [IO.File]::Delete($file) }
        }
        foreach ($plan in $plans) {
            if ($plan.Stage) { foreach ($folder in @($plan.New, $plan.Previous, $plan.Stage)) {
                [void](Assert-ContainedPath $folder $root); Assert-NoReparseComponents $folder
                if ([IO.Directory]::Exists($folder)) { [IO.Directory]::Delete($folder, $false) }
            } }
        }
        foreach ($folder in @($source, $destination, $previous, $archived)) {
            if ($folder) { [void](Assert-ContainedPath $folder $root); Assert-NoReparseComponents $folder
                if ([IO.Directory]::Exists($folder)) { [IO.Directory]::Delete($folder, $false) } }
        }
        [void](Assert-ContainedPath $root $Parent); Assert-NoReparseComponents $root
        [IO.Directory]::Delete($root, $false)
    }
}

if ($SelfTest) {
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
    try {
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

        [void][IO.Directory]::CreateDirectory($newSettings)
        [IO.File]::WriteAllText((Join-Path $newSettings 'settings.json'), '{"enabled":"true"}', [Text.UTF8Encoding]::new($false))
        [IO.Directory]::Move($newSettings, $publishedData)
        if ([IO.Directory]::Exists($newSettings) -or -not [IO.File]::Exists((Join-Path $publishedData 'settings.json'))) {
            throw 'Atomic new-settings publication test failed.'
        }
        $checks++
        [void][IO.Directory]::CreateDirectory($collidingSettings)
        [void][IO.Directory]::CreateDirectory($occupiedData)
        [IO.File]::WriteAllText((Join-Path $collidingSettings 'settings.json'), '{"enabled":"true"}', [Text.UTF8Encoding]::new($false))
        $refused = $false
        try { [IO.Directory]::Move($collidingSettings, $occupiedData) } catch { $refused = $true }
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
    }
    finally {
        foreach ($file in @($hardLink, $original, $markerLink, $marker, $payloadLink, (Join-Path $newSettings 'settings.json'),
                            (Join-Path $publishedData 'settings.json'), (Join-Path $collidingSettings 'settings.json'),
                            (Join-Path $publishedStage 'settings.json'), (Join-Path $applicationStage 'payload.bin'),
                            (Join-Path $expectedPayload 'AgentBroker.dll'), (Join-Path $expectedPayload 'worker\AgentBroker.Worker.dll'),
                            (Join-Path $installedPayload 'AgentBroker.dll'), (Join-Path $installedPayload 'worker\AgentBroker.Worker.dll'))) {
            [void](Assert-ContainedPath $file $testRoot)
            Assert-NoReparseComponents $file
            if ([IO.File]::Exists($file)) { [IO.File]::Delete($file) }
        }
        foreach ($folder in @($newSettings, $publishedData, $collidingSettings, $occupiedData, $publishedStage, $replacedStage,
                              $applicationStage, (Join-Path $expectedPayload 'worker'), (Join-Path $installedPayload 'worker'),
                              $expectedPayload, $installedPayload)) {
            [void](Assert-ContainedPath $folder $testRoot)
            Assert-NoReparseComponents $folder
            if ([IO.Directory]::Exists($folder)) { [IO.Directory]::Delete($folder, $false) }
        }
        [void](Assert-ContainedPath $testRoot $testArtifacts)
        Assert-NoReparseComponents $testRoot
        [IO.Directory]::Delete($testRoot, $false)
    }
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
$hostVersion = [version][Diagnostics.FileVersionInfo]::GetVersionInfo($companionExe).FileVersion
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
        Write-Host "StageUpdateMode=$([bool]$StageUpdate)"
        Write-Host "HostRuntimeInputsVerified=$([bool]($null -ne $runtimePlan))"
        Write-Host "ArchivePreviousCopyRequested=$([bool]$ArchivePreviousCopy)"
        if ($StageUpdate) { Write-Host 'ExistingDataUnopened=True'; Write-Host "UpdateDestinationsAbsent=$(-not (Test-Path -LiteralPath $updateReplaced))" }
        else { Write-Host 'DestinationAbsent=True'; Write-Host 'SettingsAbsent=True' }
        Write-Host 'ExternalWrites=False'
        return
    }
    if ($CompanionProcessId -le 1) { throw 'Actual installation requires the exact running companion PID.' }

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
        $initialListeners = [AgentBrokerLocalInstallNative]::Listeners()
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
            if ($initialListeners.Length -ne 2 -or $initialListeners[0].Pid -ne $initialListeners[1].Pid -or
                @($initialListeners.Where({ $_.Port -eq 7432 -and $_.Address -eq '127.0.0.1' })).Count -ne 1 -or
                @($initialListeners.Where({ $_.Port -eq 7433 })).Count -ne 1) { throw 'The existing broker listener pair was not unique.' }
            $previousWorker = [Diagnostics.Process]::GetProcessById($initialListeners[0].Pid)
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
                if ($previousWorker.HasExited -and [AgentBrokerLocalInstallNative]::Listeners().Length -eq 0) { break }
                Start-Sleep -Milliseconds 200
            }
            if (-not $previousWorker.HasExited -or [AgentBrokerLocalInstallNative]::Listeners().Length -ne 0) {
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
        [IO.Directory]::Move($dataStage, $moduleData)
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
            $listeners = [AgentBrokerLocalInstallNative]::Listeners()
            if ($listeners.Length -eq 2 -and @($listeners.Where({ $_.Port -eq 7432 -and $_.Address -eq '127.0.0.1' })).Count -eq 1 -and
                @($listeners.Where({ $_.Port -eq 7433 })).Count -eq 1 -and $listeners[0].Pid -eq $listeners[1].Pid) {
                $candidate = [Diagnostics.Process]::GetProcessById($listeners[0].Pid)
                try {
                    if ([AgentBrokerLocalInstallNative]::Image($candidate).Equals($workerExe, [StringComparison]::OrdinalIgnoreCase)) { $worker = $candidate; break }
                } finally { if ($null -eq $worker) { $candidate.Dispose() } }
                throw 'Required listener ownership differed from the installed worker.'
            }
            Start-Sleep -Milliseconds 500
        }
        if ($null -eq $worker) { throw 'The installed worker did not own both required listeners within the readiness deadline.' }
        try {
            $workerCreated = [AgentBrokerLocalInstallNative]::Created($worker)
            $probe = [AgentBrokerLocalInstallNative]::ProbeAsync($workerExe, $moduleData).GetAwaiter().GetResult()
            $listeners = [AgentBrokerLocalInstallNative]::Listeners()
            if ($worker.HasExited -or [AgentBrokerLocalInstallNative]::Created($worker) -ne $workerCreated -or
                $listeners.Length -ne 2 -or @($listeners.Where({ $_.Pid -ne $worker.Id })).Count -ne 0 -or
                @($listeners.Where({ $_.Port -eq 7432 -and $_.Address -eq '127.0.0.1' })).Count -ne 1 -or
                @($listeners.Where({ $_.Port -eq 7433 })).Count -ne 1) {
                throw 'Worker listener ownership changed during the MCP readiness probe.'
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
        if (-not $readyCompleted -and $companionStopped -and $runtimeLaunchSafe -and
            ($null -eq $runtimePlan -or -not $runtimePlan.RollbackIncomplete) -and ($null -eq $relaunched -or $relaunched.HasExited)) {
            try {
                Assert-NoReparseComponents $companionExe
                $recovery = Start-Process -FilePath $companionExe -WorkingDirectory $installRoot -WindowStyle Hidden -PassThru
                $recovery.Dispose()
                Write-Host 'CompanionRecoveryStarted=True'
            } catch { Write-Host 'CompanionRecoveryStarted=False' }
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
