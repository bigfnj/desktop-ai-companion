using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBroker.Core;
using AgentBroker.Worker;

internal static class ReplyToolChecks
{
    internal static async Task RunStandaloneAsync(Action<bool, string> check)
    {
        using var scratchOwner = OwnedScratch.Create("reply-http");
        string scratch = scratchOwner.Root;
        var clock = new ToolFixture.ManualClock();
        Guid brokerId;
        using (var seed = new BrokerStore(Path.Combine(scratch, "store"), clock: clock)) brokerId = seed.BrokerId;
        string keyName = AgentBroker.Worker.Security.BrokerIdentity.KeyNameFor(scratch, brokerId);
        try
        {
            await using var worker = await WorkerApplication.CreateAsync(new(scratch, "Synthetic reply HTTP test", LoopbackPort: 0,
                EnableLan: false, AllowUnauthenticatedLoopback: false) { EnableBrokerDiscovery = false }, [], clock);
            // Exercise real HTTP middleware, authentication, tools and store without the background delivery/discovery
            // loops: their policy-refusal pass must not race this fixture's deliberate Receive-capability revocation.
            await worker.Application.StartAsync();
            string endpoint = worker.Application.Urls.Single(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
            using var client = new HttpClient { BaseAddress = new Uri(endpoint + "/mcp"), Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", worker.LocalAccessToken);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            using (var initialized = await client.PostAsJsonAsync("", new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "synthetic-reply-test", version = "1" } }
            })) initialized.EnsureSuccessStatusCode();
            await RunAsync(worker, client, clock, check);
        }
        finally
        {
            if (CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent))
            { using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent); key.Delete(); }
        }
        await RunRuntimeAsync(check);
    }

    internal static async Task RunRuntimeAsync(Action<bool, string> check)
    {
        using var scratchOwner = OwnedScratch.Create("reply-http-runtime");
        string scratch = scratchOwner.Root;
        Guid brokerId;
        using (var seed = new BrokerStore(Path.Combine(scratch, "store"))) brokerId = seed.BrokerId;
        string keyName = AgentBroker.Worker.Security.BrokerIdentity.KeyNameFor(scratch, brokerId);
        var adapter = new RuntimeReplyAdapter();
        try
        {
            await using var worker = await WorkerApplication.CreateAsync(new(scratch, "Synthetic runtime reply HTTP test", LoopbackPort: 0,
                EnableLan: false, AllowUnauthenticatedLoopback: false) { EnableBrokerDiscovery = false }, [adapter]);
            // Start the actual discovery, receipt and delivery loops. Only the adapter boundary is synthetic.
            await worker.StartAsync();
            check(await WaitUntilAsync(() => adapter.Discoveries > 0 && worker.Runtime.Store.Directory().Any(session =>
                    session.Address == adapter.Address(brokerId) && session.Adapter == adapter.Id)),
                "REPLY_HTTP_RUNTIME_DISCOVERY: a running worker loop discovers the exact synthetic recipient");
            var sender = worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic runtime reply sender", false);
            var now = DateTimeOffset.UtcNow;
            var parent = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, adapter.Address(brokerId),
                "Synthetic original submitted by the runtime reply regression.", now, now.AddMinutes(1));
            worker.Runtime.Store.Accept(parent, brokerId);
            bool submitted = await WaitUntilAsync(() => worker.Runtime.Store.FindReceipt(parent.Id)?.Status == MessageStatus.Submitted);
            check(submitted && adapter.Submissions == 1 && adapter.SubmittedMessage == parent &&
                worker.Runtime.Store.FindReceipt(parent.Id) is { Status: MessageStatus.Submitted, Reason: "synthetic_reply_admission", Attempts: 1 } &&
                worker.Runtime.Store.ReadPendingInbox(parent.Recipient).PendingCount == 1,
                "REPLY_HTTP_RUNTIME_SUBMITTED: the running delivery loop submits the exact parent once and leaves recipient acknowledgement pending");

            string endpoint = worker.Application.Urls.Single(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
            using var client = new HttpClient { BaseAddress = new Uri(endpoint + "/mcp"), Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", worker.LocalAccessToken);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            using (var initialized = await client.PostAsJsonAsync("", new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "synthetic-runtime-reply-test", version = "1" } }
            })) initialized.EnsureSuccessStatusCode();
            Guid replyId = Guid.NewGuid();
            check(await Reply(client, parent.Id, replyId, "The runtime-submitted task is complete.") &&
                worker.Runtime.Store.FindReceipt(replyId)?.Message is { } reply && reply.ReplyTo == parent.Id &&
                reply.Sender == parent.Recipient && reply.Recipient == parent.Sender && reply.ConversationId == parent.ConversationId,
                "REPLY_HTTP_RUNTIME_REPLY: authenticated MCP HTTP admits the exact correlated reply while background loops remain active");
            check(worker.Runtime.Store.FindReceipt(parent.Id) is { Status: MessageStatus.Delivered, Reason: "reply_receipt", Attempts: 1 } settled &&
                settled.Message == parent with { Text = "" } && worker.Runtime.Store.ReadPendingInbox(parent.Recipient).PendingCount == 0 &&
                adapter.Submissions == 1,
                "REPLY_HTTP_RUNTIME_SETTLED: an actual HTTP reply settles the runtime-submitted parent as Delivered/reply_receipt and clears pending mail without resubmission");
        }
        finally
        {
            if (CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent))
            { using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent); key.Delete(); }
        }
    }

    private static async Task RunAsync(WorkerApplication worker, HttpClient client, ToolFixture.ManualClock clock, Action<bool, string> check)
    {
        check(worker.Runtime.StoreNow == clock.GetUtcNow(),
            "REPLY_HTTP_RUNTIME_CLOCK: the HTTP fixture runtime uses the controlled clock before any registration");
        BrokerSession? registeredSender = null, registeredRecipient = null;
        try
        {
            registeredSender = worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic reply sender", false);
            registeredRecipient = worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic reply recipient", false);
        }
        catch (BrokerException error) when (error.Code == "invalid_lease") { }
        check(registeredSender is not null && registeredRecipient is not null,
            "REPLY_HTTP_STORE_CLOCK: the store admits leases from the same controlled clock as its runtime");
        var sender = registeredSender!;
        var recipient = registeredRecipient!;
        var now = clock.GetUtcNow();
        var parent = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, recipient.Address,
            "Synthetic original for the HTTP reply regression.", now, now.AddMinutes(1));
        worker.Runtime.Store.Accept(parent, worker.Runtime.Store.BrokerId);
        Guid replyId = Guid.NewGuid();
        check(await Reply(client, parent.Id, replyId, "Stable HTTP reply."), "reply_to_message admits a reply through the actual authenticated MCP HTTP schema");
        var firstParent = worker.Runtime.Store.FindReceipt(parent.Id)!;
        check(firstParent is { Status: MessageStatus.Delivered, Reason: "reply_receipt", Attempts: 1 } &&
            firstParent.Message == parent with { Text = "" } && worker.Runtime.Store.ReadPendingInbox(recipient.Address).PendingCount == 0,
            "REPLY_HTTP_PARENT_RECEIPT: actual authenticated reply settles its eligible local parent and removes pending mail");
        var first = worker.Runtime.Store.FindReceipt(replyId)!;
        clock.Advance(TimeSpan.FromMilliseconds(25));
        check(await Reply(client, parent.Id, replyId, "Stable HTTP reply."), "same-ID HTTP reply retry succeeds after the controlled clock advances");
        check(worker.Runtime.Store.FindReceipt(replyId) == first,
            "HTTP reply retry preserves the originally persisted creation and expiry timestamps");
        check(worker.Runtime.Store.FindReceipt(parent.Id) == firstParent,
            "REPLY_HTTP_PARENT_RETRY: an exact HTTP retry does not repeat or refresh the parent's acknowledgement");
        check(!await Reply(client, parent.Id, replyId, "Conflicting HTTP reply text."),
            "HTTP reply retry with changed text returns a real tool error instead of silently replacing the reply");
        check(worker.Runtime.Store.FindReceipt(replyId) == first, "conflicting HTTP reply retry leaves the accepted reply intact");
        Guid simultaneousId = Guid.NewGuid();
        bool[] admitted = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Reply(client, parent.Id, simultaneousId, "Concurrent HTTP reply.")));
        check(admitted.All(value => value), "concurrent actual MCP reply calls share one stable-ID admission without collision errors");
        check(worker.Runtime.Store.Messages(sender.Address).Count(receipt => receipt.Message.Id == simultaneousId) == 1,
            "concurrent HTTP reply retries persist exactly one correlated reply message");
        var rightsParent = parent with { Id = Guid.NewGuid(), CreatedAt = clock.GetUtcNow(), ExpiresAt = clock.GetUtcNow().AddMinutes(1) };
        worker.Runtime.Store.Accept(rightsParent, worker.Runtime.Store.BrokerId);
        worker.Runtime.Store.Register(recipient with { Capabilities = SessionCapabilities.Reply });
        check(await Reply(client, rightsParent.Id, Guid.NewGuid(), "A Reply-only HTTP sender keeps its existing permission.") &&
            worker.Runtime.Store.FindReceipt(rightsParent.Id) is { Status: MessageStatus.Accepted } unacknowledged &&
            unacknowledged.Message == rightsParent,
            "REPLY_HTTP_RECEIVE_SKIP: Reply-only HTTP success does not acquire implicit Receive acknowledgement rights");
        worker.Runtime.Store.Register(recipient with { LeaseUntil = clock.GetUtcNow().AddMinutes(1) });
        var beforeRefused = worker.Runtime.Store.FindReceipt(rightsParent.Id);
        Guid refusedId = Guid.NewGuid();
        check(!await Reply(client, rightsParent.Id, refusedId, "") && worker.Runtime.Store.FindReceipt(rightsParent.Id) == beforeRefused &&
            worker.Runtime.Store.FindReceipt(refusedId) is null,
            "REPLY_HTTP_REFUSAL_NO_ACK: invalid HTTP reply admission neither acknowledges the parent nor publishes a reply");
        var deliveredParent = parent with { Id = Guid.NewGuid(), CreatedAt = clock.GetUtcNow(), ExpiresAt = clock.GetUtcNow().AddSeconds(1) };
        worker.Runtime.Store.Accept(deliveredParent, worker.Runtime.Store.BrokerId);
        var delivered = worker.Runtime.Store.RecordRecipientReceipt(deliveredParent.Id, recipient.Address, worker.Runtime.Store.BrokerId, MessageStatus.Delivered);
        check(delivered.Status == MessageStatus.Delivered && delivered.UpdatedAt == clock.GetUtcNow() && clock.GetUtcNow() < deliveredParent.ExpiresAt,
            "REPLY_HTTP_DELIVERED_BEFORE_ADVANCE: both durable commits finish as Delivered before the controlled deadline advances");
        clock.Advance(TimeSpan.FromSeconds(2));
        check(worker.Runtime.StoreNow == clock.GetUtcNow() && clock.GetUtcNow() > deliveredParent.ExpiresAt,
            "REPLY_HTTP_AFTER_DEADLINE: the actual HTTP late reply runs only after its delivered parent's original deadline");
        Guid lateReplyId = Guid.NewGuid();
        check(await Reply(client, deliveredParent.Id, lateReplyId, "Finished the previously delivered task."),
            "actual HTTP reply tool permits finishing a delivered task after its original delivery deadline");
        var lateReply = worker.Runtime.Store.FindReceipt(lateReplyId)!.Message;
        check(lateReply.CreatedAt == clock.GetUtcNow() && lateReply.CreatedAt > deliveredParent.ExpiresAt && lateReply.ExpiresAt > clock.GetUtcNow() &&
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
        AdvisoryKindChecks.ObserveResponse(result.RootElement);
        if (!result.RootElement.TryGetProperty("result", out var tool)) return false;
        return !tool.TryGetProperty("isError", out var error) || !error.GetBoolean();
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
        return condition();
    }

    private sealed class RuntimeReplyAdapter : ILocalAgentAdapter
    {
        public string Id => "synthetic-reply-runtime";
        private readonly Guid incarnation = Guid.NewGuid();
        private int discoveries, submissions;
        private BrokerMessage? submittedMessage;
        internal int Discoveries => Volatile.Read(ref discoveries);
        internal int Submissions => Volatile.Read(ref submissions);
        internal BrokerMessage? SubmittedMessage => Volatile.Read(ref submittedMessage);
        internal SessionAddress Address(Guid brokerId) => new(brokerId, "synthetic-reply-runtime-recipient", incarnation);
        public Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token)
        {
            Interlocked.Increment(ref discoveries);
            return Task.FromResult<IReadOnlyList<BrokerSession>>([new(Address(brokerId), "Synthetic runtime reply recipient", Id,
                SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply,
                SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(1))]);
        }
        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token)
        {
            Volatile.Write(ref submittedMessage, message);
            Interlocked.Increment(ref submissions);
            return Task.FromResult(new AdapterResult(AdapterAdmission.Submitted, "synthetic_reply_admission"));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
