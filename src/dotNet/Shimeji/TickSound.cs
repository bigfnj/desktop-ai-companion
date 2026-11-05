using System;
using System.Collections.Generic;
using System.Globalization;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// One clip of the source's shared sound cache (Sounds.java): a sound a pose names, at the pose's volume. The source
    /// loads one Clip per sound path and volume (SoundLoader.java:16-38, keyed by path + volume), so every companion whose
    /// pose names the same sound at the same volume shares that one clip, and the same sound at another volume is another
    /// clip. The image set stands for the path's folder: here a type's clips are its own (grimoire/06 section 3).
    /// </summary>
    internal sealed class TickClip : IEquatable<TickClip>
    {
        public readonly string ImageSet;
        public readonly string Name;
        public readonly float Volume;

        public TickClip(string imageSet, string name, float volume)
        {
            ImageSet = imageSet ?? "";
            Name = name ?? "";
            Volume = volume;
        }

        public bool Equals(TickClip other)
        {
            return other != null && string.Equals(ImageSet, other.ImageSet, StringComparison.Ordinal)
                && string.Equals(Name, other.Name, StringComparison.Ordinal) && Volume.Equals(other.Volume);
        }

        public override bool Equals(object obj) { return Equals(obj as TickClip); }

        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(ImageSet) * 31 + StringComparer.Ordinal.GetHashCode(Name) * 7 + Volume.GetHashCode();
        }

        /// <summary>imageSet/name@volume, the volume round-trippable.</summary>
        public override string ToString()
        {
            return ImageSet + "/" + Name + "@" + Volume.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// The audio side of the clip cache: whether a clip is playing (Clip.isRunning), and starting or stopping it. The app
    /// routes these to its audio output (TickHost); a world without one (a parity run, a test that plays nothing) has no
    /// clip loaded, so nothing is started there.
    /// </summary>
    internal interface ITickClips
    {
        bool IsRunning(TickClip clip);

        /// <summary>Clip.stop, setMicrosecondPosition(0), start: the clip plays once from its beginning.</summary>
        void Start(TickClip clip);

        void Stop(TickClip clip);
    }

    /// <summary>
    /// The source's sound rules (Mascot.java:612-625, Mute.java:32-56, Sounds.java:37-63), decided here and carried out by
    /// <see cref="TickWorld.Clips"/>. Every start and every stop of a running clip is reported to the observer (CONTRACT
    /// event <c>sound</c>), from the decision itself.
    /// </summary>
    internal static class TickSound
    {
        /// <summary>The gain control's floor: Java's linearToDB(0), which reads 0 as 0.0001 (-80 dB).</summary>
        public const float MinimumDecibels = -80f;

        /// <summary>The gain control's ceiling: linearToDB(2), a factor of two (+6.02 dB).</summary>
        public static readonly float MaximumDecibels = (float)(Math.Log10(2.0) * 20.0);

        /// <summary>
        /// Mascot.apply's sound, after the image: when this companion's sounds are on and its pose names a clip, the clip
        /// restarts from the beginning unless it is still running. The clip is SHARED, so a companion whose clip another
        /// companion is playing does not start it again; and the check is made at every apply, so a clip that finishes
        /// while the pose still names it plays again at the next tick. A clip is never stopped by a pose change, nor by
        /// the companion's disposal (Mascot.dispose leaves it): only Mute stops one. The sound and its volume are the
        /// APPLIED pose's, so right after a Transform the old type's clip plays (its pose is still the one shown), while
        /// the Sounds setting is read for the companion's current type, as the source reads it by the current image set.
        /// </summary>
        public static void Apply(TickWorld world, TickMascot mascot)
        {
            ITickClips clips = world.Clips;
            if (clips == null || mascot.Sound == null) return;
            if (!world.Settings.Get("Sounds", mascot.Type.Name)) return;
            var clip = new TickClip((mascot.PoseType ?? mascot.Type).Name, mascot.Sound, mascot.Pose.Volume);
            if (clips.IsRunning(clip)) return;
            clips.Start(clip);
            world.Observer.Sound(mascot, clip.Name, "start");
        }

        /// <summary>
        /// Mute with a Sound (Mute.java:35-47): every RUNNING clip of the initiator's image set whose name starts with the
        /// Sound stops, at any volume (getSoundsIgnoringVolume matches by prefix, Sounds.java:37-50), wherever it plays,
        /// another companion's included, because the clip is shared. A clip that is not running is left alone.
        /// </summary>
        public static void MuteNamed(TickWorld world, TickMascot mascot, string sound)
        {
            ITickClips clips = world.Clips;
            if (clips == null) return;
            foreach (TickClip clip in Cached(world))
                if (string.Equals(clip.ImageSet, mascot.Type.Name, StringComparison.Ordinal)
                    && clip.Name.StartsWith(sound, StringComparison.Ordinal) && clips.IsRunning(clip))
                {
                    clips.Stop(clip);
                    world.Observer.Sound(mascot, clip.Name, "stop");
                }
        }

        /// <summary>Mute with no Sound, once the caller has found the initiator's sounds on: Sounds.setMuted(true) stops
        /// every cached clip, of every image set (Sounds.java:52-63). Only a clip that was running is reported.</summary>
        public static void MuteAll(TickWorld world, TickMascot mascot)
        {
            ITickClips clips = world.Clips;
            if (clips == null) return;
            foreach (TickClip clip in Cached(world))
            {
                bool running = clips.IsRunning(clip);
                clips.Stop(clip);
                if (running) world.Observer.Sound(mascot, clip.Name, "stop");
            }
        }

        /// <summary>
        /// The cache: every clip a pose of a type in the world names, at that pose's volume (the source loads each one with
        /// its configuration, AnimationBuilder.java:147-166). Sorted, so stops are reported in a stable order (the
        /// source's ConcurrentHashMap has none).
        /// </summary>
        public static List<TickClip> Cached(TickWorld world)
        {
            var seen = new HashSet<TickClip>();
            var list = new List<TickClip>();
            foreach (TickType type in world.Types)
                foreach (TickActDef act in type.Actions.Values)
                    foreach (TickVariantDef variant in act.Variants)
                        foreach (TickPoseDef pose in variant.Poses)
                        {
                            if (pose.Sound == null) continue;
                            var clip = new TickClip(type.Name, pose.Sound, pose.Volume);
                            if (seen.Add(clip)) list.Add(clip);
                        }
            list.Sort(delegate (TickClip a, TickClip b)
            {
                int c = string.CompareOrdinal(a.ImageSet, b.ImageSet);
                if (c == 0) c = string.CompareOrdinal(a.Name, b.Name);
                return c != 0 ? c : a.Volume.CompareTo(b.Volume);
            });
            return list;
        }

        /// <summary>
        /// The pose's Volume is the clip's MASTER_GAIN in decibels (SoundLoader.java:24), which Java applies as a linear
        /// factor of 10^(dB/20) (dBToLinear): 0 dB plays the clip as recorded, -6 dB at about half, +6.02 dB at twice. The
        /// control's range is -80 to +6.02 dB, and the source refuses a Volume outside it (setValue throws, so the image
        /// set fails to load); the host clamps it to the range instead (a recorded divergence).
        /// </summary>
        public static double LinearGain(float decibels)
        {
            float db = decibels < MinimumDecibels ? MinimumDecibels : decibels > MaximumDecibels ? MaximumDecibels : decibels;
            return Math.Pow(10.0, db / 20.0);
        }
    }
}
