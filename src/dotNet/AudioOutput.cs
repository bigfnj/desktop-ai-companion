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
        /// <summary>
        /// Decoded audio, held only for as long as the SOURCE array is.
        ///
        /// This was a Dictionary keyed by byte[] reference identity and cleared only in Dispose, which
        /// made it unbounded in the one direction that matters. Removing the last pet of a type
        /// disposes its Animations and drops its TSound.Data arrays; adding the type again re-stages
        /// and produces FRESH arrays. So every add/remove cycle left behind entries that could never
        /// be hit again -- each pinning the encoded MP3 AND a mixer-format buffer roughly 7x its size
        /// (44.1 kHz stereo float, 8 bytes per frame) for the life of the process. The tray's Add and
        /// Remove rows make that a two-click loop, and Companion Studio's preview does the same thing.
        ///
        /// A ConditionalWeakTable says exactly what was meant all along: this is a decode cache for
        /// bytes somebody else owns, and it has no business outliving them. Key comparison is
        /// reference identity, which is what ReferenceComparer was hand-rolling.
        /// </summary>
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], float[]> _cache =
            new System.Runtime.CompilerServices.ConditionalWeakTable<byte[], float[]>();

        /// <summary>Stored for a buffer that would not decode, so a broken sound is not retried on every
        /// trigger. A zero-length array rather than null: the play paths already treat "no samples" as
        /// nothing to play, and ConditionalWeakTable is a poor place to reason about null values.</summary>
        private static readonly float[] Undecodable = new float[0];
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
        /// <summary>
        /// The CHOSEN device failed this round -- synchronously in <see cref="TryStart"/>, or asynchronously
        /// through <see cref="OnPlaybackStopped"/> -- so the next <see cref="EnsureStarted"/> goes straight to
        /// the default device instead of re-opening the one that just died. Cleared by <see cref="SetDevice"/>
        /// and by the retry below.
        /// </summary>
        private bool _chosenDeviceFailed;
        /// <summary>
        /// When <see cref="_unavailable"/> may be tried again. The latch used to be permanent, which was right
        /// for "this box has no sound card" and wrong for "the dock with the speakers was unplugged for a
        /// minute": the output stayed dead until a different device was picked or the app restarted. Bounded
        /// retry keeps the "don't retry on every trigger" property and gives a returning endpoint a way back.
        /// </summary>
        private DateTime _retryAfterUtc = DateTime.MinValue;
        internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(2);
        /// <summary>The device the running output was opened on; null when nothing is running.</summary>
        private Guid? _runningDevice;
        /// <summary>How many times the output thread reported a stop we did not ask for.</summary>
        private int _observedFailures;
        private bool _disposed;

        // Test seams for --audio-selftest, which has to watch the asynchronous failure path (F245) on a box
        // whose real devices it cannot rely on. Read-only, and every one takes _sync like the code they observe.
        internal bool IsOutputRunning { get { lock (_sync) return _started; } }
        internal Guid? RunningDevice { get { lock (_sync) return _runningDevice; } }
        internal bool ChosenDeviceFailed { get { lock (_sync) return _chosenDeviceFailed; } }
        internal int ObservedOutputFailures { get { lock (_sync) return _observedFailures; } }
        /// <summary>Run <see cref="EnsureStarted"/> without queuing anything, so a self-test can prove where
        /// the NEXT sound would go without making the box beep.</summary>
        internal bool EnsureStartedForTest() { lock (_sync) return !_disposed && EnsureStarted(); }

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
                if (_disposed) return;
                // Rebuild whenever the RUNNING output is not on the requested device, not only when the stored
                // GUID moved (RA-218). After the chosen device failed and a later sound opened the fallback on
                // the default, `_started` was true on the default: re-applying the SAME device cleared the two
                // flags below, which nothing reads while `_started` is true, and the audio stayed on the
                // fallback for the rest of the session. A device that is still dead costs the next sound to the
                // asynchronous failure before the one after falls back again, the same price the fallback has
                // always carried; a re-apply of a device that IS running rebuilds nothing and cuts nothing.
                bool rebuild = g != _deviceId || (_runningDevice.HasValue && _runningDevice.Value != g);
                _deviceId = g;
                if (rebuild) DisposeOutput();    // rebuild on the requested device the next time something plays
                // A fresh chance whether or not the GUID moved. Re-applying the SAME device from Preferences
                // is the one recovery a user has after the output died, and the old `g == _deviceId` early
                // return made that click a no-op (F245).
                _unavailable = false;
                _chosenDeviceFailed = false;
                _retryAfterUtc = DateTime.MinValue;
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
                    // Undecodable is recorded too, so a broken sound is not re-decoded on every trigger.
                    _cache.Add(mp3, samples ?? Undecodable);
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
        /// Deliberately NOT cached, and still so now that <see cref="_cache"/> is weak-keyed. A TTS line
        /// arrives as a fresh array that nothing else holds, so a cache entry would be collected almost
        /// at once and buy nothing -- while the array is alive, an entry would pin a mixer-format buffer
        /// roughly 7x larger than the input for no reuse at all, because no two lines share bytes.
        /// Pinned by a source-text invariant.
        /// </summary>
        public bool PlayOwned(string owner, byte[] audio, double volume)
        {
            if (audio == null || audio.Length == 0 || volume <= 0.0) return false;
            if (audio.Length > MaximumModuleAudioBytes) return false;
            lock (_sync)
            {
                // Device first: a box with no output should not pay for a decode before finding that out.
                if (_disposed || !EnsureStarted()) return false;
            }
            // The decode runs OUTSIDE _sync (RA-220). DecodeModuleAudio is static and side-effect free, and for
            // an MP3 or a WAV at a non-mixer rate it is the expensive half of a play -- up to a minute of
            // audio resampled into a mixer-format buffer -- while every UI-thread pet Play() takes the same
            // lock first. Held across the decode, a Reminder chime or a TTS line on a pool thread stalled the
            // animation engine for the whole of it. The second EnsureStarted below keeps the device-first
            // contract for an output that died while the decode ran.
            float[] samples = DecodeModuleAudio(audio);
            if (samples == null || samples.Length == 0) return false;
            lock (_sync)
            {
                if (_disposed || !EnsureStarted()) return false;
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
        /// spells out: the chime is one small array that never varies and is worth decoding once, while a
        /// chosen file is re-read per play, so an entry would pin a MaximumCustomFileBytes pick plus a mixer-format buffer
        /// several times its size for as long as that array happened to live, and never be reused.
        /// </summary>
        public bool PlayNotification(string owner, byte[] chosen, double volume)
        {
            if (volume <= 0.0) return false;
            float[] builtIn;
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
                builtIn = _builtInChime;
            }
            // The chosen file's decode runs OUTSIDE _sync, as PlayOwned's does (RA-220): the deferred custom
            // read already evaluates ReadChosen on a pool thread, and this is where the larger cost sat, on
            // the same lock every UI-thread pet Play() takes. Resolve is a pure function of its arguments.
            float[] samples = NotificationSound.Resolve(chosen, builtIn);
            if (samples == null || samples.Length == 0) return false;
            lock (_sync)
            {
                if (_disposed || !EnsureStarted()) return false;
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
            if (_unavailable)
            {
                if (DateTime.UtcNow < _retryAfterUtc) return false;   // stay silent, don't retry on every trigger
                _unavailable = false;                                   // ...but do give it one try per interval
                _chosenDeviceFailed = false;
            }
            // The chosen device first -- unless it has already failed this round, synchronously here or
            // asynchronously through OnPlaybackStopped, in which case the default gets the sound.
            if (!_chosenDeviceFailed && TryStart(_deviceId)) return true;
            if (_deviceId != Guid.Empty)
            {
                _chosenDeviceFailed = true;                             // chosen device gone -> default
                if (TryStart(Guid.Empty)) return true;
            }
            MarkUnavailable();
            return false;
        }

        private void MarkUnavailable()
        {
            _unavailable = true;
            _retryAfterUtc = DateTime.UtcNow + RetryInterval;
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
                // BEFORE Play(): the device is opened on the playback thread Play starts, and a failure
                // there is reported through this event and nowhere else (see OnPlaybackStopped).
                output.PlaybackStopped += OnPlaybackStopped;
                output.Play();
                _mixer = mixer;
                _output = output;
                _runningDevice = device;
                _started = true;
                return true;
            }
            catch
            {
                if (output != null)
                {
                    try { output.PlaybackStopped -= OnPlaybackStopped; } catch { }
                    try { output.Dispose(); } catch { }
                }
                return false;
            }
        }

        /// <summary>
        /// DirectSound's report that the output thread has stopped. This is the ONLY place a device failure is
        /// visible: the DirectSoundOut constructor stores the GUID, Init stores the provider and Play only
        /// starts a thread, so a nonexistent device, a box with no audio device at all, and an endpoint that
        /// vanished mid-session (a dock unplugged, HDMI speakers powered off) all "succeed" in TryStart and
        /// fail HERE, milliseconds to seconds later, with the exception in the event args. Until this handler
        /// existed nothing subscribed: `_started` stayed true for the rest of the session, every later sound was
        /// decoded and queued into a mixer no thread read, PlayOwned and PlayNotification answered true, and
        /// the fallback-to-default branch in EnsureStarted was unreachable for the one class of failure it was
        /// written for (F245). Measured with the pinned NAudio 3.0.0-preview.6: a random GUID returns from
        /// ctor+Init+Play in about a millisecond with PlaybackState Playing, and DSERR_NODRIVER arrives here
        /// a few milliseconds later.
        ///
        /// Thread: NAudio marshals the event to the SynchronizationContext the output was constructed on --
        /// the UI thread for every play that starts there, the playback thread itself for the deferred
        /// custom-notification read, which runs on a pool thread with no context. Either way this takes _sync
        /// and then, through DisposeOutput, _liveSync: the same order every caller uses, so no inversion. It
        /// never adds a mixer input and never plays, which the class doc forbids from a callback.
        ///
        /// A stop WE asked for never reaches here: DisposeOutput unsubscribes before it calls Stop, and the
        /// sender check below is the belt for the window between nulling the field and unsubscribing.
        /// </summary>
        private void OnPlaybackStopped(object sender, StoppedEventArgs e)
        {
            lock (_sync)
            {
                if (_disposed || _output == null || !ReferenceEquals(sender, _output)) return;
                Guid failed = _runningDevice ?? _deviceId;
                DisposeOutput();
                _observedFailures++;
                string why = e != null && e.Exception != null
                    ? e.Exception.GetType().Name + ": " + e.Exception.Message
                    : "the playback thread stopped without an error";
                try
                {
                    DiagnosticLog.Write(LogCategory.Audio, "warning", null,
                        "audio output stopped on device " +
                        (failed == Guid.Empty ? "(default)" : failed.ToString()) + ": " + why);
                }
                catch { }
                if (failed != Guid.Empty && !_chosenDeviceFailed)
                {
                    // The chosen device died: the next sound goes to the default. Not restarted from inside
                    // the callback; the next play is the natural moment, and it costs that one sound nothing
                    // but the fallback it would have had anyway.
                    _chosenDeviceFailed = true;
                }
                else
                {
                    // The default itself (or the fallback onto it) died: nothing left to try right now.
                    MarkUnavailable();
                }
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
                // A block copy: ArraySegment is an ICollection<T>, so AddRange copies the chunk in one go
                // instead of one bounds-checked Add per sample (F265). Up to 5.3 M samples per module clip.
                all.AddRange(new ArraySegment<float>(buf, 0, n));
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

                // The reader is OWNED for the whole decode, as Decode() above owns its own. It was disposed
                // only on the two reject branches; the success path returned from ReadAll with the reader
                // still open, which for MP3 is an ACM conversion stream (acmStreamOpen) and two GCHandle-pinned
                // buffers that the Mp3FileReaderBase constructor opens eagerly and only the finalizer then
                // released -- one set per Reminder chime, since PlayOwned is uncached by design (F246).
                // ReadAll drains synchronously before returning, so disposing afterwards is safe.
                using (var ms = new MemoryStream(audio, false))
                using (WaveStream reader = riff
                    ? (WaveStream)new WaveFileReader(ms)
                    : new Mp3FileReaderBase(ms, wf => new AcmMp3FrameDecompressor(wf)))
                {
                    // Reject >2 channels explicitly rather than letting AddMixerInput throw into a silent
                    // catch: the caller needs the false so it can fall back to a bubble.
                    if (reader.WaveFormat.Channels < 1 || reader.WaveFormat.Channels > 2) return null;
                    return ReadAll(ToMixFormat(reader.ToSampleProvider()), MaximumModuleDecodedSamples);
                }
            }
            catch { return null; }
        }

        private void DisposeOutput()
        {
            DirectSoundOut o = _output;
            MixingSampleProvider m = _mixer;
            _output = null; _mixer = null; _started = false; _runningDevice = null;
            if (m != null) { try { m.MixerInputEnded -= OnMixerInputEnded; } catch { } }
            // Those inputs died with the device, so the registry must not keep naming them as live.
            lock (_liveSync) _live.Clear();
            if (o != null)
            {
                // Unsubscribed FIRST, so the stop we are about to ask for is not read as a failure.
                try { o.PlaybackStopped -= OnPlaybackStopped; } catch { }
                try { o.Stop(); } catch { }
                // Dispose is Stop again under the hood, and NAudio's Stop aborts the playback thread when it
                // cannot take its lock within 50 ms -- Thread.Abort, which throws PlatformNotSupportedException
                // on .NET Core. Swallowed like the Stop above it; a torn-down output is torn down either way.
                try { o.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                DisposeOutput();
                _cache.Clear();   // the table would empty itself as the arrays die; this is just prompt
            }
        }

        /// <summary>Reads a cached float buffer (already at the mixer format), repeating a fixed number of
        /// extra times, then returns 0 so the mixer drops it. Internal rather than private so --audio-selftest
        /// can drive the fade-out directly: the ramp is the one piece of barge-in whose correctness is not
        /// observable without an audio device.</summary>
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
            return Play(data, output, owner, false);
        }

        /// <summary>
        /// As above, with the caller stating whether the custom-file READ AND DECODE may happen off its
        /// thread.
        ///
        /// WHY THE CALLER HAS TO SAY, in the same shape as SetNextBorderAnimation's absenceIsNormal: this
        /// method cannot tell who is asking and the right answer differs. A module's notification arrives
        /// on the UI timer and nobody needs the chime to start on that exact tick, so a MaximumCustomFileBytes read plus a
        /// decode into mixer format has no business stalling the interface. The Preferences "Test sound"
        /// button is the opposite: the user clicked it to find out WHICH layer stops their sound, and an
        /// optimistic answer would be no answer at all.
        ///
        /// Deferring costs the bool its precision -- it becomes "the settings allow this and it has been
        /// handed off" rather than "the device took it" -- which is affordable only because
        /// IHost.PlayNotificationSound already contracts that the module is never told why a notification
        /// did not play. It would not be affordable for the preview.
        /// </summary>
        internal static NotificationOutcome Play(LocalData data, AudioOutput output, string owner,
                                                 bool deferCustomRead)
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
                string chosenPath = data.GetNotificationSoundPath();

                // The BUILT-IN path is not deferred, whatever the caller asked. There is nothing to read
                // and the chime is cached after its first synthesis, so deferring would add a thread hop
                // to the cheap case and make the default configuration behave differently from every
                // assertion written about it.
                if (!deferCustomRead || string.IsNullOrWhiteSpace(chosenPath))
                    return output.PlayNotification(owner ?? "", ReadChosen(chosenPath), volume)
                        ? NotificationOutcome.Played
                        : NotificationOutcome.NoDevice;

                // SINGLE-FLIGHT, because the alternative is worse than a dropped chime. A module that
                // notifies in a burst would otherwise stack one MaximumCustomFileBytes read and one mixer-format decode per
                // notice, and those buffers are large enough to land on the LOH. Skipping a duplicate
                // chime that would have overlapped the one already starting is not a loss.
                if (System.Threading.Interlocked.CompareExchange(ref _customReadInFlight, 1, 0) != 0)
                    return NotificationOutcome.Played;

                AudioOutput target = output;
                string ownerCopy = owner ?? "";
                double volumeCopy = volume;
                System.Threading.Tasks.Task.Run(delegate
                {
                    try { target.PlayNotification(ownerCopy, ReadChosen(chosenPath), volumeCopy); }
                    catch (Exception) { }
                    finally { System.Threading.Interlocked.Exchange(ref _customReadInFlight, 0); }
                });
                // Optimistic, and the doc above says why that is allowed here and not for the preview.
                return NotificationOutcome.Played;
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

        /// <summary>Guards the deferred custom read; see the Play overload that takes deferCustomRead.</summary>
        private static int _customReadInFlight;

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
                return "that file is over " + CompanionXmlValidator.MebibytesOf(MaximumCustomFileBytes) + "; pick a short notification sound.";
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
