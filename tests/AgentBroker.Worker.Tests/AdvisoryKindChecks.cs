using System.Text.Json;

// 1.10.6 lane N round 2 (N-broker-standby-response-shape-01, BACKLOG.md:117): the advisory contract in docs/agentbroker.md
// ("### Advisory blocks"). A tool result's content[0] (or structuredContent) holds the result. Every later block is either a
// JSON object with a string kind in the agentbroker.* namespace or plain text (today only send's generic-mailbox advisory,
// BrokerTools.SendAdvisory); a client ignores any later block that is not such an object, unknown kinds and unknown members.
// One statement in each Worker.Tests helper that parses an actual worker's tools/call response feeds this collector, so it
// collects the kinds in the tool results its own wire helpers parse and cannot see a kind on a path those helpers never
// receive. Synthetic fixtures, direct unit calls and CoordinationBenchmark are deliberately not fed.
// Both entry points are total: a shape they cannot read contributes nothing and never throws, so no existing check changes
// its first failure. Concurrent RPCs (ReplyToolChecks, McpActivityChecks) are serialized by one lock.
// Killing mutation: advisory-third-kind-emitted (a third kind after recovery_guidance in BrokerRecoveryGuidance.AppendReceipt).
internal static class AdvisoryKindChecks
{
    internal const string Label = "ADVISORY_KINDS_EXACT_SET: the advisory kinds in this run's actual worker tool results are exactly agentbroker.pending_messages and agentbroker.recovery_guidance";
    private const string Namespace = "agentbroker.";
    private const string PendingKind = "agentbroker.pending_messages";
    private const string GuidanceKind = "agentbroker.recovery_guidance";
    private static readonly string[] Expected = [PendingKind, GuidanceKind]; // ordinal-sorted, the order Kinds enumerates
    private static readonly object Gate = new();
    private static readonly SortedSet<string> Kinds = new(StringComparer.Ordinal);
    private static int results, skippedPlainText;

    // A JSON-RPC response to tools/call: its result.content[1..]. content[0] (the payload or the SDK error text) is never read.
    // Other responses (initialize, tools/list, JSON-RPC errors) carry no result.content and are ignored.
    internal static void ObserveResponse(JsonElement response)
    {
        List<string?> later = [];
        try
        {
            if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array) return;
            foreach (var block in content.EnumerateArray().Skip(1))
                later.Add(block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var type) &&
                    type.ValueKind == JsonValueKind.String && type.GetString() == "text" &&
                    block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null);
        }
        catch (InvalidOperationException) { return; } // also ObjectDisposedException: an unreadable element contributes nothing
        Record(later);
    }

    // An --mcp-call output object (McpCallClient.RunAsync and RunToolAsync): its "advisories" string array, which holds every
    // text block after the first (McpCallClient.DecodeToolResult). Outputs without one (list-tools, rpc_error, refusals) are ignored.
    internal static void ObserveCallOutput(JsonElement output)
    {
        List<string?> later = [];
        try
        {
            if (output.ValueKind != JsonValueKind.Object || !output.TryGetProperty("advisories", out var advisories) ||
                advisories.ValueKind != JsonValueKind.Array) return;
            foreach (var advisory in advisories.EnumerateArray())
                later.Add(advisory.ValueKind == JsonValueKind.String ? advisory.GetString() : null);
        }
        catch (InvalidOperationException) { return; } // also ObjectDisposedException: an unreadable element contributes nothing
        Record(later);
    }

    // Prints one NOTE line to STDOUT with counts and fixed names only (an unexpected kind is counted, never printed), then
    // asserts. The hygiene runner grades the first stderr line (verify_failure), so this line never goes to stderr.
    // Not vacuous: an unfed or empty collector fails the exact-set assertion. There is no degraded mode.
    internal static void Run(Action<bool, string> check)
    {
        string[] observed;
        int observedResults, skipped;
        lock (Gate)
        {
            observed = [.. Kinds];
            observedResults = results;
            skipped = skippedPlainText;
        }
        int unexpected = observed.Count(kind => !Expected.Contains(kind, StringComparer.Ordinal));
        Console.WriteLine("NOTE ADVISORY_KINDS_OBSERVED results=" + observedResults +
            " pending_messages=" + (observed.Contains(PendingKind, StringComparer.Ordinal) ? "true" : "false") +
            " recovery_guidance=" + (observed.Contains(GuidanceKind, StringComparer.Ordinal) ? "true" : "false") +
            " unexpected=" + unexpected + " skipped_plain_text=" + skipped);
        check(observed.SequenceEqual(Expected, StringComparer.Ordinal), Label);
    }

    // One observed tool result. A later block that is not a JSON object with a string agentbroker.* kind is counted as a
    // skipped plain-text block (today only send's mailbox advisory; a non-text block, which this worker never returns, too).
    private static void Record(List<string?> later)
    {
        List<string> kinds = [];
        int skipped = 0;
        foreach (string? text in later)
        {
            if (KindOf(text) is { } kind) kinds.Add(kind);
            else skipped++;
        }
        lock (Gate)
        {
            results++;
            skippedPlainText += skipped;
            Kinds.UnionWith(kinds);
        }
    }

    // The string kind of a JSON object in the agentbroker.* namespace, else null (plain text, other JSON, or no text).
    private static string? KindOf(string? text)
    {
        if (text is null) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("kind", out var kind) &&
                kind.ValueKind == JsonValueKind.String && kind.GetString() is { } value &&
                value.StartsWith(Namespace, StringComparison.Ordinal) ? value : null;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { return null; }
    }
}
