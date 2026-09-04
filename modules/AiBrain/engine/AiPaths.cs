using System.IO;

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// Module-side replacement for the base <c>AppPaths</c> AI files. The module points this at its own
    /// storage (<c>host.GetStorage("aibrain")</c>) when the brain goes live (S4b); until then a per-user
    /// temp fallback keeps the relocated engine and its self-tests functional. Member names mirror the base
    /// <c>AppPaths</c> so the copied DesktopAICompanion.Ai code (AiSettings) rebinds by a simple AppPaths->AiPaths
    /// rename. Legacy %APPDATA% migration is deliberately OFF here: importing an existing ai-settings.json
    /// (with the DPAPI keys) is the S4b migrator's job, not the dormant module's.
    /// </summary>
    internal static class AiPaths
    {
        private static string _root;

        /// <summary>Point the engine at the module's storage root (host.GetStorage("aibrain").DataDirectory).</summary>
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
                    r = Path.Combine(Path.GetTempPath(), "DesktopAICompanion.AiBrain");
                try { Directory.CreateDirectory(r); } catch { }
                return r;
            }
        }

        public static string AiSettingsFile { get { return Path.Combine(Root, "ai-settings.json"); } }
        public static bool LegacyMigrationEnabled { get { return false; } }
        public static string LegacyRoamingDataRoot { get { return Root; } }
    }
}
