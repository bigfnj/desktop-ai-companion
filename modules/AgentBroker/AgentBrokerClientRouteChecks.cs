using System.Runtime.InteropServices;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    /// <summary>Lane feature/agentbroker-backlog-d: Claude Code's localhost route under a managed allowlist (N-bus-04) and
    /// the CLI locator client setup shares with the model calls (N-broker-a-01). A fake vendor CLI and empty files in a
    /// scratch tree: no CLI is started and no client configuration is read or written.</summary>
    private static void RunClientRouteChecks(Action<bool, string> check)
    {
        RunLocalhostRouteChecks(check);
        RunLocatorChecks(check);
    }

    private static void RunLocalhostRouteChecks(Action<bool, string> check)
    {
        const string literal = "http://127.0.0.1:7432/mcp", localhost = "http://localhost:7432/mcp", privateMarker = "SYNTHETIC_ROUTE_PRIVATE_VALUE";
        const string allowlistMiss = "Cannot add MCP server \"" + AgentClientSetup.ServerName + "\": not allowed by enterprise policy";
        string worker = typeof(AgentBrokerModule).Assembly.Location, data = Path.Combine(Path.GetTempPath(), "agentbroker-route-" + Guid.NewGuid().ToString("N"));
        var command = new RouteCommand();
        var setup = new AgentClientSetup(command);
        string[] Add(string url) => ["mcp", "add", "--transport", "http", "--scope", "user", AgentClientSetup.ServerName, url];
        string Configure(params ClientCommandResult[] adds)
        {
            command.Calls.Clear(); command.Adds.Clear();
            foreach (var add in adds) command.Adds.Enqueue(add);
            return setup.ConfigureAsync("claude", worker, data, CancellationToken.None, literal).GetAwaiter().GetResult();
        }

        string retried = Configure(new(1, privateMarker, allowlistMiss), new(0, "", ""));
        check(command.Calls.Count == 3 && command.Calls[1].Arguments.SequenceEqual(Add(literal)) && command.Calls[2].Arguments.SequenceEqual(Add(localhost)) &&
            retried.Contains("was added", StringComparison.Ordinal) && retried.Contains(localhost, StringComparison.Ordinal) &&
            !retried.Contains(privateMarker, StringComparison.Ordinal),
            "client route: a Claude allowlist miss on 127.0.0.1 is retried once with the localhost URL of the same port");
        string twice = Configure(new(1, privateMarker, allowlistMiss), new(1, privateMarker, allowlistMiss));
        check(command.Calls.Count == 3 && twice.Contains("allowlist " + localhost + " or " + literal, StringComparison.Ordinal) &&
            !twice.Contains("was added", StringComparison.Ordinal) && !twice.Contains(privateMarker, StringComparison.Ordinal),
            "client route: a second allowlist miss ends setup with guidance naming both local URLs, and no third add");
        string locked = Configure(new ClientCommandResult(9, privateMarker, "EPERM: configuration write failed"));
        bool lockedOnce = command.Calls.Count == 2 && !command.Calls.Any(call => call.Arguments.Contains(localhost));
        string denied = Configure(new ClientCommandResult(9, privateMarker, "Cannot add MCP server \"" + AgentClientSetup.ServerName + "\": server is explicitly blocked by enterprise policy"));
        check(lockedOnce && command.Calls.Count == 2 && !command.Calls.Any(call => call.Arguments.Contains(localhost)) &&
            locked.Contains("configuration locks", StringComparison.Ordinal) && denied.Contains("managed policy", StringComparison.Ordinal),
            "client route: a non-policy add failure and an explicit deny are never retried with localhost");
        command.Calls.Clear(); command.Adds.Clear(); command.Adds.Enqueue(new(1, privateMarker, allowlistMiss));
        setup.ConfigureAsync("claude", worker, data, CancellationToken.None).GetAwaiter().GetResult();
        check(command.Calls.Count == 2 && command.Calls[1].Arguments.Contains("stdio") && !command.Calls.Any(call => call.Arguments.Contains(localhost)),
            "WITNESS client route: a stdio allowlist miss tries no URL and no other transport");

        check(AgentClientSetup.IsCanonicalLoopbackEndpoint(literal) && AgentClientSetup.IsCanonicalLoopbackEndpoint(localhost) &&
            !new[] { "http://LOCALHOST:7432/mcp", "http://localhost/mcp", "https://localhost:7432/mcp", "http://localhost.evil.invalid:7432/mcp",
                "http://localhost:7432/mcp/", "http://[::1]:7432/mcp", "http://localhost:07432/mcp" }.Any(AgentClientSetup.IsCanonicalLoopbackEndpoint),
            "client route: exactly the literal and the localhost spellings are canonical loopback endpoints");
        string user = "User config (available in all your projects)";
        ClientInspection Inspect(string client, string output)
        {
            command.Calls.Clear(); command.Get = new(0, output, "");
            return setup.InspectAsync(client, worker, data, literal, DateTimeOffset.UnixEpoch, CancellationToken.None).GetAwaiter().GetResult();
        }
        string Claude(string url) => AgentClientSetup.ServerName + ":\n  Scope: " + user + "\n  Status: √ Connected\n  Type: http\n  URL: " + url + "\n";
        string Codex(string url) => "{\"transport\":{\"type\":\"streamable_http\",\"url\":\"" + url + "\"}}";
        check(Inspect("claude", Claude(localhost)) is { State: ClientConfiguration.Configured, Transport: "http" } &&
            Inspect("codex", Codex(localhost)).State == ClientConfiguration.Configured,
            "client route: an entry holding the localhost spelling of the broker's endpoint is the exact entry");
        check(Inspect("claude", Claude("http://localhost:7999/mcp")).State == ClientConfiguration.DifferentEntry &&
            Inspect("claude", Claude("http://LOCALHOST:7432/mcp")).State == ClientConfiguration.DifferentEntry &&
            Inspect("codex", Codex("http://localhost:7999/mcp")).State == ClientConfiguration.DifferentEntry,
            "client route: localhost for another port, or not in its canonical spelling, is a different entry");
        command.Get = null;
    }

    private static void RunLocatorChecks(Action<bool, string> check)
    {
        bool arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        string root = Path.Combine(Path.GetTempPath(), "agentbroker-locator-" + Guid.NewGuid().ToString("N"));
        string npm = Path.Combine(root, "npm");
        string claudeExe = Path.Combine(npm, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        string codexRoot = Path.Combine(npm, "node_modules", "@openai", "codex");
        string codexExe = Path.Combine(codexRoot, "node_modules", "@openai", arm ? "codex-win32-arm64" : "codex-win32-x64", "vendor",
            arm ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc", "bin", "codex.exe");
        static void Touch(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, ""); }
        try
        {
            foreach (string path in new[] { Path.Combine(npm, "claude.cmd"), Path.Combine(npm, "claude.ps1"), Path.Combine(npm, "codex.cmd"), claudeExe, codexExe })
                Touch(path);
            var pathOnly = new AgentCliEnvironment(PathValue: npm);
            var claude = AgentCliLocator.Locate("claude", pathOnly);
            var codex = AgentCliLocator.Locate("codex", pathOnly);
            check(string.Equals(claude?.Executable, claudeExe, StringComparison.OrdinalIgnoreCase) && claude?.NpmCodexPackageRoot is null &&
                string.Equals(codex?.Executable, codexExe, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(codex?.NpmCodexPackageRoot, codexRoot, StringComparison.OrdinalIgnoreCase),
                "cli locator: an npm install on PATH resolves to the binary its shim starts, never the .cmd shim");
            var start = AgentClientCommand.StartInfo(codex!, ["mcp", "get", AgentClientSetup.ServerName, "--json"]);
            check(!start.UseShellExecute && string.Equals(start.FileName, codexExe, StringComparison.OrdinalIgnoreCase) &&
                start.ArgumentList.SequenceEqual(new[] { "mcp", "get", AgentClientSetup.ServerName, "--json" }) &&
                start.Environment["CODEX_MANAGED_BY_NPM"] == "1" && start.Environment["CODEX_MANAGED_PACKAGE_ROOT"] == codexRoot,
                "cli locator: setup starts the located binary itself with typed arguments, as codex.js would, and no shell");
            check(AgentCliLocator.Locate("claude", new AgentCliEnvironment(AppData: root)) is { } fromAppData &&
                string.Equals(fromAppData.Executable, claudeExe, StringComparison.OrdinalIgnoreCase),
                "cli locator: with nothing on PATH, npm's default prefix under %APPDATA% is still found");
            string driveRelative = npm.Length > 2 && npm[1] == ':' ? npm[2..] : npm;
            check(AgentCliLocator.Locate("claude", new AgentCliEnvironment(PathValue: driveRelative)) is null,
                "cli locator: a PATH entry that is not a drive-qualified local path is never trusted, even naming a real install");
            string direct = Path.Combine(root, "direct");
            Touch(Path.Combine(direct, "claude.exe"));
            check(AgentCliLocator.Locate("claude", new AgentCliEnvironment(PathValue: direct + Path.PathSeparator + npm)) is { } first &&
                string.Equals(first.Executable, Path.Combine(direct, "claude.exe"), StringComparison.OrdinalIgnoreCase),
                "WITNESS cli locator: the first PATH folder holding the CLI wins, as in a terminal");
            string home = Path.Combine(root, "home");
            Touch(Path.Combine(home, ".local", "bin", "claude.exe"));
            Touch(Path.Combine(home, ".local", "bin", "codex.exe"));
            check(AgentCliLocator.Locate("claude", new AgentCliEnvironment(UserProfile: home)) is { } native &&
                string.Equals(native.Executable, Path.Combine(home, ".local", "bin", "claude.exe"), StringComparison.OrdinalIgnoreCase) &&
                AgentCliLocator.Locate("codex", new AgentCliEnvironment(UserProfile: home)) is null,
                "cli locator: Claude Code's own installer is found, and that folder is not a Codex install");
            string local = Path.Combine(root, "local");
            string wingetCodex = Path.Combine(local, "Microsoft", "WinGet", "Packages", AgentCliLocator.CodexWinGetId + "_Microsoft.Winget.Source_8wekyb3d8bbwe",
                "codex-x86_64-pc-windows-msvc.exe");
            Touch(wingetCodex);
            string older = Path.Combine(home, ".vscode", "extensions", "anthropic.claude-code-2.1.9-" + (arm ? "win32-arm64" : "win32-x64"), "resources", "native-binary", "claude.exe");
            string newer = Path.Combine(home, ".vscode", "extensions", "anthropic.claude-code-2.1.10-" + (arm ? "win32-arm64" : "win32-x64"), "resources", "native-binary", "claude.exe");
            Touch(older); Touch(newer);
            File.Delete(Path.Combine(home, ".local", "bin", "claude.exe"));
            check(AgentCliLocator.Locate("codex", new AgentCliEnvironment(LocalAppData: local)) is { } winget &&
                string.Equals(winget.Executable, wingetCodex, StringComparison.OrdinalIgnoreCase) &&
                AgentCliLocator.Locate("claude", new AgentCliEnvironment(UserProfile: home)) is { } extension &&
                string.Equals(extension.Executable, newer, StringComparison.OrdinalIgnoreCase),
                "cli locator: a WinGet package folder and the newest VS Code extension copy are found where nothing else is installed");
            File.Delete(claudeExe);
            check(AgentCliLocator.Locate("claude", pathOnly) is null,
                "cli locator: a shim whose binary is missing is not an install");

            // Not located is CLI not found, before any process could start: the fake environment holds nothing.
            var missing = new AgentClientSetup(new AgentClientCommand(() => new AgentCliEnvironment(PathValue: Path.Combine(root, "empty"))));
            string worker = typeof(AgentBrokerModule).Assembly.Location;
            check(missing.InspectAsync("claude", worker, root, "http://127.0.0.1:7432/mcp", DateTimeOffset.UnixEpoch, CancellationToken.None)
                    .GetAwaiter().GetResult().State == ClientConfiguration.CliMissing &&
                missing.InspectAsync("codex", worker, root, "http://127.0.0.1:7432/mcp", DateTimeOffset.UnixEpoch, CancellationToken.None)
                    .GetAwaiter().GetResult().State == ClientConfiguration.CliMissing,
                "cli locator: a client CLI the locator cannot find is not found, and nothing is started");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private sealed class RouteCommand : IAgentClientCommand
    {
        internal readonly Queue<ClientCommandResult> Adds = new();
        internal ClientCommandResult? Get;
        internal readonly List<(string Executable, string[] Arguments)> Calls = [];
        public Task<ClientCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            Calls.Add((executable, arguments.ToArray()));
            if (arguments[1] == "get")
                return Task.FromResult(Get ?? new ClientCommandResult(1, "", "No MCP server named " + AgentClientSetup.ServerName + " found."));
            return Task.FromResult(Adds.Count > 0 ? Adds.Dequeue() : new ClientCommandResult(0, "", ""));
        }
    }
}
