using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

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
            MarkForRemoval(moduleId, markerPath, PauseBeforeReRead);
        }

        /// <summary>
        /// FAIL CLOSED on a marker that exists and cannot be read (N-host-runtime-01), the way
        /// <see cref="PendingModuleUpdates.MarkForUpdate(string, string, Action)"/> does since N-modules-update-all-03.
        /// ReadIds answered an empty list for such a marker, and this took it for "nothing pending" and rewrote
        /// the file with the one new id, so a scanner or sync client holding the file for one read dropped every
        /// uninstall marked before it: those modules then loaded again at the next start as if never removed.
        /// The read is tried again up to <see cref="PendingModuleUpdates.MarkerReReads"/> times (the same bound,
        /// so the two markers cannot drift apart), <paramref name="beforeReRead"/> running before each; still
        /// unreadable, it THROWS and leaves the file as it was, and the pane's "Couldn't uninstall" catch says
        /// so. Refused alternative: write the one id anyway, which is the defect.
        /// </summary>
        internal static void MarkForRemoval(string moduleId, string markerPath, Action beforeReRead)
        {
            if (string.IsNullOrWhiteSpace(moduleId)) return;
            List<string> marked = ReadIdsReReading(markerPath, beforeReRead);
            if (marked == null)
                throw new IOException("The list of uninstalls waiting for the next start could not be read, so it was " +
                    "left as it was. Try the uninstall again in a moment.");
            var ids = new HashSet<string>(marked, StringComparer.OrdinalIgnoreCase);
            ids.Add(moduleId.Trim());
            WriteIds(markerPath, ids);
        }

        /// <summary>Whether an uninstall of <paramref name="moduleId"/> waits for the next launch. The Modules
        /// pane's Update all leaves such a module out and names it: its MarkForUpdate would Unmark the removal
        /// (F352) and silently undo an uninstall the user asked for. A marker that cannot be read answers
        /// false, which is safe: Update all then goes on to MarkForUpdate, whose Unmark cannot read the marker
        /// either and THROWS (N-host-runtime-01), so that module is reported as not updated, the marker is left
        /// alone, and removals run first at the next launch, so the uninstall still wins over the update.</summary>
        internal static bool IsMarked(string moduleId)
        {
            return IsMarked(moduleId, FilePath);
        }

        internal static bool IsMarked(string moduleId, string markerPath)
        {
            if (string.IsNullOrWhiteSpace(moduleId)) return false;
            List<string> marked = ReadIds(markerPath);
            if (marked == null) return false;
            string wanted = moduleId.Trim();
            foreach (string id in marked)
                if (string.Equals(id, wanted, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
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
            Unmark(moduleId, markerPath, PauseBeforeReRead);
        }

        /// <summary>
        /// A marker that exists and cannot be read THROWS here too, after the same bounded re-reads
        /// (N-host-runtime-01), for the reason the write above throws: whether it names this id cannot be known,
        /// and if it does, the next launch deletes the module the user just reinstalled or updated. It used to
        /// read as an empty marker and return quietly, which is that outcome behind a success message. The
        /// install path's catch already words this ("if the module is missing after the restart, install it
        /// again"), and an update's catch reports the update as not done and discards its staged copy, so the
        /// earlier uninstall wins exactly as it would have. Refused alternative: the silent no-op it was.
        /// </summary>
        internal static void Unmark(string moduleId, string markerPath, Action beforeReRead)
        {
            if (string.IsNullOrWhiteSpace(moduleId)) return;
            List<string> ids = ReadIdsReReading(markerPath, beforeReRead);
            if (ids == null)
                throw new IOException("The list of uninstalls waiting for the next start could not be read, so an " +
                    "uninstall of this module may still run at the next start.");
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
            List<string> pending = ReadIds(FilePath);
            IReadOnlyList<string> unfinished = ProcessPending(modulesRoot, FilePath, DefaultModuleDataRoot, PendingModuleUpdates.DefaultStagingRoot, log);
            // A removed module's grant of the shared Claude Code sign-in goes with it (host 1.8.0), and so does a no the user
            // recorded for it, so a module installed later under the same id is asked afresh; the saved token itself stays for
            // the other modules allowed it, since uninstalling one module is not a Remove token. Only for the ids removed.
            foreach (string id in pending)
            {
                bool left = false;
                foreach (string u in unfinished) if (string.Equals(u, id, StringComparison.OrdinalIgnoreCase)) left = true;
                if (!left && (HostCredentialStore.IsGranted(HostCredentialStore.DefaultDirectory, id) ||
                              HostCredentialStore.IsNotAsked(HostCredentialStore.DefaultDirectory, id)))
                    HostCredentialStore.ForgetGrant(HostCredentialStore.DefaultDirectory, id,
                        delegate (string moduleId, string line) { if (log != null) log(line + " ('" + moduleId + "' was uninstalled)"); });
            }
            return unfinished;
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
            if (ids == null)
            {
                // An UNREADABLE marker is not an empty one, and not a reason to fail the launch either
                // (N-host-runtime-01): nothing is removed and nothing is rewritten, so every pending uninstall is
                // still marked for the next launch. The modules it names load this once, as they did before the
                // marker existed; a NullReferenceException here would instead cost the launch every module.
                if (log != null) log("the removal marker could not be read; nothing is removed this launch, and it is tried again at the next");
                return unfinished;
            }
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

        /// <summary>The marked ids, an empty list for no marker, or NULL for a marker that exists but could not
        /// be read (N-host-runtime-01, the shape PendingModuleUpdates took for RA-297): the two used to be one
        /// answer, so a held marker was overwritten as if nothing had been pending.</summary>
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
            catch { return null; }
        }

        /// <summary>ReadIds, read again up to <see cref="PendingModuleUpdates.MarkerReReads"/> times while it answers
        /// null: a scanner's hold on a file just written usually ends within a few tens of milliseconds. Still
        /// null after that means unreadable, and the caller refuses to write.</summary>
        private static List<string> ReadIdsReReading(string markerPath, Action beforeReRead)
        {
            List<string> ids = ReadIds(markerPath);
            for (int reRead = 0; ids == null && reRead < PendingModuleUpdates.MarkerReReads; reRead++)
            {
                if (beforeReRead != null) beforeReRead();
                ids = ReadIds(markerPath);
            }
            return ids;
        }

        /// <summary>The shipped pause before each re-read: the update marker's, on the UI thread the pane calls from.</summary>
        private static void PauseBeforeReRead()
        {
            Thread.Sleep(PendingModuleUpdates.MarkerReReadDelayMilliseconds);
        }
    }
}
