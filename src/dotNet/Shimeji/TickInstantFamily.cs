using System.Collections.Generic;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// The instant and one-shot embedded classes (grimoire/06 section 5): Look, Offset, Mute, BringToFront, SelfDestruct.
    /// </summary>
    internal static class TickInstantFamily
    {
        public static TickAction TryCreate(TickWorld world, TickType type, TickActDef def, Dictionary<string, TickParamDef> incoming)
        {
            switch (def.Class)
            {
                default: return null;
            }
        }
    }
}
