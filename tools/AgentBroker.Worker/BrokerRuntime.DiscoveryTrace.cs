using AgentBroker.Core;
using System.Text.Json.Nodes;

namespace AgentBroker.Worker;

// N-broker-native-lease-gap-01, first step (AgentBroker 1.10.6, lane N round 2): the report-only discovery trace. Each adapter's
// discovery loop (BrokerRuntime.DiscoverLoopAsync) writes, through BrokerStore.AppendEvent, a discovery_pass line on its first
// pass and whenever a pass differs from the last line it wrote, and one session_forgotten line when a native lease it registered
// passed without renewal. Nothing here routes, leases or admits anything. No line holds message text, a label or title, a
// diagnostic description, a process ID or a path.
public sealed partial class BrokerRuntime
{
    // What one discovery loop last wrote, and the native leases it registered; owned by that loop alone (no lock). It is built
    // from the runtime's own data and never read back from an emitted line: AppendEvent takes ownership of each line, which the
    // sink may change (eventsLost), and every line is a fresh object. There is no control for that rule because no reuse path
    // exists to remove.
    private sealed class DiscoveryTraceState
    {
        internal string? Failure;
        internal bool? CensusComplete;
        internal string[]? Diagnostics;
        // The rows of the last COMPLETED pass this loop wrote; cleared and refilled, so a failed pass keeps them.
        internal readonly Dictionary<string, (Guid? Incarnation, string Outcome)> Rows = new(StringComparer.Ordinal);
        // Each native address this loop registered, with the LeaseUntil of its latest registration. Never part of a line's
        // comparison, so renewals alone write nothing.
        internal readonly Dictionary<SessionAddress, DateTimeOffset> Leases = new();
    }

    // Called in the native block, after Register and ObserveNativeSession accepted the row, so Leases holds native proofs only: an
    // mcp row, a peer row or a row of another adapter never reaches it. Before an entry of this session is overwritten (this
    // address renewed) or removed (another incarnation replaced), one whose lease had already passed lapsed inside this pass and
    // says so first: a lease renewed later in the same pass (slow rows are looked up last), or a lapsed incarnation replaced in
    // it, which the store itself forgets as native_lease_elapsed before the replacement. A still-live incarnation replaced by a
    // new one is not a lapse (the store forgets it as registration_replaced) and leaves silently.
    private void RenewNativeLease(DiscoveryTraceState trace, BrokerSession session)
    {
        foreach (var lapsed in trace.Leases.Where(pair => pair.Key.SessionId == session.Address.SessionId && pair.Value <= Now).ToArray())
            Forgotten(trace, session.Adapter, lapsed.Key, lapsed.Value);
        foreach (var replaced in trace.Leases.Keys.Where(address => address.SessionId == session.Address.SessionId && address != session.Address).ToArray())
            trace.Leases.Remove(replaced);
        trace.Leases[session.Address] = session.LeaseUntil;
    }

    // The one session_forgotten write: the end-of-pass sweep and RenewNativeLease's pre-overwrite check both come here. The entry
    // leaves Leases, so one lapse writes one line; registering the address again re-arms it.
    private void Forgotten(DiscoveryTraceState trace, string adapterId, SessionAddress address, DateTimeOffset until)
    {
        trace.Leases.Remove(address);
        store.AppendEvent(ForgottenLine(adapterId, address, until));
    }

    // The runtime's per-row view of a completed pass: the adapter's own trace when it reports one (one entry per session ID, the
    // last one listed wins), else every row the pass returned, as renewed. Read inside the pass's try, so a malformed adapter
    // trace becomes that pass's failure.
    private static Dictionary<string, (Guid? Incarnation, string Outcome)> TraceRows(IReadOnlyList<DiscoveryTraceRow>? reported,
        IReadOnlyList<BrokerSession> sessions)
    {
        var rows = new Dictionary<string, (Guid? Incarnation, string Outcome)>(StringComparer.Ordinal);
        if (reported is null) foreach (var session in sessions) rows[session.Address.SessionId] = (session.Address.Incarnation, "renewed");
        else foreach (var row in reported) rows[row.SessionId] = (row.Incarnation, row.Outcome);
        return rows;
    }

    // After each pass's try and catches, on runtime-built data only; nothing here can throw, so no catch is added (it could have
    // no killing mutation). A pass whose try completed just before shutdown still writes its line. Then every native lease this
    // loop registered that has passed by the runtime clock (the store's Live rule) lapsed, and writes its line.
    private void TraceDiscoveryPass(string adapterId, DiscoveryTraceState trace, Dictionary<string, (Guid? Incarnation, string Outcome)>? rows,
        bool? census, string[]? diagnostics, string? failure)
    {
        WritePassLine(adapterId, trace, rows, census, diagnostics, failure);
        foreach (var lapsed in trace.Leases.Where(pair => pair.Value <= Now).ToArray())
            Forgotten(trace, adapterId, lapsed.Key, lapsed.Value);
    }

    // A completed pass is compared with the last completed pass this loop wrote: changed holds the rows that are new or whose
    // incarnation or outcome differ, dropped the session IDs no longer reported (both ordinal). A failed pass reports its failure
    // code with no census, diagnostics or rows, and keeps the last completed rows, so the next completed pass is compared with
    // them. Repeated identical failures write one line, and recovery writes one.
    private void WritePassLine(string adapterId, DiscoveryTraceState trace, Dictionary<string, (Guid? Incarnation, string Outcome)>? rows,
        bool? census, string[]? diagnostics, string? failure)
    {
        KeyValuePair<string, (Guid? Incarnation, string Outcome)>[]? changed = null;
        string[]? dropped = null;
        if (failure is null && rows is { } current)
        {
            changed = current.Where(pair => !trace.Rows.TryGetValue(pair.Key, out var before) || before != pair.Value)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
            dropped = trace.Rows.Keys.Where(id => !current.ContainsKey(id)).Order(StringComparer.Ordinal).ToArray();
        }
        else { rows = null; census = null; diagnostics = null; }
        if (Unchanged(trace, failure, census, diagnostics, (changed?.Length ?? 0) + (dropped?.Length ?? 0))) return;
        store.AppendEvent(PassLine(adapterId, failure, census, diagnostics, changed, dropped));
        trace.Failure = failure; trace.CensusComplete = census; trace.Diagnostics = diagnostics;
        if (rows is not null) { trace.Rows.Clear(); foreach (var pair in rows) trace.Rows.Add(pair.Key, pair.Value); }
    }

    // A line is skipped only when nothing it would say differs from the last line this loop wrote: the distinct diagnostic codes
    // (canonical: distinct and ordinal at capture) are equal, no row changed, appeared or left, and the failure and census are
    // equal. The initial state (no census, no failure) differs from every pass, so the first pass always writes.
    private static bool Unchanged(DiscoveryTraceState trace, string? failure, bool? census, string[]? diagnostics, int changes) =>
        (trace.Diagnostics is null ? diagnostics is null : diagnostics is not null && trace.Diagnostics.SequenceEqual(diagnostics)) &&
        changes == 0 && trace.Failure == failure && trace.CensusComplete == census;

    // discovery_pass, keys in order: event, adapter, pass, failure, censusComplete, diagnostics, changed, dropped (then eventAt from
    // AppendEvent, and eventsLost only when the sink adds it). A fresh object on every call.
    private JsonObject PassLine(string adapterId, string? failure, bool? census, string[]? diagnostics,
        KeyValuePair<string, (Guid? Incarnation, string Outcome)>[]? changed, string[]? dropped) => new()
    {
        ["event"] = "discovery_pass",
        ["adapter"] = adapterId,
        ["pass"] = failure is null ? "completed" : "failed",
        ["failure"] = failure,
        ["censusComplete"] = census,
        ["diagnostics"] = TraceArray(diagnostics),
        ["changed"] = changed is null ? null : new JsonArray(changed.Select(pair => (JsonNode?)new JsonObject
        {
            ["sessionId"] = pair.Key,
            ["incarnation"] = pair.Value.Incarnation?.ToString("D"),
            ["outcome"] = pair.Value.Outcome,
        }).ToArray()),
        ["dropped"] = TraceArray(dropped),
    };

    // session_forgotten, keys in order: event, adapter, address, cause, leaseUntil (then eventAt, and eventsLost when added).
    private static JsonObject ForgottenLine(string adapterId, SessionAddress address, DateTimeOffset until) => new()
    {
        ["event"] = "session_forgotten",
        ["adapter"] = adapterId,
        ["address"] = address.BrokerId.ToString("D") + "/" + address.SessionId + "/" + address.Incarnation.ToString("D"),
        ["cause"] = "native_lease_elapsed",
        ["leaseUntil"] = until.ToString("O"),
    };

    private static JsonArray? TraceArray(string[]? values) => values is null ? null : new JsonArray(values.Select(value => (JsonNode?)value).ToArray());
}
