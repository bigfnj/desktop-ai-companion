using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// SoundBaker resolves each clip NAME once, misses included.
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
    /// versions, and it needs no external tool.
    /// </summary>
    public static class SoundResolveSelfTest
    {
        public static bool Run(out string detail)
        {
            var failures = new List<string>();
            string root = Path.Combine(Path.GetTempPath(), "dp-sound-resolve-" + Guid.NewGuid().ToString("N"));
            try
            {
                // A clip buried a couple of levels down, which is the case the recursive search exists for.
                string nested = Path.Combine(root, "img", "character");
                Directory.CreateDirectory(nested);
                File.WriteAllBytes(Path.Combine(nested, "yell.wav"), new byte[] { 0x52, 0x49, 0x46, 0x46 });

                var baker = new SoundBaker(root);
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
            }
            catch (Exception ex) { failures.Add("threw: " + ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }

            var sb = new StringBuilder();
            sb.AppendLine("sound-resolve self-test: one recursive scan per clip name, misses included");
            if (failures.Count == 0)
            {
                sb.Append("  12 hits + 8 misses over 2 names cost 2 scans");
                detail = sb.ToString();
                return true;
            }
            foreach (string f in failures) sb.AppendLine("  FAIL " + f);
            detail = sb.ToString();
            return false;
        }
    }
}
