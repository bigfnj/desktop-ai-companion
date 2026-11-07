using AgentBroker.Worker;
using System.Text.Json;

if (args.Length > 0 && args[0] == AgentBroker.Firewall.FirewallSetupProtocol.Command)
    return FirewallSetup.Run(args);

if (args.Length == 1 && args[0] == "--help")
{
    Console.WriteLine("AgentBroker.Worker --data <absolute directory> [--name <label>] [--loopback-port 7432] [--lan-port 7433] [--lan true|false] [--allow-unauthenticated-loopback true|false] [--parent-pid <PID> --parent-start <FILETIME>]");
    Console.WriteLine("AgentBroker.Worker --allow-lan-access --port <1..65535> (explicit elevated Private/local-subnet firewall setup)");
    Console.WriteLine("AgentBroker.Worker --mcp-channel --data <absolute directory> [--label <text>] [--private] (opt-in Claude Code channel over stdio)");
    Console.WriteLine("AgentBroker.Worker --mcp-call --data <absolute directory> --request-file <absolute JSON file> (one decoded result; request: tool and arguments)");
    Console.WriteLine("AgentBroker.Worker --mcp-call --data <absolute directory> --list-tools");
    Console.WriteLine("AgentBroker.Worker --claude-title-hook --data <absolute directory> (opt-in documented custom-title hook; never submits model input)");
    Console.WriteLine("MCP client exits: 0 success, 2 invalid request, 10 tool error, 11 RPC error, 12 HTTP failure, 69 transport unconfirmed. Never retries a submission.");
    return 0;
}
try
{
    if (args.Length > 0 && args[0] == ClaudeTitleHook.Command)
        return await ClaudeTitleHook.RunCommandAsync(args, Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.OpenStandardError());
    if (args.Length > 0 && args[0] == McpCallClient.Command)
        return await McpCallClient.RunCommandAsync(args, Console.OpenStandardOutput());
    if (args.Length == 3 && args[0] == "--mcp-stdio" && args[1] == "--data")
        return await McpStdioBridge.RunAsync(args[2], Console.OpenStandardInput(), Console.OpenStandardOutput());
    // Opt-in only: nothing configures this mode. Claude Code starts it when the owner adds it as an MCP server
    // and names it in --dangerously-load-development-channels for an interactive session.
    if (args.Length > 0 && args[0] == ChannelBridgeOptions.Command)
        return await McpChannelBridge.RunAsync(ChannelBridgeOptions.Parse(args), Console.OpenStandardInput(), Console.OpenStandardOutput());
    var options = WorkerOptions.Parse(args);
    await using var worker = await WorkerApplication.CreateAsync(options);
    await worker.StartAsync();
    // No access token, certificate bytes, message body or local path is written to stdout.
    Console.WriteLine(JsonSerializer.Serialize(new { ready = true, brokerId = worker.Runtime.Store.BrokerId,
        endpoints = worker.Application.Urls.ToArray() }));
    return await WorkerLifetime.WaitForShutdownAsync(worker, Console.Error);
}
catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
{ Console.Error.WriteLine("agentbroker: invalid_arguments"); return 2; }
catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or
    System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException)
{ Console.Error.WriteLine("agentbroker: startup_or_storage_unavailable"); return 73; }
catch (Exception) { Console.Error.WriteLine("agentbroker: unexpected_worker_failure"); return 1; }
