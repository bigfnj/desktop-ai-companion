using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DesktopAICompanion
{
    /// <summary>
    /// Shared pet enumeration, naming, and on-disk XML resolution, used by both the Options gallery
    /// (FormOptions) and the tray menu (ContextMenus) plus the loaded-pet-type registry, so pet ids,
    /// display names, and xml lookup live in one place. A pet "type" is a folder id under a pets root
    /// (AppPaths.BundledPetsDirectory beside the exe, then AppPaths.LibraryPetsDirectory for downloads),
    /// each folder holding an animations.xml. The built-in default (eSheep) has a null id.
    /// </summary>
    internal static class CompanionCatalog
    {
        internal sealed class CompanionInfo
        {
            public string Id;          // folder/catalog id; null for the built-in default
            public string DisplayName;
            public string XmlPath;     // null for the built-in default
            public bool IsBuiltIn;
        }

        internal const int MaximumPetXmlBytes = 12 * 1024 * 1024;   // matches AppSettingsDocument.MaximumXmlBytes

        // Explicit id for the built-in default pet (the embedded eSheep). Distinct from "" which means
        // "whatever pet is currently active" — a card/tray "Add" must add the specific pet it names,
        // not the active one, so those sites pass this id for the built-in.
        internal const string BuiltInPetId = "eSheep";

        // The colored-sheep pets ship as "<colour>_sheep" but each has its own character name in its
        // animations.xml. The thumbnail already shows the colour, so we show the name instead of a
        // redundant "Pink Sheep". Keyed by catalog/folder id.
        private static readonly Dictionary<string, string> CharacterNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "blue_sheep",   "Ben"    },
                { "green_sheep",  "Gus"    },
                { "orange_sheep", "Omar"   },
                { "pink_sheep",   "Pearl"  },
                { "purple_sheep", "Patsu"  },
                { "red_sheep",    "Rick"   },
                { "yellow_sheep", "Yogurt" },
            };

        /// <summary>
        /// Preferred label for a pet: a curated character name when we have one, then any name the
        /// catalog supplied, then a title-cased folder id. Used by the local list, the online download
        /// grid, and the tray so a pet reads the same everywhere.
        /// </summary>
        internal static string DisplayName(string folder, string catalogName)
        {
            string mapped;
            if (!string.IsNullOrWhiteSpace(folder) &&
                CharacterNames.TryGetValue(folder.Trim(), out mapped))
                return mapped;
            if (!string.IsNullOrWhiteSpace(catalogName))
                return catalogName.Trim();
            return PrettyName(folder);
        }

        /// <summary>
        /// The friendly label for a pet id ALONE, with no catalog entry to hand.
        ///
        /// <see cref="DisplayName"/> needs a catalog name passed in, and callers that only hold an id used to
        /// pass null -- which skips straight past the pet's own header to the prettified folder id, so the
        /// tray's "Remove a pet" and "Pet Speech" menus read "Shimeji 3x56f4pl" while "Add a pet" (which
        /// enumerates, and so has the header) read "Monkey D. Luffy" for the same pet. This reads the header
        /// the same way <see cref="EnumerateLocal"/> does, so an id reads identically everywhere.
        ///
        /// Cached: these are tray menus rebuilt on every open, and the alternative is a bounded file read per
        /// pet per open. A pet's header name only changes when the file is replaced, which happens in exactly
        /// one place -- so that place calls <see cref="Forget"/>. (This comment used to claim a replacement
        /// always arrived through a download that restarts the app, which stopped being true the moment an
        /// update could be applied in-process: the cache would then keep serving the OLD pet's name.)
        /// </summary>
        internal static string DisplayNameForId(string id)
        {
            if (string.IsNullOrEmpty(id)) return PrettyName(id);
            // The built-in has no folder to read a header from, and PrettyName upper-cases the first letter
            // of a folder id -- so the default pet would read "ESheep". Return the id's own casing.
            if (string.Equals(id, BuiltInPetId, StringComparison.OrdinalIgnoreCase)) return BuiltInPetId;
            string cached;
            lock (HeaderNameCache)
                if (HeaderNameCache.TryGetValue(id, out cached)) return cached;

            string resolved = DisplayName(id, ReadHeaderNameForId(id));
            lock (HeaderNameCache) HeaderNameCache[id] = resolved;
            return resolved;
        }

        private static readonly Dictionary<string, string> HeaderNameCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Drop the cached display name for one pet, because its animations.xml has just been rewritten.
        ///
        /// Call this from wherever a pet file is replaced, NOT from wherever a pet is re-rendered: the whole
        /// point of the cache is that rendering is frequent and replacement is rare.
        /// </summary>
        internal static void Forget(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (HeaderNameCache) HeaderNameCache.Remove(id);
        }

        /// <summary>The pet's own header name, located the same way <see cref="TryReadPetXml"/> locates its
        /// xml (library root first, then bundled), or null when the pet is not on disk.</summary>
        private static string ReadHeaderNameForId(string id)
        {
            if (!SecureDownload.IsSafeId(id)) return null;
            foreach (string root in new[] { AppPaths.LibraryPetsDirectory, AppPaths.BundledPetsDirectory })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string path = Path.Combine(root, id, "animations.xml");
                if (!File.Exists(path)) continue;
                return ReadHeaderName(path);
            }
            return null;
        }

        internal static string PrettyName(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return "Pet";
            string spaced = folder.Replace('_', ' ').Replace('-', ' ');
            string[] words = spaced.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var builder = new StringBuilder();
            foreach (string word in words)
            {
                if (builder.Length > 0) builder.Append(' ');
                builder.Append(char.ToUpperInvariant(word[0]));
                if (word.Length > 1) builder.Append(word.Substring(1));
            }
            return builder.Length > 0 ? builder.ToString() : "Pet";
        }

        /// <summary>
        /// The pet's own display name from the START of its animations.xml. The header (with petname/title)
        /// always precedes the multi-MB base64 sprite sheet, so a bounded read is enough and we never load the
        /// whole file just to label a card. Prefers &lt;petname&gt;, then &lt;title&gt; minus a trailing
        /// " (converted)"; returns null when neither is present (caller falls back to the folder id).
        /// </summary>
        private static string ReadHeaderName(string xmlPath)
        {
            try
            {
                var buf = new char[32 * 1024];
                int read;
                using (var reader = new StreamReader(xmlPath, Encoding.UTF8, true))
                    read = reader.ReadBlock(buf, 0, buf.Length);
                string head = new string(buf, 0, Math.Max(0, read));
                string name = Between(head, "<petname>", "</petname>");
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = Between(head, "<title>", "</title>");
                    const string suffix = " (converted)";
                    if (!string.IsNullOrWhiteSpace(name) && name.EndsWith(suffix, StringComparison.Ordinal))
                        name = name.Substring(0, name.Length - suffix.Length);
                }
                if (string.IsNullOrWhiteSpace(name)) return null;
                return DecodeEntities(name.Trim());
            }
            catch { return null; }
        }

        private static string Between(string s, string open, string close)
        {
            int i = s.IndexOf(open, StringComparison.Ordinal);
            if (i < 0) return null;
            i += open.Length;
            int j = s.IndexOf(close, i, StringComparison.Ordinal);
            return j < 0 ? null : s.Substring(i, j - i);
        }

        // The five predefined XML entities a serialized name can carry (&amp; decoded last so "&amp;lt;"
        // does not collapse to "<").
        private static string DecodeEntities(string s)
        {
            return s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
                    .Replace("&apos;", "'").Replace("&amp;", "&");
        }

        /// <summary>
        /// The built-in default plus every safe pet folder under the bundled (beside-exe) and library
        /// (downloaded) roots. The built-in is first, with a null id. Mirrors the gallery's listing so
        /// the tray offers exactly the pets the user can see.
        /// </summary>
        internal static List<CompanionInfo> EnumerateLocal()
        {
            var list = new List<CompanionInfo>
            {
                new CompanionInfo { Id = null, DisplayName = "eSheep (default)", IsBuiltIn = true }
            };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddFrom(AppPaths.BundledPetsDirectory, list, seen);   // read-only, beside the exe
            AddFrom(AppPaths.LibraryPetsDirectory, list, seen);   // writable, downloaded pets
            return list;
        }

        private static void AddFrom(string root, List<CompanionInfo> list, HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            List<string> directories;
            try { directories = new List<string>(Directory.EnumerateDirectories(root)); }
            catch { return; }
            directories.Sort(StringComparer.OrdinalIgnoreCase);

            const int maxPets = 256;
            foreach (string directory in directories)
            {
                if (list.Count > maxPets) break;
                string folder = Path.GetFileName(directory);
                if (!SecureDownload.IsSafeId(folder) || !seen.Add(folder)) continue;
                string xmlPath = Path.Combine(directory, "animations.xml");
                if (!File.Exists(xmlPath)) continue;
                list.Add(new CompanionInfo
                {
                    Id = folder,
                    // Prefer the pet's own name from its animations.xml header (so a converted shimeji reads
                    // "Bugcat Capoo", not the prettified folder id "Shimeji <id>"); fall back to the folder.
                    DisplayName = DisplayName(folder, ReadHeaderName(xmlPath)),
                    XmlPath = xmlPath,
                    IsBuiltIn = false,
                });
            }
        }

        /// <summary>
        /// Resolve a pet id to its raw animations.xml text. The built-in default (null/empty/"eSheep")
        /// returns the embedded default; a folder id is read (BOM-stripped by File.ReadAllText, size-
        /// bounded) from the library root first, then the bundled root. The text is validated by the
        /// caller (StartUp.TryStageRuntime) before use.
        /// </summary>
        internal static bool TryReadPetXml(string id, out string xml, out string error)
        {
            xml = null;
            error = null;
            if (string.IsNullOrEmpty(id) ||
                string.Equals(id, BuiltInPetId, StringComparison.OrdinalIgnoreCase))
            {
                xml = Properties.Resources.animations;
                return true;
            }
            if (!SecureDownload.IsSafeId(id))
            {
                error = "Unsafe pet id.";
                return false;
            }
            foreach (string root in new[] { AppPaths.LibraryPetsDirectory, AppPaths.BundledPetsDirectory })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string path = Path.Combine(root, id, "animations.xml");
                if (!File.Exists(path)) continue;
                try
                {
                    if (new FileInfo(path).Length > MaximumPetXmlBytes)
                    {
                        error = "Pet file too large.";
                        return false;
                    }
                    xml = File.ReadAllText(path);   // File.ReadAllText strips a leading UTF-8 BOM
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }
            error = "Pet '" + id + "' was not found.";
            return false;
        }
    }
}
