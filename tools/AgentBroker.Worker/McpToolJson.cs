using AgentBroker.Core;
using ModelContextProtocol;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AgentBroker.Worker;

internal static class McpToolJson
{
    // The SDK advertises non-optional constructor parameters as required, including nullable ones.
    // Its default null omission otherwise makes non-reply messages and unblocked recovery entries
    // violate that schema. Scope this to tool marshalling: these domain types also participate in
    // durable state, federation and retry contracts, which do not need this SDK-specific adjustment.
    internal static JsonSerializerOptions Create() => new(McpJsonUtilities.DefaultOptions)
    {
        TypeInfoResolver = (McpJsonUtilities.DefaultOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver())
            .WithAddedModifier(IncludeRequiredQueryNulls)
    };

    private static void IncludeRequiredQueryNulls(JsonTypeInfo type)
    {
        string? required = type.Type == typeof(BrokerMessage) ? "replyTo"
            : type.Type == typeof(NativeRecoveryEntry) ? "blockReason" : null;
        if (required is null) return;
        foreach (var property in type.Properties)
            if (property.Name == required) property.ShouldSerialize = static (_, _) => true;
    }
}
