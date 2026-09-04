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

        // Delete audio + screenshots older than the retention window; never a transcript. Runs over the whole
        // tree so both storage modes are covered. Best-effort per file.
        public void Purge()
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                DateTime cutoff = DateTime.UtcNow - Retention;
                foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                {
                    if (!IsEphemeral(file)) continue;
                    try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); }
                    catch { }
                }
            }
            catch { }
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
            return lower.EndsWith(".wav") || lower.EndsWith(".mp3") || lower.EndsWith(".png");
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
