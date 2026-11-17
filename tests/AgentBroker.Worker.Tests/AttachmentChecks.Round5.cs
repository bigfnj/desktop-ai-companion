using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Attachments;

// Lane F round 5 (DECISIONS 96): checks for the code defects the round-4 doc-claims audit confirmed
// (lanes/F/round4/CLASSIFICATION.md). Each label names the guard a hygiene recipe (attachment-r5-*) breaks; each check fails on
// the round-3 head 90fce1a5. Item 6 (the per-broker admitted cap) is in QuotaAsync beside the cap it completes.
internal static partial class AttachmentChecks
{
    private static async Task RoundFiveAsync(Action<bool, string> check)
    {
        await SweepLockedFileAloneAsync(check);
        await SenderBoundWithoutStampAsync(check);
        await BusyManifestDefersAsync(check);
        FailedAllowNotUnsaved(check);
        await RecountStoredFilesOnlyAsync(check);
    }

    // Item 1: a received file another program holds open stays alone; its siblings and the manifest (the message text) go
    // with the folder's retention, and the file itself once it is released.
    private static async Task SweepLockedFileAloneAsync(Action<bool, string> check)
    {
        var clock = new NearRealClock();
        await using var fixture = new ToolFixture("attachments-sweep-locked", clock: clock, attachments: true);
        var store = fixture.Attachments!;
        string folder = store.InboxFolder(Guid.NewGuid(), Guid.NewGuid());
        Directory.CreateDirectory(folder);
        string locked = Path.Combine(folder, "0-locked.bin"), sibling = Path.Combine(folder, "1-sibling.bin");
        string manifest = Path.Combine(folder, AttachmentStore.ManifestName);
        File.WriteAllBytes(locked, new byte[16]);
        File.WriteAllBytes(sibling, new byte[16]);
        File.WriteAllText(manifest, "{\"version\":1}");
        clock.Advance(TimeSpan.FromMinutes(16)); // no receipt: an unadmitted folder past the orphan grace
        bool lockedStays, othersGone;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            store.Sweep();
            lockedStays = File.Exists(locked);
            othersGone = !File.Exists(sibling) && !File.Exists(manifest);
        }
        store.Sweep();
        check(lockedStays && othersGone && !Directory.Exists(folder),
            "ATTACHMENT_SWEEP_LOCKED_FILE_ALONE: a received file another program holds open stays alone: the sweep deletes its siblings and the manifest now, and the file once it is released");
    }

    // Item 8: when the sender cannot store its first-deferral stamp, its own bound still runs (in memory) and refuses at 10 min.
    private static async Task SenderBoundWithoutStampAsync(Action<bool, string> check)
    {
        var clock = new NearRealClock();
        await using var fixture = new ToolFixture("attachments-bound-unstored", clock: clock, attachments: true);
        var store = fixture.Attachments!;
        string source = Source(fixture.Root, "bound.txt", "Synthetic bound content."u8.ToArray());
        Guid id = Guid.NewGuid();
        store.Stage(id, "Synthetic note.", [source], out _);
        // A directory where the stamp file belongs: every save of the stamp fails.
        Directory.CreateDirectory(Path.Combine(store.OutboxFolder(id), AttachmentStore.FetchPendingName));
        var bound = TimeSpan.FromMinutes(10);
        bool atStart = store.FetchPendingExpired(id, bound);
        clock.Advance(TimeSpan.FromMinutes(9));
        bool atNine = store.FetchPendingExpired(id, bound);
        clock.Advance(TimeSpan.FromMinutes(2));
        bool atEleven = store.FetchPendingExpired(id, bound);
        check(!atStart && !atNine && atEleven,
            "ATTACHMENT_SENDER_BOUND_WITHOUT_STAMP: a sender that cannot store its first-deferral stamp still refuses at its 10-minute bound (not at 9 min, at 11 min)");
    }

    // Item 9: a staged manifest another program holds open answers Busy (503, defer); a missing one stays Missing (404).
    private static async Task BusyManifestDefersAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("attachments-busy-manifest", attachments: true);
        var store = fixture.Attachments!;
        string source = Source(fixture.Root, "served.txt", "Synthetic served content."u8.ToArray());
        Guid id = Guid.NewGuid();
        string envelope = store.Stage(id, "Synthetic note.", [source], out _);
        bool parsed = AttachmentEnvelope.TryParse(envelope, out var value) == AttachmentParse.Valid;
        var entry = value!.Attachments[0];
        var ready = store.OpenOutgoing(id, envelope, entry, out var first);
        first?.Dispose();
        string manifest = Path.Combine(store.OutboxFolder(id), AttachmentStore.ManifestName);
        OutgoingState whileHeld;
        using (new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            whileHeld = store.OpenOutgoing(id, envelope, entry, out var held);
            held?.Dispose();
        }
        File.Delete(manifest);
        var gone = store.OpenOutgoing(id, envelope, entry, out var last);
        last?.Dispose();
        check(parsed && ready == OutgoingState.Ready && whileHeld == OutgoingState.Busy && gone == OutgoingState.Missing,
            "ATTACHMENT_BUSY_MANIFEST_DEFERS: a staged manifest another program holds open answers busy (a deferral), not the 404 that ends the message; a missing manifest is still a 404");
    }

    // Item 14: an allow whose save failed changed nothing: it is not reported as an unsaved change in force.
    private static void FailedAllowNotUnsaved(Action<bool, string> check)
    {
        string data = Path.Combine(Path.GetTempPath(), "agentbroker-r5-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        try
        {
            var policy = new AttachmentPolicy(data);
            var peer = new PeerTrust(Guid.NewGuid(), "Synthetic allow broker", "https://192.0.2.31/", new string('B', 64), DateTimeOffset.UtcNow);
            policy.WriteFault = () => new IOException("Synthetic policy write fault.");
            bool threw = false;
            try { policy.Set(peer, true); }
            catch (IOException) { threw = true; }
            bool allowFailedClean = threw && !policy.Dirty && !policy.Allows(peer);
            // The removal path keeps its round-2b behaviour: memory denies at once and the change is reported unsaved.
            policy.WriteFault = null;
            policy.Set(peer, true);
            policy.WriteFault = () => new IOException("Synthetic policy write fault.");
            try { policy.Remove(peer.BrokerId); } catch (IOException) { }
            bool removalUnsaved = policy.Dirty && !policy.Allows(peer);
            check(allowFailedClean && removalUnsaved,
                "ATTACHMENT_FAILED_ALLOW_NOT_UNSAVED: an allow whose save failed is a failure, not an unsaved change in force; a removal whose save failed is still denied at once and reported unsaved");
        }
        finally { try { Directory.Delete(data, true); } catch (IOException) { } }
    }

    // Item 15: the quarantine counts only stored attachments, never the manifest or a LocalOutputFile temporary beside them.
    private static async Task RecountStoredFilesOnlyAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("attachments-recount-stored", attachments: true);
        string folder = fixture.Attachments!.InboxFolder(Guid.NewGuid(), Guid.NewGuid());
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "0-data.bin"), new byte[100]);
        File.WriteAllText(Path.Combine(folder, AttachmentStore.ManifestName), "{\"version\":1}");
        File.WriteAllBytes(Path.Combine(folder, ".agentbroker-output-" + Guid.NewGuid().ToString("N") + ".tmp"), new byte[500]);
        // The outbox quota the same way: a staged copy counts, its manifest, stamp and a temporary do not.
        string outbox = fixture.Attachments.OutboxFolder(Guid.NewGuid());
        Directory.CreateDirectory(outbox);
        File.WriteAllBytes(Path.Combine(outbox, "0-staged.bin"), new byte[200]);
        File.WriteAllText(Path.Combine(outbox, AttachmentStore.ManifestName), "{\"version\":1}");
        File.WriteAllText(Path.Combine(outbox, AttachmentStore.FetchPendingName), "1");
        File.WriteAllBytes(Path.Combine(outbox, ".agentbroker-output-" + Guid.NewGuid().ToString("N") + ".tmp"), new byte[700]);
        var restarted = new AttachmentStore(fixture.Attachments.DataDirectory, fixture.Store, true);
        check(restarted.InboxFiles == 1 && restarted.InboxBytes == 100 && restarted.OutboxBytes == 200,
            "ATTACHMENT_RECOUNT_STORED_FILES_ONLY: the quarantine and the outbox count stored attachments only: a LocalOutputFile temporary beside them is neither a received file nor quota bytes");
    }
}
