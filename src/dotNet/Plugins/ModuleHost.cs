using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.Plugins
{
    /// <summary>Why a module folder did not end up running. Id is the folder name (which is the module id by
    /// convention) because a module that failed early may never have produced a ModuleInfo to ask.</summary>
    internal sealed class ModuleLoadFailure
    {
        public string Id;
        public string Reason;
        /// <summary>True when the module is fine but this host is too old — it declared a MinHostVersion above
        /// the running version. Worth distinguishing: the fix is updating the app, not reinstalling the module.</summary>
        public bool NeedsNewerHost;
    }

    /// <summary>
    /// Loads plugin module DLLs from a folder, each in its own collectible <see cref="AssemblyLoadContext"/>,
    /// while sharing the single DesktopAICompanion.Contracts assembly from the default context so IModule/IHost
    /// types unify across host and modules. A module that fails to load or init is isolated (logged +
    /// skipped) so one bad module can never take the host down. Sideload layout: &lt;modulesRoot&gt;/&lt;id&gt;/&lt;id&gt;.dll.
    /// </summary>
    internal sealed class ModuleHost : IDisposable
    {
        // Directory was here, assigned once and read by nothing: Loaded is private to this type, and
        // its only consumers are ShutdownAll (Module, Alc) and the Modules projection (Module).
        private sealed class Loaded { public IModule Module; public ModuleLoadContext Alc; }
        private readonly List<Loaded> _loaded = new List<Loaded>();
        private readonly List<ModuleLoadFailure> _failures = new List<ModuleLoadFailure>();

        // Published as an immutable array, replaced whole after every Add and on ShutdownAll (RA-276). Modules
        // is read off the UI thread: CompanionHost.PlaySound's permission lookup runs on the caller's thread,
        // and a Reminder chime due at launch plays from a pool thread while LoadFrom is still adding modules.
        // A LINQ projection over the mutable _loaded list could see a growth mid-enumeration; a reader of the
        // array sees the set as it was at the last publish, which is what a permission lookup wants.
        private volatile IModule[] _snapshot = new IModule[0];
        public IReadOnlyList<IModule> Modules { get { return _snapshot; } }
        private void PublishSnapshot() { _snapshot = _loaded.Select(l => l.Module).ToArray(); }

        /// <summary>Folders that looked like a module but did not end up running, with the reason.
        ///
        /// Without this the failure is invisible: the Modules pane enumerates FOLDERS to decide what is
        /// installed, so a broken module counts as installed (and is filtered out of "available"), reports no
        /// live version (so no update is ever offered), and shows "restart to activate" forever — leaving
        /// Uninstall, which deletes the module's settings and keys, as the only way out of a state the user
        /// did not cause.</summary>
        public IReadOnlyList<ModuleLoadFailure> Failures { get { return _failures.ToList(); } }

        private void Fail(string dir, string reason, Action<string> log)
        {
            string id = Path.GetFileName(dir);
            _failures.Add(new ModuleLoadFailure { Id = id, Reason = reason });
            if (log != null) log("module did not load: " + id + " -- " + reason);
        }

        /// <summary>Scan the modules root and load every module folder. Returns the count loaded.</summary>
        public int LoadFrom(string modulesRoot, IHost host, Action<string> log)
        {
            return LoadFrom(modulesRoot, host, log, null);
        }

        /// <summary>
        /// As above, but never loading a folder named in <paramref name="pendingRemoval"/> (F352). Those are
        /// the ids PendingModuleRemovals could not delete this launch -- still locked by a sibling instance
        /// or an indexer -- and loading one would lock its DLL again in THIS process, so the retry on the next
        /// launch fails the same way for as long as the app keeps being restarted. Recorded as a failure with
        /// the reason, so the Modules pane says why rather than "restart to activate".
        /// </summary>
        public int LoadFrom(string modulesRoot, IHost host, Action<string> log, IReadOnlyCollection<string> pendingRemoval)
        {
            if (string.IsNullOrWhiteSpace(modulesRoot) || !Directory.Exists(modulesRoot)) return 0;
            int count = 0;
            foreach (string dir in Directory.GetDirectories(modulesRoot))
            {
                if (IsPendingRemoval(dir, pendingRemoval))
                {
                    Fail(dir, "still being removed: its folder was locked when this launch tried; it goes on a later launch", log);
                    continue;
                }
                ModuleLoadContext alc = null;
                try
                {
                    string dll = FindModuleDll(dir);
                    if (dll == null)
                    {
                        Fail(dir, "no module DLL in the folder", log);
                        continue;
                    }
                    alc = new ModuleLoadContext(dll);
                    Assembly asm = alc.LoadFromAssemblyPath(dll);
                    Type moduleType = asm.GetTypes().FirstOrDefault(t => !t.IsAbstract && typeof(IModule).IsAssignableFrom(t));
                    if (moduleType == null)
                    {
                        Fail(dir, "no type implementing IModule", log);
                        alc.Unload();
                        continue;
                    }
                    var module = (IModule)Activator.CreateInstance(moduleType);

                    // Check the module's declared MinHostVersion BEFORE Init: a module the host cannot
                    // satisfy must never touch it. Refusing is a log + unload, never a throw, so one
                    // too-new module cannot stop the others from loading.
                    ModuleInfo info = module.Info;
                    string requirement;
                    if (!ModuleHostRequirement.IsSatisfied(
                            host != null ? host.HostVersion : "",
                            info != null ? info.MinHostVersion : "",
                            out requirement))
                    {
                        // Not a defect: a module correctly refusing an older host. Recorded all the same, so
                        // the pane can say WHY rather than showing a module that silently never runs.
                        //
                        // Keyed by the FOLDER NAME like every other failure (F343). The pane matches failures
                        // to the folders it enumerates, so a refusal filed under Info.Id was invisible the
                        // moment the two differed and the row fell back to "restart to activate". The
                        // declared id still travels in the reason when it is not the folder's.
                        string folder = Path.GetFileName(dir);
                        string declaredId = info != null && !string.IsNullOrWhiteSpace(info.Id) ? info.Id.Trim() : "";
                        bool idDiffers = declaredId.Length > 0 && !string.Equals(declaredId, folder, StringComparison.OrdinalIgnoreCase);
                        _failures.Add(new ModuleLoadFailure
                        {
                            Id = folder,
                            Reason = requirement + (idDiffers ? " (module id '" + declaredId + "')" : ""),
                            NeedsNewerHost = true,
                        });
                        if (log != null)
                            log("module skipped: " + (declaredId.Length > 0 ? declaredId : folder) + " " + requirement);
                        alc.Unload();
                        continue;
                    }

                    // Attribute everything registered inside Init to this module. The finally is the
                    // whole point: a module that throws from Init must not leave the next one's panes
                    // credited to it -- and, since F344, must not keep its OWN either: an Init that threw
                    // after contributing is rolled back (tray items, panes, responders, hotkeys, event
                    // subscriptions), so the module the pane reports as failed holds nothing in the host.
                    var attributing = host as CompanionHost;
                    if (attributing != null) attributing.BeginModuleInit(module.Info != null ? module.Info.Id : null);
                    bool initialised = false;
                    try { module.Init(host); initialised = true; }
                    finally
                    {
                        if (attributing != null)
                        {
                            if (initialised) attributing.EndModuleInit();
                            else attributing.RollBackModuleInit();
                        }
                    }
                    _loaded.Add(new Loaded { Module = module, Alc = alc });
                    PublishSnapshot();
                    count++;
                    // GUARDED, like the two reads of Info above it. This line dereferenced it bare,
                    // inside the same try that had already written `module.Info != null ? ... : null`
                    // twenty lines earlier -- and it runs AFTER _loaded.Add, so a null Info threw into
                    // the catch below and the module ended up in BOTH Modules and Failures, with
                    // alc.Unload() requested underneath a module whose Init had already registered
                    // panes and tray items. The class promises the opposite at the top of this file:
                    // "a module that fails to load or init is isolated ... so one bad module can never
                    // take the host down."
                    //
                    // Not reachable with any in-repo module -- all eight declare Info -- so this is the
                    // third-party path. Which is exactly the path the isolation promise is FOR.
                    ModuleInfo loadedInfo = module.Info;
                    if (log != null)
                        log("module loaded: " +
                            (loadedInfo != null ? loadedInfo.Id : "(no ModuleInfo)") + " " +
                            (loadedInfo != null ? loadedInfo.Version : "?") +
                            (requirement.Length > 0 ? " (" + requirement + ")" : ""));
                }
                catch (Exception ex)
                {
                    Fail(dir, ex.GetType().Name + ": " + ex.Message, log);
                    if (alc != null) { try { alc.Unload(); } catch { } }
                }
            }
            return count;
        }

        private static bool IsPendingRemoval(string dir, IReadOnlyCollection<string> pendingRemoval)
        {
            if (pendingRemoval == null || pendingRemoval.Count == 0) return false;
            string folder = Path.GetFileName(dir);
            foreach (string id in pendingRemoval)
                if (string.Equals(id, folder, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// The module's own assembly in <paramref name="dir"/>: <c>&lt;folder&gt;.dll</c> when it exists; else
        /// the assembly the folder's single <c>*.deps.json</c> names (the anchor tests/Invoke-SelfTests.ps1
        /// already uses to tell a module DLL from its dependencies); else the first DLL that is not one of the
        /// host's own. The fallback excluded only Contracts.dll, and every module folder also ships
        /// DesktopAICompanion.ModuleKit.dll, so a sideloaded folder not named after its assembly could have
        /// ModuleKit chosen and be refused as "no type implementing IModule" (RA-291). Internal for
        /// --module-host-selftest, which drives it over fixture folders.
        /// </summary>
        internal static string FindModuleDll(string dir)
        {
            string preferred = Path.Combine(dir, Path.GetFileName(dir) + ".dll");
            if (File.Exists(preferred)) return preferred;
            string[] deps = Directory.GetFiles(dir, "*.deps.json");
            if (deps.Length == 1)
            {
                string assemblyName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(deps[0]));
                string named = Path.Combine(dir, assemblyName + ".dll");
                if (File.Exists(named)) return named;
            }
            foreach (string f in Directory.GetFiles(dir, "*.dll"))
                if (!Path.GetFileName(f).StartsWith("DesktopAICompanion.", StringComparison.OrdinalIgnoreCase))
                    return f;
            return null;
        }

        public void ShutdownAll(Action<string> log)
        {
            foreach (Loaded l in _loaded)
            {
                try { l.Module.Shutdown(); }
                catch (Exception ex)
                {
                    // INSIDE A CATCH, which is why this one is worse than the load-time twin. It read
                    // `l.Module.Info.Id` bare, so a module with a null Info that ALSO threw from
                    // Shutdown raised an NRE from the handler itself -- escaping ShutdownAll entirely
                    // and skipping Alc.Unload() for this module AND every module after it in the list.
                    // A handler that can throw is not a handler.
                    if (log != null)
                    {
                        ModuleInfo info = null;
                        try { info = l.Module.Info; } catch { }
                        log("module shutdown error (" +
                            (info != null ? info.Id : "(no ModuleInfo)") + "): " + ex.Message);
                    }
                }
                try { l.Alc.Unload(); } catch { }
            }
            _loaded.Clear();
            PublishSnapshot();
        }

        public void Dispose() { ShutdownAll(null); }

        /// <summary>
        /// Per-module load context. The <c>Load</c> override returns null for DesktopAICompanion.Contracts so it
        /// resolves from the default context (a single shared contract assembly = unified IModule/IHost
        /// types); the module's own dependencies resolve from its folder via the dependency resolver.
        /// </summary>
        private sealed class ModuleLoadContext : AssemblyLoadContext
        {
            private readonly AssemblyDependencyResolver _resolver;
            private readonly string _moduleDir;
            public ModuleLoadContext(string moduleDll)
                : base("module:" + Path.GetFileNameWithoutExtension(moduleDll), true)
            { _resolver = new AssemblyDependencyResolver(moduleDll); _moduleDir = Path.GetDirectoryName(moduleDll); }

            protected override Assembly Load(AssemblyName name)
            {
                if (name.Name == "DesktopAICompanion.Contracts") return null;   // share from the default context
                string path = _resolver.ResolveAssemblyToPath(name);
                return path != null ? LoadFromAssemblyPath(path) : null;
            }

            protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
            {
                // A module can carry native dependencies (e.g. onnxruntime.dll for the Fortunes smart
                // engine). First try the module's deps.json (runtimes\<rid>\native\ or the NuGet cache) via
                // the dependency resolver.
                string path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
                if (path != null && File.Exists(path)) return LoadUnmanagedDllFromPath(path);
                // Some native-carrying packages (onnxruntime) flatten their native dll beside the module dll
                // rather than under runtimes\<rid>\native\; probe the module's own folder so it resolves on an
                // installed machine too (no NuGet cache). IntPtr.Zero falls back to default OS/host resolution.
                string file = Path.HasExtension(unmanagedDllName) ? unmanagedDllName : unmanagedDllName + ".dll";
                string local = _moduleDir != null ? Path.Combine(_moduleDir, file) : null;
                if (local != null && File.Exists(local)) return LoadUnmanagedDllFromPath(local);
                return IntPtr.Zero;
            }
        }
    }
}
