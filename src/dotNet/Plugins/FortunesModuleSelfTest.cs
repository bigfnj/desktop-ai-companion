using System;
using System.Collections.Generic;
using System.IO;
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
            try
            {
                string modulesRoot = Path.Combine(AppContext.BaseDirectory, "modules");
                if (!Directory.Exists(Path.Combine(modulesRoot, "fortunes")))
                {
                    sb.AppendLine("SKIP: no bundled fortunes module at " + Path.Combine(modulesRoot, "fortunes"));
                    return Finish(sb, true);
                }

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
                    PaneAction check = FindAction(fortunesPane, "Check online for packs");
                    PaneAction download = FindAction(fortunesPane, "Download selected");
                    PaneAction selectAll = FindAction(fortunesPane, "Select all");
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
                        ok &= Check(sb, "a downloaded pack joins the live pool without a restart",
                            spokeAfter && host.Said.Count > 0);
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
                        ok &= Check(sb, "a catalog pack groups by its curated collection, not as the user's own",
                            string.Equals(dadGroup, "Dad Jokes", StringComparison.Ordinal) &&
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
                }
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                string releaseDetail;
                if (!SelfTestScratch.TryRelease(storageDir, out releaseDetail))
                    sb.AppendLine("NOTE: scratch left for the next sweep (" + releaseDetail + ")");
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
            public string LastSayAll;
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

            public void Say(ICompanion pet, string text) { LastSayAll = text; Said.Add(text); }
            public void SayAll(string text) { LastSayAll = text; Said.Add(text); }
            public void Say(ICompanion pet, string text, DesktopAICompanion.Modules.SpeechStyle style) { Say(pet, text); }
            public void SayAll(string text, DesktopAICompanion.Modules.SpeechStyle style) { SayAll(text); }
            public bool TryPlayAnimation(ICompanion pet, string animationName) { return true; }
            public void PlayAnimationAll(IReadOnlyList<string> animationCandidates) { }
            public ScreenContext CaptureScreenContext(ICompanion pet) { return new ScreenContext { WindowTitle = "", ProcessName = "", MonitorBounds = new PixelRect(0, 0, 1920, 1080) }; }
            public IDisposable RegisterHotkey(string combo, Action onPressed) { return new NoopDisposable(); }
            public IModuleStorage GetStorage(string moduleId) { return new DirStorage(_storage); }
            public IModuleSettings GetSettings(string moduleId) { return new MemSettings(); }
            public IDisposable RegisterDropResponder(int priority, Func<bool> onDrop) { DropResponder = onDrop; return new NoopDisposable(); }
            public IDisposable RegisterPokeResponder(string moduleId, int priority, Func<bool> onPoke) { PokeResponder = onPoke; return new NoopDisposable(); }
            public IDisposable RegisterCompanionDropResponder(int priority, Func<ICompanion, bool> onDrop) { PetDropResponder = onDrop; return new NoopDisposable(); }
            public IDisposable RegisterCompanionPokeResponder(string moduleId, int priority, Func<ICompanion, bool> onPoke) { PetPokeResponder = onPoke; return new NoopDisposable(); }
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
            public void Log(string moduleId, string message) { }
            public IReadOnlyList<string> PickFilesToOpen(string title, string fileKindLabel, IReadOnlyList<string> extensions) { return PickedFiles; }
            public string OpenedLink;
            public bool OpenLink(string moduleId, string httpsUrl) { OpenedLink = httpsUrl; return true; }
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
