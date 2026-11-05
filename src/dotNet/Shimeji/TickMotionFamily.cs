using System.Collections.Generic;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// The motion family of embedded classes (grimoire/06 section 5): Fall (and Thrown, which runs Fall), Jump, Dragged,
    /// Regist, Turn, MoveWithTurn, the deprecated Broadcast aliases, and Breed's moving variants.
    /// </summary>
    internal static class TickMotionFamily
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
