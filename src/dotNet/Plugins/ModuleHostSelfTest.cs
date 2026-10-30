using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.Plugins
{
    /// <summary>
    /// --module-host-selftest: proves the plugin pipeline end-to-end without WinForms. Loads the real
    /// bundled test-module DLL (from &lt;baseDir&gt;\modules) through the AssemblyLoadContext loader against a
    /// recording host, then asserts the module's Init ran (contributed a tray item + an options pane) and
    /// that a raised CompanionPoked event reached the module (it calls host.SayAll). Skips-pass if the test
    /// module folder is absent (e.g. a payload without dev modules).
    /// </summary>
    internal static class ModuleHostSelfTest
    {
        public static bool Run()
        {
            var sb = new StringBuilder();
            bool ok = true;
            string scratch = null;
            string dataRootScratch = null;
            string previousDataRoot = null;
            bool dataRootRedirected = false;
            try
            {
                string modulesRoot = Path.Combine(AppContext.BaseDirectory, "modules");
                if (!Directory.Exists(Path.Combine(modulesRoot, "testmodule")))
                {
                    sb.AppendLine("SKIP: no bundled test module at " + Path.Combine(modulesRoot, "testmodule"));
                    return Finish(sb, true);
                }

                // ISOLATE THE DATA ROOT FIRST (F345). PaneAttribution loads every bundled module through a
                // REAL CompanionHost, whose GetStorage provisions <data root>\modules\<id> and whose Inits
                // write there: six modules' Inits ran against the gate exe's own data\ (or the user's live
                // root, when the flag is run against an installed exe). AppPaths resolves once, on first
                // touch, and nothing in Program.cs touches it before dispatching here, so the override set
                // now is the one that binds -- and the assertion is what makes that checkable rather than
                // assumed: a future early touch would leave DataRoot elsewhere and this line red.
                dataRootScratch = SelfTestScratch.Create("module-host-data");
                previousDataRoot = Environment.GetEnvironmentVariable(AppPaths.DataRootOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(AppPaths.DataRootOverrideEnvironmentVariable, dataRootScratch);
                dataRootRedirected = true;
                ok &= Check(sb, "data root isolated for this run (every module Init below writes under scratch)",
                    AppPaths.IsDataRootOverridden && SamePath(AppPaths.DataRoot, dataRootScratch));

                // One scratch root for every module these loads Init. The fake used to hand them
                // Path.GetTempPath() itself -- the user's TEMP root, not a scratch directory -- so Fortunes
                // created %TEMP%\fortunes and %TEMP%\vectors (+ cache.bin.lock) on every run and AiBrain once
                // copied the user's legacy ai-settings.json into %TEMP% (F351). None of those names start
                // with dp-, so the SelfTestScratch sweep never collected them. Released in the finally; the
                // release is expected to fail while the collectible ALCs still map the module DLLs, and the
                // next run's sweep collects it, exactly as the sibling self-tests already do.
                scratch = SelfTestScratch.Create("module-host");
                var host = new RecordingHost { StorageRoot = scratch };
                using (var loader = new ModuleHost())
                {
                    int loaded = loader.LoadFrom(modulesRoot, host, s => sb.AppendLine("  " + s));
                    ok &= Check(sb, "at least one module loaded", loaded >= 1);
                    // Asked of the loader that just loaded every bundled module. This line used to read
                    // Failures on a fresh `new ModuleHost()` that had never called LoadFrom (F347), so it
                    // could only fail if the constructor pre-populated the list, and a LoadFrom that
                    // started recording a spurious failure per loaded module would have stayed green.
                    ok &= Check(sb, "failures: a healthy load reports none",
                        loaded >= 1 && loader.Failures.Count == 0);
                    // Fortunes' Init points its paths at the storage root and builds the pool synchronously,
                    // creating <root>\fortunes on the way. Under the old fake that landed at the TEMP root;
                    // this is the line that fails if the fake ever hands out the TEMP root again. Two lines
                    // (RA-292): the first names the module whose Init the second rides on, so a payload pruned
                    // of fortunes fails on the cause instead of on a storage claim it never tested.
                    ok &= Check(sb, "WITNESS the bundled payload carries fortunes, the module whose Init writes storage",
                        HasModule(loader, "fortunes"));
                    ok &= Check(sb, "modules' storage writes land under the scratch root, not the TEMP root",
                        !HasModule(loader, "fortunes") || Directory.Exists(Path.Combine(scratch, "fortunes")));
                    // The settings seed keeps the three Fortunes loads in this flag from each warming the whole
                    // corpus on every core (RA-284; the mechanism is R-026's). Asserted on the module's STATE
                    // through the status seam its pane reads, which Init sets synchronously, not on the "smart
                    // picker constructed, warming" log line: that line is written from the picker's pool thread
                    // after construction, so its absence caught the seed's removal in one harness run and missed
                    // it in the next. WITNESS first: the seam exists, so an off reading is the module's answer.
                    string smartStatus = SmartStatusOf(FindModule(loader, "fortunes"));
                    sb.AppendLine("  fortunes smart status after Init: " + (smartStatus ?? "<seam not found>"));
                    ok &= Check(sb, "WITNESS the fortunes module exposes SmartStatusTextForDiagnostics, the status line its pane reads",
                        smartStatus != null);
                    ok &= Check(sb, "the loaded fortunes module reports smart picks off after Init (smartFortunes is seeded off)",
                        smartStatus != null && smartStatus.StartsWith("Smart picks are off", StringComparison.Ordinal));
                    ok &= Check(sb, "test module reports its id", HasModule(loader, "testmodule"));
                    ok &= Check(sb, "module contributed a tray item", host.TrayItems.Count >= 1);
                    ok &= Check(sb, "module contributed an options pane", host.OptionsPanes.Count >= 1);

                    host.RaiseCompanionPoked(new PokeInfo { Pet = new FakeCompanion(), PokeCount = 1 });
                    ok &= Check(sb, "CompanionPoked event reached the module (a line was spoken)", host.LastSayAll == "poked!");
                    // The routing the Say/SayAll split was added to make assertable, and then never asserted
                    // (F350): a poke is the poked pet's reaction, so the line goes to THAT pet, not to all of them.
                    ok &= Check(sb, "the poke was routed to the poked pet, not broadcast",
                        host.LastSayPet != null && host.LastSay == "poked!");

                    loader.ShutdownAll(s => sb.AppendLine("  " + s));
                    // After shutdown the module unsubscribes: a second poke must NOT re-trigger.
                    host.LastSayAll = null;
                    host.RaiseCompanionPoked(new PokeInfo { Pet = new FakeCompanion(), PokeCount = 2 });
                    ok &= Check(sb, "module unsubscribed on Shutdown", host.LastSayAll == null);
                }

                ok &= PaneAttribution(sb, modulesRoot);
                ok &= MinHostVersionGate(sb, modulesRoot, scratch);
                ok &= FailuresAreReported(sb);
                ok &= PetManagerPermissionGate(sb);
                ok &= PendingUpdateSwap(sb);
                ok &= WeeklyCheckSchedule(sb);
                ok &= UpdateScanVersionRule(sb);
                ok &= ScratchSweep(sb);
                ok &= SharedContextChannel(sb);
                ok &= ContextRaisedOnUiThread(sb);
                ok &= PendingRemovalRetry(sb);
                // ---- lane feature/followups2 ----
                ok &= UnprobeableMarkers(sb);
                ok &= EmptiedUnprobeableMarker(sb);
                ok &= InitRollback(sb, modulesRoot);
                ok &= RefusalKeyedByFolder(sb, modulesRoot, scratch);
                ok &= SelfTestFinder(sb);
                ok &= ResponderChainDeclines(sb);
                ok &= ModuleDllDiscovery(sb);
                // ---- lane feature/host-abi-180 ----
                ok &= SharedSignInStore(sb, dataRootScratch);
                ok &= StoreReviewFixes(sb);
                ok &= DeclarativeFlagList(sb);
                ok &= SignInStartQuestion(sb);
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                if (dataRootRedirected)
                {
                    try { Environment.SetEnvironmentVariable(AppPaths.DataRootOverrideEnvironmentVariable, previousDataRoot); } catch { }
                }
                string releaseDetail;
                if (!SelfTestScratch.TryRelease(scratch, out releaseDetail))
                    sb.AppendLine("NOTE: scratch left for the next sweep (" + releaseDetail + ")");
                if (!SelfTestScratch.TryRelease(dataRootScratch, out releaseDetail))
                    sb.AppendLine("NOTE: data-root scratch left for the next sweep (" + releaseDetail + ")");
            }
            return Finish(sb, ok);
        }

        /// <summary>Case- and separator-insensitive path equality; shared with the two Fortunes self-tests, which
        /// isolate their data root the same way (RA-283, RA-285).</summary>
        internal static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// A removal whose folder is LOCKED stays marked, is reported back for the loader to skip, and goes on
        /// a later launch once the lock is gone (F352). The marker used to be deleted whatever happened, so the
        /// uninstall was lost and the half-deleted folder loaded again. Driven on throwaway roots with a held
        /// file handle standing in for the sibling instance or indexer that holds the DLL.
        /// </summary>
        private static bool PendingRemovalRetry(StringBuilder sb)
        {
            string root = SelfTestScratch.Create("module-removal");
            bool ok = true;
            FileStream held = null;
            try
            {
                string modulesRoot = Path.Combine(root, "modules");
                string dataRoot = Path.Combine(root, "data", "modules");
                string marker = Path.Combine(root, "pending-module-removals.txt");
                string locked = Path.Combine(modulesRoot, "lockedmod");
                string free = Path.Combine(modulesRoot, "freemod");
                Directory.CreateDirectory(locked);
                Directory.CreateDirectory(free);
                File.WriteAllText(Path.Combine(locked, "Locked.dll"), "not an assembly");
                File.WriteAllText(Path.Combine(free, "Free.dll"), "not an assembly");
                Directory.CreateDirectory(Path.Combine(dataRoot, "lockedmod"));
                File.WriteAllText(Path.Combine(dataRoot, "lockedmod", "settings.json"), "kept until the module goes");
                Directory.CreateDirectory(Path.Combine(dataRoot, "freemod"));
                PendingModuleRemovals.MarkForRemoval("lockedmod", marker);
                PendingModuleRemovals.MarkForRemoval("freemod", marker);
                held = new FileStream(Path.Combine(locked, "Locked.dll"), FileMode.Open, FileAccess.Read, FileShare.None);

                // The staging folder's copies of an uninstalled module go with it (RA-295). A .replaced that
                // Swap's swallowed post-swap delete stranded outlived the uninstall on every launch, because
                // SweepStrands keeps a .replaced whose install folder is gone. An unrelated id's copy stays.
                string stagingRoot = Path.Combine(root, "module-staging");
                string freeReplaced = PendingModuleUpdates.ReplacedDirectory("freemod", stagingRoot);
                string freeStaged = PendingModuleUpdates.StagedDirectory("freemod", stagingRoot);
                string otherReplaced = PendingModuleUpdates.ReplacedDirectory("othermod", stagingRoot);
                Directory.CreateDirectory(freeReplaced);
                File.WriteAllText(Path.Combine(freeReplaced, "Free.dll"), "previous copy");
                Directory.CreateDirectory(freeStaged);
                File.WriteAllText(Path.Combine(freeStaged, "Free.dll"), "half-unpacked");
                Directory.CreateDirectory(otherReplaced);
                File.WriteAllText(Path.Combine(otherReplaced, "Other.dll"), "somebody else's previous copy");

                IReadOnlyList<string> unfinished = PendingModuleRemovals.ProcessPending(
                    modulesRoot, marker, dataRoot, stagingRoot, s => sb.AppendLine("  " + s));
                ok &= Check(sb, "removal: the unlocked module is removed, install folder and data folder both",
                    !Directory.Exists(free) && !Directory.Exists(Path.Combine(dataRoot, "freemod")));
                ok &= Check(sb, "removal: the uninstalled module's .replaced and .staged copies leave the staging folder with it",
                    !Directory.Exists(freeReplaced) && !Directory.Exists(freeStaged));
                ok &= Check(sb, "removal: WITNESS an unrelated module's previous copy in the staging folder is left alone",
                    Directory.Exists(otherReplaced));
                ok &= Check(sb, "removal: the locked module is reported back as unfinished",
                    unfinished.Count == 1 && string.Equals(unfinished[0], "lockedmod", StringComparison.OrdinalIgnoreCase));
                ok &= Check(sb, "removal: the locked module stays marked, and only it",
                    File.Exists(marker) && File.ReadAllText(marker).Trim().Equals("lockedmod", StringComparison.OrdinalIgnoreCase));
                ok &= Check(sb, "removal: a locked install folder keeps the module's data folder, so its settings survive the retry",
                    File.Exists(Path.Combine(dataRoot, "lockedmod", "settings.json")));

                // The loader is told, and skips the folder with a reason instead of loading and locking what
                // is left of it. WITNESS: not told, the same folder is tried and fails for its own reason.
                using (var loader = new ModuleHost())
                {
                    int loaded = loader.LoadFrom(modulesRoot, new RecordingHost(), delegate { }, unfinished);
                    ModuleLoadFailure skipped = null;
                    foreach (ModuleLoadFailure f in loader.Failures)
                        if (string.Equals(f.Id, "lockedmod", StringComparison.OrdinalIgnoreCase)) skipped = f;
                    ok &= Check(sb, "removal: the loader does not load a folder whose removal is pending, and records why",
                        loaded == 0 && skipped != null && skipped.Reason.Contains("still being removed") && !skipped.NeedsNewerHost);
                }
                using (var loader = new ModuleHost())
                {
                    loader.LoadFrom(modulesRoot, new RecordingHost(), delegate { });
                    ModuleLoadFailure tried = loader.Failures.Count == 1 ? loader.Failures[0] : null;
                    ok &= Check(sb, "removal: WITNESS a loader NOT told about the removal tries the folder and fails for its own reason",
                        tried != null && !tried.Reason.Contains("still being removed"));
                }

                held.Dispose();
                held = null;
                unfinished = PendingModuleRemovals.ProcessPending(modulesRoot, marker, dataRoot, stagingRoot, s => sb.AppendLine("  " + s));
                ok &= Check(sb, "removal: once the lock is gone the retry removes it, data folder too, and clears the marker",
                    unfinished.Count == 0 && !Directory.Exists(locked) &&
                    !Directory.Exists(Path.Combine(dataRoot, "lockedmod")) && !File.Exists(marker));

                // A marker write that fails THROWS (RA-296): MarkForRemoval, Unmark and MarkForUpdate swallowed
                // it, so the Modules pane announced the uninstall or update, prompted for the restart, and the
                // user's one action vanished. A marker FILE marked read-only reads and cannot be written, on any
                // box; the pane's existing "Couldn't uninstall/update" handlers are what catch it. (This was a
                // directory at the marker's path until N-codex-review-01: a directory is an unreadable marker now,
                // which throws at the READ, before the write these lines are about is reached.)
                string unwritable = Path.Combine(root, "marker-that-is-read-only.txt");
                File.WriteAllLines(unwritable, new[] { "kept" });
                File.SetAttributes(unwritable, FileAttributes.ReadOnly);
                bool removalThrew = false, updateThrew = false, unmarkThrew = false;
                try
                {
                    try { PendingModuleRemovals.MarkForRemoval("doomed", unwritable); } catch (Exception) { removalThrew = true; }
                    try { PendingModuleUpdates.MarkForUpdate("doomed", unwritable); } catch (Exception) { updateThrew = true; }
                    // Unmark rewrites only when the id was present. This marker names "kept", so "stale" is absent and
                    // Unmark returns before any write: the throwing write is Unmark's WriteIds too, asserted through
                    // MarkForRemoval above, and this early return is the WITNESS that an id nobody marked costs nothing.
                    try { PendingModuleRemovals.Unmark("stale", unwritable); }
                    catch (Exception) { unmarkThrew = true; }
                }
                finally { File.SetAttributes(unwritable, FileAttributes.Normal); }
                ok &= Check(sb, "removal: a removal marker that cannot be written throws instead of reporting success",
                    removalThrew);
                ok &= Check(sb, "update: an update marker that cannot be written throws instead of reporting success",
                    updateThrew);
                ok &= Check(sb, "removal: WITNESS Unmark of an id no readable marker names is a quiet no-op", !unmarkThrew);
                ok &= Check(sb, "removal: WITNESS a writable marker path still records the id",
                    MarkThenRead("witness", Path.Combine(root, "writable-marker.txt")));

                // A reinstall or update of the same id forgets the pending removal, or removals -- which run
                // first on the next launch -- would delete what the user just put back.
                PendingModuleRemovals.MarkForRemoval("again", marker);
                PendingModuleRemovals.MarkForRemoval("other", marker);
                PendingModuleRemovals.Unmark("again", marker);
                ok &= Check(sb, "removal: Unmark forgets one id and keeps the rest",
                    File.Exists(marker) && File.ReadAllText(marker).Trim().Equals("other", StringComparison.OrdinalIgnoreCase));
                PendingModuleRemovals.Unmark("other", marker);
                ok &= Check(sb, "removal: Unmark of the last id removes the marker file", !File.Exists(marker));

                // AN UNREADABLE REMOVAL MARKER (N-host-runtime-01), the update marker's held-file shape in
                // PendingUpdateSwap, whose WITNESS proves on this box that such a hold refuses a read and lets a
                // write through. MarkForRemoval took a held marker for an empty one and rewrote it with the one new
                // id, dropping every uninstall marked before it. Each step below starts from the same two earlier
                // uninstalls, so one broken rule fails its own line and not the steps after it: a launch removes
                // nothing, rewrites nothing and does not throw, and IsMarked answers false; the mark throws after
                // its re-reads and the earlier ids survive; Unmark throws rather than guess whether the marker
                // names its id; a hold released at the first re-read loses nothing; readable, nothing is re-read.
                string removalHeld = Path.Combine(root, "pending-module-removals-write-held.txt");
                string[] earlierRemovals = { "first", "second" };
                string heldModule = Path.Combine(modulesRoot, "first");
                Directory.CreateDirectory(heldModule);
                File.WriteAllText(Path.Combine(heldModule, "First.dll"), "not an assembly");

                File.WriteAllLines(removalHeld, earlierRemovals);
                bool heldIsMarked = true, heldLaunchThrew = false;
                IReadOnlyList<string> heldLaunch = null;
                using (new FileStream(removalHeld, FileMode.Open, FileAccess.Read, FileShare.Write))
                {
                    heldIsMarked = PendingModuleRemovals.IsMarked("first", removalHeld);
                    try { heldLaunch = PendingModuleRemovals.ProcessPending(modulesRoot, removalHeld, dataRoot, stagingRoot, s => sb.AppendLine("  " + s)); }
                    catch (Exception) { heldLaunchThrew = true; }
                }
                ok &= Check(sb, "removal: a launch that cannot read the removal marker removes nothing, rewrites nothing and does not throw",
                    !heldLaunchThrew && heldLaunch != null && heldLaunch.Count == 0 && Directory.Exists(heldModule) &&
                    MarkerIdsForSelfTest(removalHeld) == "first,second");
                ok &= Check(sb, "removal: an unreadable removal marker answers not marked", !heldIsMarked);
                ok &= Check(sb, "removal: WITNESS the same removal marker readable answers marked for an id it names",
                    PendingModuleRemovals.IsMarked("first", removalHeld));

                File.WriteAllLines(removalHeld, earlierRemovals);
                bool removalHeldThrew = false;
                int removalReReads = 0;
                using (new FileStream(removalHeld, FileMode.Open, FileAccess.Read, FileShare.Write))
                {
                    try { PendingModuleRemovals.MarkForRemoval("third", removalHeld, delegate { removalReReads++; }); }
                    catch (IOException) { removalHeldThrew = true; }
                }
                ok &= Check(sb, "removal: marking onto a removal marker that cannot be read throws and keeps the uninstalls marked before it, marker=" + MarkerIdsForSelfTest(removalHeld),
                    removalHeldThrew && MarkerIdsForSelfTest(removalHeld) == "first,second");
                ok &= Check(sb, "removal: an unreadable removal marker is read again before the mark gives up, re-reads=" + removalReReads,
                    removalReReads == PendingModuleUpdates.MarkerReReads);

                File.WriteAllLines(removalHeld, earlierRemovals);
                bool unmarkHeldThrew = false;
                using (new FileStream(removalHeld, FileMode.Open, FileAccess.Read, FileShare.Write))
                {
                    try { PendingModuleRemovals.Unmark("first", removalHeld, delegate { }); }
                    catch (IOException) { unmarkHeldThrew = true; }
                }
                ok &= Check(sb, "removal: Unmark of a removal marker that cannot be read throws instead of leaving its uninstall in place unseen, marker=" + MarkerIdsForSelfTest(removalHeld),
                    unmarkHeldThrew && MarkerIdsForSelfTest(removalHeld) == "first,second");

                File.WriteAllLines(removalHeld, earlierRemovals);
                FileStream removalTransient = new FileStream(removalHeld, FileMode.Open, FileAccess.Read, FileShare.Write);
                int removalTransientReReads = 0;
                try
                {
                    PendingModuleRemovals.MarkForRemoval("third", removalHeld, delegate
                    {
                        removalTransientReReads++;
                        if (removalTransient != null) { removalTransient.Dispose(); removalTransient = null; }
                    });
                }
                catch (IOException) { }
                finally { if (removalTransient != null) removalTransient.Dispose(); }
                ok &= Check(sb, "removal: a hold on the removal marker that ends before a re-read loses nothing, marker=" + MarkerIdsForSelfTest(removalHeld),
                    removalTransientReReads == 1 && MarkerIdsForSelfTest(removalHeld) == "first,second,third");

                File.WriteAllLines(removalHeld, earlierRemovals);
                int removalReadableReReads = 0;
                PendingModuleRemovals.MarkForRemoval("fourth", removalHeld, delegate { removalReadableReReads++; });
                ok &= Check(sb, "removal: WITNESS a readable removal marker is marked on the first read, with no re-read",
                    removalReadableReReads == 0 && MarkerIdsForSelfTest(removalHeld) == "first,fourth,second");
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("EXC (pending removal retry): " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (held != null) { try { held.Dispose(); } catch { } }
                string releaseDetail;
                if (!SelfTestScratch.TryRelease(root, out releaseDetail))
                    sb.AppendLine("NOTE: scratch left for the next sweep (" + releaseDetail + ")");
            }
            return ok;
        }

        /// <summary>
        /// A MARKER THIS ACCOUNT CANNOT PROBE IS UNREADABLE, NOT ABSENT (N-codex-review-01, lane feature/followups2). Both
        /// markers' ReadIds began with File.Exists, which answers false for a file whose attributes are refused as well as
        /// for one that is not there, so such a marker read as "nothing pending". The shape is a real deny ACL on this
        /// process's own account, on a scratch folder it created and owns, inherited by the two markers in it and removed in
        /// finally: read-data and read-attributes refused, which defeats the probe (GetFileAttributesEx and the
        /// FindFirstFile it falls back on). Every open of such a file is refused, a write's included (CreateFile asks for
        /// the attributes too), so the defect shows where the code reads and does not write: Unmark returned quietly over
        /// an uninstall it could not see, a launch said nothing about the removal marker, and a launch swept a marked update
        /// payload past the age limit as abandoned. The WITNESS proves the deny defeats File.Exists on this box before any
        /// line relies on it; a deny that cannot be set is said as DEGRADED, and the axis is then not exercised.
        /// </summary>
        private static bool UnprobeableMarkers(StringBuilder sb)
        {
            string root = SelfTestScratch.Create("module-marker-unprobeable");
            bool ok = true;
            DirectoryInfo deniedInfo = null;
            System.Security.AccessControl.FileSystemAccessRule denyRule = null;
            try
            {
                string denied = Path.Combine(root, "denied");
                string modulesRoot = Path.Combine(root, "modules");
                string dataRoot = Path.Combine(root, "data", "modules");
                string stagingRoot = Path.Combine(root, "module-staging");
                Directory.CreateDirectory(denied);
                string removalMarker = Path.Combine(denied, "pending-module-removals.txt");
                string updateMarker = Path.Combine(denied, "pending-module-updates.txt");
                File.WriteAllLines(removalMarker, new[] { "first", "second" });
                File.WriteAllLines(updateMarker, new[] { "marked" });
                // A module the removal marker names, and an installed module whose verified update the update marker names,
                // staged longer ago than the age limit: what an "empty" update marker lets the strand sweep delete.
                string firstModule = Path.Combine(modulesRoot, "first");
                Directory.CreateDirectory(firstModule);
                File.WriteAllText(Path.Combine(firstModule, "First.dll"), "not an assembly");
                string markedModule = Path.Combine(modulesRoot, "marked");
                Directory.CreateDirectory(markedModule);
                File.WriteAllText(Path.Combine(markedModule, "Marked.dll"), "old");
                string markedPayload = PendingModuleUpdates.PrepareStagingDirectory("marked", stagingRoot);
                File.WriteAllText(Path.Combine(markedPayload, "Marked.dll"), "retrying");
                Directory.SetLastWriteTimeUtc(markedPayload, DateTime.UtcNow - PendingModuleUpdates.AbandonedStagingAge - TimeSpan.FromMinutes(5));

                bool aclApplied = false;
                try
                {
                    deniedInfo = new DirectoryInfo(denied);
                    System.Security.AccessControl.DirectorySecurity acl = FileSystemAclExtensions.GetAccessControl(deniedInfo);
                    denyRule = new System.Security.AccessControl.FileSystemAccessRule(
                        System.Security.Principal.WindowsIdentity.GetCurrent().User,
                        System.Security.AccessControl.FileSystemRights.ReadData | System.Security.AccessControl.FileSystemRights.ReadAttributes,
                        System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                        System.Security.AccessControl.PropagationFlags.None,
                        System.Security.AccessControl.AccessControlType.Deny);
                    acl.AddAccessRule(denyRule);
                    FileSystemAclExtensions.SetAccessControl(deniedInfo, acl);
                    aclApplied = true;
                }
                catch (Exception aclException)
                {
                    denyRule = null;
                    sb.AppendLine("NOTE: DEGRADED: this account could not be denied a scratch folder's read access (" +
                        aclException.GetType().Name + "), so a marker that cannot be probed was NOT exercised");
                }
                if (aclApplied)
                {
                    bool readRefused = false;
                    try { File.ReadAllLines(removalMarker); } catch (UnauthorizedAccessException) { readRefused = true; }
                    bool probeDefeated = !File.Exists(removalMarker) && !File.Exists(updateMarker) && readRefused;
                    bool unmarkThrew = false;
                    try { PendingModuleRemovals.Unmark("first", removalMarker, delegate { }); }
                    catch (IOException) { unmarkThrew = true; }
                    var removalLaunch = new List<string>();
                    IReadOnlyList<string> unfinished = PendingModuleRemovals.ProcessPending(
                        modulesRoot, removalMarker, dataRoot, stagingRoot, delegate (string line) { removalLaunch.Add(line); });
                    var updateLaunch = new List<string>();
                    PendingModuleUpdates.ProcessPending(modulesRoot, stagingRoot, updateMarker, delegate (string line) { updateLaunch.Add(line); });
                    bool payloadSurvived = Directory.Exists(markedPayload);

                    // The deny goes BEFORE the lines are judged, so the WITNESSes below read the markers as they are.
                    System.Security.AccessControl.DirectorySecurity lifted = FileSystemAclExtensions.GetAccessControl(deniedInfo);
                    lifted.RemoveAccessRule(denyRule);
                    FileSystemAclExtensions.SetAccessControl(deniedInfo, lifted);
                    denyRule = null;

                    ok &= Check(sb, "marker: WITNESS a marker in a folder that refuses this account its attributes is there, yet File.Exists answers false and a read is refused",
                        probeDefeated && File.Exists(removalMarker) && File.Exists(updateMarker));
                    ok &= Check(sb, "removal: Unmark of a removal marker this account cannot probe throws instead of answering that nothing is pending",
                        unmarkThrew);
                    ok &= Check(sb, "removal: a launch whose removal marker cannot be probed says it could not be read and removes nothing, said=" +
                        string.Join(" | ", removalLaunch),
                        unfinished.Count == 0 && Directory.Exists(firstModule) && removalLaunch.Count == 1 &&
                        removalLaunch[0].StartsWith("the removal marker could not be read", StringComparison.Ordinal));
                    ok &= Check(sb, "update: a launch whose update marker cannot be probed sweeps nothing, so a marked payload past the age limit survives, said=" +
                        string.Join(" | ", updateLaunch),
                        payloadSurvived && updateLaunch.Count == 1 &&
                        updateLaunch[0].StartsWith("the update marker could not be read", StringComparison.Ordinal));
                    ok &= Check(sb, "removal: WITNESS the removal marker still names both uninstalls once it can be read, marker=" + MarkerIdsForSelfTest(removalMarker),
                        MarkerIdsForSelfTest(removalMarker) == "first,second");
                    PendingModuleUpdates.ProcessPending(modulesRoot, stagingRoot, updateMarker, delegate { });
                    ok &= Check(sb, "update: WITNESS once the update marker can be read the same payload is applied, not swept",
                        File.ReadAllText(Path.Combine(markedModule, "Marked.dll")) == "retrying" && !File.Exists(updateMarker));
                }
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("EXC (unprobeable markers): " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (denyRule != null && deniedInfo != null)
                {
                    try
                    {
                        System.Security.AccessControl.DirectorySecurity restore = FileSystemAclExtensions.GetAccessControl(deniedInfo);
                        restore.RemoveAccessRule(denyRule);
                        FileSystemAclExtensions.SetAccessControl(deniedInfo, restore);
                    }
                    catch (Exception restoreFailure) { sb.AppendLine("NOTE: the scratch folder's deny could not be lifted (" + restoreFailure.GetType().Name + ")"); }
                }
                string releaseDetail;
                if (!SelfTestScratch.TryRelease(root, out releaseDetail))
                    sb.AppendLine("NOTE: scratch left for the next sweep (" + releaseDetail + ")");
            }
            return ok;
        }

        /// <summary>
        /// AN EMPTIED MARKER THIS ACCOUNT CANNOT PROBE IS DELETED OR REFUSED, NEVER LEFT IN SILENCE (N-followups2-01, the
        /// write half of N-codex-review-01). WriteIds deleted an emptied removal marker only after File.Exists, which answers
        /// false for a file whose attributes are refused, so the marker stayed, still naming the uninstalls just finished,
        /// and the call returned as if it had deleted it. The same deny as UnprobeableMarkers (read-data and read-attributes
        /// on a scratch folder this process owns, lifted in finally); the WITNESS proves the probe is defeated first. The
        /// delete itself may succeed or be refused on this box; either is an answer, and only the quiet return is wrong.
        /// </summary>
        private static bool EmptiedUnprobeableMarker(StringBuilder sb)
        {
            string root = SelfTestScratch.Create("module-marker-emptied");
            bool ok = true;
            DirectoryInfo deniedInfo = null;
            System.Security.AccessControl.FileSystemAccessRule denyRule = null;
            try
            {
                string denied = Path.Combine(root, "denied");
                Directory.CreateDirectory(denied);
                string removalMarker = Path.Combine(denied, "pending-module-removals.txt");
                File.WriteAllLines(removalMarker, new[] { "finished" });
                bool existedBefore = File.Exists(removalMarker);
                bool aclApplied = false;
                try
                {
                    deniedInfo = new DirectoryInfo(denied);
                    System.Security.AccessControl.DirectorySecurity acl = FileSystemAclExtensions.GetAccessControl(deniedInfo);
                    denyRule = new System.Security.AccessControl.FileSystemAccessRule(
                        System.Security.Principal.WindowsIdentity.GetCurrent().User,
                        System.Security.AccessControl.FileSystemRights.ReadData | System.Security.AccessControl.FileSystemRights.ReadAttributes,
                        System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                        System.Security.AccessControl.PropagationFlags.None,
                        System.Security.AccessControl.AccessControlType.Deny);
                    acl.AddAccessRule(denyRule);
                    FileSystemAclExtensions.SetAccessControl(deniedInfo, acl);
                    aclApplied = true;
                }
                catch (Exception aclException)
                {
                    denyRule = null;
                    sb.AppendLine("NOTE: DEGRADED: this account could not be denied a scratch folder's read access (" +
                        aclException.GetType().Name + "), so an emptied marker that cannot be probed was NOT exercised");
                }
                if (aclApplied)
                {
                    bool probeDefeated = !File.Exists(removalMarker);
                    string outcome;
                    try { PendingModuleRemovals.WriteIds(removalMarker, new string[0]); outcome = "returned"; }
                    catch (UnauthorizedAccessException) { outcome = "refused"; }
                    catch (IOException) { outcome = "refused"; }

                    System.Security.AccessControl.DirectorySecurity lifted = FileSystemAclExtensions.GetAccessControl(deniedInfo);
                    lifted.RemoveAccessRule(denyRule);
                    FileSystemAclExtensions.SetAccessControl(deniedInfo, lifted);
                    denyRule = null;
                    bool stillThere = File.Exists(removalMarker);

                    ok &= Check(sb, "marker: WITNESS an emptied removal marker in a folder that refuses this account its attributes is there, yet File.Exists answers false",
                        existedBefore && probeDefeated);
                    ok &= Check(sb, "removal: emptying a removal marker this account cannot probe deletes it or throws, never returns with it in place (" +
                        outcome + ", " + (stillThere ? "marker kept" : "marker gone") + ")",
                        (outcome == "returned" && !stillThere) || (outcome == "refused" && stillThere));
                }
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("EXC (emptied unprobeable marker): " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (denyRule != null && deniedInfo != null)
                {
                    try
                    {
                        System.Security.AccessControl.DirectorySecurity restore = FileSystemAclExtensions.GetAccessControl(deniedInfo);
                        restore.RemoveAccessRule(denyRule);
                        FileSystemAclExtensions.SetAccessControl(deniedInfo, restore);
                    }
                    catch (Exception restoreFailure) { sb.AppendLine("NOTE: the scratch folder's deny could not be lifted (" + restoreFailure.GetType().Name + ")"); }
                }
                string releaseDetail;
                if (!SelfTestScratch.TryRelease(root, out releaseDetail))
                    sb.AppendLine("NOTE: scratch left for the next sweep (" + releaseDetail + ")");
            }
            return ok;
        }

        /// <summary>
        /// A module whose Init throws AFTER contributing holds nothing in the host afterwards (F344): its tray
        /// items, pane and event subscriptions are rolled back, not left live behind a "failed to load" row.
        /// Against the REAL CompanionHost, because the ledger lives there; the bundled test module throws on
        /// a switch this probe sets for one load. The healthy load first is the witness that the zeros mean
        /// "rolled back" and not "never registered".
        /// </summary>
        private static bool InitRollback(StringBuilder sb, string modulesRoot)
        {
            string bundled = Path.Combine(modulesRoot, "testmodule");
            if (!Directory.Exists(bundled))
            {
                sb.AppendLine("SKIP: no bundled test module for the Init rollback");
                return true;
            }
            const string Switch = "DESKTOP_AI_COMPANION_TESTMODULE_THROW_IN_INIT";   // TestModule.ThrowInInitSwitch
            string root = SelfTestScratch.Create("module-rollback");
            string previous = Environment.GetEnvironmentVariable(Switch);
            bool ok = true;
            try
            {
                SelfTestScratch.CopyTree(bundled, Path.Combine(root, "testmodule"));

                var healthy = new CompanionHost(null);
                using (var loader = new ModuleHost())
                {
                    int loaded = loader.LoadFrom(root, healthy, delegate { });
                    ok &= Check(sb, "rollback: WITNESS a healthy Init contributes tray items, a pane and a subscription",
                        loaded == 1 && healthy.TrayItems.Count >= 1 && healthy.OptionsPanes.Count == 1 &&
                        healthy.LifecycleSubscriberCount >= 1);
                    // The test module subscribes to CompanionPoked and to CredentialChanged (the 2026-10-29 audit,
                    // A-host-04), so both are in the count the rollback check below reads as zero.
                    ok &= Check(sb, "audit-host: rollback: WITNESS a healthy Init's CredentialChanged subscription is counted (" +
                        healthy.LifecycleSubscriberCount + " subscribers)", healthy.LifecycleSubscriberCount >= 2);
                    loader.ShutdownAll(delegate { });
                }

                Environment.SetEnvironmentVariable(Switch, "1");
                var failed = new CompanionHost(null);
                using (var loader = new ModuleHost())
                {
                    int loaded = loader.LoadFrom(root, failed, delegate { });
                    ModuleLoadFailure failure = loader.Failures.Count == 1 ? loader.Failures[0] : null;
                    ok &= Check(sb, "rollback: an Init that throws after contributing is reported as a failure, not loaded",
                        loaded == 0 && failure != null && failure.Id == "testmodule" &&
                        failure.Reason.Contains("InvalidOperationException"));
                    ok &= Check(sb, "rollback: ...and the failed module holds no tray item, no pane and no event subscription in the host",
                        failed.TrayItems.Count == 0 && failed.OptionsPanes.Count == 0 && failed.LifecycleSubscriberCount == 0);
                    // The Init window closed properly after the rollback: a pane registered now has no owner.
                    var late = new OptionsPane { Title = "after the failed Init" };
                    failed.AddOptionsPane(late);
                    ok &= Check(sb, "rollback: ...and the Init window is closed, so a later pane is unowned",
                        failed.ModuleOwningPane(late) == null);
                }

                // The ledger also undoes RESPONDERS (RA-281). The checks above read tray items, panes and
                // subscriptions, so deleting the Registrations loop in RollBackModuleInit survived them: the
                // bundled test module registers no responder. Driven through the Init window directly, on the
                // real host, since that is where the ledger lives and what a module's Init would do.
                var probe = new CompanionHost(null);
                probe.BeginModuleInit("rollbackprobe");
                probe.RegisterCompanionPokeResponder("rollbackprobe", 0, delegate { return false; });
                probe.RegisterSpeechResponder("rollbackprobe", 0, delegate { return false; });
                ok &= Check(sb, "rollback: WITNESS a poke responder and a speech responder registered inside the Init window are live",
                    HasResponderModule(probe, "rollbackprobe") && probe.SpeechResponderCountForDiagnostics == 1);
                probe.RollBackModuleInit();
                ok &= Check(sb, "rollback: an Init that throws after registering responders holds none of them afterwards",
                    !HasResponderModule(probe, "rollbackprobe") && probe.SpeechResponderCountForDiagnostics == 0);
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("EXC (init rollback): " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { Environment.SetEnvironmentVariable(Switch, previous); } catch { }
                string releaseDetail;
                if (!SelfTestScratch.TryRelease(root, out releaseDetail))
                    sb.AppendLine("NOTE: scratch left for the next sweep (" + releaseDetail + ")");
            }
            return ok;
        }

        /// <summary>
        /// A MinHostVersion refusal is keyed by the FOLDER name, as every other failure is (F343). The Modules
        /// pane matches failures to the folders it enumerates, so a refusal filed under Info.Id vanished the
        /// moment the two differed and the row fell back to "restart to activate". The folder is renamed away
        /// from the module's declared id to make the two differ.
        /// </summary>
        private static bool RefusalKeyedByFolder(StringBuilder sb, string modulesRoot, string scratch)
        {
            string bundled = Path.Combine(modulesRoot, "testmodule");
            if (!Directory.Exists(bundled))
            {
                sb.AppendLine("SKIP: no bundled test module for the refusal key");
                return true;
            }
            string root = SelfTestScratch.Create("module-refusal");
            bool ok = true;
            try
            {
                SelfTestScratch.CopyTree(bundled, Path.Combine(root, "renamed-testmodule"));
                var tooOld = new RecordingHost { HostVersionValue = "0.0.1", StorageRoot = scratch };
                using (var loader = new ModuleHost())
                {
                    int loaded = loader.LoadFrom(root, tooOld, delegate { });
                    ModuleLoadFailure f = loader.Failures.Count == 1 ? loader.Failures[0] : null;
                    ok &= Check(sb, "refusal: a MinHostVersion refusal is keyed by the folder name the pane enumerates, not by Info.Id",
                        loaded == 0 && f != null && f.Id == "renamed-testmodule" && f.NeedsNewerHost);
                    ok &= Check(sb, "refusal: ...and the declared id still travels in the reason",
                        f != null && f.Reason.Contains("testmodule"));
                }
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("EXC (refusal key): " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                string releaseDetail;
                if (!SelfTestScratch.TryRelease(root, out releaseDetail))
                    sb.AppendLine("NOTE: scratch left for the next sweep (" + releaseDetail + ")");
            }
            return ok;
        }

        /// <summary>
        /// The self-test entry-point finder is as deterministic as its doc promises (F339). Two edges: a type
        /// carrying a second SelfTest overload used to hide the conventional one (GetMethod threw, the throw
        /// was swallowed into "none"), and a module type without one, in an assembly where two other module
        /// types have one, used to be handed whichever the runtime listed first.
        /// </summary>
        private static bool SelfTestFinder(StringBuilder sb)
        {
            bool ok = true;
            MethodInfo entry;
            string ambiguity;
            bool found = ModuleConventionSelfTest.TryFindSelfTest(typeof(OverloadedSelfTestModule), out entry, out ambiguity);
            ok &= Check(sb, "finder: an overload beside the conventional SelfTest(out string) no longer hides it",
                found && entry != null && entry.DeclaringType == typeof(OverloadedSelfTestModule) &&
                entry.GetParameters().Length == 1 && ambiguity == null);
            found = ModuleConventionSelfTest.TryFindSelfTest(typeof(BareModule), out entry, out ambiguity);
            ok &= Check(sb, "finder: two other module types with a SelfTest is reported as ambiguous, naming both, rather than handing over the first",
                !found && entry == null && ambiguity != null &&
                ambiguity.Contains("OverloadedSelfTestModule") && ambiguity.Contains("SiblingSelfTestModule"));
            return ok;
        }

        /// <summary>
        /// A responder that throws is treated as DECLINED, and the chain still reaches the next one (F328).
        /// The record of the throw goes to the diagnostic log, which a headless run cannot read back; what
        /// this pins is the semantics the record was added beside, against the real CompanionHost, in
        /// priority order (the drop chain does not shuffle).
        /// </summary>
        private static bool ResponderChainDeclines(StringBuilder sb)
        {
            var host = new CompanionHost(null);
            int throwerCalls = 0, survivorCalls = 0;
            bool handled = false;
            string escaped = null;
            using (host.RegisterCompanionDropResponder(10, delegate { throwerCalls++; throw new InvalidOperationException("self-test: responder threw"); }))
            using (host.RegisterCompanionDropResponder(5, delegate { survivorCalls++; return true; }))
            {
                // Caught here so a chain that lets the throw out reads as this assertion failing, not as the
                // whole self-test aborting with an EXC line that names nothing.
                try { handled = host.RaiseDropTick(null); }
                catch (Exception ex) { escaped = ex.GetType().Name; }
            }
            return Check(sb, "chain: a responder that throws is treated as declined, and the next in priority order is still offered the turn"
                             + (escaped != null ? " -- the throw escaped the chain as " + escaped : ""),
                escaped == null && handled && throwerCalls == 1 && survivorCalls == 1);
        }

        // F339 fixtures. Both SelfTest carriers implement IModule so the finder's stage two (other IModule
        // types in the assembly) sees exactly these two; nothing else in the host assembly implements IModule
        // with a public static SelfTest, so the ambiguity below names exactly this pair.
        private sealed class OverloadedSelfTestModule : IModule
        {
            public ModuleInfo Info { get { return new ModuleInfo { Id = "overloaded", Name = "Overloaded", Version = "1.0.0" }; } }
            public void Init(IHost host) { }
            public void Shutdown() { }
            public static bool SelfTest(out string detail) { detail = "exact"; return true; }
            public static bool SelfTest(out string detail, bool verbose) { detail = "overload"; return verbose; }
        }
        private sealed class SiblingSelfTestModule : IModule
        {
            public ModuleInfo Info { get { return new ModuleInfo { Id = "sibling", Name = "Sibling", Version = "1.0.0" }; } }
            public void Init(IHost host) { }
            public void Shutdown() { }
            public static bool SelfTest(out string detail) { detail = "sibling"; return true; }
        }
        private sealed class BareModule : IModule
        {
            public ModuleInfo Info { get { return new ModuleInfo { Id = "bare", Name = "Bare", Version = "1.0.0" }; } }
            public void Init(IHost host) { }
            public void Shutdown() { }
        }

        /// <summary>A loaded module reporting an id and version, for ModuleUpdateScan.FindUpdates. No SelfTest
        /// method, like BareModule, so the finder's ambiguity check above still sees exactly its pair.</summary>
        private sealed class UpdateScanModule : IModule
        {
            private readonly ModuleInfo _info;
            public UpdateScanModule(string id, string version) { _info = new ModuleInfo { Id = id, Name = id, Version = version }; }
            public ModuleInfo Info { get { return _info; } }
            public void Init(IHost host) { }
            public void Shutdown() { }
        }

        /// <summary>
        /// Which module contributed which options pane, through the REAL loader and a REAL CompanionHost.
        ///
        /// The RecordingHost used by every other wiring test above is not a CompanionHost, so it cannot
        /// exercise this at all: attribution happens inside CompanionHost.AddOptionsPane, and ModuleHost
        /// reaches it through an `as CompanionHost` cast that yields null for any other IHost. A test built
        /// on the stub would pass against a ModuleHost that never calls BeginModuleInit, which is the whole
        /// thing worth proving here.
        ///
        /// This is what lets PaneView narrow a RevealsPath containment test from the app-wide data root to
        /// the calling module's own storage directory.
        /// </summary>
        private static bool PaneAttribution(StringBuilder sb, string modulesRoot)
        {
            bool ok = true;
            if (!Directory.Exists(Path.Combine(modulesRoot, "testmodule")))
            {
                sb.AppendLine("SKIP: no bundled test module for pane attribution");
                return ok;
            }

            var real = new CompanionHost(null);
            using (var loader = new ModuleHost())
            {
                loader.LoadFrom(modulesRoot, real, delegate { });
                ok &= Check(sb, "a real host received at least one module pane", real.OptionsPanes.Count >= 1);
                var owners = new List<string>();
                foreach (OptionsPane p in real.OptionsPanes) owners.Add(real.ModuleOwningPane(p));
                ok &= Check(sb, "every pane registered during Init is attributed to some module",
                    real.OptionsPanes.Count >= 1 && !owners.Contains(null) && !owners.Contains(""));
                // The RIGHT module, not merely SOME module. A marker left set from a previous Init would
                // still be non-empty and would credit every later pane to whoever loaded first, so the
                // distinct-owner count is the assertion that catches it. Panes are NOT indexed by load
                // order here on purpose: the loader walks directories alphabetically, so testmodule is
                // not OptionsPanes[0] and asserting that it was is how the first draft of this failed.
                ok &= Check(sb, "...and each module owns its own pane rather than all of them sharing one",
                    new List<string>(new HashSet<string>(owners)).Count == owners.Count);
                ok &= Check(sb, "...including the bundled test module, whose pane is attributed to it",
                    owners.Contains("testmodule"));

                // WITNESS: outside an Init window there is no owner, and the host must say so rather than
                // guess. The Preferences pane the host builds itself is exactly this case, and it has to
                // keep the data-root-wide rule.
                var orphan = new OptionsPane { Title = "Host built" };
                real.AddOptionsPane(orphan);
                ok &= Check(sb, "WITNESS a pane registered outside any Init has no owner",
                    real.ModuleOwningPane(orphan) == null);
                ok &= Check(sb, "WITNESS an unknown pane object has no owner",
                    real.ModuleOwningPane(new OptionsPane { Title = "Never registered" }) == null);

                // A throwing subscriber costs only itself.
                //
                // The raise sites used to wrap the whole multicast invocation in one catch, so the first
                // handler that threw aborted the invocation list and every later subscriber was skipped,
                // silently, for that event and every later one on the same path. Four shipped modules
                // subscribe to CompanionSpawned; Fortunes.OnPetSpawned calls host.SayAll with no internal
                // guard, so a throw out of a bubble draw permanently cost Reminder its spawn handler.
                //
                // Asserted on HostShutdown because it is the one raise site that takes no arguments and
                // needs no live pet, so the check tests the ISOLATION and nothing else. Subscription order
                // is the point: the thrower is first, so a per-event catch cannot reach the second.
                bool secondRan = false;
                Action thrower = delegate { throw new InvalidOperationException("self-test: a bad module"); };
                Action survivor = delegate { secondRan = true; };
                real.HostShutdown += thrower;
                real.HostShutdown += survivor;
                real.RaiseShutdown();
                real.HostShutdown -= thrower;
                real.HostShutdown -= survivor;
                ok &= Check(sb, "a handler that throws does not starve the subscribers after it", secondRan);

                loader.ShutdownAll(delegate { });
            }
            return ok;
        }

        /// <summary>
        /// The deferred module-update swap (<see cref="PendingModuleUpdates"/>), on throwaway directories. It
        /// is the only place an update can go wrong destructively, so all four outcomes are asserted: a staged
        /// payload replaces the installed one, the module's DATA survives (the reason updates exist at all),
        /// an id whose install folder is gone is discarded rather than resurrected, and an empty staging folder
        /// leaves the installed copy alone. Then the strand sweep, the unreadable marker, and a marker naming
        /// several ids at once (what Update all writes) with one of their swaps failing. Everything (install
        /// root, staging root, marker file) is a throwaway temp path, so the test never reads or writes the real
        /// install or data directories.
        /// </summary>
        private static bool PendingUpdateSwap(StringBuilder sb)
        {
            string root = SelfTestScratch.Create("module-update");
            bool ok = true;
            try
            {
                string modulesRoot = Path.Combine(root, "modules");
                string stagingRoot = Path.Combine(root, "module-staging");
                string marker = Path.Combine(root, "pending-module-updates.txt");
                Directory.CreateDirectory(root);

                string installed = Path.Combine(modulesRoot, "demo");
                Directory.CreateDirectory(installed);
                File.WriteAllText(Path.Combine(installed, "Demo.dll"), "old");
                // Mirrors CompanionHost.ModuleDataDirectoryPath's layout (<data root>\modules\<id>): a module's data
                // lives OUTSIDE its install folder, and an update -- unlike an uninstall -- must leave it alone.
                string moduleData = Path.Combine(root, "data", "modules", "demo");
                Directory.CreateDirectory(moduleData);
                File.WriteAllText(Path.Combine(moduleData, "settings.json"), "keep me");

                string staged = PendingModuleUpdates.PrepareStagingDirectory("demo", stagingRoot);
                File.WriteAllText(Path.Combine(staged, "Demo.dll"), "new");
                PendingModuleUpdates.MarkForUpdate("demo", marker);
                PendingModuleUpdates.ProcessPending(modulesRoot, stagingRoot, marker, s => sb.AppendLine("  " + s));

                ok &= Check(sb, "update: staged payload replaced the installed module",
                    File.ReadAllText(Path.Combine(installed, "Demo.dll")) == "new");
                ok &= Check(sb, "update: the module's data directory survived",
                    File.Exists(Path.Combine(moduleData, "settings.json")));
                ok &= Check(sb, "update: marker cleared so the swap runs once", !File.Exists(marker));

                // An update for something no longer installed must be discarded, not resurrected -- and its
                // payload must GO (F353): it used to stay, bounded to one per id and collected by nothing.
                string gone = PendingModuleUpdates.PrepareStagingDirectory("removed", stagingRoot);
                File.WriteAllText(Path.Combine(gone, "Removed.dll"), "new");
                PendingModuleUpdates.MarkForUpdate("removed", marker);
                PendingModuleUpdates.ProcessPending(modulesRoot, stagingRoot, marker, s => sb.AppendLine("  " + s));
                ok &= Check(sb, "update: an uninstalled module is not resurrected",
                    !Directory.Exists(Path.Combine(modulesRoot, "removed")));
                ok &= Check(sb, "update: a discarded payload's staging folder is removed with it", !Directory.Exists(gone));

                // An empty staging folder must leave the installed copy intact, and go.
                string empty = PendingModuleUpdates.PrepareStagingDirectory("demo", stagingRoot);
                PendingModuleUpdates.MarkForUpdate("demo", marker);
                PendingModuleUpdates.ProcessPending(modulesRoot, stagingRoot, marker, s => sb.AppendLine("  " + s));
                ok &= Check(sb, "update: an empty staged payload keeps the installed module",
                    File.Exists(Path.Combine(installed, "Demo.dll")) &&
                    File.ReadAllText(Path.Combine(installed, "Demo.dll")) == "new");
                ok &= Check(sb, "update: an empty payload's staging folder is removed too", !Directory.Exists(empty));

                // Strands nothing marked will ever visit again (F353): a previous copy beside a module that
                // is installed (Swap's post-swap delete is swallowed), and an unpack that died with its
                // process before MarkForUpdate. Both are swept on a launch with NO marker at all. A fresh
                // unmarked folder survives (a sibling process could be writing it), and a previous copy
                // whose module is NOT installed survives (it may be the only copy left).
                string oldCopy = Path.Combine(stagingRoot, "demo.replaced");
                Directory.CreateDirectory(oldCopy);
                File.WriteAllText(Path.Combine(oldCopy, "Demo.dll"), "older");
                string stale = Path.Combine(stagingRoot, "stale.staged");
                Directory.CreateDirectory(stale);
                File.WriteAllText(Path.Combine(stale, "Stale.dll"), "half");
                Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow - PendingModuleUpdates.AbandonedStagingAge - TimeSpan.FromMinutes(5));
                string fresh = Path.Combine(stagingRoot, "fresh.staged");
                Directory.CreateDirectory(fresh);
                File.WriteAllText(Path.Combine(fresh, "Fresh.dll"), "half");
                string orphanCopy = Path.Combine(stagingRoot, "orphan.replaced");
                Directory.CreateDirectory(orphanCopy);
                File.WriteAllText(Path.Combine(orphanCopy, "Orphan.dll"), "only copy");
                ok &= Check(sb, "update: WITNESS no marker is pending before the sweep runs", !File.Exists(marker));
                PendingModuleUpdates.ProcessPending(modulesRoot, stagingRoot, marker, s => sb.AppendLine("  " + s));
                ok &= Check(sb, "update: a previous copy left beside an installed module is removed on the next launch",
                    !Directory.Exists(oldCopy));
                ok &= Check(sb, "update: an abandoned, unmarked staging folder older than the age limit is swept",
                    !Directory.Exists(stale));
                ok &= Check(sb, "update: WITNESS a fresh unmarked staging folder survives the sweep", Directory.Exists(fresh));
                ok &= Check(sb, "update: WITNESS a previous copy whose module is not installed is left alone", Directory.Exists(orphanCopy));
                ok &= Check(sb, "update: WITNESS the installed module is untouched by the sweep",
                    File.ReadAllText(Path.Combine(installed, "Demo.dll")) == "new");

                // An UNREADABLE marker is not an empty one (RA-297). With both answered as "nothing marked", a
                // launch on which the marker read threw swept a marked payload older than the age limit as
                // abandoned, losing a verified download whose swap had merely been retrying. The shape is a
                // marker FILE another process holds open (an antivirus scan, a sync client): held here with
                // FileShare.None, so File.ReadAllLines throws a sharing violation on every box. (A directory
                // at the marker's path is NOT this case: File.Exists answers false for a directory, so it reads
                // as no marker at all, which the first draft of this check learned the hard way.) Once the
                // hold is released the same marker reads, names an id with no payload, and the sweep runs:
                // the WITNESS that the stale folder IS swept on a launch whose marker is readable.
                string heldMarker = Path.Combine(root, "pending-module-updates-held.txt");
                File.WriteAllText(heldMarker, "somethingelse\n", new UTF8Encoding(false));
                string marked = Path.Combine(stagingRoot, "marked.staged");
                Directory.CreateDirectory(marked);
                File.WriteAllText(Path.Combine(marked, "Marked.dll"), "retrying");
                Directory.SetLastWriteTimeUtc(marked, DateTime.UtcNow - PendingModuleUpdates.AbandonedStagingAge - TimeSpan.FromMinutes(5));
                using (new FileStream(heldMarker, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    PendingModuleUpdates.ProcessPending(modulesRoot, stagingRoot, heldMarker, s => sb.AppendLine("  " + s));
                }
                ok &= Check(sb, "update: a marker that cannot be read sweeps nothing, so a marked payload past the age limit survives the launch",
                    Directory.Exists(marked) && File.Exists(heldMarker));
                PendingModuleUpdates.ProcessPending(modulesRoot, stagingRoot, heldMarker, s => sb.AppendLine("  " + s));
                ok &= Check(sb, "update: WITNESS the same stale folder is swept once the marker is readable",
                    !Directory.Exists(marked));

                // SEVERAL IDS AT ONCE, which the Modules pane's Update all writes (feature/modules-update-all):
                // every id the marker names is swapped at the next start, not only the first, and one whose swap
                // fails (its folder held, here by an open file, the way an indexer or a sibling instance holds a
                // DLL) stays marked alone with its payload and goes on the start after. The marker has always been
                // a set and the loop has always walked it, but every case above marks one id per launch. Marked
                // locked-first, so an applier that stops after one id keeps the locked one marked anyway and
                // fails only the line that says the others were applied.
                string[] several = { "beta", "alpha", "gamma" };
                string severalMarker = Path.Combine(root, "pending-module-updates-several.txt");
                foreach (string id in several)
                {
                    string folder = Path.Combine(modulesRoot, id);
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(Path.Combine(folder, id + ".dll"), "old " + id);
                    string payload = PendingModuleUpdates.PrepareStagingDirectory(id, stagingRoot);
                    File.WriteAllText(Path.Combine(payload, id + ".dll"), "new " + id);
                    PendingModuleUpdates.MarkForUpdate(id, severalMarker);
                }
                // WHICH HALF, if the two lines below ever fail again (N-tray-questions-02: on 2026-10-15 both failed once
                // and passed on a re-run). A marker that lost the earlier ids to a transient read (N-modules-update-all-03,
                // on which MarkForUpdate now fails closed, asserted below) and a swap a scanner held fail those two lines
                // alike; this line fails only for the first, and the report's "could not update" lines name the second.
                string severalMarked = MarkerIdsForSelfTest(severalMarker);
                ok &= Check(sb, "update: WITNESS three ids marked back to back are all in the marker before the start, marker=" + severalMarked,
                    severalMarked == "alpha,beta,gamma");
                using (new FileStream(Path.Combine(modulesRoot, "beta", "beta.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    PendingModuleUpdates.ProcessPending(modulesRoot, stagingRoot, severalMarker, s => sb.AppendLine("  " + s));
                }
                ok &= Check(sb, "update: several ids marked at once (Update all) are all applied at the next start, not only the first",
                    File.ReadAllText(Path.Combine(modulesRoot, "alpha", "alpha.dll")) == "new alpha" &&
                    File.ReadAllText(Path.Combine(modulesRoot, "gamma", "gamma.dll")) == "new gamma");
                bool lockedStayedMarked = File.Exists(severalMarker) &&
                    File.ReadAllText(severalMarker).Trim().Equals("beta", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(Path.Combine(PendingModuleUpdates.StagedDirectory("beta", stagingRoot), "beta.dll")) &&
                    File.ReadAllText(Path.Combine(modulesRoot, "beta", "beta.dll")) == "old beta";
                PendingModuleUpdates.ProcessPending(modulesRoot, stagingRoot, severalMarker, s => sb.AppendLine("  " + s));
                ok &= Check(sb, "update: of several, the one whose swap fails stays marked alone with its payload, and the next start applies it and clears the marker",
                    lockedStayedMarked &&
                    File.ReadAllText(Path.Combine(modulesRoot, "beta", "beta.dll")) == "new beta" && !File.Exists(severalMarker));

                // THE WRITE SIDE OF AN UNREADABLE MARKER (N-modules-update-all-03). MarkForUpdate took a marker it
                // could not read for an empty one and overwrote it with the one new id, so a hold between two of
                // Update all's back-to-back writes dropped the ids marked before it. The shape is a marker another
                // process has open for READING while sharing only WRITE (a scanner reading the file just written):
                // a read is refused and a write is let through, which is exactly what made the overwrite possible.
                // The WITNESS proves that shape on this box first, without writing a byte. Then: held throughout,
                // the call throws after its three re-reads and the file still names the earlier ids; released at
                // the first re-read, nothing is lost; readable, nothing is read twice.
                string writeHeld = Path.Combine(root, "pending-module-updates-write-held.txt");
                PendingModuleUpdates.MarkForUpdate("first", writeHeld);
                PendingModuleUpdates.MarkForUpdate("second", writeHeld);
                bool heldRefusesRead = false, heldAllowsWrite = false, heldThrew = false;
                int heldReReads = 0;
                using (new FileStream(writeHeld, FileMode.Open, FileAccess.Read, FileShare.Write))
                {
                    try { File.ReadAllLines(writeHeld); } catch (IOException) { heldRefusesRead = true; }
                    try { using (new FileStream(writeHeld, FileMode.Open, FileAccess.Write, FileShare.Read)) heldAllowsWrite = true; }
                    catch (IOException) { }
                    try { PendingModuleUpdates.MarkForUpdate("third", writeHeld, delegate { heldReReads++; }); }
                    catch (IOException) { heldThrew = true; }
                }
                ok &= Check(sb, "update: WITNESS a marker held for reading with write shared refuses a read and lets a write through",
                    heldRefusesRead && heldAllowsWrite);
                ok &= Check(sb, "update: marking onto a marker that cannot be read throws and keeps the ids marked before it",
                    heldThrew && MarkerIdsForSelfTest(writeHeld) == "first,second");
                ok &= Check(sb, "update: an unreadable marker is read again before the mark gives up, re-reads=" + heldReReads,
                    heldReReads == PendingModuleUpdates.MarkerReReads);
                FileStream transientHold = new FileStream(writeHeld, FileMode.Open, FileAccess.Read, FileShare.Write);
                int transientReReads = 0;
                try
                {
                    PendingModuleUpdates.MarkForUpdate("third", writeHeld, delegate
                    {
                        transientReReads++;
                        if (transientHold != null) { transientHold.Dispose(); transientHold = null; }
                    });
                }
                catch (IOException) { }
                finally { if (transientHold != null) transientHold.Dispose(); }
                ok &= Check(sb, "update: a hold that ends before a re-read loses nothing, the earlier ids and the new one all marked",
                    transientReReads == 1 && MarkerIdsForSelfTest(writeHeld) == "first,second,third");
                int readableReReads = 0;
                PendingModuleUpdates.MarkForUpdate("fourth", writeHeld, delegate { readableReReads++; });
                ok &= Check(sb, "update: WITNESS a readable marker is marked on the first read, with no re-read",
                    readableReReads == 0 && MarkerIdsForSelfTest(writeHeld) == "first,fourth,second,third");

                // IsStaged, which the Modules pane reads so a staged update shows as staged instead of being
                // offered again (feature/modules-update-all): it needs the marker AND the payload, because a
                // marked id with no payload applies nothing, and a payload nothing marks is swept as abandoned.
                string stagedMarker = Path.Combine(root, "pending-module-updates-staged.txt");
                string bothPayload = PendingModuleUpdates.PrepareStagingDirectory("both", stagingRoot);
                File.WriteAllText(Path.Combine(bothPayload, "Both.dll"), "staged");
                PendingModuleUpdates.MarkForUpdate("both", stagedMarker);
                PendingModuleUpdates.MarkForUpdate("markedonly", stagedMarker);
                string unmarkedPayload = PendingModuleUpdates.PrepareStagingDirectory("payloadonly", stagingRoot);
                File.WriteAllText(Path.Combine(unmarkedPayload, "PayloadOnly.dll"), "unmarked");
                ok &= Check(sb, "update: WITNESS IsStaged answers true for a marked id whose payload is in the staging folder",
                    PendingModuleUpdates.IsStaged("both", stagedMarker, stagingRoot));
                ok &= Check(sb, "update: IsStaged answers false for a marked id with no payload, and for a payload nothing marks",
                    !PendingModuleUpdates.IsStaged("markedonly", stagedMarker, stagingRoot) &&
                    !PendingModuleUpdates.IsStaged("payloadonly", stagedMarker, stagingRoot));
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("EXC (pending update swap): " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                string releaseDetail;
                if (!SelfTestScratch.TryRelease(root, out releaseDetail))
                    sb.AppendLine("NOTE: scratch left for the next sweep (" + releaseDetail + ")");
            }
            return ok;
        }

        /// <summary>The ids a marker names, lower-cased and sorted, comma-joined; "" for no marker and "?" for one
        /// that cannot be read, so a comparison names what is there whatever order a HashSet wrote it in.</summary>
        private static string MarkerIdsForSelfTest(string markerPath)
        {
            try
            {
                if (!File.Exists(markerPath)) return "";
                var ids = new List<string>();
                foreach (string line in File.ReadAllLines(markerPath))
                    if (!string.IsNullOrWhiteSpace(line)) ids.Add(line.Trim().ToLowerInvariant());
                ids.Sort(StringComparer.Ordinal);
                return string.Join(",", ids);
            }
            catch (IOException) { return "?"; }
        }

        /// <summary>
        /// The pet-manager permission gate. A module that did not declare ModulePermissions.Companions must get a
        /// service that refuses everything rather than an exception or a null, so a module written against a
        /// permission it forgot to declare fails legibly instead of crashing. Asserted against the REAL
        /// CompanionHost (with no StartUp, which is also the "host not running" degradation path), not a fake.
        /// </summary>
        /// <summary>
        /// A module folder that cannot run must be REPORTED, not silently skipped. Before this the Modules pane
        /// had no way to tell a broken module from one waiting on a restart, so it offered "installed — restart
        /// to activate" forever and Uninstall — which deletes the module's settings and keys — was the only exit.
        ///
        /// Driven with real folders through the real loader rather than a stub, because the value is precisely
        /// that every early-return path in LoadFrom records something.
        /// </summary>
        private static bool FailuresAreReported(StringBuilder sb)
        {
            string root = SelfTestScratch.Create("modulefail");
            bool ok = true;
            try
            {
                // A folder with nothing in it: the "no module DLL" path.
                Directory.CreateDirectory(Path.Combine(root, "emptymodule"));
                // A folder holding a DLL that implements nothing: the "no IModule type" path. The contract
                // assembly itself is a real, loadable DLL that contains no IModule implementation.
                string junk = Path.Combine(root, "junkmodule");
                Directory.CreateDirectory(junk);
                File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "DesktopAICompanion.Contracts.dll"),
                    Path.Combine(junk, "junkmodule.dll"), true);
                // A folder whose DLL is not a managed assembly at all: the exception path.
                string corrupt = Path.Combine(root, "corruptmodule");
                Directory.CreateDirectory(corrupt);
                File.WriteAllText(Path.Combine(corrupt, "corruptmodule.dll"), "this is not an assembly");

                var host = new RecordingHost();
                using (var loader = new ModuleHost())
                {
                    int loaded = loader.LoadFrom(root, host, delegate { });
                    ok &= Check(sb, "failures: none of the three broken folders loaded", loaded == 0);

                    IReadOnlyList<ModuleLoadFailure> failures = loader.Failures;
                    ok &= Check(sb, "failures: all three are reported, not silently skipped", failures.Count == 3);

                    foreach (string expected in new[] { "emptymodule", "junkmodule", "corruptmodule" })
                    {
                        ModuleLoadFailure found = null;
                        foreach (ModuleLoadFailure f in failures)
                            if (string.Equals(f.Id, expected, StringComparison.OrdinalIgnoreCase)) found = f;
                        ok &= Check(sb, "failures: '" + expected + "' is reported with a reason",
                            found != null && !string.IsNullOrWhiteSpace(found.Reason));
                        // The reason reaches the user, so it must not be blamed on the wrong thing.
                        ok &= Check(sb, "failures: '" + expected + "' is not mislabelled as needing a newer app",
                            found != null && !found.NeedsNewerHost);
                    }

                    // The positive half, "a healthy load reports none", lives in Run() against the loader
                    // that actually loaded the bundled modules. It sat here on a fresh `new ModuleHost()`
                    // that never called LoadFrom, which no mutation of LoadFrom could fail (F347).
                }
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                string releaseDetail;
                if (!SelfTestScratch.TryRelease(root, out releaseDetail))
                    sb.AppendLine("NOTE: scratch left for the next sweep (" + releaseDetail + ")");
            }
            return ok;
        }

        /// <summary>
        /// The shared-context channel, both halves, against the REAL CompanionHost.
        ///
        /// Written 2026-09-17 because the PUSH half had never executed. `ContextChanged` is raised by
        /// exactly one publisher (Reminder) and subscribed by nothing in the repo -- the one plausible
        /// consumer, Remembrance, reads `ReadContext` at the moment it uses the value instead. That is
        /// the right design and it is now recorded as a decision in docs/DESIGN-REGISTER.md, but it
        /// left an ABI event that had never been delivered to anyone.
        ///
        /// The last two assertions are the ones that make that decision CHECKABLE rather than a
        /// paragraph: the host retains every value, so a reader arriving late still gets it, while a
        /// SUBSCRIBER arriving late gets nothing at all. If those two ever stop being true, the reason
        /// Remembrance reads at use is gone and the register entry is wrong.
        /// </summary>
        private static bool SharedContextChannel(StringBuilder sb)
        {
            var host = new CompanionHost(null);
            var delivered = new System.Collections.Generic.List<string>();
            Action<string> subscriber = key => delivered.Add(key);
            host.ContextChanged += subscriber;

            host.PublishContext("a-module", "meeting", "{\"title\":\"standup\"}");
            bool ok = Check(sb, "context: publishing RAISES ContextChanged with the key",
                delivered.Count == 1 && delivered[0] == "meeting");
            ok &= Check(sb, "context: ...and the value reads back",
                host.ReadContext("meeting") == "{\"title\":\"standup\"}");

            // The raise is documented as best-effort: "a throwing subscriber must not take down the
            // publisher's tick". A module that throws in a handler would otherwise break Reminder.
            Action<string> thrower = key => { throw new InvalidOperationException("subscriber blew up"); };
            host.ContextChanged += thrower;
            bool publishSurvived = true;
            try { host.PublishContext("a-module", "meeting", "{}"); }
            catch { publishSurvived = false; }
            ok &= Check(sb, "context: a THROWING subscriber does not take down the publisher",
                publishSurvived);
            // ...and does not STARVE the subscribers after it (RA-280). One try/catch around the multicast
            // invoke satisfied the line above while the first thrower aborted every later handler in the
            // invocation list; the thrower here is already registered, so a subscriber added now sits after it.
            var afterThrower = new System.Collections.Generic.List<string>();
            Action<string> after = key => afterThrower.Add(key);
            host.ContextChanged += after;
            try { host.PublishContext("a-module", "meeting", "{}"); } catch { }
            ok &= Check(sb, "context: a subscriber registered after a throwing one is still delivered",
                afterThrower.Count == 1 && afterThrower[0] == "meeting");
            host.ContextChanged -= after;
            host.ContextChanged -= thrower;
            host.ContextChanged -= subscriber;

            ok &= Check(sb, "context: an empty key publishes nothing and reads back empty",
                host.ReadContext("") == "" && host.ReadContext(null) == "");

            // The retain guarantee, and its mirror. These two together are why reading at use beats
            // subscribing for a consumer that only needs the CURRENT value.
            int deliveredBefore = delivered.Count;
            host.PublishContext("a-module", "late", "42");
            var lateSubscriber = new System.Collections.Generic.List<string>();
            Action<string> late = key => lateSubscriber.Add(key);
            host.ContextChanged += late;
            ok &= Check(sb, "context: WITNESS a reader arriving after the publish still gets the value",
                host.ReadContext("late") == "42");
            ok &= Check(sb, "context: WITNESS a SUBSCRIBER arriving after the publish gets nothing",
                lateSubscriber.Count == 0 && delivered.Count == deliveredBefore);
            host.ContextChanged -= late;
            return ok;
        }

        /// <summary>
        /// ContextChanged fires on the UI thread whoever publishes (N-aibrain-standdown-01), as the ABI promises.
        /// The REAL CompanionHost is built under a pumped context installed on this thread, so it captures this
        /// thread as its UI thread and <see cref="PumpedContext"/> as the context to post to; a publish from a pool
        /// thread must then raise nothing on the pool thread, and the handler runs here when the queue is pumped.
        /// The WITNESS is the other half of the rule: a publish ON the UI thread is raised inline, before
        /// PublishContext returns, with nothing posted, so a module tick's publish still reaches its subscribers
        /// within that tick.
        /// </summary>
        private static bool ContextRaisedOnUiThread(StringBuilder sb)
        {
            System.Threading.SynchronizationContext previous = System.Threading.SynchronizationContext.Current;
            var ui = new PumpedContext();
            System.Threading.SynchronizationContext.SetSynchronizationContext(ui);
            bool ok = true;
            try
            {
                var host = new CompanionHost(null);
                int uiThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                var raisedOn = new List<int>();
                Action<string> subscriber = key => { lock (raisedOn) raisedOn.Add(System.Threading.Thread.CurrentThread.ManagedThreadId); };
                host.ContextChanged += subscriber;

                host.PublishContext("a-module", "on-ui", "1");
                int raisedInline;
                lock (raisedOn) raisedInline = raisedOn.Count;
                ok &= Check(sb, "context: WITNESS a publish on the UI thread is raised inline, with nothing posted",
                    raisedInline == 1 && raisedOn[0] == uiThread && ui.Pending == 0);

                int poolThread = -1;
                bool onPool = false;
                using (var published = new System.Threading.ManualResetEventSlim(false))
                {
                    System.Threading.ThreadPool.QueueUserWorkItem(delegate
                    {
                        try
                        {
                            poolThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                            onPool = System.Threading.Thread.CurrentThread.IsThreadPoolThread;
                            host.PublishContext("a-module", "from-pool", "2");
                        }
                        finally { published.Set(); }
                    });
                    bool returned = published.Wait(TimeSpan.FromSeconds(10));
                    int raisedBeforePump;
                    lock (raisedOn) raisedBeforePump = raisedOn.Count;
                    bool readsAtOnce = host.ReadContext("from-pool") == "2";
                    int pumped = ui.Pump();
                    bool onUiAfterPump;
                    lock (raisedOn) onUiAfterPump = raisedOn.Count == 2 && raisedOn[1] == uiThread;
                    ok &= Check(sb, "context: a publish from a pool thread raises ContextChanged on the UI thread, not the publisher's, raised-before-pump=" +
                            raisedBeforePump + " pumped=" + pumped,
                        returned && onPool && poolThread != uiThread && raisedBeforePump == 1 && pumped == 1 && onUiAfterPump);
                    ok &= Check(sb, "context: the value a pool thread published reads back before its posted raise lands", readsAtOnce);
                }
                host.ContextChanged -= subscriber;
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("EXC (context on the UI thread): " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { System.Threading.SynchronizationContext.SetSynchronizationContext(previous); }
            return ok;
        }

        /// <summary>A SynchronizationContext whose posts wait until the thread that owns it pumps them: the stand-in
        /// for the WinForms UI context, so "posted to the UI thread" is observable with no message loop. Send is not
        /// supported, because nothing under test may block on the UI thread.</summary>
        private sealed class PumpedContext : System.Threading.SynchronizationContext
        {
            private readonly Queue<KeyValuePair<System.Threading.SendOrPostCallback, object>> _posted =
                new Queue<KeyValuePair<System.Threading.SendOrPostCallback, object>>();

            public override void Post(System.Threading.SendOrPostCallback d, object state)
            {
                lock (_posted) _posted.Enqueue(new KeyValuePair<System.Threading.SendOrPostCallback, object>(d, state));
            }

            public override void Send(System.Threading.SendOrPostCallback d, object state)
            {
                throw new NotSupportedException("the self-test's UI context takes posts only");
            }

            public int Pending { get { lock (_posted) return _posted.Count; } }

            /// <summary>Run every queued post on the calling thread; the count run.</summary>
            public int Pump()
            {
                int ran = 0;
                while (true)
                {
                    KeyValuePair<System.Threading.SendOrPostCallback, object> next;
                    lock (_posted)
                    {
                        if (_posted.Count == 0) return ran;
                        next = _posted.Dequeue();
                    }
                    next.Key(next.Value);
                    ran++;
                }
            }
        }

        private static bool PetManagerPermissionGate(StringBuilder sb)
        {
            var host = new CompanionHost(null);
            ICompanionManager denied = host.GetCompanionManager("a-module-that-declared-nothing");
            bool ok = Check(sb, "pets: an undeclared module still gets a service, never null", denied != null);
            if (denied == null) return false;

            string error;
            ok &= Check(sb, "pets: refuses to validate, with a reason",
                !denied.ValidateXml("<xml/>", out error) && !string.IsNullOrEmpty(error));
            ok &= Check(sb, "pets: refuses to preview, with a reason",
                denied.SpawnPreview("<xml/>", out error) == null && !string.IsNullOrEmpty(error));
            ok &= Check(sb, "pets: refuses to install and to uninstall",
                !denied.InstallType("x", "<xml/>", out error) && !denied.UninstallType("x", out error));
            ok &= Check(sb, "pets: refuses to spawn or remove", !denied.SpawnOne("eSheep") && !denied.RemoveOne("eSheep"));
            ok &= Check(sb, "pets: enumerations come back empty rather than throwing",
                denied.InstalledTypes().Count == 0 && denied.OnScreenMix().Count == 0);
            string readXml;
            ok &= Check(sb, "pets: refuses to read a type's xml, with a reason (1.8.0 member)",
                !denied.TryReadTypeXml("eSheep", out readXml, out error) &&
                readXml == null && !string.IsNullOrEmpty(error));
            ok &= Check(sb, "pets: still reports the real cap so a module can size its UI",
                denied.MaxCompanions == StartUp.MAX_SHEEPS);
            ok &= Check(sb, "pets: reports no library path when the permission is missing",
                denied.CompanionsDirectory == "");

            // The two members added in 1.4.7, asserted against the REAL CompanionHost with no StartUp behind it --
            // which is also the "host not running" degradation path. Both must answer rather than throw,
            // because a module owning a window queries the theme while building UI, and a module logging a
            // diagnostic must never be punished for the log being unavailable.
            bool themeAnswered = true;
            try { bool unused = host.IsDarkTheme; }
            catch { themeAnswered = false; }
            ok &= Check(sb, "theme: IsDarkTheme answers even with no settings behind it", themeAnswered);

            bool logSurvived = true;
            try
            {
                host.Log("a-module", "self-test line");
                host.Log(null, "no id");
                host.Log("a-module", null);
            }
            catch { logSurvived = false; }
            ok &= Check(sb, "log: accepts a line, and tolerates a null id or message", logSurvived);
            return ok;
        }

        /// <summary>
        /// The MinHostVersion load gate. Two halves: the rule table (pure, so it runs even on a payload with
        /// no dev modules), then the real wiring through ModuleHost.LoadFrom. The wiring half lies about the
        /// HOST's version rather than shipping a purpose-built too-new module, and asserts the refusal happens
        /// BEFORE Init -- a module the host cannot satisfy must not get to contribute anything, subscribe to
        /// anything, or touch the host at all.
        /// </summary>
        private static bool MinHostVersionGate(StringBuilder sb, string modulesRoot, string scratch)
        {
            string reason;
            bool ok = Check(sb, "gate: an older requirement loads",
                ModuleHostRequirement.IsSatisfied("1.5.0", "1.0.0", out reason) && reason.Length == 0);
            ok &= Check(sb, "gate: an exactly-equal requirement loads",
                ModuleHostRequirement.IsSatisfied("1.5.0", "1.5.0", out reason));
            ok &= Check(sb, "gate: a shorter requirement (1.5) loads on 1.5.0",
                ModuleHostRequirement.IsSatisfied("1.5.0", "1.5", out reason));
            ok &= Check(sb, "gate: a NEWER requirement is refused, with a reason",
                !ModuleHostRequirement.IsSatisfied("1.5.0", "1.6.0", out reason) && reason.Length > 0);
            ok &= Check(sb, "gate: a newer requirement one patch up is refused",
                !ModuleHostRequirement.IsSatisfied("1.5.0", "1.5.1", out reason));
            ok &= Check(sb, "gate: a semver-tagged requirement still compares (1.6.0-beta > 1.5.0)",
                !ModuleHostRequirement.IsSatisfied("1.5.0", "1.6.0-beta", out reason));
            ok &= Check(sb, "gate: no requirement loads (every module shipped so far predates the gate)",
                ModuleHostRequirement.IsSatisfied("1.5.0", null, out reason) &&
                ModuleHostRequirement.IsSatisfied("1.5.0", "", out reason) &&
                ModuleHostRequirement.IsSatisfied("1.5.0", "   ", out reason));
            ok &= Check(sb, "gate: an unparseable requirement loads with a note, not a refusal",
                ModuleHostRequirement.IsSatisfied("1.5.0", "dev", out reason) && reason.Length > 0);
            ok &= Check(sb, "gate: an unparseable HOST version never refuses anything",
                ModuleHostRequirement.IsSatisfied("selftest", "9.9.9", out reason) && reason.Length > 0);

            // Wiring: the real loader, the real testmodule (which declares MinHostVersion 1.0.0).
            if (!Directory.Exists(Path.Combine(modulesRoot, "testmodule")))
            {
                sb.AppendLine("SKIP: no bundled test module for the MinHostVersion wiring half");
                return ok;
            }

            var tooOld = new RecordingHost { HostVersionValue = "0.0.1", StorageRoot = scratch };
            using (var loader = new ModuleHost())
            {
                int loaded = loader.LoadFrom(modulesRoot, tooOld, s => sb.AppendLine("  " + s));
                // The unstated rule this line rests on, stated (RA-293): EVERY bundled module declares a
                // MinHostVersion floor. One that declared none would load into the 0.0.1 host and fail here,
                // which is the right outcome, since the gate itself is pinned by RefusalKeyedByFolder on an
                // isolated testmodule copy and this line is about the payload, not the gate.
                ok &= Check(sb, "wiring: a host below every module's MinHostVersion loads nothing (every bundled module declares a floor)", loaded == 0);
                ok &= Check(sb, "wiring: a refused module contributes no tray item and no pane (refused before Init)",
                    tooOld.TrayItems.Count == 0 && tooOld.OptionsPanes.Count == 0);
                tooOld.RaiseCompanionPoked(new PokeInfo { Pet = new FakeCompanion(), PokeCount = 1 });
                ok &= Check(sb, "wiring: a refused module never subscribed to anything", tooOld.LastSayAll == null);
            }

            var satisfied = new RecordingHost { HostVersionValue = "1.5.0", StorageRoot = scratch };
            using (var loader = new ModuleHost())
            {
                int loaded = loader.LoadFrom(modulesRoot, satisfied, s => sb.AppendLine("  " + s));
                ok &= Check(sb, "wiring: a satisfying host version loads the modules normally", loaded >= 1);
                ok &= Check(sb, "wiring: and they contribute as usual", satisfied.TrayItems.Count >= 1);
                loader.ShutdownAll(s => sb.AppendLine("  " + s));
            }
            return ok;
        }

        /// <summary>
        /// The weekly cadence for module and pet update checks.
        ///
        /// Replaces a month-granularity "yyyy-MM" stamp. That shape could only ever answer "has the calendar
        /// month changed", and it came with a fresh-install seed that stamped WITHOUT checking, so a new
        /// install could not learn about a module update until the following month. Both are gone; the rule
        /// is now the same clock-injectable AppUpdateCheck.ShouldCheck the app's own version check uses, at a
        /// different interval.
        ///
        /// The cases that matter are the boundary and the two ways a stamp can be useless.
        /// </summary>
        private static bool WeeklyCheckSchedule(StringBuilder sb)
        {
            DateTimeOffset now = new DateTimeOffset(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);
            TimeSpan week = AppUpdateCheck.ContentInterval;

            bool ok = Check(sb, "weekly: the interval is seven days", week == TimeSpan.FromDays(7));
            ok &= Check(sb, "weekly: a check from six days and 23 hours ago is not due yet",
                !AppUpdateCheck.ShouldCheck(true, now - TimeSpan.FromDays(7) + TimeSpan.FromHours(1), now, week));
            ok &= Check(sb, "weekly: a check from exactly a week ago is due",
                AppUpdateCheck.ShouldCheck(true, now - week, now, week));
            // The bug the old seed-without-checking branch caused: a fresh install went blind for a whole
            // month. MinValue means "never checked", which must read as DUE, not as recently checked.
            ok &= Check(sb, "weekly: never having checked is due, so a fresh install is not blind",
                AppUpdateCheck.ShouldCheck(true, DateTimeOffset.MinValue, now, week));
            // A stamp in the future can only come from a clock change. Treating it as "recent" would go
            // quiet until the clock caught up, which could be years.
            ok &= Check(sb, "weekly: a stamp in the future is due (the clock moved backwards)",
                AppUpdateCheck.ShouldCheck(true, now + TimeSpan.FromDays(30), now, week));
            ok &= Check(sb, "weekly: switching the check off stops it regardless of the stamp",
                !AppUpdateCheck.ShouldCheck(false, DateTimeOffset.MinValue, now, week));

            // The six encode/decode assertions here went with Encode and Decode themselves on
            // 2026-09-17. They asserted that a cache round-tripped, and nothing read the cache: the
            // settings key was write-only, so the format was correct and pointless. An assertion
            // whose subject has no consumer is not coverage, it is ballast that makes a suite look
            // thorough -- and it would have kept the dead code alive by appearing to justify it.

            return ok;
        }

        /// <summary>
        /// The one version rule shared by the Update button and the weekly check. Newer offers, equal and older
        /// do not, and an unparseable version on either side offers NOTHING — a guess there becomes an update
        /// prompt that survives being accepted.
        /// </summary>
        private static bool UpdateScanVersionRule(StringBuilder sb)
        {
            var catalog = new RemoteCatalog();
            catalog.Modules.Add(new CatalogModule { Id = "demo", Name = "Demo", Version = "1.1.1" });
            catalog.Modules.Add(new CatalogModule { Id = "weird", Name = "Weird", Version = "not-a-version" });

            bool ok = Check(sb, "scan: a newer catalog version is offered",
                ModuleUpdateScan.FindUpdate(catalog, "demo", "1.1.0") != null);
            ok &= Check(sb, "scan: an equal version is not offered",
                ModuleUpdateScan.FindUpdate(catalog, "demo", "1.1.1") == null);
            ok &= Check(sb, "scan: an older catalog version is not offered",
                ModuleUpdateScan.FindUpdate(catalog, "demo", "1.2.0") == null);
            ok &= Check(sb, "scan: an unknown id is not offered",
                ModuleUpdateScan.FindUpdate(catalog, "absent", "1.0.0") == null);
            ok &= Check(sb, "scan: an unparseable installed version offers nothing",
                ModuleUpdateScan.FindUpdate(catalog, "demo", "dev") == null);
            ok &= Check(sb, "scan: an unparseable catalog version offers nothing",
                ModuleUpdateScan.FindUpdate(catalog, "weird", "1.0.0") == null);
            ok &= Check(sb, "scan: no catalog (never fetched) offers nothing",
                ModuleUpdateScan.FindUpdate(null, "demo", "1.1.0") == null);

            // What the weekly balloon may announce is what this host can RUN (N-modules-update-all-01): the loader's
            // question, ModuleHostRequirement, asked inside the scan with the host version, so the balloon cannot
            // name an update the Modules pane shows as "needs a newer app". Three updates on offer: one with no
            // floor, one whose floor this host meets exactly, one whose floor it does not. The WITNESS is the same
            // catalog on a host new enough, which offers all three: the filter is the floor, not the module.
            var floors = new RemoteCatalog();
            floors.Modules.Add(new CatalogModule { Id = "nofloor", Name = "No Floor", Version = "1.1.0" });
            floors.Modules.Add(new CatalogModule { Id = "metfloor", Name = "Met Floor", Version = "1.1.0", MinHostVersion = "1.6.1" });
            floors.Modules.Add(new CatalogModule { Id = "highfloor", Name = "High Floor", Version = "2.0.0", MinHostVersion = "9.0.0" });
            var loadedForFloors = new List<IModule>
            {
                new UpdateScanModule("nofloor", "1.0.0"), new UpdateScanModule("metfloor", "1.0.0"), new UpdateScanModule("highfloor", "1.0.0"),
            };
            var heldBack = new List<string>();
            List<ModuleUpdateOffer> runnable = ModuleUpdateScan.FindUpdates(floors, loadedForFloors, "1.6.1", heldBack);
            ok &= Check(sb, "scan: the weekly check leaves out an update whose MinHostVersion this host cannot satisfy, offered=" +
                    ModuleUpdateScan.Describe(runnable),
                runnable.Count == 2 && runnable[0].Id == "nofloor" && runnable[1].Id == "metfloor" &&
                heldBack.Count == 1 && heldBack[0].StartsWith("High Floor 2.0.0: needs host 9.0.0", StringComparison.Ordinal));
            ok &= Check(sb, "scan: WITNESS on a host new enough the same catalog offers all three",
                ModuleUpdateScan.FindUpdates(floors, loadedForFloors, "9.0.0", null).Count == 3);

            var offers = new List<ModuleUpdateOffer>
            {
                new ModuleUpdateOffer { Offered = catalog.Modules[0], InstalledVersion = "1.1.0" },
            };
            ok &= Check(sb, "scan: one offer describes as 'Demo 1.1.1'", ModuleUpdateScan.Describe(offers) == "Demo 1.1.1");
            offers.Add(new ModuleUpdateOffer { Offered = new CatalogModule { Id = "b", Name = "Bee", Version = "2.0" }, InstalledVersion = "1.0" });
            ok &= Check(sb, "scan: two offers read as a sentence", ModuleUpdateScan.Describe(offers) == "Demo 1.1.1 and Bee 2.0");
            ok &= Check(sb, "scan: no offers describe as empty", ModuleUpdateScan.Describe(new List<ModuleUpdateOffer>()) == "");
            return ok;
        }

        /// <summary>
        /// The scratch-root sweep (<see cref="SelfTestScratch"/>). Four self-tests load a module through a
        /// collectible AssemblyLoadContext and so physically cannot delete their own temp directory on the way
        /// out; cleanup is deferred to the NEXT run's sweep. That makes the sweep the only thing standing
        /// between this suite and an unbounded pile of directories in %TEMP%, which is exactly what it had
        /// already produced, so it gets asserted directly rather than assumed.
        ///
        /// The fresh-root case is the control that matters: a sweep which deleted everything would still pass
        /// the aged assertion, and would then delete a concurrently running instance's scratch out from under
        /// it.
        /// </summary>
        private static bool ScratchSweep(StringBuilder sb)
        {
            bool ok = true;
            string aged = Path.Combine(Path.GetTempPath(), SelfTestScratch.NameFor("sweepprobe-aged"));
            string fresh = Path.Combine(Path.GetTempPath(), SelfTestScratch.NameFor("sweepprobe-fresh"));
            // Deliberately hand-built rather than via NameFor: it stands in for a harness that has since
            // been renamed, which is how the real orphans were created.
            string legacy = Path.Combine(Path.GetTempPath(), "dp-legacyprobe-" + Guid.NewGuid().ToString("N"));
            string live = null;
            try
            {
                // Aged, and NOT empty: a sweep that only removes empty directories would leak every real one.
                Directory.CreateDirectory(aged);
                File.WriteAllText(Path.Combine(aged, "payload.txt"), "x");
                Directory.SetLastWriteTimeUtc(aged, DateTime.UtcNow - SelfTestScratch.Age - TimeSpan.FromMinutes(5));

                Directory.CreateDirectory(fresh);

                Directory.CreateDirectory(legacy);
                File.WriteAllText(Path.Combine(legacy, "payload.txt"), "x");
                Directory.SetLastWriteTimeUtc(legacy, DateTime.UtcNow - SelfTestScratch.Age - TimeSpan.FromMinutes(5));

                live = SelfTestScratch.Create("sweepprobe-live");   // Create() sweeps before it creates
                ok &= Check(sb, "scratch: Create returns a directory that exists", Directory.Exists(live));
                ok &= Check(sb, "scratch: an aged root is swept, contents and all", !Directory.Exists(aged));
                ok &= Check(sb, "scratch: a fresh root survives the sweep", Directory.Exists(fresh));
                // The case that actually leaked: a root whose name does NOT follow the current convention.
                // The sweep used to require a "-selftest-" marker, so when a harness was renamed its orphans
                // became uncollectable -- 61 of them, the oldest a month old, from dp-petmgr-<guid> code that
                // no longer exists in the tree. Every assertion above uses NameFor(), so all of them passed
                // while this leaked. Age is the only safe question to ask about a dp- scratch directory.
                ok &= Check(sb, "scratch: an aged root NOT matching the current naming is still swept",
                    !Directory.Exists(legacy));

                string detail;
                ok &= Check(sb, "scratch: TryRelease removes a root it can delete",
                    SelfTestScratch.TryRelease(live, out detail) && !Directory.Exists(live));
                ok &= Check(sb, "scratch: releasing an absent path is not an error",
                    SelfTestScratch.TryRelease(live, out detail));
                ok &= Check(sb, "scratch: releasing a null path is not an error",
                    SelfTestScratch.TryRelease(null, out detail));
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("EXC (scratch sweep): " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(aged)) Directory.Delete(aged, true); } catch { }
                try { if (Directory.Exists(fresh)) Directory.Delete(fresh, true); } catch { }
                try { if (Directory.Exists(legacy)) Directory.Delete(legacy, true); } catch { }
                try { if (live != null && Directory.Exists(live)) Directory.Delete(live, true); } catch { }
            }
            return ok;
        }

        /// <summary>Whether a poke responder registered under <paramref name="moduleId"/> is in the host's chain.</summary>
        private static bool HasResponderModule(CompanionHost host, string moduleId)
        {
            foreach (string id in host.PokeResponderModuleIds)
                if (string.Equals(id, moduleId, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Mark an id and read the marker back: the positive control for the throwing-write checks.</summary>
        private static bool MarkThenRead(string id, string markerPath)
        {
            PendingModuleRemovals.MarkForRemoval(id, markerPath);
            return File.Exists(markerPath) && File.ReadAllText(markerPath).Trim() == id;
        }

        /// <summary>
        /// FindModuleDll picks the module's OWN assembly from a folder not named after it (RA-291). The fallback
        /// excluded only Contracts.dll, and every module folder also ships ModuleKit.dll, so a sideloaded
        /// weather-pet/ holding WeatherPet.dll had DesktopAICompanion.ModuleKit.dll chosen (it sorts first)
        /// and was refused as "no type implementing IModule". The deps.json names the assembly, as
        /// Invoke-SelfTests.ps1 already relies on; without one the host's own DLLs are skipped.
        /// </summary>
        private static bool ModuleDllDiscovery(StringBuilder sb)
        {
            string root = SelfTestScratch.Create("module-dll");
            bool ok = true;
            try
            {
                string sideloaded = Path.Combine(root, "weather-pet");
                Directory.CreateDirectory(sideloaded);
                File.WriteAllText(Path.Combine(sideloaded, "DesktopAICompanion.Contracts.dll"), "shared contract");
                File.WriteAllText(Path.Combine(sideloaded, "DesktopAICompanion.ModuleKit.dll"), "the helper library");
                File.WriteAllText(Path.Combine(sideloaded, "WeatherPet.dll"), "the module");
                File.WriteAllText(Path.Combine(sideloaded, "WeatherPet.deps.json"), "{}");
                string picked = ModuleHost.FindModuleDll(sideloaded);
                ok &= Check(sb, "dll: a folder not named after its assembly yields the assembly its deps.json names",
                    picked != null && Path.GetFileName(picked) == "WeatherPet.dll");

                File.Delete(Path.Combine(sideloaded, "WeatherPet.deps.json"));
                picked = ModuleHost.FindModuleDll(sideloaded);
                ok &= Check(sb, "dll: without a deps.json the host's own DLLs are skipped, never chosen as the module",
                    picked != null && Path.GetFileName(picked) == "WeatherPet.dll");

                string named = Path.Combine(root, "named");
                Directory.CreateDirectory(named);
                File.WriteAllText(Path.Combine(named, "DesktopAICompanion.ModuleKit.dll"), "the helper library");
                File.WriteAllText(Path.Combine(named, "named.dll"), "the module");
                ok &= Check(sb, "dll: WITNESS a folder named after its assembly still yields <folder>.dll first",
                    Path.GetFileName(ModuleHost.FindModuleDll(named) ?? "") == "named.dll");

                string onlyHost = Path.Combine(root, "onlyhost");
                Directory.CreateDirectory(onlyHost);
                File.WriteAllText(Path.Combine(onlyHost, "DesktopAICompanion.ModuleKit.dll"), "the helper library");
                ok &= Check(sb, "dll: a folder holding only the host's DLLs yields nothing, so the loader says 'no module DLL'",
                    ModuleHost.FindModuleDll(onlyHost) == null);
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("EXC (module dll discovery): " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                string releaseDetail;
                if (!SelfTestScratch.TryRelease(root, out releaseDetail))
                    sb.AppendLine("NOTE: scratch left for the next sweep (" + releaseDetail + ")");
            }
            return ok;
        }

        // ================= lane feature/host-abi-180: the shared Claude Code sign-in store (host 1.8.0) =================

        private const string SyntheticToken = "sk-ant-oat01-SYNTHETIC-abi180-first";
        private const string SyntheticOther = "sk-ant-oat01-SYNTHETIC-abi180-other";

        /// <summary>
        /// BACKLOG N-cli-token-02 and the binding design review of 2026-10-29: the host's IHostCredentials, cast from a real
        /// CompanionHost, over a scratch store. The gate (a declaration AND a recorded grant, on every verb; an unknown id),
        /// the DPAPI round trip and the value never in the file or the log, the four content states, the store's life
        /// (never used, saved, removed: an import only while never used or holding nothing usable, never after a save or a
        /// removal, and the compare-and-set under the lock, with two threads racing an import), Remove's outcomes, a lock
        /// held elsewhere, and the change event: once per commit, with the revision, after the commit, and a throwing
        /// handler skipped while the others still run.
        /// </summary>
        private static bool SharedSignInStore(StringBuilder sb, string isolatedDataRoot)
        {
            bool ok = true;
            string dir = SelfTestScratch.Create("credentials");
            try
            {
                // The shipped host with no app behind it: no module is loaded, so every caller is refused before any disk.
                IHostCredentials bare = new CompanionHost(null) as IHostCredentials;
                ok &= Check(sb, "abi180: store: the host's IHost is an IHostCredentials, as the ABI promises", bare != null);
                if (bare == null) return ok;
                HostCredentialReading bareRead = bare.ReadCredential("aibrain", HostCredentialId.ClaudeCodeSignIn);
                HostCredentialCommit bareSave = bare.TrySaveCredential("aibrain", HostCredentialId.ClaudeCodeSignIn, SyntheticToken);
                ok &= Check(sb, "abi180: store: with no loaded module behind it the shipped host refuses every caller, Denied, and writes nothing (" +
                    bareRead.State + ", " + bareSave.Outcome + ")",
                    bareRead.State == HostCredentialState.Denied && bareRead.Value == null && bareSave.Outcome == HostCredentialOutcome.Denied &&
                    !File.Exists(Path.Combine(HostCredentialStore.DefaultDirectory, HostCredentialStore.ClaudeCodeSignInFileName)));

                var log = new List<string>();
                var store = new HostCredentialStore(dir,
                    delegate (string id) { return id == "aibrain" || id == "remembrance" || id == "ungranted"; },
                    delegate (string id, string line) { lock (log) log.Add((id ?? "") + ": " + line); });
                var host = new CompanionHost(null, store);
                IHostCredentials creds = host;
                var events = new List<long>();
                int throwingCalls = 0;
                // Each handler added inside its module's Init, as a module adds it: the host hands CredentialChanged only to a
                // module allowed the sign-in, and knows a handler's module from the Init that added it (BACKLOG
                // N-audit-runner-02). Beside AI Brain's two, one of a module declaring the flag with no grant, one of a module
                // not declaring it, and one added outside any Init, each counting what it hears.
                int ungrantedHeard = 0, undeclaredHeard = 0, unattributedHeard = 0;
                host.BeginModuleInit("aibrain");
                creds.CredentialChanged += delegate (HostCredentialId id, long revision) { throwingCalls++; throw new InvalidOperationException("a module's handler threw"); };
                creds.CredentialChanged += delegate (HostCredentialId id, long revision) { lock (events) events.Add(id == HostCredentialId.ClaudeCodeSignIn ? revision : -999); };
                host.EndModuleInit();
                host.BeginModuleInit("ungranted");
                creds.CredentialChanged += delegate { ungrantedHeard++; };
                host.EndModuleInit();
                host.BeginModuleInit("fortunes");
                creds.CredentialChanged += delegate { undeclaredHeard++; };
                host.EndModuleInit();
                creds.CredentialChanged += delegate { unattributedHeard++; };
                string envelope = Path.Combine(dir, HostCredentialStore.ClaudeCodeSignInFileName);

                // THE GATE. Not declared: Denied. Declared and not allowed: ConsentRequired. Neither writes, imports or moves.
                var undeclared = new List<string>();
                undeclared.Add(creds.ReadCredential("fortunes", HostCredentialId.ClaudeCodeSignIn).State.ToString());
                undeclared.Add(creds.TrySaveCredential("fortunes", HostCredentialId.ClaudeCodeSignIn, SyntheticToken).Outcome.ToString());
                undeclared.Add(creds.ImportCredentialIfUnset("fortunes", HostCredentialId.ClaudeCodeSignIn, SyntheticToken).Outcome.ToString());
                undeclared.Add(creds.RemoveCredential("fortunes", HostCredentialId.ClaudeCodeSignIn).Outcome.ToString());
                ok &= Check(sb, "abi180: store: a module that does not declare ClaudeCodeSignIn is refused Denied on every verb, and nothing is written (" +
                    string.Join(", ", undeclared.ToArray()) + ")",
                    string.Join(",", undeclared.ToArray()) == "Denied,Denied,Denied,Denied" && !File.Exists(envelope));
                var ungranted = new List<string>();
                ungranted.Add(creds.ReadCredential("ungranted", HostCredentialId.ClaudeCodeSignIn).State.ToString());
                ungranted.Add(creds.TrySaveCredential("ungranted", HostCredentialId.ClaudeCodeSignIn, SyntheticToken).Outcome.ToString());
                ungranted.Add(creds.ImportCredentialIfUnset("ungranted", HostCredentialId.ClaudeCodeSignIn, SyntheticToken).Outcome.ToString());
                ungranted.Add(creds.RemoveCredential("ungranted", HostCredentialId.ClaudeCodeSignIn).Outcome.ToString());
                ok &= Check(sb, "abi180: store: a module that declares it without the user's grant is refused ConsentRequired on every verb, and nothing is written (" +
                    string.Join(", ", ungranted.ToArray()) + ")",
                    string.Join(",", ungranted.ToArray()) == "ConsentRequired,ConsentRequired,ConsentRequired,ConsentRequired" && !File.Exists(envelope));
                bool loggedRefusals = false;
                lock (log) foreach (string line in log) if (line.StartsWith("ungranted: credentials: read refused: not-allowed", StringComparison.Ordinal)) loggedRefusals = true;
                ok &= Check(sb, "abi180: store: a refusal is logged by module id and reason", loggedRefusals);
                ok &= Check(sb, "abi180: store: an id this host does not know is Denied",
                    creds.ReadCredential("aibrain", (HostCredentialId)99).State == HostCredentialState.Denied);

                bool granted = HostCredentialStore.SetGrant(dir, "aibrain", true, null) && HostCredentialStore.SetGrant(dir, "Remembrance", true, null);
                ok &= Check(sb, "abi180: store: WITNESS a recorded grant lets a declaring module in, ids compared as the loader compares them",
                    granted && HostCredentialStore.IsGranted(dir, "AIBRAIN") && HostCredentialStore.IsGranted(dir, "remembrance") &&
                    !HostCredentialStore.IsGranted(dir, "ungranted"));
                // A RECORDED ALLOW IS ANNOUNCED (N-final-host-02): a grant commits no envelope, and a module refused until now
                // heard no other module's commit either, so its pane kept its refused readings. AI Brain's two handlers hear
                // its Allow once, at the store's current revision (0, never used); nobody else hears it. Counted afresh below.
                ok &= Check(sb, "final-host: store: a recorded Allow raises CredentialChanged to that module at the store's current revision (" +
                    string.Join(",", events.ConvertAll<string>(delegate (long r) { return r.ToString(); }).ToArray()) + "; thrower " + throwingCalls +
                    "; others " + ungrantedHeard + "," + undeclaredHeard + "," + unattributedHeard + ")",
                    events.Count == 1 && events[0] == 0 && throwingCalls == 1 && ungrantedHeard == 0 && undeclaredHeard == 0 && unattributedHeard == 0);
                lock (events) events.Clear();
                throwingCalls = 0;

                // NEVER USED, then SAVED: DPAPI round trip, shared by every allowed module, one event with the revision.
                HostCredentialReading virgin = creds.ReadCredential("aibrain", HostCredentialId.ClaudeCodeSignIn);
                ok &= Check(sb, "abi180: store: a store never used reads None at revision 0",
                    virgin.State == HostCredentialState.None && virgin.Revision == 0 && virgin.Value == null);
                HostCredentialCommit saved = creds.TrySaveCredential("aibrain", HostCredentialId.ClaudeCodeSignIn, SyntheticToken);
                HostCredentialReading other = creds.ReadCredential("remembrance", HostCredentialId.ClaudeCodeSignIn);
                ok &= Check(sb, "abi180: store: a save is read back by every allowed module, the same token at the revision it committed (" +
                    saved.Outcome + " " + saved.Revision + ", " + other.State + " " + other.Revision + ")",
                    saved.Outcome == HostCredentialOutcome.Saved && saved.Revision == 1 && other.State == HostCredentialState.Saved &&
                    other.Value == SyntheticToken && other.Revision == 1);
                string onDisk = File.Exists(envelope) ? File.ReadAllText(envelope) : "";
                bool unseals = false;
                try
                {
                    var o = System.Text.Json.Nodes.JsonNode.Parse(onDisk) as System.Text.Json.Nodes.JsonObject;
                    byte[] clear = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(o["sealed"].GetValue<string>()),
                        HostCredentialStore.Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                    unseals = Encoding.UTF8.GetString(clear) == SyntheticToken;
                }
                catch { unseals = false; }
                ok &= Check(sb, "abi180: store: the file holds the token DPAPI-sealed for this user with the host's entropy, never in the clear",
                    onDisk.Length > 0 && onDisk.IndexOf(SyntheticToken, StringComparison.Ordinal) < 0 && unseals &&
                    dir.Length > 0 && Path.GetDirectoryName(envelope) == dir);
                ok &= Check(sb, "abi180: store: a commit raises CredentialChanged once, with its revision, and a handler that throws is skipped while the next still runs (" +
                    string.Join(",", events.ConvertAll<string>(delegate (long r) { return r.ToString(); }).ToArray()) + "; thrower " + throwingCalls + ")",
                    events.Count == 1 && events[0] == 1 && throwingCalls == 1);
                ok &= Check(sb, "final-host: store: CredentialChanged reaches only a module allowed the sign-in; one declaring it without the grant, one not declaring it and a handler added outside any Init hear nothing (" +
                    ungrantedHeard + ", " + undeclaredHeard + ", " + unattributedHeard + ")",
                    events.Count == 1 && ungrantedHeard == 0 && undeclaredHeard == 0 && unattributedHeard == 0);

                // NO RESURRECTION. An import over a saved store is refused; Remove closes the store; an import after it too.
                HostCredentialCommit overSaved = creds.ImportCredentialIfUnset("remembrance", HostCredentialId.ClaudeCodeSignIn, SyntheticOther);
                ok &= Check(sb, "abi180: store: an import over a saved token is NotImported, the saved one kept and nothing announced (" + overSaved.Outcome + ")",
                    overSaved.Outcome == HostCredentialOutcome.NotImported && overSaved.Revision == 1 &&
                    creds.ReadCredential("aibrain", HostCredentialId.ClaudeCodeSignIn).Value == SyntheticToken && events.Count == 1);
                HostCredentialCommit removed = creds.RemoveCredential("remembrance", HostCredentialId.ClaudeCodeSignIn);
                HostCredentialReading afterRemove = creds.ReadCredential("aibrain", HostCredentialId.ClaudeCodeSignIn);
                ok &= Check(sb, "abi180: store: Remove through one module removes it for every module, at a new revision, announced once (" +
                    removed.Outcome + " " + removed.Revision + ", " + afterRemove.State + ")",
                    removed.Outcome == HostCredentialOutcome.Removed && removed.Revision == 2 && afterRemove.State == HostCredentialState.None &&
                    afterRemove.Revision == 2 && events.Count == 2 && events[1] == 2);
                HostCredentialCommit lateImport = creds.ImportCredentialIfUnset("aibrain", HostCredentialId.ClaudeCodeSignIn, SyntheticOther);
                ok &= Check(sb, "abi180: store: an import after a Remove is NotImported, so another module's old copy never brings the removed sign-in back (" +
                    lateImport.Outcome + ")",
                    lateImport.Outcome == HostCredentialOutcome.NotImported &&
                    creds.ReadCredential("aibrain", HostCredentialId.ClaudeCodeSignIn).State == HostCredentialState.None && events.Count == 2);
                HostCredentialCommit again = creds.RemoveCredential("aibrain", HostCredentialId.ClaudeCodeSignIn);
                ok &= Check(sb, "abi180: store: a second Remove is NothingToRemove at the same revision, and announces nothing (" + again.Outcome + " " + again.Revision + ")",
                    again.Outcome == HostCredentialOutcome.NothingToRemove && again.Revision == 2 && events.Count == 2);
                // The grant is asked when the event is raised, not when the handler was added: allowed now, it hears the next.
                int ungrantedPreGrant = ungrantedHeard, aibrainPreGrant = events.Count;
                HostCredentialStore.SetGrant(dir, "ungranted", true, null);
                int ungrantedBefore = ungrantedHeard;
                ok &= Check(sb, "final-host: store: an Allow recorded for one module is heard by that module alone; an allowed module beside it hears nothing (" +
                    (ungrantedBefore - ungrantedPreGrant) + ", " + (events.Count - aibrainPreGrant) + ")",
                    ungrantedBefore == ungrantedPreGrant + 1 && events.Count == aibrainPreGrant);
                HostCredentialCommit pasted = creds.TrySaveCredential("aibrain", HostCredentialId.ClaudeCodeSignIn, SyntheticOther);
                int ungrantedAfter = ungrantedHeard;
                HostCredentialStore.SetGrant(dir, "ungranted", false, null);
                ok &= Check(sb, "abi180: store: WITNESS a save after a Remove is taken, the user's explicit paste (" + pasted.Outcome + " " + pasted.Revision + ")",
                    pasted.Outcome == HostCredentialOutcome.Saved && pasted.Revision == 3 &&
                    creds.ReadCredential("remembrance", HostCredentialId.ClaudeCodeSignIn).Value == SyntheticOther && events.Count == 3);
                ok &= Check(sb, "final-host: store: WITNESS a module allowed after it subscribed hears the next commit, the grant read when the event is raised (" +
                    ungrantedBefore + " -> " + ungrantedAfter + ")", ungrantedAfter == ungrantedBefore + 1);

                HostCredentialCommit refused = creds.TrySaveCredential("aibrain", HostCredentialId.ClaudeCodeSignIn, "has a space");
                ok &= Check(sb, "abi180: store: a value no token can be is Refused with the reason, the saved one kept and nothing announced (" + refused.Problem + ")",
                    refused.Outcome == HostCredentialOutcome.Refused && refused.Problem.Length > 0 &&
                    creds.ReadCredential("aibrain", HostCredentialId.ClaudeCodeSignIn).Value == SyntheticOther && events.Count == 3);

                // A STORE NEVER USED, and a Remove: nothing to remove, nothing written, nothing announced, and the store left
                // open, so another module's old token can still move in (BACKLOG N-audit-runner-02; this check said the
                // opposite, "still closes it", until the host took the shared runner's rule, N-audit-20261029-01).
                string closedDir = SelfTestScratch.Create("credentials-closed");
                var closed = new CompanionHost(null, new HostCredentialStore(closedDir, delegate (string id) { return id == "aibrain"; }, null));
                HostCredentialStore.SetGrant(closedDir, "aibrain", true, null);
                int closedHeard = 0;
                closed.BeginModuleInit("aibrain");
                ((IHostCredentials)closed).CredentialChanged += delegate { closedHeard++; };
                closed.EndModuleInit();
                HostCredentialCommit closeFirst = closed.RemoveCredential("aibrain", HostCredentialId.ClaudeCodeSignIn);
                bool closeWrote = File.Exists(Path.Combine(closedDir, HostCredentialStore.ClaudeCodeSignInFileName));
                int heardAfterClose = closedHeard;
                HostCredentialCommit afterClose = closed.ImportCredentialIfUnset("aibrain", HostCredentialId.ClaudeCodeSignIn, SyntheticToken);
                ok &= Check(sb, "final-host: store: Remove on a store never used is NothingToRemove at revision 0, writes nothing and announces nothing, so a later import is taken (" +
                    closeFirst.Outcome + " " + closeFirst.Revision + ", " + (closeWrote ? "file written" : "no file") + ", " + heardAfterClose + " heard, " + afterClose.Outcome + ")",
                    closeFirst.Outcome == HostCredentialOutcome.NothingToRemove && closeFirst.Revision == 0 && !closeWrote && heardAfterClose == 0 &&
                    afterClose.Outcome == HostCredentialOutcome.Imported);
                ok &= Check(sb, "final-host: store: WITNESS the import into the store a Remove left open is announced to the allowed module (" + closedHeard + ")",
                    closedHeard == 1);
                SelfTestScratch.TryRelease(closedDir, out string ignoredRelease);

                // THE CONTENT STATES, and an import taken over each unusable one (the review's P2: shared Unreadable or
                // NotAToken never counts as present).
                string statesDir = SelfTestScratch.Create("credentials-states");
                var states = new HostCredentialStore(statesDir, delegate (string id) { return id == "aibrain"; }, null);
                HostCredentialStore.SetGrant(statesDir, "aibrain", true, null);
                string statesEnvelope = Path.Combine(statesDir, HostCredentialStore.ClaudeCodeSignInFileName);
                string notATokenSealed = Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes("https://not a token"),
                    HostCredentialStore.Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser));
                var seen = new List<string>();
                File.WriteAllText(statesEnvelope, "{\"schema\":1,\"revision\":4,\"state\":\"saved\",\"sealed\":\"" + notATokenSealed + "\"}");
                seen.Add(states.Read("aibrain", HostCredentialId.ClaudeCodeSignIn).State + "/" + states.ImportIfUnset("aibrain", HostCredentialId.ClaudeCodeSignIn, SyntheticToken).Outcome);
                File.WriteAllText(statesEnvelope, "{\"schema\":1,\"revision\":6,\"state\":\"saved\",\"sealed\":\"AAAA\"}");
                seen.Add(states.Read("aibrain", HostCredentialId.ClaudeCodeSignIn).State + "/" + states.ImportIfUnset("aibrain", HostCredentialId.ClaudeCodeSignIn, SyntheticToken).Outcome);
                File.WriteAllText(statesEnvelope, "{ this is not an envelope");
                seen.Add(states.Read("aibrain", HostCredentialId.ClaudeCodeSignIn).State + "/" + states.ImportIfUnset("aibrain", HostCredentialId.ClaudeCodeSignIn, SyntheticToken).Outcome);
                HostCredentialReading recovered = states.Read("aibrain", HostCredentialId.ClaudeCodeSignIn);
                ok &= Check(sb, "abi180: store: a sealed value that is no token reads NotAToken, one this account cannot unseal or a damaged file Unreadable, and an import is taken over each (" +
                    string.Join(", ", seen.ToArray()) + ")",
                    string.Join(",", seen.ToArray()) == "NotAToken/Imported,Unreadable/Imported,Unreadable/Imported" &&
                    recovered.State == HostCredentialState.Saved && recovered.Value == SyntheticToken && recovered.Revision > 0);

                // THE COMPARE-AND-SET. A reads an empty store, B saves, A's import of its old copy is refused; and two
                // threads racing an import into a store never used: exactly one is taken.
                string casDir = SelfTestScratch.Create("credentials-cas");
                var cas = new HostCredentialStore(casDir, delegate (string id) { return id == "aibrain" || id == "remembrance"; }, null);
                HostCredentialStore.SetGrant(casDir, "aibrain", true, null);
                HostCredentialStore.SetGrant(casDir, "remembrance", true, null);
                HostCredentialReading aSawEmpty = cas.Read("aibrain", HostCredentialId.ClaudeCodeSignIn);
                cas.TrySave("remembrance", HostCredentialId.ClaudeCodeSignIn, SyntheticOther);
                HostCredentialCommit aImports = cas.ImportIfUnset("aibrain", HostCredentialId.ClaudeCodeSignIn, SyntheticToken);
                ok &= Check(sb, "abi180: store: an import decided on an empty read that lost the race to another module's save is NotImported, the save kept (" +
                    aSawEmpty.State + " then " + aImports.Outcome + ")",
                    aSawEmpty.State == HostCredentialState.None && aImports.Outcome == HostCredentialOutcome.NotImported &&
                    cas.Read("aibrain", HostCredentialId.ClaudeCodeSignIn).Value == SyntheticOther);
                string raceDir = SelfTestScratch.Create("credentials-race");
                var race = new HostCredentialStore(raceDir, delegate (string id) { return true; }, null);
                HostCredentialStore.SetGrant(raceDir, "aibrain", true, null);
                HostCredentialStore.SetGrant(raceDir, "remembrance", true, null);
                var gate = new System.Threading.ManualResetEventSlim(false);
                var outcomes = new HostCredentialOutcome[2];
                var racers = new System.Threading.Thread[2];
                for (int i = 0; i < 2; i++)
                {
                    int slot = i;
                    racers[i] = new System.Threading.Thread(delegate ()
                    {
                        gate.Wait();
                        outcomes[slot] = race.ImportIfUnset(slot == 0 ? "aibrain" : "remembrance", HostCredentialId.ClaudeCodeSignIn,
                            slot == 0 ? SyntheticToken : SyntheticOther).Outcome;
                    });
                    racers[i].Start();
                }
                gate.Set();
                bool joined = racers[0].Join(20000) & racers[1].Join(20000);
                int taken = (outcomes[0] == HostCredentialOutcome.Imported ? 1 : 0) + (outcomes[1] == HostCredentialOutcome.Imported ? 1 : 0);
                int left = (outcomes[0] == HostCredentialOutcome.NotImported ? 1 : 0) + (outcomes[1] == HostCredentialOutcome.NotImported ? 1 : 0);
                ok &= Check(sb, "abi180: store: two imports racing into a store never used: exactly one is taken and the other is NotImported (" +
                    outcomes[0] + ", " + outcomes[1] + ")", joined && taken == 1 && left == 1);

                // A STORE HELD ELSEWHERE (a second window of the app mid-commit): Unavailable, nothing committed.
                race.LockTimeout = 200;
                string racePath = Path.Combine(raceDir, HostCredentialStore.ClaudeCodeSignInFileName);
                HostCredentialOutcome whileHeld = HostCredentialOutcome.Saved;
                long heldRevision = race.Read("aibrain", HostCredentialId.ClaudeCodeSignIn).Revision;
                using (IDisposable held = DesktopAICompanion.ModuleKit.CrossSessionLock.TryAcquire(DesktopAICompanion.ModuleKit.CrossSessionLock.BuildGlobalMutexName("credentials", racePath), racePath, 2000))
                {
                    var holder = new System.Threading.Thread(delegate ()
                    {
                        whileHeld = race.TrySave("aibrain", HostCredentialId.ClaudeCodeSignIn, SyntheticToken + "x").Outcome;
                    });
                    holder.Start();
                    holder.Join(20000);
                    ok &= Check(sb, "abi180: store: a change while another window holds the store's lock answers Unavailable and commits nothing (" + whileHeld + ")",
                        held != null && whileHeld == HostCredentialOutcome.Unavailable);
                }
                ok &= Check(sb, "abi180: store: WITNESS once the lock is free the same save commits",
                    race.TrySave("aibrain", HostCredentialId.ClaudeCodeSignIn, SyntheticToken + "x").Revision == heldRevision + 1);

                // REVOKED: the next call is refused at once; the grant is read on every call, never cached.
                HostCredentialStore.SetGrant(dir, "remembrance", false, null);
                ok &= Check(sb, "abi180: store: a revoked grant refuses the module's next call ConsentRequired, and leaves the token for the others",
                    creds.ReadCredential("remembrance", HostCredentialId.ClaudeCodeSignIn).State == HostCredentialState.ConsentRequired &&
                    creds.ReadCredential("aibrain", HostCredentialId.ClaudeCodeSignIn).Value == SyntheticOther);

                // AN UNINSTALL drops the removed module's grant when the removal is applied, and only its own; the saved
                // token stays (uninstalling is not Remove token). The shipped ProcessPending, against this run's data root,
                // and ONLY there (the independent review's P3-11): it writes a token and grants and processes the pending
                // removals, so against a data root that is not this run's own scratch it would replace a user's real token
                // and delete a real module's data. Not run, and a FAIL, unless the isolation is proved here.
                bool isolated = AppPaths.IsDataRootOverridden && !string.IsNullOrEmpty(isolatedDataRoot) &&
                                SamePath(AppPaths.DataRoot, isolatedDataRoot);
                if (!isolated)
                    ok &= Check(sb, "abi180: store: the uninstall check runs only against this run's own data root; not run, nothing written (" +
                        AppPaths.DataRoot + ")", false);
                else
                {
                    string shipped = HostCredentialStore.DefaultDirectory;
                    var shippedStore = new HostCredentialStore(shipped, delegate (string id) { return true; }, null);
                    HostCredentialStore.SetGrant(shipped, "abi180-gone", true, null);
                    HostCredentialStore.SetGrant(shipped, "abi180-kept", true, null);
                    shippedStore.TrySave("abi180-kept", HostCredentialId.ClaudeCodeSignIn, SyntheticToken);
                    PendingModuleRemovals.MarkForRemoval("abi180-gone");
                    string removalRoot = SelfTestScratch.Create("credentials-uninstall");
                    PendingModuleRemovals.ProcessPending(removalRoot, null);
                    ok &= Check(sb, "abi180: store: an applied uninstall drops that module's grant alone, and the saved token stays for the others",
                        !HostCredentialStore.IsGranted(shipped, "abi180-gone") && HostCredentialStore.IsGranted(shipped, "abi180-kept") &&
                        shippedStore.Read("abi180-kept", HostCredentialId.ClaudeCodeSignIn).Value == SyntheticToken);

                    // THE LAUNCH PATH WITH A MARKER IT CANNOT READ (the 2026-10-29 audit, A-host-01): the same shipped
                    // ProcessPending, its removal marker held open with no sharing (a scanner's hold at start). It must answer,
                    // remove nothing and leave the grant; it threw a NullReferenceException, which cost the launch every module.
                    PendingModuleRemovals.MarkForRemoval("abi180-held");
                    HostCredentialStore.SetGrant(shipped, "abi180-held", true, null);
                    string heldOutcome;
                    IReadOnlyList<string> heldUnfinished = null;
                    var heldLog = new List<string>();
                    using (new FileStream(PendingModuleRemovals.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        try
                        {
                            heldUnfinished = PendingModuleRemovals.ProcessPending(removalRoot, delegate (string line) { heldLog.Add(line); });
                            heldOutcome = "answered";
                        }
                        catch (Exception heldEx) { heldOutcome = heldEx.GetType().Name; }
                    }
                    ok &= Check(sb, "abi180: store: a launch whose removal marker cannot be read answers, removes nothing and keeps the module's grant (" +
                        heldOutcome + "; " + string.Join(" | ", heldLog) + ")",
                        heldOutcome == "answered" && heldUnfinished != null && heldUnfinished.Count == 0 &&
                        HostCredentialStore.IsGranted(shipped, "abi180-held") && PendingModuleRemovals.IsMarked("abi180-held"));
                    PendingModuleRemovals.ProcessPending(removalRoot, null);
                    ok &= Check(sb, "abi180: store: WITNESS once the marker can be read the same launch path removes it and drops its grant",
                        !HostCredentialStore.IsGranted(shipped, "abi180-held") && !PendingModuleRemovals.IsMarked("abi180-held"));
                    SelfTestScratch.TryRelease(removalRoot, out ignoredRelease);
                }

                var leaked = new List<string>();
                lock (log)
                    foreach (string line in log)
                        if (line.IndexOf("SYNTHETIC", StringComparison.Ordinal) >= 0 || line.IndexOf("has a space", StringComparison.Ordinal) >= 0) leaked.Add(line);
                ok &= Check(sb, "abi180: store: no log line carries a value (" + log.Count + " lines" + (leaked.Count > 0 ? "; leaked: " + leaked[0] : "") + ")",
                    log.Count >= 8 && leaked.Count == 0);
                SelfTestScratch.TryRelease(statesDir, out ignoredRelease);
                SelfTestScratch.TryRelease(casDir, out ignoredRelease);
                SelfTestScratch.TryRelease(raceDir, out ignoredRelease);
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("EXC (shared sign-in store): " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                string releaseDetail;
                SelfTestScratch.TryRelease(dir, out releaseDetail);
            }
            return ok;
        }

        // The Modules pane's available-row tooltip names the declarative flags from the enum (2026-10-29): every single-bit
        // flag is either enforced or named there, never both and never neither.
        private static bool DeclarativeFlagList(StringBuilder sb)
        {
            var named = new HashSet<string>(DesktopAICompanion.Wpf.ModulesPaneControl.DeclarativePermissionNames()
                .Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries));
            bool ok = true;
            var missed = new List<string>();
            var both = new List<string>();
            foreach (ModulePermissions flag in Enum.GetValues(typeof(ModulePermissions)))
            {
                long bits = Convert.ToInt64(flag);
                if (bits == 0 || (bits & (bits - 1)) != 0) continue;
                bool enforced = (DesktopAICompanion.Wpf.ModulesPaneControl.EnforcedPermissions & flag) != 0;
                if (enforced && named.Contains(flag.ToString())) both.Add(flag.ToString());
                if (!enforced && !named.Contains(flag.ToString())) missed.Add(flag.ToString());
            }
            ok &= Check(sb, "modules pane: the tooltip names every declarative flag (" + string.Join(",", missed) + " missed)",
                missed.Count == 0 && named.Contains("Microphone") && named.Contains("SystemAudio") && named.Contains("InputSynthesis"));
            ok &= Check(sb, "WITNESS modules pane: no enforced flag is named as a statement (" + string.Join(",", both) + ")",
                both.Count == 0 && !named.Contains("Network") && !named.Contains("ClaudeCodeSignIn"));
            return ok;
        }

        /// <summary>
        /// The independent review of 2026-10-29: an I/O error is never content (a held envelope fails an import over a removed
        /// store as Unavailable, the file untouched; a held or damaged grants.json is Unavailable, never "not allowed", and is
        /// never rewritten), the gate is re-checked under the lock, a refused read is logged once, the same sealed value is
        /// unsealed once, the host's token rule is the runner's, and Remove's answer for an unusable value matches the doc.
        /// Pixel-free: held files are FileStreams opened with FileShare.None in this process.
        /// </summary>
        private static bool StoreReviewFixes(StringBuilder sb)
        {
            bool ok = true;
            string dir = SelfTestScratch.Create("credentials-review");
            try
            {
                var log = new List<string>();
                var store = new HostCredentialStore(dir, delegate (string id) { return true; },
                    delegate (string id, string line) { lock (log) log.Add((id ?? "") + ": " + line); });
                HostCredentialStore.SetGrant(dir, "aibrain", true, null);
                string envelope = Path.Combine(dir, HostCredentialStore.ClaudeCodeSignInFileName);
                string grants = Path.Combine(dir, HostCredentialStore.GrantsFileName);
                store.TrySave("aibrain", HostCredentialId.ClaudeCodeSignIn, SyntheticToken);
                store.Remove("aibrain", HostCredentialId.ClaudeCodeSignIn);
                byte[] removedBytes = File.ReadAllBytes(envelope);

                // P2-1: an envelope another program holds without read sharing. Nothing is decided over it.
                var held = new List<string>();
                using (new FileStream(envelope, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    held.Add(store.Read("aibrain", HostCredentialId.ClaudeCodeSignIn).State.ToString());
                    held.Add(store.ImportIfUnset("aibrain", HostCredentialId.ClaudeCodeSignIn, SyntheticOther).Outcome.ToString());
                    held.Add(store.TrySave("aibrain", HostCredentialId.ClaudeCodeSignIn, SyntheticOther).Outcome.ToString());
                    held.Add(store.Remove("aibrain", HostCredentialId.ClaudeCodeSignIn).Outcome.ToString());
                }
                ok &= Check(sb, "abi180: review: an envelope another program holds is Unavailable to every verb, and an import never lands over the removal in it (" +
                    string.Join(", ", held.ToArray()) + ")",
                    string.Join(",", held.ToArray()) == "Unavailable,Unavailable,Unavailable,Unavailable" &&
                    Convert.ToBase64String(File.ReadAllBytes(envelope)) == Convert.ToBase64String(removedBytes));
                ok &= Check(sb, "abi180: review: WITNESS once the file is free the same import is refused for the removal, not taken",
                    store.ImportIfUnset("aibrain", HostCredentialId.ClaudeCodeSignIn, SyntheticOther).Outcome == HostCredentialOutcome.NotImported);

                // P2-1 and P3-15: a grants record that cannot be read, or is damaged, is no answer: Unavailable, never "not
                // allowed", and never rewritten from what could be read.
                HostCredentialReading whileGrantsHeld;
                bool recordedWhileHeld;
                using (new FileStream(grants, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    whileGrantsHeld = store.Read("aibrain", HostCredentialId.ClaudeCodeSignIn);
                    recordedWhileHeld = HostCredentialStore.SetGrant(dir, "remembrance", true, null);
                }
                ok &= Check(sb, "abi180: review: a grants record another program holds reads Unavailable, never not allowed, and records nothing (" +
                    whileGrantsHeld.State + ")",
                    whileGrantsHeld.State == HostCredentialState.Unavailable && !recordedWhileHeld && !HostCredentialStore.IsGranted(dir, "remembrance"));
                byte[] goodGrants = File.ReadAllBytes(grants);
                File.WriteAllText(grants, "{ damaged");
                byte[] damagedGrants = File.ReadAllBytes(grants);
                HostCredentialReading whileDamaged = store.Read("aibrain", HostCredentialId.ClaudeCodeSignIn);
                bool recordedOverDamage = HostCredentialStore.SetGrant(dir, "remembrance", true, null);
                List<string> asked = SignInGrantQuestion.ModulesToAsk(new List<ModuleInfo> { SignInModule("remembrance", true) }, dir);
                ok &= Check(sb, "abi180: review: a damaged grants record reads Unavailable, is kept byte for byte rather than rewritten, and no start question is asked (" +
                    whileDamaged.State + ", " + asked.Count + " asked)",
                    whileDamaged.State == HostCredentialState.Unavailable && !recordedOverDamage && asked.Count == 0 &&
                    Convert.ToBase64String(File.ReadAllBytes(grants)) == Convert.ToBase64String(damagedGrants));
                File.WriteAllBytes(grants, goodGrants);

                // P3-13: a Revoke while a change waits for the lock refuses that change.
                store.LockTimeout = 5000;
                HostCredentialOutcome waited = HostCredentialOutcome.Saved;
                using (IDisposable lease = DesktopAICompanion.ModuleKit.CrossSessionLock.TryAcquire(
                    DesktopAICompanion.ModuleKit.CrossSessionLock.BuildGlobalMutexName("credentials", envelope), envelope, 2000))
                {
                    var saver = new System.Threading.Thread(delegate ()
                    {
                        waited = store.TrySave("aibrain", HostCredentialId.ClaudeCodeSignIn, SyntheticToken).Outcome;
                    });
                    saver.Start();
                    System.Threading.Thread.Sleep(400);
                    HostCredentialStore.SetGrant(dir, "aibrain", false, null);
                    lease.Dispose();
                    saver.Join(20000);
                }
                ok &= Check(sb, "abi180: review: a Revoke made while a save waits for the lock refuses that save, and nothing is written (" + waited + ")",
                    waited == HostCredentialOutcome.ConsentRequired &&
                    Convert.ToBase64String(File.ReadAllBytes(envelope)) == Convert.ToBase64String(removedBytes));
                HostCredentialStore.SetGrant(dir, "aibrain", true, null);

                // P3-8: a refused read is logged once per module and reason; the same sealed value is unsealed once.
                var refusing = new HostCredentialStore(dir, delegate (string id) { return true; },
                    delegate (string id, string line) { lock (log) log.Add((id ?? "") + ": " + line); });
                for (int i = 0; i < 5; i++) refusing.Read("ungranted", HostCredentialId.ClaudeCodeSignIn);
                int refusedLines = 0;
                lock (log) foreach (string line in log) if (line == "ungranted: credentials: read refused: not-allowed") refusedLines++;
                store.TrySave("aibrain", HostCredentialId.ClaudeCodeSignIn, SyntheticToken);
                int unsealsBefore = store.UnsealsForDiagnostics;
                for (int i = 0; i < 5; i++) store.Read("aibrain", HostCredentialId.ClaudeCodeSignIn);
                int unseals = store.UnsealsForDiagnostics - unsealsBefore;
                ok &= Check(sb, "abi180: review: five refused reads log one line, and five reads of one saved value unseal it once (" +
                    refusedLines + " lines, " + unseals + " unseals)",
                    refusedLines == 1 && unseals == 1 && store.Read("aibrain", HostCredentialId.ClaudeCodeSignIn).Value == SyntheticToken);

                // P3-9: the host's rule is the runner's: an API key, a web address and a character outside a bearer token are refused.
                var refusedValues = new List<string>();
                foreach (string bad in new[] { "sk-ant-api03-SYNTHETIC-key", "https://example.invalid/x", "www.example.invalid", "token!with?marks" })
                    refusedValues.Add(store.TrySave("aibrain", HostCredentialId.ClaudeCodeSignIn, bad).Outcome.ToString());
                ok &= Check(sb, "abi180: review: the host refuses an API key, a web address and a non-token character, as the runner does (" +
                    string.Join(", ", refusedValues.ToArray()) + ")",
                    string.Join(",", refusedValues.ToArray()) == "Refused,Refused,Refused,Refused" &&
                    store.Read("aibrain", HostCredentialId.ClaudeCodeSignIn).Value == SyntheticToken);

                // P3-14: Remove of an unusable value is Removed, as the contract now says ("nothing was saved" is NothingToRemove).
                string unusable = Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes("https://not a token"),
                    HostCredentialStore.Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser));
                File.WriteAllText(envelope, "{\"schema\":1,\"revision\":40,\"state\":\"saved\",\"sealed\":\"" + unusable + "\"}");
                HostCredentialCommit removedUnusable = store.Remove("aibrain", HostCredentialId.ClaudeCodeSignIn);
                ok &= Check(sb, "abi180: review: Remove of a saved value that is no token answers Removed, as the contract says (" + removedUnusable.Outcome + ")",
                    removedUnusable.Outcome == HostCredentialOutcome.Removed && removedUnusable.Revision == 41);

                // THE UNSEAL CACHE LETS GO (the 2026-10-29 audit, A-host-05, BACKLOG N-audit-20261029-12). Read straight
                // after each commit, before any read could replace what the cache holds: a save lets go of the token it
                // replaced, and a Remove of the one it removed; a Remove made through another store on the same folder (a
                // second window of the app) is let go by this store's own Remove, or by its next read.
                store.TrySave("aibrain", HostCredentialId.ClaudeCodeSignIn, SyntheticToken);
                bool cacheFilled = store.Read("aibrain", HostCredentialId.ClaudeCodeSignIn).Value == SyntheticToken && store.HoldsUnsealedForDiagnostics;
                store.TrySave("aibrain", HostCredentialId.ClaudeCodeSignIn, SyntheticOther);
                bool saveLetGo = !store.HoldsUnsealedForDiagnostics;
                bool cacheRefilled = store.Read("aibrain", HostCredentialId.ClaudeCodeSignIn).Value == SyntheticOther && store.HoldsUnsealedForDiagnostics;
                HostCredentialCommit cacheRemove = store.Remove("aibrain", HostCredentialId.ClaudeCodeSignIn);
                bool removeLetGo = !store.HoldsUnsealedForDiagnostics;
                var otherWindow = new HostCredentialStore(dir, delegate (string id) { return true; }, null);
                store.TrySave("aibrain", HostCredentialId.ClaudeCodeSignIn, SyntheticToken);
                store.Read("aibrain", HostCredentialId.ClaudeCodeSignIn);
                HostCredentialCommit otherRemove = otherWindow.Remove("aibrain", HostCredentialId.ClaudeCodeSignIn);
                bool heldAfterOtherRemove = store.HoldsUnsealedForDiagnostics;
                HostCredentialCommit ownRemove = store.Remove("aibrain", HostCredentialId.ClaudeCodeSignIn);
                bool ownRemoveLetGo = !store.HoldsUnsealedForDiagnostics;
                store.TrySave("aibrain", HostCredentialId.ClaudeCodeSignIn, SyntheticOther);
                store.Read("aibrain", HostCredentialId.ClaudeCodeSignIn);
                otherWindow.Remove("aibrain", HostCredentialId.ClaudeCodeSignIn);
                bool heldBeforeRead = store.HoldsUnsealedForDiagnostics;
                HostCredentialReading readAfterOther = store.Read("aibrain", HostCredentialId.ClaudeCodeSignIn);
                bool readLetGo = !store.HoldsUnsealedForDiagnostics;
                ok &= Check(sb, "audit-host: unseal cache: WITNESS a read fills the cache, so the checks beside it are not vacuous",
                    cacheFilled && cacheRefilled && heldAfterOtherRemove && heldBeforeRead);
                ok &= Check(sb, "audit-host: unseal cache: a save lets go of the token it replaced before the new one is read (" +
                    (saveLetGo ? "nothing held" : "the old token held") + ")", saveLetGo);
                ok &= Check(sb, "audit-host: unseal cache: a Remove leaves no plaintext of the removed token in the store (" +
                    cacheRemove.Outcome + ", " + (removeLetGo ? "nothing held" : "the token held") + ")",
                    cacheRemove.Outcome == HostCredentialOutcome.Removed && removeLetGo);
                ok &= Check(sb, "audit-host: unseal cache: a Remove after another window's lets go of what this store held (" +
                    otherRemove.Outcome + ", " + ownRemove.Outcome + ", " + (ownRemoveLetGo ? "nothing held" : "the token held") + ")",
                    otherRemove.Outcome == HostCredentialOutcome.Removed && ownRemove.Outcome == HostCredentialOutcome.NothingToRemove && ownRemoveLetGo);
                ok &= Check(sb, "audit-host: unseal cache: a read after another window's Remove lets go of what this store held (" +
                    readAfterOther.State + ", " + (readLetGo ? "nothing held" : "the token held") + ")",
                    readAfterOther.State == HostCredentialState.None && readLetGo);

                // THE MODULES ROW'S READ OF THE GRANT (the 2026-10-29 audit, A-host-06, BACKLOG N-audit-20261029-13): the
                // shipped seam and the row's words, pixel-free. A grants record held or damaged is no answer (null), as the
                // store's gate reads it, and the row then says it cannot be read and offers no button; it said "Not allowed"
                // and offered an Allow that failed.
                bool? rowAllowed = DesktopAICompanion.Wpf.ModulesPaneControl.SignInGrantAnswer(dir, "aibrain");
                bool? rowNotAllowed = DesktopAICompanion.Wpf.ModulesPaneControl.SignInGrantAnswer(dir, "ungranted");
                bool? rowWhileHeld;
                using (new FileStream(grants, FileMode.Open, FileAccess.Read, FileShare.None))
                    rowWhileHeld = DesktopAICompanion.Wpf.ModulesPaneControl.SignInGrantAnswer(dir, "aibrain");
                byte[] rowGoodGrants = File.ReadAllBytes(grants);
                File.WriteAllText(grants, "{ damaged");
                bool? rowWhileDamaged = DesktopAICompanion.Wpf.ModulesPaneControl.SignInGrantAnswer(dir, "aibrain");
                File.WriteAllBytes(grants, rowGoodGrants);
                string allowCaption, revokeCaption, heldCaption;
                string notAllowedLine = DesktopAICompanion.Wpf.ModulesPaneControl.SignInRowText(rowNotAllowed, out allowCaption);
                DesktopAICompanion.Wpf.ModulesPaneControl.SignInRowText(rowAllowed, out revokeCaption);
                string heldLine = DesktopAICompanion.Wpf.ModulesPaneControl.SignInRowText(rowWhileHeld, out heldCaption);
                ok &= Check(sb, "audit-host: modules row: WITNESS a grants record that can be read answers allowed and not allowed, offering Revoke and Allow (" +
                    revokeCaption + ", " + allowCaption + ")",
                    rowAllowed == true && rowNotAllowed == false && revokeCaption == "Revoke sign-in" && allowCaption == "Allow sign-in" &&
                    notAllowedLine == "Not allowed the shared Claude Code sign-in.");
                ok &= Check(sb, "audit-host: modules row: a grants record held or damaged is no answer, said as unreadable, with no button (" +
                    (rowWhileHeld.HasValue ? rowWhileHeld.ToString() : "no answer") + ", " + (rowWhileDamaged.HasValue ? rowWhileDamaged.ToString() : "no answer") +
                    ", " + (heldCaption ?? "no button") + ")",
                    !rowWhileHeld.HasValue && !rowWhileDamaged.HasValue && heldCaption == null && heldLine.Contains("can't be read just now"));
                // BACKLOG N-audit-host-01: nothing in the app clears such a record, so the line names its real file and the two
                // ways out, and says the saved token is not part of it.
                string recordFile = Path.Combine(HostCredentialStore.DefaultDirectory, HostCredentialStore.GrantsFileName);
                ok &= Check(sb, "final-host: modules row: an unreadable grants record names its file and the ways out: " + heldLine,
                    heldLine.Contains("delete " + recordFile + " (") && heldLine.Contains("Allow and Don't ask again go with it") &&
                    heldLine.Contains("the saved token stays") && heldLine.Contains("\"Clear all settings and modules\""));

                // P3-5: the start question for a long name still fits, and names the module's id beside it.
                string longName = new string('N', 120);
                TrayQuestion longQuestion = SignInGrantQuestion.For(longName, "longmodule");
                QuestionEntry entry;
                string reason;
                bool longAccepted = TrayQuestionRules.TryAccept(longQuestion, DateTimeOffset.UtcNow, out entry, out reason);
                TrayQuestion plain = SignInGrantQuestion.For("AI Brain", "aibrain");
                ok &= Check(sb, "abi180: review: a module name of 120 characters still gives a question the queue takes, shortened, with the id beside it (" +
                    (longAccepted ? longQuestion.Text.Length + " characters" : "refused: " + reason) + ")",
                    longAccepted && longQuestion.Text.Contains("… (longmodule)") && plain.Text.StartsWith("Allow AI Brain (aibrain) to use", StringComparison.Ordinal));

                // The Allow that could not be recorded: a second card says so and offers one try again.
                string retryDir = SelfTestScratch.Create("credentials-retry");
                var retryStore = new HostCredentialStore(retryDir, delegate (string id) { return true; }, null);
                var retryAsked = new List<string>();
                string retryGrants = Path.Combine(retryDir, HostCredentialStore.GrantsFileName);
                var retryHost = new CompanionHost(null, retryStore)
                {
                    SignInAskForDiagnostics = delegate (string id, TrayQuestion q)
                    {
                        lock (retryAsked) retryAsked.Add(q.Key + "|" + q.Text);
                        // The record goes bad while the first card is on screen, so the Allow cannot be written.
                        if (q.Key == SignInGrantQuestion.Key) File.WriteAllText(retryGrants, "{ damaged");
                        return System.Threading.Tasks.Task.FromResult(new TrayAnswer
                        {
                            Outcome = TrayQuestionOutcome.Chosen,
                            ChoiceId = q.Key == SignInGrantQuestion.Key ? SignInGrantQuestion.AllowChoice : SignInGrantQuestion.RetryChoice,
                        });
                    },
                };
                retryHost.AskForSignInGrantsAsync(new List<ModuleInfo> { SignInModule("aibrain", true) }).Wait(20000);
                ok &= Check(sb, "abi180: review: an Allow that could not be recorded is said on a second card with one Try again, and nothing is granted (" +
                    retryAsked.Count + " cards)",
                    retryAsked.Count == 2 && retryAsked[1] == SignInGrantQuestion.RetryKey + "|" +
                        ModulePermissionConsent.RetryQuestionText("AI Brain (aibrain)") && !HostCredentialStore.IsGranted(retryDir, "aibrain"));
                File.Delete(retryGrants);
                var retryOk = new List<string>();
                var retryHost2 = new CompanionHost(null, retryStore)
                {
                    SignInAskForDiagnostics = delegate (string id, TrayQuestion q)
                    {
                        lock (retryOk) retryOk.Add(q.Key);
                        return System.Threading.Tasks.Task.FromResult(new TrayAnswer { Outcome = TrayQuestionOutcome.Chosen, ChoiceId = SignInGrantQuestion.AllowChoice });
                    },
                };
                retryHost2.AskForSignInGrantsAsync(new List<ModuleInfo> { SignInModule("aibrain", true) }).Wait(20000);
                ok &= Check(sb, "abi180: review: WITNESS an Allow that is recorded asks no second card",
                    retryOk.Count == 1 && HostCredentialStore.IsGranted(retryDir, "aibrain"));
                string ignoredRetry;
                SelfTestScratch.TryRelease(retryDir, out ignoredRetry);
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("EXC (store review fixes): " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                string releaseDetail;
                SelfTestScratch.TryRelease(dir, out releaseDetail);
            }
            return ok;
        }

        private static ModuleInfo SignInModule(string id, bool declares)
        {
            return new ModuleInfo
            {
                Id = id,
                Name = id == "aibrain" ? "AI Brain" : id,
                Version = "1.0.0",
                Permissions = ModulePermissions.Speech | (declares ? ModulePermissions.ClaudeCodeSignIn : ModulePermissions.None),
            };
        }

        /// <summary>
        /// The coordinator's finding of 2026-10-29: a module the MSI installs, or one placed by hand, never passes the Modules
        /// pane's update question, so it reached the shared sign-in with no grant and its Claude Code calls stopped. The start
        /// question asks for each such module, through the host's question queue; checked here through CompanionHost's own
        /// asking code, with a stand-in for the card answering as the user would, so no pixel is involved.
        /// </summary>
        private static bool SignInStartQuestion(StringBuilder sb)
        {
            bool ok = true;
            string dir = SelfTestScratch.Create("credentials-start");
            try
            {
                var store = new HostCredentialStore(dir, delegate (string id) { return true; }, null);
                HostCredentialStore.SetGrant(dir, "already", true, null);
                HostCredentialStore.SetNotAsked(dir, "declined", null);
                var loaded = new List<ModuleInfo>
                {
                    SignInModule("aibrain", true), SignInModule("remembrance", true), SignInModule("fortunes", false),
                    SignInModule("already", true), SignInModule("declined", true),
                };
                var answers = new Dictionary<string, TrayAnswer>(StringComparer.OrdinalIgnoreCase)
                {
                    { "aibrain", new TrayAnswer { Outcome = TrayQuestionOutcome.Chosen, ChoiceId = SignInGrantQuestion.AllowChoice, WasShown = true } },
                    { "remembrance", new TrayAnswer { Outcome = TrayQuestionOutcome.TimedOut, ChoiceId = SignInGrantQuestion.NotNowChoice, WasShown = true } },
                };
                var asked = new List<string>();
                var questions = new List<TrayQuestion>();
                Func<string, TrayQuestion, System.Threading.Tasks.Task<TrayAnswer>> card = delegate (string id, TrayQuestion asking)
                {
                    lock (asked) { asked.Add(id); questions.Add(asking); }
                    TrayAnswer a;
                    return System.Threading.Tasks.Task.FromResult(answers.TryGetValue(id, out a) ? a : new TrayAnswer { Outcome = TrayQuestionOutcome.Cancelled });
                };
                var host = new CompanionHost(null, store) { SignInAskForDiagnostics = card };
                bool ended = host.AskForSignInGrantsAsync(loaded).Wait(20000);
                ok &= Check(sb, "abi180: start question: asked once for each loaded module declaring ClaudeCodeSignIn with no recorded answer, and for no other (" +
                    string.Join(", ", asked.ToArray()) + ")",
                    ended && string.Join(",", asked.ToArray()) == "aibrain,remembrance");
                ok &= Check(sb, "abi180: start question: the user's press of Allow records the grant",
                    HostCredentialStore.IsGranted(dir, "aibrain") && store.Read("aibrain", HostCredentialId.ClaudeCodeSignIn).State == HostCredentialState.None);
                ok &= Check(sb, "abi180: start question: a question nobody answered records nothing, so it is asked again at the next start",
                    !HostCredentialStore.IsGranted(dir, "remembrance") && !HostCredentialStore.IsNotAsked(dir, "remembrance"));

                host.AskForSignInGrantsAsync(loaded).Wait(20000);
                ok &= Check(sb, "abi180: start question: at most one question per module per start (" + asked.Count + " asked)", asked.Count == 2);
                var nextStart = new CompanionHost(null, store) { SignInAskForDiagnostics = card };
                nextStart.AskForSignInGrantsAsync(loaded).Wait(20000);
                ok &= Check(sb, "abi180: start question: WITNESS the next start asks again for the one left unanswered, and only for it (" +
                    string.Join(", ", asked.ToArray()) + ")",
                    string.Join(",", asked.ToArray()) == "aibrain,remembrance,remembrance");

                // NEVER A SILENT GRANT: anything but the user's press of Allow records nothing, even an answer carrying Allow's id.
                var silent = new List<string>();
                foreach (TrayQuestionOutcome outcome in new[] { TrayQuestionOutcome.TimedOut, TrayQuestionOutcome.Expired, TrayQuestionOutcome.Withdrawn,
                                                                TrayQuestionOutcome.Cancelled, TrayQuestionOutcome.Refused })
                {
                    string id = "silent-" + outcome.ToString().ToLowerInvariant();
                    SignInGrantQuestion.Apply(dir, id, new TrayAnswer { Outcome = outcome, ChoiceId = SignInGrantQuestion.AllowChoice }, null);
                    if (HostCredentialStore.IsGranted(dir, id) || HostCredentialStore.IsNotAsked(dir, id)) silent.Add(outcome.ToString());
                }
                SignInGrantQuestion.Apply(dir, "silent-null", null, null);
                ok &= Check(sb, "abi180: start question: never a silent grant: no answer but the user's press of Allow records one, even one carrying Allow's id" +
                    (silent.Count > 0 ? " (granted on " + string.Join(", ", silent.ToArray()) + ")" : ""),
                    silent.Count == 0 && !HostCredentialStore.IsGranted(dir, "silent-null"));

                // DON'T ASK AGAIN: a no recorded, no grant, and not asked again; the row stays the place to change it.
                string never = SignInGrantQuestion.Apply(dir, "remembrance",
                    new TrayAnswer { Outcome = TrayQuestionOutcome.Chosen, ChoiceId = SignInGrantQuestion.NeverChoice }, null);
                var afterNever = new CompanionHost(null, store) { SignInAskForDiagnostics = card };
                int askedBefore = asked.Count;
                afterNever.AskForSignInGrantsAsync(loaded).Wait(20000);
                ok &= Check(sb, "abi180: start question: Don't ask again records a no and no grant, and the next start does not ask (" + never + ")",
                    !HostCredentialStore.IsGranted(dir, "remembrance") && HostCredentialStore.IsNotAsked(dir, "remembrance") && asked.Count == askedBefore);
                bool notNow = SignInGrantQuestion.Apply(dir, "pressed-not-now",
                    new TrayAnswer { Outcome = TrayQuestionOutcome.Chosen, ChoiceId = SignInGrantQuestion.NotNowChoice }, null) == "not now";
                ok &= Check(sb, "abi180: start question: WITNESS Not now records nothing",
                    notNow && !HostCredentialStore.IsGranted(dir, "pressed-not-now") && !HostCredentialStore.IsNotAsked(dir, "pressed-not-now"));
                HostCredentialStore.SetGrant(dir, "remembrance", true, null);
                bool allowClears = HostCredentialStore.IsGranted(dir, "remembrance") && !HostCredentialStore.IsNotAsked(dir, "remembrance");
                HostCredentialStore.SetGrant(dir, "remembrance", false, null);
                bool revokeAnswers = !HostCredentialStore.IsGranted(dir, "remembrance") && HostCredentialStore.IsNotAsked(dir, "remembrance");
                HostCredentialStore.ForgetGrant(dir, "remembrance", null);
                bool forgotten = !HostCredentialStore.IsGranted(dir, "remembrance") && !HostCredentialStore.IsNotAsked(dir, "remembrance");
                ok &= Check(sb, "abi180: start question: Allow on the row clears a recorded no, Revoke records one, and an uninstall forgets both",
                    allowClears && revokeAnswers && forgotten);

                // THE QUESTION ITSELF, as the host's queue takes it.
                TrayQuestion q = questions.Count > 0 ? questions[0] : null;
                QuestionEntry entry;
                string reason = "(none asked)";
                bool accepted = q != null && TrayQuestionRules.TryAccept(q, DateTimeOffset.UtcNow, out entry, out reason);
                string choices = q == null ? "" : string.Join(", ", new List<TrayChoice>(q.Choices).ConvertAll<string>(delegate (TrayChoice c) { return c.Id + "=" + c.Label; }).ToArray());
                ok &= Check(sb, "abi180: start question: the queue takes it; it names the module and says what the grant allows; Allow, Not now and Don't ask again; it times out to Not now and waits while held (" +
                    (accepted ? choices : "refused: " + reason) + ")",
                    accepted && q.Text.StartsWith("Allow AI Brain (aibrain) to use the shared Claude Code sign-in?", StringComparison.Ordinal) &&
                    q.Text.Contains("read, replace and remove") && q.Text.Contains("on its row in Settings, Modules") &&
                    choices == "allow=Allow, not-now=Not now, never=Don't ask again" &&
                    q.TimeoutChoiceId == SignInGrantQuestion.NotNowChoice && !q.RunsWhileHeld);

                // The shipped path, with no app behind it: AskAsync answers Refused and nothing is recorded.
                string bareDir = SelfTestScratch.Create("credentials-start-bare");
                var bare = new CompanionHost(null, new HostCredentialStore(bareDir, delegate (string id) { return true; }, null));
                bool bareEnded = bare.AskForSignInGrantsAsync(new List<ModuleInfo> { SignInModule("aibrain", true) }).Wait(20000);
                ok &= Check(sb, "abi180: start question: WITNESS with no question queue behind it the shipped ask is refused and records nothing",
                    bareEnded && !HostCredentialStore.IsGranted(bareDir, "aibrain") && !HostCredentialStore.IsNotAsked(bareDir, "aibrain"));
                string ignoredBare;
                SelfTestScratch.TryRelease(bareDir, out ignoredBare);
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("EXC (start question): " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                string releaseDetail;
                SelfTestScratch.TryRelease(dir, out releaseDetail);
            }
            return ok;
        }

        private static bool HasModule(ModuleHost loader, string id)
        {
            return FindModule(loader, id) != null;
        }
        private static IModule FindModule(ModuleHost loader, string id)
        {
            foreach (IModule m in loader.Modules)
                if (m.Info != null && string.Equals(m.Info.Id, id, StringComparison.OrdinalIgnoreCase)) return m;
            return null;
        }
        /// <summary>The Fortunes module's smart status line (what its pane's "Rebuild smart index" status shows),
        /// read through its internal SmartStatusTextForDiagnostics seam by reflection: null when the module or the
        /// seam is missing, an "EXC: ..." string when the seam throws. Shared by the three self-tests whose fakes
        /// seed smartFortunes=false (RA-284), which assert "Smart picks are off" on it after Init.</summary>
        internal static string SmartStatusOf(object module)
        {
            if (module == null) return null;
            MethodInfo seam = module.GetType().GetMethod("SmartStatusTextForDiagnostics",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (seam == null) return null;
            try { return seam.Invoke(module, null) as string; }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                return "EXC: " + inner.GetType().Name + ": " + inner.Message;
            }
        }
        private static bool Check(StringBuilder sb, string name, bool cond) { sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name); return cond; }
        private static bool Finish(StringBuilder sb, bool ok)
        {
            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dp-module-host-selftest.txt"), sb.ToString()); } catch { }
            // To stdout as well, like its six siblings: the gate's red-run diagnostics come from the redirected
            // stdout log, which for this flag was empty, so a failure printed no FAIL line at all (F348).
            Console.Out.Write(sb.ToString());
            return ok;
        }

        private sealed class FakeCompanion : ICompanion { public int Id { get { return 1; } } public bool IsBusy { get { return false; } } public string TypeId { get { return ""; } } }

        /// <summary>A headless IHost that records what modules do, for the self-test.</summary>
        private sealed class RecordingHost : IHost
        {
            // Settable so the MinHostVersion gate can be exercised by lying about the HOST's version, which
            // avoids needing a purpose-built too-new module DLL on disk. Defaults to "9999.0.0", which parses
            // and satisfies every module's MinHostVersion; the unparseable-host path is driven by the explicit
            // "selftest" value in MinHostVersionGate (F346).
            public string HostVersionValue = "9999.0.0";
            public string HostVersion { get { return HostVersionValue; } }
            public bool SpeechEnabled { get { return true; } }
            public double Volume { get { return 0.5; } }
            public string OwnerName { get { return ""; } }
            public void SetOwnerName(string name) { }
            public string LastSayAll;
            public readonly List<TrayItem> TrayItems = new List<TrayItem>();
            public readonly List<OptionsPane> OptionsPanes = new List<OptionsPane>();

            public event Action<ICompanion> CompanionSpawned;
            public event Action<PokeInfo> CompanionPoked;
            public event Action<ICompanion> CompanionLanded;
            public event Action HostShutdown;
            public void RaiseCompanionPoked(PokeInfo p) { var h = CompanionPoked; if (h != null) h(p); }
            // (Other Raise* omitted: the self-test only exercises CompanionPoked; referencing the events keeps the
            //  compiler from warning them unused.)
            // Never called: it exists so the events count as "used" under TreatWarningsAsErrors (CS0067).
            internal void TouchEvents() { CompanionSpawned?.Invoke(null); CompanionLanded?.Invoke(null); HostShutdown?.Invoke(); }

            // Split, because Say and SayAll both writing LastSayAll made "did the module route this line to
            // one pet, or broadcast it to all of them?" unassertable -- which is precisely the distinction
            // this release exists to introduce. LastSayAll stays as the union so existing assertions read
            // unchanged; LastSay/LastSayPet carry the routing, which Run asserts after the poke (F350).
            public string LastSay;
            public ICompanion LastSayPet;
            public void Say(ICompanion pet, string text) { LastSay = text; LastSayPet = pet; LastSayAll = text; }
            public void SayAll(string text) { LastSayAll = text; }
            public void Say(ICompanion pet, string text, DesktopAICompanion.Modules.SpeechStyle style) { Say(pet, text); }
            public void SayAll(string text, DesktopAICompanion.Modules.SpeechStyle style) { SayAll(text); }
            public bool TryPlayAnimation(ICompanion pet, string animationName) { return true; }
            public void PlayAnimationAll(IReadOnlyList<string> animationCandidates) { }
            public ScreenContext CaptureScreenContext(ICompanion pet) { return new ScreenContext { WindowTitle = "", ProcessName = "", MonitorBounds = new PixelRect(0, 0, 1920, 1080) }; }
            public IDisposable RegisterHotkey(string combo, Action onPressed) { return new NoopDisposable(); }
            // The root every module's Init is pointed at. It was Path.GetTempPath() itself until
            // 2026-09-29 (F351); Run() hands each host its per-run SelfTestScratch root instead. Null
            // means "no storage", the shape a module without the Storage permission sees, and every
            // in-tree module already tolerates it (ConventionHost hands out exactly that).
            public string StorageRoot;
            public IModuleStorage GetStorage(string moduleId) { return StorageRoot == null ? null : new DirStorage(StorageRoot); }
            public IModuleSettings GetSettings(string moduleId) { return new MemSettings(); }
            public IDisposable RegisterDropResponder(int priority, Func<bool> onDrop) { return new NoopDisposable(); }
            public IDisposable RegisterPokeResponder(string moduleId, int priority, Func<bool> onPoke) { return new NoopDisposable(); }
            public IDisposable RegisterCompanionDropResponder(int priority, Func<ICompanion, bool> onDrop) { return new NoopDisposable(); }
            public IDisposable RegisterCompanionPokeResponder(string moduleId, int priority, Func<ICompanion, bool> onPoke) { return new NoopDisposable(); }
            public bool IsCompanionAlive(ICompanion pet) { return pet != null; }
            // Fullscreen is environmental, so a double reports "no game running" unless a test says
            // otherwise; FullscreenActive lets one say otherwise.
            public bool FullscreenActive;
            public bool IsFullscreenActive { get { return FullscreenActive; } }
            public event Action<bool> FullscreenChanged;
            public void RaiseFullscreen(bool on)
            {
                FullscreenActive = on;
                var h = FullscreenChanged; if (h != null) h(on);
            }
            public bool PlaySound(string moduleId, byte[] audio, double volume) { return false; }
            public bool PlayNotificationSound(string moduleId) { return false; }
            public bool StopSound(string moduleId) { return false; }
            public IDisposable RegisterSpeechResponder(string moduleId, int priority, Func<SpeechRequest, bool> onSpeech) { return new NoopDisposable(); }
            public System.Threading.Tasks.Task<IReadOnlyList<CatalogItem>> FetchCatalogItemsAsync(string kind) { return System.Threading.Tasks.Task.FromResult((IReadOnlyList<CatalogItem>)new List<CatalogItem>()); }
            public System.Threading.Tasks.Task<byte[]> DownloadCatalogItemAsync(string kind, string id) { return System.Threading.Tasks.Task.FromResult(new byte[0]); }
            // A fake host grants nothing: the real permission-gated bridge is exercised through
            // CompanionHost itself, not through these stand-ins.
            public ICompanionManager GetCompanionManager(string moduleId) { return new DenyingCompanionManager(); }
            public bool IsDarkTheme { get { return false; } }
            /// <summary>What modules logged, recorded rather than discarded (RA-284). No assertion reads it now: the
            /// warm check it was added for moved to the module's status seam, because the log line it asserted
            /// absent is written from a pool thread and raced the check. Kept so a failing run's transcript can be
            /// read from the fake.</summary>
            public readonly List<string> LoggedLines = new List<string>();
            public void Log(string moduleId, string message) { LoggedLines.Add((moduleId ?? "") + ": " + (message ?? "")); }
            public IReadOnlyList<string> PickFilesToOpen(string title, string fileKindLabel, IReadOnlyList<string> extensions) { return PickedFiles; }
            public bool OpenLink(string moduleId, string httpsUrl) { return true; }
            public List<string> PickedFiles = new List<string>();
            public void AddTrayItems(IEnumerable<TrayItem> items) { if (items != null) TrayItems.AddRange(items); }
            public void AddOptionsPane(OptionsPane pane) { if (pane != null) OptionsPanes.Add(pane); }
            public void PublishContext(string moduleId, string key, string valueJson) { }
            public string ReadContext(string key) { return ""; }
            public event Action<string> ContextChanged { add { } remove { } }

            private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
            private sealed class DirStorage : IModuleStorage
            {
                public DirStorage(string dir) { DataDirectory = dir; }
                public string DataDirectory { get; private set; }
            }
            /// <summary>A fresh store per call, EMPTY except for one seed: the Fortunes module's smart picker is
            /// OFF (RA-284; R-026's mechanism). An empty store means smart ON, and every Fortunes load in this
            /// flag then warmed the whole corpus on every core until Shutdown cancelled it. Nothing here asserts
            /// a smart pick, so nothing is lost; the one load that still warms is PaneAttribution's, which goes
            /// through a REAL CompanionHost and the isolated data root's empty module settings.</summary>
            private sealed class MemSettings : IModuleSettings
            {
                private readonly Dictionary<string, string> _d = new Dictionary<string, string> { { "smartFortunes", "false" } };
                public string Get(string key, string fallback) { string v; return _d.TryGetValue(key, out v) ? v : fallback; }
                public int GetInt(string key, int fallback) { string v; int n; return (_d.TryGetValue(key, out v) && int.TryParse(v, out n)) ? n : fallback; }
                public bool GetBool(string key, bool fallback) { string v; bool b; return (_d.TryGetValue(key, out v) && bool.TryParse(v, out b)) ? b : fallback; }
                public void Set(string key, string value) { _d[key] = value; }
                public bool Save() { return true; }
            }
        }
    }
}
