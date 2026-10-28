using AgentBroker.Core;

// N-broker-notices-01: a local sender whose message ends Expired or Refused is told once, by a broker notice in its own
// queue, as soon as its exact address can take it. Each label below names the rule a mutation of
// BrokerStore.WithSenderNotices, its delivery-pass trigger or the reserved-sender guards must break.
internal static class SenderNoticeChecks
{
    public static int Run(string root)
    {
        int passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed++;
        }
        void Refuses(string code, Action action, string label)
        {
            string? actual = null;
            try { action(); }
            catch (BrokerException e) { actual = e.Code; }
            Check(actual == code, label + " (expected " + code + ", got " + (actual ?? "no refusal") + ")");
        }
        BrokerSession Native(BrokerStore store, Clock clock, string id) => new(new(store.BrokerId, id, Guid.NewGuid()), "Native " + id,
            "claude-native", SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply,
            SessionActivity.Idle, clock.GetUtcNow().AddMinutes(2));
        BrokerSession Mailbox(BrokerStore store, Clock clock, string id) => new(new(store.BrokerId, "mcp:" + id, Guid.NewGuid()),
            "Mailbox " + id, "mcp", SessionCapabilities.Receive | SessionCapabilities.Reply, SessionActivity.Idle, clock.GetUtcNow().AddMinutes(1));
        BrokerSession Renewed(BrokerSession session, Clock clock) => session with { LeaseUntil = clock.GetUtcNow().AddMinutes(1) };
        BrokerMessage Message(BrokerSession from, BrokerSession to, Clock clock, TimeSpan lifetime) => new(Guid.NewGuid(), Guid.NewGuid(), null,
            from.Address, to.Address, "A harmless synthetic request.", clock.GetUtcNow(), clock.GetUtcNow() + lifetime);
        List<MessageReceipt> Notices(BrokerStore store, SessionAddress owner) => store.Messages(owner)
            .Where(r => r.Message.Sender.SessionId == BrokerStore.NoticeSessionId && r.Message.Recipient == owner).ToList();
        bool About(MessageReceipt notice, BrokerMessage message) => notice.Message.Text.Contains(message.Id.ToString(), StringComparison.Ordinal);

        // Expiry in a mailbox nobody reads: the 2026-10-28 case, at the exact deadline.
        string path = Path.Combine(root, "sender-notice-expired");
        var clock = new Clock();
        SessionAddress senderAddress;
        Guid noticeId;
        using (var store = new BrokerStore(path, clock: clock))
        {
            var sender = Native(store, clock, "claude:notice-sender"); var mailbox = Mailbox(store, clock, "unread");
            store.Register(sender); store.Register(mailbox);
            senderAddress = sender.Address;
            var lost = Message(sender, mailbox, clock, TimeSpan.FromMinutes(20));
            store.Accept(lost, store.BrokerId);
            Check(Notices(store, sender.Address).Count == 0, "NOTICE_NOT_EARLY: no notice while the message is still pending");
            // Discovery re-registers a live native session every 10 seconds; the expiry commits on the next store call.
            clock.Advance(TimeSpan.FromMinutes(20));
            store.Register(sender with { LeaseUntil = clock.GetUtcNow().AddMinutes(2) });
            var notices = Notices(store, sender.Address);
            Check(notices.Count == 1 && About(notices[0], lost), "NOTICE_EXPIRED: a message expiring at its exact deadline yields one notice to its sender");
            var notice = notices[0];
            noticeId = notice.Message.Id;
            Check(notice.Status == MessageStatus.Accepted && notice.Message.ReplyTo is null && notice.Message.RemainingReplies == 0 &&
                notice.Message.Sender.BrokerId == store.BrokerId && notice.Message.ExpiresAt == clock.GetUtcNow().AddMinutes(1440),
                "NOTICE_SHAPE: a notice is pending, not a reply, has no reply budget and gets the default lifetime");
            Check(notice.Message.Text.Contains("20-minute delivery deadline", StringComparison.Ordinal) &&
                notice.Message.Text.Contains("without a Delivered receipt", StringComparison.Ordinal) &&
                notice.Message.Text.Contains("cannot tell whether the recipient read it", StringComparison.Ordinal) &&
                notice.Message.Text.Contains("\"Mailbox unread\" (mcp:unread)", StringComparison.Ordinal) &&
                !notice.Message.Text.Contains("undelivered", StringComparison.Ordinal),
                "NOTICE_TEXT: the notice names the outcome, deadline and recipient without claiming the message went unseen");
            Check(store.BeginDelivery()?.Message.Id == noticeId, "NOTICE_DELIVERABLE: a native sender is woken with its notice");
            store.CompleteDelivery(noticeId, MessageStatus.Delivered, "native_submitted");
            store.Register(sender with { LeaseUntil = clock.GetUtcNow().AddMinutes(2) });
            clock.Advance(TimeSpan.FromSeconds(5));
            store.BeginDelivery();
            Check(store.Messages(sender.Address).Count(r => r.Message.Sender.SessionId == BrokerStore.NoticeSessionId) == 1,
                "NOTICE_ONCE: later commits and delivery passes never add a second notice for the same message");
            Refuses("invalid_reply", () => store.AcceptReply(noticeId, Guid.NewGuid(), "A reply to a notice.", store.BrokerId),
                "NOTICE_NO_REPLY: nobody can reply to a notice");
        }
        using (var reopened = new BrokerStore(path, clock: clock))
        {
            reopened.Register(new(senderAddress, "Native again", "claude-native",
                SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply, SessionActivity.Idle, clock.GetUtcNow().AddMinutes(2)));
            reopened.BeginDelivery();
            Check(reopened.Messages(senderAddress).Count(r => r.Message.Sender.SessionId == BrokerStore.NoticeSessionId) == 1 &&
                reopened.FindReceipt(noticeId)?.Status == MessageStatus.Delivered,
                "NOTICE_DURABLE_ONCE: a restart neither repeats nor loses an admitted notice");
        }

        // Every route to Expired: a Held message, a released claim (Attempts 1, so no attempts filter), an Accept retry.
        clock = new Clock();
        using (var store = new BrokerStore(Path.Combine(root, "sender-notice-paths"), clock: clock))
        {
            var sender = Mailbox(store, clock, "paths-sender"); var mailbox = Mailbox(store, clock, "paths-mailbox");
            var native = Native(store, clock, "claude:paths-native");
            store.Register(sender); store.Register(mailbox); store.Register(native);
            var held = Message(sender, mailbox, clock, TimeSpan.FromMinutes(5));
            var claimed = Message(sender, native, clock, TimeSpan.FromMinutes(5));
            var retried = Message(sender, mailbox, clock, TimeSpan.FromMinutes(5));
            store.Accept(held, store.BrokerId);
            store.RecordRecipientReceipt(held.Id, mailbox.Address, store.BrokerId, MessageStatus.Held);
            store.Accept(claimed, store.BrokerId);
            Check(store.BeginDelivery()?.Message.Id == claimed.Id, "The native message is claimed before its deadline.");
            store.Accept(retried, store.BrokerId);
            clock.Advance(TimeSpan.FromMinutes(6));
            store.Register(Renewed(sender, clock)); store.Register(Renewed(mailbox, clock));
            Check(store.Accept(retried, store.BrokerId).Status == MessageStatus.Expired, "The Accept retry is the first call to see the expiry.");
            Check(store.ReleaseUnsubmittedClaim(claimed.Id).Status == MessageStatus.Expired, "A released claim past its deadline expires.");
            var notices = Notices(store, sender.Address);
            Check(notices.Count(n => About(n, held)) == 1, "NOTICE_HELD: a Held message that expires yields one notice");
            Check(notices.Count(n => About(n, claimed)) == 1 && notices.Single(n => About(n, claimed)).Message.Text.Contains("(1 delivery attempts)", StringComparison.Ordinal),
                "NOTICE_RELEASE: a released claim that expires yields one notice reporting its attempt");
            Check(notices.Count(n => About(n, retried)) == 1, "NOTICE_RETRY_PATH: an expiry first seen by an Accept retry yields one notice");
        }

        // Outcomes that must and must not produce a notice.
        clock = new Clock();
        using (var store = new BrokerStore(Path.Combine(root, "sender-notice-outcomes"), clock: clock))
        {
            var sender = Mailbox(store, clock, "sender"); var recipient = Mailbox(store, clock, "recipient");
            store.Register(sender); store.Register(recipient);
            var refused = Message(sender, recipient, clock, TimeSpan.FromMinutes(30));
            store.Accept(refused, store.BrokerId);
            store.RecordRecipientReceipt(refused.Id, recipient.Address, store.BrokerId, MessageStatus.Refused);
            var notices = Notices(store, sender.Address);
            Check(notices.Count == 1 && About(notices[0], refused) && notices[0].Message.Text.Contains("was refused by its recipient", StringComparison.Ordinal),
                "NOTICE_REFUSED: a refusal is reported to the sender once");
            var delivered = Message(sender, recipient, clock, TimeSpan.FromMinutes(30));
            store.Accept(delivered, store.BrokerId);
            store.RecordRecipientReceipt(delivered.Id, recipient.Address, store.BrokerId, MessageStatus.Delivered);
            clock.Advance(TimeSpan.FromMinutes(31));
            store.Register(Renewed(sender, clock));
            Check(Notices(store, sender.Address).Count == 1, "NOTICE_NOT_ON_DELIVERY: a delivered message never produces a notice");

            // A notice nobody acknowledges expires quietly: notices never chain.
            store.Register(Renewed(recipient, clock));
            var unread = Message(recipient, sender, clock, TimeSpan.FromMinutes(1));
            store.Accept(unread, store.BrokerId);
            clock.Advance(TimeSpan.FromMinutes(2));
            store.Register(Renewed(recipient, clock)); store.Register(Renewed(sender, clock));
            Check(Notices(store, recipient.Address).Count == 1, "NOTICE_EXPIRED_MAILBOX: a generic mailbox sender gets its notice too");
            clock.Advance(TimeSpan.FromMinutes(1441));
            store.Register(Renewed(sender, clock)); store.Register(Renewed(recipient, clock));
            store.BeginDelivery();
            Check(store.Messages(recipient.Address).Count(r => r.Message.Sender.SessionId == BrokerStore.NoticeSessionId) == 1 &&
                Notices(store, recipient.Address).Single().Status == MessageStatus.Expired,
                "NOTICE_NO_CHAIN: an expired notice never produces another notice");

            // A sender that is away is owed its notice, and gets it on the first delivery pass after it is back.
            var gone = Mailbox(store, clock, "gone");
            store.Register(gone);
            var orphan = Message(gone, recipient, clock, TimeSpan.FromMinutes(1));
            store.Accept(orphan, store.BrokerId);
            store.Disconnect(gone.Address);
            clock.Advance(TimeSpan.FromMinutes(2));
            store.Register(Renewed(recipient, clock));
            Check(store.Messages(recipient.Address).Single(r => r.Message.Id == orphan.Id).Status == MessageStatus.Expired &&
                Notices(store, gone.Address).Count == 0,
                "NOTICE_SENDER_GONE: an unaddressable sender gets nothing yet, and the expiry still commits");
            store.Register(Renewed(gone, clock));
            store.BeginDelivery();
            Check(Notices(store, gone.Address).Count(n => About(n, orphan)) == 1,
                "NOTICE_OWED_RETURN: the same exact sender gets its owed notice on the first delivery pass after it is back");

            // A native sender whose lease lapsed is gone even while its record waits for trimming; one that cannot receive is skipped.
            var lapsed = Native(store, clock, "claude:lapsed"); var deaf = Native(store, clock, "claude:deaf") with { Capabilities = SessionCapabilities.Reply };
            store.Register(lapsed); store.Register(deaf);
            var fromLapsed = Message(lapsed, recipient, clock, TimeSpan.FromMinutes(5));
            var fromDeaf = Message(deaf, recipient, clock, TimeSpan.FromMinutes(1));
            store.Register(Renewed(recipient, clock));
            store.Accept(fromLapsed, store.BrokerId); store.Accept(fromDeaf, store.BrokerId);
            clock.Advance(TimeSpan.FromMinutes(1.5));
            store.Register(deaf with { LeaseUntil = clock.GetUtcNow().AddMinutes(2) });
            Check(Notices(store, deaf.Address).Count == 0 && store.Messages(deaf.Address).Single(r => r.Message.Id == fromDeaf.Id).Status == MessageStatus.Expired,
                "NOTICE_SENDER_CANNOT_RECEIVE: a sender without Receive gets no notice");
            // No Register or Directory call here: either would trim the lapsed record before the expiry commits.
            clock.Advance(TimeSpan.FromMinutes(4));
            Check(store.Messages(recipient.Address).Single(r => r.Message.Id == fromLapsed.Id).Status == MessageStatus.Expired &&
                Notices(store, lapsed.Address).Count == 0,
                "NOTICE_SENDER_LAPSED: a native sender whose lease lapsed gets no notice while it is gone");

            // The notice is for the exact address: a new incarnation of the same session ID never receives the old one's.
            var replacement = lapsed with { Address = lapsed.Address with { Incarnation = Guid.NewGuid() }, LeaseUntil = clock.GetUtcNow().AddMinutes(2) };
            store.Register(replacement);
            store.BeginDelivery();
            Check(Notices(store, replacement.Address).Count == 0, "NOTICE_EXACT_INCARNATION: another incarnation never receives the notice");
            store.Register(lapsed with { LeaseUntil = clock.GetUtcNow().AddMinutes(2) });
            store.BeginDelivery();
            Check(Notices(store, lapsed.Address).Count(n => About(n, fromLapsed)) == 1,
                "NOTICE_EXACT_RETURN: the original incarnation gets it when it is back");

            // Nothing a caller or peer submits can be, or be addressed to, the notice sender.
            var forged = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null,
                new(store.BrokerId, BrokerStore.NoticeSessionId, Guid.NewGuid()), sender.Address, "A forged notice.",
                clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(5));
            Refuses("reserved_session", () => store.Accept(forged, store.BrokerId), "NOTICE_RESERVED_SENDER: a forged notice is refused");
            Refuses("reserved_session", () => store.Accept(forged with { Sender = sender.Address, Recipient = forged.Sender }, store.BrokerId),
                "NOTICE_RESERVED_RECIPIENT: nothing can be sent to the notice sender");
            Refuses("reserved_session", () => store.Send(forged.Sender, sender.Address, Guid.NewGuid(), "Forged.", null, 0, store.BrokerId),
                "NOTICE_RESERVED_SEND: send cannot claim the notice sender either");
            Refuses("reserved_session", () => store.Register(Mailbox(store, clock, "x") with
                { Address = new(store.BrokerId, BrokerStore.NoticeSessionId, Guid.NewGuid()) }),
                "NOTICE_RESERVED_REGISTER: no session can register as the notice sender");
        }

        // A full sender queue defers the notice instead of failing the expiry or dropping it.
        clock = new Clock();
        using (var small = new BrokerStore(Path.Combine(root, "sender-notice-queue"), new(MaximumPendingPerSession: 1), clock))
        {
            var sender = Mailbox(small, clock, "busy"); var recipient = Mailbox(small, clock, "slow");
            small.Register(sender); small.Register(recipient);
            var blocker = Message(recipient, sender, clock, TimeSpan.FromHours(2));
            small.Accept(blocker, small.BrokerId);
            var expiring = Message(sender, recipient, clock, TimeSpan.FromMinutes(1));
            small.Accept(expiring, small.BrokerId);
            clock.Advance(TimeSpan.FromMinutes(2));
            small.Register(Renewed(sender, clock));
            Check(small.Messages(sender.Address).Single(r => r.Message.Id == expiring.Id).Status == MessageStatus.Expired &&
                Notices(small, sender.Address).Count == 0,
                "NOTICE_QUEUE_BOUND: a sender at its pending limit gets no notice yet and the expiry still commits");
            clock.Advance(TimeSpan.FromMinutes(30));
            small.Register(Renewed(sender, clock)); small.Register(Renewed(recipient, clock));
            small.RecordRecipientReceipt(blocker.Id, sender.Address, small.BrokerId, MessageStatus.Delivered);
            var deferred = Notices(small, sender.Address).Where(n => About(n, expiring)).ToList();
            Check(deferred.Count == 1, "NOTICE_QUEUE_DEFERRED: the deferred notice arrives once the sender's queue has room");
            Check(deferred[0].Message.CreatedAt == clock.GetUtcNow() && deferred[0].Message.ExpiresAt == clock.GetUtcNow().AddMinutes(1440),
                "NOTICE_TTL_FROM_ADMISSION: a deferred notice's lifetime starts when it is admitted, not when it became owed");
        }

        // An owed notice lasts a day: a sender back after that gets nothing, even when another commit runs.
        clock = new Clock();
        using (var store = new BrokerStore(Path.Combine(root, "sender-notice-grace"), clock: clock))
        {
            var sender = Mailbox(store, clock, "late"); var recipient = Mailbox(store, clock, "other");
            store.Register(sender); store.Register(recipient);
            store.Accept(Message(sender, recipient, clock, TimeSpan.FromMinutes(1)), store.BrokerId);
            store.Disconnect(sender.Address);
            clock.Advance(TimeSpan.FromMinutes(2));
            store.Register(Renewed(recipient, clock));
            store.Messages(recipient.Address);
            clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
            store.Register(Renewed(recipient, clock));
            store.Messages(recipient.Address);   // the commit that prunes the source, while its sender is still away
            store.Register(Renewed(sender, clock));
            store.Accept(Message(recipient, sender, clock, TimeSpan.FromMinutes(30)), store.BrokerId);
            store.BeginDelivery();
            Check(Notices(store, sender.Address).Count == 0, "NOTICE_OWED_GRACE: a notice still owed when its source is pruned is dropped, not sent late");
        }

        // A full store defers a notice until a slot frees; one still deferred when its source's retention ends is dropped,
        // even for a sender that stayed addressable (documented loss condition). Two messages from a paired peer fill the
        // store without owing notices of their own.
        foreach (bool freed in new[] { true, false })
        {
            clock = new Clock();
            using var full = new BrokerStore(Path.Combine(root, "sender-notice-capacity-" + freed), new(MaximumMessages: 3), clock);
            var peer = new PeerTrust(Guid.NewGuid(), "Synthetic peer", "https://192.0.2.9/", new string('D', 64), clock.GetUtcNow());
            full.AuthorizePeer(peer);
            var sender = Mailbox(full, clock, "full-sender"); var local = Mailbox(full, clock, "full-local"); var filler = Mailbox(full, clock, "full-filler");
            full.Register(sender); full.Register(local); full.Register(filler);
            var remote = new SessionAddress(peer.BrokerId, "remote", Guid.NewGuid());
            full.Accept(new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, remote, local.Address, "From a paired broker.",
                clock.GetUtcNow(), clock.GetUtcNow().AddHours(24)), peer.BrokerId);
            var fill = Message(filler, local, clock, TimeSpan.FromHours(24));
            full.Accept(fill, full.BrokerId);
            var squeezed = Message(sender, local, clock, TimeSpan.FromMinutes(1));
            full.Accept(squeezed, full.BrokerId);
            clock.Advance(TimeSpan.FromMinutes(2));
            full.Register(Renewed(sender, clock)); full.Register(Renewed(local, clock)); full.Register(Renewed(filler, clock));
            Check(Notices(full, sender.Address).Count == 0, "A full store defers the notice although its sender stays addressable.");
            if (freed)
            {
                full.RecordRecipientReceipt(fill.Id, local.Address, full.BrokerId, MessageStatus.Delivered);
                Check(Notices(full, sender.Address).Count(n => About(n, squeezed)) == 1,
                    "NOTICE_CAPACITY_DEFERRED: a notice deferred by a full store is admitted once a slot frees");
                continue;
            }
            clock.Advance(TimeSpan.FromDays(1));   // the source's retention ends (deadline plus one day), freeing its slot
            full.Register(Renewed(sender, clock)); full.Register(Renewed(local, clock));
            full.BeginDelivery();
            Check(Notices(full, sender.Address).Count(n => About(n, squeezed)) == 0,
                "NOTICE_CAPACITY_LOSS: a notice still deferred when its source's retention ends is dropped, never sent late");
        }

        // The tombstone bound counts the tombstones this very commit writes: a refusal's own tombstone plus its notice must
        // fit, or the store fails to reopen (Codex 01a18357's counterexample at MaximumTombstones 1).
        foreach (int bound in new[] { 1, 3 })
        {
            clock = new Clock();
            path = Path.Combine(root, "sender-notice-tombstone-bound-" + bound);
            SessionAddress boundSender;
            using (var store = new BrokerStore(path, new(MaximumTombstones: bound), clock))
            {
                var sender = Mailbox(store, clock, "bound-sender"); var recipient = Mailbox(store, clock, "bound-recipient");
                store.Register(sender); store.Register(recipient); boundSender = sender.Address;
                var refused = Message(sender, recipient, clock, TimeSpan.FromMinutes(30));
                store.Accept(refused, store.BrokerId);
                store.RecordRecipientReceipt(refused.Id, recipient.Address, store.BrokerId, MessageStatus.Refused);
                var notices = Notices(store, sender.Address);
                Check(notices.Count == (bound == 1 ? 0 : 1), "NOTICE_TOMBSTONE_BOUND: a notice is admitted only when it and this commit's tombstones fit the bound");
                foreach (var notice in notices) store.RecordRecipientReceipt(notice.Message.Id, sender.Address, store.BrokerId, MessageStatus.Delivered);
            }
            bool reopened;
            try { using var again = new BrokerStore(path, new(MaximumTombstones: bound), clock); reopened = Notices(again, boundSender).Count == (bound == 1 ? 0 : 1); }
            catch (InvalidDataException) { reopened = false; }
            Check(reopened, "NOTICE_TOMBSTONE_REOPEN: the store reopens after a refusal and its acknowledged notice at a small tombstone bound");
        }

        // Tombstones past their grace free room on the next delivery pass, not only after some unrelated receipt read
        // (Codex 01a18357's witness against e0406c0). Observed through BeginDelivery alone: an inbox read would prune too.
        clock = new Clock();
        using (var store = new BrokerStore(Path.Combine(root, "sender-notice-tombstone-prune"), new(MaximumTombstones: 3), clock))
        {
            var mailbox = Mailbox(store, clock, "prune-sender"); var recipient = Mailbox(store, clock, "prune-recipient");
            var absent = Native(store, clock, "claude:prune-absent");
            store.Register(mailbox); store.Register(recipient); store.Register(absent);
            foreach (var _ in Enumerable.Range(0, 2))
            {
                var shortLived = Message(mailbox, recipient, clock, TimeSpan.FromMinutes(1));
                store.Accept(shortLived, store.BrokerId);
                store.RecordRecipientReceipt(shortLived.Id, recipient.Address, store.BrokerId, MessageStatus.Delivered);
            }
            var refused = Message(absent, recipient, clock, TimeSpan.FromHours(24));
            store.Accept(refused, store.BrokerId);
            store.Disconnect(absent.Address);
            store.RecordRecipientReceipt(refused.Id, recipient.Address, store.BrokerId, MessageStatus.Refused);
            clock.Advance(TimeSpan.FromHours(25));   // the two Delivered tombstones are past grace; the refusal is not
            store.Register(absent with { LeaseUntil = clock.GetUtcNow().AddMinutes(2) });
            store.BeginDelivery();
            var claimed = store.BeginDelivery();
            Check(claimed?.Message.Sender.SessionId == BrokerStore.NoticeSessionId && claimed.Message.Recipient == absent.Address,
                "NOTICE_TOMBSTONES_PRUNED: tombstones past their grace free room on the next delivery pass");
        }

        // A notice is held to the configured text bound, or the snapshot would not load again.
        clock = new Clock();
        path = Path.Combine(root, "sender-notice-text-bound");
        SessionAddress textSender;
        using (var store = new BrokerStore(path, new(MaximumTextBytes: 64), clock))
        {
            var sender = Mailbox(store, clock, "text-sender"); var recipient = Mailbox(store, clock, "text-recipient");
            store.Register(sender); store.Register(recipient); textSender = sender.Address;
            var refused = Message(sender, recipient, clock, TimeSpan.FromMinutes(30)) with { Text = "Short." };
            store.Accept(refused, store.BrokerId);
            store.RecordRecipientReceipt(refused.Id, recipient.Address, store.BrokerId, MessageStatus.Refused);
            var notice = Notices(store, sender.Address).Single();
            Check(System.Text.Encoding.UTF8.GetByteCount(notice.Message.Text) <= 64 && notice.Message.Text.StartsWith("AgentBroker delivery notice", StringComparison.Ordinal),
                "NOTICE_TEXT_FITS: a notice is trimmed to the configured text bound, keeping its start");
        }
        bool textReopened;
        try { using var again = new BrokerStore(path, new(MaximumTextBytes: 64), clock); textReopened = Notices(again, textSender).Count == 1; }
        catch (InvalidDataException) { textReopened = false; }
        Check(textReopened, "NOTICE_TEXT_REOPEN: the store reopens with a notice under a small text bound");

        // Message IDs of version 8 are the broker's own, so no submitted message can take a notice's ID and suppress it.
        clock = new Clock();
        using (var store = new BrokerStore(Path.Combine(root, "sender-notice-reserved-id"), clock: clock))
        {
            var sender = Mailbox(store, clock, "id-sender"); var recipient = Mailbox(store, clock, "id-recipient");
            store.Register(sender); store.Register(recipient);
            // The squatter takes the exact ID the broker would give a notice about another message.
            var original = Message(sender, recipient, clock, TimeSpan.FromMinutes(5));
            var squatter = Message(sender, recipient, clock, TimeSpan.FromMinutes(5)) with { Id = BrokerStore.NoticeId(original.Id) };
            Refuses("reserved_message_id", () => store.Accept(squatter, store.BrokerId), "NOTICE_RESERVED_ID: a submitted message holding a notice's ID is refused");
            Refuses("reserved_message_id", () => store.Send(sender.Address, recipient.Address, squatter.Id, "Squatting.", null, 0, store.BrokerId),
                "NOTICE_RESERVED_ID_SEND: send refuses a notice's ID too");
            // Other version-8 IDs stay usable: the channel bridge derives its reply IDs that way.
            var bytes = Guid.NewGuid().ToByteArray();
            bytes[7] = (byte)(bytes[7] & 0x0F | 0x80);
            var versionEight = Message(sender, recipient, clock, TimeSpan.FromMinutes(5)) with { Id = new Guid(bytes) };
            Check(versionEight.Id.Version == 8 && !BrokerStore.IsNoticeId(versionEight.Id) && store.Accept(versionEight, store.BrokerId).Status == MessageStatus.Accepted,
                "NOTICE_V8_ALLOWED: a version-8 ID without the notice marker is still accepted");
        }

        // A deferred notice is never sent after its source's retention, even when no commit ran in between.
        clock = new Clock();
        using (var store = new BrokerStore(Path.Combine(root, "sender-notice-late"), clock: clock))
        {
            var sender = Mailbox(store, clock, "late-sender"); var recipient = Mailbox(store, clock, "late-recipient");
            store.Register(sender); store.Register(recipient);
            var refused = Message(sender, recipient, clock, TimeSpan.FromMinutes(30));
            store.Accept(refused, store.BrokerId);
            store.Disconnect(sender.Address);
            store.RecordRecipientReceipt(refused.Id, recipient.Address, store.BrokerId, MessageStatus.Refused);
            clock.Advance(TimeSpan.FromDays(2));   // no store call at all in between
            store.Register(Renewed(sender, clock)); store.Register(Renewed(recipient, clock));
            store.BeginDelivery();
            store.Accept(Message(recipient, sender, clock, TimeSpan.FromMinutes(30)), store.BrokerId);   // a commit after the return
            Check(Notices(store, sender.Address).Count == 0, "NOTICE_LATE_NEVER: a notice whose source's retention ended is never sent late");
        }

        // A short configured default never shortens a notice, so it outlives an early-refused source with a long deadline.
        clock = new Clock();
        path = Path.Combine(root, "sender-notice-short-default");
        SessionAddress shortSender;
        using (var store = new BrokerStore(path, new(DefaultMessageLifetimeMinutes: 1), clock))
        {
            var sender = Mailbox(store, clock, "short-sender"); var recipient = Mailbox(store, clock, "short-recipient");
            store.Register(sender); store.Register(recipient);
            shortSender = sender.Address;
            var longLived = Message(sender, recipient, clock, TimeSpan.FromHours(24));
            store.Accept(longLived, store.BrokerId);
            clock.Advance(TimeSpan.FromMinutes(5));
            store.Register(Renewed(recipient, clock)); store.Register(Renewed(sender, clock));
            store.RecordRecipientReceipt(longLived.Id, recipient.Address, store.BrokerId, MessageStatus.Refused);
            var notice = Notices(store, sender.Address).Single();
            Check(notice.Message.ExpiresAt == clock.GetUtcNow() + BrokerLimits.MaximumLifetime,
                "NOTICE_MAX_LIFETIME: a notice gets the maximum lifetime whatever the configured default");
            store.RecordRecipientReceipt(notice.Message.Id, sender.Address, store.BrokerId, MessageStatus.Delivered);
        }
        clock.Advance(TimeSpan.FromHours(47));   // past a one-minute notice's tombstone, before the source's tombstone goes
        using (var store = new BrokerStore(path, new(DefaultMessageLifetimeMinutes: 1), clock))
        {
            store.Register(new(shortSender, "Mailbox again", "mcp", SessionCapabilities.Receive | SessionCapabilities.Reply,
                SessionActivity.Idle, clock.GetUtcNow().AddMinutes(1)));
            store.BeginDelivery();
            var notices = Notices(store, shortSender);
            Check(notices.Count == 1 && notices[0].Status == MessageStatus.Delivered,
                "NOTICE_SHORT_DEFAULT: with a short default and a long-lived refused source, a reopen never recreates the notice");
        }

        // No starvation: 200 notices owed to a sender that is away, then one owed to a sender that is present. The owed set
        // has no cap (it is bounded by the retained source records), so the count only needs to make starvation visible:
        // 1,030 sources cost about 56 seconds here, because every Accept rewrites a growing snapshot with write-through.
        clock = new Clock();
        using (var many = new BrokerStore(Path.Combine(root, "sender-notice-many"), new(MaximumPendingPerSession: 400), clock))
        {
            var away = Native(many, clock, "claude:away"); var present = Mailbox(many, clock, "present"); var sink = Mailbox(many, clock, "sink");
            many.Register(away); many.Register(present); many.Register(sink);
            foreach (var _ in Enumerable.Range(0, 200)) many.Accept(Message(away, sink, clock, TimeSpan.FromMinutes(1)), many.BrokerId);
            clock.Advance(TimeSpan.FromMinutes(3));   // the native lease (two minutes) has lapsed
            many.Messages(sink.Address);
            many.Register(Renewed(present, clock)); many.Register(Renewed(sink, clock));
            var last = Message(present, sink, clock, TimeSpan.FromMinutes(1));
            many.Accept(last, many.BrokerId);
            clock.Advance(TimeSpan.FromMinutes(2));
            many.Register(Renewed(present, clock)); many.Register(Renewed(sink, clock));
            Check(Notices(many, present.Address).Count(n => About(n, last)) == 1,
                "NOTICE_NO_STARVATION: 200 notices owed to an absent sender never delay one owed to a present sender");
            many.Register(away with { LeaseUntil = clock.GetUtcNow().AddMinutes(2) });
            many.BeginDelivery();
            Check(Notices(many, away.Address).Count == 200, "NOTICE_ALL_ADMITTED: every owed notice is admitted in one pass when its sender is back");
        }

        // Scale without a slow setup: one valid schema-1 snapshot holding 1,025 Expired sources from an absent sender and,
        // newest, one from a present sender, opened by the real store. 1,025 is past the earlier 1,024 cap, so a truncated
        // owed set or admission pass shows here; the absent prefix sorts first, so starvation would show too.
        clock = new Clock();
        path = Path.Combine(root, "sender-notice-scale");
        System.IO.Directory.CreateDirectory(path);
        var scaleBroker = Guid.NewGuid();
        var awayAddress = new SessionAddress(scaleBroker, "claude:scale-away", Guid.NewGuid());
        var presentAddress = new SessionAddress(scaleBroker, "mcp:scale-present", Guid.NewGuid());
        var sinkAddress = new SessionAddress(scaleBroker, "mcp:scale-sink", Guid.NewGuid());
        MessageReceipt Ended(SessionAddress from, int minutesAgo)
        {
            var deadline = clock.GetUtcNow().AddMinutes(-minutesAgo);
            return new(new(Guid.NewGuid(), Guid.NewGuid(), null, from, sinkAddress, "A harmless synthetic source.", deadline.AddMinutes(-60), deadline),
                MessageStatus.Expired, deadline, "expired");
        }
        var sources = Enumerable.Range(0, 1025).Select(index => Ended(awayAddress, 61 + index % 60)).Append(Ended(presentAddress, 1)).ToList();
        File.WriteAllText(Path.Combine(path, "broker-state.json"), System.Text.Json.JsonSerializer.Serialize(
            new { Version = 1, BrokerId = scaleBroker, Peers = Array.Empty<PeerTrust>(), Messages = sources }));
        using (var store = new BrokerStore(path, new(MaximumPendingPerSession: 2000), clock))
        {
            store.Register(new(presentAddress, "Present", "mcp", SessionCapabilities.Receive | SessionCapabilities.Reply,
                SessionActivity.Idle, clock.GetUtcNow().AddMinutes(1)));
            store.BeginDelivery();
            Check(Notices(store, presentAddress).Count == 1,
                "NOTICE_SCALE_NO_STARVATION: past 1,024 owed notices to an absent sender, a present sender's notice is still admitted");
            store.Register(new(awayAddress, "Away, back", "claude-native", SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply,
                SessionActivity.Idle, clock.GetUtcNow().AddMinutes(2)));
            store.BeginDelivery();
            Check(Notices(store, awayAddress).Count == 1025, "NOTICE_SCALE_ALL: all 1,025 rebuilt notices are admitted when their sender is back");
        }

        // Expiry during downtime: the first pass after a restart commits it before discovery re-registers the sender.
        clock = new Clock();
        path = Path.Combine(root, "sender-notice-restart");
        SessionAddress restartSender, restartMailbox;
        BrokerMessage early, refusedAway, pending;
        using (var store = new BrokerStore(path, clock: clock))
        {
            var sender = Native(store, clock, "claude:restart-sender"); var mailbox = Mailbox(store, clock, "restart-mailbox");
            store.Register(sender); store.Register(mailbox);
            restartSender = sender.Address; restartMailbox = mailbox.Address;
            early = Message(sender, mailbox, clock, TimeSpan.FromMinutes(1));
            refusedAway = Message(sender, mailbox, clock, TimeSpan.FromMinutes(30));
            pending = Message(sender, mailbox, clock, TimeSpan.FromMinutes(10));
            store.Accept(early, store.BrokerId); store.Accept(refusedAway, store.BrokerId); store.Accept(pending, store.BrokerId);
            clock.Advance(TimeSpan.FromMinutes(3));
            store.Register(Renewed(mailbox, clock));
            store.RecordRecipientReceipt(refusedAway.Id, mailbox.Address, store.BrokerId, MessageStatus.Refused);
            Check(store.Messages(mailbox.Address).Single(r => r.Message.Id == early.Id).Status == MessageStatus.Expired &&
                store.FindReceipt(refusedAway.Id) is { Status: MessageStatus.Refused, Message.Text: "" } && Notices(store, sender.Address).Count == 0,
                "Before the restart, one message expired and one was refused (now a tombstone) while their native sender was away.");
        }
        clock.Advance(TimeSpan.FromMinutes(20));
        using (var store = new BrokerStore(path, clock: clock))
        {
            store.BeginDelivery();
            Check(store.Messages(restartMailbox).Single(r => r.Message.Id == pending.Id).Status == MessageStatus.Expired &&
                Notices(store, restartSender).Count == 0, "The pending message's expiry commits before its sender is rediscovered.");
            store.Register(new(restartSender, "Native back", "claude-native",
                SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply, SessionActivity.Idle, clock.GetUtcNow().AddMinutes(2)));
            store.BeginDelivery();
            var notices = Notices(store, restartSender);
            Check(notices.Count(n => About(n, pending)) == 1, "NOTICE_AFTER_RESTART: an expiry first committed after a restart reaches the rediscovered sender");
            Check(notices.Count(n => About(n, early)) == 1, "NOTICE_REBUILD_EXPIRED: an Expired record owed before the restart is rebuilt and admitted");
            Check(notices.Count(n => About(n, refusedAway)) == 1 &&
                notices.Single(n => About(n, refusedAway)).Message.Text.Contains("was refused by its recipient", StringComparison.Ordinal),
                "NOTICE_REBUILD_REFUSED: a Refused tombstone owed before the restart is rebuilt and admitted");
            Check(notices.Count == 3, "NOTICE_REBUILD_ONCE: the rebuild adds nothing beyond the three owed notices");
        }
        return passed;
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
