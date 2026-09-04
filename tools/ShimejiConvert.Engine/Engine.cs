using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Serialization;
using DesktopAICompanion.Tools.ShimejiConvert.Emit;
using DesktopAICompanion.Tools.ShimejiConvert.Shimeji;

namespace DesktopAICompanion.Tools.ShimejiConvert
{
    /// <summary>
    /// The public conversion-engine surface, shared by the ShimejiConvert CLI (tools/ShimejiConvert) and Pet
    /// Studio (modules/PetStudio, source-linked). Both reach the app's REAL validator and the
    /// reachability pass through here, so neither has to duplicate the rules -- the whole point of the
    /// source-linked validator is that a consumer's verdict cannot drift from what the host actually runs.
    ///
    /// CompanionXmlValidator is internal to this assembly (it is source-linked, not referenced), so callers in
    /// other assemblies cannot reach it directly; these wrappers are the sanctioned way in.
    /// </summary>
    public static class ShimejiEngine
    {
        /// <summary>
        /// Grade a pet XML string with the app's own validator (XSD + semantic limits). This is the oracle
        /// that makes conversion safe: emitted XML is only accepted if the app itself would load it.
        /// </summary>
        public static bool TryValidate(string xml, out XmlData.RootNode root, out string error)
        {
            return CompanionXmlValidator.TryParse(xml, out root, out error);
        }

        /// <summary>
        /// Reachability/terminal/edge report over the &lt;next&gt; graph. Reports rather than throws:
        /// the interesting output of a conversion is which animations a flattened behaviour tree orphaned.
        /// </summary>
        public static GraphReport Analyze(XmlData.RootNode root)
        {
            return PetGraph.Analyze(root);
        }

        /// <summary>
        /// Serialize a parsed pet back out through its own DTOs and re-validate the result. This is the
        /// emitter's foundation: the converter builds a RootNode and writes it the same way, so anything the
        /// DTOs cannot express faithfully shows up here first, on known-good input.
        /// </summary>
        public static bool RoundTrips(XmlData.RootNode root, out string error)
        {
            error = null;
            try
            {
                string emitted = Serialize(root);
                XmlData.RootNode reparsed;
                if (!CompanionXmlValidator.TryParse(emitted, out reparsed, out error)) return false;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Serialize a pet DTO graph to an animations.xml string (the same serializer the validator
        /// round-trips through).</summary>
        public static string Serialize(XmlData.RootNode root)
        {
            var serializer = new XmlSerializer(typeof(XmlData.RootNode));
            // A plain StringWriter reports UTF-16 as its Encoding, so XmlSerializer stamps the prolog with
            // encoding="utf-16" even though the string is later written to disk as UTF-8 (no BOM). The app
            // loads pets by parsing decoded text, so that lie is invisible there, but any consumer that reads
            // the file as a byte stream (XDocument.Load, XmlReader) then honours the prolog, finds no UTF-16
            // BOM, and throws. Report UTF-8 so the declared encoding matches how the pet is actually stored --
            // this is why the shipped pets say encoding="utf-8" and converted ones used to say utf-16.
            using (var writer = new Utf8StringWriter())
            {
                serializer.Serialize(writer, root);
                return writer.ToString();
            }
        }

        /// <summary>A StringWriter that reports UTF-8 so the XML prolog matches the on-disk byte encoding.</summary>
        private sealed class Utf8StringWriter : StringWriter
        {
            public override Encoding Encoding
            {
                get { return new UTF8Encoding(false); }
            }
        }

        /// <summary>
        /// Full pipeline: parse a Shimeji conf dir, composite the skin's sprites from its img dir, and emit a
        /// desktopPet pet. Returns null with <paramref name="error"/> set if parsing or compositing fails;
        /// otherwise the result carries the pet, the residue report, and the acceptance verdict.
        /// </summary>
        public static ConversionResult ConvertSkin(string confDir, string imgDir, string skinName, out string error, bool alpha = true)
        {
            error = null;
            bool bundled = string.IsNullOrEmpty(confDir);
            ShimejiConfig config;
            try { config = bundled ? ShimejiParser.ParseBundledConf() : ShimejiParser.ParseConfDirectory(confDir); }
            catch (Exception ex) { error = "parse failed: " + ex.Message; return null; }

            SpriteSheet sheet;
            if (!SpriteSheetBuilder.Build(PetEmitter.PosesToComposite(config), SpriteSheetBuilder.FileLoader(imgDir), alpha, out sheet, out error))
                return null;

            // Capture each sounded action's clip as embedded MP3 (best-effort; classic skins only -- a bundle
            // carries no audio). No transcoder (e.g. the Pet Studio module ships none) -> silent, noted in residue.
            Func<string, byte[]> loadSound = null;
            if (!bundled)
            {
                var baker = new SoundBaker(SoundSearchRoot(confDir, imgDir));
                if (baker.TranscoderAvailable) loadSound = baker.Bake;
            }

            ConversionResult result = PetEmitter.Emit(config, sheet, SpriteSheetBuilder.FileLoader(imgDir), skinName, loadSound);
            if (bundled && result != null && result.Residue != null)
                result.Residue.Notes.Insert(0, "This skin shipped no behaviour config, so the bundled Shimeji base behaviour was used (Shimeji-EE, BSD-licensed -- see THIRD_PARTY_NOTICES).");
            return result;
        }

        // Where to look for a pose's Sound clip. Start at the directory that holds both the conf and the
        // sprites, then climb a few levels to a dir that has a sound/ child: a multi-character pack keeps
        // conf+sprites under img/<char>/ but its clips in a top-level sound/, above that common ancestor.
        private static string SoundSearchRoot(string confDir, string imgDir)
        {
            string start = CommonAncestor(confDir, imgDir);
            if (string.IsNullOrEmpty(start)) start = imgDir ?? confDir;
            try
            {
                string dir = start;
                for (int i = 0; i < 4 && !string.IsNullOrEmpty(dir); i++)
                {
                    if (Directory.Exists(Path.Combine(dir, "sound"))) return dir;
                    string parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
                    if (string.IsNullOrEmpty(parent) ||
                        string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase)) break;
                    dir = parent;
                }
            }
            catch { }
            return start;
        }

        private static string CommonAncestor(string a, string b)
        {
            try
            {
                if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return null;
                string fa = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar);
                string fb = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar);
                string[] pa = fa.Split(Path.DirectorySeparatorChar);
                string[] pb = fb.Split(Path.DirectorySeparatorChar);
                int n = Math.Min(pa.Length, pb.Length);
                int i = 0;
                while (i < n && string.Equals(pa[i], pb[i], StringComparison.OrdinalIgnoreCase)) i++;
                if (i == 0) return null;
                return string.Join(Path.DirectorySeparatorChar.ToString(), pa, 0, i);
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Resolves a Shimeji pose's Sound clip to MP3 bytes for embedding, transcoding through ffmpeg (WAV/OGG ->
    /// mono MP3) and enforcing a conservative per-pet audio budget so a converted pet stays well under the
    /// 12 MiB per-pet cap (and the all-or-nothing catalog parse). Best-effort: if ffmpeg is not found (e.g. the
    /// Pet Studio module bundles no transcoder) or a clip is missing/oversize, Bake returns null and the pet is
    /// simply silent, with the emitter recording that in the residue. Single conversion at a time (not thread-safe).
    /// </summary>
    internal sealed class SoundBaker
    {
        private const int DefaultPerSoundBytes = 1024 * 1024;       // <= the validator's 2 MiB/sound, kept smaller
        private const int DefaultTotalBytes = 3 * 1024 * 1024;      // << the validator's 8 MiB, leaving room for the sheet under 12 MiB
        private const int DefaultMaxSounds = 64;

        private readonly string _root;
        private readonly int _perSoundCap;
        private readonly int _totalCap;
        private readonly int _maxSounds;
        private readonly string _ffmpeg;
        private readonly Dictionary<string, byte[]> _cache =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private int _total;
        private int _count;

        public SoundBaker(string searchRoot)
            : this(searchRoot, DefaultPerSoundBytes, DefaultTotalBytes, DefaultMaxSounds) { }

        public SoundBaker(string searchRoot, int perSoundCap, int totalCap, int maxSounds)
        {
            _root = searchRoot;
            _perSoundCap = perSoundCap;
            _totalCap = totalCap;
            _maxSounds = maxSounds;
            _ffmpeg = FindFfmpeg();
        }

        public bool TranscoderAvailable { get { return _ffmpeg != null; } }

        // MP3 bytes for the clip named by clipPath (a pose Sound value like "/foo.wav"), or null if it is
        // unavailable / over budget. Deduplicated: a clip reused by several actions is transcoded and charged once.
        public byte[] Bake(string clipPath)
        {
            if (_ffmpeg == null || string.IsNullOrWhiteSpace(_root) || string.IsNullOrWhiteSpace(clipPath))
                return null;
            string file = Resolve(clipPath);
            if (file == null) return null;
            byte[] cached;
            if (_cache.TryGetValue(file, out cached)) return cached;
            if (_count >= _maxSounds || _total >= _totalCap) return null;

            byte[] mp3 = Transcode(file);
            if (mp3 == null || mp3.Length == 0 || mp3.Length > _perSoundCap) return null;
            if (_total + mp3.Length > _totalCap) return null;
            _total += mp3.Length;
            _count++;
            _cache[file] = mp3;
            return mp3;
        }

        // Find the clip by its file name, searched case-insensitively under the skin root. A pose Sound is
        // authored relative to the skin ("/yell.wav", "sound/yell.wav"); matching the base name is robust to
        // which subfolder (sound/, img/<char>/) a pack keeps it in.
        private string Resolve(string clipPath)
        {
            string name;
            try { name = Path.GetFileName(clipPath.Replace('\\', '/').TrimStart('/')); }
            catch { return null; }
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                if (!Directory.Exists(_root)) return null;
                foreach (string path in Directory.EnumerateFiles(_root, name, SearchOption.AllDirectories))
                    return path;
            }
            catch { }
            return null;
        }

        private byte[] Transcode(string inputFile)
        {
            string temp = Path.Combine(Path.GetTempPath(), "dp-snd-" + Guid.NewGuid().ToString("N") + ".mp3");
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _ffmpeg,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // ffmpeg writes to a temp file; stdout/stderr are only drained. Pin the encoding anyway,
                    // per the runtime-hardening invariant, so no redirected pipe rides the OS default codepage.
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-hide_banner");
                psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(inputFile);
                psi.ArgumentList.Add("-vn");
                psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("1");
                psi.ArgumentList.Add("-codec:a"); psi.ArgumentList.Add("libmp3lame");
                psi.ArgumentList.Add("-q:a"); psi.ArgumentList.Add("6");
                psi.ArgumentList.Add(temp);
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (p == null) return null;
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return null; }
                    if (p.ExitCode != 0) return null;
                }
                return File.Exists(temp) ? File.ReadAllBytes(temp) : null;
            }
            catch { return null; }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        private static string FindFfmpeg()
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                if (!string.IsNullOrEmpty(baseDir))
                {
                    string local = Path.Combine(baseDir, "native", "ffmpeg.exe");
                    if (File.Exists(local)) return local;
                }
            }
            catch { }
            return ProbeFfmpeg("ffmpeg") ? "ffmpeg" : null;
        }

        private static bool ProbeFfmpeg(string exe)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "-version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (p == null) return false;
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    return p.WaitForExit(5000) && p.ExitCode == 0;
                }
            }
            catch { return false; }
        }
    }
}
