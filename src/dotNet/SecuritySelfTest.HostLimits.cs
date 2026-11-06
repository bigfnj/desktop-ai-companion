using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace DesktopAICompanion
{
    /// <summary>
    /// The host limits a tick-clock companion gets (feature/shimeji-host-limits). Its frames may be up to
    /// <see cref="ShimejiFormatValidator.TickMaximumFrameDimension"/> a side where a classic companion's stay at
    /// <see cref="CompanionXmlValidator.MaximumSpriteFrameDimension"/>, and a clip that declares a format may be
    /// <see cref="CompanionXmlValidator.MaximumDeclaredAudioBytesPerSound"/> where an undeclared one stays at
    /// <see cref="CompanionXmlValidator.MaximumAudioBytesPerSound"/>, inside the unchanged document total. The declared
    /// limit is reached only through feature 'wav', which a host that does not run it refuses by name before any size is
    /// measured. Every sheet and clip is generated here.
    /// </summary>
    internal static partial class SecuritySelfTest
    {
        private static void CheckHostLimits(XmlData.RootNode classic, ref int failures, TextWriter output)
        {
            CheckFrameLimits(classic, ref failures, output);
            CheckClipLimits(classic, ref failures, output);
        }

        private static void CheckFrameLimits(XmlData.RootNode classic, ref int failures, TextWriter output)
        {
            int tickCap = ShimejiFormatValidator.TickMaximumFrameDimension, classicCap = CompanionXmlValidator.MaximumSpriteFrameDimension;
            XmlData.RootNode parsed;
            string error;
            Check(CompanionXmlValidator.TryParse(Serialize(TickWithCell(classic, 650, 320)), out parsed, out error),
                "host limits: a tick companion's 650 x 320 frame validates" + FormatError(error), ref failures, output);
            Check(CompanionXmlValidator.TryParse(Serialize(TickWithCell(classic, tickCap, 64)), out parsed, out error),
                "host limits: WITNESS a tick frame exactly the tick cap wide validates (" + tickCap + " px)" + FormatError(error), ref failures, output);
            Check(!CompanionXmlValidator.TryParse(Serialize(TickWithCell(classic, tickCap + 1, 64)), out parsed, out error)
                  && Contains(error, "contains a frame larger than the runtime supports (" + tickCap + " px a side)"),
                "host limits: a tick frame one pixel past the tick cap is refused (" + error + ")", ref failures, output);
            Check(CompanionXmlValidator.TryParse(Serialize(ClassicWithCell(classic, classicCap, classicCap)), out parsed, out error),
                "host limits: WITNESS a classic frame exactly the classic cap validates (" + classicCap + " px)" + FormatError(error), ref failures, output);
            Check(!CompanionXmlValidator.TryParse(Serialize(ClassicWithCell(classic, 300, 300)), out parsed, out error)
                  && Contains(error, "contains a frame larger than the runtime supports (" + classicCap + " px a side)"),
                "host limits: a classic companion with a 300 px frame is refused (" + error + ")", ref failures, output);
        }

        private static void CheckClipLimits(XmlData.RootNode classic, ref int failures, TextWriter output)
        {
            const int Mi = 1024 * 1024;
            int declared = CompanionXmlValidator.MaximumDeclaredAudioBytesPerSound, undeclared = CompanionXmlValidator.MaximumAudioBytesPerSound;
            XmlData.RootNode parsed;
            string error;
            byte[] atDeclared = WaveOfLength(declared);
            Check(CompanionXmlValidator.TryParseWithFeatures(Serialize(WavClipFixture(classic, atDeclared, "wav", true)), WavExtensionFeatures, out parsed, out error),
                "host limits: WITNESS a clip declaring a format validates at exactly the declared limit (" + declared / Mi + " MiB)" + FormatError(error),
                ref failures, output);
            byte[] pastDeclared = WaveOfLength(declared + 1);
            Check(!CompanionXmlValidator.TryParseWithFeatures(Serialize(WavClipFixture(classic, pastDeclared, "wav", true)), WavExtensionFeatures, out parsed, out error)
                  && Contains(error, "sound exceeds its decoded size limit"),
                "host limits: a declared clip one byte past the declared limit is refused (" + error + ")", ref failures, output);
            // format="mp3" declares a format too: the attribute, whatever its value, is what needs the token.
            Check(CompanionXmlValidator.TryParseWithFeatures(Serialize(WavClipFixture(classic, Id3OfLength(3 * Mi), "mp3", true)), WavExtensionFeatures, out parsed, out error),
                "host limits: a clip declared format=\"mp3\" gets the declared limit too (3 MiB)" + FormatError(error), ref failures, output);
            Check(CompanionXmlValidator.TryParseWithFeatures(Serialize(WavClipFixture(classic, Id3OfLength(undeclared), null, false)), AllExtensionFeatures, out parsed, out error),
                "host limits: WITNESS an undeclared clip validates at exactly 2 MiB" + FormatError(error), ref failures, output);
            Check(!CompanionXmlValidator.TryParseWithFeatures(Serialize(WavClipFixture(classic, Id3OfLength(undeclared + 1), null, false)), AllExtensionFeatures, out parsed, out error)
                  && Contains(error, "sound exceeds its decoded size limit"),
                "host limits: an undeclared clip one byte past 2 MiB is refused (" + error + ")", ref failures, output);
            int animation = classic.Animations.Animation[0].Id;
            XmlData.RootNode classicSound = With(Clone(classic), r => r.Sounds = new XmlData.SoundsNode
                { Sound = new[] { new XmlData.SoundNode { Id = animation, Probability = 10, Base64 = Convert.ToBase64String(Id3OfLength(undeclared + 1)) } } });
            Check(!CompanionXmlValidator.TryParse(Serialize(classicSound), out parsed, out error) && Contains(error, "sound exceeds its decoded size limit"),
                "host limits: a classic sound one byte past 2 MiB is refused (" + error + ")", ref failures, output);

            // The order: a clip past EVERY size limit, on a host that does not run 'wav', is refused by the feature's name;
            // and a format with the token undeclared is refused by name too, before any clip is measured.
            Check(!CompanionXmlValidator.TryParseWithFeatures(Serialize(WavClipFixture(classic, pastDeclared, "wav", true)), AllExtensionFeatures, out parsed, out error)
                  && Contains(error, "uses feature 'wav', which this version of Desktop AI Companion does not run yet") && !Contains(error, "size limit"),
                "host limits: a host that does not run 'wav' refuses a clip past every size limit by the feature's name, not its size (" + error + ")",
                ref failures, output);
            Check(!CompanionXmlValidator.TryParseWithFeatures(Serialize(WavClipFixture(classic, pastDeclared, "wav", false)), WavExtensionFeatures, out parsed, out error)
                  && Contains(error, "format needs feature 'wav' declared in header/features") && !Contains(error, "size limit"),
                "host limits: an undeclared 'wav' is refused by name before a clip past every size limit is measured (" + error + ")",
                ref failures, output);

            // The document total stays: three declared clips of exactly 8 MiB together validate, one byte more does not. A
            // small generated sheet keeps the document under the XML limit, which the base64 of 8 MiB nearly fills.
            int total = CompanionXmlValidator.MaximumAudioBytesTotal, last = 1000;
            int second = total - declared - last;
            Check(CompanionXmlValidator.TryParseWithFeatures(Serialize(ThreeClips(classic, declared, second, last)), WavExtensionFeatures, out parsed, out error),
                "host limits: WITNESS declared clips of exactly the document total validate (" + total / Mi + " MiB)" + FormatError(error), ref failures, output);
            Check(!CompanionXmlValidator.TryParseWithFeatures(Serialize(ThreeClips(classic, declared, second, last + 1)), WavExtensionFeatures, out parsed, out error)
                  && Contains(error, "Pet audio exceeds the total size limit"),
                "host limits: one byte past the document total is refused, however the clips are declared (" + error + ")", ref failures, output);
        }

        /// <summary>The extension gate's tick fixture on a generated sheet of two cells of the given size.</summary>
        private static XmlData.RootNode TickWithCell(XmlData.RootNode classic, int width, int height)
        {
            XmlData.RootNode r = TickFixture(classic);
            r.Image.TilesX = 2;
            r.Image.TilesY = 1;
            r.Image.Png = SheetPng(width * 2, height);
            return r;
        }

        /// <summary>The bundled pet with every animation on frame 0 of a generated one-cell sheet of the given size.</summary>
        private static XmlData.RootNode ClassicWithCell(XmlData.RootNode classic, int width, int height)
        {
            XmlData.RootNode r = Clone(classic);
            r.Image.TilesX = 1;
            r.Image.TilesY = 1;
            r.Image.Png = SheetPng(width, height);
            foreach (XmlData.AnimationNode a in r.Animations.Animation)
            {
                a.Sequence.Frame = new[] { 0 };
                a.Sequence.RepeatFromFrame = 0;
            }
            return r;
        }

        /// <summary>A tick companion on a small sheet with three declared WAV clips of the given byte lengths.</summary>
        private static XmlData.RootNode ThreeClips(XmlData.RootNode classic, int a, int b, int c)
        {
            XmlData.RootNode r = TickWithCell(classic, 16, 16);
            r.Header.Features += " " + CompanionFormatFeatures.Wav;
            r.Sounds = new XmlData.SoundsNode
            {
                Sound = new[]
                {
                    new XmlData.SoundNode { SoundId = "a", Format = "wav", Base64 = Convert.ToBase64String(WaveOfLength(a)) },
                    new XmlData.SoundNode { SoundId = "b", Format = "wav", Base64 = Convert.ToBase64String(WaveOfLength(b)) },
                    new XmlData.SoundNode { SoundId = "c", Format = "wav", Base64 = Convert.ToBase64String(WaveOfLength(c)) },
                },
            };
            StandAct(r).Variants.Variant[0].Pose[0].Sound = "a";
            return r;
        }

        /// <summary>An 8-bit mono 22.05 kHz PCM clip of exactly <paramref name="bytes"/> bytes (a plain 44-byte header).</summary>
        private static byte[] WaveOfLength(int bytes)
        {
            byte[] wav = AudioOutputSelfTest.SyntheticWave(1, 8, 1, 22050, bytes - 44, false);
            if (wav.Length != bytes) throw new InvalidOperationException("the generated clip is " + wav.Length + " bytes, not " + bytes);
            return wav;
        }

        /// <summary>Bytes the MP3 sniff accepts (an ID3 tag's opening), <paramref name="bytes"/> long; never decoded.</summary>
        private static byte[] Id3OfLength(int bytes)
        {
            var b = new byte[bytes];
            b[0] = (byte)'I'; b[1] = (byte)'D'; b[2] = (byte)'3';
            return b;
        }

        /// <summary>A plain PNG sheet of the given size, as the base64 an &lt;image&gt;&lt;png&gt; holds. The window self-test's
        /// large frames use it too.</summary>
        internal static string SheetPng(int width, int height)
        {
            using (var sheet = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            using (var ms = new MemoryStream())
            {
                using (Graphics g = Graphics.FromImage(sheet)) g.Clear(Color.SteelBlue);
                sheet.Save(ms, ImageFormat.Png);
                return Convert.ToBase64String(ms.ToArray());
            }
        }
    }
}
