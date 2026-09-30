using System;
using System.IO;

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// Module-side replacement for the base <c>AppPaths</c> AI files. The module points this at its own
    /// storage (<c>host.GetStorage("aibrain")</c>) when it initialises, and the self-tests point it at
    /// throwaway roots of their own. Member names mirror the base <c>AppPaths</c> so the copied
    /// DesktopAICompanion.Ai code (AiSettings) rebinds by a simple AppPaths->AiPaths rename. The one-time import
    /// of an existing base ai-settings.json (DPAPI keys included) is AiBrainModule.MigrateFromBaseIfNeeded, run
    /// at Init before Load; nothing in here reads a legacy root. (A LegacyMigrationEnabled hard-coded false and
    /// a LegacyRoamingDataRoot that was this Root sat here until 2026-09-30, feeding a Load branch that could
    /// never run, F088.)
    ///
    /// There is NO temp fallback for an unset root any more. Until 2026-09-29 an unset root resolved to
    /// %TEMP%\DesktopAICompanion.AiBrain, which is where `--module-selftest=aibrain` left an ai-settings.json
    /// and its .lock on every run (N-gates-02): the convention host hands a module no storage, Init still
    /// called Load, and Load wrote defaults into a directory nobody owned or swept. The shipped host always
    /// provisions a storage directory (CompanionHost.GetStorage), so in the product an unset root is
    /// unreachable; a host that gives none now gets an engine that reads defaults and saves nothing, and says
    /// so through AiSettings.LoadWarning, rather than one that quietly persists into a temp folder.
    /// </summary>
    internal static class AiPaths
    {
        private static string _root;

        /// <summary>Point the engine at the module's storage root (host.GetStorage("aibrain").DataDirectory).</summary>
        public static void SetRoot(string root)
        {
            if (!string.IsNullOrWhiteSpace(root)) _root = root;
        }

        /// <summary>
        /// Replace the root and hand back the one it replaced, null included, so a probe that borrows the root
        /// can put back EXACTLY what it found. <see cref="SetRoot"/> ignores a blank on purpose (the live module
        /// must never lose its root to a bad host answer), which made restoring a previously-unset root a silent
        /// no-op and left the process pointing at a deleted temp directory after every self-test (F086).
        /// </summary>
        internal static string SwapRoot(string root)
        {
            string previous = _root;
            _root = string.IsNullOrWhiteSpace(root) ? null : root;
            return previous;
        }

        /// <summary>The root as set, or null when nothing has set one. For the self-test's restore assertion.</summary>
        internal static string CurrentRootForDiagnostics { get { return _root; } }

        /// <summary>True once a host or a probe has said where the files live.</summary>
        internal static bool HasRoot { get { return !string.IsNullOrWhiteSpace(_root); } }

        private static string Root
        {
            get
            {
                string r = _root;
                if (string.IsNullOrWhiteSpace(r))
                    throw new InvalidOperationException("The AI settings root has not been set by the host.");
                try { Directory.CreateDirectory(r); } catch { }
                return r;
            }
        }

        public static string AiSettingsFile { get { return Path.Combine(Root, "ai-settings.json"); } }
    }
}
