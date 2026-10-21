using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBroker.Core;
using AgentBroker.Worker;

internal static class ReplyToolChecks
{
    internal static async Task RunStandaloneAsync(Action<bool, string> check)
    {
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "reply-http-" + Guid.NewGuid().ToString("N"));
        Guid brokerId;
        using (var seed = new BrokerStore(Path.Combine(scratch, "store"))) brokerId = seed.BrokerId;
        string keyName = AgentBroker.Worker.Security.BrokerIdentity.KeyNameFor(scratch, brokerId);
        try
        {
            await using var worker = await WorkerApplication.CreateAsync(new(scratch, "Synthetic reply HTTP test", LoopbackPort: 0,
                EnableLan: false) { EnableBrokerDiscovery = false }, []);
            await worker.StartAsync();
            string endpoint = worker.Application.Urls.Single(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
            using var client = new HttpClient { BaseAddress = new Uri(endpoint + "/mcp"), Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            using (var initialized = await client.PostAsJsonAsync("", new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "synthetic-reply-test", version = "1" } }
            })) initialized.EnsureSuccessStatusCode();
            await RunAsync(worker, client, check);
        }
        finally
        {
            if (CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent))
            { using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent); key.Delete(); }
        }
    }

    internal static async Task RunAsync(WorkerApplication worker, HttpClient client, Action<bool, string> check)
    {
        var sender = worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic reply sender", false);
        var recipient = worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic reply recipient", false);
        var now = DateTimeOffset.UtcNow;
        var parent = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, recipient.Address,
            "Synthetic original for the HTTP reply regression.", now, now.AddMinutes(1));
        worker.Runtime.Store.Accept(parent, worker.Runtime.Store.BrokerId);
        Guid replyId = Guid.NewGuid();
        check(await Reply(client, parent.Id, replyId, "Stable HTTP reply."), "reply_to_message admits a reply through the actual authenticated MCP HTTP schema");
        var first = worker.Runtime.Store.FindReceipt(replyId)!;
        await Task.Delay(25);
        check(await Reply(client, parent.Id, replyId, "Stable HTTP reply."), "same-ID HTTP reply retry succeeds after wall-clock time advances");
        check(worker.Runtime.Store.FindReceipt(replyId) == first,
            "HTTP reply retry preserves the originally persisted creation and expiry timestamps");
        check(!await Reply(client, parent.Id, replyId, "Conflicting HTTP reply text."),
            "HTTP reply retry with changed text returns a real tool error instead of silently replacing the reply");
        check(worker.Runtime.Store.FindReceipt(replyId) == first, "conflicting HTTP reply retry leaves the accepted reply intact");
        Guid simultaneousId = Guid.NewGuid();
        bool[] admitted = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Reply(client, parent.Id, simultaneousId, "Concurrent HTTP reply.")));
        check(admitted.All(value => value), "concurrent actual MCP reply calls share one stable-ID admission without collision errors");
        check(worker.Runtime.Store.Messages(sender.Address).Count(receipt => receipt.Message.Id == simultaneousId) == 1,
            "concurrent HTTP reply retries persist exactly one correlated reply message");
        var deliveredParent = parent with { Id = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(1) };
        worker.Runtime.Store.Accept(deliveredParent, worker.Runtime.Store.BrokerId);
        worker.Runtime.Store.RecordRecipientReceipt(deliveredParent.Id, recipient.Address, worker.Runtime.Store.BrokerId, MessageStatus.Delivered);
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        Guid lateReplyId = Guid.NewGuid();
        check(await Reply(client, deliveredParent.Id, lateReplyId, "Finished the previously delivered task."),
            "actual HTTP reply tool permits finishing a delivered task after its original delivery deadline");
        var lateReply = worker.Runtime.Store.FindReceipt(lateReplyId)!.Message;
        check(lateReply.CreatedAt > deliveredParent.ExpiresAt && lateReply.ExpiresAt > DateTimeOffset.UtcNow &&
            lateReply.RemainingReplies == deliveredParent.RemainingReplies - 1,
            "late HTTP task reply receives its own valid delivery deadline and decremented reply budget");
    }

    private static async Task<bool> Reply(HttpClient client, Guid parent, Guid replyId, string text)
    {
        using var response = await client.PostAsJsonAsync("", new
        {
            jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method = "tools/call",
            @params = new { name = "reply_to_message", arguments = new { originalMessageId = parent, messageId = replyId, text } }
        });
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = string.Join("\n", body.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => line[6..]));
        using var result = JsonDocument.Parse(body);
        if (!result.RootElement.TryGetProperty("result", out var tool)) return false;
        return !tool.TryGetProperty("isError", out var error) || !error.GetBoolean();
    }
}
