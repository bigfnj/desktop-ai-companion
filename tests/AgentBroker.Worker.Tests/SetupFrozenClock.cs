using System.Diagnostics;

// A deadline fixture's clock (post-push audit of e398eba2, test-only). It holds one instant through the durable setup, so a
// slow commit cannot age a message or a lease before the wait under test starts, and it advances with real monotonic
// time only after Start. Call Start once the wait is proven admitted on a pending record. Like ToolFixture.ManualClock it
// fakes only GetUtcNow: timers and waits stay real, so a deadline wake still has to come from the wait's own timer.
internal sealed class SetupFrozenClock : TimeProvider
{
    private readonly DateTimeOffset origin = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private long startedAt;

    internal void Start() => Interlocked.CompareExchange(ref startedAt, Stopwatch.GetTimestamp(), 0);

    // Real time since Start, from the same monotonic timestamp that drives GetUtcNow, so a lower bound measured with it
    // has no scheduling gap between starting the clock and starting a second stopwatch.
    internal TimeSpan ElapsedSinceStart
    {
        get
        {
            long started = Interlocked.Read(ref startedAt);
            return started == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(started);
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        long started = Interlocked.Read(ref startedAt);
        return started == 0 ? origin : origin + Stopwatch.GetElapsedTime(started);
    }

    // The admission witness: the wait holds the one active wait slot and has not ended. Bounded, so a refused wait returns
    // false instead of hanging; the frozen clock means the time spent here cannot age the record under test. A refused
    // wait has completed faulted, so the caller must check this result BY NAME before it awaits the wait, or the await
    // rethrows the refusal as an unlabelled exception and the named check is never reached.
    internal static async Task<bool> AdmittedAsync(ToolFixture fixture, Task wait)
    {
        var limit = Stopwatch.StartNew();
        while (limit.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (wait.IsCompleted) return false;
            if (fixture.Runtime.ActiveWaitCount == 1) return true;
            await Task.Delay(10);
        }
        return false;
    }
}
