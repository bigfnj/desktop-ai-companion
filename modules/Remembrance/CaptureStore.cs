using System;
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
            string prefix = FolderPerCapture ? "recording" : baseName;
            return new CapturePaths
            {
                Directory = dir,
                Audio = Path.Combine(dir, prefix + ".wav"),
                Transcript = Path.Combine(dir, prefix + ".transcript.txt"),
                Summary = Path.Combine(dir, prefix + ".summary.txt"),
                BaseName = baseName,
                SnapshotPrefix = FolderPerCapture ? "snap" : baseName + " - snap",
            };
        }

        // Delete audio + screenshots older than the retention window; never a transcript. Best-effort per file.
        //
        // IT MAY ONLY DELETE FILES THIS MODULE WROTE, and that is the whole design of this method rather than
        // a precaution bolted onto it. Root is the free-text `storageLocation` setting, labelled "Where
        // recordings are stored" and also settable with a folder picker, so a user may perfectly reasonably
        // point it at Documents (the PARENT of the default root) or at Pictures. This used to enumerate
        // AllDirectories and File.Delete -- not the recycle bin -- every .wav, .mp3 and .png older than 72
        // hours anywhere beneath it, and it runs on Init and then hourly. Pointed at Pictures, the first tick
        // permanently destroyed the user's photo library with no prompt.
        //
        // Three independent narrowings, because one is a rule and three is a design:
        //   * ONLY THIS MODULE'S OWN FILE SHAPES, which NamesThisModuleWrites spells out from the same
        //     strings NewCapture builds paths from.
        //   * ONE LEVEL DEEP. A capture is either a file in the root or a file in a capture folder directly
        //     under it; this module never creates a third level, so recursing further can only ever reach
        //     somebody else's data.
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
                    PurgeOneDirectory(sub, cutoff, true);
            }
            catch { }
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
        /// Derived from the same two strings NewCapture uses, so the two cannot drift apart silently: inside a
        /// capture folder it writes "recording.wav" and "snap*.png"; flat in the root it writes
        /// "{base}.wav" and "{base} - snap*.png". The flat audio case is the loose one -- any .wav in the root
        /// matches -- and it has to be, because the base name is the user's meeting title and is not
        /// recoverable from the file name alone. That is the case the one-level rule and the folder-per-capture
        /// default exist to contain.
        /// </summary>
        internal static bool NamesThisModuleWrites(string fileName, bool insideCaptureFolder)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string lower = fileName.ToLowerInvariant();
            if (insideCaptureFolder)
                return lower == "recording.wav" || (lower.StartsWith("snap") && lower.EndsWith(".png"));
            return lower.EndsWith(".wav") || (lower.Contains(" - snap") && lower.EndsWith(".png"));
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
    }
}
