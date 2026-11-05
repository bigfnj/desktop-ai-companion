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

            // The production gate (this build's Supported set) refuses every extension until its runtime lands.
            string tickDoc = Serialize(TickFixture(classic));
            XmlData.RootNode parsed;
            Check(!CompanionXmlValidator.TryParse(tickDoc, out parsed, out error) && Contains(error, "does not run yet"),
                "extension gate: a tick-clock companion is refused by this build, naming the feature (" + error + ")",
                ref failures, output);
            Check(CompanionXmlValidator.TryParseWithFeatures(tickDoc, AllExtensionFeatures, out parsed, out error),
                "extension gate: the same companion validates when its features are supported" + FormatError(error),
                ref failures, output);

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

        /// <summary>A minimal tick-clock companion: one route, one behaviour, one Stay action of two poses.</summary>
        private static XmlData.RootNode TickFixture(XmlData.RootNode classic)
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

        private static string Serialize(XmlData.RootNode root)
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
