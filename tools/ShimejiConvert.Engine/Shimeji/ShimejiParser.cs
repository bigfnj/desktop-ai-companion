using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// Reads a Shimeji conf directory (actions.xml + behaviors.xml) into a <see cref="ShimejiConfig"/>.
    ///
    /// Deliberately TOLERANT and namespace-blind. Two traps this handles (see MAPPING.md):
    ///   * The vendor's own Mascot.xsd restricts Type to six values, but the vendor's shipped actions.xml uses
    ///     nine (Sequence/Floor/Stay/Animate/Wall/Ceiling on top of the schema's set). Validating input
    ///     against Mascot.xsd would reject the reference skin, so we drive off observed values and never
    ///     schema-validate the input.
    ///   * Elements are in the http://www.group-finity.com/Mascot namespace; we match on LocalName so a skin
    ///     that omits or renames the namespace still parses.
    /// </summary>
    public static class ShimejiParser
    {
        public const int MaximumSourceDepth = 256;
        public const int MaximumSourceCharacters = 16 * 1024 * 1024;
        public const int MaximumSourceMaterializationCharacters = 8 * 1024 * 1024;
        /// <summary>
        /// The leaf names a conf directory may give its ACTIONS file, in the reference loader's own order of
        /// precedence (Shimeji-Desktop's Main.java tries actions.xml, 動作.xml, one.xml, 1.xml). The original
        /// group-finity Shimeji shipped conf/動作.xml + conf/行動.xml; Shimeji-EE renamed them and its wiki tells
        /// users to rename by hand. This engine went to real effort to read the Japanese and British XML
        /// CONTENT (the alias table below, VocabSelfTest) and then keyed both directory entry points on the
        /// one English leaf, so a skin with conf/動作.xml was reported as having "no behaviour config of its
        /// own" and converted against the bundled base conf -- 74 of the 2779 archives in the maintainer's
        /// corpus -- while 21 more kept actions.xml beside a Behavior.xml (singular) and lost every frequency
        /// in silence, the F442 symptom on the user-conf path F442 left alone (RA-385). ONE declaration, read
        /// by <see cref="ParseConfDirectory"/>, <see cref="SkinLayout"/> and the CLI's corpus census, so the
        /// three cannot drift again. English first, so no existing conf changes meaning when a directory
        /// carries both spellings.
        /// </summary>
        public static readonly string[] ActionsFileNames = { "actions.xml", "動作.xml", "one.xml", "1.xml" };

        /// <summary>The leaf names of the BEHAVIOURS file, same order and reason as <see cref="ActionsFileNames"/>
        /// (behaviors.xml, behavior.xml, 行動.xml, two.xml, 2.xml in the reference loader; behaviours.xml is the
        /// British spelling the alias table already reads inside the file).</summary>
        public static readonly string[] BehaviorsFileNames =
            { "behaviors.xml", "behavior.xml", "behaviours.xml", "行動.xml", "two.xml", "2.xml" };

        /// <summary>
        /// Parse the actions and behaviours files found under <paramref name="confDir"/>, under any of the
        /// accepted leaf names. The actions file is required; the behaviours file is optional (a skin may ship
        /// only actions), and which one was read -- or that none was -- travels out on
        /// <see cref="ShimejiConfig.BehaviorsFile"/>. Throws if no actions file is present or it is malformed.
        /// </summary>
        public static ShimejiConfig ParseConfDirectory(string confDir)
        {
            return ParseConfDirectory(confDir, null, null);
        }

        /// <summary>Legacy file selection, with explicit source identity for the pack converter.</summary>
        public static ShimejiConfig ParseConfDirectory(string confDir, string packRoot, string imageSet)
        {
            if (string.IsNullOrEmpty(confDir)) throw new ArgumentNullException("confDir");
            if (!Directory.Exists(confDir)) throw new DirectoryNotFoundException("No such conf directory: " + confDir);

            string actionsPath = FindActionsFile(confDir);
            if (actionsPath == null)
                throw new FileNotFoundException("No actions file (" + string.Join(", ", ActionsFileNames) + ") under " + confDir);
            string behaviorsPath = FindBehaviorsFile(confDir);

            var config = new ShimejiConfig();
            XDocument actions = LoadXmlFile(actionsPath);
            ShimejiSourceDocument actionSource = ReadSource(actions, Path.GetFileName(actionsPath), imageSet,
                packRoot == null ? Path.GetFileName(actionsPath) : PackPath(packRoot, actionsPath));
            ParseActions(actions, config);
            ShimejiSourceDocument behaviorSource = null;
            if (behaviorsPath != null)
            {
                XDocument behaviors = LoadXmlFile(behaviorsPath);
                behaviorSource = ReadSource(behaviors, Path.GetFileName(behaviorsPath), imageSet,
                    packRoot == null ? Path.GetFileName(behaviorsPath) : PackPath(packRoot, behaviorsPath));
                ParseBehaviorConditions(behaviors, config);
                ParseBehaviorFrequencies(behaviors, config);
                config.BehaviorsFile = behaviorsPath;
            }
            config.Source = new ShimejiSourceConfig(actionSource, behaviorSource);
            return config;
        }

        /// <summary>The actions file in <paramref name="dir"/> under the first accepted leaf name that exists
        /// (case-insensitive, set order), or null.</summary>
        public static string FindActionsFile(string dir)
        {
            return FindFile(dir, ActionsFileNames);
        }

        /// <summary>The behaviours file in <paramref name="dir"/>, resolved like <see cref="FindActionsFile"/>.</summary>
        public static string FindBehaviorsFile(string dir)
        {
            return FindFile(dir, BehaviorsFileNames);
        }

        /// <summary>True when <paramref name="leaf"/> is one of <see cref="ActionsFileNames"/>, ignoring case;
        /// for callers that hold an entry name rather than a directory (the CLI's zip census).</summary>
        public static bool IsActionsFileName(string leaf)
        {
            foreach (string name in ActionsFileNames)
                if (string.Equals(leaf, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Set order first, then whatever the directory holds: a directory carrying both actions.xml and 動作.xml
        // resolves to the English one deterministically rather than to whichever the file system lists first.
        private static string FindFile(string dir, string[] names)
        {
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (DirectoryNotFoundException) { return null; }
            foreach (string name in names)
                foreach (string path in files)
                    if (string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))
                        return path;
            return null;
        }

        /// <summary>Parse an in-memory actions.xml string. For self-tests and callers that already hold the
        /// document text rather than a directory on disk -- the `reloop` migration reads confs straight out
        /// of zip entries, where there is no directory to point <see cref="ParseConfDirectory"/> at, and
        /// going through this parser rather than a regex is what makes a Japanese conf read as the same
        /// data as an English one.</summary>
        public static ShimejiConfig ParseActionsXml(string actionsXml)
        {
            return ParseActionsXml(actionsXml, null, "actions.xml");
        }

        public static ShimejiConfig ParseActionsXml(string actionsXml, string imageSet, string packRelativePath)
        {
            return ParseActionsXml(actionsXml, imageSet, packRelativePath, MaximumSourceMaterializationCharacters);
        }

        internal static ShimejiConfig ParseActionsXml(string actionsXml, string imageSet, string packRelativePath,
            int maximumMaterializationCharacters)
        {
            packRelativePath = ValidatePackPath(packRelativePath);
            var config = new ShimejiConfig();
            XDocument actions = ParseXml(actionsXml, Path.GetFileName(packRelativePath.Replace('\\', '/')), maximumMaterializationCharacters);
            config.Source = new ShimejiSourceConfig(ReadSource(actions, Path.GetFileName(packRelativePath.Replace('\\', '/')),
                imageSet, packRelativePath), null);
            ParseActions(actions, config);
            return config;
        }

        /// <summary>Parse in-memory actions + behaviours text, the behaviour-side twin of
        /// <see cref="ParseActionsXml"/>. Frequencies and conditions live in behaviors.xml, so a test that
        /// only has actions text cannot reach them, and going through a temp directory to assert a parse
        /// rule tests the filesystem as much as the parser.</summary>
        public static ShimejiConfig ParseConfXml(string actionsXml, string behaviorsXml)
        {
            return ParseConfXml(actionsXml, behaviorsXml, null, "actions.xml", "behaviors.xml");
        }

        public static ShimejiConfig ParseConfXml(string actionsXml, string behaviorsXml, string imageSet,
            string actionsRelativePath, string behaviorsRelativePath)
        {
            actionsRelativePath = ValidatePackPath(actionsRelativePath);
            var config = new ShimejiConfig();
            XDocument actions = ParseXml(actionsXml, Path.GetFileName(actionsRelativePath.Replace('\\', '/')));
            ShimejiSourceDocument actionSource = ReadSource(actions, Path.GetFileName(actionsRelativePath.Replace('\\', '/')),
                imageSet, actionsRelativePath);
            ParseActions(actions, config);
            ShimejiSourceDocument behaviorSource = null;
            if (!string.IsNullOrEmpty(behaviorsXml))
            {
                behaviorsRelativePath = ValidatePackPath(behaviorsRelativePath);
                XDocument behaviors = ParseXml(behaviorsXml, Path.GetFileName(behaviorsRelativePath.Replace('\\', '/')));
                behaviorSource = ReadSource(behaviors, Path.GetFileName(behaviorsRelativePath.Replace('\\', '/')),
                    imageSet, behaviorsRelativePath);
                ParseBehaviorConditions(behaviors, config);
                ParseBehaviorFrequencies(behaviors, config);
            }
            config.Source = new ShimejiSourceConfig(actionSource, behaviorSource);
            return config;
        }

        /// <summary>Read only the source IR, using Main.loadConfiguration's three configuration tiers.
        /// This does not use the legacy converter's tolerant filename or vocabulary rules.</summary>
        public static ShimejiSourceConfig ParseSourcePack(string packRoot, string imageSet)
        {
            if (string.IsNullOrEmpty(packRoot)) throw new ArgumentNullException("packRoot");
            if (string.IsNullOrEmpty(imageSet) || imageSet == "." || imageSet == ".."
                || imageSet.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                throw new ArgumentException("Source image set must be one directory name.", "imageSet");
            string actions = SourceConfigFile(packRoot, imageSet, false);
            string behaviors = SourceConfigFile(packRoot, imageSet, true);
            string info = Path.Combine(packRoot, "conf", "info.xml");
            foreach (string directory in new[] { Path.Combine(packRoot, "conf", imageSet), Path.Combine(packRoot, "img", imageSet, "conf") })
                if (File.Exists(Path.Combine(directory, "info.xml"))) info = Path.Combine(directory, "info.xml");
            return new ShimejiSourceConfig(ReadSourceFile(packRoot, actions, imageSet), ReadSourceFile(packRoot, behaviors, imageSet),
                File.Exists(info) ? ReadSourceFile(packRoot, info, imageSet) : null);
        }

        private static ShimejiSourceDocument ReadSourceFile(string packRoot, string path, string imageSet)
        {
            // The probe names above are the source's spellings, and a case-insensitive file system answers them for a
            // file spelt otherwise ("Actions.xml"); the document is named as the pack spells it, which is what the
            // source's refKey is relative to (CONTRACT section 3: the configuration file, relative to the pack).
            path = AsSpelled(packRoot, path);
            string relativePath = PackPath(packRoot, path);
            return ReadSource(LoadXmlFile(path), Path.GetFileName(path), imageSet, relativePath);
        }

        /// <summary>
        /// <paramref name="path"/>, under <paramref name="packRoot"/>, with every component below the root spelt as its
        /// directory entry is (H3). A component no entry matches is left as given, so a missing file is still refused
        /// where it is read. Two entries that differ only in case (a case-sensitive directory) make the name ambiguous,
        /// which refuses by name rather than guess which one the source would open.
        /// </summary>
        internal static string AsSpelled(string packRoot, string path)
        {
            string root = Path.GetFullPath(packRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return path;
            string current = root;
            foreach (string part in full.Substring(root.Length + 1).Split(Path.DirectorySeparatorChar))
            {
                string[] entries = Directory.Exists(current) ? Directory.GetFileSystemEntries(current).Select(Path.GetFileName).ToArray() : new string[0];
                current = Path.Combine(current, SpelledEntry(entries, part));
            }
            return current;
        }

        /// <summary>The directory entry <paramref name="probe"/> names, ignoring case: the one entry that matches, the
        /// probe itself when none does, and a refusal when several do.</summary>
        internal static string SpelledEntry(IEnumerable<string> entries, string probe)
        {
            List<string> matches = entries.Where(e => string.Equals(e, probe, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count > 1)
                throw new InvalidDataException("SOURCE_PATH_AMBIGUOUS: '" + probe + "' matches " + matches.Count
                    + " directory entries that differ only in case");
            return matches.Count == 1 ? matches[0] : probe;
        }

        /// <summary>
        /// Decision 69: whether the source finds any configuration for <paramref name="imageSet"/>: an action or a behaviour
        /// file, its own (conf/&lt;set&gt;/, img/&lt;set&gt;/conf/) or the pack's shared default under conf/, looked up as
        /// <see cref="SourceConfigFile"/> looks (Main.java:323-366). With neither, the source's load fails and it drops the set
        /// and loads the others (Main.java:367, :478-484, :289-294).
        /// </summary>
        public static bool HasSourceConfiguration(string packRoot, string imageSet)
        {
            return File.Exists(SourceConfigFile(packRoot, imageSet, false)) || File.Exists(SourceConfigFile(packRoot, imageSet, true));
        }

        private static string SourceConfigFile(string packRoot, string imageSet, bool behaviors)
        {
            string defaultName = behaviors ? "behaviors.xml" : "actions.xml";
            string japaneseName = behaviors ? "行動.xml" : "動作.xml";
            string rootConf = Path.Combine(packRoot, "conf");
            string selected = Path.Combine(rootConf, defaultName);
            if (File.Exists(Path.Combine(rootConf, japaneseName))) selected = Path.Combine(rootConf, japaneseName);
            string[] names = behaviors
                ? new[] { "behaviors.xml", "behavior.xml", "行動.xml", "\u00DE\u00ED\u00EE\u00D5\u00EF\u00F2.xml",
                    "\u00AA\u00B5\u00A6-.xml", "\u00ECs\u00F4\u00AB.xml", "two.xml", "2.xml" }
                : new[] { "actions.xml", "動作.xml", "\u00D5\u00EF\u00F2\u00F5\u00A2\u00A3.xml",
                    "\u00A6-\u00BA@.xml", "\u00F4\u00AB\u00EC\u00FD.xml", "one.xml", "1.xml" };
            foreach (string directory in new[] { Path.Combine(rootConf, imageSet), Path.Combine(packRoot, "img", imageSet, "conf") })
            {
                foreach (string name in names)
                    if (File.Exists(Path.Combine(directory, name)))
                    {
                        selected = Path.Combine(directory, name);
                        break;
                    }
            }
            return selected;
        }

        /// <summary>Parse the bundled Shimeji-EE base behaviour config (embedded in this assembly), used for a
        /// sprites-only skin that ships no conf of its own. See base-conf/NOTICE.txt for licensing.</summary>
        public static ShimejiConfig ParseBundledConf()
        {
            var config = new ShimejiConfig();
            XDocument actions = LoadEmbeddedXml("base-actions.xml");
            ShimejiSourceDocument actionSource = ReadSource(actions, "base-actions.xml");
            ParseActions(actions, config);
            // REQUIRED, like the actions. This served only the bundle, which always ships a behaviours file,
            // so its absence is a build defect and not a skin choice -- yet it was loaded as optional and
            // skipped in silence, leaving every behaviour frequency at zero (so every hub spoke got the
            // base weight and the PLAYED set-piece gate went dead) while the self-test still printed
            // "behaviors.xml embed and parse" (F442). A user skin's optional behaviours go through
            // ParseConfDirectory and are unaffected. The optional path itself is gone (R-072): with both
            // callers requiring their resource it was a `required` parameter that was constant-true and a
            // return-null branch nothing could reach.
            XDocument behaviors = LoadEmbeddedXml("base-behaviors.xml");
            ShimejiSourceDocument behaviorSource = ReadSource(behaviors, "base-behaviors.xml");
            ParseBehaviorConditions(behaviors, config);
            ParseBehaviorFrequencies(behaviors, config);
            config.Source = new ShimejiSourceConfig(actionSource, behaviorSource);
            return config;
        }

        private static XDocument LoadEmbeddedXml(string logicalName)
        {
            Assembly asm = typeof(ShimejiParser).Assembly;
            using (Stream s = asm.GetManifestResourceStream(logicalName))
            {
                if (s == null) throw new InvalidOperationException("Bundled Shimeji conf resource missing: " + logicalName);
                using (var bytes = new MemoryStream())
                {
                    s.CopyTo(bytes);
                    return LoadXmlBytes(bytes.ToArray(), logicalName);
                }
            }
        }

        private sealed class SourceXmlName
        {
            internal string QualifiedName;
            internal string Prefix;
            internal bool IsNamespaceDeclaration;
            internal readonly List<SourceXmlName> Attributes = new List<SourceXmlName>();
        }

        private static XmlReaderSettings SourceReaderSettings()
        {
            return new XmlReaderSettings { MaxCharactersInDocument = MaximumSourceCharacters, XmlResolver = null };
        }

        private static XDocument ParseXml(string text, string sourceFileName,
            int maximumMaterializationCharacters = MaximumSourceMaterializationCharacters)
        {
            return LoadXml(() => XmlReader.Create(new StringReader(text), SourceReaderSettings()), sourceFileName,
                maximumMaterializationCharacters);
        }

        private static XDocument LoadXmlFile(string path)
        {
            return LoadXmlBytes(File.ReadAllBytes(path), Path.GetFileName(path));
        }

        private static XDocument LoadXmlBytes(byte[] bytes, string sourceFileName)
        {
            return LoadXml(() => XmlReader.Create(new MemoryStream(bytes, false), SourceReaderSettings()), sourceFileName,
                MaximumSourceMaterializationCharacters);
        }

        // Conservative units count the strings the IR will retain, before constructing them. Each ordinal is
        // charged ten digits, the Int32 maximum; node/attribute overhead is charged too. Direct text is separately
        // bounded by MaximumSourceCharacters and is not duplicated down the tree.
        private static XDocument LoadXml(Func<XmlReader> open, string sourceFileName, int maximumMaterializationCharacters)
        {
            var names = new List<SourceXmlName>();
            var canonicalPathCharacters = new long[MaximumSourceDepth];
            var rawPathCharacters = new long[MaximumSourceDepth];
            long materializationCharacters = 0;
            int keyPrefixCharacters = sourceFileName.Length + 1;
            using (XmlReader reader = open())
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (reader.Depth + 1 > MaximumSourceDepth)
                        throw new InvalidDataException("SOURCE_DEPTH_LIMIT: maximum element depth is " + MaximumSourceDepth);
                    int depth = reader.Depth;
                    long canonical = (depth == 0 ? 0 : canonicalPathCharacters[depth - 1]) + Canon(reader.LocalName).Length + 13;
                    long raw = (depth == 0 ? 0 : rawPathCharacters[depth - 1]) + reader.LocalName.Length + 13;
                    materializationCharacters += 32 + keyPrefixCharacters + 2 * canonical + raw;
                    if (materializationCharacters > maximumMaterializationCharacters)
                        throw new InvalidDataException("SOURCE_MATERIALIZATION_LIMIT: cumulative node paths exceed " + maximumMaterializationCharacters);
                    canonicalPathCharacters[depth] = canonical;
                    rawPathCharacters[depth] = raw;
                    var name = new SourceXmlName { QualifiedName = reader.Name, Prefix = reader.Prefix };
                    if (reader.MoveToFirstAttribute())
                    {
                        do
                        {
                            materializationCharacters += 16 + keyPrefixCharacters + canonical + Canon(reader.LocalName).Length + 13 + reader.Name.Length;
                            if (materializationCharacters > maximumMaterializationCharacters)
                                throw new InvalidDataException("SOURCE_MATERIALIZATION_LIMIT: cumulative attribute keys exceed " + maximumMaterializationCharacters);
                            name.Attributes.Add(new SourceXmlName { QualifiedName = reader.Name, Prefix = reader.Prefix,
                                IsNamespaceDeclaration = reader.Name == "xmlns" || reader.Prefix == "xmlns" });
                        } while (reader.MoveToNextAttribute());
                        reader.MoveToElement();
                    }
                    names.Add(name);
                }
            }
            XDocument document;
            using (XmlReader reader = open())
                document = XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
            if (document.Root == null) return document;
            var elements = document.Root.DescendantsAndSelf().ToList();
            if (elements.Count != names.Count) throw new InvalidDataException("SOURCE_NAME_MISMATCH: element count changed");
            for (int i = 0; i < elements.Count; i++)
            {
                elements[i].AddAnnotation(names[i]);
                var attributes = elements[i].Attributes().ToList();
                if (attributes.Count != names[i].Attributes.Count) throw new InvalidDataException("SOURCE_NAME_MISMATCH: attribute count changed");
                for (int j = 0; j < attributes.Count; j++) attributes[j].AddAnnotation(names[i].Attributes[j]);
            }
            return document;
        }

        private static string PackPath(string packRoot, string path)
        {
            return ValidatePackPath(Path.GetRelativePath(Path.GetFullPath(packRoot), Path.GetFullPath(path)).Replace('\\', '/'));
        }

        private static string ValidatePackPath(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Source configuration needs a pack-relative path.");
            string result = path.Replace('\\', '/');
            if (Path.IsPathRooted(result) || result.IndexOf(':') >= 0
                || result.Split('/').Any(p => p == ".." || p == "." || p.Length == 0))
                throw new ArgumentException("Source configuration path must stay relative to the pack: " + path);
            return result;
        }

        private static ShimejiSourceDocument ReadSource(XDocument document, string fileName,
            string imageSet = null, string packRelativePath = null)
        {
            if (document.Root == null) throw new InvalidDataException("Shimeji configuration has no root element: " + fileName);
            string relativePath = ValidatePackPath(packRelativePath ?? fileName);
            bool japanese = document.Root.Elements().Any(e => e.Annotation<SourceXmlName>().QualifiedName == "動作リスト"
                || e.Annotation<SourceXmlName>().QualifiedName == "行動リスト");
            Dictionary<string, string> schema = CreateSourceSchema(japanese);
            var elements = document.Root.DescendantsAndSelf().ToList();
            var paths = new Dictionary<XElement, string>();
            var counts = new Dictionary<XElement, Dictionary<string, int>>();
            var sourcePaths = new Dictionary<XElement, string>();
            var sourceCounts = new Dictionary<XElement, Dictionary<string, int>>();
            foreach (XElement element in elements)
            {
                string name = CanonLocal(element);
                int ordinal = 1;
                string parentPath = "";
                string sourceName = element.Name.LocalName;
                int sourceOrdinal = 1;
                string sourceParentPath = "";
                if (element.Parent != null)
                {
                    Dictionary<string, int> siblings;
                    if (!counts.TryGetValue(element.Parent, out siblings))
                    {
                        siblings = new Dictionary<string, int>(StringComparer.Ordinal);
                        counts.Add(element.Parent, siblings);
                    }
                    int previous;
                    siblings.TryGetValue(name, out previous);
                    ordinal = previous + 1;
                    siblings[name] = ordinal;
                    parentPath = paths[element.Parent];
                    Dictionary<string, int> sourceSiblings;
                    if (!sourceCounts.TryGetValue(element.Parent, out sourceSiblings))
                    {
                        sourceSiblings = new Dictionary<string, int>(StringComparer.Ordinal);
                        sourceCounts.Add(element.Parent, sourceSiblings);
                    }
                    int sourcePrevious;
                    sourceSiblings.TryGetValue(sourceName, out sourcePrevious);
                    sourceOrdinal = sourcePrevious + 1;
                    sourceSiblings[sourceName] = sourceOrdinal;
                    sourceParentPath = sourcePaths[element.Parent];
                }
                paths[element] = parentPath + "/" + name + "[" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
                sourcePaths[element] = sourceParentPath + "/" + sourceName + "["
                    + sourceOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
            }
            var nodes = new Dictionary<XElement, ShimejiSourceElement>();
            // Bottom-up construction avoids recursive parsing of deeply nested source composites.
            for (int index = elements.Count - 1; index >= 0; index--)
            {
                XElement element = elements[index];
                string key = fileName + "#" + paths[element];
                var attributes = new List<ShimejiSourceAttribute>();
                var attributeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (XAttribute attribute in element.Attributes())
                {
                    string name = Canon(attribute.Name.LocalName);
                    int previous;
                    attributeCounts.TryGetValue(name, out previous);
                    attributeCounts[name] = previous + 1;
                    SourceXmlName sourceAttribute = attribute.Annotation<SourceXmlName>();
                    attributes.Add(new ShimejiSourceAttribute(name, attribute.Name.LocalName, attribute.Name.NamespaceName,
                        attribute.Value, key + "@" + name + "[" + (previous + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "]",
                        ResolveSchemaKey(schema, sourceAttribute.QualifiedName), sourceAttribute.QualifiedName,
                        sourceAttribute.Prefix, sourceAttribute.IsNamespaceDeclaration));
                }
                var children = new List<ShimejiSourceElement>();
                foreach (XElement child in element.Elements()) children.Add(nodes[child]);
                IXmlLineInfo line = element;
                SourceXmlName sourceElement = element.Annotation<SourceXmlName>();
                nodes[element] = new ShimejiSourceElement(CanonLocal(element), element.Name.LocalName,
                    element.Name.NamespaceName, paths[element], key, line.HasLineInfo() ? line.LineNumber : 0,
                    line.HasLineInfo() ? line.LinePosition : 0, string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value)),
                    attributes, children, sourcePaths[element], ResolveSchemaKey(schema, sourceElement.QualifiedName),
                    sourceElement.QualifiedName, sourceElement.Prefix);
            }
            return new ShimejiSourceDocument(fileName, nodes[document.Root],
                japanese ? ShimejiSourceSchema.Japanese : ShimejiSourceSchema.Base, imageSet, relativePath);
        }

        // Vocabulary facts in ordinal key order. File layout and properties-line syntax are not retained.
        private static readonly Dictionary<string, string> SourceBaseSchemaFacts =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Action", "Action" },
            { "ActionList", "ActionList" },
            { "ActionReference", "ActionReference" },
            { "Add", "Add" },
            { "Affordance", "Affordance" },
            { "Animate", "Animate" },
            { "Animation", "Animation" },
            { "Arc", "Arc" },
            { "Artist", "Artist" },
            { "Behaviour", "Behavior" },
            { "BehaviourList", "BehaviorList" },
            { "BehaviourReference", "BehaviorReference" },
            { "BorderType", "BorderType" },
            { "BornBehaviour", "BornBehavior" },
            { "BornCount", "BornCount" },
            { "BornInterval", "BornInterval" },
            { "BornMascot", "BornMascot" },
            { "BornTransient", "BornTransient" },
            { "BornX", "BornX" },
            { "BornY", "BornY" },
            { "Breed", "Breed" },
            { "Ceiling", "Ceiling" },
            { "Characteristics", "Characteristics" },
            { "ChaseMouse", "ChaseMouse" },
            { "Class", "Class" },
            { "Commissioner", "Commissioner" },
            { "Condition", "Condition" },
            { "Constant", "Constant" },
            { "Draggable", "Draggable" },
            { "Dragged", "Dragged" },
            { "Duration", "Duration" },
            { "Embedded", "Embedded" },
            { "Fall", "Fall" },
            { "Floor", "Floor" },
            { "FootDX", "FootDX" },
            { "FootX", "FootX" },
            { "Frequency", "Frequency" },
            { "Gravity", "Gravity" },
            { "Hidden", "Hidden" },
            { "Hotspot", "Hotspot" },
            { "IeOffsetX", "IeOffsetX" },
            { "IeOffsetY", "IeOffsetY" },
            { "Image", "Image" },
            { "ImageAnchor", "ImageAnchor" },
            { "ImageRight", "ImageRight" },
            { "Information", "Information" },
            { "InitialVX", "InitialVX" },
            { "InitialVY", "InitialVY" },
            { "IsTurn", "IsTurn" },
            { "Look", "Look" },
            { "LookRight", "LookRight" },
            { "Loop", "Loop" },
            { "Move", "Move" },
            { "Name", "Name" },
            { "NextBehaviourList", "NextBehaviorList" },
            { "OffsetType", "OffsetType" },
            { "OffsetX", "OffsetX" },
            { "OffsetY", "OffsetY" },
            { "Origin", "Origin" },
            { "Pose", "Pose" },
            { "PreviewImage", "PreviewImage" },
            { "ResistanceX", "RegistanceX" },
            { "ResistanceY", "RegistanceY" },
            { "Scan", "Scan" },
            { "Scripter", "Scripter" },
            { "Select", "Select" },
            { "Sequence", "Sequence" },
            { "Shape", "Shape" },
            { "Size", "Size" },
            { "Sound", "Sound" },
            { "SplashImage", "SplashImage" },
            { "SplashImages", "SplashImages" },
            { "Stay", "Stay" },
            { "Support", "Support" },
            { "TargetBehaviour", "TargetBehavior" },
            { "TargetLook", "TargetLook" },
            { "TargetX", "TargetX" },
            { "TargetY", "TargetY" },
            { "Thrown", "Thrown" },
            { "Toggleable", "Toggleable" },
            { "TransformBehaviour", "TransformBehavior" },
            { "TransformMascot", "TransformMascot" },
            { "Type", "Type" },
            { "URL", "URL" },
            { "Value", "Value" },
            { "Velocity", "Velocity" },
            { "VelocityParam", "VelocityParam" },
            { "VelocityX", "VelocityX" },
            { "VelocityY", "VelocityY" },
            { "Volume", "Volume" },
            { "Wall", "Wall" },
            { "X", "X" },
            { "Y", "Y" },
        };

        private static readonly Dictionary<string, string> SourceJapaneseSchemaFacts =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Action", "動作" },
            { "ActionList", "動作リスト" },
            { "ActionReference", "動作参照" },
            { "Add", "追加" },
            { "Animate", "固定" },
            { "Animation", "アニメーション" },
            { "Behaviour", "行動" },
            { "BehaviourList", "行動リスト" },
            { "BehaviourReference", "行動参照" },
            { "BorderType", "枠" },
            { "BornBehaviour", "生まれた時の行動" },
            { "BornX", "生まれる場所X" },
            { "BornY", "生まれる場所Y" },
            { "Ceiling", "天井" },
            { "ChaseMouse", "マウスの周りに集まる" },
            { "Class", "クラス" },
            { "Condition", "条件" },
            { "Constant", "定数" },
            { "Dragged", "ドラッグされる" },
            { "Duration", "長さ" },
            { "Embedded", "組み込み" },
            { "Fall", "落下する" },
            { "Floor", "地面" },
            { "FootX", "footX" },
            { "Frequency", "頻度" },
            { "Gravity", "重力" },
            { "H", "H" },
            { "IeOffsetX", "IEの端X" },
            { "IeOffsetY", "IEの端Y" },
            { "Image", "画像" },
            { "ImageAnchor", "基準座標" },
            { "InitialVX", "初速X" },
            { "InitialVY", "初速Y" },
            { "LookRight", "右向き" },
            { "Loop", "繰り返し" },
            { "Move", "移動" },
            { "Name", "名前" },
            { "NextBehaviourList", "次の行動リスト" },
            { "OffsetX", "端X" },
            { "OffsetY", "端Y" },
            { "Pose", "ポーズ" },
            { "ResistanceX", "空気抵抗X" },
            { "ResistanceY", "空気抵抗Y" },
            { "Select", "選択" },
            { "Sequence", "複合" },
            { "Stay", "静止" },
            { "TargetX", "目的地X" },
            { "TargetY", "目的地Y" },
            { "Thrown", "投げられる" },
            { "Type", "種類" },
            { "Value", "値" },
            { "Velocity", "移動速度" },
            { "VelocityParam", "速度" },
            { "W", "W" },
            { "Wall", "壁" },
            { "X", "X" },
            { "Y", "Y" },
        };

        /// <summary>The source's <c>schema.getString(key)</c> for a document read in <paramref name="schema"/>: the
        /// Japanese override when the document is Japanese and the key has one, else the base read name (the
        /// ResourceBundle parent fallback, Configuration.java:62), or null for a key neither table holds. The source
        /// reads enumerated attribute VALUES this way too (Type, BorderType, OffsetType, a Characteristics piece) and
        /// the engine's behaviour names (Fall, Dragged, Thrown), so a converter mapping a value reads it here, from the
        /// same verified facts the element and attribute names resolve through.</summary>
        internal static string SchemaString(ShimejiSourceSchema schema, string key)
        {
            string value;
            if (schema == ShimejiSourceSchema.Japanese && SourceJapaneseSchemaFacts.TryGetValue(key, out value)) return value;
            return SourceBaseSchemaFacts.TryGetValue(key, out value) ? value : null;
        }

        private static Dictionary<string, string> CreateSourceSchema(bool japanese)
        {
            var values = new Dictionary<string, string>(SourceBaseSchemaFacts, StringComparer.Ordinal);
            if (japanese)
                foreach (KeyValuePair<string, string> pair in SourceJapaneseSchemaFacts) values[pair.Key] = pair.Value;
            var byQualifiedName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> pair in values) byQualifiedName.Add(pair.Value, pair.Key);
            return byQualifiedName;
        }

        private static string ResolveSchemaKey(Dictionary<string, string> schema, string qualifiedName)
        {
            string key;
            return schema.TryGetValue(qualifiedName, out key) ? key : null;
        }

        // Shimeji XML ships in three vocabularies: English (Pose/Image/...), British (Behaviour...), and the
        // official Japanese schema (ポーズ/画像/基準座標/...). The parser is namespace-blind; this makes it
        // vocabulary-blind too, canonicalising every element name, attribute name, and the Type/BorderType
        // enum VALUES to the English form the rest of the engine keys on. Source: gil/shimeji-ee
        // conf/schema_ja.properties. Without this, a Japanese skin parses to zero poses and fails compositing.
        private static readonly Dictionary<string, string> Alias =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // elements
            { "動作リスト", "ActionList" }, { "動作", "Action" }, { "動作参照", "ActionReference" },
            { "アニメーション", "Animation" }, { "ポーズ", "Pose" }, { "条件", "Condition" },
            { "行動", "Behavior" }, { "行動リスト", "BehaviorList" }, { "行動参照", "BehaviorReference" },
            { "次の行動リスト", "NextBehaviorList" }, { "定数", "Constant" },
            // attributes
            { "名前", "Name" }, { "種類", "Type" }, { "クラス", "Class" }, { "枠", "BorderType" },
            { "画像", "Image" }, { "基準座標", "ImageAnchor" }, { "移動速度", "Velocity" }, { "長さ", "Duration" },
            { "頻度", "Frequency" }, { "繰り返し", "Loop" }, { "追加", "Add" }, { "重力", "Gravity" }, { "値", "Value" },
            { "目的地X", "TargetX" }, { "目的地Y", "TargetY" }, { "速度", "VelocityParam" },
            { "初速X", "InitialVX" }, { "初速Y", "InitialVY" }, { "空気抵抗X", "RegistanceX" }, { "空気抵抗Y", "RegistanceY" },
            { "右向き", "LookRight" }, { "IEの端X", "IeOffsetX" }, { "IEの端Y", "IeOffsetY" },
            { "生まれる場所X", "BornX" }, { "生まれる場所Y", "BornY" }, { "生まれた時の行動", "BornBehavior" },
            { "端X", "OffsetX" }, { "端Y", "OffsetY" },
            // Type values
            { "組み込み", "Embedded" }, { "移動", "Move" }, { "静止", "Stay" }, { "固定", "Animate" },
            { "複合", "Sequence" }, { "選択", "Select" },
            // BorderType values
            { "地面", "Floor" }, { "壁", "Wall" }, { "天井", "Ceiling" },
            // British spellings
            { "Behaviour", "Behavior" }, { "BehaviourReference", "BehaviorReference" },
            { "BehaviourList", "BehaviorList" }, { "NextBehaviourList", "NextBehaviorList" },
            // NextBehaviour SINGULAR. The List form was aliased and this one was not, which is exactly
            // backwards: base-conf/behaviors.xml contains 14 <NextBehavior> and ZERO <NextBehaviorList>.
            // ParseBehaviorFrequencies excludes a <Behavior> that sits under either, so without this a
            // British conf's inline transition weights were counted as ROOT selection frequencies and
            // every hub weight for that skin came out skewed.
            { "NextBehaviour", "NextBehavior" },
            { "BornBehaviour", "BornBehavior" }, { "TransformBehaviour", "TransformBehavior" }, { "TargetBehaviour", "TargetBehavior" },
        };

        private static string Canon(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            string c;
            return Alias.TryGetValue(name, out c) ? c : name;
        }

        private static string CanonLocal(XElement e) { return Canon(e.Name.LocalName); }

        private static string Attr(XElement e, string name)
        {
            foreach (XAttribute a in e.Attributes())
                if (string.Equals(Canon(a.Name.LocalName), name, StringComparison.Ordinal))
                    return a.Value;
            return null;
        }

        private static void ParseActions(XDocument doc, ShimejiConfig config)
        {
            // Top-level actions are the DIRECT children of each <ActionList> named Action. Nested <Action>
            // elements (inside a Sequence/Select/Composite) are lower actions and are NOT top-level -- they
            // are folded into their parent's subtree blob instead, exactly as the census does.
            foreach (XElement list in doc.Descendants().Where(e => CanonLocal(e) == "ActionList"))
            {
                foreach (XElement el in list.Elements().Where(e => CanonLocal(e) == "Action"))
                {
                    var action = new ShimejiAction
                    {
                        Name = Attr(el, "Name"),
                        // Type and BorderType are enum VALUES that are localised too (組み込み/地面...),
                        // so canonicalise them; Class is a Java class path and stays verbatim.
                        Type = Canon(Attr(el, "Type")),
                        Class = ShortClass(Attr(el, "Class")),
                        BorderType = Canon(Attr(el, "BorderType")),
                        SubtreeBlob = SubtreeBlob(el),
                    };
                    // The author's own statement about repetition: Animate plays through once, Move loops
                    // until its target and Stay holds. Read after the Type is canonicalised, so 固定 counts.
                    action.PlaysOnce = string.Equals(action.Type, "Animate", StringComparison.Ordinal);
                    // Direct <Animation> children only -- a composite action's nested <Action>s are folded
                    // into the subtree blob for classification, not into this action's animation list.
                    foreach (XElement anim in el.Elements().Where(e => CanonLocal(e) == "Animation"))
                    {
                        var animation = new ShimejiAnimation { Condition = Attr(anim, "Condition") };
                        foreach (XElement pose in anim.Elements().Where(e => CanonLocal(e) == "Pose"))
                            animation.Poses.Add(ParsePose(pose));
                        action.Animations.Add(animation);
                    }
                    action.ReferencedActions = CollectReferencedNames(el);
                    ActionClassifier.Classify(action);
                    config.Actions.Add(action);
                }
            }

            // The document-wide pose CENSUS, gathered from EVERY <Pose> regardless of how deeply it is nested.
            // Nothing composites from it: PosesToComposite walks the actions' own pose lists, so a pose inside a
            // nested composite action is counted here and never composited (F455). Its readers are the
            // script-flattening residue note and the parser self-tests.
            foreach (XElement pose in doc.Descendants().Where(e => CanonLocal(e) == "Pose"))
                config.Poses.Add(ParsePose(pose));
        }

        private const int ScriptDurationFallback = 8;  // a gentle hold, not the 1-tick flash a raw parse gives

        private static bool IsScript(string s)
        {
            return s != null && (s.IndexOf("${", StringComparison.Ordinal) >= 0 ||
                                 s.IndexOf("#{", StringComparison.Ordinal) >= 0);
        }

        private static ShimejiPose ParsePose(XElement p)
        {
            string durAttr = Attr(p, "Duration");
            string velAttr = Attr(p, "Velocity");
            string anchorAttr = Attr(p, "ImageAnchor");

            var pose = new ShimejiPose
            {
                Image = Attr(p, "Image"),
                Sound = Attr(p, "Sound"),
                // A ${...}/#{...} duration cannot be evaluated offline; flatten to a gentle hold rather than the
                // 1-tick flash a raw int-parse fallback would produce (a cause of "animations play too fast").
                Duration = ParseInt(durAttr, IsScript(durAttr) ? ScriptDurationFallback : 1),
                ScriptFlattened = IsScript(durAttr) || IsScript(velAttr) || IsScript(anchorAttr),
            };
            ParsePair(anchorAttr, out pose.AnchorX, out pose.AnchorY);
            ParsePair(velAttr, out pose.VelX, out pose.VelY);
            return pose;
        }

        private static int ParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, out v) ? v : fallback;
        }

        private static void ParsePair(string s, out int x, out int y)
        {
            x = 0; y = 0;
            if (string.IsNullOrEmpty(s)) return;
            string[] parts = s.Split(',');
            if (parts.Length >= 1) x = ParseInt(parts[0].Trim(), 0);
            if (parts.Length >= 2) y = ParseInt(parts[1].Trim(), 0);
        }

        private static void ParseBehaviorConditions(XDocument doc, ShimejiConfig config)
        {
            // Match the census: collect the Condition attribute wherever it appears on a Condition wrapper, a
            // Behavior, or a BehaviorReference, de-duplicated by (owner, condition).
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (XElement el in doc.Descendants())
            {
                // Canonicalised, so English, British "Behaviour", and Japanese 行動/条件 all match here.
                string local = CanonLocal(el);
                if (local != "Condition" && local != "Behavior" && local != "BehaviorReference") continue;
                string cond = Attr(el, "Condition");
                if (cond == null) continue;
                string owner = Attr(el, "Name") ?? "<wrapper>";
                string key = owner + "\0" + cond;
                if (!seen.Add(key)) continue;

                var bc = new ShimejiBehaviorCondition { Owner = owner, Condition = cond };
                ActionClassifier.ClassifyBehaviorCondition(bc);
                config.BehaviorConditions.Add(bc);
            }
        }

        // Root-level behaviour selection frequencies. In behaviors.xml each <Behavior Name="X" Frequency="N">
        // is a resting-state choice weighted by N (Frequency 0 = hidden / only reached as a transition), and X
        // is the name of the <Action> it runs. Transition targets live in <NextBehavior> as <BehaviorReference>
        // and are NOT root choices, so they are excluded. A name can appear under several context-gated
        // <Condition> lists (floor / wall / ceiling), so frequencies are summed by name; the emitter applies
        // them only to the actions it actually emits as floor spokes, so wall/ceiling/IE weights fall away.
        private static void ParseBehaviorFrequencies(XDocument doc, ShimejiConfig config)
        {
            foreach (XElement el in doc.Descendants().Where(e => CanonLocal(e) == "Behavior"))
            {
                if (HasAncestor(el, "NextBehavior") || HasAncestor(el, "NextBehaviorList")) continue;
                string name = Attr(el, "Name");
                if (string.IsNullOrEmpty(name)) continue;
                int freq = ParseInt(Attr(el, "Frequency"), 0);
                if (freq <= 0) continue;
                int existing;
                config.BehaviorFrequency.TryGetValue(name, out existing);
                config.BehaviorFrequency[name] = existing + freq;
            }
        }

        private static bool HasAncestor(XElement el, string canonLocalName)
        {
            for (XElement p = el.Parent; p != null; p = p.Parent)
                if (CanonLocal(p) == canonLocalName) return true;
            return false;
        }

        // Names an action's subtree references: every <ActionReference Name> and nested <Action Name> under it
        // (not the action itself). A composite (Sequence/Select) action lists the lower actions it plays here;
        // resolving these lets a root behaviour's Frequency reach the low-level posed actions the emitter turns
        // into spokes (e.g. behaviour "WalkAlongWorkAreaFloor" -> that Sequence action -> the posed "Walk").
        private static List<string> CollectReferencedNames(XElement actionEl)
        {
            var names = new List<string>();
            foreach (XElement d in actionEl.Descendants())
            {
                string local = CanonLocal(d);
                if (local != "ActionReference" && local != "Action") continue;
                string n = Attr(d, "Name");
                if (!string.IsNullOrEmpty(n)) names.Add(n);
            }
            return names;
        }

        internal static string ShortClass(string fullClass)
        {
            if (string.IsNullOrEmpty(fullClass)) return null;
            int dot = fullClass.LastIndexOf('.');
            return dot >= 0 ? fullClass.Substring(dot + 1) : fullClass;
        }

        /// <summary>
        /// The EXPRESSION text in an action's subtree: every Condition value, plus any other attribute value
        /// that carries a script (${...} / #{...}). Names, image paths, sound paths and enum values are left
        /// out on purpose.
        ///
        /// It used to be every attribute value, and ActionClassifier tests the blob for the bare token
        /// "cursor" -- so an action whose IMAGE was named cursor*.png was graded Group2 as "branches on cursor
        /// position" and, being stationary, emitted as a faceCursor gaze on the strength of a filename; a
        /// moving one would have been dropped from the floor graph outright. Not hypothetical: alan becker's
        /// Victim skin ships `CursorHate`, a plain Stay with no Condition over /cursorsetup01.png, in seven
        /// copies across the corpus (F441). PetEmitter.IsGazeAction reads the same blob for the same token,
        /// so both halves narrow together, which is what the 1.1.7 fix that aligned their case sensitivity
        /// asked for. Every token the classifier looks for -- cursor, activeIE, totalCount, mascot.anchor,
        /// TargetX/TargetY -- is Shimeji expression vocabulary, so nothing that classified before stops
        /// classifying; BundledConfSelfTest pins the 91-action census to prove it.
        /// </summary>
        private static string SubtreeBlob(XElement el)
        {
            var values = new List<string>();
            foreach (XElement e in el.DescendantsAndSelf())
                foreach (XAttribute a in e.Attributes())
                {
                    if (string.Equals(Canon(a.Name.LocalName), "Condition", StringComparison.Ordinal) || IsScript(a.Value))
                        values.Add(a.Value);
                }
            return string.Join(" || ", values);
        }
    }
}
