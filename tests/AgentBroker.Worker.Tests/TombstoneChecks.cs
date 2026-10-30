using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Security;
using ModelContextProtocol;

// N-broker-a-02, owner decision 2026-10-27 ("Tombstone on ack"), lane feature/agentbroker-backlog-e. An acknowledged
// (Delivered or Refused) message this broker sent keeps a tombstone without its text, in broker-tombstones.jsonl, and
// only messages that still carry text count toward message_limit. Each group runs alone with --tombstone-case <group>
// so a mutation witness fails on the check it targets; --tombstone-only and the full run run them all.
internal static class TombstoneChecks
{
    internal static readonly string[] Groups =
        ["disk", "upgrade", "retry", "receipt", "reply", "wait-reply", "inbox", "held", "capacity", "grace", "restart", "rollback", "wire"];

    internal static async Task RunAsync(Action<bool, string> check, string? only = null)
    {
        if (only is not null && !Groups.Contains(only, StringComparer.Ordinal)) throw new ArgumentException("Unknown tombstone check group.");
        bool Selected(string group) => only is null || only == group;
        if (Selected("disk")) await DiskAsync(check);
        if (Selected("upgrade")) Upgrade(check);
        if (Selected("retry")) await RetryAsync(check);
        if (Selected("receipt")) await ReceiptAsync(check);
        if (Selected("reply")) await LateReplyAsync(check);
        if (Selected("wait-reply")) await WaitReplyAsync(check);
        if (Selected("inbox")) await InboxAsync(check);
        if (Selected("held")) await HeldAsync(check);
        if (Selected("capacity")) await CapacityAsync(check);
        if (Selected("grace")) await GraceAsync(check);
        if (Selected("restart")) Restart(check);
        if (Selected("rollback")) Rollback(check);
        if (Selected("wire")) await WireAsync(check);
    }

    // The text leaves every store file, not just the snapshot: the probe reads each file's bytes, and its positive
    // control first proves the same probe sees the text while the message is unacknowledged.
    private static async Task DiskAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("tombstone-disk");
        var sender = fixture.Register("Synthetic tombstone sender");
        var recipient = fixture.Register("Synthetic tombstone recipient");
        string store = StoreDirectory(fixture);
        Guid delivered = Guid.NewGuid(), refused = Guid.NewGuid();
        const string deliveredText = "Synthetic tombstone text d41c7a that must leave the disk.";
        const string refusedText = "Synthetic tombstone text 9e02bb refused by its recipient.";
        fixture.Tools.Send(sender.Address, recipient.Address, delivered, deliveredText);
        fixture.Tools.Send(sender.Address, recipient.Address, refused, refusedText);
        check(FilesContain(store, deliveredText) && FilesContain(store, refusedText),
            "WITNESS an unacknowledged message's text is in the store files before acknowledgement");
        var acknowledged = fixture.Tools.AcknowledgeMessage(delivered, recipient.Address, MessageStatus.Delivered);
        fixture.Tools.AcknowledgeMessage(refused, recipient.Address, MessageStatus.Refused);
        check(!FilesContain(store, deliveredText) && !FilesContain(store, refusedText) &&
            File.Exists(Path.Combine(store, "broker-tombstones.jsonl")) && FilesContain(store, delivered.ToString("D")),
            "a Delivered or Refused acknowledgement removes the text from every store file and keeps a tombstone by ID");
        check(acknowledged.Status == MessageStatus.Delivered && acknowledged.Message.Text.Length == 0 && acknowledged.Message.Id == delivered,
            "acknowledge_message answers with what the store keeps: the Delivered status and an empty text");
    }

    // A state written by 1.0.4 (or by 1.0.4 during a rollback) holds acknowledged messages with their text. Opening it
    // moves them to tombstones at once, before any other operation.
    private static void Upgrade(Action<bool, string> check)
    {
        string root = Scratch("tombstone-upgrade");
        string store = Path.Combine(root, "store");
        Directory.CreateDirectory(store);
        var brokerId = Guid.NewGuid();
        var from = new SessionAddress(brokerId, "mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid());
        var to = new SessionAddress(brokerId, "mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        const string oldText = "Synthetic 1.0.4 delivered text 52aa10.", pendingText = "Synthetic 1.0.4 pending text 52aa11.";
        var delivered = new MessageReceipt(new(Guid.NewGuid(), Guid.NewGuid(), null, from, to, oldText, now, now.AddHours(1)),
            MessageStatus.Delivered, now, "recipient_receipt", 1);
        var pending = new MessageReceipt(new(Guid.NewGuid(), Guid.NewGuid(), null, from, to, pendingText, now, now.AddHours(1)),
            MessageStatus.Accepted, now);
        File.WriteAllBytes(Path.Combine(store, "broker-state.json"), JsonSerializer.SerializeToUtf8Bytes(new
        { Version = 1, BrokerId = brokerId, Peers = Array.Empty<PeerTrust>(), Messages = new[] { delivered, pending } }));
        try
        {
            using var opened = new BrokerStore(store);
            check(FilesContain(store, pendingText), "WITNESS the pending message from the older state keeps its text");
            check(!FilesContain(store, oldText) && opened.FindReceipt(delivered.Message.Id) is { Status: MessageStatus.Delivered } receipt &&
                receipt.Message == delivered.Message with { Text = "" },
                "opening a state an older worker wrote turns its acknowledged messages into tombstones at once");
        }
        finally { DeleteTree(root); }
    }

    private static async Task RetryAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("tombstone-retry");
        var tools = fixture.Tools;
        var sender = fixture.Register("Synthetic retry sender");
        var recipient = fixture.Register("Synthetic retry recipient");
        Guid id = Guid.NewGuid();
        const string text = "Synthetic acknowledged text for a retry.";
        var first = tools.Send(sender.Address, recipient.Address, id, text);
        tools.AcknowledgeMessage(id, recipient.Address, MessageStatus.Delivered);
        var stored = fixture.Store.FindReceipt(id)!;
        var original = stored with { Message = stored.Message with { Text = text } };
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        var retry = Attempt(() => tools.Send(sender.Address, recipient.Address, id, text, ttlMinutes: 5));
        check(retry is not null && retry == original && retry.Status == MessageStatus.Delivered && retry.Message.CreatedAt == first.Message.CreatedAt,
            "a send retry of an acknowledged message with the same ID and text returns its original receipt, matched by SHA-256");
        check(ToolFixture.Refused(() => tools.Send(sender.Address, recipient.Address, id, "Changed synthetic retry text."), "message_id_collision") &&
            ToolFixture.Refused(() => tools.Send(sender.Address, recipient.Address, id, ""), "message_id_collision"),
            "a send retry of an acknowledged message with different (or empty) text is message_id_collision");
        check(Attempt(() => tools.SendMessage(first.Message)) == original &&
            ToolFixture.Refused(() => tools.SendMessage(first.Message with { Text = "Changed complete record." }), "message_id_collision"),
            "a send_message retry of the acknowledged record returns the original receipt, and changed text collides");
        Guid replyId = Guid.NewGuid();
        const string replyText = "Synthetic reply that is acknowledged and retried.";
        tools.ReplyToMessage(id, replyId, replyText);
        tools.AcknowledgeMessage(replyId, sender.Address, MessageStatus.Delivered);
        check(Attempt(() => tools.ReplyToMessage(id, replyId, replyText)) is { Status: MessageStatus.Delivered } replyRetry && replyRetry.Message.Text == replyText &&
            ToolFixture.Refused(() => tools.ReplyToMessage(id, replyId, "Changed reply text."), "message_id_collision"),
            "a reply retry after its acknowledgement returns the original receipt, and changed text collides");
        check(fixture.Store.Messages(recipient.Address).Count(receipt => receipt.Message.Id == id) == 1 &&
            (await tools.WaitForMessages(recipient.Address, default, timeoutSeconds: 0)).Count == 0,
            "a recovered retry creates no second delivery");
    }

    private static async Task ReceiptAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("tombstone-receipt");
        var sender = fixture.Register("Synthetic receipt sender");
        var recipient = fixture.Register("Synthetic receipt recipient");
        Guid delivered = Guid.NewGuid(), refused = Guid.NewGuid();
        var sent = fixture.Tools.Send(sender.Address, recipient.Address, delivered, "Synthetic receipt text.").Message;
        fixture.Tools.Send(sender.Address, recipient.Address, refused, "Synthetic refused receipt text.");
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        fixture.Tools.AcknowledgeMessage(delivered, recipient.Address, MessageStatus.Delivered);
        fixture.Tools.AcknowledgeMessage(refused, recipient.Address, MessageStatus.Refused);
        var receipt = fixture.Tools.GetMessageReceipt(delivered);
        // The sender's compact receipt (N-broker-compact-receipts-01): no text by default, and textBytes null for a tombstone.
        var asked = fixture.Tools.GetMessageReceipt(delivered, includeText: true);
        check(receipt is { Status: MessageStatus.Delivered, Reason: "recipient_receipt", Attempts: 1, TextBytes: null } &&
            receipt.UpdatedAt == fixture.Clock.GetUtcNow() && receipt.Message.Text is null && receipt.Message.Id == sent.Id &&
            receipt.Message.Sender == sent.Sender && receipt.Message.Recipient == sent.Recipient && receipt.Message.ConversationId == sent.ConversationId &&
            receipt.Message.CreatedAt == sent.CreatedAt && receipt.Message.ExpiresAt == sent.ExpiresAt && asked?.Message.Text == "",
            "get_message_receipt answers an acknowledged message's status, addresses and times with empty text");
        check(fixture.Tools.GetMessageReceipt(refused, includeText: true) is { Status: MessageStatus.Refused, TextBytes: null } refusal && refusal.Message.Text?.Length == 0 &&
            fixture.Tools.GetMessageReceipt(Guid.NewGuid()) is null,
            "get_message_receipt answers a Refused tombstone too, and null for an unknown ID");
    }

    // "Late" is after the original's own delivery deadline: the parent's expiry bounds delivery, not the work.
    private static async Task LateReplyAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("tombstone-late-reply");
        var sender = fixture.Register("Synthetic late reply sender");
        var recipient = fixture.Register("Synthetic late reply recipient");
        Guid id = Guid.NewGuid(), replyId = Guid.NewGuid();
        // A conversation of its own: send defaults it to the message ID, which would hide a tombstone that lost it.
        var original = fixture.Tools.Send(sender.Address, recipient.Address, id, "Synthetic task that is answered late.",
            conversationId: Guid.NewGuid(), ttlMinutes: 1).Message;
        fixture.Tools.AcknowledgeMessage(id, recipient.Address, MessageStatus.Delivered);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        var reply = Attempt(() => fixture.Tools.ReplyToMessage(id, replyId, "Synthetic late reply."));
        check(reply is { Status: MessageStatus.Accepted } && reply.Message.ReplyTo == id && reply.Message.ConversationId == original.ConversationId &&
            reply.Message.Sender == recipient.Address && reply.Message.Recipient == sender.Address &&
            reply.Message.RemainingReplies == original.RemainingReplies - 1,
            "a late reply_to_message to an acknowledged original routes back to its exact sender with the decremented budget");
    }

    private static async Task WaitReplyAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("tombstone-wait-reply");
        var sender = fixture.Register("Synthetic waiting sender");
        var recipient = fixture.Register("Synthetic answering recipient");
        Guid id = Guid.NewGuid(), replyId = Guid.NewGuid();
        const string replyText = "Synthetic reply that ends a wait on an acknowledged original.";
        fixture.Tools.Send(sender.Address, recipient.Address, id, "Synthetic question.");
        fixture.Tools.AcknowledgeMessage(id, recipient.Address, MessageStatus.Delivered);
        Task<IReadOnlyList<MessageReceipt>>? waiting = null;
        try { waiting = fixture.Tools.WaitForReply(id, default, timeoutSeconds: 20); }
        catch (McpException) { }
        if (waiting is not null) await Task.WhenAny(waiting, Task.Delay(300));
        check(waiting is not null && !waiting.IsCompleted, "wait_for_reply accepts an acknowledged original and blocks until a reply exists");
        fixture.Tools.ReplyToMessage(id, replyId, replyText);
        var replies = await waiting!.WaitAsync(TimeSpan.FromSeconds(5));
        check(replies.Count == 1 && replies[0].Message.Id == replyId && replies[0].Message.Text == replyText,
            "wait_for_reply on an acknowledged original returns its reply with the reply's own text");
    }

    // Chosen: read_inbox LISTS an acknowledged message, with its status and empty text, rather than omitting it. The
    // history keeps its keyset order and its cursors, and an agent can still see what it acknowledged.
    private static async Task InboxAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("tombstone-inbox");
        var sender = fixture.Register("Synthetic inbox sender");
        var recipient = fixture.Register("Synthetic inbox recipient");
        Guid delivered = Guid.NewGuid(), refused = Guid.NewGuid(), pending = Guid.NewGuid();
        const string pendingText = "Synthetic pending inbox text.";
        fixture.Tools.Send(sender.Address, recipient.Address, delivered, "Synthetic delivered inbox text.");
        fixture.Tools.Send(sender.Address, recipient.Address, refused, "Synthetic refused inbox text.");
        fixture.Tools.Send(sender.Address, recipient.Address, pending, pendingText);
        fixture.Tools.AcknowledgeMessage(delivered, recipient.Address, MessageStatus.Delivered);
        fixture.Tools.AcknowledgeMessage(refused, recipient.Address, MessageStatus.Refused);
        var page = fixture.Tools.ReadInbox(recipient.Address);
        check(page.Count == 3 && page.Select(receipt => receipt.Message.Id).SequenceEqual(new[] { delivered, refused, pending }.Order()) &&
            page.Single(receipt => receipt.Message.Id == delivered) is { Status: MessageStatus.Delivered } d && d.Message.Text.Length == 0 &&
            page.Single(receipt => receipt.Message.Id == refused) is { Status: MessageStatus.Refused } r && r.Message.Text.Length == 0,
            "read_inbox lists acknowledged messages in keyset order with their status and empty text");
        check(page.Single(receipt => receipt.Message.Id == pending).Message.Text == pendingText,
            "WITNESS read_inbox still returns an unacknowledged message's text");
    }

    // Held is an acknowledgement that is not final: it can still become Delivered, Refused or Expired.
    private static async Task HeldAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("tombstone-held");
        var sender = fixture.Register("Synthetic held sender");
        var recipient = fixture.Register("Synthetic held recipient");
        string store = StoreDirectory(fixture);
        Guid id = Guid.NewGuid();
        const string text = "Synthetic held text 3c81f0 kept until a final outcome.";
        fixture.Tools.Send(sender.Address, recipient.Address, id, text);
        var held = fixture.Tools.AcknowledgeMessage(id, recipient.Address, MessageStatus.Held);
        check(held.Status == MessageStatus.Held && held.Message.Text == text && FilesContain(store, text) &&
            fixture.Store.FindReceipt(id)?.Message.Text == text,
            "a Held acknowledgement keeps the text on disk and in the receipt, because Held is not final");
        fixture.Tools.AcknowledgeMessage(id, recipient.Address, MessageStatus.Delivered);
        check(!FilesContain(store, text), "WITNESS the held message's later Delivered acknowledgement removes its text");
    }

    private static async Task CapacityAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("tombstone-capacity", new BrokerLimits(MaximumMessages: 2, MaximumTombstones: 4));
        var tools = fixture.Tools;
        var sender = fixture.Register("Synthetic capacity sender");
        var recipient = fixture.Register("Synthetic capacity recipient");
        Guid Send(string text) { Guid id = Guid.NewGuid(); tools.Send(sender.Address, recipient.Address, id, text); return id; }
        Guid a = Send("Synthetic capacity message a."), b = Send("Synthetic capacity message b.");
        tools.AcknowledgeMessage(a, recipient.Address, MessageStatus.Delivered);
        tools.AcknowledgeMessage(b, recipient.Address, MessageStatus.Delivered);
        Guid c = Guid.NewGuid(), d = Guid.NewGuid();
        check(Attempt(() => tools.Send(sender.Address, recipient.Address, c, "Synthetic capacity message c.")) is not null &&
            Attempt(() => tools.Send(sender.Address, recipient.Address, d, "Synthetic capacity message d.")) is not null,
            "acknowledged messages no longer count toward message_limit: two more fit a two-message bound");
        check(ToolFixture.Refused(() => Send("Synthetic capacity message e."), "message_limit"),
            "WITNESS unacknowledged messages still meet message_limit at its bound");
        tools.AcknowledgeMessage(c, recipient.Address, MessageStatus.Delivered);
        check(ToolFixture.Refused(() => Send("Synthetic capacity message f."), "message_limit") &&
            new[] { a, b, c }.All(id => fixture.Store.FindReceipt(id)?.Status == MessageStatus.Delivered),
            "the tombstone cap counts tombstones and full messages together and refuses a send instead of dropping a tombstone");
        tools.AcknowledgeMessage(d, recipient.Address, MessageStatus.Delivered);
        check(new[] { a, b, c, d }.All(id => fixture.Store.FindReceipt(id)?.Status == MessageStatus.Delivered),
            "an acknowledgement at the cap still has room for its tombstone");
    }

    // The grace after expiry is unchanged at one day: the window for a late reply and for retry de-duplication.
    private static async Task GraceAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("tombstone-grace");
        var sender = fixture.Register("Synthetic grace sender");
        var recipient = fixture.Register("Synthetic grace recipient");
        Guid id = Guid.NewGuid();
        const string text = "Synthetic text whose tombstone ages out.";
        fixture.Tools.Send(sender.Address, recipient.Address, id, text, ttlMinutes: 30);
        fixture.Tools.AcknowledgeMessage(id, recipient.Address, MessageStatus.Delivered);
        fixture.Clock.Advance(TimeSpan.FromMinutes(30) + TimeSpan.FromDays(1) - TimeSpan.FromMinutes(1));
        check(fixture.Store.FindReceipt(id)?.Status == MessageStatus.Delivered &&
            Attempt(() => fixture.Tools.Send(sender.Address, recipient.Address, id, text))?.Status == MessageStatus.Delivered,
            "a tombstone stays until one day after its message expires: the receipt and retry de-duplication still answer");
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        check(fixture.Store.FindReceipt(id) is null && fixture.Store.Messages(recipient.Address).All(receipt => receipt.Message.Id != id),
            "WITNESS a tombstone goes one day after expiry, exactly as a full receipt does");
    }

    private static void Restart(Action<bool, string> check)
    {
        string root = Scratch("tombstone-restart");
        string directory = Path.Combine(root, "store");
        string journal = Path.Combine(directory, "broker-tombstones.jsonl");
        string snapshot = Path.Combine(directory, "broker-state.json");
        var clock = new ToolFixture.ManualClock();
        BrokerMessage first, second, third;
        SessionAddress from, to;
        try
        {
            using (var store = new BrokerStore(directory, clock: clock))
            {
                (from, to) = Pair(store, clock);
                first = Message(from, to, clock, "Synthetic restart message one.");
                second = Message(from, to, clock, "Synthetic restart message two.");
                third = Message(from, to, clock, "Synthetic restart message three 0b5e.");
                foreach (var message in new[] { first, second, third }) store.Accept(message, store.BrokerId);
                store.RecordRecipientReceipt(first.Id, to, store.BrokerId, MessageStatus.Delivered);
            }
            using (var reopened = new BrokerStore(directory, clock: clock))
            {
                check(reopened.FindReceipt(first.Id) is { Status: MessageStatus.Delivered } receipt && receipt.Message.Text.Length == 0 &&
                    reopened.Accept(first, reopened.BrokerId) is { Status: MessageStatus.Delivered } recovered && recovered.Message == first,
                    "tombstones survive a restart: the receipt answers and an exact retry recovers it");
            }
            // An interrupted append leaves a partial line. Everything before it loads, and the next append cuts it off.
            File.AppendAllText(journal, "{\"Id\":\"" + new string('0', 1500));
            MessageReceipt? tornReceipt = null;
            bool tornLoaded = false;
            try
            {
                using var torn = new BrokerStore(directory, clock: clock);
                tornLoaded = true;
                tornReceipt = torn.FindReceipt(first.Id);
                Pair(torn, clock, from, to);
                torn.RecordRecipientReceipt(second.Id, to, torn.BrokerId, MessageStatus.Delivered);
            }
            catch (InvalidDataException) { }
            check(tornLoaded && tornReceipt?.Status == MessageStatus.Delivered,
                "a torn last journal line is ignored and every complete line before it loads");
            byte[] appended = File.ReadAllBytes(journal);
            check(appended[^1] == (byte)'\n' && Encoding.UTF8.GetString(appended).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .All(line => { try { using var _ = JsonDocument.Parse(line); return true; } catch (JsonException) { return false; } }),
                "the next append cuts the torn tail off, so every journal line is complete JSON");
            // A crash between the tombstone append and the snapshot write: a blocked snapshot write leaves exactly that
            // on disk. The snapshot wins at load, so the message is still pending, with its text, and listed once.
            using (var interrupted = new BrokerStore(directory, clock: clock))
            {
                Pair(interrupted, clock, from, to);
                bool failed = false;
                using (new FileStream(snapshot + ".next", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    try { interrupted.RecordRecipientReceipt(third.Id, to, interrupted.BrokerId, MessageStatus.Delivered); }
                    catch (IOException) { failed = true; }
                }
                check(failed && File.ReadAllText(journal).Contains(third.Id.ToString("D"), StringComparison.Ordinal) &&
                    interrupted.FindReceipt(third.Id)?.Status == MessageStatus.Accepted,
                    "WITNESS a failed snapshot write after the tombstone append leaves both records on disk and the message pending");
            }
            using (var recovered = new BrokerStore(directory, clock: clock))
            {
                check(recovered.FindReceipt(third.Id) is { Status: MessageStatus.Accepted } pending && pending.Message == third &&
                    recovered.Messages(to).Count(receipt => receipt.Message.Id == third.Id) == 1,
                    "after a crash between the tombstone append and the snapshot write the snapshot wins: pending, with its text, listed once");
            }
            // A damaged complete line is corruption, not a torn tail: refused, and the bytes are preserved for recovery.
            var lines = File.ReadAllLines(journal).ToList();
            lines.Insert(1, "{\"Id\":\"not a tombstone\"}");
            File.WriteAllText(journal, string.Join('\n', lines) + "\n");
            byte[] damaged = File.ReadAllBytes(journal);
            bool refused = false;
            try { using var rejected = new BrokerStore(directory, clock: clock); }
            catch (InvalidDataException) { refused = true; }
            check(refused && File.ReadAllBytes(journal).SequenceEqual(damaged),
                "a malformed complete journal line refuses to load and preserves the journal's exact bytes");
        }
        finally { DeleteTree(root); }
    }

    // ROLLBACK: a 1.0.4 worker must load what this build wrote. Rollback104/*.cs is the published 1.0.4 store, so this is
    // its loader, not a model of it. What a rollback loses is exactly what 1.0.4 cannot see: the tombstoned IDs.
    private static void Rollback(Action<bool, string> check)
    {
        string root = Scratch("tombstone-rollback");
        string directory = Path.Combine(root, "store");
        var clock = new ToolFixture.ManualClock();
        BrokerMessage acknowledged, pending, fromPeer;
        SessionAddress from, to;
        Guid broker;
        var peer = new PeerTrust(Guid.NewGuid(), "Synthetic rollback peer", "https://192.0.2.20/", new string('F', 64), clock.GetUtcNow());
        try
        {
            using (var store = new BrokerStore(directory, clock: clock))
            {
                broker = store.BrokerId;
                (from, to) = Pair(store, clock);
                store.AuthorizePeer(peer);
                acknowledged = Message(from, to, clock, "Synthetic rollback acknowledged text 61d2.");
                pending = Message(from, to, clock, "Synthetic rollback pending text.");
                fromPeer = Message(new(peer.BrokerId, "remote", Guid.NewGuid()), to, clock, "Synthetic rollback text from a paired broker.");
                store.Accept(acknowledged, broker); store.Accept(pending, broker); store.Accept(fromPeer, peer.BrokerId);
                store.RecordRecipientReceipt(acknowledged.Id, to, broker, MessageStatus.Delivered);
                store.RecordRecipientReceipt(fromPeer.Id, to, broker, MessageStatus.Delivered);
            }
            check(!FilesContain(directory, acknowledged.Text) && File.Exists(Path.Combine(directory, "broker-tombstones.jsonl")),
                "WITNESS this build wrote a tombstone, not the text, for the acknowledged message");
            Exception? failure = null;
            AgentBrokerRollback104.MessageReceipt? legacyPending = null, legacyPeer = null, legacyAcknowledged = null;
            AgentBrokerRollback104.MessageReceipt? readmitted = null;
            try
            {
                using var legacy = new AgentBrokerRollback104.BrokerStore(directory, clock: clock);
                legacyPending = legacy.FindReceipt(pending.Id);
                legacyPeer = legacy.FindReceipt(fromPeer.Id);
                legacyAcknowledged = legacy.FindReceipt(acknowledged.Id);
                // What 1.0.4 does with a retry of an ID it cannot see: it admits it as a new message.
                var legacyFrom = new AgentBrokerRollback104.SessionAddress(from.BrokerId, from.SessionId, from.Incarnation);
                var legacyTo = new AgentBrokerRollback104.SessionAddress(to.BrokerId, to.SessionId, to.Incarnation);
                foreach (var address in new[] { legacyFrom, legacyTo })
                    legacy.Register(new(address, "Synthetic rollback session", "synthetic",
                        AgentBrokerRollback104.SessionCapabilities.Receive | AgentBrokerRollback104.SessionCapabilities.Reply,
                        AgentBrokerRollback104.SessionActivity.Idle, clock.GetUtcNow().AddMinutes(1)));
                readmitted = legacy.Accept(new(acknowledged.Id, acknowledged.ConversationId, null, legacyFrom, legacyTo, acknowledged.Text,
                    acknowledged.CreatedAt, acknowledged.ExpiresAt, acknowledged.RemainingReplies), broker);
            }
            catch (Exception error) { failure = error; }
            check(failure is null && legacyPending is { Status: AgentBrokerRollback104.MessageStatus.Accepted } p && p.Message.Text == pending.Text &&
                legacyPeer is { Status: AgentBrokerRollback104.MessageStatus.Delivered } f && f.Message.Text == fromPeer.Text,
                "the published 1.0.4 loader opens the state this build wrote, with pending and paired-broker messages intact");
            check(legacyAcknowledged is null && readmitted is { Status: AgentBrokerRollback104.MessageStatus.Accepted },
                "WITNESS what a rollback loses: 1.0.4 cannot see a tombstoned ID, so its receipt is unknown and a retry is admitted again");
            Exception? forward = null;
            MessageReceipt? current = null;
            int listed = 0;
            try
            {
                using var again = new BrokerStore(directory, clock: clock);
                current = again.FindReceipt(acknowledged.Id);
                listed = again.Messages(to).Count(receipt => receipt.Message.Id == acknowledged.Id);
            }
            catch (Exception error) { forward = error; }
            check(forward is null && current is { Status: MessageStatus.Accepted } && current.Message == acknowledged && listed == 1,
                "rolling forward loads 1.0.4's state: the ID it admitted again is its full record, listed once over the older tombstone");
        }
        finally { DeleteTree(root); }
    }

    // Constraint 1, the federation wire, between two real workers over mutual TLS on loopback. The sender's broker A
    // settles a message only by fetching B's receipt and comparing the whole message with its own copy, Text included
    // (BrokerRuntime.ReconcilePeersAsync, unchanged since 1.0.4), so B keeps a paired sender's text after the
    // acknowledgement. A's own copy becomes a tombstone, and the route answers a tombstoned ID with 404, as an unknown one.
    private static async Task WireAsync(Action<bool, string> check)
    {
        check(Shape(typeof(MessageReceipt)) == Shape(typeof(AgentBrokerRollback104.MessageReceipt)) &&
            Shape(typeof(BrokerMessage)) == Shape(typeof(AgentBrokerRollback104.BrokerMessage)) &&
            Shape(typeof(SessionAddress)) == Shape(typeof(AgentBrokerRollback104.SessionAddress)) &&
            Shape(typeof(BrokerSession)) == Shape(typeof(AgentBrokerRollback104.BrokerSession)) &&
            Shape(typeof(PeerTrust)) == Shape(typeof(AgentBrokerRollback104.PeerTrust)) &&
            string.Join(",", Enum.GetNames<MessageStatus>()) == string.Join(",", Enum.GetNames<AgentBrokerRollback104.MessageStatus>()),
            "every record a peer is sent has exactly the 1.0.4 members, so a 1.0.4 peer's disallowed-member reader accepts it");
        check(Shape(typeof(AgentBrokerRollback104.BrokerMessage)).Contains("Text:String", StringComparison.Ordinal),
            "WITNESS the record shape comparison sees each member and its type");
        string root = Scratch("tombstone-wire");
        var keys = new List<string>();
        var workers = new List<WorkerApplication>();
        try
        {
            var a = await StartAsync(Path.Combine(root, "a"), keys); workers.Add(a);
            var b = await StartAsync(Path.Combine(root, "b"), keys); workers.Add(b);
            var now = DateTimeOffset.UtcNow;
            a.Runtime.Store.AuthorizePeer(new(b.Runtime.Store.BrokerId, "Synthetic peer B", LanEndpoint(b), b.Runtime.Federation.LocalAdvertisement.CertificateSha256, now));
            b.Runtime.Store.AuthorizePeer(new(a.Runtime.Store.BrokerId, "Synthetic peer A", LanEndpoint(a), a.Runtime.Federation.LocalAdvertisement.CertificateSha256, now));
            var peerB = a.Runtime.Store.Peers().Single(); var peerA = b.Runtime.Store.Peers().Single();
            var sender = a.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic federated sender", false);
            var recipient = b.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic federated recipient", false);
            Guid id = Guid.NewGuid(), replyId = Guid.NewGuid();
            const string text = "Synthetic federated text 7d03e9 for the receipt comparison.";
            a.Runtime.Store.Send(sender.Address, recipient.Address, id, text, null, 0, a.Runtime.Store.BrokerId);
            await Until(() => b.Runtime.Store.FindReceipt(id) is not null && a.Runtime.Store.FindReceipt(id)?.Status == MessageStatus.Submitted, 15);
            b.Runtime.Store.RecordRecipientReceipt(id, recipient.Address, b.Runtime.Store.BrokerId, MessageStatus.Delivered);
            check(FilesContain(Path.Combine(root, "b", "store"), text) && b.Runtime.Store.FindReceipt(id)?.Message.Text == text,
                "a message from a paired broker keeps its text after acknowledgement, for its sender's receipt check");
            var sentCopy = a.Runtime.Store.FindReceiptWithText(id)?.Message;
            var fetched = await a.Runtime.Federation.GetReceiptAsync(peerB, id, default);
            check(sentCopy is not null && fetched is { Status: MessageStatus.Delivered } && fetched.Message == sentCopy,
                "the receipts route answers the sender's broker with the whole message, text included, which is what a 1.0.4 sender compares");
            await Until(() => a.Runtime.Store.FindReceipt(id)?.Status == MessageStatus.Delivered, 20);
            check(a.Runtime.Store.FindReceipt(id) is { Status: MessageStatus.Delivered } settled && settled.Message.Text.Length == 0 &&
                !FilesContain(Path.Combine(root, "a", "store"), text),
                "the sender's broker settles Delivered through its own reconciliation and then keeps only a tombstone");
            var tombstoned = await b.Runtime.Federation.GetReceiptAsync(peerA, id, default);
            var unknown = await b.Runtime.Federation.GetReceiptAsync(peerA, Guid.NewGuid(), default);
            check(tombstoned is null && unknown is null,
                "the receipts route answers a tombstoned ID as it answers an unknown one, so no empty text is ever sent to a peer");
            b.Runtime.Store.AcceptReply(id, replyId, "Synthetic federated reply.", b.Runtime.Store.BrokerId);
            await Until(() => a.Runtime.Store.FindReceipt(replyId) is not null, 15);
            check(a.Runtime.Store.FindReceipt(replyId) is { } reply && reply.Message.ReplyTo == id && reply.Message.Recipient == sender.Address &&
                reply.Message.Text == "Synthetic federated reply.",
                "a paired broker's reply to a tombstoned original is admitted and routed to the original sender");
        }
        finally
        {
            foreach (var worker in workers) await worker.DisposeAsync();
            DeleteKeys(keys);
            DeleteTree(root);
        }
    }

    // Members and their types by name, in name order. Type names, not identities: the 1.0.4 copy is another namespace.
    private static string Shape(Type type) => string.Join(",", type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.Name != "EqualityContract").OrderBy(property => property.Name, StringComparer.Ordinal)
        .Select(property => property.Name + ":" + TypeName(property.PropertyType)));

    private static string TypeName(Type type) => type.IsGenericType
        ? type.Name + "[" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + "]" : type.Name;

    private static (SessionAddress From, SessionAddress To) Pair(BrokerStore store, TimeProvider clock,
        SessionAddress? from = null, SessionAddress? to = null)
    {
        from ??= new(store.BrokerId, "synthetic:from", Guid.NewGuid());
        to ??= new(store.BrokerId, "synthetic:to", Guid.NewGuid());
        foreach (var address in new[] { from, to })
            store.Register(new(address, "Synthetic tombstone session", "synthetic", SessionCapabilities.Receive | SessionCapabilities.Reply,
                SessionActivity.Idle, clock.GetUtcNow().AddMinutes(1)));
        return (from, to);
    }

    private static BrokerMessage Message(SessionAddress from, SessionAddress to, TimeProvider clock, string text) =>
        new(Guid.NewGuid(), Guid.NewGuid(), null, from, to, text, clock.GetUtcNow(), clock.GetUtcNow().AddHours(1));

    private static MessageReceipt? Attempt(Func<MessageReceipt> action)
    {
        try { return action(); }
        catch (McpException) { return null; }
    }

    private static string StoreDirectory(ToolFixture fixture) => Path.Combine(fixture.Root, "store");

    // Every store file except the owner lock, which the open store holds exclusively and which never holds data.
    private static bool FilesContain(string directory, string text)
    {
        byte[] needle = Encoding.UTF8.GetBytes(text);
        return Directory.EnumerateFiles(directory).Where(file => !file.EndsWith("broker-owner.lock", StringComparison.OrdinalIgnoreCase))
            .Any(file => File.ReadAllBytes(file).AsSpan().IndexOf(needle) >= 0);
    }

    private static string Scratch(string name)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "scratch", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTree(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { Console.Error.WriteLine("tombstone checks: could not delete a scratch directory"); }
        catch (UnauthorizedAccessException) { Console.Error.WriteLine("tombstone checks: could not delete a scratch directory"); }
    }

    private static async Task<WorkerApplication> StartAsync(string directory, List<string> keys)
    {
        var options = new WorkerOptions(directory, "Synthetic tombstone broker", LoopbackPort: 0, LanPort: 0, EnableLan: true,
            AllowUnauthenticatedLoopback: true) { LanListenAddress = IPAddress.Loopback, EnableBrokerDiscovery = false };
        var worker = await WorkerApplication.CreateAsync(options, []);
        keys.Add(BrokerIdentity.KeyNameFor(directory, worker.Runtime.Store.BrokerId));
        await worker.StartAsync();
        return worker;
    }

    private static string LanEndpoint(WorkerApplication worker) =>
        worker.Application.Urls.Single(url => url.StartsWith("https://", StringComparison.Ordinal)) + "/";

    // Returns at the deadline without throwing: the check after it then fails with its own label.
    private static async Task Until(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(100);
    }

    private static void DeleteKeys(IEnumerable<string> names)
    {
        foreach (string name in names)
            if (CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent))
            {
                using var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent);
                key.Delete();
            }
    }
}
