using System;
using System.Collections.Generic;
using System.IO;

namespace DesktopAICompanion
{
    /// <summary>
    /// Packs in the installed catalog (grimoire/06 section 9): the pack index over the library and bundled roots, and the
    /// Add-a-companion list without hidden members (helpers and egg stages). A member's file is the one
    /// <see cref="TryReadPetXml"/> would stage for its id: the library copy when there is one, so a bundled copy it
    /// shadows is never indexed.
    /// </summary>
    internal static partial class CompanionCatalog
    {
        private sealed class CachedFacts { public DateTime WrittenUtc; public long Length; public PackHeaderFacts Facts; }
        private static readonly Dictionary<string, CachedFacts> FactsByPath =
            new Dictionary<string, CachedFacts>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The pack facts of one animations.xml, cached per file by write time and length (a rewrite is a miss),
        /// or null when it declares no pack or cannot be read.</summary>
        internal static PackHeaderFacts ReadPackFacts(string xmlPath)
        {
            try
            {
                var info = new FileInfo(xmlPath);
                if (!info.Exists) return null;
                DateTime writtenUtc = info.LastWriteTimeUtc;
                long length = info.Length;
                lock (FactsByPath)
                {
                    CachedFacts hit;
                    if (FactsByPath.TryGetValue(xmlPath, out hit) && hit.WrittenUtc == writtenUtc && hit.Length == length)
                        return hit.Facts;
                }
                PackHeaderFacts facts = PackHeaderFacts.FromHeaderText(ReadHeaderText(xmlPath));
                lock (FactsByPath)
                    FactsByPath[xmlPath] = new CachedFacts { WrittenUtc = writtenUtc, Length = length, Facts = facts };
                return facts;
            }
            catch { return null; }
        }

        /// <summary>The pack index of the installed companions (the app's roots).</summary>
        internal static CompanionPackIndex PackIndexLocal()
        {
            return PackIndexFrom(AppPaths.LibraryPetsDirectory, AppPaths.BundledPetsDirectory);
        }

        /// <summary>The pack index over two roots: every safe folder with an animations.xml that declares a pack, each id
        /// read where TryReadPetXml reads it (library first). The precedence between members is the index's own.</summary>
        internal static CompanionPackIndex PackIndexFrom(string libraryRoot, string bundledRoot)
        {
            var candidates = new List<PackIndexEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddPackCandidates(libraryRoot, true, candidates, seen);
            AddPackCandidates(bundledRoot, false, candidates, seen);
            return CompanionPackIndex.Build(candidates);
        }

        private static void AddPackCandidates(string root, bool library, List<PackIndexEntry> candidates, HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            List<string> directories;
            try { directories = new List<string>(Directory.EnumerateDirectories(root)); }
            catch { return; }
            foreach (string directory in directories)
            {
                string id = Path.GetFileName(directory);
                if (!SecureDownload.IsSafeId(id)) continue;
                string xmlPath = Path.Combine(directory, "animations.xml");
                if (!File.Exists(xmlPath) || !seen.Add(id)) continue;
                PackHeaderFacts facts = ReadPackFacts(xmlPath);
                if (facts != null) candidates.Add(new PackIndexEntry { TypeId = id, FromLibrary = library, Facts = facts });
            }
        }

        /// <summary>Whether an installed id is a hidden pack member (read where TryReadPetXml reads it).</summary>
        internal static bool IsHiddenPackMember(string id, string libraryRoot, string bundledRoot)
        {
            PackHeaderFacts facts = InstalledPackFacts(id, libraryRoot, bundledRoot);
            return facts != null && facts.Hidden;
        }

        /// <summary>An installed id's own pack facts, read where TryReadPetXml reads its file (the library copy before a
        /// bundled one), or null when it declares no pack. What ICompanionManager.InstalledTypes reports per type (1.10.0)
        /// and what keeps a hidden member out of the Add list: one reader, so the two cannot disagree about a type.</summary>
        internal static PackHeaderFacts InstalledPackFacts(string id, string libraryRoot, string bundledRoot)
        {
            if (string.IsNullOrEmpty(id) || !SecureDownload.IsSafeId(id)) return null;
            foreach (string root in new[] { libraryRoot, bundledRoot })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string path = Path.Combine(root, id, "animations.xml");
                if (!File.Exists(path)) continue;
                return ReadPackFacts(path);
            }
            return null;
        }

        /// <summary>The companions a user may add or use: <see cref="EnumerateLocal"/> without hidden pack members, which
        /// arrive only through their pack (an egg hatching, a helper born).</summary>
        internal static List<CompanionInfo> EnumerateAddable()
        {
            return EnumerateAddableFrom(AppPaths.BundledPetsDirectory, AppPaths.LibraryPetsDirectory);
        }

        /// <summary><see cref="EnumerateAddable"/> over two given roots (the self-test's scratch corpus).</summary>
        internal static List<CompanionInfo> EnumerateAddableFrom(string bundledRoot, string libraryRoot)
        {
            List<CompanionInfo> list = EnumerateFrom(bundledRoot, libraryRoot, true);
            list.RemoveAll(info => !info.IsBuiltIn && IsHiddenPackMember(info.Id, libraryRoot, bundledRoot));
            return list;
        }
    }
}
