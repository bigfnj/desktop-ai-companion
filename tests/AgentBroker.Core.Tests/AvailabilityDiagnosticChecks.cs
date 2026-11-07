using AgentBroker.Core;

internal static class AvailabilityDiagnosticChecks
{
    public static int Run(string root)
    {
        int passed = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); passed++; Console.WriteLine("PASS: " + label); }
        AvailabilityDiagnostic Refusal(Action action, string code, string subject, string cause, SessionAddress address, string label)
        {
            try { action(); throw new InvalidOperationException(label + ": expected refusal"); }
            catch (BrokerException error)
            {
                Check(error.Code == code && error.Message == "Broker request refused: " + code + "." &&
                    error.Availability == new AvailabilityDiagnostic(subject, cause, address), label);
                return error.Availability!;
            }
        }
        var clock = new Clock();
        BrokerSession Native(BrokerStore store) => new(new(store.BrokerId, "codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid()),
            "Synthetic diagnostic native", "codex-native", SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply,
            SessionActivity.Idle, clock.GetUtcNow().AddMinutes(2));
        using (var store = new BrokerStore(Path.Combine(root, "availability-subjects"), clock: clock))
        {
            var sender = Native(store); var recipient = Native(store); store.Register(sender); store.Register(recipient);
            var original = store.Send(sender.Address, recipient.Address, Guid.NewGuid(), "Synthetic diagnostic request", null, 0, store.BrokerId);
            store.Disconnect(sender.Address);
            Refusal(() => store.Send(sender.Address, recipient.Address, Guid.NewGuid(), "Synthetic sender refusal", null, 0, store.BrokerId),
                "sender_unavailable", "caller_sender", "disconnected", sender.Address, "AVAILABILITY_SEND_CALLER: existing sender code names the caller's exact unavailable address");
            Refusal(() => store.AcceptReply(original.Message.Id, Guid.NewGuid(), "Synthetic reply target refusal", store.BrokerId),
                "recipient_unavailable", "stored_request_sender", "disconnected", sender.Address, "AVAILABILITY_REPLY_TARGET: reply refusal names the stored original requester");
            store.Register(sender); store.Disconnect(recipient.Address);
            Refusal(() => store.Send(sender.Address, recipient.Address, Guid.NewGuid(), "Synthetic recipient refusal", null, 0, store.BrokerId),
                "recipient_unavailable", "peer_recipient", "disconnected", recipient.Address, "AVAILABILITY_SEND_PEER: send refusal names the intended peer rather than caller");
            Refusal(() => store.AcceptReply(original.Message.Id, Guid.NewGuid(), "Synthetic reply caller refusal", store.BrokerId),
                "sender_unavailable", "stored_request_recipient", "disconnected", recipient.Address, "AVAILABILITY_REPLY_CALLER: reply sender refusal names the stored request recipient");
            Refusal(() => store.RecordRecipientReceipt(original.Message.Id, recipient.Address, store.BrokerId, MessageStatus.Delivered),
                "recipient_unavailable", "caller_recipient", "disconnected", recipient.Address, "AVAILABILITY_ACK_CALLER: acknowledgement refusal names its caller's receiving address");
            var unknown = sender.Address with { Incarnation = Guid.NewGuid() };
            Refusal(() => store.RequireAddressable(unknown, "stored_request_sender"),
                "session_unavailable", "stored_request_sender", "unknown", unknown, "AVAILABILITY_WAIT_SUBJECT: wait helper preserves the caller-selected stored-request subject");
            store.Register(recipient with { Capabilities = SessionCapabilities.None });
            Refusal(() => store.RecordRecipientReceipt(original.Message.Id, recipient.Address, store.BrokerId, MessageStatus.Delivered),
                "recipient_unavailable", "caller_recipient", "receive_unavailable", recipient.Address, "AVAILABILITY_RECEIVE_CAPABILITY: live but nonreceiving acknowledgement is distinguished");
            Check(store.FindReceipt(original.Message.Id)?.Status == MessageStatus.Accepted,
                "AVAILABILITY_NO_ROUTING_CHANGE: refusal diagnostics publish no synthetic receipt");
        }
        string restartRoot = Path.Combine(root, "availability-proof");
        SessionAddress prior;
        using (var store = new BrokerStore(restartRoot, clock: clock))
        {
            var old = Native(store); prior = old.Address; store.Register(old); store.ObserveNativeSession(old.Address, old.Adapter);
            clock.Advance(TimeSpan.FromSeconds(121));
            Refusal(() => store.RequireAddressable(old.Address, "caller_recipient"), "session_unavailable", "caller_recipient", "native_lease_elapsed", old.Address,
                "AVAILABILITY_EXACT_LAPSE: known native lease expiry is reported before trimming");
            store.Directory();
            Refusal(() => store.RequireAddressable(old.Address, "caller_recipient"), "session_unavailable", "caller_recipient", "native_lease_elapsed", old.Address,
                "AVAILABILITY_TRIMMED_LAPSE: bounded history preserves a known exact-tuple lapse");
            old = old with { LeaseUntil = clock.GetUtcNow().AddMinutes(2) }; store.Register(old); store.ObserveNativeSession(old.Address, old.Adapter);
            store.RequireAddressable(old.Address, "caller_recipient");
            Check(store.IsAddressable(old.Address), "AVAILABILITY_SAME_TUPLE_RETURN: a renewed exact incarnation is addressable without stale refusal");
            var replacement = old with { Address = old.Address with { Incarnation = Guid.NewGuid() } }; store.Register(replacement);
            Refusal(() => store.RequireAddressable(old.Address, "peer_recipient"), "session_unavailable", "peer_recipient", "registration_replaced", old.Address,
                "AVAILABILITY_REGISTER_ONLY: registration reports its own event without claiming verified native succession");
            store.ObserveNativeSession(replacement.Address, replacement.Adapter);
            Refusal(() => store.RequireAddressable(old.Address, "peer_recipient"), "session_unavailable", "peer_recipient", "native_incarnation_replaced", old.Address,
                "AVAILABILITY_VERIFIED_REPLACEMENT: fresh matching native proof distinguishes a live sibling");
            clock.Advance(TimeSpan.FromSeconds(61));
            store.Register(replacement with { LeaseUntil = clock.GetUtcNow().AddMinutes(2) });
            clock.Advance(TimeSpan.FromSeconds(60));
            Refusal(() => store.RequireAddressable(old.Address, "peer_recipient"), "session_unavailable", "peer_recipient", "registration_replaced", old.Address,
                "AVAILABILITY_CURRENT_PROOF: expired sibling proof cannot keep reporting a live native replacement");
        }
        using (var reopened = new BrokerStore(restartRoot, clock: clock))
            Refusal(() => reopened.RequireAddressable(prior, "caller_recipient"), "session_unavailable", "caller_recipient", "unknown", prior,
                "AVAILABILITY_RESTART_UNKNOWN: a fresh worker never infers absence, lapse or replacement from retained records");
        using (var store = new BrokerStore(Path.Combine(root, "availability-mailboxes"), clock: clock))
        {
            var first = Native(store) with { Adapter = "mcp", Capabilities = SessionCapabilities.Receive | SessionCapabilities.Reply };
            var second = Native(store) with { Adapter = "mcp", Capabilities = SessionCapabilities.Receive | SessionCapabilities.Reply };
            store.Register(first); store.Register(second); clock.Advance(TimeSpan.FromSeconds(121));
            Check(store.IsAddressable(first.Address), "AVAILABILITY_MAILBOX_RETENTION_WITNESS: lapsed presence still leaves a reachable generic mailbox");
            var evicted = store.EvictRetainedMailbox() ?? throw new InvalidOperationException("Expected retained mailbox eviction");
            Refusal(() => store.RequireAddressable(evicted, "caller_recipient"), "session_unavailable", "caller_recipient", "mailbox_capacity_evicted", evicted,
                "AVAILABILITY_MAILBOX_EVICTION: capacity eviction has a distinct observed cause");
            var retained = evicted == first.Address ? second : first; clock.Advance(TimeSpan.FromHours(25));
            Refusal(() => store.RequireAddressable(retained.Address, "caller_recipient"), "session_unavailable", "caller_recipient", "mailbox_retention_elapsed", retained.Address,
                "AVAILABILITY_MAILBOX_RETENTION: expired mailbox retention differs from native lease lapse");
        }
        using (var store = new BrokerStore(Path.Combine(root, "availability-bounds"), clock: clock))
        {
            SessionAddress? oldest = null, newest = null;
            for (int index = 0; index < 513; index++)
            {
                var row = Native(store); oldest ??= row.Address; newest = row.Address;
                store.Register(row); store.Disconnect(row.Address);
            }
            Refusal(() => store.RequireAddressable(oldest!, "caller_recipient"), "session_unavailable", "caller_recipient", "unknown", oldest!,
                "AVAILABILITY_HISTORY_BOUND: a 513th forgotten address evicts the oldest of the 512-entry history");
            Refusal(() => store.RequireAddressable(newest!, "caller_recipient"), "session_unavailable", "caller_recipient", "disconnected", newest!,
                "AVAILABILITY_HISTORY_NEWEST: recent exact-address evidence survives bounded pruning");
        }
        using (var store = new BrokerStore(Path.Combine(root, "availability-race"), clock: clock))
        {
            var row = Native(store); store.Register(row); store.Disconnect(row.Address);
            var captured = Refusal(() => store.RequireAddressable(row.Address, "caller_recipient"), "session_unavailable", "caller_recipient", "disconnected", row.Address,
                "AVAILABILITY_CAPTURED_FACT: refusal captures its exact cause under the state lock");
            store.Register(row); store.RequireAddressable(row.Address, "caller_recipient");
            Check(captured.Cause == "disconnected" && store.IsAddressable(row.Address),
                "AVAILABILITY_NO_POSTHOC_REREAD: a later renewal cannot rewrite the already captured refusal");
            int failures = 0;
            Parallel.For(0, 128, _ =>
            {
                store.Register(row); store.Disconnect(row.Address);
                try { store.RequireAddressable(row.Address, "caller_recipient"); }
                catch (BrokerException error)
                {
                    if (error.Code != "session_unavailable" || error.Availability != new AvailabilityDiagnostic("caller_recipient", "disconnected", row.Address))
                        Interlocked.Increment(ref failures);
                }
            });
            Check(failures == 0, "AVAILABILITY_CONCURRENT_RENEWAL: exact-address snapshots remain consistent during concurrent register, disconnect and read");
        }
        return passed;
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 6, 16, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
