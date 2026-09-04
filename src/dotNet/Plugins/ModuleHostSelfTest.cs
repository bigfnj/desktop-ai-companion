using System;
using System.Collections.Generic;
using System.IO;
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
            try
            {
                string modulesRoot = Path.Combine(AppContext.BaseDirectory, "modules");
                if (!Directory.Exists(Path.Combine(modulesRoot, "testmodule")))
                {
                    sb.AppendLine("SKIP: no bundled test module at " + Path.Combine(modulesRoot, "testmodule"));
                    return Finish(sb, true);
                }

                var host = new RecordingHost();
                using (var loader = new ModuleHost())
                {
                    int loaded = loader.LoadFrom(modulesRoot, host, s => sb.AppendLine("  " + s));
                    ok &= Check(sb, "at least one module loaded", loaded >= 1);
                    ok &= Check(sb, "test module reports its id", HasModule(loader, "testmodule"));
                    ok &= Check(sb, "module contributed a tray item", host.TrayItems.Count >= 1);
                    ok &= Check(sb, "module contributed an options pane", host.OptionsPanes.Count >= 1);

                    host.RaiseCompanionPoked(new PokeInfo { Pet = new FakeCompanion(), PokeCount = 1 });
                    ok &= Check(sb, "CompanionPoked event reached the module (SayAll recorded)", host.LastSayAll == "poked!");

                    loader.ShutdownAll(s => sb.AppendLine("  " + s));
                    // After shutdown the module unsubscribes: a second poke must NOT re-trigger.
                    host.LastSayAll = null;
                    host.RaiseCompanionPoked(new PokeInfo { Pet = new FakeCompanion(), PokeCount = 2 });
                    ok &= Check(sb, "module unsubscribed on Shutdown", host.LastSayAll == null);
                }

                ok &= MinHostVersionGate(sb, modulesRoot);
                ok &= PetManagerPermissionGate(sb);
                ok &= PendingUpdateSwap(sb);
                ok &= WeeklyCheckSchedule(sb);
                ok &= UpdateScanVersionRule(sb);
                ok &= ScratchSweep(sb);
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            return Finish(sb, ok);
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
                // Mirrors CompanionHost.ModuleDataDirectory's layout (<data root>\modules\<id>): a module's data lives
                // OUTSIDE its install folder, and an update -- unlike an uninstall -- must leave it alone.
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

                // An update for something no longer installed must be discarded, not resurrected.
                string gone = PendingModuleUpdates.PrepareStagingDirectory("removed", stagingRoot);
                File.WriteAllText(Path.Combine(gone, "Removed.dll"), "new");
                PendingModuleUpdates.MarkForUpdate("removed", marker);
                PendingModuleUpdates.ProcessPending(modulesRoot, stagingRoot, marker, s => sb.AppendLine("  " + s));
                ok &= Check(sb, "update: an uninstalled module is not resurrected",
                    !Directory.Exists(Path.Combine(modulesRoot, "removed")));

                // An empty staging folder must leave the installed copy intact.
                PendingModuleUpdates.PrepareStagingDirectory("demo", stagingRoot);
                PendingModuleUpdates.MarkForUpdate("demo", marker);
                PendingModuleUpdates.ProcessPending(modulesRoot, stagingRoot, marker, s => sb.AppendLine("  " + s));
                ok &= Check(sb, "update: an empty staged payload keeps the installed module",
                    File.Exists(Path.Combine(installed, "Demo.dll")) &&
                    File.ReadAllText(Path.Combine(installed, "Demo.dll")) == "new");
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

                    ok &= Check(sb, "failures: a healthy load reports none",
                        new ModuleHost().Failures.Count == 0);
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
        private static bool MinHostVersionGate(StringBuilder sb, string modulesRoot)
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

            ok &= FailuresAreReported(sb);

            // Wiring: the real loader, the real testmodule (which declares MinHostVersion 1.0.0).
            if (!Directory.Exists(Path.Combine(modulesRoot, "testmodule")))
            {
                sb.AppendLine("SKIP: no bundled test module for the MinHostVersion wiring half");
                return ok;
            }

            var tooOld = new RecordingHost { HostVersionValue = "0.0.1" };
            using (var loader = new ModuleHost())
            {
                int loaded = loader.LoadFrom(modulesRoot, tooOld, s => sb.AppendLine("  " + s));
                ok &= Check(sb, "wiring: a host below every module's MinHostVersion loads nothing", loaded == 0);
                ok &= Check(sb, "wiring: a refused module contributes no tray item and no pane (refused before Init)",
                    tooOld.TrayItems.Count == 0 && tooOld.OptionsPanes.Count == 0);
                tooOld.RaiseCompanionPoked(new PokeInfo { Pet = new FakeCompanion(), PokeCount = 1 });
                ok &= Check(sb, "wiring: a refused module never subscribed to anything", tooOld.LastSayAll == null);
            }

            var satisfied = new RecordingHost { HostVersionValue = "1.5.0" };
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

            // The cached result has to survive a round trip, or a pane renders nothing on open.
            var offers = new List<ModuleUpdateOffer>
            {
                new ModuleUpdateOffer { Offered = new CatalogModule { Id = "aibrain", Version = "1.4.1" } },
                new ModuleUpdateOffer { Offered = new CatalogModule { Id = "fortunes", Version = "1.2.8" } },
            };
            string encoded = ModuleUpdateScan.Encode(offers);
            ok &= Check(sb, "weekly: offers encode as id=version;id=version",
                encoded == "aibrain=1.4.1;fortunes=1.2.8");
            Dictionary<string, string> decoded = ModuleUpdateScan.Decode(encoded);
            ok &= Check(sb, "weekly: offers decode back to the same pairs",
                decoded.Count == 2 && decoded["aibrain"] == "1.4.1" && decoded["fortunes"] == "1.2.8");
            ok &= Check(sb, "weekly: an empty cache decodes to nothing rather than throwing",
                ModuleUpdateScan.Decode("").Count == 0 && ModuleUpdateScan.Decode(null).Count == 0);
            // A cache is not user data: a malformed entry costs one redundant check, so it is skipped rather
            // than thrown on.
            ok &= Check(sb, "weekly: malformed cache entries are skipped, not fatal",
                ModuleUpdateScan.Decode("garbage;=1.0;aibrain=;good=2.0").Count == 1);
            ok &= Check(sb, "weekly: nothing to offer encodes as empty",
                ModuleUpdateScan.Encode(new List<ModuleUpdateOffer>()) == "" && ModuleUpdateScan.Encode(null) == "");
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
            return ok;
        }

        private sealed class FakeCompanion : ICompanion { public int Id { get { return 1; } } public bool IsBusy { get { return false; } } public string TypeId { get { return ""; } } }

        /// <summary>A headless IHost that records what modules do, for the self-test.</summary>
        private sealed class RecordingHost : IHost
        {
            // Settable so the MinHostVersion gate can be exercised by lying about the HOST's version, which
            // avoids needing a purpose-built too-new module DLL on disk. Defaults to the unparseable
            // "selftest", which the gate treats permissively.
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
            // unchanged; LastSay/LastSayPet are the additive channel.
            public string LastSay;
            public ICompanion LastSayPet;
            public int SayAllCount;
            public void Say(ICompanion pet, string text) { LastSay = text; LastSayPet = pet; LastSayAll = text; }
            public void SayAll(string text) { SayAllCount++; LastSayAll = text; }
            public void Say(ICompanion pet, string text, DesktopAICompanion.Modules.SpeechStyle style) { Say(pet, text); }
            public void SayAll(string text, DesktopAICompanion.Modules.SpeechStyle style) { SayAll(text); }
            public bool TryPlayAnimation(ICompanion pet, string animationName) { return true; }
            public void PlayAnimationAll(IReadOnlyList<string> animationCandidates) { }
            public ScreenContext CaptureScreenContext(ICompanion pet) { return new ScreenContext { WindowTitle = "", ProcessName = "", MonitorBounds = new PixelRect(0, 0, 1920, 1080) }; }
            public IDisposable RegisterHotkey(string combo, Action onPressed) { return new NoopDisposable(); }
            public IModuleStorage GetStorage(string moduleId) { return new MemStorage(); }
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
            public int PlaySoundCount;
            public int StopSoundCount;
            public bool PlaySound(string moduleId, byte[] audio, double volume) { PlaySoundCount++; return false; }
            public bool StopSound(string moduleId) { StopSoundCount++; return false; }
            public IDisposable RegisterSpeechResponder(string moduleId, int priority, Func<SpeechRequest, bool> onSpeech) { return new NoopDisposable(); }
            public System.Threading.Tasks.Task<IReadOnlyList<CatalogItem>> FetchCatalogItemsAsync(string kind) { return System.Threading.Tasks.Task.FromResult((IReadOnlyList<CatalogItem>)new List<CatalogItem>()); }
            public System.Threading.Tasks.Task<byte[]> DownloadCatalogItemAsync(string kind, string id) { return System.Threading.Tasks.Task.FromResult(new byte[0]); }
            // A fake host grants nothing: the real permission-gated bridge is exercised through
            // CompanionHost itself, not through these stand-ins.
            public ICompanionManager GetCompanionManager(string moduleId) { return new DenyingCompanionManager(); }
            public bool IsDarkTheme { get { return false; } }
            public void Log(string moduleId, string message) { }
            public IReadOnlyList<string> PickFilesToOpen(string title, string fileKindLabel, IReadOnlyList<string> extensions) { return PickedFiles; }
            public string OpenedLink;
            public bool OpenLink(string moduleId, string httpsUrl) { OpenedLink = httpsUrl; return true; }
            public List<string> PickedFiles = new List<string>();
            public void AddTrayItems(IEnumerable<TrayItem> items) { if (items != null) TrayItems.AddRange(items); }
            public void AddOptionsPane(OptionsPane pane) { if (pane != null) OptionsPanes.Add(pane); }
            public void PublishContext(string moduleId, string key, string valueJson) { }
            public string ReadContext(string key) { return ""; }
            public event Action<string> ContextChanged { add { } remove { } }

            private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
            private sealed class MemStorage : IModuleStorage { public string DataDirectory { get { return Path.GetTempPath(); } } }
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
