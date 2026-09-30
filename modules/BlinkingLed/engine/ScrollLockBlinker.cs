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
    /// Nothing here throws: a failed toggle records why, leaves the phase flag where the key is, and the next
    /// tick tries again.
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

        /// <summary>Toggles ATTEMPTED, accepted or not. ToggleCount advances only when Windows accepts the
        /// SendInput, so on a runner that refuses synthesized input it cannot say whether Stop() tried its
        /// corrective toggle at all; this can, on any machine. Self-test seam (F114).</summary>
        internal long AttemptCount { get; private set; }

        /// <summary>How Stop() reads the key before its corrective toggle. Defaults to the real
        /// IsScrollLockOn; the self-test substitutes a fixed answer so BOTH branches of the gate -- a key we
        /// lit, and a key the USER lit -- run whatever the machine's own LED is doing (F114).</summary>
        internal Func<bool> ScrollLockReader = IsScrollLockOn;

        /// <summary>The shape of <see cref="KeypressSender"/>: deliver one Scroll Lock press-and-release,
        /// answer whether Windows accepted it, and say which Win32 error it gave when it did not.</summary>
        internal delegate bool KeypressDelivery(out int win32Error);

        /// <summary>How Toggle() delivers the keypress. Defaults to the real SendInput; the self-test
        /// substitutes a fixed acceptance or a fixed refusal so "the phase moves only with the key" is
        /// asserted in BOTH directions on any machine. A real SendInput cannot show the refused branch on a
        /// box that accepts every synthesized keypress (this one does), nor the accepted branch on a headless
        /// runner that refuses them all, and it moves the developer's own LED besides (F116, F113).</summary>
        internal KeypressDelivery KeypressSender = SendScrollLockKeypress;

        /// <summary>Real keypresses this PROCESS has delivered through SendInput, whatever Windows answered.
        /// The self-test asserts it made an even number of them, because Scroll Lock is a toggle: paired
        /// presses leave the developer's key where the machine had it, and the suite used to leave it lit
        /// after every gate run (F113). Static because the count belongs to the keyboard, not to one blinker.</summary>
        internal static long RealKeypressCount { get; private set; }

        /// <summary>The interval the timer is armed with, 0 when not running. Exposed for the self-test, which
        /// cannot wait for a tick: the cadence is only right if the interval follows the phase the key is
        /// actually in, and Start() used to arm the dark gap against a key it had lit (F115).</summary>
        internal int ArmedIntervalMs { get { return _timer != null ? _timer.Interval : 0; } }

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
            // RECONCILE the belief with the key before arming the timer; do not zero it. "Blink once now" is
            // live while the feature is off, and it leaves the key lit with _phaseOn true and _running
            // false. The enable path -- the pane's Apply with the box ticked, a speed picked in the tray --
            // reaches Start() and never Stop(), where the 1.0.5 reconciliation lives, so `_phaseOn = false`
            // here ran the whole cadence inverted against a lit key for the rest of the session (on
            // Glacial, lit for four minutes and dark for four seconds) and disarmed Stop()'s corrective
            // toggle whenever the LED was lit: the very defect 1.0.5's note claimed fixed (F115, BUG-011).
            //
            // ADOPT the key: the belief becomes whatever the key reads, so the cadence can never start
            // inverted. A key we lit that still reads lit stays ours and the cadence continues from its LIT
            // phase, so the blink the user just made becomes the first blink rather than being undone; a key
            // we lit that reads dark is no longer ours (the user pressed it). Both as in the 1.0.6 first cut.
            // What that cut did NOT do was adopt a Scroll Lock the USER had lit before enabling: it applied
            // Stop()'s "our belief AND the hardware" rule here too, left `_phaseOn` false against a lit key,
            // and the cadence ran inverted for the session (lit for the dark gap's share of the cycle, 75%
            // on Normal) while Stop() then left the key wherever the cadence landed, so the Readme's "stopping
            // always leaves the light off" held only for a key the module lit (N-blinkingled-01). Adopting
            // lets Stop() clear a key the module did not light; the coordinator chose that on 2026-09-30
            // (variant C under `#### fix/blinkingled` in docs/DESIGN-REGISTER.md): a user who switches the
            // blinker on has asked for the light to be driven, and "off when it stops" is the promise made.
            // No keypress on enable, still: the rejected alternative was to mirror Stop() and clear the key
            // first, which costs a SendInput that can itself be refused and would then leave flag and key
            // disagreeing again. The read is the same one Stop() has relied on since 1.0.4; a reader that
            // throws is treated as "unknown", and the belief stands.
            try { _phaseOn = ScrollLockReader(); } catch { }
            _timer = new Timer();
            _timer.Interval = Math.Max(1, _phaseOn ? _onMs : _offMs);
            _timer.Tick += OnTick;
            _timer.Start();
        }

        /// <summary>
        /// Stop blinking, and leave the LED OFF rather than wherever the cadence happened to land. Without
        /// this, stopping mid-blink leaves Scroll Lock stuck on and the user is left with a lit LED and no
        /// obvious way to clear it.
        /// </summary>
        /// <summary>The phase the blinker BELIEVES it is in. Exposed for the self-test: the suite is
        /// headless, so the LED itself cannot be asserted, and the bug was precisely that this flag and the
        /// key disagreed.</summary>
        internal bool PhaseOn { get { return _phaseOn; } }

        internal void Stop()
        {
            // NO `if (!_running) return;` HERE. That guard is what made the 1.0.4 fix a half fix.
            //
            // "Blink once now" is live whether or not the feature is switched on -- its whole job is
            // answering "is this doing anything at all?" when the LED has not moved -- so the ordering
            // already-off-then-blink reached Stop() with _running false, returned before the corrective
            // toggle, and left Scroll Lock lit with nothing running to clear it. Ticking the feature on
            // afterwards then made it worse: Start() sets _phaseOn = false against a physically lit key,
            // so the cadence ran inverted for the rest of the session (on Glacial, lit for four minutes
            // and dark for four seconds). The 1.0.4 note claimed this was fixed; it fixed only the other
            // ordering, blink-then-switch-off, where _running was still true. Removing the guard fixed
            // only the Stop() half in turn: the enable path never reaches Stop(), so Start() had to learn
            // to reconcile as well (1.0.6, F115).
            //
            // Gated on _phaseOn AND the hardware, not on the hardware alone. _phaseOn is this object's
            // own belief that WE are holding the key on -- BlinkOnce and the cadence both maintain it, and
            // since 2026-09-30 Start() ADOPTS the key's state as well (N-blinkingled-01) -- so a Scroll Lock
            // the USER turned on is left alone only while the blinker was never started: at startup with the
            // feature off, where Init calls ApplyState(false) on a fresh blinker. Once the user has switched
            // the blinker on, the light is the module's to drive, and stopping leaves it off, as the Readme
            // promises.
            _running = false;
            DisposeTimer();
            if (_phaseOn)
            {
                // The belief is dropped only when the key is no longer ours: it reads dark (the user pressed
                // it), or Windows ACCEPTED the toggle that clears it. A refused corrective toggle leaves the
                // LED lit with this object still the one holding it, so _phaseOn stays true and the next
                // Stop() or Start() gets to try again. Zeroing it regardless, as 1.0.5 did, was the same
                // flag/hardware drift as a refused tick (F116): a later Start() then ran the cadence
                // inverted against a key it did not know it had lit, and a later Stop() left it lit.
                bool stillOurs;
                try { stillOurs = ScrollLockReader() && !Toggle(); }
                catch { stillOurs = true; }   // unknown: keep the belief, a retry costs one keypress
                _phaseOn = stillOurs;
            }
        }

        /// <summary>
        /// One immediate toggle, for the pane's "Blink once now" button. Independent of the timer and of
        /// whether blinking is on, because its whole job is answering "is this doing anything at all?" when
        /// the LED has not moved: it either bumps ToggleCount or leaves a Win32 error to report.
        /// </summary>
        internal void BlinkOnce()
        {
            // The phase moves with the key, and ONLY with the key. Without the flip a manual blink drove the
            // next cadence tick from an inverted flag (1.0.4). Flipping after a toggle Windows REFUSED
            // (sent == 0: a locked session, a UAC prompt, an elevated foreground window under UIPI) put the
            // belief one step out of phase with a key that had not moved; after an odd number of refusals
            // the cadence ran inverted and Stop()'s corrective toggle was disarmed whenever the LED was lit,
            // with no user action involved (F116, BUG-011).
            try { if (Toggle()) _phaseOn = !_phaseOn; }
            catch { LastWin32Error = -1; NoteDelivery(false, -1); }
        }

        private void OnTick(object sender, EventArgs e) { Tick(); }

        /// <summary>One step of the cadence: toggle, then choose the next gap from the phase the key is in.
        /// Internal so the self-test can step it without a message pump; the timer's handler is the only
        /// production caller (F116).</summary>
        internal void Tick()
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

                // Only with the key, as in BlinkOnce. A refused tick leaves the phase where it was, so the
                // next interval is the one for the phase the LED is actually in, and that tick tries again.
                if (Toggle()) _phaseOn = !_phaseOn;
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

        /// <summary>One synthesized press-and-release of Scroll Lock, through <see cref="KeypressSender"/>.
        /// True when Windows accepted it. A refusal is recorded (LastWin32Error, the delivery log), never
        /// thrown, and every caller moves the phase flag only on true.</summary>
        private bool Toggle()
        {
            AttemptCount++;
            int error;
            bool accepted = KeypressSender(out error);
            // Read from the RESULT rather than from a flag, so a refusal is reported as one: sent == 0 is
            // exactly what Windows says when it drops the input.
            LastWin32Error = accepted ? 0 : error;
            if (accepted) ToggleCount++;
            NoteDelivery(accepted, LastWin32Error);
            return accepted;
        }

        /// <summary>The real keypress: two INPUTs, key down and key up, through SendInput.</summary>
        private static bool SendScrollLockKeypress(out int win32Error)
        {
            RealKeypressCount++;
            var inputs = new INPUT[2];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].U.ki.wVk = VK_SCROLL;
            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].U.ki.wVk = VK_SCROLL;
            inputs[1].U.ki.dwFlags = KEYEVENTF_KEYUP;

            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            win32Error = sent == 0 ? Marshal.GetLastWin32Error() : 0;
            return sent != 0;
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
