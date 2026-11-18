using AgentBroker.CodexDesktopAdapter;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Adapters;

// N-broker-native-confirmation-01 (AgentBroker 1.10.6, lane N round 1; design v3 + v4 + v5). The Codex library answers a
// cancellation or I/O failure before its turn frame with a typed pre-frame deferral (nothing written). The adapter defers such
// a message under its own reason PreFrameDeferralBound (3) times in a row and settles the fourth consecutive one Uncertain with
// the stage named; the count belongs to one message lifetime to one exact recipient, restarts on another provider answer
// (accepted, rejected, uncertain, the busy reply, owner_unavailable) while adapter-level and pre-library provider deferrals
// neither count nor restart it, ages out, is capped, and lives in one adapter instance. The provider retires the owner
// connection only when the request closed it.
// This file stops at its first failure, so each mutant reports at its own label.
internal static class CodexPreFrameChecks
{
    private static readonly AdapterResult Deferral = new(AdapterAdmission.DeferredNotSubmitted, "native_owner_rediscovery_unavailable");

    internal static async Task RunAsync(Action<bool, string> check)
    {
        await AdapterBoundAsync(check);
        await StageMapAsync(check);
        await ResetAsync(check);
        await IdentityAsync(check);
        await PrunedAsync(check);
        await BoundedStorageAsync(check);
        await RestartAsync(check);
        await ProviderRetireAsync(check);
    }

    private static DesktopSubmissionResult PreFrame(string code)
        => new(DesktopSubmissionOutcome.DeferredNotSubmitted, ErrorCode: code, ConnectionClosed: code != "attempt_cancelled");

    private static async Task<AdapterResult[]> Times(CodexNativeAdapter adapter, BrokerMessage message, int count)
    {
        var results = new AdapterResult[count];
        for (int i = 0; i < count; i++) results[i] = await adapter.SubmitAsync(message, default);
        return results;
    }

    private static async Task AdapterBoundAsync(Action<bool, string> check)
    {
        var (provider, adapter, row) = await Start(TimeProvider.System);
        await using var owned = adapter;
        var message = Message(row);
        provider.Result = PreFrame("rediscovery_timeout");
        var deferred = await Times(adapter, message, CodexNativeAdapter.PreFrameDeferralBound);
        check(deferred.Length == 3 && deferred.All(result => result == Deferral) && provider.Submits == 3 && provider.LastMessageId == message.Id,
            "CODEX_ADAPTER_REDISCOVERY_DEFERS: pre-frame rediscovery results one to three defer under their own reason");
        var fourth = await adapter.SubmitAsync(message, default);
        check(fourth == new AdapterResult(AdapterAdmission.Uncertain, "native_uncertain_rediscovery_timeout"),
            "CODEX_REDISCOVERY_BOUND: the fourth consecutive pre-frame result of one message settles Uncertain with the stage named");
    }

    private static async Task StageMapAsync(Action<bool, string> check)
    {
        var (provider, adapter, row) = await Start(TimeProvider.System);
        await using var owned = adapter;
        var attempt = Message(row);
        provider.Result = PreFrame("attempt_cancelled");
        var attemptResults = await Times(adapter, attempt, 4);
        var io = Message(row);
        provider.Result = PreFrame("rediscovery_io");
        var ioResults = await Times(adapter, io, 4);
        check(attemptResults[..3].All(result => result == Deferral) &&
            attemptResults[3] == new AdapterResult(AdapterAdmission.Uncertain, "native_uncertain_raised_cancelled") &&
            ioResults[..3].All(result => result == Deferral) &&
            ioResults[3] == new AdapterResult(AdapterAdmission.Uncertain, "native_uncertain_raised_io"),
            "CODEX_REDISCOVERY_STAGE_MAP: at the fourth result a spent attempt token reads native_uncertain_raised_cancelled and a rediscovery I/O failure native_uncertain_raised_io");
    }

    private static async Task ResetAsync(Action<bool, string> check)
    {
        var (provider, adapter, row) = await Start(TimeProvider.System);
        await using var owned = adapter;
        // Another provider result (here an accepted turn) restarts the count: three more deferrals before the fourth settles.
        var reset = Message(row);
        provider.Result = PreFrame("rediscovery_timeout");
        await Times(adapter, reset, 2);
        provider.Result = new(DesktopSubmissionOutcome.Accepted, TurnId: "synthetic-turn");
        bool submitted = (await adapter.SubmitAsync(reset, default)).Outcome == AdapterAdmission.Submitted;
        provider.Result = PreFrame("rediscovery_timeout");
        var afterReset = await Times(adapter, reset, 4);
        bool resetHolds = submitted && afterReset[..3].All(result => result == Deferral) && afterReset[3].Outcome == AdapterAdmission.Uncertain;
        // An adapter-level deferral before the provider (the first owner lookup failing) neither counts nor resets.
        var interleaved = Message(row);
        provider.Result = PreFrame("rediscovery_timeout");
        await Times(adapter, interleaved, 2);
        int submitsBefore = provider.Submits;
        provider.FailResolve = true;
        var ownerDeferral = await adapter.SubmitAsync(interleaved, default);
        provider.FailResolve = false;
        var third = await adapter.SubmitAsync(interleaved, default);
        var fourth = await adapter.SubmitAsync(interleaved, default);
        bool interleaveHolds = ownerDeferral == new AdapterResult(AdapterAdmission.DeferredNotSubmitted, "native_owner_unavailable") &&
            provider.Submits == submitsBefore + 2 && third == Deferral && fourth.Outcome == AdapterAdmission.Uncertain;
        // A provider deferral before the library is called (native_not_submitted: the token ran out in the profile hash) neither
        // counts nor resets either, so a budget spent sometimes there and sometimes in the rediscovery still reaches the bound.
        var preLibrary = Message(row);
        provider.Result = PreFrame("rediscovery_timeout");
        await Times(adapter, preLibrary, 2);
        provider.Result = new(DesktopSubmissionOutcome.DeferredNotSubmitted, ErrorCode: "native_not_submitted");
        var notSubmitted = await adapter.SubmitAsync(preLibrary, default);
        provider.Result = PreFrame("rediscovery_timeout");
        var thirdPreFrame = await adapter.SubmitAsync(preLibrary, default);
        var fourthPreFrame = await adapter.SubmitAsync(preLibrary, default);
        bool preLibraryHolds = notSubmitted == new AdapterResult(AdapterAdmission.DeferredNotSubmitted, "native_not_submitted") &&
            thirdPreFrame == Deferral && fourthPreFrame.Outcome == AdapterAdmission.Uncertain;
        check(resetHolds && interleaveHolds && preLibraryHolds,
            "CODEX_REDISCOVERY_RESET: another provider answer restarts the count, while adapter-level and pre-library provider deferrals neither count nor reset");
    }

    private static async Task IdentityAsync(Action<bool, string> check)
    {
        var (provider, adapter, row) = await Start(TimeProvider.System);
        await using var owned = adapter;
        var first = Message(row);
        provider.Result = PreFrame("rediscovery_timeout");
        await Times(adapter, first, 3);
        // The same UUID admitted again after its record was pruned is a new lifetime: its count starts at zero.
        var reused = first with { CreatedAt = first.CreatedAt.AddSeconds(1), ExpiresAt = first.ExpiresAt.AddSeconds(1) };
        var reusedFirst = await adapter.SubmitAsync(reused, default);
        var original = await adapter.SubmitAsync(first, default);
        // The same message to another recipient incarnation (the owner's server process restarted, so its address changed)
        // is a new count too, while the original incarnation's count stands at three.
        var second = Message(row);
        await Times(adapter, second, 3);
        provider.NewServerProcess();
        var rowB = (await adapter.DiscoverAsync(row.Address.BrokerId, default)).Single();
        var otherIncarnation = await adapter.SubmitAsync(second with { Recipient = rowB.Address }, default);
        check(reusedFirst == Deferral && original.Outcome == AdapterAdmission.Uncertain &&
            rowB.Address.Incarnation != row.Address.Incarnation && otherIncarnation == Deferral,
            "CODEX_REDISCOVERY_IDENTITY: the same UUID in a new lifetime, or to another recipient incarnation, starts at zero and leaves the original count alone");
    }

    private static async Task PrunedAsync(Action<bool, string> check)
    {
        var clock = new StepClock();
        var (provider, adapter, row) = await Start(clock);
        await using var owned = adapter;
        var message = Message(row);
        provider.Result = PreFrame("rediscovery_timeout");
        await Times(adapter, message, 3);
        clock.Advance(CodexNativeAdapter.PreFrameDeferralRetention + TimeSpan.FromSeconds(1));
        var afterQuiet = await adapter.SubmitAsync(message, default);
        check(afterQuiet == Deferral,
            "CODEX_REDISCOVERY_PRUNED: a count not updated for ten monotonic minutes is pruned and the next pre-frame result starts at zero");
    }

    private static async Task BoundedStorageAsync(Action<bool, string> check)
    {
        var (provider, adapter, row) = await Start(TimeProvider.System);
        await using var owned = adapter;
        provider.Result = PreFrame("rediscovery_timeout");
        for (int i = 0; i <= CodexNativeAdapter.PreFrameDeferralCapacity; i++) await adapter.SubmitAsync(Message(row), default);
        check(adapter.PreFrameDeferralEntries == CodexNativeAdapter.PreFrameDeferralCapacity,
            "CODEX_REDISCOVERY_BOUNDED_STORAGE: one more distinct message than the cap leaves exactly the cap of pre-frame counts");
    }

    private static async Task RestartAsync(Action<bool, string> check)
    {
        var provider = new ScriptedProvider(TimeProvider.System);
        BrokerMessage message;
        await using (var before = new CodexNativeAdapter(provider, enableDelivery: true))
        {
            var row = (await before.DiscoverAsync(Guid.NewGuid(), default)).Single();
            message = Message(row);
            provider.Result = PreFrame("rediscovery_timeout");
            await Times(before, message, 3);
        }
        await using var after = new CodexNativeAdapter(provider, enableDelivery: true);
        await after.DiscoverAsync(message.Recipient.BrokerId, default);
        var first = await after.SubmitAsync(message, default);
        check(first == Deferral, "CODEX_REDISCOVERY_RESTART: a new adapter instance (a worker restart) starts every message at zero");
    }

    private static async Task ProviderRetireAsync(Action<bool, string> check)
    {
        var owner = new ScriptedOwner();
        var provider = new CodexNativeProvider(true, _ => Task.FromResult<ICodexOwnerConnection>(owner), () => { },
            _ => Task.FromResult<IReadOnlyList<DesktopDirectorySession>>(
                [new(owner.Binding.ConversationId, DesktopSessionActivity.Unknown, DateTimeOffset.UtcNow, null)]));
        await using var adapter = new CodexNativeAdapter(provider, enableDelivery: true);
        var session = (await adapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
        owner.Result = new(DesktopSubmissionOutcome.DeferredNotSubmitted, ErrorCode: "attempt_cancelled", ConnectionClosed: false);
        var open = await adapter.SubmitAsync(Message(session), default);
        int disposalsWhileOpen = owner.Disposals;
        owner.Result = new(DesktopSubmissionOutcome.DeferredNotSubmitted, ErrorCode: "rediscovery_timeout", ConnectionClosed: true);
        var closed = await adapter.SubmitAsync(Message(session), default);
        check(open.Reason == "native_owner_rediscovery_unavailable" && closed.Reason == "native_owner_rediscovery_unavailable" &&
            owner.Submissions == 2 && disposalsWhileOpen == 0 && owner.Disposals == 1,
            "CODEX_REDISCOVERY_RETIRE_ONLY_CLOSED: a pre-frame deferral keeps an open owner connection and retires a closed one");
    }

    private static async Task<(ScriptedProvider Provider, CodexNativeAdapter Adapter, BrokerSession Row)> Start(TimeProvider clock)
    {
        var provider = new ScriptedProvider(clock);
        var adapter = new CodexNativeAdapter(provider, enableDelivery: true, clock: clock);
        var row = (await adapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
        return (provider, adapter, row);
    }

    private static BrokerMessage Message(BrokerSession recipient)
        => new(Guid.NewGuid(), Guid.NewGuid(), null, new(recipient.Address.BrokerId, "synthetic-preframe-sender", Guid.NewGuid()),
            recipient.Address, "Synthetic pre-frame input, never sent to a real client.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));

    // Wall time fixed, monotonic time moved only by Advance.
    private sealed class StepClock : TimeProvider
    {
        private readonly DateTimeOffset utc = DateTimeOffset.UtcNow;
        private long timestamp = 1;
        public override DateTimeOffset GetUtcNow() => utc;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        internal void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }

    private sealed class ScriptedProvider(TimeProvider clock) : ICodexNativeProvider
    {
        private readonly Guid thread = Guid.NewGuid();
        private DesktopOwnerBinding binding = new(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), 12345, 134000000000000000, true)
        { RouterTrust = DesktopRouterTrust.ExactPinned };
        // The owner's server process restarted: a new creation time gives the conversation a new native incarnation.
        internal void NewServerProcess() => binding = binding with { ServerCreationFileTime = binding.ServerCreationFileTime + 1 };
        internal DesktopSubmissionResult Result = new(DesktopSubmissionOutcome.Accepted, TurnId: "synthetic-turn");
        internal bool FailResolve;
        internal int Submits;
        internal Guid LastMessageId;
        private DesktopOwnerBinding Binding => binding with { ConversationId = thread };
        public bool NativeIdleGuard => true;
        public bool IsDeliveryAuthorized(DesktopOwnerBinding current) => current.ConversationId == thread && current.OwnerClientId == binding.OwnerClientId;
        public Task<IReadOnlyList<DesktopDirectorySession>> ObserveAsync(CancellationToken token)
            => Task.FromResult<IReadOnlyList<DesktopDirectorySession>>([new(thread, DesktopSessionActivity.Unknown, clock.GetUtcNow(), null)]);
        public Task<DesktopOwnerBinding?> ResolveOwnerAsync(Guid id, CancellationToken token)
            => FailResolve ? throw new DesktopAdapterException("owner_discovery_rejected") : Task.FromResult<DesktopOwnerBinding?>(Binding);
        public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding current, Guid id, string text, CancellationToken token)
        { Submits++; LastMessageId = id; return Task.FromResult(Result); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedOwner : ICodexOwnerConnection
    {
        internal DesktopOwnerBinding Binding { get; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 12345, 134000000000000000, true) { RouterTrust = DesktopRouterTrust.ExactPinned };
        internal DesktopSubmissionResult Result { get; set; } = new(DesktopSubmissionOutcome.Accepted);
        internal int Submissions, Disposals;
        public bool NativeIdleGuard => true;
        public Task<DesktopOwnerBinding?> DiscoverAsync(Guid conversationId, CancellationToken token)
            => Task.FromResult<DesktopOwnerBinding?>(conversationId == Binding.ConversationId ? Binding : null);
        public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token)
        { Submissions++; return Task.FromResult(Result); }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
