using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AgentBroker.ClaudeAdapter;

internal interface INativePipeLease : IDisposable
{
    int ServerProcessId { get; }
    Stream Outbound { get; }
    void ValidateSecurity();
}

/// <summary>Production connection composition. Tests inject OS boundaries, not a replacement connection.</summary>
public sealed class WindowsNativePeerConnectionFactory : INativePeerConnectionFactory
{
    private readonly Func<string, CancellationToken, Task<INativePipeLease>> connect;
    private readonly Func<int, INativeImageProcess> openProcess;
    private readonly Func<string, INativeImageLease> acquireImage;
    private readonly Action<string>? stageObserver;

    public WindowsNativePeerConnectionFactory(Action<string>? stageObserver = null)
        : this(ConnectWindowsAsync, pid => new KernelProcessImage(pid), path => new LockedPublicImage(path), stageObserver) { }

    internal WindowsNativePeerConnectionFactory(Func<string, CancellationToken, Task<INativePipeLease>> connect,
        Func<int, INativeImageProcess> openProcess, Func<string, INativeImageLease> acquireImage,
        Action<string>? stageObserver = null)
    {
        this.connect = connect; this.openProcess = openProcess; this.acquireImage = acquireImage;
        this.stageObserver = stageObserver;
    }

    public async Task<INativePeerConnection> ConnectAsync(string pipeName, CancellationToken cancellation)
    {
        stageObserver?.Invoke("pipe_create");
        stageObserver?.Invoke("pipe_connect");
        var pipe = await connect(pipeName, cancellation).ConfigureAwait(false);
        try
        {
            stageObserver?.Invoke("pipe_connected");
            return new Connection(pipe, openProcess, acquireImage, stageObserver);
        }
        catch { pipe.Dispose(); throw; }
    }

    internal static async Task<INativePipeLease> ConnectWindowsAsync(string pipeName, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        // .NET CurrentUserOnly compares pipe owner to the token's default Owner, which can differ
        // from its User SID. Validate actual connected server identity and pipe User ownership below.
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(5000, cancellation).ConfigureAwait(false);
            return new WindowsPipeLease(pipe);
        }
        catch { pipe.Dispose(); throw; }
    }

    private sealed class Connection : INativePeerConnection
    {
        private readonly INativePipeLease pipe;
        private NativeImageBinding? binding;
        private bool disposed;
        public bool EnforcesCurrentUser => true;
        public NativeProcessIdentity Identity => binding!.Identity;
        public Stream Outbound => pipe.Outbound;
        internal Connection(INativePipeLease pipe, Func<int, INativeImageProcess> openProcess,
            Func<string, INativeImageLease> acquireImage, Action<string>? stageObserver)
        {
            this.pipe = pipe;
            INativeImageProcess? process = null;
            try
            {
                stageObserver?.Invoke("server_pid");
                int pid = pipe.ServerProcessId;
                process = openProcess(pid);
                stageObserver?.Invoke("pipe_owner_and_dacl");
                pipe.ValidateSecurity();
                stageObserver?.Invoke("server_public_image_hash");
                // Transfer before construction: the binding owns cleanup even when its constructor throws.
                var ownedProcess = process;
                process = null;
                binding = new NativeImageBinding(ownedProcess, acquireImage);
                if (pipe.ServerProcessId != pid) throw new IOException("Native process identity changed.");
                stageObserver?.Invoke("identity_observed");
            }
            catch { binding?.Dispose(); throw; }
            finally { process?.Dispose(); }
        }
        public NativeProcessIdentity Reinspect()
        {
            if (binding is null) throw new ObjectDisposedException(nameof(Connection));
            if (pipe.ServerProcessId != Identity.ProcessId) throw new IOException("Native process identity changed.");
            pipe.ValidateSecurity();
            var identity = binding.Reinspect();
            if (pipe.ServerProcessId != identity.ProcessId) throw new IOException("Native process identity changed.");
            return identity;
        }
        public ValueTask DisposeAsync()
        {
            if (disposed) return ValueTask.CompletedTask;
            disposed = true;
            try { binding?.Dispose(); binding = null; }
            finally { pipe.Dispose(); }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class WindowsPipeLease(NamedPipeClientStream pipe) : INativePipeLease
    {
        public int ServerProcessId => WindowsNativePeerConnectionFactory.ServerProcessId(pipe);
        public Stream Outbound => pipe;
        public void ValidateSecurity() => ValidatePipeSecurity(pipe.SafePipeHandle);
        public void Dispose() => pipe.Dispose();
    }

    private static int ServerProcessId(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid > int.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        return checked((int)pid);
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafePipeHandle handle, int kind, uint information, out IntPtr owner, out IntPtr group,
        out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
