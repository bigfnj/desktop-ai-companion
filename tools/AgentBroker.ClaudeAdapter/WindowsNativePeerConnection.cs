using System.ComponentModel;
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
        private NativeImageBinding? binding;
        public bool EnforcesCurrentUser => true;
        public NativeProcessIdentity Identity => binding!.Identity;
        public Stream Outbound => pipe;
        internal Connection(NamedPipeClientStream pipe, Action<string>? stageObserver)
        {
            this.pipe = pipe;
            KernelProcessImage? process = null;
            try
            {
                stageObserver?.Invoke("server_pid");
                int pid = ServerProcessId(pipe);
                process = new KernelProcessImage(pid);
                stageObserver?.Invoke("pipe_owner_and_dacl");
                ValidatePipeSecurity(pipe.SafePipeHandle);
                stageObserver?.Invoke("server_public_image_hash");
                binding = new NativeImageBinding(process, path => new LockedPublicImage(path));
                process = null; // The binding owns the retained process handle as well as the file lease.
                if (ServerProcessId(pipe) != pid) throw new IOException("Native process identity changed.");
                stageObserver?.Invoke("identity_observed");
            }
            catch { binding?.Dispose(); throw; }
            finally { process?.Dispose(); }
        }
        public NativeProcessIdentity Reinspect()
        {
            if (binding is null) throw new ObjectDisposedException(nameof(Connection));
            if (ServerProcessId(pipe) != Identity.ProcessId) throw new IOException("Native process identity changed.");
            ValidatePipeSecurity(pipe.SafePipeHandle);
            var identity = binding.Reinspect();
            if (ServerProcessId(pipe) != identity.ProcessId) throw new IOException("Native process identity changed.");
            return identity;
        }
        public ValueTask DisposeAsync()
        {
            try { binding?.Dispose(); binding = null; }
            finally { pipe.Dispose(); }
            return ValueTask.CompletedTask;
        }
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
