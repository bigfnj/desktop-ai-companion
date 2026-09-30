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

        // The root used when nothing called SetRoot: a per-user TEMP folder. It STAYS (N-gates-02, the
        // decision is in docs/DESIGN-REGISTER.md): a module loaded by a host that hands it no storage still
        // needs somewhere for a vector cache if smart picks are on. What changed is that nothing reaches it by
        // accident any more -- reading the fortunes folder does not create it (FortunesDirPath), a host
        // without a settings store gets smart picks off, and the module's SelfTest runs the probe under a
        // scratch root it removes and asserts this one gained nothing.
        private static string FallbackRoot
        {
            get { return Path.Combine(Path.GetTempPath(), "DesktopAICompanion.Fortunes"); }
        }

        private static string Root
        {
            get
            {
                string r = _root;
                return string.IsNullOrWhiteSpace(r) ? FallbackRoot : r;
            }
        }

        /// <summary>Diagnostics: the root in effect, so a self-test that re-points the engine at a
        /// throwaway root can put the previous one back.</summary>
        internal static string RootForDiagnostics { get { return Root; } }

        /// <summary>Diagnostics: where an un-rooted engine would write, so a self-test can assert it did not.</summary>
        internal static string FallbackRootForDiagnostics { get { return FallbackRoot; } }

        /// <summary>The user's writable fortune-pack folder (created on access). For WRITERS: the folder button,
        /// the importer's destination, the download.</summary>
        public static string FortunesDir { get { return Ensure(FortunesDirPath); } }

        /// <summary>The same folder WITHOUT creating it, for READERS. The loader checks Directory.Exists
        /// itself, and a read that created the folder is what left an empty tree under the TEMP fallback on
        /// every self-test run under a host with no storage (N-gates-02).</summary>
        public static string FortunesDirPath { get { return Path.Combine(Root, "fortunes"); } }

        /// <summary>Create the user's drop folder under the CURRENT root. Init calls this once a real storage
        /// root has been set, so the folder exists from the first run ("Open fortunes folder" opens
        /// something, and the host's engine self-test sees the redirect took); under a host that hands no
        /// storage it is never called, so the TEMP fallback gains nothing.</summary>
        public static void CreateFortunesDir() { Ensure(FortunesDirPath); }

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
