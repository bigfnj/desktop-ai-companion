using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentBroker.ClaudeAdapter;

/// <summary>One retained, limited-query process handle. Image paths come from the kernel, never its module list.</summary>
public sealed class KernelProcessImage : IDisposable, INativeImageProcess
{
    internal delegate bool ImagePathQuery(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
    private readonly SafeProcessHandle process;
    private readonly ImagePathQuery query;
    public int ProcessId { get; }

    public KernelProcessImage(int processId) : this(processId, Open(processId), QueryFullProcessImageNameW) { }

    internal KernelProcessImage(int processId, SafeProcessHandle process, ImagePathQuery query)
    { ProcessId = processId; this.process = process; this.query = query; }

    private static SafeProcessHandle Open(int processId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var handle = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION only.
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    public string ImagePath
    {
        get
        {
            var path = new StringBuilder(32768);
            uint length = (uint)path.Capacity;
            if (!query(process, 0, path, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (length == 0 || length >= path.Capacity || length != path.Length)
                throw new IOException("Kernel process image path is unavailable.");
            return NormalizePath(path.ToString());
        }
    }

    internal static string NormalizePath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Native process image must be local.");
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Native process image path is invalid.");
        return Path.GetFullPath(path);
    }

    public ulong StartFileTime
    {
        get
        {
            ReadTimes(out var created, out _);
            return ((ulong)created.High << 32) | created.Low;
        }
    }

    // Waiting would need SYNCHRONIZE; GetProcessTimes' exit time is undefined for a running process.
    // STILL_ACTIVE is a liveness hint (an exited process can use code 259). The retained handle and pipe PID
    // checks preserve identity even then; an exited pipe cannot authenticate a different process.
    public bool HasExited
    {
        get
        {
            if (!GetExitCodeProcess(process, out uint code)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return code != 259;
        }
    }

    public bool IsCurrentUser
    {
        get
        {
            if (!OpenProcessToken(process, 0x0008, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            using (var server = new WindowsIdentity(token.DangerousGetHandle()))
            using (var current = WindowsIdentity.GetCurrent())
                return server.User is { } serverSid && current.User is { } currentSid && serverSid.Equals(currentSid);
        }
    }

    private void ReadTimes(out FileTime created, out FileTime exited)
    {
        if (!GetProcessTimes(process, out created, out exited, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose() => process.Dispose();

    [StructLayout(LayoutKind.Sequential)] private struct FileTime { internal uint Low, High; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
