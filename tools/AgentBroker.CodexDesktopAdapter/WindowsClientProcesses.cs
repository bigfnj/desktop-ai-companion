using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace AgentBroker.CodexDesktopAdapter;

internal sealed record CodeOwnedEngine(string Image, ulong CreationFileTime, string ParentImage,
    ulong ParentCreationFileTime, bool SameUser);

internal static class WindowsClientProcesses
{
    internal static IReadOnlyList<CodeOwnedEngine> ReadCodeOwnedEngines()
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

        List<CodeOwnedEngine> result = [];
        foreach (var process in processes.Where(p => string.Equals(p.Value.Name, "codex.exe", StringComparison.OrdinalIgnoreCase)))
        {
            if (!processes.TryGetValue(process.Value.Parent, out var parent))
                throw new DesktopAdapterException("vscode_process_parent_unverified");
            if (!string.Equals(parent.Name, "Code.exe", StringComparison.OrdinalIgnoreCase)) continue;
            var childMetadata = ReadMetadata(process.Key);
            var parentMetadata = ReadMetadata(process.Value.Parent);
            result.Add(new(childMetadata.Image, childMetadata.Created, parentMetadata.Image, parentMetadata.Created,
                childMetadata.SameUser && parentMetadata.SameUser));
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
