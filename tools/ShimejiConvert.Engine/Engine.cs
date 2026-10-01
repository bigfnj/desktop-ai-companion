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
        /// <paramref name="alpha"/> is true for every product caller (the CLI verbs and PetStudio pass four
        /// arguments); the keyed path it can select is reached only by the dev `composite` verb and the emitter
        /// self-tests, so a magenta-keyed CONVERSION has no producer (F434).
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
            //
            // Only when some pose actually names a clip. Constructing the baker probes for ffmpeg -- a process
            // start, two pipe drains and a wait, up to five seconds on a stalled shim -- and it used to run on
            // every classic conversion before anyone asked whether the skin had a sound at all; most do not,
            // and the bundled base conf has zero Sound attributes (F436). The residue is unaffected:
            // AppendSoundResidue says nothing when no animation wanted a clip.
            Func<string, byte[]> loadSound = null;
            SoundBaker baker = null;
            if (!bundled && HasSoundedPose(config))
            {
                baker = BakerFactory(SoundSearchRoot(confDir, imgDir));
                if (baker.TranscoderAvailable) loadSound = baker.Bake;
            }

            ConversionResult result = PetEmitter.Emit(config, sheet, SpriteSheetBuilder.FileLoader(imgDir), skinName, loadSound);
            if (bundled && result != null && result.Residue != null)
                result.Residue.Notes.Insert(0, "This skin shipped no behaviour config, so the bundled Shimeji base behaviour was used (Shimeji-EE, BSD-licensed -- see THIRD_PARTY_NOTICES).");
            // Said, not left to be inferred from a flat hub. A conf directory that holds an actions file but no
            // behaviours file under any accepted name (ShimejiParser.BehaviorsFileNames) parses with every
            // frequency at zero, which is the F442 symptom on the user-conf path; until RA-385 the only
            // visible effect was every hub choice sitting at the base weight, with nothing in the report to say why.
            if (!bundled && config.BehaviorsFile == null && result != null && result.Residue != null)
                result.Residue.Notes.Insert(0, "This skin's conf has an actions file but no behaviours file ("
                    + string.Join(", ", ShimejiParser.BehaviorsFileNames) + " were looked for beside it), so it declares no "
                    + "behaviour frequencies and every hub choice starts at the base weight.");
            // A clip the scan could not look for is not a clip that was missing, and the residue's "missing or
            // over the audio budget" would be the wrong diagnosis. Said only when the scan actually faulted.
            if (baker != null && baker.ScanFaulted && result != null && result.Residue != null)
                result.Residue.Notes.Add("The skin folder could not be fully scanned for sound clips (a directory under it failed to enumerate), so a clip reported as missing may exist.");
            return result;
        }

        /// <summary>
        /// The baker <see cref="ConvertSkin"/> builds for a sounded classic skin. The default is the real one,
        /// whose constructor probes for ffmpeg (a process spawn). A seam rather than a fixed call so
        /// SoundResolveSelfTest can prove the WIRING -- that a sounded skin's conversion asks a baker for its
        /// clip and embeds what comes back -- through an injected transcoder and without the spawn: until
        /// RA-387 the only conversion the test ran was a silent skin, so a ConvertSkin that never built a
        /// baker at all passed every gate. Process-global, restored by the test in a finally; the suites run
        /// sequentially (EngineSelfTest.RunAll), which is what makes that safe.
        /// </summary>
        internal static Func<string, SoundBaker> BakerFactory = delegate(string root) { return new SoundBaker(root); };

        /// <summary>The animation names the host binds as runtime entry points (fall/drag/kill/sync), as
        /// <c>PetGraph.ReservedEntryPointNames</c> declares them. The graph is internal to this assembly and
        /// the CLI consumes the engine as a compiled reference, so this is how the migration verbs read the
        /// one array instead of carrying a fourth copy of it (RA-370).</summary>
        public static IReadOnlyList<string> ReservedEntryPointNames { get { return PetGraph.ReservedEntryPointNames; } }

        /// <summary>True when some pose the EMITTER would play names a Sound clip, so a conversion knows
        /// whether it has any reason to look for a transcoder before it pays to find one (F436). Read through
        /// the emitter's own view of the actions (the played variant of each top-level action), not the
        /// document-wide pose census: the census counts poses inside nested composites and non-played variants
        /// that no embedding ever asks for, so a skin sounded only there paid the probe and embedded nothing
        /// (RA-377). Still a superset for an action that ends up unemitted, which costs one probe.</summary>
        internal static bool HasSoundedPose(ShimejiConfig config)
        {
            return PetEmitter.HasSoundedEmittableAction(config);
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
    /// mono MP3) and bounding how much DISTINCT audio one conversion will transcode. Best-effort: if ffmpeg is
    /// not found (e.g. the Pet Studio module bundles no transcoder) or a clip is missing/oversize, Bake returns
    /// null and the pet is simply silent, with the emitter recording that in the residue. Single conversion at
    /// a time (not thread-safe).
    ///
    /// NOT the pet's size budget. The caps here bound transcoding work per distinct clip; the room a clip has
    /// under the 12 MiB pet limit is decided where the clips are embedded (PetEmitter's sound loop), per
    /// embedding, against the sheet the compositor actually produced. This class used to claim its total
    /// "leaves room for the sheet under 12 MiB" while knowing nothing about the sheet, and charged a clip once
    /// while the emitter embeds it once per animation that plays it (F435, F426).
    /// </summary>
    internal sealed class SoundBaker
    {
        private const int DefaultPerSoundBytes = 1024 * 1024;       // <= the validator's 2 MiB/sound, kept smaller
        private const int DefaultTotalBytes = 3 * 1024 * 1024;      // distinct audio one conversion will transcode
        private const int DefaultMaxSounds = 64;

        private readonly string _root;
        private readonly int _perSoundCap;
        private readonly int _totalCap;
        private readonly int _maxSounds;
        private readonly string _ffmpeg;
        // The transcoder actually used: ffmpeg when one was found, an injected one for the self-test, null
        // when the pet stays silent. Injectable because Bake's memo and budget logic is exactly the part a
        // test needs to drive, and it sits behind a process spawn nothing in the gate can rely on.
        private readonly Func<string, byte[]> _transcode;
        private readonly Dictionary<string, byte[]> _cache =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        // Clip NAME -> resolved path (null when the skin does not contain it). Keyed on the name because
        // that is what Resolve searches on, so two actions naming the same clip share one scan.
        private readonly Dictionary<string, string> _resolved =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Files ffmpeg has already refused. Without this a clip that cannot be transcoded respawns ffmpeg,
        // and waits up to 30 s for it, once per action that names it.
        private readonly HashSet<string> _unusable =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Files that transcoded fine but would overshoot the total cap. The outcome of that comparison is
        // fixed for the rest of the conversion -- _total never decreases and ffmpeg's output for one input
        // is the same size every time -- yet it was the one refusal not remembered, so every later animation
        // naming the clip (a spoke plus each chain step replaying it) spawned ffmpeg again to be refused
        // again (F437). Kept apart from _unusable because the CLIP is not at fault.
        private readonly HashSet<string> _overBudget =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _total;
        private int _count;
        private int _scans;
        private bool _scanFaulted;

        /// <summary>How many recursive scans of the skin root have actually been performed. Exposed so the
        /// self-test can assert that N references to one clip cost ONE scan; the count is the only
        /// observable difference between the cached and uncached versions.</summary>
        internal int Scans { get { return _scans; } }

        /// <summary>True when a scan of the skin root threw (other than for a subdirectory it could not
        /// enter, which is skipped). A miss recorded after a faulted scan is not evidence the clip is
        /// absent, and ConvertSkin says so in the residue.</summary>
        internal bool ScanFaulted { get { return _scanFaulted; } }

        /// <summary>How many times a baker has probed for ffmpeg since the process started. The probe is a
        /// process spawn, so the self-tests assert it did NOT happen: a baker built for a silent skin, or
        /// for a test of scans and budgets, has no reason to pay for it (F436, F457).</summary>
        internal static int TranscoderProbes;

        public SoundBaker(string searchRoot)
            : this(searchRoot, DefaultPerSoundBytes, DefaultTotalBytes, DefaultMaxSounds) { }

        public SoundBaker(string searchRoot, int perSoundCap, int totalCap, int maxSounds)
            : this(searchRoot, perSoundCap, totalCap, maxSounds, true, null) { }

        private SoundBaker(string searchRoot, int perSoundCap, int totalCap, int maxSounds,
                           bool probeForFfmpeg, Func<string, byte[]> transcoder)
        {
            _root = searchRoot;
            _perSoundCap = perSoundCap;
            _totalCap = totalCap;
            _maxSounds = maxSounds;
            if (transcoder != null)
            {
                _transcode = transcoder;
            }
            else if (probeForFfmpeg)
            {
                TranscoderProbes++;
                _ffmpeg = FindFfmpeg();
                if (_ffmpeg != null) _transcode = TranscodeWithFfmpeg;
            }
        }

        /// <summary>A baker that never looks for ffmpeg: Bake returns null, everything else works. For the
        /// self-test of the resolve cache, which asserts scans and needs no external tool -- and used to
        /// spawn `ffmpeg -version` anyway, once per selftest run, through the public constructor (F457).</summary>
        internal static SoundBaker WithoutTranscoder(string searchRoot)
        {
            return new SoundBaker(searchRoot, DefaultPerSoundBytes, DefaultTotalBytes, DefaultMaxSounds, false, null);
        }

        /// <summary>A baker whose transcoder is <paramref name="transcoder"/> (resolved file path -> MP3 bytes,
        /// null or empty for a refusal), so the self-test can drive Bake's memo sets and caps without ffmpeg.</summary>
        internal static SoundBaker WithTranscoder(string searchRoot, int perSoundCap, int totalCap, int maxSounds,
                                                  Func<string, byte[]> transcoder)
        {
            if (transcoder == null) throw new ArgumentNullException("transcoder");
            return new SoundBaker(searchRoot, perSoundCap, totalCap, maxSounds, false, transcoder);
        }

        public bool TranscoderAvailable { get { return _transcode != null; } }

        // MP3 bytes for the clip named by clipPath (a pose Sound value like "/foo.wav"), or null if it is
        // unavailable / over budget. Deduplicated: a clip reused by several actions is transcoded and charged once.
        public byte[] Bake(string clipPath)
        {
            if (_transcode == null || string.IsNullOrWhiteSpace(_root) || string.IsNullOrWhiteSpace(clipPath))
                return null;
            string file = ResolveCached(clipPath);
            if (file == null) return null;
            byte[] cached;
            if (_cache.TryGetValue(file, out cached)) return cached;
            // Already tried and refused, for either reason. Checked BEFORE the budget so a failing clip
            // cannot consume attempts, and before the transcoder so it cannot respawn ffmpeg.
            if (_unusable.Contains(file) || _overBudget.Contains(file)) return null;
            if (_count >= _maxSounds || _total >= _totalCap) return null;

            byte[] mp3;
            try { mp3 = _transcode(file); }
            catch { mp3 = null; }
            if (mp3 == null || mp3.Length == 0 || mp3.Length > _perSoundCap)
            {
                // The CLIP is the problem, so remember it as unusable.
                _unusable.Add(file);
                return null;
            }
            if (_total + mp3.Length > _totalCap)
            {
                // The BUDGET is the problem, and it will still be the problem next time: remember that too,
                // in its own set, so the file is not blamed and ffmpeg is not run again to reach the same answer.
                _overBudget.Add(file);
                return null;
            }
            _total += mp3.Length;
            _count++;
            _cache[file] = mp3;
            return mp3;
        }

        // Find the clip by its file name, searched case-insensitively under the skin root. A pose Sound is
        // authored relative to the skin ("/yell.wav", "sound/yell.wav"); matching the base name is robust to
        // which subfolder (sound/, img/<char>/) a pack keeps it in.
        /// <summary>Resolve once per clip name, negative results included.
        ///
        /// Resolve does a recursive EnumerateFiles over the whole skin root, and it used to run on EVERY
        /// Bake call -- BEFORE the byte cache was consulted, so a clip shared by a dozen actions paid a
        /// dozen full scans to rediscover the same file. Caching the NAME rather than the resolved path
        /// is the point: the old cache was keyed on the result, which a repeat call could only reach by
        /// doing the scan first.</summary>
        internal string ResolveCached(string clipPath)
        {
            string name;
            try { name = Path.GetFileName((clipPath ?? "").Replace('\\', '/').TrimStart('/')); }
            catch { return null; }
            if (string.IsNullOrEmpty(name)) return null;
            string hit;
            if (_resolved.TryGetValue(name, out hit)) return hit;
            string found = Resolve(clipPath);
            _resolved[name] = found;
            return found;
        }

        private string Resolve(string clipPath)
        {
            string name;
            try { name = Path.GetFileName(clipPath.Replace('\\', '/').TrimStart('/')); }
            catch { return null; }
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                if (!Directory.Exists(_root)) return null;
                _scans++;
                // EnumerationOptions, not the (path, pattern, SearchOption) overload. On .NET Core that
                // overload maps to options with IgnoreInaccessible = false, so ONE subdirectory the user
                // cannot list -- a locked vendor folder, a junction the account lacks rights to -- threw
                // UnauthorizedAccessException out of the enumerator before or after the clip was reached,
                // the bare catch swallowed it, and ResolveCached stored the null: every clip in the skin
                // became a cached miss and the residue blamed "missing clips" (F438). SoundSearchRoot can
                // climb up to four parents looking for a sound/ sibling, so the scanned root is often wider
                // than the skin folder. AttributesToSkip is 0 to keep the legacy overload's reach over
                // hidden and system files; the case rule is the one the legacy overload had on Windows.
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0,
                    MatchCasing = MatchCasing.CaseInsensitive,
                };
                foreach (string path in Directory.EnumerateFiles(_root, name, options))
                    return path;
            }
            catch { _scanFaulted = true; }
            return null;
        }

        private byte[] TranscodeWithFfmpeg(string inputFile)
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
                    // Both pipes drained CONCURRENTLY, then the wait. A blocking ReadToEnd on stdout
                    // only returns at EOF, which is exit, so the 30-second cap below was always called
                    // on a finished process and could never fire -- and a converting ffmpeg that filled
                    // its 4 KB stderr pipe while this thread sat on stdout stopped forever, with no
                    // timeout left to rescue it. ffmpeg is exactly the tool that writes a lot to stderr.
                    System.Threading.Tasks.Task<string> outText = p.StandardOutput.ReadToEndAsync();
                    System.Threading.Tasks.Task<string> errText = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(30000))
                    {
                        // Kill is asynchronous at the OS level: TerminateProcess returns before the target's
                        // handles are closed, and ffmpeg holds the output file open without FILE_SHARE_DELETE.
                        // Returning straight into the finally below deleted a file ffmpeg still owned, the
                        // sharing violation was swallowed, and a partial dp-snd-<guid>.mp3 stayed in %TEMP%
                        // -- measured 20/20 with a plain Kill, hidden 40/40 here only because Kill(true)'s
                        // process-tree walk happens to outlast the handle teardown (F439). A bounded wait
                        // makes the delete deterministic instead of lucky; it costs at most two seconds on a
                        // path that has already spent thirty.
                        try { p.Kill(true); } catch { }
                        try { p.WaitForExit(2000); } catch { }
                        return null;
                    }
                    // WaitForExit(int) does not guarantee the redirected readers have drained.
                    p.WaitForExit();
                    try { outText.GetAwaiter().GetResult(); } catch { }
                    try { errText.GetAwaiter().GetResult(); } catch { }
                    if (p.ExitCode != 0) return null;
                }
                return File.Exists(temp) ? File.ReadAllBytes(temp) : null;
            }
            catch { return null; }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        /// <summary>The environment variable naming the ffmpeg executable to use ahead of the bundled and PATH
        /// candidates. The PATH probe starts the bare name with UseShellExecute=false, which CreateProcess
        /// resolves to an .exe only: a .cmd shim -- the one way the maintainer's own toolbox exposes ffmpeg --
        /// is invisible to it, so every sounded classic skin converted silent on that box while `ffmpeg
        /// -version` worked in every shell (RA-379). An explicit override keeps ffmpeg an executable, so no
        /// clip path ever passes through cmd.exe.</summary>
        public const string FfmpegOverrideVariable = "SHIMEJICONVERT_FFMPEG";

        /// <summary>The override's value when it names an existing file, else null. Read without spawning,
        /// so the self-test can pin the rule while keeping its zero-probe budget (F457).</summary>
        internal static string ResolveFfmpegOverride()
        {
            try
            {
                string configured = Environment.GetEnvironmentVariable(FfmpegOverrideVariable);
                if (string.IsNullOrWhiteSpace(configured)) return null;
                string path = configured.Trim().Trim('"');
                return File.Exists(path) ? Path.GetFullPath(path) : null;
            }
            catch { return null; }
        }

        private static string FindFfmpeg()
        {
            // The override first, and probed like any other candidate: a path that exists but does not run
            // falls through to the bundled and PATH candidates rather than silencing the pet.
            string configured = ResolveFfmpegOverride();
            if (configured != null && ProbeFfmpeg(configured)) return configured;
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
                    // As above. This one also LEAKED: when the wait returned false the method returned
                    // without killing, leaving an ffmpeg running for the life of the converter.
                    System.Threading.Tasks.Task<string> probeOut = p.StandardOutput.ReadToEndAsync();
                    System.Threading.Tasks.Task<string> probeErr = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } return false; }
                    p.WaitForExit();
                    try { probeOut.GetAwaiter().GetResult(); } catch { }
                    try { probeErr.GetAwaiter().GetResult(); } catch { }
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }
    }
}
