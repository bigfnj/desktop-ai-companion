using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>What the auto-approve row can honestly claim about itself.</summary>
    internal enum ApproveState
    {
        /// <summary>The user has not asked for it. Red.</summary>
        Off,
        /// <summary>Asked for, but the editor is not reachable, so nothing will be pressed. Orange.</summary>
        CannotSee,
        /// <summary>On, and able to press. Green.</summary>
        Able,
    }

    /// <summary>
    /// The coloured dot beside the auto-approve row, drawn rather than typed.
    ///
    /// WHY NOT AN EMOJI. The first version used U+1F534 / U+1F7E1 / U+1F7E2 in the label text, and
    /// on this box they came out as a flat grey ring: a WinForms menu item renders its text in the
    /// menu font, which has the glyphs but not their colour layers, so every state looked
    /// identical and the one thing the row exists to communicate was the thing it could not say.
    /// TrayItem.IconPng takes raw PNG bytes, so the dot is drawn and handed over as an image.
    ///
    /// The three bitmaps are built ONCE and cached. Tray labels and icons are re-evaluated on
    /// every menu open -- the ABI says so in TrayItem.Visible's comment -- so allocating a Bitmap
    /// per open would put GDI handles on a path the user hits repeatedly, and handle growth is
    /// exactly what this repo's leak soak measures.
    /// </summary>
    internal static class StatusDot
    {
        private const int Size = 32;

        private static readonly object Gate = new object();
        private static byte[] _off;
        private static byte[] _cannotSee;
        private static byte[] _able;

        /// <summary>Red for off, orange for cannot-see, green for able.</summary>
        public static byte[] For(ApproveState state)
        {
            lock (Gate)
            {
                switch (state)
                {
                    case ApproveState.Able:
                        return _able ?? (_able = Draw(Color.FromArgb(60, 190, 90)));
                    case ApproveState.CannotSee:
                        return _cannotSee ?? (_cannotSee = Draw(Color.FromArgb(240, 150, 30)));
                    default:
                        return _off ?? (_off = Draw(Color.FromArgb(215, 65, 65)));
                }
            }
        }

        /// <summary>
        /// A filled circle with a darker rim.
        ///
        /// The rim is not decoration: a flat disc of any of these three colours disappears against
        /// a menu background of similar lightness, and the tray menu follows the system theme, so
        /// which background it lands on is not ours to choose.
        /// </summary>
        private static byte[] Draw(Color fill)
        {
            using (var bitmap = new Bitmap(Size, Size))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.Clear(Color.Transparent);
                    var box = new Rectangle(5, 5, Size - 11, Size - 11);
                    using (var brush = new SolidBrush(fill))
                        graphics.FillEllipse(brush, box);
                    using (var pen = new Pen(
                               System.Windows.Forms.ControlPaint.Dark(fill, 0.25f), 2f))
                        graphics.DrawEllipse(pen, box);
                }
                using (var buffer = new MemoryStream())
                {
                    bitmap.Save(buffer, System.Drawing.Imaging.ImageFormat.Png);
                    return buffer.ToArray();
                }
            }
        }
    }
}
