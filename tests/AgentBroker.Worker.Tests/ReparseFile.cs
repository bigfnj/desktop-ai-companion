using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Lane F round 2 (T-3): puts a reparse point of a non-Microsoft tag on a regular file the test owns, and takes it off again.
// FSCTL_SET_REPARSE_POINT needs SeCreateSymbolicLinkPrivilege only for symbolic links, so this runs without developer mode
// or elevation. The file keeps its data and has no directory bit, so only the reparse bit of an opened handle marks it.
internal static class ReparseFile
{
    private const uint Tag = 0x0000_7A11; // bit 31 clear: a non-Microsoft tag, stored as a REPARSE_GUID_DATA_BUFFER
    private static readonly Guid Owner = Guid.Parse("6f1c1f4e-6a0c-4d0e-9a51-1d2a6b7c8e90");
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000, FileFlagBackupSemantics = 0x02000000;
    private const uint FsctlSetReparsePoint = 0x000900A4, FsctlDeleteReparsePoint = 0x000900AC;

    internal static bool TrySet(string path)
    {
        using var handle = Open(path);
        if (handle.IsInvalid) return false;
        byte[] buffer = Header(dataLength: 4, total: 24 + 4);
        return DeviceIoControl(handle, FsctlSetReparsePoint, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    internal static void Remove(string path)
    {
        using (var handle = Open(path))
        {
            byte[] buffer = Header(dataLength: 0, total: 24);
            if (!handle.IsInvalid) DeviceIoControl(handle, FsctlDeleteReparsePoint, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        File.Delete(path);
    }

    private static SafeFileHandle Open(string path) => CreateFileW(@"\\?\" + Path.GetFullPath(path), GenericRead | GenericWrite, 0, IntPtr.Zero,
        OpenExisting, FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);

    // ReparseTag, ReparseDataLength, Reserved, ReparseGuid, then dataLength zero bytes of data.
    private static byte[] Header(ushort dataLength, int total)
    {
        byte[] buffer = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, Tag);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), dataLength);
        Owner.TryWriteBytes(buffer.AsSpan(8, 16));
        return buffer;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, uint inputSize, IntPtr output, uint outputSize,
        out uint returned, IntPtr overlapped);
}
