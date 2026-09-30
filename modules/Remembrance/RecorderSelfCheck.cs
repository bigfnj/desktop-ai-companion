using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// The recorder's start and stop paths, exercised with no audio device at all.
    ///
    /// Device capture cannot run on a CI runner or under Remote Desktop, so until 2026-09-29 nothing checked the
    /// stop path and BUG-009 shipped: a 20 s hang on every exit-while-recording, invisible to a green suite.
    /// The three seams on <see cref="AudioRecorder"/> take the device out of the picture; what stays is the
    /// recorder's own logic and, crucially, a fake capture that reproduces the ONE piece of NAudio behaviour
    /// the bug turned on -- see <see cref="FakeCapture"/>. A fake that raised RecordingStopped from anywhere
    /// convenient would have passed with the defect in place.
    /// </summary>
    internal static class RecorderSelfCheck
    {
        /// <summary>
        /// A stand-in for WasapiCapture with NAudio's threading reproduced faithfully: it reads
        /// SynchronizationContext.Current ONCE, in its constructor, exactly as the real class does (verified in
        /// the IL of the pinned NAudio.Wasapi 3.0.0-preview.6), raises DataAvailable inline from its capture
        /// thread, and raises RecordingStopped through the captured context when there is one (Post) or inline
        /// on the capture thread when there is not. The optional gate holds every stop until a test opens it,
        /// so a save can be caught in flight deterministically rather than by racing a sleep.
        /// </summary>
        internal sealed class FakeCapture : IWaveIn
        {
            public readonly bool Loopback;
            public readonly SynchronizationContext ContextSeenAtConstruction;
            private readonly ManualResetEventSlim _stopGate;
            private Thread _thread;
            private volatile bool _stopping;
            private volatile bool _handlerInvokedInline;

            public FakeCapture(bool loopback, ManualResetEventSlim stopGate)
            {
                Loopback = loopback;
                _stopGate = stopGate;
                ContextSeenAtConstruction = SynchronizationContext.Current;
                WaveFormat = new WaveFormat(16000, 16, 1);
            }

            /// <summary>True once RecordingStopped was delivered by a direct call on the capture thread -- the
            /// only delivery that can reach a thread blocked with no message loop.</summary>
            public bool HandlerInvokedInline { get { return _handlerInvokedInline; } }

            public WaveFormat WaveFormat { get; set; }
            public event EventHandler<WaveInEventArgs> DataAvailable;
            public event EventHandler<StoppedEventArgs> RecordingStopped;

            public void StartRecording()
            {
                _stopping = false;
                _thread = new Thread(Run) { IsBackground = true, Name = "remembrance-selftest-fake-capture" };
                _thread.Start();
            }

            private void Run()
            {
                // 20 ms of 16 kHz mono 16-bit silence per buffer, every 20 ms: enough bytes for the mix to have
                // something to read, and a cadence close to a real capture's.
                var buffer = new byte[640];
                while (!_stopping)
                {
                    EventHandler<WaveInEventArgs> data = DataAvailable;
                    if (data != null) data(this, new WaveInEventArgs(buffer, buffer.Length));
                    Thread.Sleep(20);
                }
                if (_stopGate != null) _stopGate.Wait(TimeSpan.FromSeconds(30));
                // The real class nulls its thread field BEFORE raising the event (which is what makes a bare
                // Dispose-and-Join unable to close BUG-009 on its own); the fake does the same.
                _thread = null;
                EventHandler<StoppedEventArgs> handler = RecordingStopped;
                if (handler == null) return;
                if (ContextSeenAtConstruction == null)
                {
                    _handlerInvokedInline = true;
                    handler(this, new StoppedEventArgs());
                }
                else
                {
                    ContextSeenAtConstruction.Post(delegate { handler(this, new StoppedEventArgs()); }, null);
                }
            }

            public void StopRecording() { _stopping = true; }

            public void Dispose()
            {
                StopRecording();
                Thread thread = _thread;
                if (thread != null) thread.Join();
            }
        }

        /// <summary>The context of a UI thread whose message loop has exited: Post queues and never runs. This
        /// is what the module's UI thread looks like from the shutdown path, and what NAudio was handed.</summary>
        internal sealed class DeadLoopSynchronizationContext : SynchronizationContext
        {
            private int _posted;
            public int Posted { get { return _posted; } }
            public override void Post(SendOrPostCallback d, object state) { Interlocked.Increment(ref _posted); }
            public override void Send(SendOrPostCallback d, object state) { Interlocked.Increment(ref _posted); }
        }

        /// <summary>
        /// A UI thread's context, stood in for by the test thread itself: Post queues, and the test runs the
        /// queue when it chooses. The module captures this at Init as its _ui, so everything it would have
        /// marshalled to the UI thread -- every Announce -- runs on the test thread, where the recording
        /// host's lists can be read without racing a pool thread.
        /// </summary>
        internal sealed class QueueSynchronizationContext : SynchronizationContext
        {
            private readonly System.Collections.Concurrent.ConcurrentQueue<KeyValuePair<SendOrPostCallback, object>> _queue =
                new System.Collections.Concurrent.ConcurrentQueue<KeyValuePair<SendOrPostCallback, object>>();

            public int Pending { get { return _queue.Count; } }

            public override void Post(SendOrPostCallback d, object state)
            {
                _queue.Enqueue(new KeyValuePair<SendOrPostCallback, object>(d, state));
            }

            public override void Send(SendOrPostCallback d, object state) { d(state); }

            /// <summary>Run everything queued so far, on the calling thread. Returns how many ran.</summary>
            public int Drain()
            {
                int ran = 0;
                KeyValuePair<SendOrPostCallback, object> item;
                while (_queue.TryDequeue(out item)) { item.Key(item.Value); ran++; }
                return ran;
            }

            /// <summary>Wait until at least one callback is queued, or the deadline passes.</summary>
            public bool WaitForPost(TimeSpan timeout)
            {
                return SpinWait.SpinUntil(delegate { return _queue.Count > 0; }, timeout);
            }
        }

        /// <summary>Records that a loopback source asked for a silent render stream, and that it was released.</summary>
        internal sealed class KeepAliveProbe : IDisposable
        {
            public WaveFormat Format;
            public volatile bool Disposed;
            public void Dispose() { Disposed = true; }
        }

        /// <summary>
        /// The three recorder seams swapped for fakes for the lifetime of one <c>using</c> block, and restored
        /// whatever happens inside it. The module-level checks use this too, to drive a recording through the
        /// module's own start and stop paths.
        /// </summary>
        internal sealed class FakeDevices : IDisposable
        {
            public readonly List<FakeCapture> Captures = new List<FakeCapture>();
            public readonly List<KeepAliveProbe> KeepAlives = new List<KeepAliveProbe>();
            /// <summary>Open by default. Reset it and every fake holds its RecordingStopped until it is Set.</summary>
            public readonly ManualResetEventSlim StopGate = new ManualResetEventSlim(true);
            /// <summary>When set, opening a microphone throws, the way an endpoint in exclusive use does.</summary>
            public bool MicrophoneRefuses;

            private readonly Func<MMDevice, bool, IWaveIn> _factory;
            private readonly Func<DataFlow, string, MMDevice> _resolver;
            private readonly Func<MMDevice, WaveFormat, IDisposable> _keepAlive;

            public FakeDevices()
            {
                _factory = AudioRecorder.CaptureFactory;
                _resolver = AudioRecorder.DeviceResolver;
                _keepAlive = AudioRecorder.KeepAliveFactory;
                AudioRecorder.DeviceResolver = delegate { return null; };
                AudioRecorder.CaptureFactory = delegate(MMDevice device, bool loopback)
                {
                    if (!loopback && MicrophoneRefuses)
                        throw new InvalidOperationException("simulated: the microphone is in exclusive use");
                    var fake = new FakeCapture(loopback, StopGate);
                    lock (Captures) Captures.Add(fake);
                    return fake;
                };
                AudioRecorder.KeepAliveFactory = delegate(MMDevice device, WaveFormat format)
                {
                    var probe = new KeepAliveProbe { Format = format };
                    lock (KeepAlives) KeepAlives.Add(probe);
                    return probe;
                };
            }

            public void Dispose()
            {
                AudioRecorder.CaptureFactory = _factory;
                AudioRecorder.DeviceResolver = _resolver;
                AudioRecorder.KeepAliveFactory = _keepAlive;
                StopGate.Set();
            }
        }

        /// <summary>The recorder on its own: construction, stop delivery, mix, scratch cleanup, failed start.
        /// <paramref name="scratch"/> is a directory this call may create and write into.</summary>
        public static void Run(Action<string, bool> check, string scratch)
        {
            var dead = new DeadLoopSynchronizationContext();
            SynchronizationContext previous = SynchronizationContext.Current;
            using (var devices = new FakeDevices())
            {
                try
                {
                    // The thread that starts a recording in the app is the WinForms UI thread, and on the
                    // shutdown path its message loop is gone. A context whose posts never run is that thread.
                    SynchronizationContext.SetSynchronizationContext(dead);
                    Directory.CreateDirectory(scratch);
                    string output = Path.Combine(scratch, "recording.wav");
                    string systemScratch = Path.Combine(scratch, "recording" + AudioRecorder.SystemScratchSuffix);
                    string micScratch = Path.Combine(scratch, "recording" + AudioRecorder.MicScratchSuffix);

                    var recorder = new AudioRecorder();
                    recorder.Start(output, true, "", true, "");
                    check("WITNESS a capture was constructed for each of the two sources", devices.Captures.Count == 2);
                    check("captures are constructed with NO SynchronizationContext current (BUG-009)",
                        devices.Captures.Count == 2 && devices.Captures.All(f => f.ContextSeenAtConstruction == null));
                    check("...and the caller's own context is put back afterwards",
                        ReferenceEquals(SynchronizationContext.Current, dead));
                    check("a loopback source gets a silent render stream in its own format; a microphone does not",
                        devices.KeepAlives.Count == 1 && devices.KeepAlives[0].Format != null
                        && devices.Captures.Count(f => f.Loopback) == 1);
                    check("WITNESS both scratch WAVs are being written", File.Exists(systemScratch) && File.Exists(micScratch));

                    Thread.Sleep(150);   // a few buffers from each fake
                    var stopwatch = Stopwatch.StartNew();
                    string mixed = recorder.Stop();
                    stopwatch.Stop();
                    check("RecordingStopped is delivered on the capture thread, not posted to a dead message loop "
                          + "(Stop took " + stopwatch.ElapsedMilliseconds + " ms; capture stop "
                          + (long)recorder.LastCaptureStopTime.TotalMilliseconds + " ms)",
                        devices.Captures.All(f => f.HandlerInvokedInline) && dead.Posted == 0);
                    check("the two scratch tracks are mixed into the output",
                        mixed != null && File.Exists(mixed) && new FileInfo(mixed).Length > 44);
                    check("the scratch WAVs are deleted after a successful mix",
                        !File.Exists(systemScratch) && !File.Exists(micScratch));
                    check("the silent render stream is released with its source",
                        devices.KeepAlives.Count == 1 && devices.KeepAlives[0].Disposed);
                    recorder.Dispose();

                    // ---- a start that fails half-way leaves no header-only scratch behind (F171) ----
                    devices.MicrophoneRefuses = true;
                    string failedSystemScratch = Path.Combine(scratch, "failed" + AudioRecorder.SystemScratchSuffix);
                    var failing = new AudioRecorder();
                    bool threw = false;
                    try { failing.Start(Path.Combine(scratch, "failed.wav"), true, "", true, ""); }
                    catch (InvalidOperationException) { threw = true; }
                    check("WITNESS a microphone that refuses to open fails the start", threw);
                    check("...and the header-only system scratch it had already created is deleted",
                        !File.Exists(failedSystemScratch));
                    failing.Dispose();
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previous);
                }
            }

            // ---- DeleteIfEmptyRecording keeps whatever it cannot vouch for ----
            // It runs after a failed start, in a folder the user chose, so "empty" has to mean a WAV this
            // module's own writer left with no audio -- not "small", and never "something I could not parse".
            try
            {
                var format = new WaveFormat(16000, 16, 1);
                string empty = Path.Combine(scratch, "empty.wav");
                using (new WaveFileWriter(empty, format)) { }
                string kept = Path.Combine(scratch, "kept.wav");
                using (var writer = new WaveFileWriter(kept, format)) writer.Write(new byte[3200], 0, 3200);
                string notes = Path.Combine(scratch, "notes.system.wav");
                File.WriteAllText(notes, "not a wav at all");

                check("WITNESS a header-only WAV is deleted",
                    AudioRecorder.DeleteIfEmptyRecording(empty) && !File.Exists(empty));
                check("a WAV with audio in it is kept", !AudioRecorder.DeleteIfEmptyRecording(kept) && File.Exists(kept));
                check("a file that does not parse as a WAV is kept",
                    !AudioRecorder.DeleteIfEmptyRecording(notes) && File.Exists(notes));
            }
            catch (Exception ex) { check("DeleteIfEmptyRecording fixtures: " + ex.Message, false); }
        }
    }
}
