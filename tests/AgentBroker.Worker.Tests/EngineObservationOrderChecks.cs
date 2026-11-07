using AgentBroker.CodexDesktopAdapter;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Adapters;

internal static class EngineObservationOrderChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: ENGINE_OBSERVATION_ORDER_EXECUTED");
        var clock = new ToolFixture.ManualClock();
        var owner = new Owner(clock);
        var source = new OneAtATime(clock);
        await using var adapter = new CodexNativeAdapter(owner, true, clock, engineBindings: source);
        Guid broker = Guid.NewGuid();
        for (int pass = 0; pass < 4; pass++)
        {
            await adapter.DiscoverAsync(broker, default);
            await adapter.EngineObservation;
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        Guid a = owner.Threads[0], b = owner.Threads[1], c = owner.Threads[2];
        check(source.Asked.Count == 4 && source.Asked[0].SequenceEqual(new[] { a, b, c }) &&
            source.Asked[1].SequenceEqual(new[] { b, c, a }) && source.Asked[2].SequenceEqual(new[] { c, a, b }) &&
            source.Asked[3].SequenceEqual(new[] { a, b, c }),
            "ENGINE_BACKGROUND_OLDEST_FIRST: actual background observations reach never-seen rows first, then the oldest completed row");
    }

    private sealed class OneAtATime(TimeProvider clock) : ICodexEngineBindingSource
    {
        internal List<Guid[]> Asked { get; } = [];
        public IReadOnlyDictionary<Guid, CodexEngineBindingVerdict> Observe(IReadOnlyCollection<Guid> threads, CancellationToken token)
        {
            Asked.Add(threads.ToArray());
            return threads.Take(1).ToDictionary(thread => thread,
                _ => new CodexEngineBindingVerdict(true, CodexEngineBinding.CodeEngine, clock.GetUtcNow()));
        }
    }

    private sealed class Owner(TimeProvider clock) : ICodexNativeProvider
    {
        internal Guid[] Threads { get; } = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        private readonly Guid owner = Guid.NewGuid(), connection = Guid.NewGuid();
        public bool NativeIdleGuard => true;
        public Task<IReadOnlyList<DesktopDirectorySession>> ObserveAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<DesktopDirectorySession>>(Threads.Select(thread =>
                new DesktopDirectorySession(thread, DesktopSessionActivity.Idle, clock.GetUtcNow(), clock.GetUtcNow())).ToArray());
        public Task<DesktopOwnerBinding?> ResolveOwnerAsync(Guid thread, CancellationToken token) =>
            Task.FromResult<DesktopOwnerBinding?>(new(thread, owner, connection, 12345, 134000000000000000, true)
            { RouterTrust = DesktopRouterTrust.ExactPinned });
        public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token) =>
            throw new InvalidOperationException("Observation ordering must not submit a turn.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
