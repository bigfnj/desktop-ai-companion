using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DesktopAICompanion.Plugins
{
    /// <summary>
    /// The update half of <see cref="PendingModuleRemovals"/>, and it exists for the same reason: a module's
    /// DLL is locked for as long as its AssemblyLoadContext is loaded, so the process the user clicked
    /// "Update" in can never overwrite the files it is replacing. The download is verified and unpacked into a
    /// STAGING folder, the id is recorded, the caller restarts, and the next process swaps the staged payload
    /// into place (<see cref="ProcessPending"/>) before <c>ModuleHost.LoadFrom</c> locks anything.
    ///
    /// Two placement rules matter. The staging root sits beside <c>modules/</c> and NOT inside it, because
    /// <c>ModuleHost.LoadFrom</c> loads every subdirectory it finds and would happily load a half-written
    /// "aibrain.new" as a module; it is under <see cref="AppContext.BaseDirectory"/> rather than the data root
    /// so the swap is a same-volume <see cref="Directory.Move"/> (a portable install can sit on a different
    /// drive than <c>%LOCALAPPDATA%</c>). The marker file follows removals and lives in the data root.
    ///
    /// Unlike an uninstall, an update deliberately leaves the module's DATA directory alone: keeping settings,
    /// API keys and history across an update is the entire point of having an update path instead of telling
    /// people to uninstall and reinstall.
    /// </summary>
    internal static class PendingModuleUpdates
    {
        private const string StagedSuffix = ".staged";
        private const string ReplacedSuffix = ".replaced";

        private static string FilePath { get { return Path.Combine(AppPaths.DataRoot, "pending-module-updates.txt"); } }

        /// <summary>Staging root: beside the modules folder, never inside it (the loader scans subdirectories).</summary>
        internal static string DefaultStagingRoot
        {
            get { return Path.Combine(AppContext.BaseDirectory, "module-staging"); }
        }

        /// <summary>
        /// An empty directory to unpack a verified module payload into, replacing any earlier abandoned
        /// staging for the same id. Throws if it cannot be created: staging nothing and then marking the id
        /// would turn the next launch into a no-op the user reads as a silent failure.
        /// </summary>
        internal static string PrepareStagingDirectory(string moduleId)
        {
            return PrepareStagingDirectory(moduleId, DefaultStagingRoot);
        }

        internal static string PrepareStagingDirectory(string moduleId, string stagingRoot)
        {
            string staged = StagedDirectory(moduleId, stagingRoot);
            if (Directory.Exists(staged)) Directory.Delete(staged, true);
            Directory.CreateDirectory(staged);
            return staged;
        }

        internal static void MarkForUpdate(string moduleId)
        {
            MarkForUpdate(moduleId, FilePath);
        }

        /// <summary>Marker path is explicit for the self-test: <see cref="AppPaths.DataRoot"/> is resolved once
        /// per process at static init, so a test cannot redirect it by setting the override variable late.</summary>
        internal static void MarkForUpdate(string moduleId, string markerPath)
        {
            if (string.IsNullOrWhiteSpace(moduleId)) return;
            var ids = new HashSet<string>(ReadIds(markerPath), StringComparer.OrdinalIgnoreCase);
            ids.Add(moduleId.Trim());
            try { File.WriteAllLines(markerPath, ids, new UTF8Encoding(false)); } catch { }
        }

        /// <summary>Swap every staged module into its install folder, then clear the marker. Call BEFORE
        /// <c>ModuleHost.LoadFrom</c> on every launch, and AFTER <see cref="PendingModuleRemovals"/> so an
        /// uninstall that raced an update wins instead of resurrecting the module. A no-op when nothing is
        /// pending.</summary>
        internal static void ProcessPending(string modulesRoot, Action<string> log)
        {
            ProcessPending(modulesRoot, DefaultStagingRoot, FilePath, log);
        }

        internal static void ProcessPending(
            string modulesRoot,
            string stagingRoot,
            string markerPath,
            Action<string> log)
        {
            List<string> ids = ReadIds(markerPath);
            if (ids.Count == 0) return;
            foreach (string id in ids)
            {
                string staged = null;
                try
                {
                    staged = StagedDirectory(id, stagingRoot);
                    if (!Directory.Exists(staged))
                    {
                        if (log != null) log("update for '" + id + "' had no staged payload; skipped");
                        continue;
                    }
                    string installDir = Path.Combine(modulesRoot, id);
                    // Never install a module the user has since uninstalled: a removal ran first this launch,
                    // and moving the staged copy in would bring it back from the dead.
                    if (!Directory.Exists(installDir))
                    {
                        if (log != null) log("module '" + id + "' is no longer installed; discarded its update");
                        continue;
                    }
                    if (!HasAnyFile(staged))
                    {
                        if (log != null) log("staged update for '" + id + "' was empty; kept the installed copy");
                        continue;
                    }
                    Swap(installDir, staged, id, stagingRoot, log);
                }
                catch (Exception ex)
                {
                    if (log != null) log("could not update '" + id + "': " + ex.Message);
                }
                finally
                {
                    try { if (staged != null && Directory.Exists(staged)) Directory.Delete(staged, true); } catch { }
                }
            }
            try { File.Delete(markerPath); } catch { }
            try
            {
                if (Directory.Exists(stagingRoot) &&
                    Directory.GetFileSystemEntries(stagingRoot).Length == 0)
                    Directory.Delete(stagingRoot);
            }
            catch { }
        }

        /// <summary>
        /// Move the old install aside, move the staged copy in, then delete the old copy. The detour exists so
        /// a failure is recoverable: deleting first and then failing the move would leave the user with no
        /// module at all, which is a worse outcome than the stale one they were trying to replace.
        /// </summary>
        private static void Swap(
            string installDir,
            string staged,
            string id,
            string stagingRoot,
            Action<string> log)
        {
            string replaced = Path.Combine(stagingRoot, id + ReplacedSuffix);
            if (Directory.Exists(replaced)) Directory.Delete(replaced, true);
            Directory.Move(installDir, replaced);
            try
            {
                Directory.Move(staged, installDir);
            }
            catch
            {
                try { if (!Directory.Exists(installDir)) Directory.Move(replaced, installDir); } catch { }
                throw;
            }
            try { Directory.Delete(replaced, true); } catch { }
            if (log != null) log("updated module '" + id + "'");
        }

        private static bool HasAnyFile(string directory)
        {
            try { return Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length > 0; }
            catch { return false; }
        }

        private static string StagedDirectory(string moduleId, string stagingRoot)
        {
            if (string.IsNullOrWhiteSpace(moduleId))
                throw new ArgumentException("A module id is required.", "moduleId");
            string id = moduleId.Trim();
            string root = Path.GetFullPath(stagingRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string directory = Path.GetFullPath(Path.Combine(root, id + StagedSuffix));
            if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Staged module path escapes the staging folder.");
            return directory;
        }

        private static List<string> ReadIds(string markerPath)
        {
            try
            {
                if (!File.Exists(markerPath)) return new List<string>();
                var result = new List<string>();
                foreach (string line in File.ReadAllLines(markerPath))
                    if (!string.IsNullOrWhiteSpace(line)) result.Add(line.Trim());
                return result;
            }
            catch { return new List<string>(); }
        }
    }
}
