using System.Diagnostics;
using System.Globalization;

namespace AgentBroker.Core;

/// <summary>
/// One reading of <see cref="BrokerStore.CommitTimings"/>. Every Commit runs while the store gate is held, so a commit's
/// duration is time the gate was held FOR THAT COMMIT; total gate hold time is not measured here.
/// </summary>
internal readonly record struct CommitTimingSnapshot(long Completed, long Failed, long Slow, double SlowestMilliseconds,
    DateTimeOffset? LastSlowUtc, string? ActiveStage, DateTimeOffset? ActiveStartUtc, double ActiveMilliseconds)
{
    public override string ToString()
    {
        string slowest = SlowestMilliseconds.ToString("F0", CultureInfo.InvariantCulture);
        string last = LastSlowUtc is { } at ? ", last slow at " + at.ToString("HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) : "";
        string active = ActiveStage is null ? "none in flight"
            : "one in flight in " + ActiveStage + " since " +
              (ActiveStartUtc ?? default).ToString("HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + " for " +
              ActiveMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms";
        return $"store commits (duration while the gate is held): {Completed} completed, {Failed} failed, {Slow} over 1 s " +
            $"(slowest {slowest} ms{last}); {active}";
    }
}

// Process-local commit timing for attributing timeouts (N-broker-next-native-test-timing-01, AgentBroker next campaign
// 2026-11-08). Read WITHOUT the gate, so an observer never waits behind the work it diagnoses, and a commit stuck in IO
// shows as in flight instead of as zero slow completed writes. Nothing here is durable, in Status, on the MCP or federation
// wire, or carries a path or payload; it changes no timeout and no storage behaviour.
public sealed partial class BrokerStore
{
    internal static readonly TimeSpan SlowCommit = TimeSpan.FromSeconds(1);
    private static readonly string[] CommitStages = ["notices", "tombstones", "snapshot-write", "snapshot-replace", "compaction"];
    private const int NoticesStage = 0, TombstonesStage = 1, SnapshotWriteStage = 2, SnapshotReplaceStage = 3, CompactionStage = 4;
    private long commitStartTimestamp; // Stopwatch timestamp of the commit in flight; 0 when none. Written last, cleared first.
    private long commitStartUtcTicks;  // Wall-clock start of that commit, reported only while the timestamp says it is in flight.
    private int commitStage;
    private long commitsCompleted, commitsFailed, commitsSlow, slowestCommitTicks, lastSlowCommitUtcTicks;

    /// <summary>Test seam: called with each stage's name as it begins, while the gate is held. Production never sets it.</summary>
    internal Action<string>? CommitStageProbe { get; set; }

    internal CommitTimingSnapshot CommitTimings()
    {
        long start = Volatile.Read(ref commitStartTimestamp);
        long startUtc = Volatile.Read(ref commitStartUtcTicks);
        int stage = Volatile.Read(ref commitStage);
        double active = start == 0 ? 0 : Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        // A commit that ended (or a new one that began) between the reads is reported as finished, never with a stale
        // stage or start.
        bool inFlight = start != 0 && Volatile.Read(ref commitStartTimestamp) == start;
        long lastSlow = Volatile.Read(ref lastSlowCommitUtcTicks);
        return new(Interlocked.Read(ref commitsCompleted), Interlocked.Read(ref commitsFailed), Interlocked.Read(ref commitsSlow),
            TimeSpan.FromTicks(Interlocked.Read(ref slowestCommitTicks)).TotalMilliseconds,
            lastSlow == 0 ? null : new DateTimeOffset(lastSlow, TimeSpan.Zero),
            inFlight ? CommitStages[stage] : null, inFlight ? new DateTimeOffset(startUtc, TimeSpan.Zero) : null, inFlight ? active : 0);
    }

    private void BeginCommitStage(int stage)
    {
        Volatile.Write(ref commitStage, stage);
        CommitStageProbe?.Invoke(CommitStages[stage]);
    }

    private void Commit(State next)
    {
        Volatile.Write(ref commitStage, NoticesStage);
        Volatile.Write(ref commitStartUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        Volatile.Write(ref commitStartTimestamp, Stopwatch.GetTimestamp());
        bool succeeded = false;
        try { CommitCore(next); succeeded = true; }
        finally { EndCommitTiming(succeeded); }
    }

    private void EndCommitTiming(bool succeeded)
    {
        long start = Interlocked.Exchange(ref commitStartTimestamp, 0);
        if (start == 0) return;
        long ticks = Stopwatch.GetElapsedTime(start).Ticks;
        if (succeeded) Interlocked.Increment(ref commitsCompleted);
        else Interlocked.Increment(ref commitsFailed);
        if (ticks >= SlowCommit.Ticks)
        {
            Interlocked.Increment(ref commitsSlow);
            Volatile.Write(ref lastSlowCommitUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        }
        long slowest;
        while (ticks > (slowest = Interlocked.Read(ref slowestCommitTicks)) &&
            Interlocked.CompareExchange(ref slowestCommitTicks, ticks, slowest) != slowest) { }
    }
}
