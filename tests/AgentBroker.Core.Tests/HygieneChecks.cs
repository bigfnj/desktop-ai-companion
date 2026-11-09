using System.Text;
using System.Text.Json;
using AgentBroker.Core;

// AgentBroker next campaign, hygiene lane (2026-11-08). Each label names the rule its mutation must break:
// X1, a local Held past the deadline is refused message_expired instead of turning a reconcilable record into a dead end;
// A1, the 16 KiB text bound counts UTF-8 bytes, on admission and on loading durable state.
internal static class HygieneChecks
{
    public static int Run(string root)
    {
        int passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed++;
        }
        string? Refusal(Action action)
        {
            try { action(); return null; }
            catch (BrokerException error) { return error.Code; }
        }

        // X1: the native recipient is woken (Submitted), the deadline passes (Uncertain), and only then does it answer.
        var clock = new Clock();
        using (var store = new BrokerStore(Path.Combine(root, "hygiene-late-held"), clock: clock))
        {
            var sender = new BrokerSession(new(store.BrokerId, "claude:hygiene-sender", Guid.NewGuid()), "Synthetic sender", "claude-native",
                SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply, SessionActivity.Busy, clock.GetUtcNow().AddMinutes(2));
            var recipient = new BrokerSession(new(store.BrokerId, "codex:hygiene-recipient", Guid.NewGuid()), "Synthetic recipient", "codex-native",
                SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply, SessionActivity.Idle, clock.GetUtcNow().AddMinutes(2));
            void Renew()
            {
                store.Register(sender with { LeaseUntil = clock.GetUtcNow().AddMinutes(2) });
                store.Register(recipient with { LeaseUntil = clock.GetUtcNow().AddMinutes(2) });
            }
            Renew();
            BrokerMessage Woken(string text)
            {
                var message = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, recipient.Address, text,
                    clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(1));
                store.Accept(message, store.BrokerId);
                if (store.BeginDelivery()?.Message.Id != message.Id) throw new InvalidOperationException("Synthetic message was not claimed.");
                store.CompleteDelivery(store.FindReceipt(message.Id)!, MessageStatus.Submitted, "synthetic_admission");
                return message;
            }
            var lateHeld = Woken("Synthetic message held after its deadline.");
            var lateDelivered = Woken("Synthetic message delivered after its deadline.");
            var lateRefused = Woken("Synthetic message refused after its deadline.");
            var earlyHeld = Woken("Synthetic message held before its deadline.");
            Check(store.RecordRecipientReceipt(earlyHeld.Id, recipient.Address, store.BrokerId, MessageStatus.Held).Status == MessageStatus.Held,
                "LATE_HELD_WITNESS: Held before the deadline is still recorded");
            clock.Advance(TimeSpan.FromMinutes(2));
            Renew(); // The recipient is still there, still able to answer: only the message's deadline has passed.
            Check(store.FindReceipt(lateHeld.Id) is { Status: MessageStatus.Uncertain, Reason: "receipt_unconfirmed" },
                "LATE_HELD_SETUP: a Submitted message past its deadline reads Uncertain");
            Check(Refusal(() => store.RecordRecipientReceipt(lateHeld.Id, recipient.Address, store.BrokerId, MessageStatus.Held)) == "message_expired" &&
                store.FindReceipt(lateHeld.Id) is { Status: MessageStatus.Uncertain, Reason: "receipt_unconfirmed" },
                "LATE_HELD_REFUSED: a local Held after the deadline is refused message_expired and the record stays Uncertain");
            Check(Refusal(() => store.RecordRecipientReceipt(lateHeld.Id, recipient.Address, store.BrokerId, MessageStatus.Delivered)) is null &&
                store.FindReceipt(lateHeld.Id)?.Status == MessageStatus.Delivered,
                "LATE_HELD_THEN_DELIVERED: after the refused Held, the recipient's own Delivered still settles the record");
            Check(Refusal(() => store.RecordRecipientReceipt(lateDelivered.Id, recipient.Address, store.BrokerId, MessageStatus.Delivered)) is null &&
                store.FindReceipt(lateDelivered.Id)?.Status == MessageStatus.Delivered,
                "LATE_DELIVERED_ACCEPTED: a late local Delivered on an Uncertain record is unchanged");
            Check(store.FindReceipt(lateRefused.Id) is { Status: MessageStatus.Uncertain, Reason: "receipt_unconfirmed" } &&
                Refusal(() => store.RecordRecipientReceipt(lateRefused.Id, recipient.Address, store.BrokerId, MessageStatus.Refused)) is null &&
                store.FindReceipt(lateRefused.Id)?.Status == MessageStatus.Refused,
                "LATE_REFUSED_ACCEPTED: a late local Refused still settles an Uncertain record");

            // Keep this after the original late-receipt cases so their existing named negative witnesses remain first failures.
            var exactHeld = Woken("Synthetic message held at its exact deadline.");
            clock.Advance(TimeSpan.FromMinutes(1));
            Check(clock.GetUtcNow() == exactHeld.ExpiresAt &&
                Refusal(() => store.RecordRecipientReceipt(exactHeld.Id, recipient.Address, store.BrokerId, MessageStatus.Held)) == "message_expired" &&
                store.FindReceipt(exactHeld.Id) is { Status: MessageStatus.Uncertain, Reason: "receipt_unconfirmed" },
                "LATE_HELD_EXACT_DEADLINE: a local Held exactly at ExpiresAt is refused and remains Uncertain");
        }

        // X1, paired control: a paired broker's Held for a forwarded message keeps its own path, deadline or not.
        var peerClock = new Clock();
        using (var store = new BrokerStore(Path.Combine(root, "hygiene-peer-held"), clock: peerClock))
        {
            Guid peerId = Guid.NewGuid();
            store.AuthorizePeer(new(peerId, "Synthetic peer", "https://192.0.2.10/", new string('E', 64), peerClock.GetUtcNow()));
            var sender = new BrokerSession(new(store.BrokerId, "claude:hygiene-peer-sender", Guid.NewGuid()), "Synthetic sender", "claude-native",
                SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply, SessionActivity.Busy, peerClock.GetUtcNow().AddMinutes(2));
            store.Register(sender);
            var remote = new SessionAddress(peerId, "codex:hygiene-peer-recipient", Guid.NewGuid());
            var forwarded = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, remote, "Synthetic forwarded message.",
                peerClock.GetUtcNow(), peerClock.GetUtcNow().AddMinutes(1));
            store.Accept(forwarded, store.BrokerId);
            if (store.BeginDelivery()?.Message.Id != forwarded.Id) throw new InvalidOperationException("Synthetic forward was not claimed.");
            store.CompleteDelivery(store.FindReceipt(forwarded.Id)!, MessageStatus.Submitted, "peer_receipt");
            peerClock.Advance(TimeSpan.FromMinutes(2));
            var expected = store.FindReceipt(forwarded.Id)!;
            Check(Refusal(() => store.RecordPeerReceipt(expected, remote, peerId, MessageStatus.Held)) is null,
                "LATE_HELD_PAIRED_UNCHANGED: a paired broker's late Held is not refused by the local-only rule");
        }

        // A1: 5,461 euro signs plus one ASCII letter is exactly 16,384 UTF-8 bytes in 5,462 characters; 5,462 euro signs is
        // 16,386 bytes in the same 5,462 characters, so only a byte count tells them apart.
        string atLimit = new string('€', 5461) + "x", overLimit = new string('€', 5462);
        if (Encoding.UTF8.GetByteCount(atLimit) != 16 * 1024 || Encoding.UTF8.GetByteCount(overLimit) != 16 * 1024 + 2 || atLimit.Length != overLimit.Length)
            throw new InvalidOperationException("The A1 fixture does not straddle the byte limit at equal length.");
        var limitClock = new Clock();
        using (var store = new BrokerStore(Path.Combine(root, "hygiene-utf8-limit"), clock: limitClock))
        {
            var from = new BrokerSession(new(store.BrokerId, "claude:hygiene-limit-sender", Guid.NewGuid()), "Synthetic limit sender", "claude-native",
                SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply, SessionActivity.Busy, limitClock.GetUtcNow().AddMinutes(2));
            var to = new BrokerSession(new(store.BrokerId, "codex:hygiene-limit-recipient", Guid.NewGuid()), "Synthetic limit recipient", "codex-native",
                SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply, SessionActivity.Busy, limitClock.GetUtcNow().AddMinutes(2));
            store.Register(from); store.Register(to);
            BrokerMessage Text(string text) => new(Guid.NewGuid(), Guid.NewGuid(), null, from.Address, to.Address, text,
                limitClock.GetUtcNow(), limitClock.GetUtcNow().AddMinutes(5));
            string? atRefusal = Refusal(() => store.Accept(Text(atLimit), store.BrokerId));
            Check(atRefusal is null, "TEXT_LIMIT_UTF8_AT: a 16,384-byte multibyte text is admitted" + (atRefusal is null ? "" : " (refused " + atRefusal + ")"));
            Check(Refusal(() => store.Accept(Text(overLimit), store.BrokerId)) == "invalid_message",
                "TEXT_LIMIT_UTF8_OVER: a 16,386-byte text of the same character count is refused invalid_message");
        }

        // A1 on load: durable state holding an over-limit text is refused, so the snapshot check is not shadowed by admission.
        string Seeded(string name, string text)
        {
            string directory = Path.Combine(root, name);
            Directory.CreateDirectory(directory);
            Guid brokerId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var from = new SessionAddress(brokerId, "mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid());
            var to = new SessionAddress(brokerId, "mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid());
            var receipt = new MessageReceipt(new(Guid.NewGuid(), Guid.NewGuid(), null, from, to, text, now, now.AddHours(1)), MessageStatus.Accepted, now);
            File.WriteAllBytes(Path.Combine(directory, "broker-state.json"), JsonSerializer.SerializeToUtf8Bytes(new
            { Version = 1, BrokerId = brokerId, Peers = Array.Empty<PeerTrust>(), Messages = new[] { receipt } }));
            return directory;
        }
        bool Opens(string directory)
        {
            try { using var store = new BrokerStore(directory); return true; }
            catch (InvalidDataException) { return false; }
        }
        Check(Opens(Seeded("hygiene-load-at", atLimit)), "TEXT_LIMIT_LOAD_AT: durable state with a 16,384-byte multibyte text opens");
        Check(!Opens(Seeded("hygiene-load-over", overLimit)),
            "TEXT_LIMIT_LOAD_OVER: durable state with a 16,386-byte text of the same character count is refused on open");
        return passed;
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
