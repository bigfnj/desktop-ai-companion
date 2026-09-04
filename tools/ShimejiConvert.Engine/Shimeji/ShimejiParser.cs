using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
        /// <summary>
        /// Parse the actions.xml and behaviors.xml found under <paramref name="confDir"/>. actions.xml is
        /// required; behaviors.xml is optional (a skin may ship only actions). Throws if actions.xml is
        /// missing or malformed.
        /// </summary>
        public static ShimejiConfig ParseConfDirectory(string confDir)
        {
            if (string.IsNullOrEmpty(confDir)) throw new ArgumentNullException("confDir");
            if (!Directory.Exists(confDir)) throw new DirectoryNotFoundException("No such conf directory: " + confDir);

            string actionsPath = FindFile(confDir, "actions.xml");
            if (actionsPath == null)
                throw new FileNotFoundException("No actions.xml under " + confDir);
            string behaviorsPath = FindFile(confDir, "behaviors.xml");

            var config = new ShimejiConfig();
            ParseActions(XDocument.Load(actionsPath), config);
            if (behaviorsPath != null)
            {
                XDocument behaviors = XDocument.Load(behaviorsPath);
                ParseBehaviorConditions(behaviors, config);
                ParseBehaviorFrequencies(behaviors, config);
            }
            return config;
        }

        private static string FindFile(string dir, string name)
        {
            foreach (string path in Directory.GetFiles(dir))
                if (string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))
                    return path;
            return null;
        }

        /// <summary>Parse an in-memory actions.xml string. For self-tests and callers that already hold the
        /// document text rather than a directory on disk.</summary>
        internal static ShimejiConfig ParseActionsXml(string actionsXml)
        {
            var config = new ShimejiConfig();
            ParseActions(XDocument.Parse(actionsXml), config);
            return config;
        }

        /// <summary>Parse the bundled Shimeji-EE base behaviour config (embedded in this assembly), used for a
        /// sprites-only skin that ships no conf of its own. See base-conf/NOTICE.txt for licensing.</summary>
        public static ShimejiConfig ParseBundledConf()
        {
            var config = new ShimejiConfig();
            ParseActions(LoadEmbeddedXml("base-actions.xml", true), config);
            XDocument behaviors = LoadEmbeddedXml("base-behaviors.xml", false);
            if (behaviors != null)
            {
                ParseBehaviorConditions(behaviors, config);
                ParseBehaviorFrequencies(behaviors, config);
            }
            return config;
        }

        private static XDocument LoadEmbeddedXml(string logicalName, bool required)
        {
            Assembly asm = typeof(ShimejiParser).Assembly;
            using (Stream s = asm.GetManifestResourceStream(logicalName))
            {
                if (s == null)
                {
                    if (required) throw new InvalidOperationException("Bundled Shimeji conf resource missing: " + logicalName);
                    return null;
                }
                return XDocument.Load(s);
            }
        }

        private static string Local(XElement e) { return e.Name.LocalName; }

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
            // Type values
            { "組み込み", "Embedded" }, { "移動", "Move" }, { "静止", "Stay" }, { "固定", "Animate" },
            { "複合", "Sequence" }, { "選択", "Select" },
            // BorderType values
            { "地面", "Floor" }, { "壁", "Wall" }, { "天井", "Ceiling" },
            // British spellings
            { "Behaviour", "Behavior" }, { "BehaviourReference", "BehaviorReference" },
            { "BehaviourList", "BehaviorList" }, { "NextBehaviourList", "NextBehaviorList" },
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

            // The complete sprite set, gathered from EVERY <Pose> in the document regardless of how deeply it
            // is nested, so the compositor cannot miss a frame a skin tucked inside a composite action.
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
                string key = owner + " " + cond;
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

        private static string SubtreeBlob(XElement el)
        {
            var values = new List<string>();
            foreach (XElement e in el.DescendantsAndSelf())
                foreach (XAttribute a in e.Attributes())
                    values.Add(a.Value);
            return string.Join(" || ", values);
        }
    }
}
