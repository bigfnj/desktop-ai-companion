using System.Globalization;
using System.Text;
using AgentBroker.Core;
using AgentBroker.Worker.Federation;

internal static class PeerObservationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: PEER_OBSERVATIONS_EXECUTED");
        Guid peer = Guid.NewGuid();
        bool Refusal(string body) => PeerAdmissionRefusal.TryRead(Encoding.UTF8.GetBytes(body), peer, out _);
        check(Refusal("""{"error":"recipient_cannot_receive"}""") &&
            PeerAdmissionRefusal.TryRead("""{"error":"private_session"}"""u8, peer, out var refusal) &&
            refusal is { Code: "private_session" } && refusal.BrokerId == peer,
            "PEER_REFUSAL_KNOWN: only a bounded recognized admission error yields a typed refusal");
        check(new[] { """{"error":"unknown_future_code"}""", """{"error":"recipient_cannot_receive","other":1}""",
            """{"error":"recipient_cannot_receive","error":"recipient_cannot_receive"}""", """{"Error":"recipient_cannot_receive"}""",
            """{"error":null}""", "[]", "{", new string(' ', 1025) + """{"error":"recipient_cannot_receive"}""" }.All(body => !Refusal(body)),
            "PEER_REFUSAL_STRICT: unknown, duplicate, extra, malformed and oversized answers remain unconfirmed");

        var clock = new Clock();
        string directory = Path.Combine(AppContext.BaseDirectory, "scratch", "clock-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new BrokerStore(directory, clock: clock);
            var trust = new PeerTrust(peer, "Synthetic clock peer", "https://192.0.2.10/", new string('A', 64), clock.GetUtcNow());
            store.AuthorizePeer(trust);
            var observations = new PeerClockObservations(clock, store.IsPeerAuthorized);
            var authorization = store.CapturePeerAuthorization(trust)!;
            check(observations.Read(peer) is { State: "not_checked", OffsetSeconds: null, Warning: false },
                "PEER_CLOCK_NOT_ZERO: no observation is unavailable, never a zero-offset success");
            void Observe(double offset, double seconds = .2)
            {
                var start = observations.Begin();
                string date = (start.Utc + TimeSpan.FromSeconds(offset)).ToString("r", CultureInfo.InvariantCulture);
                clock.Advance(TimeSpan.FromSeconds(seconds));
                observations.Record(peer, authorization, [date], start, observations.Begin());
            }
            Observe(20);
            var ahead = observations.Read(peer);
            check(ahead.State == "observed" && ahead.OffsetSeconds > 19 && ahead.UncertaintySeconds == 1.1 && ahead.Warning,
                "PEER_CLOCK_AHEAD: midpoint offset includes RTT and Date quantization before warning");
            Observe(-20);
            check(observations.Read(peer) is { OffsetSeconds: < -19, Warning: true },
                "PEER_CLOCK_BEHIND: a negative offset receives the same qualified warning");
            Observe(6.1);
            check(!observations.Read(peer).Warning,
                "PEER_CLOCK_UNCERTAINTY: an apparent difference inside the uncertainty margin does not warn");
            Observe(30, 3);
            check(observations.Read(peer) is { State: "high_latency", Warning: false },
                "PEER_CLOCK_LATENCY: an overly slow round trip is explicit and never warns");
            var begun = observations.Begin();
            observations.Record(peer, authorization, null, begun, begun);
            check(observations.Read(peer) is { State: "missing_date", OffsetSeconds: null, Warning: false },
                "PEER_CLOCK_MISSING: missing Date has no fabricated offset");
            observations.Record(peer, authorization, ["not-a-date"], begun, begun);
            check(observations.Read(peer).State == "invalid_date", "PEER_CLOCK_INVALID: malformed Date is explicit");
            observations.Record(peer, authorization, [begun.Utc.ToString("r"), begun.Utc.ToString("r")], begun, begun);
            check(observations.Read(peer).State == "invalid_date", "PEER_CLOCK_DUPLICATE: ambiguous duplicate dates are refused");
            begun = observations.Begin(); clock.Jump(TimeSpan.FromSeconds(10));
            observations.Record(peer, authorization, [begun.Utc.ToString("r")], begun, observations.Begin());
            check(observations.Read(peer) is { State: "local_clock_changed", OffsetSeconds: null, Warning: false },
                "PEER_CLOCK_JUMP: wall-clock discontinuity cannot create a skew warning");
            Observe(20); clock.Advance(PeerClockObservations.MaximumAge + TimeSpan.FromSeconds(1));
            check(observations.Read(peer) is { State: "stale", Warning: false, AgeSeconds: > 300 },
                "PEER_CLOCK_STALE: monotonic observation age suppresses an old warning");
            var oldStart = observations.Begin(); clock.Advance(TimeSpan.FromMilliseconds(100));
            var oldEnd = observations.Begin();
            var newStart = observations.Begin(); clock.Advance(TimeSpan.FromMilliseconds(100));
            observations.Record(peer, authorization, [(newStart.Utc + TimeSpan.FromSeconds(20)).ToString("r")], newStart, observations.Begin());
            observations.Record(peer, authorization, [(oldStart.Utc - TimeSpan.FromSeconds(20)).ToString("r")], oldStart, oldEnd);
            check(observations.Read(peer).OffsetSeconds > 19,
                "PEER_CLOCK_LATEST_SAMPLE: delayed publication of an older concurrent response cannot replace a newer sample");
            Observe(20); clock.Jump(TimeSpan.FromSeconds(-20));
            check(observations.Read(peer).State == "local_clock_changed",
                "PEER_CLOCK_LATER_JUMP: a discontinuity after sampling invalidates the comparison");
            store.RevokePeer(peer); store.AuthorizePeer(trust);
            check(observations.Read(peer).State == "not_checked",
                "PEER_CLOCK_NEW_AUTHORIZATION: re-pairing cannot inherit the previous authorization's observation");
            for (int index = 0; index < 40; index++)
            {
                var rotating = trust with { BrokerId = Guid.NewGuid() };
                store.AuthorizePeer(rotating);
                var rotatingAuthorization = store.CapturePeerAuthorization(rotating)!;
                var start = observations.Begin(); clock.Advance(TimeSpan.FromMilliseconds(100));
                observations.Record(rotating.BrokerId, rotatingAuthorization, [(start.Utc + TimeSpan.FromSeconds(20)).ToString("r")], start, observations.Begin());
                store.RevokePeer(rotating.BrokerId);
            }
            // This is a resource-bound assertion: revoked identities are no longer externally readable, but must
            // still release their retained samples instead of accumulating forever across pairing churn.
            var retained = (System.Collections.IDictionary)typeof(PeerClockObservations)
                .GetField("samples", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(observations)!;
            check(retained.Count <= 32, "PEER_CLOCK_CAPACITY: pairing churn cannot retain more than 32 clock samples");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    internal sealed class Clock : TimeProvider
    {
        private DateTimeOffset utc = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        private long timestamp;
        public override DateTimeOffset GetUtcNow() => utc;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        internal void Advance(TimeSpan elapsed) { utc += elapsed; timestamp += elapsed.Ticks; }
        internal void Jump(TimeSpan elapsed) => utc += elapsed;
    }
}
