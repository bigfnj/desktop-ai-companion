using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace DesktopAICompanion.Shimeji
{
    internal static partial class ShimejiWindowSelfTest
    {
        /// <summary>
        /// grimoire/06 section 3, feature 'pages': a tick sheet on two pages of different cells (650 x 320, then 40 x 50)
        /// stages through the app's own path (StartUp.TryStageRuntime). Its frames number on across the pages, each the
        /// size of its page's cell with that cell's own pixels; a pose on the second page shows its whole frame facing
        /// either way (mirrored from x = 0 of its own cell, not of the first page's), so no region is cut; and at a size
        /// above 1:1 each pose's size is its own frame's staged size. Then the validator's paged rules: the token, the
        /// tick clock, a pose size other than its cell, and the paged pixel budget, each refused by name. Sheets are
        /// generated here; nothing is drawn.
        /// </summary>
        private static void CheckPagedSheet(Action<bool, string> check, StringBuilder log)
        {
            XmlData.RootNode classic;
            string error;
            if (!CompanionXmlValidator.TryParse(Properties.Resources.animations, out classic, out error))
            {
                check(false, "paged sheet: the bundled pet parses (" + error + ")");
                return;
            }
            Xml xml = null;
            Animations animations = null;
            try
            {
                bool staged = StartUp.TryStageRuntime(SecuritySelfTest.Serialize(PagedDocument(classic, null)), 1.0,
                    out xml, out animations, out error);
                TickType type = staged ? TickStaging.Of(xml) : null;
                check(type != null && xml.SpriteCount == 7 && xml.SpriteFrameSize(1) == new Size(100, 100)
                      && xml.SpriteFrameSize(2) == new Size(40, 50) && xml.SpriteFrameSize(4) == new Size(40, 50)
                      && xml.SpriteFrameSize(5) == new Size(650, 320) && xml.SpriteFrameSize(6) == new Size(200, 150),
                    "paged sheet: four pages stage as seven frames, each its page's cell ("
                    + (staged ? xml.SpriteCount + " frames, frame 6 " + xml.SpriteFrameSize(6) : error) + ")");
                if (type == null) return;
                check(type.FrameCells != null && type.FrameCells.Length == 7 && type.FrameCells[3] == new Size(40, 50),
                    "paged sheet: the tick type knows each frame's cell");
                // The live graph lives as long as the pet: once the pages are staged it keeps neither their base64 nor the
                // validator's proven bytes (the compiler re-parses AnimationXMLString into a graph of its own).
                XmlData.PageNode[] livePages = xml.AnimationXML.Image.Pages;
                check(livePages != null && livePages.Length > 0
                      && Array.TrueForAll(livePages, p => p != null && string.IsNullOrEmpty(p.Png) && p.ValidatedPng == null)
                      && xml.SpriteCount == 7,
                    "paged sheet: once staged, the live graph keeps no page's base64 or proven bytes, and the frames stay staged");
                TickPoseDef small = type.Actions["Stand"].Variants[0].Poses[0];
                TickPoseDef middle = type.Actions["Stand"].Variants[0].Poses[1];
                // Frame 6's cell is wider than the first page's and narrower than the largest: its pose keeps its own size.
                check(middle.Width == 200 && middle.Height == 150,
                    "paged sheet: a pose whose cell is neither the first page's nor the largest keeps its own size ("
                    + middle.Width + "x" + middle.Height + ")");
                int index;
                bool flipped;
                bool exactLeft, exactRight;
                Rectangle left = TickPoseImage.Region(small, false, cell => xml.SpriteFrameSize(cell).Width, out index, out flipped);
                Bitmap shownLeft = xml.GetSpriteRegion(index, flipped, left, out exactLeft);
                Rectangle right = TickPoseImage.Region(small, true, cell => xml.SpriteFrameSize(cell).Width, out index, out flipped);
                Bitmap shownRight = xml.GetSpriteRegion(index, flipped, right, out exactRight);
                check(index == 3 && left == new Rectangle(0, 0, 40, 50) && right == new Rectangle(0, 0, 40, 50)
                      && exactLeft && exactRight && shownLeft.Size == new Size(40, 50) && shownRight.Size == new Size(40, 50)
                      && xml.SpriteRegionCount == 0,
                    "paged sheet: a pose on the second page shows its whole 40 x 50 frame facing either way, with no region cut ("
                    + left + ", " + right + ", regions " + xml.SpriteRegionCount + ")");
                // The second page's cell 1 is frame 3: its own colour, and its top-left mark, unmoved.
                check(shownLeft.GetPixel(5, 5).ToArgb() == PageColour(1, 1).ToArgb() && shownLeft.GetPixel(0, 0).ToArgb() == Color.White.ToArgb()
                      && shownRight.GetPixel(39, 0).ToArgb() == Color.White.ToArgb(),
                    "paged sheet: frame 3 is the second page's second cell, pixel for pixel, mirrored about its own width ("
                    + shownLeft.GetPixel(5, 5) + ")");

                // The host's own draw path (TickPoseImage.Frame) facing right: the window shows the mirrored 40 x 50
                // frame, its mark at the top-right, and is that size. Drawn at opacity 0; a synthetic step, no input.
                type.Name = "Paged";
                using (var host = new TickHost(t =>
                    {
                        var form = new FormCompanion(new Animations(t.Sprites), t.Sprites);
                        form.OpenHiddenForTick(t.Type.CellWidth, t.Type.CellHeight);
                        return form;
                    },
                    form => form.Close(),
                    message => log.AppendLine("LOG: " + message)) { RenderOpacity = 0.0 })
                {
                    host.Environment.SearchesWindows = false;
                    host.AddType(new TickHostType("paged", type, xml));
                    Rectangle work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                    TickMascot m = host.Spawn("Paged", new TickPoint(work.Left + work.Width / 2, work.Bottom), true, "Stand");
                    host.Step();
                    FormCompanion f = host.FormOf(m);
                    var shown = f == null ? null : f.TickFrameForSelfTest as Bitmap;
                    check(m.Pose == small && f != null && f.Width == 40 && f.Height == 50 && shown != null && shown.Width == 40
                          && shown.Height == 50 && shown.GetPixel(39, 0).ToArgb() == Color.White.ToArgb(),
                        "paged sheet: the host draws a right-facing pose on the second page as its own mirrored 40 x 50 frame ("
                        + Describe(f) + ")");
                }
            }
            catch (Exception e)
            {
                check(false, "paged sheet: the staging checks ran without an exception: " + e);
            }
            finally
            {
                if (animations != null) animations.Dispose();
                if (xml != null) xml.Dispose();
                xml = null;
                animations = null;
            }

            // Above 1:1 (the user's 200%, which the 768 px frame cap holds to 768 / 650): each pose is its own frame's
            // staged size, the second page's as much as the first's.
            try
            {
                bool staged = StartUp.TryStageRuntime(SecuritySelfTest.Serialize(PagedDocument(classic, null)), 2.0,
                    out xml, out animations, out error);
                TickType type = staged ? TickStaging.Of(xml) : null;
                TickPoseDef small = type == null ? null : type.Actions["Stand"].Variants[0].Poses[0];
                TickPoseDef middle = type == null ? null : type.Actions["Stand"].Variants[0].Poses[1];
                check(type != null && xml.ScaleFactorD > 1.0 && small.Width == xml.SpriteFrameSize(3).Width && small.Height == xml.SpriteFrameSize(3).Height
                      && middle.Width == xml.SpriteFrameSize(6).Width && middle.Height == xml.SpriteFrameSize(6).Height && xml.SpriteFrameSize(3).Width > 40,
                    "paged sheet: above 1:1 a pose's size is its own frame's staged size ("
                    + (type == null ? error : small.Width + "x" + small.Height + " on " + xml.SpriteFrameSize(3)) + ")");
            }
            catch (Exception e)
            {
                check(false, "paged sheet: the scaled staging check ran without an exception: " + e);
            }
            finally
            {
                if (animations != null) animations.Dispose();
                if (xml != null) xml.Dispose();
            }

            XmlData.RootNode ignored;
            check(!CompanionXmlValidator.TryParse(SecuritySelfTest.Serialize(PagedDocument(classic, r => r.Header.Features =
                    r.Header.Features.Replace(" " + CompanionFormatFeatures.Pages, ""))), out ignored, out error)
                  && error != null && error.Contains("'" + CompanionFormatFeatures.Pages + "'"),
                "paged sheet: pages without the 'pages' token are refused by that name (" + error + ")");
            check(!CompanionXmlValidator.TryParse(SecuritySelfTest.Serialize(PagedDocument(classic, r =>
                    r.Animations.Animation[2].Act.Variants.Variant[0].Pose[0].Width = 39)), out ignored, out error)
                  && error != null && error.Contains("whose image size (w, h) is not its frame's cell 40 x 50"),
                "paged sheet: a pose narrower than its paged cell is refused (" + error + ")");
            // A right-only pose (frame -1, right 3: hidden facing left, frame 3 facing right) is drawn from its right frame,
            // so on a paged sheet its rw and rh must be that frame's whole cell, as a two-image pose's are (Codex review of
            // 61b8405d): accepted with them, refused with them missing, partial or wrong.
            check(CompanionXmlValidator.TryParse(SecuritySelfTest.Serialize(PagedDocument(classic, r => RightOnly(r, 40, 50, true, true))),
                    out ignored, out error),
                "paged sheet: a right-only pose whose rw, rh are its right frame's cell is accepted (" + error + ")");
            string missing, partial, wrong;
            bool refusedMissing = !CompanionXmlValidator.TryParse(SecuritySelfTest.Serialize(PagedDocument(classic, r => RightOnly(r, 40, 50, false, false))),
                out ignored, out missing);
            bool refusedPartial = !CompanionXmlValidator.TryParse(SecuritySelfTest.Serialize(PagedDocument(classic, r => RightOnly(r, 40, 50, true, false))),
                out ignored, out partial);
            bool refusedWrong = !CompanionXmlValidator.TryParse(SecuritySelfTest.Serialize(PagedDocument(classic, r => RightOnly(r, 39, 50, true, true))),
                out ignored, out wrong);
            const string rightCell = "whose right image size (rw, rh) is not its right frame's cell 40 x 50";
            check(refusedMissing && refusedPartial && refusedWrong && missing.Contains(rightCell) && partial.Contains(rightCell) && wrong.Contains(rightCell),
                "paged sheet: a right-only pose whose rw, rh are missing, partial or not its right frame's cell is refused ("
                + missing + " | " + partial + " | " + wrong + ")");
            // The bundled classic pet, declaring 'pages' and carrying one: refused for the clock (every extension token needs
            // it), not run on one sheet.
            XmlData.RootNode classicPaged = XmlData.NodeCopy.Deep(classic);
            classicPaged.Header.Features = CompanionFormatFeatures.Pages;
            classicPaged.Image.Pages = new[] { new XmlData.PageNode { TilesX = 1, TilesY = 1, Png = SecuritySelfTest.SheetPng(40, 40) } };
            check(!CompanionXmlValidator.TryParse(SecuritySelfTest.Serialize(classicPaged), out ignored, out error)
                  && error != null && error.Contains("Every Shimeji runtime extension runs on the tick clock"),
                "paged sheet: pages on a classic companion are refused (" + error + ")");
            // 4096 x 4096 and 4096 x 2048 pages are 24 Mi pixels exactly, which fits; one more row of cells (2304) does not.
            check(CompanionXmlValidator.TryParse(SecuritySelfTest.Serialize(BudgetDocument(classic, 2048)), out ignored, out error),
                "paged sheet: pages of exactly the paged budget (24 Mi pixels) are accepted (" + error + ")");
            // And the sprite store holds them: 384 frames, 24 Mi pixels, past the one-sheet store's 16 Mi.
            try
            {
                bool staged = StartUp.TryStageRuntime(SecuritySelfTest.Serialize(BudgetDocument(classic, 2048)), 1.0,
                    out xml, out animations, out error);
                check(staged && xml.SpriteCount == 384 && xml.ScaleFactorD == 1.0,
                    "paged sheet: pages of the paged budget stage at 1:1, past the one-sheet frame budget ("
                    + (staged ? xml.SpriteCount + " frames" : error) + ")");
            }
            finally
            {
                if (animations != null) animations.Dispose();
                if (xml != null) xml.Dispose();
                animations = null;
                xml = null;
            }
            check(!CompanionXmlValidator.TryParse(SecuritySelfTest.Serialize(BudgetDocument(classic, 2304)), out ignored, out error)
                  && error != null && error.Contains("Sheet pages together exceed " + CompanionXmlValidator.MaximumPagedPixels + " pixels"),
                "paged sheet: pages past the paged budget are refused (" + error + ")");
        }

        /// <summary>The tick fixture on four pages: 2 x 1 cells of 100 x 100 (frames 0, 1), 3 x 1 of 40 x 50 (frames 2 to
        /// 4), 1 x 1 of 650 x 320 (frame 5) and 1 x 1 of 200 x 150 (frame 6), every cell its own colour with a white
        /// top-left pixel. Stand's poses show frame 3 and frame 6, each with its cell's size, as a paged document must;
        /// frame 6's cell is larger than the first page's and smaller than the largest.</summary>
        private static XmlData.RootNode PagedDocument(XmlData.RootNode classic, Action<XmlData.RootNode> change)
        {
            XmlData.RootNode r = SecuritySelfTest.TickFixture(classic);
            r.Header.Features += " " + CompanionFormatFeatures.Pages;
            r.Image.TilesX = 2;
            r.Image.TilesY = 1;
            r.Image.Png = PagePng(0, 100, 100, 2);
            r.Image.Pages = new[]
            {
                new XmlData.PageNode { TilesX = 3, TilesY = 1, Png = PagePng(1, 40, 50, 3) },
                new XmlData.PageNode { TilesX = 1, TilesY = 1, Png = PagePng(2, 650, 320, 1) },
                new XmlData.PageNode { TilesX = 1, TilesY = 1, Png = PagePng(3, 200, 150, 1) },
            };
            XmlData.PoseNode[] poses = r.Animations.Animation[2].Act.Variants.Variant[0].Pose;
            poses[0].Frame = 3;
            poses[0].Width = 40; poses[0].WidthSpecified = true;
            poses[0].Height = 50; poses[0].HeightSpecified = true;
            poses[0].AnchorX = 20; poses[0].AnchorXSpecified = true;
            poses[0].AnchorY = 50; poses[0].AnchorYSpecified = true;
            poses[1].Frame = 6;
            poses[1].Width = 200; poses[1].WidthSpecified = true;
            poses[1].Height = 150; poses[1].HeightSpecified = true;
            if (change != null) change(r);
            return r;
        }

        /// <summary>A paged document whose pages are a 4096 x 4096 sheet of 16 x 16 cells of 256 x 256 and a 4096 x
        /// <paramref name="height"/> one of the same cells, 16 a row.</summary>
        private static XmlData.RootNode BudgetDocument(XmlData.RootNode classic, int height)
        {
            XmlData.RootNode r = PagedDocument(classic, null);
            r.Image.TilesX = 16;
            r.Image.TilesY = 16;
            r.Image.Png = SecuritySelfTest.SheetPng(4096, 4096);
            r.Image.Pages = new[] { new XmlData.PageNode { TilesX = 16, TilesY = height / 256, Png = SecuritySelfTest.SheetPng(4096, height) } };
            XmlData.PoseNode[] poses = r.Animations.Animation[2].Act.Variants.Variant[0].Pose;
            poses[0].Frame = 256;
            poses[0].Width = 256; poses[0].Height = 256;
            poses[1].Frame = 0;
            poses[1].Width = 256; poses[1].Height = 256;
            return r;
        }

        /// <summary>Stand's first pose made right-only: no left image (frame -1, no w, h), right frame 3 (a 40 x 50 cell),
        /// with rw and rh given as asked.</summary>
        private static void RightOnly(XmlData.RootNode r, int width, int height, bool giveWidth, bool giveHeight)
        {
            XmlData.PoseNode pose = r.Animations.Animation[2].Act.Variants.Variant[0].Pose[0];
            pose.Frame = -1;
            pose.WidthSpecified = false;
            pose.HeightSpecified = false;
            pose.Right = 3;
            pose.RightSpecified = true;
            pose.RightWidth = width;
            pose.RightWidthSpecified = giveWidth;
            pose.RightHeight = height;
            pose.RightHeightSpecified = giveHeight;
        }

        private static Color PageColour(int page, int cell)
        {
            return Color.FromArgb(255, 40 + 60 * page, 60 + 50 * cell, 200 - 30 * cell);
        }

        private static string PagePng(int page, int width, int height, int cells)
        {
            using (var sheet = new Bitmap(width * cells, height, PixelFormat.Format32bppArgb))
            using (var ms = new MemoryStream())
            {
                using (Graphics g = Graphics.FromImage(sheet))
                    for (int c = 0; c < cells; c++)
                        using (var brush = new SolidBrush(PageColour(page, c))) g.FillRectangle(brush, c * width, 0, width, height);
                for (int c = 0; c < cells; c++) sheet.SetPixel(c * width, 0, Color.White);
                sheet.Save(ms, ImageFormat.Png);
                return Convert.ToBase64String(ms.ToArray());
            }
        }
    }
}
