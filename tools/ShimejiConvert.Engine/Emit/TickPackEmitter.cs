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
    /// <summary>A strict tick-format lowering. Unsupported source constructs fail with their source location;
    /// it refuses rather than approximates. It does not change the classic converter or claim action-family
    /// support. Each pose retains its source image bounds inside a top-left-aligned sheet cell.</summary>
    public static class TickPackEmitter
    {
        private static readonly XNamespace Ns = "https://esheep.petrucci.ch/";
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly HashSet<string> Types = Set("Stay Move Animate Sequence Select Embedded");
        // The closed class list of grimoire/06 section 5: every class the host's tick runtime constructs
        // (TickWorld.CreateEmbedded and its four family files), the deprecated Broadcast aliases included.
        private static readonly HashSet<string> Classes = Set("Fall Jump Dragged Regist Look Turn Offset Mute BringToFront SelfDestruct "
            + "Breed BreedMove BreedJump BreedFall Transform ScanMove ScanJump ScanInteract Interact ComplexMove ComplexJump "
            + "MoveWithTurn Broadcast BroadcastStay BroadcastMove BroadcastJump WalkWithIE FallWithIE JumpWithIE ThrowIE MinimizeIE");
        // Every parameter some host class reads, by the exact name its family file reads (ActionBase's Duration,
        // Condition, Draggable, Affordance, BorderType and Loop; then TickMotionFamily, TickInstantFamily,
        // TickSocialFamily, TickWindowFamily, Breed and Transform), which is the source's English schema name. A name
        // no class reads stays refused, so a misspelt attribute is not carried as a parameter nothing uses.
        private static readonly HashSet<string> Parameters = Set("Duration Condition Draggable Affordance BorderType Loop TargetX TargetY InitialVX InitialVY RegistanceX RegistanceY Gravity OffsetX OffsetY OffsetType BornX BornY BornBehavior BornMascot BornCount BornTransient TransformMascot TransformBehavior "
            + "LookRight X Y Sound VelocityParam Arc BornInterval Behavior TargetBehavior TargetLook Look Characteristics IeOffsetX IeOffsetY");
        // An action-context script sees the parameters any class reads and the locals the source publishes, as the
        // validator's scope does (ShimejiFormatValidator.ScopeFor, which also adds the action's own and bound names;
        // every name this emitter writes is already in KnownParameters, so the two scopes agree).
        private static readonly Func<string, bool> ActionScope = name =>
            Array.IndexOf(ShimejiFormatValidator.KnownParameters, name) >= 0 || Array.IndexOf(ShimejiFormatValidator.Locals, name) >= 0;
        // A behaviour-selection condition sees the header constants only, and this emitter writes none.
        private static readonly Func<string, bool> SelectionScope = name => false;

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
            List<PreparedMember> prepared = PrepareMembers(packRoot, false);
            if (mainMember != null && !prepared.Any(m => m.Name == mainMember))
                throw new InvalidDataException("TICK_PACK_MAIN_UNKNOWN: the requested main image set is not in this pack.");
            if (prepared.Any(m => m.CrossMember) || mainMember != null || prepared.Any(m => HiddenInSourceChooser(m.Name)))
            {
                if (mainMember == null)
                    throw new InvalidDataException("TICK_PACK_MAIN_REQUIRED: provide the main image set for pack output.");
                // Output formatting and converter changes must not change a source pack's identity.
                string packId = SourcePackId(prepared);
                foreach (PreparedMember member in prepared)
                {
                    XDocument document = XDocument.Parse(member.Xml);
                    XElement header = document.Root.Element(Ns + "header");
                    XElement features = header.Element(Ns + "features");
                    if (!features.Value.Split(' ').Contains("packs")) features.Value += " packs";
                    // Preserve the source chooser's explicit visibility rule. Startup and egg roles
                    // still require caller metadata rather than inference from a name.
                    bool hidden = HiddenInSourceChooser(member.Name);
                    string role = member.Name == mainMember ? "main" : hidden ? "helper" : "form";
                    header.Add(E("pack", new XAttribute("id", packId), new XAttribute("member", member.Name),
                        new XAttribute("role", role), new XAttribute("hidden", hidden)));
                    member.Xml = document.ToString();
                }
            }
            // Prepare every member before writing, so an unsupported input does not leave half a pack.
            var paths = new List<string>();
            foreach (PreparedMember member in prepared)
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

        internal static List<PreparedMember> PrepareMembers(string packRoot, bool captureRefusals)
        {
            packRoot = Path.GetFullPath(packRoot ?? throw new ArgumentNullException("packRoot"));
            SafeEntry(packRoot);
            string images = Path.Combine(packRoot, "img");
            SafeEntry(images);
            if (!Directory.Exists(images)) throw new InvalidDataException("Tick pack needs an img directory.");
            string[] members = Directory.GetDirectories(images);
            Array.Sort(members, StringComparer.Ordinal);
            if (members.Length == 0) throw new InvalidDataException("Tick pack has no image-set members.");
            var prepared = new List<PreparedMember>();
            foreach (string memberPath in members)
            {
                string member = Path.GetFileName(memberPath);
                try
                {
                    SafeEntry(memberPath);
                    ShimejiSourceConfig source = ShimejiParser.ParseSourcePack(packRoot, member);
                    prepared.Add(new MemberBuilder(packRoot, member, source).Build());
                }
                catch (Exception failure) when (failure is InvalidDataException || failure is IOException
                    || failure is UnauthorizedAccessException || failure is System.Xml.XmlException
                    || failure is ArgumentException || failure is ExternalException)
                {
                    if (!captureRefusals) throw;
                    prepared.Add(new PreparedMember { Name = member, Error = member + ": " + failure.Message });
                }
            }
            return prepared;
        }

        internal static string SourcePackId(IEnumerable<PreparedMember> members)
        {
            PreparedMember[] prepared = members.ToArray();
            if (prepared.Length == 0 || prepared.Any(m => m.Error != null || m.SourceIdentity == null))
                throw new InvalidDataException("TICK_PACK_ID_UNAVAILABLE: source preparation is incomplete.");
            return "shimeji-" + Hash(Encoding.UTF8.GetBytes("shimeji-source-pack/1\n"
                + string.Join("\n", prepared.Select(m => m.Name + "\n" + m.SourceIdentity)))).Substring(0, 24);
        }

        internal static bool HiddenInSourceChooser(string name)
        {
            return name.StartsWith(".", StringComparison.Ordinal)
                || string.Equals(name, "unused", StringComparison.OrdinalIgnoreCase);
        }

        internal sealed class PreparedMember { public string Name, Xml, Map, SourceIdentity, Error; public bool CrossMember; }
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
        private sealed class PairAnchor { public int X, Y; }

        private sealed class MemberBuilder
        {
            // Match the host's per-activation limits before preparing any output files.
            private const int ActivationDepthLimit = 32, ActivationSizeLimit = 4096;
            private sealed class ActivationBounds { public int Count, Depth; }
            private readonly string _pack, _member;
            private readonly ShimejiSourceConfig _source;
            private readonly List<XElement> _nodes = new List<XElement>();
            private readonly Dictionary<string, ShimejiSourceElement> _actions = new Dictionary<string, ShimejiSourceElement>(StringComparer.Ordinal);
            private readonly HashSet<string> _globalActions = new HashSet<string>(StringComparer.Ordinal);
            private readonly List<ShimejiSourceElement> _actionOrder = new List<ShimejiSourceElement>();
            private readonly Dictionary<string, RootBehaviour> _roots = new Dictionary<string, RootBehaviour>(StringComparer.Ordinal);
            private readonly List<string> _rootOrder = new List<string>();
            private readonly List<Frame> _frames = new List<Frame>();
            private readonly Dictionary<string, int> _frameIds = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly Dictionary<string, PairAnchor> _pairAnchors = new Dictionary<string, PairAnchor>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _actionMap = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _behaviourMap = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _childMap = new Dictionary<string, string>(StringComparer.Ordinal);
            private int _next = 2;
            // The feature tokens the validator demands for what this member USES (grimoire/06 sections 1 and 10).
            private bool _lifecycle, _social, _windows, _hotspots, _expressions, _variables;

            public MemberBuilder(string pack, string member, ShimejiSourceConfig source)
            { _pack = pack; _member = member; _source = source; }

            public PreparedMember Build()
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
                        _actionOrder.Add(action);
                    }
                }
                foreach (ShimejiSourceElement list in _source.Behaviors.Root.Children)
                {
                    if (list.Kind != ShimejiSourceKind.BehaviorList) Fail(list, "only BehaviorList is supported at the behaviour document root");
                    Check(list);
                    CollectRoots(list, null);
                }
                if (_roots.Count == 0 || _actions.Count == 0) throw new InvalidDataException("Tick member has no actions or root behaviours: " + _member);
                // Source image pairs are loaded eagerly, including from unused actions. Their cache
                // excludes the authored anchor, so later poses with the same pair use the first one.
                foreach (ShimejiSourceElement action in _actionOrder) IndexPairAnchors(action);
                foreach (ShimejiSourceElement action in _actionOrder) EmitAction(action, false);
                ValidateActivations();
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
                // Token order is fixed so a member that uses none of the newer extensions keeps its exact earlier bytes.
                string features = "tick actions behaviours" + (_lifecycle ? " lifecycle" : "") + (_social ? " social" : "")
                    + (_windows ? " windows" : "") + (_hotspots ? " hotspots" : "");
                if (_expressions || _nodes.SelectMany(n => n.Descendants()).Any(n => n.Attribute("when") != null)) features += " expressions";
                if (_variables) features += " variables";
                // Display names have a shorter host limit than exact pack member keys.
                int displayLength = Math.Min(_member.Length, 128);
                if (displayLength < _member.Length && char.IsHighSurrogate(_member[displayLength - 1])) displayLength--;
                string displayName = _member.Substring(0, displayLength);
                var header = E("header", E("author", "ShimejiConvert"), E("title", _member), E("petname", displayName),
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
                return new PreparedMember { Name = _member, Xml = xml, Map = map + "\n", CrossMember = crossMember,
                    SourceIdentity = SourceIdentity() };
            }

            private void CollectRoots(ShimejiSourceElement parent, string condition)
            {
                foreach (ShimejiSourceElement node in parent.Children)
                {
                    if (node.Kind == ShimejiSourceKind.Condition)
                    {
                        Check(node, "Condition");
                        CollectRoots(node, CheckedCombine(condition, SelectionCondition(node), node));
                    }
                    else if (node.Kind == ShimejiSourceKind.Behavior)
                    {
                        string name = Required(node, "Name");
                        if (!_roots.ContainsKey(name)) _rootOrder.Add(name);
                        _roots[name] = new RootBehaviour { Source = node, Condition = CheckedCombine(condition, SelectionCondition(node), node) };
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
                    // The feature token each family needs, judged by the validator's own class lists (grimoire/06
                    // section 10: what a document USES is declared). ComplexMove and ComplexJump add theirs by
                    // their Characteristics (NoteCharacteristics).
                    if (Array.IndexOf(ShimejiFormatValidator.LifecycleClasses, cls) >= 0) _lifecycle = true;
                    if (Array.IndexOf(ShimejiFormatValidator.SocialClasses, cls) >= 0) _social = true;
                    if (Array.IndexOf(ShimejiFormatValidator.WindowClasses, cls) >= 0) _windows = true;
                }
                else if (cls != null) Fail(source, "Class is supported only on Embedded");
                XElement guard = null, offers = null;
                foreach (ShimejiSourceAttribute attribute in Attributes(source))
                {
                    if (attribute.SchemaKey == "Name" || attribute.SchemaKey == "Type" || attribute.SchemaKey == "Class") continue;
                    if (!Parameters.Contains(attribute.Name)) Fail(source, "unsupported action attribute '" + attribute.QualifiedName + "'");
                    act.Add(MemberParameter("param", attribute, null, source));
                    // grimoire/06 4.1: a literal Affordance is the action's offer, replacing the companion's offer list
                    // at each step (ActionBase.java:88-101). The parameter stays too: the scan classes read the
                    // affordance they seek by that name (TickSocialFamily.Affordance). A Java-blank one offers nothing
                    // (the runtime skips it), so it writes no offer. A scripted one was refused in Parameter.
                    if (attribute.Name == "Affordance" && !JavaBlank(attribute.RawValue))
                    {
                        if (attribute.RawValue.Length > 256) Fail(source, "TICK_OFFER_LIMIT: an offer is at most 256 characters");
                        offers = E("offers", E("offer", new XAttribute("value", attribute.RawValue)));
                        _social = true;
                    }
                    // grimoire/06 4.1: a source Condition written with a cache wrapper is a cached parameter that the
                    // guard's when NAMES, so a ${} Condition holds its first value across both checks of a step. The
                    // runtime's guard read evaluates that name through the parameter, so its cache and its draw site
                    // (param:Condition) are the parameter's own. A literal Condition stays a plain parameter, as before.
                    if (attribute.Name == "Condition" && attribute.Cache != ShimejiSourceCache.Literal)
                        guard = E("guard", new XAttribute("when", "Condition"));
                    // A scripted BorderType is read through its parameter at init (BorderedAction.java:39-51, and
                    // TickBorderedAction.Init reads the parameter first); the act's border attribute is the literal form.
                    if (attribute.Name == "BorderType" && attribute.Cache == ShimejiSourceCache.Literal)
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
                // Parameters a scripted Animation Condition is lowered to; they join the act's own parameters, ahead of
                // the guard and the variants as the schema orders them.
                var conditions = new List<XElement>();
                int childIndex = 0, animationIndex = 0;
                foreach (ShimejiSourceElement child in source.Children)
                {
                    if (child.Kind == ShimejiSourceKind.Animation)
                    {
                        // Composite animations do not run, but the source still loads their images.
                        XElement variant = EmitVariant(child, ++animationIndex, composite ? null : conditions);
                        if (!composite) variants.Add(variant);
                    }
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
                                reference.Add(MemberParameter("bind", attribute, _actions[target], child));
                            }
                        children.Add(reference);
                        _childMap.Add(Key(id) + ":" + childIndex++, Ref(_source.Actions, child));
                    }
                    else Fail(child, "unsupported child of action '" + name + "'");
                }
                act.Add(conditions);
                if (guard != null) act.Add(guard);
                if (offers != null) act.Add(offers);
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

            private void ValidateActivations()
            {
                var known = new Dictionary<string, ActivationBounds>(StringComparer.Ordinal);
                var active = new HashSet<string>(StringComparer.Ordinal);
                var path = new List<string>();
                foreach (ShimejiSourceElement action in _actions.Values)
                    InspectActivation(action, known, active, path);
            }

            private ActivationBounds InspectActivation(ShimejiSourceElement source,
                Dictionary<string, ActivationBounds> known, HashSet<string> active, List<string> path)
            {
                string name = Required(source, "Name");
                if (active.Contains(name))
                    Fail(source, "TICK_COMPOSITE_CYCLE: activation repeats '" + name + "' (" + string.Join(" -> ", path) + ").");
                if (path.Count >= ActivationDepthLimit)
                    Fail(source, "TICK_COMPOSITE_DEPTH: activation exceeds " + ActivationDepthLimit + " actions deep.");
                ActivationBounds cached;
                if (known.TryGetValue(name, out cached))
                {
                    // Reused subtrees still consume the caller's remaining depth budget.
                    if (path.Count + cached.Depth > ActivationDepthLimit)
                        Fail(source, "TICK_COMPOSITE_DEPTH: activation exceeds " + ActivationDepthLimit + " actions deep.");
                    return cached;
                }
                active.Add(name);
                path.Add(name);
                int count = 1, depth = 1;
                string type = Required(source, "Type");
                if (type == "Sequence" || type == "Select")
                    foreach (ShimejiSourceElement child in source.Children)
                    {
                        if (child.Kind != ShimejiSourceKind.Action && child.Kind != ShimejiSourceKind.ActionReference) continue;
                        ActivationBounds bounds = InspectActivation(_actions[Required(child, "Name")], known, active, path);
                        count += bounds.Count;
                        if (count > ActivationSizeLimit)
                            Fail(source, "TICK_COMPOSITE_SIZE: activation builds more than " + ActivationSizeLimit + " actions.");
                        depth = Math.Max(depth, 1 + bounds.Depth);
                    }
                path.RemoveAt(path.Count - 1);
                active.Remove(name);
                var result = new ActivationBounds { Count = count, Depth = depth };
                known.Add(name, result);
                return result;
            }

            private XElement EmitVariant(ShimejiSourceElement source, int index, List<XElement> conditions)
            {
                Check(source, "Condition", "IsTurn");
                var variant = E("variant");
                ShimejiSourceAttribute scripted = source.Attribute("Condition");
                if (scripted != null && scripted.Cache != ShimejiSourceCache.Literal)
                {
                    // grimoire/06 4.1: the source caches an Animation Condition by its own ${}/#{} kind
                    // (Animation.java:39-47) while the format evaluates a variant's when at every lookup, so the body
                    // becomes a parameter with that cache and the when names it. The name is not a source parameter
                    // name (those are all in Parameters), so no source attribute or binding can collide with it. A
                    // composite's animations never play: their body is still checked, and nothing is written.
                    string parameter = "AnimationCondition" + index.ToString(Invariant);
                    XElement lowered = ScriptParameter("param", parameter, scripted, source);
                    if (conditions != null) conditions.Add(lowered);
                    variant.Add(new XAttribute("when", parameter));
                }
                else
                {
                    string condition = SelectionCondition(source);
                    if (condition != null) variant.Add(new XAttribute("when", condition));
                }
                if (source.Attribute("IsTurn") != null) variant.Add(new XAttribute("turn", Bool(source, "IsTurn")));
                // grimoire/06 sections 3 and 4.1: a variant's <hotspots> follow its poses (the schema's order), whatever
                // order the source interleaves Pose and Hotspot children in.
                var hotspots = new List<XElement>();
                foreach (ShimejiSourceElement child in source.Children)
                {
                    if (child.Kind == ShimejiSourceKind.Hotspot)
                    {
                        hotspots.Add(EmitHotspot(child));
                        continue;
                    }
                    if (child.Kind != ShimejiSourceKind.Pose) Fail(child, "only Pose and Hotspot are supported in an Animation");
                    Check(child, "Image", "ImageRight", "ImageAnchor", "Velocity", "Duration");
                    int dx, dy;
                    Pair(Required(child, "Velocity"), child, out dx, out dy);
                    int ticks = Int(child, "Duration", null);
                    if (ticks < 1 || ticks > 100000) Fail(child, "pose duration must be 1 to 100000");
                    string image = Value(child, "Image");
                    string right = Value(child, "ImageRight");
                    var pose = E("pose", new XAttribute("frame", -1), new XAttribute("ticks", ticks), new XAttribute("dx", dx), new XAttribute("dy", dy));
                    if (image != null)
                    {
                        int x, y;
                        Pair(Required(child, "ImageAnchor"), child, out x, out y);
                        PairAnchor effective = _pairAnchors[PairKey(image, right, child)];
                        int frame = AddFrame(image, effective.X, effective.Y, child);
                        pose.SetAttributeValue("frame", frame);
                        pose.Add(new XAttribute("ax", effective.X), new XAttribute("ay", effective.Y),
                            new XAttribute("w", _frames[frame].Width), new XAttribute("h", _frames[frame].Height));
                        if (right != null)
                        {
                            int rightFrame = AddFrame(right, effective.X, effective.Y, child);
                            pose.Add(new XAttribute("right", rightFrame),
                                new XAttribute("rw", _frames[rightFrame].Width), new XAttribute("rh", _frames[rightFrame].Height));
                        }
                    }
                    else if (right != null) Fail(child, "TICK_RIGHT_ONLY_UNSUPPORTED: a right-only pose can depend on an earlier image-pair cache entry");
                    variant.Add(pose);
                }
                if (!variant.Elements().Any()) Fail(source, "Animation has no poses");
                if (hotspots.Count > 64) Fail(source, "TICK_HOTSPOT_LIMIT: an Animation has at most 64 hotspots in this format");
                if (hotspots.Count > 0)
                {
                    variant.Add(E("hotspots", hotspots));
                    // A composite's animations are not written (conditions is null there), so they use nothing.
                    if (conditions != null) _hotspots = true;
                }
                return variant;
            }

            /// <summary>A source Hotspot (Hotspot.java): a Rectangle or an Ellipse at Origin with Size, in the image's
            /// pixels, and the behaviour a press on it enters; without one the press is held (a head-pat).</summary>
            private XElement EmitHotspot(ShimejiSourceElement source)
            {
                Check(source, "Shape", "Origin", "Size", "Behaviour");
                string shape = Required(source, "Shape");
                // The source's two shapes, by their exact names; any other spelling refuses rather than guesses.
                string kind = shape == "Rectangle" ? "rect" : shape == "Ellipse" ? "ellipse" : null;
                if (kind == null) Fail(source, "TICK_HOTSPOT_SHAPE_UNSUPPORTED: Shape '" + shape + "' is neither Rectangle nor Ellipse");
                int x, y, width, height;
                Pair(Required(source, "Origin"), source, out x, out y);
                Pair(Required(source, "Size"), source, out width, out height);
                if (new[] { x, y, width, height }.Any(v => v < -4096 || v > 4096))
                    Fail(source, "hotspot origin or size is outside this lowering's geometry limits");
                var hotspot = E("hotspot", new XAttribute("shape", kind),
                    new XAttribute("origin", x.ToString(Invariant) + "," + y.ToString(Invariant)),
                    new XAttribute("size", width.ToString(Invariant) + "," + height.ToString(Invariant)));
                string behaviour = Value(source, "Behaviour");
                if (behaviour != null)
                {
                    // Entered by name (4.3), so it must be a route behaviour, as a next reference must.
                    if (!_roots.ContainsKey(behaviour)) Fail(source, "a hotspot needs a root behaviour named '" + behaviour + "'");
                    hotspot.Add(new XAttribute("behaviour", behaviour));
                }
                return hotspot;
            }

            private void IndexPairAnchors(ShimejiSourceElement action)
            {
                // The builder loads direct animations before recursing into nested actions.
                foreach (ShimejiSourceElement animation in action.Children.Where(n => n.Kind == ShimejiSourceKind.Animation))
                    foreach (ShimejiSourceElement pose in animation.Children.Where(n => n.Kind == ShimejiSourceKind.Pose))
                    {
                        string image = Value(pose, "Image");
                        if (image == null) continue;
                        string key = PairKey(image, Value(pose, "ImageRight"), pose);
                        int x, y;
                        Pair(Required(pose, "ImageAnchor"), pose, out x, out y);
                        if (!_pairAnchors.ContainsKey(key)) _pairAnchors.Add(key, new PairAnchor { X = x, Y = y });
                    }
                foreach (ShimejiSourceElement nested in action.Children.Where(n => n.Kind == ShimejiSourceKind.Action))
                    IndexPairAnchors(nested);
            }

            private string PairKey(string left, string right, ShimejiSourceElement source)
            {
                // AnimationBuilder joins ImageRight to the member name as text, unlike Image.
                // Only the leading-separator form stays in this lowering's member-local subset.
                if (right != null && (right.Length == 0 || (right[0] != '/' && right[0] != '\\')))
                    Fail(source, "TICK_RIGHT_PATH_UNSUPPORTED: ImageRight needs a leading separator for a member-local sprite");
                string prefix = ".\\img\\" + _member + "\\";
                return prefix + SpriteRelative(left, source).Replace('/', '\\')
                    + (right == null ? "" : prefix + SpriteRelative(right, source).Replace('/', '\\'));
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
                    behaviour.Add(MemberParameter("bind", attribute, _actions[runs], source));
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
                        EmitNextEntries(entry, CheckedCombine(condition, SelectionCondition(entry), entry), sequence);
                    }
                    else if (entry.Kind == ShimejiSourceKind.BehaviorReference)
                    {
                        string name = Required(entry, "Name");
                        if (!_roots.ContainsKey(name)) Fail(entry, "next reference needs a root behaviour named '" + name + "'");
                        int id = _next++;
                        string when = CheckedCombine(condition, SelectionCondition(entry), entry);
                        EmitBehaviour(entry, when, id);
                        sequence.Add(Edge(id, Int(entry, "Frequency", null), when));
                    }
                    else Fail(entry, "unsupported next-list entry");
                }
            }

            private int AddFrame(string image, int x, int y, ShimejiSourceElement source)
            {
                if (x < -4096 || x > 4096 || y < -4096 || y > 4096) Fail(source, "authored anchor is outside this lowering's geometry limits");
                string relative = SpriteRelative(image, source);
                if (!relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) Fail(source, "only PNG sprites are supported");
                string path = Path.GetFullPath(Path.Combine(_pack, "img", _member, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!Inside(Path.Combine(_pack, "img", _member), path)) Fail(source, "sprite leaves its image set");
                SafeEntry(path);
                string key = relative + "|" + x + "|" + y;
                int index;
                if (_frameIds.TryGetValue(key, out index)) return index;
                if (_frames.Count >= SpriteSheetBuilder.MaxTiles) Fail(source, "too many sprite identities");
                // GDI+'s filename entry point has a shorter path limit than managed file I/O.
                using (var imageInput = File.OpenRead(path))
                using (var bitmap = new Bitmap(imageInput))
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

            private static string SpriteRelative(string image, ShimejiSourceElement source)
            {
                string relative = image.TrimStart('/', '\\').Replace('\\', '/');
                if (relative.Length == 0 || relative.Split('/').Any(p => p == ".." || p == "." || p.Contains(":"))) Fail(source, "unsafe sprite path '" + image + "'");
                return string.Join("/", relative.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries));
            }

            private string SourceIdentity()
            {
                // Hash only known source inputs, never generated XML, cache paths or output files.
                // Parsing and image loading already restrict these entries to the source pack.
                var paths = new SortedSet<string>(StringComparer.Ordinal);
                paths.Add(_source.Actions.PackRelativePath.Replace('\\', '/'));
                paths.Add(_source.Behaviors.PackRelativePath.Replace('\\', '/'));
                foreach (Frame frame in _frames) paths.Add(frame.Image);
                var manifest = new StringBuilder();
                foreach (string relative in paths)
                {
                    string path = Path.GetFullPath(Path.Combine(_pack, relative.Replace('/', Path.DirectorySeparatorChar)));
                    if (!Inside(_pack, path)) throw new InvalidDataException("Source identity input leaves its pack.");
                    SafeEntry(path);
                    manifest.Append(Hash(File.ReadAllBytes(path))).Append("  ").Append(relative).Append('\n');
                }
                return Hash(Encoding.UTF8.GetBytes(manifest.ToString()));
            }

            private XElement MemberParameter(string tag, ShimejiSourceAttribute attribute, ShimejiSourceElement target,
                ShimejiSourceElement owner)
            {
                if (IsMemberParameter(attribute.Name) && attribute.RawValue == _member)
                {
                    ShimejiSourceAttribute inherited = target == null ? null : target.Attribute(attribute.SchemaKey);
                    // Dropping a binding must not expose a different member inherited from the action.
                    if (inherited == null || inherited.RawValue == _member) return null;
                }
                if (attribute.Name == "Characteristics") NoteCharacteristics(attribute, target ?? owner);
                if (attribute.Name == "Affordance" && target != null && attribute.Cache == ShimejiSourceCache.Literal)
                {
                    // A bound Affordance is what the source offers (eval reads the binding), but an action with its own
                    // literal Affordance offers that literal through <offers>, which a binding cannot change: refuse
                    // rather than offer the wrong value. Onto an action without one, the runtime offers the bound
                    // parameter itself (TickAction.CurrentOffers), which is the source's reading, and that is social.
                    ShimejiSourceAttribute own = target.Attribute("Affordance");
                    if (own != null && own.Cache == ShimejiSourceCache.Literal && !JavaBlank(own.RawValue))
                        Fail(owner, "TICK_AFFORDANCE_BINDING " + attribute.QualifiedName
                            + ": the bound action offers its own literal Affordance, which a binding cannot replace in this format");
                    _social = true;
                }
                return Parameter(tag, attribute, owner);
            }

            /// <summary>ComplexMove and ComplexJump need 'lifecycle' and 'social' by their Characteristics, split on
            /// commas and compared exactly as the runtime does (ComplexMove.java, ComplexJump.java), and both when it
            /// is scripted (grimoire/06 section 10). A binding is judged by the action it binds.</summary>
            private void NoteCharacteristics(ShimejiSourceAttribute attribute, ShimejiSourceElement action)
            {
                string cls = action == null ? null : Value(action, "Class");
                if (cls != "com.group_finity.mascot.action.ComplexMove" && cls != "com.group_finity.mascot.action.ComplexJump") return;
                bool scripted = attribute.Cache != ShimejiSourceCache.Literal;
                string[] pieces = attribute.RawValue.Split(',');
                if (scripted || pieces.Contains("Breed")) _lifecycle = true;
                if (scripted || pieces.Contains("Scan")) _social = true;
            }

            private XElement Parameter(string tag, ShimejiSourceAttribute attribute, ShimejiSourceElement owner)
            {
                if (attribute.Cache != ShimejiSourceCache.Literal)
                {
                    // grimoire/06 4.1 gives an offer no expression form: Affordance is a literal string there.
                    if (attribute.Name == "Affordance")
                        Fail(owner, "TICK_AFFORDANCE_SCRIPTED " + attribute.QualifiedName + ": an offer has no expression form in this format");
                    return ScriptParameter(tag, attribute.Name, attribute, owner);
                }
                // Java accepts hexadecimal floating literals here. The host's current literal reader
                // retains those as strings, so this lowering must refuse them.
                string literal = attribute.RawValue.Trim();
                bool javaOnlyNumber = Regex.IsMatch(literal, @"^[+-]?0[xX](?:[0-9a-fA-F]+(?:\.[0-9a-fA-F]*)?|\.[0-9a-fA-F]+)[pP][+-]?[0-9]+[dDfF]?$");
                if (javaOnlyNumber) throw new InvalidDataException("TICK_LITERAL_UNSUPPORTED " + attribute.SourceKey + ": Java floating literal needs faithful host parsing.");
                return E(tag, new XAttribute("name", attribute.Name), new XAttribute("value", attribute.RawValue), new XAttribute("cache", "none"));
            }

            /// <summary>A ${} or #{} source value as a parameter or binding: the BODY, without its wrapper, whose kind
            /// becomes the cache (grimoire/06 4.1: ${} is "action", #{} is "frame").</summary>
            private XElement ScriptParameter(string tag, string name, ShimejiSourceAttribute attribute, ShimejiSourceElement owner)
            {
                string body = Script(attribute, owner, ActionScope);
                return E(tag, new XAttribute("name", name), new XAttribute("value", body),
                    new XAttribute("cache", attribute.Cache == ShimejiSourceCache.Action ? "action" : "frame"));
            }

            /// <summary>
            /// A source script body, checked with the host's own tick grammar: SafeScript.TryParse, the parser the
            /// validator runs on every tick when and parameter value (ShimejiFormatValidator.ParseExpression), in the
            /// scope the validator gives that attribute. A body the grammar does not take is REFUSED with the
            /// attribute's qualified name and the parser's message, never approximated: Java and JavaScript forms
            /// outside the subset (a hexadecimal floating literal, BACKLOG N-shimeji-host-01, among them) stop here.
            /// </summary>
            private string Script(ShimejiSourceAttribute attribute, ShimejiSourceElement owner, Func<string, bool> scope)
            {
                DesktopAICompanion.ScriptProgram program;
                string error;
                bool parsed = DesktopAICompanion.SafeScript.TryParse(attribute.Expression, scope, out program, out error);
                if (!parsed) Fail(owner, "TICK_EXPRESSION_UNSUPPORTED " + attribute.QualifiedName + ": " + error);
                _expressions = true;
                // The validator demands 'variables' of an expression its parser finds reading or writing them.
                if (program != null && program.UsesVariables) _variables = true;
                return attribute.Expression;
            }

            /// <summary>A Behavior, BehaviorReference, Condition or Animation selection condition: a literal boolean as
            /// before, or a script body written as the when with no cache (grimoire/06 4.3: parsed afresh per check).</summary>
            private string SelectionCondition(ShimejiSourceElement source)
            {
                ShimejiSourceAttribute attribute = source.Attribute("Condition");
                if (attribute == null) return null;
                if (attribute.Cache != ShimejiSourceCache.Literal) return Script(attribute, source, SelectionScope);
                string value = attribute.RawValue;
                if (value != "true" && value != "false") Fail(source, "only literal boolean selection conditions are lowered yet");
                return value;
            }

            /// <summary>The conjunction an edge carries must itself fit the grammar's limits (length, tokens, depth),
            /// which its separately checked parts can each meet while the whole does not.</summary>
            private string CheckedCombine(string first, string second, ShimejiSourceElement source)
            {
                string combined = Combine(first, second);
                if (combined == null || (first == null) || (second == null)) return combined;
                DesktopAICompanion.ScriptProgram program;
                string error;
                if (!DesktopAICompanion.SafeScript.TryParse(combined, SelectionScope, out program, out error))
                    Fail(source, "TICK_EXPRESSION_UNSUPPORTED combined Condition: " + error);
                return combined;
            }

            private byte[] Composite(out int anchorX, out int anchorY, out int tilesX, out int tilesY)
            {
                if (_frames.Count == 0) throw new InvalidDataException("Tick member needs at least one visible pose: " + _member);
                int width = _frames.Max(frame => frame.Width), height = _frames.Max(frame => frame.Height);
                if (width < 1 || height < 1 || width > SpriteSheetBuilder.MaxCell || height > SpriteSheetBuilder.MaxCell)
                    throw new InvalidDataException("Tick sheet requires cells outside 1.." + SpriteSheetBuilder.MaxCell + "; this lowering never rescales source geometry.");
                anchorX = _frames[0].X; anchorY = _frames[0].Y;
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
                            using (var spriteInput = File.OpenRead(frame.Path))
                            using (var bitmap = new Bitmap(spriteInput))
                                CopyPixels(bitmap, destination, (i % tilesX) * width, (i / tilesX) * height);
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
        /// <summary>Java's <c>trim().length() == 0</c>: every character at or below U+0020 (TickAction.IsJavaBlank).</summary>
        private static bool JavaBlank(string value)
        {
            foreach (char c in value) if (c > ' ') return false;
            return true;
        }
        private static bool IsMemberParameter(string name)
        { return name == "BornMascot" || name == "TransformMascot"; }
        private static string Hash(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        private static IEnumerable<ShimejiSourceAttribute> Attributes(ShimejiSourceElement source)
        {
            foreach (ShimejiSourceAttribute attribute in source.Attributes)
            {
                if (attribute.IsNamespaceDeclaration) continue;
                if (attribute.SchemaKey == null) Fail(source, "unrecognised source attribute '" + attribute.QualifiedName + "'");
                // A ${} or #{} value is judged where it is used: a parameter or binding lowers it (Parameter), a
                // selection or Animation condition writes it as a when, and every other attribute reads it as
                // literal text (Value).
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
            // Names, types, classes, pose and hotspot geometry, frequencies and flags are not script parameters.
            if (attribute.Cache != ShimejiSourceCache.Literal)
                Fail(source, "TICK_LITERAL_ONLY " + key + ": this attribute is not a script parameter, and a ${} or #{} value here is not lowered");
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
