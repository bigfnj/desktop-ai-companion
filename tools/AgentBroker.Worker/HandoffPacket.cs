using System.Text;
using System.Text.Json;

namespace AgentBroker.Worker;

// A client convention inside ordinary message Text, not a broker/federation schema or a trusted task assertion.
public sealed record HandoffArtifact(string Role, string Path, string Sha256, long Bytes);
public sealed record HandoffPacket(string Kind, string TaskId, string State, string Summary,
    IReadOnlyList<HandoffArtifact> Artifacts)
{
    public const string Version = "agentbroker.handoff.v1";
    public const int MaximumTextBytes = 16 * 1024;
    public const int MaximumInputBytes = 128 * 1024;
    // This is ordinary broker message text, never HTML. Count the UTF-8 bytes of this exact representation.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 8, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static HandoffPacket Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumInputBytes) throw Invalid();
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = Fields(document.RootElement, "kind", "taskId", "state", "summary", "artifacts");
        string kind = Text(root["kind"], 40), task = Text(root["taskId"], 128), state = Text(root["state"], 20);
        string summary = Text(root["summary"], 8192, multiline: true);
        if (kind != Version || state is not ("request" or "progress" or "result" or "cannot_do") ||
            root["artifacts"].ValueKind != JsonValueKind.Array || root["artifacts"].GetArrayLength() > 16)
            throw Invalid();
        var artifacts = new List<HandoffArtifact>();
        foreach (var entry in root["artifacts"].EnumerateArray())
        {
            var fields = Fields(entry, "role", "path", "sha256", "bytes");
            string role = Text(fields["role"], 40), path = Text(fields["path"], 4096), hash = Text(fields["sha256"], 64);
            if (hash.Length != 64 || hash.Any(character => !char.IsAsciiHexDigit(character)) ||
                fields["bytes"].ValueKind != JsonValueKind.Number ||
                !fields["bytes"].TryGetInt64(out long length) || length < 0) throw Invalid();
            // The path and hash are peer claims. Do not open the path, fetch it, or infer any access permission.
            artifacts.Add(new(role, path, hash.ToUpperInvariant(), length));
        }
        var packet = new HandoffPacket(kind, task, state, summary, artifacts);
        if (JsonSerializer.SerializeToUtf8Bytes(packet, Json).Length > MaximumTextBytes) throw Invalid();
        return packet;
    }

    public string ToText()
    {
        // Public record construction and with-copies receive the same validation as a parsed packet.
        foreach (string? value in new[] { Kind, TaskId, State, Summary }) RequireUnicode(value);
        if (Artifacts is not null)
            foreach (var artifact in Artifacts)
                if (artifact is not null)
                    foreach (string? value in new[] { artifact.Role, artifact.Path, artifact.Sha256 }) RequireUnicode(value);
        var normalized = Parse(JsonSerializer.SerializeToUtf8Bytes(this, Json));
        return JsonSerializer.Serialize(normalized, Json);
    }

    private static Dictionary<string, JsonElement> Fields(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid();
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!expected.Contains(property.Name, StringComparer.Ordinal) ||
                !fields.TryAdd(property.Name, property.Value)) throw Invalid();
        if (fields.Count != expected.Length) throw Invalid();
        return fields;
    }

    private static string Text(JsonElement element, int maximum, bool multiline = false)
    {
        if (element.ValueKind != JsonValueKind.String) throw Invalid();
        string? text;
        try { text = element.GetString(); }
        catch (InvalidOperationException) { throw Invalid(); }
        if (text is null ||
            string.IsNullOrWhiteSpace(text) || text.Length > maximum ||
            text.Any(character => char.IsControl(character) && !(multiline && character is '\r' or '\n' or '\t')))
            throw Invalid();
        RequireUnicode(text);
        return text;
    }

    private static void RequireUnicode(string? text)
    {
        if (text is null) return; // The required-field validator names null as invalid metadata.
        try { _ = new UTF8Encoding(false, true).GetByteCount(text); }
        catch (EncoderFallbackException) { throw Invalid(); }
    }

    private static InvalidDataException Invalid() => new("invalid_handoff");
}
