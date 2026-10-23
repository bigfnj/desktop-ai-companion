using System.Collections;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using AgentBroker.Firewall;

namespace AgentBroker.Worker;

/// <summary>A fixed-purpose command, entered before any broker identity or data access.</summary>
public static class FirewallSetup
{
    public const string WorkerFileName = "AgentBroker.Worker.exe";
    public const string RuleGroup = "Desktop AI Companion AgentBroker";
    public const string RuleDescription = "AgentBroker LAN access v1: this worker, TCP port, Private profile, local subnet only.";

    public static int Run(string[] args)
    {
        FirewallSetupExitCode result;
        if (!FirewallSetupProtocol.TryParse(args, out int port)) result = FirewallSetupExitCode.InvalidArguments;
        else if (!OperatingSystem.IsWindows()) result = FirewallSetupExitCode.UnsupportedPlatform;
        else
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    result = FirewallSetupExitCode.AdministratorRequired;
                else if (Environment.ProcessPath is not { } path ||
                    !string.Equals(Path.GetFileName(path), WorkerFileName, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(path))
                    result = FirewallSetupExitCode.InvalidArguments;
                else
                {
                    using var backend = new WindowsFirewallPolicyBackend();
                    result = Apply(path, port, backend);
                }
            }
            catch (Exception error) when (error is COMException or UnauthorizedAccessException or
                System.Security.SecurityException or InvalidOperationException or ArgumentException or IOException or
                Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            { result = FailureCode(error); }
        }
        // No user paths, exception details, profile names or network addresses in the receipt.
        Console.WriteLine("agentbroker_firewall:" + result.ToString());
        return (int)result;
    }

    public static FirewallSetupExitCode Apply(string workerPath, int port, IFirewallPolicyBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        FirewallRuleState desired;
        try { desired = CreateRule(workerPath, port); }
        catch (ArgumentException) { return FirewallSetupExitCode.InvalidArguments; }
        try
        {
            if (backend.LocalPolicyModifyState != 0) return FirewallSetupExitCode.PolicyRestricted;
            if (backend.HasPotentialBlockingRule(desired)) return FirewallSetupExitCode.ExistingBlock;
            var existing = backend.FindRules(desired.Name);
            if (existing.Count == 1 && Matches(existing[0], desired)) return FirewallSetupExitCode.Allowed;
            // Never edit or replace an existing rule, including a stale rule with our own name.
            if (existing.Count != 0) return FirewallSetupExitCode.ConflictingRule;
            backend.Add(desired);
            var installed = backend.FindRules(desired.Name);
            if (installed.Count != 1 || !Matches(installed[0], desired)) return FirewallSetupExitCode.VerificationFailed;
            if (backend.HasPotentialBlockingRule(desired)) return FirewallSetupExitCode.ExistingBlock;
            return backend.LocalPolicyModifyState == 0
                ? FirewallSetupExitCode.Allowed : FirewallSetupExitCode.PolicyRestricted;
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or
            System.Security.SecurityException or InvalidOperationException or ArgumentException or
            Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        { return FailureCode(error); }
    }

    private static FirewallSetupExitCode FailureCode(Exception error) => error is UnauthorizedAccessException or
        System.Security.SecurityException || error is COMException { HResult: unchecked((int)0x80070005) }
        ? FirewallSetupExitCode.PolicyRestricted : FirewallSetupExitCode.Unavailable;

    public static FirewallRuleState CreateRule(string workerPath, int port)
    {
        if (string.IsNullOrWhiteSpace(workerPath) || !Path.IsPathFullyQualified(workerPath) ||
            !string.Equals(Path.GetFileName(workerPath), WorkerFileName, StringComparison.OrdinalIgnoreCase) ||
            port is < 1 or > 65535) throw new ArgumentException("A worker path and one TCP port are required.");
        string application = Path.GetFullPath(workerPath);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(application.ToUpperInvariant())))[..24];
        return new FirewallRuleState("Desktop AI Companion AgentBroker LAN " + key + " TCP " +
            port.ToString(CultureInfo.InvariantCulture), RuleDescription, RuleGroup, application, "",
            6, port.ToString(CultureInfo.InvariantCulture), "*", "*", "LocalSubnet", 1, "All", true, 2, false, 0, 1, false);
    }

    public static bool Matches(FirewallRuleState actual, FirewallRuleState desired) =>
        actual.Name == desired.Name && actual.Description == desired.Description && actual.Grouping == desired.Grouping &&
        SamePath(actual.ApplicationName, desired.ApplicationName) && string.IsNullOrEmpty(actual.ServiceName) &&
        actual.Protocol == 6 && SinglePort(actual.LocalPorts, desired.LocalPorts) && Token(actual.RemotePorts, "*") &&
        Token(actual.LocalAddresses, "*") && Token(actual.RemoteAddresses, "LocalSubnet") &&
        actual.Direction == 1 && Token(actual.InterfaceTypes, "All") && !actual.HasInterfaceRestriction &&
        actual.Enabled && actual.Profiles == 2 && !actual.EdgeTraversal && actual.EdgeTraversalOptions == 0 && actual.Action == 1;

    private static bool Token(string? value, string expected) =>
        string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static bool SinglePort(string? actual, string? expected) =>
        int.TryParse(actual?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) &&
        value.ToString(CultureInfo.InvariantCulture) == expected;

    private static bool SamePath(string actual, string desired)
    {
        try { return Path.IsPathFullyQualified(actual) && string.Equals(Path.GetFullPath(actual), desired, StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    // Windows block rules take precedence over an allow. This deliberately reports a possible
    // conflict even when a block has additional address/interface restrictions we do not evaluate.
    public static bool CouldBlock(FirewallRuleState rule, FirewallRuleState desired) =>
        rule.Enabled && rule.Action == 0 && rule.Direction == 1 && (rule.Profiles & 2) != 0 &&
        rule.Protocol is 6 or 256 && SamePath(rule.ApplicationName, desired.ApplicationName) &&
        (rule.Protocol == 256 || PortMayInclude(rule.LocalPorts, int.Parse(desired.LocalPorts!, CultureInfo.InvariantCulture)));

    private static bool PortMayInclude(string? ports, int requested)
    {
        if (string.IsNullOrWhiteSpace(ports) || ports.Trim() == "*") return true;
        foreach (string item in ports.Split(','))
        {
            string[] range = item.Trim().Split('-');
            if (range.Length == 1 && int.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out int single))
            { if (single == requested) return true; }
            else if (range.Length == 2 && int.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out int first) &&
                int.TryParse(range[1], NumberStyles.None, CultureInfo.InvariantCulture, out int last) && first <= last)
            { if (requested >= first && requested <= last) return true; }
            else return true;
        }
        return false;
    }
}

// Values are the INetFwRule COM constants, not the NetSecurity CIM enumeration values.
public sealed record FirewallRuleState(string Name, string Description, string Grouping, string ApplicationName,
    string? ServiceName, int Protocol, string? LocalPorts, string? RemotePorts, string? LocalAddresses,
    string? RemoteAddresses, int Direction, string? InterfaceTypes, bool Enabled, int Profiles,
    bool EdgeTraversal, int EdgeTraversalOptions, int Action, bool HasInterfaceRestriction);

public interface IFirewallPolicyBackend
{
    int LocalPolicyModifyState { get; }
    bool HasPotentialBlockingRule(FirewallRuleState desired);
    IReadOnlyList<FirewallRuleState> FindRules(string name);
    void Add(FirewallRuleState rule);
}

internal sealed class WindowsFirewallPolicyBackend : IFirewallPolicyBackend, IDisposable
{
    private readonly object policy;
    private readonly object rules;

    public WindowsFirewallPolicyBackend()
    {
        policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)
            ?? throw new InvalidOperationException("Firewall policy is unavailable.");
        try { rules = ((dynamic)policy).Rules; }
        catch { Release(policy); throw; }
    }

    public int LocalPolicyModifyState => (int)((dynamic)policy).LocalPolicyModifyState;

    public bool HasPotentialBlockingRule(FirewallRuleState desired)
    {
        foreach (object item in (IEnumerable)rules)
        {
            try
            {
                dynamic rule = item;
                int protocol = (int)rule.Protocol;
                if (!(bool)rule.Enabled || (int)rule.Action != 0 || (int)rule.Direction != 1 ||
                    ((int)rule.Profiles & 2) == 0 || protocol is not (6 or 256)) continue;
                var candidate = desired with { ApplicationName = (string?)rule.ApplicationName ?? "", Protocol = protocol,
                    LocalPorts = protocol == 6 ? (string?)rule.LocalPorts : null, Action = 0, Profiles = (int)rule.Profiles };
                if (FirewallSetup.CouldBlock(candidate, desired)) return true;
            }
            finally { Release(item); }
        }
        return false;
    }

    public IReadOnlyList<FirewallRuleState> FindRules(string name)
    {
        var result = new List<FirewallRuleState>();
        foreach (object item in (IEnumerable)rules)
        {
            try
            {
                dynamic rule = item;
                if (string.Equals((string)rule.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    int protocol = (int)rule.Protocol;
                    object? interfaces = rule.Interfaces;
                    bool restricted = interfaces is Array array ? array.Length != 0 : interfaces is not null;
                    result.Add(new FirewallRuleState((string)rule.Name, (string)rule.Description, (string)rule.Grouping,
                        (string?)rule.ApplicationName ?? "", (string?)rule.ServiceName, protocol,
                        protocol is 6 or 17 ? (string?)rule.LocalPorts : null,
                        protocol is 6 or 17 ? (string?)rule.RemotePorts : null, (string?)rule.LocalAddresses,
                        (string?)rule.RemoteAddresses, (int)rule.Direction, (string?)rule.InterfaceTypes,
                        (bool)rule.Enabled, (int)rule.Profiles, (bool)rule.EdgeTraversal,
                        (int)rule.EdgeTraversalOptions, (int)rule.Action, restricted));
                }
            }
            finally { Release(item); }
        }
        return result;
    }

    public void Add(FirewallRuleState state)
    {
        object value = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)
            ?? throw new InvalidOperationException("Firewall rule creation is unavailable.");
        try
        {
            dynamic rule = value;
            rule.Name = state.Name;
            rule.Description = state.Description;
            rule.Grouping = state.Grouping;
            rule.ApplicationName = state.ApplicationName;
            rule.Protocol = state.Protocol;
            rule.LocalPorts = state.LocalPorts;
            rule.RemotePorts = state.RemotePorts;
            rule.LocalAddresses = state.LocalAddresses;
            rule.RemoteAddresses = state.RemoteAddresses;
            rule.Direction = state.Direction;
            rule.InterfaceTypes = state.InterfaceTypes;
            rule.Enabled = state.Enabled;
            rule.Profiles = state.Profiles;
            rule.EdgeTraversal = state.EdgeTraversal;
            rule.EdgeTraversalOptions = state.EdgeTraversalOptions;
            rule.Action = state.Action;
            ((dynamic)rules).Add(rule);
        }
        finally { Release(value); }
    }

    public void Dispose() { Release(rules); Release(policy); }

    private static void Release(object value)
    { if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
