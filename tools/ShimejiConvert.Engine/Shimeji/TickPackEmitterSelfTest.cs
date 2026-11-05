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
                new { Name = "expression-placements", Run = (Action)ExpressionPlacements },
                new { Name = "expression-variables", Run = (Action)ExpressionVariables },
                new { Name = "expression-grammar-refusal", Run = (Action)ExpressionGrammarRefusal },
                new { Name = "affordance-script-refusal", Run = (Action)AffordanceScriptRefusal },
                new { Name = "literal-only-refusal", Run = (Action)LiteralOnlyRefusal },
                new { Name = "class-motion", Run = (Action)ClassMotion },
                new { Name = "class-instant", Run = (Action)ClassInstant },
                new { Name = "class-breeding", Run = (Action)ClassBreeding },
                new { Name = "class-social", Run = (Action)ClassSocial },
                new { Name = "class-broadcast", Run = (Action)ClassBroadcast },
                new { Name = "class-complex", Run = (Action)ClassComplex },
                new { Name = "class-windows", Run = (Action)ClassWindows },
                new { Name = "hotspot-lowering", Run = (Action)HotspotLowering },
                new { Name = "hotspot-shape-case", Run = (Action)HotspotShapeCase },
                new { Name = "hotspot-refusal", Run = (Action)HotspotRefusal },
                new { Name = "offer-lowering", Run = (Action)OfferLowering },
                new { Name = "offer-binding-refusal", Run = (Action)OfferBindingRefusal },
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
                new { Name = "toggle-lowering", Run = (Action)ToggleLowering },
                new { Name = "toggle-required-names", Run = (Action)ToggleRequiredNames },
                new { Name = "toggle-id-refusal", Run = (Action)ToggleIdRefusal },
                new { Name = "root-attributes-ignored", Run = (Action)RootAttributesIgnored },
                new { Name = "stray-text-ignored", Run = (Action)StrayTextIgnored },
                new { Name = "instant-without-animation", Run = (Action)InstantWithoutAnimation },
                new { Name = "anonymous-nested-actions", Run = (Action)AnonymousNestedActions },
                new { Name = "custom-variables", Run = (Action)CustomVariables },
                new { Name = "ignored-behaviour-children", Run = (Action)IgnoredBehaviourChildren },
                new { Name = "ignored-list-entries", Run = (Action)IgnoredListEntries },
                new { Name = "class-on-plain-action", Run = (Action)ClassOnPlainAction },
                new { Name = "bean-getter-scripts", Run = (Action)BeanGetterScripts },
                new { Name = "nan-scripts", Run = (Action)NaNScripts },
                new { Name = "nan-refusal", Run = (Action)NaNRefusal },
                new { Name = "next-missing-dropped", Run = (Action)NextMissingDropped },
                new { Name = "sound-lowering", Run = (Action)SoundLowering },
                new { Name = "sound-refusal", Run = (Action)SoundRefusal },
                new { Name = "configuration-spelling", Run = (Action)ConfigurationSpelling },
                new { Name = "information-lowering", Run = (Action)InformationLowering },
                new { Name = "own-set-binding-reset", Run = (Action)OwnSetBindingReset },
                new { Name = "japanese-schema-values", Run = (Action)JapaneseSchemaValues },
                new { Name = "japanese-schema-refusal", Run = (Action)JapaneseSchemaRefusal },
                new { Name = "japanese-engine-names", Run = (Action)JapaneseEngineNames },
                new { Name = "japanese-engine-name-refusal", Run = (Action)JapaneseEngineNameRefusal },
                new { Name = "ignored-reference-lists", Run = (Action)IgnoredReferenceLists },
                new { Name = "cross-member-pack-metadata", Run = (Action)CrossMemberPackMetadata },
                new { Name = "pack-source-identity", Run = (Action)PackSourceIdentity },
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
                Validate(document, "base fixture");
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

        /// <summary>The emitted member passes the host's own document validator with every format feature enabled,
        /// the check PackConversion applies; the paritypack verb writes without it, so this suite runs it.</summary>
        private static void Validate(XDocument document, string what)
        {
            XmlData.RootNode root;
            string error;
            Require(DesktopAICompanion.CompanionXmlValidator.TryParseWithFeatures(document.ToString(),
                DesktopAICompanion.CompanionFormatFeatures.All, out root, out error),
                "host validator refused the emitted " + what + ": " + error);
        }

        private static string[] Features(XDocument document)
        { return document.Root.Element(Ns + "header").Element(Ns + "features").Value.Split(' '); }

        /// <summary>A scripted parameter or binding: its body and cache, asserting the element exists once.</summary>
        private static string Scripted(XElement owner, string tag, string name, string cache)
        {
            XElement[] found = owner.Elements(Ns + tag).Where(p => p.Attribute("name").Value == name).ToArray();
            Require(found.Length == 1, "scripted " + tag + " " + name + " is missing or repeated");
            Require((string)found[0].Attribute("cache") == cache,
                "scripted " + tag + " " + name + " lost its " + cache + " cache (found '" + (string)found[0].Attribute("cache") + "')");
            return found[0].Attribute("value").Value;
        }

        private static XElement RouteEdge(XDocument document, string behaviour)
        {
            XElement route = Nodes(document).Single(n => n.Element(Ns + "route") != null);
            string id = RootBehavior(document, behaviour).Attribute("id").Value;
            return route.Element(Ns + "sequence").Elements(Ns + "next").Single(e => e.Value == id);
        }

        // Each expression placement of grimoire/06 4.1 and 4.3: an action parameter (${} action, #{} frame), a
        // scripted action Condition (a cached parameter the guard's when names), a scripted Animation Condition
        // (a cached parameter the variant's when names), a reference and a behaviour binding, and the behaviour and
        // <Condition> selection conditions (the edge's when, no cache). It replaces the first lowering's
        // expression-refusal case, whose ${1 + 2} binding now converts; its successor is expression-grammar-refusal.
        private static void ExpressionPlacements()
        {
            using (var fixture = new Fixture())
            {
                XDocument actions = BuildActions();
                XElement walk = actions.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Walk");
                walk.SetAttributeValue("TargetX", "${mascot.environment.cursor.x}");
                walk.SetAttributeValue("TargetY", "#{mascot.environment.cursor.y + 1}");
                XElement stand = actions.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Stand");
                stand.SetAttributeValue("Condition", "#{mascot.anchor.x > 0}");
                stand.Element("Animation").SetAttributeValue("Condition", "${mascot.lookRight}");
                stand.Add(S("Animation", A("Condition", "#{!mascot.lookRight}"), Pose("/p.png", 2)));
                actions.Descendants("ActionReference").First().SetAttributeValue("TargetX", "${1 + 2}");
                fixture.Actions(actions);
                XDocument behaviors = BuildBehaviors();
                XElement main = behaviors.Descendants("Behavior").First();
                main.SetAttributeValue("Condition", "${mascot.totalCount < 5}");
                XElement reference = main.Descendants("BehaviorReference").First();
                reference.SetAttributeValue("TargetX", "#{mascot.anchor.x}");
                reference.SetAttributeValue("Condition", "#{mascot.count > 0}");
                XElement idle = behaviors.Descendants("Behavior").Single(b => (string)b.Attribute("Name") == "Idle");
                idle.ReplaceWith(S("Condition", A("Condition", "${mascot.dragging == false}"), new XElement(idle)));
                fixture.Behaviors(behaviors);
                XDocument document = fixture.Convert();

                XElement walkAct = NamedNode(document, "Walk").Element(Ns + "act");
                Require(Scripted(walkAct, "param", "TargetX", "action") == "mascot.environment.cursor.x"
                    && Scripted(walkAct, "param", "TargetY", "frame") == "mascot.environment.cursor.y + 1",
                    "action parameter body lost or its wrapper kept");
                Require(walkAct.Element(Ns + "guard") == null, "a scripted parameter other than Condition acquired a guard");
                XElement standAct = NamedNode(document, "Stand").Element(Ns + "act");
                Require(Scripted(standAct, "param", "Condition", "frame") == "mascot.anchor.x > 0",
                    "scripted action Condition lost its cached parameter");
                XElement guard = standAct.Element(Ns + "guard");
                Require(guard != null && (string)guard.Attribute("when") == "Condition",
                    "scripted action Condition: the guard's when does not name its cached parameter");
                XElement[] variants = standAct.Element(Ns + "variants").Elements(Ns + "variant").ToArray();
                Require(variants.Length == 2 && (string)variants[0].Attribute("when") == "AnimationCondition1"
                    && (string)variants[1].Attribute("when") == "AnimationCondition2",
                    "scripted Animation Condition: the variant's when does not name its parameter");
                Require(Scripted(standAct, "param", "AnimationCondition1", "action") == "mascot.lookRight"
                    && Scripted(standAct, "param", "AnimationCondition2", "frame") == "!mascot.lookRight",
                    "scripted Animation Condition lost its cached parameter");
                string[] order = standAct.Elements().Select(e => e.Name.LocalName).ToArray();
                Require(order.Length >= 3 && order[order.Length - 1] == "variants" && order[order.Length - 2] == "guard"
                    && order.Take(order.Length - 2).All(n => n == "param"),
                    "act children leave the schema's param, guard, variants order");
                XElement child = NamedNode(document, "Composite").Element(Ns + "act").Element(Ns + "children").Elements(Ns + "child").First();
                Require(Scripted(child, "bind", "TargetX", "action") == "1 + 2", "reference binding body lost");

                XElement mainEdge = RouteEdge(document, "Main");
                Require((string)mainEdge.Attribute("when") == "mascot.totalCount < 5" && mainEdge.Attribute("cache") == null,
                    "behaviour selection condition is not the route edge's uncached when");
                Require((string)RouteEdge(document, "Idle").Attribute("when") == "mascot.dragging == false",
                    "enclosing <Condition> script is not the route edge's when");
                XElement nextEdge = RootBehavior(document, "Main").Element(Ns + "sequence").Elements(Ns + "next").First();
                Require((string)nextEdge.Attribute("when") == "mascot.count > 0", "next-reference selection condition lost");
                XElement nextNode = Nodes(document).Single(n => n.Attribute("id").Value == nextEdge.Value);
                Require(Scripted(nextNode.Element(Ns + "behaviour"), "bind", "TargetX", "frame") == "mascot.anchor.x",
                    "behaviour binding body lost");
                Require(Features(document).Contains("expressions") && !Features(document).Contains("variables"),
                    "scripted member declared the wrong expression features");
                Validate(document, "expression placements");
            }
        }

        // An expression the parser finds reading or writing mascot.variables needs feature 'variables'.
        private static void ExpressionVariables()
        {
            using (var fixture = new Fixture())
            {
                XDocument actions = BuildActions();
                actions.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Change")
                    .SetAttributeValue("Condition", "${mascot.variables.put('kept', 7) == null}");
                fixture.Actions(actions);
                XDocument document = fixture.Convert();
                // The validator first, so an undeclared token is reported in the host's own words.
                Validate(document, "variables member");
                Require(Features(document).Contains("variables") && Features(document).Contains("expressions"),
                    "expression reading mascot.variables did not declare variables");
            }
        }

        // The successor of expression-refusal: a body outside the host's tick grammar refuses, naming the
        // attribute and the parser's message, with no partial output. Unknown names, a hexadecimal floating
        // literal (N-shimeji-host-01) and an action-only name in a selection condition refuse the same way.
        private static void ExpressionGrammarRefusal()
        {
            foreach (string body in new[] { "${1 +}", "${Nope + 1}", "${0x1p4}" })
                using (var fixture = new Fixture())
                {
                    XDocument actions = BuildActions();
                    actions.Descendants("ActionReference").First().SetAttributeValue("TargetX", body);
                    fixture.Actions(actions);
                    string message = Refusal(fixture, "TICK_EXPRESSION_UNSUPPORTED TargetX: ");
                    Require(message.IndexOf("actions.xml#", StringComparison.Ordinal) >= 0
                        && message.IndexOf("ActionReference[1]", StringComparison.Ordinal) >= 0,
                        "expression refusal lost its source locator");
                    Require(!Directory.Exists(fixture.Output), "refused expression left a partial output");
                }
            using (var fixture = new Fixture())
            {
                XDocument behaviors = BuildBehaviors();
                behaviors.Descendants("Behavior").First().SetAttributeValue("Condition", "${TargetX > 0}");
                fixture.Behaviors(behaviors);
                Refusal(fixture, "TICK_EXPRESSION_UNSUPPORTED Condition: ");
                Require(!Directory.Exists(fixture.Output), "selection condition naming an action parameter left output");
            }
        }

        // grimoire/06 4.1: a source Affordance written as an expression has no representation.
        private static void AffordanceScriptRefusal()
        {
            using (var fixture = new Fixture())
            {
                XDocument actions = BuildActions();
                actions.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Stand").SetAttributeValue("Affordance", "${'Hug'}");
                fixture.Actions(actions);
                Refusal(fixture, "TICK_AFFORDANCE_SCRIPTED Affordance");
                Require(!Directory.Exists(fixture.Output), "scripted Affordance left output");
            }
        }

        // An attribute the source reads as literal text keeps refusing a script-shaped value.
        private static void LiteralOnlyRefusal()
        {
            using (var fixture = new Fixture())
            {
                XDocument actions = BuildActions();
                actions.Descendants("Pose").First().SetAttributeValue("Duration", "${2}");
                fixture.Actions(actions);
                Refusal(fixture, "TICK_LITERAL_ONLY Duration");
                Require(!Directory.Exists(fixture.Output), "script-shaped pose duration left output");
            }
        }

        // ---- every class of grimoire/06 section 5, one case per family. Each converts the base fixture with the
        // family's actions added, passes the emitted member through the host validator FIRST (so an undeclared
        // feature is named in the host's words), then checks each action's class and its parameters by the names
        // the host's family files read.

        private static XElement Embedded(string name, string cls, params object[] content)
        {
            return S("Action", A("Name", name), A("Type", "Embedded"), A("Class", "com.group_finity.mascot.action." + cls),
                content, S("Animation", Pose("/p.png", 2)));
        }

        private static XDocument ConvertWith(Fixture fixture, string what, params XElement[] extra)
        {
            XDocument actions = BuildActions();
            actions.Root.Element("ActionList").Add(extra);
            fixture.Actions(actions);
            XDocument document = fixture.Convert();
            Validate(document, what);
            return document;
        }

        /// <summary>The action's class and each name=value literal parameter, exactly.</summary>
        private static void RequireAct(XDocument document, string action, string cls, params string[] parameters)
        {
            XElement act = NamedNode(document, action).Element(Ns + "act");
            Require((string)act.Attribute("class") == cls, action + " lost its class " + cls);
            foreach (string pair in parameters)
            {
                string[] parts = pair.Split(new[] { '=' }, 2);
                Require(act.Elements(Ns + "param").Any(p => p.Attribute("name").Value == parts[0])
                    && Bound(act, "param", parts[0]) == parts[1], action + " (" + cls + ") lost parameter " + parts[0]);
            }
        }

        private static void ClassMotion()
        {
            using (var fixture = new Fixture())
            {
                XDocument document = ConvertWith(fixture, "motion classes",
                    Embedded("Hop", "Jump", A("TargetX", 10), A("TargetY", 20), A("VelocityParam", 12), A("Arc", "false")),
                    Embedded("Hold", "Regist", A("OffsetX", 3), A("OffsetType", "Origin")),
                    Embedded("Spin", "Turn", A("LookRight", "true")),
                    S("Action", A("Name", "Pivot"), A("Type", "Embedded"), A("Class", "com.group_finity.mascot.action.MoveWithTurn"),
                        A("TargetX", 50), S("Animation", Pose("/p.png", 2)), S("Animation", Pose("/q.png", 3))));
                RequireAct(document, "Hop", "Jump", "TargetX=10", "TargetY=20", "VelocityParam=12", "Arc=false");
                RequireAct(document, "Hold", "Regist", "OffsetX=3", "OffsetType=Origin");
                RequireAct(document, "Spin", "Turn", "LookRight=true");
                RequireAct(document, "Pivot", "MoveWithTurn", "TargetX=50");
                Require(NamedNode(document, "Pivot").Element(Ns + "act").Element(Ns + "variants").Elements(Ns + "variant").Count() == 2,
                    "MoveWithTurn lost its turn animation");
                // The negative control for the family tokens below: no social or window class, no such token.
                Require(!Features(document).Contains("social") && !Features(document).Contains("windows"),
                    "a member with no social or window class declared social or windows");
            }
        }

        private static void ClassInstant()
        {
            using (var fixture = new Fixture())
            {
                XDocument document = ConvertWith(fixture, "instant classes",
                    Embedded("Face", "Look", A("LookRight", "false")),
                    Embedded("Nudge", "Offset", A("X", 2), A("Y", -3)),
                    Embedded("Hush", "Mute", A("Sound", "chime")),
                    Embedded("Raise", "BringToFront"),
                    Embedded("Vanish", "SelfDestruct", A("Duration", 3)));
                RequireAct(document, "Face", "Look", "LookRight=false");
                RequireAct(document, "Nudge", "Offset", "X=2", "Y=-3");
                RequireAct(document, "Hush", "Mute", "Sound=chime");
                RequireAct(document, "Raise", "BringToFront");
                RequireAct(document, "Vanish", "SelfDestruct", "Duration=3");
            }
        }

        private static void ClassBreeding()
        {
            using (var fixture = new Fixture())
            {
                // A member whose only lifecycle classes are these three still declares 'lifecycle'.
                ConfigureGraph(fixture, new[] {
                    S("Action", A("Name", "Stand"), A("Type", "Stay"), S("Animation", Pose("/p.png", 1))),
                    Embedded("Trail", "BreedMove", A("TargetX", 600), A("BornInterval", 4), A("BornBehavior", "Main")),
                    Embedded("Hop", "BreedJump", A("TargetX", 700), A("TargetY", 680), A("VelocityParam", 10), A("BornInterval", 3)),
                    Embedded("Drop", "BreedFall", A("BornInterval", 5), A("InitialVY", 2)) }, "Stand");
                XDocument document = fixture.Convert();
                Validate(document, "breeding classes");
                RequireAct(document, "Trail", "BreedMove", "TargetX=600", "BornInterval=4", "BornBehavior=Main");
                RequireAct(document, "Hop", "BreedJump", "TargetX=700", "TargetY=680", "VelocityParam=10", "BornInterval=3");
                RequireAct(document, "Drop", "BreedFall", "BornInterval=5", "InitialVY=2");
                Require(Features(document).Contains("lifecycle"), "BreedMove, BreedJump and BreedFall declared no lifecycle");
            }
        }

        private static void ClassSocial()
        {
            using (var fixture = new Fixture())
            {
                XDocument document = ConvertWith(fixture, "social classes",
                    Embedded("Seek", "ScanMove", A("Affordance", "Hug"), A("Behavior", "Idle"), A("TargetBehavior", "Idle"), A("TargetLook", "true")),
                    Embedded("Leap", "ScanJump", A("Affordance", "Hug"), A("Behavior", "Idle"), A("VelocityParam", 9), A("Arc", "true")),
                    Embedded("Watch", "ScanInteract", A("Affordance", "Wave"), A("Behavior", "Idle"), A("Look", "false"), A("Duration", 6)),
                    Embedded("Huddle", "Interact", A("Behavior", "Idle"), A("Duration", 6)));
                RequireAct(document, "Seek", "ScanMove", "Affordance=Hug", "Behavior=Idle", "TargetBehavior=Idle", "TargetLook=true");
                RequireAct(document, "Leap", "ScanJump", "Affordance=Hug", "VelocityParam=9", "Arc=true");
                RequireAct(document, "Watch", "ScanInteract", "Affordance=Wave", "Look=false");
                RequireAct(document, "Huddle", "Interact", "Behavior=Idle");
                Require(Features(document).Contains("social"), "social classes declared no social feature");
            }
            using (var fixture = new Fixture())
            {
                // A social class with no Affordance, so no offer can be what declares 'social': the class must.
                ConfigureGraph(fixture, new[] { Embedded("Huddle", "Interact", A("Behavior", "Main"), A("Duration", 6)) }, "Huddle");
                XDocument document = fixture.Convert();
                Validate(document, "Interact-only member");
                Require(Features(document).Contains("social"), "an Interact-only member declared no social feature");
            }
        }

        private static void ClassBroadcast()
        {
            using (var fixture = new Fixture())
            {
                XDocument document = ConvertWith(fixture, "deprecated Broadcast aliases",
                    Embedded("Call", "Broadcast", A("Affordance", "Wave"), A("Duration", 4)),
                    Embedded("CallStay", "BroadcastStay", A("Affordance", "Wave")),
                    Embedded("CallMove", "BroadcastMove", A("Affordance", "Wave"), A("TargetX", 300)),
                    Embedded("CallJump", "BroadcastJump", A("Affordance", "Wave"), A("TargetX", 300), A("TargetY", 600)));
                RequireAct(document, "Call", "Broadcast", "Affordance=Wave");
                RequireAct(document, "CallStay", "BroadcastStay", "Affordance=Wave");
                RequireAct(document, "CallMove", "BroadcastMove", "Affordance=Wave", "TargetX=300");
                RequireAct(document, "CallJump", "BroadcastJump", "Affordance=Wave", "TargetY=600");
                // The aliases offer their Affordance like any action (section 5: "Deprecated aliases").
                foreach (string name in new[] { "Call", "CallStay", "CallMove", "CallJump" })
                    Require((string)NamedNode(document, name).Element(Ns + "act").Elements(Ns + "offers").Elements(Ns + "offer")
                        .Select(o => o.Attribute("value")).SingleOrDefault() == "Wave", name + " lost its offer");
            }
        }

        private static void ClassComplex()
        {
            // Characteristics decides the feature tokens, split on commas and compared exactly; a scripted one
            // needs both, and a binding is judged by the action it binds.
            var cases = new[] {
                new { Cls = "ComplexMove", Own = "Breed", Bind = (string)null, Lifecycle = true, Social = false },
                new { Cls = "ComplexJump", Own = "Scan", Bind = (string)null, Lifecycle = false, Social = true },
                new { Cls = "ComplexMove", Own = "Breed ,Scan", Bind = (string)null, Lifecycle = false, Social = true },
                new { Cls = "ComplexJump", Own = "${'Breed'}", Bind = (string)null, Lifecycle = true, Social = true },
                new { Cls = "ComplexMove", Own = (string)null, Bind = "Breed,Scan", Lifecycle = true, Social = true },
            };
            foreach (var c in cases)
                using (var fixture = new Fixture())
                {
                    XElement complex = Embedded("Combo", c.Cls, A("TargetX", 400));
                    if (c.Own != null) complex.SetAttributeValue("Characteristics", c.Own);
                    XElement sequence = S("Action", A("Name", "Run"), A("Type", "Sequence"), S("ActionReference", A("Name", "Combo")));
                    if (c.Bind != null) sequence.Element("ActionReference").SetAttributeValue("Characteristics", c.Bind);
                    ConfigureGraph(fixture, new[] { complex, sequence }, "Run");
                    XDocument document = fixture.Convert();
                    string what = c.Cls + " Characteristics " + (c.Own ?? "bound " + c.Bind);
                    Validate(document, what);
                    Require((string)NamedNode(document, "Combo").Element(Ns + "act").Attribute("class") == c.Cls, what + " lost its class");
                    Require(Features(document).Contains("lifecycle") == c.Lifecycle && Features(document).Contains("social") == c.Social,
                        what + " declared lifecycle " + Features(document).Contains("lifecycle") + ", social " + Features(document).Contains("social"));
                }
        }

        private static void ClassWindows()
        {
            using (var fixture = new Fixture())
            {
                XDocument document = ConvertWith(fixture, "window classes",
                    Embedded("Carry", "WalkWithIE", A("TargetX", 500), A("IeOffsetX", 1), A("IeOffsetY", 2)),
                    Embedded("Ride", "FallWithIE", A("IeOffsetX", 0), A("Gravity", 2)),
                    Embedded("Vault", "JumpWithIE", A("TargetX", 500), A("TargetY", 600), A("IeOffsetY", 4)),
                    Embedded("Toss", "ThrowIE", A("InitialVX", 32), A("InitialVY", -10), A("Gravity", "0.5")),
                    Embedded("Shrink", "MinimizeIE", A("Duration", 6)));
                RequireAct(document, "Carry", "WalkWithIE", "TargetX=500", "IeOffsetX=1", "IeOffsetY=2");
                RequireAct(document, "Ride", "FallWithIE", "IeOffsetX=0", "Gravity=2");
                RequireAct(document, "Vault", "JumpWithIE", "TargetY=600", "IeOffsetY=4");
                RequireAct(document, "Toss", "ThrowIE", "InitialVX=32", "InitialVY=-10", "Gravity=0.5");
                RequireAct(document, "Shrink", "MinimizeIE", "Duration=6");
                Require(Features(document).Contains("windows"), "window classes declared no windows feature");
            }
        }

        // ---- hotspots and offers (grimoire/06 sections 3, 4.1 and 7)

        private static void HotspotLowering()
        {
            using (var fixture = new Fixture())
            {
                XDocument actions = BuildActions();
                XElement animation = actions.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Stand").Element("Animation");
                // Interleaved with the poses on purpose: the format puts a variant's hotspots after its poses.
                animation.AddFirst(S("Hotspot", A("Shape", "Rectangle"), A("Origin", "0,1"), A("Size", "4,3"), A("Behavior", "Idle")));
                animation.Add(S("Hotspot", A("Shape", "Ellipse"), A("Origin", "-2,2"), A("Size", "3,5")));
                fixture.Actions(actions);
                XDocument document = fixture.Convert();
                Validate(document, "hotspot member");
                XElement variant = NamedNode(document, "Stand").Element(Ns + "act").Element(Ns + "variants").Element(Ns + "variant");
                Require(variant.Elements().Last().Name == Ns + "hotspots" && variant.Elements(Ns + "pose").Count() == 4,
                    "hotspots are not the variant's last child after its four poses");
                XElement[] spots = variant.Element(Ns + "hotspots").Elements(Ns + "hotspot").ToArray();
                Require(spots.Length == 2 && (string)spots[0].Attribute("origin") == "0,1" && (string)spots[0].Attribute("size") == "4,3"
                    && (string)spots[1].Attribute("origin") == "-2,2" && (string)spots[1].Attribute("size") == "3,5",
                    "hotspot origin or size lost");
                Require((string)spots[0].Attribute("shape") == "rect" && (string)spots[1].Attribute("shape") == "ellipse",
                    "hotspot shape is not the source's Rectangle or Ellipse");
                Require((string)spots[0].Attribute("behaviour") == "Idle", "hotspot lost its behaviour");
                Require(spots[1].Attribute("behaviour") == null, "a hotspot without a behaviour acquired one");
                Require(Features(document).Contains("hotspots"), "hotspot member declared no hotspots feature");
            }
            using (var fixture = new Fixture())
            {
                string[] plain = Features(fixture.Convert());
                Require(!plain.Contains("hotspots") && !plain.Contains("social"), "a member with no hotspot or offer declared hotspots or social");
            }
        }

        // The source matches a hotspot's Shape with equalsIgnoreCase (AnimationBuilder.java:192-196): any case of
        // Rectangle or Ellipse is that shape, a dotless i included (Java maps it to I), and a name that is neither in
        // any case still refuses.
        private static void HotspotShapeCase()
        {
            var shapes = new[] {
                new { Shape = "rectangle", Kind = "rect" }, new { Shape = "RECTANGLE", Kind = "rect" },
                new { Shape = "ELLIPSE", Kind = "ellipse" }, new { Shape = "eLLiPsE", Kind = "ellipse" },
                new { Shape = "Ell" + (char)0x131 + "pse", Kind = "ellipse" },
            };
            using (var fixture = new Fixture())
            {
                XDocument actions = BuildActions();
                XElement animation = actions.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Stand").Element("Animation");
                foreach (var shape in shapes) animation.Add(S("Hotspot", A("Shape", shape.Shape), A("Origin", "0,0"), A("Size", "2,2")));
                fixture.Actions(actions);
                XDocument document = fixture.Convert();
                Validate(document, "case-varied hotspot member");
                string[] kinds = NamedNode(document, "Stand").Descendants(Ns + "hotspot").Select(h => (string)h.Attribute("shape")).ToArray();
                Require(kinds.SequenceEqual(shapes.Select(s => s.Kind)),
                    "a case variant of Rectangle or Ellipse did not lower to its shape: " + string.Join(", ", kinds));
            }
            foreach (string unknown in new[] { "rectangles", "ELIPSE" })
                using (var fixture = new Fixture())
                {
                    XDocument actions = BuildActions();
                    actions.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Stand").Element("Animation")
                        .Add(S("Hotspot", A("Shape", unknown), A("Origin", "0,0"), A("Size", "2,2")));
                    fixture.Actions(actions);
                    Refusal(fixture, "TICK_HOTSPOT_SHAPE_UNSUPPORTED: Shape '" + unknown + "'");
                }
        }

        // The successor of the first lowering's "only Pose is supported in an Animation": a Hotspot converts, and
        // any other child, an unknown shape or a behaviour with no root definition still refuses.
        private static void HotspotRefusal()
        {
            var inputs = new[] {
                new { Child = S("Hotspot", A("Shape", "Circle"), A("Origin", "0,0"), A("Size", "2,2")), Expected = "TICK_HOTSPOT_SHAPE_UNSUPPORTED" },
                new { Child = S("Hotspot", A("Shape", "Rectangle"), A("Origin", "0,0"), A("Size", "2,2"), A("Behavior", "Ghost")),
                    Expected = "a hotspot needs a root behaviour named 'Ghost'" },
                new { Child = S("Constant", A("Name", "k"), A("Value", "1")), Expected = "only Pose and Hotspot are supported in an Animation" },
            };
            foreach (var input in inputs)
                using (var fixture = new Fixture())
                {
                    XDocument actions = BuildActions();
                    actions.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Stand").Element("Animation").Add(input.Child);
                    fixture.Actions(actions);
                    Refusal(fixture, input.Expected);
                    Require(!Directory.Exists(fixture.Output), "hotspot refusal left output: " + input.Expected);
                }
        }

        private static void OfferLowering()
        {
            using (var fixture = new Fixture())
            {
                XDocument actions = BuildActions();
                XElement stand = actions.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Stand");
                stand.SetAttributeValue("Affordance", "Hug");
                stand.SetAttributeValue("Condition", "${true}");
                actions.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Walk").SetAttributeValue("Affordance", " ");
                fixture.Actions(actions);
                XDocument document = fixture.Convert();
                Validate(document, "offer member");
                XElement act = NamedNode(document, "Stand").Element(Ns + "act");
                XElement[] offers = act.Elements(Ns + "offers").Elements(Ns + "offer").ToArray();
                Require(offers.Length == 1 && (string)offers[0].Attribute("value") == "Hug", "a literal Affordance lost its offer");
                Require(Bound(act, "param", "Affordance") == "Hug", "a literal Affordance lost the parameter the scan classes read");
                string[] order = act.Elements().Select(e => e.Name.LocalName).ToArray();
                Require(order.SkipWhile(n => n == "param").SequenceEqual(new[] { "guard", "offers", "variants" }),
                    "act children leave the schema's param, guard, offers, variants order");
                XElement walk = NamedNode(document, "Walk").Element(Ns + "act");
                Require(walk.Element(Ns + "offers") == null && Bound(walk, "param", "Affordance") == " ",
                    "a Java-blank Affordance wrote an offer the runtime would skip");
                Require(Features(document).Contains("social"), "offer member declared no social feature");
            }
            using (var fixture = new Fixture())
            {
                // Onto an action without its own Affordance the runtime offers the bound parameter: no <offers>, social.
                XDocument behaviors = BuildBehaviors();
                behaviors.Descendants("Behavior").Single(b => (string)b.Attribute("Name") == "Idle").SetAttributeValue("Affordance", "Wave");
                fixture.Behaviors(behaviors);
                XDocument document = fixture.Convert();
                Validate(document, "bound offer member");
                Require(Bound(RootBehavior(document, "Idle").Element(Ns + "behaviour"), "bind", "Affordance") == "Wave"
                    && NamedNode(document, "Stand").Element(Ns + "act").Element(Ns + "offers") == null,
                    "an Affordance binding was not kept as a binding");
                Require(Features(document).Contains("social"), "a bound Affordance declared no social feature");
            }
        }

        private static void OfferBindingRefusal()
        {
            using (var fixture = new Fixture())
            {
                XDocument actions = BuildActions();
                actions.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Walk").SetAttributeValue("Affordance", "Hug");
                actions.Descendants("ActionReference").First().SetAttributeValue("Affordance", "Wave");
                fixture.Actions(actions);
                Refusal(fixture, "TICK_AFFORDANCE_BINDING Affordance");
                Require(!Directory.Exists(fixture.Output), "Affordance binding refusal left output");
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
                // A body outside the grammar, now that a well-formed one converts.
                actions.Descendants("Action").First().SetAttributeValue("Duration", "${2 +}");
                fixture.Write("img/B/conf/actions.xml", actions.ToString());
                fixture.Write("img/B/conf/behaviors.xml", BuildBehaviors().ToString());
                Refusal(fixture, "TICK_EXPRESSION_UNSUPPORTED Duration");
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

        // Replaces toggle-refusal ("toggleable behaviours are not lowered yet"): grimoire/06 section 2 defines header
        // toggles, so a Toggleable behaviour converts. Its successor refusal is toggle-id-refusal.
        private static void ToggleLowering()
        {
            using (var fixture = new Fixture())
            {
                XDocument source = BuildBehaviors();
                XElement idle = source.Descendants("Behavior").Single(b => (string)b.Attribute("Name") == "Idle");
                idle.SetAttributeValue("Toggleable", "true");
                // The first next reference (named Idle) says Toggleable itself; the second does not.
                source.Descendants("BehaviorReference").First().SetAttributeValue("Toggleable", "TRUE");
                source.Descendants("Behavior").Single(b => (string)b.Attribute("Name") == "BornBehavior").SetAttributeValue("Toggleable", "false");
                fixture.Behaviors(source);
                XDocument document = fixture.Convert();
                Validate(document, "toggle member");
                XElement[] toggles = document.Root.Element(Ns + "header").Elements(Ns + "toggles").Elements(Ns + "toggle").ToArray();
                Require(toggles.Length == 1 && (string)toggles[0].Attribute("id") == "Idle" && (string)toggles[0].Attribute("label") == "Idle"
                    && toggles[0].Attribute("default") == null, "header toggles are not one Idle switch with no default");
                Require((string)RootBehavior(document, "Idle").Element(Ns + "behaviour").Attribute("toggle") == "Idle",
                    "Toggleable behaviour lost its toggle");
                XElement[] next = RootBehavior(document, "Main").Element(Ns + "sequence").Elements(Ns + "next")
                    .Select(e => Nodes(document).Single(n => n.Attribute("id").Value == e.Value).Element(Ns + "behaviour")).ToArray();
                Require((string)next[0].Attribute("toggle") == "Idle" && next[1].Attribute("toggle") == null,
                    "a next reference's own Toggleable was not what decides its toggle");
                Require(RootBehavior(document, "BornBehavior").Element(Ns + "behaviour").Attribute("toggle") == null,
                    "Toggleable false acquired a toggle");
            }
            using (var fixture = new Fixture())
                Require(fixture.Convert().Root.Element(Ns + "header").Element(Ns + "toggles") == null,
                    "a member with no Toggleable behaviour wrote header toggles");
        }

        // The source never lets the three required behaviours be toggled (BehaviorBuilder overrides Toggleable for the
        // exact names Fall, Thrown and Dragged), so neither a root Behavior nor a next BehaviorReference of those names
        // gets a toggle or a header switch, while an ordinary Toggleable behaviour beside them still does.
        private static void ToggleRequiredNames()
        {
            using (var fixture = new Fixture())
            {
                XDocument source = BuildBehaviors();
                source.Descendants("Behavior").Single(b => (string)b.Attribute("Name") == "Idle").SetAttributeValue("Toggleable", "true");
                foreach (string name in new[] { "Fall", "Thrown", "Dragged" })
                    source.Root.Element("BehaviorList").Add(S("Behavior", A("Name", name), A("Action", "Stand"), A("Frequency", 0),
                        A("Toggleable", "true")));
                source.Descendants("NextBehaviorList").First().Add(S("BehaviorReference", A("Name", "Fall"), A("Action", "Stand"),
                    A("Frequency", 1), A("Toggleable", "true")));
                fixture.Behaviors(source);
                XDocument document = fixture.Convert();
                Validate(document, "required-name toggle member");
                string[] toggles = document.Root.Element(Ns + "header").Elements(Ns + "toggles").Elements(Ns + "toggle")
                    .Select(t => (string)t.Attribute("id")).ToArray();
                Require(toggles.SequenceEqual(new[] { "Idle" }),
                    "header toggles are not the one ordinary Idle switch: " + string.Join(", ", toggles));
                foreach (string name in new[] { "Fall", "Thrown", "Dragged" })
                    Require(RootBehavior(document, name).Element(Ns + "behaviour").Attribute("toggle") == null,
                        "required behaviour " + name + " acquired a toggle");
                XElement reference = RootBehavior(document, "Main").Element(Ns + "sequence").Elements(Ns + "next")
                    .Select(e => Nodes(document).Single(n => n.Attribute("id").Value == e.Value).Element(Ns + "behaviour"))
                    .Single(b => (string)b.Attribute("name") == "Fall");
                Require(reference.Attribute("toggle") == null, "a next reference named Fall acquired a toggle");
                Require((string)RootBehavior(document, "Idle").Element(Ns + "behaviour").Attribute("toggle") == "Idle",
                    "WITNESS the ordinary Toggleable behaviour beside the required ones lost its toggle");
            }
        }

        // A toggle id is at most 64 characters (the validator's rule), and the id is the behaviour's name.
        private static void ToggleIdRefusal()
        {
            using (var fixture = new Fixture())
            {
                XDocument source = BuildBehaviors();
                source.Root.Element("BehaviorList").Add(S("Behavior", A("Name", new string('T', 65)), A("Action", "Stand"),
                    A("Frequency", 0), A("Toggleable", "true")));
                fixture.Behaviors(source);
                Refusal(fixture, "TICK_TOGGLE_ID_LIMIT");
                Require(!Directory.Exists(fixture.Output), "toggle id refusal left output");
            }
        }

        // The source reads no attribute of a configuration document's root (Configuration.load selects its children
        // only), so a root carrying a namespace-qualified schema location, its namespace declaration and any other
        // attribute converts to exactly the bytes the bare root does; the root's text is still refused.
        private static void RootAttributesIgnored()
        {
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
            string plain;
            using (var fixture = new Fixture())
                plain = fixture.Convert().ToString();
            using (var fixture = new Fixture())
            {
                foreach (XDocument document in new[] { BuildActions(), BuildBehaviors() })
                {
                    document.Root.Add(new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName),
                        new XAttribute(xsi + "schemaLocation", "urn:example Mascot.xsd"), new XAttribute("version", "2"));
                    if (document.Root.Element("ActionList") != null) fixture.Actions(document); else fixture.Behaviors(document);
                }
                string decorated = fixture.Convert().ToString();
                Require(decorated == plain, "a root attribute the source never reads changed the emitted member");
            }
        }

        // Direct text in a configuration element is never read by the source (its Entry lists element children only),
        // so text on the roots, in an ActionList, an Action, an Animation, a BehaviorList and a Behavior converts to
        // exactly the bytes the member has without it. Replaces root-attributes-ignored's root-text refusal.
        private static void StrayTextIgnored()
        {
            string plain;
            using (var fixture = new Fixture())
                plain = fixture.Convert().ToString();
            using (var fixture = new Fixture())
            {
                XDocument actions = BuildActions();
                actions.Root.AddFirst("root text");
                actions.Root.Element("ActionList").AddFirst("list text");
                XElement stand = actions.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Stand");
                stand.AddFirst("action text");
                stand.Element("Animation").AddFirst("Pose Image=/p.png ImageAnchor=2,3 Velocity=0,0 Duration=2 /");
                fixture.Actions(actions);
                XDocument behaviours = BuildBehaviors();
                behaviours.Root.Element("BehaviorList").AddFirst("behaviour list text");
                behaviours.Descendants("Behavior").First().AddFirst("behaviour text");
                fixture.Behaviors(behaviours);
                XDocument document = fixture.Convert();
                Validate(document, "stray-text member");
                Require(document.ToString() == plain, "stray text the source never reads changed the emitted member");
            }
        }

        // An instant embedded class (Look, Offset, Mute, BringToFront) is built without animations and ends in the step
        // it starts, so one written with no Animation, as the stock configuration writes them, converts to an act with
        // its parameters and no <variants>; a posed action with no Animation, embedded or not, still refuses.
        private static void InstantWithoutAnimation()
        {
            using (var fixture = new Fixture())
            {
                XDocument actions = BuildActions();
                Func<string, string, object[], XElement> bare = (name, cls, content) => S("Action", A("Name", name), A("Type", "Embedded"),
                    A("Class", "com.group_finity.mascot.action." + cls), content);
                actions.Root.Element("ActionList").Add(bare("Face", "Look", new object[] { A("LookRight", "true") }),
                    bare("Nudge", "Offset", new object[] { A("X", 1), A("Y", -2) }), bare("Hush", "Mute", new object[] { A("Sound", "chime") }),
                    bare("Raise", "BringToFront", new object[0]));
                fixture.Actions(actions);
                XDocument document = fixture.Convert();
                Validate(document, "instant-without-animation member");
                foreach (string[] row in new[] { new[] { "Face", "Look" }, new[] { "Nudge", "Offset" }, new[] { "Hush", "Mute" }, new[] { "Raise", "BringToFront" } })
                {
                    XElement act = NamedNode(document, row[0]).Element(Ns + "act");
                    Require((string)act.Attribute("class") == row[1] && act.Element(Ns + "variants") == null,
                        "instant action " + row[0] + " without an Animation is not a bare " + row[1] + " act");
                }
                Require(Bound(NamedNode(document, "Nudge").Element(Ns + "act"), "param", "Y") == "-2"
                    && Bound(NamedNode(document, "Face").Element(Ns + "act"), "param", "LookRight") == "true",
                    "an instant action without an Animation lost its parameters");
            }
            foreach (XElement posed in new[] {
                S("Action", A("Name", "Drop"), A("Type", "Embedded"), A("Class", "com.group_finity.mascot.action.Fall")),
                S("Action", A("Name", "Wait"), A("Type", "Stay")) })
                using (var fixture = new Fixture())
                {
                    XDocument actions = BuildActions();
                    actions.Root.Element("ActionList").Add(posed);
                    fixture.Actions(actions);
                    Refusal(fixture, "posed action has no Animation");
                }
        }

        // A nested Action is built where it stands and never looked up by name, so it may have none: an anonymous one
        // (no Name, or an empty one) becomes an action node named after its place, <parent>/<position>, at any depth,
        // referenced by its composite and host-validated; a global action that already has that name refuses.
        private static void AnonymousNestedActions()
        {
            using (var fixture = new Fixture())
            {
                XDocument actions = BuildActions();
                actions.Root.Element("ActionList").Add(S("Action", A("Name", "Chain"), A("Type", "Sequence"),
                    S("Action", A("Type", "Stay"), A("Duration", 2), S("Animation", Pose("/p.png", 1))),
                    S("ActionReference", A("Name", "Stand")),
                    S("Action", A("Name", ""), A("Type", "Select"),
                        S("Action", A("Type", "Embedded"), A("Class", "com.group_finity.mascot.action.Look"), A("LookRight", "true"))),
                    S("Action", A("Name", "Named"), A("Type", "Stay"), S("Animation", Pose("/q.png", 1)))));
                fixture.Actions(actions);
                XDocument document = fixture.Convert();
                Validate(document, "anonymous nested action member");
                string[] children = NamedNode(document, "Chain").Element(Ns + "act").Element(Ns + "children").Elements(Ns + "child")
                    .Select(c => (string)c.Attribute("animation")).ToArray();
                Require(children.SequenceEqual(new[] { "Chain/1", "Stand", "Chain/3", "Named" }),
                    "anonymous nested actions are not named after their place: " + string.Join(", ", children));
                Require((string)NamedNode(document, "Chain/1").Element(Ns + "act").Attribute("type") == "Stay"
                    && (string)NamedNode(document, "Chain/3").Element(Ns + "act").Element(Ns + "children").Element(Ns + "child").Attribute("animation") == "Chain/3/1"
                    && (string)NamedNode(document, "Chain/3/1").Element(Ns + "act").Attribute("class") == "Look",
                    "an anonymous nested action's node, or one nested inside it, is not the source's action");
            }
            using (var fixture = new Fixture())
            {
                XDocument actions = BuildActions();
                actions.Root.Element("ActionList").Add(
                    S("Action", A("Name", "Chain"), A("Type", "Sequence"), S("Action", A("Type", "Stay"), S("Animation", Pose("/p.png", 1)))),
                    S("Action", A("Name", "Chain/1"), A("Type", "Stay"), S("Animation", Pose("/p.png", 1))));
                fixture.Actions(actions);
                Refusal(fixture, "a nested action needs a unique name for this first lowering: 'Chain/1'");
            }
        }

        // An attribute the schema does not name is a variable of the source's action map: on an action, a reference
        // or a behaviour it is lowered under its own name, and a script in that map can read it (a reference's TargetX
        // reading the Gap it binds beside it, an action's Duration reading its own Speed, an action's TargetX reading
        // the Step its behaviour binds). A grammar word, a custom variable no script reads, or a name no binding
        // declares, still refuses.
        private static void CustomVariables()
        {
            using (var fixture = new Fixture())
            {
                XDocument actions = BuildActions();
                XElement list = actions.Root.Element("ActionList");
                list.Add(S("Action", A("Name", "Chase"), A("Type", "Sequence"),
                        S("ActionReference", A("Name", "Walk"), A("TargetX", "#{mascot.environment.cursor.x+Gap}"), A("Gap", "${mascot.anchor.x < 0 ? 1 : 2}"))),
                    S("Action", A("Name", "Pace"), A("Type", "Stay"), A("Speed", "3"), A("Duration", "${Speed*2}"), S("Animation", Pose("/p.png", 1))),
                    S("Action", A("Name", "Stride"), A("Type", "Move"), A("TargetX", "${mascot.anchor.x+Step}"), S("Animation", Pose("/q.png", 1))));
                fixture.Actions(actions);
                XDocument behaviours = BuildBehaviors();
                behaviours.Root.Element("BehaviorList").Add(S("Behavior", A("Name", "Striding"), A("Action", "Stride"), A("Frequency", 0), A("Step", "4")));
                fixture.Behaviors(behaviours);
                XDocument document = fixture.Convert();
                Validate(document, "custom-variable member");
                XElement child = NamedNode(document, "Chase").Element(Ns + "act").Element(Ns + "children").Element(Ns + "child");
                Require(Scripted(child, "bind", "Gap", "action") == "mascot.anchor.x < 0 ? 1 : 2"
                    && Scripted(child, "bind", "TargetX", "frame") == "mascot.environment.cursor.x+Gap",
                    "a reference's custom Gap binding, or the TargetX that reads it, was not lowered");
                XElement pace = NamedNode(document, "Pace").Element(Ns + "act");
                Require(Bound(pace, "param", "Speed") == "3" && Scripted(pace, "param", "Duration", "action") == "Speed*2",
                    "an action's own custom variable, or the script that reads it, was not lowered");
                Require(Bound(RootBehavior(document, "Striding").Element(Ns + "behaviour"), "bind", "Step") == "4"
                    && Scripted(NamedNode(document, "Stride").Element(Ns + "act"), "param", "TargetX", "action") == "mascot.anchor.x+Step",
                    "a behaviour's custom binding, or the action script that reads it, was not lowered");
            }
            var refusals = new[] {
                new { Attribute = A("mascot", "1"), Expected = "unrecognised source attribute 'mascot'" },
                new { Attribute = A("Unread", "1"), Expected = "unrecognised source attribute 'Unread'" },
                new { Attribute = A("TargetX", "${Nope}"), Expected = "TICK_EXPRESSION_UNSUPPORTED TargetX" },
            };
            foreach (var r in refusals)
                using (var fixture = new Fixture())
                {
                    XDocument actions = BuildActions();
                    actions.Root.Element("ActionList").Add(S("Action", A("Name", "Odd"), A("Type", "Move"), r.Attribute, S("Animation", Pose("/p.png", 1))));
                    fixture.Actions(actions);
                    Refusal(fixture, r.Expected);
                }
        }

        // 1.0.23 reads only a Behavior's NextBehaviorList children: a <NextBehavior> (the official default
        // configuration's element) or any other child is never read, so the behaviour converts as if it were absent:
        // the same bytes, its add left at the root reading, and none of the ignored element's references as edges.
        private static void IgnoredBehaviourChildren()
        {
            string plain;
            using (var fixture = new Fixture())
                plain = fixture.Convert().ToString();
            using (var fixture = new Fixture())
            {
                XDocument behaviours = BuildBehaviors();
                XElement idle = behaviours.Descendants("Behavior").Single(b => (string)b.Attribute("Name") == "Idle");
                idle.Add(S("NextBehavior", A("Add", "false"), S("BehaviorReference", A("Name", "Main"), A("Frequency", 5))),
                    S("Note", A("Text", "x")));
                fixture.Behaviors(behaviours);
                XDocument document = fixture.Convert();
                Validate(document, "ignored behaviour children member");
                Require(document.ToString() == plain, "a behaviour child the source never reads changed the emitted member");
            }
        }

        // Replaces information-refusal: an information document holding only <Information> (a name, a preview, credits)
        // converts to exactly the bytes the member has without it, since no mascot reads that metadata; a Constant, an
        // ActionList or a BehaviourList in it, which the source would load as configuration, refuses by name.
        // Both behaviour-list readers take only Condition and Behaviour (root list) or BehaviourReference (next list)
        // children: a NextBehavior or a BehaviorReference straight in the root list or in a Condition there, and an
        // unknown element in a next list, convert to exactly the bytes the member has without them.
        private static void IgnoredListEntries()
        {
            string plain;
            using (var fixture = new Fixture())
                plain = fixture.Convert().ToString();
            using (var fixture = new Fixture())
            {
                XDocument behaviours = BuildBehaviors();
                XElement list = behaviours.Root.Element("BehaviorList");
                list.Add(S("Condition", A("Condition", "true"), S("NextBehavior", S("BehaviorReference", A("Name", "Idle"), A("Frequency", 1)))),
                    S("BehaviorReference", A("Name", "Idle"), A("Frequency", 4)));
                behaviours.Descendants("NextBehaviorList").First().Add(S("Aside", A("Name", "Idle")));
                fixture.Behaviors(behaviours);
                XDocument document = fixture.Convert();
                Validate(document, "ignored list entries member");
                Require(document.ToString() == plain, "a behaviour-list child the source never reads changed the emitted member");
            }
        }

        // A Class on an action whose Type is not Embedded is never used by the source (ActionBuilder builds the class
        // only for Embedded), so it converts to exactly the bytes the action has without it.
        private static void ClassOnPlainAction()
        {
            string plain;
            using (var fixture = new Fixture())
                plain = fixture.Convert().ToString();
            using (var fixture = new Fixture())
            {
                XDocument actions = BuildActions();
                actions.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Walk")
                    .SetAttributeValue("Class", "com.group_finity.mascot.action.Fall");
                fixture.Actions(actions);
                XDocument document = fixture.Convert();
                Validate(document, "Class on a Move member");
                Require(document.ToString() == plain, "a Class on a non-Embedded action changed the emitted member");
            }
        }

        // A zero-argument JavaBean getter call reads what its property does in Nashorn, so a script written with
        // getScreen() or isLookRight() is lowered as the property form the host models; one inside a string is left
        // alone, and a getter the host does not model still refuses under its property name.
        private static void BeanGetterScripts()
        {
            using (var fixture = new Fixture())
            {
                XDocument actions = BuildActions();
                XElement walk = actions.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Walk");
                walk.SetAttributeValue("TargetX", "${mascot.environment.getScreen().right - 10}");
                walk.SetAttributeValue("TargetY", "#{mascot.isLookRight() ? mascot.environment.getWorkArea().bottom : 0}");
                actions.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Stand")
                    .SetAttributeValue("Condition", "${'a.getX()' != 'b' && mascot.environment.screen.width > 0}");
                fixture.Actions(actions);
                XDocument document = fixture.Convert();
                Validate(document, "bean-getter member");
                XElement act = NamedNode(document, "Walk").Element(Ns + "act");
                Require(Scripted(act, "param", "TargetX", "action") == "mascot.environment.screen.right - 10"
                    && Scripted(act, "param", "TargetY", "frame") == "mascot.lookRight ? mascot.environment.workArea.bottom : 0",
                    "a JavaBean getter call was not written as its property");
                Require(Scripted(NamedNode(document, "Stand").Element(Ns + "act"), "param", "Condition", "action")
                    == "'a.getX()' != 'b' && mascot.environment.screen.width > 0", "a getter call inside a string literal was rewritten");
            }
            using (var fixture = new Fixture())
            {
                XDocument actions = BuildActions();
                actions.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Walk")
                    .SetAttributeValue("TargetX", "${mascot.getManager().count}");
                fixture.Actions(actions);
                Refusal(fixture, "TICK_EXPRESSION_UNSUPPORTED TargetX");
            }
        }

        // Decision 54: Math.random named without a call is NaN in Nashorn, with no draw, and a Move's or a Jump's int
        // TargetX/TargetY reader makes NaN 0 (Move.java:147-154, Jump.java:103-110). So the stock ternary's NaN branch,
        // a whole-NaN Move TargetY and a whole-NaN Jump TargetX lower to 0, and the other branch, with its real draw,
        // stays as written.
        private static void NaNScripts()
        {
            using (var fixture = new Fixture())
            {
                XDocument actions = BuildActions();
                XElement list = actions.Root.Element("ActionList");
                list.Add(S("Action", A("Name", "Roam"), A("Type", "Sequence"), S("ActionReference", A("Name", "Walk"),
                        A("TargetX", "${mascot.lookRight ? mascot.environment.workArea.left+Math.random()*100 : mascot.environment.workArea.right-Math.random*100}"),
                        A("TargetY", "#{-(Math.random) * 3}"))),
                    S("Action", A("Name", "Leap"), A("Type", "Embedded"), A("Class", "com.group_finity.mascot.action.Jump"),
                        A("TargetX", "${Math.max(Math.random * 2, 5)}"), S("Animation", Pose("/p.png", 1))),
                    // A getter call inside the NaN part reads as its property does (getters are rewritten first), and NaN
                    // in max's later argument still reaches the result; the checked parts name only known members.
                    S("Action", A("Name", "Hop"), A("Type", "Move"),
                        A("TargetX", "${mascot.lookRight ? 5 : mascot.environment.getScreen().right - Math.random*10}"),
                        A("TargetY", "${Math.max(5, Math.random)}"), S("Animation", Pose("/p.png", 1))));
                fixture.Actions(actions);
                XDocument document = fixture.Convert();
                Validate(document, "NaN-lowered member");
                XElement hop = NamedNode(document, "Hop").Element(Ns + "act");
                Require(Scripted(hop, "param", "TargetX", "action") == "mascot.lookRight ? 5 : 0",
                    "a getter call inside a NaN part did not lower as its property form does: " + Scripted(hop, "param", "TargetX", "action"));
                Require(Scripted(hop, "param", "TargetY", "action") == "0", "NaN in Math.max's second argument did not reach the result");
                XElement child = NamedNode(document, "Roam").Element(Ns + "act").Element(Ns + "children").Element(Ns + "child");
                Require(Scripted(child, "bind", "TargetX", "action") == "mascot.lookRight ? mascot.environment.workArea.left+Math.random()*100 : 0",
                    "the stock ternary's NaN branch was not written as the int reader's 0: " + Scripted(child, "bind", "TargetX", "action"));
                Require(Scripted(child, "bind", "TargetY", "frame") == "0", "a whole-NaN Move TargetY was not written as 0");
                Require(Scripted(NamedNode(document, "Leap").Element(Ns + "act"), "param", "TargetX", "action") == "0",
                    "a whole-NaN Jump TargetX was not written as 0");
            }
        }

        // Every shape the NaN pass cannot prove refuses by name: NaN reaching a comparison, a + with text, a NaN part
        // holding a draw, NaN inside a value that is not the result, Math.pow, and a reader no source line fixes.
        private static void NaNRefusal()
        {
            var cases = new[] {
                new { Name = "TargetX", Value = "${mascot.anchor.x < Math.random*100 ? 1 : 2}", Expected = "TICK_NAN_COMPARED TargetX" },
                new { Name = "TargetX", Value = "${'a' + Math.random}", Expected = "TICK_NAN_PLUS_UNTYPED TargetX" },
                new { Name = "TargetX", Value = "${Math.random()*2 - Math.random*3}", Expected = "TICK_NAN_IMPURE TargetX" },
                new { Name = "TargetX", Value = "${1 + (mascot.lookRight ? 2 : Math.random)}", Expected = "TICK_NAN_PARTIAL TargetX" },
                new { Name = "TargetX", Value = "${Math.pow(Math.random, 0)}", Expected = "TICK_NAN_FUNCTION TargetX" },
                new { Name = "Duration", Value = "${Math.random*10}", Expected = "TICK_NAN_READER Duration" },
                // Review D54-B: a name or member the source would fail on is not hidden inside the fixed value.
                new { Name = "TargetX", Value = "${mascot.lookRight ? 10 : Bogus - Math.random*100}", Expected = "TICK_NAN_UNCHECKED TargetX" },
                new { Name = "TargetX", Value = "${mascot.environment.bogus.x - Math.random}", Expected = "TICK_NAN_UNCHECKED TargetX" },
                // Review D54-C: an argument past the function's arity is ignored by the source, so NaN there is not the result.
                new { Name = "TargetX", Value = "${Math.abs(300, Math.random)}", Expected = "TICK_NAN_ARITY TargetX" },
                new { Name = "TargetX", Value = "${Math.atan2(1, 2, Math.random)}", Expected = "TICK_NAN_ARITY TargetX" },
            };
            foreach (var c in cases)
                using (var fixture = new Fixture())
                {
                    XDocument actions = BuildActions();
                    actions.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Walk").SetAttributeValue(c.Name, c.Value);
                    fixture.Actions(actions);
                    Refusal(fixture, c.Expected);
                    Require(!Directory.Exists(fixture.Output), "NaN refusal left output: " + c.Expected);
                }
            // Review D54-A: the lowered TargetX is also the action's variable, which another script reads by name. The
            // source's Condition sees NaN != 0 (true) facing left; a lowered one would see 0 != 0. Read in the same
            // action and in another one, it refuses.
            foreach (string reader in new[] { "Walk", "Stand" })
                using (var fixture = new Fixture())
                {
                    XDocument actions = BuildActions();
                    actions.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Walk")
                        .SetAttributeValue("TargetX", "${mascot.lookRight ? 10 : 500 - Math.random*100}");
                    actions.Descendants("Action").Single(a => (string)a.Attribute("Name") == reader).SetAttributeValue("Condition", "${TargetX != 0}");
                    fixture.Actions(actions);
                    Refusal(fixture, "TICK_NAN_READER TargetX: a script of this member names 'TargetX'");
                    Require(!Directory.Exists(fixture.Output), "a NaN reader refusal left output: " + reader);
                }
            // Review D54-D: only a Move's and an embedded Jump's TargetX/TargetY reader is cited. The same script on a
            // Stay, or on an embedded class that is not a Jump (a Breed), has no reader this pass can cite.
            foreach (string holder in new[] { "Stand", "Born" })
                using (var fixture = new Fixture())
                {
                    XDocument actions = BuildActions();
                    actions.Descendants("Action").Single(a => (string)a.Attribute("Name") == holder).SetAttributeValue("TargetX", "${Math.random*5}");
                    fixture.Actions(actions);
                    bool refused = false;
                    try { TickPackEmitter.ConvertPack(fixture.Pack, fixture.Output); }
                    catch (InvalidDataException error)
                    {
                        refused = error.Message.IndexOf("TICK_NAN_READER TargetX: Math.random without a call is NaN", StringComparison.Ordinal) >= 0;
                    }
                    Require(refused, "a NaN TargetX on " + holder + ", a class with no cited int reader, was not refused as TICK_NAN_READER");
                }
        }

        // Decision 55: a next entry naming no root behaviour is dropped with a warning (the source would stop its tick
        // thread when its behaviour ends); a list it empties lowers as the source runs an empty list, its Add kept; one
        // whose condition is a script, which the source evaluates at every selection, stays refused.
        private static void NextMissingDropped()
        {
            using (var fixture = new Fixture())
            {
                XDocument behaviours = BuildBehaviors();
                behaviours.Descendants("NextBehaviorList").First().Add(S("BehaviorReference", A("Name", "Ghost"), A("Action", "Stand"), A("Frequency", 4)));
                behaviours.Root.Element("BehaviorList").Add(S("Behavior", A("Name", "Lone"), A("Action", "Stand"), A("Frequency", 0),
                    S("NextBehaviorList", A("Add", "false"), S("BehaviorReference", A("Name", "Ghost"), A("Frequency", 1)))),
                    S("Behavior", A("Name", "Keep"), A("Action", "Stand"), A("Frequency", 0),
                        S("NextBehaviorList", A("Add", "true"), S("Condition", A("Condition", "true"), S("BehaviorReference", A("Name", "Ghost"), A("Frequency", 1))))));
                fixture.Behaviors(behaviours);
                XDocument document = fixture.Convert();
                Validate(document, "dropped next entry member");
                Require(RootBehavior(document, "Main").Element(Ns + "sequence").Elements(Ns + "next").Count() == 2,
                    "the next entry naming no root behaviour was not dropped from Main's list");
                XElement lone = RootBehavior(document, "Lone"), keep = RootBehavior(document, "Keep");
                Require(!lone.Element(Ns + "sequence").Elements(Ns + "next").Any() && (string)lone.Element(Ns + "behaviour").Attribute("add") == "false"
                    && !keep.Element(Ns + "sequence").Elements(Ns + "next").Any() && (string)keep.Element(Ns + "behaviour").Attribute("add") == "true",
                    "an emptied next list lost its Add");
                Require(!Nodes(document).Any(n => (string)n.Element(Ns + "behaviour")?.Attribute("name") == "Ghost"), "a dropped entry left a node");
                string[] warnings = TickPackEmitter.PrepareMembers(fixture.Pack, true).Single().Warnings.ToArray();
                Require(warnings.Length == 3 && warnings.All(w => w.IndexOf("the next entry 'Ghost' names no root behaviour and was dropped", StringComparison.Ordinal) >= 0)
                    && warnings.Any(w => w.StartsWith("Behaviour 'Lone'", StringComparison.Ordinal)),
                    "no warning names each dropped entry and its behaviour: " + string.Join(" | ", warnings));
            }
            using (var fixture = new Fixture())
            {
                XDocument behaviours = BuildBehaviors();
                behaviours.Descendants("NextBehaviorList").First().Add(S("BehaviorReference", A("Name", "Ghost"), A("Action", "Stand"),
                    A("Frequency", 4), A("Condition", "${Math.random() < 0.5}")));
                fixture.Behaviors(behaviours);
                Refusal(fixture, "TICK_NEXT_MISSING_SCRIPTED");
            }
            // Review D55-C: a dangling entry under an enclosing scripted Condition stays refused too; the source
            // evaluates that wrapper at every selection as it would the entry's own.
            using (var fixture = new Fixture())
            {
                XDocument behaviours = BuildBehaviors();
                behaviours.Descendants("NextBehaviorList").First().Add(S("Condition", A("Condition", "${mascot.lookRight}"),
                    S("BehaviorReference", A("Name", "Ghost"), A("Action", "Stand"), A("Frequency", 4))));
                fixture.Behaviors(behaviours);
                bool refused = false;
                try { TickPackEmitter.ConvertPack(fixture.Pack, fixture.Output); }
                catch (InvalidDataException error) { refused = error.Message.IndexOf("TICK_NEXT_MISSING_SCRIPTED", StringComparison.Ordinal) >= 0; }
                Require(refused, "a dangling entry under a scripted enclosing Condition was dropped, not refused");
            }
        }

        /// <summary>A synthetic 16-bit PCM mono RIFF/WAVE clip of <paramref name="samples"/> frames at 22.05 kHz.</summary>
        private static byte[] Wave(int samples, int seed)
        {
            var data = new byte[samples * 2];
            for (int i = 0; i < samples; i++)
            {
                short value = (short)(((i * 37 + seed) % 2000) - 1000);
                data[2 * i] = (byte)(value & 0xFF);
                data[2 * i + 1] = (byte)((value >> 8) & 0xFF);
            }
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + data.Length); writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(Encoding.ASCII.GetBytes("fmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                writer.Write(22050); writer.Write(44100); writer.Write((short)2); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(data.Length); writer.Write(data);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static void WriteBytes(Fixture fixture, string relative, byte[] bytes)
        {
            fixture.Write(relative, "");
            File.WriteAllBytes(Path.Combine(fixture.Pack, relative.Replace('/', Path.DirectorySeparatorChar)), bytes);
        }

        /// <summary>Sets Sound (and Volume) on the Stand action's first pose and Sound on its second.</summary>
        private static XDocument SoundActions(string first, string volume, string second)
        {
            XDocument actions = BuildActions();
            XElement[] poses = actions.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Stand").Descendants("Pose").ToArray();
            if (first != null) poses[0].SetAttributeValue("Sound", first);
            if (volume != null) poses[0].SetAttributeValue("Volume", volume);
            if (second != null) poses[1].SetAttributeValue("Sound", second);
            return actions;
        }

        // Pose Sound and Volume (grimoire/06 section 3, the WAV lane's rule): each Sound resolves as the source's
        // AnimationBuilder does (sound/, then sound/<set>/, then img/<set>/sound/), becomes one named WAV clip by its
        // Sound text with its bytes unchanged, and each pose names it with its Volume in dB; 'wav' is declared.
        private static void SoundLowering()
        {
            using (var fixture = new Fixture())
            {
                byte[] tone = Wave(400, 1), shadow = Wave(300, 2), beep = Wave(200, 3);
                WriteBytes(fixture, "sound/tone.wav", tone);
                WriteBytes(fixture, "img/A/sound/tone.wav", shadow);
                WriteBytes(fixture, "img/A/sound/beep.wav", beep);
                fixture.Actions(SoundActions("/tone.wav", "-6.5", "beep.wav"));
                // A dot path the source joins under sound/<set>/ and resolves inside the pack: sound/A/../../x.wav is x.wav.
                WriteBytes(fixture, "up.wav", Wave(150, 6));
                XDocument withUp = SoundActions("/tone.wav", "-6.5", "beep.wav");
                withUp.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Stand").Descendants("Pose").ElementAt(2)
                    .SetAttributeValue("Sound", "../../up.wav");
                fixture.Actions(withUp);
                XDocument document = fixture.Convert();
                Validate(document, "sounded member");
                Require(Features(document).Contains("wav"), "a member with clips declared no 'wav'");
                XElement[] sounds = document.Root.Element(Ns + "sounds").Elements(Ns + "sound").ToArray();
                Require(sounds.Length == 3 && (string)sounds[0].Attribute("id") == "/tone.wav" && (string)sounds[1].Attribute("id") == "beep.wav"
                    && (string)sounds[2].Attribute("id") == "../../up.wav"
                    && sounds.All(s => (string)s.Attribute("format") == "wav" && s.Element(Ns + "probability").Value == "0" && s.Element(Ns + "loop") == null),
                    "the clips are not one named WAV sound per Sound text");
                Require(Convert.FromBase64String(sounds[0].Element(Ns + "base64").Value).SequenceEqual(tone)
                    && Convert.FromBase64String(sounds[1].Element(Ns + "base64").Value).SequenceEqual(beep),
                    "a clip's bytes changed, or the source's resolution order (sound/ before img/<set>/sound/) was not kept");
                XElement[] poses = NamedNode(document, "Stand").Element(Ns + "act").Descendants(Ns + "pose").ToArray();
                Require((string)poses[0].Attribute("sound") == "/tone.wav" && (string)poses[0].Attribute("volume") == "-6.5"
                    && (string)poses[1].Attribute("sound") == "beep.wav" && poses[1].Attribute("volume") == null
                    && (string)poses[2].Attribute("sound") == "../../up.wav" && poses[3].Attribute("sound") == null,
                    "a pose lost its clip or its Volume");
            }
            using (var fixture = new Fixture())
            {
                XDocument plain = fixture.Convert();
                Require(plain.Root.Element(Ns + "sounds") == null && !Features(plain).Contains("wav"),
                    "a member without clips wrote sounds or declared 'wav'");
            }
        }

        // What the source cannot load refuses the member, by name: a Sound naming no file, MP3 bytes (whatever the
        // name), a WAVE the host's rule rejects, a Volume outside the gain range, a path out of the pack, an id over
        // 64 characters, and a clip larger than the host's per-clip limit.
        private static void SoundRefusal()
        {
            byte[] good = Wave(100, 4);
            byte[] mp3 = new byte[64];
            mp3[0] = 0x49; mp3[1] = 0x44; mp3[2] = 0x33;
            byte[] truncated = good.Take(good.Length - 10).ToArray();
            var cases = new[] {
                new { File = (string)null, Bytes = good, Sound = "/none.wav", Volume = (string)null, Expected = "TICK_SOUND_MISSING: Sound '/none.wav'" },
                new { File = "sound/fake.wav", Bytes = mp3, Sound = "/fake.wav", Volume = (string)null, Expected = "TICK_SOUND_MP3: clip '/fake.wav'" },
                new { File = "sound/cut.wav", Bytes = truncated, Sound = "/cut.wav", Volume = (string)null, Expected = "TICK_SOUND_WAV: clip '/cut.wav' is truncated" },
                new { File = "sound/ok.wav", Bytes = good, Sound = "/ok.wav", Volume = "7", Expected = "TICK_SOUND_VOLUME: Volume '7'" },
                new { File = "sound/ok.wav", Bytes = good, Sound = "/ok.wav", Volume = "loud", Expected = "TICK_SOUND_VOLUME: Volume 'loud'" },
                new { File = (string)null, Bytes = good, Sound = "../../../../ok.wav", Volume = (string)null, Expected = "TICK_SOUND_PATH: Sound '../../../../ok.wav' leaves the pack" },
                new { File = (string)null, Bytes = good, Sound = new string('s', 65), Volume = (string)null, Expected = "TICK_SOUND_ID_LIMIT" },
                new { File = "sound/big.wav", Bytes = Wave(DesktopAICompanion.CompanionXmlValidator.MaximumAudioBytesPerSound / 2 + 16, 5),
                    Sound = "/big.wav", Volume = (string)null, Expected = "TICK_SOUND_CLIP_LIMIT: the host refuses clip '/big.wav'" },
            };
            foreach (var c in cases)
                using (var fixture = new Fixture())
                {
                    if (c.File != null) WriteBytes(fixture, c.File, c.Bytes);
                    fixture.Actions(SoundActions(c.Sound, c.Volume, null));
                    Refusal(fixture, c.Expected);
                    Require(!Directory.Exists(fixture.Output), "sound refusal left output: " + c.Expected);
                }
        }

        // H3: a configuration file spelt otherwise on disk ("Actions.xml", "CONF") is named as the pack spells it in the
        // parity map's references, which are the source's refKeys; two entries that differ only in case refuse by name.
        private static void ConfigurationSpelling()
        {
            using (var fixture = new Fixture())
            {
                string conf = Path.Combine(fixture.Pack, "conf");
                File.Move(Path.Combine(conf, "actions.xml"), Path.Combine(conf, "Actions.xml"));
                fixture.Convert();
                using (JsonDocument map = JsonDocument.Parse(File.ReadAllText(fixture.MapPath)))
                {
                    string[] actionRefs = map.RootElement.GetProperty("actions").EnumerateObject().Select(p => p.Value.GetString()).ToArray();
                    Require(actionRefs.Length > 0 && actionRefs.All(r => r.StartsWith("A|conf/Actions.xml|", StringComparison.Ordinal)),
                        "a configuration file is named by the probe's spelling, not the pack's: " + actionRefs.FirstOrDefault());
                }
                File.Move(Path.Combine(conf, "Actions.xml"), Path.Combine(conf, "actions.xml"));
            }
            Require(ShimejiParser.SpelledEntry(new[] { "Img", "Conf" }, "conf") == "Conf"
                && ShimejiParser.SpelledEntry(new[] { "img" }, "conf") == "conf", "an entry is not matched ignoring case, or a missing one not left as given");
            bool refused = false;
            try { ShimejiParser.SpelledEntry(new[] { "actions.xml", "Actions.xml" }, "actions.xml"); }
            catch (InvalidDataException error) { refused = error.Message.StartsWith("SOURCE_PATH_AMBIGUOUS: 'actions.xml'", StringComparison.Ordinal); }
            Require(refused, "two entries differing only in case were not refused by name");
        }

        private static void InformationLowering()
        {
            string plain;
            using (var fixture = new Fixture())
                plain = fixture.Convert().ToString();
            using (var fixture = new Fixture())
            {
                fixture.Write("conf/info.xml", new XDocument(S("Mascot", A("version", "1"), S("Information", S("Name", "Synthetic"),
                    S("PreviewImage", "p.png"), S("Artist", A("Name", "someone"), A("URL", "https://example.invalid/")),
                    S("SplashImages", S("SplashImage", "q.png"))))).ToString());
                XDocument document = fixture.Convert();
                Validate(document, "member with an information document");
                Require(document.ToString() == plain, "information metadata changed the emitted member");
            }
            foreach (string list in new[] { "Constant", "ActionList", "BehaviorList" })
                using (var fixture = new Fixture())
                {
                    fixture.Write("conf/info.xml", new XDocument(S("Mascot", S("Information", S("Name", "Synthetic")), S(list))).ToString());
                    Refusal(fixture, "TICK_INFORMATION_CONFIGURATION");
                    Require(!Directory.Exists(fixture.Output), "information configuration refusal left output: " + list);
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

        /// <summary>A synthetic Japanese-vocabulary pose (ポーズ): image, anchor, velocity, duration.</summary>
        private static XElement JaPose(string image, int duration)
        {
            return S("ポーズ", A("画像", image), A("基準座標", "2,3"), A("移動速度", "0,0"), A("長さ", duration));
        }

        /// <summary>A Japanese action document's enumerated values, read through its own schema as the source compares
        /// them (ActionBuilder.java:101-138, BorderedAction.java:44-49): every Type value and every BorderType value of
        /// schema_ja, the literal BorderType written mapped both as the act's border and as its parameter (the host
        /// reads the parameter first), and a reference binding of BorderType mapped too. Replaces action-schema-refusal,
        /// which refused every Japanese action document outright; its successor refusal is japanese-schema-refusal.</summary>
        private static void JapaneseSchemaValues()
        {
            using (var fixture = new Fixture())
            {
                fixture.Actions(new XDocument(S("Mascot", S("動作リスト",
                    S("動作", A("名前", "Stand"), A("種類", "静止"), A("枠", "地面"), S("アニメーション", JaPose("/p.png", 2))),
                    S("動作", A("名前", "Climb"), A("種類", "移動"), A("枠", "壁"), A("目的地X", 5), S("アニメーション", JaPose("/q.png", 2))),
                    S("動作", A("名前", "Hang"), A("種類", "固定"), A("枠", "天井"), S("アニメーション", JaPose("/p.png", 3))),
                    S("動作", A("名前", "Drop"), A("種類", "組み込み"), A("クラス", "com.group_finity.mascot.action.Fall"),
                        S("アニメーション", JaPose("/q.png", 1))),
                    S("動作", A("名前", "Pick"), A("種類", "選択"), S("動作参照", A("名前", "Stand")), S("動作参照", A("名前", "Hang"))),
                    S("動作", A("名前", "Chain"), A("種類", "複合"), A("繰り返し", "true"),
                        S("動作参照", A("名前", "Stand"), A("枠", "天井")), S("動作参照", A("名前", "Climb")))))));
                fixture.Behaviors(new XDocument(S("Mascot", S("行動リスト",
                    S("行動", A("名前", "Main"), A("動作", "Chain"), A("頻度", 3)),
                    S("行動", A("名前", "Other"), A("動作", "Pick"), A("頻度", 1)),
                    S("行動", A("名前", "Drop"), A("頻度", 0))))));
                XDocument document = fixture.Convert();
                Validate(document, "Japanese-vocabulary member");
                var expected = new[] { new[] { "Stand", "Stay", "Floor" }, new[] { "Climb", "Move", "Wall" }, new[] { "Hang", "Animate", "Ceiling" },
                    new[] { "Drop", "Embedded", null }, new[] { "Pick", "Select", null }, new[] { "Chain", "Sequence", null } };
                foreach (string[] row in expected)
                {
                    XElement act = NamedNode(document, row[0]).Element(Ns + "act");
                    Require((string)act.Attribute("type") == row[1],
                        "Japanese Type of " + row[0] + " became '" + (string)act.Attribute("type") + "', expected " + row[1]);
                    Require((string)act.Attribute("border") == row[2],
                        "Japanese BorderType of " + row[0] + " became '" + (string)act.Attribute("border") + "', expected " + (row[2] ?? "none"));
                    if (row[2] != null)
                        Require(Bound(act, "param", "BorderType") == row[2],
                            "Japanese BorderType parameter of " + row[0] + " kept its source value: " + Bound(act, "param", "BorderType"));
                }
                Require((string)NamedNode(document, "Drop").Element(Ns + "act").Attribute("class") == "Fall", "Japanese Embedded class lost");
                XElement chain = NamedNode(document, "Chain").Element(Ns + "act");
                Require(Bound(chain.Element(Ns + "children").Elements(Ns + "child").First(), "bind", "BorderType") == "Ceiling",
                    "a Japanese BorderType reference binding kept its source value");
                Require((string)chain.Element(Ns + "children").Attribute("mode") == "sequence"
                    && (string)NamedNode(document, "Pick").Element(Ns + "act").Element(Ns + "children").Attribute("mode") == "select",
                    "a Japanese composite lost its mode");
                // The keys the source also reads by value have no Japanese override, so their values pass through
                // unmapped; if the facts ever gave them one, these would need mapping too.
                foreach (string key in new[] { "Origin", "Breed", "Scan" })
                    Require(ShimejiParser.SchemaString(ShimejiSourceSchema.Japanese, key) == key,
                        "the Japanese schema now overrides the by-value key " + key);
            }
        }

        /// <summary>The Japanese engine behaviours: a member whose behaviour document is Japanese enters 落下する,
        /// ドラッグされる and 投げられる on a ground loss, a drag and a release (UserBehavior.java, through the configuration's
        /// schema), which the host enters as Fall, Dragged and Thrown, so each takes that name everywhere a literal
        /// names it: the route, a next reference, a hotspot and a BornBehavior. Other names stay as they are.</summary>
        private static void JapaneseEngineNames()
        {
            using (var fixture = new Fixture())
            {
                fixture.Actions(JapaneseEngineActions(A("生まれた時の行動", "落下する")));
                fixture.Behaviors(JapaneseEngineBehaviours());
                XDocument document = fixture.Convert();
                Validate(document, "Japanese engine-name member");
                string[] routeNames = Nodes(document).Single(n => n.Element(Ns + "route") != null).Element(Ns + "sequence").Elements(Ns + "next")
                    .Select(e => (string)Nodes(document).Single(n => n.Attribute("id").Value == e.Value).Element(Ns + "behaviour").Attribute("name")).ToArray();
                Require(new[] { "Fall", "Dragged", "Thrown" }.All(routeNames.Contains),
                    "route behaviours do not carry the host's engine names: " + string.Join(", ", routeNames));
                foreach (string name in new[] { "Fall", "Dragged", "Thrown", "Main" })
                    Require(RootBehavior(document, name).Element(Ns + "name").Value == "beh:" + name,
                        "route behaviour " + name + " missing after the engine-name mapping");
                Require(!Nodes(document).Any(n => n.Element(Ns + "name").Value.IndexOf("落下する", StringComparison.Ordinal) >= 0
                    || (string)n.Element(Ns + "behaviour")?.Attribute("name") == "落下する"), "a Japanese engine name survived");
                XElement next = Nodes(document).Single(n => n.Attribute("id").Value
                    == RootBehavior(document, "Main").Element(Ns + "sequence").Elements(Ns + "next").Single().Value);
                Require((string)next.Element(Ns + "behaviour").Attribute("name") == "Fall", "a next reference to 落下する was not renamed");
                Require(Bound(NamedNode(document, "Born").Element(Ns + "act"), "param", "BornBehavior") == "Fall",
                    "a literal BornBehavior naming 落下する was not renamed");
                XElement hotspot = NamedNode(document, "Stand").Descendants(Ns + "hotspot").Single();
                Require((string)hotspot.Attribute("behaviour") == "Thrown", "a hotspot naming 投げられる was not renamed");
            }
        }

        private static XDocument JapaneseEngineActions(XAttribute bornBehaviour)
        {
            return new XDocument(S("Mascot", S("動作リスト",
                S("動作", A("名前", "Stand"), A("種類", "静止"), A("枠", "地面"), S("アニメーション", JaPose("/p.png", 2),
                    S("Hotspot", A("Shape", "Rectangle"), A("Origin", "0,0"), A("Size", "2,2"), A("行動", "投げられる")))),
                S("動作", A("名前", "Drop"), A("種類", "組み込み"), A("クラス", "com.group_finity.mascot.action.Fall"), S("アニメーション", JaPose("/q.png", 1))),
                S("動作", A("名前", "Hold"), A("種類", "組み込み"), A("クラス", "com.group_finity.mascot.action.Dragged"), S("アニメーション", JaPose("/p.png", 1))),
                S("動作", A("名前", "Born"), A("種類", "組み込み"), A("クラス", "com.group_finity.mascot.action.Breed"), A("長さ", 4), bornBehaviour,
                    S("アニメーション", JaPose("/q.png", 4))))));
        }

        private static XDocument JapaneseEngineBehaviours(params XElement[] extra)
        {
            return new XDocument(S("Mascot", S("行動リスト",
                S("行動", A("名前", "Main"), A("動作", "Stand"), A("頻度", 3),
                    S("次の行動リスト", A("追加", "true"), S("行動参照", A("名前", "落下する"), A("動作", "Drop"), A("頻度", 1)))),
                S("行動", A("名前", "落下する"), A("動作", "Drop"), A("頻度", 0)),
                S("行動", A("名前", "ドラッグされる"), A("動作", "Hold"), A("頻度", 0)),
                S("行動", A("名前", "投げられる"), A("動作", "Drop"), A("頻度", 0)),
                S("行動", A("名前", "Breeding"), A("動作", "Born"), A("頻度", 0)),
                extra)));
        }

        /// <summary>What the engine-name mapping cannot follow refuses by name: an English "Fall" that is an ordinary
        /// behaviour under a Japanese schema (the host would enter it on events), a scripted behaviour name in a
        /// renaming member, and a literal engine name in a pack whose members look those names up in different
        /// schemas.</summary>
        private static void JapaneseEngineNameRefusal()
        {
            using (var fixture = new Fixture())
            {
                fixture.Actions(JapaneseEngineActions(A("生まれた時の行動", "Main")));
                fixture.Behaviors(JapaneseEngineBehaviours(S("行動", A("名前", "Fall"), A("動作", "Drop"), A("頻度", 0))));
                Refusal(fixture, "TICK_ENGINE_NAME_COLLISION: 'Fall'");
                Require(!Directory.Exists(fixture.Output), "engine-name collision left output");
            }
            using (var fixture = new Fixture())
            {
                fixture.Actions(JapaneseEngineActions(A("生まれた時の行動", "${'Main'}")));
                fixture.Behaviors(JapaneseEngineBehaviours());
                Refusal(fixture, "TICK_ENGINE_NAME_SCRIPTED 生まれた時の行動");
            }
            using (var fixture = new Fixture())
            {
                fixture.Actions(JapaneseEngineActions(A("生まれた時の行動", "Main")));
                fixture.Behaviors(JapaneseEngineBehaviours());
                fixture.Member("B");
                XDocument english = BuildActions();
                english.Descendants("Action").Single(a => (string)a.Attribute("Name") == "Born").SetAttributeValue("BornBehavior", "Fall");
                fixture.Write("conf/B/actions.xml", english.ToString());
                fixture.Write("conf/B/behaviors.xml", BuildBehaviors().ToString());
                Refusal(fixture, "TICK_ENGINE_NAME_MIXED BornBehavior");
            }
            // Review Q7: TargetBehavior names a behaviour of the PARTNER, whose schema this member cannot know, so a
            // renaming member's literal Japanese engine name refuses rather than be renamed for a partner that may not be.
            using (var fixture = new Fixture())
            {
                XDocument actions = JapaneseEngineActions(A("生まれた時の行動", "Main"));
                actions.Root.Element("動作リスト").Add(S("動作", A("名前", "Seek"), A("種類", "組み込み"),
                    A("クラス", "com.group_finity.mascot.action.ScanMove"), A("Affordance", "Hug"), A("行動", "Main"),
                    A("TargetBehavior", "落下する"), S("アニメーション", JaPose("/p.png", 2))));
                fixture.Actions(actions);
                fixture.Behaviors(JapaneseEngineBehaviours());
                Refusal(fixture, "TICK_ENGINE_NAME_PARTNER TargetBehavior: '落下する'");
                Require(!Directory.Exists(fixture.Output), "a partner engine-name refusal left output");
            }
        }

        /// <summary>A Japanese document's value with no source mapping refuses by name: English Type and BorderType
        /// values (the source compares them with 静止 and 地面 and matches nothing), and a scripted BorderType, whose
        /// Japanese value the host could not map at run time.</summary>
        private static void JapaneseSchemaRefusal()
        {
            var cases = new[] {
                new { Type = "Stay", Border = "地面", Expected = "TICK_SCHEMA_VALUE_UNMAPPED Type: 'Stay'" },
                new { Type = "静止", Border = "Floor", Expected = "TICK_SCHEMA_VALUE_UNMAPPED BorderType: 'Floor'" },
                new { Type = "静止", Border = "${'地面'}", Expected = "TICK_SCHEMA_SCRIPTED_VALUE 枠" },
            };
            foreach (var c in cases)
                using (var fixture = new Fixture())
                {
                    fixture.Actions(new XDocument(S("Mascot", S("動作リスト", S("動作", A("名前", "Stand"),
                        A("種類", c.Type), A("枠", c.Border), S("アニメーション", JaPose("/p.png", 2)))))));
                    fixture.Behaviors(new XDocument(S("Mascot", S("行動リスト", S("行動", A("名前", "Main"),
                        A("動作", "Stand"), A("頻度", 1))))));
                    Refusal(fixture, c.Expected);
                    Require(!Directory.Exists(fixture.Output), "Japanese value refusal left output");
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
