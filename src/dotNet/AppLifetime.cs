using System;
using System.Windows.Forms;

namespace DesktopAICompanion
{
    /// <summary>
    /// Owns the process lifetime, so that "please close" from Windows actually closes the app.
    ///
    /// The app called <c>Application.Run()</c> with no main form, because a pet IS a window and no single
    /// window owns the app. Two consequences, and the second is the damaging one:
    ///
    ///   * An installer's Restart Manager pass cannot close it, so every upgrade over a running instance
    ///     stops on "The setup was unable to automatically close all requested applications."
    ///   * When Restart Manager tries anyway it CLOSES THE WINDOWS IT CAN FIND and the process survives,
    ///     because with no main form nothing ends the message loop. The tray icon's window goes with them,
    ///     leaving pets wandering the desktop with no tray icon and no way to quit.
    ///
    /// Measured on a real install: the app answers WM_QUERYENDSESSION in 27ms and is still running 32
    /// seconds later with 9 windows and 28 threads up.
    ///
    /// The cause is thread placement. WinForms turns WM_QUERYENDSESSION into an application exit from its
    /// hidden ".NET-BroadcastEventWindow", and that window is created on whichever thread first touches
    /// SystemEvents. On a configured install a module touches it during load, so it lands on a BACKGROUND
    /// thread (measured: tid 39260) while every form lives on the UI thread (tid 43076). Session-end
    /// handling then runs on a thread that owns no forms, and the UI message loop never stops. A build with
    /// no modules configured does NOT reproduce it, which is why this read as an installer problem.
    ///
    /// Two independent belts here, because the failure is a race about thread affinity and either one alone
    /// leaves a gap:
    ///   * A hidden MAIN form, so closing it returns from Application.Run and shuts down through the
    ///     existing disposal paths, restoring the ordinary WinForms contract.
    ///   * An explicit SessionEnding subscription, marshalled onto the thread that owns this form, so the
    ///     exit happens on the UI thread no matter which thread the event is raised on.
    /// </summary>
    internal sealed class AppLifetime : Form
    {
        private bool subscribed;

        internal AppLifetime()
        {
            // A message-only-ish window: no chrome, no taskbar button, nothing to alt-tab to. It still has
            // an HWND and is still top-level, which is what matters.
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Opacity = 0;
            Width = 1;
            Height = 1;
            Text = "DesktopAICompanion";

            Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding;
            subscribed = true;
        }

        /// <summary>Never become visible, however it is asked. Application.Run shows its main form; this
        /// keeps that from putting a stray window on screen while still creating the handle.</summary>
        protected override void SetVisibleCore(bool value)
        {
            if (!IsHandleCreated) CreateHandle();   // Run() would otherwise never materialise the HWND
            base.SetVisibleCore(false);
        }

        /// <summary>
        /// Windows, or an installer's Restart Manager, is asking the app to close. Say yes and mean it.
        /// </summary>
        private void OnSessionEnding(object sender, Microsoft.Win32.SessionEndingEventArgs e)
        {
            // Never set e.Cancel. Refusing does not keep the app usefully alive; it makes the installer give
            // up and tell the user to close things by hand, which is the bug being fixed.
            try
            {
                if (IsHandleCreated && InvokeRequired) BeginInvoke((MethodInvoker)Application.Exit);
                else Application.Exit();
            }
            catch
            {
                // A teardown race (handle already gone) is not worth surfacing: the app is closing anyway.
            }
        }

        protected override void Dispose(bool disposing)
        {
            // SystemEvents holds a STATIC event, so a missed unsubscribe outlives the form and keeps the
            // whole object graph alive.
            if (disposing && subscribed)
            {
                subscribed = false;
                try { Microsoft.Win32.SystemEvents.SessionEnding -= OnSessionEnding; } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
