using System;
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
                if (!CompanionXmlValidator.TryParse(xml.AnimationXMLString, out root, out error)) return false;
                TickType type = TickType.Compile("", root);
                type.ApplyScale(xml.ScaleFactorD, xml.spriteWidth, xml.spriteHeight);
                Types.Add(xml, type);
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
    }
}
