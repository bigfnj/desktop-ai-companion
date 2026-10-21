using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AgentBroker.Claude.Tests")]

namespace AgentBroker.ClaudeAdapter;

/// <summary>One connection's immutable public executable attestation, never a cross-connection cache.</summary>
internal sealed class LockedPublicImage : IDisposable
{
    private readonly FileStream image;
    private readonly string expectedPath;
    private bool disposed;
    internal string Sha256 { get; }

    internal LockedPublicImage(string path)
    {
        expectedPath = Path.GetFullPath(path);
        CredentialPathPolicy.RejectReparseComponents(expectedPath, File.GetAttributes);
        // Deny writes and replacement while this short-lived native connection is being authenticated.
        image = new FileStream(expectedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.SequentialScan);
        try
        {
            VerifyPath(expectedPath);
            Sha256 = Convert.ToHexString(SHA256.HashData(image));
        }
        catch { image.Dispose(); throw; }
    }

    internal void VerifyPath(string observedPath)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!string.Equals(Path.GetFullPath(observedPath), expectedPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Native public image path changed.");
        var final = new StringBuilder(32768);
        uint count = GetFinalPathNameByHandleW(image.SafeFileHandle, final, (uint)final.Capacity, 0);
        if (count == 0 || count >= final.Capacity) throw new IOException("Cannot verify native public image handle.");
        string resolved = final.ToString();
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) resolved = @"\\" + resolved[8..];
        else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        if (!string.Equals(Path.GetFullPath(resolved), expectedPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Native public image handle changed.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; image.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint count, uint flags);
}
