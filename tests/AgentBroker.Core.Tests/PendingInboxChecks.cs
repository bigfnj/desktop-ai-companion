using AgentBroker.Core;
using System.Text;
using System.Text.Json.Nodes;

internal static class PendingInboxChecks
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
        void Refuses(string code, Action action, string label)
        {
            try { action(); Check(false, label); }
            catch (BrokerException error) { Check(error.Code == code, label); }
        }
        var clock = new Clock();
        string directory = Path.Combine(root, "pending-inbox");
        SessionAddress recipient;
        string savedCursor;
        Guid[] retained;
        using (var store = new BrokerStore(directory, clock: clock))
        {
            BrokerSession Session(string id) => new(new(store.BrokerId, id, Guid.NewGuid()), id, "synthetic",
                SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply,
                SessionActivity.Idle, clock.GetUtcNow().AddMinutes(2));
            var owner = Session("codex:pending-owner"); var sender = Session("peer"); var other = Session("other");
            recipient = owner.Address;
            foreach (var session in new[] { owner, sender, other }) store.Register(session);
            BrokerMessage Message(Guid id, BrokerSession? to = null, int seconds = 600, int createdAgo = 0) =>
                new(id, id, null, sender.Address, (to ?? owner).Address, "Synthetic pending: Unicode 猫, newline\n and literal \\path.",
                    clock.GetUtcNow().AddSeconds(-createdAgo), clock.GetUtcNow().AddSeconds(seconds));
            var submitted = Message(Guid.Parse("ffffffff-0000-4000-8000-000000000001"));
            store.Accept(submitted, store.BrokerId);
            Check(store.BeginDelivery()?.Message.Id == submitted.Id, "INBOX_WITNESS_SUBMITTED: real delivery claims the first message");
            store.CompleteDelivery(submitted.Id, MessageStatus.Submitted);
            var uncertain = Message(Guid.Parse("00000001-0000-4000-8000-000000000001"), createdAgo: 30);
            store.Accept(uncertain, store.BrokerId);
            Check(store.BeginDelivery()?.Message.Id == uncertain.Id, "INBOX_WITNESS_UNCERTAIN: lower UUID arrives second");
            store.CompleteDelivery(uncertain.Id, MessageStatus.Uncertain, "synthetic_uncertain");
            var submitting = Message(Guid.NewGuid()); store.Accept(submitting, store.BrokerId);
            Check(store.BeginDelivery()?.Message.Id == submitting.Id, "INBOX_WITNESS_SUBMITTING: native attempt remains in flight");
            var accepted = Message(Guid.NewGuid()); store.Accept(accepted, store.BrokerId);
            foreach (var outcome in new[] { MessageStatus.Held, MessageStatus.Delivered, MessageStatus.Refused })
            {
                var done = Message(Guid.NewGuid()); store.Accept(done, store.BrokerId);
                store.RecordRecipientReceipt(done.Id, recipient, store.BrokerId, outcome);
            }
            var expired = Message(Guid.NewGuid(), seconds: 10); store.Accept(expired, store.BrokerId);
            store.Accept(Message(Guid.NewGuid(), other), store.BrokerId);
            store.Accept(Message(Guid.NewGuid(), sender) with { Sender = recipient }, store.BrokerId);
            clock.Advance(TimeSpan.FromSeconds(11));
            var all = store.ReadPendingInbox(recipient);
            Check(all.Entries.Select(entry => entry.Receipt.Message.Id).SequenceEqual(new[] { submitted.Id, uncertain.Id, submitting.Id, accepted.Id }) &&
                all.PendingCount == 4 && store.PendingIncomingCount(recipient) == 4,
                "INBOX_PENDING_STATES: exact incoming Accepted, Submitting, Submitted and Uncertain, excluding acknowledged, expired, outgoing and others");
            var directoryCounts = store.PendingIncomingCounts();
            Check(directoryCounts.GetValueOrDefault(recipient) == 4 && directoryCounts.GetValueOrDefault(other.Address) == 1 &&
                directoryCounts.GetValueOrDefault(sender.Address) >= 1 && directoryCounts.Keys.All(address => address.BrokerId == store.BrokerId),
                "INBOX_DIRECTORY_COUNTS: one query preserves exact local per-recipient counts without tombstone history");
            Guid remoteBroker = Guid.NewGuid();
            store.AuthorizePeer(new(remoteBroker, "Synthetic remote", "https://10.0.0.2:7433/", new string('A', 64), clock.GetUtcNow()));
            var remoteRecipient = new SessionAddress(remoteBroker, "remote", Guid.NewGuid());
            store.Accept(Message(Guid.NewGuid()) with { Recipient = remoteRecipient }, store.BrokerId);
            Check(store.PendingIncomingCounts().GetValueOrDefault(remoteRecipient) == 0 && store.PendingIncomingCounts().GetValueOrDefault(recipient) == 4,
                "INBOX_DIRECTORY_LOCAL_ONLY: actual queued outbound peer mail never appears in local inbox counts");
            Check(all.Entries.Select(entry => entry.ArrivalSequence).SequenceEqual(all.Entries.Select(entry => entry.ArrivalSequence).Order()) &&
                all.Entries[0].Receipt.Message.CreatedAt > all.Entries[1].Receipt.Message.CreatedAt,
                "INBOX_ARRIVAL_ORDER: broker acceptance wins over UUID and sender timestamp");
            Check(store.FindReceipt(submitting.Id)?.Status == MessageStatus.Submitting && store.FindReceipt(submitted.Id)?.Status == MessageStatus.Submitted &&
                all.Entries[0].Receipt.Message.Text == submitted.Text,
                "INBOX_READ_ONLY: fetch retains full text, acknowledges nothing and cancels no native attempt");
            store.Accept(submitted, store.BrokerId);
            Check(store.ReadPendingInbox(recipient).Entries[0].ArrivalSequence == all.Entries[0].ArrivalSequence,
                "INBOX_IDEMPOTENCE: same-ID retry retains sequence");
            var first = store.ReadPendingInbox(recipient, limit: 2);
            Check(first.HasMore && first.Entries.Count == 2 && first.PendingCount == 4,
                "INBOX_PAGE_BOUND: bounded full page reports more and total pending count");
            store.RecordRecipientReceipt(submitting.Id, recipient, store.BrokerId, MessageStatus.Delivered);
            var second = store.ReadPendingInbox(recipient, first.NextCursor, limit: 2);
            Check(!second.HasMore && second.Entries.Select(entry => entry.Receipt.Message.Id).SequenceEqual(new[] { accepted.Id }) && second.PendingCount == 3,
                "INBOX_ACK_BETWEEN_PAGES: removal does not shift or skip continuation");
            var newer = Message(Guid.Parse("00000000-0000-4000-8000-000000000001"), createdAgo: 60); store.Accept(newer, store.BrokerId);
            var nextPoll = store.ReadPendingInbox(recipient, second.NextCursor);
            Check(nextPoll.Entries.Select(entry => entry.Receipt.Message.Id).SequenceEqual(new[] { newer.Id }) && !nextPoll.HasMore,
                "INBOX_CURSOR_BETWEEN_POLLS: retained cursor returns later smaller UUID");
            var end = store.ReadPendingInbox(recipient, nextPoll.NextCursor);
            Check(end.Entries.Count == 0 && end.PendingCount == 4,
                "INBOX_EMPTY_INCREMENTAL_NOT_EMPTY_INBOX: no newer arrival still counts existing unacknowledged mail");
            Refuses("invalid_inbox_cursor", () => store.ReadPendingInbox(other.Address, nextPoll.NextCursor), "INBOX_CURSOR_SCOPE: full recipient address is bound");
            Refuses("invalid_inbox_cursor", () => store.ReadPendingInbox(recipient, "not base64"), "INBOX_CURSOR_PARSE: malformed cursor never reads as empty");
            string ChangeCursor(string field, long value)
            {
                var json = JsonNode.Parse(Convert.FromBase64String(nextPoll.NextCursor))!.AsObject(); json[field] = value;
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(json.ToJsonString()));
            }
            Refuses("invalid_inbox_cursor", () => store.ReadPendingInbox(recipient, ChangeCursor("Version", 2)), "INBOX_CURSOR_VERSION: unsupported version refuses");
            Refuses("invalid_inbox_cursor", () => store.ReadPendingInbox(recipient, ChangeCursor("AfterSequence", long.MaxValue)), "INBOX_CURSOR_FUTURE: cursor cannot skip future arrivals");
            foreach (int bad in new[] { 0, 17 })
            {
                try { store.ReadPendingInbox(recipient, limit: bad); Check(false, "INBOX_LIMIT: outside 1..16 refuses"); }
                catch (ArgumentOutOfRangeException) { passed++; }
            }
            savedCursor = nextPoll.NextCursor;
            retained = store.ReadPendingInbox(recipient).Entries.Select(entry => entry.Receipt.Message.Id).ToArray();
        }
        using (var reopened = new BrokerStore(directory, clock: clock))
        {
            reopened.Register(new(recipient, "Restored native", "synthetic", SessionCapabilities.Receive | SessionCapabilities.WakeIdle,
                SessionActivity.Idle, clock.GetUtcNow().AddMinutes(1)));
            Refuses("inbox_cursor_reset_required", () => reopened.ReadPendingInbox(recipient, savedCursor), "INBOX_RESTART_RESET: previous epoch explicitly requires rescan");
            Check(reopened.ReadPendingInbox(recipient).Entries.Select(entry => entry.Receipt.Message.Id).SequenceEqual(retained), "INBOX_RESTART_RESCAN: persisted acceptance order survives restart");
            reopened.Disconnect(recipient);
            Refuses("session_unavailable", () => reopened.ReadPendingInbox(recipient), "INBOX_ADDRESSABILITY: unavailable native address cannot report empty");
        }
        using (var store = new BrokerStore(Path.Combine(root, "pending-commit-failure"), clock: clock))
        {
            BrokerSession Session(string id) => new(new(store.BrokerId, id, Guid.NewGuid()), id, "mcp", SessionCapabilities.Receive,
                SessionActivity.Idle, clock.GetUtcNow().AddMinutes(1));
            var sender = Session("sender"); var owner = Session("owner"); store.Register(sender); store.Register(owner);
            BrokerMessage Message() => new(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, owner.Address, "Synthetic atomic sequence",
                clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(5));
            store.Accept(Message(), store.BrokerId);
            string beforeFailure = store.ReadPendingInbox(owner.Address).NextCursor;
            string blocked = Path.Combine(root, "pending-commit-failure", "broker-state.json.next"); Directory.CreateDirectory(blocked);
            var failed = Message(); bool rejected = false;
            try { store.Accept(failed, store.BrokerId); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
            Check(rejected && store.ReadPendingInbox(owner.Address).Entries.Single().ArrivalSequence == 1,
                "INBOX_FAILED_COMMIT_WITNESS: actual failed snapshot publishes no arrival");
            Check(store.ReadPendingInbox(owner.Address).NextCursor == beforeFailure,
                "INBOX_COMMIT_CURSOR_ATOMIC: failed snapshot publishes no cursor high-water change");
            Directory.Delete(blocked); store.Accept(failed, store.BrokerId);
            Check(store.ReadPendingInbox(owner.Address).Entries.Select(entry => entry.ArrivalSequence).SequenceEqual(new long[] { 1, 2 }),
                "INBOX_COMMIT_ATOMIC: failed snapshot publishes neither assignment nor counter");
        }
        return passed;
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
