using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>One rename: a capture's folder, or one of its files in a day folder. Full paths.</summary>
    internal sealed class RenameStep
    {
        public string From;
        public string To;
        public bool IsFolder;
    }

    /// <summary>Every rename one capture needs, made together or not at all: a per-capture folder is one step, a capture
    /// in a day folder one step per file.</summary>
    internal sealed class RenameGroup
    {
        public readonly List<RenameStep> Steps = new List<RenameStep>();
        /// <summary>The new name took a " (n)" because its minute's name was already in use.</summary>
        public bool Suffixed;
    }

    /// <summary>What a rename pass would do, and what it leaves alone. Counts and paths for the pass itself; nothing here
    /// is logged or shown but the counts, because the names carry the meetings' titles.</summary>
    internal sealed class RenamePlan
    {
        public readonly List<RenameGroup> Groups = new List<RenameGroup>();
        /// <summary>Files with pre-2.6.0 names loose in the storage root (the flat layout before 2.0.0), left as they are.</summary>
        public int LooseInRoot;
        /// <summary>Captures in a day folder whose own date is another day's (moved there by hand), left as they are.</summary>
        public int OtherDay;
        /// <summary>Captures every name from (2) to (99) was in use for, left as they are.</summary>
        public int NoFreeName;
        /// <summary>Folders that could not be listed, so nothing in them was looked at.</summary>
        public int Unread;

        public int Folders { get { return Count(true); } }
        public int Files { get { return Count(false); } }

        private int Count(bool folders)
        {
            int n = 0;
            foreach (RenameGroup group in Groups)
                foreach (RenameStep step in group.Steps)
                    if (step.IsFolder == folders) n++;
            return n;
        }
    }

    /// <summary>What a rename pass did: counts and exception TYPE names only, as <see cref="DeletionReport"/> keeps them
    /// and for its reason (an IOException's message names the path, and the path carries the meeting's title).</summary>
    internal sealed class RenameReport
    {
        public int Captures;
        public int Folders;
        public int Files;
        public int Suffixed;
        /// <summary>Captures one of whose renames failed; what had already moved was put back.</summary>
        public int CapturesFailed;
        /// <summary>Renames a failed capture could not undo, so that capture is split across the two names.</summary>
        public int NotPutBack;

        private readonly List<string> _failureTypes = new List<string>();

        public string FailureTypes { get { return string.Join(", ", _failureTypes); } }

        public void NoteType(Exception ex)
        {
            string type = ex == null ? "Exception" : ex.GetType().Name;
            if (!_failureTypes.Contains(type)) _failureTypes.Add(type);
        }
    }

    internal sealed partial class CaptureStore
    {
        /// <summary>The purge and the rename never run at once (2.6.0): each would count the other's moves as failures.</summary>
        internal static readonly object FileWorkGate = new object();

        /// <summary>
        /// Plan the move of every pre-2.6.0 capture name under the root to the time-first one (2.6.0, the owner's decision
        /// of 2026-10-20 to rename the existing recordings too). Reads names only, and moves nothing.
        ///
        ///   * A per-capture folder in the root named "&lt;meeting&gt; - &lt;stamp&gt;" or "&lt;stamp&gt;" is renamed as a
        ///     whole. The files inside keep their names ("recording.wav", "snap &lt;stamp&gt;.png"), which carry no title.
        ///   * In a day folder, every file named for a pre-2.6.0 capture of THAT day is renamed with its siblings, under the
        ///     new base: "&lt;base&gt;.wav", its two scratch tracks, ".transcript.txt", ".summary.txt" and
        ///     "&lt;base&gt; - snap &lt;stamp&gt;.png".
        ///
        /// Left as they are, and counted: files loose in the root (the pre-2.0.0 flat layout, which the purge reads there
        /// by its old names alone, so a new name there would never be purged), a capture in a day folder for another day
        /// (the purge reads a new name in a day folder only with that day's date), and anything not in a shape this module
        /// writes. Oldest first within a folder, so where two captures of one meeting began inside the same minute the
        /// earlier keeps the plain name and the later takes " (2)", as two new captures would.
        /// </summary>
        public RenamePlan PlanRenames()
        {
            var plan = new RenamePlan();
            try
            {
                if (!Directory.Exists(Root)) return plan;
                foreach (string file in Directory.EnumerateFiles(Root))
                {
                    string oldBase, rest;
                    if (TryOldCaptureFile(Path.GetFileName(file), out oldBase, out rest)) plan.LooseInRoot++;
                }
                var captures = new List<OldCapture>();
                foreach (string sub in Directory.EnumerateDirectories(Root))
                {
                    string name = Path.GetFileName(sub);
                    if (IsDayFolderName(name)) { PlanDayFolder(sub, name, plan); continue; }
                    DateTime stamp;
                    string meeting;
                    if (TryParseOldBase(name, out stamp, out meeting))
                        captures.Add(new OldCapture { Base = name, Stamp = stamp, Meeting = meeting });
                }
                captures.Sort(OldestFirst);
                var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (OldCapture capture in captures)
                {
                    string target = TimeFirstBase(capture.Stamp, capture.Meeting);
                    string free;
                    try { free = FreeBaseName(target, Root, true, claimed); }
                    catch (IOException) { plan.NoFreeName++; continue; }
                    claimed.Add(free);
                    var group = new RenameGroup { Suffixed = !string.Equals(free, target, StringComparison.Ordinal) };
                    group.Steps.Add(new RenameStep { From = Path.Combine(Root, capture.Base), To = Path.Combine(Root, free), IsFolder = true });
                    plan.Groups.Add(group);
                }
            }
            catch (Exception) { plan.Unread++; }
            return plan;
        }

        private void PlanDayFolder(string dir, string dayName, RenamePlan plan)
        {
            DateTime? day = DayOf(dayName);
            var byBase = new Dictionary<string, OldCapture>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    string name = Path.GetFileName(file);
                    string oldBase, rest;
                    if (!TryOldCaptureFile(name, out oldBase, out rest)) continue;
                    OldCapture capture;
                    if (!byBase.TryGetValue(oldBase, out capture))
                    {
                        DateTime stamp;
                        string meeting;
                        TryParseOldBase(oldBase, out stamp, out meeting);
                        capture = new OldCapture { Base = oldBase, Stamp = stamp, Meeting = meeting };
                        byBase.Add(oldBase, capture);
                    }
                    capture.Rests.Add(rest);
                }
            }
            catch (Exception) { plan.Unread++; return; }

            var captures = new List<OldCapture>(byBase.Values);
            captures.Sort(OldestFirst);
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (OldCapture capture in captures)
            {
                if (!day.HasValue || capture.Stamp.Date != day.Value.Date) { plan.OtherDay++; continue; }
                string target = TimeFirstBase(capture.Stamp, capture.Meeting);
                string free;
                try { free = FreeBaseName(target, dir, false, claimed); }
                catch (IOException) { plan.NoFreeName++; continue; }
                claimed.Add(free);
                var group = new RenameGroup { Suffixed = !string.Equals(free, target, StringComparison.Ordinal) };
                foreach (string rest in capture.Rests)
                    group.Steps.Add(new RenameStep { From = Path.Combine(dir, capture.Base + rest), To = Path.Combine(dir, free + rest) });
                plan.Groups.Add(group);
            }
        }

        /// <summary>
        /// Make the renames, one capture at a time and never beside the purge. A capture one of whose renames fails has
        /// what it had already moved put back, newest first, so it is not left split across two names; the rest go on.
        /// Never overwrites: File.Move and Directory.Move both refuse a target that exists, and that refusal is a failure
        /// like any other.
        /// </summary>
        public static RenameReport ApplyRenames(RenamePlan plan)
        {
            var report = new RenameReport();
            if (plan == null) return report;
            lock (FileWorkGate)
            {
                foreach (RenameGroup group in plan.Groups)
                {
                    var done = new List<RenameStep>();
                    try
                    {
                        foreach (RenameStep step in group.Steps)
                        {
                            MoveWithRetry(step.From, step.To, step.IsFolder);
                            done.Add(step);
                        }
                        report.Captures++;
                        foreach (RenameStep step in done)
                            if (step.IsFolder) report.Folders++; else report.Files++;
                        if (group.Suffixed) report.Suffixed++;
                    }
                    catch (Exception ex)
                    {
                        report.CapturesFailed++;
                        report.NoteType(ex);
                        for (int i = done.Count - 1; i >= 0; i--)
                        {
                            try { MoveWithRetry(done[i].To, done[i].From, done[i].IsFolder); }
                            catch (Exception back) { report.NotPutBack++; report.NoteType(back); }
                        }
                    }
                }
            }
            return report;
        }

        /// <summary>
        /// How many times a move is tried while its source is still there and its target still free, and the pause
        /// between tries. A folder made a moment before failed to move once in a self-test run on 2026-10-20 and moved on
        /// the next run, which is the shape a scanner or the search indexer briefly holding a file inside it gives; on this
        /// kind of box the endpoint filters do exactly that. A move whose target exists is never tried again: that refusal
        /// is the never-overwrite, not a hiccup.
        /// </summary>
        internal const int MoveAttempts = 5;
        internal static int MoveRetryDelayMs = 200;

        private static void MoveWithRetry(string from, string to, bool isFolder)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    MoveEntry(from, to, isFolder);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < MoveAttempts
                                           && EntryExists(from) && !EntryExists(to))
                {
                    System.Threading.Thread.Sleep(MoveRetryDelayMs);
                }
            }
        }

        private static bool EntryExists(string path)
        {
            return File.Exists(path) || Directory.Exists(path);
        }

        /// <summary>The one move both directions use. A seam: the self-test swaps it to fail one file of a capture.</summary>
        internal static Action<string, string, bool> MoveEntry = (from, to, isFolder) =>
        {
            if (isFolder) Directory.Move(from, to);
            else File.Move(from, to);
        };

        private sealed class OldCapture
        {
            public string Base;
            public DateTime Stamp;
            public string Meeting;
            public readonly List<string> Rests = new List<string>();
        }

        private static int OldestFirst(OldCapture a, OldCapture b)
        {
            int byStamp = a.Stamp.CompareTo(b.Stamp);
            return byStamp != 0 ? byStamp : string.CompareOrdinal(a.Base, b.Base);
        }

        /// <summary>
        /// A pre-2.6.0 capture's base name, case kept: "&lt;meeting&gt; - &lt;yyyy-MM-dd HH-mm-ss&gt;" or the stamp alone,
        /// with the stamp parsed and the meeting handed back. The same shape <see cref="IsCaptureBaseName"/> accepts.
        /// </summary>
        internal static bool TryParseOldBase(string name, out DateTime stamp, out string meeting)
        {
            const string Separator = " - ";
            stamp = default(DateTime);
            meeting = null;
            if (string.IsNullOrEmpty(name) || name.Length < StampLength) return false;
            if (!DateTime.TryParseExact(name.Substring(name.Length - StampLength), StampFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp)) return false;
            string head = name.Substring(0, name.Length - StampLength);
            if (head.Length == 0) { meeting = ""; return true; }
            if (head.Length <= Separator.Length || !head.EndsWith(Separator, StringComparison.Ordinal)) return false;
            meeting = head.Substring(0, head.Length - Separator.Length);
            return true;
        }

        // The endings a pre-2.6.0 capture's files carry after its base name in a day folder; longest first, so a scratch
        // track is not read as "<base>.system" + ".wav".
        private static readonly string[] OldEndings =
            { ".transcript.txt", ".summary.txt", AudioRecorder.SystemScratchSuffix, AudioRecorder.MicScratchSuffix, ".wav" };

        /// <summary>
        /// Is this file one a pre-2.6.0 capture wrote under its base name? Hands back that base, case kept, and the rest of
        /// the name. Its snapshots ("&lt;base&gt; - snap &lt;stamp&gt;.png") count, judged by the purge's own shape.
        /// </summary>
        internal static bool TryOldCaptureFile(string fileName, out string oldBase, out string rest)
        {
            oldBase = null;
            rest = null;
            if (string.IsNullOrEmpty(fileName)) return false;
            string lower = fileName.ToLowerInvariant();
            DateTime stamp;
            string meeting;
            foreach (string ending in OldEndings)
            {
                if (!lower.EndsWith(ending, StringComparison.Ordinal)) continue;
                string stem = fileName.Substring(0, fileName.Length - ending.Length);
                if (!TryParseOldBase(stem, out stamp, out meeting)) return false;
                oldBase = stem;
                rest = fileName.Substring(stem.Length);
                return true;
            }
            const string Middle = " - snap ";
            if (!IsFlatSnapshotName(lower)) return false;
            int at = lower.LastIndexOf(Middle, StringComparison.Ordinal);
            string head = fileName.Substring(0, at);
            if (!TryParseOldBase(head, out stamp, out meeting)) return false;
            oldBase = head;
            rest = fileName.Substring(at);
            return true;
        }
    }
}
