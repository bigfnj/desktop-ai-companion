using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// Executables are launched only from canonical absolute paths. A non-empty configured path is
    /// authoritative: if it is relative, malformed, missing, or names a different executable, the
    /// caller fails closed instead of silently launching something else from PATH.
    /// </summary>
    internal static class AiExecutablePolicy
    {
        private const uint DriveUnknown = 0;
        private const uint DriveNoRootDirectory = 1;
        private const uint DriveRemote = 4;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetDriveType(string rootPathName);

        private delegate FileAttributes FileAttributeReader(string path);

        public static string ResolveConfigured(string value, string expectedFileName)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !IsSimpleFileName(expectedFileName))
                return null;
            try
            {
                string canonical;
                if (!TryCanonicalizeLocalPath(
                        Environment.ExpandEnvironmentVariables(value.Trim()),
                        out canonical))
                    return null;
                if (!string.Equals(
                        Path.GetFileName(canonical),
                        expectedFileName,
                        StringComparison.OrdinalIgnoreCase) ||
                    !IsExistingLocalFileWithoutReparsePoints(canonical))
                    return null;
                return canonical;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Resolves an executable from an explicit PATH value without ever probing UNC, device,
        /// drive-relative, or mapped-network roots.
        /// </summary>
        public static string ResolveFromPath(
            string pathValue,
            string expectedFileName)
        {
            if (string.IsNullOrEmpty(pathValue) ||
                !IsSimpleFileName(expectedFileName))
                return null;

            foreach (string raw in pathValue.Split(Path.PathSeparator))
            {
                try
                {
                    string directory = Environment.ExpandEnvironmentVariables(
                        (raw ?? "").Trim().Trim('"'));
                    string canonicalDirectory;
                    if (!TryCanonicalizeLocalPath(
                            directory,
                            out canonicalDirectory))
                        continue;

                    string candidate;
                    if (!TryCanonicalizeLocalPath(
                            Path.Combine(
                                canonicalDirectory,
                                expectedFileName),
                            out candidate))
                        continue;
                    if (IsExistingLocalFileWithoutReparsePoints(candidate))
                        return candidate;
                }
                catch
                {
                    // A malformed PATH entry is skipped without affecting later local entries.
                }
            }
            return null;
        }

        internal static bool IsLocalAbsolutePath(string value)
        {
            try
            {
                string ignored;
                return TryCanonicalizeLocalPath(
                    Environment.ExpandEnvironmentVariables(value ?? ""),
                    out ignored);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsReparseFreeLocalFile(string value)
        {
            try
            {
                string canonical;
                return TryCanonicalizeLocalPath(
                        Environment.ExpandEnvironmentVariables(value ?? ""),
                        out canonical) &&
                    IsExistingLocalFileWithoutReparsePoints(canonical);
            }
            catch
            {
                return false;
            }
        }

        internal static bool ReparseScanStopsBeforeTraversalForDiagnostics()
        {
            int calls = 0;
            bool readPastReparsePoint = false;
            bool accepted = IsExistingLocalFileWithoutReparsePoints(
                @"C:\safe\junction\ollama.exe",
                delegate(string ignored)
                {
                    calls++;
                    if (calls == 1) return FileAttributes.Directory;
                    if (calls == 2)
                        return FileAttributes.Directory |
                            FileAttributes.ReparsePoint;
                    readPastReparsePoint = true;
                    return FileAttributes.Normal;
                });
            return !accepted && calls == 2 && !readPastReparsePoint;
        }

        private static bool TryCanonicalizeLocalPath(
            string value,
            out string canonical)
        {
            canonical = null;
            string candidate = (value ?? "").Trim();
            if (!IsDriveQualifiedAbsolutePath(candidate)) return false;

            // GetFullPath is purely lexical for a drive-qualified path. Check the shape both
            // before and after normalization so extended/device namespaces never reach a probe.
            string normalized = Path.GetFullPath(candidate);
            if (!IsDriveQualifiedAbsolutePath(normalized)) return false;

            uint driveType = GetDriveType(normalized.Substring(0, 3));
            if (driveType == DriveUnknown ||
                driveType == DriveNoRootDirectory ||
                driveType == DriveRemote)
                return false;

            canonical = normalized;
            return true;
        }

        private static bool IsExistingLocalFileWithoutReparsePoints(
            string canonical)
        {
            return IsExistingLocalFileWithoutReparsePoints(
                canonical,
                File.GetAttributes);
        }

        private static bool IsExistingLocalFileWithoutReparsePoints(
            string canonical,
            FileAttributeReader readAttributes)
        {
            string root = Path.GetPathRoot(canonical);
            if (string.IsNullOrEmpty(root) ||
                canonical.Length <= root.Length)
                return false;

            string current = root;
            string[] segments = canonical.Substring(root.Length).Split(
                new[]
                {
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                },
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return false;

            for (int index = 0; index < segments.Length; index++)
            {
                current = Path.Combine(current, segments[index]);
                FileAttributes attributes = readAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return false;

                bool isDirectory =
                    (attributes & FileAttributes.Directory) != 0;
                bool isLast = index == segments.Length - 1;
                if ((!isLast && !isDirectory) ||
                    (isLast && isDirectory))
                    return false;
            }
            return true;
        }

        private static bool IsDriveQualifiedAbsolutePath(string value)
        {
            return value != null &&
                value.Length >= 3 &&
                ((value[0] >= 'A' && value[0] <= 'Z') ||
                 (value[0] >= 'a' && value[0] <= 'z')) &&
                value[1] == Path.VolumeSeparatorChar &&
                (value[2] == Path.DirectorySeparatorChar ||
                 value[2] == Path.AltDirectorySeparatorChar);
        }

        private static bool IsSimpleFileName(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                string.Equals(
                    Path.GetFileName(value),
                    value,
                    StringComparison.Ordinal) &&
                value.IndexOf(Path.DirectorySeparatorChar) < 0 &&
                value.IndexOf(Path.AltDirectorySeparatorChar) < 0;
        }
    }
}
