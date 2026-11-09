using AgentBroker.Core;

// N-agentbroker-peer-expired-01: a paired broker's verified Expired was recorded as Refused with reason recipient_receipt,
// so the sender's notice said its recipient refused a message that had expired unread over there. Each label names the rule
// a mutation of BrokerStore.RecordPeerReceipt or the notice wording must break.
internal static class PeerReceiptChecks
{
    public static int Run(string root)
    {
        int passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed++;
        }
        var clock = new Clock();
        using var store = new BrokerStore(Path.Combine(root, "peer-receipt"), clock: clock);
        Guid peerId = Guid.NewGuid();
        store.AuthorizePeer(new(peerId, "Synthetic peer", "https://192.0.2.9/", new string('F', 64), clock.GetUtcNow()));
        var sender = new BrokerSession(new(store.BrokerId, "claude:peer-receipt-sender", Guid.NewGuid()), "Synthetic sender", "claude-native",
            // Busy, so the notices this check expects stay queued instead of being claimed ahead of the next forward.
            SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply, SessionActivity.Busy, clock.GetUtcNow().AddMinutes(2));
        store.Register(sender);
        var remote = new SessionAddress(peerId, "codex:peer-receipt-recipient", Guid.NewGuid());
        BrokerMessage Forwarded(string text)
        {
            var message = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, remote, text, clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(30));
            store.Accept(message, store.BrokerId);
            if (store.BeginDelivery()?.Message.Id != message.Id) throw new InvalidOperationException("Synthetic forward was not claimed.");
            store.CompleteDelivery(store.FindReceipt(message.Id)!, MessageStatus.Submitted, "peer_receipt");
            return message;
        }
        string? NoticeAbout(BrokerMessage message) => store.Messages(sender.Address)
            .Where(r => r.Message.Sender.SessionId == BrokerStore.NoticeSessionId && r.Message.Text.Contains(message.Id.ToString(), StringComparison.Ordinal))
            .Select(r => r.Message.Text).SingleOrDefault();

        var lapsed = Forwarded("Synthetic message the paired broker lets expire.");
        var settled = store.RecordPeerReceipt(store.FindReceipt(lapsed.Id)!, remote, peerId, MessageStatus.Expired);
        Check(settled.Status == MessageStatus.Refused && settled.Reason == "peer_expired",
            "PEER_EXPIRED_REASON: a paired broker's Expired is recorded as Refused with reason peer_expired");
        Check(NoticeAbout(lapsed) is { } expiredNotice && expiredNotice.Contains("expired at the recipient's paired broker", StringComparison.Ordinal) &&
            !expiredNotice.Contains("was refused by its recipient", StringComparison.Ordinal),
            "PEER_EXPIRED_NOTICE: the sender's notice says the message expired at the paired broker, not that its recipient refused it");

        var refused = Forwarded("Synthetic message the remote recipient refuses.");
        var declined = store.RecordPeerReceipt(store.FindReceipt(refused.Id)!, remote, peerId, MessageStatus.Refused);
        Check(declined.Status == MessageStatus.Refused && declined.Reason == "recipient_receipt" &&
            NoticeAbout(refused) is { } refusedNotice && refusedNotice.Contains("was refused by its recipient", StringComparison.Ordinal),
            "PEER_REFUSED_UNCHANGED: a remote recipient's own refusal still reads as refused by its recipient");
        foreach (string reason in new[] { "recipient_replaced", "recipient_unavailable", "recipient_cannot_receive", "private_session", "recipient_queue_full", "message_limit" })
        {
            var message = Forwarded("Synthetic known peer refusal.");
            Check(store.RecordPeerReceipt(store.FindReceipt(message.Id)!, remote, peerId, MessageStatus.Refused, reason).Reason == reason,
                "PEER_KNOWN_REASON: only recognized refusal metadata survives receipt reconciliation");
            // X2 (AgentBroker next campaign 2026-11-08): the paired broker refused it, so the notice must not blame this one.
            if (reason != "recipient_replaced")
                Check(NoticeAbout(message) is { } peerNotice && peerNotice.Contains("refused by the recipient's paired broker " + peerId, StringComparison.Ordinal) &&
                    peerNotice.Contains("(" + reason + ")", StringComparison.Ordinal) && !peerNotice.Contains("refused by this broker", StringComparison.Ordinal),
                    "PEER_REFUSAL_ATTRIBUTED: a paired broker's refusal reason is attributed to that broker, not this one");
        }
        foreach (string? reason in new[] { "unknown_future_code", "bad\nprivate_reason", "RECIPIENT_REPLACED", new string('x', 81), null })
        {
            var message = Forwarded("Synthetic unrecognized peer refusal.");
            Check(store.RecordPeerReceipt(store.FindReceipt(message.Id)!, remote, peerId, MessageStatus.Refused, reason).Reason == "recipient_receipt",
                "PEER_UNKNOWN_REASON: malformed or unrecognized peer text never becomes receipt metadata");
        }
        var expiredWithReason = Forwarded("Synthetic expired peer metadata.");
        Check(store.RecordPeerReceipt(store.FindReceipt(expiredWithReason.Id)!, remote, peerId, MessageStatus.Expired, "recipient_replaced").Reason == "peer_expired",
            "PEER_EXPIRED_REASON_PRIORITY: expiry keeps its special meaning regardless of supplied refusal metadata");
        var deliveredWithReason = Forwarded("Synthetic successful peer metadata.");
        Check(store.RecordPeerReceipt(store.FindReceipt(deliveredWithReason.Id)!, remote, peerId, MessageStatus.Delivered, "recipient_replaced").Reason == "recipient_receipt",
            "PEER_REASON_STATUS_SCOPE: refusal metadata never decorates a successful receipt");

        // X2 control: a local recipient that lost Receive after admission is refused by THIS broker at delivery, and its notice
        // still says so. (One that cannot receive is refused at admission, before any record or notice exists.)
        var mute = new BrokerSession(new(store.BrokerId, "codex:peer-receipt-local-mute", Guid.NewGuid()), "Synthetic local recipient", "codex-native",
            SessionCapabilities.Receive | SessionCapabilities.WakeIdle, SessionActivity.Busy, clock.GetUtcNow().AddMinutes(2));
        store.Register(mute);
        var local = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, mute.Address, "Synthetic local refusal.",
            clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(30));
        store.Accept(local, store.BrokerId);
        store.Register(mute with { Capabilities = SessionCapabilities.WakeIdle });
        store.BeginDelivery();
        Check(store.FindReceipt(local.Id) is { Status: MessageStatus.Refused, Reason: "recipient_cannot_receive" } &&
            NoticeAbout(local) is { } localNotice && localNotice.Contains("was refused by this broker before delivery (recipient_cannot_receive)", StringComparison.Ordinal) &&
            !localNotice.Contains("paired broker", StringComparison.Ordinal),
            "LOCAL_REFUSAL_ATTRIBUTED: a local refusal still reads as refused by this broker");
        return passed;
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 29, 4, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
    }
}
