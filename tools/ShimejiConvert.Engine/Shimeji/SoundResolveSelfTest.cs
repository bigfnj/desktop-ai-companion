using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using DesktopAICompanion.Tools.ShimejiConvert.Emit;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// SoundBaker resolves each clip NAME once, misses included; remembers every refusal, the budget's as well
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

                // The same clip named with a different prefix is the same NAME, so it must not rescan:
                // a pose may author "/yell.wav" while another writes "sound/yell.wav".
                baker.ResolveCached("sound/yell.wav");
                if (baker.Scans != 1)
                    failures.Add("a differently-prefixed reference to the same clip rescanned");

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
                Func<string, SoundBaker> previousFactory = ShimejiEngine.BakerFactory;
                ShimejiEngine.BakerFactory = delegate(string searchRoot)
                {
                    return SoundBaker.WithTranscoder(searchRoot, 10000, 100000, 64, delegate(string file)
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
            sb.AppendLine("sound-resolve self-test: one recursive scan per clip name, misses included; every refusal remembered; no ffmpeg probe");
            foreach (string d in degraded) sb.AppendLine("  " + d);
            if (failures.Count == 0)
            {
                sb.Append("  12 hits + 8 misses over 2 names cost 2 scans; a denied sibling directory hid nothing; "
                    + "an over-budget clip was transcoded once; a silent skin converted without a probe; a sounded skin "
                    + "asked its baker once and embedded the clip; the ffmpeg override names a file or nothing");
                detail = sb.ToString();
                return true;
            }
            foreach (string f in failures) sb.AppendLine("  FAIL " + f);
            detail = sb.ToString();
            return false;
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
