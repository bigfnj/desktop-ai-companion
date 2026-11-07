using AgentBroker.CodexDesktopAdapter;

internal static class DiscoveryInvalidationChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: DISCOVERY_INVALIDATION_EXECUTED");
        var engines = new[] { new CodeOwnedEngine(@"C:\Synthetic\codex.exe", 200, @"C:\Synthetic\Code.exe", 100, true) };
        var reuse = new DiscoveryReuse();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int verifications = 0;
        var pending = Task.Run(() => reuse.Verify(engines, () =>
        {
            Interlocked.Increment(ref verifications);
            entered.TrySetResult();
            release.Task.Wait(TimeSpan.FromSeconds(5));
            return engines.Length;
        }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            reuse.Forget();
        }
        finally { release.TrySetResult(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        reuse.Verify(engines, () => { verifications++; return engines.Length; });
        check(verifications == 2,
            "DISCOVERY_INVALIDATION_WINS: a fresh invalidation during in-flight discovery cannot be overwritten by its older success");
        reuse.Verify(engines, () => { verifications++; return engines.Length; });
        check(verifications == 2,
            "DISCOVERY_INVALIDATION_RECOVERS: a verification that starts after invalidation can publish and be reused");
    }
}
