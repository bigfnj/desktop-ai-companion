using System;
using System.IO;
using System.Text;
using System.Xml.Serialization;

namespace DesktopAICompanion
{
    /// <summary>
    /// The Shimeji runtime extensions' gate (grimoire/06 sections 1 and 10): a companion that uses an extension this
    /// build does not run is refused BY FEATURE NAME, an extension used without being declared is refused, and a
    /// well-formed tick-clock companion validates when its features are supported. Built from the bundled pet's
    /// header, image and icon, so no new art is involved.
    /// </summary>
    internal static partial class SecuritySelfTest
    {
        private static readonly string[] AllExtensionFeatures =
        {
            CompanionFormatFeatures.Tick, CompanionFormatFeatures.Actions, CompanionFormatFeatures.Behaviours,
            CompanionFormatFeatures.Expressions,
        };

        private static void CheckShimejiExtensionGate(string defaultXml, ref int failures, TextWriter output)
        {
            XmlData.RootNode classic;
            string error;
            if (!CompanionXmlValidator.TryParse(defaultXml, out classic, out error))
            {
                Check(false, "extension gate: the bundled pet parses as the fixture's base (" + error + ")", ref failures, output);
                return;
            }

            // The production gate (this build's Supported set, which the app's runtime fills): a tick-clock companion that
            // uses only what this build runs is accepted...
            string tickDoc = Serialize(TickFixture(classic));
            XmlData.RootNode parsed;
            Check(CompanionXmlValidator.TryParse(tickDoc, out parsed, out error),
                "extension gate: this build accepts a tick-clock companion that uses only features it runs" + FormatError(error),
                ref failures, output);
            Check(CompanionXmlValidator.TryParseWithFeatures(tickDoc, AllExtensionFeatures, out parsed, out error),
                "extension gate: the same companion validates when exactly its features are supported" + FormatError(error),
                ref failures, output);
            // ...and one declaring a feature this build does not run is refused by that feature's name.
            string notRun = null;
            foreach (string token in CompanionFormatFeatures.All)
                if (!System.Linq.Enumerable.Contains(CompanionFormatFeatures.Supported, token)) { notRun = token; break; }
            if (notRun != null)
            {
                string unrun = Serialize(With(TickFixture(classic), r => r.Header.Features += " " + notRun));
                Check(!CompanionXmlValidator.TryParse(unrun, out parsed, out error)
                      && Contains(error, "uses feature '" + notRun + "', which this version of Desktop AI Companion does not run yet"),
                    "extension gate: a companion declaring a feature this build does not run is refused by its name (" + error + ")",
                    ref failures, output);
            }
            else output.WriteLine("NOTE extension gate: every defined feature runs in this build; the unsupported-feature refusal is not exercised");

            CheckClassGate(classic, ref failures, output);

            string unknown = Serialize(With(TickFixture(classic), r => r.Header.Features += " teleport"));
            Check(!CompanionXmlValidator.TryParseWithFeatures(unknown, AllExtensionFeatures, out parsed, out error)
                  && Contains(error, "needs a newer Desktop AI Companion (feature 'teleport')"),
                "extension gate: an unknown feature asks for a newer app, by name (" + error + ")", ref failures, output);

            string undeclaredClock = Serialize(With(Clone(classic), r => r.Header.Clock = "tick"));
            Check(!CompanionXmlValidator.TryParseWithFeatures(undeclaredClock, AllExtensionFeatures, out parsed, out error)
                  && Contains(error, "needs feature 'tick'"),
                "extension gate: <clock> without the 'tick' feature declared is refused (" + error + ")", ref failures, output);

            string undeclaredWhen = Serialize(With(TickFixture(classic), r => r.Header.Features = "tick actions behaviours"));
            Check(!CompanionXmlValidator.TryParseWithFeatures(undeclaredWhen, AllExtensionFeatures, out parsed, out error)
                  && Contains(error, "transition condition needs feature 'expressions'"),
                "extension gate: a transition condition without 'expressions' declared is refused AT the condition (" + error + ")", ref failures, output);

            string partialSupport = tickDoc;
            Check(!CompanionXmlValidator.TryParseWithFeatures(partialSupport,
                      new[] { CompanionFormatFeatures.Tick, CompanionFormatFeatures.Behaviours, CompanionFormatFeatures.Expressions },
                      out parsed, out error) && Contains(error, "feature 'actions'"),
                "extension gate: a host that runs some features but not 'actions' refuses by that name (" + error + ")", ref failures, output);

            int tiles = classic.Image.TilesX * classic.Image.TilesY;
            ExpectRefused(classic, r => StandAct(r).Class = "Teleport", "embedded class 'Teleport'",
                "an unknown embedded class is refused", ref failures, output, r => StandAct(r).Type = "Embedded");
            ExpectRefused(classic, r => StandAct(r).Variants.Variant[0].Pose[0].Frame = tiles, "invalid frame",
                "a pose frame past the sheet is refused", ref failures, output);
            ExpectRefused(classic, r => StandAct(r).Guard.When = "Bogus > 1", "unknown name 'Bogus'",
                "a guard naming something outside its scope is refused by that name", ref failures, output);
            ExpectRefused(classic, r => r.Animations.Animation[1].Behaviour.Runs = "Missing", "runs 'Missing'",
                "a behaviour that runs a missing action is refused", ref failures, output);
            ExpectRefused(classic, r => r.Animations.Animation[0].Sequence.Frame = new[] { 0 }, "classic frames",
                "a tick-clock node with classic frames is refused", ref failures, output);
            ExpectRefused(classic, r =>
                {
                    var extra = XmlData.NodeCopy.Deep(r.Animations.Animation[0]);
                    extra.Id = 9;
                    var list = new System.Collections.Generic.List<XmlData.AnimationNode>(r.Animations.Animation) { extra };
                    r.Animations.Animation = list.ToArray();
                }, "exactly one <route>", "two route nodes are refused", ref failures, output);

            // WITNESS: an image-less pose (frame -1) is legal; it hides the companion, it is not an error.
            string hidden = Serialize(With(TickFixture(classic), r => StandAct(r).Variants.Variant[0].Pose[1].Frame = -1));
            Check(CompanionXmlValidator.TryParseWithFeatures(hidden, AllExtensionFeatures, out parsed, out error),
                "extension gate: WITNESS an image-less pose (frame -1) validates" + FormatError(error), ref failures, output);

            // WITNESS: a classic companion is untouched by any of this.
            Check(CompanionXmlValidator.TryParseWithFeatures(defaultXml, new string[0], out parsed, out error),
                "extension gate: WITNESS the bundled classic pet validates with no features supported" + FormatError(error),
                ref failures, output);

            // NodeCopy is complete: a deep copy serialises byte-identically to its source, extension fields included...
            XmlData.RootNode fixture = TickFixture(classic);
            Check(Serialize(XmlData.NodeCopy.Deep(fixture)) == Serialize(fixture),
                "extension gate: NodeCopy.Deep copies every field of a tick-clock document", ref failures, output);
            // ...and independent: changing the copy leaves the source alone (a shallow copy serialises identically too).
            XmlData.RootNode copy = XmlData.NodeCopy.Deep(fixture);
            StandAct(copy).Variants.Variant[0].Pose[0].Frame = 99;
            copy.Animations.Animation[0].Sequence.Next[0].When = "false";
            Check(StandAct(fixture).Variants.Variant[0].Pose[0].Frame == 0 && fixture.Animations.Animation[0].Sequence.Next[0].When != "false",
                "extension gate: a NodeCopy.Deep copy shares no node or array with its source", ref failures, output);

            // The tick-mode limits are the raised ones: 1100 nodes (classic allows 1024) and a 300-edge root list
            // (classic allows 256) validate.
            XmlData.RootNode many = TickFixture(classic);
            var nodes = new System.Collections.Generic.List<XmlData.AnimationNode>(many.Animations.Animation);
            for (int i = 0; i < 1100; i++)
            {
                XmlData.AnimationNode act = XmlData.NodeCopy.Deep(many.Animations.Animation[2]);
                act.Id = 100 + i;
                act.Name = "Stand" + i;
                nodes.Add(act);
            }
            many.Animations.Animation = nodes.ToArray();
            Check(CompanionXmlValidator.TryParseWithFeatures(Serialize(many), AllExtensionFeatures, out parsed, out error),
                "extension gate: a tick-clock companion of 1103 nodes validates (tick-mode limit 2048, classic 1024)" + FormatError(error),
                ref failures, output);
            XmlData.RootNode wide = TickFixture(classic);
            var edges = new XmlData.NextNode[300];
            for (int i = 0; i < edges.Length; i++) edges[i] = new XmlData.NextNode { Value = 2, Probability = 1 };
            wide.Animations.Animation[0].Sequence.Next = edges;
            Check(CompanionXmlValidator.TryParseWithFeatures(Serialize(wide), AllExtensionFeatures, out parsed, out error),
                "extension gate: a 300-edge root behaviour list validates (tick-mode limit 1024, classic 256)" + FormatError(error),
                ref failures, output);

            // A behaviour whose NextBehaviorList replaces the root list (add="false") with transition-only entries
            // weighs 0 in total. The source then drops the companion in and enters Fall, so in tick mode that is legal.
            XmlData.RootNode zero = TickFixture(classic);
            XmlData.AnimationNode replacing = zero.Animations.Animation[1];
            replacing.Behaviour.Add = false;
            replacing.Behaviour.AddSpecified = true;
            replacing.Sequence.Next = new[] { new XmlData.NextNode { Value = 2, Probability = 0 } };
            Check(CompanionXmlValidator.TryParseWithFeatures(Serialize(zero), AllExtensionFeatures, out parsed, out error),
                "extension gate: a replacing behaviour list (add=false) that weighs 0 validates in tick mode" + FormatError(error),
                ref failures, output);
            // WITNESS: a classic pet has no such fallback, and a list of its that weighs 0 is still refused.
            XmlData.RootNode classicZero = Clone(classic);
            XmlData.AnimationNode withEdges = Array.Find(classicZero.Animations.Animation,
                a => a != null && a.Sequence != null && a.Sequence.Next != null && a.Sequence.Next.Length > 0);
            if (withEdges != null)
                foreach (XmlData.NextNode n in withEdges.Sequence.Next) n.Probability = 0;
            Check(withEdges != null
                  && !CompanionXmlValidator.TryParseWithFeatures(Serialize(classicZero), new string[0], out parsed, out error)
                  && Contains(error, "has no positive-probability transition"),
                "extension gate: WITNESS a classic pet whose list weighs 0 is still refused (" + error + ")", ref failures, output);

            // A next-list entry is a behaviour node of its own, off the route, sharing its root behaviour's name.
            XmlData.RootNode entry = WithNextListEntry(TickFixture(classic), "Stand");
            Check(CompanionXmlValidator.TryParseWithFeatures(Serialize(entry), AllExtensionFeatures, out parsed, out error),
                "extension gate: a next-list entry sharing its root behaviour's name validates" + FormatError(error), ref failures, output);
            ExpectRefused(classic, r => WithNextListEntry(r, "Ghost"), "which no behaviour on the route is",
                "an off-route behaviour whose name no root behaviour has is refused", ref failures, output);
            ExpectRefused(classic, r =>
                {
                    WithNextListEntry(r, "Stand");
                    r.Animations.Animation[0].Sequence.Next = new[]
                    {
                        new XmlData.NextNode { Value = 2, Probability = 1 }, new XmlData.NextNode { Value = 4, Probability = 1 },
                    };
                }, "unique among the route's behaviours", "two route behaviours with one name are refused", ref failures, output);
            ExpectRefused(classic, r => r.Animations.Animation[0].Sequence.Next[0].Value = 3, "which is not a behaviour",
                "a route edge to an action node is refused", ref failures, output);
            ExpectRefused(classic, r => r.Animations.Animation[2].Sequence.Next = new[] { new XmlData.NextNode { Value = 2, Probability = 1 } },
                "is an action with an edge", "an action node with an edge is refused", ref failures, output);

            CheckUsageAndShapeRules(classic, ref failures, output);
            CheckPoseGeometryRules(classic, ref failures, output);
            CheckPackKeys(classic, ref failures, output);
        }

        /// <summary>
        /// A pack id is compact; a member key is the source's image-set name, exactly. Codex executed the counterexample
        /// against one grammar serving both: members "Egg (2)" and "A+B" were refused, and a key cannot be rewritten because
        /// a scripted TransformMascot or BornMascot names it.
        /// </summary>
        private static void CheckPackKeys(XmlData.RootNode classic, ref int failures, TextWriter output)
        {
            var supported = new System.Collections.Generic.List<string>(AllExtensionFeatures) { CompanionFormatFeatures.Packs };
            Func<string, string, string, string> refusal = (id, member, startupMember) =>
            {
                XmlData.RootNode r = TickFixture(classic);
                r.Header.Features += " packs";
                r.Header.Pack = new XmlData.PackNode { Id = id, Member = member, Role = "main" };
                if (startupMember != null) r.Header.Startup = new XmlData.StartupNode { Member = startupMember, Behaviour = "Hatch" };
                XmlData.RootNode parsed;
                string error;
                return CompanionXmlValidator.TryParseWithFeatures(Serialize(r), supported, out parsed, out error) ? null : (error ?? "refused");
            };
            string verdict = refusal("smoke-pack", "Egg (2)", null);
            Check(verdict == null, "pack keys: a member named as the source names an image set (\"Egg (2)\") validates" + FormatError(verdict), ref failures, output);
            verdict = refusal("smoke-pack", "A+B", "Egg (2)");
            Check(verdict == null, "pack keys: member \"A+B\" with startup member \"Egg (2)\" validates" + FormatError(verdict), ref failures, output);
            verdict = refusal("smoke-pack", new string('x', 255), null);
            Check(verdict == null, "pack keys: a 255-character member validates" + FormatError(verdict), ref failures, output);
            foreach (string bad in new[] { "a/b", "a:b", "x|y", "   ", new string('x', 256) })
            {
                verdict = refusal("smoke-pack", bad, null);
                Check(verdict != null && Contains(verdict, "header/pack member"),
                    "pack keys: member '" + (bad.Length > 20 ? bad.Length + " characters" : bad) + "' is refused (" + verdict + ")", ref failures, output);
            }
            verdict = refusal("smoke-pack", "Alpha", "x|y");
            Check(verdict != null && Contains(verdict, "header/startup member"),
                "pack keys: a startup member 'x|y' is refused (" + verdict + ")", ref failures, output);
            verdict = refusal("A+B", "Alpha", null);
            Check(verdict != null && Contains(verdict, "header/pack id"),
                "pack keys: the pack id keeps the compact grammar ('A+B' is refused) (" + verdict + ")", ref failures, output);
            // A control character never reaches a key (the index joins pack id and member with a line feed); checked on the
            // rule itself, since an XML attribute may normalise a tab or line feed on the way in.
            Check(!ShimejiFormatValidator.IsMemberKey("a\nb") && !ShimejiFormatValidator.IsMemberKey("a\tb")
                  && !ShimejiFormatValidator.IsMemberKey("a\u0085b") && ShimejiFormatValidator.IsMemberKey("a b"),
                "pack keys: a member key holds no control character", ref failures, output);
        }

        /// <summary>
        /// Per-pose image geometry (grimoire/06 section 3): each image stays inside its cell (w, h, rw, rh from 1 to the
        /// cell size), rw and rh come only with a right frame, the anchor stays within -4096..4096, and a document using
        /// any of them declares 'tick'. Each bound is probed just past its edge, and a witness uses all six AT their edges.
        /// </summary>
        private static void CheckPoseGeometryRules(XmlData.RootNode classic, ref int failures, TextWriter output)
        {
            int sheetWidth, sheetHeight;
            if (!Shimeji.TickType.TryReadPngSize(classic.Image.Png, out sheetWidth, out sheetHeight))
            {
                Check(false, "extension gate: the bundled pet's sheet size is readable for the pose geometry checks", ref failures, output);
                return;
            }
            int cellWidth = sheetWidth / classic.Image.TilesX, cellHeight = sheetHeight / classic.Image.TilesY;
            int limit = ShimejiFormatValidator.MaximumPoseAnchor;
            Func<XmlData.RootNode, XmlData.PoseNode> pose = r => StandAct(r).Variants.Variant[0].Pose[0];

            ExpectRefused(classic, r => { pose(r).Width = cellWidth + 1; pose(r).WidthSpecified = true; },
                "image width w is not 1 to the cell width", "a pose image wider than its cell is refused", ref failures, output);
            ExpectRefused(classic, r => { pose(r).Width = 0; pose(r).WidthSpecified = true; },
                "image width w is not 1 to the cell width", "a pose image 0 wide is refused", ref failures, output);
            ExpectRefused(classic, r => { pose(r).Height = cellHeight + 1; pose(r).HeightSpecified = true; },
                "image height h is not 1 to the cell height", "a pose image taller than its cell is refused", ref failures, output);
            ExpectRefused(classic, r => { pose(r).RightWidth = 10; pose(r).RightWidthSpecified = true; },
                "giving rw or rh without a right frame", "a right image size without a right frame is refused", ref failures, output);
            ExpectRefused(classic, r =>
                {
                    pose(r).Right = 1; pose(r).RightSpecified = true;
                    pose(r).RightWidth = cellWidth + 1; pose(r).RightWidthSpecified = true;
                }, "right image width rw is not 1 to the cell width", "a right image wider than its cell is refused", ref failures, output);
            ExpectRefused(classic, r =>
                {
                    pose(r).Right = 1; pose(r).RightSpecified = true;
                    pose(r).RightHeight = 0; pose(r).RightHeightSpecified = true;
                }, "right image height rh is not 1 to the cell height", "a right image 0 high is refused", ref failures, output);
            ExpectRefused(classic, r => { pose(r).AnchorX = limit + 1; pose(r).AnchorXSpecified = true; },
                "anchor (ax, ay) is outside", "a pose anchor x past +" + limit + " is refused", ref failures, output);
            ExpectRefused(classic, r => { pose(r).AnchorY = -limit - 1; pose(r).AnchorYSpecified = true; },
                "anchor (ax, ay) is outside", "a pose anchor y past -" + limit + " is refused", ref failures, output);
            ExpectRefused(classic, r =>
                {
                    r.Header.Clock = null;
                    r.Header.Features = "actions behaviours expressions";
                    pose(r).Width = 1; pose(r).WidthSpecified = true;
                }, "pose geometry (ax, ay, w, h, rw, rh) needs feature 'tick'",
                "pose geometry without 'tick' declared is refused AT the pose", ref failures, output);

            // WITNESS: every attribute at its edge validates (the bounds are inclusive), so the refusals above are the
            // bounds and not the attributes.
            XmlData.RootNode edges = With(TickFixture(classic), r =>
                {
                    XmlData.PoseNode p = pose(r);
                    p.Width = cellWidth; p.WidthSpecified = true;
                    p.Height = 1; p.HeightSpecified = true;
                    p.Right = 1; p.RightSpecified = true;
                    p.RightWidth = 1; p.RightWidthSpecified = true;
                    p.RightHeight = cellHeight; p.RightHeightSpecified = true;
                    p.AnchorX = limit; p.AnchorXSpecified = true;
                    p.AnchorY = -limit; p.AnchorYSpecified = true;
                });
            XmlData.RootNode parsed;
            string error;
            Check(CompanionXmlValidator.TryParseWithFeatures(Serialize(edges), AllExtensionFeatures, out parsed, out error),
                "extension gate: WITNESS a pose using all six geometry attributes at their edges validates" + FormatError(error),
                ref failures, output);
            XmlData.PoseNode read = parsed == null ? null : parsed.Animations.Animation[2].Act.Variants.Variant[0].Pose[0];
            Check(read != null && read.Width == cellWidth && read.Height == 1 && read.RightWidth == 1 && read.RightHeight == cellHeight
                  && read.AnchorX == limit && read.AnchorY == -limit && read.RightWidthSpecified && !parsed.Animations.Animation[2].Act.Variants.Variant[0].Pose[1].WidthSpecified,
                "extension gate: the pose geometry round-trips through the schema and the parser, and an absent attribute stays absent",
                ref failures, output);
        }

        /// <summary>
        /// What a document USES must be declared, judged from what it contains rather than from its text, and its shape
        /// must be one the runtime can build: the counterexamples the format review executed against the full validator.
        /// </summary>
        private static void CheckUsageAndShapeRules(XmlData.RootNode classic, ref int failures, TextWriter output)
        {
            // A scripted binding is an expression, on a behaviour or on a composite child.
            ExpectRefused(classic, r =>
                {
                    r.Header.Features = "tick actions behaviours";
                    r.Animations.Animation[0].Sequence.Next[0].When = null;
                    StandAct(r).Guard = null;
                    r.Animations.Animation[1].Behaviour.Bind = new[] { new XmlData.BindNode { Name = "Duration", Value = "Math.random() * 10", Cache = "action" } };
                }, "binding expression needs feature 'expressions'", "a scripted binding without 'expressions' declared is refused",
                ref failures, output);
            // mascot.variables, found by the parser rather than by the text.
            ExpectRefused(classic, r => StandAct(r).Guard.When = "mascot.variables['k'] == null",
                "mascot.variables needs feature 'variables'", "a guard using mascot.variables without 'variables' declared is refused",
                ref failures, output);
            XmlData.RootNode withVariables = With(TickFixture(classic), r =>
                {
                    r.Header.Features += " variables";
                    StandAct(r).Guard.When = "mascot.variables['k'] == null";
                });
            XmlData.RootNode parsed;
            string error;
            var withVariablesSupported = new System.Collections.Generic.List<string>(AllExtensionFeatures) { CompanionFormatFeatures.Variables };
            Check(CompanionXmlValidator.TryParseWithFeatures(Serialize(withVariables), withVariablesSupported, out parsed, out error),
                "extension gate: WITNESS the same guard validates with 'variables' declared" + FormatError(error), ref failures, output);
            // A scripted Characteristics could turn out to be either flag, so it needs both features.
            ExpectRefused(classic, r =>
                {
                    StandAct(r).Type = "Embedded";
                    StandAct(r).Class = "ComplexMove";
                    StandAct(r).Params = new[]
                    {
                        new XmlData.ParamNode { Name = "Duration", Value = "20", Cache = "none" },
                        new XmlData.ParamNode { Name = "Characteristics", Value = "'B' + 'reed'", Cache = "action" },
                    };
                }, "Characteristics Breed needs feature 'lifecycle'", "a scripted Characteristics without 'lifecycle' declared is refused",
                ref failures, output);
            // Naming another member is a pack's business, as a parameter or as a binding.
            ExpectRefused(classic, r =>
                {
                    var ps = new System.Collections.Generic.List<XmlData.ParamNode>(StandAct(r).Params)
                        { new XmlData.ParamNode { Name = "BornMascot", Value = "Beta", Cache = "none" } };
                    StandAct(r).Params = ps.ToArray();
                }, "BornMascot (another pack member) needs feature 'packs'", "a BornMascot parameter without 'packs' declared is refused",
                ref failures, output);
            ExpectRefused(classic, r => r.Animations.Animation[1].Behaviour.Bind = new[]
                    { new XmlData.BindNode { Name = "TransformMascot", Value = "Beta", Cache = "none" } },
                "binding TransformMascot (another pack member) needs feature 'packs'",
                "a TransformMascot binding without 'packs' declared is refused", ref failures, output);
            // Toggles: a behaviour names a declared one, and ids are distinct.
            ExpectRefused(classic, r => r.Animations.Animation[1].Behaviour.Toggle = "Ghost",
                "names toggle 'Ghost', which header/toggles does not declare", "a behaviour naming an undeclared toggle is refused",
                ref failures, output);
            ExpectRefused(classic, r => r.Header.Toggles = new XmlData.TogglesNode
                    { Toggle = new[] { new XmlData.ToggleNode { Id = "T", Label = "a" }, new XmlData.ToggleNode { Id = "T", Label = "b" } } },
                "repeated toggle id", "two header toggles with one id are refused", ref failures, output);
            // Composite activation is finite: no self-containment, bounded depth and size.
            ExpectRefused(classic, r => AddComposites(r, 1, 1, true), "contains itself through its children",
                "a composite that contains itself is refused", ref failures, output);
            ExpectRefused(classic, r => AddComposites(r, ShimejiFormatValidator.MaximumCompositeDepth + 1, 1, false),
                "deep", "composites nested past the depth limit are refused", ref failures, output);
            // Declared leaf first, the same chain must still be measured from its top (Codex's review: a memoised size
            // without its height let this order pass any depth), and the limit itself still fits.
            ExpectRefused(classic, r => AddComposites(r, ShimejiFormatValidator.MaximumCompositeDepth + 1, 1, false, true),
                "deep", "composites nested past the depth limit are refused when declared leaf first", ref failures, output);
            XmlData.RootNode deepest = With(TickFixture(classic), r => AddComposites(r, ShimejiFormatValidator.MaximumCompositeDepth, 1, false, true));
            Check(CompanionXmlValidator.TryParseWithFeatures(Serialize(deepest), AllExtensionFeatures, out parsed, out error),
                "extension gate: WITNESS a chain exactly at the depth limit, declared leaf first, validates" + FormatError(error),
                ref failures, output);
            ExpectRefused(classic, r => AddComposites(r, 13, 2, false), "nested actions each time it starts",
                "a composite that builds more than the action limit per start is refused (2^13 by doubling)", ref failures, output);
            XmlData.RootNode shallow = With(TickFixture(classic), r => AddComposites(r, 4, 2, false));
            Check(CompanionXmlValidator.TryParseWithFeatures(Serialize(shallow), AllExtensionFeatures, out parsed, out error),
                "extension gate: WITNESS a four-deep doubling composite (31 actions per start) validates" + FormatError(error),
                ref failures, output);
            // Sounds: a tick companion's clips are named, and a named clip with real audio validates.
            string audio = Convert.ToBase64String(ValidMp3Fixture());
            XmlData.RootNode sounded = With(TickFixture(classic), r =>
                {
                    r.Sounds = new XmlData.SoundsNode { Sound = new[] { new XmlData.SoundNode { SoundId = "clip", Base64 = audio } } };
                    StandAct(r).Variants.Variant[0].Pose[0].Sound = "clip";
                });
            Check(CompanionXmlValidator.TryParseWithFeatures(Serialize(sounded), AllExtensionFeatures, out parsed, out error),
                "extension gate: a named clip that a pose plays validates, audio checks included" + FormatError(error),
                ref failures, output);
            ExpectRefused(classic, r => r.Sounds = new XmlData.SoundsNode
                    { Sound = new[] { new XmlData.SoundNode { Id = 3, Probability = 100, Base64 = audio } } },
                "sounds are named clips", "a tick-clock companion's unnamed (classic) sound is refused", ref failures, output);
            ExpectRefused(classic, r => r.Sounds = new XmlData.SoundsNode
                    { Sound = new[] { new XmlData.SoundNode { SoundId = "clip", Base64 = Convert.ToBase64String(new byte[64]) } } },
                "not a usable MP3", "a named clip that is not MP3 audio is refused by the audio sniff", ref failures, output);
            // A named clip plays when a pose names it; a probability or a loop on it would be silently ignored.
            ExpectRefused(classic, r => r.Sounds = new XmlData.SoundsNode
                    { Sound = new[] { new XmlData.SoundNode { SoundId = "clip", Probability = 50, Base64 = audio } } },
                "belong to classic sounds", "a named clip with a probability is refused", ref failures, output);
            ExpectRefused(classic, r => r.Sounds = new XmlData.SoundsNode
                    { Sound = new[] { new XmlData.SoundNode { SoundId = "clip", Loop = 2, Base64 = audio } } },
                "belong to classic sounds", "a named clip with a loop is refused", ref failures, output);
        }

        /// <summary>Add composite actions C0..C(depth-1), each a Sequence of <paramref name="fanout"/> references to the
        /// next, the last a Stay; with <paramref name="selfLoop"/> C0 references itself instead.</summary>
        private static void AddComposites(XmlData.RootNode r, int depth, int fanout, bool selfLoop, bool leafFirst = false)
        {
            XmlData.MovingNode still = new XmlData.MovingNode { X = "0", Y = "0", Interval = "40" };
            var nodes = new System.Collections.Generic.List<XmlData.AnimationNode>(r.Animations.Animation);
            int firstAdded = nodes.Count;
            for (int i = 0; i < depth; i++)
            {
                bool last = i == depth - 1 && !selfLoop;
                var node = new XmlData.AnimationNode
                {
                    Id = 500 + i, Name = "C" + i, Start = still, End = still,
                    Sequence = new XmlData.SequenceNode { RepeatCount = "0" },
                };
                if (last)
                    node.Act = new XmlData.ActNode
                    {
                        Type = "Stay",
                        Params = new[] { new XmlData.ParamNode { Name = "Duration", Value = "5", Cache = "none" } },
                        Variants = new XmlData.VariantsNode { Variant = new[] { new XmlData.VariantNode { Pose = new[] { new XmlData.PoseNode { Frame = 0, Ticks = 5 } } } } },
                    };
                else
                {
                    var children = new XmlData.CompositeChildNode[fanout];
                    for (int k = 0; k < fanout; k++) children[k] = new XmlData.CompositeChildNode { Animation = selfLoop ? "C0" : "C" + (i + 1) };
                    node.Act = new XmlData.ActNode { Type = "Sequence", Children = new XmlData.ChildrenNode { Mode = "sequence", Child = children } };
                }
                nodes.Add(node);
            }
            // Leaf first: the chain's last action is declared first, so a measurement made in declaration order meets
            // each action after everything it contains.
            if (leafFirst) nodes.Reverse(firstAdded, nodes.Count - firstAdded);
            r.Animations.Animation = nodes.ToArray();
        }

        /// <summary>
        /// The per-class half of the gate. A feature token joins Supported only when every class it gates runs, so each
        /// supported token's classes must construct here; and a class this build does not construct is refused by name
        /// even when every feature it needs is supported, rather than accepted and ended at its first step.
        /// </summary>
        private static void CheckClassGate(XmlData.RootNode classic, ref int failures, TextWriter output)
        {
            Check(CompanionFormatFeatures.RunsClass("Fall") && !CompanionFormatFeatures.RunsClass(""),
                "extension gate: the class probe is wired to the runtime (Fall runs, a blank class does not)", ref failures, output);

            foreach (string token in CompanionFormatFeatures.Supported)
            {
                var missing = new System.Collections.Generic.List<string>();
                foreach (string cls in ClassesGatedBy(token))
                    if (!CompanionFormatFeatures.RunsClass(cls)) missing.Add(cls);
                Check(missing.Count == 0,
                    "extension gate: every class supported feature '" + token + "' gates runs in this build"
                    + (missing.Count == 0 ? "" : " (not: " + string.Join(", ", missing) + ")"), ref failures, output);
            }

            // The refusal itself, with every feature supported: a build that does not construct a class (here, one that
            // says ScanMove does not run) refuses a companion using it by name, rather than accepting it and ending it at
            // its first step. The override stands in for a build whose family has not landed; every family has, now.
            XmlData.RootNode doc = TickFixture(classic);
            StandAct(doc).Type = "Embedded";
            StandAct(doc).Class = "ScanMove";
            doc.Header.Features = string.Join(" ", CompanionFormatFeatures.All);
            string text = Serialize(doc);
            XmlData.RootNode parsed;
            string error;
            Check(!CompanionXmlValidator.TryParseWithFeatures(text, CompanionFormatFeatures.All, out parsed, out error, cls => cls != "ScanMove")
                  && Contains(error, "embedded class 'ScanMove' does not run in this version"),
                "extension gate: a class this build does not run is refused by name with every feature supported (" + error + ")",
                ref failures, output);
            Check(CompanionXmlValidator.TryParseWithFeatures(text, CompanionFormatFeatures.All, out parsed, out error),
                "extension gate: WITNESS the same companion validates with this build's own answer (ScanMove runs)" + FormatError(error),
                ref failures, output);
        }

        /// <summary>The embedded classes a feature token gates. 'actions' gates every class no other token does, except
        /// ComplexMove and ComplexJump, which run with the social family (their Characteristics add 'lifecycle' and
        /// 'social'; with neither they are plain moves, and the class gate refuses them until that family lands).</summary>
        private static string[] ClassesGatedBy(string token)
        {
            if (token == CompanionFormatFeatures.Lifecycle) return ShimejiFormatValidator.LifecycleClasses;
            if (token == CompanionFormatFeatures.Social) return ShimejiFormatValidator.SocialClasses;
            if (token == CompanionFormatFeatures.Windows) return ShimejiFormatValidator.WindowClasses;
            if (token != CompanionFormatFeatures.Actions) return new string[0];
            var classes = new System.Collections.Generic.List<string>();
            foreach (string cls in ShimejiFormatValidator.EmbeddedClasses)
                if (Array.IndexOf(ShimejiFormatValidator.LifecycleClasses, cls) < 0
                    && Array.IndexOf(ShimejiFormatValidator.SocialClasses, cls) < 0
                    && Array.IndexOf(ShimejiFormatValidator.WindowClasses, cls) < 0
                    && cls != "ComplexMove" && cls != "ComplexJump")
                    classes.Add(cls);
            return classes.ToArray();
        }

        /// <summary>Add behaviour node 4, named <paramref name="name"/>, running Stand, reached only from beh:Stand's
        /// own list (so it is off the route).</summary>
        private static XmlData.RootNode WithNextListEntry(XmlData.RootNode r, string name)
        {
            XmlData.MovingNode still = new XmlData.MovingNode { X = "0", Y = "0", Interval = "40" };
            var nodes = new System.Collections.Generic.List<XmlData.AnimationNode>(r.Animations.Animation)
            {
                new XmlData.AnimationNode
                {
                    Id = 4, Name = "beh:Stand@next1", Start = still, End = still,
                    Behaviour = new XmlData.BehaviourNode { Name = name, Runs = "Stand", Frequency = 1 },
                    Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
                },
            };
            r.Animations.Animation = nodes.ToArray();
            r.Animations.Animation[1].Sequence.Next = new[] { new XmlData.NextNode { Value = 4, Probability = 1 } };
            return r;
        }

        private static void ExpectRefused(XmlData.RootNode classic, Action<XmlData.RootNode> breakIt, string expected,
            string name, ref int failures, TextWriter output, Action<XmlData.RootNode> prepare = null)
        {
            XmlData.RootNode doc = TickFixture(classic);
            if (prepare != null) prepare(doc);
            breakIt(doc);
            XmlData.RootNode parsed;
            string error;
            bool refused = !CompanionXmlValidator.TryParseWithFeatures(Serialize(doc), AllExtensionFeatures, out parsed, out error);
            Check(refused && Contains(error, expected), "extension gate: " + name + " (" + error + ")", ref failures, output);
        }

        private static XmlData.ActNode StandAct(XmlData.RootNode r)
        {
            return r.Animations.Animation[2].Act;
        }

        /// <summary>A minimal tick-clock companion: one route, one behaviour, one Stay action of two poses, on the bundled
        /// pet's image (so it is a real document with a real sheet). The window self-test stages it too.</summary>
        internal static XmlData.RootNode TickFixture(XmlData.RootNode classic)
        {
            XmlData.RootNode r = Clone(classic);
            r.Header.Features = "tick actions behaviours expressions";
            r.Header.Clock = "tick";
            XmlData.MovingNode still = new XmlData.MovingNode { X = "0", Y = "0", Interval = "40" };
            r.Animations.Animation = new[]
            {
                new XmlData.AnimationNode
                {
                    Id = 1, Name = "route", Start = still, End = still, Route = new XmlData.RouteNode(),
                    Sequence = new XmlData.SequenceNode
                    {
                        RepeatCount = "0",
                        Next = new[] { new XmlData.NextNode { Value = 2, Probability = 1, When = "mascot.totalCount < 50" } },
                    },
                },
                new XmlData.AnimationNode
                {
                    // No edges: when it ends, the next behaviour is selected from the route's list (grimoire/06 4.3),
                    // synchronously; there is no edge back to the route.
                    Id = 2, Name = "beh:Stand", Start = still, End = still,
                    Behaviour = new XmlData.BehaviourNode { Name = "Stand", Runs = "Stand", Frequency = 1 },
                    Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
                },
                new XmlData.AnimationNode
                {
                    Id = 3, Name = "Stand", Start = still, End = still,
                    Sequence = new XmlData.SequenceNode { RepeatCount = "0" },
                    Act = new XmlData.ActNode
                    {
                        Type = "Stay", Border = "Floor",
                        Params = new[] { new XmlData.ParamNode { Name = "Duration", Value = "20", Cache = "none" } },
                        Guard = new XmlData.GuardNode { Duration = "Duration", When = "mascot.environment.floor.isOn(mascot.anchor)" },
                        Variants = new XmlData.VariantsNode
                        {
                            Variant = new[]
                            {
                                new XmlData.VariantNode
                                {
                                    Pose = new[]
                                    {
                                        new XmlData.PoseNode { Frame = 0, Ticks = 5 },
                                        new XmlData.PoseNode { Frame = 1, Ticks = 5, DX = -2 },
                                    },
                                },
                            },
                        },
                    },
                },
            };
            r.Spawns = new XmlData.SpawnsNode
            {
                Spawn = new[] { new XmlData.SpawnNode { Id = 1, Probability = 1, X = "0", Y = "0", Next = new XmlData.NextNode { Value = 1, Probability = 100 } } },
            };
            r.Childs = new XmlData.ChildsNode { Child = new XmlData.ChildNode[0] };
            r.Sounds = null;
            return r;
        }

        private static XmlData.RootNode Clone(XmlData.RootNode r)
        {
            return XmlData.NodeCopy.Deep(r);
        }

        private static XmlData.RootNode With(XmlData.RootNode r, Action<XmlData.RootNode> change)
        {
            change(r);
            return r;
        }

        internal static string Serialize(XmlData.RootNode root)
        {
            var serializer = new XmlSerializer(typeof(XmlData.RootNode));
            var sb = new StringBuilder();
            using (var writer = new StringWriter(sb))
                serializer.Serialize(writer, root);
            return sb.ToString();
        }

        private static bool Contains(string text, string expected)
        {
            return text != null && text.IndexOf(expected, StringComparison.Ordinal) >= 0;
        }
    }
}
