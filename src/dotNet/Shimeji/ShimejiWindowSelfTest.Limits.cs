using System;
using System.Drawing;
using System.Text;

namespace DesktopAICompanion.Shimeji
{
    internal static partial class ShimejiWindowSelfTest
    {
        /// <summary>
        /// feature/shimeji-host-limits: a tick-clock companion's 650 x 320 frame (past the classic 256 px cap, inside the
        /// tick cap) stages through the app's own path (StartUp.TryStageRuntime) at its own size, its window and the frame
        /// it shows are that size, and a press 640 px into the image reaches the hotspot there, which at any smaller staged
        /// size lies outside the window. Then a companion whose frames would pass the frame budget at the user's size
        /// stages at the largest size that fits instead of refusing to load. Sheets generated here; drawn at opacity 0.
        /// </summary>
        private static void CheckLargeFrames(Action<bool, string> check, StringBuilder log)
        {
            XmlData.RootNode classic;
            string error;
            if (!CompanionXmlValidator.TryParse(Properties.Resources.animations, out classic, out error))
            {
                check(false, "large frames: the bundled pet parses (" + error + ")");
                return;
            }
            // The staging notice (review N3) goes to a list here, and back to the log after.
            var notices = new System.Collections.Generic.List<string>();
            Action<string> previousSink = StartUp.TickSizeNoticeSink;
            StartUp.TickSizeNoticeSink = notices.Add;
            try
            {
                CheckLargeFrameStaging(check, log, classic, notices);
            }
            finally
            {
                StartUp.TickSizeNoticeSink = previousSink;
            }
        }

        private static void CheckLargeFrameStaging(Action<bool, string> check, StringBuilder log, XmlData.RootNode classic,
            System.Collections.Generic.List<string> notices)
        {
            string error;
            Xml xml = null;
            Animations animations = null;
            TickHost host = null;
            try
            {
                bool staged = StartUp.TryStageRuntime(SecuritySelfTest.Serialize(LargeFrameDocument(classic, 650, 320, 2, 1)), 1.0,
                    out xml, out animations, out error);
                TickType type = staged ? TickStaging.Of(xml) : null;
                check(type != null && xml.spriteWidth == 650 && xml.spriteHeight == 320 && type.CellWidth == 650 && type.CellHeight == 320,
                    "large frames: a 650 x 320 tick frame stages at its own size ("
                    + (staged ? xml.spriteWidth + "x" + xml.spriteHeight + ", cell " + (type == null ? "none" : type.CellWidth + "x" + type.CellHeight) : error) + ")");
                check(staged && notices.Count == 0,
                    "large frames: WITNESS a companion staged at the size asked for writes no size notice (" + notices.Count + ")");
                if (type == null) return;
                type.Name = "Large";
                host = new TickHost(t =>
                    {
                        var form = new FormCompanion(new Animations(t.Sprites), t.Sprites);
                        form.Show(t.Type.CellWidth, t.Type.CellHeight);
                        return form;
                    },
                    form => form.Close(),
                    message => log.AppendLine("LOG: " + message)) { RenderOpacity = 0.0 };
                host.Environment.SearchesWindows = false;
                host.AddType(new TickHostType("large", type, xml));
                Rectangle work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                TickMascot m = host.Spawn("Large", new TickPoint(work.Left + work.Width / 2, work.Bottom), false, "Stand");
                host.Step();
                FormCompanion f = host.FormOf(m);
                Image shown = f == null ? null : f.TickFrameForSelfTest;
                check(f != null && f.Width == 650 && f.Height == 320 && shown != null && shown.Width == 650 && shown.Height == 320,
                    "large frames: the window and the frame it shows are 650 x 320 (" + Describe(f) + ")");
                // A synthetic press at a screen point (no real pointer moves): 640 px in, 310 down.
                if (f != null) host.Pointer(f, TickInputKind.Press, new Point(f.Left + 640, f.Top + 310));
                host.Step();
                check(m.Behaviour != null && m.Behaviour.Name == "Petted",
                    "large frames: a press 640 px into the 650 px image reaches the hotspot there ("
                    + (m.Behaviour == null ? "no behaviour" : m.Behaviour.Name) + ")");
            }
            catch (Exception e)
            {
                check(false, "large frames: the large-frame checks ran without an exception: " + e);
            }
            finally
            {
                if (host != null) host.Dispose();
                if (animations != null) animations.Dispose();
                if (xml != null) xml.Dispose();
            }

            // 1024 cells of 64 px at the user's 400%: 256 px frames, 64 Mi pixels, four times the budget. The size is lowered
            // to the largest that fits (about 1.97) rather than the companion refused, and the tick type is scaled to match.
            xml = null;
            animations = null;
            try
            {
                bool staged = StartUp.TryStageRuntime(SecuritySelfTest.Serialize(LargeFrameDocument(classic, 64, 64, 32, 32)), 4.0,
                    out xml, out animations, out error);
                TickType type = staged ? TickStaging.Of(xml) : null;
                long pixels = staged ? (long)xml.SpriteCount * xml.spriteWidth * xml.spriteHeight : -1;
                check(type != null && xml.ScaleFactorD > 1.0 && xml.ScaleFactorD < 4.0 && pixels <= SpriteFrameStore.MaximumOriginalPixels
                      && type.Scale == xml.ScaleFactorD && type.CellWidth == xml.spriteWidth,
                    "large frames: a tick companion whose frames would pass the budget at the user's size stages at the largest size that fits ("
                    + (staged ? "scale " + xml.ScaleFactorD.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
                        + ", " + xml.spriteWidth + "x" + xml.spriteHeight + ", " + pixels + " pixels" : error) + ")");
                // Review N3: said once at staging, naming the companion, the size asked for and the size used.
                string name = staged ? xml.AnimationXML.Header.Petname : "";
                check(staged && notices.Count == 1 && notices[0].IndexOf("tick companion '" + name + "' is drawn at 197%, not the 400% asked for",
                        StringComparison.Ordinal) >= 0,
                    "large frames: a lowered size is said once at staging, naming the companion and both sizes ("
                    + notices.Count + ": " + string.Join(" | ", notices) + ")");
            }
            catch (Exception e)
            {
                check(false, "large frames: the frame-budget check ran without an exception: " + e);
            }
            finally
            {
                if (animations != null) animations.Dispose();
                if (xml != null) xml.Dispose();
            }

            // Review N2: 1024 cells of 128 x 127 (a 4096 x 4064 sheet, inside the 16 Mi pixel cap) at 150%. With a pixel of
            // rounding a side the budget fits only 0.996, below 1:1; the floor stages it at exactly 1:1, which the sheet's own
            // pixels prove fits.
            xml = null;
            animations = null;
            try
            {
                bool staged = StartUp.TryStageRuntime(SecuritySelfTest.Serialize(LargeFrameDocument(classic, 128, 127, 32, 32)), 1.5,
                    out xml, out animations, out error);
                check(staged && xml.ScaleFactorD == 1.0 && xml.spriteWidth == 128 && xml.spriteHeight == 127,
                    "large frames: a size the budget would put below 1:1 stages at exactly 1:1 ("
                    + (staged ? "scale " + xml.ScaleFactorD.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
                        + ", " + xml.spriteWidth + "x" + xml.spriteHeight : error) + ")");
            }
            catch (Exception e)
            {
                check(false, "large frames: the 1:1 floor check ran without an exception: " + e);
            }
            finally
            {
                if (animations != null) animations.Dispose();
                if (xml != null) xml.Dispose();
            }
        }

        /// <summary>
        /// The extension gate's tick fixture (route, Stand) on a generated sheet of tilesX x tilesY cells of the given size,
        /// with a hotspot over the image's far corner (600..650 x 280..320 when the cell is 650 x 320) entering Petted, a
        /// root behaviour the route never chooses.
        /// </summary>
        private static XmlData.RootNode LargeFrameDocument(XmlData.RootNode classic, int width, int height, int tilesX, int tilesY)
        {
            XmlData.RootNode r = SecuritySelfTest.TickFixture(classic);
            r.Header.Features += " " + CompanionFormatFeatures.Hotspots;
            r.Image.TilesX = tilesX;
            r.Image.TilesY = tilesY;
            r.Image.Png = SecuritySelfTest.SheetPng(width * tilesX, height * tilesY);
            var still = new XmlData.MovingNode { X = "0", Y = "0", Interval = "40" };
            var nodes = new System.Collections.Generic.List<XmlData.AnimationNode>(r.Animations.Animation)
            {
                new XmlData.AnimationNode
                {
                    Id = 4, Name = "beh:Petted", Start = still, End = still,
                    Behaviour = new XmlData.BehaviourNode { Name = "Petted", Runs = "PetAct", Frequency = 0 },
                    Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
                },
                new XmlData.AnimationNode
                {
                    Id = 5, Name = "PetAct", Start = still, End = still,
                    Sequence = new XmlData.SequenceNode { RepeatCount = "0" },
                    Act = new XmlData.ActNode
                    {
                        Type = "Stay",
                        Params = new[] { new XmlData.ParamNode { Name = "Duration", Value = "50", Cache = "none" } },
                        Variants = new XmlData.VariantsNode
                        {
                            Variant = new[] { new XmlData.VariantNode { Pose = new[] { new XmlData.PoseNode { Frame = 0, Ticks = 5 } } } },
                        },
                    },
                },
            };
            r.Animations.Animation = nodes.ToArray();
            var route = new System.Collections.Generic.List<XmlData.NextNode>(r.Animations.Animation[0].Sequence.Next)
                { new XmlData.NextNode { Value = 4, Probability = 0 } };
            r.Animations.Animation[0].Sequence.Next = route.ToArray();
            r.Animations.Animation[2].Act.Variants.Variant[0].Hotspots = new XmlData.HotspotsNode
            {
                Hotspot = new[]
                {
                    new XmlData.HotspotNode
                    {
                        Shape = "rect", Origin = Math.Max(0, width - 50) + "," + Math.Max(0, height - 40),
                        Size = Math.Min(50, width) + "," + Math.Min(40, height), Behaviour = "Petted",
                    },
                },
            };
            return r;
        }
    }
}
