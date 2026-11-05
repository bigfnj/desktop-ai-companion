using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// How faithfully a Shimeji action survives conversion to the desktopPet animations.xml format.
    /// The taxonomy is fixed and documented in tools/ShimejiConvert/MAPPING.md:
    ///   Group1 = preservable with converter-only work (deterministic map, sheet baking, magic names);
    ///   Group2 = preservable only with new host state the format exposes (cursorX/cursorY/selfX/selfY);
    ///   Group3 = structurally out of scope (IE window manipulation, autonomous breeding) -- residue.
    /// </summary>
    public enum FidelityGroup { Group1 = 1, Group2 = 2, Group3 = 3 }

    /// <summary>
    /// One top-level Shimeji &lt;Action&gt; (a direct child of an &lt;ActionList&gt;), captured with just
    /// enough shape to classify it. Later stages (compositor, emitter) extend this with poses and the
    /// reference tree; Stage 1 only needs the fields the classifier reads plus the raw subtree text.
    /// </summary>
    public sealed class ShimejiAction
    {
        /// <summary>The Name attribute (top-level actions always have one).</summary>
        public string Name;

        /// <summary>The observed Type attribute. Driven off observed values, NOT the vendor XSD: the shipped
        /// actions.xml uses Sequence/Floor/Stay/Animate/Wall/Ceiling, none of which Mascot.xsd permits.</summary>
        public string Type;

        /// <summary>The short embedded-class name (after the last '.'), or null. e.g. Class=
        /// "com.group_finity.mascot.action.ThrowIE" -&gt; "ThrowIE". Only Type="Embedded" carries one.</summary>
        public string Class;

        /// <summary>Floor / Wall / Ceiling, or null.</summary>
        public string BorderType;

        /// <summary>The EXPRESSION text in this action's subtree (itself and all descendants): every Condition
        /// value and every scripted (${...} / #{...}) attribute value, concatenated. Not names, image paths or
        /// sound paths, which an earlier version included and which made a sprite called cursor*.png read as a
        /// cursor condition (F441). The classifier scans this for the state references (activeIE, cursor,
        /// mascot.anchor, totalCount) that decide whether an action needs host state a converted pet cannot
        /// express today.</summary>
        public string SubtreeBlob;

        /// <summary>Result of classification (see <see cref="ActionClassifier"/>).</summary>
        public FidelityGroup Group;

        /// <summary>Human-readable reason the action landed in its group -- the residue report's text.</summary>
        public string Reason;

        /// <summary>
        /// The source declared this action PLAYS THROUGH ONCE rather than looping: a classic conf's
        /// Type="Animate" (Move loops until its target is reached and Stay holds; Animate is the one-shot),
        /// or a bundle animation whose <c>loop</c> is ONESHOT rather than LOOP. Read by the emitter's
        /// mount-prefix rule (<c>PetEmitter.MountPrefixLength</c>): a surface pose the converter loops for
        /// reach may repeat from past its stationary intro only when the author never asked for the whole
        /// block to loop. A declared LOOP over a block that opens with a still pose is the stock ClimbWall's
        /// pause-step rhythm, not a mount, and stays whole.
        /// </summary>
        public bool PlaysOnce;

        /// <summary>The &lt;Animation&gt; blocks directly on this action (empty for a composite action, which
        /// carries ActionReference/nested-Action children instead). Populated for the emitter (Stage 3).</summary>
        public readonly List<ShimejiAnimation> Animations = new List<ShimejiAnimation>();

        /// <summary>Names referenced from this action's subtree (&lt;ActionReference Name&gt; and any nested
        /// &lt;Action Name&gt;). A behaviour names a top-level (often composite) action, but the sprites live on
        /// the low-level posed actions it plays; this lets the emitter carry a behaviour's Frequency down to
        /// the floor spokes it actually produces. Empty for a simple posed action.</summary>
        public List<string> ReferencedActions = new List<string>();
    }

    /// <summary>One Shimeji &lt;Pose&gt;: a single sprite frame with its anchor, per-pose velocity and hold.</summary>
    public sealed class ShimejiPose
    {
        public string Image;   // e.g. "/shime1.png" (leading slash, relative to the skin's img dir)
        public int AnchorX;    // ImageAnchor x -- the hotspot that stays fixed as frames change
        public int AnchorY;    // ImageAnchor y
        public int VelX;       // Velocity x (px per tick)
        public int VelY;       // Velocity y
        public int Duration;   // ticks to hold this frame
        public string Sound;   // Sound attribute (a clip path), or null -- desktopPet pets are silent, so dropped
        public bool ScriptFlattened; // true if any numeric attr was a ${...}/#{...} script flattened to a fixed value

        /// <summary>
        /// Composite this frame with its anchor on the cell's TOP edge instead of its bottom. Set only for
        /// ceiling poses, and set by the emitter rather than the parser: at the top border the engine pins the
        /// WINDOW's top edge to the screen top, so for a hanging pet the contact point has to be the top of
        /// the cell, exactly mirroring the floor case where the window's bottom edge is the ground contact.
        /// </summary>
        public bool AnchorToTop;

        /// <summary>
        /// The anchor was DERIVED from a declared sprite size rather than authored for this pose, so the
        /// compositor re-derives it from the bitmap it actually decodes. An Android bundle anchors every pose
        /// bottom-centre at (width/2, height) of the manifest's sprites.size, and 5 of 948 real bundles ship
        /// sprites that disagree with their own manifest: an anchor taken from the manifest then floats a
        /// shorter sprite above the floor line or clips a taller one, and nothing downstream can see it
        /// (F444). A classic skin authors ImageAnchor per pose and leaves this false.
        /// </summary>
        public bool AnchorFollowsSprite;

        /// <summary>Frame identity for the sprite sheet: a given image placed with a given anchor is one tile.
        /// Two poses that reuse the same image at the same anchor share a tile; a different anchor is a
        /// different tile, because the anchor is baked into pixel placement.</summary>
        /// AnchorToTop is part of the identity: the same image top-anchored and bottom-anchored is two
        /// DIFFERENT tiles, so a skin that reuses one sprite for both a floor and a ceiling pose cannot end up
        /// sharing a tile and silently getting one of them wrong.
        public string FrameKey
        {
            get { return (Image ?? "") + "|" + AnchorX + "|" + AnchorY + (AnchorToTop ? "|top" : ""); }
        }
    }

    /// <summary>One &lt;Animation&gt; block: an ordered run of poses, with an optional selection Condition.</summary>
    public sealed class ShimejiAnimation
    {
        public string Condition;  // optional; a Group2 signal if it references cursor/anchor/activeIE state
        public readonly List<ShimejiPose> Poses = new List<ShimejiPose>();
    }

    /// <summary>
    /// One &lt;Condition&gt; gate in behaviors.xml (a Condition wrapper, or the Condition attribute on a
    /// Behavior / BehaviorReference). These decide whether Shimeji's behaviour selection can be reproduced by
    /// the desktopPet only= situation enum + probability weights, or whether it needs state the format cannot
    /// see. Reported alongside the action census.
    /// </summary>
    public sealed class ShimejiBehaviorCondition
    {
        public string Owner;      // the Name of the behavior/reference, or "<wrapper>" for a bare Condition
        public string Condition;  // the raw expression text
        public FidelityGroup Group;
        public string Reason;
    }

    /// <summary>A parsed Shimeji configuration: its top-level actions, its behaviour-selection conditions,
    /// and a CENSUS of every pose in the document, gathered independently of action nesting. The census is
    /// read by the script-flattening residue note and the parser self-tests; the compositor walks the
    /// actions' own pose lists (PetEmitter.PosesToComposite), so a pose nested inside a composite action is
    /// counted here and never composited (F455).</summary>
    public sealed class ShimejiConfig
    {
        /// <summary>Ordered source IR for the tick-runtime converter. The legacy frame projection below
        /// remains available to existing callers. Bundle-only configurations have no XML source.</summary>
        public ShimejiSourceConfig Source { get; internal set; }
        public readonly List<ShimejiAction> Actions = new List<ShimejiAction>();
        public readonly List<ShimejiBehaviorCondition> BehaviorConditions = new List<ShimejiBehaviorCondition>();
        public readonly List<ShimejiPose> Poses = new List<ShimejiPose>();

        /// <summary>Root-level behaviour selection weights from behaviors.xml, keyed by behaviour name (which
        /// is the name of the action it runs). Empty when the skin ships no behaviors.xml. The emitter weights
        /// the hub's action choices by these so a pet moves and rests at the source author's real frequencies
        /// instead of a flat pick that drowns locomotion under a character's many idle poses.</summary>
        public readonly Dictionary<string, int> BehaviorFrequency =
            new Dictionary<string, int>(System.StringComparer.Ordinal);

        /// <summary>The behaviours file <c>ShimejiParser.ParseConfDirectory</c> read beside the actions file,
        /// or null when the conf directory carried none under any accepted name -- every frequency then
        /// stays at zero, and ConvertSkin says so in the residue instead of leaving a flat hub unexplained.
        /// In-memory parses (self-tests, the census, bundles) leave it null.</summary>
        public string BehaviorsFile;
    }

    /// <summary>The source Variable.parse envelope, without evaluating or rewriting its expression.</summary>
    public enum ShimejiSourceCache { Literal, Action, Frame }

    public sealed class ShimejiSourceAttribute
    {
        public string Name { get; private set; }
        public string SourceName { get; private set; }
        public string NamespaceName { get; private set; }
        public string RawValue { get; private set; }
        public string Expression { get; private set; }
        public ShimejiSourceCache Cache { get; private set; }
        public string SourceKey { get; private set; }

        internal ShimejiSourceAttribute(string name, string sourceName, string namespaceName,
            string rawValue, string sourceKey)
        {
            Name = name;
            SourceName = sourceName;
            NamespaceName = namespaceName;
            RawValue = rawValue;
            SourceKey = sourceKey;
            // Match Java Variable.parse exactly. Embedded or padded markers are literal strings;
            // attribute-specific lowering decides whether this value is a script-capable parameter.
            bool envelope = rawValue.Length >= 3 && rawValue.EndsWith("}", System.StringComparison.Ordinal);
            if (envelope && rawValue.StartsWith("${", System.StringComparison.Ordinal)) Cache = ShimejiSourceCache.Action;
            else if (envelope && rawValue.StartsWith("#{", System.StringComparison.Ordinal)) Cache = ShimejiSourceCache.Frame;
            else Cache = ShimejiSourceCache.Literal;
            if (Cache != ShimejiSourceCache.Literal) Expression = rawValue.Substring(2, rawValue.Length - 3);
        }
    }

    public enum ShimejiSourceKind
    {
        Unknown, Mascot, ActionList, Action, ActionReference, Animation, Pose, Hotspot,
        Constant, Condition, BehaviorList, Behavior, BehaviorReference, NextBehaviorList, NextBehavior
    }

    /// <summary>A semantic XML node. Ordered children and all attributes survive, including extension
    /// nodes, nested actions, reference overrides and condition wrappers. No numeric fallback is applied.</summary>
    public sealed class ShimejiSourceElement
    {
        public ShimejiSourceKind Kind { get; private set; }
        public string Name { get; private set; }
        public string SourceName { get; private set; }
        public string NamespaceName { get; private set; }
        public string ElementPath { get; private set; }
        public string SourceKey { get; private set; }
        public int Line { get; private set; }
        public int Column { get; private set; }
        public string Text { get; private set; }
        public ReadOnlyCollection<ShimejiSourceAttribute> Attributes { get; private set; }
        public ReadOnlyCollection<ShimejiSourceElement> Children { get; private set; }

        internal ShimejiSourceElement(string name, string sourceName, string namespaceName,
            string path, string sourceKey, int line, int column, string text,
            IList<ShimejiSourceAttribute> attributes, IList<ShimejiSourceElement> children)
        {
            ShimejiSourceKind kind;
            Kind = System.Enum.TryParse(name, false, out kind) ? kind : ShimejiSourceKind.Unknown;
            Name = name;
            SourceName = sourceName;
            NamespaceName = namespaceName;
            ElementPath = path;
            SourceKey = sourceKey;
            Line = line;
            Column = column;
            Text = text;
            Attributes = new List<ShimejiSourceAttribute>(attributes).AsReadOnly();
            Children = new List<ShimejiSourceElement>(children).AsReadOnly();
        }

        /// <summary>Returns the first canonical attribute, retaining any alias collision in Attributes
        /// for the lowering validator to diagnose rather than overwriting it during parsing.</summary>
        public ShimejiSourceAttribute Attribute(string canonicalName)
        {
            foreach (ShimejiSourceAttribute attribute in Attributes)
                if (string.Equals(attribute.Name, canonicalName, System.StringComparison.Ordinal)) return attribute;
            return null;
        }

        public IEnumerable<ShimejiSourceElement> DescendantsAndSelf()
        {
            var pending = new Stack<ShimejiSourceElement>();
            pending.Push(this);
            while (pending.Count != 0)
            {
                ShimejiSourceElement current = pending.Pop();
                yield return current;
                for (int index = current.Children.Count - 1; index >= 0; index--) pending.Push(current.Children[index]);
            }
        }
    }

    public sealed class ShimejiSourceDocument
    {
        /// <summary>Relative configuration leaf, never an absolute workstation path.</summary>
        public string FileName { get; private set; }
        public ShimejiSourceElement Root { get; private set; }

        internal ShimejiSourceDocument(string fileName, ShimejiSourceElement root)
        {
            FileName = fileName;
            Root = root;
        }
    }

    public sealed class ShimejiSourceConfig
    {
        public ShimejiSourceDocument Actions { get; private set; }
        public ShimejiSourceDocument Behaviors { get; private set; }

        internal ShimejiSourceConfig(ShimejiSourceDocument actions, ShimejiSourceDocument behaviors)
        {
            Actions = actions;
            Behaviors = behaviors;
        }
    }
}
