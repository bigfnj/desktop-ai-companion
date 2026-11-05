using System;
using System.IO;
using System.Text;
using NAudio.Wave;

namespace DesktopAICompanion
{
    /// <summary>
    /// --audio-selftest: the module-audio path, asserted WITHOUT an audio device.
    ///
    /// Everything here is deliberately device-independent, because the interesting parts are the decode seam
    /// and the barge-in ramp, and CI runners have no playback device. Playing through a REAL device is not
    /// covered and cannot be: that is what the live smoke script is for. One case does open DirectSound, on a
    /// device that does not exist, because that failure is the one DirectSound reports identically on every
    /// box (see OutputFailureIsObserved).
    ///
    /// Why this exists at all: PlaySound's contract is "false means nothing will be heard", and every caller
    /// is expected to fall back to a bubble on false. That makes the difference between "returned null" and
    /// "threw" load-bearing, and it is invisible to every other gate.
    ///
    /// The shared notification sound (IHost.PlayNotificationSound, the Preferences picker) is asserted here
    /// for the same reason and one more: its three gates are ORDERED, and every wrong order still produces
    /// a silent notification, so nothing a user or another test could observe tells the two apart.
    /// </summary>
    internal static class AudioOutputSelfTest
    {
        public static bool Run()
        {
            var sb = new StringBuilder();
            bool ok = true;
            try
            {
                ok &= DecodesRealAudio(sb);
                ok &= DecodesAnMp3(sb);
                ok &= DecodesWavClips(sb);
                ok &= RejectsRubbish(sb);
                ok &= FadeEndsTheInput(sb);
                ok &= OutputFailureIsObserved(sb);
                ok &= NotificationSoundSettingRoundTrips(sb);
                ok &= NotificationGatesInOrder(sb);
                ok &= NotificationPicksAreValidatedAtPickTime(sb);
                ok &= NotificationFallsBackToTheBuiltIn(sb);
                ok &= NotificationActionsAreOfferedInOrder(sb);
                // ModuleKit's WavAudio is asserted in CoreTests ("ModuleKit wav audio", since RA-221), not here:
                // the host compiles only AtomicFile.cs and CrossSessionLock.cs out of ModuleKit, by source link
                // (F358), and WavAudio ships inside each MODULE's folder, so it cannot be reached from this exe.
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }

            sb.AppendLine("RESULT=" + (ok ? "PASS" : "FAIL"));
            Console.Write(sb.ToString());
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "dp-audio-selftest.txt"), sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
            return ok;
        }

        private static bool DecodesRealAudio(StringBuilder sb)
        {
            bool ok = true;

            // Mono at 22050 must come back resampled to 44100 AND upmixed to stereo: 1 second in, 44100
            // frames x 2 channels out. This is the single assertion proving ToMixFormat is actually applied --
            // a source at the wrong rate would otherwise play at the wrong speed, which no other test notices.
            float[] mono = Tone(22050, 1, 22050);
            byte[] monoWav = PcmWav(mono, 22050, 1);
            float[] decodedMono = AudioOutput.DecodeModuleAudio(monoWav);
            ok &= Check(sb, "22050 mono WAV decodes", decodedMono != null && decodedMono.Length > 0);
            if (decodedMono != null)
            {
                int frames = decodedMono.Length / 2;
                // Resamplers round; a couple of frames either way is fine, an octave out is not.
                ok &= Check(sb,
                    "22050 mono is resampled to 44100 and upmixed to stereo (" + frames + " frames)",
                    Math.Abs(frames - 44100) <= 64);
            }

            // Already at the mixer format: length must be preserved exactly.
            float[] stereo = Tone(44100, 2, 4410);
            float[] decodedStereo = AudioOutput.DecodeModuleAudio(PcmWav(stereo, 44100, 2));
            ok &= Check(sb, "44100 stereo WAV decodes to its own length",
                decodedStereo != null && Math.Abs(decodedStereo.Length - stereo.Length) <= 4);

            return ok;
        }

        /// <summary>
        /// The MP3 branch of DecodeModuleAudio, which no fixture here ever entered: every earlier case was a
        /// PCM WAV or junk, so the ACM path a Reminder chime takes ran under --audio-selftest exactly never
        /// (F246). The fixture is the structurally valid MPEG-frame clip SecuritySelfTest already carries;
        /// what is asserted is that the branch decodes and returns samples at the mixer format, and that the
        /// decode does not leave the reader's ACM stream to the finalizer -- the latter observed through
        /// repetition, since a leaked acmStreamOpen per call is invisible to any single-call assertion.
        /// </summary>
        private static bool DecodesAnMp3(StringBuilder sb)
        {
            bool ok = true;
            byte[] mp3 = SecuritySelfTest.ValidMp3Fixture();
            string sniff;
            ok &= Check(sb, "the MP3 fixture sniffs as an MP3 (frame sync)", Mp3Format.LooksLikeMp3(mp3, out sniff));
            float[] decoded = AudioOutput.DecodeModuleAudio(mp3);
            // The fixture is a handful of near-silent frames, so only the shape is claimed: it decoded, and
            // it came out interleaved stereo (an odd length would mean the upmix did not run).
            ok &= Check(sb, "an MP3 decodes through the ACM branch (" + (decoded != null ? decoded.Length : -1) + " samples)",
                decoded != null && decoded.Length > 0 && decoded.Length % 2 == 0);
            // Twenty decodes in a row. With the reader left undisposed each one held an ACM stream and two
            // pinned buffers until a collection; a hundred chimes over a day did the same. This cannot see
            // the handle count directly, and the F246 leak as shipped would pass it (RA-222): what it DOES
            // catch is a decode that fails part way through the batch, which is what an exhausted or wedged
            // ACM looks like, and the batch is cheap. The label says what is measured, not what is hoped.
            bool allDecoded = true;
            for (int i = 0; i < 20 && allDecoded; i++)
                allDecoded = AudioOutput.DecodeModuleAudio(mp3) != null;
            ok &= Check(sb, "twenty MP3 decodes in a row all succeed (a wedged or exhausted ACM fails part way; disposal itself is not observable here)", allDecoded);
            return ok;
        }

        /// <summary>
        /// A tick companion's WAV clips (feature 'wav'): every encoding the validator's WavFormat accepts decodes through
        /// AudioOutput.DecodeClip, the path PlayClip takes, to the mixer format at the length its frames say and with its
        /// tone intact; an encoding it refuses (ADPCM) does not decode here either, which is why it is refused. A clip
        /// that fails at runtime is reported ONCE and then stays silent. Every clip is generated here; nothing is played.
        /// </summary>
        private static bool DecodesWavClips(StringBuilder sb)
        {
            bool ok = true;
            var encodings = new[]
            {
                new[] { 1, 8 }, new[] { 1, 16 }, new[] { 1, 24 }, new[] { 1, 32 }, new[] { 3, 32 }, new[] { 3, 64 },
            };
            foreach (bool extensible in new[] { false, true })
                foreach (int[] e in encodings)
                {
                    string name = (e[0] == 1 ? "PCM " : "float ") + e[1] + "-bit" + (extensible ? " extensible" : "");
                    byte[] wav = SyntheticWave(e[0], e[1], 1, 22050, 2205, extensible);
                    string invalid;
                    bool valid = WavFormat.IsDecodableWave(wav, out invalid);
                    float[] decoded = null;
                    string failure = "";
                    try { decoded = AudioOutput.DecodeClip(wav, true); }
                    catch (Exception ex) { failure = " " + ex.GetType().Name + ": " + ex.Message; }
                    float peak = 0f;
                    if (decoded != null) foreach (float s in decoded) peak = Math.Max(peak, Math.Abs(s));
                    // 0.1 s of 22.05 kHz mono comes back as 4410 stereo frames at 44.1 kHz, the tone's 0.5 peak intact.
                    ok &= Check(sb, "wav clip: " + name + " passes the validator and decodes to the mixer format ("
                        + (decoded == null ? "no samples" + failure : decoded.Length / 2 + " frames, peak "
                        + peak.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)) + (valid ? "" : ", " + invalid) + ")",
                        valid && decoded != null && Math.Abs(decoded.Length / 2 - 4410) <= 64 && peak > 0.4f && peak < 0.6f);
                }
            float[] stereo = null;
            try { stereo = AudioOutput.DecodeClip(SyntheticWave(1, 16, 2, 48000, 4800, false), true); }
            catch (Exception) { }
            ok &= Check(sb, "wav clip: 16-bit stereo at 48 kHz is resampled to 44.1 kHz (" + (stereo == null ? -1 : stereo.Length / 2) + " frames)",
                stereo != null && Math.Abs(stereo.Length / 2 - 4410) <= 64);

            ok &= PlaysWhatTheValidatorRead(sb);
            ok &= ClipDecodeIsCapped(sb);

            byte[] adpcm = SyntheticWave(1, 16, 1, 22050, 2205, false);
            adpcm[20] = 0x02;   // format tag 1 -> 2, MS ADPCM
            string refused;
            ok &= Check(sb, "wav clip: an ADPCM clip is refused by the validator", !WavFormat.IsDecodableWave(adpcm, out refused));
            using (var output = new AudioOutput())
            {
                var lines = new System.Collections.Generic.List<string>();
                output.ClipFailureSink = lines.Add;
                string owner = AudioOutput.ClipOwnerPrefix + "type|bad|0";
                float[] first = output.ClipSamplesForTest(owner, adpcm, true);
                float[] second = output.ClipSamplesForTest(owner, adpcm, true);
                ok &= Check(sb, "wav clip: one that will not decode at runtime is silent and reported once ("
                    + lines.Count + " line(s)" + (lines.Count > 0 ? ": " + lines[0] : "") + ")",
                    (first == null || first.Length == 0) && second != null && second.Length == 0 && lines.Count == 1
                    && lines[0].IndexOf("type|bad|0 (WAV) would not decode", StringComparison.Ordinal) >= 0);
                float[] good = output.ClipSamplesForTest(owner, SyntheticWave(1, 16, 1, 22050, 2205, false), true);
                ok &= Check(sb, "wav clip: WITNESS a good clip through the same output decodes and reports nothing",
                    good != null && good.Length > 0 && lines.Count == 1);
            }
            return ok;
        }

        /// <summary>
        /// Review F1: the decoder plays the fmt and data the validator read, whatever another RIFF reader would make of the
        /// same bytes. The stand-in review's three layouts, rebuilt here from a clean 0.1 s clip: a second fmt chunk at 1 Hz
        /// (NAudio's reader keeps the LAST fmt), a 1 Hz fmt hidden behind a non-zero pad byte (NAudio skips a pad only when
        /// it is zero, so it walks one byte behind the validator and meets the hidden chunk), and a data chunk before its
        /// fmt. Each must validate and decode to exactly the clean clip's samples; under the old decoder the first two
        /// decoded at 1 Hz, 88,200 samples a frame.
        /// </summary>
        private static bool PlaysWhatTheValidatorRead(StringBuilder sb)
        {
            bool ok = true;
            byte[] clean = SyntheticWave(1, 16, 1, 22050, 2205, false);
            byte[] fmt = Slice(clean, 12, 24), data = Slice(clean, 36, clean.Length - 36);
            byte[] slow = (byte[])fmt.Clone();
            WriteUInt32(slow, 12, 1);   // the sample rate: 1 Hz
            WriteUInt32(slow, 16, 2);   // bytes a second
            float[] expected = AudioOutput.DecodeClip(clean, true);

            // The hidden layout, byte by byte from offset 36 (after the clean fmt). The validator walks junk(1), skips its
            // pad 'J', reads "unk\n"(0) and junk(40), and lands on data at 102; NAudio does not skip the 'J', reads
            // "Junk"(10), then the hidden fmt at 63 and junk(7), and lands on data at 102 too.
            var hidden = new byte[66];
            Ascii(hidden, 0, "junk"); WriteUInt32(hidden, 4, 1); hidden[8] = (byte)'x'; hidden[9] = (byte)'J';
            Ascii(hidden, 10, "unk"); hidden[13] = 0x0A;                      // its size, bytes 14-17, is 0
            Ascii(hidden, 18, "junk"); WriteUInt32(hidden, 22, 40);
            Ascii(hidden, 27, "fmt "); WriteUInt32(hidden, 31, 16); Array.Copy(slow, 8, hidden, 35, 16);
            Ascii(hidden, 51, "junk"); WriteUInt32(hidden, 55, 7);

            var layouts = new[]
            {
                new { Name = "a second fmt chunk at 1 Hz", Bytes = Riff(fmt, slow, data) },
                new { Name = "a 1 Hz fmt hidden behind a non-zero pad byte", Bytes = Riff(fmt, hidden, data) },
                new { Name = "a data chunk before its fmt", Bytes = Riff(data, fmt) },
            };
            foreach (var layout in layouts)
            {
                string invalid;
                bool valid = WavFormat.IsDecodableWave(layout.Bytes, out invalid);
                float[] decoded = null;
                string failure = "";
                try { decoded = AudioOutput.DecodeClip(layout.Bytes, true); }
                catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }
                bool same = decoded != null && expected != null && decoded.Length == expected.Length;
                for (int i = 0; same && i < decoded.Length; i++) same = decoded[i] == expected[i];
                ok &= Check(sb, "wav clip: " + layout.Name + " is refused or plays exactly what the validator read ("
                    + (valid ? "valid" : "refused: " + invalid) + ", " + (decoded == null ? failure : decoded.Length + " samples")
                    + " against " + (expected == null ? -1 : expected.Length) + ")",
                    !valid || same);
            }
            return ok;
        }

        /// <summary>
        /// The decode cap (<see cref="AudioOutput.MaximumClipDecodedSamples"/>), for both formats: a WAV that decodes to
        /// exactly the cap plays (44.1 kHz mono, so no resampling moves the count), one frame more is refused at decode and
        /// reported once, and an MP3 past it (the bundled 8 kbps fixture's frames repeated, about 353 decoded bytes a byte) is
        /// refused too. The cap-sized decode is timed and the time printed, for BACKLOG N-shimeji-host-limits-01 (decoding runs
        /// on the UI thread under the audio lock).
        /// </summary>
        private static bool ClipDecodeIsCapped(StringBuilder sb)
        {
            bool ok = true;
            int capFrames = AudioOutput.MaximumClipDecodedSamples / 2;
            byte[] atCap = SyntheticWave(1, 8, 1, 44100, capFrames, false);
            float[] decoded = null;
            string failure = "";
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try { decoded = AudioOutput.DecodeClip(atCap, true); }
            catch (Exception ex) { failure = " " + ex.GetType().Name + ": " + ex.Message; }
            timer.Stop();
            ok &= Check(sb, "wav clip: WITNESS a clip that decodes to exactly the cap plays ("
                + (decoded == null ? "no samples" + failure : decoded.Length + " samples") + ")",
                decoded != null && decoded.Length == AudioOutput.MaximumClipDecodedSamples);
            sb.AppendLine("NOTE: wav clip: a cap-sized clip (" + atCap.Length + " bytes, 8-bit mono 44.1 kHz) decoded in "
                + timer.ElapsedMilliseconds + " ms");
            decoded = null;

            byte[] overCap = SyntheticWave(1, 8, 1, 44100, capFrames + 1, false);
            failure = "";
            try { decoded = AudioOutput.DecodeClip(overCap, true); }
            catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }
            ok &= Check(sb, "wav clip: one frame past the decode cap is refused at decode (" + (decoded == null ? failure : decoded.Length + " samples") + ")",
                decoded == null && failure.IndexOf("decodes to more than the 60 s a clip may play", StringComparison.Ordinal) >= 0);
            decoded = null;
            using (var output = new AudioOutput())
            {
                var lines = new System.Collections.Generic.List<string>();
                output.ClipFailureSink = lines.Add;
                string owner = AudioOutput.ClipOwnerPrefix + "type|long|0";
                float[] first = output.ClipSamplesForTest(owner, overCap, true);
                float[] second = output.ClipSamplesForTest(owner, overCap, true);
                ok &= Check(sb, "wav clip: a clip past the decode cap is silent and reported once (" + lines.Count + " line(s)"
                    + (lines.Count > 0 ? ": " + lines[0] : "") + ")",
                    (first == null || first.Length == 0) && second != null && second.Length == 0 && lines.Count == 1
                    && lines[0].IndexOf("type|long|0 (WAV) would not decode", StringComparison.Ordinal) >= 0);
            }

            byte[] mp3 = SecuritySelfTest.ValidMp3Fixture();
            float[] shortMp3 = null;
            try { shortMp3 = AudioOutput.DecodeClip(mp3, false); }
            catch (Exception) { }
            int copies = (int)(AudioOutput.MaximumClipDecodedSamples / Math.Max(1L, shortMp3 == null ? 1L : shortMp3.Length)) + 2;
            var longMp3 = new byte[(long)mp3.Length * copies];
            for (int i = 0; i < copies; i++) Array.Copy(mp3, 0, longMp3, (long)i * mp3.Length, mp3.Length);
            failure = "";
            float[] longDecoded = null;
            timer.Restart();
            try { longDecoded = AudioOutput.DecodeClip(longMp3, false); }
            catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }
            timer.Stop();
            sb.AppendLine("NOTE: wav clip: an 8 kbps MP3 decoded up to the cap and refused in " + timer.ElapsedMilliseconds + " ms");
            // The slowest WAV path to the cap: 8 kHz upsampled (the resampler), 8-bit mono.
            byte[] slowAtCap = SyntheticWave(1, 8, 1, 8000, (int)((long)capFrames * 8000 / 44100) - 64, false);
            timer.Restart();
            float[] slowDecoded = null;
            try { slowDecoded = AudioOutput.DecodeClip(slowAtCap, true); }
            catch (Exception) { }
            timer.Stop();
            sb.AppendLine("NOTE: wav clip: an 8 kHz clip just under the cap (" + slowAtCap.Length + " bytes, "
                + (slowDecoded == null ? -1 : slowDecoded.Length) + " samples) decoded in " + timer.ElapsedMilliseconds + " ms");
            slowDecoded = null;
            ok &= Check(sb, "wav clip: an MP3 past the decode cap is refused at decode too (" + copies + " copies of a "
                + (shortMp3 == null ? -1 : shortMp3.Length) + "-sample fixture, " + longMp3.Length + " bytes: "
                + (longDecoded == null ? failure : longDecoded.Length + " samples") + ")",
                shortMp3 != null && shortMp3.Length > 0 && longDecoded == null
                && failure.IndexOf("decodes to more than the 60 s a clip may play", StringComparison.Ordinal) >= 0);
            return ok;
        }

        private static byte[] Riff(params byte[][] chunks)
        {
            int length = 12;
            foreach (byte[] c in chunks) length += c.Length;
            var b = new byte[length];
            Ascii(b, 0, "RIFF"); WriteUInt32(b, 4, (uint)(length - 8)); Ascii(b, 8, "WAVE");
            int at = 12;
            foreach (byte[] c in chunks) { Array.Copy(c, 0, b, at, c.Length); at += c.Length; }
            return b;
        }

        private static byte[] Slice(byte[] b, int at, int length)
        {
            var s = new byte[length];
            Array.Copy(b, at, s, 0, length);
            return s;
        }

        private static void Ascii(byte[] b, int at, string text)
        {
            for (int i = 0; i < text.Length; i++) b[at + i] = (byte)text[i];
        }

        private static void WriteUInt32(byte[] b, int at, uint value)
        {
            b[at] = (byte)value; b[at + 1] = (byte)(value >> 8); b[at + 2] = (byte)(value >> 16); b[at + 3] = (byte)(value >> 24);
        }

        /// <summary>
        /// A RIFF/WAVE generated here (no file from any pack): a 440 Hz tone at half scale, <paramref name="frames"/> long,
        /// as PCM (<paramref name="tag"/> 1, 8-bit unsigned, wider signed) or IEEE float (3), optionally as
        /// WAVE_FORMAT_EXTENSIBLE with that subformat. Shared with the validator's and the tick runtime's WAV checks.
        /// </summary>
        internal static byte[] SyntheticWave(int tag, int bits, int channels, int rate, int frames, bool extensible)
        {
            int blockAlign = channels * bits / 8;
            int dataBytes = frames * blockAlign;
            int fmtSize = extensible ? 40 : 16;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(4 + 8 + fmtSize + 8 + dataBytes);
                w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' });
                w.Write(fmtSize);
                w.Write((short)(extensible ? 0xFFFE : tag));
                w.Write((short)channels);
                w.Write(rate);
                w.Write(rate * blockAlign);
                w.Write((short)blockAlign);
                w.Write((short)bits);
                if (extensible)
                {
                    w.Write((short)22);                 // cbSize
                    w.Write((short)bits);               // valid bits
                    w.Write(channels == 1 ? 4 : 3);     // channel mask: centre, or left and right
                    w.Write((short)tag);                // the subformat GUID: the tag, then the standard tail
                    w.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 });
                }
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(dataBytes);
                for (int f = 0; f < frames; f++)
                {
                    double s = Math.Sin(2.0 * Math.PI * 440.0 * f / rate) * 0.5;
                    for (int c = 0; c < channels; c++)
                    {
                        if (tag == 3 && bits == 32) w.Write((float)s);
                        else if (tag == 3) w.Write(s);
                        else if (bits == 8) w.Write((byte)Math.Round(128 + s * 127));
                        else if (bits == 16) w.Write((short)Math.Round(s * short.MaxValue));
                        else if (bits == 24)
                        {
                            int v = (int)Math.Round(s * 8388607);
                            w.Write((byte)v); w.Write((byte)(v >> 8)); w.Write((byte)(v >> 16));
                        }
                        else w.Write((int)Math.Round(s * int.MaxValue));
                    }
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        /// <summary>
        /// The one device failure DirectSound ever reports arrives asynchronously, through PlaybackStopped,
        /// after TryStart has already returned true (F245). A nonexistent device GUID is the deterministic way
        /// to provoke it: DirectSoundCreate answers DSERR_NODRIVER on the playback thread on every box,
        /// including one with no audio device at all, which is what makes this assertable in a gate. The wait
        /// is bounded and pumps: NAudio posts the event through the SynchronizationContext the output was
        /// built on when there is one, and fires it on its playback thread when there is not.
        /// </summary>
        private static bool OutputFailureIsObserved(StringBuilder sb)
        {
            bool ok = true;
            using (var output = new AudioOutput())
            {
                Guid bogus = Guid.NewGuid();
                output.SetDevice(bogus.ToString());
                output.PlayTestTone();
                // The premise, and the reason a synchronous check could never see this: DirectSound opens
                // the device on its own thread, so the output either looks started on the bogus device or
                // has already been torn down by the failure that followed. Both prove the attempt ran.
                bool attempted = output.RunningDevice == bogus || output.ObservedOutputFailures > 0;
                ok &= Check(sb, "WITNESS opening a nonexistent output device was attempted", attempted);

                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (output.ObservedOutputFailures == 0 && waited.ElapsedMilliseconds < 5000)
                {
                    try { System.Windows.Forms.Application.DoEvents(); } catch { }
                    System.Threading.Thread.Sleep(20);
                }
                ok &= Check(sb, "a nonexistent output device's asynchronous failure is observed (after "
                    + waited.ElapsedMilliseconds + " ms)", output.ObservedOutputFailures > 0);
                ok &= Check(sb, "...the dead output is torn down rather than left 'started'",
                    !output.IsOutputRunning);
                ok &= Check(sb, "...and the chosen device is remembered as failed, so the next sound goes to the default",
                    output.ChosenDeviceFailed);

                // Where the NEXT sound would go. Not played, so the dev box does not beep during the gate;
                // on a runner with no device the default fails the same asynchronous way and that is fine
                // too. What must not happen is re-opening the device that just died.
                output.EnsureStartedForTest();
                ok &= Check(sb, "the next sound does not re-open the device that just died",
                    output.RunningDevice != bogus);

                // WITNESS: a device change from Preferences is a fresh chance whatever happened before,
                // including re-applying the very same device.
                output.SetDevice(bogus.ToString());
                ok &= Check(sb, "WITNESS re-applying a device from Preferences clears the failure latch",
                    !output.ChosenDeviceFailed);
            }
            return ok;
        }

        private static bool RejectsRubbish(StringBuilder sb)
        {
            bool ok = true;
            ok &= Check(sb, "null is rejected", AudioOutput.DecodeModuleAudio(null) == null);
            ok &= Check(sb, "a 4-byte buffer is rejected", AudioOutput.DecodeModuleAudio(new byte[4]) == null);
            ok &= Check(sb, "random bytes are rejected",
                AudioOutput.DecodeModuleAudio(Encoding.ASCII.GetBytes("not audio at all, honestly")) == null);

            // RIFF header with the wrong form type: sniffed as not-WAV rather than handed to a reader.
            byte[] wrongForm = PcmWav(Tone(44100, 1, 128), 44100, 1);
            wrongForm[11] = (byte)'X';   // "WAVE" -> "WAVX"
            ok &= Check(sb, "RIFF with a non-WAVE form type is rejected",
                AudioOutput.DecodeModuleAudio(wrongForm) == null);

            // More than two channels: rejected explicitly, so the caller gets false and can show a bubble,
            // rather than the mixer throwing into a silent catch.
            // PcmWav with channels = 3 writes the same bytes a dedicated three-channel builder did (the
            // channel count, byte rate and block align are all derived from the argument), so the fixture
            // is the shared writer rather than a second WAV layout that could drift from it (F248).
            ok &= Check(sb, "a 3-channel WAV is rejected",
                AudioOutput.DecodeModuleAudio(PcmWav(new float[64 * 3], 44100, 3)) == null);

            // Over the encoded cap. Built as a bare oversized RIFF header so the test does not allocate 16 MB
            // of samples just to be told no.
            var oversized = new byte[AudioOutput.MaximumModuleAudioBytes + 1];
            oversized[0] = (byte)'R'; oversized[1] = (byte)'I'; oversized[2] = (byte)'F'; oversized[3] = (byte)'F';
            oversized[8] = (byte)'W'; oversized[9] = (byte)'A'; oversized[10] = (byte)'V'; oversized[11] = (byte)'E';
            ok &= Check(sb, "an over-cap buffer is rejected", AudioOutput.DecodeModuleAudio(oversized) == null);

            return ok;
        }

        /// <summary>A 16-bit PCM WAV from interleaved floats. Written here rather than reused from ModuleKit's
        /// WavAudio because the host does not reference ModuleKit (that library ships inside each MODULE).
        /// Keeping the fixture independent also means a bug in that helper cannot make the host's decoder look
        /// correct -- WavAudio is asserted separately in CoreTests.</summary>
        private static byte[] PcmWav(float[] interleaved, int sampleRate, int channels)
        {
            if (interleaved == null) return null;
            int dataBytes = interleaved.Length * 2;
            int blockAlign = channels * 2;
            using (var ms = new MemoryStream(44 + dataBytes))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + dataBytes);
                w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)1);
                w.Write((short)channels);
                w.Write(sampleRate);
                w.Write(sampleRate * blockAlign);
                w.Write((short)blockAlign);
                w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(dataBytes);
                foreach (float s in interleaved)
                {
                    float c = s > 1f ? 1f : (s < -1f ? -1f : s);
                    w.Write((short)Math.Round(c * short.MaxValue));
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        private static bool FadeEndsTheInput(StringBuilder sb)
        {
            bool ok = true;
            WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

            // A long buffer that would otherwise keep playing for a second: after FadeOutAndEnd it must ramp
            // out and then report end-of-stream, which is how NAudio drops the input. Read in small chunks so
            // the ramp is exercised across more than one call.
            var source = new AudioOutput.CachedSampleProvider(new float[44100 * 2], format, 0);
            source.FadeOutAndEnd();
            int total = 0;
            var buffer = new float[128];
            for (int i = 0; i < 100; i++)
            {
                int n = source.Read(buffer.AsSpan());
                total += n;
                if (n == 0) break;
            }
            ok &= Check(sb, "a faded input ends quickly instead of playing on (" + total + " samples)",
                total > 0 && total <= 441 * 2 + 128);
            ok &= Check(sb, "a faded input then reports end-of-stream", source.Read(buffer.AsSpan()) == 0);

            // A chunk smaller than the ramp must still terminate rather than looping forever.
            var small = new AudioOutput.CachedSampleProvider(new float[44100 * 2], format, 0);
            small.FadeOutAndEnd();
            var tiny = new float[16];
            int guard = 0;
            while (small.Read(tiny.AsSpan()) > 0 && guard < 1000) guard++;
            ok &= Check(sb, "the ramp terminates even when read in chunks smaller than it", guard < 1000);

            // Untouched providers still play in full: the fade must not have changed normal playback.
            var normal = new AudioOutput.CachedSampleProvider(new float[1000], format, 0);
            int played = 0;
            var chunk = new float[256];
            int n2;
            while ((n2 = normal.Read(chunk.AsSpan())) > 0) played += n2;
            ok &= Check(sb, "an un-faded input still plays to completion", played == 1000);
            return ok;
        }

        /// <summary>
        /// The chosen-notification-sound setting, through the REAL store and the real accessors rather
        /// than through a stub, because the interesting cases are all normalization: what an older file
        /// with no such key reads as, and what happens to a path the setting must not keep.
        /// </summary>
        private static bool NotificationSoundSettingRoundTrips(StringBuilder sb)
        {
            bool ok = true;
            string root = null;
            try
            {
                root = DesktopAICompanion.Plugins.SelfTestScratch.Create("audio");
                string settings = Path.Combine(root, "settings.json");

                ok &= Check(sb, "a fresh document defaults to the built-in sound",
                    AppSettingsDocument.CreateDefault().NotificationSoundPath == "");

                // Absent, written as RAW JSON with the key missing rather than as a document with the
                // field left null. Those are different bytes on disk, and "absent" is the state every
                // settings file already out there is in -- a test that only covers null would pass on a
                // build that read a missing key as something else entirely.
                File.WriteAllText(settings, "{\"schemaVersion\":2}", new UTF8Encoding(false));
                ok &= Check(sb, "a settings file with no notificationSoundPath reads as the built-in",
                    new AppSettingsStore(settings, null).Load().NotificationSoundPath == "");

                string chosen = Path.Combine(root, "chime.wav");
                File.WriteAllBytes(chosen, PcmWav(Tone(44100, 1, 4410), 44100, 1));

                var store = new AppSettingsStore(settings, null);
                AppSettingsDocument doc = store.Load();
                doc.NotificationSoundPath = "  " + chosen + "  ";   // trimmed on normalize
                ok &= Check(sb, "a document carrying a chosen sound saves", store.Save(doc));
                ok &= Check(sb, "the chosen sound round-trips through save/load",
                    new AppSettingsStore(settings, null).Load().NotificationSoundPath == chosen);

                // A relative path resolves against the process working directory, which for a
                // shell-launched app is wherever the user happened to be standing. Refused back to the
                // built-in, and asserted here because nothing at play time could tell the difference
                // between "resolved somewhere odd" and "the file is missing".
                var store2 = new AppSettingsStore(settings, null);
                AppSettingsDocument relative = store2.Load();
                relative.NotificationSoundPath = "chime.wav";
                ok &= Check(sb, "a document carrying a relative path saves", store2.Save(relative));
                ok &= Check(sb, "a relative path falls back to the built-in",
                    new AppSettingsStore(settings, null).Load().NotificationSoundPath == "");

                // The same trip through LocalData, which is the only way the app ever touches this.
                var data = new LocalData(new AppSettingsStore(settings, null));
                ok &= Check(sb, "LocalData starts on the built-in", data.GetNotificationSoundPath() == "");
                data.SetNotificationSoundPath(chosen);
                ok &= Check(sb, "LocalData writes the chosen sound through to disk",
                    new LocalData(new AppSettingsStore(settings, null)).GetNotificationSoundPath() == chosen);
                data.SetNotificationSoundPath("");
                ok &= Check(sb, "clearing it returns to the built-in",
                    new LocalData(new AppSettingsStore(settings, null)).GetNotificationSoundPath() == "");
            }
            catch (Exception ex)
            {
                ok = Check(sb, "the notification-sound setting round-trip ran (" +
                    ex.GetType().Name + ": " + ex.Message + ")", false);
            }
            finally
            {
                // ASSERTED, not noted (R-054): this flag loads no module, so no collectible ALC keeps a handle
                // on the scratch, and a refused release here would be a real leaked handle rather than the
                // benign case F247 recorded for the module-loading flags, whose NOTE line no grader reads.
                string detail = "";
                bool released = root == null || DesktopAICompanion.Plugins.SelfTestScratch.TryRelease(root, out detail);
                ok &= Check(sb, "the audio scratch was released (no leaked handle)" + (released ? "" : " -- " + detail), released);
            }
            return ok;
        }

        /// <summary>
        /// The three layers PlayNotificationSound honours, and the ORDER it honours them in.
        ///
        /// Every case here passes a null output, so a build that consulted the device first would answer
        /// "no device" to all of them and still, correctly, never play -- which is why the outcome and not
        /// the silence is what gets asserted. A bool return cannot tell a muted user from a dead sound
        /// card, and neither can the person holding the laptop.
        /// </summary>
        private static bool NotificationGatesInOrder(StringBuilder sb)
        {
            bool ok = true;
            string root = null;
            try
            {
                root = DesktopAICompanion.Plugins.SelfTestScratch.Create("audio");
                var data = new LocalData(new AppSettingsStore(Path.Combine(root, "settings.json"), null));

                ok &= Check(sb, "no settings at all means no notification",
                    NotificationSound.Play(null, null, "module") == NotificationOutcome.NoSettings);

                data.SetNotificationSoundsEnabled(false);
                data.SetVolume(1.0);
                ok &= Check(sb, "the notificationSounds switch being off refuses first",
                    NotificationSound.Play(data, null, "module") == NotificationOutcome.SwitchedOff);

                data.SetNotificationSoundsEnabled(true);
                data.SetVolume(0.0);
                ok &= Check(sb, "master volume 0 refuses",
                    NotificationSound.Play(data, null, "module") == NotificationOutcome.Muted);

                // The ORDER of the two setting gates (RA-223): the switch is consulted before the volume, so a
                // user who turned notification sounds off AND has the volume at 0 is told SwitchedOff, not
                // Muted. Swapping the two checks in Play passed every case above; this one answers Muted then.
                data.SetNotificationSoundsEnabled(false);
                ok &= Check(sb, "with the switch off and the volume at 0, the switch answers first (switch before volume)",
                    NotificationSound.Play(data, null, "module") == NotificationOutcome.SwitchedOff);
                data.SetNotificationSoundsEnabled(true);

                // Both gates open, so it gets as far as the device it has not got. Without this case the
                // two above pass for a Play() that refuses everything it is ever handed.
                data.SetVolume(0.5);
                ok &= Check(sb, "with both settings open it reaches the output device",
                    NotificationSound.Play(data, null, "module") == NotificationOutcome.NoDevice);
            }
            catch (Exception ex)
            {
                ok = Check(sb, "the notification gate order ran (" +
                    ex.GetType().Name + ": " + ex.Message + ")", false);
            }
            finally
            {
                // ASSERTED, not noted (R-054): this flag loads no module, so no collectible ALC keeps a handle
                // on the scratch, and a refused release here would be a real leaked handle rather than the
                // benign case F247 recorded for the module-loading flags, whose NOTE line no grader reads.
                string detail = "";
                bool released = root == null || DesktopAICompanion.Plugins.SelfTestScratch.TryRelease(root, out detail);
                ok &= Check(sb, "the audio scratch was released (no leaked handle)" + (released ? "" : " -- " + detail), released);
            }
            return ok;
        }

        /// <summary>
        /// A file that is not playable audio is refused WHERE THE USER IS -- in the Preferences picker --
        /// and not at play time, where the only report available is silence, days later, from a reminder
        /// nobody is standing next to.
        /// </summary>
        private static bool NotificationPicksAreValidatedAtPickTime(StringBuilder sb)
        {
            bool ok = true;
            string root = null;
            try
            {
                root = DesktopAICompanion.Plugins.SelfTestScratch.Create("audio");
                string rubbish = Path.Combine(root, "not-really.wav");
                File.WriteAllBytes(rubbish, Encoding.ASCII.GetBytes("an audio extension and nothing else"));
                string real = Path.Combine(root, "real.wav");
                File.WriteAllBytes(real, PcmWav(Tone(44100, 2, 4410), 44100, 2));
                string empty = Path.Combine(root, "empty.wav");
                File.WriteAllBytes(empty, new byte[0]);
                string gone = Path.Combine(root, "never-existed.wav");

                ok &= Check(sb, "a real WAV passes pick-time validation", NotificationSound.Validate(real) == null);
                ok &= Check(sb, "a file with an audio extension and no audio in it is refused",
                    NotificationSound.Validate(rubbish) != null);
                ok &= Check(sb, "an empty file is refused", NotificationSound.Validate(empty) != null);
                ok &= Check(sb, "a missing file is refused", NotificationSound.Validate(gone) != null);

                // Guarded rather than assumed: --audio-selftest exits long before Program.MyData exists,
                // and if that ever changes the call below would write a scratch path into the real user's
                // settings.json. It must fail here instead of quietly editing someone's preferences.
                bool headless = Program.MyData == null;
                ok &= Check(sb, "the self-test runs before settings exist, so nothing here can persist", headless);
                if (headless)
                {
                    string refused = DesktopAICompanion.Wpf.OptionsShell.ApplyNotificationSoundChoice(rubbish);
                    ok &= Check(sb, "the picker refuses an undecodable file: " + refused,
                        refused != null && refused.StartsWith("✗", StringComparison.Ordinal) &&
                        refused.IndexOf("can play", StringComparison.OrdinalIgnoreCase) >= 0);

                    // The same call with a real file gets PAST validation and stops at the next step
                    // instead. Without this the check above is satisfied by a picker that refuses
                    // everything, which is a different bug wearing the same green tick.
                    string accepted = DesktopAICompanion.Wpf.OptionsShell.ApplyNotificationSoundChoice(real);
                    ok &= Check(sb, "...and lets a real file through to the settings step: " + accepted,
                        accepted != null &&
                        accepted.IndexOf("settings are unavailable", StringComparison.OrdinalIgnoreCase) >= 0);
                }
            }
            catch (Exception ex)
            {
                ok = Check(sb, "pick-time validation ran (" +
                    ex.GetType().Name + ": " + ex.Message + ")", false);
            }
            finally
            {
                // ASSERTED, not noted (R-054): this flag loads no module, so no collectible ALC keeps a handle
                // on the scratch, and a refused release here would be a real leaked handle rather than the
                // benign case F247 recorded for the module-loading flags, whose NOTE line no grader reads.
                string detail = "";
                bool released = root == null || DesktopAICompanion.Plugins.SelfTestScratch.TryRelease(root, out detail);
                ok &= Check(sb, "the audio scratch was released (no leaked handle)" + (released ? "" : " -- " + detail), released);
            }
            return ok;
        }

        /// <summary>
        /// The built-in chime, and the two ways it gets used: nothing chosen, and a choice that has
        /// stopped being playable. Asserted on the samples themselves because there is no device here and
        /// because "it played" would not have caught a chime that is 0.75 s of silence.
        /// </summary>
        private static bool NotificationFallsBackToTheBuiltIn(StringBuilder sb)
        {
            bool ok = true;
            float[] builtIn = NotificationSound.BuiltInChime(44100, 2);
            ok &= Check(sb, "the built-in chime is 0.75 s of stereo at the mixer rate (" + builtIn.Length + " samples)",
                builtIn.Length == (int)(44100 * 0.75) * 2);

            float peak = 0f;
            for (int i = 0; i < builtIn.Length; i++) { float a = Math.Abs(builtIn[i]); if (a > peak) peak = a; }
            ok &= Check(sb, "the built-in chime is audible and leaves the mixer headroom (peak " +
                peak.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + ")",
                peak > 0.4f && peak <= 0.8f);
            ok &= Check(sb, "the built-in chime starts and ends at silence (no click)",
                builtIn.Length > 0 && Math.Abs(builtIn[0]) < 0.0001f &&
                Math.Abs(builtIn[builtIn.Length - 1]) < 0.0001f);

            ok &= Check(sb, "an empty path means no chosen bytes", NotificationSound.ReadChosen("") == null);
            ok &= Check(sb, "nothing chosen plays the built-in",
                ReferenceEquals(NotificationSound.Resolve(null, builtIn), builtIn));
            // A chosen file the picker accepted can still rot: renamed, re-saved as something else, or
            // replaced by a sync client. Silence would be the wrong answer to that -- the notification's
            // whole job is to be heard, and the built-in is how the user finds out something changed.
            ok &= Check(sb, "a chosen file that no longer decodes falls back to the built-in",
                ReferenceEquals(
                    NotificationSound.Resolve(Encoding.ASCII.GetBytes("not audio any more"), builtIn), builtIn));

            // The other direction, or all three above are satisfied by a Resolve that ignores the choice.
            float[] custom = NotificationSound.Resolve(PcmWav(Tone(44100, 2, 4410), 44100, 2), builtIn);
            ok &= Check(sb, "a decodable chosen file is what plays, not the built-in",
                custom != null && !ReferenceEquals(custom, builtIn) && Math.Abs(custom.Length - 4410 * 2) <= 4);
            return ok;
        }

        /// <summary>
        /// The Sound card's buttons. Both halves of the choice have to be REACHABLE — a picker with no way
        /// back to the default is a setting the user cannot undo — and "Choose" has to sit above "Test
        /// sound" so the card reads in the order the task is performed.
        ///
        /// Here rather than in the options-shell self-test because what it pins is this feature's UI
        /// contract, not the renderer's, and because a label or an ordering is exactly the kind of thing a
        /// later edit reshuffles without noticing. Asserted on POSITION, not presence: three buttons all
        /// existing in the wrong order is the failure this is for.
        /// </summary>
        private static bool NotificationActionsAreOfferedInOrder(StringBuilder sb)
        {
            bool ok = true;
            try
            {
                DesktopAICompanion.Modules.OptionsPane prefs =
                    DesktopAICompanion.Wpf.OptionsShell.BuildPreferencesPane();
                var sound = new System.Collections.Generic.List<string>();
                if (prefs != null && prefs.Actions != null)
                    foreach (DesktopAICompanion.Modules.PaneAction a in prefs.Actions)
                        if (a != null && string.Equals(a.Group, "Sound", StringComparison.Ordinal))
                            sound.Add(a.Label ?? "");

                int choose = sound.FindIndex(delegate (string l) { return l.StartsWith("Choose notification sound", StringComparison.Ordinal); });
                int builtIn = sound.FindIndex(delegate (string l) { return l.IndexOf("built-in", StringComparison.OrdinalIgnoreCase) >= 0; });
                int test = sound.IndexOf("Test sound");
                int device = sound.FindIndex(delegate (string l) { return l.IndexOf("output device", StringComparison.OrdinalIgnoreCase) >= 0; });

                ok &= Check(sb, "the Sound card offers a way to choose a notification sound", choose >= 0);
                ok &= Check(sb, "...and a way back to the built-in", builtIn >= 0);
                ok &= Check(sb, "...with Choose above Test sound", choose >= 0 && test > choose);
                // The 440 Hz tone keeps a button of its own. It ignores mute and the master volume by
                // design, which is the one thing "Test sound" must no longer do now that it previews the
                // real notification sound -- two jobs, two buttons, neither able to answer for the other.
                ok &= Check(sb, "...and the output-device tone is still reachable separately",
                    device >= 0 && device != test);

                bool shows = false;
                if (prefs != null && prefs.Schema != null)
                    foreach (DesktopAICompanion.Modules.SettingField f in prefs.Schema)
                        if (f != null && f.Id == "notificationSoundInfo" &&
                            f.Kind == DesktopAICompanion.Modules.SettingKind.Info) shows = true;
                ok &= Check(sb, "the pane says which notification sound is currently chosen", shows);
            }
            catch (Exception ex)
            {
                ok = Check(sb, "the preferences actions could be built (" +
                    ex.GetType().Name + ": " + ex.Message + ")", false);
            }
            return ok;
        }

        /// <summary>Interleaved sine, loud enough that a decode failure would not look like success.</summary>
        private static float[] Tone(int sampleRate, int channels, int frames)
        {
            var buf = new float[frames * channels];
            for (int f = 0; f < frames; f++)
            {
                float s = (float)(Math.Sin(2.0 * Math.PI * 440.0 * f / sampleRate) * 0.5);
                for (int c = 0; c < channels; c++) buf[f * channels + c] = s;
            }
            return buf;
        }

        private static bool Check(StringBuilder sb, string what, bool condition)
        {
            sb.AppendLine((condition ? "PASS: " : "FAIL: ") + what);
            return condition;
        }
    }
}
