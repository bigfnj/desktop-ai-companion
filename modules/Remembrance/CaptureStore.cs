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
    /// Decides where a capture's files live, names them "{date} {time} {meeting}" (2.6.0; sanitized, the stamp alone
    /// when there is no meeting), and purges the ephemeral media (audio + screenshots)
    /// older than the retention window while KEEPING transcripts forever. A capture is its own folder (per capture)
    /// or a set of base-named files in the folder for its day (by date, 2.0.0); before 2.0.0 the second layout put
    /// those files flat in the root, and the purge still knows that shape for the files already there. Before 2.6.0 a
    /// capture was named "{meeting} - {timestamp}"; the purge knows that shape too, and CaptureStore.Rename.cs moves
    /// those names to the new one when the user asks.
    /// </summary>
    internal sealed partial class CaptureStore
    {
        /// <summary>How long the recorded media is kept. Internal so the log lines that report the purge
        /// (RemembranceModule.PurgeLine, ScratchCleanupLine) state the window from the same value it runs on.</summary>
        internal static readonly TimeSpan Retention = TimeSpan.FromHours(72);

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

        /// <summary>The three variables OneDrive sets for its folders: the one in use, the work or school one, the personal one.</summary>
        internal static readonly string[] OneDriveVariables = { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" };

        /// <summary>
        /// The OneDrive folder the storage root sits in, or null when it is in none (2.4.0, the owner's decision of
        /// 2026-10-15). Documents is often redirected into OneDrive, and with it the default root, so recordings of
        /// meetings upload to the cloud and a deleted recording -- the 72-hour purge's, or the unattended trim's cut --
        /// stays in OneDrive's recycle bin for its own retention. The Status line says so; nothing is moved.
        ///
        /// A folder-boundary match against each of <see cref="OneDriveVariables"/>, read through
        /// <paramref name="environment"/> so the self-test can stand them in: "C:\Users\someone\OneDrive - Contoso" holds
        /// "...\OneDrive - Contoso\Documents\Remembrance" but not "...\OneDrive - Contoso Archive". Case-insensitive, as the
        /// file system is. Pure apart from the lookups, and never throws.
        /// </summary>
        internal static string OneDriveRootOf(string root, Func<string, string> environment)
        {
            if (string.IsNullOrWhiteSpace(root) || environment == null) return null;
            string folder = NormalizedFolder(root);
            if (folder == null) return null;
            foreach (string variable in OneDriveVariables)
            {
                string value;
                try { value = environment(variable); }
                catch { continue; }
                string oneDrive = NormalizedFolder(value);
                if (oneDrive == null) continue;
                if (string.Equals(folder, oneDrive, StringComparison.OrdinalIgnoreCase) ||
                    folder.StartsWith(oneDrive.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return oneDrive;
            }
            return null;
        }

        // A full path with forward slashes turned back and no trailing separator, or null for a value that is blank or
        // not rooted (a relative storage location is resolved against no known folder, so it matches nothing).
        private static string NormalizedFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                string trimmed = path.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                if (!Path.IsPathRooted(trimmed)) return null;
                string full = Path.GetFullPath(trimmed);
                string bare = full.TrimEnd(Path.DirectorySeparatorChar);
                // "C:\" trims to "C:", which is not a folder; keep the root's own separator.
                return bare.Length == 0 || bare.EndsWith(":", StringComparison.Ordinal) ? full : bare;
            }
            catch { return null; }
        }

        // Build the paths a capture starting now writes to. Per capture: a folder named for the capture, with simple
        // file names inside. By date (2.0.0): the folder for the capture's LOCAL start date, holding files named after
        // the capture ("<base>.wav" and its siblings, "<base> - snap ...png"), so a file moved out of the folder still
        // says what it is. Nothing new is written flat into the root. The base is "<date> <time> <meeting>" since 2.6.0,
        // made free with " (2)" .. when that minute's name is already in use (FreeBaseName).
        public CapturePaths NewCapture(string meetingName, DateTimeOffset now)
        {
            DateTimeOffset local = now.ToLocalTime();
            string meeting = Sanitize(meetingName);
            bool perCapture = Layout != FolderLayout.ByDate;
            string dayDir = Path.Combine(Root, local.ToString(DayFormat, CultureInfo.InvariantCulture));
            string baseName = FreeBaseName(CaptureBaseName(local, meeting), perCapture ? Root : dayDir, perCapture, null);
            string dir = perCapture ? Path.Combine(Root, baseName) : dayDir;
            bool created = !Directory.Exists(dir);
            Directory.CreateDirectory(dir);
            string prefix = perCapture ? FolderPrefix : baseName;
            return new CapturePaths
            {
                Directory = dir,
                CreatedDirectory = created,
                Audio = Path.Combine(dir, prefix + ".wav"),
                Transcript = Path.Combine(dir, prefix + ".transcript.txt"),
                Summary = Path.Combine(dir, prefix + SummarySuffix),
                BaseName = baseName,
                SnapshotPrefix = perCapture ? "snap" : baseName + " - snap",
                StartedAt = now,
            };
        }

        /// <summary>
        /// A capture's own stamp since 2.6.0: to the MINUTE, at the FRONT of its name, so "2026-10-20 14-08 Design Review"
        /// (the owner's request of 2026-10-20: "when I open the folder, the meetings are in the order I had them in").
        /// Date first and a 24-hour time, so names sort by when the meeting began wherever they sit, a day folder or the
        /// root of the per-capture layout alike; ":" cannot appear in a Windows file name, hence "14-08". Snapshots keep
        /// <see cref="StampFormat"/>, to the second, and the pre-2.6.0 "&lt;meeting&gt; - &lt;yyyy-MM-dd HH-mm-ss&gt;" stays
        /// a shape the purge and the rename read.
        /// </summary>
        internal const string CaptureStampFormat = "yyyy-MM-dd HH-mm";
        private const int CaptureStampLength = 16;

        /// <summary>"2026-10-20 14-08 Sprint Review", or the stamp alone when there is no meeting name.</summary>
        internal static string CaptureBaseName(DateTimeOffset local, string sanitizedMeeting)
        {
            return TimeFirstBase(local.DateTime, sanitizedMeeting);
        }

        /// <summary>The same from a local wall-clock time: what the rename gives a pre-2.6.0 capture's own stamp.</summary>
        internal static string TimeFirstBase(DateTime localStamp, string meeting)
        {
            string stamp = localStamp.ToString(CaptureStampFormat, CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(meeting) ? stamp : stamp + " " + meeting;
        }

        // A summary is written as Markdown since 2.9.0 (2026-10-29, the owner's request: the summary text is Markdown, so it
        // opens as Markdown). Summaries written before keep ".summary.txt", and everything that finds, keeps or names a
        // summary accepts both endings.
        internal const string SummarySuffix = ".summary.md";
        internal const string LegacySummarySuffix = ".summary.txt";
        internal static readonly string[] SummarySuffixes = { SummarySuffix, LegacySummarySuffix };

        /// <summary>Whether a path is a summary's, either ending, any case.</summary>
        internal static bool IsSummaryPath(string path) => !string.IsNullOrEmpty(path) &&
            (path.EndsWith(SummarySuffix, StringComparison.OrdinalIgnoreCase) || path.EndsWith(LegacySummarySuffix, StringComparison.OrdinalIgnoreCase));

        // Everything one capture writes under its base name in a day folder; each must be free for the name to be.
        private static readonly string[] FlatSuffixes =
            { ".wav", ".transcript.txt", SummarySuffix, LegacySummarySuffix, AudioRecorder.SystemScratchSuffix, AudioRecorder.MicScratchSuffix };

        /// <summary>
        /// <paramref name="baseName"/> when nothing in <paramref name="folder"/> uses it, otherwise the first of
        /// "&lt;base&gt; (2)" .. " (99)" that is free (2.6.0). The stamp is to the minute now, so two captures of one
        /// meeting started inside the same minute would share a name, and the second must never write into the first's
        /// folder or over its files. Per capture the folder is the name; by date, every file the capture writes under
        /// it. <paramref name="reserved"/> holds names a rename has already claimed in the same folder, or null. Past
        /// (99) it throws, a bound nothing reaches, rather than overwrite anything.
        /// </summary>
        internal static string FreeBaseName(string baseName, string folder, bool perCapture, ICollection<string> reserved)
        {
            for (int n = 1; n <= MaxSnapshotCollisions; n++)
            {
                string candidate = n == 1 ? baseName : baseName + " (" + n.ToString(CultureInfo.InvariantCulture) + ")";
                if (reserved != null && reserved.Contains(candidate)) continue;
                if (!BaseNameTaken(folder, candidate, perCapture)) return candidate;
            }
            throw new IOException("Every name from (2) to (99) is already in use for this capture's minute.");
        }

        private static bool BaseNameTaken(string folder, string candidate, bool perCapture)
        {
            string at = Path.Combine(folder, candidate);
            if (perCapture) return Directory.Exists(at) || File.Exists(at);
            foreach (string suffix in FlatSuffixes)
                if (File.Exists(at + suffix)) return true;
            return false;
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
        //
        // IT SAYS WHAT IT DID (lane feature/remembrance-delete-logging, the owner's rule of 2026-10-07). This returned
        // nothing and every catch below was empty: it counted its deletions only to decide a folder's removal, and a
        // file it could not delete was skipped without a trace, so on 2026-10-07 whether a test instance pointed at a
        // real Documents\Remembrance had deleted anything could be told only by looking in the folder. The report holds
        // counts by kind and exception TYPE names, never a path (DeletionReport says why), and the module turns it into
        // its one log line. Still best-effort per file, as before: a failure is counted and the walk goes on.
        public DeletionReport Purge()
        {
            var report = new DeletionReport();
            // Never beside a rename (2.6.0): a file the rename is moving is neither where the purge listed it nor where it
            // is going, and the two would each count the other's work as a failure.
            lock (FileWorkGate)
            try
            {
                if (!Directory.Exists(Root)) return report;
                DateTime cutoff = DateTime.UtcNow - Retention;
                PurgeOneDirectory(Root, cutoff, false, report);
                foreach (string sub in Directory.EnumerateDirectories(Root))
                {
                    if (PurgeDayOrSnapshotFolder(sub, cutoff, report)) continue;
                    if (!IsCaptureFolderName(Path.GetFileName(sub))) continue;
                    // Read BEFORE the files go: deleting an entry bumps the directory's own write time, so a
                    // folder judged afterwards would read as touched today and wait another whole window.
                    bool aged = IsOlderThan(sub, cutoff);
                    PurgeOneDirectory(sub, cutoff, true, report);
                    // A capture folder this pass has emptied goes with its last file, and so does one that was
                    // already empty: a start that failed after a packet had landed kept its scratch (audio is
                    // never deleted on a guess), the purge later removed that scratch, and the folder it had been
                    // in stayed for ever (R-037). Name-parsed like everything else here, empty only, and only
                    // once it is older than the window itself, so a folder NewCapture made a moment ago for a
                    // recording whose first writer has not opened yet is never in reach.
                    if (aged) RemoveEmptyFolder(sub, report);
                }
            }
            // The storage root's own listing is the one thing in here not guarded nearer. A root this process may not
            // list is counted as a folder it could not read, so it says so on every pass instead of purging nothing.
            catch (Exception ex) { report.FolderUnread(ex); }
            return report;
        }

        /// <summary>Remove an empty folder for the purge, and count what happened: removed, failed with its type, or
        /// kept because something is in it (which is not a failure).</summary>
        private static void RemoveEmptyFolder(string directory, DeletionReport report)
        {
            Exception failure;
            if (TryRemoveEmptyCaptureFolder(directory, out failure)) report.Folders++;
            else if (failure != null) report.FolderFailed(failure);
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
        /// "&lt;date&gt; &lt;time&gt; &lt;meeting&gt;" (2.6.0) or the pre-2.6.0 "&lt;meeting&gt; - &lt;stamp&gt;", either with
        /// its stamp alone, case-insensitively; the purge descends into nothing else (RA-151).</summary>
        internal static bool IsCaptureFolderName(string folderName)
        {
            if (string.IsNullOrEmpty(folderName)) return false;
            string lower = folderName.ToLowerInvariant();
            return IsCaptureBaseName(lower) || IsTimeFirstBaseName(lower, null);
        }

        private static void PurgeOneDirectory(string dir, DateTime cutoff, bool insideCaptureFolder, DeletionReport report)
        {
            PurgeFiles(dir, cutoff, name => NamesThisModuleWrites(name, insideCaptureFolder), report);
        }

        /// <summary>Delete, one level deep, each ephemeral file older than <paramref name="cutoff"/> whose name
        /// <paramref name="ours"/> accepts; best-effort per file, each deletion and each failure counted in
        /// <paramref name="report"/>. Returns how many it deleted, which is what lets a day or Snapshots folder be removed
        /// only when this pass emptied it.</summary>
        private static int PurgeFiles(string dir, DateTime cutoff, Func<string, bool> ours, DeletionReport report)
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
                        if (File.GetLastWriteTimeUtc(file) < cutoff)
                        {
                            File.Delete(file);
                            deleted++;
                            // IsEphemeral has already said it is a .wav or a .png: the recording's audio or a snapshot.
                            if (file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) report.Snapshots++;
                            else report.AudioFiles++;
                        }
                    }
                    // Counted with its type, and the walk goes on; the file is tried again at the next pass. A recording
                    // another program holds open (a player, a sync client, an indexer) is the usual one.
                    catch (Exception ex) { report.FileFailed(ex); }
                }
            }
            catch (Exception ex) { report.FolderUnread(ex); }
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
        private static bool PurgeDayOrSnapshotFolder(string sub, DateTime cutoff, DeletionReport report)
        {
            string name = Path.GetFileName(sub);
            Func<string, bool> ours;
            if (IsDayFolderName(name)) ours = file => NamesThisModuleWrites(file, false, name);
            else if (IsSnapshotFolderName(name)) ours = file => IsStampedSnapshotName(file.ToLowerInvariant());
            else return false;
            int deleted = PurgeFiles(sub, cutoff, ours, report);
            if (deleted > 0) RemoveEmptyFolder(sub, report);
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
            return NamesThisModuleWrites(fileName, insideCaptureFolder, null);
        }

        /// <summary>
        /// The same, for a file in the day folder <paramref name="dayFolderName"/> ("yyyy-MM-dd"), where 2.6.0's
        /// time-first names are ours too, but only with THAT day's date at their front: NewCapture files a capture under
        /// its own start date, so a "2026-10-19 ..." in "2026-10-20" is not a file it wrote there. Null for the storage
        /// root, which holds only the pre-2.0.0 flat names: nothing new has been written flat into the root since
        /// 2.0.0, so the looser-looking "&lt;date&gt; &lt;time&gt; &lt;anything&gt;.wav" has no reason to be ours there.
        /// </summary>
        internal static bool NamesThisModuleWrites(string fileName, bool insideCaptureFolder, string dayFolderName)
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
            DateTime? day = DayOf(dayFolderName);
            return IsCaptureAudioName(lower, day)
                || IsScratchAudioName(lower, day)
                || IsFlatSnapshotName(lower)
                || IsStampedSnapshotName(lower);
        }

        /// <summary>The date a day folder's name stands for, or null for anything that is not one.</summary>
        private static DateTime? DayOf(string dayFolderName)
        {
            DateTime day;
            if (!IsDayFolderName(dayFolderName)) return null;
            DateTime.TryParseExact(dayFolderName, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
            return day;
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
            return IsCaptureAudioName(lowerFileName, null);
        }

        /// <summary>The same, in the day folder for <paramref name="day"/>, where 2.6.0's time-first base for that day
        /// qualifies too (see <see cref="NamesThisModuleWrites(string, bool, string)"/>).</summary>
        internal static bool IsCaptureAudioName(string lowerFileName, DateTime? day)
        {
            string stem;
            if (!TryStripSuffix(lowerFileName, ".wav", out stem)) return false;
            return IsCaptureBaseName(stem) || (day.HasValue && IsTimeFirstBaseName(stem, day));
        }

        /// <summary>
        /// A scratch track in the root: "&lt;baseName&gt;.system.wav" or "&lt;baseName&gt;.mic.wav", which is
        /// what AudioRecorder writes beside a flat-mode recording while it is in flight. The base-name rule is
        /// IsCaptureAudioName's, so "my.system.wav" and "holiday.mic.wav" do not qualify: the suffix alone is
        /// no more a shape than ".wav" was.
        /// </summary>
        internal static bool IsScratchAudioName(string lowerFileName)
        {
            return IsScratchAudioName(lowerFileName, null);
        }

        /// <summary>The same, in the day folder for <paramref name="day"/>, as <see cref="IsCaptureAudioName(string, DateTime?)"/>.</summary>
        internal static bool IsScratchAudioName(string lowerFileName, DateTime? day)
        {
            string stem;
            if (!TryStripSuffix(lowerFileName, AudioRecorder.SystemScratchSuffix, out stem)
                && !TryStripSuffix(lowerFileName, AudioRecorder.MicScratchSuffix, out stem)) return false;
            return IsCaptureBaseName(stem) || (day.HasValue && IsTimeFirstBaseName(stem, day));
        }

        /// <summary>
        /// The front of a 2.6.0 base name: <see cref="CaptureStampFormat"/>, for a real date and time, then the end of the
        /// name or one space and something after it. <paramref name="stamp"/> is what it parsed. A pre-2.6.0 stamp-only
        /// name ("2026-10-20 14-08-25") is not this shape: its seventeenth character is a dash.
        /// </summary>
        private static bool TryStripLeadingCaptureStamp(string name, out DateTime stamp)
        {
            stamp = default(DateTime);
            if (string.IsNullOrEmpty(name) || name.Length < CaptureStampLength) return false;
            if (!DateTime.TryParseExact(name.Substring(0, CaptureStampLength), CaptureStampFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp)) return false;
            if (name.Length == CaptureStampLength) return true;
            return name[CaptureStampLength] == ' ' && name.Length > CaptureStampLength + 1;
        }

        /// <summary>A 2.6.0 base name, lower-cased: the leading stamp alone, or with " &lt;meeting&gt;" and any " (n)"
        /// after it. With <paramref name="day"/> set, the stamp's date must be that day.</summary>
        private static bool IsTimeFirstBaseName(string stem, DateTime? day)
        {
            DateTime stamp;
            if (!TryStripLeadingCaptureStamp(stem, out stamp)) return false;
            return !day.HasValue || stamp.Date == day.Value.Date;
        }

        /// <summary>NewCapture's baseName before 2.6.0, lower-cased: "&lt;stamp&gt;" alone, or "&lt;meeting&gt; - &lt;stamp&gt;".</summary>
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
        /// only when it removed the folder (F171). <paramref name="failure"/> is the exception when the removal
        /// FAILED, and null when the folder was removed or simply kept (missing, or not empty): the caller counts or
        /// logs it by type, where until 2026-10-07 the catch swallowed it (lane feature/remembrance-delete-logging).
        /// </summary>
        internal static bool TryRemoveEmptyCaptureFolder(string directory, out Exception failure)
        {
            failure = null;
            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
                using (IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator())
                    if (entries.MoveNext()) return false;
                Directory.Delete(directory);
                return true;
            }
            catch (Exception ex)
            {
                failure = ex;
                return false;
            }
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
            if (IsSummaryPath(lower)) return false;
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

    /// <summary>
    /// What one pass that deletes the user's data did: how many of each kind went, and what could not be deleted, by
    /// count and exception TYPE name. The owner's rule of 2026-10-07 (lane feature/remembrance-delete-logging): every
    /// deletion of the user's data writes one metadata-only log line, and a failure is never silently swallowed.
    ///
    /// METADATA ONLY, and the type name is the whole reason this class exists rather than a list of exceptions. A
    /// capture's folder and file names carry the meeting's title from the calendar, and an IOException's Message names
    /// the path it failed on ("The process cannot access the file '...\Board offsite - 2026-...\recording.wav'"), so a
    /// message would put the title in diagnostics.log, the file SUPPORT.md asks users to attach to an issue. The
    /// storage location itself is already on screen in the pane. The purge fills every field; the recorder's scratch
    /// cleanup fills only the file failures.
    /// </summary>
    internal sealed class DeletionReport
    {
        /// <summary>Recording audio deleted: recording.wav and its two scratch tracks, under either layout's names.</summary>
        public int AudioFiles;
        /// <summary>Snapshot PNGs deleted.</summary>
        public int Snapshots;
        /// <summary>Capture, day and Snapshots folders removed once empty.</summary>
        public int Folders;
        /// <summary>Files that were due and could not be deleted.</summary>
        public int FilesFailed;
        /// <summary>Empty folders that could not be removed.</summary>
        public int FoldersFailed;
        /// <summary>Folders, the storage root among them, that could not be listed, so nothing in them was looked at.</summary>
        public int FoldersUnread;

        private readonly List<string> _failureTypes = new List<string>();

        public bool Removed { get { return AudioFiles + Snapshots + Folders > 0; } }
        public int FailureCount { get { return FilesFailed + FoldersFailed + FoldersUnread; } }

        /// <summary>The distinct exception type names, in the order first seen: "IOException, UnauthorizedAccessException".</summary>
        public string FailureTypes { get { return string.Join(", ", _failureTypes); } }

        public void FileFailed(Exception ex) { FilesFailed++; NoteType(ex); }

        /// <summary>Add another pass's counts and failure types to this one: the purge of the earlier storage roots says
        /// them all in one line (RemembranceModule.PurgeEarlierRoots, lane feature/remembrance-backlog).</summary>
        public void Add(DeletionReport other)
        {
            if (other == null) return;
            AudioFiles += other.AudioFiles;
            Snapshots += other.Snapshots;
            Folders += other.Folders;
            FilesFailed += other.FilesFailed;
            FoldersFailed += other.FoldersFailed;
            FoldersUnread += other.FoldersUnread;
            foreach (string type in other._failureTypes)
                if (!_failureTypes.Contains(type)) _failureTypes.Add(type);
        }
        public void FolderFailed(Exception ex) { FoldersFailed++; NoteType(ex); }
        public void FolderUnread(Exception ex) { FoldersUnread++; NoteType(ex); }

        private void NoteType(Exception ex)
        {
            string type = ex == null ? "Exception" : ex.GetType().Name;
            if (!_failureTypes.Contains(type)) _failureTypes.Add(type);
        }
    }
}
