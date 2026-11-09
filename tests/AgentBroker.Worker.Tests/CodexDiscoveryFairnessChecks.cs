using AgentBroker.Core;
using AgentBroker.CodexDesktopAdapter;
using AgentBroker.Worker.Adapters;

internal static class CodexDiscoveryFairnessChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: CODEX_DISCOVERY_FAIRNESS_EXECUTED");
        var clock = new FairClock();
        var provider = new BudgetProvider(clock);
        await using var adapter = new CodexNativeAdapter(provider, enableDelivery: true, clock: clock);
        using var owned = OwnedScratch.Create("codex-discovery-fairness");
        using var store = new BrokerStore(Path.Combine(owned.Root, "store"), clock: clock);
        var warm = await adapter.DiscoverAsync(store.BrokerId, default);
        foreach (var row in warm) store.Register(row);
        check(warm.Count == 3 && warm.All(row => row.LeaseUntil == clock.GetUtcNow().AddSeconds(30)),
            "CODEX_FAIR_WARM: three valid exact owners start with ordinary thirty-second leases");
        provider.CutAfter = 1;
        var singleLookups = new List<Guid[]>();
        bool exact = true;
        for (int pass = 0; pass < 6; pass++)
        {
            using var deadline = new CancellationTokenSource();
            provider.Deadline = deadline; provider.Lookups.Clear();
            var rows = await adapter.DiscoverAsync(store.BrokerId, deadline.Token);
            singleLookups.Add(provider.Lookups.ToArray());
            exact &= rows.Count == 1 && rows.All(row => warm.Single(old => old.Address.SessionId == row.Address.SessionId).Address == row.Address &&
                row.LeaseUntil <= clock.GetUtcNow().AddSeconds(30));
            foreach (var row in rows) store.Register(row);
            clock.Advance(TimeSpan.FromSeconds(10));
        }
        Console.WriteLine("NOTE CODEX_FAIR_SINGLE_LOOKUPS=" + string.Join(";", singleLookups.Select(pass => string.Join(",", pass.Select(id => Array.IndexOf(provider.Threads, id))))));
        // One owner every12s cannot keep all three leases continuously live. Prove bounded revisits,
        // then start the capacity2 lease check with fresh validation, without extending any lease.
        provider.CutAfter = 0;
        foreach (var row in await adapter.DiscoverAsync(store.BrokerId, default)) store.Register(row);
        provider.CutAfter = 2;
        var lookups = new List<Guid[]>();
        for (int pass = 0; pass < 4; pass++)
        {
            using var deadline = new CancellationTokenSource();
            provider.Deadline = deadline;
            provider.Lookups.Clear();
            var rows = await adapter.DiscoverAsync(store.BrokerId, deadline.Token);
            lookups.Add(provider.Lookups.ToArray());
            exact &= rows.Count == 2 && rows.All(row => warm.Single(old => old.Address.SessionId == row.Address.SessionId).Address == row.Address &&
                row.LeaseUntil <= clock.GetUtcNow().AddSeconds(30));
            foreach (var row in rows) store.Register(row);
            clock.Advance(TimeSpan.FromSeconds(10));
        }
        Console.WriteLine("NOTE CODEX_FAIR_LOOKUPS=" + string.Join(";", lookups.Select(pass => string.Join(",", pass.Select(id => Array.IndexOf(provider.Threads, id))))));
        check(singleLookups.Take(3).SelectMany(pass => pass).Distinct().Count() == provider.Threads.Length &&
            singleLookups.SelectMany(pass => pass).GroupBy(id => id).All(group => group.Count() == 2) &&
            lookups.Skip(1).Any(pass => pass.Contains(provider.Threads[2])),
            "CODEX_FAIR_BUDGET_TAIL_REACHED: a successful prefix cannot consume every later pass while a valid unreached tail is never attempted");
        check(exact && warm.All(row => store.IsAddressable(row.Address)),
            "CODEX_FAIR_EXACT_RENEWAL: bounded passes renew all exact owners without extending leases or inheriting an earlier capability");
        provider.CutAfter = 0;
        provider.Missing = provider.Threads[2];
        var missing = await adapter.DiscoverAsync(store.BrokerId, default);
        check(missing.Count == 2 && missing.All(row => row.Address != warm[2].Address),
            "CODEX_FAIR_MISSING_OWNER: a previously verified but now missing owner receives no renewed row");
        provider.Missing = null; provider.Stale = provider.Threads[2]; provider.Lookups.Clear();
        var stale = await adapter.DiscoverAsync(store.BrokerId, default);
        check(stale.Count == 2 && !provider.Lookups.Contains(provider.Threads[2]) &&
            adapter.DiscoveryDiagnostics.Any(item => item.Code == "codex_directory_observation_stale"),
            "CODEX_FAIR_STALE_METADATA: prioritization cannot turn a stale candidate into an owner lookup or renewed lease");
        provider.Stale = null; provider.Invalid = provider.Threads[2];
        var invalid = await adapter.DiscoverAsync(store.BrokerId, default);
        check(invalid.Count == 2 && adapter.DiscoveryDiagnostics.Any(item => item.Code == "codex_owner_binding_invalid"),
            "CODEX_FAIR_INVALID_OWNER: a fresh candidate with an invalid owner binding receives no renewed row");
        provider.Invalid = null; provider.NewServer = provider.Threads[2];
        var successor = (await adapter.DiscoverAsync(store.BrokerId, default)).Single(row => row.Address.SessionId == warm[2].Address.SessionId);
        store.Register(successor);
        check(successor.Address != warm[2].Address && !store.IsAddressable(warm[2].Address) && store.IsAddressable(successor.Address),
            "CODEX_FAIR_EXACT_SUCCESSOR: a newly verified server gets its own exact incarnation and cannot renew its predecessor");
    }

    private sealed class FairClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan span) => now += span;
    }

    private sealed class BudgetProvider(FairClock clock) : ICodexNativeProvider
    {
        internal Guid[] Threads { get; } = Enumerable.Range(1, 3).Select(index => Guid.Parse("00000000-0000-0000-0000-" + index.ToString("D12"))).ToArray();
        private readonly Guid owner = Guid.NewGuid(), incarnation = Guid.NewGuid();
        internal readonly List<Guid> Lookups = [];
        internal int CutAfter;
        internal Guid? Missing, Stale, Invalid, NewServer;
        internal CancellationTokenSource? Deadline;
        public bool NativeIdleGuard => true;
        public Task<IReadOnlyList<DesktopDirectorySession>> ObserveAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<DesktopDirectorySession>>(
            Threads.Select(id => new DesktopDirectorySession(id, DesktopSessionActivity.Idle,
                id == Stale ? clock.GetUtcNow().AddMinutes(-2) : clock.GetUtcNow(), clock.GetUtcNow())).ToArray());
        public Task<DesktopOwnerBinding?> ResolveOwnerAsync(Guid conversationId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Lookups.Add(conversationId);
            if (conversationId == Missing) return Task.FromResult<DesktopOwnerBinding?>(null);
            if (CutAfter > 0)
            {
                clock.Advance(TimeSpan.FromSeconds(2));
                if (Lookups.Count == CutAfter) Deadline!.Cancel();
            }
            return Task.FromResult<DesktopOwnerBinding?>(new(conversationId, owner, incarnation,
                conversationId == Invalid ? 1u : conversationId == NewServer ? 54321u : 12345u, 134000000000000000, true)
                { RouterTrust = DesktopRouterTrust.ExactPinned });
        }
        public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
