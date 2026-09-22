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

                    ok &= RunModuleSelfTest(sb, module.GetType(), moduleId);

                    // THE CONVENTION, ENFORCED BY THE HOST rather than by each module choosing to.
                    // The check has lived in ModuleKit since 2026-09-17 and exactly one module
                    // called it, so five others could break the convention freely. The tray is
                    // shared by the host and six modules: an icon-less row reads as a rendering
                    // bug beside its neighbours, and two rows with one glyph look like duplicates.
                    //
                    // Uses the `out reason` overload, not CheckTrayIcons: that one takes
                    // ModuleKit's SelfTestProbe, and this file reports through a StringBuilder.
                    // A bare false in a six-module menu does not say which row broke it.
                    //
                    // SCOPE, honestly: --module-selftest runs for four of the seven in-tree
                    // modules (Invoke-SelfTests.ps1), so this covers those four plus every
                    // out-of-tree module. Submenu icons are NOT covered -- TrayConventions walks
                    // only the top level, and AgentFlow sets IconPng on a child -- because
                    // recursing is a ModuleKit change that would stale all seven published
                    // payloads.
                    string trayReason;
                    bool trayOk = DesktopAICompanion.ModuleKit.Testing.TrayConventions
                        .EveryTrayEntryHasAUniqueIcon(host.TrayItems, out trayReason);
                    ok &= Check(sb, "every tray entry it registered has its own unique icon"
                                    + (trayOk ? "" : " -- " + trayReason), trayOk);

                    loader.ShutdownAll(s => sb.AppendLine("  " + s));

                    // Nothing asserted anything about Shutdown until 2026-09-17: ShutdownAll was
                    // called and its result discarded. A module that subscribes in Init and never
                    // unsubscribes keeps being invoked on an instance whose Init state is gone --
                    // which is the defect Remembrance actually shipped with, on HostShutdown, and
                    // which ReminderModule's own comment warns about.
                    //
                    // Unsubscribe assertions did exist, but only in three bespoke host-side
                    // self-tests each carrying its own private fake host, so reminder,
                    // remembrance, blinkingled and agentflow had none. One assertion here covers
                    // every module the real loader can load, including out-of-tree ones.
                    string stillSubscribed = "";
                    if (host.CompanionSpawnedHasSubs) stillSubscribed += " CompanionSpawned";
                    if (host.CompanionPokedHasSubs) stillSubscribed += " CompanionPoked";
                    if (host.CompanionLandedHasSubs) stillSubscribed += " CompanionLanded";
                    if (host.HostShutdownHasSubs) stillSubscribed += " HostShutdown";
                    // Six of six. These two were the blind spots: ContextChanged threw its
                    // subscribers away, and FullscreenChanged kept them but nothing looked.
                    if (host.ContextChangedHasSubs) stillSubscribed += " ContextChanged";
                    if (host.FullscreenChangedHasSubs) stillSubscribed += " FullscreenChanged";
                    ok &= Check(sb,
                        "Shutdown unsubscribed every host event it subscribed to"
                        + (stillSubscribed.Length > 0 ? " -- still attached:" + stillSubscribed : ""),
                        stillSubscribed.Length == 0);
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

        /// <summary>
        /// Resolve the module's self-test entry point, preferring the identity we already have.
        ///
        /// The previous version walked <c>Assembly.GetTypes()</c> and took the FIRST match, which
        /// had three sharp edges at once. GetTypes() order is not specified, so the winner could
        /// change between rebuilds; <c>BindingFlags.NonPublic</c> was included, so a private
        /// helper could outrank the module's real entry point; and <c>parameters[0].IsOut</c>
        /// alone would have accepted <c>out int</c>. Reminder's six helper methods were renamed to
        /// SelfCheck to dodge this, which is a module working around a host defect.
        ///
        /// The loader has already resolved the module instance by the time this runs, so the
        /// unambiguous answer is free: look at the module's own type first, then at other
        /// <see cref="IModule"/> implementations, and only then scan -- deterministically, and
        /// FAILING on more than one match. "Two methods claim to be this module's self-test, here
        /// are their names" beats a coin flip, and it is the only version that holds for a
        /// third-party module this repo does not own.
        /// </summary>
        private static bool TryFindSelfTest(Type moduleType, out MethodInfo entry, out string ambiguity)
        {
            entry = null;
            ambiguity = null;
            if (moduleType == null) return false;

            entry = SelfTestOn(moduleType);
            if (entry != null) return true;

            Assembly assembly = moduleType.Assembly;
            var candidates = new List<MethodInfo>();
            var owners = new List<string>();
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types ?? new Type[0]; }

            // Prefer other IModule implementations before anything else in the assembly.
            foreach (Type type in types)
            {
                if (type == null || type == moduleType) continue;
                if (!typeof(IModule).IsAssignableFrom(type)) continue;
                MethodInfo found = SelfTestOn(type);
                if (found != null) { entry = found; return true; }
            }

            foreach (Type type in types)
            {
                if (type == null) continue;
                MethodInfo found = SelfTestOn(type);
                if (found == null) continue;
                candidates.Add(found);
                owners.Add(type.FullName ?? type.Name);
            }
            if (candidates.Count == 1) { entry = candidates[0]; return true; }
            if (candidates.Count > 1)
            {
                owners.Sort(StringComparer.Ordinal);
                ambiguity = "more than one type declares static bool SelfTest(out string): "
                            + string.Join(", ", owners.ToArray())
                            + ". Put it on the module type, or rename the others.";
                return false;
            }
            return false;
        }

        /// <summary>The exact shape, on one type. PUBLIC and STATIC only, and the parameter must be
        /// <c>out string</c> rather than merely out-anything.</summary>
        private static MethodInfo SelfTestOn(Type type)
        {
            MethodInfo candidate;
            try
            {
                candidate = type.GetMethod("SelfTest", BindingFlags.Public | BindingFlags.Static);
            }
            catch (AmbiguousMatchException) { return null; }
            if (candidate == null || candidate.ReturnType != typeof(bool)) return null;
            ParameterInfo[] parameters = candidate.GetParameters();
            if (parameters.Length != 1 || !parameters[0].IsOut) return null;
            return parameters[0].ParameterType == typeof(string).MakeByRefType() ? candidate : null;
        }

        /// <summary>Find and run the module's own <c>public static bool SelfTest(out string)</c>. Absent is a
        /// FAILURE rather than a skip: a module with no self-test is exactly what this flag exists to catch,
        /// and a silent pass here would be indistinguishable from a real one.</summary>
        private static bool RunModuleSelfTest(StringBuilder sb, Type moduleType, string moduleId)
        {
            MethodInfo entry;
            string ambiguity;
            if (!TryFindSelfTest(moduleType, out entry, out ambiguity) && ambiguity != null)
            {
                sb.AppendLine("FAIL: " + ambiguity);
                return false;
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

            // Whether each event still has a subscriber. Field-like events are directly observable
            // from inside their declaring class, which is why this costs four one-line properties
            // and no new ABI.
            //
            // Deliberately HERE and not on ModuleKit's RecordingHost. ModuleKit is referenced
            // without Private="false", so its DLL is copied into every module folder and ships in
            // every zip -- adding a member there marks all six published payloads stale, which is
            // exactly what adding TrayConventions.cs did. This fake is host-only, so it costs
            // nothing, and it covers every module plus any future third-party one rather than
            // needing an edit per module.
            internal bool CompanionSpawnedHasSubs { get { return CompanionSpawned != null; } }
            internal bool CompanionPokedHasSubs { get { return CompanionPoked != null; } }
            internal bool CompanionLandedHasSubs { get { return CompanionLanded != null; } }
            internal bool HostShutdownHasSubs { get { return HostShutdown != null; } }
            internal bool ContextChangedHasSubs { get { return ContextChanged != null; } }
            internal bool FullscreenChangedHasSubs { get { return FullscreenChanged != null; } }

            // Never called: it exists so the declared events count as used under warnings-as-errors (CS0067).
            internal void TouchEvents()
            {
                CompanionSpawned?.Invoke(new FakeCompanion());
                CompanionPoked?.Invoke(null);
                CompanionLanded?.Invoke(null);
                HostShutdown?.Invoke();
                ContextChanged?.Invoke("");
                FullscreenChanged?.Invoke(false);
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
            public bool PlayNotificationSound(string moduleId) { return false; }
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
            /// <summary>Records rather than discards, so the tray convention can be asserted
            /// over what the module actually registered. It threw these away until 2026-09-22,
            /// which is why the convention existed in ModuleKit and was enforced by exactly one
            /// module that chose to call it.</summary>
            internal readonly List<TrayItem> TrayItems = new List<TrayItem>();

            public void AddTrayItems(IEnumerable<TrayItem> items)
            {
                if (items != null) TrayItems.AddRange(items);
            }
            public void AddOptionsPane(OptionsPane pane) { }
            public void PublishContext(string moduleId, string key, string valueJson) { }
            public string ReadContext(string key) { return ""; }
            // FIELD-LIKE, and that is the whole point of this line. It used to be
            // `add { } remove { }`, which does not merely fail to observe a subscription --
            // it DISCARDS it. A module that subscribed and never unsubscribed left nothing
            // behind to find, so the leak check below could not have caught it however it was
            // written. An empty accessor pair is the one shape that makes a test silently
            // unfalsifiable rather than merely incomplete.
            public event Action<string> ContextChanged;

            private sealed class Noop : IDisposable { public void Dispose() { } }
        }
    }
}
