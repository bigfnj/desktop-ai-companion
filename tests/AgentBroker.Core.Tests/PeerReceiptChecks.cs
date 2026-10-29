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
            store.CompleteDelivery(message.Id, MessageStatus.Submitted, "peer_receipt");
            return message;
        }
        string? NoticeAbout(BrokerMessage message) => store.Messages(sender.Address)
            .Where(r => r.Message.Sender.SessionId == BrokerStore.NoticeSessionId && r.Message.Text.Contains(message.Id.ToString(), StringComparison.Ordinal))
            .Select(r => r.Message.Text).SingleOrDefault();

        var lapsed = Forwarded("Synthetic message the paired broker lets expire.");
        var settled = store.RecordPeerReceipt(lapsed.Id, remote, peerId, MessageStatus.Expired);
        Check(settled.Status == MessageStatus.Refused && settled.Reason == "peer_expired",
            "PEER_EXPIRED_REASON: a paired broker's Expired is recorded as Refused with reason peer_expired");
        Check(NoticeAbout(lapsed) is { } expiredNotice && expiredNotice.Contains("expired at the recipient's paired broker", StringComparison.Ordinal) &&
            !expiredNotice.Contains("was refused by its recipient", StringComparison.Ordinal),
            "PEER_EXPIRED_NOTICE: the sender's notice says the message expired at the paired broker, not that its recipient refused it");

        var refused = Forwarded("Synthetic message the remote recipient refuses.");
        var declined = store.RecordPeerReceipt(refused.Id, remote, peerId, MessageStatus.Refused);
        Check(declined.Status == MessageStatus.Refused && declined.Reason == "recipient_receipt" &&
            NoticeAbout(refused) is { } refusedNotice && refusedNotice.Contains("was refused by its recipient", StringComparison.Ordinal),
            "PEER_REFUSED_UNCHANGED: a remote recipient's own refusal still reads as refused by its recipient");
        return passed;
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 29, 4, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
    }
}
