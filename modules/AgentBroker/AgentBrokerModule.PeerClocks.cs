using System.Globalization;
using AgentBroker.Protocol;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    internal static string PeerClockText(PeerClockView? clock)
    {
        if (clock is null) return "Clock: not checked by this worker.";
        string age = clock.AgeSeconds is { } seconds && double.IsFinite(seconds) && seconds >= 0
            ? " Observed " + seconds.ToString("0", CultureInfo.InvariantCulture) + " s ago." : "";
        string state = clock.State switch
        {
            "not_checked" => "not checked yet",
            "missing_date" => "unavailable (the peer returned no Date header)",
            "invalid_date" => "unavailable (the peer returned an invalid Date header)",
            "transport_unavailable" => "unavailable (the HTTPS request did not complete)",
            "local_clock_changed" => "unavailable (this PC's clock changed during or after observation)",
            "stale" => "stale; refresh status after contacting the peer",
            "high_latency" => "unavailable (the HTTPS round trip exceeded 2 seconds)",
            _ => "unavailable"
        };
        if (clock.State != "observed" || clock.OffsetSeconds is not { } offset || !double.IsFinite(offset) ||
            clock.UncertaintySeconds is not { } uncertainty || !double.IsFinite(uncertainty) || uncertainty < 0)
            return "Clock: " + state + "." + age;
        string result = "Clock: peer minus this PC " + offset.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) +
            " s, uncertainty " + uncertainty.ToString("0.0", CultureInfo.InvariantCulture) + " s." + age;
        if (clock.Warning)
            result += " The difference exceeds 5 seconds after uncertainty. Check time settings on both PCs.";
        return result;
    }
}
