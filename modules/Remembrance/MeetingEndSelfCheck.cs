using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.MeetingApps;
using DesktopAICompanion.Modules;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// A RecordingHost that also answers ITrayQuestions (host 1.6.0) the way the module needs to be tested against it:
    /// every question is CHECKED against the ABI's rules first, the way the host's TrayQuestionRules refuses one, so a
    /// question the real host would refuse fails the self-test rather than a user's afternoon (Invalid, and the
    /// Refused answer the host would give). The test answers a question (Answer), lets it time out (TimeOut), or leaves
    /// it; cancelling the module's token withdraws it, as the host does, unless HonourWithdraw is off (a click racing the
    /// withdraw). AI Brain's StandDownHost is the pattern: a module-local double, since ModuleKit carries none.
    /// </summary>
    internal sealed class QuestionsHost : DesktopAICompanion.ModuleKit.Testing.RecordingHost, ITrayQuestions
    {
        internal sealed class Asked
        {
            internal string ModuleId;
            internal TrayQuestion Question;
            internal string Invalid;
            internal volatile bool Withdrawn;
            // The module cancelled its token. With HonourWithdraw off the question stays unanswered (the user's click
            // reached the host first and its answer is still on its way), but it is no longer pending, as on the host.
            internal volatile bool WithdrawRequested;
            internal readonly TaskCompletionSource<TrayAnswer> Answer =
                new TaskCompletionSource<TrayAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool Done { get { return Answer.Task.IsCompleted; } }
        }

        private readonly List<Asked> _asks = new List<Asked>();
        internal Func<DateTimeOffset> Clock = () => DateTimeOffset.UtcNow;
        internal bool HonourWithdraw = true;

        internal List<Asked> Asks { get { lock (_asks) return new List<Asked>(_asks); } }
        internal List<string> InvalidAsks { get { return Asks.Where(a => a.Invalid != null).Select(a => a.Question.Key + ": " + a.Invalid).ToList(); } }

        public Task<TrayAnswer> AskAsync(string moduleId, TrayQuestion question, CancellationToken withdraw)
        {
            var a = new Asked { ModuleId = moduleId, Question = Copy(question) };
            a.Invalid = Validate(question, Clock());
            if (a.Invalid == null && Pending(question.Key) != null) a.Invalid = "duplicate-key";
            lock (_asks) _asks.Add(a);
            if (a.Invalid != null)
            {
                a.Answer.TrySetResult(new TrayAnswer { Outcome = TrayQuestionOutcome.Refused, Reason = a.Invalid });
                return a.Answer.Task;
            }
            withdraw.Register(delegate
            {
                a.WithdrawRequested = true;
                if (HonourWithdraw && a.Answer.TrySetResult(new TrayAnswer { Outcome = TrayQuestionOutcome.Withdrawn })) a.Withdrawn = true;
            });
            return a.Answer.Task;
        }

        /// <summary>The newest question with this key that has not ended and was not withdrawn.</summary>
        internal Asked Pending(string key)
        {
            lock (_asks) return _asks.LastOrDefault(a => a.Question.Key == key && a.Invalid == null && !a.Done && !a.WithdrawRequested);
        }

        internal Asked Last(string key)
        {
            lock (_asks) return _asks.LastOrDefault(a => a.Question.Key == key);
        }

        internal bool Answer(string key, string choiceId) { return Answer(Pending(key), choiceId); }

        internal bool Answer(Asked a, string choiceId)
        {
            if (a == null || !a.Question.Choices.Any(c => c.Id == choiceId)) return false;
            return a.Answer.TrySetResult(new TrayAnswer { Outcome = TrayQuestionOutcome.Chosen, ChoiceId = choiceId, WasShown = true });
        }

        internal bool TimeOut(string key)
        {
            Asked a = Pending(key);
            return a != null && a.Answer.TrySetResult(new TrayAnswer
            {
                Outcome = TrayQuestionOutcome.TimedOut, ChoiceId = a.Question.TimeoutChoiceId, WasShown = true,
            });
        }

        private static TrayQuestion Copy(TrayQuestion q)
        {
            if (q == null) return new TrayQuestion();
            return new TrayQuestion
            {
                Key = q.Key, Title = q.Title, Text = q.Text,
                Choices = q.Choices == null ? null : q.Choices.Select(c => c == null ? null : new TrayChoice(c.Id, c.Label)).ToList(),
                TimeoutChoiceId = q.TimeoutChoiceId, Timeout = q.Timeout, Deadline = q.Deadline, RunsWhileHeld = q.RunsWhileHeld,
            };
        }

        private static readonly Regex KeyShape = new Regex("^[A-Za-z0-9._-]{1,64}$");
        private static readonly Regex IdShape = new Regex("^[A-Za-z0-9._-]{1,32}$");

        /// <summary>The ABI's rules for a question (PluginApi.cs, TrayQuestion), or null when it keeps them all.</summary>
        internal static string Validate(TrayQuestion q, DateTimeOffset now)
        {
            if (q == null) return "no-question";
            if (q.Key == null || !KeyShape.IsMatch(q.Key)) return "bad-key";
            if (q.Title != null && q.Title.Length > 64) return "title-too-long";
            string text = Regex.Replace(new string((q.Text ?? "").Where(ch => !char.IsControl(ch)).ToArray()), "\\s+", " ").Trim();
            if (text.Length < 1) return "no-text";
            if (text.Length > 240) return "text-too-long";
            if (q.Choices == null || q.Choices.Count < 2) return "too-few-choices";
            if (q.Choices.Count > 4) return "too-many-choices";
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TrayChoice c in q.Choices)
            {
                if (c == null || c.Id == null || !IdShape.IsMatch(c.Id)) return "bad-choice-id";
                if (string.IsNullOrEmpty(c.Label) || c.Label.Length > 24) return "bad-choice-label";
                if (!ids.Add(c.Id)) return "duplicate-choice-id";
                if (!labels.Add(c.Label)) return "duplicate-choice-label";
            }
            if (q.TimeoutChoiceId == null || !ids.Contains(q.TimeoutChoiceId)) return "bad-timeout-choice";
            if (q.Timeout < TimeSpan.FromSeconds(15) || q.Timeout > TimeSpan.FromMinutes(60)) return "bad-timeout";
            if (q.Deadline.HasValue)
            {
                TimeSpan ahead = q.Deadline.Value - now;
                if (ahead < TimeSpan.FromSeconds(5) || ahead > TimeSpan.FromHours(24)) return "bad-deadline";
            }
            return null;
        }
    }

    public sealed partial class RemembranceModule
    {
        // ---- signals for the converter's checks ----

        /// <summary>Encode frames of a test signal in a device format: 32-bit float, or 16-bit PCM; any channel count.
        /// <paramref name="sample"/> takes the frame and the channel.</summary>
        private static byte[] Encode(WaveFormat format, int frames, Func<int, int, float> sample)
        {
            WaveFormat standard = TrackConverter.Standardize(format);
            int ch = format.Channels;
            bool isFloat = standard.Encoding == WaveFormatEncoding.IeeeFloat;
            int bytesPer = isFloat ? 4 : 2;
            var bytes = new byte[frames * ch * bytesPer];
            int at = 0;
            for (int f = 0; f < frames; f++)
                for (int c = 0; c < ch; c++)
                {
                    float v = sample(f, c);
                    if (isFloat)
                    {
                        byte[] b = BitConverter.GetBytes(v);
                        Buffer.BlockCopy(b, 0, bytes, at, 4);
                        at += 4;
                    }
                    else
                    {
                        short s = (short)(Math.Max(-1f, Math.Min(1f, v)) * 32767);
                        bytes[at++] = (byte)s;
                        bytes[at++] = (byte)(s >> 8);
                    }
                }
            return bytes;
        }

        /// <summary>A speech-shaped test signal: tones with a little noise, a different pitch per channel.</summary>
        private static Func<int, int, float> TestSignal(int rate)
        {
            return delegate(int f, int c)
            {
                double t = f / (double)rate;
                // Deterministic noise from the frame and channel, so two encodings of the same signal agree exactly.
                uint h = (uint)(f * 2654435761u) ^ (uint)(c * 40503u);
                double noise = ((h % 1000) / 1000.0 - 0.5) * 0.05;
                return (float)(0.3 * Math.Sin(2 * Math.PI * (440 + 50 * c) * t) + noise);
            };
        }

        /// <summary>The offline chain MixToWhisperWav ran on every track before 2.4.0, verbatim apart from the input being
        /// in memory: decode, StereoToMono 0.5/0.5 or the first of more channels, WdlResamplingSampleProvider to 16 kHz,
        /// then CreateWaveFile16's clamp and (short)(f * 32767).</summary>
        private static short[] OldOfflineChain(WaveFormat format, byte[] bytes)
        {
            var raw = new RawSourceWaveStream(new MemoryStream(bytes), TrackConverter.Standardize(format));
            ISampleProvider sp = raw.ToSampleProvider();
            if (sp.WaveFormat.Channels == 2) sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
            else if (sp.WaveFormat.Channels > 2) sp = new MultiplexingSampleProvider(new[] { sp }, 1);
            if (sp.WaveFormat.SampleRate != 16000) sp = new WdlResamplingSampleProvider(sp, 16000);
            var output = new List<short>();
            var buffer = new float[4096];
            int n;
            while ((n = sp.Read(buffer.AsSpan())) > 0)
                for (int i = 0; i < n; i++) output.Add((short)(Math.Max(-1f, Math.Min(1f, buffer[i])) * 32767));
            return output.ToArray();
        }

        /// <summary>Feed bytes through a converter in packets of varying size, one of them splitting a frame, and return
        /// the 16-bit samples it handed on.</summary>
        private static short[] Converted(TrackConverter converter, WaveFormat format, byte[] bytes, out long meteredSamples)
        {
            var output = new List<byte>();
            long metered = 0;
            int packetFrames = Math.Max(1, format.SampleRate / 100);
            int pos = 0, round = 0;
            while (pos < bytes.Length)
            {
                int len = packetFrames * format.BlockAlign * (1 + round % 3);
                if (round == 7) len += 3;   // ends inside a frame: the carry must hold the rest
                len = Math.Min(len, bytes.Length - pos);
                byte[] packet = new byte[len];
                Buffer.BlockCopy(bytes, pos, packet, 0, len);
                converter.Convert(packet, len, delegate(byte[] pcm, int n) { for (int i = 0; i < n; i++) output.Add(pcm[i]); },
                    delegate(float[] f, int n) { metered += n; });
                pos += len;
                round++;
            }
            meteredSamples = metered;
            var shorts = new short[output.Count / 2];
            for (int i = 0; i < shorts.Length; i++) shorts[i] = (short)(output[2 * i] | (output[2 * i + 1] << 8));
            return shorts;
        }

        private static long SamplesIn(string wav, out WaveFormat format)
        {
            using (var reader = new WaveFileReader(wav))
            {
                format = reader.WaveFormat;
                return reader.Length / Math.Max(1, reader.WaveFormat.BlockAlign);
            }
        }

        private static void WriteWav(string path, WaveFormat format, byte[] bytes)
        {
            using (var writer = new WaveFileWriter(path, format)) writer.Write(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// The converter against the chain it replaces, and the recorder around it (lane feature/meeting-end, 2.4.0): the
        /// same samples within 2 LSB and the same length within 2 samples for the device shapes this module meets, a
        /// 16 kHz mono 16-bit device passed through byte for byte, a ten-minute burst coming out at exactly its length,
        /// the levels the watch reads, a track whose writes fail stopping alone and saying so, the RF64 backstop, the trim,
        /// and a scratch an earlier version wrote in its device's format still mixing.
        /// </summary>
        private static void SelfCheckTrackConverter(Action<string, bool> check)
        {
            string scratch = Path.Combine(Path.GetTempPath(), "dp-remembrance-convert-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Func<string, WaveFormat, WaveFileWriter> savedFactory = AudioRecorder.ScratchWriterFactory;
            SynchronizationContext previous = SynchronizationContext.Current;
            try
            {
                Directory.CreateDirectory(scratch);
                // ---- the same audio as the old offline chain ----
                var shapes = new[]
                {
                    new KeyValuePair<string, WaveFormat>("48 kHz float stereo", WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)),
                    new KeyValuePair<string, WaveFormat>("48 kHz float stereo as WAVE_FORMAT_EXTENSIBLE (a WASAPI mix format)", new WaveFormatExtensible(48000, 32, 2)),
                    new KeyValuePair<string, WaveFormat>("44.1 kHz 16-bit mono", new WaveFormat(44100, 16, 1)),
                    new KeyValuePair<string, WaveFormat>("48 kHz float 6-channel", WaveFormat.CreateIeeeFloatWaveFormat(48000, 6)),
                };
                foreach (KeyValuePair<string, WaveFormat> shape in shapes)
                {
                    WaveFormat format = shape.Value;
                    byte[] bytes = Encode(format, format.SampleRate * 10, TestSignal(format.SampleRate));
                    short[] reference = OldOfflineChain(format, bytes);
                    string why;
                    TrackConverter converter = TrackConverter.TryCreate(format, out why);
                    long metered = 0;
                    short[] converted = converter != null ? Converted(converter, format, bytes, out metered) : new short[0];
                    int common = Math.Min(reference.Length, converted.Length);
                    int worst = 0;
                    for (int i = 0; i < common; i++) worst = Math.Max(worst, Math.Abs(reference[i] - converted[i]));
                    check("converter: " + shape.Key + " comes out as the old offline chain made it, within 2 LSB and 2 samples (" +
                          converted.Length.ToString(CultureInfo.InvariantCulture) + " samples against " +
                          reference.Length.ToString(CultureInfo.InvariantCulture) + ", worst " + worst.ToString(CultureInfo.InvariantCulture) + " LSB)",
                        converter != null && !converter.PassThrough && Math.Abs(reference.Length - converted.Length) <= 2 && worst <= 2
                        && metered == converted.Length);
                }

                // ---- a device already at 16 kHz mono 16-bit goes through untouched ----
                {
                    var format = new WaveFormat(16000, 16, 1);
                    byte[] bytes = Encode(format, 16000 * 3, TestSignal(16000));
                    string why;
                    TrackConverter converter = TrackConverter.TryCreate(format, out why);
                    long metered = 0;
                    short[] converted = converter != null ? Converted(converter, format, bytes, out metered) : new short[0];
                    bool same = converted.Length * 2 == bytes.Length;
                    for (int i = 0; same && i < converted.Length; i++) same = converted[i] == (short)(bytes[2 * i] | (bytes[2 * i + 1] << 8));
                    check("converter: a 16 kHz mono 16-bit device is passed through byte for byte, a carried half-frame included",
                        converter != null && converter.PassThrough && same && metered == converted.Length);
                }

                // ---- a format NAudio has no decoder for falls back to the device's own ----
                {
                    string why;
                    TrackConverter none = TrackConverter.TryCreate(WaveFormat.CreateALawFormat(8000, 1), out why);
                    check("converter: a format with no decoder (A-law) is refused with a reason, so the track is written as it comes: " + why,
                        none == null && !string.IsNullOrEmpty(why));
                }

                // ---- the levels the watch reads ----
                {
                    var meter = new TrackMeter();
                    var tone = new float[16000 * 3];
                    for (int i = 0; i < tone.Length; i++) tone[i] = (float)(0.1 * Math.Sin(2 * Math.PI * 1000 * i / 16000.0));
                    meter.Add(tone, tone.Length);
                    meter.Add(new float[16000], 16000);
                    List<LevelSecond> seconds = meter.CopySince(0);
                    bool toneOk = seconds.Count == 4 && seconds.Take(3).All(s => Math.Abs(s.RmsDb + 23.01f) < 0.3f && Math.Abs(s.MinFrameDb + 23.01f) < 0.3f);
                    check("meter: a 0.1-amplitude sine measures about -23 dBFS a second, its quietest frame the same (" +
                          (seconds.Count > 0 ? seconds[0].RmsDb.ToString("0.00", CultureInfo.InvariantCulture) : "none") + " dB)", toneOk);
                    check("meter: digital silence is -120 dB, the floor, both RMS and quietest frame",
                        seconds.Count == 4 && seconds[3].RmsDb == TrackMeter.SilenceDb && seconds[3].MinFrameDb == TrackMeter.SilenceDb);
                    check("meter: a reader asks for the seconds it has not seen, by index",
                        meter.CopySince(3).Count == 1 && meter.CopySince(3)[0].Index == 3 && meter.CopySince(4).Count == 0
                        && meter.SecondsMeasured == 4);
                }

                // ---- through the recorder: ten minutes in a burst, at exactly its length ----
                var dead = new RecorderSelfCheck.DeadLoopSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(dead);
                using (var devices = new RecorderSelfCheck.FakeDevices())
                {
                    // 100 ms of a 1 kHz tone at 48 kHz float stereo is exactly a hundred periods, so one packet repeats.
                    var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
                    byte[] packet = Encode(format, 4800, delegate(int f, int c) { return (float)(0.25 * Math.Sin(2 * Math.PI * 1000 * f / 48000.0)); });
                    devices.SystemFormat = format;
                    devices.SystemPacket = delegate { return packet; };
                    devices.BurstPackets = 6000;
                    var writers = new List<WaveFileWriter>();
                    AudioRecorder.ScratchWriterFactory = delegate(string p, WaveFormat f)
                    {
                        WaveFileWriter w = savedFactory(p, f);
                        lock (writers) writers.Add(w);
                        return w;
                    };
                    string output = Path.Combine(scratch, "burst.wav");
                    var recorder = new AudioRecorder();
                    recorder.Start(output, true, "", false, "");
                    bool burst = devices.Captures.Count == 1 && devices.Captures[0].BurstDone.Wait(TimeSpan.FromSeconds(120));
                    long scratchBytes = writers.Count == 1 ? writers[0].Length : -1;
                    WaveFormat scratchFormat = writers.Count == 1 ? writers[0].WaveFormat : null;
                    string mixed = recorder.Stop();
                    WaveFormat outFormat = null;
                    long samples = mixed != null ? SamplesIn(mixed, out outFormat) : -1;
                    check("converter: 600 s of 48 kHz float stereo in a burst is written to its scratch as 16 kHz mono 16-bit, " +
                          (scratchBytes / 2).ToString(CultureInfo.InvariantCulture) + " samples, about 19 MB where the device format took 230",
                        burst && scratchFormat != null && scratchFormat.SampleRate == 16000 && scratchFormat.Channels == 1
                        && scratchFormat.BitsPerSample == 16 && Math.Abs(scratchBytes / 2 - 9600000) <= 4);
                    check("converter: ...and the recording is 9,600,000 samples within 4, 16 kHz mono 16-bit (" +
                          samples.ToString(CultureInfo.InvariantCulture) + ")",
                        outFormat != null && outFormat.SampleRate == 16000 && outFormat.Channels == 1 && outFormat.BitsPerSample == 16
                        && Math.Abs(samples - 9600000) <= 4);
                    check("WITNESS converter: a burst ten times faster than real time is no track shorter than the clock",
                        recorder.ShortTracks.Count == 0);
                    recorder.Dispose();
                    AudioRecorder.ScratchWriterFactory = savedFactory;
                }

                // ---- a write that fails stops that track alone, and says which ----
                using (var devices = new RecorderSelfCheck.FakeDevices())
                {
                    AudioRecorder.ScratchWriterFactory = delegate(string p, WaveFormat f)
                    {
                        if (!p.EndsWith(AudioRecorder.MicScratchSuffix, StringComparison.OrdinalIgnoreCase)) return savedFactory(p, f);
                        return new WaveFileWriter(new FailingStream(p, 16000), f, new WaveFileWriterOptions { EnableRf64 = true });
                    };
                    string output = Path.Combine(scratch, "failing.wav");
                    var recorder = new AudioRecorder();
                    recorder.Start(output, true, "", true, "");
                    bool failed = SpinWait.SpinUntil(delegate { return recorder.WriteFailures.Count > 0; }, TimeSpan.FromSeconds(10));
                    Thread.Sleep(200);   // the system track carries on meanwhile
                    IReadOnlyList<TrackWriteFailure> during = recorder.WriteFailures;
                    string mixed = recorder.Stop();
                    IReadOnlyList<TrackWriteFailure> after = recorder.WriteFailures;
                    TrackWriteFailure f0 = during.Count > 0 ? during[0] : null;
                    check("recorder: a scratch whose writes start failing stops THAT track, named, with how far in and the exception's type: " +
                          (f0 == null ? "none" : f0.Track + " at " + f0.At.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s, " + f0.ExceptionType),
                        failed && during.Count == 1 && f0.Track == "microphone" && f0.ExceptionType == "IOException"
                        && f0.At > TimeSpan.Zero && f0.At <= TimeSpan.FromSeconds(1));
                    check("recorder: ...the other track is untouched and the recording still mixes, and the failure is still reported after the stop",
                        mixed != null && File.Exists(mixed) && after.Count == 1 && after[0].Track == "microphone");
                    recorder.Dispose();
                    AudioRecorder.ScratchWriterFactory = savedFactory;
                }
                SynchronizationContext.SetSynchronizationContext(previous);

                // ---- RF64: the 4 GB backstop, promoted at a tiny threshold, reads back and mixes ----
                {
                    string rf = Path.Combine(scratch, "rf64" + AudioRecorder.MicScratchSuffix);
                    var format = new WaveFormat(16000, 16, 1);
                    byte[] second = Encode(format, 16000, TestSignal(16000));
                    using (var writer = new WaveFileWriter(rf, format, new WaveFileWriterOptions { EnableRf64 = true, Rf64PromotionThreshold = 8000 }))
                        writer.Write(second, 0, second.Length);
                    byte[] head = new byte[4];
                    using (var fs = File.OpenRead(rf)) fs.ReadExactly(head, 0, 4);
                    string out64 = Path.Combine(scratch, "rf64.wav");
                    AudioRecorder.MixReport report;
                    string mixed = AudioRecorder.MixToWhisperWav(new List<AudioRecorder.MixTrack> { new AudioRecorder.MixTrack(rf, "microphone") },
                        out64, new DeletionReport(), null, TimeSpan.Zero, out report);
                    WaveFormat f;
                    check("recorder: an RF64 scratch (promoted past a tiny threshold) is recognised, read back and mixed whole",
                        System.Text.Encoding.ASCII.GetString(head) == "RF64" && AudioRecorder.HasAudio(out64) && mixed == out64
                        && SamplesIn(out64, out f) == 16000);
                    WaveFileWriter real = AudioRecorder.ScratchWriterFactory(Path.Combine(scratch, "real" + AudioRecorder.MicScratchSuffix), format);
                    real.Dispose();
                    byte[] realHead = File.ReadAllBytes(Path.Combine(scratch, "real" + AudioRecorder.MicScratchSuffix));
                    check("recorder: the shipped scratch writer reserves RF64's room (an 82-byte header holding a JUNK chunk), so 4 GB promotes rather than throws",
                        realHead.Length == 82 && System.Text.Encoding.ASCII.GetString(realHead, 12, 4) == "JUNK");
                }

                // ---- the trim ----
                {
                    var format = new WaveFormat(16000, 16, 1);
                    string a = Path.Combine(scratch, "trim" + AudioRecorder.SystemScratchSuffix);
                    string b = Path.Combine(scratch, "trim" + AudioRecorder.MicScratchSuffix);
                    WriteWav(a, format, Encode(format, 16000 * 30, TestSignal(16000)));
                    WriteWav(b, format, Encode(format, 16000 * 30, TestSignal(16000)));
                    string trimmed = Path.Combine(scratch, "trim.wav");
                    AudioRecorder.MixReport report;
                    AudioRecorder.MixToWhisperWav(new List<AudioRecorder.MixTrack> { new AudioRecorder.MixTrack(a, "system output"), new AudioRecorder.MixTrack(b, "microphone") },
                        trimmed, new DeletionReport(), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), out report);
                    WaveFormat f;
                    check("recorder: keeping 10 s of two 30 s tracks writes 160,000 samples and reports 20 s trimmed, 10 s kept",
                        SamplesIn(trimmed, out f) == 160000 && report.Trimmed == TimeSpan.FromSeconds(20) && report.Kept == TimeSpan.FromSeconds(10));
                    WriteWav(a, format, Encode(format, 16000 * 30, TestSignal(16000)));
                    string untrimmed = Path.Combine(scratch, "untrimmed.wav");
                    AudioRecorder.MixToWhisperWav(new List<AudioRecorder.MixTrack> { new AudioRecorder.MixTrack(a, "system output") },
                        untrimmed, new DeletionReport(), TimeSpan.FromMilliseconds(400), TimeSpan.Zero, out report);
                    check("recorder: a keep under a second is no trim (OffsetSampleProvider reads a zero Take as the whole track)",
                        SamplesIn(untrimmed, out f) == 480000 && report.Trimmed == TimeSpan.Zero);
                }

                // ---- a scratch in the device's own format still mixes (an earlier version's, or the fallback's) ----
                {
                    var legacy = new[]
                    {
                        new KeyValuePair<string, WaveFormat>("48 kHz float stereo", WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)),
                        new KeyValuePair<string, WaveFormat>("48 kHz float stereo written as WAVE_FORMAT_EXTENSIBLE", new WaveFormatExtensible(48000, 32, 2)),
                    };
                    foreach (KeyValuePair<string, WaveFormat> shape in legacy)
                    {
                        string p = Path.Combine(scratch, "legacy" + AudioRecorder.SystemScratchSuffix);
                        WriteWav(p, shape.Value, Encode(shape.Value, 48000 * 10, TestSignal(48000)));
                        string outLegacy = Path.Combine(scratch, "legacy.wav");
                        AudioRecorder.MixReport report;
                        string mixed = null;
                        try
                        {
                            mixed = AudioRecorder.MixToWhisperWav(new List<AudioRecorder.MixTrack> { new AudioRecorder.MixTrack(p, "system output") },
                                outLegacy, new DeletionReport(), null, TimeSpan.Zero, out report);
                        }
                        catch (Exception ex) { check("recorder: a legacy " + shape.Key + " scratch threw " + ex.GetType().Name, false); }
                        WaveFormat f;
                        long samples = mixed != null ? SamplesIn(mixed, out f) : -1;
                        check("recorder: a scratch in the device's format, " + shape.Key + ", still mixes to 16 kHz (" +
                              samples.ToString(CultureInfo.InvariantCulture) + " samples)", Math.Abs(samples - 160000) <= 2);
                    }
                }
            }
            catch (Exception ex) { check("converter checks threw " + ex.GetType().Name + ": " + ex.Message, false); }
            finally
            {
                AudioRecorder.ScratchWriterFactory = savedFactory;
                SynchronizationContext.SetSynchronizationContext(previous);
                try { Directory.Delete(scratch, true); } catch { }
            }
        }

        /// <summary>A file stream that refuses every write past a byte count, the way a full disk or NAudio's 4 GB limit
        /// refuses one; the header's rewrite at the close (at its own offset, before the limit) still goes through.</summary>
        private sealed class FailingStream : FileStream
        {
            private readonly long _limit;
            internal FailingStream(string path, long limit) : base(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read) { _limit = limit; }
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Position + count > _limit && Position > 100) throw new IOException("simulated: there is not enough space on the disk");
                base.Write(buffer, offset, count);
            }
            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (Position + buffer.Length > _limit && Position > 100) throw new IOException("simulated: there is not enough space on the disk");
                base.Write(buffer);
            }
        }

        // ---- the watch, pure ----

        private static readonly DateTimeOffset WatchT0 = new DateTimeOffset(2026, 10, 15, 16, 0, 0, TimeSpan.Zero);

        private static LevelSecond Talk(int i) { return new LevelSecond(i, -25f, -60f); }
        private static LevelSecond Hush(int i) { return new LevelSecond(i, -70f, -72f); }

        private static void FeedRange(RecordingWatch w, string track, int from, int to, Func<int, LevelSecond> second)
        {
            var list = new List<LevelSecond>();
            for (int i = from; i < to; i++) list.Add(second(i));
            w.Feed(track, list);
        }

        private static MicUseSnapshot Mic(params Action<FakeConsentStore>[] setup)
        {
            var store = new FakeConsentStore();
            foreach (Action<FakeConsentStore> s in setup) s(store);
            return new MeetingAppMicrophone(store).Read();
        }

        private static Action<FakeConsentStore> Teams(DateTimeOffset start, DateTimeOffset? stop)
        {
            return delegate(FakeConsentStore s) { s.Add(MeetingAppMicrophoneSelfCheck.TeamsPackaged, true, start, stop); };
        }

        private static Action<FakeConsentStore> Zoom(DateTimeOffset start, DateTimeOffset? stop)
        {
            return delegate(FakeConsentStore s) { s.Add(MeetingAppMicrophoneSelfCheck.ZoomDesktop, false, start, stop); };
        }

        private static MeetingContext Meeting(string name, DateTimeOffset start, DateTimeOffset? end)
        {
            return MeetingContext.Parse(MeetingJson(name, start, end));
        }

        private static string MeetingJson(string name, DateTimeOffset start, DateTimeOffset? end)
        {
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                name = name,
                startUtc = start.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                endUtc = end.HasValue ? end.Value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) : null,
                location = "",
                attendees = new[] { new { name = "Someone", status = "accepted" } },
            });
        }

        private static RecordingWatch NewWatch(MeetingContext anchor, bool system = true)
        {
            var w = new RecordingWatch(WatchT0, anchor ?? new MeetingContext());
            if (system) w.AddTrack("system output", true, true);
            w.AddTrack("microphone", false, true);
            return w;
        }

        private static bool Asks(WatchStep step, WatchRule rule) { return step != null && step.Ask != null && step.Ask.Rule == rule; }
        private static bool Quiet(WatchStep step) { return step != null && step.IsEmpty; }
        private static string AskedText(WatchStep step) { return step != null && step.Ask != null ? step.Ask.Question.Text : ""; }

        /// <summary>
        /// Every rule of the meeting-end watch, on its own, with an injected clock and per-second levels and no audio: the
        /// talking detector, R1 by app release and by silence (the silent lobby, 300 s against 900 s in a call, withdrawn
        /// when talking resumes, spent and re-armed), R2 with and without a calendar end, R3 and its extension, R4 with its
        /// five-minute lead and the same meeting republished, the priorities and pre-emption, Remind only and Off, and the
        /// trim point. Every question asked is held to the ABI's rules.
        /// </summary>
        private static void SelfCheckRecordingWatch(Action<string, bool> check)
        {
            var invalid = new List<string>();
            Func<WatchStep, WatchStep> valid = delegate(WatchStep s)
            {
                if (s != null && s.Ask != null)
                {
                    string why = QuestionsHost.Validate(s.Ask.Question, WatchT0);
                    if (why != null) invalid.Add(s.Ask.Question.Key + ": " + why);
                }
                return s;
            };
            MicUseSnapshot none = Mic();
            string stop = MeetingEndMode.Stop;
            try
            {
                // ---- what counts as talking ----
                {
                    var d = new ActivityDetector();
                    for (int i = 0; i < 60; i++) d.Feed(new LevelSecond(i, -40f, -41f));
                    check("watch: a steady sound is the noise floor, not talking", d.LastTalkingIndex == -1);
                    d.Feed(Talk(60)); d.Feed(Talk(61));
                    check("watch: two loud seconds are not yet talking", d.LastTalkingIndex == -1);
                    d.Feed(Talk(62));
                    check("watch: three loud seconds within ten are talking", d.LastTalkingIndex == 62);
                    var q = new ActivityDetector();
                    for (int i = 0; i < 30; i++) q.Feed(new LevelSecond(i, -100f, -110f));
                    q.Feed(new LevelSecond(30, -90f, -100f)); q.Feed(new LevelSecond(31, -90f, -100f)); q.Feed(new LevelSecond(32, -90f, -100f));
                    check("watch: in a near-silent room a second must still reach -55 dB to count", q.LastTalkingIndex == -1);
                }

                // ---- R1b: the silent lobby, then talk and silence ----
                {
                    RecordingWatch w = NewWatch(Meeting("Design review", WatchT0.AddMinutes(-2), WatchT0.AddMinutes(60)));
                    FeedRange(w, "microphone", 0, 600, Hush);
                    FeedRange(w, "system output", 0, 600, Hush);
                    bool lobby = true;
                    for (int m = 1; m <= 10; m++) lobby &= Quiet(valid(w.Tick(WatchT0.AddMinutes(m), stop, 3, none, null)));
                    check("watch: a silent lobby asks nothing, however long, before anyone talks or the meeting's end passes", lobby);

                    w = NewWatch(Meeting("Design review", WatchT0.AddMinutes(-2), WatchT0.AddMinutes(60)));
                    FeedRange(w, "microphone", 0, 120, Talk);
                    FeedRange(w, "microphone", 120, 600, Hush);
                    FeedRange(w, "system output", 0, 600, Hush);
                    // Talk ends at second 120; the ten-second window still counts it talking up to second 126.
                    check("watch: talk that stopped is not over yet at 4 min 50 s of silence",
                        Quiet(valid(w.Tick(WatchT0.AddSeconds(420), stop, 3, none, null))));
                    WatchStep ask = valid(w.Tick(WatchT0.AddSeconds(430), stop, 3, none, null));
                    check("watch: five minutes after the talk ends, with no meeting app on the microphone, R1 asks \"" + AskedText(ask) + "\"",
                        Asks(ask, WatchRule.MeetingOver) && ask.Ask.Question.Key == "remembrance.meeting-over"
                        && ask.Ask.Question.Text == "Your meeting looks over. Keep recording?"
                        && ask.Ask.Question.TimeoutChoiceId == "stop" && ask.Ask.Question.RunsWhileHeld
                        && ask.Ask.Question.Timeout == TimeSpan.FromMinutes(2)
                        && string.Join("|", ask.Ask.Question.Choices.Select(c => c.Id + "=" + c.Label)) == "keep=Keep recording|stop=Stop now");
                    check("watch: one question at a time: the next tick asks nothing more", Quiet(w.Tick(WatchT0.AddSeconds(432), stop, 3, none, null)));
                    WatchAction act = w.Answer(ask.Ask.Serial, TrayQuestionOutcome.TimedOut, "stop", WatchT0.AddSeconds(550));
                    check("watch: no answer stops the recording and keeps it to the last talk plus 30 s (157 s): " +
                          (act.KeepUntil.HasValue ? act.KeepUntil.Value.TotalSeconds.ToString(CultureInfo.InvariantCulture) : "none"),
                        act.Kind == WatchActionKind.Stop && act.KeepUntil == TimeSpan.FromSeconds(157) && act.Reason.Contains("nobody answered"));
                    check("watch: an answer is applied once", w.Answer(ask.Ask.Serial, TrayQuestionOutcome.TimedOut, "stop", WatchT0.AddSeconds(551)).Kind == WatchActionKind.None);
                }

                // ---- R1b: an explicit Stop now keeps everything; the window is 15 min while a meeting app holds the mic ----
                {
                    RecordingWatch w = NewWatch(null);
                    FeedRange(w, "microphone", 0, 120, Talk);
                    FeedRange(w, "microphone", 120, 1200, Hush);
                    MicUseSnapshot inCall = Mic(Teams(WatchT0.AddSeconds(-30), null));
                    check("watch: in a call (Teams holds the microphone) five minutes of silence asks nothing",
                        Quiet(w.Tick(WatchT0.AddSeconds(430), stop, 3, inCall, null)));
                    check("watch: ...nor fourteen", Quiet(w.Tick(WatchT0.AddSeconds(127 + 899), stop, 3, inCall, null)));
                    WatchStep ask = valid(w.Tick(WatchT0.AddSeconds(127 + 901), stop, 3, inCall, null));
                    check("watch: in a call it asks after fifteen minutes of silence", Asks(ask, WatchRule.MeetingOver));
                    WatchAction act = w.Answer(ask.Ask.Serial, TrayQuestionOutcome.Chosen, "stop", WatchT0.AddSeconds(1030));
                    check("watch: a Stop now the user presses stops and keeps EVERYTHING; the trim is for no answer only",
                        act.Kind == WatchActionKind.Stop && !act.KeepUntil.HasValue && act.Reason.Contains("you chose Stop now"));
                }

                // ---- R1b: withdrawn when talking resumes; spent by Keep recording, re-armed by talk ----
                {
                    RecordingWatch w = NewWatch(null);
                    FeedRange(w, "microphone", 0, 60, Talk);
                    FeedRange(w, "microphone", 60, 400, Hush);
                    WatchStep ask = valid(w.Tick(WatchT0.AddSeconds(400), stop, 3, none, null));
                    FeedRange(w, "microphone", 400, 404, Talk);
                    WatchStep back = w.Tick(WatchT0.AddSeconds(404), stop, 3, none, null);
                    check("watch: talking again withdraws \"looks over\", and asks nothing else",
                        Asks(ask, WatchRule.MeetingOver) && back.WithdrawSerial == ask.Ask.Serial && back.Ask == null);
                    check("watch: ...and a no-answer arriving for the withdrawn question does nothing",
                        w.Answer(ask.Ask.Serial, TrayQuestionOutcome.TimedOut, "stop", WatchT0.AddSeconds(405)).Kind == WatchActionKind.None);
                    FeedRange(w, "microphone", 404, 1100, Hush);
                    // The four talking seconds 400-403 keep the ten-second window "talking" up to second 410, so the
                    // silence runs from 411 s.
                    check("watch: ...and the silence restarts from the talk: not asked at 4 min 59 s",
                        Quiet(w.Tick(WatchT0.AddSeconds(710), stop, 3, none, null)));
                    WatchStep again = valid(w.Tick(WatchT0.AddSeconds(720), stop, 3, none, null));
                    check("watch: a withdrawn question is not spent: five more minutes of silence ask again", Asks(again, WatchRule.MeetingOver));
                    w.Answer(again.Ask.Serial, TrayQuestionOutcome.Chosen, "keep", WatchT0.AddSeconds(721));
                    check("watch: Keep recording spends it: more silence asks nothing",
                        Quiet(w.Tick(WatchT0.AddSeconds(1099), stop, 3, none, null)));
                    FeedRange(w, "microphone", 1100, 1105, Talk);
                    FeedRange(w, "microphone", 1105, 1500, Hush);
                    check("watch: ...until someone talks; then five minutes of silence ask again",
                        Asks(valid(w.Tick(WatchT0.AddSeconds(1112 + 300), stop, 3, none, null)), WatchRule.MeetingOver));
                }

                // ---- R1b: no talk at all, but the meeting's end passed ----
                {
                    RecordingWatch w = NewWatch(Meeting("Design review", WatchT0.AddMinutes(-2), WatchT0.AddMinutes(20)));
                    FeedRange(w, "microphone", 0, 1500, Hush);
                    check("watch: a silent recording asks nothing before the meeting's end",
                        Quiet(w.Tick(WatchT0.AddMinutes(19), stop, 3, none, null)));
                    WatchStep ask = valid(w.Tick(WatchT0.AddMinutes(20), stop, 3, none, null));
                    WatchAction act = ask.Ask != null ? w.Answer(ask.Ask.Serial, TrayQuestionOutcome.Expired, "stop", WatchT0.AddMinutes(23)) : WatchAction.Nothing;
                    check("watch: once the meeting's end has passed, silence since the start is \"looks over\", trimmed to the end plus 30 s",
                        Asks(ask, WatchRule.MeetingOver) && act.Kind == WatchActionKind.Stop && act.KeepUntil == TimeSpan.FromSeconds(20 * 60 + 30));
                    // A measured microphone that talked and fell silent beside a system track written in its device's
                    // format: the system track's silence cannot be known, so neither can "nobody is talking".
                    var unmeasured = new RecordingWatch(WatchT0, new MeetingContext());
                    unmeasured.AddTrack("microphone", false, true);
                    unmeasured.AddTrack("system output", true, false);
                    FeedRange(unmeasured, "microphone", 0, 60, Talk);
                    FeedRange(unmeasured, "microphone", 60, 1000, Hush);
                    check("watch: a track whose loudness is not measured makes silence unknowable: R1b never asks",
                        Quiet(unmeasured.Tick(WatchT0.AddSeconds(900), stop, 3, none, null)));
                }

                // ---- R1a: the meeting app let go of the microphone ----
                {
                    RecordingWatch w = NewWatch(null);
                    FeedRange(w, "system output", 0, 100, Hush);
                    MicUseSnapshot released = Mic(Teams(WatchT0.AddSeconds(-10), WatchT0.AddSeconds(70)));
                    check("watch: Teams let go ten seconds ago: not yet", Quiet(w.Tick(WatchT0.AddSeconds(80), stop, 3, released, null)));
                    WatchStep ask = valid(w.Tick(WatchT0.AddSeconds(86), stop, 3, released, null));
                    check("watch: Teams held the microphone for over a minute of the recording and let go 15 s ago: R1 asks",
                        Asks(ask, WatchRule.MeetingOver) && ask.Ask.Note == "Teams let go of the microphone");
                    WatchStep retaken = w.Tick(WatchT0.AddSeconds(90), stop, 3, Mic(Teams(WatchT0.AddSeconds(88), null)), null);
                    check("watch: Teams taking the microphone again withdraws it", retaken.WithdrawSerial == ask.Ask.Serial && retaken.Ask == null);
                    MicUseSnapshot second = Mic(Teams(WatchT0.AddSeconds(88), WatchT0.AddSeconds(200)));
                    WatchStep again = valid(w.Tick(WatchT0.AddSeconds(216), stop, 3, second, null));
                    WatchAction act = again.Ask != null ? w.Answer(again.Ask.Serial, TrayQuestionOutcome.TimedOut, "stop", WatchT0.AddSeconds(340)) : WatchAction.Nothing;
                    check("watch: ...its next release asks again, and no answer keeps up to the release plus 30 s (230 s)",
                        Asks(again, WatchRule.MeetingOver) && act.Kind == WatchActionKind.Stop && act.KeepUntil == TimeSpan.FromSeconds(230));

                    RecordingWatch kept = NewWatch(null);
                    WatchStep k = kept.Tick(WatchT0.AddSeconds(86), stop, 3, released, null);
                    kept.Answer(k.Ask.Serial, TrayQuestionOutcome.Chosen, "keep", WatchT0.AddSeconds(90));
                    check("watch: Keep recording spends that release: it is not asked about again",
                        Quiet(kept.Tick(WatchT0.AddSeconds(120), stop, 3, released, null)));

                    check("watch: a meeting app that held the microphone for under a minute of the recording asks nothing",
                        Quiet(NewWatch(null).Tick(WatchT0.AddSeconds(80), stop, 3, Mic(Teams(WatchT0.AddSeconds(-10), WatchT0.AddSeconds(50))), null)));
                    check("watch: Zoom on the microphone while Teams lets go asks nothing (the call moved)",
                        Quiet(NewWatch(null).Tick(WatchT0.AddSeconds(86), stop, 3,
                            Mic(Teams(WatchT0.AddSeconds(-10), WatchT0.AddSeconds(70)), Zoom(WatchT0.AddSeconds(75), null)), null)));
                    RecordingWatch loud = NewWatch(null);
                    FeedRange(loud, "system output", 0, 80, Talk);
                    check("watch: the system output still talking 15 s after the release asks nothing",
                        Quiet(loud.Tick(WatchT0.AddSeconds(86), stop, 3, released, null)));
                    check("watch: ...and asks once it has been quiet for 20 s",
                        Asks(valid(loud.Tick(WatchT0.AddSeconds(102), stop, 3, released, null)), WatchRule.MeetingOver));
                    check("watch: Zoom's release asks too, naming Zoom in the log note",
                        NewWatch(null).Tick(WatchT0.AddSeconds(86), stop, 3, Mic(Zoom(WatchT0.AddSeconds(-10), WatchT0.AddSeconds(70))), null)
                            .Ask.Note == "Zoom let go of the microphone");
                }

                // ---- Remind only, and Off ----
                {
                    RecordingWatch w = NewWatch(null);
                    MicUseSnapshot released = Mic(Teams(WatchT0.AddSeconds(-10), WatchT0.AddSeconds(70)));
                    WatchStep ask = valid(w.Tick(WatchT0.AddSeconds(86), MeetingEndMode.Remind, 3, released, null));
                    WatchAction act = ask.Ask != null ? w.Answer(ask.Ask.Serial, TrayQuestionOutcome.TimedOut, "keep", WatchT0.AddSeconds(210)) : WatchAction.Nothing;
                    check("watch: Remind only says \"Still recording.\", and no answer keeps recording",
                        ask.Ask != null && ask.Ask.Question.Text == "Your meeting looks over. Still recording."
                        && ask.Ask.Question.TimeoutChoiceId == "keep" && act.Kind == WatchActionKind.None);
                    check("watch: Remind only's question was spent by its no-answer",
                        Quiet(w.Tick(WatchT0.AddSeconds(220), MeetingEndMode.Remind, 3, released, null)));
                    RecordingWatch off = NewWatch(Meeting("Design review", WatchT0.AddMinutes(-2), WatchT0.AddMinutes(20)));
                    FeedRange(off, "microphone", 0, 60, Talk);
                    bool offQuiet = Quiet(off.Tick(WatchT0.AddMinutes(30), MeetingEndMode.Off, 3, released,
                        Meeting("Next one", WatchT0.AddMinutes(25), WatchT0.AddMinutes(50))));
                    WatchStep limit = valid(off.Tick(WatchT0.AddHours(3), MeetingEndMode.Off, 3, released, null));
                    check("watch: Off asks nothing about the meeting ending, its calendar end or the next one...", offQuiet);
                    check("watch: ...but the length limit applies in every mode, Off included", Asks(limit, WatchRule.Limit));
                }

                // ---- R3: the length limit ----
                {
                    RecordingWatch w = NewWatch(null);
                    check("watch: no limit question a second before three hours", Quiet(w.Tick(WatchT0.AddHours(3).AddSeconds(-1), stop, 3, none, null)));
                    WatchStep ask = valid(w.Tick(WatchT0.AddHours(3), stop, 3, none, null));
                    check("watch: at three hours R3 asks \"" + AskedText(ask) + "\", runs while held, and stops with no answer",
                        Asks(ask, WatchRule.Limit) && ask.Ask.Question.Key == "remembrance.limit"
                        && ask.Ask.Question.Text == "Recording has reached the 3-hour limit."
                        && ask.Ask.Question.TimeoutChoiceId == "stop" && ask.Ask.Question.RunsWhileHeld
                        && string.Join("|", ask.Ask.Question.Choices.Select(c => c.Id + "=" + c.Label)) == "extend=Keep 30 more min|stop=Stop now");
                    w.Answer(ask.Ask.Serial, TrayQuestionOutcome.Chosen, "extend", WatchT0.AddHours(3).AddMinutes(1));
                    check("watch: Keep 30 more min asks nothing for thirty minutes", Quiet(w.Tick(WatchT0.AddHours(3).AddMinutes(30), stop, 3, none, null)));
                    WatchStep again = valid(w.Tick(WatchT0.AddHours(3).AddMinutes(31), stop, 3, none, null));
                    WatchAction act = again.Ask != null ? w.Answer(again.Ask.Serial, TrayQuestionOutcome.Expired, "stop", WatchT0.AddHours(3).AddMinutes(33)) : WatchAction.Nothing;
                    check("watch: ...then asks again, and no answer stops it whole, never trimmed",
                        Asks(again, WatchRule.Limit) && act.Kind == WatchActionKind.Stop && !act.KeepUntil.HasValue);
                    check("watch: \"Longest recording\" at 1 hour asks at one hour",
                        Asks(NewWatch(null).Tick(WatchT0.AddHours(1), stop, 1, none, null), WatchRule.Limit));
                    RecordingWatch raised = NewWatch(null);
                    WatchStep r = raised.Tick(WatchT0.AddHours(1), stop, 1, none, null);
                    check("watch: raising the setting past the length withdraws the limit question",
                        r.Ask != null && raised.Tick(WatchT0.AddHours(1).AddMinutes(1), stop, 4, none, null).WithdrawSerial == r.Ask.Serial);
                }

                // ---- R2: the calendar end ----
                {
                    RecordingWatch w = NewWatch(Meeting("Design review", WatchT0.AddMinutes(-2), WatchT0.AddMinutes(30)));
                    Action<int> talkUntil = delegate(int s) { FeedRange(w, "microphone", 0, s, Talk); };
                    talkUntil(30 * 60);
                    check("watch: no calendar-end question before the end", Quiet(w.Tick(WatchT0.AddMinutes(29), stop, 3, none, null)));
                    WatchStep ask = valid(w.Tick(WatchT0.AddMinutes(30), stop, 3, none, null));
                    check("watch: once the calendar end passes, with talk going on, R2 asks \"" + AskedText(ask) + "\"",
                        Asks(ask, WatchRule.PastEnd) && ask.Ask.Question.Key == "remembrance.past-end"
                        && ask.Ask.Question.Text.StartsWith("The meeting was due to end at ", StringComparison.Ordinal)
                        && ask.Ask.Question.Text.EndsWith(". Still recording.", StringComparison.Ordinal)
                        && ask.Ask.Question.TimeoutChoiceId == "later" && !ask.Ask.Question.RunsWhileHeld
                        && string.Join("|", ask.Ask.Question.Choices.Select(c => c.Id)) == "keep|stop|later");
                    w.Answer(ask.Ask.Serial, TrayQuestionOutcome.TimedOut, "later", WatchT0.AddMinutes(35));
                    talkUntil(46 * 60);
                    check("watch: no answer asks again ten minutes later, not sooner",
                        Quiet(w.Tick(WatchT0.AddMinutes(44), stop, 3, none, null)) && Asks(valid(w.Tick(WatchT0.AddMinutes(45), stop, 3, none, null)), WatchRule.PastEnd));
                    RecordingWatch noEnd = NewWatch(Meeting("Open-ended", WatchT0.AddMinutes(-2), null));
                    FeedRange(noEnd, "microphone", 0, 7200, Talk);
                    check("watch: a meeting with no calendar end never gets R2: no end is invented",
                        Quiet(noEnd.Tick(WatchT0.AddMinutes(110), stop, 3, none, null)));
                    RecordingWatch kept = NewWatch(Meeting("Design review", WatchT0.AddMinutes(-2), WatchT0.AddMinutes(30)));
                    FeedRange(kept, "microphone", 0, 3600, Talk);
                    WatchStep k = kept.Tick(WatchT0.AddMinutes(30), stop, 3, none, null);
                    kept.Answer(k.Ask.Serial, TrayQuestionOutcome.Chosen, "keep", WatchT0.AddMinutes(31));
                    check("watch: Keep recording ends R2 for good", Quiet(kept.Tick(WatchT0.AddMinutes(55), stop, 3, none, null)));
                }

                // ---- R4: the next meeting ----
                {
                    MeetingContext a = Meeting("Design review", WatchT0.AddMinutes(-1), WatchT0.AddMinutes(60));
                    MeetingContext b = Meeting("Budget sync", WatchT0.AddMinutes(30), WatchT0.AddMinutes(60));
                    RecordingWatch w = NewWatch(a);
                    FeedRange(w, "microphone", 0, 3600, Talk);
                    check("watch: the same meeting republished (an attendee's answer changed) is not a new meeting",
                        Quiet(w.Tick(WatchT0.AddMinutes(10), stop, 3, none, MeetingContext.Parse(MeetingJson("Design review", WatchT0.AddMinutes(-1), WatchT0.AddMinutes(60)).Replace("accepted", "declined")))));
                    check("watch: the next meeting published in its five-minute lead asks nothing yet",
                        Quiet(w.Tick(WatchT0.AddMinutes(25), stop, 3, none, b)));
                    WatchStep ask = valid(w.Tick(WatchT0.AddMinutes(30), stop, 3, none, b));
                    check("watch: once it starts, R4 asks \"" + AskedText(ask) + "\"",
                        Asks(ask, WatchRule.NextMeeting) && ask.Ask.Question.Key == "remembrance.next-meeting"
                        && ask.Ask.Question.Text == "\"Budget sync\" has started." && ask.Ask.Question.TimeoutChoiceId == "one"
                        && !ask.Ask.Question.RunsWhileHeld
                        && string.Join("|", ask.Ask.Question.Choices.Select(c => c.Id + "=" + c.Label)) == "split=Start new recording|one=Keep as one|stop=Stop");
                    WatchAction split = w.Answer(ask.Ask.Serial, TrayQuestionOutcome.Chosen, "split", WatchT0.AddMinutes(31));
                    check("watch: Start new recording is a split naming the new meeting", split.Kind == WatchActionKind.Split
                        && split.Meeting != null && split.Meeting.Name == "Budget sync" && !split.KeepUntil.HasValue);

                    RecordingWatch one = NewWatch(a);
                    FeedRange(one, "microphone", 0, 7200, Talk);
                    WatchStep o = one.Tick(WatchT0.AddMinutes(30), stop, 3, none, b);
                    WatchAction kept = one.Answer(o.Ask.Serial, TrayQuestionOutcome.TimedOut, "one", WatchT0.AddMinutes(35));
                    check("watch: no answer keeps it as one recording, asks no more about that meeting, and waits for ITS calendar end",
                        kept.Kind == WatchActionKind.None && Quiet(one.Tick(WatchT0.AddMinutes(40), stop, 3, none, b))
                        && one.Anchor.Name == "Budget sync");

                    var late = new RecordingWatch(WatchT0, new MeetingContext());
                    late.AddTrack("microphone", false, true);
                    FeedRange(late, "microphone", 0, 600, Talk);
                    check("watch: a meeting already under way when the recording started, published late, is adopted and asks nothing",
                        Quiet(late.Tick(WatchT0.AddMinutes(2), stop, 3, none, Meeting("Design review", WatchT0.AddMinutes(-3), WatchT0.AddMinutes(30))))
                        && late.Anchor.Name == "Design review");
                    string longName = new string('x', 300);
                    WatchStep cut = valid(NewWatch(a).Tick(WatchT0.AddMinutes(30), stop, 3, none, Meeting(longName, WatchT0.AddMinutes(30), null)));
                    check("watch: a very long meeting name is cut with an ellipsis, inside the host's 240 characters",
                        cut.Ask != null && cut.Ask.Question.Text.Contains("\u2026") && cut.Ask.Question.Text.Length <= 240);
                }

                // ---- priorities: R3 > R1 > R4 > R2, and pre-emption ----
                {
                    MeetingContext a = Meeting("Design review", WatchT0.AddMinutes(-1), WatchT0.AddMinutes(30));
                    MeetingContext b = Meeting("Budget sync", WatchT0.AddMinutes(20), WatchT0.AddMinutes(60));
                    RecordingWatch w = NewWatch(a);
                    FeedRange(w, "microphone", 0, 1800, Talk);
                    WatchStep r2 = valid(w.Tick(WatchT0.AddMinutes(30), stop, 3, none, null));
                    WatchStep r4 = valid(w.Tick(WatchT0.AddMinutes(30).AddSeconds(2), stop, 3, none, b));
                    check("watch: the next meeting pre-empts the calendar-end question: R2 withdrawn, R4 asked in the same tick",
                        Asks(r2, WatchRule.PastEnd) && r4.WithdrawSerial == r2.Ask.Serial && Asks(r4, WatchRule.NextMeeting));
                    MicUseSnapshot released = Mic(Teams(WatchT0.AddMinutes(1), WatchT0.AddMinutes(30).AddSeconds(4)));
                    FeedRange(w, "system output", 0, 1800, Hush);
                    WatchStep r1 = valid(w.Tick(WatchT0.AddMinutes(30).AddSeconds(20), stop, 3, released, b));
                    check("watch: \"looks over\" pre-empts the next meeting", r1.WithdrawSerial == r4.Ask.Serial && Asks(r1, WatchRule.MeetingOver));
                    WatchStep r3 = valid(w.Tick(WatchT0.AddHours(3), stop, 3, released, b));
                    check("watch: the length limit pre-empts \"looks over\"", r3.WithdrawSerial == r1.Ask.Serial && Asks(r3, WatchRule.Limit));
                    check("watch: a button pressed on a question the watch had just withdrawn still counts (the click won the race)",
                        w.Answer(r4.Ask.Serial, TrayQuestionOutcome.Chosen, "stop", WatchT0.AddHours(3)).Kind == WatchActionKind.Stop);
                    w.Answer(r3.Ask.Serial, TrayQuestionOutcome.Chosen, "extend", WatchT0.AddHours(3).AddSeconds(5));
                    WatchStep back = valid(w.Tick(WatchT0.AddHours(3).AddSeconds(8), stop, 3, released, b));
                    check("watch: a pre-empted question is not spent: once the limit is answered, \"looks over\" comes back", Asks(back, WatchRule.MeetingOver));

                    RecordingWatch refused = NewWatch(null);
                    WatchStep f = refused.Tick(WatchT0.AddHours(3), stop, 3, none, null);
                    refused.Answer(f.Ask.Serial, TrayQuestionOutcome.Refused, null, WatchT0.AddHours(3));
                    check("watch: a question the host refused is held back a minute rather than put again every tick",
                        Quiet(refused.Tick(WatchT0.AddHours(3).AddSeconds(30), stop, 3, none, null))
                        && Asks(refused.Tick(WatchT0.AddHours(3).AddSeconds(61), stop, 3, none, null), WatchRule.Limit));
                }

                SelfCheckOfferWatch(check, valid, none);
                check("watch: every question asked keeps the ABI's rules (key, text, 2-4 choices, labels, timeout, deadline)" +
                      (invalid.Count > 0 ? ": " + string.Join("; ", invalid) : ""), invalid.Count == 0);
            }
            catch (Exception ex) { check("watch checks threw " + ex.GetType().Name + ": " + ex.Message + " " + ex.StackTrace, false); }
        }

        private static void SelfCheckOfferWatch(Action<string, bool> check, Func<WatchStep, WatchStep> valid, MicUseSnapshot none)
        {
            MeetingContext c = Meeting("Quarterly planning", WatchT0, WatchT0.AddMinutes(60));
            MicUseSnapshot teams = Mic(Teams(WatchT0.AddMinutes(1), null));
            DateTimeOffset at = WatchT0.AddMinutes(1).AddSeconds(25);
            check("offer: off by default means off: nothing is asked", Quiet(new OfferWatch().Tick(at, false, false, c, teams)));
            check("offer: nothing while a recording runs", Quiet(new OfferWatch().Tick(at, true, true, c, teams)));
            check("offer: nothing with no calendar meeting", Quiet(new OfferWatch().Tick(at, true, false, new MeetingContext(), teams)));
            check("offer: nothing until Teams has held the microphone for 20 s",
                Quiet(new OfferWatch().Tick(WatchT0.AddMinutes(1).AddSeconds(10), true, false, c, teams)));
            check("offer: nothing for a call that took the microphone more than five minutes before the meeting",
                Quiet(new OfferWatch().Tick(at, true, false, c, Mic(Teams(WatchT0.AddMinutes(-20), null)))));
            var o = new OfferWatch();
            WatchStep ask = valid(o.Tick(at, true, false, c, teams));
            check("offer: Teams on the microphone for 25 s of a current meeting asks \"" + AskedText(ask) + "\", until the meeting's end",
                Asks(ask, WatchRule.Offer) && ask.Ask.Question.Key == "remembrance.offer-record"
                && ask.Ask.Question.Text == "Teams is using the microphone for \"Quarterly planning\". Record it?"
                && ask.Ask.Question.TimeoutChoiceId == "skip" && ask.Ask.Question.Deadline == WatchT0.AddMinutes(60)
                && string.Join("|", ask.Ask.Question.Choices.Select(x => x.Id + "=" + x.Label)) == "record=Record|skip=Not this one");
            MeetingContext recorded = o.Answer(ask.Ask.Serial, TrayQuestionOutcome.Chosen, "record", at.AddSeconds(5));
            check("offer: Record names the meeting to record", recorded != null && recorded.Name == "Quarterly planning");
            check("offer: once per meeting: it is not asked again", Quiet(o.Tick(at.AddMinutes(5), true, false, c, teams)));

            var skip = new OfferWatch();
            WatchStep s = skip.Tick(at, true, false, c, teams);
            check("offer: no answer records nothing and spends it",
                skip.Answer(s.Ask.Serial, TrayQuestionOutcome.Expired, "skip", at.AddMinutes(5)) == null
                && Quiet(skip.Tick(at.AddMinutes(6), true, false, c, teams)));

            var left = new OfferWatch();
            WatchStep l = left.Tick(at, true, false, c, teams);
            WatchStep gone = left.Tick(at.AddSeconds(10), true, false, c, Mic(Teams(WatchT0.AddMinutes(1), at.AddSeconds(8))));
            check("offer: Teams letting go of the microphone withdraws it", gone.WithdrawSerial == l.Ask.Serial);
            check("offer: ...and rejoining asks again once Teams has held it 20 s (a withdrawn offer is not spent)",
                Quiet(left.Tick(at.AddSeconds(30), true, false, c, Mic(Teams(at.AddSeconds(20), null))))
                && Asks(left.Tick(at.AddSeconds(41), true, false, c, Mic(Teams(at.AddSeconds(20), null))), WatchRule.Offer));
            var manual = new OfferWatch();
            WatchStep m = manual.Tick(at, true, false, c, teams);
            manual.NoteRecordingStarted();
            WatchStep mw = manual.Tick(at.AddSeconds(2), true, true, c, teams);
            check("offer: a recording started by hand withdraws it, spent", mw.WithdrawSerial == m.Ask.Serial
                && Quiet(manual.Tick(at.AddMinutes(30), true, false, c, teams)));
            WatchStep noEnd = valid(new OfferWatch().Tick(at, true, false, Meeting("Quarterly planning", WatchT0, null), Mic(Zoom(WatchT0.AddMinutes(1), null))));
            check("offer: a meeting with no calendar end is offered until its start plus an hour, and Zoom is named",
                noEnd.Ask != null && noEnd.Ask.Question.Deadline == WatchT0.AddMinutes(60)
                && noEnd.Ask.Question.Text.StartsWith("Zoom is using the microphone", StringComparison.Ordinal));
            check("offer: an unreadable microphone record offers nothing",
                Quiet(new OfferWatch().Tick(at, true, false, c, new MeetingAppMicrophone(new FakeConsentStore { Entries = null }).Read())));
        }

        /// <summary>MeetingContext's times (2.4.0): round-trip parsed, null-tolerant, a meeting's identity, emptiness.</summary>
        private static void SelfCheckMeetingTimes(Action<string, bool> check)
        {
            MeetingContext m = MeetingContext.Parse(MeetingJson("Design review", WatchT0, WatchT0.AddMinutes(45)));
            check("meeting: startUtc and endUtc are read from Reminder's round-trip times",
                m.StartUtc == WatchT0 && m.EndUtc == WatchT0.AddMinutes(45) && !m.IsEmpty);
            MeetingContext noEnd = MeetingContext.Parse(MeetingJson("Design review", WatchT0, null));
            check("meeting: an endUtc of JSON null is no end", noEnd.StartUtc == WatchT0 && !noEnd.EndUtc.HasValue);
            MeetingContext odd = MeetingContext.Parse("{\"name\":\"X\",\"startUtc\":\"not a time\",\"endUtc\":12}");
            check("meeting: a time that does not parse, or is not a string, is null", !odd.StartUtc.HasValue && !odd.EndUtc.HasValue && odd.Name == "X");
            MeetingContext backwards = MeetingContext.Parse(MeetingJson("X", WatchT0, WatchT0.AddMinutes(-5)));
            check("meeting: an end at or before the start is no end", !backwards.EndUtc.HasValue);
            check("meeting: the identity is name and start, so a republish with other attendees is the same meeting",
                m.MeetingKey == MeetingContext.Parse(MeetingJson("Design review", WatchT0, WatchT0.AddMinutes(50))).MeetingKey
                && m.MeetingKey != MeetingContext.Parse(MeetingJson("Design review", WatchT0.AddMinutes(60), null)).MeetingKey);
            check("meeting: an unpublished channel is empty, with no identity",
                MeetingContext.Parse("").IsEmpty && MeetingContext.Parse("").MeetingKey == "");
        }

        /// <summary>The OneDrive warning's pure helper, with a stand-in environment (2.4.0, the owner's decision of 2026-10-15).</summary>
        private static void SelfCheckOneDrive(Action<string, bool> check)
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["OneDrive"] = @"C:\Users\someone\OneDrive - Contoso",
                ["OneDriveCommercial"] = @"C:\Users\someone\OneDrive - Contoso\",
                ["OneDriveConsumer"] = @"D:\Personal\OneDrive",
            };
            Func<string, string> lookup = delegate(string k) { string v; return env.TryGetValue(k, out v) ? v : null; };
            check("onedrive: Documents redirected into OneDrive is inside it",
                CaptureStore.OneDriveRootOf(@"C:\Users\someone\OneDrive - Contoso\Documents\Remembrance", lookup) == @"C:\Users\someone\OneDrive - Contoso");
            check("onedrive: the personal OneDrive counts too, matched case-insensitively and with forward slashes",
                CaptureStore.OneDriveRootOf("d:/personal/onedrive/Meetings", lookup) != null);
            check("onedrive: a sibling folder whose name merely starts the same is not inside it (folder-boundary match)",
                CaptureStore.OneDriveRootOf(@"C:\Users\someone\OneDrive - Contoso Archive\Remembrance", lookup) == null);
            check("onedrive: a folder outside every OneDrive is not, and nor is a relative path or a blank one",
                CaptureStore.OneDriveRootOf(@"C:\Users\someone\Documents\Remembrance", lookup) == null
                && CaptureStore.OneDriveRootOf("Remembrance", lookup) == null && CaptureStore.OneDriveRootOf("", lookup) == null);
            check("onedrive: no OneDrive variables at all is never inside one",
                CaptureStore.OneDriveRootOf(@"C:\Users\someone\OneDrive - Contoso\Documents", delegate { return null; }) == null);
        }

        // ---- the module, end to end ----

        /// <summary>
        /// The watch inside the module, through its own start, stop and tick paths, on fake devices, a fake microphone
        /// record, a stood-in clock and the questions double: Teams lets go and nobody answers, so the recording stops
        /// and is trimmed; an explicit Stop now keeps it whole; R3 at three hours with its extension; R4's split into two
        /// captures; a manual stop withdrawing the open question; a stale answer ignored; the backstop; the offer
        /// starting a recording; a track that stops saving logged once and shown; the pane's new fields; the Status line's
        /// OneDrive warning; and the floor and the permission this all needs.
        /// </summary>
        private static void SelfCheckMeetingEndInModule(Action<string, bool> check)
        {
            string scratch = Path.Combine(Path.GetTempPath(), "dp-remembrance-meetingend-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Func<DateTimeOffset> savedClock = WatchClock;
            Func<string, string> savedEnv = EnvironmentLookup;
            Func<string, WaveFormat, WaveFileWriter> savedFactory = AudioRecorder.ScratchWriterFactory;
            SynchronizationContext previous = SynchronizationContext.Current;
            var ui = new RecorderSelfCheck.QueueSynchronizationContext();
            DateTimeOffset now = WatchT0;
            WatchClock = delegate { return now; };
            var transcripts = new List<string>();
            Func<Func<bool>, bool> pumpUntil = delegate(Func<bool> condition)
            {
                return SpinWait.SpinUntil(delegate { ui.Drain(); return condition(); }, TimeSpan.FromSeconds(20));
            };
            var allInvalid = new List<string>();
            Func<FakeConsentStore, QuestionsHost, string, RemembranceModule> start = delegate(FakeConsentStore store, QuestionsHost host, string root)
            {
                host.Clock = delegate { return now; };
                DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings s = host.SettingsFor(Id);
                s.Set("storageLocation", root);
                s.Set("summaryModelsCache", "alpha:1b");
                s.Set("whisperExe", @"c:\seeded\whisper-cli.exe");
                var module = new RemembranceModule();
                module.MicForDiagnostics = new MeetingAppMicrophone(store);
                module.Init(host);
                return module;
            };
            Action<RemembranceModule, QuestionsHost> finish = delegate(RemembranceModule module, QuestionsHost host)
            {
                try { if (module.IsRecordingForSelfTest) module.StopRecordingForSelfTest(); } catch { }
                pumpUntil(delegate { return module.PendingSavesForSelfTest.All(t => t.IsCompleted); });
                CapturePaths last = module.LastCaptureForSelfTest;
                if (last != null) transcripts.Add(last.Transcript);
                try { module.Shutdown(); } catch { }
                allInvalid.AddRange(host.InvalidAsks);
            };
            Func<RemembranceModule, bool> saved = delegate(RemembranceModule module)
            {
                return pumpUntil(delegate { return !module.IsRecordingForSelfTest && module.PendingSavesForSelfTest.All(t => t.IsCompleted); });
            };
            try
            {
                SynchronizationContext.SetSynchronizationContext(ui);
                Directory.CreateDirectory(scratch);
                byte[] silence100ms = new byte[3200];

                // ---- Teams lets go and nobody answers: stopped, saved, trimmed to the release plus 30 s ----
                using (var devices = new RecorderSelfCheck.FakeDevices())
                {
                    // The microphone at 44.1 kHz, so this track goes through the resampler as it arrives and the trim
                    // cuts a converted track; the system output at 16 kHz goes through untouched.
                    devices.MicFormat = new WaveFormat(44100, 16, 1);
                    byte[] mic100ms = new byte[4410 * 2];
                    devices.SystemPacket = delegate { return silence100ms; };
                    devices.MicPacket = delegate { return mic100ms; };
                    devices.BurstPackets = 1200;   // 120 s of each track, at once
                    var store = new FakeConsentStore();
                    store.Add(MeetingAppMicrophoneSelfCheck.TeamsPackaged, true, WatchT0.AddSeconds(-10), null);
                    var host = new QuestionsHost();
                    host.PublishContext("reminder", MeetingContext.Key, MeetingJson("Design review", WatchT0.AddMinutes(-2), WatchT0.AddMinutes(60)));
                    now = WatchT0;
                    RemembranceModule module = start(store, host, Path.Combine(scratch, "release"));
                    module.StartRecordingForSelfTest();
                    ui.Drain();
                    CapturePaths paths = module.LastCaptureForSelfTest;
                    bool burst = devices.Captures.Count == 2 && devices.Captures.All(c => c.BurstDone.Wait(TimeSpan.FromSeconds(30)));
                    check("WITNESS meeting end in the module: the recording runs at 2 s ticks while recording",
                        module.IsRecordingForSelfTest && burst && module.WatchIntervalForSelfTest == 2000);
                    now = WatchT0.AddSeconds(30);
                    module.WatchTickForSelfTest();
                    check("meeting end in the module: nothing is asked while Teams holds the microphone", host.Asks.Count == 0);
                    store.Entries.Clear();
                    store.Add(MeetingAppMicrophoneSelfCheck.TeamsPackaged, true, WatchT0.AddSeconds(-10), WatchT0.AddSeconds(70));
                    now = WatchT0.AddSeconds(90);
                    module.WatchTickForSelfTest();
                    QuestionsHost.Asked asked = host.Pending(WatchQuestions.MeetingOverKey);
                    check("meeting end in the module: 20 s after Teams let go, the module asks \"Your meeting looks over\" through the host, as remembrance",
                        asked != null && asked.ModuleId == Id && asked.Question.RunsWhileHeld && module.OpenQuestionForSelfTest == WatchRule.MeetingOver);
                    host.TimeOut(WatchQuestions.MeetingOverKey);
                    bool stopped = saved(module);
                    WaveFormat f = null;
                    long samples = stopped && paths != null && File.Exists(paths.Audio) ? SamplesIn(paths.Audio, out f) : -1;
                    check("meeting end in the module: nobody answered, so the recording stopped and kept 100 s of its 120 (the release plus 30 s): " +
                          samples.ToString(CultureInfo.InvariantCulture) + " samples",
                        stopped && samples == 1600000);
                    check("meeting end in the module: the log says why it stopped and what the trim kept and cut, naming no meeting",
                        host.LoggedLines.Any(l => l.Contains("stopping: the meeting looked over (Teams let go of the microphone) and nobody answered"))
                        && host.LoggedLines.Any(l => l.Contains("kept the first 1 min 40 s; trimmed 20 s after the meeting ended (no answer)"))
                        && host.LoggedLines.Any(l => l.Contains("meeting watch: asked remembrance.meeting-over (Teams let go of the microphone)"))
                        && !host.LoggedLines.Any(l => l.Contains("meeting watch") && l.Contains("Design review")));
                    finish(module, host);
                }

                // ---- the same release answered Stop now: everything is kept ----
                using (var devices = new RecorderSelfCheck.FakeDevices())
                {
                    devices.SystemPacket = delegate { return silence100ms; };
                    devices.MicPacket = delegate { return silence100ms; };
                    devices.BurstPackets = 1200;
                    var store = new FakeConsentStore();
                    store.Add(MeetingAppMicrophoneSelfCheck.TeamsPackaged, true, WatchT0.AddSeconds(-10), WatchT0.AddSeconds(70));
                    var host = new QuestionsHost();
                    now = WatchT0;
                    RemembranceModule module = start(store, host, Path.Combine(scratch, "stopnow"));
                    module.StartRecordingForSelfTest();
                    ui.Drain();
                    CapturePaths paths = module.LastCaptureForSelfTest;
                    devices.Captures.ForEach(c => c.BurstDone.Wait(TimeSpan.FromSeconds(30)));
                    now = WatchT0.AddSeconds(90);
                    module.WatchTickForSelfTest();
                    host.Answer(WatchQuestions.MeetingOverKey, "stop");
                    bool stopped = saved(module);
                    WaveFormat f;
                    long samples = stopped && File.Exists(paths.Audio) ? SamplesIn(paths.Audio, out f) : -1;
                    check("meeting end in the module: Stop now pressed by the user keeps all 120 s; the trim is for no answer only (" +
                          samples.ToString(CultureInfo.InvariantCulture) + " samples)", samples == 1920000
                        && !host.LoggedLines.Any(l => l.Contains("trimmed")));
                    finish(module, host);
                }

                // ---- R3 at three hours, its extension, then no answer; a manual stop withdraws; the backstop ----
                using (var devices = new RecorderSelfCheck.FakeDevices())
                {
                    var store = new FakeConsentStore();
                    var host = new QuestionsHost();
                    now = WatchT0;
                    RemembranceModule module = start(store, host, Path.Combine(scratch, "limit"));
                    module.StartRecordingForSelfTest();
                    ui.Drain();
                    Thread.Sleep(60);
                    now = WatchT0.AddHours(3);
                    module.WatchTickForSelfTest();
                    QuestionsHost.Asked first = host.Pending(WatchQuestions.LimitKey);
                    check("meeting end in the module: at three hours (the default Longest recording) the limit question is asked",
                        first != null && first.Question.Text == "Recording has reached the 3-hour limit.");
                    host.Answer(WatchQuestions.LimitKey, "extend");
                    pumpUntil(delegate { return module.OpenQuestionForSelfTest == WatchRule.None; });
                    now = WatchT0.AddHours(3).AddMinutes(31);
                    module.WatchTickForSelfTest();
                    bool askedAgain = host.Pending(WatchQuestions.LimitKey) != null && host.Pending(WatchQuestions.LimitKey) != first;
                    host.TimeOut(WatchQuestions.LimitKey);
                    bool stopped = saved(module);
                    check("meeting end in the module: Keep 30 more min keeps it going; thirty minutes on it asks again, and no answer stops and saves it",
                        askedAgain && stopped && host.LoggedLines.Any(l => l.Contains("stopping: the 3-hour limit was reached and nobody answered")));
                    finish(module, host);

                    host = new QuestionsHost();
                    now = WatchT0;
                    module = start(store, host, Path.Combine(scratch, "manual"));
                    module.StartRecordingForSelfTest();
                    ui.Drain();
                    now = WatchT0.AddHours(3);
                    module.WatchTickForSelfTest();
                    QuestionsHost.Asked open = host.Pending(WatchQuestions.LimitKey);
                    module.StopRecordingForSelfTest();
                    ui.Drain();
                    check("meeting end in the module: a manual stop withdraws the question that was showing", open != null && open.Withdrawn);
                    finish(module, host);

                    host = new QuestionsHost();
                    now = WatchT0;
                    module = start(store, host, Path.Combine(scratch, "backstop"));
                    module.StartRecordingForSelfTest();
                    ui.Drain();
                    now = WatchT0.AddHours(3);
                    module.WatchTickForSelfTest();
                    QuestionsHost.Asked silentHost = host.Pending(WatchQuestions.LimitKey);
                    now = WatchT0.AddHours(3).AddMinutes(2).AddSeconds(59);
                    module.WatchTickForSelfTest();
                    bool notYet = module.IsRecordingForSelfTest;
                    now = WatchT0.AddHours(3).AddMinutes(3).AddSeconds(1);
                    module.WatchTickForSelfTest();
                    bool backstopped = saved(module);
                    check("meeting end in the module: a host that never answers the limit question is backstopped a minute past its timeout: withdrawn, and stopped",
                        silentHost != null && notYet && backstopped && silentHost.Withdrawn
                        && host.LoggedLines.Any(l => l.Contains("no answer to remembrance.limit came back from the host")));
                    finish(module, host);
                }

                // ---- a stale answer for an earlier recording is ignored ----
                using (var devices = new RecorderSelfCheck.FakeDevices())
                {
                    var store = new FakeConsentStore();
                    var host = new QuestionsHost { HonourWithdraw = false };   // the click wins the race with the withdraw
                    now = WatchT0;
                    RemembranceModule module = start(store, host, Path.Combine(scratch, "stale"));
                    module.StartRecordingForSelfTest();
                    ui.Drain();
                    now = WatchT0.AddHours(3);
                    module.WatchTickForSelfTest();
                    QuestionsHost.Asked old = host.Pending(WatchQuestions.LimitKey);
                    module.StopRecordingForSelfTest();
                    ui.Drain();
                    Thread.Sleep(1100);   // a distinct second, so the next capture gets its own folder
                    module.StartRecordingForSelfTest();
                    ui.Drain();
                    // The new recording has its own limit question open, under the same serial its watch numbers from, so
                    // only the generation tells the two answers apart.
                    now = WatchT0.AddHours(6);
                    module.WatchTickForSelfTest();
                    QuestionsHost.Asked current = host.Pending(WatchQuestions.LimitKey);
                    host.Answer(old, "stop");
                    pumpUntil(delegate { return host.LoggedLines.Any(l => l.Contains("came back after its recording ended; ignored")); });
                    ui.Drain();
                    check("meeting end in the module: Stop now arriving for the PREVIOUS recording's question is ignored; this one keeps recording, its own question still open",
                        old != null && current != null && current != old && module.IsRecordingForSelfTest
                        && module.OpenQuestionForSelfTest == WatchRule.Limit
                        && host.LoggedLines.Any(l => l.Contains("an answer to remembrance.limit came back after its recording ended; ignored")));
                    finish(module, host);
                }

                // ---- R4: the next meeting, split into two captures ----
                using (var devices = new RecorderSelfCheck.FakeDevices())
                {
                    var store = new FakeConsentStore();
                    var host = new QuestionsHost();
                    host.PublishContext("reminder", MeetingContext.Key, MeetingJson("Design review", WatchT0.AddMinutes(-1), WatchT0.AddMinutes(60)));
                    now = WatchT0;
                    RemembranceModule module = start(store, host, Path.Combine(scratch, "split"));
                    module.StartRecordingForSelfTest();
                    ui.Drain();
                    CapturePaths firstCapture = module.LastCaptureForSelfTest;
                    Thread.Sleep(80);
                    host.PublishContext("reminder", MeetingContext.Key, MeetingJson("Budget sync", WatchT0.AddMinutes(30), WatchT0.AddMinutes(60)));
                    now = WatchT0.AddMinutes(25);
                    module.WatchTickForSelfTest();
                    bool leadQuiet = host.Asks.Count == 0;
                    now = WatchT0.AddMinutes(30);
                    module.WatchTickForSelfTest();
                    QuestionsHost.Asked r4 = host.Pending(WatchQuestions.NextMeetingKey);
                    host.Answer(WatchQuestions.NextMeetingKey, "split");
                    pumpUntil(delegate { return module.LastCaptureForSelfTest != firstCapture && module.IsRecordingForSelfTest; });
                    CapturePaths second = module.LastCaptureForSelfTest;
                    pumpUntil(delegate { return File.Exists(firstCapture.Audio); });
                    check("meeting end in the module: nothing in the next meeting's five-minute lead; at its start \"" +
                          (r4 != null ? r4.Question.Text : "nothing") + "\" is asked",
                        leadQuiet && r4 != null && r4.Question.Text == "\"Budget sync\" has started.");
                    check("meeting end in the module: Start new recording saves the first capture and starts a second named for the new meeting: " +
                          (second != null ? second.BaseName : "none"),
                        second != null && second != firstCapture && second.BaseName.EndsWith(" Budget sync", StringComparison.Ordinal)
                        && firstCapture.BaseName.EndsWith(" Design review", StringComparison.Ordinal) && File.Exists(firstCapture.Audio)
                        && module.IsRecordingForSelfTest);
                    transcripts.Add(firstCapture.Transcript);
                    finish(module, host);
                }

                // ---- the offer: Teams takes the microphone in a calendar meeting, Record starts a recording ----
                using (var devices = new RecorderSelfCheck.FakeDevices())
                {
                    var store = new FakeConsentStore();
                    var host = new QuestionsHost();
                    host.PublishContext("reminder", MeetingContext.Key, MeetingJson("Quarterly planning", WatchT0, WatchT0.AddMinutes(60)));
                    now = WatchT0.AddMinutes(1);
                    RemembranceModule module = start(store, host, Path.Combine(scratch, "offer"));
                    store.Add(MeetingAppMicrophoneSelfCheck.TeamsPackaged, true, WatchT0.AddMinutes(1), null);
                    now = WatchT0.AddMinutes(1).AddSeconds(25);
                    module.WatchTickForSelfTest();
                    check("meeting end in the module: with \"Offer to record meetings\" off (the default) nothing is offered and the microphone record is never read",
                        host.Asks.Count == 0 && store.Reads == 0 && module.WatchIntervalForSelfTest == 5000);
                    host.SettingsFor(Id).Set(OfferToRecordKey, "true");
                    module.WatchTickForSelfTest();
                    QuestionsHost.Asked offer = host.Pending(WatchQuestions.OfferKey);
                    host.Answer(WatchQuestions.OfferKey, "record");
                    bool recording = pumpUntil(delegate { return module.IsRecordingForSelfTest; });
                    check("meeting end in the module: turned on, it offers once Teams has held the microphone 20 s, and Record starts a recording named for the meeting",
                        offer != null && recording && module.LastCaptureForSelfTest != null
                        && module.LastCaptureForSelfTest.BaseName.EndsWith(" Quarterly planning", StringComparison.Ordinal));
                    finish(module, host);
                }

                // ---- Join & record (2.5.0): Reminder's request starts a recording on the next idle tick ----
                using (var devices = new RecorderSelfCheck.FakeDevices())
                {
                    var host = new QuestionsHost();
                    now = WatchT0;
                    RemembranceModule module = start(new FakeConsentStore(), host, Path.Combine(scratch, "joinrecord"));
                    check("join & record: at Init Remembrance says it accepts a request and is not recording",
                        RecordingHandshake.CanRecordNow(host.ReadContext(RecordingHandshake.ReadyKey)));
                    host.PublishContext("reminder", RecordingHandshake.RequestKey,
                        RecordingHandshake.RequestJson("req-1", WatchT0, MeetingJson("Design review", WatchT0, WatchT0.AddMinutes(30))));
                    now = WatchT0.AddSeconds(4);
                    module.WatchTickForSelfTest();
                    bool recording = pumpUntil(delegate { return module.IsRecordingForSelfTest; });
                    check("join & record: the next idle tick starts a recording named for the request's meeting",
                        recording && module.LastCaptureForSelfTest != null
                        && module.LastCaptureForSelfTest.BaseName.EndsWith(" Design review", StringComparison.Ordinal));
                    string ready = host.ReadContext(RecordingHandshake.ReadyKey);
                    check("join & record: ...logs it without naming the meeting, and tells Reminder it is recording",
                        host.LoggedLines.Any(l => l.Contains("join & record: Reminder's Join & record asked for a recording; starting one"))
                        && !host.LoggedLines.Any(l => l.Contains("join & record") && l.Contains("Design review"))
                        && !RecordingHandshake.CanRecordNow(ready) && ready.Contains("\"recording\":true"));
                    module.StopRecordingForSelfTest();
                    saved(module);
                    now = WatchT0.AddSeconds(20);
                    module.WatchTickForSelfTest();
                    check("join & record: after the stop it is ready again, and the SAME request starts no second recording",
                        RecordingHandshake.CanRecordNow(host.ReadContext(RecordingHandshake.ReadyKey)) && !module.IsRecordingForSelfTest);
                    host.PublishContext("reminder", RecordingHandshake.RequestKey,
                        RecordingHandshake.RequestJson("req-2", now.AddSeconds(-90), MeetingJson("Design review", WatchT0, WatchT0.AddMinutes(30))));
                    module.WatchTickForSelfTest();
                    check("join & record: a request older than a minute starts nothing, and the log says how old",
                        !module.IsRecordingForSelfTest && host.LoggedLines.Any(l => l.Contains("was ignored because it was 90 s old")));
                    host.SettingsFor(Id).Set(AcceptJoinRecordKey, "false");
                    module.PublishRecorderReadyForSelfTest();
                    check("join & record: with the setting off Remembrance says it does not accept, so Reminder shows no button",
                        !RecordingHandshake.CanRecordNow(host.ReadContext(RecordingHandshake.ReadyKey)));
                    host.PublishContext("reminder", RecordingHandshake.RequestKey,
                        RecordingHandshake.RequestJson("req-3", now, MeetingJson("Design review", WatchT0, WatchT0.AddMinutes(30))));
                    module.WatchTickForSelfTest();
                    check("join & record: ...and a request that comes anyway starts nothing, and the log says why",
                        !module.IsRecordingForSelfTest
                        && host.LoggedLines.Any(l => l.Contains("was ignored because \"" + AcceptJoinRecordLabel + "\" is off")));
                    finish(module, host);
                    check("join & record: at Shutdown the ready key is cleared, so Reminder stops offering the button",
                        host.ReadContext(RecordingHandshake.ReadyKey) == "");
                }
                using (var devices = new RecorderSelfCheck.FakeDevices())
                {
                    // A request already in the channel when an instance starts belongs to an earlier one (a reload, an update).
                    var host = new QuestionsHost();
                    host.PublishContext("reminder", RecordingHandshake.RequestKey,
                        RecordingHandshake.RequestJson("old-1", WatchT0, MeetingJson("Design review", WatchT0, WatchT0.AddMinutes(30))));
                    now = WatchT0.AddSeconds(2);
                    RemembranceModule module = start(new FakeConsentStore(), host, Path.Combine(scratch, "joinrecord-old"));
                    module.WatchTickForSelfTest();
                    check("join & record: a fresh request already there when the module starts (a reload) starts nothing",
                        !module.IsRecordingForSelfTest);
                    finish(module, host);
                }

                // ---- a track that stops saving: logged once, shown on the Status line ----
                using (var devices = new RecorderSelfCheck.FakeDevices())
                {
                    AudioRecorder.ScratchWriterFactory = delegate(string p, WaveFormat fmt)
                    {
                        if (!p.EndsWith(AudioRecorder.MicScratchSuffix, StringComparison.OrdinalIgnoreCase)) return savedFactory(p, fmt);
                        return new WaveFileWriter(new FailingStream(p, 8000), fmt, new WaveFileWriterOptions { EnableRf64 = true });
                    };
                    var store = new FakeConsentStore();
                    var host = new QuestionsHost();
                    now = WatchT0;
                    RemembranceModule module = start(store, host, Path.Combine(scratch, "failing"));
                    module.StartRecordingForSelfTest();
                    ui.Drain();
                    Thread.Sleep(500);
                    now = WatchT0.AddSeconds(2);
                    module.WatchTickForSelfTest();
                    now = WatchT0.AddSeconds(4);
                    module.WatchTickForSelfTest();
                    int lines = host.LoggedLines.Count(l => l.Contains("the microphone track stopped saving"));
                    string status = module.StatusLineForSelfTest();
                    check("meeting end in the module: a track that stops saving is logged ONCE, by its name and exception type (" +
                          lines.ToString(CultureInfo.InvariantCulture) + " line(s))",
                        lines == 1 && host.LoggedLines.Any(l => l.Contains("the microphone track stopped saving") && l.Contains("(IOException)")));
                    check("meeting end in the module: ...and the Status line warns of it",
                        status.Contains("⚠ The microphone track stopped saving"));
                    finish(module, host);
                    AudioRecorder.ScratchWriterFactory = savedFactory;
                }

                // ---- the Status line's OneDrive warning ----
                {
                    var host = new QuestionsHost();
                    RemembranceModule module = start(new FakeConsentStore(), host, @"C:\Users\someone\OneDrive - Contoso\Documents\Remembrance");
                    EnvironmentLookup = delegate(string k) { return k == "OneDriveCommercial" ? @"C:\Users\someone\OneDrive - Contoso" : null; };
                    string inside = module.StatusLineForSelfTest();
                    EnvironmentLookup = delegate { return null; };
                    string outside = module.StatusLineForSelfTest();
                    EnvironmentLookup = savedEnv;
                    check("meeting end in the module: a storage folder inside OneDrive puts a warning on the Status line that recordings upload and deleted audio stays in its recycle bin",
                        inside.Contains("⚠ The storage folder is inside OneDrive") && inside.Contains("recycle bin")
                        && !outside.Contains("⚠ The storage folder is inside OneDrive"));
                    try { module.Shutdown(); } catch { }
                }

                // ---- the Meetings card ----
                {
                    var host = new QuestionsHost();
                    RemembranceModule module = start(new FakeConsentStore(), host, Path.Combine(scratch, "pane"));
                    OptionsPane pane = host.OptionsPanes[0];
                    IReadOnlyDictionary<string, string> shown = pane.Load();
                    SettingField mode = FieldFor(pane, MeetingEndMode.SettingKey);
                    SettingField hours = FieldFor(pane, LongestRecordingKey);
                    SettingField offer = FieldFor(pane, OfferToRecordKey);
                    SettingField apps = FieldFor(pane, "meetingApps");
                    int hotkeysAt = IndexOfField(pane, "snapshotHotkey");
                    check("meetings card: a Meetings card after Hotkeys: When the meeting ends (Off, Remind only, Warn, then stop), Longest recording 1-8, Offer to record meetings, Meeting apps",
                        mode != null && mode.Kind == SettingKind.Radio && mode.Group == MeetingsCard && mode.Label == "When the meeting ends"
                        && string.Join("|", mode.Options) == "Off|Remind only|Warn, then stop"
                        && hours != null && hours.Kind == SettingKind.Int && hours.Min == 1 && hours.Max == 8 && hours.Group == MeetingsCard
                        && offer != null && offer.Kind == SettingKind.Bool && offer.Label == "Offer to record meetings"
                        && apps != null && apps.Kind == SettingKind.Info && IndexOfField(pane, MeetingEndMode.SettingKey) == hotkeysAt + 1);
                    string v;
                    check("meetings card: the defaults are Warn, then stop, 3 hours, and the offer off; Meeting apps says the record is readable",
                        shown.TryGetValue(MeetingEndMode.SettingKey, out v) && v == "Warn, then stop"
                        && shown.TryGetValue(LongestRecordingKey, out v) && v == "3"
                        && shown.TryGetValue(OfferToRecordKey, out v) && v == "false"
                        && shown.TryGetValue("meetingApps", out v) && v.StartsWith("Teams and Zoom", StringComparison.Ordinal));
                    Dictionary<string, string> edit = CopyOf(shown);
                    edit[MeetingEndMode.SettingKey] = "Remind only";
                    edit[LongestRecordingKey] = "5";
                    edit[OfferToRecordKey] = "true";
                    pane.Save(edit);
                    DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings s = host.SettingsFor(Id);
                    IReadOnlyDictionary<string, string> back = pane.Load();
                    check("meetings card: Apply stores remind, 5 and true, and the pane shows them back",
                        s.Get(MeetingEndMode.SettingKey, "") == MeetingEndMode.Remind && s.Get(LongestRecordingKey, "") == "5"
                        && s.Get(OfferToRecordKey, "") == "true" && back[MeetingEndMode.SettingKey] == "Remind only" && back[LongestRecordingKey] == "5");
                    edit[MeetingEndMode.SettingKey] = "";
                    edit[LongestRecordingKey] = "12";
                    pane.Save(edit);
                    check("meetings card: a radio value that is no option stores nothing, and 12 hours is held to 8",
                        s.Get(MeetingEndMode.SettingKey, "") == MeetingEndMode.Remind && s.Get(LongestRecordingKey, "") == "8");
                    check("meetings card: an unreadable microphone record says so in Meeting apps",
                        new Func<string>(delegate
                        {
                            var bare = new QuestionsHost();
                            RemembranceModule m = start(new FakeConsentStore { Entries = null }, bare, Path.Combine(scratch, "pane2"));
                            string line;
                            bare.OptionsPanes[0].Load().TryGetValue("meetingApps", out line);
                            try { m.Shutdown(); } catch { }
                            return line ?? "";
                        })().StartsWith("Not available: Windows keeps no record of microphone use here", StringComparison.Ordinal));
                    try { module.Shutdown(); } catch { }
                }

                // ---- the floor and the permission ----
                {
                    var module = new RemembranceModule();
                    Version floor;
                    check("meeting end: the module asks for host 1.6.0 or newer, where ITrayQuestions arrived in Contracts; it declares " + module.Info.MinHostVersion,
                        Version.TryParse(module.Info.MinHostVersion, out floor) && floor >= new Version(1, 6, 0));
                    check("remembrance: the module asks for host 1.7.0 or newer, where PaneAction.EnabledWhen greys Remove token off Claude Code; it declares " + module.Info.MinHostVersion,
                        Version.TryParse(module.Info.MinHostVersion, out floor) && floor >= new Version(1, 7, 0));
                    check("meeting end: the module declares MicrophoneUsage, the reading of Teams' and Zoom's microphone times",
                        (module.Info.Permissions & ModulePermissions.MicrophoneUsage) == ModulePermissions.MicrophoneUsage);
                }

                check("meeting end in the module: every question the module put to the host keeps the ABI's rules" +
                      (allInvalid.Count > 0 ? ": " + string.Join("; ", allInvalid) : ""), allInvalid.Count == 0);
            }
            catch (Exception ex) { check("meeting end in the module threw " + ex.GetType().Name + ": " + ex.Message + " " + ex.StackTrace, false); }
            finally
            {
                WatchClock = savedClock;
                EnvironmentLookup = savedEnv;
                AudioRecorder.ScratchWriterFactory = savedFactory;
                SynchronizationContext.SetSynchronizationContext(previous);
                foreach (string t in transcripts)
                    SpinWait.SpinUntil(delegate { return File.Exists(t); }, TimeSpan.FromSeconds(5));
                try { Directory.Delete(scratch, true); } catch { }
            }
        }
    }
}
