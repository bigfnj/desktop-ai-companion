using AgentBroker.CodexDesktopAdapter;

namespace AgentBroker.Worker.Adapters;

/// <summary>Uses the existing GUI owner and its native idle checks, without asserting a widget identity.</summary>
public sealed class CodexNativeProvider : ICodexNativeProvider
{
    private readonly bool enableDelivery;
    private readonly bool updateTolerant;
    private readonly Func<CancellationToken, Task<ICodexOwnerConnection>> connect;
    private readonly Action verifyProfiles;
    private readonly Action verifyProfilesByPublisher;
    // Discovery passes may reuse a publisher success for the same running engine processes (VsCodeCompatibilityProfile's
    // discovery variant); the exact-profile check stays fresh. Submission always uses the two fresh verifiers above, and a
    // failed fresh check forgets the reused success.
    private readonly Action verifyProfilesForDiscovery;
    private readonly Action verifyByPublisherForDiscovery;
    private readonly Action forgetDiscovery;
    private readonly Func<CancellationToken, Task<IReadOnlyList<DesktopDirectorySession>>> observe;
    private readonly Func<CancellationToken, Task<ICodexDirectoryObserver>> connectDirectory;
    private ICodexOwnerConnection? adapter;
    // Exact when every running Code-owned engine matched its reviewed profile; PublisherSigned only under the
    // opt-in update-tolerant policy; Unverified otherwise. Strict mode never reaches PublisherSigned.
    private DesktopRouterTrust profileTrust;
    // Unverified because the last engine check could not complete (an IO or access error), not because it found a
    // mismatch: the one Unverified that a retry can clear (N-agentbroker-refusal-transience-01).
    private bool profileUnavailable;
    private IReadOnlyList<AdapterDiscoveryDiagnostic> discoveryDiagnostics = Array.Empty<AdapterDiscoveryDiagnostic>();
    public bool NativeIdleGuard => enableDelivery && profileTrust != DesktopRouterTrust.Unverified && adapter?.NativeIdleGuard == true;
    // Delivery is on and only the broker's own state stands in the way: the profile check could not complete, or (with
    // a verified profile) the owner connection is down after a failed lookup, which the next lookup re-establishes. A
    // verified mismatch is a verdict and never pending.
    public bool VerificationPending => enableDelivery &&
        (profileTrust == DesktopRouterTrust.Unverified ? profileUnavailable : adapter is null);
    public IReadOnlyList<AdapterDiscoveryDiagnostic> DiscoveryDiagnostics => discoveryDiagnostics;

    public CodexNativeProvider(bool enableDelivery = false, bool updateTolerantDelivery = false)
        : this(enableDelivery, async token => new CodexOwnerConnection(await DesktopAdapter.ConnectAsync(
                // Discovery always admits a publisher-signed router for observation; its bindings deliver only
                // when update-tolerant delivery is also on, and the adapter library refuses them otherwise.
                new(MaximumMessageBytes: 32 * 1024, EnableExternalPeerDelivery: enableDelivery)
                { AllowPublisherSignedRouter = true, EnableUpdateTolerantDelivery = enableDelivery && updateTolerantDelivery }, token)),
            () => VsCodeCompatibilityProfile.VerifyRunningClients(),
            null, null, updateTolerantDelivery, () => VsCodeCompatibilityProfile.VerifyRunningClientsByPublisher(),
            () => VsCodeCompatibilityProfile.VerifyRunningClients(),
            () => VsCodeCompatibilityProfile.VerifyRunningClientsByPublisherForDiscovery(),
            VsCodeCompatibilityProfile.ForgetDiscoveryVerification) { }

    internal CodexNativeProvider(bool enableDelivery,
        Func<CancellationToken, Task<ICodexOwnerConnection>> connect, Action verifyProfiles,
        Func<CancellationToken, Task<IReadOnlyList<DesktopDirectorySession>>>? observe,
        Func<CancellationToken, Task<ICodexDirectoryObserver>>? connectDirectory = null,
        bool updateTolerantDelivery = false, Action? verifyProfilesByPublisher = null,
        Action? verifyProfilesForDiscovery = null, Action? verifyByPublisherForDiscovery = null, Action? forgetDiscovery = null)
    {
        this.enableDelivery = enableDelivery; this.connect = connect; this.verifyProfiles = verifyProfiles;
        // Update-tolerant delivery is meaningless without delivery, so it cannot be on by itself.
        updateTolerant = enableDelivery && updateTolerantDelivery;
        this.verifyProfilesByPublisher = verifyProfilesByPublisher ??
            (() => throw new DesktopAdapterException("unsupported_running_vscode_profile"));
        this.verifyProfilesForDiscovery = verifyProfilesForDiscovery ?? verifyProfiles;
        this.verifyByPublisherForDiscovery = verifyByPublisherForDiscovery ?? this.verifyProfilesByPublisher;
        this.forgetDiscovery = forgetDiscovery ?? (() => { });
        this.observe = observe ?? ObservePersistentAsync;
        this.connectDirectory = connectDirectory ?? (async token => new CodexDirectoryObserver(
            await DesktopDirectoryObserver.ConnectAsync(allowPublisherSignedRouter: true, token)));
    }

    private ICodexDirectoryObserver? directory;
    private async Task<IReadOnlyList<DesktopDirectorySession>> ObservePersistentAsync(CancellationToken token)
    {
        try { directory ??= await connectDirectory(token); return directory.Snapshot(); }
        catch
        {
            var retired = directory; directory = null;
            if (retired is not null) await retired.DisposeAsync();
            throw; // Next bounded discovery reconnects and performs full cryptographic image verification.
        }
    }

    public async Task<IReadOnlyList<DesktopDirectorySession>> ObserveAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var diagnostics = new List<AdapterDiscoveryDiagnostic>();
        profileTrust = DesktopRouterTrust.Unverified; profileUnavailable = false;
        if (enableDelivery)
        {
            try { verifyProfilesForDiscovery(); profileTrust = DesktopRouterTrust.ExactPinned; }
            catch (DesktopAdapterException error)
            {
                profileTrust = DesktopRouterTrust.Unverified;
                diagnostics.Add(CodexNativeAdapter.DescribeDiscoveryFailure(error));
                profileTrust = VerifyProfilesByPublisher(error, diagnostics, verifyByPublisherForDiscovery);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                profileTrust = DesktopRouterTrust.Unverified; profileUnavailable = true;
                diagnostics.Add(new("vscode_profile_unavailable",
                    $"Codex VS Code compatibility verification could not complete; verified extension {VsCodeCompatibilityProfile.VerifiedVersions}."));
            }
        }
        var observed = await observe(token);
        if (directory?.ExactPinRefusal is { } routerPin)
        {
            // A publisher-signed router is observed, never silently promoted: name the failed exact pin and the
            // mode on every pass, so the status says so for as long as the build stays unreviewed.
            diagnostics.Add(CodexNativeAdapter.DescribeDiscoveryFailure(routerPin));
            diagnostics.Add(ModeDiagnostic());
        }
        else if (profileTrust == DesktopRouterTrust.PublisherSigned) diagnostics.Add(ModeDiagnostic());
        // Nonfatal delivery-profile refusals must not discard read-only observations. Only a
        // completed pass replaces diagnostics, so a subsequent healthy pass clears the warning.
        discoveryDiagnostics = Array.AsReadOnly(diagnostics.Distinct().ToArray());
        return observed;
    }

    private AdapterDiscoveryDiagnostic ModeDiagnostic() => updateTolerant
        ? new("codex_update_tolerant_delivery",
            "Codex update-tolerant delivery is on: a publisher-signed build that is not exactly reviewed can receive messages.")
        : new("codex_observation_only",
            "Codex sessions on a publisher-signed build that is not exactly reviewed are listed read-only; delivery is refused.");

    // Strict mode stops at the exact refusal. Update-tolerant mode may stand in Windows-verified OpenAI engines for
    // an exact-pin mismatch only, never for an unverifiable process snapshot or identity failure.
    private DesktopRouterTrust VerifyProfilesByPublisher(DesktopAdapterException exact, List<AdapterDiscoveryDiagnostic>? diagnostics, Action verify)
    {
        if (!updateTolerant || !DesktopCompatibilityProfile.IsExactPinMismatch(exact.Code)) return DesktopRouterTrust.Unverified;
        try { verify(); return DesktopRouterTrust.PublisherSigned; }
        catch (DesktopAdapterException error)
        {
            diagnostics?.Add(CodexNativeAdapter.DescribeDiscoveryFailure(error));
            return DesktopRouterTrust.Unverified;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { profileUnavailable = true; return DesktopRouterTrust.Unverified; }
    }

    public bool IsDeliveryAuthorized(DesktopOwnerBinding binding)
        => Authorize(binding) is CodexDeliveryAuthority.ExactPinned or CodexDeliveryAuthority.UpdateTolerant;

    public CodexDeliveryAuthority Authorize(DesktopOwnerBinding binding)
    {
        // The reviewed path: exact router pin and exact running-client profiles, unchanged from strict mode.
        if (binding.RouterTrust == DesktopRouterTrust.ExactPinned && profileTrust == DesktopRouterTrust.ExactPinned)
            return NativeIdleGuard && binding.SupportsUntrustedAppInput ? CodexDeliveryAuthority.ExactPinned
                : Pending(binding) ? CodexDeliveryAuthority.VerificationUnavailable : CodexDeliveryAuthority.None;
        if (binding.RouterTrust == DesktopRouterTrust.Unverified) return CodexDeliveryAuthority.None;
        if (!updateTolerant)
            return binding.RouterTrust == DesktopRouterTrust.PublisherSigned ? CodexDeliveryAuthority.ObservationOnly
                : Pending(binding) ? CodexDeliveryAuthority.VerificationUnavailable : CodexDeliveryAuthority.None;
        // Update-tolerant: every component is exactly pinned or publisher-verified, and the owner itself still
        // advertises the native external-input capability. Anything less is an explicit unsupported capability, except a
        // broker-side check that could not complete for an owner that does advertise it, which is pending.
        return NativeIdleGuard && profileTrust != DesktopRouterTrust.Unverified && binding.SupportsUntrustedAppInput
            ? CodexDeliveryAuthority.UpdateTolerant
            : Pending(binding) ? CodexDeliveryAuthority.VerificationUnavailable : CodexDeliveryAuthority.UnsupportedCapability;
    }

    // Pending only for an owner that advertises the native capability: its absence is the owner's own verdict.
    private bool Pending(DesktopOwnerBinding binding) => binding.SupportsUntrustedAppInput && VerificationPending;

    public async Task<DesktopOwnerBinding?> ResolveOwnerAsync(Guid conversationId, CancellationToken token)
    {
        try
        {
            adapter ??= await connect(token);
            return await adapter.DiscoverAsync(conversationId, token);
        }
        catch
        {
            if (adapter is not null) await adapter.DisposeAsync();
            adapter = null; throw;
        }
    }

    public async Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token)
    {
        if (!IsDeliveryAuthorized(binding) || adapter is null)
            return new(DesktopSubmissionOutcome.Rejected, ErrorCode: "native_client_profile_unverified");
        // Advertisement uses only the verification from this discovery pass. Actual model input
        // always gets fresh full hashes and running-client validation; no metadata/hash cache is trusted.
        profileUnavailable = false;
        try { verifyProfiles(); profileTrust = DesktopRouterTrust.ExactPinned; }
        catch (DesktopAdapterException error) when (updateTolerant && DesktopCompatibilityProfile.IsExactPinMismatch(error.Code))
        {
            // The same fresh check under the opt-in policy: Windows-verified OpenAI engines, or no input at all.
            profileTrust = VerifyProfilesByPublisher(error, diagnostics: null, verifyProfilesByPublisher);
            if (!IsDeliveryAuthorized(binding))
            {
                forgetDiscovery(); // A fresh refusal disproves any reused discovery success.
                return Unverified();
            }
        }
        catch (Exception error) when (error is DesktopAdapterException or IOException or UnauthorizedAccessException)
        {
            profileTrust = DesktopRouterTrust.Unverified;
            profileUnavailable = error is not DesktopAdapterException;
            forgetDiscovery();
            return Unverified();
        }
        try { return await adapter.SubmitAsync(binding, messageId, text, token); }
        catch (DesktopAdapterException exception) when (exception.Code == "receipt_capacity")
        {
            // This exact library error precedes discovery and the first model-input write. Core owns
            // durable message dedup, so a new connection lease may safely admit this unsubmitted ID.
            var retired = adapter;
            adapter = null;
            try { await retired.DisposeAsync(); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            return new(DesktopSubmissionOutcome.DeferredNotSubmitted, ErrorCode: "native_receipt_capacity_reconnect");
        }
    }

    // Nothing was written either way. A fresh check that could not complete (IO or access) is transient and defers with its
    // own reason; a verdict (a mismatch, a wrong publisher) refuses as before (N-agentbroker-refusal-transience-01).
    private DesktopSubmissionResult Unverified() => profileUnavailable
        ? new(DesktopSubmissionOutcome.DeferredNotSubmitted, ErrorCode: "native_verification_unavailable")
        : new(DesktopSubmissionOutcome.Rejected, ErrorCode: "native_client_profile_unverified");

    public async ValueTask DisposeAsync()
    {
        profileTrust = DesktopRouterTrust.Unverified;
        discoveryDiagnostics = Array.Empty<AdapterDiscoveryDiagnostic>();
        var retiredDirectory = directory; directory = null;
        var retiredOwner = adapter; adapter = null;
        try { if (retiredDirectory is not null) await retiredDirectory.DisposeAsync(); }
        finally { if (retiredOwner is not null) await retiredOwner.DisposeAsync(); }
    }
}

internal interface ICodexDirectoryObserver : IAsyncDisposable
{
    IReadOnlyList<DesktopDirectorySession> Snapshot();
    // Diagnostic only: the exact pin a publisher-signed router fell back from. It never grants authority.
    DesktopAdapterException? ExactPinRefusal => null;
}

internal sealed class CodexDirectoryObserver(DesktopDirectoryObserver observer) : ICodexDirectoryObserver
{
    public IReadOnlyList<DesktopDirectorySession> Snapshot() => observer.Snapshot();
    public DesktopAdapterException? ExactPinRefusal => observer.ExactPinRefusal;
    public ValueTask DisposeAsync() => observer.DisposeAsync();
}

internal interface ICodexOwnerConnection : IAsyncDisposable
{
    bool NativeIdleGuard { get; }
    Task<DesktopOwnerBinding?> DiscoverAsync(Guid conversationId, CancellationToken token);
    Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token);
}

internal sealed class CodexOwnerConnection(DesktopAdapter adapter) : ICodexOwnerConnection
{
    public bool NativeIdleGuard => adapter.NativeIdleGuard;
    public Task<DesktopOwnerBinding?> DiscoverAsync(Guid conversationId, CancellationToken token) => adapter.DiscoverAsync(conversationId, token);
    public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token)
        => adapter.SubmitExternalPeerAsync(binding, messageId, text, token);
    public ValueTask DisposeAsync() => adapter.DisposeAsync();
}
