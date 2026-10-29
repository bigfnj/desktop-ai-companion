using AgentBroker.Core;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace AgentBroker.Worker;

public sealed class BrokerRuntime(BrokerStore store, IPeerFederation federation,
    IReadOnlyList<ILocalAgentAdapter> adapters, SessionPreferences? sessionPreferences = null,
    TimeProvider? clock = null, ICallerProcessResolver? callerProcesses = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, string> adapterErrors = new(StringComparer.Ordinal);
    private readonly DeferredDeliveryClaims claims = new(store);
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
    public event Action? FatalError;
    public bool IsMuted(string sessionId) => sessionPreferences?.IsMuted(sessionId) == true;
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    // The store clock, for a tool that must wake at a message deadline (BrokerTools.WaitForReceipt).
    internal DateTimeOffset StoreNow => Now;

    public void SetSessionPreference(string sessionId, bool? isPrivate, bool? isMuted)
    {
        lock (sessionGate)
        {
            if (sessionPreferences is null) throw new BrokerException("preferences_unavailable", "Preferences are unavailable.");
            if (!observed.TryGetValue(sessionId, out var session) || !Observable(session))
                throw new BrokerException("recipient_unavailable", "This session is no longer available.");
            sessionPreferences.Set(sessionId, isPrivate, isMuted);
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
        return new WaitSlot(this, session);
    }

    private sealed class WaitSlot(BrokerRuntime owner, SessionAddress session) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
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
        var result = store.Directory(forPeer).ToList();
        // A generic mailbox reads Idle only while its owner is inside a wait on this broker. Registering or renewing proves
        // nobody reads it: one unread for hours looked live to its senders (N-agentbroker-mailbox-activity-01, 2026-10-28),
        // so it reads Unknown. The store keeps Idle as its presence state; native rows are untouched.
        for (int i = 0; i < result.Count; i++)
            if (result[i].Adapter == "mcp" && result[i].Activity == SessionActivity.Idle && !waits.ContainsKey(result[i].Address))
                result[i] = result[i] with { Activity = SessionActivity.Unknown };
        if (!forPeer)
        {
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
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or
                        HttpRequestException or BrokerException or OperationCanceledException)
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
        await Task.WhenAll(new[] { GuardAsync(DiscoverAsync, stop), GuardAsync(ReceiptLoopAsync, stop) }
            .Concat(Enumerable.Range(0, 4).Select(_ => GuardAsync(DeliveryLoopAsync, stop))));
    }

    private async Task GuardAsync(Func<CancellationToken, Task> run, CancellationToken stop)
    {
        try
        {
            await run(stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or InvalidDataException or BrokerException or UnauthorizedAccessException)
        {
            adapterErrors["broker"] = "durable_state_unavailable";
            lifetime?.Cancel(); FatalError?.Invoke();
        }
    }

    private async Task DiscoverAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            foreach (var adapter in adapters)
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    var sessions = await adapter.DiscoverAsync(store.BrokerId, deadline.Token);
                    if (sessions.Count > 256) throw new BrokerException("session_limit", "Adapter directory exceeded its bound.");
                    foreach (var session in sessions) Register(session);
                    var diagnostics = adapter.DiscoveryDiagnostics.Take(8).Select(item => item.ToStatusText()).Distinct().ToArray();
                    if (diagnostics.Length == 0) adapterErrors.TryRemove(adapter.Id, out _);
                    else adapterErrors[adapter.Id] = string.Join("; ", diagnostics);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or BrokerException or
                    UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
                {
                    if (stop.IsCancellationRequested) throw;
                    adapterErrors[adapter.Id] = e is AdapterDiscoveryException diagnostic ? diagnostic.Diagnostic.ToStatusText()
                        : e is BrokerException b ? b.Code : "adapter_unavailable";
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(10), stop);
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
    public (BrokerSession? Session, string Code) WhoAmI(System.Net.IPAddress client, int clientPort, int serverPort)
    {
        var chain = (callerProcesses ?? WindowsCallerProcessResolver.Instance).Resolve(client, clientPort, serverPort);
        if (chain.Count == 0) return (null, "caller_unresolved");
        var directory = store.Directory();
        foreach (var process in chain)
        {
            foreach (var adapter in adapters)
                if (adapter.FindSessionByProcess(process.ProcessId, process.StartFileTime) is { } address)
                    return directory.SingleOrDefault(session => session.Address == address) is { } row ? (row, "ok") : (null, "no_native_session");
            if (IsAgentImage(process.ImageName)) break;
        }
        return (null, "no_native_session");
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
                }, "peer_receipt");
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
                    store.RecordRecipientReceipt(message.Message.Id, message.Message.Recipient, peer.BrokerId,
                        remote.Status == MessageStatus.Expired ? MessageStatus.Refused : remote.Status);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or
                    HttpRequestException or BrokerException or OperationCanceledException)
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
