using System;
using System.IO;

namespace DesktopAICompanion.ModuleKit
{
    /// <summary>
    /// Wrap raw PCM samples in a WAV container, so a module can hand them to <c>IHost.PlaySound</c>.
    ///
    /// This lives in ModuleKit rather than in the contract on purpose. <c>PlaySound</c> takes a
    /// self-describing container (WAV or MP3) instead of a float array plus a sample rate plus a channel
    /// count, which keeps interleaving order, channel semantics and range clamping out of a frozen ABI
    /// forever, and means a future container can be added host-side with no contract change. It also avoids
    /// aliasing: a float[] handed across would be read by the mixer thread for the life of playback, so a
    /// module reusing its synthesis buffer would be audible as a seam.
    ///
    /// The cost of that choice is a header write, which is this file. Most engines never need it -- Windows
    /// speech synthesis and Piper already emit WAV -- but a neural model that outputs bare float samples does.
    /// </summary>
    public static class WavAudio
    {
        /// <summary>Largest buffer worth building; the host rejects anything over its own cap anyway.</summary>
        private const int MaximumSamples = 64 * 1024 * 1024 / 2;

        /// <summary>
        /// A 16-bit PCM WAV from interleaved float samples in -1..1 (values outside are clamped, not wrapped,
        /// because wrapping turns a loud passage into noise). Returns null rather than throwing on bad input.
        /// </summary>
        public static byte[] FromPcm(float[] interleaved, int sampleRate, int channels)
        {
            if (interleaved == null || interleaved.Length == 0) return null;
            if (sampleRate < 8000 || sampleRate > 192000) return null;
            if (channels < 1 || channels > 2) return null;
            if (interleaved.Length > MaximumSamples) return null;

            var pcm = new short[interleaved.Length];
            for (int i = 0; i < interleaved.Length; i++)
            {
                float s = interleaved[i];
                if (s > 1f) s = 1f;
                else if (s < -1f) s = -1f;
                pcm[i] = (short)Math.Round(s * short.MaxValue);
            }
            return FromPcm(pcm, sampleRate, channels);
        }

        /// <summary>A 16-bit PCM WAV from interleaved 16-bit samples.</summary>
        public static byte[] FromPcm(short[] interleaved, int sampleRate, int channels)
        {
            if (interleaved == null || interleaved.Length == 0) return null;
            if (sampleRate < 8000 || sampleRate > 192000) return null;
            if (channels < 1 || channels > 2) return null;
            if (interleaved.Length > MaximumSamples) return null;

            int dataBytes = interleaved.Length * 2;
            int blockAlign = channels * 2;
            using (var ms = new MemoryStream(44 + dataBytes))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + dataBytes);              // RIFF chunk size = everything after this field
                w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' });
                w.Write(16);                          // PCM fmt chunk length
                w.Write((short)1);                    // WAVE_FORMAT_PCM
                w.Write((short)channels);
                w.Write(sampleRate);
                w.Write(sampleRate * blockAlign);     // byte rate
                w.Write((short)blockAlign);
                w.Write((short)16);                   // bits per sample
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(dataBytes);
                foreach (short sample in interleaved) w.Write(sample);
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
