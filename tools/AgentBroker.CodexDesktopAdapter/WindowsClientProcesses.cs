using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace AgentBroker.CodexDesktopAdapter;

// Pid is carried for the engine binding report (lane broker-163); the profile checks and DiscoveryReuse's key never read it.
internal sealed record CodeOwnedEngine(string Image, ulong CreationFileTime, string ParentImage,
    ulong ParentCreationFileTime, bool SameUser, uint Pid = 0);

internal static class WindowsClientProcesses
{
    internal static IReadOnlyList<CodeOwnedEngine> ReadCodeOwnedEngines() => SelectCodeOwnedEngines(ReadSnapshot(), ReadMetadata);

    // The engines a thread's writer-lock holder may be (CodexEngineBinding, report only): codex.exe processes under a running
    // Code.exe (the engines the profile check reads) and the desktop app's, a codex.exe whose parent is the Codex package's
    // ChatGPT.exe (the image the desktop path identifies by its WindowsApps package name). Both need the same user and a
    // parent created no later than the engine (a later one is a reused parent ID). Unlike the profile check, which must
    // refuse on any unreadable engine, a process that cannot be read is set aside on its own (Unreadable): it can only make
    // a lock it holds unverifiable, never every lock (review of 25a0c65, finding 4).
    internal static CodexEngineSet ReadCodexEngines() => SelectCodexEngines(ReadSnapshot(), ReadMetadata);
    internal const int MaximumEngineCandidates = 512;

    internal static CodexEngineSet SelectCodexEngines(IReadOnlyDictionary<uint, (uint Parent, string Name)> processes,
        Func<uint, (string Image, ulong Created, bool SameUser)> readMetadata)
    {
        var engines = new List<CodexEngine>();
        var unreadable = new HashSet<uint>();
        bool incomplete = false;
        foreach (var process in processes.Where(p => string.Equals(p.Value.Name, "codex.exe", StringComparison.OrdinalIgnoreCase)))
        {
            // The bound: past it, the set says it is incomplete, and an unmatched holder reads query_failed (Classify).
            if (engines.Count + unreadable.Count >= MaximumEngineCandidates) { incomplete = true; break; }
            if (!processes.TryGetValue(process.Value.Parent, out var parent)) continue;
            CodexEngineHost host;
            if (string.Equals(parent.Name, "Code.exe", StringComparison.OrdinalIgnoreCase)) host = CodexEngineHost.Code;
            else if (string.Equals(parent.Name, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase)) host = CodexEngineHost.Desktop;
            else continue;
            (string Image, ulong Created, bool SameUser) child, owner;
            try { child = readMetadata(process.Key); owner = readMetadata(process.Value.Parent); }
            catch (Exception error) when (error is DesktopAdapterException or Win32Exception or UnauthorizedAccessException or
                InvalidOperationException or ArgumentException)
            { unreadable.Add(process.Key); continue; }
            string expectedParent = host == CodexEngineHost.Code ? "Code.exe" : "ChatGPT.exe";
            if (!child.SameUser || !owner.SameUser || owner.Created > child.Created ||
                !string.Equals(Path.GetFileName(owner.Image), expectedParent, StringComparison.OrdinalIgnoreCase) ||
                host == CodexEngineHost.Desktop && DesktopCompatibilityProfile.InstalledApplicationVersion(owner.Image) is null) continue;
            engines.Add(new(process.Key, child.Created, host));
        }
        return new(engines, unreadable, incomplete);
    }

    private static Dictionary<uint, (uint Parent, string Name)> ReadSnapshot()
    {
        using var snapshot = Native.CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new DesktopAdapterException("vscode_process_snapshot_unverified");
        var processes = new Dictionary<uint, (uint Parent, string Name)>();
        var entry = new Native.ProcessEntry { Size = (uint)Marshal.SizeOf<Native.ProcessEntry>() };
        if (!Native.Process32FirstW(snapshot, ref entry))
            throw new DesktopAdapterException("vscode_process_snapshot_unverified");
        do
        {
            if (processes.Count >= 32768) throw new DesktopAdapterException("vscode_process_snapshot_limit");
            processes.Add(entry.Id, (entry.Parent, entry.ImageName));
            entry.Size = (uint)Marshal.SizeOf<Native.ProcessEntry>();
        } while (Native.Process32NextW(snapshot, ref entry));
        if (Marshal.GetLastWin32Error() != 18) throw new DesktopAdapterException("vscode_process_snapshot_unverified");
        return processes;
    }

    // The engines a running Code.exe owns, from one process snapshot. A codex.exe whose parent is not in the snapshot has no
    // live parent, so it cannot be Code-owned: it is a standalone CLI or an app-server left behind by a closed window, which
    // the profile ignores (VsCodeCompatibilityProfile.VerifyRunningClients). It used to throw vscode_process_parent_unverified
    // for the whole set, so one orphan anywhere on the machine turned off Codex wake for every chat (A-broker-03). Every
    // codex.exe whose parent IS a running Code.exe is still read and checked exactly as before.
    internal static IReadOnlyList<CodeOwnedEngine> SelectCodeOwnedEngines(IReadOnlyDictionary<uint, (uint Parent, string Name)> processes,
        Func<uint, (string Image, ulong Created, bool SameUser)> readMetadata) => SelectOwnedEngines(processes, readMetadata, "Code.exe");

    private static IReadOnlyList<CodeOwnedEngine> SelectOwnedEngines(IReadOnlyDictionary<uint, (uint Parent, string Name)> processes,
        Func<uint, (string Image, ulong Created, bool SameUser)> readMetadata, string parentName)
    {
        List<CodeOwnedEngine> result = [];
        foreach (var process in processes.Where(p => string.Equals(p.Value.Name, "codex.exe", StringComparison.OrdinalIgnoreCase)))
        {
            if (!processes.TryGetValue(process.Value.Parent, out var parent)) continue;
            if (!string.Equals(parent.Name, parentName, StringComparison.OrdinalIgnoreCase)) continue;
            var childMetadata = readMetadata(process.Key);
            var parentMetadata = readMetadata(process.Value.Parent);
            result.Add(new(childMetadata.Image, childMetadata.Created, parentMetadata.Image, parentMetadata.Created,
                childMetadata.SameUser && parentMetadata.SameUser, process.Key));
            if (result.Count > 256) throw new DesktopAdapterException("vscode_process_count_limit");
        }
        return result;
    }

    private static (string Image, ulong Created, bool SameUser) ReadMetadata(uint pid)
    {
        using var process = Native.OpenProcess(0x1000, false, pid);
        if (process.IsInvalid || !Native.GetProcessTimes(process, out long created, out _, out _, out _))
            throw new DesktopAdapterException("vscode_process_identity_unverified");
        StringBuilder image = new(32768);
        uint length = (uint)image.Capacity;
        if (!Native.QueryFullProcessImageNameW(process, 0, image, ref length)
            || !Native.OpenProcessToken(process, 8, out var token))
            throw new DesktopAdapterException("vscode_process_identity_unverified");
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        using (var current = WindowsIdentity.GetCurrent())
            return (Path.GetFullPath(image.ToString()), unchecked((ulong)created),
                identity.User is not null && current.User is not null && identity.User.Equals(current.User));
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ProcessEntry
        {
            internal uint Size, Usage, Id;
            internal UIntPtr DefaultHeapId;
            internal uint ModuleId, ThreadCount, Parent;
            internal int PriorityClassBase;
            internal uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string ImageName;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder image, ref uint length);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    }
}
