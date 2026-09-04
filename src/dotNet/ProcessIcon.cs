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
    }
}
