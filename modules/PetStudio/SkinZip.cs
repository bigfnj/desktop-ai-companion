using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace DesktopAICompanion.PetStudioModule
{
    /// <summary>
    /// Unpacks a skin .zip for "Import Shimeji .zip". It does not call ZipFile.ExtractToDirectory, because that
    /// call refuses the WHOLE archive when one entry is named "/". Dropbox writes exactly that entry at the top of
    /// every shared-folder download, so each one failed with "Extracting Zip entry would have resulted in a file
    /// outside the specified destination directory". All 381 downloaded packs tested on 2026-11-04 did. Such an
    /// entry names the archive's own root, which is the destination itself, so skipping it creates nothing.
    ///
    /// Every other rule ExtractToDirectory applies still holds:
    /// <list type="bullet">
    /// <item>An entry that would land outside the destination (../x, /x) refuses the archive. Every entry is
    /// checked BEFORE anything is written, so a refused archive leaves the destination empty. ExtractToDirectory
    /// writes the entries ahead of the bad one first.</item>
    /// <item>Names are sanitised as .NET sanitises them on Windows: control characters and " * : &lt; &gt; ? |
    /// become '_'.</item>
    /// <item>An entry never overwrites a file already extracted, so a duplicate name refuses the archive.</item>
    /// </list>
    /// </summary>
    internal static class SkinZip
    {
        /// <summary>Extract <paramref name="zipPath"/> into <paramref name="destination"/>. Returns how many
        /// archive-root entries were skipped. Throws IOException for an entry that would land outside the
        /// destination, and writes nothing in that case.</summary>
        internal static int ExtractToDirectory(string zipPath, string destination)
        {
            string root = Path.GetFullPath(destination);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                root += Path.DirectorySeparatorChar;

            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                var plan = new List<KeyValuePair<ZipArchiveEntry, string>>();
                int skipped = 0;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (IsArchiveRootEntry(entry.FullName)) { skipped++; continue; }
                    string target = Path.GetFullPath(Path.Combine(root, Sanitize(entry.FullName)));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The .zip entry '" + entry.FullName
                            + "' would be extracted outside the destination folder, so nothing was extracted.");
                    plan.Add(new KeyValuePair<ZipArchiveEntry, string>(entry, target));
                }

                Directory.CreateDirectory(root);
                foreach (KeyValuePair<ZipArchiveEntry, string> item in plan)
                {
                    // A directory entry ("img/") has an empty Name and creates the folder only.
                    if (item.Key.Name.Length == 0) { Directory.CreateDirectory(item.Value); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Value));
                    item.Key.ExtractToFile(item.Value, false);
                }
                return skipped;
            }
        }

        /// <summary>True for an entry whose name is nothing but separators ("/", "\", "//"): it names the
        /// archive's root. An EMPTY name is not one of these: no archiver writes it, and it stays as
        /// ExtractToDirectory treats it.</summary>
        internal static bool IsArchiveRootEntry(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return false;
            foreach (char c in fullName)
                if (c != '/' && c != '\\') return false;
            return true;
        }

        private static string Sanitize(string fullName)
        {
            char[] chars = fullName.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (c < ' ' || c == '"' || c == '*' || c == ':' || c == '<' || c == '>' || c == '?' || c == '|')
                    chars[i] = '_';
            }
            return new string(chars);
        }
    }
}
