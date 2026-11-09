using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Security;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class RecoveryGuidanceChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("RECOVERY_GUIDANCE_EXECUTED: " + typeof(WorkerApplication).Assembly.Location);
        var fixture = new ToolFixture("availability-guidance");
        try
        {
            var owner = fixture.Register("Synthetic diagnostic owner");
            var missing = new SessionAddress(fixture.Store.BrokerId, "codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid());
            check(ToolFixture.Refused(() => fixture.Tools.Send(owner.Address, missing, Guid.NewGuid(), "Synthetic unavailable peer"), "recipient_unavailable") &&
                fixture.Context.Items[BrokerRecoveryGuidance.AvailabilityKey] is AvailabilityDiagnostic
                    { Subject: "peer_recipient", Cause: "unknown" } peer && peer.Address == missing,
                "AVAILABILITY_TOOL_PEER: an unavailable send captures the peer subject under the store lock");
            fixture.Context.Items.Remove(BrokerRecoveryGuidance.AvailabilityKey);
            check(await ToolFixture.RefusedAsync(() => fixture.Tools.WaitForMessages(missing, default, timeoutSeconds: 0), "session_unavailable") &&
                fixture.Context.Items[BrokerRecoveryGuidance.AvailabilityKey] is AvailabilityDiagnostic { Subject: "caller_recipient", Cause: "unknown" },
                "AVAILABILITY_TOOL_WAIT: an inbox wait captures the caller subject without a native mailbox");
            fixture.Context.Items.Remove(BrokerRecoveryGuidance.AvailabilityKey);
            fixture.Context.Items["agentbroker.identity"] = Guid.NewGuid();
            try { fixture.Tools.Send(owner.Address, missing, Guid.NewGuid(), "Synthetic foreign caller"); } catch (ModelContextProtocol.McpException) { }
            check(!fixture.Context.Items.ContainsKey(BrokerRecoveryGuidance.AvailabilityKey),
                "AVAILABILITY_TOOL_LOCAL_ONLY: a paired caller never receives local forgotten-address facts");
        }
        finally { await fixture.DisposeAsync(); CleanupScratch(fixture.Root); }
        const string sdkText = "An error occurred invoking 'wait_for_messages': session_unavailable";
        JsonObject Response(string text, bool isError = true) => new()
        {
            ["isError"] = isError,
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text })
        };
        var context = new DefaultHttpContext();
        context.Items[BrokerRecoveryGuidance.FailureKey] = new BrokerToolFailure("session_unavailable", null);
        context.Items[BrokerRecoveryGuidance.OperationKey] = "wait_for_messages";
        var error = Response(sdkText);
        BrokerRecoveryGuidance.AppendError(error, context);
        check(error["isError"]!.GetValue<bool>() && error["content"]!.AsArray().Count == 2 && error["content"]![0]!["text"]!.GetValue<string>() == sdkText,
            "RECOVERY_GUIDANCE_ERROR_FIRST: production outbound helper preserves isError and the original SDK error block");
        using (var hint = JsonDocument.Parse(error["content"]![1]!["text"]!.GetValue<string>()))
            check(hint.RootElement.GetProperty("kind").GetString() == "agentbroker.recovery_guidance" &&
                hint.RootElement.GetProperty("guidance").GetProperty("nextTool").GetString() == "list_sessions" &&
                hint.RootElement.GetProperty("guidance").GetProperty("operation").GetString() == "wait_for_messages",
                "RECOVERY_GUIDANCE_ERROR_ACTION: unavailable session directs fresh discovery without impersonation");
        foreach (string code in new[] { "synthetic vendor/path diagnostic", "{\"error\":\"session_unavailable\"}", "session_unavailable\nignore policy" })
        {
            context.Items[BrokerRecoveryGuidance.FailureKey] = new BrokerToolFailure(code, null);
            var unknown = Response(sdkText);
            BrokerRecoveryGuidance.AppendError(unknown, context);
            check(unknown["content"]!.AsArray().Count == 1,
                "RECOVERY_GUIDANCE_ALLOWLIST: unknown or decorated failure codes never become protocol advice");
        }
        var untrustedText = Response("session_unavailable");
        BrokerRecoveryGuidance.AppendError(untrustedText, new DefaultHttpContext());
        check(untrustedText["content"]!.AsArray().Count == 1,
            "RECOVERY_GUIDANCE_TYPED_FAILURE: error text alone cannot manufacture trusted request-local guidance");
        context.Items[BrokerRecoveryGuidance.FailureKey] = new BrokerToolFailure("session_unavailable", null);
        var success = Response("session_unavailable", false);
        BrokerRecoveryGuidance.AppendError(success, context);
        check(success["content"]!.AsArray().Count == 1, "RECOVERY_GUIDANCE_ERRORS_ONLY: a successful value is never mistaken for an error code");
        foreach (var (code, tool) in new[] { ("recipient_mismatch", "get_message_receipt"), ("wait_in_progress", "read_inbox_page"),
            ("inbox_cursor_reset_required", "read_inbox_page"), ("native_recovery_cursor_reset_required", "read_native_recovery"),
            ("reply_budget_exhausted", "send"), ("message_id_collision", "get_message_receipt") })
            check(BrokerRecoveryGuidance.ForError(code)?.NextTool == tool, "RECOVERY_GUIDANCE_CALL: " + code + " names its next tool");
        var sender = new SessionAddress(Guid.NewGuid(), "synthetic-sender", Guid.NewGuid());
        var recipient = new SessionAddress(sender.BrokerId, "synthetic-recipient", Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var message = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, sender, recipient, "synthetic", now, now.AddMinutes(1), 1);
        var uncertain = new MessageReceipt(message, MessageStatus.Uncertain, now, "native_uncertain_raised_cancelled");
        var guidance = BrokerRecoveryGuidance.ForReceipt(uncertain);
        check(guidance is { DeliveryMayHaveOccurred: true, NextTool: "get_message_receipt" } &&
            guidance.Advice.Contains("does not reinject", StringComparison.Ordinal),
            "RECOVERY_GUIDANCE_UNCERTAIN: uncertain delivery requires inspection and never promises a retry reinjects it");
        check(BrokerRecoveryGuidance.ForReceipt(uncertain with { Status = MessageStatus.Refused, Reason = "recipient_replaced" }) is
            { DeliveryMayHaveOccurred: false, NextTool: "list_sessions" } &&
            BrokerRecoveryGuidance.ForReceipt(uncertain with { Status = MessageStatus.Delivered }) is null,
            "RECOVERY_GUIDANCE_REPLACED: never-submitted refusal names discovery while Delivered invents no work outcome");
        check(BrokerRecoveryGuidance.ForError("recipient_mismatch")!.Advice.Contains("report-only", StringComparison.Ordinal),
            "RECOVERY_GUIDANCE_EXACT: recovery guidance grants no predecessor action rights");
        check(BrokerRecoveryGuidance.ForError("wait_in_progress")!.Advice.Contains("wait_for_receipt has its own and may run beside them", StringComparison.Ordinal) &&
            BrokerRecoveryGuidance.ForError("wait_capacity")!.Advice.Contains("share one wait per session", StringComparison.Ordinal),
            "RECOVERY_GUIDANCE_WAIT_CLASSES: wait refusals explain the shared message/reply wait and the separate receipt wait");
        // X3 (AgentBroker next campaign 2026-11-08): declaration and title refusals get fixed advice, never "wait for
        // discovery", which cannot help a generic mailbox, an observation-only row or a codex: title.
        string[] hintCodes = ["native_session_unverified", "invalid_session_declaration", "declaration_limit", "invalid_session_title", "session_title_limit"];
        check(hintCodes.All(code => BrokerRecoveryGuidance.ForError(code) is { NextTool: "list_sessions" } hint &&
                !hint.Advice.Contains("Wait for", StringComparison.OrdinalIgnoreCase)) &&
            BrokerRecoveryGuidance.ForError("native_session_unverified")!.Advice.Contains("codex: title cannot set one", StringComparison.Ordinal),
            "RECOVERY_GUIDANCE_HINT_CODES: every declaration and title refusal names list_sessions without promising that waiting helps");
        check(BrokerRecoveryGuidance.ForError("notice_reply_refused") is { NextTool: "acknowledge_message" } noticeHint &&
            noticeHint.Advice.Contains("cannot be answered", StringComparison.Ordinal),
            "RECOVERY_GUIDANCE_NOTICE_REPLY: a refused reply to a notice points to acknowledge_message, not send");
        // 1.10.1 (post-push audit of e398eba2): message_expired has two causes (HygieneChecks LATE_ACCEPTED_EXPIRED and
        // LATE_HELD_REFUSED), so its advice branches on the receipt instead of promising that every late message settles.
        check(BrokerRecoveryGuidance.ForError("message_expired") is { NextTool: "get_message_receipt" } expired &&
            expired.Advice.Contains("Held cannot extend it", StringComparison.Ordinal) &&
            expired.Advice.Contains("Expired means nothing remains to acknowledge", StringComparison.Ordinal) &&
            expired.Advice.Contains("Submitted or Uncertain means acknowledge Delivered", StringComparison.Ordinal),
            "RECOVERY_GUIDANCE_MESSAGE_EXPIRED: a late acknowledgement is told how to read its receipt for both causes");
        check(BrokerRecoveryGuidance.ForError("message_id_collision")!.Advice.Contains("reserved by a retained delivery notice", StringComparison.Ordinal) &&
            BrokerRecoveryGuidance.ForError("message_id_collision")!.Advice.Contains("never to replay uncertain work", StringComparison.Ordinal),
            "RECOVERY_GUIDANCE_ID_RESERVED: an ID reserved by a retained notice is explained, and a fresh ID is only for deliberately new work");

        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "recovery-guidance-" + Guid.NewGuid().ToString("N"));
        string? keyName = null;
        try
        {
            var adapter = new UncertainAdapter();
            await using var worker = await WorkerApplication.CreateAsync(new(scratch, "Synthetic guidance", LoopbackPort: 0,
                EnableLan: false, AllowUnauthenticatedLoopback: false) { EnableBrokerDiscovery = false }, [adapter]);
            keyName = BrokerIdentity.KeyNameFor(scratch, worker.Runtime.Store.BrokerId);
            await worker.StartAsync();
            var owner = worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic guidance owner", false);
            string request = Path.Combine(scratch, "request.json");
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new { tool = "read_inbox_page", arguments = new { session = owner.Address, cursor = "malformed" } }), new UTF8Encoding(false));
            using var output = new MemoryStream();
            int exitCode = await McpCallClient.RunAsync(new(scratch, request, false), output);
            using var reply = JsonDocument.Parse(output.ToArray());
            var root = reply.RootElement;
            check(exitCode == 10 && root.GetProperty("dispatchState").GetString() == "confirmed" &&
                root.GetProperty("result").GetString() == "An error occurred invoking 'read_inbox_page': invalid_inbox_cursor",
                "RECOVERY_GUIDANCE_WIRE_ERROR: actual authenticated MCP preserves failure exit and the original SDK error block");
            string[] advisories = root.GetProperty("advisories").EnumerateArray().Select(value => value.GetString()!).ToArray();
            check(advisories.Length == 1 && advisories[0].Contains("agentbroker.recovery_guidance", StringComparison.Ordinal) &&
                !advisories[0].Contains("pending_messages", StringComparison.Ordinal),
                "RECOVERY_GUIDANCE_WIRE_HINT: actual error gets recovery guidance without a successful inbox count");
            var missing = new SessionAddress(worker.Runtime.Store.BrokerId, "codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid());
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new { tool = "wait_for_messages", arguments = new { session = missing, timeoutSeconds = 0 } }), new UTF8Encoding(false));
            using var unavailableOutput = new MemoryStream();
            int unavailableCode = await McpCallClient.RunAsync(new(scratch, request, false), unavailableOutput);
            using var unavailable = JsonDocument.Parse(unavailableOutput.ToArray());
            using var availabilityHint = JsonDocument.Parse(unavailable.RootElement.GetProperty("advisories")[0].GetString()!);
            var actualGuidance = availabilityHint.RootElement.GetProperty("guidance");
            check(unavailableCode == 10 && unavailable.RootElement.GetProperty("result").GetString() == "An error occurred invoking 'wait_for_messages': session_unavailable" &&
                actualGuidance.GetProperty("availability").GetProperty("subject").GetString() == "caller_recipient" &&
                actualGuidance.GetProperty("availability").GetProperty("cause").GetString() == "unknown" &&
                actualGuidance.GetProperty("operation").GetString() == "wait_for_messages",
                "AVAILABILITY_WIRE_SUBJECT: actual authenticated MCP preserves SDK text plus local subject and cause");
            // X3 on the wire: a codex: row that discovery never observed cannot set a declaration, and the refusal carries guidance.
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new { tool = "set_session_declaration", arguments = new { session = missing, displayName = "Synthetic hint" } }), new UTF8Encoding(false));
            using var hintOutput = new MemoryStream();
            int hintCode = await McpCallClient.RunAsync(new(scratch, request, false), hintOutput);
            using var hint = JsonDocument.Parse(hintOutput.ToArray());
            var hintAdvisories = hint.RootElement.GetProperty("advisories").EnumerateArray().Select(value => value.GetString()!).ToArray();
            bool hintGuided = hintAdvisories.Any(value =>
            {
                using var advisory = JsonDocument.Parse(value);
                return advisory.RootElement.TryGetProperty("kind", out var kind) && kind.GetString() == "agentbroker.recovery_guidance" &&
                    advisory.RootElement.GetProperty("guidance").GetProperty("code").GetString() == "native_session_unverified" &&
                    advisory.RootElement.GetProperty("guidance").GetProperty("nextTool").GetString() == "list_sessions";
            });
            check(hintCode == 10 && hint.RootElement.GetProperty("result").GetString() == "An error occurred invoking 'set_session_declaration': native_session_unverified" &&
                hintGuided,
                "RECOVERY_GUIDANCE_WIRE_HINT_CODE: an unverified declaration refusal reaches the client with list_sessions guidance");

            var destination = adapter.Address(worker.Runtime.Store.BrokerId);
            await UntilAsync(() => worker.Runtime.Store.IsAddressable(destination));
            var dispatched = worker.Runtime.Store.Send(owner.Address, destination, Guid.NewGuid(), "Synthetic uncertain receipt", null, 1, worker.Runtime.Store.BrokerId);
            await UntilAsync(() => worker.Runtime.Store.FindReceipt(dispatched.Message.Id)?.Status == MessageStatus.Uncertain);
            await CheckReceiptHintAsync(dispatched.Message.Id, "synthetic_uncertain", true, "RECOVERY_GUIDANCE_WIRE_UNCERTAIN");

            string nativeId = "codex:" + Guid.NewGuid().ToString("D");
            var predecessor = new BrokerSession(new(worker.Runtime.Store.BrokerId, nativeId, Guid.NewGuid()), "Synthetic old native", "codex-native",
                SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply, SessionActivity.Busy, DateTimeOffset.UtcNow.AddMinutes(1));
            worker.Runtime.Store.Register(predecessor);
            worker.Runtime.Store.ObserveNativeSession(predecessor.Address, predecessor.Adapter);
            var queued = worker.Runtime.Store.Send(owner.Address, predecessor.Address, Guid.NewGuid(), "Synthetic never submitted receipt", null, 1, worker.Runtime.Store.BrokerId);
            var successor = predecessor with { Address = predecessor.Address with { Incarnation = Guid.NewGuid() } };
            worker.Runtime.Store.Register(successor);
            worker.Runtime.Store.ObserveNativeSession(successor.Address, successor.Adapter);
            worker.Runtime.Store.BeginDelivery();
            await CheckReceiptHintAsync(queued.Message.Id, "recipient_replaced", false, "RECOVERY_GUIDANCE_WIRE_REPLACED");

            async Task CheckReceiptHintAsync(Guid id, string code, bool mayHaveOccurred, string label)
            {
                using var receiptOutput = new MemoryStream();
                int receiptCode = await McpCallClient.RunToolAsync(scratch, "get_message_receipt", JsonSerializer.SerializeToElement(new { messageId = id }), receiptOutput, default);
                using var receiptReply = JsonDocument.Parse(receiptOutput.ToArray());
                var receipt = receiptReply.RootElement.GetProperty("result");
                bool matches = receiptReply.RootElement.GetProperty("advisories").EnumerateArray().Any(value =>
                {
                    using var advisory = JsonDocument.Parse(value.GetString()!);
                    return advisory.RootElement.TryGetProperty("kind", out var kind) && kind.GetString() == "agentbroker.recovery_guidance" &&
                        advisory.RootElement.GetProperty("guidance").GetProperty("code").GetString() == code &&
                        advisory.RootElement.GetProperty("guidance").GetProperty("deliveryMayHaveOccurred").GetBoolean() == mayHaveOccurred;
                });
                check(receiptCode == 0 && receipt.GetProperty("message").GetProperty("id").GetGuid() == id &&
                    !receipt.GetProperty("message").TryGetProperty("text", out _) && receipt.GetProperty("reason").GetString() == code && matches,
                    label + ": actual authenticated receipt preserves the original body and carries accurate recovery advice");
            }
        }
        finally
        {
            if (keyName is not null && CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider))
                using (var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider)) key.Delete();
            CleanupScratch(scratch);
        }
        await RunRecordedRefusalWireChecksAsync(check);
    }

    private static async Task RunRecordedRefusalWireChecksAsync(Action<bool, string> check)
    {
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "recovery-refusal-wire-" + Guid.NewGuid().ToString("N"));
        string? keyName = null;
        var clock = new GuidanceClock();
        try
        {
            await using var worker = await WorkerApplication.CreateAsync(new(scratch, "Synthetic recorded refusal guidance", LoopbackPort: 0,
                EnableLan: false, AllowUnauthenticatedLoopback: false) { EnableBrokerDiscovery = false }, [], clock);
            var store = worker.Runtime.Store;
            keyName = BrokerIdentity.KeyNameFor(scratch, store.BrokerId);

            BrokerSession Session(string name, bool native = false) => new(
                new(store.BrokerId, (native ? "claude:" : "mcp:") + Guid.NewGuid().ToString("D"), Guid.NewGuid()), name,
                native ? "synthetic-guidance-receipt" : "mcp", SessionCapabilities.Receive | SessionCapabilities.Reply |
                    (native ? SessionCapabilities.WakeIdle : SessionCapabilities.None), SessionActivity.Idle, clock.GetUtcNow().AddMinutes(1));
            void Renew(BrokerSession session) => store.Register(session with { LeaseUntil = clock.GetUtcNow().AddMinutes(1) });

            // The terminal notice is deliberately delayed, as in the Core lifetime fixture. Its source then reaches
            // ordinary retention before the notice does. No wall-clock wait or background delivery is needed.
            var reservedSender = Session("Synthetic reserved-ID sender");
            var reservedRecipient = Session("Synthetic reserved-ID recipient");
            store.Register(reservedSender); store.Register(reservedRecipient);
            var source = store.Send(reservedSender.Address, reservedRecipient.Address, Guid.NewGuid(), "Synthetic original reserved lifetime",
                null, 1440, store.BrokerId);
            store.Disconnect(reservedSender.Address);
            store.RecordRecipientReceipt(source.Message.Id, reservedRecipient.Address, store.BrokerId, MessageStatus.Refused);
            clock.Advance(TimeSpan.FromHours(47));
            Renew(reservedSender); Renew(reservedRecipient); store.BeginDelivery();
            var notice = store.Messages(reservedSender.Address).Single(receipt =>
                receipt.Message.Sender.SessionId == BrokerStore.NoticeSessionId && receipt.Message.Recipient == reservedSender.Address);
            store.RecordRecipientReceipt(notice.Message.Id, reservedSender.Address, store.BrokerId, MessageStatus.Delivered);
            clock.Advance(TimeSpan.FromHours(2));
            Renew(reservedSender); Renew(reservedRecipient);
            check(store.FindReceipt(source.Message.Id) is null &&
                store.FindReceipt(notice.Message.Id) is { Status: MessageStatus.Delivered } retained &&
                retained.Message.Sender.SessionId == BrokerStore.NoticeSessionId && retained.Message.Recipient == reservedSender.Address,
                "RECOVERY_GUIDANCE_WIRE_RESERVED_SETUP: the original source is pruned while its delayed terminal notice remains retained");

            var sender = Session("Synthetic late receipt sender");
            var recipient = Session("Synthetic late receipt recipient", native: true);
            store.Register(sender); store.Register(recipient);
            BrokerMessage Submitted(string text)
            {
                var message = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, recipient.Address,
                    text, clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(1));
                store.Accept(message, store.BrokerId);
                var claim = store.BeginDelivery();
                if (claim?.Message.Id != message.Id) throw new InvalidOperationException("Synthetic late receipt was not claimed.");
                store.CompleteDelivery(claim, MessageStatus.Submitted, "synthetic_admission");
                return message;
            }
            var delivered = Submitted("Synthetic late Delivered receipt");
            var refused = Submitted("Synthetic late Refused receipt");
            check(store.FindReceipt(delivered.Id)?.Status == MessageStatus.Submitted &&
                store.FindReceipt(refused.Id)?.Status == MessageStatus.Submitted && clock.GetUtcNow() < delivered.ExpiresAt,
                "RECOVERY_GUIDANCE_WIRE_LATE_SETUP: both exact native messages are Submitted before their controlled deadlines");
            clock.Advance(TimeSpan.FromMinutes(2));
            Renew(sender); Renew(recipient); Renew(reservedSender); Renew(reservedRecipient);
            await worker.StartAsync();

            foreach (var (message, outcome) in new[] { (delivered, MessageStatus.Delivered), (refused, MessageStatus.Refused) })
            {
                var held = await CallAsync("acknowledge_message", new { messageId = message.Id, recipient = recipient.Address, outcome = "Held" });
                using var heldReply = held.Reply;
                var receipt = await CallAsync("get_message_receipt", new { messageId = message.Id });
                using var receiptReply = receipt.Reply;
                check(held.ExitCode == 10 && Confirmed(heldReply.RootElement) &&
                    heldReply.RootElement.GetProperty("result").GetString() == "An error occurred invoking 'acknowledge_message': message_expired" &&
                    HasGuidance(heldReply.RootElement, "acknowledge_message", "message_expired", "get_message_receipt",
                        "Held cannot extend it", "Expired means nothing remains to acknowledge", "Submitted or Uncertain means acknowledge Delivered", "or Refused") &&
                    receipt.ExitCode == 0 && Confirmed(receiptReply.RootElement) && Status(receiptReply.RootElement) == MessageStatus.Uncertain &&
                    store.FindReceipt(message.Id) is { Status: MessageStatus.Uncertain, Reason: "receipt_unconfirmed" },
                    "RECOVERY_GUIDANCE_WIRE_LATE_HELD_" + outcome.ToString().ToUpperInvariant() +
                    ": actual authenticated Held refusal carries receipt-aware advice and leaves the submitted message Uncertain");
                var settled = await CallAsync("acknowledge_message", new { messageId = message.Id, recipient = recipient.Address, outcome = outcome.ToString() });
                using var settledReply = settled.Reply;
                check(settled.ExitCode == 0 && Confirmed(settledReply.RootElement) && Status(settledReply.RootElement) == outcome &&
                    store.FindReceipt(message.Id)?.Status == outcome,
                    "RECOVERY_GUIDANCE_WIRE_LATE_SETTLED_" + outcome.ToString().ToUpperInvariant() +
                    ": the exact recipient can still settle the refused late Held through authenticated MCP");
            }

            var absent = await CallAsync("get_message_receipt", new { messageId = source.Message.Id });
            using var absentReply = absent.Reply;
            check(absent.ExitCode == 0 && Confirmed(absentReply.RootElement) && absentReply.RootElement.GetProperty("result").ValueKind == JsonValueKind.Null,
                "RECOVERY_GUIDANCE_WIRE_RESERVED_SOURCE_ABSENT: actual authenticated receipt lookup returns null after original source retention");
            var collision = await CallAsync("send", new { sender = reservedSender.Address, recipient = reservedRecipient.Address,
                messageId = source.Message.Id, text = "Synthetic deliberately new lifetime with an old reserved ID" });
            using var collisionReply = collision.Reply;
            check(collision.ExitCode == 10 && Confirmed(collisionReply.RootElement) &&
                collisionReply.RootElement.GetProperty("result").GetString() == "An error occurred invoking 'send': message_id_collision" &&
                HasGuidance(collisionReply.RootElement, "send", "message_id_collision", "get_message_receipt",
                    "reserved by a retained delivery notice", "retry reuses the identical ID, content and addresses",
                    "fresh ID only for deliberately new work", "never to replay uncertain work") &&
                store.FindReceipt(source.Message.Id) is null && store.FindReceipt(notice.Message.Id)?.Status == MessageStatus.Delivered,
                "RECOVERY_GUIDANCE_WIRE_RESERVED_ID: retained-notice collision reaches authenticated MCP with fresh-ID guidance only for deliberately new work");

            async Task<(int ExitCode, JsonDocument Reply)> CallAsync(string tool, object arguments)
            {
                using var output = new MemoryStream();
                int exitCode = await McpCallClient.RunToolAsync(scratch, tool, JsonSerializer.SerializeToElement(arguments), output, default);
                return (exitCode, JsonDocument.Parse(output.ToArray()));
            }
        }
        finally
        {
            if (keyName is not null && CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider))
                using (var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider)) key.Delete();
            CleanupScratch(scratch);
            Console.WriteLine("RECOVERY_REFUSAL_WIRE_OWNED_RESOURCES_CLEAN=" +
                (!Directory.Exists(scratch) && (keyName is null || !CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider))).ToString().ToUpperInvariant());
        }
    }

    private static bool Confirmed(JsonElement reply) => reply.GetProperty("dispatchState").GetString() == "confirmed";
    private static MessageStatus Status(JsonElement reply) =>
        JsonSerializer.Deserialize<MessageStatus>(reply.GetProperty("result").GetProperty("status"), McpToolJson.Create());
    private static bool HasGuidance(JsonElement reply, string operation, string code, string nextTool, params string[] advice)
    {
        if (!reply.TryGetProperty("advisories", out var advisories) || advisories.ValueKind != JsonValueKind.Array) return false;
        int matches = 0;
        foreach (var value in advisories.EnumerateArray())
        {
            using var document = JsonDocument.Parse(value.GetString()!);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "agentbroker.recovery_guidance") continue;
            var guidance = root.GetProperty("guidance");
            if (guidance.GetProperty("operation").GetString() == operation && guidance.GetProperty("code").GetString() == code &&
                guidance.GetProperty("nextTool").GetString() == nextTool && advice.All(part => guidance.GetProperty("advice").GetString()!.Contains(part, StringComparison.Ordinal))) matches++;
        }
        return matches == 1;
    }

    private sealed class GuidanceClock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Advance(TimeSpan amount) => Interlocked.Add(ref ticks, amount.Ticks);
    }

    private static void CleanupScratch(string scratch)
    {
        string allowed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scratch")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(scratch).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new IOException("Guidance fixture escaped its owned scratch root.");
        if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class UncertainAdapter : ILocalAgentAdapter
    {
        private readonly Guid incarnation = Guid.NewGuid();
        public string Id => "guidance-test";
        internal SessionAddress Address(Guid brokerId) => new(brokerId, "synthetic-guidance", incarnation);
        public Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<BrokerSession>>([new(Address(brokerId), "Synthetic guidance", Id,
                SessionCapabilities.Receive | SessionCapabilities.WakeIdle, SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(1))]);
        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token) =>
            Task.FromResult(new AdapterResult(AdapterAdmission.Uncertain, "synthetic_uncertain"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
