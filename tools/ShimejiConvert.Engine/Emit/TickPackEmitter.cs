using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Xml.Linq;
using DesktopAICompanion.Tools.ShimejiConvert.Shimeji;

namespace DesktopAICompanion.Tools.ShimejiConvert.Emit
{
    /// <summary>A strict, deliberately limited first tick-format lowering. Unsupported source constructs fail
    /// with their source location. It does not change the classic converter or claim action-family support.
    /// Until the host carries per-pose source bounds, each member must have one authored image geometry.</summary>
    public static class TickPackEmitter
    {
        private static readonly XNamespace Ns = "https://esheep.petrucci.ch/";
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly HashSet<string> Types = Set("Stay Move Animate Sequence Select Embedded");
        // Fall and Dragged are needed by the shared packs' reserved, zero-frequency behaviours.
        private static readonly HashSet<string> Classes = Set("Breed Transform Fall Dragged");
        private static readonly HashSet<string> Parameters = Set("Duration Condition Draggable Affordance BorderType Loop TargetX TargetY InitialVX InitialVY RegistanceX RegistanceY Gravity OffsetX OffsetY OffsetType BornX BornY BornBehavior BornMascot BornCount BornTransient TransformMascot TransformBehavior");

        /// <summary>Convert every immediate img/member directory. Returns the emitted animations.xml paths.
        /// The output is separate from the source pack; every member also gets a parity-map.json.</summary>
        public static IReadOnlyList<string> ConvertPack(string packRoot, string outDir, string mainMember = null)
        {
            packRoot = Path.GetFullPath(packRoot ?? throw new ArgumentNullException("packRoot"));
            outDir = Path.GetFullPath(outDir ?? throw new ArgumentNullException("outDir"));
            SafeEntry(packRoot);
            if (Inside(packRoot, outDir)) throw new InvalidDataException("Tick output must be outside its source pack.");
            SafeEntry(outDir);
            if (Directory.Exists(outDir) || File.Exists(outDir))
                throw new InvalidDataException("TICK_OUTPUT_EXISTS: choose a new output directory.");
            string images = Path.Combine(packRoot, "img");
            SafeEntry(images);
            if (!Directory.Exists(images)) throw new InvalidDataException("Tick pack needs an img directory.");
            string[] members = Directory.GetDirectories(images);
            Array.Sort(members, StringComparer.Ordinal);
            if (members.Length == 0) throw new InvalidDataException("Tick pack has no image-set members.");
            var prepared = new List<MemberOutput>();
            foreach (string memberPath in members)
            {
                SafeEntry(memberPath);
                string member = Path.GetFileName(memberPath);
                ShimejiSourceConfig source = ShimejiParser.ParseSourcePack(packRoot, member);
                prepared.Add(new MemberBuilder(packRoot, member, source).Build());
            }
            if (mainMember != null && !prepared.Any(m => m.Name == mainMember))
                throw new InvalidDataException("TICK_PACK_MAIN_UNKNOWN: the requested main image set is not in this pack.");
            if (prepared.Any(m => m.CrossMember))
            {
                if (mainMember == null)
                    throw new InvalidDataException("TICK_PACK_MAIN_REQUIRED: provide the main image set for cross-member output.");
                string packId;
                using (SHA256 sha = SHA256.Create())
                    packId = "shimeji-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(
                        string.Join("\n", prepared.Select(m => m.Name + "\n" + m.Xml))))).Replace("-", "").ToLowerInvariant().Substring(0, 24);
                foreach (MemberOutput member in prepared)
                {
                    XDocument document = XDocument.Parse(member.Xml);
                    XElement header = document.Root.Element(Ns + "header");
                    XElement features = header.Element(Ns + "features");
                    if (!features.Value.Split(' ').Contains("packs")) features.Value += " packs";
                    // The caller chooses the main member; other members remain neutral forms.
                    // Roles such as egg/helper and startup behaviour are not guessed from names.
                    header.Add(E("pack", new XAttribute("id", packId), new XAttribute("member", member.Name),
                        new XAttribute("role", member.Name == mainMember ? "main" : "form")));
                    member.Xml = document.ToString();
                }
            }
            // Prepare every member before writing, so an unsupported input does not leave half a pack.
            var paths = new List<string>();
            foreach (MemberOutput member in prepared)
            {
                string directory = Path.Combine(outDir, member.Name);
                SafeEntry(directory);
                Directory.CreateDirectory(directory);
                string xml = Path.Combine(directory, "animations.xml"), map = Path.Combine(directory, "parity-map.json");
                SafeEntry(xml);
                SafeEntry(map);
                File.WriteAllText(xml, member.Xml, new UTF8Encoding(false));
                File.WriteAllText(map, member.Map, new UTF8Encoding(false));
                paths.Add(xml);
            }
            return paths.AsReadOnly();
        }

        private sealed class MemberOutput { public string Name, Xml, Map; public bool CrossMember; }
        private sealed class RootBehaviour
        {
            public ShimejiSourceElement Source;
            public string Condition;
            public int Id;
        }
        private sealed class Frame
        {
            public string Image, Path;
            public int X, Y, Width, Height;
        }

        private sealed class MemberBuilder
        {
            private readonly string _pack, _member;
            private readonly ShimejiSourceConfig _source;
            private readonly List<XElement> _nodes = new List<XElement>();
            private readonly Dictionary<string, ShimejiSourceElement> _actions = new Dictionary<string, ShimejiSourceElement>(StringComparer.Ordinal);
            private readonly HashSet<string> _globalActions = new HashSet<string>(StringComparer.Ordinal);
            private readonly Dictionary<string, RootBehaviour> _roots = new Dictionary<string, RootBehaviour>(StringComparer.Ordinal);
            private readonly List<string> _rootOrder = new List<string>();
            private readonly List<Frame> _frames = new List<Frame>();
            private readonly Dictionary<string, int> _frameIds = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _actionMap = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _behaviourMap = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _childMap = new Dictionary<string, string>(StringComparer.Ordinal);
            private int _next = 2;
            private bool _lifecycle;

            public MemberBuilder(string pack, string member, ShimejiSourceConfig source)
            { _pack = pack; _member = member; _source = source; }

            public MemberOutput Build()
            {
                if (_source.Information != null) Fail(_source.Information.Root, "information metadata is not lowered yet");
                if (_source.Actions == null || _source.Behaviors == null) throw new InvalidDataException("Tick pack needs both action and behaviour documents.");
                if (_source.Actions.Schema != ShimejiSourceSchema.Base)
                    Fail(_source.Actions.Root, "TICK_SCHEMA_UNSUPPORTED: action enum values require the base document schema.");
                Check(_source.Actions.Root);
                Check(_source.Behaviors.Root);
                foreach (ShimejiSourceElement list in _source.Actions.Root.Children)
                {
                    if (list.Kind != ShimejiSourceKind.ActionList) Fail(list, "only ActionList is supported at the action document root");
                    Check(list);
                    foreach (ShimejiSourceElement action in list.Children)
                    {
                        if (action.Kind != ShimejiSourceKind.Action) Fail(action, "expected a source Action");
                        string name = Required(action, "Name");
                        if (_actions.ContainsKey(name)) Fail(action, "duplicate action '" + name + "'");
                        _actions.Add(name, action);
                        _globalActions.Add(name);
                    }
                }
                foreach (ShimejiSourceElement list in _source.Behaviors.Root.Children)
                {
                    if (list.Kind != ShimejiSourceKind.BehaviorList) Fail(list, "only BehaviorList is supported at the behaviour document root");
                    Check(list);
                    CollectRoots(list, null);
                }
                if (_roots.Count == 0 || _actions.Count == 0) throw new InvalidDataException("Tick member has no actions or root behaviours: " + _member);
                foreach (ShimejiSourceElement action in _actions.Values.ToArray()) EmitAction(action, false);
                foreach (string name in _rootOrder) _roots[name].Id = _next++;
                foreach (string name in _rootOrder)
                {
                    RootBehaviour root = _roots[name];
                    EmitBehaviour(root.Source, root.Condition, root.Id);
                }
                XElement route = Node(1, "route", E("route"));
                foreach (string name in _rootOrder)
                {
                    RootBehaviour root = _roots[name];
                    route.Element(Ns + "sequence").Add(Edge(root.Id, Int(root.Source, "Frequency", null), root.Condition));
                }
                _nodes.Insert(0, route);
                int anchorX, anchorY, tilesX, tilesY;
                byte[] png = Composite(out anchorX, out anchorY, out tilesX, out tilesY);
                string features = "tick actions behaviours" + (_lifecycle ? " lifecycle" : "");
                if (_nodes.SelectMany(n => n.Descendants()).Any(n => n.Attribute("when") != null)) features += " expressions";
                var header = E("header", E("author", "ShimejiConvert"), E("title", _member), E("petname", _member),
                    E("version", "1"), E("info", "Tick conversion; unsupported constructs are refused."), E("application", 1),
                    E("icon", Convert.ToBase64String(IconBuilder.BuildIco(null))), E("features", features), E("clock", "tick"));
                var document = new XDocument(new XDeclaration("1.0", "utf-8", null), E("animations", header,
                    E("image", E("tilesx", tilesX), E("tilesy", tilesY), E("png", Convert.ToBase64String(png)),
                        E("transparency", "Alpha"), E("anchorx", anchorX), E("anchory", anchorY)),
                    E("spawns", E("spawn", new XAttribute("id", 0), new XAttribute("probability", 1),
                        E("x", "0"), E("y", "0"), E("next", new XAttribute("probability", 1), 1))),
                    E("animations", _nodes), E("childs")));
                var frameMap = new List<object>();
                for (int i = 0; i < _frames.Count; i++)
                {
                    Frame frame = _frames[i];
                    frameMap.Add(new { frame = i, image = frame.Image, anchor = new[] { frame.X, frame.Y }, size = new[] { frame.Width, frame.Height } });
                }
                string map = JsonSerializer.Serialize(new { schema = "shimeji-parity-map/1", imageSet = _member,
                    frames = frameMap, actions = _actionMap, children = _childMap, behaviours = _behaviourMap },
                    new JsonSerializerOptions { WriteIndented = true });
                string xml = document.ToString();
                if (png.Length > DesktopAICompanion.CompanionXmlValidator.MaximumImageBytes
                    || Encoding.UTF8.GetByteCount(xml) > DesktopAICompanion.CompanionXmlValidator.MaximumXmlBytes)
                    throw new InvalidDataException("Tick member exceeds the host's image or XML byte limit: " + _member);
                bool crossMember = _nodes.SelectMany(n => n.Descendants()).Any(n =>
                    (n.Name == Ns + "param" || n.Name == Ns + "bind") &&
                    IsMemberParameter((string)n.Attribute("name")));
                return new MemberOutput { Name = _member, Xml = xml, Map = map + "\n", CrossMember = crossMember };
            }

            private void CollectRoots(ShimejiSourceElement parent, string condition)
            {
                foreach (ShimejiSourceElement node in parent.Children)
                {
                    if (node.Kind == ShimejiSourceKind.Condition)
                    {
                        Check(node, "Condition");
                        CollectRoots(node, Combine(condition, SelectionCondition(node)));
                    }
                    else if (node.Kind == ShimejiSourceKind.Behavior)
                    {
                        string name = Required(node, "Name");
                        if (!_roots.ContainsKey(name)) _rootOrder.Add(name);
                        _roots[name] = new RootBehaviour { Source = node, Condition = Combine(condition, SelectionCondition(node)) };
                    }
                    else Fail(node, "unsupported root behaviour entry");
                }
            }

            private int EmitAction(ShimejiSourceElement source, bool nested)
            {
                string name = Required(source, "Name"), type = Required(source, "Type");
                if (!Types.Contains(type)) Fail(source, "unsupported action type '" + type + "'");
                if (nested)
                {
                    if (_actions.ContainsKey(name)) Fail(source, "a nested action needs a unique name for this first lowering: '" + name + "'");
                    _actions.Add(name, source);
                }
                int id = _next++;
                var act = E("act", new XAttribute("type", type));
                string cls = Value(source, "Class");
                if (type == "Embedded")
                {
                    const string prefix = "com.group_finity.mascot.action.";
                    if (cls == null || !cls.StartsWith(prefix, StringComparison.Ordinal) || !Classes.Contains(cls.Substring(prefix.Length)))
                        Fail(source, "unsupported embedded class '" + cls + "'");
                    cls = cls.Substring(prefix.Length);
                    act.Add(new XAttribute("class", cls));
                    if (cls == "Breed" || cls == "Transform") _lifecycle = true;
                }
                else if (cls != null) Fail(source, "Class is supported only on Embedded");
                foreach (ShimejiSourceAttribute attribute in Attributes(source))
                {
                    if (attribute.SchemaKey == "Name" || attribute.SchemaKey == "Type" || attribute.SchemaKey == "Class") continue;
                    if (!Parameters.Contains(attribute.Name)) Fail(source, "unsupported action attribute '" + attribute.QualifiedName + "'");
                    act.Add(MemberParameter("param", attribute, null));
                    if (attribute.Name == "BorderType")
                    {
                        if (attribute.RawValue != "Floor" && attribute.RawValue != "Wall" && attribute.RawValue != "Ceiling" && attribute.RawValue != "")
                            Fail(source, "unsupported border '" + attribute.RawValue + "'");
                        act.Add(new XAttribute("border", attribute.RawValue));
                    }
                }
                XElement node = Node(id, name, act);
                _nodes.Add(node);
                _actionMap.Add(Key(id), Ref(_source.Actions, source));
                bool composite = type == "Sequence" || type == "Select";
                XElement variants = E("variants"), children = E("children", new XAttribute("mode", type == "Select" ? "select" : "sequence"));
                int childIndex = 0;
                foreach (ShimejiSourceElement child in source.Children)
                {
                    if (child.Kind == ShimejiSourceKind.Animation && !composite) variants.Add(EmitVariant(child));
                    else if (composite && (child.Kind == ShimejiSourceKind.ActionReference || child.Kind == ShimejiSourceKind.Action))
                    {
                        string target = Required(child, "Name");
                        if (child.Kind == ShimejiSourceKind.Action) EmitAction(child, true);
                        if (child.Kind == ShimejiSourceKind.ActionReference && !_globalActions.Contains(target))
                            Fail(child, "missing global action '" + target + "'");
                        var reference = E("child", new XAttribute("animation", target));
                        if (child.Kind == ShimejiSourceKind.ActionReference)
                            foreach (ShimejiSourceAttribute attribute in Attributes(child))
                            {
                                if (attribute.SchemaKey == "Name") continue;
                                if (!Parameters.Contains(attribute.Name)) Fail(child, "unsupported reference binding '" + attribute.QualifiedName + "'");
                                reference.Add(MemberParameter("bind", attribute, _actions[target]));
                            }
                        children.Add(reference);
                        _childMap.Add(Key(id) + ":" + childIndex++, Ref(_source.Actions, child));
                    }
                    else Fail(child, "unsupported child of action '" + name + "'");
                }
                if (composite)
                {
                    if (!children.Elements().Any()) Fail(source, "empty composite");
                    act.Add(children);
                }
                else
                {
                    if (!variants.Elements().Any()) Fail(source, "posed action has no Animation");
                    act.Add(variants);
                }
                return id;
            }

            private XElement EmitVariant(ShimejiSourceElement source)
            {
                Check(source, "Condition", "IsTurn");
                var variant = E("variant");
                string condition = SelectionCondition(source);
                if (condition != null) variant.Add(new XAttribute("when", condition));
                if (source.Attribute("IsTurn") != null) variant.Add(new XAttribute("turn", Bool(source, "IsTurn")));
                foreach (ShimejiSourceElement child in source.Children)
                {
                    if (child.Kind != ShimejiSourceKind.Pose) Fail(child, "only Pose is supported in an Animation");
                    Check(child, "Image", "ImageAnchor", "Velocity", "Duration");
                    int dx, dy;
                    Pair(Required(child, "Velocity"), child, out dx, out dy);
                    int ticks = Int(child, "Duration", null);
                    if (ticks < 1 || ticks > 100000) Fail(child, "pose duration must be 1 to 100000");
                    int frame = -1;
                    string image = Value(child, "Image");
                    if (image != null)
                    {
                        int x, y;
                        Pair(Required(child, "ImageAnchor"), child, out x, out y);
                        frame = AddFrame(image, x, y, child);
                    }
                    variant.Add(E("pose", new XAttribute("frame", frame), new XAttribute("ticks", ticks), new XAttribute("dx", dx), new XAttribute("dy", dy)));
                }
                if (!variant.Elements().Any()) Fail(source, "Animation has no poses");
                return variant;
            }

            private void EmitBehaviour(ShimejiSourceElement source, string condition, int id)
            {
                string name = Required(source, "Name"), runs = Value(source, "Action") ?? name;
                if (!_globalActions.Contains(runs)) Fail(source, "behaviour names missing global action '" + runs + "'");
                int frequency = Int(source, "Frequency", null);
                if (frequency < 0 || frequency > 1000000) Fail(source, "unsupported behaviour frequency");
                bool add = true;
                bool rootDefinition = source.Kind == ShimejiSourceKind.Behavior;
                foreach (ShimejiSourceElement list in rootDefinition ? source.Children : Enumerable.Empty<ShimejiSourceElement>())
                {
                    if (list.Kind != ShimejiSourceKind.NextBehaviorList) Fail(list, "unsupported behaviour child");
                    Check(list, "Add");
                    add = Bool(list, "Add");
                }
                var behaviour = E("behaviour", new XAttribute("name", name), new XAttribute("runs", runs),
                    new XAttribute("frequency", frequency), new XAttribute("hidden", Bool(source, "Hidden")));
                if (rootDefinition) behaviour.Add(new XAttribute("add", add));
                foreach (ShimejiSourceAttribute attribute in Attributes(source))
                {
                    if (new[] { "Name", "Action", "Frequency", "Hidden", "Condition" }.Contains(attribute.SchemaKey)) continue;
                    if (attribute.SchemaKey == "Toggleable")
                    {
                        if (Bool(source, "Toggleable")) Fail(source, "toggleable behaviours are not lowered yet");
                        continue;
                    }
                    if (_source.Actions.Schema != _source.Behaviors.Schema)
                        Fail(source, "cross-schema behaviour bindings need raw target-schema resolution before lowering");
                    if (!Parameters.Contains(attribute.Name)) Fail(source, "unsupported behaviour binding '" + attribute.QualifiedName + "'");
                    behaviour.Add(MemberParameter("bind", attribute, _actions[runs]));
                }
                XElement node = Node(id, "beh:" + name, behaviour);
                _nodes.Add(node);
                _behaviourMap.Add(Key(id), Ref(_source.Behaviors, source));
                // Continuations are subsequently looked up through the ROOT behaviour by name by the host.
                if (rootDefinition)
                    foreach (ShimejiSourceElement list in source.Children) EmitNextEntries(list, null, node.Element(Ns + "sequence"));
            }

            private void EmitNextEntries(ShimejiSourceElement parent, string condition, XElement sequence)
            {
                foreach (ShimejiSourceElement entry in parent.Children)
                {
                    if (entry.Kind == ShimejiSourceKind.Condition)
                    {
                        Check(entry, "Condition");
                        EmitNextEntries(entry, Combine(condition, SelectionCondition(entry)), sequence);
                    }
                    else if (entry.Kind == ShimejiSourceKind.BehaviorReference)
                    {
                        string name = Required(entry, "Name");
                        if (!_roots.ContainsKey(name)) Fail(entry, "next reference needs a root behaviour named '" + name + "'");
                        int id = _next++;
                        string when = Combine(condition, SelectionCondition(entry));
                        EmitBehaviour(entry, when, id);
                        sequence.Add(Edge(id, Int(entry, "Frequency", null), when));
                    }
                    else Fail(entry, "unsupported next-list entry");
                }
            }

            private int AddFrame(string image, int x, int y, ShimejiSourceElement source)
            {
                if (x < -4096 || x > 4096 || y < -4096 || y > 4096) Fail(source, "authored anchor is outside this lowering's geometry limits");
                string relative = image.TrimStart('/', '\\').Replace('\\', '/');
                if (relative.Length == 0 || relative.Split('/').Any(p => p == ".." || p == "." || p.Contains(":"))) Fail(source, "unsafe sprite path '" + image + "'");
                if (!relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) Fail(source, "only PNG sprites are supported");
                string path = Path.GetFullPath(Path.Combine(_pack, "img", _member, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!Inside(Path.Combine(_pack, "img", _member), path)) Fail(source, "sprite leaves its image set");
                SafeEntry(path);
                string key = relative + "|" + x + "|" + y;
                int index;
                if (_frameIds.TryGetValue(key, out index)) return index;
                if (_frames.Count >= SpriteSheetBuilder.MaxTiles) Fail(source, "too many sprite identities");
                using (var bitmap = new Bitmap(path))
                {
                    if (bitmap.Width > SpriteSheetBuilder.MaxSheetDimension || bitmap.Height > SpriteSheetBuilder.MaxSheetDimension)
                        Fail(source, "sprite exceeds image dimension limit");
                    index = _frames.Count;
                    _frames.Add(new Frame { Image = "img/" + _member + "/" + relative, Path = path, X = x, Y = y,
                        Width = bitmap.Width, Height = bitmap.Height });
                }
                _frameIds.Add(key, index);
                return index;
            }

            private XElement MemberParameter(string tag, ShimejiSourceAttribute attribute, ShimejiSourceElement target)
            {
                if (IsMemberParameter(attribute.Name) && attribute.RawValue == _member)
                {
                    ShimejiSourceAttribute inherited = target == null ? null : target.Attribute(attribute.SchemaKey);
                    // Dropping a binding must not expose a different member inherited from the action.
                    if (inherited == null || inherited.RawValue == _member) return null;
                }
                return Parameter(tag, attribute);
            }

            private byte[] Composite(out int anchorX, out int anchorY, out int tilesX, out int tilesY)
            {
                if (_frames.Count == 0) throw new InvalidDataException("Tick member needs at least one visible pose: " + _member);
                Frame first = _frames[0];
                foreach (Frame frame in _frames)
                    if (frame.Width != first.Width || frame.Height != first.Height || frame.X != first.X || frame.Y != first.Y
                        || frame.X < 0 || frame.X > frame.Width || frame.Y < 0 || frame.Y > frame.Height)
                        throw new InvalidDataException("TICK_GEOMETRY_UNSUPPORTED " + _member + ": mixed authored image bounds or anchors need per-pose host bounds (" + frame.Image + ").");
                int left = 0, right = 0, top = 0, bottom = 0;
                foreach (Frame frame in _frames)
                {
                    left = Math.Max(left, frame.X); right = Math.Max(right, frame.Width - frame.X);
                    top = Math.Max(top, frame.Y); bottom = Math.Max(bottom, frame.Height - frame.Y);
                }
                int width = checked(left + right), height = checked(top + bottom);
                if (width < 1 || height < 1 || width > SpriteSheetBuilder.MaxCell || height > SpriteSheetBuilder.MaxCell)
                    throw new InvalidDataException("Tick sheet requires cells outside 1.." + SpriteSheetBuilder.MaxCell + "; this lowering never rescales source geometry.");
                anchorX = left; anchorY = top;
                tilesX = (int)Math.Ceiling(Math.Sqrt(_frames.Count));
                tilesY = (_frames.Count + tilesX - 1) / tilesX;
                if (tilesX * width > SpriteSheetBuilder.MaxSheetDimension || tilesY * height > SpriteSheetBuilder.MaxSheetDimension)
                    throw new InvalidDataException("Tick sheet exceeds the sheet dimension limit.");
                using (var sheet = new Bitmap(tilesX * width, tilesY * height, PixelFormat.Format32bppArgb))
                {
                    BitmapData destination = sheet.LockBits(new Rectangle(0, 0, sheet.Width, sheet.Height),
                        ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        var blank = new byte[checked(sheet.Width * 4)];
                        for (int y = 0; y < sheet.Height; y++)
                            Marshal.Copy(blank, 0, IntPtr.Add(destination.Scan0, checked(y * destination.Stride)), blank.Length);
                        for (int i = 0; i < _frames.Count; i++)
                        {
                            Frame frame = _frames[i];
                            using (var bitmap = new Bitmap(frame.Path))
                                CopyPixels(bitmap, destination, (i % tilesX) * width + left - frame.X, (i / tilesX) * height + top - frame.Y);
                        }
                    }
                    finally { sheet.UnlockBits(destination); }
                    using (var output = new MemoryStream())
                    {
                        sheet.Save(output, ImageFormat.Png);
                        return output.ToArray();
                    }
                }
            }

            private static void CopyPixels(Bitmap source, BitmapData destination, int x, int y)
            {
                BitmapData pixels = source.LockBits(new Rectangle(0, 0, source.Width, source.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[checked(source.Width * 4)];
                    for (int line = 0; line < source.Height; line++)
                    {
                        Marshal.Copy(IntPtr.Add(pixels.Scan0, checked(line * pixels.Stride)), row, 0, row.Length);
                        Marshal.Copy(row, 0, IntPtr.Add(destination.Scan0, checked((y + line) * destination.Stride + x * 4)), row.Length);
                    }
                }
                finally { source.UnlockBits(pixels); }
            }

            private string Ref(ShimejiSourceDocument document, ShimejiSourceElement source)
            { return _member + "|" + document.PackRelativePath.Replace('\\', '/') + "|" + source.SourceElementPath; }
        }

        private static XElement E(string name, params object[] content) { return new XElement(Ns + name, content); }
        private static XElement Node(int id, string name, XElement kind)
        {
            return E("animation", new XAttribute("id", id), E("name", name),
                E("start", E("x", "0"), E("y", "0"), E("interval", "40")),
                E("end", E("x", "0"), E("y", "0"), E("interval", "40")),
                kind, E("sequence", new XAttribute("repeat", "0"), new XAttribute("repeatfrom", 0)));
        }
        private static XElement Edge(int id, int weight, string condition)
        {
            var edge = E("next", new XAttribute("probability", weight), id);
            if (condition != null) edge.Add(new XAttribute("when", condition));
            return edge;
        }
        private static XElement Parameter(string tag, ShimejiSourceAttribute attribute)
        {
            // Java accepts hexadecimal floating literals here. The host's current literal reader
            // retains those as strings, so this initial lowering must refuse them.
            string literal = attribute.RawValue.Trim();
            bool javaOnlyNumber = Regex.IsMatch(literal, @"^[+-]?0[xX](?:[0-9a-fA-F]+(?:\.[0-9a-fA-F]*)?|\.[0-9a-fA-F]+)[pP][+-]?[0-9]+[dDfF]?$");
            if (javaOnlyNumber) throw new InvalidDataException("TICK_LITERAL_UNSUPPORTED " + attribute.SourceKey + ": Java floating literal needs faithful host parsing.");
            return E(tag, new XAttribute("name", attribute.Name), new XAttribute("value", attribute.RawValue), new XAttribute("cache", "none"));
        }
        private static bool IsMemberParameter(string name)
        { return name == "BornMascot" || name == "TransformMascot"; }
        private static IEnumerable<ShimejiSourceAttribute> Attributes(ShimejiSourceElement source)
        {
            foreach (ShimejiSourceAttribute attribute in source.Attributes)
            {
                if (attribute.IsNamespaceDeclaration) continue;
                if (attribute.SchemaKey == null) Fail(source, "unrecognised source attribute '" + attribute.QualifiedName + "'");
                if (attribute.Cache != ShimejiSourceCache.Literal) Fail(source, "expressions are not lowered yet: " + attribute.QualifiedName);
                yield return attribute;
            }
        }
        private static void Check(ShimejiSourceElement source, params string[] allowed)
        {
            foreach (ShimejiSourceAttribute attribute in Attributes(source))
                if (!allowed.Contains(attribute.SchemaKey)) Fail(source, "unsupported attribute '" + attribute.QualifiedName + "'");
            if (!string.IsNullOrWhiteSpace(source.Text)) Fail(source, "unexpected source text");
        }
        private static string Value(ShimejiSourceElement source, string key)
        {
            ShimejiSourceAttribute attribute = source.Attribute(key);
            if (attribute == null) return null;
            if (attribute.Cache != ShimejiSourceCache.Literal) Fail(source, "expressions are not lowered yet: " + key);
            return attribute.RawValue;
        }
        private static string Required(ShimejiSourceElement source, string key)
        {
            string value = Value(source, key);
            if (string.IsNullOrEmpty(value)) Fail(source, "missing " + key);
            return value;
        }
        private static int Int(ShimejiSourceElement source, string key, int? fallback)
        {
            string text = Value(source, key);
            if (text == null && fallback.HasValue) return fallback.Value;
            int result;
            if (text == null || !int.TryParse(text, NumberStyles.AllowLeadingSign, Invariant, out result))
                throw new InvalidDataException(source.SourceKey + ": " + key + " must be a literal Java integer");
            return result;
        }
        private static bool Bool(ShimejiSourceElement source, string key)
        { return string.Equals(Value(source, key), "true", StringComparison.OrdinalIgnoreCase); }
        private static void Pair(string value, ShimejiSourceElement source, out int x, out int y)
        {
            x = 0;
            y = 0;
            string[] parts = value.Split(',');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, Invariant, out x)
                || !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, Invariant, out y))
                throw new InvalidDataException(source.SourceKey + ": expected a literal integer pair");
        }
        private static string SelectionCondition(ShimejiSourceElement source)
        {
            string value = Value(source, "Condition");
            if (value == null) return null;
            if (value != "true" && value != "false") Fail(source, "only literal boolean selection conditions are lowered yet");
            return value;
        }
        private static string Combine(string first, string second)
        { return first == null ? second : second == null ? first : "(" + first + ") && (" + second + ")"; }
        private static string Key(int id) { return id.ToString(Invariant); }
        private static HashSet<string> Set(string text) { return new HashSet<string>(text.Split(' '), StringComparer.Ordinal); }
        private static void Fail(ShimejiSourceElement source, string message)
        { throw new InvalidDataException(source.SourceKey + " (" + source.Line + "," + source.Column + "): " + message); }
        private static bool Inside(string root, string path)
        {
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            path = Path.GetFullPath(path);
            return string.Equals(root, path, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        private static void SafeEntry(string path)
        {
            string full = Path.GetFullPath(path), current = Path.GetPathRoot(full);
            foreach (string part in full.Substring(current.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string lower = part.ToLowerInvariant();
                if (lower.EndsWith(".env", StringComparison.Ordinal) || lower.StartsWith(".env.", StringComparison.Ordinal)
                    || lower == ".npmrc" || lower == "credentials" || lower.StartsWith("privatekey", StringComparison.Ordinal)
                    || new[] { ".pem", ".key", ".p12", ".pfx", ".keystore" }.Any(s => lower.EndsWith(s, StringComparison.Ordinal)))
                    throw new InvalidDataException("Protected path refused: " + part);
                current = Path.Combine(current, part);
                if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Reparse path refused: " + current);
            }
        }
    }
}
