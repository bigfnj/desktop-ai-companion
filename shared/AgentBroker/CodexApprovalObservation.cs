#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBroker.Observations;

/// <summary>Aggregate browser observation. It contains no conversation identity, prompt or command text.</summary>
public sealed record CodexApprovalObservation([property: JsonRequired] int Count, [property: JsonRequired] DateTimeOffset ObservedAtUtc)
{
    public const string ContextKey = "agentflow.codexApprovalPrompts";
    public const int MaximumCount = 256;
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 4 };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static CodexApprovalObservation? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 256) return null;
        try
        {
            var value = JsonSerializer.Deserialize<CodexApprovalObservation>(raw, Json);
            return value is { Count: >= 0 and <= MaximumCount } && value.ObservedAtUtc != default ? value : null;
        }
        catch (JsonException) { return null; }
    }

    public static CodexApprovalView View(CodexApprovalObservation? value, DateTimeOffset now)
    {
        if (value is null || value.Count is < 0 or > MaximumCount || value.ObservedAtUtc == default ||
            value.ObservedAtUtc > now.AddSeconds(5)) return new("unavailable", null, null);
        if (now - value.ObservedAtUtc >= FreshFor) return new("stale", null, value.ObservedAtUtc);
        return new("fresh", value.Count, value.ObservedAtUtc);
    }
}

public sealed record CodexApprovalView(string State, int? VisiblePromptCount, DateTimeOffset? ObservedAtUtc);
