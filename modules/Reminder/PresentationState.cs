using System;
using System.Runtime.InteropServices;

namespace DesktopAICompanion.ReminderModule
{
    /// <summary>
    /// Asks Windows whether now is a bad time to interrupt: a fullscreen app, presentation mode, a full-screen
    /// D3D game, a fullscreen Store app, or Do Not Disturb / quiet time. Uses SHQueryUserNotificationState, the
    /// same signal the OS uses to hold back its own toast notifications. A failed call defaults to "don't hush"
    /// so a reminder is never silently dropped on a box where the query misbehaves.
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
            QUNS_QUIET_TIME = 6,              // Do Not Disturb / quiet hours portion
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
