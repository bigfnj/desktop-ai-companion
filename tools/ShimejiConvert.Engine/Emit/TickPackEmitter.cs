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
        // UserBehavior's BEHAVIOURNAME_FALL, _THROWN and _DRAGGED: the names BehaviorBuilder never lets be toggleable.
        private static readonly HashSet<string> RequiredBehaviours = Set("Fall Thrown Dragged");
        // The source's InstantAction subclasses, which the host's TickInstantFamily builds as TickInstantAction.
        private static readonly HashSet<string> InstantClasses = Set("Look Offset Mute BringToFront");
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
            // Whether the members disagree on the schema the source looks engine behaviour names up in (RuntimeSchema).
            // A member that fails to parse is refused below and judged by nothing here.
            var runtimeSchemas = new HashSet<ShimejiSourceSchema>();
            foreach (string memberPath in members)
            {
                try
                {
                    SafeEntry(memberPath);
                    runtimeSchemas.Add(RuntimeSchema(ShimejiParser.ParseSourcePack(packRoot, Path.GetFileName(memberPath))));
                }
                catch (Exception failure) when (failure is InvalidDataException || failure is IOException
                    || failure is UnauthorizedAccessException || failure is System.Xml.XmlException
                    || failure is ArgumentException || failure is ExternalException) { }
            }
            bool mixedRuntime = runtimeSchemas.Count > 1;
            var prepared = new List<PreparedMember>();
            foreach (string memberPath in members)
            {
                string member = Path.GetFileName(memberPath);
                try
                {
                    SafeEntry(memberPath);
                    ShimejiSourceConfig source = ShimejiParser.ParseSourcePack(packRoot, member);
                    prepared.Add(new MemberBuilder(packRoot, member, source, mixedRuntime).Build());
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

        /// <summary>
        /// The schema the source looks its engine behaviours up in. <c>Configuration.schema</c> is ONE field that every
        /// <c>load</c> reassigns (Configuration.java:62), and Main loads the action document, then the behaviour
        /// document, then an information document when there is one (Main.java:371, 418, 437). UserBehavior and
        /// Configuration read it at run time for the behaviour a ground loss, a drag and a release enter
        /// (<c>schema.getString("Fall")</c>, UserBehavior.java:164, 192, 272, 303; Configuration.java:245, 282), so a
        /// member whose last document is Japanese enters 落下する, ドラッグされる and 投げられる there.
        /// </summary>
        internal static ShimejiSourceSchema RuntimeSchema(ShimejiSourceConfig source)
        {
            if (source.Information != null) return source.Information.Schema;
            return source.Behaviors != null ? source.Behaviors.Schema : ShimejiSourceSchema.Base;
        }

        /// <summary>The behaviours the host enters by name on its own events, which are always these English names
        /// (TickRuntime's BuildBehaviourByName(type, "Fall"), TickReachability.EngineBehaviours).</summary>
        private static readonly string[] EngineBehaviourKeys = { "Fall", "Dragged", "Thrown" };

        /// <summary>The behaviour-naming parameters the host classes read (Interact and the scan classes' Behavior and
        /// TargetBehavior, the Breed family's BornBehavior, Transform's TransformBehavior).</summary>
        private static readonly string[] BehaviourParameters = { "Behavior", "TargetBehavior", "BornBehavior", "TransformBehavior" };

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
            // Each nested action element and its node name (its Name, or the name an anonymous one is given).
            private readonly Dictionary<ShimejiSourceElement, string> _nestedNames = new Dictionary<ShimejiSourceElement, string>();
            // Global action name -> the custom variables references and behaviours bind to it (ScopeFor).
            private readonly Dictionary<string, HashSet<string>> _boundCustom = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            // The scope ScriptParameter parses in: the action whose variable map the attribute being lowered joins.
            private Func<string, bool> _scope;
            // Every identifier a script of this member's two documents mentions (Carried).
            private readonly HashSet<string> _scriptNames = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>A custom variable is carried when some script of this member names it. One that nothing reads is
            /// an inert variable in the source, which changes nothing; it stays refused as an attribute nothing reads, as
            /// every unread name has been, so a misspelt attribute is still caught rather than carried.</summary>
            private bool Carried(ShimejiSourceAttribute attribute)
            {
                return IsCustom(attribute) && _scriptNames.Contains(attribute.SourceName);
            }
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
            // Header toggles, one per Toggleable behaviour name, in first-seen order.
            private readonly List<string> _toggles = new List<string>();
            // Source behaviour name -> the host's engine name, for a member whose RuntimeSchema is Japanese
            // (落下する is the Fall the source enters); empty for a base one, whose engine names are the host's already.
            private readonly Dictionary<string, string> _engineNames = new Dictionary<string, string>(StringComparer.Ordinal);
            // Some member of this pack looks engine names up in another schema than this one (see BehaviourValue).
            private readonly bool _mixedRuntime;

            public MemberBuilder(string pack, string member, ShimejiSourceConfig source, bool mixedRuntime = false)
            {
                _pack = pack; _member = member; _source = source; _mixedRuntime = mixedRuntime;
                foreach (string key in EngineBehaviourKeys)
                {
                    string sourceName = ShimejiParser.SchemaString(RuntimeSchema(source), key);
                    if (sourceName != key) _engineNames.Add(sourceName, key);
                }
            }

            /// <summary>The name a source behaviour has in the emitted document: its engine name when the source enters
            /// it on an engine event, else its own.</summary>
            private string BehaviourName(string sourceName)
            {
                string engine;
                return _engineNames.TryGetValue(sourceName, out engine) ? engine : sourceName;
            }

            /// <summary>
            /// A behaviour-naming parameter's value as the host must read it. A literal naming one of this member's own
            /// behaviours gets that behaviour's emitted name; the target of BornBehavior and TransformBehavior is the
            /// born or transformed member (or this one), so the mapping holds only while every member of the pack
            /// looks engine names up in the same schema, and a literal engine name in a pack that mixes them refuses.
            /// TargetBehavior names a behaviour of the PARTNER, which can be any companion, so a renamed name there
            /// refuses; so does a scripted value in a renaming member, which could evaluate to a renamed name.
            /// </summary>
            private string BehaviourValue(ShimejiSourceAttribute attribute, ShimejiSourceElement owner)
            {
                bool engineName = EngineBehaviourKeys.Any(key => key == attribute.RawValue
                    || ShimejiParser.SchemaString(ShimejiSourceSchema.Japanese, key) == attribute.RawValue);
                if (_mixedRuntime && engineName && attribute.Cache == ShimejiSourceCache.Literal)
                    Fail(owner, "TICK_ENGINE_NAME_MIXED " + attribute.QualifiedName + ": the pack's members look engine behaviour names up in different schemas, so '"
                        + attribute.RawValue + "' cannot be mapped for its target");
                if (_engineNames.Count == 0) return attribute.RawValue;
                if (attribute.Cache != ShimejiSourceCache.Literal)
                    Fail(owner, "TICK_ENGINE_NAME_SCRIPTED " + attribute.QualifiedName
                        + ": this member's engine behaviours are renamed for the host, and a scripted behaviour name cannot follow them");
                if (attribute.Name == "TargetBehavior" && _engineNames.ContainsKey(attribute.RawValue))
                    Fail(owner, "TICK_ENGINE_NAME_PARTNER " + attribute.QualifiedName + ": '" + attribute.RawValue
                        + "' names a partner's engine behaviour, whose name depends on the partner's schema");
                return BehaviourName(attribute.RawValue);
            }

            public PreparedMember Build()
            {
                if (_source.Information != null) CheckInformation(_source.Information);
                if (_source.Actions == null || _source.Behaviors == null) throw new InvalidDataException("Tick pack needs both action and behaviour documents.");
                // A Japanese action document's enumerated VALUES are Japanese too: ActionType and BorderValue map them
                // through the document's own schema, exactly as the source compares them (see SchemaValue).
                CheckRoot(_source.Actions.Root);
                CheckRoot(_source.Behaviors.Root);
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
                // Under a Japanese RuntimeSchema an English "Fall" is an ordinary behaviour the source never enters on
                // an event, and the host would; the Japanese one takes that name here, so the two cannot both exist.
                foreach (string key in EngineBehaviourKeys)
                    if (_engineNames.ContainsValue(key) && _roots.ContainsKey(key))
                        Fail(_roots[key].Source, "TICK_ENGINE_NAME_COLLISION: '" + key + "' is an ordinary behaviour under this member's Japanese schema, and the host enters a behaviour of that name on its own events");
                // The names the member's scripts mention (Carried), then the custom variables each global action is
                // bound (ScopeFor), from every reference and behaviour.
                foreach (ShimejiSourceDocument scripted in new[] { _source.Actions, _source.Behaviors })
                    foreach (ShimejiSourceElement element in scripted.Root.DescendantsAndSelf())
                        foreach (ShimejiSourceAttribute attribute in element.Attributes)
                            if (attribute.Cache != ShimejiSourceCache.Literal)
                                foreach (Match token in ScriptToken.Matches(attribute.Expression)) _scriptNames.Add(token.Value);
                foreach (ShimejiSourceElement element in _source.Actions.Root.DescendantsAndSelf())
                    if (element.Kind == ShimejiSourceKind.ActionReference) NoteBoundCustom(LiteralRaw(element, "Name"), element);
                foreach (ShimejiSourceElement element in _source.Behaviors.Root.DescendantsAndSelf())
                    if (element.Kind == ShimejiSourceKind.Behavior || element.Kind == ShimejiSourceKind.BehaviorReference)
                        NoteBoundCustom(LiteralRaw(element, "Action") ?? LiteralRaw(element, "Name"), element);
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
                // grimoire/06 section 2: one switch per Toggleable name, labelled with it. No default is written:
                // every Toggleable behaviour starts enabled in the source, which is the format's absent default.
                if (_toggles.Count > 0)
                    header.Add(E("toggles", _toggles.Select(t => E("toggle", new XAttribute("id", t), new XAttribute("label", t)))));
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
                    // Configuration.loadBehaviors takes only Condition and Behaviour children (Configuration.java:104-121):
                    // any other child of the root list, or of a Condition in it, is never read, so it is skipped.
                }
            }

            /// <summary>The key whose <c>schema.getString</c> in the action document's schema equals <paramref name="raw"/>,
            /// ordinally, among the keys the source compares that attribute with; null when none does. This is the
            /// source's own test (<c>type.equals(schema.getString("Stay"))</c>, ActionBuilder.java:101-138, and
            /// <c>schema.getString("Floor").equals(borderType)</c>, BorderedAction.java:44-49), with the schema the
            /// action was built under: ActionBuilder takes the configuration's schema while its document loads, which is
            /// the action document's (Configuration.java:62). So a Japanese document's 静止 is Stay, and its English
            /// "Stay" is no type at all. OffsetType (Origin) and a Characteristics piece (Breed, Scan) are compared the
            /// same way, but the Japanese schema has no override for those keys (ShimejiParser's facts), so their values
            /// read the same in both vocabularies and pass through unmapped.</summary>
            private string SchemaValue(string raw, params string[] keys)
            {
                foreach (string key in keys)
                    if (string.Equals(ShimejiParser.SchemaString(_source.Actions.Schema, key), raw, StringComparison.Ordinal)) return key;
                return null;
            }

            private string ActionType(ShimejiSourceElement source)
            {
                string raw = Required(source, "Type");
                if (_source.Actions.Schema == ShimejiSourceSchema.Base)
                {
                    if (!Types.Contains(raw)) Fail(source, "unsupported action type '" + raw + "'");
                    return raw;
                }
                string type = SchemaValue(raw, "Embedded", "Move", "Stay", "Animate", "Sequence", "Select");
                if (type == null)
                    Fail(source, "TICK_SCHEMA_VALUE_UNMAPPED Type: '" + raw + "' is none of the action types in this document's Japanese schema");
                return type;
            }

            /// <summary>A literal BorderType as the host reads it: Floor, Wall, Ceiling or empty. A base document's value is
            /// checked as before; a Japanese one is mapped (地面 is Floor), and one that maps to nothing refuses by name.</summary>
            private string BorderValue(ShimejiSourceAttribute attribute, ShimejiSourceElement owner)
            {
                string raw = attribute.RawValue;
                if (_source.Actions.Schema == ShimejiSourceSchema.Base || raw == "") return raw;
                string border = SchemaValue(raw, "Floor", "Wall", "Ceiling");
                if (border == null)
                    Fail(owner, "TICK_SCHEMA_VALUE_UNMAPPED BorderType: '" + raw + "' is none of the borders in this document's Japanese schema");
                return border;
            }

            /// <summary>The node name of an action element: its Name, or the name an anonymous nested action was given.</summary>
            private string ActionName(ShimejiSourceElement source)
            {
                string name;
                return _nestedNames.TryGetValue(source, out name) ? name : Required(source, "Name");
            }

            private int EmitAction(ShimejiSourceElement source, bool nested, string anonymousName = null)
            {
                string type = ActionType(source), name;
                if (nested)
                {
                    // A nested Action is built from its own element where it stands (ActionBuilder.java:68-70); the
                    // source never looks it up by name, so it may have none (the stock configuration's composites
                    // inline anonymous ones). The format names every action node, so an anonymous one is named after its
                    // place, "<parent>/<position among the parent's children>", which no reference can name; a
                    // collision with any other action's name still refuses, as a duplicate nested name does.
                    name = Value(source, "Name");
                    if (string.IsNullOrEmpty(name)) name = anonymousName;
                    if (name == null) Fail(source, "missing Name");
                    if (_actions.ContainsKey(name)) Fail(source, "a nested action needs a unique name for this first lowering: '" + name + "'");
                    _actions.Add(name, source);
                    _nestedNames.Add(source, name);
                }
                else name = Required(source, "Name");
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
                // ActionBuilder reads Class for every action but uses it only for an Embedded type (ActionBuilder.java:
                // 50, 101-126); a Move, Stay, Animate or composite is built from its type whatever Class says, so a Class
                // there is ignored (and, as on every action, never written as a parameter).
                XElement guard = null, offers = null;
                Func<string, bool> scope = ScopeFor(source);
                _scope = scope;
                foreach (ShimejiSourceAttribute attribute in ParameterAttributes(source, Carried))
                {
                    if (attribute.SchemaKey == "Name" || attribute.SchemaKey == "Type" || attribute.SchemaKey == "Class") continue;
                    if (Carried(attribute))
                    {
                        act.Add(CustomParameter("param", attribute, source));
                        continue;
                    }
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
                        string border = BorderValue(attribute, source);
                        if (border != "Floor" && border != "Wall" && border != "Ceiling" && border != "")
                            Fail(source, "unsupported border '" + attribute.RawValue + "'");
                        act.Add(new XAttribute("border", border));
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
                int childIndex = 0, animationIndex = 0, position = 0;
                foreach (ShimejiSourceElement child in source.Children)
                {
                    position++;
                    if (child.Kind == ShimejiSourceKind.Animation)
                    {
                        // Composite animations do not run, but the source still loads their images.
                        _scope = scope;
                        XElement variant = EmitVariant(child, ++animationIndex, composite ? null : conditions);
                        if (!composite) variants.Add(variant);
                    }
                    else if (composite && (child.Kind == ShimejiSourceKind.ActionReference || child.Kind == ShimejiSourceKind.Action))
                    {
                        string target;
                        if (child.Kind == ShimejiSourceKind.Action)
                        {
                            EmitAction(child, true, name + "/" + position.ToString(Invariant));
                            target = ActionName(child);
                        }
                        else target = Required(child, "Name");
                        if (child.Kind == ShimejiSourceKind.ActionReference && !_globalActions.Contains(target))
                            Fail(child, "missing global action '" + target + "'");
                        var reference = E("child", new XAttribute("animation", target));
                        if (child.Kind == ShimejiSourceKind.ActionReference)
                        {
                            // A reference's bindings are evaluated in the variable map of the action it builds
                            // (ActionRef passes them in, ActionBuilder.createVariables), so their scope is the target's.
                            _scope = ScopeFor(_actions[target]);
                            foreach (ShimejiSourceAttribute attribute in ParameterAttributes(child, Carried))
                            {
                                if (attribute.SchemaKey == "Name") continue;
                                if (Carried(attribute))
                                {
                                    reference.Add(CustomParameter("bind", attribute, child));
                                    continue;
                                }
                                if (!Parameters.Contains(attribute.Name)) Fail(child, "unsupported reference binding '" + attribute.QualifiedName + "'");
                                reference.Add(MemberParameter("bind", attribute, _actions[target], child));
                            }
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
                else if (!variants.Elements().Any() && type == "Embedded" && InstantClasses.Contains(cls))
                {
                    // An instant class is built without animations (Look, Offset, Mute and BringToFront have only the
                    // (schema, params) constructor ActionBuilder falls back to, ActionBuilder.java:107-113) and ends in
                    // the step it starts (InstantAction.java), as the host's TickInstantAction does, which reads no
                    // variant. So one with no Animation, the stock configuration's shape, writes no <variants>.
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
                string name = ActionName(source);
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
                string type = ActionType(source);
                if (type == "Sequence" || type == "Select")
                    foreach (ShimejiSourceElement child in source.Children)
                    {
                        if (child.Kind != ShimejiSourceKind.Action && child.Kind != ShimejiSourceKind.ActionReference) continue;
                        ActivationBounds bounds = InspectActivation(
                            _actions[child.Kind == ShimejiSourceKind.Action ? ActionName(child) : Required(child, "Name")], known, active, path);
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
                // The source's two shapes, matched as it matches them: shapeText.equalsIgnoreCase("Rectangle") and
                // ("Ellipse") (AnimationBuilder.java:192-196), so "rectangle" and "ELLIPSE" are those shapes. Any other
                // name refuses rather than guesses, as the source refuses it (HotspotShapeNotSupported).
                string kind = JavaEqualsIgnoreCase(shape, "Rectangle") ? "rect" : JavaEqualsIgnoreCase(shape, "Ellipse") ? "ellipse" : null;
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
                    hotspot.Add(new XAttribute("behaviour", BehaviourName(behaviour)));
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
                    // BehaviorBuilder reads only its NextBehaviourList children (BehaviorBuilder.java:82, selectChildren);
                    // any other child, the official default configuration's <NextBehavior> among them, is never read,
                    // so the behaviour keeps the root list and gains no edge from it (grimoire/06 4.2). Ignored here too.
                    if (list.Kind != ShimejiSourceKind.NextBehaviorList) continue;
                    Check(list, "Add");
                    add = Bool(list, "Add");
                }
                var behaviour = E("behaviour", new XAttribute("name", BehaviourName(name)), new XAttribute("runs", runs),
                    new XAttribute("frequency", frequency), new XAttribute("hidden", Bool(source, "Hidden")));
                if (rootDefinition) behaviour.Add(new XAttribute("add", add));
                // A behaviour's bindings are passed to the action it runs (BehaviorBuilder.java:72-78), whose scope they take.
                _scope = ScopeFor(_actions[runs]);
                foreach (ShimejiSourceAttribute attribute in ParameterAttributes(source, Carried))
                {
                    if (new[] { "Name", "Action", "Frequency", "Hidden", "Condition" }.Contains(attribute.SchemaKey)) continue;
                    if (attribute.SchemaKey == "Toggleable")
                    {
                        // grimoire/06 section 2: a Toggleable behaviour is a header toggle the user can turn off. The
                        // source keys a disabled behaviour by NAME (DisabledBehaviours.<imageSet>, Main.java:1420), so
                        // the toggle id is the name, and each element that says Toggleable itself (a root Behavior or
                        // a BehaviorReference) names that toggle; one that does not stays ungated, as its builder does.
                        // The builder overrides the flag for the three behaviours the engine needs: a behaviour named
                        // exactly Fall, Thrown or Dragged is never toggleable, whatever it says (BehaviorBuilder.java:59-68,
                        // the literal English names, not the schema's), so it gets no toggle and writes no header switch.
                        if (Bool(source, "Toggleable") && !RequiredBehaviours.Contains(name))
                        {
                            if (name.Length > 64)
                                Fail(source, "TICK_TOGGLE_ID_LIMIT: a Toggleable behaviour's name is its toggle id, at most 64 characters in this format");
                            behaviour.Add(new XAttribute("toggle", name));
                            if (!_toggles.Contains(name)) _toggles.Add(name);
                        }
                        continue;
                    }
                    if (_source.Actions.Schema != _source.Behaviors.Schema)
                        Fail(source, "cross-schema behaviour bindings need raw target-schema resolution before lowering");
                    if (Carried(attribute))
                    {
                        behaviour.Add(CustomParameter("bind", attribute, source));
                        continue;
                    }
                    if (!Parameters.Contains(attribute.Name)) Fail(source, "unsupported behaviour binding '" + attribute.QualifiedName + "'");
                    behaviour.Add(MemberParameter("bind", attribute, _actions[runs], source));
                }
                XElement node = Node(id, "beh:" + BehaviourName(name), behaviour);
                _nodes.Add(node);
                _behaviourMap.Add(Key(id), Ref(_source.Behaviors, source));
                // Continuations are subsequently looked up through the ROOT behaviour by name by the host.
                if (rootDefinition)
                    foreach (ShimejiSourceElement list in source.Children)
                        if (list.Kind == ShimejiSourceKind.NextBehaviorList) EmitNextEntries(list, null, node.Element(Ns + "sequence"));
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
                    // BehaviorBuilder.loadBehaviors takes only Condition and BehaviourReference children
                    // (BehaviorBuilder.java:102-121): any other child of a next list is never read, so it is skipped.
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
                // The information document is a source input too: it decides the schema engine names are read in.
                if (_source.Information != null) paths.Add(_source.Information.PackRelativePath.Replace('\\', '/'));
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
                // The host reads a BorderType parameter by its English value (TickBorderedAction.Init), the source by the
                // action document's schema: a Japanese literal is written mapped, and a Japanese script, whose value is
                // known only at run time, cannot be.
                bool japaneseBorder = attribute.Name == "BorderType" && _source.Actions.Schema == ShimejiSourceSchema.Japanese;
                if (Array.IndexOf(BehaviourParameters, attribute.Name) >= 0)
                {
                    string named = BehaviourValue(attribute, owner);
                    if (attribute.Cache == ShimejiSourceCache.Literal && named != attribute.RawValue)
                        return E(tag, new XAttribute("name", attribute.Name), new XAttribute("value", named), new XAttribute("cache", "none"));
                }
                if (attribute.Cache != ShimejiSourceCache.Literal)
                {
                    // grimoire/06 4.1 gives an offer no expression form: Affordance is a literal string there.
                    if (attribute.Name == "Affordance")
                        Fail(owner, "TICK_AFFORDANCE_SCRIPTED " + attribute.QualifiedName + ": an offer has no expression form in this format");
                    if (japaneseBorder)
                        Fail(owner, "TICK_SCHEMA_SCRIPTED_VALUE " + attribute.QualifiedName
                            + ": a scripted BorderType in a Japanese document yields a Japanese value the host cannot map at run time");
                    return ScriptParameter(tag, attribute.Name, attribute, owner);
                }
                if (japaneseBorder)
                    return E(tag, new XAttribute("name", attribute.Name), new XAttribute("value", BorderValue(attribute, owner)), new XAttribute("cache", "none"));
                // Java accepts hexadecimal floating literals here. The host's current literal reader
                // retains those as strings, so this lowering must refuse them.
                string literal = attribute.RawValue.Trim();
                bool javaOnlyNumber = Regex.IsMatch(literal, @"^[+-]?0[xX](?:[0-9a-fA-F]+(?:\.[0-9a-fA-F]*)?|\.[0-9a-fA-F]+)[pP][+-]?[0-9]+[dDfF]?$");
                if (javaOnlyNumber) throw new InvalidDataException("TICK_LITERAL_UNSUPPORTED " + attribute.SourceKey + ": Java floating literal needs faithful host parsing.");
                return E(tag, new XAttribute("name", attribute.Name), new XAttribute("value", attribute.RawValue), new XAttribute("cache", "none"));
            }

            /// <summary>
            /// The scope a script in an action's variable map sees: what ActionScope holds, plus the action's own custom
            /// variables and every custom variable a reference or behaviour binds to it anywhere. That union is the
            /// validator's ScopeFor (the act's own params and every name bound to it), so a body this accepts the host
            /// accepts. An action with no custom variable keeps ActionScope itself.
            /// </summary>
            private Func<string, bool> ScopeFor(ShimejiSourceElement action)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (ShimejiSourceAttribute attribute in action.Attributes) if (Carried(attribute)) names.Add(attribute.SourceName);
                HashSet<string> bound;
                string own = _nestedNames.ContainsKey(action) ? null : LiteralRaw(action, "Name");
                if (own != null && _boundCustom.TryGetValue(own, out bound)) names.UnionWith(bound);
                if (names.Count == 0) return ActionScope;
                return name => ActionScope(name) || names.Contains(name);
            }

            /// <summary>Records the custom variables an ActionReference, Behavior or BehaviorReference binds to the
            /// action it names, for that action's ScopeFor.</summary>
            private void NoteBoundCustom(string target, ShimejiSourceElement element)
            {
                if (target == null) return;
                foreach (ShimejiSourceAttribute attribute in element.Attributes)
                {
                    if (!Carried(attribute)) continue;
                    HashSet<string> names;
                    if (!_boundCustom.TryGetValue(target, out names)) _boundCustom.Add(target, names = new HashSet<string>(StringComparer.Ordinal));
                    names.Add(attribute.SourceName);
                }
            }

            private static string LiteralRaw(ShimejiSourceElement element, string key)
            {
                ShimejiSourceAttribute attribute = element.Attribute(key);
                return attribute != null && attribute.Cache == ShimejiSourceCache.Literal ? attribute.RawValue : null;
            }

            /// <summary>A custom variable as a parameter or binding, by its source name: a literal as written, a script as
            /// its body and cache, parsed in the scope of the action whose map it joins.</summary>
            private XElement CustomParameter(string tag, ShimejiSourceAttribute attribute, ShimejiSourceElement owner)
            {
                if (attribute.Cache != ShimejiSourceCache.Literal) return ScriptParameter(tag, attribute.SourceName, attribute, owner);
                string literal = attribute.RawValue.Trim();
                if (Regex.IsMatch(literal, @"^[+-]?0[xX](?:[0-9a-fA-F]+(?:\.[0-9a-fA-F]*)?|\.[0-9a-fA-F]+)[pP][+-]?[0-9]+[dDfF]?$"))
                    throw new InvalidDataException("TICK_LITERAL_UNSUPPORTED " + attribute.SourceKey + ": Java floating literal needs faithful host parsing.");
                return E(tag, new XAttribute("name", attribute.SourceName), new XAttribute("value", attribute.RawValue), new XAttribute("cache", "none"));
            }

            /// <summary>A ${} or #{} source value as a parameter or binding: the BODY, without its wrapper, whose kind
            /// becomes the cache (grimoire/06 4.1: ${} is "action", #{} is "frame").</summary>
            private XElement ScriptParameter(string tag, string name, ShimejiSourceAttribute attribute, ShimejiSourceElement owner)
            {
                string body = Script(attribute, owner, _scope ?? ActionScope);
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
                string body = BeanProperties(attribute.Expression);
                bool parsed = DesktopAICompanion.SafeScript.TryParse(body, scope, out program, out error);
                if (!parsed) Fail(owner, "TICK_EXPRESSION_UNSUPPORTED " + attribute.QualifiedName + ": " + error);
                _expressions = true;
                // The validator demands 'variables' of an expression its parser finds reading or writing them.
                if (program != null && program.UsesVariables) _variables = true;
                return body;
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
        /// <summary>Java's <c>String.equalsIgnoreCase</c> (String.regionMatches with ignoreCase): equal lengths, and each
        /// pair of UTF-16 units equal, or equal upper-cased, or equal lower-cased after upper-casing. .NET's invariant
        /// casing leaves the dotless i (U+0131) and the dotted capital I (U+0130) unmapped where Java's Character maps
        /// them to I and i, so those two are mapped here; so "Ellıpse" is an Ellipse in both. Not OrdinalIgnoreCase,
        /// which compares upper-cased units only.</summary>
        private static bool JavaEqualsIgnoreCase(string value, string other)
        {
            if (value == null || other == null || value.Length != other.Length) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char a = value[i], b = other[i];
                if (a == b) continue;
                char upperA = JavaUpper(a), upperB = JavaUpper(b);
                if (upperA == upperB || JavaLower(upperA) == JavaLower(upperB)) continue;
                return false;
            }
            return true;
        }
        private static char JavaUpper(char c) { return c == (char)0x131 ? 'I' : char.ToUpperInvariant(c); }
        private static char JavaLower(char c) { return c == (char)0x130 ? 'i' : char.ToLowerInvariant(c); }
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
        /// <summary>
        /// An information document (info.xml). Main loads it as one more configuration document, after the action and
        /// behaviour ones (Main.java:420-438), and Configuration.load takes four kinds of root child from it
        /// (Configuration.java:64-99): Constant, ActionList and BehaviourList, which would join the member's
        /// configuration and which this lowering does not merge, so each refuses by name; and Information, whose Name,
        /// PreviewImage, SplashImage(s) and Artist/Scripter/Commissioner/Support credits (Configuration.java:133-172)
        /// only the image-set chooser and the information window read (ImageSetChooser.java:189-195,
        /// InformationWindow.java:73-192, Main.java:307 and 1556 deciding whether to show that window). No mascot reads
        /// them, so they have no runtime effect and nothing is written for them. Any other root child is never read.
        /// The one runtime effect of the document is its schema, which becomes the configuration's (RuntimeSchema).
        /// </summary>
        private static void CheckInformation(ShimejiSourceDocument information)
        {
            CheckRoot(information.Root);
            foreach (ShimejiSourceElement child in information.Root.Children)
                if (child.SchemaKey == "Constant" || child.SchemaKey == "ActionList" || child.SchemaKey == "BehaviourList")
                    Fail(child, "TICK_INFORMATION_CONFIGURATION: the source loads this " + child.SchemaKey
                        + " from the information document as configuration, which this lowering does not merge");
        }

        /// <summary>A configuration document's root element. The source reads none of its attributes: Configuration.load
        /// only asks the root for its children (hasChild, selectChildren; Configuration.java:41-102), and Main hands it the
        /// document element and nothing else (Main.java:371, 418, 437). So a root attribute, such as the schema-location
        /// pair the stock configuration's root carries, is ignored here exactly as there, and writes nothing. Its direct
        /// text is not read either (see Check).</summary>
        private static void CheckRoot(ShimejiSourceElement root)
        {
            // Nothing on a configuration root is read: not its attributes, not its text.
        }
        // A member call with no arguments to a JavaBean getter: ".getScreen()", ".isLookRight()".
        private static readonly Regex BeanGetter = new Regex(@"\.\s*(?:get|is)([A-Z][A-Za-z0-9_]*)\s*\(\s*\)");

        /// <summary>
        /// A script with each zero-argument JavaBean getter call written as the property it reads, outside string
        /// literals: "mascot.environment.getScreen().bottom" is "mascot.environment.screen.bottom". Nashorn's bean
        /// linkage evaluates both forms with the same call (a property of a Java object is its getX or isX method), and
        /// the host's object model is that bean surface (grimoire/06 section 6), so the two read the same value. The
        /// property is spelt as java.beans.Introspector.decapitalize spells it ("getIE" is "IE"). A getter the host does
        /// not model is still refused by the grammar, under its property name. A script with no such call is unchanged.
        /// </summary>
        private static string BeanProperties(string script)
        {
            var result = new StringBuilder(script.Length);
            int start = 0;
            for (int i = 0; i <= script.Length; i++)
            {
                bool end = i == script.Length;
                char c = end ? '\0' : script[i];
                if (end || c == '\'' || c == '"')
                {
                    result.Append(BeanGetter.Replace(script.Substring(start, i - start), m => "." + Decapitalize(m.Groups[1].Value)));
                    if (end) break;
                    int close = i + 1;
                    while (close < script.Length && script[close] != c) close += script[close] == '\\' ? 2 : 1;
                    close = Math.Min(close + 1, script.Length);
                    result.Append(script, i, close - i);
                    i = close - 1;
                    start = close;
                }
            }
            return result.ToString();
        }

        private static string Decapitalize(string name)
        {
            if (name.Length > 1 && char.IsUpper(name[0]) && char.IsUpper(name[1])) return name;
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        // A name a script can read: an ASCII identifier, as the host's grammar reads one; and one inside a script.
        private static readonly Regex ScriptToken = new Regex(@"[A-Za-z_$][A-Za-z0-9_$]*");
        private static readonly Regex Identifier = new Regex(@"^[A-Za-z_$][A-Za-z0-9_$]*$");
        // Names a custom variable may not take: the grammar's own words, and the prefix of the parameters this emitter
        // generates for scripted Animation Conditions.
        private static readonly HashSet<string> ReservedNames = Set("mascot Math true false null");

        /// <summary>
        /// An attribute the schema does not name, on an Action, ActionReference, Behavior or BehaviorReference, is a
        /// variable of the source's action map all the same: ActionBuilder puts EVERY attribute of the action and every
        /// incoming parameter into it (ActionBuilder.java:56, 171-179), ActionRef passes its own attributes in, and a
        /// behaviour passes its extra attributes (BehaviorBuilder.java:72-78). The stock configuration binds one
        /// (a "Gap" its sibling TargetX script reads). Such a custom variable is lowered under its source name when that
        /// name is a plain identifier the format can carry: not namespace-qualified, not a parameter or local the host
        /// reads (an English Duration in a Japanese document is an inert variable to the source but the duration to the
        /// host, so it stays refused), and not a word of the grammar.
        /// </summary>
        private static bool IsCustom(ShimejiSourceAttribute attribute)
        {
            return attribute.SchemaKey == null && !attribute.IsNamespaceDeclaration && attribute.Prefix == ""
                && string.IsNullOrEmpty(attribute.NamespaceName) && Identifier.IsMatch(attribute.SourceName)
                && Array.IndexOf(ShimejiFormatValidator.KnownParameters, attribute.SourceName) < 0
                && Array.IndexOf(ShimejiFormatValidator.Locals, attribute.SourceName) < 0
                && !ReservedNames.Contains(attribute.SourceName)
                && !attribute.SourceName.StartsWith("AnimationCondition", StringComparison.Ordinal);
        }

        /// <summary>The attributes of an element whose attributes are action variables: as <see cref="Attributes"/>, but a
        /// carried custom variable passes; any other attribute the schema does not name still refuses.</summary>
        private static IEnumerable<ShimejiSourceAttribute> ParameterAttributes(ShimejiSourceElement source,
            Func<ShimejiSourceAttribute, bool> carried)
        {
            foreach (ShimejiSourceAttribute attribute in source.Attributes)
            {
                if (attribute.IsNamespaceDeclaration) continue;
                if (attribute.SchemaKey == null && !carried(attribute)) Fail(source, "unrecognised source attribute '" + attribute.QualifiedName + "'");
                yield return attribute;
            }
        }

        /// <summary>An element's attributes against the keys it may carry. Its direct text is not judged: the source's
        /// Entry lists element children only (Entry.java:117-123) and reads text only for an Information child
        /// (Configuration.java:133-172, through Entry.getText), so a stray text node in an Animation, an Action or a
        /// list (in the corpus, a pose that lost its opening bracket) is never read there, and is ignored here.</summary>
        private static void Check(ShimejiSourceElement source, params string[] allowed)
        {
            foreach (ShimejiSourceAttribute attribute in Attributes(source))
                if (!allowed.Contains(attribute.SchemaKey)) Fail(source, "unsupported attribute '" + attribute.QualifiedName + "'");
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
