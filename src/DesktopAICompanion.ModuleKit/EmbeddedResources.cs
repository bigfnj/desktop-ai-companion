using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace DesktopAICompanion.ModuleKit
{
    /// <summary>
    /// Read a file a module embedded in its own DLL — a tray icon PNG, a JSON corpus, a seed data file.
    ///
    /// Every module was open-coding the same loop: enumerate GetManifestResourceNames() and take the one
    /// whose name ENDS WITH the file name. That suffix match is the point — the SDK prefixes a manifest
    /// resource with the root namespace and folder path, so the full name is brittle to a namespace rename
    /// or a file move, while the trailing file name is not. (A module that needs an exact name should set
    /// LogicalName in its csproj and pass that; a suffix match still finds it.)
    ///
    /// Everything here returns null/empty rather than throwing: a missing icon must never break a tray item.
    /// </summary>
    public static class EmbeddedResources
    {
        /// <summary>The raw bytes of the embedded resource whose manifest name ends with
        /// <paramref name="fileNameSuffix"/> (e.g. "icon.png"), or null when absent or unreadable.</summary>
        public static byte[] LoadBytes(Assembly assembly, string fileNameSuffix)
        {
            try
            {
                if (assembly == null || string.IsNullOrEmpty(fileNameSuffix)) return null;
                string name = FindName(assembly, fileNameSuffix);
                if (name == null) return null;
                using (Stream stream = assembly.GetManifestResourceStream(name))
                {
                    if (stream == null) return null;
                    using (var buffer = new MemoryStream())
                    {
                        stream.CopyTo(buffer);
                        return buffer.ToArray();
                    }
                }
            }
            catch { return null; }
        }

        /// <summary>The embedded resource as UTF-8 text, or "" when absent. A leading byte-order mark is
        /// stripped: StreamReader keeps it out of the string, but callers that later re-parse the text (or
        /// compare it) have been bitten by a surviving BOM in this codebase before.</summary>
        public static string LoadText(Assembly assembly, string fileNameSuffix)
        {
            try
            {
                if (assembly == null || string.IsNullOrEmpty(fileNameSuffix)) return "";
                string name = FindName(assembly, fileNameSuffix);
                if (name == null) return "";
                using (Stream stream = assembly.GetManifestResourceStream(name))
                {
                    if (stream == null) return "";
                    using (var reader = new StreamReader(stream, new UTF8Encoding(false), true))
                        return reader.ReadToEnd().TrimStart('﻿');
                }
            }
            catch { return ""; }
        }

        /// <summary>The embedded resource deserialized from JSON, or default(T) when absent or malformed.
        /// Trailing commas and comments are tolerated so a hand-edited data file still loads.</summary>
        public static T LoadJson<T>(Assembly assembly, string fileNameSuffix)
        {
            try
            {
                string text = LoadText(assembly, fileNameSuffix);
                if (string.IsNullOrWhiteSpace(text)) return default(T);
                var options = new JsonSerializerOptions
                {
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    PropertyNameCaseInsensitive = true,
                };
                return JsonSerializer.Deserialize<T>(text, options);
            }
            catch { return default(T); }
        }

        /// <summary>Whether a matching resource exists — useful in a self-test that asserts the csproj
        /// actually embedded what the module reads at runtime.</summary>
        public static bool Exists(Assembly assembly, string fileNameSuffix)
        {
            if (assembly == null || string.IsNullOrEmpty(fileNameSuffix)) return false;
            try { return FindName(assembly, fileNameSuffix) != null; }
            catch { return false; }
        }

        private static string FindName(Assembly assembly, string fileNameSuffix)
        {
            string[] names = assembly.GetManifestResourceNames();
            foreach (string candidate in names)
                if (candidate.EndsWith(fileNameSuffix, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            return null;
        }
    }
}
