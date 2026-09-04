using System;
using System.IO;

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// Module-side replacement for the base <c>AppPaths</c> fortune/vector directories. The module points
    /// this at its own storage (<c>host.GetStorage("fortunes")</c>) when the engine goes live (S3d); until
    /// then a per-user temp fallback keeps the engine and its self-tests functional. Mirrors the semantics
    /// of <c>AppPaths.PrepareFortunesDirectory</c> / <c>PrepareVectorCacheDirectory</c> /
    /// <c>BundledFortunesDirectory</c> (the writable dirs are created on access).
    /// </summary>
    internal static class FortunePaths
    {
        private static string _root;

        /// <summary>Point the engine at the module's storage root (e.g. host.GetStorage("fortunes").DataDirectory).</summary>
        public static void SetRoot(string root)
        {
            if (!string.IsNullOrWhiteSpace(root)) _root = root;
        }

        private static string Root
        {
            get
            {
                string r = _root;
                if (string.IsNullOrWhiteSpace(r))
                    r = Path.Combine(Path.GetTempPath(), "DesktopAICompanion.Fortunes");
                return r;
            }
        }

        /// <summary>The user's writable fortune-pack folder (created on access).</summary>
        public static string FortunesDir { get { return Ensure(Path.Combine(Root, "fortunes")); } }

        /// <summary>Persistent embedding/vector cache folder (created on access; used by the smart layer).</summary>
        public static string VectorCacheDir { get { return Ensure(Path.Combine(Root, "vectors")); } }

        /// <summary>Read-only bundled packs. The module bundles none by default, so this normally does not
        /// exist and simply contributes nothing to the corpus.</summary>
        public static string BundledDir { get { return Path.Combine(Root, "bundled"); } }

        private static string Ensure(string dir)
        {
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }
}
