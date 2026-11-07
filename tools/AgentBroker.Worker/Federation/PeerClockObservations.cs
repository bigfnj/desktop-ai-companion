using System.Globalization;
using AgentBroker.Core;
using AgentBroker.Protocol;

namespace AgentBroker.Worker.Federation;

/// <summary>Local advisory observations only. No time here participates in admission, expiry or authorization.</summary>
internal sealed class PeerClockObservations(TimeProvider clock, Func<BrokerStore.PeerAuthorization, bool> isAuthorized)
{
    internal static readonly TimeSpan MaximumRoundTrip = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(5);
    internal readonly record struct Timing(DateTimeOffset Utc, long Timestamp);
    private sealed record Sample(BrokerStore.PeerAuthorization Authorization, PeerClockView View, long Timestamp);
    private readonly object gate = new();
    private readonly Dictionary<Guid, Sample> samples = [];

    internal Timing Begin() => new(clock.GetUtcNow(), clock.GetTimestamp());

    internal void Record(Guid peer, BrokerStore.PeerAuthorization authorization, IEnumerable<string>? dateValues, Timing start, Timing end)
    {
        string state;
        double? offset = null, uncertainty = null;
        TimeSpan roundTrip = clock.GetElapsedTime(start.Timestamp, end.Timestamp);
        double? elapsed = roundTrip >= TimeSpan.Zero ? roundTrip.TotalSeconds : null;
        string[] dates = dateValues?.Take(2).ToArray() ?? [];
        if (roundTrip < TimeSpan.Zero || ((end.Utc - start.Utc) - roundTrip).Duration() > TimeSpan.FromSeconds(1))
            state = "local_clock_changed";
        else if (dates.Length == 0) state = "missing_date";
        else if (dates.Length != 1 || dates[0].Length > 128 ||
            !DateTimeOffset.TryParseExact(dates[0], "r", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var peerDate))
            state = "invalid_date";
        else
        {
            offset = (peerDate - (start.Utc + roundTrip / 2)).TotalSeconds;
            // HTTP Date has one-second resolution. Include that whole second plus half of the round trip.
            uncertainty = 1 + roundTrip.TotalSeconds / 2;
            state = roundTrip > MaximumRoundTrip ? "high_latency" : "observed";
        }
        bool warning = state == "observed" && Math.Abs(offset!.Value) - uncertainty!.Value > 5;
        Put(peer, authorization, new(peer, state, offset, uncertainty, elapsed, end.Utc, 0, warning), end.Timestamp);
    }

    internal void Unavailable(Guid peer, BrokerStore.PeerAuthorization authorization)
    {
        var end = Begin();
        Put(peer, authorization, new(peer, "transport_unavailable", null, null, null, end.Utc, 0, false), end.Timestamp);
    }

    private void Put(Guid peer, BrokerStore.PeerAuthorization authorization, PeerClockView view, long timestamp)
    {
        lock (gate)
        {
            if (!isAuthorized(authorization) || samples.TryGetValue(peer, out var previous) && previous.Timestamp > timestamp) return;
            samples[peer] = new(authorization, view, timestamp);
            if (samples.Count > 32) samples.Remove(samples.MinBy(pair => pair.Value.Timestamp).Key);
        }
    }

    internal PeerClockView Read(Guid peer)
    {
        Sample? sample;
        lock (gate) sample = samples.GetValueOrDefault(peer);
        if (sample is null || !isAuthorized(sample.Authorization))
            return new(peer, "not_checked", null, null, null, null, null, false);
        var now = Begin();
        TimeSpan age = clock.GetElapsedTime(sample.Timestamp, now.Timestamp);
        if (age < TimeSpan.Zero || sample.View.CheckedUtc is { } checkedUtc &&
            ((now.Utc - checkedUtc) - age).Duration() > TimeSpan.FromSeconds(1))
            return sample.View with { State = "local_clock_changed", OffsetSeconds = null, UncertaintySeconds = null, AgeSeconds = null, Warning = false };
        return sample.View with { State = age > MaximumAge ? "stale" : sample.View.State, AgeSeconds = age.TotalSeconds,
            Warning = age <= MaximumAge && sample.View.Warning };
    }
}
