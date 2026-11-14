using AgentBroker.Core;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

internal static partial class CoordinationBenchmark
{
    internal static Action<string>? AfterWaitingMailSeed;

    private sealed class WaitingMailBenchmarkClock : TimeProvider
    {
        internal static readonly DateTimeOffset Origin = new(2026, 11, 14, 3, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Origin;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => TimeSpan.TicksPerHour;
    }

    private static Guid WaitingMailBenchmarkId(int value) =>
        Guid.Parse("71a70000-0000-4000-8000-" + value.ToString("D12", CultureInfo.InvariantCulture));

    // waiting-mail <pending: 0|16|4096> <pending-only|fresh-busy> <new absolute root>
    // No ReadWaitingMail call occurs until the stopwatch for sample zero starts.
    private static string WaitingMail(int pending, string cohort, string root)
    {
        if (pending is not (0 or 16 or 4096) || cohort is not ("pending-only" or "fresh-busy"))
            throw new ArgumentOutOfRangeException(nameof(pending));
        const int recipientCount = 64, samples = 6, gapMilliseconds = 500;
        bool freshBusy = cohort == "fresh-busy";
        string storeDirectory = NewDirectory(root, "store");
        try
        {
            var clock = new WaitingMailBenchmarkClock();
            var now = clock.GetUtcNow();
            Guid brokerId = WaitingMailBenchmarkId(1);
            SessionAddress sender = new(brokerId, "mcp:waiting-benchmark-sender", WaitingMailBenchmarkId(100));
            var recipients = Enumerable.Range(0, recipientCount).Select(index => new SessionAddress(brokerId,
                "claude:waiting-benchmark-" + index.ToString("D2", CultureInfo.InvariantCulture),
                WaitingMailBenchmarkId(1000 + index))).ToArray();
            // Direct durable seeding avoids 4096 growing-snapshot commits. No row exceeds 64 pending.
            var seeds = Enumerable.Range(0, pending).Select(index => new MessageReceipt(
                new BrokerMessage(WaitingMailBenchmarkId(10000 + index), WaitingMailBenchmarkId(20000 + index), null,
                    sender, recipients[index % recipientCount], "Synthetic waiting benchmark message.", now, now.AddHours(1)),
                MessageStatus.Accepted, now)).ToList();
            using (var seed = File.Create(Path.Combine(storeDirectory, "broker-state.json")))
                JsonSerializer.Serialize(seed, new { Version = 1, BrokerId = brokerId,
                    Peers = Array.Empty<PeerTrust>(), Messages = seeds });
            seeds.Clear();
            // Validation can remove an intended seed without changing the requested pending value/oracle.
            AfterWaitingMailSeed?.Invoke(storeDirectory);
            using var store = new BrokerStore(storeDirectory, clock: clock);
            store.Register(new(sender, "Synthetic waiting sender", "mcp",
                SessionCapabilities.Receive | SessionCapabilities.Reply, SessionActivity.Idle, now.AddMinutes(1)));
            foreach (var recipient in recipients)
                store.Register(new(recipient, "Synthetic waiting recipient", "claude-native",
                    SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply,
                    SessionActivity.Busy, now.AddMinutes(1)));
            if (freshBusy)
            {
                // Observe once at age zero. This populates waits without querying the summary or admitting a five-minute notice.
                var observed = store.AdmitDueWaitingMailNotices();
                if (observed.Admitted != 0 || observed.AlreadyAdmitted != 0)
                    throw new InvalidDataException("Waiting benchmark seed unexpectedly admitted a notice.");
            }
            // Build the oracle only after sample zero, so constructing/serializing these result records
            // does not warm their constructors or serializer metadata before the first timed query.
            byte[] ExpectedBytes()
            {
                // Derived from the requested shape and registration order, never actual loaded counts.
                var expectedRows = new SessionWaitingMail[recipientCount + 1];
                expectedRows[0] = new(sender, 0, 0, 0, 0, 0, null, null);
                for (int index = 0; index < recipientCount; index++)
                {
                    int count = pending / recipientCount + (index < pending % recipientCount ? 1 : 0);
                    bool busy = freshBusy && count > 0;
                    expectedRows[index + 1] = new(recipients[index], count, count, count,
                        freshBusy ? count : 0, freshBusy ? 0 : count,
                        busy ? now : null, busy ? 0L : null);
                }
                return JsonSerializer.SerializeToUtf8Bytes(new WaitingMailSnapshot(brokerId, now, 300, expectedRows));
            }
            byte[]? expectedBytes = null;
            List<double> queryMs = [];
            List<string> summarySha256ByCall = [];
            string? summaryUtf8Base64 = null;
            for (int sample = 0; sample < samples; sample++)
            {
                if (sample > 0) Thread.Sleep(gapMilliseconds);
                var watch = Stopwatch.StartNew();
                WaitingMailSnapshot snapshot = store.ReadWaitingMail();
                watch.Stop();
                queryMs.Add(watch.Elapsed.TotalMilliseconds);
                // Serialization, oracle comparison, hashes and output are outside the timed full query.
                expectedBytes ??= ExpectedBytes();
                byte[] actualBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
                if (!actualBytes.AsSpan().SequenceEqual(expectedBytes))
                    throw new InvalidDataException("Waiting benchmark summary differs from the requested fixture.");
                summarySha256ByCall.Add(Convert.ToHexString(SHA256.HashData(actualBytes)));
                summaryUtf8Base64 ??= Convert.ToBase64String(actualBytes);
            }
            return JsonSerializer.Serialize(new
            {
                scenario = "waiting-mail", pending, cohort, sessions = recipientCount + 1,
                recipients = recipientCount, maximumPerRecipient = pending == 4096 ? 64 : pending == 16 ? 1 : 0,
                expectedObservedMessages = freshBusy ? pending : 0,
                samples, gapMilliseconds, firstQuerySample = 0,
                queryMs, summaryUtf8Base64, summarySha256ByCall,
                expectedSummarySha256 = Convert.ToHexString(SHA256.HashData(expectedBytes!))
            });
        }
        finally { DeleteTree(root); }
    }
}
