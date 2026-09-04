using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.PetStudioModule
{
    /// <summary>
    /// A light/dark theme for the Companion Studio window that matches the host's WPF settings window. The module
    /// cannot reference the host's internal WpfTheme, so this mirrors its palette (#202020 / #2D2D30 / #F0F0F0
    /// / #46464A) and its approach: paint the window and install implicit control styles in dark mode, and set
    /// the immersive dark title bar once the window has a handle. Light mode keeps the stock WPF look, exactly
    /// as the host does.
    ///
    /// The dark/light decision comes from <see cref="IHost.IsDarkTheme"/> (host 1.4.7+), NOT from the OS. It
    /// has to: the user's choice is light / dark / SYSTEM, and only the host knows which of those is set, so
    /// reading the OS directly is right for "system" and wrong the moment someone pins the opposite. Re-read
    /// per window rather than cached, because a preference change takes effect on the next open.
    /// </summary>
    internal sealed class PetStudioTheme
    {
        internal bool Dark;

        internal Brush WindowBg, Text, Muted, Surface, Border, PreviewBg;
        internal Brush RootFill, RootStroke, LiveFill, LiveStroke, DeadFill, DeadStroke, ChipText;
        /// <summary>The third state the behaviour timeline needs, between "the companion does this by itself"
        /// (Live/green) and "we are inventing this" (Dead/red): natural, but only on contact. Two colours
        /// would have to lump a border edge in with one of them, and a jump's landing IS a border edge, so
        /// the distinction is the whole point rather than a nicety.</summary>
        internal Brush HintFill, HintStroke;

        /// <summary>The theme the host is currently presenting. A null host (or a host that cannot answer)
        /// falls back to light — the same direction the host's own resolver fails in, since a
        /// wrong-but-readable window beats a throw.</summary>
        internal static PetStudioTheme Current(IHost host)
        {
            return EffectiveDark(host) ? BuildDark() : BuildLight();
        }

        private static bool EffectiveDark(IHost host)
        {
            if (host == null) return false;
            try { return host.IsDarkTheme; }
            catch { return false; }
        }

        private static Brush B(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static PetStudioTheme BuildLight()
        {
            return new PetStudioTheme
            {
                Dark = false,
                WindowBg = null,                 // keep the stock WPF light look
                Text = B(0x11, 0x11, 0x11),
                Muted = B(0x80, 0x80, 0x80),
                Surface = null,
                Border = Brushes.Gray,
                PreviewBg = B(0xE8, 0xE8, 0xE8),
                RootFill = B(0xCF, 0xE3, 0xF7), RootStroke = B(0x3A, 0x6E, 0xA5),
                LiveFill = B(0xD9, 0xEA, 0xD3), LiveStroke = B(0x4C, 0x9A, 0x5A),
                DeadFill = B(0xF4, 0xCC, 0xCC), DeadStroke = B(0xC0, 0x39, 0x2B),
                HintFill = B(0xFC, 0xE8, 0xC0), HintStroke = B(0xC8, 0x8B, 0x1E),
                ChipText = B(0x22, 0x22, 0x22),
            };
        }

        private static PetStudioTheme BuildDark()
        {
            return new PetStudioTheme
            {
                Dark = true,
                WindowBg = B(0x20, 0x20, 0x20),
                Text = B(0xF0, 0xF0, 0xF0),
                Muted = B(0xAA, 0xAA, 0xAA),
                Surface = B(0x2D, 0x2D, 0x30),
                Border = B(0x46, 0x46, 0x4A),
                PreviewBg = B(0x3A, 0x3A, 0x3D),
                RootFill = B(0x24, 0x40, 0x5C), RootStroke = B(0x4E, 0x86, 0xC4),
                LiveFill = B(0x2C, 0x4A, 0x34), LiveStroke = B(0x5F, 0xB0, 0x6E),
                DeadFill = B(0x5C, 0x2B, 0x27), DeadStroke = B(0xD8, 0x69, 0x5B),
                HintFill = B(0x5A, 0x45, 0x1C), HintStroke = B(0xD8, 0xA8, 0x3F),
                ChipText = B(0xF0, 0xF0, 0xF0),
            };
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1 = 19;

        /// <summary>Paint the window to match the host. The title bar is set for both themes; the control
        /// tree is recoloured only in dark mode (light keeps the stock templates, like the host).</summary>
        internal void Apply(Window window)
        {
            if (window == null) return;

            window.SourceInitialized += delegate
            {
                try
                {
                    IntPtr hwnd = new WindowInteropHelper(window).Handle;
                    int v = Dark ? 1 : 0;
                    if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int)) != 0)
                        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1, ref v, sizeof(int));
                }
                catch { }
            };

            if (!Dark) return;

            window.Background = WindowBg;
            window.Foreground = Text;
            ResourceDictionary res = window.Resources;
            Implicit(res, typeof(TextBlock), new Setter(TextBlock.ForegroundProperty, Text));
            Implicit(res, typeof(Button),
                new Setter(Control.BackgroundProperty, Surface),
                new Setter(Control.ForegroundProperty, Text),
                new Setter(Control.BorderBrushProperty, Border));
            Implicit(res, typeof(TextBox),
                new Setter(Control.BackgroundProperty, Surface),
                new Setter(Control.ForegroundProperty, Text),
                new Setter(Control.BorderBrushProperty, Border),
                new Setter(TextBoxBase.CaretBrushProperty, Text));
            // No custom ScrollBar template: the host's is vertical-only, but this window has horizontal
            // scrollbars (the XML editor's long base64 line, the frame strip), so a vertical-only template
            // would break them. Default WPF scrollbars work in both orientations; the slight light look in
            // dark mode is an accepted trade for correctness.
        }

        private static void Implicit(ResourceDictionary res, Type target, params Setter[] setters)
        {
            var style = new Style(target);
            foreach (Setter s in setters) style.Setters.Add(s);
            res[target] = style;
        }
    }
}
