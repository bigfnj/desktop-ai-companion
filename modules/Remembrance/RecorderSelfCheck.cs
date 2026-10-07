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
            private readonly ManualResetEventSlim _firstPacketGate;
            private readonly ManualResetEventSlim _firstPacketWritten;
            private Thread _thread;
            private volatile bool _stopping;
            private volatile bool _handlerInvokedInline;

            /// <summary>
            /// When set, StartRecording throws the way WasapiCapture refuses an endpoint in exclusive use: AFTER
            /// construction, when AudioClient.Initialize runs, with the system capture's thread already delivering
            /// (verified in the pinned NAudio.Wasapi IL; R-037). <see cref="WaitForFirstPacketBeforeRefusing"/> holds
            /// the refusal until another fake has written a packet, so the "audio had already landed" case is
            /// reached deterministically instead of by racing the system fake's thread.
            /// </summary>
            public bool RefusesStart;
            public bool WaitForFirstPacketBeforeRefusing;

            /// <summary>Carried in RecordingStopped's StoppedEventArgs, the way NAudio reports its capture thread
            /// dying (RA-150). Null for the clean stop every other check drives.</summary>
            public Exception StopException;

            public FakeCapture(bool loopback, ManualResetEventSlim stopGate) : this(loopback, stopGate, null, null) { }

            public FakeCapture(bool loopback, ManualResetEventSlim stopGate,
                ManualResetEventSlim firstPacketGate, ManualResetEventSlim firstPacketWritten)
            {
                Loopback = loopback;
                _stopGate = stopGate;
                _firstPacketGate = firstPacketGate;
                _firstPacketWritten = firstPacketWritten;
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
                if (RefusesStart)
                {
                    if (WaitForFirstPacketBeforeRefusing && _firstPacketWritten != null)
                        _firstPacketWritten.Wait(TimeSpan.FromSeconds(2));
                    throw new InvalidOperationException("simulated: the endpoint is in exclusive use (AUDCLNT_E_DEVICE_IN_USE)");
                }
                _stopping = false;
                _thread = new Thread(Run) { IsBackground = true, Name = "remembrance-selftest-fake-capture" };
                _thread.Start();
            }

            private void Run()
            {
                // 20 ms of 16 kHz mono 16-bit silence per buffer, every 20 ms: enough bytes for the mix to have
                // something to read, and a cadence close to a real capture's.
                var buffer = new byte[640];
                // Held before the FIRST packet while a test keeps the gate closed, so a scratch can be left
                // header-only on purpose (R-037, R-038); polled, so a stop while it is closed still ends the thread.
                if (_firstPacketGate != null)
                    while (!_stopping && !_firstPacketGate.Wait(10)) { }
                while (!_stopping)
                {
                    EventHandler<WaveInEventArgs> data = DataAvailable;
                    if (data != null) data(this, new WaveInEventArgs(buffer, buffer.Length));
                    if (_firstPacketWritten != null) _firstPacketWritten.Set();
                    Thread.Sleep(20);
                }
                if (_stopGate != null) _stopGate.Wait(TimeSpan.FromSeconds(30));
                // The real class nulls its thread field BEFORE raising the event (which is what makes a bare
                // Dispose-and-Join unable to close BUG-009 on its own); the fake does the same.
                _thread = null;
                EventHandler<StoppedEventArgs> handler = RecordingStopped;
                if (handler == null) return;
                var stopped = new StoppedEventArgs(StopException);
                if (ContextSeenAtConstruction == null)
                {
                    _handlerInvokedInline = true;
                    handler(this, stopped);
                }
                else
                {
                    ContextSeenAtConstruction.Post(delegate { handler(this, stopped); }, null);
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

        /// <summary>Records that a loopback source asked for a silent render stream, and that it was released.
        /// A test sets <see cref="Failure"/> to stand in for a render stream that died mid-capture (RA-150).</summary>
        internal sealed class KeepAliveProbe : IKeepAlive
        {
            public WaveFormat Format;
            public volatile bool Disposed;
            public string Failure { get; set; }
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
            /// <summary>Open by default. Reset it and no fake delivers its FIRST packet until it is Set, so a
            /// scratch stays header-only for as long as a test wants (R-037, R-038).</summary>
            public readonly ManualResetEventSlim FirstPacketGate = new ManualResetEventSlim(true);
            /// <summary>Set by any fake once it has written a packet; a refusal can wait on it (R-037).</summary>
            public readonly ManualResetEventSlim FirstPacketWritten = new ManualResetEventSlim(false);
            /// <summary>When set, opening a microphone throws at CONSTRUCTION, the shape of no microphone present at
            /// all (a Remote Desktop session).</summary>
            public bool MicrophoneRefuses;
            /// <summary>When set, the microphone constructs and then refuses at StartRecording, the shape NAudio
            /// raises for an endpoint another program holds exclusively, after the system capture is already
            /// delivering (R-037). With <see cref="RefusalWaitsForFirstPacket"/> it refuses only once a packet has
            /// landed in the other scratch.</summary>
            public bool MicrophoneRefusesAtStart;
            public bool RefusalWaitsForFirstPacket;

            private readonly Func<MMDevice, bool, IWaveIn> _factory;
            private readonly Func<DataFlow, string, MMDevice> _resolver;
            private readonly Func<MMDevice, WaveFormat, IKeepAlive> _keepAlive;

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
                    var fake = new FakeCapture(loopback, StopGate, FirstPacketGate, FirstPacketWritten);
                    if (!loopback && MicrophoneRefusesAtStart)
                    {
                        fake.RefusesStart = true;
                        fake.WaitForFirstPacketBeforeRefusing = RefusalWaitsForFirstPacket;
                    }
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
                FirstPacketGate.Set();
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
                    // "In its own format" is asserted as identity with the loopback capture's WaveFormat -- the
                    // endpoint's mix format, so nothing is resampled on the way in. This line tested only
                    // `Format != null`, so a keep-alive started in any other format passed it (RA-154).
                    FakeCapture loopbackCapture = devices.Captures.FirstOrDefault(f => f.Loopback);
                    check("a loopback source gets a silent render stream in its own format; a microphone does not",
                        devices.KeepAlives.Count == 1 && loopbackCapture != null
                        && ReferenceEquals(devices.KeepAlives[0].Format, loopbackCapture.WaveFormat)
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
                    check("WITNESS a clean mix records no scratch delete that failed",
                        recorder.ScratchCleanup.FailureCount == 0);
                    check("the silent render stream is released with its source",
                        devices.KeepAlives.Count == 1 && devices.KeepAlives[0].Disposed);
                    check("WITNESS a clean stop reports neither a keep-alive nor a capture failure",
                        recorder.KeepAliveEndedEarly == null && recorder.CaptureFailure == null);
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
                    devices.MicrophoneRefuses = false;

                    // ---- a render stream or a capture that dies MID-recording is reported, not lost (RA-150) ----
                    // The probe stands in for PlaybackStopped carrying AUDCLNT_E_DEVICE_INVALIDATED (the default
                    // output's format changed under the stream); the fake capture raises RecordingStopped with an
                    // exception the way NAudio does when its capture thread dies. Both were dropped on the floor.
                    var dying = new AudioRecorder();
                    dying.Start(Path.Combine(scratch, "dying.wav"), true, "", true, "");
                    devices.KeepAlives[devices.KeepAlives.Count - 1].Failure = "simulated: AUDCLNT_E_DEVICE_INVALIDATED";
                    devices.Captures.Last(f => !f.Loopback).StopException =
                        new InvalidOperationException("simulated: the capture thread died");
                    Thread.Sleep(60);
                    dying.Stop();
                    check("a silent render stream that stopped mid-capture is reported at Stop, with the reason",
                        dying.KeepAliveEndedEarly != null && dying.KeepAliveEndedEarly.Contains("DEVICE_INVALIDATED"));
                    check("a capture whose thread died is reported at Stop, naming the source and the reason",
                        dying.CaptureFailure != null && dying.CaptureFailure.Contains("microphone")
                        && dying.CaptureFailure.Contains("capture thread died"));
                    dying.Dispose();

                    // ---- the refusal NAudio actually raises: at StartRecording, with the system capture running (R-037) ----
                    // WasapiCapture's constructor only activates the client and reads the mix format; the
                    // exclusive-use refusal comes from AudioClient.Initialize inside StartRecording, by which time
                    // the system capture's thread is delivering. The construction-time refusal above is the
                    // "no microphone present" shape; this is the other one, in both of its outcomes.
                    devices.FirstPacketGate.Reset();
                    devices.FirstPacketWritten.Reset();
                    devices.MicrophoneRefusesAtStart = true;
                    devices.RefusalWaitsForFirstPacket = false;
                    string lateSystemScratch = Path.Combine(scratch, "late" + AudioRecorder.SystemScratchSuffix);
                    var late = new AudioRecorder();
                    bool lateThrew = false;
                    try { late.Start(Path.Combine(scratch, "late.wav"), true, "", true, ""); }
                    catch (InvalidOperationException) { lateThrew = true; }
                    check("WITNESS a microphone refused at StartRecording, after the system capture is running, fails the start", lateThrew);
                    check("...and the system scratch that had received no packet by then is deleted",
                        !File.Exists(lateSystemScratch));
                    late.Dispose();

                    devices.FirstPacketGate.Set();
                    devices.FirstPacketWritten.Reset();
                    devices.RefusalWaitsForFirstPacket = true;
                    string packetSystemScratch = Path.Combine(scratch, "packet" + AudioRecorder.SystemScratchSuffix);
                    var packet = new AudioRecorder();
                    bool packetThrew = false;
                    try { packet.Start(Path.Combine(scratch, "packet.wav"), true, "", true, ""); }
                    catch (InvalidOperationException) { packetThrew = true; }
                    check("WITNESS the same refusal once a system packet has landed still fails the start", packetThrew);
                    // The standing rule: audio is never deleted on a guess about what the user wants. The scratch
                    // is a purge shape, and the purge now removes the capture folder it leaves empty (R-037).
                    check("...and a scratch that already holds audio is kept for the purge, never deleted on a guess",
                        File.Exists(packetSystemScratch) && AudioRecorder.HasAudio(packetSystemScratch));
                    packet.Dispose();
                    devices.MicrophoneRefusesAtStart = false;
                    devices.RefusalWaitsForFirstPacket = false;

                    // ---- a stop before any packet arrived says so: null, no empty recording.wav (R-038) ----
                    // FileInfo.Length > 44 passed WaveFileWriter's 46-byte header-only scratch as live, so this
                    // returned an empty recording.wav and the caller announced it saved.
                    devices.FirstPacketGate.Reset();
                    string nothingOut = Path.Combine(scratch, "nothing.wav");
                    var nothing = new AudioRecorder();
                    nothing.Start(nothingOut, true, "", true, "");
                    string nothingMixed = nothing.Stop();
                    check("a stop before any packet arrived returns null rather than an empty recording.wav",
                        nothingMixed == null && !File.Exists(nothingOut));
                    check("...and its two header-only scratch WAVs are deleted rather than left for the purge",
                        !File.Exists(Path.Combine(scratch, "nothing" + AudioRecorder.SystemScratchSuffix))
                        && !File.Exists(Path.Combine(scratch, "nothing" + AudioRecorder.MicScratchSuffix)));
                    nothing.Dispose();
                    devices.FirstPacketGate.Set();
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

                var fixtures = new DeletionReport();
                check("WITNESS a header-only WAV is deleted",
                    AudioRecorder.DeleteIfEmptyRecording(empty, fixtures) && !File.Exists(empty));
                check("a WAV with audio in it is kept", !AudioRecorder.DeleteIfEmptyRecording(kept, fixtures) && File.Exists(kept));
                check("a file that does not parse as a WAV is kept",
                    !AudioRecorder.DeleteIfEmptyRecording(notes, fixtures) && File.Exists(notes));

                // A header-only WAV whose delete FAILS (read-only here; a scanner's open handle does the same) is kept
                // and counted by its error type for the caller's log; the catch around the whole method swallowed it
                // (lane feature/remembrance-delete-logging). Keeping the unreadable one above is a decision, not a
                // failure, so the three fixtures count none.
                string stuck = Path.Combine(scratch, "stuck.wav");
                using (new WaveFileWriter(stuck, format)) { }
                File.SetAttributes(stuck, FileAttributes.ReadOnly);
                var stuckFailures = new DeletionReport();
                bool stuckDeleted = AudioRecorder.DeleteIfEmptyRecording(stuck, stuckFailures);
                bool stuckKept = File.Exists(stuck);
                File.SetAttributes(stuck, FileAttributes.Normal);
                check("a header-only WAV whose delete fails is kept and counted by its error type, not swallowed; counted: " +
                      stuckFailures.FilesFailed + " (" + stuckFailures.FailureTypes + ")",
                    !stuckDeleted && stuckKept && stuckFailures.FilesFailed == 1
                    && stuckFailures.FailureTypes == "UnauthorizedAccessException" && fixtures.FailureCount == 0);
            }
            catch (Exception ex) { check("DeleteIfEmptyRecording fixtures: " + ex.Message, false); }
        }
    }
}
