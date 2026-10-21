using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Security;

internal static class StartupCleanupChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string scratch, HashSet<string> keys)
    {
        string directory = Path.Combine(scratch, "startup-failure");
        Guid brokerId;
        using (var seed = new BrokerStore(Path.Combine(directory, "store"))) brokerId = seed.BrokerId;
        keys.Add(BrokerIdentity.KeyNameFor(directory, brokerId));
        string preferences = Path.Combine(directory, "session-preferences.json");
        await File.WriteAllTextAsync(preferences, "not-json");
        var first = new StartupAdapter(true);
        var second = new StartupAdapter(false);
        bool rejected = false;
        try
        {
            await using var unexpected = await WorkerApplication.CreateAsync(new(directory, "Synthetic startup failure",
                EnableLan: false) { EnableBrokerDiscovery = false }, [first, second]);
        }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "malformed startup preferences preserve the original startup failure");
        check(first.Disposals == 1 && second.Disposals == 1,
            "partial startup cleans every adapter even when one cleanup throws");
        using (var reopened = new BrokerStore(Path.Combine(directory, "store")))
            check(reopened.BrokerId == brokerId, "partial startup releases durable ownership for recovery");
        check(await File.ReadAllTextAsync(preferences) == "not-json",
            "startup failure preserves malformed preferences for inspection");
    }

    private sealed class StartupAdapter(bool failDisposal) : ILocalAgentAdapter
    {
        public string Id => "synthetic-startup";
        internal int Disposals;
        public Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token) =>
            throw new InvalidOperationException("Startup failure must precede discovery.");
        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token) =>
            throw new InvalidOperationException("Startup failure must precede delivery.");
        public ValueTask DisposeAsync()
        {
            Disposals++;
            if (failDisposal) throw new IOException("Synthetic cleanup failure.");
            return ValueTask.CompletedTask;
        }
    }
}
