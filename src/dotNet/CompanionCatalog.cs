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

        /// <summary>
        /// The <c>&lt;author&gt;</c> every converted skin carries, so a converted companion can be told
        /// apart from a hand-authored one by its header alone.
        ///
        /// Duplicated from <c>PetEmitter.ConvertedAuthor</c> because the converter is a separate tool the app
        /// does not reference. A gate invariant asserts the two literals stay identical, which is the same
        /// treatment every other cross-assembly string in this repo gets -- the alternative, two copies free
        /// to drift, would silently turn the check below into "always false".
        /// </summary>
        internal const string ConvertedAuthor = "Converted from a Shimeji skin";

        /// <summary>
        /// Whether a pet header's author says a converter produced it. Used to label the header's
        /// <c>&lt;version&gt;</c> honestly: for a hand-authored pet that field is the author's own version
        /// (the shipped sheep range over 0.1, 0.3, 1.2.3 and 8.0), but for a converted one it is the
        /// CONVERTER's output-format number and means nothing to whoever is reading it.
        /// </summary>
        internal static bool IsConvertedAuthor(string author)
        {
            return author != null &&
                   author.Trim().Equals(ConvertedAuthor, StringComparison.OrdinalIgnoreCase);
        }

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
        /// Drop the cached display name for one pet, because its animations.xml has just been rewritten or
        /// removed, and tell every other per-id cache to do the same through <see cref="Forgotten"/>.
        ///
        /// Call this from wherever a pet file is replaced or deleted, NOT from wherever a pet is re-rendered:
        /// the whole point of the cache is that rendering is frequent and replacement is rare. The writers
        /// are the pane's download, the pane's uninstall, and Companion Studio's install and uninstall
        /// through the companion manager (F249, F336); the download was the only one calling this for a
        /// while, so a Studio install over an existing id kept serving the old name, icon and counts.
        /// </summary>
        internal static void Forget(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (HeaderNameCache) HeaderNameCache.Remove(id);
            Action<string> listeners = Forgotten;
            if (listeners != null) { try { listeners(id); } catch { } }
        }

        /// <summary>
        /// Raised by <see cref="Forget"/> with the id whose file changed. The Companions pane keeps its own
        /// per-id caches (stats, icon) in a WPF class this catalog must not reference, so it subscribes here
        /// instead of every writer having to know about it (F336).
        /// </summary>
        internal static event Action<string> Forgotten;

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
        ///
        /// CACHED PER FILE (F251), keyed by the file's write time and length, so a rewrite is a miss and a
        /// re-render is a hit: the tray's "Add a companion" submenu, the Companions pane and InstalledTypes
        /// each re-read every installed pet's header on the UI thread on every open (54 pets on this box).
        /// The saving is stated as avoided I/O -- one read per file per change instead of per open -- not
        /// as a timing. Keyed by the PATH rather than the id, so the bundled-vs-library precedence the two
        /// callers disagree about is untouched.
        /// </summary>
        internal static string ReadHeaderName(string xmlPath)
        {
            try
            {
                var info = new FileInfo(xmlPath);
                DateTime writtenUtc = info.LastWriteTimeUtc;
                long length = info.Length;
                lock (HeaderByPath)
                {
                    CachedHeader hit;
                    if (HeaderByPath.TryGetValue(xmlPath, out hit) && hit.WrittenUtc == writtenUtc && hit.Length == length)
                        return hit.Name;
                }
                string name = ReadHeaderNameUncached(xmlPath);
                lock (HeaderByPath)
                    HeaderByPath[xmlPath] = new CachedHeader { WrittenUtc = writtenUtc, Length = length, Name = name };
                return name;
            }
            catch { return null; }
        }

        private sealed class CachedHeader { public DateTime WrittenUtc; public long Length; public string Name; }
        private static readonly Dictionary<string, CachedHeader> HeaderByPath =
            new Dictionary<string, CachedHeader>(StringComparer.OrdinalIgnoreCase);

        /// <summary>How many times a header was actually read from disk: the self-test seam for the cache.</summary>
        internal static int HeaderFileReads;

        /// <summary>
        /// How far into the file the header read may go before giving up (F250): a header may carry an icon
        /// of up to MaximumIconBytes as base64 plus its text, and the old fixed 32K-character read missed the
        /// petname of any hand-authored header whose icon or info ran past it.
        /// </summary>
        internal const int HeaderReadBoundChars = CompanionXmlValidator.MaximumIconBytes * 4 / 3 + 70 * 1024;

        private static string ReadHeaderNameUncached(string xmlPath)
        {
            try
            {
                HeaderFileReads++;
                // Read in 32K blocks until </header> has arrived or the bound is reached. The close tag is
                // looked for only in the newest block plus a tag's width before it, so a tag split across
                // two blocks is still seen and the search stays linear.
                const string headerClose = "</header>";
                var head = new StringBuilder();
                var buf = new char[32 * 1024];
                using (var reader = new StreamReader(xmlPath, Encoding.UTF8, true))
                {
                    while (head.Length < HeaderReadBoundChars)
                    {
                        int read = reader.ReadBlock(buf, 0, buf.Length);
                        if (read <= 0) break;
                        int searchFrom = Math.Max(0, head.Length - headerClose.Length);
                        head.Append(buf, 0, read);
                        if (head.ToString(searchFrom, head.Length - searchFrom).IndexOf(headerClose, StringComparison.Ordinal) >= 0)
                            break;
                    }
                }
                string text = head.ToString();
                string name = Between(text, "<petname>", "</petname>");
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = Between(text, "<title>", "</title>");
                    // LEGACY. The converter no longer appends this -- it decorated a title the Author line
                    // already explained, and stripping it here to get a usable label is what proved it was
                    // noise. Kept because a skin someone converted with an older Companion Studio still
                    // carries it on their disk, and the same reasoning that keeps AppPaths.LegacySettingsFiles
                    // around applies: a reader costs one comparison, a wrong display name is visible forever.
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
            return EnumerateFrom(AppPaths.BundledPetsDirectory, AppPaths.LibraryPetsDirectory, true);
        }

        /// <summary>
        /// The one traversal both entry points use, with the two roots passed in rather than read
        /// from AppPaths.
        ///
        /// The roots were the only thing EnumerateLocal and EnumerateLocalIds did not already share,
        /// and passing them is what makes the pair testable: the installed corpus is EMPTY on a dev
        /// build and in CI, because the `companions/` directory beside the exe is added by the
        /// packaging step, so a comparison against it agreed vacuously and a deliberately divergent
        /// fast path still reported PASS.
        ///
        /// Order is bundled-then-library with a `seen` set, so for an id present in BOTH roots the
        /// BUNDLED copy wins. That is worth stating because DisplayNameForId resolves the other way
        /// round, library-then-bundled: the two disagree about which file wins for a duplicated id,
        /// which is a real inconsistency and the reason the header-name cache is NOT simply shared
        /// between them.
        /// </summary>
        internal static List<CompanionInfo> EnumerateFrom(
            string bundledRoot, string libraryRoot, bool readDisplayNames)
        {
            var list = new List<CompanionInfo>
            {
                new CompanionInfo { Id = null, DisplayName = "eSheep (default)", IsBuiltIn = true }
            };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddFrom(bundledRoot, list, seen, readDisplayNames);   // read-only, beside the exe
            AddFrom(libraryRoot, list, seen, readDisplayNames);   // writable, downloaded pets
            return list;
        }

        /// <summary>The ids from a given pair of roots, for the self-test's synthetic corpus.</summary>
        internal static List<string> EnumerateIdsFrom(string bundledRoot, string libraryRoot)
        {
            var ids = new List<string>();
            foreach (CompanionInfo info in EnumerateFrom(bundledRoot, libraryRoot, false))
                if (!info.IsBuiltIn && !string.IsNullOrEmpty(info.Id)) ids.Add(info.Id);
            return ids;
        }

        /// <summary>
        /// The installed pet ids, WITHOUT reading any display name.
        ///
        /// Callers that only want to know which pets exist were paying for names they discarded: per
        /// pet, a File.Exists, a StreamReader and a ReadBlock into a 32768-char buffer that always
        /// fills, because an animations.xml is hundreds of KB (158 KB esheep64, 406 KB hornet). Over
        /// the 54 shipped companions that is ~54 file opens and ~1.7 MB of decoded reads per call,
        /// synchronously on the UI thread, and CompanionsPaneControl.LocalPetIds ran it on pane open,
        /// on every Check, after every download and after every uninstall.
        ///
        /// SHARES AddFrom, so the id set is identical BY CONSTRUCTION rather than by my reading of
        /// it: same directory walk, same IsSafeId filter, same seen-dedup, same animations.xml
        /// existence test, same 256-pet cap, same bundled-then-library order. Only the header read is
        /// skipped. Writing a second traversal would have been the easy way to make the two answers
        /// drift.
        ///
        /// No duration is claimed for this. The saving is the avoided I/O, which is a count.
        /// </summary>
        internal static List<string> EnumerateLocalIds()
        {
            return EnumerateIdsFrom(AppPaths.BundledPetsDirectory, AppPaths.LibraryPetsDirectory);
        }

        private static void AddFrom(string root, List<CompanionInfo> list, HashSet<string> seen,
                                    bool readDisplayNames)
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
                    // The header read is the expensive part and the only thing the flag controls.
                    DisplayName = readDisplayNames
                        ? DisplayName(folder, ReadHeaderName(xmlPath))
                        : folder,
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
