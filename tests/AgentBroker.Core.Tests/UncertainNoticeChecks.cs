using AgentBroker.Core;

internal static class UncertainNoticeChecks
{
    public static int Run(string root, string? legacyAssembly = null, string? legacySha256 = null)
    {
        Console.WriteLine("WITNESS: UNCERTAIN_NOTICE_EXECUTED");
        if (legacyAssembly is null && legacySha256 is null)
            Console.WriteLine("COMPATIBILITY_NOT_RUN: UNCERTAIN_NOTICE_191 is opt-in; supply the pinned baseline assembly and expected SHA-256. Ordinary notice checks do not qualify rollback compatibility.");
        else
        {
            if (legacyAssembly is null || !Path.IsPathFullyQualified(legacyAssembly) || !File.Exists(legacyAssembly) ||
                legacySha256 is null || legacySha256.Length != 64 || !legacySha256.All(Uri.IsHexDigit) ||
                !Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(legacyAssembly))).Equals(legacySha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("UNCERTAIN_NOTICE_LEGACY_FIXTURE_HASH: opt-in compatibility requires an existing absolute assembly path and its exact expected SHA-256.");
            Console.WriteLine("UNCERTAIN_NOTICE_LEGACY_FIXTURE_SHA256=" + legacySha256.ToUpperInvariant());
        }
        int passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed++;
        }
        foreach (string reason in new[] { "native_uncertain_timeout", "native_uncertain_raised_cancelled", "submission_unconfirmed",
            "submission_failed", "peer_answer_invalid", "peer_revoked_during_submission" })
        {
            var clock = new Clock();
            using var store = new BrokerStore(Path.Combine(root, "notice-uncertain-" + reason), clock: clock);
            var sender = Mailbox(store, clock, "sender"); var recipient = Native(store, clock, "recipient");
            store.Register(sender); store.Register(recipient);
            var message = Message(sender, recipient, clock);
            store.Accept(message, store.BrokerId);
            Check(store.BeginDelivery()?.Message.Id == message.Id, "UNCERTAIN_NOTICE_ATTEMPT_WITNESS: the real source is claimed");
            store.CompleteDelivery(store.FindReceipt(message.Id)!, MessageStatus.Uncertain, reason);
            Check(Notices(store, sender.Address).Count == 1,
                "UNCERTAIN_NOTICE_EXACT_SENDER_CREATED: entering Uncertain creates one advisory for its exact original sender");
            var notice = Notices(store, sender.Address).Single();
            Check(notice.Message.Id != BrokerStore.NoticeId(message.Id) && BrokerStore.IsNoticeId(notice.Message.Id) &&
                notice.Message.Recipient == sender.Address && notice.Message.RemainingReplies == 0 && notice.Message.ReplyTo is null,
                "UNCERTAIN_NOTICE_DOMAIN: advisory has a distinct reserved ID and exact original sender");
            Check(notice.Message.Text.Contains("became Uncertain", StringComparison.Ordinal) &&
                notice.Message.Text.Contains(reason, StringComparison.Ordinal) && notice.Message.Text.Contains(message.Id.ToString(), StringComparison.Ordinal) &&
                notice.Message.Text.Contains("historical event", StringComparison.Ordinal) && notice.Message.Text.Contains("may have reached", StringComparison.Ordinal) &&
                notice.Message.Text.Contains("get_message_receipt", StringComparison.Ordinal) && notice.Message.Text.Contains("Do not resend automatically", StringComparison.Ordinal) &&
                notice.Message.Text.Contains(recipient.Address.Incarnation.ToString(), StringComparison.Ordinal) &&
                !notice.Message.Text.Contains(message.Text, StringComparison.Ordinal),
                "UNCERTAIN_NOTICE_HONEST_TEXT: reason, exact target and inspect-before-resend guidance retain no source payload");
            Check(File.ReadAllText(Path.Combine(root, "notice-uncertain-" + reason, "broker-state.json")).Split(message.Text).Length == 2,
                "UNCERTAIN_NOTICE_PAYLOAD_ONCE: only the ordinary retained source holds its payload");
            for (int i = 0; i < 3; i++) { store.FindReceipt(message.Id); store.BeginDelivery(); }
            Check(Notices(store, sender.Address).Count == 1 && store.FindReceipt(message.Id) is { Status: MessageStatus.Uncertain, Attempts: 1 },
                "UNCERTAIN_NOTICE_NO_REPLAY: reads and delivery passes neither repeat the notice nor retry uncertain input");
            store.RecordRecipientReceipt(notice.Message.Id, sender.Address, store.BrokerId, MessageStatus.Delivered);
            store.RecordRecipientReceipt(message.Id, recipient.Address, store.BrokerId, MessageStatus.Refused);
            Check(Notices(store, sender.Address).Count == 2 && store.FindReceipt(BrokerStore.NoticeId(message.Id)) is { Status: MessageStatus.Accepted },
                "UNCERTAIN_NOTICE_TERMINAL_INDEPENDENT: the historical advisory never consumes the terminal notice");
        }
        foreach (var resolution in new[] { MessageStatus.Delivered, MessageStatus.Held, MessageStatus.Refused })
        {
            var clock = new Clock();
            using var store = new BrokerStore(Path.Combine(root, "notice-uncertain-resolved-" + resolution), clock: clock);
            var sender = Mailbox(store, clock, "sender"); var recipient = Native(store, clock, "recipient");
            store.Register(sender); store.Register(recipient);
            var message = Message(sender, recipient, clock); store.Accept(message, store.BrokerId); store.BeginDelivery();
            store.Disconnect(sender.Address);
            store.CompleteDelivery(store.FindReceipt(message.Id)!, MessageStatus.Uncertain, "native_uncertain_timeout");
            Check(Notices(store, sender.Address).Count == 0, "UNCERTAIN_NOTICE_DEFERRED_WITNESS: absent exact sender keeps an obligation");
            var obligations = (IReadOnlyDictionary<Guid, MessageReceipt>)typeof(BrokerStore)
                .GetField("owed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(store)!;
            Check(obligations.Count == 1 && obligations.Values.All(receipt => receipt.Message.Text.Length == 0),
                "UNCERTAIN_NOTICE_OBLIGATION_NO_TEXT: the actual deferred obligation retains metadata without source payload");
            store.RecordRecipientReceipt(message.Id, recipient.Address, store.BrokerId, resolution);
            store.Register(Renew(sender, clock)); store.BeginDelivery();
            var notices = Notices(store, sender.Address);
            Check(notices.Count == (resolution == MessageStatus.Refused ? 1 : 0) &&
                notices.All(notice => notice.Message.Id == BrokerStore.NoticeId(message.Id)) && store.DroppedNotices().Total == 0,
                "UNCERTAIN_NOTICE_RESOLVED_CANCELS: resolution cancels only unadmitted uncertainty without reporting a lost notice");
        }
        {
            var clock = new Clock();
            using var store = new BrokerStore(Path.Combine(root, "notice-uncertain-capacity"),
                new(MaximumMessages: 2, MaximumPendingPerSession: 1, MaximumTombstones: 3), clock);
            var sender = Mailbox(store, clock, "sender"); var recipient = Native(store, clock, "recipient");
            store.Register(sender); store.Register(recipient);
            var blocker = Message(recipient, sender, clock); store.Accept(blocker, store.BrokerId);
            var message = Message(sender, recipient, clock); store.Accept(message, store.BrokerId); store.BeginDelivery();
            store.CompleteDelivery(store.FindReceipt(message.Id)!, MessageStatus.Uncertain, "submission_unconfirmed");
            Check(Notices(store, sender.Address).Count == 0, "UNCERTAIN_NOTICE_CAPACITY_DEFERRED: full store and exact sender queue defer advisory");
            store.RecordRecipientReceipt(blocker.Id, sender.Address, store.BrokerId, MessageStatus.Delivered);
            Check(Notices(store, sender.Address).Count == 1, "UNCERTAIN_NOTICE_CAPACITY_RETURN: freeing real capacity admits the owed advisory");
        }
        {
            var clock = new Clock(); string path = Path.Combine(root, "notice-uncertain-restart");
            BrokerSession sender, recipient; Guid source, noticeId;
            using (var store = new BrokerStore(path, clock: clock))
            {
                sender = Mailbox(store, clock, "sender"); recipient = Native(store, clock, "recipient");
                store.Register(sender); store.Register(recipient);
                var message = Message(sender, recipient, clock); source = message.Id;
                store.Accept(message, store.BrokerId); store.BeginDelivery();
                // A durable in-flight source becomes Uncertain on the next real store opening.
            }
            using (var store = new BrokerStore(path, clock: clock))
            {
                var successor = sender with { Address = sender.Address with { Incarnation = Guid.NewGuid() } };
                store.Register(successor); store.Register(recipient); store.BeginDelivery();
                Check(Notices(store, successor.Address).Count == 0 && store.FindReceipt(source)?.Reason == "restart_during_submission",
                    "UNCERTAIN_NOTICE_EXACT_RESTART: restarted input is uncertain and a successor receives no predecessor notice");
                store.Register(sender); store.BeginDelivery();
                Check(Notices(store, sender.Address).Count == 1,
                    "UNCERTAIN_NOTICE_RESTART_REBUILD: retained uncertainty rebuilds one obligation for the original sender after restart");
                var notice = Notices(store, sender.Address).Single(); noticeId = notice.Message.Id;
                store.RecordRecipientReceipt(noticeId, sender.Address, store.BrokerId, MessageStatus.Delivered);
            }
            if (legacyAssembly is not null)
            {
                var legacy = LegacyPass(legacyAssembly, path, clock, sender, recipient);
                Console.WriteLine("WITNESS: UNCERTAIN_NOTICE_191_ROLLBACK_EXECUTED");
                Check(legacy.Count(receipt => receipt.Message.Sender.SessionId == BrokerStore.NoticeSessionId) == 1,
                    "UNCERTAIN_NOTICE_ROLLBACK: actual 1.9.1 reader retains the advisory tombstone and invents no additional notice");
            }
            using (var store = new BrokerStore(path, clock: clock))
            {
                store.Register(sender); store.Register(recipient); store.BeginDelivery();
                Check(Notices(store, sender.Address).Count == 1 && store.FindReceipt(noticeId)?.Status == MessageStatus.Delivered,
                    "UNCERTAIN_NOTICE_DURABLE_DEDUPE: restart after acknowledgement preserves one historical event");
                store.RecordRecipientReceipt(source, recipient.Address, store.BrokerId, MessageStatus.Refused);
            }
            if (legacyAssembly is not null)
            {
                var legacy = LegacyPass(legacyAssembly, path, clock, sender, recipient);
                Console.WriteLine("WITNESS: UNCERTAIN_NOTICE_191_TERMINAL_ROLLBACK_EXECUTED");
                Check(legacy.Count(receipt => receipt.Message.Sender.SessionId == BrokerStore.NoticeSessionId) == 2,
                    "UNCERTAIN_NOTICE_TERMINAL_ROLLBACK: actual 1.9.1 reader retains independent advisory and legacy terminal notice without duplicates");
            }
            using (var store = new BrokerStore(path, clock: clock))
            {
                store.Register(sender); store.BeginDelivery();
                Check(Notices(store, sender.Address).Count == 2,
                    "UNCERTAIN_NOTICE_ROLLFORWARD: advisory and terminal notice remain singly represented after rollback");
            }
        }
        {
            // A source in flight at a stop and reopened after its retention ended (post-push audit of e398eba2, 1.10.1): the
            // recovery commit drops its owed advisory once (sender_unavailable), and the next delivery pass, which prunes the
            // record, must not re-owe and drop the same advisory again.
            var clock = new Clock(); string path = Path.Combine(root, "notice-uncertain-reopen-past-retention");
            BrokerSession sender, recipient;
            using (var store = new BrokerStore(path, clock: clock))
            {
                sender = Mailbox(store, clock, "sender"); recipient = Native(store, clock, "recipient");
                store.Register(sender); store.Register(recipient);
                store.Accept(Message(sender, recipient, clock), store.BrokerId); store.BeginDelivery();
            }
            clock.Advance(TimeSpan.FromHours(49));
            using (var store = new BrokerStore(path, clock: clock))
            {
                var recovered = store.DroppedNotices();
                store.BeginDelivery(); store.BeginDelivery();
                var drops = store.DroppedNotices();
                Console.WriteLine($"NOTE UNCERTAIN_NOTICE_REOPEN_PAST_RETENTION recoveredDrops={recovered.Total} afterPasses={drops.Total}");
                Check(recovered.SenderUnavailable == 1 && drops.SenderUnavailable == 1 && drops.Total == 1 &&
                    Notices(store, sender.Address).Count == 0,
                    "UNCERTAIN_NOTICE_REOPEN_PAST_RETENTION: a source recovered after its retention ended drops its advisory once, not again on the next pass");
            }
        }
        {
            var clock = new Clock();
            using var store = new BrokerStore(Path.Combine(root, "notice-uncertain-deadline"), clock: clock);
            var sender = Mailbox(store, clock, "sender"); var recipient = Native(store, clock, "recipient");
            store.Register(sender); store.Register(recipient);
            var message = Message(sender, recipient, clock) with { ExpiresAt = clock.GetUtcNow().AddMinutes(2) };
            store.Accept(message, store.BrokerId); store.BeginDelivery();
            store.CompleteDelivery(store.FindReceipt(message.Id)!, MessageStatus.Submitted, "native_admission_unconfirmed");
            clock.Advance(TimeSpan.FromMinutes(3)); store.Register(Renew(sender, clock));
            Check(store.FindReceipt(message.Id)?.Reason == "receipt_unconfirmed" && Notices(store, sender.Address).Count == 1,
                "UNCERTAIN_NOTICE_DEADLINE: a real submitted deadline transition emits one advisory");
        }
        {
            var clock = new Clock();
            using var store = new BrokerStore(Path.Combine(root, "notice-uncertain-federation"), clock: clock);
            var sender = Mailbox(store, clock, "sender"); var recipient = Native(store, clock, "recipient");
            store.Register(sender); store.Register(recipient);
            Guid peer = Guid.NewGuid(); store.AuthorizePeer(new(peer, "Synthetic peer", "https://10.0.0.2:7433/", new string('A', 64), clock.GetUtcNow()));
            var remote = recipient with { Address = recipient.Address with { BrokerId = peer } };
            var outgoing = Message(sender, remote, clock); store.Accept(outgoing, store.BrokerId); store.BeginDelivery();
            store.CompleteDelivery(store.FindReceipt(outgoing.Id)!, MessageStatus.Uncertain, "submission_unconfirmed");
            var incoming = Message(remote, recipient, clock); store.Accept(incoming, peer); store.BeginDelivery();
            store.CompleteDelivery(store.FindReceipt(incoming.Id)!, MessageStatus.Uncertain, "native_uncertain_timeout");
            Check(Notices(store, sender.Address).Count == 1 && Notices(store, remote.Address).Count == 0,
                "UNCERTAIN_NOTICE_LOCAL_SENDER: outgoing peer uncertainty advises its local sender, never a foreign sender");
            var obligations = (IReadOnlyDictionary<Guid, MessageReceipt>)typeof(BrokerStore)
                .GetField("owed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(store)!;
            Check(obligations.Count == 0,
                "UNCERTAIN_NOTICE_NO_FOREIGN_OBLIGATION: foreign uncertainty creates no deferred obligation even though no foreign session can register");
        }
        {
            var clock = new Clock();
            using var store = new BrokerStore(Path.Combine(root, "notice-uncertain-retention"), clock: clock);
            var sender = Mailbox(store, clock, "sender"); var recipient = Native(store, clock, "recipient");
            store.Register(sender); store.Register(recipient);
            var message = Message(sender, recipient, clock); store.Accept(message, store.BrokerId); store.BeginDelivery();
            store.Disconnect(sender.Address); store.CompleteDelivery(store.FindReceipt(message.Id)!, MessageStatus.Uncertain, "native_uncertain_timeout");
            clock.Advance(TimeSpan.FromHours(49)); store.BeginDelivery();
            store.Register(Renew(sender, clock)); store.BeginDelivery();
            Check(Notices(store, sender.Address).Count == 0 && store.DroppedNotices().SenderUnavailable == 1 && store.FindReceipt(message.Id) is null,
                "UNCERTAIN_NOTICE_RETENTION: an undeliverable advisory drops once at ordinary source retention without retaining payload");
        }
        {
            var clock = new Clock();
            using var store = new BrokerStore(Path.Combine(root, "notice-uncertain-transition-only"), clock: clock);
            var sender = Mailbox(store, clock, "short-sender"); var recipient = Native(store, clock, "recipient");
            var otherSender = Mailbox(store, clock, "other-sender");
            var otherRecipient = Native(store, clock, "other-recipient");
            store.Register(sender); store.Register(recipient); store.Register(otherSender); store.Register(otherRecipient);
            var source = Message(sender, recipient, clock) with { ExpiresAt = clock.GetUtcNow().AddMinutes(1) };
            store.Accept(source, store.BrokerId); var sourceClaim = store.BeginDelivery()!;
            store.Disconnect(sender.Address);
            store.CompleteDelivery(sourceClaim, MessageStatus.Uncertain, "native_uncertain_timeout");
            var first = Message(otherSender, recipient, clock); store.Accept(first, store.BrokerId);
            var firstClaim = store.BeginDelivery()!;
            var second = Message(otherSender, otherRecipient, clock); store.Accept(second, store.BrokerId);
            var secondClaim = store.BeginDelivery()!;
            Check(firstClaim.Message.Id == first.Id && secondClaim.Message.Id == second.Id,
                "UNCERTAIN_NOTICE_TRANSITION_CLAIMS: two distinct real in-flight claims remain available beside the short uncertain source");
            clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(2));
            // CompleteDelivery deliberately does not prune unrelated sources. Each of these is a real different claim,
            // not an invalid repeat completion of the original source, so both commits can encounter its old record.
            store.CompleteDelivery(firstClaim, MessageStatus.Held);
            Check(store.DroppedNotices().SenderUnavailable == 1,
                "UNCERTAIN_NOTICE_TRANSITION_FIRST_DROP: one unavailable advisory is counted when its retention ends");
            store.CompleteDelivery(secondClaim, MessageStatus.Held);
            Check(store.DroppedNotices().SenderUnavailable == 1,
                "UNCERTAIN_NOTICE_TRANSITION_ONLY: an unchanged Uncertain source does not re-owe or recount its dropped advisory");
        }
        {
            var clock = new Clock();
            using var store = new BrokerStore(Path.Combine(root, "notice-uncertain-nonchain"), clock: clock);
            var sender = Native(store, clock, "sender"); var recipient = Mailbox(store, clock, "recipient");
            store.Register(sender); store.Register(recipient);
            var message = Message(sender, recipient, clock); store.Accept(message, store.BrokerId);
            store.RecordRecipientReceipt(message.Id, recipient.Address, store.BrokerId, MessageStatus.Refused);
            var notice = store.BeginDelivery()!;
            store.CompleteDelivery(notice, MessageStatus.Uncertain, "native_uncertain_timeout");
            Check(Notices(store, sender.Address).Count == 1 && store.Messages(notice.Message.Sender).Count == 1,
                "UNCERTAIN_NOTICE_NO_CHAIN_WITNESS: one actual notice became uncertain");
            clock.Advance(TimeSpan.FromHours(49)); store.BeginDelivery();
            Check(store.DroppedNotices().Total == 0,
                "UNCERTAIN_NOTICE_NO_CHAIN: uncertain delivery of a broker notice creates no advisory to the reserved sender");
        }
        {
            var clock = new Clock();
            using var store = new BrokerStore(Path.Combine(root, "notice-uncertain-lifetime"), clock: clock);
            var sender = Mailbox(store, clock, "sender"); var recipient = Native(store, clock, "recipient");
            store.Register(sender); store.Register(recipient);
            var message = Message(sender, recipient, clock); store.Accept(message, store.BrokerId); store.BeginDelivery();
            store.Disconnect(sender.Address); store.CompleteDelivery(store.FindReceipt(message.Id)!, MessageStatus.Uncertain, "native_uncertain_timeout");
            clock.Advance(TimeSpan.FromHours(47)); store.Register(Renew(sender, clock)); store.BeginDelivery();
            var first = Notices(store, sender.Address).Single(); store.RecordRecipientReceipt(first.Message.Id, sender.Address, store.BrokerId, MessageStatus.Delivered);
            clock.Advance(TimeSpan.FromHours(2)); store.FindReceipt(message.Id); store.Register(Renew(recipient, clock));
            var next = store.Send(sender.Address, recipient.Address, message.Id, "A later lifetime.", null, 1440, store.BrokerId);
            var claim = store.BeginDelivery()!;
            Check(claim.Message.Id == next.Message.Id, "UNCERTAIN_NOTICE_REUSE_WITNESS: the pruned source UUID is actually readmitted and claimed");
            store.CompleteDelivery(claim, MessageStatus.Uncertain, "native_uncertain_timeout");
            Check(Notices(store, sender.Address).Count == 2 && Notices(store, sender.Address).Select(notice => notice.Message.Id).Distinct().Count() == 2,
                "UNCERTAIN_NOTICE_LIFETIME_ID: a retained delayed advisory cannot suppress the reused source UUID's new event");
        }
        {
            var clock = new Clock(); string path = Path.Combine(root, "notice-uncertain-atomic");
            using var store = new BrokerStore(path, clock: clock);
            var sender = Mailbox(store, clock, "sender"); var recipient = Native(store, clock, "recipient");
            store.Register(sender); store.Register(recipient);
            var message = Message(sender, recipient, clock); store.Accept(message, store.BrokerId); var claim = store.BeginDelivery()!;
            bool failed = false;
            using (var locked = new FileStream(Path.Combine(path, "broker-state.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { store.CompleteDelivery(claim, MessageStatus.Uncertain, "native_uncertain_timeout"); }
                catch (IOException) { failed = true; }
            }
            Check(failed && store.FindReceipt(message.Id)?.Status == MessageStatus.Submitting && Notices(store, sender.Address).Count == 0,
                "UNCERTAIN_NOTICE_COMMIT_ATOMIC: an actual failed snapshot publishes neither uncertainty nor its advisory");
            store.CompleteDelivery(claim, MessageStatus.Uncertain, "native_uncertain_timeout");
            Check(Notices(store, sender.Address).Count == 1,
                "UNCERTAIN_NOTICE_COMMIT_RETRY: restored durable storage commits the original claim and exactly one advisory");
        }
        {
            var clock = new Clock(); string path = Path.Combine(root, "notice-uncertain-text-bound");
            using var store = new BrokerStore(path, new(MaximumTextBytes: 64, DefaultMessageLifetimeMinutes: 1), clock);
            var sender = Mailbox(store, clock, "sender"); var recipient = Native(store, clock, "recipient");
            store.Register(sender); store.Register(recipient);
            var message = Message(sender, recipient, clock); store.Accept(message, store.BrokerId); var claim = store.BeginDelivery()!;
            store.CompleteDelivery(claim, MessageStatus.Uncertain, "native_uncertain_timeout");
            var notice = Notices(store, sender.Address).Single();
            Check(System.Text.Encoding.UTF8.GetByteCount(notice.Message.Text) <= 64 && notice.Message.ExpiresAt == clock.GetUtcNow().AddHours(24),
                "UNCERTAIN_NOTICE_TEXT_AND_TTL: advisory obeys the real text cap and uses maximum lifetime instead of configured default");
        }
        return passed + TerminalReuse(root);
    }

    public static int TerminalReuse(string root)
    {
        int passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed++;
        }
        var clock = new Clock();
        using var store = new BrokerStore(Path.Combine(root, "notice-terminal-lifetime-reuse"), clock: clock);
        var sender = Mailbox(store, clock, "sender");
        var recipient = Mailbox(store, clock, "recipient");
        store.Register(sender); store.Register(recipient);
        var original = Message(sender, recipient, clock);
        store.Accept(original, store.BrokerId);
        store.Disconnect(sender.Address);
        store.RecordRecipientReceipt(original.Id, recipient.Address, store.BrokerId, MessageStatus.Refused);
        clock.Advance(TimeSpan.FromHours(47));
        store.Register(Renew(sender, clock)); store.Register(Renew(recipient, clock));
        store.BeginDelivery();
        var oldNotice = Notices(store, sender.Address).Single();
        store.RecordRecipientReceipt(oldNotice.Message.Id, sender.Address, store.BrokerId, MessageStatus.Delivered);
        clock.Advance(TimeSpan.FromHours(2));
        Check(store.FindReceipt(original.Id) is null, "NOTICE_REUSE_WITNESS: original source must be pruned.");
        string? refused = null;
        try { store.Send(sender.Address, recipient.Address, original.Id, "A later lifetime.", null, 1440, store.BrokerId); }
        catch (BrokerException error) { refused = error.Code; }
        Check(refused == "message_id_collision", "NOTICE_TERMINAL_REUSE_GUARD: a delayed legacy notice reserves its original UUID while retained.");
        clock.Advance(TimeSpan.FromHours(31)); // T0+80h: the notice expired at +71h but remains retained until +95h.
        store.Register(Renew(sender, clock)); store.Register(Renew(recipient, clock));
        refused = null;
        try { store.Send(sender.Address, recipient.Address, original.Id, "A later lifetime.", null, 1440, store.BrokerId); }
        catch (BrokerException error) { refused = error.Code; }
        Check(refused == "message_id_collision",
            "NOTICE_TERMINAL_REUSE_GRACE: an expired but retained delayed notice still reserves the source UUID at T0+80h.");
        clock.Advance(TimeSpan.FromHours(16));
        store.Register(Renew(sender, clock)); store.Register(Renew(recipient, clock));
        Check(store.FindReceipt(oldNotice.Message.Id) is null, "NOTICE_REUSE_PRUNE_WITNESS: old notice must reach ordinary retention.");
        var next = store.Send(sender.Address, recipient.Address, original.Id, "A later lifetime.", null, 1440, store.BrokerId);
        store.RecordRecipientReceipt(next.Message.Id, recipient.Address, store.BrokerId, MessageStatus.Refused);
        var notices = Notices(store, sender.Address);
        Check(notices.Count == 1 && notices[0].Status == MessageStatus.Accepted,
            "NOTICE_TERMINAL_REUSE_AFTER_PRUNE: reused UUID receives its new terminal notice once the old notice prunes.");
        Check(store.Accept(next.Message, store.BrokerId).Status == MessageStatus.Refused,
            "NOTICE_TERMINAL_SOURCE_RETRY: retry of a still-retained source returns its original receipt despite its notice.");
        {
            var unacknowledgedClock = new Clock();
            using var unacknowledgedStore = new BrokerStore(Path.Combine(root, "notice-terminal-unacknowledged-reuse"), clock: unacknowledgedClock);
            var from = Mailbox(unacknowledgedStore, unacknowledgedClock, "sender");
            var to = Mailbox(unacknowledgedStore, unacknowledgedClock, "recipient");
            unacknowledgedStore.Register(from); unacknowledgedStore.Register(to);
            var source = Message(from, to, unacknowledgedClock); unacknowledgedStore.Accept(source, unacknowledgedStore.BrokerId);
            unacknowledgedStore.Disconnect(from.Address);
            unacknowledgedStore.RecordRecipientReceipt(source.Id, to.Address, unacknowledgedStore.BrokerId, MessageStatus.Refused);
            unacknowledgedClock.Advance(TimeSpan.FromHours(47));
            unacknowledgedStore.Register(Renew(from, unacknowledgedClock)); unacknowledgedStore.BeginDelivery();
            Check(Notices(unacknowledgedStore, from.Address).Count == 1,
                "NOTICE_UNACKNOWLEDGED_WITNESS: the delayed terminal notice exists as a full unacknowledged record");
            unacknowledgedClock.Advance(TimeSpan.FromHours(2));
            Check(unacknowledgedStore.FindReceipt(source.Id) is null,
                "NOTICE_UNACKNOWLEDGED_SOURCE_PRUNED: the source has ended ordinary retention before its notice");
            unacknowledgedClock.Advance(TimeSpan.FromHours(47));
            // No read or delivery pass here: Register does not prune the expired full notice. Admission itself must
            // disregard that out-of-retention record before its ordinary commit removes it.
            unacknowledgedStore.Register(Renew(from, unacknowledgedClock)); unacknowledgedStore.Register(Renew(to, unacknowledgedClock));
            MessageReceipt? admitted = null;
            try { admitted = unacknowledgedStore.Send(from.Address, to.Address, source.Id, "A later lifetime.", null, 1440, unacknowledgedStore.BrokerId); }
            catch (BrokerException) { }
            Check(admitted?.Status == MessageStatus.Accepted,
                "NOTICE_UNACKNOWLEDGED_REUSE_AFTER_RETENTION: an unpruned full notice cannot reserve its source UUID past ordinary retention");
        }
        return passed;
    }

    private static BrokerSession Mailbox(BrokerStore store, Clock clock, string id) => new(
        new(store.BrokerId, "mcp:" + id, Guid.NewGuid()), id, "mcp", SessionCapabilities.Receive | SessionCapabilities.Reply,
        SessionActivity.Idle, clock.GetUtcNow().AddMinutes(1));
    private static BrokerSession Native(BrokerStore store, Clock clock, string id) => new(
        new(store.BrokerId, "claude:" + id, Guid.NewGuid()), id, "claude-native", SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply,
        SessionActivity.Idle, clock.GetUtcNow().AddMinutes(1));
    private static BrokerSession Renew(BrokerSession session, Clock clock) => session with { LeaseUntil = clock.GetUtcNow().AddMinutes(1) };
    private static BrokerMessage Message(BrokerSession sender, BrokerSession recipient, Clock clock) => new(
        Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, recipient.Address, "A private synthetic request.", clock.GetUtcNow(), clock.GetUtcNow().AddHours(24));
    private static List<MessageReceipt> Notices(BrokerStore store, SessionAddress owner) => store.Messages(owner)
        .Where(receipt => receipt.Message.Sender.SessionId == BrokerStore.NoticeSessionId && receipt.Message.Recipient == owner).ToList();
    private static IReadOnlyList<MessageReceipt> LegacyPass(string assembly, string path, Clock clock, BrokerSession sender, BrokerSession recipient)
    {
        var context = new System.Runtime.Loader.AssemblyLoadContext("notice-legacy-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            var binary = context.LoadFromAssemblyPath(Path.GetFullPath(assembly));
            var storeType = binary.GetType("AgentBroker.Core.BrokerStore", throwOnError: true)!;
            var sessionType = binary.GetType("AgentBroker.Core.BrokerSession", throwOnError: true)!;
            var addressType = binary.GetType("AgentBroker.Core.SessionAddress", throwOnError: true)!;
            using var store = (IDisposable)Activator.CreateInstance(storeType, [path, null, clock])!;
            foreach (var session in new[] { sender, recipient })
                storeType.GetMethod("Register")!.Invoke(store, [System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(session), sessionType)]);
            storeType.GetMethod("BeginDelivery")!.Invoke(store, [null]);
            var address = System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(sender.Address), addressType);
            var rows = storeType.GetMethod("Messages")!.Invoke(store, [address])!;
            return System.Text.Json.JsonSerializer.Deserialize<MessageReceipt[]>(System.Text.Json.JsonSerializer.Serialize(rows, rows.GetType()))!;
        }
        finally { context.Unload(); }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
