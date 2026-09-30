using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using DesktopAICompanion.ModuleKit;
using DesktopAICompanion.ModuleKit.Testing;
using DesktopAICompanion.Modules;
// ALIASES, NOT SIMPLE NAMES. This harness compiles src\Portable\AppSettingsStore.cs and
// src\dotNet\RuntimeGeometry.cs into itself, and RuntimeGeometry.cs declares a same-named production
// twin in the enclosing namespace (DesktopAICompanion.UnicodeTextProgress; AppSettingsStore.cs declared
// DesktopAICompanion.AtomicFile until F358, 2026-09-30, when the host started compiling ModuleKit's
// AtomicFile.cs by source link, so that twin no longer exists). C#
// resolves a simple name against the enclosing namespace BEFORE the compilation unit's using
// directives, so inside `namespace DesktopAICompanion` the bare names bound to the twins compiled
// here -- silently, with no warning (CS0436 needs identical fully-qualified names) -- and two of the
// ModuleKit groups below spent their whole life testing the host's copies while the ModuleKit copies
// every module ships had no direct test (F382). The aliases pin the ModuleKit types, and each group
// asserts the assembly it landed in, so the next same-named type cannot rebind them in silence.
using KitAtomicFile = DesktopAICompanion.ModuleKit.AtomicFile;
using KitUnicode = DesktopAICompanion.ModuleKit.UnicodeTextProgress;

namespace DesktopAICompanion
{
    /// <summary>
    /// Regression groups for DesktopAICompanion.ModuleKit — the support library module authors reference. These
    /// assert the behaviour a module DEPENDS on: that a settings write survives a crash, that a resource
    /// lookup tolerates a namespace change, that text is never cut through a surrogate pair, and that a
    /// module which cannot persist degrades instead of throwing.
    /// </summary>
    internal static partial class Program
    {
        private const string ModuleKitAssemblyName = "DesktopAICompanion.ModuleKit";

        private static void TestModuleKitAtomicFile()
        {
            // The type under test must be the one inside ModuleKit.dll, not the twin compiled into this exe.
            AssertEqual(ModuleKitAssemblyName, typeof(KitAtomicFile).Assembly.GetName().Name,
                "This group bound to an AtomicFile outside ModuleKit.dll, so it would test the wrong copy.");

            string directory = Path.Combine(_testRoot, "modulekit-atomic");
            string path = Path.Combine(directory, "settings.json");

            AssertTrue(KitAtomicFile.TryWriteAllText(path, "{\"a\":1}", null),
                "A first write into a not-yet-existing directory failed.");
            AssertEqual("{\"a\":1}", File.ReadAllText(path), "The written content did not round-trip.");

            AssertTrue(KitAtomicFile.TryWriteAllText(path, "{\"a\":2}", null), "An overwrite failed.");
            AssertEqual("{\"a\":2}", File.ReadAllText(path), "The overwrite did not replace the content.");

            // UTF-8 with NO BOM: a stray BOM has broken this app's own XML/JSON readers before.
            byte[] bytes = File.ReadAllBytes(path);
            AssertFalse(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "The atomic write emitted a UTF-8 BOM.");

            // A backup keeps the PREVIOUS content, so a bad write is recoverable.
            string backup = Path.Combine(directory, "settings.bak");
            AssertTrue(KitAtomicFile.TryWriteAllText(path, "{\"a\":3}", backup), "A write with a backup failed.");
            AssertEqual("{\"a\":3}", File.ReadAllText(path), "The backed-up write did not land.");
            AssertEqual("{\"a\":2}", File.ReadAllText(backup), "The backup did not capture the prior content.");

            // Failure is reported, not thrown: a module that cannot persist should degrade.
            AssertFalse(KitAtomicFile.TryWriteAllText("not-a-full-path.json", "x", null),
                "A relative path was accepted; it must be refused rather than written somewhere surprising.");

            // No temp files are left behind.
            foreach (string leftover in Directory.GetFiles(directory))
                AssertFalse(leftover.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase),
                    "A temp file survived an atomic write: " + leftover);
        }

        /// <summary>The shape of AtomicFile.ReplaceExisting, so one driver exercises it at both path lengths.</summary>
        private delegate void ReplaceExistingDelegate(string temporaryPath, string destinationPath,
            string backupPath, System.Threading.CancellationToken cancellationToken,
            Action<string, string, string, bool> replaceFile);

        /// <summary>
        /// The MoveFileEx fallback of AtomicFile past MAX_PATH (N-gates-01; until F358 the host carried a twin
        /// copy, exercised here side by side, and now compiles this same file), reached through the
        /// test seam that refuses File.Replace, the way the Fortunes VectorCache probe reaches it. The raw
        /// P/Invoke was handed the plain path and failed with ERROR_FILENAME_EXCED_RANGE once that path passed
        /// 260 characters, while File.Replace on the happy path accepted the same path, so the fallback was
        /// durable only under a short data root: measured on 2026-09-29, a 74-character %TEMP% passed and a
        /// 107-character one lost the save. The WITNESS runs the same forced fallback at a short path, where
        /// it always worked, so a failure past MAX_PATH is the length and nothing else.
        /// </summary>
        private static void TestAtomicReplaceFallbackPastMaxPath()
        {
            AssertEqual(ModuleKitAssemblyName, typeof(KitAtomicFile).Assembly.GetName().Name,
                "This group bound to an AtomicFile outside ModuleKit.dll, so it would test the wrong copy.");

            // Past MAX_PATH on the directory alone, whatever TEMP the harness runs under: the shortest
            // realistic one (C:\Users\x\AppData\Local\Temp) is 30 characters, and this adds 300.
            string longDirectory = Path.Combine(_testRoot, "atomic-long",
                new string('a', 120), new string('b', 120), new string('c', 40));
            AssertTrue(longDirectory.Length > 260,
                "WITNESS: the long fixture must be past MAX_PATH to test anything; it is " +
                longDirectory.Length + " characters.");
            string shortDirectory = Path.Combine(_testRoot, "atomic-short");
            AssertTrue(Path.Combine(shortDirectory, "settings.json").Length < 260,
                "WITNESS: the short fixture must be inside MAX_PATH, or both runs test the same thing.");

            // WITNESS first: the forced fallback at a short path, which is where it has always worked.
            RunForcedFallbackReplace(shortDirectory, "ModuleKit AtomicFile", KitAtomicFile.ReplaceExisting);
            RunForcedFallbackReplace(longDirectory, "ModuleKit AtomicFile", KitAtomicFile.ReplaceExisting);
        }

        private static void RunForcedFallbackReplace(string directory, string twin, ReplaceExistingDelegate replace)
        {
            Directory.CreateDirectory(directory);
            string destination = Path.Combine(directory, "settings.json");
            string temporary = Path.Combine(directory, ".settings.json.tmp");
            string backup = Path.Combine(directory, "settings.bak");
            File.WriteAllText(destination, "old");
            File.WriteAllText(temporary, "new");
            if (File.Exists(backup)) File.Delete(backup);

            int refused = 0;
            Action<string, string, string, bool> unsupported = delegate
            {
                refused++;
                throw new PlatformNotSupportedException("File.Replace refused by the test seam");
            };
            string where = directory.Length > 260 ? "past MAX_PATH" : "at a short path";
            string label = twin + ": the MoveFileEx fallback";
            try
            {
                replace(temporary, destination, backup, System.Threading.CancellationToken.None, unsupported);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(label + " threw " + where + " (" + directory.Length +
                    " characters): " + ex.GetType().Name + ": " + ex.Message);
            }
            AssertEqual(1, refused, "WITNESS: " + label + " was not reached " + where +
                ", so the File.Replace seam did not refuse.");
            AssertEqual("new", File.ReadAllText(destination), label + " did not replace the file " + where + ".");
            AssertFalse(File.Exists(temporary), label + " left the temporary file behind " + where + ".");
            AssertEqual("old", File.ReadAllText(backup), label + " did not keep the previous content as the backup " + where + ".");
        }

        private static void TestModuleKitEmbeddedResources()
        {
            // This harness embeds nothing, so assert against an assembly that does: ModuleKit itself has no
            // resources, which is the "absent" case, and every absent lookup must degrade rather than throw.
            // Through the alias, so `kit` really is ModuleKit.dll: the bare name bound to this exe (F382).
            Assembly kit = typeof(KitAtomicFile).Assembly;
            AssertEqual(ModuleKitAssemblyName, kit.GetName().Name, "The 'kit' assembly is not ModuleKit.dll.");
            AssertFalse(EmbeddedResources.Exists(kit, "definitely-not-here.png"),
                "A missing resource reported as present.");
            AssertEqual(null, EmbeddedResources.LoadBytes(kit, "definitely-not-here.png"),
                "A missing resource returned bytes.");
            AssertEqual("", EmbeddedResources.LoadText(kit, "definitely-not-here.txt"),
                "A missing resource returned text.");
            AssertEqual(null, EmbeddedResources.LoadJson<string[]>(kit, "definitely-not-here.json"),
                "A missing resource deserialized to something.");

            // Null/empty arguments are tolerated (a module may pass a computed name).
            AssertEqual(null, EmbeddedResources.LoadBytes(null, "x.png"), "A null assembly threw or returned data.");
            AssertEqual("", EmbeddedResources.LoadText(kit, null), "A null suffix returned text.");

            // The suffix match is the contract: the SDK prefixes a manifest name with namespace + folder,
            // so callers match on the trailing file name -- and the suffix must start at a segment boundary
            // (F229), or "icon.png" also finds "tray-icon.png". The pure rule first.
            AssertTrue(EmbeddedResources.MatchesResourceName("Mod.Assets.icon.png", "icon.png"),
                "A segment-aligned suffix did not match.");
            AssertTrue(EmbeddedResources.MatchesResourceName("icon.png", "ICON.PNG"),
                "An exact name did not match case-insensitively.");
            AssertFalse(EmbeddedResources.MatchesResourceName("Mod.Assets.tray-icon.png", "icon.png"),
                "A longer file name that merely ends the same way matched.");
            AssertFalse(EmbeddedResources.MatchesResourceName("Mod.Assets.myicon.png", "icon.png"),
                "A suffix starting mid-segment matched.");

            // Then the loader, against this harness's own two embedded fixtures. The csproj lists the decoy
            // FIRST, and the manifest keeps that order, so a loader that matched with a bare EndsWith would
            // hand back tray-icon.png for "icon.png". The WITNESS asserts the order and that the bytes differ,
            // because a check that would pass on either fixture proves nothing.
            Assembly self = typeof(Program).Assembly;
            string[] names = self.GetManifestResourceNames();
            string iconName = Array.Find(names, n => n.EndsWith(".Fixtures.icon.png", StringComparison.Ordinal));
            string decoyName = Array.Find(names, n => n.EndsWith(".Fixtures.tray-icon.png", StringComparison.Ordinal));
            AssertTrue(iconName != null && decoyName != null, "The colliding fixtures are not embedded.");
            AssertTrue(Array.IndexOf(names, decoyName) < Array.IndexOf(names, iconName),
                "WITNESS: the decoy must be listed before the real resource for this test to bite.");
            byte[] iconBytes = ReadManifestResource(self, iconName);
            byte[] decoyBytes = ReadManifestResource(self, decoyName);
            AssertFalse(BytesEqual(iconBytes, decoyBytes), "WITNESS: the two fixtures must differ.");
            AssertTrue(BytesEqual(iconBytes, EmbeddedResources.LoadBytes(self, "icon.png")),
                "LoadBytes(\"icon.png\") did not return icon.png's bytes (the decoy tray-icon.png won).");
            AssertTrue(BytesEqual(decoyBytes, EmbeddedResources.LoadBytes(self, "tray-icon.png")),
                "LoadBytes(\"tray-icon.png\") did not return tray-icon.png's bytes.");
            AssertTrue(EmbeddedResources.Exists(self, "icon.png") && EmbeddedResources.Exists(self, "Fixtures.icon.png"),
                "A resource that exists was not found by its trailing name.");
        }

        private static byte[] ReadManifestResource(Assembly assembly, string name)
        {
            using (Stream stream = assembly.GetManifestResourceStream(name))
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void TestModuleKitUnicodeBoundaries()
        {
            // The ModuleKit copy, asserted: the host's twin in RuntimeGeometry.cs is covered by
            // TestSpeechGeometryAndUnicode, and this group used to test it a second time by accident (F382).
            AssertEqual(ModuleKitAssemblyName, typeof(KitUnicode).Assembly.GetName().Name,
                "This group bound to a UnicodeTextProgress outside ModuleKit.dll, so it would test the wrong copy.");

            // "A" + a non-BMP emoji (surrogate PAIR) + "B": the emoji occupies two UTF-16 code units.
            string text = "A\U0001F600B";
            AssertEqual(4, text.Length, "The fixture is not the expected length in code units.");

            AssertEqual(1, KitUnicode.NextCodePointBoundary(text, 0), "Advancing over 'A' was wrong.");
            AssertEqual(3, KitUnicode.NextCodePointBoundary(text, 1),
                "Advancing over a surrogate pair must move by two code units, not one.");
            AssertEqual(4, KitUnicode.NextCodePointBoundary(text, 3), "Advancing over 'B' was wrong.");
            AssertEqual(4, KitUnicode.NextCodePointBoundary(text, 99), "Past the end must clamp.");
            AssertEqual(0, KitUnicode.NextCodePointBoundary("", 0), "Empty text must stay at 0.");
            AssertEqual(1, KitUnicode.NextCodePointBoundary(text, -5), "A negative index must clamp to 0.");

            // Truncating INTO the pair backs off, so no lone surrogate is ever produced.
            AssertEqual("A", KitUnicode.TruncateAtCodePointBoundary(text, 2),
                "Truncation split a surrogate pair.");
            AssertEqual("A\U0001F600", KitUnicode.TruncateAtCodePointBoundary(text, 3),
                "Truncation at a whole-pair boundary was wrong.");
            AssertEqual(text, KitUnicode.TruncateAtCodePointBoundary(text, 99),
                "A cap beyond the length must return the whole string.");
            AssertEqual("", KitUnicode.TruncateAtCodePointBoundary(text, 0), "A zero cap must be empty.");
            AssertEqual("", KitUnicode.TruncateAtCodePointBoundary(null, 5), "Null must be empty.");

            foreach (string clipped in new[]
            {
                KitUnicode.TruncateAtCodePointBoundary(text, 2),
                KitUnicode.TruncateAtCodePointBoundary(text, 3),
            })
                if (clipped.Length > 0)
                    AssertFalse(char.IsHighSurrogate(clipped[clipped.Length - 1]),
                        "Truncation left a dangling high surrogate.");
        }

        private sealed class ProbeSettings
        {
            public string Name { get; set; }
            public int Count { get; set; }
            public List<string> Items { get; set; }
        }

        private static void TestModuleKitJsonSettingsStore()
        {
            string path = Path.Combine(_testRoot, "modulekit-json", "settings.json");
            var store = new JsonSettingsStore<ProbeSettings>(path, "coretests");

            // A missing file yields defaults rather than throwing — a module must still start.
            ProbeSettings fresh = store.Load();
            AssertTrue(fresh != null, "Load() returned null for a missing file.");
            AssertEqual(null, fresh.Name, "A fresh document was not default-constructed.");

            fresh.Name = "pearl";
            fresh.Count = 3;
            fresh.Items = new List<string> { "a", "b" };
            AssertTrue(store.Save(fresh), "Save() failed.");
            AssertTrue(File.Exists(path), "Save() did not create the file.");

            ProbeSettings loaded = store.Load();
            AssertEqual("pearl", loaded.Name, "A string did not round-trip.");
            AssertEqual(3, loaded.Count, "An int did not round-trip.");
            AssertTrue(loaded.Items != null && loaded.Items.Count == 2, "A list did not round-trip.");

            // Update mutates and persists in one step, and the write keeps the previous document as a
            // backup (F230), as the two stores this class was distilled from do.
            AssertTrue(store.Update(s => s.Count = 7), "Update() failed.");
            AssertEqual(7, store.Load().Count, "Update() did not persist.");
            AssertTrue(File.Exists(store.BackupPath_), "Save() kept no backup of the previous document.");
            AssertTrue(File.ReadAllText(store.BackupPath_).Contains("\"Count\": 3"),
                "The backup is not the previous document.");

            // Corrupt content degrades to defaults instead of throwing, says so, and is NOT written over:
            // Update on a document that is merely unreadable to this build would replace the user's
            // settings with defaults (F230). The file is left byte for byte.
            File.WriteAllText(path, "{ this is not json");
            ProbeSettings recovered = store.Load();
            AssertTrue(recovered != null, "A corrupt file threw instead of returning defaults.");
            AssertEqual(null, recovered.Name, "A corrupt file did not fall back to defaults.");
            AssertTrue(store.LastLoadWasUnreadable, "An unreadable file was not reported as such.");
            AssertFalse(store.Update(s => s.Count = 9), "Update() wrote defaults over an unreadable file.");
            AssertEqual("{ this is not json", File.ReadAllText(path), "Update() changed an unreadable file.");

            // A BOM-prefixed file still parses (the reader trims it).
            File.WriteAllText(path, "{\"Name\":\"gus\"}", new UTF8Encoding(true));
            AssertEqual("gus", store.Load().Name, "A BOM-prefixed settings file failed to parse.");
            AssertFalse(store.LastLoadWasUnreadable, "A readable file was reported unreadable.");

            // Update holds ONE cross-session lease across read, mutate and write (F231). With the lease held
            // elsewhere it declines outright, where Load-then-Save under two leases let a second instance
            // write in between and lose. Held the way the settings-store lock test holds its lock: the lease
            // file open with no sharing.
            using (new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                AssertFalse(store.Update(s => s.Name = "raced"), "Update() proceeded without the lease.");
            }
            AssertEqual("gus", store.Load().Name, "A declined Update() still changed the file.");

            AssertFalse(store.Save(null), "Saving null reported success.");
        }

        private static void TestModuleKitModulePaths()
        {
            // The host-provisioned directory is used as-is.
            using (var storage = new TempModuleStorage("probe"))
            {
                ModulePaths paths = ModulePaths.FromStorage(storage, "probe");
                AssertPathEqual(storage.DataDirectory, paths.Root);

                string file = paths.File("state.json");
                AssertPathEqual(Path.Combine(storage.DataDirectory, "state.json"), file);
                AssertTrue(Directory.Exists(paths.Root), "File() did not ensure the directory exists.");

                string sub = paths.Directory_("cache");
                AssertTrue(Directory.Exists(sub), "Directory_() did not create the subdirectory.");
            }

            // A module handed NO storage gets no root, not a temp folder (N-aibrain-02). The %TEMP% fallback
            // this group used to assert is the shape N-gates-02 removed from AiBrain: a directory nobody
            // owned or swept, written on every headless --module-selftest run. The shipped host always
            // provisions a storage directory, so in the product the branch is unreachable; a test host that
            // hands none gets HasRoot false, a Warning that names the module and the missing storage, and a
            // clear exception from every path member. Nothing is created anywhere.
            string oldFallback = Path.Combine(Path.GetTempPath(), "DesktopAICompanion.probe");
            if (Directory.Exists(oldFallback)) Directory.Delete(oldFallback, true);
            ModulePaths none = ModulePaths.FromStorage(null, "probe");
            AssertFalse(none.HasRoot, "A null storage produced a root.");
            AssertTrue(none.Warning != null && none.Warning.Contains("'probe'") && none.Warning.Contains("no storage directory"),
                "The no-storage warning does not name the module and the missing storage: " + none.Warning);
            AssertThrows<InvalidOperationException>(() => { string r = none.Root; },
                "Root did not throw with no storage.");
            AssertThrows<InvalidOperationException>(() => none.Ensure(), "Ensure() did not throw with no storage.");
            AssertThrows<InvalidOperationException>(() => none.File("state.json"),
                "File() did not throw with no storage; it used to hand out a %TEMP% path and create it.");
            AssertThrows<InvalidOperationException>(() => none.Directory_("cache"), "Directory_() did not throw with no storage.");
            AssertFalse(Directory.Exists(oldFallback),
                "A module handed no storage created the old %TEMP%\\DesktopAICompanion.<id> fallback directory.");
            // A blank directory from a storage handle is the same case as none.
            AssertFalse(ModulePaths.FromStorage(new BlankStorage(), "probe").HasRoot, "A blank storage directory produced a root.");
            // WITNESS: the same members work as before once there IS a root, so the throws above are the
            // no-storage state and not a broken class.
            using (var storage = new TempModuleStorage("probe"))
            {
                ModulePaths rooted = ModulePaths.FromStorage(storage, "probe");
                AssertTrue(rooted.HasRoot && rooted.Warning == null, "WITNESS: a real storage did not yield a root.");
                AssertPathEqual(storage.DataDirectory, rooted.Ensure());
            }

            AssertThrows<ArgumentException>(() => ModulePaths.FromRoot(""), "An empty root was accepted.");
        }

        /// <summary>A storage handle whose directory is blank, for the ModulePaths no-root case.</summary>
        private sealed class BlankStorage : IModuleStorage
        {
            public string DataDirectory { get { return "   "; } }
        }

        private static void TestModuleKitSelfTestProbe()
        {
            var passing = new SelfTestProbe();
            passing.Check("a true assertion", true);
            passing.Note("some context");
            string detail;
            AssertTrue(passing.Finish(out detail), "An all-pass probe reported failure.");
            AssertTrue(detail.Contains("PASS: a true assertion"), "The report omitted the assertion.");
            AssertTrue(detail.Contains("RESULT=PASS"), "The report omitted the RESULT line.");

            var failing = new SelfTestProbe();
            failing.Check("a true assertion", true);
            failing.Check("a false assertion", false);
            AssertFalse(failing.Passed, "A failed assertion did not flip the result.");
            AssertFalse(failing.Finish(out detail), "A probe with a failure reported success.");
            AssertTrue(detail.Contains("FAIL: a false assertion"), "The report omitted the failure.");
            AssertTrue(detail.Contains("RESULT=FAIL"), "The report omitted the failing RESULT line.");

            // A throwing assertion becomes a failure, not an escape.
            var throwing = new SelfTestProbe();
            throwing.Check("throws", () => { throw new InvalidOperationException("boom"); });
            AssertFalse(throwing.Passed, "A throwing assertion did not fail the probe.");
            AssertFalse(throwing.Finish(out detail), "A throwing probe reported success.");
            AssertTrue(detail.Contains("boom"), "The report omitted the exception message.");

            // The gate greps for SKIP:, so the probe must emit exactly that token.
            var skipped = new SelfTestProbe();
            skipped.Skip("nothing bundled");
            skipped.Finish(out detail);
            AssertTrue(detail.Contains("SKIP: nothing bundled"), "Skip() did not emit a SKIP: line.");
        }

        private static void TestModuleKitRecordingHost()
        {
            var host = new RecordingHost();

            // Contributions are recorded.
            host.AddTrayItems(new List<TrayItem> { new TrayItem { Label = "Do a thing" } });
            host.AddOptionsPane(new OptionsPane { Title = "Probe" });
            AssertEqual(1, host.TrayItems.Count, "A tray item was not recorded.");
            AssertEqual(1, host.OptionsPanes.Count, "An options pane was not recorded.");

            // Events reach a subscriber.
            int spawned = 0;
            host.CompanionSpawned += pet => spawned++;
            host.RaiseCompanionSpawned(new FakeCompanion());
            AssertEqual(1, spawned, "RaiseCompanionSpawned did not reach the handler.");

            // Speech is captured.
            host.SayAll("hello");
            AssertEqual(1, host.SaidLines.Count, "SayAll was not captured.");
            AssertEqual("hello", host.SaidLines[0], "The captured line was wrong.");

            // The recorded lists are SNAPSHOTS taken under a lock (N-remembrance-01): a module appends from
            // whatever thread it calls on, the test reads on its own, and List<T> is safe for neither an
            // append during a foreach nor a Count read during a growth. A view handed out before an append
            // keeps its count; a fresh read sees the append; the Clear* methods are the reset, since a
            // Clear() on the view clears a copy.
            List<string> view = host.SaidLines;
            host.SayAll("later");
            AssertEqual(1, view.Count, "A list handed to a test moved under it: the fake hands out its live list again.");
            AssertEqual(2, host.SaidLines.Count, "A fresh read did not see the later line.");
            AssertEqual(2, host.BroadcastLines.Count, "The broadcast copy did not see both lines.");
            host.ClearSaidLines();
            AssertEqual(0, host.SaidLines.Count, "ClearSaidLines left lines behind.");
            AssertEqual(0, host.BroadcastLines.Count, "ClearSaidLines left the broadcast copy behind.");
            AssertEqual(0, host.SaidToCompanions.Count, "ClearSaidLines left the targeted copy behind.");

            // ...and a pool thread appending while this thread enumerates: nothing thrown, nothing lost. The
            // writer parks half way so one snapshot is provably taken mid-append (the WITNESS that the reader
            // overlapped the writer at all), then the rest is appended under continuous enumeration.
            var stress = new RecordingHost();
            const int Lines = 20000;
            var midway = new System.Threading.ManualResetEventSlim();
            var proceed = new System.Threading.ManualResetEventSlim();
            System.Threading.Tasks.Task writer = System.Threading.Tasks.Task.Run(delegate
            {
                for (int i = 0; i < Lines; i++)
                {
                    if (i == Lines / 2) { midway.Set(); proceed.Wait(); }
                    stress.Log("probe", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            });
            AssertTrue(midway.Wait(TimeSpan.FromSeconds(30)), "The writer never reached the half-way mark.");
            AssertEqual(Lines / 2, stress.LoggedLines.Count,
                "WITNESS: the snapshot taken while the writer is parked half way does not hold exactly the lines written so far.");
            proceed.Set();
            int enumerated = 0;
            while (!writer.IsCompleted)
            {
                foreach (string line in stress.LoggedLines)
                    if (line == null) throw new InvalidOperationException("A null line was recorded.");
                enumerated++;
            }
            writer.GetAwaiter().GetResult();
            AssertEqual(Lines, stress.LoggedLines.Count, "Lines appended from a pool thread were lost.");
            AssertTrue(enumerated > 0, "The reader never enumerated while the writer ran.");
            stress.ClearLoggedLines();
            AssertEqual(0, stress.LoggedLines.Count, "ClearLoggedLines left lines behind.");

            // Responders are arbitrated in registration order: the first that returns true wins.
            var order = new List<string>();
            host.RegisterPokeResponder("first", 0, () => { order.Add("first"); return false; });
            host.RegisterPokeResponder("second", 0, () => { order.Add("second"); return true; });
            host.RegisterPokeResponder("third", 0, () => { order.Add("third"); return true; });
            AssertTrue(host.RaisePokeResponders(), "No poke responder claimed the poke.");
            AssertEqual(2, order.Count, "Arbitration did not stop at the first responder that spoke.");

            // ...and by PRIORITY across both registration styles, as the real host does. The double used
            // to discard the priority and run every legacy registration before any pet-aware one, so a
            // legacy responder at 0 pre-empted a pet-aware one at 10 -- the opposite of what a module
            // saw once loaded for real (F233). Registered legacy-first at the LOWER priority, so both the
            // priority order and the style interleaving are what decide this.
            var arbitrated = new RecordingHost();
            var fired = new List<string>();
            arbitrated.RegisterDropResponder(0, () => { fired.Add("legacy@0"); return true; });
            arbitrated.RegisterCompanionDropResponder(10, pet => { fired.Add("pet-aware@10"); return true; });
            AssertTrue(arbitrated.RaiseDrop(new FakeCompanion()), "No drop responder claimed the drop.");
            AssertEqual(1, fired.Count, "A claiming responder did not stop the chain.");
            AssertEqual("pet-aware@10", fired[0],
                "The higher-priority pet-aware responder did not run before the lower-priority legacy one.");
            // Equal priorities keep registration order, pet-aware first this time, so the tie-break is
            // what decides and not the style.
            var tied = new RecordingHost();
            var tiedOrder = new List<string>();
            tied.RegisterCompanionPokeResponder("a", 5, pet => { tiedOrder.Add("pet-aware"); return false; });
            tied.RegisterPokeResponder("b", 5, () => { tiedOrder.Add("legacy"); return true; });
            AssertTrue(tied.RaisePokeResponders(), "No poke responder claimed the tied poke.");
            AssertEqual("pet-aware", tiedOrder[0], "Equal priorities did not keep registration order.");
            AssertEqual(2, tiedOrder.Count, "The tied chain did not run both responders in order.");

            // Settings are shared per module id, so a test can assert what a pane persisted.
            IModuleSettings settings = host.GetSettings("probe");
            settings.Set("k", "v");
            settings.Save();
            AssertEqual("v", host.SettingsFor("probe").Get("k", null), "Settings did not persist in the fake.");
            AssertEqual(1, host.SettingsFor("probe").SaveCount, "Save() was not counted.");

            // A failed Save() puts the values back (N-blinkingled-02): the host hands a fresh instance loaded
            // from disk to every GetSettings, and a write that failed never reached it, so a module that
            // re-reads after a failed write sees the click did nothing. The same handle still shows a Set()
            // before Save(), as the host's does.
            FakeModuleSettings probeSettings = host.SettingsFor("probe");
            probeSettings.FailSaves = true;
            settings.Set("k", "unsaved");
            AssertEqual("unsaved", settings.Get("k", null), "A Set() was not visible on the same handle before Save().");
            AssertFalse(settings.Save(), "FailSaves did not fail the save.");
            AssertEqual("v", settings.Get("k", null),
                "A failed Save() kept the unsaved value; the host's next GetSettings would have read the disk.");
            AssertEqual(2, probeSettings.SaveCount, "A failed Save() was not counted.");
            probeSettings.FailSaves = false;
            settings.Set("k", "saved");
            AssertTrue(settings.Save(), "WITNESS: a Save() with FailSaves off failed.");
            AssertEqual("saved", settings.Get("k", null), "WITNESS: a successful Save() did not keep the value.");

            // Storage is absent unless the test provides it (mirroring an undeclared Storage permission).
            AssertEqual(null, host.GetStorage("probe"), "Storage was handed out without being provided.");

            // The default pet manager refuses everything with a reason, like the host's own denying bridge.
            string error;
            ICompanionManager pets = host.GetCompanionManager("probe");
            AssertFalse(pets.ValidateXml("<xml/>", out error), "The default pet manager validated.");
            AssertTrue(!string.IsNullOrEmpty(error), "The refusal carried no reason.");
            AssertEqual("", pets.CompanionsDirectory, "The denying pet manager exposed a pets directory.");

            // The sentinel version keeps the loader's MinHostVersion gate quiet by default.
            AssertEqual("9999.0.0", host.HostVersion, "The default host version is not the high sentinel.");

            // Declared enforces EVERY gate the host has, not only the two Audio verbs (F234). A module that
            // declared only Speech: its link is refused and not recorded, its speech responder is never
            // offered anything, and it gets the denying companion manager.
            var gated = new RecordingHost { Declared = ModulePermissions.Speech };
            bool responderRan = false;
            gated.RegisterSpeechResponder("probe", 0, request => { responderRan = true; return true; });
            AssertFalse(gated.OpenLink("probe", "https://example.invalid/"), "OpenLink succeeded without Network.");
            AssertEqual(0, gated.OpenedLinks.Count, "A refused OpenLink was still recorded.");
            AssertFalse(gated.RaiseSpeechRequest("hello", null), "A speech responder was offered a line without Voice.");
            AssertFalse(responderRan, "A speech responder ran for a module that never declared Voice.");
            // Identity, not type: the host's CONFIGURED manager defaults to a denying one too, so "is
            // DenyingCompanionManager" would be true either way. What the gate hands out is a fresh
            // denying bridge that is NOT the configured object.
            AssertFalse(ReferenceEquals(gated.GetCompanionManager("probe"), gated.CompanionManager),
                "A module without Companions was handed the configured companion manager.");
            // WITNESS: the same calls succeed once the flags are declared, so the refusals above are the
            // gates and not a broken double.
            var permitted = new RecordingHost
            {
                Declared = ModulePermissions.Speech | ModulePermissions.Network | ModulePermissions.Voice | ModulePermissions.Companions,
            };
            bool permittedRan = false;
            permitted.RegisterSpeechResponder("probe", 0, request => { permittedRan = true; return true; });
            AssertTrue(permitted.OpenLink("probe", "https://example.invalid/"), "OpenLink failed with Network declared.");
            AssertEqual(1, permitted.OpenedLinks.Count, "A permitted OpenLink was not recorded.");
            permitted.RaiseSpeechRequest("hello", null);
            AssertTrue(permittedRan, "A speech responder was skipped with Voice declared.");
            AssertTrue(ReferenceEquals(permitted.GetCompanionManager("probe"), permitted.CompanionManager),
                "A module with Companions was not handed the configured companion manager.");
        }
    }
}
