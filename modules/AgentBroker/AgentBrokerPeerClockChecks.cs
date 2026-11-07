using AgentBroker.Protocol;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    private static void RunPeerClockChecks(Action<bool, string> check)
    {
        var clock = new PeerClockView(Guid.NewGuid(), "observed", 20, 1.1, .2, DateTimeOffset.UtcNow, 2, true);
        string text = PeerClockText(clock);
        check(text.Contains("+20.0 s", StringComparison.Ordinal) && text.Contains("uncertainty 1.1 s", StringComparison.Ordinal) &&
            text.Contains("Observed 2 s ago", StringComparison.Ordinal) && text.Contains("Check time settings on both PCs", StringComparison.Ordinal),
            "PEER_CLOCK_PANE_OBSERVED: local display shows signed offset, uncertainty, age and qualified guidance");
        check(!PeerClockText(clock with { State = "stale", Warning = false }).Contains("+20.0", StringComparison.Ordinal) &&
            PeerClockText(clock with { State = "stale" }).Contains("stale", StringComparison.Ordinal) &&
            PeerClockText(null).Contains("not checked", StringComparison.Ordinal),
            "PEER_CLOCK_PANE_UNAVAILABLE: stale and absent comparisons are never shown as healthy zero offsets");
        foreach (string state in new[] { "missing_date", "invalid_date", "local_clock_changed", "high_latency", "transport_unavailable" })
            check(PeerClockText(clock with { State = state }).Contains("unavailable", StringComparison.Ordinal) &&
                !PeerClockText(clock with { State = state }).Contains("difference exceeds", StringComparison.Ordinal),
                "PEER_CLOCK_PANE_FAILURE_STATE: " + state + " remains explicit without a skew warning");
        check(PeerClockText(clock with { OffsetSeconds = double.NaN }).Contains("unavailable", StringComparison.Ordinal),
            "PEER_CLOCK_PANE_FINITE: invalid numeric observations cannot appear as measured offsets");
    }
}
