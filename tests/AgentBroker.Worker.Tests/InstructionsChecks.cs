using System.IO.Pipelines;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBroker.Core;
using AgentBroker.Worker;

// The protocol card must reach every client in the initialize result: HTTP clients directly, Codex
// through the stdio bridge, which forwards initialize verbatim. The new tools' schemas are pinned too.
internal static class InstructionsChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        check(BrokerInstructions.Text.Length is > 0 and <= BrokerInstructions.MaximumLength,
            "the server instructions card stays within 1,800 characters");
        // First contact (2026-10-29): the identity line comes right after the opening sentence, before any flow.
        string[] lines = BrokerInstructions.Text.Split('\n');
        check(lines.Length > 2 && lines[1].StartsWith("Your address: call whoami first.", StringComparison.Ordinal) &&
            lines[1].Contains("do not register a mailbox or poll", StringComparison.Ordinal),
            "CARD_WHOAMI_FIRST: the card's first instruction is whoami, and a native row means no mailbox and no polling");
        check(BrokerInstructions.Text.Contains("on recipient_unavailable, list_sessions again and resend", StringComparison.Ordinal) &&
            BrokerInstructions.Text.Contains("at remainingReplies 0, continue with send and the same conversationId", StringComparison.Ordinal),
            "CARD_CHURN_AND_BUDGET: the card says how to recover from a changed incarnation and from a spent reply budget");
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "instructions-" + Guid.NewGuid().ToString("N"));
        Guid brokerId;
        using (var seed = new BrokerStore(Path.Combine(scratch, "store"))) brokerId = seed.BrokerId;
        string keyName = AgentBroker.Worker.Security.BrokerIdentity.KeyNameFor(scratch, brokerId);
        try
        {
            await using var worker = await WorkerApplication.CreateAsync(new(scratch, "Synthetic instructions test", LoopbackPort: 0,
                EnableLan: false) { EnableBrokerDiscovery = false }, []);
            await worker.StartAsync();
            string endpoint = worker.Application.Urls.Single(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
            using var client = new HttpClient { BaseAddress = new Uri(endpoint + "/mcp"), Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            var initialized = await Rpc(client, "initialize", new
            { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "synthetic-instructions", version = "1" } });
            check(initialized.GetProperty("result").TryGetProperty("instructions", out var http) && http.GetString() == BrokerInstructions.Text,
                "the instructions card is in the initialize result over HTTP");

            var input = new Pipe();
            var output = new Pipe();
            using (var bridgeInput = input.Reader.AsStream())
            using (var bridgeOutput = output.Writer.AsStream())
            using (var reader = new StreamReader(output.Reader.AsStream(), new UTF8Encoding(false, true)))
            {
                Task<int> bridge = McpStdioBridge.RunAsync(scratch, bridgeInput, bridgeOutput);
                await input.Writer.WriteAsync(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":41,\"method\":\"initialize\",\"params\":" +
                    "{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"synthetic-stdio-instructions\",\"version\":\"1\"}}}\n"));
                string line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) ?? throw new IOException("Missing stdio initialize response.");
                using var reply = JsonDocument.Parse(line);
                check(reply.RootElement.GetProperty("id").GetInt32() == 41 &&
                    reply.RootElement.GetProperty("result").TryGetProperty("instructions", out var stdio) && stdio.GetString() == BrokerInstructions.Text,
                    "the instructions card is in the initialize result over the stdio bridge");
                await input.Writer.CompleteAsync();
                check(await bridge.WaitAsync(TimeSpan.FromSeconds(5)) == 0, "the instructions stdio bridge shuts down cleanly at EOF");
            }

            var tools = (await Rpc(client, "tools/list", new { })).GetProperty("result").GetProperty("tools").EnumerateArray()
                .ToDictionary(tool => tool.GetProperty("name").GetString()!, tool => tool.GetProperty("inputSchema").Clone());
            static (string[] Properties, string[] Required) Shape(JsonElement schema) => (
                schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray(),
                schema.TryGetProperty("required", out var required)
                    ? required.EnumerateArray().Select(name => name.GetString()!).Order(StringComparer.Ordinal).ToArray() : []);
            var send = Shape(tools["send"]);
            var messages = Shape(tools["wait_for_messages"]);
            var replyShape = Shape(tools["wait_for_reply"]);
            var whoami = Shape(tools["whoami"]);
            check(whoami.Properties.Length == 0 && whoami.Required.Length == 0, "the whoami tool schema takes no parameters");
            check(send.Properties.SequenceEqual(new[] { "conversationId", "messageId", "recipient", "sender", "text", "ttlMinutes" }) &&
                send.Required.SequenceEqual(new[] { "messageId", "recipient", "sender", "text" }) &&
                messages.Properties.SequenceEqual(new[] { "limit", "session", "timeoutSeconds" }) && messages.Required.SequenceEqual(new[] { "session" }) &&
                replyShape.Properties.SequenceEqual(new[] { "messageId", "timeoutSeconds" }) && replyShape.Required.SequenceEqual(new[] { "messageId" }),
                "the send and wait tool schemas publish exactly their parameters and required fields");
        }
        finally
        {
            if (CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent))
            { using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent); key.Delete(); }
        }
    }

    private static async Task<JsonElement> Rpc(HttpClient client, string method, object parameters)
    {
        using var response = await client.PostAsJsonAsync("", new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = parameters });
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = string.Join("\n", body.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => line[6..]));
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
