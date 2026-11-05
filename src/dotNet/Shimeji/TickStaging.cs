using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// The tick type a staged tick-clock companion runs, kept beside its <see cref="Xml"/>. A side table rather than a
    /// field because Studio compiles Xml.cs without the runtime. Weak, so the entry goes when the staged Xml does.
    /// </summary>
    internal static class TickStaging
    {
        private static readonly ConditionalWeakTable<Xml, TickType> Types = new ConditionalWeakTable<Xml, TickType>();

        /// <summary>
        /// Compile the tick type of a staged tick-clock companion and scale it to the size its frames were staged at.
        /// Xml.TryReadXml blanks the sheet's base64 once the frames are built, and the type sizes its cells from the
        /// PNG header, so this reads the document's own text again (Xml.AnimationXMLString is kept intact). That text
        /// already passed the validator once in TryReadXml; the second parse costs one more validation per staging,
        /// never per tick.
        /// </summary>
        public static bool TryStage(Xml xml, out string error)
        {
            error = null;
            try
            {
                XmlData.RootNode root;
                byte[] sheet, icon;
                byte[][] sounds;
                if (!CompanionXmlValidator.TryParse(xml.AnimationXMLString, out root, out sheet, out icon, out sounds, out error)) return false;
                TickType type = TickType.Compile("", root);
                type.ApplyScale(xml.ScaleFactorD, xml.spriteWidth, xml.spriteHeight);
                Types.Add(xml, type);
                Clips.Add(xml, ClipsByName(root, sounds));
                return true;
            }
            catch (Exception ex)
            {
                error = "the tick companion could not be compiled: " + ex.Message;
                return false;
            }
        }

        /// <summary>The tick type staged with this Xml, or null for a classic companion.</summary>
        public static TickType Of(Xml xml)
        {
            TickType type;
            return xml != null && Types.TryGetValue(xml, out type) ? type : null;
        }

        /// <summary>
        /// A staged tick companion's named clips, by name: the bytes the validator decoded and proved MP3 in the second
        /// parse above (TryReadXml blanks every Sound.Base64 in the Xml's own graph, so this is where they survive). The
        /// source loads every clip with its configuration (AnimationBuilder.java:147-166); so does this. Null for a
        /// classic companion.
        /// </summary>
        public static IReadOnlyDictionary<string, byte[]> ClipsOf(Xml xml)
        {
            Dictionary<string, byte[]> clips;
            return xml != null && Clips.TryGetValue(xml, out clips) ? clips : null;
        }

        private static readonly ConditionalWeakTable<Xml, Dictionary<string, byte[]>> Clips =
            new ConditionalWeakTable<Xml, Dictionary<string, byte[]>>();

        /// <summary>Each named sound's decoded bytes, matched to the document's sounds by position (the validator hands
        /// them back in document order).</summary>
        private static Dictionary<string, byte[]> ClipsByName(XmlData.RootNode root, byte[][] sounds)
        {
            var clips = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (root.Sounds == null || root.Sounds.Sound == null || sounds == null) return clips;
            for (int i = 0; i < root.Sounds.Sound.Length && i < sounds.Length; i++)
            {
                XmlData.SoundNode node = root.Sounds.Sound[i];
                if (node != null && node.SoundId != null && sounds[i] != null) clips[node.SoundId] = sounds[i];
            }
            return clips;
        }
    }
}
