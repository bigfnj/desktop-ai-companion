using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// Renaming older recordings to the 2.6.0 names (the owner's decision of 2026-10-20: "rename"). The renames
    /// themselves are CaptureStore's (CaptureStore.Rename.cs); this half is the Storage card's line and button.
    ///
    /// A BUTTON, NOT A RENAME ON UPDATE. The module is published, and an update that renamed every user's recordings on
    /// its first start would act on files nobody asked it to touch. The Older recordings line says how many there are
    /// before anything moves, and the owner presses the button once.
    /// </summary>
    public sealed partial class RemembranceModule
    {
        internal const string OlderRecordingsKey = "olderRecordings";
        internal const string RenameOlderLabel = "Rename older recordings to the new names";

        /// <summary>Stops whose save, transcription and summary are still running (2.6.0). Each holds its capture's paths
        /// and writes to them, so the rename waits until there are none.</summary>
        private int _stopsInFlight;

        // ---- the Older recordings line, read off the UI thread (N-audit-1106-286) ----
        // Every pane Load walked the storage folder and each day folder in it (CaptureStore.PlanRenames) ON THE UI THREAD to
        // fill this line, and Load runs on every build of the pane and on every NotifyInfoChanged refresh, so a storage
        // folder on a slow or offline share stalled the whole Options window each time. The walk now runs on a pool thread,
        // one at a time. Load shows the line the last walk read for the same folder and layout, or says the folder is being
        // read, and a walk that lands with a different line tells the open pane, whose refresh then shows it; a walk that
        // reads the same line says nothing, so that refresh does not start another round. A Load while a walk runs asks for
        // one more after it, so the newest folder and layout are always the last read. Rejected: a short bounded wait in
        // Load for a walk that finishes quickly, which keeps the line exact on a fast disk but still stalls the UI thread for
        // the whole bound on every refresh of a pane whose folder is slow, the defect itself in a smaller size.
        private readonly object _olderLock = new object();
        private string _olderFor;          // the folder and layout _olderLine describes; null until a walk lands
        private string _olderLine;
        private string _olderWantedRoot;   // the newest Load's folder and layout, which the next walk reads
        private string _olderWantedLayout;
        private bool _olderAsked;          // a Load came after the running walk read what it walks
        private bool _olderWalking;        // the single-flight gate
        private int _olderGeneration;      // moved on by a rename, so a walk that read the folder before it is not kept
        private Task _olderWalk = Task.CompletedTask;
        private int _olderNotices;         // how many times a walk told the pane, for the self-test

        /// <summary>The line while no walk has read this folder and layout yet.</summary>
        internal const string OlderRecordingsReading = "Reading the storage folder…";

        /// <summary>The walk behind a delegate, so the self-test can hold one open and see Load answer without it
        /// (N-audit-1106-286). Defaults to the real plan, which only reads.</summary>
        internal static Func<CaptureStore, RenamePlan> PlanOlderRecordings = store => store.PlanRenames();

        /// <summary>The Older recordings line for Load, which runs on the UI thread: the settings are read here, the folder
        /// never is (N-audit-1106-286). Names only; nothing is moved.</summary>
        private string OlderRecordingsLine()
        {
            string root, layout;
            try
            {
                root = StorageRoot();
                if (root == null) return CaptureStore.NoDefaultRootReason;   // no folder to read (N-modules-fixes-02)
                if (!CaptureStore.IsFullFolder(root)) return CaptureStore.RelativeRootReason;   // nor here (N-followups-02)
                layout = CurrentFolderLayout();
            }
            catch (Exception ex) { return "Could not read the storage folder (" + ex.GetType().Name + ")."; }
            string line;
            lock (_olderLock)
            {
                line = _olderFor == OlderKey(root, layout) ? _olderLine : null;
                _olderWantedRoot = root;
                _olderWantedLayout = layout;
                _olderAsked = true;
                if (!_olderWalking)
                {
                    _olderWalking = true;
                    _olderWalk = Task.Run(() => WalkOlderRecordings());
                }
            }
            return line ?? OlderRecordingsReading;
        }

        private static string OlderKey(string root, string layout) { return root + "\n" + layout; }

        /// <summary>The walk, on a pool thread, until no Load has asked for another. Logs nothing: the line it reads is
        /// counts, and a failure is the line itself.</summary>
        private void WalkOlderRecordings()
        {
            while (true)
            {
                string root, layout;
                int generation;
                lock (_olderLock)
                {
                    if (!_olderAsked) { _olderWalking = false; return; }
                    _olderAsked = false;
                    root = _olderWantedRoot;
                    layout = _olderWantedLayout;
                    generation = _olderGeneration;
                }
                string line;
                try { line = DescribePlan(PlanOlderRecordings(new CaptureStore(root, layout))); }
                catch (Exception ex) { line = "Could not read the storage folder (" + ex.GetType().Name + ")."; }
                string key = OlderKey(root, layout);
                bool changed = false;
                lock (_olderLock)
                {
                    if (generation == _olderGeneration)
                    {
                        changed = _olderFor != key || !string.Equals(_olderLine, line, StringComparison.Ordinal);
                        _olderFor = key;
                        _olderLine = line;
                    }
                }
                if (changed)
                {
                    Interlocked.Increment(ref _olderNotices);
                    NotifyInfoChanged();
                }
            }
        }

        /// <summary>After a rename: the line read before it describes names that have moved, so the next Load says the
        /// folder is being read until a walk after the rename lands, and a walk already running is not kept.</summary>
        private void ForgetOlderRecordingsLine()
        {
            lock (_olderLock)
            {
                _olderGeneration++;
                _olderFor = null;
                _olderLine = null;
            }
        }

        /// <summary>The walk Load last started, for the self-test.</summary>
        internal Task OlderWalkForSelfTest { get { lock (_olderLock) return _olderWalk; } }
        internal int OlderNoticesForSelfTest { get { return Volatile.Read(ref _olderNotices); } }

        /// <summary>What the line says about a plan. Counts only, never a name. Pure, for the self-test.</summary>
        internal static string DescribePlan(RenamePlan plan)
        {
            if (plan == null) return "";
            string text = plan.Groups.Count == 0
                ? "Every recording here has the new names, date and time first."
                : Count(plan.Groups.Count, "recording") + " still " + (plan.Groups.Count == 1 ? "has" : "have")
                  + " the names from before 2.6.0, with the time last. \"" + RenameOlderLabel + "\" puts the time first.";
            return text + LeftAlone(plan);
        }

        /// <summary>The button's answer. Pure, for the self-test.</summary>
        internal static string DescribeRename(RenameReport report, RenamePlan plan)
        {
            if (report == null || plan == null) return "";
            string text;
            if (plan.Groups.Count == 0) text = "Nothing to rename: every recording here has the new names.";
            else
            {
                text = "Renamed " + Count(report.Captures, "recording")
                       + " (" + Count(report.Folders, "folder") + ", " + Count(report.Files, "file") + ").";
                if (report.Suffixed > 0)
                    text += " " + Count(report.Suffixed, "recording") + " took \" (2)\" or the like, because "
                            + (report.Suffixed == 1 ? "its" : "their") + " minute's name was already in use.";
                if (report.CapturesFailed > 0)
                    text += " " + Count(report.CapturesFailed, "recording") + " could not be renamed (" + report.FailureTypes
                            + ") and " + (report.CapturesFailed == 1 ? "was" : "were") + " left as before; close whatever has "
                            + (report.CapturesFailed == 1 ? "it" : "them") + " open and press again.";
                if (report.NotPutBack > 0)
                    text += " " + Count(report.NotPutBack, "file") + " of a recording that failed could not be put back, so "
                            + "that recording is split across its old and new names.";
            }
            return text + LeftAlone(plan);
        }

        private static string LeftAlone(RenamePlan plan)
        {
            string text = "";
            if (plan.LooseInRoot > 0)
                text += " " + Count(plan.LooseInRoot, "file") + " from before 2.0.0 " + (plan.LooseInRoot == 1 ? "lies" : "lie")
                        + " loose in the storage folder and " + (plan.LooseInRoot == 1 ? "is" : "are") + " left as before.";
            if (plan.OtherDay > 0)
                text += " " + Count(plan.OtherDay, "recording") + " in another day's folder " + (plan.OtherDay == 1 ? "is" : "are")
                        + " left as before.";
            if (plan.NoFreeName > 0)
                text += " " + Count(plan.NoFreeName, "recording") + " had no free name and " + (plan.NoFreeName == 1 ? "is" : "are")
                        + " left as before.";
            if (plan.Unread > 0)
                text += " " + Count(plan.Unread, "folder") + " could not be read.";
            return text;
        }

        private static string Count(int n, string noun)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");
        }

        /// <summary>Why the rename must wait, or null when it may run: never while a recording, a stop's save,
        /// transcription or summary, a model's work, or a snapshot could be writing under the names it would move.</summary>
        private string RenameRefusal()
        {
            if (_recording)
                return "Remembrance is recording. Press this again once the recording has stopped and its transcript is written.";
            if (Volatile.Read(ref _stopsInFlight) > 0)
                return "A recording is still being saved, transcribed or summarized. Press this again when it has finished.";
            lock (_busySync)
                if (_busySpans.Count > 0)
                    return "Remembrance is transcribing or summarizing. Press this again when it has finished.";
            if (Volatile.Read(ref _snapshotInFlight) != 0)
                return "A snapshot is being saved. Press this again in a moment.";
            return null;
        }

        /// <summary>
        /// The button. On the SAVED storage folder, and it says so when the one on screen differs: a rename is not an edit
        /// to preview, so it refuses to act on a folder that has not been applied. Off the UI thread, and the log line
        /// carries counts and exception types, never a name.
        /// </summary>
        private Task<string> RenameOlderAsync(IReadOnlyDictionary<string, string> pending)
        {
            string saved = _settings.Get("storageLocation", "");
            string onScreen = OnScreenSettings(pending).Get("storageLocation", "");
            if (!string.Equals((onScreen ?? "").Trim(), (saved ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                return Task.FromResult("The storage folder on screen has not been applied yet. Apply it first, then press this again.");
            string refusal = RenameRefusal();
            if (refusal != null) return Task.FromResult(refusal);
            // No folder to rename in (a blank location and no Documents, N-modules-fixes-02): said, where the store's
            // constructor would have thrown out of the button's press.
            string root = StorageRoot();
            if (root == null) return Task.FromResult(CaptureStore.NoDefaultRootReason);
            // Nor in a location that is not a full path (N-followups-02), whose store would throw out of the press.
            if (!CaptureStore.IsFullFolder(root)) return Task.FromResult(CaptureStore.RelativeRootReason);
            var store = new CaptureStore(root, CurrentFolderLayout());
            return Task.Run(() =>
            {
                RenamePlan plan = store.PlanRenames();
                RenameReport report = CaptureStore.ApplyRenames(plan);
                if (plan.Groups.Count > 0) ForgetOlderRecordingsLine();   // before the pane's reload reads it (N-audit-1106-286)
                if (plan.Groups.Count > 0) Log(RenameLogLine(report, plan));
                return DescribeRename(report, plan);
            });
        }

        /// <summary>The one log line a rename writes: counts and exception TYPE names. Pure, for the self-test.</summary>
        internal static string RenameLogLine(RenameReport report, RenamePlan plan)
        {
            string line = "renamed older recordings: " + report.Captures.ToString(CultureInfo.InvariantCulture) + " of "
                          + plan.Groups.Count.ToString(CultureInfo.InvariantCulture) + " (" + Count(report.Folders, "folder")
                          + ", " + Count(report.Files, "file") + ", " + report.Suffixed.ToString(CultureInfo.InvariantCulture) + " suffixed)";
            if (report.CapturesFailed > 0)
                line += "; " + report.CapturesFailed.ToString(CultureInfo.InvariantCulture) + " failed (" + report.FailureTypes + ")"
                        + (report.NotPutBack > 0 ? ", " + report.NotPutBack.ToString(CultureInfo.InvariantCulture) + " not put back" : "");
            if (plan.LooseInRoot + plan.OtherDay + plan.NoFreeName > 0)
                line += "; left as before: " + plan.LooseInRoot.ToString(CultureInfo.InvariantCulture) + " loose in the root, "
                        + plan.OtherDay.ToString(CultureInfo.InvariantCulture) + " in another day's folder, "
                        + plan.NoFreeName.ToString(CultureInfo.InvariantCulture) + " with no free name";
            return line;
        }

        /// <summary>The refusal, for the self-test: drives it with the module's own state.</summary>
        internal string RenameRefusalForSelfTest() { return RenameRefusal(); }

        /// <summary>Test seam: pretend a stop is in flight.</summary>
        internal void StopsInFlightForSelfTest(int delta) { Interlocked.Add(ref _stopsInFlight, delta); }

        /// <summary>The button's own delegate, for the self-test.</summary>
        internal Task<string> RenameOlderForSelfTest(IReadOnlyDictionary<string, string> pending) { return RenameOlderAsync(pending); }
    }
}
