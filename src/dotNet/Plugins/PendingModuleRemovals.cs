using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DesktopAICompanion.Plugins
{
    /// <summary>
    /// A module's DLL is locked by the OS for as long as its AssemblyLoadContext is loaded in the current
    /// process, so an Uninstall action can never delete it immediately -- <see cref="MarkForRemoval"/> records
    /// the id instead, the caller restarts, and the NEXT process deletes it (<see cref="ProcessPending"/>)
    /// before it ever calls <c>ModuleHost.LoadFrom</c>, so the process doing the removing never re-locks the
    /// files it is trying to delete. The marker lives under <c>AppPaths.DataRoot</c>, never inside the
    /// <c>modules/</c> install folder itself, so it is never mistaken for a module id by the loader's
    /// directory scan.
    ///
    /// A removal that does not finish STAYS MARKED (F352). The marker used to be deleted whatever happened,
    /// so an uninstall whose folder was still locked -- a sibling instance holding the DLL, an indexer or
    /// antivirus mid-scan -- was simply lost: the half-deleted folder stayed, the loader loaded and locked
    /// what was left of it, the pane listed it as installed, and the user's one action had vanished
    /// without a word. Now the ids whose delete threw are written back, the launch that hit the lock skips
    /// loading them (<c>ModuleHost.LoadFrom</c> takes the list), and the next launch tries again. The
    /// counterpart is <see cref="Unmark"/>: a reinstall or update of the same id forgets the pending
    /// removal, because removals run before updates on every launch and would otherwise delete the module
    /// the user had just re-staged.
    ///
    /// A marker write that fails THROWS (RA-296, RA-318). MarkForRemoval, Unmark and MarkForUpdate all
    /// swallowed it, so pending-module-removals.txt held open by a sync client, or a data root on a
    /// read-only share, let the Modules pane announce the uninstall, prompt for the restart, and lose the
    /// user's one action behind a success message; the pane's "Couldn't uninstall/update" handlers existed
    /// and could never fire. The launch-time rewrite in ProcessPending is the one place that still catches,
    /// because it runs where nobody is watching and logs instead.
    /// </summary>
    internal static class PendingModuleRemovals
    {
        private static string FilePath { get { return Path.Combine(AppPaths.DataRoot, "pending-module-removals.txt"); } }

        /// <summary>Where every module's DATA directory lives: <c>&lt;data root&gt;\modules</c>.</summary>
        private static string DefaultModuleDataRoot { get { return Path.Combine(AppPaths.DataRoot, "modules"); } }

        /// <summary>Record an uninstall for the next launch. Throws when the marker cannot be written, so the
        /// pane reports the refusal instead of a success (RA-296).</summary>
        internal static void MarkForRemoval(string moduleId)
        {
            MarkForRemoval(moduleId, FilePath);
        }

        /// <summary>Marker path is explicit for the self-test, as it is on PendingModuleUpdates: AppPaths.DataRoot
        /// is resolved once per process, so a test cannot redirect it by setting the override late.</summary>
        internal static void MarkForRemoval(string moduleId, string markerPath)
        {
            if (string.IsNullOrWhiteSpace(moduleId)) return;
            var ids = new HashSet<string>(ReadIds(markerPath), StringComparer.OrdinalIgnoreCase);
            ids.Add(moduleId.Trim());
            WriteIds(markerPath, ids);
        }

        /// <summary>
        /// Forget a pending removal of <paramref name="moduleId"/>, because the user has since staged an
        /// update or a reinstall of the same id, or installed it anew (F352). Without this a retained marker
        /// outlived the re-install: removals run first on the next launch and would have deleted the module
        /// the user just put back, after which the staged update was "discarded" as belonging to nothing.
        /// Throws when the marker cannot be rewritten (RA-296): a stale removal marker is exactly the
        /// failure the caller has to know about, since the next launch would act on it.
        /// </summary>
        internal static void Unmark(string moduleId)
        {
            Unmark(moduleId, FilePath);
        }

        internal static void Unmark(string moduleId, string markerPath)
        {
            if (string.IsNullOrWhiteSpace(moduleId)) return;
            List<string> ids = ReadIds(markerPath);
            string wanted = moduleId.Trim();
            int before = ids.Count;
            ids.RemoveAll(id => string.Equals(id, wanted, StringComparison.OrdinalIgnoreCase));
            if (ids.Count == before) return;
            WriteIds(markerPath, ids);
        }

        /// <summary>
        /// Delete every pending module's install folder, data folder and staging copies, then rewrite the
        /// marker with the ids that could NOT be removed (or delete it when every one went). Call BEFORE
        /// <c>ModuleHost.LoadFrom</c> on every launch so a pending removal is never (re-)loaded by the very
        /// process trying to remove it, and hand the returned ids to LoadFrom so a folder whose delete threw
        /// is not loaded and locked again by this launch either. A no-op when nothing is pending.
        /// </summary>
        internal static IReadOnlyList<string> ProcessPending(string modulesRoot, Action<string> log)
        {
            return ProcessPending(modulesRoot, FilePath, DefaultModuleDataRoot, PendingModuleUpdates.DefaultStagingRoot, log);
        }

        internal static IReadOnlyList<string> ProcessPending(
            string modulesRoot,
            string markerPath,
            string moduleDataRoot,
            string stagingRoot,
            Action<string> log)
        {
            List<string> ids = ReadIds(markerPath);
            var unfinished = new List<string>();
            if (ids.Count == 0) return unfinished;
            foreach (string id in ids)
            {
                try
                {
                    // Install folder first, data folder second, in ONE try: a locked install folder must not
                    // cost the user their settings while the module itself survives.
                    string installDir = Path.Combine(modulesRoot, id);
                    if (Directory.Exists(installDir)) Directory.Delete(installDir, true);
                    // The data directory's PATH, never the mkdir helper CompanionHost.ModuleDataDir, which creates
                    // the folder it names: a removal must not bring into existence the thing it is about to delete.
                    string dataDir = Path.Combine(moduleDataRoot, CompanionHost.SafeId(id));
                    if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true);
                    // The staging folder's copies of the module go with it (RA-295). Swap's post-swap delete of
                    // the old copy is swallowed, so an <id>.replaced it stranded outlived the uninstall on every
                    // later launch: SweepStrands keeps a .replaced whose install folder is gone, by design, since
                    // for a module that is still installed it may be the only copy left. An uninstalled id is
                    // nobody's copy, and a leftover .staged for it would only be discarded by the update path.
                    if (!string.IsNullOrEmpty(stagingRoot))
                    {
                        string replaced = PendingModuleUpdates.ReplacedDirectory(id, stagingRoot);
                        if (Directory.Exists(replaced)) Directory.Delete(replaced, true);
                        string staged = PendingModuleUpdates.StagedDirectory(id, stagingRoot);
                        if (Directory.Exists(staged)) Directory.Delete(staged, true);
                    }
                    if (log != null) log("removed pending-uninstalled module '" + id + "'");
                }
                catch (Exception ex)
                {
                    unfinished.Add(id);
                    if (log != null)
                        log("could not finish removing '" + id + "': " + ex.Message +
                            " -- it stays marked, is not loaded this launch, and is retried next launch");
                }
            }
            // Rewrite rather than delete: the ids that DID go must not be retried, and the ones that did not
            // must not be forgotten. Caught and logged HERE, unlike the pane-facing marks above: this runs at
            // launch with nobody to tell, and a rewrite that fails leaves the old marker, whose finished ids
            // are retried next launch against folders that are already gone (a no-op).
            try { WriteIds(markerPath, unfinished); }
            catch (Exception ex)
            {
                if (log != null)
                    log("could not rewrite the removal marker: " + ex.Message +
                        " -- the finished ids are retried next launch against folders that are already gone");
            }
            return unfinished;
        }

        /// <summary>Write the id list, or delete the marker when it is empty. THROWS on a failed write or
        /// delete (RA-296): the callers that face the user report it, the launch path catches and logs.</summary>
        private static void WriteIds(string markerPath, IEnumerable<string> ids)
        {
            var kept = new List<string>();
            foreach (string id in ids) if (!string.IsNullOrWhiteSpace(id)) kept.Add(id.Trim());
            if (kept.Count == 0) { if (File.Exists(markerPath)) File.Delete(markerPath); return; }
            File.WriteAllLines(markerPath, kept, new UTF8Encoding(false));
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
