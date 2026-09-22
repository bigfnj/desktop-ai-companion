using System;
using System.Globalization;
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

        /// <summary>
        /// Where this engine's diagnostic lines go. <c>BlinkingLedModule</c> points it at
        /// <c>IHost.Log(Info.Id, ...)</c>; left null (a self-test constructing the blinker directly) the
        /// lines are discarded.
        ///
        /// Static, following the <c>AiBrain.LogSink</c> precedent: the blinker is constructed by the module
        /// and by its self-test, and threading a sink through the constructor would change more call sites
        /// than it is worth.
        /// </summary>
        internal static Action<string> LogSink;

        /// <summary>
        /// Emit one diagnostic line. Never throws: a broken sink must not be able to stop the blink, which
        /// is the whole reason every path in here swallows in the first place.
        /// </summary>
        private static void Log(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            Action<string> sink = LogSink;
            if (sink == null) return;
            try { sink(message); } catch { }
        }

        /// <summary>
        /// Last known SendInput outcome, or null before the first attempt. Exists so delivery is recorded on
        /// the TRANSITION rather than per toggle: Hyper toggles once a second, so a line per attempt would
        /// bury every other module's diagnostics inside a day.
        /// </summary>
        private bool? _lastDeliveryOk;

        /// <summary>
        /// Record whether Windows accepted the synthesized keypress, logging only when it CHANGED.
        ///
        /// This is the module's one genuinely invisible failure. <c>SendInput</c> returning 0 -- UIPI
        /// refusing a lower-integrity sender, a locked or disconnected session, an elevated foreground
        /// window -- leaves the LED dark after the pet has already said "Keeping the lights on for you",
        /// and the only place that error was ever reported is the pane's "Blink once now" button, which a
        /// user has to know to press. The first attempt logs whichever way it went (null to known is a
        /// transition), because "did this ever work in this session" is the question a dead-LED report
        /// needs answered, and the answer is as interesting when it is yes.
        ///
        /// Internal so the self-test can drive BOTH outcomes without depending on whether the machine it
        /// runs on accepts synthesized input at all -- the same reason that self-test asserts nothing about
        /// the LED itself.
        /// </summary>
        internal void NoteDelivery(bool ok, int win32Error)
        {
            if (_lastDeliveryOk.HasValue && _lastDeliveryOk.Value == ok) return;
            bool first = !_lastDeliveryOk.HasValue;
            _lastDeliveryOk = ok;
            Log("blink delivery " + (ok ? "accepted" : "refused") +
                (first ? " (first attempt)" : " (was " + (ok ? "refused" : "accepted") + ")") +
                (ok ? "" : " win32=" + win32Error.ToString(CultureInfo.InvariantCulture)));
        }

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
            catch { LastWin32Error = -1; NoteDelivery(false, -1); }
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
                // Record the failure so "Blink once now" can still report it, and keep ticking. -1 is this
                // module's marker for "threw" rather than "Windows said no", so the log distinguishes the
                // two without carrying a message that could name a path.
                LastWin32Error = -1;
                NoteDelivery(false, -1);
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
            // Read from the RESULT rather than from a flag, so a refusal is reported as one: sent == 0 is
            // exactly what Windows says when it drops the input.
            NoteDelivery(sent != 0, LastWin32Error);
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

        /// <summary>
        /// THE UNION MUST BE SIZED BY ITS LARGEST MEMBER, NOT BY THE ONE WE USE.
        ///
        /// Win32's INPUT is a tagged union over MOUSEINPUT, KEYBDINPUT and HARDWAREINPUT, and
        /// SendInput validates cbSize against the full thing. Declaring only KEYBDINPUT made
        /// Marshal.SizeOf(typeof(INPUT)) report 32 on x64 where Windows requires 40, so every
        /// call was refused with ERROR_INVALID_PARAMETER (87) and the LED never blinked at all.
        /// Reported from the pane on 2026-09-22 as "Windows refused the input (error 87)".
        ///
        /// MEASURED, both shapes, in one process on this box:
        ///   INPUT with only KEYBDINPUT   32 bytes   SendInput sent=0 err=87
        ///   INPUT with the full union    40 bytes   SendInput sent=2 err=0
        ///
        /// MouseInput and HardwareInput are never read. They exist so the union is the size the
        /// API expects, which is why they are not dead code and must not be "tidied away".
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        /// <summary>Present only to size <see cref="InputUnion"/>. Never read.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        /// <summary>Present only to size <see cref="InputUnion"/>. Never read.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
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

        /// <summary>
        /// What SendInput requires cbSize to be, from the pointer size alone: 40 on x64, 28 on
        /// x86. Exposed so a self-test can assert the marshalled struct matches WITHOUT needing a
        /// window station, which is the gap that let error 87 ship. The delivery assertion is
        /// deliberately outcome-agnostic so it passes on a headless runner, and that makes a
        /// permanently broken interop indistinguishable from a runner refusing input. A size
        /// check has no such excuse: it is the same answer everywhere.
        /// </summary>
        internal static int RequiredInputSize { get { return IntPtr.Size == 8 ? 40 : 28; } }

        /// <summary>The size this build will actually pass as cbSize.</summary>
        internal static int MarshalledInputSize { get { return Marshal.SizeOf(typeof(INPUT)); } }
    }
}
