using System.Diagnostics;
using System.Security.Cryptography;
using AgentBroker.ClaudeAdapter;

namespace AgentBroker.Worker.Adapters;

/// <summary>Public process/image metadata discovery. No credential source is instantiated by discovery.</summary>
public sealed class ClaudeNativeProvider(bool enablePeerCredentials = false) : IClaudeNativeProvider
{
    private readonly Dictionary<(int Pid, ulong Started, string Path), EngineProfile> profiles = [];
    private bool disposed;

    public async Task<IReadOnlyList<ClaudeNativeCandidate>> DiscoverAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var found = new List<ClaudeNativeCandidate>();
        var live = new HashSet<int>();
        var processes = Process.GetProcessesByName("claude");
        try
        {
            foreach (var process in processes.OrderBy(p => p.Id).Take(32))
            {
                token.ThrowIfCancellationRequested(); live.Add(process.Id);
                var candidate = await RefreshAsync(process.Id, token);
                if (candidate is not null) found.Add(candidate);
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        foreach (var key in profiles.Keys.Where(key => !live.Contains(key.Pid)).ToArray()) profiles.Remove(key);
        return found;
    }

    public Task<ClaudeNativeCandidate?> RefreshAsync(int processId, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        try
        {
            // Exact numeric JSON target only. No vendor list routine or directory/key enumeration.
            var metadata = NativeMetadataReader.ReadDefaultHomeProcess(processId);
            using var process = Process.GetProcessById(processId);
            if (!string.Equals(process.ProcessName, "claude", StringComparison.OrdinalIgnoreCase)) return Task.FromResult<ClaudeNativeCandidate?>(null);
            string? image = process.MainModule?.FileName;
            if (image is null || !Path.IsPathFullyQualified(image) ||
                !string.Equals(Path.GetFileName(image), "claude.exe", StringComparison.OrdinalIgnoreCase)) return Task.FromResult<ClaudeNativeCandidate?>(null);
            // ToFileTimeUtc converts the native process creation timestamp; DateTime.Ticks is not used.
            ulong started = checked((ulong)process.StartTime.ToUniversalTime().ToFileTimeUtc());
            if (started != metadata.StartFileTime) return Task.FromResult<ClaudeNativeCandidate?>(null);
            var key = (processId, started, Path.GetFullPath(image));
            if (!profiles.TryGetValue(key, out var profile))
            {
                CredentialPathPolicy.RejectReparseComponents(image, File.GetAttributes);
                // Public version metadata is only a cheap refusal filter. It never grants trust:
                // every supported candidate must still match the complete public executable hash.
                string? version = FileVersionInfo.GetVersionInfo(image).ProductVersion;
                if (!EngineProfile.IsSupportedPublicVersion(version)) return Task.FromResult<ClaudeNativeCandidate?>(null);
                using var input = File.OpenRead(image);
                string hash = Convert.ToHexString(SHA256.HashData(input));
                profile = EngineProfile.Verified
                    .SingleOrDefault(candidate => string.Equals(candidate.Sha256, hash, StringComparison.OrdinalIgnoreCase));
                if (profile is null) return Task.FromResult<ClaudeNativeCandidate?>(null);
                if (profiles.Count >= 32) profiles.Remove(profiles.Keys.First());
                profiles[key] = profile;
            }
            token.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            bool plausible = metadata.HasPlausibleActivityTimestamp(now);
            return Task.FromResult<ClaudeNativeCandidate?>(new(new(metadata.CanonicalPipe, processId, image,
                profile.Sha256, profile.Version, started, metadata.SessionId), plausible ? metadata.Status : null));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or OverflowException)
        { return Task.FromResult<ClaudeNativeCandidate?>(null); }
    }

    public Task<SubmissionResult> SubmitAsync(NativeTarget target, NativeMessage message, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!enablePeerCredentials)
            return Task.FromResult(new SubmissionResult(SubmissionStatus.Refused, "claude_credentials_disabled"));
        // This is the sole production credential boundary. An agent must obtain exact H05/W06 platform
        // approval before executing this path. Normal discovery/tests never construct or invoke it.
        var adapter = new NativePeerAdapter(new WindowsNativePeerConnectionFactory(), new ProtectedPeerCredentialSource());
        return adapter.SendAsync(target, message, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), cancellation: token);
    }

    public ValueTask DisposeAsync() { disposed = true; profiles.Clear(); return ValueTask.CompletedTask; }

}
