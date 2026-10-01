using System.Collections.Generic;

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
}
