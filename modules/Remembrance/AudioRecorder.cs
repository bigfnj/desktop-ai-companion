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
    /// Since 2.4.0 (lane feature/meeting-end) each scratch is ALREADY 16 kHz mono 16-bit: the TrackConverter turns
    /// every packet into that format as it arrives, so an hour of a track is 115 MB rather than the 1.3 GB the device
    /// format took, and the mix at the stop sums two tracks that need no resampling. A track whose format NAudio
    /// cannot decode is written in the device's format as before, and the mix converts it. Each track also feeds a
    /// TrackMeter, the loudness the meeting-end watch reads, and a write that throws stops that track and is reported
    /// (WriteFailures) where it used to be swallowed.
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
            public IKeepAlive KeepAlive;
            public bool Loopback;
            public WaveFileWriter Writer;
            public string TempPath;
            public readonly ManualResetEventSlim Stopped = new ManualResetEventSlim(false);

            // ---- 2.4.0 (lane feature/meeting-end) ----
            // The packet-by-packet conversion to 16 kHz mono 16-bit, or null when the device's format has no decoder
            // and the track is written as it comes (ConverterFailure says why; the mix converts it at the stop).
            public TrackConverter Converter;
            public string ConverterFailure;
            // Loudness, one value a second, for the meeting-end watch. Fed only through the converter, so a track
            // written in the device's format is not measured and the watch draws no silence from it.
            public readonly TrackMeter Meter = new TrackMeter();
            // Set on the capture thread by the first write that throws; from then on nothing more is written to this
            // track (TrackWriteFailure says why it is never resumed). Read on the UI thread by the watch's tick.
            public volatile bool Failed;
            public volatile TrackWriteFailure WriteFailure;
            public WaveFormat WrittenFormat;
            public long BytesWritten;
            private Action<byte[], int> _writePcm;
            private Action<float[], int> _meter;

            public string Name { get { return Loopback ? "system output" : "microphone"; } }

            /// <summary>One packet from the capture thread, converted and written, or the write's failure recorded.
            /// Nothing here throws: an exception out of DataAvailable would end NAudio's capture thread.</summary>
            public void Write(byte[] buffer, int count)
            {
                if (Failed) return;
                WaveFileWriter writer = Writer;
                if (writer == null || buffer == null || count <= 0) return;
                try
                {
                    if (Converter == null)
                    {
                        writer.Write(buffer, 0, count);
                        BytesWritten += count;
                        return;
                    }
                    if (_writePcm == null)
                    {
                        _writePcm = delegate(byte[] pcm, int n) { Writer.Write(pcm, 0, n); BytesWritten += n; };
                        _meter = Meter.Add;
                    }
                    Converter.Convert(buffer, count, _writePcm, _meter);
                }
                catch (Exception ex)
                {
                    // The type only, never the message, which can carry the scratch's path and so the meeting's title.
                    Failed = true;
                    WriteFailure = new TrackWriteFailure(Name, WrittenTime, ex.GetType().Name);
                }
            }

            /// <summary>How much audio this track has on disk, from the bytes written and the format they were written in.</summary>
            public TimeSpan WrittenTime
            {
                get
                {
                    WaveFormat f = WrittenFormat;
                    if (f == null || f.AverageBytesPerSecond <= 0) return TimeSpan.Zero;
                    return TimeSpan.FromSeconds(BytesWritten / (double)f.AverageBytesPerSecond);
                }
            }
        }

        /// <summary>One recording track as the meeting-end watch sees it: its name, its loudness, and whether that
        /// loudness is measured at all (a track written in the device's format, the fallback, is not).</summary>
        internal sealed class TrackLevels
        {
            internal readonly string Name;
            internal readonly bool Loopback;
            internal readonly TrackMeter Meter;
            internal readonly bool Measured;

            internal TrackLevels(string name, bool loopback, TrackMeter meter, bool measured)
            {
                Name = name;
                Loopback = loopback;
                Meter = meter;
                Measured = measured;
            }
        }

        private readonly List<Source> _sources = new List<Source>();
        public string OutputPath { get; private set; }
        public bool IsRecording { get; private set; }

        // Wall time from the moment every capture was started, for the stop's length check: a track more than 2 % shorter
        // than this lost audio somewhere, and the log says which (2.4.0).
        private Stopwatch _wall;

        /// <summary>The tracks being recorded, for the meeting-end watch's tick (UI thread, while recording).</summary>
        internal IReadOnlyList<TrackLevels> Tracks
        {
            get
            {
                var tracks = new List<TrackLevels>();
                foreach (Source s in _sources) tracks.Add(new TrackLevels(s.Name, s.Loopback, s.Meter, s.Converter != null));
                return tracks;
            }
        }

        /// <summary>The tracks that have stopped saving, at most one entry each (2.4.0). Read while recording by the
        /// module's tick, which logs each once and shows it on the Status line, and after the stop as well.</summary>
        internal IReadOnlyList<TrackWriteFailure> WriteFailures
        {
            get
            {
                var failures = new List<TrackWriteFailure>();
                foreach (Source s in _sources)
                {
                    TrackWriteFailure f = s.WriteFailure;
                    if (f != null) failures.Add(f);
                }
                failures.AddRange(_stoppedFailures);
                return failures;
            }
        }
        private readonly List<TrackWriteFailure> _stoppedFailures = new List<TrackWriteFailure>();

        /// <summary>Why a track is written in its device's format rather than converted as it arrives, one line per such
        /// track, or empty. Read by the module right after Start, for the log.</summary>
        internal IReadOnlyList<string> ConverterFallbacks
        {
            get
            {
                var lines = new List<string>();
                foreach (Source s in _sources)
                    if (s.Converter == null && s.ConverterFailure != null) lines.Add(s.Name + ": " + s.ConverterFailure);
                return lines;
            }
        }

        /// <summary>
        /// What the last <see cref="Stop"/> cut off the end when it was asked to keep only the start (2.4.0: the meeting
        /// ended and nobody answered "Keep recording"), and how much it kept. Zero trimmed when nothing was. The audio cut
        /// is gone: it is never written to recording.wav and the scratch tracks are deleted after the mix (PRIVACY.md).
        /// </summary>
        public TimeSpan LastTrimmed { get; private set; }
        public TimeSpan LastKept { get; private set; }

        /// <summary>One line per track that came out more than 2 % shorter than the wall time it was recorded over, for
        /// the caller's log (2.4.0): a track that short lost packets, and everything after the loss is early against the
        /// other track. Empty when every track kept time, or the recording was under 30 seconds.</summary>
        public IReadOnlyList<string> ShortTracks { get { return _shortTracks; } }
        private List<string> _shortTracks = new List<string>();

        /// <summary>Why the loopback source has no silent render stream beside it, or null when it has one (or
        /// there is no loopback source). Reported rather than thrown: recording without it is what every version
        /// before 1.0.17 did, and a refused second stream must not cost the recording.</summary>
        public string KeepAliveFailure { get; private set; }

        /// <summary>
        /// Why the silent render stream stopped BEFORE the capture did, or null when it played to the end. Read
        /// off the stream at <see cref="Stop"/>: WasapiOut raises PlaybackStopped with the exception when its
        /// render thread dies (the default output's format changed under it, AUDCLNT_E_DEVICE_INVALIDATED),
        /// and until 2026-09-30 nothing listened, so from that second on the recording reverted to the
        /// pre-1.0.17 alignment with no line in the log to say so (RA-150). The caller logs it.
        /// </summary>
        public string KeepAliveEndedEarly { get; private set; }

        /// <summary>
        /// The exception a capture's RecordingStopped carried, prefixed with which source, or null when both
        /// stopped clean. NAudio delivers a capture thread's death through that event's Exception, and the
        /// handler dropped it (RA-150); the writer was finalised either way, so the scratch holds what arrived
        /// before the failure, and the caller logs why it ends where it does.
        /// </summary>
        public string CaptureFailure { get; private set; }

        /// <summary>
        /// How long the last <see cref="Stop"/> spent stopping the captures and finalising the scratch WAVs, and
        /// how long mixing them took. Two numbers because they answer different questions: the first is the
        /// BUG-009 property -- milliseconds now; it was the full <see cref="StopBound"/> per source on the shutdown
        /// path -- and the second scales with the length of the meeting and is the honest cost of a save.
        /// </summary>
        public TimeSpan LastCaptureStopTime { get; private set; }
        public TimeSpan LastMixTime { get; private set; }

        /// <summary>
        /// The scratch WAVs this recorder tried to delete and could not, by count and exception type, for the caller
        /// to log (lane feature/remembrance-delete-logging): the two tracks after the mix, which hold the raw
        /// microphone and system audio, and a header-only one a stop or a failed start removes. Both deletes had
        /// empty catches. A scratch left this way sits beside recording.wav in the capture's folder (per capture) or
        /// the day's folder (by date), under the storage root, and its names are purge shapes (F171), so the 72-hour
        /// purge removes it with the rest of that capture's audio; it is never in %TEMP%, where nothing would. A
        /// delete that WORKS is not counted or logged: these are the recording's own working files, and the audio
        /// they held is in recording.wav, or there was none.
        /// </summary>
        public DeletionReport ScratchCleanup { get; } = new DeletionReport();

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
        internal static Func<MMDevice, WaveFormat, IKeepAlive> KeepAliveFactory = RenderKeepAlive.Start;

        /// <summary>
        /// Opens a scratch track's writer (2.4.0). The real one enables RF64: a converted track reaches NAudio's 4 GB WAV
        /// limit only after some 37 hours, but a track written in its device's format (the fallback) reaches it after
        /// about 3.1, and with RF64 the writer promotes its header at that size instead of throwing. WaveFileReader reads
        /// the promoted file back (the self-test pins it with a tiny promotion threshold, through this seam).
        /// </summary>
        internal static Func<string, WaveFormat, WaveFileWriter> ScratchWriterFactory = DefaultScratchWriter;

        private static WaveFileWriter DefaultScratchWriter(string path, WaveFormat format)
        {
            return new WaveFileWriter(path, format, new WaveFileWriterOptions { EnableRf64 = true });
        }

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
                _wall = Stopwatch.StartNew();
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
                DeleteIfEmptyRecording(systemTemp, ScratchCleanup);
                DeleteIfEmptyRecording(micTemp, ScratchCleanup);
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

            var s = new Source { Capture = capture, Device = device, TempPath = tempPath, Loopback = loopback };
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
            // The track is converted to 16 kHz mono 16-bit as it arrives (2.4.0), unless NAudio has no decoder for the
            // device's format, in which case it is written as it comes and the mix converts it at the stop.
            string converterFailure;
            s.Converter = TrackConverter.TryCreate(capture.WaveFormat, out converterFailure);
            s.ConverterFailure = converterFailure;
            s.WrittenFormat = s.Converter != null ? TrackConverter.TargetFormat() : capture.WaveFormat;
            s.Writer = ScratchWriterFactory(tempPath, s.WrittenFormat);
            // NO SILENT CATCH any more (2.4.0). This was `try { s.Writer.Write(...) } catch { }`, so the write NAudio
            // refuses past 4 GB -- three hours into a meeting in the device's format -- threw on every packet from then
            // on and the track simply stopped, with nothing in the log and the Status line saying "Recording". Source.Write
            // stops the track at the first failure and records which track, how far in and the exception's type.
            capture.DataAvailable += (sender, e) =>
            {
                if (e != null) s.Write(e.Buffer, e.BytesRecorded);
            };
            capture.RecordingStopped += (sender, e) =>
            {
                // Runs on NAudio's capture thread -- see OpenCapture for why that is now guaranteed -- which is
                // also the thread DataAvailable wrote from, so the writer changes hands to nobody.
                try { if (s.Writer != null) { s.Writer.Dispose(); s.Writer = null; } } catch { }
                // The capture thread's own death arrives here, as the event's Exception, and nowhere else. Kept
                // for the caller's log rather than dropped (RA-150); the writer above was finalised regardless,
                // so the scratch holds everything that arrived before it.
                try
                {
                    if (e != null && e.Exception != null)
                        CaptureFailure = (s.Loopback ? "the system output capture" : "the microphone capture")
                                         + " stopped with an error: " + e.Exception.Message;
                }
                catch { }
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
        /// Idempotent; returns the output path, or null if nothing was captured.
        ///
        /// <paramref name="keepUntil"/> (2.4.0) keeps only the first part of every track, for a recording stopped because
        /// the meeting ended and nobody answered: the module passes the detected end plus 30 seconds. Null, or anything
        /// under <see cref="MinimumKeep"/>, keeps it all. <see cref="LastTrimmed"/> says how much went.</summary>
        public string Stop(TimeSpan? keepUntil = null)
        {
            if (!IsRecording) return OutputPath;
            IsRecording = false;
            TimeSpan wall = _wall != null ? _wall.Elapsed : TimeSpan.Zero;

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
            // Read BEFORE the stream is disposed, which is the only moment its failure can still be asked for.
            // A keep-alive that died mid-capture left the loopback track short from that second on (RA-150).
            foreach (Source s in _sources)
            {
                try { if (s.KeepAlive != null && s.KeepAlive.Failure != null) KeepAliveEndedEarly = s.KeepAlive.Failure; }
                catch { }
            }
            foreach (Source s in _sources) DisposeSource(s);
            LastCaptureStopTime = stopwatch.Elapsed;

            var tracks = _sources.Select(s => new MixTrack(s.TempPath, s.Name)).ToList();
            foreach (Source s in _sources) { TrackWriteFailure f = s.WriteFailure; if (f != null) _stoppedFailures.Add(f); }
            _sources.Clear();
            stopwatch.Restart();
            MixReport report;
            string mixed = MixToWhisperWav(tracks, OutputPath, ScratchCleanup, keepUntil, wall, out report);
            LastMixTime = stopwatch.Elapsed;
            LastTrimmed = report.Trimmed;
            LastKept = report.Kept;
            _shortTracks = report.ShortTracks;
            return mixed;
        }

        /// <summary>A scratch track to mix and the name the log gives it.</summary>
        internal sealed class MixTrack
        {
            internal readonly string Path;
            internal readonly string Name;
            internal MixTrack(string path, string name) { Path = path; Name = name ?? "track"; }
        }

        /// <summary>What a mix did beyond writing the file: how much it kept and cut, and which tracks ran short.</summary>
        internal sealed class MixReport
        {
            internal TimeSpan Kept;
            internal TimeSpan Trimmed;
            internal List<string> ShortTracks = new List<string>();
        }

        /// <summary>A trim shorter than this is no trim: OffsetSampleProvider reads Take = 0 as "to the end", and a
        /// recording cut to under a second would be a recording deleted by arithmetic.</summary>
        internal static readonly TimeSpan MinimumKeep = TimeSpan.FromSeconds(1);

        /// <summary>A track this much shorter than the wall time it covered is logged (2.4.0).</summary>
        internal const double ShortTrackFraction = 0.02;

        // Read each temp WAV, downmix to mono, resample to 16 kHz, sum, and write one 16-bit PCM WAV. One input
        // is just a format conversion; two are mixed. Returns null if nothing usable was captured.
        //
        // "USABLE" IS PARSED, NOT SIZED. This filtered on FileInfo.Length > 44, the size of a minimal RIFF header,
        // and WaveFileWriter's header is 46 bytes (an 18-byte fmt chunk), so a scratch that had received no
        // packet at all -- both of them, on a stop inside the first buffer period -- passed as live, the mix
        // wrote an empty recording.wav, and the caller announced it saved (R-038). A header with no data
        // behind it is what DeleteIfEmptyRecording already recognises; the same reading decides here, and the
        // empty scratch goes the way a failed start's does instead of waiting for the purge.
        //
        // SINCE 2.4.0 a scratch is usually 16 kHz mono 16-bit already (TrackConverter), so neither conversion branch runs
        // for it; they stay for a track the converter could not take and for a scratch an earlier version left. A scratch
        // in WAVE_FORMAT_EXTENSIBLE is read as the plain format it describes (TrackConverter.Standardize), since NAudio's
        // decoder refuses that encoding. With keepUntil each track is cut there (OffsetSampleProvider.Take, on the 16 kHz
        // stream) before the tracks are summed, and the cut audio is never written. Internal for the self-test.
        internal static string MixToWhisperWav(List<MixTrack> inputs, string outPath, DeletionReport cleanup,
            TimeSpan? keepUntil, TimeSpan wall, out MixReport report)
        {
            report = new MixReport();
            var live = inputs.Where(t => HasAudio(t.Path)).ToList();
            foreach (MixTrack t in inputs) { if (!live.Contains(t)) DeleteIfEmptyRecording(t.Path, cleanup); }
            if (live.Count == 0) return null;
            bool trim = keepUntil.HasValue && keepUntil.Value >= MinimumKeep;

            var readers = new List<WaveFileReader>();
            try
            {
                var providers = new List<ISampleProvider>();
                TimeSpan longest = TimeSpan.Zero;
                foreach (MixTrack t in live)
                {
                    var reader = new WaveFileReader(t.Path);
                    readers.Add(reader);
                    TimeSpan length = reader.TotalTime;
                    if (length > longest) longest = length;
                    // A track that lost time: more than 2 % short of the wall clock, over a recording long enough for the
                    // figure to mean something. Measured on the track as captured, before any trim.
                    if (wall >= TimeSpan.FromSeconds(30) && length.TotalSeconds < wall.TotalSeconds * (1 - ShortTrackFraction))
                        report.ShortTracks.Add("the " + t.Name + " track is " +
                            ((1 - length.TotalSeconds / wall.TotalSeconds) * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                            " % shorter than the time recorded (" + Duration(length) + " of " + Duration(wall) + ")");
                    ISampleProvider sp = SamplesOf(reader);
                    if (sp.WaveFormat.Channels == 2)
                        sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
                    else if (sp.WaveFormat.Channels > 2)
                        sp = new MultiplexingSampleProvider(new[] { sp }, 1);   // take the first channel
                    if (sp.WaveFormat.SampleRate != 16000)
                        sp = new WdlResamplingSampleProvider(sp, 16000);
                    if (trim && length > keepUntil.Value)
                        sp = new OffsetSampleProvider(sp) { Take = keepUntil.Value };
                    providers.Add(sp);
                }
                if (trim && longest > keepUntil.Value)
                {
                    report.Kept = keepUntil.Value;
                    report.Trimmed = longest - keepUntil.Value;
                }
                else report.Kept = longest;
                ISampleProvider final = providers.Count == 1 ? providers[0] : new MixingSampleProvider(providers);
                WaveFileWriter.CreateWaveFile16(outPath, final);
            }
            finally
            {
                foreach (WaveFileReader r in readers) { try { r.Dispose(); } catch { } }
            }
            // The raw tracks, now that recording.wav holds their audio. One that will not go (another program holds
            // it, or it is read-only) is counted for the caller's log rather than swallowed; see ScratchCleanup for
            // where it stays and what removes it.
            foreach (MixTrack t in live)
            {
                try { File.Delete(t.Path); }
                catch (Exception ex) { cleanup.FileFailed(ex); }
            }
            return outPath;
        }

        /// <summary>A scratch's samples, a WAVE_FORMAT_EXTENSIBLE one read as the plain format it describes.</summary>
        private static ISampleProvider SamplesOf(WaveFileReader reader)
        {
            WaveFormat standard = TrackConverter.Standardize(reader.WaveFormat);
            if (ReferenceEquals(standard, reader.WaveFormat) || standard.Encoding == WaveFormatEncoding.Extensible)
                return reader.ToSampleProvider();
            return new RawSourceWaveStream(reader, standard).ToSampleProvider();
        }

        /// <summary>"47 min 12 s", "2 h 3 min 4 s", "40 s": a length for a log line.</summary>
        internal static string Duration(TimeSpan span)
        {
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            long total = (long)Math.Round(span.TotalSeconds);
            long h = total / 3600, m = (total / 60) % 60, sec = total % 60;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (h > 0) return h.ToString(ci) + " h " + m.ToString(ci) + " min " + sec.ToString(ci) + " s";
            if (m > 0) return m.ToString(ci) + " min " + sec.ToString(ci) + " s";
            return sec.ToString(ci) + " s";
        }

        /// <summary>
        /// Delete a scratch WAV that holds a header and no audio, and nothing else: a file that does not parse
        /// as a WAV, or one with any audio in it, is left where it is. Returns true only when it deleted. A delete
        /// that fails is counted in <paramref name="failures"/> by type (lane feature/remembrance-delete-logging);
        /// a file that cannot be READ is not a failure, it is kept on purpose, because nothing vouches it is empty.
        /// </summary>
        internal static bool DeleteIfEmptyRecording(string path, DeletionReport failures)
        {
            long dataLength;
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
                using (var reader = new WaveFileReader(path)) dataLength = reader.Length;
            }
            catch { return false; }
            if (dataLength != 0) return false;
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                failures.FileFailed(ex);
                return false;
            }
        }

        /// <summary>A WAV whose data chunk holds at least one byte, read off its header. False for a
        /// header-only scratch, a missing file and anything that does not parse: none of those can be mixed.</summary>
        internal static bool HasAudio(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
                using (var reader = new WaveFileReader(path)) return reader.Length > 0;
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
    ///
    /// TWO VISIBLE SIDE EFFECTS, both by design and neither a defect (R-036). An active shared-mode render
    /// stream is an audio session, so for the length of a recording this process is listed in the Volume
    /// Mixer, silent; and the audio engine holds its "An audio stream is currently in use." power request
    /// while any render stream is active, so `powercfg /requests` shows a SYSTEM entry attributed to the
    /// audio driver (not to this process) and automatic sleep is blocked -- which during a meeting recording
    /// is the behaviour anyone would want, since sleep would end the recording. Whether a given endpoint's
    /// driver honours the packet-delivery workaround is the live check BLOCKED.md T25 still needs a console
    /// session for; the suite pins the recorder's side of it through the KeepAliveFactory seam.
    /// </summary>
    internal sealed class RenderKeepAlive : IKeepAlive
    {
        private WasapiOut _out;
        private volatile bool _stopRequested;
        private volatile string _failure;

        /// <summary>Why the stream stopped before <see cref="Dispose"/> asked it to, or null while it plays
        /// (RA-150). WasapiOut raises PlaybackStopped on its render thread when that thread dies, carrying the
        /// exception; a stop the engine chose with no exception is reported too, since a silent provider never
        /// ends on its own.</summary>
        public string Failure { get { return _failure; } }

        /// <summary>Start playing silence on <paramref name="device"/> in the loopback's own format -- the
        /// endpoint's mix format, so nothing is resampled on the way in. Throws when the endpoint refuses; the
        /// caller records that and records without.</summary>
        public static IKeepAlive Start(MMDevice device, WaveFormat format)
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
                keepAlive._out.PlaybackStopped += keepAlive.OnPlaybackStopped;
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

        private void OnPlaybackStopped(object sender, StoppedEventArgs e)
        {
            // Raised on the render thread (no context was current at construction). Our own Stop() raises it
            // too, with no exception, and that one is not a failure.
            if (_stopRequested) return;
            _failure = e != null && e.Exception != null
                ? e.Exception.Message
                : "the render stream stopped on its own";
        }

        public void Dispose()
        {
            _stopRequested = true;
            WasapiOut playing = _out;
            _out = null;
            if (playing == null) return;
            try { playing.Stop(); } catch { }
            try { playing.Dispose(); } catch { }
        }
    }

    /// <summary>What the recorder holds for a loopback source's silent render stream: something to release
    /// with the source, and a reason when it stopped early (RA-150). RenderKeepAlive is the real one; the
    /// self-test's probe is the other.</summary>
    internal interface IKeepAlive : IDisposable
    {
        string Failure { get; }
    }
}
