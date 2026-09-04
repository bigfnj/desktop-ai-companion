using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DesktopAICompanion
{
    /// <summary>
    /// Host-owned audio output (B1): one shared mixer + output device that plays the pet's animation sounds
    /// today and the AI speech (TTS) engine later, through a single path. Pet MP3s are decoded once (ACM,
    /// the OS codec, so no native binary ships) into a cached float buffer at the mixer format; each play
    /// adds a volume-wrapped, optionally-looping input, so distinct sounds overlap, per-sound volume works,
    /// and speech can duck SFX once TTS arrives. Device errors are swallowed — a box with no audio device
    /// stays silent and never throws into the engine.
    ///
    /// Output is DirectSound (B1.5): it plays through a chosen playback device (<see cref="SetDevice"/>),
    /// enumerated with full friendly names via <see cref="EnumerateDevices"/> for the Preferences picker.
    /// DirectSound was chosen over WASAPI because WASAPI's package needs a Win10-versioned TFM that drags a
    /// ~25 MB Windows SDK projection into the payload; DirectSound needs no TFM bump and no native binary.
    /// NAudio (Core + WinMM + Dmo) is a base dependency again as of B1 (it left in S2 on the false premise
    /// that no pet shipped audio; every bundled pet does).
    ///
    /// Threading: the canonical NAudio "fire-and-forget" pattern — the output callback thread reads the mixer
    /// while callers add inputs; <see cref="MixingSampleProvider"/> guards its own source list, the decode
    /// cache + output lifecycle are guarded here.
    /// </summary>
    internal sealed class AudioOutput : IDisposable
    {
        private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

        private readonly object _sync = new object();
        private readonly Dictionary<byte[], float[]> _cache =
            new Dictionary<byte[], float[]>(ReferenceComparer.Instance);
        /// <summary>Owner tag for the pet engine's own sounds (animation SFX + the test tone), so a module's
        /// StopSound can never silence them.</summary>
        internal const string EngineOwner = "";

        /// <summary>Biggest encoded buffer a module may hand us (~95 s of 16-bit 44.1k stereo WAV).</summary>
        internal const int MaximumModuleAudioBytes = 16 * 1024 * 1024;
        /// <summary>...and a decoded-length cap too, because a byte cap cannot catch a decompression bomb.</summary>
        private const int MaximumModuleDecodedSamples = 60 * 44100 * 2;

        // Live mixer inputs, tagged by owner, so StopSound can cut one module's audio without touching the
        // pet's. Its OWN lock, never _sync: MixerInputEnded fires on the output callback thread from inside
        // MixingSampleProvider's own source lock, while callers hold _sync and then take that same lock via
        // AddMixerInput. Sharing a lock across those two orders is a textbook ABBA deadlock.
        private readonly object _liveSync = new object();
        private readonly Dictionary<ISampleProvider, LiveInput> _live = new Dictionary<ISampleProvider, LiveInput>();
        private sealed class LiveInput
        {
            public string Owner;
            public CachedSampleProvider Source;
        }

        private MixingSampleProvider _mixer;
        private DirectSoundOut _output;
        private Guid _deviceId = Guid.Empty;   // Guid.Empty = the default playback device ("Primary Sound Driver")
        private float[] _testTone;
        private bool _started;
        private bool _unavailable;
        private bool _disposed;

        /// <summary>Playback devices as (device GUID string, friendly name); the first is the default.</summary>
        public static IReadOnlyList<KeyValuePair<string, string>> EnumerateDevices()
        {
            var list = new List<KeyValuePair<string, string>>();
            try
            {
                foreach (DirectSoundDeviceInfo d in DirectSoundOut.Devices)
                    list.Add(new KeyValuePair<string, string>(d.Guid.ToString(), d.Description ?? ""));
            }
            catch { }
            return list;
        }

        /// <summary>Route audio to the given device GUID (empty/invalid = default). Takes effect on the next play.</summary>
        public void SetDevice(string deviceId)
        {
            Guid g;
            if (string.IsNullOrEmpty(deviceId) || !Guid.TryParse(deviceId, out g)) g = Guid.Empty;
            lock (_sync)
            {
                if (_disposed || g == _deviceId) return;
                _deviceId = g;
                DisposeOutput();        // rebuild on the new device the next time something plays
                _unavailable = false;   // give the new device a fresh chance
            }
        }

        /// <summary>Play an MP3 (raw bytes) at <paramref name="volume"/> (0..1), repeating it
        /// <paramref name="loop"/> extra times (0 = play once). Silent + safe when volume is 0 or no device.</summary>
        public void Play(byte[] mp3, int loop, double volume)
        {
            if (mp3 == null || mp3.Length == 0 || volume <= 0.0) return;   // 0 volume = muted (pre-S2 behavior)
            lock (_sync)
            {
                if (_disposed || !EnsureStarted()) return;
                float[] samples;
                if (!_cache.TryGetValue(mp3, out samples))
                {
                    try { samples = Decode(mp3); }
                    catch { samples = null; }
                    _cache[mp3] = samples;   // cache even null so an undecodable sound isn't retried each trigger
                }
                if (samples == null || samples.Length == 0) return;
                AddInput(samples, Math.Max(0, Math.Min(20, loop)), (float)Math.Max(0.0, Math.Min(1.0, volume)), EngineOwner);
            }
        }

        /// <summary>
        /// Play a module's sound through this same mixer and device: one volume, one device picker, one place
        /// audio comes from. <paramref name="audio"/> is a self-describing container (WAV or MP3) so the ABI
        /// never has to name a sample format. False means nothing will be heard -- no device, muted,
        /// undecodable, over the caps -- which is what lets a caller fall back to a bubble.
        ///
        /// Deliberately NOT cached. <see cref="_cache"/> is keyed by byte[] REFERENCE identity and cleared only
        /// in Dispose, so caching TTS would retain every line the pet ever spoke, plus a mixer-format buffer
        /// roughly 7x larger than the input. Pinned by a source-text invariant.
        /// </summary>
        public bool PlayOwned(string owner, byte[] audio, double volume)
        {
            if (audio == null || audio.Length == 0 || volume <= 0.0) return false;
            if (audio.Length > MaximumModuleAudioBytes) return false;
            lock (_sync)
            {
                // Device first: a box with no output should not pay for a decode before finding that out.
                if (_disposed || !EnsureStarted()) return false;
                float[] samples = DecodeModuleAudio(audio);
                if (samples == null || samples.Length == 0) return false;
                AddInput(samples, 0, (float)Math.Max(0.0, Math.Min(1.0, volume)), owner ?? "");
                return true;
            }
        }

        /// <summary>Cut everything this owner is playing. True when something was actually stopped.</summary>
        public bool StopOwned(string owner)
        {
            string key = owner ?? "";
            var cut = new List<CachedSampleProvider>();
            lock (_liveSync)
            {
                var dead = new List<ISampleProvider>();
                foreach (KeyValuePair<ISampleProvider, LiveInput> pair in _live)
                    if (string.Equals(pair.Value.Owner, key, StringComparison.OrdinalIgnoreCase))
                    { dead.Add(pair.Key); cut.Add(pair.Value.Source); }
                foreach (ISampleProvider k in dead) _live.Remove(k);
            }
            // Outside the lock: the ramp only flips a flag, but NAudio may call back into us while it drains.
            foreach (CachedSampleProvider source in cut) source.FadeOutAndEnd();
            return cut.Count > 0;
        }

        /// <summary>Cut every owner except the pet engine -- used when the user switches speech off, since a
        /// module has no way to notice a settings change on its own.</summary>
        public bool StopAllExcept(string keepOwner)
        {
            string keep = keepOwner ?? "";
            var owners = new List<string>();
            lock (_liveSync)
                foreach (KeyValuePair<ISampleProvider, LiveInput> pair in _live)
                    if (!string.Equals(pair.Value.Owner, keep, StringComparison.OrdinalIgnoreCase) &&
                        !owners.Contains(pair.Value.Owner))
                        owners.Add(pair.Value.Owner);
            bool any = false;
            foreach (string owner in owners) any |= StopOwned(owner);
            return any;
        }

        /// <summary>Play a short test tone through the current device at a fixed audible level (the Preferences
        /// "Test sound" button). Ignores the mute setting — the user explicitly asked to hear the device.</summary>
        public void PlayTestTone()
        {
            lock (_sync)
            {
                if (_disposed || !EnsureStarted()) return;
                if (_testTone == null) _testTone = MakeTone(440.0, 0.4);
                AddInput(_testTone, 0, 0.5f, EngineOwner);
            }
        }

        private void AddInput(float[] samples, int loops, float volume, string owner)
        {
            var cached = new CachedSampleProvider(samples, MixFormat, loops);
            var scaled = new VolumeSampleProvider(cached) { Volume = volume };
            // Register the WRAPPER: that is the instance MixerInputEnded hands back, not the inner provider.
            lock (_liveSync) _live[scaled] = new LiveInput { Owner = owner ?? "", Source = cached };
            try { _mixer.AddMixerInput(scaled); }
            catch { lock (_liveSync) _live.Remove(scaled); }
        }

        /// <summary>
        /// Runs on the output callback thread, INSIDE MixingSampleProvider's source lock, and NAudio removes
        /// the input itself immediately after this returns. So: bookkeeping only. Never call
        /// Add/RemoveMixerInput here (the by-index removal that follows would drop the wrong element) and never
        /// take _sync (lock-order inversion against every caller).
        /// </summary>
        private void OnMixerInputEnded(object sender, SampleProviderEventArgs e)
        {
            if (e == null || e.SampleProvider == null) return;
            lock (_liveSync) _live.Remove(e.SampleProvider);
        }

        private bool EnsureStarted()
        {
            if (_started) return true;
            if (_unavailable) return false;
            if (TryStart(_deviceId)) return true;
            if (_deviceId != Guid.Empty && TryStart(Guid.Empty)) return true;   // chosen device gone -> default
            _unavailable = true;   // no usable device: stay silent, don't retry on every trigger
            return false;
        }

        private bool TryStart(Guid device)
        {
            MixingSampleProvider mixer = null;
            DirectSoundOut output = null;
            try
            {
                mixer = new MixingSampleProvider(MixFormat) { ReadFully = true };   // keep running when idle
                mixer.MixerInputEnded += OnMixerInputEnded;
                output = new DirectSoundOut(device, 100);
                output.Init(mixer.ToWaveProvider16());   // 16-bit PCM: universally accepted by DirectSound
                output.Play();
                _mixer = mixer;
                _output = output;
                _started = true;
                return true;
            }
            catch
            {
                if (output != null) { try { output.Dispose(); } catch { } }
                return false;
            }
        }

        private static float[] MakeTone(double frequency, double seconds)
        {
            int frames = (int)(MixFormat.SampleRate * seconds);
            int fade = Math.Min(frames / 8, MixFormat.SampleRate / 100);   // ~10ms fade in/out, no clicks
            var buf = new float[frames * MixFormat.Channels];
            for (int i = 0; i < frames; i++)
            {
                double gain = 1.0;
                if (i < fade) gain = (double)i / fade;
                else if (i > frames - fade) gain = (double)(frames - i) / fade;
                float s = (float)(Math.Sin(2.0 * Math.PI * frequency * i / MixFormat.SampleRate) * gain);
                buf[i * 2] = s;
                buf[i * 2 + 1] = s;
            }
            return buf;
        }

        private static float[] Decode(byte[] mp3)
        {
            using (var ms = new MemoryStream(mp3, false))
            using (var reader = new Mp3FileReaderBase(ms, wf => new AcmMp3FrameDecompressor(wf)))
                return ReadAll(ToMixFormat(reader.ToSampleProvider()), int.MaxValue);
        }

        /// <summary>Resample and upmix a source to the mixer's own format.</summary>
        private static ISampleProvider ToMixFormat(ISampleProvider sp)
        {
            if (sp.WaveFormat.SampleRate != MixFormat.SampleRate)
                sp = new WdlResamplingSampleProvider(sp, MixFormat.SampleRate);
            if (sp.WaveFormat.Channels == 1)
                sp = new MonoToStereoSampleProvider(sp);
            return sp;
        }

        /// <summary>Drain a mixer-format source into one buffer, giving up past <paramref name="maxSamples"/>
        /// (null). The engine path passes int.MaxValue so no bundled pet changes behaviour.</summary>
        private static float[] ReadAll(ISampleProvider sp, int maxSamples)
        {
            var all = new List<float>(1 << 16);
            float[] buf = new float[8192];
            int n;
            while ((n = sp.Read(buf.AsSpan())) > 0)
            {
                if (all.Count + n > maxSamples) return null;
                for (int i = 0; i < n; i++) all.Add(buf[i]);
            }
            return all.ToArray();
        }

        /// <summary>
        /// Decode a module-supplied container to the mixer format, or null when it cannot be played. Sniffed
        /// by magic bytes rather than trusting a declared type, and deliberately static + side-effect free so
        /// it is testable on a machine with no audio device at all (--audio-selftest).
        /// </summary>
        internal static float[] DecodeModuleAudio(byte[] audio)
        {
            if (audio == null || audio.Length < 12 || audio.Length > MaximumModuleAudioBytes) return null;
            try
            {
                bool riff = audio[0] == (byte)'R' && audio[1] == (byte)'I' && audio[2] == (byte)'F' && audio[3] == (byte)'F' &&
                            audio[8] == (byte)'W' && audio[9] == (byte)'A' && audio[10] == (byte)'V' && audio[11] == (byte)'E';
                bool id3 = audio[0] == (byte)'I' && audio[1] == (byte)'D' && audio[2] == (byte)'3';
                bool mpegSync = audio[0] == 0xFF && (audio[1] & 0xE0) == 0xE0;
                if (!riff && !id3 && !mpegSync) return null;

                using (var ms = new MemoryStream(audio, false))
                {
                    ISampleProvider sp;
                    if (riff)
                    {
                        var wav = new WaveFileReader(ms);
                        // Reject >2 channels explicitly rather than letting AddMixerInput throw into a silent
                        // catch: the caller needs the false so it can fall back to a bubble.
                        if (wav.WaveFormat.Channels < 1 || wav.WaveFormat.Channels > 2) { wav.Dispose(); return null; }
                        sp = wav.ToSampleProvider();
                    }
                    else
                    {
                        var mp3 = new Mp3FileReaderBase(ms, wf => new AcmMp3FrameDecompressor(wf));
                        if (mp3.WaveFormat.Channels < 1 || mp3.WaveFormat.Channels > 2) { mp3.Dispose(); return null; }
                        sp = mp3.ToSampleProvider();
                    }
                    return ReadAll(ToMixFormat(sp), MaximumModuleDecodedSamples);
                }
            }
            catch { return null; }
        }

        private void DisposeOutput()
        {
            DirectSoundOut o = _output;
            MixingSampleProvider m = _mixer;
            _output = null; _mixer = null; _started = false;
            if (m != null) { try { m.MixerInputEnded -= OnMixerInputEnded; } catch { } }
            // Those inputs died with the device, so the registry must not keep naming them as live.
            lock (_liveSync) _live.Clear();
            if (o != null) { try { o.Stop(); } catch { } o.Dispose(); }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                DisposeOutput();
                _cache.Clear();
            }
        }

        /// <summary>Reads a cached float buffer (already at the mixer format), repeating a fixed number of
        /// extra times, then returns 0 so the mixer drops it.</summary>
        /// <summary>Internal rather than private so --audio-selftest can drive the fade-out directly: the ramp
        /// is the one piece of barge-in whose correctness is not observable without an audio device.</summary>
        internal sealed class CachedSampleProvider : ISampleProvider
        {
            private readonly float[] _samples;
            private readonly WaveFormat _format;
            private int _position;
            private int _loopsRemaining;
            // ~10ms of ramp, the same anti-click reasoning as MakeTone. Cutting an input by returning SHORT is
            // deliberately better than the obvious VolumeSampleProvider.Volume = 0, which leaves a silent input
            // sitting in the mixer for the utterance's full remaining length.
            private const int FadeFrames = 441;
            private volatile bool _ending;
            private int _fadeRemaining;

            public CachedSampleProvider(float[] samples, WaveFormat format, int loops)
            {
                _samples = samples; _format = format; _loopsRemaining = loops;
            }

            /// <summary>Ramp out over ~10 ms and then end, so barge-in costs one mixer buffer (~100 ms) and
            /// does not click. Safe to call from another thread: the reader only ever shortens its output.</summary>
            internal void FadeOutAndEnd()
            {
                if (_ending) return;
                _fadeRemaining = FadeFrames * _format.Channels;
                _ending = true;
            }

            public WaveFormat WaveFormat { get { return _format; } }
            // NAudio 3 modernized ISampleProvider to a Span-based Read.
            public int Read(Span<float> buffer)
            {
                if (_ending)
                {
                    // Ignore any remaining loops: we are being cut, not finishing.
                    int ramp = Math.Min(buffer.Length, _fadeRemaining);
                    if (ramp <= 0) return 0;
                    int available = _samples.Length - _position;
                    if (available < ramp) ramp = Math.Max(0, available);
                    for (int i = 0; i < ramp; i++)
                    {
                        float gain = (float)(_fadeRemaining - i) / FadeFrames / _format.Channels;
                        if (gain > 1f) gain = 1f;
                        buffer[i] = _samples[_position + i] * gain;
                    }
                    _position += ramp;
                    _fadeRemaining -= ramp;
                    return ramp;   // short (or 0) => NAudio drops us and raises MixerInputEnded
                }

                int count = buffer.Length;
                int written = 0;
                while (written < count)
                {
                    int available = _samples.Length - _position;
                    if (available <= 0)
                    {
                        if (_loopsRemaining <= 0) break;
                        _loopsRemaining--; _position = 0; available = _samples.Length;
                        if (available <= 0) break;
                    }
                    int take = Math.Min(available, count - written);
                    _samples.AsSpan(_position, take).CopyTo(buffer.Slice(written, take));
                    _position += take; written += take;
                }
                return written;
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<byte[]>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(byte[] x, byte[] y) { return ReferenceEquals(x, y); }
            public int GetHashCode(byte[] obj) { return RuntimeHelpers.GetHashCode(obj); }
        }
    }
}
