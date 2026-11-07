using AgentBroker.Core;
using System.Text;
using System.Text.Json;

namespace AgentBroker.Worker;

/// <summary>Opt-in custom-title metadata bridge. It never emits a hook decision or reads private session files.</summary>
public static class ClaudeTitleHook
{
    public const string Command = "--claude-title-hook";
    internal const int MaximumInputBytes = 128 * 1024;
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal sealed record Reply(int ExitCode, JsonElement Envelope);
    internal delegate Task<Reply> Call(string tool, JsonElement arguments, CancellationToken token);

    public static Task<int> RunCommandAsync(string[] arguments, Stream input, Stream output, Stream error,
        CancellationToken cancellation = default)
    {
        if (arguments.Length != 3 || arguments[0] != Command || arguments[1] != "--data" ||
            !Path.IsPathFullyQualified(arguments[2])) return CompleteAsync("skipped", output, error);
        string directory = arguments[2];
        return RunAsync(input, output, error, async (tool, values, token) =>
        {
            using var result = new MemoryStream();
            int code = await McpCallClient.RunToolAsync(directory, tool, values, result, token);
            using var document = JsonDocument.Parse(result.ToArray());
            return new(code, document.RootElement.Clone());
        }, cancellation);
    }

    internal static async Task<int> RunAsync(Stream input, Stream output, Stream error, Call call,
        CancellationToken cancellation = default)
    {
        string status = "skipped";
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(Deadline);
            byte[] bytes = await McpStdioBridge.ReadBoundedAsync(input, MaximumInputBytes, deadline.Token).WaitAsync(deadline.Token);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return await CompleteAsync(status, output, error);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) return await CompleteAsync(status, output, error);
            if (!root.TryGetProperty("hook_event_name", out var eventName) || eventName.ValueKind != JsonValueKind.String ||
                eventName.GetString() is not ("SessionStart" or "UserPromptSubmit") ||
                !root.TryGetProperty("session_id", out var session) || session.ValueKind != JsonValueKind.String ||
                session.GetString() is not { } sessionId || !Guid.TryParseExact(sessionId, "D", out var parsed) ||
                parsed == Guid.Empty || parsed.ToString("D") != sessionId) return await CompleteAsync(status, output, error);
            // Missing means no documented custom title. Never infer one from prompt, summary, transcript or paths.
            string title = "";
            if (root.TryGetProperty("session_title", out var custom))
            {
                if (custom.ValueKind != JsonValueKind.String) return await CompleteAsync(status, output, error);
                title = custom.GetString()!;
            }
            if (!SessionTitleRegistry.ValidTitle(title)) return await CompleteAsync(status, output, error);
            var identity = await call("whoami", JsonSerializer.SerializeToElement(new { }, Json), deadline.Token).WaitAsync(deadline.Token);
            if (!Succeeded(identity) || !identity.Envelope.TryGetProperty("result", out var row) || row.ValueKind != JsonValueKind.Object ||
                !row.TryGetProperty("adapter", out var adapter) || adapter.GetString() != "claude-native" ||
                !row.TryGetProperty("address", out var addressValue)) return await CompleteAsync("unavailable", output, error);
            var address = addressValue.Deserialize<SessionAddress>(Json);
            if (address is null || address.SessionId != "claude:" + sessionId || !SessionTitleRegistry.CanonicalClaude(address))
                return await CompleteAsync("unavailable", output, error);
            address.Validate();
            var changed = await call("set_session_title", JsonSerializer.SerializeToElement(new { session = address, title }, Json), deadline.Token).WaitAsync(deadline.Token);
            status = Succeeded(changed) ? "updated" : "unavailable";
        }
        // Hooks are observational. Even an unexpected local failure must not block Claude or echo private input/errors.
        catch (Exception) { status = "unavailable"; }
        return await CompleteAsync(status, output, error);
    }

    private static bool Succeeded(Reply reply) => reply.ExitCode == 0 && reply.Envelope.ValueKind == JsonValueKind.Object &&
        reply.Envelope.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() == "ok";

    private static async Task<int> CompleteAsync(string status, Stream output, Stream error)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try
        {
            await output.WriteAsync("{\"suppressOutput\":true}\n"u8.ToArray(), deadline.Token).AsTask().WaitAsync(deadline.Token);
            await output.FlushAsync(deadline.Token).WaitAsync(deadline.Token);
            byte[] line = Encoding.UTF8.GetBytes("agentbroker-title-hook: " + status + "\n");
            await error.WriteAsync(line, deadline.Token).AsTask().WaitAsync(deadline.Token);
            await error.FlushAsync(deadline.Token).WaitAsync(deadline.Token);
        }
        catch (Exception) { }
        return 0;
    }
}
