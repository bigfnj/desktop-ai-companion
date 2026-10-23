using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using AgentBroker.ClaudeAdapter;

[assembly: InternalsVisibleTo("AgentBroker.Worker.Tests")]

namespace AgentBroker.Worker.Adapters;

// This internal boundary supplies public discovery inputs for deterministic provider tests. It has
// no credential or native-message operations, and production has no caller-supplied source path.
internal interface IClaudeDiscoverySource
{
    IReadOnlyList<int> GetProcessIds();
    NativeSessionMetadata ReadMetadata(int processId);
    IClaudeDiscoveryProcess OpenProcess(int processId);
    void RejectReparseComponents(string image);
    string? ReadProductVersion(string image);
    string ReadImageSha256(string image);
}

internal interface IClaudeDiscoveryProcess : IDisposable
{
    string ProcessName { get; }
    string? ImagePath { get; }
    ulong StartFileTime { get; }
}

internal sealed class WindowsClaudeDiscoverySource : IClaudeDiscoverySource
{
    public IReadOnlyList<int> GetProcessIds()
    {
        var processes = Process.GetProcessesByName("claude");
        try { return processes.Select(process => process.Id).ToArray(); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public NativeSessionMetadata ReadMetadata(int processId) => NativeMetadataReader.ReadDefaultHomeProcess(processId);
    public IClaudeDiscoveryProcess OpenProcess(int processId) => new PublicProcess(Process.GetProcessById(processId));
    public void RejectReparseComponents(string image) => CredentialPathPolicy.RejectReparseComponents(image, File.GetAttributes);
    public string? ReadProductVersion(string image) => FileVersionInfo.GetVersionInfo(image).ProductVersion;
    public string ReadImageSha256(string image)
    {
        using var input = File.OpenRead(image);
        string hash = Convert.ToHexString(SHA256.HashData(input));
        return hash;
    }

    private sealed class PublicProcess(Process process) : IClaudeDiscoveryProcess
    {
        public string ProcessName => process.ProcessName;
        public string? ImagePath => process.MainModule?.FileName;
        // This is the unchanged native FILETIME conversion, not DateTime.Ticks.
        public ulong StartFileTime => checked((ulong)process.StartTime.ToUniversalTime().ToFileTimeUtc());
        public void Dispose() => process.Dispose();
    }
}
