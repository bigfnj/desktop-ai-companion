using System;
using System.Collections.Generic;
using System.Text;
using DesktopAICompanion.Ai;
using DesktopAICompanion.ModuleKit;   // AtomicFile / CrossSessionLock / UnicodeTextProgress

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
                    embedded.Count > 5000);
                var embeddedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FortuneEntry e in embedded) embeddedSources.Add(e.Source ?? "");
                // These seven ship only in the corpus -- no pack file carries them -- so they are the ones
                // that vanish without a trace if the embed is dropped again.
                bool orphansPresent = true;
                foreach (string s in new[] { "quotable", "cleanjokes", "fortunes", "godin", "SimpsonsChalkboard", "activists", "BibleAbridged" })
                    if (!embeddedSources.Contains(s)) { orphansPresent = false; sb.AppendLine("    missing corpus source: " + s); }
                ok &= Check(sb, "the corpus sources that exist in no pack file are all present", orphansPresent);

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
                string building = FortunesModule.SmartStatusFor(true, 12345, true, false, false, 0, 0);
                ok &= Check(sb, "a just-started warm reports indexing, not an empty pool",
                    building.IndexOf("12,345", StringComparison.Ordinal) >= 0 &&
                    building.IndexOf("No fortunes", StringComparison.Ordinal) < 0);
                ok &= Check(sb, "a finished index reports what it indexed",
                    FortunesModule.SmartStatusFor(true, 900, true, true, true, 900, 900)
                        .IndexOf("ready", StringComparison.Ordinal) >= 0);
                ok &= Check(sb, "a partly-warm index says it is usable now",
                    FortunesModule.SmartStatusFor(true, 900, true, true, false, 100, 900)
                        .IndexOf("usable now", StringComparison.Ordinal) >= 0);
                ok &= Check(sb, "smart picks off is reported as off, not as an empty pool",
                    FortunesModule.SmartStatusFor(false, 0, false, false, false, 0, 0)
                        .IndexOf("off", StringComparison.Ordinal) >= 0);
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

                // The engine's full self-test suite, running in the module's context.
                bool filter = FortuneProvider.FilterSelfTest();
                ok &= Check(sb, "engine FilterSelfTest (dedup/classifier/parser/ingestion/importer)", filter);

                // --- smart layer: proves ONNX loads + runs inside the module's own load context ---
                ok &= Check(sb, "bge-small model present beside the module", Embedder.ModelPresent);
                if (Embedder.ModelPresent)
                {
                    // Embedder.SelfTest loads the ONNX model + embeds hardcoded strings and checks
                    // cos(code,code) > cos(code,weather) - the definitive proof that native onnxruntime.dll
                    // resolved and ran in the module's AssemblyLoadContext.
                    ok &= Check(sb, "Embedder loads ONNX + embeds in the module ALC", Embedder.SelfTest());

                    // SmartFortunes warm/pick over the injected pool exercises the rebinds: VectorCache
                    // (AtomicFile + FortunePaths.VectorCacheDir) + CrossSessionLock, all in-module. The
                    // public parameterless ctor uses the default cache dir = FortunePaths.VectorCacheDir.
                    using (var sm = new SmartFortunes())
                    {
                        sm.Warm(entries);
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        bool ready = false, complete = false; int idx = 0, total = 0;
                        while (!complete && sw.ElapsedMilliseconds < 60000)
                        {
                            sm.WarmProgress(out ready, out complete, out idx, out total);
                            if (!complete) System.Threading.Thread.Sleep(100);
                        }
                        ok &= Check(sb, "SmartFortunes warms the injected pool in-module (VectorCache/lock rebinds)",
                            sm.Ready && sm.PoolCount == entries.Count);
                        string pick = sm.Pick("Visual Studio Code editing a C# file", "devenv");
                        sb.AppendLine("    smart pick -> " + (pick ?? "(random fallback)"));
                    }

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

        private static bool Check(StringBuilder sb, string name, bool cond) { sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name); return cond; }
    }
}
