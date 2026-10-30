using AgentBroker.Core;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace AgentBroker.Worker;

public sealed class BrokerRuntime(BrokerStore store, IPeerFederation federation,
    IReadOnlyList<ILocalAgentAdapter> adapters, SessionPreferences? sessionPreferences = null,
    TimeProvider? clock = null, ICallerProcessResolver? callerProcesses = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, string> adapterErrors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> adapterNotes = new(StringComparer.Ordinal);
    private readonly DeferredDeliveryClaims claims = new(store);
    private readonly DeliveryObservations observations = new(clock ?? TimeProvider.System);
    private readonly ConcurrentDictionary<string, BrokerSession> observed = new(StringComparer.Ordinal);
    private readonly object sessionGate = new();
    private readonly Dictionary<Guid, Guid> receiptCursors = new();
    private int nextReceiptPeer;
    private CancellationTokenSource? lifetime;
    private Task? loop;
    private readonly object disposalGate = new();
    private Task? disposal;
    public BrokerStore Store => store;
    public IPeerFederation Federation => federation;
    public IReadOnlyDictionary<string, string> AdapterErrors => adapterErrors;
    // Count-only notes about some of an adapter's sessions (AdapterDiscoveryDiagnostic.Note), from its last completed pass.
    public IReadOnlyDictionary<string, string> AdapterNotes => adapterNotes;
    public event Action? FatalError;
    public bool IsMuted(string sessionId) => sessionPreferences?.IsMuted(sessionId) == true;
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    // The store clock, for a tool that must wake at a message deadline (BrokerTools.WaitForReceipt).
    internal DateTimeOffset StoreNow => Now;

    // A local native row's last engine observation, from the adapter that discovered it (report only). Null for a paired
    // broker's row, a generic mailbox, or an adapter that observes nothing.
    internal EngineBinding? EngineBindingOf(BrokerSession session) => session.Address.BrokerId != store.BrokerId ? null
        : adapters.FirstOrDefault(adapter => adapter.Id == session.Adapter)?.EngineBindingOf(session.Address);

    // The sender's view of a receipt: no text unless asked, its own read time, and since when its recipient has been seen
    // busy (SenderReceipt, DeliveryObservations). A paired broker's caller (forPeer) gets neither busySince nor the engine
    // binding: both are this workstation's observations of its own processes, which a paired broker is never sent, the
    // same rule as list_sessions (review of 25a0c65, finding 2).
    internal SenderReceipt View(MessageReceipt receipt, bool includeText = false, bool forPeer = false) => forPeer
        ? SenderReceipt.From(receipt, Now, includeText)
        : SenderReceipt.From(receipt, Now, includeText, observations.BusySince(receipt), observations.EngineAtAttempt(receipt.Message.Id));

    public void SetSessionPreference(string sessionId, bool? isPrivate, bool? isMuted)
    {
        lock (sessionGate)
        {
            if (sessionPreferences is null) throw new BrokerException("preferences_unavailable", "Preferences are unavailable.");
            if (!observed.TryGetValue(sessionId, out var session) || !Observable(session))
                throw new BrokerException("recipient_unavailable", "This session is no longer available.");
            sessionPreferences.Set(sessionId, isPrivate, isMuted, id => observed.TryGetValue(id, out var present) && Observable(present));
            // A retained mailbox has no live lease to register with, and the owner's choice is not the
            // agent's activity: apply it in place without restoring presence or extending retention.
            if (session.LeaseUntil > Now) store.Register(sessionPreferences.Apply(session));
            else store.ApplyRetainedPreference(sessionPreferences.Apply(session));
        }
    }

    // A native observation ends with its lease. A generic MCP mailbox ends when the store stops retaining it.
    private bool Observable(BrokerSession session) => session.LeaseUntil > Now ||
        session.Adapter == "mcp" && store.IsAddressable(session.Address);

    private void Register(BrokerSession session)
    {
        lock (sessionGate)
        {
            foreach (var stale in observed.Where(p => !Observable(p.Value)).Select(p => p.Key))
                observed.TryRemove(stale, out _);
            if (observed.Count >= 256 && !observed.ContainsKey(session.Address.SessionId))
            {
                // A retained mailbox gives up its slot before a registration is refused; a present session never does.
                if (store.EvictRetainedMailbox() is { } evicted && observed.TryGetValue(evicted.SessionId, out var entry) &&
                    entry.Address == evicted) observed.TryRemove(evicted.SessionId, out _);
                if (observed.Count >= 256) throw new BrokerException("session_limit", "Session observation limit reached.");
            }
            store.Register(sessionPreferences?.Apply(session) ?? session);
            observed[session.Address.SessionId] = session;
        }
    }

    internal void RenewPollingAgent(SessionAddress address, Guid caller)
    {
        if (caller != store.BrokerId || address.BrokerId != caller) return;
        lock (sessionGate)
        {
            if (!observed.TryGetValue(address.SessionId, out var session) || session.Address != address ||
                session.Adapter != "mcp" || !store.IsAddressable(address)) return;
            var renewed = store.RenewPollingSession(address);
            // Preserve the original registration under the preference overlay. A replacement
            // incarnation registered concurrently must never be overwritten by old activity.
            if (renewed is not null) observed[address.SessionId] = session with { LeaseUntil = renewed.LeaseUntil };
        }
    }

    // Blocking MCP waits hold an HTTP request each. One per session address (a second would only race
    // the first for the same messages) and sixteen per worker, well inside Kestrel's connection bound.
    internal const int MaximumConcurrentWaits = 16;
    private readonly ConcurrentDictionary<SessionAddress, byte> waits = new();
    private int activeWaits;

    internal IDisposable BeginWait(SessionAddress session)
    {
        if (!waits.TryAdd(session, 0)) throw new BrokerException("wait_in_progress", "This session already has an active wait.");
        if (Interlocked.Increment(ref activeWaits) > MaximumConcurrentWaits)
        {
            Interlocked.Decrement(ref activeWaits); waits.TryRemove(session, out _);
            throw new BrokerException("wait_capacity", "The broker's concurrent wait capacity is in use.");
        }
        store.SetListening(session, true);
        return new WaitSlot(this, session);
    }

    private sealed class WaitSlot(BrokerRuntime owner, SessionAddress session) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            owner.Store.SetListening(session, false);
            Interlocked.Decrement(ref owner.activeWaits); owner.waits.TryRemove(session, out _);
        }
    }

    public void Start(CancellationToken stop)
    {
        if (loop is not null) throw new InvalidOperationException("Broker runtime already started.");
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        loop = RunAsync(lifetime.Token);
    }

    public BrokerSession RegisterPollingAgent(Guid clientId, Guid incarnation, string name, bool isPrivate)
    {
        if (clientId == Guid.Empty || incarnation == Guid.Empty || string.IsNullOrWhiteSpace(name) || name.Length > 160)
            throw new BrokerException("invalid_session", "A stable client ID, incarnation, and label are required.");
        var session = new BrokerSession(new(store.BrokerId, "mcp:" + clientId.ToString("D"), incarnation), name, "mcp",
            SessionCapabilities.Receive | SessionCapabilities.Reply, SessionActivity.Idle, Now.AddMinutes(1), isPrivate);
        Register(session); return sessionPreferences?.Apply(session) ?? session;
    }

    public async Task<IReadOnlyList<BrokerSession>> DirectoryAsync(bool forPeer, CancellationToken token)
    {
        // The presented view: a mailbox nobody is waiting on reads Unknown, here and in /federation/directory alike.
        var result = store.PresentedDirectory(forPeer).ToList();
        if (!forPeer)
        {
            // A native row discovery could only call Unknown, whose recipient answered "a turn is still running" to a
            // delivery attempt within the last 30 seconds, is listed Busy (N-broker-busy-visibility-01): a sender saw Unknown
            // for two hours while every attempt said busy. Presentation only, local list only: the stored activity, and so
            // which message is claimed and when (BrokerStore.Ready), are unchanged.
            for (int index = 0; index < result.Count; index++)
                if (result[index] is { Activity: SessionActivity.Unknown } row && observations.SeenBusy(row.Address))
                    result[index] = row with { Activity = SessionActivity.Busy };
            var peers = store.Peers();
            var authorizations = peers.ToDictionary(p => p.BrokerId, store.CapturePeerAuthorization);
            var directories = new ConcurrentDictionary<Guid, BrokerSession[]>();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await Parallel.ForEachAsync(peers, new ParallelOptions
                { MaxDegreeOfParallelism = 4, CancellationToken = budget.Token }, async (peer, peerToken) =>
                {
                    try
                    {
                        // The outer wait preserves the whole-directory deadline even if a transport
                        // takes longer to observe cancellation. Late results cannot publish sessions.
                        var remote = await federation.GetDirectoryAsync(peer, peerToken).WaitAsync(peerToken);
                        if (remote.BrokerId != peer.BrokerId || remote.Sessions.Count > 256) return;
                        directories[peer.BrokerId] = remote.Sessions.Where(s => s.Address.BrokerId == peer.BrokerId &&
                            !s.IsPrivate && s.LeaseUntil > DateTimeOffset.UtcNow).Take(256).ToArray();
                    }
                    // A paired broker's body the strict federation options refuse (an unknown member from a newer release, a
                    // truncated answer) is that peer's failure for this listing, as PeerDiagnostics already reads it
                    // (invalid_directory); it used to fail list_sessions for every local caller (A-broker-02).
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or
                        HttpRequestException or BrokerException or OperationCanceledException or System.Text.Json.JsonException)
                    { peerToken.ThrowIfCancellationRequested(); }
                });
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested && !token.IsCancellationRequested)
            { /* Return local and already completed peer directories within the global budget. */ }
            token.ThrowIfCancellationRequested();
            // Stable peer ordering keeps partial results predictable despite concurrent lookups.
            foreach (var peer in peers)
                if (authorizations[peer.BrokerId] is { } authorization && store.IsPeerAuthorized(authorization) &&
                    directories.TryGetValue(peer.BrokerId, out var sessions)) result.AddRange(sessions);
        }
        return result;
    }

    private async Task RunAsync(CancellationToken stop)
    {
        await Task.WhenAll(new[] { GuardAsync("discovery", DiscoverAsync, stop), GuardAsync("receipts", ReceiptLoopAsync, stop) }
            .Concat(Enumerable.Range(0, 4).Select(_ => GuardAsync("delivery", DeliveryLoopAsync, stop))));
    }

    // The pause before a loop that failed unexpectedly runs again: doubled after each failure up to the limit, and back to
    // the first pause once the loop has run longer than the limit without failing. Internal so the lifecycle checks can
    // scale them down.
    internal TimeSpan LoopRestartDelay { get; init; } = TimeSpan.FromSeconds(1);
    internal TimeSpan LoopRestartLimit { get; init; } = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, int> loopFailures = new(StringComparer.Ordinal);

    private async Task GuardAsync(string name, Func<CancellationToken, Task> run, CancellationToken stop)
    {
        var pause = LoopRestartDelay;
        while (true)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                await run(stop);
                return;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception e) when (e is IOException or InvalidDataException or BrokerException or UnauthorizedAccessException)
            {
                adapterErrors["broker"] = "durable_state_unavailable";
                lifetime?.Cancel(); FatalError?.Invoke();
                return;
            }
            // Any other exception ended the loop in silence (N-broker-161-02): a receipt loop that stopped meant no outbound
            // message got its receipt again, and the four delivery loops could end one by one, each as quietly, until
            // nothing was delivered; Status showed nothing and the fault surfaced only at shutdown. The discovery loops
            // already record such a failure and try again (P3-7); these now do the same, under the loop's own name, and
            // the entry stays until the worker restarts, with a count and the last time, so a one-off reads differently
            // from a loop that keeps failing. The type is not shown: it could carry a vendor message. A fault while the
            // runtime is stopping still ends the loop and is reported by DisposeAsync, as before.
            catch (Exception) when (!stop.IsCancellationRequested)
            {
                int failures = loopFailures.AddOrUpdate(name, 1, (_, count) => count + 1);
                adapterErrors[name] = "loop_failed: " + failures + (failures == 1 ? " unexpected failure" : " unexpected failures") +
                    ", restarted each time; the last at " + Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " UTC.";
            }
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) > LoopRestartLimit) pause = LoopRestartDelay;
            try { await Task.Delay(pause, stop); }
            catch (OperationCanceledException) { return; }
            pause = TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, LoopRestartLimit.Ticks));
        }
    }

    // Discovery timing: each adapter's pass deadline and the pause after it. Internal so the lifecycle checks can scale
    // them down; production keeps ten seconds each, inside the 30-second native leases a pass renews.
    internal TimeSpan DiscoveryPassDeadline { get; init; } = TimeSpan.FromSeconds(10);
    internal TimeSpan DiscoveryInterval { get; init; } = TimeSpan.FromSeconds(10);

    // One discovery loop per adapter (N-agentbroker-native-flicker-01). The adapters used to run one after another in a
    // single loop, so a slow pass in one (a Codex owner lookup can spend its router's whole discovery timeout, and a
    // transport may outlive the pass deadline) postponed every other adapter's renewal by that long, and the next
    // adapter's 30-second leases could lapse while its sessions were live. Each adapter now renews on its own schedule;
    // a slow one delays only itself.
    private Task DiscoverAsync(CancellationToken stop) => Task.WhenAll(adapters.Select(adapter => DiscoverLoopAsync(adapter, stop)));

    private async Task DiscoverLoopAsync(ILocalAgentAdapter adapter, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                deadline.CancelAfter(DiscoveryPassDeadline);
                var sessions = await adapter.DiscoverAsync(store.BrokerId, deadline.Token);
                // An adapter may now end a pass early with what it resolved (CodexNativeAdapter); at shutdown, nothing registers.
                stop.ThrowIfCancellationRequested();
                if (sessions.Count > 256) throw new BrokerException("session_limit", "Adapter directory exceeded its bound.");
                foreach (var session in sessions) Register(session);
                var reported = adapter.DiscoveryDiagnostics.Take(8).ToArray();
                var diagnostics = reported.Where(item => !item.Note).Select(item => item.ToStatusText()).Distinct().ToArray();
                if (diagnostics.Length == 0) adapterErrors.TryRemove(adapter.Id, out _);
                else adapterErrors[adapter.Id] = string.Join("; ", diagnostics);
                var notes = reported.Where(item => item.Note).Select(item => item.ToStatusText()).Distinct().ToArray();
                if (notes.Length == 0) adapterNotes.TryRemove(adapter.Id, out _);
                else adapterNotes[adapter.Id] = string.Join("; ", notes);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or BrokerException or
                UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                if (stop.IsCancellationRequested) throw;
                adapterNotes.TryRemove(adapter.Id, out _); // A failed pass completed nothing to note.
                adapterErrors[adapter.Id] = e is AdapterDiscoveryException diagnostic ? diagnostic.Diagnostic.ToStatusText()
                    : e is BrokerException b ? b.Code : "adapter_unavailable";
            }
            // Any other failure is this adapter's alone. With one loop per adapter it used to end that adapter's discovery in
            // silence while the others went on (its rows lapsed, its status kept its last value, and nothing surfaced until
            // shutdown): it is now that adapter's error, and the loop tries again next pass (independent review of 1.6.0,
            // P3-7). The type is not shown: it could carry a vendor message.
            catch (Exception) when (!stop.IsCancellationRequested) { adapterErrors[adapter.Id] = "adapter_failed"; }
            await Task.Delay(DiscoveryInterval, stop);
        }
    }

    private async Task DeliveryLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var pending = claims.BeginDelivery();
            if (pending is not null) await DeliverAsync(pending, stop);
            await Task.Delay(pending is null ? 500 : 50, stop);
        }
    }

    private async Task ReceiptLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        { await ReconcilePeersAsync(stop); await Task.Delay(TimeSpan.FromSeconds(5), stop); }
    }

    // whoami: the session owned by the nearest process in the caller's ancestry that an adapter binds to exactly one
    // directory row. The walk ends at the nearest claude or codex process even when that process is no discovered session,
    // so an agent another agent started (claude -p inside a Claude session) is never told that it is its parent.
    // caller_elevated (N-broker-elevated-caller-01): the connection's owner was found but even a limited query of it was
    // denied, which from this Medium-integrity broker means an elevated process or another user's. It is deliberately NOT
    // bridged: verifying, waking or injecting into a process above the broker's integrity would let any Medium process
    // drive an elevated one. The caller is told what it can do instead (a mailbox, or run unelevated); caller_unresolved
    // keeps meaning that no owner was found at all.
    public (BrokerSession? Session, string Code) WhoAmI(System.Net.IPAddress client, int clientPort, int serverPort)
    {
        var resolver = callerProcesses ?? WindowsCallerProcessResolver.Instance;
        var resolution = resolver.ResolveCaller(client, clientPort, serverPort);
        var chain = resolution.Chain;
        if (chain.Count == 0) return (null, resolution.OwnerAccessDenied ? "caller_elevated" : "caller_unresolved");
        var directory = store.Directory();
        CallerProcess? nearestAgent = null;
        foreach (var process in chain)
        {
            foreach (var adapter in adapters)
                if (adapter.FindSessionByProcess(process.ProcessId, process.StartFileTime) is { } address)
                    return directory.SingleOrDefault(session => session.Address == address) is { } row ? (row, "ok") : (null, "no_native_session");
            if (IsAgentImage(process.ImageName)) { nearestAgent = process; break; }
        }
        // No adapter binds the chain. An elevated launch that allowed the limited query got this far (review of 25a0c65,
        // finding 1): the nearest claude or codex process it walked to is asked whether a full query is refused, which for
        // this user's agent means elevated. Only that process: a parent the broker cannot fully query proves nothing.
        return (null, nearestAgent is not null && resolver.QueryDenied(nearestAgent) ? "caller_elevated" : "no_native_session");
    }

    private static bool IsAgentImage(string image) => Path.GetFileNameWithoutExtension(image) is { } name &&
        (name.Equals("claude", StringComparison.OrdinalIgnoreCase) || name.Equals("codex", StringComparison.OrdinalIgnoreCase));

    // The envelope's description of a sender: its own registration label when it is a session on this broker (null once it
    // is gone), or the name its paired broker advertised when the pairing was approved. Neither identifies or authorizes.
    public PeerSenderInfo DescribeSender(SessionAddress sender) => sender.BrokerId == store.BrokerId
        ? new(store.Directory().SingleOrDefault(session => session.Address == sender)?.Label, "local")
        : new(null, "paired broker \"" + (store.Peers().SingleOrDefault(peer => peer.BrokerId == sender.BrokerId)?.Name ?? "unknown") + "\" " +
            sender.BrokerId.ToString("D"));

    private async Task DeliverAsync(MessageReceipt receipt, CancellationToken stop)
    {
        AdapterResult result;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            if (receipt.Message.Recipient.BrokerId != store.BrokerId)
            {
                var peer = store.Peers().SingleOrDefault(p => p.BrokerId == receipt.Message.Recipient.BrokerId);
                if (peer is null) return; // RevokePeer already resolves this claim truthfully.
                var remote = await federation.SubmitAsync(peer, receipt.Message, deadline.Token);
                if (remote.Message != receipt.Message) throw new BrokerException("receipt_mismatch", "Peer returned a different receipt.");
                result = new(remote.Status switch
                {
                    MessageStatus.Delivered => AdapterAdmission.Delivered,
                    MessageStatus.Held => AdapterAdmission.Held,
                    MessageStatus.Refused or MessageStatus.Expired => AdapterAdmission.Refused,
                    MessageStatus.Uncertain => AdapterAdmission.Uncertain,
                    _ => AdapterAdmission.Submitted,
                // A paired broker that answers the forward itself with Expired: the same peer_expired reason and notice as
                // the receipt loop's BrokerStore.RecordPeerReceipt (Codex 01a1855c's review of 3f6c2a3).
                }, remote.Status == MessageStatus.Expired ? "peer_expired" : "peer_receipt");
            }
            else
            {
                var target = store.Directory().SingleOrDefault(s => s.Address == receipt.Message.Recipient);
                var adapter = adapters.SingleOrDefault(a => a.Id == target?.Adapter);
                result = adapter is null ? new(AdapterAdmission.Refused, "adapter_unavailable")
                    : await adapter.SubmitAsync(receipt.Message, DescribeSender(receipt.Message.Sender), deadline.Token);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or HttpRequestException or BrokerException or
            UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { result = new(AdapterAdmission.Uncertain, "submission_unconfirmed"); }
        // A paired broker's answer the strict federation options refuse escaped this loop: the loop ended, the claim stayed
        // Submitting and held every later message to that recipient until a restart (A-broker-02). The forward may have
        // been admitted, so it is Uncertain, with a reason that names the cause.
        catch (System.Text.Json.JsonException) { result = new(AdapterAdmission.Uncertain, "peer_answer_invalid"); }
        // Any other exception out of a submission (an adapter's or a transport's defect) did the same as that one: the claim
        // stayed Submitting, holding every later message to that recipient until a restart, and the loop ended
        // (N-broker-161-02). Restarting the loop alone would leave the claim held, so the message settles here. The
        // submission may have been written, so it is Uncertain like any unconfirmed one, under its own reason; the type is
        // not recorded, as it could carry a vendor message.
        catch (Exception) { result = new(AdapterAdmission.Uncertain, "submission_failed"); }
        // What this attempt learned about a local recipient, for the sender's receipt and the directory. Observation only.
        if (receipt.Message.Recipient.BrokerId == store.BrokerId)
            observations.Record(receipt.Message, result, id => store.FindReceipt(id) is { Status: MessageStatus.Accepted or MessageStatus.Submitting });
        // A revocation may have resolved an in-flight claim while its IO was completing.
        if (store.FindReceipt(receipt.Message.Id)?.Status != MessageStatus.Submitting) return;
        try
        {
            if (result.Outcome == AdapterAdmission.DeferredNotSubmitted)
            {
                claims.ReleaseUnsubmittedClaim(receipt.Message.Id, result.Reason);
                return;
            }
            store.CompleteDelivery(receipt.Message.Id, result.Outcome switch
            {
                AdapterAdmission.Delivered => MessageStatus.Delivered,
                AdapterAdmission.Held => MessageStatus.Held,
                AdapterAdmission.Refused => MessageStatus.Refused,
                AdapterAdmission.Submitted => MessageStatus.Submitted,
                _ => MessageStatus.Uncertain,
            }, result.Reason);
        }
        catch (BrokerException e) when (e.Code == "message_not_submitting")
        { /* A concurrent acknowledgement or revocation already settled this claim. */ }
    }

    private async Task ReconcilePeersAsync(CancellationToken stop)
    {
        var peers = store.Peers();
        if (peers.Count == 0) { receiptCursors.Clear(); nextReceiptPeer = 0; return; }
        foreach (var stale in receiptCursors.Keys.Where(id => !peers.Any(p => p.BrokerId == id)).ToArray())
            receiptCursors.Remove(stale);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(stop);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        for (int i = 0; i < peers.Count && !budget.IsCancellationRequested; i++)
        {
            var peer = peers[nextReceiptPeer % peers.Count];
            nextReceiptPeer = (nextReceiptPeer + 1) % peers.Count;
            // A bounded query covers only this broker's outbound registrations, never third-party histories.
            var messages = store.OutboundAwaitingReceipt(peer.BrokerId, 64, receiptCursors.GetValueOrDefault(peer.BrokerId));
            foreach (var message in messages)
            {
                if (budget.IsCancellationRequested) break;
                receiptCursors[peer.BrokerId] = message.Message.Id;
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(1));
                    var remote = await federation.GetReceiptAsync(peer, message.Message.Id, deadline.Token);
                    if (remote?.Message != message.Message || remote.Status is not
                        (MessageStatus.Delivered or MessageStatus.Held or MessageStatus.Refused or MessageStatus.Expired)) continue;
                    store.RecordPeerReceipt(message.Message.Id, message.Message.Recipient, peer.BrokerId, remote.Status);
                }
                // An unreadable receipt answer is that message's failure for this pass; it used to end the receipt loop, so
                // no outbound message to any peer got its receipt again (A-broker-02).
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or
                    HttpRequestException or BrokerException or OperationCanceledException or System.Text.Json.JsonException)
                { if (stop.IsCancellationRequested) throw; }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate) return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        var failures = new List<Exception>();
        try { lifetime?.Cancel(); }
        catch (Exception error) { failures.Add(error); }
        try { if (loop is not null) await loop; }
        catch (Exception error) { failures.Add(error); }
        // Every adapter owns independent native resources. One failure cannot skip the others.
        foreach (var adapter in adapters)
            try { await adapter.DisposeAsync(); }
            catch (Exception error) { failures.Add(error); }
        try { lifetime?.Dispose(); }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Broker runtime shutdown failed.", failures);
    }
}
