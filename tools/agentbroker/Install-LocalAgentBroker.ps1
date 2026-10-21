#requires -Version 7
<#
.SYNOPSIS
    Install a verified AgentBroker ZIP for the current user and restart only its installed companion.
.DESCRIPTION
    Run ValidateOnly first. Actual installation requires the caller's approval for the displayed
    script invocation, the current user's application folders, companion restart and broker identity
    creation. The helper refuses existing module/settings paths. It never changes client MCP
    configuration, enables native delivery, changes firewall rules, or restarts an editor.
    Before actual installation, run scripts-utilities/scripts/test-host-projection.ps1 and use
    an unprojected shell if that check reports projection.
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Install-LocalAgentBroker.ps1 -ZipPath modules-dist/agentbroker.zip -ExpectedSha256 <catalog SHA256> -ValidateOnly
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Install-LocalAgentBroker.ps1 -ZipPath modules-dist/agentbroker.zip -ExpectedSha256 <catalog SHA256> -CompanionProcessId <exact PID>
#>
[CmdletBinding()]
param(
    [string]$ZipPath,
    [string]$ExpectedSha256,
    [int]$CompanionProcessId,
    [switch]$ValidateOnly,
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

    public static void VerifySingleLink(FileStream file) {
        if (!GetFileInformationByHandle(file.SafeFileHandle, out var identity)) throw new IOException("Archive file identity unavailable.");
        if (identity.Links != 1 || (identity.Attributes & (0x10 | 0x400)) != 0)
            throw new IOException("Archive input must be one ordinary file with one link.");
    }
    public static void CreateTestHardLink(string link, string existing) {
        if (!CreateHardLink(link, existing, IntPtr.Zero)) throw new Win32Exception();
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

if ($SelfTest) {
    $checks = 0
    foreach ($name in @('AgentBroker.dll', 'worker/AgentBroker.Worker.exe', 'worker/licenses/THIRD-PARTY-NOTICES.txt')) {
        [void](Assert-EntryName $name); $checks++
    }
    foreach ($name in @('../escape.dll', '/absolute.dll', 'worker//bad.dll', 'worker/name. ', 'worker/CON.txt',
                        'worker/file:stream', 'worker\wrong.dll', '.env.settings', 'worker/a.env', '.npmrc',
                        '.git/credentials', 'a.pem', 'a.key', 'a.p12', 'a.pfx', 'a.keystore', 'PrivateKeyFixture/code.cs')) {
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
    }
    finally {
        foreach ($file in @($hardLink, $original, (Join-Path $newSettings 'settings.json'),
                            (Join-Path $publishedData 'settings.json'), (Join-Path $collidingSettings 'settings.json'))) {
            [void](Assert-ContainedPath $file $testRoot)
            Assert-NoReparseComponents $file
            if ([IO.File]::Exists($file)) { [IO.File]::Delete($file) }
        }
        foreach ($folder in @($newSettings, $publishedData, $collidingSettings, $occupiedData)) {
            [void](Assert-ContainedPath $folder $testRoot)
            Assert-NoReparseComponents $folder
            if ([IO.Directory]::Exists($folder)) { [IO.Directory]::Delete($folder, $false) }
        }
        [void](Assert-ContainedPath $testRoot $testArtifacts)
        Assert-NoReparseComponents $testRoot
        [IO.Directory]::Delete($testRoot, $false)
    }
    Write-Host "PathPolicyChecks=$checks"
    Write-Host 'PayloadFilesOpened=False'
    Write-Host 'SyntheticFixturesCleaned=True'
    return
}

if (-not $IsWindows) { throw 'This helper supports Windows user installations only.' }
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
foreach ($target in @($moduleDirectory, $moduleData, $companionExe)) { Assert-NoReparseComponents $target }
if (-not [IO.File]::Exists($companionExe) -or [IO.File]::Exists((Join-Path $installRoot 'DesktopAICompanion.portable'))) {
    throw 'The supported current-user installed companion was not found.'
}
$hostVersion = [version][Diagnostics.FileVersionInfo]::GetVersionInfo($companionExe).FileVersion
if ($hostVersion -lt [version]'1.6.0.0') { throw 'AgentBroker requires installed host 1.6.0 or later.' }
if (Test-Path -LiteralPath $moduleDirectory) { throw 'AgentBroker is already installed; no existing installation was changed.' }
if (Test-Path -LiteralPath $settingsPath) { throw 'AgentBroker settings already exist; no existing settings were read or changed.' }
if (Test-Path -LiteralPath $moduleData) { throw 'AgentBroker data already exists; this first-install helper does not alter it.' }

$zipStream = [IO.File]::Open($zipFull, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
$archive = $null
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
    if ($ValidateOnly) {
        Write-Host 'ArchiveVerified=True'
        Write-Host "ArchiveEntryCount=$($archive.Entries.Count)"
        Write-Host "ExpandedBytes=$expanded"
        Write-Host 'HostVersionVerified=True'
        Write-Host 'DestinationAbsent=True'
        Write-Host 'SettingsAbsent=True'
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
    try {
        if (-not [AgentBrokerLocalInstallNative]::Image($companion).Equals($companionExe, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The supplied PID does not own the supported installed companion executable.'
        }
        $companionCreated = [AgentBrokerLocalInstallNative]::Created($companion)
        if ([AgentBrokerLocalInstallNative]::Listeners().Length -ne 0) { throw 'A required broker port is already listening; nothing was installed.' }
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
        [AgentBrokerLocalInstallNative]::CloseLifetime($companion, $companionCreated)
        Assert-NoReparseComponents $companionExe
        $relaunched = Start-Process -FilePath $companionExe -WorkingDirectory $installRoot -WindowStyle Hidden -PassThru
        $relaunched.Dispose()
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
            Write-Host 'ListenerOwnersVerified=True'
            Write-Host "McpInitialized=$($probe.Initialized)"
            Write-Host "McpToolCount=$($probe.ToolCount)"
            Write-Host "ProbeExitedCleanly=$($probe.CleanExit)"
            Write-Host 'Ready=True'
        } finally { $worker.Dispose() }
    }
    finally {
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
    if ($archive) { $archive.Dispose() }
    $zipStream.Dispose()
}
