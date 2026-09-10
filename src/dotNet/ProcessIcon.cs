using System;
using System.Windows.Forms;
using System.Drawing;
using System.Reflection;

namespace DesktopAICompanion
{
        /// <summary>
        /// System Tray Icon. Shows an icon on the Taskbar to allow a ContextMenu.
        /// </summary>
    public sealed class ProcessIcon : IDisposable
    {
            /// <summary>
            /// The NotifyIcon object.
            /// </summary>
        NotifyIcon ni;
        ContextMenus menus;
        TaskbarWatcher taskbarWatcher;
        System.Windows.Forms.Timer presenceTimer;
        int presenceAttempt;
        int presenceRepairs;

            /// <summary>
            /// The app's name in the notification area.
            ///
            /// Deliberately a CONSTANT, and deliberately not the active pet's name. Windows 11 keys its tray
            /// entry on the EXECUTABLE and caches a single label per path (see TrayPromotion), so a per-pet
            /// label in that slot is a category error -- and because several pet types can be on screen at
            /// once, the old "&lt;pet&gt; Desktop Pet" named whichever one happened to be the default and
            /// silently misdescribed the rest. The pet's own name still reaches the About dialog through
            /// ContextMenus.UpdateIcon, which is where it identifies something real.
            ///
            /// Kept separate from ProductVersion.props's DesktopAICompanionProductName ("Desktop AI Companion"),
            /// which is the INSTALL identity: that one names the install directory and the MSI product, so
            /// changing it would move %LOCALAPPDATA%\Programs\... and break upgrade detection.
            /// </summary>
        internal const string TrayDisplayName = "Desktop AI Companion";

            /// <summary>
            /// Initializes a new instance of the <see cref="ProcessIcon"/> class.
            /// </summary>
        public ProcessIcon()
        {
            // Instantiate the NotifyIcon object.
            ni = new NotifyIcon();
        }

            /// <summary>
            /// Displays the icon in the system tray.
            /// </summary>
        public void Display()
        {
            // Put the icon in the system tray and allow it react to mouse clicks.			
            ni.MouseClick += new MouseEventHandler(Ni_MouseClick);
            ni.MouseDoubleClick += new MouseEventHandler(Ni_MouseDoubleClick);

            ni.Text = TrayDisplayName;
            ni.Visible = true;

            // Attach a context menu.
            menus = new ContextMenus();
            ni.ContextMenuStrip = menus.Create();

            // Listen for the shell rebuilding its notification area, and for an outside request
            // to close (BUG-001). Created here rather than in the constructor so the window is
            // owned by the same thread that pumps the icon's messages.
            if (taskbarWatcher == null)
            {
                try
                {
                    taskbarWatcher = new TaskbarWatcher(
                        ReassertIcon, RequestOrderlyExit, RequestSessionEndExit);
                }
                catch (Exception ex)
                {
                    // The icon still works; only the recovery belt is missing. Say which.
                    StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.error,
                        "tray watcher not created: " + ex.GetType().Name);
                }
            }

            StartPresenceVerification();
        }

            /// <summary>
            /// Displays the icon in the system tray.
            /// </summary>
        public void SetIcon(System.IO.MemoryStream icon, string petName, string aboutAuthor, string aboutTitle, string aboutVersion, string aboutInfo)
        {
            bool success = true;
			try
			{
                Icon replacement = new Icon(icon, 32, 32);
                Icon oldIcon = ni.Icon;
                // Text BEFORE Icon, and it matters: WinForms only issues the Shell_NotifyIcon NIM_ADD once
                // an icon exists (Display() sets Visible with a null Icon, which adds nothing), and Windows
                // 11 permanently caches the tooltip carried by that first ADD as the entry's InitialTooltip.
                // Re-asserted here rather than left to Display() so the guarantee is local to the one method
                // that triggers the ADD, and cannot be broken by a later change to Display()'s ordering.
                ni.Text = TrayDisplayName;
				ni.Icon = replacement;
                if (oldIcon != null) oldIcon.Dispose();
				ContextMenus.UpdateIcon(ni.Icon, petName, aboutAuthor, aboutTitle, aboutVersion, aboutInfo);
			}
			catch(Exception)
			{
                success = false;
			}
            if(!success)
            {
                try
                {
                    StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.error, "Animation ICON is invalid (icon converter is on the webpage)");
                    Icon replacement;
                    using (Icon extracted = Icon.ExtractAssociatedIcon(
                        Assembly.GetExecutingAssembly().Location))
                        replacement = new Icon(extracted, 32, 32);
                    Icon oldIcon = ni.Icon;
                    ni.Icon = replacement;
                    if (oldIcon != null) oldIcon.Dispose();
                    ContextMenus.UpdateIcon(ni.Icon, petName, aboutAuthor, aboutTitle, aboutVersion, aboutInfo);
                }
                catch (Exception) { } // probably thread error.
            }
            // Say what actually happened, every run. A missing tray icon leaves the app running but
            // unreachable, and the one investigation into that had NO evidence to work from: this method
            // logged only on the failure branch, so a run where SetIcon "succeeded" and the icon still did
            // not appear looked identical to a run that never got here. These three facts separate the
            // cases -- did we set an icon, is the control marked visible, and which name did the shell get.
            // "success" here means only that the assignment above did not throw, which is NOT the
            // same as the shell having accepted the icon -- and conflating the two is what hid
            // BUG-001 through two investigations that both read this line. `shellHasIt` is the
            // answer to the question that actually matters; "unknown" is reported as such.
            bool? shellHasIt = TrayIconPresence.TryIsPresent(ni);
            StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info,
                "tray icon set: noThrow=" + success +
                " icon=" + (ni.Icon != null) +
                " visible=" + ni.Visible +
                " shellHasIt=" + (shellHasIt.HasValue ? shellHasIt.Value.ToString() : "unknown") +
                " text='" + (ni.Text ?? "") + "'");

            // The icon is registered with the shell by this point (successfully or via the fallback above),
            // so its Windows 11 notification-area entry now exists and can be lifted out of the hidden-icons
            // flyout. Fire-and-forget: nothing about the pet depends on the outcome.
            TrayPromotion.PromoteOnce(Application.ExecutablePath, ni.Text);
        }

            /// <summary>
            /// Show a tray notification (Windows renders it as a toast), with an optional one-shot action for
            /// when the user clicks it. Used by the monthly module-update check: the pet must not nag with a
            /// modal dialog for something as minor as "a module has a newer build", but a notification the user
            /// can click through to the Modules pane is the difference between an update they find and one they
            /// never learn about. Silent no-op when the icon is not visible, so it can be called blindly.
            /// </summary>
        public void ShowBalloon(string title, string text, Action onClicked)
        {
            try
            {
                if (ni == null || !ni.Visible) return;
                if (balloonClicked != null) { ni.BalloonTipClicked -= balloonClicked; balloonClicked = null; }
                if (onClicked != null)
                {
                    // One-shot: unhook on the first click, so a later notification never replays this action.
                    balloonClicked = delegate
                    {
                        if (balloonClicked != null) { ni.BalloonTipClicked -= balloonClicked; balloonClicked = null; }
                        try { onClicked(); } catch (Exception) { }
                    };
                    ni.BalloonTipClicked += balloonClicked;
                }
                ni.BalloonTipTitle = title ?? "";
                ni.BalloonTipText = text ?? "";
                ni.BalloonTipIcon = ToolTipIcon.Info;
                ni.ShowBalloonTip(10000);
            }
            catch (Exception) { }
        }

        EventHandler balloonClicked;

            /// <summary>
            /// Releases unmanaged and - optionally - managed resources
            /// </summary>
        public void Dispose()
        {
            StopPresenceVerification();
            if (taskbarWatcher != null)
            {
                try { taskbarWatcher.Dispose(); } catch { }
                taskbarWatcher = null;
            }
            // When the application closes, this will remove the icon from the system tray immediately.
            if (ni != null)
            {
                ni.MouseClick -= Ni_MouseClick;
                ni.MouseDoubleClick -= Ni_MouseDoubleClick;
                if (balloonClicked != null) { ni.BalloonTipClicked -= balloonClicked; balloonClicked = null; }
                ni.ContextMenuStrip = null;
                if (menus != null)
                {
                    menus.Dispose();
                    menus = null;
                }
                if (ni.Icon != null)
                {
                    ni.Icon.Dispose();
                    ni.Icon = null;
                }
                ni.Visible = false;
                ni.Dispose();
                ni = null;
            }
        }

            /// <summary>
            /// Handles the MouseClick event of the ni control.
            /// </summary>
            /// <param name="sender">The source of the event.</param>
            /// <param name="e">The <see cref="System.Windows.Forms.MouseEventArgs"/> instance containing the event data.</param>
        void Ni_MouseClick(object sender, MouseEventArgs e)
        {
            // Handle mouse button clicks.
            if (e.Button == MouseButtons.Left)
            {
                // Start Windows Explorer.
                Program.Mainthread.TopMostSheeps();
            }
        }

            /// <summary>
            /// A double click will automatically start a new pet.
            /// </summary>
            /// <param name="sender">Caller as object.</param>
            /// <param name="e">Mouse event values.</param>
        void Ni_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            // Handle mouse button clicks.
            if (e.Button == MouseButtons.Left)
            {
                // Start Windows Explorer.
                //Process.Start("explorer", null);
                Program.Mainthread.AddSheep();
            }
        }

        /// <summary>
        /// Re-add the tray icon after the shell has thrown its notification area away.
        ///
        /// BUG-001. Windows keeps a notification-area slot per owning window and that slot can outlive
        /// its owner: when the app is force-killed (msiexec's TerminateProcess during an upgrade, Task
        /// Manager, a crash) the NIM_DELETE a clean exit sends is never sent. Explorer restarting has a
        /// comparable effect, and was equally unhandled -- TaskbarCreated was not observed ANYWHERE in
        /// this codebase before this.
        ///
        /// This is the belt that does not care WHY the area went away, which is why it is worth having
        /// alongside the WM_CLOSE fix below: it covers the crash and the Explorer restart, neither of
        /// which an orderly-exit path can reach.
        /// </summary>
        private void ReassertIcon()
        {
            ReassertIcon("TaskbarCreated");
        }

        /// <summary>
        /// <paramref name="reason"/> is logged verbatim, because the first real capture of this bug
        /// (2026-09-10, v1.1.1 on a fresh install) produced four lines reading "re-added after
        /// TaskbarCreated" when nothing of the sort had happened -- the presence check had triggered
        /// them. A log line that misattributes its own cause is the exact defect this whole fix exists
        /// to remove, so it is not repeated here.
        /// </summary>
        private void ReassertIcon(string reason)
        {
            NotifyIcon icon = ni;
            if (icon == null) return;
            try
            {
                ReassertSequence(new NotifyIconVisibility(icon));
                // A fresh shell can file a brand new registry entry, and a new entry has no IsPromoted
                // value -- the very case PromoteOnce exists for -- so the once-per-process guard has to
                // be lifted or the icon stays buried in the overflow flyout.
                TrayPromotion.AllowRetry();
                TrayPromotion.PromoteOnce(Application.ExecutablePath, icon.Text);
                StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info, "tray icon re-added (" + reason + ")");
            }
            catch (Exception ex)
            {
                // Never throw out of a broadcast handler: it runs on the UI pump and the shell does not
                // care, but an escaping exception would take the app down.
                StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.error,
                    "tray icon re-add failed: " + ex.GetType().Name);
            }
        }

        /// <summary>
        /// The two operations a re-add consists of, in the order that matters.
        ///
        /// Visible false -> true is NIM_DELETE followed by NIM_ADD. Setting Visible = true ALONE does
        /// nothing when WinForms already believes the icon is shown, which is exactly the state after a
        /// shell restart: our side never changed, only the shell forgot. Split out behind a seam so that
        /// ordering can be asserted without a real notification area -- the same reasoning that made
        /// SetIcon's Text-before-Icon an asserted invariant rather than a comment.
        /// </summary>
        internal static void ReassertSequence(ITrayVisibility surface)
        {
            if (surface == null) return;
            surface.SetVisible(false);
            surface.SetVisible(true);
        }

        /// <summary>The one operation <see cref="ReassertSequence"/> needs. See it for why.</summary>
        internal interface ITrayVisibility
        {
            void SetVisible(bool visible);
        }

        private sealed class NotifyIconVisibility : ITrayVisibility
        {
            private readonly NotifyIcon _icon;
            internal NotifyIconVisibility(NotifyIcon icon) { _icon = icon; }
            public void SetVisible(bool visible) { _icon.Visible = visible; }
        }

        /// <summary>
        /// Shut down the way the tray menu does, so the notification icon is removed on the way out.
        /// Used when something outside the app asks it to close: the installer, or a session end.
        /// </summary>
        private static void RequestOrderlyExit()
        {
            StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info,
                "orderly exit requested by WM_CLOSE (installer or session end)");
            StartUp main = Program.Mainthread;
            if (main == null)
            {
                Application.Exit();
                return;
            }
            // The same call ContextMenus.Exit_Click makes. KillSheeps disposes the tray icon as its
            // FIRST action, which is the point: that is the NIM_DELETE the force-kill never sent.
            main.KillSheeps(true);
        }

        /// <summary>
        /// Shut down IMMEDIATELY for a session end, skipping the farewell animations.
        ///
        /// Distinct from RequestOrderlyExit on purpose. That path calls KillSheeps(true), which
        /// deliberately lingers about a second so the companions can play a death animation -- lovely when
        /// the user chose to quit, and fatal here: Restart Manager gives an app a short window to go and
        /// force-terminates anything still running, and a terminated process never sends its NIM_DELETE,
        /// which is what leaves a dead icon behind in the tray.
        ///
        /// So the icon is disposed FIRST (that is the NIM_DELETE), then the message loop is ended. No
        /// animation, no timers, nothing that can take longer than the deadline.
        /// </summary>
        private static void RequestSessionEndExit()
        {
            StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info,
                "session end: removing the tray icon and exiting immediately");
            StartUp main = Program.Mainthread;
            try
            {
                // Dispose the icon before anything else can fail. Everything after this is best effort.
                if (main != null) main.RemoveTrayIconForSessionEnd();
            }
            catch (Exception ex)
            {
                StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.error,
                    "session end: tray icon removal failed: " + ex.GetType().Name);
            }
            try { Application.Exit(); } catch { }
        }

        /// <summary>
        /// A hidden TOP-LEVEL window that receives the shell's "TaskbarCreated" broadcast, a session-end
        /// request, and an external WM_CLOSE.
        ///
        /// Top-level on purpose: the shell posts TaskbarCreated with HWND_BROADCAST, which reaches
        /// top-level windows ONLY -- a message-only (HWND_MESSAGE) window is never sent it, which is the
        /// usual way this fix gets written and silently does nothing. Kept 0x0 and WS_EX_TOOLWINDOW so
        /// it cannot show up on screen, in the taskbar, or in Alt-Tab.
        /// </summary>
        private sealed class TaskbarWatcher : NativeWindow, IDisposable
        {
            private const int WsExToolWindow = 0x00000080;
            private const int WsVisible = 0x10000000;
            private const int WmClose = 0x0010;
            private const int WmQueryEndSession = 0x0011;
            private const int WmEndSession = 0x0016;
            private readonly int _taskbarCreated;
            private readonly Action _onTaskbarCreated;
            private readonly Action _onCloseRequested;
            private readonly Action _onSessionEnd;
            private readonly System.Threading.SynchronizationContext _ui =
                System.Threading.SynchronizationContext.Current;
            private bool _sessionEndStarted;

            internal TaskbarWatcher(Action onTaskbarCreated, Action onCloseRequested)
                : this(onTaskbarCreated, onCloseRequested, null,
                       NativeRegisterWindowMessage("TaskbarCreated"))
            {
            }

            internal TaskbarWatcher(Action onTaskbarCreated, Action onCloseRequested, Action onSessionEnd)
                : this(onTaskbarCreated, onCloseRequested, onSessionEnd,
                       NativeRegisterWindowMessage("TaskbarCreated"))
            {
            }

            /// <summary>
            /// Test seam: supply the message id directly. Exists so the id-is-zero branch is REACHABLE --
            /// RegisterWindowMessage effectively never fails in practice, so a mutation removing that
            /// guard survived every assertion until the id could be injected, which made the guard
            /// unverifiable defensive code rather than tested behaviour.
            /// </summary>
            internal TaskbarWatcher(Action onTaskbarCreated, Action onCloseRequested, Action onSessionEnd,
                int messageId)
            {
                _onTaskbarCreated = onTaskbarCreated;
                _onCloseRequested = onCloseRequested;
                _onSessionEnd = onSessionEnd;
                _taskbarCreated = messageId;
                var p = new CreateParams
                {
                    Caption = "DesktopAICompanionTrayWatcher",
                    // Parked far off the virtual desktop. Windows refuses a 0x0 window and silently
                    // enlarged this to 16x16 at (0,0), the top-left corner of the primary display, where
                    // an unpainted window can show as an artifact. POSITION is what keeps it unseen, not
                    // size, and that is what the self-test asserts.
                    X = -32000,
                    Y = -32000,
                    Width = 0,
                    Height = 0,
                    // WS_VISIBLE, and it is load-bearing. Measured 2026-09-10 on a real upgrade:
                    // with this window invisible, the installer's util:CloseApplication never delivered
                    // WM_CLOSE to it -- the app logged no shutdown at all, Restart Manager reported "unable
                    // to automatically close all requested applications", and TerminateProcess killed the
                    // process. So the close request only reaches windows the enumeration considers visible.
                    //
                    // Imperceptible regardless: 0x0 pixels has nothing to paint, and WS_EX_TOOLWINDOW keeps
                    // it out of the taskbar and Alt-Tab. Asserted below by SIZE and style rather than by
                    // IsWindowVisible, because "cannot be seen" is the property that matters and
                    // "not visible to the window manager" was the thing breaking the fix.
                    //
                    // Not WS_CHILD: HWND_BROADCAST (TaskbarCreated) reaches top-level windows only.
                    Style = WsVisible,
                    ExStyle = WsExToolWindow,
                };
                CreateHandle(p);
            }

            protected override void WndProc(ref Message m)
            {
                // RegisterWindowMessage returns 0 on failure, and treating that as a match would fire on
                // WM_NULL, which arrives routinely.
                if (_taskbarCreated != 0 && m.Msg == _taskbarCreated && _onTaskbarCreated != null)
                {
                    try { _onTaskbarCreated(); } catch { }
                }
                else if (m.Msg == WmQueryEndSession)
                {
                    // Restart Manager asks this before an install replaces our files, and answering it is
                    // what decides whether the user sees "setup was unable to automatically close all
                    // requested applications". Measured from an MSI verbose log 2026-09-10: RM shuts the
                    // app down at T+0.15s, a full HALF SECOND before WiX's util:CloseApplication runs at
                    // T+0.70s -- so RM, not the WiX action, is what closes the app during an install, and
                    // RM speaks the SESSION-END protocol, never WM_CLOSE. The WM_CLOSE handler below was
                    // therefore waiting for a message no installer sends.
                    //
                    // WinForms does answer this already, but on a hidden broadcast window living on a
                    // BACKGROUND thread that owns no forms, so it says "yes" and nothing shuts down. This
                    // window is top-level and on the UI THREAD, which is why the message lands here where
                    // it can be acted on.
                    StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info,
                        "session end queried (installer or shutdown); agreeing to close");
                    m.Result = (IntPtr)1;   // yes, we can close

                    // ...and then actually close, rather than waiting for WM_ENDSESSION.
                    //
                    // Measured on a real interactive repair 2026-09-10: this query arrived and was
                    // answered, and WM_ENDSESSION NEVER FOLLOWED. Restart Manager took "yes" to mean the
                    // app would now exit under its own steam, waited, and force-terminated it -- which the
                    // maintainer saw as the installer hanging at the end before closing. Answering yes and
                    // then doing nothing is worse than not answering at all, because RM believes it has an
                    // agreement.
                    //
                    // POSTED, not called inline: the answer above has to be returned to RM before the
                    // message loop shuts down, and exiting inside the handler never lets that happen.
                    StartSessionEndExit();
                    return;
                }
                else if (m.Msg == WmEndSession)
                {
                    // wParam == 0 means the session end was cancelled; only a non-zero value is a real
                    // instruction to go.
                    // Still handled, for the case where RM (or a real logoff) does follow through, and
                    // idempotent because the query above will usually have started this already.
                    if (m.WParam != IntPtr.Zero) StartSessionEndExit();
                    m.Result = IntPtr.Zero;
                    return;
                }
                else if (m.Msg == WmClose && _onCloseRequested != null)
                {
                    // BUG-001, and the half that addresses the reported repro. The MSI's
                    // util:CloseApplication posts WM_CLOSE to the target's TOP-LEVEL windows and then
                    // force-kills if the process has not gone. Nothing in the app turned that into a
                    // real shutdown: WinForms answers WM_QUERYENDSESSION on a background broadcast
                    // thread that owns no forms, so the polite request was acknowledged and ignored, the
                    // kill always won, and the icon's NIM_DELETE was never sent.
                    //
                    // Routing it into the same orderly exit the tray's "Remove all companions and Close"
                    // uses tears the icon down first, deliberately.
                    //
                    // This only takes effect from the version that CONTAINS it: during an upgrade the
                    // exe being closed is the OLD one. TerminateProcess stays as the backstop for that
                    // first hop and for a genuinely wedged process.
                    try { _onCloseRequested(); } catch { }
                    return;   // handled: do not let DefWindowProc destroy the window mid-shutdown
                }
                base.WndProc(ref m);
            }

            /// <summary>
            /// Begin the session-end exit exactly once, on the UI thread, without blocking the message
            /// being handled. Idempotent: WM_QUERYENDSESSION and WM_ENDSESSION can both ask.
            /// </summary>
            private void StartSessionEndExit()
            {
                if (_sessionEndStarted || _onSessionEnd == null) return;
                _sessionEndStarted = true;
                Action exit = _onSessionEnd;
                System.Threading.SynchronizationContext ui = _ui;
                if (ui != null)
                    ui.Post(delegate { try { exit(); } catch { } }, null);
                else
                    try { exit(); } catch { }
            }

            public void Dispose()
            {
                if (Handle != IntPtr.Zero) DestroyHandle();
            }

            [System.Runtime.InteropServices.DllImport(
                "user32.dll",
                EntryPoint = "RegisterWindowMessageW",
                CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            private static extern int NativeRegisterWindowMessage(string message);
        }

        /// <summary>
        /// --traywatcher-selftest: prove the BUG-001 recovery wiring, with no tray and no installer.
        ///
        /// Worth asserting rather than eyeballing, because both halves fail SILENTLY when got wrong: a
        /// message-only window compiles, runs, and simply never receives the shell broadcast, and a
        /// mistyped message name registers a perfectly valid id that nothing will ever send.
        /// </summary>
        internal static bool SelfTest()
        {
            var report = new System.Text.StringBuilder();
            bool ok = true;
            int taskbarFired = 0;
            int closeFired = 0;
            int sessionEndFired = 0;
            TaskbarWatcher watcher = null;
            // Install a real WinForms synchronization context for the duration. Without one,
            // SynchronizationContext.Current is null (there is no Application.Run here), the watcher falls
            // back to invoking the exit inline, and the "posted, not inline" property -- the one that lets
            // RM receive its answer before the loop stops -- could not be tested at all.
            System.Threading.SynchronizationContext previousContext =
                System.Threading.SynchronizationContext.Current;
            System.Threading.SynchronizationContext.SetSynchronizationContext(
                new WindowsFormsSynchronizationContext());
            try
            {
                watcher = new TaskbarWatcher(
                    delegate { taskbarFired++; },
                    delegate { closeFired++; },
                    delegate { sessionEndFired++; });

                ok &= TrayAssert(report, "watcher has a window handle", watcher.Handle != IntPtr.Zero);

                // TOP-LEVEL, not message-only: HWND_BROADCAST never reaches an HWND_MESSAGE window, so
                // this assertion is the difference between a working fix and one that does nothing.
                ok &= TrayAssert(report, "watcher window is top-level (no parent)",
                    NativeGetParent(watcher.Handle) == IntPtr.Zero);
                const int GwlStyle = -16;
                const int WsChild = 0x40000000;
                int style = NativeGetWindowLong(watcher.Handle, GwlStyle);
                ok &= TrayAssert(report, "watcher window is not a child window", (style & WsChild) == 0);

                // Deliberately NOT "is not visible" any more. That assertion passed while the fix it was
                // guarding silently did nothing: an invisible window never receives the installer's
                // WM_CLOSE. What must hold is that the user cannot SEE it, which is a matter of size and
                // of being a tool window, so those are what get checked.
                const int WsExToolWindowCheck = 0x00000080;
                const int GwlExStyle = -20;
                int exStyle = NativeGetWindowLong(watcher.Handle, GwlExStyle);
                ok &= TrayAssert(report, "watcher window is a tool window (never in taskbar or Alt-Tab)",
                    (exStyle & WsExToolWindowCheck) != 0);
                // Windows will not honour a 0x0 window (it became 16x16), so the guarantee is
                // POSITION: the window must not intersect the virtual desktop at all. Checked against the
                // real multi-monitor bounds rather than a hardcoded coordinate.
                NativeRect rect;
                bool gotRect = NativeGetWindowRect(watcher.Handle, out rect);
                System.Drawing.Rectangle virtualScreen = SystemInformation.VirtualScreen;
                var watcherBounds = new System.Drawing.Rectangle(
                    rect.Left, rect.Top,
                    Math.Max(0, rect.Right - rect.Left),
                    Math.Max(0, rect.Bottom - rect.Top));
                ok &= TrayAssert(report,
                    "watcher window sits off the virtual desktop, so it cannot be seen",
                    gotRect && !virtualScreen.IntersectsWith(watcherBounds));

                int registered = NativeRegisterWindowMessage2("TaskbarCreated");
                ok &= TrayAssert(report, "TaskbarCreated registers a usable message id", registered != 0);

                NativeSendMessage(watcher.Handle, registered, IntPtr.Zero, IntPtr.Zero);
                ok &= TrayAssert(report, "the shell's TaskbarCreated invokes the re-add", taskbarFired == 1);

                // WM_NULL must not be mistaken for it: that is what a failed RegisterWindowMessage
                // (which returns 0) would collide with if the id were not checked.
                NativeSendMessage(watcher.Handle, 0x0000, IntPtr.Zero, IntPtr.Zero);
                ok &= TrayAssert(report, "WM_NULL does not trigger a re-add", taskbarFired == 1);

                // --- the SESSION-END protocol, which is what an installer actually uses -----------
                // Measured from an MSI verbose log: Restart Manager shuts the app down half a second
                // BEFORE WiX's util:CloseApplication runs, and RM speaks WM_QUERYENDSESSION /
                // WM_ENDSESSION -- never WM_CLOSE. The WM_CLOSE handling below was therefore waiting for
                // a message no installer sends, which is why the maintainer still saw "setup was unable
                // to automatically close all requested applications" on a 1.1.2 upgrade.
                const int WmQueryEndSessionMsg = 0x0011;
                const int WmEndSessionMsg = 0x0016;

                // Must answer TRUE, or Windows treats us as refusing to close.
                IntPtr agreed = NativeSendMessage(watcher.Handle, WmQueryEndSessionMsg,
                    IntPtr.Zero, IntPtr.Zero);
                ok &= TrayAssert(report, "WM_QUERYENDSESSION is answered yes", agreed != IntPtr.Zero);

                // The exit must be SCHEDULED by the query, not deferred until WM_ENDSESSION. Measured on a
                // real interactive repair: that second message never came, RM waited on an agreement we
                // were not keeping, and force-killed the process -- the "installer hangs at the end"
                // symptom. Posted rather than inline, so it is only observable after the loop is pumped.
                ok &= TrayAssert(report, "the exit has not run INSIDE the message handler",
                    sessionEndFired == 0);
                Application.DoEvents();
                ok &= TrayAssert(report, "answering the query schedules the exit", sessionEndFired == 1);

                // Idempotent: a following WM_ENDSESSION must not exit a second time.
                NativeSendMessage(watcher.Handle, WmEndSessionMsg, (IntPtr)1, IntPtr.Zero);
                Application.DoEvents();
                ok &= TrayAssert(report, "a following WM_ENDSESSION does not exit twice",
                    sessionEndFired == 1);
                ok &= TrayAssert(report, "session end is not confused with the tray re-add",
                    taskbarFired == 1);

                // A CANCELLED session end (wParam == 0) on a fresh watcher must not exit at all: quitting
                // the app because Windows changed its mind would be worse than the bug.
                using (var cancelled = new TaskbarWatcher(
                    delegate { }, null, delegate { sessionEndFired += 100; }))
                {
                    NativeSendMessage(cancelled.Handle, WmEndSessionMsg, IntPtr.Zero, IntPtr.Zero);
                    Application.DoEvents();
                    ok &= TrayAssert(report, "a cancelled session end does NOT exit",
                        sessionEndFired == 1);
                }

                // WM_CLOSE is still handled: it covers a manual close request, and costs nothing.
                NativeSendMessage(watcher.Handle, WmCloseForTest, IntPtr.Zero, IntPtr.Zero);
                ok &= TrayAssert(report, "WM_CLOSE requests the orderly exit", closeFired == 1);
                ok &= TrayAssert(report, "WM_CLOSE does not also trigger a re-add", taskbarFired == 1);

                // ...and must not have destroyed the window out from under the shutdown it just began.
                ok &= TrayAssert(report, "the watcher survives the WM_CLOSE it handled",
                    NativeIsWindow(watcher.Handle));

                // The id-is-zero case, now reachable. RegisterWindowMessage returns 0 on failure, and a
                // watcher that trusted it would re-add the icon on every WM_NULL -- which is routine
                // traffic, so the tray would flicker constantly on a machine where registration failed.
                using (var blind = new TaskbarWatcher(delegate { taskbarFired++; }, null, null, 0))
                {
                    int before = taskbarFired;
                    NativeSendMessage(blind.Handle, 0x0000, IntPtr.Zero, IntPtr.Zero);
                    ok &= TrayAssert(report, "a failed message registration does not re-add on WM_NULL",
                        taskbarFired == before);
                }

                // The re-add is NIM_DELETE then NIM_ADD, in that order. Asserted through the seam
                // because Visible = true on its own is a no-op in the exact state this recovers from.
                var recorder = new VisibilityRecorder();
                ReassertSequence(recorder);
                ok &= TrayAssert(report, "the re-add toggles visibility off then on",
                    recorder.Calls.Count == 2 && recorder.Calls[0] == false && recorder.Calls[1] == true);
                ok &= TrayAssert(report, "the re-add tolerates a missing surface",
                    SafeReassertNull());

                // --- BUG-001's real root cause: is the icon ACTUALLY in the shell? --------------
                // The whole fix rests on TrayIconPresence being able to answer that, via WinForms
                // internals reached by reflection. If a future runtime renames them the check would
                // silently start answering "unknown" forever and the bug would quietly return, so the
                // seam is asserted here rather than trusted.
                ok &= TrayAssert(report, "the icon-presence check is supported on this runtime",
                    TrayIconPresence.Supported);

                // Only a definite "the shell does not have it" may trigger a re-add. Repairing on
                // unknown would re-add on every tick of every healthy run.
                ok &= TrayAssert(report, "a present icon is not repaired",
                    !TrayIconPresence.ShouldRepair(true));
                ok &= TrayAssert(report, "a missing icon IS repaired",
                    TrayIconPresence.ShouldRepair(false));
                ok &= TrayAssert(report, "an UNKNOWN answer is not treated as missing",
                    !TrayIconPresence.ShouldRepair(null));
                ok &= TrayAssert(report, "presence of no icon at all is unknown, not false",
                    TrayIconPresence.TryIsPresent(null) == null);

                // Backoff must actually back off, and stay bounded.
                bool rising = true;
                for (int a = 1; a < TrayIconPresence.MaximumAttempts; a++)
                    if (TrayIconPresence.RetryDelayMilliseconds(a + 1) <
                        TrayIconPresence.RetryDelayMilliseconds(a))
                        rising = false;
                ok &= TrayAssert(report, "the retry delay never decreases", rising);
                ok &= TrayAssert(report, "the retry delay is bounded",
                    TrayIconPresence.RetryDelayMilliseconds(99) <= 60000);
                ok &= TrayAssert(report, "verification is bounded to a few attempts",
                    TrayIconPresence.MaximumAttempts >= 3 && TrayIconPresence.MaximumAttempts <= 10);

                // The primitive itself, against a REAL tray icon. This is the assertion that would
                // have caught the bug: it distinguishes an icon the shell holds from one it does not,
                // which is precisely what `success=True` could never do.
                using (var probe = new NotifyIcon())
                {
                    probe.Icon = System.Drawing.SystemIcons.Information;
                    probe.Text = "presence-selftest";
                    probe.Visible = true;
                    Application.DoEvents();
                    bool? shown = TrayIconPresence.TryIsPresent(probe);
                    ok &= TrayAssert(report, "a shown icon reads as present in the shell",
                        shown.HasValue && shown.Value);

                    probe.Visible = false;
                    Application.DoEvents();
                    bool? hidden = TrayIconPresence.TryIsPresent(probe);
                    ok &= TrayAssert(report, "a hidden icon reads as ABSENT from the shell",
                        hidden.HasValue && !hidden.Value);
                }

                // --- the repair path, proven by FAULT INJECTION -------------------------------
                // The natural failure is intermittent: one msiexec-launched start dropped the icon and
                // a later identical one did not. Waiting for a bad run would mean the repair is never
                // actually exercised, so the exact broken state is manufactured here -- the shell holds
                // no icon while WinForms still believes it is shown, which is what a dropped NIM_ADD
                // leaves behind. This is the assertion that shows the fix WORKS, not merely that it
                // compiles.
                using (var victim = new NotifyIcon())
                {
                    victim.Icon = System.Drawing.SystemIcons.Warning;
                    victim.Text = "repair-selftest";
                    victim.Visible = true;
                    Application.DoEvents();

                    bool? before = TrayIconPresence.TryIsPresent(victim);
                    ok &= TrayAssert(report, "fault injection starts from a present icon",
                        before.HasValue && before.Value);

                    ok &= TrayAssert(report, "the icon can be dropped behind WinForms' back",
                        TrayIconPresence.TryDeleteBehindWinForms(victim));
                    Application.DoEvents();

                    // WinForms is now WRONG about its own state -- exactly the situation that made the
                    // old log line useless.
                    ok &= TrayAssert(report, "WinForms still believes the icon is visible", victim.Visible);
                    bool? dropped = TrayIconPresence.TryIsPresent(victim);
                    ok &= TrayAssert(report, "the check DETECTS the dropped icon",
                        dropped.HasValue && !dropped.Value);
                    ok &= TrayAssert(report, "a dropped icon is judged repairable",
                        TrayIconPresence.ShouldRepair(dropped));

                    // The repair the timer performs.
                    ReassertSequence(new NotifyIconVisibility(victim));
                    Application.DoEvents();

                    bool? repaired = TrayIconPresence.TryIsPresent(victim);
                    ok &= TrayAssert(report, "the repair puts the icon BACK in the shell",
                        repaired.HasValue && repaired.Value);
                    ok &= TrayAssert(report, "a repaired icon needs no further repair",
                        !TrayIconPresence.ShouldRepair(repaired));
                }
            }
            catch (Exception ex)
            {
                ok = false;
                report.AppendLine("FAIL: threw " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (watcher != null) { try { watcher.Dispose(); } catch { } }
                System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);
            }

            report.AppendLine("RESULT=" + (ok ? "PASS" : "FAIL"));
            Console.Write(report.ToString());
            return ok;
        }

        private const int WmCloseForTest = 0x0010;

        private sealed class VisibilityRecorder : ITrayVisibility
        {
            internal readonly System.Collections.Generic.List<bool> Calls =
                new System.Collections.Generic.List<bool>();
            public void SetVisible(bool visible) { Calls.Add(visible); }
        }

        private static bool SafeReassertNull()
        {
            try { ReassertSequence(null); return true; }
            catch { return false; }
        }

        private static bool TrayAssert(System.Text.StringBuilder report, string what, bool condition)
        {
            report.AppendLine((condition ? "PASS: " : "FAIL: ") + what);
            return condition;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetParent")]
        private static extern IntPtr NativeGetParent(IntPtr window);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "IsWindow")]
        private static extern bool NativeIsWindow(IntPtr window);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int NativeGetWindowLong(IntPtr window, int index);

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowRect")]
        private static extern bool NativeGetWindowRect(IntPtr window, out NativeRect rect);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr NativeSendMessage(IntPtr window, int message, IntPtr w, IntPtr l);

        [System.Runtime.InteropServices.DllImport(
            "user32.dll",
            EntryPoint = "RegisterWindowMessageW",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int NativeRegisterWindowMessage2(string message);


        /// <summary>
        /// Verify the shell actually took the icon, and re-add it if not.
        ///
        /// THE fix for BUG-001, and the reason the earlier attempts missed: nothing ever checked. The
        /// app called `Visible = true`, logged that no exception was thrown, and moved on. Measured on
        /// the shipped 1.1.0 MSI: a process launched by msiexec has its NIM_ADD silently dropped, while
        /// the identical binary launched normally is fine. So the icon must be verified rather than
        /// assumed, and re-added when the shell says it does not have it.
        ///
        /// Only a DEFINITE "not present" triggers a re-add (see TrayIconPresence.ShouldRepair). When the
        /// answer is unknown -- the WinForms internals the check reads have moved, or the icon has no
        /// window yet -- nothing is re-added, because re-adding on every tick of a healthy run is worse
        /// than the bug. Bounded attempts with backoff, so a shell that will never accept the icon
        /// produces one clear log line instead of a permanent timer.
        /// </summary>
        private void StartPresenceVerification()
        {
            if (presenceTimer != null) return;
            if (!TrayIconPresence.Supported)
            {
                // Say so rather than silently degrading: this is the seam the whole check rests on.
                StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info,
                    "tray icon presence cannot be verified on this runtime; relying on TaskbarCreated only");
                return;
            }
            presenceAttempt = 0;
            presenceRepairs = 0;
            presenceTimer = new System.Windows.Forms.Timer
            {
                Interval = TrayIconPresence.RetryDelayMilliseconds(1)
            };
            presenceTimer.Tick += PresenceTimer_Tick;
            presenceTimer.Start();
        }

        private void PresenceTimer_Tick(object sender, EventArgs e)
        {
            presenceAttempt++;
            NotifyIcon icon = ni;
            if (icon == null) { StopPresenceVerification(); return; }

            bool? present = TrayIconPresence.TryIsPresent(icon);

            if (TrayIconPresence.ShouldRepair(present))
            {
                StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info,
                    "tray icon MISSING from the shell (check " + presenceAttempt + "); re-adding");
                ReassertIcon("presence check " + presenceAttempt);
                presenceRepairs++;
            }

            // Deliberately does NOT stop at the first success. The whole schedule runs, because the
            // window in which the shell drops an icon is the period while it is still settling after
            // an install -- so an icon can be accepted at 1.5s and gone at 6s. Stopping early would
            // cover only the first case. Five NIM_MODIFY calls over ~22s cost nothing measurable, and
            // a healthy run logs nothing at all.
            if (presenceAttempt >= TrayIconPresence.MaximumAttempts)
            {
                bool? last = TrayIconPresence.TryIsPresent(icon);
                if (last.HasValue && !last.Value)
                    StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.error,
                        "tray icon STILL missing after " + presenceRepairs +
                        " repair attempt(s); the shell is refusing it");
                else if (presenceRepairs > 0)
                    StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info,
                        "tray icon recovered after " + presenceRepairs + " repair attempt(s)");
                else if (!last.HasValue)
                    StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info,
                        "tray icon presence stayed unknown across " + presenceAttempt + " checks");
                StopPresenceVerification();
                return;
            }

            presenceTimer.Interval =
                TrayIconPresence.RetryDelayMilliseconds(presenceAttempt + 1);
        }

        private void StopPresenceVerification()
        {
            if (presenceTimer == null) return;
            presenceTimer.Stop();
            presenceTimer.Tick -= PresenceTimer_Tick;
            presenceTimer.Dispose();
            presenceTimer = null;
        }

    }
}
