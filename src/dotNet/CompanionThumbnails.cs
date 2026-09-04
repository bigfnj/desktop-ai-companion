using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace DesktopAICompanion
{
    /// <summary>
    /// Bundled preview icons for catalog pets, keyed by pet id. The icons ship inside the assembly as a
    /// single zip (one "&lt;id&gt;.png" per pet) so the "Get more pets" grid can show a thumbnail for a pet
    /// that hasn't been downloaded yet -- instantly and offline, with no per-tile network fetch.
    /// </summary>
    internal static class CompanionThumbnails
    {
        private static readonly object Gate = new object();
        private static Dictionary<string, byte[]> _icons;   // id (lower) -> PNG bytes; loaded once

        // A pet icon is tiny; cap what we accept from the zip so a bad asset can't balloon memory.
        private const int MaximumIconBytes = 256 * 1024;

        /// <summary>A fresh image for the pet id, or null when none is bundled. The caller owns and
        /// must dispose it (the pet gallery disposes card images when it rebuilds).</summary>
        public static Image Get(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            byte[] png;
            lock (Gate)
            {
                if (_icons == null) _icons = LoadArchive();
                if (!_icons.TryGetValue(id.Trim().ToLowerInvariant(), out png) || png == null)
                    return null;
            }
            try
            {
                using (var stream = new MemoryStream(png, false))
                using (var decoded = Image.FromStream(stream, false, true))
                    return new Bitmap(decoded);   // detach from the stream so it survives disposal
            }
            catch { return null; }
        }

        /// <summary>Raw PNG bytes for the pet id, or null when none is bundled. Lets the WPF pet gallery
        /// build a BitmapImage directly (no System.Drawing round-trip). Returns a copy so callers can't
        /// mutate the cached array.</summary>
        public static byte[] GetPng(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            lock (Gate)
            {
                if (_icons == null) _icons = LoadArchive();
                byte[] png;
                if (!_icons.TryGetValue(id.Trim().ToLowerInvariant(), out png) || png == null) return null;
                return (byte[])png.Clone();
            }
        }

        private static Dictionary<string, byte[]> LoadArchive()
        {
            var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string resourceName = null;
                foreach (string name in asm.GetManifestResourceNames())
                    if (name.EndsWith("pet-thumbnails.zip", StringComparison.OrdinalIgnoreCase))
                    {
                        resourceName = name;
                        break;
                    }
                if (resourceName == null) return map;

                using (Stream zipStream = asm.GetManifestResourceStream(resourceName))
                {
                    if (zipStream == null) return map;
                    using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
                        foreach (ZipArchiveEntry entry in archive.Entries)
                        {
                            if (entry.Length <= 0 || entry.Length > MaximumIconBytes) continue;
                            string id = Path.GetFileNameWithoutExtension(entry.Name);
                            if (string.IsNullOrEmpty(id)) continue;
                            try
                            {
                                using (Stream es = entry.Open())
                                using (var ms = new MemoryStream())
                                {
                                    es.CopyTo(ms);
                                    map[id] = ms.ToArray();
                                }
                            }
                            catch { }
                        }
                }
            }
            catch { }
            return map;
        }
    }
}
