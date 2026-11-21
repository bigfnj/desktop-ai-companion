using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBroker.Core;
using DesktopAICompanion.ModuleKit;
using Microsoft.Win32.SafeHandles;

namespace AgentBroker.Worker.Attachments;

// What fetch_attachment returns. Path is null unless State is "ready".
public sealed record AttachmentView(string Name, long Bytes, string Sha256, string? Path, string State, string Zone);

// The attachment bounds (round 2, F-5: a BrokerLimits-style parameter, so checks can drive each bound at a few KiB).
// QuarantineBytes is the pool for received files; PerPeerAdmittedBytes caps what one broker's ADMITTED files may hold inside
// it; PerPeerInFlightBytes caps one broker's bytes that are not admitted: reserved by its running pulls plus its finished but
// never-admitted folders (round 2b, M4). The pull-state and deferral tables have a global bound and a per-broker share; the
// globals are AttachmentPolicy.MaximumPeers times the share, so one broker can only ever defer itself.
public sealed record AttachmentLimits(
    long QuarantineBytes = 512L * 1024 * 1024,
    long OutboxBytes = 512L * 1024 * 1024,
    long PerPeerAdmittedBytes = 128L * 1024 * 1024,
    long PerPeerInFlightBytes = 2 * AttachmentEnvelope.MaximumTotalBytes,
    int MaximumPulls = 4,
    int MaximumPullsPerPeer = 2,
    int MaximumServesPerPeer = 2,
    int MaximumPullStates = 128,
    int MaximumDeferrals = 512,
    int MaximumPullStatesPerPeer = 4,
    int MaximumDeferralsPerPeer = 16);

// The outcome of reserving quarantine room for a pull (F-5). Defer: room is taken by pulls not yet admitted, or this broker
// already has its in-flight share; it frees soon. RefuseFull: admitted files alone leave no room (for everyone, or for this
// broker), which frees only on a retention scale.
internal enum ReserveResult { Reserved, Defer, RefuseFull }

// Opening a staged file for the GET endpoint (F-16): Missing is a definite 404, Busy (the file exists but cannot be opened
// now: a sharing violation, an ACL hold) answers 503 so the recipient defers instead of refusing.
internal enum OutgoingState { Ready, Missing, Busy }

// The worker's attachment folders (lane F, 1.10.5), all under <data>\attachments:
//   outbox\<messageId>\<index>-<name> + manifest.json     copies this broker staged for send_with_attachments
//                       + fetch-pending-since.txt         the first time a recipient answered attachment_fetch_pending (F-7)
//   inbox\<senderBroker>\<messageId>\<index>-<name>        files a paired broker sent, published only after their size and
//                       + manifest.json                    SHA-256 matched, each with a ZoneId=3 Mark of the Web; the manifest
//                                                          is the quarantine's record of what arrived and from whom (DECISIONS 49)
//   README.txt                                             what the folders are; the pane's reveal action selects it
// No name here comes from a peer except a validated AttachmentEnvelope name behind a numeric index, and every delete names
// a GUID folder this class owns. The broker never runs or scans a stored file; it reads one only to hash it or serve it.
public sealed class AttachmentStore
{
    internal static readonly TimeSpan Retention = TimeSpan.FromHours(48);
    // A folder whose message was never admitted (inbox) or never reached the store (outbox) stays this long, longer than the
    // 10-minute deferral bound, so a pull or a staging still in progress is never swept from under itself.
    internal static readonly TimeSpan OrphanGrace = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);
    internal static readonly byte[] InternetZone = "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=about:internet\r\n"u8.ToArray();
    internal const string ManifestName = "manifest.json";
    internal const string FetchPendingName = "fetch-pending-since.txt";
    private sealed record OutboxManifest(int Version, Guid MessageId, string Envelope);
    private sealed record InboxManifest(int Version, Guid MessageId, Guid SenderBroker, string Envelope);

    private readonly object gate = new();
    private readonly object stagingGate = new();
    private readonly BrokerStore store;
    private readonly TimeProvider clock;
    private Dictionary<(Guid Peer, Guid Message), long> inboxFolderBytes = new();
    private HashSet<(Guid Peer, Guid Message)> admittedFolders = new();
    private readonly Dictionary<(Guid Peer, Guid Message), long> reservations = new();
    private long outboxBytes;
    private int inboxFiles;
    private long? lastSweep;
    private long publishedFiles, publishedBytes;

    public bool Enabled { get; }
    public AttachmentLimits Limits { get; }
    public AttachmentPolicy Policy { get; }
    public string DataDirectory { get; }
    public string Root { get; }
    public string ReadmePath => Path.Combine(Root, "README.txt");
    internal string OutboxRoot => Path.Combine(Root, "outbox");
    internal string InboxRoot => Path.Combine(Root, "inbox");

    // F-6: set when a received file could not be published because the quarantine is not a plain local NTFS folder; the
    // pane and the status view show it until a later publication succeeds.
    public bool QuarantineUnavailable { get; private set; }
    // Round 2b: the federation's Verified branch (recheck, then admission) and the sweep's decision for one received folder
    // both run under this gate, so a sweep can never delete a folder between its recheck and its admission.
    internal readonly object AdmissionGate = new();
    // Test seams, null in production: a fault thrown at publication (F-6 checks) and a hook between the source read and the
    // staged write (T-7: a source rewritten there must not change the staged copy).
    internal Func<Exception?>? PublishFault { get; set; }
    internal Action? BeforeStageWrite { get; set; }
    // Round 6 test seams, null in production: a hook inside a received file's publication, after its temporary is written and
    // before the rename (S-017, item 11: a check runs a deletion there), and a fault thrown at the start of a recount (S-013a:
    // the recount after a received file that no longer hashes keeps its fault inside HasVerified).
    internal Action? BeforePublishFile { get; set; }
    internal Func<Exception?>? RecountFault { get; set; }
    // Round 6 (S-017, item 11): a publication into a received folder and every deletion of that folder hold the folder's gate,
    // so a deletion never lands between the manifest check and the file's rename (which left a file without its manifest).
    // Striped: two folders may share a gate, which only serializes them, and nothing holds two of these at once. Lock order:
    // AdmissionGate, then a folder gate, then gate; a folder gate's holder never waits for AdmissionGate.
    private readonly object[] folderGates = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
    private object FolderGate(Guid senderBroker, Guid messageId) =>
        folderGates[(int)((uint)HashCode.Combine(senderBroker, messageId) % (uint)folderGates.Length)];
    // T-8: monotonic counts of files and bytes this store published into quarantine.
    internal long PublishedFiles => Interlocked.Read(ref publishedFiles);
    internal long PublishedBytes => Interlocked.Read(ref publishedBytes);

    public AttachmentStore(string dataDirectory, BrokerStore store, bool enabled, TimeProvider? clock = null, AttachmentLimits? limits = null)
    {
        DataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        Root = Path.Combine(DataDirectory, "attachments");
        this.store = store;
        this.clock = clock ?? TimeProvider.System;
        Enabled = enabled;
        Limits = limits ?? new AttachmentLimits();
        Policy = new AttachmentPolicy(DataDirectory);
        if (enabled) WriteReadme();
        Recount();
    }

    public long InboxBytes { get { lock (gate) return inboxFolderBytes.Values.Sum(); } }
    public int InboxFiles { get { lock (gate) return inboxFiles; } }
    public long OutboxBytes { get { lock (gate) return outboxBytes; } }

    internal string OutboxFolder(Guid messageId) => Path.Combine(OutboxRoot, messageId.ToString("D"));
    internal string InboxFolder(Guid senderBroker, Guid messageId) =>
        Path.Combine(InboxRoot, senderBroker.ToString("D"), messageId.ToString("D"));

    internal const string ReadmeText =
        "AgentBroker attachments\r\n\r\n" +
        "This folder holds copies of files that this broker's sessions attached with send_with_attachments, and files\r\n" +
        "received from other brokers over a pairing.\r\n\r\n" +
        "inbox\\<broker>\\<message>\\  files received from that broker while it was paired with this one. Each received\r\n" +
        "  file (<index>-<name>) was checked against the size and SHA-256 its sender declared before it was saved here\r\n" +
        "  with a Mark of the Web (downloaded from the internet). Treat the content as untrusted peer data. Before the\r\n" +
        "  first file, this broker writes a manifest.json that names the message and its sender broker and holds the\r\n" +
        "  message text, with no Mark of the Web; treat that text as untrusted peer data too.\r\n" +
        "outbox\\<message>\\  copies this workstation staged when one of its sessions sent files.\r\n\r\n" +
        "AgentBroker never runs or scans these files. The running broker reads an attached file stored here only to hash\r\n" +
        "it, to serve it to its recipient's broker, or to copy it when a session sends it on with send_with_attachments.\r\n" +
        "While the broker runs, a sweep about once a minute deletes a message's folder once the message's record ends,\r\n" +
        "once a staged copy's message is Refused or Expired, or once the folder is over 48 hours old (over 15 minutes for\r\n" +
        "a staged copy whose message has not reached the store, or a received folder whose message has not been\r\n" +
        "admitted), but skips a received folder while a pull for it is running. The sweep deletes each file on its own: one\r\n" +
        "that another program holds open stays until it can be deleted, and the other files in its folder still go.\r\n" +
        "Anything else the sweep cannot read or delete can stay longer, and nothing is deleted while the broker is stopped.\r\n" +
        "If this attachments folder is itself a junction or other reparse point, nothing in it is swept until it is a plain\r\n" +
        "folder again.\r\n" +
        "Files can also go sooner, for example when a pull ends in a refusal. Copy anything you want to keep.\r\n";

    private void WriteReadme()
    {
        try
        {
            Directory.CreateDirectory(Root);
            if (File.Exists(ReadmePath) && File.ReadAllText(ReadmePath) == ReadmeText) return;
            AtomicFile.TryWriteAllText(ReadmePath, ReadmeText, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // ---- Sender ----

    // Stages the caller's files and returns the envelope that becomes the message text. A retry that names the same content
    // reuses the existing staging; different content under a staged ID is message_id_collision, and the staging stays.
    // discard removes only what this call created, by exact path, for a send the store then refuses.
    internal string Stage(Guid messageId, string text, IReadOnlyList<string> paths, out Action discard)
    {
        discard = static () => { };
        if (paths is null || paths.Count == 0) throw new BrokerException(AttachmentCodes.SourceInvalid, "Name one to four files.");
        if (paths.Count > AttachmentEnvelope.MaximumFiles) throw new BrokerException(AttachmentCodes.TooLarge, "At most four files.");
        var loaded = new List<AttachmentSource.Loaded>(paths.Count);
        long total = 0;
        foreach (string path in paths)
        {
            var file = AttachmentSource.Load(path, AttachmentEnvelope.MaximumTotalBytes - total, DataDirectory);
            loaded.Add(file);
            total += file.Bytes.LongLength;
        }
        var entries = loaded.Select((file, index) => new AttachmentEntry(index, file.Name, file.Bytes.LongLength, file.Sha256)).ToArray();
        if ((text ?? "").Contains('\0')) throw new BrokerException("invalid_message", "The note contains a NUL character.");
        string envelope;
        try { envelope = AttachmentEnvelope.Compose(text ?? "", entries); }
        // F-18: the 16 KiB bound is on the whole envelope, so the note's budget shrinks with each file it carries.
        // Text that is not valid UTF-16 (a lone surrogate) keeps failing as before; only a well-formed note that is too long
        // gets the new code.
        catch (ArgumentException) when (WellFormed(text ?? ""))
        { throw new BrokerException(AttachmentCodes.TextTooLong, "The note does not fit beside its attachments in 16 KiB."); }
        BeforeStageWrite?.Invoke();

        lock (stagingGate)
        {
            string folder = OutboxFolder(messageId);
            if (Directory.Exists(folder))
            {
                string? staged = ReadManifest(messageId);
                if (staged == envelope) return envelope;
                // A folder without a manifest and without a receipt is an interrupted staging: it never reached the store.
                if (staged is not null || store.FindReceipt(messageId) is not null)
                    throw new BrokerException("message_id_collision", "That message ID already carries other attachments.");
                DeleteFolder(folder);
            }
            lock (gate)
                if (outboxBytes + total > Limits.OutboxBytes)
                    throw new BrokerException(AttachmentCodes.OutboxFull, "The attachment outbox is full.");
            var created = new List<string>();
            try
            {
                Directory.CreateDirectory(folder);
                for (int index = 0; index < entries.Length; index++)
                {
                    string destination = Path.Combine(folder, AttachmentEnvelope.StoredFileName(entries[index]));
                    // F-17: a source that carried a Mark of the Web (or a received file being forwarded) keeps it.
                    LocalOutputFile.Save(destination, loaded[index].Bytes, zoneIdentifier: loaded[index].Zone);
                    created.Add(destination);
                }
                string manifest = Path.Combine(folder, ManifestName);
                LocalOutputFile.Save(manifest, JsonSerializer.SerializeToUtf8Bytes(new OutboxManifest(1, messageId, envelope),
                    Protocol.ControlProtocol.Json));
                created.Add(manifest);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
            {
                DeleteExact(created, folder);
                throw new BrokerException(AttachmentCodes.StorageUnavailable, "The attachment could not be staged.");
            }
            lock (gate) outboxBytes += total;
            discard = () => { DeleteExact(created, folder); lock (gate) outboxBytes = Math.Max(0, outboxBytes - total); };
            return envelope;
        }
    }

    internal string? ReadManifest(Guid messageId)
    {
        var manifest = ReadJson<OutboxManifest>(Path.Combine(OutboxFolder(messageId), ManifestName));
        return manifest is { Version: 1 } && manifest.MessageId == messageId &&
            AttachmentEnvelope.TryParse(manifest.Envelope, out _) == AttachmentParse.Valid ? manifest.Envelope : null;
    }

    // True when the staged manifest exists as a plain file but opening it for reading fails now (a sharing violation or an
    // ACL hold); a missing or corrupt manifest is not busy.
    private bool ManifestBusy(Guid messageId)
    {
        string path = Path.Combine(OutboxFolder(messageId), ManifestName);
        if (!File.Exists(path)) return false;
        try { using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return File.Exists(path); }
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            AttachmentSource.RequirePlainParents(path);
            using SafeFileHandle handle = AttachmentSource.OpenRegularFile(path);
            return JsonSerializer.Deserialize<T>(AttachmentSource.ReadBounded(handle, 64 * 1024), Protocol.ControlProtocol.Json);
        }
        catch (Exception error) when (error is BrokerException or IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or NotSupportedException) { return null; }
    }

    // The GET endpoint's file: only while the staged manifest is exactly the stored message text and the file has exactly
    // the declared length. Missing answers 404; Busy (the file exists but cannot be opened now) answers 503 (F-16).
    internal OutgoingState OpenOutgoing(Guid messageId, string text, AttachmentEntry entry, out SafeFileHandle? handle)
    {
        handle = null;
        string? manifest = ReadManifest(messageId);
        // Round 5 (DECISIONS 96, item 9): a manifest that exists but cannot be opened now (another program holds it) is Busy,
        // a deferral, not the definite 404 that ends the message attachment_unavailable.
        if (manifest != text) return manifest is null && ManifestBusy(messageId) ? OutgoingState.Busy : OutgoingState.Missing;
        string path = Path.Combine(OutboxFolder(messageId), AttachmentEnvelope.StoredFileName(entry));
        if (!File.Exists(path)) return OutgoingState.Missing;
        try
        {
            AttachmentSource.RequirePlainParents(path);
            handle = AttachmentSource.OpenRegularFile(path);
        }
        catch (Exception error) when (error is BrokerException or IOException or UnauthorizedAccessException)
        {
            handle = null;
            return File.Exists(path) ? OutgoingState.Busy : OutgoingState.Missing;
        }
        try
        {
            if (RandomAccess.GetLength(handle) == entry.Bytes) return OutgoingState.Ready;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        handle.Dispose();
        handle = null;
        return OutgoingState.Missing;
    }

    // F-7: the first time a recipient answered attachment_fetch_pending for this staged message, stored beside its manifest
    // so it survives a worker restart. Null when nothing is staged (no bound can be kept for a message this broker did not stage).
    // A stamp in the future (the wall clock moved back) is not trusted: it is rewritten to now, so the bound starts again
    // from the corrected clock instead of never expiring (lead's ruling on F-7).
    // Round 5 (DECISIONS 96, item 8): a first deferral whose stamp could not be stored is kept here for this worker's lifetime,
    // so the sender's bound still runs; it is written to disk as soon as a save succeeds. Guarded by stagingGate.
    private readonly Dictionary<Guid, DateTimeOffset> unstoredFetchPending = new();

    internal DateTimeOffset? MarkFetchPending(Guid messageId)
    {
        string folder = OutboxFolder(messageId);
        string path = Path.Combine(folder, FetchPendingName);
        lock (stagingGate)
        {
            if (!Directory.Exists(folder)) { unstoredFetchPending.Remove(messageId); return null; }
            var now = clock.GetUtcNow();
            if (ReadStamp(path) is { } first && first <= now) return first;
            var stamp = unstoredFetchPending.TryGetValue(messageId, out var kept) && kept <= now ? kept : now;
            try
            {
                if (File.Exists(path)) File.Delete(path);
                LocalOutputFile.Save(path, System.Text.Encoding.ASCII.GetBytes(stamp.UtcTicks.ToString(CultureInfo.InvariantCulture)));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                if (ReadStamp(path) is { } stored && stored <= now) return stored;
                unstoredFetchPending[messageId] = stamp;
                return stamp;
            }
            unstoredFetchPending.Remove(messageId);
            return stamp;
        }
    }

    // F-7, the sender's own bound: true once this staged message has been answered attachment_fetch_pending for at least
    // the bound since the first such answer. False when nothing is staged here (only the recipient's bound applies then).
    internal bool FetchPendingExpired(Guid messageId, TimeSpan bound) =>
        MarkFetchPending(messageId) is { } first && clock.GetUtcNow() - first >= bound;

    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);
    private static bool WellFormed(string text)
    {
        try { _ = StrictUtf8.GetByteCount(text); return true; }
        catch (System.Text.EncoderFallbackException) { return false; }
    }

    private static DateTimeOffset? ReadStamp(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path).Trim();
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) && ticks > 0
                ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    // ---- Recipient ----

    // A published inbox file counts only while it still hashes to the declared value; a file that does not is deleted
    // (exact path) so the pull fetches it again.
    internal bool HasVerified(Guid senderBroker, Guid messageId, AttachmentEntry entry)
    {
        string path = Path.Combine(InboxFolder(senderBroker, messageId), AttachmentEnvelope.StoredFileName(entry));
        if (!File.Exists(path)) return false;
        try
        {
            AttachmentSource.RequirePlainParents(path);
            bool equal;
            using (var handle = AttachmentSource.OpenRegularFile(path))
                equal = Convert.ToHexStringLower(SHA256.HashData(AttachmentSource.ReadBounded(handle, AttachmentEnvelope.MaximumFileBytes))) == entry.Sha256;
            if (equal) return true;
        }
        catch (Exception error) when (error is BrokerException or IOException or UnauthorizedAccessException) { }
        DeleteExact([path], null);
        TryRecount();
        return false;
    }

    // F-5: room for one pull's declared bytes, reserved until Release (the pull failed, was abandoned, or its message was
    // admitted and its folder now counts as admitted bytes).
    internal ReserveResult Reserve(Guid senderBroker, Guid messageId, long bytes)
    {
        var key = (senderBroker, messageId);
        lock (gate)
        {
            if (reservations.ContainsKey(key)) return ReserveResult.Reserved;
            long admitted = 0, admittedPeer = 0, orphan = 0, orphanPeer = 0;
            foreach (var (folder, size) in inboxFolderBytes)
            {
                if (admittedFolders.Contains(folder)) { admitted += size; if (folder.Peer == senderBroker) admittedPeer += size; }
                // Round 6 (S-020, F-X1C-Q1): this message's own verified files, kept from an earlier partial pull, are part of
                // the bytes it reserves now, so they are not counted again as unadmitted data; other messages' still are.
                else if (folder != key && !reservations.ContainsKey(folder)) { orphan += size; if (folder.Peer == senderBroker) orphanPeer += size; }
            }
            long reserved = reservations.Values.Sum();
            long reservedPeer = reservations.Where(pair => pair.Key.Peer == senderBroker).Sum(pair => pair.Value);
            if (admittedPeer + bytes > Limits.PerPeerAdmittedBytes || admitted + bytes > Limits.QuarantineBytes) return ReserveResult.RefuseFull;
            if (admitted + orphan + reserved + bytes > Limits.QuarantineBytes)
                return ReserveResult.Defer;
            // Round 2b (M4): a broker's finished but never-admitted folders count toward its own share with its running pulls,
            // so it cannot keep the pool full of unadmitted bytes for every other broker.
            if (reservedPeer + orphanPeer + bytes > Limits.PerPeerInFlightBytes) return ReserveResult.Defer;
            // Round 5 (DECISIONS 96, item 6): the admitted cap above counts admitted bytes only, and admission does not check
            // again. This broker's running pulls and its verified folders not yet admitted become admitted bytes, so they count
            // too; while they hold the room the pull defers (they finish or are swept soon) instead of passing the cap.
            if (admittedPeer + reservedPeer + orphanPeer + bytes > Limits.PerPeerAdmittedBytes) return ReserveResult.Defer;
            reservations[key] = bytes;
            return ReserveResult.Reserved;
        }
    }

    internal void Release(Guid senderBroker, Guid messageId)
    {
        lock (gate) reservations.Remove((senderBroker, messageId));
    }

    // After admission: the reservation ends and the folder counts as admitted bytes. In memory only: the message is already
    // admitted, so a disk recount failing here must not turn that admission into an error for the sender (round 2b).
    internal void Admitted(Guid senderBroker, Guid messageId)
    {
        lock (gate)
        {
            reservations.Remove((senderBroker, messageId));
            admittedFolders.Add((senderBroker, messageId));
        }
    }

    // Publication of bytes whose size and SHA-256 the caller already verified in memory: unverified bytes never reach the
    // disk, and the Mark of the Web is on the temporary before it is renamed into place (LocalOutputFile.WriteZoneIdentifier).
    // The inbox manifest, the quarantine's record of the message, its sender broker and its envelope, is written once, before
    // the first file (DECISIONS 49). Fetch never reads it (the recipient keeps a paired sender's text, BrokerStore.Tombstoned);
    // the admission gate does, so one message ID carries one text (ReceivedEnvelopeSha256). InvalidDataException or
    // UnauthorizedAccessException here means the quarantine cannot hold files at all (F-6); IOException stays transient.
    internal void PublishVerified(Guid senderBroker, Guid messageId, AttachmentEntry entry, byte[] bytes, string envelope)
    {
        if (PublishFault?.Invoke() is { } fault) throw fault;
        string folder = InboxFolder(senderBroker, messageId);
        // Round 6 (S-017, item 11): from the manifest check to the count, under the folder's gate (DeleteInbox and the sweep's
        // deletion take it too), so a deletion never lands between the manifest check and the file's rename.
        lock (FolderGate(senderBroker, messageId))
        {
            Directory.CreateDirectory(folder);
            string manifest = Path.Combine(folder, ManifestName);
            if (!File.Exists(manifest))
                LocalOutputFile.Save(manifest, JsonSerializer.SerializeToUtf8Bytes(new InboxManifest(1, messageId, senderBroker, envelope),
                    Protocol.ControlProtocol.Json));
            LocalOutputFile.Save(Path.Combine(folder, AttachmentEnvelope.StoredFileName(entry)), bytes, beforePublish: BeforePublishFile,
                zoneIdentifier: InternetZone);
            Interlocked.Increment(ref publishedFiles);
            Interlocked.Add(ref publishedBytes, bytes.LongLength);
            QuarantineUnavailable = false;
            lock (gate)
            {
                inboxFolderBytes[(senderBroker, messageId)] = inboxFolderBytes.GetValueOrDefault((senderBroker, messageId)) + bytes.LongLength;
                inboxFiles++;
            }
        }
    }

    internal void MarkQuarantineUnavailable() => QuarantineUnavailable = true;

    // Round 2b (M2/M3): the SHA-256 of the envelope a received folder holds files for, or null when there is no such folder.
    internal string? ReceivedEnvelopeSha256(Guid senderBroker, Guid messageId)
    {
        string manifest = Path.Combine(InboxFolder(senderBroker, messageId), ManifestName);
        if (!File.Exists(manifest)) return null;
        var record = ReadJson<InboxManifest>(manifest);
        // A manifest that exists but cannot be read is not evidence of the same text: it holds the ID like a different one.
        return record is { Version: 1 } && record.MessageId == messageId && record.SenderBroker == senderBroker
            ? Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(record.Envelope))) : "unreadable";
    }

    // Round 2b (M2): a received folder counts as admitted only when its message is admitted here from that same broker AND
    // its text is a valid envelope. A plain text admitted under the same ID never makes a folder admitted.
    private bool AdmittedHere(Guid senderBroker, Guid messageId) =>
        store.FindReceiptWithText(messageId) is { } receipt && receipt.Message.Sender.BrokerId == senderBroker &&
        AttachmentEnvelope.TryParse(receipt.Message.Text, out _) == AttachmentParse.Valid;

    // A definite refusal's deletion of a received folder. Round 6 (S-017, items 5 and 11): it holds the admission gate, so it
    // never lands between the Verified branch's recheck and its admission, and the folder's gate, so it never lands inside a
    // publication. It keeps the folder of a message admitted here from that broker (the sweep's own rule): another forward of
    // the message admitted it while this one waited for the gate. True when the folder was kept for that reason.
    internal bool DeleteInbox(Guid senderBroker, Guid messageId)
    {
        lock (AdmissionGate)
        {
            if (AdmittedHere(senderBroker, messageId)) return true;
            // Round 6 (item 18, DECISIONS 122): never through an attachments root that is itself a reparse point, as the sweep.
            if (!RootIsReparsePoint())
                lock (FolderGate(senderBroker, messageId)) DeleteFolder(InboxFolder(senderBroker, messageId));
        }
        Release(senderBroker, messageId);
        Recount();
        return false;
    }

    // ---- fetch_attachment ----

    internal AttachmentView Describe(SessionAddress session, Guid messageId, int index)
    {
        var receipt = store.FindReceipt(messageId);
        if (receipt is null || receipt.Message.Recipient != session && receipt.Message.Sender != session)
            throw new BrokerException(AttachmentCodes.NotFound, "No such attachment for this session.");
        bool localSender = receipt.Message.Sender.BrokerId == store.BrokerId;
        Guid senderBroker = receipt.Message.Sender.BrokerId;
        // A message this broker SENT keeps no text once acknowledged (BrokerStore.Tombstoned), so its staging manifest names
        // the files. A paired sender's message keeps its text here until its record ends (ATTACHMENT_PAIRED_TEXT_KEPT).
        string? text = store.FindReceiptWithText(messageId)?.Message.Text ?? (localSender ? ReadManifest(messageId) : null);
        if (AttachmentEnvelope.TryParse(text, out var envelope) != AttachmentParse.Valid || index < 0 || index >= envelope!.Attachments.Count)
            throw new BrokerException(AttachmentCodes.NotFound, "No such attachment for this session.");
        var entry = envelope.Attachments[index];
        string path = Path.Combine(localSender ? OutboxFolder(messageId) : InboxFolder(senderBroker, messageId),
            AttachmentEnvelope.StoredFileName(entry));
        bool ready = false;
        try
        {
            if (File.Exists(path))
            {
                AttachmentSource.RequirePlainParents(path);
                using var handle = AttachmentSource.OpenRegularFile(path);
                ready = Convert.ToHexStringLower(SHA256.HashData(AttachmentSource.ReadBounded(handle, AttachmentEnvelope.MaximumFileBytes))) == entry.Sha256;
            }
        }
        catch (Exception error) when (error is BrokerException or IOException or UnauthorizedAccessException) { }
        // F-17: a staged copy that kept a Mark of the Web (a forwarded received file) is reported as internet, like a received one.
        string zone = !localSender || File.Exists(path + ":Zone.Identifier") ? "internet" : "local";
        return new(entry.Name, entry.Bytes, entry.Sha256, ready ? path : null, ready ? "ready" : "missing", zone);
    }

    // ---- Retention ----

    internal void SweepIfDue()
    {
        long now = clock.GetTimestamp();
        lock (gate)
        {
            if (lastSweep is { } last && clock.GetElapsedTime(last, now) < SweepInterval) return;
            lastSweep = now;
        }
        // The receipt loop's pass must survive a sweep that cannot read or write; the next minute tries again.
        try { Sweep(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    // DECISIONS 38 (round 2, F-3): a staged copy and a received folder follow one rule for every message, local or paired:
    // they go when the record is gone (after the orphan grace), when the message was Refused or Expired, or after 48 h.
    internal void Sweep()
    {
        // Round 2b: an allow removal whose save failed is retried here until it holds on disk too.
        Policy.RetryIfDirty();
        // Round 6 (item 18, DECISIONS 120): an attachments root that is itself a reparse point is never walked, or the sweep
        // would delete aged folders in whatever tree it points to. GuidFolders refuses one at outbox or inbox, a level down.
        if (RootIsReparsePoint()) return;
        var now = clock.GetUtcNow();
        foreach (var (messageId, folder) in GuidFolders(OutboxRoot))
        {
            var receipt = store.FindReceipt(messageId);
            TimeSpan age = Age(folder, now);
            // F-2: a folder whose message has not reached the store yet is a staging in progress; it gets the orphan grace.
            if (receipt is null && age > OrphanGrace || receipt?.Status is MessageStatus.Refused or MessageStatus.Expired || age > Retention)
                lock (stagingGate) DeleteFolder(folder.FullName);
        }
        foreach (var (senderBroker, brokerFolder) in GuidFolders(InboxRoot))
        {
            foreach (var (messageId, folder) in GuidFolders(brokerFolder.FullName))
            {
                lock (AdmissionGate)
                {
                    bool reserved;
                    lock (gate) reserved = reservations.ContainsKey((senderBroker, messageId));
                    if (reserved) continue; // a pull is running for it
                    bool admitted = AdmittedHere(senderBroker, messageId);
                    TimeSpan age = Age(folder, now);
                    // A received folder stays while its message is admitted (the recipient may still fetch after
                    // acknowledging), up to 48 h; any other folder is an unadmitted pull and goes after the orphan grace.
                    if (!admitted && age > OrphanGrace || age > Retention)
                        lock (FolderGate(senderBroker, messageId)) DeleteFolder(folder.FullName);
                }
            }
            TryDeleteEmpty(brokerFolder.FullName);
        }
        Recount();
    }

    // True when Root exists and is a reparse point (a read of its attributes that fails after the existence check counts as one).
    private bool RootIsReparsePoint()
    {
        try { return Directory.Exists(Root) && (File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    private static TimeSpan Age(DirectoryInfo folder, DateTimeOffset now)
    {
        try { return now - new DateTimeOffset(Directory.GetCreationTimeUtc(folder.FullName), TimeSpan.Zero); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return TimeSpan.Zero; }
    }

    // GUID-named plain directories directly under parent; a reparse point is never followed or deleted.
    private static IEnumerable<(Guid Id, DirectoryInfo Folder)> GuidFolders(string parent)
    {
        DirectoryInfo[] children;
        try
        {
            children = Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) == 0
                ? new DirectoryInfo(parent).GetDirectories() : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var child in children)
            if ((child.Attributes & FileAttributes.ReparsePoint) == 0 && Guid.TryParseExact(child.Name, "D", out Guid id))
                yield return (id, child);
    }

    // The plain files directly in one owned GUID folder, then the folder if that left it empty. Never recursive.
    // Round 5 (DECISIONS 96, item 1): each file is deleted on its own, so one that another program holds open stays alone
    // until it can be deleted; its siblings and the manifest (which holds the message text) still go now.
    private static void DeleteFolder(string folder)
    {
        FileInfo[] files;
        try
        {
            if (!Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return;
            files = new DirectoryInfo(folder).GetFiles();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { TryDeleteEmpty(folder); return; }
        foreach (var file in files)
        {
            try { if ((file.Attributes & FileAttributes.ReparsePoint) == 0) file.Delete(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } // the next file still goes
        }
        TryDeleteEmpty(folder);
    }

    private static void DeleteExact(IEnumerable<string> files, string? folder)
    {
        foreach (string file in files)
            try { File.Delete(file); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        if (folder is not null) TryDeleteEmpty(folder);
    }

    private static void TryDeleteEmpty(string folder)
    {
        try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder, false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // The quota picture from disk: per received folder its attachment bytes and whether its message is admitted here
    // (AdmittedHere), and the staged bytes. A restarted store recounts every folder, orphans included (F-5).
    private void TryRecount()
    {
        try { Recount(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BrokerException) { }
    }

    private void Recount()
    {
        if (RecountFault?.Invoke() is { } fault) throw fault;
        var folders = new Dictionary<(Guid Peer, Guid Message), long>();
        var admitted = new HashSet<(Guid Peer, Guid Message)>();
        long outbox = 0;
        int files = 0;
        foreach (var (senderBroker, brokerFolder) in GuidFolders(InboxRoot))
            foreach (var (messageId, folder) in GuidFolders(brokerFolder.FullName))
            {
                long bytes = 0;
                foreach (var file in SafeFiles(folder)) if (IsStoredFileName(file.Name)) { bytes += file.Length; files++; }
                folders[(senderBroker, messageId)] = bytes;
                if (AdmittedHere(senderBroker, messageId)) admitted.Add((senderBroker, messageId));
            }
        foreach (var (_, folder) in GuidFolders(OutboxRoot))
            foreach (var file in SafeFiles(folder)) if (IsStoredFileName(file.Name)) outbox += file.Length;
        lock (gate) { inboxFolderBytes = folders; admittedFolders = admitted; inboxFiles = files; outboxBytes = outbox; }
    }

    // Round 5 (DECISIONS 96, item 15): only a stored attachment, "<index>-<name>" as AttachmentEnvelope.StoredFileName writes
    // it, counts as a received file or as quota bytes; the manifest, the stamp and LocalOutputFile's temporary beside them do not.
    internal static bool IsStoredFileName(string name) =>
        name.Length > 2 && name[0] is >= '0' and <= '3' && name[1] == '-' && AttachmentEnvelope.ValidName(name[2..]);

    private static IEnumerable<FileInfo> SafeFiles(DirectoryInfo folder)
    {
        try { return folder.GetFiles().Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    internal Protocol.AttachmentsView View() => new(Enabled, Policy.Allowed(store.Peers()).ToArray(), InboxFiles, InboxBytes,
        Enabled && File.Exists(ReadmePath) ? ReadmePath : null, Policy.Unreadable, QuarantineUnavailable ? true : null,
        Policy.Dirty ? true : null);
}
