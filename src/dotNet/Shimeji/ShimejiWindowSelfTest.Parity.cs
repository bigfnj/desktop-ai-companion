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
                CheckParityPolicy(check, Path.Combine(scratch, "parity-policy"), log);
                CheckParityProofs(check, Path.Combine(scratch, "parity-proofs"), log);
                CheckParityWindowProofs(check, Path.Combine(scratch, "parity-window-proofs"), log);
                CheckParityInputDiagnostics(check, Path.Combine(scratch, "parity-input"), log);
                CheckParityRegistryRouting(check, Path.Combine(scratch, "parity-registry"), log);
                CheckParityRegistryAdmitted(check, Path.Combine(scratch, "parity-admitted"), log);
            }
            catch (Exception e)
            {
                check(false, "parity map/2: the host checks ran without an exception: " + e);
            }
        }

        /// <summary>
        /// N-bd-parity-host-1-03: build.commit is the full source revision the project file stamped into its own metadata
        /// value, never the informational (product) version and never a placeholder, and the command refuses a build
        /// without one before it opens a trace or reads anything but the scenario (read first, since the trace path is
        /// compared with the pack it names). This build's own stamp is required: the window self-test
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

            // The command itself: exit 2, its line, and no trace, ahead of any refusal of the scenario (which does not
            // exist). The pets and converter arguments are folders the trace is not in: a trace path inside an input is
            // refused before this refusal (ParityRun.TraceClaim), and with both set to root, as they were, it was.
            string trace = Path.Combine(root, "refused.jsonl");
            var log = new StringWriter();
            int exit;
            try
            {
                exit = ParityRunCommand.Execute(new[] { ParityRunCommand.Flag, Path.Combine(root, "absent.json"), Path.Combine(root, "absent-pets"), trace,
                    Path.Combine(root, "absent-converter") }, 0, log, unstamped);
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
            // N-bd-w12-review-03, as the coordinator decided after two follow-ups that released (deleted) an earlier file at
            // the trace path and were each refuted by a spelling of an input the comparison missed: anything already at the
            // trace path (a file, a folder, a link, dangling or not) is refused by name with exit 2 BEFORE every other
            // refusal, and left as it was (ParityRun.TraceClaim). Each kind runs on an input set that completes (the WITNESS
            // after them shows it does) and on sets refused later: by the run (no pets folder), and by the command's own
            // refusals after the claim (the test-data-root gate, the build-identity gate). The refusal must be the log's
            // first and only line, so a leg fails when another refusal comes first or the command goes on after it.
            const string earlier = "an earlier run's trace\n";
            System.Reflection.Assembly stampedBuild = typeof(ParityRun).Assembly;
            Func<string, Func<string>, string, string> occupied = (traceArg, snapshot, kind) =>
            {
                string want = "refused " + traceArg + ": " + kind + " is already there and a parity run never replaces one;";
                string before = snapshot(), failed = "";
                foreach (string leg in new[] { "run complete", "run refused later", "command complete", "command refused later", "command unisolated", "command unstamped" })
                {
                    var legLog = new StringWriter();
                    string legExit;
                    try
                    {
                        string petsArg = leg.EndsWith("refused later", StringComparison.Ordinal) ? absentPets : goodPets;
                        legExit = (leg.StartsWith("run", StringComparison.Ordinal) ? ParityRun.Run(goodScenario, petsArg, traceArg, build, legLog)
                            : ParityRunCommand.Execute(new[] { ParityRunCommand.Flag, goodScenario, petsArg, traceArg, codeless }, 0, legLog,
                                leg == "command unstamped" ? typeof(object).Assembly : stampedBuild, leg != "command unisolated"))
                            .ToString(CultureInfo.InvariantCulture);
                    }
                    catch (Exception e)
                    {
                        legExit = "threw";
                        legLog.Write(e.GetType().Name + ": " + e.Message);
                    }
                    string after = snapshot();
                    string text = legLog.ToString().TrimEnd();
                    if (legExit == "2" && after == before && text.StartsWith(want, StringComparison.Ordinal) && text.IndexOf('\n') < 0) continue;
                    failed += leg + ": exit " + legExit + ", " + (after == before ? "kept" : "changed to " + after) + ", " + legLog.ToString().Trim() + "; ";
                    if (after != before) break;
                }
                return failed;
            };
            string occupiedFile = Path.Combine(root, "trace-occupied-file.jsonl");
            File.WriteAllText(occupiedFile, earlier, new UTF8Encoding(false));
            string asFile = occupied(occupiedFile, () => File.Exists(occupiedFile) ? "file " + Convert.ToBase64String(File.ReadAllBytes(occupiedFile))
                : Directory.Exists(occupiedFile) ? "a folder" : "nothing", "a file");
            check(asFile.Length == 0,
                "parity run: a file already at the trace path is refused by name before any other refusal and left byte for byte, so an earlier trace is never taken for this run's (exit 2, "
                + (asFile.Length == 0 ? "run and command, complete and refused later" : asFile.Trim()) + ")");
            string occupiedFolder = Path.Combine(root, "trace-occupied-folder"), occupiedInside = Path.Combine(occupiedFolder, "inside.txt");
            Directory.CreateDirectory(occupiedFolder);
            File.WriteAllText(occupiedInside, earlier, new UTF8Encoding(false));
            string asOccupiedFolder = occupied(occupiedFolder, () => !Directory.Exists(occupiedFolder) ? (File.Exists(occupiedFolder) ? "a file" : "nothing")
                : "folder " + string.Join("|", Directory.GetFileSystemEntries(occupiedFolder)) + " " + (File.Exists(occupiedInside) ? File.ReadAllText(occupiedInside) : "none"), "a folder");
            check(asOccupiedFolder.Length == 0,
                "parity run: a folder already at the trace path is refused by name before any other refusal and left as it was (exit 2, "
                + (asOccupiedFolder.Length == 0 ? "run and command, complete and refused later" : asOccupiedFolder.Trim()) + ")");
            // A junction whose target is gone: File.Exists says no and Directory.Exists says yes, so only reading the entry
            // itself sees it. Made here and removed by name afterwards, never by a recursive delete through it.
            string dangling = Path.Combine(root, "trace-dangling"), danglingTarget = Path.Combine(root, "trace-dangling-target");
            Directory.CreateDirectory(danglingTarget);
            bool danglingMade = TryMakeDirectoryLink(dangling, danglingTarget);
            if (danglingMade) Directory.Delete(danglingTarget);
            try
            {
                if (!danglingMade)
                    log.AppendLine("DEGRADED: parity run: the dangling link legs are not exercised (no symlink or junction could be made here)");
                else
                {
                    Func<string> linkState = () =>
                    {
                        if (!Directory.Exists(dangling) && !File.Exists(dangling)) return "nothing";
                        var info = new DirectoryInfo(dangling);
                        return info.Attributes + " to " + info.LinkTarget + ", target " + (Directory.Exists(danglingTarget) || File.Exists(danglingTarget) ? "made" : "absent");
                    };
                    string asLink = occupied(dangling, linkState, "a link");
                    check(asLink.Length == 0,
                        "parity run: a dangling junction at the trace path is refused by name as a link before any other refusal and left as it was (exit 2, "
                        + (asLink.Length == 0 ? "run and command, complete and refused later" : asLink.Trim()) + ")");
                    // The claim's comparison resolves the existing part of each path through a handle; a dangling link has
                    // none, so the comparison ran on the spelling only, and the log says so on every run (DEGRADED), never
                    // in silence. The trace path under it is then refused at the open, as an unwritable one always was.
                    string under = Path.Combine(dangling, "new-trace.jsonl");
                    var underLog = new StringWriter();
                    int underExit;
                    try { underExit = ParityRun.Run(goodScenario, goodPets, under, build, underLog); }
                    catch (Exception e)
                    {
                        underExit = -1;
                        underLog.Write("threw " + e.GetType().Name + ": " + e.Message);
                    }
                    check(underExit == 2 && underLog.ToString().Contains("DEGRADED: parity run: " + dangling + " (Win32 error ")
                          && underLog.ToString().Contains("refused " + under + ": the trace cannot be written: ") && linkState().EndsWith("target absent", StringComparison.Ordinal),
                        "parity run: a trace path under a link no handle resolves says DEGRADED on the run's log, naming the link, and is refused unwritten (exit "
                        + underExit + ", " + underLog.ToString().Trim() + ")");
                }
            }
            finally
            {
                if (danglingMade) try { Directory.Delete(dangling); } catch { }
            }
            // The open is the last line of defence: a file that appears at the trace path after the claim (another run, the
            // caller) is refused by the same line, never truncated (FileMode.CreateNew, never Create). Claimed and run on
            // the run's own two steps, with the file written between them.
            string raceTrace = Path.Combine(root, "trace-race.jsonl");
            var raceLog = new StringWriter();
            ParityRun.ScenarioFile raceScenario = ParityRun.ScenarioFile.Read(goodScenario);
            bool raceClaimed = ParityRun.TraceClaim.Claim(raceScenario, goodPets, raceTrace, null, raceLog) != null;
            File.WriteAllText(raceTrace, earlier, new UTF8Encoding(false));
            int raceExit;
            try { raceExit = ParityRun.Run(raceScenario, goodPets, raceTrace, build, raceLog, false); }
            catch (Exception e)
            {
                raceExit = -1;
                raceLog.Write("threw " + e.GetType().Name + ": " + e.Message);
            }
            string raceLeft = File.Exists(raceTrace) ? (File.ReadAllText(raceTrace) == earlier ? "kept" : "overwritten") : "deleted";
            check(raceClaimed && raceExit == 2 && raceLeft == "kept"
                  && raceLog.ToString().Contains("refused " + raceTrace + ": a file is already there and a parity run never replaces one;"),
                "parity run: a file that appears at the trace path after the claim is refused at the open by name and never overwritten (claimed "
                + raceClaimed + ", exit " + raceExit + ", file " + raceLeft + ", " + raceLog.ToString().Trim() + ")");
            // WITNESS: the occupied legs' complete input set, at a fresh path, runs to a new trace through the run and the
            // command, so their exit 2 is the occupied path's and nothing else's.
            string freshRun = Path.Combine(root, "trace-fresh-run.jsonl"), freshCommand = Path.Combine(root, "trace-fresh-command.jsonl");
            int freshRunExit = ParityRun.Run(goodScenario, goodPets, freshRun, build, new StringWriter());
            int freshCommandExit = ParityRunCommand.Execute(new[] { ParityRunCommand.Flag, goodScenario, goodPets, freshCommand, codeless }, 0, new StringWriter(), stampedBuild, true);
            check(freshRunExit == 0 && File.Exists(freshRun) && freshCommandExit == 0 && File.Exists(freshCommand),
                "parity run: WITNESS the occupied legs' complete input set runs to a new trace at a fresh path (run exit " + freshRunExit
                + ", command exit " + freshCommandExit + ")");

            // The claim's other half, kept as defence in depth for NEW files only: a trace path inside an input folder is
            // refused by name with exit 2 and nothing is written there. One check per folder kind, each through the run and,
            // where the run has that input, the command, with the trace spelled otherwise than the folder (case, "..",
            // "/", a trailing separator on the folder). A leg that wrote a file into an input folder takes it back out
            // (it made that file; nothing was there), so the checks after it read what they always read. No leg here may
            // say DEGRADED: every path in them resolves.
            Func<string, string, string, string, string> fresh = (petsArg, traceArg, converterArg, want) =>
            {
                var legLog = new StringWriter();
                string legExit;
                try
                {
                    legExit = (converterArg == null ? ParityRun.Run(goodScenario, petsArg, traceArg, build, legLog)
                        : ParityRunCommand.Execute(new[] { ParityRunCommand.Flag, goodScenario, petsArg, traceArg, converterArg }, 0, legLog, stampedBuild, true))
                        .ToString(CultureInfo.InvariantCulture);
                }
                catch (Exception e)
                {
                    legExit = "threw";
                    legLog.Write(e.GetType().Name + ": " + e.Message);
                }
                bool made = File.Exists(traceArg);
                if (made) File.Delete(traceArg);
                if (legExit == "2" && !made && legLog.ToString().Contains(want) && !legLog.ToString().Contains("DEGRADED")) return "";
                return (converterArg == null ? "run" : "command") + ": exit " + legExit + ", trace " + (made ? "written (taken out again)" : "none") + ", " + legLog.ToString().Trim() + "; ";
            };
            const string intoInputs = "; a parity run never writes into its inputs";
            string petsTrace = Path.Combine(goodPets, "Par", "..", "Par", "new-trace.jsonl"), petsSlash = goodPets + Path.DirectorySeparatorChar;
            string petsWant = "refused " + petsTrace + ": the trace path is inside the pets folder " + petsSlash + intoInputs;
            string inPets = fresh(petsSlash, petsTrace, null, petsWant) + fresh(petsSlash, petsTrace, codeless, petsWant);
            check(inPets.Length == 0,
                "parity run: a new trace path inside the pets folder is refused by name and nothing is written there (exit 2, "
                + (inPets.Length == 0 ? "run and command" : inPets.Trim()) + ")");
            string packTrace = Path.Combine(root, "PACK") + Path.AltDirectorySeparatorChar + "new-trace.jsonl";
            string packWant = "refused " + packTrace + ": the trace path is inside the scenario's pack folder " + Path.GetFullPath(pack) + intoInputs;
            string inPack = fresh(goodPets, packTrace, null, packWant) + fresh(goodPets, packTrace, codeless, packWant);
            check(inPack.Length == 0,
                "parity run: a new trace path inside the scenario's pack folder is refused by name and nothing is written there (exit 2, "
                + (inPack.Length == 0 ? "run and command" : inPack.Trim()) + ")");
            // WITNESS: a trace beside the pets folder whose name only starts like it is no input, and the run completes.
            string besidePets = goodPets + "-beside.jsonl";
            int besideExit = ParityRun.Run(goodScenario, goodPets, besidePets, build, new StringWriter());
            check(besideExit == 0 && File.Exists(besidePets),
                "parity run: WITNESS a trace path beside the pets folder, its name starting like the folder's, is no input and runs to a trace (exit "
                + besideExit + ", trace " + (File.Exists(besidePets) ? "written" : "none") + ")");
            string converterTraceNew = Path.Combine(codeless.ToUpperInvariant(), "new-trace.dll");
            string inConverter = fresh(goodPets, converterTraceNew, codeless, "refused " + converterTraceNew + ": the trace path is inside the converter " + codeless + intoInputs);
            check(inConverter.Length == 0,
                "parity run: a new trace path inside the converter folder is refused by name and nothing is written there (exit 2, "
                + (inConverter.Length == 0 ? "command" : inConverter.Trim()) + ")");

            // Follow-up 2 (the wave 13 review): a link INSIDE an input folder. LoadPets reads a pet folder that is a junction
            // through it, so a new trace spelled through the junction is inside the pets folder (as written), and one
            // spelled as the folder the junction leads to is inside that pet folder (as the pet folder resolves, on its
            // own). The links are made here (TryMakeDirectoryLink: a symbolic link where this account may make one, else
            // mklink /J) and removed by name afterwards, never by a recursive delete through them; a box that can make
            // neither says DEGRADED.
            string linkedPets = Path.Combine(root, "pets-linked"), linkedTarget = Path.Combine(root, "pets-linked-target");
            string linkedPet = Path.Combine(linkedPets, "Ball");
            Directory.CreateDirectory(linkedPets);
            Directory.CreateDirectory(linkedTarget);
            File.Copy(Path.Combine(goodPets, "Par", "animations.xml"), Path.Combine(linkedTarget, "animations.xml"), true);
            File.Copy(Path.Combine(goodPets, "Par", "parity-map.json"), Path.Combine(linkedTarget, "parity-map.json"), true);
            if (!TryMakeDirectoryLink(linkedPet, linkedTarget))
                log.AppendLine("DEGRADED: parity run: the linked pet folder legs are not exercised (no symlink or junction could be made here)");
            else
            {
                string throughLink, asLinkTarget;
                try
                {
                    string throughTrace = Path.Combine(linkedPet, "new-trace.jsonl");
                    string throughWant = "refused " + throughTrace + ": the trace path is inside the pets folder " + linkedPets + intoInputs;
                    throughLink = fresh(linkedPets, throughTrace, null, throughWant) + fresh(linkedPets, throughTrace, codeless, throughWant);
                    string targetTrace = Path.Combine(linkedTarget, "new-trace.jsonl");
                    string targetWant = "refused " + targetTrace + ": the trace path is inside the pet folder " + linkedPet + intoInputs;
                    asLinkTarget = fresh(linkedPets, targetTrace, null, targetWant) + fresh(linkedPets, targetTrace, codeless, targetWant);
                }
                finally
                {
                    try { Directory.Delete(linkedPet); } catch { }
                }
                check(throughLink.Length == 0,
                    "parity run: a new trace path spelled through a pet folder that is a junction is refused by name as inside the pets folder (exit 2, "
                    + (throughLink.Length == 0 ? "run and command" : throughLink.Trim()) + ")");
                check(asLinkTarget.Length == 0,
                    "parity run: a new trace path in the folder a junction pet folder leads to is refused by name as inside that pet folder (exit 2, "
                    + (asLinkTarget.Length == 0 ? "run and command" : asLinkTarget.Trim()) + ")");
            }

            // The wave 13 fix-2 review: past 260 characters CreateFileW refused the resolution's plain path (this process is
            // not longPathAware), and the comparison fell back to the spelling in silence, so a pet folder that is a
            // junction at a long path no longer protected the folder it leads to. The pets folder here sits past 260
            // characters in the scratch root; its Ball is a junction (made at a short name and moved there, since mklink
            // is not long-path aware) to a short folder, and the trace is spelled in that short folder. It must be refused
            // as inside the long pet folder, with no DEGRADED note: the resolution ran. WITNESS: the same long pets folder
            // runs to a trace at a fresh path, so the run itself reads through it.
            string longPets = Path.Combine(root, "long-" + new string('l', 120), new string('m', 120), "pets");
            string longPet = Path.Combine(longPets, "Ball"), longTarget = Path.Combine(root, "long-pet-target"), shortLink = Path.Combine(root, "long-pet-link");
            Directory.CreateDirectory(longPets);
            Directory.CreateDirectory(longTarget);
            File.Copy(Path.Combine(goodPets, "Par", "animations.xml"), Path.Combine(longTarget, "animations.xml"), true);
            File.Copy(Path.Combine(goodPets, "Par", "parity-map.json"), Path.Combine(longTarget, "parity-map.json"), true);
            bool longMade = false;
            try
            {
                if (TryMakeDirectoryLink(shortLink, longTarget))
                {
                    try
                    {
                        Directory.Move(shortLink, longPet);
                        longMade = (File.GetAttributes(longPet) & FileAttributes.ReparsePoint) != 0;
                    }
                    catch (Exception e)
                    {
                        log.AppendLine("LOG: parity run: the long pet folder link could not be moved into place: " + e.GetType().Name + ": " + e.Message);
                    }
                }
                if (!longMade)
                    log.AppendLine("DEGRADED: parity run: the long-path legs are not exercised (no junction could be made at a path past 260 characters here)");
                else
                {
                    string longTrace = Path.Combine(longTarget, "new-trace.jsonl");
                    string longWant = "refused " + longTrace + ": the trace path is inside the pet folder " + longPet + intoInputs;
                    string pastMaxPath = fresh(longPets, longTrace, null, longWant) + fresh(longPets, longTrace, codeless, longWant);
                    check(longPet.Length > 260 && pastMaxPath.Length == 0,
                        "parity run: a new trace path in the folder a junction pet folder past 260 characters leads to is refused by name, with no DEGRADED note (exit 2, "
                        + longPet.Length + " characters, " + (pastMaxPath.Length == 0 ? "run and command" : pastMaxPath.Trim()) + ")");
                    string longWitness = Path.Combine(root, "trace-long-witness.jsonl");
                    int longWitnessExit = ParityRun.Run(goodScenario, longPets, longWitness, build, new StringWriter());
                    check(longWitnessExit == 0 && File.Exists(longWitness),
                        "parity run: WITNESS the pets folder past 260 characters, its pet folder a junction, runs to a trace at a fresh path (exit " + longWitnessExit + ")");
                }
            }
            finally
            {
                // By name and not recursive: the link goes and its target stays.
                if (longMade) try { Directory.Delete(longPet); } catch { }
                try { if (Directory.Exists(shortLink)) Directory.Delete(shortLink); } catch { }
            }

            // The converter's half. The converter is given through a junction to its folder (converter-linked to
            // converter-linked-real), and that folder holds a junction the run never reads (sub, to
            // converter-linked-outside): a new trace path spelled inside the converter folder, as the argument names it or
            // as it resolves, is refused whatever sub links to, and nothing is written behind sub. Compared only as
            // resolved, the trace followed sub out of the folder.
            string convReal = Path.Combine(root, "converter-linked-real"), convLink = Path.Combine(root, "converter-linked");
            string convOutside = Path.Combine(root, "converter-linked-outside"), convSub = Path.Combine(convReal, "sub");
            Directory.CreateDirectory(convReal);
            Directory.CreateDirectory(convOutside);
            File.WriteAllBytes(Path.Combine(convReal, "stand-in.dll"), new byte[] { 0x4D, 0x5A });
            try
            {
                if (!TryMakeDirectoryLink(convLink, convReal) || !TryMakeDirectoryLink(convSub, convOutside))
                    log.AppendLine("DEGRADED: parity run: the linked converter folder legs are not exercised (no symlink or junction could be made here)");
                else
                {
                    string asNamed = Path.Combine(convLink, "sub", "new-trace.dll"), asResolved = Path.Combine(convReal, "sub", "new-trace.dll");
                    string inLinkedConverter = fresh(goodPets, asNamed, convLink, "refused " + asNamed + ": the trace path is inside the converter " + convLink + intoInputs)
                        + fresh(goodPets, asResolved, convLink, "refused " + asResolved + ": the trace path is inside the converter " + convLink + intoInputs);
                    check(inLinkedConverter.Length == 0,
                        "parity run: a new trace path spelled inside a converter folder given through a junction is refused by name, whatever a junction in it links to (exit 2, "
                        + (inLinkedConverter.Length == 0 ? "as named and as resolved" : inLinkedConverter.Trim()) + ")");
                }
            }
            finally
            {
                // By name and not recursive: a link goes and its target stays, and a real folder with files is refused.
                try { Directory.Delete(convSub); } catch { }
                try { Directory.Delete(convLink); } catch { }
            }

            CheckTraceClaimLimits(check, root, log, goodScenario, goodPets, Path.GetFullPath(pack), codeless, build, fresh, intoInputs);

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

        /// <summary>
        /// N-bd-w13-review-09, the limits of the trace claim's input-folder comparison: a data stream of an input is
        /// refused, a share and a path the resolution cannot read or find say DEGRADED, and each branch of
        /// ParityRun.LongPathForm and of ParityRun.FinalPathOrNull's failures is reached. <paramref name="fresh"/> is the
        /// caller's leg: exit 2, nothing made at the trace path (a leg that made it takes it back out), the wanted line
        /// on the log and no DEGRADED note.
        /// </summary>
        private static void CheckTraceClaimLimits(Action<bool, string> check, string root, StringBuilder log, string goodScenario, string goodPets,
            string packFull, string codeless, Dictionary<string, string> build, Func<string, string, string, string, string> fresh, string intoInputs)
        {
            // A colon in the trace's final name names a data stream of the file or folder before it, so a trace there went
            // INTO that input (its content as it was, its time stamp moved), past the occupant check (the stream was new)
            // and the folder comparison. Each input kind: the scenario file, a converter dll, the pack folder and the pets
            // folder, through the run and the command. A stream a leg made is taken back out by its leg (File.Delete on a
            // stream path deletes that stream only).
            string streams = "";
            foreach (string owner in new[] { goodScenario, Path.Combine(codeless, "stand-in.dll"), packFull, goodPets })
            {
                string streamTrace = owner + ":t";
                DateTime stamp = File.GetLastWriteTimeUtc(owner);
                string want = "refused " + streamTrace + ": the colon in its name names a data stream of " + owner + ", not a file of its own;";
                string legs = fresh(goodPets, streamTrace, null, want) + fresh(goodPets, streamTrace, codeless, want);
                if (File.GetLastWriteTimeUtc(owner) != stamp) legs += "its time stamp moved; ";
                if (legs.Length > 0) streams += Path.GetFileName(owner) + ": " + legs;
            }
            check(streams.Length == 0,
                "parity run: a trace path naming a data stream of an input (scenario:t, a converter dll:t, pack:t, pets:t) is refused by name and nothing is written into the input (exit 2, "
                + (streams.Length == 0 ? "run and command, four inputs" : OneLine(streams)) + ")");

            // LongPathForm's device branches: a new trace inside the pets folder spelled in the \\.\ or the \\?\ form is
            // inside it only as resolved (as written it starts with the prefix, the pets folder with its drive), so it is
            // refused only when the resolution opened it, which a broken branch (\\.\ handed over as a share, \\?\
            // prefixed again) cannot: that leg says DEGRADED and the run writes into the pets folder.
            string inPets = Path.Combine(Path.GetFullPath(goodPets), "Par", "new-trace-device.jsonl");
            string devices = "";
            foreach (string prefix in new[] { @"\\.\", @"\\?\" })
            {
                string deviceTrace = prefix + inPets;
                devices += fresh(goodPets, deviceTrace, null, "refused " + deviceTrace + ": the trace path is inside the pets folder " + goodPets + intoInputs);
            }
            check(devices.Length == 0,
                @"parity run: a new trace path inside the pets folder spelled in the \\.\ or the \\?\ form is refused by name with no DEGRADED note, the resolution having opened it (exit 2, "
                + (devices.Length == 0 ? "both forms" : OneLine(devices)) + ")");

            // A trace path that resolves to a share says so on every run: the comparison cannot see behind a share, so one
            // that leads back to this machine reaches an input folder unrefused. Here a loopback share that does not exist,
            // which no box answers: no part of the path exists, which says DEGRADED too, and the open refuses it unwritten.
            string noShare = @"\\localhost\parity-no-such-share-" + Guid.NewGuid().ToString("N").Substring(0, 8) + @"\new-trace.jsonl";
            var noShareLog = new StringWriter();
            int noShareExit;
            try { noShareExit = ParityRun.Run(goodScenario, goodPets, noShare, build, noShareLog); }
            catch (Exception e)
            {
                noShareExit = -1;
                noShareLog.Write("threw " + e.GetType().Name + ": " + e.Message);
            }
            string noShareText = noShareLog.ToString();
            check(noShareExit == 2 && noShareText.Contains("DEGRADED: parity run: the trace path resolves to the share " + noShare + ",")
                  && noShareText.Contains("DEGRADED: parity run: " + noShare + " (no part of it exists)")
                  && noShareText.Contains("refused " + noShare + ": the trace cannot be written: "),
                "parity run: a trace path on a loopback share no part of which exists says DEGRADED for the share and for the missing path, and is refused unwritten (exit "
                + noShareExit + ", " + OneLine(noShareText) + ")");

            // LongPathForm's share branch: the pets folder spelled through this machine's administrative share. The claim
            // alone runs (nothing is opened or written): it says the share, and the share's \\?\UNC\ form resolved, so no
            // "could not be resolved" note. A box with no administrative share says DEGRADED instead.
            string fullPets = Path.GetFullPath(goodPets);
            string adminPets = fullPets.Length > 2 && fullPets[1] == ':' && char.IsLetter(fullPets[0])
                ? @"\\localhost\" + fullPets[0] + "$" + fullPets.Substring(2) : null;
            if (adminPets == null || !Directory.Exists(adminPets))
                log.AppendLine("DEGRADED: parity run: the loopback share leg is not exercised (no administrative share answers here)");
            else
            {
                string shareTrace = Path.Combine(adminPets, "Par", "new-trace-share.jsonl");
                var shareLog = new StringWriter();
                bool claimed = ParityRun.TraceClaim.Claim(ParityRun.ScenarioFile.Read(goodScenario), goodPets, shareTrace, null, shareLog) != null;
                string shareText = shareLog.ToString();
                check(shareText.IndexOf("DEGRADED: parity run: the trace path resolves to the share " + shareTrace + ",", StringComparison.OrdinalIgnoreCase) >= 0
                      && shareText.IndexOf("could not be resolved", StringComparison.Ordinal) < 0 && !File.Exists(shareTrace),
                    "parity run: a new trace path in the pets folder spelled through this machine's administrative share says DEGRADED, naming the share, which resolved (claimed "
                    + claimed + ", " + OneLine(shareText) + ")");
            }

            // FinalPathOrNull's second failure: a handle that opens and has no final path name. The pipe namespace answers
            // as a folder and opens, and GetFinalPathNameByHandle refuses it; the log names it as DEGRADED and the open
            // refuses the trace unwritten (no pipe of that name).
            const string pipes = @"\\.\pipe\";
            if (!Directory.Exists(pipes))
                log.AppendLine("DEGRADED: parity run: the no-final-path leg is not exercised (the pipe namespace does not answer as a folder here)");
            else
            {
                string pipeTrace = pipes + "parity-trace-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".jsonl";
                var pipeLog = new StringWriter();
                int pipeExit;
                try { pipeExit = ParityRun.Run(goodScenario, goodPets, pipeTrace, build, pipeLog); }
                catch (Exception e)
                {
                    pipeExit = -1;
                    pipeLog.Write("threw " + e.GetType().Name + ": " + e.Message);
                }
                string pipeText = pipeLog.ToString();
                check(pipeExit == 2 && pipeText.Contains("DEGRADED: parity run: " + pipes + " (Win32 error ")
                      && pipeText.Contains("refused " + pipeTrace + ": the trace cannot be written: "),
                    "parity run: a trace path whose existing part opens with no final path name says DEGRADED, naming it, and is refused unwritten (exit "
                    + pipeExit + ", " + OneLine(pipeText) + ")");
            }

            // A part whose attributes cannot be read was taken for absent in silence (File.Exists says no). Here a malformed
            // name, which takes the same branch as a folder this account may not read: DEGRADED, naming it, and the open
            // refuses the trace unwritten.
            string malformed = Path.Combine(root, "bad?name"), malformedTrace = Path.Combine(malformed, "new-trace.jsonl");
            var malformedLog = new StringWriter();
            int malformedExit;
            try { malformedExit = ParityRun.Run(goodScenario, goodPets, malformedTrace, build, malformedLog); }
            catch (Exception e)
            {
                malformedExit = -1;
                malformedLog.Write("threw " + e.GetType().Name + ": " + e.Message);
            }
            string malformedText = malformedLog.ToString();
            check(malformedExit == 2 && malformedText.Contains("DEGRADED: parity run: " + malformed + " (its attributes could not be read: Win32 error ")
                  && malformedText.Contains("refused " + malformedTrace + ": the trace cannot be written: "),
                "parity run: a trace path through a part whose attributes cannot be read says DEGRADED, naming it, and is refused unwritten (exit "
                + malformedExit + ", " + OneLine(malformedText) + ")");
        }

        /// <summary>A run's log on one line for a check's detail: a second line starting DEGRADED would read on the marker
        /// as the self-test's own DEGRADED report (tests/Invoke-SelfTests.ps1 prints those beside the verdict).</summary>
        private static string OneLine(string text)
        {
            return text.Trim().Replace("\r\n", " | ").Replace("\n", " | ");
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
