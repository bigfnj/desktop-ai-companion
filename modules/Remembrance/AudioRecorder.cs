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
                if (captureSystem)
                {
                    MMDevice dev = AudioDevices.Resolve(DataFlow.Render, systemDeviceId);
                    AddSource(new WasapiLoopbackCapture(dev), Path.Combine(dir, stem + ".system.wav"));
                }
                if (captureMic)
                {
                    MMDevice dev = AudioDevices.Resolve(DataFlow.Capture, micDeviceId);
                    AddSource(new WasapiCapture(dev), Path.Combine(dir, stem + ".mic.wav"));
                }
                foreach (Source s in _sources) s.Capture.StartRecording();
                IsRecording = true;
            }
            catch
            {
                CleanupCaptures();
                throw;
            }
        }

        private void AddSource(WasapiCapture capture, string tempPath)
        {
            var s = new Source { Capture = capture, TempPath = tempPath };
            s.Writer = new WaveFileWriter(tempPath, capture.WaveFormat);
            capture.DataAvailable += (sender, e) =>
            {
                try { if (s.Writer != null) s.Writer.Write(e.Buffer, 0, e.BytesRecorded); } catch { }
            };
            capture.RecordingStopped += (sender, e) =>
            {
                try { if (s.Writer != null) { s.Writer.Dispose(); s.Writer = null; } } catch { }
                s.Stopped.Set();
            };
            _sources.Add(s);
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
            foreach (Source s in _sources)
            {
                try { if (s.Writer != null) { s.Writer.Dispose(); s.Writer = null; } } catch { }
                try { s.Capture.Dispose(); } catch { }
            }

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
            foreach (Source s in _sources)
            {
                try { if (s.Writer != null) { s.Writer.Dispose(); s.Writer = null; } } catch { }
                try { if (s.Capture != null) s.Capture.Dispose(); } catch { }
            }
            _sources.Clear();
        }

        public void Dispose()
        {
            try { if (IsRecording) Stop(); else CleanupCaptures(); } catch { }
        }
    }
}
