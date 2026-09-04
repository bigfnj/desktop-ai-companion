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
    /// <code>_paths = ModulePaths.FromStorage(host.GetStorage(Info.Id), Info.Id);</code>
    /// A module without the Storage permission still gets a working (temp) root rather than a crash.
    /// </summary>
    public sealed class ModulePaths
    {
        private readonly string _root;

        private ModulePaths(string root) { _root = root; }

        /// <summary>Build from the host's storage. Falls back to a stable per-module temp folder when the
        /// module did not declare Storage (storage is null) or the host could not provision one — a module
        /// that cannot persist should degrade to scratch space, not fail to load.</summary>
        public static ModulePaths FromStorage(IModuleStorage storage, string moduleId)
        {
            string root = storage == null ? null : storage.DataDirectory;
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(Path.GetTempPath(), "DesktopAICompanion." + SafeSegment(moduleId));
            return new ModulePaths(root);
        }

        /// <summary>An explicit root. For tests, or a module that already knows its directory.</summary>
        public static ModulePaths FromRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A root is required.", "root");
            return new ModulePaths(root);
        }

        /// <summary>The module's data directory. Reading this does NOT create it; call <see cref="Ensure"/>
        /// or use <see cref="File(string)"/>, which creates on demand.</summary>
        public string Root { get { return _root; } }

        /// <summary>Create the root if absent and return it. Safe to call repeatedly.</summary>
        public string Ensure()
        {
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
            string path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>Reduce a module id to ONE safe folder name, so a hostile or careless id can never walk
        /// out of the temp fallback root. Directory separators are invalid file-name characters, so they are
        /// replaced; ".." is then collapsed as well, purely so the result cannot read like a traversal even
        /// though a single segment could not perform one.</summary>
        private static string SafeSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "module";
            char[] invalid = Path.GetInvalidFileNameChars();
            var builder = new System.Text.StringBuilder(value.Length);
            foreach (char c in value)
                builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            string cleaned = builder.ToString().Replace("..", "_").Trim('.', ' ');
            return cleaned.Length == 0 ? "module" : cleaned;
        }
    }
}
