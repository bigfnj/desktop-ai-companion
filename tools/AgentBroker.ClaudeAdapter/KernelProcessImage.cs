using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AgentBroker.Worker.Tests")]

namespace AgentBroker.ClaudeAdapter;

// The production constructor and synthetic checks use the same limited-open and elevation-admission flow.
internal interface IKernelProcessOperations
{
    SafeProcessHandle Open(int processId, uint access);
    bool QueryImagePath(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
    ulong StartFileTime(SafeProcessHandle process);
    uint ExitCode(SafeProcessHandle process);
    bool IsCurrentUser(SafeProcessHandle process);
    bool IsElevated(SafeProcessHandle process);
}

/// <summary>One retained, limited-query process handle. Image paths come from the kernel, never its module list.</summary>
public sealed class KernelProcessImage : IDisposable, INativeImageProcess
{
    internal delegate bool ImagePathQuery(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
    private readonly SafeProcessHandle process;
    private readonly IKernelProcessOperations operations;
    private readonly ImagePathQuery query;
    public int ProcessId { get; }

    public KernelProcessImage(int processId) : this(processId, WindowsKernelProcessOperations.Instance) { }

    internal KernelProcessImage(int processId, IKernelProcessOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ProcessId = processId;
        this.operations = operations;
        process = operations.Open(processId, 0x1000); // PROCESS_QUERY_LIMITED_INFORMATION only.
        try
        {
            if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            // A limited open can succeed for an elevated same-user process. Refuse before any metadata gains authority.
            if (operations.IsElevated(process)) throw new Win32Exception(5);
            query = operations.QueryImagePath;
        }
        catch { process.Dispose(); throw; }
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

    public ulong StartFileTime => operations.StartFileTime(process);

    // Waiting would need SYNCHRONIZE; GetProcessTimes' exit time is undefined for a running process.
    // STILL_ACTIVE is a liveness hint (an exited process can use code 259). The retained handle and pipe PID
    // checks preserve identity even then; an exited pipe cannot authenticate a different process.
    public bool HasExited => operations.ExitCode(process) != 259;

    public bool IsCurrentUser => operations.IsCurrentUser(process);

    public void Dispose() => process.Dispose();
}

internal sealed class WindowsKernelProcessOperations : IKernelProcessOperations
{
    internal static readonly WindowsKernelProcessOperations Instance = new();

    private WindowsKernelProcessOperations() { }

    public SafeProcessHandle Open(int processId, uint access)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var handle = OpenProcess(access, false, processId);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    public bool QueryImagePath(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length)
        => QueryFullProcessImageNameW(process, flags, path, ref length);

    public ulong StartFileTime(SafeProcessHandle process)
    {
        if (!GetProcessTimes(process, out var created, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return ((ulong)created.High << 32) | created.Low;
    }

    public uint ExitCode(SafeProcessHandle process)
    {
        if (!GetExitCodeProcess(process, out uint code)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return code;
    }

    public bool IsCurrentUser(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, 0x0008, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        using (var server = new WindowsIdentity(token.DangerousGetHandle()))
        using (var current = WindowsIdentity.GetCurrent())
            return server.User is { } serverSid && current.User is { } currentSid && serverSid.Equals(currentSid);
    }

    public bool IsElevated(SafeProcessHandle process)
    {
        // Unknown elevation is not an unelevated process. Error 5 also lets discovery count and skip the session.
        if (!OpenProcessToken(process, 0x0008, out var token)) throw new Win32Exception(5);
        using (token)
        {
            if (!GetTokenInformation(token, 20, out uint elevated, sizeof(uint), out uint returned) || returned != sizeof(uint))
                throw new Win32Exception(5); // TokenElevation is a single DWORD, queried from the retained process's token.
            return elevated != 0;
        }
    }

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
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        out uint information, uint bytes, out uint returned);
}
