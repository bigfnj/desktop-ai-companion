using System.Collections.Concurrent;
using System.Diagnostics;
using AgentBroker.CodexDesktopAdapter;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Adapters;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

internal static class RuntimeLifecycleChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "runtime-" + Guid.NewGuid().ToString("N"));
        using (var store = new BrokerStore(Path.Combine(scratch, "directory")))
        {
            for (int i = 0; i < 8; i++)
                store.AuthorizePeer(new(Guid.NewGuid(), "Synthetic peer " + i, "https://127.0.0.1:" + (15000 + i), new string('A', 64), DateTimeOffset.UtcNow));
            var peers = store.Peers();
            await using var federation = new SlowDirectoryFederation(peers[0].BrokerId);
            await using var runtime = new BrokerRuntime(store, federation, []);
            var local = runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic local", false);
            var elapsed = Stopwatch.StartNew();
            var directory = await runtime.DirectoryAsync(false, default);
            elapsed.Stop();
            check(elapsed.Elapsed < TimeSpan.FromSeconds(8), "all peer directories share one bounded five-second budget despite transports ignoring cancellation");
            check(directory.Any(s => s.Address == local.Address) && directory.Count == 2 &&
                directory.Any(s => s.Address.BrokerId == peers[0].BrokerId), "directory budget returns local and completed live peer sessions without waiting for unavailable peers");
            check(federation.PeakConcurrent is > 1 and <= 4, "peer directory lookups run concurrently with a fixed limit of four");
            check(federation.Calls < peers.Count, "global directory deadline stops starting additional unavailable peer lookups");
            int beforePeerList = federation.Calls;
            var ownDirectory = await runtime.DirectoryAsync(true, default);
            check(ownDirectory.Count == 1 && federation.Calls == beforePeerList, "peer-facing directory never performs transitive peer discovery");
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            try { await runtime.DirectoryAsync(false, cancelled.Token); throw new InvalidOperationException("Caller cancellation was swallowed."); }
            catch (OperationCanceledException) { check(true, "directory caller cancellation remains observable instead of returning a success-shaped partial result"); }
            federation.ReleasePending();
            await federation.DrainAsync();
            check(federation.Active == 0, "late transport completions drain without publishing additional directory sessions");
            federation.UnavailablePeersReturnEmpty = true;
            federation.OnReady = peer => store.RevokePeer(peer.BrokerId);
            var revoked = await runtime.DirectoryAsync(false, default);
            check(revoked.Count == 1 && revoked[0].Address == local.Address,
                "a peer revoked during directory lookup cannot publish a late session listing");
        }
        using (var store = new BrokerStore(Path.Combine(scratch, "shutdown")))
        {
            await using var federation = new SlowDirectoryFederation(Guid.Empty);
            // Since lane broker-next (P3-7) a discovery failure no longer faults the loop (RUNTIME_ADAPTER_FAILURE_RECORDED
            // below), and since lane broker-162 (N-broker-161-02) neither does a delivery's: the adapter lists a session, its
            // submission throws, and the message settles. The shutdown faults injected here are therefore both adapters'.
            var failing = new LifecycleAdapter("faulting", failDispose: true, listSession: true);
            var second = new LifecycleAdapter("second", failDispose: true);
            var runtime = new BrokerRuntime(store, federation, [failing, second]);
            runtime.Start(default);
            var started = Stopwatch.StartNew();
            while (failing.Address is null || !store.Directory().Any(session => session.Address == failing.Address))
            {
                if (started.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The faulting adapter's session was never listed.");
                await Task.Delay(20);
            }
            var poller = runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic lifecycle sender", false);
            BrokerMessage Delivery(string text) => new(Guid.NewGuid(), Guid.NewGuid(), null, poller.Address, failing.Address!,
                text, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));
            var faulted = Delivery("Synthetic lifecycle delivery.");
            var behind = Delivery("Synthetic lifecycle delivery behind it.");
            store.Accept(faulted, store.BrokerId);
            store.Accept(behind, store.BrokerId);
            // The second message to the same recipient is claimed only once the first has settled (per-pair order).
            while (Volatile.Read(ref failing.Submissions) < 2 && started.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(20);
            var settled = store.FindReceipt(faulted.Id);
            check(settled is { Status: MessageStatus.Uncertain, Reason: "submission_failed" } && Volatile.Read(ref failing.Submissions) >= 2,
                "RUNTIME_DELIVERY_FAULT_SETTLES: an unexpected submission failure settles that message Uncertain and the next one to the recipient still goes (" +
                settled?.Status + " " + settled?.Reason + ", " + Volatile.Read(ref failing.Submissions) + " submissions)");
            try { await runtime.DisposeAsync(); throw new InvalidOperationException("Injected lifecycle faults were lost."); }
            catch (AggregateException error)
            {
                check(error.InnerExceptions.Count == 2, "runtime preserves every adapter shutdown failure after attempting cleanup");
            }
            check(failing.DisposeCalls == 1 && second.DisposeCalls == 1, "a first-adapter disposal failure cannot skip remaining native adapter cleanup");
            try { await runtime.DisposeAsync(); }
            catch (AggregateException) { }
            check(failing.DisposeCalls == 1 && second.DisposeCalls == 1, "repeated runtime disposal shares the original cleanup task and never disposes adapters twice");
        }
        await IndependentAdapterLoopsAsync(check, scratch);
        await AdapterFailureRecordedAsync(check, scratch);
        await LoopRestartAsync(check, scratch);
        await LoopShutdownAsync(check, scratch);
        await SlowCodexRowAsync(check);
        await CodexReconnectAsync(check);
        await CodexReconnectBetweenPassesAsync(check);
        await CodexSlowOrderReconnectAsync(check);
        await CodexConnectionReuseAsync(check);
        await CodexSubmitFailureReconnectAsync(check);
        await PeerAnswerInvalidAsync(check, scratch);
        SessionPreferenceLimit(check, scratch);
        await SessionPreferenceWiringAsync(check, scratch);
    }

    // N-broker-161-04: SetSessionPreference hands SessionPreferences the runtime's own observed set, which decides whose
    // choice may be evicted at the 1,024-entry bound. Only the eviction was tested (PREFERENCE_LIMIT_EVICTS_ENDED), so a
    // predicate wired to the wrong set passed: every session observed (nothing evictable, every new choice refused) or none
    // (a present session's choice evicted). The present session's choice is written first, so "none" evicts it.
    private static async Task SessionPreferenceWiringAsync(Action<bool, string> check, string scratch)
    {
        string directory = Path.Combine(scratch, "preference-wiring");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "session-preferences.json");
        Guid keptClient = Guid.NewGuid(), targetClient = Guid.NewGuid();
        string kept = "mcp:" + keptClient.ToString("D");
        File.WriteAllText(file, "{\"" + kept + "\":{\"isPrivate\":true,\"isMuted\":false}," + string.Join(",", Enumerable.Range(0, 1023).Select(i =>
            "\"claude:ended-wiring-" + i + "\":{\"isPrivate\":true,\"isMuted\":false}")) + "}");
        using var store = new BrokerStore(Path.Combine(directory, "store"));
        await using var federation = new SlowDirectoryFederation(Guid.Empty);
        await using var runtime = new BrokerRuntime(store, federation, [], new SessionPreferences(directory));
        var present = runtime.RegisterPollingAgent(keptClient, Guid.NewGuid(), "Synthetic present session", false);
        var target = runtime.RegisterPollingAgent(targetClient, Guid.NewGuid(), "Synthetic chosen session", false);
        string? refused = null;
        try { runtime.SetSessionPreference(target.Address.SessionId, true, null); } catch (BrokerException error) { refused = error.Code; }
        var reopened = new SessionPreferences(directory);
        bool presentKept = reopened.Apply(present with { IsPrivate = false }).IsPrivate;
        bool targetSet = reopened.Apply(target with { IsPrivate = false }).IsPrivate;
        int ended;
        using (var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(file)))
            ended = json.RootElement.EnumerateObject().Count(entry => entry.Name.StartsWith("claude:ended-wiring-", StringComparison.Ordinal));
        check(refused is null && targetSet && presentKept,
            "PREFERENCE_RUNTIME_OBSERVED: through the runtime, a choice at the bound is kept and a present session's choice is never the one evicted (" +
            (refused ?? "set") + ", present kept " + presentKept + ")");
        check(ended == 1022, "PREFERENCE_RUNTIME_OBSERVED WITNESS: the entry evicted was an ended session's (" + ended + " of 1023 remain)");
    }

    // N-broker-161-02: an exception outside GuardAsync's list ended a delivery or receipt loop in silence. The receipt loop
    // is the one faulted here (no outbound message got its receipt again): it is now reported under its name and runs again.
    private static async Task LoopRestartAsync(Action<bool, string> check, string scratch)
    {
        using var store = new BrokerStore(Path.Combine(scratch, "loop-restart"));
        var peerId = Guid.NewGuid();
        store.AuthorizePeer(new(peerId, "Synthetic receipt peer", "https://127.0.0.1:15200", new string('C', 64), DateTimeOffset.UtcNow));
        await using var federation = new FaultingReceiptFederation { Failures = 2 };
        var runtime = new BrokerRuntime(store, federation, [])
        { LoopRestartDelay = TimeSpan.FromMilliseconds(100), LoopRestartLimit = TimeSpan.FromSeconds(1) };
        try
        {
            var local = runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic receipt sender", false);
            var message = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, local.Address,
                new SessionAddress(peerId, "claude:synthetic-receipt-recipient", Guid.NewGuid()), "Synthetic forward awaiting its receipt.",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));
            store.Accept(message, store.BrokerId);
            runtime.Start(default);
            var watch = Stopwatch.StartNew();
            while (store.FindReceipt(message.Id)?.Status != MessageStatus.Delivered && watch.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(50);
            var status = store.FindReceipt(message.Id)?.Status;
            runtime.AdapterErrors.TryGetValue("receipts", out var error);
            check(status == MessageStatus.Delivered && error is not null && error.StartsWith("loop_failed: 2 unexpected failures", StringComparison.Ordinal) &&
                Volatile.Read(ref federation.ReceiptCalls) >= 3,
                "RUNTIME_LOOP_RESTARTED: a receipt loop that failed unexpectedly is reported and runs again (" + status + ", " + (error ?? "not reported") + ")");
            check(!runtime.AdapterErrors.ContainsKey("delivery") && !runtime.AdapterErrors.ContainsKey("broker"),
                "RUNTIME_LOOP_RESTARTED WITNESS: only the loop that failed is reported (" + string.Join(", ", runtime.AdapterErrors.Keys) + ")");
        }
        finally { try { await runtime.DisposeAsync(); } catch (Exception) { } }
    }

    // N-broker-161-02, the lifecycle half: a loop waiting to run again must still end at shutdown, and every other loop with
    // it. The receipt loop fails on every call with a 30-second pause, so a pause that ignored shutdown would hold disposal.
    private static async Task LoopShutdownAsync(Action<bool, string> check, string scratch)
    {
        using var store = new BrokerStore(Path.Combine(scratch, "loop-shutdown"));
        var peerId = Guid.NewGuid();
        store.AuthorizePeer(new(peerId, "Synthetic receipt peer", "https://127.0.0.1:15201", new string('D', 64), DateTimeOffset.UtcNow));
        await using var federation = new FaultingReceiptFederation { Failures = int.MaxValue };
        var native = new LifecycleAdapter("shutdown-native");
        var runtime = new BrokerRuntime(store, federation, [native])
        { LoopRestartDelay = TimeSpan.FromSeconds(30), LoopRestartLimit = TimeSpan.FromMinutes(1), DiscoveryInterval = TimeSpan.FromMilliseconds(50) };
        Exception? fault = null;
        var elapsed = TimeSpan.Zero;
        try
        {
            var local = runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic receipt sender", false);
            store.Accept(new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, local.Address,
                new SessionAddress(peerId, "claude:synthetic-shutdown-recipient", Guid.NewGuid()), "Synthetic forward during shutdown.",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5)), store.BrokerId);
            runtime.Start(default);
            var watch = Stopwatch.StartNew();
            while (!runtime.AdapterErrors.ContainsKey("receipts") && watch.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(50);
        }
        finally
        {
            var disposal = Stopwatch.StartNew();
            try { await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60)); } catch (Exception error) { fault = error; }
            elapsed = disposal.Elapsed;
        }
        int calls = Volatile.Read(ref federation.ReceiptCalls), passes = Volatile.Read(ref native.Passes);
        await Task.Delay(300);
        check(runtime.AdapterErrors.ContainsKey("receipts") && elapsed < TimeSpan.FromSeconds(5) && fault is null &&
            Volatile.Read(ref federation.ReceiptCalls) == calls && Volatile.Read(ref native.Passes) == passes,
            "RUNTIME_LOOP_SHUTDOWN: shutdown ends every loop, one waiting to run again included (" + (int)elapsed.TotalMilliseconds + " ms, " +
            (fault?.GetType().Name ?? "no fault") + ")");
        check(passes >= 2, "RUNTIME_LOOP_SHUTDOWN WITNESS: the discovery loop was running until shutdown (" + passes + " passes)");
    }

    // A paired broker that accepts every forward and fails its first receipt answers with an exception type outside every
    // catch list of the receipt path: a defect, not a transport failure.
    private sealed class FaultingReceiptFederation : IPeerFederation
    {
        private readonly ConcurrentDictionary<Guid, BrokerMessage> forwarded = new();
        internal int Failures, ReceiptCalls;
        public IReadOnlyList<BrokerAdvertisement> Discovered => [];
        public IReadOnlyList<PairingOffer> PairingOffers => [];
        public BrokerAdvertisement LocalAdvertisement => new(Guid.NewGuid(), "Synthetic", "https://127.0.0.1:15000", new string('A', 64), DateTimeOffset.UtcNow);
        public Guid? AuthenticatePeer(HttpContext context) => null;
        public void MapEndpoints(WebApplication application) { }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task RequestPairAsync(BrokerAdvertisement peer, CancellationToken token) => throw new NotSupportedException();
        public Task ApprovePairAsync(Guid requestId, CancellationToken token) => throw new NotSupportedException();
        public void RejectPair(Guid requestId) { }
        public void RevokePeer(Guid brokerId) { }
        public Task<PeerDirectory> GetDirectoryAsync(PeerTrust peer, CancellationToken token) => Task.FromResult(new PeerDirectory(peer.BrokerId, []));
        public Task<MessageReceipt> SubmitAsync(PeerTrust peer, BrokerMessage message, CancellationToken token)
        {
            forwarded[message.Id] = message;
            return Task.FromResult(new MessageReceipt(message, MessageStatus.Submitted, DateTimeOffset.UtcNow));
        }
        public Task<MessageReceipt?> GetReceiptAsync(PeerTrust peer, Guid messageId, CancellationToken token)
        {
            if (Interlocked.Increment(ref ReceiptCalls) <= Volatile.Read(ref Failures))
                return Task.FromException<MessageReceipt?>(new InvalidOperationException("Synthetic receipt loop failure."));
            return Task.FromResult(forwarded.TryGetValue(messageId, out var message)
                ? new MessageReceipt(message, MessageStatus.Delivered, DateTimeOffset.UtcNow) : null);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // A-broker-04: preferences were never pruned, so at 1,024 every new private or mute choice failed preference_limit and
    // the stale entries (sessions that ended) could not be cleared. The file is written directly in its own format.
    private static void SessionPreferenceLimit(Action<bool, string> check, string scratch)
    {
        string directory = Path.Combine(scratch, "preferences");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "session-preferences.json");
        File.WriteAllText(file, "{" + string.Join(",", Enumerable.Range(0, 1024).Select(i =>
            "\"claude:ended-" + i + "\":{\"isPrivate\":true,\"isMuted\":false}")) + "}");
        BrokerSession Session(string id) => new(new(Guid.NewGuid(), id, Guid.NewGuid()), "Synthetic " + id, "claude-native",
            SessionCapabilities.None, SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(1));
        int Entries() { using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(file)); return json.RootElement.EnumerateObject().Count(); }
        var preferences = new SessionPreferences(directory);
        string? refused = null;
        try { preferences.Set("claude:live", true, null, id => id == "claude:live"); } catch (BrokerException error) { refused = error.Code; }
        var reopened = new SessionPreferences(directory);
        check(refused is null && reopened.Apply(Session("claude:live")).IsPrivate && Entries() == 1024,
            "PREFERENCE_LIMIT_EVICTS_ENDED: at the bound a choice for a session no longer observed makes room for a new one (" +
            (refused ?? "set") + ", " + Entries() + " entries)");
        string? limit = null;
        try { reopened.Set("claude:another-live", true, null, _ => true); } catch (BrokerException error) { limit = error.Code; }
        check(limit == "preference_limit",
            "PREFERENCE_LIMIT_EVICTS_ENDED WITNESS: when every entry belongs to an observed session the limit still refuses (" + (limit ?? "accepted") + ")");
    }

    // A-broker-02: the federation's strict JSON options throw JsonException on a paired broker's unknown member, and the
    // runtime's three peer paths did not catch it: list_sessions failed for every local caller, a delivery loop ended with
    // its claim left Submitting (holding every later message to that recipient), and the receipt loop ended. The fake peer
    // parses its answers with the same strict options PeerFederation uses, so the exception is the real one.
    private static async Task PeerAnswerInvalidAsync(Action<bool, string> check, string scratch)
    {
        using var store = new BrokerStore(Path.Combine(scratch, "peer-answer"));
        var peerId = Guid.NewGuid();
        store.AuthorizePeer(new(peerId, "Synthetic newer peer", "https://127.0.0.1:15100", new string('B', 64), DateTimeOffset.UtcNow));
        await using var federation = new UnreadablePeerFederation();
        var runtime = new BrokerRuntime(store, federation, []);
        try
        {
            var local = runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic peer sender", false);
            IReadOnlyList<BrokerSession>? listed = null;
            try { listed = await runtime.DirectoryAsync(false, default); } catch (System.Text.Json.JsonException) { }
            check(listed is not null && listed.Count == 1 && listed[0].Address == local.Address,
                "PEER_JSON_DIRECTORY: a peer directory answer with an unknown member costs that peer's listing, not list_sessions (" +
                (listed is null ? "threw" : listed.Count + " rows") + ")");
            var recipient = new SessionAddress(peerId, "claude:synthetic-peer-recipient", Guid.NewGuid());
            BrokerMessage Message(string text) => new(Guid.NewGuid(), Guid.NewGuid(), null, local.Address, recipient, text,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));
            var unreadable = Message("Synthetic forward whose answer is unreadable.");
            var later = Message("Synthetic forward after it.");
            federation.UnreadableSubmit = unreadable.Id;
            federation.UnreadableReceipt = unreadable.Id;
            store.Accept(unreadable, store.BrokerId);
            store.Accept(later, store.BrokerId);
            runtime.Start(default);
            var watch = Stopwatch.StartNew();
            while (store.FindReceipt(later.Id)?.Status is not (MessageStatus.Submitted or MessageStatus.Delivered) && watch.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(50);
            var first = store.FindReceipt(unreadable.Id);
            check(first is { Status: MessageStatus.Uncertain, Reason: "peer_answer_invalid" } &&
                store.FindReceipt(later.Id)?.Status is MessageStatus.Submitted or MessageStatus.Delivered,
                "PEER_JSON_DELIVERY: an unreadable forward answer is that message's Uncertain peer_answer_invalid and the next message to the recipient still goes (" +
                first?.Status + " " + first?.Reason + ", later " + store.FindReceipt(later.Id)?.Status + ")");
            // The receipt pass meets the unreadable receipt answer first (list order), then must still reach the later one.
            while (store.FindReceipt(later.Id)?.Status != MessageStatus.Delivered && watch.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(50);
            check(store.FindReceipt(later.Id)?.Status == MessageStatus.Delivered && federation.UnreadableReceipts >= 1,
                "PEER_JSON_RECEIPT: an unreadable receipt answer costs that message's receipt for the pass, never the receipt loop (later " +
                store.FindReceipt(later.Id)?.Status + ", " + federation.UnreadableReceipts + " unreadable answers)");
        }
        // A loop that died rethrows its fault at disposal, which must not replace a check's own label.
        finally { try { await runtime.DisposeAsync(); } catch (Exception) { } }
    }

    private sealed class UnreadablePeerFederation : IPeerFederation
    {
        // PeerFederation's own options (Federation/PeerFederation.cs): an unknown member is refused.
        private static readonly System.Text.Json.JsonSerializerOptions Strict = new(System.Text.Json.JsonSerializerDefaults.Web)
        {
            MaxDepth = 16, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true
        };
        private const string NewerBody = "{\"brokerId\":\"00000000-0000-0000-0000-000000000001\",\"sessions\":[],\"aMemberFromANewerRelease\":1}";
        private readonly ConcurrentDictionary<Guid, BrokerMessage> forwarded = new();
        internal Guid? UnreadableSubmit, UnreadableReceipt;
        internal int UnreadableReceipts;
        public IReadOnlyList<BrokerAdvertisement> Discovered => [];
        public IReadOnlyList<PairingOffer> PairingOffers => [];
        public BrokerAdvertisement LocalAdvertisement => new(Guid.NewGuid(), "Synthetic", "https://127.0.0.1:15000", new string('A', 64), DateTimeOffset.UtcNow);
        public Guid? AuthenticatePeer(HttpContext context) => null;
        public void MapEndpoints(WebApplication application) { }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task RequestPairAsync(BrokerAdvertisement peer, CancellationToken token) => throw new NotSupportedException();
        public Task ApprovePairAsync(Guid requestId, CancellationToken token) => throw new NotSupportedException();
        public void RejectPair(Guid requestId) { }
        public void RevokePeer(Guid brokerId) { }
        public Task<PeerDirectory> GetDirectoryAsync(PeerTrust peer, CancellationToken token)
            => Task.FromResult(System.Text.Json.JsonSerializer.Deserialize<PeerDirectory>(NewerBody, Strict)!);
        public Task<MessageReceipt> SubmitAsync(PeerTrust peer, BrokerMessage message, CancellationToken token)
        {
            forwarded[message.Id] = message;
            if (message.Id == UnreadableSubmit) System.Text.Json.JsonSerializer.Deserialize<MessageReceipt>(NewerBody, Strict);
            return Task.FromResult(new MessageReceipt(message, MessageStatus.Submitted, DateTimeOffset.UtcNow));
        }
        public Task<MessageReceipt?> GetReceiptAsync(PeerTrust peer, Guid messageId, CancellationToken token)
        {
            if (messageId == UnreadableReceipt)
            {
                Interlocked.Increment(ref UnreadableReceipts);
                System.Text.Json.JsonSerializer.Deserialize<MessageReceipt>(NewerBody, Strict);
            }
            return Task.FromResult(forwarded.TryGetValue(messageId, out var message)
                ? new MessageReceipt(message, MessageStatus.Delivered, DateTimeOffset.UtcNow) : null);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // N-broker-next-03: after a failed lookup the provider drops the owner connection and the next lookup reconnects. The
    // reconnect's router verification is synchronous (WindowsDesktopConnection.ConnectAsync) and the initialize request after
    // it honours the token it is given. With the pass token, a reconnect outlasting the pass deadline failed at initialize and
    // was thrown away, so the next pass reconnected again: while a reconnect costs more than a pass, no pass renewed a row.
    // Scaled down: pass deadline 400 ms, reconnect 700 ms (ignoring cancellation, as the verification does).
    private static async Task CodexReconnectAsync(Action<bool, string> check)
    {
        var owner = new ReconnectOwner();
        int connects = 0;
        var connectCost = TimeSpan.FromMilliseconds(700);
        var provider = new CodexNativeProvider(true, async token =>
            {
                Interlocked.Increment(ref connects);
                await Task.Delay(connectCost);
                token.ThrowIfCancellationRequested(); // The initialize request the library sends after the verification.
                return owner;
            }, () => { },
            _ => Task.FromResult<IReadOnlyList<DesktopDirectorySession>>(
                [new(owner.Thread, DesktopSessionActivity.Idle, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]),
            null, false, () => { });
        await using var adapter = new CodexNativeAdapter(provider, enableDelivery: true);
        var brokerId = Guid.NewGuid();
        async Task<int> PassAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            try { return (await adapter.DiscoverAsync(brokerId, deadline.Token)).Count; }
            catch (OperationCanceledException) { return 0; }
        }
        int renewed = 0;
        for (int pass = 0; pass < 4; pass++) renewed += await PassAsync();
        check(renewed == 3 && Volatile.Read(ref connects) == 1,
            "CODEX_RECONNECT_KEPT: a reconnect that outlasts the pass deadline is kept, so the next pass renews the row (renewed " +
            renewed + " of 4 passes, " + Volatile.Read(ref connects) + " connects)");
        // A lookup that FAILED still discards the connection: the library closed its protocol, and a late answer could be
        // read as the next request's (DesktopAdapter.RequestAsync). Only a connection no request ever failed on is kept.
        // Counted from before the failing pass: since N-broker-161-01 the reconnect starts as the failed lookup discards the
        // connection, so it may already have begun when that pass returns.
        connectCost = TimeSpan.Zero;
        int before = Volatile.Read(ref connects), closed = Volatile.Read(ref owner.Disposals);
        owner.Failure = new IOException("Synthetic owner lookup failure.");
        await PassAsync();
        owner.Failure = null;
        int after = await PassAsync();
        check(after == 1 && Volatile.Read(ref connects) == before + 1 && Volatile.Read(ref owner.Disposals) == closed + 1,
            "CODEX_RECONNECT_KEPT WITNESS: a lookup that failed still discards the connection and the next lookup uses a new one (" +
            (Volatile.Read(ref connects) - before) + " reconnects, " + (Volatile.Read(ref owner.Disposals) - closed) + " closed)");
    }

    // N-broker-161-01, the rest of N-broker-next-03: one conversation whose owner lookup times out on every pass (the failed
    // lookup discards the connection, as it must) and a reconnect longer than a pass. The reconnect ran inside the next pass,
    // charged to whichever conversation came first, so the other rows were renewed on every other pass at best. Scaled down
    // at the production ratio: pass deadline and pause 600 ms each, a reconnect of 700 ms that ignores cancellation (as the
    // router verification does) and then honours its token. Counts are deterministic for this fake; no production timing
    // is claimed.
    private static async Task CodexReconnectBetweenPassesAsync(Action<bool, string> check)
    {
        var owner = new ReconnectOwner { Hanging = Guid.NewGuid() };
        var other = Guid.NewGuid();
        int connects = 0;
        var provider = new CodexNativeProvider(true, async token =>
            {
                Interlocked.Increment(ref connects);
                await Task.Delay(TimeSpan.FromMilliseconds(700));
                token.ThrowIfCancellationRequested(); // The initialize request the library sends after the verification.
                return owner;
            }, () => { },
            _ => Task.FromResult<IReadOnlyList<DesktopDirectorySession>>(
                [new(owner.Hanging.Value, DesktopSessionActivity.Idle, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                 new(owner.Thread, DesktopSessionActivity.Idle, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                 new(other, DesktopSessionActivity.Idle, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]),
            null, false, () => { });
        await using var adapter = new CodexNativeAdapter(provider, enableDelivery: true);
        var brokerId = Guid.NewGuid();
        int renewedOwner = 0, renewedOther = 0;
        const int passes = 6;
        for (int pass = 0; pass < passes; pass++)
        {
            using (var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(600)))
            {
                IReadOnlyList<BrokerSession> rows = [];
                try { rows = await adapter.DiscoverAsync(brokerId, deadline.Token); } catch (OperationCanceledException) { }
                renewedOwner += rows.Count(row => row.Address.SessionId == "codex:" + owner.Thread.ToString("D"));
                renewedOther += rows.Count(row => row.Address.SessionId == "codex:" + other.ToString("D"));
            }
            await Task.Delay(TimeSpan.FromMilliseconds(600)); // The discovery loop's pause.
        }
        // The first pass connects inside itself (nothing failed before it) and renews nothing. Since N-broker-162-01 the
        // conversation charged with that connect is not marked slow, so the second pass meets the timing-out conversation
        // first and renews nothing either (before, the charge happened to fall on it); every later pass renews both. At least,
        // because the ordering has its own check (CODEX_SLOW_ORDER_RECONNECT); this one is about where the reconnect runs.
        check(renewedOwner >= passes - 2 && renewedOther >= passes - 2,
            "CODEX_RECONNECT_BETWEEN_PASSES: with one lookup timing out every pass and a reconnect longer than a pass, the other rows renew on every pass (renewed " +
            renewedOwner + " and " + renewedOther + " of " + passes + " passes, " + Volatile.Read(ref connects) + " connects, " +
            Volatile.Read(ref owner.Disposals) + " closed)");
        check(Volatile.Read(ref owner.Disposals) == passes - 1,
            "CODEX_RECONNECT_BETWEEN_PASSES WITNESS: every pass's timed-out lookup still discarded its connection (" + Volatile.Read(ref owner.Disposals) + " closed)");
    }

    // N-broker-162-01: the slow ordering. With a reconnect longer than a pass and a pause together, the conversation that
    // adopts the reconnect is charged the wait and the pass runs out before its own lookup; it used to be marked slow, and
    // the timing-out conversation, not reached that pass, lost its mark, so it went first on the pass after and ended it.
    // From then on no pass renewed anything. Scaled down: pass deadline and pause 400 ms each, reconnect 1,000 ms (ignoring
    // cancellation, then honouring its token). Counts are deterministic for this fake; no production timing is claimed.
    private static async Task CodexSlowOrderReconnectAsync(Action<bool, string> check)
    {
        var owner = new ReconnectOwner { Hanging = Guid.NewGuid() };
        var other = Guid.NewGuid();
        int connects = 0;
        var provider = new CodexNativeProvider(true, async token =>
            {
                Interlocked.Increment(ref connects);
                await Task.Delay(TimeSpan.FromMilliseconds(1000));
                token.ThrowIfCancellationRequested();
                return owner;
            }, () => { },
            _ => Task.FromResult<IReadOnlyList<DesktopDirectorySession>>(
                [new(owner.Hanging.Value, DesktopSessionActivity.Idle, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                 new(owner.Thread, DesktopSessionActivity.Idle, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                 new(other, DesktopSessionActivity.Idle, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]),
            null, false, () => { });
        await using var adapter = new CodexNativeAdapter(provider, enableDelivery: true);
        var brokerId = Guid.NewGuid();
        int renewedOwner = 0, renewedOther = 0;
        const int passes = 8;
        for (int pass = 0; pass < passes; pass++)
        {
            using (var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(400)))
            {
                IReadOnlyList<BrokerSession> rows = [];
                try { rows = await adapter.DiscoverAsync(brokerId, deadline.Token); } catch (OperationCanceledException) { }
                renewedOwner += rows.Count(row => row.Address.SessionId == "codex:" + owner.Thread.ToString("D"));
                renewedOther += rows.Count(row => row.Address.SessionId == "codex:" + other.ToString("D"));
            }
            await Task.Delay(TimeSpan.FromMilliseconds(400)); // The discovery loop's pause.
        }
        // Passes 1 to 3 renew nothing (the first connects inside itself, the second meets the timing-out conversation first,
        // the third waits out the reconnect); from then on every other pass renews both, the most a reconnect this long
        // allows, since each timed-out lookup discards the connection.
        check(renewedOwner == 3 && renewedOther == 3,
            "CODEX_SLOW_ORDER_RECONNECT: a lookup charged with a reconnect is not marked slow and an unreached slow conversation keeps its mark (renewed " +
            renewedOwner + " and " + renewedOther + " of " + passes + " passes, " + Volatile.Read(ref connects) + " connects)");
    }

    // A-broker-07 and A-broker-01, the owner connection's reuse. Modelled on the library: a failed start-turn request closes
    // the protocol and answers Uncertain, and every later request on it throws connection_closed; the router's error answer
    // for one conversation (owner_discovery_rejected) arrives as a complete response and leaves the protocol in step.
    private static async Task CodexConnectionReuseAsync(Action<bool, string> check)
    {
        var owner = new ReconnectOwner();
        var other = Guid.NewGuid();
        int connects = 0;
        var provider = new CodexNativeProvider(true, _ =>
            {
                Interlocked.Increment(ref connects);
                owner.Closed = false; // A new connection.
                return Task.FromResult<ICodexOwnerConnection>(owner);
            }, () => { },
            _ => Task.FromResult<IReadOnlyList<DesktopDirectorySession>>(
                [new(owner.Thread, DesktopSessionActivity.Idle, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                 new(other, DesktopSessionActivity.Idle, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]),
            null, false, () => { });
        await using var adapter = new CodexNativeAdapter(provider, enableDelivery: true);
        var brokerId = Guid.NewGuid();
        var rows = await adapter.DiscoverAsync(brokerId, default);
        var target = rows.Single(row => row.Address.SessionId == "codex:" + owner.Thread.ToString("D"));
        owner.SubmitTimesOut = true;
        var uncertain = await adapter.SubmitAsync(new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null,
            new(brokerId, "synthetic-reuse-sender", Guid.NewGuid()), target.Address, "Synthetic start-turn that times out.",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1)), default);
        IReadOnlyList<BrokerSession>? next = null;
        try { next = await adapter.DiscoverAsync(brokerId, default); } catch (AdapterDiscoveryException) { }
        check(uncertain.Outcome == AdapterAdmission.Uncertain && next?.Count == 2 && connects == 2,
            "CODEX_SUBMIT_FAILURE_RETIRES: after a submission whose request failed, the next pass reconnects and renews every row (" +
            (next is null ? "pass failed" : next.Count + " rows") + ", " + connects + " connects)");
        // The router's error answer for one conversation: that row is skipped, the connection is kept.
        owner.Rejected = other;
        int before = connects;
        var first = await adapter.DiscoverAsync(brokerId, default);
        var second = await adapter.DiscoverAsync(brokerId, default);
        check(first.Count == 1 && second.Count == 1 && connects == before,
            "CODEX_REJECTED_KEEPS_CONNECTION: a router error answer for one conversation skips its row and keeps the intact connection (" +
            (connects - before) + " reconnects over two passes)");
    }

    // N-broker-162-02: a submission whose request failed retires the connection (A-broker-07), and the replacement used to be
    // made inside the next discovery pass. It now starts as the connection is retired, so the next pass adopts it. Scaled
    // down: pass deadline and pause 600 ms each, reconnect 700 ms (longer than a pass). Deterministic for this fake.
    private static async Task CodexSubmitFailureReconnectAsync(Action<bool, string> check)
    {
        var owner = new ReconnectOwner();
        var other = Guid.NewGuid();
        int connects = 0, connectsInPass = 0;
        bool inPass = false;
        var cost = TimeSpan.Zero;
        var provider = new CodexNativeProvider(true, async token =>
            {
                Interlocked.Increment(ref connects);
                if (Volatile.Read(ref inPass)) Interlocked.Increment(ref connectsInPass);
                await Task.Delay(cost);
                token.ThrowIfCancellationRequested();
                owner.Closed = false; // A new connection.
                return owner;
            }, () => { },
            _ => Task.FromResult<IReadOnlyList<DesktopDirectorySession>>(
                [new(owner.Thread, DesktopSessionActivity.Idle, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                 new(other, DesktopSessionActivity.Idle, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]),
            null, false, () => { });
        await using var adapter = new CodexNativeAdapter(provider, enableDelivery: true);
        var brokerId = Guid.NewGuid();
        async Task<IReadOnlyList<BrokerSession>> PassAsync()
        {
            Volatile.Write(ref inPass, true);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
                try { return await adapter.DiscoverAsync(brokerId, deadline.Token); }
                catch (OperationCanceledException) { return []; }
            }
            finally { Volatile.Write(ref inPass, false); }
        }
        var rows = await PassAsync(); // The first connection is made inside the first pass, as always.
        int inPassBefore = Volatile.Read(ref connectsInPass);
        var target = rows.Single(row => row.Address.SessionId == "codex:" + owner.Thread.ToString("D"));
        cost = TimeSpan.FromMilliseconds(700);
        owner.SubmitTimesOut = true;
        var uncertain = await adapter.SubmitAsync(new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null,
            new(brokerId, "synthetic-retire-sender", Guid.NewGuid()), target.Address, "Synthetic start-turn whose answer is lost.",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1)), default);
        await Task.Delay(TimeSpan.FromMilliseconds(600)); // The discovery loop's pause.
        var next = await PassAsync();
        int inside = Volatile.Read(ref connectsInPass) - inPassBefore;
        check(uncertain.Outcome == AdapterAdmission.Uncertain && next.Count == 2 && inside == 0 && Volatile.Read(ref connects) == 2,
            "CODEX_SUBMIT_FAILURE_RECONNECTS_IN_PAUSE: after a failed submission the replacement connection is made in the pause, so the next pass renews every row (" +
            next.Count + " rows, " + inside + " connects inside the pass, " + Volatile.Read(ref connects) + " connects)");
        check(Volatile.Read(ref owner.Disposals) == 1,
            "CODEX_SUBMIT_FAILURE_RECONNECTS_IN_PAUSE WITNESS: the connection the failed request used was closed, never reused (" +
            Volatile.Read(ref owner.Disposals) + " closed)");
    }

    private sealed class ReconnectOwner : ICodexOwnerConnection
    {
        internal Guid Thread { get; } = Guid.NewGuid();
        internal Exception? Failure;
        internal bool Closed, SubmitTimesOut;
        internal Guid? Rejected;
        // A conversation whose lookup never answers, so it runs out of time on every pass.
        internal Guid? Hanging { get; init; }
        internal int Disposals;
        public bool NativeIdleGuard => true;
        public Task<DesktopOwnerBinding?> DiscoverAsync(Guid conversationId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (conversationId == Hanging) return HangAsync(token);
            if (Closed) return Task.FromException<DesktopOwnerBinding?>(new DesktopAdapterException("connection_closed"));
            if (Failure is { } failure) return Task.FromException<DesktopOwnerBinding?>(failure);
            if (conversationId == Rejected) return Task.FromException<DesktopOwnerBinding?>(new DesktopAdapterException("owner_discovery_rejected"));
            return Task.FromResult<DesktopOwnerBinding?>(new DesktopOwnerBinding(conversationId, ownerClient, connectionLease, 12345,
                134000000000000000, true) { RouterTrust = DesktopRouterTrust.ExactPinned });
        }
        private readonly Guid ownerClient = Guid.NewGuid(), connectionLease = Guid.NewGuid();
        public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token)
        {
            if (!SubmitTimesOut) throw new NotSupportedException();
            Closed = true; // DesktopAdapter.RequestAsync's catch: the protocol is closed and the answer is Uncertain.
            return Task.FromResult(new DesktopSubmissionResult(DesktopSubmissionOutcome.Uncertain, ErrorCode: "response_timeout_or_cancelled"));
        }
        private static async Task<DesktopOwnerBinding?> HangAsync(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return null;
        }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return ValueTask.CompletedTask; }
    }

    // N-agentbroker-native-flicker-01, the runtime half: one adapter's slow pass (a transport that outlives the pass
    // deadline and ignores it) must not let another adapter's leases lapse. Scaled down: pass deadline and pause 300 ms,
    // the other adapter's lease 3 s, the slow pass 5 s. When the adapters ran one after another, the other adapter was
    // renewed only every 5.3 s and its row was missing for about 2.3 s of each cycle; run independently it renews every
    // 0.3 s, which leaves about 2.7 s of margin for a stalled thread pool under load (independent review P3-8: 1.2 s
    // and 2 s left only 0.9 s).
    private static async Task IndependentAdapterLoopsAsync(Action<bool, string> check, string scratch)
    {
        using var store = new BrokerStore(Path.Combine(scratch, "independent-loops"));
        await using var federation = new SlowDirectoryFederation(Guid.Empty);
        var slow = new TimedAdapter("slow-native", store.BrokerId, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3), ignoreCancellation: true);
        var peer = new TimedAdapter("peer-native", store.BrokerId, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        var runtime = new BrokerRuntime(store, federation, [slow, peer])
        { DiscoveryPassDeadline = TimeSpan.FromMilliseconds(300), DiscoveryInterval = TimeSpan.FromMilliseconds(300) };
        await using (runtime)
        {
            runtime.Start(default);
            var first = Stopwatch.StartNew();
            while (!store.Directory().Any(session => session.Address == peer.Address) && first.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
            int samples = 0, missing = 0;
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(8))
            {
                samples++;
                if (!store.Directory().Any(session => session.Address == peer.Address)) missing++;
                await Task.Delay(50);
            }
            check(samples >= 40 && missing == 0 && peer.Passes >= 10,
                "RUNTIME_ADAPTERS_INDEPENDENT: a slow adapter's pass never lets another adapter's lease lapse (" + missing + " of " + samples + " samples missing)");
            check(slow.Passes >= 1 && slow.Completed >= 1,
                "RUNTIME_ADAPTERS_INDEPENDENT WITNESS: the slow adapter ran its long pass past the deadline");
        }
    }

    // Independent review of 1.6.0, P3-7: with one loop per adapter, an exception outside the loop's catch list ended that
    // adapter's discovery in silence while the other went on. It is now that adapter's error and the loop tries again.
    private static async Task AdapterFailureRecordedAsync(Action<bool, string> check, string scratch)
    {
        using var store = new BrokerStore(Path.Combine(scratch, "adapter-failure"));
        await using var federation = new SlowDirectoryFederation(Guid.Empty);
        var broken = new LifecycleAdapter("broken-native", faultDiscovery: true);
        var peer = new TimedAdapter("peer-native", store.BrokerId, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        var runtime = new BrokerRuntime(store, federation, [broken, peer])
        { DiscoveryPassDeadline = TimeSpan.FromMilliseconds(300), DiscoveryInterval = TimeSpan.FromMilliseconds(100) };
        runtime.Start(default);
        var watch = Stopwatch.StartNew();
        while (Volatile.Read(ref broken.Passes) < 3 && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        int passes = Volatile.Read(ref broken.Passes);
        bool recorded = runtime.AdapterErrors.TryGetValue("broken-native", out var error) && error == "adapter_failed";
        bool peerListed = store.Directory().Any(session => session.Address == peer.Address);
        // Disposed before the checks: a loop that died would rethrow its fault here and hide the check's own label.
        try { await runtime.DisposeAsync(); } catch (Exception) { }
        check(passes >= 3 && recorded,
            "RUNTIME_ADAPTER_FAILURE_RECORDED: an unexpected discovery failure is that adapter's error and its loop keeps trying (" + passes + " passes)");
        check(peerListed, "RUNTIME_ADAPTER_FAILURE_RECORDED WITNESS: the other adapter keeps its row");
    }

    // N-agentbroker-native-flicker-01, the Codex half: a conversation whose owner lookup outlives the pass deadline is
    // skipped and goes last on the next pass, so every other conversation is still renewed; a lookup failing at once is
    // skipped without ending the pass. The diagnostic says so on every pass that skipped one.
    private static async Task SlowCodexRowAsync(Action<bool, string> check)
    {
        var provider = new SlowRowProvider(3);
        await using var adapter = new CodexNativeAdapter(provider);
        var brokerId = Guid.NewGuid();
        async Task<IReadOnlyList<BrokerSession>> PassAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            return await adapter.DiscoverAsync(brokerId, deadline.Token);
        }
        string Ids(IReadOnlyList<BrokerSession> sessions) => string.Join(",", sessions.Select(session =>
            Array.IndexOf(provider.Threads, Guid.Parse(session.Address.SessionId["codex:".Length..]))).Order());
        provider.Slow = provider.Threads[1];
        IReadOnlyList<BrokerSession>? firstPass = null;
        try { firstPass = await PassAsync(); } catch (OperationCanceledException) { }
        check(firstPass is not null && Ids(firstPass) == "0",
            "CODEX_SLOW_ROW_PARTIAL: a lookup that outlives the pass deadline ends the pass with what was resolved, not a failure (" + (firstPass is null ? "failed" : Ids(firstPass)) + ")");
        check(adapter.DiscoveryDiagnostics.Any(item => item.Code == "codex_owner_lookup_skipped" && item.Description.StartsWith("2 Codex conversations", StringComparison.Ordinal)),
            "CODEX_SLOW_ROW_DIAGNOSTIC: the pass that skipped conversations names how many");
        var second = await PassAsync();
        check(Ids(second) == "0,2" && provider.Order[^3..].SequenceEqual(new[] { provider.Threads[0], provider.Threads[2], provider.Threads[1] }),
            "CODEX_SLOW_ROW_LAST: the slow conversation goes last on the next pass, so every other one is renewed (" + Ids(second) + ")");
        provider.Slow = null; provider.Failing = provider.Threads[0];
        IReadOnlyList<BrokerSession>? third = null;
        try { third = await PassAsync(); } catch (IOException) { }
        check(third is not null && Ids(third) == "1,2",
            "CODEX_SLOW_ROW_FAILING: a lookup that fails at once is skipped and the pass goes on (" + (third is null ? "failed" : Ids(third)) + ")");
        provider.Failing = null;
        // The router's error answer for one conversation (owner_discovery_rejected) skips that row, not the pass
        // (independent review of 1.6.0, P3-5).
        provider.Rejected = provider.Threads[1];
        IReadOnlyList<BrokerSession>? rejectedPass = null;
        try { rejectedPass = await PassAsync(); } catch (AdapterDiscoveryException) { }
        check(rejectedPass is not null && Ids(rejectedPass) == "0,2",
            "CODEX_SLOW_ROW_REJECTED: one conversation's owner_discovery_rejected skips that row and the others are renewed (" +
            (rejectedPass is null ? "failed" : Ids(rejectedPass)) + ")");
        provider.Rejected = null;
        var healthy = await PassAsync();
        check(Ids(healthy) == "0,1,2" && !adapter.DiscoveryDiagnostics.Any(item => item.Code == "codex_owner_lookup_skipped"),
            "CODEX_SLOW_ROW_DIAGNOSTIC WITNESS: a pass that resolves every conversation renews all and reports no skip");
    }

    private sealed class TimedAdapter(string id, Guid brokerId, TimeSpan pass, TimeSpan lease, bool ignoreCancellation = false) : ILocalAgentAdapter
    {
        public string Id => id;
        public SessionAddress Address { get; } = new(brokerId, id + ":" + Guid.NewGuid().ToString("D"), Guid.NewGuid());
        internal int Passes, Completed;
        public async Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid broker, CancellationToken token)
        {
            Interlocked.Increment(ref Passes);
            if (pass > TimeSpan.Zero) await Task.Delay(pass, ignoreCancellation ? CancellationToken.None : token);
            Interlocked.Increment(ref Completed);
            return [new BrokerSession(Address, "Synthetic " + id, id, SessionCapabilities.Receive | SessionCapabilities.WakeIdle,
                SessionActivity.Idle, DateTimeOffset.UtcNow + lease)];
        }
        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // A provider with three observed conversations; one may hang until the pass deadline or fail at once.
    private sealed class SlowRowProvider(int count) : ICodexNativeProvider
    {
        internal Guid[] Threads { get; } = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        internal Guid? Slow, Failing, Rejected;
        internal List<Guid> Order { get; } = [];
        public bool NativeIdleGuard => false;
        public Task<IReadOnlyList<DesktopDirectorySession>> ObserveAsync(CancellationToken token)
            => Task.FromResult<IReadOnlyList<DesktopDirectorySession>>(Threads.Select(thread =>
                new DesktopDirectorySession(thread, DesktopSessionActivity.Idle, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)).ToArray());
        public async Task<DesktopOwnerBinding?> ResolveOwnerAsync(Guid conversationId, CancellationToken token)
        {
            Order.Add(conversationId);
            if (conversationId == Slow) await Task.Delay(Timeout.Infinite, token);
            if (conversationId == Failing) throw new IOException("Synthetic owner connection failure.");
            if (conversationId == Rejected) throw new DesktopAdapterException("owner_discovery_rejected");
            return new DesktopOwnerBinding(conversationId, Guid.NewGuid(), Guid.NewGuid(), 12345, 134000000000000000, true)
            { RouterTrust = DesktopRouterTrust.ExactPinned };
        }
        public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LifecycleAdapter(string id, bool faultDiscovery = false, bool failDispose = false, bool listSession = false) : ILocalAgentAdapter
    {
        public string Id => id;
        internal int DisposeCalls, Passes, Submissions;
        internal SessionAddress? Address { get; private set; }
        public Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token)
        {
            Interlocked.Increment(ref Passes);
            if (faultDiscovery) return Task.FromException<IReadOnlyList<BrokerSession>>(new InvalidOperationException("Synthetic discovery failure."));
            if (!listSession) return Task.FromResult<IReadOnlyList<BrokerSession>>([]);
            Address ??= new(brokerId, id + ":synthetic", Guid.NewGuid());
            return Task.FromResult<IReadOnlyList<BrokerSession>>([new BrokerSession(Address, "Synthetic " + id, id,
                SessionCapabilities.Receive | SessionCapabilities.WakeIdle, SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(1))]);
        }
        // An unexpected delivery failure: it still faults the runtime's delivery loop, which shutdown reports.
        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token)
        { Interlocked.Increment(ref Submissions); throw new NotSupportedException("Synthetic delivery failure."); }
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return failDispose ? ValueTask.FromException(new IOException("Synthetic disposal failure.")) : ValueTask.CompletedTask;
        }
    }

    private sealed class SlowDirectoryFederation(Guid readyPeer) : IPeerFederation
    {
        private readonly ConcurrentBag<TaskCompletionSource<PeerDirectory>> pending = [];
        private int active, calls, peak;
        internal int Calls => Volatile.Read(ref calls);
        internal int Active => Volatile.Read(ref active);
        internal int PeakConcurrent => Volatile.Read(ref peak);
        internal Action<PeerTrust>? OnReady;
        internal bool UnavailablePeersReturnEmpty;
        public IReadOnlyList<BrokerAdvertisement> Discovered => [];
        public IReadOnlyList<PairingOffer> PairingOffers => [];
        public BrokerAdvertisement LocalAdvertisement => new(Guid.NewGuid(), "Synthetic", "https://127.0.0.1:15000", new string('A', 64), DateTimeOffset.UtcNow);
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
            try
            {
                if (peer.BrokerId == readyPeer)
                {
                    await Task.Delay(25, token);
                    var session = new BrokerSession(new(peer.BrokerId, "synthetic-ready", Guid.NewGuid()), "Ready peer", "fake",
                        SessionCapabilities.Receive, SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(1));
                    OnReady?.Invoke(peer);
                    return new(peer.BrokerId, [session, session with { IsPrivate = true }, session with { LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1) },
                        session with { Address = session.Address with { BrokerId = Guid.NewGuid() } }]);
                }
                if (UnavailablePeersReturnEmpty) return new(peer.BrokerId, []);
                // Intentionally ignores cancellation to exercise the public directory's hard wait bound.
                var completion = new TaskCompletionSource<PeerDirectory>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending.Add(completion);
                return await completion.Task;
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public Task<MessageReceipt> SubmitAsync(PeerTrust peer, BrokerMessage message, CancellationToken token) => throw new NotSupportedException();
        public Task<MessageReceipt?> GetReceiptAsync(PeerTrust peer, Guid messageId, CancellationToken token) => Task.FromResult<MessageReceipt?>(null);
        internal void ReleasePending()
        { foreach (var completion in pending) completion.TrySetResult(new(Guid.Empty, [])); }
        internal async Task DrainAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (Active != 0) await Task.Delay(10, deadline.Token);
        }
        public async ValueTask DisposeAsync() { ReleasePending(); await DrainAsync(); }
    }
}
