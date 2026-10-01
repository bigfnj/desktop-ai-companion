using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// Decides where a capture's files live, names them "{meeting} - {timestamp}" (sanitized, with a
    /// timestamp-only fallback when there is no meeting), and purges the ephemeral media (audio + screenshots)
    /// older than the retention window while KEEPING transcripts forever. A capture is either its own folder
    /// (folder-per-capture on) or a flat set of prefixed files in the root.
    /// </summary>
    internal sealed class CaptureStore
    {
        private static readonly TimeSpan Retention = TimeSpan.FromHours(72);

        /// <summary>The file stem inside a per-capture folder: "recording.wav", and the scratch tracks
        /// "recording.system.wav" / "recording.mic.wav" AudioRecorder writes beside it. One constant, so the
        /// purge parses the same string NewCapture builds paths from.</summary>
        internal const string FolderPrefix = "recording";

        public string Root { get; private set; }
        public bool FolderPerCapture { get; private set; }

        public CaptureStore(string root, bool folderPerCapture)
        {
            Root = string.IsNullOrWhiteSpace(root) ? DefaultRoot() : root.Trim();
            FolderPerCapture = folderPerCapture;
        }

        public static string DefaultRoot()
        {
            try { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Remembrance"); }
            catch { return "Remembrance"; }
        }

        // Build the paths a capture starting now writes to. Flat mode: files prefixed with the name in the root.
        // Folder-per-capture: a folder named for the capture, with simple file names inside.
        public CapturePaths NewCapture(string meetingName, DateTimeOffset now)
        {
            string stamp = now.ToLocalTime().ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
            string meeting = Sanitize(meetingName);
            string baseName = string.IsNullOrEmpty(meeting) ? stamp : meeting + " - " + stamp;
            string dir = FolderPerCapture ? Path.Combine(Root, baseName) : Root;
            Directory.CreateDirectory(dir);
            string prefix = FolderPerCapture ? FolderPrefix : baseName;
            return new CapturePaths
            {
                Directory = dir,
                Audio = Path.Combine(dir, prefix + ".wav"),
                Transcript = Path.Combine(dir, prefix + ".transcript.txt"),
                Summary = Path.Combine(dir, prefix + ".summary.txt"),
                BaseName = baseName,
                SnapshotPrefix = FolderPerCapture ? "snap" : baseName + " - snap",
                StartedAt = now,
            };
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
        public void Purge()
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                DateTime cutoff = DateTime.UtcNow - Retention;
                PurgeOneDirectory(Root, cutoff, false);
                foreach (string sub in Directory.EnumerateDirectories(Root))
                {
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

        private void PurgeOneDirectory(string dir, DateTime cutoff, bool insideCaptureFolder)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!IsEphemeral(file)) continue;
                    if (!NamesThisModuleWrites(Path.GetFileName(file), insideCaptureFolder)) continue;
                    try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); }
                    catch { }
                }
            }
            catch { }
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
        /// "snap &lt;stamp&gt;.png" -- the standalone hotkey snapshot in the ROOT (TakeSnapshot's else
        /// branch), and the snapshot inside a capture folder, which are the same shape because
        /// CapturePaths.SnapshotPrefix is the bare "snap" in folder-per-capture mode.
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
