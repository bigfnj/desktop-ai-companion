using System.Globalization;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Security;

// N-broker-long-outage-client-recovery-01, M3 (AgentBroker 1.10.6, lane N round 2, BACKLOG.md:89; DECISIONS 132 Q10 and N34,
// DECISIONS 142 A3): one running stdio bridge across a worker restart on one data root. Worker 1 listens on a token-required
// loopback port; the bridge (McpStdioBridge.RunAsync, in process over System.IO.Pipelines) relays tools/list to it; worker 1
// stops; a request during the outage gets the bridge's -32001 answer and the bridge keeps reading its input; worker 2 starts on
// the same data root (same control pipe name, newly drawn loopback port, new token); the same bridge relays the next tools/list.
// This file stops at its first failure: one check per label, in this order, so each killer reports at its own label.
// Killing mutations (exact sets, one label each; the killer list and its first-failure arguments live outside the repo):
// stdio-bridge-exits-on-error at label 2 and stdio-bridge-connection-cached at label 3. Label 1 is a fixture precondition and
// claims no guard.
//
// Token-required on purpose: a token-required connection is the one Claude Code reaches through this bridge
// (AgentClientSetup.cs:83, :88). Every WorkerApplication draws its own token (WorkerApplication.cs:21), so a connection kept from
// worker 1 fails whatever port worker 2 draws: the old port refuses the connect, or, if worker 2 drew it, answers 401 (:172-173).
//
// Timing (N34). During the outage no control pipe server exists, so GetConnectionAsync's pipe connect waits for its own 5 s
// deadline (McpStdioBridge.cs:131-132) and only then is the -32001 answer written (:100-102). Each reply is read with a 15 s bound,
// three times that deadline. A reply that does not arrive in time, the end of the bridge's output or an unparsable line reads as
// null, so a missing answer fails at its own label and never as an unlabelled exception. Label 2 then gives the bridge 1 s to
// exit; a bridge that exits on its error answer does so within milliseconds. Worker 2 starts only after the outage answer was
// read, so that request can never reach worker 2.
//
// Teardown. The hygiene runner grades the first stderr line (verify_failure), and a label's exception reaches stderr only after
// this method's finally has run. So the finally first sends EOF (the bridge's silent exit at :33 and :65), then waits at most 5 s
// for the bridge and swallows whatever it ends with, then disposes the reader and both streams, then the live worker, then deletes
// the identity key. Nothing on the healthy path or a mutant path writes stderr. A tools/list answer carries no tools/call result,
// so this file does not feed AdvisoryKindChecks, whose collector ignores such answers anyway.
internal static class StdioBridgeRestartChecks
{
    private const string FirstWorkerAnswers = "STDIO_BRIDGE_FIRST_WORKER_ANSWERS: the stdio bridge relays tools/list from the first worker over a token-required loopback connection";
    private const string StaysAliveDuringOutage = "STDIO_BRIDGE_STAYS_ALIVE_DURING_OUTAGE: with the worker stopped, a request gets its id-matched -32001 answer and the bridge keeps running";
    private const string RecoversAfterWorkerRestart = "STDIO_BRIDGE_RECOVERS_AFTER_WORKER_RESTART: the same running bridge relays the next request to the restarted worker through its new connection and token";

    private const int NotConfirmedCode = -32001;
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OutageSettle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BridgeStopTimeout = TimeSpan.FromSeconds(5);

    internal static async Task RunAsync(Action<bool, string> check)
    {
        using var scratchOwner = OwnedScratch.Create("stdio-bridge-restart");
        string scratch = scratchOwner.Root;
        Guid brokerId;
        using (var seed = new BrokerStore(Path.Combine(scratch, "store"))) brokerId = seed.BrokerId;
        string keyName = BrokerIdentity.KeyNameFor(scratch, brokerId);
        var options = new WorkerOptions(scratch, "Synthetic stdio restart", LoopbackPort: 0, EnableLan: false,
            AllowUnauthenticatedLoopback: false) { EnableBrokerDiscovery = false };
        var input = new Pipe();
        var output = new Pipe();
        Stream bridgeInput = input.Reader.AsStream();
        Stream bridgeOutput = output.Writer.AsStream();
        var reader = new StreamReader(output.Reader.AsStream(), new UTF8Encoding(false, true));
        WorkerApplication? worker = null;
        Task<int>? bridge = null;
        try
        {
            worker = await WorkerApplication.CreateAsync(options, []);
            await worker.StartAsync();
            bridge = McpStdioBridge.RunAsync(scratch, bridgeInput, bridgeOutput);

            // Label 1: worker 1 answers through the bridge; its tool count is what worker 2 must list at label 3.
            await SendAsync(input, ToolsList(201));
            JsonElement? first = await NextReplyAsync(reader, ReplyTimeout);
            int listed = first is { } firstReply && HasId(firstReply, 201) ? ToolCount(firstReply) : -1;
            check(listed > 0, FirstWorkerAnswers);

            // Label 2: worker 1 stopped (its control pipe, listener and token are gone); the answer comes from the bridge itself.
            await worker.DisposeAsync();
            worker = null;
            await SendAsync(input, ToolsList(202));
            JsonElement? during = await NextReplyAsync(reader, ReplyTimeout);
            await Task.WhenAny(bridge, Task.Delay(OutageSettle));
            check(during is { } duringReply && HasId(duringReply, 202) && ErrorCode(duringReply) == NotConfirmedCode &&
                !bridge.IsCompleted, StaysAliveDuringOutage);

            // Label 3: worker 2 on the same options (same data root and pipe name, a newly drawn port, a new token), same bridge task.
            worker = await WorkerApplication.CreateAsync(options, []);
            await worker.StartAsync();
            await SendAsync(input, ToolsList(203));
            JsonElement? after = await NextReplyAsync(reader, ReplyTimeout);
            check(after is { } afterReply && HasId(afterReply, 203) && ToolCount(afterReply) == listed && !bridge.IsCompleted,
                RecoversAfterWorkerRestart);
        }
        finally
        {
            try { await input.Writer.CompleteAsync(); }
            catch (Exception) { /* EOF is best effort here; the bridge wait below is bounded either way. */ }
            if (bridge is not null)
            {
                try { await bridge.WaitAsync(BridgeStopTimeout); }
                catch (Exception) { /* A bridge that did not stop or ended in an exception never replaces a label's exception. */ }
            }
            try { reader.Dispose(); }
            catch (Exception) { /* The streams below are disposed regardless. */ }
            try { bridgeInput.Dispose(); }
            catch (Exception) { /* As above. */ }
            try { bridgeOutput.Dispose(); }
            catch (Exception) { /* As above. */ }
            if (worker is not null) await worker.DisposeAsync();
            if (CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent))
            { using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent); key.Delete(); }
        }
    }

    private static string ToolsList(int id) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id.ToString(CultureInfo.InvariantCulture) + ",\"method\":\"tools/list\",\"params\":{}}";

    private static async Task SendAsync(Pipe input, string frame) =>
        await input.Writer.WriteAsync(Encoding.UTF8.GetBytes(frame + "\n"));

    // Null on timeout, on the end of the bridge's output or on a line that is not JSON, so a missing answer fails at its label.
    private static async Task<JsonElement?> NextReplyAsync(StreamReader reader, TimeSpan timeout)
    {
        string? line;
        try { line = await reader.ReadLineAsync().WaitAsync(timeout); }
        catch (TimeoutException) { return null; }
        if (line is null) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    private static bool HasId(JsonElement reply, int id) =>
        reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("id", out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int actual) && actual == id;

    // The length of result.tools, or -1 when the reply holds no such array (an error answer included).
    private static int ToolCount(JsonElement reply) =>
        reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("result", out var result) &&
        result.ValueKind == JsonValueKind.Object && result.TryGetProperty("tools", out var tools) &&
        tools.ValueKind == JsonValueKind.Array ? tools.GetArrayLength() : -1;

    // error.code of a JSON-RPC error answer, or null when the reply holds none.
    private static int? ErrorCode(JsonElement reply) =>
        reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("error", out var error) &&
        error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) &&
        code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out int value) ? value : null;
}
