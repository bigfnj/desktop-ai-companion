using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentBroker.Worker;

// Explicit client output only. No peer-supplied path is passed to this class.
// Handle-owned publication (LocalOutputFile design v2 as amended by v2.1): hold the validated parent directory, prove it
// is a local NTFS directory whose canonical chain is plain and, when typed in another case, case-insensitive; create the
// temporary at the held directory's volume GUID path; repeat the chain proofs once that temporary exists (D5; the last
// point-in-time proof before any payload byte, not a freeze); write and flush it by handle; publish it with NtSetInformationFile
// relative to the held handle without replacing; prove the published GUID final path; and on every handled failure
// discard the temporary through its own handle. No destructive operation ever takes a path.
internal static class LocalOutputFile
{
    internal sealed record Retention(string Path, string Sha256, long Bytes);

    internal enum DiscardOutcome { PosixDeleted, NotDeleted }

    // A discard that the file system refused is reported on the primary exception, never hidden (design v2.1 D3).
    internal const string CleanupDataKey = "agentbroker.output.cleanup";
    internal const string TemporaryNotRemoved = "temporary_not_removed";
    internal const string NtStatusDataKey = "agentbroker.output.ntstatus";

    // Test seams, passed per call and null in production. Each transforms what Windows reported, except SkipDisposition,
    // which leaves the disposition call unmade so the refused-discard report is reachable; none replaces a decision.
    internal sealed class Seams
    {
        internal Func<string?, string?>? ParentVolumePath { get; init; }
        internal Func<string?, string?>? ParentFinalPath { get; init; }
        internal Func<string?, string?>? FileSystemName { get; init; }
        internal Func<string, FileAttributes, FileAttributes>? ChainAttributes { get; init; }
        internal Func<string, uint, uint>? CaseSensitiveFlags { get; init; }
        internal Func<string>? TempLeaf { get; init; }
        internal Func<string?, string?>? TempFinalPath { get; init; }
        internal Func<int, int>? RenameStatus { get; init; }
        internal Func<string?, string?>? PublishedFinalPath { get; init; }
        internal bool SkipDisposition { get; init; }
        internal Action<DiscardOutcome>? Discarded { get; init; }
    }

    // The caller's preflight, before any tool is dispatched: the path checks below plus every held-directory proof Save
    // will make (step 2: tag, volume GUID path, DOS pin, NTFS, canonical chain, case), on a handle that is closed again at
    // once. So a destination Save would refuse at step 2 is refused here, before dispatch: a SUBST drive (its DOS final
    // path names the backing volume, so the pin fails), a network or FAT volume, a reparse point, a case-sensitive folder
    // typed in another case. Save repeats every proof on its own held handle, so a change between this preflight and
    // publication is still refused there, after dispatch, with the truthful exit 74.
    internal static string Validate(string destination, Seams? seams = null)
    {
        string full = ValidatePath(destination);
        try { using (OpenHeldDirectory(Path.GetDirectoryName(full)!, seams, out _, out _)) { } }
        catch (Exception error) when (error is TypeLoadException or MarshalDirectiveException or ExternalException)
        { throw new IOException("output_unavailable", error); }
        return full;
    }

    // Path checks only: shape, names, plain typed directories, reserved names, and an existing destination.
    private static string ValidatePath(string destination)
    {
        if (!Path.IsPathFullyQualified(destination) || destination.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidDataException("invalid_output_path");
        RequireUnambiguousNames(destination);
        string full = Path.GetFullPath(destination);
        if (full.Length < 4 || full[1] != ':' || full[2] != Path.DirectorySeparatorChar ||
            full[3..].Contains(':') || full.Any(char.IsControl) || Path.GetFileName(full).Length == 0)
            throw new InvalidDataException("invalid_output_path");
        string parent = Path.GetDirectoryName(full) ?? throw new InvalidDataException("invalid_output_path");
        RequirePlainDirectories(parent);
        string name = Path.GetFileName(full), stem = name.Split('.')[0];
        if (name.EndsWith(' ') || name.EndsWith('.') || stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9')
            throw new InvalidDataException("invalid_output_path");
        try { _ = File.GetAttributes(full); throw new IOException("output_exists"); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        return full;
    }

    private static void RequireUnambiguousNames(string destination)
    {
        // GetFullPath can remove trailing dots and navigation segments. Check the names the caller supplied first.
        string[] components = destination.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int index = 0; index < components.Length; index++)
        {
            string component = components[index];
            if (component is "." or "..") continue;
            if (component.EndsWith(' ') || component.EndsWith('.'))
                throw new InvalidDataException("invalid_output_path");
        }
    }

    private static void RequirePlainDirectories(string directory, Func<string, FileAttributes, FileAttributes>? observe = null)
    {
        for (string? component = directory; component is not null; component = Path.GetDirectoryName(component))
        {
            var attributes = File.GetAttributes(component);
            if (observe is not null) attributes = observe(component, attributes);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0 ||
                Path.GetFileName(component).EndsWith(' ') || Path.GetFileName(component).EndsWith('.'))
                throw new InvalidDataException("invalid_output_path");
        }
    }

    // The beforePublish seam permits an actual destination-race check without timing-dependent test loops.
    internal static Retention Save(string destination, byte[] bytes, Action? beforePublish = null, Seams? seams = null)
    {
        try
        {
            if (IntPtr.Size != 8) throw new IOException("output_unavailable"); // the rename buffer layout is the 64-bit one
            string full = ValidatePath(destination); // step 2 below makes the held-directory proofs on the handle it keeps
            string parent = Path.GetDirectoryName(full)!, leaf = Path.GetFileName(full);
            var retention = new Retention(full, Convert.ToHexString(SHA256.HashData(bytes)), bytes.LongLength);
            using SafeFileHandle held = OpenHeldDirectory(parent, seams, out string canon, out string volume);
            string tempLeaf = seams?.TempLeaf?.Invoke() ?? ".agentbroker-output-" + Guid.NewGuid().ToString("N") + ".tmp";
            using SafeFileHandle temp = CreateFileW(Join(volume, tempLeaf), TempAccess, 0, IntPtr.Zero, CreateNew,
                FileAttributeNormal | FileFlagWriteThrough, IntPtr.Zero);
            if (temp.IsInvalid) throw Unavailable(Marshal.GetLastPInvokeError());
            try
            {
                string? created = FinalPath(temp, VolumeNameGuid);
                if (seams?.TempFinalPath is { } observeCreated) created = observeCreated(created);
                if (!IsFinalPathOf(created, volume, tempLeaf)) throw new IOException("output_unavailable");
                RequireChain(canon, parent, seams); // step 3b, post-create: our entry exists; last proof before any payload byte
                Write(temp, bytes);
                beforePublish?.Invoke();
                int status = Rename(temp, held, leaf);
                if (seams?.RenameStatus is { } observeRenamed) status = observeRenamed(status);
                if (status != 0) throw RenameFailure(status);
                string? published = FinalPath(temp, VolumeNameGuid);
                if (seams?.PublishedFinalPath is { } observePublished) published = observePublished(published);
                if (!IsFinalPathOf(published, volume, leaf)) throw new IOException("output_unavailable");
            }
            catch (Exception error)
            {
                DiscardOutcome outcome = Discard(temp, seams);
                seams?.Discarded?.Invoke(outcome);
                if (outcome == DiscardOutcome.NotDeleted) error.Data[CleanupDataKey] = TemporaryNotRemoved;
                throw;
            }
            return retention;
        }
        catch (Exception error) when (error is TypeLoadException or MarshalDirectiveException or ExternalException)
        {
            var wrapped = new IOException("output_unavailable", error);
            if (CleanupReport(error) is { } cleanup) wrapped.Data[CleanupDataKey] = cleanup;
            throw wrapped;
        }
    }

    // The callers' exit-74 JSON adds "cleanup":"temporary_not_removed" only when this returns a value.
    internal static string? CleanupReport(Exception error) =>
        error.Data[CleanupDataKey] is TemporaryNotRemoved ? TemporaryNotRemoved : null;

    // Step 2. The returned handle holds the validated parent for the whole publication: FILE_TRAVERSE makes it take part in
    // share checks and FILE_SHARE_DELETE is withheld, so nobody can rename or delete it, and on NTFS no ancestor can be
    // renamed while it is open (MS-FSA 2.1.4.2). canon is the file system's DOS spelling (the pin); volume is the volume
    // GUID final path that steps 3 and 7 use.
    internal static SafeFileHandle OpenHeldDirectory(string parent, Seams? seams, out string canon, out string volume)
    {
        SafeFileHandle handle = CreateFileW(@"\\?\" + parent, HeldAccess, FileShareRead | FileShareWrite, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid) throw Unavailable(Marshal.GetLastPInvokeError());
            byte[] tag = new byte[8]; // FILE_ATTRIBUTE_TAG_INFO: FileAttributes, ReparseTag
            if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, tag, (uint)tag.Length))
                throw Unavailable(Marshal.GetLastPInvokeError());
            uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(tag);
            if ((attributes & FileAttributeReparsePoint) != 0) throw new InvalidDataException("invalid_output_path");
            if ((attributes & FileAttributeDirectory) == 0) throw new InvalidDataException("invalid_output_path");
            string? guid = FinalPath(handle, VolumeNameGuid);
            if (seams?.ParentVolumePath is { } observeVolume) guid = observeVolume(guid);
            if (guid is null) throw new InvalidDataException("invalid_output_path");
            string? dos = FinalPath(handle, VolumeNameDos);
            if (seams?.ParentFinalPath is { } observeDos) dos = observeDos(dos);
            canon = CanonicalDirectory(dos, parent) ?? throw new InvalidDataException("invalid_output_path");
            string? fileSystem = FileSystemName(handle);
            if (seams?.FileSystemName is { } observeFileSystem) fileSystem = observeFileSystem(fileSystem);
            if (!string.Equals(fileSystem, "NTFS", StringComparison.Ordinal)) throw new InvalidDataException("invalid_output_path");
            RequireChain(canon, parent, seams); // preflight (design v2.1 D5); Save repeats it once the temporary exists
            volume = guid;
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    // Step 2c. The held directory's DOS final path must name the validated parent. Case-insensitive, because the user may
    // type any case; the value returned is the file system's spelling. A case difference is then proven harmless by 2f.
    internal static string? CanonicalDirectory(string? finalPath, string parent) =>
        finalPath is not null && finalPath.StartsWith(@"\\?\", StringComparison.Ordinal) &&
        string.Equals(finalPath[4..], parent, StringComparison.OrdinalIgnoreCase) ? finalPath[4..] : null;

    // Steps 2e and 2f, run twice (design v2.1 D5). First as a preflight in OpenHeldDirectory; then again in Save, after
    // CREATE_NEW has made our owned entry and before any payload byte is written. The held handle freezes the chain against
    // renames, not against attribute changes: a directory, even a non-empty one, can still gain a reparse point whose tag
    // has the directory bit (IsReparseTagDirectory, e.g. a cloud-files placeholder; never a name surrogate such as a
    // junction), and its case-sensitivity flag can change. So the second run is the last point-in-time proof before any
    // payload byte, not a freeze. A refusal there is caught in Save, which discards only the owned temporary, by its handle.
    private static void RequireChain(string canon, string parent, Seams? seams)
    {
        RequireCanonicalChain(canon, seams);
        if (!string.Equals(canon, parent, StringComparison.Ordinal)) RequireCaseInsensitiveChain(canon, seams);
    }

    // Step 2e. With the chain frozen by the held handle, walk the canonical spelling, not the typed one. A component that
    // cannot be read is a refusal; nothing is inferred.
    private static void RequireCanonicalChain(string canon, Seams? seams)
    {
        try { RequirePlainDirectories(canon, seams?.ChainAttributes); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new InvalidDataException("invalid_output_path", error); }
    }

    // Step 2f. Only when the typed parent differs from canon (in case alone, given the pin): every canonical component, the
    // held directory and each ancestor up to the volume root, must be case-insensitive, so the typed spelling names the
    // held directory. A failed open or query refuses.
    private static void RequireCaseInsensitiveChain(string canon, Seams? seams)
    {
        for (string? current = canon; current is not null; current = Path.GetDirectoryName(current))
        {
            using SafeFileHandle directory = CreateFileW(@"\\?\" + current, FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (directory.IsInvalid) throw new InvalidDataException("invalid_output_path", new Win32Exception(Marshal.GetLastPInvokeError()));
            byte[] flags = new byte[4]; // FILE_CASE_SENSITIVE_INFO: Flags
            if (!GetFileInformationByHandleEx(directory, FileCaseSensitiveInfo, flags, (uint)flags.Length))
                throw new InvalidDataException("invalid_output_path", new Win32Exception(Marshal.GetLastPInvokeError()));
            uint observed = BinaryPrimitives.ReadUInt32LittleEndian(flags);
            if (seams?.CaseSensitiveFlags is { } observeFlags) observed = observeFlags(current, observed);
            if ((observed & CaseSensitiveDirectory) != 0) throw new InvalidDataException("invalid_output_path");
        }
    }

    // Steps 3 and 7. Ordinal: the held directory's volume GUID final path plus the exact leaf. A rename that took a
    // tunnel-cache entry's name (MS-FSA 2.1.5.15.12) is therefore a safe refusal, never a match.
    internal static bool IsFinalPathOf(string? finalPath, string directory, string leaf) =>
        finalPath is not null && string.Equals(finalPath, Join(directory, leaf), StringComparison.Ordinal);

    internal static string Join(string directory, string name) =>
        directory.EndsWith(Path.DirectorySeparatorChar) ? directory + name : directory + Path.DirectorySeparatorChar + name;

    private static string? FinalPath(SafeFileHandle handle, uint flags)
    {
        var path = new StringBuilder(32768);
        uint count = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, flags);
        return count == 0 || count >= path.Capacity ? null : path.ToString();
    }

    private static string? FileSystemName(SafeFileHandle handle)
    {
        var name = new StringBuilder(MaximumPath + 1);
        return GetVolumeInformationByHandleW(handle, null, 0, out _, out _, out _, name, (uint)name.Capacity) ? name.ToString() : null;
    }

    private static void Write(SafeFileHandle temp, byte[] bytes)
    {
        try { RandomAccess.Write(temp, bytes, 0); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        { throw new IOException("output_unavailable", error); }
        if (!FlushFileBuffers(temp)) throw Unavailable(Marshal.GetLastPInvokeError());
    }

    // Step 6. FILE_RENAME_INFORMATION as the WDK defines it for 64-bit callers: 0 ReplaceIfExists (BOOLEAN), 8 RootDirectory,
    // 16 FileNameLength in bytes, 20 FileName; sizeof 24. RootDirectory is the held parent and FileName the bare leaf, so the
    // kernel resolves the target through the handle, not a path. The I/O manager copies this buffer before the call goes on.
    // Assumption (design v2.1 D2 correction): the temporary is opened WITHOUT FILE_FLAG_OVERLAPPED, so the I/O manager
    // completes the call before it returns, and the returned NTSTATUS is authoritative; the IO_STATUS_BLOCK is never read.
    // It lives in unmanaged memory because a STATUS_PENDING return would break that assumption and leave a later completion
    // free to write it: in that one case it is deliberately NOT freed (16 bytes retained per such call, not a global bound)
    // and the status is refused like any other non-zero one. On every other path, an exception before the call included,
    // it is freed here.
    private static int Rename(SafeFileHandle temp, SafeFileHandle held, string leaf)
    {
        ReadOnlySpan<byte> name = MemoryMarshal.AsBytes(leaf.AsSpan());
        byte[] info = new byte[24 + name.Length]; // zeroed, so the NUL after the name lies inside the buffer
        info[0] = 0; // ReplaceIfExists = FALSE
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(16), (uint)name.Length);
        name.CopyTo(info.AsSpan(20));
        IntPtr ioStatus = Marshal.AllocHGlobal(IoStatusBlockSize); // IO_STATUS_BLOCK: Status (union with Pointer) at 0, Information at 8
        bool added = false, pending = false;
        try
        {
            held.DangerousAddRef(ref added);
            BinaryPrimitives.WriteInt64LittleEndian(info.AsSpan(8), held.DangerousGetHandle().ToInt64());
            int status = NtSetInformationFile(temp, ioStatus, info, (uint)info.Length, FileRenameInformation);
            pending = status == StatusPending;
            return status;
        }
        finally
        {
            info.AsSpan().Clear();
            if (added) held.DangerousRelease();
            if (!pending) Marshal.FreeHGlobal(ioStatus);
        }
    }

    // NTSTATUS mapping, separate from GetLastError and decided from the returned status alone: STATUS_OBJECT_NAME_COLLISION
    // is output_exists; every other non-zero status, STATUS_PENDING included, is output_unavailable. The status travels in
    // the inner exception's Data.
    private static IOException RenameFailure(int status)
    {
        bool exists = status == StatusObjectNameCollision;
        string text = "0x" + status.ToString("X8", CultureInfo.InvariantCulture);
        var inner = new IOException("NtSetInformationFile status " + text);
        inner.Data[NtStatusDataKey] = text;
        return new IOException(exists ? "output_exists" : "output_unavailable", inner);
    }

    // Our own file, through its own handle: POSIX disposition only (design v2.1 D4). It never throws; after a disposition
    // is set, the handle is only closed.
    private static DiscardOutcome Discard(SafeFileHandle temp, Seams? seams)
    {
        if (seams?.SkipDisposition == true) return DiscardOutcome.NotDeleted;
        byte[] posix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(posix, DispositionDelete | DispositionPosixSemantics | DispositionIgnoreReadOnly);
        try
        {
            return SetFileInformationByHandle(temp, FileDispositionInfoEx, posix, (uint)posix.Length)
                ? DiscardOutcome.PosixDeleted : DiscardOutcome.NotDeleted;
        }
        catch (Exception error) when (error is TypeLoadException or MarshalDirectiveException or ExternalException or ObjectDisposedException)
        {
            return DiscardOutcome.NotDeleted;
        }
    }

    private static IOException Unavailable(int error) => new("output_unavailable", new Win32Exception(error));

    private const uint FileTraverse = 0x20, FileReadAttributes = 0x80, Synchronize = 0x00100000, DeleteAccess = 0x00010000,
        GenericWrite = 0x40000000;
    private const uint HeldAccess = FileTraverse | FileReadAttributes | Synchronize; // FILE_TRAVERSE is the share-checked bit
    private const uint TempAccess = GenericWrite | DeleteAccess;
    private const uint FileShareRead = 0x1, FileShareWrite = 0x2, FileShareDelete = 0x4;
    private const uint CreateNew = 1, OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80, FileFlagWriteThrough = 0x80000000, FileFlagBackupSemantics = 0x02000000,
        FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x10, FileAttributeReparsePoint = 0x400;
    private const uint VolumeNameDos = 0x0, VolumeNameGuid = 0x1; // each with FILE_NAME_NORMALIZED (0x0)
    private const int MaximumPath = 260;
    // Win32 FILE_INFO_BY_HANDLE_CLASS values, and the NT FILE_INFORMATION_CLASS value for the one NtSetInformationFile call.
    private const int FileAttributeTagInfo = 9, FileDispositionInfoEx = 21, FileCaseSensitiveInfo = 23;
    private const int FileRenameInformation = 10;
    private const uint CaseSensitiveDirectory = 0x1; // FILE_CS_FLAG_CASE_SENSITIVE_DIR
    private const uint DispositionDelete = 0x1, DispositionPosixSemantics = 0x2, DispositionIgnoreReadOnly = 0x10;
    private const int StatusObjectNameCollision = unchecked((int)0xC0000035), StatusPending = 0x103, IoStatusBlockSize = 16;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation,
        uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint count, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle file, StringBuilder? volumeName, uint volumeNameSize,
        out uint serialNumber, out uint maximumComponentLength, out uint fileSystemFlags, StringBuilder fileSystemName,
        uint fileSystemNameSize);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, [Out] byte[] information,
        uint size);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, byte[] information, uint size);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);
    // Returns an NTSTATUS, so there is no last error to capture. ioStatusBlock is unmanaged memory owned by Rename.
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtSetInformationFile(SafeFileHandle file, IntPtr ioStatusBlock, byte[] information, uint length,
        int informationClass);
}
