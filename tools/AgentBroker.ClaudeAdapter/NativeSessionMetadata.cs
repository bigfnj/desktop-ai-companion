using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentBroker.ClaudeAdapter;

/// <summary>Untrusted public registry hints, never session readiness, authorization or process ownership proof.</summary>
public sealed record NativeSessionMetadata(int ProcessId, string CanonicalPipe, string SessionId,
    ulong StartFileTime, string? Kind, string? Status, string? Entrypoint)
{
    public string CreationField { get; init; } = "procStartFt";
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? ActivityObservedAt { get; init; }
    public const int MaximumRecordBytes = 262144; // Public 2.1.304 bbt read bound.
    private static readonly Regex Filename = new(@"^[1-9][0-9]{0,9}\.json$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>
    /// The native publisher writes on activity transitions, not a periodic heartbeat. Old idle is valid
    /// for a currently verified process incarnation. Callers must freshly read metadata and separately
    /// match OS creation time; this timestamp check alone conveys no liveness or admission authority.
    /// </summary>
    public bool HasPlausibleActivityTimestamp(DateTimeOffset now)
    {
        if (UpdatedAt is not { } updated || updated > now.AddSeconds(5) || StartFileTime > long.MaxValue) return false;
        DateTimeOffset created;
        try { created = DateTimeOffset.FromFileTime((long)StartFileTime); }
        catch (ArgumentOutOfRangeException) { return false; }
        // The registry uses milliseconds while native creation uses100ns units.
        if (updated < created.AddSeconds(-1)) return false;
        return ActivityObservedAt is not { } activity || activity >= created.AddSeconds(-1) && activity <= updated.AddSeconds(5);
    }

    /// <summary>Parses caller-supplied bytes only. Does not open paths, inspect processes, or read credentials.</summary>
    public static NativeSessionMetadata Parse(string filename, ReadOnlyMemory<byte> bytes)
    {
        if (!Filename.IsMatch(filename) || !int.TryParse(filename.AsSpan(0, filename.Length - 5),
            NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 1)
            throw new InvalidDataException("Noncanonical registry filename.");
        if (bytes.Length is 0 or > MaximumRecordBytes) throw new InvalidDataException("Registry record exceeds bounds.");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid registry record.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) throw new InvalidDataException("Ambiguous registry record.");
            string pipe = RequiredString(root, "messagingSocketPath", 256);
            string session = RequiredString(root, "sessionId", 36);
            if (!Guid.TryParseExact(session, "D", out Guid id) || id == Guid.Empty || id.ToString("D") != session)
                throw new InvalidDataException("Invalid native session ID.");
            string? nativeStart = OptionalString(root, "procStartFt", 20), legacyStart = OptionalString(root, "procStart", 20);
            string start = nativeStart ?? legacyStart ?? throw new InvalidDataException("Missing native creation hint.");
            if (nativeStart is not null && legacyStart is not null || !ulong.TryParse(start, NumberStyles.None,
                CultureInfo.InvariantCulture, out ulong fileTime) || fileTime == 0 || fileTime.ToString(CultureInfo.InvariantCulture) != start)
                throw new InvalidDataException("Exact native creation token is required.");
            string? kind = OptionalString(root, "kind", 32);
            string? status = OptionalString(root, "status", 16);
            if (kind is not null && kind is not ("interactive" or "bg" or "daemon" or "daemon-worker"))
                throw new InvalidDataException("Unknown native session kind.");
            if (status is not null && status is not ("busy" or "shell" or "idle" or "waiting"))
                throw new InvalidDataException("Unknown native session status.");
            // Some currently running native publishers store the exact FILETIME in procStart rather
            // than procStartFt. This remains a hint until it equals the actual OS creation FILETIME;
            // no .NET tick/epoch conversion or timestamp tolerance is permitted.
            return new(pid, CanonicalPeerTarget.Parse(pipe).CanonicalPath, session, fileTime, kind, status,
                OptionalString(root, "entrypoint", 128))
            {
                CreationField = nativeStart is null ? "procStart" : "procStartFt",
                UpdatedAt = OptionalTimestamp(root, "updatedAt"), ActivityObservedAt = OptionalTimestamp(root, "statusUpdatedAt"),
            };
        }
        catch (JsonException) { throw new InvalidDataException("Malformed registry record."); }
        catch (ArgumentException) { throw new InvalidDataException("Invalid native message pipe."); }
    }

    private static string RequiredString(JsonElement root, string name, int maximum)
        => OptionalString(root, name, maximum) ?? throw new InvalidDataException("Missing native metadata.");

    private static string? OptionalString(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not string text || text.Length == 0 || text.Length > maximum)
            throw new InvalidDataException("Invalid native metadata field.");
        return text;
    }

    private static DateTimeOffset? OptionalTimestamp(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long timestamp) || timestamp < 0)
            throw new InvalidDataException("Invalid native metadata timestamp.");
        try { return DateTimeOffset.FromUnixTimeMilliseconds(timestamp); }
        catch (ArgumentOutOfRangeException) { throw new InvalidDataException("Invalid native metadata timestamp."); }
    }
}
