using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentBroker.ClaudeAdapter;

/// <summary>
/// Prepared production reader. An agent must obtain exact platform approval for this operation before
/// invoking it: targets match protected pattern H05 (home *.key) and, when in a workspace, W06.
/// No test constructs this source or reads/creates any credential file.
/// </summary>
public sealed class ProtectedPeerCredentialSource : IPeerCredentialSource
{
    public byte[] Read(CredentialTarget target)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string path = Path.GetFullPath(target.FullPath);
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.CanonicalPipe))).ToLowerInvariant();
        string expected = target.ProcessId.ToString(CultureInfo.InvariantCulture) + "." + digest + ".key";
        if (!Path.IsPathFullyQualified(target.FullPath) || Path.GetFileName(path) != expected ||
            !string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "sessions", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid native credential target.");
        // Reject every reparse component. Recheck final handle identity after opening to catch swaps.
        CredentialPathPolicy.RejectReparseComponents(path, File.GetAttributes);
        using var handle = CreateFileW(path, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandle(handle, out var info) ||
            !CredentialPathPolicy.IsBoundedCredentialFile((FileAttributes)info.Attributes, info.Links, ((ulong)info.SizeHigh << 32) | info.SizeLow))
            throw new IOException("Invalid native credential file.");
        var final = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(handle, final, (uint)final.Capacity, 0);
        if (length == 0 || length >= final.Capacity) throw new IOException("Cannot verify native credential target.");
        string resolved = final.ToString();
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) resolved = @"\\" + resolved[8..];
        else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        if (!string.Equals(Path.GetFullPath(resolved), path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Native credential target changed.");
        byte[] buffer = new byte[4097];
        try
        {
            using var input = new FileStream(handle, FileAccess.Read, 1, isAsync: false);
            int used = 0, count;
            while (used < buffer.Length && (count = input.Read(buffer, used, buffer.Length - used)) != 0) used += count;
            if (used > 4096) throw new IOException("Native credential record is oversized.");
            return buffer.AsSpan(0, used).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct FileTime { internal uint Low; internal uint High; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        internal uint Attributes;
        internal FileTime Created, Accessed, Written;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);
}
