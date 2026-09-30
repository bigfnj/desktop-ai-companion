using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DesktopAICompanion.ModuleKit
{
    /// <summary>
    /// Durable file writes: content lands in full or not at all, so a crash or a power cut can never leave a
    /// module's settings truncated. Write to a temp file in the SAME directory, flush through to disk, then
    /// swap it over the destination.
    ///
    /// Lifted from the app's own AppSettingsStore (two modules had copied it already). Prefer
    /// <see cref="TryWriteAllText"/>; it is the whole pattern in one call.
    /// </summary>
    public static class AtomicFile
    {
        private const int MoveFileReplaceExisting = 0x1;
        private const int MoveFileWriteThrough = 0x8;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

        /// <summary>Swap a temp file over a destination, keeping an optional backup. Falls back to
        /// MoveFileEx when File.Replace is unsupported (some network/virtual filesystems).</summary>
        public static void ReplaceExisting(string temporaryPath, string destinationPath, string backupPath,
            CancellationToken cancellationToken)
        {
            ReplaceExisting(temporaryPath, destinationPath, backupPath, cancellationToken, null);
        }

        /// <param name="replaceFile">Test seam: substitute for File.Replace. Null uses the real one.</param>
        public static void ReplaceExisting(string temporaryPath, string destinationPath, string backupPath,
            CancellationToken cancellationToken, Action<string, string, string, bool> replaceFile)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (replaceFile == null)
                    File.Replace(temporaryPath, destinationPath, backupPath, true);
                else
                    replaceFile(temporaryPath, destinationPath, backupPath, true);
                return;
            }
            catch (PlatformNotSupportedException) { }
            catch (NotSupportedException) { }
            catch (IOException) { }

            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(backupPath))
                File.Copy(destinationPath, backupPath, true);
            cancellationToken.ThrowIfCancellationRequested();
            // The extended-length form on BOTH paths. This fallback is reached where File.Replace is
            // unsupported, and it failed a second way on ordinary NTFS: once the module's data directory was
            // deep enough the plain path passed MAX_PATH and MoveFileEx refused it, while File.Replace and
            // the File.Copy above, which add the prefix themselves, kept working on the same path. That is
            // how a "durable" self-test line went red under a 107-character %TEMP% (N-gates-01).
            //
            // The P/Invoke stays rather than moving to File.Move(overwrite: true), which handles long paths
            // on its own: File.Move asks for MOVEFILE_COPY_ALLOWED and not MOVEFILE_WRITE_THROUGH, so it may
            // degrade to a copy-and-delete (not a rename, so not atomic) and it returns before the rename is
            // on disk, and the write-through rename is the durability this method exists to give.
            if (!MoveFileEx(ExtendedLengthPath(temporaryPath), ExtendedLengthPath(destinationPath),
                    MoveFileReplaceExisting | MoveFileWriteThrough))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        /// <summary>
        /// A path in the extended-length form Win32 accepts past MAX_PATH (260): <c>\\?\C:\...</c>, or
        /// <c>\\?\UNC\server\share\...</c> for a network path; one already in a device form is returned as
        /// it is. A process that has not opted into long paths through its manifest gets the 260-character
        /// limit on every plain path it hands to Win32 whatever the registry says, and this app has not;
        /// .NET's own file APIs add the prefix themselves, a raw P/Invoke does not. The prefix switches
        /// Win32's own normalisation OFF, so the path is fully qualified and normalised here first.
        /// </summary>
        private static string ExtendedLengthPath(string path)
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
                return full;
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
                return @"\\?\UNC\" + full.Substring(2);
            return @"\\?\" + full;
        }

        /// <summary>
        /// Write text durably, creating the directory as needed. Returns false rather than throwing — a
        /// module that cannot persist a setting should degrade, not crash the pet. Writes UTF-8 with NO BOM:
        /// a stray BOM has broken this app's own XML/JSON readers before.
        /// </summary>
        /// <param name="backupPath">Optional previous-content backup; null or "" for none.</param>
        public static bool TryWriteAllText(string path, string contents, string backupPath)
        {
            string temp = null;
            try
            {
                if (!Path.IsPathFullyQualified(path))
                    return false;

                path = Path.GetFullPath(path);
                string directory = Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);
                temp = Path.Combine(directory,
                    "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(contents ?? "");
                    writer.Flush();
                    stream.Flush(true);
                }

                if (!File.Exists(path))
                {
                    try
                    {
                        File.Move(temp, path);
                        temp = null;
                        return true;
                    }
                    catch (IOException)
                    {
                        // Another process may have created the destination after our existence check.
                        if (!File.Exists(path)) throw;
                    }
                }

                ReplaceExisting(temp, path, backupPath, CancellationToken.None);
                temp = null;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (temp != null)
                {
                    try { File.Delete(temp); } catch { }
                }
            }
        }
    }
}
