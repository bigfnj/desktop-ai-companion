using AgentBroker.CodexDesktopAdapter;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Adapters;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

internal static class AdapterSafetyChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: ADAPTER_SAFETY_EXECUTED");
        await IndependentConnectionsAsync(check);
        await MalformedResultsAsync(check);
        await DirectoryBusyRuntimeAsync(check);
        await ClaudeObservationChecks.RunBusyCausesAsync(check);
        BusyClassification(check);
    }

    private static async Task IndependentConnectionsAsync(Action<bool, string> check)
    {
        Guid thread = Guid.NewGuid();
        var discoveryOwner = new OwnerConnection(thread);
        var deliveryOwner = new OwnerConnection(thread) { HoldSubmission = true };
        int observed = 0, prepared = 0, fresh = 0;
        var discovery = new CodexNativeProvider(true, _ => Task.FromResult<ICodexOwnerConnection>(discoveryOwner), () => { },
            _ => { observed++; return Task.FromResult<IReadOnlyList<DesktopDirectorySession>>([
                new(thread, DesktopSessionActivity.Idle, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]); });
        var delivery = new CodexNativeProvider(true, _ => Task.FromResult<ICodexOwnerConnection>(deliveryOwner), () => fresh++,
            _ => throw new InvalidOperationException("A delivery provider must never enumerate directory rows."),
            verifyProfilesForDiscovery: () => prepared++);
        var adapter = new CodexNativeAdapter(discovery, true, deliveryProvider: delivery);
        try
        {
            Guid broker = Guid.NewGuid();
            var row = (await adapter.DiscoverAsync(broker, default)).Single();
            BrokerMessage Message() => new(Guid.NewGuid(), Guid.NewGuid(), null,
                new(broker, "synthetic-sender", Guid.NewGuid()), row.Address, "Synthetic separate owner connection.",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));
            var submitted = adapter.SubmitAsync(Message(), default);
            try
            {
                try { await deliveryOwner.SubmissionEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException)
                {
                    check(false, "CODEX_DELIVERY_OWNS_CONNECTION: submission never entered the independently owned delivery connection");
                    throw;
                }
                using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                IReadOnlyList<BrokerSession> renewed;
                try { renewed = await adapter.DiscoverAsync(broker, deadline.Token); }
                catch (OperationCanceledException)
                {
                    check(false, "CODEX_DISCOVERY_DURING_SUBMISSION: independently verified discovery was blocked by delivery");
                    throw;
                }
                check(renewed.Single().Address == row.Address && observed == 2 && discoveryOwner.Submissions == 0,
                    "CODEX_DISCOVERY_DURING_SUBMISSION: a held delivery cannot prevent independently verified discovery renewal");
            }
            finally { deliveryOwner.ReleaseSubmission.TrySetResult(); }
            check((await submitted).Outcome == AdapterAdmission.Submitted && prepared == 1 && fresh == 1 &&
                deliveryOwner.LastSubmittedBinding?.Incarnation == deliveryOwner.ConnectionId &&
                deliveryOwner.ConnectionId != discoveryOwner.ConnectionId,
                "CODEX_DELIVERY_OWNS_CONNECTION: preparation and fresh verification use delivery's independently resolved connection binding");

            deliveryOwner.Started++;
            var changed = await adapter.SubmitAsync(Message(), default);
            check(changed is { Outcome: AdapterAdmission.DeferredNotSubmitted, Reason: "native_owner_changed" } && deliveryOwner.Submissions == 1,
                "CODEX_INDEPENDENT_EXACT_INCARNATION: a replacement router process cannot receive an earlier incarnation's message");
            deliveryOwner.Started--;
            deliveryOwner.Result = new(DesktopSubmissionOutcome.DeferredNotSubmitted, ErrorCode: "native_receipt_capacity_reconnect");
            check((await adapter.SubmitAsync(Message(), default)).Reason == "native_receipt_capacity_reconnect",
                "CODEX_RECONNECT_IS_NOT_BUSY: a typed receipt-capacity reconnect keeps its own non-submission reason");
            deliveryOwner.Result = new(DesktopSubmissionOutcome.DeferredNotSubmitted, ErrorCode: "future_deferred_cause");
            check((await adapter.SubmitAsync(Message(), default)).Reason == "native_not_submitted",
                "CODEX_UNKNOWN_DEFERRAL_IS_NOT_BUSY: an unknown non-submission cause never invents a busy observation");

            discoveryOwner.Missing = true;
            check((await adapter.DiscoverAsync(broker, default)).Count == 0 &&
                adapter.DiscoveryDiagnostics.Any(item => item.Code == "codex_owner_not_found"),
                "CODEX_MISSING_OWNER_VISIBLE: no-client-found reports its count and never renews a cached binding");
        }
        finally { await adapter.DisposeAsync(); }
        check(discoveryOwner.Disposals == 1 && deliveryOwner.Disposals == 1,
            "CODEX_INDEPENDENT_DISPOSAL: both owned connections close once");
    }

    private static async Task MalformedResultsAsync(Action<bool, string> check)
    {
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "adapter-safety-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new BrokerStore(scratch);
            await using var federation = new EmptyFederation();
            var adapter = new SafetyAdapter();
            await using var runtime = new BrokerRuntime(store, federation, [adapter])
            { DiscoveryInterval = TimeSpan.FromMilliseconds(20) };
            bool fatal = false;
            runtime.FatalError += () => fatal = true;
            runtime.Start(default);
            await UntilAsync(() => store.Directory().Any(row => row.Address == adapter.Address));
            var sender = runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic result sender", false);
            foreach (var malformed in new AdapterResult?[] {
                new(AdapterAdmission.Refused, "bad-reason"), new(AdapterAdmission.Refused, new string('a', 81)),
                new(AdapterAdmission.Refused, ""), new((AdapterAdmission)999, "unknown_outcome"), null,
                new(AdapterAdmission.DeferredNotSubmitted, "bad.reason") })
            {
                Guid id = Guid.NewGuid();
                adapter.Results[id] = malformed;
                store.Send(sender.Address, adapter.Address!, id, "Synthetic malformed result.", null, 0, store.BrokerId);
                await UntilAsync(() => store.FindReceipt(id)?.Reason is "adapter_reason_invalid" or "adapter_result_invalid",
                    "ADAPTER_INVALID_RESULT_CONTAINED: malformed result never settled under a safe reason");
                var receipt = store.FindReceipt(id)!;
                var expected = malformed?.Outcome == AdapterAdmission.DeferredNotSubmitted ? MessageStatus.Accepted :
                    malformed?.Outcome == AdapterAdmission.Refused ? MessageStatus.Refused : MessageStatus.Uncertain;
                check(receipt.Status == expected && !fatal,
                    "ADAPTER_INVALID_RESULT_CONTAINED: malformed result preserves proven admission outcome and does not stop the worker");
                if (receipt.Status == MessageStatus.Accepted)
                    store.RecordRecipientReceipt(id, adapter.Address!, store.BrokerId, MessageStatus.Delivered);
                Guid witness = Guid.NewGuid();
                store.Send(sender.Address, adapter.Address!, witness, "Synthetic healthy result after malformed input.", null, 0, store.BrokerId);
                await UntilAsync(() => store.FindReceipt(witness)?.Status == MessageStatus.Delivered,
                    "ADAPTER_INVALID_RESULT_NEXT_DELIVERS: a later healthy message was not delivered");
                check(!fatal, "ADAPTER_INVALID_RESULT_NEXT_DELIVERS: a later healthy message is executed after each malformed result");
            }
            check(runtime.AdapterErrors.ContainsKey("adapter_result"),
                "ADAPTER_INVALID_RESULT_DIAGNOSTIC: malformed results remain visible without recording their contents");
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    private static void BusyClassification(Action<bool, string> check)
    {
        var clock = new ToolFixture.ManualClock();
        var observations = new DeliveryObservations(clock);
        Guid broker = Guid.NewGuid();
        var recipient = new SessionAddress(broker, "claude:" + Guid.NewGuid(), Guid.NewGuid());
        var message = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null,
            new(broker, "synthetic-sender", Guid.NewGuid()), recipient, "Synthetic activity.", clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(1));
        MessageReceipt Receipt(string reason, MessageStatus status = MessageStatus.Accepted) => new(message, status, clock.GetUtcNow(), reason);
        observations.Record(message, new(AdapterAdmission.DeferredNotSubmitted, "native_recipient_busy"));
        check(observations.BusySince(Receipt("native_recipient_busy")) == clock.GetUtcNow() && observations.SeenBusy(recipient),
            "BUSY_CLAUDE_TYPED_CAUSE: a verified Claude busy answer contributes the same observation as Codex busy");
        foreach (string reason in new[] { "native_recipient_awaiting_approval", "native_recipient_activity_unknown", "native_receipt_capacity_reconnect" })
        {
            observations.Record(message, new(AdapterAdmission.DeferredNotSubmitted, reason));
            check(observations.BusySince(Receipt(reason)) is null && !observations.SeenBusy(recipient),
                "BUSY_CAUSES_STAY_DISTINCT: approval, unknown activity and reconnect are never reported busy");
        }
        var row = new BrokerSession(recipient, "Synthetic native", "claude-native", SessionCapabilities.Receive,
            SessionActivity.Busy, clock.GetUtcNow().AddMinutes(1));
        observations.ObserveDirectory(row);
        var first = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromSeconds(1));
        observations.ObserveDirectory(row);
        check(observations.BusySince(Receipt("recipient_busy")) == first &&
            observations.BusySince(Receipt("native_admission_unconfirmed", MessageStatus.Submitted)) == first,
            "BUSY_DIRECTORY_BEFORE_ATTEMPT: current native Busy observations retain their first time for queued and submitted receipts");
        observations.ObserveDirectory(row with { Activity = SessionActivity.ApprovalRequired });
        check(observations.BusySince(Receipt("recipient_busy")) is null && !observations.SeenBusy(recipient),
            "BUSY_DIRECTORY_APPROVAL_CLEARS: a current approval observation ends the busy run");
        observations.ObserveDirectory(row);
        clock.Advance(DeliveryObservations.BusyFreshFor);
        check(observations.BusySince(Receipt("recipient_busy")) is null,
            "BUSY_DIRECTORY_EXPIRES: discovery evidence expires without a fresh observation");
    }

    private static async Task DirectoryBusyRuntimeAsync(Action<bool, string> check)
    {
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "directory-busy-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new BrokerStore(scratch);
            await using var federation = new EmptyFederation();
            var adapter = new BusyNativeAdapter();
            await using var runtime = new BrokerRuntime(store, federation, [adapter])
            { DiscoveryInterval = TimeSpan.FromMilliseconds(20) };
            runtime.Start(default);
            await UntilAsync(() => store.Directory().Any(row => row.Address == adapter.Address));
            var sender = runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic busy observer", false);
            var sent = store.Send(sender.Address, adapter.Address!, Guid.NewGuid(), "Synthetic queued busy observation.", null, 0, store.BrokerId);
            await UntilAsync(() => runtime.View(store.FindReceipt(sent.Message.Id)!).BusySince is not null,
                "BUSY_DIRECTORY_RUNTIME_WIRING: a discovered Busy native row never supplied the queued receipt's observation");
            check(runtime.View(store.FindReceipt(sent.Message.Id)!) is { Reason: "recipient_busy", BusySince: not null } &&
                runtime.View(store.FindReceipt(sent.Message.Id)!, forPeer: true).BusySince is null && adapter.Submissions == 0,
                "BUSY_DIRECTORY_RUNTIME_WIRING: runtime observes busy discovery without claiming input or disclosing local timing to peers");
            adapter.Activity = SessionActivity.ApprovalRequired;
            await UntilAsync(() => store.FindReceipt(sent.Message.Id)?.Reason == "recipient_awaiting_approval" &&
                runtime.View(store.FindReceipt(sent.Message.Id)!).BusySince is null,
                "BUSY_DIRECTORY_RUNTIME_APPROVAL: fresh approval activity did not end the busy observation");
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    private static async Task UntilAsync(Func<bool> condition, string failure = "ADAPTER_SAFETY_SETUP: synthetic session was not discovered")
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try { while (!condition()) await Task.Delay(10, deadline.Token); }
        catch (OperationCanceledException) { throw new InvalidOperationException(failure); }
    }

    private sealed class OwnerConnection(Guid thread) : ICodexOwnerConnection
    {
        internal readonly Guid ConnectionId = Guid.NewGuid();
        internal ulong Started = 134000000000000000;
        internal bool HoldSubmission, Missing;
        internal int Submissions, Disposals;
        internal DesktopOwnerBinding? LastSubmittedBinding;
        internal DesktopSubmissionResult Result = new(DesktopSubmissionOutcome.Accepted);
        internal TaskCompletionSource SubmissionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSubmission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool NativeIdleGuard => true;
        public Task<DesktopOwnerBinding?> DiscoverAsync(Guid conversationId, CancellationToken token) =>
            Task.FromResult<DesktopOwnerBinding?>(Missing ? null : new(conversationId, thread, ConnectionId, 12345, Started, true)
            { RouterTrust = DesktopRouterTrust.ExactPinned });
        public async Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token)
        {
            if (binding.Incarnation != ConnectionId) throw new InvalidOperationException("Foreign connection binding.");
            LastSubmittedBinding = binding;
            Submissions++;
            SubmissionEntered.TrySetResult();
            if (HoldSubmission) await ReleaseSubmission.Task.WaitAsync(token);
            return Result;
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class SafetyAdapter : ILocalAgentAdapter
    {
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, AdapterResult?> Results = new();
        internal SessionAddress? Address;
        public string Id => "synthetic-safety";
        public Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token)
        {
            Address ??= new(brokerId, "synthetic:safety", Guid.NewGuid());
            return Task.FromResult<IReadOnlyList<BrokerSession>>([new(Address, "Synthetic safety", Id,
                SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply,
                SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(1))]);
        }
        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token) =>
            Task.FromResult(Results.TryGetValue(message.Id, out var result) ? result! : new(AdapterAdmission.Delivered));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BusyNativeAdapter : ILocalAgentAdapter
    {
        internal SessionAddress? Address;
        internal volatile SessionActivity Activity = SessionActivity.Busy;
        internal int Submissions;
        public string Id => "codex-native";
        public Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token)
        {
            Address ??= new(brokerId, "codex:" + Guid.NewGuid(), Guid.NewGuid());
            return Task.FromResult<IReadOnlyList<BrokerSession>>([new(Address, "Synthetic busy native", Id,
                SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.NativeIdleGuard,
                Activity, DateTimeOffset.UtcNow.AddMinutes(1))]);
        }
        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token)
        { Submissions++; throw new InvalidOperationException("A known-busy native recipient must not be claimed."); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyFederation : IPeerFederation
    {
        public IReadOnlyList<BrokerAdvertisement> Discovered => [];
        public IReadOnlyList<PairingOffer> PairingOffers => [];
        public BrokerAdvertisement LocalAdvertisement => throw new NotSupportedException();
        public Guid? AuthenticatePeer(HttpContext context) => null;
        public void MapEndpoints(WebApplication application) { }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task RequestPairAsync(BrokerAdvertisement peer, CancellationToken token) => throw new NotSupportedException();
        public Task ApprovePairAsync(Guid requestId, CancellationToken token) => throw new NotSupportedException();
        public void RejectPair(Guid requestId) { }
        public void RevokePeer(Guid brokerId) { }
        public Task<PeerDirectory> GetDirectoryAsync(PeerTrust peer, CancellationToken token) => throw new NotSupportedException();
        public Task<MessageReceipt> SubmitAsync(PeerTrust peer, BrokerMessage message, CancellationToken token) => throw new NotSupportedException();
        public Task<MessageReceipt?> GetReceiptAsync(PeerTrust peer, Guid messageId, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
