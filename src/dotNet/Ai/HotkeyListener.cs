using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// Registers one system-wide hotkey via user32 <c>RegisterHotKey</c> and raises
    /// <see cref="Pressed"/> when it fires. Owns a hidden message window (<see cref="NativeWindow"/>)
    /// created on the UI thread, so WM_HOTKEY is delivered by the WinForms message pump.
    /// </summary>
    internal sealed class HotkeyListener : NativeWindow, IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const int HotkeyId = 0x1D01;

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private bool _registered;

        /// <summary>Raised (on the UI thread) each time the registered hotkey is pressed.</summary>
        public event EventHandler Pressed;

        public HotkeyListener()
        {
            CreateHandle(new CreateParams());
        }

        /// <summary>
        /// Register the hotkey described by a string such as "Ctrl+Alt+P".
        /// Returns false when the string is invalid or the combination is already in use.
        /// </summary>
        public bool Register(string hotkey)
        {
            Unregister();
            uint mods, vk;
            if (!TryParse(hotkey, out mods, out vk)) return false;
            _registered = RegisterHotKey(Handle, HotkeyId, mods | MOD_NOREPEAT, vk);
            return _registered;
        }

        public void Unregister()
        {
            if (_registered)
            {
                UnregisterHotKey(Handle, HotkeyId);
                _registered = false;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && (int)m.WParam == HotkeyId)
            {
                EventHandler h = Pressed;
                if (h != null) h(this, EventArgs.Empty);
            }
            base.WndProc(ref m);
        }

        /// <summary>
        /// Parse "Ctrl+Alt+P" into modifier flags + a virtual-key code (case-insensitive).
        /// Requires at least one modifier and exactly one non-modifier key.
        /// </summary>
        public static bool TryParse(string hotkey, out uint modifiers, out uint vk)
        {
            modifiers = 0;
            vk = 0;
            if (string.IsNullOrWhiteSpace(hotkey)) return false;

            bool haveKey = false;
            foreach (string raw in hotkey.Split('+'))
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;
                switch (p.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control": modifiers |= MOD_CONTROL; break;
                    case "alt": modifiers |= MOD_ALT; break;
                    case "shift": modifiers |= MOD_SHIFT; break;
                    case "win":
                    case "windows": modifiers |= MOD_WIN; break;
                    default:
                        Keys k;
                        if (haveKey || !Enum.TryParse(p, true, out k)) return false;
                        vk = (uint)k;
                        haveKey = true;
                        break;
                }
            }
            return haveKey && modifiers != 0;
        }

        public void Dispose()
        {
            Unregister();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
