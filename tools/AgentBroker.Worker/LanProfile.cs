using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using AgentBroker.Protocol;
using AgentBroker.Worker.Security;

namespace AgentBroker.Worker;

/// <summary>Read-only operating-system observations behind <see cref="LanProfileMonitor"/>.</summary>
public interface ILanProfileProbe
{
    /// <summary>One fixed category code for the interface that carries <paramref name="address"/>.</summary>
    string Category(IPAddress address);
    /// <summary>This worker's own LAN rule, as one fixed state code and the rule's profile bitmask.</summary>
    (string State, int Profiles) Rule(string workerPath, int port);
}

/// <summary>
/// Separates "the rule is configured" from "a LAN request actually arrived", and reports the network
/// category Windows applies to the interface carrying the broker's advertised address. Never changes
/// a network profile or a firewall rule: the probe only reads, and the category it reads cannot be set
/// through the interfaces declared below.
/// </summary>
public sealed class LanProfileMonitor(ILanProfileProbe probe, TimeProvider? clock = null)
{
    // The firewall read enumerates every Windows rule, so status polls (every two seconds) reuse one
    // reading for this long. The inbound observation is never cached; it is a timestamp.
    public static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(15);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private (IPAddress Address, string Worker, int Port)? key;
    private LanProfileView? reading;
    private Task refresh = Task.CompletedTask;
    private int refreshing;
    private long lastInboundTicks;

    /// <summary>Returns the latest completed reading for this exact address, worker and port, starting at
    /// most one background refresh when it is missing or older than <see cref="MaximumAge"/>. Until a
    /// reading exists the codes are "pending"; the control pipe never waits for COM or the rule list.</summary>
    public LanProfileView Snapshot(IPAddress address, string workerPath, int port)
    {
        var requested = (address, workerPath, port);
        LanProfileView? current;
        bool stale;
        lock (gate)
        {
            // Value equality: IPAddress has no == operator, and a fresh instance arrives with every poll.
            if (!Nullable.Equals(key, requested)) { key = requested; reading = null; }
            current = reading;
            stale = current?.CheckedUtc is not { } checkedUtc || time.GetUtcNow() - checkedUtc >= MaximumAge;
        }
        if (stale && Interlocked.CompareExchange(ref refreshing, 1, 0) == 0)
            lock (gate) refresh = Task.Run(() => Refresh(requested));
        DateTimeOffset? inbound = LastInboundUtc;
        return current is null ? new("pending", "pending", 0, null, inbound) : current with { LastInboundUtc = inbound };
    }

    /// <summary>Completes when the refresh started by the last <see cref="Snapshot"/> has finished.</summary>
    public Task WhenRefreshed() { lock (gate) return refresh; }

    public DateTimeOffset? LastInboundUtc
    {
        get
        {
            long ticks = Interlocked.Read(ref lastInboundTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Records an HTTPS request that reached the LAN listener from another host. A connection
    /// from this PC to its own LAN address arrives with the same local and remote address and proves
    /// nothing about Windows Firewall, so it is not evidence; nor is loopback or a public address.</summary>
    public void ObserveInbound(IPAddress? remote, IPAddress? local)
    {
        if (remote is null || !NetworkPolicy.PrivateV4(remote, allowLoopback: false)) return;
        if (local is not null && NetworkPolicy.Normalize(local).Equals(NetworkPolicy.Normalize(remote))) return;
        Interlocked.Exchange(ref lastInboundTicks, time.GetUtcNow().UtcTicks);
    }

    private void Refresh((IPAddress Address, string Worker, int Port) requested)
    {
        try
        {
            string category;
            (string State, int Profiles) rule;
            try { category = probe.Category(requested.Address); }
            catch (Exception error) when (error is COMException or InvalidOperationException or NetworkInformationException or
                InvalidCastException or UnauthorizedAccessException) { category = "unavailable"; }
            try { rule = probe.Rule(requested.Worker, requested.Port); }
            catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException or
                InvalidCastException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            { rule = ("unavailable", 0); }
            var completed = new LanProfileView(KnownCategory(category), KnownRule(rule.State), rule.Profiles, time.GetUtcNow(), null);
            lock (gate) if (Nullable.Equals(key, requested)) reading = completed;
        }
        finally { Interlocked.Exchange(ref refreshing, 0); }
    }

    private static string KnownCategory(string value) => value is "public" or "private" or "domain" or "unknown" or
        "interface_not_found" or "unavailable" ? value : "unknown";
    private static string KnownRule(string value) => value is "configured" or "absent" or "conflicting" or "unavailable"
        ? value : "unavailable";

    /// <summary>Pure resolution, shared by the Windows probe and the tests: the interface that carries the
    /// address, then the network connections on that adapter. Several connections on one adapter resolve
    /// to the most restrictive category, because that is the profile Windows Firewall applies to it.</summary>
    public static string ResolveCategory(IPAddress address,
        IEnumerable<(string InterfaceId, IReadOnlyList<IPAddress> Addresses)> interfaces,
        IEnumerable<(Guid AdapterId, int Category)> connections)
    {
        var wanted = NetworkPolicy.Normalize(address);
        var carrier = interfaces.Where(item => item.Addresses.Any(candidate => NetworkPolicy.Normalize(candidate).Equals(wanted)))
            .Select(item => Guid.TryParse(item.InterfaceId, out var id) ? id : Guid.Empty).Take(2).ToArray();
        if (carrier.Length != 1 || carrier[0] == Guid.Empty) return "interface_not_found";
        var categories = connections.Where(item => item.AdapterId == carrier[0]).Select(item => item.Category).ToArray();
        if (categories.Length == 0 || categories.Any(value => value is < 0 or > 2)) return "unknown";
        // NLM_NETWORK_CATEGORY: 0 Public, 1 Private, 2 DomainAuthenticated.
        return categories.Min() switch { 0 => "public", 1 => "private", _ => "domain" };
    }

    /// <summary>Pure rule classification: exactly the rule Allow LAN access creates, no rule with this
    /// worker's rule name, or something else under that name (never edited here or by setup).</summary>
    public static (string State, int Profiles) ClassifyRule(IReadOnlyList<FirewallRuleState> found, FirewallRuleState desired)
    {
        if (found.Count == 0) return ("absent", 0);
        int profiles = found.Aggregate(0, (all, rule) => all | rule.Profiles);
        return found.Count == 1 && FirewallSetup.Matches(found[0], desired) ? ("configured", profiles) : ("conflicting", profiles);
    }
}

/// <summary>Windows Network List Manager and Windows Firewall reads. Neither needs administrator rights.</summary>
public sealed class WindowsLanProfileProbe : ILanProfileProbe
{
    public string Category(IPAddress address)
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Select(item => (item.Id, (IReadOnlyList<IPAddress>)item.GetIPProperties().UnicastAddresses.Select(unicast => unicast.Address).ToArray()))
            .ToArray();
        var manager = (INetworkListManager)(Activator.CreateInstance(Type.GetTypeFromCLSID(NetworkListManagerClass, throwOnError: true)!)
            ?? throw new InvalidOperationException("Network List Manager is unavailable."));
        var connections = new List<(Guid, int)>();
        IEnumNetworkConnections? enumerator = null;
        try
        {
            enumerator = manager.GetNetworkConnections();
            for (int index = 0; index < 64 && enumerator.Next(1, out var connection, out uint fetched) == 0 && fetched == 1; index++)
            {
                INetwork? network = null;
                try
                {
                    network = connection.GetNetwork();
                    connections.Add((connection.GetAdapterId(), network.GetCategory()));
                }
                finally { Release(network); Release(connection); }
            }
        }
        finally { Release(enumerator); Release(manager); }
        return LanProfileMonitor.ResolveCategory(address, interfaces, connections);
    }

    public (string State, int Profiles) Rule(string workerPath, int port)
    {
        var desired = FirewallSetup.CreateRule(workerPath, port);
        using var backend = new WindowsFirewallPolicyBackend();
        return LanProfileMonitor.ClassifyRule(backend.FindRules(desired.Name), desired);
    }

    private static void Release(object? value)
    { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

    private static readonly Guid NetworkListManagerClass = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    // Vtable layouts from the Windows SDK's netlistmgr.h. Only the slots up to the last read method are
    // declared. INetwork stops at GetCategory, so SetCategory, which follows it, is not callable from here;
    // the two Set methods before it are placeholders that keep the slot order and are never invoked.
    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworks(int flags);
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetwork(Guid networkId);
        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        [return: MarshalAs(UnmanagedType.Interface)] object NewEnum();
        [PreserveSig] int Next(uint count, out INetworkConnection connection, out uint fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        INetwork GetNetwork();
        short IsConnectedToInternet();
        short IsConnected();
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
        void SlotSetNameNeverCalled([MarshalAs(UnmanagedType.BStr)] string name);
        [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
        void SlotSetDescriptionNeverCalled([MarshalAs(UnmanagedType.BStr)] string description);
        Guid GetNetworkId();
        int GetDomainType();
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworkConnections();
        void GetTimeCreatedAndConnected(out uint createdLow, out uint createdHigh, out uint connectedLow, out uint connectedHigh);
        short IsConnectedToInternet();
        short IsConnected();
        int GetConnectivity();
        int GetCategory();
    }
}
