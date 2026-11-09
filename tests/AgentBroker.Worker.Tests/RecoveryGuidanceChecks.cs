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
