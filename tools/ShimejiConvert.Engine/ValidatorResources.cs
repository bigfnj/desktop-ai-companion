using System;
using System.IO;
using System.Reflection;

namespace DesktopAICompanion.Properties
{
    /// <summary>
    /// The one external dependency CompanionXmlValidator.cs has on the app: it reads the pet XSD from the
    /// generated ResX accessor <c>Properties.Resources.animations1</c>, which exists only in
    /// DesktopAICompanion_Portable. Rather than edit the validator (which would let the engine's copy of the rules
    /// drift from the app's), this satisfies that one member from an embedded copy of the same
    /// src/Resources/animations.xsd the app ships. The member name matches the generated accessor
    /// deliberately -- it is an ABI to a generated file, not a style choice.
    /// </summary>
    internal static class Resources
    {
        private const string SchemaResourceName = "ShimejiConvert.animations.xsd";

        private static string _schema;

        internal static string animations1
        {
            get
            {
                if (_schema != null) return _schema;
                Assembly assembly = typeof(Resources).Assembly;
                using (Stream stream = assembly.GetManifestResourceStream(SchemaResourceName))
                {
                    if (stream == null)
                    {
                        throw new InvalidOperationException(
                            "Embedded schema '" + SchemaResourceName + "' is missing. The build should have " +
                            "embedded src/Resources/animations.xsd; a validator that silently falls back to " +
                            "no schema would report every malformed pet as valid.");
                    }

                    using (var reader = new StreamReader(stream))
                        _schema = reader.ReadToEnd();
                }

                return _schema;
            }
        }
    }
}

namespace DesktopAICompanion
{
    /// <summary>
    /// The second and last thing CompanionXmlValidator.cs needs from the app: one compile-time constant. The real
    /// SpriteFrameStore is a runtime bitmap cache in src/dotNet/Xml.cs, and including that file would drag
    /// the whole animation runtime (TNextAnimation, Animations, the pet form) into an offline converter.
    ///
    /// A shim can drift from the value it mirrors, so it is NOT left on trust: the AssertSpriteFrameLimit
    /// target in ShimejiConvert.Engine.csproj fails the BUILD if src/dotNet/Xml.cs stops declaring 1024. If
    /// that error fires, change the literal here to match -- do not relax the check.
    /// </summary>
    internal static class SpriteFrameStore
    {
        internal const int MaximumFrames = 1024;
    }
}
