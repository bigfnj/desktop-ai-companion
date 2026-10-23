using AgentBroker.Firewall;
using AgentBroker.Worker;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal static class FirewallSetupChecks
{
    private static readonly string WorkerPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "synthetic-worker", FirewallSetup.WorkerFileName));

    internal static void Run(Action<bool, string> check, string? only = null)
    {
        string assembly = typeof(FirewallSetup).Assembly.Location;
        Console.WriteLine("FIREWALL_WITNESS worker_sha256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))) +
            " worker_modified_utc=" + File.GetLastWriteTimeUtc(assembly).ToString("O"));
        var cases = new (string Name, Action Run)[]
        {
            ("scope", () => Scope(check)), ("arguments", () => Arguments(check)),
            ("conflict", () => Conflict(check)), ("readback", () => Readback(check)),
            ("policy", () => Policy(check)), ("blocks", () => Blocks(check)),
        };
        if (only is not null && !cases.Any(item => item.Name == only)) throw new ArgumentException("Unknown firewall test case.");
        foreach (var item in cases) if (only is null || only == item.Name) item.Run();
    }

    private static void Scope(Action<bool, string> check)
    {
        var desired = FirewallSetup.CreateRule(WorkerPath, 7433);
        check(desired.RemoteAddresses == "LocalSubnet", "firewall scope: only the local subnet is allowed");
        check(desired.ApplicationName == WorkerPath && desired.LocalPorts == "7433", "firewall scope: exact worker executable and configured TCP port");
        check(desired.Profiles == 2 && desired.Protocol == 6 && desired.Direction == 1 && desired.Action == 1,
            "firewall scope: COM Private TCP Inbound Allow constants are explicit");
        check(desired.Enabled && !desired.EdgeTraversal && desired.EdgeTraversalOptions == 0,
            "firewall scope: enabled LAN access never enables edge traversal");
        check(desired.ServiceName == "" && desired.InterfaceTypes == "All" && !desired.HasInterfaceRestriction &&
            desired.LocalAddresses == "*" && desired.RemotePorts == "*", "firewall scope: no hidden service or interface restriction");
        check(FirewallSetup.CreateRule(WorkerPath.ToUpperInvariant(), 7433).Name == desired.Name,
            "firewall ownership: equivalent Windows path casing has one stable rule name");
        check(FirewallSetup.CreateRule(WorkerPath, 7434).Name != desired.Name &&
            FirewallSetup.CreateRule(Path.Combine(Path.GetDirectoryName(WorkerPath)!, "other", FirewallSetup.WorkerFileName), 7433).Name != desired.Name,
            "firewall ownership: another port or installation has a different owned rule name");
        check(FirewallSetup.Matches(desired with { ApplicationName = WorkerPath.ToUpperInvariant(), ServiceName = null,
            LocalPorts = "07433", RemoteAddresses = " localsubnet ", InterfaceTypes = "all" }, desired),
            "firewall readback: equivalent COM path, token and numeric port forms normalize");
        var backend = new FakeBackend();
        check(FirewallSetup.Apply(WorkerPath, 7433, backend) == FirewallSetupExitCode.Allowed && backend.AddCalls == 1 &&
            backend.Stored.Single() == desired, "firewall create: exactly one fixed-scope rule is added and read back");
        check(FirewallSetup.Apply(WorkerPath, 7433, backend) == FirewallSetupExitCode.Allowed && backend.AddCalls == 1,
            "firewall create: an already matching owned rule is idempotent");
    }

    private static void Arguments(Action<bool, string> check)
    {
        foreach (int port in new[] { 1, 7433, 65535 })
            check(FirewallSetupProtocol.TryParse(FirewallSetupProtocol.Arguments(port).Split(' '), out int parsed) && parsed == port,
                "firewall arguments: one bounded port round-trips without shell input");
        string[][] invalid = [[], ["--allow-lan-access"], ["--allow-lan-access", "--port"],
            ["--allow-lan-access", "--port", "0"], ["--allow-lan-access", "--port", "65536"],
            ["--allow-lan-access", "--port", "-1"], ["--allow-lan-access", "--port", "+7433"],
            ["--allow-lan-access", "--port", " 7433"], ["--allow-lan-access", "--port", "7433;other"],
            ["--allow-lan-access", "--port", "7433", "--data", "ignored"],
            ["--allow-lan-access", "--program", WorkerPath], ["--port", "7433", "--allow-lan-access"],
            ["--allow-lan-access", "--port", "7433-7434"]];
        foreach (var args in invalid)
            check(!FirewallSetupProtocol.TryParse(args, out _), "firewall arguments: malformed or expanded authority is refused before policy access");
        foreach (string path in new[] { "relative\\AgentBroker.Worker.exe", Path.Combine(AppContext.BaseDirectory, "other.exe") })
        {
            var backend = new FakeBackend();
            check(FirewallSetup.Apply(path, 7433, backend) == FirewallSetupExitCode.InvalidArguments && backend.PolicyReads == 0,
                "firewall arguments: relative paths and another executable never reach the backend");
        }
        foreach (int port in new[] { 0, 65536 })
        {
            var backend = new FakeBackend();
            check(FirewallSetup.Apply(WorkerPath, port, backend) == FirewallSetupExitCode.InvalidArguments && backend.PolicyReads == 0,
                "firewall arguments: invalid programmatic ports never reach the backend");
        }
    }

    private static void Conflict(Action<bool, string> check)
    {
        var desired = FirewallSetup.CreateRule(WorkerPath, 7433);
        var backend = new FakeBackend { Stored = [desired with { RemoteAddresses = "*" }] };
        check(FirewallSetup.Apply(WorkerPath, 7433, backend) == FirewallSetupExitCode.ConflictingRule && backend.AddCalls == 0,
            "firewall conflict: an existing widened owned rule is refused without replacement");
        var mutations = new Func<FirewallRuleState, FirewallRuleState>[]
        {
            rule => rule with { Name = rule.Name.ToUpperInvariant() }, rule => rule with { Description = "unrelated" },
            rule => rule with { Grouping = "other" }, rule => rule with { ApplicationName = Path.Combine(AppContext.BaseDirectory, "other.exe") },
            rule => rule with { ServiceName = "other" }, rule => rule with { Protocol = 17 },
            rule => rule with { LocalPorts = "7433,7434" }, rule => rule with { LocalPorts = "7430-7440" },
            rule => rule with { RemotePorts = "443" }, rule => rule with { LocalAddresses = "127.0.0.1" },
            rule => rule with { RemoteAddresses = "LocalSubnet,Internet" }, rule => rule with { Direction = 2 },
            rule => rule with { InterfaceTypes = "LAN" }, rule => rule with { HasInterfaceRestriction = true },
            rule => rule with { Enabled = false }, rule => rule with { Profiles = 6 }, rule => rule with { Profiles = 3 },
            rule => rule with { EdgeTraversal = true }, rule => rule with { EdgeTraversalOptions = 1 }, rule => rule with { Action = 2 },
        };
        foreach (var mutate in mutations)
        {
            backend = new FakeBackend { Stored = [mutate(desired)] };
            check(FirewallSetup.Apply(WorkerPath, 7433, backend) == FirewallSetupExitCode.ConflictingRule && backend.AddCalls == 0,
                "firewall conflict: every owned rule field is verified without changing existing rules");
        }
        backend = new FakeBackend { Stored = [desired, desired] };
        check(FirewallSetup.Apply(WorkerPath, 7433, backend) == FirewallSetupExitCode.ConflictingRule && backend.AddCalls == 0,
            "firewall conflict: duplicate owned rule names are refused");
    }

    private static void Readback(Action<bool, string> check)
    {
        var backend = new FakeBackend { TransformAdd = rule => rule with { RemoteAddresses = "*" } };
        check(FirewallSetup.Apply(WorkerPath, 7433, backend) == FirewallSetupExitCode.VerificationFailed,
            "firewall readback: a widened installed remote scope never reports success");
        backend = new FakeBackend { OmitAdd = true };
        check(FirewallSetup.Apply(WorkerPath, 7433, backend) == FirewallSetupExitCode.VerificationFailed,
            "firewall readback: a missing installed rule never reports success");
        backend = new FakeBackend { DuplicateAdd = true };
        check(FirewallSetup.Apply(WorkerPath, 7433, backend) == FirewallSetupExitCode.VerificationFailed,
            "firewall readback: duplicate rules after creation never report success");
    }

    private static void Policy(Action<bool, string> check)
    {
        foreach (int state in new[] { 1, 2, 99 })
        {
            var backend = new FakeBackend { PolicyState = state };
            check(FirewallSetup.Apply(WorkerPath, 7433, backend) == FirewallSetupExitCode.PolicyRestricted && backend.AddCalls == 0,
                "firewall policy: Group Policy, inbound blocking and unknown restrictions never report an effective allowance");
        }
        var changed = new FakeBackend { PolicyAfterAdd = 1 };
        check(FirewallSetup.Apply(WorkerPath, 7433, changed) == FirewallSetupExitCode.PolicyRestricted && changed.AddCalls == 1,
            "firewall policy: policy changes during setup are reported after readback");
        foreach (Exception failure in new Exception[] { new UnauthorizedAccessException(), new COMException("synthetic", unchecked((int)0x80070005)) })
        {
            var backend = new FakeBackend { AddFailure = failure };
            check(FirewallSetup.Apply(WorkerPath, 7433, backend) == FirewallSetupExitCode.PolicyRestricted,
                "firewall policy: denied policy writes produce a truthful restriction result");
        }
        check(FirewallSetup.Apply(WorkerPath, 7433, new FakeBackend { AddFailure = new COMException("synthetic") }) == FirewallSetupExitCode.Unavailable,
            "firewall policy: an unavailable COM backend never reports success");
    }

    private static void Blocks(Action<bool, string> check)
    {
        var desired = FirewallSetup.CreateRule(WorkerPath, 7433);
        var block = desired with { Name = "Synthetic Windows prompt denial", Action = 0 };
        foreach (string? ports in new string?[] { "7433", "*", "7430-7440", "80,7433,443", "", null, "RPC" })
            check(FirewallSetup.CouldBlock(block with { LocalPorts = ports }, desired),
                "firewall block: exact-worker Private TCP wildcard, range and uncertain port blocks are reported");
        check(FirewallSetup.CouldBlock(block with { Protocol = 256, LocalPorts = null, Profiles = 7 }, desired),
            "firewall block: an Any-protocol block including Private is reported");
        foreach (var harmless in new[] { block with { Enabled = false }, block with { Action = 1 }, block with { Direction = 2 },
            block with { Profiles = 4 }, block with { Protocol = 17 }, block with { LocalPorts = "80,443,7440-7450" },
            block with { ApplicationName = Path.Combine(AppContext.BaseDirectory, "other.exe") } })
            check(!FirewallSetup.CouldBlock(harmless, desired), "firewall block: unrelated applications, profiles, directions, ports and protocols are excluded");
        var backend = new FakeBackend { Blocks = [block] };
        check(FirewallSetup.Apply(WorkerPath, 7433, backend) == FirewallSetupExitCode.ExistingBlock && backend.AddCalls == 0,
            "firewall block: a Windows prompt denial is reported without adding an ineffective allow");
        backend = new FakeBackend { AddBlockAfterAdd = true };
        check(FirewallSetup.Apply(WorkerPath, 7433, backend) == FirewallSetupExitCode.ExistingBlock,
            "firewall block: a racing block is detected before success is reported");
    }

    private sealed class FakeBackend : IFirewallPolicyBackend
    {
        internal List<FirewallRuleState> Stored { get; set; } = [];
        internal List<FirewallRuleState> Blocks { get; init; } = [];
        internal Func<FirewallRuleState, FirewallRuleState>? TransformAdd { get; init; }
        internal bool OmitAdd { get; init; }
        internal bool DuplicateAdd { get; init; }
        internal bool AddBlockAfterAdd { get; init; }
        internal int PolicyState { get; init; }
        internal int? PolicyAfterAdd { get; init; }
        internal Exception? AddFailure { get; init; }
        internal int AddCalls { get; private set; }
        internal int PolicyReads { get; private set; }
        public int LocalPolicyModifyState { get { PolicyReads++; return AddCalls > 0 ? PolicyAfterAdd ?? PolicyState : PolicyState; } }
        public bool HasPotentialBlockingRule(FirewallRuleState desired) =>
            (AddBlockAfterAdd && AddCalls > 0) || Blocks.Any(rule => FirewallSetup.CouldBlock(rule, desired));
        public IReadOnlyList<FirewallRuleState> FindRules(string name) => Stored.ToArray();
        public void Add(FirewallRuleState rule)
        {
            AddCalls++;
            if (AddFailure is not null) throw AddFailure;
            if (!OmitAdd) Stored.Add(TransformAdd?.Invoke(rule) ?? rule);
            if (DuplicateAdd) Stored.Add(rule);
        }
    }
}
