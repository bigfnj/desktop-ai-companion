using System.Text.Json;
using AgentBroker.CodexDesktopAdapter;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Adapters;
using ModelContextProtocol;

// Lane broker-163, N-agentbroker-code-owner-provenance-01, REPORT ONLY (owner's decision 2026-10-30): the Codex adapter
// observes who holds each thread's writer lock at discovery and just before a delivery's input is written, and says so on
// the list_sessions row and the sender's receipt. Nothing is refused, deferred or re-addressed on it. Synthetic threads,
// owners and verdicts only; each label names the rule a mutation of CodexNativeAdapter, BrokerRuntime, BrokerStore or
// DeliveryObservations must break.
internal static class EngineBindingReportChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await AdapterAsync(check);
        await RuntimeAsync(check);
        SerializationChecks(check);
    }

    private static async Task AdapterAsync(Action<bool, string> check)
    {
        var clock = new ToolFixture.ManualClock();
        var provider = new Owner(now: clock.GetUtcNow);
        var source = new Source();
        await using var reporting = new CodexNativeAdapter(provider, enableDelivery: true, clock, engineBindings: source);
        await using var silent = new CodexNativeAdapter(new Owner(provider.Thread, clock.GetUtcNow), enableDelivery: true, clock);
        Guid broker = Guid.NewGuid();
        source.Verdict = new(true, CodexEngineBinding.CodeEngine);
        var row = (await reporting.DiscoverAsync(broker, default)).Single();
        var plain = (await silent.DiscoverAsync(broker, default)).Single();
        check(reporting.EngineBindingOf(row.Address) == new EngineBinding(true, "code_engine", clock.GetUtcNow()) && source.Asked.Single().SequenceEqual([provider.Thread]),
            "ENGINE_ROW_BOUND: a discovered Codex row has the observed binding of its own thread, stamped when observed");
        check(silent.EngineBindingOf(plain.Address) is null && row == plain,
            "ENGINE_ROW_REPORT_ONLY: the observation changes nothing on the row: address, capabilities, activity and lease are those without it");

        source.Verdict = new(false, "foreign_holder");
        clock.Advance(TimeSpan.FromSeconds(1));
        var message = Message(row.Address);
        var unbound = await reporting.SubmitAsync(message, default);
        check(unbound.Outcome == AdapterAdmission.Submitted && unbound.Reason == "native_admission_unconfirmed" &&
            unbound.EngineBinding == new EngineBinding(false, "foreign_holder", clock.GetUtcNow()) && provider.Submissions == 1,
            "ENGINE_SUBMIT_UNBOUND_NOT_REFUSED: an unbound observation at the attempt is reported on the result, and the input is still written");
        source.Verdict = new(true, CodexEngineBinding.DesktopEngine);
        provider.DeferNext = true;
        var deferred = await reporting.SubmitAsync(Message(row.Address), default);
        check(deferred.Outcome == AdapterAdmission.DeferredNotSubmitted && deferred.Reason == "native_context_busy" &&
            deferred.EngineBinding?.Reason == "desktop_engine",
            "ENGINE_SUBMIT_EVERY_OUTCOME: a deferred attempt reports what was observed too");

        source.Throws = true;
        var failed = (await reporting.DiscoverAsync(broker, default)).Single();
        check(reporting.EngineBindingOf(failed.Address) is { Bound: false, Reason: "query_failed" } && failed.Capabilities == row.Capabilities,
            "ENGINE_SOURCE_THROWS: a source that throws reads query_failed on every row and fails neither the pass nor the row");
        source.Throws = false;
    }

    private static async Task RuntimeAsync(Action<bool, string> check)
    {
        var provider = new Owner();
        var source = new Source { Verdict = new(true, CodexEngineBinding.CodeEngine) };
        // The real clock for store and adapter alike: the adapter stamps leases and observations with its own clock.
        await using var fixture = new ToolFixture("engine-binding", clock: TimeProvider.System,
            adapters: [new CodexNativeAdapter(provider, enableDelivery: true, null, engineBindings: source)]);
        var sender = fixture.Register("Synthetic engine sender");
        using var stop = new CancellationTokenSource();
        fixture.Runtime.Start(stop.Token);
        ListedSession? row = null;
        for (int i = 0; i < 100 && row is null; i++)
        {
            row = (await fixture.Tools.ListSessions(default)).SingleOrDefault(session => session.Adapter == "codex-native");
            if (row is null) await Task.Delay(50);
        }
        check(row?.EngineBinding is { Bound: true, Reason: "code_engine" },
            "ENGINE_LISTED: list_sessions shows the Codex row's engine binding");
        // A paired broker's caller: the same tool, authenticated as another broker. Its directory has no engine member at
        // all (BrokerSession keeps the 1.0.4 members, TombstoneChecks.WireAsync), and its list_sessions gets none either.
        fixture.Context.Items["agentbroker.identity"] = Guid.NewGuid();
        var asPeer = await fixture.Tools.ListSessions(default);
        fixture.Context.Items["agentbroker.identity"] = fixture.Store.BrokerId;
        check(asPeer.Any(session => session.Address == row!.Address) && asPeer.All(session => session.EngineBinding is null),
            "ENGINE_NOT_FEDERATED: a paired broker's caller lists the row but never its engine binding");
        source.Verdict = new(false, "multiple_holders");
        Guid id = Guid.NewGuid();
        fixture.Tools.Send(sender.Address, row!.Address, id, "Synthetic message to a Codex row with an engine report.");
        for (int i = 0; i < 100 && fixture.Store.FindReceipt(id)?.Status is MessageStatus.Accepted or MessageStatus.Submitting; i++) await Task.Delay(50);
        var receipt = fixture.Tools.GetMessageReceipt(id);
        check(receipt is { Status: MessageStatus.Submitted } && receipt.EngineBinding is { Bound: false, Reason: "multiple_holders" },
            "ENGINE_RECEIPT_AT_ATTEMPT: the sender's receipt says what was observed at the attempt, and the unbound message was still submitted");
        check(fixture.Store.FindReceipt(id)?.Reason == "native_admission_unconfirmed",
            "ENGINE_RECEIPT_STORED_UNCHANGED: the stored receipt is the one it would have been without the report");
        stop.Cancel();
    }

    private static void SerializationChecks(Action<bool, string> check)
    {
        var session = new BrokerSession(new(Guid.NewGuid(), "codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid()), "Synthetic row", "codex-native",
            SessionCapabilities.None, SessionActivity.Unknown, DateTimeOffset.UtcNow.AddMinutes(1));
        var bare = ListedSession.From(session, null);
        // Web defaults write nulls: only the member's own JsonIgnore keeps a row without an observation byte-identical to the
        // BrokerSession it lists, whatever options a client or a later caller serializes with.
        var federation = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string json = JsonSerializer.Serialize(bare, McpJsonUtilities.DefaultOptions);
        string wire = JsonSerializer.Serialize(bare, federation);
        string before = JsonSerializer.Serialize(session, federation);
        string reported = JsonSerializer.Serialize(bare with { EngineBinding = new(false, "lock_missing", DateTimeOffset.UtcNow) }, McpJsonUtilities.DefaultOptions);
        check(!json.Contains("engineBinding", StringComparison.OrdinalIgnoreCase) && wire == before &&
            reported.Contains("\"engineBinding\"", StringComparison.Ordinal) && reported.Contains("lock_missing", StringComparison.Ordinal),
            "ENGINE_JSON_ABSENT: a row without an observation serializes exactly as before the field existed, nulls-writing options included");
    }

    private static BrokerMessage Message(SessionAddress recipient)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), Guid.NewGuid(), null, new(recipient.BrokerId, "mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid()),
            recipient, "Synthetic engine report message.", now, now.AddMinutes(5));
    }

    private sealed class Source : ICodexEngineBindingSource
    {
        internal CodexEngineBindingVerdict Verdict = new(false, "lock_missing");
        internal bool Throws;
        internal List<Guid[]> Asked { get; } = [];
        public IReadOnlyDictionary<Guid, CodexEngineBindingVerdict> Observe(IReadOnlyCollection<Guid> threadIds)
        {
            Asked.Add(threadIds.ToArray());
            if (Throws) throw new InvalidOperationException("Synthetic observation failure.");
            return threadIds.ToDictionary(thread => thread, _ => Verdict);
        }
    }

    private sealed class Owner(Guid? thread = null, Func<DateTimeOffset>? now = null) : ICodexNativeProvider
    {
        private readonly Func<DateTimeOffset> now = now ?? (() => DateTimeOffset.UtcNow);
        internal Guid Thread { get; } = thread ?? Guid.NewGuid();
        private readonly Guid owner = Guid.NewGuid(), connection = Guid.NewGuid();
        private int submissions;
        internal int Submissions => Volatile.Read(ref submissions);
        internal volatile bool DeferNext;
        public bool NativeIdleGuard => true;
        private DesktopOwnerBinding Binding => new(Thread, owner, connection, 12345, 134000000000000000, true) { RouterTrust = DesktopRouterTrust.ExactPinned };
        public bool IsDeliveryAuthorized(DesktopOwnerBinding binding) => binding == Binding;
        public Task<IReadOnlyList<DesktopDirectorySession>> ObserveAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<DesktopDirectorySession>>([new(Thread, DesktopSessionActivity.Idle, now(), now())]);
        public Task<DesktopOwnerBinding?> ResolveOwnerAsync(Guid conversationId, CancellationToken token) =>
            Task.FromResult<DesktopOwnerBinding?>(conversationId == Thread ? Binding : null);
        public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token)
        {
            if (DeferNext) { DeferNext = false; return Task.FromResult(new DesktopSubmissionResult(DesktopSubmissionOutcome.DeferredNotSubmitted)); }
            Interlocked.Increment(ref submissions);
            return Task.FromResult(new DesktopSubmissionResult(DesktopSubmissionOutcome.Accepted));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
