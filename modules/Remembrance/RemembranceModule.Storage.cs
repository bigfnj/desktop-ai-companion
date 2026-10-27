using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
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
        /// Documents path. Defaults to CaptureStore.DefaultRoot.</summary>
        internal static Func<string> DocumentsRoot = CaptureStore.DefaultRoot;

        /// <summary>Directory.Exists behind a delegate, for the same reason.</summary>
        internal static Func<string, bool> FolderExists = Directory.Exists;

        /// <summary>
        /// The folder recordings go to now: the storage location saved; Documents\Remembrance while it is saved BLANK, which
        /// is what an Apply before 2.7.0 wrote for most installs and what "blank" has always meant; and for an install that
        /// has never saved one, <see cref="FreshInstallRoot"/>'s folder, else Documents\Remembrance.
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
            if (_settings.Get("storageLocation", null) != null || SameFolder(root, DocumentsRoot())) return root;
            SetInBackground("storageLocation", root);
            string notPersisted;
            TrySaveSettings("the storage folder", out notPersisted);
            Log("storage: recordings go to this app's data folder on this computer, because Documents is inside OneDrive");
            return root;
        }

        /// <summary>The note the Status line gives the storage folder when it is the local one a fresh install chose.</summary>
        private string StorageNote(string root)
        {
            string dataRoot = AppDataRootOf(DataDirectory());
            if (dataRoot == null || !SameFolder(root, Path.Combine(dataRoot, LocalStorageFolderName))) return "";
            return CaptureStore.OneDriveRootOf(root, EnvironmentLookup) == null ? " (on this computer, outside OneDrive)" : "";
        }

        // The storage folder's row, held so Load can set its hint (RefreshStorageHint).
        private SettingField _storageField;

        /// <summary>The storage folder box's hint while blank: the local folder's on an install whose unsaved default it is,
        /// Documents\Remembrance's otherwise. Set by Load, before the host reads the schema.</summary>
        private void RefreshStorageHint()
        {
            if (_storageField == null) return;
            bool local = _settings.Get("storageLocation", null) == null && !SameFolder(StorageRoot(), DocumentsRoot());
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
    }
}
