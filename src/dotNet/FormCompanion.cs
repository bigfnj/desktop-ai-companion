using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;

namespace DesktopAICompanion
{
        /// <summary>
        /// Form2 is the main class (form) of the pet. <br />
        /// Frames are borrowed from the Xml-owned shared sprite store and a Timer moves the pet.<br />
        /// The animations of this form is loaded from an XML.<br />
        /// </summary>
    public partial class FormCompanion : Form
    {
            /// <summary>
            /// Current step in the animation-frames list.
            /// </summary>
            /// <remarks>
            /// Every animation has a defined quantity of steps. They are calculated from:<br />
            /// - Quantity of frames<br />
            /// - Repeat count and repeat from<br />
            /// If an animation has 10 different frames and frame 5 to 9 are repeated 8 times, the total of steps is 10 + 5 * 8 = 50 steps.<br />
            /// Once the last step was reached, the next animation will be started. If now animation is set, the SPAWN will be executed.<br />
            /// </remarks>
        int AnimationStep;
            /// <summary>
            /// Structure with all informations about the current animation.
            /// </summary>
        TAnimation CurrentAnimation;
            /// <summary>
            /// Handle to the current window. If this value is 0, the sheep is NOT walking on a window.
            /// </summary>
        IntPtr _hwndWindow = (IntPtr)0;
        IntPtr hwndWindow
        {
            get { return _hwndWindow; }
            set
            {
                _hwndWindow = value;
                // A grip is meaningless without the window it grips. Nine places drop this handle for their
                // own reasons (spawn, relocate, drag, walked off the edge, window covered by another), and
                // any one of them forgetting would leave the pet pinned to a rectangle nothing re-reads. A
                // property is the only version of this that cannot go stale as sites are added.
                if (value == (IntPtr)0)
                {
                    windowGrip = WindowGrip.None;
                    _gripLastWindowTop = int.MinValue;
                }
            }
        }
            /// <summary>
            /// Handle to the full screen window. If this value is 0, there is no full screen window.
            /// </summary>
        IntPtr hwndFullscreenWindow = (IntPtr)0;
        NativeMethods.RECT currentWindowSize;

            /// <summary>Which side of <see cref="hwndWindow"/> the pet is gripping, if any.</summary>
        internal enum WindowGrip
        {
            /// <summary>Not gripping. hwndWindow, if set, means the pet is standing on the window's TOP.</summary>
            None = 0,
            Left,
            Right,
            /// <summary>Hanging from the window's UNDERSIDE. The transpose of Left/Right: the pet's Y is
            /// pinned and it travels horizontally, rather than the other way round.</summary>
            Bottom,
        }
            /// <summary>
            /// The pet is hanging on the LEFT or RIGHT side of <see cref="hwndWindow"/> rather than standing on
            /// its top.
            ///
            /// A separate field rather than a new meaning for hwndWindow, because everything already reading
            /// hwndWindow assumes "standing on the top edge" and means it geometrically: CheckTopWindow's
            /// coverage test compares a candidate window against rctO.TOP, and FollowWindow re-pins the pet to
            /// the top when the window moves. Overloading the handle would have made both of those quietly
            /// wrong instead of loudly absent.
            /// </summary>
        WindowGrip windowGrip = WindowGrip.None;
            /// <summary>Where the gripped window's top edge was last tick, so a window dragged vertically
            /// carries the pet with it. int.MinValue = no reading yet (the first tick of a grip).</summary>
        int _gripLastWindowTop = int.MinValue;

            /// <summary>Forces the next <see cref="Play"/> onto a specific display (relocation); -1 = none.</summary>
        int _forcedDisplayIndex = -1;
        DateTime _lastFullscreenScanUtc = DateTime.MinValue;
        DateTime _lastRelocateUtc = DateTime.MinValue;
        bool _fullscreenHidden = false;   // hidden because every monitor is blocked (no free screen)

            /// <summary>
            /// If sheep is walking to left  (default).
            /// </summary>
            /// <remarks>
            /// The original eSheep was a Japanese application. So it was normal to see something from right to left.<br />
            /// To leave the same characteristic, moveLeft is set to true. But it doesn't matter, because the sprite and movements gives the direction...<br />
            /// </remarks>
        bool IsMovingLeft = true;
            /// <summary>
            /// Animations class. The entire animation and its values are described here.
            /// </summary>
        readonly Animations Animations;
            /// <summary>
            /// Which pet TYPE this instance is: a folder/catalog id, "" for the active/default pet, or a
            /// synthetic "preview:..." id for a transient preview. Stable for the pet's lifetime. Backs the
            /// plugin ABI's ICompanion.TypeId, which is the only join a module has between the events it receives
            /// (bare pet handles) and the type-keyed pet-manager verbs.
            /// </summary>
        internal string PetTypeId { get { return Animations != null ? (Animations.PetTypeId ?? "") : ""; } }
            /// <summary>
            /// Xml class. Xml parser and functionality are stored here.
            /// </summary>
        readonly Xml Xml;
            /// <summary>
            /// If the pet is in dragging mode (user is holding the pet with the mouse)
            /// </summary>
        bool IsDragging = false;

            /// <summary>
            /// True while the user is actively handling the pet. The AI idle loop reads this
            /// to avoid interrupting an interaction (backlog 3.5). Read-only, additive.
            /// </summary>
        public bool IsBusy { get { return IsDragging; } }

        /// <summary>
        /// Title of the window the pet is currently standing on (its title bar), or "" when it is
        /// roaming the desktop / taskbar. Used by the AI layer for screen-zone awareness (backlog 5.6).
        /// </summary>
        public string WindowUnderCompanion
        {
            get
            {
                try
                {
                    if (hwndWindow == IntPtr.Zero) return "";
                    StringBuilder sb = new StringBuilder(256);
                    NativeMethods.GetWindowText(hwndWindow, sb, sb.Capacity);
                    return sb.ToString().Trim();
                }
                catch { return ""; }
            }
        }
            /// <summary>
            /// If the pet is leaving the screen
            /// </summary>
        bool IsLeaving = false;
            /// <summary>
            /// Offset Y - Sprite size is taken and not the single image. So, over the taskbar or over the windows, the pet could be 1-2 pixels over the border if you didn't drawn it on the bottom of the sprite frame.<br />
            /// With this offset, you can re-place the pet or you can give them an offset so that it is positioned over the window (for example if you want to show a girl sitting over the taskbar, you need this function)
            /// </summary>
        double OffsetY = 0.0;
            /// <summary>
            /// Current X position of the form. Because an offset can be used, this is the origin of the sprite (not like Form2.Left) before an offset was interpolated with the form position.
            /// </summary>
        double PositionX = 0.0;
            /// <summary>
            /// Current Y position of the form. Because an offset can be used, this is the origin of the sprite (not like Form2.Top) before an offset was interpolated with the form position.
            /// </summary>
        double PositionY = 0.0;

            /// <summary>
            /// If multi screens are available, the pet can be set on a defined screen
            /// </summary>
        int DisplayIndex = 0;

        private readonly List<FormCompanion> childs = new List<FormCompanion>(4);
        private const int MaximumChildDepth = 5;
        private const int MaximumActiveChildrenPerRoot = 32;
        private const int MaximumActiveChildrenProcess = 64;
        private readonly FormCompanion parentPet;
        private readonly int childDepth;
        private readonly ChildBudget childBudget;
        private readonly Point parentPosition;
        private readonly bool parentWasFlipped;
        private bool childOwnershipReleased;

        // Speech bubble — one per pet instance, lazy-created
        private FormSpeech _speech;

        /// <summary>
        /// Form constructor. This is never called. <br />
        /// Form2(Animations animations, Xml xml) -> Called when a new sheep is generated<br />
        /// Form2(Animations animations, Xml xml, Point parentPos, bool parentFlipped) -> Called when a Child is generated<br />
        /// </summary>
        public FormCompanion()
        {
            InitializeComponent();
        }

            /// <summary>
            /// Form constructor.  Called when a new sheep is generated. 
            /// </summary>
            /// <param name="animations">Animation class, with all values.</param>
            /// <param name="xml">Xml class, with xml functions</param>
        public FormCompanion(Animations animations, Xml xml)
        {
            Animations = animations;
            Xml = xml;
            childBudget = new ChildBudget();
            childDepth = 0;
            parentPosition = new Point(-1, -1);
            InitializeComponent();
            ConfigureTransparencyMode();
            Visible = false;            // Is invisible at beginning (we don't know where this sprite should be positioned)
            SetPetOpacity(0.0);
            for (var s = 0; s < Screen.AllScreens.Length; s++)
            {
                if (Screen.AllScreens[s].Primary)
                {
                    DisplayIndex = s;
                    break;
                }
            }
        }

            /// <summary>
            /// Form constructor. Called when a Child is generated. 
            /// </summary>
            /// <param name="animations">Animation class, with all values.</param>
            /// <param name="xml">Xml class, with xml functions</param>
            /// <param name="parentPos">Position of the parent - used to detect where the child should be positioned</param>
            /// <param name="parentFlipped">If parent is flipped. If true, the child image will also be flipped</param>
            /// <param name="parentDisplay">Display Index of the parent. Put the child on same screen</param>
        private FormCompanion(
            Animations animations,
            Xml xml,
            FormCompanion parent,
            Point parentPos,
            bool parentFlipped,
            int parentDisplay,
            int depth,
            ChildBudget budget)
        {
            Animations = animations;
            Xml = xml;
            parentPet = parent;
            parentPosition = parentPos;
            parentWasFlipped = parentFlipped;
            childDepth = depth;
            childBudget = budget;
            DisplayIndex = parentDisplay;
			IsMovingLeft = !parentFlipped;
            InitializeComponent();
            ConfigureTransparencyMode();
            Visible = false;            // Is invisible at beginning (we don't know where this sprite should be positioned)
            SetPetOpacity(0.0);
        }

            /// <summary>
            /// With this overridden function, it is possible to remove the application from the ALT-TAB list.
            /// This, because it is not nice to see 10 times the same sheep when you press ALT-TAB (with 10 sheeps walking on your screen).
            /// If this form is a child, remove the possibility to interact with this form.
            /// See: https://msdn.microsoft.com/en-us/library/windows/desktop/ff700543(v=vs.85).aspx
            /// </summary>
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;

                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW             <- remove from ALT-TAB list
                cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST                <- set on TopMost
                cp.ExStyle |= 0x00080000; // WS_EX_LAYERED                <- increase paint performance
                //cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT            <- Do not draw window -> unclickable
                //cp.Style |= 0x80000000; // WS_POPUP

                if (Name.IndexOf("child") == 0)
                {
                    cp.ExStyle |= 0x08000000;   //WS_EX_NOACTIVATE  <- prevent focus when created
                }
                return cp;
            }
        }

        /// <summary>
        /// With this overridden function, it is possible to prevent the form to get the focus once created.
        /// </summary>
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        /// <summary>
        /// Once the form was created, this is the next function to call.
        /// It will set the size of the pet. Form will still be invisible because it has an opacity of 0.0.
        /// </summary>
        /// <param name="w">Single frame width</param>
        /// <param name="h">Single frame height</param>
        public void Show(int w, int h)
        {
            Width = w;
            Height = h;

            pictureBox1.Width = w;
            pictureBox1.Height = h;
            pictureBox1.Top = 0;
            pictureBox1.Left = 0;
            pictureBox1.Tag = 0;

			AnimationStep = 0;

            Show();
        }

        private Image GetSpriteFrame(int index)
        {
            if (Xml == null)
                throw new InvalidOperationException("Pet sprite frames are unavailable.");
            return Xml.GetSpriteFrame(index, !IsMovingLeft);
        }

        /// <summary>Intended window opacity in [0,1]. For colour-key pets this mirrors Form.Opacity;
        /// for alpha pets it is folded into the per-pixel push as the source constant alpha.</summary>
        private double petOpacity = 1.0;

        /// <summary>The last frame handed to the layered (alpha) window, re-pushed when opacity
        /// changes. Owned by the shared <see cref="Xml"/> sprite store -- never disposed here.</summary>
        private Image lastLayeredFrame;

        /// <summary>
        /// Configure the transparency mode once, right after InitializeComponent. Colour-key pets keep
        /// the WinForms TransparencyKey path unchanged. Alpha pets (&lt;transparency&gt;Alpha) clear the
        /// key so WinForms never drives the layered-window attributes; FormCompanion then pushes each frame
        /// with per-pixel alpha via <see cref="PushLayeredFrame"/>. WS_EX_LAYERED is already forced in
        /// <see cref="CreateParams"/>, which is what makes UpdateLayeredWindow legal here.
        /// </summary>
        private void ConfigureTransparencyMode()
        {
            if (Xml != null && Xml.UsesAlpha)
            {
                TransparencyKey = Color.Empty;   // do not let WinForms colour-key the window
                BackColor = Color.Black;         // unused under UpdateLayeredWindow
            }
        }

        /// <summary>
        /// Set the pet's opacity through the correct path for its transparency mode. Alpha pets must
        /// never touch Form.Opacity -- doing so hands the layered window back to WinForms'
        /// SetLayeredWindowAttributes and fights UpdateLayeredWindow -- so they fold opacity into the
        /// next per-pixel push instead and re-push the current frame immediately.
        /// </summary>
        private void SetPetOpacity(double value)
        {
            double clamped = Math.Max(0.0, Math.Min(1.0, value));
            if (Xml != null && Xml.UsesAlpha)
            {
                bool changed = clamped != petOpacity;
                petOpacity = clamped;
                if (changed && lastLayeredFrame != null) PushLayeredFrame(lastLayeredFrame);
            }
            else
            {
                petOpacity = clamped;
                Opacity = clamped;
            }
        }

        /// <summary>
        /// Show the current animation frame. Colour-key pets assign it to the child PictureBox as
        /// before; alpha pets blit it to the layered window with per-pixel alpha.
        /// </summary>
        private void RenderCurrentFrame(Image frame)
        {
            if (Xml != null && Xml.UsesAlpha)
            {
                lastLayeredFrame = frame;
                PushLayeredFrame(frame);
            }
            else
            {
                pictureBox1.Image = frame;
            }
        }

        /// <summary>
        /// Push a 32-bpp premultiplied frame onto this window with per-pixel alpha (ULW_ALPHA). Alpha
        /// pets only. The surface is positioned at the window's current top-left; the current
        /// <see cref="petOpacity"/> becomes the source constant alpha. The frame bitmap is shared and
        /// owned by the sprite store, so only the temporary GDI HBITMAP/DCs are freed here.
        /// </summary>
        private void PushLayeredFrame(Image frame)
        {
            if (!IsHandleCreated) return;
            var bmp = frame as Bitmap;
            if (bmp == null) return;

            IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
            IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                hBitmap = bmp.GetHbitmap(Color.FromArgb(0));   // preserves the premultiplied alpha
                oldBitmap = NativeMethods.SelectObject(memDc, hBitmap);

                var size = new NativeMethods.SIZE(bmp.Width, bmp.Height);
                var pointSource = new NativeMethods.POINT(0, 0);
                var topPos = new NativeMethods.POINT(Left, Top);
                var blend = new NativeMethods.BLENDFUNCTION
                {
                    BlendOp = 0,        // AC_SRC_OVER
                    BlendFlags = 0,
                    SourceConstantAlpha = (byte)Math.Round(petOpacity * 255.0),
                    AlphaFormat = 1     // AC_SRC_ALPHA
                };
                NativeMethods.UpdateLayeredWindow(
                    Handle, screenDc, ref topPos, ref size,
                    memDc, ref pointSource, 0, ref blend, 0x02 /* ULW_ALPHA */);
            }
            finally
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
                if (hBitmap != IntPtr.Zero)
                {
                    NativeMethods.SelectObject(memDc, oldBitmap);
                    NativeMethods.DeleteObject(hBitmap);
                }
                NativeMethods.DeleteDC(memDc);
            }
        }

        private void FlipOrientation()
        {
            IsMovingLeft = !IsMovingLeft;
        }

        /// <summary>
        /// Turn to face the pointer. Sets facing outright rather than toggling, because "look at the mouse"
        /// is an absolute direction and a toggle would be wrong half the time.
        ///
        /// Compares against the CHARACTER's centre, not the window's: a converted shimeji floats inside a
        /// padded cell (Hornet's sprite sits far right of a 256px cell), so using the window centre would make
        /// the pet look the wrong way whenever the cursor sat between the two.
        /// </summary>
        private void FaceTheCursor()
        {
            try
            {
                SpriteInsets ins = GetSpriteInsets();
                double characterCentreX = PositionX + ins.Left + ins.Width / 2.0;
                // Through ShouldFaceLeft rather than inline: a copy of the comparison here would make the
                // self-test's assertions about the rule true of a function nothing calls.
                IsMovingLeft = ShouldFaceLeft(Cursor.Position.X, characterCentreX);
            }
            catch { /* a facing change must never break the animation */ }
        }

        /// <summary>Pure, so the facing rule can be asserted without a form or a mouse.</summary>
        internal static bool ShouldFaceLeft(double cursorX, double characterCentreX)
        {
            return cursorX < characterCentreX;
        }

        /// <summary>
        /// Whether an edge that declared <paramref name="chosenOnly"/> is asking the pet to GRIP the window's
        /// side, and which side.
        ///
        /// Deliberately an exact match on the discriminator rather than a bit test. An edge saying
        /// <c>only="window"</c> carries the WINDOW bit and fires here too, but it is the old wildcard, written
        /// when a window had one undifferentiated edge, and it means "do something at a window" -- not "hang
        /// off the side of it". 955 of those ship in the hand-authored pets. A bit test would have recruited
        /// every one of them into a behaviour their authors never asked for.
        ///
        /// Pure and internal so the opt-in rule can be asserted without a window on screen.
        /// </summary>
        internal static WindowGrip GripFor(TNextAnimation.TOnly chosenOnly)
        {
            if (chosenOnly == TNextAnimation.TOnly.WINDOW_LEFT) return WindowGrip.Left;
            if (chosenOnly == TNextAnimation.TOnly.WINDOW_RIGHT) return WindowGrip.Right;
            if (chosenOnly == TNextAnimation.TOnly.WINDOW_BOTTOM) return WindowGrip.Bottom;
            return WindowGrip.None;
        }

        /// <summary>
        /// Where a pet hanging from a window's underside sits: its visible TOP against the window's bottom
        /// edge. Pure, and the transpose of <see cref="GripPositionX"/> -- the ceiling compositor
        /// top-anchors these poses, so the sprite's own top padding is what has to be discounted.
        /// </summary>
        internal static double GripPositionY(int windowBottom, double insetTop)
        {
            return windowBottom - insetTop;
        }

        /// <summary>
        /// Where a gripping pet's window-edge x-coordinate is, given the window rect and the sprite's own
        /// padding. Pure: the arithmetic is the same two lines the screen-edge cling uses, and getting the
        /// inset on the wrong side is the mistake that puts a pet's transparent margin against the window and
        /// the character itself a hundred pixels away.
        /// </summary>
        internal static double GripPositionX(WindowGrip grip, int windowLeft, int windowRight, int formWidth, double insetLeft, double insetRight)
        {
            if (grip == WindowGrip.Right) return windowRight - formWidth + insetRight;
            return windowLeft - insetLeft;
        }

        /// <summary>
        /// Take hold of a window's side, if that is what the chosen edge asked for. Resets the vertical
        /// follow tracker: carrying a reading over from a PREVIOUS grip would jerk the pet by the difference
        /// between two unrelated windows' top edges on its first tick.
        /// </summary>
        private void BeginWindowGrip(TNextAnimation.TOnly chosenOnly)
        {
            windowGrip = GripFor(chosenOnly);
            _gripLastWindowTop = int.MinValue;
        }

        /// <summary>
        /// Must a window grip be dropped because the pet is entering this animation?
        ///
        /// Two reasons, and the second is the one that was missing.
        ///
        /// **Entering `fall` means let go.** <see cref="ReleaseWindowGrip"/> already implements letting go BY
        /// playing the fall animation, so arriving at fall from anywhere else has to mean the same thing. It
        /// did not, and a pet reaching fall through a ceiling pose's own &lt;next&gt; edge kept its grip.
        ///
        /// **An UNDERSIDE grip cannot survive an animation that moves vertically.** That branch of NextStep
        /// pins the pet's y to 0 (the pin IS the follow), and both of its release conditions test y, so with y
        /// zeroed neither can ever fire: the trap is structural rather than a missed case. A pose the pet can
        /// legitimately hang in has no vertical velocity, which the converter's own self-test asserts, so
        /// "wants to move vertically" is exactly the set that must let go. Left/Right grips are deliberately
        /// exempt: climbing DOWN a window's side is vertical motion and is the whole point of them.
        ///
        /// Pure and static so it can be asserted directly. The bug it fixes was invisible in every structural
        /// check the pet graph has, because the graph was correct and the engine did not honour it.
        /// </summary>
        internal static bool GripMustRelease(WindowGrip grip, bool enteringFall, double startY, double endY)
        {
            if (grip == WindowGrip.None) return false;
            if (enteringFall) return true;
            return grip == WindowGrip.Bottom && (startY != 0.0 || endY != 0.0);
        }

        /// <summary>
        /// Let go of a window's side and fall. One place, because a grip that is cleared without also clearing
        /// <see cref="hwndWindow"/> leaves the pet in a state nothing else in NextStep expects: still "on" a
        /// window, no longer pinned to it.
        /// </summary>
        private void ReleaseWindowGrip(bool startFalling)
        {
            windowGrip = WindowGrip.None;
            hwndWindow = (IntPtr)0;
            if (startFalling && Name.IndexOf("child") < 0) SetNewAnimation(Animations.AnimationFall);
        }

        internal Image SpriteFrameForDiagnostics(int index)
        {
            return GetSpriteFrame(index);
        }

        internal bool IsMovingLeftForDiagnostics
        {
            get { return IsMovingLeft; }
        }

        internal void FlipOrientationForDiagnostics()
        {
            FlipOrientation();
        }

        internal FormCompanion CreateUnshownChildForDiagnostics()
        {
            FormCompanion child = new FormCompanion(
                Animations,
                Xml,
                this,
                Point.Empty,
                !IsMovingLeft,
                DisplayIndex,
                childDepth + 1,
                null);
            child.Name = "child-diagnostic";
            return child;
        }

        private int ValidDisplayIndex
        {
            get
            {
                int count = Screen.AllScreens.Length;
                if (count <= 0) return 0;
                if (DisplayIndex < 0 || DisplayIndex >= count) DisplayIndex = 0;
                return DisplayIndex;
            }
        }
        private Rectangle ScreenBounds { get { return Screen.AllScreens[ValidDisplayIndex].Bounds; } }
        private Rectangle ScreenArea { get { return Screen.AllScreens[ValidDisplayIndex].WorkingArea; } }
        internal Rectangle CaptureScreenBounds
        {
            get
            {
                if (!IsDisposed && IsHandleCreated)
                    return Screen.FromRectangle(Bounds).Bounds;
                return ScreenBounds;
            }
        }

        /// <summary>
        /// Once the form was created, resized and all images was set, this is the next function to call.<br />
        /// It will initialize all variables and start the first animation (SPAWN).
        /// </summary>
        /// <param name="first">If it is playing a spawn for the first time. Does not have any functionality for the moment.</param>
        public void Play(bool first, int forceSpawn = -1)
        {
            timer1.Enabled = false;                     // Stop the timer

			AnimationStep = 0;                         // First step
            hwndWindow = (IntPtr)0;                     // It is not over a window

            // Multiscreen
            if(Program.MyData.GetMultiscreen())
            {
                int oldDisplayIndex = DisplayIndex;
                // A pending relocation (fullscreen game on the pet's monitor) forces the target screen;
                // otherwise the spawn picks a random screen as before.
                // A PIN wins over both: it is an explicit instruction, and a pinned pet is never relocated
                // anyway (see PinnedDisplay's use in CheckFullScreen), so a pending relocation here would
                // mean something already went wrong.
                int pinned = PinnedDisplay;
                if (pinned >= 0)
                    DisplayIndex = pinned;
                else if (_forcedDisplayIndex >= 0 && _forcedDisplayIndex < Screen.AllScreens.Length)
                    DisplayIndex = _forcedDisplayIndex;
                else
                    DisplayIndex = new Random().Next(0, Screen.AllScreens.Length);
                if(oldDisplayIndex != DisplayIndex) // display changed, all computed values could be wrong
                {

                }
            }
            _forcedDisplayIndex = -1;   // consume the relocation request (also honored above when set)

            TSpawn spawn;
            if (forceSpawn < 0) spawn = Animations.GetRandomSpawn(); // Get a random SPAWN, to setting the form properties
            else
            {
                var k = Animations.SheepSpawn.Keys.ToList();
                spawn = Animations.SheepSpawn[k[forceSpawn]];
            }
            int spawnX = spawn.Start.X.GetValue(DisplayIndex);
            int spawnY = spawn.Start.Y.GetValue(DisplayIndex);
            spawnX = IsMovingLeft
                ? AnimationRuntimeLimits.ClampLocalPosition(spawnX, ScreenBounds.Width)
                : AnimationRuntimeLimits.MirrorLocalX(
                    spawnX,
                    ScreenBounds.Width,
                    pictureBox1.Width);
            spawnY = AnimationRuntimeLimits.ClampLocalPosition(
                spawnY,
                ScreenBounds.Height);
            Point spawnPosition = DesktopGeometry.MonitorLocalToVirtual(
                new Point(spawnX, spawnY), ScreenBounds);
            Left = spawnPosition.X;
            Top = spawnPosition.Y;
            pictureBox1.Left = 0;
            pictureBox1.Top = 0;
            Width = pictureBox1.Width;
            Height = pictureBox1.Height;
			PositionX = Left;
			PositionY = Top;
			OffsetY = 0.0;
            IsLeaving = false;
            SetNewAnimation(spawn.Next);                // Set next animation
            // A respawn must not walk back onto a monitor a fullscreen app owns. Play() used to show and
            // top-most unconditionally, which is how a spawn reached the screen over a game; CheckFullScreen
            // would then correct it a tick later at best, and (before the enforcement fix above) never at
            // worst. Deciding it HERE also removes the visible flash between spawning and the next scan.
            bool spawningOntoBlockedMonitor = MonitorIsBlockedNow();
            Visible = !spawningOntoBlockedMonitor;      // Now we can show the form
            _fullscreenHidden = spawningOntoBlockedMonitor;
            SetPetOpacity(0.0);                         // do not show first frame (as it is undefined)
            timer1.Enabled = true;                      // Enable the timer (interval is well known now)
            TopMost = !spawningOntoBlockedMonitor;      // new in 1.2.6
        }

			/// <summary>
			/// If this form is a child, this function is called instead of Play().
			/// It will initialize all variables and start the first animation using CHILD, not SPAWN.
			/// </summary>
			/// <param name="aniID">Animation playing by the parent (child will synchronize to this animation).</param>
			/// <param name="child">The child to play (more than 1 childs can be played at the same time).</param>
		public void PlayChild(int aniID, TChild child)
        {
            timer1.Enabled = false;                     // Stop the timer

			AnimationStep = 0;                          // First step
            hwndWindow = (IntPtr)0;                     // It is not over a window

            using (CreateExpressionContext())
            {
                int childX = child.Position.X.GetValue(DisplayIndex);
                int childY = child.Position.Y.GetValue(DisplayIndex);
                childX = IsMovingLeft
                    ? AnimationRuntimeLimits.ClampLocalPosition(childX, ScreenBounds.Width)
                    : AnimationRuntimeLimits.MirrorLocalX(
                        childX,
                        ScreenBounds.Width,
                        pictureBox1.Width);
                childY = AnimationRuntimeLimits.ClampLocalPosition(
                    childY,
                    ScreenBounds.Height);
                Point childPosition = DesktopGeometry.MonitorLocalToVirtual(
                    new Point(childX, childY), ScreenBounds);
                Left = childPosition.X;
                Top = childPosition.Y;
			    PositionX = Left;
			    PositionY = Top;
			    OffsetY = 0.0;
                // A child (the UFO ship among them) is shown by its own code path, so it needs the same
                // question Play() asks. Without it, a parent correctly hidden for a fullscreen game could
                // still put a visible child on top of it -- the child is a separate window and inherits
                // nothing.
                bool childOntoBlockedMonitor = MonitorIsBlockedNow();
                Visible = !childOntoBlockedMonitor;     // Now we can show this child
                _fullscreenHidden = childOntoBlockedMonitor;
                SetPetOpacity(1.0);
                IsLeaving = false;
                pictureBox1.Cursor = Cursors.Default;

                SetNewAnimationCore(child.Next);        // Set next animation to play
            }

            timer1.Enabled = true;                      // Enable timer (interval is known, now)
        }

            /// <summary>
            /// If application is closed, all forms have still 1 second to show something (change animation).
            /// </summary>
            /// <remarks>
            /// Kill, Sync, Drag and Fall are "Key-names" in the XML file. If you use one of them, this program will automatically run the animation linked to this names.
            /// </remarks>
        public void Kill()
        {
            CloseChildren();
            if (Animations.AnimationKill > 1)
            {
                SetNewAnimation(Animations.AnimationKill);
            }
            else
            {
                Close();
                Dispose();
            }
        }

            /// <summary>
            /// If user press the CANCEL button in the about box, all pets are synchronized executing the SYNC-animation.
            /// </summary>
            /// <remarks>
            /// Kill, Sync, Drag and Fall are "Key-names" in the XML file. If you use one of them, this program will automatically run the animation linked to this names.
            /// </remarks>
        public void Sync()
        {
            if (Animations.AnimationSync > 1)
                SetNewAnimation(Animations.AnimationSync);
        }

            /// <summary>
            /// Timer tick. The entire animation is droved through this timer. The interval is set in the XML animation file.
            /// </summary>
            /// <remarks>
            /// On each tick, the next step is called. If it fails an error message will be show and the animation will stop.
            /// </remarks>
        private void Timer1_Tick(object sender, EventArgs e)
        {
            timer1.Enabled = false;
            if (AnimationStep < 0) AnimationStep = 0;
            try
            {
                // Poll independently of motion direction. Stationary, upward-only, dragged, and
                // window-attached pets must all yield to a fullscreen foreground window.
                CheckFullScreen();
                NextStep();
                if (IsDisposed)
                {
                    timer1.Enabled = false;
                }
                else
                {
					AnimationStep++;
                    UpdateSpeechFollow();   // keep any active bubble anchored over the pet's mouth
                    timer1.Enabled = true;
                }
            }
            catch(Exception ex) // if form is closed timer could continue to tick (why?)
            {
                if(MessageBox.Show("Fatal Error: " + ex.Message + "\n----------\nPress Cancel for more info", "App error", MessageBoxButtons.OKCancel, MessageBoxIcon.Error) == DialogResult.Cancel)
                {
                    string seqIndex = "";
                    foreach (var item in CurrentAnimation.Sequence.Frames) seqIndex += item + ",";

                        MessageBox.Show(
                        "Current Animation ID: " + CurrentAnimation.ID + "\n" +
                        "Current Animation Name: " + CurrentAnimation.Name + "\n" +
                        "Current Animation Sequence: " + seqIndex + "\n"
                        );

                }
            }
        }

            /// <summary>
            /// After an animation is over and after a new animation was selected, this function will play the selected animation.
            /// </summary>
            /// <param name="id">Animation ID to play.</param>
        private void SetNewAnimation(int id)
        {
            using (CreateExpressionContext())
                SetNewAnimationCore(id);
        }

        private void SetNewAnimationCore(int id)
        {
            if (CurrentAnimation.ID == Animations.AnimationKill) return;
            if (id < 0)  // no animation found, spawn!
            {
                Play(false);
            }
            else
            {
				AnimationStep = -1;
                CurrentAnimation = Animations.GetAnimation(id);
                CurrentAnimation.UpdateValues(DisplayIndex);

                // Letting go has to work in BOTH directions. ReleaseWindowGrip implements "let go" by playing
                // the fall animation, but nothing did the inverse: a graph that transitioned INTO fall by its
                // own <next> edge kept the grip, and for the UNDERSIDE grip that is a permanent trap, because
                // that branch pins y to 0 and both of its release conditions test y. A pet that took the
                // ceiling poses' 24%-weighted edge to fall then hung under the window playing the falling
                // animation for ever, going nowhere. Measured on Hornet: 99% of window-underside grabs.
                if (GripMustRelease(windowGrip, id == Animations.AnimationFall,
                        CurrentAnimation.Start.Y.Value, CurrentAnimation.End.Y.Value))
                    hwndWindow = (IntPtr)0;   // the property clears windowGrip and the follow tracker

                // faceCursor: aim at the pointer as the animation BEGINS. `flip` is applied at the sequence
                // END instead (that is how `turn` reverses after playing its frames), but a gaze pose has to
                // be aimed before it is held, not after. Deliberately once on entry rather than tracked per
                // tick: the source does the same, re-entering its look action every few seconds to re-aim,
                // so the pet glances rather than swivelling continuously.
                // Sequence is a struct, so there is no null to guard; Action is simply empty when absent.
                if (string.Equals(CurrentAnimation.Sequence.Action, "faceCursor", StringComparison.OrdinalIgnoreCase))
                    FaceTheCursor();

                // v.1.2.6: this will steal taskbar focus and the tray menu will disappear. So this should not be used too often.
                if (Program.MyData.GetStealTaskbarFocus() &&
                    hwndFullscreenWindow == IntPtr.Zero &&
                    CurrentAnimation.Start.OffsetY != 0 &&
                    CurrentAnimation.Start.X.Value != 0)
                {
                    TopMost = true; // bring to top again on each new animation
                }

                // Check if animation ID has a child. If so, the child will be created.
                if (Animations.HasAnimationChild(id))
                {
                    PruneClosedChildren();
                    if (childDepth < MaximumChildDepth)
                    {
						foreach (TChild childInfo in Animations.GetAnimationChild(id))
						{
                            if (childBudget == null || !childBudget.TryAcquire())
                            {
                                StartUp.AddDebugInfo(
                                    StartUp.DEBUG_TYPE.warning,
                                    "active child-pet limit reached");
                                break;
                            }

                            Point parentVirtual = new Point(
                                AnimationRuntimeLimits.ClampFormCoordinate(PositionX),
                                AnimationRuntimeLimits.ClampFormCoordinate(PositionY + OffsetY));
                            Point parentLocal = DesktopGeometry.VirtualToMonitorLocal(
                                parentVirtual, ScreenBounds);
                            FormCompanion child = null;
                            try
                            {
							    child = new FormCompanion(
                                    Animations,
                                    Xml,
                                    this,
                                    parentLocal,
                                    !IsMovingLeft,
                                    DisplayIndex,
                                    childDepth + 1,
                                    childBudget);
                                child.Name = "child" + (childDepth + 1).ToString();
                                childs.Add(child);
							    child.Show(
                                    Math.Max(1, pictureBox1.Width),
                                    Math.Max(1, pictureBox1.Height));
							    child.PlayChild(id, childInfo);
                            }
                            catch
                            {
                                if (child != null)
                                {
                                    childs.Remove(child);
                                    child.childOwnershipReleased = true;
                                    try { child.Close(); } catch { }
                                    try { child.Dispose(); } catch { }
                                }
                                childBudget.Release();
                                throw;
                            }
                        }
                    }
                }

                timer1.Interval = AnimationRuntimeLimits.ClampInterval(
                    CurrentAnimation.Start.Interval.Value);
            }
        }

            /// <summary>
            /// The most important function. Each movement step is managed by this function:<br />
            /// Will calculate how much and where a pet should be positioned in the next step.<br />
            /// This function is called from <see cref="Timer1_Tick(object, EventArgs)"/>.
            /// </summary>
        private void NextStep()
        {
            Rectangle monitorBounds = ScreenBounds;
            Rectangle workArea = ScreenArea;
            double workRight =
                (long)workArea.X + Math.Max(0L, (long)workArea.Width);
            double workBottom =
                (long)workArea.Y + Math.Max(0L, (long)workArea.Height);

            // A leaving animation temporarily shrinks the borderless form to the visible
            // slice of its sprite. Physics and border detection must always work from the
            // full sprite dimensions on the following tick.
            if (IsLeaving)
            {
                Width = Math.Max(1, pictureBox1.Width);
                Height = Math.Max(1, pictureBox1.Height);
                pictureBox1.Left = 0;
                pictureBox1.Top = 0;
                Left = AnimationRuntimeLimits.ClampFormCoordinate(PositionX);
                Top = AnimationRuntimeLimits.ClampFormCoordinate(PositionY + OffsetY);
            }

            int totalSteps = Math.Max(1, CurrentAnimation.Sequence.TotalSteps);
            int lastStep = AnimationRuntimeLimits.LastStepIndex(totalSteps);
            int frameStep = Math.Max(0, Math.Min(AnimationStep, lastStep));
            int interpolationSteps =
                AnimationRuntimeLimits.InterpolationSteps(totalSteps);

                // If there is no repeat, we don't need to calculate the frame index.
            int sequenceFrameIndex = AnimationRuntimeLimits.SequenceFrameIndex(
                frameStep,
                CurrentAnimation.Sequence.Frames.Count,
                CurrentAnimation.Sequence.RepeatFrom);
            // While DRAGGING, a multi-frame drag animation is a SWING ARC, not a timed loop: its frames run
            // from "body trailing far left of the cursor" to "far right", and the original picks between them
            // by where the pet has lagged to. So drive it from the hand, not the clock.
            if (IsDragging && CurrentAnimation.Sequence.Frames.Count > 1)
                sequenceFrameIndex = DragSwingFrameIndex(CurrentAnimation.Sequence.Frames.Count);
            RenderCurrentFrame(
                GetSpriteFrame(CurrentAnimation.Sequence.Frames[sequenceFrameIndex]));

                // Get interval, opacity and offset interpolated from START and END values.
            long interval = CurrentAnimation.Start.Interval.Value +
                ((long)CurrentAnimation.End.Interval.Value -
                 CurrentAnimation.Start.Interval.Value) * frameStep / interpolationSteps;
            timer1.Interval = AnimationRuntimeLimits.ClampInterval(
                interval > int.MaxValue ? int.MaxValue :
                interval < int.MinValue ? int.MinValue : (int)interval);
            SetPetOpacity(Math.Max(
                0.0,
                Math.Min(
                    1.0,
                    CurrentAnimation.Start.Opacity +
                    (CurrentAnimation.End.Opacity - CurrentAnimation.Start.Opacity) *
                    frameStep / interpolationSteps)));
			OffsetY = CurrentAnimation.Start.OffsetY +
                (double)(CurrentAnimation.End.OffsetY - CurrentAnimation.Start.OffsetY) *
                frameStep / interpolationSteps;

                // If dragging is enabled, move the pet to the mouse position.
            if (IsDragging)
            {
                // Self-heal a drag whose MouseUp never arrived. PictureBox1_MouseUp is the only other thing
                // that clears IsDragging, so anything stealing mouse capture mid-drag (a delayed screenshot
                // tool, the lock screen, a UAC prompt, an RDP reconnect) welded the pet to the cursor with no
                // way to put it down. Control.MouseButtons reads GLOBAL button state and needs no capture, so
                // it still sees the release we were never told about.
                if ((Control.MouseButtons & MouseButtons.Left) == 0)
                {
                    // Same two steps the real MouseUp takes, in the same order.
                    if (Name.IndexOf("child") < 0) SetNewAnimation(Animations.AnimationFall);
                    EndDrag();      // next tick runs normal physics on the fall animation
                    return;
                }
                TrackDragSwing();
                // Grab the CHARACTER, not the padded window. Centring on Width alone left the cursor ~77px
                // off Hornet, whose sprite sits far to the right inside its 256px cell.
                SpriteInsets grab = GetSpriteInsets();
                PositionX = Left = Cursor.Position.X - (int)Math.Round(grab.Left + grab.Width / 2);
                PositionY = Top = Cursor.Position.Y - (int)Math.Round(grab.Top) - 2;
                return;
            }
            
            double x = CurrentAnimation.Start.X.Value;
            double y = CurrentAnimation.Start.Y.Value;
            // if TotalSteps is more than 1, we have to interpolate START and END values)
            if (CurrentAnimation.Sequence.TotalSteps > 1)
            {
                x += ((CurrentAnimation.End.X.Value - CurrentAnimation.Start.X.Value) * (double)frameStep / interpolationSteps);
                y += ((CurrentAnimation.End.Y.Value - CurrentAnimation.Start.Y.Value) * (double)frameStep / interpolationSteps);
            }
                // If a new animation need to be started
            bool bNewAnimation = false;
                // If animation is leaving screen, cut the form so that it is not visibile on multiscreens
            bool bLeavingScreen = false;
                // If the pet is "flipped", mirror the movement
            if (!IsMovingLeft) x = -x;

                // Contact the border with the CHARACTER, not the sprite window. A converted shimeji floats
                // inside a padded cell (Hornet: 176px of transparent padding on the left of a 256px cell), so
                // comparing raw window edges turned her around while she was still visibly inland. Computed
                // once per tick; SpriteBounds caches per Image, so this is a dictionary hit after the first
                // time a frame is seen. Hand-authored pets fill their frame and get zero insets, i.e. the
                // previous behaviour exactly.
            SpriteInsets ins = GetSpriteInsets();

                // Gripping the SIDE of a window. The pet is pinned to a moving target, so the rect is re-read
                // every tick rather than trusted from when the grip started: a window can be dragged, resized,
                // minimised or closed underneath it, and a pet still pinned to where the window used to be is
                // the most obviously broken thing this feature could do.
            NativeMethods.RECT gripRect = default(NativeMethods.RECT);
            bool gripping = false;
            if (windowGrip != WindowGrip.None)
            {
                if (hwndWindow == (IntPtr)0 ||
                    !NativeMethods.IsWindowVisible(hwndWindow) ||
                    !NativeMethods.GetWindowRect(new HandleRef(this, hwndWindow), out gripRect) ||
                    gripRect.Right <= gripRect.Left || gripRect.Bottom <= gripRect.Top)
                {
                    // The window is gone, hidden, or reports a degenerate rect (minimised windows do).
                    ReleaseWindowGrip(true);
                }
                else if (windowGrip == WindowGrip.Bottom)
                {
                    gripping = true;
                    // The UNDERSIDE is the transpose of a side grip: Y is pinned and the pet travels
                    // horizontally, so the vertical velocity is the meaningless one here. The ceiling poses
                    // it hangs with have vy=0 anyway (asserted in the converter's self-test).
                    y = 0;
                    PositionY = GripPositionY(gripRect.Bottom, ins.Top);
                    // No delta tracking: the pin IS the follow. A window dragged sideways is handled by the
                    // existing left/right border checks below, which fire at the new rect.
                }
                else
                {
                    gripping = true;
                    // Horizontal velocity is meaningless while gripping -- the pet is held against the frame.
                    // The wall animations it grips with have vx=0 anyway; zeroing it here means a skin that
                    // wired a moving animation into the grip slides along the glass instead of off it.
                    x = 0;
                    PositionX = GripPositionX(windowGrip, gripRect.Left, gripRect.Right, Width, ins.Left, ins.Right);
                    // Ride the window vertically too. Tracked as a DELTA rather than as a fixed offset from
                    // the window top, because the pet is climbing: its offset changes every tick by design,
                    // and only the part of the change the window caused should be added back.
                    if (_gripLastWindowTop != int.MinValue)
                        PositionY += gripRect.Top - _gripLastWindowTop;
                    _gripLastWindowTop = gripRect.Top;
                }
            }

            if(x < 0)   // moving left (detect left borders)
            {
                if (hwndWindow == (IntPtr)0)
                {
                    if (PositionX + ins.Left + x < workArea.X)    // left screen border!
                    {
                        int iBorderAnimation = Animations.SetNextBorderAnimation(CurrentAnimation.ID, TNextAnimation.TOnly.VERTICAL);
                        if (iBorderAnimation >= 0)
                        {
                            PositionX = workArea.X - ins.Left;
                            x = 0;
                            SetNewAnimation(iBorderAnimation);
                            bNewAnimation = true;
                        }
                        else
                        {
                            bLeavingScreen = true;
                        }
                    }
                }
                else
                {
                    if (NativeMethods.GetWindowRect(new HandleRef(this, hwndWindow), out NativeMethods.RECT rct))
                    {
                        if (PositionX + ins.Left + x < rct.Left)    // left window border!
                        {
                            // WINDOW as well as WINDOW_LEFT: the generic bit is what keeps every pet written
                            // before the edge was distinguishable behaving exactly as it did.
                            TNextAnimation.TOnly chosenOnly;
                            int iBorderAnimation = Animations.SetNextBorderAnimation(CurrentAnimation.ID, TNextAnimation.TOnly.WINDOW | TNextAnimation.TOnly.WINDOW_LEFT, out chosenOnly);
                            if (iBorderAnimation >= 0)
                            {
                                PositionX = rct.Left - ins.Left;
                                x = 0;
                                SetNewAnimation(iBorderAnimation);
                                bNewAnimation = true;
                                BeginWindowGrip(chosenOnly);
                            }
                            else
                            {
                                // Not anymore on the window. A pet that was STANDING on it simply stops
                                // tracking it and gravity takes over on the next tick -- but a pet that was
                                // GRIPPING has no gravity node, so leaving it in that animation strands it
                                // hanging in mid-air where the window edge used to be.
                                if (windowGrip != WindowGrip.None) ReleaseWindowGrip(true);
                                else hwndWindow = (IntPtr)0;
                            }
                        }
                    }
                }
            }
            else if (x > 0)   // moving right (detect right borders)
            {
                if (hwndWindow == (IntPtr)0)
                {
                    if (PositionX + x + Width - ins.Right > workRight)    // right screen border!
                    {

                        int iBorderAnimation = Animations.SetNextBorderAnimation(CurrentAnimation.ID, TNextAnimation.TOnly.VERTICAL);
                        if (iBorderAnimation >= 0)
                        {
                            PositionX = workRight - Width + ins.Right;
                            x = 0;
                            SetNewAnimation(iBorderAnimation);
                            bNewAnimation = true;
                        }
                        else
                        {
                            bLeavingScreen = true;
                        }
                    }
                }
                else
                {
                    if (NativeMethods.GetWindowRect(new HandleRef(this, hwndWindow), out NativeMethods.RECT rct))
                    {
                        if (PositionX + x + Width - ins.Right > rct.Right)    // right window border!
                        {
                            TNextAnimation.TOnly chosenOnly;
                            int iBorderAnimation = Animations.SetNextBorderAnimation(CurrentAnimation.ID, TNextAnimation.TOnly.WINDOW | TNextAnimation.TOnly.WINDOW_RIGHT, out chosenOnly);
                            if (iBorderAnimation >= 0)
                            {
                                PositionX = rct.Right - Width + ins.Right;
                                x = 0;
                                SetNewAnimation(iBorderAnimation);
                                bNewAnimation = true;
                                BeginWindowGrip(chosenOnly);
                            }
                            else
                            {
                                // Not anymore on the window. A pet that was STANDING on it simply stops
                                // tracking it and gravity takes over on the next tick -- but a pet that was
                                // GRIPPING has no gravity node, so leaving it in that animation strands it
                                // hanging in mid-air where the window edge used to be.
                                if (windowGrip != WindowGrip.None) ReleaseWindowGrip(true);
                                else hwndWindow = (IntPtr)0;
                            }
                        }
                    }
                }
            }
            if(bNewAnimation || bLeavingScreen)
            {
                // don't check anymore for y movement
            }
                // A gripping pet's vertical limits are the WINDOW's, not the screen's, and they are checked
                // before the screen ones because the window is inside the screen: falling through to the
                // taskbar test would let the pet climb straight past the frame it is supposed to be holding.
            else if (gripping)
            {
                // ins.Top + ins.Height is where the CHARACTER's feet are; SpriteInsets carries no Bottom
                // because every other caller wants the visible span rather than the padding under it.
                if (y > 0 && PositionY + ins.Top + ins.Height + y > gripRect.Bottom)
                {
                    // Climbed off the bottom of the window. There is nothing below to hold, so let go.
                    // Deliberately not a border transition: a pet that turned around here would climb the
                    // same three inches of window edge forever.
                    ReleaseWindowGrip(true);
                    bNewAnimation = true;
                }
                else if (y < 0 && PositionY + ins.Top + y < gripRect.Top)
                {
                    // Reached the top of the window, which is a surface the pet can stand on. WINDOW_TOP is
                    // the same situation FallDetect raises when it lands there from above, so a pet needs no
                    // extra vocabulary to come over the lip.
                    PositionY = gripRect.Top - Height;
                    OffsetY = 0;
                    y = 0;
                    int iBorderAnimation = Animations.SetNextBorderAnimation(CurrentAnimation.ID, TNextAnimation.TOnly.WINDOW | TNextAnimation.TOnly.WINDOW_TOP);
                    if (iBorderAnimation >= 0)
                    {
                        SetNewAnimation(iBorderAnimation);
                        windowGrip = WindowGrip.None;   // standing on the top now, still on the same window
                    }
                    else
                    {
                        // Nothing to do up here. Let go rather than hover at the corner.
                        ReleaseWindowGrip(true);
                    }
                    bNewAnimation = true;
                }
            }
            else if(y > 0)   // moving down (detect taskbar and windows)
            {
                double bottomY = workBottom;

                if (PositionY + y > bottomY - Height) // border detected!
                {
                    int iBorderAnimation = Animations.SetNextBorderAnimation(CurrentAnimation.ID, TNextAnimation.TOnly.TASKBAR);
                    if (iBorderAnimation >= 0)
                    {
                        PositionY = bottomY - Height;
                        OffsetY = 0;
                        y = 0;
                        SetNewAnimation(iBorderAnimation);
                        bNewAnimation = true;
                    }
                    else
                    {
                        bLeavingScreen = true;
                    }
                }
                else
                {
                    WindowTopHit windowHit = FallDetect(y);
                    if (windowHit.Found)
                    {
                        int iBorderAnimation = Animations.SetNextBorderAnimation(CurrentAnimation.ID, TNextAnimation.TOnly.WINDOW | TNextAnimation.TOnly.WINDOW_TOP);
                        if (iBorderAnimation >= 0)
                        {
                            PositionY = windowHit.Top - Height;
                            OffsetY = 0;
                            y = 0;
                            SetNewAnimation(iBorderAnimation);
                            bNewAnimation = true;
                            if (CurrentAnimation.Start.Y.Value != 0)
                            {
                                hwndWindow = (IntPtr)0;
                            }
                        }
                    }
                }
            }
            else if(y < 0)  // moving up, detect upper screen border and window undersides
            {
                // The underside of a window is checked FIRST, for the same reason the window top is checked
                // before the taskbar: a window is inside the screen, so testing the screen border first would
                // let a jumping pet pass straight through one on its way to the top of the display.
                WindowTopHit underside = hwndWindow == (IntPtr)0 ? RiseDetect(y, ins) : WindowTopHit.None;
                if (underside.Found)
                {
                    TNextAnimation.TOnly chosenOnly;
                    int iBorderAnimation = Animations.SetNextBorderAnimation(CurrentAnimation.ID, TNextAnimation.TOnly.WINDOW | TNextAnimation.TOnly.WINDOW_BOTTOM, out chosenOnly);
                    WindowGrip hang = GripFor(chosenOnly);
                    if (iBorderAnimation >= 0 && hang == WindowGrip.Bottom)
                    {
                        PositionY = GripPositionY(underside.Top, ins.Top);
                        OffsetY = 0;
                        y = 0;
                        SetNewAnimation(iBorderAnimation);
                        BeginWindowGrip(chosenOnly);
                        bNewAnimation = true;
                    }
                    else
                    {
                        // Nothing wants to hang there. RiseDetect claimed the handle on the way in, so give
                        // it back: leaving it set would make the pet think it is standing on a window it is
                        // merely underneath, and the gravity branch would start following that window.
                        hwndWindow = (IntPtr)0;
                    }
                }
                if (bNewAnimation)
                {
                    // grabbed the underside; the screen border is not also a thing that happened
                }
                else if (PositionY + y < workArea.Y) // border detected!
                {
                    int iBorderAnimation = Animations.SetNextBorderAnimation(CurrentAnimation.ID, TNextAnimation.TOnly.HORIZONTAL);
                    if (iBorderAnimation >= 0)
                    {
                        PositionY = workArea.Y;
                        y = 0;
                        SetNewAnimation(iBorderAnimation);
                        bNewAnimation = true;
                    }
                    else
                    {
                        bLeavingScreen = true;
                    }
                }
            }

            if (AnimationStep >= lastStep) // the final declared frame was rendered; animation is over
            {
                int iNextAni;
                if(CurrentAnimation.Sequence.Action == "flip")
                {
                    // Select the shared, lazily materialized mirrored view. Never mutate the
                    // Xml-owned originals because every root and child borrows the same frames.
                    FlipOrientation();
                }
                if(hwndWindow != (IntPtr)0)
                {
                    iNextAni = Animations.SetNextSequenceAnimation(CurrentAnimation.ID, TNextAnimation.TOnly.WINDOW);
                }
                else
                {
                    // If the logical full sprite is outside the monitor, spawn it again.
                    // The physical form can be a one-pixel clipped anchor and must not be
                    // used for this decision.
                    double candidateX = AnimationRuntimeLimits.ClampVirtualPosition(
                        PositionX + x,
                        workArea.X,
                        workArea.Width);
                    double candidateY = AnimationRuntimeLimits.ClampVirtualPosition(
                        PositionY + y,
                        workArea.Y,
                        workArea.Height);
                    double candidateTop = AnimationRuntimeLimits.ClampVirtualPosition(
                        candidateY + OffsetY,
                        workArea.Y,
                        workArea.Height);
                    if (AnimationRuntimeLimits.IsSpriteFullyOutside(
                        candidateX,
                        candidateTop,
                        pictureBox1.Width,
                        pictureBox1.Height,
                        monitorBounds.X,
                        monitorBounds.Y,
                        monitorBounds.Width,
                        monitorBounds.Height))
                    {
                        iNextAni = -1;
                    }
                    else
                    {
                        iNextAni = Animations.SetNextSequenceAnimation(
                            CurrentAnimation.ID, 
                            PositionY + pictureBox1.Height + y >= workBottom - 2
                                ? TNextAnimation.TOnly.TASKBAR
                                : TNextAnimation.TOnly.NONE
                        );
                    }
                }
                if(CurrentAnimation.ID == Animations.AnimationKill)
                {
                    if (timer1.Tag == null) timer1.Tag = 1.0;

                    double op = double.Parse(timer1.Tag.ToString());
                    timer1.Tag = op - 0.1;
                    SetPetOpacity(op);
                    if (op <= 0.1)
                    {
                        Close();
                    }
                }
                else if (iNextAni >= 0)
                {
                    SetNewAnimation(iNextAni);
                    bNewAnimation = true;
                }
                else
                {
                        // Child doesn't have a spawn, they will be closed once the animation is over.
                    if(Name.IndexOf("child")==0)
                    {
                        StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info, "removing child");
                        Close();
                    }
                    else
                    {
                        Play(false);
                        return;
                    }
                }
            }
                // If there is a Gravity-Next animation, check if gravity is present.
            else if(CurrentAnimation.Gravity)
            {
                if(hwndWindow == (IntPtr)0)
                {
                    if(PositionY + y < workBottom - Height)
                    {
                        if(PositionY + y + 3 >= workBottom - Height) // allow 3 pixels to move without fall
                        {
                            y = workBottom - PositionY - Height;
                        }
                        else
                        {
                            SetNewAnimation(Animations.SetNextGravityAnimation(CurrentAnimation.ID, TNextAnimation.TOnly.NONE));
                            bNewAnimation = true;
                        }
                    }
                }
                else
                {
                    if (AnimationStep > 0 && CheckTopWindow(true))
                    {
                        if (CurrentAnimation.Start.X.Value != 0 && FollowWindow())
                        {
                            PositionX = Left;
                            PositionY = Top - OffsetY;
                            return;
                        }
                        else
                        {
                            hwndWindow = (IntPtr)0;
                            SetNewAnimation(Animations.SetNextGravityAnimation(CurrentAnimation.ID, TNextAnimation.TOnly.WINDOW));
                            bNewAnimation = true;
                        }
                    }
                }
            }

                // If a new animation was started, set the interval and the first animation frame image.
            if(bNewAnimation)
            {
                timer1.Interval = 1;    // execute immediately the first step of the next animation.
                //x = 0;                  // don't move the pet, if a new animation must be started
                //y = 0;                  //  if falling, set the pet to the new position
                RenderCurrentFrame(GetSpriteFrame(CurrentAnimation.Sequence.Frames[0]));
            }

			// Set the new pet position (and offset) in the screen. Keep logical
            // coordinates bounded before any conversion to WinForms integer bounds.
			PositionX = AnimationRuntimeLimits.ClampVirtualPosition(
                PositionX + x,
                workArea.X,
                workArea.Width);
			PositionY = AnimationRuntimeLimits.ClampVirtualPosition(
                PositionY + y,
                workArea.Y,
                workArea.Height);
            double renderTop = AnimationRuntimeLimits.ClampVirtualPosition(
                PositionY + OffsetY,
                workArea.Y,
                workArea.Height);

            int fullWidth = Math.Max(1, pictureBox1.Width);
            int fullHeight = Math.Max(1, pictureBox1.Height);
            int leftCut = AnimationRuntimeLimits.ClipCut(
                workArea.X - PositionX,
                fullWidth);
            int rightCut = AnimationRuntimeLimits.ClipCut(
                PositionX + fullWidth - workRight,
                fullWidth);
            int topCut = AnimationRuntimeLimits.ClipCut(
                workArea.Y - renderTop,
                fullHeight);
            int bottomCut = AnimationRuntimeLimits.ClipCut(
                renderTop + fullHeight - workBottom,
                fullHeight);
            bool hasCut =
                leftCut > 0 || rightCut > 0 || topCut > 0 || bottomCut > 0;

            // Derive clipping from absolute geometry on every tick. This also covers an
            // off-screen spawn moving inward and a leaving animation whose velocity
            // becomes zero; prior clipped dimensions are never reused cumulatively.
            // Alpha pets render through UpdateLayeredWindow (no child PictureBox to offset and
            // no form to shrink), so they skip form-resize clipping and always position full-size;
            // the only cost is a possible small overhang past a shared multi-monitor edge (v1).
            if (hasCut && !Xml.UsesAlpha)
            {
                IsLeaving = true;
                bool fullyClipped = false;
                int visibleWidth = Math.Max(0, fullWidth - leftCut - rightCut);
                int visibleHeight = Math.Max(0, fullHeight - topCut - bottomCut);

                if (visibleWidth > 0)
                {
                    Width = visibleWidth;
                    Left = AnimationRuntimeLimits.ClampFormCoordinate(
                        PositionX + leftCut);
                    pictureBox1.Left = -leftCut;
                }
                else
                {
                    fullyClipped = true;
                    Width = 1;
                    if (PositionX + fullWidth <= workArea.X)
                    {
                        Left = AnimationRuntimeLimits.ClampFormCoordinate(
                            (long)workArea.X - 1L);
                        pictureBox1.Left = -fullWidth;
                    }
                    else if (PositionX >= workRight)
                    {
                        Left = AnimationRuntimeLimits.ClampFormCoordinate(workRight);
                        pictureBox1.Left = fullWidth;
                    }
                    else
                    {
                        Left = AnimationRuntimeLimits.ClampFormCoordinate(workArea.X);
                        pictureBox1.Left = -leftCut;
                    }
                }

                if (visibleHeight > 0)
                {
                    Height = visibleHeight;
                    Top = AnimationRuntimeLimits.ClampFormCoordinate(
                        renderTop + topCut);
                    pictureBox1.Top = -topCut;
                }
                else
                {
                    fullyClipped = true;
                    Height = 1;
                    if (renderTop + fullHeight <= workArea.Y)
                    {
                        Top = AnimationRuntimeLimits.ClampFormCoordinate(
                            (long)workArea.Y - 1L);
                        pictureBox1.Top = -fullHeight;
                    }
                    else if (renderTop >= workBottom)
                    {
                        Top = AnimationRuntimeLimits.ClampFormCoordinate(workBottom);
                        pictureBox1.Top = fullHeight;
                    }
                    else
                    {
                        Top = AnimationRuntimeLimits.ClampFormCoordinate(workArea.Y);
                        pictureBox1.Top = -topCut;
                    }
                }

                if (fullyClipped)
                {
                    AnimationStep += Math.Max(
                        1,
                        CurrentAnimation.Sequence.Frames.Count / 3);
                }
            }
            else
            {
                IsLeaving = false;
                pictureBox1.Top = 0;
                pictureBox1.Left = 0;
                Width = fullWidth;
                Height = fullHeight;
                Left = AnimationRuntimeLimits.ClampFormCoordinate(PositionX);
                Top = AnimationRuntimeLimits.ClampFormCoordinate(renderTop);
            }

            
        }

            /// <summary>
            /// Detect if pet is still falling or if taskbar/window was detected.
            /// </summary>
            /// <param name="y">Y moves in pixels for the next step (function will detect if window/taskbar is inside the movement).</param>
            /// <returns>An explicit hit plus the virtual-screen Y coordinate of the window top.</returns>
        private WindowTopHit FallDetect(double y)
        {
            Dictionary<IntPtr, string> windows = new Dictionary<IntPtr, string>();
            NativeMethods.TITLEBARINFO titleBarInfo = new NativeMethods.TITLEBARINFO();
            titleBarInfo.cbSize = Marshal.SizeOf(titleBarInfo);

                // Enumerate all windows on the desktop.
            NativeMethods.EnumWindows(delegate (IntPtr hWnd, int lParam)
            {
                if (hWnd == Handle) return true;    // form itself, don't parse

                    // Enumerate only visible windows
                if (NativeMethods.IsWindowVisible(hWnd))
                {
                    StringBuilder sTitle = new StringBuilder(128);
                    NativeMethods.GetWindowText(hWnd, sTitle, 128);

                    // Sheep windows doesn't have a title bar, but we want detect if another pet is present
                    if (sTitle.ToString() == "Sheep") { }
                    // If there is no title bar, continue enumerating other windows
                    else if (!NativeMethods.GetTitleBarInfo(hWnd, ref titleBarInfo)) return true;
                    // If title bar is not visible, continue enumerating other windows
                    else if ((titleBarInfo.rgstate[0] & 0x00008000) > 0) // invisible
                        return true;
                    
                        // If window has a title, add this window to list
                    if (sTitle.Length > 0)
                    {
                        windows[hWnd] = sTitle.ToString();
                    }
                }
                return true;
            }, (IntPtr)0);

                // For each valid window found:
            foreach (KeyValuePair<IntPtr, string> window in windows)
            {
                    // Get size and position of window
                if (NativeMethods.GetWindowRect(new HandleRef(this, window.Key), out NativeMethods.RECT rct))
                {
                        // If vertical position is in the falling range and pet is over window and window is at least 20 pixels under the screen border
                    if (DesktopGeometry.CrossesDescendingBoundary(
                            PositionY + Height,
                            y,
                            rct.Top) &&
						PositionX >= rct.Left - Width / 2 && PositionX + Width <= rct.Right + Width / 2 &&
						PositionY > 20 + ScreenArea.Y)
                    {
                            // Pet need to walk over THIS window!
                        hwndWindow = window.Key;
                        currentWindowSize = rct;
						StringBuilder sTitle = new StringBuilder(128);
						NativeMethods.GetWindowText(hwndWindow, sTitle, 128);

						// If window is not covered by other windows, set this as current window for the pet.
						if (!CheckTopWindow(false))
                        {
								// Only if the option is set (this is an invasive functionality)
							if (Program.MyData.GetWindowForeground())
							{
								NativeMethods.ShowWindow(window.Key, 5);        // show window again
								NativeMethods.SetForegroundWindow(window.Key);  // set focus to window
							}
                            return WindowTopHit.At(rct.Top);               // return the position for the pet
                        }
                        else
                        {
                            hwndWindow = (IntPtr)0;                         // window is covered by other windows, reset handle
                        }
                    }
                }
            }
            return WindowTopHit.None;     // no windows detected.
        }

        /// <summary>
        /// The mirror of <see cref="FallDetect"/>: a RISING pet's head passing into the underside of a
        /// window. Returns the window's bottom edge, and sets <see cref="hwndWindow"/> to it, so the pet can
        /// hang there the way it can stand on a top edge.
        ///
        /// A separate walk rather than a parameter on FallDetect. The two share the enumeration but nothing
        /// else: the boundary is the opposite edge, the crossing test is the opposite direction, and the
        /// z-order question is different -- standing on a window asks "is anything covering the surface I am
        /// on", hanging under one asks nothing of the sort, because a window in front of it does not stop it
        /// being underneath.
        ///
        /// Requires the pet's whole width to be inside the window rather than FallDetect's half-width
        /// tolerance. A pet standing half off a window's edge reads as balancing; one hanging half off the
        /// corner of a window reads as broken.
        /// </summary>
        private WindowTopHit RiseDetect(double y, SpriteInsets ins)
        {
            if (y >= 0) return WindowTopHit.None;

            var windows = new Dictionary<IntPtr, string>();
            NativeMethods.TITLEBARINFO titleBarInfo = new NativeMethods.TITLEBARINFO();
            titleBarInfo.cbSize = Marshal.SizeOf(titleBarInfo);

            NativeMethods.EnumWindows(delegate (IntPtr hWnd, int lParam)
            {
                if (hWnd == Handle) return true;    // form itself, don't parse
                if (!NativeMethods.IsWindowVisible(hWnd)) return true;

                StringBuilder sTitle = new StringBuilder(128);
                NativeMethods.GetWindowText(hWnd, sTitle, 128);
                if (sTitle.ToString() == "Sheep") { }
                else if (!NativeMethods.GetTitleBarInfo(hWnd, ref titleBarInfo)) return true;
                else if ((titleBarInfo.rgstate[0] & 0x00008000) > 0) return true;   // invisible title bar

                if (sTitle.Length > 0) windows[hWnd] = sTitle.ToString();
                return true;
            }, (IntPtr)0);

            double headY = PositionY + ins.Top;
            foreach (KeyValuePair<IntPtr, string> window in windows)
            {
                NativeMethods.RECT rct;
                if (!NativeMethods.GetWindowRect(new HandleRef(this, window.Key), out rct)) continue;
                if (rct.Right <= rct.Left || rct.Bottom <= rct.Top) continue;   // minimised

                // A maximised window's bottom edge sits on the work area, i.e. right on top of a pet
                // standing on the taskbar, and without this it would grab the underside on the first tick
                // of every jump it ever made. The window has to be genuinely overhead.
                if (rct.Bottom >= ScreenArea.Y + ScreenArea.Height - 4) continue;

                if (!DesktopGeometry.CrossesAscendingBoundary(headY, y, rct.Bottom)) continue;
                if (PositionX + ins.Left < rct.Left) continue;
                if (PositionX + ins.Left + ins.Width > rct.Right) continue;

                hwndWindow = window.Key;
                currentWindowSize = rct;
                return WindowTopHit.At(rct.Bottom);
            }
            return WindowTopHit.None;
        }

        // Sentinel stored in hwndFullscreenWindow while the pet's monitor is blocked. The value is
        // only ever tested against IntPtr.Zero (re-topmost gate + speech suppression), never used as
        // a real window handle, so a marker is enough.
        private static readonly IntPtr FullscreenBlockedMarker = (IntPtr)1;

        /// <summary>
        /// If a fullscreen (borderless or exclusive) window covers the pet's monitor, move the pet to
        /// a free monitor -- or hide it when none is free -- so it never sits on top of a game. Unlike
        /// a plain foreground check this walks the z-order (see <see cref="FullscreenScan"/>), so a
        /// sheep that stole focus by being grabbed over a borderless game still detects the game.
        /// </summary>
        /// <summary>
        /// Is a fullscreen app occupying the monitor this pet is on, right now?
        ///
        /// Asked at SPAWN time, where the 300ms scan throttle is no help: a pet that respawns onto a blocked
        /// screen and waits for the next tick has already been seen over the game. Uses the same
        /// FullscreenScan the tick uses, so there is one detector and one policy.
        ///
        /// Answers FALSE on any failure. A pet that will not appear is worse than one that appears and is
        /// corrected a tick later, and this runs during spawn where throwing is not an option.
        /// </summary>
        /// <summary>
        /// The monitor this pet's TYPE is pinned to, or -1 when unpinned.
        ///
        /// A pin is deliberately stronger than "Allow multiple screens": that setting only decides whether an
        /// UNPINNED pet spawns on a random screen, whereas naming a monitor is an explicit instruction. A pin
        /// to a display that has been unplugged reads as unpinned, so the pet still appears somewhere.
        /// </summary>
        private int PinnedDisplay
        {
            get
            {
                try
                {
                    if (Program.MyData == null || Program.Mainthread == null) return -1;
                    // A child follows its parent; pinning it separately would tear a UFO off its sheep.
                    if (Name != null && Name.IndexOf("child") == 0) return -1;
                    // PetTypeId, NOT the petEntries registry. AddSheepCore calls Play() inside its
                    // initialize callback and only registers the pet AFTERWARDS, so a registry lookup here
                    // missed and fell back to the ACTIVE pet's id -- which is why a pet pinned to screen 2
                    // spawned on screen 1. PetTypeId comes from Animations, which is populated before the
                    // form is even constructed, so it is correct at spawn time.
                    string typeId = PetTypeId;
                    if (string.IsNullOrEmpty(typeId)) return -1;
                    return Program.MyData.GetPetMonitor(typeId, Screen.AllScreens.Length);
                }
                catch { return -1; }
            }
        }
        private bool MonitorIsBlockedNow()
        {
            try
            {
                Screen[] screens = Screen.AllScreens;
                if (screens.Length == 0) return false;
                bool[] blocked = FullscreenScan.BlockedMonitors(
                    Program.Mainthread != null ? Program.Mainthread.SheepHandles() : null);
                if (blocked == null || blocked.Length != screens.Length) return false;
                // Report it to the host too: this is a real scan, and letting it feed the shared state keeps a
                // module's IsFullscreenActive fresh at exactly the moment a pet is appearing.
                if (Program.Mainthread != null) Program.Mainthread.NoteFullscreenScan(blocked);

                string device = Screen.FromRectangle(Bounds).DeviceName;
                for (int i = 0; i < screens.Length; i++)
                    if (screens[i].DeviceName == device) return blocked[i];
                return false;
            }
            catch { return false; }
        }

        private void CheckFullScreen()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastFullscreenScanUtc).TotalMilliseconds < 300) return;  // decouple from frame rate
            _lastFullscreenScanUtc = now;

            Screen[] screens = Screen.AllScreens;
            if (screens.Length == 0) return;

            bool[] blocked;
            try
            {
                HashSet<IntPtr> pets =
                    Program.Mainthread != null ? Program.Mainthread.SheepHandles() : null;
                blocked = FullscreenScan.BlockedMonitors(pets);
            }
            catch { return; }
            if (blocked == null || blocked.Length != screens.Length) return;
            // Hand the whole picture to the host before narrowing to this pet's monitor. A module asking
            // "is a game running" means ANY monitor, not the one this particular pet happens to stand on.
            if (Program.Mainthread != null) Program.Mainthread.NoteFullscreenScan(blocked);

            int current = 0;
            string device = Screen.FromRectangle(Bounds).DeviceName;
            for (int i = 0; i < screens.Length; i++)
                if (screens[i].DeviceName == device) { current = i; break; }

            if (!blocked[current])
            {
                // Monitor is clear again: undo any hide/suppression and resume normal top-most.
                if (_fullscreenHidden) { _fullscreenHidden = false; if (!Visible) Visible = true; }
                if (hwndFullscreenWindow != IntPtr.Zero)
                {
                    hwndFullscreenWindow = IntPtr.Zero;
                    if (!TopMost) TopMost = true;
                    if (_speech != null && !_speech.IsDisposed)
                        _speech.SetFullscreenSuppressed(false);
                }
                return;
            }

            // Pet's monitor is blocked. Stop covering the game right away, then relocate or hide.
            hwndFullscreenWindow = FullscreenBlockedMarker;
            if (TopMost) TopMost = false;
            if (_speech != null && !_speech.IsDisposed)
                _speech.SetFullscreenSuppressed(true);

            // Children follow their parent's animation and must not re-spawn themselves.
            bool isChild = Name != null && Name.IndexOf("child") == 0;

            var monitors = new List<Rectangle>(screens.Length);
            for (int i = 0; i < screens.Length; i++) monitors.Add(screens[i].Bounds);
            // A PINNED pet never relocates: "Hornet on monitor 2" is an instruction, not a preference to be
            // overridden the first time a game starts. It hides instead and comes back when the screen frees.
            // An UNPINNED pet keeps the old behaviour and moves to a free monitor rather than vanishing.
            int target = (isChild || PinnedDisplay >= 0)
                ? -1
                : DesktopGeometry.ChooseRelocationTarget(current, monitors, blocked);

            if (target >= 0)
            {
                if ((now - _lastRelocateUtc).TotalMilliseconds < 1200) return;   // no rapid bouncing
                _lastRelocateUtc = now;
                _fullscreenHidden = false;
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(new Action(delegate { RelocateToDisplay(target); }));
            }
            else
            {
                // No free monitor (single screen, every screen blocked, or a child): hide instead.
                //
                // ENFORCED on every scan, not latched behind `!_fullscreenHidden`. The latch was a desync
                // waiting to happen and it happened: a hidden pet KEEPS TICKING, so its animation runs on
                // invisibly and can reach a respawn (`spawn_ship` in the sheep). Play() then sets Visible and
                // TopMost unconditionally, while `_fullscreenHidden` stayed true -- so this branch believed it
                // had already hidden the pet and never hid it again. Result: a UFO flying over a fullscreen
                // borderless game, permanently, because the very flag meant to keep it away said "done".
                //
                // Asking "is it visible?" instead of "did I hide it?" cannot desync: the window's own state is
                // the truth, and anything that shows the pet behind our back is corrected within one scan.
                _fullscreenHidden = true;
                if (Visible) Visible = false;
            }
        }

        /// <summary>Move the pet to <paramref name="target"/> and re-spawn there (a natural fall-in).</summary>
        private void RelocateToDisplay(int target)
        {
            if (IsDisposed) return;
            if (target < 0 || target >= Screen.AllScreens.Length) return;
            _fullscreenHidden = false;
            hwndFullscreenWindow = IntPtr.Zero;     // the target monitor is free; allow top-most again
            DisplayIndex = target;
            _forcedDisplayIndex = target;           // keep Play() from re-randomising under multiscreen
            Play(false);
        }

        private bool FollowWindow()
        {
            if (hwndWindow != IntPtr.Zero)
            {
				// Get window size and position of the current pet
				NativeMethods.RECT rctO;
                if (!NativeMethods.GetWindowRect(
                        new HandleRef(this, hwndWindow),
                        out rctO) ||
                    rctO.Right <= rctO.Left ||
                    rctO.Bottom <= rctO.Top)
                    return false;

				// window disappeared! Maybe it was closed.
				if (rctO.Top == 0 && rctO.Bottom == 0)
                {
                    return false;
                }

                if (currentWindowSize.Top != rctO.Top || currentWindowSize.Left != rctO.Left || currentWindowSize.Right != rctO.Right)
                {
                    // same width as before
                    if (rctO.Right - rctO.Left == currentWindowSize.Right - currentWindowSize.Left)
                    {
                        Top -= (currentWindowSize.Top - rctO.Top);
                        Left -= (currentWindowSize.Left - rctO.Left);
                    }
                    else // new width
                    {
                        int scaledLeft;
                        if (!DesktopGeometry.TryScaleWindowRelativeX(
                                Left,
                                currentWindowSize.Left,
                                currentWindowSize.Right,
                                rctO.Left,
                                rctO.Right,
                                out scaledLeft))
                            return false;
                        Top -= (currentWindowSize.Top - rctO.Top);
                        Left = scaledLeft;
                    }
                    currentWindowSize = rctO;
                    return true;
                }
            }
            return false;
        }

            /// <summary>
            /// Check if current window handler is still valid (if another window cover the visual of this window, it must not be used as window)
            /// </summary>
            /// <param name="bCheck">Check if it is still valid. Set false if window is not proofed, true if pet is already walking on a window => check if window is still valid.</param>
            /// <returns>True if window is still valid and present. False if window is not anymore there.</returns>
            /// <seealso cref="NativeMethods.GetWindow(IntPtr, int)"/>
            /// <seealso cref="NativeMethods.GetTitleBarInfo(IntPtr, ref NativeMethods.TITLEBARINFO)"/>
        private bool CheckTopWindow(bool bCheck)
        {
                // Check only if we have a valid window handler
            if (hwndWindow != IntPtr.Zero)
            {
				// Get window size and position of the current pet
				NativeMethods.GetWindowRect(new HandleRef(this, hwndWindow), out NativeMethods.RECT rctO);

				// If pet was walking on a window, check if window is still in the same position
				if (bCheck)
                {
                    if(currentWindowSize.Top != rctO.Top || currentWindowSize.Left != rctO.Left || currentWindowSize.Right != rctO.Right)
                    {
                        return true;
                    }
                }

                    // Get more informations about the current window title bar
                NativeMethods.TITLEBARINFO titleBarInfo = new NativeMethods.TITLEBARINFO();
                titleBarInfo.cbSize = Marshal.SizeOf(titleBarInfo);

                //Debug.WriteLine("Window TREE");
                
                // Get the handle to the first window (from user visual, in Z-order)
                IntPtr hwnd2 = NativeMethods.GetTopWindow((IntPtr)0);
                    // Loop until there are windows over the current window (in Z-Order)
                while (hwnd2 != (IntPtr)0)
                {
						// All windows up to the current window was parsed, now window is overlapping the current window
					if (hwnd2 == hwndWindow)
					{
                        //Debug.WriteLine("--XX Parsed all windows");
						return false;
					}

                    if (NativeMethods.IsWindowVisible(hwnd2))
                    {
                        StringBuilder sTitle = new StringBuilder(128);
                        NativeMethods.GetWindowText(hwnd2, sTitle, 128);

                        //Debug.WriteLine("--> " + sTitle);

                        // If window has a title bar
                        if (sTitle.Length > 0 && NativeMethods.GetTitleBarInfo(hwnd2, ref titleBarInfo))
                        {
                            // If window has a title name and a valid size and is not fullscreen
                            if (NativeMethods.GetWindowRect(new HandleRef(this, hwnd2), out NativeMethods.RECT rct) &&
                                (titleBarInfo.rcTitleBar.Bottom >= 0 || sTitle.ToString() == "sheep"))
                            {
                                //Debug.WriteLine("   -->  Pos:" + rct.Top + "," + rct.Left + " - Size:" + (rct.Right - rct.Left).ToString() + "," + (rct.Bottom - rct.Top).ToString());
                                if (rct.Top < rctO.Top && rct.Bottom > rctO.Top)
                                {
                                    if (rct.Left < PositionX && rct.Right > PositionX + 40/* && iAnimationStep > 4*/)
                                    {
                                        //Debug.WriteLine("   --> Window found!");
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                        // Get the handle to the next window (to user visual, in Z-order)
                    hwnd2 = NativeMethods.GetWindow(hwnd2, 2);
                }
            }
            return false;
        }
         
            /// <summary>
            /// Picture box fills the form, so mouse events are managed by this object: mouse pressed = pick pet. 
            /// </summary>
            /// <param name="sender">The caller object.</param>
            /// <param name="e">Mouse event values.</param>
        private void PictureBox1_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Name.IndexOf("child") < 0)
            {
                hwndWindow = (IntPtr)0;             // Remove window handles
                TopMost = false;
                // Re-assert topmost only when no fullscreen app owns this screen. Grabbing a pet must not be
                // a way to force it back over a game; CheckFullScreen sets the marker while blocked.
                TopMost = hwndFullscreenWindow == IntPtr.Zero;   // Set again the topmost
				IsDragging = true;                   // Flag it as dragging pet
                SetNewAnimation(Animations.AnimationDrag);  // Set the dragging animation (if present)
            }
            else if (e.Button == MouseButtons.Right && !StartUp.IsDebugActive())
            {
                // Poking the sheep (right-click) -> a fortune. Pass THIS pet: it is the one the user clicked,
                // and the host cannot recover that afterwards (it used to fall back to the first pet on
                // screen, so poking pet #5 was reported as pet #1).
                if (Program.Mainthread != null) Program.Mainthread.OnPetPoked(this);
            }
            else if(e.Button == MouseButtons.Right && StartUp.IsDebugActive())
            {
                ContextMenu cm = new ContextMenu();
                cm.MenuItems.Add("ID." + CurrentAnimation.ID + " - " + CurrentAnimation.Name).Enabled = false;
                cm.MenuItems.Add("-");
                MenuItem menuNext = cm.MenuItems.Add("Next");
                MenuItem menuBorder = cm.MenuItems.Add("Border");
                MenuItem menuGravity = cm.MenuItems.Add("Gravity");
                cm.MenuItems.Add("-");
                MenuItem menuSpawn = cm.MenuItems.Add("Spawns");

                List<TNextAnimation> list = Animations.GetNextAnimations(CurrentAnimation.ID, true, false, false);
                foreach (TNextAnimation ani in list)
                {
                    MenuItem menu = menuNext.MenuItems.Add("ID." + ani.ID + " - " + Animations.SheepAnimations[ani.ID].Name + "\t (Prob: " + ani.Probability + ") only:" + ani.only.ToString());
                    menu.Click += (ms, me) => { SetNewAnimation(ani.ID); };
                }
                if (list.Count == 0) menuNext.Enabled = false;

                list = Animations.GetNextAnimations(CurrentAnimation.ID, false, true, false);
                foreach (TNextAnimation ani in list)
                {
                    MenuItem menu = menuBorder.MenuItems.Add("ID." + ani.ID + " - " + Animations.SheepAnimations[ani.ID].Name + "\t (Prob: " + ani.Probability + ") only: " + ani.only.ToString());
                    menu.Click += (ms, me) => { SetNewAnimation(ani.ID); };
                }
                if (list.Count == 0) menuBorder.Enabled = false;

                list = Animations.GetNextAnimations(CurrentAnimation.ID, false, false, true);
                foreach (TNextAnimation ani in list)
                {
                    MenuItem menu = menuGravity.MenuItems.Add("ID." + ani.ID + " - " + Animations.SheepAnimations[ani.ID].Name + "\t (Prob: " + ani.Probability + ") only:" + ani.only.ToString());
                    menu.Click += (ms, me) =>{ SetNewAnimation(ani.ID); };
                }
                if (list.Count == 0) menuGravity.Enabled = false;

                List<TSpawn> listS = Animations.GetNextSpawns();
                foreach (TSpawn spa in listS)
                {
                    MenuItem menu = menuSpawn.MenuItems.Add("ID." + spa.Next + " - " + Animations.SheepAnimations[spa.Next].Name + "\t (Prob: " + spa.Probability + ")");
                    menu.Click += (ms, me) => 
                    {
                        //Top = ScreenBounds.Y + spa.Start.Y.GetValue(DisplayIndex);
                        //Left = ScreenBounds.X + spa.Start.X.GetValue(DisplayIndex);
                        //PositionX = Left;
                        //PositionY = Top;
                        //OffsetY = 0.0;
                        int spawnIndex = menu.Index;
                        Play(false, spawnIndex);
                    };
                }

                timer1.Enabled = false;

                cm.Collapse += (ms, me) =>
                {
                    timer1.Interval = 1;
                    timer1.Enabled = true;
                };

                pictureBox1.ContextMenu = cm;
                pictureBox1.ContextMenu.Show(pictureBox1, new Point(0,this.Top > 500 ? 0 : this.Height));
            }
        }

            /// <summary>
            /// Mouse released the pet.
            /// </summary>
            /// <param name="sender">Caller object.</param>
            /// <param name="e">Mouse event values.</param>
        private void PictureBox1_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Name.IndexOf("child") < 0)
            {
                SetNewAnimation(Animations.AnimationFall);
            }
            EndDrag();
        }

            /// <summary>
            /// Put the pet down: re-home it on whichever screen it was dropped on, then clear the drag flag.
            /// Shared by the real MouseUp and by NextStep's self-heal for a MouseUp that never arrived, so a
            /// recovered drag ends on exactly the same path as a normal one.
            /// </summary>
        private void EndDrag()
        {
            if (IsDragging)
            {
                // if it was dragged, check if the screen is different
                // if(Program.MyData.GetMultiscreen()) <-- If manually moved to another screen, set the new screen as default screen.
                {
                    Point petCenter = new Point(Left + Width / 2, Top + Height / 2);
                    for(var k=0;k<Screen.AllScreens.Length;k++)
                    {
                        Rectangle bounds = Screen.AllScreens[k].Bounds;
                        if (bounds.Contains(petCenter))
                        {
                            if (DisplayIndex != k)
                            {
                                DisplayIndex = k;
                                CurrentAnimation.UpdateValues(DisplayIndex);
                            }
                            break;
                        }
                    }
                }
            }
			IsDragging = false;
            ResetDragSwing();   // next drag starts hanging straight, not mid-swing from the last one
        }
        
            /// <summary>
            /// Mouse double click on pet. From old eSheep, a double click with the right mouse will kill the sheep.
            /// </summary>
            /// <param name="sender">Caller object.</param>
            /// <param name="e">Mouse event values.</param>
        private void pictureBox1_DoubleClick(object sender, EventArgs e)
        {
            MouseEventArgs me = (MouseEventArgs)e;
            if (me.Button == MouseButtons.Right)
            {
                if(!Program.Mainthread.KillSheep(this))
                {
                    Close();
                }
            }
        }

        /// <summary>
        /// Pet allows dropping other files. If you drop a XML animation file, the mouse icon will change.
        /// </summary>
        /// <param name="sender">Caller object.</param>
        /// <param name="e">Mouse event values.</param>
        private void Form2_DragEnter(object sender, DragEventArgs e)
        {
            StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info, "dragging file...");
            string ignored;
            e.Effect = TryGetSupportedPetDrop(e.Data, out ignored)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }

            /// <summary>
            /// Pet allows dropping other files. If a XML file was dropped, this one will be loaded.
            /// </summary>
            /// <param name="sender">Caller object.</param>
            /// <param name="e">Dragging event values.</param>
        private void Form2_DragDrop(object sender, DragEventArgs e)
        {
            try
            {
                string file;
                if (!TryGetSupportedPetDrop(e.Data, out file)) return;

                string candidate;
                candidate = ReadBoundedPetXml(file);

                if (!Program.Mainthread.LoadNewXMLFromString(candidate))
                    MessageBox.Show(
                        "The dropped pet was rejected. The current pet is unchanged.",
                        "Invalid pet",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Could not load the dropped pet: " + ex.Message,
                    "Invalid pet",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private static string ReadBoundedPetXml(string file)
        {
            int maximumBytes = CompanionXmlValidator.MaximumXmlBytes;
            byte[] bytes = new byte[checked(maximumBytes + 1)];
            int total = 0;
            CompanionXmlValidator.RetainedLocalXmlFile retained;
            string pathError;
            if (!CompanionXmlValidator.TryOpenLocalXmlFile(
                    file,
                    out retained,
                    out pathError))
                throw new InvalidDataException(pathError);
            using (retained)
            using (var stream = retained.OpenRead(4096))
            {
                while (total < bytes.Length)
                {
                    int read = stream.Read(bytes, total, bytes.Length - total);
                    if (read == 0) break;
                    total += read;
                }
            }

            if (total > maximumBytes)
                throw new InvalidDataException("Pet XML exceeds the 12 MiB limit.");

            return DecodePetXml(bytes, total);
        }

        private static string DecodePetXml(byte[] bytes, int count)
        {
            Encoding encoding = new UTF8Encoding(false, true);
            int offset = 0;

            if (count >= 4 &&
                bytes[0] == 0x00 && bytes[1] == 0x00 &&
                bytes[2] == 0xfe && bytes[3] == 0xff)
            {
                encoding = new UTF32Encoding(true, true, true);
                offset = 4;
            }
            else if (count >= 4 &&
                     bytes[0] == 0xff && bytes[1] == 0xfe &&
                     bytes[2] == 0x00 && bytes[3] == 0x00)
            {
                encoding = new UTF32Encoding(false, true, true);
                offset = 4;
            }
            else if (count >= 3 &&
                     bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
            {
                offset = 3;
            }
            else if (count >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
            {
                encoding = new UnicodeEncoding(true, true, true);
                offset = 2;
            }
            else if (count >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
            {
                encoding = new UnicodeEncoding(false, true, true);
                offset = 2;
            }

            return encoding.GetString(bytes, offset, count - offset);
        }

        private void Form2_FormClosed(object sender, FormClosedEventArgs e)
        {
            timer1.Stop();
            CloseChildren();
            pictureBox1.Image = null;
            if (_speech != null) _speech.Dispose();
            _speech = null;
            ReleaseChildOwnership();
            if(!IsDisposed) Dispose();
        }

        private static bool TryGetSupportedPetDrop(
            IDataObject data,
            out string canonicalPath)
        {
            canonicalPath = null;
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return false;
            string[] files = data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length != 1) return false;
            string error;
            return CompanionXmlValidator.TryResolveLocalXmlFile(
                files[0],
                out canonicalPath,
                out error);
        }

        /// <summary>Last line THIS pet said, for the optional back-to-back repeat guard (Preferences).</summary>
        private string _lastSaid;

        /// <summary>
        /// Display a speech bubble above this pet.
        /// Does nothing when speech bubbles are disabled in Options.
        ///
        /// The repeat guard lives HERE, per pet, rather than in StartUp.SayAll where it used to sit as a single
        /// global "last broadcast line". Two reasons. It was bypassable: IHost.Say(pet, text) goes straight to
        /// this method, so the moment modules speak to one pet instead of broadcasting, a global guard in
        /// SayAll stops seeing the lines it exists to de-duplicate and the user's "don't repeat yourself"
        /// preference silently stops working. And it was wrong for several pets: Pearl saying "X" should not
        /// silence Rick saying "X" -- different pets, different bubbles, no repetition the user can perceive --
        /// while Pearl saying "X" twice genuinely is a repeat. Per pet answers both.
        /// </summary>
        public void Say(string text) { SayWithDwell(text, 0); }

        /// <summary>
        /// As <see cref="Say(string)"/>, but with an explicit dwell in seconds (0 = the user's configured
        /// duration). Needed because FormSpeech starts its dismiss timer only once the typewriter finishes,
        /// so on-screen time is typing + dwell: a twelve-second spoken line under a six-second bubble looks
        /// broken. The host's ShowBubble callback passes a dwell through here.
        /// </summary>
        internal void SayWithDwell(string text, int dwellSeconds, DesktopAICompanion.Modules.SpeechStyle style = null)
        {
            if (!Program.MyData.GetSpeechEnabled()) return;

            // Only track/compare lines with real content, so a transient "…" thinking cue between two remarks
            // doesn't reset the guard (which would let quip / … / quip slip through as "not back-to-back").
            // This matters more per-pet than it did globally: the AI's cue and its answer land on the SAME pet.
            string trimmed = (text ?? "").Trim();
            if (StartUp.HasSpeechContent(trimmed))
            {
                bool dupe = string.Equals(trimmed, _lastSaid, StringComparison.OrdinalIgnoreCase);
                _lastSaid = trimmed;
                if (dupe)
                {
                    try { if (Program.MyData != null && Program.MyData.GetSuppressRepeats()) return; }
                    catch { }
                }
            }

            if (_speech == null || _speech.IsDisposed)
                _speech = new FormSpeech();
            _speech.SetFullscreenSuppressed(
                hwndFullscreenWindow != IntPtr.Zero);

            SpriteSpeechAnchor anchor = GetSpeechAnchor();
            _speech.ShowSpeech(
                text,
                AnimationRuntimeLimits.ClampFormCoordinate(anchor.X),
                AnimationRuntimeLimits.ClampFormCoordinate(anchor.Top),
                AnimationRuntimeLimits.ClampFormCoordinate(anchor.Bottom),
                dwellSeconds > 0 ? dwellSeconds : Program.MyData.GetSpeechDuration(), IsMovingLeft, style);
        }

        internal bool PaintSpeechForResourceChurn()
        {
            if (!Program.ResourceChurnSelfTestActive ||
                _speech == null ||
                _speech.IsDisposed ||
                _speech.Width <= 0 ||
                _speech.Height <= 0)
                return false;
            using (var rendered =
                new Bitmap(_speech.Width, _speech.Height))
            {
                _speech.DrawToBitmap(
                    rendered,
                    new Rectangle(Point.Empty, rendered.Size));
            }
            return true;
        }

        /// <summary>
        /// Re-anchor an active speech bubble over the pet's mouth. Called from the pet's tick so
        /// the bubble follows the pet while it walks or falls, instead of hanging in mid-air where
        /// it first spoke. No-op when there's no bubble showing.
        /// </summary>
        private void UpdateSpeechFollow()
        {
            if (_speech == null || _speech.IsDisposed || !_speech.IsShowing) return;

            SpriteSpeechAnchor anchor = GetSpeechAnchor();
            _speech.Reposition(
                AnimationRuntimeLimits.ClampFormCoordinate(anchor.X),
                AnimationRuntimeLimits.ClampFormCoordinate(anchor.Top),
                AnimationRuntimeLimits.ClampFormCoordinate(anchor.Bottom),
                IsMovingLeft);
        }

        /// <summary>
        /// Where the CHARACTER is inside the sprite window, in on-screen pixels. The built-in pets fill their
        /// frame, but a converted shimeji floats inside a larger padded cell (Hornet's standing frame occupies
        /// x=176..233 of a 256-wide cell), so the window edges are NOT the pet's edges. Border contact, the
        /// drag grab point and the speech bubble all need this rather than the raw frame.
        /// <para>
        /// Left/Right/Top are the transparent padding on each side and Width/Height are the character's own
        /// size, all already scaled to the form so they compose directly with PositionX / Width. A frame we
        /// cannot scan falls back to zero insets and the whole window, which reproduces the pre-inset
        /// behaviour exactly.
        /// </para>
        /// </summary>
        private struct SpriteInsets
        {
            public double Left;
            public double Right;
            public double Top;
            public double Width;
            public double Height;
        }

        // ---- drag swing -----------------------------------------------------
        //
        // A converted pet's drag animation carries up to 7 poses, one per horizontal offset band between the
        // pet's body and the cursor: the pet SWINGS from your hand. Reproducing it from positional lag is not
        // possible here, because the drag branch snaps the pet's centre onto the cursor every tick, so the lag
        // is always zero. Cursor VELOCITY gives the same result and touches nothing: move the mouse right and
        // the body trails left, stop moving and it settles upright. Smoothed, or a jittery mouse would strobe
        // through the poses.
        private double _dragSwing;
        private int _dragPreviousCursorX = int.MinValue;
        private const double DragSwingFullSpeedPx = 18.0;  // cursor px per tick that reaches the extreme pose
        private const double DragSwingSmoothing = 0.35;    // 0..1; higher follows the hand more closely

        private void TrackDragSwing()
        {
            int cursorX = Cursor.Position.X;
            if (_dragPreviousCursorX == int.MinValue) _dragPreviousCursorX = cursorX;
            double delta = cursorX - _dragPreviousCursorX;
            _dragPreviousCursorX = cursorX;
            _dragSwing += (delta - _dragSwing) * DragSwingSmoothing;
        }

        private void ResetDragSwing()
        {
            _dragSwing = 0.0;
            _dragPreviousCursorX = int.MinValue;
        }

        /// <summary>
        /// Which pose of a swing arc to show. Frame 0 is the body trailing furthest LEFT of the cursor and the
        /// last frame furthest right, because Shimeji evaluates the pose conditions in ascending cursor-offset
        /// order and the converter preserves that order. Moving the cursor RIGHT therefore selects a LOW index:
        /// the body lags behind the hand.
        /// </summary>
        private int DragSwingFrameIndex(int frameCount)
        {
            return DragSwingFrameIndexFor(_dragSwing, frameCount);
        }

        /// <summary>Pure, so the mapping can be asserted without a form or a mouse.</summary>
        internal static int DragSwingFrameIndexFor(double smoothedCursorVelocity, int frameCount)
        {
            if (frameCount <= 1) return 0;
            double normalised = smoothedCursorVelocity / DragSwingFullSpeedPx;
            if (normalised > 1.0) normalised = 1.0;
            if (normalised < -1.0) normalised = -1.0;
            double t = 0.5 - normalised * 0.5;
            int index = (int)Math.Round(t * (frameCount - 1));
            if (index < 0) index = 0;
            if (index > frameCount - 1) index = frameCount - 1;
            return index;
        }

        private SpriteInsets GetSpriteInsets()
        {
            SpriteInsets r;
            r.Left = 0;
            r.Right = 0;
            r.Top = 0;
            r.Width = pictureBox1.Width;
            r.Height = pictureBox1.Height;

            Image img = pictureBox1.Image ?? lastLayeredFrame;
            if (img == null || img.Width <= 0 || img.Height <= 0) return r;

            Rectangle vis = SpriteBounds.VisibleBounds(img, TransparencyKey);
            if (vis.IsEmpty || vis.Width <= 0 || vis.Height <= 0) return r;

            double sx = (double)pictureBox1.Width / img.Width;
            double sy = (double)pictureBox1.Height / img.Height;
            r.Left = vis.Left * sx;
            r.Right = (img.Width - vis.Right) * sx;
            r.Top = vis.Top * sy;
            r.Width = vis.Width * sx;
            r.Height = vis.Height * sy;
            return r;
        }

        private SpriteSpeechAnchor GetSpeechAnchor()
        {
            // Anchor over the VISIBLE character, not the whole frame: anchoring to the frame put the bubble
            // out over empty padding (tail pointing at nothing) for every converted shimeji.
            SpriteInsets ins = GetSpriteInsets();
            return DesktopGeometry.GetSpriteSpeechAnchor(
                PositionX + ins.Left,
                (PositionY + OffsetY) + ins.Top,
                Math.Max(1, (int)Math.Round(ins.Width)),
                Math.Max(1, (int)Math.Round(ins.Height)),
                IsMovingLeft);
        }

        /// <summary>
        /// Additive AI hook (backlog 2.8 / 3.6): play the animation whose XML name matches
        /// <paramref name="name"/> (case-insensitive). Returns false and does nothing when the
        /// loaded pet defines no such animation, so callers can map an emotion to a prioritized
        /// list of candidate names and fall through gracefully on pets that lack them.
        /// Must be called on the UI thread (it drives the same private SetNewAnimation the engine
        /// and the debug menu use); it does not otherwise touch the physics engine.
        /// </summary>
        public bool TryPlayAnimation(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || Animations == null || Animations.SheepAnimations == null)
                return false;

            foreach (KeyValuePair<int, TAnimation> kv in Animations.SheepAnimations)
            {
                if (string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    SetNewAnimation(kv.Key);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Flee via the pet's "bathtub" spawn (fly in from the screen edge and land in a tub) — the
        /// climax of the poke-escalation. Finds the spawn whose next animation is named "bath*" and
        /// re-runs the engine's public Play(forceSpawn) path. Returns false when the loaded pet has
        /// no such spawn, so the caller can fall back. UI thread.
        /// </summary>
        public bool EscapeToBath()
        {
            try
            {
                if (Animations == null || Animations.SheepSpawn == null || Animations.SheepAnimations == null)
                    return false;

                List<int> keys = Animations.SheepSpawn.Keys.ToList();
                for (int i = 0; i < keys.Count; i++)
                {
                    int nextId = Animations.SheepSpawn[keys[i]].Next;
                    if (Animations.SheepAnimations.ContainsKey(nextId)
                        && (Animations.SheepAnimations[nextId].Name ?? "").StartsWith("bath", StringComparison.OrdinalIgnoreCase))
                    {
                        Play(false, i);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private IDisposable CreateExpressionContext()
        {
            if (childDepth <= 0)
                return Xml.PushParentContext(new Point(-1, -1), false);

            Point canonicalParent = new Point(
                AnimationRuntimeLimits.CanonicalParentX(
                    parentPosition.X,
                    parentWasFlipped,
                    ScreenBounds.Width,
                    pictureBox1.Width),
                parentPosition.Y);
            return Xml.PushParentContext(
                canonicalParent,
                false);
        }

        private void PruneClosedChildren()
        {
            for (int i = childs.Count - 1; i >= 0; i--)
            {
                FormCompanion child = childs[i];
                if (child == null || child.IsDisposed)
                {
                    childs.RemoveAt(i);
                    if (child != null) child.ReleaseChildOwnership();
                }
            }
        }

        private void CloseChildren()
        {
            FormCompanion[] snapshot = childs.ToArray();
            childs.Clear();
            foreach (FormCompanion child in snapshot)
            {
                if (child == null) continue;
                if (!child.IsDisposed)
                {
                    try { child.Close(); } catch { }
                    try { child.Dispose(); } catch { }
                }
                child.ReleaseChildOwnership();
            }
        }

        private void ReleaseChildOwnership()
        {
            if (childOwnershipReleased || childDepth <= 0) return;
            childOwnershipReleased = true;
            if (parentPet != null) parentPet.childs.Remove(this);
            if (childBudget != null) childBudget.Release();
        }

        private sealed class ChildBudget
        {
            private static readonly object GlobalSync = new object();
            private static int globalActive;
            private int active;

            public bool TryAcquire()
            {
                lock (GlobalSync)
                {
                    if (active >= MaximumActiveChildrenPerRoot ||
                        globalActive >= MaximumActiveChildrenProcess)
                        return false;
                    active++;
                    globalActive++;
                    return true;
                }
            }

            public void Release()
            {
                lock (GlobalSync)
                {
                    if (active <= 0) return;
                    active--;
                    if (globalActive > 0) globalActive--;
                }
            }
        }

		private void PictureBox1_Click(object sender, EventArgs e)
		{

		}
	}

	/// <summary>
	/// Native methods for the windows detection functionality. User32.dll is used for this.
	/// </summary>
	internal static class NativeMethods
    {
            /// <summary>
            /// Get size of a window.
            /// </summary>
            /// <param name="hWnd">Handle to window.</param>
            /// <param name="lpRect">returns the size of the window.</param>
            /// <returns>True if successfully.</returns>
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(HandleRef hWnd, out RECT lpRect);

            /// <summary>
            /// Get a list of all windows present on the desktop.
            /// </summary>
            /// <param name="enumFunc">Enumeration function.</param>
            /// <param name="lParam">User defined value.</param>
            /// <returns>True if successfully.</returns>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc enumFunc, IntPtr lParam);

            /// <summary>
            /// If window is visible (is on the desktop).
            /// </summary>
            /// <param name="hWnd">Handle to the window.</param>
            /// <returns>True if successfully.</returns>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

            /// <summary>
            /// Get the text present in the window title bar.
            /// </summary>
            /// <param name="hWnd">Handle to the window.</param>
            /// <param name="lpString">Array, where the title should be copied.</param>
            /// <param name="nMaxCount">Array size.</param>
            /// <returns>Length of the title on the title bar.</returns>
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

            /// <summary>
            /// Get the values of the title bar from the window.
            /// </summary>
            /// <param name="hWnd">Handle to the window.</param>
            /// <param name="pti">Pointer to a valid structure. Will be filled with all information.</param>
            /// <returns>True if successfully.</returns>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTitleBarInfo(IntPtr hWnd, ref TITLEBARINFO pti);

            /// <summary>
            /// Change window modality (show, normal, hidden, maximize, ...) of a window.
            /// </summary>
            /// <param name="hWnd">Handle to the window.</param>
            /// <param name="nCmdShow">Command to change modality.</param>
            /// <returns>True if successfully</returns>
            /// <seealso cref="ShowWindow(IntPtr, int)"/>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            /// <summary>
            /// Set the focus to the window and bring it to foreground. Used once the pet is felt over it.
            /// </summary>
            /// <param name="hWnd">Handle to the window.</param>
            /// <returns>True</returns>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

            /// <summary>
            /// Get the next window on the desktop (next to the user in Z-order, child, first window, etc.)
            /// </summary>
            /// <param name="hWnd">Handle to the current window.</param>
            /// <param name="nCmdShow">Command of the next window to get, <see cref="GetWindow(IntPtr, int)"/></param>
            /// <returns>Pointer to the next window.</returns>
        [DllImport("user32.dll")]
        internal static extern IntPtr GetWindow(IntPtr hWnd, int nCmdShow);

        /// <summary>
        /// Get the window on the top, if hWnd is NULL, the top in Z-order will be returned
        /// </summary>
        /// <param name="hWnd">Handle to the current window.</param>
        /// <returns>Pointer to the next window.</returns>
        [DllImport("user32.dll")]
        internal static extern IntPtr GetTopWindow(IntPtr hWnd);

            /// <summary>
            /// Push a per-pixel-alpha bitmap onto a WS_EX_LAYERED window (ULW_ALPHA). This is the
            /// alpha render path used only by pets whose &lt;transparency&gt; is "Alpha"; magenta pets
            /// keep the WinForms colour-key path and never call this.
            /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateLayeredWindow(
            IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(IntPtr hObject);

            /// <summary>A screen point, for UpdateLayeredWindow position/source arguments.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
            public POINT(int x, int y) { this.x = x; this.y = y; }
        }

            /// <summary>A window size, for the UpdateLayeredWindow destination size.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int cx;
            public int cy;
            public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
        }

            /// <summary>Alpha blend descriptor for UpdateLayeredWindow (AC_SRC_OVER + AC_SRC_ALPHA).</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        /// <summary>
        /// Structure with the information about the title bar of the window.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct TITLEBARINFO
        {
                /// <summary>
                /// Size (in bytes) of the current structure.
                /// </summary>
            public int cbSize;
                /// <summary>
                /// Dimension of the title bar.
                /// </summary>
            public RECT rcTitleBar;
                /// <summary>
                /// 6 bytes containing the states of the title bar.
                /// </summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public int[] rgstate;
        }

            /// <summary>
            /// Dimension structure (used for the windows size).
            /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
                /// <summary>
                /// x position of upper-left corner
                /// </summary>
            public int Left;
                /// <summary>
                /// y position of upper-left corner
                /// </summary>
            public int Top;
                /// <summary>
                /// x position of lower-right corner
                /// </summary>
            public int Right;
                /// <summary>
                /// y position of lower-right corner
                /// </summary>
            public int Bottom; 
        }

            /// <summary>
            /// Procedure used to find all windows on the desktop.
            /// </summary>
            /// <param name="hWnd">Handle of the current found window.</param>
            /// <param name="lParam">User defined parameter.</param>
            /// <returns>True if successfully found another window.</returns>
        [return: MarshalAs(UnmanagedType.Bool)]
        internal delegate bool EnumWindowsProc(IntPtr hWnd, int lParam);
    }
}
