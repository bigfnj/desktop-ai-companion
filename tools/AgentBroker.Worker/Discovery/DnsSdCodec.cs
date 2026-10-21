using System.Buffers.Binary;
using System.Net;
using System.Text;
using AgentBroker.Worker.Security;

namespace AgentBroker.Worker.Discovery;

/// <summary>Bounded DNS-SD metadata only, following RFC 6762/6763. Never conveys sessions or trust.</summary>
public static class DnsSdCodec
{
    public const string Service = "_agentbroker._tcp.local";
    public sealed record Advertisement(BrokerAdvertisement Broker, uint Ttl);
    public static byte[] Query()
    {
        using var output = new MemoryStream(); output.Write(new byte[12]); Name(output, Service); U16(output, 12); U16(output, 1);
        byte[] bytes = output.ToArray(); bytes[5] = 1; return bytes;
    }
    public static byte[] Announce(BrokerAdvertisement peer, uint ttl = 120)
    {
        NetworkPolicy.Advertisement(peer); var uri = new Uri(peer.Endpoint); var address = IPAddress.Parse(uri.Host);
        if (!NetworkPolicy.PrivateV4(address, false)) throw new InvalidDataException("Discovery only advertises private LAN IPv4.");
        string instance = peer.BrokerId.ToString("N") + "." + Service, host = "ab-" + peer.BrokerId.ToString("N") + ".local";
        using var output = new MemoryStream(); output.Write(new byte[12]);
        Record(Service, 12, Data(s => Name(s, instance)));
        Record(instance, 33, Data(s => { U16(s, 0); U16(s, 0); U16(s, uri.Port); Name(s, host); }));
        Record(instance, 16, Data(s => { Txt(s, "v=1"); Txt(s, "id=" + peer.BrokerId.ToString("N")); Txt(s, "fp=" + peer.CertificateSha256); }));
        Record(host, 1, address.GetAddressBytes());
        byte[] bytes = output.ToArray(); bytes[2] = 0x84; bytes[7] = 4; return bytes;
        void Record(string name, int type, byte[] data) { Name(output, name); U16(output, type); U16(output, type == 12 ? 1 : 0x8001); U32(output, ttl); U16(output, data.Length); output.Write(data); }
    }
    public static bool IsQuery(byte[] packet)
    {
        try { int offset = 12; return packet.Length is >= 12 and <= 1400 && (packet[2] & 0xf8) == 0 &&
            Read16(packet, 4) is > 0 and <= 8 && ReadName(packet, ref offset) == Service && Read16(packet, offset) is 12 or 255 && (Read16(packet, offset + 2) & 0x7fff) == 1; }
        catch (InvalidDataException) { return false; }
    }
    public static IReadOnlyList<Advertisement> Parse(byte[] packet, IPAddress source, DateTimeOffset now)
    {
        if (packet.Length is < 12 or > 1400 || !NetworkPolicy.PrivateV4(source, false) || (packet[2] & 0xf8) != 0x80) return [];
        try
        {
            int questions = Read16(packet, 4), count = Read16(packet, 6) + Read16(packet, 8) + Read16(packet, 10), offset = 12;
            if (questions > 8 || count > 32) return [];
            for (int i = 0; i < questions; i++) { ReadName(packet, ref offset); offset += 4; Need(packet, offset, 0); }
            var records = new List<RecordData>();
            for (int i = 0; i < count; i++)
            {
                string owner = ReadName(packet, ref offset); Need(packet, offset, 10);
                int type = Read16(packet, offset), cls = Read16(packet, offset + 2) & 0x7fff;
                uint ttl = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(offset + 4, 4)); int length = Read16(packet, offset + 8); offset += 10;
                Need(packet, offset, length); if (cls == 1) records.Add(new(owner, type, Math.Min(ttl, 120), offset, length)); offset += length;
            }
            var results = new List<Advertisement>();
            foreach (var ptr in records.Where(r => r.Owner == Service && r.Type == 12))
            {
                int pointer = ptr.Offset; string instance = ReadName(packet, ref pointer);
                if (pointer != ptr.Offset + ptr.Length) continue;
                var srv = records.FirstOrDefault(r => r.Owner == instance && r.Type == 33);
                var txt = records.FirstOrDefault(r => r.Owner == instance && r.Type == 16);
                if (srv is null || txt is null || srv.Length < 7) continue;
                int port = Read16(packet, srv.Offset + 4), hostAt = srv.Offset + 6; string host = ReadName(packet, ref hostAt);
                if (hostAt != srv.Offset + srv.Length) continue;
                var a = records.FirstOrDefault(r => r.Owner == host && r.Type == 1 && r.Length == 4);
                if (a is null || port == 0 || !new IPAddress(packet.AsSpan(a.Offset, 4)).Equals(NetworkPolicy.Normalize(source))) continue;
                var fields = new Dictionary<string, string>(StringComparer.Ordinal); int end = txt.Offset + txt.Length, at = txt.Offset;
                while (at < end) { int size = packet[at++]; if (at + size > end) throw new InvalidDataException(); string value = Encoding.ASCII.GetString(packet, at, size); at += size; int equal = value.IndexOf('='); if (equal > 0 && !fields.TryAdd(value[..equal], value[(equal + 1)..])) throw new InvalidDataException(); }
                if (fields.GetValueOrDefault("v") != "1" || !Guid.TryParseExact(fields.GetValueOrDefault("id"), "N", out var id)) continue;
                var peer = new BrokerAdvertisement(id, "Broker " + id.ToString("N")[..8], $"https://{source}:{port}/", fields.GetValueOrDefault("fp", ""), now);
                NetworkPolicy.Advertisement(peer);
                if (instance != id.ToString("N") + "." + Service || host != "ab-" + id.ToString("N") + ".local") continue;
                results.Add(new(peer, Math.Min(Math.Min(ptr.Ttl, srv.Ttl), Math.Min(txt.Ttl, a.Ttl))));
            }
            return results;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { return []; }
    }
    private sealed record RecordData(string Owner, int Type, uint Ttl, int Offset, int Length);
    private static string ReadName(byte[] packet, ref int offset)
    {
        var labels = new List<string>(); int cursor = offset, jumps = 0; bool redirected = false;
        while (true)
        {
            Need(packet, cursor, 1); int size = packet[cursor++];
            if ((size & 0xc0) == 0xc0) { Need(packet, cursor, 1); if (!redirected) offset = cursor + 1; cursor = ((size & 0x3f) << 8) | packet[cursor]; redirected = true; if (++jumps > 16) throw new InvalidDataException(); continue; }
            if (size == 0) { if (!redirected) offset = cursor; break; }
            if (size > 63 || labels.Count >= 16) throw new InvalidDataException(); Need(packet, cursor, size);
            string label = Encoding.ASCII.GetString(packet, cursor, size); if (label.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-')) throw new InvalidDataException();
            labels.Add(label.ToLowerInvariant()); cursor += size;
        }
        string name = string.Join('.', labels); if (name.Length > 253) throw new InvalidDataException(); return name;
    }
    private static void Need(byte[] data, int offset, int count) { if (offset < 0 || count < 0 || offset > data.Length - count) throw new InvalidDataException(); }
    private static int Read16(byte[] data, int offset) { Need(data, offset, 2); return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2)); }
    private static void Name(Stream stream, string name) { foreach (string label in name.Split('.')) { byte[] bytes = Encoding.ASCII.GetBytes(label); stream.WriteByte((byte)bytes.Length); stream.Write(bytes); } stream.WriteByte(0); }
    private static void U16(Stream stream, int value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)value); stream.Write(bytes); }
    private static void U32(Stream stream, uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); stream.Write(bytes); }
    private static byte[] Data(Action<Stream> write) { using var s = new MemoryStream(); write(s); return s.ToArray(); }
    private static void Txt(Stream stream, string text) { byte[] bytes = Encoding.ASCII.GetBytes(text); stream.WriteByte((byte)bytes.Length); stream.Write(bytes); }
}
