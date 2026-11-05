using System;
using System.ComponentModel;
using System.Xml.Serialization;

namespace XmlData
{
    /// <summary>
    /// This Node class is used to store the XML data using the serialize function.
    /// </summary>
    /// <remarks>Once the XML was loaded, it is possible to see the header info in the about box.</remarks>
    [XmlRoot("animations", Namespace = "https://esheep.petrucci.ch/", IsNullable = false)]
    public class RootNode
    {
        /// <summary>
        /// Main informations about the animation XML. 
        /// </summary>
        [XmlElement("header")]
        public HeaderNode Header;
        /// <summary>
        /// Information about the sprite image.
        /// </summary>
        [XmlElement("image")]
        public ImageNode Image;
        /// <summary>
        /// List of spawns. 
        /// </summary>
        [XmlElement("spawns")]
        public SpawnsNode Spawns;
        /// <summary>
        /// List of animations. 
        /// </summary>
        [XmlElement("animations")]
        public AnimationsNode Animations;
        /// <summary>
        /// List of child animations. 
        /// </summary>
        [XmlElement("childs")]
        public ChildsNode Childs;
        /// <summary>
        /// List of sounds. 
        /// </summary>
        [XmlElement("sounds")]
        public SoundsNode Sounds;
    }


    /// <summary>
    /// Main informations about the animation XML. This information is taken from the loaded xml file.
    /// </summary>
    /// <remarks>Once the XML was loaded, it is possible to see the header info in the about box.</remarks>
    public class HeaderNode
    {
        /// <summary>
        /// Author of the animation.
        /// </summary>
        [XmlElement("author")]
        public string Author;
        /// <summary>
        /// Title of the animation.
        /// </summary>
        [XmlElement("title")]
        public string Title;
        /// <summary>
        /// PetName. Similar to Title but shorter (max 16 chars). "eSheep" word will be replaced with this one in the context menu.
        /// </summary>
        [XmlElement("petname")]
        public string Petname;
        /// <summary>
        /// Version of the animation (is a string and developer can insert what he want).
        /// </summary> 
        [XmlElement("version")]
        public string Version;
        /// <summary>
        ///  Information (About and Copyright information) about the animation and the author.
        /// </summary>
        [XmlElement("info")]
        public string Info;
        /// <summary>
        ///  Application version. Used to parse different XML versions.
        /// </summary>
        [XmlElement("application")]
        public string Application;
        /// <summary>
        ///  Icon (base64) of the pet, used for the task bar.
        /// </summary>
        [XmlElement("icon")]
        public string Icon;
        // ---- Shimeji runtime extensions (grimoire/06). Every one is optional and null on a classic companion, so a
        // classic document serialises exactly as before.
        /// <summary>Space-separated feature tokens the companion uses (grimoire/06 section 1).</summary>
        [XmlElement("features")]
        public string Features;
        /// <summary>"tick" for the 40 ms logical tick; absent for classic interval timing.</summary>
        [XmlElement("clock")]
        public string Clock;
        [XmlElement("pack")]
        public PackNode Pack;
        [XmlElement("startup")]
        public StartupNode Startup;
        [XmlElement("constants")]
        public ConstantsNode Constants;
        [XmlElement("toggles")]
        public TogglesNode Toggles;
    }

    /// <summary>
    ///  Sprite image
    /// </summary>
    public class ImageNode
    {
        /// <summary>
        ///  Quantity of images on the X axis.
        /// </summary>
        [XmlElement("tilesx")]
        public int TilesX;
        /// <summary>
        ///  Quantity of images on the Y axis.
        /// </summary>
        [XmlElement("tilesy")]
        public int TilesY;
        /// <summary>
        ///  The sprite as base64 string.
        /// </summary>
        [XmlElement("png")]
        public string Png;
        /// <summary>
        ///  Color used for the transparency.
        /// </summary>
        [XmlElement("transparency")]
        public string Transparency;
        /// <summary>The contact point (Shimeji ImageAnchor) in cell pixels before scaling; default bottom-centre.</summary>
        [XmlElement("anchorx")]
        public int AnchorX;
        [XmlIgnore]
        public bool AnchorXSpecified;
        [XmlElement("anchory")]
        public int AnchorY;
        [XmlIgnore]
        public bool AnchorYSpecified;
    }

    /// <summary>
    ///  Node with a list of spawns.
    /// </summary>
    public class SpawnsNode
    {
        /// <summary>
        ///  List of spawn nodes.
        /// </summary>
        [XmlElement("spawn")]
        public SpawnNode[] Spawn;
    }

    /// <summary>
    ///  List of animations.
    /// </summary>
    public class AnimationsNode
    {
        /// <summary>
        ///  List of animation nodes.
        /// </summary>
        [XmlElement("animation")]
        public AnimationNode[] Animation;
    }

    /// <summary>
    ///  List of childs.
    /// </summary>
    public class ChildsNode
    {
        /// <summary>
        ///  List of child nodes.
        /// </summary>
        [XmlElement("child")]
        public ChildNode[] Child;
    }

    /// <summary>
    ///  List of sounds.
    /// </summary>
    public class SoundsNode
    {
        /// <summary>
        ///  List of sound nodes.
        /// </summary>
        [XmlElement("sound")]
        public SoundNode[] Sound;
    }

    /// <summary>
    ///  Information about the spawn. Used to start the pet on the screen.
    /// </summary>
    public class SpawnNode
    {
        /// <summary>
        ///  Unique ID of spawn.
        /// </summary>
        [XmlAttribute("id")]
        public int Id;
        /// <summary>
        ///  Probability to use this spawn.
        /// </summary>
        [XmlAttribute("probability")]
        public int Probability;
        /// <summary>
        ///  X start position of the pet.
        /// </summary>
        [XmlElement("x")]
        public string X;
        /// <summary>
        ///  Y start position of the pet.
        /// </summary>
        [XmlElement("y")]
        public string Y;
        /// <summary>
        ///  ID of the next animation.
        /// </summary>
        [XmlElement("next")]
        public NextNode Next;
    }

    /// <summary>
    ///  All information of each single animation is stored here.
    /// </summary>
    public class AnimationNode
    {
        /// <summary>
        ///  Unique ID, used to set the next animation.
        /// </summary>
        [XmlAttribute("id")]
        public int Id;
        /// <summary>
        ///  Name for this animation. With some key-names special actions can be defined. Otherwise it is used for debug purposes.
        /// </summary>
        [XmlElement("name")]
        public string Name;
        /// <summary>
        ///  Information about the start position (velocity, opacity, ...).
        /// </summary>
        [XmlElement("start")]
        public MovingNode Start;
        /// <summary>
        ///  Information about the end position (velocity, opacity, ...).
        /// </summary>
        [XmlElement("end")]
        public MovingNode End;
        /// <summary>
        ///  Sequence of frames to play and information about how to play.
        /// </summary>
        [XmlElement("sequence")]
        public SequenceNode Sequence;
        /// <summary>
        ///  What to do if a pet reach a border of the screen or window.
        /// </summary>
        [XmlElement("border")]
        public HitNode Border;
        /// <summary>
        ///  What to do if a pet doesn't have any gravity anymore.
        /// </summary>
        [XmlElement("gravity")]
        public HitNode Gravity;
        /// <summary>A Shimeji action node (grimoire/06 section 4.1); null on a classic animation.</summary>
        [XmlElement("act")]
        public ActNode Act;
        /// <summary>A Shimeji behaviour node (section 4.2).</summary>
        [XmlElement("behaviour")]
        public BehaviourNode Behaviour;
        /// <summary>The route node: the root behaviour list (section 4.3).</summary>
        [XmlElement("route")]
        public RouteNode Route;
    }

    /// <summary>
    ///  This is like a spawn but for second/child animation.
    /// </summary>
    public class ChildNode
    {
        /// <summary>
        ///  Id of animation. Once that animation is executed, this child is automatically played.
        /// </summary>
        [XmlAttribute("animationid")]
        public int Id;
        /// <summary>
        ///  X position when it is created.
        /// </summary>
        [XmlElement("x")]
        public string X;
        /// <summary>
        ///  Y position when it is created.
        /// </summary>
        [XmlElement("y")]
        public string Y;
        /// <summary>
        ///  The next animation used for this child.
        /// </summary>
        [XmlElement("next")]
        public int Next;
    }

    /// <summary>
    ///  To add sounds on some defined animations.
    /// </summary>
    public class SoundNode
    {
        /// <summary>
        ///  Id of animation. Once that animation is executed, this sound is automatically played.
        /// </summary>
        [XmlAttribute("animationid")]
        public int Id;
        /// <summary>A name a tick-mode pose refers to (<see cref="PoseNode.Sound"/>); null on a classic sound.</summary>
        [XmlAttribute("id")]
        public string SoundId;
        /// <summary>The declared format of <see cref="Base64"/>'s bytes: null (absent) or "mp3" is MP3, "wav" a RIFF/WAVE.
        /// Declared, never sniffed: the validator proves the bytes ARE what this says (grimoire/06 section 3, feature 'wav').</summary>
        [XmlAttribute("format")]
        public string Format;
        /// <summary>
        ///  Probability (in %) that this sound will be played together with the animation.
        /// </summary>
        [XmlElement("probability")]
        public int Probability;
        /// <summary>
        ///  How many times the sound should loop (default: 0). 1 means that the sound will play 2 times.
        /// </summary>
        [DefaultValue(0), XmlElement("loop")]
        public int Loop;
        /// <summary>
        ///  Base64 string of the mp3 sound.
        /// </summary>
        [XmlElement("base64")]
        public string Base64;
    }

    /// <summary>
    ///  Information about the moves.
    /// </summary>
    /// <remarks>
    ///  There are 2 types: start and end. If a sequence has 10 frame sequences, the other 8 frames will interpolate
    ///  the values from start and end.
    /// </remarks>
    public class MovingNode
    {
        /// <summary>
        ///  How many pixels to move in the X axis.
        /// </summary>
        [XmlElement("x")]
        public string X;
        /// <summary>
        ///  How many pixels to move in the Y axis.
        /// </summary>
        [XmlElement("y")]
        public string Y;
        /// <summary>
        ///  Graphical offset, from the physical position calculated for the pet.
        /// </summary>
        [XmlElement("offsety")]
        public int OffsetY;
        /// <summary>
        ///  Opacity from 0.0 (invisible) to 1.0 (opaque).
        /// </summary>
        [XmlElement("opacity")]
        public double Opacity = 1.0;
        /// <summary>
        ///  Interval until the next sequence is executed.
        /// </summary>
        [XmlElement("interval")]
        public string Interval;
    }

    /// <summary>
    ///  Sequence node.
    /// </summary>
    public class SequenceNode
    {
        /// <summary>
        ///  If repeat is > 0, repeat from indicate from which frame it should be repeated.
        /// </summary>
        [XmlAttribute("repeatfrom")]
        public int RepeatFromFrame;
        /// <summary>
        ///  How many times the sequence should be executed (value of 0 or 1 is NO-REPEAT).
        /// </summary>
        [XmlAttribute("repeat")]
        public string RepeatCount;
        /// <summary>
        ///  An array of images to show for this sequence.
        /// </summary>
        [XmlElement("frame")]
        public int[] Frame;
        /// <summary>
        ///  The next animation, once the sequence is over.
        /// </summary>
        [XmlElement("next")]
        public NextNode[] Next;
        /// <summary>
        ///  Action to execute if this animation is over.
        /// </summary>
        [XmlElement("action")]
        public string Action;
    }

    /// <summary>
    ///  Hit node indicate an array of next animations if the pet hit a border or has no gravity.
    /// </summary>
    public class HitNode
    {
        /// <summary>
        ///  List of next animations.
        /// </summary>
        [XmlElement("next")]
        public NextNode[] Next;
    }

    /// <summary>
    ///  Next animation to play. Animation, Border or Gravity have 0 or more Next-nodes.
    /// </summary>
    public class NextNode
    {
        /// <summary>
        ///  Probability this will be the next animation.
        /// </summary>
        [XmlAttribute("probability")]
        public int Probability;
        /// <summary>
        ///  Only flag, <see cref="TNextAnimation.TOnly"/>.
        /// </summary>
        [XmlAttribute("only")]
        public string OnlyFlag;
        /// <summary>A SafeScript condition (grimoire/06 section 6); the edge is eligible only while it holds. Null on
        /// a classic edge.</summary>
        [XmlAttribute("when")]
        public string When;
        /// <summary>
        ///  Next animation ID.
        /// </summary>
        [XmlText]
        public int Value;
    }

    // ================================================================ Shimeji runtime extensions (grimoire/06)

    public class PackNode
    {
        [XmlAttribute("id")] public string Id;
        [XmlAttribute("member")] public string Member;
        /// <summary>main, egg, form or helper.</summary>
        [XmlAttribute("role")] public string Role;
        [XmlAttribute("hidden")] public bool Hidden;
        [XmlIgnore] public bool HiddenSpecified;
    }

    public class StartupNode
    {
        [XmlAttribute("member")] public string Member;
        [XmlAttribute("behaviour")] public string Behaviour;
    }

    public class ConstantsNode
    {
        [XmlElement("constant")] public ConstantNode[] Constant;
    }

    public class ConstantNode
    {
        [XmlAttribute("name")] public string Name;
        [XmlAttribute("value")] public string Value;
    }

    public class TogglesNode
    {
        [XmlElement("toggle")] public ToggleNode[] Toggle;
    }

    public class ToggleNode
    {
        [XmlAttribute("id")] public string Id;
        [XmlAttribute("label")] public string Label;
        [XmlAttribute("default")] public bool Default = true;
    }

    /// <summary>One Shimeji action (section 4.1). Child order is the schema's: param, guard, offers, variants, children.</summary>
    public class ActNode
    {
        /// <summary>Stay, Move, Animate, Sequence, Select or Embedded.</summary>
        [XmlAttribute("type")] public string Type;
        /// <summary>The embedded class's short name (section 5); empty unless Type is Embedded.</summary>
        [XmlAttribute("class")] public string Class;
        /// <summary>Floor, Wall, Ceiling or empty.</summary>
        [XmlAttribute("border")] public string Border;
        [XmlAttribute("loop")] public bool Loop;
        [XmlIgnore] public bool LoopSpecified;
        [XmlElement("param")] public ParamNode[] Params;
        [XmlElement("guard")] public GuardNode Guard;
        [XmlElement("offers")] public OffersNode Offers;
        [XmlElement("variants")] public VariantsNode Variants;
        [XmlElement("children")] public ChildrenNode Children;
    }

    public class ParamNode
    {
        [XmlAttribute("name")] public string Name;
        [XmlAttribute("value")] public string Value;
        /// <summary>action (source ${}), frame (source #{}) or none (a literal).</summary>
        [XmlAttribute("cache")] public string Cache;
    }

    public class GuardNode
    {
        [XmlAttribute("when")] public string When;
        [XmlAttribute("duration")] public string Duration;
        [XmlAttribute("draggable")] public string Draggable;
    }

    public class OffersNode
    {
        [XmlElement("offer")] public OfferNode[] Offer;
    }

    public class OfferNode
    {
        [XmlAttribute("value")] public string Value;
    }

    public class VariantsNode
    {
        [XmlElement("variant")] public VariantNode[] Variant;
    }

    public class VariantNode
    {
        [XmlAttribute("when")] public string When;
        [XmlAttribute("turn")] public bool Turn;
        [XmlIgnore] public bool TurnSpecified;
        [XmlElement("pose")] public PoseNode[] Pose;
        [XmlElement("hotspots")] public HotspotsNode Hotspots;
    }

    /// <summary>One pose (section 3): a sheet frame (or -1, which hides the companion), an optional right-facing
    /// frame, a duration in ticks and a per-tick velocity, and the pose image's own geometry: each frame sits at its
    /// cell's top-left, and a pose says how much of the cell is the image and where its anchor is.</summary>
    public class PoseNode
    {
        [XmlAttribute("frame")] public int Frame;
        [XmlAttribute("right")] public int Right;
        [XmlIgnore] public bool RightSpecified;
        [XmlAttribute("ticks")] public int Ticks;
        [XmlAttribute("dx")] public int DX;
        [XmlAttribute("dy")] public int DY;
        [XmlAttribute("sound")] public string Sound;
        [XmlAttribute("volume")] public double Volume;
        [XmlIgnore] public bool VolumeSpecified;
        // The image's ImageAnchor in the LEFT image's pixels, before scaling (default: the type's anchor); the left
        // image's size inside its cell (default: the cell); the explicit right image's size, only with right (default:
        // the left image's size). Facing right the anchor's x is measured from the right image's right edge.
        [XmlAttribute("ax")] public int AnchorX;
        [XmlIgnore] public bool AnchorXSpecified;
        [XmlAttribute("ay")] public int AnchorY;
        [XmlIgnore] public bool AnchorYSpecified;
        [XmlAttribute("w")] public int Width;
        [XmlIgnore] public bool WidthSpecified;
        [XmlAttribute("h")] public int Height;
        [XmlIgnore] public bool HeightSpecified;
        [XmlAttribute("rw")] public int RightWidth;
        [XmlIgnore] public bool RightWidthSpecified;
        [XmlAttribute("rh")] public int RightHeight;
        [XmlIgnore] public bool RightHeightSpecified;
    }

    public class HotspotsNode
    {
        [XmlElement("hotspot")] public HotspotNode[] Hotspot;
    }

    public class HotspotNode
    {
        /// <summary>rect or ellipse.</summary>
        [XmlAttribute("shape")] public string Shape;
        /// <summary>"x,y" in cell pixels.</summary>
        [XmlAttribute("origin")] public string Origin;
        /// <summary>"w,h" in cell pixels.</summary>
        [XmlAttribute("size")] public string Size;
        /// <summary>The behaviour a press enters; null for a held-click (head-pat) hotspot.</summary>
        [XmlAttribute("behaviour")] public string Behaviour;
    }

    public class ChildrenNode
    {
        /// <summary>sequence or select.</summary>
        [XmlAttribute("mode")] public string Mode;
        [XmlElement("child")] public CompositeChildNode[] Child;
    }

    /// <summary>One reference inside a Sequence or Select: the action node by name, and this reference's bindings.</summary>
    public class CompositeChildNode
    {
        [XmlAttribute("animation")] public string Animation;
        [XmlElement("bind")] public BindNode[] Bind;
    }

    public class BindNode
    {
        [XmlAttribute("name")] public string Name;
        [XmlAttribute("value")] public string Value;
        /// <summary>As <see cref="ParamNode.Cache"/>: action, frame or none (a literal).</summary>
        [XmlAttribute("cache")] public string Cache;
    }

    /// <summary>One Shimeji behaviour (section 4.2). Its sequence's edges are its NextBehaviorList.</summary>
    public class BehaviourNode
    {
        [XmlAttribute("name")] public string Name;
        /// <summary>The root action node it runs (the source's Behavior Action=, defaulting to Name).</summary>
        [XmlAttribute("runs")] public string Runs;
        [XmlAttribute("frequency")] public int Frequency;
        [XmlAttribute("hidden")] public bool Hidden;
        [XmlIgnore] public bool HiddenSpecified;
        [XmlAttribute("toggle")] public string Toggle;
        /// <summary>When this behaviour ends, whether the root candidates stay ahead of its own edges (the source's
        /// NextBehaviorList Add). Absent means true, the source's reading of a behaviour with no NextBehaviorList;
        /// the converter writes false for a NextBehaviorList whose Add is false or missing.</summary>
        [XmlAttribute("add")] public bool Add;
        [XmlIgnore] public bool AddSpecified;
        [XmlElement("bind")] public BindNode[] Bind;
    }

    /// <summary>The route node (section 4.3): its sequence's edges are the root behaviour list.</summary>
    public class RouteNode
    {
    }

    /// <summary>
    /// A complete deep copy of any node in this file, so a caller that clones a document (Companion Studio's
    /// behaviour-chain builder, the converter's migrations) cannot drop a field it did not know about: every public
    /// instance field is copied, arrays element by element, nested nodes recursively. A self-test serialises a clone
    /// and its source and requires byte-identical output.
    /// </summary>
    public static class NodeCopy
    {
        public static T Deep<T>(T source) where T : class
        {
            return (T)DeepObject(source);
        }

        private static object DeepObject(object source)
        {
            if (source == null) return null;
            Type type = source.GetType();
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)) return source;
            if (type.IsArray)
            {
                var array = (Array)source;
                Array copy = Array.CreateInstance(type.GetElementType(), array.Length);
                for (int i = 0; i < array.Length; i++) copy.SetValue(DeepObject(array.GetValue(i)), i);
                return copy;
            }
            object clone = Activator.CreateInstance(type);
            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                field.SetValue(clone, DeepObject(field.GetValue(source)));
            return clone;
        }
    }

}
