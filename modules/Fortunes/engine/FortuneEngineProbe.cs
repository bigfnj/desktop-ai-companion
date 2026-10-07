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
                //
                // SEEDED AND SWEPT (RA-095). One unseeded bag observed one seam, so a deleted seam swap was
                // caught one run in six (the odds of a reshuffled bag starting on the line the old one ended
                // with) and passed the gate most days. Sixty-four seeded bags observe sixty-four seams, and
                // one seeded bag drawn 600 times observes ninety-nine more; both are the same run every time.
                var bagEntries = new List<FortuneEntry>();
                for (int b = 0; b < 6; b++)
                    bagEntries.Add(new FortuneEntry {
                        Source = "bag", Topic = "life", Genre = "quip", Level = "general",
                        Prof = false, Text = "bag line " + b, Custom = false });
                bool firstSweepsFull = true, secondSweepsFull = true, seamDistinct = true;
                for (int seed = 0; seed < 64; seed++)
                {
                    var bag = new FortuneProvider(bagEntries, new FortuneSettings());
                    bag.SeedForDiagnostics(seed);
                    var firstSweep = new HashSet<string>(StringComparer.Ordinal);
                    var secondSweep = new HashSet<string>(StringComparer.Ordinal);
                    string previous = null;
                    for (int draw = 0; draw < 12; draw++)
                    {
                        string line = bag.Pick();
                        (draw < 6 ? firstSweep : secondSweep).Add(line);
                        if (draw == 6 && line == previous) seamDistinct = false;   // first of bag 2 vs last of bag 1
                        previous = line;
                    }
                    if (firstSweep.Count != 6) firstSweepsFull = false;
                    if (secondSweep.Count != 6) secondSweepsFull = false;
                }
                ok &= Check(sb, "shuffle-bag sweeps the whole pool before repeating (bag 1, 64 seeds)", firstSweepsFull);
                ok &= Check(sb, "shuffle-bag reshuffles into a full second sweep (bag 2, 64 seeds)", secondSweepsFull);
                ok &= Check(sb, "shuffle-bag boundary does not repeat the previous line (64 seeded seams)", seamDistinct);
                var longRun = new FortuneProvider(bagEntries, new FortuneSettings());
                longRun.SeedForDiagnostics(20260930);
                int backToBack = 0;
                string last = null;
                for (int draw = 0; draw < 600; draw++)
                {
                    string line = longRun.Pick();
                    if (line == last) backToBack++;
                    last = line;
                }
                ok &= Check(sb, "600 seeded draws from a six-line bag never repeat a line back to back (99 seams)", backToBack == 0);

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
                    // holding an unreadable file and two valid ones, loads the first valid one. The second
                    // valid one is PAST THE CAP: counted, not dropped by a silent `break` (R-034).
                    string capped = Path.Combine(folder, "capped");
                    Directory.CreateDirectory(capped);
                    File.WriteAllBytes(Path.Combine(capped, "a-junk.txt"),
                        new byte[] { 0x41, 0xFF, 0x42, 0x20, 0x43, 0x44, 0x45, 0x46, 0x47 });
                    File.WriteAllText(Path.Combine(capped, "b-valid.txt"),
                        "A plain fortune that must still load.", utf8);
                    File.WriteAllText(Path.Combine(capped, "c-valid-past-the-cap.txt"),
                        "A plain fortune past the one-file cap.", utf8);
                    List<FortuneEntry> underCap = FortuneProvider.LoadCustomDirectoryForDiagnostics(
                        capped, 1, 4096, 4096, 100, out skips);
                    ok &= Check(sb, "an unreadable file sorted first does not consume the only pack slot",
                        underCap.Count == 1 && skips.Unreadable == 1);
                    ok &= Check(sb, "a valid pack past the file cap is counted as over the cap instead of vanishing",
                        skips.OverCap == 1 && skips.DidNotFit == 1 && skips.Damaged == 1);
                    ok &= Check(sb, "the skip line carries categories and counts, never a name",
                        FortuneProvider.DescribeSkips(skips) ==
                        "pack files skipped: malformed=0 unreadable=1 oversized=0 bad-name=0 over-budget=0 over-cap=1 error=0");
                    // A ZERO-BYTE file has no rows: malformed, the parser's own word for empty content, not
                    // "oversized" (R-034). A valid file the byte budget has no room for IS oversized.
                    string empty = Path.Combine(folder, "empty");
                    Directory.CreateDirectory(empty);
                    File.WriteAllBytes(Path.Combine(empty, "a-empty.txt"), new byte[0]);
                    File.WriteAllText(Path.Combine(empty, "b-valid.txt"),
                        "A plain fortune that must still load.", utf8);
                    File.WriteAllText(Path.Combine(empty, "c-valid-but-past-the-budget.txt"),
                        "A plain fortune the byte budget has no room for.", utf8);
                    List<FortuneEntry> budgeted = FortuneProvider.LoadCustomDirectoryForDiagnostics(
                        empty, 4, 4096, 40, 100, out skips);
                    ok &= Check(sb, "an empty pack file is counted as malformed, and a valid one the byte budget cannot hold as oversized",
                        budgeted.Count == 1 && skips.Malformed == 1 && skips.Oversized == 1 && skips.Unreadable == 0);

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

                    // RA-102: transient is a Win32 CODE, not an exception type. The predicate used to retry
                    // every IOException and every Win32Exception eight times over ~700 ms, a full disk and a
                    // destination that appeared under the commit included.
                    ok &= Check(sb, "a sharing violation, a lock violation, ERROR_BUSY and a user-mapped file are transient, as Win32Exception or as IOException",
                        FortuneFileImporter.IsTransientFileFault(new System.ComponentModel.Win32Exception(32)) &&
                        FortuneFileImporter.IsTransientFileFault(new System.ComponentModel.Win32Exception(33)) &&
                        FortuneFileImporter.IsTransientFileFault(new System.ComponentModel.Win32Exception(170)) &&
                        FortuneFileImporter.IsTransientFileFault(new System.ComponentModel.Win32Exception(1224)) &&
                        FortuneFileImporter.IsTransientFileFault(new IOException("sharing violation", unchecked((int)0x80070020))));
                    ok &= Check(sb, "a full disk, a destination that already exists, an access refusal and a missing file are permanent and surface at once",
                        !FortuneFileImporter.IsTransientFileFault(new IOException("disk full", unchecked((int)0x80070070))) &&
                        !FortuneFileImporter.IsTransientFileFault(new IOException("already exists", unchecked((int)0x800700B7))) &&
                        !FortuneFileImporter.IsTransientFileFault(new IOException("a .NET IOException with no Win32 code")) &&
                        !FortuneFileImporter.IsTransientFileFault(new System.ComponentModel.Win32Exception(5)) &&
                        !FortuneFileImporter.IsTransientFileFault(new UnauthorizedAccessException("denied")) &&
                        !FortuneFileImporter.IsTransientFileFault(new FileNotFoundException("gone")));

                    // RA-104: a replace that had already moved the destination aside when it failed (the two
                    // steps of ReplaceFile, or AtomicFile's IOException fallback copying the destination into
                    // the backup before its move) used to have that backup deleted by the commit's catch: the
                    // user's pack, gone. The stand-in does what a torn ReplaceFile does, then fails.
                    File.WriteAllText(replacementSource, "A torn replacement line, long enough.\n", utf8);
                    string beforeTorn = File.ReadAllText(Path.Combine(lockedDest, "replace.txt"), utf8);
                    FortuneImportBatchResult torn = FortuneFileImporter.ImportForDiagnostics(
                        new[] { replacementSource }, lockedDest, approved,
                        delegate(string temporaryPath, string destinationPath, string backupPath, bool ignoreMetadataErrors)
                        {
                            File.Copy(destinationPath, backupPath, true);
                            File.Delete(destinationPath);
                            throw new UnauthorizedAccessException("fault-injected torn replace");
                        });
                    ok &= Check(sb, "a replace that failed after moving the destination aside is undone from its backup: the pack is back, the batch refused, nothing left behind",
                        torn.ImportedCount == 0 &&
                        File.Exists(Path.Combine(lockedDest, "replace.txt")) &&
                        File.ReadAllText(Path.Combine(lockedDest, "replace.txt"), utf8) == beforeTorn &&
                        Directory.GetFiles(lockedDest).Length == 1);

                    // R-030, pinned: F132's decode-then-validate order admits a tagged row whose text column
                    // carries whitespace around it, TRIMMED, where 1.0.11 refused the row as untrimmed. The
                    // friendlier contract is kept (register, burn/fortunes); a change to it fails here first.
                    string padded = Path.Combine(folder, "padded");
                    Directory.CreateDirectory(padded);
                    File.WriteAllText(Path.Combine(padded, "padded.txt"),
                        rowA + "\nprobe\tlife\tquip\tgeneral\t0\t  A padded fortune text with spaces around it.  \n", utf8);
                    List<FortuneEntry> trimmed = FortuneProvider.LoadCustomDirectoryForDiagnostics(padded, out skips);
                    ok &= Check(sb, "a tagged row whose text column has whitespace around it is admitted with the text trimmed (the 1.0.12 contract, kept)",
                        trimmed.Count == 2 && skips.Total == 0 &&
                        trimmed[1].Text == "A padded fortune text with spaces around it.");
                }
                finally
                {
                    try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { }
                }
                ok &= Check(sb, "no refused packs, no note on the pane",
                    FortunesModule.SkippedPacksNote(new FortuneProvider.CustomLoadSkips()) == "" &&
                    FortunesModule.SkippedPacksNote(null) == "");
                string damagedNote = FortunesModule.SkippedPacksNote(new FortuneProvider.CustomLoadSkips { Malformed = 2 });
                ok &= Check(sb, "refused packs are counted on the pane and point at the log",
                    damagedNote.IndexOf("2 pack files", StringComparison.Ordinal) >= 0 &&
                    damagedNote.IndexOf("log", StringComparison.Ordinal) >= 0);
                // R-034: the two kinds of news are two sentences. A budget refusal is about VALID files and
                // asks for fewer packs; it used to be reported as "malformed or unreadable".
                ok &= Check(sb, "damaged files alone are described as malformed or unreadable, never as a budget matter",
                    damagedNote.IndexOf("malformed", StringComparison.Ordinal) >= 0 &&
                    damagedNote.IndexOf("budget", StringComparison.Ordinal) < 0);
                string budgetNote = FortunesModule.SkippedPacksNote(
                    new FortuneProvider.CustomLoadSkips { Oversized = 1, Budget = 1, OverCap = 1 });
                ok &= Check(sb, "valid files the budget had no room for are described as such, with the budget and the way out, never as malformed",
                    budgetNote.IndexOf("3 valid pack files", StringComparison.Ordinal) >= 0 &&
                    budgetNote.IndexOf("did not fit", StringComparison.Ordinal) >= 0 &&
                    budgetNote.IndexOf("16 MB", StringComparison.Ordinal) >= 0 &&
                    budgetNote.IndexOf("disable or remove some packs", StringComparison.Ordinal) >= 0 &&
                    budgetNote.IndexOf("malformed", StringComparison.Ordinal) < 0);
                string mixedNote = FortunesModule.SkippedPacksNote(
                    new FortuneProvider.CustomLoadSkips { Unreadable = 1, Budget = 2 });
                ok &= Check(sb, "WITNESS a mix carries both sentences, damaged first",
                    mixedNote.IndexOf("1 pack file in the fortunes folder was skipped", StringComparison.Ordinal) >= 0 &&
                    mixedNote.IndexOf("2 valid pack files did not fit", StringComparison.Ordinal) >
                        mixedNote.IndexOf("was skipped", StringComparison.Ordinal));

                // F120 residue (RA-098): a folded sub-report's exception line is re-emitted as a FAIL verdict
                // whatever suite prefix it carries, so tests/mutate-selftest-guards.py, which grades only
                // FAIL/EXC/SKIP-prefixed lines, sees the exception behind a custom_ingestion=FAIL; the gate's
                // own printer matches EXC: anywhere on a line and always showed it to a person.
                ok &= Check(sb, "a suite-prefixed exception line in a folded sub-report is re-emitted as a FAIL verdict",
                    IsSubReportFailure("CUSTOM EXC: IOException: boom") &&
                    IsSubReportFailure("IMPORT CLEANUP EXC: still locked") &&
                    IsSubReportFailure("CACHE EXC: InvalidDataException: bad header") &&
                    IsSubReportFailure("CLEANUP EXC: in use") &&
                    IsSubReportFailure("EXC: smart layer: NullReferenceException: x"));
                ok &= Check(sb, "WITNESS a sub-report's passing and informational lines are not re-emitted as verdicts",
                    !IsSubReportFailure("custom_ingestion=PASS") &&
                    !IsSubReportFailure("filter_cases=90 failures=0") &&
                    !IsSubReportFailure("warmed=True complete=True indexed=515 of 515 in 1234ms") &&
                    !IsSubReportFailure("[Program.cs - Visual Studio - writing C# code...] -> (random)"));

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

                // The pane's "Smart index" line, state by state (1.1.0; it replaced the Rebuild button's status).
                ok &= SmartIndexLineChecks(sb);

                // "Check online for packs" says WHY the catalog check failed (N-catalog-insight-05).
                ok &= CatalogFailureChecks(sb);
                // The line logged at publish time says what is true THEN (F145).
                string constructed = FortunesModule.DescribeSmartBuild(3214, SmartStandDownReason.None);
                ok &= Check(sb, "the publish-time log line says constructed and warming, never ready or indexed",
                    constructed.IndexOf("constructed", StringComparison.Ordinal) >= 0 &&
                    constructed.IndexOf("warming", StringComparison.Ordinal) >= 0 &&
                    constructed.IndexOf("3214", StringComparison.Ordinal) >= 0 &&
                    constructed.IndexOf("ready", StringComparison.Ordinal) < 0 &&
                    constructed.IndexOf("indexed", StringComparison.Ordinal) < 0);
                // ...and when Warm stood down before it started, it does not claim a warm is running (RA-120).
                string stoodDown = FortunesModule.DescribeSmartBuild(3214, SmartStandDownReason.ModelAbsent);
                ok &= Check(sb, "the publish-time log line says the warm stood down, and why, instead of 'warming' when nothing started",
                    stoodDown.IndexOf("constructed", StringComparison.Ordinal) >= 0 &&
                    stoodDown.IndexOf("stood down", StringComparison.Ordinal) >= 0 &&
                    stoodDown.IndexOf("ModelAbsent", StringComparison.Ordinal) >= 0 &&
                    stoodDown.IndexOf("warming", StringComparison.Ordinal) < 0);

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

                // N-burn-aibrain-01: the preview's clip lands on a code-point boundary. A surrogate pair (an
                // emoji, two UTF-16 units) straddling the cut used to be severed, leaving half a character
                // before the ellipsis; the twin of AiBrain's RA-057, cut through the same ModuleKit helper.
                string emoji = char.ConvertFromUtf32(0x1F600);   // one code point, two code units
                string straddling = new string('a', 159) + emoji + " and the rest of the fortune";
                ok &= Check(sb, "a preview clip that would sever a surrogate pair backs off to the character before it",
                    FortunesModule.Ellipsize(straddling, 160) == new string('a', 159) + "…");
                ok &= Check(sb, "WITNESS a clip that lands after a whole pair keeps it",
                    FortunesModule.Ellipsize(new string('a', 158) + emoji + " tail", 160) == new string('a', 158) + emoji + "…");
                ok &= Check(sb, "WITNESS a preview within the clip is returned whole, its line breaks made spaces",
                    FortunesModule.Ellipsize("short\nfortune", 160) == "short fortune");

                // Diagnostics: the module reached IHost.Log at all, and the lines say the bad outcome.
                ok &= DiagnosticsAreWired(sb);

                // The pane actions that change the folder, against the module itself (no ONNX work).
                ok &= FolderActionChecks(sb);

                // The bulk pack/genre actions save at once (RA-121, RA-122); no ONNX work either.
                ok &= BulkSelectionChecks(sb);

                // The pane's pool status over an empty pool still names the refused pack file (RA-125).
                ok &= EmptyPoolNoteChecks(sb);

                // Smart picks ON over an empty pool schedule no build (RA-120); no model needed.
                ok &= EmptyPoolSmartChecks(sb);

                // The index maintains itself (1.1.0): every trigger, the coalescing, the no-op witness, the
                // automatic retry and Shutdown, with pickers that need no model; and the unwatchable folder.
                ok &= AutoIndexChecks(sb);
                ok &= FolderWatchChecks(sb);

                // The engine's full self-test suite, running in the module's context.
                bool filter = FortuneProvider.FilterSelfTest();
                ok &= Check(sb, "engine FilterSelfTest (dedup/classifier/parser/ingestion/importer/embedded-taxonomy)", filter);
                // Its per-case lines used to go to a temp file nothing read, so a failing sub-suite reached the
                // gate as this one verdict and nothing else (F120). Folded in the way the smart reports are.
                AppendReport(sb, "dp-filter-selftest.txt");

                // --- smart layer: proves ONNX loads + runs inside the module's own load context ---
                ok &= Check(sb, "the module payload carries bge-small beside Fortunes.dll (the smart checks below need it)", Embedder.ModelPresent);
                if (Embedder.ModelPresent)
                {
                    // Embedder.SelfTest loads the ONNX model + embeds hardcoded strings and checks
                    // cos(code,code) > cos(code,weather) - the definitive proof that native onnxruntime.dll
                    // resolved and ran in the module's AssemblyLoadContext.
                    ok &= Check(sb, "Embedder loads ONNX + embeds in the module ALC", Embedder.SelfTest());
                    AppendReport(sb, "dp-embed-selftest.txt");

                    // SmartFortunes warm/pick over the injected pool exercises the rebinds: VectorCache
                    // (AtomicFile) + CrossSessionLock, all in-module. In its OWN scratch cache directory:
                    // the parameterless ctor writes FortunePaths.VectorCacheDir, which is the module's live
                    // vector cache, and under the two hosts that hand this module no settings or an empty
                    // store the module's own Init was warming the whole corpus into that same file at the
                    // same moment, each Save pruning it to its own pool (F121).
                    ok &= SmartLayerChecks(sb, entries);
                    ok &= SmartLifecycleChecks(sb);
                    ok &= AutoIndexModelChecks(sb);

                    // SmartFortunes' own suite over DiagnosticPool, a sample of the built-in corpus (its size
                    // is the diagnostic_pool= figure in the folded report): contextual picks land, a STABLE
                    // context shows nine tenths of its relevance band before any line repeats and never the
                    // same line twice running, and a second Warm supersedes the first. (The reported bug it
                    // guards was ~3 distinct lines out of thousands.) It had had no caller since the engine
                    // moved into this module, so that regression went unwatched.
                    ok &= Check(sb, "SmartFortunes.SelfTest (contextual picks + pick variety)", SmartFortunes.SelfTest());
                    AppendReport(sb, "dp-smart-selftest.txt");
                }
                else
                {
                    // Not a third verdict: the check above has already FAILED this run. It said "skipped",
                    // which read as a state the verdict does not have (RA-096).
                    sb.AppendLine("    (bge-small model absent: the FAIL above is the verdict, and the smart checks did not run)");
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

                // RA-097: Shutdown clears the shared engine sinks only while they are still THIS instance's.
                // The probe Inits and Shuts down instances beside the live module, and an unconditional null
                // dropped the live module's sink under it, so a stand-down it reported meanwhile reached no log.
                Action<string> foreign = delegate(string ignored) { };
                SmartFortunes.LogSink = foreign;
                FortuneProvider.LogSink = foreign;
                module.Shutdown();
                ok &= Check(sb, "Shutdown leaves a sink another owner installed after this module's Init in place (it clears only its own)",
                    ReferenceEquals(SmartFortunes.LogSink, foreign) && ReferenceEquals(FortuneProvider.LogSink, foreign));
                SmartFortunes.LogSink = null;
                FortuneProvider.LogSink = null;
                var second = new FortunesModule();
                second.Init(host);
                bool ownInstalled = SmartFortunes.LogSink != null && FortuneProvider.LogSink != null;
                second.Shutdown();
                ok &= Check(sb, "WITNESS Shutdown clears the sinks it installed itself",
                    ownInstalled && SmartFortunes.LogSink == null && FortuneProvider.LogSink == null);
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

            // The download line's whole reason for existing: three of the four ways a pack fails produce no
            // reason anywhere else, so they must be told apart here.
            ok &= Check(sb, "a clean download batch says so",
                FortunesModule.DescribeDownload(4, 4, 0, 0, 0, 0, "").IndexOf("failed=0", StringComparison.Ordinal) >= 0);
            string mixed = FortunesModule.DescribeDownload(5, 1, 1, 1, 1, 1, "io");
            ok &= Check(sb, "a failed download batch separates the four causes",
                mixed.IndexOf("installed=1", StringComparison.Ordinal) >= 0 &&
                mixed.IndexOf("failed=4", StringComparison.Ordinal) >= 0 &&
                mixed.IndexOf("rejected-id=1", StringComparison.Ordinal) >= 0 &&
                mixed.IndexOf("empty-payload=1", StringComparison.Ordinal) >= 0 &&
                mixed.IndexOf("malformed=1", StringComparison.Ordinal) >= 0 &&
                mixed.IndexOf("error=1", StringComparison.Ordinal) >= 0 &&
                mixed.IndexOf("last=io", StringComparison.Ordinal) >= 0);
            // A pack the loader would refuse is refused at download and is its own cause, never an installed
            // pack (F131); the pane's status says "failed" and this line says why.
            ok &= Check(sb, "a refused pack is a download cause of its own, not an installed pack",
                FortunesModule.DescribeDownload(1, 0, 0, 0, 1, 0, "")
                    .IndexOf("installed=0 failed=1 (rejected-id=0 empty-payload=0 malformed=1 error=0)", StringComparison.Ordinal) >= 0);
            // A rejected id, an empty payload or a refused pack throws nothing, so there is no category to
            // report -- and an empty "last=" would read as one having been lost.
            ok &= Check(sb, "no category is invented when nothing threw",
                FortunesModule.DescribeDownload(3, 0, 1, 1, 1, 0, "")
                    .IndexOf("last=", StringComparison.Ordinal) < 0);
            return ok;
        }

        /// <summary>
        /// The Selection card's "Smart index" line in every state it can be in (fortunes 1.1.0), through the pure
        /// function the pane's Load calls. Counts are formatted the way the MODULE formats them ("N0", current
        /// culture) rather than pinned to "12,345": on a de-DE or tr-TR machine an Ordinal match on the comma
        /// failed the whole gate for a reason unrelated to the code. Each negative has a WITNESS beside it.
        /// </summary>
        private static bool SmartIndexLineChecks(StringBuilder sb)
        {
            bool ok = true;
            System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.CurrentCulture;
            DateTime now = new DateTime(2026, 10, 6, 15, 30, 0, DateTimeKind.Local);
            DateTime started = new DateTime(2026, 10, 6, 14, 5, 0, DateTimeKind.Local);
            DateTime built = new DateTime(2026, 10, 6, 14, 2, 0, DateTimeKind.Local);
            string count = 12345.ToString("N0", culture);
            Func<FortunesModule.SmartIndexFacts> facts = delegate
            {
                return new FortunesModule.SmartIndexFacts
                {
                    SmartWanted = true, PoolCount = 12345, AnyPacksInstalled = true, NowLocal = now,
                    StartedLocal = started, Reason = IndexChange.Packs, Replaced = true,
                };
            };

            // IN PROGRESS. The F148 state is first: smart ON, no picker yet, so ready/complete are false and the
            // pool size comes from the provider. It must read as work, never as off, never as an empty pool.
            FortunesModule.SmartIndexFacts first = facts();
            first.Reason = IndexChange.Startup;
            first.Replaced = false;
            string building = FortunesModule.SmartIndexLineFor(first);
            sb.AppendLine("    smart index, building: " + building);
            ok &= Check(sb, "a first build says it is building, with the pool's count, never off and never an empty pool",
                building.StartsWith("Building the index (" + count + " fortunes", StringComparison.Ordinal) &&
                building.IndexOf("off", StringComparison.Ordinal) < 0 &&
                building.IndexOf("No fortunes", StringComparison.Ordinal) < 0);
            ok &= Check(sb, "...says fortunes are chosen at random until the first batch is in, and when it started",
                building.IndexOf("chosen at random until the first batch is indexed", StringComparison.Ordinal) >= 0 &&
                building.IndexOf("started " + started.ToString("t", culture), StringComparison.Ordinal) >= 0);
            string afterPacks = FortunesModule.SmartIndexLineFor(facts());
            ok &= Check(sb, "a rebuild a pack change started says so",
                afterPacks.StartsWith("Rebuilding after a pack change (" + count, StringComparison.Ordinal));
            FortunesModule.SmartIndexFacts folder = facts();
            folder.Reason = IndexChange.Folder;
            ok &= Check(sb, "a rebuild the folder watcher started names the fortunes folder",
                FortunesModule.SmartIndexLineFor(folder).StartsWith("Rebuilding after a change in the fortunes folder", StringComparison.Ordinal));
            FortunesModule.SmartIndexFacts selection = facts();
            selection.Reason = IndexChange.Selection;
            ok &= Check(sb, "a rebuild an Apply started names the selection",
                FortunesModule.SmartIndexLineFor(selection).StartsWith("Rebuilding after a selection change", StringComparison.Ordinal));
            FortunesModule.SmartIndexFacts retrying = facts();
            retrying.Reason = IndexChange.Retry;
            ok &= Check(sb, "the automatic retry says it is retrying",
                FortunesModule.SmartIndexLineFor(retrying).StartsWith("Retrying the index after a failed build", StringComparison.Ordinal));
            FortunesModule.SmartIndexFacts partial = facts();
            partial.Ready = true; partial.Indexed = 1024; partial.Total = 12345;
            string partialLine = FortunesModule.SmartIndexLineFor(partial);
            ok &= Check(sb, "a partly indexed pool says how far along and that smart picks already use it",
                partialLine.IndexOf(1024.ToString("N0", culture) + " of " + count + " fortunes indexed", StringComparison.Ordinal) >= 0 &&
                partialLine.IndexOf("Smart picks already use the indexed ones", StringComparison.Ordinal) >= 0);

            // UP TO DATE: the count the warm indexed and when it finished, the date too once it is not today.
            FortunesModule.SmartIndexFacts done = facts();
            done.Ready = true; done.Complete = true; done.Indexed = 4457; done.Total = 4457; done.BuiltLocal = built;
            string upToDate = FortunesModule.SmartIndexLineFor(done);
            sb.AppendLine("    smart index, complete: " + upToDate);
            ok &= Check(sb, "a complete index reads up to date, with its count and the time it was built",
                upToDate == "✓ Up to date (" + 4457.ToString("N0", culture) + " fortunes, built " + built.ToString("t", culture) + ").");
            done.BuiltLocal = built.AddDays(-1);
            ok &= Check(sb, "an index built on an earlier day names the day as well as the time",
                FortunesModule.SmartIndexLineFor(done).IndexOf("built " + built.AddDays(-1).ToString("g", culture), StringComparison.Ordinal) >= 0);
            done.Indexed = 4450;
            ok &= Check(sb, "a complete index with lines that could not be embedded says how many of the pool it holds",
                FortunesModule.SmartIndexLineFor(done).IndexOf(4450.ToString("N0", culture) + " of " + 4457.ToString("N0", culture) + " fortunes", StringComparison.Ordinal) >= 0);

            // OFF BY THE SETTING. The first words are what three host self-tests assert after an Init with smart
            // picks seeded off; the rest is what the user needs to know.
            FortunesModule.SmartIndexFacts off = facts();
            off.SmartWanted = false;
            ok &= Check(sb, "smart picks off is reported as off with random picks, not as an empty pool or an index",
                FortunesModule.SmartIndexLineFor(off) == "Smart picks are off, so fortunes are chosen at random.");

            // STAND-DOWNS. Terminal, so each is answered before any progress (F137): one sentence per reason,
            // each starting "✗ Off", saying fortunes are random, naming its way out, never claiming work.
            var standDowns = new List<KeyValuePair<string, FortunesModule.SmartIndexFacts>>();
            FortunesModule.SmartIndexFacts tooLarge = facts();
            tooLarge.PoolCount = 100001; tooLarge.StandDown = SmartStandDownReason.PoolTooLarge;
            standDowns.Add(new KeyValuePair<string, FortunesModule.SmartIndexFacts>("pool too large", tooLarge));
            FortunesModule.SmartIndexFacts absent = facts();
            absent.StandDown = SmartStandDownReason.ModelAbsent;
            standDowns.Add(new KeyValuePair<string, FortunesModule.SmartIndexFacts>("model absent", absent));
            FortunesModule.SmartIndexFacts noEngine = facts();
            noEngine.StandDown = SmartStandDownReason.EmbedderNotReady; noEngine.StandDownDetail = "model: OnnxRuntimeException";
            standDowns.Add(new KeyValuePair<string, FortunesModule.SmartIndexFacts>("embedder not ready", noEngine));
            FortunesModule.SmartIndexFacts retryDue = facts();
            retryDue.StandDown = SmartStandDownReason.WarmFailed; retryDue.StandDownDetail = "InvalidDataException";
            retryDue.RetryDueLocal = now.AddMinutes(1);
            standDowns.Add(new KeyValuePair<string, FortunesModule.SmartIndexFacts>("failed, retry due", retryDue));
            FortunesModule.SmartIndexFacts retrySpent = facts();
            retrySpent.StandDown = SmartStandDownReason.ConstructionFailed; retrySpent.RetrySpent = true;
            standDowns.Add(new KeyValuePair<string, FortunesModule.SmartIndexFacts>("failed, retry spent", retrySpent));
            bool everyStandDownIsOff = true;
            string mentionsButton = null;
            foreach (KeyValuePair<string, FortunesModule.SmartIndexFacts> kv in standDowns)
            {
                kv.Value.Ready = false;
                string text = FortunesModule.SmartIndexLineFor(kv.Value);
                sb.AppendLine("    smart index, " + kv.Key + ": " + text);
                if (!text.StartsWith("✗ Off", StringComparison.Ordinal) ||
                    text.IndexOf("fortunes are chosen at random", StringComparison.Ordinal) < 0 ||
                    text.IndexOf("Building", StringComparison.Ordinal) >= 0 ||
                    text.IndexOf("Rebuilding", StringComparison.Ordinal) >= 0 ||
                    text.IndexOf("Up to date", StringComparison.Ordinal) >= 0)
                    everyStandDownIsOff = false;
                if (text.IndexOf("Rebuild smart index", StringComparison.Ordinal) >= 0) mentionsButton = kv.Key;
            }
            ok &= Check(sb, "every stand-down reads as off with random picks and never as building or up to date (5 reasons)",
                everyStandDownIsOff);
            ok &= Check(sb, "no stand-down sends the user to the removed 'Rebuild smart index' button" +
                (mentionsButton == null ? "" : " (" + mentionsButton + ")"), mentionsButton == null);
            string tooLargeLine = FortunesModule.SmartIndexLineFor(tooLarge);
            ok &= Check(sb, "WITNESS an oversized pool names the cap and the way out, and that it rebuilds on its own",
                tooLargeLine.IndexOf("more than the index can hold, " + 100000.ToString("N0", culture), StringComparison.Ordinal) >= 0 &&
                tooLargeLine.IndexOf("Disable some packs", StringComparison.Ordinal) >= 0 &&
                tooLargeLine.IndexOf("rebuilds on its own", StringComparison.Ordinal) >= 0);
            ok &= Check(sb, "a missing model asset is reported as such, with the reinstall",
                FortunesModule.SmartIndexLineFor(absent).IndexOf("model is missing", StringComparison.Ordinal) >= 0 &&
                FortunesModule.SmartIndexLineFor(absent).IndexOf("Reinstall", StringComparison.Ordinal) >= 0);
            ok &= Check(sb, "a text engine that cannot start names the asset that failed",
                FortunesModule.SmartIndexLineFor(noEngine).IndexOf("could not start: model: OnnxRuntimeException", StringComparison.Ordinal) >= 0);
            string dueLine = FortunesModule.SmartIndexLineFor(retryDue);
            ok &= Check(sb, "a failed build with its automatic retry pending names the cause and the time it is retried",
                dueLine.IndexOf("could not be built: InvalidDataException", StringComparison.Ordinal) >= 0 &&
                dueLine.IndexOf("Trying again at " + now.AddMinutes(1).ToString("t", culture), StringComparison.Ordinal) >= 0);
            string spentLine = FortunesModule.SmartIndexLineFor(retrySpent);
            ok &= Check(sb, "a failed build whose automatic retry failed too says so and what retries it next",
                spentLine.IndexOf("the automatic retry failed too", StringComparison.Ordinal) >= 0 &&
                spentLine.IndexOf("tried again after the next pack or selection change", StringComparison.Ordinal) >= 0 &&
                spentLine.IndexOf("Trying again at", StringComparison.Ordinal) < 0);
            retrySpent.RetrySpent = false;
            ok &= Check(sb, "WITNESS a failed build with no retry run yet does not claim one failed",
                FortunesModule.SmartIndexLineFor(retrySpent).IndexOf("failed too", StringComparison.Ordinal) < 0);

            // EMPTY POOL and NO ENGINE come before the index: there is nothing to index.
            FortunesModule.SmartIndexFacts empty = facts();
            empty.PoolCount = 0;
            ok &= Check(sb, "an empty pool with packs installed blames the filters, not the index",
                FortunesModule.SmartIndexLineFor(empty).IndexOf("No fortunes match these filters", StringComparison.Ordinal) >= 0);
            empty.AnyPacksInstalled = false;
            string noPacks = FortunesModule.SmartIndexLineFor(empty);
            ok &= Check(sb, "an empty pool with nothing installed asks for a pack and no longer for a rebuild",
                noPacks.IndexOf("add a pack", StringComparison.Ordinal) >= 0 &&
                noPacks.IndexOf("rebuild", StringComparison.OrdinalIgnoreCase) < 0);
            FortunesModule.SmartIndexFacts unloaded = facts();
            unloaded.EngineLoaded = false;
            ok &= Check(sb, "an engine that is not loaded says so before anything about the index",
                FortunesModule.SmartIndexLineFor(unloaded).StartsWith("✗ The fortune engine isn't loaded", StringComparison.Ordinal));

            // THE DEGRADED WATCH says so on every state, and only when it is degraded.
            FortunesModule.SmartIndexFacts unwatched = facts();
            unwatched.Complete = true; unwatched.Ready = true; unwatched.Indexed = 10; unwatched.Total = 10;
            unwatched.FolderWatchProblem = "UnauthorizedAccessException";
            ok &= Check(sb, "a folder that cannot be watched adds its warning and Rescan folder to the line",
                FortunesModule.SmartIndexLineFor(unwatched).IndexOf("⚠ The fortunes folder cannot be watched here", StringComparison.Ordinal) > 0 &&
                FortunesModule.SmartIndexLineFor(unwatched).IndexOf("Rescan folder", StringComparison.Ordinal) > 0);
            unwatched.FolderWatchProblem = null;
            ok &= Check(sb, "WITNESS a watched folder adds nothing",
                FortunesModule.SmartIndexLineFor(unwatched).IndexOf("⚠", StringComparison.Ordinal) < 0);

            // THE NO-OP SKIP's comparison: same settings select the same pool; any field that changes it differs.
            Func<FortuneSettings> settings = delegate
            {
                return new FortuneSettings
                {
                    ContentLevel = ContentLevels.CleanEdgy, NoProfanity = true, SmartFortunes = true,
                    DisabledSources = new List<string> { "a", "b" }, DisabledGenres = new List<string> { "pun" },
                };
            };
            ok &= Check(sb, "WITNESS two reads of the same settings select the same pool",
                FortunesModule.SameSelection(settings(), settings()));
            FortuneSettings level = settings(); level.ContentLevel = ContentLevels.Everything;
            FortuneSettings source = settings(); source.DisabledSources.Add("c");
            FortuneSettings genre = settings(); genre.DisabledGenres.Clear();
            FortuneSettings profanity = settings(); profanity.NoProfanity = false;
            FortuneSettings smart = settings(); smart.SmartFortunes = false;
            ok &= Check(sb, "a changed level, pack list, genre list, profanity filter or smart setting is a different selection",
                !FortunesModule.SameSelection(settings(), level) && !FortunesModule.SameSelection(settings(), source) &&
                !FortunesModule.SameSelection(settings(), genre) && !FortunesModule.SameSelection(settings(), profanity) &&
                !FortunesModule.SameSelection(settings(), smart) && !FortunesModule.SameSelection(settings(), null));

            // THE STATUS CARD's one line (owner, 2026-10-06, mockup F2): the pool, the index, the content, in that
            // order, in the owner's own shape; red only when the companion would be silent.
            FortunesModule.SmartIndexFacts upNow = facts();
            upNow.PoolCount = 4457; upNow.Ready = true; upNow.Complete = true; upNow.Indexed = 4457; upNow.Total = 4457;
            upNow.BuiltLocal = built;
            string statusDone = FortunesModule.StatusLineFor(upNow, 7, "Clean + edgy", false);
            sb.AppendLine("    status, up to date: " + statusDone);
            ok &= Check(sb, "the Status line reads the owner's shape: pool and sources, the index and when it was built, the content",
                statusDone == 4457.ToString("N0", culture) + " fortunes from 7 sources | smart index: up to date (built " +
                    built.ToString("t", culture) + ") | content: Clean + edgy");
            ok &= Check(sb, "the Status line says a rebuild after a pack change in the same voice, with picks random meanwhile",
                FortunesModule.StatusLineFor(facts(), 7, "Clean only", false).IndexOf(
                    "| smart index: rebuilding after a pack change, picks are random until it is ready |", StringComparison.Ordinal) > 0);
            ok &= Check(sb, "the Status line calls a first build just building",
                FortunesModule.StatusLineFor(first, 7, "Clean only", false).IndexOf("| smart index: building, picks are random", StringComparison.Ordinal) > 0);
            ok &= Check(sb, "the Status line says how far a partly indexed pool has got",
                FortunesModule.StatusLineFor(partial, 7, "Clean only", false).IndexOf(
                    "(" + 1024.ToString("N0", culture) + " of " + count + " indexed, in use)", StringComparison.Ordinal) > 0);
            string statusOff = FortunesModule.StatusLineFor(off, 7, "Everything (incl. NSFW)", true);
            ok &= Check(sb, "with smart picks off the Status line still counts the pool, says picks are random, and names the content with its filter",
                statusOff == count + " fortunes from 7 sources | smart index: off, picks are random | content: Everything (incl. NSFW), profanity removed");
            ok &= Check(sb, "the Status line names a stand-down and a pending retry in a clause each",
                FortunesModule.StatusLineFor(absent, 7, "Clean only", false).IndexOf("smart index: off, picks are random (the model is missing)", StringComparison.Ordinal) > 0 &&
                FortunesModule.StatusLineFor(retryDue, 7, "Clean only", false).IndexOf(
                    "smart index: off for now, picks are random (trying again at " + now.AddMinutes(1).ToString("t", culture) + ")", StringComparison.Ordinal) > 0);
            bool redOnlyWhenSilent = true;
            foreach (KeyValuePair<string, FortunesModule.SmartIndexFacts> kv in standDowns)
                if (FortunesModule.StatusLineFor(kv.Value, 7, "Clean only", false).StartsWith("✗", StringComparison.Ordinal)) redOnlyWhenSilent = false;
            if (statusOff.StartsWith("✗", StringComparison.Ordinal) || statusDone.StartsWith("✗", StringComparison.Ordinal)) redOnlyWhenSilent = false;
            ok &= Check(sb, "the Status line is not red while fortunes still come (smart picks off, every stand-down, up to date)", redOnlyWhenSilent);
            FortunesModule.SmartIndexFacts silent = facts();
            silent.PoolCount = 0;
            string silentLine = FortunesModule.StatusLineFor(silent, 0, "Clean only", false);
            ok &= Check(sb, "WITNESS an empty pool makes the Status line red, says the companion is silent, and that there is nothing to index",
                silentLine.StartsWith("✗ No fortunes match these filters, so the companion is silent | smart index: nothing to index", StringComparison.Ordinal));
            ok &= Check(sb, "an engine that is not loaded makes the Status line red before anything else",
                FortunesModule.StatusLineFor(unloaded, 7, "Clean only", false).StartsWith("✗ The fortune engine isn't loaded", StringComparison.Ordinal));
            unwatched.FolderWatchProblem = "UnauthorizedAccessException";
            ok &= Check(sb, "an unwatched folder ends the Status line with its warning and Rescan folder",
                FortunesModule.StatusLineFor(unwatched, 7, "Clean only", false).EndsWith(
                    " | ⚠ the fortunes folder is not watched: use Rescan folder after changing it", StringComparison.Ordinal));
            unwatched.FolderWatchProblem = null;
            ok &= Check(sb, "WITNESS a watched folder adds nothing to the Status line",
                FortunesModule.StatusLineFor(unwatched, 7, "Clean only", false).IndexOf("⚠", StringComparison.Ordinal) < 0);
            return ok;
        }

        /// <summary>
        /// "Check online for packs" words a failed catalog check by its CAUSE (N-catalog-insight-05): every failure
        /// read "✗ Couldn't reach the catalog: ...", a catalog that was reached and refused included (the owner's
        /// screenshot: "Couldn't reach the catalog: Catalog contains an invalid module entry."). One check per case,
        /// through the pure function the action's catch calls; the wiring is a source invariant, because the
        /// recording host has no failing fetch to drive. Host 1.4.0's CatalogRejectedException is told apart by its
        /// type NAME, so a probe type with that name is the faithful stand-in.
        /// </summary>
        private static bool CatalogFailureChecks(StringBuilder sb)
        {
            bool ok = true;
            const string refusedTail = ". This is a fault in the published catalog, not in your install; try “Check online for packs” again later.";
            const string unreachableTail = ". Check your connection, then press “Check online for packs” to try again.";
            string newHost = FortunesModule.CatalogFailureText(new CatalogRejectedException("Catalog contains an invalid module entry."));
            sb.AppendLine("    catalog refused: " + newHost);
            ok &= Check(sb, "a catalog host 1.4.0 refused (CatalogRejectedException) reads as reached but unreadable, the publisher's fault",
                newHost == "✗ The catalog was reached but could not be read: Catalog contains an invalid module entry" + refusedTail);
            ok &= Check(sb, "a catalog host 1.3.0 refused (InvalidDataException) reads the same, never as unreachable",
                FortunesModule.CatalogFailureText(new InvalidDataException("Catalog contains an invalid module entry.")) ==
                "✗ The catalog was reached but could not be read: Catalog contains an invalid module entry" + refusedTail);
            ok &= Check(sb, "a catalog that came back and does not parse (JsonException) reads as reached but unreadable",
                FortunesModule.CatalogFailureText(new System.Text.Json.JsonException("'<' is an invalid start of a value."))
                    .StartsWith("✗ The catalog was reached but could not be read: ", StringComparison.Ordinal));
            string noAnswer = FortunesModule.CatalogFailureText(new System.Net.Http.HttpRequestException("No such host is known."));
            sb.AppendLine("    catalog unreachable: " + noAnswer);
            ok &= Check(sb, "a catalog with no answer (HttpRequestException) reads as unreachable, with the connection to check",
                noAnswer == "✗ Couldn't reach the catalog: No such host is known" + unreachableTail);
            ok &= Check(sb, "a catalog with no answer in time (TimeoutException, TaskCanceledException, OperationCanceledException) reads as unreachable",
                FortunesModule.CatalogFailureText(new TimeoutException("The request timed out.")).StartsWith("✗ Couldn't reach the catalog: ", StringComparison.Ordinal) &&
                FortunesModule.CatalogFailureText(new System.Threading.Tasks.TaskCanceledException("The operation was canceled.")).StartsWith("✗ Couldn't reach the catalog: ", StringComparison.Ordinal) &&
                FortunesModule.CatalogFailureText(new OperationCanceledException("The operation was canceled.")).StartsWith("✗ Couldn't reach the catalog: ", StringComparison.Ordinal));
            string other = FortunesModule.CatalogFailureText(new InvalidOperationException("The host refused the catalog kind."));
            ok &= Check(sb, "WITNESS any other fault says the check failed and offers the retry, claiming neither a dead connection nor a bad catalog",
                other == "✗ The catalog check failed: The host refused the catalog kind. Press “Check online for packs” to try again.");
            string longLine = FortunesModule.CatalogFailureText(new InvalidDataException(new string('x', 500)));
            ok &= Check(sb, "a long host message is clipped to the module's bound, with its ellipsis",
                longLine.IndexOf(new string('x', 160) + "…", StringComparison.Ordinal) > 0 &&
                longLine.IndexOf(new string('x', 161), StringComparison.Ordinal) < 0);
            ok &= Check(sb, "a host message that is empty leaves no empty colon behind",
                FortunesModule.CatalogFailureText(new System.Net.Http.HttpRequestException("")) == "✗ Couldn't reach the catalog" + unreachableTail);
            return ok;
        }

        /// <summary>Named like host 1.4.0's exception for a refused catalog: the module tells it apart by NAME.</summary>
        private sealed class CatalogRejectedException : Exception
        {
            public CatalogRejectedException(string message) : base(message) { }
        }

        /// <summary>
        /// Fold a sub-test's own report file into this probe's output, so the console shows why it failed
        /// instead of just that it did. A line that reports a failure in the sub-reports' own vocabulary
        /// (<c>name=FAIL</c>, <c>SUITE FAIL case</c>, <c>EXC:</c>) is re-emitted as a <c>FAIL: </c> verdict
        /// line: the gate's failure printer and tests/mutate-selftest-guards.py read only lines that START
        /// with FAIL, so without the prefix the folded case was visible to a person and to neither tool (F120).
        /// </summary>
        private static void AppendReport(StringBuilder sb, string fileName)
        {
            try
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName);
                if (!System.IO.File.Exists(path)) return;
                foreach (string line in System.IO.File.ReadAllText(path).Replace("\r", "").Split('\n'))
                {
                    if (line.Length == 0) continue;
                    sb.AppendLine((IsSubReportFailure(line) ? "      FAIL: " : "      ") + line);
                }
            }
            catch { }
        }

        internal static bool IsSubReportFailure(string line)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("EXC", StringComparison.Ordinal)) return true;
            // A suite-prefixed exception line (CUSTOM EXC:, IMPORT CLEANUP EXC:, CACHE EXC:, CLEANUP EXC:) is
            // an exception too. Only the bare EXC prefix was matched, so those lines reached the gate's printer
            // (which matches EXC: anywhere) and not tests/mutate-selftest-guards.py, which grades only lines
            // whose stripped form starts with FAIL, EXC or SKIP (RA-098).
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^[A-Z][A-Z ]* EXC: ")) return true;
            if (trimmed.IndexOf("=FAIL", StringComparison.Ordinal) >= 0) return true;
            return System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^[A-Z][A-Z ]* FAIL( |$)");
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
            // F145's ORDER half is judged HERE, in the sink, at the instant the line arrives: a completion
            // line from the watched picker while that picker still reports incomplete is the old "ready"
            // line under a new name. The loop below used to look for it every 50 ms, which cannot see the
            // microsecond window between a line written too early and the flag that follows it, so the
            // regression it was for (the line moved before the flag) survived it (N-burn-fortunes-01).
            // Only the completion line asks the picker, and that line is written outside its lock.
            SmartFortunes completionWatch = null;
            bool completeLineEarly = false;
            Action<string> previousSink = SmartFortunes.LogSink;
            SmartFortunes.LogSink = delegate(string line)
            {
                SmartFortunes watched = completionWatch;
                bool early = false;
                if (watched != null && line != null &&
                    line.StartsWith("smart index complete", StringComparison.Ordinal))
                {
                    bool watchedReady, watchedComplete; int watchedIndexed, watchedTotal;
                    watched.WarmProgress(out watchedReady, out watchedComplete, out watchedIndexed, out watchedTotal);
                    early = !watchedComplete;
                }
                lock (lines)
                {
                    if (early) completeLineEarly = true;
                    lines.Add(line);
                }
            };
            try
            {
                Directory.CreateDirectory(scratch);
                using (var sm = new SmartFortunes(Path.Combine(scratch, "vectors")))
                {
                    completionWatch = sm;
                    sm.Warm(entries);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool ready = false, complete = false; int idx = 0, total = 0;
                    while (!complete && sw.ElapsedMilliseconds < 60000)
                    {
                        sm.WarmProgress(out ready, out complete, out idx, out total);
                        if (!complete) System.Threading.Thread.Sleep(50);
                    }
                    // The line FOLLOWS the flag by design (the flag is set under the lock, the line is
                    // written after it), so the instant WarmProgress reports complete the line can still be
                    // a scheduling quantum away: counted at that instant, this check failed one standalone
                    // run in three under a parallel build (N-burn-fortunes-01). Wait, bounded, for the warm
                    // TASK to end: every line it writes is in by then, a second one a regression might add
                    // included, so the count below stays exact. Do not move the line before the flag to
                    // "fix" this; that is the regression the sink above catches.
                    var lineSettle = System.Diagnostics.Stopwatch.StartNew();
                    while (!sm.WarmTaskCompletedForDiagnostics && lineSettle.ElapsedMilliseconds < 5000)
                        System.Threading.Thread.Sleep(10);
                    bool lineEarly;
                    lock (lines) lineEarly = completeLineEarly;
                    completionWatch = null;
                    ok &= Check(sb, "SmartFortunes warms the injected pool in-module (VectorCache/lock rebinds)",
                        sm.Ready && sm.PoolCount == entries.Count);
                    // The warm's completion line, from the warm itself, exactly once, only once complete.
                    // The module's line at publish time says the picker was constructed; nothing said the
                    // warm had FINISHED until 1.0.12 (F145).
                    ok &= Check(sb, "the warm reports its completion through the sink, with the count, once it is complete and not before",
                        complete && !lineEarly &&
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

                // A warm that THROWS (R-031). F137 named three exits; the catch-all left no reason, so the pane
                // said "Indexing ..." for ever and F147's keep pinned the dead picker. Forced through a cache
                // whose cap is below the pool: BeginActivePool refuses it inside WarmCore, the one exception the
                // warm task lets out today (an invariant breach; the embedder swallows its own).
                using (var warmFails = new SmartFortunes(Path.Combine(scratch, "warm-fails"), 2))
                {
                    warmFails.Warm(entries);
                    var settle = System.Diagnostics.Stopwatch.StartNew();
                    while (!warmFails.WarmTaskCompletedForDiagnostics && settle.ElapsedMilliseconds < 20000)
                        System.Threading.Thread.Sleep(10);
                    // The STATE is the assertion; the line is evidence it reached the log, counted tolerantly
                    // because the sink is process-global and another instance can write to it (RA-115, R-026).
                    ok &= Check(sb, "a warm that throws stands the index down with WarmFailed and the exception type (state and log line)",
                        warmFails.StoodDown && warmFails.StandDownReason == SmartStandDownReason.WarmFailed &&
                        warmFails.StandDownDetail == "InvalidDataException" &&
                        CountLines(lines, "smart index warm failed: InvalidDataException") >= 1);
                    warmFails.Warm(new List<FortuneEntry>());
                    ok &= Check(sb, "WITNESS the next warm clears a failed warm's stand-down too", !warmFails.StoodDown);
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
                // R-025: a missing or unloadable native onnxruntime arrives as a TypeInitializationException
                // wrapping DllNotFoundException or BadImageFormatException (the runtime resolves it in a static
                // constructor) and read "model: TypeInitializationException", the wrong asset named. Pure, so
                // the shipped DLL stays where it is.
                ok &= Check(sb, "a native runtime that failed to load is named as the runtime, by its root cause, not as the model",
                    Embedder.DescribeLoadFailure(new TypeInitializationException("NativeMethods", new DllNotFoundException("onnxruntime"))) == "runtime: DllNotFoundException" &&
                    Embedder.DescribeLoadFailure(new TypeInitializationException("NativeMethods", new BadImageFormatException("wrong bitness"))) == "runtime: BadImageFormatException" &&
                    Embedder.DescribeLoadFailure(new AggregateException(new EntryPointNotFoundException("OrtGetApiBase"))) == "runtime: EntryPointNotFoundException");
                ok &= Check(sb, "WITNESS anything else stays the model's, by its root type, without a path",
                    Embedder.DescribeLoadFailure(new InvalidDataException("corrupt")) == "model: InvalidDataException" &&
                    Embedder.DescribeLoadFailure(new TypeInitializationException("x", new InvalidOperationException("y"))) == "model: InvalidOperationException" &&
                    NoPathIn(Embedder.DescribeLoadFailure(new FileNotFoundException("C:\\some\\path\\model.onnx"))));

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
        /// The module's own smart-picker lifecycle against a recording host with smart picks ON: the Smart index
        /// line while a build is in flight, what an Apply keeps and what it rebuilds, where a
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

                    // F148: the build is in flight or just published; the line must never say "off". Read the
                    // way the host reads it, through the pane's Load, and through the seam the host self-tests
                    // reflect on, which must be the same text.
                    string status = module.SmartStatusTextForDiagnostics();
                    string paneLine = null;
                    OptionsPane firstPane = host.OptionsPanes.Count > 0 ? host.OptionsPanes[0] : null;
                    if (firstPane != null && firstPane.Load != null) firstPane.Load().TryGetValue("smartIndex", out paneLine);
                    sb.AppendLine("    smart index after Init: " + status);
                    ok &= Check(sb, "with smart picks ON and a build in flight, the Smart index line says building (or how far along), never off",
                        status.IndexOf("off", StringComparison.Ordinal) < 0 &&
                        (status.StartsWith("Building the index", StringComparison.Ordinal) ||
                         status.IndexOf("fortunes indexed", StringComparison.Ordinal) >= 0 ||
                         status.StartsWith("✓ Up to date", StringComparison.Ordinal)));
                    // The same SHAPE, not necessarily the same string: the warm can publish a batch between the
                    // two reads, which moves "Building" to "N of M fortunes indexed".
                    ok &= Check(sb, "the pane's Load carries the Smart index line under smartIndex, building and never off",
                        paneLine != null && paneLine.IndexOf("off", StringComparison.Ordinal) < 0 &&
                        (paneLine.StartsWith("Building the index", StringComparison.Ordinal) ||
                         paneLine.IndexOf("fortunes indexed", StringComparison.Ordinal) >= 0 ||
                         paneLine.StartsWith("✓ Up to date", StringComparison.Ordinal)));

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

                        // F148 through the pane, where the user reads it: the Apply above started a rebuild, the
                        // host rebuilds the pane straight after Apply (RefreshAfterApply), and the line it reads
                        // then must say what started the rebuild and never "off". (This was the Rebuild button's
                        // answer until 1.1.0, and it said "Smart picks are off" on every press that rebuilt.)
                        string afterApply;
                        pane.Load().TryGetValue("smartIndex", out afterApply);
                        afterApply = afterApply ?? "";
                        sb.AppendLine("    smart index after the widening Apply: " + afterApply);
                        ok &= Check(sb, "after an Apply that changed the pool, the Smart index line says it is rebuilding after a selection change, never off",
                            afterApply.IndexOf("off", StringComparison.Ordinal) < 0 &&
                            afterApply.StartsWith("Rebuilding after a selection change", StringComparison.Ordinal) &&
                            module.IndexReasonForDiagnostics == IndexChange.Selection);
                        ok &= Check(sb, "the pane offers no 'Rebuild smart index' button any more",
                            FindPaneAction(pane, "Rebuild smart index") == null);
                        ok &= Check(sb, "WITNESS the pane-wide action lookup finds an action that exists",
                            FindPaneAction(pane, "Show me 5 examples") != null);
                    }
                }
                catch (Exception ex)
                {
                    ok &= Check(sb, "the smart lifecycle scenario ran (" + ex.GetType().Name + ": " + ex.Message + ")", false);
                }
                finally
                {
                    try { module.Shutdown(); } catch { }
                    // Every build must have ENDED before the storage below is disposed and the root restored: a
                    // build superseded while constructing outlives Shutdown and creates its VectorCache under
                    // whichever root is current at that instant (R-027, RA-100). Bounded, so it cannot hang the
                    // gate, and asserted, so a build that never ends is a failure rather than a leak.
                    ok &= Check(sb, "every smart build had ended before the lifecycle storage was removed (joined within 20 s)",
                        module.JoinSmartBuildsForDiagnostics(TimeSpan.FromSeconds(20)));
                    ok &= Check(sb, "WITNESS no build is in flight after the join",
                        module.SmartBuildsInFlightForDiagnostics == 0);
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

                        // RA-101: a rejected file is NAMED in the status, so the user knows which of the files
                        // they picked to fix. The name is the user's own screen; the log carries none of it.
                        string junkSource = Path.Combine(storage.DataDirectory, "not-a-pack.txt");
                        File.WriteAllText(junkSource, "short\n", utf8);
                        host.PickedFiles = new List<string> { junkSource };
                        string rejected = import.InvokeAsync().GetAwaiter().GetResult() ?? "";
                        host.PickedFiles = new List<string>();
                        ok &= Check(sb, "a rejected import names the file it refused, by its leaf, beside the reason",
                            rejected.IndexOf("1 file rejected", StringComparison.Ordinal) >= 0 &&
                            rejected.IndexOf("(not-a-pack.txt: ", StringComparison.Ordinal) >= 0 &&
                            rejected.IndexOf(storage.DataDirectory, StringComparison.OrdinalIgnoreCase) < 0 &&
                            !File.Exists(Path.Combine(folder, "not-a-pack.txt")));

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
                        // RA-124: the content check on the downloaded bytes (a full parse, classifier
                        // included) runs on a pool thread, not the one that pressed the button.
                        ok &= Check(sb, "the download's content validation ran on a pool thread, not the one that pressed the button",
                            module.LastDownloadValidationThreadForDiagnostics != 0 &&
                            module.LastDownloadValidationThreadForDiagnostics != testThread);
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

        /// <summary>
        /// "Select all" and "Select none" on the Fortune packs and Genres cards SAVE at once and rebuild the
        /// pool, driven against the module itself with smart picks OFF. Until 1.0.12 they staged into the
        /// module's own map and waited for an Apply: the map outlived a Cancel (the host discards its deferred
        /// ticks on close and nothing tells the module), so the reopened pane showed the unsaved batch as
        /// current and the next Apply for any field committed it (RA-121); and with no field edit pending the
        /// host greyed Apply out after the action's own pane reload, so the status asked for an Apply nobody
        /// could press (RA-122). WITNESS beside it: an individual tick, which the host flushes at Apply, still
        /// waits for that Apply -- the batch still costs one write.
        /// </summary>
        private static bool BulkSelectionChecks(StringBuilder sb)
        {
            bool ok = true;
            string previousRoot = FortunePaths.RootForDiagnostics;
            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            var module = new FortunesModule();
            using (var storage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("fortunes-bulk"))
            {
                host.UseStorage("fortunes", storage);
                DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings settings = host.SettingsFor("fortunes");
                settings.Set("smartFortunes", "false");
                settings.Save();
                string folder = Path.Combine(storage.DataDirectory, "fortunes");
                const string seededLine = "A seeded fortune line for the bulk selection check.";
                try
                {
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(Path.Combine(folder, "seeded.txt"), seededLine + "\n", new UTF8Encoding(false));
                    module.Init(host);
                    OptionsPane pane = host.OptionsPanes.Count > 0 ? host.OptionsPanes[0] : null;
                    ListCard packs = FindCard(pane, "Fortune packs");
                    PaneAction packsNone = FindCardAction(pane, "Fortune packs", "Select none");
                    PaneAction packsAll = FindCardAction(pane, "Fortune packs", "Select all");
                    PaneAction genresNone = FindCardAction(pane, "Genres", "Select none");
                    PaneAction genresAll = FindCardAction(pane, "Genres", "Select all");
                    bool found = pane != null && pane.Load != null && pane.Save != null &&
                                 packs != null && packs.SetChecked != null &&
                                 packsNone != null && packsAll != null && genresNone != null && genresAll != null;
                    ok &= Check(sb, "the pane offers select all/none on both DeferChanges cards", found);
                    if (found)
                    {
                        int sources = FortuneProvider.Sources().Count;
                        int rebuilds = module.EngineRebuildsForDiagnostics;
                        int saves = settings.SaveCount;
                        string status = packsNone.InvokeAsync().GetAwaiter().GetResult() ?? "";
                        HashSet<string> disabled = IdSet(settings.Get("disabledSources", ""));
                        ok &= Check(sb, "'Select none' on the packs card saves every pack as disabled at once (one write, no Apply)",
                            settings.SaveCount == saves + 1 && sources > 0 && disabled.Count == sources && disabled.Contains("seeded"));
                        ok &= Check(sb, "...rebuilds the live pool on it, so the companion falls silent at once",
                            module.EngineRebuildsForDiagnostics == rebuilds + 1 && !module.PoolContainsForDiagnostics(seededLine));
                        ok &= Check(sb, "...and its status says what happened, never 'Apply to use it'",
                            status.StartsWith("Unticked all", StringComparison.Ordinal) &&
                            status.IndexOf("Apply", StringComparison.Ordinal) < 0);
                        // The pane the host rebuilds after the action reads the SAVED state, and so does the
                        // pool status beside it: the two used to disagree (boxes unticked, count unchanged).
                        string poolStatus;
                        pane.Load().TryGetValue("poolStatus", out poolStatus);
                        int ticked = 0;
                        foreach (ListItem li in packs.LoadItems()) if (li.Checked) ticked++;
                        ok &= Check(sb, "the reloaded pane shows every pack unticked beside a pool status that agrees (no fortunes match)",
                            ticked == 0 && poolStatus != null &&
                            poolStatus.IndexOf("No fortunes match", StringComparison.Ordinal) >= 0);

                        status = packsAll.InvokeAsync().GetAwaiter().GetResult() ?? "";
                        ok &= Check(sb, "'Select all' saves every pack back and the pool returns at once",
                            settings.Get("disabledSources", "") == "" &&
                            module.EngineRebuildsForDiagnostics == rebuilds + 2 &&
                            module.PoolContainsForDiagnostics(seededLine) &&
                            status.StartsWith("Ticked all", StringComparison.Ordinal));

                        int genres = FortuneProvider.Genres().Count;
                        genresNone.InvokeAsync().GetAwaiter().GetResult();
                        ok &= Check(sb, "'Select none' on the genres card saves every genre as disabled and empties the pool",
                            genres > 0 && IdSet(settings.Get("disabledGenres", "")).Count == genres &&
                            !module.PoolContainsForDiagnostics(seededLine));
                        genresAll.InvokeAsync().GetAwaiter().GetResult();
                        ok &= Check(sb, "'Select all' on the genres card saves them back and the pool returns",
                            settings.Get("disabledGenres", "") == "" && module.PoolContainsForDiagnostics(seededLine));

                        // WITNESS: an individual tick is the host's deferred edit. It reaches the module only
                        // at Apply (the host flushes SetChecked immediately before Save), and the module stages
                        // it into the same write as the fields, so nothing is saved until that Apply.
                        saves = settings.SaveCount;
                        rebuilds = module.EngineRebuildsForDiagnostics;
                        packs.SetChecked("seeded", false);
                        ok &= Check(sb, "WITNESS an individual tick flushed by the host is staged, not saved (the Apply's batch still costs one write)",
                            settings.SaveCount == saves && settings.Get("disabledSources", "") == "" &&
                            module.EngineRebuildsForDiagnostics == rebuilds &&
                            module.PoolContainsForDiagnostics(seededLine));
                        bool applied = pane.Save(pane.Load());
                        ok &= Check(sb, "...and Apply commits the staged tick in its one write and rebuilds once",
                            applied && settings.SaveCount == saves + 1 &&
                            IdSet(settings.Get("disabledSources", "")).Contains("seeded") &&
                            module.EngineRebuildsForDiagnostics == rebuilds + 1 &&
                            !module.PoolContainsForDiagnostics(seededLine));
                    }
                }
                catch (Exception ex)
                {
                    ok &= Check(sb, "the bulk selection scenario ran (" + ex.GetType().Name + ": " + ex.Message + ")", false);
                }
                finally
                {
                    try { module.Shutdown(); } catch { }
                    FortunePaths.SetRoot(previousRoot);
                }
            }
            return ok;
        }

        /// <summary>
        /// The pane's pool status over an EMPTY pool still carries the refused-pack note (RA-125). The empty
        /// path returned early with the filter reason alone, on the one path where a refused pack is the whole
        /// cause: a user who unticked the built-in packs to hear their own, and whose own pack the loader had
        /// just refused, was told to widen the filters. A damaged tagged pack (one blank line, the F130 shape)
        /// alone in the folder, the two built-in sources disabled; then, as the WITNESS, the same folder with
        /// the built-in sources on: a healthy pool that ticks and still names the file. Smart picks off.
        /// </summary>
        private static bool EmptyPoolNoteChecks(StringBuilder sb)
        {
            bool ok = true;
            string previousRoot = FortunePaths.RootForDiagnostics;
            const string rowA = "dadjokes\tfamily\tjoke\tgeneral\t0\tWhy did the scarecrow win an award? He was outstanding in his field.";
            const string rowB = "dadjokes\tfamily\tjoke\tgeneral\t0\tI used to hate facial hair, but then it grew on me.";
            using (var storage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("fortunes-emptypool"))
            {
                string folder = Path.Combine(storage.DataDirectory, "fortunes");
                try
                {
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(Path.Combine(folder, "damaged.txt"), rowA + "\n\n" + rowB, new UTF8Encoding(false));
                    string status = PoolStatusUnder(storage, "fortunes\ndadjokes");
                    ok &= Check(sb, "an empty pool whose only pack file was refused blames the filters AND names the skipped file",
                        status.StartsWith("✗", StringComparison.Ordinal) &&
                        status.IndexOf("No fortunes match these filters", StringComparison.Ordinal) >= 0 &&
                        status.IndexOf("1 pack file", StringComparison.Ordinal) >= 0 &&
                        status.IndexOf("skipped", StringComparison.Ordinal) >= 0);
                    string healthy = PoolStatusUnder(storage, "");
                    ok &= Check(sb, "WITNESS a healthy pool over the same folder ticks and still names the skipped file (the cached parse's count)",
                        healthy.StartsWith("✓", StringComparison.Ordinal) &&
                        healthy.IndexOf("1 pack file", StringComparison.Ordinal) >= 0);
                }
                catch (Exception ex)
                {
                    ok &= Check(sb, "the empty-pool note scenario ran (" + ex.GetType().Name + ": " + ex.Message + ")", false);
                }
                finally
                {
                    FortunePaths.SetRoot(previousRoot);
                }
            }
            return ok;
        }

        /// <summary>
        /// Smart picks ON over an EMPTY pool schedule no build (RA-120). The build used to construct a picker
        /// whose VectorCache.Load parsed and retained the whole cache.bin, call a Warm that started nothing,
        /// and log "warming 0 lines". Every source the corpus has is disabled, so the pool is empty; the
        /// WITNESS is the same host with the sources on, where a build is scheduled and constructs. No model
        /// needed: the negative constructs nothing, and the WITNESS's warm stands down or is cancelled by
        /// Shutdown. Both halves join their builds before the storage goes (R-027).
        /// </summary>
        private static bool EmptyPoolSmartChecks(StringBuilder sb)
        {
            bool ok = true;
            string previousRoot = FortunePaths.RootForDiagnostics;
            using (var storage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("fortunes-emptysmart"))
            {
                var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                host.UseStorage("fortunes", storage);
                DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings settings = host.SettingsFor("fortunes");
                settings.Set("smartFortunes", "true");
                settings.Set("disabledSources", "fortunes\ndadjokes");
                settings.Save();
                var module = new FortunesModule();
                try
                {
                    module.Init(host);
                    ok &= Check(sb, "an empty pool with smart picks ON schedules no smart build (nothing to index, nothing constructed, nothing logged as warming)",
                        !module.SmartBuildingForDiagnostics &&
                        module.SmartBuildsInFlightForDiagnostics == 0 &&
                        module.SmartPickerConstructionsForDiagnostics == 0 &&
                        module.SmartPickerForDiagnostics == null &&
                        !host.LoggedLines.Exists(delegate(string l) { return l.IndexOf("smart picker constructed", StringComparison.Ordinal) >= 0; }));
                    string status = module.SmartStatusTextForDiagnostics();
                    ok &= Check(sb, "...and the button's status answers the empty pool, not an index",
                        status.IndexOf("No fortunes match", StringComparison.Ordinal) >= 0 &&
                        status.IndexOf("Indexing", StringComparison.Ordinal) < 0);
                }
                catch (Exception ex)
                {
                    ok &= Check(sb, "the empty-pool smart scenario ran (" + ex.GetType().Name + ": " + ex.Message + ")", false);
                }
                finally
                {
                    try { module.Shutdown(); } catch { }
                    module.JoinSmartBuildsForDiagnostics(TimeSpan.FromSeconds(20));
                }

                settings.Set("disabledSources", "");
                settings.Save();
                var witness = new FortunesModule();
                try
                {
                    witness.Init(host);
                    bool scheduled = witness.SmartBuildingForDiagnostics;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (witness.SmartPickerConstructionsForDiagnostics == 0 && sw.ElapsedMilliseconds < 20000)
                        System.Threading.Thread.Sleep(20);
                    ok &= Check(sb, "WITNESS a non-empty pool with smart picks ON schedules a build that constructs a picker",
                        scheduled && witness.SmartPickerConstructionsForDiagnostics == 1);
                }
                catch (Exception ex)
                {
                    ok &= Check(sb, "the empty-pool smart WITNESS ran (" + ex.GetType().Name + ": " + ex.Message + ")", false);
                }
                finally
                {
                    try { witness.Shutdown(); } catch { }
                    ok &= Check(sb, "every smart build of the witness had ended before its storage was removed (joined within 20 s)",
                        witness.JoinSmartBuildsForDiagnostics(TimeSpan.FromSeconds(20)));
                    FortunePaths.SetRoot(previousRoot);
                }
            }
            return ok;
        }

        /// <summary>
        /// THE INDEX MAINTAINS ITSELF (fortunes 1.1.0): every staleness trigger starts exactly one index build,
        /// a burst of folder changes is one, a no-op costs nothing, a failed build is retried once on its own,
        /// and the pane's "Smart index" line follows the state. Smart picks ON over a pool of scratch packs alone
        /// (the built-in sources disabled), with the module's picker factory handing out pickers that report the
        /// model ABSENT (Warm stands down at once: no ONNX session anywhere in this method), or that throw, or
        /// that wait on a gate so a build can be held in flight. The quiet window is ten minutes, so every
        /// folder rebuild here is elapsed by hand and counted exactly, except the one that proves the real timer.
        /// No SynchronizationContext, so what the module posts runs inline on threads the test can wait for.
        /// </summary>
        private static bool AutoIndexChecks(StringBuilder sb)
        {
            bool ok = true;
            string previousRoot = FortunePaths.RootForDiagnostics;
            System.Threading.SynchronizationContext previousContext = System.Threading.SynchronizationContext.Current;
            System.Threading.SynchronizationContext.SetSynchronizationContext(null);
            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            host.SpeechEnabled = true;
            var module = new FortunesModule();
            using (var storage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("fortunes-autoindex"))
            {
                host.UseStorage("fortunes", storage);
                DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings settings = host.SettingsFor("fortunes");
                settings.Set("smartFortunes", "true");
                settings.Set("disabledSources", "fortunes\ndadjokes");   // the pool is this test's packs alone
                settings.Save();
                string folder = Path.Combine(storage.DataDirectory, "fortunes");
                string pickerCache = Path.Combine(storage.DataDirectory, "probe-pickers");
                Exception factoryFault = null;
                System.Threading.ManualResetEventSlim hold = null;
                module.PickerFactoryForDiagnostics = delegate
                {
                    System.Threading.ManualResetEventSlim gate = hold;
                    if (gate != null) gate.Wait(TimeSpan.FromSeconds(20));
                    Exception fault = factoryFault;
                    if (fault != null) throw fault;
                    return new SmartFortunes(pickerCache, true);   // model reported absent: stands down, no session
                };
                module.FolderQuietWindowForDiagnostics = TimeSpan.FromMinutes(10);
                module.RetryDelayForDiagnostics = TimeSpan.FromMinutes(10);
                // FAILS FAST. Every wait on a build ENDING goes through here, and the first that runs out marks the
                // scenario stuck: the later ones return false at once. A build that never counts itself out (the
                // R-027 mutation) made each of some thirty waits run its whole bound, so the self-test outlived the
                // mutation harness's 300 s limit and was killed as BROKEN instead of failing by name.
                bool stuck = false;
                Func<bool> settled = delegate
                {
                    if (stuck) return false;
                    bool done = WaitFor(delegate
                    {
                        return module.FolderRebuildIdleForDiagnostics && module.SmartBuildsInFlightForDiagnostics == 0;
                    }, 20000);
                    if (!done) stuck = true;
                    return done;
                };
                Func<string> line = delegate { return module.SmartStatusTextForDiagnostics(); };
                try
                {
                    Directory.CreateDirectory(folder);
                    WritePack(folder, "alpha", "Alpha fortune one, long enough to count.", "Alpha fortune two, long enough to count.");
                    module.Init(host);
                    OptionsPane pane = host.OptionsPanes.Count > 0 ? host.OptionsPanes[0] : null;
                    PaneAction rescan = FindCardAction(pane, "Fortune packs", "Rescan folder");
                    PaneAction import = FindCardAction(pane, "Fortune packs", "Import your own…");
                    PaneAction genresNone = FindCardAction(pane, "Genres", "Select none");
                    PaneAction genresAll = FindCardAction(pane, "Genres", "Select all");
                    PaneAction check = FindCardAction(pane, "Available online", "Check online for packs");
                    PaneAction selectAll = FindCardAction(pane, "Available online", "Select all");
                    PaneAction download = FindCardAction(pane, "Available online", "Download selected");
                    ListCard packs = FindCard(pane, "Fortune packs");
                    bool found = pane != null && pane.Load != null && pane.Save != null && rescan != null && import != null &&
                                 genresNone != null && genresAll != null && check != null && selectAll != null &&
                                 download != null && packs != null && packs.SetChecked != null;
                    ok &= Check(sb, "the pane offers what the trigger checks drive (rescan, import, download, genres, the packs card)", found);
                    if (!found) return ok;

                    // FIRST RUN: Init starts exactly one build, for the startup.
                    ok &= Check(sb, "first run: Init starts exactly one index build, for the startup",
                        settled() && module.IndexBuildsStartedForDiagnostics == 1 &&
                        module.IndexReasonForDiagnostics == IndexChange.Startup);
                    ok &= Check(sb, "...and the Smart index line reports the stand-down the picker made (model absent here)",
                        line().StartsWith("✗ Off, fortunes are chosen at random (the text engine's model is missing", StringComparison.Ordinal));
                    // A WATCHER, not merely no recorded problem: with no watcher started at all the problem field is null
                    // too, and the Rescans below would start one, so this is the one place that sees Init's watch.
                    ok &= Check(sb, "first run: Init watches the fortunes folder (a watcher is running, nothing recorded against it)",
                        module.FolderWatchedForDiagnostics && module.FolderWatchProblemForDiagnostics == null);

                    // THE STATUS CARD (owner, 2026-10-06): the pane's first field is one Info row in a group titled
                    // Status that pins itself full-width above every card, and its line is filled at every Load.
                    SettingField statusField = pane.Schema != null && pane.Schema.Count > 0 ? pane.Schema[0] : null;
                    ok &= Check(sb, "the pane's first field is the Status card's one Info row, pinned to the top and full width",
                        statusField != null && statusField.Id == "status" && statusField.Group == "Status" &&
                        statusField.Kind == SettingKind.Info && statusField.PinTop && statusField.FullWidth);
                    int statusRows = 0;
                    foreach (SettingField sf in pane.Schema) if (sf != null && sf.Group == "Status") statusRows++;
                    ok &= Check(sb, "WITNESS the Status card holds that one row and the Selection card still carries the Smart index line",
                        statusRows == 1 && FindField(pane, "smartIndex") != null && FindField(pane, "smartIndex").Group == "Selection");
                    string statusNow;
                    pane.Load().TryGetValue("status", out statusNow);
                    sb.AppendLine("    status after Init: " + statusNow);
                    ok &= Check(sb, "the Status line read at Load says the pool, the index's stand-down and the content level as they are",
                        statusNow == 2.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) +
                            " fortunes from 1 source | smart index: off, picks are random (the model is missing) | content: Clean only");

                    // THE NO-OP WITNESS: a Rescan over an unchanged folder and unchanged settings builds nothing at
                    // all: no index build, no picker, no provider, no file or folder parse, the same picker kept.
                    int builds = module.IndexBuildsStartedForDiagnostics;
                    int engines = module.EngineRebuildsForDiagnostics;
                    int constructions = module.SmartPickerConstructionsForDiagnostics;
                    int packParses = FortuneProvider.PackParsesForDiagnostics;
                    int folderParses = FortuneProvider.CustomParsesForDiagnostics;
                    SmartFortunes kept = module.SmartPickerForDiagnostics;
                    string noop = rescan.InvokeAsync().GetAwaiter().GetResult() ?? "";
                    settled();
                    ok &= Check(sb, "a no-op Rescan costs nothing: no index build, no picker, no provider, no pack or folder parse",
                        noop.StartsWith("Rescanned", StringComparison.Ordinal) &&
                        module.IndexBuildsStartedForDiagnostics == builds &&
                        module.SmartPickerConstructionsForDiagnostics == constructions &&
                        module.EngineRebuildsForDiagnostics == engines &&
                        FortuneProvider.PackParsesForDiagnostics == packParses &&
                        FortuneProvider.CustomParsesForDiagnostics == folderParses &&
                        kept != null && ReferenceEquals(module.SmartPickerForDiagnostics, kept));

                    // RESCAN after a pack was ADDED, EDITED, REMOVED: one build each, for a pack change.
                    WritePack(folder, "bravo", "Bravo fortune, dropped while the app runs.");
                    builds = module.IndexBuildsStartedForDiagnostics;
                    engines = module.EngineRebuildsForDiagnostics;
                    rescan.InvokeAsync().GetAwaiter().GetResult();
                    ok &= Check(sb, "WITNESS a Rescan after a pack was added starts exactly one build, for a pack change, and the pack is live",
                        settled() && module.IndexBuildsStartedForDiagnostics == builds + 1 &&
                        module.EngineRebuildsForDiagnostics == engines + 1 &&
                        module.IndexReasonForDiagnostics == IndexChange.Packs &&
                        module.PoolContainsForDiagnostics("Bravo fortune, dropped while the app runs."));
                    WritePack(folder, "bravo", "Bravo fortune, edited in place so its length changes too.");
                    builds = module.IndexBuildsStartedForDiagnostics;
                    rescan.InvokeAsync().GetAwaiter().GetResult();
                    ok &= Check(sb, "a Rescan after a pack was edited in place starts exactly one build",
                        settled() && module.IndexBuildsStartedForDiagnostics == builds + 1 &&
                        module.PoolContainsForDiagnostics("Bravo fortune, edited in place so its length changes too."));
                    File.Delete(Path.Combine(folder, "bravo.txt"));
                    builds = module.IndexBuildsStartedForDiagnostics;
                    rescan.InvokeAsync().GetAwaiter().GetResult();
                    ok &= Check(sb, "a Rescan after a pack was removed starts exactly one build, and its lines leave the pool",
                        settled() && module.IndexBuildsStartedForDiagnostics == builds + 1 &&
                        !module.PoolContainsForDiagnostics("Bravo fortune, edited in place so its length changes too."));

                    // WHILE A REBUILD RUNS, picks work as they always did without the index: a build held in
                    // flight, a drop that still speaks from the pool, and the line saying what is happening.
                    hold = new System.Threading.ManualResetEventSlim(false);
                    WritePack(folder, "charlie", "Charlie fortune, added while the index is rebuilt.");
                    rescan.InvokeAsync().GetAwaiter().GetResult();
                    string during = line();
                    int spokenBefore = host.BroadcastLines.Count + host.SaidLines.Count;
                    bool spoke = host.RaiseDrop();
                    sb.AppendLine("    smart index while a rebuild is held: " + during);
                    // alpha's two lines and charlie's one: the count comes from the provider, not the index.
                    ok &= Check(sb, "while a rebuild is in flight the Smart index line says it is rebuilding after a pack change, with the pool's count",
                        during.StartsWith("Rebuilding after a pack change (" + 3.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + " fortunes, started ", StringComparison.Ordinal));
                    ok &= Check(sb, "...and a drop still speaks a fortune from the pool, the way it does with smart picks unavailable",
                        spoke && host.BroadcastLines.Count + host.SaidLines.Count > spokenBefore);
                    string statusDuring;
                    pane.Load().TryGetValue("status", out statusDuring);
                    ok &= Check(sb, "...and the Status line read meanwhile says the rebuild and that picks are random until it is ready",
                        statusDuring != null && statusDuring.IndexOf(
                            "| smart index: rebuilding after a pack change, picks are random until it is ready |", StringComparison.Ordinal) > 0);
                    hold.Set();
                    settled();
                    hold = null;

                    // THE FOLDER WATCHER, through its real timer: a pack dropped while the app runs joins the pool
                    // with nothing pressed.
                    builds = module.IndexBuildsStartedForDiagnostics;
                    int folderRuns = module.FolderRebuildsRunForDiagnostics;
                    module.FolderQuietWindowForDiagnostics = TimeSpan.FromMilliseconds(150);
                    WritePack(folder, "delta", "Delta fortune, noticed by the folder watcher alone.");
                    bool joined = WaitFor(delegate
                    {
                        return module.PoolContainsForDiagnostics("Delta fortune, noticed by the folder watcher alone.") &&
                               module.FolderRebuildIdleForDiagnostics;
                    }, 15000) && settled();
                    module.FolderQuietWindowForDiagnostics = TimeSpan.FromMinutes(10);
                    ok &= Check(sb, "a pack dropped into the folder while the app runs joins the pool on its own, through the watcher's timer, in one build",
                        joined && module.FolderRebuildsRunForDiagnostics > folderRuns &&
                        module.IndexBuildsStartedForDiagnostics == builds + 1 &&
                        module.IndexReasonForDiagnostics == IndexChange.Folder);
                    // A notification that had already armed the short window is let run out, so nothing below races it.
                    System.Threading.Thread.Sleep(400);
                    settled();

                    // A BURST COALESCES: nine packs copied in at once are one folder rebuild and one index build.
                    builds = module.IndexBuildsStartedForDiagnostics;
                    engines = module.EngineRebuildsForDiagnostics;
                    folderRuns = module.FolderRebuildsRunForDiagnostics;
                    int seen = module.FolderEventsSeenForDiagnostics;
                    for (int i = 0; i < 9; i++)
                        WritePack(folder, "burst" + i, "Burst fortune number " + i + ", one of nine copied at once.");
                    bool notified = WaitFor(delegate { return module.FolderEventsSeenForDiagnostics >= seen + 9; }, 5000);
                    module.FolderQuietElapsedForDiagnostics();
                    settled();
                    bool allNine = true;
                    for (int i = 0; i < 9; i++)
                        if (!module.PoolContainsForDiagnostics("Burst fortune number " + i + ", one of nine copied at once.")) allNine = false;
                    sb.AppendLine("    burst: " + (module.FolderEventsSeenForDiagnostics - seen) + " notifications" + (notified ? "" : " (fewer than nine arrived in 5 s)"));
                    ok &= Check(sb, "a burst of nine packs is ONE folder rebuild and ONE index build, and all nine are live",
                        allNine && module.FolderRebuildsRunForDiagnostics == folderRuns + 1 &&
                        module.EngineRebuildsForDiagnostics == engines + 1 &&
                        module.IndexBuildsStartedForDiagnostics == builds + 1);
                    module.FolderQuietElapsedForDiagnostics();
                    settled();
                    ok &= Check(sb, "WITNESS a second quiet window over the same folder runs a folder rebuild that builds nothing",
                        module.FolderRebuildsRunForDiagnostics == folderRuns + 2 &&
                        module.EngineRebuildsForDiagnostics == engines + 1 &&
                        module.IndexBuildsStartedForDiagnostics == builds + 1);

                    // THE REAL TIMER COALESCES: nine packs written together raise many notifications and ONE elapse,
                    // because each notification restarts the window. A one-second window, so the writes (a few
                    // milliseconds) cannot straddle it even on a loaded machine.
                    builds = module.IndexBuildsStartedForDiagnostics;
                    folderRuns = module.FolderRebuildsRunForDiagnostics;
                    seen = module.FolderEventsSeenForDiagnostics;
                    module.FolderQuietWindowForDiagnostics = TimeSpan.FromSeconds(1);
                    for (int i = 0; i < 9; i++)
                        WritePack(folder, "wave" + i, "Wave fortune number " + i + ", one of nine the timer folds together.");
                    bool waved = WaitFor(delegate
                    {
                        return module.FolderRebuildsRunForDiagnostics > folderRuns && module.FolderRebuildIdleForDiagnostics &&
                               module.PoolContainsForDiagnostics("Wave fortune number 8, one of nine the timer folds together.");
                    }, 15000);
                    System.Threading.Thread.Sleep(1500);   // long enough for a second elapse, if anything re-armed one
                    settled();
                    module.FolderQuietWindowForDiagnostics = TimeSpan.FromMinutes(10);
                    sb.AppendLine("    timer burst: " + (module.FolderEventsSeenForDiagnostics - seen) + " notifications, " +
                        (module.FolderRebuildsRunForDiagnostics - folderRuns) + " folder rebuild(s)");
                    ok &= Check(sb, "nine packs written together are ONE elapse of the real quiet window and ONE index build",
                        waved && module.FolderEventsSeenForDiagnostics - seen >= 9 &&
                        module.FolderRebuildsRunForDiagnostics == folderRuns + 1 &&
                        module.IndexBuildsStartedForDiagnostics == builds + 1);

                    // SINGLE-FLIGHT: with the UI thread's queue held, a second window that elapses while the first
                    // rebuild is still queued queues nothing; it marks one more pass for after it.
                    var queue = new QueueingContext();
                    module.UiContextForDiagnostics = queue;
                    builds = module.IndexBuildsStartedForDiagnostics;
                    folderRuns = module.FolderRebuildsRunForDiagnostics;
                    WritePack(folder, "xray", "Xray fortune, for the single-flight check.");
                    module.FolderQuietElapsedForDiagnostics();
                    module.FolderQuietElapsedForDiagnostics();
                    int queued = queue.Count;
                    module.UiContextForDiagnostics = null;
                    queue.RunAll();
                    settled();
                    ok &= Check(sb, "a quiet window that elapses while a folder rebuild is still queued queues no second one (single-flight)",
                        queued == 1 && module.FolderRebuildsRunForDiagnostics == folderRuns + 1 &&
                        module.IndexBuildsStartedForDiagnostics == builds + 1 &&
                        module.PoolContainsForDiagnostics("Xray fortune, for the single-flight check."));

                    // THE MODULE'S OWN WRITES wait for their own rebuild: a window that elapses inside the bracket
                    // rebuilds nothing; once it is released the next window does.
                    builds = module.IndexBuildsStartedForDiagnostics;
                    engines = module.EngineRebuildsForDiagnostics;
                    using (module.OwnFolderWritesForDiagnostics())
                    {
                        WritePack(folder, "echo", "Echo fortune, written while the module writes the folder.");
                        module.FolderQuietElapsedForDiagnostics();
                        settled();
                        ok &= Check(sb, "a quiet window that elapses while Import or Download is writing the folder rebuilds nothing",
                            module.EngineRebuildsForDiagnostics == engines &&
                            module.IndexBuildsStartedForDiagnostics == builds &&
                            !module.PoolContainsForDiagnostics("Echo fortune, written while the module writes the folder."));
                    }
                    module.FolderQuietElapsedForDiagnostics();
                    settled();
                    ok &= Check(sb, "WITNESS the same window once the writes are done rebuilds, and the pack is live",
                        module.EngineRebuildsForDiagnostics == engines + 1 &&
                        module.IndexBuildsStartedForDiagnostics == builds + 1 &&
                        module.PoolContainsForDiagnostics("Echo fortune, written while the module writes the folder."));

                    // IMPORT and DOWNLOAD: one build each, a two-pack download included, and the folder notifications
                    // their own writes raised cost nothing afterwards.
                    string mine = Path.Combine(storage.DataDirectory, "foxtrot.txt");
                    File.WriteAllText(mine, "Foxtrot fortune, imported through the pane.\n", new UTF8Encoding(false));
                    host.PickedFiles = new List<string> { mine };
                    builds = module.IndexBuildsStartedForDiagnostics;
                    string imported = import.InvokeAsync().GetAwaiter().GetResult() ?? "";
                    host.PickedFiles = new List<string>();
                    settled();
                    ok &= Check(sb, "an Import starts exactly one build, for a pack change",
                        imported.IndexOf("Imported 1 pack", StringComparison.Ordinal) >= 0 &&
                        module.IndexBuildsStartedForDiagnostics == builds + 1 &&
                        module.IndexReasonForDiagnostics == IndexChange.Packs);
                    host.CatalogItems[CatalogKinds.Pack] = new List<CatalogItem>
                    {
                        new CatalogItem { Id = "golf", Name = "Golf", Bytes = 10, Count = 1 },
                        new CatalogItem { Id = "hotel", Name = "Hotel", Bytes = 10, Count = 1 },
                    };
                    host.CatalogPayloads[CatalogKinds.Pack + "/golf"] = new UTF8Encoding(false).GetBytes("Golf fortune, from the catalog.\n");
                    host.CatalogPayloads[CatalogKinds.Pack + "/hotel"] = new UTF8Encoding(false).GetBytes("Hotel fortune, from the catalog.\n");
                    check.InvokeAsync().GetAwaiter().GetResult();
                    selectAll.InvokeAsync().GetAwaiter().GetResult();
                    builds = module.IndexBuildsStartedForDiagnostics;
                    engines = module.EngineRebuildsForDiagnostics;
                    string downloaded = download.InvokeAsync().GetAwaiter().GetResult() ?? "";
                    settled();
                    ok &= Check(sb, "a Download of two packs is one rebuild and one index build",
                        downloaded.IndexOf("Downloaded 2 packs", StringComparison.Ordinal) >= 0 &&
                        module.EngineRebuildsForDiagnostics == engines + 1 &&
                        module.IndexBuildsStartedForDiagnostics == builds + 1);
                    module.FolderQuietElapsedForDiagnostics();
                    settled();
                    ok &= Check(sb, "...and the window their writes armed builds nothing after them",
                        module.EngineRebuildsForDiagnostics == engines + 1 &&
                        module.IndexBuildsStartedForDiagnostics == builds + 1);

                    // THE SELECTION: a pack unticked at Apply, the content level, the genres, the smart switch.
                    builds = module.IndexBuildsStartedForDiagnostics;
                    packs.SetChecked("alpha", false);
                    bool saved = pane.Save(pane.Load());
                    ok &= Check(sb, "an Apply that unticks a pack starts exactly one build, for a selection change",
                        saved && settled() && module.IndexBuildsStartedForDiagnostics == builds + 1 &&
                        module.IndexReasonForDiagnostics == IndexChange.Selection);
                    builds = module.IndexBuildsStartedForDiagnostics;
                    saved = pane.Save(pane.Load());
                    ok &= Check(sb, "WITNESS an Apply that changes nothing starts none",
                        saved && settled() && module.IndexBuildsStartedForDiagnostics == builds);
                    WritePack(folder, "tagged",
                        "tagged\tfamily\tjoke\tgeneral\t0\tA general tagged fortune for the content level check.",
                        "tagged\tfamily\tjoke\tedgy\t0\tAn edgy tagged fortune for the content level check.");
                    rescan.InvokeAsync().GetAwaiter().GetResult();
                    settled();
                    builds = module.IndexBuildsStartedForDiagnostics;
                    var level = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, string> kv in pane.Load()) level[kv.Key] = kv.Value;
                    level["contentLevel"] = "Clean + edgy";
                    saved = pane.Save(level);
                    ok &= Check(sb, "a content-level change that widens the pool starts exactly one build",
                        saved && settled() && module.IndexBuildsStartedForDiagnostics == builds + 1 &&
                        module.PoolContainsForDiagnostics("An edgy tagged fortune for the content level check."));
                    builds = module.IndexBuildsStartedForDiagnostics;
                    genresNone.InvokeAsync().GetAwaiter().GetResult();
                    string noGenres = line();
                    ok &= Check(sb, "unticking every genre empties the pool, builds nothing (RA-120), and the line says why",
                        settled() && module.IndexBuildsStartedForDiagnostics == builds &&
                        module.SmartPickerForDiagnostics == null &&
                        noGenres.IndexOf("No fortunes match these filters", StringComparison.Ordinal) >= 0);
                    genresAll.InvokeAsync().GetAwaiter().GetResult();
                    ok &= Check(sb, "ticking them all again starts exactly one build, for a selection change",
                        settled() && module.IndexBuildsStartedForDiagnostics == builds + 1 &&
                        module.IndexReasonForDiagnostics == IndexChange.Selection);
                    builds = module.IndexBuildsStartedForDiagnostics;
                    var smartOff = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, string> kv in pane.Load()) smartOff[kv.Key] = kv.Value;
                    smartOff["smartFortunes"] = "false";
                    pane.Save(smartOff);
                    string offLine;
                    pane.Load().TryGetValue("smartIndex", out offLine);
                    ok &= Check(sb, "turning smart picks off builds nothing, drops the picker, and the line says picks are random",
                        settled() && module.IndexBuildsStartedForDiagnostics == builds &&
                        module.SmartPickerForDiagnostics == null &&
                        offLine == "Smart picks are off, so fortunes are chosen at random.");
                    smartOff["smartFortunes"] = "true";
                    pane.Save(smartOff);
                    ok &= Check(sb, "turning them back on starts exactly one build",
                        settled() && module.IndexBuildsStartedForDiagnostics == builds + 1);

                    // A FAILED BUILD is retried once on its own, through its real timer; then the line says the
                    // retry failed too and nothing retries until something changes.
                    factoryFault = new InvalidDataException("injected by the probe");
                    module.RetryDelayForDiagnostics = TimeSpan.FromMilliseconds(150);
                    builds = module.IndexBuildsStartedForDiagnostics;
                    WritePack(folder, "india", "India fortune, whose build is made to fail.");
                    rescan.InvokeAsync().GetAwaiter().GetResult();
                    bool retried = WaitFor(delegate
                    {
                        return module.IndexBuildsStartedForDiagnostics >= builds + 2 &&
                               module.RetryDueUtcForDiagnostics == DateTime.MinValue;
                    }, 15000) && settled();   // ...and the retry's own build has ENDED (failed), not merely started
                    // ONCE is proved by what does NOT happen next: three more retry periods with nothing due and nothing
                    // started. A retry that kept going would pass the wait above in the gap between one of its builds
                    // being scheduled and failing.
                    System.Threading.Thread.Sleep(500);
                    retried = retried && settled() && module.RetryDueUtcForDiagnostics == DateTime.MinValue;
                    module.RetryDelayForDiagnostics = TimeSpan.FromMinutes(10);
                    string spent = line();
                    sb.AppendLine("    smart index after the retry failed: " + spent);
                    ok &= Check(sb, "a build that fails is retried exactly once on its own, by the retry timer",
                        retried && module.IndexBuildsStartedForDiagnostics == builds + 2 &&
                        module.IndexReasonForDiagnostics == IndexChange.Retry);
                    ok &= Check(sb, "...and then the line says the build failed, that the automatic retry failed too, and what retries it next",
                        spent.StartsWith("✗ Off, fortunes are chosen at random (the index could not be built: InvalidDataException, and the automatic retry failed too)", StringComparison.Ordinal));
                    module.RetryIndexNowForDiagnostics();
                    ok &= Check(sb, "WITNESS with the retry spent, nothing retries on its own",
                        settled() && module.IndexBuildsStartedForDiagnostics == builds + 2);
                    // A Rescan over the unchanged folder is the user's request to retry: it starts one, and with it a
                    // new episode, so the failure schedules its own retry and the line names the time.
                    rescan.InvokeAsync().GetAwaiter().GetResult();
                    bool due = WaitFor(delegate { return module.RetryDueUtcForDiagnostics != DateTime.MinValue; }, 15000) && settled();
                    string pending = line();
                    ok &= Check(sb, "a Rescan over an unchanged folder retries a failed index, and its failure schedules a new retry the line names",
                        due && module.IndexBuildsStartedForDiagnostics == builds + 3 &&
                        pending.StartsWith("✗ Off for now, fortunes are chosen at random (the index could not be built: InvalidDataException). Trying again at ", StringComparison.Ordinal));
                    factoryFault = null;
                    module.RetryIndexNowForDiagnostics();
                    ok &= Check(sb, "...and the retry, once the fault has gone, builds a picker again",
                        settled() && module.IndexBuildsStartedForDiagnostics == builds + 4 &&
                        module.SmartPickerForDiagnostics != null &&
                        module.RetryDueUtcForDiagnostics == DateTime.MinValue);

                    // SHUTDOWN: nothing it left armed, and nothing pressed after it, starts anything.
                    factoryFault = new InvalidDataException("injected by the probe");
                    WritePack(folder, "juliet", "Juliet fortune, whose failed build leaves a retry pending at Shutdown.");
                    rescan.InvokeAsync().GetAwaiter().GetResult();
                    if (WaitFor(delegate { return module.RetryDueUtcForDiagnostics != DateTime.MinValue; }, 15000)) settled();
                    WritePack(folder, "kilo", "Kilo fortune, whose notification is still armed at Shutdown.");
                    factoryFault = null;
                    module.Shutdown();
                    builds = module.IndexBuildsStartedForDiagnostics;
                    engines = module.EngineRebuildsForDiagnostics;
                    folderRuns = module.FolderRebuildsRunForDiagnostics;
                    module.FolderQuietElapsedForDiagnostics();
                    ok &= Check(sb, "after Shutdown a quiet window that elapses starts no folder rebuild",
                        module.FolderRebuildsRunForDiagnostics == folderRuns);
                    module.RetryIndexNowForDiagnostics();
                    ok &= Check(sb, "after Shutdown the retry that was pending starts no build",
                        settled() && module.IndexBuildsStartedForDiagnostics == builds);
                    rescan.InvokeAsync().GetAwaiter().GetResult();
                    ok &= Check(sb, "after Shutdown a Rescan publishes no provider into the module",
                        settled() && module.EngineRebuildsForDiagnostics == engines &&
                        !module.PoolContainsForDiagnostics("Kilo fortune, whose notification is still armed at Shutdown."));
                    ok &= Check(sb, "every build and folder rebuild of the auto-index scenario ended whenever it was waited for (within 20 s)",
                        !stuck);
                }
                catch (Exception ex)
                {
                    ok &= Check(sb, "the auto-index scenario ran (" + ex.GetType().Name + ": " + ex.Message + ")", false);
                }
                finally
                {
                    if (hold != null) hold.Set();
                    try { module.Shutdown(); } catch { }
                    ok &= Check(sb, "every smart build of the auto-index scenario had ended before its storage was removed (joined within 20 s)",
                        module.JoinSmartBuildsForDiagnostics(TimeSpan.FromSeconds(20)));
                    FortunePaths.SetRoot(previousRoot);
                    System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);
                }
            }
            return ok;
        }

        /// <summary>
        /// The two halves of the self-maintaining index that need the real embedder (fortunes 1.1.0). A real warm
        /// over a three-line pool, so the line's "✓ Up to date (N fortunes, built HH:mm)" is read off a picker
        /// that really completed. Then a warm that THROWS (a picker whose vector cache holds two entries, below
        /// the pool, the R-031 fixture): its end is seen through the warm task, the automatic retry is scheduled,
        /// and the line names when it runs. Two session loads and a handful of embeds, in-process, on the CPU;
        /// no model server is involved anywhere.
        /// </summary>
        private static bool AutoIndexModelChecks(StringBuilder sb)
        {
            bool ok = true;
            string previousRoot = FortunePaths.RootForDiagnostics;
            System.Threading.SynchronizationContext previousContext = System.Threading.SynchronizationContext.Current;
            System.Threading.SynchronizationContext.SetSynchronizationContext(null);
            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            var module = new FortunesModule();
            using (var storage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("fortunes-autoindex-model"))
            {
                host.UseStorage("fortunes", storage);
                DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings settings = host.SettingsFor("fortunes");
                settings.Set("smartFortunes", "true");
                settings.Set("disabledSources", "fortunes\ndadjokes");
                settings.Save();
                string folder = Path.Combine(storage.DataDirectory, "fortunes");
                string realCache = Path.Combine(storage.DataDirectory, "probe-real");
                string tinyCache = Path.Combine(storage.DataDirectory, "probe-tiny");
                string absentCache = Path.Combine(storage.DataDirectory, "probe-absent");
                int mode = 0;   // 0: a real picker; 1: a picker whose warm throws; 2: a picker that reports the model absent
                module.PickerFactoryForDiagnostics = delegate
                {
                    if (mode == 1) return new SmartFortunes(tinyCache, 2);
                    if (mode == 2) return new SmartFortunes(absentCache, true);
                    return new SmartFortunes(realCache);
                };
                module.FolderQuietWindowForDiagnostics = TimeSpan.FromMinutes(10);
                module.RetryDelayForDiagnostics = TimeSpan.FromMinutes(10);
                try
                {
                    Directory.CreateDirectory(folder);
                    WritePack(folder, "lima", "Lima fortune about writing code all night.",
                        "Lima fortune about the weather turning cold.", "Lima fortune about a long walk home.");
                    module.Init(host);
                    bool complete = WaitFor(delegate
                    {
                        SmartFortunes live = module.SmartPickerForDiagnostics;
                        if (live == null) return false;
                        bool ready, done; int indexed, total;
                        live.WarmProgress(out ready, out done, out indexed, out total);
                        return done;
                    }, 60000);
                    string upToDate = module.SmartStatusTextForDiagnostics();
                    sb.AppendLine("    smart index after a real warm: " + upToDate);
                    ok &= Check(sb, "a real warm that completed reads up to date, with the pool's count and the time it was built",
                        complete && upToDate.StartsWith("✓ Up to date (" + 3.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) +
                            " fortunes, built ", StringComparison.Ordinal));
                    OptionsPane pane = host.OptionsPanes.Count > 0 ? host.OptionsPanes[0] : null;
                    string paneLine = null;
                    if (pane != null && pane.Load != null) pane.Load().TryGetValue("smartIndex", out paneLine);
                    ok &= Check(sb, "...and the pane's Load shows the same line", string.Equals(paneLine, upToDate, StringComparison.Ordinal));
                    string statusUp = null;
                    if (pane != null && pane.Load != null) pane.Load().TryGetValue("status", out statusUp);
                    sb.AppendLine("    status after a real warm: " + statusUp);
                    ok &= Check(sb, "...and the Status line says the index is up to date and when it was built",
                        statusUp != null && statusUp.StartsWith(3.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) +
                            " fortunes from 1 source | smart index: up to date (built ", StringComparison.Ordinal));

                    PaneAction rescan = FindCardAction(pane, "Fortune packs", "Rescan folder");
                    mode = 1;
                    int builds = module.IndexBuildsStartedForDiagnostics;
                    WritePack(folder, "mike", "Mike fortune, the line that makes the tiny cache overflow.");
                    if (rescan != null) rescan.InvokeAsync().GetAwaiter().GetResult();
                    bool due = WaitFor(delegate { return module.RetryDueUtcForDiagnostics != DateTime.MinValue; }, 30000);
                    string pending = module.SmartStatusTextForDiagnostics();
                    sb.AppendLine("    smart index after a warm that threw: " + pending);
                    ok &= Check(sb, "a warm that throws is seen at its end and its automatic retry is scheduled, and the line names when",
                        rescan != null && due && module.IndexBuildsStartedForDiagnostics == builds + 1 &&
                        pending.StartsWith("✗ Off for now, fortunes are chosen at random (the index could not be built: InvalidDataException). Trying again at ", StringComparison.Ordinal));
                    mode = 2;
                    module.RetryIndexNowForDiagnostics();
                    ok &= Check(sb, "...and the retry builds again, for the retry",
                        WaitFor(delegate { return module.SmartBuildsInFlightForDiagnostics == 0; }, 20000) &&
                        module.IndexBuildsStartedForDiagnostics == builds + 2 &&
                        module.IndexReasonForDiagnostics == IndexChange.Retry &&
                        module.RetryDueUtcForDiagnostics == DateTime.MinValue);
                }
                catch (Exception ex)
                {
                    ok &= Check(sb, "the model-backed auto-index scenario ran (" + ex.GetType().Name + ": " + ex.Message + ")", false);
                }
                finally
                {
                    try { module.Shutdown(); } catch { }
                    ok &= Check(sb, "every smart build of the model-backed scenario had ended before its storage was removed (joined within 20 s)",
                        module.JoinSmartBuildsForDiagnostics(TimeSpan.FromSeconds(20)));
                    FortunePaths.SetRoot(previousRoot);
                    System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);
                }
            }
            return ok;
        }

        /// <summary>
        /// A folder that cannot be watched says so: in the log once, and on the Smart index line on every read,
        /// with Rescan folder as the way to pick a change up. Forced by a FILE where the fortunes folder belongs,
        /// so the folder cannot be created and the watcher cannot open it; then the folder is made good and Rescan
        /// folder must watch it again. WITNESS: a real folder is watched and the line carries no warning, a Rescan
        /// there starts no second watch, and of the watcher's two error kinds only the one that stops it (not an
        /// overflowed buffer) is followed by a new watcher. Smart picks off: no index work.
        /// </summary>
        private static bool FolderWatchChecks(StringBuilder sb)
        {
            bool ok = true;
            string previousRoot = FortunePaths.RootForDiagnostics;
            // No SynchronizationContext, so the quiet windows elapsed below run their rebuild inline.
            System.Threading.SynchronizationContext previousContext = System.Threading.SynchronizationContext.Current;
            System.Threading.SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                ok &= FolderWatchScenarios(sb, previousRoot);
            }
            finally
            {
                System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);
            }
            return ok;
        }

        private static bool FolderWatchScenarios(StringBuilder sb, string previousRoot)
        {
            bool ok = true;
            using (var storage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("fortunes-unwatched"))
            {
                var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                host.UseStorage("fortunes", storage);
                host.SettingsFor("fortunes").Set("smartFortunes", "false");
                var module = new FortunesModule();
                try
                {
                    File.WriteAllText(Path.Combine(storage.DataDirectory, "fortunes"), "not a folder", new UTF8Encoding(false));
                    module.Init(host);
                    string text = module.SmartStatusTextForDiagnostics();
                    ok &= Check(sb, "a fortunes folder that cannot be watched is recorded, logged once, and named on the Smart index line with Rescan folder",
                        module.FolderWatchProblemForDiagnostics != null &&
                        host.LoggedLines.FindAll(delegate(string l) { return l.IndexOf("fortunes folder not watched: ", StringComparison.Ordinal) >= 0; }).Count == 1 &&
                        text.StartsWith("Smart picks are off", StringComparison.Ordinal) &&
                        text.IndexOf("⚠ The fortunes folder cannot be watched here", StringComparison.Ordinal) > 0);
                    ok &= Check(sb, "...and the log line carries a category, never the folder's path",
                        host.LoggedLines.TrueForAll(delegate(string l) { return l.IndexOf(storage.DataDirectory, StringComparison.OrdinalIgnoreCase) < 0; }));

                    // RESCAN FOLDER STARTS A WATCH THAT IS DOWN. The folder made good (the file gone, a folder in its
                    // place), one press of Rescan folder watches it again and the line loses its warning: without it
                    // a folder deleted and made again stayed unwatched until the next start.
                    File.Delete(Path.Combine(storage.DataDirectory, "fortunes"));
                    Directory.CreateDirectory(Path.Combine(storage.DataDirectory, "fortunes"));
                    int starts = module.FolderWatchStartsForDiagnostics;
                    PaneAction rescanDown = FindCardAction(host.OptionsPanes.Count > 0 ? host.OptionsPanes[0] : null, "Fortune packs", "Rescan folder");
                    if (rescanDown != null) rescanDown.InvokeAsync().GetAwaiter().GetResult();
                    ok &= Check(sb, "Rescan folder starts a watch that was down, and once it is watched the line loses its warning",
                        rescanDown != null && module.FolderWatchStartsForDiagnostics == starts + 1 &&
                        module.FolderWatchProblemForDiagnostics == null &&
                        module.SmartStatusTextForDiagnostics().IndexOf("⚠", StringComparison.Ordinal) < 0);
                }
                catch (Exception ex)
                {
                    ok &= Check(sb, "the unwatched-folder scenario ran (" + ex.GetType().Name + ": " + ex.Message + ")", false);
                }
                finally
                {
                    try { module.Shutdown(); } catch { }
                    FortunePaths.SetRoot(previousRoot);
                }
            }
            using (var storage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("fortunes-watched"))
            {
                var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                host.UseStorage("fortunes", storage);
                host.SettingsFor("fortunes").Set("smartFortunes", "false");
                var module = new FortunesModule();
                try
                {
                    module.Init(host);
                    module.FolderQuietWindowForDiagnostics = TimeSpan.FromMinutes(10);
                    ok &= Check(sb, "WITNESS a real fortunes folder is watched, nothing is logged about it, and the line carries no warning",
                        module.FolderWatchProblemForDiagnostics == null &&
                        !host.LoggedLines.Exists(delegate(string l) { return l.IndexOf("not watched", StringComparison.Ordinal) >= 0; }) &&
                        module.SmartStatusTextForDiagnostics().IndexOf("⚠", StringComparison.Ordinal) < 0);
                    int starts = module.FolderWatchStartsForDiagnostics;
                    PaneAction rescanUp = FindCardAction(host.OptionsPanes.Count > 0 ? host.OptionsPanes[0] : null, "Fortune packs", "Rescan folder");
                    if (rescanUp != null) rescanUp.InvokeAsync().GetAwaiter().GetResult();
                    ok &= Check(sb, "WITNESS a Rescan over a folder that is watched starts no second watch",
                        rescanUp != null && module.FolderWatchStartsForDiagnostics == starts);

                    // AN ERROR THAT STOPS THE WATCHER (the folder deleted, its volume gone) is followed by a new watcher
                    // at the next quiet window. An OVERFLOWED buffer is not: details were lost, the watcher still runs,
                    // and the rebuild's fingerprint settles what changed.
                    module.RaiseFolderErrorForDiagnostics(new InternalBufferOverflowException("probe"));
                    module.FolderQuietElapsedForDiagnostics();
                    WaitFor(delegate { return module.FolderRebuildIdleForDiagnostics; }, 20000);
                    ok &= Check(sb, "WITNESS an overflowed watcher buffer rebuilds without replacing the watcher",
                        module.FolderWatchStartsForDiagnostics == starts);
                    module.RaiseFolderErrorForDiagnostics(new IOException("probe: the watched folder went away"));
                    module.FolderQuietElapsedForDiagnostics();
                    WaitFor(delegate { return module.FolderRebuildIdleForDiagnostics; }, 20000);
                    ok &= Check(sb, "a watcher that reported an error is replaced at the next quiet window",
                        module.FolderWatchStartsForDiagnostics == starts + 1 && module.FolderWatchProblemForDiagnostics == null);
                }
                catch (Exception ex)
                {
                    ok &= Check(sb, "the watched-folder WITNESS ran (" + ex.GetType().Name + ": " + ex.Message + ")", false);
                }
                finally
                {
                    try { module.Shutdown(); } catch { }
                    FortunePaths.SetRoot(previousRoot);
                }
            }
            return ok;
        }

        /// <summary>A UI thread that is busy: Post queues, nothing runs until <see cref="RunAll"/>.</summary>
        private sealed class QueueingContext : System.Threading.SynchronizationContext
        {
            private readonly Queue<KeyValuePair<System.Threading.SendOrPostCallback, object>> _posted =
                new Queue<KeyValuePair<System.Threading.SendOrPostCallback, object>>();

            public int Count { get { lock (_posted) return _posted.Count; } }

            public override void Post(System.Threading.SendOrPostCallback d, object state)
            {
                lock (_posted) _posted.Enqueue(new KeyValuePair<System.Threading.SendOrPostCallback, object>(d, state));
            }

            public void RunAll()
            {
                while (true)
                {
                    KeyValuePair<System.Threading.SendOrPostCallback, object> next;
                    lock (_posted)
                    {
                        if (_posted.Count == 0) return;
                        next = _posted.Dequeue();
                    }
                    next.Key(next.Value);
                }
            }
        }

        private static void WritePack(string folder, string id, params string[] lines)
        {
            File.WriteAllText(Path.Combine(folder, id + ".txt"), string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        }

        /// <summary>Poll <paramref name="condition"/> every 10 ms for up to <paramref name="milliseconds"/>.</summary>
        private static bool WaitFor(Func<bool> condition, int milliseconds)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                bool met;
                try { met = condition(); } catch { met = false; }
                if (met) return true;
                if (sw.ElapsedMilliseconds >= milliseconds) return false;
                System.Threading.Thread.Sleep(10);
            }
        }

        /// <summary>Init a module against <paramref name="storage"/> with smart picks off and the given
        /// disabled-source list, read its pane's pool status, and shut it down.</summary>
        private static string PoolStatusUnder(DesktopAICompanion.ModuleKit.Testing.TempModuleStorage storage, string disabledSources)
        {
            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            host.UseStorage("fortunes", storage);
            DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings settings = host.SettingsFor("fortunes");
            settings.Set("smartFortunes", "false");
            settings.Set("disabledSources", disabledSources);
            settings.Save();
            var module = new FortunesModule();
            try
            {
                module.Init(host);
                OptionsPane pane = host.OptionsPanes.Count > 0 ? host.OptionsPanes[0] : null;
                string status = null;
                if (pane != null && pane.Load != null) pane.Load().TryGetValue("poolStatus", out status);
                return status ?? "";
            }
            finally
            {
                try { module.Shutdown(); } catch { }
            }
        }

        /// <summary>The ids of a stored "disabled" list ('\n'-joined, as the module persists it).</summary>
        private static HashSet<string> IdSet(string joined)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in (joined ?? "").Split('\n'))
                if (part.Trim().Length > 0) ids.Add(part.Trim());
            return ids;
        }

        private static ListCard FindCard(OptionsPane pane, string cardTitle)
        {
            if (pane == null || pane.Lists == null) return null;
            foreach (ListCard card in pane.Lists)
                if (card != null && string.Equals(card.Title, cardTitle, StringComparison.Ordinal)) return card;
            return null;
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

        private static SettingField FindField(OptionsPane pane, string id)
        {
            if (pane == null || pane.Schema == null) return null;
            foreach (SettingField f in pane.Schema)
                if (f != null && string.Equals(f.Id, id, StringComparison.Ordinal)) return f;
            return null;
        }

        /// <summary>An action by label ANYWHERE on the pane: its own actions and every card's. For asserting
        /// an action is gone, where a card-scoped lookup would miss it on a card the test did not name.</summary>
        private static PaneAction FindPaneAction(OptionsPane pane, string label)
        {
            if (pane == null) return null;
            if (pane.Actions != null)
                foreach (PaneAction a in pane.Actions)
                    if (a != null && string.Equals(a.Label, label, StringComparison.Ordinal)) return a;
            if (pane.Lists != null)
                foreach (ListCard card in pane.Lists)
                    if (card != null && card.Actions != null)
                        foreach (PaneAction a in card.Actions)
                            if (a != null && string.Equals(a.Label, label, StringComparison.Ordinal)) return a;
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
