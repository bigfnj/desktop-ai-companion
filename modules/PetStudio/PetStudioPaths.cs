using System;

namespace DesktopAICompanion.PetStudioModule
{
    /// <summary>
    /// Where the "Open animations.xml…" dialog should start. Pure and UI-free so the module self-test can
    /// pin the policy directly, the same separation PetAnalyzer keeps from the window.
    ///
    /// The policy, in the user's words: default to the pet library, but once the author browses out to a
    /// work-in-progress folder elsewhere, remember that folder and reopen there next time. So a remembered
    /// directory wins when it still exists; otherwise fall back to the library, and only then to Documents.
    /// </summary>
    internal static class PetStudioPaths
    {
        internal const string LastOpenDirKey = "lastOpenDir";

        /// <param name="savedDir">The directory last browsed to, persisted from a previous Open (or "").</param>
        /// <param name="petsDir">The host's pet library (ICompanionManager.CompanionsDirectory), or "".</param>
        /// <param name="documentsDir">The final fallback when neither of the above resolves.</param>
        /// <param name="exists">Directory-existence probe (injected so the self-test needs no real disk).</param>
        internal static string ResolveInitialDir(string savedDir, string petsDir, string documentsDir,
            Func<string, bool> exists)
        {
            if (exists == null) exists = _ => false;

            if (!string.IsNullOrWhiteSpace(savedDir) && exists(savedDir)) return savedDir;
            if (!string.IsNullOrWhiteSpace(petsDir) && exists(petsDir)) return petsDir;
            return documentsDir ?? "";
        }
    }
}
