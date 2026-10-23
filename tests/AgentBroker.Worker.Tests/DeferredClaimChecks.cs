using AgentBroker.Core;
using AgentBroker.Worker;

internal static class DeferredClaimChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string scratchRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scratch"));
        string scratch = Path.Combine(scratchRoot, "deferred-claim-" + Guid.NewGuid().ToString("N"));
        using var selectedSnapshot = new ManualResetEventSlim();
        using var continueSelection = new ManualResetEventSlim();
        using var deferralStarted = new ManualResetEventSlim();
        int armed = 0;
        Task<MessageReceipt?>? selection = null;
        Task? release = null;
        try
        {
            using var store = new BrokerStore(scratch);
            var recipient = new BrokerSession(new(store.BrokerId, "synthetic-deferred-recipient", Guid.NewGuid()),
                "Synthetic deferred recipient", "synthetic", SessionCapabilities.Receive | SessionCapabilities.WakeIdle,
                SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(1));
            store.Register(recipient);
            var sender = recipient with
            {
                Address = new(store.BrokerId, "synthetic-deferred-sender", Guid.NewGuid()),
                Label = "Synthetic deferred sender",
            };
            store.Register(sender);
            var now = DateTimeOffset.UtcNow;
            var message = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null,
                sender.Address, recipient.Address,
                "Synthetic concurrency probe.", now, now.AddMinutes(1));
            store.Accept(message, store.BrokerId);
            var claims = new DeferredDeliveryClaims(store, () =>
            {
                if (Interlocked.Exchange(ref armed, 0) != 1) return;
                selectedSnapshot.Set();
                if (!continueSelection.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("DeferredClaimChecks.cs: controlled selection was not released.");
            });
            check(claims.BeginDelivery()?.Message.Id == message.Id,
                "DeferredClaimChecks.cs: the synthetic message has one initial in-flight claim");
            Interlocked.Exchange(ref armed, 1);
            selection = Task.Factory.StartNew(claims.BeginDelivery, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            if (!selectedSnapshot.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("DeferredClaimChecks.cs: selection did not reach its controlled snapshot.");
            release = Task.Factory.StartNew(() =>
            {
                deferralStarted.Set();
                claims.ReleaseUnsubmittedClaim(message.Id, "synthetic_busy");
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            if (!deferralStarted.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("DeferredClaimChecks.cs: the competing deferral did not start.");
            bool overtook = release.Wait(TimeSpan.FromSeconds(2));
            continueSelection.Set();
            await Task.WhenAll(selection, release).WaitAsync(TimeSpan.FromSeconds(5));
            check(!overtook && selection.Result is null,
                "DeferredClaimChecks.cs: returning a busy claim cannot overtake a previously captured selection snapshot");
            check(claims.BeginDelivery() is null && store.FindReceipt(message.Id)?.Status == MessageStatus.Accepted,
                "DeferredClaimChecks.cs: the returned claim remains accepted throughout its actual retry backoff");
            await Task.Delay(TimeSpan.FromMilliseconds(5100));
            check(claims.BeginDelivery()?.Message.Id == message.Id,
                "DeferredClaimChecks.cs: the same unsubmitted message becomes claimable after its retry backoff");
        }
        finally
        {
            continueSelection.Set();
            if (selection is not null) await DrainAsync(selection);
            if (release is not null) await DrainAsync(release);
            if (!Path.GetFullPath(scratch).StartsWith(scratchRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Synthetic deferred-claim fixture escaped its scratch root.");
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    private static async Task DrainAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) when (task.IsCompleted) { /* Preserve the original failure after observing its task. */ }
    }
}
