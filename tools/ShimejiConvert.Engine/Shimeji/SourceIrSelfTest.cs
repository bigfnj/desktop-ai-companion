using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

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
            Require(custom.Attribute("FutureParameter").RawValue == "preserve-me",
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
            Require(hotspot.Kind == ShimejiSourceKind.Hotspot && hotspot.Attribute("Behavior").RawValue == "Touch"
                && hotspot.Attribute("Unknown").RawValue == "hot", "hotspot or extension attributes dropped");
            ShimejiSourceElement constant = root.Children.Single(e => e.Kind == ShimejiSourceKind.Constant);
            Require(constant.Attribute("Name").RawValue == "limit" && constant.Attribute("Value").RawValue == "17",
                "root constants missing");
            ShimejiSourceElement extension = root.Children.Single(e => e.Name == "Extension");
            Require(extension.Kind == ShimejiSourceKind.Unknown && extension.NamespaceName == "urn:extension"
                && extension.Attribute("flag").NamespaceName == "urn:extension"
                && extension.Attribute("flag").RawValue == "opaque" && extension.Text == "beforeafter"
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
            Require(action.Attribute("ActionValue").Cache == ShimejiSourceCache.Action
                && action.Attribute("ActionValue").Expression == "mascot.anchor.x + 1", "action script envelope lost");
            Require(action.Attribute("FrameValue").Cache == ShimejiSourceCache.Frame
                && action.Attribute("FrameValue").Expression == "mascot.lookRight", "frame script envelope lost");
            foreach (string name in new[] { "Padded", "Embedded", "Trailing", "MissingBrace", "Literal" })
                Require(action.Attribute(name).Cache == ShimejiSourceCache.Literal
                    && action.Attribute(name).Expression == null, "literal classified as a script: " + name);
            Require(action.Attribute("Padded").RawValue == " ${1}" && action.Attribute("Trailing").RawValue == "${1} "
                && action.Attribute("Literal").RawValue == "01", "literal whitespace or numeric spelling rewritten");
            Require(action.Attribute("EmptyAction").Cache == ShimejiSourceCache.Action
                && action.Attribute("EmptyAction").Expression == "", "empty script envelope changed");
            Require(action.Attribute("Escaped").RawValue == "#{1 < 2 && true}"
                && action.Attribute("Escaped").Expression == "1 < 2 && true", "XML decoding changed script content");
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
            Require(idle.Attribute("CustomParameter").RawValue == "${mascot.anchor.x}", "behavior parameter binding lost");
            Require(idle.Children.Select(e => e.Kind).SequenceEqual(new[] {
                ShimejiSourceKind.NextBehaviorList, ShimejiSourceKind.NextBehaviorList, ShimejiSourceKind.NextBehavior }),
                "next-list forms or order changed");
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
            const string actions = "<Mascot><動作リスト><動作 名前='移動' Name='collision' 種類='静止' 枠='地面'>"
                + "<アニメーション><ポーズ 画像='/j.png' 基準座標='1,2' 長さ='3'/></アニメーション>"
                + "</動作></動作リスト></Mascot>";
            const string behaviors = "<Mascot><BehaviourList><Behaviour Name='Move' Frequency='0'>"
                + "<NextBehaviour Add='false'><BehaviourReference Name='Other' Frequency='1'/></NextBehaviour>"
                + "</Behaviour></BehaviourList></Mascot>";
            ShimejiSourceConfig source = ShimejiParser.ParseConfXml(actions, behaviors).Source;
            ShimejiSourceElement action = source.Actions.Root.Children.Single().Children.Single();
            Require(action.Kind == ShimejiSourceKind.Action && action.Name == "Action" && action.SourceName == "動作",
                "Japanese element source/canonical names lost");
            ShimejiSourceAttribute[] names = action.Attributes.Where(a => a.Name == "Name").ToArray();
            Require(names.Length == 2 && names[0].SourceName == "名前" && names[0].RawValue == "移動"
                && names[1].SourceName == "Name" && names[1].RawValue == "collision"
                && names[0].SourceKey != names[1].SourceKey, "canonical attribute collision overwritten");
            Require(object.ReferenceEquals(action.Attribute("Name"), names[0]), "first canonical attribute query changed");
            Require(action.Attribute("Type").RawValue == "静止" && action.Attribute("BorderType").RawValue == "地面",
                "source enum value rewritten");
            ShimejiSourceElement british = source.Behaviors.Root.Children.Single().Children.Single();
            Require(british.Kind == ShimejiSourceKind.Behavior && british.SourceName == "Behaviour"
                && british.Children.Single().Kind == ShimejiSourceKind.NextBehavior
                && british.Children.Single().Children.Single().Kind == ShimejiSourceKind.BehaviorReference,
                "British element aliases incomplete");
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
