using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// Packs through the real tick host, the real type registry and the app's pack binding (grimoire/06 section 9): a
    /// synthetic pack built in code, where Alpha (main) starts as Egg, Egg hatches into Alpha, Alpha breeds Ball, and
    /// Ball transforms into a member that does not exist. Each member has its own colour, so the window shows which
    /// member's frames it draws. Members are staged on demand by id, exactly once, and each registry entry lives exactly
    /// as long as a companion runs or draws as that member.
    /// </summary>
    internal static partial class ShimejiWindowSelfTest
    {
        private const string PackId = "selfpack";

        private static void CheckPacks(Action<bool, string> check, StringBuilder log, string scratch)
        {
            var registry = new CompanionTypeRegistry();
            var typesByXml = new Dictionary<Xml, TickType>();
            var staged = new List<string>();
            var lines = new List<string>();
            bool arriveAsEgg = true;
            var docs = new Dictionary<string, KeyValuePair<XmlData.RootNode, Color[]>>(StringComparer.Ordinal)
            {
                // Alpha has a second frame of its own, so a window that drew the egg's pose from Alpha's sheet would show it.
                { "sp-alpha", new KeyValuePair<XmlData.RootNode, Color[]>(AlphaDoc(), new[] { Color.Red, Color.DarkRed }) },
                { "sp-egg", new KeyValuePair<XmlData.RootNode, Color[]>(EggDoc(), new[] { Color.Yellow, Color.Gold }) },
                { "sp-ball", new KeyValuePair<XmlData.RootNode, Color[]>(BallDoc(), new[] { Color.Blue }) },
            };
            Func<string, CompanionTypeRegistry.Entry> stageType = id =>
            {
                staged.Add(id);
                CompanionTypeRegistry.Entry existing;
                if (registry.TryGet(id, out existing)) return existing;
                KeyValuePair<XmlData.RootNode, Color[]> doc;
                if (!docs.TryGetValue(id, out doc)) return null;
                var frames = new List<Bitmap>();
                foreach (Color c in doc.Value)
                {
                    var b = new Bitmap(64, 64);
                    using (Graphics g = Graphics.FromImage(b)) g.Clear(c);
                    frames.Add(b);
                }
                var sprites = new Xml(frames, 64, 64);
                TickType type = TickType.Compile("", doc.Key);
                type.ApplyScale(1.0, sprites.spriteWidth, sprites.spriteHeight);
                typesByXml[sprites] = type;
                var animations = new Animations(sprites) { PetTypeId = id };
                return registry.Add(id, sprites, animations);
            };
            CompanionPackIndex index = CompanionPackIndex.Build(new[]
            {
                IndexEntry("sp-alpha", "Alpha", "main", false), IndexEntry("sp-egg", "Egg", "egg", true), IndexEntry("sp-ball", "Ball", "helper", true),
            });

            TickHost host = null;
            var forms = new List<FormCompanion>();
            CompanionTypeRegistry.Entry alphaEntry = null;
            try
            {
                host = new TickHost(t =>
                    {
                        var form = new FormCompanion(t.Animations, t.Sprites);
                        form.Show(t.Type.CellWidth, t.Type.CellHeight);
                        forms.Add(form);
                        return form;
                    },
                    form => form.Close(),
                    message => lines.Add(message)) { RenderOpacity = 0.0 };
                host.Environment.SearchesWindows = false;
                var binding = new TickPackBinding(host, registry, stageType,
                    x => { TickType t; return typesByXml.TryGetValue(x, out t) ? t : null; },
                    () => index, id => arriveAsEgg, line => lines.Add(line));

                // The user adds Alpha: its own entry is held by its window (as StartUp's petEntries does).
                alphaEntry = stageType("sp-alpha");
                TickType alpha = typesByXml[alphaEntry.Xml];
                host.AddType(binding.Describe(alphaEntry, alpha));
                registry.Increment(alphaEntry);
                staged.Clear();

                string behaviour;
                string start = binding.PlanStart(alpha, "sp-alpha", out behaviour);
                check(start == "sp-egg" && behaviour == "Laid" && staged.Count == 1 && staged[0] == "sp-egg",
                    "packs: a main member with an egg startup starts as the egg member, staged once on demand (" + start + ", " + behaviour
                    + ", staged " + string.Join("|", staged) + ")");

                Rectangle work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                var window = new FormCompanion(alphaEntry.Animations, alphaEntry.Xml);
                window.Show(64, 64);
                forms.Add(window);
                TickMascot m = host.Spawn(start, new TickPoint(work.Left + work.Width / 2, work.Bottom), false, behaviour, window);
                CompanionTypeRegistry.Entry eggEntry;
                registry.TryGet("sp-egg", out eggEntry);
                TickType egg = eggEntry == null ? null : typesByXml[eggEntry.Xml];
                check(eggEntry != null && eggEntry.RefCount == 1 && alphaEntry.RefCount == 1,
                    "packs: the egg's registry entry is held from the spawn (egg " + (eggEntry == null ? "none" : eggEntry.RefCount.ToString())
                    + ", alpha " + alphaEntry.RefCount + ")");

                host.Step();   // the egg activates and shows its first frame
                check(ReferenceEquals(m.Type, egg) && FrameColour(window) == Color.Yellow.ToArgb(),
                    "packs: an egg-started companion draws the egg member's frames");

                host.Step();   // Laid's penultimate tick: Transform into Alpha; the egg's last image stays this step
                bool hatched = ReferenceEquals(m.Type, alpha) && ReferenceEquals(m.PoseType, egg);
                check(hatched && FrameColour(window) == Color.Gold.ToArgb() && eggEntry.Xml != null
                      && eggEntry.RefCount == 1 && alphaEntry.RefCount == 2 && host.HeldBy(m).Length == 2,
                    "packs: on the hatch step both members are held, and the window still shows the egg's last image (egg "
                    + eggEntry.RefCount + ", alpha " + alphaEntry.RefCount + ")");

                host.Step();   // Alpha's first pose: the window draws Alpha, the egg is released and its pair disposed
                check(FrameColour(window) == Color.Red.ToArgb() && eggEntry.RefCount == 0 && eggEntry.Xml == null
                      && alphaEntry.RefCount == 2 && host.HostTypeOf(egg) == null,
                    "packs: once Alpha's first pose shows, the egg is released, disposed and forgotten (egg "
                    + eggEntry.RefCount + ", alpha " + alphaEntry.RefCount + ")");

                // Throw breeds Ball at its penultimate tick: the member is staged on demand, once.
                TickMascot child = null;
                for (int i = 0; i < 6 && child == null; i++)
                {
                    foreach (TickMascot p in host.World.Pending) if (p != m) child = p;
                    if (child == null) host.Step();
                }
                CompanionTypeRegistry.Entry ballEntry;
                registry.TryGet("sp-ball", out ballEntry);
                int ballStaged = staged.FindAll(s => s == "sp-ball").Count;
                check(child != null && ballEntry != null && ReferenceEquals(child.Type, typesByXml[ballEntry.Xml]) && ballStaged == 1
                      && ballEntry.RefCount == 1,
                    "packs: Alpha's birth is a Ball, staged once on demand and held while pending (staged " + ballStaged + ")");

                host.Step();   // the birth activates in its own window, drawing Ball's frames
                FormCompanion ballWindow = child == null ? null : host.FormOf(child);
                check(ballWindow != null && FrameColour(ballWindow) == Color.Blue.ToArgb(),
                    "packs: a born member's window draws its own member's frames");

                // Ball keeps transforming into a member the pack does not have: it stays a Ball, said once.
                for (int i = 0; i < 6; i++) host.Step();
                int ghostLines = lines.FindAll(l => l.Contains("'Ghost'")).Count;
                check(child != null && !child.Disposing && ballEntry != null && ReferenceEquals(child.Type, typesByXml[ballEntry.Xml]) && ghostLines == 1,
                    "packs: a missing member keeps the current type, with one diagnostic however often it is named (" + ghostLines + ")");

                // The born companion leaves: its member's registry entry goes with it.
                if (ballWindow != null) ballWindow.Kill();
                host.Step();
                check(ballEntry != null && ballEntry.RefCount == 0 && ballEntry.Xml == null,
                    "packs: a member's pair is disposed when the last companion using it leaves");

                // "Arrive as an egg" off: the main member starts directly, chosen by the route, and the egg is not staged.
                arriveAsEgg = false;
                staged.Clear();
                start = binding.PlanStart(alpha, "sp-alpha", out behaviour);
                check(start == "sp-alpha" && behaviour == null && staged.Count == 0,
                    "packs: with Arrive as an egg off the main member starts directly and no egg is staged");

                // A preview's members resolve only inside its own namespace: never the installed pack's.
                TickType preview = TickType.Compile("preview:abc", AlphaDoc());
                TickPackBinding.ScopeForPreview(preview, "preview:abc");
                host.AddType(new TickHostType("preview:abc", preview, alphaEntry.Xml));
                staged.Clear();
                check(ReferenceEquals(host.World.ResolveReference(preview, "Egg"), preview) && staged.Count == 0,
                    "packs: a preview member does not resolve an installed pack's member");
            }
            catch (Exception e)
            {
                check(false, "packs: the pack checks ran without an exception: " + e);
            }
            finally
            {
                if (host != null) host.Dispose();
                foreach (FormCompanion f in forms) { try { if (!f.IsDisposed) f.Close(); } catch { } }
                if (alphaEntry != null) registry.Decrement(alphaEntry);
                registry.DisposeAll();
                foreach (string l in lines) log.AppendLine("LOG: " + l);
            }

            CheckPackCatalog(check, scratch);
            CheckEggPreference(check, scratch);
            CheckPackStaging(check);
        }

        /// <summary>The installed pack index over real folders: the library copy of an id shadows the bundled one, and a
        /// member is found by (pack id, member key).</summary>
        private static void CheckPackCatalog(Action<bool, string> check, string scratch)
        {
            string library = Path.Combine(scratch, "library"), bundled = Path.Combine(scratch, "bundled");
            Action<string, string, string, string> write = (root, id, member, role) =>
            {
                Directory.CreateDirectory(Path.Combine(root, id));
                File.WriteAllText(Path.Combine(root, id, "animations.xml"),
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?><animations><header><petname>p</petname><pack id=\"" + PackId
                    + "\" member=\"" + member + "\" role=\"" + role + "\" hidden=\"" + (role == "main" ? "false" : "true")
                    + "\"/></header><image/></animations>",
                    new UTF8Encoding(false));
            };
            write(library, "sp-egg", "Egg", "egg");
            write(bundled, "sp-egg", "Shadowed", "egg");
            write(bundled, "sp-ball", "Ball", "helper");
            write(library, "sp-alpha", "Alpha", "main");
            CompanionPackIndex index = CompanionCatalog.PackIndexFrom(library, bundled);
            PackIndexEntry egg = index.Find(PackId, "Egg");
            check(egg != null && egg.FromLibrary && index.Find(PackId, "Shadowed") == null && index.Find(PackId, "Ball") != null
                  && index.Find(PackId, "Ball").TypeId == "sp-ball",
                "packs: the installed index reads each id where staging would (library first) and finds members by key");

            var addable = new List<string>();
            foreach (CompanionCatalog.CompanionInfo info in CompanionCatalog.EnumerateAddableFrom(bundled, library))
                if (!info.IsBuiltIn) addable.Add(info.Id);
            check(addable.Count == 1 && addable[0] == "sp-alpha",
                "packs: hidden members (the egg, the helper) stay out of the Add-a-companion list (" + string.Join(",", addable) + ")");
        }

        /// <summary>A pack member stages through the app's real path (validator, feature gate, compile): 'packs' is a
        /// feature this build runs, and the staged type knows its pack, member key and startup.</summary>
        private static void CheckPackStaging(Action<bool, string> check)
        {
            XmlData.RootNode classic;
            string error;
            if (!CompanionXmlValidator.TryParse(Properties.Resources.animations, out classic, out error))
            {
                check(false, "packs: the bundled pet parses (" + error + ")");
                return;
            }
            XmlData.RootNode member = SecuritySelfTest.TickFixture(classic);
            member.Header.Features += " packs";
            member.Header.Pack = new XmlData.PackNode { Id = PackId, Member = "Alpha", Role = "main" };
            member.Header.Startup = new XmlData.StartupNode { Member = "Egg", Behaviour = "Laid" };
            Xml xml;
            Animations animations;
            bool staged = StartUp.TryStageRuntime(SecuritySelfTest.Serialize(member), 1.0, out xml, out animations, out error);
            try
            {
                TickType type = staged ? TickStaging.Of(xml) : null;
                check(type != null && type.PackScope == PackId && type.Member == "Alpha" && type.Role == "main"
                      && type.StartupMember == "Egg" && type.StartupBehaviour == "Laid",
                    "packs: a pack member stages through the app (feature 'packs' runs) and keeps its pack, key and startup"
                    + (staged ? "" : " (" + error + ")"));
            }
            finally
            {
                if (animations != null) animations.Dispose();
                if (xml != null) xml.Dispose();
            }
        }

        /// <summary>"Arrive as an egg" is stored per type and survives a restart (a new LocalData over the same file).</summary>
        private static void CheckEggPreference(Action<bool, string> check, string scratch)
        {
            string path = Path.Combine(scratch, "egg-settings.json");
            var first = new LocalData(new AppSettingsStore(path, null));
            bool defaultOn = first.GetArriveAsEgg("sp-alpha");
            first.SetArriveAsEgg("sp-alpha", false);
            var second = new LocalData(new AppSettingsStore(path, null));
            check(defaultOn && !second.GetArriveAsEgg("sp-alpha") && second.GetArriveAsEgg("sp-other"),
                "packs: Arrive as an egg defaults on, and turning it off for one type survives a restart");
        }

        /// <summary>The colour of the frame a window shows, or 0 when it shows none or one whose sheet was disposed under
        /// it (GDI+ refuses a disposed bitmap with ArgumentException): exactly what the holds exist to prevent.</summary>
        private static int FrameColour(FormCompanion form)
        {
            var frame = form.TickFrameForSelfTest as Bitmap;
            try { return frame == null ? 0 : frame.GetPixel(32, 32).ToArgb(); }
            catch (ArgumentException) { return 0; }
        }

        private static PackIndexEntry IndexEntry(string id, string member, string role, bool hidden)
        {
            return new PackIndexEntry
            {
                TypeId = id,
                FromLibrary = true,
                Facts = new PackHeaderFacts { PackId = PackId, Member = member, Role = role, Hidden = hidden },
            };
        }

        // ---- the members: cells 64x64, anchor bottom centre

        private static XmlData.RootNode PackMember(string member, string role, XmlData.StartupNode startup, int frames,
            XmlData.AnimationNode[] nodes)
        {
            return new XmlData.RootNode
            {
                Header = new XmlData.HeaderNode
                {
                    Title = member, Features = "tick actions behaviours lifecycle packs", Clock = "tick",
                    Pack = new XmlData.PackNode { Id = PackId, Member = member, Role = role, Hidden = role != "main" },
                    Startup = startup,
                },
                Image = new XmlData.ImageNode
                {
                    TilesX = frames, TilesY = 1, Png = PngHeader(64 * frames, 64),
                    AnchorX = 32, AnchorXSpecified = true, AnchorY = 64, AnchorYSpecified = true,
                },
                Animations = new XmlData.AnimationsNode { Animation = nodes },
            };
        }

        private static XmlData.AnimationNode PackNode(int id, string name)
        {
            var still = new XmlData.MovingNode { X = "0", Y = "0", Interval = "40" };
            return new XmlData.AnimationNode
            {
                Id = id, Name = name, Start = still, End = still,
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
            };
        }

        private static XmlData.AnimationNode PackAct(int id, string name, string cls, XmlData.ParamNode[] ps, params XmlData.PoseNode[] poses)
        {
            XmlData.AnimationNode a = PackNode(id, name);
            a.Act = new XmlData.ActNode
            {
                Type = cls == null ? "Stay" : "Embedded", Class = cls, Border = "Floor", Params = ps,
                Variants = new XmlData.VariantsNode { Variant = new[] { new XmlData.VariantNode { Pose = poses } } },
            };
            return a;
        }

        private static XmlData.AnimationNode PackBehaviour(int id, string name)
        {
            XmlData.AnimationNode a = PackNode(id, "beh:" + name);
            a.Behaviour = new XmlData.BehaviourNode { Name = name, Runs = name, Frequency = 0 };
            return a;
        }

        private static XmlData.AnimationNode PackRoute(params int[] weights)
        {
            XmlData.AnimationNode route = PackNode(1, "route");
            route.Route = new XmlData.RouteNode();
            var next = new XmlData.NextNode[weights.Length];
            for (int i = 0; i < weights.Length; i++) next[i] = new XmlData.NextNode { Value = 10 + i, Probability = weights[i] };
            route.Sequence.Next = next;
            return route;
        }

        private static XmlData.ParamNode PackParam(string n, string v) { return new XmlData.ParamNode { Name = n, Value = v, Cache = "none" }; }

        /// <summary>Alpha: Stand (red, the route's choice) and Throw (breeds one Ball, which enters Morph); frame 1 (dark
        /// red) is never posed.</summary>
        private static XmlData.RootNode AlphaDoc()
        {
            return PackMember("Alpha", "main", new XmlData.StartupNode { Member = "Egg", Behaviour = "Laid" }, 2, new[]
            {
                PackRoute(1, 0), PackBehaviour(10, "Stand"), PackBehaviour(11, "Throw"),
                PackAct(20, "Stand", null, new[] { PackParam("Duration", "500") }, new XmlData.PoseNode { Frame = 0, Ticks = 1 }),
                PackAct(21, "Throw", "Breed", new[] { PackParam("BornX", "80"), PackParam("BornMascot", "Ball"), PackParam("BornBehavior", "Morph") },
                    new XmlData.PoseNode { Frame = 0, Ticks = 1 }, new XmlData.PoseNode { Frame = 0, Ticks = 1 }),
            });
        }

        /// <summary>Egg: Laid (yellow, then gold) hatches into Alpha, entering Throw.</summary>
        private static XmlData.RootNode EggDoc()
        {
            return PackMember("Egg", "egg", null, 2, new[]
            {
                PackRoute(1), PackBehaviour(10, "Laid"),
                PackAct(20, "Laid", "Transform", new[] { PackParam("TransformMascot", "Alpha"), PackParam("TransformBehavior", "Throw") },
                    new XmlData.PoseNode { Frame = 0, Ticks = 1 }, new XmlData.PoseNode { Frame = 1, Ticks = 1 }),
            });
        }

        /// <summary>Ball: Morph (blue) transforms into Ghost, which the pack does not have, and enters Morph again.</summary>
        private static XmlData.RootNode BallDoc()
        {
            return PackMember("Ball", "helper", null, 1, new[]
            {
                PackRoute(1), PackBehaviour(10, "Morph"),
                PackAct(20, "Morph", "Transform", new[] { PackParam("TransformMascot", "Ghost"), PackParam("TransformBehavior", "Morph") },
                    new XmlData.PoseNode { Frame = 0, Ticks = 1 }, new XmlData.PoseNode { Frame = 0, Ticks = 1 }),
            });
        }
    }
}
