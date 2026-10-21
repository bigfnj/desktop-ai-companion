using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DesktopAICompanion.AgentBroker;

internal sealed record ClientCommandResult(int ExitCode, string Output, string Error);
internal interface IAgentClientCommand
{
    Task<ClientCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token);
}

internal sealed class AgentClientSetup(IAgentClientCommand command)
{
    internal const string ServerName = "desktop-ai-companion-agentbroker";
    public async Task<string> ConfigureAsync(string client, string worker, string directory, CancellationToken token)
    {
        if (client is not ("codex" or "claude") || !Path.IsPathFullyQualified(worker) || !File.Exists(worker) ||
            !Path.IsPathFullyQualified(directory)) return "The packaged worker and module storage must be available first.";
        string[] bridgeArguments = ["--mcp-stdio", "--data", directory];
        try
        {
            string[] get = client == "codex" ? ["mcp", "get", ServerName, "--json"] : ["mcp", "get", ServerName];
            var existing = await command.RunAsync(client, get, token);
            if (existing.ExitCode == 0)
            {
                if (client == "codex" && MatchesCodex(existing.Output, worker, bridgeArguments))
                    return "AgentBroker is already configured. Reconnect MCP or restart the client to load it.";
                return "An MCP entry with this name already exists. Review it with the client's MCP settings before changing it.";
            }
            // A malformed config or unrelated CLI error must never become an overwrite attempt.
            string diagnostics = existing.Output + "\n" + existing.Error;
            if (!diagnostics.Contains(ServerName, StringComparison.Ordinal) ||
                !(diagnostics.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                  diagnostics.Contains("No MCP server", StringComparison.OrdinalIgnoreCase)))
                return "Could not verify the client's MCP configuration. No entry was changed.";
            var arguments = new List<string> { "mcp", "add" };
            if (client == "claude") arguments.AddRange(["--transport", "stdio", "--scope", "user"]);
            arguments.Add(ServerName);
            arguments.Add("--");
            arguments.Add(worker); arguments.AddRange(bridgeArguments);
            var added = await command.RunAsync(client, arguments, token);
            return added.ExitCode == 0
                ? "AgentBroker MCP was added for this user. Reconnect MCP or restart the client to load its reply tools."
                : "The client did not confirm setup. Check its MCP settings before retrying.";
        }
        catch (OperationCanceledException) { return "Setup was cancelled or timed out. Check the client's MCP settings before retrying."; }
        catch (Exception) { return "The client CLI could not complete setup. Install or repair that client's supported CLI, then try again."; }
    }

    private static bool MatchesCodex(string json, string worker, string[] arguments)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var transport = document.RootElement.GetProperty("transport");
            return transport.GetProperty("type").GetString() == "stdio" &&
                string.Equals(transport.GetProperty("command").GetString(), worker, StringComparison.OrdinalIgnoreCase) &&
                transport.GetProperty("args").EnumerateArray().Select(v => v.GetString()).SequenceEqual(arguments);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }
}

internal sealed class AgentClientCommand : IAgentClientCommand
{
    public async Task<ClientCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
          StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Client did not start.");
        try
        {
            Task<string> output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            Task<string> error = ReadBoundedAsync(process.StandardError, deadline.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token));
            return new(process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: false);
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder(); char[] buffer = new char[1024]; int read;
        while ((read = await reader.ReadAsync(buffer, token)) > 0)
        {
            if (result.Length + read > 64 * 1024) throw new IOException("Client response exceeded its bound.");
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }
}
