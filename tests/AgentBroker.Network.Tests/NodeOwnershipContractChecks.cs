using AgentBroker.Core;

internal static class NodeOwnershipContractChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: NODE_OWNERSHIP_CONTRACT_EXECUTED");
        string root;
        using (var owned = OwnedScratch.Create("node-ownership-contract"))
        {
            root = owned.Root;
            string directory = Path.Combine(root, "partial");
            bool refused = false;
            try { await Node.Create(directory, TimeProvider.System, (_, _) => throw new IOException("Synthetic identity-loader failure.")); }
            catch (IOException error) when (error.Message == "Synthetic identity-loader failure.") { refused = true; }
            check(refused, "NODE_SETUP_PRIMARY: partial setup preserves its original injected loader failure");
            using var reopened = new BrokerStore(directory);
            check(reopened.BrokerId != Guid.Empty, "NODE_SETUP_OWNERSHIP: partial setup releases its store ownership for a normal reopen");
        }
        check(!Path.Exists(root), "NODE_SETUP_SCRATCH: the exact owned partial-setup tree is absent after disposal");
        int attempted = 0;
        bool cleanupReported = false;
        try
        {
            await Node.DisposeAllAsync(() => { attempted++; throw new IOException("Synthetic cleanup failure."); },
                () => { attempted++; return ValueTask.CompletedTask; }, () => { attempted++; return ValueTask.CompletedTask; });
        }
        catch (AggregateException error) when (error.InnerExceptions.Count == 1) { cleanupReported = true; }
        check(cleanupReported && attempted == 3, "NODE_DISPOSE_ALL: one cleanup failure cannot skip remaining owned resources");
    }
}
