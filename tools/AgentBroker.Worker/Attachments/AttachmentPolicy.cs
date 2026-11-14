using System.Text.Json;
using AgentBroker.Core;
using DesktopAICompanion.ModuleKit;

namespace AgentBroker.Worker.Attachments;

// Which paired brokers may send this broker files (lane F, D-9). Each entry is an explicit owner choice for one broker ID
// AND its pinned certificate: pairing never implies it, revoking removes it, a re-paired broker starts without it, and a
// broker whose pinned certificate changed is denied until the owner allows it again. Inbound pulls only: sending needs the
// master switch and an agent's explicit send_with_attachments call.
public sealed class AttachmentPolicy
{
    internal const int MaximumPeers = 32;
    internal const string FileName = "attachment-peers.json";
    private sealed record Entry(Guid BrokerId, string CertificateSha256);
    private sealed record Document(int Version, Entry[] Peers);
    private readonly object gate = new();
    private readonly string path;
    private Entry[] entries = [];

    // An unreadable sidecar denies every broker (the safe default) until the owner's next explicit choice rewrites it.
    public bool Unreadable { get; private set; }

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
            entry.CertificateSha256.Equals(peer.CertificateSha256, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<Guid> Allowed(IEnumerable<PeerTrust> peers) => peers.Where(Allows).Select(peer => peer.BrokerId).ToArray();

    // The owner's choice for one currently paired broker, pinned to its current certificate.
    public void Set(PeerTrust peer, bool allow)
    {
        ArgumentNullException.ThrowIfNull(peer);
        lock (gate)
        {
            var next = entries.Where(entry => entry.BrokerId != peer.BrokerId).ToList();
            if (allow) next.Add(new(peer.BrokerId, peer.CertificateSha256.ToUpperInvariant()));
            if (next.Count > MaximumPeers) throw new BrokerException("attachment_policy_limit", "Attachment policy limit reached.");
            Write(next.ToArray());
        }
    }

    public void Remove(Guid brokerId)
    {
        lock (gate)
        {
            if (!Unreadable && entries.All(entry => entry.BrokerId != brokerId)) return;
            Write(entries.Where(entry => entry.BrokerId != brokerId).ToArray());
        }
    }

    private void Write(Entry[] next)
    {
        if (!AtomicFile.TryWriteAllText(path, JsonSerializer.Serialize(new Document(1, next), Protocol.ControlProtocol.Json), null))
            throw new IOException("Attachment policy could not be persisted.");
        entries = next;
        Unreadable = false;
    }
}
