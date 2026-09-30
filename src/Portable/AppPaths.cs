using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace DesktopAICompanion
{
    /// <summary>
    /// Immutable, process-independent result of resolving DesktopAICompanion's executable and data roots.
    /// Kept separate from <see cref="AppPaths"/> so path policy can be tested without changing the
    /// current process or touching the real user profile.
    /// </summary>
    internal sealed class AppPathLayout
    {
        public string ExecutableDirectory { get; private set; }
        public string DataRoot { get; private set; }
        public bool IsInstalled { get; private set; }
        public bool IsDataRootOverridden { get; private set; }

        public bool IsPortable { get { return !IsInstalled; } }

        internal AppPathLayout(
            string executableDirectory,
            string dataRoot,
            bool isInstalled,
            bool isDataRootOverridden)
        {
            ExecutableDirectory = executableDirectory;
            DataRoot = dataRoot;
            IsInstalled = isInstalled;
            IsDataRootOverridden = isDataRootOverridden;
        }
    }

    /// <summary>
    /// Canonical application paths. Installed builds keep mutable data in
    /// <c>%LOCALAPPDATA%\DesktopAICompanion</c>; a portable copy keeps it under an absolute
    /// <c>data</c> directory beside the executable. Neither mode depends on the current working
    /// directory. Set <c>DESKTOP_AI_COMPANION_DATA_ROOT</c> to an absolute temporary directory for isolated
    /// smoke tests.
    /// </summary>
    internal static class AppPaths
    {
        internal const string DataRootOverrideEnvironmentVariable = "DESKTOP_AI_COMPANION_DATA_ROOT";
        internal const string PortableMarkerFileName = "DesktopAICompanion.portable";
        internal static readonly string ProductName = GetAssemblyProductName();

        private static readonly string LocalAppData =
            Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        private static readonly AppPathLayout Current = ResolveCurrentProcess();

        public static string ExecutableDirectory { get { return Current.ExecutableDirectory; } }
        public static string DataRoot { get { return Current.DataRoot; } }
        public static bool IsInstalled { get { return Current.IsInstalled; } }
        public static bool IsDataRootOverridden { get { return Current.IsDataRootOverridden; } }
        public static bool LegacyMigrationEnabled { get { return !IsDataRootOverridden; } }

        public static string SettingsFile { get { return Path.Combine(DataRoot, "settings.json"); } }
        /// <summary>The base ai-settings.json. Read by LocalData's one-time random-drop bridge and copied once
        /// into the AiBrain module's store by AiBrainModule.MigrateFromBaseIfNeeded; nothing in the host
        /// writes it any more.</summary>
        public static string AiSettingsFile { get { return Path.Combine(DataRoot, "ai-settings.json"); } }

        /// <summary>
        /// Read-only content shipped beside the executable (portable zip only). The directory is absent in
        /// the lean MSI install, so every consumer must tolerate its non-existence. Bundled pets are still
        /// run through <see cref="CompanionXmlValidator"/> before use.
        /// </summary>
        public static string BundledPetsDirectory { get { return Path.Combine(ExecutableDirectory, "companions"); } }

        /// <summary>Writable pet library under the data root: where pets downloaded from the
        /// runtime catalog are installed, alongside the read-only bundled pets beside the exe.</summary>
        public static string LibraryPetsDirectory { get { return Path.Combine(DataRoot, "companions"); } }

        /// <summary>
        /// Legacy mapped configuration files considered for the one-time settings migration.
        /// Candidates are anchored to known application/user locations and never to the caller's
        /// mutable current directory.
        /// </summary>
        public static IList<string> LegacySettingsFiles
        {
            get
            {
                var result = new List<string>();
                if (!LegacyMigrationEnabled) return result;
                AddUnique(result, Path.Combine(ExecutableDirectory, "DesktopPet.config"));
                AddUnique(result, Path.Combine(LocalAppData, "DesktopPet", "DesktopPet.config"));
                return result;
            }
        }

        // The pre-1.0 roaming-root migration cluster was here: LegacyRoamingDataRoot, LegacyFortunesDirectory,
        // PrepareFortunesDirectory and the bounded, lock-and-marker TryMigrateFilesOnce / TryCopyFileAtomic /
        // TryDelete it drove, plus the ChatHistoryFile, FortunesDirectory, CatalogCacheDirectory and
        // BundledFortunesDirectory paths and a static IsPortable. The vector-cache half went on 2026-09-27
        // with a note that "the fortunes pair above is deliberately NOT removed with it: that one still has
        // callers"; it had none. The Fortunes module owns fortune storage (FortunePaths.cs, over
        // host.GetStorage("fortunes")), the catalog cache is RemoteCatalogClient's, and the chat history is
        // the AiBrain module's, so PrepareFortunesDirectory was reachable from nothing in the product and
        // the ~200 lines of copy logic behind it were kept green by a CoreTests group and by nothing the
        // app does. Both went together (F357). LegacyMigrationEnabled and LegacySettingsFiles above are the
        // live remainder: the settings-file import still runs on a fresh data root.

        /// <summary>
        /// Resolve a path layout using only supplied values. This is the single product-mode rule
        /// used by production and by the pure regression tests.
        /// </summary>
        internal static AppPathLayout Resolve(
            string executableDirectory,
            string localAppData,
            string overrideRoot,
            bool portableMarkerPresent)
        {
            if (string.IsNullOrWhiteSpace(executableDirectory))
                throw new ArgumentException("Executable directory is required.", "executableDirectory");
            if (string.IsNullOrWhiteSpace(localAppData))
                throw new ArgumentException("Local application-data directory is required.", "localAppData");

            string exe = NormalizeDirectory(executableDirectory);
            string local = NormalizeDirectory(localAppData);

            string legacyInstall = NormalizeDirectory(Path.Combine(local, "DesktopPet"));
            string msiInstall = NormalizeDirectory(
                Path.Combine(local, "Programs", ProductName));

            bool installed = !portableMarkerPresent &&
                (SameDirectory(exe, legacyInstall) || SameDirectory(exe, msiInstall));

            string dataRoot;
            bool isDataRootOverridden = !string.IsNullOrWhiteSpace(overrideRoot);
            if (isDataRootOverridden)
            {
                if (!IsFullyQualifiedPath(overrideRoot))
                    throw new ArgumentException(
                        DataRootOverrideEnvironmentVariable + " must be an absolute path.",
                        "overrideRoot");
                dataRoot = NormalizeDirectory(overrideRoot);
            }
            else
            {
                dataRoot = installed
                    ? NormalizeDirectory(Path.Combine(local, "DesktopAICompanion"))
                    : NormalizeDirectory(Path.Combine(exe, "data"));
            }

            return new AppPathLayout(
                exe,
                dataRoot,
                installed,
                isDataRootOverridden);
        }

        private static AppPathLayout ResolveCurrentProcess()
        {
            string executableDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string overrideRoot =
                Environment.GetEnvironmentVariable(DataRootOverrideEnvironmentVariable);

            // A malformed environment override must not prevent the application from launching.
            if (!string.IsNullOrWhiteSpace(overrideRoot) && !IsFullyQualifiedPath(overrideRoot))
                overrideRoot = null;

            bool marker = File.Exists(
                Path.Combine(executableDirectory, PortableMarkerFileName));
            return Resolve(executableDirectory, LocalAppData, overrideRoot, marker);
        }

        internal static bool IsFullyQualifiedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                return false;

            string root;
            try { root = Path.GetPathRoot(path); }
            catch { return false; }

            if (string.IsNullOrEmpty(root))
                return false;

            // Path.IsPathRooted deliberately accepts drive-relative ("C:folder") and
            // current-drive-rooted ("\folder") forms. Both still depend on ambient process
            // state, so an isolation override must reject them. Fully qualified drive, UNC,
            // and extended paths have a larger root.
            return !(root.Length == 2 && root[1] == Path.VolumeSeparatorChar) &&
                   !(root.Length == 1 &&
                     (root[0] == Path.DirectorySeparatorChar ||
                      root[0] == Path.AltDirectorySeparatorChar));
        }

        private static string NormalizeDirectory(string path)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full;
        }

        private static string GetAssemblyProductName()
        {
            object[] attributes = typeof(AppPaths).Assembly.GetCustomAttributes(
                typeof(AssemblyProductAttribute),
                false);
            if (attributes.Length != 1 ||
                string.IsNullOrWhiteSpace(
                    ((AssemblyProductAttribute)attributes[0]).Product))
            {
                throw new InvalidOperationException(
                    "The application assembly must define exactly one non-empty product name.");
            }
            return ((AssemblyProductAttribute)attributes[0]).Product.Trim();
        }

        private static bool SameDirectory(string left, string right)
        {
            return string.Equals(
                NormalizeDirectory(left),
                NormalizeDirectory(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static void AddUnique(ICollection<string> paths, string candidate)
        {
            string full;
            try { full = Path.GetFullPath(candidate); }
            catch { return; }

            foreach (string existing in paths)
            {
                if (string.Equals(existing, full, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            paths.Add(full);
        }
    }
}
