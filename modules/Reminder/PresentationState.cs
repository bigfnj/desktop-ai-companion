using System;
using System.Runtime.InteropServices;

namespace DesktopAICompanion.ReminderModule
{
    /// <summary>
    /// Asks Windows whether now is a bad time to interrupt: a fullscreen app, presentation mode, a full-screen
    /// D3D game, a fullscreen Store app, or Windows' quiet time. Uses SHQueryUserNotificationState, the shell
    /// call the host's question card asks too (UserPresence in src/dotNet/TrayAnchor.cs). A failed call defaults
    /// to "don't hush" so a reminder is never silently dropped on a box where the query misbehaves.
    ///
    /// QUNS_QUIET_TIME is NOT Do Not Disturb (N-tray-questions-03). It is Windows' own quiet period, the first
    /// hour after setup or a feature update, when the shell holds notifications back. This comment called it
    /// "Do Not Disturb / quiet hours" until 2026-10-26. Whether Windows 11's Do Not Disturb (Focus) reaches this
    /// call at all is UNVERIFIED: no Do Not Disturb run has been read on a real machine, and the shell may not
    /// report it here. The pane's "Stay quiet while presenting or in Do Not Disturb" promises it, so the check is
    /// owed: switch Do Not Disturb on, ask a test question from the debug window, and read the host's
    /// "[question] held" line, which is the same call.
    /// </summary>
    internal static class PresentationState
    {
        private enum QUERY_USER_NOTIFICATION_STATE
        {
            QUNS_NOT_PRESENT = 1,
            QUNS_BUSY = 2,                     // a fullscreen app is running, or Presentation Settings are on
            QUNS_RUNNING_D3D_FULL_SCREEN = 3,
            QUNS_PRESENTATION_MODE = 4,
            QUNS_ACCEPTS_NOTIFICATIONS = 5,   // normal
            QUNS_QUIET_TIME = 6,              // Windows' quiet period after setup or a feature update; not Do Not Disturb
            QUNS_APP = 7,                     // a fullscreen Store app
        }

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE state);

        public static bool ShouldHush()
        {
            try
            {
                QUERY_USER_NOTIFICATION_STATE state;
                if (SHQueryUserNotificationState(out state) != 0) return false;   // S_OK is 0; anything else, don't hush
                switch (state)
                {
                    case QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY:
                    case QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN:
                    case QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE:
                    case QUERY_USER_NOTIFICATION_STATE.QUNS_QUIET_TIME:
                    case QUERY_USER_NOTIFICATION_STATE.QUNS_APP:
                        return true;
                    default:
                        return false;
                }
            }
            catch { return false; }
        }
    }
}
