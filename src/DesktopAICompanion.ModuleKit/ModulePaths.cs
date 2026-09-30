using System;
using System.IO;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.ModuleKit
{
    /// <summary>
    /// Where a module keeps its files. The host provisions a per-module data directory and hands it over as
    /// <see cref="IModuleStorage.DataDirectory"/>; this wraps it so a module never hard-codes a path and
    /// never writes beside the installed exe (which is read-only for a per-user install, and which an
    /// uninstall or a module UPDATE would wipe — the host deliberately preserves the data directory across
    /// an update, so anything durable belongs here).
    ///
    /// Generalized from the near-identical FortunePaths/AiPaths providers. Create one in Init:
    /// <code>_paths = ModulePaths.FromStorage(host.GetStorage(Info.Id), Info.Id);
    /// if (!_paths.HasRoot) host.Log(Info.Id, _paths.Warning);</code>
    ///
    /// There is NO temp fallback for a module handed no storage. Until 2026-09-30 a null or blank storage
    /// resolved to %TEMP%\DesktopAICompanion.&lt;id&gt;, a directory nobody owned or swept, which is where a
    /// headless <c>--module-selftest</c> run left files on every by-hand run (N-aibrain-02, the shape
    /// N-gates-02 removed from AiBrain's AiPaths). The shipped host always provisions a storage directory
    /// (IHost.GetStorage: never null from the shipped host), so in the product the no-storage branch is
    /// unreachable; a test double that hands none (the app's own convention self-test host does, on purpose,
    /// as the gate's exercise of every module's null tolerance) gets a ModulePaths with <see cref="HasRoot"/>
    /// false and a <see cref="Warning"/> to log, and every path member throws that warning rather than
    /// quietly persisting into a temp folder. A module that wants to run on defaults with nothing saved
    /// checks HasRoot; one that does not gets a clear exception at its first write, not a leak.
    /// </summary>
    public sealed class ModulePaths
    {
        private readonly string _root;      // null when the host handed no storage
        private readonly string _moduleId;

        private ModulePaths(string root, string moduleId)
        {
            _root = root;
            _moduleId = string.IsNullOrWhiteSpace(moduleId) ? "?" : moduleId;
        }

        /// <summary>Build from the host's storage. A null storage, or one with a blank directory, yields a
        /// ModulePaths with no root (<see cref="HasRoot"/> false): nothing is created anywhere, and the
        /// module decides whether to run on defaults or to fail loudly.</summary>
        public static ModulePaths FromStorage(IModuleStorage storage, string moduleId)
        {
            string root = storage == null ? null : storage.DataDirectory;
            if (string.IsNullOrWhiteSpace(root)) return new ModulePaths(null, moduleId);
            return new ModulePaths(root, moduleId);
        }

        /// <summary>An explicit root. For tests, or a module that already knows its directory.</summary>
        public static ModulePaths FromRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A root is required.", "root");
            return new ModulePaths(root, null);
        }

        /// <summary>True when the host handed this module a storage directory. False only under a host that
        /// gave none (a test double); then every path member throws <see cref="Warning"/>.</summary>
        public bool HasRoot { get { return _root != null; } }

        /// <summary>The line to log when <see cref="HasRoot"/> is false, and the message every path member
        /// throws in that state; null when there is a root.</summary>
        public string Warning
        {
            get
            {
                if (HasRoot) return null;
                return "the host gave module '" + _moduleId + "' no storage directory; running with nothing " +
                       "persisted (the shipped host always provisions one; a test host hands one through " +
                       "RecordingHost.UseStorage)";
            }
        }

        /// <summary>The module's data directory. Reading this does NOT create it; call <see cref="Ensure"/>
        /// or use <see cref="File(string)"/>, which creates on demand. Throws when there is no root.</summary>
        public string Root { get { RequireRoot(); return _root; } }

        /// <summary>Create the root if absent and return it. Safe to call repeatedly.</summary>
        public string Ensure()
        {
            RequireRoot();
            Directory.CreateDirectory(_root);
            return _root;
        }

        /// <summary>A full path to a file in the module's directory, with the directory created. Use this
        /// for anything you are about to write.</summary>
        public string File(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("A file name is required.", "fileName");
            Ensure();
            return Path.Combine(_root, fileName);
        }

        /// <summary>A full path to a subdirectory, created. Use for caches or downloaded content.</summary>
        public string Directory_(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A directory name is required.", "name");
            RequireRoot();
            string path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        private void RequireRoot()
        {
            if (_root == null) throw new InvalidOperationException(Warning);
        }
    }
}
