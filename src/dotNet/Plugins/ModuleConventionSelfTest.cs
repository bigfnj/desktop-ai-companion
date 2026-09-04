using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;   // Application.ProductVersion — the version the MinHostVersion gate uses
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.Plugins
{
    /// <summary>
    /// --module-selftest=&lt;id&gt;: run whatever self-test a module carries, without the base knowing anything
    /// about that module.
    ///
    /// The three modules that predate the SDK each have a bespoke class in this folder, because each asserts
    /// something specific about how it integrates with the host (Fortunes' corpus, AiBrain's OCR encoding,
    /// Companion Studio's validator agreement). Those stay. What was missing is the ordinary case: a new module
    /// with ordinary assertions had to edit Program.cs to be testable at all, which is a poor first
    /// experience and a step people forget.
    ///
    /// So this is convention over registration. A module exposes
    /// <c>public static bool SelfTest(out string detail)</c> — the shape the template scaffolds, built on
    /// ModuleKit's SelfTestProbe — and the base finds it by reflection, exactly as it reaches every other
    /// module member. The module is first loaded through the REAL <see cref="ModuleHost"/>, so a pass also
    /// proves the loader accepts it, the MinHostVersion gate lets it through, and Init ran.
    ///
    /// Still add the flag to tests\run-gate.ps1 and .github\workflows\build.yml — those are data, and the
    /// gate deliberately fails on a self-test that did not actually run.
    /// </summary>
    internal static class ModuleConventionSelfTest
    {
        internal const string FlagPrefix = "--module-selftest=";

        /// <summary>The module id from "--module-selftest=&lt;id&gt;", or null when the flag is absent.</summary>
        internal static string FindRequestedId(string[] args)
        {
            if (args == null) return null;
            foreach (string arg in args)
            {
                if (arg == null || !arg.StartsWith(FlagPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                string id = arg.Substring(FlagPrefix.Length).Trim().Trim('"');
                return id.Length == 0 ? null : id;
            }
            return null;
        }

        public static bool Run(string moduleId)
        {
            var sb = new StringBuilder();
            bool ok = true;
            string tempRoot = null;
            try
            {
                if (string.IsNullOrWhiteSpace(moduleId) || !SecureDownload.IsSafeId(moduleId))
                {
                    sb.AppendLine("FAIL: '" + moduleId + "' is not a usable module id.");
                    return Finish(moduleId, sb, false);
                }

                string bundled = Path.Combine(AppContext.BaseDirectory, "modules", moduleId);
                if (!Directory.Exists(bundled))
                {
                    sb.AppendLine("SKIP: no bundled module at " + bundled);
                    return Finish(moduleId, sb, true);
                }

                // Isolate, so the recording host reflects this module's Init alone and a sibling module's
                // failure cannot be misread as this one's.
                tempRoot = SelfTestScratch.Create("module");
                string dest = Path.Combine(tempRoot, moduleId);
                Directory.CreateDirectory(dest);
                foreach (string file in Directory.GetFiles(bundled))
                    File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);

                var host = new ConventionHost();
                using (var loader = new ModuleHost())
                {
                    int loaded = loader.LoadFrom(tempRoot, host, s => sb.AppendLine("  " + s));
                    ok &= Check(sb, "the real loader accepted the module", loaded == 1);
                    if (loaded != 1) return Finish(moduleId, sb, false);

                    IModule module = null;
                    foreach (IModule candidate in loader.Modules)
                        if (candidate != null && candidate.Info != null &&
                            string.Equals(candidate.Info.Id, moduleId, StringComparison.OrdinalIgnoreCase))
                            module = candidate;
                    ok &= Check(sb, "the module reports the id its folder claims", module != null);
                    if (module == null) return Finish(moduleId, sb, false);

                    // Info hygiene every module owes the catalog and the update check.
                    ok &= Check(sb, "declares a name", !string.IsNullOrWhiteSpace(module.Info.Name));
                    Version parsed;
                    ok &= Check(sb, "declares a parseable Version (the update check compares it)",
                        Version.TryParse(module.Info.Version, out parsed));

                    ok &= RunModuleSelfTest(sb, module.GetType().Assembly, moduleId);

                    loader.ShutdownAll(s => sb.AppendLine("  " + s));
                }
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                // Expected to fail here: the collectible ALC unloads asynchronously, so the module DLL is
                // still mapped. Say so rather than swallowing it; the next run's sweep collects the directory.
                string releaseDetail;
                if (!SelfTestScratch.TryRelease(tempRoot, out releaseDetail))
                    sb.AppendLine("NOTE: scratch left for the next sweep (" + releaseDetail + ")");
            }
            return Finish(moduleId, sb, ok);
        }

        /// <summary>Find and run the module's own <c>public static bool SelfTest(out string)</c>. Absent is a
        /// FAILURE rather than a skip: a module with no self-test is exactly what this flag exists to catch,
        /// and a silent pass here would be indistinguishable from a real one.</summary>
        private static bool RunModuleSelfTest(StringBuilder sb, Assembly moduleAssembly, string moduleId)
        {
            MethodInfo entry = null;
            foreach (Type type in moduleAssembly.GetTypes())
            {
                MethodInfo candidate = type.GetMethod("SelfTest",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                if (candidate == null) continue;
                ParameterInfo[] parameters = candidate.GetParameters();
                if (candidate.ReturnType != typeof(bool)) continue;
                if (parameters.Length != 1 || !parameters[0].IsOut) continue;
                entry = candidate;
                break;
            }

            if (!Check(sb, "the module exposes static bool SelfTest(out string detail)", entry != null))
            {
                sb.AppendLine("  Add one (the template scaffolds it) so this module is testable.");
                return false;
            }

            object[] callArgs = new object[] { null };
            bool result;
            try { result = (bool)entry.Invoke(null, callArgs); }
            catch (TargetInvocationException ex)
            {
                sb.AppendLine("EXC: the module's SelfTest threw: " +
                    (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
                return false;
            }

            string detail = callArgs[0] as string;
            if (!string.IsNullOrEmpty(detail))
                foreach (string line in detail.Replace("\r\n", "\n").Split('\n'))
                    if (line.Length > 0) sb.AppendLine("  [" + moduleId + "] " + line);

            // The module reports its own verdict; a module that passes nothing still has to say RESULT=PASS.
            return Check(sb, "the module's own self-test passed", result);
        }

        private static bool Check(StringBuilder sb, string name, bool cond)
        {
            sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name);
            return cond;
        }

        private static bool Finish(string moduleId, StringBuilder sb, bool ok)
        {
            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            string safe = string.IsNullOrWhiteSpace(moduleId) ? "unknown" : moduleId;
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "dp-module-" + safe + "-selftest.txt"), sb.ToString());
            }
            catch { }
            Console.Out.Write(sb.ToString());
            return ok;
        }

        private sealed class FakeCompanion : ICompanion
        {
            public int Id { get { return 1; } }
            public bool IsBusy { get { return false; } }
            public string TypeId { get { return ""; } }
        }

        /// <summary>
        /// A headless host, only as capable as loading a module requires. Deliberately thin: the module's own
        /// SelfTest builds a richer host from ModuleKit (which travels with the module), so duplicating that
        /// here would put a second, drifting copy in the base.
        /// </summary>
        private sealed class ConventionHost : IHost
        {
            public string HostVersion { get { return Application.ProductVersion; } }
            public bool SpeechEnabled { get { return true; } }
            public double Volume { get { return 0.5; } }
            public string OwnerName { get { return ""; } }
            public void SetOwnerName(string name) { }

            public event Action<ICompanion> CompanionSpawned;
            public event Action<PokeInfo> CompanionPoked;
            public event Action<ICompanion> CompanionLanded;
            public event Action HostShutdown;
            // Never called: it exists so the declared events count as used under warnings-as-errors (CS0067).
            internal void TouchEvents()
            {
                CompanionSpawned?.Invoke(new FakeCompanion());
                CompanionPoked?.Invoke(null);
                CompanionLanded?.Invoke(null);
                HostShutdown?.Invoke();
            }

            public void Say(ICompanion pet, string text) { }
            public void SayAll(string text) { }
            public void Say(ICompanion pet, string text, DesktopAICompanion.Modules.SpeechStyle style) { Say(pet, text); }
            public void SayAll(string text, DesktopAICompanion.Modules.SpeechStyle style) { SayAll(text); }
            public bool TryPlayAnimation(ICompanion pet, string animationName) { return true; }
            public void PlayAnimationAll(IReadOnlyList<string> animationCandidates) { }
            public ScreenContext CaptureScreenContext(ICompanion pet)
            {
                return new ScreenContext
                {
                    WindowTitle = "",
                    ProcessName = "",
                    MonitorBounds = new PixelRect(0, 0, 1920, 1080),
                };
            }
            public IDisposable RegisterHotkey(string combo, Action onPressed) { return new Noop(); }
            public IModuleStorage GetStorage(string moduleId) { return null; }
            public IModuleSettings GetSettings(string moduleId) { return null; }
            public IDisposable RegisterDropResponder(int priority, Func<bool> onDrop) { return new Noop(); }
            public IDisposable RegisterPokeResponder(string moduleId, int priority, Func<bool> onPoke) { return new Noop(); }
            public IDisposable RegisterCompanionDropResponder(int priority, Func<ICompanion, bool> onDrop) { return new Noop(); }
            public IDisposable RegisterCompanionPokeResponder(string moduleId, int priority, Func<ICompanion, bool> onPoke) { return new Noop(); }
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
            public IDisposable RegisterSpeechResponder(string moduleId, int priority, Func<SpeechRequest, bool> onSpeech) { return new Noop(); }
            public System.Threading.Tasks.Task<IReadOnlyList<CatalogItem>> FetchCatalogItemsAsync(string kind)
            {
                return System.Threading.Tasks.Task.FromResult((IReadOnlyList<CatalogItem>)new List<CatalogItem>());
            }
            public System.Threading.Tasks.Task<byte[]> DownloadCatalogItemAsync(string kind, string id)
            {
                return System.Threading.Tasks.Task.FromResult(new byte[0]);
            }
            public ICompanionManager GetCompanionManager(string moduleId) { return new DenyingCompanionManager(); }
            public bool IsDarkTheme { get { return false; } }
            public void Log(string moduleId, string message) { }
            public IReadOnlyList<string> PickFilesToOpen(string title, string fileKindLabel, IReadOnlyList<string> extensions)
            {
                return new List<string>();
            }
            public bool OpenLink(string moduleId, string httpsUrl) { return false; }
            public void AddTrayItems(IEnumerable<TrayItem> items) { }
            public void AddOptionsPane(OptionsPane pane) { }
            public void PublishContext(string moduleId, string key, string valueJson) { }
            public string ReadContext(string key) { return ""; }
            public event Action<string> ContextChanged { add { } remove { } }

            private sealed class Noop : IDisposable { public void Dispose() { } }
        }
    }
}
