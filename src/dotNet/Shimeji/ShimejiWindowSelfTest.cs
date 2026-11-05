using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// --shimeji-window-selftest: tick companions in REAL windows, stepped by hand on the real desktop's geometry. A
    /// companion's window sits at its anchor minus the image's anchor point, hides for an image-less pose, a birth opens
    /// a window, SelfDestruct closes one, and a press on the window enters Dragged. Everything is drawn at opacity 0, so
    /// nothing appears on the desktop; the synthetic type is built in code with plain coloured frames.
    /// </summary>
    internal static partial class ShimejiWindowSelfTest
    {
        public const string Flag = "--shimeji-window-selftest";
        public const string MarkerName = "dp-shimeji-window-selftest.txt";

        public static bool Run(TextWriter output)
        {
            var log = new StringBuilder();
            bool ok = true;
            Action<bool, string> check = (pass, name) =>
            {
                log.AppendLine((pass ? "PASS: " : "FAIL: ") + name);
                if (!pass) ok = false;
            };
            SynchronizationContext previousContext = SynchronizationContext.Current;
            LocalData previousData = Program.MyData;
            string scratch = Path.Combine(Path.GetTempPath(), "dp-shimeji-window-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            TickHost host = null;
            var closed = new List<FormCompanion>();
            try
            {
                Directory.CreateDirectory(scratch);
                // Self-test flags run before the app creates its settings; the form reads them, so give it scratch ones.
                Program.MyData = new LocalData(new AppSettingsStore(Path.Combine(scratch, "settings.json"), null));

                var frames = new List<Bitmap>();
                Color[] colours = { Color.Red, Color.Green, Color.Blue, Color.Orange };
                foreach (Color c in colours)
                {
                    var b = new Bitmap(64, 64);
                    using (Graphics g = Graphics.FromImage(b)) g.Clear(c);
                    frames.Add(b);
                }
                var sprites = new Xml(frames, 64, 64);
                TickType type = TickType.Compile("Alpha", Document());
                type.ApplyScale(1.0, sprites.spriteWidth, sprites.spriteHeight);
                var hostType = new TickHostType("selftest", type, sprites);

                // First, while no other host exists: pumping messages would step that host's timer too.
                CheckWallClock(check, log);

                host = new TickHost(t =>
                    {
                        var form = new FormCompanion(new Animations(t.Sprites), t.Sprites);
                        form.Show(t.Type.CellWidth, t.Type.CellHeight);
                        return form;
                    },
                    form => { closed.Add(form); form.Close(); },
                    message => log.AppendLine("LOG: " + message)) { RenderOpacity = 0.0 };
                // The owner's open windows must not become floors here (a whitelisted Notepad is one): no window search.
                host.Environment.SearchesWindows = false;
                host.AddType(hostType);

                // The app's settings for tick companions: the lifecycle switches on, window actions off until a type opts
                // in, and any setting the list does not name OFF, so a new or misspelt switch never defaults a behaviour on.
                var settings = new HostTickSettings(id => id);
                check(settings.Get("Breeding", "Alpha") && settings.Get("Transients", "Alpha") && settings.Get("Transformation", "Alpha")
                      && settings.Get("Throwing", "Alpha") && !settings.Get(TickWindowFamily.Setting, "Alpha") && !settings.Get("NoSuchSwitch", "Alpha"),
                    "tick settings: lifecycle switches on, window actions off, an unknown setting off");
                CheckStoredSwitches(check, host, scratch);
                CheckPutWindowsBack(check, host);

                Rectangle work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                int x = work.Left + work.Width / 2, floor = work.Bottom;
                TickMascot stander = host.Spawn("Alpha", new TickPoint(x - 200, floor), false, "Stand");
                TickMascot breeder = host.Spawn("Alpha", new TickPoint(x, floor), false, "Throw");
                TickMascot popper = host.Spawn("Alpha", new TickPoint(x + 200, floor), false, "Pop");

                // A companion whose first behaviour cannot be built is declined, not thrown into the app's spawn path.
                int formsBefore = host.Count;
                TickMascot unbuildable = null;
                string thrown = null;
                try { unbuildable = host.Spawn("Alpha", new TickPoint(x, floor), false, "NoSuchBehaviour"); }
                catch (Exception e) { thrown = e.GetType().Name; }
                check(thrown == null && unbuildable == null && host.Count == formsBefore && host.World.Pending.Count == 3,
                    "a companion whose first behaviour cannot be built is declined, not thrown" + (thrown == null ? "" : " (threw " + thrown + ")"));

                host.Step();   // step 1: the three start companions activate, step and are drawn
                FormCompanion standForm = host.FormOf(stander);
                check(standForm != null && host.Count == 3, "each start companion gets a window at its first step");
                check(standForm != null && standForm.Left == x - 200 - 32 && standForm.Top == floor - 64
                      && standForm.Width == 64 && standForm.Height == 64,
                    "a window sits at its anchor minus the image anchor (" + (standForm == null ? "none" : standForm.Bounds.ToString()) + ")");
                check(standForm != null && standForm.Visible, "a companion showing an image has a visible window");

                host.Step();   // step 2: Breed's penultimate tick requests a birth; SelfDestruct's disposes
                FormCompanion popForm = host.FormOf(popper);
                check(popper.Disposing && popForm != null && !popForm.Visible, "SelfDestruct hides its window at once");
                check(host.World.Pending.Count == 1, "Breed queued one birth on its penultimate tick");

                host.Step();   // step 3: the birth activates and opens a window; the disposed companion is removed
                check(host.Count == 3 && host.FormOf(popper) == null && closed.Contains(popForm),
                    "a birth opens a window and a removed companion's window is closed");
                TickMascot child = host.World.Mascots.Count > 3 ? null : FindChild(host.World);
                check(child != null && host.FormOf(child) != null, "the born companion has its own window");

                // Steps 4 and 5 reach Stand's image-less pose (frame -1 at ages 3-4): the window hides, the companion stays.
                host.Step();
                host.Step();
                check(standForm != null && !standForm.Visible && !stander.Disposing && host.FormOf(stander) == standForm,
                    "an image-less pose hides the window without removing the companion");

                // Whatever re-arms a tick-driven window's classic timer (Play, the debug menu, a fault respawn), the
                // timer stops at its first tick and the classic engine never runs.
                FormCompanion breedForm = host.FormOf(breeder);
                Rectangle before = breedForm.Bounds;
                check(!breedForm.ClassicTimerSurvivesForSelfTest() && breedForm.Bounds == before,
                    "a re-armed classic timer stops at once on a tick-driven window and moves nothing");

                // A left press through the window's own handler reaches the tick world at the next step and enters
                // Dragged. (The handler reads where the pointer is; it moves nothing.)
                breedForm.LeftPressForSelfTest();
                host.Step();
                check(breeder.Behaviour != null && breeder.Behaviour.Name == "Dragged", "a left press on a window enters Dragged at the next step");

                // A fullscreen window on the companion's monitor hides its window while the companion keeps running, and
                // the window is back at the first step after the monitor frees.
                // (The dragged companion: Dragged's one pose always shows an image.)
                int timeBefore = breeder.Time;
                bool shownBefore = breedForm.Visible;
                breedForm.TickBlockedForSelfTest = area => true;
                host.Step();
                bool hidden = !breedForm.Visible && breedForm.IsStoodDownForFullscreen && breeder.Time == timeBefore + 1;
                breedForm.TickBlockedForSelfTest = area => false;
                host.Step();
                check(shownBefore && hidden && breedForm.Visible && !breedForm.IsStoodDownForFullscreen,
                    "a fullscreen window on its monitor hides a tick window while the companion runs on, and it returns after");
                breedForm.TickBlockedForSelfTest = null;

                // Play (a respawn, a relocation, the debug menu) changes nothing on a tick-driven window: its host places it.
                Rectangle placed = breedForm.Bounds;
                bool shown = breedForm.Visible;
                string playFault = null;
                try { breedForm.Play(); }
                catch (Exception e) { playFault = e.GetType().Name; }
                check(playFault == null && breedForm.Bounds == placed && breedForm.Visible == shown,
                    "Play() on a tick-driven window moves and shows nothing" + (playFault == null ? "" : " (threw " + playFault + ")"));

                // Kill, as the user's double right-click and the app's exit call it: a tick window has no death animation,
                // so it closes at once, and its companion leaves the world at the next step without the host closing it twice.
                FormCompanion standing = host.FormOf(stander);
                standing.Kill();
                host.Step();
                check(standing.IsDisposed && stander.Disposing && host.FormOf(stander) == null
                      && !Contains(host.World.Mascots, stander) && !closed.Contains(standing),
                    "Kill() closes a tick window at once and its companion leaves the world at the next step");

                CheckStaging(check);
                CheckPacks(check, log, scratch);
                CheckParityTooling(check, scratch, log);
                // The interactive-title list a test copy may narrow (the real-window smoke's disposable window); the
                // installed app, on its real data root, keeps the shipped list whatever the variable says.
                check(Win32InteractiveWindow.WhitelistText(false, "SmokeToken") == TickWindowRules.DefaultWhitelist
                      && Win32InteractiveWindow.WhitelistText(true, "SmokeToken") == "SmokeToken"
                      && Win32InteractiveWindow.WhitelistText(true, null) == TickWindowRules.DefaultWhitelist,
                    "window titles: a test copy may narrow the interactive titles; the installed app never reads the variable");
                ShimejiSoundSelfTest.Run(check, log);
                CheckPoseGeometry(check, log);
                CheckParityGeometry(check);
                CheckParityHiddenTrace(check, log);
                CheckParitySettings(check);
            }
            catch (Exception e)
            {
                check(false, "the self-test ran without an exception: " + e);
            }
            finally
            {
                try { if (host != null) host.Dispose(); } catch (Exception e) { check(false, "the host closed cleanly: " + e.Message); }
                Program.MyData = previousData;
                SynchronizationContext.SetSynchronizationContext(previousContext);
                try { Directory.Delete(scratch, true); } catch { }
            }
            log.AppendLine("RESULT=" + (ok ? "PASS" : "FAIL"));
            output.Write(log.ToString());
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), MarkerName), log.ToString(), new UTF8Encoding(false)); } catch { }
            return ok;
        }

        /// <summary>
        /// The user's per-type switches as the running host reads them (lane feature/shimeji-host-settings): stored in the
        /// app's settings under the companion type's id, read afresh at each decision (a change reaches live companions
        /// with no restart), separate per type, honoured for a header toggle, and still there after a restart.
        /// </summary>
        private static void CheckStoredSwitches(Action<bool, string> check, TickHost host, string scratch)
        {
            // A second type, Beta, stored under its own id, as a transformed companion's new type would be.
            TickType beta = TickType.Compile("Beta", Document());
            var betaSprites = new Xml(new List<Bitmap> { new Bitmap(64, 64), new Bitmap(64, 64), new Bitmap(64, 64), new Bitmap(64, 64) }, 64, 64);
            beta.ApplyScale(1.0, 64, 64);
            host.AddType(new TickHostType("beta-id", beta, betaSprites));
            ITickSettings live = host.World.Settings;
            bool before = live.Get("Breeding", "Alpha") && !live.Get(TickWindowFamily.Setting, "Alpha");
            Program.MyData.SetCompanionSwitch("selftest", "Breeding", false);
            Program.MyData.SetCompanionSwitch("selftest", TickWindowFamily.Setting, true);
            check(before && !live.Get("Breeding", "Alpha") && live.Get(TickWindowFamily.Setting, "Alpha"),
                "switches: the running host reads a choice stored under the type's id at its next question, no restart");
            check(live.Get("Breeding", "Beta") && !live.Get(TickWindowFamily.Setting, "Beta"),
                "switches: one type's choices do not reach another type (Beta keeps its defaults)");

            var waving = new TickBehaviourDef { Name = "Wave", Toggle = "Waving", ToggleDefault = true };
            bool waveBefore = live.IsBehaviourEnabled(waving, "Alpha");
            Program.MyData.SetCompanionSwitch("selftest", "toggle:Waving", false);
            check(waveBefore && !live.IsBehaviourEnabled(waving, "Alpha") && live.IsBehaviourEnabled(waving, "Beta"),
                "switches: a header toggle turned off disables its behaviour for that type only");

            var restarted = new LocalData(new AppSettingsStore(Path.Combine(scratch, "settings.json"), null));
            check(restarted.GetCompanionSwitch("selftest", "Breeding") == false && restarted.GetCompanionSwitch("SELFTEST", TickWindowFamily.Setting) == true
                  && restarted.GetCompanionSwitch("selftest", "toggle:Waving") == false && restarted.GetCompanionSwitch("beta-id", "Breeding") == null,
                "switches: the choices survive a restart, keyed by type id");
            Program.MyData.SetCompanionSwitch("selftest", "Breeding", null);
            Program.MyData.SetCompanionSwitch("selftest", TickWindowFamily.Setting, null);
            Program.MyData.SetCompanionSwitch("selftest", "toggle:Waving", null);
            check(live.Get("Breeding", "Alpha") && !live.Get(TickWindowFamily.Setting, "Alpha") && live.IsBehaviourEnabled(waving, "Alpha"),
                "switches: a forgotten choice returns the type to its defaults");
        }

        /// <summary>A window record that acts on nothing: what "Put windows back" sees and does, counted.</summary>
        private sealed class FakeWindowRecord : ITickWindowRecord
        {
            public int Count;
            public int Restores = -1;   // how many a put-back restores; -1 = all of them
            public int Calls;
            public int RecordedCount { get { return Count; } }
            public int PutWindowsBack()
            {
                Calls++;
                int restored = Restores < 0 ? Count : Restores;
                Count = 0;
                return restored;
            }
        }

        /// <summary>
        /// The tray's "Put windows back", through the window seam with a fake record, so no real window is touched: hidden
        /// with nothing to put back, shown with a count when something was moved, its action goes through the record once
        /// and says how many went back, and it hides again after.
        /// </summary>
        private static void CheckPutWindowsBack(Action<bool, string> check, TickHost host)
        {
            check(host.WindowRecord != null && ReferenceEquals(host.WindowRecord, host.Environment.Interactive),
                "put back: the host's record is the desktop's interactive window by default");
            var fake = new FakeWindowRecord();
            host.WindowRecord = fake;
            using (var item = new System.Windows.Forms.ToolStripMenuItem())
            {
                TickWindowsTray.Refresh(item, host);
                bool hiddenEmpty = !item.Available;
                fake.Count = 2;
                TickWindowsTray.Refresh(item, host);
                bool shown = item.Available && item.Text.Contains("2");
                string said = TickWindowsTray.PutBack(host);
                TickWindowsTray.Refresh(item, host);
                check(hiddenEmpty && shown && fake.Calls == 1 && said == "Put 2 windows back." && !item.Available,
                    "put back: the tray item shows only with windows to put back, puts them back once and says how many ("
                    + said + ")");
                fake.Count = 3;
                fake.Restores = 1;
                said = TickWindowsTray.PutBack(host);
                check(fake.Calls == 2 && said.StartsWith("Put 1 of 3 windows back", StringComparison.Ordinal),
                    "put back: a partial put-back says how many of how many (" + said + ")");
                said = TickWindowsTray.PutBack(host);
                check(fake.Calls == 2 && said == "No window needed putting back.",
                    "put back: with nothing recorded the record is not asked");
            }
            // The tray menu carries the item, hidden until a window was moved.
            var menus = new ContextMenus();
            try
            {
                System.Windows.Forms.ContextMenuStrip strip = menus.Create();
                System.Windows.Forms.ToolStripItem found = null;
                foreach (System.Windows.Forms.ToolStripItem i in strip.Items)
                    if (i.Text == "Put &windows back") found = i;
                check(found != null && !found.Available, "put back: the tray menu has the item, hidden until a window was moved");
            }
            finally { menus.Dispose(); }
        }

        /// <summary>
        /// The app's staging of a tick-clock document (StartUp.TryStageRuntime): it compiles a tick type beside the Xml,
        /// scaled to the frames' staged size, and leaves the classic animation set empty; a classic document stages as it
        /// always has, with no tick type. The document is the extension gate's fixture, on the bundled pet's real sheet.
        /// </summary>
        private static void CheckStaging(Action<bool, string> check)
        {
            XmlData.RootNode classic;
            string error;
            if (!CompanionXmlValidator.TryParse(Properties.Resources.animations, out classic, out error))
            {
                check(false, "staging: the bundled pet parses (" + error + ")");
                return;
            }
            string tickText = SecuritySelfTest.Serialize(SecuritySelfTest.TickFixture(classic));
            Xml xml;
            Animations animations;
            bool staged = StartUp.TryStageRuntime(tickText, 2.0, out xml, out animations, out error);
            try
            {
                TickType type = staged ? TickStaging.Of(xml) : null;
                check(type != null && animations.SheepAnimations.Count == 0 && animations.SheepSpawn.Count == 0,
                    "staging: a tick-clock document stages a tick type and an empty classic set" + (staged ? "" : " (" + error + ")"));
                check(type != null && type.Scale == xml.ScaleFactorD && type.CellWidth == xml.spriteWidth
                      && type.CellHeight == xml.spriteHeight && type.RootByName.ContainsKey("Stand"),
                    "staging: the tick type is scaled to its frames' staged size and keeps its behaviours"
                    + (type == null ? "" : " (scale " + type.Scale + " vs " + xml.ScaleFactorD + ", cell " + type.CellWidth + "x"
                       + type.CellHeight + " vs " + xml.spriteWidth + "x" + xml.spriteHeight + ")"));
            }
            finally
            {
                if (animations != null) animations.Dispose();
                if (xml != null) xml.Dispose();
            }

            // The Pets pane's switches for an installed companion come from its document: a tick-clock one with a header
            // toggle offers that toggle (and nothing it does not use); a classic one offers nothing, so its card is unchanged.
            XmlData.RootNode toggled = SecuritySelfTest.TickFixture(classic);
            toggled.Header.Toggles = new XmlData.TogglesNode { Toggle = new[] { new XmlData.ToggleNode { Id = "Standing", Label = "Stands about", Default = false } } };
            toggled.Animations.Animation[1].Behaviour.Toggle = "Standing";
            List<TickSettingRow> tickRows = TickPaneRows.ForPetXml(SecuritySelfTest.Serialize(toggled));
            check(tickRows.Count == 1 && tickRows[0].Key == "toggle:Standing" && tickRows[0].Label == "Stands about" && !tickRows[0].DefaultOn,
                "pane rows: a tick companion's document offers its header toggle and nothing it does not use ("
                + tickRows.Count + " rows)");
            check(TickPaneRows.ForPetXml(Properties.Resources.animations).Count == 0,
                "pane rows: a classic companion's document offers no switch");

            // WITNESS: a classic document stages classically, with no tick type beside it.
            staged = StartUp.TryStageRuntime(Properties.Resources.animations, 1.0, out xml, out animations, out error);
            try
            {
                check(staged && TickStaging.Of(xml) == null && animations.SheepAnimations.Count > 0,
                    "staging: WITNESS a classic document stages classically with no tick type" + (staged ? "" : " (" + error + ")"));
            }
            finally
            {
                if (animations != null) animations.Dispose();
                if (xml != null) xml.Dispose();
            }
        }

        /// <summary>
        /// The parity run's witnesses (CONTRACT section 3): the converter's hash binds its managed assemblies and ignores
        /// an apphost, whose bytes do not change with the code; the pack hash refuses a link before reading through it.
        /// </summary>
        private static void CheckParityTooling(Action<bool, string> check, string scratch, StringBuilder log)
        {
            string conv = Path.Combine(scratch, "converter");
            Directory.CreateDirectory(conv);
            File.WriteAllBytes(Path.Combine(conv, "Engine.dll"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(conv, "Convert.exe"), new byte[] { 2 });
            string first = ParityRun.ConverterSha256(conv);
            File.WriteAllBytes(Path.Combine(conv, "Convert.exe"), new byte[] { 3 });
            string apphostChanged = ParityRun.ConverterSha256(conv);
            File.WriteAllBytes(Path.Combine(conv, "Engine.dll"), new byte[] { 4 });
            string codeChanged = ParityRun.ConverterSha256(conv);
            check(first == apphostChanged && first != codeChanged,
                "parity: the converter witness binds the managed assemblies and ignores the apphost");

            string pack = Path.Combine(scratch, "pack"), outside = Path.Combine(scratch, "outside");
            Directory.CreateDirectory(Path.Combine(pack, "img"));
            Directory.CreateDirectory(outside);
            File.WriteAllBytes(Path.Combine(pack, "img", "a.png"), new byte[] { 5 });
            string link = Path.Combine(pack, "img", "link");
            if (!TryMakeDirectoryLink(link, outside))
            {
                log.AppendLine("NOTE: parity: the pack-link refusal is not exercised (no symlink or junction could be made here)");
                return;
            }
            string refusal = null;
            try { ParityRun.PackSha256(pack); }
            catch (InvalidDataException e) { refusal = e.Message; }
            finally { try { Directory.Delete(link); } catch { } }
            check(refusal != null && refusal.Contains("pack-invalid"),
                "parity: the pack hash refuses a link before reading through it (" + (refusal ?? "read through it") + ")");
        }

        /// <summary>
        /// Per-pose geometry in real windows (grimoire/06 section 3). Frame 0's image is its cell's top-left 30x40 (green,
        /// its first column yellow) on a blue cell; frame 1's is a 50x20 orange top-left, first column yellow. A pose
        /// shows only its image: the window is the image's size, at the anchor minus the pose's anchor, facing left and
        /// right (the mirrored image is the flipped cell's top-RIGHT, so it is green with the yellow column last), and an
        /// explicit right frame sizes the window to itself; on the colour-key and the alpha path alike. The store cuts a
        /// region once and disposes it with the frames; past its region budget it hands back the whole cell, which is
        /// drawn so the region still lands at the companion's bounds, and that is logged once.
        /// </summary>
        private const uint QS_ALLINPUT = 0x04FF;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint MsgWaitForMultipleObjects(uint count, IntPtr handles, bool waitAll, uint milliseconds, uint wakeMask);

        /// <summary>Runs the thread's messages until <paramref name="done"/> or <paramref name="limit"/> ms, waiting as the
        /// app's message loop does (GetMessage wakes when a timer is due) rather than sleeping between pumps, which drops
        /// firings: a timer that expires twice between two pumps delivers one (a 10 ms timer fired every 22.7 ms under a
        /// DoEvents-and-Sleep(1) loop, 15.8 ms under this one). Not Application.Run: its exit destroys the thread's windows.</summary>
        private static void PumpUntil(Func<bool> done, long limit)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < limit)
            {
                MsgWaitForMultipleObjects(0, IntPtr.Zero, false, (uint)Math.Max(1, limit - clock.ElapsedMilliseconds), QS_ALLINPUT);
                System.Windows.Forms.Application.DoEvents();
            }
        }

        /// <summary>
        /// The real timer on a real message loop: one companion over three seconds of wall time takes the source's 75 steps
        /// (25 a second, TickPacer), within 8%. Stepping at every firing gave 64 with a 40 ms timer (it fires every 46.9 ms
        /// here) and 192 with a 10 ms one (every 15.6 ms). The count starts after the first steps: the first firing came
        /// about 450 ms after the timer started and the first step opens the companion's window, and the source's clock
        /// drops slots that late rather than make them up, so counting from the start measured the warm-up (61 of 75).
        /// </summary>
        private static void CheckWallClock(Action<bool, string> check, StringBuilder log)
        {
            TickHost host = null;
            Xml sprites = null;
            try
            {
                sprites = new Xml(GeometryFrames(), 64, 64);
                host = new TickHost(t =>
                    {
                        var form = new FormCompanion(new Animations(t.Sprites), t.Sprites);
                        form.Show(t.Type.CellWidth, t.Type.CellHeight);
                        return form;
                    },
                    form => form.Close(),
                    message => log.AppendLine("LOG: " + message)) { RenderOpacity = 0.0 };
                host.Environment.SearchesWindows = false;
                TickType type = TickType.Compile("Clock", GeometryDocument());
                type.ApplyScale(1.0, 64, 64);
                host.AddType(new TickHostType("Clock", type, sprites));
                Rectangle work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                host.Spawn("Clock", new TickPoint(work.Left + work.Width / 2, work.Bottom), false, "Small");
                int started = host.World.Step;
                PumpUntil(() => host.World.Step >= started + 3, 5000);
                bool warmed = host.World.Step >= started + 3;
                int first = host.World.Step, firstFiring = host.TimerFirings;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                PumpUntil(() => false, 3000);
                long elapsed = clock.ElapsedMilliseconds;
                int steps = host.World.Step - first, firings = host.TimerFirings - firstFiring;
                // What one step costs here, so a short count says whether the clock or the step is slow.
                var cost = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 20; i++) host.Step();
                double perStep = cost.Elapsed.TotalMilliseconds / 20;
                double expected = elapsed / (double)TickPacer.TickMilliseconds;
                check(warmed && Math.Abs(steps - expected) <= expected * 0.08,
                    "tick clock: the real timer steps 25 times a second, as the source's Manager (" + steps + " steps in "
                    + elapsed + " ms, " + expected.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                    + " expected" + (warmed ? "" : "; the first three steps never came") + "; the timer fired " + firings + " times; one step takes "
                    + perStep.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms)");
            }
            catch (Exception e)
            {
                check(false, "tick clock: the wall-clock check ran without an exception: " + e);
            }
            finally
            {
                if (host != null) host.Dispose();
                if (sprites != null) sprites.Dispose();
            }
        }

        private static void CheckPoseGeometry(Action<bool, string> check, StringBuilder log)
        {
            TickHost host = null;
            Xml colourKey = null, alpha = null, spent = null;
            var geoLog = new StringBuilder();
            try
            {
                colourKey = new Xml(GeometryFrames(), 64, 64);
                alpha = new Xml(GeometryFrames(), 64, 64, true);
                spent = new Xml(GeometryFrames(), 64, 64);
                spent.SetSpriteRegionBudgetForSelfTest(100);
                host = new TickHost(t =>
                    {
                        var form = new FormCompanion(new Animations(t.Sprites), t.Sprites);
                        form.Show(t.Type.CellWidth, t.Type.CellHeight);
                        return form;
                    },
                    form => form.Close(),
                    message => geoLog.AppendLine(message)) { RenderOpacity = 0.0 };
                host.Environment.SearchesWindows = false;
                foreach (KeyValuePair<string, Xml> t in new[]
                    {
                        new KeyValuePair<string, Xml>("Geo", colourKey), new KeyValuePair<string, Xml>("GeoA", alpha),
                        new KeyValuePair<string, Xml>("GeoB", spent),
                    })
                {
                    TickType type = TickType.Compile(t.Key, GeometryDocument());
                    type.ApplyScale(1.0, 64, 64);
                    host.AddType(new TickHostType(t.Key, type, t.Value));
                }

                Rectangle work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                int x = work.Left + work.Width / 2 - 300, floor = work.Bottom;
                TickMascot left = host.Spawn("Geo", new TickPoint(x, floor), false, "Small");
                TickMascot right = host.Spawn("Geo", new TickPoint(x + 150, floor), true, "Small");
                TickMascot pair = host.Spawn("Geo", new TickPoint(x + 300, floor), true, "Pair");
                TickMascot pairLeft = host.Spawn("Geo", new TickPoint(x + 450, floor), false, "Pair");
                TickMascot alphaRight = host.Spawn("GeoA", new TickPoint(x + 600, floor), true, "Small");
                TickMascot whole = host.Spawn("GeoB", new TickPoint(x + 750, floor), true, "Small");
                host.Step();

                // Facing left: the 30x40 image anchored at (10, 40), so the window is at (x - 10, floor - 40).
                FormCompanion f = host.FormOf(left);
                check(f != null && f.Bounds == new Rectangle(x - 10, floor - 40, 30, 40) && Shows(f, 30, 40, 0, 0, Color.Yellow) && Shows(f, 30, 40, 29, 39, Color.Green),
                    "geometry: facing left, a window is its pose image (30x40 from the cell's top-left) at the anchor minus the pose anchor (" + Describe(f) + ")");
                // Facing right, mirrored: anchor x 30 - 10 = 20, window at (x + 150 - 20); the image is the flipped cell's
                // top-right, so the yellow column is last and nothing blue (the rest of the cell) is in it.
                f = host.FormOf(right);
                check(f != null && f.Bounds == new Rectangle(x + 130, floor - 40, 30, 40) && Shows(f, 30, 40, 29, 0, Color.Yellow) && Shows(f, 30, 40, 0, 39, Color.Green),
                    "geometry: facing right, a mirrored pose shows the flipped cell's top-right 30x40 at the anchor minus (w - ax, ay) (" + Describe(f) + ")");
                // An explicit right frame of 50x20: anchor x 50 - 10 = 40, window (x + 300 - 40, floor - 40), 50x20.
                f = host.FormOf(pair);
                check(f != null && f.Bounds == new Rectangle(x + 260, floor - 40, 50, 20) && Shows(f, 50, 20, 0, 0, Color.Yellow) && Shows(f, 50, 20, 49, 19, Color.Orange),
                    "geometry: an explicit right frame of another size sizes and places the window by itself (" + Describe(f) + ")");
                f = host.FormOf(pairLeft);
                check(f != null && f.Bounds == new Rectangle(x + 440, floor - 40, 30, 40) && Shows(f, 30, 40, 0, 0, Color.Yellow),
                    "geometry: the same pose facing left shows its left image (" + Describe(f) + ")");
                // The alpha path pushes the region itself, so the layered window takes its size.
                f = host.FormOf(alphaRight);
                check(f != null && f.Bounds == new Rectangle(x + 580, floor - 40, 30, 40) && Shows(f, 30, 40, 29, 0, Color.Yellow),
                    "geometry: on the alpha path a mirrored pose's window is its image too (" + Describe(f) + ")");

                // The region is cut once: the next step draws the same bitmap, and the store keeps three regions (frame
                // 0's, flipped frame 0's, frame 1's) for its four companions.
                Image first = host.FormOf(left) == null ? null : host.FormOf(left).TickFrameForSelfTest;
                host.Step();
                check(first != null && ReferenceEquals(first, host.FormOf(left).TickFrameForSelfTest) && colourKey.SpriteRegionCount == 3,
                    "geometry: a pose's region is cut once and shared (" + colourKey.SpriteRegionCount + " regions kept)");

                // Past the region budget the whole 64x64 cell is drawn, its window moved left by the region's x (64 - 30)
                // so the image still lies at the bounds (x + 750 - 20), and the log says so once.
                f = host.FormOf(whole);
                int notices = CountOf(geoLog.ToString(), "the sprite region budget is spent");
                check(f != null && f.Bounds == new Rectangle(x + 730 - 34, floor - 40, 64, 64) && notices == 1,
                    "geometry: past the region budget the whole cell is drawn with the image at the bounds, said once ("
                    + Describe(f) + ", " + notices + " notices)");

                host.Dispose();
                host = null;
                Image cut = first;
                colourKey.Dispose();
                colourKey = null;
                check(cut != null && IsDisposed(cut), "geometry: a cut region is disposed with its sprite store");
            }
            catch (Exception e)
            {
                check(false, "geometry: the pose geometry checks ran without an exception: " + e);
            }
            finally
            {
                if (host != null) host.Dispose();
                if (colourKey != null) colourKey.Dispose();
                if (alpha != null) alpha.Dispose();
                if (spent != null) spent.Dispose();
                if (geoLog.Length > 0) log.Append("LOG: " + geoLog);
            }
        }

        /// <summary>The parity map's frames carry each image's anchor and size; a pose that disagrees with its frame is
        /// refused before anything is traced (CONTRACT section 9), and one that agrees applies.</summary>
        /// <summary>
        /// The trace while an image-less pose hides a companion (CONTRACT section 3): poseAnchor null, origin the bounds
        /// Mascot.getBounds gives, the window's last ones, which stay put while the hidden companion moves. The trace once
        /// wrote the anchor less the image-less pose's centre, which followed the anchor.
        /// </summary>
        private static void CheckParityHiddenTrace(Action<bool, string> check, StringBuilder log)
        {
            TickHost host = null;
            Xml sprites = null;
            try
            {
                sprites = new Xml(GeometryFrames(), 64, 64);
                host = new TickHost(t =>
                    {
                        var form = new FormCompanion(new Animations(t.Sprites), t.Sprites);
                        form.Show(t.Type.CellWidth, t.Type.CellHeight);
                        return form;
                    },
                    form => form.Close(),
                    message => log.AppendLine("LOG: " + message)) { RenderOpacity = 0.0 };
                host.Environment.SearchesWindows = false;
                // Small, as two poses: its 30x40 image anchored at (10, 40) for one tick, then no image while it moves
                // 3 px left a tick.
                XmlData.RootNode doc = GeometryDocument();
                XmlData.PoseNode shown = new XmlData.PoseNode
                {
                    Frame = 0, Ticks = 1, AnchorX = 10, AnchorXSpecified = true, AnchorY = 40, AnchorYSpecified = true,
                    Width = 30, WidthSpecified = true, Height = 40, HeightSpecified = true,
                };
                XmlData.PoseNode hidden = new XmlData.PoseNode { Frame = -1, Ticks = 10, DX = -3 };
                doc.Animations.Animation[3].Act.Variants.Variant[0].Pose = new[] { shown, hidden };
                TickType type = TickType.Compile("Hide", doc);
                type.ApplyScale(1.0, 64, 64);
                host.AddType(new TickHostType("Hide", type, sprites));
                Rectangle work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                int x = work.Left + work.Width / 2, floor = work.Bottom;
                host.Spawn("Hide", new TickPoint(x, floor), false, "Small");
                Func<string> line = () =>
                {
                    var sw = new StringWriter();
                    new ParityTraceWriter(sw, new Dictionary<string, ParityMap>()).States(host.World);
                    return sw.ToString();
                };
                host.Step();   // the image: bounds (x - 10, floor - 40)
                string first = line();
                host.Step();   // hidden, the anchor 3 px left
                host.Step();   // hidden, 6 px left
                string later = line();
                string frozen = "\"origin\":[" + (x - 10) + "," + (floor - 40) + "]";
                check(first.Contains("\"poseAnchor\":[10,40]") && first.Contains(frozen),
                    "parity trace: with an image, origin is the anchor less the image anchor (" + first.Trim() + ")");
                check(later.Contains("\"anchor\":[" + (x - 6) + "," + floor + "]") && later.Contains("\"poseAnchor\":null")
                      && later.Contains(frozen) && later.Contains("\"visible\":false"),
                    "parity trace: while hidden, poseAnchor is null and origin keeps the window's last bounds (" + later.Trim() + ")");
            }
            catch (Exception e)
            {
                check(false, "parity trace: the hidden-pose trace check ran without an exception: " + e);
            }
            finally
            {
                if (host != null) host.Dispose();
                if (sprites != null) sprites.Dispose();
            }
        }

        /// <summary>A parity run's behaviour toggles read as the host's do: the scenario's value, else the toggle's header
        /// default (TickTypeSwitches.IsBehaviourEnabled). They once read every unstated toggle as on.</summary>
        private static void CheckParitySettings(Action<bool, string> check)
        {
            Func<string, ParitySettings> parse = json =>
            {
                using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json)) return new ParitySettings(doc.RootElement);
            };
            var offByDefault = new TickBehaviourDef { Name = "Wave", Toggle = "waves", ToggleDefault = false };
            var onByDefault = new TickBehaviourDef { Name = "Wave", Toggle = "waves" };
            ParitySettings unstated = parse("{}");
            ParitySettings stated = parse("{\"settings\":{\"behaviour:waves\":true}}");
            check(!unstated.IsBehaviourEnabled(offByDefault, "Alpha") && unstated.IsBehaviourEnabled(onByDefault, "Alpha")
                  && stated.IsBehaviourEnabled(offByDefault, "Alpha"),
                "parity settings: a toggle the scenario does not state reads its header default, and a stated one wins");
        }

        private static void CheckParityGeometry(Action<bool, string> check)
        {
            Func<string, string, string> map = (frame0, frame1) =>
                "{\"schema\":\"shimeji-parity-map/1\",\"imageSet\":\"Geo\",\"frames\":["
                + "{\"frame\":0,\"image\":\"img/Geo/a.png\"," + frame0 + "},{\"frame\":1,\"image\":\"img/Geo/b.png\"," + frame1 + "}],"
                + "\"actions\":{\"30\":\"Geo|a|1\",\"31\":\"Geo|a|2\"},\"behaviours\":{\"10\":\"Geo|b|1\",\"11\":\"Geo|b|2\"}}";
            Func<string, string> apply = json =>
            {
                try
                {
                    ParityMap.Parse(json).Apply(TickType.Compile("Geo", GeometryDocument()));
                    return null;
                }
                catch (InvalidDataException e) { return e.Message; }
            };
            string agree = apply(map("\"anchor\":[10,40],\"size\":[30,40]", "\"anchor\":[10,40],\"size\":[50,20]"));
            check(agree == null, "parity: a map whose frames' anchors and sizes are the poses' applies" + (agree == null ? "" : " (" + agree + ")"));
            string anchor = apply(map("\"anchor\":[11,40],\"size\":[30,40]", "\"anchor\":[10,40],\"size\":[50,20]"));
            check(anchor != null && anchor.Contains("is anchored at (11, 40)"),
                "parity: a frame anchored elsewhere than its poses is refused (" + (anchor ?? "applied") + ")");
            string size = apply(map("\"anchor\":[10,40],\"size\":[30,40]", "\"anchor\":[10,40],\"size\":[50,21]"));
            check(size != null && size.Contains("is 50x21"),
                "parity: a right frame sized otherwise than its poses' rw x rh is refused (" + (size ?? "applied") + ")");
        }

        /// <summary>Whether the window's frame is <paramref name="w"/> x <paramref name="h"/> with
        /// <paramref name="colour"/> at (<paramref name="px"/>, <paramref name="py"/>).</summary>
        private static bool Shows(FormCompanion f, int w, int h, int px, int py, Color colour)
        {
            var b = f.TickFrameForSelfTest as Bitmap;
            return b != null && b.Width == w && b.Height == h && b.GetPixel(px, py).ToArgb() == colour.ToArgb();
        }

        private static string Describe(FormCompanion f)
        {
            if (f == null) return "no window";
            Image i = f.TickFrameForSelfTest;
            return "window " + f.Bounds + ", frame " + (i == null ? "none" : i.Width + "x" + i.Height);
        }

        private static bool IsDisposed(Image image)
        {
            try { return image.Width < 0; }
            catch (ArgumentException) { return true; }
        }

        private static int CountOf(string text, string part)
        {
            int n = 0;
            for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        private static List<Bitmap> GeometryFrames()
        {
            var frames = new List<Bitmap>();
            foreach (Size image in new[] { new Size(30, 40), new Size(50, 20) })
            {
                var b = new Bitmap(64, 64);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(Color.Blue);
                    using (var fill = new SolidBrush(image.Width == 30 ? Color.Green : Color.Orange))
                        g.FillRectangle(fill, 0, 0, image.Width, image.Height);
                    using (var column = new SolidBrush(Color.Yellow))
                        g.FillRectangle(column, 0, 0, 1, image.Height);
                }
                frames.Add(b);
            }
            return frames;
        }

        /// <summary>Small: frame 0's 30x40 image anchored at (10, 40). Pair: the same, with frame 1 as an explicit 50x20
        /// right image. Cells 64x64, the type's anchor (32, 64).</summary>
        private static XmlData.RootNode GeometryDocument()
        {
            var still = new XmlData.MovingNode { X = "0", Y = "0", Interval = "40" };
            Func<int, string, XmlData.AnimationNode> node = (id, name) => new XmlData.AnimationNode
            {
                Id = id, Name = name, Start = still, End = still,
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
            };
            Func<int, string, XmlData.PoseNode, XmlData.AnimationNode> act = (id, name, pose) =>
            {
                XmlData.AnimationNode a = node(id, name);
                a.Act = new XmlData.ActNode
                {
                    Type = "Stay", Border = "Floor",
                    Params = new[] { new XmlData.ParamNode { Name = "Duration", Value = "500", Cache = "none" } },
                    Variants = new XmlData.VariantsNode { Variant = new[] { new XmlData.VariantNode { Pose = new[] { pose } } } },
                };
                return a;
            };
            Func<int, string, int, XmlData.AnimationNode> beh = (id, name, frequency) =>
            {
                XmlData.AnimationNode a = node(id, "beh:" + name);
                a.Behaviour = new XmlData.BehaviourNode { Name = name, Runs = name, Frequency = frequency };
                return a;
            };
            Func<XmlData.PoseNode> small = () => new XmlData.PoseNode
            {
                Frame = 0, Ticks = 1, AnchorX = 10, AnchorXSpecified = true, AnchorY = 40, AnchorYSpecified = true,
                Width = 30, WidthSpecified = true, Height = 40, HeightSpecified = true,
            };
            XmlData.PoseNode paired = small();
            paired.Right = 1;
            paired.RightSpecified = true;
            paired.RightWidth = 50;
            paired.RightWidthSpecified = true;
            paired.RightHeight = 20;
            paired.RightHeightSpecified = true;
            XmlData.AnimationNode route = node(1, "route");
            route.Route = new XmlData.RouteNode();
            route.Sequence.Next = new[] { new XmlData.NextNode { Value = 10, Probability = 1 }, new XmlData.NextNode { Value = 11, Probability = 0 } };
            return new XmlData.RootNode
            {
                Header = new XmlData.HeaderNode { Title = "t", Features = "tick actions behaviours", Clock = "tick" },
                Image = new XmlData.ImageNode
                {
                    TilesX = 2, TilesY = 1, Png = PngHeader(128, 64),
                    AnchorX = 32, AnchorXSpecified = true, AnchorY = 64, AnchorYSpecified = true,
                },
                Animations = new XmlData.AnimationsNode
                {
                    Animation = new[] { route, beh(10, "Small", 1), beh(11, "Pair", 0), act(30, "Small", small()), act(31, "Pair", paired) },
                },
            };
        }

        private static bool TryMakeDirectoryLink(string link, string target)
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
                return true;
            }
            catch (Exception) { }
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                };
                // mklink's message is not read, so it is not redirected: a synchronous read of it would wait for cmd to
                // exit and leave the 5-second bound below unreachable.
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
                    p.WaitForExit(5000);
                return Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool Contains(IReadOnlyList<TickMascot> list, TickMascot m)
        {
            foreach (TickMascot x in list) if (ReferenceEquals(x, m)) return true;
            return false;
        }

        private static TickMascot FindChild(TickWorld world)
        {
            foreach (TickMascot m in world.Mascots) if (m.Sid.Contains(".b")) return m;
            return null;
        }

        // ---- the synthetic type: Stand (shows frame 0 for 3 ticks, then nothing for 2), Throw (Breed), Pop
        // (SelfDestruct), Dragged; cells 64x64 with the anchor at the bottom centre (32, 64)

        private static XmlData.RootNode Document()
        {
            var still = new XmlData.MovingNode { X = "0", Y = "0", Interval = "40" };
            Func<int, string, XmlData.AnimationNode> node = (id, name) => new XmlData.AnimationNode
            {
                Id = id, Name = name, Start = still, End = still,
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
            };
            Func<int, string, string, XmlData.ParamNode[], XmlData.PoseNode[], XmlData.AnimationNode> act = (id, name, cls, ps, poses) =>
            {
                XmlData.AnimationNode a = node(id, name);
                a.Act = new XmlData.ActNode
                {
                    Type = cls == null ? "Stay" : "Embedded", Class = cls, Border = "Floor", Params = ps,
                    Variants = new XmlData.VariantsNode { Variant = new[] { new XmlData.VariantNode { Pose = poses } } },
                };
                return a;
            };
            Func<int, string, int, XmlData.AnimationNode> beh = (id, name, frequency) =>
            {
                XmlData.AnimationNode a = node(id, "beh:" + name);
                a.Behaviour = new XmlData.BehaviourNode { Name = name, Runs = name, Frequency = frequency };
                return a;
            };
            XmlData.AnimationNode route = node(1, "route");
            route.Route = new XmlData.RouteNode();
            route.Sequence.Next = new[]
            {
                new XmlData.NextNode { Value = 10, Probability = 1 }, new XmlData.NextNode { Value = 11, Probability = 0 },
                new XmlData.NextNode { Value = 12, Probability = 0 }, new XmlData.NextNode { Value = 13, Probability = 0 },
            };
            var p = new Func<string, string, XmlData.ParamNode>((n, v) => new XmlData.ParamNode { Name = n, Value = v, Cache = "none" });
            return new XmlData.RootNode
            {
                Header = new XmlData.HeaderNode { Title = "t", Features = "tick actions behaviours lifecycle", Clock = "tick" },
                Image = new XmlData.ImageNode
                {
                    TilesX = 4, TilesY = 1, Png = PngHeader(256, 64),
                    AnchorX = 32, AnchorXSpecified = true, AnchorY = 64, AnchorYSpecified = true,
                },
                Animations = new XmlData.AnimationsNode
                {
                    Animation = new[]
                    {
                        route, beh(10, "Stand", 1), beh(11, "Throw", 0), beh(12, "Pop", 0), beh(13, "Dragged", 0),
                        act(20, "Stand", null, new[] { p("Duration", "500") },
                            new[] { new XmlData.PoseNode { Frame = 0, Ticks = 3 }, new XmlData.PoseNode { Frame = -1, Ticks = 2 } }),
                        act(21, "Throw", "Breed", new[] { p("BornX", "40"), p("BornBehavior", "Stand") },
                            new[] { new XmlData.PoseNode { Frame = 1, Ticks = 1 }, new XmlData.PoseNode { Frame = 2, Ticks = 1 } }),
                        act(22, "Pop", "SelfDestruct", new XmlData.ParamNode[0],
                            new[] { new XmlData.PoseNode { Frame = 3, Ticks = 1 }, new XmlData.PoseNode { Frame = 3, Ticks = 1 } }),
                        act(23, "Dragged", null, new[] { p("Duration", "500") }, new[] { new XmlData.PoseNode { Frame = 2, Ticks = 1 } }),
                    },
                },
            };
        }

        /// <summary>The PNG signature and IHDR size fields: all TickType reads to size the sheet.</summary>
        private static string PngHeader(int width, int height)
        {
            byte[] head =
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52,
                (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
                (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height, 8, 6, 0, 0, 0,
            };
            return Convert.ToBase64String(head);
        }
    }
}
