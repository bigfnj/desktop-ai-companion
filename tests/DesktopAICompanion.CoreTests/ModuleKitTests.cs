using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using DesktopAICompanion.ModuleKit;
using DesktopAICompanion.ModuleKit.Testing;
using DesktopAICompanion.Modules;

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
        private static void TestModuleKitAtomicFile()
        {
            string directory = Path.Combine(_testRoot, "modulekit-atomic");
            string path = Path.Combine(directory, "settings.json");

            AssertTrue(AtomicFile.TryWriteAllText(path, "{\"a\":1}", null),
                "A first write into a not-yet-existing directory failed.");
            AssertEqual("{\"a\":1}", File.ReadAllText(path), "The written content did not round-trip.");

            AssertTrue(AtomicFile.TryWriteAllText(path, "{\"a\":2}", null), "An overwrite failed.");
            AssertEqual("{\"a\":2}", File.ReadAllText(path), "The overwrite did not replace the content.");

            // UTF-8 with NO BOM: a stray BOM has broken this app's own XML/JSON readers before.
            byte[] bytes = File.ReadAllBytes(path);
            AssertFalse(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "The atomic write emitted a UTF-8 BOM.");

            // A backup keeps the PREVIOUS content, so a bad write is recoverable.
            string backup = Path.Combine(directory, "settings.bak");
            AssertTrue(AtomicFile.TryWriteAllText(path, "{\"a\":3}", backup), "A write with a backup failed.");
            AssertEqual("{\"a\":3}", File.ReadAllText(path), "The backed-up write did not land.");
            AssertEqual("{\"a\":2}", File.ReadAllText(backup), "The backup did not capture the prior content.");

            // Failure is reported, not thrown: a module that cannot persist should degrade.
            AssertFalse(AtomicFile.TryWriteAllText("not-a-full-path.json", "x", null),
                "A relative path was accepted; it must be refused rather than written somewhere surprising.");

            // No temp files are left behind.
            foreach (string leftover in Directory.GetFiles(directory))
                AssertFalse(leftover.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase),
                    "A temp file survived an atomic write: " + leftover);
        }

        private static void TestModuleKitEmbeddedResources()
        {
            // This harness embeds nothing, so assert against an assembly that does: ModuleKit itself has no
            // resources, which is the "absent" case, and every absent lookup must degrade rather than throw.
            Assembly kit = typeof(AtomicFile).Assembly;
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
            // so callers match on the trailing file name. Prove it against a real resource in this harness's
            // own assembly if one exists; otherwise the absent-path assertions above carry the group.
            string[] names = typeof(Program).Assembly.GetManifestResourceNames();
            if (names.Length > 0)
            {
                string full = names[0];
                int dot = full.LastIndexOf('.');
                string suffix = dot > 0 && dot < full.Length - 1 ? full.Substring(dot + 1) : full;
                AssertTrue(EmbeddedResources.Exists(typeof(Program).Assembly, suffix),
                    "A resource that exists was not found by its trailing name: " + full);
            }
        }

        private static void TestModuleKitUnicodeBoundaries()
        {
            // "A" + a non-BMP emoji (surrogate PAIR) + "B": the emoji occupies two UTF-16 code units.
            string text = "A\U0001F600B";
            AssertEqual(4, text.Length, "The fixture is not the expected length in code units.");

            AssertEqual(1, UnicodeTextProgress.NextCodePointBoundary(text, 0), "Advancing over 'A' was wrong.");
            AssertEqual(3, UnicodeTextProgress.NextCodePointBoundary(text, 1),
                "Advancing over a surrogate pair must move by two code units, not one.");
            AssertEqual(4, UnicodeTextProgress.NextCodePointBoundary(text, 3), "Advancing over 'B' was wrong.");
            AssertEqual(4, UnicodeTextProgress.NextCodePointBoundary(text, 99), "Past the end must clamp.");
            AssertEqual(0, UnicodeTextProgress.NextCodePointBoundary("", 0), "Empty text must stay at 0.");
            AssertEqual(1, UnicodeTextProgress.NextCodePointBoundary(text, -5), "A negative index must clamp to 0.");

            // Truncating INTO the pair backs off, so no lone surrogate is ever produced.
            AssertEqual("A", UnicodeTextProgress.TruncateAtCodePointBoundary(text, 2),
                "Truncation split a surrogate pair.");
            AssertEqual("A\U0001F600", UnicodeTextProgress.TruncateAtCodePointBoundary(text, 3),
                "Truncation at a whole-pair boundary was wrong.");
            AssertEqual(text, UnicodeTextProgress.TruncateAtCodePointBoundary(text, 99),
                "A cap beyond the length must return the whole string.");
            AssertEqual("", UnicodeTextProgress.TruncateAtCodePointBoundary(text, 0), "A zero cap must be empty.");
            AssertEqual("", UnicodeTextProgress.TruncateAtCodePointBoundary(null, 5), "Null must be empty.");

            foreach (string clipped in new[]
            {
                UnicodeTextProgress.TruncateAtCodePointBoundary(text, 2),
                UnicodeTextProgress.TruncateAtCodePointBoundary(text, 3),
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

            // Update mutates and persists in one step.
            AssertTrue(store.Update(s => s.Count = 7), "Update() failed.");
            AssertEqual(7, store.Load().Count, "Update() did not persist.");

            // Corrupt content degrades to defaults instead of throwing.
            File.WriteAllText(path, "{ this is not json");
            ProbeSettings recovered = store.Load();
            AssertTrue(recovered != null, "A corrupt file threw instead of returning defaults.");
            AssertEqual(null, recovered.Name, "A corrupt file did not fall back to defaults.");

            // A BOM-prefixed file still parses (the reader trims it).
            File.WriteAllText(path, "{\"Name\":\"gus\"}", new UTF8Encoding(true));
            AssertEqual("gus", store.Load().Name, "A BOM-prefixed settings file failed to parse.");

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

            // A module WITHOUT the Storage permission gets null storage: it must still get a usable root
            // (scratch space) rather than crash on every path call.
            ModulePaths fallback = ModulePaths.FromStorage(null, "probe");
            AssertTrue(!string.IsNullOrEmpty(fallback.Root), "A null storage produced no root.");
            AssertTrue(fallback.Root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
                "The no-storage fallback did not land under the temp directory.");

            // A hostile id cannot escape the fallback directory. The property that matters is containment of
            // the RESOLVED path, not the spelling of the id.
            string tempRoot = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string hostileId in new[] { "../../escape", @"..\..\escape", "a/b/c", "..", "   " })
            {
                string resolved = Path.GetFullPath(ModulePaths.FromStorage(null, hostileId).Root);
                AssertTrue(resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase),
                    "A hostile module id escaped the temp fallback root: '" + hostileId + "' -> " + resolved);
                AssertPathEqual(tempRoot.TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetDirectoryName(resolved));
            }

            AssertThrows<ArgumentException>(() => ModulePaths.FromRoot(""), "An empty root was accepted.");
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

            // Responders are arbitrated in registration order: the first that returns true wins.
            var order = new List<string>();
            host.RegisterPokeResponder("first", 0, () => { order.Add("first"); return false; });
            host.RegisterPokeResponder("second", 0, () => { order.Add("second"); return true; });
            host.RegisterPokeResponder("third", 0, () => { order.Add("third"); return true; });
            AssertTrue(host.RaisePokeResponders(), "No poke responder claimed the poke.");
            AssertEqual(2, order.Count, "Arbitration did not stop at the first responder that spoke.");

            // Settings are shared per module id, so a test can assert what a pane persisted.
            IModuleSettings settings = host.GetSettings("probe");
            settings.Set("k", "v");
            settings.Save();
            AssertEqual("v", host.SettingsFor("probe").Get("k", null), "Settings did not persist in the fake.");
            AssertEqual(1, host.SettingsFor("probe").SaveCount, "Save() was not counted.");

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
        }
    }
}
