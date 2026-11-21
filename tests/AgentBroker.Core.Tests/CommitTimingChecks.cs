using AgentBroker.Core;

// Commit timing diagnostic (N-broker-next-native-test-timing-01, AgentBroker next campaign 2026-11-08). Each label names the
// rule a mutation must break. Every count is compared with a snapshot taken just before its action, so no check assumes a
// quiet disk: an ordinary commit that happens to take a second only adds to Slow, and one case makes that deterministic.
internal static class CommitTimingChecks
{
    public static int Run(string root)
    {
        int passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed++;
        }
        using var store = new BrokerStore(Path.Combine(root, "commit-timing"));
        BrokerSession Session(string id) => new(new(store.BrokerId, id, Guid.NewGuid()), "Synthetic timing session", "codex-native",
            SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply, SessionActivity.Busy, DateTimeOffset.UtcNow.AddMinutes(1));
        // Sessions are process-local, so registering one never commits; accepting a message commits exactly once.
        var sender = Session("codex:commit-timing-sender");
        var recipient = Session("codex:commit-timing-recipient");
        store.Register(sender); store.Register(recipient);
        BrokerMessage Message(string text) => new(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, recipient.Address, text,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));

        var start = store.CommitTimings();
        store.Accept(Message("Synthetic message committed normally."), store.BrokerId);
        var ordinary = store.CommitTimings();
        Check(ordinary.Completed == start.Completed + 1 && ordinary.Failed == start.Failed &&
            ordinary.ActiveStage is null && ordinary.ActiveStartUtc is null,
            "COMMIT_TIMING_COMPLETED: an ordinary commit is counted once and leaves nothing in flight");

        // Deterministic slow ordinary commit: the same delta checks hold, and Slow grows by exactly one.
        store.CommitStageProbe = stage => { if (stage == "snapshot-write") Thread.Sleep(1100); };
        try { store.Accept(Message("Synthetic ordinary message whose commit is slow."), store.BrokerId); }
        finally { store.CommitStageProbe = null; }
        var slowOrdinary = store.CommitTimings();
        Check(slowOrdinary.Completed == ordinary.Completed + 1 && slowOrdinary.Slow == ordinary.Slow + 1 &&
            slowOrdinary.Failed == ordinary.Failed && slowOrdinary.ActiveStage is null && slowOrdinary.SlowestMilliseconds >= 1000,
            "COMMIT_TIMING_SLOW_BASELINE: an already-slow ordinary commit is counted completed and slow, so no check needs a quiet disk");

        // A commit held inside snapshot-replace while the store gate is held.
        var settled = store.CommitTimings();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool armed = true;
        store.CommitStageProbe = stage =>
        {
            if (!armed || stage != "snapshot-replace") return;
            armed = false;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };
        var heldAt = DateTimeOffset.UtcNow;
        Task held = Task.Run(() => store.Accept(Message("Synthetic message whose commit is held."), store.BrokerId));
        Task<IReadOnlyList<BrokerSession>>? blocked = null;
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(10)),
                "COMMIT_TIMING_WITNESS_STAGE_REACHED: the controlled commit reaches its snapshot-replace stage marker");
            // A real store call queues behind the held commit: proof that the gate is held while the observer reads.
            blocked = Task.Run(() => store.Directory());
            Check(!blocked.Wait(TimeSpan.FromMilliseconds(300)),
                "COMMIT_TIMING_WITNESS_GATE_HELD: a store call made during the held commit waits for it");
            Task<CommitTimingSnapshot> observed = Task.Run(store.CommitTimings);
            Check(observed.Wait(TimeSpan.FromSeconds(1)),
                "COMMIT_TIMING_OBSERVER_UNBLOCKED: reading commit timing never waits on the gate the diagnosed commit holds");
            Thread.Sleep(1100);
            var inFlight = store.CommitTimings();
            Check(inFlight.ActiveStage == "snapshot-replace" && inFlight.ActiveMilliseconds >= 1000 &&
                inFlight.ActiveStartUtc is { } began && began >= heldAt.AddSeconds(-1) && began <= DateTimeOffset.UtcNow &&
                inFlight.ToString().Contains("one in flight in snapshot-replace since ", StringComparison.Ordinal),
                "COMMIT_TIMING_IN_FLIGHT: a held commit is reported in flight with its stage, start and elapsed time");
        }
        finally
        {
            release.Set();
            held.Wait(TimeSpan.FromSeconds(30));
            blocked?.Wait(TimeSpan.FromSeconds(30));
            store.CommitStageProbe = null;
        }
        var after = store.CommitTimings();
        Check(after.ActiveStage is null && after.ActiveStartUtc is null && after.Completed == settled.Completed + 1 &&
            after.Slow == settled.Slow + 1 && after.SlowestMilliseconds >= 1000 && after.LastSlowUtc is not null,
            "COMMIT_TIMING_SLOW: the held commit, once ended, adds exactly one slow completed commit and clears its start");

        // A commit that fails in a stage: Failed grows, Completed does not, nothing stays in flight, the caller sees the
        // original failure.
        var failure = new IOException("Synthetic commit failure for the timing check.");
        store.CommitStageProbe = stage => { if (stage == "snapshot-replace") throw failure; };
        var beforeFailure = store.CommitTimings();
        Exception? surfaced = null;
        try { store.Accept(Message("Synthetic message whose commit fails."), store.BrokerId); }
        catch (Exception error) { surfaced = error; }
        finally { store.CommitStageProbe = null; }
        var failed = store.CommitTimings();
        Check(ReferenceEquals(surfaced, failure) && failed.Failed == beforeFailure.Failed + 1 && failed.Completed == beforeFailure.Completed &&
            failed.ActiveStage is null && failed.ActiveStartUtc is null,
            "COMMIT_TIMING_FAILED: a failed commit counts as failed, not completed, clears its in-flight entry and surfaces its original failure");

        Check(failed.ToString().StartsWith("store commits (duration while the gate is held): ", StringComparison.Ordinal) &&
            failed.ToString().Contains("none in flight", StringComparison.Ordinal),
            "COMMIT_TIMING_TEXT: the summary names its gate-held scope and says when nothing is in flight");
        return passed;
    }
}
