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
        // 1.10.6 B1 checks first: this harness stops at its first failure, so a mutant that also breaks an older check below
        // reports at its own label.
        passed += Grace(root);
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
            if (reason.StartsWith("native_", StringComparison.Ordinal))
            {
                // 1.10.6 B1: a native-cause advisory waits the grace (the GRACE checks above pin that). Advance past it,
                // renewing the one-minute fixture leases, and let a delivery pass admit it.
                clock.Advance(BrokerStore.UncertainAdvisoryGrace);
                store.Register(Renew(sender, clock)); store.Register(Renew(recipient, clock)); store.BeginDelivery();
            }
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
            // 1.10.6 B1: resolved inside the grace, then checked past it, so this proves the cancellation rather than the grace.
            clock.Advance(BrokerStore.UncertainAdvisoryGrace + TimeSpan.FromSeconds(1));
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
            // 1.10.6 B1: the new lifetime's native-cause advisory waits the grace; a pass past it admits it.
            clock.Advance(BrokerStore.UncertainAdvisoryGrace); store.Register(Renew(sender, clock)); store.BeginDelivery();
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
            // 1.10.6: an out-of-grace reason (submission_unconfirmed), whose advisory is still immediate, so the advisory half
            // of these checks keeps proving commit atomicity rather than the B1 grace.
            using (var locked = new FileStream(Path.Combine(path, "broker-state.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { store.CompleteDelivery(claim, MessageStatus.Uncertain, "submission_unconfirmed"); }
                catch (IOException) { failed = true; }
            }
            Check(failed && store.FindReceipt(message.Id)?.Status == MessageStatus.Submitting && Notices(store, sender.Address).Count == 0,
                "UNCERTAIN_NOTICE_COMMIT_ATOMIC: an actual failed snapshot publishes neither uncertainty nor its advisory");
            store.CompleteDelivery(claim, MessageStatus.Uncertain, "submission_unconfirmed");
            Check(Notices(store, sender.Address).Count == 1,
                "UNCERTAIN_NOTICE_COMMIT_RETRY: restored durable storage commits the original claim and exactly one advisory");
        }
        {
            var clock = new Clock(); string path = Path.Combine(root, "notice-uncertain-text-bound");
            using var store = new BrokerStore(path, new(MaximumTextBytes: 64, DefaultMessageLifetimeMinutes: 1), clock);
            var sender = Mailbox(store, clock, "sender"); var recipient = Native(store, clock, "recipient");
            store.Register(sender); store.Register(recipient);
            var message = Message(sender, recipient, clock); store.Accept(message, store.BrokerId); var claim = store.BeginDelivery()!;
            // 1.10.6: the reason is incidental here; an out-of-grace one keeps the advisory immediate.
            store.CompleteDelivery(claim, MessageStatus.Uncertain, "submission_unconfirmed");
            var notice = Notices(store, sender.Address).Single();
            Check(System.Text.Encoding.UTF8.GetByteCount(notice.Message.Text) <= 64 && notice.Message.ExpiresAt == clock.GetUtcNow().AddHours(24),
                "UNCERTAIN_NOTICE_TEXT_AND_TTL: advisory obeys the real text cap and uses maximum lifetime instead of configured default");
        }
        return passed + TerminalReuse(root);
    }

    // AgentBroker 1.10.6 B1 (N-broker-native-confirmation-01; design v3 + v4 + v5, DECISIONS 15, 17, 21, 23): an advisory for
    // a native-cause Uncertain source waits UncertainAdvisoryGrace (120 monotonic seconds) and is withheld, with an
    // advisory_withheld event in broker-events.jsonl, when the recipient settles first. Each label has its mutants; the harness
    // stops at its first failure. Only the event checks assert what the events file holds; FAILED_SETTLEMENT also asserts that
    // a failed commit wrote nothing to it.
    private static int Grace(string root)
    {
        int passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed++;
        }
        TimeSpan grace = BrokerStore.UncertainAdvisoryGrace, second = TimeSpan.FromSeconds(1);
        (Clock Clock, BrokerStore Store, BrokerSession Sender, BrokerSession Recipient, BrokerMessage Message) Open(string name, string? text = null)
        {
            var clock = new Clock();
            var store = new BrokerStore(Path.Combine(root, "notice-grace-" + name), clock: clock);
            var sender = Mailbox(store, clock, "sender"); var recipient = Native(store, clock, "recipient");
            store.Register(sender); store.Register(recipient);
            var message = Message(sender, recipient, clock);
            if (text is not null) message = message with { Text = text };
            store.Accept(message, store.BrokerId);
            return (clock, store, sender, recipient, message);
        }
        void Uncertain(BrokerStore store, BrokerMessage message, string reason = "native_uncertain_timeout")
        {
            var claim = store.BeginDelivery() ?? throw new InvalidOperationException("UNCERTAIN_NOTICE_GRACE_CLAIM_SETUP");
            store.CompleteDelivery(claim, MessageStatus.Uncertain, reason);
        }
        // A delivery pass with both fixture sessions renewed (their leases are one minute).
        void Pass(Clock clock, BrokerStore store, BrokerSession sender, BrokerSession recipient)
        { store.Register(Renew(sender, clock)); store.Register(Renew(recipient, clock)); store.BeginDelivery(); }
        void Settle(Clock clock, BrokerStore store, BrokerSession recipient, BrokerMessage message)
        { store.Register(Renew(recipient, clock)); store.RecordRecipientReceipt(message.Id, recipient.Address, store.BrokerId, MessageStatus.Delivered); }
        IReadOnlyDictionary<Guid, MessageReceipt> Owed(BrokerStore store) => (IReadOnlyDictionary<Guid, MessageReceipt>)typeof(BrokerStore)
            .GetField("owed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(store)!;
        string EventsPath(string name, bool rotated = false) => Path.Combine(root, "notice-grace-" + name, rotated ? "broker-events.1.jsonl" : "broker-events.jsonl");
        string[] EventLines(string name) => File.Exists(EventsPath(name)) ? File.ReadAllLines(EventsPath(name)).Where(l => l.Length > 0).ToArray() : [];

        {
            var (clock, store, sender, recipient, message) = Open("settle-before"); using var owned = store;
            Uncertain(store, message); Pass(clock, store, sender, recipient);
            clock.Advance(grace - second); Pass(clock, store, sender, recipient);
            Settle(clock, store, recipient, message);
            clock.Advance(second * 10); Pass(clock, store, sender, recipient);
            Check(Notices(store, sender.Address).Count == 0 && store.FindReceipt(message.Id)?.Status == MessageStatus.Delivered,
                "UNCERTAIN_NOTICE_GRACE_SETTLE_BEFORE: a native-cause source the recipient settles at grace minus one second sends no advisory");
        }
        {
            var (clock, store, sender, recipient, message) = Open("never-settles"); using var owned = store;
            Uncertain(store, message); Pass(clock, store, sender, recipient);
            clock.Advance(grace - second); Pass(clock, store, sender, recipient);
            int before = Notices(store, sender.Address).Count;
            clock.Advance(second); Pass(clock, store, sender, recipient);
            Check(before == 0 && Notices(store, sender.Address).Count == 1,
                "UNCERTAIN_NOTICE_GRACE_NEVER_SETTLES: with no other write, an unsettled native-cause advisory is admitted at exactly the grace, not a second before");
        }
        {
            var (clock, store, sender, recipient, message) = Open("settle-after"); using var owned = store;
            Uncertain(store, message);
            clock.Advance(grace); Pass(clock, store, sender, recipient);
            int admitted = Notices(store, sender.Address).Count;
            clock.Advance(second); Settle(clock, store, recipient, message); Pass(clock, store, sender, recipient);
            Check(admitted == 1 && Notices(store, sender.Address).Count == 1 && store.FindReceipt(message.Id)?.Status == MessageStatus.Delivered,
                "UNCERTAIN_NOTICE_GRACE_SETTLE_AFTER: an advisory admitted at the grace stays historical when the recipient settles a second later");
        }
        {
            var clock = new Clock(); string path = Path.Combine(root, "notice-grace-restart-cause");
            BrokerSession sender, recipient; Guid source;
            using (var store = new BrokerStore(path, clock: clock))
            {
                sender = Mailbox(store, clock, "sender"); recipient = Native(store, clock, "recipient");
                store.Register(sender); store.Register(recipient);
                var message = Message(sender, recipient, clock); source = message.Id;
                store.Accept(message, store.BrokerId); store.BeginDelivery();
            }
            using (var store = new BrokerStore(path, clock: clock))
            {
                store.Register(sender); store.Register(recipient); store.BeginDelivery();
                Check(store.FindReceipt(source)?.Reason == "restart_during_submission" && Notices(store, sender.Address).Count == 1,
                    "UNCERTAIN_NOTICE_GRACE_RESTART_CAUSE_IMMEDIATE: a restart_during_submission advisory is admitted at once, outside the grace");
            }
        }
        {
            var (clock, store, sender, recipient, message) = Open("wall-rollback"); using var owned = store;
            Uncertain(store, message);
            var uncertainAt = store.FindReceipt(message.Id)!.UpdatedAt;
            bool early = false;
            // X1d's counterexample (handoff/x1d-01a1eb25/outbox/024-N-design-proof): the wall clock steps 0, 100, 50, 100, 50
            // seconds after the Uncertain time while monotonic time advances; a wall-clock predicate never comes due.
            foreach (var (monotonic, wall) in new[] { (30, 100), (30, 50), (30, 100), (29, 50) })
            {
                clock.Advance(TimeSpan.FromSeconds(monotonic)); clock.SetUtc(uncertainAt + TimeSpan.FromSeconds(wall));
                Pass(clock, store, sender, recipient);
                early |= Notices(store, sender.Address).Count != 0;
            }
            clock.Advance(second); clock.SetUtc(uncertainAt + TimeSpan.FromSeconds(50)); Pass(clock, store, sender, recipient);
            Check(!early && Notices(store, sender.Address).Count == 1,
                "UNCERTAIN_NOTICE_GRACE_WALL_ROLLBACK: repeated partial wall-clock rollbacks inside the window still admit the advisory at exactly 120 monotonic seconds");
        }
        {
            var (clock, store, sender, recipient, message) = Open("prestart"); using var owned = store;
            Uncertain(store, message);
            var uncertainAt = store.FindReceipt(message.Id)!.UpdatedAt;
            clock.SetUtc(uncertainAt - second); Pass(clock, store, sender, recipient);
            int atStart = Notices(store, sender.Address).Count;
            clock.Advance(grace - second); clock.SetUtc(uncertainAt - second); Pass(clock, store, sender, recipient);
            int atEnd = Notices(store, sender.Address).Count;
            clock.Advance(second); clock.SetUtc(uncertainAt - second); Pass(clock, store, sender, recipient);
            Check(atStart == 0 && atEnd == 0 && Notices(store, sender.Address).Count == 1,
                "UNCERTAIN_NOTICE_GRACE_PRESTART: a wall clock set before the Uncertain time neither admits the advisory early nor holds it past 120 monotonic seconds");
        }
        {
            // X1d N-D5 (design v5) and N-T1 (DECISIONS 25): two CLEAN retained snapshots, each already Uncertain, with no
            // Submitting or Submitted record, nothing to upgrade, no expiry due and no unrelated write, so a reopen makes no
            // recovery commit. Fixture A passes at once (0, 119 and 120 s after the open) and catches open timers that are due at
            // once. Fixture B makes no due check and no commit before its first pass at 119 s, so only the timer seeded at the
            // open can make the advisory due at 120 s: it catches a missing open seed that the seed-where-missing path would hide.
            (string Dir, BrokerSession Sender, BrokerSession Recipient, Guid Source, DateTime Written, byte[] Snapshot, Clock Clock) Retain(string name)
            {
                var clock = new Clock(); string dir = Path.Combine(root, "notice-grace-" + name);
                BrokerSession sender, recipient; Guid source;
                using (var store = new BrokerStore(dir, clock: clock))
                {
                    sender = Mailbox(store, clock, "sender"); recipient = Native(store, clock, "recipient");
                    store.Register(sender); store.Register(recipient);
                    var message = Message(sender, recipient, clock); source = message.Id;
                    store.Accept(message, store.BrokerId); Uncertain(store, message);
                }
                string state = Path.Combine(dir, "broker-state.json");
                return (dir, sender, recipient, source, File.GetLastWriteTimeUtc(state), File.ReadAllBytes(state), clock);
            }
            bool Untouched(string dir, DateTime written, byte[] snapshot) =>
                File.GetLastWriteTimeUtc(Path.Combine(dir, "broker-state.json")) == written &&
                File.ReadAllBytes(Path.Combine(dir, "broker-state.json")).SequenceEqual(snapshot);
            bool cleanA, cleanB; int a0, a119, a120, b119, b120;
            var a = Retain("restart-reeligible");
            using (var store = new BrokerStore(a.Dir, clock: a.Clock))
            {
                cleanA = Untouched(a.Dir, a.Written, a.Snapshot) &&
                    store.FindReceipt(a.Source) is { Status: MessageStatus.Uncertain, Reason: "native_uncertain_timeout" };
                Pass(a.Clock, store, a.Sender, a.Recipient); a0 = Notices(store, a.Sender.Address).Count;
                a.Clock.Advance(grace - second); Pass(a.Clock, store, a.Sender, a.Recipient); a119 = Notices(store, a.Sender.Address).Count;
                a.Clock.Advance(second); Pass(a.Clock, store, a.Sender, a.Recipient); a120 = Notices(store, a.Sender.Address).Count;
            }
            var b = Retain("restart-reeligible-late");
            using (var store = new BrokerStore(b.Dir, clock: b.Clock))
            {
                // Leases renewed in memory only: Register makes no due check and no commit (counted from the open on).
                long commits = store.CommitTimings().Completed;
                store.Register(Renew(b.Sender, b.Clock)); store.Register(Renew(b.Recipient, b.Clock));
                b.Clock.Advance(grace - second);
                cleanB = Untouched(b.Dir, b.Written, b.Snapshot) && store.CommitTimings().Completed == commits;
                Pass(b.Clock, store, b.Sender, b.Recipient); b119 = Notices(store, b.Sender.Address).Count;
                b.Clock.Advance(second); Pass(b.Clock, store, b.Sender, b.Recipient); b120 = Notices(store, b.Sender.Address).Count;
            }
            Check(cleanA && cleanB,
                "UNCERTAIN_NOTICE_GRACE_RESTART_CLEAN_WITNESS: both reopens over a clean already-Uncertain snapshot make no recovery commit, and fixture B commits nothing before its first pass");
            Check(a0 == 0 && a119 == 0 && a120 == 1 && b119 == 0 && b120 == 1,
                "UNCERTAIN_NOTICE_GRACE_RESTART_REELIGIBLE: after a clean reopen the retained advisory is admitted 120 monotonic seconds after the open, neither at once nor never");
        }
        {
            var (clock, store, sender, recipient, message) = Open("no-churn"); using var owned = store;
            Uncertain(store, message);
            long completed = store.CommitTimings().Completed;
            foreach (int step in new[] { 30, 30, 50 }) { clock.Advance(TimeSpan.FromSeconds(step)); Pass(clock, store, sender, recipient); }
            Check(store.CommitTimings().Completed == completed && Notices(store, sender.Address).Count == 0,
                "UNCERTAIN_NOTICE_GRACE_NO_CHURN: delivery passes inside the grace write nothing for a not-yet-due advisory");
        }
        {
            var (clock, store, sender, recipient, message) = Open("failed-settlement"); using var owned = store;
            string name = "failed-settlement";
            Uncertain(store, message); Pass(clock, store, sender, recipient);
            clock.Advance(TimeSpan.FromSeconds(30)); store.Register(Renew(recipient, clock));
            bool failed = false;
            using (var locked = new FileStream(Path.Combine(root, "notice-grace-" + name, "broker-state.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { store.RecordRecipientReceipt(message.Id, recipient.Address, store.BrokerId, MessageStatus.Delivered); }
                catch (IOException) { failed = true; }
            }
            bool intact = failed && store.FindReceipt(message.Id)?.Status == MessageStatus.Uncertain && Owed(store).Count == 1 && EventLines(name).Length == 0;
            Settle(clock, store, recipient, message);
            clock.Advance(grace); Pass(clock, store, sender, recipient);
            Check(intact && store.FindReceipt(message.Id)?.Status == MessageStatus.Delivered && Owed(store).Count == 0 && Notices(store, sender.Address).Count == 0,
                "UNCERTAIN_NOTICE_GRACE_FAILED_SETTLEMENT: a settlement whose snapshot write fails leaves the source Uncertain, the advisory owed and nothing recorded; the retry withholds it");
        }
        {
            var (clock, store, sender, recipient, message) = Open("sender-away"); using var owned = store;
            var other = Mailbox(store, clock, "other"); store.Register(other);
            Uncertain(store, message);
            clock.Advance(TimeSpan.FromSeconds(10)); store.Disconnect(sender.Address);
            clock.Advance(grace); store.Register(Renew(recipient, clock)); store.BeginDelivery();
            // An unrelated commit while the advisory is due and its sender away: the admission loop must defer it, not cancel it.
            store.Register(Renew(other, clock)); store.Accept(Message(other, recipient, clock), store.BrokerId);
            bool kept = Notices(store, sender.Address).Count == 0 && Owed(store).Count == 1 && store.DroppedNotices().Total == 0;
            store.Register(Renew(sender, clock)); store.BeginDelivery();
            bool away = kept && Notices(store, sender.Address).Count == 1;
            // Queue full: the sender's one pending slot is taken until it acknowledges the blocker.
            var full = new Clock();
            using var capped = new BrokerStore(Path.Combine(root, "notice-grace-queue-full"), new(MaximumPendingPerSession: 1), full);
            var from = Mailbox(capped, full, "sender"); var to = Native(capped, full, "recipient"); var third = Mailbox(capped, full, "other");
            capped.Register(from); capped.Register(to); capped.Register(third);
            var blocker = Message(to, from, full); capped.Accept(blocker, capped.BrokerId);
            var source = Message(from, to, full); capped.Accept(source, capped.BrokerId);
            var claim = capped.BeginDelivery()!; capped.CompleteDelivery(claim, MessageStatus.Uncertain, "native_uncertain_timeout");
            full.Advance(grace); capped.Register(Renew(from, full)); capped.Register(Renew(to, full)); capped.BeginDelivery();
            // An unrelated commit while the advisory is due and the sender's queue is full: deferred, not cancelled.
            // To a session with room (third has nothing pending), so the only full queue is the advisory's sender's.
            capped.Register(Renew(third, full)); capped.Accept(Message(to, third, full), capped.BrokerId);
            bool queued = Notices(capped, from.Address).Count == 0 && Owed(capped).Count == 1 && capped.DroppedNotices().Total == 0;
            capped.RecordRecipientReceipt(blocker.Id, from.Address, capped.BrokerId, MessageStatus.Delivered);
            Check(away && queued && Notices(capped, from.Address).Count == 1,
                "UNCERTAIN_NOTICE_GRACE_SENDER_AWAY: a due advisory whose sender is away or has no queue room stays owed and is admitted when the sender returns or has room");
        }
        {
            var (clock, store, sender, recipient, message) = Open("retention-counted"); using var owned = store;
            Uncertain(store, message);
            // Wall time alone past the source's retention (ExpiresAt + 1 day) while monotonic time stays inside the grace.
            clock.StepUtc(TimeSpan.FromHours(49)); store.BeginDelivery();
            Check(store.DroppedNotices().Total == 1 && Owed(store).Count == 0 && Notices(store, sender.Address).Count == 0,
                "UNCERTAIN_NOTICE_GRACE_RETENTION_COUNTED: a native-cause advisory whose source retention ends inside the grace is dropped and counted, not hidden");
        }
        {
            var (clock, store, sender, recipient, message) = Open("timer-staged"); using var owned = store;
            var claim = store.BeginDelivery()!;
            bool failed = false;
            using (var locked = new FileStream(Path.Combine(root, "notice-grace-timer-staged", "broker-state.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { store.CompleteDelivery(claim, MessageStatus.Uncertain, "native_uncertain_timeout"); }
                catch (IOException) { failed = true; }
            }
            clock.Advance(TimeSpan.FromSeconds(60));
            store.Register(Renew(sender, clock)); store.Register(Renew(recipient, clock));
            store.CompleteDelivery(claim, MessageStatus.Uncertain, "native_uncertain_timeout");
            clock.Advance(TimeSpan.FromSeconds(60)); Pass(clock, store, sender, recipient);
            int atFailedPlusGrace = Notices(store, sender.Address).Count;
            clock.Advance(grace - TimeSpan.FromSeconds(60) - second); Pass(clock, store, sender, recipient);
            int beforeDue = Notices(store, sender.Address).Count;
            clock.Advance(second); Pass(clock, store, sender, recipient);
            Check(failed && atFailedPlusGrace == 0 && beforeDue == 0 && Notices(store, sender.Address).Count == 1,
                "UNCERTAIN_NOTICE_GRACE_TIMER_STAGED: the grace starts at the commit that succeeds, never at a failed attempt");
        }
        {
            // X1d 033 / DECISIONS 24: the writer opens, appends and closes, so a scan of the store's files (TombstoneChecks'
            // File.ReadAllBytes over the top level) never meets an open write handle. This check asserts only that property, with
            // the scan's own open, whenever the file exists; whether a line was written at all is WITHHELD_EVENT's question.
            var (clock, store, sender, recipient, message) = Open("events-closed"); using var owned = store;
            Uncertain(store, message); clock.Advance(TimeSpan.FromSeconds(10)); Settle(clock, store, recipient, message);
            bool readable = true;
            if (File.Exists(EventsPath("events-closed")))
            {
                try { File.ReadAllBytes(EventsPath("events-closed")); }
                catch (IOException) { readable = false; }
            }
            Check(readable,
                "UNCERTAIN_NOTICE_EVENTS_CLOSED_APPEND: once an append returns, a scan of the store files can read broker-events.jsonl: the store holds no write handle on it");
        }
        {
            var (clock, store, sender, recipient, message) = Open("withheld-event"); using var owned = store;
            Uncertain(store, message);
            var uncertain = store.FindReceipt(message.Id)!;
            clock.Advance(TimeSpan.FromSeconds(30)); Settle(clock, store, recipient, message);
            var settled = store.FindReceipt(message.Id)!;
            var lines = EventLines("withheld-event");
            var line = lines.Length == 1 ? System.Text.Json.Nodes.JsonNode.Parse(lines[0])!.AsObject() : null;
            string Field(string key) => line?[key]?.GetValue<string>() ?? "";
            var to = message.Recipient;
            Check(line is not null && Field("event") == "advisory_withheld" && Field("messageId") == message.Id.ToString("D") &&
                Field("recipient") == to.BrokerId.ToString("D") + "/" + to.SessionId + "/" + to.Incarnation.ToString("D") &&
                Field("uncertainAt") == uncertain.UpdatedAt.ToString("O") && Field("uncertainReason") == "native_uncertain_timeout" &&
                Field("settled") == "Delivered" && Field("settledAt") == settled.UpdatedAt.ToString("O") && Field("settledReason") == "recipient_receipt",
                "UNCERTAIN_NOTICE_WITHHELD_EVENT: a withheld advisory leaves one advisory_withheld line naming the message, recipient, Uncertain step and settlement");
        }
        {
            var (clock, store, sender, recipient, message) = Open("events-rotation"); using var owned = store;
            File.WriteAllText(EventsPath("events-rotation", rotated: true), "older rotated file\n");
            string filler = new string('#', (int)BrokerEventLog.MaximumBytes - 16) + "\n";
            File.WriteAllText(EventsPath("events-rotation"), filler);
            Uncertain(store, message); clock.Advance(TimeSpan.FromSeconds(30)); Settle(clock, store, recipient, message);
            Check(File.ReadAllText(EventsPath("events-rotation", rotated: true)) == filler && EventLines("events-rotation").Length == 1 &&
                EventLines("events-rotation")[0].Contains(message.Id.ToString("D"), StringComparison.Ordinal),
                "UNCERTAIN_NOTICE_EVENTS_ROTATION: at the 1 MiB cap the events file rotates once to broker-events.1.jsonl, replacing the older one");
        }
        {
            var (clock, store, sender, recipient, message) = Open("events-write-failure"); using var owned = store;
            var second2 = Message(sender, recipient, clock); store.Accept(second2, store.BrokerId);
            File.WriteAllText(EventsPath("events-write-failure"), "");
            Uncertain(store, message); clock.Advance(TimeSpan.FromSeconds(10));
            using (var held = new FileStream(EventsPath("events-write-failure"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Settle(clock, store, recipient, message);
            bool committed = store.FindReceipt(message.Id)?.Status == MessageStatus.Delivered && Owed(store).Count == 0;
            Uncertain(store, second2); clock.Advance(TimeSpan.FromSeconds(10)); Settle(clock, store, recipient, second2);
            var lines = EventLines("events-write-failure");
            var line = lines.Length == 1 ? System.Text.Json.Nodes.JsonNode.Parse(lines[0])!.AsObject() : null;
            Check(committed && line is not null && line["messageId"]?.GetValue<string>() == second2.Id.ToString("D") &&
                line["eventsLost"]?.GetValue<long>() == 1,
                "UNCERTAIN_NOTICE_EVENTS_WRITE_FAILURE: a failed event write leaves the settlement committed and is counted into the next line as eventsLost");
        }
        {
            // Distinctive full sentences, so the raw scan cannot be satisfied by an ID or address, plus a structured check of every
            // JSON line in both files (X1d 033 / DECISIONS 24): only the event fields, and no value carrying a message text.
            const string firstText = "Synthetic grace event text 5f3a91, which must never reach the events file.";
            const string laterText = "Synthetic grace event text 7c20e4, which must never reach the rotated file either.";
            var (clock, store, sender, recipient, message) = Open("events-no-text", firstText); using var owned = store;
            var later = Message(sender, recipient, clock) with { Text = laterText }; store.Accept(later, store.BrokerId);
            Uncertain(store, message); clock.Advance(TimeSpan.FromSeconds(10)); Settle(clock, store, recipient, message);
            File.AppendAllText(EventsPath("events-no-text"), new string('#', (int)BrokerEventLog.MaximumBytes) + "\n");
            Uncertain(store, later); clock.Advance(TimeSpan.FromSeconds(10)); Settle(clock, store, recipient, later);
            string current = File.ReadAllText(EventsPath("events-no-text")), rotated = File.ReadAllText(EventsPath("events-no-text", rotated: true));
            string[] allowed = ["event", "messageId", "recipient", "uncertainAt", "uncertainReason", "settled", "settledAt", "settledReason", "eventAt", "eventsLost"];
            bool Structured(string file) => file.Split('\n').Where(line => line.StartsWith('{')).All(line =>
                System.Text.Json.Nodes.JsonNode.Parse(line)!.AsObject().All(field => allowed.Contains(field.Key) &&
                    !(field.Value?.ToJsonString() ?? "").Contains("Synthetic grace event text", StringComparison.Ordinal)));
            Check(rotated.Contains(message.Id.ToString("D"), StringComparison.Ordinal) && current.Contains(later.Id.ToString("D"), StringComparison.Ordinal) &&
                !rotated.Contains(firstText, StringComparison.Ordinal) && !current.Contains(laterText, StringComparison.Ordinal) &&
                !rotated.Contains(laterText, StringComparison.Ordinal) && !current.Contains(firstText, StringComparison.Ordinal) &&
                Structured(current) && Structured(rotated),
                "UNCERTAIN_NOTICE_EVENTS_NO_TEXT: neither the current nor the rotated events file holds message text, while both name their messages");
        }
        return passed;
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
    // Wall and monotonic time move together under Advance, as ordinary time does; StepUtc moves the wall clock alone (a UTC
    // step), which the B1 grace must ignore (DECISIONS 17).
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        private long timestamp = 1;
        public override DateTimeOffset GetUtcNow() => now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan duration) { now += duration; timestamp += duration.Ticks; }
        public void StepUtc(TimeSpan step) => now += step;
        public void SetUtc(DateTimeOffset wall) => now = wall;
    }
}
