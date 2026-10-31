using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentBroker.Worker;

/// <summary>One process in a caller's ancestry. The creation time keeps a reused process ID from matching a session.</summary>
public sealed record CallerProcess(int ProcessId, ulong StartFileTime, string ImageName);

/// <summary>A caller's ancestry, and whether an empty one is because the connection's owner was found but Windows denied
/// even a limited query of it (N-broker-elevated-caller-01): some elevated launches, or a process running as another user,
/// seen from this Medium-integrity broker. Other elevated launches allow the limited query and deny only the full one; that
/// shape is QueryDenied's.</summary>
public sealed record CallerResolution(IReadOnlyList<CallerProcess> Chain, bool OwnerAccessDenied = false);

/// <summary>Finds the process that opened a loopback connection, for whoami. Read-only: it opens processes only to query
/// them (limited access, plus a full-query probe of the nearest agent that is closed at once) and reads no memory, command
/// line or token.</summary>
public interface ICallerProcessResolver
{
    /// <summary>The process owning the client end of a loopback TCP connection, then its parents, nearest first. Empty when
    /// the owner cannot be found.</summary>
    IReadOnlyList<CallerProcess> Resolve(IPAddress clientAddress, int clientPort, int serverPort);

    /// <summary>The same chain, saying whether an empty one met an owner it was refused access to. A resolver that cannot
    /// tell never claims a refusal.</summary>
    CallerResolution ResolveCaller(IPAddress clientAddress, int clientPort, int serverPort) => new(Resolve(clientAddress, clientPort, serverPort));

    /// <summary>Whether Windows refuses this broker a full query (PROCESS_QUERY_INFORMATION) of a process in the chain that
    /// it could query in part: for a claude or codex process of this user's, it runs elevated. A resolver that cannot tell
    /// never claims a refusal.</summary>
    bool QueryDenied(CallerProcess process) => false;
}

public sealed class WindowsCallerProcessResolver : ICallerProcessResolver
{
    public static readonly WindowsCallerProcessResolver Instance = new();
    internal const int MaximumDepth = 16;

    public IReadOnlyList<CallerProcess> Resolve(IPAddress clientAddress, int clientPort, int serverPort)
        => ResolveCaller(clientAddress, clientPort, serverPort).Chain;

    public CallerResolution ResolveCaller(IPAddress clientAddress, int clientPort, int serverPort)
    {
        if (!OperatingSystem.IsWindows() || !IPAddress.IsLoopback(clientAddress) ||
            clientPort is <= 0 or > 65535 || serverPort is <= 0 or > 65535) return new([]);
        if (clientAddress.IsIPv4MappedToIPv6) clientAddress = clientAddress.MapToIPv4();
        int? owner = clientAddress.AddressFamily == AddressFamily.InterNetwork
            ? Owner(2, 24, clientAddress, clientPort, serverPort) : Owner(23, 56, clientAddress, clientPort, serverPort);
        return Compose(owner, pid => Walk(pid, Snapshot(), StartTime), pid => AccessDenied(pid));
    }

    /// <summary>The resolution for a found (or missing) connection owner. Pure over its two lookups, so the elevated case is
    /// tested without an elevated process. Only an EMPTY chain asks: an owner that could be opened is never a refusal, and
    /// a refusal is asked about the owner itself, never a parent (a parent the broker cannot open just ends the walk).</summary>
    internal static CallerResolution Compose(int? owner, Func<int, IReadOnlyList<CallerProcess>> walk, Func<int, bool> accessDenied)
    {
        if (owner is not { } pid) return new([]);
        var chain = walk(pid);
        return new(chain, chain.Count == 0 && accessDenied(pid));
    }

    // ERROR_ACCESS_DENIED from the same limited query StartTime makes, read at once. Any other failure (the process exited,
    // an invalid ID) is not a refusal: it stays caller_unresolved. The open is injectable so the refusal itself is checked
    // without an elevated process.
    internal static bool AccessDenied(int processId, Func<uint, int, (bool Opened, int Error)>? open = null)
        => Denied(processId, ProcessQueryLimitedInformation, open ?? Open);

    // The second shape (review of 25a0c65, finding 1): a same-user UAC elevation that allows the limited query, so the walk
    // reads its start time, and denies the full one (PROCESS_QUERY_INFORMATION; the coordinator's measurement: 0x1000
    // opened, 0x0410 failed with 5). The start time is read again first, so a process ID reused since the walk is never
    // called elevated.
    public bool QueryDenied(CallerProcess process) => QueryDenied(process, StartTime, Open);

    internal static bool QueryDenied(CallerProcess process, Func<int, ulong> startTime, Func<uint, int, (bool Opened, int Error)> open)
        => startTime(process.ProcessId) == process.StartFileTime && Denied(process.ProcessId, ProcessQueryInformation, open);

    internal static bool Denied(int processId, uint access, Func<uint, int, (bool Opened, int Error)> open)
    {
        var (opened, error) = open(access, processId);
        return !opened && error == ErrorAccessDenied;
    }

    private static (bool Opened, int Error) Open(uint access, int processId)
    {
        var process = OpenProcess(access, false, processId);
        int error = Marshal.GetLastPInvokeError();
        bool opened = !process.IsInvalid;
        process.Dispose();
        return (opened, error);
    }

    /// <summary>The owner and its parents, nearest first, from a process snapshot (ID to parent ID and image) and a creation
    /// time lookup (0 when unknown). Pure, so the reused-ID rule is tested without real processes.</summary>
    public static IReadOnlyList<CallerProcess> Walk(int owner, IReadOnlyDictionary<int, (int Parent, string Image)> parents, Func<int, ulong> startTime)
    {
        var chain = new List<CallerProcess>();
        int current = owner;
        ulong childStart = ulong.MaxValue;
        while (chain.Count < MaximumDepth && current > 4 && parents.TryGetValue(current, out var entry))
        {
            ulong start = startTime(current);
            // A "parent" created after its child is a reused ID: the real parent has exited, so the chain ends here.
            if (start == 0 || start > childStart || chain.Any(process => process.ProcessId == current)) break;
            chain.Add(new(current, start, entry.Image));
            childStart = start; current = entry.Parent;
        }
        return chain;
    }

    // MIB_TCPROW_OWNER_PID (24 bytes) or MIB_TCP6ROW_OWNER_PID (56 bytes) after a 4-byte count. Ports are in network byte
    // order in the low 16 bits of their DWORD; addresses are in network byte order as stored.
    private static int? Owner(int family, int rowSize, IPAddress client, int clientPort, int serverPort)
    {
        int size = 0;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            IntPtr buffer = size == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(size);
            try
            {
                uint status = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidConnections, 0);
                if (status == ErrorInsufficientBuffer) continue;
                if (status != 0 || buffer == IntPtr.Zero) return null;
                int count = Marshal.ReadInt32(buffer);
                byte[] clientBytes = client.GetAddressBytes();
                for (int index = 0; index < count && 4 + (index + 1) * rowSize <= size; index++)
                {
                    IntPtr row = buffer + 4 + index * rowSize;
                    byte[] local; int localPort, remotePort, pid;
                    if (family == 2)
                    {
                        local = BitConverter.GetBytes(Marshal.ReadInt32(row, 4));
                        localPort = Port(Marshal.ReadInt32(row, 8)); remotePort = Port(Marshal.ReadInt32(row, 16)); pid = Marshal.ReadInt32(row, 20);
                    }
                    else
                    {
                        local = new byte[16]; Marshal.Copy(row, local, 0, 16);
                        localPort = Port(Marshal.ReadInt32(row, 20)); remotePort = Port(Marshal.ReadInt32(row, 44)); pid = Marshal.ReadInt32(row, 52);
                    }
                    if (localPort == clientPort && remotePort == serverPort && local.AsSpan().SequenceEqual(clientBytes)) return pid > 0 ? pid : null;
                }
                return null;
            }
            finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    private static int Port(int value) => ((value & 0xFF) << 8) | ((value >> 8) & 0xFF);

    private static Dictionary<int, (int Parent, string Image)> Snapshot()
    {
        var result = new Dictionary<int, (int Parent, string Image)>();
        using var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot.IsInvalid) return result;
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            result[(int)entry.ProcessId] = ((int)entry.ParentProcessId, entry.ExeFile ?? "");
        return result;
    }

    private static ulong StartTime(int processId)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid || !GetProcessTimes(process, out ulong created, out _, out _, out _)) return 0;
        return created;
    }

    private const int TcpTableOwnerPidConnections = 4;
    private const uint ErrorInsufficientBuffer = 122;
    private const int ErrorAccessDenied = 5;
    private const uint SnapProcess = 0x2;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessQueryInformation = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? ExeFile;
    }

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out ulong created, out ulong exited, out ulong kernel, out ulong user);
}
