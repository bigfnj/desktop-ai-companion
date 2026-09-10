using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopAICompanion
{
    /// <summary>
    /// Answers one question the app could not previously ask: does the shell ACTUALLY hold our tray
    /// icon right now?
    ///
    /// BUG-001, root cause. `ProcessIcon.SetIcon` logged `success=True` whenever its try block did not
    /// throw, and `NotifyIcon.Visible = true` does not report whether the shell accepted the resulting
    /// `NIM_ADD`. So a dropped add and a working one produced byte-identical log lines, and the bug
    /// survived two investigations that both had the log in hand.
    ///
    /// Measured 2026-09-10 against the shipped 1.1.0 MSI, and the honest summary is that the fault is
    /// INTERMITTENT rather than deterministic:
    ///   - one msiexec-launched start  -> icon absent; re-adding it made it appear at once
    ///   - a direct launch             -> icon present first try
    ///   - a later msiexec-launched start, same binary and same installer -> present first try
    /// So "the installer launch always drops it" is NOT supported, and the backlog's claim that this
    /// "reproduces every time" was wrong. What IS established: the add can be dropped, the app could
    /// not tell, and a re-add recovers it. `Shell_NotifyIcon` returning TRUE does not mean the shell
    /// retained the icon.
    ///
    /// Because the natural failure is flaky, the repair path is proven by FAULT INJECTION instead of by
    /// waiting for a bad run -- see TryDeleteBehindWinForms and the --traywatcher-selftest cases that
    /// manufacture "shell has no icon, app thinks it does" and assert recovery.
    ///
    /// The check itself: `Shell_NotifyIcon(NIM_MODIFY)` returns FALSE when the shell holds no icon for
    /// a given (hWnd, uID). Validated before being relied on -- with the icon shown it answers True,
    /// with the icon hidden it answers False -- so it genuinely discriminates rather than always
    /// succeeding.
    /// </summary>
    internal static class TrayIconPresence
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATAW
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

        private const uint NimModify = 0x00000001;
        private const uint NimDelete = 0x00000002;

        // WinForms keeps the icon's owning window and its shell id private. Resolved once and cached;
        // a rename in a future WinForms would make these null, which is reported as "unknown" rather
        // than guessed at -- see TryIsPresent.
        private static readonly FieldInfo WindowField =
            typeof(NotifyIcon).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo IdField =
            typeof(NotifyIcon).GetField("_id", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>True when the reflection seam this check depends on is still present.</summary>
        internal static bool Supported
        {
            get { return WindowField != null && IdField != null; }
        }

        /// <summary>
        /// Does the shell hold this icon? <c>true</c>/<c>false</c> when known, and <c>null</c> when the
        /// question cannot be asked at all (the WinForms internals moved, or the icon has no window
        /// yet). Null is deliberately distinct from false: "I cannot tell" must not be reported as "it
        /// is missing", or the repair loop below would churn forever on a healthy tray.
        /// </summary>
        internal static bool? TryIsPresent(NotifyIcon icon)
        {
            if (icon == null || !Supported) return null;
            try
            {
                var window = WindowField.GetValue(icon) as NativeWindow;
                if (window == null) return null;
                IntPtr handle = window.Handle;
                if (handle == IntPtr.Zero) return null;

                uint id = Convert.ToUInt32(IdField.GetValue(icon));

                var data = new NOTIFYICONDATAW
                {
                    cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATAW)),
                    hWnd = handle,
                    uID = id,
                    uFlags = 0,          // touch nothing: we want only the existence answer
                    szTip = string.Empty,
                    szInfo = string.Empty,
                    szInfoTitle = string.Empty,
                };
                return Shell_NotifyIconW(NimModify, ref data);
            }
            catch (Exception)
            {
                // Never let a diagnostic take down the tray. Unknown, not missing.
                return null;
            }
        }

        /// <summary>
        /// FAULT INJECTION, for the self-test only: remove the icon from the shell behind WinForms'
        /// back, leaving the NotifyIcon believing it is still shown.
        ///
        /// This exists because the real failure is INTERMITTENT. Measured 2026-09-10: an
        /// msiexec-launched start dropped the icon once and then, on a later identical install, accepted
        /// it first try. A repair path that only runs during a flaky natural failure is a repair path
        /// that is never actually tested, so the self-test manufactures the exact state instead:
        /// shell has no icon, app thinks it does. That is precisely what a dropped NIM_ADD leaves
        /// behind.
        /// </summary>
        internal static bool TryDeleteBehindWinForms(NotifyIcon icon)
        {
            if (icon == null || !Supported) return false;
            try
            {
                var window = WindowField.GetValue(icon) as NativeWindow;
                if (window == null || window.Handle == IntPtr.Zero) return false;
                var data = new NOTIFYICONDATAW
                {
                    cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATAW)),
                    hWnd = window.Handle,
                    uID = Convert.ToUInt32(IdField.GetValue(icon)),
                    uFlags = 0,
                    szTip = string.Empty,
                    szInfo = string.Empty,
                    szInfoTitle = string.Empty,
                };
                return Shell_NotifyIconW(NimDelete, ref data);
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Should a repair be attempted, given what <see cref="TryIsPresent"/> answered?
        ///
        /// Pure, so the three cases are asserted rather than reasoned about. Only a definite "the shell
        /// does not have it" justifies re-adding: repairing on <c>null</c> would mean re-adding on every
        /// tick of every healthy run, which is the failure mode a blind retry loop has.
        /// </summary>
        internal static bool ShouldRepair(bool? present)
        {
            return present.HasValue && !present.Value;
        }

        /// <summary>
        /// Delay before verification attempt <paramref name="attempt"/> (1-based), in milliseconds.
        ///
        /// Backed off rather than fixed, because the two cases have very different timescales: a normal
        /// launch is correct within a second, while an installer-launched start is racing a shell that
        /// is still settling after the install. Bounded on purpose -- see
        /// <see cref="MaximumAttempts"/> -- because a tray that never accepts the icon is a real
        /// possibility (a locked-down shell, a broken Explorer) and must not become a permanent timer.
        /// </summary>
        internal static int RetryDelayMilliseconds(int attempt)
        {
            switch (attempt)
            {
                case 1: return 1500;
                case 2: return 3000;
                case 3: return 6000;
                case 4: return 12000;
                case 5: return 20000;
                default: return 30000;
            }
        }

        /// <summary>
        /// How many times to verify before giving up and saying so in the log.
        ///
        /// Sized from a MEASURED refusal window, not a guess. First real capture (2026-09-10, v1.1.1 on
        /// a fresh install): the shell refused the add at 1.5s, 3s, 6s, 12s and 20s, and the icon only
        /// came back on the check after that. Recovery landed on the LAST attempt of the original
        /// five-step schedule, so a slightly longer refusal would have exhausted it and left the user
        /// with no icon and a "giving up" line.
        ///
        /// Nine attempts now run out to roughly 2.7 minutes. The cost of the extra headroom on a healthy
        /// machine is four more Shell_NotifyIcon(NIM_MODIFY) calls and no log output at all, which is a
        /// far better trade than being one tick short.
        /// </summary>
        internal const int MaximumAttempts = 9;
    }
}
