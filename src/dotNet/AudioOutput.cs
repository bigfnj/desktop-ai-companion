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
        private float[] _builtInChime;   // the notification default, synthesized on first use
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

        /// <summary>
        /// Play the application's shared notification sound: <paramref name="chosen"/> is the user's own
        /// file, already read to bytes (a self-describing WAV or MP3), or null for the built-in chime.
        /// False means nothing will be heard, the same contract as <see cref="PlayOwned"/>.
        ///
        /// A chosen file that will not decode falls back to the built-in instead of returning false. The
        /// user cannot pick an undecodable file — OptionsShell refuses it at pick time — so arriving here
        /// with rubbish means the file CHANGED underneath them, and the whole job of a notification is to
        /// be heard. The opposite choice turns "I re-saved that wav as a video" into a reminder that goes
        /// quiet with no way to find out why.
        ///
        /// The built-in is cached and the chosen file is not, for the reason <see cref="PlayOwned"/>
        /// spells out: <see cref="_cache"/> is keyed by byte[] reference identity and cleared only in
        /// Dispose, so holding an 8 MiB pick there would pin it plus a mixer-format buffer several times
        /// its size for the life of the process. The chime is one small array that never varies.
        /// </summary>
        public bool PlayNotification(string owner, byte[] chosen, double volume)
        {
            if (volume <= 0.0) return false;
            lock (_sync)
            {
                // Device first, exactly as in PlayOwned: a box with no output must not pay for a decode
                // before finding that out.
                if (_disposed || !EnsureStarted()) return false;
                // Synthesized even when a chosen file is about to win. It costs ~33k samples once per
                // process, and having it in hand is what lets the fallback be a pure function -- which is
                // the only way "no choice plays the built-in" is assertable without a device.
                if (_builtInChime == null)
                    _builtInChime = NotificationSound.BuiltInChime(MixFormat.SampleRate, MixFormat.Channels);
                float[] samples = NotificationSound.Resolve(chosen, _builtInChime);
                if (samples == null || samples.Length == 0) return false;
                AddInput(samples, 0, (float)Math.Max(0.0, Math.Min(1.0, volume)), owner ?? "");
                return true;
            }
        }

        /// <summary>Play a short test tone through the current device at a fixed audible level (the Preferences
        /// "Test output device" button). Ignores the mute setting — the user explicitly asked to hear the
        /// device, which is also why it is no longer what "Test sound" plays: that one previews the chosen
        /// notification sound and must obey the settings this deliberately ignores.</summary>
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

    /// <summary>Why a notification was or was not heard. The caller that can act on it is the Preferences
    /// preview button, which has to tell the user WHICH setting silenced their test; a module only ever
    /// sees the bool this collapses to, because "your chime was refused because the user muted you" is not
    /// a module's business.</summary>
    internal enum NotificationOutcome
    {
        Played,
        /// <summary>The notificationSounds master switch is off.</summary>
        SwitchedOff,
        /// <summary>Master volume is 0 — the slider's own mute position.</summary>
        Muted,
        /// <summary>Nothing to play through: no device, a torn-down output, or a sound that would not decode.</summary>
        NoDevice,
        /// <summary>Settings are not loaded — a self-test, or the part of launch that runs before them.</summary>
        NoSettings,
        /// <summary>Something threw. Kept distinct from NoDevice so a real fault cannot masquerade as a
        /// laptop with the speakers disabled.</summary>
        Failed,
    }

    /// <summary>
    /// The application's notification sound: which sound it is, whether it may be heard, and the built-in
    /// that plays before the user has chosen anything.
    ///
    /// Deliberately not folded into StartUp. The three layers a notification has to honour — the
    /// notificationSounds master switch, the master volume, and the output device — are ORDERED, and the
    /// order is the half that fails silently: check the device first and a muted user gets "no device",
    /// the module falls back to a bubble it would have fallen back to anyway, and every symptom looks
    /// identical to the correct behaviour. So the decision is one static function that reports which layer
    /// stopped it, and it is asserted on a machine with no audio device at all (--audio-selftest) — the
    /// same reason <see cref="AudioOutput.DecodeModuleAudio"/> is static and side-effect free.
    /// </summary>
    internal static class NotificationSound
    {
        /// <summary>Biggest file a user may choose. Half of <see cref="AudioOutput.MaximumModuleAudioBytes"/>
        /// and the same number Reminder enforces on its own chime, so a pick that would sail past this one
        /// and die at the mixer's cap is refused where there is still a person to tell. A notification is a
        /// second of sound; anything approaching this is already the wrong file.</summary>
        internal const long MaximumCustomFileBytes = 8 * 1024 * 1024;

        // The built-in chime: G5 -> C6, two struck notes, ~0.75 s. The same two notes Reminder's embedded
        // clip plays, so a user who has both does not hear two unrelated sounds for the same kind of event.
        //
        // SYNTHESIZED rather than carried as a base64 MP3 the way Reminder carries its one. Reminder had no
        // choice: a module can only hand the host encoded BYTES across the ABI, so its chime has to exist
        // as a file-format blob. The host is on the other side of that boundary — it owns the mixer and can
        // hand it float samples directly — so embedding ~12 KB of base64 here would buy a clip that is
        // itself only two faded sine tones (read Chime.cs's own comment), at the cost of an MP3 decode
        // through the OS ACM codec on every play and a literal nobody can review. The numbers below are
        // reviewable; a base64 blob is not.
        private const double FirstNoteHz = 783.99;      // G5
        private const double SecondNoteHz = 1046.50;    // C6
        private const double SecondNoteDelaySeconds = 0.18;
        private const double ChimeSeconds = 0.75;
        private const double DecaySeconds = 0.16;       // exponential, so the note reads as struck, not held
        private const double PeakAmplitude = 0.7;       // headroom: the mixer SUMS inputs, and the pet may be talking

        /// <summary>
        /// Decide and play, honouring every layer in the order that matters. Never throws: a notification
        /// is a nicety and the caller is usually inside a module's tick.
        ///
        /// <paramref name="output"/> may be null (no audio subsystem yet) and <paramref name="data"/> may
        /// be null (settings not loaded). Both answer "did not play" — but through DIFFERENT outcomes than
        /// the two user settings do, which is what lets the self-test prove the switch is read before the
        /// device rather than inferring it from a shared false.
        /// </summary>
        internal static NotificationOutcome Play(LocalData data, AudioOutput output, string owner)
        {
            try
            {
                // No settings means no master volume to scale by. CompanionHost.Volume already answers 0.0
                // in that state and every module sound is silent, so playing here would make the shared
                // notification the one sound that ignores a condition the rest of the app treats as mute.
                if (data == null) return NotificationOutcome.NoSettings;
                if (!data.GetNotificationSoundsEnabled()) return NotificationOutcome.SwitchedOff;
                double volume = data.GetVolume();
                if (double.IsNaN(volume) || volume <= 0.0) return NotificationOutcome.Muted;
                if (output == null) return NotificationOutcome.NoDevice;
                // The user's slider IS the level, with no per-sound fraction on top. A module's own PlaySound
                // is scaled down because the module chose that number and must not be able to out-shout the
                // pet; this sound is the user's own pick played at the user's own volume, and quietly
                // halving it would make the Preferences preview disagree with the slider beside it.
                return output.PlayNotification(owner ?? "", ReadChosen(data.GetNotificationSoundPath()), volume)
                    ? NotificationOutcome.Played
                    : NotificationOutcome.NoDevice;
            }
            catch (Exception ex)
            {
                // The only outcome here that is a FAULT rather than a setting, so it is the only one worth
                // a line. Written because the Preferences button tells the user to look in the log, and a
                // message pointing at a record that was never made is worse than no message at all.
                // Audio category, so it filters with every other sound line rather than hiding under App.
                try
                {
                    DiagnosticLog.Write(LogCategory.Audio, "warning", null,
                        "notification sound failed: " + ex.GetType().Name + ": " + ex.Message);
                }
                catch { }
                return NotificationOutcome.Failed;
            }
        }

        /// <summary>
        /// The chosen file as bytes, or null meaning "use the built-in". Every failure answers null: a
        /// blank path, a file that moved, an unplugged drive, a size past the cap, another process holding
        /// it open. None of those is worth failing a notification over, and the fallback is audible, so the
        /// user finds out by hearing the default instead of by hearing nothing.
        /// </summary>
        internal static byte[] ReadChosen(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= 0 || info.Length > MaximumCustomFileBytes) return null;
                return File.ReadAllBytes(path);
            }
            catch { return null; }
        }

        /// <summary>
        /// Pick-time validation: null when the mixer can actually play this file, otherwise the reason to
        /// put in front of the user. It DECODES, rather than trusting the extension, because "is it named
        /// .wav" is not the question — a renamed video, a 24-bit 6-channel studio export and a zero-byte
        /// placeholder all pass a name check and none of them will ever make a sound.
        ///
        /// The alternative is discovering it at play time, where the only report available is silence,
        /// three days later, from a reminder the user is no longer standing next to.
        /// </summary>
        internal static string Validate(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "no file was chosen.";
            FileInfo info;
            try { info = new FileInfo(path.Trim()); }
            catch (Exception ex) { return "that path can't be read (" + ex.GetType().Name + ")."; }
            if (!info.Exists) return "that file isn't there any more.";
            if (info.Length <= 0) return "that file is empty.";
            if (info.Length > MaximumCustomFileBytes)
                return "that file is over 8 MiB; pick a short notification sound.";
            byte[] bytes;
            try { bytes = File.ReadAllBytes(info.FullName); }
            catch (Exception ex) { return "couldn't read that file: " + ex.Message; }
            if (AudioOutput.DecodeModuleAudio(bytes) == null)
                return "that isn't a sound this app can play — use a WAV or MP3 (mono or stereo).";
            return null;
        }

        /// <summary>
        /// Which samples a notification will actually sound: the chosen file decoded, or
        /// <paramref name="builtIn"/> when there is no choice or the choice will not decode. Returning the
        /// built-in ARRAY (not a copy) is what lets a caller assert the fallback by reference.
        /// </summary>
        internal static float[] Resolve(byte[] chosen, float[] builtIn)
        {
            if (chosen != null && chosen.Length > 0)
            {
                float[] decoded = AudioOutput.DecodeModuleAudio(chosen);
                if (decoded != null && decoded.Length > 0) return decoded;
            }
            return builtIn;
        }

        /// <summary>
        /// The built-in chime as interleaved samples at the mixer's own format, ready to add with no decode
        /// step at all. Pure and parameterised by format so it can be asserted without an audio device.
        /// </summary>
        internal static float[] BuiltInChime(int sampleRate, int channels)
        {
            if (sampleRate <= 0 || channels < 1 || channels > 2) return new float[0];
            int frames = (int)(sampleRate * ChimeSeconds);
            if (frames <= 0) return new float[0];

            var mono = new double[frames];
            AddStruckNote(mono, sampleRate, FirstNoteHz, 0.0);
            AddStruckNote(mono, sampleRate, SecondNoteHz, SecondNoteDelaySeconds);

            // The decay has not reached zero when the buffer ends, and a buffer that stops mid-waveform
            // clicks. Same ~10 ms tail, and the same reason, as MakeTone's fade.
            int fade = Math.Min(frames / 8, sampleRate / 100);
            for (int i = 0; i < fade; i++) mono[frames - 1 - i] *= (double)i / fade;

            // Normalize to a fixed peak instead of trusting the arithmetic above. The two notes overlap and
            // each carries partials, so the sum's peak depends on phase; guessing it wrong clips, and a
            // clipped chime is not reported as clipping, it is reported as the app sounding cheap.
            double peak = 0.0;
            for (int i = 0; i < frames; i++) { double a = Math.Abs(mono[i]); if (a > peak) peak = a; }
            double gain = peak > 0.0 ? PeakAmplitude / peak : 0.0;

            var buffer = new float[frames * channels];
            for (int f = 0; f < frames; f++)
            {
                float s = (float)(mono[f] * gain);
                for (int c = 0; c < channels; c++) buffer[f * channels + c] = s;
            }
            return buffer;
        }

        /// <summary>One note: a fundamental with two quiet partials under an exponential decay, which is
        /// what makes it read as struck rather than as the 440 Hz test tone with a different number.</summary>
        private static void AddStruckNote(double[] mono, int sampleRate, double hz, double startSeconds)
        {
            int start = (int)(startSeconds * sampleRate);
            if (start >= mono.Length) return;
            int attack = Math.Max(1, sampleRate / 400);   // ~2.5 ms: enough to kill the onset click, short enough to still be a strike
            for (int i = start; i < mono.Length; i++)
            {
                int n = i - start;
                double t = (double)n / sampleRate;
                double envelope = Math.Exp(-t / DecaySeconds);
                if (n < attack) envelope *= (double)n / attack;
                mono[i] +=
                    envelope *
                    (Math.Sin(2.0 * Math.PI * hz * t) +
                     0.35 * Math.Sin(2.0 * Math.PI * hz * 2.0 * t) +
                     0.12 * Math.Sin(2.0 * Math.PI * hz * 3.0 * t));
            }
        }
    }
}
