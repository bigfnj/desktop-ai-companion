using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentBroker.Worker;

public sealed record WorkerOptions(string DataDirectory, string Name, int LoopbackPort = 7432,
    int LanPort = 7433, bool EnableLan = true, bool AllowUnauthenticatedLoopback = true,
    int ParentPid = 0, long ParentCreationFileTime = 0)
{
    public bool EnableCodexNativeDiscovery { get; init; } = true;
    public bool EnableCodexPeerMessages { get; init; }
    public bool EnableClaudeNativeDiscovery { get; init; } = true;
    public bool EnableClaudePeerCredentials { get; init; }
    public System.Net.IPAddress LanListenAddress { get; init; } = System.Net.IPAddress.Any;
    public bool EnableBrokerDiscovery { get; init; } = true;
    public int DefaultMessageLifetimeMinutes { get; init; } = 1440;
    public string ControlPipeName => AgentBroker.Protocol.ControlProtocol.PipeName(DataDirectory);

    public void Validate()
    {
        if (!Path.IsPathFullyQualified(DataDirectory) || string.IsNullOrWhiteSpace(Name) || Name.Length > 80 || Name.Any(char.IsControl) ||
            LoopbackPort is < 0 or > 65535 || LanPort is < 0 or > 65535 ||
            (EnableLan && LanPort != 0 && LanPort == LoopbackPort) ||
            ParentPid < 0 || (ParentPid > 0 && ParentCreationFileTime <= 0) || DefaultMessageLifetimeMinutes is < 1 or > 1440)
            throw new ArgumentException("Invalid broker options.");
    }

    public static WorkerOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--data" or "--name" or "--loopback-port" or "--lan-port" or
                "--lan" or "--allow-unauthenticated-loopback" or "--parent-pid" or "--parent-start" or
                "--codex-discovery" or "--codex-peer-messages" or "--claude-discovery" or "--claude-peer-credentials" or
                "--message-lifetime-minutes") ||
                !values.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Invalid broker arguments.");
        }
        if (!values.TryGetValue("--data", out var directory)) throw new ArgumentException("--data is required.");
        var result = new WorkerOptions(directory, values.GetValueOrDefault("--name", Environment.MachineName),
            Int("--loopback-port", 7432), Int("--lan-port", 7433), Bool("--lan", true),
            Bool("--allow-unauthenticated-loopback", true), Int("--parent-pid", 0),
            long.Parse(values.GetValueOrDefault("--parent-start", "0"), CultureInfo.InvariantCulture))
        {
            EnableCodexNativeDiscovery = Bool("--codex-discovery", true),
            EnableCodexPeerMessages = Bool("--codex-peer-messages", false),
            EnableClaudeNativeDiscovery = Bool("--claude-discovery", true),
            EnableClaudePeerCredentials = Bool("--claude-peer-credentials", false),
            DefaultMessageLifetimeMinutes = Int("--message-lifetime-minutes", 1440),
        };
        result.Validate(); return result;

        int Int(string key, int fallback) => int.Parse(values.GetValueOrDefault(key, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        bool Bool(string key, bool fallback) => bool.Parse(values.GetValueOrDefault(key, fallback.ToString(CultureInfo.InvariantCulture)));
    }
}
