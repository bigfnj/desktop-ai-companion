using System;
using System.Globalization;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// Where the tick host's clips play: the app gives the <see cref="TickHost"/> one (StartUp: <see cref="AppTickAudio"/>,
    /// over its AudioOutput), so the engine never holds an app type. One owner per clip.
    /// </summary>
    internal interface ITickAudio
    {
        /// <summary>Play <paramref name="mp3"/> once under <paramref name="owner"/> at the pose's linear
        /// <paramref name="gain"/>. False when nothing will be heard.</summary>
        bool Play(string owner, byte[] mp3, double gain);

        bool IsPlaying(string owner);

        bool Stop(string owner);
    }

    /// <summary>
    /// The engine's clip cache (<see cref="ITickClips"/>) on the host's audio: a clip is the type's staged bytes by name,
    /// played under an owner of its own (type, name and volume, so every companion naming it shares it, as the source's
    /// cache does), and "running" is whether that owner is still playing.
    /// </summary>
    internal sealed class HostTickClips : ITickClips
    {
        private readonly Func<string, TickHostType> _typeOf;
        private readonly Func<ITickAudio> _audio;

        public HostTickClips(Func<string, TickHostType> typeOf, Func<ITickAudio> audio)
        {
            _typeOf = typeOf;
            _audio = audio;
        }

        /// <summary>The audio owner of one clip: the AudioOutput clip prefix, then type|name|volume. A type name is a pet
        /// id (never '|'), so the owner reads back unambiguously.</summary>
        public static string OwnerOf(TickClip clip)
        {
            return AudioOutput.ClipOwnerPrefix + clip.ImageSet + "|" + clip.Name + "|"
                + clip.Volume.ToString("R", CultureInfo.InvariantCulture);
        }

        public bool IsRunning(TickClip clip)
        {
            ITickAudio audio = _audio();
            return audio != null && audio.IsPlaying(OwnerOf(clip));
        }

        public void Start(TickClip clip)
        {
            ITickAudio audio = _audio();
            TickHostType type = _typeOf(clip.ImageSet);
            byte[] mp3;
            if (audio == null || type == null || type.Clips == null || !type.Clips.TryGetValue(clip.Name, out mp3)) return;
            audio.Play(OwnerOf(clip), mp3, TickSound.LinearGain(clip.Volume));
        }

        public void Stop(TickClip clip)
        {
            ITickAudio audio = _audio();
            if (audio != null) audio.Stop(OwnerOf(clip));
        }
    }

    /// <summary>
    /// A tick companion's clips through the app's audio output, under the app's rules for a companion's own sounds. The
    /// global companion-sound switch and the type's own mute are the source's Sounds setting, which the engine reads
    /// before it starts a clip (HostTickSettings); here the fullscreen hold and the user's volume apply, as they do to a
    /// classic companion's sounds (StartUp's Animations.SoundSink). A held clip is not queued: the engine asks again at
    /// the next apply while its pose still names it.
    /// </summary>
    internal sealed class AppTickAudio : ITickAudio
    {
        private readonly Func<AudioOutput> _output;
        private readonly Func<bool> _held;

        public AppTickAudio(Func<AudioOutput> output, Func<bool> held)
        {
            _output = output;
            _held = held;
        }

        public bool Play(string owner, byte[] mp3, double gain)
        {
            AudioOutput output = _output();
            double volume = Volume(Program.MyData, _held(), gain);
            return output != null && volume > 0.0 && output.PlayClip(owner, mp3, volume);
        }

        public bool IsPlaying(string owner)
        {
            AudioOutput output = _output();
            return output != null && output.IsPlaying(owner);
        }

        public bool Stop(string owner)
        {
            AudioOutput output = _output();
            return output != null && output.StopOwned(owner);
        }

        /// <summary>The level a clip plays at: the user's volume times the pose's gain, capped at full scale (so a pose
        /// above 0 dB plays louder than the user's level, as the source's gain is relative to its output, but never
        /// past full scale); 0, so not played, with no settings or while sounds are held for a fullscreen window.</summary>
        internal static double Volume(LocalData data, bool held, double gain)
        {
            if (data == null || held) return 0.0;
            return Math.Min(1.0, data.GetVolume() * gain);
        }
    }
}
