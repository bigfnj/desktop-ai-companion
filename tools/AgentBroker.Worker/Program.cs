using AgentBroker.Worker;
using System.Text.Json;

if (args.Length == 1 && args[0] == "--help")
{
    Console.WriteLine("AgentBroker.Worker --data <absolute directory> [--name <label>] [--loopback-port 7432] [--lan-port 7433] [--lan true|false] [--allow-unauthenticated-loopback true|false] [--parent-pid <PID> --parent-start <FILETIME>]");
    return 0;
}
try
{
    if (args.Length == 3 && args[0] == "--mcp-stdio" && args[1] == "--data")
        return await McpStdioBridge.RunAsync(args[2], Console.OpenStandardInput(), Console.OpenStandardOutput());
    var options = WorkerOptions.Parse(args);
    await using var worker = await WorkerApplication.CreateAsync(options);
    await worker.StartAsync();
    // No access token, certificate bytes, message body or local path is written to stdout.
    Console.WriteLine(JsonSerializer.Serialize(new { ready = true, brokerId = worker.Runtime.Store.BrokerId,
        endpoints = worker.Application.Urls.ToArray() }));
    await worker.Application.WaitForShutdownAsync();
    return 0;
}
catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
{ Console.Error.WriteLine("agentbroker: invalid_arguments"); return 2; }
catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or
    System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException)
{ Console.Error.WriteLine("agentbroker: startup_or_storage_unavailable"); return 73; }
catch (Exception) { Console.Error.WriteLine("agentbroker: unexpected_worker_failure"); return 1; }
