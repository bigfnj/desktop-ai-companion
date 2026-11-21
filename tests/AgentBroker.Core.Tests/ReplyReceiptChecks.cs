using AgentBroker.Core;

internal static class ReplyReceiptChecks
{
    public static int Run(string root)
    {
        int passed = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException(label);
            passed++;
            Console.WriteLine("PASS: " + label);
        }
        BrokerSession Session(BrokerStore store, Clock clock, string id) => new(new(store.BrokerId, id, Guid.NewGuid()),
            "Synthetic reply receipt", "synthetic", SessionCapabilities.Receive | SessionCapabilities.Reply | SessionCapabilities.WakeIdle,
            SessionActivity.Idle, clock.GetUtcNow().AddMinutes(2));
        BrokerMessage Parent(BrokerSession from, BrokerSession to, Clock clock, int budget = 8) => new(Guid.NewGuid(), Guid.NewGuid(), null,
            from.Address, to.Address, "Synthetic parent retained before reply.", clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(1), budget);
        string? Refusal(Action action)
        {
            try { action(); return null; }
            catch (BrokerException error) { return error.Code; }
        }
        byte[] Bytes(string directory, string name) => File.Exists(Path.Combine(directory, name)) ? File.ReadAllBytes(Path.Combine(directory, name)) : [];

        foreach (var status in new[] { MessageStatus.Accepted, MessageStatus.Submitting, MessageStatus.Submitted, MessageStatus.Uncertain })
        {
            var clock = new Clock(); string directory = Path.Combine(root, "reply-receipt-pending-" + status);
            using var store = new BrokerStore(directory, clock: clock);
            var from = Session(store, clock, "from"); var to = Session(store, clock, "to"); store.Register(from); store.Register(to);
            var parent = Parent(from, to, clock); store.Accept(parent, store.BrokerId);
            MessageReceipt? claim = null;
            if (status != MessageStatus.Accepted)
            {
                claim = store.BeginDelivery()!;
                if (status != MessageStatus.Submitting) store.CompleteDelivery(claim, status, "synthetic_outcome");
            }
            int notices = store.Messages(from.Address).Count(receipt => receipt.Message.Sender.SessionId == BrokerStore.NoticeSessionId);
            int pending = store.ReadPendingInbox(to.Address).PendingCount;
            var before = store.CommitTimings(); clock.Advance(TimeSpan.FromMilliseconds(25));
            Guid replyId = Guid.NewGuid(); var reply = store.AcceptReply(parent.Id, replyId, "Synthetic answer.", store.BrokerId);
            var settled = store.FindReceipt(parent.Id)!;
            Check(settled.Status == MessageStatus.Delivered && settled.Reason == "reply_receipt" && settled.Attempts == 1 &&
                settled.UpdatedAt == clock.GetUtcNow() && settled.Message == parent with { Text = "" } && reply.Status == MessageStatus.Accepted,
                "REPLY_RECEIPT_PENDING_" + status + ": a fresh authorized local reply settles exactly its pending parent");
            Check(pending == 1 && store.ReadPendingInbox(to.Address).PendingCount == 0 &&
                store.CommitTimings().Completed == before.Completed + 1 && store.CommitTimings().Failed == before.Failed,
                "REPLY_RECEIPT_ONE_COMMIT_" + status + ": parent settlement, pending removal and reply share one commit");
            byte[] snapshot = Bytes(directory, "broker-state.json"), journal = Bytes(directory, "broker-tombstones.jsonl");
            clock.Advance(TimeSpan.FromMilliseconds(25));
            Check(store.AcceptReply(parent.Id, replyId, reply.Message.Text, store.BrokerId) == reply &&
                Bytes(directory, "broker-state.json").SequenceEqual(snapshot) && Bytes(directory, "broker-tombstones.jsonl").SequenceEqual(journal),
                "REPLY_RECEIPT_RETRY_" + status + ": same-ID retry neither repeats the receipt nor rewrites timestamps");
            if (claim is not null)
                Check(!store.TryCompleteDelivery(DeliveryClaim.Capture(claim), MessageStatus.Submitted, "late_transport", out _) &&
                    store.FindReceipt(parent.Id)?.Status == MessageStatus.Delivered,
                    "REPLY_RECEIPT_CLAIM_" + status + ": an old native claim cannot downgrade the recipient receipt");
            Check(store.Messages(from.Address).Count(receipt => receipt.Message.Sender.SessionId == BrokerStore.NoticeSessionId) == notices,
                "REPLY_RECEIPT_NOTICES_" + status + ": settling a parent preserves historical notices without inventing failure");
        }

        foreach (var status in new[] { MessageStatus.Held, MessageStatus.Delivered, MessageStatus.Refused, MessageStatus.Expired })
        {
            var clock = new Clock(); using var store = new BrokerStore(Path.Combine(root, "reply-receipt-final-" + status), clock: clock);
            var from = Session(store, clock, "from"); var to = Session(store, clock, "to"); store.Register(from); store.Register(to);
            var parent = Parent(from, to, clock); store.Accept(parent, store.BrokerId);
            if (status == MessageStatus.Expired) clock.Advance(TimeSpan.FromSeconds(61));
            else store.RecordRecipientReceipt(parent.Id, to.Address, store.BrokerId, status);
            var original = store.FindReceipt(parent.Id)!;
            MessageReceipt? reply = null;
            try { reply = store.AcceptReply(parent.Id, Guid.NewGuid(), "Reply without changing an acknowledged parent.", store.BrokerId); }
            catch (ArgumentOutOfRangeException) { /* An omitted tombstone-index guard must fail this named API check. */ }
            Check(reply?.Status == MessageStatus.Accepted && store.FindReceipt(parent.Id) == original,
                "REPLY_RECEIPT_PRESERVE_" + status + ": replying never overwrites Held or a terminal receipt");
        }
        {
            var clock = new Clock(); using var store = new BrokerStore(Path.Combine(root, "reply-receipt-late-uncertain"), clock: clock);
            var from = Session(store, clock, "from"); var to = Session(store, clock, "to"); store.Register(from); store.Register(to);
            var parent = Parent(from, to, clock); store.Accept(parent, store.BrokerId);
            store.CompleteDelivery(store.BeginDelivery()!, MessageStatus.Uncertain, "synthetic_uncertain");
            clock.Advance(TimeSpan.FromSeconds(61)); var previous = store.FindReceipt(parent.Id)!;
            var reply = store.AcceptReply(parent.Id, Guid.NewGuid(), "Late work still has its separate reply lifetime.", store.BrokerId);
            Check(reply.Status == MessageStatus.Accepted && store.FindReceipt(parent.Id) == previous && previous.Status == MessageStatus.Uncertain,
                "REPLY_RECEIPT_DEADLINE: a past-deadline Uncertain parent is not implicitly acknowledged");
        }
        {
            var clock = new Clock(); using var store = new BrokerStore(Path.Combine(root, "reply-receipt-rights"), clock: clock);
            var from = Session(store, clock, "from"); var to = Session(store, clock, "to"); store.Register(from); store.Register(to);
            var parent = Parent(from, to, clock); store.Accept(parent, store.BrokerId);
            store.Register(to with { Capabilities = SessionCapabilities.Reply });
            MessageReceipt? reply = null;
            string? replyRefusal = Refusal(() => reply = store.AcceptReply(parent.Id, Guid.NewGuid(), "Reply-only success is preserved.", store.BrokerId));
            Check(replyRefusal is null && reply?.Status == MessageStatus.Accepted && store.FindReceipt(parent.Id)?.Status == MessageStatus.Accepted &&
                store.FindReceiptWithText(parent.Id)?.Message == parent &&
                Refusal(() => store.RecordRecipientReceipt(parent.Id, to.Address, store.BrokerId, MessageStatus.Delivered)) == "recipient_unavailable",
                "REPLY_RECEIPT_RECEIVE: lacking Receive skips only the implicit receipt, never existing Reply permission");
        }
        foreach (bool incoming in new[] { false, true })
        {
            var clock = new Clock(); using var store = new BrokerStore(Path.Combine(root, "reply-receipt-peer-" + incoming), clock: clock);
            var local = Session(store, clock, "local"); store.Register(local);
            Guid peer = Guid.NewGuid(); store.AuthorizePeer(new(peer, "Synthetic peer", "https://192.0.2.105/", new string('A', 64), clock.GetUtcNow()));
            var remote = Session(store, clock, "remote") with { Address = new(peer, "remote", Guid.NewGuid()) };
            var parent = incoming ? Parent(remote, local, clock) : Parent(local, remote, clock);
            var original = store.Accept(parent, incoming ? peer : store.BrokerId);
            MessageReceipt? reply = null;
            try { reply = store.AcceptReply(parent.Id, Guid.NewGuid(), "A federation boundary remains explicit.", incoming ? store.BrokerId : peer); }
            catch (KeyNotFoundException) { /* Never invent a foreign local registration to make a removed scope guard reachable. */ }
            Check(reply?.Status == MessageStatus.Accepted && store.FindReceiptWithText(parent.Id) == original,
                "REPLY_RECEIPT_PEER_" + (incoming ? "SENDER" : "RECIPIENT") + ": either remote endpoint excludes implicit delivery");
        }
        {
            var clock = new Clock(); string directory = Path.Combine(root, "reply-receipt-generic-retry");
            using var store = new BrokerStore(directory, clock: clock);
            var from = Session(store, clock, "from"); var to = Session(store, clock, "to"); store.Register(from); store.Register(to);
            var parent = Parent(from, to, clock); var original = store.Accept(parent, store.BrokerId);
            var direct = new BrokerMessage(Guid.NewGuid(), parent.ConversationId, parent.Id, to.Address, from.Address,
                "Synthetic legacy lower-level reply.", clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(1), parent.RemainingReplies - 1);
            var first = store.Accept(direct, store.BrokerId);
            Check(store.FindReceiptWithText(parent.Id) == original,
                "REPLY_RECEIPT_GENERIC: lower-level Accept never infers acknowledgement from ReplyTo alone");
            byte[] snapshot = Bytes(directory, "broker-state.json"), journal = Bytes(directory, "broker-tombstones.jsonl");
            Check(store.AcceptReply(parent.Id, direct.Id, direct.Text, store.BrokerId) == first && store.FindReceiptWithText(parent.Id) == original &&
                Bytes(directory, "broker-state.json").SequenceEqual(snapshot) && Bytes(directory, "broker-tombstones.jsonl").SequenceEqual(journal),
                "REPLY_RECEIPT_LEGACY_RETRY: a stable stored reply does not heal an old pending parent");
            store.Disconnect(from.Address); store.Disconnect(to.Address);
            Check(store.AcceptReply(parent.Id, direct.Id, direct.Text, store.BrokerId) == first &&
                Bytes(directory, "broker-state.json").SequenceEqual(snapshot) && Bytes(directory, "broker-tombstones.jsonl").SequenceEqual(journal),
                "REPLY_RECEIPT_DISCONNECTED_RETRY: old receipt recovery needs no new session rights or mutation");
        }
        foreach (string kind in new[] { "text", "target", "collision", "budget" })
        {
            var clock = new Clock(); string directory = Path.Combine(root, "reply-receipt-refused-" + kind);
            using var store = new BrokerStore(directory, kind == "budget" ? new(MaximumReplies: 0) : null, clock);
            var from = Session(store, clock, "from"); var to = Session(store, clock, "to"); store.Register(from); store.Register(to);
            var parent = Parent(from, to, clock, kind == "budget" ? 0 : 8); store.Accept(parent, store.BrokerId);
            Guid replyId = Guid.NewGuid(); string text = "Refused reply must not acknowledge.";
            string expected = kind switch { "text" => "invalid_message", "target" => "recipient_unavailable", "collision" => "message_id_collision", _ => "invalid_reply" };
            if (kind == "text") text = "";
            if (kind == "target") store.Disconnect(from.Address);
            if (kind == "collision") store.Accept(Parent(from, to, clock) with { Id = replyId }, store.BrokerId);
            byte[] snapshot = Bytes(directory, "broker-state.json"), journal = Bytes(directory, "broker-tombstones.jsonl");
            string? refused = Refusal(() => store.AcceptReply(parent.Id, replyId, text, store.BrokerId));
            Check(refused == expected && store.FindReceiptWithText(parent.Id)?.Status == MessageStatus.Accepted &&
                Bytes(directory, "broker-state.json").SequenceEqual(snapshot) && Bytes(directory, "broker-tombstones.jsonl").SequenceEqual(journal),
                "REPLY_RECEIPT_REFUSAL_" + kind + ": refused admission leaves the parent and durable bytes untouched");
        }
        {
            var clock = new Clock(); using var store = new BrokerStore(Path.Combine(root, "reply-receipt-text-slot"), new(MaximumMessages: 1), clock);
            var from = Session(store, clock, "from"); var to = Session(store, clock, "to"); store.Register(from); store.Register(to);
            var parent = Parent(from, to, clock); store.Accept(parent, store.BrokerId);
            MessageReceipt? reply = null;
            try { reply = store.AcceptReply(parent.Id, Guid.NewGuid(), "The parent's released text slot funds this reply.", store.BrokerId); }
            catch (BrokerException error) when (error.Code == "message_limit") { }
            Check(reply?.Status == MessageStatus.Accepted && store.FindReceipt(parent.Id)?.Status == MessageStatus.Delivered,
                "REPLY_RECEIPT_TEXT_SLOT: capacity is evaluated after staging the local parent's tombstone");
        }
        foreach (bool queue in new[] { false, true })
        {
            var clock = new Clock(); string directory = Path.Combine(root, "reply-receipt-bound-" + queue);
            using var store = new BrokerStore(directory, queue ? new(MaximumPendingPerSession: 1) : new(MaximumTombstones: 1), clock);
            var from = Session(store, clock, "from"); var to = Session(store, clock, "to"); store.Register(from); store.Register(to);
            if (queue) { var third = Session(store, clock, "third"); store.Register(third); store.Accept(Parent(third, from, clock), store.BrokerId); }
            var parent = Parent(from, to, clock); store.Accept(parent, store.BrokerId);
            byte[] snapshot = Bytes(directory, "broker-state.json"), journal = Bytes(directory, "broker-tombstones.jsonl");
            Check(Refusal(() => store.AcceptReply(parent.Id, Guid.NewGuid(), "No cap bypass.", store.BrokerId)) == (queue ? "recipient_queue_full" : "message_limit") &&
                store.FindReceiptWithText(parent.Id)?.Status == MessageStatus.Accepted && Bytes(directory, "broker-state.json").SequenceEqual(snapshot) &&
                Bytes(directory, "broker-tombstones.jsonl").SequenceEqual(journal),
                "REPLY_RECEIPT_BOUND_" + (queue ? "QUEUE" : "RECORDS") + ": staging never weakens admission caps or publishes a refused receipt");
        }
        foreach (string stage in new[] { "snapshot-write", "snapshot-replace" })
        {
            var clock = new Clock(); string directory = Path.Combine(root, "reply-receipt-failure-" + stage);
            BrokerMessage parent; Guid replyId = Guid.NewGuid(); SessionAddress fromAddress, toAddress;
            using (var store = new BrokerStore(directory, clock: clock))
            {
                var from = Session(store, clock, "from"); var to = Session(store, clock, "to"); store.Register(from); store.Register(to);
                fromAddress = from.Address; toAddress = to.Address; parent = Parent(from, to, clock); store.Accept(parent, store.BrokerId);
                byte[] snapshot = Bytes(directory, "broker-state.json"); var before = store.CommitTimings();
                var failure = new IOException("Synthetic reply receipt commit failure."); Exception? observed = null;
                store.CommitStageProbe = current => { if (current == stage) throw failure; };
                try { store.AcceptReply(parent.Id, replyId, "Atomic durable answer.", store.BrokerId); }
                catch (IOException error) { observed = error; }
                finally { store.CommitStageProbe = null; }
                Check(ReferenceEquals(observed, failure) && store.FindReceiptWithText(parent.Id)?.Status == MessageStatus.Accepted &&
                    store.FindReceipt(replyId) is null && Bytes(directory, "broker-state.json").SequenceEqual(snapshot) &&
                    store.CommitTimings().Failed == before.Failed + 1 && store.CommitTimings().Completed == before.Completed,
                    "REPLY_RECEIPT_FAILURE_" + stage + ": a failed snapshot publishes neither parent receipt nor reply");
            }
            MessageReceipt retried;
            using (var reopened = new BrokerStore(directory, clock: clock))
            {
                Check(reopened.FindReceiptWithText(parent.Id)?.Message == parent && reopened.FindReceipt(parent.Id)?.Status == MessageStatus.Accepted &&
                    reopened.FindReceipt(replyId) is null,
                    "REPLY_RECEIPT_REOPEN_" + stage + ": the original snapshot wins over any pre-appended tombstone");
                reopened.Register(Session(reopened, clock, "from") with { Address = fromAddress });
                reopened.Register(Session(reopened, clock, "to") with { Address = toAddress });
                retried = reopened.AcceptReply(parent.Id, replyId, "Atomic durable answer.", reopened.BrokerId);
            }
            // Reopen after the successful retry, rather than observing only its current in-memory state.
            using var committed = new BrokerStore(directory, clock: clock);
            // Compare the persisted reply: FindReceipt adds a process-local wait reason after registrations are lost on restart.
            Check(retried.Status == MessageStatus.Accepted && committed.FindReceiptWithText(replyId) == retried &&
                committed.FindReceipt(parent.Id) is { Status: MessageStatus.Delivered, Reason: "reply_receipt", Attempts: 1 } delivered &&
                delivered.Message == parent with { Text = "" } && committed.PendingIncomingCounts().GetValueOrDefault(toAddress) == 0,
                "REPLY_RECEIPT_RESTORED_" + stage + ": retry after failed durability commits the pair normally");
        }
        return passed;
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 12, 1, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
