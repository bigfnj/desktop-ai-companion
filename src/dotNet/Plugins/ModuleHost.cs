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
        private sealed class Loaded { public IModule Module; public ModuleLoadContext Alc; public string Directory; }
        private readonly List<Loaded> _loaded = new List<Loaded>();
        private readonly List<ModuleLoadFailure> _failures = new List<ModuleLoadFailure>();

        public IReadOnlyList<IModule> Modules { get { return _loaded.Select(l => l.Module).ToList(); } }

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
            if (string.IsNullOrWhiteSpace(modulesRoot) || !Directory.Exists(modulesRoot)) return 0;
            int count = 0;
            foreach (string dir in Directory.GetDirectories(modulesRoot))
            {
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
                        _failures.Add(new ModuleLoadFailure
                        {
                            Id = info != null && !string.IsNullOrWhiteSpace(info.Id) ? info.Id : Path.GetFileName(dir),
                            Reason = requirement,
                            NeedsNewerHost = true,
                        });
                        if (log != null)
                            log("module skipped: " + (info != null ? info.Id : Path.GetFileName(dir)) +
                                " " + requirement);
                        alc.Unload();
                        continue;
                    }

                    module.Init(host);
                    _loaded.Add(new Loaded { Module = module, Alc = alc, Directory = dir });
                    count++;
                    if (log != null)
                        log("module loaded: " + module.Info.Id + " " + module.Info.Version +
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

        private static string FindModuleDll(string dir)
        {
            // Prefer <foldername>.dll; otherwise the first non-contract dll in the folder.
            string preferred = Path.Combine(dir, Path.GetFileName(dir) + ".dll");
            if (File.Exists(preferred)) return preferred;
            foreach (string f in Directory.GetFiles(dir, "*.dll"))
                if (!Path.GetFileName(f).Equals("DesktopAICompanion.Contracts.dll", StringComparison.OrdinalIgnoreCase))
                    return f;
            return null;
        }

        public void ShutdownAll(Action<string> log)
        {
            foreach (Loaded l in _loaded)
            {
                try { l.Module.Shutdown(); }
                catch (Exception ex) { if (log != null) log("module shutdown error (" + l.Module.Info.Id + "): " + ex.Message); }
                try { l.Alc.Unload(); } catch { }
            }
            _loaded.Clear();
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
