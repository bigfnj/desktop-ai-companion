using System;
using System.Collections.Generic;
using System.IO;

namespace DesktopAICompanion
{
    /// <summary>
    /// Wipe everything this app has written, so the next launch behaves like a first launch.
    ///
    /// Driven by the installer's "Clear all settings and modules" checkbox, which runs
    /// <c>DesktopAICompanion.exe --factory-reset</c> after laying down the new payload. The installer deliberately
    /// does NOT do the deleting itself: the two locations are decided by <see cref="AppPaths"/> at runtime
    /// (an override, a portable layout beside the exe, or the installed profile directory), and duplicating
    /// that logic in WiX would be a second, silently divergent definition of "the app's data".
    ///
    /// TWO locations, which is easy to get wrong:
    ///   * <see cref="AppPaths.DataRoot"/> -- settings.json, downloaded pets, fortunes, vectors, caches.
    ///   * <c>&lt;install&gt;\modules</c> -- downloaded MODULES live beside the exe, not under the profile.
    ///     The MSI ships no modules (packaging/runtime-files.txt has none), so nothing deleted here is ever
    ///     an installer-owned file, and a major upgrade leaves them untouched -- which is exactly why they
    ///     survive a reinstall and why "as if brand new" has to name them.
    ///
    /// Refuses rather than guesses. A path that is a drive root, a well-known profile or system folder, or
    /// simply not where this app keeps things, is left alone and reported: the cost of a wrong delete here
    /// is a user's documents, and the cost of refusing is that they clear it by hand.
    /// </summary>
    internal static class FactoryReset
    {
        internal const string Flag = "--factory-reset";

        /// <summary>Exit code when a live instance holds a slot and the wipe did not run (RA-245). Distinct
        /// from 1 ("ran, with errors") so a script can tell "close the app first" from a locked file.</summary>
        internal const int RunningInstanceExitCode = 3;

        internal static int Run()
        {
            var log = new List<string>();
            bool ok = true;

            string dataRoot = SafeGet(delegate { return AppPaths.DataRoot; });
            string modulesRoot = SafeGet(delegate { return Path.Combine(AppContext.BaseDirectory, "modules"); });
            // The staging folder beside modules\ (F353): a staged or half-swapped module update lives there,
            // and the MSI removes INSTALLFOLDER only when it is empty, so an orphan survived both "Clear all
            // settings and modules" and an uninstall.
            string stagingRoot = SafeGet(delegate { return Plugins.PendingModuleUpdates.DefaultStagingRoot; });

            // BOTH INSTANCE SLOTS ARE TAKEN FIRST, with no wait, and held for the length of the wipe (RA-245).
            // A running instance holds the authoritative settings in memory and writes them back on its next
            // save, so a reset under it deleted settings.json only for the live instance to recreate it, while
            // "completed with errors" went to a console nobody was reading. The MSI's CloseApplication closes
            // the app before its reset action runs; this is the hand-run `--factory-reset` and the Restart
            // Manager close a user skipped. A slot another process owns answers at once. A data root that
            // cannot be written answers the same null (RA-247), so that is told apart before the refusal
            // calls anything "running". Held, not released, so no instance can start under the wipe.
            IDisposable slot1 = null, slot2 = null;
            try
            {
                if (dataRoot != null)
                {
                    slot1 = Program.TryAcquireInstanceSlot(1, 0);
                    if (slot1 != null) slot2 = Program.TryAcquireInstanceSlot(2, 0);
                    if (slot1 == null || slot2 == null)
                    {
                        string fault;
                        if (!Program.TryProbeDataRootWritable(out fault))
                        {
                            Console.WriteLine("factory reset: the data folder cannot be written (" + fault + ")");
                            return 1;
                        }
                        Console.WriteLine("factory reset: refused, DesktopAICompanion is running; close it first");
                        return RunningInstanceExitCode;
                    }
                }
                // The two lock files are the leases this process is holding: zero bytes, recreated by every
                // launch, and deleting them under our own lease would count as two failures.
                ISet<string> heldLeases = dataRoot == null ? null : new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    Path.GetFileName(Program.InstanceSlotPath(1)) + ".lock",
                    Path.GetFileName(Program.InstanceSlotPath(2)) + ".lock",
                };

                ok &= Wipe(dataRoot, "settings and downloaded pets", log, heldLeases);
                ok &= Wipe(modulesRoot, "installed modules", log);
                ok &= Wipe(stagingRoot, "staged module updates", log);
                ok &= ClearStartupRegistration(log);
            }
            finally
            {
                if (slot2 != null) slot2.Dispose();
                if (slot1 != null) slot1.Dispose();
            }

            foreach (string line in log) Console.WriteLine(line);
            Console.WriteLine(ok ? "factory reset: done" : "factory reset: completed with errors");
            return ok ? 0 : 1;
        }

        /// <summary>
        /// The HKCU Run value this app writes goes with the files (RA-232). The installer's "Clear all settings
        /// and modules" promises a first launch, and a first launch does not autostart; the Preferences reset
        /// already cleared this value, so until now the two reset surfaces disagreed. A registry failure counts
        /// like a file that could not be deleted: reported, and the exit code says so.
        /// </summary>
        private static bool ClearStartupRegistration(List<string> log)
        {
            string detail;
            StartupRegistration.RemovalOutcome outcome = StartupRegistration.Remove(out detail);
            log.Add("  run at startup: " + detail);
            return outcome != StartupRegistration.RemovalOutcome.Failed;
        }

        private static string SafeGet(Func<string> get)
        {
            try { return get(); } catch { return null; }
        }

        private static bool Wipe(string root, string what, List<string> log)
        {
            return Wipe(root, what, log, null);
        }

        /// <summary>
        /// Delete a directory's CONTENTS, keeping the directory itself. Keeping it matters: the app may be
        /// mid-install and the installer's own file layout should not be disturbed, and an empty data root
        /// is exactly the state a first launch expects. <paramref name="keepFileNames"/> are file NAMES left
        /// in place and reported: the two instance-slot lock files this process itself holds for the length
        /// of the reset (RA-245); null keeps nothing.
        /// </summary>
        private static bool Wipe(string root, string what, List<string> log, ISet<string> keepFileNames)
        {
            string refusal;
            if (!IsSafeToWipe(root, out refusal))
            {
                log.Add("  skipped " + what + ": " + refusal);
                return true;   // refusing is a correct outcome, not a failure
            }
            if (!Directory.Exists(root))
            {
                log.Add("  " + what + ": nothing there already (" + root + ")");
                return true;
            }

            int files = 0, dirs = 0, failed = 0, kept = 0;
            foreach (string file in SafeList(delegate { return Directory.GetFiles(root); }, "the files of " + what, log, ref failed))
            {
                if (keepFileNames != null && keepFileNames.Contains(Path.GetFileName(file))) { kept++; continue; }
                try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); files++; }
                catch (Exception ex) { failed++; log.Add("    could not delete " + Path.GetFileName(file) + ": " + ex.Message); }
            }
            foreach (string dir in SafeList(delegate { return Directory.GetDirectories(root); }, "the folders of " + what, log, ref failed))
            {
                try { Directory.Delete(dir, true); dirs++; }
                catch (Exception ex) { failed++; log.Add("    could not delete " + Path.GetFileName(dir) + "\\: " + ex.Message); }
            }
            log.Add("  " + what + ": removed " + files + " file(s) and " + dirs + " folder(s) from " + root +
                (failed > 0 ? "  (" + failed + " could not be removed)" : "") +
                (kept > 0 ? "  (" + kept + " lock file(s) this reset holds were left)" : ""));
            return failed == 0;
        }

        /// <summary>List through <paramref name="list"/>; when the listing itself throws, count it as a failure
        /// and say so (F260). A root that could not be listed used to come back as an empty one, wiped with
        /// success, so the exit code said "clean" over a folder that was never touched.</summary>
        internal static string[] SafeList(Func<string[]> list, string what, List<string> log, ref int failed)
        {
            try { return list() ?? new string[0]; }
            catch (Exception ex)
            {
                failed++;
                if (log != null) log.Add("    could not list " + what + ": " + ex.Message);
                return new string[0];
            }
        }

        /// <summary>
        /// Whether a path is somewhere this app is allowed to empty. Deliberately strict: it must be a rooted
        /// path, more than a drive root, at least two levels deep, and not one of the folders a user would
        /// never forgive us for.
        /// </summary>
        internal static bool IsSafeToWipe(string path, out string refusal)
        {
            refusal = null;
            if (string.IsNullOrWhiteSpace(path)) { refusal = "no path"; return false; }

            // Reject anything not FULLY qualified before resolving it. Path.GetFullPath would otherwise
            // silently make a dangerous input look safe: "data" resolves against the current directory, and
            // "C:" is drive-RELATIVE and resolves to the process's current directory on C:, not to C:\ --
            // so both would come back as some deep, innocent-looking path and pass every check below.
            if (!Path.IsPathFullyQualified(path)) { refusal = "not an absolute path"; return false; }

            string full;
            try { full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch (Exception ex) { refusal = "unusable path (" + ex.Message + ")"; return false; }
            if (full.Length == 0) { refusal = "no path"; return false; }

            string root;
            try { root = Path.GetPathRoot(full); } catch { refusal = "unusable path"; return false; }
            if (string.IsNullOrEmpty(root)) { refusal = "not a rooted path"; return false; }
            if (string.Equals(full + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(full, root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            { refusal = "that is a drive root"; return false; }

            // At least two segments below the root, so a single mistaken folder name cannot take a whole
            // profile: "C:\Users" and "C:\Program Files" have one.
            string relative = full.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length)
                                  .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string[] segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                                               StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2) { refusal = "too close to the drive root"; return false; }

            foreach (Environment.SpecialFolder folder in ProtectedFolders)
            {
                string special;
                try { special = Environment.GetFolderPath(folder); } catch { continue; }
                if (string.IsNullOrEmpty(special)) continue;
                string normalised = special.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (normalised.Length > 0 && string.Equals(full, normalised, StringComparison.OrdinalIgnoreCase))
                { refusal = "that is " + folder; return false; }
            }
            return true;
        }

        private static readonly Environment.SpecialFolder[] ProtectedFolders =
        {
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86,
        };
    }
}
