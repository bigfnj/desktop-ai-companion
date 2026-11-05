using System.Collections.Generic;

namespace DesktopAICompanion
{
    /// <summary>
    /// Companion Studio's half of <see cref="CompanionFormatFeatures"/> (the app's is src/dotNet/Shimeji/TickFeatures.cs,
    /// which Studio does not compile and must not: it calls into the tick runtime). Studio analyses, previews and installs
    /// a tick-clock companion, so it accepts the tokens the app of this release runs, the same list as the app's file;
    /// --petstudio-selftest compares the two sets and fails when they differ.
    ///
    /// The per-class hook is deliberately NOT implemented, so <see cref="CompanionFormatFeatures.RunsClass"/> keeps the
    /// format's answer (every class in the closed list runs). Studio has no runtime to ask, and it does not need one:
    /// the HOST re-validates the document with its own class gate (TickWorld.RunsEmbeddedClass) when Studio previews it
    /// (ICompanionManager.SpawnPreview) and when it installs it (ICompanionManager.InstallType), so a class the app does
    /// not run is refused there, by name, before anything runs or lands on disk.
    ///
    /// Safe because every Studio path that re-serialises a parsed document either keeps every field or refuses a tick
    /// document first (BehaviourChain.BuildDebugXml refuses one; Preview, Install and Save pass the editor's text through
    /// untouched). The converter (tools/ShimejiConvert*) compiles the validator without this file and keeps its empty set.
    /// </summary>
    internal static partial class CompanionFormatFeatures
    {
        static partial void AddRuntimeFeatures(List<string> supported)
        {
            supported.Add(Tick);
            supported.Add(Actions);
            supported.Add(Behaviours);
            supported.Add(Expressions);
            supported.Add(Lifecycle);
            // A single pack member opens, previews (in its own preview scope) and installs like any companion; its
            // references to other members are reported by the analysis. A whole pack is imported and installed through
            // PackImport.cs (1.3.0).
            supported.Add(Packs);
            supported.Add(Social);
            supported.Add(Windows);
            supported.Add(Hotspots);
            supported.Add(Variables);
            // A companion with WAV clips (feature 'wav') opens, previews and installs like any other: Studio plays no
            // sound itself (nothing in modules/PetStudio decodes or plays audio), and the host decodes the clips when its
            // preview or installed companion plays them. Missing here, Studio refused every WAV companion by this name
            // and --petstudio-selftest's set comparison failed (the WAV lane's stand-in review, F2).
            supported.Add(Wav);
        }
    }
}
