using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBroker.Core;
using AgentBroker.Worker;

// N-agentbroker-send-advisory-01: a sender to a generic mailbox was not told that nothing can wake it. Through the real HTTP
// MCP endpoint, send now adds a second text block for such a recipient and keeps the receipt as the first block, in the shape
// send_message returns. Each label names the rule a mutation of BrokerTools.SendTool or PollingOnly must break.
internal static class SendAdvisoryChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "send-advisory-" + Guid.NewGuid().ToString("N"));
        Guid brokerId;
        using (var seed = new BrokerStore(Path.Combine(scratch, "store"))) brokerId = seed.BrokerId;
        string keyName = AgentBroker.Worker.Security.BrokerIdentity.KeyNameFor(scratch, brokerId);
        try
        {
            await using var worker = await WorkerApplication.CreateAsync(new(scratch, "Synthetic send advisory test", LoopbackPort: 0,
                EnableLan: false) { EnableBrokerDiscovery = false }, []);
            await worker.StartAsync();
            string endpoint = worker.Application.Urls.Single(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
            using var client = new HttpClient { BaseAddress = new Uri(endpoint + "/mcp"), Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            await Rpc(client, "initialize", new
            { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "synthetic-send-advisory", version = "1" } });

            var sender = worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic advisory sender", false);
            var mailbox = worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic advisory mailbox", false);
            // Busy, so the started runtime leaves the message queued instead of trying an adapter that does not exist.
            var wakeable = new BrokerSession(new(worker.Runtime.Store.BrokerId, "codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid()),
                "Synthetic wakeable session", "synthetic", SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply,
                SessionActivity.Busy, DateTimeOffset.UtcNow.AddMinutes(1));
            worker.Runtime.Store.Register(wakeable);
            async Task<JsonElement> Send(BrokerSession to) => (await Rpc(client, "tools/call", new
            {
                name = "send",
                arguments = new { sender = sender.Address, recipient = to.Address, messageId = Guid.NewGuid(), text = "Synthetic advisory text." }
            })).GetProperty("result").GetProperty("content");

            var toMailbox = await Send(mailbox);
            check(toMailbox.GetArrayLength() == 2 && toMailbox[1].GetProperty("text").GetString() == BrokerTools.SendAdvisory,
                "SEND_ADVISORY_MAILBOX: a send to a generic mailbox returns a second block saying nothing can wake it");
            var toWakeable = await Send(wakeable);
            check(toWakeable.GetArrayLength() == 1, "SEND_ADVISORY_WAKEABLE: a send to a session with WakeIdle returns the receipt alone");
            // A Codex row whose engine check could not complete lists Receive alone (N-broker-next-02); it is no mailbox.
            var pendingNative = new BrokerSession(new(worker.Runtime.Store.BrokerId, "codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid()),
                "Synthetic pending Codex row", "codex-native", SessionCapabilities.Receive, SessionActivity.Unknown, DateTimeOffset.UtcNow.AddMinutes(1));
            worker.Runtime.Store.Register(pendingNative);
            var toPending = await Send(pendingNative);
            check(toPending.GetArrayLength() == 1,
                "SEND_ADVISORY_PENDING_NATIVE: a native row listed with Receive alone gets the receipt alone, never the mailbox advisory (" +
                toPending.GetArrayLength() + " blocks)");

            var now = DateTimeOffset.UtcNow;
            var legacy = (await Rpc(client, "tools/call", new
            {
                name = "send_message",
                arguments = new
                {
                    message = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, wakeable.Address,
                        "Synthetic legacy text.", now, now.AddMinutes(5))
                }
            })).GetProperty("result").GetProperty("content");
            using var sent = JsonDocument.Parse(toMailbox[0].GetProperty("text").GetString()!);
            using var classic = JsonDocument.Parse(legacy[0].GetProperty("text").GetString()!);
            check(Shape(sent.RootElement) == Shape(classic.RootElement) && sent.RootElement.TryGetProperty("message", out var sentMessage) &&
                sentMessage.GetProperty("recipient").GetProperty("sessionId").GetString() == mailbox.Address.SessionId,
                "SEND_ADVISORY_SHAPE: send's first block is the receipt, in exactly the shape send_message returns");
        }
        finally
        {
            if (CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent))
            { using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent); key.Delete(); }
        }
    }

    // Property names of the receipt and of its message, sorted: the shape a client parses, without the values.
    private static string Shape(JsonElement receipt) =>
        string.Join(",", receipt.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)) + "|" +
        (receipt.TryGetProperty("message", out var message)
            ? string.Join(",", message.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)) : "no message");

    private static async Task<JsonElement> Rpc(HttpClient client, string method, object parameters)
    {
        using var response = await client.PostAsJsonAsync("", new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = parameters });
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = string.Join("\n", body.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => line[6..]));
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("result", out var result) && result.TryGetProperty("isError", out var isError) && isError.GetBoolean())
            throw new InvalidOperationException("Synthetic MCP call failed: " + result.GetRawText());
        return document.RootElement.Clone();
    }
}
