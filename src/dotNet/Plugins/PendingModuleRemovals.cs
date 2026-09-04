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
    /// </summary>
    internal static class PendingModuleRemovals
    {
        private static string FilePath { get { return Path.Combine(AppPaths.DataRoot, "pending-module-removals.txt"); } }

        internal static void MarkForRemoval(string moduleId)
        {
            if (string.IsNullOrWhiteSpace(moduleId)) return;
            var ids = new HashSet<string>(ReadIds(), StringComparer.OrdinalIgnoreCase);
            ids.Add(moduleId.Trim());
            try { File.WriteAllLines(FilePath, ids, new UTF8Encoding(false)); } catch { }
        }

        /// <summary>Delete every pending module's install folder and data folder, then clear the marker.
        /// Call BEFORE <c>ModuleHost.LoadFrom</c> on every launch so a pending removal is never (re-)loaded
        /// by the very process trying to remove it. A no-op when nothing is pending.</summary>
        internal static void ProcessPending(string modulesRoot, Action<string> log)
        {
            List<string> ids = ReadIds();
            if (ids.Count == 0) return;
            foreach (string id in ids)
            {
                try
                {
                    string installDir = Path.Combine(modulesRoot, id);
                    if (Directory.Exists(installDir)) Directory.Delete(installDir, true);
                    string dataDir = CompanionHost.ModuleDataDirectory(id);
                    if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true);
                    if (log != null) log("removed pending-uninstalled module '" + id + "'");
                }
                catch (Exception ex)
                {
                    if (log != null) log("could not finish removing '" + id + "': " + ex.Message);
                }
            }
            try { File.Delete(FilePath); } catch { }
        }

        private static List<string> ReadIds()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<string>();
                var result = new List<string>();
                foreach (string line in File.ReadAllLines(FilePath))
                    if (!string.IsNullOrWhiteSpace(line)) result.Add(line.Trim());
                return result;
            }
            catch { return new List<string>(); }
        }
    }
}
