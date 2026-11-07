using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using DesktopAICompanion.Tools.ShimejiConvert.Emit;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// SoundBaker resolves each image set and relative clip path once, misses included; remembers every refusal, the budget's as well
    /// as the clip's; keeps scanning past a subdirectory it cannot list; and is not built, let alone probed
    /// for ffmpeg, when the skin names no clip.
    ///
    /// Resolve does a recursive EnumerateFiles over the whole skin root. It used to run on every Bake
    /// call, BEFORE the byte cache was consulted, so twelve actions naming one clip paid twelve full
    /// scans to rediscover the same file. The byte cache could not prevent that: it was keyed on the
    /// RESOLVED PATH, which a repeat call could only obtain by doing the scan first.
    ///
    /// Asserted through the scan COUNTER rather than through Bake, deliberately. Bake short-circuits when
    /// ffmpeg is absent, so a test written against it would pass on a machine with no ffmpeg by never
    /// reaching the code under test -- a check that quietly stops checking on exactly the CI runner it is
    /// supposed to protect. The counter is the one observable difference between the cached and uncached
    /// versions, and it needs no external tool. The budget section below drives Bake all the same, through
    /// an INJECTED transcoder, which is what lets it run without ffmpeg rather than skip without it.
    ///
    /// NO PROBE. Every baker here is built through <see cref="SoundBaker.WithoutTranscoder"/> or
    /// <see cref="SoundBaker.WithTranscoder"/>: the public constructor probes for ffmpeg, a process spawn
    /// this test used to pay once per selftest run while asserting nothing about it (F457), and the
    /// counter <see cref="SoundBaker.TranscoderProbes"/> is asserted unmoved at the end.
    /// </summary>
    public static class SoundResolveSelfTest
    {
        public static bool Run(out string detail)
        {
            var failures = new List<string>();
            // Said on every run, pass or fail: a leg this test could not set up is a leg it did not check.
            var degraded = new List<string>();
            string root = Path.Combine(Path.GetTempPath(), "dp-sound-resolve-" + Guid.NewGuid().ToString("N"));
            int probesBefore = SoundBaker.TranscoderProbes;
            string locked = null;
            try
            {
                // A clip buried a couple of levels down, which is the case the recursive search exists for.
                string nested = Path.Combine(root, "img", "character");
                Directory.CreateDirectory(nested);
                File.WriteAllBytes(Path.Combine(nested, "yell.wav"), new byte[] { 0x52, 0x49, 0x46, 0x46 });

                SoundBaker baker = SoundBaker.WithoutTranscoder(root);
                if (baker.Scans != 0) failures.Add("a fresh baker had already scanned (" + baker.Scans + ")");

                string first = baker.ResolveCached("/yell.wav");
                if (first == null) failures.Add("the nested clip was not found at all");
                if (baker.Scans != 1) failures.Add("first resolve cost " + baker.Scans + " scans, expected 1");

                // Twelve actions naming the same clip. The old code paid twelve scans for this.
                for (int i = 0; i < 12; i++)
                {
                    string again = baker.ResolveCached("/yell.wav");
                    if (!string.Equals(again, first, StringComparison.Ordinal))
                        failures.Add("repeat resolve returned a different path");
                }
                if (baker.Scans != 1)
                    failures.Add("12 repeats cost " + baker.Scans + " scans, expected 1 (the cache is not holding)");

                if (baker.FallbackResolutions.Count != 1)
                    failures.Add("SOUND_FALLBACK_REPORT: a filename-only match was not reported exactly once");

                // A MISS must be remembered too, or an absent clip costs a full scan per reference --
                // which is the more expensive case, since a miss walks the entire tree.
                string missing = baker.ResolveCached("/nope.wav");
                if (missing != null) failures.Add("a clip that does not exist resolved to something");
                if (baker.Scans != 2) failures.Add("first miss cost " + (baker.Scans - 1) + " scans, expected 1");
                for (int i = 0; i < 8; i++) baker.ResolveCached("/nope.wav");
                if (baker.Scans != 2)
                    failures.Add("8 repeats of a miss cost " + baker.Scans + " scans total, expected 2 (misses are not cached)");
                if (baker.ScanFaulted)
                    failures.Add("a clean scan of an ordinary tree was reported as faulted");
                CheckClassicSoundOrder(root, failures);
                CheckDotSoundPaths(root, failures);
                CheckLocalSoundFallback(root, failures);
                CheckRootImageSet(root, failures);

                // ---- A SUBDIRECTORY THE USER CANNOT LIST MUST NOT HIDE THE CLIP ----
                // The (path, pattern, SearchOption) overload enumerates with IgnoreInaccessible = false, so
                // one denied directory under the skin root threw out of the enumerator, the bare catch
                // returned null, and ResolveCached remembered that null for the name: every clip in the skin
                // became a cached miss and the residue blamed "missing clips" (F438). Named to sort before
                // the clip's own directory, though the enumerator was measured to throw in either order.
                // The deny is put on the directory for the CURRENT user and removed again in the finally;
                // a box where that cannot be done is reported as DEGRADED rather than passed in silence.
                locked = Path.Combine(root, "aaa-locked");
                Directory.CreateDirectory(locked);
                string why;
                if (!TryDenyListing(locked, out why))
                {
                    degraded.Add("DEGRADED: could not deny listing on a test directory (" + why + "), so the "
                        + "inaccessible-subdirectory case was not exercised");
                    locked = null;
                }
                else
                {
                    try
                    {
                        // WITNESS first: the deny is real, or the assertion after it proves nothing.
                        bool listingThrows = false;
                        try { Directory.GetFiles(locked); }
                        catch (UnauthorizedAccessException) { listingThrows = true; }
                        if (!listingThrows)
                            failures.Add("WITNESS: the locked directory could still be listed, so the inaccessible-"
                                + "subdirectory assertion proves nothing");
                        SoundBaker walker = SoundBaker.WithoutTranscoder(root);
                        string found = walker.ResolveCached("/yell.wav");
                        if (found == null)
                            failures.Add("a clip under the skin root was not found while a sibling directory could not be "
                                + "listed: the scan aborted on the first inaccessible subdirectory instead of skipping it");
                        if (walker.ScanFaulted)
                            failures.Add("the scan reported a fault for an inaccessible subdirectory it should have skipped");
                    }
                    finally
                    {
                        // Lifted here rather than at the end, so the sections below scan an ordinary tree and a
                        // regression in THIS leg fails these two lines and no others.
                        AllowListing(locked);
                        locked = null;
                    }
                }

                // ---- A CLIP REFUSED BY THE TOTAL BUDGET IS REMEMBERED ----
                // _total never decreases and a transcoder's output for one input has one size, so the
                // outcome of "would this overshoot the total" is fixed for the conversion. It was the one
                // refusal Bake did not memoise: every later animation naming the clip -- a spoke plus each
                // chain step that replays it -- ran ffmpeg again to be refused again (F437). The transcoder
                // is injected and counts its calls; each call is what a real one would spend on a spawn.
                string soundDir = Path.Combine(root, "sound");
                Directory.CreateDirectory(soundDir);
                foreach (string clip in new[] { "a.wav", "b.wav", "c.wav" })
                    File.WriteAllBytes(Path.Combine(soundDir, clip), new byte[] { 0x52, 0x49, 0x46, 0x46 });
                var calls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                Func<string, byte[]> counting = delegate(string file)
                {
                    string name = Path.GetFileName(file);
                    int n;
                    calls.TryGetValue(name, out n);
                    calls[name] = n + 1;
                    // a and b fit the per-clip cap but not both the total; c is over the per-clip cap.
                    return new byte[string.Equals(name, "c.wav", StringComparison.OrdinalIgnoreCase) ? 20000 : 600];
                };
                SoundBaker budget = SoundBaker.WithTranscoder(root, 10000, 1000, 64, counting);
                if (!budget.TranscoderAvailable)
                    failures.Add("a baker given a transcoder reports none available");
                byte[] a1 = budget.Bake("/a.wav");
                if (a1 == null || a1.Length != 600) failures.Add("the first clip within both caps was not accepted");
                if (budget.Bake("/b.wav") != null)
                    failures.Add("a clip that would overshoot the total budget was accepted");
                budget.Bake("/b.wav");
                budget.Bake("/b.wav");
                int bCalls;
                calls.TryGetValue("b.wav", out bCalls);
                if (bCalls != 1)
                    failures.Add("a clip refused by the total budget was transcoded " + bCalls + " times over three "
                        + "requests; the refusal is fixed for the conversion and must be remembered, or ffmpeg "
                        + "runs once per animation that names the clip");
                // WITNESSES: the two memos that already existed still hold, so the new set is not the only
                // thing between a repeat request and a respawn.
                budget.Bake("/a.wav");
                int aCalls;
                calls.TryGetValue("a.wav", out aCalls);
                if (aCalls != 1) failures.Add("WITNESS: an accepted clip was transcoded again on a repeat request");
                budget.Bake("/c.wav");
                budget.Bake("/c.wav");
                int cCalls;
                calls.TryGetValue("c.wav", out cCalls);
                if (cCalls != 1) failures.Add("WITNESS: a clip over the per-clip cap was transcoded again on a repeat request");

                // ---- NO FFMPEG PROBE FROM THIS TEST, AND NONE FOR A SKIN WITH NO SOUND ----
                // The probe is a process spawn. This test asserts scans and budgets and needs no transcoder,
                // yet its bakers used to be built through the probing constructor (F457); and ConvertSkin
                // built a probing baker for every classic skin before asking whether any pose named a clip,
                // which most do not (F436). A real silent skin goes through the whole pipeline here.
                if (SoundBaker.TranscoderProbes != probesBefore)
                    failures.Add("this test probed for ffmpeg " + (SoundBaker.TranscoderProbes - probesBefore)
                        + " time(s); it asserts scans and budgets and needs no transcoder");
                string skinConf = Path.Combine(root, "skin", "conf");
                string skinImg = Path.Combine(root, "skin", "img");
                Directory.CreateDirectory(skinConf);
                Directory.CreateDirectory(skinImg);
                File.WriteAllText(Path.Combine(skinConf, "actions.xml"), SilentActionsXml, new UTF8Encoding(false));
                WritePng(Path.Combine(skinImg, "n1.png"), Color.FromArgb(255, 210, 190, 120));
                WritePng(Path.Combine(skinImg, "n2.png"), Color.FromArgb(255, 190, 170, 100));
                string convertError;
                ConversionResult silent = ShimejiEngine.ConvertSkin(skinConf, skinImg, "SilentSkin", out convertError);
                if (silent == null)
                    failures.Add("the silent skin failed to convert: " + convertError);
                else if (!silent.Accepted)
                    failures.Add("the silent skin was not accepted, so the probe assertion below ran on a broken conversion: "
                        + silent.Error);
                if (silent != null && silent.Accepted) CheckRoundTrips(silent.Root, failures);
                if (SoundBaker.TranscoderProbes != probesBefore)
                    failures.Add("converting a skin with no Sound attribute probed for ffmpeg; the probe runs before "
                        + "anyone asks whether the skin has a clip");
                // The predicate ConvertSkin gates on, both ways. These two prove the PREDICATE's answers and
                // nothing about whether ConvertSkin consults it -- the comment here used to claim they made the
                // probe assertion above proof against a gate that never builds a baker, which they cannot
                // (RA-387). The wiring is proved by the sounded conversion below.
                if (ShimejiEngine.HasSoundedPose(ShimejiParser.ParseActionsXml(SilentActionsXml)))
                    failures.Add("a skin whose poses name no clip is reported as sounded");
                if (!ShimejiEngine.HasSoundedPose(ShimejiParser.ParseActionsXml(SoundedActionsXml)))
                    failures.Add("WITNESS: a skin whose pose names a clip is reported as silent, so the gate would "
                        + "mute every sounded skin");
                // Emitter's view, not the census's (RA-377): a clip named only inside a nested composite's pose,
                // which no embedding ever reads, must not make the skin sounded and pay the probe.
                if (ShimejiEngine.HasSoundedPose(ShimejiParser.ParseActionsXml(NestedSoundOnlyActionsXml)))
                    failures.Add("a skin whose only Sound sits on a pose inside a nested composite is reported as sounded, so it "
                        + "pays the ffmpeg probe for a clip the emitter never embeds");

                // ---- THE WIRING, POSITIVELY: A SOUNDED SKIN ASKS ITS BAKER AND EMBEDS THE ANSWER (RA-387) ----
                // Everything above is satisfied by a ConvertSkin that never builds a baker for any skin: the
                // silent conversion does not probe, the predicate still answers, and F436's own mutation (the gate
                // widened to `!bundled`) fires only in the direction that PROBES. A refactor that dropped or
                // inverted the baker construction would have muted every sounded classic skin with every gate
                // green. So a sounded skin goes through ConvertSkin here, with the baker built through the
                // engine's factory seam and an injected, counting transcoder -- which keeps this test's
                // zero-probe budget (F457) while proving the request reached a baker and the clip reached the
                // document.
                string soundedConf = Path.Combine(root, "sounded", "conf");
                string soundedImg = Path.Combine(root, "sounded", "img");
                Directory.CreateDirectory(soundedConf);
                Directory.CreateDirectory(soundedImg);
                Directory.CreateDirectory(Path.Combine(root, "sounded", "sound"));
                File.WriteAllText(Path.Combine(soundedConf, "actions.xml"), SoundedActionsXml, new UTF8Encoding(false));
                WritePng(Path.Combine(soundedImg, "n1.png"), Color.FromArgb(255, 210, 190, 120));
                WritePng(Path.Combine(soundedImg, "n2.png"), Color.FromArgb(255, 190, 170, 100));
                File.WriteAllBytes(Path.Combine(root, "sounded", "sound", "hi.wav"), new byte[] { 0x52, 0x49, 0x46, 0x46 });
                var asked = new List<string>();
                Func<string, string, SoundBaker> previousFactory = ShimejiEngine.BakerFactory;
                ShimejiEngine.BakerFactory = delegate(string searchRoot, string imageSet)
                {
                    return SoundBaker.WithTranscoder(searchRoot, imageSet, delegate(string file)
                    {
                        asked.Add(Path.GetFileName(file));
                        return FakeMp3(600);
                    });
                };
                try
                {
                    string soundedError;
                    ConversionResult sounded = ShimejiEngine.ConvertSkin(soundedConf, soundedImg, "SoundedSkin", out soundedError);
                    if (sounded == null)
                        failures.Add("the sounded skin failed to convert: " + soundedError);
                    else
                    {
                        if (asked.Count != 1 || !string.Equals(asked[0], "hi.wav", StringComparison.OrdinalIgnoreCase))
                            failures.Add("a sounded skin asked the transcoder for [" + string.Join(", ", asked.ToArray())
                                + "], expected exactly hi.wav once: ConvertSkin is not building a baker for a skin whose pose "
                                + "names a clip, so every sounded classic skin converts silent");
                        int embedded = sounded.Root != null && sounded.Root.Sounds != null && sounded.Root.Sounds.Sound != null
                            ? sounded.Root.Sounds.Sound.Length : 0;
                        if (embedded != 1)
                            failures.Add("the sounded skin's pet carries " + embedded + " <sound> node(s), expected 1: the baker's "
                                + "clip did not reach the document");
                        if (!sounded.Accepted)
                            failures.Add("the sounded skin was not accepted: " + sounded.Error);
                        if (sounded.Residue == null || !sounded.Residue.Notes.Exists(delegate(string s) { return s.IndexOf("1 animation sound(s) captured", StringComparison.Ordinal) >= 0; }))
                            failures.Add("the residue does not report the captured clip");
                    }
                }
                finally { ShimejiEngine.BakerFactory = previousFactory; }
                if (SoundBaker.TranscoderProbes != probesBefore)
                    failures.Add("the sounded conversion probed for ffmpeg through the factory seam; the seam exists so it need not");

                // ---- THE FFMPEG OVERRIDE NAMES A FILE, OR NOTHING (RA-379) ----
                // The PATH probe cannot see a .cmd shim, which is how this box exposes ffmpeg, so an explicit
                // override exists. Its rule is pinned without a spawn: an existing file is returned as given
                // (full path), a missing one is not, and no variable means no override.
                string previousOverride = Environment.GetEnvironmentVariable(SoundBaker.FfmpegOverrideVariable);
                try
                {
                    string standIn = Path.Combine(root, "tools", "ffmpeg-stand-in.exe");
                    Directory.CreateDirectory(Path.GetDirectoryName(standIn));
                    File.WriteAllBytes(standIn, new byte[] { 0x4D, 0x5A });
                    Environment.SetEnvironmentVariable(SoundBaker.FfmpegOverrideVariable, standIn);
                    string resolved = SoundBaker.ResolveFfmpegOverride();
                    if (resolved == null || !string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(standIn), StringComparison.OrdinalIgnoreCase))
                        failures.Add(SoundBaker.FfmpegOverrideVariable + " names an existing file and the override resolved to '"
                            + (resolved ?? "null") + "'; the override is not being read");
                    Environment.SetEnvironmentVariable(SoundBaker.FfmpegOverrideVariable, Path.Combine(root, "tools", "missing.exe"));
                    if (SoundBaker.ResolveFfmpegOverride() != null)
                        failures.Add("the override resolved to a file that does not exist, so a typo would be probed as ffmpeg");
                    Environment.SetEnvironmentVariable(SoundBaker.FfmpegOverrideVariable, null);
                    if (SoundBaker.ResolveFfmpegOverride() != null)
                        failures.Add("WITNESS: with no override set the override still resolved to something");
                }
                finally { Environment.SetEnvironmentVariable(SoundBaker.FfmpegOverrideVariable, previousOverride); }
            }
            catch (Exception ex) { failures.Add("threw: " + ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                if (locked != null) AllowListing(locked);
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch (Exception ex) { degraded.Add("DEGRADED: could not remove the test tree " + root + " (" + ex.Message + ")"); }
            }

            var sb = new StringBuilder();
            sb.AppendLine("sound-resolve self-test: ordered image-set paths, reported filename fallback, cached misses, faithful round trips; no ffmpeg probe");
            foreach (string d in degraded) sb.AppendLine("  " + d);
            if (failures.Count == 0)
            {
                sb.Append("  12 hits + 8 misses over 2 names cost 2 scans; a denied sibling directory hid nothing; "
                    + "an over-budget clip was transcoded once; a silent skin converted without a probe; a sounded skin "
                    + "asked its baker once and embedded the clip; two sets kept their own ordered sound paths and reported "
                    + "filename fallback with local-set preference; empty step defaults round-tripped without hiding changed values; "
                    + "round trips checked semantic fidelity and a stable second serialization; "
                    + "the ffmpeg override names a file or nothing");
                detail = sb.ToString();
                return true;
            }
            foreach (string f in failures) sb.AppendLine("  FAIL " + f);
            detail = sb.ToString();
            return false;
        }

        private static void CheckClassicSoundOrder(string parent, List<string> failures)
        {
            string root = Path.Combine(parent, "two-sets");
            string[] clips = {
                "sound/Alpha/hi.wav", "sound/Beta/hi.wav", "sound/Gamma/hi.wav", "img/Alpha/sound/hi.wav", "sound/priority.wav",
                "sound/Alpha/priority.wav", "img/Alpha/sound/priority.wav", "img/Alpha/sound/image.wav",
                "sound/Alpha/left/same.wav", "sound/Alpha/right/same.wav"
            };
            foreach (string clip in clips)
            {
                string file = Path.Combine(root, clip.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllBytes(file, new byte[] { 1 });
            }
            SoundBaker baker = SoundBaker.WithoutTranscoder(root);
            if (baker.ResolveCached("/hi.wav", "Alpha") != Path.Combine(root, "sound", "Alpha", "hi.wav")
                || baker.ResolveCached("/hi.wav", "Beta") != Path.Combine(root, "sound", "Beta", "hi.wav"))
                failures.Add("SOUND_SET_BINDING: two image sets sharing a filename resolved to the wrong set");
            if (baker.ResolveCached("/priority.wav", "Alpha") != Path.Combine(root, "sound", "priority.wav"))
                failures.Add("SOUND_GLOBAL_FIRST: global sound must precede set and image-set sound");
            if (baker.ResolveCached("/image.wav", "Alpha") != Path.Combine(root, "img", "Alpha", "sound", "image.wav"))
                failures.Add("SOUND_IMAGE_PATH: img/<set>/sound was not resolved");
            if (baker.ResolveCached("/left/same.wav", "Alpha") != Path.Combine(root, "sound", "Alpha", "left", "same.wav")
                || baker.ResolveCached("/right/same.wav", "Alpha") != Path.Combine(root, "sound", "Alpha", "right", "same.wav"))
                failures.Add("SOUND_RELATIVE_CACHE: equal basenames in distinct relative paths shared a cache entry");
            if (baker.Scans != 0 || baker.FallbackResolutions.Count != 0)
                failures.Add("SOUND_DIRECT_WITNESS: canonical paths used a filename-only scan");

            // A display name is deliberately different from the sprite directory's set name. The factory
            // observes the real ConvertSkin wiring and the transcoder receives each set's own clip.
            string conf = Path.Combine(root, "conf");
            Directory.CreateDirectory(conf);
            File.WriteAllText(Path.Combine(conf, "actions.xml"), SoundedActionsXml, new UTF8Encoding(false));
            Func<string, string, SoundBaker> previous = ShimejiEngine.BakerFactory;
            try
            {
                foreach (string set in new[] { "Alpha", "Beta", "Gamma" })
                {
                    string img = set == "Gamma" ? Path.Combine(root, set, "img") : Path.Combine(root, "img", set);
                    Directory.CreateDirectory(img);
                    WritePng(Path.Combine(img, "n1.png"), Color.Coral);
                    WritePng(Path.Combine(img, "n2.png"), Color.CadetBlue);
                    string setConf = set == "Alpha" ? Path.Combine(img, "conf") : conf;
                    Directory.CreateDirectory(setConf);
                    File.WriteAllText(Path.Combine(setConf, "actions.xml"), SoundedActionsXml, new UTF8Encoding(false));
                    string captured = null;
                    ShimejiEngine.BakerFactory = delegate(string searchRoot, string imageSet)
                    {
                        return SoundBaker.WithTranscoder(searchRoot, imageSet, delegate(string file)
                        {
                            captured = file;
                            return FakeMp3(600);
                        });
                    };
                    string error;
                    ConversionResult result = ShimejiEngine.ConvertSkin(setConf, img, "Renamed pet", out error);
                    if (result == null || !result.Accepted || captured != Path.Combine(root, "sound", set, "hi.wav"))
                        failures.Add("SOUND_CONVERSION_SET: " + set + " conversion did not bake its own clip: " + error);
                }
                // Remove only the canonical Alpha clip, forcing a legacy name-only match. Its use must
                // reach the conversion residue, not merely an internal resolver counter.
                File.Delete(Path.Combine(root, "sound", "Alpha", "hi.wav"));
                File.Delete(Path.Combine(root, "img", "Alpha", "sound", "hi.wav"));
                string fallbackError;
                ConversionResult fallback = ShimejiEngine.ConvertSkin(conf, Path.Combine(root, "img", "Alpha"), "Alpha", out fallbackError);
                if (fallback == null || fallback.Residue == null || !fallback.Residue.MemberNotes.Exists(n => n.StartsWith("Sound filename-only fallback used: ", StringComparison.Ordinal)))
                    failures.Add("SOUND_FALLBACK_RESIDUE: legacy filename fallback was not reported by conversion");
            }
            finally { ShimejiEngine.BakerFactory = previous; }
        }


        private static void CheckDotSoundPaths(string parent, List<string> failures)
        {
            string pack = Path.Combine(parent, "dot-path-pack");
            Directory.CreateDirectory(Path.Combine(pack, "sound"));
            Directory.CreateDirectory(Path.Combine(pack, "legacy"));
            File.WriteAllBytes(Path.Combine(pack, "tone.wav"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(pack, "legacy", "escape.wav"), new byte[] { 2 });
            SoundBaker baker = SoundBaker.WithoutTranscoder(pack);
            if (baker.ResolveCached("../tone.wav", "Alpha") != Path.Combine(pack, "tone.wav")
                || baker.Scans != 0 || baker.FallbackResolutions.Count != 0)
                failures.Add("SOUND_CONTAINED_DOT_PATH: a contained parent path did not use its canonical source candidate");
            if (baker.ContainedSoundPath(Path.GetFullPath(Path.Combine(pack, "..", "never-probed.wav"))) != null)
            { failures.Add("SOUND_CONTAINMENT_BOUNDARY: an outside candidate passed lexical containment"); return; }
            string escaped = baker.ResolveCached("../../../../../escape.wav", "Alpha");
            if (escaped != Path.Combine(pack, "legacy", "escape.wav") || baker.FallbackResolutions.Count != 1)
                failures.Add("SOUND_OUTSIDE_SAFE_FALLBACK: outside candidates did not use a reported basename match inside the pack");
            string drive = baker.ResolveCached("Q:/tone.wav", "Alpha");
            if (drive != Path.Combine(pack, "tone.wav") || baker.FallbackResolutions.Count != 2)
                failures.Add("SOUND_DRIVE_SAFE_FALLBACK: a drive-qualified candidate lost safe basename fallback");

            string conf = Path.Combine(pack, "conf"), img = Path.Combine(pack, "img", "Alpha");
            Directory.CreateDirectory(conf); Directory.CreateDirectory(img);
            WritePng(Path.Combine(img, "n1.png"), Color.Coral);
            WritePng(Path.Combine(img, "n2.png"), Color.CadetBlue);
            File.WriteAllText(Path.Combine(conf, "actions.xml"),
                SoundedActionsXml.Replace("Sound=\"/hi.wav\"", "Sound=\"../tone.wav\""), new UTF8Encoding(false));
            Func<string, string, SoundBaker> previous = ShimejiEngine.BakerFactory;
            string captured = null;
            ShimejiEngine.BakerFactory = delegate(string root, string set)
            {
                return SoundBaker.WithTranscoder(root, set, delegate(string file) { captured = file; return FakeMp3(600); });
            };
            try
            {
                string error;
                ConversionResult result = ShimejiEngine.ConvertSkin(conf, img, "Alpha", out error);
                int embedded = result == null || result.Root.Sounds == null ? 0 : result.Root.Sounds.Sound.Length;
                if (result == null || !result.Accepted || embedded != 1 || captured != Path.Combine(pack, "tone.wav"))
                    failures.Add("SOUND_DOT_CONVERSION: classic conversion silently lost a valid contained parent-path clip");
            }
            finally { ShimejiEngine.BakerFactory = previous; }
        }

        private static void CheckLocalSoundFallback(string parent, List<string> failures)
        {
            string pack = Path.Combine(parent, "local-conf-fallback");
            Func<string, string, SoundBaker> previous = ShimejiEngine.BakerFactory;
            try
            {
                foreach (string set in new[] { "Alpha", "Beta" })
                {
                    string img = Path.Combine(pack, "img", set);
                    Directory.CreateDirectory(Path.Combine(img, "se"));
                    Directory.CreateDirectory(Path.Combine(img, "conf"));
                    File.WriteAllBytes(Path.Combine(img, "se", "hi.wav"), new byte[] { 1 });
                    WritePng(Path.Combine(img, "n1.png"), Color.Coral);
                    WritePng(Path.Combine(img, "n2.png"), Color.CadetBlue);
                    File.WriteAllText(Path.Combine(img, "conf", "actions.xml"), SoundedActionsXml, new UTF8Encoding(false));
                }
                foreach (string set in new[] { "Alpha", "Beta" })
                {
                    string img = Path.Combine(pack, "img", set), captured = null;
                    ShimejiEngine.BakerFactory = delegate(string searchRoot, string imageSet)
                    {
                        return SoundBaker.WithTranscoder(searchRoot, imageSet,
                            delegate(string file) { captured = file; return FakeMp3(600); });
                    };
                    string error;
                    ConversionResult result = ShimejiEngine.ConvertSkin(Path.Combine(img, "conf"), img, "Renamed", out error);
                    if (result == null || !result.Accepted || captured != Path.Combine(img, "se", "hi.wav"))
                        failures.Add("SOUND_LOCAL_FALLBACK: " + set + " borrowed a sibling's noncanonical clip: " + error);
                    if (result == null || !result.Residue.MemberNotes.Any(n => n.Contains(" -> img/" + set + "/se/hi.wav")))
                        failures.Add("SOUND_LOCAL_FALLBACK_REPORT: local filename fallback was not reported for " + set);
                }
                // WITNESS: when the selected set has no match, the reported pack-wide fallback remains.
                string beta = Path.Combine(pack, "img", "Beta");
                File.Delete(Path.Combine(beta, "se", "hi.wav"));
                SoundBaker wider = SoundBaker.WithoutTranscoder(pack);
                wider.FallbackDirectory = beta;
                if (wider.ResolveCached("/hi.wav", "Beta") != Path.Combine(pack, "img", "Alpha", "se", "hi.wav")
                    || wider.FallbackResolutions.Count != 1 || wider.Scans != 2)
                    failures.Add("SOUND_LOCAL_FALLBACK_WITNESS: a local miss lost the reported wider match");
            }
            finally { ShimejiEngine.BakerFactory = previous; }
        }

        private static void CheckRootImageSet(string parent, List<string> failures)
        {
            string root = Path.Combine(parent, "root-image-set");
            Directory.CreateDirectory(Path.Combine(root, "sound"));
            Directory.CreateDirectory(Path.Combine(root, "legacy"));
            File.WriteAllBytes(Path.Combine(root, "sound", "global.wav"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(root, "legacy", "fallback.wav"), new byte[] { 2 });
            foreach (string invalid in new[] { "E:\\", "\\\\server\\share\\", "bad/set", ".." })
            {
                SoundBaker baker = SoundBaker.WithoutTranscoder(root);
                if (baker.ResolveCached("/global.wav", invalid) != Path.Combine(root, "sound", "global.wav")
                    || baker.Scans != 0 || baker.FallbackResolutions.Count != 0)
                    failures.Add("SOUND_ROOT_SET_GLOBAL: an invalid set segment suppressed global sound");
                if (baker.ResolveCached("/fallback.wav", invalid) != Path.Combine(root, "legacy", "fallback.wav")
                    || baker.FallbackResolutions.Count != 1)
                    failures.Add("SOUND_ROOT_SET_FALLBACK: an invalid set segment suppressed filename fallback");
            }
            if (ShimejiEngine.ImageSetName(Path.Combine(Path.GetPathRoot(parent), "img")) != ""
                || ShimejiEngine.ImageSetName("\\\\synthetic-server\\synthetic-share\\img") != "")
                failures.Add("SOUND_ROOT_SET_NAME: a drive/share-root img folder acquired a set segment");
            string driveRoot = Path.GetPathRoot(parent), shareRoot = "\\\\synthetic-server\\synthetic-share\\";
            string driveSearch = ShimejiEngine.SoundSearchRoot(Path.Combine(driveRoot, "conf"), Path.Combine(driveRoot, "img"));
            string shareSearch = ShimejiEngine.SoundSearchRoot(Path.Combine(shareRoot, "conf"), Path.Combine(shareRoot, "img"));
            // Path.GetPathRoot returns a UNC share without its optional final separator. Unlike D:,
            // that form is still absolute; require qualification and the same share, not its spelling.
            if (driveSearch != driveRoot || !Path.IsPathFullyQualified(shareSearch)
                || shareSearch.TrimEnd(Path.DirectorySeparatorChar) != shareRoot.TrimEnd(Path.DirectorySeparatorChar))
                failures.Add("SOUND_ROOT_SEARCH: a drive/share-root install lost its absolute search root: " + driveSearch + " | " + shareSearch);
            if (ShimejiEngine.ImageSetName(Path.Combine(root, "img", "Alpha")) != "Alpha"
                || ShimejiEngine.ImageSetName(Path.Combine(root, "Beta", "img")) != "Beta")
                failures.Add("SOUND_ROOT_SET_WITNESS: ordinary image-set names were discarded");
        }

        private static void CheckRoundTrips(XmlData.RootNode root, List<string> failures)
        {
            string error;
            if (!ShimejiEngine.RoundTrips(root, out error) || !ShimejiEngine.RoundTrips(ShimejiEngine.Serialize(root), out error))
                failures.Add("ROUNDTRIP_WITNESS: a faithful synthetic pet failed: " + error);
            if (!ShimejiEngine.RoundTrips(((char)0xFEFF).ToString() + ShimejiEngine.Serialize(root), out error))
                failures.Add("ROUNDTRIP_BOM_WITNESS: a decoded UTF-8 BOM changed document semantics: " + error);
            Func<XmlData.RootNode, string> lossy = delegate(XmlData.RootNode input)
            {
                XmlData.RootNode copy = XmlData.NodeCopy.Deep(input);
                copy.Header.Title = "Information lost";
                return ShimejiEngine.Serialize(copy);
            };
            if (ShimejiEngine.RoundTripsCore(root, null, lossy, out error)
                || error == null || error.IndexOf("/header[1]/title[1]", StringComparison.Ordinal) < 0)
                failures.Add("ROUNDTRIP_DTO_LOSS: a valid stable serialization changed the title without naming its first differing location");
            string original = ShimejiEngine.Serialize(root);
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
            foreach (string hintName in new[] { "schemaLocation", "noNamespaceSchemaLocation" })
            {
                XElement hinted = XElement.Parse(original);
                hinted.SetAttributeValue(xsi + hintName, hintName == "schemaLocation"
                    ? hinted.Name.NamespaceName + " urn:synthetic:schema" : "urn:synthetic:schema");
                if (!ShimejiEngine.RoundTrips(hinted.ToString(), out error))
                    failures.Add("ROUNDTRIP_SCHEMA_HINTS: " + hintName + " changed runtime semantics: " + error);
            }
            // Equal local names in another namespace are data, and other XSI attributes are not hints.
            foreach (XName meaningful in new[] { XName.Get("schemaLocation"),
                XName.Get("schemaLocation", "urn:synthetic:metadata"), xsi + "type" })
            {
                XElement attributed = new XElement("animations", new XAttribute(meaningful, "meaningful"));
                string difference = ShimejiEngine.FirstSemanticDifference(attributed, new XElement("animations"),
                    typeof(XmlData.RootNode), "/animations");
                if (difference != "/animations/@" + meaningful)
                    failures.Add("ROUNDTRIP_SCHEMA_LOCATION_SEMANTIC: a meaningful attribute was ignored: " + meaningful);
            }
            string defaultHintDifference = ShimejiEngine.FirstSemanticDifference(
                new XElement("sound", new XElement("loop", new XAttribute(xsi + "schemaLocation", "urn:synthetic:pet urn:synthetic:schema"), 0)),
                new XElement("sound"), typeof(XmlData.SoundNode), "/sound");
            if (defaultHintDifference != null)
                failures.Add("ROUNDTRIP_SCHEMA_HINTS: a schema hint kept a default-valued scalar in the semantic comparison");
            if (ShimejiEngine.RoundTripsCore(root, original, lossy, out error)
                || error == null || error.IndexOf("/header[1]/title[1]", StringComparison.Ordinal) < 0)
                failures.Add("ROUNDTRIP_XML_LOSS: original/output information loss was not detected");
            Func<XmlData.RootNode, string> attributeLoss = delegate(XmlData.RootNode input)
            {
                XmlData.RootNode copy = XmlData.NodeCopy.Deep(input);
                copy.Spawns.Spawn[0].Probability = input.Spawns.Spawn[0].Probability == 1 ? 2 : 1;
                return ShimejiEngine.Serialize(copy);
            };
            if (ShimejiEngine.RoundTripsCore(root, original, attributeLoss, out error)
                || error == null || error.IndexOf("/spawns[1]/spawn[1]/@probability", StringComparison.Ordinal) < 0)
                failures.Add("ROUNDTRIP_ATTRIBUTE_LOSS: a changed attribute did not name its first differing location");

            // This is genuine parse-time loss, not an injected serializer: the XSD permits probability
            // on child/next, while ChildNode.Next is only an int and cannot retain the attribute.
            var childInput = XElement.Parse(original);
            XNamespace childNs = childInput.Name.Namespace;
            int target = root.Animations.Animation[0].Id;
            childInput.Element(childNs + "childs").Add(new XElement(childNs + "child",
                new XAttribute("animationid", target), new XElement(childNs + "x", "0"),
                new XElement(childNs + "y", "0"), new XElement(childNs + "next", new XAttribute("probability", 37), target)));
            XmlData.RootNode parsedChild;
            if (!ShimejiEngine.TryValidate(childInput.ToString(), out parsedChild, out error))
                failures.Add("ROUNDTRIP_PARSE_LOSS_WITNESS: synthetic child input is invalid: " + error);
            else if (ShimejiEngine.RoundTrips(childInput.ToString(), out error)
                || error == null || error.IndexOf("/childs[1]/child[1]/next[1]/@probability", StringComparison.Ordinal) < 0)
                failures.Add("ROUNDTRIP_PARSE_LOSS: the public document API silently lost child/next probability while parsing");
            // The schema accepts next=0 but the graph validator requires positive animation IDs. Exercise
            // normalization directly for that default-valued carrier, alongside the valid public witness above.
            string carrierDifference = ShimejiEngine.FirstSemanticDifference(
                XElement.Parse("<child><next probability='37'>0</next></child>"),
                XElement.Parse("<child><next>0</next></child>"), typeof(XmlData.ChildNode), "/child");
            if (carrierDifference != "/child/next[1]")
                failures.Add("ROUNDTRIP_DEFAULT_CARRIER: normalization discarded attributes on a default-valued scalar");
            int calls = 0;
            Func<XmlData.RootNode, string> unstable = delegate(XmlData.RootNode input)
            {
                calls++;
                return ShimejiEngine.Serialize(input) + (calls == 1 ? "" : "\n");
            };
            if (ShimejiEngine.RoundTripsCore(root, original, unstable, out error)
                || error == null || !error.StartsWith("Round-trip serialization is not stable at character ", StringComparison.Ordinal))
                failures.Add("ROUNDTRIP_FIXED_POINT: the second serialization changed without refusal");
            // Attribute order and default numeric lexical forms carry no semantic difference.
            var normalized = System.Xml.Linq.XElement.Parse(original);
            XNamespace ns = normalized.Name.Namespace;
            System.Xml.Linq.XElement moving = normalized.Descendants(ns + "start")
                .FirstOrDefault(n => (string)n.Element(ns + "opacity") == "1" && (string)n.Element(ns + "offsety") == "0");
            if (moving != null)
            {
                moving.Elements(ns + "offsety").Remove();
                moving.Elements(ns + "opacity").Remove();
            }
            else failures.Add("ROUNDTRIP_NORMALIZATION_WITNESS: synthetic pet has no moving node");
            if (!ShimejiEngine.RoundTrips(normalized.ToString(), out error))
                failures.Add("ROUNDTRIP_NORMALIZATION_WITNESS: omitted moving defaults changed semantics: " + error);
            if (moving != null)
            {
                moving.Add(new XElement(ns + "offsety"), new XElement(ns + "opacity"));
                if (!ShimejiEngine.RoundTrips(normalized.ToString(), out error))
                    failures.Add("ROUNDTRIP_EMPTY_DEFAULTS: empty XSD step defaults changed semantics: " + error);
                // WITNESS: nondefault values remain meaningful, even beside an empty default.
                moving.Element(ns + "offsety").Value = "7";
                string difference = ShimejiEngine.FirstSemanticDifference(normalized, XElement.Parse(original),
                    typeof(XmlData.RootNode), "/animations");
                if (difference == null || difference.IndexOf("/offsety[1]", StringComparison.Ordinal) < 0)
                    failures.Add("ROUNDTRIP_EMPTY_DEFAULTS_WITNESS: normalization hid a nondefault offset");
                moving.Element(ns + "offsety").Value = "";
                moving.Element(ns + "opacity").Value = "0.25";
                difference = ShimejiEngine.FirstSemanticDifference(normalized, XElement.Parse(original),
                    typeof(XmlData.RootNode), "/animations");
                if (difference == null || difference.IndexOf("/opacity[1]", StringComparison.Ordinal) < 0)
                    failures.Add("ROUNDTRIP_EMPTY_DEFAULTS_WITNESS: normalization hid a nondefault opacity");
                string attributeDifference = ShimejiEngine.FirstSemanticDifference(
                    new XElement("start", new XElement("offsety", new XAttribute("marker", "kept"))),
                    new XElement("start"), typeof(XmlData.MovingNode), "/start");
                if (attributeDifference != "/start/offsety[1]")
                    failures.Add("ROUNDTRIP_EMPTY_DEFAULTS_ATTRIBUTE: an empty default hid a meaningful attribute");
            }
        }

        /// <summary>Deny the current user the right to list <paramref name="dir"/>. False, with the reason,
        /// when the ACL cannot be written here.</summary>
        private static bool TryDenyListing(string dir, out string why)
        {
            why = null;
            try
            {
                var info = new DirectoryInfo(dir);
                DirectorySecurity acl = info.GetAccessControl();
                acl.AddAccessRule(new FileSystemAccessRule(
                    WindowsIdentity.GetCurrent().User, FileSystemRights.ListDirectory, AccessControlType.Deny));
                info.SetAccessControl(acl);
                return true;
            }
            catch (Exception ex)
            {
                why = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>Undo <see cref="TryDenyListing"/>, so the tree can be deleted. Best-effort: the finally
        /// reports a tree it could not remove.</summary>
        private static void AllowListing(string dir)
        {
            try
            {
                var info = new DirectoryInfo(dir);
                DirectorySecurity acl = info.GetAccessControl();
                acl.RemoveAccessRule(new FileSystemAccessRule(
                    WindowsIdentity.GetCurrent().User, FileSystemRights.ListDirectory, AccessControlType.Deny));
                info.SetAccessControl(acl);
            }
            catch { }
        }

        /// <summary>A stub clip the validator's MP3 sniff accepts: an MPEG frame sync followed by zeroes.</summary>
        private static byte[] FakeMp3(int length)
        {
            var bytes = new byte[Math.Max(4, length)];
            bytes[0] = 0xFF;
            bytes[1] = 0xFB;
            return bytes;
        }

        private static void WritePng(string path, Color colour)
        {
            using (var bmp = new Bitmap(40, 60, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    g.Clear(colour);
                }
                bmp.Save(path, ImageFormat.Png);
            }
        }

        /// <summary>A classic skin with no Sound attribute anywhere: a stand and a fall, which is enough for
        /// the emitter to accept it.</summary>
        private const string SilentActionsXml =
@"<?xml version=""1.0"" encoding=""UTF-8"" ?>
<Mascot xmlns=""http://www.group-finity.com/Mascot"">
  <ActionList>
    <Action Name=""Stand"" Type=""Stay"" BorderType=""Floor"">
      <Animation><Pose Image=""/n1.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <Action Name=""Falling"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Fall"" Gravity=""2"">
      <Animation><Pose Image=""/n2.png"" ImageAnchor=""20,60"" Velocity=""0,2"" Duration=""4"" /></Animation>
    </Action>
  </ActionList>
</Mascot>";

        /// <summary>A skin whose only Sound sits on a pose inside a NESTED action of a Sequence composite: the
        /// parser's pose census counts it, the emitter never plays it (composites carry no poses of their own
        /// and a nested action is not top-level), so the probe gate must not see it (RA-377).</summary>
        private const string NestedSoundOnlyActionsXml =
@"<?xml version=""1.0"" encoding=""UTF-8"" ?>
<Mascot xmlns=""http://www.group-finity.com/Mascot"">
  <ActionList>
    <Action Name=""Stand"" Type=""Stay"" BorderType=""Floor"">
      <Animation><Pose Image=""/n1.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <Action Name=""Routine"" Type=""Sequence"">
      <Action Name=""Inner"" Type=""Stay"" BorderType=""Floor"">
        <Animation><Pose Image=""/n1.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""10"" Sound=""/hidden.wav"" /></Animation>
      </Action>
    </Action>
    <Action Name=""Falling"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Fall"" Gravity=""2"">
      <Animation><Pose Image=""/n2.png"" ImageAnchor=""20,60"" Velocity=""0,2"" Duration=""4"" /></Animation>
    </Action>
  </ActionList>
</Mascot>";

        /// <summary>The same skin with one pose naming a clip: the witness for the gate, and the skin the
        /// positive wiring leg converts (its clip is written under sound/ at test time).</summary>
        private const string SoundedActionsXml =
@"<?xml version=""1.0"" encoding=""UTF-8"" ?>
<Mascot xmlns=""http://www.group-finity.com/Mascot"">
  <ActionList>
    <Action Name=""Stand"" Type=""Stay"" BorderType=""Floor"">
      <Animation><Pose Image=""/n1.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" Sound=""/hi.wav"" /></Animation>
    </Action>
    <Action Name=""Falling"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Fall"" Gravity=""2"">
      <Animation><Pose Image=""/n2.png"" ImageAnchor=""20,60"" Velocity=""0,2"" Duration=""4"" /></Animation>
    </Action>
  </ActionList>
</Mascot>";
    }
}
