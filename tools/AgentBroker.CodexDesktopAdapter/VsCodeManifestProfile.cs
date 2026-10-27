using System.Security.Cryptography;
using System.Text.Json;

namespace AgentBroker.CodexDesktopAdapter;

internal static class VsCodeManifestProfile
{
    internal const int MaximumBytes = 256 * 1024;

    internal static byte[] ReadBounded(Stream source)
    {
        using var contents = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (contents.Length + read > MaximumBytes) throw new DesktopAdapterException("vscode_manifest_invalid");
            contents.Write(buffer, 0, read);
        }
        return contents.ToArray();
    }

    // The expected hash is the reviewed build's "manifestFunctionalSha256" pin.
    internal static void Verify(byte[] bytes, string functionalSha256)
    {
        if (ComputeSha256(bytes) != functionalSha256) throw new DesktopAdapterException("vscode_image_mismatch");
    }

    internal static string ComputeSha256(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new DesktopAdapterException("vscode_manifest_invalid");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
                { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new DesktopAdapterException("vscode_manifest_invalid");
            ValidateUniqueNames(document.RootElement);
            using var canonical = new MemoryStream();
            using (var writer = new Utf8JsonWriter(canonical)) WriteCanonical(document.RootElement, writer, true);
            return Convert.ToHexString(SHA256.HashData(canonical.ToArray()));
        }
        catch (JsonException) { throw new DesktopAdapterException("vscode_manifest_invalid"); }
        catch (InvalidOperationException) { throw new DesktopAdapterException("vscode_manifest_invalid"); }
    }

    private static void ValidateUniqueNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new DesktopAdapterException("vscode_manifest_invalid");
                ValidateUniqueNames(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateUniqueNames(item);
        else if (value.ValueKind == JsonValueKind.String) _ = value.GetString();
    }

    private static void WriteCanonical(JsonElement value, Utf8JsonWriter writer, bool root = false)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    // Only VS Code's installation metadata is excluded; nested or differently cased names remain functional.
                    if (root && property.Name == "__metadata") continue;
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(item, writer);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break;
            // Preserve the exact numeric value representation rather than rounding through a floating-point conversion.
            case JsonValueKind.Number: writer.WriteRawValue(value.GetRawText()); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new DesktopAdapterException("vscode_manifest_invalid");
        }
    }
}
