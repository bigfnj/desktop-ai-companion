using System.Diagnostics;
using System.Text;
using AgentBroker.Core;
using AgentBroker.Protocol;
using AgentBroker.Worker;
using AgentBroker.Worker.Attachments;
using AgentBroker.Worker.Federation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

// Lane F round 2 (003-F-round2.md): the store, source, policy, sender-bound and forward checks. Each label names the guard
// a hygiene recipe breaks; the recipe names are in tests/run_agentbroker_hygiene_mutations.py (attachment-r2-*).
internal static partial class AttachmentChecks
{
    private static async Task RoundTwoAsync(Action<bool, string> check)
    {
        await StagingGraceAsync(check);
        await SenderCopyKeptAsync(check);
        await QuotaAsync(check);
        await FinalPathAsync(check);
        await ZoneKeptAsync(check);
        await FetchNoJunctionAsync(check);
        await TextTooLongAsync(check);
        PolicyRoundTwo(check);
        await ControlWhileOffAsync(check);
        await SenderBoundAsync(check);
        await ForwardOffLoopAsync(check);
        await RoundTwoBAsync(check);
        await RoundFiveAsync(check);
        await RoundSixAsync(check);
    }

    // F-2: Stage released its gate before the store recorded the message; a sweep in that window must not take the staging.
    private static async Task StagingGraceAsync(Action<bool, string> check)
    {
        var clock = new NearRealClock();
        await using var fixture = new ToolFixture("attachments-staging-grace", clock: clock, attachments: true);
        var store = fixture.Attachments!;
        var sender = fixture.Register("Synthetic staging sender");
        var recipient = fixture.Register("Synthetic staging recipient");
        string path = Source(fixture.Root, "notes.txt", "Synthetic staging content."u8.ToArray());
        Guid id = Guid.NewGuid();
        string envelope = store.Stage(id, "Synthetic note.", [path], out _);
        store.Sweep();
        bool survived = Directory.Exists(store.OutboxFolder(id)) && store.ReadManifest(id) == envelope;
        fixture.Store.Send(sender.Address, recipient.Address, id, envelope, null, 60, fixture.Store.BrokerId);
        check(survived && fixture.Tools.FetchAttachment(recipient.Address, id, 0) is { State: "ready" },
            "ATTACHMENT_STAGING_GRACE: a staging whose message has not reached the store yet survives a sweep, and the message then serves its file");
    }

    // F-3 (DECISIONS 38): the sender's copy of a paired recipient's message follows the local rule, not "gone at Submitted",
    // and an admitted received folder stays while its record does, up to 48 h.
    private static async Task SenderCopyKeptAsync(Action<bool, string> check)
    {
        var clock = new NearRealClock();
        await using var fixture = new ToolFixture("attachments-sender-copy", clock: clock, attachments: true);
        var store = fixture.Attachments!;
        Guid peerId = Guid.NewGuid();
        fixture.Store.AuthorizePeer(new(peerId, "Synthetic paired broker", "https://192.0.2.14/", new string('F', 64), clock.GetUtcNow()));
        var sender = fixture.Register("Synthetic copy sender");
        var remote = new SessionAddress(peerId, "codex:synthetic-remote", Guid.NewGuid());
        string path = Source(fixture.Root, "kept.txt", "Synthetic kept content."u8.ToArray());
        Guid id = Guid.NewGuid();
        string envelope = store.Stage(id, "Synthetic note.", [path], out _);
        var now = clock.GetUtcNow();
        var accepted = fixture.Store.Accept(new(id, Guid.NewGuid(), null, sender.Address, remote, envelope, now, now.AddHours(24)), fixture.Store.BrokerId);
        var claim = fixture.Store.BeginDeliveryClaim();
        bool submitted = claim is not null && claim.Message.Id == id && fixture.Store.TryCompleteDelivery(claim, MessageStatus.Submitted, "peer_receipt", out _);
        store.Sweep();
        bool keptSubmitted = submitted && Directory.Exists(store.OutboxFolder(id));
        fixture.Store.RecordPeerReceipt(fixture.Store.FindReceipt(id)!, remote, peerId, MessageStatus.Delivered);
        store.Sweep();
        bool keptDelivered = Directory.Exists(store.OutboxFolder(id));
        clock.Advance(TimeSpan.FromHours(47));
        store.Sweep();
        bool kept47 = Directory.Exists(store.OutboxFolder(id)) && fixture.Store.FindReceipt(id)?.Status == MessageStatus.Delivered;
        clock.Advance(TimeSpan.FromHours(2));
        store.Sweep();
        check(keptSubmitted && keptDelivered && kept47 && !Directory.Exists(store.OutboxFolder(id)),
            "ATTACHMENT_SENDER_COPY_KEPT: a paired recipient's message keeps the sender's copy through Submitted and Delivered until 48 h (47 h kept, 49 h gone)");

        // The received side: a message from the paired broker, admitted here, with its verified folder.
        var clock2 = new NearRealClock();
        await using var receiver = new ToolFixture("attachments-received-copy", clock: clock2, attachments: true);
        receiver.Store.AuthorizePeer(new(peerId, "Synthetic paired broker", "https://192.0.2.14/", new string('F', 64), clock2.GetUtcNow()));
        var local = receiver.Register("Synthetic copy recipient");
        Guid received = Guid.NewGuid();
        var at = clock2.GetUtcNow();
        receiver.Store.Accept(new(received, Guid.NewGuid(), null, new(peerId, "codex:synthetic-remote-sender", Guid.NewGuid()), local.Address,
            SyntheticEnvelope("Synthetic received note."), at, at.AddHours(24)), peerId);
        string folder = receiver.Attachments!.InboxFolder(peerId, received);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "0-received.txt"), "Synthetic received content.");
        clock2.Advance(TimeSpan.FromHours(47));
        receiver.Attachments.Sweep();
        bool receivedKept = Directory.Exists(folder);
        clock2.Advance(TimeSpan.FromHours(2));
        receiver.Attachments.Sweep();
        check(receivedKept && !Directory.Exists(folder),
            "ATTACHMENT_RECEIVED_COPY_KEPT: an admitted received folder stays while its record does, up to 48 h (47 h kept, 49 h gone)");
    }

    // F-5 (DECISIONS 39) and T-4: the outbox quota, and the quarantine reservation driven at a few KiB through the limits seam.
    private static async Task QuotaAsync(Action<bool, string> check)
    {
        await using (var fixture = new ToolFixture("attachments-outbox-quota", attachments: true, attachmentLimits: new(OutboxBytes: 4096)))
        {
            var sender = fixture.Register("Synthetic quota sender");
            var recipient = fixture.Register("Synthetic quota recipient");
            string first = Source(fixture.Root, "first.bin", new byte[3000]);
            string second = Source(fixture.Root, "second.bin", new byte[3000]);
            fixture.Tools.SendWithAttachments(sender.Address, recipient.Address, Guid.NewGuid(), "x", [first]);
            Guid refused = Guid.NewGuid();
            check(ToolFixture.Refused(() => fixture.Tools.SendWithAttachments(sender.Address, recipient.Address, refused, "x", [second]), AttachmentCodes.OutboxFull) &&
                !Directory.Exists(fixture.Attachments!.OutboxFolder(refused)) && fixture.Store.FindReceipt(refused) is null,
                "ATTACHMENT_OUTBOX_QUOTA: a send that would pass the outbox quota is refused attachment_outbox_full and stages nothing");
        }

        var clock = new NearRealClock();
        await using var quota = new ToolFixture("attachments-quarantine-quota", clock: clock, attachments: true);
        Guid admittedPeer = Guid.NewGuid(), orphanPeer = Guid.NewGuid();
        quota.Store.AuthorizePeer(new(admittedPeer, "Synthetic admitted broker", "https://192.0.2.15/", new string('A', 64), clock.GetUtcNow()));
        var local = quota.Register("Synthetic quarantine recipient");
        Guid admittedMessage = Guid.NewGuid();
        var at = clock.GetUtcNow();
        quota.Store.Accept(new(admittedMessage, Guid.NewGuid(), null, new(admittedPeer, "codex:synthetic-quota-sender", Guid.NewGuid()), local.Address,
            SyntheticEnvelope("Synthetic admitted note."), at, at.AddHours(1)), admittedPeer);
        void Folder(Guid peer, Guid message, int bytes)
        {
            string folder = quota.Attachments!.InboxFolder(peer, message);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "0-data.bin"), new byte[bytes]);
        }
        Folder(admittedPeer, admittedMessage, 5000);
        Folder(orphanPeer, Guid.NewGuid(), 2000);
        string data = quota.Attachments!.DataDirectory;
        // A store created now is a restarted one: it recounts both folders from disk, the orphan included.
        var limits = new AttachmentLimits(QuarantineBytes: 10_000, PerPeerAdmittedBytes: 6000, PerPeerInFlightBytes: 1_000_000);
        var restarted = new AttachmentStore(data, quota.Store, true, clock, limits);
        check(restarted.Reserve(Guid.NewGuid(), Guid.NewGuid(), 3001) is not ReserveResult.Reserved,
            "ATTACHMENT_QUOTA_RECOUNT: a restarted store counts the folders on disk, an unadmitted orphan included, and defers a pull that does not fit beside them");
        Guid pressurePeer = Guid.NewGuid(), pressureMessage = Guid.NewGuid();
        bool reserved = restarted.Reserve(pressurePeer, pressureMessage, 3000) == ReserveResult.Reserved;
        check(reserved && restarted.Reserve(Guid.NewGuid(), Guid.NewGuid(), 1000) == ReserveResult.Defer,
            "ATTACHMENT_QUOTA_PRESSURE_DEFERS: room taken by unadmitted files and running pulls defers a pull instead of refusing it");
        check(restarted.Reserve(Guid.NewGuid(), Guid.NewGuid(), 5001) == ReserveResult.RefuseFull,
            "ATTACHMENT_QUOTA_FULL_REFUSES: a pull that the admitted files alone leave no room for is refused");
        restarted.Release(pressurePeer, pressureMessage);
        check(restarted.Reserve(admittedPeer, Guid.NewGuid(), 1001) == ReserveResult.RefuseFull &&
            restarted.Reserve(Guid.NewGuid(), Guid.NewGuid(), 1001) == ReserveResult.Reserved,
            "ATTACHMENT_PER_PEER_ADMITTED_CAP: one broker's admitted cap refuses that broker's next pull only; another broker's still fits");
        var inFlight = new AttachmentStore(data, quota.Store, true, clock, new AttachmentLimits(PerPeerInFlightBytes: 4000));
        Guid busyPeer = Guid.NewGuid();
        check(inFlight.Reserve(busyPeer, Guid.NewGuid(), 3000) == ReserveResult.Reserved &&
            inFlight.Reserve(busyPeer, Guid.NewGuid(), 1500) == ReserveResult.Defer &&
            inFlight.Reserve(Guid.NewGuid(), Guid.NewGuid(), 1500) == ReserveResult.Reserved,
            "ATTACHMENT_PER_PEER_IN_FLIGHT_CAP: one broker's in-flight share defers its next pull; another broker's still starts");

        // Round 5 (DECISIONS 96, item 6): the admitted cap counts what will be admitted, not only what is. The pool and the
        // in-flight share are wide here, so only the per-broker admitted cap can defer: admittedPeer holds 5000 of its 6000.
        var pending = new AttachmentStore(data, quota.Store, true, clock,
            new AttachmentLimits(QuarantineBytes: 1_000_000, PerPeerAdmittedBytes: 6000, PerPeerInFlightBytes: 1_000_000));
        Guid firstPull = Guid.NewGuid(), secondPull = Guid.NewGuid();
        bool firstReserved = pending.Reserve(admittedPeer, firstPull, 600) == ReserveResult.Reserved;
        bool concurrentDefers = pending.Reserve(admittedPeer, secondPull, 600) == ReserveResult.Defer;
        bool otherStarts = pending.Reserve(Guid.NewGuid(), Guid.NewGuid(), 600) == ReserveResult.Reserved;
        pending.Release(admittedPeer, firstPull);
        bool afterRelease = pending.Reserve(admittedPeer, secondPull, 600) == ReserveResult.Reserved;
        pending.Release(admittedPeer, secondPull);
        // A verified folder of that broker that is not admitted yet (no reservation, no receipt) counts the same way.
        Folder(admittedPeer, Guid.NewGuid(), 600);
        var withOrphan = new AttachmentStore(data, quota.Store, true, clock,
            new AttachmentLimits(QuarantineBytes: 1_000_000, PerPeerAdmittedBytes: 6000, PerPeerInFlightBytes: 1_000_000));
        bool orphanDefers = withOrphan.Reserve(admittedPeer, Guid.NewGuid(), 600) == ReserveResult.Defer;
        check(firstReserved && concurrentDefers && otherStarts && afterRelease && orphanDefers,
            "ATTACHMENT_PER_PEER_CAP_COUNTS_PENDING: one broker's running pulls and its folders not yet admitted count against its admitted cap, so a concurrent pull defers instead of passing it; another broker's still starts");
    }

    // F-9: a SUBST drive letter over a plain folder is another spelling of the same file; the opened handle's final path
    // differs from the validated one, so it is refused. subst changes this logon session's drive table, so the letter is
    // removed in finally; without subst the check is skipped LOUDLY.
    private static async Task FinalPathAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("attachments-final-path", attachments: true);
        var sender = fixture.Register("Synthetic final-path sender");
        var recipient = fixture.Register("Synthetic final-path recipient");
        string folder = Path.Combine(fixture.Root, "subst-target");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "plain.txt"), "Synthetic content behind a drive letter.");
        var used = DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0])).ToHashSet();
        char? letter = "QRSTUVWXYZ".Cast<char?>().FirstOrDefault(candidate => !used.Contains(candidate!.Value));
        if (letter is null || Subst(letter + ":", folder) != 0)
        {
            Console.WriteLine("SKIP: ATTACHMENT_SOURCE_FINAL_PATH: subst is unavailable or no drive letter is free");
            return;
        }
        try
        {
            string aliased = letter + @":\plain.txt";
            check(File.Exists(aliased) &&
                ToolFixture.Refused(() => fixture.Tools.SendWithAttachments(sender.Address, recipient.Address, Guid.NewGuid(), "x", [aliased]), AttachmentCodes.SourceInvalid),
                "ATTACHMENT_SOURCE_FINAL_PATH: a file reached through a SUBST drive letter is refused, because the opened handle's final path is not the validated path");
        }
        finally { Subst(letter + ":", null); }
    }

    private static int Subst(string drive, string? target)
    {
        try
        {
            // Redirected (and discarded) so a subst message never reaches this harness's own stderr; read asynchronously so the
            // timeout below can actually end a hung child, with the encodings pinned (runtime-hardening invariants).
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "subst.exe")) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            start.ArgumentList.Add(drive);
            start.ArgumentList.Add(target ?? "/d");
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return -1;
            }
            Task.WaitAll(output, errors);
            return process.ExitCode;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { return -1; }
    }

    // F-17: a source that carries a Mark of the Web, or a file this broker received, is staged still marked.
    private static async Task ZoneKeptAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("attachments-zone-kept", attachments: true);
        var sender = fixture.Register("Synthetic zone sender");
        var recipient = fixture.Register("Synthetic zone recipient");
        string marked = Source(fixture.Root, "marked.txt", "Synthetic downloaded content."u8.ToArray());
        File.WriteAllText(marked + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        string receivedFolder = fixture.Attachments!.InboxFolder(Guid.NewGuid(), Guid.NewGuid());
        Directory.CreateDirectory(receivedFolder);
        string received = Path.Combine(receivedFolder, "0-received.txt");
        File.WriteAllText(received, "Synthetic received content.");
        Guid id = Guid.NewGuid();
        fixture.Tools.SendWithAttachments(sender.Address, recipient.Address, id, "x", [marked, received]);
        string folder = fixture.Attachments.OutboxFolder(id);
        string Zone(string file) => File.Exists(file + ":Zone.Identifier") ? File.ReadAllText(file + ":Zone.Identifier") : "";
        check(Zone(Path.Combine(folder, "0-marked.txt")).Contains("ZoneId=3", StringComparison.Ordinal) &&
            Zone(Path.Combine(folder, "1-0-received.txt")).Contains("ZoneId=3", StringComparison.Ordinal) &&
            fixture.Tools.FetchAttachment(recipient.Address, id, 0) is { Zone: "internet", State: "ready" } &&
            fixture.Tools.FetchAttachment(recipient.Address, id, 1) is { Zone: "internet", State: "ready" },
            "ATTACHMENT_SOURCE_ZONE_KEPT: a marked source and a received file are staged still marked, and fetch reports internet");
    }

    // The parent-chain walk (AttachmentSource.RequirePlainParents) on the recipient's own read: a received folder replaced by a
    // junction to a folder holding a file with the right name and hash is never followed.
    private static async Task FetchNoJunctionAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("attachments-fetch-junction", attachments: true);
        Guid peerId = Guid.NewGuid();
        fixture.Store.AuthorizePeer(new(peerId, "Synthetic paired broker", "https://192.0.2.21/", new string('F', 64), fixture.Clock.GetUtcNow()));
        var local = fixture.Register("Synthetic junction recipient");
        byte[] content = "Synthetic content behind a planted junction."u8.ToArray();
        string envelope = AttachmentEnvelope.Compose("Synthetic note.", [new AttachmentEntry(0, "data.txt", content.Length, Hash(content))]);
        Guid id = Guid.NewGuid();
        var at = fixture.Clock.GetUtcNow();
        fixture.Store.Accept(new(id, Guid.NewGuid(), null, new(peerId, "codex:synthetic-junction-sender", Guid.NewGuid()), local.Address, envelope,
            at, at.AddHours(1)), peerId);
        string target = Path.Combine(fixture.Root, "junction-planted");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "0-data.txt"), content);
        Directory.CreateDirectory(Path.Combine(fixture.Attachments!.InboxRoot, peerId.ToString("D")));
        string folder = fixture.Attachments.InboxFolder(peerId, id);
        HandoffClientChecks.CreateJunction(folder, target);
        // The link goes before the fixture's scratch cleanup, which refuses any linked path.
        try
        {
            check(fixture.Tools.FetchAttachment(local.Address, id, 0) is { State: "missing", Path: null },
                "ATTACHMENT_FETCH_NO_JUNCTION: fetch never follows a junction planted for a received folder; the file reports missing");
        }
        finally { Directory.Delete(folder); }
    }

    // F-18: the 16 KiB bound is on the whole envelope, so a note near it no longer fits beside a file.
    private static async Task TextTooLongAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("attachments-text-too-long", attachments: true);
        var sender = fixture.Register("Synthetic long-note sender");
        var recipient = fixture.Register("Synthetic long-note recipient");
        string path = Source(fixture.Root, "small.txt", "x"u8.ToArray());
        Guid id = Guid.NewGuid();
        // Before F-18 Compose's ArgumentException escaped the tool; that shape is this guard's failure, named, not a crash.
        bool refused;
        try
        {
            refused = ToolFixture.Refused(() => fixture.Tools.SendWithAttachments(sender.Address, recipient.Address, id, new string('n', 16_300), [path]),
                AttachmentCodes.TextTooLong);
        }
        catch (ArgumentException) { refused = false; }
        check(refused && !Directory.Exists(fixture.Attachments!.OutboxFolder(id)),
            "ATTACHMENT_TEXT_TOO_LONG: a note that leaves no room for its file list in 16 KiB is refused attachment_text_too_long, with nothing staged");
    }

    // F-14 and T-4: an allow belongs to one pairing; a failed removal still denies; the sidecar has a peer bound.
    private static void PolicyRoundTwo(Action<bool, string> check)
    {
        using var scratchOwner = OwnedScratch.Create("attachments-policy-r2");
        string root = scratchOwner.Root;
        var peer = new PeerTrust(Guid.NewGuid(), "Synthetic", "https://192.0.2.16/", new string('C', 64), DateTimeOffset.UtcNow);
        var policy = new AttachmentPolicy(root);
        policy.Set(peer, true);
        check(policy.Allows(peer) && new AttachmentPolicy(root).Allows(peer) && !policy.Allows(peer with { PairedAt = peer.PairedAt.AddSeconds(1) }),
            "ATTACHMENT_POLICY_PAIRING: an allow belongs to one pairing; the same broker and certificate paired again start denied");
        policy.WriteFault = () => new IOException("Synthetic sidecar write failure.");
        bool threw = false;
        try { policy.Remove(peer.BrokerId); }
        catch (IOException) { threw = true; }
        bool deniedAtOnce = !policy.Allows(peer) && policy.Dirty && new AttachmentPolicy(root).Allows(peer);
        policy.WriteFault = null;
        policy.Remove(peer.BrokerId);
        check(threw && deniedAtOnce && !policy.Dirty && !new AttachmentPolicy(root).Allows(peer),
            "ATTACHMENT_POLICY_REMOVE_FIRST: a removal whose write fails denies the broker at once, marks the sidecar dirty, and the retry brings the file in line");
        var bounded = new AttachmentPolicy(Path.Combine(root, "bounded"));
        Directory.CreateDirectory(Path.Combine(root, "bounded"));
        var peers = Enumerable.Range(0, AttachmentPolicy.MaximumPeers + 1)
            .Select(index => new PeerTrust(Guid.NewGuid(), "Synthetic " + index, "https://192.0.2.17/", new string('D', 64), DateTimeOffset.UtcNow)).ToArray();
        foreach (var each in peers.Take(AttachmentPolicy.MaximumPeers)) bounded.Set(each, true);
        bool limited = false;
        try { bounded.Set(peers[^1], true); }
        catch (BrokerException error) { limited = error.Code == "attachment_policy_limit"; }
        check(limited && !bounded.Allows(peers[^1]) && bounded.Allows(peers[0]),
            "ATTACHMENT_POLICY_LIMIT: the sidecar holds at most 32 brokers; the next allow is refused attachment_policy_limit and the others stay");
    }

    // F-23 (DECISIONS 41): with the master switch off an allow can be removed through the control operation, never added.
    private static async Task ControlWhileOffAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("attachments-control-off", attachments: false);
        Guid peerId = Guid.NewGuid();
        fixture.Store.AuthorizePeer(new(peerId, "Synthetic paired broker", "https://192.0.2.18/", new string('E', 64), fixture.Clock.GetUtcNow()));
        var trust = fixture.Store.Peers().Single(peer => peer.BrokerId == peerId);
        fixture.Attachments!.Policy.Set(trust, true);
        await using var worker = new AttachmentControlWorker(fixture);
        await using var control = new LocalControlServer(worker);
        bool addRefused = false;
        try { await control.HandleAsync(new("peer-attachments", peerId, Value: true), default); }
        catch (BrokerException error) { addRefused = error.Code == AttachmentCodes.Disabled; }
        bool keptWhileOff = control.Snapshot().Attachments?.AllowedBrokers.Contains(peerId) == true;
        // A removal refused while off is exactly the regression this guards; it must fail by name, not escape.
        bool removedOk;
        try { removedOk = (await control.HandleAsync(new("peer-attachments", peerId, Value: false), default)).Success; }
        catch (BrokerException) { removedOk = false; }
        check(addRefused && keptWhileOff && removedOk && !fixture.Attachments.Policy.Allows(trust) &&
            !new AttachmentPolicy(fixture.Attachments.DataDirectory).Allows(trust),
            "ATTACHMENT_ALLOW_REMOVAL_WHILE_OFF: with attachments off an allow persists and can be removed, but a new one is refused attachments_disabled");
    }

    // F-7: the recipient keeps answering attachment_fetch_pending (as one that lost its own clock to a restart would), so
    // only the sender's stored bound, counted from the FIRST deferral, ends the wait.
    private static async Task SenderBoundAsync(Action<bool, string> check)
    {
        var clock = new NearRealClock();
        await using var fixture = new ToolFixture("attachments-sender-bound", clock: clock, attachments: true);
        var store = fixture.Attachments!;
        Guid peerId = Guid.NewGuid();
        fixture.Store.AuthorizePeer(new(peerId, "Synthetic paired broker", "https://192.0.2.19/", new string('B', 64), clock.GetUtcNow()));
        var sender = fixture.Register("Synthetic bound sender");
        var remote = new SessionAddress(peerId, "codex:synthetic-bound-recipient", Guid.NewGuid());
        string path = Source(fixture.Root, "bound.txt", "Synthetic bound content."u8.ToArray());
        Guid id = Guid.NewGuid();
        string envelope = store.Stage(id, "Synthetic note.", [path], out _);
        var now = clock.GetUtcNow();
        fixture.Store.Accept(new(id, Guid.NewGuid(), null, sender.Address, remote, envelope, now, now.AddHours(1)), fixture.Store.BrokerId);
        var federation = new PendingPeer();
        string stamp = Path.Combine(store.OutboxFolder(id), AttachmentStore.FetchPendingName);
        await using (var runtime = new BrokerRuntime(fixture.Store, federation, [], null, clock) { Attachments = store })
        {
            using var stop = new CancellationTokenSource();
            runtime.Start(stop.Token);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && !(File.Exists(stamp) && fixture.Store.FindReceipt(id) is { Status: MessageStatus.Accepted, Reason: AttachmentCodes.FetchPending }))
                await Task.Delay(25);
            bool deferred = File.Exists(stamp) && fixture.Store.FindReceipt(id)?.Reason == AttachmentCodes.FetchPending;
            // A restarted sender reads the same stamp: not expired just short of the bound.
            clock.Advance(PeerFederation.AttachmentDeferralBound - TimeSpan.FromMinutes(1));
            bool notYet = !new AttachmentStore(store.DataDirectory, fixture.Store, true, clock).FetchPendingExpired(id, PeerFederation.AttachmentDeferralBound);
            int forwardsAtNine = Volatile.Read(ref federation.Forwards);
            deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && Volatile.Read(ref federation.Forwards) <= forwardsAtNine) await Task.Delay(25);
            deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && fixture.Store.FindReceipt(id) is { Status: MessageStatus.Submitting }) await Task.Delay(25);
            bool stillPending = Volatile.Read(ref federation.Forwards) > forwardsAtNine &&
                fixture.Store.FindReceipt(id) is { Status: MessageStatus.Accepted, Reason: AttachmentCodes.FetchPending };
            clock.Advance(TimeSpan.FromMinutes(2));
            deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && fixture.Store.FindReceipt(id)?.Status != MessageStatus.Refused) await Task.Delay(25);
            check(deferred && notYet && stillPending && fixture.Store.FindReceipt(id) is { Status: MessageStatus.Refused, Reason: AttachmentCodes.FetchFailed } &&
                federation.Forwards >= 2,
                "ATTACHMENT_SENDER_BOUND: a recipient that keeps deferring is refused attachment_fetch_failed by the sender once the bound passes from the FIRST deferral (not before), stamped beside the staging");
            stop.Cancel();
        }
        // The lead's ruling: a stamp from a clock that moved back is not trusted; it is rewritten to now and the bound still ends.
        Guid future = Guid.NewGuid();
        store.Stage(future, "Synthetic note.", [path], out _);
        File.WriteAllText(Path.Combine(store.OutboxFolder(future), AttachmentStore.FetchPendingName),
            clock.GetUtcNow().AddDays(1).UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        bool early = store.FetchPendingExpired(future, PeerFederation.AttachmentDeferralBound);
        clock.Advance(PeerFederation.AttachmentDeferralBound + TimeSpan.FromMinutes(1));
        check(!early && store.FetchPendingExpired(future, PeerFederation.AttachmentDeferralBound),
            "ATTACHMENT_SENDER_BOUND_FUTURE_STAMP: a stamp from a clock that moved back is replaced by now, so the bound still ends");
    }

    // F-20: forwarding an envelope waits up to the recipient's 5 s inline wait. Four of them wait on slow pulls here (10 s
    // each, so a loop that waits on one cannot deliver anything for 10 s); the bound is 4 s, which a free loop meets on a busy
    // machine and a blocked one cannot. No delivery loop may block on them, and at most two run at once.
    private static async Task ForwardOffLoopAsync(Action<bool, string> check)
    {
        using var scratchOwner = OwnedScratch.Create("attachments-forward-loop");
        using var store = new BrokerStore(Path.Combine(scratchOwner.Root, "store"));
        Guid peerId = Guid.NewGuid();
        store.AuthorizePeer(new(peerId, "Synthetic paired broker", "https://192.0.2.20/", new string('B', 64), DateTimeOffset.UtcNow));
        var adapter = new DeliveringAdapter();
        BrokerSession Row(string name) => new(new(store.BrokerId, "codex:" + name + "-" + Guid.NewGuid().ToString("N")[..8], Guid.NewGuid()),
            "Synthetic " + name, adapter.Id, SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply, SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(2));
        var localSender = Row("local-sender");
        var localRecipient = Row("local-recipient");
        store.Register(localSender);
        store.Register(localRecipient);
        var slow = new SlowPeer();
        string envelope = AttachmentEnvelope.Compose("Synthetic note.", [new AttachmentEntry(0, "data.txt", 1, new string('a', 64))]);
        await using var runtime = new BrokerRuntime(store, slow, [adapter]);
        using var stop = new CancellationTokenSource();
        runtime.Start(stop.Token);
        for (int index = 0; index < 4; index++)
        {
            var sender = Row("forward-sender-" + index);
            store.Register(sender);
            store.Accept(new(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, new(peerId, "codex:forward-recipient-" + index, Guid.NewGuid()),
                envelope, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10)), store.BrokerId);
        }
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && Volatile.Read(ref slow.InFlight) < SlowPeer.Expected) await Task.Delay(10);
        bool waiting = Volatile.Read(ref slow.InFlight) >= SlowPeer.Expected;
        var local = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, localSender.Address, localRecipient.Address, "Synthetic local message.",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10));
        var watch = Stopwatch.StartNew();
        store.Accept(local, store.BrokerId);
        while (watch.Elapsed < TimeSpan.FromSeconds(15) && store.FindReceipt(local.Id)?.Status != MessageStatus.Delivered) await Task.Delay(10);
        var elapsed = watch.Elapsed;
        bool passed = waiting && store.FindReceipt(local.Id)?.Status == MessageStatus.Delivered && elapsed <= SlowPeer.LocalBound &&
            Volatile.Read(ref slow.MaximumInFlight) <= BrokerRuntime.MaximumAttachmentForwards;
        if (!passed)
            Console.WriteLine("OBSERVED: ATTACHMENT_FORWARD_OFF_LOOP forwards waiting " + waiting + "; local message " +
                store.FindReceipt(local.Id)?.Status + "/" + store.FindReceipt(local.Id)?.Reason + " after " + (int)elapsed.TotalMilliseconds +
                " ms; most forwards at once " + Volatile.Read(ref slow.MaximumInFlight));
        check(passed, "ATTACHMENT_FORWARD_OFF_LOOP: while attachment forwards wait on slow pulls (at most two at a time), a local message is delivered within 4 s, long before a 10 s forward ends");
        stop.Cancel();
    }

    private static string SyntheticEnvelope(string note) =>
        AttachmentEnvelope.Compose(note, [new AttachmentEntry(0, "data.bin", 1, new string('a', 64))]);

    private sealed class AttachmentControlWorker(ToolFixture fixture) : ILocalControlWorker, IAsyncDisposable
    {
        public BrokerRuntime Runtime { get; } = fixture.Runtime;
        public WorkerOptions Options { get; } = new(fixture.Root, "Synthetic attachments", 0, 0, false) { EnableBrokerDiscovery = false };
        public WebApplication Application { get; } = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = fixture.Root }).Build();
        public string LocalAccessToken => "synthetic-unused";
        public McpStatusView McpStatus => new(false, null, null, []);
        public LanProfileMonitor LanProfile => throw new InvalidOperationException("LAN observation was not requested.");
        public ValueTask DisposeAsync() => Application.DisposeAsync();
    }

    private sealed class DeliveringAdapter : ILocalAgentAdapter
    {
        public string Id => "synthetic-delivering";
        public Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token) => Task.FromResult<IReadOnlyList<BrokerSession>>([]);
        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token) =>
            Task.FromResult(new AdapterResult(AdapterAdmission.Delivered, "synthetic_delivered"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // A paired broker that answers every forward attachment_fetch_pending, as a recipient whose pull never finishes would.
    private sealed class PendingPeer : NoFederation
    {
        internal int Forwards;
        public override Task<MessageReceipt> SubmitAsync(PeerTrust peer, BrokerMessage message, CancellationToken token)
        {
            Interlocked.Increment(ref Forwards);
            throw new PeerAdmissionRefusedException(peer.BrokerId, AttachmentCodes.FetchPending);
        }
    }

    // A paired broker whose pulls take 10 s before it defers (longer than the inline wait, as a stalled recipient would).
    private sealed class SlowPeer : NoFederation
    {
        internal const int Expected = 2;
        internal static readonly TimeSpan Hold = TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan LocalBound = TimeSpan.FromSeconds(4);
        internal int InFlight, MaximumInFlight;
        public override async Task<MessageReceipt> SubmitAsync(PeerTrust peer, BrokerMessage message, CancellationToken token)
        {
            int now = Interlocked.Increment(ref InFlight);
            int seen;
            while ((seen = Volatile.Read(ref MaximumInFlight)) < now && Interlocked.CompareExchange(ref MaximumInFlight, now, seen) != seen) { }
            try
            {
                await Task.Delay(Hold, token);
                throw new PeerAdmissionRefusedException(peer.BrokerId, AttachmentCodes.FetchPending);
            }
            finally { Interlocked.Decrement(ref InFlight); }
        }
    }

    private abstract class NoFederation : IPeerFederation
    {
        public IReadOnlyList<BrokerAdvertisement> Discovered => [];
        public IReadOnlyList<PairingOffer> PairingOffers => [];
        public BrokerAdvertisement LocalAdvertisement => new(Guid.NewGuid(), "Synthetic", "https://127.0.0.1:15003", new string('E', 64), DateTimeOffset.UtcNow);
        public Guid? AuthenticatePeer(HttpContext context) => null;
        public void MapEndpoints(WebApplication application) { }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task RequestPairAsync(BrokerAdvertisement peer, CancellationToken token) => throw new NotSupportedException();
        public Task ApprovePairAsync(Guid requestId, CancellationToken token) => throw new NotSupportedException();
        public void RejectPair(Guid requestId) { }
        public void RevokePeer(Guid brokerId) { }
        public Task<PeerDirectory> GetDirectoryAsync(PeerTrust peer, CancellationToken token) => Task.FromResult(new PeerDirectory(peer.BrokerId, []));
        public abstract Task<MessageReceipt> SubmitAsync(PeerTrust peer, BrokerMessage message, CancellationToken token);
        public Task<MessageReceipt?> GetReceiptAsync(PeerTrust peer, Guid messageId, CancellationToken token) => Task.FromResult<MessageReceipt?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
