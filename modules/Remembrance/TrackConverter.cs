using System;
using System.Collections.Generic;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// One track's device audio turned into the recording's own format AS IT ARRIVES: 16 kHz, mono, 16-bit PCM, the
    /// format MixToWhisperWav has always produced at the stop and the one Whisper reads (lane feature/meeting-end,
    /// remembrance 2.4.0, the owner's decision of 2026-10-15).
    ///
    /// WHY AT CAPTURE TIME. Until 2.4.0 each scratch track was written in the device's format, on this machine 48 kHz
    /// float stereo, about 1.3 GB an hour, measured. NAudio refuses a WAV write past 4 GB, which that reaches after about
    /// 3.1 hours, and AudioRecorder's DataAvailable handler swallowed the exception: from that second on the track
    /// stopped saving and nothing said so. At 16 kHz mono 16-bit the same hour is 115 MB, so a track reaches 4 GB after
    /// some 37 hours, and the RF64 writer behind it (AudioRecorder.ScratchWriterFactory) is the backstop past that.
    ///
    /// THE SAME CONVERSION THE MIX DID, piece for piece: NAudio's own decoder for the device format (ToSampleProvider),
    /// then StereoToMonoSampleProvider at 0.5/0.5 for two channels or MultiplexingSampleProvider's first channel for more,
    /// then the WDL resampler to 16 kHz in exactly the mode WdlResamplingSampleProvider sets, then the clamp and the
    /// (short)(f * 32767) that WaveFileWriter.CreateWaveFile16 applies. The self-test holds the result to the old offline
    /// chain within 2 LSB and 2 samples of length for the device shapes this module meets.
    ///
    /// NOT WdlResamplingSampleProvider ITSELF, and this is the one place the design moved. That provider asks its source
    /// for as many input frames as the next output block needs, and the WDL resampler underneath treats a SHORT answer
    /// as the end of the stream: it pads with zeros and flushes ("if numsamples_in &lt; the value returned by
    /// ResamplePrepare(), then it will be flushed", NAudio's own doc). Behind a BufferedWaveProvider that answers only
    /// what one packet held, measured on 2026-10-15, ten seconds of 48 kHz stereo came out as 320 samples instead of
    /// 160,000. So the resampler is driven here directly: ResamplePrepare is asked how many input frames a block needs,
    /// the block shrinks until the frames on hand cover it, and it is fed EXACTLY that many, never fewer. What is left
    /// waits for the next packet. The NAudio doc says ResamplePrepare may be called again without ResampleOut.
    ///
    /// NO PADDING AND NO FLUSH AT THE STOP. A packet's last few input frames wait for the next one, and the resampler
    /// keeps its own few; at the stop that remainder, well under a millisecond, is simply not written. Padding would
    /// lengthen the track by audio nobody recorded, and the self-test pins the length either way.
    ///
    /// Runs on the capture thread only, one instance per source, so nothing here is locked.
    /// </summary>
    internal sealed class TrackConverter
    {
        internal const int TargetRate = 16000;

        /// <summary>A fresh 16 kHz mono 16-bit format. A method rather than a shared instance: WaveFormat is mutable.</summary>
        internal static WaveFormat TargetFormat() { return new WaveFormat(TargetRate, 16, 1); }

        // The bound on one packet's drain loop. Each round writes up to OutBlock samples or stops; a packet of a second
        // needs about four, so the bound only matters if a provider ever kept answering without consuming anything.
        private const int MaxRounds = 4096;
        private const int OutBlock = 4096;

        private readonly int _blockAlign;
        private readonly bool _passThrough;
        private readonly BufferedWaveProvider _buffer;
        private readonly int _slice;
        private readonly ISampleProvider _mono;
        private readonly WdlResampler _resampler;   // null when the device already runs at 16 kHz
        private readonly double _ratio;
        private readonly byte[] _carry;
        private int _carryCount;
        private float[] _fifo = new float[OutBlock * 8];
        private int _fifoCount;
        private readonly float[] _read = new float[OutBlock];
        private readonly float[] _out = new float[OutBlock];
        private byte[] _pcm = new byte[OutBlock * 2];

        /// <summary>16 kHz samples handed on so far: the track's length as written.</summary>
        internal long SamplesOut { get; private set; }

        /// <summary>True when the device already delivers 16 kHz mono 16-bit PCM and the bytes go through untouched.</summary>
        internal bool PassThrough { get { return _passThrough; } }

        private TrackConverter(WaveFormat native)
        {
            WaveFormat standard = Standardize(native);
            _blockAlign = Math.Max(1, standard.BlockAlign);
            _carry = new byte[_blockAlign];
            _passThrough = standard.Encoding == WaveFormatEncoding.Pcm && standard.SampleRate == TargetRate
                           && standard.BitsPerSample == 16 && standard.Channels == 1;
            if (_passThrough) return;

            // Five seconds of the device format: one packet is ten to a hundred milliseconds, and Convert feeds it in
            // slices of at most half this, draining after each, so the buffer never holds more than one slice and its
            // overflow (DiscardOnBufferOverflow false: it throws, and the source records the throw) cannot be reached.
            _buffer = new BufferedWaveProvider(standard, TimeSpan.FromSeconds(5)) { ReadFully = false, DiscardOnBufferOverflow = false };
            _slice = Math.Max(_blockAlign, (_buffer.BufferLength / 2) / _blockAlign * _blockAlign);
            ISampleProvider sp = _buffer.ToSampleProvider();   // throws for an encoding NAudio cannot decode: the caller falls back
            if (sp.WaveFormat.Channels == 2)
                sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
            else if (sp.WaveFormat.Channels > 2)
                sp = new MultiplexingSampleProvider(new[] { sp }, 1);   // take the first channel
            _mono = sp;
            if (standard.SampleRate != TargetRate)
            {
                // WdlResamplingSampleProvider's own configuration, call for call (read off the pinned 3.0.0-preview.6 by
                // reflection on 2026-10-15: every field of the two resamplers matched).
                _resampler = new WdlResampler();
                _resampler.SetMode(true, 2, false);
                _resampler.SetFilterParms();
                _resampler.SetFeedMode(false);
                _resampler.SetRates(standard.SampleRate, TargetRate);
                _ratio = standard.SampleRate / (double)TargetRate;
            }
        }

        /// <summary>
        /// The converter for a device format, or null with the reason when NAudio has no decoder for it. A null means
        /// the track is written in the device's own format, as every version before 2.4.0 wrote it, and the mix at the
        /// stop converts it there; nothing is lost but the size, and the module logs why.
        /// </summary>
        internal static TrackConverter TryCreate(WaveFormat native, out string failure)
        {
            failure = null;
            if (native == null) { failure = "the device reported no format"; return null; }
            try { return new TrackConverter(native); }
            catch (Exception ex)
            {
                // The type and the format, never more: the message can be NAudio's own and is no use to a reader.
                failure = ex.GetType().Name + " for " + native.Encoding + " " + native.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture)
                          + " Hz, " + native.BitsPerSample.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-bit, "
                          + native.Channels.ToString(System.Globalization.CultureInfo.InvariantCulture) + " channel(s)";
                return null;
            }
        }

        /// <summary>
        /// A WASAPI mix format is usually WAVE_FORMAT_EXTENSIBLE, and NAudio's ToSampleProvider refuses that encoding
        /// outright ("Unsupported source encoding", measured on the pinned NAudio 2026-10-15) even when the sub-format is
        /// plain PCM or IEEE float. The same layout as a standard format decodes. Also used by the mix, for a scratch
        /// written in the device's format by an earlier version or by the fallback above.
        /// </summary>
        internal static WaveFormat Standardize(WaveFormat format)
        {
            if (format == null || format.Encoding != WaveFormatEncoding.Extensible) return format;
            WaveFormat standard = format.AsStandardWaveFormat();
            if (standard.Encoding != WaveFormatEncoding.Extensible) return standard;
            // Read back from a file, the format is a WaveFormatExtraData carrying the extension as bytes: two of valid
            // bits, four of channel mask, then the sixteen-byte sub-format GUID.
            var extra = format as WaveFormatExtraData;
            byte[] bytes = extra != null ? extra.ExtraData : null;
            if (bytes == null || bytes.Length < 22) return format;
            var sub = new Guid(new ReadOnlySpan<byte>(bytes, 6, 16));
            if (sub == PcmSubFormat)
                return WaveFormat.CreateCustomFormat(WaveFormatEncoding.Pcm, format.SampleRate, format.Channels,
                    format.AverageBytesPerSecond, format.BlockAlign, format.BitsPerSample);
            if (sub == IeeeFloatSubFormat)
                return WaveFormat.CreateCustomFormat(WaveFormatEncoding.IeeeFloat, format.SampleRate, format.Channels,
                    format.AverageBytesPerSecond, format.BlockAlign, format.BitsPerSample);
            return format;
        }

        private static readonly Guid PcmSubFormat = new Guid("00000001-0000-0010-8000-00aa00389b71");
        private static readonly Guid IeeeFloatSubFormat = new Guid("00000003-0000-0010-8000-00aa00389b71");

        /// <summary>
        /// Convert one packet. <paramref name="writePcm"/> receives the 16-bit bytes to append to the scratch, and
        /// <paramref name="meter"/> the same audio as floats in [-1, 1], for the levels the meeting-end watch reads. A
        /// device that already runs at the target format hands its bytes to writePcm unchanged. Throws whatever a
        /// decoder or the writer throws; the source catches it and stops that track (AudioRecorder).
        /// </summary>
        internal void Convert(byte[] data, int count, Action<byte[], int> writePcm, Action<float[], int> meter)
        {
            if (data == null || count <= 0) return;
            int offset = 0;
            // A packet that ends inside a frame (WASAPI delivers whole frames, so this is a guard, not the expected
            // case) keeps the partial frame for the next one: fed whole, it would shift every later sample by a byte.
            if (_carryCount > 0)
            {
                int need = Math.Min(_blockAlign - _carryCount, count);
                Buffer.BlockCopy(data, 0, _carry, _carryCount, need);
                _carryCount += need;
                offset = need;
                if (_carryCount < _blockAlign) return;
                Feed(_carry, 0, _blockAlign, writePcm, meter);
                _carryCount = 0;
            }
            int whole = (count - offset) / _blockAlign * _blockAlign;
            if (whole > 0) Feed(data, offset, whole, writePcm, meter);
            int rest = count - offset - whole;
            if (rest > 0)
            {
                Buffer.BlockCopy(data, offset + whole, _carry, 0, rest);
                _carryCount = rest;
            }
        }

        private void Feed(byte[] data, int offset, int count, Action<byte[], int> writePcm, Action<float[], int> meter)
        {
            if (_passThrough)
            {
                writePcm(Slice(data, offset, count), count);
                MeterPcm16(data, offset, count, meter);
                SamplesOut += count / 2;
                return;
            }
            for (int at = 0; at < count; at += _slice)
            {
                int n = Math.Min(_slice, count - at);
                _buffer.AddSamples(data, offset + at, n);
                PullMono();
                Drain(writePcm, meter);
            }
        }

        // writePcm takes (bytes, count) from index 0, so a pass-through packet that does not start at 0 is copied once.
        private static byte[] Slice(byte[] data, int offset, int count)
        {
            if (offset == 0) return data;
            var copy = new byte[count];
            Buffer.BlockCopy(data, offset, copy, 0, count);
            return copy;
        }

        // Everything the buffer holds, decoded and downmixed, onto the mono FIFO. The decoders and the downmix keep no
        // state between reads, so a short read here is harmless; only the resampler below must never be fed short.
        private void PullMono()
        {
            for (int round = 0; round < MaxRounds; round++)
            {
                int n = _mono.Read(_read.AsSpan());
                if (n <= 0) break;
                EnsureFifo(_fifoCount + n);
                Array.Copy(_read, 0, _fifo, _fifoCount, n);
                _fifoCount += n;
            }
        }

        private void Drain(Action<byte[], int> writePcm, Action<float[], int> meter)
        {
            if (_resampler == null)
            {
                int at = 0;
                while (at < _fifoCount)
                {
                    int n = Math.Min(OutBlock, _fifoCount - at);
                    Array.Copy(_fifo, at, _out, 0, n);
                    Emit(n, writePcm, meter);
                    at += n;
                }
                _fifoCount = 0;
                return;
            }
            for (int round = 0; round < MaxRounds; round++)
            {
                int want = (int)Math.Min(OutBlock, Math.Floor(_fifoCount / _ratio));
                if (want <= 0) break;
                Span<float> into;
                int need = _resampler.ResamplePrepare(want, 1, out into);
                // Shrink the block until the frames on hand cover what it needs. Bounded: the first estimate is off by
                // the resampler's few frames of look-ahead, so one or two steps are the norm.
                for (int tries = 0; need > _fifoCount && tries < 16; tries++)
                {
                    want -= Math.Max(1, (int)Math.Ceiling((need - _fifoCount) / _ratio));
                    if (want <= 0) break;
                    need = _resampler.ResamplePrepare(want, 1, out into);
                }
                if (want <= 0 || need > _fifoCount) break;   // wait for the next packet; never feed short
                if (need > 0) _fifo.AsSpan(0, need).CopyTo(into);
                Consume(need);
                int got = _resampler.ResampleOut(_out.AsSpan(), need, want, 1);
                if (got <= 0) break;
                Emit(got, writePcm, meter);
            }
        }

        // Clamp, hand the floats to the meter, and quantize as CreateWaveFile16 does: (short)(sample * 32767).
        private void Emit(int n, Action<byte[], int> writePcm, Action<float[], int> meter)
        {
            if (n <= 0) return;
            if (_pcm.Length < n * 2) _pcm = new byte[n * 2];
            for (int i = 0; i < n; i++)
            {
                float f = _out[i];
                if (f > 1f) f = 1f;
                else if (f < -1f) f = -1f;
                _out[i] = f;
                short s = (short)(f * 32767);
                _pcm[2 * i] = (byte)s;
                _pcm[2 * i + 1] = (byte)(s >> 8);
            }
            if (meter != null) meter(_out, n);
            writePcm(_pcm, n * 2);
            SamplesOut += n;
        }

        // A pass-through packet's levels: the meter wants floats, the writer keeps the device's bytes exactly.
        private void MeterPcm16(byte[] data, int offset, int count, Action<float[], int> meter)
        {
            if (meter == null) return;
            int samples = count / 2;
            int at = 0;
            while (at < samples)
            {
                int n = Math.Min(OutBlock, samples - at);
                for (int i = 0; i < n; i++)
                {
                    int b = offset + 2 * (at + i);
                    _out[i] = (short)(data[b] | (data[b + 1] << 8)) / 32768f;
                }
                meter(_out, n);
                at += n;
            }
        }

        private void EnsureFifo(int size)
        {
            if (_fifo.Length >= size) return;
            var bigger = new float[Math.Max(size, _fifo.Length * 2)];
            Array.Copy(_fifo, bigger, _fifoCount);
            _fifo = bigger;
        }

        private void Consume(int n)
        {
            if (n <= 0) return;
            int rest = _fifoCount - n;
            if (rest > 0) Array.Copy(_fifo, n, _fifo, 0, rest);
            _fifoCount = Math.Max(0, rest);
        }
    }

    /// <summary>One second of a track's loudness. Index counts the track's own seconds from its first sample.</summary>
    internal struct LevelSecond
    {
        internal int Index;
        /// <summary>The second's RMS in dBFS; -120 for digital silence.</summary>
        internal float RmsDb;
        /// <summary>The quietest of its ten 100 ms frames, in dBFS: the gaps between words, or the room's noise.</summary>
        internal float MinFrameDb;

        internal LevelSecond(int index, float rmsDb, float minFrameDb) { Index = index; RmsDb = rmsDb; MinFrameDb = minFrameDb; }
    }

    /// <summary>
    /// A track's loudness, one <see cref="LevelSecond"/> a second of its 16 kHz audio, for the meeting-end watch
    /// (RecordingWatch). Fed on the capture thread by the converter, read on the UI thread by the watch's tick, so the
    /// finished seconds sit behind a lock. Loudness only, kept in memory for the life of the recording and never written
    /// anywhere: it says whether someone was talking, nothing about what was said (PRIVACY.md). The newest hour is kept.
    /// </summary>
    internal sealed class TrackMeter
    {
        internal const int Rate = TrackConverter.TargetRate;
        internal const int FrameSamples = Rate / 10;   // 100 ms
        internal const int FramesPerSecond = 10;
        internal const float SilenceDb = -120f;
        internal const int SecondsKept = 3600;

        private double _frameSum;
        private int _frameFill;
        private double _secondSum;
        private int _framesInSecond;
        private float _minFrameDb = float.MaxValue;
        private int _next;
        private readonly List<LevelSecond> _seconds = new List<LevelSecond>();
        private readonly object _lock = new object();

        /// <summary>Samples at 16 kHz, in [-1, 1].</summary>
        internal void Add(float[] samples, int count)
        {
            if (samples == null) return;
            for (int i = 0; i < count; i++)
            {
                double s = samples[i];
                _frameSum += s * s;
                if (++_frameFill == FrameSamples) CloseFrame();
            }
        }

        private void CloseFrame()
        {
            float db = Db(_frameSum / FrameSamples);
            if (db < _minFrameDb) _minFrameDb = db;
            _secondSum += _frameSum;
            _frameSum = 0;
            _frameFill = 0;
            if (++_framesInSecond < FramesPerSecond) return;
            var second = new LevelSecond(0, Db(_secondSum / Rate), _minFrameDb);
            lock (_lock)
            {
                second.Index = _next++;
                _seconds.Add(second);
                if (_seconds.Count > SecondsKept) _seconds.RemoveRange(0, _seconds.Count - SecondsKept);
            }
            _secondSum = 0;
            _framesInSecond = 0;
            _minFrameDb = float.MaxValue;
        }

        /// <summary>The finished seconds from <paramref name="index"/> on, oldest first (fewer when older ones aged out).</summary>
        internal List<LevelSecond> CopySince(int index)
        {
            lock (_lock)
            {
                var copy = new List<LevelSecond>();
                if (_seconds.Count == 0) return copy;
                int first = _seconds[0].Index;
                int start = Math.Max(0, index - first);
                for (int i = start; i < _seconds.Count; i++) copy.Add(_seconds[i]);
                return copy;
            }
        }

        /// <summary>How many whole seconds have been measured.</summary>
        internal int SecondsMeasured { get { lock (_lock) return _next; } }

        /// <summary>A mean square as dBFS, floored at -120 (digital silence).</summary>
        internal static float Db(double meanSquare)
        {
            if (meanSquare <= 1e-12) return SilenceDb;
            return (float)Math.Max(SilenceDb, 10.0 * Math.Log10(meanSquare));
        }
    }

    /// <summary>
    /// A scratch track that stopped saving: which one, how far into it, and the exception's type. Since 2.4.0 a write
    /// that throws stops THAT track for good, rather than being swallowed packet after packet (the 4 GB cut-off went
    /// unseen that way for three hours a meeting). Never resumed: the mix lines the tracks up from their first sample,
    /// so a track that picked up again later would put everything after the gap early. The module logs it once and the
    /// Status line shows it.
    /// </summary>
    internal sealed class TrackWriteFailure
    {
        internal readonly string Track;
        internal readonly TimeSpan At;
        internal readonly string ExceptionType;

        internal TrackWriteFailure(string track, TimeSpan at, string exceptionType)
        {
            Track = track ?? "";
            At = at;
            ExceptionType = exceptionType ?? "";
        }
    }
}
