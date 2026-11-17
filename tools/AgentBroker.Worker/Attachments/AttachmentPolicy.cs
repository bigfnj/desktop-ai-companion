using System.Text.Json;
using AgentBroker.Core;
using DesktopAICompanion.ModuleKit;

namespace AgentBroker.Worker.Attachments;

// Which paired brokers may send this broker files (lane F, D-9; round 2, F-14). Each entry is an explicit owner choice for
// one PAIRING: the broker ID, its pinned certificate and the pairing's PairedAt. Pairing never implies it, revoking removes
// it, a broker that pairs again (a new PairedAt) starts denied, and a changed certificate is denied until the owner allows
// it again. Inbound pulls only: sending needs the master switch and an agent's explicit send_with_attachments call.
// DECISIONS 41: allows persist while the master switch is off; removing one needs no switch, adding one does (control op).
public sealed class AttachmentPolicy
{
    internal const int MaximumPeers = 32;
    internal const string FileName = "attachment-peers.json";
    private sealed record Entry(Guid BrokerId, string CertificateSha256, DateTimeOffset PairedAt);
    private sealed record Document(int Version, Entry[] Peers);
    private readonly object gate = new();
    private readonly string path;
    private Entry[] entries = [];

    // An unreadable sidecar denies every broker (the safe default) until the owner's next explicit choice rewrites it.
    public bool Unreadable { get; private set; }
    // A removal whose write failed: memory already denies the broker; the next successful write brings the file in line.
    public bool Dirty { get; private set; }
    // Test seam, null in production: a fault thrown instead of writing the sidecar (F-14 checks).
    internal Func<Exception?>? WriteFault { get; set; }

    public AttachmentPolicy(string dataDirectory)
    {
        path = Path.Combine(dataDirectory, FileName);
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Attachment policy exceeds its bound.");
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path), Protocol.ControlProtocol.Json)
                ?? throw new JsonException();
            if (document.Version != 1 || document.Peers is null || document.Peers.Length > MaximumPeers ||
                document.Peers.Any(entry => entry is null || entry.BrokerId == Guid.Empty || !ValidThumbprint(entry.CertificateSha256)) ||
                document.Peers.Select(entry => entry.BrokerId).Distinct().Count() != document.Peers.Length)
                throw new JsonException();
            entries = document.Peers;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or
            NotSupportedException)
        {
            Unreadable = true;
            entries = [];
        }
    }

    private static bool ValidThumbprint(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public bool Allows(PeerTrust? peer)
    {
        if (peer is null) return false;
        lock (gate) return !Unreadable && entries.Any(entry => entry.BrokerId == peer.BrokerId &&
            entry.CertificateSha256.Equals(peer.CertificateSha256, StringComparison.OrdinalIgnoreCase) &&
            entry.PairedAt == peer.PairedAt);
    }

    public IReadOnlyList<Guid> Allowed(IEnumerable<PeerTrust> peers) => peers.Where(Allows).Select(peer => peer.BrokerId).ToArray();

    // The owner's choice for one currently paired broker, pinned to its current certificate and pairing.
    public void Set(PeerTrust peer, bool allow)
    {
        ArgumentNullException.ThrowIfNull(peer);
        lock (gate)
        {
            var next = entries.Where(entry => entry.BrokerId != peer.BrokerId).ToList();
            if (allow) next.Add(new(peer.BrokerId, peer.CertificateSha256.ToUpperInvariant(), peer.PairedAt));
            if (next.Count > MaximumPeers) throw new BrokerException("attachment_policy_limit", "Attachment policy limit reached.");
            Write(next.ToArray());
        }
    }

    // Removal clears memory FIRST, so a failed write still denies the broker from now on; the write failure is rethrown so
    // the caller (RevokePeer) can stop before committing anything else, and the sidecar is marked dirty.
    public void Remove(Guid brokerId)
    {
        lock (gate)
        {
            if (!Unreadable && !Dirty && entries.All(entry => entry.BrokerId != brokerId)) return;
            var next = entries.Where(entry => entry.BrokerId != brokerId).ToArray();
            entries = next;
            Write(next);
        }
    }

    // Round 2b: a removal whose save failed leaves the sidecar Dirty (memory already denies the broker, the file does not yet).
    // The store's once-a-minute sweep calls this until the file agrees, and the status shows the change as not saved until then.
    public void RetryIfDirty()
    {
        lock (gate)
        {
            if (!Dirty) return;
            try { Write(entries); }
            catch (IOException) { }
        }
    }

    private void Write(Entry[] next)
    {
        try
        {
            if (WriteFault?.Invoke() is { } fault) throw fault;
            if (!AtomicFile.TryWriteAllText(path, JsonSerializer.Serialize(new Document(1, next), Protocol.ControlProtocol.Json), null))
                throw new IOException("Attachment policy could not be persisted.");
        }
        // Round 5 (DECISIONS 96, item 14): only a change already in force in memory (a removal, or the retry of one) is left
        // Dirty; a failed allow changed nothing, so it is reported as a failure and not as an unsaved change.
        catch (IOException) { if (ReferenceEquals(entries, next)) Dirty = true; throw; }
        entries = next;
        Unreadable = false;
        Dirty = false;
    }
}
