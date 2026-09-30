using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// Captures the selected microphone and/or the system output (WASAPI loopback), each to its own scratch WAV,
    /// then on stop mixes them offline into one 16 kHz mono 16-bit WAV -- meeting-voice quality, small, and
    /// exactly what Whisper wants, so the transcriber feeds it straight in with no conversion.
    ///
    /// The two tracks are summed sample-aligned from t=0 and the mixer pads nothing, so each scratch file has to
    /// track wall time by itself. Two independent device clocks drift slightly over a long meeting, which is
    /// acceptable for a transcript. What is not acceptable is a loopback track that is simply SHORTER: WASAPI
    /// loopback delivers packets only while some render stream is active on the endpoint, so a loopback source
    /// keeps a silent render stream of its own playing for the whole capture (<see cref="RenderKeepAlive"/>).
    ///
    /// Uses NAudio's classic WasapiCapture / WasapiLoopbackCapture / WasapiOut. The pinned 3.0.0-preview.6 marks
    /// all three obsolete in favour of WasapiRecorder and WasapiPlayer, and it DOES ship those (this comment and
    /// the csproj said it did not until 2026-09-29); the classic classes stay because the stop path below is
    /// built on their threading, which is now understood and pinned by the self-test (<see cref="OpenCapture"/>).
    /// RealtimeCaptureMixer is not in the pinned package. The live WASAPI path is exercised by a real recording,
    /// not a self-test; what the self-test covers, through the three seams below, is the construction and stop
    /// properties, driven by a fake capture that reproduces NAudio's threading (RecorderSelfCheck).
    /// </summary>
    internal sealed class AudioRecorder : IDisposable
    {
        /// <summary>
        /// The scratch names, built from the output stem. Named constants because CaptureStore's purge parses the
        /// same two suffixes: until 2026-09-29 it did not know them at all, so a scratch WAV left by any abnormal
        /// end -- a crash, a Restart Manager kill, an exit inside the mix window -- matched no purge shape and sat
        /// on disk for ever against the 72-hour promise, holding the only and the largest copy of the audio (F171).
        /// </summary>
        internal const string SystemScratchSuffix = ".system.wav";
        internal const string MicScratchSuffix = ".mic.wav";

        /// <summary>
        /// The ceiling on waiting for one source's RecordingStopped. A ceiling for a wedged capture thread, not
        /// the expected cost: the event is signalled from the capture thread within about one buffer period (see
        /// <see cref="OpenCapture"/>). It used to be the expected cost on the shutdown path, per source.
        /// </summary>
        internal static readonly TimeSpan StopBound = TimeSpan.FromSeconds(10);

        private sealed class Source
        {
            public IWaveIn Capture;
            // The endpoint the capture was opened on. AudioDevices.Resolve hands ownership to us and NAudio
            // does NOT take it (WasapiCapture.Dispose releases the audio client, never the endpoint), so the
            // Source owns it and disposes it alongside the capture.
            public MMDevice Device;
            // The silent render stream that keeps a loopback source fed. Null for a microphone, and null for a
            // loopback endpoint that refused a second stream (see KeepAliveFailure).
            public IDisposable KeepAlive;
            public WaveFileWriter Writer;
            public string TempPath;
            public readonly ManualResetEventSlim Stopped = new ManualResetEventSlim(false);
        }

        private readonly List<Source> _sources = new List<Source>();
        public string OutputPath { get; private set; }
        public bool IsRecording { get; private set; }

        /// <summary>Why the loopback source has no silent render stream beside it, or null when it has one (or
        /// there is no loopback source). Reported rather than thrown: recording without it is what every version
        /// before 1.0.17 did, and a refused second stream must not cost the recording.</summary>
        public string KeepAliveFailure { get; private set; }

        /// <summary>
        /// How long the last <see cref="Stop"/> spent stopping the captures and finalising the scratch WAVs, and
        /// how long mixing them took. Two numbers because they answer different questions: the first is the
        /// BUG-009 property -- milliseconds now; it was the full <see cref="StopBound"/> per source on the shutdown
        /// path -- and the second scales with the length of the meeting and is the honest cost of a save.
        /// </summary>
        public TimeSpan LastCaptureStopTime { get; private set; }
        public TimeSpan LastMixTime { get; private set; }

        // ---- seams --------------------------------------------------------------------------------------
        // Three delegates, all defaulting to the real thing, so the self-test can run Start and Stop with no
        // audio device at all: a fake IWaveIn that reproduces NAudio's threading, a resolver that hands back no
        // endpoint, and a keep-alive that records it was asked for. Swapped and restored inside the self-test
        // only (RecorderSelfCheck); production never touches them.

        /// <summary>Constructs the native capture for an endpoint. The one call <see cref="OpenCapture"/> wraps.</summary>
        internal static Func<MMDevice, bool, IWaveIn> CaptureFactory = DefaultCaptureFactory;

        /// <summary>Resolves a saved device name to an endpoint (<see cref="AudioDevices.Resolve"/>).</summary>
        internal static Func<DataFlow, string, MMDevice> DeviceResolver = AudioDevices.Resolve;

        /// <summary>Starts the silent render stream beside a loopback source (<see cref="RenderKeepAlive.Start"/>).</summary>
        internal static Func<MMDevice, WaveFormat, IDisposable> KeepAliveFactory = RenderKeepAlive.Start;

        private static IWaveIn DefaultCaptureFactory(MMDevice device, bool loopback)
        {
            if (loopback) return new WasapiLoopbackCapture(device);
            return new WasapiCapture(device);
        }

        /// <summary>Start capturing to <paramref name="outputWavPath"/>. At least one source must be enabled.
        /// Device NAMES come from <see cref="AudioDevices"/> (the dropdown stores the friendly name; "" = system
        /// default). Throws on a device-open
        /// failure so the caller can report it and fall back.</summary>
        public void Start(string outputWavPath, bool captureSystem, string systemDeviceName, bool captureMic, string micDeviceName)
        {
            if (IsRecording) return;
            if (!captureSystem && !captureMic)
                throw new InvalidOperationException("Select the microphone, the system output, or both.");

            OutputPath = outputWavPath;
            string dir = Path.GetDirectoryName(outputWavPath);
            string stem = Path.GetFileNameWithoutExtension(outputWavPath);
            // The scratch paths are built BEFORE any endpoint is resolved: a bad output path must fail while
            // there is still nothing to leak.
            string systemTemp = Path.Combine(dir, stem + SystemScratchSuffix);
            string micTemp = Path.Combine(dir, stem + MicScratchSuffix);

            try
            {
                if (captureSystem)
                    AddSource(DeviceResolver(DataFlow.Render, systemDeviceName), true, systemTemp);
                if (captureMic)
                    AddSource(DeviceResolver(DataFlow.Capture, micDeviceName), false, micTemp);
                foreach (Source s in _sources) s.Capture.StartRecording();
                IsRecording = true;
            }
            catch
            {
                CleanupCaptures();
                // A start that failed half-way -- the system endpoint opened, then the microphone refused
                // (exclusive use, or a Remote Desktop session that presents no mic at all) -- had already
                // created the first scratch file: a finalised header with no audio behind it. Nothing deleted
                // it and the purge did not know its name, so every failed attempt left one behind for ever
                // (F171). An EMPTY recording is deleted here; one that holds audio is left for the purge,
                // never for a guess about what the user wants.
                DeleteIfEmptyRecording(systemTemp);
                DeleteIfEmptyRecording(micTemp);
                throw;
            }
        }

        /// <summary>Open a capture on <paramref name="device"/> (loopback for a render endpoint, plain capture
        /// for a microphone) and register it as a source. Takes ownership of the device either way: on success
        /// the <see cref="Source"/> disposes it, and on a failed open it is disposed here.</summary>
        private void AddSource(MMDevice device, bool loopback, string tempPath)
        {
            IWaveIn capture = null;
            try
            {
                // A ctor that throws -- the endpoint was yanked between resolve and open, or is in use
                // exclusively -- must not strand the device: nothing else holds a reference to it yet.
                capture = OpenCapture(device, loopback);
            }
            catch
            {
                try { if (device != null) device.Dispose(); } catch { }
                throw;
            }

            var s = new Source { Capture = capture, Device = device, TempPath = tempPath };
            // REGISTERED BEFORE THE WRITER, and that order is the whole point. WaveFileWriter's ctor creates a
            // file under a storage location the user types by hand, so it throws on a bad/read-only/full path.
            // With the Add last, a fully-constructed native capture plus its endpoint were unreachable by both
            // CleanupCaptures() and Dispose() and leaked for the life of the process. CleanupCaptures and Stop
            // both tolerate a null Writer, which is what makes registering this early safe.
            _sources.Add(s);
            if (loopback)
            {
                // Started before the capture, so the very first loopback packets already have a stream to ride
                // on; see RenderKeepAlive for why a loopback source needs one at all. Best-effort: the failure
                // is kept for the caller to log, and the recording goes ahead the way every version before
                // 1.0.17 recorded.
                try { s.KeepAlive = KeepAliveFactory(device, capture.WaveFormat); }
                catch (Exception ex) { s.KeepAlive = null; KeepAliveFailure = ex.Message; }
            }
            s.Writer = new WaveFileWriter(tempPath, capture.WaveFormat);
            capture.DataAvailable += (sender, e) =>
            {
                try { if (s.Writer != null) s.Writer.Write(e.Buffer, 0, e.BytesRecorded); } catch { }
            };
            capture.RecordingStopped += (sender, e) =>
            {
                // Runs on NAudio's capture thread -- see OpenCapture for why that is now guaranteed -- which is
                // also the thread DataAvailable wrote from, so the writer changes hands to nobody.
                try { if (s.Writer != null) { s.Writer.Dispose(); s.Writer = null; } } catch { }
                // Guarded: Stopped is disposed once the capture is torn down, and an unguarded Set() on a
                // disposed event would throw out of NAudio's capture thread and take the process with it.
                try { s.Stopped.Set(); } catch { }
            };
        }

        /// <summary>
        /// Construct the capture with NO SynchronizationContext current, and put the caller's back afterwards.
        ///
        /// NAudio's WasapiCapture reads SynchronizationContext.Current once, in its constructor, and from then
        /// on raises RecordingStopped through it: Post on the captured context when there is one, a direct call
        /// on the capture thread when there is not. Recording starts from a hotkey or a tray click, so the
        /// constructor ran on the WinForms UI thread and captured its context, and <see cref="Stop"/> then
        /// WAITED for that event on the same UI thread -- on the shutdown path with the message loop already
        /// gone, so the posted handler could never run and every wait ran to its 10 s bound, per source
        /// (BUG-009, F168). Reproduced on this box with a probe of exactly that shape, on the real device:
        /// context captured, the wait times out at 10 008 ms; context nulled, the handler runs on the capture
        /// thread 33 ms after StopRecording while the constructing thread sits blocked with no message loop.
        ///
        /// Nulling the context around the `new` is the whole fix: the handler then runs on the capture thread,
        /// where the writer it disposes was being written from anyway, and where DisposeSource's comment always
        /// assumed it ran. The alternative -- dropping the wait and trusting Capture.Dispose() to join the
        /// capture thread -- would have removed the symptom too, but WasapiCapture nulls its thread field before
        /// it raises the event, so a Dispose that arrives in that window skips the Join and the handler would
        /// still be disposing the writer while DisposeSource did the same. The wait closes that window, and now
        /// costs milliseconds instead of the bound.
        ///
        /// Restored in a finally, so an exception from the constructor (endpoint yanked, exclusive use) cannot
        /// leave the UI thread without its context.
        /// </summary>
        internal static IWaveIn OpenCapture(MMDevice device, bool loopback)
        {
            return ConstructWithNoSynchronizationContext(() => CaptureFactory(device, loopback));
        }

        /// <summary>The no-context window <see cref="OpenCapture"/> and <see cref="RenderKeepAlive"/> both
        /// construct inside; see OpenCapture for why it exists.</summary>
        internal static T ConstructWithNoSynchronizationContext<T>(Func<T> construct)
        {
            SynchronizationContext previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try { return construct(); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        /// <summary>Stop, finalize each source, and mix to the 16 kHz mono WAV at <see cref="OutputPath"/>.
        /// Idempotent; returns the output path, or null if nothing was captured.</summary>
        public string Stop()
        {
            if (!IsRecording) return OutputPath;
            IsRecording = false;

            var stopwatch = Stopwatch.StartNew();
            foreach (Source s in _sources)
            {
                try { s.Capture.StopRecording(); } catch { s.Stopped.Set(); }
            }
            // Signalled from the capture thread once its loop notices StopRecording, about one buffer period
            // later, which is why the bound is a ceiling and no longer the cost (OpenCapture has the history).
            foreach (Source s in _sources)
            {
                try { s.Stopped.Wait(StopBound); } catch { }
            }
            foreach (Source s in _sources) DisposeSource(s);
            LastCaptureStopTime = stopwatch.Elapsed;

            List<string> temps = _sources.Select(s => s.TempPath).ToList();
            _sources.Clear();
            stopwatch.Restart();
            string mixed = MixToWhisperWav(temps, OutputPath);
            LastMixTime = stopwatch.Elapsed;
            return mixed;
        }

        // Read each temp WAV, downmix to mono, resample to 16 kHz, sum, and write one 16-bit PCM WAV. One input
        // is just a format conversion; two are mixed. Returns null if nothing usable was captured.
        private static string MixToWhisperWav(List<string> inputs, string outPath)
        {
            var live = inputs.Where(p => { try { return new FileInfo(p).Length > 44; } catch { return false; } }).ToList();
            if (live.Count == 0) return null;

            var readers = new List<WaveFileReader>();
            try
            {
                var providers = new List<ISampleProvider>();
                foreach (string p in live)
                {
                    var reader = new WaveFileReader(p);
                    readers.Add(reader);
                    ISampleProvider sp = reader.ToSampleProvider();
                    if (sp.WaveFormat.Channels == 2)
                        sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
                    else if (sp.WaveFormat.Channels > 2)
                        sp = new MultiplexingSampleProvider(new[] { sp }, 1);   // take the first channel
                    if (sp.WaveFormat.SampleRate != 16000)
                        sp = new WdlResamplingSampleProvider(sp, 16000);
                    providers.Add(sp);
                }
                ISampleProvider final = providers.Count == 1 ? providers[0] : new MixingSampleProvider(providers);
                WaveFileWriter.CreateWaveFile16(outPath, final);
            }
            finally
            {
                foreach (WaveFileReader r in readers) { try { r.Dispose(); } catch { } }
            }
            foreach (string p in live) { try { File.Delete(p); } catch { } }
            return outPath;
        }

        /// <summary>
        /// Delete a scratch WAV that holds a header and no audio, and nothing else: a file that does not parse
        /// as a WAV, or one with any audio in it, is left where it is. Returns true only when it deleted.
        /// </summary>
        internal static bool DeleteIfEmptyRecording(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
                long dataLength;
                using (var reader = new WaveFileReader(path)) dataLength = reader.Length;
                if (dataLength != 0) return false;
                File.Delete(path);
                return true;
            }
            catch { return false; }
        }

        private void CleanupCaptures()
        {
            foreach (Source s in _sources) DisposeSource(s);
            _sources.Clear();
        }

        /// <summary>
        /// Release everything one source owns, in the only order that is safe, and the SINGLE place that
        /// knows the order -- both <see cref="Stop"/> and <see cref="CleanupCaptures"/> come through here, so
        /// a field added to <see cref="Source"/> has exactly one site to be freed in. Writer first (it is
        /// what holds the temp WAV open; after a clean stop the RecordingStopped handler has already done it
        /// and this is a no-op), then the capture, whose Dispose joins the capture thread, then the silent
        /// render stream that was keeping a loopback capture fed, then the endpoint both of those came from,
        /// and only then the stop event: NAudio raises RecordingStopped from the capture thread, so the event
        /// must outlive the join. Every step is individually guarded, because one COM failure must not strand
        /// the rest.
        /// </summary>
        private static void DisposeSource(Source s)
        {
            if (s == null) return;
            try { if (s.Writer != null) { s.Writer.Dispose(); s.Writer = null; } } catch { }
            try { if (s.Capture != null) { s.Capture.Dispose(); s.Capture = null; } } catch { }
            try { if (s.KeepAlive != null) { s.KeepAlive.Dispose(); s.KeepAlive = null; } } catch { }
            try { if (s.Device != null) { s.Device.Dispose(); s.Device = null; } } catch { }
            try { s.Stopped.Dispose(); } catch { }
        }

        public void Dispose()
        {
            try { if (IsRecording) Stop(); else CleanupCaptures(); } catch { }
        }
    }

    /// <summary>
    /// A silent render stream on a loopback source's endpoint, for the duration of the capture.
    ///
    /// WASAPI loopback hands out packets only while SOME render stream is active on the endpoint; NAudio's own
    /// WasapiLoopbackCapture doc says it plainly ("if no audio is playing, the DataAvailable event will not
    /// fire") and names playing silence as the workaround. The mixer downstream sums the two scratch files
    /// sample-aligned from t=0 and pads nothing, so every second in which the meeting's audio was not yet
    /// playing was a second MISSING from the system track, and everything after it landed that much early
    /// against the microphone: remote speech interleaved with the wrong local speech in the transcript (F169).
    /// With this stream playing, the engine mixes our zeros with whatever else plays, the loopback receives a
    /// continuous stream, and the scratch file tracks wall time.
    ///
    /// The other route -- padding by a Stopwatch inside DataAvailable -- has to reconcile the device clock with
    /// the wall clock over an hour, decide a threshold that drift never crosses but a real gap always does, and
    /// pad the tail at Stop as well; this asks the audio engine to do the one thing it already does exactly.
    /// Best-effort: an endpoint that refuses a second stream (exclusive-mode use) records the way every earlier
    /// version did, and the recorder reports why.
    /// </summary>
    internal sealed class RenderKeepAlive : IDisposable
    {
        private WasapiOut _out;

        /// <summary>Start playing silence on <paramref name="device"/> in the loopback's own format -- the
        /// endpoint's mix format, so nothing is resampled on the way in. Throws when the endpoint refuses; the
        /// caller records that and records without.</summary>
        public static IDisposable Start(MMDevice device, WaveFormat format)
        {
            if (device == null) throw new ArgumentNullException("device");
            if (format == null) throw new ArgumentNullException("format");
            var keepAlive = new RenderKeepAlive();
            try
            {
                // Constructed inside the same no-context window as the capture: WasapiOut captures
                // SynchronizationContext.Current for PlaybackStopped exactly as WasapiCapture does for
                // RecordingStopped, and nothing here wants a callback posted to a UI thread that may be gone.
                keepAlive._out = AudioRecorder.ConstructWithNoSynchronizationContext(
                    () => new WasapiOut(device, AudioClientShareMode.Shared, true, 200));
                keepAlive._out.Init(new SilenceProvider(format));
                keepAlive._out.Play();
                return keepAlive;
            }
            catch
            {
                keepAlive.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            WasapiOut playing = _out;
            _out = null;
            if (playing == null) return;
            try { playing.Stop(); } catch { }
            try { playing.Dispose(); } catch { }
        }
    }
}
