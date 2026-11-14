using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AgentBroker.Core;
using Microsoft.Win32.SafeHandles;

namespace AgentBroker.Worker.Attachments;

// The sender's read of a caller-named file (send_with_attachments, lane F). The path is the local agent's own; the broker
// runs as the same user, so this grants nothing the agent lacks. It still refuses what the broker should never copy: a
// path that is not a plain drive-letter path, a reparse point anywhere from the file up to the volume root, anything but a
// regular disk file, an alternate data stream, and the broker's own data directory (its identity and store) other than
// files it received. The bytes are read ONCE from one handle; the hash and the staged copy both come from that one read,
// so a later change to the original is never sent and never makes the hash disagree with the copy.
internal static class AttachmentSource
{
    internal sealed record Loaded(string Name, byte[] Bytes, string Sha256);

    // Test seam: counts files this process opened for attachment, so a refused caller can prove nothing was read.
    internal static int OpenedFiles;

    internal static Loaded Load(string path, long remainingBytes, string dataDirectory)
    {
        string full = Validate(path, dataDirectory);
        using SafeFileHandle handle = OpenRegularFile(full);
        Interlocked.Increment(ref OpenedFiles);
        byte[] bytes = ReadBounded(handle, Math.Min(AttachmentEnvelope.MaximumFileBytes, remainingBytes));
        return new(AttachmentEnvelope.Sanitise(Path.GetFileName(full)), bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    // One open of a regular disk file that is not itself a reparse point: FILE_FLAG_OPEN_REPARSE_POINT opens a link rather
    // than its target, and the attribute tag of the opened handle then refuses it. The caller proved the parent chain.
    internal static SafeFileHandle OpenRegularFile(string full)
    {
        SafeFileHandle handle = CreateFileW(@"\\?\" + full, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting,
            FileFlagOpenReparsePoint | FileFlagSequentialScan, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid || GetFileType(handle) != FileTypeDisk) throw Invalid();
            byte[] tag = new byte[8]; // FILE_ATTRIBUTE_TAG_INFO: FileAttributes, ReparseTag
            if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, tag, (uint)tag.Length)) throw Invalid();
            uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(tag);
            if ((attributes & (FileAttributeReparsePoint | FileAttributeDirectory | FileAttributeDevice)) != 0) throw Invalid();
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    // The whole file from the one handle, refused beyond maximum: a file that grew while it was read is measured by what
    // exists, not by the length taken first, and one that shrank is not a stable source.
    internal static byte[] ReadBounded(SafeFileHandle handle, long maximum)
    {
        try
        {
            long length = RandomAccess.GetLength(handle);
            if (length > maximum) throw TooLarge();
            byte[] bytes = new byte[length];
            int read = 0;
            while (read < bytes.Length)
            {
                int count = RandomAccess.Read(handle, bytes.AsSpan(read), read);
                if (count == 0) throw Invalid();
                read += count;
            }
            if (RandomAccess.Read(handle, new byte[1], read) != 0) throw TooLarge();
            return bytes;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw Invalid(); }
    }

    // Every existing directory from the file's parent up to the volume root is a plain directory (HandoffPrepareClient rule).
    internal static void RequirePlainParents(string full)
    {
        try
        {
            for (string? component = Path.GetDirectoryName(full); component is not null; component = Path.GetDirectoryName(component))
                if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0) throw Invalid();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw Invalid(); }
    }

    internal static string Validate(string path, string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Any(char.IsControl)) throw Invalid();
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { throw Invalid(); }
        // A drive-letter path whose only colon is the drive's: no UNC, device or alternate-data-stream name.
        if (full.Length < 4 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != Path.DirectorySeparatorChar ||
            full[3..].Contains(':') || Path.GetFileName(full).Length == 0) throw Invalid();
        string data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)) + Path.DirectorySeparatorChar;
        string received = Path.Combine(data, "attachments", "inbox") + Path.DirectorySeparatorChar;
        if (full.StartsWith(data, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(received, StringComparison.OrdinalIgnoreCase))
            throw Invalid();
        RequirePlainParents(full);
        return full;
    }

    private static BrokerException Invalid() => new(AttachmentCodes.SourceInvalid, "The attachment source is not a plain local file.");
    private static BrokerException TooLarge() => new(AttachmentCodes.TooLarge, "The attachment exceeds its size bound.");

    private const uint GenericRead = 0x80000000, FileShareRead = 0x1, FileShareWrite = 0x2, OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000, FileFlagSequentialScan = 0x08000000;
    private const uint FileAttributeDirectory = 0x10, FileAttributeDevice = 0x40, FileAttributeReparsePoint = 0x400;
    private const uint FileTypeDisk = 1;
    private const int FileAttributeTagInfo = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation,
        uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle file);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, [Out] byte[] information, uint size);
}
