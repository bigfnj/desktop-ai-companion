using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace AgentBroker.Core;

// File attachments between paired brokers (1.10.5, lane F). The envelope is ordinary message text, so BrokerMessage and the
// schema-1 store are unchanged and a 1.10.4 peer sees the JSON as plain text. This type only parses and composes: it never
// opens a file, never builds a path from peer data beyond the validated name, and never decides admission (the worker's
// attachment store and federation gate do).
public sealed record AttachmentEntry(int Index, string Name, long Bytes, string Sha256);

public sealed record AttachmentEnvelopeData(string Text, IReadOnlyList<AttachmentEntry> Attachments)
{
    public long TotalBytes => Attachments.Sum(entry => entry.Bytes);
}

// NotEnvelope: ordinary text. Invalid: the text claims the envelope kind but breaks a rule. A 1.10.5 broker passes Invalid
// neither as files nor as plain text: the federation gate refuses it and the plain send tools refuse it as reserved.
public enum AttachmentParse { NotEnvelope, Invalid, Valid }

public static class AttachmentEnvelope
{
    public const string Kind = "agentbroker.attachments.v1";
    public const int MaximumFiles = 4;
    public const long MaximumFileBytes = 8L * 1024 * 1024;
    public const long MaximumTotalBytes = 16L * 1024 * 1024;
    public const int MaximumNameLength = 120;
    // The store's default BrokerLimits.MaximumTextBytes: the envelope is the whole message text.
    public const int MaximumEnvelopeBytes = 16 * 1024;

    // All Unicode ranges stay unescaped, so non-ASCII text costs its UTF-8 size, not six bytes per character; quotes,
    // control characters and HTML-sensitive characters are still escaped by the default encoder rules.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    public static AttachmentParse TryParse(string? text, out AttachmentEnvelopeData? envelope)
    {
        envelope = null;
        if (string.IsNullOrEmpty(text)) return AttachmentParse.NotEnvelope;
        string trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return AttachmentParse.NotEnvelope;
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException) { return AttachmentParse.NotEnvelope; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kind) ||
                kind.ValueKind != JsonValueKind.String || kind.GetString() != Kind) return AttachmentParse.NotEnvelope;
            // From here the text claims the kind: any broken rule is Invalid, never ordinary text.
            return Strict(text, root, out envelope) ? AttachmentParse.Valid : AttachmentParse.Invalid;
        }
    }

    private static bool Strict(string text, JsonElement root, out AttachmentEnvelopeData? envelope)
    {
        envelope = null;
        if (Encoding.UTF8.GetByteCount(text) > MaximumEnvelopeBytes || !ExactMembers(root, "kind", "text", "attachments")) return false;
        var inner = root.GetProperty("text");
        var list = root.GetProperty("attachments");
        if (inner.ValueKind != JsonValueKind.String || list.ValueKind != JsonValueKind.Array) return false;
        string body = inner.GetString()!;
        if (body.Contains('\0')) return false;
        int count = list.GetArrayLength();
        if (count is < 1 or > MaximumFiles) return false;
        var entries = new List<AttachmentEntry>(count);
        long total = 0;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !ExactMembers(item, "index", "name", "bytes", "sha256")) return false;
            var index = item.GetProperty("index");
            var name = item.GetProperty("name");
            var bytes = item.GetProperty("bytes");
            var sha256 = item.GetProperty("sha256");
            if (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out int position) || position != entries.Count) return false;
            if (name.ValueKind != JsonValueKind.String || !ValidName(name.GetString())) return false;
            if (bytes.ValueKind != JsonValueKind.Number || !bytes.TryGetInt64(out long size) || size is < 0 or > MaximumFileBytes) return false;
            if (sha256.ValueKind != JsonValueKind.String || !ValidSha256(sha256.GetString())) return false;
            total += size;
            if (total > MaximumTotalBytes) return false;
            entries.Add(new(position, name.GetString()!, size, sha256.GetString()!));
        }
        envelope = new(body, entries);
        return true;
    }

    // Exactly one occurrence of each name and nothing else: as many members as names, each one allowed, and every name
    // present, so a duplicated member always leaves another name missing.
    private static bool ExactMembers(JsonElement element, params string[] names)
    {
        int count = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (Array.IndexOf(names, property.Name) < 0) return false;
            count++;
        }
        return count == names.Length && names.All(name => element.TryGetProperty(name, out _));
    }

    public static bool ValidSha256(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    // A name the broker chose: ASCII letters, digits, dot, dash and underscore, at most 120 characters, no leading or
    // trailing dot (so never "." or ".."; Windows drops a trailing dot), and no reserved device stem.
    public static bool ValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaximumNameLength || name[0] == '.' || name[^1] == '.') return false;
        foreach (char c in name) if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) return false;
        return !ReservedStem(name);
    }

    private static bool ReservedStem(string name)
    {
        string stem = name.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)) return true;
        return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '0' and <= '9';
    }

    // The broker's name for a caller's file: every other character becomes '_', leading and trailing dots go, a long name is
    // cut to 120 keeping a short extension, and a reserved stem gets a '_' prefix. The result always passes ValidName.
    public static string Sanitise(string? fileName)
    {
        var builder = new StringBuilder();
        foreach (char c in fileName ?? "") builder.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');
        string name = builder.ToString().Trim('.');
        if (name.Length > MaximumNameLength)
        {
            int dot = name.LastIndexOf('.');
            string extension = dot > 0 && name.Length - dot <= 16 ? name[dot..] : "";
            name = name[..(MaximumNameLength - extension.Length)].TrimEnd('.') + extension;
        }
        if (name.Length == 0) name = "file";
        if (ReservedStem(name)) name = "_" + name;
        if (name.Length > MaximumNameLength) name = name[..MaximumNameLength].TrimEnd('.');
        return ValidName(name) ? name : "file";
    }

    // The file's name on disk: its index first, so two attachments with one name never collide and no stored name is a
    // device name.
    public static string StoredFileName(AttachmentEntry entry) =>
        entry.Index.ToString(CultureInfo.InvariantCulture) + "-" + entry.Name;

    public static string Compose(string text, IReadOnlyList<AttachmentEntry> attachments)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(attachments);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", Kind);
            writer.WriteString("text", text);
            writer.WriteStartArray("attachments");
            foreach (var entry in attachments)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", entry.Index);
                writer.WriteString("name", entry.Name);
                writer.WriteNumber("bytes", entry.Bytes);
                writer.WriteString("sha256", entry.Sha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        string composed = Encoding.UTF8.GetString(buffer.ToArray());
        // What this broker sends must be exactly what a recipient will accept.
        if (TryParse(composed, out var parsed) != AttachmentParse.Valid || parsed!.Text != text ||
            !parsed.Attachments.SequenceEqual(attachments)) throw new ArgumentException("attachment_envelope_invalid");
        return composed;
    }
}

// The fixed codes lane F adds. Tool refusals never cross the wire; admission codes travel in a paired broker's 409 body
// (Worker PeerAdmissionRefusal) and only FetchPending is a deferral, everything else a Refusal.
public static class AttachmentCodes
{
    public const string Disabled = "attachments_disabled";
    public const string SourceInvalid = "attachment_source_invalid";
    public const string TooLarge = "attachment_too_large";
    public const string OutboxFull = "attachment_outbox_full";
    public const string EnvelopeReserved = "attachment_envelope_reserved";
    public const string NotFound = "attachment_not_found";
    public const string StorageUnavailable = "attachment_storage_unavailable";
    // Round 2 (F-18): the note does not fit beside the attachment list inside the 16 KiB envelope.
    public const string TextTooLong = "attachment_text_too_long";

    public const string RefusedByPolicy = "attachments_refused_by_policy";
    public const string EnvelopeInvalid = "attachment_envelope_invalid";
    public const string Mismatch = "attachment_mismatch";
    public const string Unavailable = "attachment_unavailable";
    public const string FetchFailed = "attachment_fetch_failed";
    public const string QuarantineFull = "attachment_quarantine_full";
    public const string FetchPending = "attachment_fetch_pending";
    // Round 2 (F-6): the recipient's quarantine cannot hold files at all (its folder is not a plain local NTFS folder), a
    // definite refusal rather than ten minutes of deferral blamed on the sender.
    public const string QuarantineUnavailable = "attachment_quarantine_unavailable";

    public static bool IsAdmissionCode([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? code) => code is RefusedByPolicy or EnvelopeInvalid or Mismatch or Unavailable or
        FetchFailed or QuarantineFull or FetchPending or QuarantineUnavailable;
}
