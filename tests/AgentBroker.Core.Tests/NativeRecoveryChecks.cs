using AgentBroker.Core;

internal static class NativeRecoveryChecks
{
    public static int Run(string root)
    {
        int passed = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException(label);
            passed++; Console.WriteLine("PASS: " + label);
        }
        void Refuses(string code, Action action, string label)
        {
            try { action(); Check(false, label); }
            catch (BrokerException error) { Check(error.Code == code, label + " (actual " + error.Code + ")"); }
        }
        BrokerSession Native(BrokerStore store, Clock clock, string? id = null) => new(
            new(store.BrokerId, id ?? "codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid()), "Same native label", "codex-native",
            SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply, SessionActivity.Idle,
            clock.GetUtcNow().AddMinutes(2));
        void Discover(BrokerStore store, BrokerSession row) { store.Register(row); store.ObserveNativeSession(row.Address, row.Adapter); }
        var clock = new Clock();
        string directory = Path.Combine(root, "native-recovery");
        BrokerSession current;
        Guid incomingId, outgoingId, replyId;
        string savedCursor;
        using (var store = new BrokerStore(directory, clock: clock))
        {
            var peer = Native(store, clock); var old = Native(store, clock);
            Discover(store, peer); Discover(store, old);
            incomingId = Guid.NewGuid(); outgoingId = Guid.NewGuid(); replyId = Guid.NewGuid();
            var incoming = store.Send(peer.Address, old.Address, incomingId, "Synthetic original request.", null, 0, store.BrokerId);
            Check(store.BeginDelivery()?.Message.Id == incomingId, "RECOVERY_WITNESS_INFLIGHT: original request is actually submitting");
            store.CompleteDelivery(incomingId, MessageStatus.Submitted, "synthetic_submitted");
            store.Send(old.Address, peer.Address, outgoingId, "Synthetic outgoing request.", null, 0, store.BrokerId);
            store.RecordRecipientReceipt(outgoingId, peer.Address, store.BrokerId, MessageStatus.Delivered);
            var reply = store.AcceptReply(outgoingId, replyId, "Synthetic exact-address answer.", store.BrokerId);
            Check(store.BeginDelivery()?.Message.Id == replyId, "RECOVERY_WITNESS_REPLY: actual reply uses its original recipient");
            store.CompleteDelivery(replyId, MessageStatus.Submitted, "synthetic_submitted");
            Guid inflightId = store.Send(peer.Address, old.Address, Guid.NewGuid(), "Synthetic in-flight native input.", null, 0, store.BrokerId).Message.Id;
            Check(store.BeginDelivery()?.Message.Id == inflightId, "RECOVERY_WITNESS_SUBMITTING: a separate native input remains in flight");
            current = old with { Address = old.Address with { Incarnation = Guid.NewGuid() } };
            store.Register(current);
            Refuses("native_recovery_unverified", () => store.ReadNativeRecovery(current.Address),
                "RECOVERY_DISCOVERY_PROOF: a native-looking registration is insufficient");
            store.ObserveNativeSession(current.Address, current.Adapter);
            var inventory = store.ReadNativeRecovery(current.Address);
            Check(inventory.Entries.Count == 4 && inventory.Entries.All(entry => entry.PreviousIncarnation && !entry.CanAcknowledge && !entry.CanReply) &&
                inventory.RecoveryPendingCount == 3 && store.AcceptedIncoming(current.Address, 16).Count == 0,
                "RECOVERY_REPORT_ONLY: prior-incarnation inventory confers no actions or ordinary-inbox delivery");
            Check(inventory.Entries.Single(entry => entry.Receipt.Message.Id == incomingId).Receipt.Message == incoming.Message &&
                inventory.Entries.Single(entry => entry.Receipt.Message.Id == replyId).Receipt.Message == reply.Message &&
                inventory.Entries.Single(entry => entry.Receipt.Message.Id == outgoingId).Receipt.Message.Text == "",
                "RECOVERY_CONTENT_AND_LINKS: original addresses, reply links and tombstones remain intact");
            Refuses("recipient_unavailable", () => store.RecordRecipientReceipt(incomingId, old.Address, store.BrokerId, MessageStatus.Delivered),
                "RECOVERY_OLD_ACK_REFUSED: report-only recovery never makes the predecessor addressable");
            Refuses("recipient_mismatch", () => store.RecordRecipientReceipt(incomingId, current.Address, store.BrokerId, MessageStatus.Delivered),
                "RECOVERY_SUCCESSOR_ACK_REFUSED: current address cannot acknowledge a predecessor's message");
            Refuses("sender_unavailable", () => store.AcceptReply(incomingId, Guid.NewGuid(), "No predecessor impersonation", store.BrokerId),
                "RECOVERY_SUCCESSOR_REPLY_REFUSED: a recovered request cannot be answered as its absent original recipient");
            store.BeginDelivery();
            Check(store.FindReceipt(incomingId)?.Status == MessageStatus.Submitted && store.FindReceipt(replyId)?.Status == MessageStatus.Submitted &&
                store.FindReceipt(inflightId)?.Status == MessageStatus.Submitting,
                "RECOVERY_NO_RECEIPT_MUTATION: inventory, refused successor actions and replacement re-evaluation preserve submitted and in-flight receipts");
            Check(typeof(BrokerStore).GetMethod("AcknowledgeRecoveredMessage") is null && typeof(BrokerStore).GetMethod("ReplyToRecoveredMessage") is null,
                "RECOVERY_NO_MUTATIVE_API: successor acknowledgement and reply entry points are absent");
            foreach (int bad in new[] { 0, 17 })
            {
                try { store.ReadNativeRecovery(current.Address, limit: bad); Check(false, "RECOVERY_PAGE_LIMIT: invalid limit refused"); }
                catch (ArgumentOutOfRangeException) { Check(true, "RECOVERY_PAGE_LIMIT: invalid limit refused"); }
            }
            var first = store.ReadNativeRecovery(current.Address, limit: 1);
            var rest = store.ReadNativeRecovery(current.Address, first.NextCursor);
            Check(first.HasMore && rest.Entries.Count == 3 && rest.Entries.All(entry => entry.Receipt.Message.Id != first.Entries[0].Receipt.Message.Id),
                "RECOVERY_PAGE_CONTINUATION: bounded inventory traverses each original record once");
            savedCursor = inventory.NextCursor;
            var newer = store.Send(peer.Address, current.Address, Guid.Parse("00000000-0000-4000-8000-000000000001"), "Synthetic later record.", null, 0, store.BrokerId);
            var later = store.ReadNativeRecovery(current.Address, savedCursor);
            Check(later.Entries.Select(entry => entry.Receipt.Message.Id).SequenceEqual(new[] { newer.Message.Id }) &&
                later.Entries[0] is { PreviousIncarnation: false, CanAcknowledge: true, CanReply: true },
                "RECOVERY_CURSOR_LATER_UUID: newly observed smaller UUID retains only ordinary exact-current action eligibility");
            var unrelated = Native(store, clock); Discover(store, unrelated);
            Check(store.ReadNativeRecovery(unrelated.Address).Entries.Count == 0,
                "RECOVERY_SESSION_SCOPE: a matching label and adapter cannot expose a different conversation");
            Refuses("invalid_native_recovery_cursor", () => store.ReadNativeRecovery(unrelated.Address, savedCursor),
                "RECOVERY_CURSOR_ADDRESS: cursor is bound to the full current address");
            Refuses("invalid_native_recovery_cursor", () => store.ReadNativeRecovery(current.Address, "malformed"),
                "RECOVERY_CURSOR_PARSE: malformed cursor is never an empty page");
            var generic = new BrokerSession(new(store.BrokerId, "mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid()), current.Label, "mcp",
                SessionCapabilities.Receive | SessionCapabilities.Reply, SessionActivity.Idle, clock.GetUtcNow().AddMinutes(1)); store.Register(generic);
            Refuses("native_recovery_unverified", () => store.ReadNativeRecovery(generic.Address), "RECOVERY_GENERIC_SPOOF: generic labels grant no inventory rights");
            Refuses("native_recovery_unverified", () => store.ReadNativeRecovery(current.Address with { BrokerId = Guid.NewGuid() }), "RECOVERY_LOCAL_ONLY: foreign broker scope refused");
            clock.Advance(TimeSpan.FromSeconds(61));
            store.Register(current with { LeaseUntil = clock.GetUtcNow().AddMinutes(2) });
            clock.Advance(TimeSpan.FromSeconds(60));
            Refuses("native_recovery_unverified", () => store.ReadNativeRecovery(current.Address),
                "RECOVERY_PROOF_LEASE: registration cannot extend discovery authority");
        }
        using (var store = new BrokerStore(directory, clock: clock))
        {
            current = current with { LeaseUntil = clock.GetUtcNow().AddMinutes(2) }; store.Register(current);
            Refuses("native_recovery_unverified", () => store.ReadNativeRecovery(current.Address), "RECOVERY_RESTART_PROOF: restart requires native rediscovery");
            store.ObserveNativeSession(current.Address, current.Adapter);
            Refuses("native_recovery_cursor_reset_required", () => store.ReadNativeRecovery(current.Address, savedCursor), "RECOVERY_CURSOR_RESTART: stale epoch explicitly requests rescan");
            Check(store.ReadNativeRecovery(current.Address).Entries.Any(entry => entry.Receipt.Message.Id == replyId && entry.Receipt.Message.ReplyTo == outgoingId),
                "RECOVERY_RESTART_CONTENT: read-only inventory survives worker reopen");
        }
        using (var store = new BrokerStore(Path.Combine(root, "recovery-policy"), clock: clock))
        {
            Guid peerId = Guid.NewGuid(); store.AuthorizePeer(new(peerId, "Synthetic peer", "https://10.0.0.2:7433/", new string('A', 64), clock.GetUtcNow()));
            var old = Native(store, clock); Discover(store, old);
            var incoming = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, new(peerId, "peer", Guid.NewGuid()), old.Address,
                "Synthetic private payload", clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(10)); store.Accept(incoming, peerId);
            var replacement = old with { Address = old.Address with { Incarnation = Guid.NewGuid() }, IsPrivate = true }; Discover(store, replacement);
            var hidden = store.ReadNativeRecovery(replacement.Address);
            Check(hidden.Entries.Single() is { ContentAvailable: false, CanAcknowledge: false, CanReply: false, BlockReason: "private_session", Receipt.Message.Text: "" },
                "RECOVERY_PRIVATE_CONTENT: current private policy suppresses foreign content and actions");
            replacement = replacement with { IsPrivate = false }; store.Register(replacement);
            Refuses("native_recovery_cursor_reset_required", () => store.ReadNativeRecovery(replacement.Address, hidden.NextCursor), "RECOVERY_POLICY_RESET: privacy change requires rescan");
            Check(store.ReadNativeRecovery(replacement.Address).Entries.Single().ContentAvailable, "RECOVERY_POLICY_REFRESH: rescan restores permitted retained content");
            store.RevokePeer(peerId);
            Check(store.ReadNativeRecovery(replacement.Address).Entries.Single() is { ContentAvailable: false, BlockReason: "unpaired_peer" },
                "RECOVERY_REVOKED_CONTENT: revoked peer text remains blocked");
        }
        using (var store = new BrokerStore(Path.Combine(root, "recovery-reply-refusal"), clock: clock))
        {
            var old = Native(store, clock); var peer = Native(store, clock); Discover(store, old); Discover(store, peer);
            var parent = store.Send(old.Address, peer.Address, Guid.NewGuid(), "Synthetic absent request", null, 0, store.BrokerId);
            store.Disconnect(old.Address);
            Guid absentId = Guid.NewGuid();
            Refuses("recipient_unavailable", () => store.AcceptReply(parent.Message.Id, absentId, "Absent answer", store.BrokerId),
                "RECOVERY_ABSENT_REPLY_REFUSED: no reply is retained for an absent native requester");
            var replacement = old with { Address = old.Address with { Incarnation = Guid.NewGuid() } }; Discover(store, replacement);
            Guid replacedId = Guid.NewGuid();
            Refuses("recipient_unavailable", () => store.AcceptReply(parent.Message.Id, replacedId, "Replaced answer", store.BrokerId),
                "RECOVERY_REPLACED_REPLY_REFUSED: a successor never becomes the reply's recipient");
            Check(store.FindReceipt(absentId) is null && store.FindReceipt(replacedId) is null && store.ReadPendingInbox(replacement.Address).PendingCount == 0,
                "RECOVERY_NO_HELD_REPLY: refusal publishes no record and no successor mail");
        }
        passed += ReplacementChecks(root, clock);
        passed += ActionEligibilityChecks(root);
        using (var store = new BrokerStore(Path.Combine(root, "recovery-id-reuse"), clock: clock))
        {
            var sender = Native(store, clock); var recipient = Native(store, clock); Discover(store, sender); Discover(store, recipient);
            Guid id = Guid.NewGuid(); store.Send(sender.Address, recipient.Address, id, "Synthetic first record", null, 1, store.BrokerId);
            var first = store.ReadNativeRecovery(recipient.Address); clock.Advance(TimeSpan.FromHours(25));
            sender = sender with { LeaseUntil = clock.GetUtcNow().AddMinutes(2) }; recipient = recipient with { LeaseUntil = clock.GetUtcNow().AddMinutes(2) };
            Discover(store, sender); Discover(store, recipient);
            Check(store.FindReceipt(id) is null, "RECOVERY_ID_REUSE_WITNESS: ordinary expiry prunes the original record");
            store.Send(sender.Address, recipient.Address, id, "Synthetic second record", null, 1, store.BrokerId);
            var next = store.ReadNativeRecovery(recipient.Address, first.NextCursor);
            Check(next.Entries.Count == 1 && next.Entries[0].Receipt.Message.Text == "Synthetic second record" && next.Entries[0].RecordSequence > first.Entries[0].RecordSequence,
                "RECOVERY_ID_REUSE_SEQUENCE: re-admitted ID gets a fresh inventory sequence");
        }
        using (var store = new BrokerStore(Path.Combine(root, "recovery-proof-capacity"), new(MaximumSessions: 2), clock))
        {
            var first = Native(store, clock); var kept = Native(store, clock); var next = Native(store, clock);
            Discover(store, first); Discover(store, kept); store.Disconnect(first.Address); Discover(store, next);
            Check(store.IsVerifiedNativeSession(kept.Address) && store.IsVerifiedNativeSession(next.Address), "RECOVERY_PROOF_CAPACITY_REUSE: disconnected proofs free bounded slots");
        }
        return passed;
    }

    private static int ActionEligibilityChecks(string root)
    {
        int passed = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); passed++; Console.WriteLine("PASS: " + label); }
        BrokerSession Native(BrokerStore store, Clock clock) => new(new(store.BrokerId, "codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid()),
            "Synthetic action eligibility", "codex-native", SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply,
            SessionActivity.Idle, clock.GetUtcNow().AddMinutes(2));
        void Discover(BrokerStore store, BrokerSession row) { store.Register(row); store.ObserveNativeSession(row.Address, row.Adapter); }
        foreach (var status in new[] { MessageStatus.Submitting, MessageStatus.Submitted, MessageStatus.Uncertain })
        {
            var clock = new Clock();
            using var store = new BrokerStore(Path.Combine(root, "recovery-late-ack-" + status), clock: clock);
            var sender = Native(store, clock); var recipient = Native(store, clock); Discover(store, sender); Discover(store, recipient);
            var sent = store.Send(sender.Address, recipient.Address, Guid.NewGuid(), "Synthetic late receipt", null, 1, store.BrokerId);
            Check(store.BeginDelivery()?.Message.Id == sent.Message.Id, "RECOVERY_ACTION_WITNESS: actual native submission precedes late receipt");
            if (status != MessageStatus.Submitting) store.CompleteDelivery(sent.Message.Id, status);
            clock.Advance(TimeSpan.FromSeconds(61));
            var entry = store.ReadNativeRecovery(recipient.Address).Entries.Single(value => value.Receipt.Message.Id == sent.Message.Id);
            var acknowledged = store.RecordRecipientReceipt(sent.Message.Id, recipient.Address, store.BrokerId, MessageStatus.Delivered);
            Check(entry.CanAcknowledge && acknowledged.Status == MessageStatus.Delivered,
                "RECOVERY_ACK_LATE_MATCHES_ACTION: " + status + " remains acknowledgeable after its delivery deadline");
            Check(!store.ReadNativeRecovery(recipient.Address).Entries.Single(value => value.Receipt.Message.Id == sent.Message.Id).CanAcknowledge,
                "RECOVERY_ACK_FINAL_NO_NEW_ACTION: a completed receipt advertises no new acknowledgement");
        }
        {
            var clock = new Clock();
            using var store = new BrokerStore(Path.Combine(root, "recovery-reply-target"), clock: clock);
            var sender = Native(store, clock); var recipient = Native(store, clock); Discover(store, sender); Discover(store, recipient);
            var parent = store.Send(sender.Address, recipient.Address, Guid.NewGuid(), "Synthetic reply target", null, 0, store.BrokerId);
            NativeRecoveryEntry Entry() => store.ReadNativeRecovery(recipient.Address).Entries.Single(value => value.Receipt.Message.Id == parent.Message.Id);
            bool ReplySucceeds(out string? refusal)
            {
                try { var reply = store.AcceptReply(parent.Message.Id, Guid.NewGuid(), "Synthetic target answer", store.BrokerId); refusal = null; return reply.Status == MessageStatus.Accepted; }
                catch (BrokerException error) { refusal = error.Code; return false; }
            }
            bool eligible = Entry().CanReply;
            Check(ReplySucceeds(out _) == eligible && eligible, "RECOVERY_REPLY_LIVE_MATCHES_ACTION: exact live target permits an actual new reply");
            store.Disconnect(sender.Address);
            eligible = Entry().CanReply;
            bool replied = ReplySucceeds(out var absent);
            Check(eligible == replied && !replied && absent == "recipient_unavailable",
                "RECOVERY_REPLY_TARGET_MATCHES_ACTION: absent original sender is not an eligible reply recipient");
            var successor = sender with { Address = sender.Address with { Incarnation = Guid.NewGuid() } }; Discover(store, successor);
            eligible = Entry().CanReply;
            replied = ReplySucceeds(out var replaced);
            Check(eligible == replied && !replied && replaced == "recipient_unavailable",
                "RECOVERY_REPLY_REPLACED_MATCHES_ACTION: a successor never supplies the original target's rights");
            Discover(store, sender);
            eligible = Entry().CanReply;
            Check(ReplySucceeds(out _) == eligible && eligible, "RECOVERY_REPLY_RETURN_MATCHES_ACTION: the exact original sender may return");
            store.Register(sender with { Capabilities = SessionCapabilities.Reply });
            eligible = Entry().CanReply;
            replied = ReplySucceeds(out var cannotReceive);
            Check(eligible == replied && !replied && cannotReceive == "recipient_cannot_receive",
                "RECOVERY_REPLY_RECEIVE_MATCHES_ACTION: an addressable target must still be able to receive");
        }
        return passed;
    }

    private static int ReplacementChecks(string root, Clock clock)
    {
        int passed = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); passed++; Console.WriteLine("PASS: " + label); }
        BrokerSession Native(BrokerStore store, string? id = null) => new(new(store.BrokerId, id ?? "codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid()),
            "Same replacement label", "codex-native", SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply,
            SessionActivity.Busy, clock.GetUtcNow().AddMinutes(2));
        using (var store = new BrokerStore(Path.Combine(root, "native-replacement"), clock: clock))
        {
            var sender = Native(store) with { Adapter = "mcp", Capabilities = SessionCapabilities.Receive | SessionCapabilities.Reply };
            var old = Native(store); store.Register(sender); store.Register(old); store.ObserveNativeSession(old.Address, old.Adapter);
            var queued = store.Send(sender.Address, old.Address, Guid.NewGuid(), "Synthetic queued predecessor request", null, 0, store.BrokerId);
            // A fake successor registration must never suffice, including after Register forgets the predecessor.
            var successor = old with { Address = old.Address with { Incarnation = Guid.NewGuid() } }; store.Register(successor);
            store.BeginDelivery();
            Check(store.FindReceipt(queued.Message.Id)?.Status == MessageStatus.Accepted, "REPLACEMENT_REQUIRES_DISCOVERY: registration alone cannot refuse queued mail");
            store.ObserveNativeSession(successor.Address, successor.Adapter); store.BeginDelivery();
            var refused = store.FindReceipt(queued.Message.Id);
            var notices = store.Messages(sender.Address).Where(receipt => receipt.Message.Sender.SessionId == BrokerStore.NoticeSessionId).ToArray();
            Check(refused is { Status: MessageStatus.Refused, Reason: "recipient_replaced", Attempts: 0 } && refused.Message.Recipient == old.Address,
                "REPLACEMENT_REFUSES_ACCEPTED: discovery-verified replacement refuses the unsubmitted original address");
            Check(notices.Length == 1 && notices[0].Message.Text.Contains(successor.Address.Incarnation.ToString(), StringComparison.Ordinal) &&
                notices[0].Message.Text.Contains("fresh messageId", StringComparison.Ordinal),
                "REPLACEMENT_NOTICE_ONCE: sender learns the exact replacement and safe explicit resend rule");
            store.BeginDelivery();
            Check(store.Messages(sender.Address).Count(receipt => receipt.Message.Sender.SessionId == BrokerStore.NoticeSessionId) == 1 &&
                store.ReadPendingInbox(successor.Address).Entries.Count == 0, "REPLACEMENT_NO_REDELIVERY: repeated passes neither duplicate notice nor inject old mail");
        }
        string restartRoot = Path.Combine(root, "native-replacement-restart");
        SessionAddress oldAddress, senderAddress; Guid queuedId, submittedId, uncertainId;
        using (var store = new BrokerStore(restartRoot, clock: clock))
        {
            var sender = Native(store); var old = Native(store) with { Activity = SessionActivity.Idle };
            senderAddress = sender.Address; oldAddress = old.Address; store.Register(sender); store.Register(old);
            submittedId = store.Send(sender.Address, old.Address, Guid.NewGuid(), "Synthetic already submitted", null, 0, store.BrokerId).Message.Id;
            store.BeginDelivery(); store.CompleteDelivery(submittedId, MessageStatus.Submitted);
            uncertainId = store.Send(sender.Address, old.Address, Guid.NewGuid(), "Synthetic already uncertain", null, 0, store.BrokerId).Message.Id;
            store.BeginDelivery(); store.CompleteDelivery(uncertainId, MessageStatus.Uncertain);
            store.Register(old with { Activity = SessionActivity.Busy });
            queuedId = store.Send(sender.Address, old.Address, Guid.NewGuid(), "Synthetic queued before restart", null, 0, store.BrokerId).Message.Id;
        }
        using (var store = new BrokerStore(restartRoot, clock: clock))
        {
            var successor = Native(store, oldAddress.SessionId); store.Register(successor); store.ObserveNativeSession(successor.Address, successor.Adapter);
            store.BeginDelivery();
            Check(store.FindReceipt(queuedId) is { Status: MessageStatus.Refused, Reason: "recipient_replaced" },
                "REPLACEMENT_AFTER_RESTART: absent predecessor registration does not hide a verified successor");
            Check(store.FindReceipt(submittedId)?.Status == MessageStatus.Uncertain && store.FindReceipt(uncertainId)?.Status == MessageStatus.Uncertain,
                "REPLACEMENT_PRESERVES_UNCERTAINTY: prior submitted or uncertain native input is never refused or replayed");
            // The sender's original incarnation must return to receive its owed notice.
            store.Register(new(senderAddress, "Synthetic sender returned", "mcp", SessionCapabilities.Receive | SessionCapabilities.Reply,
                SessionActivity.Idle, clock.GetUtcNow().AddMinutes(1))); store.BeginDelivery();
            Check(store.Messages(senderAddress).Any(receipt => receipt.Message.Sender.SessionId == BrokerStore.NoticeSessionId),
                "REPLACEMENT_OWED_NOTICE: existing exact-sender notice admission survives restart");
        }
        using (var store = new BrokerStore(Path.Combine(root, "native-replacement-flicker"), clock: clock))
        {
            var sender = Native(store); var recipient = Native(store); store.Register(sender); store.Register(recipient); store.ObserveNativeSession(recipient.Address, recipient.Adapter);
            var queued = store.Send(sender.Address, recipient.Address, Guid.NewGuid(), "Synthetic same-tuple lapse", null, 0, store.BrokerId);
            clock.Advance(TimeSpan.FromSeconds(121)); store.BeginDelivery();
            Check(store.FindReceipt(queued.Message.Id)?.Status == MessageStatus.Accepted, "REPLACEMENT_LEASE_LAPSE: expiry alone is not replacement");
            recipient = recipient with { LeaseUntil = clock.GetUtcNow().AddMinutes(2) }; store.Register(recipient); store.ObserveNativeSession(recipient.Address, recipient.Adapter);
            store.BeginDelivery();
            Check(store.FindReceipt(queued.Message.Id)?.Status == MessageStatus.Accepted, "REPLACEMENT_SAME_TUPLE_RETURN: rediscovery of the original incarnation preserves queued mail");
        }
        return passed;
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 6, 15, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
