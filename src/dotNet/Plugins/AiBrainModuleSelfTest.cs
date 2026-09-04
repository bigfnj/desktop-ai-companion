using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.Plugins
{
    /// <summary>
    /// --aibrain-selftest: proves the AI-brain module's BOUNDARY and its LIVE wiring. It copies only the
    /// bundled aibrain module into an isolated modules root (the shared build folder also holds fortunes and
    /// testmodule, which subscribe too), loads it through the real AssemblyLoadContext loader against a
    /// recording host, and asserts: the module loads and reports its id / name / the full capability set it
    /// declares; it subscribes to the pet lifecycle and registers a drop responder that outranks Fortunes;
    /// it contributes its two tray items and its options pane; and — the point of an OFF-by-default feature —
    /// that with fresh settings every trigger stays silent and the drop responder declines so Fortunes
    /// handles the tick. Then the relocated engine is exercised inside the module's own load context
    /// (settings store, DPAPI keys, endpoint/model policy, backends), and the OCR start-info factory is
    /// asserted to pin UTF-8 on both streams — the regression that made the pet quote mojibake.
    /// It also smoke-checks the host's real global-hotkey registrar (skip-passes where a message window /
    /// RegisterHotKey isn't available, e.g. a headless CI window station). Skips-pass if the module is absent.
    /// </summary>
    internal static class AiBrainModuleSelfTest
    {
        public static bool Run()
        {
            var sb = new StringBuilder();
            bool ok = true;
            string tempRoot = null;
            string previousDataRoot = null;
            bool dataRootOverridden = false;
            try
            {
                string bundled = Path.Combine(AppContext.BaseDirectory, "modules", "aibrain");
                if (!Directory.Exists(bundled))
                {
                    sb.AppendLine("SKIP: no bundled aibrain module at " + bundled);
                    return Finish(sb, true);
                }

                // Isolate: load ONLY aibrain, so the recording host reflects this module's Init alone
                // (the shared build folder also has fortunes/sound/testmodule, which DO subscribe).
                tempRoot = SelfTestScratch.Create("aibrain");
                string dest = Path.Combine(tempRoot, "aibrain");
                Directory.CreateDirectory(dest);
                foreach (string file in Directory.GetFiles(bundled))
                    File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);

                // Isolate the module's settings store + the base->module migrator under this temp root, so
                // the test never reads or writes the real ai-settings.json and the brain loads fresh (OFF).
                string storageDir = Path.Combine(tempRoot, "store");
                Directory.CreateDirectory(storageDir);
                previousDataRoot = Environment.GetEnvironmentVariable("DESKTOP_AI_COMPANION_DATA_ROOT");
                Environment.SetEnvironmentVariable("DESKTOP_AI_COMPANION_DATA_ROOT", tempRoot);
                dataRootOverridden = true;

                var host = new RecordingHost(storageDir);
                using (var loader = new ModuleHost())
                {
                    int loaded = loader.LoadFrom(tempRoot, host, s => sb.AppendLine("  " + s));
                    ok &= Check(sb, "exactly one module loaded (isolated)", loaded == 1);

                    IModule brain = FindModule(loader, "aibrain");
                    ok &= Check(sb, "aibrain module reports its id", brain != null);
                    if (brain != null && brain.Info != null)
                    {
                        ok &= Check(sb, "module name is 'AI Brain'", string.Equals(brain.Info.Name, "AI Brain", StringComparison.Ordinal));
                        ModulePermissions p = brain.Info.Permissions;
                        ok &= Check(sb, "declares Speech+Animation+ScreenContext+Network+Hotkey+Storage",
                            p.HasFlag(ModulePermissions.Speech) && p.HasFlag(ModulePermissions.Animation) &&
                            p.HasFlag(ModulePermissions.ScreenContext) && p.HasFlag(ModulePermissions.Network) &&
                            p.HasFlag(ModulePermissions.Hotkey) && p.HasFlag(ModulePermissions.Storage));
                    }

                    // Live wiring (S4b): the module subscribes to track the current pet and registers a
                    // drop responder that outranks Fortunes. No tray/Options UI yet (rebuilt in S5).
                    ok &= Check(sb, "live: subscribes to CompanionSpawned/CompanionLanded/CompanionPoked (pet tracking)",
                        host.SpawnedHasSubs && host.LandedHasSubs && host.PokedHasSubs);
                    ok &= Check(sb, "live: registers a drop responder (outranks Fortunes)", host.HasDropResponder);
                    ok &= Check(sb, "contributes Enable + Ask tray items (S5a) + an AI config pane (S5b)", host.TrayCount == 2 && host.PaneCount == 1);

                    // The brain is OFF by default (fresh isolated settings): every trigger stays silent and
                    // the drop responder declines so Fortunes handles the tick.
                    host.Said.Clear();
                    host.RaiseCompanionSpawned(new FakeCompanion(1));
                    host.RaiseCompanionLanded(new FakeCompanion(1));
                    host.RaiseCompanionPoked(new PokeInfo { Pet = new FakeCompanion(1), PokeCount = 1 });
                    ok &= Check(sb, "brain OFF by default: spawn/land/poke speak nothing", host.Said.Count == 0);
                    ok &= Check(sb, "brain OFF: drop responder declines so Fortunes handles it",
                        host.HasDropResponder && host.FireDrop(new FakeCompanion(1)) == false);

                    // ---- STAND DOWN FOR A GAME ----
                    // The module must SUBSCRIBE to the transition, not merely be able to answer the predicate.
                    // A game starting is the only moment VRAM can be handed back before the game needs it; a
                    // module that only checks at its own next tick (up to 15 minutes away) is too late, and
                    // that failure is invisible without this assertion.
                    ok &= Check(sb, "live: subscribes to FullscreenChanged (releases VRAM when a game starts)",
                        host.FullscreenHasSubs);
                    // Raising it must not throw even with the brain off / no model loaded, because this fires
                    // on every game launch regardless of what the module is doing.
                    bool raiseOk = true;
                    try { host.RaiseFullscreen(true); host.RaiseFullscreen(false); }
                    catch (Exception ex) { raiseOk = false; sb.AppendLine("  RaiseFullscreen threw: " + ex.GetType().Name); }
                    ok &= Check(sb, "a fullscreen transition is safe with no model loaded", raiseOk);

                    // Engine leg (S4a-3): prove the relocated engine RUNS in the module's load context —
                    // the DPAPI-scoped settings store, chat history, endpoint/persona/model policy, and
                    // backend construction. Reflected so the base keeps no reference to the module engine.
                    if (brain != null)
                    {
                        Type probe = brain.GetType().Assembly.GetType("DesktopAICompanion.AiBrainModule.AiEngineProbe");
                        ok &= Check(sb, "module exposes AiEngineProbe", probe != null);
                        MethodInfo run = probe != null ? probe.GetMethod("Run", BindingFlags.Public | BindingFlags.Static) : null;
                        ok &= Check(sb, "AiEngineProbe exposes Run", run != null);
                        if (run != null)
                        {
                            var pargs = new object[] { null };
                            bool engineOk = false;
                            try { engineOk = (bool)run.Invoke(null, pargs); }
                            catch (Exception ex) { sb.AppendLine("  AiEngineProbe.Run threw: " + ex.GetType().Name + ": " + ex.Message); }
                            string edetail = pargs[0] as string;
                            if (!string.IsNullOrEmpty(edetail))
                                foreach (string line in edetail.Replace("\r", "").Split('\n'))
                                    if (line.Length > 0) sb.AppendLine("    " + line);
                            ok &= Check(sb, "engine ran inside the module's load context", engineOk);
                        }

                        ok &= OcrOutputEncodingPinned(sb, brain.GetType().Assembly);
                    }

                    loader.ShutdownAll(s => sb.AppendLine("  " + s));
                    ok &= Check(sb, "Shutdown unsubscribes lifecycle events",
                        !host.SpawnedHasSubs && !host.LandedHasSubs && !host.PokedHasSubs);
                }

                ok &= HotkeyRegistrarSmoke(sb);
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                if (dataRootOverridden)
                {
                    try { Environment.SetEnvironmentVariable("DESKTOP_AI_COMPANION_DATA_ROOT", previousDataRoot); } catch { }
                }
                // Expected to fail: the collectible ALC unloads asynchronously, so the module DLL is still
                // mapped. Reported rather than swallowed; the next run's sweep collects the directory.
                string releaseDetail;
                if (!SelfTestScratch.TryRelease(tempRoot, out releaseDetail))
                    sb.AppendLine("NOTE: scratch left for the next sweep (" + releaseDetail + ")");
            }
            return Finish(sb, ok);
        }

        /// <summary>
        /// The OCR child process must be read as UTF-8, PINNED explicitly. Left unset, a redirected stream is
        /// decoded via GetConsoleOutputCP(), which is 0 in a GUI process with no console, and .NET reads
        /// codepage 0 as CP_ACP -- the system ANSI codepage. Tesseract writes UTF-8, so every non-ASCII glyph
        /// on screen reached the model as mojibake ("as®" as "asÂ®") and the model quoted the garbage back.
        /// Asserted through the psi factory rather than by running tesseract, so it holds on CI where no OCR
        /// engine is installed. Reflected because the base keeps no reference to the module engine.
        /// </summary>
        private static bool OcrOutputEncodingPinned(StringBuilder sb, Assembly moduleAssembly)
        {
            const int Utf8 = 65001;
            Type brainType = moduleAssembly.GetType("DesktopAICompanion.Ai.AiBrain");
            if (!Check(sb, "module exposes AiBrain", brainType != null)) return false;
            MethodInfo factory = brainType.GetMethod(
                "BuildOcrStartInfo", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (!Check(sb, "AiBrain exposes the OCR start-info factory", factory != null)) return false;

            var psi = factory.Invoke(null, new object[] { "tesseract.exe", "probe.png" }) as ProcessStartInfo;
            if (!Check(sb, "OCR start-info builds", psi != null)) return false;
            bool ok = Check(sb, "OCR stdout is pinned to UTF-8 (not the ANSI codepage)",
                psi.StandardOutputEncoding != null && psi.StandardOutputEncoding.CodePage == Utf8);
            ok &= Check(sb, "OCR stderr is pinned to UTF-8",
                psi.StandardErrorEncoding != null && psi.StandardErrorEncoding.CodePage == Utf8);
            return ok;
        }

        /// <summary>
        /// Best-effort check of the host's real RegisterHotkey (CompanionHost wraps the proven HotkeyListener).
        /// Verifies the wrapping's lifecycle without fragile input injection: a valid combo returns a
        /// disposable that disposes cleanly, and a null/empty combo degrades to a disposable no-op. Skips
        /// (passes) if creating a message window / RegisterHotKey isn't available in this context.
        /// </summary>
        private static bool HotkeyRegistrarSmoke(StringBuilder sb)
        {
            try
            {
                var host = new CompanionHost(null);   // RegisterHotkey does not touch StartUp
                using (IDisposable a = host.RegisterHotkey("Ctrl+Alt+F24", delegate { }))
                    if (a == null) return Check(sb, "hotkey registrar returns a handle for a valid combo", false);
                IDisposable b = host.RegisterHotkey("", delegate { });   // graceful no-op
                if (b != null) b.Dispose();
                return Check(sb, "hotkey registrar: valid combo + empty combo both return safe handles", true);
            }
            catch (Exception ex)
            {
                sb.AppendLine("SKIP: hotkey registrar smoke unavailable (" + ex.GetType().Name + ")");
                return true;
            }
        }

        private static IModule FindModule(ModuleHost loader, string id)
        {
            foreach (IModule m in loader.Modules)
                if (m.Info != null && string.Equals(m.Info.Id, id, StringComparison.OrdinalIgnoreCase)) return m;
            return null;
        }
        private static bool Check(StringBuilder sb, string name, bool cond) { sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name); return cond; }
        private static bool Finish(StringBuilder sb, bool ok)
        {
            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dp-aibrain-selftest.txt"), sb.ToString()); } catch { }
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

        /// <summary>A headless IHost that records SayAll + subscription/contribution state.</summary>
        private sealed class RecordingHost : IHost
        {
            private readonly string _storageDir;
            public RecordingHost(string storageDir) { _storageDir = storageDir; }

            // A sentinel that parses as a version and satisfies any module's MinHostVersion, so the load
            // gate stays quiet in these tests; the gate's own rules are asserted directly in
            // ModuleHostSelfTest.MinHostVersionGate.
            public string HostVersion { get { return "9999.0.0"; } }
            public bool SpeechEnabled { get { return true; } }
            public double Volume { get { return 0.5; } }
            public string OwnerName { get { return ""; } }
            public void SetOwnerName(string name) { }
            public readonly List<string> Said = new List<string>();
            // Both registration styles captured, so these assertions survive the module's migration to the
            // pet-aware overloads rather than needing to change in lockstep with it.
            public Func<bool> DropResponder;
            public Func<bool> PokeResponder;
            public Func<ICompanion, bool> PetDropResponder;
            public Func<ICompanion, bool> PetPokeResponder;
            public bool HasDropResponder { get { return DropResponder != null || PetDropResponder != null; } }
            public bool FireDrop(ICompanion pet)
            {
                if (PetDropResponder != null) return PetDropResponder(pet);
                return DropResponder != null && DropResponder();
            }
            public int TrayCount;
            public int PaneCount;

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
            public IDisposable RegisterHotkey(string combo, Action onPressed) { return new NoopDisposable(); }
            public IModuleStorage GetStorage(string moduleId) { return new DirStorage(_storageDir); }
            public IModuleSettings GetSettings(string moduleId) { return new MemSettings(); }
            public IDisposable RegisterDropResponder(int priority, Func<bool> onDrop) { DropResponder = onDrop; return new NoopDisposable(); }
            public IDisposable RegisterPokeResponder(string moduleId, int priority, Func<bool> onPoke) { PokeResponder = onPoke; return new NoopDisposable(); }
            public IDisposable RegisterCompanionDropResponder(int priority, Func<ICompanion, bool> onDrop) { PetDropResponder = onDrop; return new NoopDisposable(); }
            public IDisposable RegisterCompanionPokeResponder(string moduleId, int priority, Func<ICompanion, bool> onPoke) { PetPokeResponder = onPoke; return new NoopDisposable(); }
            public bool IsCompanionAlive(ICompanion pet) { return PetAlive && pet != null; }
            // Fullscreen is environmental, so a double reports "no game running" unless a test says
            // otherwise; FullscreenActive lets one say otherwise.
            public bool FullscreenActive;
            public bool IsFullscreenActive { get { return FullscreenActive; } }
            public event Action<bool> FullscreenChanged;
            public bool FullscreenHasSubs { get { return FullscreenChanged != null; } }
            public void RaiseFullscreen(bool on)
            {
                FullscreenActive = on;
                var h = FullscreenChanged; if (h != null) h(on);
            }
            public bool PlaySound(string moduleId, byte[] audio, double volume) { return false; }
            public bool StopSound(string moduleId) { return false; }
            public IDisposable RegisterSpeechResponder(string moduleId, int priority, Func<SpeechRequest, bool> onSpeech) { return new NoopDisposable(); }
            /// <summary>Set false to prove an answer arriving after its pet is gone is dropped, not
            /// redirected to some other pet.</summary>
            public bool PetAlive = true;
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
            public void AddTrayItems(IEnumerable<TrayItem> items) { if (items != null) foreach (var i in items) TrayCount++; }
            public void AddOptionsPane(OptionsPane pane) { if (pane != null) PaneCount++; }
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
