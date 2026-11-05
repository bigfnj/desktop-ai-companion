using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DesktopAICompanion.Tools.ShimejiConvert.Emit;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>Exercise the real tick emitter with original synthetic XML and RGBA art.</summary>
    public static class TickPackEmitterSelfTest
    {
        public static bool Run(out string detail, string onlyCase = null)
        {
            var output = new StringBuilder("tick-pack-emitter self-test: executing conversion witnesses\n");
            var cases = new[] {
                new { Name = "graph-bindings-and-origins", Run = (Action)GraphBindings },
                new { Name = "sprite-pixels-and-identity", Run = (Action)SpritePixels },
                new { Name = "sprite-dpi72", Run = (Action)SpriteDpi72 },
                new { Name = "lifecycle-parameters", Run = (Action)LifecycleParameters },
                new { Name = "expression-refusal", Run = (Action)ExpressionRefusal },
                new { Name = "mixed-pose-geometry", Run = (Action)MixedPoseGeometry },
                new { Name = "explicit-right-geometry", Run = (Action)ExplicitRightGeometry },
                new { Name = "pair-anchor-cache", Run = (Action)PairAnchorCache },
                new { Name = "pair-anchor-preload-order", Run = (Action)PairAnchorPreloadOrder },
                new { Name = "pair-anchor-identity", Run = (Action)PairAnchorIdentity },
                new { Name = "right-only-refusal", Run = (Action)RightOnlyRefusal },
                new { Name = "right-path-refusal", Run = (Action)RightPathRefusal },
                new { Name = "existing-output-refusal", Run = (Action)ExistingOutputRefusal },
                new { Name = "private-action-reference-refusal", Run = (Action)PrivateReferenceRefusal },
                new { Name = "private-behavior-action-refusal", Run = (Action)PrivateBehaviorRefusal },
                new { Name = "composite-cycle-refusal", Run = (Action)CompositeCycleRefusal },
                new { Name = "composite-depth-refusal", Run = (Action)CompositeDepthRefusal },
                new { Name = "composite-size-refusal", Run = (Action)CompositeSizeRefusal },
                new { Name = "hidden-pack-members", Run = (Action)HiddenPackMembers },
                new { Name = "explicit-pack-metadata", Run = (Action)ExplicitPackMetadata },
                new { Name = "java-literal-refusal", Run = (Action)JavaLiteralRefusal },
                new { Name = "declaration-order", Run = (Action)DeclarationOrder },
                new { Name = "missing-add-false", Run = (Action)MissingAddFalse },
                new { Name = "duplicate-root-last-definition", Run = (Action)DuplicateRoot },
                new { Name = "enclosing-conditions", Run = (Action)EnclosingConditions },
                new { Name = "prepare-all-members", Run = (Action)PrepareAllMembers },
                new { Name = "cross-schema-refusal", Run = (Action)CrossSchemaRefusal },
                new { Name = "toggle-refusal", Run = (Action)ToggleRefusal },
                new { Name = "information-refusal", Run = (Action)InformationRefusal },
                new { Name = "own-set-binding-reset", Run = (Action)OwnSetBindingReset },
                new { Name = "action-schema-refusal", Run = (Action)ActionSchemaRefusal },
                new { Name = "ignored-reference-lists", Run = (Action)IgnoredReferenceLists },
                new { Name = "cross-member-pack-metadata", Run = (Action)CrossMemberPackMetadata },
                new { Name = "pack-source-identity", Run = (Action)PackSourceIdentity },
                new { Name = "tick-cell-cap", Run = (Action)TickCellCap },
            };
            if (onlyCase != null && !cases.Any(c => c.Name == onlyCase))
            {
                detail = "tick-pack-emitter unknown case: " + onlyCase;
                return false;
            }
            foreach (var item in cases.Where(c => onlyCase == null || c.Name == onlyCase))
            {
                try { item.Run(); output.AppendLine("  tick-pack-emitter PASS " + item.Name); }
                catch (Exception error)
                {
                    output.AppendLine("  FAIL TickPackEmitterSelfTest/" + item.Name + ": " + error.Message);
                    detail = output.ToString().TrimEnd();
                    return false;
                }
            }
            detail = output.ToString().TrimEnd();
            return true;
        }

        private static void Require(bool condition, string witness)
        { if (!condition) throw new InvalidOperationException(witness); }

        private static readonly XNamespace Ns = "https://esheep.petrucci.ch/";

        private static IEnumerable<XElement> Nodes(XDocument document)
        { return document.Root.Element(Ns + "animations").Elements(Ns + "animation"); }

        private static XElement NamedNode(XDocument document, string name)
        { return Nodes(document).Single(n => n.Element(Ns + "name").Value == name); }

        private static XElement RootBehavior(XDocument document, string name)
        {
            XElement route = Nodes(document).Single(n => n.Element(Ns + "route") != null);
            return route.Element(Ns + "sequence").Elements(Ns + "next")
                .Select(edge => Nodes(document).Single(n => n.Attribute("id").Value == edge.Value))
                .Single(n => n.Element(Ns + "behaviour").Attribute("name").Value == name);
        }

        private static string Bound(XElement owner, string tag, string name)
        {
            XElement binding = owner.Elements(Ns + tag).Single(p => p.Attribute("name").Value == name);
            Require(binding.Attribute("cache").Value == "none", "literal binding acquired an expression cache");
            return binding.Attribute("value").Value;
        }

        private static void GraphBindings()
        {
            using (var fixture = new Fixture())
            {
                XDocument document = fixture.Convert();
                XElement route = Nodes(document).Single(n => n.Element(Ns + "route") != null);
                XElement idle = RootBehavior(document, "Idle");
                XElement idleEdge = route.Element(Ns + "sequence").Elements(Ns + "next")
                    .Single(e => e.Value == idle.Attribute("id").Value);
                Require(idleEdge.Attribute("probability").Value == "0"
                    && idle.Element(Ns + "behaviour").Attribute("frequency").Value == "0",
                    "zero-frequency root became an automatic choice");
                Require(idle.Element(Ns + "behaviour").Attribute("hidden").Value == "true"
                    && Bound(idle.Element(Ns + "behaviour"), "bind", "Duration") == "99", "root behavior parameters lost");
                XElement main = RootBehavior(document, "Main");
                Require(main.Element(Ns + "behaviour").Attribute("add").Value == "false",
                    "non-additive continuation became additive");
                XElement[] next = main.Element(Ns + "sequence").Elements(Ns + "next").ToArray();
                Require(next.Length == 2 && next.Select(e => e.Attribute("probability").Value).SequenceEqual(new[] { "2", "3" }),
                    "next references were merged or reweighted");
                XElement[] targets = next.Select(e => Nodes(document).Single(n => n.Attribute("id").Value == e.Value)).ToArray();
                Require(next[0].Value != next[1].Value && next.All(e => e.Value != idle.Attribute("id").Value),
                    "next references were replaced by the root behavior");
                Require(targets.Select(n => n.Element(Ns + "behaviour").Attribute("runs").Value).SequenceEqual(new[] { "Stand", "Walk" })
                    && targets.Select(n => Bound(n.Element(Ns + "behaviour"), "bind", "TargetX")).SequenceEqual(new[] { "77", "88" })
                    && targets.Select(n => Bound(n.Element(Ns + "behaviour"), "bind", "Duration")).SequenceEqual(new[] { "5", "6" }),
                    "independent next-reference action or binding lost");
                XElement composite = NamedNode(document, "Composite");
                XElement[] children = composite.Element(Ns + "act").Element(Ns + "children").Elements(Ns + "child").ToArray();
                Require(children.Select(e => e.Attribute("animation").Value).SequenceEqual(new[] { "Walk", "Inline", "Walk" })
                    && Bound(children[0], "bind", "TargetX") == "11" && Bound(children[2], "bind", "TargetX") == "22"
                    && Bound(children[2], "bind", "Duration") == "3", "composite child order or reference binding lost");
                using (JsonDocument map = JsonDocument.Parse(File.ReadAllText(fixture.MapPath)))
                {
                    JsonElement behaviorMap = map.RootElement.GetProperty("behaviours");
                    string prefix = "A|conf/behaviors.xml|/Mascot[1]/BehaviorList[1]/Behavior[1]/NextBehaviorList[1]/BehaviorReference[";
                    Require(behaviorMap.GetProperty(next[0].Value).GetString() == prefix + "1]"
                        && behaviorMap.GetProperty(next[1].Value).GetString() == prefix + "2]",
                        "next-reference parity origins point at declarations");
                    string key = composite.Attribute("id").Value;
                    JsonElement childMap = map.RootElement.GetProperty("children");
                    string actionPrefix = "A|conf/actions.xml|/Mascot[1]/ActionList[1]/Action[1]/";
                    Require(childMap.GetProperty(key + ":0").GetString() == actionPrefix + "ActionReference[1]"
                        && childMap.GetProperty(key + ":1").GetString() == actionPrefix + "Action[1]"
                        && childMap.GetProperty(key + ":2").GetString() == actionPrefix + "ActionReference[2]",
                        "composite child parity origins lost raw source order");
                }
            }
        }

        private static void SpritePixels() { CheckSpritePixels(96, true); }
        private static void SpriteDpi72() { CheckSpritePixels(72, false); }

        private static void CheckSpritePixels(float dpi, bool alpha)
        {
            using (var fixture = new Fixture())
            {
                SavePng(Path.Combine(fixture.Pack, "img", "A", "p.png"), false, 4, dpi, alpha);
                SavePng(Path.Combine(fixture.Pack, "img", "A", "q.png"), true, 4, dpi, alpha);
                XDocument document = fixture.Convert();
                XElement image = document.Root.Element(Ns + "image");
                int tilesX = int.Parse(image.Element(Ns + "tilesx").Value), tilesY = int.Parse(image.Element(Ns + "tilesy").Value);
                Require(image.Element(Ns + "anchorx").Value == "2" && image.Element(Ns + "anchory").Value == "3",
                    "authored anchor moved to the cell bottom");
                using (JsonDocument map = JsonDocument.Parse(File.ReadAllText(fixture.MapPath)))
                using (var bytes = new MemoryStream(Convert.FromBase64String(image.Element(Ns + "png").Value)))
                using (var sheet = new Bitmap(bytes))
                {
                    JsonElement[] frames = map.RootElement.GetProperty("frames").EnumerateArray().ToArray();
                    Require(frames.Length == 2 && frames.Select(f => f.GetProperty("image").GetString())
                        .OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(new[] { "img/A/p.png", "img/A/q.png" }),
                        "frame identities lost pack-relative source image paths");
                    Require(sheet.Width / tilesX == 4 && sheet.Height / tilesY == 6, "source image was cropped or rescaled");
                    foreach (JsonElement frame in frames)
                    {
                        int index = frame.GetProperty("frame").GetInt32();
                        Require(frame.GetProperty("anchor").EnumerateArray().Select(v => v.GetInt32()).SequenceEqual(new[] { 2, 3 })
                            && frame.GetProperty("size").EnumerateArray().Select(v => v.GetInt32()).SequenceEqual(new[] { 4, 6 }),
                            "frame map lost source anchor or bounds");
                        using (var original = new Bitmap(Path.Combine(fixture.Pack, frame.GetProperty("image").GetString().Replace('/', Path.DirectorySeparatorChar))))
                        {
                            Require(Math.Abs(original.HorizontalResolution - dpi) < 0.1 && Math.Abs(original.VerticalResolution - dpi) < 0.1,
                                "sprite DPI witness did not survive PNG encoding");
                            byte[] expected = PixelBytes(original, new Rectangle(0, 0, 4, 6));
                            byte[] actual = PixelBytes(sheet, new Rectangle((index % tilesX) * 4, (index / tilesX) * 6, 4, 6));
                            Require(actual.SequenceEqual(expected), alpha ? "colored low-alpha RGBA bytes changed" : "72-DPI opaque pixel bytes changed");
                            if (alpha)
                                Require(expected[0] == 77 && expected[1] == 128 && expected[2] == 255 && expected[3] == 10
                                    && expected[4] == 50 && expected[5] == 100 && expected[6] == 200 && expected[7] == 1,
                                    "colored low-alpha witness is degenerate");
                        }
                    }
                    int p = frames.Single(f => f.GetProperty("image").GetString() == "img/A/p.png").GetProperty("frame").GetInt32();
                    int q = frames.Single(f => f.GetProperty("image").GetString() == "img/A/q.png").GetProperty("frame").GetInt32();
                    XElement[] poses = NamedNode(document, "Stand").Element(Ns + "act").Element(Ns + "variants")
                        .Element(Ns + "variant").Elements(Ns + "pose").ToArray();
                    Require(poses.Select(e => int.Parse(e.Attribute("frame").Value)).SequenceEqual(new[] { p, q, p, -1 }),
                        "reused image identity or image-less pose lost");
                    Require(poses.Select(e => e.Attribute("ticks").Value).SequenceEqual(new[] { "2", "3", "4", "1" }),
                        "pose timing was flattened");
                }
            }
        }

        private static void LifecycleParameters()
        {
            using (var fixture = new Fixture())
            {
                XDocument document = fixture.Convert();
                Require(document.Root.Element(Ns + "header").Element(Ns + "features").Value.Split(' ').Contains("lifecycle"),
                    "lifecycle feature token missing");
                XElement breed = NamedNode(document, "Born").Element(Ns + "act");
                Require(breed.Attribute("class").Value == "Breed" && Bound(breed, "param", "BornX") == "7"
                    && Bound(breed, "param", "BornY") == "5" && Bound(breed, "param", "BornBehavior") == "Idle"
                    && !breed.Elements(Ns + "param").Any(p => p.Attribute("name").Value == "BornMascot") && Bound(breed, "param", "BornCount") == "2"
                    && Bound(breed, "param", "BornTransient") == "true" && Bound(breed, "param", "Duration") == "4",
                    "Breed lifecycle parameters lost");
                XElement transform = NamedNode(document, "Change").Element(Ns + "act");
                Require(transform.Attribute("class").Value == "Transform" && !transform.Elements(Ns + "param").Any(p => p.Attribute("name").Value == "TransformMascot")
                    && Bound(transform, "param", "TransformBehavior") == "Idle" && Bound(transform, "param", "Duration") == "3",
                    "Transform lifecycle parameters lost");
            }
        }

        private static string Refusal(Fixture fixture, string expected)
        {
            try { TickPackEmitter.ConvertPack(fixture.Pack, fixture.Output); }
            catch (InvalidDataException error)
            {
                Require(error.Message.IndexOf(expected, StringComparison.Ordinal) >= 0, "wrong refusal: " + error.Message);
                return error.Message;
            }
            throw new InvalidOperationException("conversion accepted an input requiring refusal: " + expected);
        }

        private static void ExpressionRefusal()
        {
            using (var fixture = new Fixture())
            {
                XDocument actions = BuildActions();
                actions.Descendants("ActionReference").First().SetAttributeValue("TargetX", "${1 + 2}");
                fixture.Actions(actions);
                string message = Refusal(fixture, "expressions are not lowered yet");
                Require(message.IndexOf("actions.xml#", StringComparison.Ordinal) >= 0
                    && message.IndexOf("ActionReference[1]", StringComparison.Ordinal) >= 0,
                    "expression refusal lost its source locator");
                Require(!Directory.Exists(fixture.Output), "refused expression left a partial output");
            }
        }

        private static void MixedPoseGeometry()
        {
            using (var fixture = new Fixture())
            {
                SavePng(Path.Combine(fixture.Pack, "img", "A", "q.png"), true, 7, height: 4);
                XDocument source = BuildActions();
                foreach (XElement pose in source.Descendants("Pose").Where(p => (string)p.Attribute("Image") == "/q.png"))
                    pose.SetAttributeValue("ImageAnchor", "-2,9");
                fixture.Actions(source);
                XDocument document = fixture.Convert();
                XElement[] poses = NamedNode(document, "Stand").Element(Ns + "act").Element(Ns + "variants")
                    .Element(Ns + "variant").Elements(Ns + "pose").ToArray();
                Require(Geometry(poses[0], "ax", "ay", "w", "h").SequenceEqual(new[] { 2, 3, 4, 6 })
                    && Geometry(poses[1], "ax", "ay", "w", "h").SequenceEqual(new[] { -2, 9, 7, 4 }),
                    "mixed pose geometry lost each image's authored anchor or dimensions");
                Require(poses[3].Attribute("frame").Value == "-1" && poses[3].Attribute("right") == null,
                    "image-less pose acquired a visible frame");
                using (JsonDocument map = JsonDocument.Parse(File.ReadAllText(fixture.MapPath)))
                    foreach (JsonElement frame in map.RootElement.GetProperty("frames").EnumerateArray())
                    {
                        int[] anchor = frame.GetProperty("image").GetString() == "img/A/q.png" ? new[] { -2, 9 } : new[] { 2, 3 };
                        Require(frame.GetProperty("anchor").EnumerateArray().Select(v => v.GetInt32()).SequenceEqual(anchor),
                            "mixed frame map replaced authored anchors with a shared default");
                    }
                CheckGeometrySheet(fixture, document, 7, 6);
            }
        }

        private static void ExplicitRightGeometry()
        {
            using (var fixture = new Fixture())
            {
                SavePng(Path.Combine(fixture.Pack, "img", "A", "q.png"), true, 7, height: 4);
                XDocument source = BuildActions();
                XElement pose = source.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Stand")
                    .Element("Animation").Elements("Pose").First();
                pose.SetAttributeValue("ImageAnchor", "1,5");
                pose.SetAttributeValue("ImageRight", "/q.png");
                fixture.Actions(source);
                XDocument document = fixture.Convert();
                XElement emitted = NamedNode(document, "Stand").Element(Ns + "act").Element(Ns + "variants")
                    .Element(Ns + "variant").Elements(Ns + "pose").First();
                Require(Geometry(emitted, "ax", "ay", "w", "h", "rw", "rh")
                    .SequenceEqual(new[] { 1, 5, 4, 6, 7, 4 }),
                    "explicit right image lost left anchor or independent right dimensions");
                using (JsonDocument map = JsonDocument.Parse(File.ReadAllText(fixture.MapPath)))
                {
                    JsonElement[] frames = map.RootElement.GetProperty("frames").EnumerateArray().ToArray();
                    int left = int.Parse(emitted.Attribute("frame").Value), right = int.Parse(emitted.Attribute("right").Value);
                    Require(left != right && frames.Single(f => f.GetProperty("frame").GetInt32() == left).GetProperty("image").GetString() == "img/A/p.png"
                        && frames.Single(f => f.GetProperty("frame").GetInt32() == right).GetProperty("image").GetString() == "img/A/q.png",
                        "explicit right pose points at a left or mirrored source frame");
                    Require(frames.Where(f => f.GetProperty("frame").GetInt32() == left || f.GetProperty("frame").GetInt32() == right)
                        .All(f => f.GetProperty("anchor").EnumerateArray().Select(v => v.GetInt32()).SequenceEqual(new[] { 1, 5 })),
                        "explicit pair frame map lost the authored left anchor");
                }
                CheckGeometrySheet(fixture, document, 7, 6);
            }
        }

        private static void PairAnchorCache()
        {
            using (var fixture = new Fixture())
            {
                XDocument source = BuildActions();
                XElement animation = source.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Stand").Element("Animation");
                animation.RemoveNodes();
                XElement first = Pose("/q.png", 2), second = Pose("/q.png", 3);
                first.SetAttributeValue("ImageAnchor", "1,5"); second.SetAttributeValue("ImageAnchor", "-3,8");
                first.SetAttributeValue("ImageRight", "/p.png"); second.SetAttributeValue("ImageRight", "/p.png");
                animation.Add(first, second);
                // Other actions must not preload this pair with the fixture's default anchor.
                foreach (XElement other in source.Descendants("Action").Where(a => (string)a.Attribute("Name") != "Stand").Descendants("Pose"))
                    if ((string)other.Attribute("Image") == "/q.png") other.SetAttributeValue("Image", "/p.png");
                fixture.Actions(source);
                XDocument document = fixture.Convert();
                XElement[] poses = NamedNode(document, "Stand").Element(Ns + "act").Element(Ns + "variants")
                    .Element(Ns + "variant").Elements(Ns + "pose").ToArray();
                Require(poses.Length == 2 && poses.All(p => Geometry(p, "ax", "ay").SequenceEqual(new[] { 1, 5 })),
                    "same image-pair identity did not retain its first loaded anchor");
                using (JsonDocument map = JsonDocument.Parse(File.ReadAllText(fixture.MapPath)))
                {
                    JsonElement[] frames = map.RootElement.GetProperty("frames").EnumerateArray()
                        .Where(f => f.GetProperty("image").GetString() == "img/A/q.png").ToArray();
                    Require(frames.Length == 1 && frames[0].GetProperty("anchor").EnumerateArray().Select(v => v.GetInt32()).SequenceEqual(new[] { 1, 5 })
                        && poses.Select(p => p.Attribute("frame").Value).Distinct().Count() == 1,
                        "pair-cache parity map disagrees with the effective pose anchor or duplicates its frame");
                    int right = int.Parse(poses[0].Attribute("right").Value);
                    JsonElement[] rightFrames = map.RootElement.GetProperty("frames").EnumerateArray()
                        .Where(f => f.GetProperty("frame").GetInt32() == right).ToArray();
                    Require(rightFrames.Length == 1 && poses.Select(p => p.Attribute("right").Value).Distinct().Count() == 1
                        && rightFrames[0].GetProperty("anchor").EnumerateArray().Select(v => v.GetInt32()).SequenceEqual(new[] { 1, 5 }),
                        "pair-cache right parity map disagrees with the effective pose anchor or duplicates its frame");
                }
            }
        }

        private static IEnumerable<int> Geometry(XElement pose, params string[] names)
        { return names.Select(name => int.Parse(pose.Attribute(name).Value)); }

        private static void PairAnchorPreloadOrder()
        {
            using (var fixture = new Fixture())
            {
                XDocument source = BuildActions();
                XElement direct = Pose("/q.png", 1), nested = Pose("/q.png", 1);
                direct.SetAttributeValue("ImageAnchor", "8,-4"); nested.SetAttributeValue("ImageAnchor", "-3,9");
                // It is unused and the nested child precedes the direct animation in XML.
                // Source construction still loads the direct animation before the child's pair.
                source.Root.Element("ActionList").AddFirst(S("Action", A("Name", "UnusedPreload"), A("Type", "Sequence"),
                    S("Action", A("Name", "PreloadChild"), A("Type", "Stay"), S("Animation", nested)),
                    S("Animation", direct)));
                fixture.Actions(source);
                XDocument document = fixture.Convert();
                XElement stand = NamedNode(document, "Stand").Element(Ns + "act").Element(Ns + "variants").Element(Ns + "variant");
                XElement q = stand.Elements(Ns + "pose").ElementAt(1);
                Require(Geometry(q, "ax", "ay").SequenceEqual(new[] { 8, -4 }),
                    "unused action or direct-before-nested pair preload order was ignored");
                XElement child = NamedNode(document, "PreloadChild").Element(Ns + "act").Element(Ns + "variants").Element(Ns + "variant").Element(Ns + "pose");
                Require(Geometry(child, "ax", "ay").SequenceEqual(new[] { 8, -4 }),
                    "nested action replaced the pair anchor loaded by its parent's direct animation");
            }
        }

        private static void RightOnlyRefusal()
        {
            using (var fixture = new Fixture())
            {
                XDocument source = BuildActions();
                source.Descendants("Pose").Single(p => p.Attribute("Image") == null).SetAttributeValue("ImageRight", "/q.png");
                fixture.Actions(source);
                Refusal(fixture, "TICK_RIGHT_ONLY_UNSUPPORTED");
                Require(!Directory.Exists(fixture.Output), "right-only refusal left a partial output");
            }
        }

        private static void PairAnchorIdentity()
        {
            using (var fixture = new Fixture())
            {
                // Register this exact fixture path so cleanup includes its containing directory.
                fixture.Write("img/A/sub/p.png", "");
                SavePng(Path.Combine(fixture.Pack, "img", "A", "sub", "p.png"), false);
                XElement plain = Pose("/p.png", 1), alias = Pose("p.png", 1), explicitSame = Pose("/p.png", 1),
                    upperCase = Pose("/P.png", 1), repeated = Pose("/sub//p.png", 1), collapsed = Pose("/sub/p.png", 1),
                    explicitAlias = Pose("/p.png", 1);
                plain.SetAttributeValue("ImageAnchor", "1,5"); alias.SetAttributeValue("ImageAnchor", "-2,6");
                explicitSame.SetAttributeValue("ImageAnchor", "2,4"); explicitSame.SetAttributeValue("ImageRight", "/p.png");
                upperCase.SetAttributeValue("ImageAnchor", "3,3");
                repeated.SetAttributeValue("ImageAnchor", "4,2"); collapsed.SetAttributeValue("ImageAnchor", "9,8");
                explicitAlias.SetAttributeValue("ImageAnchor", "8,9"); explicitAlias.SetAttributeValue("ImageRight", "//p.png");
                fixture.Actions(new XDocument(S("Mascot", S("ActionList", S("Action", A("Name", "Stand"), A("Type", "Stay"),
                    S("Animation", plain, alias, explicitSame, upperCase, repeated, collapsed, explicitAlias))))));
                fixture.Behaviors(new XDocument(S("Mascot", S("BehaviorList", S("Behavior", A("Name", "Main"),
                    A("Action", "Stand"), A("Frequency", 1))))));
                XDocument document = fixture.Convert();
                XElement[] poses = NamedNode(document, "Stand").Element(Ns + "act").Element(Ns + "variants")
                    .Element(Ns + "variant").Elements(Ns + "pose").ToArray();
                Require(poses.Length == 7 && Geometry(poses[0], "ax", "ay").SequenceEqual(new[] { 1, 5 })
                    && Geometry(poses[1], "ax", "ay").SequenceEqual(new[] { 1, 5 }),
                    "image-pair left leading separator aliases acquired independent anchors");
                Require(Geometry(poses[2], "ax", "ay").SequenceEqual(new[] { 2, 4 })
                    && Geometry(poses[6], "ax", "ay").SequenceEqual(new[] { 2, 4 })
                    && poses[2].Attribute("right") != null && poses[6].Attribute("right") != null,
                    "image-pair absent right and explicit same right shared an anchor or lost explicit pixels");
                Require(Geometry(poses[3], "ax", "ay").SequenceEqual(new[] { 3, 3 }),
                    "image-pair cache key lost Java ordinal case sensitivity");
                Require(Geometry(poses[4], "ax", "ay").SequenceEqual(new[] { 4, 2 })
                    && Geometry(poses[5], "ax", "ay").SequenceEqual(new[] { 4, 2 }),
                    "image-pair repeated internal separators acquired independent anchors");
            }
        }

        private static void RightPathRefusal()
        {
            using (var fixture = new Fixture())
            {
                XDocument source = BuildActions();
                source.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Stand")
                    .Element("Animation").Elements("Pose").First().SetAttributeValue("ImageRight", "q.png");
                fixture.Actions(source);
                Refusal(fixture, "TICK_RIGHT_PATH_UNSUPPORTED");
                Require(!Directory.Exists(fixture.Output), "member-relative right-path refusal left a partial output");
            }
        }

        private static void CheckGeometrySheet(Fixture fixture, XDocument document, int width, int height)
        {
            XElement image = document.Root.Element(Ns + "image");
            int tilesX = int.Parse(image.Element(Ns + "tilesx").Value), tilesY = int.Parse(image.Element(Ns + "tilesy").Value);
            using (JsonDocument map = JsonDocument.Parse(File.ReadAllText(fixture.MapPath)))
            using (var bytes = new MemoryStream(Convert.FromBase64String(image.Element(Ns + "png").Value)))
            using (var sheet = new Bitmap(bytes))
            {
                Require(sheet.Width / tilesX == width && sheet.Height / tilesY == height,
                    "mixed sheet cell is not the maximum source width and height");
                foreach (JsonElement frame in map.RootElement.GetProperty("frames").EnumerateArray())
                {
                    int index = frame.GetProperty("frame").GetInt32();
                    using (var original = new Bitmap(Path.Combine(fixture.Pack, frame.GetProperty("image").GetString().Replace('/', Path.DirectorySeparatorChar))))
                    {
                        Require(frame.GetProperty("size").EnumerateArray().Select(v => v.GetInt32()).SequenceEqual(new[] { original.Width, original.Height }),
                            "mixed frame map replaced authored dimensions with cell dimensions");
                        var cell = new Rectangle((index % tilesX) * width, (index / tilesX) * height, width, height);
                        Require(PixelBytes(sheet, new Rectangle(cell.X, cell.Y, original.Width, original.Height))
                            .SequenceEqual(PixelBytes(original, new Rectangle(0, 0, original.Width, original.Height))),
                            "mixed sprite pixels are not copied at the cell top-left");
                        if (original.Width < width)
                            Require(PixelBytes(sheet, new Rectangle(cell.X + original.Width, cell.Y, width - original.Width, height)).All(b => b == 0),
                                "mixed sprite right padding is not transparent");
                        if (original.Height < height)
                            Require(PixelBytes(sheet, new Rectangle(cell.X, cell.Y + original.Height, original.Width, height - original.Height)).All(b => b == 0),
                                "mixed sprite bottom padding is not transparent");
                    }
                }
            }
        }

        private static void ExistingOutputRefusal()
        {
            using (var fixture = new Fixture())
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fixture.XmlPath));
                File.WriteAllText(fixture.XmlPath, "owner xml", new UTF8Encoding(false));
                File.WriteAllText(fixture.MapPath, "owner map", new UTF8Encoding(false));
                Refusal(fixture, "TICK_OUTPUT_EXISTS");
                Require(File.ReadAllText(fixture.XmlPath) == "owner xml" && File.ReadAllText(fixture.MapPath) == "owner map",
                    "existing output changed during refusal");
            }
        }

        private static void PrivateReferenceRefusal()
        {
            using (var fixture = new Fixture())
            {
                XDocument actions = BuildActions();
                actions.Descendants("Action").First().Add(S("ActionReference", A("Name", "Inline")));
                fixture.Actions(actions);
                Refusal(fixture, "missing global action 'Inline'");
                Require(!Directory.Exists(fixture.Output), "private-action reference refusal left a partial output");
            }
        }

        private static void PrivateBehaviorRefusal()
        {
            using (var fixture = new Fixture())
            {
                XDocument behaviors = BuildBehaviors();
                behaviors.Descendants("Behavior").First().SetAttributeValue("Action", "Inline");
                fixture.Behaviors(behaviors);
                Refusal(fixture, "behaviour names missing global action 'Inline'");
                Require(!Directory.Exists(fixture.Output), "private behavior action refusal left a partial output");
            }
        }

        private static void JavaLiteralRefusal()
        {
            using (var fixture = new Fixture())
            {
                XDocument actions = BuildActions();
                actions.Descendants("Action").First().SetAttributeValue("Duration", "2f");
                fixture.Actions(actions);
                XDocument document = fixture.Convert();
                Require(Bound(NamedNode(document, "Composite").Element(Ns + "act"), "param", "Duration") == "2f",
                    "supported Java decimal suffix was refused or changed");
            }
            foreach (string literal in new[] { "0x1p4", "0x1p-4" })
                using (var fixture = new Fixture())
                {
                    XDocument actions = BuildActions();
                    actions.Descendants("Action").First().SetAttributeValue("Duration", literal);
                    fixture.Actions(actions);
                    Refusal(fixture, "TICK_LITERAL_UNSUPPORTED");
                    Require(!Directory.Exists(fixture.Output), "Java-only literal refusal left a partial output");
                }
        }

        private static string[] RouteNames(XDocument document)
        {
            XElement route = Nodes(document).Single(n => n.Element(Ns + "route") != null);
            return route.Element(Ns + "sequence").Elements(Ns + "next")
                .Select(e => Nodes(document).Single(n => n.Attribute("id").Value == e.Value)
                    .Element(Ns + "behaviour").Attribute("name").Value).ToArray();
        }

        private static void DeclarationOrder()
        {
            using (var fixture = new Fixture())
                Require(RouteNames(fixture.Convert()).SequenceEqual(new[] { "Main", "Idle", "BornBehavior", "ChangeBehavior" }),
                    "root route changed declaration order");
        }

        private static void MissingAddFalse()
        {
            using (var fixture = new Fixture())
            {
                XDocument source = BuildBehaviors();
                source.Descendants("NextBehaviorList").First().Attribute("Add").Remove();
                fixture.Behaviors(source);
                Require(RootBehavior(fixture.Convert(), "Main").Element(Ns + "behaviour").Attribute("add").Value == "false",
                    "present next list with missing Add became additive");
            }
        }

        private static void DuplicateRoot()
        {
            using (var fixture = new Fixture())
            {
                XDocument source = BuildBehaviors();
                source.Root.Element("BehaviorList").Add(S("Behavior", A("Name", "Main"), A("Action", "Walk"),
                    A("Frequency", 23), A("Duration", 101)));
                fixture.Behaviors(source);
                XDocument document = fixture.Convert();
                Require(RouteNames(document).SequenceEqual(new[] { "Main", "Idle", "BornBehavior", "ChangeBehavior" }),
                    "duplicate root did not retain first position");
                XElement root = RootBehavior(document, "Main");
                XElement behavior = root.Element(Ns + "behaviour");
                Require(behavior.Attribute("runs").Value == "Walk" && behavior.Attribute("frequency").Value == "23"
                    && Bound(behavior, "bind", "Duration") == "101" && behavior.Attribute("add").Value == "true",
                    "duplicate root did not use last definition");
                using (JsonDocument map = JsonDocument.Parse(File.ReadAllText(fixture.MapPath)))
                    Require(map.RootElement.GetProperty("behaviours").GetProperty(root.Attribute("id").Value).GetString()
                        == "A|conf/behaviors.xml|/Mascot[1]/BehaviorList[1]/Behavior[5]", "duplicate root refKey points at discarded definition");
            }
        }

        private static void EnclosingConditions()
        {
            using (var fixture = new Fixture())
            {
                XDocument source = BuildBehaviors();
                XElement main = source.Descendants("Behavior").First();
                main.SetAttributeValue("Condition", "true");
                main.ReplaceWith(S("Condition", A("Condition", "false"), new XElement(main)));
                main = source.Descendants("Behavior").First();
                XElement list = main.Element("NextBehaviorList");
                XElement[] references = list.Elements().ToArray();
                references[0].SetAttributeValue("Condition", "true");
                foreach (XElement reference in references) reference.Remove();
                list.Add(S("Condition", A("Condition", "false"), references));
                fixture.Behaviors(source);
                XDocument document = fixture.Convert();
                XElement root = RootBehavior(document, "Main");
                XElement route = Nodes(document).Single(n => n.Element(Ns + "route") != null);
                XElement edge = route.Element(Ns + "sequence").Elements(Ns + "next").Single(e => e.Value == root.Attribute("id").Value);
                Require(edge.Attribute("when").Value == "(false) && (true)", "enclosing root condition was dropped");
                Require(root.Element(Ns + "sequence").Elements(Ns + "next").First().Attribute("when").Value == "(false) && (true)",
                    "enclosing next-reference condition was dropped");
            }
        }

        private static void PrepareAllMembers()
        {
            using (var fixture = new Fixture())
            {
                fixture.Member("B");
                XDocument actions = BuildActions();
                actions.Descendants("Action").First().SetAttributeValue("Duration", "${2}");
                fixture.Write("img/B/conf/actions.xml", actions.ToString());
                fixture.Write("img/B/conf/behaviors.xml", BuildBehaviors().ToString());
                Refusal(fixture, "expressions are not lowered yet");
                Require(!Directory.Exists(fixture.Output), "member A was written before invalid member B was prepared");
            }
        }

        private static void CrossSchemaRefusal()
        {
            using (var fixture = new Fixture())
            {
                fixture.Behaviors(new XDocument(S("Mascot", S("行動リスト", S("行動", A("名前", "Main"),
                    A("動作", "Stand"), A("頻度", 1), A("長さ", 2))))));
                Refusal(fixture, "cross-schema behaviour bindings");
                Require(!Directory.Exists(fixture.Output), "cross-schema refusal left output");
            }
        }

        private static void ToggleRefusal()
        {
            using (var fixture = new Fixture())
            {
                XDocument source = BuildBehaviors();
                source.Descendants("Behavior").First().SetAttributeValue("Toggleable", "true");
                fixture.Behaviors(source);
                Refusal(fixture, "toggleable behaviours are not lowered yet");
                Require(!Directory.Exists(fixture.Output), "toggle refusal left output");
            }
        }

        private static void InformationRefusal()
        {
            using (var fixture = new Fixture())
            {
                fixture.Write("conf/info.xml", "<Mascot/>");
                Refusal(fixture, "information metadata is not lowered yet");
                Require(!Directory.Exists(fixture.Output), "information refusal left output");
            }
        }

        private static void OwnSetBindingReset()
        {
            using (var fixture = new Fixture())
            {
                fixture.Member("B");
                XDocument source = BuildActions();
                XElement composite = source.Descendants("Action").First();
                composite.RemoveNodes();
                composite.Add(S("ActionReference", A("Name", "Born"), A("BornMascot", "A")));
                source.Descendants("Action").Single(e => (string)e.Attribute("Name") == "Born").SetAttributeValue("BornMascot", "B");
                fixture.Actions(source);
                XDocument document = fixture.Convert("A");
                Require(Bound(NamedNode(document, "Born").Element(Ns + "act"), "param", "BornMascot") == "B",
                    "foreign default member was lost");
                XElement child = NamedNode(document, "Composite").Element(Ns + "act").Element(Ns + "children").Element(Ns + "child");
                Require(child.Elements(Ns + "bind").Count(p => p.Attribute("name").Value == "BornMascot") == 1,
                    "own-set binding reset to a foreign default was dropped");
                Require(Bound(child, "bind", "BornMascot") == "A", "own-set binding reset to a foreign default was dropped");
            }
        }

        private static void CompositeCycleRefusal()
        {
            using (var fixture = new Fixture())
            {
                XDocument source = BuildActions();
                source.Descendants("Action").First().Add(S("ActionReference", A("Name", "Composite")));
                fixture.Actions(source);
                string error = Refusal(fixture, "TICK_COMPOSITE_CYCLE");
                Require(error.Contains("actions.xml") && error.Contains("/Mascot[1]/ActionList[1]/Action["),
                    "composite cycle refusal lost its source locator");
                Require(!Directory.Exists(fixture.Output), "composite cycle refusal wrote output");
            }
        }

        private static void ConfigureGraph(Fixture fixture, IEnumerable<XElement> actions, string entry)
        {
            fixture.Actions(new XDocument(S("Mascot", S("ActionList", actions))));
            fixture.Behaviors(new XDocument(S("Mascot", S("BehaviorList",
                S("Behavior", A("Name", "Main"), A("Action", entry), A("Frequency", 1))))));
        }

        private static IEnumerable<XElement> DepthGraph(int depth, bool leafFirst)
        {
            var actions = new List<XElement>();
            for (int i = 0; i < depth - 1; i++)
                actions.Add(S("Action", A("Name", "Depth" + i), A("Type", "Sequence"),
                    S("ActionReference", A("Name", i == depth - 2 ? "Stand" : "Depth" + (i + 1)))));
            actions.Add(S("Action", A("Name", "Stand"), A("Type", "Stay"), S("Animation", Pose("/p.png", 1))));
            if (leafFirst) actions.Reverse();
            return actions;
        }

        private static void CompositeDepthRefusal()
        {
            foreach (bool leafFirst in new[] { false, true })
            {
                using (var positive = new Fixture())
                {
                    ConfigureGraph(positive, DepthGraph(32, leafFirst), "Depth0");
                    Require(positive.Convert().Root != null, "32-deep activation boundary was refused");
                }
                using (var invalid = new Fixture())
                {
                    ConfigureGraph(invalid, DepthGraph(33, leafFirst), "Depth0");
                    string error = Refusal(invalid, "TICK_COMPOSITE_DEPTH");
                    Require(error.Contains("actions.xml"), "composite depth refusal lost its source locator");
                    Require(!Directory.Exists(invalid.Output), "composite depth refusal wrote output");
                }
            }
        }

        private static IEnumerable<XElement> SizeGraph(bool tooLarge)
        {
            XElement root = S("Action", A("Name", "Wide"), A("Type", "Sequence"),
                S("ActionReference", A("Name", "Double10")), S("ActionReference", A("Name", "Double10")),
                S("ActionReference", A("Name", "Stand")));
            if (tooLarge) root.Add(S("ActionReference", A("Name", "Stand")));
            var actions = new List<XElement> { root };
            for (int i = 10; i >= 1; i--)
            {
                string child = i == 1 ? "Stand" : "Double" + (i - 1);
                actions.Add(S("Action", A("Name", "Double" + i), A("Type", "Sequence"),
                    S("ActionReference", A("Name", child)), S("ActionReference", A("Name", child))));
            }
            actions.Add(S("Action", A("Name", "Stand"), A("Type", "Stay"), S("Animation", Pose("/p.png", 1))));
            return actions;
        }

        private static void CompositeSizeRefusal()
        {
            using (var positive = new Fixture())
            {
                ConfigureGraph(positive, SizeGraph(false), "Wide");
                Require(positive.Convert().Root != null, "4096-action activation boundary was refused");
            }
            using (var invalid = new Fixture())
            {
                ConfigureGraph(invalid, SizeGraph(true), "Wide");
                string error = Refusal(invalid, "TICK_COMPOSITE_SIZE");
                Require(error.Contains("actions.xml"), "composite size refusal lost its source locator");
                Require(!Directory.Exists(invalid.Output), "composite size refusal wrote output");
            }
        }

        /// <summary>A tick member's cell is its largest image at the source's own size up to the host's TICK cap (768, past
        /// the classic 256), graded by the host validator as a tick companion; one pixel past the cap is refused by name.</summary>
        private static void TickCellCap()
        {
            using (var wide = new Fixture())
            {
                SavePng(Path.Combine(wide.Pack, "img", "A", "q.png"), true, 650, height: 320);
                XDocument document = wide.Convert();
                XElement image = document.Root.Element(Ns + "image");
                int tilesX = int.Parse(image.Element(Ns + "tilesx").Value), tilesY = int.Parse(image.Element(Ns + "tilesy").Value);
                using (var bytes = new MemoryStream(Convert.FromBase64String(image.Element(Ns + "png").Value)))
                using (var sheet = new Bitmap(bytes))
                    Require(sheet.Width / tilesX == 650 && sheet.Height / tilesY == 320,
                        "tick cell cap: a 650 x 320 image was not kept at its own size (cell " + sheet.Width / tilesX + "x" + sheet.Height / tilesY + ")");
            }
            using (var over = new Fixture())
            {
                SavePng(Path.Combine(over.Pack, "img", "A", "q.png"), true, SpriteSheetBuilder.MaxTickCell + 1);
                Refusal(over, "Tick sheet requires cells outside 1.." + SpriteSheetBuilder.MaxTickCell + ";");
                Require(!Directory.Exists(over.Output), "tick cell cap: the refused member wrote output");
            }
        }

        private static void HiddenPackMembers()
        {
            foreach (string name in new[] { ".Ball", "unused", "UNUSED", "UnusedSide", "Visible" })
            {
                using (var fixture = new Fixture())
                {
                    fixture.Member(name);
                    fixture.Convert("A");
                    XElement pack = XDocument.Load(Path.Combine(fixture.Output, name, "animations.xml")).Root
                        .Element(Ns + "header").Element(Ns + "pack");
                    bool hidden = name == ".Ball" || name == "unused" || name == "UNUSED";
                    Require((string)pack.Attribute("hidden") == (hidden ? "true" : "false"),
                        "source chooser visibility was lost for member " + name);
                    Require((string)pack.Attribute("role") == (hidden ? "helper" : "form"),
                        "source chooser helper/form role was lost for member " + name);
                }
            }
        }

        private static void ExplicitPackMetadata()
        {
            using (var fixture = new Fixture())
            {
                ConfigureGraph(fixture, new[] { S("Action", A("Name", "Stand"), A("Type", "Stay"), S("Animation", Pose("/p.png", 1))) }, "Stand");
                XDocument document = fixture.Convert("A");
                XElement header = document.Root.Element(Ns + "header"), pack = header.Element(Ns + "pack");
                Require(pack != null && (string)pack.Attribute("member") == "A" && (string)pack.Attribute("role") == "main"
                    && header.Element(Ns + "features").Value.Split(' ').Contains("packs"),
                    "explicit main request lost its pack metadata");
            }
            using (var fixture = new Fixture())
            {
                fixture.Member(".Ball");
                ConfigureGraph(fixture, new[] { S("Action", A("Name", "Stand"), A("Type", "Stay"), S("Animation", Pose("/p.png", 1))) }, "Stand");
                Refusal(fixture, "TICK_PACK_MAIN_REQUIRED");
                Require(!Directory.Exists(fixture.Output), "hidden member without a main wrote output");
                fixture.Convert("A");
                XElement helper = XDocument.Load(Path.Combine(fixture.Output, ".Ball", "animations.xml")).Root
                    .Element(Ns + "header").Element(Ns + "pack");
                Require(helper != null && (string)helper.Attribute("hidden") == "true" && (string)helper.Attribute("role") == "helper",
                    "hidden member without cross references lost its chooser visibility metadata");
            }
        }

        private static void ActionSchemaRefusal()
        {
            using (var fixture = new Fixture())
            {
                // English enum values are deliberate: removing the document-schema guard
                // would otherwise emit these values under the wrong runtime enum vocabulary.
                fixture.Actions(new XDocument(S("Mascot", S("動作リスト", S("動作", A("名前", "Stand"),
                    A("種類", "Stay"), A("枠", "Floor"), S("アニメーション", S("ポーズ", A("画像", "/p.png"),
                        A("基準座標", "2,3"), A("移動速度", "0,0"), A("長さ", 2))))))));
                fixture.Behaviors(new XDocument(S("Mascot", S("BehaviorList", S("Behavior", A("Name", "Main"),
                    A("Action", "Stand"), A("Frequency", 1))))));
                Refusal(fixture, "TICK_SCHEMA_UNSUPPORTED");
                Require(!Directory.Exists(fixture.Output), "action-schema refusal left output");
            }
        }

        private static void IgnoredReferenceLists()
        {
            using (var fixture = new Fixture())
            {
                XDocument source = BuildBehaviors();
                source.Descendants("BehaviorReference").First().Add(S("NextBehaviorList", A("Add", false),
                    S("BehaviorReference", A("Name", "Ghost"), A("Action", "Stand"), A("Frequency", 1))));
                fixture.Behaviors(source);
                XDocument document = fixture.Convert();
                XElement main = RootBehavior(document, "Main");
                XElement[] references = main.Element(Ns + "sequence").Elements(Ns + "next")
                    .Select(edge => Nodes(document).Single(node => node.Attribute("id").Value == edge.Value)).ToArray();
                Require(references.Length == 2 && references.All(node => !node.Element(Ns + "sequence").Elements(Ns + "next").Any()
                    && node.Element(Ns + "behaviour").Attribute("add") == null),
                    "off-route reference acquired its own outgoing edges or Add override");
                Require(!Nodes(document).Any(node => (string)node.Element(Ns + "behaviour")?.Attribute("name") == "Ghost"),
                    "ignored reference list created a Ghost behaviour");
            }
        }

        private static void CrossMemberPackMetadata()
        {
            using (var fixture = new Fixture())
            {
                fixture.Member("B");
                XDocument first = fixture.Convert("A");
                XDocument second = XDocument.Load(Path.Combine(fixture.Output, "B", "animations.xml"));
                XElement[] headers = new[] { first, second }.Select(document => document.Root.Element(Ns + "header")).ToArray();
                Require(headers.All(header => header.Element(Ns + "features").Value.Split(' ').Contains("packs")),
                    "cross-member pack feature declaration missing");
                XElement[] packs = headers.Select(header => header.Element(Ns + "pack")).ToArray();
                Require(packs.All(pack => pack != null) && packs[0].Attribute("id").Value == packs[1].Attribute("id").Value
                    && !string.IsNullOrEmpty(packs[0].Attribute("id").Value)
                    && packs.Select(pack => pack.Attribute("member").Value).SequenceEqual(new[] { "A", "B" })
                    && packs.Select(pack => pack.Attribute("role").Value).SequenceEqual(new[] { "main", "form" }),
                    "cross-member output lost shared pack identity or explicit main/form metadata");
                Require(!NamedNode(first, "Born").Element(Ns + "act").Elements(Ns + "param").Any(p => p.Attribute("name").Value == "BornMascot")
                    && Bound(NamedNode(second, "Born").Element(Ns + "act"), "param", "BornMascot") == "A",
                    "own-set omission removed or changed a foreign member reference");
            }
        }

        private static void PackSourceIdentity()
        {
            using (var fixture = new Fixture())
            {
                fixture.Member("B");
                XDocument first = fixture.Convert("A");
                XDocument second = fixture.ConvertSeparate("repeat", "A");
                string originalId = (string)first.Root.Element(Ns + "header").Element(Ns + "pack").Attribute("id");
                Require(originalId == (string)second.Root.Element(Ns + "header").Element(Ns + "pack").Attribute("id"),
                    "pack identity depends on output directory");
                XDocument source = BuildActions();
                source.AddFirst(new XComment("source identity witness"));
                fixture.Actions(source);
                XDocument revised = fixture.ConvertSeparate("revised", "A");
                Require(originalId != (string)revised.Root.Element(Ns + "header").Element(Ns + "pack").Attribute("id"),
                    "pack identity came from emitted XML instead of source bytes");
                first.Root.Element(Ns + "header").Element(Ns + "pack").Attribute("id").Remove();
                revised.Root.Element(Ns + "header").Element(Ns + "pack").Attribute("id").Remove();
                Require(XNode.DeepEquals(first, revised), "source-only comment unexpectedly changed emitted content");
            }
        }

        private static byte[] PixelBytes(Bitmap image, Rectangle rectangle)
        {
            BitmapData data = image.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var result = new byte[rectangle.Width * rectangle.Height * 4];
                for (int row = 0; row < rectangle.Height; row++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, row * data.Stride), result, row * rectangle.Width * 4, rectangle.Width * 4);
                return result;
            }
            finally { image.UnlockBits(data); }
        }

        private static XElement S(string name, params object[] content) { return new XElement(name, content); }
        private static XAttribute A(string name, object value) { return new XAttribute(name, value); }
        private static XElement Pose(string image, int ticks)
        {
            var pose = S("Pose", A("Velocity", "0,0"), A("Duration", ticks));
            if (image != null) pose.Add(A("Image", image), A("ImageAnchor", "2,3"));
            return pose;
        }

        private static XDocument BuildActions()
        {
            return new XDocument(S("Mascot", S("ActionList",
                S("Action", A("Name", "Composite"), A("Type", "Sequence"),
                    S("ActionReference", A("Name", "Walk"), A("TargetX", 11)),
                    S("Action", A("Name", "Inline"), A("Type", "Stay"), S("Animation", Pose("/p.png", 1))),
                    S("ActionReference", A("Name", "Walk"), A("TargetX", 22), A("Duration", 3))),
                S("Action", A("Name", "Stand"), A("Type", "Stay"), S("Animation", Pose("/p.png", 2), Pose("/q.png", 3), Pose("p.png", 4), Pose(null, 1))),
                S("Action", A("Name", "Walk"), A("Type", "Move"), A("TargetX", 99), A("TargetY", 10), S("Animation", Pose("/q.png", 2))),
                S("Action", A("Name", "Born"), A("Type", "Embedded"), A("Class", "com.group_finity.mascot.action.Breed"),
                    A("Duration", 4), A("BornX", 7), A("BornY", 5), A("BornBehavior", "Idle"), A("BornMascot", "A"),
                    A("BornCount", 2), A("BornTransient", "true"), S("Animation", Pose("/p.png", 4))),
                S("Action", A("Name", "Change"), A("Type", "Embedded"), A("Class", "com.group_finity.mascot.action.Transform"),
                    A("Duration", 3), A("TransformMascot", "A"), A("TransformBehavior", "Idle"), S("Animation", Pose("/q.png", 3))))));
        }

        private static XDocument BuildBehaviors()
        {
            return new XDocument(S("Mascot", S("BehaviorList",
                S("Behavior", A("Name", "Main"), A("Action", "Composite"), A("Frequency", 17), A("Duration", 9),
                    S("NextBehaviorList", A("Add", "false"),
                        S("BehaviorReference", A("Name", "Idle"), A("Action", "Stand"), A("Frequency", 2), A("TargetX", 77), A("Duration", 5)),
                        S("BehaviorReference", A("Name", "Idle"), A("Action", "Walk"), A("Frequency", 3), A("TargetX", 88), A("Duration", 6)))),
                S("Behavior", A("Name", "Idle"), A("Action", "Stand"), A("Frequency", 0), A("Hidden", "true"), A("Duration", 99)),
                S("Behavior", A("Name", "BornBehavior"), A("Action", "Born"), A("Frequency", 0)),
                S("Behavior", A("Name", "ChangeBehavior"), A("Action", "Change"), A("Frequency", 0)))));
        }

        private static void SavePng(string path, bool alternate, int width = 4, float dpi = 96, bool alpha = true, int height = 6)
        {
            using (var image = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                image.SetResolution(dpi, dpi);
                int seed = alternate ? 71 : 19;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        image.SetPixel(x, y, Color.FromArgb(255, (x * 43 + seed) % 256, (y * 37 + seed) % 256, (x * 17 + y * 13 + seed) % 256));
                if (alpha)
                {
                    image.SetPixel(0, 0, Color.FromArgb(10, 255, 128, 77));
                    image.SetPixel(1, 0, Color.FromArgb(1, 200, 100, 50));
                    image.SetPixel(2, 0, Color.FromArgb(254, 255, 255, 255));
                    image.SetPixel(1, 1, Color.FromArgb(0, 0, 0, 0));
                }
                image.SetPixel(width - 1, height - 1, alternate ? Color.Cyan : Color.Magenta);
                image.Save(path, ImageFormat.Png);
            }
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly string Root, Pack, Output, XmlPath, MapPath;
            private readonly string _actions, _behaviors, _p, _q;
            private readonly List<string> _extraFiles = new List<string>();
            private readonly List<string> _extraDirectories = new List<string>();
            private readonly List<string> _members = new List<string> { "A" };

            internal Fixture()
            {
                string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                Root = Path.Combine(temp, "tick-pack-" + Guid.NewGuid().ToString("N"));
                Require(string.Equals(Path.GetDirectoryName(Path.GetFullPath(Root)), temp, StringComparison.OrdinalIgnoreCase),
                    "fixture escaped the task temp directory");
                Pack = Path.Combine(Root, "source"); Output = Path.Combine(Root, "output");
                Directory.CreateDirectory(Path.Combine(Pack, "conf"));
                Directory.CreateDirectory(Path.Combine(Pack, "img", "A"));
                _actions = Path.Combine(Pack, "conf", "actions.xml"); _behaviors = Path.Combine(Pack, "conf", "behaviors.xml");
                _p = Path.Combine(Pack, "img", "A", "p.png"); _q = Path.Combine(Pack, "img", "A", "q.png");
                XmlPath = Path.Combine(Output, "A", "animations.xml"); MapPath = Path.Combine(Output, "A", "parity-map.json");
                Actions(BuildActions());
                File.WriteAllText(_behaviors, BuildBehaviors().ToString(), new UTF8Encoding(false));
                SavePng(_p, false); SavePng(_q, true);
            }

            internal void Actions(XDocument document)
            { File.WriteAllText(_actions, document.ToString(), new UTF8Encoding(false)); }

            internal void Behaviors(XDocument document)
            { File.WriteAllText(_behaviors, document.ToString(), new UTF8Encoding(false)); }

            internal void Write(string relative, string content)
            {
                string path = Path.GetFullPath(Path.Combine(Pack, relative.Replace('/', Path.DirectorySeparatorChar)));
                Require(path.StartsWith(Pack + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "fixture write escaped source pack");
                string directory = Path.GetDirectoryName(path);
                var create = new Stack<string>();
                while (!Directory.Exists(directory)) { create.Push(directory); directory = Path.GetDirectoryName(directory); }
                while (create.Count > 0) { directory = create.Pop(); Directory.CreateDirectory(directory); _extraDirectories.Add(directory); }
                File.WriteAllText(path, content, new UTF8Encoding(false));
                if (!_extraFiles.Contains(path)) _extraFiles.Add(path);
            }

            internal void Member(string name)
            {
                string directory = Path.Combine(Pack, "img", name);
                Directory.CreateDirectory(directory);
                _extraDirectories.Add(directory);
                _members.Add(name);
                foreach (string image in new[] { "p", "q" })
                {
                    string path = Path.Combine(directory, image + ".png");
                    SavePng(path, image == "q");
                    _extraFiles.Add(path);
                }
            }

            internal XDocument Convert(string mainMember = null)
            {
                IReadOnlyList<string> paths = mainMember == null ? TickPackEmitter.ConvertPack(Pack, Output) : TickPackEmitter.ConvertPack(Pack, Output, mainMember);
                Require(paths.Count == _members.Count && paths.Contains(XmlPath) && File.Exists(MapPath), "actual emitter did not produce its member XML and parity map");
                return XDocument.Load(XmlPath);
            }

            internal XDocument ConvertSeparate(string name, string mainMember)
            {
                string output = Path.Combine(Root, name);
                Require(string.Equals(Path.GetDirectoryName(output), Root, StringComparison.OrdinalIgnoreCase),
                    "separate conversion output escaped the fixture");
                try
                {
                    TickPackEmitter.ConvertPack(Pack, output, mainMember);
                    return XDocument.Load(Path.Combine(output, "A", "animations.xml"));
                }
                finally
                {
                    foreach (string member in _members)
                    {
                        string directory = Path.Combine(output, member);
                        foreach (string file in new[] { "animations.xml", "parity-map.json" })
                        {
                            string path = Path.Combine(directory, file);
                            if (File.Exists(path)) File.Delete(path);
                        }
                        if (Directory.Exists(directory)) Directory.Delete(directory, false);
                    }
                    if (Directory.Exists(output)) Directory.Delete(output, false);
                }
            }

            public void Dispose()
            {
                foreach (string member in _members)
                {
                    string directory = Path.Combine(Output, member);
                    foreach (string file in new[] { Path.Combine(directory, "animations.xml"), Path.Combine(directory, "parity-map.json") })
                        if (File.Exists(file)) File.Delete(file);
                    if (Directory.Exists(directory)) Directory.Delete(directory, false);
                }
                foreach (string file in _extraFiles) if (File.Exists(file)) File.Delete(file);
                foreach (string directory in _extraDirectories.OrderByDescending(d => d.Length))
                    if (Directory.Exists(directory)) Directory.Delete(directory, false);
                foreach (string file in new[] { _actions, _behaviors, _p, _q, XmlPath, MapPath })
                    if (File.Exists(file)) File.Delete(file);
                foreach (string directory in new[] { Path.Combine(Output, "A"), Output, Path.Combine(Pack, "img", "A"),
                    Path.Combine(Pack, "img"), Path.Combine(Pack, "conf"), Pack, Root })
                    if (Directory.Exists(directory)) Directory.Delete(directory, false);
                Require(!Directory.Exists(Root), "temporary tick emitter fixture remains");
            }
        }
    }
}
