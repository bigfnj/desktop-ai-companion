using System.Text.Json;

namespace AgentBroker.Worker.Federation;

/// <summary>A pinned paired endpoint explicitly refused admission of this submission.</summary>
public sealed class PeerAdmissionRefusedException : Exception
{
    internal PeerAdmissionRefusedException(Guid brokerId, string code) : base("The paired broker refused message admission.")
    { BrokerId = brokerId; Code = code; }
    public Guid BrokerId { get; }
    public string Code { get; }
}

internal static class PeerAdmissionRefusal
{
    internal static bool TryRead(ReadOnlySpan<byte> body, Guid brokerId, out PeerAdmissionRefusedException? refusal)
    {
        refusal = null;
        if (body.Length is 0 or > 1024 || brokerId == Guid.Empty) return false;
        try
        {
            using var document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var properties = document.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 1 || properties[0].Name != "error" || properties[0].Value.ValueKind != JsonValueKind.String) return false;
            string? code = properties[0].Value.GetString();
            // Admission-only store outcomes known to leave this submission unaccepted. Future codes stay uncertain.
            if (code is not ("recipient_unavailable" or "recipient_cannot_receive" or "private_session" or
                "recipient_queue_full" or "message_limit")) return false;
            refusal = new(brokerId, code);
            return true;
        }
        catch (JsonException) { return false; }
    }
}
