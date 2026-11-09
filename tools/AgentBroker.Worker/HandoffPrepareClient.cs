using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBroker.Worker;

/// <summary>Prepare typed handoff text without contacting a broker or opening referenced artifacts.</summary>
public static class HandoffPrepareClient
{
    public const string Command = "--handoff-prepare";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };

    public static async Task<int> RunCommandAsync(string[] arguments, Stream output, CancellationToken cancellation = default)
    {
        string? request = null, destination = null;
        try
        {
            if (arguments.Length == 0 || arguments[0] != Command) throw new ArgumentException("invalid_arguments");
            for (int index = 1; index < arguments.Length; index++)
            {
                if (index + 1 >= arguments.Length) throw new ArgumentException("invalid_arguments");
                if (arguments[index] == "--request-file" && request is null) request = arguments[++index];
                else if (arguments[index] == "--output-file" && destination is null) destination = arguments[++index];
                else throw new ArgumentException("invalid_arguments");
            }
            if (request is null || !Path.IsPathFullyQualified(request) ||
                !Path.GetExtension(request).Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                destination is not null && !Path.IsPathFullyQualified(destination)) throw new ArgumentException("invalid_arguments");
        }
        catch (ArgumentException)
        {
            return await WriteAsync(new { status = "invalid_arguments", dispatchState = "not_dispatched", code = "invalid_arguments" }, 2);
        }

        string text;
        try
        {
            string full = Path.GetFullPath(request);
            for (string? component = full; component is not null; component = Path.GetDirectoryName(component))
                if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("invalid_handoff");
            await using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
            byte[] bytes = await McpStdioBridge.ReadBoundedAsync(input, HandoffPacket.MaximumInputBytes, cancellation);
            text = HandoffPacket.Parse(bytes).ToText();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
            ArgumentException or OperationCanceledException or NotSupportedException or System.Security.SecurityException)
        {
            return await WriteAsync(new { status = "invalid_handoff", dispatchState = "not_dispatched", code = "invalid_handoff" }, 2);
        }

        byte[] payload = Encoding.UTF8.GetBytes(text);
        LocalOutputFile.Retention? retention = null;
        if (destination is not null)
        {
            try { retention = LocalOutputFile.Save(destination, payload); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or System.Security.SecurityException)
            {
                return await WriteAsync(new { status = "output_error", dispatchState = "not_dispatched", retained = false,
                    code = "output_unavailable", nextAction = "Choose a new local file in an existing plain directory; no broker was contacted." }, 74);
            }
        }
        return await WriteAsync(new { status = "ok", dispatchState = "not_dispatched", text, textBytes = payload.Length,
            sha256 = Convert.ToHexString(SHA256.HashData(payload)), retention }, 0);

        async Task<int> WriteAsync(object value, int exitCode)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            await output.WriteAsync(bytes, CancellationToken.None);
            await output.WriteAsync("\n"u8.ToArray(), CancellationToken.None);
            await output.FlushAsync(CancellationToken.None);
            return exitCode;
        }
    }
}
