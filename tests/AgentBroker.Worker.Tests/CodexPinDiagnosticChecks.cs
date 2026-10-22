using AgentBroker.CodexDesktopAdapter;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Adapters;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

internal static class CodexPinDiagnosticChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var fixture in new[]
        {
            (Code: "unsupported_desktop_version", Version: "26.1020.1.0", Verified: DesktopCompatibilityProfile.ApplicationVersion),
            (Code: "desktop_image_mismatch", Version: DesktopCompatibilityProfile.ApplicationVersion, Verified: DesktopCompatibilityProfile.ApplicationVersion),
            (Code: "unsupported_vscode_router", Version: "1.144.0", Verified: VsCodeRouterCompatibilityProfile.ProductVersion),
            (Code: "owner_discovery_rejected", Version: (string?)null, Verified: (string?)null)
        })
        foreach (bool failOwnerResolution in new[] { false, true })
        {
            var original = new DesktopAdapterException(fixture.Code, fixture.Version);
            var engine = new DiagnosticNativeProvider(original, failOwnerResolution);
            await using var adapter = new CodexNativeAdapter(engine);
            AdapterDiscoveryException? captured = null;
            try { await adapter.DiscoverAsync(Guid.NewGuid(), default); }
            catch (AdapterDiscoveryException error) { captured = error; }
            check(captured is not null && captured.Diagnostic.Code == fixture.Code && ReferenceEquals(captured.InnerException, original),
                $"Codex {fixture.Code} survives the real adapter catch during {(failOwnerResolution ? "owner resolution" : "directory observation")}");
            string status = captured!.Diagnostic.ToStatusText();
            check(status.StartsWith(fixture.Code + ": ", StringComparison.Ordinal) &&
                (fixture.Version is null || status.Contains(fixture.Version + " installed", StringComparison.Ordinal)) &&
                (fixture.Verified is null || status.Contains(fixture.Verified, StringComparison.Ordinal)),
                $"Codex {fixture.Code} diagnostic carries its exact code and available installed and verified versions");
            engine.ClearFailure();
            var recovered = await adapter.DiscoverAsync(Guid.NewGuid(), default);
            check(recovered.Count == 1 && recovered[0].Adapter == "codex-native" && ((ILocalAgentAdapter)adapter).DiscoveryDiagnostics.Count == 0,
                $"Codex discovery recovers after {fixture.Code} without retaining a stale diagnostic");
        }

        var unknownVersion = new DiagnosticNativeProvider(new("unsupported_desktop_version"));
        await using (var adapter = new CodexNativeAdapter(unknownVersion))
        {
            try { await adapter.DiscoverAsync(Guid.NewGuid(), default); throw new InvalidOperationException("Missing compatibility failure."); }
            catch (AdapterDiscoveryException error)
            {
                check(error.Diagnostic.Description.Contains("installed version unknown", StringComparison.Ordinal) &&
                    error.Diagnostic.Description.Contains(DesktopCompatibilityProfile.ApplicationVersion, StringComparison.Ordinal),
                    "Unknown installed Codex version remains explicit instead of claiming the verified build is installed");
            }
        }
        await CheckRuntimeStatusAsync(check);
    }

    private static async Task CheckRuntimeStatusAsync(Action<bool, string> check)
    {
        string scratchRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scratch"));
        string scratch = Path.Combine(scratchRoot, "codex-pin-diagnostic-" + Guid.NewGuid().ToString("N"));
        var engine = new DiagnosticNativeProvider(new("unsupported_desktop_version", "26.1020.1.0"));
        try
        {
            using var store = new BrokerStore(scratch);
            await using var federation = new DiagnosticFederation();
            await using var runtime = new BrokerRuntime(store, federation, [new CodexNativeAdapter(engine)]);
            runtime.Start(default);
            await UntilAsync(() => runtime.AdapterErrors.ContainsKey("codex-native"));
            check(runtime.AdapterErrors["codex-native"].Contains("unsupported_desktop_version", StringComparison.Ordinal) &&
                runtime.AdapterErrors["codex-native"].Contains("26.1020.1.0 installed", StringComparison.Ordinal) &&
                runtime.AdapterErrors["codex-native"].Contains(DesktopCompatibilityProfile.ApplicationVersion, StringComparison.Ordinal),
                "Real broker runtime exposes the Codex pin rejection with its specific code and installed and verified versions");
            engine.ClearFailure();
            await UntilAsync(() => !runtime.AdapterErrors.ContainsKey("codex-native") &&
                store.Directory().Any(session => session.Adapter == "codex-native"));
            check(true, "Real broker runtime clears the stale Codex pin diagnostic when supported discovery succeeds");
        }
        finally
        {
            if (!Path.GetFullPath(scratch).StartsWith(scratchRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Synthetic Codex diagnostic fixture escaped its scratch root.");
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
        check(engine.Disposed, "Pin diagnostic fixture closes its synthetic native provider without launching any vendor process");
    }

    private static async Task UntilAsync(Func<bool> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!ready()) await Task.Delay(20, deadline.Token);
    }

    private sealed class DiagnosticNativeProvider(DesktopAdapterException failure, bool failOwnerResolution = false) : ICodexNativeProvider
    {
        private DesktopAdapterException? currentFailure = failure;
        private readonly Guid thread = Guid.NewGuid(), owner = Guid.NewGuid(), connection = Guid.NewGuid();
        internal bool Disposed { get; private set; }
        public bool NativeIdleGuard => false;
        internal void ClearFailure() => Volatile.Write(ref currentFailure, null);
        public Task<IReadOnlyList<DesktopDirectorySession>> ObserveAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!failOwnerResolution && Volatile.Read(ref currentFailure) is { } error)
                return Task.FromException<IReadOnlyList<DesktopDirectorySession>>(error);
            return Task.FromResult<IReadOnlyList<DesktopDirectorySession>>([new(thread, DesktopSessionActivity.Unknown, DateTimeOffset.UtcNow, null)]);
        }
        public Task<DesktopOwnerBinding?> ResolveOwnerAsync(Guid conversationId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (failOwnerResolution && Volatile.Read(ref currentFailure) is { } error)
                return Task.FromException<DesktopOwnerBinding?>(error);
            return Task.FromResult<DesktopOwnerBinding?>(conversationId == thread
                ? new(thread, owner, connection, 12345, 134000000000000000, true) : null);
        }
        public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token)
            => throw new InvalidOperationException("Read-only pin diagnostic tests cannot submit native input.");
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class DiagnosticFederation : IPeerFederation
    {
        public IReadOnlyList<BrokerAdvertisement> Discovered => [];
        public IReadOnlyList<PairingOffer> PairingOffers => [];
        public BrokerAdvertisement LocalAdvertisement => new(Guid.Empty, "Synthetic pin diagnostics", "https://127.0.0.1:15000", new string('A', 64), DateTimeOffset.UtcNow);
        public Guid? AuthenticatePeer(HttpContext context) => null;
        public void MapEndpoints(WebApplication application) { }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task RequestPairAsync(BrokerAdvertisement peer, CancellationToken token) => throw new NotSupportedException();
        public Task ApprovePairAsync(Guid requestId, CancellationToken token) => throw new NotSupportedException();
        public void RejectPair(Guid requestId) => throw new NotSupportedException();
        public void RevokePeer(Guid brokerId) => throw new NotSupportedException();
        public Task<PeerDirectory> GetDirectoryAsync(PeerTrust peer, CancellationToken token) => throw new NotSupportedException();
        public Task<MessageReceipt> SubmitAsync(PeerTrust peer, BrokerMessage message, CancellationToken token) => throw new NotSupportedException();
        public Task<MessageReceipt?> GetReceiptAsync(PeerTrust peer, Guid messageId, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
