using System.Diagnostics;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using AgentBroker.Protocol;

namespace DesktopAICompanion.AgentBroker;

internal sealed record ClientCommandResult(int ExitCode, string Output, string Error);
internal enum ClientConfiguration { CliMissing, Absent, Configured, DifferentEntry, Disabled, PolicyBlocked, Unrecognized, Failed, Cancelled }
// Fixed codes only. Scope: user, project, local, managed, "" (the client reports none) or unrecognized.
// Transport: http, stdio or other. Health is Claude's own connection check: connected, tools_unavailable,
// needs_authentication, failed, connection_error, pending_approval, rejected, disabled, timed_out or unknown.
internal sealed record ClientInspection(ClientConfiguration State, string Scope = "", string Transport = "", string Health = "",
    int ExitCode = 0, DateTimeOffset CheckedUtc = default);
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

    /// <summary>
    /// Read-only detection through the vendor's own read command (`claude mcp get`, `codex mcp get --json`),
    /// for the fixed AgentBroker name only. Never adds, edits or removes an entry, never reads a client
    /// configuration file, and never returns or logs vendor output: every field is a fixed code. Claude's
    /// get also runs its own connection check against the configured entry, which is the one place this
    /// detection observes reachability directly.
    /// </summary>
    public async Task<ClientInspection> InspectAsync(string client, string worker, string directory, string loopbackMcpEndpoint,
        DateTimeOffset now, CancellationToken token)
    {
        if (client is not ("codex" or "claude")) throw new ArgumentOutOfRangeException(nameof(client));
        string[] get = client == "codex" ? ["mcp", "get", ServerName, "--json"] : ["mcp", "get", ServerName];
        ClientCommandResult result;
        try
        {
            token.ThrowIfCancellationRequested();
            result = await command.RunAsync(client, get, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(ClientConfiguration.Cancelled, CheckedUtc: now); }
        catch (OperationCanceledException) { return new(ClientConfiguration.Failed, Health: "timed_out", CheckedUtc: now); }
        // Only an executable is launched (never a script shim such as an npm .cmd), and Claude's one
        // fallback is the current user's standard install; both missing is "not found", not a failure.
        catch (Exception error) when (error is FileNotFoundException || error is Win32Exception { NativeErrorCode: 2 or 3 })
        { return new(ClientConfiguration.CliMissing, CheckedUtc: now); }
        catch (Exception) { return new(ClientConfiguration.Failed, CheckedUtc: now); }
        string text = result.Output + "\n" + result.Error;
        bool Has(string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
        if (result.ExitCode != 0)
        {
            if (Has("enterprise policy") || Has("enterprise MCP configuration") || Has("managed settings") || Has("managed MCP"))
                return new(ClientConfiguration.PolicyBlocked, ExitCode: result.ExitCode, CheckedUtc: now);
            // The same absence rule setup uses: our exact name and a not-found phrase, nothing looser. Claude's
            // message also lists the other configured server names, which are matched here and never kept.
            if (text.Contains(ServerName, StringComparison.Ordinal) && (Has("not found") || Has("No MCP server")))
                return new(ClientConfiguration.Absent, ExitCode: result.ExitCode, CheckedUtc: now);
            return new(ClientConfiguration.Failed, ExitCode: result.ExitCode, CheckedUtc: now);
        }
        return client == "claude"
            ? InspectClaude(result.Output, worker, directory, loopbackMcpEndpoint, now)
            : InspectCodex(result.Output, worker, directory, loopbackMcpEndpoint, now);
    }

    // The labels the reviewed public mcpGetHandler prints (Connect-ClaudeMcpEntry.mjs connectionFields). A
    // block that does not have exactly one of each label it needs is Unrecognized, never Configured.
    private static ClientInspection InspectClaude(string output, string worker, string directory, string endpoint, DateTimeOffset now)
    {
        var lines = output.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var headings = lines.Select((line, index) => (line, index)).Where(item => item.line == ServerName + ":").ToArray();
        if (headings.Length != 1) return new(ClientConfiguration.Unrecognized, CheckedUtc: now);
        var block = lines.Skip(headings[0].index + 1).TakeWhile(line => line.Length == 0 || line.StartsWith("  ", StringComparison.Ordinal)).ToArray();
        string? One(string label)
        {
            string prefix = "  " + label + ": ";
            var found = block.Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            return found.Length == 1 ? found[0][prefix.Length..] : null;
        }
        string? scopeLabel = One("Scope"), type = One("Type"), status = One("Status");
        string scope = scopeLabel is null ? "unrecognized"
            : scopeLabel.StartsWith("User config", StringComparison.Ordinal) ? "user"
            : scopeLabel.StartsWith("Project config", StringComparison.Ordinal) ? "project"
            : scopeLabel.StartsWith("Local config", StringComparison.Ordinal) ? "local"
            : scopeLabel.StartsWith("Enterprise config", StringComparison.Ordinal) || scopeLabel.StartsWith("Managed", StringComparison.Ordinal) ? "managed"
            : "unrecognized";
        string health = status is null ? "unknown"
            : System.Text.RegularExpressions.Regex.IsMatch(status, "^[\u2713\u2714\u221A] Connected$") ? "connected"
            : status == "! Connected \u00B7 tools fetch failed" ? "tools_unavailable"
            : status == "! Needs authentication" ? "needs_authentication"
            : System.Text.RegularExpressions.Regex.IsMatch(status, "^[\u2717\u2718\u00D7] Failed to connect$") ? "failed"
            : System.Text.RegularExpressions.Regex.IsMatch(status, "^[\u2717\u2718\u00D7] Connection error$") ? "connection_error"
            : status.StartsWith("\u23F8 Pending approval", StringComparison.Ordinal) ? "pending_approval"
            : System.Text.RegularExpressions.Regex.IsMatch(status, "^[\u2717\u2718\u00D7] Rejected ") ? "rejected"
            : status.StartsWith("\u2298 Disabled", StringComparison.Ordinal) ? "disabled"
            : "unknown";
        if (type is null) return new(ClientConfiguration.Unrecognized, scope, "", health, CheckedUtc: now);
        bool exact = type == "http" ? One("URL") == endpoint
            : type == "stdio" && string.Equals(One("Command"), worker, StringComparison.OrdinalIgnoreCase) &&
                One("Args") == "--mcp-stdio --data " + directory;
        string transport = type is "http" or "stdio" ? type : "other";
        var state = !exact ? ClientConfiguration.DifferentEntry
            : health is "rejected" or "disabled" ? ClientConfiguration.Disabled : ClientConfiguration.Configured;
        return new(state, scope, transport, health, CheckedUtc: now);
    }

    // Codex prints no scope; its JSON names the transport and whether the entry is enabled.
    private static ClientInspection InspectCodex(string json, string worker, string directory, string endpoint, DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var transport = root.GetProperty("transport");
            string? type = transport.GetProperty("type").GetString();
            bool exact = type == "stdio"
                ? MatchesCodex(json, worker, ["--mcp-stdio", "--data", directory])
                : type == "streamable_http" && transport.TryGetProperty("url", out var url) && url.GetString() == endpoint;
            string kind = type == "stdio" ? "stdio" : type == "streamable_http" ? "http" : "other";
            bool disabled = root.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False;
            var state = !exact ? ClientConfiguration.DifferentEntry : disabled ? ClientConfiguration.Disabled : ClientConfiguration.Configured;
            return new(state, "", kind, "", CheckedUtc: now);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        { return new(ClientConfiguration.Unrecognized, CheckedUtc: now); }
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
