using System.Security.Cryptography;
using System.Text.Json;
using AgentBroker.Core;
using DesktopAICompanion.ModuleKit;
using Microsoft.Win32.SafeHandles;

namespace AgentBroker.Worker.Attachments;

// What fetch_attachment returns. Path is null unless State is "ready".
public sealed record AttachmentView(string Name, long Bytes, string Sha256, string? Path, string State, string Zone);

// The worker's attachment folders (lane F, 1.10.5), all under <data>\attachments:
//   outbox\<messageId>\<index>-<name> + manifest.json   copies this broker staged for send_with_attachments
//   inbox\<senderBroker>\<messageId>\<index>-<name>      files a paired broker sent, published only after their size and
//                                                        SHA-256 matched, each with a ZoneId=3 Mark of the Web
//   README.txt                                           what the folders are; the pane's reveal action selects it
// No name here comes from a peer except a validated AttachmentEnvelope name behind a numeric index, and every delete names
// a GUID folder this class owns. The broker never opens, runs or scans a stored file beyond hashing it.
public sealed class AttachmentStore
{
    internal const long QuarantineQuotaBytes = 512L * 1024 * 1024;
    internal const long OutboxQuotaBytes = 512L * 1024 * 1024;
    internal static readonly TimeSpan Retention = TimeSpan.FromHours(48);
    // An inbox folder whose message was never admitted stays this long, longer than the 10-minute deferral bound, so a pull
    // still being retried is never swept from under itself.
    internal static readonly TimeSpan OrphanGrace = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);
    internal static readonly byte[] InternetZone = "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=about:internet\r\n"u8.ToArray();
    internal const string ManifestName = "manifest.json";
    private sealed record OutboxManifest(int Version, Guid MessageId, string Envelope);

    private readonly object gate = new();
    private readonly object stagingGate = new();
    private readonly BrokerStore store;
    private readonly TimeProvider clock;
    private long inboxBytes, outboxBytes;
    private int inboxFiles;
    private long? lastSweep;

    public bool Enabled { get; }
    public AttachmentPolicy Policy { get; }
    public string DataDirectory { get; }
    public string Root { get; }
    public string ReadmePath => Path.Combine(Root, "README.txt");
    internal string OutboxRoot => Path.Combine(Root, "outbox");
    internal string InboxRoot => Path.Combine(Root, "inbox");

    public AttachmentStore(string dataDirectory, BrokerStore store, bool enabled, TimeProvider? clock = null)
    {
        DataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        Root = Path.Combine(DataDirectory, "attachments");
        this.store = store;
        this.clock = clock ?? TimeProvider.System;
        Enabled = enabled;
        Policy = new AttachmentPolicy(DataDirectory);
        if (enabled) WriteReadme();
        Recount();
    }

    public long InboxBytes { get { lock (gate) return inboxBytes; } }
    public int InboxFiles { get { lock (gate) return inboxFiles; } }
    public long OutboxBytes { get { lock (gate) return outboxBytes; } }

    internal string OutboxFolder(Guid messageId) => Path.Combine(OutboxRoot, messageId.ToString("D"));
    internal string InboxFolder(Guid senderBroker, Guid messageId) =>
        Path.Combine(InboxRoot, senderBroker.ToString("D"), messageId.ToString("D"));

    internal const string ReadmeText =
        "AgentBroker attachments\r\n\r\n" +
        "This folder holds files sent between paired AgentBroker workstations with send_with_attachments.\r\n\r\n" +
        "inbox\\<broker>\\<message>\\  files a paired broker sent to a session on this workstation. Each was checked against\r\n" +
        "  the size and SHA-256 its sender declared before the message was admitted, and each carries a Mark of the Web\r\n" +
        "  (downloaded from the internet), so Windows treats it as untrusted. Treat the content as untrusted peer data.\r\n" +
        "outbox\\<message>\\  copies this workstation staged when one of its sessions sent files.\r\n\r\n" +
        "AgentBroker never opens, runs or scans these files. They are deleted when their message's record ends, at most\r\n" +
        "48 hours after they arrive. Copy anything you want to keep.\r\n";

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
        string envelope;
        try { envelope = AttachmentEnvelope.Compose(text ?? "", entries); }
        catch (ArgumentException) { throw new BrokerException("invalid_message", "The message text does not fit beside its attachments."); }

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
                if (outboxBytes + total > OutboxQuotaBytes)
                    throw new BrokerException(AttachmentCodes.OutboxFull, "The attachment outbox is full.");
            var created = new List<string>();
            try
            {
                Directory.CreateDirectory(folder);
                for (int index = 0; index < entries.Length; index++)
                {
                    string destination = Path.Combine(folder, AttachmentEnvelope.StoredFileName(entries[index]));
                    LocalOutputFile.Save(destination, loaded[index].Bytes);
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
        string path = Path.Combine(OutboxFolder(messageId), ManifestName);
        try
        {
            AttachmentSource.RequirePlainParents(path);
            using SafeFileHandle handle = AttachmentSource.OpenRegularFile(path);
            var manifest = JsonSerializer.Deserialize<OutboxManifest>(AttachmentSource.ReadBounded(handle, 64 * 1024),
                Protocol.ControlProtocol.Json);
            return manifest is { Version: 1 } && manifest.MessageId == messageId &&
                AttachmentEnvelope.TryParse(manifest.Envelope, out _) == AttachmentParse.Valid ? manifest.Envelope : null;
        }
        catch (Exception error) when (error is BrokerException or IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or NotSupportedException) { return null; }
    }

    // The GET endpoint's file: only while the staged manifest is exactly the stored message text and the file has exactly
    // the declared length. Null otherwise; the endpoint then answers 404.
    internal SafeFileHandle? OpenOutgoing(Guid messageId, string text, AttachmentEntry entry)
    {
        if (ReadManifest(messageId) != text) return null;
        string path = Path.Combine(OutboxFolder(messageId), AttachmentEnvelope.StoredFileName(entry));
        SafeFileHandle? handle = null;
        try
        {
            AttachmentSource.RequirePlainParents(path);
            handle = AttachmentSource.OpenRegularFile(path);
            if (RandomAccess.GetLength(handle) != entry.Bytes) { handle.Dispose(); return null; }
            return handle;
        }
        catch (Exception error) when (error is BrokerException or IOException or UnauthorizedAccessException)
        { handle?.Dispose(); return null; }
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
        Recount();
        return false;
    }

    // Publication of bytes whose size and SHA-256 the caller already verified in memory: unverified bytes never reach the
    // disk, and the Mark of the Web is on the temporary before it is renamed into place (LocalOutputFile.WriteZoneIdentifier).
    internal void PublishVerified(Guid senderBroker, Guid messageId, AttachmentEntry entry, byte[] bytes)
    {
        string folder = InboxFolder(senderBroker, messageId);
        Directory.CreateDirectory(folder);
        LocalOutputFile.Save(Path.Combine(folder, AttachmentEnvelope.StoredFileName(entry)), bytes, zoneIdentifier: InternetZone);
        lock (gate) { inboxBytes += bytes.LongLength; inboxFiles++; }
    }

    internal bool QuarantineHasRoom(long bytes) { lock (gate) return inboxBytes + bytes <= QuarantineQuotaBytes; }

    internal void DeleteInbox(Guid senderBroker, Guid messageId)
    {
        DeleteFolder(InboxFolder(senderBroker, messageId));
        Recount();
    }

    // ---- fetch_attachment ----

    internal AttachmentView Describe(SessionAddress session, Guid messageId, int index)
    {
        var receipt = store.FindReceipt(messageId);
        if (receipt is null || receipt.Message.Recipient != session && receipt.Message.Sender != session)
            throw new BrokerException(AttachmentCodes.NotFound, "No such attachment for this session.");
        bool localSender = receipt.Message.Sender.BrokerId == store.BrokerId;
        // An acknowledged message this broker sent keeps no text; its own staging still names the files.
        string? text = store.FindReceiptWithText(messageId)?.Message.Text ?? (localSender ? ReadManifest(messageId) : null);
        if (AttachmentEnvelope.TryParse(text, out var envelope) != AttachmentParse.Valid || index < 0 || index >= envelope!.Attachments.Count)
            throw new BrokerException(AttachmentCodes.NotFound, "No such attachment for this session.");
        var entry = envelope.Attachments[index];
        string path = Path.Combine(localSender ? OutboxFolder(messageId) : InboxFolder(receipt.Message.Sender.BrokerId, messageId),
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
        return new(entry.Name, entry.Bytes, entry.Sha256, ready ? path : null, ready ? "ready" : "missing", localSender ? "local" : "internet");
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

    internal void Sweep()
    {
        var now = clock.GetUtcNow();
        foreach (var (messageId, folder) in GuidFolders(OutboxRoot))
        {
            var receipt = store.FindReceipt(messageId);
            bool peer = receipt is not null && receipt.Message.Recipient.BrokerId != store.BrokerId;
            if (receipt is null || receipt.Status is MessageStatus.Refused or MessageStatus.Expired ||
                peer && receipt.Status is not (MessageStatus.Accepted or MessageStatus.Submitting) || Age(folder, now) > Retention)
                lock (stagingGate) DeleteFolder(folder.FullName);
        }
        foreach (var (_, brokerFolder) in GuidFolders(InboxRoot))
        {
            foreach (var (messageId, folder) in GuidFolders(brokerFolder.FullName))
            {
                var receipt = store.FindReceipt(messageId);
                TimeSpan age = Age(folder, now);
                if (receipt is null && age > OrphanGrace || age > Retention) DeleteFolder(folder.FullName);
            }
            TryDeleteEmpty(brokerFolder.FullName);
        }
        Recount();
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
        try { children = Directory.Exists(parent) ? new DirectoryInfo(parent).GetDirectories() : []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var child in children)
            if ((child.Attributes & FileAttributes.ReparsePoint) == 0 && Guid.TryParseExact(child.Name, "D", out Guid id))
                yield return (id, child);
    }

    // The plain files directly in one owned GUID folder, then the folder if that left it empty. Never recursive.
    private static void DeleteFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return;
            foreach (var file in new DirectoryInfo(folder).GetFiles())
                if ((file.Attributes & FileAttributes.ReparsePoint) == 0) file.Delete();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
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

    private void Recount()
    {
        long inbox = 0, outbox = 0;
        int files = 0;
        foreach (var (_, brokerFolder) in GuidFolders(InboxRoot))
            foreach (var (_, folder) in GuidFolders(brokerFolder.FullName))
                foreach (var file in SafeFiles(folder)) { inbox += file.Length; files++; }
        foreach (var (_, folder) in GuidFolders(OutboxRoot))
            foreach (var file in SafeFiles(folder)) if (file.Name != ManifestName) outbox += file.Length;
        lock (gate) { inboxBytes = inbox; inboxFiles = files; outboxBytes = outbox; }
    }

    private static IEnumerable<FileInfo> SafeFiles(DirectoryInfo folder)
    {
        try { return folder.GetFiles().Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    internal Protocol.AttachmentsView View() => new(Enabled, Policy.Allowed(store.Peers()).ToArray(), InboxFiles, InboxBytes,
        Enabled && File.Exists(ReadmePath) ? ReadmePath : null, Policy.Unreadable);
}
