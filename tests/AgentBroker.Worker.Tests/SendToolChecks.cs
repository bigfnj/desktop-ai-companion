using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBroker.Core;
using AgentBroker.Worker;
using ModelContextProtocol;

// send compares only caller-supplied fields on a retry, so a regenerated timestamp can never turn an
// honest retry into message_id_collision, while a changed payload still collides.
internal static class SendToolChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await using (var fixture = new ToolFixture("send-tool"))
        {
            var tools = fixture.Tools;
            var sender = fixture.Register("Synthetic send sender");
            var recipient = fixture.Register("Synthetic send recipient");
            var third = fixture.Register("Synthetic send bystander");
            var defaults = new BrokerLimits();
            Guid id = Guid.NewGuid();
            const string text = "Synthetic stable send.";
            var first = tools.Send(sender.Address, recipient.Address, id, text);
            var message = first.Message;
            check(first.Status == MessageStatus.Accepted && message.ConversationId == id && message.ReplyTo is null &&
                message.RemainingReplies == defaults.MaximumReplies && message.CreatedAt == fixture.Clock.GetUtcNow() &&
                message.ExpiresAt == message.CreatedAt.AddMinutes(defaults.DefaultMessageLifetimeMinutes),
                "send starts a conversation under its message ID with the default lifetime and the full reply budget");
            fixture.Clock.Advance(TimeSpan.FromSeconds(10));
            var retry = Attempt(() => tools.Send(sender.Address, recipient.Address, id, text, ttlMinutes: 5));
            check(retry is not null && retry == first && retry.Message.CreatedAt == message.CreatedAt,
                "a send retry with the same fields returns the identical receipt with the original CreatedAt");
            var named = Attempt(() => tools.Send(sender.Address, recipient.Address, id, text, conversationId: id));
            check(named == first, "WITNESS a retry that names the same conversation recovers the same receipt");
            check(ToolFixture.Refused(() => tools.Send(sender.Address, recipient.Address, id, "Changed synthetic text."), "message_id_collision") &&
                fixture.Store.FindReceipt(id) == first,
                "a send retry with changed text collides and leaves the original receipt intact");
            check(ToolFixture.Refused(() => tools.Send(sender.Address, third.Address, id, text), "message_id_collision") &&
                ToolFixture.Refused(() => tools.Send(third.Address, recipient.Address, id, text), "message_id_collision") &&
                ToolFixture.Refused(() => tools.Send(sender.Address, recipient.Address, id, text, conversationId: Guid.NewGuid()), "message_id_collision"),
                "a send retry with a different recipient sender or conversation collides");
            Guid replyId = Guid.NewGuid();
            tools.ReplyToMessage(id, replyId, "Synthetic reply text.");
            check(ToolFixture.Refused(() => tools.Send(recipient.Address, sender.Address, replyId, "Synthetic reply text."), "message_id_collision"),
                "send cannot recover a reply's message ID as a new conversation");

            var shortLived = tools.Send(sender.Address, recipient.Address, Guid.NewGuid(), "Synthetic short lifetime.", ttlMinutes: 30).Message;
            var capped = tools.Send(sender.Address, recipient.Address, Guid.NewGuid(), "Synthetic capped lifetime.", ttlMinutes: 5000).Message;
            check(shortLived.ExpiresAt - shortLived.CreatedAt == TimeSpan.FromMinutes(30) &&
                capped.ExpiresAt - capped.CreatedAt == BrokerLimits.MaximumLifetime,
                "send honours a requested lifetime and caps it at 24 hours");
            check(ToolFixture.Refused(() => tools.Send(sender.Address, recipient.Address, Guid.NewGuid(), text, ttlMinutes: -1),
                    "ttlMinutes must be 0 (the default lifetime) or a positive number of minutes.") &&
                ToolFixture.Refused(() => tools.Send(sender.Address, recipient.Address, Guid.NewGuid(), " "), "invalid_message") &&
                ToolFixture.Refused(() => tools.Send(sender.Address, recipient.Address, Guid.NewGuid(), new string('x', 17 * 1024)), "invalid_message") &&
                ToolFixture.Refused(() => tools.Send(sender.Address, recipient.Address, Guid.Empty, text), "invalid_message"),
                "send refuses a negative lifetime, blank or oversized text and an empty message ID");

            fixture.Clock.Advance(TimeSpan.FromSeconds(10));
            var recipientLease = fixture.Current(recipient.Address)!.LeaseUntil;
            tools.Send(sender.Address, recipient.Address, Guid.NewGuid(), "Synthetic renewal send.");
            check(fixture.Current(sender.Address)!.LeaseUntil == fixture.Clock.GetUtcNow().AddMinutes(1) &&
                fixture.Current(recipient.Address)!.LeaseUntil == recipientLease,
                "a successful send renews only its exact sender");
        }
        await HttpRetryAsync(check);
    }

    private static MessageReceipt? Attempt(Func<MessageReceipt> action)
    {
        try { return action(); }
        catch (McpException) { return null; }
    }

    // The real HTTP tool schema, the real clock: a retry after time passes recovers the stored receipt.
    private static async Task HttpRetryAsync(Action<bool, string> check)
    {
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "send-http-" + Guid.NewGuid().ToString("N"));
        Guid brokerId;
        using (var seed = new BrokerStore(Path.Combine(scratch, "store"))) brokerId = seed.BrokerId;
        string keyName = AgentBroker.Worker.Security.BrokerIdentity.KeyNameFor(scratch, brokerId);
        try
        {
            await using var worker = await WorkerApplication.CreateAsync(new(scratch, "Synthetic send HTTP test", LoopbackPort: 0,
                EnableLan: false) { EnableBrokerDiscovery = false }, []);
            await worker.StartAsync();
            string endpoint = worker.Application.Urls.Single(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
            using var client = new HttpClient { BaseAddress = new Uri(endpoint + "/mcp"), Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            var sender = worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic HTTP send sender", false);
            var recipient = worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic HTTP send recipient", false);
            Guid id = Guid.NewGuid();
            var first = await Call(client, sender.Address, recipient.Address, id, "Stable HTTP send.");
            var stored = worker.Runtime.Store.FindReceipt(id);
            await Task.Delay(25);
            var second = await Call(client, sender.Address, recipient.Address, id, "Stable HTTP send.");
            // The compact receipt carries readAt, the broker's time of the read (lane broker-163), which a retry later in time
            // must move; everything else is the stored receipt's and must not.
            static System.Text.Json.Nodes.JsonNode? Unread(string? receipt)
            {
                var node = receipt is null ? null : System.Text.Json.Nodes.JsonNode.Parse(receipt);
                node?.AsObject().Remove("readAt");
                return node;
            }
            check(first is not null && second is not null && System.Text.Json.Nodes.JsonNode.DeepEquals(Unread(first), Unread(second)) &&
                first != second && stored is not null && worker.Runtime.Store.FindReceipt(id) == stored,
                "an HTTP send retry after wall-clock time advances returns the stored receipt unchanged, its read time aside");
            check(await Call(client, sender.Address, recipient.Address, id, "Conflicting HTTP send.") is null &&
                worker.Runtime.Store.FindReceipt(id) == stored,
                "an HTTP send retry with changed text returns a tool error and leaves the receipt intact");
        }
        finally
        {
            if (CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent))
            { using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent); key.Delete(); }
        }
    }

    // Returns the receipt text, or null for a tool error.
    private static async Task<string?> Call(HttpClient client, SessionAddress sender, SessionAddress recipient, Guid messageId, string text)
    {
        using var response = await client.PostAsJsonAsync("", new
        {
            jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method = "tools/call",
            @params = new { name = "send", arguments = new { sender, recipient, messageId, text } },
        });
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = string.Join("\n", body.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => line[6..]));
        using var result = JsonDocument.Parse(body);
        if (!result.RootElement.TryGetProperty("result", out var tool) ||
            tool.TryGetProperty("isError", out var error) && error.GetBoolean()) return null;
        return tool.GetProperty("content").EnumerateArray().First(item => item.GetProperty("type").GetString() == "text").GetProperty("text").GetString();
    }
}
