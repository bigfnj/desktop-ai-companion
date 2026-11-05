using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// The sound half of --shimeji-window-selftest: tick companions in real windows (opacity 0) whose poses name clips,
    /// played into a recording audio sink that never reaches a device; the app's volume rules; AudioOutput's per-clip
    /// owner bookkeeping, asserted with live inputs that play nowhere; the staged clip bytes; the parity trace's sound
    /// event. Nothing here makes a sound.
    /// </summary>
    internal static class ShimejiSoundSelfTest
    {
        public const string TypeId = "selftest-sound";

        /// <summary>A sink that plays nothing: a clip is "playing" from its start until the test says it finished or it
        /// is stopped. Owners compare as AudioOutput compares them (case-insensitively).</summary>
        private sealed class RecordingAudio : ITickAudio
        {
            public readonly List<string> Plays = new List<string>();
            public readonly List<byte[]> Bytes = new List<byte[]>();
            public readonly List<string> Stops = new List<string>();
            public readonly HashSet<string> Playing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public bool Play(string owner, byte[] mp3, double gain)
            {
                Plays.Add(owner + " " + gain.ToString("0.000", CultureInfo.InvariantCulture));
                Bytes.Add(mp3);
                Playing.Add(owner);
                return true;
            }

            public bool IsPlaying(string owner) { return Playing.Contains(owner); }

            public bool Stop(string owner)
            {
                Stops.Add(owner);
                return Playing.Remove(owner);
            }
        }

        /// <summary>Program.MyData must be the caller's scratch settings.</summary>
        public static void Run(Action<bool, string> check, StringBuilder log)
        {
            CheckHost(check, log);
            CheckRoute(check);
            CheckOutputOwners(check);
            CheckStagedClips(check);
        }

        private static void CheckHost(Action<bool, string> check, StringBuilder log)
        {
            TickHost host = null;
            var frames = new List<Bitmap>();
            try
            {
                foreach (Color c in new[] { Color.Purple, Color.Teal })
                {
                    var b = new Bitmap(64, 64);
                    using (Graphics g = Graphics.FromImage(b)) g.Clear(c);
                    frames.Add(b);
                }
                var sprites = new Xml(frames, 64, 64);
                TickType type = TickType.Compile("Alpha", Document());
                type.ApplyScale(1.0, sprites.spriteWidth, sprites.spriteHeight);
                byte[] la = { 1, 2, 3 }, lo = { 4, 5, 6 };
                var hostType = new TickHostType(TypeId, type, sprites)
                {
                    Clips = new Dictionary<string, byte[]>(StringComparer.Ordinal) { { "la", la }, { "lo", lo } },
                };
                var audio = new RecordingAudio();
                host = new TickHost(t =>
                    {
                        var form = new FormCompanion(new Animations(t.Sprites), t.Sprites);
                        form.Show(t.Type.CellWidth, t.Type.CellHeight);
                        return form;
                    },
                    form => form.Close(),
                    message => log.AppendLine("LOG: " + message)) { RenderOpacity = 0.0, Audio = audio };
                host.Environment.SearchesWindows = false;
                host.AddType(hostType);

                Rectangle work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                int x = work.Left + work.Width / 2, floor = work.Bottom;
                TickMascot a = host.Spawn("Alpha", new TickPoint(x - 150, floor), false, "Sing");
                host.Spawn("Alpha", new TickPoint(x + 150, floor), false, "Sing");
                string owner = HostTickClips.OwnerOf(new TickClip("Alpha", "la", -6f));

                host.Step();   // both companions apply "la" at -6 dB
                check(audio.Plays.Count == 1 && audio.Plays[0] == owner + " 0.501" && ReferenceEquals(audio.Bytes[0], la),
                    "sound: a pose's clip plays once for the two companions naming it, with the type's bytes at the pose's gain ("
                    + string.Join("; ", audio.Plays) + ")");
                host.Step();
                check(audio.Plays.Count == 1, "sound: a clip still playing is not started again");
                audio.Playing.Clear();   // the clip finished
                host.Step();
                check(audio.Plays.Count == 2, "sound: a clip that finished while its pose names it plays again at the next step");

                // A third companion's Mute (applied as it is created) stops the clip the other two are playing.
                host.Spawn("Alpha", new TickPoint(x, floor), false, "Hush");
                check(audio.Stops.Count == 1 && audio.Stops[0] == owner && !audio.Playing.Contains(owner),
                    "sound: a named Mute stops the clip other companions started (" + string.Join("; ", audio.Stops) + ")");

                // The switches are the engine's Sounds setting (HostTickSettings): the global one, then the type's own.
                LocalData data = Program.MyData;
                int plays = audio.Plays.Count;
                data.SetPetSoundsEnabled(false);
                host.Step();
                check(audio.Plays.Count == plays, "sound: with companion sounds switched off no clip plays");
                data.SetPetSoundsEnabled(true);
                data.SetPetSoundEnabled(TypeId, false);
                host.Step();
                check(audio.Plays.Count == plays, "sound: with the type's own sound off no clip plays");
                data.SetPetSoundEnabled(TypeId, true);
                host.Step();
                check(audio.Plays.Count == plays + 1, "sound: WITNESS with both on again the clip plays");

                // A companion that leaves does not cut the clip (a classic pet's sound plays out too, and the source's
                // dispose leaves its clip).
                int stops = audio.Stops.Count;
                host.FormOf(a).Kill();
                host.Step();
                check(a.Disposing && audio.Stops.Count == stops && audio.Playing.Contains(owner),
                    "sound: a companion leaving does not stop the clip it was playing");

                var trace = new StringWriter();
                var writer = new ParityTraceWriter(trace, new Dictionary<string, ParityMap>()) { Tick = 7 };
                writer.Sound(a, "la", "start");
                check(trace.ToString().Contains("\"tick\":7,\"sid\":\"" + a.Sid + "\",\"kind\":\"sound\",\"clip\":\"la\",\"op\":\"start\""),
                    "parity: the trace's sound event carries the clip and the op (" + trace.ToString().Trim() + ")");
            }
            finally
            {
                if (host != null) host.Dispose();
            }
        }

        /// <summary>The app's rules a clip plays under (AppTickAudio): the user's volume times the pose's gain, capped at
        /// full scale; nothing while sounds are held for a fullscreen window, or with no settings.</summary>
        private static void CheckRoute(Action<bool, string> check)
        {
            LocalData data = Program.MyData;
            double before = data.GetVolume();
            try
            {
                data.SetVolume(0.5);
                check(Math.Abs(AppTickAudio.Volume(data, false, 0.5) - 0.25) < 1e-6, "sound route: a clip plays at the user's volume times the pose's gain");
                check(AppTickAudio.Volume(data, true, 1.0) == 0.0, "sound route: nothing plays while sounds are held for a fullscreen window");
                check(AppTickAudio.Volume(null, false, 1.0) == 0.0, "sound route: nothing plays without settings");
                data.SetVolume(1.0);
                check(AppTickAudio.Volume(data, false, 2.0) == 1.0, "sound route: a gain above 1 is capped at full scale");
            }
            finally
            {
                data.SetVolume(before);
            }
        }

        /// <summary>AudioOutput's owner bookkeeping for clips, with live inputs that play nowhere (no device is opened).</summary>
        private static void CheckOutputOwners(Action<bool, string> check)
        {
            using (var output = new AudioOutput())
            {
                string clip = HostTickClips.OwnerOf(new TickClip("Alpha", "la", 0f));
                output.RegisterLiveForTest(clip);
                output.RegisterLiveForTest("some.module");
                output.RegisterLiveForTest(AudioOutput.EngineOwner);
                check(output.IsPlaying(clip) && output.IsPlaying("some.module") && !output.IsPlaying("other.module"),
                    "sound output: an owner is playing while it has a live input, and only then");
                output.StopAllExcept(AudioOutput.EngineOwner);
                check(!output.IsPlaying("some.module") && output.IsPlaying(clip) && output.IsPlaying(AudioOutput.EngineOwner),
                    "sound output: cutting every module's sound keeps the companions' clips and the engine's own");
                output.StopOwned(clip);
                check(!output.IsPlaying(clip), "sound output: a clip's owner stops by itself");
            }
        }

        /// <summary>The app's staging keeps a tick companion's clips by name, decoded, though the staged document's own
        /// base64 is blanked; the host type takes them; the pose keeps its volume through the document.</summary>
        private static void CheckStagedClips(Action<bool, string> check)
        {
            XmlData.RootNode classic;
            string error;
            if (!CompanionXmlValidator.TryParse(Properties.Resources.animations, out classic, out error))
            {
                check(false, "sound staging: the bundled pet parses (" + error + ")");
                return;
            }
            XmlData.RootNode r = SecuritySelfTest.TickFixture(classic);
            byte[] mp3 = SecuritySelfTest.ValidMp3Fixture();
            r.Sounds = new XmlData.SoundsNode { Sound = new[] { new XmlData.SoundNode { SoundId = "la", Base64 = Convert.ToBase64String(mp3) } } };
            XmlData.PoseNode pose = r.Animations.Animation[2].Act.Variants.Variant[0].Pose[0];
            pose.Sound = "la";
            pose.Volume = -6;
            pose.VolumeSpecified = true;
            Xml xml;
            Animations animations;
            bool staged = StartUp.TryStageRuntime(SecuritySelfTest.Serialize(r), 1.0, out xml, out animations, out error);
            try
            {
                IReadOnlyDictionary<string, byte[]> clips = staged ? TickStaging.ClipsOf(xml) : null;
                byte[] la;
                check(clips != null && clips.TryGetValue("la", out la) && la.SequenceEqual(mp3)
                      && string.IsNullOrEmpty(xml.AnimationXML.Sounds.Sound[0].Base64),
                    "sound staging: a named clip is kept by name, decoded, though the staged document's base64 is blanked"
                    + (staged ? "" : " (" + error + ")"));
                check(staged && ReferenceEquals(new TickHostType(TypeId, TickStaging.Of(xml), xml).Clips, clips),
                    "sound staging: the host's type takes its staged clips");
                check(staged && TickStaging.Of(xml).Actions["Stand"].Variants[0].Poses[0].Volume == -6f,
                    "sound staging: a pose's volume is compiled from the document");
            }
            finally
            {
                if (animations != null) animations.Dispose();
                if (xml != null) xml.Dispose();
            }
        }

        // ---- the synthetic type: Sing (one pose naming "la" at -6 dB), Hush (a Mute of "la", then Rest)

        private static XmlData.RootNode Document()
        {
            var still = new XmlData.MovingNode { X = "0", Y = "0", Interval = "40" };
            Func<int, string, XmlData.AnimationNode> node = (id, name) => new XmlData.AnimationNode
            {
                Id = id, Name = name, Start = still, End = still,
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
            };
            Func<string, string, XmlData.ParamNode> p = (n, v) => new XmlData.ParamNode { Name = n, Value = v, Cache = "none" };
            Func<int, string, XmlData.PoseNode, XmlData.AnimationNode> stay = (id, name, pose) =>
            {
                XmlData.AnimationNode a = node(id, name);
                a.Act = new XmlData.ActNode
                {
                    Type = "Stay", Border = "Floor", Params = new[] { p("Duration", "500") },
                    Variants = new XmlData.VariantsNode { Variant = new[] { new XmlData.VariantNode { Pose = new[] { pose } } } },
                };
                return a;
            };
            Func<int, string, int, XmlData.AnimationNode> beh = (id, name, frequency) =>
            {
                XmlData.AnimationNode a = node(id, "beh:" + name);
                a.Behaviour = new XmlData.BehaviourNode { Name = name, Runs = name, Frequency = frequency };
                return a;
            };
            XmlData.AnimationNode route = node(1, "route");
            route.Route = new XmlData.RouteNode();
            route.Sequence.Next = new[] { new XmlData.NextNode { Value = 10, Probability = 1 }, new XmlData.NextNode { Value = 11, Probability = 0 } };
            XmlData.AnimationNode hush = node(22, "Hush");
            hush.Act = new XmlData.ActNode
            {
                Type = "Sequence",
                Children = new XmlData.ChildrenNode
                {
                    Mode = "sequence",
                    Child = new[] { new XmlData.CompositeChildNode { Animation = "MuteLa" }, new XmlData.CompositeChildNode { Animation = "Rest" } },
                },
            };
            XmlData.AnimationNode mute = node(23, "MuteLa");
            mute.Act = new XmlData.ActNode { Type = "Embedded", Class = "Mute", Params = new[] { p("Sound", "la") } };
            return new XmlData.RootNode
            {
                Header = new XmlData.HeaderNode { Title = "t", Features = "tick actions behaviours", Clock = "tick" },
                Image = new XmlData.ImageNode
                {
                    TilesX = 2, TilesY = 1, Png = PngHeader(128, 64),
                    AnchorX = 32, AnchorXSpecified = true, AnchorY = 64, AnchorYSpecified = true,
                },
                Animations = new XmlData.AnimationsNode
                {
                    Animation = new[]
                    {
                        route, beh(10, "Sing", 1), beh(11, "Hush", 0),
                        stay(20, "Sing", new XmlData.PoseNode { Frame = 0, Ticks = 1, Sound = "la", Volume = -6, VolumeSpecified = true }),
                        stay(21, "Rest", new XmlData.PoseNode { Frame = 1, Ticks = 1 }),
                        hush, mute,
                    },
                },
            };
        }

        /// <summary>The PNG signature and IHDR size fields: all TickType reads to size the sheet.</summary>
        private static string PngHeader(int width, int height)
        {
            byte[] head =
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52,
                (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
                (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height, 8, 6, 0, 0, 0,
            };
            return Convert.ToBase64String(head);
        }
    }
}
