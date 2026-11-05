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
                ShimejiSoundSelfTest.Run(check, log);
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
                    RedirectStandardOutput = true,
                    // Pinned and drained asynchronously (the hardening invariants): a synchronous read would block until
                    // mklink exits and leave the timeout below unreachable. The text is discarded either way.
                    StandardOutputEncoding = Encoding.UTF8,
                };
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
                {
                    System.Threading.Tasks.Task<string> drained = p.StandardOutput.ReadToEndAsync();
                    p.WaitForExit(5000);
                    GC.KeepAlive(drained);
                }
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
