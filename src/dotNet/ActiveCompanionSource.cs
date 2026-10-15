using System;
using System.IO;
using System.Text;
using DesktopAICompanion.ModuleKit;   // AtomicFile, compiled from ModuleKit's files (F358)

namespace DesktopAICompanion
{
    /// <summary>
    /// Which definition the DEFAULT companion (the "" slot of the on-screen mix) runs at launch, and what is written
    /// back to settings.json's "xml" key with it (lane feature/stale-pet-xml, app 1.5.1, BACKLOG N-stale-pet-xml-01,
    /// BUG-016).
    ///
    /// THE DEFECT. Until 1.5.1 the default companion was built from a full copy of its animations.xml kept in
    /// settings.json, the shape upstream desktopPet used before there was a companion library, and the library file
    /// was never read for it at launch. The copy was written by LoadNewXMLFromString, which "Use this companion" and a
    /// drop on a companion reach, and by the launch itself, which saved a localxml= file or the embedded default back
    /// into it; nothing ever refreshed it from the companion file. So an Update in the Companions pane (or a Companion
    /// Studio install over the same id) rewrote the companion file while the default companion kept running the copy
    /// from the day it was picked, across every restart and app upgrade, while the pane said "Restart to see the
    /// change" and the weekly check, which hashes the file, called it current. Measured on the owner's box on
    /// 2026-10-14: the settings copy of pink_sheep was byte-identical to the 1.0.0 blob (sha256 89452d68...), the
    /// companion file was the 2026-09-25 fix (3d33ff50..., the catalog's hash), and they differed by exactly the two
    /// taskbar edges 1d48936 added, which is why the log still showed state 173 with "6 candidate(s) declared and none
    /// eligible" at where=TASKBAR, in bursts, three weeks after the fix shipped.
    ///
    /// THE RULE. The companion file wins whenever the copy in settings is a copy OF that file and differs from it; a
    /// companion XML the user supplied directly (a file dropped on a companion, or localxml=) is never replaced,
    /// because it is not a copy of any file. Which of the two a copy is cannot be inferred from the copy alone, so
    /// since 1.5.1 every writer DECLARES it in the "xmlSource" key beside it ("pack:" or "custom:" and the SHA-256 of
    /// the "xml" it describes): "Use this companion" declares a pack copy, a drop and localxml= declare custom. A
    /// marker whose hash does not match the "xml" beside it describes some other text (an older build changed the xml
    /// and carried this key through untouched, which its extension data does) and is ignored. A copy that is byte for
    /// byte the companion file is a copy of it whatever its marker says, so a 1.5.1 drop of the companion's own file is
    /// not pinned as hand-loaded.
    ///
    /// A copy with no usable marker was written by 1.5.0 or earlier, which is every affected user. For those the copy
    /// counts as a copy of the file when both carry the same header name (the petname, else the title less a legacy
    /// " (converted)", read by CompanionCatalog.HeaderNameFromText, the name every card shows): checked against every
    /// version of all 32 companions changed since v1.0.0, the petname never moved, while the title lost its
    /// " (converted)" on 2026-09-09. A copy whose header names a different companion is a hand-loaded XML and is kept,
    /// now marked custom.
    ///
    /// THE COST THAT REMAINS, and why nothing is lost to it. The header name is not unique: different companions share
    /// one (the catalog ships three such pairs: the built-in eSheep and esheep64 are both "eSheep", fox and pink_fox
    /// are both "fox mate", red_sheep and the Shimeji Rick are both "Rick"), and a hand-edited copy of a companion
    /// keeps its name. So ANY undeclared copy whose header name equals the file's is replaced on the first 1.5.1
    /// launch: a hand-edit of the same companion, or a different companion of the same name dropped on it under 1.5.0
    /// or earlier. Before that replacement is saved, the launch writes the copy it replaces to a side file in the data
    /// folder (<see cref="Commit"/>, replaced-default-companion-&lt;sha8&gt;.xml, one per distinct copy) and names that
    /// file in its log line; if the side file cannot be written, nothing is saved and the copy stays in settings. A
    /// drop made under 1.5.1 is declared custom and never replaced, unless it is byte for byte the companion file
    /// (above). One case the name rule gets wrong the other way: a 1.5.0 copy whose companion a Companion Studio
    /// install renamed over its id before the first 1.5.1 launch names a different companion and is kept as custom; the
    /// pane offers Use only on an inactive companion, so Use on another, then Use on it again (which resets the
    /// companions on screen) declares it a copy of the file again. Both are recorded in DESIGN-REGISTER.md.
    ///
    /// REFUSED: dropping the settings copy altogether and always reading the file. A hand-loaded XML has no file, the
    /// copy is what keeps a companion whose file went missing running, and moving the payload out of settings.json is
    /// the schema change F361 and RA-302 already scheduled for a host release of its own (DESIGN-REGISTER.md). This
    /// keeps the copy and adds one key, so 1.5.0 reads what 1.5.1 writes.
    ///
    /// Pure over what it is handed (the settings copy, its marker, a reader for the companion file, a save and a
    /// folder), so --hardening-selftest can drive it with fakes as well as through the real library and settings
    /// store. Which branches it drives, and which it does not, is under Guards in DESIGN-REGISTER.md.
    /// </summary>
    internal static class ActiveCompanionSource
    {
        internal const string PackPrefix = "pack:";
        internal const string CustomPrefix = "custom:";

        /// <summary>The name every side file starts with (<see cref="KeepCopyAside"/>).</summary>
        internal const string KeptCopyPrefix = "replaced-default-companion-";

        /// <summary>Reads a companion's file by id; CompanionCatalog.TryReadPetXml in the app (the embedded default
        /// for the built-in, else the library and then the bundled folder).</summary>
        internal delegate bool PackReader(string id, out string xml, out string error);

        internal enum Kind
        {
            /// <summary>Nothing to run: no copy in settings and no companion file. The caller runs the built-in.</summary>
            None,
            /// <summary>The localxml= file the app was started with.</summary>
            LocalXmlArgument,
            /// <summary>The companion's own file (the embedded default for the built-in).</summary>
            CompanionFile,
            /// <summary>The copy saved in settings.json.</summary>
            SettingsCopy,
        }

        internal sealed class Choice
        {
            /// <summary>What to stage, or null for <see cref="Kind.None"/>.</summary>
            public string Xml;
            /// <summary>The "xmlSource" marker to persist beside <see cref="Xml"/>.</summary>
            public string Source;
            public Kind Kind;
            /// <summary>True when the copy in settings differed from the companion file and is being replaced by it:
            /// a copy of the file by its marker or, undeclared, by its header name. Resolve proves only that the two
            /// differ, never which is newer, so nothing calls the copy older. The launch line says it once the save
            /// that replaced it succeeded; the next launch then finds them equal.</summary>
            public bool ReplacedStaleCopy;
            /// <summary>True when the replaced copy carried no usable marker, so the replacement rests on its header
            /// name alone and the copy may be the user's own XML: <see cref="Commit"/> keeps it in a side file before
            /// it saves the replacement.</summary>
            public bool KeepReplacedCopy;
            /// <summary>The copy in settings to stage instead when the companion file is rejected, or null; it is
            /// already in settings, so nothing is written for it. Also the text <see cref="Commit"/> keeps aside.</summary>
            public string Fallback;
            /// <summary>Why the settings copy runs, for the log line (<see cref="Kind.SettingsCopy"/> only).</summary>
            public string Reason;
        }

        internal static string PackMarker(string xml) { return PackPrefix + HashOf(xml); }

        internal static string CustomMarker(string xml) { return CustomPrefix + HashOf(xml); }

        /// <summary>True for a marker that declares a hand-loaded XML (whatever text it describes).</summary>
        internal static bool IsCustomMarker(string source)
        {
            return source != null && source.StartsWith(CustomPrefix, StringComparison.Ordinal);
        }

        internal static string HashOf(string xml)
        {
            return CompanionProvenance.HashBytes(new UTF8Encoding(false).GetBytes(xml ?? ""));
        }

        /// <summary>
        /// "pack" or "custom" when <paramref name="source"/> is a marker for exactly <paramref name="xmlHash"/>, else
        /// null: absent (written by 1.5.0 or earlier), malformed, or describing some other text.
        /// </summary>
        internal static string MarkerKind(string source, string xmlHash)
        {
            if (string.IsNullOrEmpty(source)) return null;
            string kind;
            string hash;
            if (source.StartsWith(PackPrefix, StringComparison.Ordinal))
            {
                kind = "pack";
                hash = source.Substring(PackPrefix.Length);
            }
            else if (source.StartsWith(CustomPrefix, StringComparison.Ordinal))
            {
                kind = "custom";
                hash = source.Substring(CustomPrefix.Length);
            }
            else return null;
            return string.Equals(hash.Trim(), xmlHash, StringComparison.OrdinalIgnoreCase) ? kind : null;
        }

        /// <summary>
        /// Whether two animations.xml texts are the same companion by header name. Null on either side is "cannot
        /// tell", which is false: an unprovable copy is kept, never replaced.
        /// </summary>
        internal static bool SameCompanion(string a, string b)
        {
            string nameA = CompanionCatalog.HeaderNameFromText(Head(a));
            string nameB = CompanionCatalog.HeaderNameFromText(Head(b));
            return nameA != null && nameB != null && string.Equals(nameA, nameB, StringComparison.Ordinal);
        }

        private static string Head(string xml)
        {
            if (xml == null) return null;
            int bound = CompanionCatalog.HeaderReadBoundChars;
            return xml.Length > bound ? xml.Substring(0, bound) : xml;
        }

        /// <summary>
        /// Decide what the default companion runs. <paramref name="overrideXml"/> is the localxml= text or null;
        /// <paramref name="cachedXml"/> and <paramref name="cachedSource"/> are settings.json's "xml" and "xmlSource".
        /// </summary>
        internal static Choice Resolve(string overrideXml, string activeId, string cachedXml, string cachedSource,
                                       PackReader readPack)
        {
            if (!string.IsNullOrWhiteSpace(overrideXml))
                return new Choice { Xml = overrideXml, Source = CustomMarker(overrideXml), Kind = Kind.LocalXmlArgument };

            string cached = cachedXml ?? "";
            bool haveCopy = !string.IsNullOrWhiteSpace(cached);

            string fileXml = null;
            string fileError = null;
            bool haveFile = false;
            if (readPack != null)
            {
                try { haveFile = readPack(activeId, out fileXml, out fileError) && !string.IsNullOrWhiteSpace(fileXml); }
                catch (Exception ex) { haveFile = false; fileError = ex.Message; }
            }
            if (!haveFile && string.IsNullOrEmpty(fileError)) fileError = "it was not found";

            if (!haveCopy)
            {
                if (haveFile)
                    return new Choice { Xml = fileXml, Source = PackMarker(fileXml), Kind = Kind.CompanionFile };
                return new Choice { Kind = Kind.None, Reason = "there is no companion file (" + fileError + ") and no copy in settings" };
            }

            string cachedHash = HashOf(cached);
            string marker = MarkerKind(cachedSource, cachedHash);

            // Byte for byte the companion file: a copy of it, whatever its marker says. Ahead of the custom test so a
            // drop of the companion's own file under 1.5.1 (declared custom by the drop) is marked a copy of the file
            // here, rather than pinned as hand-loaded and never given the file's next update.
            if (haveFile && string.Equals(cached, fileXml, StringComparison.Ordinal))
                return new Choice { Xml = fileXml, Source = PackPrefix + cachedHash, Kind = Kind.CompanionFile };

            // A hand-loaded XML, declared so by whatever wrote it: kept whatever the file says.
            if (marker == "custom")
                return new Choice
                {
                    Xml = cached, Source = cachedSource, Kind = Kind.SettingsCopy,
                    Reason = "it is a companion XML loaded by hand, so no companion file replaces it",
                };

            if (!haveFile)
                return new Choice
                {
                    Xml = cached, Source = cachedSource ?? "", Kind = Kind.SettingsCopy,
                    Reason = "its companion file could not be read (" + fileError + ")",
                };

            // A declared copy of the file, or an undeclared one (1.5.0 and earlier) of the same companion, that differs
            // from it (which is newer is never proved): the file wins. The copy stays the fallback until the file
            // stages; an undeclared one, judged by its header name alone, goes to a side file first.
            if (marker == "pack" || SameCompanion(cached, fileXml))
                return new Choice
                {
                    Xml = fileXml, Source = PackMarker(fileXml), Kind = Kind.CompanionFile,
                    ReplacedStaleCopy = true, Fallback = cached,
                    KeepReplacedCopy = marker == null,
                };

            // Undeclared, and its header names a different companion: an XML loaded by hand under 1.5.0 or earlier.
            // Kept, and marked custom so the next launch need not ask again.
            return new Choice
            {
                Xml = cached, Source = CustomPrefix + cachedHash, Kind = Kind.SettingsCopy,
                Reason = "it names a different companion from the companion file, so it is a companion XML loaded by hand",
            };
        }

        /// <summary>The side file <paramref name="copy"/> is kept in: the first 8 hex digits of its SHA-256 name it.</summary>
        internal static string KeptCopyFileName(string copy)
        {
            return KeptCopyPrefix + HashOf(copy).Substring(0, 8) + ".xml";
        }

        /// <summary>
        /// Write <paramref name="copy"/> to its side file in <paramref name="directory"/>, one per distinct copy: the
        /// same copy again (a later launch, after a save that failed) finds its file and writes nothing. A file of that
        /// name holding some other text is NEVER overwritten, since it may itself be the only copy of something; the
        /// copy then goes under its whole hash, and if that name is taken too, nothing is written and the caller saves
        /// nothing. The write is durable (AtomicFile) and is read back by hash before it counts.
        /// </summary>
        internal static bool KeepCopyAside(string directory, string copy, out string path, out string error)
        {
            path = null;
            error = null;
            if (string.IsNullOrWhiteSpace(copy)) { error = "there was no copy to keep"; return false; }
            if (string.IsNullOrEmpty(directory)) { error = "there is no data folder"; return false; }
            string hash = HashOf(copy);
            foreach (string name in new[] { KeptCopyFileName(copy), KeptCopyPrefix + hash + ".xml" })
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    if (string.Equals(CompanionProvenance.HashFile(candidate), hash, StringComparison.OrdinalIgnoreCase))
                    {
                        path = candidate;
                        return true;
                    }
                    continue;
                }
                bool written = AtomicFile.TryWriteAllText(candidate, copy, null) &&
                               string.Equals(CompanionProvenance.HashFile(candidate), hash, StringComparison.OrdinalIgnoreCase);
                if (written)
                {
                    path = candidate;
                    return true;
                }
                error = "it could not be written to " + candidate;
                return false;
            }
            error = "both of its names in " + directory + " hold other text";
            return false;
        }

        /// <summary>
        /// The launch's write of what it staged, in its one safe order, here so --hardening-selftest can hold the ORDER
        /// and not only the parts. A copy replaced on its header name alone (<see cref="Choice.KeepReplacedCopy"/>) is
        /// first written to its side file in <paramref name="keepDirectory"/> (the data folder, beside settings.json),
        /// and only then is the replacement saved through <paramref name="save"/> (LocalData.SetXml). Without the side
        /// file the user's XML would live on only in settings.json.bak, which AppSettingsStore rotates on every save, so
        /// the next settings write of any kind would take it. If the side file cannot be written, nothing is saved: the
        /// file runs this launch, the copy stays in settings, and the next launch tries again.
        /// Returns whether the save happened. <paramref name="keptAt"/> is the side file, or null.
        /// <paramref name="warning"/> is the line to log when nothing was saved, worded for what the next launch will
        /// really do: a launch that ran the companion file and could not save it runs that file again, so it does not
        /// say "the previous pet will return".
        /// </summary>
        internal static bool Commit(Choice choice, string activeId, Func<string, string, bool> save, string keepDirectory,
                                    out string keptAt, out string warning)
        {
            keptAt = null;
            warning = null;
            string keepError;
            if (choice.KeepReplacedCopy && !KeepCopyAside(keepDirectory, choice.Fallback, out keptAt, out keepError))
            {
                keptAt = null;
                warning = "[companions] the copy saved in settings for the default companion '" + (activeId ?? "") +
                          "' could not be kept aside (" + keepError + "), so it stays in settings and nothing replaced it; " +
                          "the default companion runs from " + FileNoun(activeId) + " again at the next launch";
                return false;
            }
            if (save(choice.Xml, choice.Source)) return true;
            warning = choice.Kind == Kind.CompanionFile
                ? "The active pet could not be persisted; it runs from " + FileNoun(activeId) + " again at the next launch."
                : "The active pet could not be persisted; the previous pet will return next launch.";
            return false;
        }

        private static string FileNoun(string activeId)
        {
            return string.Equals(activeId, CompanionCatalog.BuiltInPetId, StringComparison.OrdinalIgnoreCase)
                ? "the built-in default" : "its companion file";
        }

        /// <summary>The one launch line that names where the default companion came from. <paramref name="persisted"/>
        /// is <see cref="Commit"/>'s answer: a replacement is reported only once it is saved, because a line logged
        /// before a save that then failed claimed a replacement the next launch would announce again.
        /// <paramref name="keptAt"/> is the side file the replaced copy went to, or null.</summary>
        internal static string Describe(Choice choice, string activeId, bool persisted, string keptAt)
        {
            string who = "the default companion '" + (activeId ?? "") + "'";
            if (choice == null) return "[companions] " + who + " has no recorded source";
            switch (choice.Kind)
            {
                case Kind.LocalXmlArgument:
                    return "[companions] " + who + " runs from the localxml= file it was started with";
                case Kind.CompanionFile:
                    return "[companions] " + who + " runs from " + FileNoun(activeId) +
                           (choice.ReplacedStaleCopy && persisted
                               ? "; the copy saved in settings differed from it and has been replaced" +
                                 (keptAt != null ? ", and that copy is kept in " + keptAt : "")
                               : "");
                case Kind.SettingsCopy:
                    return "[companions] " + who + " runs from the copy saved in settings: " + (choice.Reason ?? "");
                default:
                    return "[companions] " + who + " has nothing to run: " + (choice.Reason ?? "");
            }
        }
    }
}
