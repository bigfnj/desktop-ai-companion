using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>Source preservation checks use synthetic configurations only.</summary>
    public static class SourceIrSelfTest
    {
        public static bool Run(out string detail)
        {
            var output = new StringBuilder("source-ir self-test: executing source preservation witnesses\n");
            var cases = new[]
            {
                new { Name = "ordered-reference-overrides", Run = (Action)OrderedReferences },
                new { Name = "raw-parameters-and-images", Run = (Action)RawParameters },
                new { Name = "script-envelopes", Run = (Action)ScriptEnvelopes },
                new { Name = "behavior-wrappers-and-add", Run = (Action)BehaviorWrappers },
                new { Name = "aliases-and-collisions", Run = (Action)Aliases },
                new { Name = "source-path-raw-ordinals", Run = (Action)RawSourcePaths },
                new { Name = "source-schema-resolution", Run = (Action)SchemaResolution },
                new { Name = "qualified-names-and-depth", Run = (Action)QualifiedNamesAndDepth },
                new { Name = "source-materialization-budget", Run = (Action)MaterializationBudget },
                new { Name = "source-pack-context", Run = (Action)SourcePackContext },
                new { Name = "immutable-snapshot", Run = (Action)ImmutableSnapshot },
                new { Name = "entrypoints-and-locators", Run = (Action)EntryPoints },
            };
            foreach (var item in cases)
            {
                try
                {
                    item.Run();
                    output.AppendLine("  source-ir PASS " + item.Name);
                }
                catch (Exception error)
                {
                    output.AppendLine("  FAIL SourceIrSelfTest/" + item.Name + ": " + error.Message);
                    detail = output.ToString().TrimEnd();
                    return false;
                }
            }
            detail = output.ToString().TrimEnd();
            return true;
        }

        private static void Require(bool condition, string witness)
        {
            if (!condition) throw new InvalidOperationException(witness);
        }

        private static ShimejiSourceElement Named(ShimejiSourceElement root, ShimejiSourceKind kind, string name)
        {
            return root.DescendantsAndSelf().Single(e => e.Kind == kind && e.Attribute("Name") != null
                && e.Attribute("Name").RawValue == name);
        }

        private static void OrderedReferences()
        {
            ShimejiConfig config = ShimejiParser.ParseConfXml(ActionsXml, BehaviorsXml);
            Require(config.Source != null && config.Source.Actions != null && config.Source.Behaviors != null,
                "source attachment missing on ParseConfXml");
            ShimejiSourceElement composite = Named(config.Source.Actions.Root, ShimejiSourceKind.Action, "Composite");
            Require(composite.Children.Select(e => e.Kind).SequenceEqual(new[] {
                ShimejiSourceKind.ActionReference, ShimejiSourceKind.Action, ShimejiSourceKind.ActionReference }),
                "nested action/reference order changed");
            ShimejiSourceElement first = composite.Children[0];
            ShimejiSourceElement second = composite.Children[2];
            Require(first.Attribute("Name").RawValue == "Motion" && second.Attribute("Name").RawValue == "Motion",
                "repeated references were resolved or deduplicated");
            Require(first.Attribute("TargetX").RawValue == "${mascot.anchor.x + 7}"
                && second.Attribute("TargetX").RawValue == "${mascot.anchor.x + 9}",
                "reference overrides lost their individual values");
            Require(first.SourceKey != second.SourceKey && first.ElementPath != second.ElementPath,
                "repeated references share a source locator");
            Require(composite.Children[1].Attribute("Type").RawValue == "Select"
                && composite.Children[1].Children[0].Kind == ShimejiSourceKind.ActionReference,
                "inline Select structure flattened");
            Require(composite.DescendantsAndSelf().Select(e => e.Kind).SequenceEqual(new[] {
                ShimejiSourceKind.Action, ShimejiSourceKind.ActionReference, ShimejiSourceKind.Action,
                ShimejiSourceKind.ActionReference, ShimejiSourceKind.Action, ShimejiSourceKind.ActionReference }),
                "source traversal no longer follows document order");
        }

        private static void RawParameters()
        {
            ShimejiConfig config = ShimejiParser.ParseActionsXml(ActionsXml);
            Require(config.Source != null && config.Source.Actions != null,
                "source attachment missing on ParseActionsXml");
            ShimejiSourceElement root = config.Source.Actions.Root;
            ShimejiSourceElement custom = root.DescendantsAndSelf().Single(e => e.Attribute("Class") != null);
            Require(custom.Attribute("Class").RawValue == "org.fixture.CustomAction",
                "full embedded class path shortened");
            Require(custom.RawAttribute("FutureParameter").RawValue == "preserve-me",
                "unknown action parameter dropped");
            ShimejiSourceElement motion = Named(root, ShimejiSourceKind.Action, "Motion");
            Require(motion.Attributes.Select(a => a.SourceName).SequenceEqual(new[] { "Name", "Type", "BorderType" }),
                "attribute order changed");
            ShimejiSourceElement animation = motion.Children.Single();
            Require(animation.Kind == ShimejiSourceKind.Animation
                && animation.Attribute("Condition").RawValue == "#{mascot.lookRight}", "animation condition missing");
            ShimejiSourceElement pose = animation.Children[0];
            Require(pose.Attribute("Image").RawValue == "/left.png" && pose.Attribute("ImageRight").RawValue == "/right.png",
                "authored left/right image identity lost");
            Require(pose.Attribute("ImageAnchor").RawValue == "8,16"
                && pose.Attribute("Velocity").RawValue == "#{mascot.anchor.x},0"
                && pose.Attribute("Duration").RawValue == "${4 + 2}", "pose parameters flattened");
            Require(pose.Attribute("Sound").RawValue == "/tick.wav" && pose.Attribute("Volume").RawValue == "0.4",
                "sound path or volume lost");
            Require(animation.Children[1].Attribute("Image") == null
                && animation.Children[2].Attribute("Image").RawValue == "", "missing image merged with explicit empty image");
            ShimejiSourceElement hotspot = animation.Children[3];
            Require(hotspot.Kind == ShimejiSourceKind.Hotspot && hotspot.Attribute("Behaviour").RawValue == "Touch"
                && hotspot.RawAttribute("Unknown").RawValue == "hot", "hotspot or extension attributes dropped");
            ShimejiSourceElement constant = root.Children.Single(e => e.Kind == ShimejiSourceKind.Constant);
            Require(constant.Attribute("Name").RawValue == "limit" && constant.Attribute("Value").RawValue == "17",
                "root constants missing");
            ShimejiSourceElement extension = root.Children.Single(e => e.Name == "Extension");
            Require(extension.Kind == ShimejiSourceKind.Unknown && extension.NamespaceName == "urn:extension"
                && extension.RawAttribute("ext:flag").NamespaceName == "urn:extension"
                && extension.RawAttribute("ext:flag").RawValue == "opaque" && extension.Text == "beforeafter"
                && extension.Children.Single().Name == "Part", "unknown namespace/text subtree lost");
        }

        private static void ScriptEnvelopes()
        {
            const string xml = "<Mascot><ActionList><Action Name='Envelopes' Type='Stay' "
                + "ActionValue='${mascot.anchor.x + 1}' FrameValue='#{mascot.lookRight}' "
                + "Padded=' ${1}' Embedded='asset-${1}.png' Trailing='${1} ' MissingBrace='${1' "
                + "EmptyAction='${}' Literal='01' Escaped='#{1 &lt; 2 &amp;&amp; true}'/></ActionList></Mascot>";
            ShimejiSourceElement action = Named(ShimejiParser.ParseActionsXml(xml).Source.Actions.Root,
                ShimejiSourceKind.Action, "Envelopes");
            Require(action.RawAttribute("ActionValue").Cache == ShimejiSourceCache.Action
                && action.RawAttribute("ActionValue").Expression == "mascot.anchor.x + 1", "action script envelope lost");
            Require(action.RawAttribute("FrameValue").Cache == ShimejiSourceCache.Frame
                && action.RawAttribute("FrameValue").Expression == "mascot.lookRight", "frame script envelope lost");
            foreach (string name in new[] { "Padded", "Embedded", "Trailing", "MissingBrace", "Literal" })
                Require(action.RawAttribute(name).Cache == ShimejiSourceCache.Literal
                    && action.RawAttribute(name).Expression == null, "literal classified as a script: " + name);
            Require(action.RawAttribute("Padded").RawValue == " ${1}" && action.RawAttribute("Trailing").RawValue == "${1} "
                && action.RawAttribute("Literal").RawValue == "01", "literal whitespace or numeric spelling rewritten");
            Require(action.RawAttribute("EmptyAction").Cache == ShimejiSourceCache.Action
                && action.RawAttribute("EmptyAction").Expression == "", "empty script envelope changed");
            Require(action.RawAttribute("Escaped").RawValue == "#{1 < 2 && true}"
                && action.RawAttribute("Escaped").Expression == "1 < 2 && true", "XML decoding changed script content");
        }

        private static void BehaviorWrappers()
        {
            ShimejiSourceElement root = ShimejiParser.ParseConfXml(ActionsXml, BehaviorsXml).Source.Behaviors.Root;
            ShimejiSourceElement list = root.Children.Single();
            Require(list.Children.Select(e => e.Kind).SequenceEqual(new[] {
                ShimejiSourceKind.Condition, ShimejiSourceKind.Behavior }), "root condition wrapper flattened");
            ShimejiSourceElement wrapper = list.Children[0];
            Require(wrapper.Attribute("Condition").RawValue == "#{mascot.anchor.y == 0}", "root wrapper expression changed");
            ShimejiSourceElement idle = wrapper.Children.Single();
            Require(idle.Attribute("Action").RawValue == "Composite" && idle.Attribute("Frequency").RawValue == "0"
                && idle.Attribute("Hidden").RawValue == "true" && idle.Attribute("Toggleable").RawValue == "true",
                "behavior routing/menu/toggle parameters lost");
            Require(idle.RawAttribute("CustomParameter").RawValue == "${mascot.anchor.x}", "behavior parameter binding lost");
            Require(idle.Children.Select(e => e.Kind).SequenceEqual(new[] {
                ShimejiSourceKind.NextBehaviorList, ShimejiSourceKind.NextBehaviorList, ShimejiSourceKind.Unknown }),
                "next-list order changed or unread NextBehavior treated as a list");
            Require(idle.Children[2].SchemaKey == null && idle.Children[2].QualifiedName == "NextBehavior",
                "unread NextBehavior declaration was recognised by source schema");
            Require(idle.Children[0].Attribute("Add").RawValue == "true" && idle.Children[1].Attribute("Add") == null
                && idle.Children[2].Attribute("Add").RawValue == "false", "next-list Add presence/value merged");
            Require(idle.Children[1].Children.Single().Kind == ShimejiSourceKind.Condition
                && idle.Children[1].Children.Single().Children.Single().Attribute("Name").RawValue == "B",
                "next-list condition wrapper flattened");
            Require(idle.Children.SelectMany(e => e.DescendantsAndSelf())
                .Where(e => e.Kind == ShimejiSourceKind.BehaviorReference)
                .Select(e => e.Attribute("Name").RawValue).SequenceEqual(new[] { "A", "B", "C" }),
                "transition candidates lost source order");
            Require(list.Children[1].Children.Count == 0, "absent next-list synthesized");
        }

        private static void Aliases()
        {
            const string actions = "<Mascot><動作リスト><動作 Name='collision' 名前='移動' 種類='静止' 枠='地面'>"
                + "<アニメーション><ポーズ 画像='/j.png' 基準座標='1,2' 長さ='3'/></アニメーション>"
                + "</動作></動作リスト></Mascot>";
            const string behaviors = "<Mascot><BehaviourList><Behaviour Name='Move' Frequency='0'>"
                + "<NextBehaviour Add='false'><BehaviourReference Name='Other' Frequency='1'/></NextBehaviour>"
                + "</Behaviour></BehaviourList></Mascot>";
            ShimejiSourceConfig source = ShimejiParser.ParseConfXml(actions, behaviors).Source;
            ShimejiSourceElement action = source.Actions.Root.Children.Single().Children.Single();
            Require(action.Kind == ShimejiSourceKind.Action && action.Name == "Action" && action.SourceName == "動作",
                "Japanese element source/canonical names lost");
            Require(action.SourceElementPath == "/Mascot[1]/動作リスト[1]/動作[1]",
                "raw source path rewrote an ancestor or element name");
            ShimejiSourceAttribute[] names = action.Attributes.Where(a => a.Name == "Name").ToArray();
            Require(names.Length == 2 && names[0].SourceName == "Name" && names[0].RawValue == "collision" && names[0].SchemaKey == null
                && names[1].SourceName == "名前" && names[1].RawValue == "移動" && names[1].SchemaKey == "Name"
                && names[0].SourceKey != names[1].SourceKey, "canonical attribute collision overwritten");
            Require(object.ReferenceEquals(action.Attribute("Name"), names[1]), "source schema resolution used first alias");
            Require(action.Attribute("Type").RawValue == "静止" && action.Attribute("BorderType").RawValue == "地面",
                "source enum value rewritten");
            ShimejiSourceElement british = source.Behaviors.Root.Children.Single().Children.Single();
            Require(british.Kind == ShimejiSourceKind.Unknown && british.SourceName == "Behaviour" && british.SchemaKey == null
                && british.Children.Single().Kind == ShimejiSourceKind.Unknown
                && british.Children.Single().Children.Single().Kind == ShimejiSourceKind.Unknown,
                "British element names that source ignores were recognised");
        }

        private static void RawSourcePaths()
        {
            const string xml = "<Mascot><ActionList><Action Name='One' Type='Stay'/>"
                + "<動作 名前='Two' 種類='静止'/><Action Name='Three' Type='Stay'/></ActionList></Mascot>";
            ShimejiSourceElement[] actions = ShimejiParser.ParseActionsXml(xml).Source.Actions.Root.Children.Single()
                .Children.ToArray();
            Require(actions.Select(e => e.SourceElementPath).SequenceEqual(new[] {
                "/Mascot[1]/ActionList[1]/Action[1]", "/Mascot[1]/ActionList[1]/動作[1]",
                "/Mascot[1]/ActionList[1]/Action[2]" }), "raw source sibling ordinals used canonical names");
            Require(actions.Select(e => e.ElementPath).SequenceEqual(new[] {
                "/Mascot[1]/ActionList[1]/Action[1]", "/Mascot[1]/ActionList[1]/Action[2]",
                "/Mascot[1]/ActionList[1]/Action[3]" }), "canonical element path identity changed");
            Require(actions.All(e => e.SourceKey == "actions.xml#" + e.ElementPath),
                "canonical source key identity changed");
        }

        private static void SchemaResolution()
        {
            const string english = "<Mascot><ActionList><Action Name='Fall' Type='Embedded' Class='org.fixture.Fall' "
                + "TargetX='1' TargetY='2' VelocityParam='3' InitialVX='4' InitialVY='5' RegistanceX='0.2' RegistanceY='0.3' "
                + "LookRight='true' IeOffsetX='6' IeOffsetY='7' BornX='8' BornY='9' BornBehavior='Sit' OffsetX='10' OffsetY='11' "
                + "BornBehaviour='ignored' TransformBehaviour='ignored' TargetBehaviour='ignored'/></ActionList></Mascot>";
            const string japanese = "<Mascot><動作リスト><動作 名前='Fall' 種類='組み込み' クラス='org.fixture.Fall' "
                + "目的地X='1' 目的地Y='2' 速度='3' 初速X='4' 初速Y='5' 空気抵抗X='0.2' 空気抵抗Y='0.3' "
                + "右向き='true' IEの端X='6' IEの端Y='7' 生まれる場所X='8' 生まれる場所Y='9' 生まれた時の行動='Sit' 端X='10' 端Y='11' "
                + "ImageRight='/right.png' Sound='/sound.wav' Volume='0.4' Hidden='true' Toggleable='true' BornMascot='Child' "
                + "TransformBehavior='Changed' TransformMascot='Next' Affordance='offer' TargetBehavior='Target'>"
                + "<アニメーション><ポーズ 画像='/left.png' 基準座標='8,16' 移動速度='0,0' 長さ='1'/>"
                + "<Hotspot Shape='Rectangle' Origin='0,0' Size='1,1' 行動='Touch'/></アニメーション></動作></動作リスト></Mascot>";
            ShimejiSourceDocument en = ShimejiParser.ParseActionsXml(english).Source.Actions;
            ShimejiSourceDocument ja = ShimejiParser.ParseActionsXml(japanese).Source.Actions;
            Require(en.Schema == ShimejiSourceSchema.Base && ja.Schema == ShimejiSourceSchema.Japanese,
                "document did not select one source schema");
            ShimejiSourceElement enAction = en.Root.Children.Single().Children.Single();
            ShimejiSourceElement jaAction = ja.Root.Children.Single().Children.Single();
            foreach (string key in new[] { "TargetX", "TargetY", "VelocityParam", "InitialVX", "InitialVY", "ResistanceX",
                "ResistanceY", "LookRight", "IeOffsetX", "IeOffsetY", "BornX", "BornY", "BornBehaviour", "OffsetX", "OffsetY" })
                Require(enAction.Attribute(key) != null && jaAction.Attribute(key) != null
                    && enAction.Attribute(key).RawValue == jaAction.Attribute(key).RawValue, "ja/base schema parameter differs: " + key);
            Require(jaAction.Attribute("ResistanceX").Name == "RegistanceX" && jaAction.Attribute("ResistanceY").Name == "RegistanceY",
                "Japanese resistance canonical name differs from the base read name");
            foreach (string key in new[] { "ImageRight", "Sound", "Volume", "Hidden", "Toggleable", "BornMascot",
                "TransformBehaviour", "TransformMascot", "Affordance", "TargetBehaviour" })
                Require(jaAction.Attribute(key) != null && jaAction.Attribute(key).SchemaKey == key,
                    "Japanese schema did not fall back to base key: " + key);
            Require(jaAction.Children.Single().Children[1].Kind == ShimejiSourceKind.Hotspot
                && jaAction.Children.Single().Children[1].Attribute("Behaviour").RawValue == "Touch",
                "Japanese fallback element or translated hotspot parameter missing");
            foreach (string rawName in new[] { "BornBehaviour", "TransformBehaviour", "TargetBehaviour" })
                Require(enAction.RawAttribute(rawName).SchemaKey == null, "unread British parameter recognised: " + rawName);

            ShimejiSourceDocument nested = ShimejiParser.ParseActionsXml(
                "<Mascot><Wrapper><動作リスト><動作 名前='Ignored' 種類='静止'/></動作リスト></Wrapper></Mascot>").Source.Actions;
            Require(nested.Schema == ShimejiSourceSchema.Base && nested.Root.Children[0].Children[0].Children[0].Kind == ShimejiSourceKind.Unknown,
                "a nested Japanese list selected the document schema");
            ShimejiSourceElement mixed = ShimejiParser.ParseActionsXml(
                "<Mascot><ActionList><動作 名前='Ignored' 種類='静止'/></ActionList></Mascot>").Source.Actions.Root.Children[0].Children[0];
            Require(mixed.Kind == ShimejiSourceKind.Unknown && mixed.SchemaKey == null && mixed.Attribute("Name") == null,
                "English schema consumed Japanese names");
        }

        private static void QualifiedNamesAndDepth()
        {
            const string xml = "<Mascot xmlns='urn:default' xmlns:a='urn:shared' xmlns:b='urn:shared'>"
                + "<a:動作リスト/><ActionList><a:Action Name='IgnoredA'/><b:Action Name='IgnoredB'/>"
                + "<Action a:Name='ignored' Name='Read' Type='Stay'/></ActionList>"
                + "<a:Extension>before<a:Part>inner</a:Part><![CDATA[after]]></a:Extension></Mascot>";
            ShimejiSourceDocument source = ShimejiParser.ParseActionsXml(xml).Source.Actions;
            Require(source.Schema == ShimejiSourceSchema.Base, "qualified Japanese list selected Japanese schema");
            ShimejiSourceElement list = source.Root.Children[1];
            Require(list.Children[0].Kind == ShimejiSourceKind.Unknown && list.Children[0].QualifiedName == "a:Action"
                && list.Children[1].Kind == ShimejiSourceKind.Unknown && list.Children[1].QualifiedName == "b:Action"
                && list.Children[0].Prefix == "a" && list.Children[1].Prefix == "b", "original qualified names or prefixes lost");
            Require(list.Children[2].Kind == ShimejiSourceKind.Action && list.Children[2].NamespaceName == "urn:default"
                && list.Children[2].Attribute("Name").RawValue == "Read"
                && list.Children[2].RawAttribute("a:Name").SchemaKey == null, "qualified attribute consumed as an unqualified key");
            Require(source.Root.RawAttribute("xmlns").IsNamespaceDeclaration
                && source.Root.RawAttribute("xmlns:a").IsNamespaceDeclaration
                && source.Root.RawAttribute("xmlns:a").Prefix == "xmlns", "namespace declarations not identified");
            Require(source.Root.Children[2].Text == "beforeafter" && source.Root.Children[2].Children[0].Text == "inner",
                "ancestor Text copied descendant text");
            string atLimit = string.Concat(Enumerable.Repeat("<N>", ShimejiParser.MaximumSourceDepth))
                + string.Concat(Enumerable.Repeat("</N>", ShimejiParser.MaximumSourceDepth));
            Require(ShimejiParser.ParseActionsXml(atLimit).Source.Actions.Root.DescendantsAndSelf().Count()
                == ShimejiParser.MaximumSourceDepth, "depth boundary refused a valid document");
            bool refused = false;
            try { ShimejiParser.ParseActionsXml("<N>" + atLimit + "</N>"); }
            catch (InvalidDataException error) { refused = error.Message.StartsWith("SOURCE_DEPTH_LIMIT:", StringComparison.Ordinal); }
            Require(refused, "source depth limit did not refuse its first invalid depth");
            string atCharacterLimit = "<N>" + new string('x', ShimejiParser.MaximumSourceCharacters - 7) + "</N>";
            Require(ShimejiParser.ParseActionsXml(atCharacterLimit).Source.Actions.Root.Text.Length
                == ShimejiParser.MaximumSourceCharacters - 7, "character boundary refused a valid document");
            bool charactersRefused = false;
            try { ShimejiParser.ParseActionsXml("<N>" + new string('x', ShimejiParser.MaximumSourceCharacters - 6) + "</N>"); }
            catch (XmlException) { charactersRefused = true; }
            Require(charactersRefused, "source character limit did not refuse its first invalid length");
        }

        private static void MaterializationBudget()
        {
            // Small inputs exercise the production preflight with a tighter limit. The conservative units
            // include ten ordinal digits per segment, three node keys and fixed record overhead.
            Require(ShimejiParser.ParseActionsXml("<N/>", null, "actions.xml", 86).Source.Actions.Root.SourceElementPath == "/N[1]",
                "materialization node boundary refused a valid document");
            MaterializationRefused("<N/>", 85, "actions.xml", "node boundary");
            MaterializationRefused("<N/>", 86, "long-actions-file.xml", "filename prefix");
            string seven = "<N>" + string.Concat(Enumerable.Repeat("<L/>", 7)) + "</N>";
            Require(ShimejiParser.ParseActionsXml(seven, null, "actions.xml", 1024).Source.Actions.Root.Children.Count == 7,
                "materialization budget refused its fan-out control");
            MaterializationRefused("<N>" + string.Concat(Enumerable.Repeat("<L/>", 8)) + "</N>", 1024, "actions.xml", "cumulative fan-out");
            MaterializationRefused("<N><N>" + string.Concat(Enumerable.Repeat("<L/>", 6)) + "</N></N>",
                1024, "actions.xml", "depth-times-count");
            const string attributes = "<N a='1' b='2' c='3'/>";
            Require(ShimejiParser.ParseActionsXml(attributes, null, "actions.xml", 257).Source.Actions.Root.Attributes.Count == 3,
                "materialization attribute boundary refused a valid document");
            MaterializationRefused(attributes, 256, "actions.xml", "attribute key boundary");
        }

        private static void MaterializationRefused(string xml, int limit, string fileName, string witness)
        {
            bool refused = false;
            try { ShimejiParser.ParseActionsXml(xml, null, fileName, limit); }
            catch (InvalidDataException error)
            { refused = error.Message.StartsWith("SOURCE_MATERIALIZATION_LIMIT:", StringComparison.Ordinal); }
            Require(refused, "materialization limit did not refuse " + witness);
        }

        private static void SourcePackContext()
        {
            ShimejiSourceConfig memory = ShimejiParser.ParseConfXml(ActionsXml, BehaviorsXml, "Set",
                "img/Set/conf/one.xml", "img/Set/conf/two.xml").Source;
            Require(memory.Actions.ImageSet == "Set" && memory.Actions.PackRelativePath == "img/Set/conf/one.xml"
                && memory.Actions.FileName == "one.xml" && memory.Behaviors.PackRelativePath == "img/Set/conf/two.xml",
                "in-memory source context missing");
            bool escaped = false;
            try { ShimejiParser.ParseActionsXml(ActionsXml, "Set", "../outside.xml"); }
            catch (ArgumentException) { escaped = true; }
            Require(escaped, "pack-relative source path accepted traversal");

            string tempRoot = Path.GetFullPath(Path.GetTempPath());
            string directory = Path.Combine(tempRoot, "source-ir-pack-" + Guid.NewGuid().ToString("N"));
            Require(string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), tempRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase), "pack fixture escaped task temp root");
            string[] directories = { "conf", "conf/Set", "img", "img/Set", "img/Set/conf" };
            var files = new List<string>();
            Directory.CreateDirectory(directory);
            foreach (string relative in directories) Directory.CreateDirectory(Path.Combine(directory, relative));
            Action<string, string> write = (relative, text) => {
                string path = Path.Combine(directory, relative);
                File.WriteAllText(path, text, new UTF8Encoding(false));
                files.Add(path);
            };
            try
            {
                const string actions = "<Mascot><ActionList><Action Name='Stand' Type='Stay'/></ActionList></Mascot>";
                const string behaviors = "<Mascot><BehaviorList><Behavior Name='Stand' Frequency='1'/></BehaviorList></Mascot>";
                write("conf/actions.xml", actions);
                write("conf/動作.xml", actions);
                write("conf/behaviors.xml", behaviors);
                write("conf/行動.xml", behaviors);
                write("conf/behaviours.xml", behaviors);
                ShimejiSourceConfig root = ShimejiParser.ParseSourcePack(directory, "Set");
                Require(root.Actions.PackRelativePath == "conf/動作.xml" && root.Behaviors.PackRelativePath == "conf/行動.xml",
                    "source root-conf filename priority differs");
                Require(ShimejiParser.ParseConfDirectory(Path.Combine(directory, "conf"), directory, "Set")
                    .Source.Actions.PackRelativePath == "conf/actions.xml", "legacy filename priority changed");
                write("conf/Set/actions.xml", actions);
                write("conf/Set/動作.xml", actions);
                write("conf/Set/behavior.xml", behaviors);
                write("conf/Set/behaviours.xml", behaviors);
                ShimejiSourceConfig perSet = ShimejiParser.ParseSourcePack(directory, "Set");
                Require(perSet.Actions.PackRelativePath == "conf/Set/actions.xml"
                    && perSet.Behaviors.PackRelativePath == "conf/Set/behavior.xml", "source per-set filename priority differs");
                write("img/Set/conf/one.xml", actions);
                write("img/Set/conf/two.xml", behaviors);
                write("img/Set/conf/info.xml", "<Mascot><Constant Name='extra' Value='9'/></Mascot>");
                ShimejiSourceConfig local = ShimejiParser.ParseSourcePack(directory, "Set");
                Require(local.Actions.PackRelativePath == "img/Set/conf/one.xml"
                    && local.Behaviors.PackRelativePath == "img/Set/conf/two.xml"
                    && local.Information.PackRelativePath == "img/Set/conf/info.xml" && local.Information.ImageSet == "Set",
                    "source image-local override or info context missing");
                Require(local.Information.Root.Children.Single().Kind == ShimejiSourceKind.Constant
                    && local.Information.Root.Line > 0, "source information document did not retain configuration/line info");
            }
            finally
            {
                foreach (string file in files) File.Delete(file);
                foreach (string relative in directories.Reverse()) Directory.Delete(Path.Combine(directory, relative), false);
                Directory.Delete(directory, false);
            }
            Require(!Directory.Exists(directory), "temporary source pack remains");
        }

        private static void ImmutableSnapshot()
        {
            ShimejiConfig config = ShimejiParser.ParseConfXml(ActionsXml, BehaviorsXml);
            ShimejiSourceElement root = config.Source.Actions.Root;
            ShimejiSourceElement pose = Named(root, ShimejiSourceKind.Action, "Motion").Children.Single().Children[0];
            config.Actions[1].Animations[0].Poses[0].Image = "/legacy-mutated.png";
            config.Actions[0].ReferencedActions.Clear();
            config.Actions.Clear();
            config.Poses.Clear();
            config.BehaviorFrequency.Clear();
            Require(pose.Attribute("Image").RawValue == "/left.png" && root.Children.Count == 3,
                "legacy mutations changed source snapshot");

            var attributes = new List<ShimejiSourceAttribute> { pose.Attribute("Image") };
            var children = new List<ShimejiSourceElement> { pose };
            var copy = new ShimejiSourceElement("Fixture", "Fixture", "", "/Fixture[1]", "manual#/Fixture[1]",
                1, 1, "text", attributes, children);
            Require(copy.SourceElementPath == copy.ElementPath, "manual source path default changed");
            attributes.Clear();
            children.Clear();
            Require(copy.Attributes.Count == 1 && copy.Children.Count == 1, "constructor kept mutable caller lists");
            bool attributesBlocked = false;
            bool childrenBlocked = false;
            try { ((IList<ShimejiSourceAttribute>)copy.Attributes).Clear(); }
            catch (NotSupportedException) { attributesBlocked = true; }
            try { ((IList<ShimejiSourceElement>)copy.Children).Clear(); }
            catch (NotSupportedException) { childrenBlocked = true; }
            Require(attributesBlocked && childrenBlocked && copy.Attributes.Count == 1 && copy.Children.Count == 1,
                "source collections accept mutation");
        }

        private static void EntryPoints()
        {
            ShimejiConfig memory = ShimejiParser.ParseActionsXml(ActionsXml);
            Require(memory.Source != null && memory.Source.Behaviors == null
                && memory.Source.Actions.FileName == "actions.xml", "actions-only entrypoint source metadata missing");
            string[] keys = memory.Source.Actions.Root.DescendantsAndSelf().Select(e => e.SourceKey).ToArray();
            ShimejiConfig compact = ShimejiParser.ParseActionsXml(ActionsXml.Replace("\r", "").Replace("\n", ""));
            Require(keys.SequenceEqual(compact.Source.Actions.Root.DescendantsAndSelf().Select(e => e.SourceKey)),
                "formatting whitespace changed structural locators");
            Require(keys.Distinct(StringComparer.Ordinal).Count() == keys.Length
                && keys.All(k => k.StartsWith("actions.xml#", StringComparison.Ordinal)), "source locators are not unique/relative");
            Require(memory.Source.Actions.Root.DescendantsAndSelf().All(e => e.Line > 0 && e.Column > 0),
                "in-memory line information absent");
            Require(ShimejiParser.ParseConfXml(ActionsXml, "<Mascot><BehaviorList/></Mascot>").Source.Behaviors != null,
                "empty behavior document treated as absent");

            string tempRoot = Path.GetFullPath(Path.GetTempPath());
            string directory = Path.Combine(tempRoot, "source-ir-" + Guid.NewGuid().ToString("N"));
            string actionsPath = Path.Combine(directory, "動作.xml");
            string behaviorsPath = Path.Combine(directory, "behaviours.xml");
            Require(string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), tempRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase), "fixture directory escaped task temp root");
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(actionsPath, ActionsXml, new UTF8Encoding(false));
                File.WriteAllText(behaviorsPath, BehaviorsXml, new UTF8Encoding(false));
                ShimejiConfig files = ShimejiParser.ParseConfDirectory(directory);
                Require(files.Source != null && files.Source.Actions.FileName == "動作.xml"
                    && files.Source.Behaviors.FileName == "behaviours.xml", "directory source filenames missing");
                Require(files.Source.Actions.Root.DescendantsAndSelf().All(e => e.Line > 0 && e.Column > 0)
                    && files.Source.Behaviors.Root.DescendantsAndSelf().All(e => e.Line > 0 && e.Column > 0),
                    "directory line information absent");
                File.Delete(behaviorsPath);
                Require(ShimejiParser.ParseConfDirectory(directory).Source.Behaviors == null,
                    "missing behavior file synthesized a source document");
            }
            finally
            {
                // Only these two files were created; an unexpected extra file prevents directory removal.
                File.Delete(actionsPath);
                File.Delete(behaviorsPath);
                Directory.Delete(directory, false);
            }
            Require(!Directory.Exists(directory), "temporary fixture directory remains");

            ShimejiConfig bundled = ShimejiParser.ParseBundledConf();
            Require(bundled.Source != null && bundled.Source.Actions != null && bundled.Source.Behaviors != null
                && bundled.Source.Actions.FileName == "base-actions.xml" && bundled.Source.Behaviors.FileName == "base-behaviors.xml",
                "bundled entrypoint source metadata missing");
            Require(bundled.Source.Actions.Root.Line > 0 && bundled.Source.Behaviors.Root.Line > 0,
                "bundled line information absent");
        }

        private const string ActionsXml = @"<Mascot xmlns=""urn:source-ir"" xmlns:ext=""urn:extension"">
  <Constant Name=""limit"" Value=""17""/>
  <ActionList>
    <Action Name=""Composite"" Type=""Sequence"" Loop=""true"" Condition=""#{mascot.anchor.x &lt; limit}"">
      <ActionReference Name=""Motion"" TargetX=""${mascot.anchor.x + 7}"" Unknown=""kept-first""/>
      <Action Type=""Select""><ActionReference Name=""Motion"" TargetX=""#{mascot.anchor.x}""/>
        <Action Type=""Embedded"" Class=""org.fixture.CustomAction"" FutureParameter=""preserve-me""/>
      </Action>
      <ActionReference Name=""Motion"" TargetX=""${mascot.anchor.x + 9}""/>
    </Action>
    <Action Name=""Motion"" Type=""Move"" BorderType=""Floor"">
      <Animation Condition=""#{mascot.lookRight}""><Pose Image=""/left.png"" ImageRight=""/right.png"" ImageAnchor=""8,16"" Velocity=""#{mascot.anchor.x},0"" Duration=""${4 + 2}"" Sound=""/tick.wav"" Volume=""0.4""/>
        <Pose Duration=""1""/><Pose Image="""" Duration=""2""/>
        <Hotspot Shape=""Rectangle"" Origin=""0,0"" Size=""3,4"" Behavior=""Touch"" Unknown=""hot""/>
      </Animation>
    </Action>
  </ActionList>
  <ext:Extension ext:flag=""opaque"">before<ext:Part/>after</ext:Extension>
</Mascot>";

        private const string BehaviorsXml = @"<Mascot>
  <BehaviorList>
    <Condition Condition=""#{mascot.anchor.y == 0}""><Behavior Name=""Idle"" Action=""Composite"" Frequency=""0"" Hidden=""true"" Toggleable=""true"" CustomParameter=""${mascot.anchor.x}"">
      <NextBehaviorList Add=""true""><BehaviorReference Name=""A"" Frequency=""1""/></NextBehaviorList>
      <NextBehaviorList><Condition Condition=""#{mascot.lookRight}""><BehaviorReference Name=""B"" Frequency=""2""/></Condition></NextBehaviorList>
      <NextBehavior Add=""false""><BehaviorReference Name=""C"" Frequency=""3""/></NextBehavior>
    </Behavior></Condition>
    <Behavior Name=""Stand"" Frequency=""5""/>
  </BehaviorList>
</Mascot>";
    }
}
