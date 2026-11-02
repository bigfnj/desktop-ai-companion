using AgentBroker.ClaudeAdapter;

namespace AgentBroker.Worker.Adapters;

/// <summary>Public process/image metadata discovery. No credential source is instantiated by discovery.</summary>
public sealed class ClaudeNativeProvider : IClaudeNativeProvider
{
    private readonly Dictionary<(int Pid, ulong Started, string Path), EngineProfile> profiles = [];
    private readonly IClaudeDiscoverySource source;
    private readonly bool enablePeerCredentials;
    // Update-tolerant delivery (the owner's decision of 2026-11-02): effective only with credentials, as Codex's is with delivery.
    private readonly bool updateTolerant;
    private readonly Func<string?> pinDocumentError;
    private IReadOnlyList<AdapterDiscoveryDiagnostic> discoveryDiagnostics = Array.Empty<AdapterDiscoveryDiagnostic>();
    // Sessions this pass found but could not open (see RefreshCoreAsync). Discovery runs one pass at a time.
    private int unopenableThisPass;
    private const int ErrorAccessDenied = 5;
    private bool disposed;

    public ClaudeNativeProvider(bool enablePeerCredentials = false, bool updateTolerantDelivery = false)
        : this(new WindowsClaudeDiscoverySource(), enablePeerCredentials, updateTolerantDelivery: updateTolerantDelivery) { }

    internal ClaudeNativeProvider(IClaudeDiscoverySource source, bool enablePeerCredentials = false, Func<string?>? pinDocumentError = null,
        bool updateTolerantDelivery = false)
    {
        this.source = source; this.enablePeerCredentials = enablePeerCredentials;
        updateTolerant = enablePeerCredentials && updateTolerantDelivery;
        this.pinDocumentError = pinDocumentError ?? (() => EngineProfile.PinDocumentError);
    }

    public IReadOnlyList<AdapterDiscoveryDiagnostic> DiscoveryDiagnostics => discoveryDiagnostics;

    /// <summary>The publisher check NativePeerAdapter runs on the connected image before a send to an unpinned build; null
    /// (exact pins only) unless update-tolerant delivery is in effect. It observes through the same source as discovery.</summary>
    internal AgentBroker.ClaudeAdapter.IPublisherImageTrust? DeliveryImageTrust
        => updateTolerant ? new ClaudePublisherImageTrust(source.ObserveUnpinnedPublicImage) : null;

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
        unopenableThisPass = 0;
        foreach (int processId in source.GetProcessIds().Order().Take(32))
        {
            token.ThrowIfCancellationRequested(); live.Add(processId);
            var candidate = await RefreshCoreAsync(processId, diagnostics, token);
            if (candidate is not null) found.Add(candidate);
        }
        // Counted, never named (no process ID, session ID or path): such a session used to be skipped in silence, so its
        // owner saw no row and no reason (N-broker-elevated-caller-01).
        if (unopenableThisPass > 0)
            // A note, not an adapter failure (review of 25a0c65, finding 5): the other sessions are healthy, so the pane keeps
            // their wake line and shows this beside it. At most 116 characters for any count up to the 32-process bound, so it
            // fits a 120-character tray label with its marker (the plural once ran to 117 and lost its final period).
            diagnostics.Add(new("claude_session_elevated", unopenableThisPass + " Claude Code session" + (unopenableThisPass == 1
                ? " is elevated or another user's and cannot be woken; use register_agent or run unelevated."
                : "s are elevated or another user's and cannot be woken; use register_agent or run unelevated."))
            { Note = true });
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
        bool identifying = false;
        try
        {
            // Exact numeric JSON target only. No vendor list routine or directory/key enumeration.
            var metadata = source.ReadMetadata(processId);
            identifying = true;
            using var process = source.OpenProcess(processId);
            if (!string.Equals(process.ProcessName, "claude", StringComparison.OrdinalIgnoreCase)) return Task.FromResult<ClaudeNativeCandidate?>(null);
            string? image = process.ImagePath;
            if (image is null || !Path.IsPathFullyQualified(image) ||
                !string.Equals(Path.GetFileName(image), "claude.exe", StringComparison.OrdinalIgnoreCase)) return Task.FromResult<ClaudeNativeCandidate?>(null);
            // The native creation FILETIME comes from the same retained limited-query process handle.
            ulong started = process.StartFileTime;
            identifying = false;
            if (started != metadata.StartFileTime) return Task.FromResult<ClaudeNativeCandidate?>(null);
            var key = (processId, started, Path.GetFullPath(image));
            if (!profiles.TryGetValue(key, out var profile))
            {
                source.RejectReparseComponents(image);
                // Public version metadata is only a cheap refusal filter. It never grants trust:
                // every supported candidate must still match the complete public executable hash.
                string? version = source.ReadProductVersion(image);
                if (!EngineProfile.IsSupportedPublicVersion(version))
                    return Task.FromResult(Unpinned("unsupported_public_version", processId, metadata, process, image, started, version, diagnostics, token));
                string hash = source.ReadImageSha256(image);
                profile = EngineProfile.Verified
                    .SingleOrDefault(candidate => string.Equals(candidate.Sha256, hash, StringComparison.OrdinalIgnoreCase));
                if (profile is null)
                    return Task.FromResult(Unpinned("image_hash_mismatch", processId, metadata, process, image, started, version, diagnostics, token));
                if (profiles.Count >= 32) profiles.Remove(profiles.Keys.First());
                profiles[key] = profile;
            }
            token.ThrowIfCancellationRequested();
            // Pinned/cache discovery also re-queries the kernel image, not a stale module-list name.
            if (process.StartFileTime != started ||
                !string.Equals(process.ImagePath, image, StringComparison.Ordinal))
                return Task.FromResult<ClaudeNativeCandidate?>(null);
            var now = DateTimeOffset.UtcNow;
            bool plausible = metadata.HasPlausibleActivityTimestamp(now);
            return Task.FromResult<ClaudeNativeCandidate?>(new(new(metadata.CanonicalPipe, processId, image,
                profile.Sha256, profile.Version, started, metadata.SessionId), plausible ? metadata.Status : null));
        }
        // A record in this user's home identifies a possible local session, but its process must still be admitted.
        // KernelProcessImage rejects elevated tokens explicitly with error 5 even when the limited open succeeds;
        // unavailable elevation or denied image/start identity reads also fail closed here. Such sessions are counted
        // and skipped before a candidate can be published. Metadata reads and later image-file/hash denials are not
        // counted as process elevation, so an unrelated unavailable file cannot hide the healthy sessions' wake state.
        catch (System.ComponentModel.Win32Exception ex) when (identifying && diagnostics is not null && ex.NativeErrorCode == ErrorAccessDenied)
        { unopenableThisPass++; return Task.FromResult<ClaudeNativeCandidate?>(null); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or OverflowException)
        { return Task.FromResult<ClaudeNativeCandidate?>(null); }
    }

    private ClaudeNativeCandidate? Unpinned(string pinRefusal, int processId, NativeSessionMetadata metadata,
        IClaudeDiscoveryProcess process, string image, ulong started, string? version,
        List<AdapterDiscoveryDiagnostic>? diagnostics, CancellationToken token)
    {
        int reportAt = diagnostics?.Count ?? 0;
        ClaudeNativeCandidate? observed = null;
        try { return observed = ObserveUnpinned(processId, metadata, process, image, started, version, diagnostics, token); }
        finally
        {
            // The failed exact pin stays the adapter's error, first, as before, unless update-tolerant delivery carries this
            // session. Then it is a note (the owner, 2026-11-02: an end user should not have to know), not a failure.
            if (observed?.Provenance == ClaudeDiscoveryProvenance.UpdateTolerant) diagnostics?.Add(UpdateTolerantNote(version));
            else diagnostics?.Insert(reportAt, CompatibilityDiagnostic(pinRefusal, version));
        }
    }

    internal static AdapterDiscoveryDiagnostic UpdateTolerantNote(string? version)
        => new("claude_update_tolerant_delivery", "Claude Code " + Installed(DisplayVersion(version)) +
            " is not a reviewed build; it is woken because Windows verified Anthropic's signature.") { Note = true };

    // The version as people see it: "2.1.314", not the file version "2.1.314.0". A zero fourth part is dropped only when
    // the first three parts parse; a non-zero one, or anything that does not parse, is kept as it was.
    internal static string? DisplayVersion(string? version)
        => System.Version.TryParse(version, out var parsed) && parsed.Revision == 0
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{parsed.Major}.{parsed.Minor}.{parsed.Build}") : version;

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
            !string.Equals(process.ImagePath, image, StringComparison.Ordinal)) return null;
        bool plausible = metadata.HasPlausibleActivityTimestamp(DateTimeOffset.UtcNow);
        // Update-tolerant delivery makes the same signed, identity-checked observation deliverable. NativePeerAdapter checks the
        // publisher again on the connected pipe server's own image, and its hash against this one, before any credential read.
        return new(new(metadata.CanonicalPipe, processId, image, hash, version ?? "unknown",
            started, metadata.SessionId), plausible ? metadata.Status : null)
        { Provenance = updateTolerant ? ClaudeDiscoveryProvenance.UpdateTolerant : ClaudeDiscoveryProvenance.PublisherSignedObservation };
    }

    private static string Installed(string? version) => string.IsNullOrWhiteSpace(version) ? "unknown version" :
        new string(version.Where(character => !char.IsControl(character)).Take(64).ToArray());

    internal static AdapterDiscoveryDiagnostic CompatibilityDiagnostic(string code, string? version)
    {
        string installed = Installed(version);
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
        var adapter = new NativePeerAdapter(new WindowsNativePeerConnectionFactory(), new ProtectedPeerCredentialSource(), DeliveryImageTrust);
        return adapter.SendAsync(target, message, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), cancellation: token);
    }

    public ValueTask DisposeAsync()
    { disposed = true; profiles.Clear(); discoveryDiagnostics = Array.Empty<AdapterDiscoveryDiagnostic>(); return ValueTask.CompletedTask; }

}
