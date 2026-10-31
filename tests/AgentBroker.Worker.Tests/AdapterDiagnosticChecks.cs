using AgentBroker.Core;
using AgentBroker.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

internal static class AdapterDiagnosticChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "pin-diagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new BrokerStore(scratch);
            await using var federation = new NoPeers();
            var adapter = new MixedAdapter();
            await using var runtime = new BrokerRuntime(store, federation, [adapter]);
            runtime.Start(default);
            await UntilAsync(() => runtime.AdapterErrors.ContainsKey(adapter.Id));
            check(runtime.AdapterErrors.TryGetValue(adapter.Id, out string? error) &&
                error.Contains("unsupported_public_version", StringComparison.Ordinal) &&
                error.Contains("2.1.310 installed", StringComparison.Ordinal) &&
                error.Contains("2.1.292, 2.1.304, 2.1.306", StringComparison.Ordinal),
                "AdapterDiagnosticChecks.cs: completed discovery publishes installed and verified versions");
            check(store.Directory().Count == 1 && store.Directory()[0].Label == "Supported synthetic session",
                "AdapterDiagnosticChecks.cs: a stale process warning does not discard the supported session");
            adapter.Recovered = true;
            await UntilAsync(() => adapter.Calls >= 2 && !runtime.AdapterErrors.ContainsKey(adapter.Id));
            check(store.Directory().Count == 1 && store.Directory()[0].LeaseUntil > DateTimeOffset.UtcNow,
                "AdapterDiagnosticChecks.cs: the next supported-only pass clears the warning and renews its session");
            // Lane broker-163 (review finding 5): a note about some sessions is kept apart from the adapter's errors, so Status
            // keeps the healthy sessions' line, and it clears with the next pass that has none.
            adapter.Noting = true;
            await UntilAsync(() => runtime.AdapterNotes.ContainsKey(adapter.Id));
            check(runtime.AdapterNotes[adapter.Id].Contains("synthetic_note", StringComparison.Ordinal) && !runtime.AdapterErrors.ContainsKey(adapter.Id),
                "AdapterDiagnosticChecks.cs: RUNTIME_NOTE_APART: a note is reported as a note and never as the adapter's error");
            // Review of d9c2967, item 4: an unexpected failure (adapter_failed) completed nothing to note either.
            adapter.Fails = true;
            await UntilAsync(() => runtime.AdapterErrors.TryGetValue(adapter.Id, out string? failed) && failed == "adapter_failed");
            check(!runtime.AdapterNotes.ContainsKey(adapter.Id),
                "AdapterDiagnosticChecks.cs: RUNTIME_NOTE_CLEARED_ON_FAILURE: an unexpected discovery failure clears the last pass's note");
            adapter.Fails = false;
            adapter.Noting = false;
            await UntilAsync(() => !runtime.AdapterNotes.ContainsKey(adapter.Id) && !runtime.AdapterErrors.ContainsKey(adapter.Id));
        }
        finally
        {
            string scratchRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scratch"));
            if (!Path.GetFullPath(scratch).StartsWith(scratchRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Synthetic adapter diagnostic fixture escaped its scratch root.");
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    private sealed class MixedAdapter : ILocalAgentAdapter
    {
        private readonly Guid incarnation = Guid.NewGuid();
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        internal volatile bool Recovered, Noting, Fails;
        public string Id => "synthetic-pin-diagnostics";
        public IReadOnlyList<AdapterDiscoveryDiagnostic> DiscoveryDiagnostics { get; private set; } = [];
        public Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Fails) throw new InvalidOperationException("Synthetic unexpected discovery failure.");
            DiscoveryDiagnostics = Noting ? [new("synthetic_note", "1 synthetic session is noted.") { Note = true }] : Recovered ? [] : [new("unsupported_public_version",
                "Claude Code 2.1.310 installed; verified 2.1.292, 2.1.304, 2.1.306")];
            Interlocked.Increment(ref calls);
            return Task.FromResult<IReadOnlyList<BrokerSession>>([new(new(brokerId, "synthetic-supported", incarnation),
                "Supported synthetic session", Id, SessionCapabilities.Receive, SessionActivity.Idle,
                DateTimeOffset.UtcNow.AddSeconds(30))]);
        }
        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoPeers : IPeerFederation
    {
        public IReadOnlyList<BrokerAdvertisement> Discovered => [];
        public IReadOnlyList<PairingOffer> PairingOffers => [];
        public BrokerAdvertisement LocalAdvertisement => new(Guid.NewGuid(), "Synthetic", "https://127.0.0.1:1",
            new string('A', 64), DateTimeOffset.UtcNow);
        public Guid? AuthenticatePeer(HttpContext context) => null;
        public void MapEndpoints(WebApplication application) { }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task RequestPairAsync(BrokerAdvertisement peer, CancellationToken token) => throw new NotSupportedException();
        public Task ApprovePairAsync(Guid requestId, CancellationToken token) => throw new NotSupportedException();
        public void RejectPair(Guid requestId) { }
        public void RevokePeer(Guid brokerId) { }
        public Task<PeerDirectory> GetDirectoryAsync(PeerTrust peer, CancellationToken token) => throw new NotSupportedException();
        public Task<MessageReceipt> SubmitAsync(PeerTrust peer, BrokerMessage message, CancellationToken token) => throw new NotSupportedException();
        public Task<MessageReceipt?> GetReceiptAsync(PeerTrust peer, Guid messageId, CancellationToken token) => Task.FromResult<MessageReceipt?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
