using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBroker.Core;
using AgentBroker.Protocol;
using AgentBroker.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

internal static class PeerDiagnosticsChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "peer-diagnostics-" + Guid.NewGuid().ToString("N"));
        await VerifyOutcomesAndWire(scratch, check);
        await VerifyPeerCap(scratch, check);
        await VerifyAuthorizationChanges(scratch, check);
        await VerifyBudgetAndLateResults(scratch, check);
        await VerifyCallerCancellation(scratch, check);
    }

    private static PeerTrust AddPeer(BrokerStore store, int ordinal)
    {
        var peer = new PeerTrust(Guid.NewGuid(), "Synthetic diagnostic peer " + ordinal,
            "https://127.0.0.1:" + (18000 + ordinal) + "/", new string('A', 64), DateTimeOffset.UtcNow);
        store.AuthorizePeer(peer);
        return peer;
    }

    private static PeerDirectory DirectoryFor(PeerTrust peer, bool withSession = false)
    {
        BrokerSession[] sessions = withSession ? [new(new(peer.BrokerId, "synthetic", Guid.NewGuid()),
            "Synthetic diagnostic session", "fake", SessionCapabilities.Receive,
            SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(1))] : [];
        return new(peer.BrokerId, sessions);
    }

    private static async Task VerifyOutcomesAndWire(string scratch, Action<bool, string> check)
    {
        string data = Path.Combine(scratch, "outcomes");
        using var store = new BrokerStore(data);
        var peers = Enumerable.Range(0, 8).Select(i => AddPeer(store, i)).ToArray();
        var kinds = peers.Select((peer, index) => (peer.BrokerId, index)).ToDictionary(p => p.BrokerId, p => p.index);
        await using var federation = new DiagnosticFederation((peer, _) => kinds[peer.BrokerId] switch
        {
            0 => Task.FromResult(DirectoryFor(peer)),
            1 => Task.FromResult(DirectoryFor(peer, true)),
            2 => Task.FromException<PeerDirectory>(new HttpRequestException("Synthetic forbidden response.", null, HttpStatusCode.Forbidden)),
            3 => Task.FromException<PeerDirectory>(new HttpRequestException("Synthetic unavailable transport.")),
            4 => Task.FromException<PeerDirectory>(new HttpRequestException("Synthetic TLS failure.", new AuthenticationException("Synthetic certificate refusal."))),
            5 => Task.FromException<PeerDirectory>(new JsonException("Synthetic invalid directory JSON.")),
            6 => Task.FromResult(new PeerDirectory(Guid.NewGuid(), [])),
            _ => Task.FromException<PeerDirectory>(new HttpRequestException("Synthetic HTTP failure.", null, HttpStatusCode.ServiceUnavailable)),
        });
        await using var runtime = new BrokerRuntime(store, federation, []);
        byte[] before = File.ReadAllBytes(Path.Combine(data, "broker-state.json"));
        var rows = await PeerDiagnostics.CollectAsync(runtime, default);
        var byPeer = rows.ToDictionary(row => row.BrokerId);
        check(rows.Length == peers.Length && federation.Calls == peers.Length,
            "peer diagnostics checks each saved peer exactly once without relying on public broker info");
        var empty = byPeer[peers[0].BrokerId];
        check(empty is { Success: true, Code: "ok", ReturnedSessionCount: 0, AuthenticationStatus: "confirmed", CheckedAtUtc: not null },
            "peer diagnostics marks an authenticated empty directory as confirmed success with zero returned sessions");
        check(byPeer[peers[1].BrokerId] is { Success: true, Code: "ok", ReturnedSessionCount: 1, AuthenticationStatus: "confirmed" },
            "peer diagnostics reports the actual returned directory session count");
        string[] codes = ["peer_refused", "transport_failed", "tls_failed", "invalid_directory", "invalid_directory", "http_failed"];
        for (int i = 2; i < peers.Length; i++)
        {
            var row = byPeer[peers[i].BrokerId];
            check(!row.Success && row.Code == codes[i - 2] && row.ReturnedSessionCount is null &&
                row.AuthenticationStatus == "not_confirmed" && row.CheckedAtUtc is not null,
                "peer diagnostics keeps " + codes[i - 2] + " distinct from an authenticated empty directory");
        }
        check(rows.All(row => peers.Single(peer => peer.BrokerId == row.BrokerId) is var peer &&
                row.Endpoint == peer.Endpoint && row.CertificateSha256 == peer.CertificateSha256),
            "peer diagnostics binds every outcome to the saved endpoint and certificate pin");
        check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(data, "broker-state.json"))) &&
                store.Peers().OrderBy(peer => peer.BrokerId).SequenceEqual(peers.OrderBy(peer => peer.BrokerId)) &&
                store.Directory().Count == 0,
            "collecting peer diagnostics leaves durable trust and local session state unchanged");

        string ordinary = JsonSerializer.Serialize(new ControlResponse(true, "ok"), ControlProtocol.Json);
        using var ordinaryJson = JsonDocument.Parse(ordinary);
        check(!ordinaryJson.RootElement.TryGetProperty("peerDiagnostics", out _),
            "ordinary control responses omit the new optional peer diagnostics wire field");
        var strictOld = new JsonSerializerOptions(ControlProtocol.Json)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var legacy = JsonSerializer.Deserialize<LegacyControlResponse>(ordinary, strictOld);
        check(legacy is { Success: true, Code: "ok", Snapshot: null, Connection: null },
            "ordinary control responses still deserialize through the strict previous four-field contract");
        string diagnostic = JsonSerializer.Serialize(new ControlResponse(true, "ok", PeerDiagnostics: rows), ControlProtocol.Json);
        var roundTrip = JsonSerializer.Deserialize<ControlResponse>(diagnostic, ControlProtocol.Json);
        check(roundTrip?.PeerDiagnostics is { Length: 8 } restored &&
                restored.Single(row => row.BrokerId == empty.BrokerId) is { Success: true, ReturnedSessionCount: 0 } &&
                restored.Single(row => row.BrokerId == peers[2].BrokerId) is { Success: false, ReturnedSessionCount: null },
            "explicit peer diagnostics round trips confirmed zero and unknown counts without conflating them");
        bool strictOldRefused = false;
        try { JsonSerializer.Deserialize<LegacyControlResponse>(diagnostic, strictOld); }
        catch (JsonException) { strictOldRefused = true; }
        check(strictOldRefused,
            "the strict old-client witness rejects the explicit diagnostics field that ordinary status omits");
    }

    private static async Task VerifyPeerCap(string scratch, Action<bool, string> check)
    {
        using var store = new BrokerStore(Path.Combine(scratch, "peer-cap"), new(MaximumPeers: 33));
        var peers = Enumerable.Range(0, 33).Select(i => AddPeer(store, i)).ToArray();
        await using var federation = new DiagnosticFederation((peer, _) => Task.FromResult(DirectoryFor(peer)));
        await using var runtime = new BrokerRuntime(store, federation, []);
        var rows = await PeerDiagnostics.CollectAsync(runtime, default);
        check(rows.Length == 32 && federation.Calls == 32 && rows.Select(row => row.BrokerId).Distinct().Count() == 32 &&
                rows.All(row => peers.Any(peer => peer.BrokerId == row.BrokerId) && row.Success && row.ReturnedSessionCount == 0) &&
                store.Peers().Count == 33,
            "peer diagnostics caps one management request at 32 peers even when the synthetic store permits more");
    }

    private static async Task VerifyAuthorizationChanges(string scratch, Action<bool, string> check)
    {
        using (var store = new BrokerStore(Path.Combine(scratch, "in-flight-revoke")))
        {
            var peer = AddPeer(store, 0);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<PeerDirectory>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var federation = new DiagnosticFederation((_, _) => { entered.TrySetResult(); return release.Task; });
            await using var runtime = new BrokerRuntime(store, federation, []);
            var pending = PeerDiagnostics.CollectAsync(runtime, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            store.RevokePeer(peer.BrokerId); store.AuthorizePeer(peer);
            release.SetResult(DirectoryFor(peer, true));
            var row = (await pending.WaitAsync(TimeSpan.FromSeconds(2))).Single();
            check(row is { Success: false, Code: "trust_changed", ReturnedSessionCount: null, AuthenticationStatus: "not_confirmed" },
                "peer diagnostics rejects an in-flight result after revoke and identical-value re-pairing");
        }
        using (var store = new BrokerStore(Path.Combine(scratch, "batch-publish-revoke")))
        {
            var first = AddPeer(store, 0); var second = AddPeer(store, 1);
            var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Inline completion lets the first lookup finish while the second keeps the batch unpublished.
            var firstRelease = new TaskCompletionSource<PeerDirectory>();
            var secondRelease = new TaskCompletionSource<PeerDirectory>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var federation = new DiagnosticFederation((peer, _) =>
            {
                if (peer.BrokerId == first.BrokerId) { firstEntered.TrySetResult(); return firstRelease.Task; }
                secondEntered.TrySetResult(); return secondRelease.Task;
            });
            await using var runtime = new BrokerRuntime(store, federation, []);
            var pending = PeerDiagnostics.CollectAsync(runtime, default);
            await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(TimeSpan.FromSeconds(2));
            firstRelease.SetResult(DirectoryFor(first, true));
            store.RevokePeer(first.BrokerId); store.AuthorizePeer(first);
            secondRelease.SetResult(DirectoryFor(second));
            var rows = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            check(rows.Single(row => row.BrokerId == first.BrokerId) is
                    { Success: false, Code: "trust_changed", ReturnedSessionCount: null, AuthenticationStatus: "not_confirmed" } &&
                    rows.Single(row => row.BrokerId == second.BrokerId) is { Success: true, ReturnedSessionCount: 0 },
                "peer diagnostics rechecks completed peer authorization before publishing the final batch");
        }
    }

    private static async Task VerifyBudgetAndLateResults(string scratch, Action<bool, string> check)
    {
        using var store = new BrokerStore(Path.Combine(scratch, "budget"));
        var peers = Enumerable.Range(0, 8).Select(i => AddPeer(store, i)).ToArray();
        var pending = new ConcurrentBag<(PeerTrust Peer, TaskCompletionSource<PeerDirectory> Completion)>();
        await using var federation = new DiagnosticFederation((peer, _) =>
        {
            var completion = new TaskCompletionSource<PeerDirectory>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add((peer, completion));
            return completion.Task; // Intentionally ignore cancellation to test the public hard wait bound.
        });
        await using var runtime = new BrokerRuntime(store, federation, []);
        var elapsed = Stopwatch.StartNew();
        PeerDiagnosticView[] rows;
        try { rows = await PeerDiagnostics.CollectAsync(runtime, default); }
        finally { elapsed.Stop(); }
        check(elapsed.Elapsed >= TimeSpan.FromSeconds(4) && elapsed.Elapsed < TimeSpan.FromSeconds(8),
            "peer diagnostics enforces one actual five-second budget even when transports ignore cancellation");
        check(federation.PeakConcurrent is > 1 and <= 4 && federation.Calls == 4,
            "peer diagnostics starts at most four unavailable lookups and never starts queued peers after the deadline");
        check(rows.Length == peers.Length && rows.Count(row => row.Code == "timeout") == 4 &&
                rows.Count(row => row.Code == "not_checked_budget") == 4,
            "peer diagnostics distinguishes attempted timeouts from saved peers never checked within the budget");
        check(rows.All(row => !row.Success && row.ReturnedSessionCount is null &&
                (row.Code == "timeout" ? row.AuthenticationStatus == "not_confirmed" && row.CheckedAtUtc is not null
                    : row.AuthenticationStatus == "not_checked" && row.CheckedAtUtc is null)),
            "peer diagnostics never assigns an authenticated zero count or checked timestamp to skipped peers");
        string before = JsonSerializer.Serialize(rows, ControlProtocol.Json);
        foreach (var request in pending) request.Completion.TrySetResult(DirectoryFor(request.Peer, true));
        await Until(() => federation.Active == 0);
        check(JsonSerializer.Serialize(rows, ControlProtocol.Json) == before && rows.All(row => !row.Success),
            "late peer directory completions cannot turn a published timeout batch into successful sessions");
    }

    private static async Task VerifyCallerCancellation(string scratch, Action<bool, string> check)
    {
        using var store = new BrokerStore(Path.Combine(scratch, "caller-cancellation"));
        var peer = AddPeer(store, 0);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<PeerDirectory>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var federation = new DiagnosticFederation((_, _) => { entered.TrySetResult(); return release.Task; });
        await using var runtime = new BrokerRuntime(store, federation, []);
        using var cancelled = new CancellationTokenSource();
        var pending = PeerDiagnostics.CollectAsync(runtime, cancelled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        bool observed = false;
        try { await pending.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { observed = true; }
        finally { release.TrySetResult(DirectoryFor(peer)); }
        check(observed, "peer diagnostics propagates caller cancellation instead of returning a success-shaped diagnostic batch");
        await Until(() => federation.Active == 0);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(5, deadline.Token);
    }

    private sealed record LegacyControlResponse(bool Success, string Code, JsonElement? Snapshot, JsonElement? Connection);

    private sealed class DiagnosticFederation(Func<PeerTrust, CancellationToken, Task<PeerDirectory>> read) : IPeerFederation
    {
        private int active, calls, peak;
        internal int Calls => Volatile.Read(ref calls);
        internal int Active => Volatile.Read(ref active);
        internal int PeakConcurrent => Volatile.Read(ref peak);
        public IReadOnlyList<BrokerAdvertisement> Discovered => [];
        public IReadOnlyList<PairingOffer> PairingOffers => [];
        public BrokerAdvertisement LocalAdvertisement => new(Guid.NewGuid(), "Synthetic diagnostics", "https://127.0.0.1:18000/", new string('A', 64), DateTimeOffset.UtcNow);
        public Guid? AuthenticatePeer(HttpContext context) => null;
        public void MapEndpoints(WebApplication application) { }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task RequestPairAsync(BrokerAdvertisement peer, CancellationToken token) => throw new NotSupportedException();
        public Task ApprovePairAsync(Guid requestId, CancellationToken token) => throw new NotSupportedException();
        public void RejectPair(Guid requestId) { }
        public void RevokePeer(Guid brokerId) { }
        public async Task<PeerDirectory> GetDirectoryAsync(PeerTrust peer, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            int count = Interlocked.Increment(ref active), old;
            do { old = Volatile.Read(ref peak); if (old >= count) break; }
            while (Interlocked.CompareExchange(ref peak, count, old) != old);
            try { return await read(peer, token); }
            finally { Interlocked.Decrement(ref active); }
        }
        public Task<MessageReceipt> SubmitAsync(PeerTrust peer, BrokerMessage message, CancellationToken token) => throw new NotSupportedException();
        public Task<MessageReceipt?> GetReceiptAsync(PeerTrust peer, Guid messageId, CancellationToken token) => Task.FromResult<MessageReceipt?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
