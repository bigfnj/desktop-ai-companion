using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DesktopAICompanion.Shimeji
{
    internal static partial class ShimejiWindowSelfTest
    {
        /// <summary>
        /// burndown/parity-host-1, the host half of CONTRACT v1.1 (N-shimeji-1106-13): a parity run reads
        /// shimeji-parity-map/2 only, checks every mapping strictly, binds each emitted pose to its source position by the
        /// pose object, traces that position (also for an old member's pose kept after a Transform), refuses before it opens
        /// a trace, stops at a left-button edge for a companion that is not active, and proves a trace-on and a trace-off
        /// run agree. burndown/parity-host-2 moves it to trace/2 (CONTRACT section 10): the header, a tape outrun on 0.5
        /// with tapeExhausted in every footer, and the observation proof judged by 10.5.3. Every map and pet here is
        /// generated; nothing is read from a real pack.
        /// </summary>
        private static void CheckParityMapV2(Action<bool, string> check, string scratch, StringBuilder log)
        {
            try
            {
                CheckParityMapSchema(check);
                CheckParityPoseBinding(check);
                CheckParityPoseTrace(check);
                CheckParityRunRefusals(check, Path.Combine(scratch, "parity-v2"), log);
                CheckParityBuildRevision(check, Path.Combine(scratch, "parity-revision"));
                CheckParityErrors(check);
            }
            catch (Exception e)
            {
                check(false, "parity map/2: the host checks ran without an exception: " + e);
            }
        }

        /// <summary>
        /// N-bd-parity-host-1-03: build.commit is the full source revision the project file stamped into its own metadata
        /// value, never the informational (product) version and never a placeholder, and the command refuses a build
        /// without one before it reads anything or opens a trace. This build's own stamp is required: the window self-test
        /// runs from a git checkout (the gate, CI, the mutation harness), and a build with none could not run the parity
        /// tooling at all.
        /// </summary>
        private static void CheckParityBuildRevision(Action<bool, string> check, string root)
        {
            Directory.CreateDirectory(root);
            string refusal;
            System.Reflection.Assembly own = typeof(ParityRun).Assembly;
            string revision = ParityRunCommand.SourceRevision(own, out refusal);
            string informational = InformationalVersionOf(own);
            check(revision != null && ParityRunCommand.IsFullRevision(revision) && revision != informational,
                "parity run: build.commit is this build's stamped 40-hex source revision, not its product version ("
                + (revision ?? refusal) + "; product " + informational + ")");

            // The runtime's own assembly has an informational version a fallback could take, and no stamp.
            System.Reflection.Assembly unstamped = typeof(object).Assembly;
            string fallback = InformationalVersionOf(unstamped);
            string none = ParityRunCommand.SourceRevision(unstamped, out refusal);
            check(!string.IsNullOrEmpty(fallback), "parity run: WITNESS the unstamped stand-in has an informational version a fallback could take (" + fallback + ")");
            check(none == null && refusal != null && refusal.StartsWith("this build carries no source revision", StringComparison.Ordinal),
                "parity run: a build with no source revision metadata is refused, never given its informational version ("
                + (none == null ? refusal : "took " + none) + ")");

            var accepted = new List<string>();
            foreach (string value in new[] { "", "unknown", "selftest", "1.11.0", new string('0', 40), "ABCDEF0123456789ABCDEF0123456789ABCDEF01",
                "abcdef0123456789abcdef0123456789abcdef0", "abcdef0123456789abcdef0123456789abcdef012", " bcdef0123456789abcdef0123456789abcdef01" })
                if (ParityRunCommand.IsFullRevision(value)) accepted.Add("'" + value + "'");
            check(accepted.Count == 0 && ParityRunCommand.IsFullRevision("abcdef0123456789abcdef0123456789abcdef01"),
                "parity run: a placeholder, a version, the all-zero id, upper case, 39 or 41 characters are no source revision;"
                + " 40 lowercase hex is (" + string.Join(", ", accepted) + ")");

            // The command itself: exit 2, its line, and no trace, before the scenario (which does not exist) is read.
            string trace = Path.Combine(root, "refused.jsonl");
            var log = new StringWriter();
            int exit;
            try
            {
                exit = ParityRunCommand.Execute(new[] { ParityRunCommand.Flag, Path.Combine(root, "absent.json"), root, trace, root }, 0, log, unstamped);
            }
            catch (Exception e)
            {
                exit = -1;
                log.Write("threw " + e.GetType().Name + ": " + e.Message);
            }
            check(exit == 2 && !File.Exists(trace) && log.ToString().StartsWith(ParityRunCommand.Flag + " refused: this build carries no source revision", StringComparison.Ordinal),
                "parity run: the command refuses a build with no source revision with exit 2, its log line and no trace (exit " + exit + ", "
                + log.ToString().Trim() + ")");
        }

        private static string InformationalVersionOf(System.Reflection.Assembly assembly)
        {
            var info = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            return info == null ? null : info.InformationalVersion;
        }

        private static string ParPoseRef(string set, int action, int animation, int pose)
        {
            return set + "|conf/actions.xml|/Mascot[1]/ActionList[1]/Action[" + action + "]/Animation[" + animation + "]/Pose[" + pose + "]";
        }

        private static string ParPoseValue(string set, int action, int animation, int pose)
        {
            return "{\"refKey\":" + JsonLine.Quote(ParPoseRef(set, action, animation, pose)) + ",\"index\":" + (pose - 1) + "}";
        }

        /// <summary>The poses a complete map gives <see cref="ParDocument"/>. Walk (node 30) is a source Animation[2] (an
        /// omitted Animation[1] shifts the variant) whose Pose[1] and Pose[3] have Duration 0 and are not emitted, so its
        /// three emitted poses are the source's 1, 3 and 4 (a Hotspot between Pose[4] and Pose[5] takes no index); the
        /// middle one has no image. With <paramref name="full"/>, Mover (31) has two variants, its last a false-condition
        /// one shaped like the placeholder, and Ends (32) is decision 68's placeholder, which has no mapping.</summary>
        private static Dictionary<string, string> ParPoses(string set, bool full)
        {
            var poses = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "30:0:0", ParPoseValue(set, 1, 2, 2) },
                { "30:0:1", ParPoseValue(set, 1, 2, 4) },
                { "30:0:2", ParPoseValue(set, 1, 2, 5) },
            };
            if (full)
            {
                poses["31:0:0"] = ParPoseValue(set, 2, 1, 1);
                poses["31:1:0"] = ParPoseValue(set, 2, 2, 1);
            }
            return poses;
        }

        /// <summary>A map/2 document for <see cref="ParDocument"/>, its poses written in the order given (a list, so a
        /// test can repeat a key) or as raw text.</summary>
        private static string ParMapJson(string set, bool full, IEnumerable<KeyValuePair<string, string>> poses, string schema = ParityMap.Schema, string rawPoses = null)
        {
            string action = set + "|conf/actions.xml|/Mascot[1]/ActionList[1]/Action[";
            var b = new StringBuilder("{\"schema\":" + JsonLine.Quote(schema) + ",\"imageSet\":" + JsonLine.Quote(set)
                + ",\"frames\":[{\"frame\":0,\"image\":\"img/" + set + "/a.png\"},{\"frame\":1,\"image\":\"img/" + set + "/b.png\"}],"
                + "\"actions\":{\"30\":" + JsonLine.Quote(action + "1]"));
            if (full)
                b.Append(",\"31\":" + JsonLine.Quote(action + "2]") + ",\"32\":" + JsonLine.Quote(action + "3]") + ",\"33\":" + JsonLine.Quote(action + "4]"));
            b.Append("},\"children\":{" + (full ? "\"33:0\":" + JsonLine.Quote(action + "4]/ActionReference[1]") : "") + "},");
            b.Append("\"behaviours\":{\"10\":" + JsonLine.Quote(set + "|conf/behaviors.xml|/Mascot[1]/BehaviorList[1]/Behavior[1]") + "},");
            b.Append("\"sourceNames\":{},\"poses\":");
            if (rawPoses != null) b.Append(rawPoses);
            else
            {
                b.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, string> p in poses)
                {
                    if (!first) b.Append(',');
                    first = false;
                    b.Append(JsonLine.Quote(p.Key)).Append(':').Append(p.Value);
                }
                b.Append('}');
            }
            return b.Append('}').ToString();
        }

        /// <summary>Walk (30, Stay, Duration 3, a frame-cached Draggable that draws), and with <paramref name="full"/>
        /// Mover (31, Embedded MoveWithTurn: its last variant when="false" with one image-less 1-tick pose), Ends (32,
        /// Animate: decision 68's placeholder) and Seq (33, a Sequence of Walk). Cells 64x64, two tiles.</summary>
        private static XmlData.RootNode ParDocument(bool full)
        {
            var still = new XmlData.MovingNode { X = "0", Y = "0", Interval = "40" };
            Func<int, string, XmlData.AnimationNode> node = (id, name) => new XmlData.AnimationNode
            {
                Id = id, Name = name, Start = still, End = still,
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
            };
            Func<int, int, XmlData.PoseNode> pose = (frame, ticks) => new XmlData.PoseNode { Frame = frame, Ticks = ticks };
            XmlData.AnimationNode route = node(1, "route");
            route.Route = new XmlData.RouteNode();
            route.Sequence.Next = new[] { new XmlData.NextNode { Value = 10, Probability = 1 } };
            XmlData.AnimationNode walkBehaviour = node(10, "beh:Walk");
            walkBehaviour.Behaviour = new XmlData.BehaviourNode { Name = "Walk", Runs = "Walk", Frequency = 1 };
            XmlData.AnimationNode walk = node(30, "Walk");
            walk.Act = new XmlData.ActNode
            {
                Type = "Stay", Border = "Floor",
                Params = new[]
                {
                    new XmlData.ParamNode { Name = "Duration", Value = "3", Cache = "none" },
                    new XmlData.ParamNode { Name = "Draggable", Value = "Math.random() < 2", Cache = "frame" },
                },
                Variants = new XmlData.VariantsNode { Variant = new[] { new XmlData.VariantNode { Pose = new[] { pose(0, 1), pose(-1, 1), pose(1, 1) } } } },
            };
            var nodes = new List<XmlData.AnimationNode> { route, walkBehaviour, walk };
            if (full)
            {
                XmlData.AnimationNode mover = node(31, "Mover");
                mover.Act = new XmlData.ActNode
                {
                    Type = "Embedded", Class = "MoveWithTurn", Border = "Floor",
                    Variants = new XmlData.VariantsNode
                    {
                        Variant = new[]
                        {
                            new XmlData.VariantNode { Pose = new[] { pose(0, 1) } },
                            new XmlData.VariantNode { When = "false", Pose = new[] { pose(-1, 1) } },
                        },
                    },
                };
                XmlData.AnimationNode ends = node(32, "Ends");
                ends.Act = new XmlData.ActNode
                {
                    Type = "Animate",
                    Variants = new XmlData.VariantsNode { Variant = new[] { new XmlData.VariantNode { When = "false", Pose = new[] { pose(-1, 1) } } } },
                };
                XmlData.AnimationNode seq = node(33, "Seq");
                seq.Act = new XmlData.ActNode
                {
                    Type = "Sequence",
                    Children = new XmlData.ChildrenNode { Mode = "sequence", Child = new[] { new XmlData.CompositeChildNode { Animation = "Walk" } } },
                };
                nodes.Add(mover);
                nodes.Add(ends);
                nodes.Add(seq);
            }
            return new XmlData.RootNode
            {
                Header = new XmlData.HeaderNode { Title = "Par", Features = "tick actions behaviours expressions", Clock = "tick" },
                Image = new XmlData.ImageNode
                {
                    TilesX = 2, TilesY = 1, Png = PngHeader(128, 64),
                    AnchorX = 32, AnchorXSpecified = true, AnchorY = 64, AnchorYSpecified = true,
                },
                Animations = new XmlData.AnimationsNode { Animation = nodes.ToArray() },
            };
        }

        /// <summary>Parse and apply; null when accepted, else the refusal's message (any exception type, named).</summary>
        private static string ParApply(string json, bool full)
        {
            try
            {
                ParityMap.Parse(json).Apply(TickType.Compile("Par", ParDocument(full)));
                return null;
            }
            catch (InvalidDataException e) { return e.Message; }
            catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
        }

        private static List<KeyValuePair<string, string>> Pairs(Dictionary<string, string> poses)
        {
            return new List<KeyValuePair<string, string>>(poses);
        }

        /// <summary>CONTRACT v1.1 section 9's malformed and retired maps, each refused with its own message.</summary>
        private static void CheckParityMapSchema(Action<bool, string> check)
        {
            Dictionary<string, string> poses = ParPoses("Par", true);
            string accepted = ParApply(ParMapJson("Par", true, poses), true);
            check(accepted == null, "parity map/2: WITNESS a complete map/2 is accepted" + (accepted == null ? "" : " (" + accepted + ")"));

            string retired = ParApply(ParMapJson("Par", true, poses, "shimeji-parity-map/1"), true);
            check(retired != null && retired.Contains("is shimeji-parity-map/1; regenerate it with the current converter (map/2 required)"),
                "parity map/2: a map/1 file is refused with a regenerate message (" + retired + ")");
            string noPoses = ParApply(ParMapJson("Par", true, poses).Replace(",\"poses\":", ",\"unposed\":"), true);
            check(noPoses != null && noPoses.Contains("has no poses"), "parity map/2: a map with no poses is refused (" + noPoses + ")");
            string notObject = ParApply(ParMapJson("Par", true, null, rawPoses: "[]"), true);
            check(notObject != null && notObject.Contains("poses is not an object"), "parity map/2: poses that is not an object is refused (" + notObject + ")");

            var repeated = Pairs(poses);
            repeated.Add(new KeyValuePair<string, string>("30:0:0", ParPoseValue("Par", 1, 2, 2)));
            string twice = ParApply(ParMapJson("Par", true, repeated), true);
            check(twice != null && twice.Contains("repeats the poses key '30:0:0'"), "parity map/2: a repeated poses key is refused (" + twice + ")");

            Func<string, string, string> rekey = (from, to) =>
            {
                var moved = new List<KeyValuePair<string, string>>();
                foreach (KeyValuePair<string, string> p in poses) moved.Add(new KeyValuePair<string, string>(p.Key == from ? to : p.Key, p.Value));
                return ParApply(ParMapJson("Par", true, moved), true);
            };
            string leadingZero = rekey("31:0:0", "031:0:0");
            check(leadingZero != null && leadingZero.Contains("'031:0:0' is not node:variant:pose in canonical decimal"),
                "parity map/2: a pose key '031:0:0' is refused as noncanonical (" + leadingZero + ")");
            string signed = rekey("31:0:0", "+31:0:0");
            check(signed != null && signed.Contains("'+31:0:0' is not node:variant:pose in canonical decimal"),
                "parity map/2: a pose key '+31:0:0' is refused as noncanonical (" + signed + ")");
            string huge = rekey("31:0:0", "31:2147483648:0");
            check(huge != null && huge.Contains("'31:2147483648:0' has a part outside Int32"),
                "parity map/2: a pose key with a part outside Int32 is refused (" + huge + ")");

            Func<string, string> revalue = value =>
            {
                var changed = new Dictionary<string, string>(poses, StringComparer.Ordinal);
                changed["31:0:0"] = value;
                return ParApply(ParMapJson("Par", true, changed), true);
            };
            string refKey = JsonLine.Quote(ParPoseRef("Par", 2, 1, 1));
            string extra = revalue("{\"refKey\":" + refKey + ",\"index\":0,\"extra\":1}");
            check(extra != null && extra.Contains("has the property 'extra'"), "parity map/2: a pose mapping with an extra property is refused (" + extra + ")");
            string twiceProperty = revalue("{\"refKey\":" + refKey + ",\"index\":0,\"index\":0}");
            check(twiceProperty != null && twiceProperty.Contains("repeats its property 'index'"),
                "parity map/2: a pose mapping repeating a property is refused (" + twiceProperty + ")");
            string negativeZero = revalue("{\"refKey\":" + refKey + ",\"index\":-0}");
            check(negativeZero != null && negativeZero.Contains("has index -0, not a canonical"), "parity map/2: index -0 is refused (" + negativeZero + ")");
            string fraction = revalue("{\"refKey\":" + refKey + ",\"index\":0.0}");
            check(fraction != null && fraction.Contains("has index 0.0, not a canonical"), "parity map/2: index 0.0 is refused (" + fraction + ")");
            string twoParts = revalue("{\"refKey\":\"Par|/Mascot[1]/Pose[1]\",\"index\":0}");
            string fourParts = revalue("{\"refKey\":\"Par|conf/actions.xml|x|/Mascot[1]/Pose[1]\",\"index\":0}");
            check(twoParts != null && twoParts.Contains("a refKey of 2 parts") && fourParts != null && fourParts.Contains("a refKey of 4 parts"),
                "parity map/2: a refKey of 2 or 4 parts is refused (" + twoParts + "; " + fourParts + ")");
            string otherSet = revalue("{\"refKey\":" + JsonLine.Quote(ParPoseRef("Other", 2, 1, 1)) + ",\"index\":0}");
            check(otherSet != null && otherSet.Contains("a refKey in image set 'Other', not 'Par'"),
                "parity map/2: a refKey in another image set is refused (" + otherSet + ")");
            string offByOne = revalue("{\"refKey\":" + JsonLine.Quote(ParPoseRef("Par", 2, 1, 2)) + ",\"index\":0}");
            check(offByOne != null && offByOne.Contains("has index 0 where its refKey's step Pose[2] gives 1"),
                "parity map/2: an index that is not the refKey's position less one is refused (" + offByOne + ")");
        }

        /// <summary>CONTRACT v1.1 section 9's binding: one mapping per emitted pose, the placeholder exemption only for
        /// its exact shape, and surplus keys refused.</summary>
        private static void CheckParityPoseBinding(Action<bool, string> check)
        {
            Dictionary<string, string> poses = ParPoses("Par", true);
            TickType type = TickType.Compile("Par", ParDocument(true));
            ParityMap map = ParityMap.Parse(ParMapJson("Par", true, poses));
            map.Apply(type);
            var bound = new List<string>();
            foreach (TickPoseDef p in type.ActionsById[30].Variants[0].Poses)
            {
                int index;
                bound.Add(map.TryGetPoseIndex(p, out index) ? index.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unbound");
            }
            int placeholder;
            bool placeholderBound = map.TryGetPoseIndex(type.ActionsById[32].Variants[0].Poses[0], out placeholder);
            check(string.Join(",", bound) == "1,3,4" && !placeholderBound,
                "parity map/2: each emitted pose binds its source index past Duration-0, image-less and omitted-variant shifts ("
                + string.Join(",", bound) + "; placeholder " + (placeholderBound ? "bound" : "unbound") + ")");

            var missing = new Dictionary<string, string>(poses, StringComparer.Ordinal);
            missing.Remove("30:0:1");
            string hole = ParApply(ParMapJson("Par", true, missing), true);
            check(hole != null && hole.Contains("has no source pose for 30:0:1"), "parity map/2: an unmapped live pose is refused (" + hole + ")");

            var turning = new Dictionary<string, string>(poses, StringComparer.Ordinal);
            turning.Remove("31:1:0");
            string unmappedTurn = ParApply(ParMapJson("Par", true, turning), true);
            check(unmappedTurn != null && unmappedTurn.Contains("has no source pose for 31:1:0"),
                "parity map/2: an unmapped false-condition last variant of a MoveWithTurn is refused (" + unmappedTurn + ")");

            var supplied = new Dictionary<string, string>(poses, StringComparer.Ordinal);
            supplied["32:0:0"] = ParPoseValue("Par", 3, 1, 1);
            ParityMap authored = ParityMap.Parse(ParMapJson("Par", true, supplied));
            TickType authoredType = TickType.Compile("Par", ParDocument(true));
            authored.Apply(authoredType);
            int authoredIndex;
            bool authoredBound = authored.TryGetPoseIndex(authoredType.ActionsById[32].Variants[0].Poses[0], out authoredIndex);
            supplied["32:0:0"] = "{\"refKey\":" + JsonLine.Quote(ParPoseRef("Par", 3, 1, 1)) + ",\"index\":1}";
            string authoredWrong = ParApply(ParMapJson("Par", true, supplied), true);
            check(authoredBound && authoredIndex == 0 && authoredWrong != null && authoredWrong.Contains("has index 1 where"),
                "parity map/2: a mapping supplied for the placeholder shape is bound, and validated like any other (" + authoredWrong + ")");

            var composite = new Dictionary<string, string>(poses, StringComparer.Ordinal);
            composite["33:0:0"] = ParPoseValue("Par", 4, 1, 1);
            string onComposite = ParApply(ParMapJson("Par", true, composite), true);
            check(onComposite != null && onComposite.Contains("poses key '33:0:0' names no emitted pose of Par (surplus)"),
                "parity map/2: a pose key on a composite node is surplus, refused (" + onComposite + ")");
            var ranges = new List<string>();
            foreach (string key in new[] { "30:1:0", "30:0:3", "99:0:0" })
            {
                var surplus = new Dictionary<string, string>(poses, StringComparer.Ordinal);
                surplus[key] = ParPoseValue("Par", 1, 2, 2);
                string refusal = ParApply(ParMapJson("Par", true, surplus), true);
                if (refusal != null && refusal.Contains("'" + key + "' names no emitted pose")) ranges.Add(key);
            }
            check(ranges.Count == 3, "parity map/2: a variant, a pose or a node out of range is surplus, refused (" + string.Join(", ", ranges) + ")");
        }

        /// <summary>A scenario desktop for driving a world by hand: one 1280x720 screen, its work area 680 high.</summary>
        private static TickWorld ParWorld(ITickObserver observer)
        {
            // Each of the three reads (or clones) what it keeps while the document is open.
            using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(
                "{\"screens\":[{\"x\":0,\"y\":0,\"w\":1280,\"h\":720}],\"workArea\":{\"x\":0,\"y\":0,\"w\":1280,\"h\":680},\"rng\":{\"kind\":\"none\"}}"))
            {
                System.Text.Json.JsonElement s = doc.RootElement;
                return new TickWorld(new ParityEnvironment(s), new ParityRandom(s.GetProperty("rng")), new ParitySettings(s), observer);
            }
        }

        /// <summary>The trace's pose.index after a Transform (the S-slice-transform-01 shape): the companion is the new
        /// member, still showing a pose of the OLD member, which only the old member's map binds. A writer without the old
        /// member's map has no binding for it: a provenance fault, index null, and the run ends in error.</summary>
        private static void CheckParityPoseTrace(Action<bool, string> check)
        {
            TickType oldType = TickType.Compile("Old", ParDocument(false));
            ParityMap oldMap = ParityMap.Parse(ParMapJson("Old", false, ParPoses("Old", false)));
            oldMap.Apply(oldType);
            TickType newType = TickType.Compile("Par", ParDocument(false));
            ParityMap newMap = ParityMap.Parse(ParMapJson("Par", false, ParPoses("Par", false)));
            newMap.Apply(newType);
            TickWorld world = ParWorld(null);
            world.AddType(oldType);
            world.AddType(newType);
            world.Start();
            TickMascot m = world.CreateAtStart("m1", oldType, new TickPoint(640, 680), false, "Walk");
            world.ActivateStartup();
            // The old member's third emitted pose (source index 4) stays applied while the companion is the new member.
            m.ApplyPose(oldType.ActionsById[30].Variants[0].Poses[2], oldType);
            m.Type = newType;
            var both = new StringWriter();
            var bothWriter = new ParityTraceWriter(both, new Dictionary<string, ParityMap> { { "Old", oldMap }, { "Par", newMap } }) { Tick = 3 };
            bothWriter.States(world);
            check(both.ToString().Contains("\"imageSet\":\"Par\"") && both.ToString().Contains("\"image\":\"img/Old/b.png\"")
                  && both.ToString().Contains("\"index\":4}") && bothWriter.ProvenanceFault == null,
                "parity trace: a pose kept after a Transform is read through the old member's map (" + both.ToString().Trim() + ")");

            var unbound = new StringWriter();
            var unboundLog = new StringWriter();
            var unboundWriter = new ParityTraceWriter(unbound, new Dictionary<string, ParityMap> { { "Par", newMap } }) { Tick = 3 };
            unboundWriter.States(world);
            int exit = ParityRun.Finish(unboundWriter, 3, "complete", null, unboundLog);
            check(unbound.ToString().Contains("\"index\":null}") && exit == 1 && unbound.ToString().Contains("\"status\":\"error\"")
                  && unboundLog.ToString().Contains("tick 3 m1: applied pose has no source binding (member Old, which has no parity map)"),
                "parity trace: an applied pose with no source binding ends the run in error with index null (exit " + exit + ", "
                + unboundLog.ToString().Trim() + ")");
        }

        /// <summary>
        /// ParityRun.Run on generated pets: the indexes it traces, its refusals (exit 2 and NO trace file, the map and the
        /// pack checked before the trace is opened), the inactive left-button target (aborted, exit 2) beside a right press
        /// on an absent companion (complete), and the trace-on/trace-off proof (CONTRACT v1.1 section 5).
        /// </summary>
        private static void CheckParityRunRefusals(Action<bool, string> check, string root, StringBuilder log)
        {
            Directory.CreateDirectory(root);
            string pack = Path.Combine(root, "pack");
            Directory.CreateDirectory(pack);
            File.WriteAllText(Path.Combine(pack, "readme.txt"), "a generated stand-in pack\n", new UTF8Encoding(false));
            string xml = Serialize(ParDocument(false), Color.Red, Color.Blue);
            string goodMap = ParMapJson("Par", false, ParPoses("Par", false));
            var build = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "commit", "selftest" }, { "exeSha256", new string('0', 64) }, { "engineSha256", new string('1', 64) }, { "converterSha256", new string('2', 64) },
            };
            int run = 0;
            // The scenario's rng, directed unless a check sets a tape or a seed for its own runs and puts it back.
            const string directed = "{\"kind\":\"none\"}";
            string rng = directed;
            // The scenario's pack, the generated one unless a refusal check names another for its run and puts it back.
            string packName = "pack";
            // The scenario's text as written, unless a scenario refusal check edits it for its run and puts it back.
            Func<string, string> scenarioEdit = null;
            // One pets folder and scenario per run, so no run reads another's leftovers.
            Func<string, string, string, bool, string[]> go = (map, input, xmlText, traceOff) =>
            {
                run++;
                string pets = Path.Combine(root, "pets" + run), member = Path.Combine(pets, "Par");
                Directory.CreateDirectory(member);
                if (xmlText != null) File.WriteAllText(Path.Combine(member, "animations.xml"), xmlText, new UTF8Encoding(false));
                if (map != null) File.WriteAllText(Path.Combine(member, "parity-map.json"), map, new UTF8Encoding(false));
                string scenario = Path.Combine(root, "S-host-" + run + ".json");
                string scenarioText = "{\"schema\":\"shimeji-parity-scenario/1\",\"id\":\"S-host-" + run + "\",\"description\":\"generated\","
                    + "\"pack\":\"" + packName + "\",\"screens\":[{\"x\":0,\"y\":0,\"w\":1280,\"h\":720}],\"workArea\":{\"x\":0,\"y\":0,\"w\":1280,\"h\":680},"
                    + "\"dpiScale\":1.0,\"ticks\":8,\"rng\":" + rng + ",\"settings\":{},"
                    + "\"mascots\":[{\"sid\":\"m1\",\"imageSet\":\"Par\",\"behavior\":\"Walk\",\"anchor\":[640,680],\"lookRight\":false}],"
                    + "\"activeWindow\":[{\"tick\":0,\"visible\":false}],\"input\":[{\"tick\":0,\"cursor\":[100,100]}" + input + "]}";
                File.WriteAllText(scenario, scenarioEdit == null ? scenarioText : scenarioEdit(scenarioText), new UTF8Encoding(false));
                string trace = Path.Combine(root, "trace" + run + ".jsonl");
                var runLog = new StringWriter();
                int exit = ParityRun.Run(scenario, pets, trace, build, runLog, traceOff);
                if (runLog.ToString().Length > 0) log.Append("LOG: parity run " + run + ": " + runLog);
                return new[] { exit.ToString(System.Globalization.CultureInfo.InvariantCulture), File.Exists(trace) ? File.ReadAllText(trace) : null, runLog.ToString(), Path.Combine(member, "parity-map.json") };
            };

            string[] good = go(goodMap, "", xml, false);
            // This run's scenario and pets are left as written; the argument refusals below run against them.
            string goodScenario = Path.Combine(root, "S-host-" + run + ".json"), goodPets = Path.Combine(root, "pets" + run);
            check(good[0] == "0" && good[1] != null && good[1].Contains("\"index\":1}") && good[1].Contains("\"index\":3}") && good[1].Contains("\"index\":4}")
                  && !good[1].Contains("\"index\":0}") && !good[1].Contains("\"index\":2}"),
                "parity run: pose.index is the source position map/2 binds (1, 3, 4), never the emitted one (exit " + good[0] + ")");

            string[] retired = go(ParMapJson("Par", false, ParPoses("Par", false), "shimeji-parity-map/1"), "", xml, false);
            check(retired[0] == "2" && retired[1] == null
                  && retired[2].Contains("refused " + retired[3] + ": the map is shimeji-parity-map/1; regenerate it with the current converter"),
                "parity run: a map/1 file is refused with exit 2 and no trace file (" + retired[2].Trim() + ")");
            string[] broken = go("{", "", xml, false);
            check(broken[0] == "2" && broken[1] == null && broken[2].Contains("refused " + broken[3] + ": "),
                "parity run: a map that is not JSON is refused with exit 2 and no trace file (" + broken[2].Trim() + ")");
            var holed = ParPoses("Par", false);
            holed.Remove("30:0:2");
            string[] unmapped = go(ParMapJson("Par", false, holed), "", xml, false);
            check(unmapped[0] == "2" && unmapped[1] == null && unmapped[2].Contains("has no source pose for 30:0:2"),
                "parity run: a map refused at apply is refused with exit 2 and no trace file (" + unmapped[2].Trim() + ")");
            string[] mapless = go(null, "", xml, false);
            check(mapless[0] == "2" && mapless[1] == null && mapless[2].Contains("a converted pet with no parity-map.json"),
                "parity run: a pet folder with animations.xml and no parity-map.json is refused, not skipped (" + mapless[2].Trim() + ")");

            // N-bd-w6-coordinator-01: a pack its preflight refuses (pack-invalid, CONTRACT section 3) is refused as a map is,
            // with exit 2, its line and no trace file, because it is hashed before the trace is opened. Hashed inside the
            // header write, the refusal left an empty trace and escaped the command, so a run that throws is caught here and
            // reported with the trace it left, rather than ending every check below it.
            Func<string[]> goCaught = () =>
            {
                try { return go(goodMap, "", xml, false); }
                catch (Exception e)
                {
                    string left = Path.Combine(root, "trace" + run + ".jsonl");
                    return new[] { "threw", File.Exists(left) ? File.ReadAllText(left) : null, e.GetType().Name + ": " + e.Message, null };
                }
            };
            string link = Path.Combine(pack, "link"), outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            // DEGRADED, not NOTE (N-bd-w12-review-02): tests/Invoke-SelfTests.ps1 prints a DEGRADED line beside the flag's
            // verdict on every run, and deletes the marker a NOTE was buried in.
            if (!TryMakeDirectoryLink(link, outside))
                log.AppendLine("DEGRADED: parity run: the linked-pack refusal is not exercised (no symlink or junction could be made here)");
            else
            {
                string[] linked;
                try { linked = goCaught(); }
                finally { try { Directory.Delete(link); } catch { } }
                check(linked[0] == "2" && linked[1] == null
                      && linked[2].Contains("refused " + Path.GetFullPath(pack) + ": pack-invalid: the pack holds a link: link"),
                    "parity run: a pack holding a link is refused before the trace is opened, with exit 2 and no trace file (exit "
                    + linked[0] + ", trace " + (linked[1] == null ? "none" : "left with " + linked[1].Length + " chars") + ", " + linked[2].Trim() + ")");
            }
            packName = "no-such-pack";
            string[] packless = goCaught();
            packName = "pack";
            check(packless[0] == "2" && packless[1] == null
                  && packless[2].Contains("pack-invalid: the pack " + Path.GetFullPath(Path.Combine(root, "no-such-pack")) + " is not a folder"),
                "parity run: a pack that is not a folder is refused before the trace is opened, with exit 2 and no trace file (exit "
                + packless[0] + ", trace " + (packless[1] == null ? "none" : "left with " + packless[1].Length + " chars") + ", " + packless[2].Trim() + ")");

            // N-bd-w12-shimeji-host-1-02: a pack string no path can be made of (a NUL, written in the scenario as JSON's
            // escape, char 92 then u0000) is refused by name as the unreadable argument it is, with exit 2, its line and no
            // trace, and not as scenario-invalid, which CONTRACT section 1 does not make it. It threw ArgumentException out
            // of the command, an unhandled exception's exit.
            packName = "pack" + (char)92 + "u0000x";
            string[] nulPack = goCaught();
            packName = "pack";
            check(nulPack[0] == "2" && nulPack[1] == null
                  && nulPack[2].Contains("refused " + Path.Combine(root, "S-host-" + run + ".json") + ": the pack ")
                  && nulPack[2].Contains(" cannot be read: ") && !nulPack[2].Contains("scenario-invalid"),
                "parity run: a pack with a NUL in it is refused by name as an unreadable pack, not scenario-invalid, with exit 2 and no trace file (exit "
                + nulPack[0] + ", trace " + (nulPack[1] == null ? "none" : "left") + ", " + nulPack[2].Trim() + ")");
            // A pack whose contents cannot be read (here a file this process holds open with no sharing) is refused by name
            // too; its IOException left the command unhandled.
            string[] lockedPack;
            using (new FileStream(Path.Combine(pack, "readme.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
                lockedPack = goCaught();
            check(lockedPack[0] == "2" && lockedPack[1] == null && lockedPack[2].Contains("refused " + Path.GetFullPath(pack) + ": the pack cannot be read: "),
                "parity run: a pack whose files cannot be read is refused by name, with exit 2 and no trace file (exit "
                + lockedPack[0] + ", trace " + (lockedPack[1] == null ? "none" : "left") + ", " + lockedPack[2].Trim() + ")");

            // N-bd-w10-shimeji-host-1-01: a scenario that breaks CONTRACT section 1 is scenario-invalid, refused by name with
            // exit 2, its line and no trace file, before the pets or the pack are read, as the oracle refuses it before it
            // writes anything. Read after the trace was opened and outside the run's try, a missing field threw out of the
            // command and left an empty trace; a missing settings ran to a complete trace and a missing mascots to an error
            // footer, neither of which any comparison can use. Each line names what fails: "" when every run is refused.
            Func<string, Func<string, string>, string, string> refused = (what, edit, reason) =>
            {
                scenarioEdit = edit;
                string[] r;
                try { r = goCaught(); }
                finally { scenarioEdit = null; }
                string want = "refused " + Path.Combine(root, "S-host-" + run + ".json") + ": scenario-invalid: " + reason;
                return r[0] == "2" && r[1] == null && r[2].Contains(want) ? ""
                    : what + " (exit " + r[0] + ", trace " + (r[1] == null ? "none" : "left with " + r[1].Length + " chars") + ", " + r[2].Trim() + "); ";
            };
            Func<Action<System.Text.Json.Nodes.JsonObject>, Func<string, string>> edited = change => text =>
            {
                System.Text.Json.Nodes.JsonObject o = System.Text.Json.Nodes.JsonNode.Parse(text).AsObject();
                change(o);
                return o.ToJsonString();
            };
            string missing = "";
            foreach (string field in new[] { "id", "pack", "screens", "workArea", "dpiScale", "ticks", "rng", "settings", "mascots" })
                missing += refused("no " + field, edited(o => o.Remove(field)), "lacks '" + field + "'");
            missing += refused("no rng.kind", edited(o => o["rng"] = new System.Text.Json.Nodes.JsonObject()), "rng.kind is not none, tape or splitmix64");
            missing += refused("no anchor", edited(o => o["mascots"][0].AsObject().Remove("anchor")), "start companion 'm1' lacks its anchor [x, y]");
            check(missing.Length == 0,
                "parity run: a scenario missing a field is refused as scenario-invalid by name, with exit 2 and no trace file ("
                + (missing.Length == 0 ? "id, pack, screens, workArea, dpiScale, ticks, rng, settings, mascots, rng.kind, anchor" : missing.Trim()) + ")");
            string notJson = refused("not JSON", text => text.Substring(0, text.Length - 1), "the scenario is not JSON: ");
            check(notJson.Length == 0,
                "parity run: a scenario that is not JSON is refused as scenario-invalid, with exit 2 and no trace file (" + (notJson.Length == 0 ? "refused" : notJson.Trim()) + ")");
            string unreadable = refused("a screen with no h", edited(o => o["screens"] = System.Text.Json.Nodes.JsonNode.Parse("[{\"x\":0,\"y\":0,\"w\":1280}]")),
                "screens, workArea, activeWindow or input cannot be read: ");
            check(unreadable.Length == 0,
                "parity run: a scenario field the run cannot read is refused as scenario-invalid, with exit 2 and no trace file ("
                + (unreadable.Length == 0 ? "refused" : unreadable.Trim()) + ")");
            string ruled = refused("no behavior", edited(o => o["mascots"][0].AsObject().Remove("behavior")),
                "start companion 'm1' names no behavior (a directed start runs no selection draw)")
                + refused("a tick-0 click", edited(o => o["input"].AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse("{\"tick\":0,\"sid\":\"m1\",\"click\":\"left\"}"))),
                "a tick-0 press, release or click (button edges start at tick 1)")
                + refused("ticks -1", edited(o => o["ticks"] = -1), "ticks is not a non-negative integer");
            check(ruled.Length == 0,
                "parity run: a scenario the comparer refuses is refused too, with exit 2 and no trace file ("
                + (ruled.Length == 0 ? "no behavior, a tick-0 click, ticks -1" : ruled.Trim()) + ")");

            // N-bd-w11-shimeji-host-1-02: a scenario with no screens has no desktop, and the oracle's runner refuses it
            // (V1_SCENARIO_INVALID no screens). Unrefused, the run went on over a bounding rectangle built from Int32's
            // limits and wrote a trace.
            string screenless = refused("no screens", edited(o => o["screens"] = new System.Text.Json.Nodes.JsonArray()),
                "screens is not a list of one or more screens");
            check(screenless.Length == 0,
                "parity run: a scenario with no screens is refused as scenario-invalid, as the oracle refuses it, with exit 2 and no trace file ("
                + (screenless.Length == 0 ? "refused" : screenless.Trim()) + ")");

            // N-bd-w11-shimeji-host-1-01: a path argument that names nothing is refused by name, with exit 2, its line and
            // no trace file, as a refused map or pack is. Unchecked, a scenario file or pets folder that did not exist, or a
            // trace in a folder that did not exist, threw FileNotFound or DirectoryNotFoundException out of the command,
            // which ended the process as an unhandled exception. Each run here has one bad argument beside good ones.
            Func<string, string, string, string, string> argument = (scenarioArg, petsArg, traceArg, want) =>
            {
                var argLog = new StringWriter();
                string exit;
                try { exit = ParityRun.Run(scenarioArg, petsArg, traceArg, build, argLog).ToString(CultureInfo.InvariantCulture); }
                catch (Exception e) { exit = "threw"; argLog.Write(e.GetType().Name + ": " + e.Message); }
                bool traced = File.Exists(traceArg);
                return exit == "2" && !traced && argLog.ToString().Contains(want) ? ""
                    : "exit " + exit + ", trace " + (traced ? "left" : "none") + ", " + argLog.ToString().Trim();
            };
            string absentScenario = Path.Combine(root, "absent-scenario.json");
            string noScenario = argument(absentScenario, goodPets, Path.Combine(root, "trace-absent-scenario.jsonl"),
                "refused " + absentScenario + ": the scenario cannot be read: ");
            check(noScenario.Length == 0,
                "parity run: a scenario file that does not exist is refused by name, with exit 2 and no trace file ("
                + (noScenario.Length == 0 ? "refused" : noScenario) + ")");
            string absentPets = Path.Combine(root, "absent-pets");
            string noPets = argument(goodScenario, absentPets, Path.Combine(root, "trace-absent-pets.jsonl"),
                "refused " + absentPets + ": the pets folder cannot be listed: ");
            check(noPets.Length == 0,
                "parity run: a pets folder that does not exist is refused by name, with exit 2 and no trace file ("
                + (noPets.Length == 0 ? "refused" : noPets) + ")");
            string absentFolder = Path.Combine(root, "absent-folder"), strandedTrace = Path.Combine(absentFolder, "trace.jsonl");
            string noFolder = argument(goodScenario, goodPets, strandedTrace, "refused " + strandedTrace + ": the trace cannot be written: ");
            check(noFolder.Length == 0 && !Directory.Exists(absentFolder),
                "parity run: a trace path in a folder that does not exist is refused by name, with exit 2, the folder not made ("
                + (noFolder.Length == 0 ? "refused" : noFolder) + ")");
            // N-bd-w12-review-02: IsArgumentFault's other two kinds get a leg each, so narrowing it to IOException fails a
            // check: every leg above throws an IOException. A scenario path that is a folder throws UnauthorizedAccessException
            // here; an empty converter path, below, throws ArgumentException.
            string folderScenario = Path.Combine(root, "scenario-is-a-folder");
            Directory.CreateDirectory(folderScenario);
            string asFolder = argument(folderScenario, goodPets, Path.Combine(root, "trace-folder-scenario.jsonl"),
                "refused " + folderScenario + ": the scenario cannot be read: ");
            check(asFolder.Length == 0,
                "parity run: a scenario path that is a folder (UnauthorizedAccessException) is refused by name, with exit 2 and no trace file ("
                + (asFolder.Length == 0 ? "refused" : asFolder) + ")");

            // N-bd-w12-review-03: a refused run leaves no file at its trace path, an earlier run's included, so "exit 2, no
            // trace" holds for a reused path too; every refusal used to leave the earlier file as it was. WITNESS: a folder
            // at the trace path is never removed, only refused.
            string staleTrace = Path.Combine(root, "trace-stale.jsonl");
            File.WriteAllText(staleTrace, "an earlier run's trace\n", new UTF8Encoding(false));
            string overStale = argument(goodScenario, absentPets, staleTrace, "refused " + absentPets + ": the pets folder cannot be listed: ");
            check(overStale.Length == 0,
                "parity run: a refused run removes an earlier file at its trace path, so exit 2 leaves no trace of another run ("
                + (overStale.Length == 0 ? "removed" : overStale) + ")");
            string folderTrace = Path.Combine(root, "trace-is-a-folder");
            Directory.CreateDirectory(folderTrace);
            string atFolder = argument(goodScenario, goodPets, folderTrace, "refused " + folderTrace + ": the trace cannot be written: ");
            check(atFolder.Length == 0 && Directory.Exists(folderTrace),
                "parity run: WITNESS a folder at the trace path is refused and left where it is (" + (atFolder.Length == 0 ? "refused" : atFolder)
                + ", folder " + (Directory.Exists(folderTrace) ? "kept" : "gone") + ")");

            // The converter is the command's own argument, hashed for the build witness before the run, so its legs go
            // through the command. They come after the command's test-data-root gate, so they pass isolated: true and run on
            // every run (N-bd-w12-review-02). They used to need a data root, and without one (CI, run-gate.ps1 from an
            // unisolated shell; tests/Invoke-IsolatedGate.ps1 sets one) said so only in the marker, under a plain 'ok'.
            Func<string, string, string[]> command = (traceArg, converterArg) =>
            {
                var commandLog = new StringWriter();
                int commandExit;
                try
                {
                    commandExit = ParityRunCommand.Execute(new[] { ParityRunCommand.Flag, goodScenario, goodPets, traceArg, converterArg }, 0, commandLog,
                        typeof(ParityRun).Assembly, true);
                }
                catch (Exception e)
                {
                    commandExit = -1;
                    commandLog.Write("threw " + e.GetType().Name + ": " + e.Message);
                }
                return new[] { commandExit.ToString(CultureInfo.InvariantCulture), File.Exists(traceArg) ? "left" : "none", commandLog.ToString().Trim() };
            };
            string absentConverter = Path.Combine(root, "absent-converter"), converterTrace = Path.Combine(root, "trace-absent-converter.jsonl");
            string[] noConverter = command(converterTrace, absentConverter);
            check(noConverter[0] == "2" && noConverter[1] == "none" && noConverter[2].Contains("refused " + absentConverter + ": the converter cannot be read: "),
                "parity run: a converter that does not exist is refused by name, with exit 2 and no trace file (exit " + noConverter[0] + ", " + noConverter[2] + ")");
            string[] emptyConverter = command(Path.Combine(root, "trace-empty-converter.jsonl"), "");
            check(emptyConverter[0] == "2" && emptyConverter[1] == "none" && emptyConverter[2].Contains("refused : the converter cannot be read: "),
                "parity run: an empty converter path (ArgumentException) is refused by name, with exit 2 and no trace file (exit "
                + emptyConverter[0] + ", " + emptyConverter[2] + ")");
            // N-bd-w12-shimeji-host-1-03: a converter folder with no *.dll hashes to an empty manifest, a well-formed value
            // that binds no code, so it is refused by name. WITNESS: the same folder with an assembly in it runs to a trace.
            string codeless = Path.Combine(root, "converter-no-dll");
            Directory.CreateDirectory(codeless);
            File.WriteAllText(Path.Combine(codeless, "readme.txt"), "no code here\n", new UTF8Encoding(false));
            string[] noCode = command(Path.Combine(root, "trace-no-dll.jsonl"), codeless);
            check(noCode[0] == "2" && noCode[1] == "none" && noCode[2].Contains("refused " + codeless + ": the converter folder holds no *.dll"),
                "parity run: a converter folder with no *.dll is refused by name, since its hash would bind no code, with exit 2 and no trace file (exit "
                + noCode[0] + ", " + noCode[2] + ")");
            File.WriteAllBytes(Path.Combine(codeless, "stand-in.dll"), new byte[] { 0x4D, 0x5A });
            string[] withCode = command(Path.Combine(root, "trace-dll.jsonl"), codeless);
            check(withCode[0] == "0" && withCode[1] == "left",
                "parity run: WITNESS a converter folder holding a *.dll is hashed and the command runs to a complete trace (exit " + withCode[0] + ", trace " + withCode[1] + ")");
            // N-bd-w12-review-03 through the command: a refusal before the run (here the converter's) removes an earlier file
            // at the trace path too, because the command releases it before its first refusal.
            File.WriteAllText(converterTrace, "an earlier run's trace\n", new UTF8Encoding(false));
            string[] staleCommand = command(converterTrace, absentConverter);
            check(staleCommand[0] == "2" && staleCommand[1] == "none",
                "parity run: a command refused before the run removes an earlier file at its trace path too (exit " + staleCommand[0] + ", trace " + staleCommand[1] + ")");

            // CONTRACT v1.1 section 1: a left-button edge for a companion not active at dispatch is scenario-invalid.
            string[] absent = go(goodMap, ",{\"tick\":2,\"sid\":\"m1.b1\",\"press\":\"left\"}", xml, false);
            check(absent[0] == "2" && absent[1] != null && absent[1].Contains("\"type\":\"footer\",\"ticks\":8,\"status\":\"aborted\"")
                  && absent[1].Contains("\"type\":\"state\",\"tick\":1,") && !absent[1].Contains("\"type\":\"state\",\"tick\":2,")
                  && absent[2].Contains("scenario-invalid: tick 2: left-button target m1.b1 is not active"),
                "parity run: a left press on a companion that is not active aborts with exit 2 and no state lines for its tick ("
                + absent[2].Trim() + ")");
            string[] right = go(goodMap, ",{\"tick\":2,\"sid\":\"m9\",\"press\":\"right\"}", xml, false);
            check(right[0] == "0" && right[1] != null && right[1].Contains("\"status\":\"complete\""),
                "parity run: WITNESS a right press on an absent companion reaches neither runtime and the run completes (exit " + right[0] + ")");

            // CONTRACT 10.1 and 10.2 (CH1): the host writes trace/2, and its header keeps trace/1's fields in their order.
            string header = good[1] == null ? "" : good[1].Split('\n')[0];
            string headerKeys = KeysOf(header);
            check(header.StartsWith("{\"type\":\"header\",\"schema\":\"shimeji-parity-trace/2\",\"runtime\":\"host\",", StringComparison.Ordinal)
                  && headerKeys == "type,schema,runtime,scenario,build,rng,tickMs,dpiScale,runId,startedUtc",
                "parity run: the header declares shimeji-parity-trace/2 and no other header field changes (" + headerKeys + "; "
                + (header.Length > 72 ? header.Substring(0, 72) : header) + ")");

            // CONTRACT 10.4 and 10.5.1 (CH13, N-parity-1107-04): a draw past the tape's end yields 0.5 and the run steps on
            // to tick N, its footer complete with tapeExhausted true and the exhaustion logged as information. The tape is
            // sized from the directed run's own draw count, so one draw short stays one draw short if the pet changes.
            List<string> directedDraws = Lines(good[1], "\"kind\":\"rng-draw\"");
            int drawn = directedDraws.Count;
            rng = Tape(drawn - 1);
            string[] outrun = go(goodMap, "", xml, false);
            rng = Tape(drawn);
            string[] fullTape = go(goodMap, "", xml, false);
            rng = "{\"kind\":\"splitmix64\",\"seed\":\"0x0123456789abcdef\"}";
            string[] seeded = go(goodMap, "", xml, false);
            rng = directed;
            List<string> outrunDraws = Lines(outrun[1], "\"kind\":\"rng-draw\"");
            string lastDraw = outrunDraws.Count == 0 ? "" : outrunDraws[outrunDraws.Count - 1];
            check(drawn >= 1 && outrun[0] == "0" && outrun[1] != null
                  && outrun[1].Contains("{\"type\":\"footer\",\"ticks\":8,\"status\":\"complete\",") && FooterFlag(outrun[1]) == "true"
                  && Lines(outrun[1], "{\"type\":\"state\",\"tick\":8,").Count > 0 && outrunDraws.Count == drawn
                  && lastDraw.Contains("\"occurrence\":" + drawn + ",\"value\":0.5,")
                  && outrun[2].Contains("rng-tape-exhausted (information): the run drew past the end of its " + (drawn - 1) + "-value tape"),
                "parity run: a one-draw-short tape steps to tick N, its footer complete with tapeExhausted true, the draw past the end 0.5"
                + " and the exhaustion logged as information (exit " + outrun[0] + ", " + outrunDraws.Count + " of " + drawn + " draws, footer "
                + LastFooter(outrun[1]) + ", last draw " + lastDraw + ")");
            check(good[0] == "0" && FooterFlag(good[1]) == "false" && fullTape[0] == "0" && FooterFlag(fullTape[1]) == "false"
                  && Lines(fullTape[1], "\"kind\":\"rng-draw\"").Count == drawn && seeded[0] == "0" && FooterFlag(seeded[1]) == "false"
                  && !fullTape[2].Contains("rng-tape-exhausted") && !good[2].Contains("rng-tape-exhausted"),
                "parity run: tapeExhausted is false for none, splitmix64 and a tape the run did not outrun (none " + FooterFlag(good[1])
                + ", full tape " + FooterFlag(fullTape[1]) + ", splitmix64 " + FooterFlag(seeded[1]) + ")");
            check(FooterFlag(absent[1]) == "false" && FooterFlag(right[1]) == "false",
                "parity run: an aborted footer carries tapeExhausted too (aborted " + FooterFlag(absent[1]) + ", complete " + FooterFlag(right[1]) + ")");

            // CONTRACT section 5 with 10.5.3 (CH14, CH20): the same directed scenario traced and untraced, and again with
            // the tape outrun; the traced run's digest is recomputed from the lines it wrote.
            string[] on = go(goodMap, "", xml, false);
            string[] off = go(goodMap, "", xml, true);
            rng = Tape(drawn - 1);
            string[] outrunOff = go(goodMap, "", xml, true);
            rng = directed;
            CheckTraceOffProof(check, on[1], off[1], outrun[1], outrunOff[1]);
        }

        /// <summary>A tape of <paramref name="values"/> values, each 0.25 (any value in [0, 1) serves the directed pet).</summary>
        private static string Tape(int values)
        {
            var b = new StringBuilder("{\"kind\":\"tape\",\"values\":[");
            for (int i = 0; i < values; i++) b.Append(i == 0 ? "0.25" : ",0.25");
            return b.Append("]}").ToString();
        }

        /// <summary>The lines of a trace that contain <paramref name="part"/>; none for a run that wrote no trace.</summary>
        private static List<string> Lines(string trace, string part)
        {
            var found = new List<string>();
            if (trace == null) return found;
            foreach (string line in trace.Split('\n'))
                if (line.Contains(part)) found.Add(line);
            return found;
        }

        /// <summary>A trace's footer line, the last one, or null when it has none (a run stopped before writing it).</summary>
        private static string LastFooter(string trace)
        {
            List<string> footers = Lines(trace, "{\"type\":\"footer\"");
            return footers.Count == 0 ? null : footers[footers.Count - 1];
        }

        /// <summary>A footer's tapeExhausted as written: "true", "false", "absent" when the footer has no such boolean, or
        /// "no footer".</summary>
        private static string FooterFlag(string trace)
        {
            string footer = LastFooter(trace);
            if (footer == null) return "no footer";
            using (JsonDocument f = JsonDocument.Parse(footer))
            {
                JsonElement v;
                if (!f.RootElement.TryGetProperty("tapeExhausted", out v)) return "absent";
                return v.ValueKind == JsonValueKind.True ? "true" : v.ValueKind == JsonValueKind.False ? "false" : "absent";
            }
        }

        /// <summary>A JSON object line's property names, in order, comma-joined; empty when it is not one.</summary>
        private static string KeysOf(string line)
        {
            try
            {
                var keys = new List<string>();
                using (JsonDocument d = JsonDocument.Parse(line))
                    foreach (JsonProperty p in d.RootElement.EnumerateObject()) keys.Add(p.Name);
                return string.Join(",", keys);
            }
            catch (JsonException) { return ""; }
        }

        private const string Proven = "proven";

        /// <summary>
        /// The host's section 5 observation proof, judged by CONTRACT 10.5.3 over one build's trace-on and trace-off runs of
        /// one directed scenario: <see cref="Proven"/>; "unverified: why" when an artifact lacks what the proof needs; or
        /// "failed: why" when a run did not complete or the two runs disagree. It compares the traced draw count with the
        /// untraced unobservedDraws, the event totals, both tapeExhausted flags, the final states and the event digest,
        /// and only complete runs can satisfy it. A trace-off run has no draw lines, so its tally and its flag come from
        /// its footer alone: a run stopped before that footer has no durable tally and is unverified, and a footer with no
        /// tapeExhausted is unverified too. Neither is ever read as false from the draw lines it withheld (CH20).
        /// It never throws: an unreadable run fails the proof.
        /// </summary>
        internal static string JudgeObservationProof(string on, string off)
        {
            try
            {
                if (on == null || off == null) return "unverified: the " + (on == null ? "traced" : "untraced") + " run wrote no trace";
                string onFooter = LastFooter(on), offFooter = LastFooter(off);
                if (onFooter == null) return "unverified: the traced run has no footer";
                if (offFooter == null)
                    return "unverified: the untraced run has no footer, so no durable tally of what it withheld; nothing is read from its absent draw lines";
                using (JsonDocument a = JsonDocument.Parse(onFooter))
                using (JsonDocument b = JsonDocument.Parse(offFooter))
                {
                    JsonElement f = a.RootElement, g = b.RootElement;
                    string onStatus = FooterText(f, "status"), offStatus = FooterText(g, "status");
                    if (onStatus != "complete" || offStatus != "complete")
                        return "failed: only complete runs can prove it (traced " + onStatus + ", untraced " + offStatus + ")";
                    int ticks, onDraws, onEvents, offDraws, offEvents;
                    bool onFlag, offFlag;
                    if (!FooterInt(f, "ticks", out ticks) || !FooterInt(f, "draws", out onDraws) || !FooterInt(f, "events", out onEvents))
                        return "unverified: the traced footer lacks its ticks, draws or events";
                    if (!FooterBool(f, "tapeExhausted", out onFlag)) return "unverified: the traced footer carries no tapeExhausted";
                    string offDigest = FooterText(g, "eventSequenceSha256");
                    if (!FooterInt(g, "unobservedDraws", out offDraws) || !FooterInt(g, "unobservedEvents", out offEvents) || offDigest == null)
                        return "unverified: the untraced footer lacks unobservedDraws, unobservedEvents or eventSequenceSha256";
                    if (!FooterBool(g, "tapeExhausted", out offFlag))
                        return "unverified: the untraced footer carries no tapeExhausted, and false is never inferred from absent draw lines";
                    // 10.5.3 compares the ACTUAL trace-on draw count, so the traced footer's totals are first held to the
                    // lines the trace holds: its event lines, and among them the rng-draw ones. Compared only with each
                    // other, two footers raised alike (one writer counter wrong on both runs) would agree and prove it.
                    var events = new StringBuilder();
                    int eventLines = 0, drawLines = 0;
                    foreach (string line in Lines(on, "{\"type\":\"event\""))
                    {
                        events.Append(line).Append('\n');
                        eventLines++;
                        using (JsonDocument d = JsonDocument.Parse(line))
                            if (FooterText(d.RootElement, "kind") == "rng-draw") drawLines++;
                    }
                    if (onDraws != drawLines || onEvents != eventLines)
                        return "failed: the traced footer counts " + onDraws + " draws and " + onEvents + " events, its trace holds "
                            + drawLines + " rng-draw lines and " + eventLines + " event lines";
                    string onDigest = ParityRun.Sha256(Encoding.UTF8.GetBytes(events.ToString()));
                    string onFinal = string.Join("\n", Lines(on, "{\"type\":\"state\",\"tick\":" + ticks + ","));
                    string offStates = string.Join("\n", Lines(off, "{\"type\":\"state\""));
                    if (onDraws != offDraws) return "failed: draws " + onDraws + " traced, " + offDraws + " withheld";
                    if (onEvents != offEvents) return "failed: events " + onEvents + " traced, " + offEvents + " withheld";
                    if (onFlag != offFlag) return "failed: tapeExhausted " + (onFlag ? "true" : "false") + " traced, " + (offFlag ? "true" : "false") + " untraced";
                    if (onDigest != offDigest) return "failed: event digest " + onDigest + " traced, " + offDigest + " untraced";
                    if (onFinal.Length == 0 || onFinal != offStates) return "failed: final states differ: " + onFinal + " VERSUS " + offStates;
                    return Proven;
                }
            }
            catch (Exception e)
            {
                return "failed: the proof could not read the runs (" + e.GetType().Name + ": " + e.Message + ")";
            }
        }

        private static string FooterText(JsonElement footer, string name)
        {
            JsonElement v;
            return footer.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        private static bool FooterInt(JsonElement footer, string name, out int value)
        {
            JsonElement v;
            value = 0;
            return footer.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value);
        }

        private static bool FooterBool(JsonElement footer, string name, out bool value)
        {
            JsonElement v;
            value = false;
            if (!footer.TryGetProperty(name, out v) || (v.ValueKind != JsonValueKind.True && v.ValueKind != JsonValueKind.False)) return false;
            value = v.ValueKind == JsonValueKind.True;
            return true;
        }

        /// <summary>A trace with the text of its footer line changed (the footer must hold <paramref name="from"/>);
        /// null when it does not, so a check whose edit missed sees no artifact and cannot pass.</summary>
        private static string EditFooter(string trace, string from, string to)
        {
            string footer = LastFooter(trace);
            if (footer == null || !footer.Contains(from)) return null;
            return trace.Replace(footer, footer.Replace(from, to));
        }

        /// <summary>A trace with its footer's integer <paramref name="field"/> raised by <paramref name="delta"/> (0 leaves it
        /// as written, through the same edit); null when the footer has no such integer.</summary>
        private static string Retally(string trace, string field, int delta)
        {
            string footer = LastFooter(trace);
            if (footer == null) return null;
            Match m = Regex.Match(footer, "\"" + field + "\":(\\d+)");
            if (!m.Success) return null;
            return EditFooter(trace, m.Value, "\"" + field + "\":" + (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + delta).ToString(CultureInfo.InvariantCulture));
        }

        private static void CheckTraceOffProof(Action<bool, string> check, string on, string off, string outrunOn, string outrunOff)
        {
            int onDraws = Lines(on, "\"kind\":\"rng-draw\"").Count, onEvents = Lines(on, "{\"type\":\"event\"").Count;
            int offEvents = Lines(off, "{\"type\":\"event\"").Count;
            check(on != null && off != null && onEvents > 0 && onDraws > 0 && offEvents == 0,
                "parity trace-on/off proof: WITNESS the directed run draws and writes events, and the untraced run writes none ("
                + onDraws + " draws, " + onEvents + " events, " + offEvents + " untraced event lines)");
            string verdict = JudgeObservationProof(on, off);
            check(verdict == Proven,
                "parity trace-on/off proof: the final states, the draw and event totals and the event digest agree, as do both"
                + " tapeExhausted flags, on complete runs (" + verdict + ")");
            string outrun = JudgeObservationProof(outrunOn, outrunOff);
            check(outrun == Proven && FooterFlag(outrunOn) == "true" && FooterFlag(outrunOff) == "true",
                "parity trace-on/off proof: over an outrun tape it holds, with tapeExhausted true on both footers (" + outrun
                + ", traced " + FooterFlag(outrunOn) + ", untraced " + FooterFlag(outrunOff) + ")");

            // The traced totals are the lines the trace holds (Codex review H2-01): both footers raised alike, by one event
            // or one draw, are refused, while the same edit by zero (the unchanged twin, through the same helper) still
            // proves it, so the refusals are the totals' and not the edit's.
            string twin = JudgeObservationProof(Retally(on, "events", 0), Retally(off, "unobservedEvents", 0));
            check(twin == Proven,
                "parity trace-on/off proof: WITNESS the footer-retally helper changes nothing at +0 on both runs (" + twin + ")");
            string moreEvents = JudgeObservationProof(Retally(on, "events", 1), Retally(off, "unobservedEvents", 1));
            string moreDraws = JudgeObservationProof(Retally(on, "draws", 1), Retally(off, "unobservedDraws", 1));
            check(moreEvents.StartsWith("failed: the traced footer counts", StringComparison.Ordinal)
                  && moreDraws.StartsWith("failed: the traced footer counts", StringComparison.Ordinal),
                "parity trace-on/off proof: footer totals raised alike on both runs fail it, held to the lines the traced run holds ("
                + moreEvents + "; " + moreDraws + ")");

            // CH14: the untraced footer's flag is compared, not taken on trust.
            string forged = JudgeObservationProof(outrunOn, EditFooter(outrunOff, "\"tapeExhausted\":true", "\"tapeExhausted\":false"));
            check(forged.StartsWith("failed: tapeExhausted", StringComparison.Ordinal),
                "parity trace-on/off proof: a forged tapeExhausted on the untraced footer fails the proof (" + forged + ")");
            // CH20: an untraced run with no durable tally proves nothing, and its flag is never inferred.
            string cut = JudgeObservationProof(outrunOn, EditFooter(outrunOff, "{\"type\":\"footer\"", "{\"type\":\"cut\""));
            check(cut.StartsWith("unverified: the untraced run has no footer", StringComparison.Ordinal),
                "parity trace-on/off proof: an untraced run stopped before its footer tally is unverified, never proven (" + cut + ")");
            string timedOut = JudgeObservationProof(outrunOn, EditFooter(outrunOff, "\"status\":\"complete\"", "\"status\":\"timeout\""));
            check(timedOut.StartsWith("failed: only complete runs", StringComparison.Ordinal),
                "parity trace-on/off proof: a timed-out untraced run cannot satisfy it, though its counters agree (" + timedOut + ")");
            string flagless = JudgeObservationProof(on, EditFooter(off, ",\"tapeExhausted\":false", ""));
            check(flagless.StartsWith("unverified: the untraced footer carries no tapeExhausted", StringComparison.Ordinal),
                "parity trace-on/off proof: an untraced footer without tapeExhausted is unverified; false is never inferred ("
                + flagless + ")");
        }
    }
}
