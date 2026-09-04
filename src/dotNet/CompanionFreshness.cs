using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace DesktopAICompanion
{
    /// <summary>How an installed pet compares to the catalog's current copy of it.</summary>
    internal enum CompanionFreshness
    {
        /// <summary>Not in the writable library at all: a download, not an update.</summary>
        NotInstalled,
        /// <summary>Byte-identical to the catalog. Nothing to do.</summary>
        UpToDate,
        /// <summary>The catalog moved on and the local copy is untouched since it was installed, so replacing
        /// it loses nothing.</summary>
        UpdateAvailable,
        /// <summary>Differs from the catalog AND from what was installed, so the user (or an authoring tool)
        /// changed it. Still offered, but replacing it discards their work and has to say so.</summary>
        LocallyModified,
        /// <summary>Differs from the catalog, and there is no record of what was installed -- a pet placed by
        /// hand, installed by a build older than the provenance stamp, or authored in Companion Studio. Cannot be
        /// told apart from LocallyModified, so it is treated the same way rather than assumed safe.</summary>
        UnknownProvenance,
    }

    /// <summary>
    /// Whether an installed pet is the catalog's current version.
    ///
    /// **Why a hash and not a version number.** A pet catalog entry has no version field, and adding one would
    /// not help: the number would have to be maintained by hand for 53 pets and would silently be wrong the
    /// first time someone forgot. The catalog already records the SHA-256 of the exact bytes it serves, and
    /// the installer writes those bytes verbatim (RemoteCatalogClient.DownloadVerifiedAsync verifies the hash,
    /// then SecureDownload.WriteAllBytesAtomic writes the same array), so hashing the installed file answers
    /// the question with data that already exists and cannot drift.
    ///
    /// Verified against the live catalog before this was written: the bytes raw.githubusercontent serves for a
    /// pet hash to exactly the catalog's sha256, because New-ContentCatalog.ps1 hashes the committed git blob
    /// and that is what raw serves. **Do NOT hash the working-tree file to check this** -- a repo checkout has
    /// CRLF endings and git stores LF, so the two disagree and it looks like a bug in here.
    ///
    /// **Why a provenance stamp.** Without one, every stale pet has to be reported as "this may overwrite your
    /// changes", which is either a lie or a nag depending on the pet. A file recording the hash AS INSTALLED
    /// separates "the catalog changed" from "you changed it", and the second is the only case that needs a
    /// warning. Absent stamp is deliberately NOT treated as safe.
    /// </summary>
    internal static class CompanionProvenance
    {
        /// <summary>Sits beside the pet's animations.xml. Not a pet file: CompanionCatalog scans for
        /// animations.xml, so an extra file in the folder is inert.</summary>
        internal const string StampFileName = "catalog.sha256";

        internal static string HashBytes(byte[] bytes)
        {
            if (bytes == null) return "";
            using (SHA256 sha = SHA256.Create())
                return ToHex(sha.ComputeHash(bytes));
        }

        internal static string HashFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
                using (SHA256 sha = SHA256.Create())
                using (FileStream stream = File.OpenRead(path))
                    return ToHex(sha.ComputeHash(stream));
            }
            catch { return ""; }
        }

        private static string ToHex(byte[] hash)
        {
            var sb = new System.Text.StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// Classify one pet. Pure: every input is a value, so the whole table can be asserted without a disk
        /// or a network.
        /// </summary>
        /// <param name="installedHash">Hash of the pet's animations.xml on disk, "" when it is not installed.</param>
        /// <param name="catalogHash">The catalog's sha256 for that id, "" when the catalog does not list it.</param>
        /// <param name="stampHash">Hash recorded when it was installed, "" when there is no stamp.</param>
        internal static CompanionFreshness Classify(string installedHash, string catalogHash, string stampHash)
        {
            installedHash = Normalize(installedHash);
            catalogHash = Normalize(catalogHash);
            stampHash = Normalize(stampHash);

            if (installedHash.Length == 0) return CompanionFreshness.NotInstalled;
            // A pet the catalog does not list cannot be compared to it. Reported as up to date rather than
            // stale: an imported or hand-authored pet is not out of date, it is simply not ours, and offering
            // to "update" it would offer to replace it with nothing.
            if (catalogHash.Length == 0) return CompanionFreshness.UpToDate;
            if (installedHash == catalogHash) return CompanionFreshness.UpToDate;

            if (stampHash.Length == 0) return CompanionFreshness.UnknownProvenance;
            // The local copy still matches what was installed, so the difference is entirely the catalog's.
            if (installedHash == stampHash) return CompanionFreshness.UpdateAvailable;
            return CompanionFreshness.LocallyModified;
        }

        /// <summary>True when the user should be warned that updating discards something. Kept next to
        /// Classify so the UI cannot invent its own opinion about which states are safe.</summary>
        internal static bool UpdateWouldDiscardChanges(CompanionFreshness freshness)
        {
            return freshness == CompanionFreshness.LocallyModified || freshness == CompanionFreshness.UnknownProvenance;
        }

        /// <summary>True when this pet should appear in the "updates available" list at all.</summary>
        internal static bool IsStale(CompanionFreshness freshness)
        {
            return freshness == CompanionFreshness.UpdateAvailable ||
                   freshness == CompanionFreshness.LocallyModified ||
                   freshness == CompanionFreshness.UnknownProvenance;
        }

        /// <summary>One line explaining the state, so the pane and the tests read the same wording.</summary>
        internal static string Describe(CompanionFreshness freshness)
        {
            switch (freshness)
            {
                case CompanionFreshness.UpToDate: return "Up to date.";
                case CompanionFreshness.NotInstalled: return "Not installed.";
                case CompanionFreshness.UpdateAvailable: return "A newer version is available.";
                case CompanionFreshness.LocallyModified:
                    return "A newer version is available, but this pet has been edited since you installed it — updating replaces your changes.";
                default:
                    // Deliberately does not claim it came from outside the catalog. The commonest way to reach
                    // this state is a pet installed by a build older than the provenance stamp, which DID come
                    // from the catalog -- saying otherwise would be wrong for most of the pets that hit it,
                    // and every pet installed before this feature shipped hits it exactly once.
                    return "A newer version is available. There is no record of what was installed here, so updating replaces this copy entirely.";
            }
        }

        /// <summary>
        /// How the INSTALLED copy of a catalog pet compares to the catalog. "" hashes mean "absent", which
        /// <see cref="Classify"/> already handles, so a missing pet or a missing stamp needs no special case.
        ///
        /// Only the writable library is considered. A BUNDLED pet ships inside the app and is replaced by an
        /// app update, not by a catalog download.
        /// </summary>
        internal static CompanionFreshness FreshnessOfInstalled(string id, string catalogSha256)
        {
            if (string.IsNullOrEmpty(id)) return CompanionFreshness.NotInstalled;
            string directory = Path.Combine(AppPaths.LibraryPetsDirectory, id);
            return Classify(
                HashFile(Path.Combine(directory, "animations.xml")),
                catalogSha256,
                ReadStamp(directory));
        }

        /// <summary>True when this id has a pet in the WRITABLE library (as opposed to bundled, or absent).</summary>
        internal static bool IsInLibrary(string id)
        {
            try
            {
                return !string.IsNullOrEmpty(id) &&
                    File.Exists(
                        Path.Combine(AppPaths.LibraryPetsDirectory, id, "animations.xml"));
            }
            catch { return false; }
        }

        /// <summary>
        /// Every catalog pet whose installed copy is no longer the catalog's.
        ///
        /// Shared between the Pets pane and the background check on purpose. Two implementations of "is this
        /// pet out of date" is how a pane and a notification end up disagreeing, which is the same class of
        /// mistake ModuleUpdateScan's own comment warns about for modules.
        ///
        /// Hashing is real file I/O over every installed catalog pet, so a caller on a UI thread should not
        /// invoke this directly.
        /// </summary>
        internal static List<string> StaleInstalledIds(RemoteCatalog catalog)
        {
            var stale = new List<string>();
            if (catalog == null || catalog.Pets == null) return stale;
            foreach (CatalogCompanion pet in catalog.Pets)
            {
                if (pet == null || string.IsNullOrEmpty(pet.Id)) continue;
                if (!IsInLibrary(pet.Id)) continue;
                if (IsStale(FreshnessOfInstalled(pet.Id, pet.Sha256))) stale.Add(pet.Id);
            }
            return stale;
        }

        internal static string StampPath(string petDirectory)
        {
            return string.IsNullOrEmpty(petDirectory) ? null : Path.Combine(petDirectory, StampFileName);
        }

        /// <summary>Record the hash a pet was installed with. Best-effort by design: a pet whose stamp could
        /// not be written still works, and the only cost is that a later update warns about overwriting when
        /// it did not need to. Failing the install over a provenance note would be the wrong trade.</summary>
        internal static void WriteStamp(string petDirectory, string hash)
        {
            try
            {
                string path = StampPath(petDirectory);
                if (path == null || string.IsNullOrEmpty(hash)) return;
                File.WriteAllText(path, hash, new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        internal static string ReadStamp(string petDirectory)
        {
            try
            {
                string path = StampPath(petDirectory);
                if (path == null || !File.Exists(path)) return "";
                return Normalize(File.ReadAllText(path));
            }
            catch { return ""; }
        }

        private static string Normalize(string hash)
        {
            return (hash ?? "").Trim().ToLowerInvariant();
        }
    }
}
