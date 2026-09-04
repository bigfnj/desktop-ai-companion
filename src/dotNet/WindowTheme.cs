using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DesktopAICompanion
{
    /// <summary>
    /// A lightweight, system-following dark theme for the tray dialogs: an immersive dark title bar
    /// and dark control colors when Windows is in dark mode, and the classic light look otherwise.
    /// Best-effort within WinForms' limits -- track bars and some native glyphs stay system-drawn.
    /// </summary>
    internal static class WindowTheme
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref int value, int size);

        // Tells comctl32 to render a common control with the OS *dark* visual style. This is the only
        // way to darken the parts WinForms doesn't paint itself (combo dropdowns, tree/list scrollbars
        // and selection, edit borders); .NET Framework WinForms has no dark mode of its own.
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string appName, string idList);

        private static void DarkenNativeControl(Control c, string appName)
        {
            try { if (c.IsHandleCreated) SetWindowTheme(c.Handle, appName, null); }
            catch { }
        }

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1 = 19;

        // Dark palette (only applied when the OS is in dark mode).
        public static readonly Color Bg = Color.FromArgb(32, 32, 32);
        public static readonly Color Surface = Color.FromArgb(45, 45, 48);   // inputs / trees / lists
        public static readonly Color Text = Color.FromArgb(240, 240, 240);
        public static readonly Color Muted = Color.FromArgb(170, 170, 170);
        public static readonly Color Border = Color.FromArgb(70, 70, 74);

        private static readonly Color LightHintGray = Color.FromArgb(80, 80, 80);   // the light-mode hint color
        private static readonly Color StatusGreen = Color.FromArgb(0, 120, 0);

        /// <summary>True when Windows apps are set to the dark theme.</summary>
        public static bool IsDark()
        {
            try
            {
                object value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);
                return value is int i && i == 0;
            }
            catch { return false; }
        }

        /// <summary>Apply the immersive dark/light title bar to a form (safe on all Windows versions).</summary>
        public static void ApplyTitleBar(Form form)
        {
            if (form == null || form.IsDisposed || !form.IsHandleCreated) return;
            int dark = IsDark() ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(
                        form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(
                        form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1, ref dark, sizeof(int));
            }
            catch { }
        }

        /// <summary>Title bar plus, when the OS is dark, a recolor of the whole control tree.</summary>
        public static void Apply(Form form)
        {
            if (form == null) return;
            ApplyTitleBar(form);
            if (!IsDark()) return;
            form.BackColor = Bg;
            form.ForeColor = Text;
            ThemeTree(form);
        }

        private static void ThemeTree(Control root)
        {
            foreach (Control child in root.Controls)
            {
                ThemeControl(child);
                if (child.HasChildren) ThemeTree(child);
            }
        }

        private static void ThemeControl(Control c)
        {
            if (c is Label label)
            {
                Color f = label.ForeColor;
                if (f == LightHintGray) label.ForeColor = Muted;                     // hint text
                else if (f == StatusGreen || f == Color.Firebrick) { }               // semantic, readable on dark
                else label.ForeColor = Text;
                label.BackColor = Color.Transparent;
                return;
            }
            if (c is LinkLabel) { c.BackColor = Color.Transparent; return; }
            if (c is Button button)
            {
                // Accent buttons (already flat with a non-system colour) keep their look.
                if (button.FlatStyle == FlatStyle.Flat && button.BackColor != SystemColors.Control) return;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Border;
                button.BackColor = Surface;
                button.ForeColor = Text;
                button.UseVisualStyleBackColor = false;
                return;
            }
            if (c is CheckBox || c is RadioButton)
            {
                c.BackColor = Color.Transparent;
                c.ForeColor = Text;
                return;
            }
            if (c is ComboBox || c is TextBox)
            {
                c.BackColor = Surface;
                c.ForeColor = Text;
                DarkenNativeControl(c, "DarkMode_CFD");   // dark dropdown / edit border
                return;
            }
            if (c is NumericUpDown nud)
            {
                // The outer NumericUpDown ignores BackColor for its inner edit + spin buttons, so
                // theme those child controls directly (else they render as white boxes on dark).
                nud.BackColor = Surface;
                nud.ForeColor = Text;
                foreach (Control inner in nud.Controls)   // inner edit + spin buttons
                {
                    inner.BackColor = Surface;
                    inner.ForeColor = Text;
                }
                return;
            }
            if (c is ListBox || c is CheckedListBox)
            {
                c.BackColor = Surface;
                c.ForeColor = Text;
                DarkenNativeControl(c, "DarkMode_Explorer");   // dark scrollbar + selection
                return;
            }
            if (c is TreeView tree)
            {
                tree.BackColor = Surface;
                tree.ForeColor = Text;
                tree.LineColor = Border;
                DarkenNativeControl(tree, "DarkMode_Explorer");
                return;
            }
            if (c is PictureBox)
            {
                // A solid dark background (not Transparent) so the image renders reliably -- transparent
                // compositing inside a TableLayoutPanel cell was blanking the preview image entirely.
                c.BackColor = Bg;
                return;
            }
            if (c is Panel || c is FlowLayoutPanel)
            {
                c.BackColor = Bg;
                c.ForeColor = Text;
                DarkenNativeControl(c, "DarkMode_Explorer");   // dark auto-scroll scrollbars
                return;
            }
            if (c is TabControl || c is TabPage || c is TableLayoutPanel ||
                c is GroupBox || c is TrackBar)
            {
                c.BackColor = Bg;
                c.ForeColor = Text;
                return;
            }
            c.BackColor = Bg;
            c.ForeColor = Text;
        }
    }
}
