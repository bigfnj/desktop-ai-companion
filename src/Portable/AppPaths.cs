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
        public static bool IsPortable { get { return Current.IsPortable; } }
        public static bool IsDataRootOverridden { get { return Current.IsDataRootOverridden; } }
        public static bool LegacyMigrationEnabled { get { return !IsDataRootOverridden; } }

        public static string SettingsFile { get { return Path.Combine(DataRoot, "settings.json"); } }
        public static string AiSettingsFile { get { return Path.Combine(DataRoot, "ai-settings.json"); } }
        public static string ChatHistoryFile { get { return Path.Combine(DataRoot, "chat-history.json"); } }
        public static string FortunesDirectory { get { return Path.Combine(DataRoot, "fortunes"); } }
        public static string VectorCacheDirectory { get { return Path.Combine(DataRoot, "vectors"); } }
        public static string CatalogCacheDirectory { get { return Path.Combine(DataRoot, "catalog-cache"); } }

        /// <summary>
        /// Read-only content shipped beside the executable (portable zip only). These directories are
        /// absent in the lean MSI install, so every consumer must tolerate their non-existence. Bundled
        /// pets are still run through <see cref="CompanionXmlValidator"/> before use; bundled fortune files are
        /// loaded read-only in addition to the user's writable <see cref="FortunesDirectory"/>.
        /// </summary>
        public static string BundledPetsDirectory { get { return Path.Combine(ExecutableDirectory, "pets"); } }
        public static string BundledFortunesDirectory { get { return Path.Combine(ExecutableDirectory, "fortunes"); } }

        /// <summary>Writable pet library under the data root: where pets downloaded from the
        /// runtime catalog are installed, alongside the read-only bundled pets beside the exe.</summary>
        public static string LibraryPetsDirectory { get { return Path.Combine(DataRoot, "pets"); } }

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

        /// <summary>Old roaming root used by the AI settings/history/fortune files.</summary>
        public static string LegacyRoamingDataRoot
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DesktopPet");
            }
        }

        public static string LegacyFortunesDirectory
        {
            get { return Path.Combine(LegacyRoamingDataRoot, "fortunes"); }
        }

        public static string LegacyVectorCacheDirectory
        {
            // Pre-rename literal, like every other Legacy* member here: these name where data used to
            // live, so renaming them with the product makes each one point at its own destination and the
            // migration silently becomes a no-op that looks like it ran.
            get { return Path.Combine(LocalAppData, "DesktopPet", "vectors"); }
        }

        /// <summary>
        /// Return the canonical fortunes directory after one bounded, non-destructive migration
        /// attempt from the historical roaming location.
        /// </summary>
        public static string PrepareFortunesDirectory()
        {
            TryMigrateFilesOnce(
                FortunesDirectory,
                LegacyFortunesDirectory,
                "*.txt",
                128,
                4L * 1024L * 1024L,
                16L * 1024L * 1024L,
                LegacyMigrationEnabled);
            return FortunesDirectory;
        }

        /// <summary>
        /// Return the canonical vector-cache directory after one bounded, non-destructive
        /// migration attempt from the historical local-app-data location.
        /// </summary>
        public static string PrepareVectorCacheDirectory()
        {
            TryMigrateFilesOnce(
                VectorCacheDirectory,
                LegacyVectorCacheDirectory,
                "cache.bin",
                1,
                256L * 1024L * 1024L,
                256L * 1024L * 1024L,
                LegacyMigrationEnabled);
            return VectorCacheDirectory;
        }

        /// <summary>
        /// Copy a bounded set of top-level legacy files into a new data directory exactly once.
        /// Existing destination files are never overwritten and the legacy source is never changed.
        /// A data-root override disables production migrations so isolated tests cannot read a real
        /// user profile.
        /// </summary>
        internal static bool TryMigrateFilesOnce(
            string destinationDirectory,
            string legacyDirectory,
            string searchPattern,
            int maximumFiles,
            long maximumFileBytes,
            long maximumTotalBytes,
            bool enabled)
        {
            if (!enabled) return false;
            if (string.IsNullOrWhiteSpace(destinationDirectory) ||
                string.IsNullOrWhiteSpace(legacyDirectory) ||
                string.IsNullOrWhiteSpace(searchPattern) ||
                maximumFiles < 1 ||
                maximumFileBytes < 1 ||
                maximumTotalBytes < 1)
                return false;

            string destination;
            string legacy;
            try
            {
                destination = NormalizeDirectory(destinationDirectory);
                legacy = NormalizeDirectory(legacyDirectory);
            }
            catch
            {
                return false;
            }

            if (SameDirectory(destination, legacy)) return true;

            const string markerName = ".legacy-migration-v1.complete";
            const string lockName = ".legacy-migration-v1.lock";
            string markerPath = Path.Combine(destination, markerName);
            string lockPath = Path.Combine(destination, lockName);
            FileStream migrationLock = null;
            try
            {
                Directory.CreateDirectory(destination);
                migrationLock = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.WriteThrough);

                if (File.Exists(markerPath)) return true;

                if (Directory.Exists(legacy))
                {
                    var candidates = new List<string>();
                    foreach (string sourcePath in Directory.EnumerateFiles(
                        legacy,
                        searchPattern,
                        SearchOption.TopDirectoryOnly))
                    {
                        if (candidates.Count >= maximumFiles)
                            return false;
                        candidates.Add(sourcePath);
                    }
                    candidates.Sort(StringComparer.OrdinalIgnoreCase);

                    long copiedBytes = 0;
                    foreach (string sourcePath in candidates)
                    {
                        FileInfo source;
                        try
                        {
                            source = new FileInfo(sourcePath);
                            if ((source.Attributes & FileAttributes.ReparsePoint) != 0 ||
                                source.Length < 0 ||
                                source.Length > maximumFileBytes ||
                                source.Length > maximumTotalBytes - copiedBytes)
                                continue;
                        }
                        catch
                        {
                            continue;
                        }

                        string fileName = Path.GetFileName(source.FullName);
                        if (string.IsNullOrEmpty(fileName)) continue;
                        string destinationPath = Path.Combine(destination, fileName);
                        if (File.Exists(destinationPath)) continue;

                        long copied;
                        if (!TryCopyFileAtomic(
                                source.FullName,
                                destinationPath,
                                maximumFileBytes,
                                maximumTotalBytes - copiedBytes,
                                out copied))
                            return false;
                        copiedBytes += copied;
                    }
                }

                try
                {
                    using (var marker = new FileStream(
                        markerPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.Read,
                        1,
                        FileOptions.WriteThrough))
                        marker.Flush(true);
                }
                catch (IOException)
                {
                    if (!File.Exists(markerPath)) throw;
                }
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (migrationLock != null) migrationLock.Dispose();
                TryDelete(lockPath);
            }
        }

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

        private static bool TryCopyFileAtomic(
            string sourcePath,
            string destinationPath,
            long maximumFileBytes,
            long maximumRemainingBytes,
            out long copiedBytes)
        {
            copiedBytes = 0;
            string temporary = null;
            try
            {
                string destinationDirectory =
                    Path.GetDirectoryName(Path.GetFullPath(destinationPath));
                temporary = Path.Combine(
                    destinationDirectory,
                    "." + Path.GetFileName(destinationPath) + "." +
                    Guid.NewGuid().ToString("N") + ".tmp");

                using (var source = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    8192,
                    FileOptions.SequentialScan))
                using (var target = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    8192,
                    FileOptions.WriteThrough))
                {
                    if (source.Length > maximumFileBytes ||
                        source.Length > maximumRemainingBytes)
                        return true;

                    byte[] buffer = new byte[8192];
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        copiedBytes = checked(copiedBytes + read);
                        if (copiedBytes > maximumFileBytes ||
                            copiedBytes > maximumRemainingBytes)
                            return false;
                        target.Write(buffer, 0, read);
                    }
                    target.Flush(true);
                }

                try
                {
                    File.Move(temporary, destinationPath);
                    temporary = null;
                    return true;
                }
                catch (IOException)
                {
                    if (File.Exists(destinationPath)) return true;
                    throw;
                }
            }
            catch
            {
                copiedBytes = 0;
                return false;
            }
            finally
            {
                TryDelete(temporary);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }
    }
}
