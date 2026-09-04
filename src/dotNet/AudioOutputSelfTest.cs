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
    /// and the barge-in ramp, and CI runners have no playback device. Opening DirectSound is not covered and
    /// cannot be: that is what the live smoke script is for.
    ///
    /// Why this exists at all: PlaySound's contract is "false means nothing will be heard", and every caller
    /// is expected to fall back to a bubble on false. That makes the difference between "returned null" and
    /// "threw" load-bearing, and it is invisible to every other gate.
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
                ok &= RejectsRubbish(sb);
                ok &= FadeEndsTheInput(sb);
                // ModuleKit's WavAudio is asserted in CoreTests instead: the host does not reference ModuleKit
                // (it is a library that ships inside each MODULE), so it cannot be exercised from here.
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
            ok &= Check(sb, "a 3-channel WAV is rejected", AudioOutput.DecodeModuleAudio(ThreeChannelWav()) == null);

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

        /// <summary>A structurally valid 3-channel PCM WAV, which WavAudio deliberately will not build.</summary>
        private static byte[] ThreeChannelWav()
        {
            const int channels = 3, rate = 44100, frames = 64;
            int dataBytes = frames * channels * 2;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + dataBytes);
                w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)1);
                w.Write((short)channels);
                w.Write(rate);
                w.Write(rate * channels * 2);
                w.Write((short)(channels * 2));
                w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(dataBytes);
                for (int i = 0; i < frames * channels; i++) w.Write((short)0);
                w.Flush();
                return ms.ToArray();
            }
        }

        private static bool Check(StringBuilder sb, string what, bool condition)
        {
            sb.AppendLine((condition ? "PASS: " : "FAIL: ") + what);
            return condition;
        }
    }
}
