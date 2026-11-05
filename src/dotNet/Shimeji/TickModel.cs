using System;
using System.Collections.Generic;
using System.Globalization;

namespace DesktopAICompanion.Shimeji
{
    internal enum TickCache { None, Action, Frame }

    /// <summary>
    /// A parameter or binding: a literal parsed the way the source parses one (Variable.parseConstant), or a SafeScript
    /// program with the source's cache kind (${} = action, #{} = frame).
    /// </summary>
    internal sealed class TickParamDef
    {
        public readonly string Name;
        public readonly TickCache Cache;
        public readonly ScriptValue Literal;
        public readonly ScriptProgram Program;

        public TickParamDef(string name, TickCache cache, ScriptValue literal, ScriptProgram program)
        {
            Name = name;
            Cache = cache;
            Literal = literal;
            Program = program;
        }
    }

    internal sealed class TickPoseDef
    {
        public int Frame;   // -1 = no image: the companion is hidden for this pose
        public int Right;   // -1 = mirror the left frame
        public int Ticks;
        public int Dx;
        public int Dy;
        public string Sound;

        /// <summary>The pose image's own geometry (grimoire/06 section 3; the source gives every image its own size and
        /// anchor, ImagePairLoader.java:37-38): the anchor in the left image's pixels, the left image's size (drawn at
        /// its cell's top-left), and the right image's size (the explicit right frame's, else the left's, mirrored).
        /// Compiled with the type's anchor and cell as defaults; scaled by <see cref="TickType.ApplyScale"/>.</summary>
        public int AnchorX, AnchorY, Width, Height, RightWidth, RightHeight;

        /// <summary>The image's anchor as the source's MascotImage holds it for a facing: the left image's is the
        /// pose's; the right image's x is measured from its own width (ImagePairLoader.java:38), so a mirrored image's
        /// anchor is mirrored with it.</summary>
        public TickPoint ImageCenter(bool right)
        {
            return new TickPoint(right ? RightWidth - AnchorX : AnchorX, AnchorY);
        }

        /// <summary>The image's size for a facing (MascotImage.getSize).</summary>
        public int ImageWidth(bool right) { return right ? RightWidth : Width; }
        public int ImageHeight(bool right) { return right ? RightHeight : Height; }
    }

    /// <summary>
    /// A clickable region of a variant (hotspot/Hotspot.java): a rectangle or an ellipse in window-local pixels, and the
    /// behaviour a press on it enters (none: the press is not taken, and becomes a drag).
    /// </summary>
    internal sealed class TickHotspotDef
    {
        public bool Ellipse;
        public int X, Y, Width, Height;
        public string Behaviour;

        /// <summary>Hotspot.contains: a right-facing companion's point is mirrored as width - x (not width - 1 - x);
        /// a rectangle holds its left and top edges but not its right and bottom (java.awt.Rectangle); an ellipse uses
        /// Ellipse2D's strict normalised test.</summary>
        public bool Contains(TickPoint point, bool lookRight, int windowWidth)
        {
            int px = lookRight ? windowWidth - point.X : point.X;
            int py = point.Y;
            if (!Ellipse)
                return Width > 0 && Height > 0 && px >= X && py >= Y && px < X + Width && py < Y + Height;
            if (Width <= 0 || Height <= 0) return false;
            double nx = (px - (double)X) / Width - 0.5;
            double ny = (py - (double)Y) / Height - 0.5;
            return nx * nx + ny * ny < 0.25;
        }
    }

    internal sealed class TickVariantDef
    {
        public ScriptProgram When;   // null = always
        public bool Turn;
        public TickPoseDef[] Poses;
        public int Duration;
        public TickHotspotDef[] Hotspots = new TickHotspotDef[0];

        /// <summary>Animation.getPoseAt: the pose for an action age, by age modulo the summed durations.</summary>
        public TickPoseDef PoseAt(int time)
        {
            if (Duration <= 0) return null;
            time %= Duration;
            foreach (TickPoseDef pose in Poses)
            {
                time -= pose.Ticks;
                if (time < 0) return pose;
            }
            return null;
        }
    }

    internal sealed class TickChildDef
    {
        public string Animation;
        public TickActDef Target;
        public Dictionary<string, TickParamDef> Binds;
        public string RefKey { get; set; }   // parity map: the ActionReference (or nested Action) element this child came from
    }

    internal sealed class TickActDef
    {
        public int NodeId;
        public string Name;
        public string Type;
        public string Class;
        public string Border;
        public bool HasLoop;
        public bool Loop;
        public Dictionary<string, TickParamDef> Params = new Dictionary<string, TickParamDef>(StringComparer.Ordinal);
        public ScriptProgram GuardWhen, GuardDuration, GuardDraggable;
        public string[] Offers;
        public TickVariantDef[] Variants = new TickVariantDef[0];
        public bool SelectMode;
        public TickChildDef[] Children = new TickChildDef[0];
        public string RefKey { get; set; }   // parity map: the Action element this node was lowered from
    }

    internal sealed class TickEdgeDef
    {
        public TickBehaviourDef Target;
        public int Weight;
        public ScriptProgram When;   // null = always; parsed afresh per selection in the source, evaluated per selection here
    }

    internal sealed class TickBehaviourDef
    {
        public int NodeId;
        public string Name;
        public string Runs;
        public int Frequency;
        public bool Hidden;
        public string Toggle;
        public bool Add = true;
        public Dictionary<string, TickParamDef> Binds = new Dictionary<string, TickParamDef>(StringComparer.Ordinal);
        public List<TickEdgeDef> Next = new List<TickEdgeDef>();
        public string RefKey { get; set; }   // parity map: the Behavior or BehaviorReference element
    }

    /// <summary>
    /// The compiled definition of one tick-clock companion type: immutable after <see cref="Compile"/>, shared by every
    /// live companion of the type. Built only from a document the validator accepted.
    /// </summary>
    internal sealed class TickType
    {
        public string Name;
        public int CellWidth, CellHeight, AnchorX, AnchorY;
        public readonly Dictionary<string, TickActDef> Actions = new Dictionary<string, TickActDef>(StringComparer.Ordinal);
        public readonly Dictionary<int, TickActDef> ActionsById = new Dictionary<int, TickActDef>();
        public readonly List<TickEdgeDef> Root = new List<TickEdgeDef>();
        /// <summary>Root behaviours by name, first edge wins (the source's LinkedHashMap keeps a name's first position).</summary>
        public readonly Dictionary<string, TickBehaviourDef> RootByName = new Dictionary<string, TickBehaviourDef>(StringComparer.Ordinal);
        public readonly Dictionary<int, TickBehaviourDef> BehavioursById = new Dictionary<int, TickBehaviourDef>();
        public readonly Dictionary<string, ScriptValue> Constants = new Dictionary<string, ScriptValue>(StringComparer.Ordinal);

        /// <summary>The size this type is drawn at (1 = the sheet's own pixels); the source's global Scaling setting,
        /// which the host keeps per type.</summary>
        public double Scale { get; private set; } = 1.0;
        private bool _scaled;

        /// <summary>
        /// Apply a drawing scale once, the way the source applies its Scaling setting while loading
        /// (AnimationBuilder.java:143-144 and 186-189, ImagePairLoader.java:37-38): the image anchor becomes
        /// round(anchor * s); the cell is the host sprite store's scaled frame size; a pose's per-tick move becomes
        /// round(move * s), except that a non-zero move scaled below one pixel stays one pixel in its direction; hotspot
        /// origin and size become round(value * s). Parameters (TargetX, Gravity, ...) are not scaled, as in the source.
        /// A pose's own anchor scales as the type's; its image sizes become the sprite store's scaled size of that region
        /// (<see cref="ScaleSize"/>).
        /// </summary>
        public void ApplyScale(double scale, int spriteWidth, int spriteHeight)
        {
            if (_scaled) throw new InvalidOperationException("type " + Name + " is already scaled");
            _scaled = true;
            Scale = scale;
            AnchorX = JavaRound(AnchorX * scale);
            AnchorY = JavaRound(AnchorY * scale);
            int cellWidth = CellWidth, cellHeight = CellHeight;
            CellWidth = spriteWidth;
            CellHeight = spriteHeight;
            foreach (TickActDef act in Actions.Values)
                foreach (TickVariantDef v in act.Variants)
                {
                    foreach (TickPoseDef p in v.Poses)
                    {
                        p.Dx = ScaleMove(p.Dx, scale);
                        p.Dy = ScaleMove(p.Dy, scale);
                        p.AnchorX = JavaRound(p.AnchorX * scale);
                        p.AnchorY = JavaRound(p.AnchorY * scale);
                        p.Width = ScaleSize(p.Width, cellWidth, spriteWidth, scale);
                        p.Height = ScaleSize(p.Height, cellHeight, spriteHeight, scale);
                        p.RightWidth = ScaleSize(p.RightWidth, cellWidth, spriteWidth, scale);
                        p.RightHeight = ScaleSize(p.RightHeight, cellHeight, spriteHeight, scale);
                    }
                    foreach (TickHotspotDef h in v.Hotspots)
                    {
                        h.X = JavaRound(h.X * scale);
                        h.Y = JavaRound(h.Y * scale);
                        h.Width = JavaRound(h.Width * scale);
                        h.Height = JavaRound(h.Height * scale);
                    }
                }
        }

        /// <summary>An image size at a scale (ImagePairLoader.scale: round(size * s), Java's rounding). A whole cell is
        /// the sprite store's scaled frame, which the host rounds its own way, so a pose that fills its cell is never a
        /// pixel wider than the frame it is cut from; a size that rounds to nothing keeps one pixel (the source cannot
        /// make a 0-pixel image at all).</summary>
        private static int ScaleSize(int size, int cell, int sprite, double scale)
        {
            if (size >= cell) return sprite;
            return Math.Max(1, JavaRound(size * scale));
        }

        private static int ScaleMove(int move, double scale)
        {
            if (move != 0 && Math.Abs(move * scale) < 1) return move > 0 ? 1 : -1;
            return JavaRound(move * scale);
        }

        /// <summary>(int) Math.round(double) as Java 8 computes it: the nearest integer with halves toward positive
        /// infinity (-3.5 is -3, where .NET's default banker's rounding gives -4), exact where floor(x + 0.5) is not
        /// (0.49999999999999994 is 0, not 1), NaN 0, and an out-of-range value saturating to a long that the int cast
        /// then truncates to its low 32 bits.</summary>
        public static int JavaRound(double value)
        {
            if (double.IsNaN(value)) return 0;
            double f = Math.Floor(value);
            double r = value - f >= 0.5 ? f + 1 : f;
            long l = r >= 9.2233720368547758E18 ? long.MaxValue : r <= -9.2233720368547758E18 ? long.MinValue : (long)r;
            return unchecked((int)l);
        }

        /// <summary>Compile a validated tick-clock document. Throws <see cref="InvalidOperationException"/> on anything
        /// the validator should have refused, naming it.</summary>
        public static TickType Compile(string name, XmlData.RootNode root)
        {
            var type = new TickType { Name = name };
            XmlData.ImageNode image = root.Image;
            int sheetWidth, sheetHeight;
            if (image.TilesX <= 0 || image.TilesY <= 0 || !TryReadPngSize(image.Png, out sheetWidth, out sheetHeight))
                throw new InvalidOperationException("the image is not a PNG sheet the engine can size");
            type.CellWidth = sheetWidth / image.TilesX;
            type.CellHeight = sheetHeight / image.TilesY;
            type.AnchorX = image.AnchorXSpecified ? image.AnchorX : type.CellWidth / 2;
            type.AnchorY = image.AnchorYSpecified ? image.AnchorY : type.CellHeight;

            if (root.Header != null && root.Header.Constants != null && root.Header.Constants.Constant != null)
                foreach (XmlData.ConstantNode c in root.Header.Constants.Constant)
                    if (c != null && c.Name != null && !type.Constants.ContainsKey(c.Name))
                        type.Constants[c.Name] = ParseLiteral(c.Value);

            XmlData.AnimationNode routeNode = null;
            foreach (XmlData.AnimationNode a in root.Animations.Animation)
            {
                if (a == null) continue;
                if (a.Act != null)
                {
                    TickActDef act = CompileAct(a, type);
                    type.Actions[act.Name] = act;
                    type.ActionsById[act.NodeId] = act;
                }
                else if (a.Behaviour != null)
                {
                    TickBehaviourDef b = CompileBehaviour(a);
                    type.BehavioursById[b.NodeId] = b;
                }
                else if (a.Route != null) routeNode = a;
            }
            foreach (TickActDef act in type.Actions.Values)
                foreach (TickChildDef child in act.Children)
                    if (!type.Actions.TryGetValue(child.Animation, out child.Target))
                        throw new InvalidOperationException("action '" + act.Name + "' names a missing child '" + child.Animation + "'");

            // Edges: a behaviour's own list (its NextBehaviorList), and the route's (the root BehaviorList).
            foreach (XmlData.AnimationNode a in root.Animations.Animation)
            {
                if (a == null || a.Behaviour == null) continue;
                type.BehavioursById[a.Id].Next.AddRange(CompileEdges(type, a));
            }
            if (routeNode != null)
            {
                type.Root.AddRange(CompileEdges(type, routeNode));
                foreach (TickEdgeDef e in type.Root)
                    if (!type.RootByName.ContainsKey(e.Target.Name)) type.RootByName[e.Target.Name] = e.Target;
            }
            return type;
        }

        /// <summary>The sheet's pixel size from the PNG's IHDR chunk (signature, length, "IHDR", width, height), so the
        /// engine sizes cells without decoding the image.</summary>
        public static bool TryReadPngSize(string base64, out int width, out int height)
        {
            width = height = 0;
            if (string.IsNullOrEmpty(base64)) return false;
            byte[] head;
            try
            {
                // 32 base64 characters decode to the 24 bytes that hold the signature and the IHDR size fields; the
                // document may wrap its base64, so whitespace is skipped.
                var prefix = new System.Text.StringBuilder(32);
                foreach (char c in base64)
                {
                    if (char.IsWhiteSpace(c)) continue;
                    prefix.Append(c);
                    if (prefix.Length == 32) break;
                }
                head = Convert.FromBase64String(prefix.ToString());
            }
            catch (FormatException)
            {
                return false;
            }
            if (head.Length < 24 || head[0] != 0x89 || head[1] != 0x50 || head[2] != 0x4E || head[3] != 0x47
                || head[12] != 0x49 || head[13] != 0x48 || head[14] != 0x44 || head[15] != 0x52)
                return false;
            width = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
            height = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
            return width > 0 && height > 0;
        }

        private static TickActDef CompileAct(XmlData.AnimationNode a, TickType type)
        {
            XmlData.ActNode n = a.Act;
            var act = new TickActDef
            {
                NodeId = a.Id,
                Name = a.Name,
                Type = n.Type,
                Class = n.Class ?? "",
                Border = n.Border ?? "",
                HasLoop = n.LoopSpecified,
                Loop = n.Loop,
            };
            if (n.Params != null)
                foreach (XmlData.ParamNode p in n.Params)
                    if (p != null) act.Params[p.Name] = CompileValue(p.Name, p.Value, p.Cache);
            if (n.Guard != null)
            {
                act.GuardWhen = Parse(n.Guard.When);
                act.GuardDuration = Parse(n.Guard.Duration);
                act.GuardDraggable = Parse(n.Guard.Draggable);
            }
            if (n.Offers != null && n.Offers.Offer != null)
            {
                var offers = new List<string>();
                foreach (XmlData.OfferNode o in n.Offers.Offer)
                    if (o != null && !string.IsNullOrEmpty(o.Value)) offers.Add(o.Value);
                act.Offers = offers.ToArray();
            }
            if (n.Variants != null && n.Variants.Variant != null)
            {
                var variants = new List<TickVariantDef>();
                foreach (XmlData.VariantNode v in n.Variants.Variant)
                {
                    if (v == null) continue;
                    var def = new TickVariantDef { When = Parse(v.When), Turn = v.TurnSpecified && v.Turn };
                    if (v.Hotspots != null && v.Hotspots.Hotspot != null)
                    {
                        var spots = new List<TickHotspotDef>();
                        foreach (XmlData.HotspotNode h in v.Hotspots.Hotspot)
                        {
                            if (h == null) continue;
                            int[] origin = ParsePair(h.Origin), size = ParsePair(h.Size);
                            spots.Add(new TickHotspotDef
                            {
                                Ellipse = string.Equals(h.Shape, "ellipse", StringComparison.Ordinal),
                                X = origin[0], Y = origin[1], Width = size[0], Height = size[1],
                                Behaviour = string.IsNullOrEmpty(h.Behaviour) ? null : h.Behaviour,
                            });
                        }
                        def.Hotspots = spots.ToArray();
                    }
                    var poses = new List<TickPoseDef>();
                    foreach (XmlData.PoseNode p in v.Pose)
                    {
                        // A pose's image geometry defaults to the type's: its anchor and a whole cell; a right image's size
                        // to the left image's (the mirrored left image, or an explicit right frame of the same size).
                        int width = p.WidthSpecified ? p.Width : type.CellWidth;
                        int height = p.HeightSpecified ? p.Height : type.CellHeight;
                        poses.Add(new TickPoseDef
                        {
                            Frame = p.Frame,
                            Right = p.RightSpecified ? p.Right : -1,
                            Ticks = p.Ticks,
                            Dx = p.DX,
                            Dy = p.DY,
                            Sound = p.Sound,
                            AnchorX = p.AnchorXSpecified ? p.AnchorX : type.AnchorX,
                            AnchorY = p.AnchorYSpecified ? p.AnchorY : type.AnchorY,
                            Width = width,
                            Height = height,
                            RightWidth = p.RightWidthSpecified ? p.RightWidth : width,
                            RightHeight = p.RightHeightSpecified ? p.RightHeight : height,
                        });
                        def.Duration += p.Ticks;
                    }
                    def.Poses = poses.ToArray();
                    variants.Add(def);
                }
                act.Variants = variants.ToArray();
            }
            if (n.Children != null && n.Children.Child != null)
            {
                act.SelectMode = string.Equals(n.Children.Mode, "select", StringComparison.Ordinal) || n.Type == "Select";
                var children = new List<TickChildDef>();
                foreach (XmlData.CompositeChildNode c in n.Children.Child)
                {
                    if (c == null) continue;
                    var child = new TickChildDef { Animation = c.Animation, Binds = new Dictionary<string, TickParamDef>(StringComparer.Ordinal) };
                    if (c.Bind != null)
                        foreach (XmlData.BindNode b in c.Bind)
                            if (b != null) child.Binds[b.Name] = CompileValue(b.Name, b.Value, b.Cache);
                    children.Add(child);
                }
                act.Children = children.ToArray();
            }
            return act;
        }

        private static TickBehaviourDef CompileBehaviour(XmlData.AnimationNode a)
        {
            XmlData.BehaviourNode n = a.Behaviour;
            var b = new TickBehaviourDef
            {
                NodeId = a.Id,
                Name = n.Name,
                Runs = string.IsNullOrEmpty(n.Runs) ? n.Name : n.Runs,
                Frequency = n.Frequency,
                Hidden = n.HiddenSpecified && n.Hidden,
                Toggle = n.Toggle,
                Add = !n.AddSpecified || n.Add,
            };
            if (n.Bind != null)
                foreach (XmlData.BindNode bind in n.Bind)
                    if (bind != null) b.Binds[bind.Name] = CompileValue(bind.Name, bind.Value, bind.Cache);
            return b;
        }

        private static IEnumerable<TickEdgeDef> CompileEdges(TickType type, XmlData.AnimationNode a)
        {
            if (a.Sequence == null || a.Sequence.Next == null) yield break;
            foreach (XmlData.NextNode n in a.Sequence.Next)
            {
                if (n == null) continue;
                TickBehaviourDef target;
                if (!type.BehavioursById.TryGetValue(n.Value, out target))
                    throw new InvalidOperationException("node " + a.Id + " has an edge to " + n.Value + ", which is not a behaviour");
                yield return new TickEdgeDef { Target = target, Weight = n.Probability, When = Parse(n.When) };
            }
        }

        private static int[] ParsePair(string value)
        {
            string[] parts = (value ?? "").Split(',');
            int x, y;
            if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
                || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out y))
                throw new InvalidOperationException("a hotspot pair the validator should have refused: '" + value + "'");
            return new[] { x, y };
        }

        private static TickParamDef CompileValue(string name, string value, string cache)
        {
            TickCache kind = string.Equals(cache, "action", StringComparison.Ordinal) ? TickCache.Action
                : string.Equals(cache, "frame", StringComparison.Ordinal) ? TickCache.Frame : TickCache.None;
            if (kind == TickCache.None) return new TickParamDef(name, kind, ParseLiteral(value), null);
            return new TickParamDef(name, kind, ScriptValue.Null, Parse(value));
        }

        private static ScriptProgram Parse(string source)
        {
            if (string.IsNullOrEmpty(source)) return null;
            ScriptProgram program;
            string error;
            if (!SafeScript.TryParse(source, AnyName, out program, out error))
                throw new InvalidOperationException("an expression the validator should have refused: " + error);
            return program;
        }

        // The validator already checked every name against its scope; the compiler does not re-judge it.
        private static readonly Func<string, bool> AnyName = delegate { return true; };

        /// <summary>
        /// Variable.parseConstant: "true" and "false" are booleans, anything Double.parseDouble reads is a number, and
        /// everything else is the string itself, "null" included (the source's null result is overwritten by the
        /// number attempt, so a literal "null" ends up the string "null").
        /// </summary>
        public static ScriptValue ParseLiteral(string source)
        {
            if (source == null) return ScriptValue.Null;
            if (source == "true") return ScriptValue.True;
            if (source == "false") return ScriptValue.False;
            double d;
            if (TryParseJavaDouble(source, out d)) return ScriptValue.FromNumber(d);
            return ScriptValue.FromString(source);
        }

        /// <summary>The decimal subset of Java's Double.parseDouble: surrounding whitespace, a sign, NaN, Infinity,
        /// digits with an optional fraction and exponent, and an optional f/F/d/D suffix. Hexadecimal floating literals
        /// are not read (no converted pack uses one) and stay strings.</summary>
        public static bool TryParseJavaDouble(string source, out double value)
        {
            value = 0;
            string s = source.Trim();
            if (s.Length == 0) return false;
            string body = s;
            char last = s[s.Length - 1];
            if (last == 'f' || last == 'F' || last == 'd' || last == 'D') body = s.Substring(0, s.Length - 1);
            string unsigned = body.Length > 0 && (body[0] == '+' || body[0] == '-') ? body.Substring(1) : body;
            if (unsigned == "NaN" || unsigned == "Infinity")
            {
                if (!ReferenceEquals(body, s)) return false;   // Java refuses "NaNf"
                value = unsigned == "NaN" ? double.NaN : (body[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity);
                return true;
            }
            if (unsigned.Length == 0) return false;
            int i = 0, digits = 0;
            while (i < unsigned.Length && char.IsDigit(unsigned[i]) && unsigned[i] < 128) { i++; digits++; }
            if (i < unsigned.Length && unsigned[i] == '.')
            {
                i++;
                while (i < unsigned.Length && char.IsDigit(unsigned[i]) && unsigned[i] < 128) { i++; digits++; }
            }
            if (digits == 0) return false;
            if (i < unsigned.Length && (unsigned[i] == 'e' || unsigned[i] == 'E'))
            {
                i++;
                if (i < unsigned.Length && (unsigned[i] == '+' || unsigned[i] == '-')) i++;
                int exp = 0;
                while (i < unsigned.Length && char.IsDigit(unsigned[i]) && unsigned[i] < 128) { i++; exp++; }
                if (exp == 0) return false;
            }
            if (i != unsigned.Length) return false;
            return double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
