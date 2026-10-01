using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.Plugins
{
    /// <summary>
    /// --fortunes-selftest: proves the Fortunes module's LIVE behavior (S3d). Loads the real bundled
    /// Fortunes.dll through the AssemblyLoadContext loader against a recording host whose storage holds a
    /// throwaway pack, then asserts: the personalized welcome fires once on the first spawn; the module is
    /// wired to CompanionLanded / CompanionPoked / a drop responder; each of those speaks a fortune drawn from the pack;
    /// a poke in the base's "ignore" range (3-4) stays silent; and Shutdown unsubscribes everything. This is
    /// the end-to-end check that the base handed fortune-speaking to the module with no double-speak.
    /// Skips-pass if the module is absent.
    /// </summary>
    internal static class FortunesModuleSelfTest
    {
        private static readonly string[] Pack =
        {
            "Probe fortune alpha, a calm line.",
            "Probe fortune bravo, another line.",
            "Probe fortune charlie, one more.",
        };

        // Written to dadjokes.txt -- a REAL catalog pack id, so the grouping assertion can prove a known
        // pack resolves to its curated collection instead of the user's-own fallback.
        private const string DadJokeLine = "Why did the probe cross the road? To group itself.";

        public static bool Run()
        {
            var sb = new StringBuilder();
            bool ok = true;
            string storageDir = null;
            string dataRootScratch = null;
            string previousDataRoot = null;
            bool dataRootRedirected = false;
            try
            {
                string modulesRoot = Path.Combine(AppContext.BaseDirectory, "modules");
                if (!Directory.Exists(Path.Combine(modulesRoot, "fortunes")))
                {
                    sb.AppendLine("SKIP: no bundled fortunes module at " + Path.Combine(modulesRoot, "fortunes"));
                    return Finish(sb, true);
                }

                // ISOLATE THE DATA ROOT (RA-285), as --module-host-selftest does (F345). LoadFrom Inits every
                // bundled module, and AiBrain's Init-time migrator read the installed app's %LOCALAPPDATA%
                // ai-settings.json (the portable layout ignored) and copied it into this run's scratch for an
                // hour: module storage was isolated, the data root the migrator reads was not. The assertion is
                // what makes the override checkable rather than assumed.
                dataRootScratch = SelfTestScratch.Create("fortunes-data");
                previousDataRoot = Environment.GetEnvironmentVariable(AppPaths.DataRootOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(AppPaths.DataRootOverrideEnvironmentVariable, dataRootScratch);
                dataRootRedirected = true;
                ok &= Check(sb, "data root isolated for this run (every module Init below reads and writes under scratch)",
                    AppPaths.IsDataRootOverridden && ModuleHostSelfTest.SamePath(AppPaths.DataRoot, dataRootScratch));

                // Isolated module storage with a throwaway one-per-line pack (source = file name), so the
                // engine's pool is non-empty and land/poke/drop have something to say. "dadjokes" uses a
                // real catalog id so grouping can be checked against the curated collection map.
                storageDir = SelfTestScratch.Create("fortunes");
                Directory.CreateDirectory(Path.Combine(storageDir, "fortunes"));
                File.WriteAllText(Path.Combine(storageDir, "fortunes", "probepack.txt"), string.Join("\n", Pack) + "\n", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(storageDir, "fortunes", "dadjokes.txt"), DadJokeLine + "\n", new UTF8Encoding(false));
                // Both seeded packs feed one pool, so "spoke a fortune from the pack" must accept either --
                // otherwise the speech assertions are flaky depending on which pack the picker draws from.
                var packSet = new HashSet<string>(Pack, StringComparer.Ordinal) { DadJokeLine };

                string expectedName = string.IsNullOrWhiteSpace(Environment.UserName) ? "friend" : Environment.UserName.Trim();
                var host = new RecordingHost(storageDir);
                using (var loader = new ModuleHost())
                {
                    int loaded = loader.LoadFrom(modulesRoot, host, s => sb.AppendLine("  " + s));
                    ok &= Check(sb, "at least one module loaded", loaded >= 1);
                    object fortunesModule = FindModule(loader, "fortunes");
                    ok &= Check(sb, "fortunes module reports its id", fortunesModule != null);

                    // The module embeds a ~10k-line built-in corpus, so the two seeded packs are a rounding
                    // error in the pool and the picker will almost never draw one. "Spoke a fortune" has to
                    // be asked against the whole fortune universe, or these assertions only pass by luck.
                    int corpusLines = AddEmbeddedTexts(fortunesModule, packSet);
                    sb.AppendLine("  fortune universe: " + packSet.Count + " lines (" + corpusLines + " from the built-in corpus)");
                    ok &= Check(sb, "the built-in corpus reached the pool", corpusLines > 0);

                    // The settings seed keeps this flag from warming the whole corpus on every core (RA-284;
                    // R-026's mechanism). Asserted on the module's STATE through the status seam its pane reads,
                    // which Init sets synchronously, not on the "smart picker constructed, warming" log line:
                    // that line is written from the picker's pool thread after construction, so its absence
                    // caught the seed's removal in one harness run and missed it in the next. WITNESS first: the
                    // seam exists, so an off reading is the module's answer.
                    string smartStatus = ModuleHostSelfTest.SmartStatusOf(fortunesModule);
                    sb.AppendLine("  smart status after Init: " + (smartStatus ?? "<seam not found>"));
                    ok &= Check(sb, "WITNESS the module exposes SmartStatusTextForDiagnostics, the status line its pane reads",
                        smartStatus != null);
                    ok &= Check(sb, "the host-loaded module reports smart picks off after Init (smartFortunes is seeded off)",
                        smartStatus != null && smartStatus.StartsWith("Smart picks are off", StringComparison.Ordinal));

                    // The welcome corpus is a SECOND embedded payload, loaded separately from the
                    // fortunes corpus above, and until 2026-09-17 nothing asserted it arrived. The
                    // assertion below it -- "welcome speaks + is personalized" -- passes on a corpus
                    // of one, and would pass on a corpus of zero if the module ever gained a
                    // hardcoded fallback line. A missing or unparseable welcome.json is a
                    // shipped-payload failure, which is exactly what `WelcomeCorpusCount()` was
                    // built for and has never been called from.
                    int welcomeLines = WelcomeCorpusCount(fortunesModule);
                    sb.AppendLine("  welcome corpus: " + welcomeLines + " lines");
                    // A floor, not the exact 116: adding lines must not fail the gate. 100 is close
                    // enough that losing the payload, or loading a truncated one, still fails.
                    ok &= Check(sb, "the embedded welcome corpus loaded (>= 100 lines)", welcomeLines >= 100);

                    // The writable-folder cache: add / edit / remove must each be reflected without a
                    // restart. Only the ADD half is covered elsewhere ("a downloaded pack joins the
                    // live pool without a restart", further down); edit and remove had no coverage at
                    // all. It runs here rather than under --fortunes-engine-selftest because this is
                    // the self-test that points the engine at throwaway storage, so CustomDir lands
                    // in the scratch folder instead of the user's real fortunes directory.
                    ok &= RunCustomCacheSelfTest(sb, fortunesModule);

                    // Wiring: the module owns the fortune triggers now.
                    ok &= Check(sb, "subscribed to CompanionSpawned (welcome)", host.SpawnedHasSubs);
                    ok &= Check(sb, "subscribed to CompanionLanded", host.LandedHasSubs);
                    ok &= Check(sb, "subscribed to CompanionPoked", host.PokedHasSubs);
                    ok &= Check(sb, "registered a drop responder", host.HasDropResponder);
                    ok &= Check(sb, "registered a poke responder", host.HasPokeResponder);

                    // Other dev modules (e.g. TestModule) are loaded too and also speak, so each trigger is
                    // checked for "a pack line is AMONG what was said", not "the last thing said".
                    // Welcome on the first spawn.
                    host.Said.Clear();
                    host.RaiseCompanionSpawned(new FakeCompanion(1));
                    ok &= Check(sb, "welcome speaks + is personalized", host.Said.Exists(s => !string.IsNullOrEmpty(s) && s.IndexOf(expectedName, StringComparison.Ordinal) >= 0));

                    // Land -> a fortune from the pack.
                    host.Said.Clear();
                    host.RaiseCompanionLanded(new FakeCompanion(1));
                    sb.AppendLine("  land said: " + string.Join(" | ", host.Said));
                    ok &= Check(sb, "CompanionLanded speaks a fortune from the pack", host.Said.Exists(s => packSet.Contains(s)));

                    // The CompanionPoked EVENT is now tracking-only (it just notes which pet was poked): speaking on
                    // a poke goes through the arbitrated poke-responder chain instead, so exactly one module
                    // wins it and the user's "Trigger Speech" preference can pick which.
                    host.Said.Clear();
                    host.RaiseCompanionPoked(new PokeInfo { Pet = new FakeCompanion(1), PokeCount = 1 });
                    sb.AppendLine("  poke event said: " + string.Join(" | ", host.Said));
                    ok &= Check(sb, "the CompanionPoked event alone speaks no fortune (the responder chain owns it)",
                        !host.Said.Exists(s => packSet.Contains(s)));
                    host.Said.Clear();
                    bool pokeHandled = host.FirePoke(new FakeCompanion(1));
                    sb.AppendLine("  poke responder said: " + string.Join(" | ", host.Said));
                    ok &= Check(sb, "poke responder speaks a fortune + reports handled",
                        pokeHandled && host.Said.Exists(s => packSet.Contains(s)));

                    // Drop responder -> a fortune, and it reports handled.
                    host.Said.Clear();
                    bool handled = host.FireDrop(new FakeCompanion(1));
                    sb.AppendLine("  drop said: " + string.Join(" | ", host.Said));
                    ok &= Check(sb, "drop responder speaks a fortune + reports handled", handled && host.Said.Exists(s => packSet.Contains(s)));

                    // Catalog packs: browse reports what's missing, download writes it into the module's own
                    // fortunes folder and the new lines join the live pool. All offline (the RecordingHost
                    // stands in for the catalog), so this proves the module's install path, not the network.
                    host.CatalogItems.Add(new CatalogItem { Id = "probepack", Name = "Probe Pack", Bytes = 10, Count = 3 });
                    host.CatalogItems.Add(new CatalogItem { Id = "extrapack", Name = "Extra Pack", Bytes = 10, Count = 1 });
                    host.CatalogPayloads["extrapack"] = new UTF8Encoding(false).GetBytes("Probe fortune delta, from the catalog.\n");
                    OptionsPane fortunesPane = host.PaneNamed("Fortunes");
                    ListCard availableCard = FindCard(fortunesPane, "Available online");
                    PaneAction check = FindAction(fortunesPane, "Available online", "Check online for packs");
                    PaneAction download = FindAction(fortunesPane, "Available online", "Download selected");
                    PaneAction selectAll = FindAction(fortunesPane, "Available online", "Select all");
                    ok &= Check(sb, "pane offers the browse/select/download catalog actions",
                        availableCard != null && check != null && download != null && selectAll != null);
                    if (availableCard != null && check != null && download != null && selectAll != null)
                    {
                        // Downloading before browsing is refused rather than silently grabbing everything.
                        string prematureStatus = download.InvokeAsync().GetAwaiter().GetResult();
                        ok &= Check(sb, "download before browsing asks the user to check online first",
                            prematureStatus.IndexOf("Check online", StringComparison.Ordinal) >= 0);

                        string browseStatus = check.InvokeAsync().GetAwaiter().GetResult();
                        sb.AppendLine("  browse said: " + browseStatus);
                        // probepack is already on disk, so exactly one of the two is offered.
                        ok &= Check(sb, "browse lists only packs that are not installed yet",
                            browseStatus.IndexOf("1 pack available", StringComparison.Ordinal) >= 0 &&
                            availableCard.LoadItems().Count == 1);

                        // Browsing must not pre-select anything, and downloading nothing is refused.
                        bool nothingPreselected = true;
                        foreach (ListItem li in availableCard.LoadItems()) if (li.Checked) nothingPreselected = false;
                        string noneStatus = download.InvokeAsync().GetAwaiter().GetResult();
                        ok &= Check(sb, "browsing selects nothing and downloading nothing is refused",
                            nothingPreselected && noneStatus.IndexOf("No packs ticked", StringComparison.Ordinal) >= 0);

                        // The pool-join property itself, through the module's own diagnostic (RA-286): the
                        // catalog line is absent from the live pool before the download and present after it.
                        // Until 2026-10-01 this block re-asserted only that the drop responder still spoke, which
                        // was true before the download too.
                        const string deltaLine = "Probe fortune delta, from the catalog.";
                        MethodInfo poolContains = fortunesModule.GetType().GetMethod("PoolContainsForDiagnostics",
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        ok &= Check(sb, "the module exposes PoolContainsForDiagnostics", poolContains != null);
                        bool deltaBefore = poolContains != null && (bool)poolContains.Invoke(fortunesModule, new object[] { deltaLine });
                        ok &= Check(sb, "WITNESS the catalog pack's line is not in the live pool before the download", !deltaBefore);

                        selectAll.InvokeAsync().GetAwaiter().GetResult();
                        string downloadStatus = download.InvokeAsync().GetAwaiter().GetResult();
                        sb.AppendLine("  download said: " + downloadStatus);
                        bool wrote = File.Exists(Path.Combine(storageDir, "fortunes", "extrapack.txt"));
                        ok &= Check(sb, "downloading the selection writes it into the fortunes folder",
                            wrote && downloadStatus.IndexOf("Downloaded 1 pack", StringComparison.Ordinal) >= 0);
                        ok &= Check(sb, "an installed pack leaves the available list",
                            availableCard.LoadItems().Count == 0);

                        host.Said.Clear();
                        bool spokeAfter = host.FireDrop(new FakeCompanion(1));
                        ok &= Check(sb, "the drop responder still speaks after a download",
                            spokeAfter && host.Said.Count > 0);
                        ok &= Check(sb, "a downloaded pack's line is in the live pool without a restart",
                            poolContains != null && (bool)poolContains.Invoke(fortunesModule, new object[] { deltaLine }));

                        // F131: a catalog payload the folder loader would refuse is refused at download, not
                        // written and counted installed. The host verifies URL, hash and size; the CONTENT check
                        // is the module's. 200 bytes of 0xFF are not UTF-8, so no parser accepts them.
                        var badBytes = new byte[200];
                        for (int badIndex = 0; badIndex < badBytes.Length; badIndex++) badBytes[badIndex] = 0xFF;
                        host.CatalogItems.Add(new CatalogItem { Id = "badpack", Name = "Bad Pack", Bytes = 200, Count = 1 });
                        host.CatalogPayloads["badpack"] = badBytes;
                        check.InvokeAsync().GetAwaiter().GetResult();
                        selectAll.InvokeAsync().GetAwaiter().GetResult();
                        string badStatus = download.InvokeAsync().GetAwaiter().GetResult();
                        sb.AppendLine("  bad download said: " + badStatus);
                        ok &= Check(sb, "a malformed catalog payload is refused, not installed",
                            !File.Exists(Path.Combine(storageDir, "fortunes", "badpack.txt")) &&
                            badStatus.IndexOf("Downloaded 0 packs", StringComparison.Ordinal) >= 0 &&
                            badStatus.IndexOf("1 pack failed", StringComparison.Ordinal) >= 0);
                    }

                    // Bulk tick on the INSTALLED packs list. Separate from the catalog buttons above and
                    // easy to conflate with them, which is the whole reason the lookups are card-scoped.
                    ListCard packsCard = FindCard(fortunesPane, "Fortune packs");
                    PaneAction packsAll = FindAction(fortunesPane, "Fortune packs", "Select all");
                    PaneAction packsNone = FindAction(fortunesPane, "Fortune packs", "Select none");
                    ok &= Check(sb, "the installed-packs card offers select all/none",
                        packsCard != null && packsAll != null && packsNone != null);
                    if (packsCard != null && packsAll != null && packsNone != null)
                    {
                        int total = packsCard.LoadItems().Count;
                        packsNone.InvokeAsync().GetAwaiter().GetResult();
                        int checkedAfterNone = 0;
                        foreach (ListItem li in packsCard.LoadItems()) if (li.Checked) checkedAfterNone++;
                        // The card is DeferChanges, so this reload reads the SAVED setting. Without the
                        // staged overlay every box would redraw ticked and the button would look inert.
                        ok &= Check(sb, "select none unticks every installed pack in the reloaded list",
                            total > 0 && checkedAfterNone == 0);

                        packsAll.InvokeAsync().GetAwaiter().GetResult();
                        int checkedAfterAll = 0;
                        foreach (ListItem li in packsCard.LoadItems()) if (li.Checked) checkedAfterAll++;
                        ok &= Check(sb, "select all re-ticks every installed pack",
                            checkedAfterAll == total);
                    }

                    // Grouping: both pack cards ask for collapsible groups + a filter, and a pack the user
                    // supplied themselves is grouped as their own rather than lumped in with catalog packs.
                    ListCard installedCard = FindCard(fortunesPane, "Fortune packs");
                    ok &= Check(sb, "pack cards ask for grouping + filtering",
                        installedCard != null && installedCard.Filterable && installedCard.CollapseGroups &&
                        availableCard != null && availableCard.Filterable && availableCard.CollapseGroups);
                    if (installedCard != null)
                    {
                        bool everyItemGrouped = true;
                        string dadGroup = null, probeGroup = null;
                        foreach (ListItem li in installedCard.LoadItems())
                        {
                            if (string.IsNullOrWhiteSpace(li.Group)) { everyItemGrouped = false; break; }
                            if (string.Equals(li.Id, "dadjokes", StringComparison.OrdinalIgnoreCase)) dadGroup = li.Group;
                            if (string.Equals(li.Id, "probepack", StringComparison.OrdinalIgnoreCase)) probeGroup = li.Group;
                        }
                        ok &= Check(sb, "every installed pack resolves a group", everyItemGrouped);
                        // The bug this guards: SourceStat.Custom is true for EVERY pack in the user's folder
                        // (catalog downloads included), so grouping off it collapsed all 150 into one section.
                        // A known catalog id must resolve to its curated collection, and only genuinely
                        // unknown ids fall back to "More packs" -- i.e. at least two distinct groups.
                        sb.AppendLine("  dadjokes group: " + (dadGroup ?? "<none>") + " | probepack group: " + (probeGroup ?? "<none>"));
                        // Asserted as a property, not a literal collection name. This used to demand
                        // dadGroup == "Dad Jokes" and broke the moment the collections were regrouped from
                        // 12 to 7 and Dad Jokes became one source inside "Jokes & Humour" -- a rename this
                        // check has no opinion about. What it actually guards is the mapping's shape: a
                        // curated id lands somewhere curated, an unknown one lands in the fallback.
                        ok &= Check(sb, "a catalog pack groups by its curated collection, not as the user's own",
                            !string.IsNullOrEmpty(dadGroup) &&
                            !string.Equals(dadGroup, "More packs", StringComparison.Ordinal) &&
                            string.Equals(probeGroup, "More packs", StringComparison.Ordinal));

                        // Labels come from the curated name map, so a pack whose id says nothing about its
                        // contents ("rfc1925", "lwall-quotes") still reads as something meaningful.
                        string dadLabel = null, probeLabel = null;
                        foreach (ListItem li in installedCard.LoadItems())
                        {
                            if (string.Equals(li.Id, "dadjokes", StringComparison.OrdinalIgnoreCase)) dadLabel = li.Label;
                            if (string.Equals(li.Id, "probepack", StringComparison.OrdinalIgnoreCase)) probeLabel = li.Label;
                        }
                        sb.AppendLine("  dadjokes label: " + (dadLabel ?? "<none>") + " | probepack label: " + (probeLabel ?? "<none>"));
                        ok &= Check(sb, "a known pack shows its curated name, an unknown one falls back to its id",
                            string.Equals(dadLabel, "Dad Jokes", StringComparison.Ordinal) &&
                            string.Equals(probeLabel, "probepack", StringComparison.Ordinal));
                    }

                    // "Import your own…": the host supplies the picked path, the module runs it through the
                    // strict FortuneFileImporter (not a raw copy) and the lines join the live pool.
                    PaneAction import = FindAction(fortunesPane, "Import your own…");
                    ok &= Check(sb, "pane offers the import action", import != null);
                    if (import != null)
                    {
                        string mine = Path.Combine(storageDir, "my-own-pack.txt");
                        File.WriteAllText(mine, "A line I wrote myself.\n", new UTF8Encoding(false));
                        host.PickedFiles.Add(mine);
                        string importStatus = import.InvokeAsync().GetAwaiter().GetResult();
                        sb.AppendLine("  import said: " + importStatus);
                        ok &= Check(sb, "importing a user pack lands it in the fortunes folder",
                            File.Exists(Path.Combine(storageDir, "fortunes", "my-own-pack.txt")) &&
                            importStatus.IndexOf("Imported 1 pack", StringComparison.Ordinal) >= 0);

                        // Cancelling the picker must be a no-op, not an error or an empty import.
                        host.PickedFiles.Clear();
                        string cancelled = import.InvokeAsync().GetAwaiter().GetResult();
                        ok &= Check(sb, "cancelling the import picker does nothing", cancelled == "");
                    }

                    loader.ShutdownAll(s => sb.AppendLine("  " + s));
                    ok &= Check(sb, "unsubscribed all triggers on Shutdown", !host.SpawnedHasSubs && !host.LandedHasSubs && !host.PokedHasSubs);
                    // The fake's registration handles unregister on Dispose, as the host's do (RA-287, R-053's
                    // host-side half), so a Shutdown that forgot one is visible here for the first time. The live
                    // COUNTS are what make it visible with several modules loaded: the dispatch slots go empty
                    // when any module's handle is disposed, a leaked one included.
                    sb.AppendLine("  live after Shutdown: drop " + host.LiveDropResponders + ", poke " + host.LivePokeResponders + ", hotkeys " + host.LiveHotkeys);
                    ok &= Check(sb, "Shutdown disposed its drop and poke responder registrations (none remains)",
                        !host.HasDropResponder && !host.HasPokeResponder &&
                        host.LiveDropResponders == 0 && host.LivePokeResponders == 0);
                }
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                if (dataRootRedirected)
                {
                    try { Environment.SetEnvironmentVariable(AppPaths.DataRootOverrideEnvironmentVariable, previousDataRoot); } catch { }
                }
                string releaseDetail;
                if (!SelfTestScratch.TryRelease(storageDir, out releaseDetail))
                    sb.AppendLine("NOTE: scratch left for the next sweep (" + releaseDetail + ")");
                if (dataRootScratch != null && !SelfTestScratch.TryRelease(dataRootScratch, out releaseDetail))
                    sb.AppendLine("NOTE: data-root scratch left for the next sweep (" + releaseDetail + ")");
            }
            return Finish(sb, ok);
        }

        private static ListCard FindCard(OptionsPane pane, string title)
        {
            if (pane == null || pane.Lists == null) return null;
            foreach (ListCard c in pane.Lists)
                if (c != null && string.Equals(c.Title, title, StringComparison.Ordinal)) return c;
            return null;
        }

        /// <summary>
        /// A pane action by label WITHIN one named card. Prefer this over the pane-wide lookup below for
        /// any label a second card could plausibly reuse. "Select all" is exactly that case: it exists on
        /// "Fortune packs", "Available online" and "Genres", and the pane-wide search returns whichever
        /// card is declared first -- so this test's download step silently retargeted itself at the
        /// installed-packs list the moment the other two gained the button.
        /// </summary>
        private static PaneAction FindAction(OptionsPane pane, string cardTitle, string label)
        {
            ListCard card = FindCard(pane, cardTitle);
            if (card == null || card.Actions == null) return null;
            foreach (PaneAction a in card.Actions)
                if (a != null && string.Equals(a.Label, label, StringComparison.Ordinal)) return a;
            return null;
        }

        // A pane action by label, across the pane's own buttons and every list card's buttons.
        private static PaneAction FindAction(OptionsPane pane, string label)
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

        private static IModule FindModule(ModuleHost loader, string id)
        {
            foreach (IModule m in loader.Modules)
                if (m.Info != null && string.Equals(m.Info.Id, id, StringComparison.OrdinalIgnoreCase)) return m;
            return null;
        }
        /// <summary>Pull the module's built-in corpus across the ALC boundary by reflection (the base holds
        /// no reference to the module engine) and fold it into the accepted set. Returns how many lines
        /// came back, so a corpus that failed to embed shows up as a failed assertion rather than as
        /// mysteriously flaky speech checks.</summary>
        private static int AddEmbeddedTexts(object fortunesModule, HashSet<string> accepted)
        {
            if (fortunesModule == null) return 0;
            try
            {
                Type probe = fortunesModule.GetType().Assembly.GetType("DesktopAICompanion.FortunesModule.FortuneEngineProbe");
                if (probe == null) return 0;
                System.Reflection.MethodInfo texts = probe.GetMethod("EmbeddedTexts", new Type[0]);
                if (texts == null) return 0;
                var lines = texts.Invoke(null, null) as string[];
                if (lines == null) return 0;
                foreach (string line in lines) if (!string.IsNullOrEmpty(line)) accepted.Add(line);
                return lines.Length;
            }
            catch { return 0; }
        }

        /// <summary>How many welcome lines the module loaded, by reflection. -1 when the hook is gone,
        /// which fails the floor rather than reading as an empty corpus.</summary>
        private static int WelcomeCorpusCount(object fortunesModule)
        {
            if (fortunesModule == null) return -1;
            try
            {
                System.Reflection.MethodInfo count =
                    fortunesModule.GetType().GetMethod("WelcomeCorpusCount", new Type[0]);
                if (count == null) return -1;
                object value = count.Invoke(fortunesModule, null);
                return value is int ? (int)value : -1;
            }
            catch { return -1; }
        }

        /// <summary>
        /// Invoke the module's own custom-corpus cache check inside its load context and fold its
        /// individual assertions into this report. The exceptions are NOT swallowed into a pass:
        /// a probe that has gone missing or throws fails here.
        /// </summary>
        private static bool RunCustomCacheSelfTest(StringBuilder sb, object fortunesModule)
        {
            if (fortunesModule == null) return Check(sb, "custom-corpus cache: module available", false);
            Type probe = fortunesModule.GetType().Assembly
                .GetType("DesktopAICompanion.FortunesModule.FortuneEngineProbe");
            System.Reflection.MethodInfo run = probe != null
                ? probe.GetMethod("CustomCacheSelfTest",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                : null;
            if (!Check(sb, "module exposes FortuneEngineProbe.CustomCacheSelfTest", run != null))
                return false;

            var args = new object[] { null };
            bool cacheOk = false;
            try { cacheOk = (bool)run.Invoke(null, args); }
            catch (Exception ex)
            {
                sb.AppendLine("  CustomCacheSelfTest threw: " + ex.GetType().Name + ": " + ex.Message);
            }
            string detail = args[0] as string;
            if (!string.IsNullOrEmpty(detail))
                foreach (string line in detail.Replace("\r", "").Split('\n'))
                    if (line.Length > 0) sb.AppendLine("    " + line);
            return Check(sb, "the custom-corpus cache reflects add/edit/remove without a restart", cacheOk);
        }

        private static bool Check(StringBuilder sb, string name, bool cond) { sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name); return cond; }
        private static bool Finish(StringBuilder sb, bool ok)
        {
            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dp-fortunes-selftest.txt"), sb.ToString()); } catch { }
            Console.Out.Write(sb.ToString());
            return ok;
        }

        private sealed class FakeCompanion : ICompanion
        {
            public FakeCompanion(int id) { Id = id; }
            public int Id { get; private set; }
            public bool IsBusy { get { return false; } }
            public string TypeId { get { return ""; } }
        }

        /// <summary>A headless IHost that records SayAll, tracks subscription state, and captures the drop
        /// responder + the module's storage directory.</summary>
        private sealed class RecordingHost : IHost
        {
            private readonly string _storage;
            public RecordingHost(string storage) { _storage = storage; }

            // A sentinel that parses as a version and satisfies any module's MinHostVersion, so the load
            // gate stays quiet in these tests; the gate's own rules are asserted directly in
            // ModuleHostSelfTest.MinHostVersionGate.
            public string HostVersion { get { return "9999.0.0"; } }
            public bool SpeechEnabled { get { return true; } }
            public double Volume { get { return 0.5; } }
            public string OwnerName { get { return ""; } }
            public void SetOwnerName(string name) { }
            public readonly List<string> Said = new List<string>();   // all SayAll/Say calls (other modules speak too)
            // Both registration styles are captured, and FireDrop/FirePoke run whichever the module used, so
            // these assertions survive the module's migration to the pet-aware overloads instead of having to
            // be rewritten in lockstep with it.
            public Func<bool> DropResponder;
            public Func<bool> PokeResponder;
            public Func<ICompanion, bool> PetDropResponder;
            public Func<ICompanion, bool> PetPokeResponder;
            public bool HasDropResponder { get { return DropResponder != null || PetDropResponder != null; } }
            public bool HasPokeResponder { get { return PokeResponder != null || PetPokeResponder != null; } }
            public bool FireDrop(ICompanion pet)
            {
                if (PetDropResponder != null) return PetDropResponder(pet);
                return DropResponder != null && DropResponder();
            }
            public bool FirePoke(ICompanion pet)
            {
                if (PetPokeResponder != null) return PetPokeResponder(pet);
                return PokeResponder != null && PokeResponder();
            }

            public event Action<ICompanion> CompanionSpawned;
            public event Action<PokeInfo> CompanionPoked;
            public event Action<ICompanion> CompanionLanded;
            public event Action HostShutdown;

            public bool SpawnedHasSubs { get { return CompanionSpawned != null; } }
            public bool LandedHasSubs { get { return CompanionLanded != null; } }
            public bool PokedHasSubs { get { return CompanionPoked != null; } }
            public void RaiseCompanionSpawned(ICompanion p) { var h = CompanionSpawned; if (h != null) h(p); }
            public void RaiseCompanionLanded(ICompanion p) { var h = CompanionLanded; if (h != null) h(p); }
            public void RaiseCompanionPoked(PokeInfo p) { var h = CompanionPoked; if (h != null) h(p); }
            // Never called: it exists so HostShutdown counts as "used" under TreatWarningsAsErrors (CS0067).
            internal void TouchEvents() { HostShutdown?.Invoke(); }

            public void Say(ICompanion pet, string text) { Said.Add(text); }
            public void SayAll(string text) { Said.Add(text); }
            public void Say(ICompanion pet, string text, DesktopAICompanion.Modules.SpeechStyle style) { Say(pet, text); }
            public void SayAll(string text, DesktopAICompanion.Modules.SpeechStyle style) { SayAll(text); }
            public bool TryPlayAnimation(ICompanion pet, string animationName) { return true; }
            public void PlayAnimationAll(IReadOnlyList<string> animationCandidates) { }
            public ScreenContext CaptureScreenContext(ICompanion pet) { return new ScreenContext { WindowTitle = "", ProcessName = "", MonitorBounds = new PixelRect(0, 0, 1920, 1080) }; }
            // Registration handles UNREGISTER on Dispose, the way CompanionHost's Remover does (RA-287): until
            // 2026-10-01 every one of these returned a no-op, so no Shutdown assertion could see a leaked
            // responder or hotkey.
            public int LiveHotkeys;
            public IDisposable RegisterHotkey(string combo, Action onPressed) { LiveHotkeys++; return new Remover(delegate { LiveHotkeys--; }); }
            public IModuleStorage GetStorage(string moduleId) { return new DirStorage(_storage); }
            // ONE store per module id, kept across calls, the way CompanionHost's disk-backed store keeps a
            // Save for the next GetSettings. A fresh MemSettings per call lost every write, so the two
            // bulk-selection checks below passed only through the module-side staging that RA-121 removed
            // (a "Select none" that survived a Cancel); a module that saves and re-reads has to see what it
            // saved. Edited by lane burn/fortunes on 2026-09-30, outside its boundary and named in its report.
            private readonly Dictionary<string, MemSettings> _settings =
                new Dictionary<string, MemSettings>(StringComparer.OrdinalIgnoreCase);
            public IModuleSettings GetSettings(string moduleId)
            {
                MemSettings settings;
                string key = moduleId ?? "";
                if (!_settings.TryGetValue(key, out settings)) _settings[key] = settings = new MemSettings();
                return settings;
            }
            // Live registration COUNTS beside the single dispatch slots (RA-287): a slot alone cannot show a leak
            // once a second module registers the same kind, because that module's Dispose empties the shared slot
            // whether or not the first module's handle was ever disposed. LoadFrom loads every bundled module
            // against this one host and AiBrain registers a companion drop responder too, so the Shutdown check
            // below was vacuous on the slots alone; the first whole harness run graded its mutation SURVIVED.
            public int LiveDropResponders, LivePokeResponders;
            public IDisposable RegisterDropResponder(int priority, Func<bool> onDrop) { DropResponder = onDrop; LiveDropResponders++; return new Remover(delegate { DropResponder = null; LiveDropResponders--; }); }
            public IDisposable RegisterPokeResponder(string moduleId, int priority, Func<bool> onPoke) { PokeResponder = onPoke; LivePokeResponders++; return new Remover(delegate { PokeResponder = null; LivePokeResponders--; }); }
            public IDisposable RegisterCompanionDropResponder(int priority, Func<ICompanion, bool> onDrop) { PetDropResponder = onDrop; LiveDropResponders++; return new Remover(delegate { PetDropResponder = null; LiveDropResponders--; }); }
            public IDisposable RegisterCompanionPokeResponder(string moduleId, int priority, Func<ICompanion, bool> onPoke) { PetPokeResponder = onPoke; LivePokeResponders++; return new Remover(delegate { PetPokeResponder = null; LivePokeResponders--; }); }
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
            // Offline catalog stand-in: the module's browse/download flow is exercised without a network.
            public readonly List<CatalogItem> CatalogItems = new List<CatalogItem>();
            public readonly Dictionary<string, byte[]> CatalogPayloads = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            public System.Threading.Tasks.Task<IReadOnlyList<CatalogItem>> FetchCatalogItemsAsync(string kind)
            {
                return System.Threading.Tasks.Task.FromResult((IReadOnlyList<CatalogItem>)new List<CatalogItem>(CatalogItems));
            }
            public System.Threading.Tasks.Task<byte[]> DownloadCatalogItemAsync(string kind, string id)
            {
                byte[] payload;
                if (!CatalogPayloads.TryGetValue(id ?? "", out payload))
                    throw new InvalidDataException("No catalog pack with id '" + (id ?? "") + "'.");
                return System.Threading.Tasks.Task.FromResult(payload);
            }
            // Files the "Import your own…" picker should return (empty = the user cancelled).
            public readonly List<string> PickedFiles = new List<string>();
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
            public void AddTrayItems(IEnumerable<TrayItem> items) { }
            // Every loaded module contributes a pane here (aibrain/testmodule too), so keep them all and
            // let the caller pick by title rather than letting the last one loaded win.
            public readonly List<OptionsPane> Panes = new List<OptionsPane>();
            public void AddOptionsPane(OptionsPane pane) { if (pane != null) Panes.Add(pane); }
            public void PublishContext(string moduleId, string key, string valueJson) { }
            public string ReadContext(string key) { return ""; }
            public event Action<string> ContextChanged { add { } remove { } }
            public OptionsPane PaneNamed(string title)
            {
                foreach (OptionsPane p in Panes)
                    if (p != null && string.Equals(p.Title, title, StringComparison.OrdinalIgnoreCase)) return p;
                return null;
            }

            private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
            /// <summary>A registration handle whose Dispose undoes the registration, once.</summary>
            private sealed class Remover : IDisposable
            {
                private Action _undo;
                public Remover(Action undo) { _undo = undo; }
                public void Dispose() { Action undo = _undo; _undo = null; if (undo != null) undo(); }
            }
            private sealed class DirStorage : IModuleStorage
            {
                public DirStorage(string dir) { DataDirectory = dir; }
                public string DataDirectory { get; private set; }
            }
            /// <summary>A fresh store per call, EMPTY except for one seed: the Fortunes module's smart picker is
            /// OFF (RA-284; R-026's mechanism). An empty store means smart ON, and this flag's load then warmed the
            /// whole corpus on every core until Shutdown cancelled it. Nothing here asserts a smart pick, so
            /// nothing is lost. The seed is keyed by name; a module that never reads it sees an empty store.</summary>
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
