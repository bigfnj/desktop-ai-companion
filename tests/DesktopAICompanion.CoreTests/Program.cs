using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace DesktopAICompanion
{
    internal static partial class Program
    {
        private static readonly IList<string> Failures = new List<string>();
        private static string _testRoot;

        private static int Main()
        {
            _testRoot = Path.Combine(
                Path.GetTempPath(),
                "DesktopAICompanion.CoreTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testRoot);

            try
            {
                Run("AppPaths installed layouts", TestInstalledLayouts);
                Run("AppPaths portable marker and override", TestPortableMarkerAndOverride);
                Run("AppPaths current-directory independence", TestCurrentDirectoryIndependence);
                Run("Settings fresh defaults and clamps", TestFreshDefaultsAndClamps);
                Run("Settings legacy volume migration", TestLegacyVolumeMigration);
                Run("Settings one-time migration", TestOneTimeMigration);
                Run("Settings atomic backup", TestAtomicBackup);
                Run("Settings corrupt-primary recovery", TestCorruptPrimaryRecovery);
                Run("Settings future-schema preservation", TestFutureSchemaPreservation);
                Run("Settings pet-mix v1->v2 migration", TestSettingsPetMixMigration);
                Run("Settings pet-mix validation", TestSettingsPetMixValidation);
                Run("Settings pet-mix cross-process merge", TestSettingsPetMixMerge);
                Run("Settings per-pet size validation", TestSettingsPetSizeValidation);
                Run("Settings theme mode normalization", TestSettingsThemeMode);
                Run("Settings audio-device id normalization", TestSettingsAudioDevice);
                Run("Settings muted-pets validation", TestSettingsMutedPets);
                Run("Settings active-pet id normalization", TestSettingsActivePetId);
                Run("Settings random-drop validation", TestSettingsRandomDrop);
                Run("Settings trigger-speech validation", TestSettingsTriggerSpeech);
                Run("Settings monthly module-update check", TestSettingsMonthlyModuleUpdateCheck);
                Run("Settings lock-failure fallback", TestSettingsLockFailureFallback);
                Run("Scale level mapping", TestScaleMapping);
                Run("Monitor local/virtual layouts", TestMonitorLayouts);
                Run("AI capture monitor selection", TestCaptureMonitorSelection);
                Run("Window landing coordinate sentinel", TestWindowLandingCoordinateSentinel);
                Run("Window-follow relative scaling", TestWindowFollowRelativeScaling);
                Run("Retiring pet runtime ownership", TestRetiringPetRuntimeOwnership);
                Run("Unicode speech and logical sprite anchoring", TestSpeechGeometryAndUnicode);
                Run("Child-position coordinate reproduction", TestChildPositionReproduction);
                Run("Screen dimensions and fullscreen detection", TestMetricsAndFullscreen);
                Run("ModuleKit atomic file writes", TestModuleKitAtomicFile);
                Run("ModuleKit embedded resources", TestModuleKitEmbeddedResources);
                Run("ModuleKit unicode boundaries", TestModuleKitUnicodeBoundaries);
                Run("ModuleKit json settings store", TestModuleKitJsonSettingsStore);
                Run("ModuleKit module paths", TestModuleKitModulePaths);
                Run("ModuleKit self-test probe", TestModuleKitSelfTestProbe);
                Run("ModuleKit recording host", TestModuleKitRecordingHost);
            }
            finally
            {
                try { Directory.Delete(_testRoot, true); }
                catch { }
            }

            if (Failures.Count == 0)
            {
                Console.WriteLine(
                    "PASS: " + _groups.ToString(CultureInfo.InvariantCulture) +
                    " DesktopAICompanion core regression groups.");
                return 0;
            }

            Console.Error.WriteLine(
                "FAIL: " + Failures.Count.ToString(CultureInfo.InvariantCulture) +
                " regression group(s).");
            foreach (string failure in Failures)
                Console.Error.WriteLine("  " + failure);
            return 1;
        }

        // Counted rather than hardcoded: the literal in the summary line had drifted five groups
        // behind reality, so it reported 26 while 31 groups ran.
        private static int _groups;

        private static void Run(string name, Action test)
        {
            _groups++;
            try
            {
                test();
                Console.WriteLine("PASS: " + name);
            }
            catch (Exception exception)
            {
                Failures.Add(name + ": " + exception.Message);
            }
        }

        private static void TestInstalledLayouts()
        {
            string local = NewDirectory("paths-installed", "Local");
            // The PRE-RENAME literal on purpose. This asserts the probe for the old pre-MSI install root,
            // %LOCALAPPDATA%\DesktopPet, which is a fixed historical location and does not move with the
            // product name. Renaming it here would also make it collide with the new data root, so the test
            // would pass while asserting something else entirely.
            string legacyExe = Path.Combine(local, "DesktopPet");
            string msiExe = Path.Combine(local, "Programs", AppPaths.ProductName);

            AppPathLayout legacy = AppPaths.Resolve(
                legacyExe + Path.DirectorySeparatorChar, local, null, false);
            AssertTrue(legacy.IsInstalled, "Legacy install root was not recognized.");
            AssertFalse(legacy.IsPortable, "Legacy install root was classified as portable.");
            AssertPathEqual(Path.Combine(local, "DesktopAICompanion"), legacy.DataRoot);

            AppPathLayout msi = AppPaths.Resolve(
                msiExe.ToUpperInvariant(), local, null, false);
            AssertTrue(msi.IsInstalled, "MSI install root was not recognized.");
            AssertPathEqual(Path.Combine(local, "DesktopAICompanion"), msi.DataRoot);

            AppPathLayout looseCopy = AppPaths.Resolve(
                Path.Combine(local, "DesktopAICompanion-copy"), local, null, false);
            AssertFalse(looseCopy.IsInstalled, "A similarly named loose copy was treated as installed.");
            AssertPathEqual(Path.Combine(local, "DesktopAICompanion-copy", "data"), looseCopy.DataRoot);
        }

        private static void TestPortableMarkerAndOverride()
        {
            string local = NewDirectory("paths-portable", "Local");
            string installedExe = Path.Combine(local, "Programs", AppPaths.ProductName);
            AppPathLayout marker = AppPaths.Resolve(installedExe, local, null, true);
            AssertTrue(marker.IsPortable, "Portable marker did not override install-location detection.");
            AssertPathEqual(Path.Combine(installedExe, "data"), marker.DataRoot);

            string customRoot = NewDirectory("paths-portable", "IsolatedData");
            AppPathLayout overridden = AppPaths.Resolve(
                installedExe, local, customRoot + Path.DirectorySeparatorChar, false);
            AssertTrue(overridden.IsInstalled, "A data override unexpectedly changed product mode.");
            AssertTrue(overridden.IsDataRootOverridden,
                "The explicit data-root override was not recorded.");
            AssertPathEqual(customRoot, overridden.DataRoot);

            AssertThrows<ArgumentException>(
                delegate { AppPaths.Resolve(installedExe, local, "relative-data", false); },
                "A relative data-root override was accepted.");
            AssertThrows<ArgumentException>(
                delegate { AppPaths.Resolve(installedExe, local, "C:relative-data", false); },
                "A drive-relative data-root override was accepted.");
            AssertThrows<ArgumentException>(
                delegate
                {
                    AppPaths.Resolve(
                        installedExe,
                        local,
                        Path.DirectorySeparatorChar + "root-relative-data",
                        false);
                },
                "A current-drive-rooted data-root override was accepted.");
            AssertThrows<ArgumentException>(
                delegate
                {
                    new AppSettingsStore(
                        "C:relative-settings.json",
                        null);
                },
                "The settings store accepted a drive-relative output path.");
            AssertThrows<ArgumentException>(
                delegate
                {
                    new AppSettingsStore(
                        Path.DirectorySeparatorChar + "root-relative-settings.json",
                        null);
                },
                "The settings store accepted a current-drive-rooted output path.");

            TestBoundedDataMigration();
        }

        private static void TestCurrentDirectoryIndependence()
        {
            string original = Environment.CurrentDirectory;
            string firstCwd = NewDirectory("paths-cwd", "one");
            string secondCwd = NewDirectory("paths-cwd", "two");
            string local = NewDirectory("paths-cwd", "Local");
            string executable = NewDirectory("paths-cwd", "Application");

            try
            {
                Environment.CurrentDirectory = firstCwd;
                AppPathLayout first = AppPaths.Resolve(executable, local, null, false);
                Environment.CurrentDirectory = secondCwd;
                AppPathLayout second = AppPaths.Resolve(executable, local, null, false);

                AssertPathEqual(first.ExecutableDirectory, second.ExecutableDirectory);
                AssertPathEqual(first.DataRoot, second.DataRoot);
                string untrustedLegacy =
                    Path.GetFullPath(Path.Combine(secondCwd, "DesktopAICompanion.config"));
                foreach (string candidate in AppPaths.LegacySettingsFiles)
                    AssertFalse(
                        string.Equals(
                            Path.GetFullPath(candidate),
                            untrustedLegacy,
                            StringComparison.OrdinalIgnoreCase),
                        "Legacy settings lookup trusted the current directory.");
            }
            finally
            {
                Environment.CurrentDirectory = original;
            }
        }

        private static void TestBoundedDataMigration()
        {
            string directory = NewDirectory("paths-migration");
            string legacy = Path.Combine(directory, "legacy");
            string destination = Path.Combine(directory, "current");
            Directory.CreateDirectory(legacy);
            Directory.CreateDirectory(destination);

            File.WriteAllText(
                Path.Combine(legacy, "existing.txt"),
                "legacy must not overwrite",
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(legacy, "migrate.txt"),
                "safe legacy data",
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(legacy, "oversized.txt"),
                new string('x', 128),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(destination, "existing.txt"),
                "current wins",
                new UTF8Encoding(false));

            AssertTrue(
                AppPaths.TryMigrateFilesOnce(
                    destination,
                    legacy,
                    "*.txt",
                    10,
                    64,
                    128,
                    true),
                "The bounded migration did not complete.");
            AssertEqual(
                "current wins",
                File.ReadAllText(Path.Combine(destination, "existing.txt"), Encoding.UTF8),
                "Migration overwrote an existing destination file.");
            AssertEqual(
                "safe legacy data",
                File.ReadAllText(Path.Combine(destination, "migrate.txt"), Encoding.UTF8),
                "Eligible legacy data was not migrated.");
            AssertFalse(
                File.Exists(Path.Combine(destination, "oversized.txt")),
                "An oversized legacy file was migrated.");
            AssertTrue(
                File.Exists(Path.Combine(legacy, "migrate.txt")),
                "Migration deleted the legacy source.");

            File.WriteAllText(
                Path.Combine(legacy, "late.txt"),
                "must not migrate after completion",
                new UTF8Encoding(false));
            AssertTrue(
                AppPaths.TryMigrateFilesOnce(
                    destination,
                    legacy,
                    "*.txt",
                    10,
                    64,
                    128,
                    true),
                "A completed migration was not recognized.");
            AssertFalse(
                File.Exists(Path.Combine(destination, "late.txt")),
                "A completed one-time migration ran again.");

            string disabled = Path.Combine(directory, "disabled");
            AssertFalse(
                AppPaths.TryMigrateFilesOnce(
                    disabled,
                    legacy,
                    "*.txt",
                    10,
                    64,
                    128,
                    false),
                "A disabled legacy migration reported success.");
            AssertFalse(
                Directory.Exists(disabled),
                "A disabled legacy migration touched the destination.");

            string tooManyLegacy = Path.Combine(directory, "too-many-legacy");
            string tooManyDestination = Path.Combine(directory, "too-many-current");
            Directory.CreateDirectory(tooManyLegacy);
            File.WriteAllText(
                Path.Combine(tooManyLegacy, "one.txt"),
                "one",
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(tooManyLegacy, "two.txt"),
                "two",
                new UTF8Encoding(false));
            AssertFalse(
                AppPaths.TryMigrateFilesOnce(
                    tooManyDestination,
                    tooManyLegacy,
                    "*.txt",
                    1,
                    64,
                    128,
                    true),
                "An over-count legacy migration was marked complete.");
            AssertEqual(
                0,
                Directory.GetFiles(tooManyDestination, "*.txt").Length,
                "An over-count migration copied a partial file set.");
            AssertFalse(
                File.Exists(Path.Combine(
                    tooManyDestination,
                    ".legacy-migration-v1.complete")),
                "An over-count migration wrote a completion marker.");
        }

        private static void TestFreshDefaultsAndClamps()
        {
            string directory = NewDirectory("settings-defaults");
            string path = Path.Combine(directory, "settings.json");
            var store = new AppSettingsStore(path, new string[0]);
            AppSettingsDocument fresh = store.Load();

            AssertEqual(0.3, fresh.Volume, "Fresh volume was not exactly 0.3.");
            AssertEqual(1, fresh.ScaleLevel, "Fresh scale level was not 1.");
            AssertEqual(1, fresh.AutoStartPets, "Fresh pet count was not 1.");
            AssertEqual(6, fresh.SpeechDurationSeconds, "Fresh speech duration was not 6.");
            AssertTrue(File.Exists(path), "Fresh settings were not persisted.");

            fresh.Volume = double.NaN;
            fresh.ScaleLevel = 99;
            fresh.AutoStartPets = 99;
            fresh.SpeechDurationSeconds = 99;
            AssertTrue(store.Save(fresh), "Clamped settings could not be saved.");

            AppSettingsDocument high = new AppSettingsStore(path, null).Load();
            AssertEqual(0.3, high.Volume, "NaN volume did not return to 0.3.");
            AssertEqual(3, high.ScaleLevel, "Scale upper clamp failed.");
            AssertEqual(16, high.AutoStartPets, "Pet-count upper clamp failed.");
            AssertEqual(30, high.SpeechDurationSeconds, "Speech-duration upper clamp failed.");

            high.Volume = -10.0;
            high.ScaleLevel = -10;
            high.AutoStartPets = -10;
            high.SpeechDurationSeconds = -10;
            AssertTrue(store.Save(high), "Lower-bound settings could not be saved.");

            AppSettingsDocument low = new AppSettingsStore(path, null).Load();
            AssertEqual(0.0, low.Volume, "Volume lower clamp failed.");
            AssertEqual(1, low.ScaleLevel, "Scale lower clamp failed.");
            AssertEqual(1, low.AutoStartPets, "Pet-count lower clamp failed.");
            AssertEqual(2, low.SpeechDurationSeconds, "Speech-duration lower clamp failed.");
        }

        private static void TestLegacyVolumeMigration()
        {
            AssertLegacyVolume("0.3", 0.3, "normalized");
            AssertLegacyVolume("30", 0.3, "percentage");
        }

        private static void AssertLegacyVolume(string persisted, double expected, string suffix)
        {
            string directory = NewDirectory("settings-legacy-" + suffix);
            string legacy = Path.Combine(directory, "DesktopAICompanion.config");
            string current = Path.Combine(directory, "settings.json");
            File.WriteAllText(
                legacy,
                "<configuration><appSettings><add key=\"Volume\" value=\"" +
                persisted +
                "\"/><add key=\"Scale\" value=\"3\"/><add key=\"AutostartPets\" value=\"99\"/>" +
                "</appSettings></configuration>",
                new UTF8Encoding(false));

            AppSettingsDocument migrated =
                new AppSettingsStore(current, new[] { legacy }).Load();
            AssertEqual(expected, migrated.Volume, "Legacy " + suffix + " volume migrated incorrectly.");
            AssertEqual(3, migrated.ScaleLevel, "Legacy scale did not migrate.");
            AssertEqual(16, migrated.AutoStartPets, "Migrated pet count was not clamped.");
        }

        private static void TestOneTimeMigration()
        {
            string directory = NewDirectory("settings-one-time");
            string legacy = Path.Combine(directory, "user.config");
            string current = Path.Combine(directory, "settings.json");
            WriteLegacyUserSettings(legacy, "Volume", "30");

            AppSettingsDocument first =
                new AppSettingsStore(current, new[] { legacy }).Load();
            AssertEqual(0.3, first.Volume, "Initial user.config migration failed.");

            WriteLegacyUserSettings(legacy, "Volume", "80");
            AppSettingsDocument second =
                new AppSettingsStore(current, new[] { legacy }).Load();
            AssertEqual(0.3, second.Volume, "Legacy settings were re-imported over settings.json.");
        }

        private static void TestAtomicBackup()
        {
            string directory = NewDirectory("settings-backup");
            string path = Path.Combine(directory, "settings.json");
            var store = new AppSettingsStore(path, null);
            AppSettingsDocument settings = store.Load();
            settings.Volume = 0.7;
            AssertTrue(store.Save(settings), "Second atomic write failed.");
            AssertTrue(File.Exists(store.BackupPath), "Atomic replace did not create a backup.");

            JsonNode primary = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
            JsonNode backup = JsonNode.Parse(File.ReadAllText(store.BackupPath, Encoding.UTF8));
            AssertEqual(0.7, (double)primary["volume"], "Primary did not contain the new value.");
            AssertEqual(0.3, (double)backup["volume"], "Backup did not contain the previous value.");
            AssertEqual(0, Directory.GetFiles(directory, "*.tmp").Length, "A temporary file was left behind.");
        }

        private static void TestCorruptPrimaryRecovery()
        {
            string directory = NewDirectory("settings-recovery");
            string path = Path.Combine(directory, "settings.json");
            var originalStore = new AppSettingsStore(path, null);
            AppSettingsDocument settings = originalStore.Load();
            settings.Volume = 0.8;
            AssertTrue(originalStore.Save(settings), "Could not create recovery backup.");

            const string corrupt = "{ this is not valid json";
            File.WriteAllText(path, corrupt, new UTF8Encoding(false));
            var recoveryStore = new AppSettingsStore(path, null);
            AppSettingsDocument recovered = recoveryStore.Load();

            AssertEqual(0.3, recovered.Volume, "The previous valid backup was not recovered.");
            AssertTrue(
                !string.IsNullOrEmpty(recoveryStore.LastRecoveryFile) &&
                File.Exists(recoveryStore.LastRecoveryFile),
                "The corrupt primary was not preserved.");
            AssertEqual(
                corrupt,
                File.ReadAllText(recoveryStore.LastRecoveryFile, Encoding.UTF8),
                "The preserved corrupt file changed.");
            AssertEqual(
                0.3,
                (double)JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))["volume"],
                "The recovered primary does not match the backup.");
        }

        private static void TestFutureSchemaPreservation()
        {
            string directory = NewDirectory("settings-future");
            string path = Path.Combine(directory, "settings.json");
            const string future =
                "{\r\n  \"schemaVersion\": 99,\r\n  \"volume\": 0.7,\r\n" +
                "  \"scaleLevel\": 2,\r\n  \"futureOnly\": { \"keep\": true }\r\n}";
            byte[] original = new UTF8Encoding(false).GetBytes(future);
            File.WriteAllBytes(path, original);

            var store = new AppSettingsStore(path, null);
            AppSettingsDocument loaded = store.Load();
            AssertEqual(99, loaded.SchemaVersion, "Future schema version was changed in memory.");
            AssertEqual(0.7, loaded.Volume, "Known future-schema fields were not read.");
            AssertTrue(
                string.IsNullOrEmpty(store.LastRecoveryFile),
                "A future schema was incorrectly preserved as corrupt.");
            AssertBytesEqual(original, File.ReadAllBytes(path), "Loading rewrote the future settings file.");

            loaded.Volume = 0.2;
            AssertFalse(store.Save(loaded), "A future-schema settings document was overwritten.");
            AssertBytesEqual(original, File.ReadAllBytes(path), "Saving rewrote the future settings file.");

            var unloadedStore = new AppSettingsStore(path, null);
            AssertFalse(
                unloadedStore.Save(AppSettingsDocument.CreateDefault()),
                "Save-before-load overwrote an on-disk future schema.");
            AssertBytesEqual(original, File.ReadAllBytes(path), "Save-before-load changed the future file.");

            string mergePath = Path.Combine(directory, "settings-merge.json");
            File.WriteAllText(
                mergePath,
                "{\n" +
                "  \"schemaVersion\": 1,\n" +
                "  \"volume\": 0.3,\n" +
                "  \"scaleLevel\": 1,\n" +
                "  \"autoStartPets\": 1,\n" +
                "  \"multiScreen\": false,\n" +
                "  \"windowForeground\": false,\n" +
                "  \"stealTaskbarFocus\": false,\n" +
                "  \"speechEnabled\": true,\n" +
                "  \"speechDurationSeconds\": 6,\n" +
                "  \"xml\": \"\",\n" +
                "  \"images\": \"\",\n" +
                "  \"icon\": \"\",\n" +
                "  \"futureSameSchema\": { \"keep\": true }\n" +
                "}",
                new UTF8Encoding(false));

            var firstStore = new AppSettingsStore(mergePath, null);
            var secondStore = new AppSettingsStore(mergePath, null);
            AppSettingsDocument first = firstStore.Load();
            AppSettingsDocument second = secondStore.Load();
            first.Volume = 0.6;
            AssertTrue(firstStore.Save(first), "First stale-snapshot settings save failed.");
            second.SpeechEnabled = false;
            AssertTrue(secondStore.Save(second), "Second stale-snapshot settings save failed.");

            JsonNode merged = JsonNode.Parse(File.ReadAllText(mergePath, Encoding.UTF8));
            AssertEqual(0.6, (double)merged["volume"],
                "A stale settings save lost another process's volume change.");
            AssertFalse((bool)merged["speechEnabled"],
                "The stale settings writer did not save its own change.");
            AssertTrue((bool)merged["futureSameSchema"]["keep"],
                "A same-schema unknown settings field was discarded.");
        }

        private static void TestSettingsPetMixMigration()
        {
            string directory = NewDirectory("settings-petmix-migrate");
            string path = Path.Combine(directory, "settings.json");
            // A schema-v1 doc with a legacy pet count and blob and NO "pets" list.
            File.WriteAllText(
                path,
                "{\n" +
                "  \"schemaVersion\": 1,\n" +
                "  \"volume\": 0.3,\n" +
                "  \"scaleLevel\": 1,\n" +
                "  \"autoStartPets\": 3,\n" +
                "  \"speechDurationSeconds\": 6,\n" +
                "  \"xml\": \"legacy-blob\"\n" +
                "}",
                new UTF8Encoding(false));

            AppSettingsDocument migrated = new AppSettingsStore(path, null).Load();
            AssertEqual(2, migrated.SchemaVersion, "A v1 doc was not upgraded to schema 2.");
            AssertTrue(migrated.Pets != null && migrated.Pets.Count == 1,
                "v1 migration did not seed a single pet-mix entry.");
            AssertTrue(migrated.Pets[0].Id == "" && migrated.Pets[0].Count == 3,
                "v1 migration did not carry the legacy count onto the active ('') pet.");
            AssertEqual("legacy-blob", migrated.Xml, "v1 migration lost the legacy pet XML.");

            JsonNode onDisk = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
            AssertEqual(2, (int)onDisk["schemaVersion"], "The upgraded schema version was not persisted.");
            AssertTrue(onDisk["pets"] is JsonArray, "The migrated pets array was not written to disk.");
        }

        private static void TestSettingsPetMixValidation()
        {
            string directory = NewDirectory("settings-petmix-validate");
            string path = Path.Combine(directory, "settings.json");
            var store = new AppSettingsStore(path, new string[0]);
            AppSettingsDocument doc = store.Load();

            doc.Pets = new List<CompanionCountEntry>
            {
                new CompanionCountEntry { Id = "pink_sheep", Count = 2 },
                new CompanionCountEntry { Id = "pink_sheep", Count = 3 },          // dupe -> summed to 5
                new CompanionCountEntry { Id = "../evil", Count = 1 },             // path separator -> dropped
                new CompanionCountEntry { Id = "", Count = 0 },                    // floor to 1, "" kept
                null,                                                         // dropped
                new CompanionCountEntry { Id = new string('x', 200), Count = 1 },  // over-long id -> dropped
                // A preview pet's synthetic registry id, which must never reach this list in the first
                // place (transient types are excluded from the on-screen mix). This is the second line of
                // defence: the ':' makes it fail IsAcceptablePetId, so even a leak upstream cannot leave a
                // dead id in the startup mix, where it would silently cost the user a pet on next launch.
                new CompanionCountEntry { Id = "preview:abc123", Count = 1 }
            };
            AssertTrue(store.Save(doc), "Pet-mix validation doc could not be saved.");

            AppSettingsDocument reloaded = new AppSettingsStore(path, null).Load();
            AssertTrue(reloaded.Pets.Count == 2, "Pet-mix was not deduped/filtered to two entries.");
            foreach (CompanionCountEntry entry in reloaded.Pets)
                AssertTrue(entry.Id.IndexOf(':') < 0,
                    "A synthetic preview id survived pet-mix validation and would be spawned at startup.");
            AssertTrue(reloaded.Pets[0].Id == "pink_sheep" && reloaded.Pets[0].Count == 5,
                "Duplicate ids were not summed.");
            AssertTrue(reloaded.Pets[1].Id == "" && reloaded.Pets[1].Count == 1,
                "The active ('') entry with a zero count was not kept and floored to 1.");

            // Running-total cap across all types.
            var capStore = new AppSettingsStore(path, null);
            AppSettingsDocument cap = capStore.Load();
            cap.Pets = new List<CompanionCountEntry>
            {
                new CompanionCountEntry { Id = "a", Count = 10 },
                new CompanionCountEntry { Id = "b", Count = 10 }
            };
            AssertTrue(capStore.Save(cap), "Pet-mix cap doc could not be saved.");
            AppSettingsDocument capped = new AppSettingsStore(path, null).Load();
            int total = 0;
            foreach (CompanionCountEntry entry in capped.Pets) total += entry.Count;
            AssertEqual(16, total, "The running pet total was not capped to 16.");
            AssertTrue(capped.Pets.Count == 2 && capped.Pets[1].Count == 6,
                "The cap did not truncate the second type to fit within 16.");
        }

        private static void TestSettingsPetMixMerge()
        {
            string directory = NewDirectory("settings-petmix-merge");
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(
                path,
                "{\n" +
                "  \"schemaVersion\": 2,\n" +
                "  \"volume\": 0.3,\n" +
                "  \"scaleLevel\": 1,\n" +
                "  \"autoStartPets\": 1,\n" +
                "  \"speechEnabled\": true,\n" +
                "  \"speechDurationSeconds\": 6,\n" +
                "  \"xml\": \"\",\n" +
                "  \"pets\": [ { \"id\": \"\", \"count\": 1 } ],\n" +
                "  \"futureSameSchema\": { \"keep\": true }\n" +
                "}",
                new UTF8Encoding(false));

            var firstStore = new AppSettingsStore(path, null);
            var secondStore = new AppSettingsStore(path, null);
            AppSettingsDocument first = firstStore.Load();
            AppSettingsDocument second = secondStore.Load();

            first.Pets = new List<CompanionCountEntry> { new CompanionCountEntry { Id = "red_sheep", Count = 2 } };
            AssertTrue(firstStore.Save(first), "First pet-mix save failed.");
            second.SpeechEnabled = false;   // stale writer, unrelated field
            AssertTrue(secondStore.Save(second), "Second stale save failed.");

            JsonNode merged = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
            JsonArray pets = (JsonArray)merged["pets"];
            AssertTrue(
                pets.Count == 1 && (string)pets[0]["id"] == "red_sheep" && (int)pets[0]["count"] == 2,
                "A stale save lost the other process's pet-mix change.");
            AssertFalse((bool)merged["speechEnabled"], "The stale writer did not save its own change.");
            AssertTrue((bool)merged["futureSameSchema"]["keep"],
                "A same-schema unknown field was discarded across the pet-mix merge.");
        }

        private static void TestSettingsPetSizeValidation()
        {
            string directory = NewDirectory("settings-petsize-validate");
            string path = Path.Combine(directory, "settings.json");
            var store = new AppSettingsStore(path, new string[0]);
            AppSettingsDocument doc = store.Load();

            doc.PetSizes = new List<CompanionSizeEntry>
            {
                new CompanionSizeEntry { Id = "pingus", Level = 2 },
                new CompanionSizeEntry { Id = "pingus", Level = 3 },              // dupe -> last wins (3)
                new CompanionSizeEntry { Id = "../evil", Level = 2 },             // path separator -> dropped
                new CompanionSizeEntry { Id = "follows_global", Level = 0 },      // level 0 -> dropped (no override)
                new CompanionSizeEntry { Id = "outofrange", Level = 9 },          // level out of range -> dropped
                new CompanionSizeEntry { Id = "", Level = 1 },                    // "" (active pet) allowed
                null,                                                         // dropped
                new CompanionSizeEntry { Id = new string('x', 200), Level = 1 }   // over-long id -> dropped
            };
            AssertTrue(store.Save(doc), "Pet-size validation doc could not be saved.");

            AppSettingsDocument reloaded = new AppSettingsStore(path, null).Load();
            AssertTrue(reloaded.PetSizes.Count == 2, "Pet-sizes were not deduped/filtered to two entries.");
            AssertTrue(reloaded.PetSizes[0].Id == "pingus" && reloaded.PetSizes[0].Level == 3,
                "Duplicate size ids did not keep the last level.");
            AssertTrue(reloaded.PetSizes[1].Id == "" && reloaded.PetSizes[1].Level == 1,
                "The active ('') size override was not kept.");
        }

        private static void TestSettingsThemeMode()
        {
            string directory = NewDirectory("settings-thememode");
            string path = Path.Combine(directory, "settings.json");
            AppSettingsDocument fresh = new AppSettingsStore(path, new string[0]).Load();
            AssertTrue(fresh.ThemeMode == "system", "Fresh theme mode was not 'system'.");

            var store = new AppSettingsStore(path, null);
            AppSettingsDocument doc = store.Load();
            doc.ThemeMode = "DARK";                                   // case-insensitive on load
            AssertTrue(store.Save(doc), "Theme doc could not be saved.");
            AssertTrue(new AppSettingsStore(path, null).Load().ThemeMode == "dark",
                "Theme mode was not normalized to lowercase 'dark'.");

            var store2 = new AppSettingsStore(path, null);
            AppSettingsDocument bad = store2.Load();
            bad.ThemeMode = "purple";                                 // invalid -> falls back to system
            AssertTrue(store2.Save(bad), "Bad theme doc could not be saved.");
            AssertTrue(new AppSettingsStore(path, null).Load().ThemeMode == "system",
                "Invalid theme mode did not fall back to 'system'.");
        }

        private static void TestSettingsAudioDevice()
        {
            string directory = NewDirectory("settings-audiodevice");
            string path = Path.Combine(directory, "settings.json");
            AppSettingsDocument fresh = new AppSettingsStore(path, new string[0]).Load();
            AssertTrue(fresh.AudioDeviceId == "", "Fresh audio device id was not empty (default).");

            var store = new AppSettingsStore(path, null);
            AppSettingsDocument doc = store.Load();
            doc.AudioDeviceId = "  42017c51-bf96-47a1-afcc-817d9f324e76  ";   // trimmed on normalize
            AssertTrue(store.Save(doc), "Audio-device doc could not be saved.");
            AssertTrue(new AppSettingsStore(path, null).Load().AudioDeviceId == "42017c51-bf96-47a1-afcc-817d9f324e76",
                "Audio device id was not trimmed on load.");

            var store2 = new AppSettingsStore(path, null);
            AppSettingsDocument bad = store2.Load();
            bad.AudioDeviceId = new string('x', 200);   // over-long -> "" (default)
            AssertTrue(store2.Save(bad), "Over-long audio-device doc could not be saved.");
            AssertTrue(new AppSettingsStore(path, null).Load().AudioDeviceId == "",
                "Over-long audio device id did not fall back to empty.");
        }

        private static void TestSettingsMutedPets()
        {
            string directory = NewDirectory("settings-mutedpets");
            string path = Path.Combine(directory, "settings.json");
            var store = new AppSettingsStore(path, new string[0]);
            AppSettingsDocument doc = store.Load();
            doc.MutedPets = new List<string>
            {
                "pingus",
                "pingus",               // dupe -> one
                "",                     // active pet, allowed
                "../evil",              // path separator -> dropped
                new string('x', 200)    // over-long -> dropped
            };
            AssertTrue(store.Save(doc), "Muted-pets doc could not be saved.");
            AppSettingsDocument reloaded = new AppSettingsStore(path, null).Load();
            AssertTrue(reloaded.MutedPets.Count == 2, "Muted-pets were not deduped/filtered to two entries.");
            AssertTrue(reloaded.MutedPets.Contains("pingus") && reloaded.MutedPets.Contains(""),
                "Muted-pets did not keep 'pingus' and the active ('') entry.");
        }

        private static void TestSettingsActivePetId()
        {
            string directory = NewDirectory("settings-activepet");
            string path = Path.Combine(directory, "settings.json");
            AppSettingsDocument fresh = new AppSettingsStore(path, new string[0]).Load();
            AssertTrue(fresh.ActivePetId == "eSheep", "Fresh active pet id was not the built-in 'eSheep'.");

            var store = new AppSettingsStore(path, null);
            AppSettingsDocument doc = store.Load();
            doc.ActivePetId = "pink_sheep";
            AssertTrue(store.Save(doc), "Active-pet doc could not be saved.");
            AssertTrue(new AppSettingsStore(path, null).Load().ActivePetId == "pink_sheep",
                "A valid active pet id was not kept.");

            var store2 = new AppSettingsStore(path, null);
            AppSettingsDocument bad = store2.Load();
            bad.ActivePetId = "../evil";   // unsafe -> falls back to the built-in
            AssertTrue(store2.Save(bad), "Bad active-pet doc could not be saved.");
            AssertTrue(new AppSettingsStore(path, null).Load().ActivePetId == "eSheep",
                "An unsafe active pet id did not fall back to 'eSheep'.");
        }

        /// <summary>
        /// The monthly module-update check's persisted toggle. The nullable-bool contract is the whole point:
        /// a doc written before the field existed must load as ABSENT and be read as ON, because the same trap
        /// with a plain bool once left SuppressRepeats silently disabled for everyone who upgraded. Also pins
        /// the cross-process merge clause, without which a stale writer would quietly drop a user's opt-out.
        /// </summary>
        private static void TestSettingsMonthlyModuleUpdateCheck()
        {
            string directory = NewDirectory("settings-monthly-update-check");
            string path = Path.Combine(directory, "settings.json");

            AppSettingsDocument fresh = new AppSettingsStore(path, new string[0]).Load();
            AssertTrue(fresh.MonthlyModuleUpdateCheck == true,
                "A fresh install did not default the monthly module-update check to on.");

            // A pre-1.4.2 doc has no such key at all: it must stay ABSENT (null), which LocalData reads as on.
            string olderPath = Path.Combine(directory, "older.json");
            File.WriteAllText(
                olderPath,
                "{\n" +
                "  \"schemaVersion\": 2,\n" +
                "  \"volume\": 0.3,\n" +
                "  \"scaleLevel\": 1,\n" +
                "  \"speechDurationSeconds\": 6\n" +
                "}",
                new UTF8Encoding(false));
            AppSettingsDocument older = new AppSettingsStore(olderPath, null).Load();
            AssertTrue(older.MonthlyModuleUpdateCheck == null,
                "An upgraded doc invented a value for the monthly module-update check instead of leaving it absent.");

            // An explicit opt-out round-trips and is distinguishable from absent.
            var store = new AppSettingsStore(path, null);
            AppSettingsDocument doc = store.Load();
            doc.MonthlyModuleUpdateCheck = false;
            AssertTrue(store.Save(doc), "The monthly module-update opt-out could not be saved.");
            AssertTrue(new AppSettingsStore(path, null).Load().MonthlyModuleUpdateCheck == false,
                "The monthly module-update opt-out did not survive a reload.");

            // Cross-process merge: a stale writer changing something else must not resurrect the check.
            var firstStore = new AppSettingsStore(path, null);
            var secondStore = new AppSettingsStore(path, null);
            AppSettingsDocument first = firstStore.Load();
            AppSettingsDocument second = secondStore.Load();
            first.MonthlyModuleUpdateCheck = true;
            AssertTrue(firstStore.Save(first), "First monthly-check save failed.");
            second.Volume = 0.9;   // stale snapshot: still carries the pre-change opt-out
            AssertTrue(secondStore.Save(second), "Second (stale) monthly-check save failed.");
            AppSettingsDocument merged = new AppSettingsStore(path, null).Load();
            AssertTrue(merged.MonthlyModuleUpdateCheck == true,
                "A stale writer lost the other process's monthly module-update change.");
            AssertEqual(0.9, merged.Volume, "The stale writer did not save its own change.");
        }

        private static void TestSettingsRandomDrop()
        {
            // Fresh defaults: off / 15 minutes / plus-or-minus 3.
            string directory = NewDirectory("settings-randomdrop");
            string path = Path.Combine(directory, "settings.json");
            AppSettingsDocument fresh = new AppSettingsStore(path, new string[0]).Load();
            AssertTrue(fresh.RandomDropEnabled == false, "Fresh random-drop was not off by default.");
            AssertTrue(fresh.RandomDropMinutes == 15, "Fresh random-drop interval was not 15 minutes.");
            AssertTrue(fresh.RandomDropJitterMinutes == 3, "Fresh random-drop jitter was not 3 minutes.");

            // Custom values round-trip.
            var store = new AppSettingsStore(path, null);
            AppSettingsDocument doc = store.Load();
            doc.RandomDropEnabled = true;
            doc.RandomDropMinutes = 42;
            doc.RandomDropJitterMinutes = 7;
            AssertTrue(store.Save(doc), "Random-drop doc could not be saved.");
            AppSettingsDocument back = new AppSettingsStore(path, null).Load();
            AssertTrue(back.RandomDropEnabled == true && back.RandomDropMinutes == 42 && back.RandomDropJitterMinutes == 7,
                "Custom random-drop values were not preserved.");

            // Clamp: interval to 1..9999, jitter to 0..center-1 (so the interval stays positive).
            var store2 = new AppSettingsStore(path, null);
            AppSettingsDocument bad = store2.Load();
            bad.RandomDropMinutes = 20000;      // over the ceiling -> 9999
            bad.RandomDropJitterMinutes = 50000; // >= center -> center-1
            AssertTrue(store2.Save(bad), "Out-of-range random-drop doc could not be saved.");
            AppSettingsDocument clamped = new AppSettingsStore(path, null).Load();
            AssertTrue(clamped.RandomDropMinutes == 9999, "Random-drop interval was not clamped to 9999.");
            AssertTrue(clamped.RandomDropJitterMinutes == 9998, "Random-drop jitter was not clamped below the center.");

            var store3 = new AppSettingsStore(path, null);
            AppSettingsDocument tight = store3.Load();
            tight.RandomDropMinutes = 10;
            tight.RandomDropJitterMinutes = 100;   // jitter must stay below the (small) center
            AssertTrue(store3.Save(tight), "Tight-interval random-drop doc could not be saved.");
            AppSettingsDocument tightBack = new AppSettingsStore(path, null).Load();
            AssertTrue(tightBack.RandomDropMinutes == 10 && tightBack.RandomDropJitterMinutes == 9,
                "Random-drop jitter was not clamped to center-1 for a small interval.");

            // A pre-rehome settings.json has the keys absent; they must load as null (the signal LocalData's
            // one-time migration uses to seed from the legacy ai-settings.json), not silently defaulted.
            string legacyDir = NewDirectory("settings-randomdrop-absent");
            string legacyPath = Path.Combine(legacyDir, "settings.json");
            File.WriteAllText(legacyPath, "{ \"schemaVersion\": 2 }");
            AppSettingsDocument absent = new AppSettingsStore(legacyPath, null).Load();
            AssertTrue(!absent.RandomDropEnabled.HasValue && !absent.RandomDropMinutes.HasValue && !absent.RandomDropJitterMinutes.HasValue,
                "Absent random-drop keys did not load as null (migration detection would break).");
        }

        private static void TestSettingsTriggerSpeech()
        {
            string directory = NewDirectory("settings-triggerspeech");
            string path = Path.Combine(directory, "settings.json");

            // Fresh: no choice recorded, i.e. "default & random".
            AppSettingsDocument fresh = new AppSettingsStore(path, new string[0]).Load();
            AssertTrue(fresh.TriggerSpeech != null && fresh.TriggerSpeech.Count == 0,
                "Fresh trigger-speech list was not empty.");

            // The global entry (id "") round-trips. Per-pet ids are reserved for a later feature but must
            // already persist, so that work needs no settings migration.
            var store = new AppSettingsStore(path, null);
            AppSettingsDocument doc = store.Load();
            doc.TriggerSpeech = new List<TriggerSpeechEntry>
            {
                new TriggerSpeechEntry { Id = "", Module = "aibrain" },
                new TriggerSpeechEntry { Id = "eSheep", Module = "fortunes" },
            };
            AssertTrue(store.Save(doc), "Trigger-speech doc could not be saved.");
            AppSettingsDocument back = new AppSettingsStore(path, null).Load();
            AssertTrue(back.TriggerSpeech != null && back.TriggerSpeech.Count == 2,
                "Trigger-speech entries were not preserved.");
            AssertTrue(back.TriggerSpeech[0].Id == "" && back.TriggerSpeech[0].Module == "aibrain",
                "The global trigger-speech entry did not round-trip.");
            AssertTrue(back.TriggerSpeech[1].Id == "eSheep" && back.TriggerSpeech[1].Module == "fortunes",
                "The per-pet trigger-speech entry did not round-trip.");

            // Duplicate ids collapse (last wins), matching the per-pet-size list's normalization.
            var store2 = new AppSettingsStore(path, null);
            AppSettingsDocument dupes = store2.Load();
            dupes.TriggerSpeech = new List<TriggerSpeechEntry>
            {
                new TriggerSpeechEntry { Id = "", Module = "fortunes" },
                new TriggerSpeechEntry { Id = "", Module = "aibrain" },
            };
            AssertTrue(store2.Save(dupes), "Duplicate trigger-speech doc could not be saved.");
            AppSettingsDocument collapsed = new AppSettingsStore(path, null).Load();
            AssertTrue(collapsed.TriggerSpeech.Count == 1 && collapsed.TriggerSpeech[0].Module == "aibrain",
                "Duplicate trigger-speech ids did not collapse to the last value.");

            // A doc written before this field existed loads as an empty list, not a crash.
            string legacyDir = NewDirectory("settings-triggerspeech-absent");
            string legacyPath = Path.Combine(legacyDir, "settings.json");
            File.WriteAllText(legacyPath, "{ \"schemaVersion\": 2 }");
            AppSettingsDocument legacy = new AppSettingsStore(legacyPath, null).Load();
            AssertTrue(legacy.TriggerSpeech != null && legacy.TriggerSpeech.Count == 0,
                "An absent trigger-speech key did not load as an empty list.");
        }

        private static void TestSettingsLockFailureFallback()
        {
            string directory = NewDirectory("settings-lock-fallback");
            string path = Path.Combine(directory, "settings.json");
            var store = new AppSettingsStore(path, null, 75);
            AppSettingsDocument fallback;

            using (var heldLock = new FileStream(
                path + ".lock",
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                fallback = store.Load();
                AssertEqual(
                    0.3,
                    fallback.Volume,
                    "Lock failure did not return normalized defaults.");
                AssertTrue(
                    store.IsReadOnlyFallback &&
                    !string.IsNullOrWhiteSpace(store.LastLoadWarning),
                    "Lock failure did not expose the read-only fallback warning.");
                AssertFalse(
                    store.Save(fallback),
                    "A fallback snapshot was allowed to overwrite unread settings.");
            }

            AppSettingsDocument recovered = store.Load();
            AssertFalse(
                store.IsReadOnlyFallback,
                "A successful reload did not leave read-only fallback mode.");
            AssertTrue(
                string.IsNullOrEmpty(store.LastLoadWarning),
                "A successful reload retained the storage warning.");
            AssertTrue(
                File.Exists(path),
                "A successful reload did not persist fresh settings.");
            recovered.Volume = 0.6;
            AssertTrue(
                store.Save(recovered),
                "Settings remained unwritable after the lock was released.");
        }

        private static void TestScaleMapping()
        {
            AssertEqual(1, ScalePolicy.FactorFromLevel(1), "Scale level 1 did not map to 1x.");
            AssertEqual(2, ScalePolicy.FactorFromLevel(2), "Scale level 2 did not map to 2x.");
            AssertEqual(4, ScalePolicy.FactorFromLevel(3), "Scale level 3 did not map to 4x.");
            AssertEqual(1, ScalePolicy.FactorFromLevel(-1), "Low scale level did not clamp.");
            AssertEqual(4, ScalePolicy.FactorFromLevel(99), "High scale level did not clamp.");
            AssertEqual(42, ScalePolicy.Scale(21, 2), "Integer scaling failed.");
            AssertEqual(int.MaxValue, ScalePolicy.Scale(int.MaxValue, 4), "Positive scaling did not saturate.");
            AssertEqual(int.MinValue, ScalePolicy.Scale(int.MinValue, 4), "Negative scaling did not saturate.");
            AssertEqual(
                4,
                ScalePolicy.FitFactorForFrame(4, 64, 64, 256),
                "A frame that fits at 4x was downgraded.");
            AssertEqual(
                2,
                ScalePolicy.FitFactorForFrame(4, 65, 64, 256),
                "A 4x frame just over the limit did not downgrade to 2x.");
            AssertEqual(
                1,
                ScalePolicy.FitFactorForFrame(4, 129, 64, 256),
                "A frame too large for 2x did not downgrade to 1x.");
            AssertEqual(
                "4x",
                ScalePolicy.StatusText(3, 4),
                "Matching requested and active scale text was incorrect.");
            AssertEqual(
                "4x requested (2x active)",
                ScalePolicy.StatusText(3, 2),
                "A downgraded active scale was not disclosed.");
        }

        private static void TestMonitorLayouts()
        {
            Rectangle[] monitors =
            {
                new Rectangle(0, 0, 1920, 1080),
                new Rectangle(1920, 0, 2560, 1440),
                new Rectangle(-1280, 0, 1280, 1024),
                new Rectangle(0, -1200, 1920, 1200),
                new Rectangle(0, 1080, 1600, 900),
                new Rectangle(-2560, -1440, 2560, 1440)
            };

            foreach (Rectangle monitor in monitors)
            {
                Point local = new Point(37, 59);
                Point expectedVirtual = new Point(monitor.X + 37, monitor.Y + 59);
                Point virtualPoint = DesktopGeometry.MonitorLocalToVirtual(local, monitor);
                AssertEqual(expectedVirtual, virtualPoint, "Monitor-local to virtual conversion failed.");
                AssertEqual(
                    local,
                    DesktopGeometry.VirtualToMonitorLocal(virtualPoint, monitor),
                    "Virtual to monitor-local round trip failed.");
            }
        }

        private static void TestCaptureMonitorSelection()
        {
            Rectangle primary = new Rectangle(0, 0, 1920, 1080);
            Rectangle left = new Rectangle(-2560, -200, 2560, 1440);
            Rectangle upper = new Rectangle(0, -1200, 1920, 1200);
            Rectangle[] monitors = { primary, left, upper };

            AssertEqual(
                left,
                DesktopGeometry.SelectCaptureMonitor(
                    new Rectangle(-2300, 40, 1200, 900),
                    primary,
                    monitors),
                "A foreground window on the left monitor selected primary.");
            AssertEqual(
                upper,
                DesktopGeometry.SelectCaptureMonitor(
                    new Rectangle(100, -1100, 1500, 900),
                    left,
                    monitors),
                "A foreground window on the upper monitor selected the pet monitor.");
            AssertEqual(
                left,
                DesktopGeometry.SelectCaptureMonitor(
                    Rectangle.Empty,
                    left,
                    monitors),
                "Missing foreground geometry did not fall back to the pet monitor.");
            AssertEqual(
                primary,
                DesktopGeometry.SelectCaptureMonitor(
                    new Rectangle(5000, 300, 500, 500),
                    left,
                    monitors),
                "An off-screen foreground window did not select the nearest monitor.");
            AssertEqual(
                left,
                DesktopGeometry.SelectCaptureMonitor(
                    new Rectangle(-500, 100, 1000, 800),
                    left,
                    monitors),
                "Equal monitor overlap did not prefer the pet's monitor.");
        }

        private static void TestWindowLandingCoordinateSentinel()
        {
            AssertTrue(
                DesktopGeometry.CrossesDescendingBoundary(100.75, 0.30, 101),
                "A fractional downward step crossing a window top was missed.");
            AssertFalse(
                DesktopGeometry.CrossesDescendingBoundary(100.75, 0.20, 101),
                "A fractional downward step short of a window top collided early.");
            AssertFalse(
                DesktopGeometry.CrossesDescendingBoundary(101.0, 0.30, 101),
                "A pet already at a window top was treated as newly crossing it.");

            WindowTopHit upperMonitor = WindowTopHit.At(-875);
            AssertTrue(
                upperMonitor.Found && upperMonitor.Top == -875,
                "A negative upper-monitor window coordinate was treated as no hit.");

            WindowTopHit formerlyAmbiguous = WindowTopHit.At(-1);
            AssertTrue(
                formerlyAmbiguous.Found && formerlyAmbiguous.Top == -1,
                "The former -1 sentinel is not usable as a real window coordinate.");

            AssertFalse(
                WindowTopHit.None.Found,
                "The explicit no-window result was marked as a hit.");
        }

        private static void TestWindowFollowRelativeScaling()
        {
            int scaledLeft;
            AssertTrue(
                DesktopGeometry.TryScaleWindowRelativeX(
                    150,
                    100,
                    300,
                    400,
                    800,
                    out scaledLeft),
                "A valid window resize was rejected.");
            AssertEqual(
                500,
                scaledLeft,
                "The pet's relative horizontal position was not preserved.");

            AssertFalse(
                DesktopGeometry.TryScaleWindowRelativeX(
                    150,
                    100,
                    100,
                    400,
                    800,
                    out scaledLeft),
                "A collapsed previous window was accepted as a divisor.");
            AssertFalse(
                DesktopGeometry.TryScaleWindowRelativeX(
                    150,
                    100,
                    300,
                    400,
                    400,
                    out scaledLeft),
                "A collapsed current window was accepted.");

            AssertTrue(
                DesktopGeometry.TryScaleWindowRelativeX(
                    int.MaxValue,
                    int.MinValue,
                    int.MinValue + 1,
                    int.MinValue,
                    int.MaxValue,
                    out scaledLeft),
                "A large positive resize offset was rejected.");
            AssertEqual(
                int.MaxValue,
                scaledLeft,
                "A large positive resize offset did not saturate safely.");

            AssertTrue(
                DesktopGeometry.TryScaleWindowRelativeX(
                    int.MinValue,
                    int.MaxValue - 1,
                    int.MaxValue,
                    int.MinValue,
                    int.MaxValue,
                    out scaledLeft),
                "A large negative resize offset was rejected.");
            AssertEqual(
                int.MinValue,
                scaledLeft,
                "A large negative resize offset did not saturate safely.");
        }

        private static void TestRetiringPetRuntimeOwnership()
        {
            var registry = new RetiringValueRegistry<object>();
            object retiring = new object();
            object second = new object();
            AssertTrue(
                registry.Add(retiring) &&
                !registry.Add(retiring) &&
                registry.Add(second) &&
                registry.Count == 2,
                "Retiring runtime owners were not tracked exactly once.");
            AssertTrue(
                object.ReferenceEquals(retiring, registry.FirstOrDefault()) ||
                object.ReferenceEquals(second, registry.FirstOrDefault()),
                "A live retiring runtime owner could not marshal reload work.");

            IList<object> reloadDrain = registry.Drain();
            AssertEqual(
                2,
                reloadDrain.Count,
                "Reload did not capture every retiring runtime owner.");
            AssertEqual(
                0,
                registry.Count,
                "Reload left a retiring owner attached to disposed runtime state.");
            AssertFalse(
                registry.Remove(retiring),
                "A drained retiring owner remained registered.");
        }

        private static void TestSpeechGeometryAndUnicode()
        {
            string emojiText = "A" + char.ConvertFromUtf32(0x1F642) + "B";
            int afterAscii =
                UnicodeTextProgress.NextCodePointBoundary(emojiText, 0);
            int afterEmoji =
                UnicodeTextProgress.NextCodePointBoundary(
                    emojiText,
                    afterAscii);
            AssertEqual(1, afterAscii, "The typewriter skipped the first character.");
            AssertEqual(
                3,
                afterEmoji,
                "The typewriter exposed half of a UTF-16 surrogate pair.");
            AssertTrue(
                char.ConvertToUtf32(emojiText, afterAscii) == 0x1F642,
                "The complete emoji was not available at the next paint boundary.");

            string emoji = char.ConvertFromUtf32(0x1F642);
            string fitsExactly = new string('a', 14) + emoji;
            string splitAtBoundary = new string('a', 15) + emoji;
            string afterBoundary = new string('a', 16) + emoji;
            AssertEqual(
                fitsExactly,
                UnicodeTextProgress.TruncateAtCodePointBoundary(
                    fitsExactly,
                    16),
                "Pet-name truncation removed an emoji that fit exactly.");
            AssertEqual(
                new string('a', 15),
                UnicodeTextProgress.TruncateAtCodePointBoundary(
                    splitAtBoundary,
                    16),
                "Pet-name truncation retained half of a surrogate pair.");
            AssertEqual(
                new string('a', 16),
                UnicodeTextProgress.TruncateAtCodePointBoundary(
                    afterBoundary,
                    16),
                "Pet-name truncation crossed the 16-code-unit boundary.");

            SpriteSpeechAnchor leftFacing =
                DesktopGeometry.GetSpriteSpeechAnchor(
                    -40.5,
                    100.25,
                    120,
                    80,
                    true);
            SpriteSpeechAnchor rightFacing =
                DesktopGeometry.GetSpriteSpeechAnchor(
                    -40.5,
                    100.25,
                    120,
                    80,
                    false);
            AssertEqual(-0.5, leftFacing.X,
                "Left-facing mouth did not use the full logical sprite width.");
            AssertEqual(39.5, rightFacing.X,
                "Right-facing mouth did not use the full logical sprite width.");
            AssertEqual(100.25, leftFacing.Top,
                "Speech anchor top did not use the logical sprite top.");
            AssertEqual(180.25, leftFacing.Bottom,
                "Speech anchor bottom did not use the full logical sprite height.");
        }

        private static void TestChildPositionReproduction()
        {
            Rectangle monitor = new Rectangle(-2560, -1440, 2560, 1440);
            Point parentVirtual = new Point(-2160, -1140);
            Point parentLocal = DesktopGeometry.VirtualToMonitorLocal(parentVirtual, monitor);
            Point childOffset = new Point(25, -40);
            Point childLocal = new Point(
                parentLocal.X + childOffset.X,
                parentLocal.Y + childOffset.Y);
            Point childVirtual = DesktopGeometry.MonitorLocalToVirtual(childLocal, monitor);

            AssertEqual(
                new Point(parentVirtual.X + childOffset.X, parentVirtual.Y + childOffset.Y),
                childVirtual,
                "Child positioning applied the monitor origin more than once.");
        }

        private static void TestMetricsAndFullscreen()
        {
            Rectangle monitor = new Rectangle(-1920, 1200, 1920, 1080);
            Rectangle workArea = new Rectangle(-1920, 1200, 1920, 1040);
            ScreenMetrics metrics = DesktopGeometry.Metrics(monitor, workArea);
            AssertEqual(1920, metrics.ScreenWidth, "Screen width included an origin.");
            AssertEqual(1080, metrics.ScreenHeight, "Screen height included an origin.");
            AssertEqual(1920, metrics.WorkAreaWidth, "Work-area width included an origin.");
            AssertEqual(1040, metrics.WorkAreaHeight, "Work-area height included an origin.");
            AssertEqual(
                new Point(-960, 1740),
                DesktopGeometry.Center(monitor),
                "Rectangle center swapped or ignored an axis.");

            AssertTrue(
                DesktopGeometry.IsFullscreenOnMonitor(monitor, monitor),
                "Exact monitor bounds were not detected as fullscreen.");
            AssertFalse(
                DesktopGeometry.IsFullscreenOnMonitor(
                    new Rectangle(-1800, 1300, 800, 600), monitor),
                "A normal window was detected as fullscreen.");
            AssertFalse(
                DesktopGeometry.IsFullscreenOnMonitor(
                    new Rectangle(0, 0, 1920, 1080), monitor),
                "A fullscreen window on another monitor matched this monitor.");
            AssertFalse(
                DesktopGeometry.IsFullscreenOnMonitor(
                    new Rectangle(-1800, 1200, 1920, 1080), monitor),
                "A monitor-sized window shifted right was detected as fullscreen.");
            AssertFalse(
                DesktopGeometry.IsFullscreenOnMonitor(
                    new Rectangle(-2040, 1200, 1920, 1080), monitor),
                "A monitor-sized window shifted left was detected as fullscreen.");
            AssertFalse(
                DesktopGeometry.IsFullscreenOnMonitor(
                    new Rectangle(-1920, 1260, 1920, 1080), monitor),
                "A monitor-sized window shifted down was detected as fullscreen.");
            AssertTrue(
                DesktopGeometry.IsFullscreenOnMonitor(
                    new Rectangle(-1930, 1190, 1940, 1100), monitor),
                "A window covering the complete monitor was not detected as fullscreen.");
            AssertFalse(
                DesktopGeometry.IsFullscreenOnMonitor(Rectangle.Empty, monitor),
                "An empty window was detected as fullscreen.");

            // ChooseRelocationTarget: move a pet off a blocked monitor to the nearest free one.
            var mons = new System.Collections.Generic.List<Rectangle>
            {
                new Rectangle(0, 0, 1920, 1080),
                new Rectangle(1920, 0, 1920, 1080),
                new Rectangle(5000, 0, 1920, 1080),
            };
            AssertEqual(-1,
                DesktopGeometry.ChooseRelocationTarget(0, mons, new[] { false, true, true }),
                "Relocation fired even though the current monitor was clear.");
            AssertEqual(1,
                DesktopGeometry.ChooseRelocationTarget(0, mons, new[] { true, false, false }),
                "Blocked monitor 0 did not relocate to the nearer free monitor 1.");
            AssertEqual(1,
                DesktopGeometry.ChooseRelocationTarget(2, mons, new[] { false, false, true }),
                "Blocked monitor 2 did not pick the nearest free monitor by center distance.");
            AssertEqual(-1,
                DesktopGeometry.ChooseRelocationTarget(0, mons, new[] { true, true, true }),
                "Relocation returned a target when every monitor was blocked.");
            AssertEqual(-1,
                DesktopGeometry.ChooseRelocationTarget(0, mons, new[] { true, false }),
                "Mismatched monitor/blocked lengths were not rejected.");
            AssertEqual(-1,
                DesktopGeometry.ChooseRelocationTarget(5, mons, new[] { true, true, true }),
                "Out-of-range current index was not rejected.");
        }

        private static string NewDirectory(params string[] parts)
        {
            string path = _testRoot;
            foreach (string part in parts)
                path = Path.Combine(path, part);
            Directory.CreateDirectory(path);
            return path;
        }

        private static void WriteLegacyUserSettings(string path, string name, string value)
        {
            File.WriteAllText(
                path,
                "<configuration><userSettings><DesktopAICompanion.Properties.Settings>" +
                "<setting name=\"" + name + "\" serializeAs=\"String\"><value>" +
                value + "</value></setting></DesktopAICompanion.Properties.Settings></userSettings>" +
                "</configuration>",
                new UTF8Encoding(false));
        }

        private static void AssertPathEqual(string expected, string actual)
        {
            AssertTrue(
                string.Equals(
                    Path.GetFullPath(expected).TrimEnd('\\', '/'),
                    Path.GetFullPath(actual).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase),
                "Expected path '" + expected + "', got '" + actual + "'.");
        }

        private static void AssertBytesEqual(byte[] expected, byte[] actual, string message)
        {
            if (expected.Length != actual.Length)
                throw new InvalidOperationException(message);
            for (int index = 0; index < expected.Length; index++)
            {
                if (expected[index] != actual[index])
                    throw new InvalidOperationException(message);
            }
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void AssertFalse(bool condition, string message)
        {
            AssertTrue(!condition, message);
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(
                    message + " Expected '" + expected + "', got '" + actual + "'.");
            }
        }

        private static void AssertThrows<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }
            throw new InvalidOperationException(message);
        }
    }
}
