using System;
using System.IO;

namespace DesktopAICompanion.Plugins
{
    /// <summary>
    /// One scratch directory convention for the host self-tests, because they were leaking.
    ///
    /// Six self-tests stage a module into %TEMP% before loading it (three of them through a collectible
    /// AssemblyLoadContext). Each rolled its own <c>Path.Combine(GetTempPath(), "dp-x-" + Guid)</c> and then
    /// either swallowed the delete or never attempted one, so every run left a directory behind: hundreds had
    /// accumulated by the time anyone looked.
    ///
    /// The delete genuinely CANNOT succeed on the current run for the ALC cases. Unloading a collectible
    /// context is asynchronous, so the module DLL is still mapped when the finally block runs and Windows
    /// refuses to remove the file. Retrying or sleeping would only make the self-test slower and still racy.
    /// So cleanup is deferred instead: each run sweeps what earlier runs left behind, which is the same trick
    /// <see cref="PendingModuleRemovals"/> uses for the same underlying reason.
    ///
    /// Nothing here throws. A locked directory belongs to a concurrently running instance and is simply left
    /// for the next sweep, which is also why the age threshold exists.
    /// </summary>
    internal static class SelfTestScratch
    {
        // The sweep matches on the PREFIX alone, deliberately. It used to also require a "-selftest-" marker
        // in the name, which coupled cleanup to a naming convention -- and the convention moved: 61 orphaned
        // roots were found a month later, including dp-petmgr-<guid> directories whose creating code no longer
        // exists anywhere in the tree. They could never be collected, because they did not carry the marker.
        //
        // Every "dp-" directory under %TEMP% is transient scratch owned by a self-test, so age is the only
        // safe question to ask about one. Nothing this app keeps in %TEMP% uses this prefix (ModulePaths uses
        // "DesktopAICompanion..."), and files are untouched -- only directories are enumerated.
        private const string Prefix = "dp-";

        // Long enough that a concurrent self-test (or a developer mid-debug) is never swept out from under
        // itself. Anything older than this cannot belong to a live run.
        private static readonly TimeSpan SweepAge = TimeSpan.FromHours(1);

        /// <summary>
        /// Sweep what previous runs left behind, then create and return a fresh scratch root for this run.
        /// <paramref name="tag"/> identifies the caller in the directory name (e.g. "aibrain").
        /// </summary>
        public static string Create(string tag)
        {
            SweepOldRoots();
            string root = Path.Combine(
                Path.GetTempPath(),
                Prefix + tag + "-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        /// <summary>
        /// Best-effort delete. Returns false with the reason rather than swallowing it, so a self-test can say
        /// that it could not clean up instead of reporting a clean run it did not have. A false here is not a
        /// test failure: for the ALC cases it is the expected outcome, and the next run's sweep collects it.
        /// </summary>
        public static bool TryRelease(string root, out string detail)
        {
            detail = null;
            if (string.IsNullOrEmpty(root)) return true;
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Remove scratch roots older than <see cref="SweepAge"/>. Returns how many went, for the self-test
        /// that asserts this actually works. Per-directory failures are ignored on purpose (still mapped, or
        /// owned by a live run).
        /// </summary>
        public static int SweepOldRoots()
        {
            int removed = 0;
            try
            {
                DateTime cutoff = DateTime.UtcNow - SweepAge;
                foreach (string dir in Directory.GetDirectories(Path.GetTempPath(), Prefix + "*"))
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(dir) > cutoff) continue;
                        Directory.Delete(dir, true);
                        removed++;
                    }
                    catch { }
                }
            }
            catch { }
            return removed;
        }

        /// <summary>Test seam: the age a directory must reach before a sweep will take it.</summary>
        public static TimeSpan Age { get { return SweepAge; } }

        /// <summary>Test seam: build a scratch-root name without creating it or sweeping.</summary>
        public static string NameFor(string tag)
        {
            return Prefix + tag + "-selftest-" + Guid.NewGuid().ToString("N");
        }
    }
}
