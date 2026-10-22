using AgentBroker.CodexDesktopAdapter;

namespace AgentBroker.Worker.Adapters;

/// <summary>Uses the existing GUI owner and its native idle checks, without asserting a widget identity.</summary>
public sealed class CodexNativeProvider : ICodexNativeProvider
{
    private readonly bool enableDelivery;
    private readonly Func<CancellationToken, Task<ICodexOwnerConnection>> connect;
    private readonly Action verifyProfiles;
    private readonly Func<CancellationToken, Task<IReadOnlyList<DesktopDirectorySession>>> observe;
    private readonly Func<CancellationToken, Task<ICodexDirectoryObserver>> connectDirectory;
    private ICodexOwnerConnection? adapter;
    private bool profilesVerified;
    private IReadOnlyList<AdapterDiscoveryDiagnostic> discoveryDiagnostics = Array.Empty<AdapterDiscoveryDiagnostic>();
    public bool NativeIdleGuard => enableDelivery && profilesVerified && adapter?.NativeIdleGuard == true;
    public IReadOnlyList<AdapterDiscoveryDiagnostic> DiscoveryDiagnostics => discoveryDiagnostics;

    public CodexNativeProvider(bool enableDelivery = false)
        : this(enableDelivery, async token => new CodexOwnerConnection(await DesktopAdapter.ConnectAsync(
                new(MaximumMessageBytes: 32 * 1024, EnableExternalPeerDelivery: enableDelivery), token)),
            () => VsCodeCompatibilityProfile.VerifyRunningClients(),
            null) { }

    internal CodexNativeProvider(bool enableDelivery,
        Func<CancellationToken, Task<ICodexOwnerConnection>> connect, Action verifyProfiles,
        Func<CancellationToken, Task<IReadOnlyList<DesktopDirectorySession>>>? observe,
        Func<CancellationToken, Task<ICodexDirectoryObserver>>? connectDirectory = null)
    {
        this.enableDelivery = enableDelivery; this.connect = connect; this.verifyProfiles = verifyProfiles;
        this.observe = observe ?? ObservePersistentAsync;
        this.connectDirectory = connectDirectory ?? (async token => new CodexDirectoryObserver(await DesktopDirectoryObserver.ConnectAsync(token)));
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
        profilesVerified = false;
        if (enableDelivery)
        {
            try { verifyProfiles(); profilesVerified = true; }
            catch (DesktopAdapterException error)
            {
                profilesVerified = false;
                diagnostics.Add(CodexNativeAdapter.DescribeDiscoveryFailure(error));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                profilesVerified = false;
                diagnostics.Add(new("vscode_profile_unavailable",
                    $"Codex VS Code compatibility verification could not complete; verified extension {VsCodeCompatibilityProfile.Version}."));
            }
        }
        var observed = await observe(token);
        // Nonfatal delivery-profile refusals must not discard read-only observations. Only a
        // completed pass replaces diagnostics, so a subsequent healthy pass clears the warning.
        discoveryDiagnostics = Array.AsReadOnly(diagnostics.ToArray());
        return observed;
    }

    public bool IsDeliveryAuthorized(DesktopOwnerBinding binding)
        => NativeIdleGuard && binding.SupportsUntrustedAppInput;

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
        try { verifyProfiles(); }
        catch (Exception error) when (error is DesktopAdapterException or IOException or UnauthorizedAccessException)
        {
            profilesVerified = false;
            return new(DesktopSubmissionOutcome.Rejected, ErrorCode: "native_client_profile_unverified");
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

    public async ValueTask DisposeAsync()
    {
        profilesVerified = false;
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
}

internal sealed class CodexDirectoryObserver(DesktopDirectoryObserver observer) : ICodexDirectoryObserver
{
    public IReadOnlyList<DesktopDirectorySession> Snapshot() => observer.Snapshot();
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
