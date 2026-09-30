using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DesktopAICompanion.Ai;
using DesktopAICompanion.ModuleKit;   // AtomicFile / CrossSessionLock / UnicodeTextProgress
using DesktopAICompanion.Modules;     // IModuleSettings, for the diagnostics wiring check

namespace DesktopAICompanion.FortunesModule
{
    /// <summary>
    /// Self-test hook (NOT part of the plugin ABI) for --fortunes-engine-selftest. Proves the relocated
    /// fortune engine works inside the module's own load context: a deterministic filter/pick over injected
    /// entries, the engine's own comprehensive <see cref="FortuneProvider.FilterSelfTest"/> (dedup /
    /// classifier-parity / parser / custom ingestion / importer), and the SMART layer (Embedder loading
    /// native ONNX + SmartFortunes warming/picking over the injected pool). Invoked reflectively by the host
    /// so the base needs no reference to the module engine.
    /// </summary>
    public static class FortuneEngineProbe
    {
        /// <summary>
        /// Every line the built-in corpus can produce, for the host's module self-test: with the corpus
        /// embedded, a seeded throwaway pack is a rounding error in the pool, so "did this trigger speak a
        /// fortune?" has to be asked against the whole fortune universe rather than the test's own two packs.
        /// </summary>
        public static string[] EmbeddedTexts()
        {
            List<FortuneEntry> all = FortuneProvider.EmbeddedEntriesForDiagnostics();
            var texts = new List<string>(all.Count);
            foreach (FortuneEntry e in all) texts.Add(e.Text ?? "");
            return texts.ToArray();
        }

        /// <summary>
        /// The writable-folder cache check, across the ALC boundary. It has to run INSIDE the module
        /// because it exercises `FortuneProvider`'s static cache against the live `CustomDir`, and
        /// the caller must already have pointed the engine at throwaway storage -- which
        /// `--fortunes-selftest` does and `--fortunes-engine-selftest` does not, so only the former
        /// calls this.
        /// </summary>
        public static bool CustomCacheSelfTest(out string detail)
        {
            return FortuneProvider.CustomCacheSelfTest(out detail);
        }

        public static bool Run(out string detail)
        {
            var sb = new StringBuilder();
            bool ok = true;
            try
            {
                var entries = new List<FortuneEntry>
                {
                    new FortuneEntry { Source = "probe", Topic = "life", Genre = "quip", Level = "general", Prof = false, Text = "A calm general line.",  Custom = false },
                    new FortuneEntry { Source = "probe", Topic = "life", Genre = "quip", Level = "general", Prof = false, Text = "Another general line.", Custom = false },
                    new FortuneEntry { Source = "probe", Topic = "life", Genre = "dark", Level = "edgy",    Prof = false, Text = "An edgy line.",        Custom = false },
                };

                // The default level is Clean => general-only, so the edgy entry is filtered out.
                var tame = new FortuneProvider(entries, new FortuneSettings());
                ok &= Check(sb, "tame pool keeps only general entries (edgy excluded)", tame.Count == 2);
                ok &= Check(sb, "tame Pick returns a non-empty line", !string.IsNullOrEmpty(tame.Pick()));

                // "Clean + edgy" pulls in the edgy entry alongside general.
                var spicy = new FortuneProvider(entries, new FortuneSettings { ContentLevel = ContentLevels.CleanEdgy });
                ok &= Check(sb, "clean+edgy includes the edgy entry", spicy.Count == 3);

                // Shuffle-bag draw: the random path hands out a fresh permutation, so with N distinct lines
                // every N-pick window is a full sweep (no line recurs until all N are shown), and the seam
                // between one bag and the next never repeats a line. Guards the fix for the reported
                // "thousands of jokes but only the same handful repeat".
                var bagEntries = new List<FortuneEntry>();
                for (int b = 0; b < 6; b++)
                    bagEntries.Add(new FortuneEntry {
                        Source = "bag", Topic = "life", Genre = "quip", Level = "general",
                        Prof = false, Text = "bag line " + b, Custom = false });
                var bag = new FortuneProvider(bagEntries, new FortuneSettings());
                var firstSweep = new HashSet<string>(StringComparer.Ordinal);
                var secondSweep = new HashSet<string>(StringComparer.Ordinal);
                string previous = null;
                bool seamDistinct = true;
                for (int draw = 0; draw < 12; draw++)
                {
                    string line = bag.Pick();
                    (draw < 6 ? firstSweep : secondSweep).Add(line);
                    if (draw == 6 && line == previous) seamDistinct = false;   // first of bag 2 vs last of bag 1
                    previous = line;
                }
                ok &= Check(sb, "shuffle-bag sweeps the whole pool before repeating (bag 1)", firstSweep.Count == 6);
                ok &= Check(sb, "shuffle-bag reshuffles into a full second sweep (bag 2)", secondSweep.Count == 6);
                ok &= Check(sb, "shuffle-bag boundary does not repeat the previous line", seamDistinct);

                // Content-level migration: a settings file written before the four tone controls were
                // collapsed must land on the level that preserves the user's evident intent. Getting this
                // wrong silently changes what the pet is allowed to say, in either direction.
                ok &= Check(sb, "migration: spicy off -> clean",
                    FortunesModule.MigrateContentLevel("", false, false) == ContentLevels.Clean);
                ok &= Check(sb, "migration: spicy on -> everything (old 'edgy' tier meant general+edgy+nsfw)",
                    FortunesModule.MigrateContentLevel("", true, false) == ContentLevels.Everything);
                ok &= Check(sb, "migration: spicy on + skip-tame -> spicy only",
                    FortunesModule.MigrateContentLevel("", true, true) == ContentLevels.SpicyOnly);
                ok &= Check(sb, "migration: skip-tame is ignored when spicy was off (never widens)",
                    FortunesModule.MigrateContentLevel("", false, true) == ContentLevels.Clean);
                ok &= Check(sb, "migration: an already-migrated value wins over the legacy keys",
                    FortunesModule.MigrateContentLevel(ContentLevels.CleanEdgy, true, true) == ContentLevels.CleanEdgy);
                ok &= Check(sb, "migration: an unrecognized stored value falls back to the legacy reading",
                    FortunesModule.MigrateContentLevel("bogus", true, false) == ContentLevels.Everything);

                // Pack/genre ticks are staged and folded in at Apply (ListCard.DeferChanges), so this fold
                // decides which packs the engine reads. Dropping or double-adding an id here would quietly
                // change what the pet is allowed to say, so assert it directly rather than by clicking.
                var off = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { { "b", true } };
                ok &= Check(sb, "merge: disabling an id adds it and keeps the untouched ones",
                    FortunesModule.MergeDisabled("a", off) == "a\nb");
                var on = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { { "a", false } };
                ok &= Check(sb, "merge: re-enabling an id removes it",
                    FortunesModule.MergeDisabled("a\nb", on) == "b");
                var both = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { { "a", false }, { "c", true } };
                ok &= Check(sb, "merge: a mixed batch applies in one pass",
                    FortunesModule.MergeDisabled("a\nb", both) == "b\nc");
                var already = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { { "A", true } };
                ok &= Check(sb, "merge: re-disabling an already-disabled id does not duplicate it (case-insensitive)",
                    FortunesModule.MergeDisabled("a", already) == "A");
                ok &= Check(sb, "merge: an empty batch leaves the stored list alone",
                    FortunesModule.MergeDisabled("a\nb", new Dictionary<string, bool>()) == "a\nb");

                // The built-in corpus must actually be embedded in this build. It silently was not for
                // months: the base csproj dropped the resource with a comment saying it had moved to this
                // module, the module never picked it up, and EmbeddedCorpus() failed into _embeddedError,
                // which nothing reads. A lean install had nothing to say and no gate noticed.
                List<FortuneEntry> embedded = FortuneProvider.EmbeddedEntriesForDiagnostics();
                ok &= Check(sb, "the built-in fortune corpus is embedded in the module (" + embedded.Count + " entries)",
                    embedded.Count > 3000);
                var embeddedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FortuneEntry e in embedded) embeddedSources.Add(e.Source ?? "");
                // The corpus is deliberately just these two. It used to carry 26 sources, six of which
                // existed nowhere else; those were exported to pack files so the box could ship the two
                // that belong in it. Classic Fortunes still has no pack, so it remains the source that
                // vanishes without a trace if the embed is dropped again.
                bool orphansPresent = true;
                foreach (string s in new[] { "fortunes", "dadjokes" })
                    if (!embeddedSources.Contains(s)) { orphansPresent = false; sb.AppendLine("    missing corpus source: " + s); }
                ok &= Check(sb, "both built-in corpus sources are present", orphansPresent);

                // Scraped packs arrived HTML-escaped, so the bubble literally showed "me &amp; Dave". The
                // Reddit-sourced lines are double-escaped (&amp;#x200B; -- a zero-width space escaped twice),
                // which one decode pass leaves half-undone, hence two bounded passes.
                ok &= Check(sb, "an escaped ampersand is decoded",
                    FortuneProvider.DecodeScrapedText("me &amp; Dave were drunk") == "me & Dave were drunk");
                ok &= Check(sb, "a double-escaped zero-width space is fully removed",
                    FortuneProvider.DecodeScrapedText("probable caws. &amp;#x200B;") == "probable caws.");
                ok &= Check(sb, "angle brackets and quotes decode too",
                    FortuneProvider.DecodeScrapedText("&lt;b&gt; and &quot;x&quot;") == "<b> and \"x\"");
                // The bound is the point: a fortune ABOUT typing an entity must survive intact rather than
                // being unescaped until it means something else.
                ok &= Check(sb, "decoding is bounded, so an entity that is the joke survives",
                    FortuneProvider.DecodeScrapedText("type &amp;amp;amp; to get an ampersand")
                        == "type &amp; to get an ampersand");
                ok &= Check(sb, "text with no entities is returned unchanged",
                    FortuneProvider.DecodeScrapedText("nothing to decode here") == "nothing to decode here");
                ok &= Check(sb, "null and empty are handled",
                    FortuneProvider.DecodeScrapedText(null) == null &&
                    FortuneProvider.DecodeScrapedText("") == "");

                // ---- pack files that are not what they look like (F130, F132) ----
                // Driven through TryValidateCustomPackBytes, the validator the folder loader and the
                // importer share, so one fixture speaks for both admission paths. The rows are the shape of
                // every shipped pack: tagged, UNDECLARED (no #!desktop-pet-fortunes-v2 line).
                const string rowA = "dadjokes\tfamily\tjoke\tgeneral\t0\tWhy did the scarecrow win an award? He was outstanding in his field.";
                const string rowB = "dadjokes\tfamily\tjoke\tgeneral\t0\tI used to hate facial hair, but then it grew on me.";
                int rows;
                string why;
                ok &= Check(sb, "WITNESS an undeclared tagged pack with valid rows is admitted as tagged (two rows, two entries)",
                    ValidatePack(rowA + "\n" + rowB, out rows, out why) && rows == 2);
                // The F130 fault: the strict parse fails on the blank line, and the file used to fall back to
                // prose, where each row became a valid fortune beginning "dadjokes family joke general 0 ".
                ok &= Check(sb, "an undeclared tagged pack with one blank line is refused, not demoted to prose that recites its metadata",
                    !ValidatePack(rowA + "\n\n" + rowB, out rows, out why) &&
                    why.IndexOf("malformed", StringComparison.Ordinal) >= 0);
                ok &= Check(sb, "an undeclared tagged pack with one dropped tab is refused too",
                    !ValidatePack(rowA + "\n" + rowB.Replace("\tjoke\t", " joke\t"), out rows, out why));
                // The damaged row FIRST: a heuristic that read only the first line demoted this one.
                ok &= Check(sb, "an undeclared tagged pack whose FIRST row is the damaged one is refused as well",
                    !ValidatePack(rowA.Replace("\tjoke\t", " joke\t") + "\n" + rowB, out rows, out why));
                ok &= Check(sb, "WITNESS prose whose columns merely look like metadata is still admitted as prose",
                    ValidatePack("Ordinary advice\ttech\tquip\tkeeps\tthese\twords understandable.", out rows, out why) && rows == 1);
                // The F132 fault: the text column was validated BEFORE HTML decoding, so a column that
                // decodes to nothing, or to a control character, entered the pool.
                ok &= Check(sb, "WITNESS an escaped ampersand in a tagged row is admitted, decoded",
                    ValidatePack(rowA + "\nprobe\tlife\tquip\tgeneral\t0\tme &amp; Dave were drunk at the party", out rows, out why) && rows == 2);
                ok &= Check(sb, "a tagged row whose text is only escaped zero-width spaces is refused after decoding (line 2 named), instead of entering the pool as an empty fortune",
                    !ValidatePack(rowA + "\nprobe\tlife\tquip\tgeneral\t0\t&amp;#x200B; &amp;#x200B; &amp;#x200B;", out rows, out why) &&
                    why.IndexOf("line 2", StringComparison.Ordinal) >= 0);
                ok &= Check(sb, "a tagged row that decodes to a control character is refused",
                    !ValidatePack(rowA + "\nprobe\tlife\tquip\tgeneral\t0\tA tab&#9;hides inside this fortune text.", out rows, out why));

                // ---- one bad file in the folder (F129) ----
                string folder = Path.Combine(Path.GetTempPath(),
                    "DesktopAICompanion-fortune-folder-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(folder);
                    var utf8 = new UTF8Encoding(false);
                    // A lone high surrogate in the file NAME. NTFS accepts it, EnumerateFiles returns it,
                    // it sorts before "b-", and String.Normalize on that stem throws inside the classifier;
                    // the loop's single try then dropped every pack after it.
                    File.WriteAllText(Path.Combine(folder, "a\uD800.txt"),
                        "A plain fortune whose file name is broken.", utf8);
                    File.WriteAllText(Path.Combine(folder, "b-valid.txt"),
                        "A plain fortune that must still load.", utf8);
                    FortuneProvider.CustomLoadSkips skips;
                    List<FortuneEntry> loaded = FortuneProvider.LoadCustomDirectoryForDiagnostics(folder, out skips);
                    ok &= Check(sb, "a pack whose file name holds an unpaired surrogate does not take the packs after it down with it",
                        loaded.Count == 1 && loaded[0].Source == "b-valid");
                    ok &= Check(sb, "...and is counted as a refused name, not as an error",
                        skips.BadName == 1 && skips.Error == 0 && skips.Total == 1);

                    // Junk before the packs must not spend the file-slot cap: a folder capped at ONE pack,
                    // holding an unreadable file and a valid one, loads the valid one.
                    string capped = Path.Combine(folder, "capped");
                    Directory.CreateDirectory(capped);
                    File.WriteAllBytes(Path.Combine(capped, "a-junk.txt"),
                        new byte[] { 0x41, 0xFF, 0x42, 0x20, 0x43, 0x44, 0x45, 0x46, 0x47 });
                    File.WriteAllText(Path.Combine(capped, "b-valid.txt"),
                        "A plain fortune that must still load.", utf8);
                    List<FortuneEntry> underCap = FortuneProvider.LoadCustomDirectoryForDiagnostics(
                        capped, 1, 4096, 4096, 100, out skips);
                    ok &= Check(sb, "an unreadable file sorted first does not consume the only pack slot",
                        underCap.Count == 1 && skips.Unreadable == 1);
                    ok &= Check(sb, "the skip line carries categories and counts, never a name",
                        FortuneProvider.DescribeSkips(skips) ==
                        "pack files skipped: malformed=0 unreadable=1 oversized=0 bad-name=0 over-budget=0 error=0");

                    // The importer's half of F129: a file the loader refuses holds no slot and no bytes in
                    // the admission either. Two existing files, one junk (200 bytes of invalid UTF-8) and
                    // one valid, a cap of two files and 150 bytes: the junk used to make the folder "full".
                    string junkDest = Path.Combine(folder, "junk-dest");
                    Directory.CreateDirectory(junkDest);
                    var junk = new byte[200];
                    for (int i = 0; i < junk.Length; i++) junk[i] = 0xFF;
                    File.WriteAllBytes(Path.Combine(junkDest, "junk.txt"), junk);
                    File.WriteAllText(Path.Combine(junkDest, "valid.txt"),
                        "A valid fortune line, long enough to count.\n", utf8);
                    string newSource = Path.Combine(folder, "new-source.txt");
                    File.WriteAllText(newSource, "Another valid fortune line, long enough to count.\n", utf8);
                    FortuneImportBatchResult admitted = FortuneFileImporter.ImportForDiagnostics(
                        new[] { newSource }, junkDest, 2, 4096, 150, 100);
                    ok &= Check(sb, "a file the loader refuses holds no slot and no bytes in the importer's admission",
                        admitted.ImportedCount == 1 && admitted.RejectedCount == 0);

                    // N-tools-02: a transient lock on the destination while the commit replaces it (a
                    // scanner holding the file the import just wrote) is retried, not surfaced. The stand-in
                    // for File.Replace refuses twice with a sharing violation and then does the real thing.
                    string lockedDest = Path.Combine(folder, "locked-dest");
                    Directory.CreateDirectory(lockedDest);
                    File.WriteAllText(Path.Combine(lockedDest, "replace.txt"),
                        "The original line, long enough to count.\n", utf8);
                    string replacementDir = Path.Combine(folder, "replacement-src");
                    Directory.CreateDirectory(replacementDir);
                    string replacementSource = Path.Combine(replacementDir, "replace.txt");
                    File.WriteAllText(replacementSource, "The replacement line, long enough to count.\n", utf8);
                    var approved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "replace.txt" };
                    int replaceCalls = 0;
                    FortuneImportBatchResult retried = FortuneFileImporter.ImportForDiagnostics(
                        new[] { replacementSource }, lockedDest, approved,
                        delegate(string temporaryPath, string destinationPath, string backupPath, bool ignoreMetadataErrors)
                        {
                            if (++replaceCalls <= 2)
                                throw new System.ComponentModel.Win32Exception(32, "fault-injected sharing violation");
                            File.Replace(temporaryPath, destinationPath, backupPath, ignoreMetadataErrors);
                        });
                    ok &= Check(sb, "a transient sharing violation during the commit is retried, not surfaced (the batch lands on the third attempt)",
                        retried.ImportedCount == 1 && replaceCalls == 3 &&
                        File.ReadAllText(Path.Combine(lockedDest, "replace.txt"), utf8)
                            .StartsWith("The replacement line", StringComparison.Ordinal));
                    // ...while a permanent fault is surfaced at once and rolled back, as before.
                    File.WriteAllText(replacementSource, "A second replacement line, long enough.\n", utf8);
                    int permanentCalls = 0;
                    FortuneImportBatchResult refused = FortuneFileImporter.ImportForDiagnostics(
                        new[] { replacementSource }, lockedDest, approved,
                        delegate(string temporaryPath, string destinationPath, string backupPath, bool ignoreMetadataErrors)
                        {
                            permanentCalls++;
                            throw new UnauthorizedAccessException("fault-injected permanent refusal");
                        });
                    ok &= Check(sb, "WITNESS a permanent refusal is surfaced after one attempt and the destination is left as it was",
                        refused.ImportedCount == 0 && permanentCalls == 1 &&
                        File.ReadAllText(Path.Combine(lockedDest, "replace.txt"), utf8)
                            .StartsWith("The replacement line", StringComparison.Ordinal));
                }
                finally
                {
                    try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { }
                }
                ok &= Check(sb, "no refused packs, no note on the pane",
                    FortunesModule.SkippedPacksNote(0) == "");
                ok &= Check(sb, "refused packs are counted on the pane and point at the log",
                    FortunesModule.SkippedPacksNote(2).IndexOf("2 pack files", StringComparison.Ordinal) >= 0 &&
                    FortunesModule.SkippedPacksNote(2).IndexOf("log", StringComparison.Ordinal) >= 0);

                // A host with NO settings store (F121). The convention runner's host is one; its Init used to
                // start an embed of the whole corpus into the TEMP fallback root on every run.
                ok &= Check(sb, "a host with no settings store gets smart picks OFF (nothing could persist turning them off)",
                    !FortunesModule.SettingsFromStore(null).SmartFortunes);
                ok &= Check(sb, "WITNESS an empty settings store keeps the default: smart picks on",
                    FortunesModule.SettingsFromStore(new DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings()).SmartFortunes);

                // A COLLAPSED pool must announce itself. This is the bug behind "the same dad joke five times
                // today": 157 of 190 sources were switched off, leaving exactly one pack of 2,794 lines, and
                // the pane reported "2,794 fortunes from 1 pack" with a tick. True, and useless.
                ok &= Check(sb, "a single enabled source warns rather than ticking",
                    FortunesModule.PoolStatusFor(2794, 1, 190).StartsWith("⚠", StringComparison.Ordinal));
                ok &= Check(sb, "the warning names how many sources are off",
                    FortunesModule.PoolStatusFor(2794, 1, 190).Contains("189 of 190"));
                ok &= Check(sb, "a heavily filtered pool warns even with several sources left",
                    FortunesModule.PoolStatusFor(5000, 10, 190).StartsWith("⚠", StringComparison.Ordinal));
                // ...and a healthy pool must NOT nag, or the warning becomes wallpaper.
                ok &= Check(sb, "a healthy pool still ticks",
                    FortunesModule.PoolStatusFor(20000, 150, 190).StartsWith("✓", StringComparison.Ordinal));
                ok &= Check(sb, "exactly at the quarter threshold is healthy",
                    FortunesModule.PoolStatusFor(20000, 48, 190).StartsWith("✓", StringComparison.Ordinal));
                ok &= Check(sb, "one source out of one is not a collapse",
                    FortunesModule.PoolStatusFor(500, 1, 1).StartsWith("✓", StringComparison.Ordinal));
                ok &= Check(sb, "an unknown source count does not invent a warning",
                    FortunesModule.PoolStatusFor(500, 0, 0).StartsWith("✓", StringComparison.Ordinal));

                // Smart-index status. Warm() runs in the background and leaves ready=false / total=0 until
                // its first batch publishes, so a status read from the index's own counters told everyone
                // "No fortunes yet" every time they pressed Rebuild, however full the pool was.
                const SmartStandDownReason up = SmartStandDownReason.None;
                string building = FortunesModule.SmartStatusFor(true, 12345, true, up, null, false, false, 0, 0);
                // Formatted the way the MODULE formats it rather than pinned to "12,345". FortunesModule.Count
                // uses "N0" with CurrentCulture, so on a de-DE or tr-TR machine the real string is "12.345"
                // and an Ordinal match on the comma failed the whole gate for a reason that has nothing to do
                // with the code under test. Still falsifiable: it fails if the status omits the count, prints
                // the wrong number, or regresses to "No fortunes".
                string buildingCount = 12345.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
                ok &= Check(sb, "a just-started warm reports indexing, not an empty pool",
                    building.IndexOf(buildingCount, StringComparison.Ordinal) >= 0 &&
                    building.IndexOf("No fortunes", StringComparison.Ordinal) < 0);
                // The F148 state: smart ON, no picker object yet (the build is in flight). The status is
                // derived from the setting, so this reads as indexing rather than "off".
                ok &= Check(sb, "smart picks on with the picker still being built reports indexing, not off",
                    building.IndexOf("Indexing", StringComparison.Ordinal) >= 0 &&
                    building.IndexOf("off", StringComparison.Ordinal) < 0);
                ok &= Check(sb, "a finished index reports what it indexed",
                    FortunesModule.SmartStatusFor(true, 900, true, up, null, true, true, 900, 900)
                        .IndexOf("ready", StringComparison.Ordinal) >= 0);
                ok &= Check(sb, "a partly-warm index says it is usable now",
                    FortunesModule.SmartStatusFor(true, 900, true, up, null, true, false, 100, 900)
                        .IndexOf("usable now", StringComparison.Ordinal) >= 0);
                ok &= Check(sb, "smart picks off is reported as off, not as an empty pool",
                    FortunesModule.SmartStatusFor(false, 0, false, up, null, false, false, 0, 0)
                        .IndexOf("off", StringComparison.Ordinal) >= 0);
                // A STAND-DOWN IS NOT PROGRESS. When the embedder never becomes ready, ready and complete
                // are both false, so before this branch existed the status fell through to "Indexing N
                // fortunes in the background" and stayed there for ever -- on a machine where nothing was
                // being indexed and nothing ever would be. Asserted against BOTH halves: it must say what
                // is wrong, and it must not claim work is happening. And it names the asset (F118).
                string down = FortunesModule.SmartStatusFor(true, 900, true,
                    SmartStandDownReason.EmbedderNotReady, "model: OnnxRuntimeException", false, false, 0, 900);
                ok &= Check(sb, "a stood-down smart index says so instead of claiming to be indexing",
                    down.IndexOf("unavailable", StringComparison.Ordinal) >= 0 &&
                    down.IndexOf("Indexing", StringComparison.Ordinal) < 0);
                ok &= Check(sb, "...and names the asset that failed",
                    down.IndexOf("model: OnnxRuntimeException", StringComparison.Ordinal) >= 0);
                // ONE SENTENCE PER REASON (F137): each names the action that fixes it, and none claims
                // work is happening.
                string tooLarge = FortunesModule.SmartStatusFor(true, 100001, true,
                    SmartStandDownReason.PoolTooLarge, null, false, false, 0, 0);
                ok &= Check(sb, "an oversized pool is reported as such, with the cap and the way out",
                    tooLarge.IndexOf("more than the smart index can hold", StringComparison.Ordinal) >= 0 &&
                    tooLarge.IndexOf("Disable some packs", StringComparison.Ordinal) >= 0 &&
                    tooLarge.IndexOf("Indexing", StringComparison.Ordinal) < 0);
                string absent = FortunesModule.SmartStatusFor(true, 900, true,
                    SmartStandDownReason.ModelAbsent, null, false, false, 0, 0);
                ok &= Check(sb, "a missing model asset is reported as such, with the reinstall",
                    absent.IndexOf("missing", StringComparison.Ordinal) >= 0 &&
                    absent.IndexOf("Reinstall", StringComparison.Ordinal) >= 0 &&
                    absent.IndexOf("Indexing", StringComparison.Ordinal) < 0);
                ok &= Check(sb, "a picker whose construction threw is reported as unavailable, not as indexing",
                    FortunesModule.SmartStatusFor(true, 900, true, SmartStandDownReason.ConstructionFailed, null, false, false, 0, 0)
                        .IndexOf("unavailable", StringComparison.Ordinal) >= 0);
                // The line logged at publish time says what is true THEN (F145).
                string constructed = FortunesModule.DescribeSmartBuild(3214);
                ok &= Check(sb, "the publish-time log line says constructed and warming, never ready or indexed",
                    constructed.IndexOf("constructed", StringComparison.Ordinal) >= 0 &&
                    constructed.IndexOf("warming", StringComparison.Ordinal) >= 0 &&
                    constructed.IndexOf("3214", StringComparison.Ordinal) >= 0 &&
                    constructed.IndexOf("ready", StringComparison.Ordinal) < 0 &&
                    constructed.IndexOf("indexed", StringComparison.Ordinal) < 0);

                // An empty pool with packs installed is a filter problem; "add a pack" would send a user
                // with 129 of them entirely the wrong way.
                ok &= Check(sb, "empty pool + packs installed blames the filters",
                    FortunesModule.EmptyPoolReason(true).IndexOf("filters", StringComparison.Ordinal) >= 0);
                ok &= Check(sb, "empty pool + nothing installed asks for a pack",
                    FortunesModule.EmptyPoolReason(false).IndexOf("add a pack", StringComparison.Ordinal) >= 0);

                // The fingerprint behind "already built, nothing to rebuild".
                var poolA = new List<FortuneEntry>(entries);
                ok &= Check(sb, "signature: the same pool fingerprints the same",
                    FortunesModule.PoolSignature(poolA) == FortunesModule.PoolSignature(new List<FortuneEntry>(entries)));
                var poolB = new List<FortuneEntry>(entries);
                poolB.RemoveAt(poolB.Count - 1);
                ok &= Check(sb, "signature: dropping an entry changes it",
                    FortunesModule.PoolSignature(poolA) != FortunesModule.PoolSignature(poolB));
                var poolC = new List<FortuneEntry>(entries);
                poolC[0] = new FortuneEntry { Source = "probe", Topic = "life", Genre = "quip", Level = "general", Text = "A different line." };
                ok &= Check(sb, "signature: swapping a line of the same count changes it",
                    FortunesModule.PoolSignature(poolA) != FortunesModule.PoolSignature(poolC));
                // Same texts, one topic different: the route bonus reads the topic, so this is a different
                // index and an Apply that produced it must rebuild (F147).
                var poolD = new List<FortuneEntry>(entries);
                poolD[0] = new FortuneEntry { Source = "probe", Topic = "tech", Genre = "quip", Level = "general", Text = poolD[0].Text };
                ok &= Check(sb, "signature: the same texts under a different topic fingerprint differently",
                    FortunesModule.PoolSignature(poolA) != FortunesModule.PoolSignature(poolD));

                // Diagnostics: the module reached IHost.Log at all, and the lines say the bad outcome.
                ok &= DiagnosticsAreWired(sb);

                // The pane actions that change the folder, against the module itself (no ONNX work).
                ok &= FolderActionChecks(sb);

                // The engine's full self-test suite, running in the module's context.
                bool filter = FortuneProvider.FilterSelfTest();
                ok &= Check(sb, "engine FilterSelfTest (dedup/classifier/parser/ingestion/importer/embedded-taxonomy)", filter);

                // --- smart layer: proves ONNX loads + runs inside the module's own load context ---
                ok &= Check(sb, "bge-small model present beside the module", Embedder.ModelPresent);
                if (Embedder.ModelPresent)
                {
                    // Embedder.SelfTest loads the ONNX model + embeds hardcoded strings and checks
                    // cos(code,code) > cos(code,weather) - the definitive proof that native onnxruntime.dll
                    // resolved and ran in the module's AssemblyLoadContext.
                    ok &= Check(sb, "Embedder loads ONNX + embeds in the module ALC", Embedder.SelfTest());

                    // SmartFortunes warm/pick over the injected pool exercises the rebinds: VectorCache
                    // (AtomicFile) + CrossSessionLock, all in-module. In its OWN scratch cache directory:
                    // the parameterless ctor writes FortunePaths.VectorCacheDir, which is the module's live
                    // vector cache, and under the two hosts that hand this module no settings or an empty
                    // store the module's own Init was warming the whole corpus into that same file at the
                    // same moment, each Save pruning it to its own pool (F121).
                    ok &= SmartLayerChecks(sb, entries);
                    ok &= SmartLifecycleChecks(sb);

                    // SmartFortunes' own suite over a 128-line sample of the built-in corpus: contextual
                    // picks land, and a STABLE context still rotates through 12+ distinct lines out of 40
                    // (the reported bug it guards was ~3 distinct lines out of thousands). It has had no
                    // caller since the engine moved into this module, so that regression went unwatched.
                    ok &= Check(sb, "SmartFortunes.SelfTest (contextual picks + pick variety)", SmartFortunes.SelfTest());
                    AppendReport(sb, "dp-smart-selftest.txt");
                }
                else
                {
                    sb.AppendLine("    (bge-small model absent - smart checks skipped)");
                }
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            detail = sb.ToString();
            return ok;
        }

        /// <summary>
        /// --fortunes-smart-progress-selftest: the deliberately slow half of the smart suite. Warms a
        /// 1,500-line sample against a COLD cache so real embedding happens, then proves Pick serves the
        /// warmed prefix before the pool finishes (indexed climbs monotonically, a ready-but-incomplete
        /// window is observed, and a pick lands inside it). ~18s, so it gets its own flag rather than padding
        /// every local gate run; CI runs it. This was the base's --smart-progress-selftest, which lost its
        /// caller when the engine moved into this module.
        /// </summary>
        public static bool RunProgressive(out string detail)
        {
            var sb = new StringBuilder();
            bool ok;
            try { ok = SmartFortunes.ProgressiveSelfTest(); }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            Check(sb, "SmartFortunes.ProgressiveSelfTest (cold-cache progressive warm)", ok);
            AppendReport(sb, "dp-smart-progress-selftest.txt");
            detail = sb.ToString();
            return ok;
        }

        /// <summary>
        /// The module reaches <c>IHost.Log</c>, and the lines it writes can say the bad outcome.
        ///
        /// <para>The load-bearing half is the first assertion. This module logged NOTHING until
        /// <c>RebuildEngine</c> started reporting the pool it had just built, and a diagnostic line that is
        /// never emitted is indistinguishable from the module having no diagnostics at all — so what has to
        /// be pinned is that a real <c>Init</c> against a real host produces the line. Neutralize the
        /// module's <c>Log</c> helper and this is the assertion that fails.</para>
        ///
        /// <para>It lives here rather than in <c>--fortunes-selftest</c> because the host side of that one
        /// invokes named module members by reflection, and adding a member to its list is a host edit; this
        /// probe is the module's own test surface and the gate runs it on every pass.</para>
        ///
        /// <para>NO STORAGE is registered on the recording host on purpose: <c>GetStorage</c> then returns
        /// null, <c>FortunePaths.SetRoot</c> is never called, and the engine's static root is left exactly
        /// as the rest of this probe expects to find it.</para>
        /// </summary>
        private static bool DiagnosticsAreWired(StringBuilder sb)
        {
            bool ok = true;
            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            // Smart picks OFF, so Init does no ONNX work and the line is deterministic. The smart layer
            // gets its own coverage further down this probe.
            IModuleSettings settings = host.GetSettings("fortunes");
            settings.Set("smartFortunes", "false");
            settings.Save();

            var module = new FortunesModule();
            try
            {
                module.Init(host);

                // Exactly one, not "at least one". Init builds the engine once, so a second line here means
                // something new started logging on a path that runs at startup for every user.
                ok &= Check(sb, "Init produces exactly one diagnostic line", host.LoggedLines.Count == 1);
                string line = host.LoggedLines.Count > 0 ? host.LoggedLines[0] : "";
                // RecordingHost records "<moduleId>: <message>", and the id is what the per-module log mute
                // keys on, so a line tagged with the wrong one cannot be muted or found.
                ok &= Check(sb, "the line is tagged with this module's id",
                    line.StartsWith("fortunes: ", StringComparison.Ordinal));
                ok &= Check(sb, "Init reports the pool it built (engine line reached IHost.Log)",
                    line.IndexOf("engine: fortunes=", StringComparison.Ordinal) >= 0 &&
                    line.IndexOf(" packs=", StringComparison.Ordinal) >= 0 &&
                    line.IndexOf(" smart=off", StringComparison.Ordinal) >= 0);
                sb.AppendLine("    " + line);
            }
            catch (Exception ex)
            {
                ok &= Check(sb, "Init ran against a recording host (" + ex.GetType().Name + ": " + ex.Message + ")", false);
            }
            finally
            {
                try { module.Shutdown(); } catch { }
            }

            // The engine line has to be able to report the BAD outcome, or it is only evidence of success.
            // fortunes=0 is the silent companion, and packs=1 off=157 is the collapsed pool behind "the
            // same joke keeps coming back" -- both are the same line, not a separate happy path.
            string silent = FortunesModule.DescribeEngine(0, 0, 3, ContentLevels.Clean, false, false);
            ok &= Check(sb, "the engine line can report an empty pool",
                silent.IndexOf("fortunes=0", StringComparison.Ordinal) >= 0 &&
                silent.IndexOf("off=3", StringComparison.Ordinal) >= 0);
            string collapsed = FortunesModule.DescribeEngine(2794, 1, 157, ContentLevels.Clean, true, true);
            ok &= Check(sb, "the engine line can report a collapsed pool",
                collapsed.IndexOf("fortunes=2794", StringComparison.Ordinal) >= 0 &&
                collapsed.IndexOf("packs=1", StringComparison.Ordinal) >= 0 &&
                collapsed.IndexOf("off=157", StringComparison.Ordinal) >= 0);
            // Smart picks ON with the model asset missing is the silent downgrade: every pick falls back to
            // random while the pane reports "indexing in the background" indefinitely.
            string degraded = FortunesModule.DescribeEngine(900, 4, 0, ContentLevels.Everything, true, false);
            ok &= Check(sb, "the engine line names a missing smart-picker model",
                degraded.IndexOf("smart=on model=ABSENT", StringComparison.Ordinal) >= 0);
            ok &= Check(sb, "...and says so only when smart picks are on",
                FortunesModule.DescribeEngine(900, 4, 0, ContentLevels.Everything, false, false)
                    .IndexOf("model=", StringComparison.Ordinal) < 0);

            // The download line's whole reason for existing: two of the three ways a pack fails produce no
            // reason anywhere else, so they must be told apart here.
            ok &= Check(sb, "a clean download batch says so",
                FortunesModule.DescribeDownload(4, 4, 0, 0, 0, "").IndexOf("failed=0", StringComparison.Ordinal) >= 0);
            string mixed = FortunesModule.DescribeDownload(4, 1, 1, 1, 1, "io");
            ok &= Check(sb, "a failed download batch separates the three causes",
                mixed.IndexOf("installed=1", StringComparison.Ordinal) >= 0 &&
                mixed.IndexOf("failed=3", StringComparison.Ordinal) >= 0 &&
                mixed.IndexOf("rejected-id=1", StringComparison.Ordinal) >= 0 &&
                mixed.IndexOf("empty-payload=1", StringComparison.Ordinal) >= 0 &&
                mixed.IndexOf("error=1", StringComparison.Ordinal) >= 0 &&
                mixed.IndexOf("last=io", StringComparison.Ordinal) >= 0);
            // A rejected id or an empty payload throws nothing, so there is no category to report -- and an
            // empty "last=" would read as one having been lost.
            ok &= Check(sb, "no category is invented when nothing threw",
                FortunesModule.DescribeDownload(2, 0, 1, 1, 0, "")
                    .IndexOf("last=", StringComparison.Ordinal) < 0);
            return ok;
        }

        /// <summary>Fold a sub-test's own report file into this probe's output, so the console shows why it
        /// failed instead of just that it did.</summary>
        private static void AppendReport(StringBuilder sb, string fileName)
        {
            try
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName);
                if (!System.IO.File.Exists(path)) return;
                foreach (string line in System.IO.File.ReadAllText(path).Replace("\r", "").Split('\n'))
                    if (line.Length > 0) sb.AppendLine("      " + line);
            }
            catch { }
        }

        /// <summary>
        /// The smart layer, in a scratch directory, with the engine's static sink collected so the lines the
        /// warm writes can be asserted as well as the state it leaves. Everything here needs the model to be
        /// present except the two stand-downs that exist precisely because it may not be.
        /// </summary>
        private static bool SmartLayerChecks(StringBuilder sb, List<FortuneEntry> entries)
        {
            bool ok = true;
            string scratch = Path.Combine(Path.GetTempPath(),
                "DesktopAICompanion-fortune-smart-probe-" + Guid.NewGuid().ToString("N"));
            var lines = new List<string>();
            Action<string> previousSink = SmartFortunes.LogSink;
            SmartFortunes.LogSink = delegate(string line) { lock (lines) lines.Add(line); };
            try
            {
                Directory.CreateDirectory(scratch);
                using (var sm = new SmartFortunes(Path.Combine(scratch, "vectors")))
                {
                    sm.Warm(entries);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool ready = false, complete = false; int idx = 0, total = 0;
                    bool completeLineEarly = false;
                    while (!complete && sw.ElapsedMilliseconds < 60000)
                    {
                        sm.WarmProgress(out ready, out complete, out idx, out total);
                        if (!complete)
                        {
                            // A completion line while WarmProgress still says incomplete would be the old
                            // "ready" line under a new name.
                            if (CountLines(lines, "smart index complete") > 0) completeLineEarly = true;
                            System.Threading.Thread.Sleep(50);
                        }
                    }
                    ok &= Check(sb, "SmartFortunes warms the injected pool in-module (VectorCache/lock rebinds)",
                        sm.Ready && sm.PoolCount == entries.Count);
                    // The warm's completion line, from the warm itself, exactly once, only once complete.
                    // The module's line at publish time says the picker was constructed; nothing said the
                    // warm had FINISHED until 1.0.12 (F145).
                    ok &= Check(sb, "the warm reports its completion through the sink, with the count, once it is complete and not before",
                        complete && !completeLineEarly &&
                        CountLines(lines, "smart index complete: " + entries.Count + " of " + entries.Count + " lines indexed") == 1);
                    // The cache's raw copy of every vector was kept for the picker's lifetime after the
                    // final save (F135); the picker works from its own centred copy.
                    ok &= Check(sb, "the vector cache releases its raw copy of the vectors once the warm has saved them",
                        complete && sm.CacheEntriesForDiagnostics == 0);
                    ok &= Check(sb, "WITNESS the released vectors were saved to disk first",
                        File.Exists(Path.Combine(scratch, "vectors", "cache.bin")));
                    string pick = sm.Pick("Visual Studio Code editing a C# file", "devenv");
                    sb.AppendLine("    smart pick -> " + (pick ?? "(random fallback)"));
                }

                // A warm cancelled before it starts (a pre-cancelled token stands in for the next Apply
                // superseding it) says so; it used to end in an empty catch (F145).
                using (var cancelled = new SmartFortunes(Path.Combine(scratch, "cancelled")))
                {
                    var cts = new System.Threading.CancellationTokenSource();
                    cts.Cancel();
                    cancelled.Warm(entries, cts.Token);
                    var settle = System.Diagnostics.Stopwatch.StartNew();
                    while (!cancelled.WarmTaskCompletedForDiagnostics && settle.ElapsedMilliseconds < 10000)
                        System.Threading.Thread.Sleep(10);
                    ok &= Check(sb, "a cancelled warm says so through the sink",
                        CountLines(lines, "smart index warm cancelled") >= 1);
                    cts.Dispose();
                }

                // The two early exits in Warm that never set the stand-down flag (F137). Neither needs the
                // model: the oversized pool is refused before Available is consulted, and the absent asset
                // is simulated for this instance alone.
                var huge = new List<FortuneEntry>(VectorCache.MaximumEntries + 1);
                for (int i = 0; i <= VectorCache.MaximumEntries; i++)
                    huge.Add(new FortuneEntry { Source = "huge", Topic = "life", Genre = "quip", Level = "general", Text = "line " + i });
                using (var big = new SmartFortunes(Path.Combine(scratch, "big")))
                {
                    big.Warm(huge);
                    ok &= Check(sb, "a pool above the vector-cache cap stands the index down with its reason (the flag used to stay unset)",
                        big.StoodDown && big.StandDownReason == SmartStandDownReason.PoolTooLarge);
                }
                using (var absent = new SmartFortunes(Path.Combine(scratch, "absent"), true))
                {
                    absent.Warm(entries);
                    ok &= Check(sb, "a missing model asset stands the index down with its reason",
                        absent.StoodDown && absent.StandDownReason == SmartStandDownReason.ModelAbsent);
                    // Cleared at the start of the next warm, so a rebuild after a reinstall reports progress.
                    absent.Warm(new List<FortuneEntry>());
                    ok &= Check(sb, "the next warm clears a previous stand-down", !absent.StoodDown);
                }

                // Which asset failed (F118). Embedder.EnsureLoaded threw the vocabulary parser's message
                // away and swallowed the session exception, so every failure read "not ready".
                string assets = Path.Combine(scratch, "assets");
                Directory.CreateDirectory(assets);
                string badVocab = Path.Combine(assets, "vocab.txt");
                File.WriteAllText(badVocab, "[UNK]\n[CLS]\nhello\n", new UTF8Encoding(false));   // no [SEP]
                using (var e = new Embedder(Embedder.ModelPath, badVocab))
                {
                    string failure = e.IsReady ? null : e.LoadFailure;
                    ok &= Check(sb, "a broken vocabulary is named as the failing asset, without a path",
                        !e.IsReady && failure != null &&
                        failure.StartsWith("vocabulary: ", StringComparison.Ordinal) &&
                        failure.IndexOf("special token", StringComparison.Ordinal) >= 0 && NoPathIn(failure));
                }
                string badModel = Path.Combine(assets, "model.onnx");
                File.WriteAllBytes(badModel, new byte[] { 1, 2, 3 });
                using (var e = new Embedder(badModel, Embedder.VocabPath))
                {
                    string failure = e.IsReady ? null : e.LoadFailure;
                    ok &= Check(sb, "a broken model file is named as the failing asset, by exception type, without a path",
                        !e.IsReady && failure != null &&
                        failure.StartsWith("model: ", StringComparison.Ordinal) && NoPathIn(failure));
                }
                // ...and the stand-down carries it, so the log and the pane can say which asset to look at.
                using (var down = new SmartFortunes(Path.Combine(scratch, "down"), new Embedder(badModel, Embedder.VocabPath)))
                {
                    down.Warm(entries);
                    var settle = System.Diagnostics.Stopwatch.StartNew();
                    while (!down.StoodDown && settle.ElapsedMilliseconds < 10000)
                        System.Threading.Thread.Sleep(10);
                    ok &= Check(sb, "an embedder that cannot start stands the index down naming the asset (state and log line)",
                        down.StoodDown && down.StandDownReason == SmartStandDownReason.EmbedderNotReady &&
                        down.StandDownDetail != null && down.StandDownDetail.StartsWith("model: ", StringComparison.Ordinal) &&
                        CountLines(lines, "smart index stood down: the embedder is present but not ready (model: ") == 1);
                }
                using (var good = new Embedder())
                {
                    ok &= Check(sb, "WITNESS the shipped assets load with no failure recorded",
                        good.IsReady && good.LoadFailure == null);
                }

                // The vector cache's own file handling (F138, F142, N-gates-01), on a tiny cache.
                string fingerprint = new string('1', 64);
                string soloDir = Path.Combine(scratch, "solo");
                var solo = new VectorCache(soloDir, fingerprint, 8);
                solo.AddForDiagnostics("alpha", 0.25f);
                bool firstSaved = solo.Save(System.Threading.CancellationToken.None);
                int readsAfterFirst = solo.MergeReadsForDiagnostics;
                solo.AddForDiagnostics("bravo", 0.5f);
                bool secondSaved = solo.Save(System.Threading.CancellationToken.None);
                // A checkpoint used to re-parse the whole file it had itself just written, every time.
                ok &= Check(sb, "a save over a file this cache itself last wrote does not re-parse it",
                    firstSaved && secondSaved && solo.MergeReadsForDiagnostics == readsAfterFirst);
                var other = new VectorCache(soloDir, fingerprint, 8);
                other.AddForDiagnostics("charlie", 0.75f);
                other.Save();
                solo.AddForDiagnostics("delta", 1.0f);
                bool thirdSaved = solo.Save(System.Threading.CancellationToken.None);
                ok &= Check(sb, "WITNESS another writer's save is still merged (the file changed underneath)",
                    thirdSaved && solo.MergeReadsForDiagnostics == readsAfterFirst + 1 &&
                    new VectorCache(soloDir, fingerprint, 8).CountForDiagnostics == 4);

                // The on-disk encoding is what BinaryWriter.Write(float) always wrote: little-endian
                // IEEE754, 384 per entry, right after the length-prefixed key. Bulk I/O must not have
                // changed a byte of it (F142).
                string encodingDir = Path.Combine(scratch, "encoding");
                var encoded = new VectorCache(encodingDir, fingerprint, 8);
                encoded.AddForDiagnostics("k", 0.25f);
                encoded.Save();
                byte[] file = File.ReadAllBytes(Path.Combine(encodingDir, "cache.bin"));
                byte[] quarter = { 0x00, 0x00, 0x80, 0x3E };   // 0.25f, little-endian
                const int vectorsAt = 12 + 32 + 2;             // header, fingerprint, 1-byte length + "k"
                bool encodingOk = file.Length == vectorsAt + VectorCache.ExpectedDimension * 4;
                for (int i = 0; encodingOk && i < VectorCache.ExpectedDimension * 4; i++)
                    if (file[vectorsAt + i] != quarter[i % 4]) encodingOk = false;
                ok &= Check(sb, "the on-disk float encoding is unchanged: little-endian IEEE754, one vector straight after its key",
                    encodingOk);
                ok &= Check(sb, "WITNESS the file reloads",
                    new VectorCache(encodingDir, fingerprint, 8).ContainsForDiagnostics("k"));

                // A save that fails is reported, and the entries stay put for a retry (N-gates-01).
                string failingDir = Path.Combine(scratch, "failing");
                var seed = new VectorCache(failingDir, fingerprint, 8);
                seed.AddForDiagnostics("seed", 0.1f);
                seed.Save();   // so the replace path, the one the fault sits on, is taken
                var failing = new VectorCache(failingDir, fingerprint, 8,
                    delegate(string temporaryPath, string destinationPath, string backupPath, bool ignoreMetadataErrors)
                    {
                        throw new UnauthorizedAccessException("fault-injected replace failure");
                    });
                failing.AddForDiagnostics("more", 0.2f);
                bool failedSave = failing.Save(System.Threading.CancellationToken.None);
                ok &= Check(sb, "a failed vector-cache save is reported, not swallowed: false, the exception type, and a line through the sink",
                    !failedSave &&
                    failing.LastSaveFailureForDiagnostics == "UnauthorizedAccessException" &&
                    CountLines(lines, "vector cache save failed: UnauthorizedAccessException") == 1);
                ok &= Check(sb, "WITNESS the unsaved entries stay in memory for a retry",
                    failing.ContainsForDiagnostics("more") && failing.CountForDiagnostics == 2);
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("EXC: smart layer: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                SmartFortunes.LogSink = previousSink;
                try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
                catch (Exception ex) { ok = false; sb.AppendLine("FAIL: the smart-layer scratch directory could not be removed -- " + ex.GetType().Name); }
            }
            return ok;
        }

        /// <summary>
        /// The module's own smart-picker lifecycle against a recording host with smart picks ON: the status
        /// the button shows while a build is in flight, what an Apply keeps and what it rebuilds, where a
        /// superseded picker is disposed, and what the log says at publish time. Needs the model, because
        /// Init starts a real build; each build's warm over the embedded corpus is cancelled by the next
        /// rebuild and the last by Shutdown, so it costs session loads, not an embed.
        /// </summary>
        private static bool SmartLifecycleChecks(StringBuilder sb)
        {
            bool ok = true;
            string previousRoot = FortunePaths.RootForDiagnostics;
            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            var module = new FortunesModule();
            using (var storage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("fortunes-lifecycle"))
            {
                host.UseStorage("fortunes", storage);
                DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings settings = host.SettingsFor("fortunes");
                settings.Set("smartFortunes", "true");
                try
                {
                    module.Init(host);
                    int testThread = Environment.CurrentManagedThreadId;

                    // F148: the build is in flight or just published; the status must never say "off".
                    string status = module.SmartStatusTextForDiagnostics();
                    ok &= Check(sb, "with smart picks ON and a build in flight, the button's status says indexing (or ready), never off",
                        status.IndexOf("off", StringComparison.Ordinal) < 0 &&
                        (status.IndexOf("Indexing", StringComparison.Ordinal) >= 0 ||
                         status.IndexOf("Smart index", StringComparison.Ordinal) >= 0));

                    // The first picker publishes once constructed (its warm is not awaited here).
                    SmartFortunes first = null;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while ((first = module.SmartPickerForDiagnostics) == null && sw.ElapsedMilliseconds < 20000)
                        System.Threading.Thread.Sleep(20);
                    ok &= Check(sb, "the picker is published once constructed", first != null);
                    ok &= Check(sb, "publishing logs that the picker was constructed and is warming, and never that it is ready",
                        host.LoggedLines.Exists(delegate(string l) { return l.IndexOf("smart picker constructed, warming", StringComparison.Ordinal) >= 0; }) &&
                        !host.LoggedLines.Exists(delegate(string l) { return l.IndexOf("smart picker ready", StringComparison.Ordinal) >= 0; }));

                    OptionsPane pane = host.OptionsPanes.Count > 0 ? host.OptionsPanes[0] : null;
                    ok &= Check(sb, "the module contributed its options pane", pane != null && pane.Load != null && pane.Save != null);
                    if (pane != null && pane.Load != null && pane.Save != null)
                    {
                        var values = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (KeyValuePair<string, string> kv in pane.Load()) values[kv.Key] = kv.Value;
                        int generation = module.SmartGenerationForDiagnostics;
                        int rebuilds = module.EngineRebuildsForDiagnostics;

                        // F147: an Apply that changes nothing about the pool keeps the picker.
                        bool savedSame = pane.Save(values);
                        ok &= Check(sb, "an Apply that changes nothing about the pool keeps the smart picker instead of rebuilding it",
                            savedSame && module.EngineRebuildsForDiagnostics == rebuilds + 1 &&
                            module.SmartGenerationForDiagnostics == generation &&
                            ReferenceEquals(module.SmartPickerForDiagnostics, first));

                        // F147: an Apply whose Save failed rebuilds nothing at all.
                        settings.FailSaves = true;
                        values["contentLevel"] = "Everything (incl. NSFW)";
                        bool savedFailed = pane.Save(values);
                        settings.FailSaves = false;
                        ok &= Check(sb, "an Apply whose Save failed rebuilds nothing (the persisted settings did not change)",
                            !savedFailed && module.EngineRebuildsForDiagnostics == rebuilds + 1 &&
                            module.SmartGenerationForDiagnostics == generation);

                        // F143, F144: a changed pool supersedes the picker, under the lock, and the old
                        // one is disposed on a pool thread, never the thread that applied.
                        bool savedChanged = pane.Save(values);
                        ok &= Check(sb, "an Apply that widens the pool supersedes the picker (a new generation)",
                            savedChanged && module.SmartGenerationForDiagnostics == generation + 1);
                        sw.Restart();
                        while (first != null && first.DisposeThreadForDiagnostics == 0 && sw.ElapsedMilliseconds < 20000)
                            System.Threading.Thread.Sleep(20);
                        ok &= Check(sb, "the superseded picker is disposed on a pool thread, not the thread that applied",
                            first != null && first.DisposeThreadForDiagnostics != 0 &&
                            first.DisposeThreadForDiagnostics != testThread);

                        // F148 through the button itself: pressed while a build is in flight it must not
                        // answer "off" -- which is what it did on every press that actually rebuilt.
                        PaneAction rebuild = null;
                        if (pane.Actions != null)
                            foreach (PaneAction a in pane.Actions)
                                if (a != null && string.Equals(a.Label, "Rebuild smart index", StringComparison.Ordinal)) rebuild = a;
                        string pressed = rebuild != null && rebuild.InvokeAsync != null
                            ? (rebuild.InvokeAsync().GetAwaiter().GetResult() ?? "")
                            : "";
                        ok &= Check(sb, "'Rebuild smart index' pressed while a build is in flight answers indexing (or ready), never off",
                            rebuild != null && pressed.IndexOf("off", StringComparison.Ordinal) < 0 &&
                            (pressed.IndexOf("Indexing", StringComparison.Ordinal) >= 0 ||
                             pressed.IndexOf("Smart index", StringComparison.Ordinal) >= 0));
                        sb.AppendLine("    rebuild said: " + pressed);
                    }
                }
                catch (Exception ex)
                {
                    ok &= Check(sb, "the smart lifecycle scenario ran (" + ex.GetType().Name + ": " + ex.Message + ")", false);
                }
                finally
                {
                    try { module.Shutdown(); } catch { }
                    // Init pointed the engine's static root at the temp storage; put the previous one back
                    // so what runs after this (the convention SelfTest's own scratch root) is unaffected.
                    FortunePaths.SetRoot(previousRoot);
                }
            }
            return ok;
        }

        /// <summary>
        /// The pane actions that change the fortunes folder, driven against the module itself with smart
        /// picks OFF (no ONNX work): Rescan, Import and Download each land their change and rebuild the
        /// pool; the parse and the import run on a pool thread rather than the one that pressed the button
        /// (F122, F127); and once the folder has been loaded an import validates no existing pack again
        /// (F123). A scratch storage root, removed at the end.
        /// </summary>
        private static bool FolderActionChecks(StringBuilder sb)
        {
            bool ok = true;
            string previousRoot = FortunePaths.RootForDiagnostics;
            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            var module = new FortunesModule();
            using (var storage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("fortunes-folder"))
            {
                host.UseStorage("fortunes", storage);
                host.SettingsFor("fortunes").Set("smartFortunes", "false");
                string folder = Path.Combine(storage.DataDirectory, "fortunes");
                var utf8 = new UTF8Encoding(false);
                try
                {
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(Path.Combine(folder, "seeded.txt"),
                        "A seeded fortune line that is long enough.\n", utf8);
                    module.Init(host);
                    int testThread = Environment.CurrentManagedThreadId;
                    OptionsPane pane = host.OptionsPanes.Count > 0 ? host.OptionsPanes[0] : null;
                    PaneAction rescan = FindCardAction(pane, "Fortune packs", "Rescan folder");
                    PaneAction import = FindCardAction(pane, "Fortune packs", "Import your own…");
                    PaneAction check = FindCardAction(pane, "Available online", "Check online for packs");
                    PaneAction selectAll = FindCardAction(pane, "Available online", "Select all");
                    PaneAction download = FindCardAction(pane, "Available online", "Download selected");
                    bool actionsFound = rescan != null && import != null && check != null &&
                                        selectAll != null && download != null;
                    ok &= Check(sb, "the pane offers rescan, import and the three catalog actions", actionsFound);
                    if (actionsFound)
                    {
                        // Rescan: a pack dropped into the folder joins the pool, parsed off the calling thread.
                        File.WriteAllText(Path.Combine(folder, "dropped.txt"),
                            "A dropped fortune line that is long enough.\n", utf8);
                        string rescanned = rescan.InvokeAsync().GetAwaiter().GetResult() ?? "";
                        ok &= Check(sb, "Rescan loads a pack dropped into the folder into the live pool",
                            rescanned.StartsWith("Rescanned", StringComparison.Ordinal) &&
                            module.PoolContainsForDiagnostics("A dropped fortune line that is long enough."));
                        ok &= Check(sb, "Rescan's parse ran on a pool thread, not the one that pressed the button",
                            module.LastParseThreadForDiagnostics != 0 &&
                            module.LastParseThreadForDiagnostics != testThread);

                        // Import: through the strict importer, on a pool thread, reusing the loader's parses
                        // of the packs already in the folder.
                        string mine = Path.Combine(storage.DataDirectory, "my-own-pack.txt");
                        File.WriteAllText(mine, "A line I wrote myself, long enough to count.\n", utf8);
                        host.PickedFiles = new List<string> { mine };
                        int validatedBefore = FortuneFileImporter.ExistingPacksValidatedForDiagnostics;
                        string imported = import.InvokeAsync().GetAwaiter().GetResult() ?? "";
                        host.PickedFiles = new List<string>();
                        ok &= Check(sb, "Import lands the pack in the fortunes folder and reports it",
                            File.Exists(Path.Combine(folder, "my-own-pack.txt")) &&
                            imported.IndexOf("Imported 1 pack", StringComparison.Ordinal) >= 0);
                        ok &= Check(sb, "the imported lines join the live pool",
                            module.PoolContainsForDiagnostics("A line I wrote myself, long enough to count."));
                        ok &= Check(sb, "the import ran on a pool thread, not the one that pressed the button",
                            FortuneFileImporter.LastImportThreadForDiagnostics != 0 &&
                            FortuneFileImporter.LastImportThreadForDiagnostics != testThread);
                        ok &= Check(sb, "the import validated no existing pack again (the loader's parses were reused)",
                            FortuneFileImporter.ExistingPacksValidatedForDiagnostics == validatedBefore);

                        // Download: the catalog stand-in hands the bytes; the file lands and joins the pool.
                        host.CatalogItems[CatalogKinds.Pack] = new List<CatalogItem>
                        {
                            new CatalogItem { Id = "extrapack", Name = "Extra Pack", Bytes = 10, Count = 1 },
                        };
                        host.CatalogPayloads[CatalogKinds.Pack + "/extrapack"] =
                            utf8.GetBytes("A catalog fortune line, long enough.\n");
                        check.InvokeAsync().GetAwaiter().GetResult();
                        selectAll.InvokeAsync().GetAwaiter().GetResult();
                        string downloaded = download.InvokeAsync().GetAwaiter().GetResult() ?? "";
                        ok &= Check(sb, "Download writes the pack and its lines join the live pool",
                            File.Exists(Path.Combine(folder, "extrapack.txt")) &&
                            downloaded.IndexOf("Downloaded 1 pack", StringComparison.Ordinal) >= 0 &&
                            module.PoolContainsForDiagnostics("A catalog fortune line, long enough."));
                    }
                }
                catch (Exception ex)
                {
                    ok &= Check(sb, "the folder-action scenario ran (" + ex.GetType().Name + ": " + ex.Message + ")", false);
                }
                finally
                {
                    try { module.Shutdown(); } catch { }
                    FortunePaths.SetRoot(previousRoot);
                }
            }
            return ok;
        }

        private static PaneAction FindCardAction(OptionsPane pane, string cardTitle, string label)
        {
            if (pane == null || pane.Lists == null) return null;
            foreach (ListCard card in pane.Lists)
            {
                if (card == null || card.Actions == null ||
                    !string.Equals(card.Title, cardTitle, StringComparison.Ordinal)) continue;
                foreach (PaneAction a in card.Actions)
                    if (a != null && string.Equals(a.Label, label, StringComparison.Ordinal)) return a;
            }
            return null;
        }

        private static int CountLines(List<string> lines, string prefix)
        {
            int n = 0;
            lock (lines)
                foreach (string line in lines)
                    if (line != null && line.StartsWith(prefix, StringComparison.Ordinal)) n++;
            return n;
        }

        /// <summary>No path separator anywhere: a quoted Windows path always carries a backslash.</summary>
        private static bool NoPathIn(string text)
        {
            return text.IndexOf('\\') < 0 && text.IndexOf('/') < 0;
        }

        /// <summary>One pack's content through the shared admission validator, as the loader and the
        /// importer both see it.</summary>
        private static bool ValidatePack(string content, out int rows, out string error)
        {
            return FortuneProvider.TryValidateCustomPackBytes(
                new UTF8Encoding(false).GetBytes(content), "dadjokes",
                FortunePackLoadPolicy.MaximumEntries, out rows, out error);
        }

        private static bool Check(StringBuilder sb, string name, bool cond) { sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name); return cond; }
    }
}
