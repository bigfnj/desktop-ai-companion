using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// Where recordings go, and where the 72-hour purge reaches (lane feature/remembrance-backlog, 2026-10-26): the storage
    /// root a FRESH install starts with when Documents is inside OneDrive (N-meeting-end-02), and the earlier storage roots
    /// the purge keeps covering once the storage folder has moved (N-remembrance-delete-logging-06). Every reader of the
    /// storage root goes through <see cref="StorageRoot"/>: a capture's start, a snapshot, the purge, the rename and the
    /// Status line.
    /// </summary>
    public sealed partial class RemembranceModule
    {
        // ---- the storage root, and a fresh install's (N-meeting-end-02) ----

        /// <summary>The folder a fresh install whose Documents is inside OneDrive files recordings in: "Remembrance" in the
        /// app's data folder, beside its "modules" folder (<see cref="FreshInstallRoot"/> says why not inside this module's
        /// own folder).</summary>
        internal const string LocalStorageFolderName = "Remembrance";

        /// <summary>What the storage folder's box says while blank on such an install, in place of
        /// <see cref="StorageEmptyHint"/>, which names Documents.</summary>
        internal const string LocalStorageEmptyHint = "Remembrance in this app's data folder, on this computer (the default here: Documents is in OneDrive)";

        /// <summary>Documents\Remembrance, the default since 1.0.0, behind a delegate so the self-test stands in a fake
        /// Documents path. Defaults to CaptureStore.DocumentsRemembrance, the same folder CaptureStore.DefaultRoot gives a
        /// blank root, and NOT to DefaultRoot itself (N-remembrance-test-documents-01): SelfTest swaps this and
        /// CaptureStore.DefaultRootSource for its whole run, and a delegate that read through the other seam would make
        /// this one's swap a no-op no check could tell from a missing one.</summary>
        internal static Func<string> DocumentsRoot = CaptureStore.DocumentsRemembrance;

        /// <summary>Directory.Exists behind a delegate, for the same reason.</summary>
        internal static Func<string, bool> FolderExists = Directory.Exists;

        /// <summary>
        /// The folder recordings go to now: the storage location saved; Documents\Remembrance while it is saved BLANK, which
        /// is what an Apply before 2.7.0 wrote for most installs and what "blank" has always meant; and for an install that
        /// has never saved one, <see cref="FreshInstallRoot"/>'s folder, else Documents\Remembrance. NULL when the location
        /// is blank or unsaved and Windows reports no Documents folder (N-modules-fixes-02): there is no folder then, and
        /// every reader says so or skips rather than resolving a relative path (CaptureStore.NoDefaultRootReason).
        /// </summary>
        private string StorageRoot()
        {
            string saved = _settings.Get("storageLocation", null);
            if (saved == null) return FreshInstallRoot(DocumentsRoot(), DataDirectory(), EnvironmentLookup, FolderExists) ?? DocumentsRoot();
            return string.IsNullOrWhiteSpace(saved) ? DocumentsRoot() : saved.Trim();
        }

        /// <summary>
        /// The storage root an install that has NEVER saved a storage location starts with, when that is not
        /// Documents\Remembrance; null keeps Documents\Remembrance. The owner's decision of 2026-10-26 on N-meeting-end-02:
        /// a fresh install whose Documents is inside OneDrive defaults to a folder on this computer, because a recording
        /// there uploads and a deleted one waits in OneDrive's recycle bin (2.4.0 only warned), and existing users keep what
        /// they have, so nothing moves. Pure apart from its two lookups, for the self-test's fake Documents path.
        ///
        /// Null, and so Documents\Remembrance as before, when Documents is not inside OneDrive; when Documents\Remembrance
        /// already exists (an existing user's recordings are there, and an install that never applied the pane has no key
        /// saved either); when this module's folder is not where the host keeps module folders, so the data root cannot be
        /// told; and when the data root is itself inside OneDrive (a portable copy kept in Documents gains nothing).
        ///
        /// THE APP'S DATA ROOT, NEVER THIS MODULE'S OWN FOLDER. The module's folder (&lt;data root&gt;\modules\remembrance)
        /// is deleted whole, recursively, when the module is uninstalled from the Modules pane
        /// (PendingModuleRemovals.ProcessPending), and transcripts and summaries are kept for ever by design: recordings
        /// there would be lost with the module. &lt;data root&gt;\Remembrance is beside the modules folder, which no uninstall
        /// removes, and it is %LOCALAPPDATA%\DesktopAICompanion\Remembrance on an installed copy, never redirected into
        /// OneDrive.
        /// </summary>
        internal static string FreshInstallRoot(string documentsRoot, string moduleDataDirectory, Func<string, string> environment,
            Func<string, bool> folderExists)
        {
            if (CaptureStore.OneDriveRootOf(documentsRoot, environment) == null) return null;
            if (folderExists == null || folderExists(documentsRoot)) return null;
            string dataRoot = AppDataRootOf(moduleDataDirectory);
            if (dataRoot == null) return null;
            string local = Path.Combine(dataRoot, LocalStorageFolderName);
            return CaptureStore.OneDriveRootOf(local, environment) == null ? local : null;
        }

        /// <summary>The app's data root that holds this module's folder, "&lt;data root&gt;\modules\remembrance", or null for a
        /// folder laid out any other way (a host or a test that hands out some other folder).</summary>
        internal static string AppDataRootOf(string moduleDataDirectory)
        {
            if (string.IsNullOrWhiteSpace(moduleDataDirectory)) return null;
            try
            {
                string own = Path.GetFullPath(moduleDataDirectory.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string modules = Path.GetDirectoryName(own);
                if (string.IsNullOrEmpty(modules) || !string.Equals(Path.GetFileName(modules), "modules", StringComparison.OrdinalIgnoreCase))
                    return null;
                string dataRoot = Path.GetDirectoryName(modules);
                return string.IsNullOrEmpty(dataRoot) ? null : dataRoot;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// The root a capture is written under (a recording's start, a snapshot). On a fresh install whose root is
        /// <see cref="FreshInstallRoot"/>'s folder, that folder is SAVED as the storage location here, the first time
        /// anything is written into it, so the choice stays put: decided afresh each time, a Documents\Remembrance that
        /// appeared later (OneDrive bringing one down from another computer) would move new recordings into OneDrive and
        /// leave the local ones out of the purge. Recorded as a write outside the pane (SetInBackground), so an Apply of a
        /// pane that still shows the box blank keeps it. Nothing is saved while the root is Documents\Remembrance, which is
        /// what an unsaved location has always meant.
        /// </summary>
        private string StorageRootForCapture()
        {
            string root = StorageRoot();
            // No folder at all (no Documents): nothing to save, and the CaptureStore built from it refuses the capture.
            if (root == null) return null;
            if (_settings.Get("storageLocation", null) != null || SameFolder(root, DocumentsRoot())) return root;
            SetInBackground("storageLocation", root);
            string notPersisted;
            TrySaveSettings("the storage folder", out notPersisted);
            Log("storage: recordings go to this app's data folder on this computer, because Documents is inside OneDrive");
            return root;
        }

        /// <summary>The note the Status line gives the storage folder when it is the local one a fresh install chose, and
        /// why it is none when it is not a full path (N-followups-02: one saved before the pane refused such a path, or
        /// written into settings.json by hand; a recording's start refuses it with the same words).</summary>
        private string StorageNote(string root)
        {
            if (!CaptureStore.IsFullFolder(root)) return " ✗ " + CaptureStore.RelativeRootReason;
            string dataRoot = AppDataRootOf(DataDirectory());
            if (dataRoot == null || !SameFolder(root, Path.Combine(dataRoot, LocalStorageFolderName))) return "";
            return CaptureStore.OneDriveRootOf(root, EnvironmentLookup) == null ? " (on this computer, outside OneDrive)" : "";
        }

        // The storage folder's row, held so Load can set its hint (RefreshStorageHint).
        private SettingField _storageField;

        /// <summary>What the storage folder's box says while blank when Windows reports no Documents folder, so blank means
        /// nothing (N-modules-fixes-02); the Status line says why (CaptureStore.NoDefaultRootReason).</summary>
        internal const string NoDocumentsEmptyHint = "No default here: Windows reports no Documents folder. Choose a folder";

        /// <summary>The storage folder box's hint while blank: the local folder's on an install whose unsaved default it is,
        /// <see cref="NoDocumentsEmptyHint"/> when blank stands for no folder, Documents\Remembrance's otherwise. Set by
        /// Load, before the host reads the schema.</summary>
        private void RefreshStorageHint()
        {
            if (_storageField == null) return;
            string documents = DocumentsRoot();
            if (documents == null) { _storageField.EmptyHint = NoDocumentsEmptyHint; return; }
            bool local = _settings.Get("storageLocation", null) == null && !SameFolder(StorageRoot(), documents);
            _storageField.EmptyHint = local ? LocalStorageEmptyHint : StorageEmptyHint;
        }

        /// <summary>Two folders are one when their full paths match, case-insensitively and with no trailing separator.</summary>
        internal static bool SameFolder(string a, string b)
        {
            string x = FullFolder(a), y = FullFolder(b);
            return x != null && y != null && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        }

        private static string FullFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch (Exception) { return path.Trim(); }
        }

        // ---- the earlier storage roots (N-remembrance-delete-logging-06) ----

        /// <summary>The folders the storage location pointed to before, newest first, '|'-joined (no Windows path holds a
        /// '|'), in this module's settings.</summary>
        internal const string EarlierRootsKey = "earlierStorageLocations";

        /// <summary>How many earlier roots are remembered. A bound, because each is walked at every hourly purge; the oldest
        /// goes past it, and the log says so then.</summary>
        internal const int EarlierRootsLimit = 8;

        // Earlier roots already said to be missing or unreadable this session, so the hourly purge says it once. Cleared for
        // a root the next time it can be read, so a later failure is said again.
        private readonly HashSet<string> _earlierRootsNoted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The '|'-joined list as folders, blanks and repeats dropped, order kept.</summary>
        internal static List<string> ParseRoots(string joined)
        {
            var roots = new List<string>();
            foreach (string raw in (joined ?? "").Split('|'))
            {
                string root = raw.Trim();
                if (root.Length > 0 && !roots.Any(r => SameFolder(r, root))) roots.Add(root);
            }
            return roots;
        }

        /// <summary>
        /// The list after the storage location moved from <paramref name="left"/> to <paramref name="now"/>: the one left goes
        /// first, the one now in use leaves the list (a move back makes it the current root again, purged as that), and past
        /// <see cref="EarlierRootsLimit"/> the oldest goes, which <paramref name="dropped"/> reports. Pure, for the self-test.
        /// </summary>
        internal static string RememberEarlierRoot(string joined, string left, string now, out bool dropped)
        {
            List<string> roots = ParseRoots(joined);
            roots.RemoveAll(r => SameFolder(r, left) || SameFolder(r, now));
            if (!string.IsNullOrWhiteSpace(left)) roots.Insert(0, left.Trim());
            dropped = roots.Count > EarlierRootsLimit;
            if (dropped) roots.RemoveRange(EarlierRootsLimit, roots.Count - EarlierRootsLimit);
            return string.Join("|", roots);
        }

        /// <summary>
        /// Called by Save after the pane's values are applied: when the storage root moved, remember the one it left, so the
        /// purge keeps covering what was recorded there. Only a folder that EXISTS is remembered: one that does not holds no
        /// recording, and a fresh install's never-created Documents\Remembrance would otherwise be reported missing at every
        /// start. A move back to a remembered root takes it off the list. One log line, with no folder named (the delete
        /// logging rule's: a folder name can carry a meeting's title, and the path its owner's).
        /// </summary>
        private void NoteStorageMove(string rootBefore)
        {
            string rootAfter = StorageRoot();
            if (SameFolder(rootBefore, rootAfter)) return;
            // A location left that was not a full path named no folder (N-followups-02): FolderExists would resolve it
            // against the current directory, and a folder there would be remembered and walked at every purge.
            if (!CaptureStore.IsFullFolder(rootBefore)) rootBefore = null;
            string joined = _settings.Get(EarlierRootsKey, "");
            bool dropped = false;
            string next;
            if (FolderExists(rootBefore)) next = RememberEarlierRoot(joined, rootBefore, rootAfter, out dropped);
            else next = string.Join("|", ParseRoots(joined).Where(r => !SameFolder(r, rootAfter)));
            if (!string.Equals(next, joined, StringComparison.Ordinal)) _settings.Set(EarlierRootsKey, next);
            int remembered = ParseRoots(next).Count;
            if (remembered == 0) return;
            Log("storage folder changed: the " + RetentionHours() + "-hour purge also covers " +
                Counted(remembered) + " earlier storage folder(s)" +
                (dropped ? "; the oldest of them is no longer remembered, and no longer purged" : ""));
        }

        /// <summary>The remembered earlier roots other than <paramref name="current"/>, read once per purge pass.</summary>
        private List<string> EarlierRoots(string current)
        {
            return ParseRoots(_settings.Get(EarlierRootsKey, "")).Where(r => !SameFolder(r, current)).ToList();
        }

        /// <summary>Whether an earlier root can be walked: null when it can, "missing", or the type of the exception its
        /// listing threw. A seam, so the self-test can make a folder unreadable without changing its permissions.</summary>
        internal static Func<string, string> EarlierRootTrouble = delegate(string root)
        {
            try
            {
                if (!FolderExists(root)) return MissingRoot;
                using (IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(root).GetEnumerator()) entries.MoveNext();
                return null;
            }
            catch (Exception ex) { return ex.GetType().Name; }
        };

        internal const string MissingRoot = "missing";

        /// <summary>
        /// The purge of the earlier roots, on the purge's pool thread, BY THE SAME STRICT SHAPES as the current root's: each
        /// through StorePurge, CaptureStore.Purge, whose shapes, one-level walk and named-folder rule are the ones the current
        /// root has. A root missing or unreadable is said ONCE while it stays so and is kept for the next pass: a drive that
        /// is not plugged in comes back. ONE line for all of them, or none when nothing happened.
        /// </summary>
        private void PurgeEarlierRoots(List<string> earlier, string layout)
        {
            if (earlier == null || earlier.Count == 0) return;
            var report = new DeletionReport();
            int missing = 0, unreadable = 0;
            var unreadableTypes = new List<string>();
            foreach (string root in earlier)
            {
                string trouble = EarlierRootTrouble(root);
                if (trouble != null)
                {
                    bool first;
                    lock (_earlierRootsNoted) first = _earlierRootsNoted.Add(FullFolder(root));
                    if (!first) continue;
                    if (trouble == MissingRoot) missing++;
                    else
                    {
                        unreadable++;
                        if (!unreadableTypes.Contains(trouble)) unreadableTypes.Add(trouble);
                    }
                    continue;
                }
                lock (_earlierRootsNoted) _earlierRootsNoted.Remove(FullFolder(root));
                try { report.Add(StorePurge(root, layout)); }
                catch (Exception ex) { report.FolderUnread(ex); }
            }
            string line = EarlierPurgeLine(report, missing, unreadable, string.Join(", ", unreadableTypes));
            if (line != null) Log(line);
        }

        /// <summary>
        /// The earlier roots' one line: "purge, earlier storage folders: removed 2 recording audio file(s) older than 72 h",
        /// in <see cref="PurgeLine"/>'s words, then the roots that could not be walked: "1 is missing; it stays on the list
        /// and is tried again at every purge". Null when nothing was removed, nothing failed and no root is newly missing.
        /// Counts and exception type names only, never a folder.
        /// </summary>
        internal static string EarlierPurgeLine(DeletionReport report, int missing, int unreadable, string unreadableTypes)
        {
            var parts = new List<string>();
            string done = PurgeLine(report);
            const string Prefix = "purge: ";
            if (done != null) parts.Add(done.StartsWith(Prefix, StringComparison.Ordinal) ? done.Substring(Prefix.Length) : done);
            var away = new List<string>();
            if (missing > 0) away.Add(Counted(missing) + (missing == 1 ? " is missing" : " are missing"));
            if (unreadable > 0) away.Add(Counted(unreadable) + " could not be read (" + unreadableTypes + ")");
            if (away.Count > 0)
                parts.Add(string.Join(" and ", away) + "; " + (missing + unreadable == 1 ? "it stays" : "each stays") +
                          " on the list and is tried again at every purge");
            return parts.Count == 0 ? null : "purge, earlier storage folders: " + string.Join("; ", parts);
        }

        // ---- self-checks (lane feature/remembrance-backlog) ----

        /// <summary>A module data folder at a path the check chooses: TempModuleStorage's random folder under TEMP is not laid
        /// out as the host lays out module folders, which is the point of one of the checks below.</summary>
        private sealed class FixedStorage : IModuleStorage
        {
            internal FixedStorage(string directory) { DataDirectory = directory; }
            public string DataDirectory { get; private set; }
        }

        /// <summary>
        /// N-remembrance-delete-logging-06: the purge keeps covering a storage folder the user moved away from, by the same
        /// strict shapes as the current one, and a remembered folder that is gone or unreadable is said once and kept. Two
        /// roots in a scratch folder, through the pane's own Apply and the module's own RunPurge.
        /// </summary>
        private static void SelfCheckEarlierRoots(Action<string, bool> check)
        {
            bool dropped;
            string one = RememberEarlierRoot("", @"C:\a", @"C:\b", out dropped);
            string two = RememberEarlierRoot(one, @"C:\b", @"C:\c", out dropped);
            string back = RememberEarlierRoot(two, @"C:\c", @"C:\a", out dropped);
            check("earlier roots: a move remembers the folder left, newest first, and a move back takes the folder now in use off the list: " +
                  one + " / " + two + " / " + back,
                one == @"C:\a" && two == @"C:\b|C:\a" && back == @"C:\c|C:\b" && !dropped &&
                RememberEarlierRoot(@"C:\A\", @"c:\a", @"C:\b", out dropped) == @"c:\a");
            var many = new List<string>();
            for (int i = 0; i < EarlierRootsLimit; i++) many.Add(@"C:\r" + i.ToString(CultureInfo.InvariantCulture));
            string full = RememberEarlierRoot(string.Join("|", many), @"C:\newest", @"C:\current", out dropped);
            check("earlier roots: at most " + EarlierRootsLimit.ToString(CultureInfo.InvariantCulture) +
                  " are remembered, the oldest going past the bound and said to: " + ParseRoots(full).Count.ToString(CultureInfo.InvariantCulture),
                dropped && ParseRoots(full).Count == EarlierRootsLimit && ParseRoots(full)[0] == @"C:\newest" &&
                !ParseRoots(full).Contains(@"C:\r" + (EarlierRootsLimit - 1).ToString(CultureInfo.InvariantCulture)));

            const string Title = "Board Review Kestrel";
            string scratch = Path.Combine(Path.GetTempPath(), "dp-remembrance-earlier-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Func<string, string> savedTrouble = EarlierRootTrouble;
            try
            {
                string first = Path.Combine(scratch, "first");
                string second = Path.Combine(scratch, "second");
                Directory.CreateDirectory(second);
                DateTime old = DateTime.UtcNow.AddDays(-4);
                string capture = Path.Combine(first, "2026-08-20 09-30 " + Title);
                string due = Path.Combine(capture, CaptureStore.FolderPrefix + ".wav");
                string snap = Path.Combine(capture, "snap 2026-08-20 09-35-00.png");
                string transcript = Path.Combine(capture, CaptureStore.FolderPrefix + ".transcript.txt");
                // Outside the strict shapes, under the old root: a user's own audio, and a name this module writes inside a
                // folder it never named.
                string theirs = Path.Combine(first, "my own interview.wav");
                string theirsDeep = Path.Combine(first, "Zoom", CaptureStore.FolderPrefix + ".wav");
                PlantAged(new[] { due, snap, transcript, theirs, theirsDeep }, old);

                var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings s = host.SettingsFor(Id);
                s.Set("storageLocation", first);
                s.Set("summaryModelsCache", "alpha:1b");
                s.Set("whisperExe", @"c:\seeded\whisper-cli.exe");
                var module = new RemembranceModule();
                module.Init(host);
                var lines = new List<string>();
                try
                {
                    OptionsPane pane = host.OptionsPanes[0];
                    Func<List<string>> ours = delegate
                    {
                        return host.LoggedLines.Where(l => l.StartsWith(Id + ": purge", StringComparison.Ordinal) ||
                                                           l.StartsWith(Id + ": storage", StringComparison.Ordinal)).ToList();
                    };
                    Func<List<string>> pass = delegate
                    {
                        int before = ours().Count;
                        bool ran = module.PurgeForSelfTest().Wait(TimeSpan.FromSeconds(10));
                        List<string> added = ours().Skip(before).ToList();
                        lines.AddRange(added);
                        return ran ? added : null;
                    };

                    // ---- the move ----
                    Dictionary<string, string> moved = CopyOf(pane.Load());
                    moved["storageLocation"] = second;
                    int beforeMove = ours().Count;
                    bool applied = pane.Save(moved);
                    List<string> moveLines = ours().Skip(beforeMove).ToList();
                    lines.AddRange(moveLines);
                    List<string> remembered = ParseRoots(s.Get(EarlierRootsKey, ""));
                    check("earlier roots: an Apply that moves the storage folder remembers the one it left, saying so without naming it: " +
                          string.Join(" | ", moveLines),
                        applied && remembered.Count == 1 && SameFolder(remembered[0], first) && moveLines.Count == 1 &&
                        moveLines[0] == Id + ": storage folder changed: the 72-hour purge also covers 1 earlier storage folder(s)");

                    // ---- the purge reaches it ----
                    List<string> purged = pass();
                    check("earlier roots: the purge removes a recording older than 72 h under the folder the storage moved away from, in one line: " +
                          (purged == null ? "(no pass)" : string.Join(" | ", purged)),
                        purged != null && purged.Count == 1 && purged[0] == Id +
                        ": purge, earlier storage folders: removed 1 recording audio file(s) and 1 snapshot(s) older than 72 h");
                    check("WITNESS earlier roots: under the old folder a file outside the strict shapes, a shape name in a folder this module " +
                          "never named and the transcript are never touched, while the recording and the snapshot went",
                        File.Exists(theirs) && File.Exists(theirsDeep) && File.Exists(transcript) && !File.Exists(due) && !File.Exists(snap));

                    // ---- gone: said once, and kept ----
                    Directory.Delete(first, true);
                    List<string> gone = pass();
                    check("earlier roots: a remembered folder that is gone is said once, and stays on the list: " +
                          (gone == null ? "(no pass)" : string.Join(" | ", gone)),
                        gone != null && gone.Count == 1 &&
                        gone[0] == Id + ": purge, earlier storage folders: 1 is missing; it stays on the list and is tried again at every purge" &&
                        ParseRoots(s.Get(EarlierRootsKey, "")).Count == 1);
                    List<string> again = pass();
                    check("earlier roots: ...and is not said again at the next purge while it stays gone",
                        again != null && again.Count == 0);
                    // A drive plugged in again: the folder is back, and so is the purge.
                    PlantAged(new[] { due }, old);
                    List<string> backAgain = pass();
                    check("WITNESS earlier roots: a remembered folder that comes back is purged again: " +
                          (backAgain == null ? "(no pass)" : string.Join(" | ", backAgain)),
                        !File.Exists(due) && backAgain != null && backAgain.Count == 1 &&
                        backAgain[0] == Id + ": purge, earlier storage folders: removed 1 recording audio file(s) older than 72 h, and 1 empty folder(s)");

                    // ---- unreadable: said once, by the exception's type ----
                    EarlierRootTrouble = delegate { return "UnauthorizedAccessException"; };
                    List<string> unread = pass();
                    List<string> unreadAgain = pass();
                    EarlierRootTrouble = savedTrouble;
                    check("earlier roots: a remembered folder that cannot be read is said once, by the exception's type: " +
                          (unread == null ? "(no pass)" : string.Join(" | ", unread)),
                        unread != null && unread.Count == 1 && unreadAgain != null && unreadAgain.Count == 0 && unread[0] == Id +
                        ": purge, earlier storage folders: 1 could not be read (UnauthorizedAccessException); it stays on the list and is tried again at every purge");

                    // ---- a folder that does not exist is not remembered, and moving back takes one off the list ----
                    Dictionary<string, string> toNowhere = CopyOf(pane.Load());
                    toNowhere["storageLocation"] = Path.Combine(scratch, "never-made");
                    pane.Save(toNowhere);
                    Dictionary<string, string> toFirst = CopyOf(pane.Load());
                    toFirst["storageLocation"] = first;
                    pane.Save(toFirst);
                    List<string> now = ParseRoots(s.Get(EarlierRootsKey, ""));
                    check("earlier roots: a folder left that does not exist is not remembered, and the folder moved back to leaves the list: " +
                          now.Count.ToString(CultureInfo.InvariantCulture) + " remembered",
                        now.Count == 1 && SameFolder(now[0], second));

                    // ---- no line names a folder ----
                    string[] planted = { Title, scratch, "first", "second", "never-made" };
                    string leaked = planted.FirstOrDefault(p => lines.Any(l => l.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0));
                    check("earlier roots: no purge or storage line names a folder, checked against the planted names (" +
                          lines.Count.ToString(CultureInfo.InvariantCulture) + " lines); leaked: " + (leaked ?? "none"),
                        lines.Count == 5 && leaked == null);
                }
                finally { module.Shutdown(); }
            }
            catch (Exception ex) { check("earlier roots: the checks threw " + ex.GetType().Name + ": " + ex.Message, false); }
            finally
            {
                EarlierRootTrouble = savedTrouble;
                ClearReadOnly(scratch);
                try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch (Exception) { }
            }
        }

        /// <summary>
        /// N-meeting-end-02, the owner's decision of 2026-10-26: a fresh install whose Documents is inside OneDrive records to
        /// &lt;data root&gt;\Remembrance, never inside this module's own folder, and an existing install keeps what it has. Both
        /// branches on a fake Documents path and a stood-in OneDrive variable.
        /// </summary>
        private static void SelfCheckFreshInstallRoot(Action<string, bool> check)
        {
            const string OneDrive = @"C:\Users\someone\OneDrive - Contoso";
            const string Documents = OneDrive + @"\Documents\Remembrance";
            Func<string, string> env = delegate(string k) { return k == "OneDriveCommercial" ? OneDrive : null; };
            Func<string, bool> none = delegate { return false; };
            check("fresh install: Documents inside OneDrive and nothing in Documents\\Remembrance records to <data root>\\Remembrance",
                FreshInstallRoot(Documents, @"D:\data\modules\remembrance", env, none) == @"D:\data\Remembrance");
            check("fresh install: ...never inside this module's own folder, which an uninstall deletes whole",
                !(FreshInstallRoot(Documents, @"D:\data\modules\remembrance", env, none) ?? "").StartsWith(@"D:\data\modules", StringComparison.OrdinalIgnoreCase));
            check("fresh install: a local Documents keeps Documents\\Remembrance",
                FreshInstallRoot(@"C:\Users\someone\Documents\Remembrance", @"D:\data\modules\remembrance", env, none) == null);
            check("fresh install: an install that never saved a location but already has Documents\\Remembrance keeps it",
                FreshInstallRoot(Documents, @"D:\data\modules\remembrance", env, delegate(string p) { return p == Documents; }) == null);
            check("fresh install: a data folder not laid out as the host's, or a data root inside OneDrive, keeps Documents\\Remembrance",
                FreshInstallRoot(Documents, @"D:\temp\dp-modulekit-remembrance-1234", env, none) == null &&
                FreshInstallRoot(Documents, OneDrive + @"\Portable\data\modules\remembrance", env, none) == null);

            string scratch = Path.Combine(Path.GetTempPath(), "dp-remembrance-fresh-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Func<string> savedDocuments = DocumentsRoot;
            Func<string, string> savedEnv = EnvironmentLookup;
            try
            {
                string oneDrive = Path.Combine(scratch, "OneDrive - Contoso");
                string documents = Path.Combine(oneDrive, "Documents", "Remembrance");
                string moduleData = Path.Combine(scratch, "data", "modules", "remembrance");
                string local = Path.Combine(scratch, "data", LocalStorageFolderName);
                Directory.CreateDirectory(moduleData);
                DocumentsRoot = delegate { return documents; };
                EnvironmentLookup = delegate(string k) { return k == "OneDriveCommercial" ? oneDrive : null; };

                using (new RecorderSelfCheck.FakeDevices())
                {
                    // ---- a fresh install: no storage location saved ----
                    var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    host.UseStorage(Id, new FixedStorage(moduleData));
                    DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings s = host.SettingsFor(Id);
                    s.Set("summaryModelsCache", "alpha:1b");
                    s.Set("whisperExe", @"c:\seeded\whisper-cli.exe");
                    var module = new RemembranceModule();
                    module.Init(host);
                    try
                    {
                        OptionsPane pane = host.OptionsPanes[0];
                        IReadOnlyDictionary<string, string> shown = pane.Load();
                        string status = module.StatusLine();
                        check("fresh install: the Status line says recordings go to this app's data folder, on this computer: " + status,
                            status.Contains("  |  storage: " + local + " (on this computer, outside OneDrive)") &&
                            !status.Contains("The storage folder is inside OneDrive"));
                        SettingField box = FieldFor(pane, "storageLocation");
                        check("fresh install: the storage box stays blank and its hint names the local default",
                            shown["storageLocation"] == "" && box != null && box.EmptyHint == LocalStorageEmptyHint);
                        pane.Save(CopyOf(shown));
                        check("fresh install: an untouched Apply leaves the location unsaved", s.Get("storageLocation", null) == null);
                        module.StartRecordingForSelfTest();
                        CapturePaths paths = module.LastCaptureForSelfTest;
                        Thread.Sleep(80);
                        host.RaiseHostShutdown();
                        check("fresh install: the first recording goes under <data root>\\Remembrance and saves it as the storage location, said once: " +
                              (paths == null ? "(no capture)" : paths.Directory),
                            paths != null && paths.Directory.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                            SameFolder(s.Get("storageLocation", null), local) &&
                            host.LoggedLines.Count(l => l == Id + ": storage: recordings go to this app's data folder on this computer, because Documents is inside OneDrive") == 1);
                        check("WITNESS fresh install: nothing was written under the fake Documents in OneDrive", !Directory.Exists(documents));
                    }
                    finally { module.Shutdown(); }

                    // ---- an existing install: the location saved BLANK, which "Documents\Remembrance (the default)" meant ----
                    var oldHost = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    oldHost.UseStorage(Id, new FixedStorage(moduleData));
                    DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings old = oldHost.SettingsFor(Id);
                    old.Set("storageLocation", "");
                    old.Set("summaryModelsCache", "alpha:1b");
                    old.Set("whisperExe", @"c:\seeded\whisper-cli.exe");
                    var oldModule = new RemembranceModule();
                    oldModule.Init(oldHost);
                    try
                    {
                        string oldStatus = oldModule.StatusLine();
                        check("existing install: a location saved blank keeps Documents\\Remembrance, and the OneDrive warning stays: " + oldStatus,
                            oldStatus.Contains("  |  storage: " + documents + "  |") && oldStatus.Contains("The storage folder is inside OneDrive"));
                    }
                    finally { oldModule.Shutdown(); }

                    // ---- an existing install that never applied the pane, with recordings in Documents\Remembrance ----
                    Directory.CreateDirectory(documents);
                    var keptHost = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    keptHost.UseStorage(Id, new FixedStorage(moduleData));
                    keptHost.SettingsFor(Id).Set("summaryModelsCache", "alpha:1b");
                    keptHost.SettingsFor(Id).Set("whisperExe", @"c:\seeded\whisper-cli.exe");
                    var keptModule = new RemembranceModule();
                    keptModule.Init(keptHost);
                    try
                    {
                        string keptStatus = keptModule.StatusLine();
                        check("existing install: no location saved but Documents\\Remembrance already there keeps it, nothing moved: " + keptStatus,
                            keptStatus.Contains("  |  storage: " + documents + "  |"));
                    }
                    finally { keptModule.Shutdown(); }
                }
            }
            catch (Exception ex) { check("fresh install: the checks threw " + ex.GetType().Name + ": " + ex.Message, false); }
            finally
            {
                DocumentsRoot = savedDocuments;
                EnvironmentLookup = savedEnv;
                try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch (Exception) { }
            }
        }

        /// <summary>
        /// N-modules-fixes-02 (lane feature/followups): when Windows reports no Documents folder, a blank storage location
        /// resolves to NO folder, never the relative "Remembrance" that GetFolderPath's "" used to make, and a recording is
        /// refused with the reason on the Status line and nothing written. Both seams are pointed at exactly what production
        /// gives a missing known folder, CaptureStore.RemembranceUnder(""), and the process's current directory at an empty
        /// scratch folder for the duration: a relative root put back (a mutant) then records THERE, where the check sees it,
        /// and nowhere else. A real folder through the same seams is the witness that the default still resolves.
        /// </summary>
        private static void SelfCheckNoDocumentsFolder(Action<string, bool> check)
        {
            string[] noFolder = { null, "", "   ", "Documents", @"C:Documents" };
            check("no Documents: a known folder that names no folder gives no default root, never a relative one: " +
                  string.Join(" | ", noFolder.Select(d => CaptureStore.RemembranceUnder(d) ?? "(none)")),
                noFolder.All(d => CaptureStore.RemembranceUnder(d) == null));
            check("WITNESS no Documents: a real Documents folder, a drive's or a share's, still resolves to Remembrance under it",
                CaptureStore.RemembranceUnder(@"C:\Users\someone\Documents") == @"C:\Users\someone\Documents\Remembrance" &&
                CaptureStore.RemembranceUnder(@"\\server\home\someone\Documents") == @"\\server\home\someone\Documents\Remembrance");

            string scratch = Path.Combine(Path.GetTempPath(), "dp-remembrance-nodocs-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string cwd = Path.Combine(scratch, "cwd");
            string realDocuments = Path.Combine(scratch, "Documents");
            Func<string> savedDocuments = DocumentsRoot;
            Func<string> savedDefault = CaptureStore.DefaultRootSource;
            string savedDirectory = Environment.CurrentDirectory;
            try
            {
                Directory.CreateDirectory(cwd);
                Directory.CreateDirectory(realDocuments);
                Environment.CurrentDirectory = cwd;
                Func<string> missing = delegate { return CaptureStore.RemembranceUnder(""); };
                DocumentsRoot = missing;
                CaptureStore.DefaultRootSource = missing;

                string refused;
                try { refused = "(not refused; its root is \"" + new CaptureStore("  ", FolderLayout.PerCapture).Root + "\")"; }
                catch (InvalidOperationException ex) { refused = ex.Message; }
                check("no Documents: a blank storage root with no default is refused by the store, with the reason: " + refused,
                    refused == CaptureStore.NoDefaultRootReason);

                using (new RecorderSelfCheck.FakeDevices())
                {
                    // Saved blank (what an Apply wrote on most installs) and never saved (a fresh install): both are the default.
                    foreach (string saved in new[] { "", null })
                    {
                        string kind = saved == null ? "unsaved" : "saved blank";
                        var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                        DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings s = host.SettingsFor(Id);
                        if (saved != null) s.Set("storageLocation", saved);
                        s.Set("summaryModelsCache", "alpha:1b");
                        s.Set("whisperExe", @"c:\seeded\whisper-cli.exe");
                        var module = new RemembranceModule();
                        module.Init(host);
                        try
                        {
                            OptionsPane pane = host.OptionsPanes[0];
                            IReadOnlyDictionary<string, string> shown = pane.Load();
                            SettingField box = FieldFor(pane, "storageLocation");
                            string renamed;
                            try
                            {
                                Task<string> press = module.RenameOlderForSelfTest(CopyOf(shown));
                                renamed = press.Wait(TimeSpan.FromSeconds(10)) ? press.Result : "(no answer)";
                            }
                            catch (Exception ex) { renamed = "(threw " + ex.GetType().Name + ")"; }
                            string olderLine;
                            shown.TryGetValue(OlderRecordingsKey, out olderLine);
                            check("no Documents (" + kind + "): the Older recordings line and its rename button say there is no folder: " +
                                  olderLine + " / " + renamed,
                                olderLine == CaptureStore.NoDefaultRootReason && renamed == CaptureStore.NoDefaultRootReason);
                            string status = module.StatusLine();
                            check("no Documents (" + kind + "): the Status line says there is no storage folder, and why: " + status,
                                status.Contains("  |  storage: ✗ none. " + CaptureStore.NoDefaultRootReason + "  |  "));
                            check("no Documents (" + kind + "): the blank storage box's hint says blank means no folder here: " +
                                  (box == null ? "(no box)" : box.EmptyHint),
                                box != null && box.EmptyHint == NoDocumentsEmptyHint);
                            module.StartRecordingForSelfTest();
                            CapturePaths paths = module.LastCaptureForSelfTest;
                            if (paths != null) Thread.Sleep(80);
                            string started = module.StatusLine();
                            host.RaiseHostShutdown();
                            check("no Documents (" + kind + "): a recording does not start, and the Status line says why: " +
                                  (paths == null ? started : "recording to " + paths.Directory),
                                paths == null && started.StartsWith("Could not start: " + CaptureStore.NoDefaultRootReason + "  |  ", StringComparison.Ordinal));
                            check("no Documents (" + kind + "): the storage location is left as it was and no storage line is logged",
                                s.Get("storageLocation", null) == saved &&
                                !host.LoggedLines.Any(l => l.StartsWith(Id + ": storage", StringComparison.Ordinal)));
                            bool ran = module.PurgeForSelfTest().Wait(TimeSpan.FromSeconds(10));
                            List<string> purge = host.LoggedLines.Where(l => l.StartsWith(Id + ": purge", StringComparison.Ordinal)).ToList();
                            check("no Documents (" + kind + "): the purge has no folder to walk and says nothing: " + string.Join(" | ", purge),
                                ran && purge.Count == 0);
                        }
                        finally { module.Shutdown(); }
                    }
                    string[] written = Directory.GetFileSystemEntries(cwd);
                    check("no Documents: nothing was written in the current directory, where a relative root resolves: " +
                          written.Length.ToString(CultureInfo.InvariantCulture) + " entries",
                        written.Length == 0);

                    // ---- the witness: a Documents folder that exists, through the same two seams ----
                    Func<string> present = delegate { return CaptureStore.RemembranceUnder(realDocuments); };
                    DocumentsRoot = present;
                    CaptureStore.DefaultRootSource = present;
                    string expected = Path.Combine(realDocuments, "Remembrance");
                    var realHost = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    realHost.SettingsFor(Id).Set("storageLocation", "");
                    realHost.SettingsFor(Id).Set("summaryModelsCache", "alpha:1b");
                    realHost.SettingsFor(Id).Set("whisperExe", @"c:\seeded\whisper-cli.exe");
                    var realModule = new RemembranceModule();
                    realModule.Init(realHost);
                    try
                    {
                        string realStatus = realModule.StatusLine();
                        realModule.StartRecordingForSelfTest();
                        CapturePaths realPaths = realModule.LastCaptureForSelfTest;
                        Thread.Sleep(80);
                        realHost.RaiseHostShutdown();
                        check("WITNESS no Documents: with a Documents folder a blank location still records under Documents\\Remembrance: " +
                              (realPaths == null ? "(no capture) " + realModule.StatusLine() : realPaths.Directory),
                            realStatus.Contains("  |  storage: " + expected + "  |  ") && realPaths != null &&
                            realPaths.Directory.StartsWith(expected + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                    }
                    finally { realModule.Shutdown(); }
                }
            }
            catch (Exception ex) { check("no Documents: the checks threw " + ex.GetType().Name + ": " + ex.Message, false); }
            finally
            {
                try { Environment.CurrentDirectory = savedDirectory; } catch (Exception) { }
                DocumentsRoot = savedDocuments;
                CaptureStore.DefaultRootSource = savedDefault;
                ClearReadOnly(scratch);
                try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch (Exception) { }
            }
        }

        /// <summary>
        /// N-followups-02 (lane feature/followups2): a storage location that is not a full path names no folder. The pane's
        /// Apply refuses one whole, saves nothing and says why on the Status line; one saved before that, or by hand, is never
        /// resolved: the store refuses it, a recording does not start and says why, and the purge, the Older recordings line,
        /// its rename button and the follow-up's newest summary leave it alone, as a move away from it does. The process's
        /// current directory is a scratch folder for the duration, holding a folder of the relative name with an aged
        /// recording and a summary in it: a mutant that resolves the path does so THERE, where the check sees it.
        /// </summary>
        private static void SelfCheckRelativeStorage(Action<string, bool> check)
        {
            string[] notFull = { "Recordings", @"Meetings\Recordings", @"\Recordings", "C:Recordings", @".\Recordings" };
            var refusals = new List<string>();
            foreach (string root in notFull)
            {
                try { refusals.Add(root + " (taken as \"" + new CaptureStore(root, FolderLayout.PerCapture).Root + "\")"); }
                catch (InvalidOperationException ex) { refusals.Add(ex.Message == CaptureStore.RelativeRootReason ? "refused" : root + " (" + ex.Message + ")"); }
            }
            check("relative storage: the store refuses a root that is not a full path, with the reason: " + string.Join(" | ", refusals),
                refusals.All(r => r == "refused"));
            check("WITNESS relative storage: a drive's folder and a share's are still taken as they are",
                new CaptureStore(@"D:\Recordings", FolderLayout.PerCapture).Root == @"D:\Recordings" &&
                new CaptureStore(@" \\server\share\Recordings ", FolderLayout.ByDate).Root == @"\\server\share\Recordings");

            const string Relative = "Recordings";
            string scratch = Path.Combine(Path.GetTempPath(), "dp-remembrance-relative-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string cwd = Path.Combine(scratch, "cwd");
            string resolved = Path.Combine(cwd, Relative);
            string full = Path.Combine(scratch, "store");
            string savedDirectory = Environment.CurrentDirectory;
            try
            {
                Directory.CreateDirectory(full);
                string strayAudio = Path.Combine(resolved, "2026-08-20 09-30 Stray", CaptureStore.FolderPrefix + ".wav");
                PlantAged(new[] { strayAudio }, DateTime.UtcNow.AddDays(-4));
                string straySummary = Path.Combine(resolved, "2026-10-29 09-30 Stray", CaptureStore.FolderPrefix + CaptureStore.SummarySuffix);
                Directory.CreateDirectory(Path.GetDirectoryName(straySummary));
                File.WriteAllText(straySummary, "a summary from wherever the app was started");
                string[] planted = Directory.GetFileSystemEntries(resolved);
                Environment.CurrentDirectory = cwd;

                using (new RecorderSelfCheck.FakeDevices())
                {
                    // ---- the pane: an Apply of a path that is not a full one is refused whole ----
                    var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings s = host.SettingsFor(Id);
                    s.Set("storageLocation", full);
                    s.Set("sysEnabled", "true");
                    s.Set("summaryModelsCache", "alpha:1b");
                    s.Set("whisperExe", @"c:\seeded\whisper-cli.exe");
                    var module = new RemembranceModule();
                    module.Init(host);
                    try
                    {
                        OptionsPane pane = host.OptionsPanes[0];
                        foreach (string typed in new[] { Relative, "C:Recordings" })
                        {
                            Dictionary<string, string> edited = CopyOf(pane.Load());
                            edited["storageLocation"] = typed;
                            edited["sysEnabled"] = "false";
                            int before = host.LoggedLines.Count;
                            bool applied = pane.Save(edited);
                            List<string> said = host.LoggedLines.Skip(before).ToList();
                            string status = module.StatusLine();
                            check("relative storage: an Apply of \"" + typed + "\" is refused whole, saving nothing, and the Status line says why: " + status,
                                !applied && s.Get("storageLocation", null) == full && s.Get("sysEnabled", null) == "true" &&
                                status.Contains("  |  ✗ Not applied: " + CaptureStore.RelativeRootReason + "  |  "));
                            check("relative storage: the refused Apply's one log line names no folder: " + string.Join(" | ", said),
                                said.Count == 1 && said[0] == Id + ": storage location not applied: it is not a full path");
                        }
                        Dictionary<string, string> corrected = CopyOf(pane.Load());
                        string other = Path.Combine(scratch, "other");
                        corrected["storageLocation"] = other;
                        bool correctedApplied = pane.Save(corrected);
                        check("WITNESS relative storage: a full path then applies, and the Status line no longer says an Apply was refused",
                            correctedApplied && s.Get("storageLocation", null) == other && !module.StatusLine().Contains("Not applied"));
                    }
                    finally { module.Shutdown(); }

                    // ---- saved before the pane refused it, or by hand: never resolved ----
                    var oldHost = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings old = oldHost.SettingsFor(Id);
                    old.Set("storageLocation", Relative);
                    old.Set("summaryModelsCache", "alpha:1b");
                    old.Set("whisperExe", @"c:\seeded\whisper-cli.exe");
                    var oldModule = new RemembranceModule();
                    oldModule.Init(oldHost);
                    try
                    {
                        IReadOnlyDictionary<string, string> shown = oldHost.OptionsPanes[0].Load();
                        string status = oldModule.StatusLine();
                        check("relative storage (saved): the Status line names the location and says why it is no folder: " + status,
                            status.Contains("  |  storage: " + Relative + " ✗ " + CaptureStore.RelativeRootReason + "  |  "));
                        string olderLine;
                        shown.TryGetValue(OlderRecordingsKey, out olderLine);
                        string renamed;
                        try
                        {
                            Task<string> press = oldModule.RenameOlderForSelfTest(CopyOf(shown));
                            renamed = press.Wait(TimeSpan.FromSeconds(10)) ? press.Result : "(no answer)";
                        }
                        catch (Exception ex) { renamed = "(threw " + ex.GetType().Name + ")"; }
                        check("relative storage (saved): the Older recordings line and its rename button say why, and read nothing: " +
                              olderLine + " / " + renamed,
                            olderLine == CaptureStore.RelativeRootReason && renamed == CaptureStore.RelativeRootReason);
                        string latest = oldModule.LatestSummaryPath();
                        check("relative storage (saved): the follow-up does not look for its newest summary there: " + (latest ?? "(none)"),
                            latest == null);
                        oldModule.StartRecordingForSelfTest();
                        CapturePaths paths = oldModule.LastCaptureForSelfTest;
                        if (paths != null) Thread.Sleep(80);
                        string started = oldModule.StatusLine();
                        oldHost.RaiseHostShutdown();
                        check("relative storage (saved): a recording does not start, and the Status line says why: " +
                              (paths == null ? started : "recording to " + paths.Directory),
                            paths == null && started.StartsWith("Could not start: " + CaptureStore.RelativeRootReason + "  |  ", StringComparison.Ordinal));
                        bool ran = oldModule.PurgeForSelfTest().Wait(TimeSpan.FromSeconds(10));
                        List<string> purge = oldHost.LoggedLines.Where(l => l.StartsWith(Id + ": purge", StringComparison.Ordinal)).ToList();
                        check("relative storage (saved): the purge has no folder to walk, says nothing and deletes nothing: " + string.Join(" | ", purge),
                            ran && purge.Count == 0 && File.Exists(strayAudio));
                    }
                    finally { oldModule.Shutdown(); }

                    // ---- a move away from it remembers nothing for the purge ----
                    var moveHost = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings m = moveHost.SettingsFor(Id);
                    m.Set("storageLocation", Relative);
                    m.Set("summaryModelsCache", "alpha:1b");
                    m.Set("whisperExe", @"c:\seeded\whisper-cli.exe");
                    var moveModule = new RemembranceModule();
                    moveModule.Init(moveHost);
                    try
                    {
                        OptionsPane movePane = moveHost.OptionsPanes[0];
                        Dictionary<string, string> moved = CopyOf(movePane.Load());
                        moved["storageLocation"] = full;
                        bool movedApplied = movePane.Save(moved);
                        check("relative storage (saved): a move away from it applies and does not remember it for the purge: \"" +
                              m.Get(EarlierRootsKey, "") + "\"",
                            movedApplied && m.Get("storageLocation", null) == full && ParseRoots(m.Get(EarlierRootsKey, "")).Count == 0);
                    }
                    finally { moveModule.Shutdown(); }
                }
                string[] after = Directory.GetFileSystemEntries(resolved);
                check("relative storage: nothing was written where the relative location resolves, " +
                      after.Length.ToString(CultureInfo.InvariantCulture) + " entries for the " +
                      planted.Length.ToString(CultureInfo.InvariantCulture) + " planted",
                    after.Length == planted.Length && Directory.GetFileSystemEntries(cwd).Length == 1);
            }
            catch (Exception ex) { check("relative storage: the checks threw " + ex.GetType().Name + ": " + ex.Message, false); }
            finally
            {
                try { Environment.CurrentDirectory = savedDirectory; } catch (Exception) { }
                ClearReadOnly(scratch);
                try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch (Exception) { }
            }
        }
    }
}
