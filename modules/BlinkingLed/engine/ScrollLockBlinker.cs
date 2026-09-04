using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopAICompanion.BlinkingLed
{
    /// <summary>
    /// The blink itself: toggle Scroll Lock on a two-phase timer so the keyboard's Scroll Lock LED blinks.
    ///
    /// Ported from the standalone BlinkingLED tray app. What it actually does is worth stating precisely,
    /// because the short description ("blinks the keyboard light") understates it: this synthesizes a real
    /// keyboard event through the Win32 <c>SendInput</c> API. Scroll Lock is chosen because it is inert -- no
    /// application receives a meaningful keystroke and nothing is ever typed anywhere -- but the event does go
    /// through the OS input queue, so Windows counts it as user input and the system idle timer resets. That
    /// is the point of the tool, and it is why the module says "presses a key that does nothing" rather than
    /// claiming it only touches the LED.
    ///
    /// <para>
    /// <c>SendInput</c> rather than <c>SendKeys.SendWait</c>, which is what the original PowerShell script
    /// used: SendKeys depends on the foreground window, which a background tray app does not own, so it fails
    /// unpredictably. Kept from the port.
    /// </para>
    ///
    /// Nothing here throws: a failed toggle records why and the next tick tries again.
    /// </summary>
    internal sealed class ScrollLockBlinker : IDisposable
    {
        // On/off phase durations per rate, verbatim from the standalone app so a user who moves over gets the
        // cadence they already chose. "On" is how long the LED stays lit, "off" the dark gap between blinks.
        internal static readonly string[] RateNames =
            { "Glacial", "Sluggish", "Slow", "Normal", "Fast", "Hyper" };

        internal const string DefaultRate = "Normal";

        private Timer _timer;
        private bool _phaseOn;
        private int _onMs = 2500;
        private int _offMs = 7500;
        private bool _running;

        // Just enough to answer the one diagnostic question worth asking: did the last SendInput land, and if
        // not, why. Reported by the "Blink once now" button rather than a live tray readout, since a stale
        // countdown was not worth the tray space.
        internal int LastWin32Error { get; private set; }
        internal long ToggleCount { get; private set; }

        /// <summary>Raised when Caps Lock is found ON at a tick, if StopOnCapsLock is set. The standalone app
        /// quit the process here; a module cannot quit the host, so it stops and tells the module instead.</summary>
        internal event Action CapsLockStopRequested;

        internal bool IsRunning { get { return _running; } }
        internal bool StopOnCapsLock { get; set; }


        internal static void DurationsFor(string rate, out int onMs, out int offMs)
        {
            switch (rate)
            {
                case "Glacial": onMs = 4000; offMs = 240000; return;
                case "Sluggish": onMs = 3500; offMs = 120000; return;
                case "Slow": onMs = 3000; offMs = 12000; return;
                case "Fast": onMs = 1000; offMs = 2000; return;
                case "Hyper": onMs = 500; offMs = 1000; return;
                default: onMs = 2500; offMs = 7500; return;   // Normal, and any unknown value
            }
        }

        internal static bool IsKnownRate(string rate)
        {
            if (string.IsNullOrEmpty(rate)) return false;
            foreach (string name in RateNames)
                if (string.Equals(name, rate, StringComparison.Ordinal)) return true;
            return false;
        }

        internal void SetRate(string rate)
        {
            DurationsFor(rate, out _onMs, out _offMs);
            // Apply immediately rather than at the next phase change, so a user switching to Hyper to check
            // it works does not wait four minutes on the old Glacial gap.
            if (_timer != null) _timer.Interval = Math.Max(1, _phaseOn ? _onMs : _offMs);
        }

        internal void Start()
        {
            if (_running) return;
            _running = true;
            _phaseOn = false;
            _timer = new Timer();
            _timer.Interval = Math.Max(1, _offMs);
            _timer.Tick += OnTick;
            _timer.Start();
        }

        /// <summary>
        /// Stop blinking, and leave the LED OFF rather than wherever the cadence happened to land. Without
        /// this, stopping mid-blink leaves Scroll Lock stuck on and the user is left with a lit LED and no
        /// obvious way to clear it.
        /// </summary>
        internal void Stop()
        {
            if (!_running) return;
            _running = false;
            DisposeTimer();
            try { if (_phaseOn && IsScrollLockOn()) Toggle(); }
            catch { }
            _phaseOn = false;
        }

        /// <summary>
        /// One immediate toggle, for the pane's "Blink once now" button. Independent of the timer and of
        /// whether blinking is on, because its whole job is answering "is this doing anything at all?" when
        /// the LED has not moved: it either bumps ToggleCount or leaves a Win32 error to report.
        /// </summary>
        internal void BlinkOnce()
        {
            try { Toggle(); }
            catch { LastWin32Error = -1; }
        }

        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                if (StopOnCapsLock && IsCapsLockOn())
                {
                    Stop();
                    Action handler = CapsLockStopRequested;
                    if (handler != null) handler();
                    return;
                }

                Toggle();
                _phaseOn = !_phaseOn;
                if (_timer != null) _timer.Interval = Math.Max(1, _phaseOn ? _onMs : _offMs);
            }
            catch
            {
                // Record the failure so "Blink once now" can still report it, and keep ticking.
                LastWin32Error = -1;
            }
        }

        private void Toggle()
        {
            var inputs = new INPUT[2];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].U.ki.wVk = VK_SCROLL;
            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].U.ki.wVk = VK_SCROLL;
            inputs[1].U.ki.dwFlags = KEYEVENTF_KEYUP;

            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            LastWin32Error = sent == 0 ? Marshal.GetLastWin32Error() : 0;
            if (sent != 0) ToggleCount++;
        }

        internal static bool IsCapsLockOn() { return Control.IsKeyLocked(Keys.CapsLock); }
        internal static bool IsScrollLockOn() { return Control.IsKeyLocked(Keys.Scroll); }

        public void Dispose()
        {
            _running = false;
            DisposeTimer();
        }

        private void DisposeTimer()
        {
            if (_timer == null) return;
            try
            {
                _timer.Stop();
                _timer.Tick -= OnTick;
                _timer.Dispose();
            }
            catch { }
            _timer = null;
        }

        // ---- Win32 ----------------------------------------------------------

        private const int INPUT_KEYBOARD = 1;
        private const ushort VK_SCROLL = 0x91;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public int type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    }
}
