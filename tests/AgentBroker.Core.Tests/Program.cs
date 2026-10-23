using AgentBroker.Core;
using System.Text.Json.Nodes;

var root = Path.Combine(AppContext.BaseDirectory, "scratch", "agentbroker-core-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
void Check(bool value, string description)
{
    if (!value) throw new InvalidOperationException(description);
    passed++;
}
void Refuses(string code, Action action)
{
    try { action(); throw new InvalidOperationException("Expected refusal: " + code); }
    catch (BrokerException e) { Check(e.Code == code, "Expected " + code + ", got " + e.Code); }
}
var clock = new TestClock();
BrokerSession Session(BrokerStore store, string id, bool isPrivate = false) => new(
    new(store.BrokerId, id, Guid.NewGuid()), "same label", "synthetic",
    SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply,
    SessionActivity.Idle, clock.GetUtcNow().AddMinutes(1), isPrivate);
BrokerMessage Message(BrokerSession from, BrokerSession to) => new(Guid.NewGuid(), Guid.NewGuid(), null,
    from.Address, to.Address, "A harmless synthetic test message.", clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(30));

try
{
    if (args.SequenceEqual(["--reply-lifetime-only"]))
    {
        passed += ReplyLifetimeChecks.Run(root);
        Console.WriteLine($"AgentBroker reply lifetime smoke: {passed} assertions passed.");
        return 0;
    }
    Guid identity;
    BrokerMessage uncertain;
    using (var store = new BrokerStore(root, clock: clock))
    {
        identity = store.BrokerId;
        try { using var duplicate = new BrokerStore(root); throw new Exception("Second owner acquired the store."); }
        catch (IOException) { passed++; }
        var sender = Session(store, "claude:sender");
        var recipient = Session(store, "codex:recipient");
        store.Register(sender); store.Register(recipient);
        Check(store.Directory().Count == 2, "Exact IDs retain two equally named sessions.");
        var message = Message(sender, recipient);
        Check(store.Accept(message, identity).Status == MessageStatus.Accepted, "Durable admission.");
        Check(store.Accept(message, identity).Status == MessageStatus.Accepted, "Duplicate admission preserves receipt.");
        Check(store.Messages(recipient.Address).Count == 1, "Duplicate does not enqueue twice.");
        Refuses("message_id_collision", () => store.Accept(message with { Text = "different" }, identity));
        Refuses("sender_mismatch", () => store.Accept(message with { Id = Guid.NewGuid() }, Guid.NewGuid()));
        var first = store.BeginDelivery();
        Check(first?.Message.Id == message.Id && first.Status == MessageStatus.Submitting, "Claim persists before submission.");
        Check(store.BeginDelivery() is null, "In-flight message cannot be claimed twice.");
        Check(store.CompleteDelivery(message.Id, MessageStatus.Delivered).Status == MessageStatus.Delivered, "Delivery receipt persisted.");
        Refuses("message_not_submitting", () => store.CompleteDelivery(message.Id, MessageStatus.Delivered));
        var reply = Message(recipient, sender) with { ConversationId = message.ConversationId, ReplyTo = message.Id, RemainingReplies = 7 };
        Check(store.Accept(reply, identity).Status == MessageStatus.Accepted, "Reply correlates reversed exact participants.");
        Refuses("invalid_reply", () => store.Accept(reply with { Id = Guid.NewGuid(), RemainingReplies = 8 }, identity));
        store.BeginDelivery(); store.CompleteDelivery(reply.Id, MessageStatus.Delivered);

        store.Register(recipient with { Activity = SessionActivity.Busy });
        var busy = Message(sender, recipient);
        store.Accept(busy, identity);
        Check(store.BeginDelivery() is null, "Busy session waits for idle.");
        store.Register(recipient with { Activity = SessionActivity.ApprovalRequired });
        Check(store.BeginDelivery() is null, "Approval wait never admits a turn.");
        store.Register(recipient with { Activity = SessionActivity.Interrupted });
        Check(store.BeginDelivery() is null, "Interrupted session is not forcibly resumed.");
        store.Register(recipient);
        Check(store.BeginDelivery()?.Message.Id == busy.Id, "Idle session claims queued message.");
        store.CompleteDelivery(busy.Id, MessageStatus.Held, "native_inbound_policy");
        Check(store.BeginDelivery() is null, "Held message is not automatically resent.");

        var peerId = Guid.NewGuid();
        var peer = new PeerTrust(peerId, "Test peer", "https://192.168.1.2:8443/", new string('A', 64), clock.GetUtcNow());
        store.AuthorizePeer(peer);
        var remote = Session(store, "remote") with { Address = new(peerId, "claude:remote", Guid.NewGuid()) };
        var fromPeer = Message(remote, recipient);
        Check(store.Accept(fromPeer, peerId).Status == MessageStatus.Accepted, "Paired broker authenticates local inbound.");
        var stranger = Guid.NewGuid();
        Refuses("unpaired_peer", () => store.Accept(fromPeer with { Id = Guid.NewGuid(), Sender = new(stranger, "remote", Guid.NewGuid()) }, stranger));
        Refuses("transit_refused", () => store.Accept(Message(remote, remote with { Address = new(Guid.NewGuid(), "third", Guid.NewGuid()) }), peerId));
        var privateSession = Session(store, "private", true); store.Register(privateSession);
        Check(store.Directory(forPeer: true).All(s => s.Address != privateSession.Address), "Private sessions omitted from peer directory.");
        Refuses("private_session", () => store.Accept(Message(remote, privateSession), peerId));
        Refuses("peer_identity_changed", () => store.AuthorizePeer(peer with { CertificateSha256 = new string('B', 64) }));
        store.RevokePeer(peerId);
        Check(store.Messages(recipient.Address).Single(r => r.Message.Id == fromPeer.Id).Status == MessageStatus.Refused, "Revocation cancels pending inbound.");
        Refuses("unpaired_peer", () => store.Accept(Message(remote, recipient), peerId));

        var newIncarnation = recipient with { Address = recipient.Address with { Incarnation = Guid.NewGuid() } };
        store.Register(newIncarnation);
        Refuses("recipient_unavailable", () => store.Accept(Message(sender, recipient), identity));
        Check(store.Directory().Count(s => s.Address.SessionId == recipient.Address.SessionId) == 1, "Restart replaces exact incarnation.");
        uncertain = Message(sender, newIncarnation);
        store.Accept(uncertain, identity); store.BeginDelivery();
    }
    using (var recovered = new BrokerStore(root, clock: clock))
    {
        Check(recovered.BrokerId == identity, "Broker identity persists.");
        Check(recovered.Directory().Count == 0, "Restart does not advertise stale sessions.");
        Check(recovered.Messages(uncertain.Recipient).Single(r => r.Message.Id == uncertain.Id).Status == MessageStatus.Uncertain,
            "Crash during admission requires reconciliation, never blind resend.");
        Check(recovered.Accept(uncertain, identity).Status == MessageStatus.Uncertain,
            "Authenticated retry recovers the original receipt without a live session.");
        Check(recovered.BeginDelivery() is null, "Restart does not re-submit uncertain native delivery.");
        var sender = Session(recovered, "new-sender"); var recipient = Session(recovered, "new-recipient");
        recovered.Register(sender); recovered.Register(recipient);
        var expiring = Message(sender, recipient) with { ExpiresAt = clock.GetUtcNow().AddSeconds(1) };
        recovered.Accept(expiring, identity);
        clock.Advance(TimeSpan.FromSeconds(2));
        Check(recovered.BeginDelivery() is null, "Expired message is never delivered.");
        Check(recovered.Messages(recipient.Address).Single().Status == MessageStatus.Expired, "Expiry has durable visible status.");
        clock.Advance(TimeSpan.FromMinutes(3));
        Check(recovered.Directory().Count == 0, "Expired session leases disappear.");
    }
    using (var small = new BrokerStore(Path.Combine(root, "bounded"), new(MaximumMessages: 2, MaximumPendingPerSession: 1), clock))
    {
        var from = Session(small, "from"); var to = Session(small, "to"); small.Register(from); small.Register(to);
        small.Accept(Message(from, to), small.BrokerId);
        Refuses("recipient_queue_full", () => small.Accept(Message(from, to), small.BrokerId));
        Refuses("invalid_message", () => small.Accept(Message(from, to) with { Text = new string('x', 16385) }, small.BrokerId));
    }
    AuditDurableState();
    AuditWriteFailures();
    AuditQueueAndMutablePolicy();
    AuditUnsubmittedClaims();
    AuditReceiptRecoveryQuery();
    AuditReceiptQueryFairness();
    AuditAtomicReplyRetries();
    passed += ReplyLifetimeChecks.Run(root);
    using (var receipts = new BrokerStore(Path.Combine(root, "explicit-receipts"), clock: clock))
    {
        var from = Session(receipts, "from"); var to = Session(receipts, "to");
        receipts.Register(from); receipts.Register(to);
        var message = Message(from, to);
        receipts.Accept(message, receipts.BrokerId); receipts.BeginDelivery();
        Check(receipts.CompleteDelivery(message.Id, MessageStatus.Submitted).Status == MessageStatus.Submitted,
            "Transport admission remains Submitted, separate from recipient delivery.");
        Check(receipts.BeginDelivery() is null, "Submitted messages are never blindly replayed.");
        Refuses("recipient_mismatch", () => receipts.RecordRecipientReceipt(message.Id, from.Address, receipts.BrokerId, MessageStatus.Delivered));
        Refuses("recipient_mismatch", () => receipts.RecordRecipientReceipt(message.Id, to.Address, Guid.NewGuid(), MessageStatus.Delivered));
        Check(receipts.RecordRecipientReceipt(message.Id, to.Address, receipts.BrokerId, MessageStatus.Delivered).Status == MessageStatus.Delivered,
            "An authenticated exact recipient acknowledges delivery.");
        Check(receipts.RecordRecipientReceipt(message.Id, to.Address, receipts.BrokerId, MessageStatus.Delivered).Status == MessageStatus.Delivered,
            "Recipient receipt is idempotent.");
        Refuses("receipt_final", () => receipts.RecordRecipientReceipt(message.Id, to.Address, receipts.BrokerId, MessageStatus.Held));
        Check(receipts.FindReceipt(message.Id)?.Status == MessageStatus.Delivered, "Receipt lookup preserves final state.");
    }
    Console.WriteLine($"AgentBroker core smoke and regression: {passed} assertions passed.");
    return 0;
}
catch (Exception e) { Console.Error.WriteLine(e); return 1; }
finally { Directory.Delete(root, recursive: true); }

void AuditDurableState()
{
    string fixture = Path.Combine(root, "valid-state-fixture");
    Guid fixtureIdentity;
    using (var store = new BrokerStore(fixture, clock: clock))
    {
        fixtureIdentity = store.BrokerId;
        var from = Session(store, "from"); var to = Session(store, "to");
        store.Register(from); store.Register(to); store.Accept(Message(from, to), store.BrokerId);
        store.AuthorizePeer(new(Guid.NewGuid(), "Synthetic peer", "https://192.0.2.2:8443/", new string('A', 64), clock.GetUtcNow()));
    }
    byte[] valid = File.ReadAllBytes(Path.Combine(fixture, "broker-state.json"));
    var mutations = new (string Name, Action<JsonNode> Mutate)[]
    {
        ("unknown-version", j => j["Version"] = 2),
        ("unknown-field", j => j["FutureField"] = "must not silently disappear"),
        ("null-peer", j => j["Peers"]![0] = null),
        ("null-peer-name", j => j["Peers"]![0]!["Name"] = null),
        ("invalid-peer-pin", j => j["Peers"]![0]!["CertificateSha256"] = "bad"),
        ("unknown-status", j => j["Messages"]![0]!["Status"] = 999),
        ("missing-status", j => j["Messages"]![0]!.AsObject().Remove("Status")),
        ("missing-created-time", j => j["Messages"]![0]!["Message"]!.AsObject().Remove("CreatedAt")),
        ("null-receipt", j => j["Messages"]![0] = null),
        ("null-message", j => j["Messages"]![0]!["Message"] = null),
        ("null-sender", j => j["Messages"]![0]!["Message"]!["Sender"] = null),
        ("empty-address", j => j["Messages"]![0]!["Message"]!["Sender"]!["BrokerId"] = Guid.Empty),
        ("null-message-text", j => j["Messages"]![0]!["Message"]!["Text"] = null),
        ("invalid-lifetime", j => j["Messages"]![0]!["Message"]!["ExpiresAt"] = clock.GetUtcNow().AddDays(2)),
        ("negative-attempt", j => j["Messages"]![0]!["Attempts"] = -1),
        ("fabricated-delivery", j => j["Messages"]![0]!["Status"] = (int)MessageStatus.Delivered),
        ("invalid-reason", j => j["Messages"]![0]!["Reason"] = "message text is not a reason code"),
        ("duplicate-peer", j => j["Peers"]!.AsArray().Add(j["Peers"]![0]!.DeepClone())),
    };
    foreach (var mutation in mutations)
    {
        string directory = Path.Combine(root, "bad-state-" + mutation.Name);
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "broker-state.json");
        var json = JsonNode.Parse(valid)!;
        mutation.Mutate(json);
        File.WriteAllText(file, json.ToJsonString());
        byte[] malformed = File.ReadAllBytes(file);
        try { using var rejected = new BrokerStore(directory, clock: clock); throw new Exception("Malformed state accepted: " + mutation.Name); }
        catch (InvalidDataException) { Check(File.ReadAllBytes(file).SequenceEqual(malformed), mutation.Name + " preserves exact malformed bytes."); }
        File.WriteAllBytes(file, valid);
        using var reopened = new BrokerStore(directory, clock: clock);
        Check(reopened.BrokerId == fixtureIdentity, mutation.Name + " releases owner lock for recovery.");
    }
    string invalidJsonDirectory = Path.Combine(root, "invalid-json");
    Directory.CreateDirectory(invalidJsonDirectory);
    string invalidJsonPath = Path.Combine(invalidJsonDirectory, "broker-state.json");
    File.WriteAllText(invalidJsonPath, "{broken");
    try { using var rejected = new BrokerStore(invalidJsonDirectory); throw new Exception("Invalid JSON accepted."); }
    catch (InvalidDataException) { Check(File.ReadAllText(invalidJsonPath) == "{broken", "Malformed JSON is preserved without replacement."); }
    string invalidLimitsDirectory = Path.Combine(root, "invalid-limits");
    try { using var rejected = new BrokerStore(invalidLimitsDirectory, new(MaximumMessages: 0)); throw new Exception("Zero limit accepted."); }
    catch (ArgumentOutOfRangeException) { Check(!Directory.Exists(invalidLimitsDirectory), "Invalid limits fail before filesystem changes."); }
    string unchangedDirectory = Path.Combine(root, "unchanged-state"); Directory.CreateDirectory(unchangedDirectory);
    string unchangedPath = Path.Combine(unchangedDirectory, "broker-state.json");
    File.WriteAllText(unchangedPath, " \n" + System.Text.Encoding.UTF8.GetString(valid) + "\n ");
    byte[] beforeOpen = File.ReadAllBytes(unchangedPath);
    using (var reopened = new BrokerStore(unchangedDirectory, clock: clock))
        Check(File.ReadAllBytes(unchangedPath).SequenceEqual(beforeOpen), "Opening compatible state without recovery does not rewrite its bytes.");
}

void AuditWriteFailures()
{
    string directory = Path.Combine(root, "failed-writes");
    using var store = new BrokerStore(directory, clock: clock);
    var from = Session(store, "from"); var to = Session(store, "to");
    store.Register(from); store.Register(to);
    var message = Message(from, to); store.Accept(message, store.BrokerId);
    string durable = Path.Combine(directory, "broker-state.json");
    void RefuseWrite(Action mutation, string description)
    {
        byte[] before = File.ReadAllBytes(durable);
        using (var deny = new FileStream(durable + ".next", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            try { mutation(); throw new Exception("Write unexpectedly succeeded."); }
            catch (IOException) { Check(File.ReadAllBytes(durable).SequenceEqual(before), description + " preserves durable bytes."); }
        }
    }
    RefuseWrite(() => store.Accept(Message(from, to), store.BrokerId), "Failed enqueue");
    Check(store.Messages(to.Address).Count == 1, "Failed enqueue does not publish memory-only receipt.");
    var peer = new PeerTrust(Guid.NewGuid(), "Synthetic", "https://192.0.2.3/", new string('B', 64), clock.GetUtcNow());
    RefuseWrite(() => store.AuthorizePeer(peer), "Failed authorization");
    Check(store.Peers().Count == 0, "Failed authorization does not expose trust.");
    RefuseWrite(() => store.BeginDelivery(), "Failed claim");
    Check(store.Messages(to.Address).Single().Status == MessageStatus.Accepted, "Failed claim remains eligible without incrementing attempt.");
    Check(store.BeginDelivery()?.Attempts == 1, "A successful claim after write failure increments once.");
    byte[] beforePoll = File.ReadAllBytes(durable);
    DateTime beforePollWrite = File.GetLastWriteTimeUtc(durable);
    using (var deny = new FileStream(durable + ".next", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        Check(store.BeginDelivery() is null && File.ReadAllBytes(durable).SequenceEqual(beforePoll) &&
            File.GetLastWriteTimeUtc(durable) == beforePollWrite,
            "An idle delivery poll makes no snapshot write, even when writes are explicitly blocked.");
    RefuseWrite(() => store.CompleteDelivery(message.Id, MessageStatus.Delivered), "Failed completion");
    Check(store.Messages(to.Address).Single().Status == MessageStatus.Submitting, "Failed completion does not advertise delivered.");
    store.CompleteDelivery(message.Id, MessageStatus.Delivered);
    store.AuthorizePeer(peer);
    RefuseWrite(() => store.RevokePeer(peer.BrokerId), "Failed revocation");
    Check(store.Peers().Single().BrokerId == peer.BrokerId, "Failed revocation leaves prior trust consistently visible.");
}

void AuditQueueAndMutablePolicy()
{
    using var store = new BrokerStore(Path.Combine(root, "mutable-policy"), new(MaximumSessions: 2, MaximumPendingPerSession: 2), clock);
    var from = Session(store, "from"); var to = Session(store, "to"); store.Register(from); store.Register(to);
    var replacement = to with { Address = to.Address with { Incarnation = Guid.NewGuid() } };
    store.Register(replacement); to = replacement;
    Check(store.Directory().Count == 2 && store.Directory().Any(s => s.Address == replacement.Address), "Full session capacity still permits replacing the same exact vendor session incarnation.");
    var first = Message(from, to); var second = Message(from, to);
    store.Accept(first, store.BrokerId); store.Accept(second, store.BrokerId);
    Check(store.BeginDelivery()?.Message.Id == first.Id && store.BeginDelivery() is null, "One recipient has at most one in-flight claim while its native state remains idle.");
    Refuses("recipient_queue_full", () => store.Accept(Message(from, to), store.BrokerId));
    store.CompleteDelivery(first.Id, MessageStatus.Delivered);
    Check(store.BeginDelivery()?.Message.Id == second.Id, "Resolving the first claim releases recipient admission.");
    store.CompleteDelivery(second.Id, MessageStatus.Delivered);
    store.Register(to with { Capabilities = SessionCapabilities.Receive | SessionCapabilities.WakeIdle });
    var reply = Message(to, from) with { ConversationId = first.ConversationId, ReplyTo = first.Id, RemainingReplies = 7 };
    Refuses("sender_cannot_reply", () => store.Accept(reply, store.BrokerId));
    store.Register(to);

    var changed = Message(from, to); store.Accept(changed, store.BrokerId);
    store.Register(to with { Capabilities = SessionCapabilities.WakeIdle });
    Check(store.BeginDelivery() is null && store.Messages(to.Address).Single(r => r.Message.Id == changed.Id).Reason == "recipient_cannot_receive",
        "Removing Receive after admission prevents delivery and records its refusal.");
    store.Register(to);
    var peer = new PeerTrust(Guid.NewGuid(), "Synthetic", "https://192.0.2.4/", new string('C', 64), clock.GetUtcNow()); store.AuthorizePeer(peer);
    var remote = Session(store, "remote") with { Address = new(peer.BrokerId, "remote", Guid.NewGuid()) };
    var privateAfterAdmission = Message(remote, to); store.Accept(privateAfterAdmission, peer.BrokerId);
    store.Register(to with { IsPrivate = true });
    Check(store.BeginDelivery() is null && store.Messages(to.Address).Single(r => r.Message.Id == privateAfterAdmission.Id).Reason == "private_session",
        "Becoming private after peer admission prevents the queued delivery.");
    var localPrivate = Message(from, to); store.Accept(localPrivate, store.BrokerId);
    Check(store.BeginDelivery()?.Message.Id == localPrivate.Id, "An explicitly allowed same-broker message can still reach a private local session.");
    store.CompleteDelivery(localPrivate.Id, MessageStatus.Delivered);
    var muted = Message(from, to); store.Accept(muted, store.BrokerId);
    store.Register(to with { IsPrivate = true, Activity = SessionActivity.Interrupted });
    Check(store.BeginDelivery() is null, "Interrupted local mute blocks automatic claim even for an accepted local message.");
    store.Register(to);
    store.BeginDelivery(); store.CompleteDelivery(muted.Id, MessageStatus.Refused);
    store.Register(to);
    var revokedInFlight = Message(remote, to); store.Accept(revokedInFlight, peer.BrokerId);
    Check(store.BeginDelivery()?.Message.Id == revokedInFlight.Id, "Peer message is claimed for revocation race test.");
    store.RevokePeer(peer.BrokerId);
    Check(store.Messages(to.Address).Single(r => r.Message.Id == revokedInFlight.Id).Status == MessageStatus.Uncertain,
        "Revoking an in-flight peer records uncertainty instead of claiming it never reached the adapter.");
    Refuses("message_not_submitting", () => store.CompleteDelivery(revokedInFlight.Id, MessageStatus.Delivered));
    Refuses("unpaired_peer", () => store.Accept(revokedInFlight, peer.BrokerId));

    var expiring = Message(from, to) with { ExpiresAt = clock.GetUtcNow().AddSeconds(1) }; store.Accept(expiring, store.BrokerId);
    clock.Advance(TimeSpan.FromSeconds(2));
    Check(store.Accept(expiring, store.BrokerId).Status == MessageStatus.Expired, "An authenticated expired retry receives an expired receipt, not stale Accepted.");
    store.Disconnect(to.Address);
    Check(store.Accept(expiring, store.BrokerId).Status == MessageStatus.Expired, "Expired receipt retry does not need the old recipient incarnation.");
}

void AuditUnsubmittedClaims()
{
    string directory = Path.Combine(root, "unsubmitted-claims");
    using var store = new BrokerStore(directory, clock: clock);
    var from = Session(store, "from"); var to = Session(store, "to");
    store.Register(from); store.Register(to with { Activity = SessionActivity.Unknown });
    var message = Message(from, to); store.Accept(message, store.BrokerId);
    Check(store.BeginDelivery() is null, "Unknown activity alone never authorizes a wake attempt.");
    Refuses("invalid_session", () => store.Register(to with { Capabilities = SessionCapabilities.NativeIdleGuard }));
    store.Register(to with { Activity = SessionActivity.Unknown, Capabilities = to.Capabilities | SessionCapabilities.NativeIdleGuard });
    Check(store.BeginDelivery()?.Message.Id == message.Id, "An explicit atomic idle capability permits guarded admission with unknown activity.");
    string durable = Path.Combine(directory, "broker-state.json");
    byte[] before = File.ReadAllBytes(durable);
    using (var deny = new FileStream(durable + ".next", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
    {
        try { store.ReleaseUnsubmittedClaim(message.Id); throw new Exception("Release write unexpectedly succeeded."); }
        catch (IOException) { Check(File.ReadAllBytes(durable).SequenceEqual(before), "Failed unsubmitted release preserves durable state."); }
    }
    Check(store.FindReceipt(message.Id)?.Status == MessageStatus.Submitting, "Failed unsubmitted release leaves the current claim intact.");
    var released = store.ReleaseUnsubmittedClaim(message.Id);
    Check(released.Status == MessageStatus.Accepted && released.Attempts == 0 && released.Message.Id == message.Id,
        "Authoritatively unsubmitted release preserves the stable message ID without consuming its submission attempt.");
    Refuses("message_not_submitting", () => store.ReleaseUnsubmittedClaim(message.Id));
    store.Register(to with { Activity = SessionActivity.Busy, Capabilities = to.Capabilities | SessionCapabilities.NativeIdleGuard });
    Check(store.BeginDelivery() is null, "Known busy activity stays queued even with an atomic idle capability.");
    store.Register(to with { Activity = SessionActivity.Idle });
    var later = Message(from, to); store.Accept(later, store.BrokerId);
    Check(store.BeginDelivery(new HashSet<Guid> { message.Id })?.Message.Id == later.Id,
        "A deferred earliest message can be excluded so later eligible messages still make progress.");
    store.CompleteDelivery(later.Id, MessageStatus.Delivered);
    Check(store.BeginDelivery()?.Attempts == 1, "A proven unsubmitted claim can be retried once actual idle activity is observed.");
    store.CompleteDelivery(message.Id, MessageStatus.Uncertain);
    Refuses("message_not_submitting", () => store.ReleaseUnsubmittedClaim(message.Id));
    Check(store.BeginDelivery() is null, "An uncertain submission can never use the unsubmitted-release path.");
    var submitted = Message(from, to); store.Accept(submitted, store.BrokerId); store.BeginDelivery();
    store.CompleteDelivery(submitted.Id, MessageStatus.Submitted);
    Refuses("message_not_submitting", () => store.ReleaseUnsubmittedClaim(submitted.Id));
    var expiring = Message(from, to) with { ExpiresAt = clock.GetUtcNow().AddSeconds(1) };
    store.Accept(expiring, store.BrokerId); store.BeginDelivery(); clock.Advance(TimeSpan.FromSeconds(2));
    Check(store.ReleaseUnsubmittedClaim(expiring.Id).Status == MessageStatus.Expired, "An expired proven-unsubmitted claim is never requeued.");
    Check(store.RecordRecipientReceipt(submitted.Id, to.Address, store.BrokerId, MessageStatus.Held).Status == MessageStatus.Held,
        "Explicit native held receipt preserves a submitted message without retrying it.");
    Check(store.RecordRecipientReceipt(submitted.Id, to.Address, store.BrokerId, MessageStatus.Delivered).Status == MessageStatus.Delivered,
        "A later exact recipient receipt can reconcile native hold into delivery.");
}

void AuditReceiptRecoveryQuery()
{
    string directory = Path.Combine(root, "receipt-recovery-query");
    Guid peerId = Guid.NewGuid(); Guid messageId;
    using (var store = new BrokerStore(directory, clock: clock))
    {
        store.AuthorizePeer(new(peerId, "Synthetic", "https://192.0.2.5/", new string('D', 64), clock.GetUtcNow()));
        var from = Session(store, "from"); store.Register(from);
        var to = Session(store, "remote") with { Address = new(peerId, "remote", Guid.NewGuid()) };
        var message = Message(from, to); messageId = message.Id;
        store.Accept(message, store.BrokerId); store.BeginDelivery(); store.CompleteDelivery(message.Id, MessageStatus.Submitted);
        Check(store.OutboundAwaitingReceipt(peerId).Single().Status == MessageStatus.Submitted, "Persisted outbound query includes transport-only receipts.");
        Refuses("invalid_limit", () => store.OutboundAwaitingReceipt(peerId, 0));
        Refuses("invalid_limit", () => store.OutboundAwaitingReceipt(peerId, 65));
        Refuses("invalid_peer", () => store.OutboundAwaitingReceipt(Guid.Empty));
    }
    using var recovered = new BrokerStore(directory, clock: clock);
    Check(recovered.Directory().Count == 0 && recovered.OutboundAwaitingReceipt(peerId, 1).Single() is { Status: MessageStatus.Uncertain } receipt && receipt.Message.Id == messageId,
        "Post-restart outbound reconciliation finds durable uncertainty without any live sender lease.");
}

void AuditReceiptQueryFairness()
{
    using var store = new BrokerStore(Path.Combine(root, "receipt-query-fairness"), new(MaximumPendingPerSession: 128), clock);
    Guid peerId = Guid.NewGuid();
    store.AuthorizePeer(new(peerId, "Synthetic", "https://192.0.2.6/", new string('E', 64), clock.GetUtcNow()));
    var from = Session(store, "from"); store.Register(from);
    var to = Session(store, "remote") with { Address = new(peerId, "remote", Guid.NewGuid()) };
    Guid firstId = Guid.Empty;
    for (int i = 0; i < 70; i++)
    {
        var message = Message(from, to);
        if (i == 0) { firstId = message.Id; message = message with { ExpiresAt = clock.GetUtcNow().AddSeconds(1) }; }
        store.Accept(message, store.BrokerId); store.BeginDelivery();
        store.CompleteDelivery(message.Id, i % 2 == 0 ? MessageStatus.Held : MessageStatus.Submitted);
    }
    var first = store.OutboundAwaitingReceipt(peerId);
    var second = store.OutboundAwaitingReceipt(peerId, afterMessageId: first[^1].Message.Id);
    Check(first.Count == 64 && second.Count == 64 && first.Concat(second).Select(r => r.Message.Id).Distinct().Count() == 70,
        "A wrapping receipt cursor fairly reaches more than64 held/unconfirmed records.");
    Check(second[0].Message.Id != first[0].Message.Id && second.Skip(6).Select(r => r.Message.Id).SequenceEqual(first.Take(58).Select(r => r.Message.Id)),
        "Receipt query advances after the cursor and wraps within the same eligible set.");
    clock.Advance(TimeSpan.FromSeconds(2));
    var afterExpired = store.OutboundAwaitingReceipt(peerId, afterMessageId: firstId);
    Check(afterExpired.Count == 64 && afterExpired.All(r => r.Message.Id != firstId) && afterExpired[0].Message.Id == first[1].Message.Id,
        "An expired cursor restarts at the first remaining eligible receipt without starving the directory.");
}

void AuditAtomicReplyRetries()
{
    var replyClock = new TestClock();
    BrokerSession Participant(BrokerStore store, string id) => new(new(store.BrokerId, id, Guid.NewGuid()), id, "synthetic",
        SessionCapabilities.Receive | SessionCapabilities.Reply, SessionActivity.Idle, replyClock.GetUtcNow().AddMinutes(1));
    BrokerMessage Parent(BrokerSession sender, BrokerSession recipient, int remaining = 8) => new(Guid.NewGuid(), Guid.NewGuid(), null,
        sender.Address, recipient.Address, "Synthetic original message.", replyClock.GetUtcNow(), replyClock.GetUtcNow().AddMinutes(30), remaining);
    using (var store = new BrokerStore(Path.Combine(root, "atomic-replies"), clock: replyClock))
    {
        var from = Participant(store, "from"); var to = Participant(store, "to");
        store.Register(from); store.Register(to);
        var parent = Parent(from, to); store.Accept(parent, store.BrokerId);
        Guid replyId = Guid.NewGuid();
        var first = store.AcceptReply(parent.Id, replyId, "Synthetic stable reply.", store.BrokerId);
        Check(first.Message.ReplyTo == parent.Id && first.Message.ConversationId == parent.ConversationId &&
            first.Message.Sender == to.Address && first.Message.Recipient == from.Address && first.Message.RemainingReplies == 7,
            "Atomic reply reverses exact participants and decrements the parent's budget.");
        string durable = Path.Combine(root, "atomic-replies", "broker-state.json");
        byte[] acceptedBytes = File.ReadAllBytes(durable);
        replyClock.Advance(TimeSpan.FromSeconds(2));
        var retried = store.AcceptReply(parent.Id, replyId, first.Message.Text, store.BrokerId);
        Check(retried == first && retried.Message.CreatedAt == first.Message.CreatedAt && retried.Message.ExpiresAt == first.Message.ExpiresAt,
            "Reply retry after clock advancement retains the first accepted timestamps and receipt.");
        Check(File.ReadAllBytes(durable).SequenceEqual(acceptedBytes), "An exact reply retry causes no durable rewrite or additional message.");
        Refuses("message_id_collision", () => store.AcceptReply(parent.Id, replyId, "Different reply text.", store.BrokerId));
        var anotherParent = Parent(from, to) with { ConversationId = parent.ConversationId };
        store.Accept(anotherParent, store.BrokerId);
        Refuses("message_id_collision", () => store.AcceptReply(anotherParent.Id, replyId, first.Message.Text, store.BrokerId));
        Refuses("message_id_collision", () => store.AcceptReply(parent.Id, parent.Id, parent.Text, store.BrokerId));
        Refuses("sender_mismatch", () => store.AcceptReply(parent.Id, replyId, first.Message.Text, Guid.NewGuid()));
        Refuses("invalid_reply", () => store.AcceptReply(Guid.NewGuid(), Guid.NewGuid(), "Missing parent.", store.BrokerId));
        Guid concurrentId = Guid.NewGuid();
        var replies = Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            store.AcceptReply(parent.Id, concurrentId, "Concurrent stable reply.", store.BrokerId)))).GetAwaiter().GetResult();
        Check(replies.All(receipt => receipt == replies[0]), "Concurrent reply retries share exactly one accepted message and timestamp pair.");
        using (var persisted = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(durable)))
            Check(persisted.RootElement.GetProperty("Messages").EnumerateArray().Count(receipt =>
                receipt.GetProperty("Message").GetProperty("Id").GetGuid() == concurrentId) == 1,
                "Sixteen concurrent reply attempts persist exactly one reply record.");
        var withoutReply = to with { Capabilities = SessionCapabilities.Receive };
        store.Register(withoutReply);
        Refuses("sender_cannot_reply", () => store.AcceptReply(parent.Id, Guid.NewGuid(), "Capability boundary.", store.BrokerId));
        store.Register(to);
        store.Disconnect(from.Address); store.Disconnect(to.Address);
        Check(store.AcceptReply(parent.Id, replyId, first.Message.Text, store.BrokerId) == first,
            "Exact reply receipt recovery after disconnection never creates another delivery.");
        Refuses("sender_unavailable", () => store.AcceptReply(parent.Id, Guid.NewGuid(), "New disconnected reply.", store.BrokerId));
    }
    using (var store = new BrokerStore(Path.Combine(root, "peer-replies"), clock: replyClock))
    {
        var localSender = Participant(store, "local"); store.Register(localSender);
        Guid peerId = Guid.NewGuid();
        store.AuthorizePeer(new(peerId, "Synthetic peer", "https://192.0.2.9/", new string('A', 64), replyClock.GetUtcNow()));
        var remote = Participant(store, "remote") with { Address = new(peerId, "remote", Guid.NewGuid()) };
        var parent = Parent(localSender, remote); store.Accept(parent, store.BrokerId);
        Guid replyId = Guid.NewGuid();
        var reply = store.AcceptReply(parent.Id, replyId, "Authenticated peer reply.", peerId);
        Check(reply.Message.Sender == remote.Address && reply.Message.Recipient == localSender.Address,
            "Authenticated direct peer can reply only as the original exact recipient.");
        Refuses("sender_mismatch", () => store.AcceptReply(parent.Id, Guid.NewGuid(), "Local caller cannot impersonate peer.", store.BrokerId));
        store.Register(localSender with { IsPrivate = true });
        Refuses("private_session", () => store.AcceptReply(parent.Id, Guid.NewGuid(), "Private local target.", peerId));
        store.RevokePeer(peerId);
        Refuses("unpaired_peer", () => store.AcceptReply(parent.Id, replyId, reply.Message.Text, peerId));
    }
    using (var store = new BrokerStore(Path.Combine(root, "expired-parent-replies"), new(DefaultMessageLifetimeMinutes: 30), replyClock))
    {
        var from = Participant(store, "from"); var to = Participant(store, "to");
        store.Register(from); store.Register(to);
        var parent = Parent(from, to) with { ExpiresAt = replyClock.GetUtcNow().AddSeconds(1) };
        store.Accept(parent, store.BrokerId);
        store.RecordRecipientReceipt(parent.Id, to.Address, store.BrokerId, MessageStatus.Delivered);
        Guid replyId = Guid.NewGuid();
        var first = store.AcceptReply(parent.Id, replyId, "Already accepted reply.", store.BrokerId);
        replyClock.Advance(TimeSpan.FromSeconds(2));
        var finishedWork = store.AcceptReply(parent.Id, Guid.NewGuid(), "Reply after the delivered task's original deadline.", store.BrokerId);
        Check(finishedWork.Status == MessageStatus.Accepted && finishedWork.Message.CreatedAt > parent.ExpiresAt &&
            finishedWork.Message.ExpiresAt > replyClock.GetUtcNow() && finishedWork.Message.RemainingReplies == parent.RemainingReplies - 1,
            "A delivered parent's deadline does not prevent finishing work and replying with a fresh bounded delivery lifetime.");
        Check(store.AcceptReply(parent.Id, replyId, first.Message.Text, store.BrokerId) == first,
            "Parent expiry does not fabricate a new payload for an already accepted reply retry.");
        var undelivered = Parent(from, to) with { ExpiresAt = replyClock.GetUtcNow().AddSeconds(1) };
        store.Accept(undelivered, store.BrokerId);
        replyClock.Advance(TimeSpan.FromSeconds(2));
        Check(store.Messages(to.Address).Single(receipt => receipt.Message.Id == undelivered.Id).Status == MessageStatus.Expired,
            "An undelivered parent still reports its own expired delivery receipt.");
        var directReply = Parent(to, from) with { ConversationId = undelivered.ConversationId, ReplyTo = undelivered.Id,
            RemainingReplies = undelivered.RemainingReplies - 1 };
        var lowerLevel = store.Accept(directReply, store.BrokerId);
        var helper = store.AcceptReply(undelivered.Id, Guid.NewGuid(), "Consistent retained-parent reply.", store.BrokerId);
        Check(lowerLevel.Status == MessageStatus.Accepted && helper.Status == MessageStatus.Accepted &&
            helper.Message.Sender == directReply.Sender && helper.Message.Recipient == directReply.Recipient &&
            helper.Message.RemainingReplies == directReply.RemainingReplies,
            "Convenience and lower-level reply admission share the retained expired-parent contract without inventing a conversation deadline.");
        replyClock.Advance(TimeSpan.FromMinutes(31));
        Check(store.AcceptReply(parent.Id, replyId, first.Message.Text, store.BrokerId).Status == MessageStatus.Expired,
            "An exact reply retry reports its own expired receipt without resubmission.");
    }
    using (var store = new BrokerStore(Path.Combine(root, "reply-budget-zero"), new(MaximumReplies: 0), replyClock))
    {
        var from = Participant(store, "from"); var to = Participant(store, "to");
        store.Register(from); store.Register(to);
        var parent = Parent(from, to, remaining: 0); store.Accept(parent, store.BrokerId);
        Refuses("invalid_reply", () => store.AcceptReply(parent.Id, Guid.NewGuid(), "No remaining reply budget.", store.BrokerId));
        Check(store.Messages(to.Address).Count == 1, "An exhausted reply budget cannot persist another message.");
    }
}

sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan elapsed) => now += elapsed;
}
