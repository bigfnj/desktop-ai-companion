using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AgentBroker.ClaudeAdapter;

/// <summary>Prepared production transport. Synthetic tests never invoke this OS/process implementation.</summary>
public sealed class WindowsNativePeerConnectionFactory(Action<string>? stageObserver = null) : INativePeerConnectionFactory
{
    public async Task<INativePeerConnection> ConnectAsync(string pipeName, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        stageObserver?.Invoke("pipe_create");
        // .NET CurrentUserOnly compares pipe owner to the token's default Owner, which can differ
        // from its User SID. Validate actual connected server identity and pipe User ownership below.
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            stageObserver?.Invoke("pipe_connect");
            await pipe.ConnectAsync(5000, cancellation).ConfigureAwait(false);
            stageObserver?.Invoke("pipe_connected");
            return new Connection(pipe, stageObserver);
        }
        catch { pipe.Dispose(); throw; }
    }

    private sealed class Connection : INativePeerConnection
    {
        private readonly NamedPipeClientStream pipe;
        private LockedPublicImage? image;
        public bool EnforcesCurrentUser => true;
        public NativeProcessIdentity Identity { get; }
        public Stream Outbound => pipe;
        internal Connection(NamedPipeClientStream pipe, Action<string>? stageObserver)
        {
            this.pipe = pipe;
            try { Identity = Inspect(pipe, path => { image = new LockedPublicImage(path); return image.Sha256; }, stageObserver); }
            catch { image?.Dispose(); throw; }
        }
        public NativeProcessIdentity Reinspect() => Inspect(pipe, path =>
        {
            if (image is null) throw new ObjectDisposedException(nameof(Connection));
            image.VerifyPath(path); return image.Sha256;
        });
        public ValueTask DisposeAsync()
        {
            try { image?.Dispose(); image = null; }
            finally { pipe.Dispose(); }
            return ValueTask.CompletedTask;
        }
    }

    private static NativeProcessIdentity Inspect(NamedPipeClientStream pipe, Func<string, string> inspectImage, Action<string>? stageObserver = null)
    {
        stageObserver?.Invoke("server_pid");
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid > int.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        stageObserver?.Invoke("server_image_path");
        using Process process = Process.GetProcessById((int)pid);
        string image = process.MainModule?.FileName ?? throw new IOException("Native process image unavailable.");
        if (!Path.IsPathFullyQualified(image) || !image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Native process image invalid.");
        stageObserver?.Invoke("server_creation_time");
        if (!GetProcessTimes(process.SafeHandle, out var created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        ulong start = ((ulong)created.High << 32) | created.Low;
        stageObserver?.Invoke("server_user_identity");
        if (!OpenProcessToken(process.SafeHandle, 0x0008, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        bool sameUser;
        using (token)
        using (var server = new WindowsIdentity(token.DangerousGetHandle()))
        using (var current = WindowsIdentity.GetCurrent())
            sameUser = server.User is { } serverSid && current.User is { } currentSid && serverSid.Equals(currentSid);
        if (!sameUser) throw new UnauthorizedAccessException("Native pipe server is not the current user.");
        stageObserver?.Invoke("pipe_owner_and_dacl");
        ValidatePipeSecurity(pipe.SafePipeHandle);
        stageObserver?.Invoke("server_public_image_hash");
        string hash = inspectImage(image);
        stageObserver?.Invoke("server_recheck");
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint again) || again != pid || process.HasExited)
            throw new IOException("Native process identity changed.");
        stageObserver?.Invoke("identity_observed");
        return new((int)pid, image, hash, start, sameUser);
    }

    private static void ValidatePipeSecurity(SafePipeHandle pipe)
    {
        uint error = GetSecurityInfo(pipe, 6, 1 | 4, out _, out _, out _, out _, out var security);
        if (error != 0) throw new Win32Exception((int)error);
        try
        {
            uint length = GetSecurityDescriptorLength(security);
            if (length is 0 or > 65536) throw new IOException("Invalid native pipe security metadata.");
            byte[] bytes = new byte[length];
            Marshal.Copy(security, bytes, 0, bytes.Length);
            using var current = WindowsIdentity.GetCurrent();
            if (current.User is not { } currentUser || !NativePipeSecurityPolicy.IsCurrentUserPipe(new RawSecurityDescriptor(bytes, 0), currentUser))
                throw new UnauthorizedAccessException("Native pipe user ownership or write policy was refused.");
        }
        finally { LocalFree(security); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { internal uint Low; internal uint High; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafePipeHandle handle, int kind, uint information, out IntPtr owner, out IntPtr group,
        out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
