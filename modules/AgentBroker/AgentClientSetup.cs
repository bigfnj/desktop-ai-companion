using System.Diagnostics;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using AgentBroker.Protocol;

namespace DesktopAICompanion.AgentBroker;

internal sealed record ClientCommandResult(int ExitCode, string Output, string Error);
internal interface IAgentClientCommand
{
    Task<ClientCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token);
}

internal sealed class AgentClientSetup(IAgentClientCommand command)
{
    internal const string ServerName = "desktop-ai-companion-agentbroker";
    public async Task<string> ConfigureAsync(string client, string worker, string directory, CancellationToken token,
        string? loopbackMcpEndpoint = null)
    {
        if (loopbackMcpEndpoint is not null && (client != "claude" || !IsCanonicalLoopbackEndpoint(loopbackMcpEndpoint)))
            return "The broker returned an invalid local MCP endpoint. Refresh its status before connecting Claude Code.";
        if (client is not ("codex" or "claude") || !Path.IsPathFullyQualified(worker) || !File.Exists(worker) ||
            !Path.IsPathFullyQualified(directory)) return "The packaged worker and module storage must be available first.";
        string[] bridgeArguments = ["--mcp-stdio", "--data", directory];
        try
        {
            token.ThrowIfCancellationRequested();
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
                return Failure(client, existing, verification: true, loopbackMcpEndpoint);
            token.ThrowIfCancellationRequested();
            var arguments = new List<string> { "mcp", "add" };
            if (client == "claude") arguments.AddRange(["--transport", loopbackMcpEndpoint is null ? "stdio" : "http", "--scope", "user"]);
            arguments.Add(ServerName);
            if (loopbackMcpEndpoint is not null) arguments.Add(loopbackMcpEndpoint);
            else
            {
                arguments.Add("--");
                arguments.Add(worker); arguments.AddRange(bridgeArguments);
            }
            var added = await command.RunAsync(client, arguments, token);
            return added.ExitCode == 0
                ? client == "claude"
                    ? "AgentBroker MCP was added for this user in Claude Code. In VS Code, start a new Claude conversation and check /mcp."
                    : "AgentBroker MCP was added for this user. Reconnect MCP or restart the client to load its reply tools."
                : Failure(client, added, verification: false, loopbackMcpEndpoint);
        }
        catch (OperationCanceledException) { return "Setup was cancelled or timed out. Check the client's MCP settings before retrying."; }
        catch (Exception) { return "The client CLI could not complete setup. Install or repair that client's supported CLI, then try again."; }
    }

    internal static string? EndpointFromConnection(ControlResponse? response) =>
        response is { Success: true, Connection: { BearerToken: null } connection } ? connection.McpEndpoint ?? "" : null;

    internal static bool HasVerifiedConnection(ControlResponse? response) =>
        response is { Success: true, Connection: { } connection } && connection.McpEndpoint is { } endpoint &&
        IsCanonicalLoopbackEndpoint(endpoint);

    private static bool IsCanonicalLoopbackEndpoint(string value)
    {
        if (value.Length > 64 || !Uri.TryCreate(value, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http" ||
            endpoint.Host != "127.0.0.1" || endpoint.Port is < 1 or > 65535 || endpoint.AbsolutePath != "/mcp" ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0) return false;
        return value == "http://127.0.0.1:" + endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/mcp";
    }

    private static string Failure(string client, ClientCommandResult result, bool verification, string? loopbackMcpEndpoint)
    {
        // Vendor diagnostics can echo environment/config values. Match known reasons in memory
        // and return fixed guidance only, never show or log the raw output.
        string text = result.Output + "\n" + result.Error;
        bool Has(string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
        string name = client == "claude" ? "Claude Code" : "Codex";
        string code = result.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (loopbackMcpEndpoint is not null && Has("not allowed by enterprise policy"))
            return $"Claude Code policy does not allow this local MCP server URL (exit {code}). Ask the administrator to allowlist {loopbackMcpEndpoint} for AgentBroker.";
        if (Has("enterprise policy") || Has("enterprise MCP configuration") || Has("managed settings") || Has("managed MCP"))
            return $"{name} refused MCP setup because of managed policy (exit {code}). Ask the administrator whether this server is permitted.";
        if (Has("already exists"))
            return $"An MCP entry with this name already exists (exit {code}). Review it in {name}'s MCP settings before changing it.";
        if (Has("EACCES") || Has("EPERM") || Has("access is denied") || Has("access denied") || Has("permission denied") ||
            Has("failed to write") || Has("config lock") || Has("lock acquisition"))
            return $"{name} could not access or save its MCP configuration (exit {code}). Check file permissions and configuration locks before retrying.";
        if (Has("unknown option") || Has("unexpected argument") || Has("Usage:") || Has("required option") || Has("command is required"))
            return $"{name} rejected the MCP setup arguments (exit {code}). Check that its supported CLI is installed and current.";
        if (Has("requires a shell tool") || ((Has("Git Bash") || Has("git-bash") || Has("PowerShell")) &&
            (Has("not found") || Has("disabled") || Has("requires"))))
            return $"{name} could not find its required shell (exit {code}). Make Git Bash or PowerShell available to the client, then retry.";
        return verification
            ? $"Could not verify {name}'s MCP configuration (exit {code}). Check its MCP settings before retrying."
            : $"{name} did not confirm MCP setup (exit {code}). Check its MCP settings before retrying.";
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
    internal static bool CanUseClaudeFallback(string executable, int nativeErrorCode) =>
        OperatingSystem.IsWindows() && executable.Equals("claude", StringComparison.OrdinalIgnoreCase) && nativeErrorCode is 2 or 3;

    private static string CanonicalClaudeExecutable()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Path.IsPathFullyQualified(profile) || profile.StartsWith("\\\\", StringComparison.Ordinal))
            throw new IOException("The current user's local Claude CLI profile is unavailable.");
        string executable = Path.Combine(profile, ".local", "bin", "claude.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("The current user's Claude CLI is unavailable.");
        for (string? path = executable; path is not null; path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The current user's Claude CLI path contains a reparse component.");
        return executable;
    }

    public async Task<ClientCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
          StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process? started;
        try { started = Process.Start(start); }
        catch (Win32Exception error) when (CanUseClaudeFallback(executable, error.NativeErrorCode))
        {
            // Only a failed bare-name launch gets one canonical user-install fallback. A child
            // exit code, policy refusal, timeout, or other launch error never reaches this path.
            token.ThrowIfCancellationRequested();
            start.FileName = CanonicalClaudeExecutable();
            started = Process.Start(start);
        }
        using var process = started ?? throw new IOException("Client did not start.");
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
