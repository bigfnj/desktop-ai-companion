using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentBroker.ClaudeAdapter;

/// <summary>Owns the only retained token bytes. Never converts the token to an immutable string.</summary>
public sealed class PeerCredential : IDisposable
{
    private readonly byte[] token;
    private bool disposed;
    public string? ProcessStart { get; }
    public string? ProcessStartFileTime { get; }
    public string? PidDomain { get; }

    private PeerCredential(byte[] token, string? processStart, string? processStartFileTime, string? pidDomain)
    {
        this.token = token;
        ProcessStart = processStart; ProcessStartFileTime = processStartFileTime; PidDomain = pidDomain;
    }

    /// <summary>Consumes and zeros the supplied buffer on success and failure. The caller relinquishes it.</summary>
    public static PeerCredential ParseAndConsume(byte[] bytes)
    {
        byte[]? token = null;
        bool retained = false;
        try
        {
            if (bytes.Length is < 2 or > 4096) throw new InvalidDataException("Invalid credential record.");
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 4 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new InvalidDataException("Invalid credential record.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? start = null, fileTime = null, domain = null;
            bool ended = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) { ended = true; break; }
                if (reader.TokenType != JsonTokenType.PropertyName) throw new InvalidDataException("Invalid credential record.");
                string name = reader.ValueTextEquals("peerToken"u8) ? "peerToken" :
                    reader.ValueTextEquals("procStart"u8) ? "procStart" :
                    reader.ValueTextEquals("procStartFt"u8) ? "procStartFt" :
                    reader.ValueTextEquals("pidDomain"u8) ? "pidDomain" : throw new InvalidDataException("Invalid credential record.");
                if (!seen.Add(name) || !reader.Read()
                    || reader.TokenType != JsonTokenType.String) throw new InvalidDataException("Invalid credential record.");
                if (name == "peerToken")
                {
                    if (reader.ValueIsEscaped || reader.ValueSpan.Length != 32 || !IsLowerHex(reader.ValueSpan))
                        throw new InvalidDataException("Invalid credential record.");
                    token = reader.ValueSpan.ToArray();
                }
                else
                {
                    string value = reader.GetString()!;
                    if (value.Length is < 1 or > 160 || value.Any(char.IsControl)) throw new InvalidDataException("Invalid credential record.");
                    if (name is "procStart" or "procStartFt")
                    {
                        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong ft) || ft == 0 ||
                            ft.ToString(CultureInfo.InvariantCulture) != value)
                            throw new InvalidDataException("Invalid credential record.");
                        if (name == "procStart") start = value;
                        else fileTime = value;
                    }
                    else domain = value;
                }
            }
            if (!ended || reader.Read() || token == null || start is not null && fileTime is not null)
                throw new InvalidDataException("Invalid credential record.");
            retained = true;
            return new PeerCredential(token, start, fileTime, domain);
        }
        catch (JsonException) { throw new InvalidDataException("Invalid credential record."); }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (!retained && token != null) CryptographicOperations.ZeroMemory(token);
        }
    }

    private static bool IsLowerHex(ReadOnlySpan<byte> value)
    {
        foreach (byte c in value) if (c is not (>= (byte)'0' and <= (byte)'9') and not (>= (byte)'a' and <= (byte)'f')) return false;
        return true;
    }

    internal byte[] AuthenticationFrame()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ReadOnlySpan<byte> prefix = "{\"type\":\"auth\",\"token\":\""u8;
        ReadOnlySpan<byte> suffix = "\"}\n"u8;
        byte[] frame = new byte[prefix.Length + token.Length + suffix.Length];
        prefix.CopyTo(frame); token.CopyTo(frame, prefix.Length); suffix.CopyTo(frame.AsSpan(prefix.Length + token.Length));
        return frame;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; CryptographicOperations.ZeroMemory(token);
    }
}
