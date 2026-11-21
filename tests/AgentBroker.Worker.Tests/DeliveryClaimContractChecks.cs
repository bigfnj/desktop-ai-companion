using AgentBroker.Core;
using AgentBroker.Worker;
using System.Text.Json;

internal static class DeliveryClaimContractChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: CLAIM_CONTRACT_EXECUTED");
        Console.WriteLine("CLAIM_CORE_SHA256=" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(typeof(MessageReceipt).Assembly.Location))));
        PureContracts(check);
        using var scratch = OwnedScratch.Create("claim-contract");
        using var store = new BrokerStore(scratch.Root);
        var sender = new SessionAddress(store.BrokerId, "contract-sender", Guid.NewGuid());
        var recipient = new SessionAddress(store.BrokerId, "contract-recipient", Guid.NewGuid());
        foreach (var address in new[] { sender, recipient }) store.Register(new(address, "Synthetic contract", "synthetic",
            SessionCapabilities.Receive | SessionCapabilities.WakeIdle, SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(1)));
        var accepted = store.Send(sender, recipient, Guid.NewGuid(), "Synthetic contract.", null, 1, store.BrokerId);
        var epoch = BrokerStore.ReceiptAdmissionEpoch(accepted);
        check(epoch != Guid.Empty && BrokerStore.ReceiptAdmissionEpoch(accepted with { Reason = "synthetic_copy" }) == epoch,
            "CLAIM_COPY_IDENTITY: ordinary record copies preserve admission identity");
        check(store.BeginDeliveryClaim(new Dictionary<Guid, Guid> { [accepted.Message.Id] = epoch }) is null,
            "CLAIM_EXCLUSION_CURRENT: an exclusion belongs to the captured admission");
        var claim = store.BeginDeliveryClaim(new Dictionary<Guid, Guid> { [accepted.Message.Id] = Guid.NewGuid() })
            ?? throw new InvalidOperationException("CLAIM_EXCLUSION_NONMATCH: a matching current message was omitted.");
        check(claim.Message.Id == accepted.Message.Id, "CLAIM_EXCLUSION_NONMATCH: an independent admission stamp does not exclude the current message");
        check(claim.Matches(claim.Receipt) && claim.AdmissionEpoch == epoch && !claim.Matches(accepted),
            "CLAIM_MATCH_PREDICATE: only a submitting snapshot with the captured stamps matches");
        var independent = DeliveryClaim.Capture(JsonSerializer.Deserialize<MessageReceipt>(JsonSerializer.Serialize(claim.Receipt))!);
        check(!independent.Matches(claim.Receipt), "CLAIM_NONMATCH_PREDICATE: matching wire values do not confer local claim identity");
        byte[] before = File.ReadAllBytes(Path.Combine(scratch.Root, "broker-state.json"));
        check(!store.TryCompleteDelivery(independent, MessageStatus.Submitted, "contract", out _) &&
            !store.TryReleaseUnsubmittedClaim(independent, "contract", out _) &&
            !store.TryRecordPeerReceipt(independent.Receipt, recipient, store.BrokerId, MessageStatus.Delivered, null, out _) &&
            File.ReadAllBytes(Path.Combine(scratch.Root, "broker-state.json")).SequenceEqual(before),
            "CLAIM_MISMATCH_NO_WRITE: unmatched contract snapshots apply no mutation");
        check(store.TryReleaseUnsubmittedClaim(claim, "contract_ready", out var released) && released!.Attempts == 0 &&
            BrokerStore.ReceiptAdmissionEpoch(released) == epoch,
            "CLAIM_CURRENT_RELEASE: a current proven-unsubmitted claim releases while retaining its admission");
        var current = store.BeginDeliveryClaim()!;
        check(current.Matches(current.Receipt) && !claim.Matches(current.Receipt),
            "CLAIM_FRESH_ATTEMPT: the next normal attempt has a fresh claim nonce");
        check(store.TryCompleteDelivery(current, MessageStatus.Submitted, "contract_submitted", out var completed) &&
            BrokerStore.ReceiptAdmissionEpoch(completed!) == epoch,
            "CLAIM_CURRENT_COMPLETE: a current claim completes with unchanged admission identity");
        store.RecordRecipientReceipt(accepted.Message.Id, recipient, store.BrokerId, MessageStatus.Delivered);
        var acknowledged = store.FindReceipt(accepted.Message.Id)!;
        check(acknowledged.Status == MessageStatus.Delivered && acknowledged.Message.Text == "" &&
            BrokerStore.ReceiptAdmissionEpoch(acknowledged) == epoch &&
            BrokerStore.ReceiptAdmissionEpoch(store.Messages(sender).Single()) == epoch,
            "CLAIM_TOMBSTONE_COPY: tombstone conversion and rendering preserve captured admission identity");
        var peer = Guid.NewGuid();
        var remote = new SessionAddress(peer, "contract-remote", Guid.NewGuid());
        store.AuthorizePeer(new(peer, "Synthetic contract peer", "https://127.0.0.1:17500/", new string('A', 64), DateTimeOffset.UtcNow));
        var outbound = store.Send(sender, remote, Guid.NewGuid(), "Synthetic peer contract.", null, 1, store.BrokerId);
        var outboundClaim = store.BeginDeliveryClaim()!;
        check(store.TryCompleteDelivery(outboundClaim, MessageStatus.Submitted, "contract_forwarded", out var waiting) &&
            store.TryRecordPeerReceipt(waiting!, remote, peer, MessageStatus.Delivered, null, out var peerReceipt) &&
            BrokerStore.ReceiptAdmissionEpoch(peerReceipt!) == BrokerStore.ReceiptAdmissionEpoch(outbound),
            "CLAIM_PEER_CURRENT: current captured peer receipts apply without changing admission identity");
        ObservationContracts(check, accepted);
        await PublicationContract(check);
    }

    // Component contracts run before any store/fixture workflow, including under the narrowly scoped unit controls.
    private static void PureContracts(Action<bool, string> check)
    {
        Guid broker = Guid.NewGuid();
        var message = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null,
            new(broker, "pure-sender", Guid.NewGuid()), new(broker, "pure-recipient", Guid.NewGuid()),
            "Synthetic pure value.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));
        var snapshot = new MessageReceipt(message, MessageStatus.Submitting, message.CreatedAt, Attempts: 1);
        string json = JsonSerializer.Serialize(snapshot);
        string shape = JsonSerializer.Serialize(new { snapshot.Message, snapshot.Status, snapshot.UpdatedAt, snapshot.Reason, snapshot.Attempts });
        var decoded = JsonSerializer.Deserialize<MessageReceipt>(json)!;
        check(json == shape && decoded == snapshot && decoded.GetHashCode() == snapshot.GetHashCode(),
            "CLAIM_JSON_AND_EQUALITY: only the original five fields serialize and define value equality/hash");
        check(BrokerStore.ReceiptAdmissionEpoch(decoded) != BrokerStore.ReceiptAdmissionEpoch(snapshot),
            "CLAIM_LOCAL_IDENTITY: deserialization creates a process-local snapshot identity");
        check(new[] { snapshot with { Message = message with { Text = "Different synthetic text." } },
            snapshot with { Status = MessageStatus.Held }, snapshot with { UpdatedAt = snapshot.UpdatedAt.AddTicks(1) },
            snapshot with { Reason = "different" }, snapshot with { Attempts = 2 } }.All(changed => changed != snapshot),
            "CLAIM_EQUALITY_FIELDS: each original value field independently participates in equality");
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var nonce = typeof(MessageReceipt).GetProperty("ClaimNonce", flags)!;
        var admission = typeof(MessageReceipt).GetProperty("AdmissionEpoch", flags)!;
        nonce.SetValue(snapshot, Guid.NewGuid());
        var handle = DeliveryClaim.Capture(snapshot);
        var differentAdmission = snapshot with { };
        admission.SetValue(differentAdmission, Guid.NewGuid());
        var differentNonce = snapshot with { };
        nonce.SetValue(differentNonce, Guid.NewGuid());
        check(handle.Matches(snapshot), "CLAIM_PURE_MATCH: matching nonempty component stamps satisfy the predicate");
        check(!handle.Matches(differentAdmission), "CLAIM_ADMISSION_PREDICATE: an independent admission stamp does not match");
        check(!handle.Matches(differentNonce), "CLAIM_NONCE_PREDICATE: an independent attempt stamp does not match");
    }

    private static void ObservationContracts(Action<bool, string> check, MessageReceipt snapshot)
    {
        var other = new MessageReceipt(snapshot.Message, snapshot.Status, snapshot.UpdatedAt, snapshot.Reason, snapshot.Attempts);
        var observations = new DeliveryObservations(TimeProvider.System);
        var first = new EngineBinding(true, "contract_first", DateTimeOffset.UtcNow);
        var second = first with { Reason = "contract_second" };
        observations.Record(snapshot.Message, new(AdapterAdmission.Submitted, "contract", EngineBinding: first), BrokerStore.ReceiptAdmissionEpoch(snapshot));
        check(observations.EngineAtAttempt(snapshot.Message, BrokerStore.ReceiptAdmissionEpoch(snapshot)) == first &&
            observations.EngineAtAttempt(other.Message, BrokerStore.ReceiptAdmissionEpoch(other)) is null,
            "CLAIM_OBSERVATION_SNAPSHOT: equal value snapshots retain independent observation identities");
        observations.Record(other.Message, new(AdapterAdmission.Submitted, "contract", EngineBinding: second), BrokerStore.ReceiptAdmissionEpoch(other));
        check(observations.EngineAtAttempt(snapshot.Message, BrokerStore.ReceiptAdmissionEpoch(snapshot)) is null &&
            observations.EngineAtAttempt(other.Message, BrokerStore.ReceiptAdmissionEpoch(other)) == second,
            "CLAIM_OBSERVATION_CURRENT: lookup uses the captured snapshot identity");
    }

    private static async Task PublicationContract(Action<bool, string> check)
    {
        using var scratch = OwnedScratch.Create("claim-publication-contract");
        using var store = new BrokerStore(scratch.Root);
        var sender = new SessionAddress(store.BrokerId, "publication-sender", Guid.NewGuid());
        var target = new SessionAddress(store.BrokerId, "publication-recipient", Guid.NewGuid());
        foreach (var address in new[] { sender, target }) store.Register(new(address, "Synthetic publication", "synthetic",
            SessionCapabilities.Receive | SessionCapabilities.WakeIdle, SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(1)));
        store.Send(sender, target, Guid.NewGuid(), "Synthetic publication.", null, 1, store.BrokerId);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        int published = 0;
        var claims = new DeferredDeliveryClaims(store, beforeObservation: () =>
        { entered.Set(); if (!resume.Wait(TimeSpan.FromSeconds(4))) throw new TimeoutException("Publication contract bound."); });
        var current = claims.BeginDelivery()!;
        var completion = Task.Run(() => claims.CompleteDelivery(current, MessageStatus.Submitted, "contract", () => Interlocked.Increment(ref published)));
        Task<DeliveryClaim?>? selection = null;
        try
        {
            check(entered.Wait(TimeSpan.FromSeconds(3)), "CLAIM_PUBLICATION_REACHED: current settlement reaches its publication boundary");
            using var selecting = new ManualResetEventSlim();
            selection = Task.Factory.StartNew(() => { selecting.Set(); return claims.BeginDelivery(); }, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            check(selecting.Wait(TimeSpan.FromSeconds(3)) && !selection.Wait(TimeSpan.FromMilliseconds(100)) && Volatile.Read(ref published) == 0,
                "CLAIM_PUBLICATION_SERIALIZED: selection waits while current settlement publishes its observation");
        }
        finally { resume.Set(); await completion.WaitAsync(TimeSpan.FromSeconds(3)); if (selection is not null) await selection.WaitAsync(TimeSpan.FromSeconds(3)); }
        check(published == 1, "CLAIM_PUBLICATION_ONCE: current settlement publishes exactly once");
    }
}
