using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DesktopAICompanion.AgentBroker.CodexRelay;

internal static class JsonlFrames
{
    // The prototype fails closed rather than allocating unbounded protocol frames. Never truncate a frame.
    internal const int MaximumFrameBytes = 16 * 1024 * 1024;

    internal static async IAsyncEnumerable<byte[]> ReadAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        byte[] buffer = new byte[8192];
        using var frame = new MemoryStream();
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
        {
            int start = 0;
            for (int i = 0; i < count; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                Add(frame, buffer.AsSpan(start, i - start + 1));
                yield return frame.ToArray();
                frame.SetLength(0);
                start = i + 1;
            }
            Add(frame, buffer.AsSpan(start, count - start));
        }
        if (frame.Length != 0) yield return frame.ToArray();
    }

    private static void Add(MemoryStream frame, ReadOnlySpan<byte> bytes)
    {
        if (frame.Length + bytes.Length > MaximumFrameBytes) throw new IOException("Protocol frame exceeds limit.");
        frame.Write(bytes);
    }

    internal static JsonDocument? Parse(byte[] frame)
    {
        try { return JsonDocument.Parse(frame); }
        catch (JsonException) { return null; }
    }

    internal static string? String(JsonElement value, string property)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var found)
            && found.ValueKind == JsonValueKind.String ? found.GetString() : null;

    internal static string? Id(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("id", out var id)) return null;
        return id.ValueKind is JsonValueKind.String or JsonValueKind.Number ? id.GetRawText() : null;
    }

    internal static byte[] Encode(object value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value);
        byte[] frame = new byte[json.Length + 1];
        json.CopyTo(frame, 0);
        frame[^1] = (byte)'\n';
        return frame;
    }
}
