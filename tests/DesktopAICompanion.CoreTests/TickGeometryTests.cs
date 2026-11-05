using System;
using System.Collections.Generic;
using DesktopAICompanion.Shimeji;

namespace DesktopAICompanion
{
    /// <summary>
    /// Per-pose image geometry (grimoire/06 section 3): in the source every image has its own size and anchor
    /// (ImagePairLoader.java:37-38), and the bounds (Mascot.java:714-722), the off-screen drop-in
    /// (UserBehavior.java:252-255), a mirrored hotspot (Hotspot.java:31) and Dragged's and Regist's Origin offsets
    /// (Dragged.java:82-83, Regist.java:51) read the image the last pose set. The sheet's cells are 64x64 with the type's
    /// anchor at (32, 64); poses here are smaller than their cell, so reading the cell instead gives other numbers.
    /// </summary>
    internal static partial class Program
    {
        /// <summary>A one-tick pose with its own geometry; <paramref name="right"/> -1 mirrors the left frame, and a
        /// right size of 0 is left out (it defaults to the left image's).</summary>
        private static XmlData.PoseNode GPose(int frame, int ax, int ay, int w, int h, int right = -1, int rw = 0, int rh = 0)
        {
            var p = new XmlData.PoseNode
            {
                Frame = frame, Ticks = 1,
                AnchorX = ax, AnchorXSpecified = true, AnchorY = ay, AnchorYSpecified = true,
                Width = w, WidthSpecified = true, Height = h, HeightSpecified = true,
            };
            if (right >= 0) { p.Right = right; p.RightSpecified = true; }
            if (rw > 0) { p.RightWidth = rw; p.RightWidthSpecified = true; }
            if (rh > 0) { p.RightHeight = rh; p.RightHeightSpecified = true; }
            return p;
        }

        /// <summary>The PNG signature and IHDR size fields of a sheet of the given size (all TickType reads).</summary>
        private static string GeoSheetPng(int width, int height)
        {
            byte[] head =
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52,
                (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
                (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height, 8, 6, 0, 0, 0,
            };
            return Convert.ToBase64String(head);
        }

        private static string Rect(TickRect r) { return "(" + r.X + ", " + r.Y + ", " + r.Width + ", " + r.Height + ")"; }

        private static XmlData.AnimationNode Borderless(XmlData.AnimationNode act)
        {
            act.Act.Border = "";
            return act;
        }

        private static void TestTickGeometryBounds()
        {
            // "Small" shows a 30x40 left image anchored at (10, 40); "Pair" adds an explicit 50x20 right frame; "Mirror" has
            // a right frame with no size of its own (so 30x40); "Plain" gives no geometry (the cell and the type's anchor).
            TickType t = TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1), TEdge(11, 0), TEdge(12, 0), TEdge(13, 0)),
                TBeh(10, "Small", "Small", 1, null), TBeh(11, "Pair", "Pair", 0, null),
                TBeh(12, "Mirror", "Mirror", 0, null), TBeh(13, "Plain", "Plain", 0, null),
                TAct(30, "Small", "Stay", null, new[] { TParam("Duration", "50") }, GPose(0, 10, 40, 30, 40)),
                TAct(31, "Pair", "Stay", null, new[] { TParam("Duration", "50") }, GPose(0, 10, 40, 30, 40, 1, 50, 20)),
                TAct(32, "Mirror", "Stay", null, new[] { TParam("Duration", "50") }, GPose(0, 10, 40, 30, 40, 1)),
                TAct(33, "Plain", "Stay", null, new[] { TParam("Duration", "50") }, TPose(0, 1))));
            Func<string, bool, TickMascot> shown = (behaviour, right) =>
            {
                TickWorld w = TickWorldOf(new TickLog(), null, t);
                TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 680), right, behaviour);
                w.StepOnce();
                return m;
            };

            // Before its first pose a companion has no image: its anchor point is the type's, its bounds the window's
            // (never placed, so empty).
            TickWorld fresh = TickWorldOf(new TickLog(), null, t);
            TickMascot unposed = fresh.CreateAtStart("m1", t, new TickPoint(640, 680), true, "Small");
            AssertEqual(new TickPoint(32, 64), unposed.ImageCenter, "Before its first pose a companion's image anchor is not the type's.");
            AssertEqual("(0, 0, 0, 0)", Rect(unposed.Bounds), "Before its first pose a companion's bounds are not the window's (none yet).");

            // Facing left the image is (w, h) = 30x40 with its anchor at (10, 40): from the anchor (640, 680) the
            // bounds are (640 - 10, 680 - 40) = (630, 640), 30x40.
            TickMascot small = shown("Small", false);
            AssertEqual(new TickPoint(10, 40), small.ImageCenter, "A left-facing pose's image anchor is not its own (10, 40).");
            AssertEqual("(630, 640, 30, 40)", Rect(small.Bounds), "A left-facing pose's bounds are not its own image's.");
            // Facing right the left image is mirrored: its anchor x is measured from its right edge, 30 - 10 = 20, so the
            // bounds are (620, 640) and the anchor point stays at 640 on screen.
            TickMascot mirrored = shown("Small", true);
            AssertEqual(new TickPoint(20, 40), mirrored.ImageCenter, "A mirrored pose's anchor x is not w - ax = 30 - 10.");
            AssertEqual("(620, 640, 30, 40)", Rect(mirrored.Bounds), "A mirrored pose's bounds are not its own image's.");
            // An explicit right image of 50x20: anchor x 50 - 10 = 40 (ImagePairLoader.java:38), bounds (600, 640), 50x20.
            TickMascot pair = shown("Pair", true);
            AssertEqual(new TickPoint(40, 40), pair.ImageCenter, "An explicit right image's anchor x is not rw - ax = 50 - 10.");
            AssertEqual("(600, 640, 50, 20)", Rect(pair.Bounds), "An explicit right image's bounds are not its own size.");
            AssertEqual("(630, 640, 30, 40)", Rect(shown("Pair", false).Bounds), "Facing left, a pose with a right frame did not show its left image.");
            // A right frame with no size of its own is the left image's size: anchor x 30 - 10 = 20.
            TickMascot mirror = shown("Mirror", true);
            AssertEqual("(620, 640, 30, 40)", Rect(mirror.Bounds), "A right frame without rw and rh did not take the left image's size.");
            // No geometry: the whole cell and the type's anchor, mirrored as 64 - 32 = 32: (608, 616), 64x64.
            TickMascot plain = shown("Plain", true);
            AssertEqual(new TickPoint(32, 64), plain.ImageCenter, "A pose with no geometry did not take the type's anchor.");
            AssertEqual("(608, 616, 64, 64)", Rect(plain.Bounds), "A pose with no geometry did not take the whole cell.");
        }

        private static void TestTickGeometryScale()
        {
            // At half size (ImagePairLoader.java:37-38 and its scale(): round(v * 0.5), Java's halves up; .NET's default
            // rounding would take every .5 below to the even integer):
            //   ax 61 -> round(30.5) = 31, ay 117 -> round(58.5) = 59, w 45 -> round(22.5) = 23, h 61 -> round(30.5) = 31,
            //   rw 41 -> round(20.5) = 21, rh 25 -> round(12.5) = 13; facing right the anchor x is 21 - 31 = -10.
            // A pose with no geometry takes the type's anchor (32, 64) -> (16, 32) and the whole scaled cell, 32x32.
            XmlData.AnimationNode walk = TAct(30, "Walk", "Stay", null, new[] { TParam("Duration", "50") },
                GPose(0, 61, 117, 45, 61, 1, 41, 25), TPose(1, 1), GPose(2, 0, 0, 1, 1));
            Func<XmlData.RootNode> doc = () => TickDoc(TRoute(TEdge(10, 1)), TBeh(10, "Walk", "Walk", 1, null), XmlData.NodeCopy.Deep(walk));
            TickType half = TickType.Compile("Alpha", doc());
            half.ApplyScale(0.5, 32, 32);
            TickPoseDef[] p = half.Actions["Walk"].Variants[0].Poses;
            AssertEqual("31,59,23,31,21,13", p[0].AnchorX + "," + p[0].AnchorY + "," + p[0].Width + "," + p[0].Height + "," + p[0].RightWidth + "," + p[0].RightHeight,
                "A pose's anchor and sizes did not scale as round(v * 0.5) with halves up.");
            AssertEqual(new TickPoint(-10, 59), p[0].ImageCenter(true), "A scaled right image's anchor x is not round(rw * s) - round(ax * s) = 21 - 31.");
            AssertEqual("16,32,32,32", p[1].AnchorX + "," + p[1].AnchorY + "," + p[1].Width + "," + p[1].Height,
                "A pose with no geometry did not scale to the type's scaled anchor and the scaled cell.");
            // w = h = 1 at a quarter: round(0.25) = 0, and an image keeps one pixel.
            TickType quarter = TickType.Compile("Alpha", doc());
            quarter.ApplyScale(0.25, 16, 16);
            TickPoseDef q = quarter.Actions["Walk"].Variants[0].Poses[2];
            AssertTrue(q.Width == 1 && q.Height == 1, "A one-pixel image scaled to nothing instead of keeping one pixel (" + q.Width + "x" + q.Height + ").");

            // A 65-pixel cell at half size: the host's sprite store rounds 32.5 to 32 (to even), so a pose filling the
            // cell must be the store's 32, not round(32.5) = 33, which is wider than the frame it is cut from. A 63-wide
            // pose is round(31.5) = 32.
            XmlData.RootNode odd = TickDoc(TRoute(TEdge(10, 1)), TBeh(10, "Walk", "Walk", 1, null),
                TAct(30, "Walk", "Stay", null, new[] { TParam("Duration", "50") }, TPose(0, 1), GPose(1, 0, 0, 63, 65)));
            odd.Image.Png = GeoSheetPng(260, 65);
            TickType oddType = TickType.Compile("Alpha", odd);
            oddType.ApplyScale(0.5, 32, 32);
            TickPoseDef[] o = oddType.Actions["Walk"].Variants[0].Poses;
            AssertEqual("32,32,32,32,32,32", o[0].Width + "," + o[0].Height + "," + o[0].RightWidth + "," + o[0].RightHeight + "," + o[1].Width + "," + o[1].Height,
                "A pose filling a 65-pixel cell at half size is not the sprite store's 32-pixel frame (or 63 did not round to 32).");
        }

        private static TickType OffScreenType()
        {
            return TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1), TEdge(11, 0)),
                TBeh(10, "Stand", "Stand", 1, null),
                TBeh(11, "Fall", "Stand", 0, null),
                Borderless(TAct(30, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, GPose(0, 10, 40, 30, 40)))));
        }

        private static void TestTickGeometryOffScreen()
        {
            // UserBehavior.java:252-255 on the screen (0, 0)-(1280, 720), after the step's pose: the image is wholly off
            // when its right edge is <= 0, its left edge >= 1280 or its top >= 720. The image is 30x40 anchored at (10, 40)
            // (mirrored: x 20); the cell would be 64x64 anchored at (32, 64) and still on screen in every case below.
            TickType t = OffScreenType();
            Func<int, int, bool, bool> drops = (x, y, right) =>
            {
                var log = new TickLog();
                TickWorld w = TickWorldOf(log, null, t);
                w.CreateAtStart("m1", t, new TickPoint(x, y), right, "Stand");
                w.StepOnce();
                return log.Events.Contains("m1 behaviour Stand>Fall forced") && log.Draws.Contains("builtin:out-of-bounds-drop-x");
            };
            // Anchor x -20: the image spans -30..0, wholly left (the cell would span -52..12).
            AssertTrue(drops(-20, 300, false), "An image whose right edge is the screen's left was not dropped in (the cell's was read?).");
            // WITNESS: one pixel further right the image (-29..1) is on the screen.
            AssertFalse(drops(-19, 300, false), "An image one pixel on the screen was dropped in.");
            // Anchor y 760: the image's top is 760 - 40 = 720, the screen's bottom (the cell's would be 696).
            AssertTrue(drops(640, 760, false), "An image whose top is the screen's bottom was not dropped in.");
            // Facing right at x 1300: the mirrored anchor x is 20, so the image starts at 1280 (the cell would at 1268).
            AssertTrue(drops(1300, 300, true), "A right-facing image starting at the screen's right was not dropped in.");
            AssertFalse(drops(1299, 300, true), "A right-facing image one pixel on the screen was dropped in.");
        }

        private static void TestTickGeometryHotspot()
        {
            // Hotspot.java:31 mirrors a right-facing point as bounds.width - x, the IMAGE's width. Facing right at (640, 680)
            // the 30x40 image (anchor x 30 - 10 = 20) is at (620, 640); a press at (645, 650) is window point (25, 10),
            // mirrored to (30 - 25, 10) = (5, 10), inside the hotspot (0, 0)-(20, 20). Mirrored by the cell's width it would
            // be (64 - 25) = 39, outside. The held pass of the same step tests the held point the same way and enters again.
            XmlData.AnimationNode stand = TAct(30, "Stand", "Stay", null, new[] { TParam("Duration", "500") }, GPose(0, 10, 40, 30, 40));
            stand.Act.Variants.Variant[0].Hotspots = new XmlData.HotspotsNode { Hotspot = new[] { THotspot("Petted") } };
            TickType t = TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1), TEdge(11, 0), TEdge(12, 0), TEdge(13, 0)),
                TBeh(10, "Stand", "Stand", 1, null),
                TBeh(11, "Dragged", "Hold", 0, null),
                TBeh(12, "Thrown", "Hold", 0, null),
                TBeh(13, "Petted", "Stand", 0, null),
                stand,
                TAct(31, "Hold", "Stay", null, new[] { TParam("Duration", "500") }, TPose(1, 1))));
            var log = new TickLog();
            TickWorld w = TInputWorld(log, t, true, 2, TickInputKind.Press, 645, 650);
            TickMascot m = w.Pending[0];
            w.StepOnce();
            AssertEqual("(620, 640, 30, 40)", Rect(m.Bounds), "The right-facing companion is not where this test assumes.");
            w.StepOnce();
            AssertTrue(log.Events.Contains("m1 behaviour Stand>Petted forced"),
                "A press mirrored across the image's width did not land in the hotspot: " + string.Join("; ", log.Events));
            AssertTrue(m.IsHotspotClicked && TCount(log, "m1 behaviour Petted>Petted forced") == 1,
                "The held pass did not mirror the held point across the image's width (the hold ended or did not re-enter).");
        }

        private static void TestTickGeometryDragged()
        {
            // Dragged.java:72-83 with OffsetX 10, OffsetY 120 (default), OffsetType Origin: offset = (-10 + centre.x,
            // -120 + centre.y), centre being the image the LAST pose set, as it was set (getImage(); Dragged turns the
            // companion left first, which does not change the image). Step 1: Settle shows its explicit right image, 50
            // wide, anchor (50 - 10, 40) = (40, 40). Step 2, Pinch's first tick: offset (30, -80), so the cursor (500, 400)
            // puts the anchor at (530, 320); then Pinch's own pose shows facing left, anchor (5, 30). Step 3: offset
            // (-5, -90), anchor (495, 310). From the cell it would be (32, 64) both times: (522, 344).
            TickType t = MotionType("Lift", MSeq(30, "Lift", false, "Settle", "Pinch"),
                MAct(34, "Settle", "Stay", null, "", new[] { TParam("Duration", "1") }, MVar(null, false, GPose(0, 10, 40, 30, 40, 1, 50, 20))),
                MAct(35, "Pinch", "Embedded", "Dragged", "", new[] { TParam("OffsetX", "10"), TParam("OffsetType", "Origin") },
                    MVar(null, false, GPose(0, 5, 30, 20, 30))));
            var desk = new MotionDesk { CursorAt = new TickPoint(500, 400) };
            TickWorld w = MotionWorld(desk, null, null, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(510, 520), true, "Lift");
            w.StepOnce();
            w.StepOnce();
            AssertEqual(new TickPoint(530, 320), m.Anchor, "Dragged's first Origin offset was not measured from the right image the last pose set.");
            w.StepOnce();
            AssertEqual(new TickPoint(495, 310), m.Anchor, "Dragged's next Origin offset was not measured from its own pose's anchor.");
        }

        private static void TestTickGeometryRegist()
        {
            // Regist.java:45-55 with OffsetType Origin, OffsetX 0: offset x = centre.x of the last pose's image, and it runs
            // while |cursor.x - anchor.x + offset| < 5. Step 1: Settle shows (10, 40). Step 2: the sequence reaches Resist,
            // whose first check reads Settle's image, |500 - 510 + 10| = 0 < 5, so it runs (dragging, frame 1); the step's
            // second check reads Resist's own pose, anchored at x 10 too. From the cell's 32 it would be
            // |500 - 510 + 32| = 22, and Resist could not start.
            TickType t = MotionType("Lift", MSeq(30, "Lift", false, "Settle", "Resist"),
                MAct(34, "Settle", "Stay", null, "", new[] { TParam("Duration", "1") }, MVar(null, false, GPose(0, 10, 40, 30, 40))),
                MAct(35, "Resist", "Embedded", "Regist", "", new[] { TParam("OffsetType", "Origin") },
                    MVar(null, false, GPose(1, 10, 40, 30, 40), GPose(1, 10, 40, 30, 40), GPose(1, 10, 40, 30, 40), GPose(1, 10, 40, 30, 40))));
            var desk = new MotionDesk { CursorAt = new TickPoint(500, 400) };
            TickWorld w = MotionWorld(desk, null, null, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(510, 520), false, "Lift");
            w.StepOnce();
            w.StepOnce();
            AssertTrue(m.Dragging && m.Pose.Frame == 1 && m.Behaviour.Name == "Lift",
                "Regist's Origin offset was not the last pose's anchor x (it did not start).");
        }
    }
}
