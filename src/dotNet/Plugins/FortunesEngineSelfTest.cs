using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.Plugins
{
    /// <summary>
    /// --fortunes-engine-selftest: proves the relocated fortune ENGINE (S3c-1) works inside the Fortunes
    /// module's own load context. Loads the real bundled Fortunes.dll through the AssemblyLoadContext
    /// loader, then reflectively invokes the module's public static DesktopAICompanion.FortunesModule.FortuneEngineProbe.Run
    /// (deterministic filter/pick over injected entries + the engine's own FilterSelfTest — dedup /
    /// classifier-parity / parser / custom ingestion / importer). The base itself keeps no reference to the
    /// module engine. Skips-pass if the module is absent.
    /// </summary>
    internal static class FortunesEngineSelfTest
    {
        public static bool Run()
        {
            return RunProbe("Run", "dp-fortunes-engine-selftest.txt");
        }

        /// <summary>--fortunes-smart-progress-selftest: the slow, opt-in half of the smart suite (a cold-cache
        /// warm of a 1,500-line sample). Same reflective plumbing, different probe entry point, kept out of
        /// the default gate because it runs in minutes.</summary>
        public static bool RunProgressive()
        {
            return RunProbe("RunProgressive", "dp-fortunes-smart-progress-selftest.txt");
        }

        private static bool RunProbe(string probeMethod, string reportFileName)
        {
            var sb = new StringBuilder();
            bool ok = true;
            try
            {
                string modulesRoot = Path.Combine(AppContext.BaseDirectory, "modules");
                if (!Directory.Exists(Path.Combine(modulesRoot, "fortunes")))
                {
                    sb.AppendLine("SKIP: no bundled fortunes module at " + Path.Combine(modulesRoot, "fortunes"));
                    return Finish(sb, true, reportFileName);
                }

                var host = new RecordingHost();
                using (var loader = new ModuleHost())
                {
                    int loaded = loader.LoadFrom(modulesRoot, host, s => sb.AppendLine("  " + s));
                    ok &= Check(sb, "at least one module loaded", loaded >= 1);
                    IModule fortunes = FindModule(loader, "fortunes");
                    ok &= Check(sb, "fortunes module reports its id", fortunes != null);

                    if (fortunes != null)
                    {
                        Type probe = fortunes.GetType().Assembly.GetType("DesktopAICompanion.FortunesModule.FortuneEngineProbe");
                        ok &= Check(sb, "module exposes FortuneEngineProbe", probe != null);
                        MethodInfo run = probe != null ? probe.GetMethod(probeMethod, BindingFlags.Public | BindingFlags.Static) : null;
                        ok &= Check(sb, "FortuneEngineProbe exposes " + probeMethod, run != null);
                        if (run != null)
                        {
                            var args = new object[] { null };
                            bool engineOk = false;
                            try { engineOk = (bool)run.Invoke(null, args); }
                            catch (Exception ex) { sb.AppendLine("  FortuneEngineProbe." + probeMethod + " threw: " + ex.GetType().Name + ": " + ex.Message); }
                            string detail = args[0] as string;
                            if (!string.IsNullOrEmpty(detail))
                                foreach (string line in detail.Replace("\r", "").Split('\n'))
                                    if (line.Length > 0) sb.AppendLine("    " + line);
                            ok &= Check(sb, "engine self-test passed inside the module's load context", engineOk);
                        }
                    }
                }
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            return Finish(sb, ok, reportFileName);
        }

        private static IModule FindModule(ModuleHost loader, string id)
        {
            foreach (IModule m in loader.Modules)
                if (m.Info != null && string.Equals(m.Info.Id, id, StringComparison.OrdinalIgnoreCase)) return m;
            return null;
        }
        private static bool Check(StringBuilder sb, string name, bool cond) { sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name); return cond; }
        private static bool Finish(StringBuilder sb, bool ok, string reportFileName)
        {
            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), reportFileName), sb.ToString()); } catch { }
            Console.Out.Write(sb.ToString());
            return ok;
        }

        /// <summary>Minimal headless IHost so ModuleHost can load the module (the engine probe is static and
        /// does not depend on Init, but LoadFrom calls Init(host)).</summary>
        private sealed class RecordingHost : IHost
        {
            // A sentinel that parses as a version and satisfies any module's MinHostVersion, so the load
            // gate stays quiet in these tests; the gate's own rules are asserted directly in
            // ModuleHostSelfTest.MinHostVersionGate.
            public string HostVersion { get { return "9999.0.0"; } }
            public bool SpeechEnabled { get { return true; } }
            public double Volume { get { return 0.5; } }
            public string OwnerName { get { return ""; } }
            public void SetOwnerName(string name) { }

            public event Action<ICompanion> CompanionSpawned;
            public event Action<PokeInfo> CompanionPoked;
            public event Action<ICompanion> CompanionLanded;
            public event Action HostShutdown;
            // Never called: it exists so the events count as "used" under TreatWarningsAsErrors (CS0067).
            internal void TouchEvents() { CompanionSpawned?.Invoke(null); CompanionPoked?.Invoke(null); CompanionLanded?.Invoke(null); HostShutdown?.Invoke(); }

            public void Say(ICompanion pet, string text) { }
            public void SayAll(string text) { }
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
            public bool PlaySound(string moduleId, byte[] audio, double volume) { return false; }
            public bool StopSound(string moduleId) { return false; }
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
            public void AddTrayItems(IEnumerable<TrayItem> items) { }
            public void AddOptionsPane(OptionsPane pane) { }
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
