using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DesktopAICompanion.AgentBroker;

internal interface IWorkerChild : IDisposable
{
    int Id { get; }
    bool IsAlive { get; }
    Task WaitForExitAsync(CancellationToken token);
    void StopForcefully();
}

internal interface IWorkerChildLauncher { IWorkerChild Start(ProcessStartInfo start); }

/// <summary>Retains the initial CreateProcess handle, including when later metadata is unavailable.</summary>
internal sealed class WorkerChildLauncher : IWorkerChildLauncher
{
    public IWorkerChild Start(ProcessStartInfo start)
    {
        if (start.UseShellExecute) throw new InvalidOperationException("The worker must be launched directly.");
        Process process = Process.Start(start) ?? throw new IOException("worker_start_failed");
        SafeProcessHandle? handle = null;
        try
        {
            // Direct Process.Start retains CreateProcess's handle. Pin it before reading metadata;
            // all forced cleanup uses this kernel object, never a later lookup by numeric PID.
            handle = process.SafeHandle;
            uint pid = GetProcessId(handle);
            if (pid is 0 or > int.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new Child(process, handle, (int)pid);
        }
        catch
        {
            if (handle is { IsInvalid: false, IsClosed: false }) TerminateProcess(handle, 1);
            process.Dispose();
            throw;
        }
    }

    private sealed class Child(Process process, SafeProcessHandle handle, int id) : IWorkerChild
    {
        public int Id => id;
        public bool IsAlive => WaitForSingleObject(handle, 0) switch
        {
            0 => false,
            258 => true,
            _ => throw new Win32Exception(Marshal.GetLastWin32Error())
        };
        public Task WaitForExitAsync(CancellationToken token) => process.WaitForExitAsync(token);
        public void StopForcefully()
        {
            if (WaitForSingleObject(handle, 0) == 0) return;
            if (!TerminateProcess(handle, 1) &&
                (!GetExitCodeProcess(handle, out uint code) || code == 259))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose() => process.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
}
