using System.Security.Cryptography;
using AgentBroker.Core;
using AgentBroker.Worker.Attachments;

// Lane F round 6 (DECISIONS 116): the store's side of the race fixes the owner chose to fix in 1.10.5 (SHEET S-017, item 11)
// and the defense-in-depth line S-013a. Each label names the guard a hygiene recipe (attachment-r6-*) breaks; those recipes
// revert each guard, since the seams these checks use are new in this round.
internal static partial class AttachmentChecks
{
    private static async Task RoundSixAsync(Action<bool, string> check)
    {
        await DeleteWaitsForPublicationAsync(check);
        await SweepWaitsForPublicationAsync(check);
        await MismatchRecountFaultStaysAsync(check);
        await SweepRootItselfJunctionAsync(check);
        await AbandonRootItselfJunctionAsync(check);
        await PartialRetryCountsOwnBytesOnceAsync(check);
    }

    private static AttachmentEntry ReceivedEntry(int index, string name, byte[] bytes) =>
        new(index, name, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)));

    // S-017, item 11: a deletion of a received folder that lands inside a publication (the temporary written, its rename not yet
    // done) waits on the folder's gate, then deletes the folder after the publication: never between the manifest check and the
    // file's rename, which left the file without its manifest.
    // The deletion holds the admission gate while it waits, so this is also the folder gate's no-deadlock check: the publication
    // and the deletion each run on their own task and must both finish within the bound.
    private static async Task DeleteWaitsForPublicationAsync(Action<bool, string> check)
    {
        var fixture = new ToolFixture("attachments-r6-publish-delete", attachments: true);
        bool settled = false;
        try
        {
            var store = fixture.Attachments!;
            Guid peer = Guid.NewGuid(), id = Guid.NewGuid();
            byte[] first = "Synthetic first received file."u8.ToArray(), second = "Synthetic second received file."u8.ToArray();
            AttachmentEntry[] entries = [ReceivedEntry(0, "one.txt", first), ReceivedEntry(1, "two.txt", second)];
            string envelope = AttachmentEnvelope.Compose("Synthetic note.", entries);
            string folder = store.InboxFolder(peer, id);
            store.PublishVerified(peer, id, entries[0], first, envelope);
            Task<bool>? deletion = null;
            bool deletedInside = true;
            store.BeforePublishFile = () =>
            {
                deletion = Task.Run(() => store.DeleteInbox(peer, id));
                deletedInside = deletion.Wait(TimeSpan.FromSeconds(1));
            };
            var publication = Task.Run(() => store.PublishVerified(peer, id, entries[1], second, envelope));
            var bound = Task.Delay(TimeSpan.FromSeconds(20));
            bool published = await Task.WhenAny(publication, bound) == publication;
            store.BeforePublishFile = null;
            bool deleted = deletion is not null && await Task.WhenAny(deletion, bound) == deletion;
            settled = published && deleted;
            if (published) await publication;
            bool whole = !Directory.Exists(folder) || File.Exists(Path.Combine(folder, AttachmentStore.ManifestName)) &&
                File.Exists(Path.Combine(folder, "0-one.txt")) && File.Exists(Path.Combine(folder, "1-two.txt"));
            if (!(settled && !deletedInside && whole))
                Console.WriteLine("OBSERVED: ATTACHMENT_DELETE_WAITS_FOR_PUBLICATION published " + published + "; deleted " + deleted +
                    "; deleted inside the publication " + deletedInside + "; folder " +
                    (Directory.Exists(folder) ? string.Join(",", Directory.GetFiles(folder).Select(Path.GetFileName)) : "absent"));
            check(settled && !deletedInside && whole,
                "ATTACHMENT_DELETE_WAITS_FOR_PUBLICATION: a received folder's deletion that lands inside a publication waits for it, then deletes the folder, and neither waits forever");
        }
        // A publication and a deletion that never finished still hold the folder's files open, so the scratch cannot be removed;
        // the failure above is the report, and the runner's scratch audit names the folder it kept.
        finally { if (settled) await fixture.DisposeAsync(); }
    }

    // S-017, item 11, the sweep's side: a sweep that reaches an unadmitted folder past the orphan grace while a file is being
    // published into it waits on the folder's gate (it holds the admission gate meanwhile; the publication never takes that
    // one), then deletes the folder after the publication. The sweep runs on its own task: on the publication's own thread
    // the reentrant gate would let it straight in.
    private static async Task SweepWaitsForPublicationAsync(Action<bool, string> check)
    {
        var clock = new NearRealClock();
        var fixture = new ToolFixture("attachments-r6-publish-sweep", clock: clock, attachments: true);
        bool settled = false;
        try
        {
            var store = fixture.Attachments!;
            Guid peer = Guid.NewGuid(), id = Guid.NewGuid();
            byte[] first = "Synthetic first swept file."u8.ToArray(), second = "Synthetic second swept file."u8.ToArray();
            AttachmentEntry[] entries = [ReceivedEntry(0, "one.txt", first), ReceivedEntry(1, "two.txt", second)];
            string envelope = AttachmentEnvelope.Compose("Synthetic note.", entries);
            string folder = store.InboxFolder(peer, id);
            store.PublishVerified(peer, id, entries[0], first, envelope); // no reservation and no receipt: an unadmitted folder
            clock.Advance(AttachmentStore.OrphanGrace + TimeSpan.FromMinutes(1));
            Task? sweep = null;
            bool sweptInside = true;
            store.BeforePublishFile = () =>
            {
                sweep = Task.Run(() => store.Sweep());
                sweptInside = sweep.Wait(TimeSpan.FromSeconds(1));
            };
            var publication = Task.Run(() => store.PublishVerified(peer, id, entries[1], second, envelope));
            var bound = Task.Delay(TimeSpan.FromSeconds(20));
            bool published = await Task.WhenAny(publication, bound) == publication;
            store.BeforePublishFile = null;
            bool swept = sweep is not null && await Task.WhenAny(sweep, bound) == sweep;
            settled = published && swept;
            if (published) await publication;
            bool whole = !Directory.Exists(folder) || File.Exists(Path.Combine(folder, AttachmentStore.ManifestName)) &&
                File.Exists(Path.Combine(folder, "0-one.txt")) && File.Exists(Path.Combine(folder, "1-two.txt"));
            if (!(settled && !sweptInside && whole))
                Console.WriteLine("OBSERVED: ATTACHMENT_SWEEP_WAITS_FOR_PUBLICATION published " + published + "; swept " + swept +
                    "; swept inside the publication " + sweptInside + "; folder " +
                    (Directory.Exists(folder) ? string.Join(",", Directory.GetFiles(folder).Select(Path.GetFileName)) : "absent"));
            check(settled && !sweptInside && whole,
                "ATTACHMENT_SWEEP_WAITS_FOR_PUBLICATION: a sweep that reaches a received folder while a file is being published into it waits for the publication, then deletes the folder, and neither waits forever");
        }
        finally { if (settled) await fixture.DisposeAsync(); }
    }

    // S-013a: the recount after deleting a received file that no longer hashes keeps an IO fault to itself: HasVerified still
    // answers false and the file is gone, and the fault seam's count shows that the recount ran and failed.
    private static async Task MismatchRecountFaultStaysAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("attachments-r6-recount-fault", attachments: true);
        var store = fixture.Attachments!;
        Guid peer = Guid.NewGuid(), id = Guid.NewGuid();
        byte[] bytes = "Synthetic verified content."u8.ToArray();
        var entry = ReceivedEntry(0, "data.txt", bytes);
        store.PublishVerified(peer, id, entry, bytes, AttachmentEnvelope.Compose("Synthetic note.", [entry]));
        string file = Path.Combine(store.InboxFolder(peer, id), "0-data.txt");
        File.WriteAllBytes(file, "Synthetic altered content."u8.ToArray());
        int faults = 0;
        store.RecountFault = () => { Interlocked.Increment(ref faults); return new IOException("Synthetic recount fault."); };
        bool answered = true;
        Exception? escaped = null;
        try { answered = store.HasVerified(peer, id, entry); }
        catch (Exception error) { escaped = error; }
        finally { store.RecountFault = null; }
        if (!(escaped is null && !answered && !File.Exists(file) && faults == 1))
            Console.WriteLine("OBSERVED: ATTACHMENT_MISMATCH_RECOUNT_FAULT_STAYS escaped " + (escaped?.GetType().Name ?? "none") + "; answered " + answered +
                "; file " + File.Exists(file) + "; faults " + faults);
        check(escaped is null && !answered && !File.Exists(file) && faults == 1,
            "ATTACHMENT_MISMATCH_RECOUNT_FAULT_STAYS: an IO fault in the recount after a received file that no longer hashes stays inside HasVerified: the file is deleted and the answer is not verified");
    }

    // Item 18 (DECISIONS 120, C2b's probe): an attachments root that is itself a junction (data\attachments pointing elsewhere)
    // is never walked: the sweep deletes nothing in the tree it points to, aged outbox and inbox folders alike.
    // ATTACHMENT_SWEEP_ROOT_JUNCTION covers a junction one level down, at outbox.
    private static async Task SweepRootItselfJunctionAsync(Action<bool, string> check)
    {
        var clock = new NearRealClock();
        await using var fixture = new ToolFixture("attachments-r6-root-itself", clock: clock, attachments: true);
        string data = Path.Combine(fixture.Root, "relocated-data");
        string target = Path.Combine(fixture.Root, "root-target");
        string outboxKept = Path.Combine(target, "outbox", Guid.NewGuid().ToString("D"), "0-kept.txt");
        string inboxKept = Path.Combine(target, "inbox", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), "0-kept.txt");
        foreach (string file in new[] { outboxKept, inboxKept })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "Synthetic file behind a junction at the attachments root.");
        }
        Directory.CreateDirectory(data);
        string root = Path.Combine(data, "attachments");
        HandoffClientChecks.CreateJunction(root, target);
        try
        {
            // Built after the junction, so nothing writes into the target first; attachments off, so no README lands there.
            var store = new AttachmentStore(data, fixture.Store, false, clock);
            clock.Advance(TimeSpan.FromHours(49)); // past the 48-hour retention and the orphan grace
            store.Sweep();
            bool kept = File.Exists(outboxKept) && File.Exists(inboxKept);
            if (!kept)
                Console.WriteLine("OBSERVED: ATTACHMENT_SWEEP_ROOT_ITSELF_JUNCTION outbox canary " + File.Exists(outboxKept) + "; inbox canary " + File.Exists(inboxKept));
            check(kept,
                "ATTACHMENT_SWEEP_ROOT_ITSELF_JUNCTION: an attachments root that is itself a junction is never walked, so the sweep deletes nothing in the tree it points to");
        }
        finally { Directory.Delete(root); }
    }

    // Item 18 (DECISIONS 122): a refusal's deletion of a received folder (DeleteInbox, behind every Abandon) never goes through
    // an attachments root that is itself a junction either. The folder in the target has no admitted message, so only the
    // root guard can keep it; the sweep's own guard is not on this path.
    private static async Task AbandonRootItselfJunctionAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("attachments-r6-abandon-root-itself", attachments: true);
        string data = Path.Combine(fixture.Root, "relocated-data");
        string target = Path.Combine(fixture.Root, "root-target");
        Guid peer = Guid.NewGuid(), id = Guid.NewGuid();
        string kept = Path.Combine(target, "inbox", peer.ToString("D"), id.ToString("D"), "0-kept.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        File.WriteAllText(kept, "Synthetic received file behind a junction at the attachments root.");
        Directory.CreateDirectory(data);
        string root = Path.Combine(data, "attachments");
        HandoffClientChecks.CreateJunction(root, target);
        try
        {
            var store = new AttachmentStore(data, fixture.Store, false);
            bool admittedKeep = store.DeleteInbox(peer, id);
            bool stays = File.Exists(kept);
            if (!stays || admittedKeep)
                Console.WriteLine("OBSERVED: ATTACHMENT_ABANDON_ROOT_ITSELF_JUNCTION file " + stays + "; answered kept-as-admitted " + admittedKeep);
            check(stays && !admittedKeep,
                "ATTACHMENT_ABANDON_ROOT_ITSELF_JUNCTION: a refusal's deletion of a received folder never goes through an attachments root that is itself a junction");
        }
        finally { Directory.Delete(root); }
    }

    // S-020 (F-X1C-Q1, DECISIONS 123; X1c's table is the fixture): a retry of the SAME message after a partial pull counts its
    // kept verified files once, inside the envelope's bytes, on each of the three bounds; another message still pays for that
    // partial. One store per bound, the other two wide: 5000 admitted bytes of the broker's share, an 800-byte envelope of two
    // 400-byte files, its first file published and the reservation released as a transient failure leaves it.
    private static async Task PartialRetryCountsOwnBytesOnceAsync(Action<bool, string> check)
    {
        const long wide = 1_000_000;
        var axes = new (string Name, AttachmentLimits Limits)[]
        {
            ("per-peer admitted", new AttachmentLimits(QuarantineBytes: wide, PerPeerAdmittedBytes: 6000, PerPeerInFlightBytes: wide)),
            ("pool", new AttachmentLimits(QuarantineBytes: 6000, PerPeerAdmittedBytes: wide, PerPeerInFlightBytes: wide)),
            ("per-peer in-flight", new AttachmentLimits(QuarantineBytes: wide, PerPeerAdmittedBytes: wide, PerPeerInFlightBytes: 1000)),
        };
        var observed = new List<string>();
        bool held = true;
        for (int axis = 0; axis < axes.Length; axis++)
        {
            var (name, limits) = axes[axis];
            await using var fixture = new ToolFixture("attachments-r6-partial-retry-" + axis, attachments: true, attachmentLimits: limits);
            var store = fixture.Attachments!;
            Guid peer = Guid.NewGuid(), admittedId = Guid.NewGuid(), retried = Guid.NewGuid(), other = Guid.NewGuid();
            byte[] admittedBytes = Enumerable.Repeat((byte)'a', 5000).ToArray();
            byte[] first = Enumerable.Repeat((byte)'b', 400).ToArray(), second = Enumerable.Repeat((byte)'c', 400).ToArray();
            var admittedEntry = ReceivedEntry(0, "admitted.bin", admittedBytes);
            store.PublishVerified(peer, admittedId, admittedEntry, admittedBytes, AttachmentEnvelope.Compose("Synthetic admitted note.", [admittedEntry]));
            store.Admitted(peer, admittedId); // what Finish records after an admission
            AttachmentEntry[] entries = [ReceivedEntry(0, "one.bin", first), ReceivedEntry(1, "two.bin", second)];
            long total = entries.Sum(entry => entry.Bytes);
            var noPartial = store.Reserve(peer, retried, total);
            store.PublishVerified(peer, retried, entries[0], first, AttachmentEnvelope.Compose("Synthetic partial note.", entries));
            store.Release(peer, retried); // the pull ended (a transient failure on the second file); the first file stays
            bool partialKept = File.Exists(Path.Combine(store.InboxFolder(peer, retried), "0-one.bin"));
            var otherMessage = store.Reserve(peer, other, total);
            if (otherMessage == ReserveResult.Reserved) store.Release(peer, other);
            var retry = store.Reserve(peer, retried, total);
            if (retry == ReserveResult.Reserved) store.Release(peer, retried);
            bool axisHeld = total == 800 && partialKept && noPartial == ReserveResult.Reserved && otherMessage == ReserveResult.Defer &&
                retry == ReserveResult.Reserved;
            observed.Add(name + ": no partial " + noPartial + ", other message " + otherMessage + ", same-message retry " + retry + ", partial kept " + partialKept);
            held &= axisHeld;
        }
        if (!held) Console.WriteLine("OBSERVED: ATTACHMENT_PARTIAL_RETRY_COUNTS_OWN_BYTES_ONCE " + string.Join("; ", observed));
        check(held,
            "ATTACHMENT_PARTIAL_RETRY_COUNTS_OWN_BYTES_ONCE: a retry of the same message after a partial pull counts its kept files once on the per-peer admitted, pool and in-flight bounds, while another message still counts them");
    }
}
