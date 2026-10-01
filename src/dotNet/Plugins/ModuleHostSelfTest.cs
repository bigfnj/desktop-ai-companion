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
                    // this is the line that fails if the fake ever hands out the TEMP root again.
                    ok &= Check(sb, "modules' storage writes land under the scratch root, not the TEMP root",
                        HasModule(loader, "fortunes") && Directory.Exists(Path.Combine(scratch, "fortunes")));
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
                ok &= PendingRemovalRetry(sb);
                ok &= InitRollback(sb, modulesRoot);
                ok &= RefusalKeyedByFolder(sb, modulesRoot, scratch);
                ok &= SelfTestFinder(sb);
                ok &= ResponderChainDeclines(sb);
                ok &= ModuleDllDiscovery(sb);
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

        private static bool SamePath(string a, string b)
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
                // user's one action vanished. A directory at the marker's path is a write that cannot succeed
                // on any box; the pane's existing "Couldn't uninstall/update" handlers are what catch it.
                string unwritable = Path.Combine(root, "marker-that-is-a-directory");
                Directory.CreateDirectory(unwritable);
                bool removalThrew = false, updateThrew = false, unmarkThrew = false;
                try { PendingModuleRemovals.MarkForRemoval("doomed", unwritable); } catch (Exception) { removalThrew = true; }
                try { PendingModuleUpdates.MarkForUpdate("doomed", unwritable); } catch (Exception) { updateThrew = true; }
                ok &= Check(sb, "removal: a removal marker that cannot be written throws instead of reporting success",
                    removalThrew);
                ok &= Check(sb, "update: an update marker that cannot be written throws instead of reporting success",
                    updateThrew);
                // Unmark rewrites only when the id was present. A directory at the marker's path reads as no
                // marker (ReadIds answers empty), so the id is absent and Unmark returns before any write: the
                // throwing write is Unmark's WriteIds too, asserted through MarkForRemoval above, and this
                // early return is the WITNESS that an id nobody marked costs nothing.
                Directory.CreateDirectory(Path.Combine(root, "unmark-marker-that-is-a-directory"));
                try { PendingModuleRemovals.Unmark("stale", Path.Combine(root, "unmark-marker-that-is-a-directory")); }
                catch (Exception) { unmarkThrew = true; }
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
        /// leaves the installed copy alone. Everything (install root, staging root, marker file) is a throwaway
        /// temp path, so the test never reads or writes the real install or data directories.
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
                ok &= Check(sb, "wiring: a host below every module's MinHostVersion loads nothing", loaded == 0);
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
        /// The one version rule shared by the Update button and the monthly check. Newer offers, equal and older
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

        private static bool HasModule(ModuleHost loader, string id)
        {
            foreach (IModule m in loader.Modules)
                if (m.Info != null && string.Equals(m.Info.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
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
            public void Log(string moduleId, string message) { }
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
            private sealed class MemSettings : IModuleSettings
            {
                private readonly Dictionary<string, string> _d = new Dictionary<string, string>();
                public string Get(string key, string fallback) { string v; return _d.TryGetValue(key, out v) ? v : fallback; }
                public int GetInt(string key, int fallback) { string v; int n; return (_d.TryGetValue(key, out v) && int.TryParse(v, out n)) ? n : fallback; }
                public bool GetBool(string key, bool fallback) { string v; bool b; return (_d.TryGetValue(key, out v) && bool.TryParse(v, out b)) ? b : fallback; }
                public void Set(string key, string value) { _d[key] = value; }
                public bool Save() { return true; }
            }
        }
    }
}
