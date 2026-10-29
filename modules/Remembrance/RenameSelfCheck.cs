using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// 2.6.0's names, date and time first, and the rename of the names from before: the " (2)" rule in both layouts, what
    /// the purge reads as ours under each name and where, the rename over a tree holding every old shape, the put-back
    /// when one file of a capture fails, the never-overwrite, and the Storage card's line and button. Every file is under
    /// <paramref name="scratch"/>; nothing reaches the user's recordings.
    /// </summary>
    internal static class RenameSelfCheck
    {
        internal static void Run(Action<string, bool> check, string scratch)
        {
            // A LOCAL instant, so NewCapture's ToLocalTime is the identity in every zone (F179).
            var fixture = new DateTimeOffset(new DateTime(2026, 8, 27, 9, 30, 15, DateTimeKind.Local));
            Directory.CreateDirectory(scratch);
            // A file the test expects and does not find is a FAIL line, never an exception that ends the group.
            Func<string, string> text = path => File.Exists(path) ? File.ReadAllText(path) : null;

            // ---- the name, and a second capture inside the same minute ----
            string perRoot = Path.Combine(scratch, "per");
            var per = new CaptureStore(perRoot, FolderLayout.PerCapture);
            CapturePaths first = per.NewCapture("Standup", fixture);
            CapturePaths second = per.NewCapture("Standup", fixture.AddSeconds(30));
            check("names 2.6.0: per capture, a second capture of one meeting in the same minute takes \" (2)\", never the first's folder",
                first.BaseName == "2026-08-27 09-30 Standup" && second.BaseName == "2026-08-27 09-30 Standup (2)"
                && second.Directory != first.Directory && Directory.Exists(second.Directory));

            string dayRoot = Path.Combine(scratch, "day");
            var byDay = new CaptureStore(dayRoot, FolderLayout.ByDate);
            CapturePaths dayFirst = byDay.NewCapture("Standup", fixture);
            File.WriteAllText(dayFirst.Transcript, "first");
            CapturePaths daySecond = byDay.NewCapture("Standup", fixture.AddSeconds(30));
            check("names 2.6.0: by date, a capture whose name a file in the day folder already uses takes \" (2)\"",
                dayFirst.BaseName == "2026-08-27 09-30 Standup" && daySecond.BaseName == "2026-08-27 09-30 Standup (2)"
                && Path.GetFileName(daySecond.Audio) == "2026-08-27 09-30 Standup (2).wav"
                && File.ReadAllText(dayFirst.Transcript) == "first");
            check("WITNESS a name nothing uses is taken as it is, and one a rename has claimed is not",
                CaptureStore.FreeBaseName("2026-08-27 11-00 Free", perRoot, true, null) == "2026-08-27 11-00 Free"
                && CaptureStore.FreeBaseName("2026-08-27 11-00 Free", perRoot, true, new List<string> { "2026-08-27 11-00 Free" })
                   == "2026-08-27 11-00 Free (2)");

            // ---- what the purge reads as ours, and where ----
            check("purge 2.6.0: in its day's folder a time-first recording, with or without a meeting or a \" (2)\", is ours",
                CaptureStore.NamesThisModuleWrites("2026-08-27 09-30 Standup.wav", false, "2026-08-27")
                && CaptureStore.NamesThisModuleWrites("2026-08-27 09-30.wav", false, "2026-08-27")
                && CaptureStore.NamesThisModuleWrites("2026-08-27 09-30 Standup (2).wav", false, "2026-08-27")
                && CaptureStore.NamesThisModuleWrites("2026-08-27 09-30 Standup" + AudioRecorder.SystemScratchSuffix, false, "2026-08-27")
                && CaptureStore.NamesThisModuleWrites("2026-08-27 09-30 Standup" + AudioRecorder.MicScratchSuffix, false, "2026-08-27"));
            check("purge 2.6.0: the same names loose in the storage ROOT are not ours, since nothing new is written flat there",
                !CaptureStore.NamesThisModuleWrites("2026-08-27 09-30 Standup.wav", false)
                && !CaptureStore.NamesThisModuleWrites("2026-08-27 09-30.wav", false)
                && !CaptureStore.NamesThisModuleWrites("2026-08-27 09-30 Standup" + AudioRecorder.MicScratchSuffix, false));
            check("purge 2.6.0: in a day folder a time-first name for ANOTHER day is not ours",
                !CaptureStore.NamesThisModuleWrites("2026-08-26 09-30 Standup.wav", false, "2026-08-27"));
            check("purge 2.6.0: near-misses of the time-first stamp are not ours",
                !CaptureStore.NamesThisModuleWrites("2026-08-27 0930 Standup.wav", false, "2026-08-27")
                && !CaptureStore.NamesThisModuleWrites("2026-08-27 09-30Standup.wav", false, "2026-08-27")
                && !CaptureStore.NamesThisModuleWrites("2026-08-27 09-30 .wav", false, "2026-08-27")
                && !CaptureStore.NamesThisModuleWrites("2026-08-27 25-30 Standup.wav", false, "2026-08-27")
                && !CaptureStore.NamesThisModuleWrites("Standup 2026-08-27 09-30.wav", false, "2026-08-27"));
            check("WITNESS purge 2.6.0: the names from before 2.6.0 are still ours, in a day folder and in the root",
                CaptureStore.NamesThisModuleWrites("Standup - 2026-08-27 09-30-15.wav", false, "2026-08-27")
                && CaptureStore.NamesThisModuleWrites("Standup - 2026-08-27 09-30-15.wav", false));
            check("purge 2.6.0: a time-first folder is a capture folder, and the old ones still are",
                CaptureStore.IsCaptureFolderName("2026-08-27 09-30 Standup") && CaptureStore.IsCaptureFolderName("2026-08-27 09-30")
                && CaptureStore.IsCaptureFolderName("2026-08-27 09-30 Standup (2)")
                && CaptureStore.IsCaptureFolderName("Standup - 2026-08-27 09-30-15") && CaptureStore.IsCaptureFolderName("2026-08-27 09-30-15")
                && !CaptureStore.IsCaptureFolderName("2026-08-27") && !CaptureStore.IsCaptureFolderName("2026-08-27 0930 Standup"));

            // The purge itself, end to end, over files four days old.
            string purgeRoot = Path.Combine(scratch, "purge");
            string day = Path.Combine(purgeRoot, "2026-08-27");
            string capture = Path.Combine(purgeRoot, "2026-08-27 09-30 Standup");
            Directory.CreateDirectory(day);
            Directory.CreateDirectory(capture);
            string dayWav = Path.Combine(day, "2026-08-27 09-30 Standup.wav");
            string dayTranscript = Path.Combine(day, "2026-08-27 09-30 Standup.transcript.txt");
            string looseWav = Path.Combine(purgeRoot, "2026-08-27 09-30 Standup.wav");
            string captureWav = Path.Combine(capture, "recording.wav");
            string captureTranscript = Path.Combine(capture, "recording.transcript.txt");
            DateTime old = DateTime.UtcNow.AddDays(-4);
            foreach (string file in new[] { dayWav, dayTranscript, looseWav, captureWav, captureTranscript })
            {
                File.WriteAllText(file, "x");
                File.SetLastWriteTimeUtc(file, old);
            }
            new CaptureStore(purgeRoot, FolderLayout.ByDate).Purge();
            check("purge 2.6.0: an old time-first recording goes from its day folder and from its capture folder; the transcripts stay",
                !File.Exists(dayWav) && !File.Exists(captureWav) && File.Exists(dayTranscript) && File.Exists(captureTranscript));
            check("WITNESS purge 2.6.0: a time-first name loose in the root is left alone",
                File.Exists(looseWav));

            // ---- the rename, over a tree holding every old shape ----
            string root = Path.Combine(scratch, "rename");
            Func<string, string> at = relative => Path.Combine(root, relative);
            Action<string, string> write = (relative, text) =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(at(relative)));
                File.WriteAllText(at(relative), text);
            };
            write(@"Sprint Review - 2026-08-27 09-30-00\recording.wav", "sprint");
            write(@"Sprint Review - 2026-08-27 09-30-00\recording.transcript.txt", "sprint");
            write(@"Sprint Review - 2026-08-27 09-30-00\snap 2026-08-27 09-31-00.png", "sprint");
            write(@"2026-08-27 10-00-05\recording.transcript.txt", "unnamed");
            // Listed latest first, so the order the rename assigns " (2)" in is its own, not the listing's.
            write(@"Standup - 2026-08-27 11-00-50\recording.transcript.txt", "later");
            write(@"Standup - 2026-08-27 11-00-05\recording.transcript.txt", "earlier");
            write(@"2026-08-28\Planning - 2026-08-28 14-08-25.wav", "planning");
            write(@"2026-08-28\Planning - 2026-08-28 14-08-25.transcript.txt", "planning");
            write(@"2026-08-28\Planning - 2026-08-28 14-08-25.summary.txt", "planning");
            write(@"2026-08-28\Planning - 2026-08-28 14-08-25 - snap 2026-08-28 14-09-00.png", "planning");
            write(@"2026-08-28\notes.txt", "mine");
            write(@"2026-08-28\Other - 2026-08-27 09-00-00.transcript.txt", "moved here by hand");
            write(@"Old - 2026-08-01 09-00-00.transcript.txt", "loose");
            write(@"2026-08-29 08-00 Already\recording.transcript.txt", "new");
            var store = new CaptureStore(root, FolderLayout.ByDate);
            RenamePlan plan = store.PlanRenames();
            check("rename: the plan finds four old capture folders and one old capture in a day folder, and moves nothing yet",
                plan.Groups.Count == 5 && plan.Folders == 4 && plan.Files == 4
                && Directory.Exists(at("Sprint Review - 2026-08-27 09-30-00")));
            check("rename: it counts, and leaves, a file loose in the root and a capture in another day's folder",
                plan.LooseInRoot == 1 && plan.OtherDay == 1);
            string preview = RemembranceModule.DescribePlan(plan);
            check("rename: the line says how many before anything moves, and what is left: " + preview,
                preview.StartsWith("5 recordings still have the names from before 2.6.0", StringComparison.Ordinal)
                && preview.Contains("1 file from before 2.0.0 lies loose") && preview.Contains("1 recording in another day's folder"));
            check("WITNESS the line names no meeting", preview.IndexOf("Sprint", StringComparison.OrdinalIgnoreCase) < 0
                && preview.IndexOf("Planning", StringComparison.OrdinalIgnoreCase) < 0);

            RenameReport report = CaptureStore.ApplyRenames(plan);
            check("rename: every planned capture is renamed, one needing \" (2)\""
                  + (report.CapturesFailed > 0 ? " (failed: " + report.FailureTypes + ")" : ""),
                report.Captures == 5 && report.Folders == 4 && report.Files == 4 && report.Suffixed == 1 && report.CapturesFailed == 0);
            check("rename: a capture folder takes the time-first name and keeps its files' own names",
                text(at(@"2026-08-27 09-30 Sprint Review\recording.wav")) == "sprint"
                && File.Exists(at(@"2026-08-27 09-30 Sprint Review\snap 2026-08-27 09-31-00.png"))
                && !Directory.Exists(at("Sprint Review - 2026-08-27 09-30-00"))
                && text(at(@"2026-08-27 10-00\recording.transcript.txt")) == "unnamed");
            check("rename: of two captures of one meeting in one minute the EARLIER keeps the plain name",
                text(at(@"2026-08-27 11-00 Standup\recording.transcript.txt")) == "earlier"
                && text(at(@"2026-08-27 11-00 Standup (2)\recording.transcript.txt")) == "later");
            check("rename: in a day folder every file of the capture moves together, its snapshot too",
                File.Exists(at(@"2026-08-28\2026-08-28 14-08 Planning.wav"))
                && File.Exists(at(@"2026-08-28\2026-08-28 14-08 Planning.transcript.txt"))
                && File.Exists(at(@"2026-08-28\2026-08-28 14-08 Planning.summary.txt"))
                && File.Exists(at(@"2026-08-28\2026-08-28 14-08 Planning - snap 2026-08-28 14-09-00.png"))
                && !File.Exists(at(@"2026-08-28\Planning - 2026-08-28 14-08-25.wav")));
            check("WITNESS rename: the user's file, another day's capture, the loose root file and a new-named folder are untouched",
                text(at(@"2026-08-28\notes.txt")) == "mine"
                && File.Exists(at(@"2026-08-28\Other - 2026-08-27 09-00-00.transcript.txt"))
                && File.Exists(at("Old - 2026-08-01 09-00-00.transcript.txt"))
                && text(at(@"2026-08-29 08-00 Already\recording.transcript.txt")) == "new");
            check("rename: the renamed audio is still the purge's to delete",
                CaptureStore.NamesThisModuleWrites("2026-08-28 14-08 Planning.wav", false, "2026-08-28")
                && CaptureStore.IsCaptureFolderName("2026-08-27 11-00 Standup (2)"));
            RenamePlan again = store.PlanRenames();
            check("rename: a second pass finds nothing left to rename",
                again.Groups.Count == 0
                && RemembranceModule.DescribePlan(again).StartsWith("Every recording here has the new names", StringComparison.Ordinal));
            string done = RemembranceModule.DescribeRename(report, plan);
            string logged = RemembranceModule.RenameLogLine(report, plan);
            check("rename: the answer counts what moved: " + done,
                done.StartsWith("Renamed 5 recordings (4 folders, 4 files). 1 recording took \" (2)\"", StringComparison.Ordinal));
            check("WITNESS rename: the log line carries counts and no meeting: " + logged,
                logged.StartsWith("renamed older recordings: 5 of 5 (4 folders, 4 files, 1 suffixed)", StringComparison.Ordinal)
                && logged.IndexOf("Standup", StringComparison.OrdinalIgnoreCase) < 0 && logged.IndexOf("Sprint", StringComparison.OrdinalIgnoreCase) < 0);

            // 2.9.0: "Run summary" on an old-named transcript writes "<old base>.summary.md"; it moves with its capture.
            string mdDay = Path.Combine(scratch, "rename-md", "2026-08-28");
            Directory.CreateDirectory(mdDay);
            foreach (string name in new[] { "Retro - 2026-08-28 16-00-00.transcript.txt", "Retro - 2026-08-28 16-00-00.summary.txt",
                                            "Retro - 2026-08-28 16-00-00.summary.md" })
                File.WriteAllText(Path.Combine(mdDay, name), name);
            RenameReport mdReport = CaptureStore.ApplyRenames(new CaptureStore(Path.GetDirectoryName(mdDay), FolderLayout.ByDate).PlanRenames());
            check("rename 2.9.0: a .summary.md beside an old-named transcript moves with it, and the .summary.txt too",
                mdReport.Captures == 1 && mdReport.Files == 3
                && File.Exists(Path.Combine(mdDay, "2026-08-28 16-00 Retro.summary.md"))
                && File.Exists(Path.Combine(mdDay, "2026-08-28 16-00 Retro.summary.txt"))
                && !Directory.EnumerateFiles(mdDay, "Retro - *").Any());

            // ---- one file failing puts the capture back; nothing is overwritten ----
            string failRoot = Path.Combine(scratch, "fail");
            string failDay = Path.Combine(failRoot, "2026-08-28");
            Directory.CreateDirectory(failDay);
            string[] oldFiles = { "Review - 2026-08-28 15-00-00.wav", "Review - 2026-08-28 15-00-00.transcript.txt",
                                  "Review - 2026-08-28 15-00-00.summary.txt" };
            foreach (string name in oldFiles) File.WriteAllText(Path.Combine(failDay, name), name);
            Action<string, string, bool> moveWas = CaptureStore.MoveEntry;
            try
            {
                // A file held open: every try at THAT file fails, as the retry would meet it.
                int heldTries = 0;
                CaptureStore.MoveEntry = (from, to, isFolder) =>
                {
                    if (from.EndsWith("15-00-00.transcript.txt", StringComparison.Ordinal)) { heldTries++; throw new IOException("held open"); }
                    moveWas(from, to, isFolder);
                };
                RenameReport failed = CaptureStore.ApplyRenames(new CaptureStore(failRoot, FolderLayout.ByDate).PlanRenames());
                check("rename: a capture one of whose files fails is put back whole, under its old names",
                    failed.CapturesFailed == 1 && failed.Captures == 0 && failed.NotPutBack == 0
                    && oldFiles.All(name => File.Exists(Path.Combine(failDay, name)))
                    && !Directory.EnumerateFiles(failDay, "2026-08-28 15-00 *").Any());
                check("rename: a file held open is tried " + CaptureStore.MoveAttempts + " times before its capture is put back",
                    heldTries > 1 && heldTries == CaptureStore.MoveAttempts);
                check("...and the answer says it was left as before, by exception type",
                    RemembranceModule.DescribeRename(failed, new RenamePlan { Groups = { new RenameGroup() } })
                        .Contains("1 recording could not be renamed (IOException) and was left as before"));
            }
            finally { CaptureStore.MoveEntry = moveWas; }

            RenamePlan raced = new CaptureStore(failRoot, FolderLayout.ByDate).PlanRenames();
            // Something takes one of the planned names between the plan and the move.
            File.WriteAllText(Path.Combine(failDay, "2026-08-28 15-00 Review.summary.txt"), "someone else's");
            RenameReport refused = CaptureStore.ApplyRenames(raced);
            check("rename: a target that appeared after the plan is never overwritten, and the capture is put back",
                refused.CapturesFailed == 1
                && text(Path.Combine(failDay, "2026-08-28 15-00 Review.summary.txt")) == "someone else's"
                && oldFiles.All(name => File.Exists(Path.Combine(failDay, name))));

            // ---- the Storage card's line and button ----
            string paneRoot = Path.Combine(scratch, "pane");
            Directory.CreateDirectory(Path.Combine(paneRoot, "Board - 2026-08-27 09-30-00"));
            File.WriteAllText(Path.Combine(paneRoot, "Board - 2026-08-27 09-30-00", "recording.transcript.txt"), "board");
            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            host.SettingsFor(RemembranceModule.Id).Set("storageLocation", paneRoot);
            host.SettingsFor(RemembranceModule.Id).Set("summaryModelsCache", "alpha:1b");
            host.SettingsFor(RemembranceModule.Id).Set("whisperExe", @"c:\seeded\whisper-cli.exe");
            var module = new RemembranceModule();
            module.Init(host);
            try
            {
                OptionsPane pane = host.OptionsPanes[0];
                IReadOnlyDictionary<string, string> shown = pane.Load();
                SettingField line = pane.Schema.FirstOrDefault(f => f != null && f.Id == RemembranceModule.OlderRecordingsKey);
                PaneAction button = pane.Actions.FirstOrDefault(a => a != null && a.Label == RemembranceModule.RenameOlderLabel);
                check("rename pane: the Storage card shows the Older recordings line, read from the folder, and the button",
                    line != null && line.Kind == SettingKind.Info && line.Group == "Storage"
                    && shown[RemembranceModule.OlderRecordingsKey].StartsWith("1 recording still has", StringComparison.Ordinal)
                    && button != null && button.Group == "Storage" && button.InvokeWithPendingAsync != null);

                var onScreen = new Dictionary<string, string>(shown) { ["storageLocation"] = Path.Combine(scratch, "elsewhere") };
                check("rename pane: with another storage folder on screen, unapplied, it refuses and moves nothing",
                    module.RenameOlderForSelfTest(onScreen).Result.StartsWith("The storage folder on screen has not been applied", StringComparison.Ordinal)
                    && Directory.Exists(Path.Combine(paneRoot, "Board - 2026-08-27 09-30-00")));

                module.StopsInFlightForSelfTest(1);
                string waiting = module.RenameOlderForSelfTest(shown).Result;
                module.StopsInFlightForSelfTest(-1);
                check("rename pane: while a stop is still saving or transcribing it waits, and moves nothing",
                    waiting.StartsWith("A recording is still being saved", StringComparison.Ordinal)
                    && Directory.Exists(Path.Combine(paneRoot, "Board - 2026-08-27 09-30-00")));
                check("WITNESS rename pane: with nothing in flight it may run", module.RenameRefusalForSelfTest() == null);

                string answer = module.RenameOlderForSelfTest(shown).Result;
                check("rename pane: pressed, it renames and says so: " + answer,
                    answer.StartsWith("Renamed 1 recording (1 folder, 0 files).", StringComparison.Ordinal)
                    && text(Path.Combine(paneRoot, "2026-08-27 09-30 Board", "recording.transcript.txt")) == "board");
                check("rename pane: ...and logs one line of counts, naming no meeting",
                    host.LoggedLines.Count(l => l.Contains("renamed older recordings: 1 of 1")) == 1
                    && !host.LoggedLines.Any(l => l.Contains("renamed older recordings") && l.Contains("Board")));
            }
            finally { module.Shutdown(); }
        }
    }
}
