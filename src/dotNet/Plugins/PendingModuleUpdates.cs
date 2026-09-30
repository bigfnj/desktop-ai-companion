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
            // A pending removal of the same id is forgotten (F352): removals run first on the next launch and
            // would delete the module this update is about to replace, after which the staged copy would be
            // "discarded" as belonging to nothing.
            PendingModuleRemovals.Unmark(moduleId);
        }

        /// <summary>Marker path is explicit for the self-test: <see cref="AppPaths.DataRoot"/> is resolved once
        /// per process at static init, so a test cannot redirect it by setting the override variable late.
        /// THROWS when the marker cannot be written (RA-296): the write sat in an empty catch, so the pane said
        /// "ready to apply" and restarted over a marker that did not exist, and the staged payload was swept
        /// an hour later as abandoned. The pane's catch reports it and discards the staging folder.</summary>
        internal static void MarkForUpdate(string moduleId, string markerPath)
        {
            if (string.IsNullOrWhiteSpace(moduleId)) return;
            List<string> known = ReadIds(markerPath) ?? new List<string>();
            var ids = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
            ids.Add(moduleId.Trim());
            File.WriteAllLines(markerPath, ids, new UTF8Encoding(false));
        }

        /// <summary>Swap every staged module into its install folder, then clear the marker. Call BEFORE
        /// <c>ModuleHost.LoadFrom</c> on every launch, and AFTER <see cref="PendingModuleRemovals"/> so an
        /// uninstall that raced an update wins instead of resurrecting the module. A no-op when nothing is
        /// pending.</summary>
        internal static void ProcessPending(string modulesRoot, Action<string> log)
        {
            ProcessPending(modulesRoot, DefaultStagingRoot, FilePath, log);
        }

        /// <summary>How old an UNMARKED staging folder must be before the sweep takes it: an unpack that
        /// died with its process is hours old by the next launch, while one a sibling process is writing
        /// right now is seconds old. The same rule SelfTestScratch applies to its roots.</summary>
        internal static readonly TimeSpan AbandonedStagingAge = TimeSpan.FromHours(1);

        internal static void ProcessPending(
            string modulesRoot,
            string stagingRoot,
            string markerPath,
            Action<string> log)
        {
            List<string> ids = ReadIds(markerPath);
            if (ids == null)
            {
                // An UNREADABLE marker is not an empty one (RA-297). ReadIds answered an empty list for both,
                // so a launch on which the marker read threw (an antivirus hold, a transient share violation)
                // walked on into SweepStrands with nothing marked and swept a marked payload whose swap had
                // been retrying for over an hour as abandoned. Nothing is swapped or swept on such a launch;
                // the marker and the payload are both still there for the next one.
                if (log != null) log("the update marker could not be read; nothing is swapped or swept this launch");
                return;
            }
            // Ids whose swap failed. Their staged payload and their marker line both survive, so the next
            // launch tries again instead of the user losing a verified download to a transient lock.
            var unfinished = new List<string>();
            foreach (string id in ids)
            {
                string staged = null;
                bool swapped = false;
                // The payload is deleted when it was applied OR when it is known to be useless (F353): the
                // "no longer installed" and "empty" branches used to leave it in place, bounded to one per
                // id but never collected, because nothing later ever visited an id the marker no longer named.
                bool discard = false;
                try
                {
                    staged = StagedDirectory(id, stagingRoot);
                    if (!Directory.Exists(staged))
                    {
                        if (log != null) log("update for '" + id + "' had no staged payload; skipped");
                        continue;
                    }
                    string installDir = Path.Combine(modulesRoot, id);

                    // Recover a strand. Swap moves the old copy to "<id>.replaced" before moving the new
                    // one in and rolls back if that second move throws -- but if the ROLLBACK also throws,
                    // the module's only remaining folder is that .replaced directory, which nothing on any
                    // later launch used to read. Then the module is simply gone, with its settings intact
                    // and nothing to load.
                    string strandedCopy = Path.Combine(stagingRoot, id + ReplacedSuffix);
                    if (!Directory.Exists(installDir) && Directory.Exists(strandedCopy))
                    {
                        try
                        {
                            Directory.Move(strandedCopy, installDir);
                            if (log != null) log("recovered module '" + id + "' from an interrupted update");
                        }
                        catch (Exception ex)
                        {
                            if (log != null) log("could not recover module '" + id + "': " + ex.Message);
                        }
                    }
                    // Never install a module the user has since uninstalled: a removal ran first this launch,
                    // and moving the staged copy in would bring it back from the dead.
                    if (!Directory.Exists(installDir))
                    {
                        discard = true;
                        if (log != null) log("module '" + id + "' is no longer installed; discarded its update");
                        continue;
                    }
                    if (!HasAnyFile(staged))
                    {
                        discard = true;
                        if (log != null) log("staged update for '" + id + "' was empty; kept the installed copy");
                        continue;
                    }
                    Swap(installDir, staged, id, stagingRoot, log);
                    swapped = true;
                }
                catch (Exception ex)
                {
                    unfinished.Add(id);
                    if (log != null)
                        log("could not update '" + id + "': " + ex.Message +
                            " -- keeping the staged copy so the next launch can retry");
                }
                finally
                {
                    // On success or a known-useless payload, never on a failed swap. Deleting unconditionally
                    // threw away a verified payload the user had waited for, alongside a marker that was
                    // deleted whatever happened, so a Directory.Move that lost to an indexer or antivirus cost
                    // them the whole download with one debug-window line, and the pane then offered the same
                    // update again forever.
                    if (swapped || discard)
                    {
                        try { if (staged != null && Directory.Exists(staged)) Directory.Delete(staged, true); } catch { }
                    }
                }
            }
            if (ids.Count > 0)
            {
                if (unfinished.Count > 0)
                {
                    // Rewrite rather than delete: the ids that DID swap must not be retried. Logged, not
                    // silent, when it fails: this runs at launch with nobody to tell, and the old marker's
                    // swapped ids are retried next launch against payloads that are already gone (skipped).
                    try { File.WriteAllLines(markerPath, unfinished, new UTF8Encoding(false)); }
                    catch (Exception ex) { if (log != null) log("could not rewrite the update marker: " + ex.Message); }
                }
                else
                {
                    try { File.Delete(markerPath); }
                    catch (Exception ex) { if (log != null) log("could not delete the update marker: " + ex.Message); }
                }
            }
            SweepStrands(modulesRoot, stagingRoot, unfinished, log);
            try
            {
                if (Directory.Exists(stagingRoot) &&
                    Directory.GetFileSystemEntries(stagingRoot).Length == 0)
                    Directory.Delete(stagingRoot);
            }
            catch { }
        }

        /// <summary>
        /// Staging entries nothing will ever visit again (F353), run on EVERY launch, marker or no marker:
        /// an <c>&lt;id&gt;.replaced</c> whose install folder is back in place (Swap's post-swap delete of the
        /// old copy is swallowed, and the id had left the marker, so a failure there was permanent), and an
        /// <c>&lt;id&gt;.staged</c> the marker does not name that is older than <see cref="AbandonedStagingAge"/>
        /// -- an unpack whose process died before MarkForUpdate, which the pane's own catches (F366, F367)
        /// cannot reach. Age gates the second so a fresh unmarked folder, which a sibling process could be
        /// writing, is left alone. An unmarked <c>.replaced</c> with NO install folder is left too: it may be
        /// the only copy of that module left, and the marked recovery path above is what puts it back.
        /// </summary>
        private static void SweepStrands(string modulesRoot, string stagingRoot, List<string> keepStaged, Action<string> log)
        {
            string[] entries;
            try { entries = Directory.Exists(stagingRoot) ? Directory.GetDirectories(stagingRoot) : new string[0]; }
            catch { return; }
            foreach (string dir in entries)
            {
                string name = Path.GetFileName(dir);
                try
                {
                    if (name.EndsWith(ReplacedSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        string id = name.Substring(0, name.Length - ReplacedSuffix.Length);
                        if (!Directory.Exists(Path.Combine(modulesRoot, id))) continue;
                        Directory.Delete(dir, true);
                        if (log != null) log("removed the previous copy of '" + id + "' that an earlier update left behind");
                    }
                    else if (name.EndsWith(StagedSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        string id = name.Substring(0, name.Length - StagedSuffix.Length);
                        bool kept = false;
                        foreach (string k in keepStaged) if (string.Equals(k, id, StringComparison.OrdinalIgnoreCase)) kept = true;
                        if (kept) continue;
                        if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) < AbandonedStagingAge) continue;
                        Directory.Delete(dir, true);
                        if (log != null) log("removed an abandoned staging folder for '" + id + "'");
                    }
                }
                catch (Exception ex)
                {
                    if (log != null) log("could not tidy '" + name + "' in the staging folder: " + ex.Message);
                }
            }
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

        /// <summary>Where a module's staged payload sits under <paramref name="stagingRoot"/>, contained to it.
        /// Internal so an uninstall can remove the staging copies of the id it deletes (RA-295).</summary>
        internal static string StagedDirectory(string moduleId, string stagingRoot)
        {
            return ContainedStagingPath(moduleId, stagingRoot, StagedSuffix);
        }

        /// <summary>Where Swap parks a module's previous copy under <paramref name="stagingRoot"/> (RA-295).</summary>
        internal static string ReplacedDirectory(string moduleId, string stagingRoot)
        {
            return ContainedStagingPath(moduleId, stagingRoot, ReplacedSuffix);
        }

        private static string ContainedStagingPath(string moduleId, string stagingRoot, string suffix)
        {
            if (string.IsNullOrWhiteSpace(moduleId))
                throw new ArgumentException("A module id is required.", "moduleId");
            string id = moduleId.Trim();
            string root = Path.GetFullPath(stagingRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string directory = Path.GetFullPath(Path.Combine(root, id + suffix));
            if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Staged module path escapes the staging folder.");
            return directory;
        }

        /// <summary>The marked ids, an empty list for no marker, or NULL for a marker that exists but could not
        /// be read (RA-297): the two used to be one answer, and only the caller can decide that an unreadable
        /// marker means "do nothing this launch" rather than "nothing is pending".</summary>
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
    }
}
