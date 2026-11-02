namespace AgentBroker.ClaudeAdapter;

internal interface INativeImageProcess : IDisposable
{
    int ProcessId { get; }
    string ImagePath { get; }
    ulong StartFileTime { get; }
    bool IsCurrentUser { get; }
    bool HasExited { get; }
}

internal interface INativeImageLease : IDisposable
{
    string ImagePath { get; }
    string Sha256 { get; }
    void VerifyPath(string observedPath);
}

internal sealed class NativeImagePathMismatchException() : IOException("Native kernel image path changed.");

// Both production and fake-based sends use these guards. The process and file leases have the connection's lifetime.
internal sealed class NativeImageBinding : IDisposable
{
    private readonly INativeImageProcess process;
    private INativeImageLease? image;
    private bool disposed;
    internal NativeProcessIdentity Identity { get; }

    internal NativeImageBinding(INativeImageProcess process, Func<string, INativeImageLease> acquireImage)
    {
        this.process = process;
        try
        {
            string before = KernelProcessImage.NormalizePath(process.ImagePath);
            if (process.HasExited || !process.IsCurrentUser) throw new UnauthorizedAccessException("Native process owner unavailable.");
            image = acquireImage(before);
            RequireSamePath(before, image.ImagePath);
            RequireSamePath(process.ImagePath, image.ImagePath); // Post-lease kernel query.
            Identity = Snapshot();
        }
        catch { Dispose(); throw; }
    }

    internal NativeProcessIdentity Reinspect()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        RequireSamePath(process.ImagePath, Identity.ImagePath); // Reinspection kernel query.
        image!.VerifyPath(Identity.ImagePath);
        RequireSamePath(process.ImagePath, image.ImagePath); // Recheck after held-path validation too.
        return Snapshot();
    }

    private NativeProcessIdentity Snapshot()
    {
        if (process.HasExited) throw new IOException("Native process exited.");
        return new(process.ProcessId, image!.ImagePath, image.Sha256, process.StartFileTime, process.IsCurrentUser);
    }

    private static void RequireSamePath(string current, string expected)
    {
        if (!string.Equals(KernelProcessImage.NormalizePath(current), KernelProcessImage.NormalizePath(expected), StringComparison.Ordinal))
            throw new NativeImagePathMismatchException();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { image?.Dispose(); image = null; }
        finally { process.Dispose(); }
    }
}
