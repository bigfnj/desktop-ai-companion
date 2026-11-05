using System;
using System.Drawing;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// What a tick companion's window shows (grimoire/06 section 3, per-pose geometry): the pose image's region of its
    /// cell, never the whole cell, with the window sized to it, so the window is the source's image rectangle
    /// (Mascot.getBounds) and a facing change does not move the image's anchor on screen.
    /// </summary>
    internal static class TickPoseImage
    {
        /// <summary>
        /// The frame and the region of it the companion's last pose shows (ImagePairs: facing right, the explicit right
        /// frame, else the left frame mirrored). Each frame is drawn at its cell's TOP-LEFT, so the left image is the
        /// cell's top-left (w, h) and an explicit right image its frame's top-left (rw, rh); mirroring the whole cell
        /// carries the left image to the cell's top-RIGHT, at x = cellWidth - w.
        /// </summary>
        public static Rectangle Region(TickPoseDef pose, bool right, int cellWidth, out int index, out bool flipped)
        {
            if (right && pose.Right >= 0)
            {
                index = pose.Right;
                flipped = false;
                return new Rectangle(0, 0, pose.RightWidth, pose.RightHeight);
            }
            index = pose.Frame;
            flipped = right;
            return new Rectangle(right ? cellWidth - pose.Width : 0, 0, pose.Width, pose.Height);
        }

        /// <summary>
        /// The bitmap to draw and where the window goes (<paramref name="window"/>): the pose image's region, cut once
        /// by the sprite store, at the companion's bounds. When the store's region budget is spent it hands back the
        /// whole frame, which is drawn so the region still lands at the bounds (the rest of the cell is the sheet's
        /// padding); that is said once in <paramref name="log"/>.
        /// </summary>
        public static Bitmap Frame(TickMascot m, Xml sprites, Action<string> log, out TickRect window)
        {
            int index;
            bool flipped;
            Rectangle region = Region(m.Pose, m.PoseRight, sprites.spriteWidth, out index, out flipped);
            bool exact;
            Bitmap frame = sprites.GetSpriteRegion(index, flipped, region, out exact);
            TickRect b = m.Bounds;
            if (exact)
            {
                window = b;
                return frame;
            }
            if (sprites.TakeSpriteRegionBudgetNotice() && log != null)
                log("tick companion " + m.Sid + " (" + (m.PoseType ?? m.Type).Name + "): the sprite region budget is spent; "
                    + "poses past it draw their whole cell");
            window = new TickRect(b.X - region.X, b.Y - region.Y, frame.Width, frame.Height);
            return frame;
        }
    }
}
