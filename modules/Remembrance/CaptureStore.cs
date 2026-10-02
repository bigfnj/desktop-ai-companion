using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// How new captures are filed under the storage root (2.0.0): one folder per capture, or one folder per day.
    /// AgentFlow's AgentMode is the shape copied: a stable id is stored, the Radio row shows the owner's words, and a
    /// pure <see cref="Migrate"/> derives the choice from the legacy checkbox at read time.
    ///
    /// It replaces the "Create a folder per capture" checkbox, whose OFF state filed every capture flat in the storage
    /// root. That layout is gone for new captures: a day folder holds the files named exactly as the flat layout named
    /// them, so a file moved out of its folder still says what it is.
    /// </summary>
    internal static class FolderLayout
    {
        public const string PerCapture = "capture";
        public const string ByDate = "date";
        /// <summary>The key the Radio row stores. The legacy checkbox's key is read, never written, and stays in
        /// settings.json as it was.</summary>
        public const string SettingKey = "folderLayout";
        public const string LegacySettingKey = "folderPerCapture";

        public static bool IsKnown(string id) { return id == PerCapture || id == ByDate; }

        /// <summary>The two options, in the owner's words, in the order they appear.</summary>
        public static string[] Displays() { return new[] { "Create a folder per capture", "Create a folder by date" }; }

        public static string ToDisplay(string id) { return Displays()[id == ByDate ? 1 : 0]; }

        /// <summary>The id behind an option, or null for text that is not one: the Radio row collects "" when the value
        /// it was loaded with matched no option, and that must store nothing rather than a guess.</summary>
        public static string FromDisplay(string display)
        {
            string[] displays = Displays();
            if (string.Equals(display, displays[0], StringComparison.Ordinal)) return PerCapture;
            if (string.Equals(display, displays[1], StringComparison.Ordinal)) return ByDate;
            return null;
        }

        /// <summary>
        /// The stored layout, or the one the old checkbox meant. A recognised new value always wins; the checkbox is
        /// consulted only when there is none. OFF filed captures flat in the root, and the layout nearest to that which
        /// writes nothing new into the root is by date; ON, or never set, is per capture. Pure, for the reason
        /// AgentMode.Migrate gives: a migration runs once, on someone else's machine, and is untestable when it can
        /// only be reached through a settings store.
        /// </summary>
        public static string Migrate(string stored, bool legacyFolderPerCapture)
        {
            if (IsKnown(stored)) return stored;
            return legacyFolderPerCapture ? PerCapture : ByDate;
        }
    }

    /// <summary>
    /// Decides where a capture's files live, names them "{meeting} - {timestamp}" (sanitized, with a
    /// timestamp-only fallback when there is no meeting), and purges the ephemeral media (audio + screenshots)
    /// older than the retention window while KEEPING transcripts forever. A capture is its own folder (per capture)
    /// or a set of base-named files in the folder for its day (by date, 2.0.0); before 2.0.0 the second layout put
    /// those files flat in the root, and the purge still knows that shape for the files already there.
    /// </summary>
    internal sealed class CaptureStore
    {
        private static readonly TimeSpan Retention = TimeSpan.FromHours(72);

        /// <summary>The file stem inside a per-capture folder: "recording.wav", and the scratch tracks
        /// "recording.system.wav" / "recording.mic.wav" AudioRecorder writes beside it. One constant, so the
        /// purge parses the same string NewCapture builds paths from.</summary>
        internal const string FolderPrefix = "recording";

        public string Root { get; private set; }
        /// <summary><see cref="FolderLayout.PerCapture"/> or <see cref="FolderLayout.ByDate"/>.</summary>
        public string Layout { get; private set; }

        public CaptureStore(string root, string layout)
        {
            Root = string.IsNullOrWhiteSpace(root) ? DefaultRoot() : root.Trim();
            Layout = FolderLayout.IsKnown(layout) ? layout : FolderLayout.PerCapture;
        }

        public static string DefaultRoot()
        {
            try { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Remembrance"); }
            catch { return "Remembrance"; }
        }

        // Build the paths a capture starting now writes to. Per capture: a folder named for the capture, with simple
        // file names inside. By date (2.0.0): the folder for the capture's LOCAL start date, holding files named as the
        // flat root layout named them before 2.0.0 ("<meeting> - <stamp>.wav" and its siblings, "<base> - snap ...png"),
        // so a file moved out of the folder still says what it is. Nothing new is written flat into the root.
        public CapturePaths NewCapture(string meetingName, DateTimeOffset now)
        {
            DateTimeOffset local = now.ToLocalTime();
            string stamp = local.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
            string meeting = Sanitize(meetingName);
            string baseName = string.IsNullOrEmpty(meeting) ? stamp : meeting + " - " + stamp;
            bool perCapture = Layout != FolderLayout.ByDate;
            string dir = perCapture ? Path.Combine(Root, baseName) : Path.Combine(Root, local.ToString(DayFormat, CultureInfo.InvariantCulture));
            bool created = !Directory.Exists(dir);
            Directory.CreateDirectory(dir);
            string prefix = perCapture ? FolderPrefix : baseName;
            return new CapturePaths
            {
                Directory = dir,
                CreatedDirectory = created,
                Audio = Path.Combine(dir, prefix + ".wav"),
                Transcript = Path.Combine(dir, prefix + ".transcript.txt"),
                Summary = Path.Combine(dir, prefix + ".summary.txt"),
                BaseName = baseName,
                SnapshotPrefix = perCapture ? "snap" : baseName + " - snap",
                StartedAt = now,
            };
        }

        /// <summary>The folder a snapshot taken while NOTHING is recording goes into (2.0.0, the owner's decision): that
        /// day's folder by date, the "Snapshots" folder per capture. Never the root any more; "snap" files already in
        /// the root are legacy, and the purge still takes them as before.</summary>
        public string SnapshotDirectory(DateTimeOffset now)
        {
            return Layout == FolderLayout.ByDate
                ? Path.Combine(Root, now.ToLocalTime().ToString(DayFormat, CultureInfo.InvariantCulture))
                : Path.Combine(Root, SnapshotFolderName);
        }

        // Delete audio + screenshots older than the retention window; never a transcript. Best-effort per file.
        //
        // IT MAY ONLY DELETE FILES THIS MODULE WROTE, and that is the whole design of this method rather than
        // a precaution bolted onto it. Root is the free-text `storageLocation` setting, labelled "Where
        // recordings are stored" and also settable with a folder picker, so a user may perfectly reasonably
        // point it at Documents (the PARENT of the default root) or at Pictures. This used to enumerate
        // AllDirectories and File.Delete -- not the recycle bin -- every .wav, .mp3 and .png older than 72
        // hours anywhere beneath it, and it ran on Init and then hourly (a minute after Init and then hourly
        // since F180). Pointed at Pictures, the first tick permanently destroyed the user's photo library
        // with no prompt.
        //
        // Four independent narrowings, because one is a rule and four is a design:
        //   * ONLY THIS MODULE'S OWN FILE SHAPES, which NamesThisModuleWrites spells out from the same
        //     strings NewCapture builds paths from.
        //   * ONE LEVEL DEEP. A capture is either a file in the root or a file in a capture folder directly
        //     under it; this module never creates a third level, so recursing further can only ever reach
        //     somebody else's data.
        //   * ONLY FOLDERS THIS MODULE NAMED. The one level it does descend into is a folder whose name
        //     parses as NewCapture's "<meeting> - <stamp>" / "<stamp>", the same test the flat audio shapes
        //     use. Until 2026-09-30 every immediate subfolder was walked, and inside one the shapes are the
        //     bare "recording.wav" / "recording.system.wav" / "snap <stamp>.png", so with storageLocation set
        //     to Documents a Documents\Zoom\recording.wav or Documents\Audacity\recording.wav older than 72
        //     hours was deleted: the folder was the one dimension of the shape 1.0.16 left unparsed (RA-151).
        //   * NO .mp3, EVER. This module does not write one -- it records .wav and screenshots .png -- so
        //     that extension could only ever have matched a file belonging to someone else.
        // Since 2.0.0 two more folder shapes are walked, each as strictly: a day folder named exactly "yyyy-MM-dd" for
        // a real date (the by-date layout), judged by the FLAT rules because its files carry the flat names, and the
        // folder named exactly "Snapshots" (where a snapshot taken with nothing recording goes, per capture), in which
        // only the snapshot shape counts. A bare date or "Snapshots" is a weak name -- a user may have a folder called
        // that -- so neither kind is ever removed unless this same pass deleted one of this module's files from it and
        // left it empty (PurgeDayOrSnapshotFolder).
        public void Purge()
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                DateTime cutoff = DateTime.UtcNow - Retention;
                PurgeOneDirectory(Root, cutoff, false);
                foreach (string sub in Directory.EnumerateDirectories(Root))
                {
                    if (PurgeDayOrSnapshotFolder(sub, cutoff)) continue;
                    if (!IsCaptureFolderName(Path.GetFileName(sub))) continue;
                    // Read BEFORE the files go: deleting an entry bumps the directory's own write time, so a
                    // folder judged afterwards would read as touched today and wait another whole window.
                    bool aged = IsOlderThan(sub, cutoff);
                    PurgeOneDirectory(sub, cutoff, true);
                    // A capture folder this pass has emptied goes with its last file, and so does one that was
                    // already empty: a start that failed after a packet had landed kept its scratch (audio is
                    // never deleted on a guess), the purge later removed that scratch, and the folder it had been
                    // in stayed for ever (R-037). Name-parsed like everything else here, empty only, and only
                    // once it is older than the window itself, so a folder NewCapture made a moment ago for a
                    // recording whose first writer has not opened yet is never in reach.
                    if (aged) TryRemoveEmptyCaptureFolder(sub);
                }
            }
            catch { }
        }

        /// <summary>The folder's own age for the purge: the later of its creation and last-write times, so a
        /// folder something wrote into today is young whatever day it was made.</summary>
        private static bool IsOlderThan(string directory, DateTime cutoffUtc)
        {
            try
            {
                DateTime created = Directory.GetCreationTimeUtc(directory);
                DateTime written = Directory.GetLastWriteTimeUtc(directory);
                return (created > written ? created : written) < cutoffUtc;
            }
            catch { return false; }
        }

        /// <summary>Is this the name of a folder <see cref="NewCapture"/> would have made? Its baseName,
        /// "&lt;meeting&gt; - &lt;stamp&gt;" or "&lt;stamp&gt;" alone, case-insensitively; the purge descends into
        /// nothing else (RA-151).</summary>
        internal static bool IsCaptureFolderName(string folderName)
        {
            if (string.IsNullOrEmpty(folderName)) return false;
            return IsCaptureBaseName(folderName.ToLowerInvariant());
        }

        private static void PurgeOneDirectory(string dir, DateTime cutoff, bool insideCaptureFolder)
        {
            PurgeFiles(dir, cutoff, name => NamesThisModuleWrites(name, insideCaptureFolder));
        }

        /// <summary>Delete, one level deep, each ephemeral file older than <paramref name="cutoff"/> whose name
        /// <paramref name="ours"/> accepts; best-effort per file. Returns how many it deleted, which is what lets a day
        /// or Snapshots folder be removed only when this pass emptied it.</summary>
        private static int PurgeFiles(string dir, DateTime cutoff, Func<string, bool> ours)
        {
            int deleted = 0;
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!IsEphemeral(file)) continue;
                    if (!ours(Path.GetFileName(file))) continue;
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff) { File.Delete(file); deleted++; }
                    }
                    catch { }
                }
            }
            catch { }
            return deleted;
        }

        /// <summary>The by-date layout's folder name: the capture's LOCAL start date, exactly this format.</summary>
        internal const string DayFormat = "yyyy-MM-dd";

        /// <summary>Where a snapshot taken while nothing is recording goes in the per-capture layout (2.0.0).</summary>
        internal const string SnapshotFolderName = "Snapshots";

        /// <summary>Is this the name of a by-date folder? Exactly "yyyy-MM-dd", and a real date: "2026-13-45", a one-digit
        /// day or "2026-10-02 notes" is not.</summary>
        internal static bool IsDayFolderName(string folderName)
        {
            if (string.IsNullOrEmpty(folderName) || folderName.Length != DayFormat.Length) return false;
            DateTime ignored;
            return DateTime.TryParseExact(folderName, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out ignored);
        }

        /// <summary>Is this the snapshot folder? The exact name "Snapshots", case included: a user's own "snapshots" or
        /// "Snapshots 2" is somebody else's.</summary>
        internal static bool IsSnapshotFolderName(string folderName)
        {
            return string.Equals(folderName, SnapshotFolderName, StringComparison.Ordinal);
        }

        /// <summary>
        /// Purge a day folder by the flat rules (its files carry the flat names) or the Snapshots folder by the snapshot
        /// shape alone, and remove it only when THIS pass deleted one of this module's files from it and left it empty:
        /// a user's own empty folder that happens to be called "2026-10-01" or "Snapshots" is never removed. True when
        /// <paramref name="sub"/> was one of the two, so the caller does not judge it again as a capture folder.
        /// </summary>
        private static bool PurgeDayOrSnapshotFolder(string sub, DateTime cutoff)
        {
            string name = Path.GetFileName(sub);
            Func<string, bool> ours;
            if (IsDayFolderName(name)) ours = file => NamesThisModuleWrites(file, false);
            else if (IsSnapshotFolderName(name)) ours = file => IsStampedSnapshotName(file.ToLowerInvariant());
            else return false;
            int deleted = PurgeFiles(sub, cutoff, ours);
            if (deleted > 0) TryRemoveEmptyCaptureFolder(sub);
            return true;
        }

        /// <summary>
        /// Does this file name match something <see cref="NewCapture"/> would have produced?
        ///
        /// Derived from the same strings NewCapture and AudioRecorder use, so they cannot drift apart silently:
        /// inside a capture folder it writes "recording.wav", the two scratch WAVs and stamped snapshot PNGs;
        /// flat in the root it writes "{stamp}.wav" or "{meeting} - {stamp}.wav", their scratch siblings and
        /// "{base} - snap ..." PNGs. Every branch is a PARSED shape (a trailing stamp, a known suffix), never an
        /// extension alone. Until 1.0.16 the flat audio case matched ANY .wav in the root, and this summary went
        /// on describing that loose rule after the body stopped implementing it (F170); the one-level rule and
        /// the folder-per-capture default are the second and third fences.
        /// </summary>
        internal static bool NamesThisModuleWrites(string fileName, bool insideCaptureFolder)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string lower = fileName.ToLowerInvariant();
            // EVERY branch is a parsed shape, not an extension or a prefix. Three of the four used to be
            // looser than this method's own doc allows, and each was a way to delete somebody else's
            // file out of a folder they chose:
            //   * in a capture folder, `snap*.png` matched ANY name starting "snap" -- and this branch had
            //     no negative coverage at all, so nothing said otherwise;
            //   * in the root, `.wav` matched ANY wav, so a user who pointed storageLocation at a folder
            //     holding their own audio lost every file in it older than the retention window;
            //   * in the root, `Contains(" - snap")` matched "my holiday - snapshot.png".
            // The narrowing loses nothing: these are the shapes NewCapture and TakeSnapshot build.
            // The two SCRATCH shapes AudioRecorder writes beside the recording -- "<stem>.system.wav" and
            // "<stem>.mic.wav" -- are shapes too, and until 2026-09-29 neither branch knew them. The normal
            // stop deletes them itself after the mix, so the gap showed only on an abnormal end (a crash, a
            // Restart Manager kill, an exit inside the mix window, a start that failed half-way): exactly when
            // they hold the only copy of the audio and are largest, they matched nothing here and stayed for
            // ever against the 72-hour promise (F171). The suffixes come from AudioRecorder's own constants,
            // for the same reason the rest of this method reads NewCapture's strings: so they cannot drift.
            if (insideCaptureFolder)
                return lower == FolderPrefix + ".wav"
                    || lower == FolderPrefix + AudioRecorder.SystemScratchSuffix
                    || lower == FolderPrefix + AudioRecorder.MicScratchSuffix
                    || IsStampedSnapshotName(lower);
            return IsCaptureAudioName(lower)
                || IsScratchAudioName(lower)
                || IsFlatSnapshotName(lower)
                || IsStampedSnapshotName(lower);
        }

        // The one timestamp this module writes, in one place. NewCapture and TakeSnapshot both format
        // with it, so the purge parses the same thing they produce.
        private const string StampFormat = "yyyy-MM-dd HH-mm-ss";
        private const int StampLength = 19;

        /// <summary>
        /// True when the name ends with exactly the timestamp this module writes, handing back everything
        /// in front of it. PARSED rather than pattern-matched, so "2026-13-45 99-99-99" does not qualify
        /// and neither does a name that merely contains digits and dashes.
        /// </summary>
        private static bool TryStripTrailingStamp(string name, out string head)
        {
            head = null;
            if (string.IsNullOrEmpty(name) || name.Length < StampLength) return false;
            DateTime ignored;
            if (!DateTime.TryParseExact(
                    name.Substring(name.Length - StampLength), StampFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out ignored)) return false;
            head = name.Substring(0, name.Length - StampLength);
            return true;
        }

        private static bool TryStripSuffix(string name, string suffix, out string stem)
        {
            stem = null;
            if (string.IsNullOrEmpty(name) || !name.EndsWith(suffix, StringComparison.Ordinal)) return false;
            stem = name.Substring(0, name.Length - suffix.Length);
            return true;
        }

        /// <summary>
        /// "snap &lt;stamp&gt;.png" -- the standalone hotkey snapshot (TakeSnapshot's else branch: the
        /// ROOT before 2.0.0, that day's folder or the Snapshots folder since), and the snapshot inside a
        /// capture folder, which are the same shape because CapturePaths.SnapshotPrefix is the bare "snap"
        /// in folder-per-capture mode.
        ///
        /// The standalone one was purged by nothing until 2026-09-27: the root gate wanted " - snap",
        /// which is the prefix used only when a recording IS in flight and folder-per-capture is off, so
        /// a snapshot taken on its own -- the ordinary way to use that hotkey -- sat on disk forever
        /// against a 72-hour retention the module header and the transcript stub both promise. A privacy
        /// defect rather than a disk one: these are captures of every monitor.
        ///
        /// MATCHED BY SHAPE, and that is the whole point of doing it here rather than loosening a prefix
        /// test. This method decides what gets DELETED out of a folder the user chose -- storageLocation
        /// is free text with a folder picker, and PurgeOneDirectory's header warns it may reasonably be
        /// Documents or Pictures -- so "snapshot of my cat.png" must not qualify. Only the exact stamp
        /// this module writes does.
        /// </summary>
        internal static bool IsStampedSnapshotName(string lowerFileName)
        {
            string stem, head;
            if (!TryStripSuffix(lowerFileName, ".png", out stem)) return false;
            stem = StripCollisionSuffix(stem);
            if (!TryStripTrailingStamp(stem, out head)) return false;
            return string.Equals(head, "snap ", StringComparison.Ordinal);
        }

        /// <summary>
        /// "&lt;baseName&gt; - snap &lt;stamp&gt;.png" -- a snapshot taken while a recording is in flight
        /// with folder-per-capture OFF, so it lands in the root carrying the capture's own base name.
        /// The separator is part of the shape: the old `Contains(" - snap")` test also matched a user's
        /// "my holiday - snapshot.png".
        /// </summary>
        internal static bool IsFlatSnapshotName(string lowerFileName)
        {
            const string Middle = " - snap ";
            string stem, head;
            if (!TryStripSuffix(lowerFileName, ".png", out stem)) return false;
            stem = StripCollisionSuffix(stem);
            if (!TryStripTrailingStamp(stem, out head)) return false;
            return head.Length > Middle.Length && head.EndsWith(Middle, StringComparison.Ordinal);
        }

        /// <summary>
        /// The one thing <see cref="UniqueSnapshotPath"/> may append to a snapshot stem: " (2)" .. " (99)". A
        /// snapshot's name is stamped to the second and the hotkey can be pressed twice inside one -- two
        /// consecutive slides half a second apart -- so until 2026-09-30 the second Bitmap.Save truncated the
        /// first and both announced "Snapshot saved." (RA-156). The suffix is a PARSED shape like the stamp
        /// itself: a space, an opening bracket, one or two digits, a closing bracket, nothing else, so
        /// "snap 2026-09-27 12-00-00 (2).png" is ours to purge and "snap 2026-09-27 12-00-00 (x).png" or
        /// "... (2)(3).png" is not.
        /// </summary>
        private const int MaxSnapshotCollisions = 99;

        private static string StripCollisionSuffix(string stem)
        {
            if (string.IsNullOrEmpty(stem) || !stem.EndsWith(")", StringComparison.Ordinal)) return stem;
            int open = stem.LastIndexOf(" (", StringComparison.Ordinal);
            if (open < 0) return stem;
            string digits = stem.Substring(open + 2, stem.Length - open - 3);
            int n;
            if (digits.Length < 1 || digits.Length > 2 ||
                !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out n) ||
                n < 2 || n > MaxSnapshotCollisions) return stem;
            return stem.Substring(0, open);
        }

        /// <summary>
        /// The path itself when nothing is there yet; otherwise the first of "&lt;stem&gt; (2)", "(3)" ... that
        /// is free, so a second snapshot inside the same wall-clock second lands beside the first rather than
        /// over it (RA-156). Past <see cref="MaxSnapshotCollisions"/> the last candidate is returned and does
        /// overwrite, a bound nothing reaches: the single-flight gate refuses a press while an encode (~0.3 s)
        /// is running, so a second holds three or four at most.
        /// </summary>
        internal static string UniqueSnapshotPath(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return path;
            string directory = Path.GetDirectoryName(path) ?? "";
            string stem = Path.GetFileNameWithoutExtension(path);
            string extension = Path.GetExtension(path);
            string candidate = path;
            for (int n = 2; n <= MaxSnapshotCollisions; n++)
            {
                candidate = Path.Combine(directory, stem + " (" + n.ToString(CultureInfo.InvariantCulture) + ")" + extension);
                if (!File.Exists(candidate)) return candidate;
            }
            return candidate;
        }

        /// <summary>
        /// The recording, in the root: "&lt;stamp&gt;.wav" when there is no meeting name, otherwise
        /// "&lt;meeting&gt; - &lt;stamp&gt;.wav". Both come straight from NewCapture's baseName.
        ///
        /// This replaced a bare `.wav` test, which is the single widest thing the purge ever matched: a
        /// user who pointed storageLocation at a folder holding their own audio lost every file in it
        /// older than the retention window, permanently and without a prompt. An extension is not a shape.
        /// </summary>
        internal static bool IsCaptureAudioName(string lowerFileName)
        {
            string stem;
            if (!TryStripSuffix(lowerFileName, ".wav", out stem)) return false;
            return IsCaptureBaseName(stem);
        }

        /// <summary>
        /// A scratch track in the root: "&lt;baseName&gt;.system.wav" or "&lt;baseName&gt;.mic.wav", which is
        /// what AudioRecorder writes beside a flat-mode recording while it is in flight. The base-name rule is
        /// IsCaptureAudioName's, so "my.system.wav" and "holiday.mic.wav" do not qualify: the suffix alone is
        /// no more a shape than ".wav" was.
        /// </summary>
        internal static bool IsScratchAudioName(string lowerFileName)
        {
            string stem;
            if (!TryStripSuffix(lowerFileName, AudioRecorder.SystemScratchSuffix, out stem)
                && !TryStripSuffix(lowerFileName, AudioRecorder.MicScratchSuffix, out stem)) return false;
            return IsCaptureBaseName(stem);
        }

        /// <summary>NewCapture's baseName, lower-cased: "&lt;stamp&gt;" alone, or "&lt;meeting&gt; - &lt;stamp&gt;".</summary>
        private static bool IsCaptureBaseName(string stem)
        {
            const string Separator = " - ";
            string head;
            if (!TryStripTrailingStamp(stem, out head)) return false;
            if (head.Length == 0) return true;
            return head.Length > Separator.Length && head.EndsWith(Separator, StringComparison.Ordinal);
        }

        /// <summary>
        /// Remove the folder NewCapture made for a capture that never started, and only then: a folder with
        /// anything at all in it is left alone, because this runs in a location the user chose. Returns true
        /// only when it removed the folder (F171).
        /// </summary>
        internal static bool TryRemoveEmptyCaptureFolder(string directory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
                using (IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator())
                    if (entries.MoveNext()) return false;
                Directory.Delete(directory);
                return true;
            }
            catch { return false; }
        }

        // Only the recorded MEDIA is ephemeral. The written record is permanent: it is the thing worth
        // keeping, it is small, and a purge that ate it would defeat the point of recording at all.
        // Transcripts and summaries are listed explicitly rather than left to fall through the extension
        // test below, so the rule reads as a decision instead of an accident of ordering.
        internal static bool IsEphemeral(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string lower = path.ToLowerInvariant();
            if (lower.EndsWith(".transcript.txt")) return false;
            if (lower.EndsWith(".summary.txt")) return false;
            // No .mp3. This module records .wav and screenshots .png; it has never written an .mp3, so that
            // extension could only ever match a file belonging to somebody else.
            return lower.EndsWith(".wav") || lower.EndsWith(".png");
        }

        public static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var sb = new StringBuilder();
            foreach (char c in name.Trim())
                sb.Append("\\/:*?\"<>|".IndexOf(c) >= 0 || c < ' ' ? '_' : c);
            string s = sb.ToString().Trim();
            return s.Length > 120 ? s.Substring(0, 120).Trim() : s;
        }
    }

    internal sealed class CapturePaths
    {
        public string Directory;
        /// <summary>NewCapture made <see cref="Directory"/> itself. A start that fails removes the folder only then: a
        /// day folder that already held other captures, or a user's own empty folder of that name, is left alone.</summary>
        public bool CreatedDirectory;
        public string Audio;
        public string Transcript;
        public string Summary;
        public string BaseName;
        public string SnapshotPrefix;
        /// <summary>When the capture started: the instant the stamp in <see cref="BaseName"/> was formatted
        /// from. The transcript header prints it as "Recorded:", which until 2026-09-30 printed the moment
        /// whisper ran instead -- the stop time on the stop path, days later on the manual one (RA-166).</summary>
        public DateTimeOffset StartedAt;
    }
}
