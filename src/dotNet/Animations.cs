using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DesktopAICompanion
{
    internal static class AnimationRuntimeLimits
    {
        public const int MaximumRepeat = 1000;
        public const int MaximumTotalSteps = 1000000;
        public const int MaximumMovementPerTick = 32768;
        public const int MaximumTimerInterval = 60000;
        public const int MaximumOffscreenDistance = 8192;

        public static int ClampRepeat(int value)
        {
            return Math.Max(0, Math.Min(MaximumRepeat, value));
        }

        public static int ClampInterval(int value)
        {
            return Math.Max(1, Math.Min(MaximumTimerInterval, value));
        }

        public static int ClampMovement(int value)
        {
            return Math.Max(-MaximumMovementPerTick, Math.Min(MaximumMovementPerTick, value));
        }

        public static int CalculateTotalSteps(int frameCount, int repeatFrom, int repeat)
        {
            if (frameCount < 1) return 1;
            repeatFrom = Math.Max(0, Math.Min(frameCount - 1, repeatFrom));
            long total = frameCount +
                (long)(frameCount - repeatFrom) * ClampRepeat(repeat);
            return (int)Math.Max(1L, Math.Min(MaximumTotalSteps, total));
        }

        public static int LastStepIndex(int totalSteps)
        {
            return Math.Max(1, totalSteps) - 1;
        }

        public static int InterpolationSteps(int totalSteps)
        {
            return totalSteps <= 1 ? 1 : totalSteps - 1;
        }

        public static int SequenceFrameIndex(int step, int frameCount, int repeatFrom)
        {
            if (frameCount < 1)
                throw new ArgumentOutOfRangeException("frameCount");

            int safeStep = Math.Max(0, step);
            if (safeStep < frameCount) return safeStep;

            int safeRepeatFrom = Math.Max(0, Math.Min(frameCount - 1, repeatFrom));
            int repeatLength = frameCount - safeRepeatFrom;
            return safeRepeatFrom + ((safeStep - frameCount) % repeatLength);
        }

        /// <summary>
        /// Keep XML-derived monitor-local coordinates within a generous off-screen margin. Pets may
        /// intentionally enter from just outside a display, but extreme integer coordinates can
        /// overflow mirror arithmetic and create unusable WinForms bounds.
        /// </summary>
        public static int ClampLocalPosition(long value, int monitorExtent)
        {
            long minimum = -MaximumOffscreenDistance;
            long maximum = Math.Min(
                (long)int.MaxValue,
                Math.Max(0L, (long)monitorExtent) + MaximumOffscreenDistance);
            return (int)Math.Max(minimum, Math.Min(maximum, value));
        }

        public static int MirrorLocalX(int value, int monitorWidth, int spriteWidth)
        {
            return ClampLocalPosition(
                (long)Math.Max(0, monitorWidth) - value - Math.Max(0, spriteWidth),
                monitorWidth);
        }

        /// <summary>
        /// Convert a parent's actual monitor-local X back to the canonical left-facing coordinate
        /// used by child expressions. The child result can then cross the full-screen mirror
        /// boundary exactly once.
        /// </summary>
        public static int CanonicalParentX(
            int actualX,
            bool parentFlipped,
            int monitorWidth,
            int parentSpriteWidth)
        {
            return parentFlipped
                ? MirrorLocalX(
                    actualX,
                    monitorWidth,
                    parentSpriteWidth)
                : ClampLocalPosition(actualX, monitorWidth);
        }

        public static double ClampVirtualPosition(
            double value,
            int monitorOrigin,
            int monitorExtent)
        {
            double minimum = Math.Max(
                (long)int.MinValue,
                (long)monitorOrigin - MaximumOffscreenDistance);
            double maximum = Math.Min(
                (long)int.MaxValue,
                (long)monitorOrigin +
                    Math.Max(0L, (long)monitorExtent) +
                    MaximumOffscreenDistance);

            if (double.IsNaN(value))
                return Math.Max(minimum, Math.Min(maximum, monitorOrigin));
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        public static int ClipCut(double amount, int fullExtent)
        {
            if (fullExtent <= 0 || double.IsNaN(amount) || amount <= 0.0)
                return 0;
            if (double.IsPositiveInfinity(amount) || amount >= fullExtent)
                return fullExtent;
            return Math.Min(fullExtent, (int)Math.Ceiling(amount));
        }

        public static int ClampFormCoordinate(double value)
        {
            if (double.IsNaN(value)) return 0;
            if (value <= int.MinValue) return int.MinValue;
            if (value >= int.MaxValue) return int.MaxValue;
            return (int)value;
        }

        public static bool IsSpriteFullyOutside(
            double left,
            double top,
            int width,
            int height,
            int boundsX,
            int boundsY,
            int boundsWidth,
            int boundsHeight)
        {
            double right = left + Math.Max(1, width);
            double bottom = top + Math.Max(1, height);
            double boundsRight = (long)boundsX + Math.Max(0L, (long)boundsWidth);
            double boundsBottom = (long)boundsY + Math.Max(0L, (long)boundsHeight);

            return right <= boundsX ||
                left >= boundsRight ||
                bottom <= boundsY ||
                top >= boundsBottom;
        }
    }

    /// <summary>
    /// In the XML you can write also strings, not only numbers.<br />
    /// So a movement or a number can also be dynamic.
    /// </summary>
    /// <remarks>
    /// Values are converted to integers in the application. But to allow flexibility, you can insert also some strings:<br />
    /// - screenW / screenH = width / height of screen<br />
    /// - areaW / areaH = width / height of area<br />
    /// - imageW / imageH = width / height of the image frame<br />
    /// - imageX / imageY = left / top position of the parent image<br />
    /// - random = a random number between 0 and 99 (inclusive)<br />
    /// - randS = a number between 0 and 99 (inclusive). This number doesn't change until next spawn<br />
    /// If you want discover more about what you can do, see <a href="https://msdn.microsoft.com/en-us/library/system.data.datacolumn.expression(v=vs.110).aspx">https://msdn.microsoft.com/en-us/library/system.data.datacolumn.expression(v=vs.110).aspx</a><br />
    /// </remarks>
    public struct TValue
    {
        /// <summary>
        /// The XML runtime that owns this value. Keeping evaluator ownership on each value prevents
        /// a newly staged pet from redirecting expressions used by an already-running pet.
        /// </summary>
        internal Xml Evaluator;

            /// <summary>
            /// If the parsed value contains a dynamic number
            /// </summary>
        public bool IsDynamic;
            /// <summary>
            /// If the parsed value contains a screen number (multiscreen have different sizes)
            /// </summary>
        public bool IsScreen;
            /// <summary>
            /// String with the expression to compute
            /// </summary>
        public string Compute;
            /// <summary>
            /// Computed value <see cref="Compute"/>
            /// </summary>
        public int Value;
        
            /// <summary>
            /// Get integer value from XML expression. IF expression is a string and contains the word "random",
            /// the returned value changes each time.
            /// </summary>
            /// <returns>The value parsed from xml file</returns>
        public int GetValue(int screenIndex = -1)
        {
            Xml evaluator = Evaluator;
            if (IsDynamic)
            {
                return evaluator == null
                    ? Value
                    : evaluator.ParseValue(Compute, "Animations.GetValue()", screenIndex);
            }
            else if(IsScreen && screenIndex >= 0)
            {
                return evaluator == null
                    ? Value
                    : evaluator.ParseValue(Compute, "Animations.GetValue()", screenIndex);
            }
            else
            {
                return Value;
            }
        }

        /// <summary>
        /// Re-evaluate the original XML expression without any automatic sprite-scale multiplier.
        /// Used before applying the effective 1x/2x/4x movement factor so repeated animation starts
        /// cannot compound an already-scaled cached value.
        /// </summary>
        public int GetRawValue(int screenIndex = -1)
        {
            Xml evaluator = Evaluator;
            if (!string.IsNullOrWhiteSpace(Compute) && evaluator != null)
                return evaluator.ParseValue(Compute, "Animations.GetRawValue()", screenIndex);
            return Value;
        }
    }

        /// <summary>
        /// Animation movement step (moves for each frame)
        /// </summary>
    public struct TMovement
    {
            /// <summary>
            /// Movement on the X axis
            /// </summary>
        public TValue X;
            /// <summary>
            /// Movement on the Y axis
            /// </summary>
        public TValue Y;
            /// <summary>
            /// Interval before the next step will be executed
            /// </summary>
        public TValue Interval;
            /// <summary>
            /// Move image from its position in the Y axis
            /// </summary>
        public int OffsetY;
            /// <summary>Original XML offset before the effective render scale is applied.</summary>
        public int UnscaledOffsetY;
            /// <summary>
            /// Opacity of the pet (0.0 = transparent, 1.0 = opaque)
            /// </summary>
        public double Opacity;
    }

    /// <summary>
    /// Information about next animation
    /// </summary>
    /// <remarks>
    /// On each animation sequence, there are 3 different NEXT:<br />
    /// 1- end of sequence<br />
    /// 2- gravity detected<br />
    /// 3- border detected<br />
    /// If all frames where played, Next-"end of sequence" will be executed.<br />
    /// If Next-"gravity" is set, pet will fall if no gravity is detected.<br />
    /// If a border is detected, Next-"border" will be executed.<br />
    /// <b>Note: if sequence is over or border was detected but you don't have a next statement for it, pet will re-spawn!</b><br />
    /// </remarks>
    public struct TNextAnimation
    {
            /// <summary>
            /// Enumeration about the Next structure.
            /// You can limit the next function to a state:
            /// </summary>
        public enum TOnly
        {
                /// <summary>
                /// No flag - is taken as next animation
                /// </summary>
            NONE        = 0x7F,
                /// <summary>
                /// Only taskbar - next animation will be executed only if pet is on the taskbar
                /// </summary>
            TASKBAR     = 0x01,
                /// <summary>
                /// Only window - next animation will be executed only if pet is on a window
                /// </summary>
            WINDOW      = 0x02,
                /// <summary>
                /// Only horizontal screen borders - next animation will be executed only if pet is on the top or bottom
                /// </summary>
            HORIZONTAL  = 0x04,
                /// <summary>
                /// Horizontal or Window borders - net animation will be executed only if pet detected an horizontal border
                /// </summary>
            HORIZONTAL_ = 0x06,
                /// <summary>
                /// Vertical screen borders - next animation will be executed only if pet is on the left or right screen border
                /// </summary>
            VERTICAL    = 0x08,
                /// <summary>
                /// Left edge of a window - the pet reached the left side of the window it is standing on.
                /// </summary>
            WINDOW_LEFT = 0x10,
                /// <summary>
                /// Right edge of a window - the pet reached the right side of the window it is standing on.
                /// </summary>
            WINDOW_RIGHT = 0x20,
                /// <summary>
                /// Top edge of a window - the pet landed on the top of a window.
                /// </summary>
            WINDOW_TOP  = 0x40,
                /// <summary>
                /// Underside of a window - a rising pet's head reached the bottom edge of one.
                /// </summary>
            WINDOW_BOTTOM = 0x80,
        }
            // The three WINDOW_* values above are DISCRIMINATORS, not replacements. The host raises them
            // alongside WINDOW (e.g. WINDOW | WINDOW_LEFT), and the match is a bitwise AND, so an animation
            // that asks for the old generic `only="window"` still fires on every one of them. That is what
            // keeps the hand-authored pets -- which carry 955 window edges between them and were written
            // when "on a window" was the only thing that could be said -- behaving exactly as before.
            //
            // Only an animation that asks for `only="window-left"` narrows itself, because its value carries
            // no plain WINDOW bit and therefore matches no other site.
            //
            // WINDOW_BOTTOM at 0x80 is the first value to sit OUTSIDE the 0x7F that NONE happens to equal.
            // That does not currently matter, and the claim that it does was written here and then
            // negative-tested away: every site raises its discriminator alongside plain WINDOW (0x02), so
            // `NONE & where` still finds a bit and an unconditional edge matches by mask either way. The
            // short-circuit in Eligible is therefore DEFENSIVE, not load-bearing -- it is what would keep
            // unconditional edges working if a future situation were ever raised without the WINDOW bit.

            /// <summary>
            /// Whether an edge declaring <paramref name="only"/> may be taken in situation
            /// <paramref name="where"/>. Pure, and the single definition: the weighting loop below walks the
            /// candidate list TWICE (once to total the weights, once to pick), and the two passes disagreeing
            /// would pick an animation whose weight was never counted.
            /// </summary>
        public static bool Eligible(TOnly only, TOnly where)
        {
            if (only == TOnly.NONE) return true;      // "no flag" is taken everywhere
            return (only & where) != 0;
        }
            /// <summary>
            /// ID of the next animation to play
            /// </summary>
        public int ID;
            /// <summary>
            /// Probability the next animation will be executed:
            /// If there are 3 Next statements wit probability 5,12,3 probabilities are: 25%, 60% and 15%
            /// </summary>
        public int Probability;
            /// <summary>
            /// One of the values of TOnly. Default: NONE
            /// </summary>
        public TOnly only;
            /// <summary>
            /// Initialisation of the Next structure
            /// </summary>
            /// <param name="id">ID of the next animation</param>
            /// <param name="probability">Probability the next animation will be executed</param>
            /// <param name="where">Where the pet must be if you want this animation to be executed</param>
        public TNextAnimation(int id, int probability, TOnly where) 
        { 
            ID = id; 
            Probability = probability;
            only = where;
        }
    }

        /// <summary>
        /// Each sequence contains a defined quantity of image frames. An animation is based on this sequence.
        /// </summary>
    public struct TSequence
    {
            /// <summary>
            /// How many times the frames should be repeated until next animation is started
            /// </summary>
        public TValue Repeat;
            /// <summary>
            /// If <see cref="Repeat"/> is more than 1, you can set from which frame the sequence should be repeated.
            /// It is a 0 index based value.
            /// </summary>
        public int RepeatFrom;
            /// <summary>
            /// Frames index list. Contains all frames to play.
            /// </summary>
        public List<int> Frames;
            /// <summary>
            /// Total steps in the animation. Because Repeat and RepeatFrom can change the number of frames, this value will be calculated at beginning to increase the performance.
            /// </summary>
        public int TotalSteps { get; set; }
            /// <summary>
            /// A defined string. It can contains one of the fallowing values:
            /// 'flip': will flip all images and mirror the x-values in the animations
            /// </summary>
        public string Action;

            /// <summary>
            /// Calculate the steps present in this sequence. 
            /// This is used to calculate the movements, opacity and offset if they are different from START to END.
            /// </summary>
            /// <returns>Number of steps in the sequence.</returns>
        public int CalculateTotalSteps(int screenIndex = -1)
        {
            int frameCount = Frames == null ? 0 : Frames.Count;
            return AnimationRuntimeLimits.CalculateTotalSteps(
                frameCount,
                RepeatFrom,
                Repeat.GetValue(screenIndex));
        }
    }

        /// <summary>
        /// Animation structure. This contains all information about an animation.
        /// </summary>
    public struct TAnimation
    {
        private readonly Xml evaluator;

            /// <summary>
            /// Movement values at beginning of the animation. Will be interpolated with the End structure.
            /// </summary>
        public TMovement Start;
            /// <summary>
            /// Movement values at the end of the animation. Will be interpolated with the Start structure.
            /// </summary>
        public TMovement End;
            /// <summary>
            /// Name of the animation. Used for debug purposes and to get the key animations
            /// </summary>
        public string Name;
            /// <summary>
            /// List of possible animations to execute, when this animation is over
            /// </summary>
        public List<TNextAnimation> EndAnimation;
            /// <summary>
            /// List of possible animations to execute, when the pet reach a border.
            /// </summary>
        public List<TNextAnimation> EndBorder;
            /// <summary>
            /// List of possible animations to execute, when the pet should fall.
            /// </summary>
        public List<TNextAnimation> EndGravity;
            /// <summary>
            /// Sequence of frames to play for this animation.
            /// </summary>
        public TSequence Sequence;
            /// <summary>
            /// If an animation for the gravity is set, the pet will fall if no window is detected.
            /// </summary>
        public bool Gravity;
            /// <summary>
            /// If an animation for the border is set, the pet will automatically jump to this animation if a border is detected.
            /// </summary>
        public bool Border;
            /// <summary>
            /// ID of the animation
            /// </summary>
        public int ID;

            /// <summary>
            /// Initialize the Animation structure
            /// </summary>
            /// <param name="name">name of the animation</param>
            /// <param name="id">ID of the animation</param>
        public TAnimation(string name, int id, Xml valueEvaluator = null)
        {
            evaluator = valueEvaluator;
            Start = new TMovement();
            End = new TMovement();
            Name = name;
            EndAnimation = new List<TNextAnimation>(8);
            EndBorder = new List<TNextAnimation>(8);
            EndGravity = new List<TNextAnimation>(8);
            Sequence = new TSequence
            {
                Frames = new List<int>(16)
            };
            Gravity = false;
            Border = false;
            ID = id;
        }
            /// <summary>
            /// Update the xml values to update them on multiscreen
            /// </summary>
            /// <param name="screenIndex">Set to screen id used for the calculation</param>
        public void UpdateValues(int screenIndex = -1)
        {
            Sequence.Repeat.Value =
                AnimationRuntimeLimits.ClampRepeat(Sequence.Repeat.GetRawValue(screenIndex));
            Sequence.TotalSteps = AnimationRuntimeLimits.CalculateTotalSteps(
                Sequence.Frames == null ? 0 : Sequence.Frames.Count,
                Sequence.RepeatFrom,
                Sequence.Repeat.Value);
            Start.Interval.Value =
                AnimationRuntimeLimits.ClampInterval(Start.Interval.GetRawValue(screenIndex));
            End.Interval.Value =
                AnimationRuntimeLimits.ClampInterval(End.Interval.GetRawValue(screenIndex));

            double scale = evaluator == null ? 1.0 : ScalePolicy.ClampFactorD(evaluator.ScaleFactorD);
            Start.X.Value = AnimationRuntimeLimits.ClampMovement(
                ScalePolicy.ScaleVelocity(Start.X.GetRawValue(screenIndex), scale));
            Start.Y.Value = AnimationRuntimeLimits.ClampMovement(
                ScalePolicy.ScaleVelocity(Start.Y.GetRawValue(screenIndex), scale));
            End.X.Value = AnimationRuntimeLimits.ClampMovement(
                ScalePolicy.ScaleVelocity(End.X.GetRawValue(screenIndex), scale));
            End.Y.Value = AnimationRuntimeLimits.ClampMovement(
                ScalePolicy.ScaleVelocity(End.Y.GetRawValue(screenIndex), scale));
            Start.OffsetY = AnimationRuntimeLimits.ClampMovement(
                ScalePolicy.ScaleD(Start.UnscaledOffsetY, scale));
            End.OffsetY = AnimationRuntimeLimits.ClampMovement(
                ScalePolicy.ScaleD(End.UnscaledOffsetY, scale));
        }
    }

        /// <summary>
        /// Spawn structure. Contains the info to start the first animation.
        /// </summary>
    public struct TSpawn
    {
            /// <summary>
            /// A start position for the pet on the screen
            /// </summary>
        public TMovement Start;
            /// <summary>
            /// Probability that this Spawn will be taken as start values.
            /// </summary>
        public int Probability;
            /// <summary>
            /// The next animation to play, once the position was set.
            /// </summary>
        public int Next;

            /// <summary>
            /// Initialisation of the Spawn structure
            /// </summary>
            /// <param name="probability">Probability that this will be the next spawn</param>
        public TSpawn(int probability)
        {
            Start = new TMovement();
            Probability = probability;
            Next = 1;
        }
    }

        /// <summary>
        /// Child structure. A second animation form can be started as child.
        /// </summary>
    public struct TChild
    {
            /// <summary>
            /// Position of the Child form.
            /// </summary>
        public TMovement Position;
            /// <summary>
            /// ID of the animation that should create this child.
            /// </summary>
        public int AnimationID;
            /// <summary>
            /// Next animation, once the child was created.
            /// </summary>
        public int Next;
    }

        /// <summary>
        /// Sound structure. A sound that can be played together with the animation. Since S2 (Sound
        /// module) this is a NAudio-free data holder: the base parses and carries the raw MP3 bytes, and
        /// the out-of-process-optional Sound module (if installed) decodes + plays them via
        /// <see cref="SoundSink"/>. The base no longer references NAudio.
        /// </summary>
    public sealed class TSound
    {
            /// <summary>
            /// ID of the animation this sound belongs to.
            /// </summary>
        public int AnimationID;
            /// <summary>
            /// Probability this sound will be played (in %).
            /// </summary>
        public int Probability;
            /// <summary>
            /// How many time the sound should be looped (1 = play 2 times).
            /// </summary>
        public int Loop;
            /// <summary>
            /// Raw MP3 bytes, handed to the Sound module for decode + playback.
            /// </summary>
        public byte[] Data;
    }

        /// <summary>
        /// Animations class. Contains all information about the animations of the pet.
        /// </summary>
    public sealed class Animations : IDisposable
    {   
            /// <summary>
            /// Each animation has a unique ID.
            /// </summary>
        public Dictionary<int, TAnimation> SheepAnimations;
            /// <summary>
            /// Each Spawn has a unique ID.
            /// </summary>
        public Dictionary<int, TSpawn> SheepSpawn;
            /// <summary>
            /// Each Child has a unique animation ID.
            /// </summary>
        public Dictionary<int, List<TChild>> SheepChild;
            /// <summary>
            /// Sound variants grouped by animation ID. At most one variant is selected when an
            /// animation starts.
            /// </summary>
        public Dictionary<int, List<TSound>> SheepSound;

            /// <summary>
            /// Host sink for animation-triggered sound: (petTypeId, animationId, mp3Bytes, loop). Set once by
            /// the running host (StartUp) to play the selected sound through the host-owned audio output.
            /// petTypeId identifies which pet TYPE fired the sound ("" = the active/default pet) so the host
            /// can honor a per-pet mute (B3). Null in headless/self-test contexts = silent. Static because
            /// there is a single audio output for the app and <see cref="Animations"/> instances are shared
            /// per pet-type.
            /// </summary>
        internal static Action<string, int, byte[], int> SoundSink;

            /// <summary>The pet-type id this Animations was staged for ("" = active/default). Set by StartUp
            /// at stage time and passed to <see cref="SoundSink"/> so the host can apply per-pet settings.</summary>
        internal string PetTypeId;

            /// <summary>
            /// Random used for the "random" key value in the xml.
            /// </summary>
        private readonly Random rand;
        /// <summary>
        /// Compatibility pointer used only by the legacy debug window. Runtime expression
        /// evaluation is instance-owned by <see cref="TValue"/> and <see cref="TAnimation"/>.
        /// </summary>
        public static Xml Xml { get; private set; }
        private readonly Xml instanceXml;
        private bool disposed;

        /// <summary>The effective scale after this pet's frame-size limit is applied.</summary>
        public int ScaleFactor { get { return instanceXml.ScaleFactor; } }
        /// <summary>The effective FRACTIONAL scale (may be below 1) used for movement.</summary>
        public double ScaleFactorD { get { return instanceXml.ScaleFactorD; } }
        
            /// <summary>
            /// Animation ID once the pet is being dragged (default: 1)
            /// </summary>
        public int AnimationDrag = 1;
            /// <summary>
            /// Animation ID for the falling animation, after the dragged pet was released (default: 1)
            /// </summary>
        public int AnimationFall = 1;
            /// <summary>
            /// Animation ID once the pet should be closed (default: -1) 
            /// </summary>
        public int AnimationKill = -1;
            /// <summary>
            /// Animation ID once the cancel button on the about box was pressed (default: 1)
            /// </summary>
        public int AnimationSync = 1;

            /// <summary>
            /// Constructor, initialize member variables
            /// </summary>
            /// <param name="xml">Xml document</param>
        public Animations(Xml xml)
        {
            SheepAnimations = new Dictionary<int, TAnimation>(64);  // Reserve space for 64 animations, more are added automatically
            SheepSpawn = new Dictionary<int, TSpawn>(8);            // Reserve space for 8 spawns
            SheepChild = new Dictionary<int, List<TChild>>(8);      // Reserve space for 8 child
            SheepSound = new Dictionary<int, List<TSound>>(8);      // Reserve space for 8 sound groups
            rand = new Random();
            instanceXml = xml ?? throw new ArgumentNullException("xml");
        }

        /// <summary>
        /// Make this animation set visible to the legacy debug window. This does not affect runtime
        /// expression evaluation, which remains bound to <see cref="instanceXml"/>.
        /// </summary>
        public void Activate()
        {
            if (disposed) throw new ObjectDisposedException("Animations");
            Xml = instanceXml;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            // TSound is now a plain data holder (raw MP3 bytes); nothing to dispose. The Sound module owns
            // any decoded/playback resources and disposes them on its own Shutdown.
            SheepSound.Clear();
            SheepChild.Clear();
            SheepSpawn.Clear();
            SheepAnimations.Clear();
            if (ReferenceEquals(Xml, instanceXml)) Xml = null;
        }

        /// <summary>
        /// Add another animation to the animations dictionary. Animations are defined in the XML.
        /// <seealso cref="AddSpawn(int, int)"/>
        /// <seealso cref="AddChild(int)"/>
        /// </summary>
        /// <param name="ID">Animation unique ID</param>
        /// <param name="name">Animation name</param>
        /// <returns>Structure item (so it is possible to fill all values)</returns>
        public TAnimation AddAnimation(int ID, string name)
        {
            try
            {
                SheepAnimations.Add(ID, new TAnimation(name, ID, instanceXml));
                StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info, "adding animation: " + name);
            }
            catch(Exception ex)
            {
                StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.error, "unable to add animation: " + ex.Message);
            }
            return SheepAnimations[ID];
        }

            /// <summary>
            /// After adding the animation and filling data, this function must be called to save values.
            /// </summary>
            /// <param name="animation">Structure of an animation.</param>
            /// <param name="ID">ID of the animation to save in.</param>
        public void SaveAnimation(TAnimation animation, int ID)
        {
            SheepAnimations[ID] = animation;
        }

            /// <summary>
            /// Add another spawn to the spawn dictionary. Spawns are defined in the XML.
            /// <seealso cref="AddAnimation(int, string)"/>
            /// <seealso cref="AddChild(int)"/>
            /// </summary>
            /// <param name="ID">Spawn unique ID.</param>
            /// <param name="probability">Probability this spawn will be taken.</param>
            /// <returns></returns>
        public TSpawn AddSpawn(int ID, int probability)
        {
            StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info, "adding spawn: " + ID.ToString());
            SheepSpawn.Add(ID, new TSpawn(probability));
            return SheepSpawn[ID];
        }

            /// <summary>
            /// After adding the spawn and filling data, this function must be called to save values.
            /// </summary>
            /// <param name="spawn">Filled structure.</param>
            /// <param name="ID">ID of the structure.</param>
        public void SaveSpawn(TSpawn spawn, int ID)
        {
            SheepSpawn[ID] = spawn;
        }

            /// <summary>
            /// Add another Child to the Child dictionary. Childs are defined in the XML.
            /// <seealso cref="AddAnimation(int, string)"/>
            /// <seealso cref="AddSpawn(int, int)"/>
            /// </summary>
            /// <param name="ID">Child unique ID.</param>
            /// <returns></returns>
        public TChild AddChild(int ID)
        {
            StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info, "adding child: ani." + ID.ToString());
			if (!SheepChild.ContainsKey(ID))  // does not contains childs
			{
				SheepChild.Add(ID, new List<TChild>(1));	
			}
			SheepChild[ID].Add(new TChild());
			return SheepChild[ID].Last();
		}

            /// <summary>
            /// After adding the Child and filling data, this function must be called to save values of the last child.
            /// </summary>
            /// <param name="child">Filled structure.</param>
            /// <param name="ID">ID of the structure.</param>
        public void SaveChild(TChild child, int ID)
        {
            SheepChild[ID][SheepChild[ID].Count-1] = child;
        }

        /// <summary>
        /// Add a sound to the sound dictionary. Sounds are defined in the XML.
        /// </summary>
        /// <param name="ID">Animation ID.</param>
        /// <param name="Probability">Probability this sound will be played with the animation sequence.</param>
        /// <param name="Loop">How many times the sound should be looped.</param>
        /// <param name="Base64">Base 64 string with the encoded mp3 file.</param>
        /// <returns></returns>
        public void AddSound(int ID, int Probability, int Loop, string Base64)
        {
            StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info, "adding sound (ani." + ID.ToString() + ")");

            try
            {
                if (Base64.IndexOf(";base64,") > 0)
                    Base64 = Base64.Substring(Base64.IndexOf(";base64,") + 8);

                byte[] data = Convert.FromBase64String(Base64);
                string error;
                if (!Mp3Format.LooksLikeMp3(data, out error))
                    throw new InvalidDataException(error);

                TSound sound = new TSound
                {
                    AnimationID = ID,
                    Probability = Math.Max(0, Math.Min(100, Probability)),
                    Loop = Math.Max(0, Math.Min(20, Loop)),
                    Data = data,
                };
                List<TSound> variants;
                if (!SheepSound.TryGetValue(ID, out variants))
                {
                    variants = new List<TSound>(1);
                    SheepSound.Add(ID, variants);
                }
                variants.Add(sound);
            }
            catch(Exception ex)
            {
                StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.error, "can't open sound:" + ex.Message);
            }
        }

        /// <summary>
        /// Calling this method, the next Spawn is returned.
        /// If more Spawns are defined, a random Spawn will be taken (based on the probability)
        /// </summary>
        /// <returns>Structure with the next Spawn values</returns>
        public TSpawn GetRandomSpawn()
        {
            long totalWeight = 0;
            // Calculate total probability using a wide accumulator. XML validation constrains the
            // input, but this public runtime API must also remain safe for programmatic callers.
            foreach (TSpawn spawn in SheepSpawn.Values)
            {
                totalWeight += Math.Max(0, spawn.Probability);
            }
            if (totalWeight <= 0)
            {
                StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.warning, "spawn probabilities total zero");
                return CreateFallbackSpawn();
            }
            long selectedWeight = NextWeight(totalWeight);

            long cumulativeWeight = 0;
            foreach (TSpawn spawn in SheepSpawn.Values)
            {
                cumulativeWeight += Math.Max(0, spawn.Probability);
                if (selectedWeight < cumulativeWeight)
                {
                    return spawn;
                }
            }

                // If no spawn was returned, return the first spawn in the dictionary
            if (SheepSpawn.Count > 0)
            {
                return SheepSpawn.First().Value;
            }
            else
            {
                return CreateFallbackSpawn();
            }
        }

        private TSpawn CreateFallbackSpawn()
        {
            TSpawn retSpawn = new TSpawn(100);
            retSpawn.Next = SheepAnimations.Count > 0 ? SheepAnimations.First().Key : 1;
            retSpawn.Start.X.Compute = "0";
            retSpawn.Start.X.Value = 0;
            retSpawn.Start.Y.Compute = "0";
            retSpawn.Start.Y.Value = 0;
            retSpawn.Start.Opacity = 1.0;
            retSpawn.Start.Interval.Compute = "1000";
            retSpawn.Start.Interval.Value = 1000;
            return retSpawn;
        }

            /// <summary>
            /// Get the structure of the animation.
            /// </summary>
            /// <param name="id">ID of the wanted animation.</param>
            /// <returns>Structure with all information about this animation.</returns>
        public TAnimation GetAnimation(int id)
        {
			if(!SheepAnimations.ContainsKey(id))
            {
				TAnimation tempAnimation = new TAnimation("NULL", 0, instanceXml);
                tempAnimation.Start.Interval.Value = 1000;
                tempAnimation.End.Interval.Value = 1000;
                tempAnimation.Sequence.Frames.Add(0);
                tempAnimation.Sequence.TotalSteps = 1;
                return tempAnimation;
            }
            return SheepAnimations[id];
        }

            /// <summary>
            /// Get the Childs connected to the Animation ID.
            /// </summary>
            /// <param name="id">ID of the Animation.</param>
            /// <returns>A list of childs structure of the current Animation.</returns>
        public List<TChild> GetAnimationChild(int id)
        {
            return SheepChild[id];
        }

            /// <summary>
            /// If the animation has a Child to play.
            /// </summary>
            /// <param name="id">ID of the Animation.</param>
            /// <returns>true if there is a Child to play. <see cref="GetAnimationChild(int)"/></returns>
        public bool HasAnimationChild(int id)
        {
            return SheepChild.ContainsKey(id);
        }

            /// <summary>
            /// Start the next animation once a border was detected.
            /// </summary>
            /// <param name="animationID">ID of the Animation.</param>
            /// <param name="where">Where the pet is "walking".</param>
            /// <returns>ID of the next animation to play. -1 if there is no animation.</returns>
        public int SetNextBorderAnimation(int animationID, TNextAnimation.TOnly where)
        {
            TNextAnimation.TOnly ignored;
            return SetNextBorderAnimation(animationID, where, out ignored);
        }

            /// <summary>
            /// Start the next animation once a border was detected, reporting WHICH condition the chosen edge
            /// declared.
            ///
            /// The caller needs this to tell an edge that opted into a behaviour from one that merely happens
            /// to fire in the same place. A pet asking for <c>only="window-left"</c> is asking to grip the side
            /// of the window; a pet asking for the old wildcard <c>only="window"</c> is not, and there are 955
            /// of those in the shipped pets. Inferring intent from the chosen animation's shape instead would
            /// have made every one of them a candidate.
            /// </summary>
        public int SetNextBorderAnimation(int animationID, TNextAnimation.TOnly where, out TNextAnimation.TOnly chosenOnly)
        {
            StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info, "border detected");
            return SetNextGeneralAnimation(SheepAnimations[animationID].EndBorder, where, out chosenOnly);
        }

            /// <summary>
            /// Start the next animation once the sequence was over.
            /// </summary>
            /// <param name="animationID">ID of the animation.</param>
            /// <param name="where">Where the pet is "walking"</param>
            /// <returns>ID of the next animation to play. -1 if there is no animation.</returns>
        public int SetNextSequenceAnimation(int animationID, TNextAnimation.TOnly where)
        {
            StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info, "animation is over");
            return SetNextGeneralAnimation(SheepAnimations[animationID].EndAnimation, where);
        }

            /// <summary>
            /// Start the next animation once the gravity was detected.
            /// </summary>
            /// <param name="animationID">ID of the animation.</param>
            /// <param name="where">Where the pet is "walking"</param>
            /// <returns>ID of the next animation to play. -1 if there is no animation.</returns>
        public int SetNextGravityAnimation(int animationID, TNextAnimation.TOnly where)
        {
            StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info, "gravity detected");
            return SetNextGeneralAnimation(SheepAnimations[animationID].EndGravity, where);
        }

            /// <summary>
            /// Set the next animation, once the last one was finished.
            /// </summary>
            /// <param name="list">List of animations that can be executed.</param>
            /// <param name="where">Where the pet is "walking"</param>
            /// <returns>ID of the next animation to play. -1 if there is no animation.</returns>
        private int SetNextGeneralAnimation(List<TNextAnimation> list, TNextAnimation.TOnly where)
        {
            TNextAnimation.TOnly ignored;
            return SetNextGeneralAnimation(list, where, out ignored);
        }

        private int SetNextGeneralAnimation(List<TNextAnimation> list, TNextAnimation.TOnly where, out TNextAnimation.TOnly chosenOnly)
        {
            int iDefaultID = -1;
            chosenOnly = TNextAnimation.TOnly.NONE;
            if (list.Count > 0)     // Find the next animation only if there is at least 1 animation in the list
            {
                long totalWeight = 0;
                foreach (TNextAnimation anim in list)
                {
                    if (!TNextAnimation.Eligible(anim.only, where)) continue;

                    totalWeight += Math.Max(0, anim.Probability);
                }
                if (totalWeight <= 0)
                {
                    StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.warning, "no eligible positive-probability transition");
                    return -1;
                }
                long selectedWeight = NextWeight(totalWeight);
                long cumulativeWeight = 0;
                foreach (TNextAnimation anim in list)
                {
                    if (!TNextAnimation.Eligible(anim.only, where)) continue;

                    cumulativeWeight += Math.Max(0, anim.Probability);
                    if (selectedWeight < cumulativeWeight)
                    {
                        iDefaultID = anim.ID;
                        chosenOnly = anim.only;
                        break;
                    }
                }
                    // If an animation was found, re-calculate the values (if there are some Random values, they must be evaluated again)
                if (iDefaultID > 0)
                {
                    UpdateAnimationValues(iDefaultID);
                    List<TSound> soundVariants;
                    if (SheepSound.TryGetValue(iDefaultID, out soundVariants))
                    {
                        TSound sound = SelectSoundForRoll(
                            soundVariants,
                            rand.Next(0, 100));
                        // Hand the selected sound to the host-owned audio output, tagged with this pet TYPE
                        // so the host can honor a per-pet mute. A null sink (headless/self-test) = silent.
                        if (sound != null && sound.Data != null)
                        {
                            Action<string, int, byte[], int> sink = SoundSink;
                            if (sink != null) sink(PetTypeId ?? "", iDefaultID, sound.Data, sound.Loop);
                        }
                    }
                }
                return iDefaultID;
            }
            else
            {
                StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.warning, "no next animation found");
                return -1;  // a new spawn is requested
            }
        }

        /// <summary>
        /// Selects at most one sound variant using each entry's percentage as a disjoint
        /// cumulative range. A single variant therefore retains the legacy roll behavior.
        /// </summary>
        internal static TSound SelectSoundForRoll(
            IList<TSound> variants,
            int roll)
        {
            if (variants == null || roll < 0 || roll >= 100) return null;
            int cumulativeProbability = 0;
            foreach (TSound variant in variants)
            {
                if (variant == null) continue;
                cumulativeProbability +=
                    Math.Max(0, Math.Min(100, variant.Probability));
                if (roll < cumulativeProbability) return variant;
                if (cumulativeProbability >= 100) break;
            }
            return null;
        }

        private long NextWeight(long exclusiveUpperBound)
        {
            if (exclusiveUpperBound <= 0)
                throw new ArgumentOutOfRangeException("exclusiveUpperBound");
            if (exclusiveUpperBound <= int.MaxValue)
                return rand.Next((int)exclusiveUpperBound);

            // Random.NextDouble has 53 bits of precision, far more than any weight total that can
            // fit in the bounded pet XML. Clamp defensively against a hypothetical rounded endpoint.
            long selected = (long)(rand.NextDouble() * exclusiveUpperBound);
            return selected >= exclusiveUpperBound ? exclusiveUpperBound - 1 : selected;
        }

            /// <summary>
            /// Update the values of the animation.<br />
            /// If "random" was used, on each start of a new animation this will change so the expression must be evaluated again.<br />
            /// Total steps are also calculated, so it has a better performance by playing it.
            /// </summary>
            /// <param name="id">ID of the Animation.</param>
        private void UpdateAnimationValues(int id)
        {
            TAnimation ani = SheepAnimations[id];
            ani.UpdateValues();
            SheepAnimations[id] = ani;
			
            StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info, "new animation: " + ani.Name + " (" + ani.ID + ")");
        }

            /// <summary>
            /// Get a list of animations, based on the flags forwarded by the parameters.
            /// </summary>
            /// <param name="currentID">The base animation to find the next animations.</param>
            /// <param name="includeNext">Include all animations after the sequence is over.</param>
            /// <param name="includeBorder">Include all animations if the pet detected a border.</param>
            /// <param name="includeGravity">Include all animations if the pet detected a gravity.</param>
            /// <returns></returns>
        public List<TNextAnimation> GetNextAnimations(int currentID, bool includeNext, bool includeBorder, bool includeGravity)
        {
            List<TNextAnimation> list = new List<TNextAnimation>();

            if (includeNext)
                list.AddRange(SheepAnimations[currentID].EndAnimation);
            if (includeBorder)
                list.AddRange(SheepAnimations[currentID].EndBorder);
            if (includeGravity)
                list.AddRange(SheepAnimations[currentID].EndGravity);

            return list;
        }

            /// <summary>
            /// Get a list of <see cref="TSpawn"/> structures. Defines the start position of the pet.
            /// </summary>
            /// <returns>List of TSpawn structures.</returns>
            /// <remarks>Once the animation is over or at begins, one of the spawns will be used to place the pet.</remarks>
        public List<TSpawn> GetNextSpawns()
        {
            List<TSpawn> list = new List<TSpawn>();

            for(int i = 0; i < SheepSpawn.Keys.Count; i++)
            {
                list.Add(SheepSpawn[SheepSpawn.Keys.ElementAt(i)]);
            }
            return list;
        }
    }
}
