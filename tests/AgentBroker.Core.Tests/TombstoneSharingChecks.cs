using AgentBroker.Core;

// N-broker-e-01 (lane broker-next, AgentBroker 1.6.0): tombstones loaded from the journal share one address instance per
// participant and one reason string, instead of each keeping its own copies (measured: 55.6 MB -> 31.0 MB of store heap at
// 65,512 tombstones). Records compare by value, so only the instances are observable; the journal format is unchanged.
internal static class TombstoneSharingChecks
{
    public static int Run(string root)
    {
        int passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed++;
        }
        string directory = Path.Combine(root, "tombstone-sharing");
        SessionAddress sender, recipient;
        using (var store = new BrokerStore(directory))
        {
            BrokerSession Mailbox(string id) => new(new(store.BrokerId, "mcp:" + id, Guid.NewGuid()), "Mailbox " + id, "mcp",
                SessionCapabilities.Receive | SessionCapabilities.Reply, SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(1));
            var from = Mailbox("sharing-sender"); var to = Mailbox("sharing-recipient");
            store.Register(from); store.Register(to);
            sender = from.Address; recipient = to.Address;
            for (int index = 0; index < 3; index++)
            {
                var receipt = store.Send(sender, recipient, Guid.NewGuid(), "Synthetic shared tombstone " + index + ".", null, 0, store.BrokerId);
                store.RecordRecipientReceipt(receipt.Message.Id, recipient, store.BrokerId, MessageStatus.Delivered);
            }
        }
        // Reopened: every tombstone comes from its own journal line, parsed separately.
        using (var reopened = new BrokerStore(directory))
        {
            var history = reopened.Messages(sender).Where(receipt => receipt.Status == MessageStatus.Delivered).ToArray();
            Check(history.Length == 3 && history.All(receipt => receipt.Message.Text.Length == 0 && receipt.Message.Sender == sender),
                "TOMBSTONE_SHARED WITNESS: the reopened store lists the three acknowledged messages as tombstones");
            Check(history.All(receipt => ReferenceEquals(receipt.Message.Sender, history[0].Message.Sender) &&
                    ReferenceEquals(receipt.Message.Recipient, history[0].Message.Recipient)),
                "TOMBSTONE_SHARED_ADDRESSES: tombstones of one participant pair share one instance of each address");
            Check(history.All(receipt => ReferenceEquals(receipt.Reason, history[0].Reason)) && history[0].Reason == "recipient_receipt",
                "TOMBSTONE_SHARED_REASON: tombstones with the same reason share one string");
        }
        return passed;
    }
}
