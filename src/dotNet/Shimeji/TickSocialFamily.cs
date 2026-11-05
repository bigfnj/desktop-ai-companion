using System.Collections.Generic;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// The social embedded classes (grimoire/06 sections 5 and 7): ScanMove, ScanJump, ScanInteract, Interact,
    /// ComplexMove, ComplexJump.
    /// </summary>
    internal static class TickSocialFamily
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
