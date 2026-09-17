using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// Captures the selected microphone and/or the system output (WASAPI loopback), each to its own temp WAV,
    /// then on stop mixes them offline into one 16 kHz mono 16-bit WAV — meeting-voice quality, small, and
    /// exactly what Whisper wants, so the transcriber feeds it straight in with no conversion. Two independent
    /// device clocks can drift slightly over a long meeting; acceptable for a transcript. Uses NAudio's stable
    /// WasapiCapture/WasapiLoopbackCapture (the newer WasapiRecorder/RealtimeCaptureMixer are not in the pinned
    /// preview). Build-verified: the live WASAPI path is exercised by a real recording, not a self-test.
    /// </summary>
    internal sealed class AudioRecorder : IDisposable
    {
        private sealed class Source
        {
            public WasapiCapture Capture;
            // The endpoint the capture was opened on. AudioDevices.Resolve hands ownership to us and NAudio
            // does NOT take it (WasapiCapture.Dispose releases the audio client, never the endpoint), so the
            // Source owns it and disposes it alongside the capture.
            public MMDevice Device;
            public WaveFileWriter Writer;
            public string TempPath;
            public readonly ManualResetEventSlim Stopped = new ManualResetEventSlim(false);
        }

        private readonly List<Source> _sources = new List<Source>();
        public string OutputPath { get; private set; }
        public bool IsRecording { get; private set; }

        /// <summary>Start capturing to <paramref name="outputWavPath"/>. At least one source must be enabled.
        /// Device ids come from <see cref="AudioDevices"/> ("" = system default). Throws on a device-open
        /// failure so the caller can report it and fall back.</summary>
        public void Start(string outputWavPath, bool captureSystem, string systemDeviceId, bool captureMic, string micDeviceId)
        {
            if (IsRecording) return;
            if (!captureSystem && !captureMic)
                throw new InvalidOperationException("Select the microphone, the system output, or both.");

            OutputPath = outputWavPath;
            string dir = Path.GetDirectoryName(outputWavPath);
            string stem = Path.GetFileNameWithoutExtension(outputWavPath);

            try
            {
                // The temp paths are built BEFORE any endpoint is resolved: a bad output path must fail while
                // there is still nothing to leak.
                string systemTemp = Path.Combine(dir, stem + ".system.wav");
                string micTemp = Path.Combine(dir, stem + ".mic.wav");
                if (captureSystem)
                    AddSource(AudioDevices.Resolve(DataFlow.Render, systemDeviceId), true, systemTemp);
                if (captureMic)
                    AddSource(AudioDevices.Resolve(DataFlow.Capture, micDeviceId), false, micTemp);
                foreach (Source s in _sources) s.Capture.StartRecording();
                IsRecording = true;
            }
            catch
            {
                CleanupCaptures();
                throw;
            }
        }

        /// <summary>Open a capture on <paramref name="device"/> (loopback for a render endpoint, plain capture
        /// for a microphone) and register it as a source. Takes ownership of the device either way: on success
        /// the <see cref="Source"/> disposes it, and on a failed open it is disposed here.</summary>
        private void AddSource(MMDevice device, bool loopback, string tempPath)
        {
            WasapiCapture capture = null;
            try
            {
                // A ctor that throws -- the endpoint was yanked between resolve and open, or is in use
                // exclusively -- must not strand the device: nothing else holds a reference to it yet.
                if (loopback) capture = new WasapiLoopbackCapture(device);
                else capture = new WasapiCapture(device);
            }
            catch
            {
                try { device.Dispose(); } catch { }
                throw;
            }

            var s = new Source { Capture = capture, Device = device, TempPath = tempPath };
            // REGISTERED BEFORE THE WRITER, and that order is the whole point. WaveFileWriter's ctor creates a
            // file under a storage location the user types by hand, so it throws on a bad/read-only/full path.
            // With the Add last, a fully-constructed native capture plus its endpoint were unreachable by both
            // CleanupCaptures() and Dispose() and leaked for the life of the process. CleanupCaptures and Stop
            // both tolerate a null Writer, which is what makes registering this early safe.
            _sources.Add(s);
            s.Writer = new WaveFileWriter(tempPath, capture.WaveFormat);
            capture.DataAvailable += (sender, e) =>
            {
                try { if (s.Writer != null) s.Writer.Write(e.Buffer, 0, e.BytesRecorded); } catch { }
            };
            capture.RecordingStopped += (sender, e) =>
            {
                try { if (s.Writer != null) { s.Writer.Dispose(); s.Writer = null; } } catch { }
                // Guarded: Stopped is disposed once the capture is torn down, and an unguarded Set() on a
                // disposed event would throw out of NAudio's capture thread and take the process with it.
                try { s.Stopped.Set(); } catch { }
            };
        }

        /// <summary>Stop, finalize each source, and mix to the 16 kHz mono WAV at <see cref="OutputPath"/>.
        /// Idempotent; returns the output path, or null if nothing was captured.</summary>
        public string Stop()
        {
            if (!IsRecording) return OutputPath;
            IsRecording = false;

            foreach (Source s in _sources)
            {
                try { s.Capture.StopRecording(); } catch { s.Stopped.Set(); }
            }
            foreach (Source s in _sources)
            {
                try { s.Stopped.Wait(TimeSpan.FromSeconds(10)); } catch { }
            }
            foreach (Source s in _sources) DisposeSource(s);

            List<string> temps = _sources.Select(s => s.TempPath).ToList();
            _sources.Clear();
            return MixToWhisperWav(temps, OutputPath);
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

        private void CleanupCaptures()
        {
            foreach (Source s in _sources) DisposeSource(s);
            _sources.Clear();
        }

        /// <summary>
        /// Release everything one source owns, in the only order that is safe, and the SINGLE place that
        /// knows the order -- both <see cref="Stop"/> and <see cref="CleanupCaptures"/> come through here, so
        /// a field added to <see cref="Source"/> has exactly one site to be freed in. Writer first (it is
        /// what holds the temp WAV open), then the capture, then the endpoint the capture's audio client came
        /// from, and only then the stop event: NAudio raises RecordingStopped while Capture.Dispose() joins
        /// the capture thread, so the event must outlive that call. Every step is individually guarded,
        /// because one COM failure must not strand the rest.
        /// </summary>
        private static void DisposeSource(Source s)
        {
            if (s == null) return;
            try { if (s.Writer != null) { s.Writer.Dispose(); s.Writer = null; } } catch { }
            try { if (s.Capture != null) { s.Capture.Dispose(); s.Capture = null; } } catch { }
            try { if (s.Device != null) { s.Device.Dispose(); s.Device = null; } } catch { }
            try { s.Stopped.Dispose(); } catch { }
        }

        public void Dispose()
        {
            try { if (IsRecording) Stop(); else CleanupCaptures(); } catch { }
        }
    }
}
