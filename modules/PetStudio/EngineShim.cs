using System.IO;

namespace DesktopAICompanion
{
    // The host ambient surface the source-linked engine files expect, supplied here so they compile outside
    // the host. Deliberately inert: this module validates and previews, it never persists settings, plays
    // audio, or logs into the host's debug window. Same approach the retired Tools\PetTester used, kept
    // small on purpose -- every member below exists because a linked file references it, and nothing else.
    //
    // Namespace DesktopAICompanion (not the module's own) so the linked sources resolve these by simple name exactly
    // as they do inside the host.

    /// <summary>Stands in for the host's debug log. Companion Studio surfaces problems in its own report pane, so
    /// engine chatter is dropped rather than shown.</summary>
    internal static class StartUp
    {
        internal enum DEBUG_TYPE
        {
            info = 1,
            warning = 2,
            error = 3,
        }

        internal static void AddDebugInfo(DEBUG_TYPE type, string text)
        {
        }
    }

    /// <summary>Forwards to the real WinForms screen list; the engine reads it for geometry.</summary>
    internal static class Screen
    {
        internal static System.Windows.Forms.Screen PrimaryScreen
        {
            get { return System.Windows.Forms.Screen.PrimaryScreen; }
        }

        internal static System.Windows.Forms.Screen[] AllScreens
        {
            get { return System.Windows.Forms.Screen.AllScreens; }
        }
    }

    /// <summary>
    /// The two resources the validator reads. `animations1` is the XSD it validates against, embedded in
    /// this module so the schema travels with the parser that uses it. `animations` is the host's built-in
    /// pet, which nothing on the validation path needs.
    /// </summary>
    internal static class Properties
    {
        internal static class Resources
        {
            internal static string animations { get { return ""; } }

            internal static string animations1
            {
                get
                {
                    using (Stream stream = typeof(Properties).Assembly
                        .GetManifestResourceStream("DesktopAICompanion.PetStudio.animations.xsd"))
                    {
                        if (stream == null) return "";
                        using (var reader = new StreamReader(stream))
                            return reader.ReadToEnd();
                    }
                }
            }
        }
    }
}
