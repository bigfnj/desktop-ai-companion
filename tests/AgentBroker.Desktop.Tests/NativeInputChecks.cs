using System.Text;
using System.Text.Json.Nodes;
using AgentBroker.CodexDesktopAdapter;

internal static class NativeInputChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var record = check;
        check = (value, label) => record(value, "NativeInputChecks.cs: " + label);
        Console.WriteLine("WITNESS: CODEX_NATIVE_INPUT_EXECUTED");
        Guid thread = Guid.NewGuid(), message = Guid.NewGuid();
        const string content = "Readable 猫 & <tag> ' + ` and a quote \" remain peer data.";
        var turn = NativeExternalPeerInput.CreateTurnStart(thread, message, content);
        string visible = (string)turn["request"]!["input"]![0]!["text"]!;
        check(visible.Contains("猫 & <tag> ' + `", StringComparison.Ordinal),
            "CODEX_VISIBLE_JSON_READABLE: Unicode and punctuation remain readable in the quoted native prompt");
        var envelope = (JsonObject)JsonNode.Parse(visible[(visible.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
        check((string?)envelope["content"] == content && (string?)envelope["message_id"] == message.ToString("D"),
            "CODEX_VISIBLE_JSON_ROUNDTRIP: required JSON escaping preserves complete exact peer data and message ID");
        check((string?)turn["context"]!["responseItems"]![0]!["content"]![0]!["text"] == visible,
            "CODEX_VISIBLE_JSON_GUARD_COPY: the native guard retains the full same provenance envelope");

        await using var connection = new FakeConnection();
        await using var adapter = await DesktopAdapter.ConnectForTestAsync(connection,
            new(EnableExternalPeerDelivery: true, MaximumMessageBytes: 32 * 1024));
        var binding = await adapter.DiscoverAsync(thread);
        var accepted = await adapter.SubmitExternalPeerAsync(binding!, Guid.NewGuid(), new string('猫', 4096));
        check(accepted.Outcome == DesktopSubmissionOutcome.Accepted && connection.SubmissionCount == 1,
            "CODEX_NATIVE_UNICODE_FITS: a bounded Unicode message fits after readable quoting without expanding the wire cap");
        var sent = connection.Requests.Single(request => (string?)request["method"] == "thread-follower-start-turn");
        check(DesktopFrames.MaximumFrameBytes == 65536 && DesktopFrames.EncodedLength(sent) <= 65536,
            "CODEX_NATIVE_FRAME_CAP: the actual encoded complete request stays within the existing 64KiB cap");

        string expanded = new('"', 8000);
        var expandedFrame = (JsonObject)sent.DeepClone();
        expandedFrame["params"]!["turnStart"] = NativeExternalPeerInput.CreateTurnStart(thread, Guid.NewGuid(), expanded);
        Console.WriteLine("CODEX_FRAME_BUDGET_SAMPLES: unicode_utf8=12288 unicode_frame=" + DesktopFrames.EncodedLength(sent)
            + " quote_utf8=8000 quote_frame=" + DesktopFrames.EncodedLength(expandedFrame) + " cap=65536; complete same transport envelope");
        check(Encoding.UTF8.GetByteCount(expanded) < 16 * 1024,
            "CODEX_FRAME_BOUNDARY_WITNESS: the escaping case is below both broker-text and adapter raw-byte bounds");
        int before = connection.Requests.Count;
        string? refusal = null;
        try { await adapter.SubmitExternalPeerAsync(binding!, Guid.NewGuid(), expanded); }
        catch (DesktopAdapterException error) { refusal = error.Code; }
        check(refusal == "message_frame_too_large" && connection.Requests.Count == before && connection.SubmissionCount == 1,
            "CODEX_NATIVE_FRAME_PREFLIGHT: encoded overflow refuses before rediscovery, receipt reservation or user input");
    }
}
