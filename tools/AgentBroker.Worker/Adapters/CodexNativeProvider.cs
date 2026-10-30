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

    // A reconnect's own budget, outside the discovery pass deadline (N-broker-next-03). The router verification inside a
    // connect takes no token (WindowsDesktopConnection.ConnectAsync) and the library bounds the pipe connect and the
    // initialize request with its own five-second timeouts, so this bounds only what those leave.
    internal static readonly TimeSpan OwnerReconnectBudget = TimeSpan.FromSeconds(30);

    public async Task<DesktopOwnerBinding?> ResolveOwnerAsync(Guid conversationId, CancellationToken token)
    {
        // A reconnect that outlasted the pass deadline used to fail at its initialize request on the expired pass token
        // and be thrown away, so the next pass reconnected again; while a reconnect cost more than a pass (under disk load,
        // the full router verification), no pass renewed a row (N-broker-next-03, reproduced offline by
        // CODEX_RECONNECT_KEPT). The reconnect now runs on its own budget, and a connection no request has failed on is
        // kept when the pass ran out meanwhile: no request was sent, so no late answer can exist. Every new connection is
        // still fully verified; nothing here skips or reuses a verification. A lookup that fails still discards it below.
        if (adapter is null)
        {
            adapter = await AdoptReconnectAsync();
            if (adapter is null)
            {
                using var budget = new CancellationTokenSource(OwnerReconnectBudget);
                adapter = await connect(budget.Token);
            }
        }
        token.ThrowIfCancellationRequested();
        try { return await adapter.DiscoverAsync(conversationId, token); }
        // The router's complete answer about one conversation leaves the protocol in step: the library throws these two
        // only after RequestAsync returned the response matching its request ID (DesktopAdapter.DiscoverCoreAsync), and
        // only RequestAsync's own failure closes the protocol. Discarding the connection here made every later pass pay a
        // full owner re-verification while that conversation kept answering with an error (A-broker-01).
        catch (DesktopAdapterException error) when (error.Code is "owner_discovery_rejected" or "owner_capability_mismatch") { throw; }
        catch
        {
            await adapter.DisposeAsync();
            adapter = null;
            BeginReconnect();
            throw;
        }
    }

    // The reconnect after a failed lookup starts at once, so it runs in the pause between passes (N-broker-161-01). It used
    // to run inside the next pass, charged to whichever conversation came first: with one conversation whose lookup times
    // out on every pass (and drops the connection each time) and a reconnect longer than a pass, the other rows were
    // renewed on every other pass at best, and with 10-second passes and pauses that exceeds the 30-second lease
    // (reproduced offline by CODEX_RECONNECT_BETWEEN_PASSES). The new connection is fully verified as before, and the
    // failed one is still discarded first: a connection a request failed on is never reused. Only code under the
    // adapter's gate (every caller of this provider holds it) reads or writes these two fields; the task touches neither.
    private Task<ICodexOwnerConnection>? reconnecting;
    private CancellationTokenSource? reconnectBudget;

    private void BeginReconnect()
    {
        if (reconnecting is not null) return;
        var budget = new CancellationTokenSource(OwnerReconnectBudget);
        reconnectBudget = budget;
        // Task.Run because the router verification inside a connect is synchronous and this runs inside a failed lookup.
        reconnecting = Task.Run(() => connect(budget.Token));
    }

    // The started reconnect's connection, or null when none was started or it failed. A failure from between passes is not
    // rethrown, since it may be stale: the caller connects afresh, and that attempt's failure is the one reported, exactly as
    // an inline reconnect's was.
    private async Task<ICodexOwnerConnection?> AdoptReconnectAsync()
    {
        var started = reconnecting; var budget = reconnectBudget;
        reconnecting = null; reconnectBudget = null;
        if (started is null) return null;
        try { return await started; }
        catch (Exception) { return null; }
        finally { budget?.Dispose(); }
    }

    // A reconnect still running at disposal is cancelled where it can be (the pipe connect and the initialize request honour
    // the token) and NOT awaited: the router verification takes no token, and shutdown should not wait for a connection
    // nobody will use. A connection it produces anyway is closed when it arrives; its failure is nobody's to report.
    private void DiscardReconnect()
    {
        var started = reconnecting; var budget = reconnectBudget;
        reconnecting = null; reconnectBudget = null;
        if (started is null || budget is null) return;
        budget.Cancel();
        _ = CloseLateAsync(started, budget);
    }

    private static async Task CloseLateAsync(Task<ICodexOwnerConnection> started, CancellationTokenSource budget)
    {
        try { await (await started).DisposeAsync(); }
        catch (Exception) { }
        finally { budget.Dispose(); }
    }

    // A submission whose request failed leaves a connection the library has closed (DesktopAdapter.RequestAsync), and an
    // Uncertain answer may come from exactly that. Kept, the next discovery pass met connection_closed, which fails the
    // whole pass and clears every binding (A-broker-07). Retired here, the state reads as pending (adapter null), delivery
    // defers without connecting, and the discovery loop reconnects. A connection a request failed on is never reused.
    private async Task RetireOwnerConnectionAsync()
    {
        var retired = adapter;
        adapter = null;
        if (retired is null) return;
        try { await retired.DisposeAsync(); }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
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
        DesktopSubmissionResult submitted;
        try { submitted = await adapter.SubmitAsync(binding, messageId, text, token); }
        catch (DesktopAdapterException exception) when (exception.Code == "receipt_capacity")
        {
            // This exact library error precedes discovery and the first model-input write. Core owns
            // durable message dedup, so a new connection lease may safely admit this unsubmitted ID.
            await RetireOwnerConnectionAsync();
            return new(DesktopSubmissionOutcome.DeferredNotSubmitted, ErrorCode: "native_receipt_capacity_reconnect");
        }
        catch
        {
            await RetireOwnerConnectionAsync();
            throw;
        }
        if (submitted.Outcome == DesktopSubmissionOutcome.Uncertain) await RetireOwnerConnectionAsync();
        return submitted;
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
        DiscardReconnect();
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
