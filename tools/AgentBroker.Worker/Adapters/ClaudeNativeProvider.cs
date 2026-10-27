using AgentBroker.ClaudeAdapter;

namespace AgentBroker.Worker.Adapters;

/// <summary>Public process/image metadata discovery. No credential source is instantiated by discovery.</summary>
public sealed class ClaudeNativeProvider : IClaudeNativeProvider
{
    private readonly Dictionary<(int Pid, ulong Started, string Path), EngineProfile> profiles = [];
    private readonly IClaudeDiscoverySource source;
    private readonly bool enablePeerCredentials;
    private readonly Func<string?> pinDocumentError;
    private IReadOnlyList<AdapterDiscoveryDiagnostic> discoveryDiagnostics = Array.Empty<AdapterDiscoveryDiagnostic>();
    private bool disposed;

    public ClaudeNativeProvider(bool enablePeerCredentials = false)
        : this(new WindowsClaudeDiscoverySource(), enablePeerCredentials) { }

    internal ClaudeNativeProvider(IClaudeDiscoverySource source, bool enablePeerCredentials = false, Func<string?>? pinDocumentError = null)
    {
        this.source = source; this.enablePeerCredentials = enablePeerCredentials;
        this.pinDocumentError = pinDocumentError ?? (() => EngineProfile.PinDocumentError);
    }

    public IReadOnlyList<AdapterDiscoveryDiagnostic> DiscoveryDiagnostics => discoveryDiagnostics;

    public async Task<IReadOnlyList<ClaudeNativeCandidate>> DiscoverAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var found = new List<ClaudeNativeCandidate>();
        var diagnostics = new List<AdapterDiscoveryDiagnostic>();
        // A refused pin document verifies no build (EngineProfile.Verified is empty), so every session below is at
        // most a read-only observation. Say why first, on every pass, rather than only "not a verified build".
        if (pinDocumentError() is { } refused)
            diagnostics.Add(new("pin_document_invalid", "AgentBroker's pin document was refused (" + refused
                + "); no Claude Code build is verified, so no session can be woken until an AgentBroker module update replaces it."));
        var live = new HashSet<int>();
        foreach (int processId in source.GetProcessIds().Order().Take(32))
        {
            token.ThrowIfCancellationRequested(); live.Add(processId);
            var candidate = await RefreshCoreAsync(processId, diagnostics, token);
            if (candidate is not null) found.Add(candidate);
        }
        foreach (var key in profiles.Keys.Where(key => !live.Contains(key.Pid)).ToArray()) profiles.Remove(key);
        // Publish only a completed pass. A stale process cannot hide a supported one, and a later
        // healthy pass replaces the entire snapshot rather than retaining historical refusals.
        discoveryDiagnostics = Array.AsReadOnly(diagnostics.Distinct().ToArray());
        return found;
    }

    public Task<ClaudeNativeCandidate?> RefreshAsync(int processId, CancellationToken token)
        => RefreshCoreAsync(processId, diagnostics: null, token);

    private Task<ClaudeNativeCandidate?> RefreshCoreAsync(int processId, List<AdapterDiscoveryDiagnostic>? diagnostics,
        CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        try
        {
            // Exact numeric JSON target only. No vendor list routine or directory/key enumeration.
            var metadata = source.ReadMetadata(processId);
            using var process = source.OpenProcess(processId);
            if (!string.Equals(process.ProcessName, "claude", StringComparison.OrdinalIgnoreCase)) return Task.FromResult<ClaudeNativeCandidate?>(null);
            string? image = process.ImagePath;
            if (image is null || !Path.IsPathFullyQualified(image) ||
                !string.Equals(Path.GetFileName(image), "claude.exe", StringComparison.OrdinalIgnoreCase)) return Task.FromResult<ClaudeNativeCandidate?>(null);
            // ToFileTimeUtc converts the native process creation timestamp; DateTime.Ticks is not used.
            ulong started = process.StartFileTime;
            if (started != metadata.StartFileTime) return Task.FromResult<ClaudeNativeCandidate?>(null);
            var key = (processId, started, Path.GetFullPath(image));
            if (!profiles.TryGetValue(key, out var profile))
            {
                source.RejectReparseComponents(image);
                // Public version metadata is only a cheap refusal filter. It never grants trust:
                // every supported candidate must still match the complete public executable hash.
                string? version = source.ReadProductVersion(image);
                if (!EngineProfile.IsSupportedPublicVersion(version))
                {
                    diagnostics?.Add(CompatibilityDiagnostic("unsupported_public_version", version));
                    return Task.FromResult(ObserveUnpinned(processId, metadata, process, image, started, version, diagnostics, token));
                }
                string hash = source.ReadImageSha256(image);
                profile = EngineProfile.Verified
                    .SingleOrDefault(candidate => string.Equals(candidate.Sha256, hash, StringComparison.OrdinalIgnoreCase));
                if (profile is null)
                {
                    diagnostics?.Add(CompatibilityDiagnostic("image_hash_mismatch", version));
                    return Task.FromResult(ObserveUnpinned(processId, metadata, process, image, started, version, diagnostics, token));
                }
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

    private ClaudeNativeCandidate? ObserveUnpinned(int processId, NativeSessionMetadata metadata,
        IClaudeDiscoveryProcess process, string image, ulong started, string? version,
        List<AdapterDiscoveryDiagnostic>? diagnostics, CancellationToken token)
    {
        // The default process implementation refuses this path. Exact-pin delivery remains separate.
        if (!process.IsCurrentUser || process.HasExited) return null;
        token.ThrowIfCancellationRequested();
        using var trust = source.ObserveUnpinnedPublicImage(image);
        token.ThrowIfCancellationRequested();
        if (trust.Trust != ClaudePublicImageTrust.ExpectedPublisher || trust.ImageSha256 is not { Length: 64 } hash ||
            !hash.All(Uri.IsHexDigit))
        {
            string code = trust.Trust switch
            {
                ClaudePublicImageTrust.UntrustedSignature => "public_image_signature_untrusted",
                ClaudePublicImageTrust.UnexpectedPublisher => "public_image_publisher_mismatch",
                _ => "public_image_trust_unavailable",
            };
            diagnostics?.Add(new(code, "Claude Code read-only observation refused: public image publisher trust was not established."));
            return null;
        }
        if (process.HasExited || !process.IsCurrentUser || process.StartFileTime != started ||
            !string.Equals(process.ImagePath, image, StringComparison.OrdinalIgnoreCase)) return null;
        bool plausible = metadata.HasPlausibleActivityTimestamp(DateTimeOffset.UtcNow);
        return new(new(metadata.CanonicalPipe, processId, image, hash, version ?? "unknown",
            started, metadata.SessionId), plausible ? metadata.Status : null)
        { Provenance = ClaudeDiscoveryProvenance.PublisherSignedObservation };
    }

    internal static AdapterDiscoveryDiagnostic CompatibilityDiagnostic(string code, string? version)
    {
        string installed = string.IsNullOrWhiteSpace(version) ? "unknown version" :
            new string(version.Where(character => !char.IsControl(character)).Take(64).ToArray());
        string verified = string.Join(", ", EngineProfile.Verified.Select(profile => profile.Version));
        // A build newer than every pin is the update case the owner asked the pane to name (lane broker-pins).
        if (code == "unsupported_public_version" && EngineProfile.IsNewerThanEveryVerified(version))
            return new(code, AdapterDiscoveryDiagnostic.NewerThanVerified("Claude Code", installed, verified));
        string refusal = code == "image_hash_mismatch" ? "; executable hash does not match a verified build" : "";
        return new(code, $"Claude Code {installed} installed{refusal}; verified {verified}.");
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

    public ValueTask DisposeAsync()
    { disposed = true; profiles.Clear(); discoveryDiagnostics = Array.Empty<AdapterDiscoveryDiagnostic>(); return ValueTask.CompletedTask; }

}
