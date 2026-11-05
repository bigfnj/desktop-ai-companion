using System.Collections.Generic;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// The window-moving embedded classes (grimoire/06 sections 5 and 8), opt-in per type and off by default:
    /// WalkWithIE, FallWithIE, JumpWithIE, ThrowIE, MinimizeIE.
    /// </summary>
    internal static class TickWindowFamily
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
