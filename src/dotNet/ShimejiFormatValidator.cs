using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace DesktopAICompanion
{
    /// <summary>
    /// The feature tokens of the Shimeji runtime extensions (grimoire/06 section 1): what a companion declares in
    /// <c>&lt;header&gt;&lt;features&gt;</c>, and what THIS build's runtime runs. A token joins <see cref="Supported"/>
    /// only in the build whose runtime implements it, so no build accepts a companion it cannot run.
    /// </summary>
    internal static class CompanionFormatFeatures
    {
        public const string Tick = "tick";
        public const string Actions = "actions";
        public const string Behaviours = "behaviours";
        public const string Expressions = "expressions";
        public const string Packs = "packs";
        public const string Social = "social";
        public const string Lifecycle = "lifecycle";
        public const string Windows = "windows";
        public const string Hotspots = "hotspots";
        public const string Variables = "variables";

        /// <summary>Every token the format defines. A token outside this list belongs to a newer format.</summary>
        public static readonly string[] All =
            { Tick, Actions, Behaviours, Expressions, Packs, Social, Lifecycle, Windows, Hotspots, Variables };

        /// <summary>What this build runs. Empty until the runtime lands: the schema and validator exist first, and a
        /// companion that uses the extensions is refused, by feature name, until the host can run it.</summary>
        public static readonly IReadOnlyCollection<string> Supported = new string[0];

        public static HashSet<string> Parse(string features)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(features)) return set;
            foreach (string token in features.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                set.Add(token);
            return set;
        }

        public static bool IsDefined(string token)
        {
            return Array.IndexOf(All, token) >= 0;
        }
    }

    /// <summary>
    /// The validator's rules for the Shimeji runtime extensions (grimoire/06 sections 1-10), called by
    /// <see cref="CompanionXmlValidator"/>: the feature gate read before schema validation, which elements need which
    /// feature, the closed list of embedded classes, tick-mode limits, scoped expression parsing (SafeScript) and the
    /// names a document must resolve. Every refusal is an <see cref="InvalidDataException"/> whose message names what
    /// was wrong and where.
    /// </summary>
    internal static class ShimejiFormatValidator
    {
        public const int TickMaximumAnimations = 2048;
        public const int TickMaximumTransitions = 1024;
        public const int MaximumPoseTicks = 100000;

        /// <summary>The embedded classes the host runs (grimoire/06 section 5). With the six base types this is the
        /// official runtime's 36 concrete actions.</summary>
        public static readonly string[] EmbeddedClasses =
        {
            "Fall", "Jump", "Dragged", "Regist", "Look", "Turn", "Offset", "Mute", "BringToFront", "SelfDestruct",
            "Breed", "BreedMove", "BreedJump", "BreedFall", "Transform",
            "ScanMove", "ScanJump", "ScanInteract", "Interact", "ComplexMove", "ComplexJump",
            "MoveWithTurn", "Broadcast", "BroadcastStay", "BroadcastMove", "BroadcastJump",
            "WalkWithIE", "FallWithIE", "JumpWithIE", "ThrowIE", "MinimizeIE",
        };

        private static readonly string[] LifecycleClasses = { "Breed", "BreedMove", "BreedJump", "BreedFall", "Transform", "SelfDestruct" };
        private static readonly string[] SocialClasses = { "ScanMove", "ScanJump", "ScanInteract", "Interact" };
        private static readonly string[] WindowClasses = { "WalkWithIE", "FallWithIE", "JumpWithIE", "ThrowIE", "MinimizeIE" };

        /// <summary>Locals the source publishes into an action's context (grimoire/06 section 4.1).</summary>
        public static readonly string[] Locals = { "TargetX", "TargetY", "VelocityX", "VelocityY", "FootX", "FootDX" };

        /// <summary>Every parameter name an action of any class reads (section 5 plus the common ones), so an
        /// expression may name a parameter the action inherits a default for without declaring it.</summary>
        public static readonly string[] KnownParameters =
        {
            "Duration", "Condition", "Draggable", "Affordance", "BorderType", "Loop",
            "TargetX", "TargetY", "LookRight", "X", "Y", "Sound", "VelocityParam", "Arc",
            "InitialVX", "InitialVY", "RegistanceX", "RegistanceY", "Gravity", "OffsetX", "OffsetY", "OffsetType",
            "BornX", "BornY", "BornBehavior", "BornMascot", "BornCount", "BornInterval", "BornTransient",
            "TransformMascot", "TransformBehavior", "Behavior", "TargetBehavior", "TargetLook", "Look",
            "Characteristics", "IeOffsetX", "IeOffsetY",
        };

        // ------------------------------------------------------------------ the feature gate (before the schema)

        /// <summary>Refuse an unknown or unsupported feature by name, before schema validation, so a companion written
        /// for a newer host is told so instead of failing on an element this schema has never heard of.</summary>
        public static void PreCheckFeatures(string xml, IReadOnlyCollection<string> supported)
        {
            string features = ReadHeaderFeatures(xml);
            if (features == null) return;
            var supportedSet = new HashSet<string>(supported ?? CompanionFormatFeatures.Supported, StringComparer.Ordinal);
            foreach (string token in CompanionFormatFeatures.Parse(features))
            {
                if (!CompanionFormatFeatures.IsDefined(token))
                    throw new InvalidDataException("This companion needs a newer Desktop AI Companion (feature '" + token + "').");
                if (!supportedSet.Contains(token))
                    throw new InvalidDataException("This companion uses feature '" + token
                        + "', which this version of Desktop AI Companion does not run yet.");
            }
        }

        /// <summary>The text of header/features, read without validation, stopping at the end of the header; null when
        /// there is none or the document does not get that far (the schema pass then reports what is wrong).</summary>
        private static string ReadHeaderFeatures(string xml)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true,
                MaxCharactersInDocument = CompanionXmlValidator.MaximumXmlBytes,
            };
            try
            {
                using (var text = new StringReader(xml))
                using (XmlReader reader = XmlReader.Create(text, settings))
                {
                    bool inHeader = false;
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            if (!inHeader)
                            {
                                if (reader.Depth == 1 && reader.LocalName == "header") inHeader = true;
                                else if (reader.Depth == 1) return null;   // past where the header must be
                                continue;
                            }
                            if (reader.Depth == 2 && reader.LocalName == "features")
                                return reader.ReadElementContentAsString();
                        }
                        else if (reader.NodeType == XmlNodeType.EndElement && inHeader && reader.Depth == 1)
                            return null;
                    }
                }
            }
            catch (XmlException)
            {
                return null;
            }
            return null;
        }

        // ------------------------------------------------------------------ semantic rules (after the schema)

        public static bool IsTickMode(XmlData.RootNode root)
        {
            return root.Header != null && string.Equals(root.Header.Clock, "tick", StringComparison.Ordinal);
        }

        /// <summary>The declared features, after proving every extension the document USES is declared.</summary>
        public static HashSet<string> CheckDeclaredUsage(XmlData.RootNode root)
        {
            HashSet<string> declared = CompanionFormatFeatures.Parse(root.Header.Features);
            XmlData.HeaderNode h = root.Header;
            Require(declared, CompanionFormatFeatures.Tick, h.Clock != null, "header/clock");
            Require(declared, CompanionFormatFeatures.Packs, h.Pack != null, "header/pack");
            Require(declared, CompanionFormatFeatures.Packs, h.Startup != null, "header/startup");
            Require(declared, CompanionFormatFeatures.Variables, h.Constants != null, "header/constants");
            Require(declared, CompanionFormatFeatures.Behaviours, h.Toggles != null, "header/toggles");
            Require(declared, CompanionFormatFeatures.Tick, root.Image.AnchorXSpecified || root.Image.AnchorYSpecified, "image/anchorx, anchory");
            foreach (XmlData.AnimationNode a in root.Animations.Animation)
            {
                string where = "animation " + a.Id;
                Require(declared, CompanionFormatFeatures.Actions, a.Act != null, where + " <act>");
                Require(declared, CompanionFormatFeatures.Behaviours, a.Behaviour != null || a.Route != null, where + " <behaviour>/<route>");
                foreach (XmlData.NextNode n in EdgesOf(a))
                    Require(declared, CompanionFormatFeatures.Expressions, n.When != null, where + " transition condition");
                if (a.Act != null)
                {
                    string cls = a.Act.Class ?? "";
                    Require(declared, CompanionFormatFeatures.Lifecycle, Array.IndexOf(LifecycleClasses, cls) >= 0, where + " class " + cls);
                    Require(declared, CompanionFormatFeatures.Social, Array.IndexOf(SocialClasses, cls) >= 0 || a.Act.Offers != null, where + " social");
                    Require(declared, CompanionFormatFeatures.Windows, Array.IndexOf(WindowClasses, cls) >= 0, where + " class " + cls);
                    if (cls == "ComplexMove" || cls == "ComplexJump")
                    {
                        string ch = ParamValue(a.Act, "Characteristics") ?? "";
                        Require(declared, CompanionFormatFeatures.Lifecycle, ch.Contains("Breed"), where + " Characteristics Breed");
                        Require(declared, CompanionFormatFeatures.Social, ch.Contains("Scan"), where + " Characteristics Scan");
                    }
                    if (a.Act.Variants != null && a.Act.Variants.Variant != null)
                        foreach (XmlData.VariantNode v in a.Act.Variants.Variant)
                            Require(declared, CompanionFormatFeatures.Hotspots, v != null && v.Hotspots != null, where + " hotspots");
                    bool scripted = false;
                    if (a.Act.Params != null)
                        foreach (XmlData.ParamNode p in a.Act.Params)
                            if (p != null && IsScripted(p.Cache)) scripted = true;
                    if (a.Act.Guard != null || HasVariantConditions(a.Act)) scripted = true;
                    Require(declared, CompanionFormatFeatures.Expressions, scripted, where + " expressions");
                }
            }
            if (root.Sounds != null && root.Sounds.Sound != null)
                foreach (XmlData.SoundNode s in root.Sounds.Sound)
                    Require(declared, CompanionFormatFeatures.Tick, s != null && s.SoundId != null, "sound id");
            if (declared.Count > 0 && !declared.Contains(CompanionFormatFeatures.Tick))
                throw new InvalidDataException("Every Shimeji runtime extension runs on the tick clock: declare feature 'tick' and <clock>tick</clock>.");
            if (declared.Contains(CompanionFormatFeatures.Tick) && !IsTickMode(root))
                throw new InvalidDataException("Feature 'tick' is declared but header/clock is not 'tick'.");
            return declared;
        }

        private static void Require(HashSet<string> declared, string token, bool used, string what)
        {
            if (used && !declared.Contains(token))
                throw new InvalidDataException(what + " needs feature '" + token + "' declared in header/features.");
        }

        private static bool HasVariantConditions(XmlData.ActNode act)
        {
            if (act.Variants == null || act.Variants.Variant == null) return false;
            foreach (XmlData.VariantNode v in act.Variants.Variant)
                if (v != null && !string.IsNullOrEmpty(v.When)) return true;
            return false;
        }

        private static bool IsScripted(string cache)
        {
            return string.Equals(cache, "action", StringComparison.Ordinal) || string.Equals(cache, "frame", StringComparison.Ordinal);
        }

        private static string ParamValue(XmlData.ActNode act, string name)
        {
            if (act.Params == null) return null;
            foreach (XmlData.ParamNode p in act.Params)
                if (p != null && string.Equals(p.Name, name, StringComparison.Ordinal)) return p.Value;
            return null;
        }

        private static IEnumerable<XmlData.NextNode> EdgesOf(XmlData.AnimationNode a)
        {
            if (a.Sequence != null && a.Sequence.Next != null) foreach (XmlData.NextNode n in a.Sequence.Next) if (n != null) yield return n;
            if (a.Border != null && a.Border.Next != null) foreach (XmlData.NextNode n in a.Border.Next) if (n != null) yield return n;
            if (a.Gravity != null && a.Gravity.Next != null) foreach (XmlData.NextNode n in a.Gravity.Next) if (n != null) yield return n;
        }

        /// <summary>The tick-mode document rules: node kinds, poses, composites, names and expressions.</summary>
        public static void ValidateTickDocument(XmlData.RootNode root, int tileCount, HashSet<string> declared)
        {
            XmlData.AnimationNode[] animations = root.Animations.Animation;
            var actByName = new Dictionary<string, XmlData.AnimationNode>(StringComparer.Ordinal);
            var behaviourByName = new Dictionary<string, XmlData.AnimationNode>(StringComparer.Ordinal);
            var offRoute = new List<XmlData.AnimationNode>();
            var kindById = new Dictionary<int, string>();
            foreach (XmlData.AnimationNode a in animations)
                kindById[a.Id] = a.Act != null ? "act" : a.Behaviour != null ? "behaviour" : a.Route != null ? "route" : "?";
            // The route's targets are the ROOT behaviours (the source's BehaviorList, found by name).
            var rootIds = new HashSet<int>();
            foreach (XmlData.AnimationNode a in animations)
                if (a.Route != null)
                    foreach (XmlData.NextNode n in EdgesOf(a)) rootIds.Add(n.Value);
            int routes = 0;
            foreach (XmlData.AnimationNode a in animations)
            {
                string where = "animation " + a.Id;
                int kinds = (a.Act != null ? 1 : 0) + (a.Behaviour != null ? 1 : 0) + (a.Route != null ? 1 : 0);
                if (kinds != 1)
                    throw new InvalidDataException(where + " must be exactly one of <act>, <behaviour> or <route> in a tick-clock companion.");
                if (a.Sequence != null && a.Sequence.Frame != null && a.Sequence.Frame.Length > 0)
                    throw new InvalidDataException(where + " has classic frames; a tick-clock node plays poses inside <act>.");
                if (a.Sequence != null && !string.IsNullOrWhiteSpace(a.Sequence.Action))
                    throw new InvalidDataException(where + " has a classic sequence action.");
                if (a.Act != null)
                {
                    if (string.IsNullOrWhiteSpace(a.Name) || actByName.ContainsKey(a.Name))
                        throw new InvalidDataException(where + " needs a name unique among actions (got '" + a.Name + "').");
                    actByName[a.Name] = a;
                }
                else if (a.Behaviour != null)
                {
                    string n = a.Behaviour.Name;
                    if (string.IsNullOrWhiteSpace(n))
                        throw new InvalidDataException(where + " needs a behaviour name.");
                    if (!rootIds.Contains(a.Id)) offRoute.Add(a);
                    else if (behaviourByName.ContainsKey(n))
                        throw new InvalidDataException(where + " needs a name unique among the route's behaviours (got '" + n + "').");
                    else behaviourByName[n] = a;
                }
                else routes++;
                // In tick mode only behaviour and route nodes have edges, and they lead to behaviours: an action never
                // chains on by itself, the behaviour layer chooses what follows (grimoire/06 4.3, 4.5).
                foreach (XmlData.NextNode e in EdgesOf(a))
                {
                    if (a.Act != null)
                        throw new InvalidDataException(where + " is an action with an edge; in a tick-clock companion the behaviour layer chooses what follows.");
                    string kind;
                    if (!kindById.TryGetValue(e.Value, out kind) || kind != "behaviour")
                        throw new InvalidDataException(where + " has an edge to " + e.Value + ", which is not a behaviour.");
                }
            }
            if (declared.Contains(CompanionFormatFeatures.Behaviours) && routes != 1)
                throw new InvalidDataException("A companion with behaviours needs exactly one <route> node (found " + routes + ").");
            // A next-list entry is a behaviour of its own (BehaviorBuilder 102-120) and shares its name with the root
            // behaviour whose list it continues into: when it ends, the source looks the next list up by that name, and
            // a name with no root behaviour ends the companion there. So an off-route behaviour must carry a root name.
            foreach (XmlData.AnimationNode a in offRoute)
                if (!behaviourByName.ContainsKey(a.Behaviour.Name))
                    throw new InvalidDataException("animation " + a.Id + " is off the route and named '" + a.Behaviour.Name
                        + "', which no behaviour on the route is; the source finds what follows it by that name.");

            var soundIds = new HashSet<string>(StringComparer.Ordinal);
            if (root.Sounds != null && root.Sounds.Sound != null)
                foreach (XmlData.SoundNode s in root.Sounds.Sound)
                    if (s != null && s.SoundId != null && !soundIds.Add(s.SoundId))
                        throw new InvalidDataException("Sound id '" + s.SoundId + "' is used twice.");

            // Which parameter names reach each action through references, for its expression scope.
            var boundNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (XmlData.AnimationNode a in animations)
            {
                if (a.Act != null && a.Act.Children != null && a.Act.Children.Child != null)
                    foreach (XmlData.CompositeChildNode c in a.Act.Children.Child)
                        AddBound(boundNames, c.Animation, c.Bind);
                if (a.Behaviour != null) AddBound(boundNames, a.Behaviour.Runs, a.Behaviour.Bind);
            }

            var constants = new HashSet<string>(StringComparer.Ordinal);
            if (root.Header.Constants != null && root.Header.Constants.Constant != null)
                foreach (XmlData.ConstantNode c in root.Header.Constants.Constant)
                {
                    if (c == null || string.IsNullOrWhiteSpace(c.Name) || !constants.Add(c.Name))
                        throw new InvalidDataException("header/constants has a missing or repeated name.");
                }
            Func<string, bool> selectionScope = n => constants.Contains(n);

            foreach (XmlData.AnimationNode a in animations)
            {
                string where = "animation " + a.Id + " '" + a.Name + "'";
                if (a.Act != null)
                    ValidateAct(a, a.Act, where, tileCount, soundIds, actByName, behaviourByName, boundNames, declared);
                else if (a.Behaviour != null)
                {
                    if (string.IsNullOrWhiteSpace(a.Behaviour.Runs) || !actByName.ContainsKey(a.Behaviour.Runs))
                        throw new InvalidDataException(where + " runs '" + a.Behaviour.Runs + "', which is not an action of this companion.");
                    if (a.Behaviour.Frequency < 0 || a.Behaviour.Frequency > 1000000)
                        throw new InvalidDataException(where + " has an invalid frequency.");
                    ValidateBinds(a.Behaviour.Bind, ScopeFor(a.Behaviour.Runs, actByName, boundNames), where);
                }
                foreach (XmlData.NextNode n in EdgesOf(a))
                    if (n.When != null) ParseExpression(n.When, selectionScope, where + " transition condition");
            }

            XmlData.StartupNode startup = root.Header.Startup;
            if (startup != null)
            {
                if (root.Header.Pack == null || !string.Equals(root.Header.Pack.Role, "main", StringComparison.Ordinal))
                    throw new InvalidDataException("header/startup is allowed only on a pack's main member.");
                if (string.Equals(startup.Member, root.Header.Pack.Member, StringComparison.Ordinal) && !behaviourByName.ContainsKey(startup.Behaviour ?? ""))
                    throw new InvalidDataException("header/startup names behaviour '" + startup.Behaviour + "', which this companion does not have.");
            }
            if (root.Header.Pack != null)
            {
                if (!IsPackKey(root.Header.Pack.Id) || !IsPackKey(root.Header.Pack.Member))
                    throw new InvalidDataException("header/pack id and member must be 1 to 64 characters of letters, digits, '.', '_', '-' or space.");
            }
        }

        private static void AddBound(Dictionary<string, HashSet<string>> bound, string action, XmlData.BindNode[] binds)
        {
            if (string.IsNullOrEmpty(action) || binds == null) return;
            HashSet<string> set;
            if (!bound.TryGetValue(action, out set)) bound[action] = set = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlData.BindNode b in binds) if (b != null && b.Name != null) set.Add(b.Name);
        }

        private static Func<string, bool> ScopeFor(string actionName, Dictionary<string, XmlData.AnimationNode> actByName,
            Dictionary<string, HashSet<string>> boundNames)
        {
            var names = new HashSet<string>(KnownParameters, StringComparer.Ordinal);
            foreach (string l in Locals) names.Add(l);
            XmlData.AnimationNode node;
            if (actionName != null && actByName.TryGetValue(actionName, out node) && node.Act.Params != null)
                foreach (XmlData.ParamNode p in node.Act.Params) if (p != null && p.Name != null) names.Add(p.Name);
            HashSet<string> bound;
            if (actionName != null && boundNames.TryGetValue(actionName, out bound)) names.UnionWith(bound);
            return n => names.Contains(n);
        }

        private static void ValidateAct(XmlData.AnimationNode a, XmlData.ActNode act, string where, int tileCount,
            HashSet<string> soundIds, Dictionary<string, XmlData.AnimationNode> actByName,
            Dictionary<string, XmlData.AnimationNode> behaviourByName, Dictionary<string, HashSet<string>> boundNames,
            HashSet<string> declared)
        {
            bool embedded = string.Equals(act.Type, "Embedded", StringComparison.Ordinal);
            string cls = act.Class ?? "";
            if (embedded != (cls.Length > 0))
                throw new InvalidDataException(where + ": a class is required for Embedded and allowed for nothing else.");
            if (embedded && Array.IndexOf(EmbeddedClasses, cls) < 0)
                throw new InvalidDataException(where + ": embedded class '" + cls + "' is not one this host runs.");
            bool composite = act.Type == "Sequence" || act.Type == "Select";
            if (composite != (act.Children != null))
                throw new InvalidDataException(where + ": <children> belongs on Sequence and Select actions, and they need it.");
            if (act.LoopSpecified && act.Type != "Sequence")
                throw new InvalidDataException(where + ": loop applies to Sequence only.");

            Func<string, bool> scope = ScopeFor(a.Name, actByName, boundNames);
            var paramNames = new HashSet<string>(StringComparer.Ordinal);
            if (act.Params != null)
                foreach (XmlData.ParamNode p in act.Params)
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.Name) || !paramNames.Add(p.Name))
                        throw new InvalidDataException(where + " has a missing or repeated parameter name.");
                    if (IsScripted(p.Cache)) ParseExpression(p.Value, scope, where + " parameter " + p.Name);
                }
            if (act.Guard != null)
            {
                if (act.Guard.When != null) ParseExpression(act.Guard.When, scope, where + " guard when");
                if (act.Guard.Duration != null) ParseExpression(act.Guard.Duration, scope, where + " guard duration");
                if (act.Guard.Draggable != null) ParseExpression(act.Guard.Draggable, scope, where + " guard draggable");
            }
            if (act.Offers != null && act.Offers.Offer != null)
                foreach (XmlData.OfferNode o in act.Offers.Offer)
                    if (o == null || string.IsNullOrEmpty(o.Value) || o.Value.Length > 256)
                        throw new InvalidDataException(where + " has an empty or over-long offer.");

            if (act.Variants != null && act.Variants.Variant != null)
                foreach (XmlData.VariantNode v in act.Variants.Variant)
                {
                    if (v == null || v.Pose == null || v.Pose.Length == 0)
                        throw new InvalidDataException(where + " has a variant with no poses.");
                    if (!string.IsNullOrEmpty(v.When)) ParseExpression(v.When, scope, where + " variant condition");
                    foreach (XmlData.PoseNode p in v.Pose)
                    {
                        if (p == null || p.Frame < -1 || p.Frame >= tileCount)
                            throw new InvalidDataException(where + " has a pose with an invalid frame (frame -1 hides the companion).");
                        if (p.RightSpecified && (p.Right < 0 || p.Right >= tileCount))
                            throw new InvalidDataException(where + " has a pose with an invalid right-facing frame.");
                        if (p.Ticks < 1 || p.Ticks > MaximumPoseTicks)
                            throw new InvalidDataException(where + " has a pose whose duration is not 1 to " + MaximumPoseTicks + " ticks.");
                        if (p.VolumeSpecified && (double.IsNaN(p.Volume) || double.IsInfinity(p.Volume)))
                            throw new InvalidDataException(where + " has a pose with an invalid volume.");
                        if (p.Sound != null && !soundIds.Contains(p.Sound))
                            throw new InvalidDataException(where + " has a pose naming sound '" + p.Sound + "', which is not defined.");
                    }
                    if (v.Hotspots != null && v.Hotspots.Hotspot != null)
                        foreach (XmlData.HotspotNode h in v.Hotspots.Hotspot)
                        {
                            if (h == null || !IsPair(h.Origin) || !IsPair(h.Size))
                                throw new InvalidDataException(where + " has a hotspot whose origin or size is not 'x,y'.");
                            if (h.Behaviour != null && !behaviourByName.ContainsKey(h.Behaviour))
                                throw new InvalidDataException(where + " has a hotspot naming behaviour '" + h.Behaviour + "', which this companion does not have.");
                        }
                }

            if (act.Children != null)
            {
                if (act.Children.Child == null || act.Children.Child.Length == 0)
                    throw new InvalidDataException(where + " is a composite with no children.");
                bool sequence = act.Children.Mode == "sequence";
                if (sequence != (act.Type == "Sequence"))
                    throw new InvalidDataException(where + ": children mode must match the action type.");
                foreach (XmlData.CompositeChildNode c in act.Children.Child)
                {
                    if (c == null || string.IsNullOrEmpty(c.Animation) || !actByName.ContainsKey(c.Animation))
                        throw new InvalidDataException(where + " references action '" + (c == null ? null : c.Animation) + "', which this companion does not have.");
                    ValidateBinds(c.Bind, ScopeFor(c.Animation, actByName, boundNames), where + " -> " + c.Animation);
                }
            }
        }

        private static void ValidateBinds(XmlData.BindNode[] binds, Func<string, bool> scope, string where)
        {
            if (binds == null) return;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlData.BindNode b in binds)
            {
                if (b == null || string.IsNullOrWhiteSpace(b.Name) || !names.Add(b.Name))
                    throw new InvalidDataException(where + " has a missing or repeated binding name.");
                if (IsScripted(b.Cache)) ParseExpression(b.Value, scope, where + " binding " + b.Name);
            }
        }

        private static void ParseExpression(string source, Func<string, bool> scope, string where)
        {
            ScriptProgram program;
            string error;
            if (!SafeScript.TryParse(source, scope, out program, out error))
                throw new InvalidDataException(where + " has an invalid expression: " + error);
        }

        private static bool IsPair(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string[] parts = value.Split(',');
            int x, y;
            return parts.Length == 2 && int.TryParse(parts[0].Trim(), out x) && int.TryParse(parts[1].Trim(), out y)
                && x >= -4096 && x <= 4096 && y >= -4096 && y <= 4096;
        }

        private static bool IsPackKey(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64) return false;
            foreach (char c in value)
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' || c == ' ')) return false;
            return value.Trim().Length == value.Length;
        }
    }
}
