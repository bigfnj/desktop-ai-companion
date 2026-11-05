using System.Collections.Generic;

namespace DesktopAICompanion
{
    /// <summary>
    /// The app's half of <see cref="CompanionFormatFeatures"/>: what the tick runtime compiled into this build runs.
    /// Only the app compiles this file; Studio and the converter compile the validator without it, so their supported
    /// set stays empty (see the note on the class).
    /// </summary>
    internal static partial class CompanionFormatFeatures
    {
        /// <summary>A token is listed once every class and construct it gates runs here. 'packs' joins when installs
        /// resolve members. 'windows' runs its classes with the per-type window switch OFF until the Pets pane offers it:
        /// the animation plays and no window moves (policy:window-off-plays).</summary>
        static partial void AddRuntimeFeatures(List<string> supported)
        {
            supported.Add(Tick);
            supported.Add(Actions);
            supported.Add(Behaviours);
            supported.Add(Expressions);
            supported.Add(Lifecycle);
            supported.Add(Social);
            supported.Add(Windows);
            supported.Add(Hotspots);
            supported.Add(Variables);
        }

        static partial void CheckRuntimeClass(string embeddedClass, ref bool runs)
        {
            runs = Shimeji.TickWorld.RunsEmbeddedClass(embeddedClass);
        }
    }
}
