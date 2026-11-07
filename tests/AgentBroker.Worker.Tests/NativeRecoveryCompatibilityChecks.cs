using AgentBroker.Core;

internal static class NativeRecoveryCompatibilityChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "scratch", "native-recovery-rollback-" + Guid.NewGuid().ToString("N"));
        Guid incomingId = Guid.NewGuid(), outgoingId = Guid.NewGuid();
        SessionAddress original;
        try
        {
            using (var store = new BrokerStore(root))
            {
                var now = DateTimeOffset.UtcNow;
                original = new(store.BrokerId, "codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid());
                var native = new BrokerSession(original, "Synthetic rollback native", "codex-native", SessionCapabilities.Receive | SessionCapabilities.Reply,
                    SessionActivity.Busy, now.AddMinutes(1));
                var peer = new BrokerSession(new(store.BrokerId, "mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid()), "Synthetic rollback mailbox", "mcp",
                    SessionCapabilities.Receive | SessionCapabilities.Reply, SessionActivity.Idle, now.AddMinutes(1));
                store.Register(native); store.Register(peer);
                Guid peerBroker = Guid.NewGuid(); store.AuthorizePeer(new(peerBroker, "Synthetic paired broker", "https://10.0.0.2:7433/", new string('A', 64), now));
                store.Accept(new(incomingId, incomingId, null, new(peerBroker, "peer", Guid.NewGuid()), original,
                    "Synthetic paired original request", now, now.AddMinutes(10)), peerBroker);
                store.Send(original, peer.Address, outgoingId, "Synthetic outgoing request", null, 0, store.BrokerId);
                store.RecordRecipientReceipt(outgoingId, peer.Address, store.BrokerId, MessageStatus.Delivered);
                var current = original with { Incarnation = Guid.NewGuid() };
                store.Register(native with { Address = current }); store.ObserveNativeSession(current, native.Adapter);
                string snapshot = Path.Combine(root, "broker-state.json"), journal = Path.Combine(root, "broker-tombstones.jsonl");
                byte[] before = File.ReadAllBytes(snapshot), tombstones = File.ReadAllBytes(journal);
                var page = store.ReadNativeRecovery(current);
                check(page.Entries.Count == 2 && page.Entries.All(entry => entry.PreviousIncarnation && !entry.CanAcknowledge && !entry.CanReply) &&
                    File.ReadAllBytes(snapshot).SequenceEqual(before) && File.ReadAllBytes(journal).SequenceEqual(tombstones),
                    "RECOVERY_ROLLBACK_QUERY_READ_ONLY: inventory writes neither schema1 snapshot nor tombstone journal");
                store.BeginDelivery();
            }
            using (var legacy = new AgentBrokerRollback104.BrokerStore(root))
            {
                var incoming = legacy.FindReceipt(incomingId);
                check(incoming is { Status: AgentBrokerRollback104.MessageStatus.Refused, Reason: "recipient_replaced", Attempts: 0 } &&
                    incoming.Message.Recipient.SessionId == original.SessionId && incoming.Message.Recipient.Incarnation == original.Incarnation &&
                    incoming.Message.Text == "Synthetic paired original request",
                    "RECOVERY_ROLLBACK_REPLACEMENT: actual 1.0.4 reader accepts unchanged schema and exact-address replacement refusal");
                check(legacy.FindReceipt(outgoingId) is null,
                    "RECOVERY_ROLLBACK_TOMBSTONE: rollback retains its documented lack of tombstone visibility");
            }
            using (var reopened = new BrokerStore(root))
            {
                check(reopened.FindReceipt(incomingId) is { Status: MessageStatus.Refused, Reason: "recipient_replaced" } &&
                    reopened.FindReceipt(outgoingId) is { Status: MessageStatus.Delivered, Message.Text: "" },
                    "RECOVERY_ROLLFORWARD: exact-address refusal and ordinary acknowledgement survive rollback and reopen");
            }
        }
        finally
        {
            string allowed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scratch")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new IOException("Native recovery rollback fixture escaped scratch.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
