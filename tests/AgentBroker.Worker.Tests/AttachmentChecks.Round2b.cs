using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Attachments;
using AgentBroker.Worker.Federation;

// Lane F round 2b: checks for the defects the round-2 completeness audit confirmed (outbox/003-F-r2-audit-triage.md). Each label
// names the guard a hygiene recipe (attachment-r2b-*) breaks.
internal static partial class AttachmentChecks
{
    private static async Task RoundTwoBAsync(Action<bool, string> check)
    {
        await ForwardFaultAsync(check);
        await UnadmittedShareAsync(check);
        await PlainTextFolderAsync(check);
        await PolicySaveRetriedAsync(check);
        await ZoneFailClosedAsync(check);
        await ReceivedFilesOnlyAsync(check);
        await NulNoteAsync(check);
        // After the check-coverage audit (wf_03b67994-abe): round-2b lines that no recipe killed.
        DefaultTableShares(check);
        await SweepRootJunctionAsync(check);
        await PlainTextRecountAsync(check);
        await UnsavedWhileOffAsync(check);
        await ZoneUnreadableAsync(check);
    }

    // M1: a fault on the background forward path follows the delivery loop's policy: durable state unavailable, the worker
    // stops (FatalError), and the status says why; nothing ends in silence with the claim still Submitting.
    private static async Task ForwardFaultAsync(Action<bool, string> check)
    {
        using var scratchOwner = OwnedScratch.Create("attachments-forward-fault");
        using var store = new BrokerStore(Path.Combine(scratchOwner.Root, "store"));
        Guid peerId = Guid.NewGuid();
        store.AuthorizePeer(new(peerId, "Synthetic paired broker", "https://192.0.2.22/", new string('B', 64), DateTimeOffset.UtcNow));
        var sender = new BrokerSession(new(store.BrokerId, "codex:forward-fault-sender", Guid.NewGuid()), "Synthetic fault sender", "synthetic",
            SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply, SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(2));
        store.Register(sender);
        var federation = new PendingPeer();
        int fatal = 0;
        await using var runtime = new BrokerRuntime(store, federation, []) { AttachmentForwardFault = () => new IOException("Synthetic journal commit failure.") };
        runtime.FatalError += () => Interlocked.Increment(ref fatal);
        using var stop = new CancellationTokenSource();
        runtime.Start(stop.Token);
        store.Accept(new(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, new(peerId, "codex:forward-fault-recipient", Guid.NewGuid()),
            SyntheticEnvelope("Synthetic note."), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10)), store.BrokerId);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && Volatile.Read(ref fatal) == 0) await Task.Delay(25);
        check(Volatile.Read(ref fatal) == 1 && runtime.AdapterErrors.TryGetValue("broker", out string? error) && error == "durable_state_unavailable",
            "ATTACHMENT_FORWARD_FAULT_STOPS_WORKER: a fault on the background forward path is the delivery loop's: durable state unavailable, and the worker stops instead of holding the claim in silence");
        stop.Cancel();
    }

    // M4: a broker's finished but never-admitted folders count toward its own in-flight share.
    private static async Task UnadmittedShareAsync(Action<bool, string> check)
    {
        var clock = new NearRealClock();
        await using var fixture = new ToolFixture("attachments-unadmitted-share", clock: clock, attachments: true);
        Guid busy = Guid.NewGuid();
        string folder = fixture.Attachments!.InboxFolder(busy, Guid.NewGuid());
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "0-data.bin"), new byte[3000]);
        var store = new AttachmentStore(fixture.Attachments.DataDirectory, fixture.Store, true, clock, new AttachmentLimits(PerPeerInFlightBytes: 4000));
        check(store.Reserve(busy, Guid.NewGuid(), 1500) == ReserveResult.Defer && store.Reserve(Guid.NewGuid(), Guid.NewGuid(), 1500) == ReserveResult.Reserved,
            "ATTACHMENT_PER_PEER_UNADMITTED_SHARE: a broker's unadmitted folders count toward its own in-flight share, so its next pull defers while another broker's starts");
    }

    // M2: only a valid envelope admitted from the folder's own broker makes a received folder admitted.
    private static async Task PlainTextFolderAsync(Action<bool, string> check)
    {
        var clock = new NearRealClock();
        await using var fixture = new ToolFixture("attachments-plain-folder", clock: clock, attachments: true);
        Guid peerId = Guid.NewGuid();
        fixture.Store.AuthorizePeer(new(peerId, "Synthetic paired broker", "https://192.0.2.23/", new string('F', 64), clock.GetUtcNow()));
        var local = fixture.Register("Synthetic plain-folder recipient");
        var at = clock.GetUtcNow();
        Guid plain = Guid.NewGuid(), real = Guid.NewGuid();
        var remote = new SessionAddress(peerId, "codex:synthetic-plain-sender", Guid.NewGuid());
        fixture.Store.Accept(new(plain, Guid.NewGuid(), null, remote, local.Address, "Synthetic plain text under an attachment message ID.", at, at.AddHours(24)), peerId);
        fixture.Store.Accept(new(real, Guid.NewGuid(), null, remote, local.Address, SyntheticEnvelope("Synthetic note."), at, at.AddHours(24)), peerId);
        foreach (Guid id in new[] { plain, real })
        {
            string folder = fixture.Attachments!.InboxFolder(peerId, id);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "0-data.bin"), new byte[1]);
        }
        clock.Advance(AttachmentStore.OrphanGrace + TimeSpan.FromMinutes(1));
        fixture.Attachments!.Sweep();
        check(!Directory.Exists(fixture.Attachments.InboxFolder(peerId, plain)) && Directory.Exists(fixture.Attachments.InboxFolder(peerId, real)),
            "ATTACHMENT_FOLDER_NEEDS_ENVELOPE_ADMISSION: a received folder counts as admitted only for a valid envelope from its own broker; under a plain text it is an orphan the sweep removes");
    }

    // A removal whose save failed is in force in memory, shown as not saved, and saved by the next sweep.
    private static async Task PolicySaveRetriedAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("attachments-policy-retry", attachments: true);
        Guid peerId = Guid.NewGuid();
        fixture.Store.AuthorizePeer(new(peerId, "Synthetic paired broker", "https://192.0.2.24/", new string('E', 64), fixture.Clock.GetUtcNow()));
        var trust = fixture.Store.Peers().Single(peer => peer.BrokerId == peerId);
        var store = fixture.Attachments!;
        store.Policy.Set(trust, true);
        store.Policy.WriteFault = () => new IOException("Synthetic sidecar write failure.");
        try { store.Policy.Remove(peerId); } catch (IOException) { }
        bool shownUnsaved = store.View().PolicyUnsaved == true && !store.Policy.Allows(trust);
        store.Policy.WriteFault = null;
        store.Sweep();
        check(shownUnsaved && !store.Policy.Dirty && store.View().PolicyUnsaved is null && !new AttachmentPolicy(store.DataDirectory).Allows(trust),
            "ATTACHMENT_POLICY_SAVE_RETRIED: an allow removal whose save failed is shown as not saved and is saved by the next sweep, so a restart keeps it removed");
    }

    // A Zone.Identifier stream over 4 KiB (a long download URL) still marks the staged copy as internet.
    private static async Task ZoneFailClosedAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("attachments-zone-fail-closed", attachments: true);
        var sender = fixture.Register("Synthetic large-zone sender");
        var recipient = fixture.Register("Synthetic large-zone recipient");
        string marked = Source(fixture.Root, "large-zone.txt", "Synthetic downloaded content with a long URL."u8.ToArray());
        File.WriteAllText(marked + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.invalid/" + new string('q', 5000) + "\r\n");
        Guid id = Guid.NewGuid();
        fixture.Tools.SendWithAttachments(sender.Address, recipient.Address, id, "x", [marked]);
        string staged = Path.Combine(fixture.Attachments!.OutboxFolder(id), "0-large-zone.txt");
        check(File.Exists(staged + ":Zone.Identifier") && File.ReadAllText(staged + ":Zone.Identifier").Contains("ZoneId=3", StringComparison.Ordinal) &&
            fixture.Tools.FetchAttachment(recipient.Address, id, 0) is { Zone: "internet" },
            "ATTACHMENT_SOURCE_ZONE_FAIL_CLOSED: a source whose Mark of the Web is too large to copy is still staged as downloaded from the internet");
    }

    // The received-files exception covers the stored files only, never the manifest beside them.
    private static async Task ReceivedFilesOnlyAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("attachments-received-only", attachments: true);
        var sender = fixture.Register("Synthetic manifest sender");
        var recipient = fixture.Register("Synthetic manifest recipient");
        string folder = fixture.Attachments!.InboxFolder(Guid.NewGuid(), Guid.NewGuid());
        Directory.CreateDirectory(folder);
        string manifest = Path.Combine(folder, AttachmentStore.ManifestName);
        File.WriteAllText(manifest, "{\"version\":1,\"note\":\"another session's text\"}");
        string stored = Path.Combine(folder, "0-data.txt");
        File.WriteAllText(stored, "Synthetic received content.");
        check(ToolFixture.Refused(() => fixture.Tools.SendWithAttachments(sender.Address, recipient.Address, Guid.NewGuid(), "x", [manifest]), AttachmentCodes.SourceInvalid) &&
            fixture.Tools.SendWithAttachments(sender.Address, recipient.Address, Guid.NewGuid(), "x", [stored]).Message.Text.Length > 0,
            "ATTACHMENT_SOURCE_RECEIVED_FILES_ONLY: only a received file itself can be sent on from the broker's folders; the manifest beside it is refused");
    }

    // A note with a NUL is an invalid note, not one that is too long.
    private static async Task NulNoteAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("attachments-nul-note", attachments: true);
        var sender = fixture.Register("Synthetic NUL sender");
        var recipient = fixture.Register("Synthetic NUL recipient");
        string path = Source(fixture.Root, "small.txt", "x"u8.ToArray());
        Guid id = Guid.NewGuid();
        bool refused;
        try { refused = ToolFixture.Refused(() => fixture.Tools.SendWithAttachments(sender.Address, recipient.Address, id, "short\0note", [path]), "invalid_message"); }
        catch (ArgumentException) { refused = false; }
        check(refused && !Directory.Exists(fixture.Attachments!.OutboxFolder(id)),
            "ATTACHMENT_NOTE_NUL_INVALID: a note with a NUL is refused invalid_message, not attachment_text_too_long, with nothing staged");
    }

    // M4: the default global tables hold every allowed broker at its full share, so a share is the only limit one broker meets.
    private static void DefaultTableShares(Action<bool, string> check)
    {
        var defaults = new AttachmentLimits();
        check(defaults.MaximumPullStatesPerPeer > 0 && defaults.MaximumDeferralsPerPeer > 0 &&
            defaults.MaximumPullStates >= AttachmentPolicy.MaximumPeers * defaults.MaximumPullStatesPerPeer &&
            defaults.MaximumDeferrals >= AttachmentPolicy.MaximumPeers * defaults.MaximumDeferralsPerPeer,
            "ATTACHMENT_DEFAULT_TABLES_COVER_SHARES: the default pull-state and deferral tables hold every allowed broker at its full share, so one broker can only ever defer itself");
    }

    // An outbox root that is a junction is never enumerated: the sweep cannot reach or delete anything behind it.
    private static async Task SweepRootJunctionAsync(Action<bool, string> check)
    {
        var clock = new NearRealClock();
        await using var fixture = new ToolFixture("attachments-root-junction", clock: clock, attachments: true);
        var store = fixture.Attachments!;
        string target = Path.Combine(fixture.Root, "outbox-target");
        string behind = Path.Combine(target, Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(behind);
        string kept = Path.Combine(behind, "0-keep.txt");
        File.WriteAllText(kept, "Synthetic file behind a junction.");
        if (Directory.Exists(store.OutboxRoot)) Directory.Delete(store.OutboxRoot);
        HandoffClientChecks.CreateJunction(store.OutboxRoot, target);
        try
        {
            clock.Advance(TimeSpan.FromHours(49));
            store.Sweep();
            check(File.Exists(kept),
                "ATTACHMENT_SWEEP_ROOT_JUNCTION: an outbox root that is a junction is never enumerated, so the sweep deletes nothing behind it");
        }
        finally { Directory.Delete(store.OutboxRoot); }
    }

    // M2/M3 at restart: a received folder under a plain-text message ID is recounted as unadmitted bytes in its broker's share.
    private static async Task PlainTextRecountAsync(Action<bool, string> check)
    {
        var clock = new NearRealClock();
        await using var fixture = new ToolFixture("attachments-plain-recount", clock: clock, attachments: true);
        Guid peerId = Guid.NewGuid();
        fixture.Store.AuthorizePeer(new(peerId, "Synthetic paired broker", "https://192.0.2.25/", new string('C', 64), clock.GetUtcNow()));
        var local = fixture.Register("Synthetic plain-recount recipient");
        var at = clock.GetUtcNow();
        Guid plain = Guid.NewGuid();
        fixture.Store.Accept(new(plain, Guid.NewGuid(), null, new(peerId, "codex:synthetic-recount-sender", Guid.NewGuid()), local.Address,
            "Synthetic plain text under an attachment message ID.", at, at.AddHours(24)), peerId);
        string folder = fixture.Attachments!.InboxFolder(peerId, plain);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "0-data.bin"), new byte[3000]);
        var counted = new AttachmentStore(fixture.Attachments.DataDirectory, fixture.Store, true, clock, new AttachmentLimits(PerPeerInFlightBytes: 4000));
        check(counted.Reserve(peerId, Guid.NewGuid(), 1500) == ReserveResult.Defer && counted.Reserve(Guid.NewGuid(), Guid.NewGuid(), 1500) == ReserveResult.Reserved,
            "ATTACHMENT_PLAIN_TEXT_FOLDER_UNADMITTED_SHARE: a received folder under a plain-text message ID is recounted as unadmitted bytes in its broker's in-flight share, not as admitted bytes");
    }

    // With attachments off and no allow left, a removal the broker could not save is still in the status the pane reads.
    private static async Task UnsavedWhileOffAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("attachments-unsaved-off", attachments: false);
        Guid peerId = Guid.NewGuid();
        fixture.Store.AuthorizePeer(new(peerId, "Synthetic paired broker", "https://192.0.2.26/", new string('D', 64), fixture.Clock.GetUtcNow()));
        var trust = fixture.Store.Peers().Single(peer => peer.BrokerId == peerId);
        var store = fixture.Attachments!;
        store.Policy.Set(trust, true);
        await using var worker = new AttachmentControlWorker(fixture);
        await using var control = new LocalControlServer(worker);
        store.Policy.WriteFault = () => new IOException("Synthetic sidecar write failure.");
        try { store.Policy.Remove(peerId); } catch (IOException) { }
        var view = control.Snapshot().Attachments;
        store.Policy.WriteFault = null;
        check(view is { Enabled: false, PolicyUnsaved: true } && view.AllowedBrokers.Length == 0,
            "ATTACHMENT_UNSAVED_SHOWN_WHILE_OFF: with attachments off, an allow removal the broker could not save is still reported as not saved, though no broker is allowed");
    }

    // A Zone.Identifier stream that cannot be read (held exclusively) fails closed: the staged copy is marked internet.
    private static async Task ZoneUnreadableAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("attachments-zone-unreadable", attachments: true);
        var sender = fixture.Register("Synthetic locked-zone sender");
        var recipient = fixture.Register("Synthetic locked-zone recipient");
        string marked = Source(fixture.Root, "locked-zone.txt", "Synthetic downloaded content with a locked zone."u8.ToArray());
        File.WriteAllText(marked + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.invalid/locked\r\n");
        Guid id = Guid.NewGuid();
        using (new FileStream(marked + ":Zone.Identifier", FileMode.Open, FileAccess.Read, FileShare.None))
            fixture.Tools.SendWithAttachments(sender.Address, recipient.Address, id, "x", [marked]);
        string staged = Path.Combine(fixture.Attachments!.OutboxFolder(id), "0-locked-zone.txt");
        check(File.Exists(staged + ":Zone.Identifier") && File.ReadAllText(staged + ":Zone.Identifier").Contains("HostUrl=about:internet", StringComparison.Ordinal) &&
            fixture.Tools.FetchAttachment(recipient.Address, id, 0) is { Zone: "internet" },
            "ATTACHMENT_SOURCE_ZONE_UNREADABLE_FAILS_CLOSED: a source whose Mark of the Web cannot be read is staged as downloaded from the internet");
    }
}
