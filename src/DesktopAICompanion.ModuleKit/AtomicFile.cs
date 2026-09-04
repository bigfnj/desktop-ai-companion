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
            if (!MoveFileEx(temporaryPath, destinationPath, MoveFileReplaceExisting | MoveFileWriteThrough))
                throw new Win32Exception(Marshal.GetLastWin32Error());
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
