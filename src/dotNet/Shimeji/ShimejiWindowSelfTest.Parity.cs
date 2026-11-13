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
        /// burndown/parity-host-1, the host half of CONTRACT v1.1 (N-shimeji-1106-13): a parity run reads one map
        /// version only (map/2 then), checks every mapping strictly, binds each emitted pose to its source position by the
        /// pose object, traces that position (also for an old member's pose kept after a Transform), refuses before it opens
        /// a trace, stops at a left-button edge for a companion that is not active, and proves a trace-on and a trace-off
        /// run agree. burndown/parity-host-2 moves it to trace/2 (CONTRACT section 10): the header, a tape outrun on 0.5
        /// with tapeExhausted in every footer, and the observation proof judged by 10.5.3. burndown/host-mute-1 moves it to
        /// shimeji-parity-map/3 (CONTRACT v1.4 draft P1-F): a run reads map/3 only and refuses map/1 and map/2 with a
        /// regenerate message, and CheckParityMapSound checks the Sound rows, each rule by the reader and by a run.
        /// CheckParityMuteTrace checks the trace writer's Mute dispositions (P2-C), the MUTE_PROVENANCE_MISSING channel
        /// (FC-2, FC-3) and the information lines (FC-6), C18 at every observation site, each label in its own try and
        /// three of them also through a parity run. Every map and pet here is generated; nothing is read from a real pack.
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
                CheckParityMapSound(check, Path.Combine(scratch, "parity-sound"));
                CheckParityMuteTrace(check, Path.Combine(scratch, "parity-mute"));
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

        /// <summary>A map/3 document for <see cref="ParDocument"/>, its poses written in the order given (a list, so a
        /// test can repeat a key) or as raw text, and <paramref name="soundRows"/> as the raw soundParameters text (an
        /// empty array by default; null leaves the property out).</summary>
        private static string ParMapJson(string set, bool full, IEnumerable<KeyValuePair<string, string>> poses, string schema = ParityMap.Schema, string rawPoses = null,
            string soundRows = "[]")
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
            if (soundRows != null) b.Append(",\"soundParameters\":").Append(soundRows);
            return b.Append('}').ToString();
        }

        /// <summary>Walk (30, Stay, Duration 3, a frame-cached Draggable that draws), and with <paramref name="full"/>
        /// Mover (31, Embedded MoveWithTurn: its last variant when="false" with one image-less 1-tick pose), Ends (32,
        /// Animate: decision 68's placeholder) and Seq (33, a Sequence of Walk). Cells 64x64, two tiles. With
        /// <paramref name="sounds"/> (burndown/host-mute-1, CONTRACT v1.4 draft P1-D) every container kind defines Sound:
        /// Walk's own literal "to", the Walk behaviour's action-cached bind 'a' + 1, and with <paramref name="full"/> Mover's
        /// frame-cached 'b', Ends' literal true, Seq's own literal " ${x}" and Seq's six references to Walk: 33:0 binds
        /// "1", 33:1 to 33:3 each bind "" (the emitted text of /, a backslash and the empty string, control 5), 33:4 binds
        /// nothing, and 33:5 binds only "sound", which is not Sound by the ordinal name.</summary>
        private static XmlData.RootNode ParDocument(bool full, bool sounds = false)
        {
            Func<string, string, string, XmlData.ParamNode> param = (name, value, cache) => new XmlData.ParamNode { Name = name, Value = value, Cache = cache };
            Func<string, string, XmlData.BindNode[]> bind = (value, cache) => new[] { new XmlData.BindNode { Name = "Sound", Value = value, Cache = cache } };
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
            if (sounds)
            {
                walkBehaviour.Behaviour.Bind = bind("'a' + 1", "action");
                walk.Act.Params = new[] { walk.Act.Params[0], walk.Act.Params[1], param("Sound", "to", "none") };
            }
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
                if (sounds)
                {
                    mover.Act.Params = new[] { param("Sound", "'b'", "frame") };
                    ends.Act.Params = new[] { param("Sound", "true", "none") };
                    seq.Act.Params = new[] { param("Sound", " ${x}", "none") };
                    Func<XmlData.BindNode[], XmlData.CompositeChildNode> walkRef = binds => new XmlData.CompositeChildNode { Animation = "Walk", Bind = binds };
                    seq.Act.Children.Child = new[]
                    {
                        walkRef(bind("1", "none")), walkRef(bind("", "none")), walkRef(bind("", "none")), walkRef(bind("", "none")), walkRef(null),
                        walkRef(new[] { new XmlData.BindNode { Name = "sound", Value = "x", Cache = "none" } }),
                    };
                }
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

        /// <summary>Parse and apply; null when accepted, else the refusal's message: an InvalidDataException's message
        /// alone, any other exception's prefixed with its type name, so an exact comparison with a declared message also
        /// asserts the type.</summary>
        private static string ParApply(string json, bool full)
        {
            return ParApply(json, ParDocument(full));
        }

        /// <summary>As <see cref="ParApply(string, bool)"/>, against a document the caller built (it is only read).</summary>
        private static string ParApply(string json, XmlData.RootNode document)
        {
            try
            {
                ParityMap.Parse(json).Apply(TickType.Compile("Par", document), document);
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

            // The label stays "parity map/2:" because existing mutation cases match it; the message is map/3's (P1-F).
            string retired = ParApply(ParMapJson("Par", true, poses, "shimeji-parity-map/1"), true);
            check(retired != null && retired.Contains("is shimeji-parity-map/1; regenerate it with the current converter (map/3 required)"),
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

        /// <summary>A literal Sound row on Walk (action 30): authored /TO, emitted as the lowered "to".</summary>
        private const string ParSoundLiteral = "{\"kind\":\"action\",\"nodeId\":30,\"sourceRefKey\":\"Par|conf/actions.xml|/Mascot[1]/ActionList[1]/Action[1]\","
            + "\"emittedValue\":\"to\",\"emittedCache\":\"none\",\"mode\":\"literal\",\"sourceValue\":\"/TO\",\"sourceKind\":\"string\"}";
        /// <summary>An evaluated, action-cached Sound bind on the Walk behaviour (behaviour 10).</summary>
        private const string ParSoundEvaluated = "{\"kind\":\"behaviour\",\"nodeId\":10,\"sourceRefKey\":\"Par|conf/behaviors.xml|/Mascot[1]/BehaviorList[1]/Behavior[1]\","
            + "\"emittedValue\":\"'a' + 1\",\"emittedCache\":\"action\",\"mode\":\"evaluated\"}";
        /// <summary>A literal Sound bind on Seq's first child reference (33:0), authored " 1", a number by Java trim.</summary>
        private const string ParSoundChild = "{\"kind\":\"child\",\"nodeId\":33,\"childIndex\":0,\"sourceRefKey\":\"Par|conf/actions.xml|/Mascot[1]/ActionList[1]/Action[4]/ActionReference[1]\","
            + "\"emittedValue\":\"1\",\"emittedCache\":\"none\",\"mode\":\"literal\",\"sourceValue\":\" 1\",\"sourceKind\":\"number\"}";

        /// <summary>
        /// burndown/host-mute-1, C13's run leg: a parity run of one map beside one animations.xml text (the arguments, in
        /// that order), through ParityRun.Run in this process with a generated scenario and pack, as CheckParityRunRefusals'
        /// go does, one pets folder and scenario per run so no run reads another's leftovers. Gives the exit code, the
        /// trace text (null when no trace file was left), the run's log and the map's path, which a refusal names. A run
        /// that throws (an exception no refusal catches, as a mutant can cause) gives exit "threw" and the exception in the
        /// log, so it fails the one label whose map it was, not every check after it. The member is
        /// <paramref name="imageSet"/>, started in <paramref name="behaviour"/> (the Mute legs' Mu starts in Go).
        /// </summary>
        private static Func<string, string, string[]> ParRunner(string root, string imageSet = "Par", string behaviour = "Walk")
        {
            Directory.CreateDirectory(root);
            string pack = Path.Combine(root, "pack");
            Directory.CreateDirectory(pack);
            File.WriteAllText(Path.Combine(pack, "readme.txt"), "a generated stand-in pack\n", new UTF8Encoding(false));
            var build = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "commit", "selftest" }, { "exeSha256", new string('0', 64) }, { "engineSha256", new string('1', 64) }, { "converterSha256", new string('2', 64) },
            };
            int run = 0;
            return (xml, map) =>
            {
                run++;
                string pets = Path.Combine(root, "pets" + run), member = Path.Combine(pets, imageSet), mapPath = Path.Combine(member, "parity-map.json");
                Directory.CreateDirectory(member);
                File.WriteAllText(Path.Combine(member, "animations.xml"), xml, new UTF8Encoding(false));
                File.WriteAllText(mapPath, map, new UTF8Encoding(false));
                string scenario = Path.Combine(root, "S-sound-" + run + ".json");
                File.WriteAllText(scenario, "{\"schema\":\"shimeji-parity-scenario/1\",\"id\":\"S-sound-" + run + "\",\"description\":\"generated\","
                    + "\"pack\":\"pack\",\"screens\":[{\"x\":0,\"y\":0,\"w\":1280,\"h\":720}],\"workArea\":{\"x\":0,\"y\":0,\"w\":1280,\"h\":680},"
                    + "\"dpiScale\":1.0,\"ticks\":8,\"rng\":{\"kind\":\"none\"},\"settings\":{},"
                    + "\"mascots\":[{\"sid\":\"m1\",\"imageSet\":" + JsonLine.Quote(imageSet) + ",\"behavior\":" + JsonLine.Quote(behaviour) + ",\"anchor\":[640,680],\"lookRight\":false}],"
                    + "\"activeWindow\":[{\"tick\":0,\"visible\":false}],\"input\":[{\"tick\":0,\"cursor\":[100,100]}]}", new UTF8Encoding(false));
                string trace = Path.Combine(root, "trace" + run + ".jsonl");
                var runLog = new StringWriter();
                string exit;
                try
                {
                    exit = ParityRun.Run(scenario, pets, trace, build, runLog, false).ToString(CultureInfo.InvariantCulture);
                }
                catch (Exception e)
                {
                    exit = "threw";
                    runLog.Write(e.GetType().Name + ": " + e.Message);
                }
                return new[] { exit, File.Exists(trace) ? File.ReadAllText(trace) : null, runLog.ToString(), mapPath };
            };
        }

        /// <summary>
        /// burndown/host-mute-1, the host half of N-bd-codex-converter-1-02: the strict shimeji-parity-map/3 row reader
        /// (CONTRACT v1.4 draft P1-B, P1-C, P1-E and FC-1, read with the coordinator's revision 2 review). One map per
        /// rule, each breaking exactly that rule (C13), and each label compares the reader's whole message with a message
        /// declared here, never one derived from the exception just caught, so another guard or an exception of another
        /// type (ParApply names its type) fails rather than substitutes. Commit 4b adds the binding (P1-D) and the
        /// coverage (P1-E): the document defines Sound in every container kind, the complete rows bind each definition
        /// once, and each binding rule has its own map and label. Its own try: CheckParityMapV2 runs every lane's checks
        /// in one, and an exception here must not hide another lane's labels.
        /// C13 (the coordinator's ruling after round 2): every map is also given to a parity run (<see cref="ParRunner"/>,
        /// ParityRun.Run in this process, as CheckParityRunRefusals' go does), and the SAME label requires exit 2, no trace
        /// file and the declared message after "refused &lt;map path&gt;: ". The run leg sits inside each rule's own label,
        /// never beside it, so a mutant removing one reader check still fails exactly one label. The shadowed-definition
        /// leg (P1-E) and control 17's action-param and behaviour-bind legs run on their own copies of the document.
        /// </summary>
        private static void CheckParityMapSound(Action<bool, string> check, string root)
        {
            try
            {
                Dictionary<string, string> poses = ParPoses("Par", true);
                // Every map here is read against ParDocument(true, sounds: true), whose Sound definitions the complete
                // rows below bind one to one (commit 4b, P1-D and P1-E). Each shape rule's map is those complete rows with
                // one row broken (swap), so it breaks exactly that rule (C13) and, with the rule's check removed, binds
                // or fails binding by what the broken row then says; only the two maps with no array at all have no rows.
                XmlData.RootNode document = ParDocument(true, true);
                string xml = Serialize(document, Color.Red, Color.Blue);
                Func<string, string, string[]> runMap = ParRunner(root);
                // The map/2 children entries of the Sound-binding references 33:1 to 33:3 (33:0's is ParMapJson's own).
                // 33:3 repeats 33:2's refKey, which P1-D allows: rows need not have distinct sourceRefKeys (critique B5).
                // 33:4 binds nothing and 33:5 binds only "sound", so neither has an entry, which Apply tolerates.
                const string childRef = "Par|conf/actions.xml|/Mascot[1]/ActionList[1]/Action[4]/ActionReference[";
                string firstChild = "\"33:0\":" + JsonLine.Quote(childRef + "1]");
                Func<string, string> rows = soundRows => ParMapJson("Par", true, Pairs(poses), soundRows: soundRows).Replace(firstChild,
                    firstChild + ",\"33:1\":" + JsonLine.Quote(childRef + "2]") + ",\"33:2\":" + JsonLine.Quote(childRef + "3]") + ",\"33:3\":" + JsonLine.Quote(childRef + "3]"));
                // The row a broken shape-rule map breaks: 0 is the own literal, 1 the behaviour's evaluated bind, 2 the
                // first child's literal bind (the complete rows' order, below).
                const string at = "parity map soundParameters row 0 ", at1 = "parity map soundParameters row 1 ", at2 = "parity map soundParameters row 2 ";
                // Null when the reader and a run both give exactly the declared outcome, else what each gave. A declared
                // message is a refusal (the reader's InvalidDataException message; the run's exit 2, no trace and that
                // reason); a declared null is an acceptance (the reader accepts; the run completes with a trace).
                Func<string, XmlData.RootNode, string, string, string> judgeOn = (json, doc, docXml, expected) =>
                {
                    string reader = ParApply(json, doc);
                    string[] run = runMap(docXml, json);
                    bool runAgrees = expected == null
                        ? run[0] == "0" && run[1] != null
                        : run[0] == "2" && run[1] == null && run[2].Contains("refused " + run[3] + ": " + expected + Environment.NewLine);
                    if (reader == expected && runAgrees) return null;
                    return "reader " + (reader ?? "accepted") + "; run exit " + run[0] + (run[1] == null ? ", no trace: " : ", a trace: ") + OneLine(run[2]);
                };
                Func<string, string, string> judge = (json, expected) => judgeOn(json, document, xml, expected);
                Action<string, string, string> exact = (label, json, expected) =>
                {
                    string fault = judge(json, expected);
                    check(fault == null, label + " (" + (fault ?? "reader and run: " + expected) + ")");
                };

                // The WITNESS: a row for every Sound definition of the document, of every kind and mode: an own literal
                // normalized in emission (/TO emitted as "to"), a behaviour bind, child binds, evaluated action- and
                // frame-cached definitions, an untrimmed " ${x}" that is a constant (P1-C rule 0 reads the untrimmed text),
                // three definitions that all emit "" (control 5), and two rows sharing one sourceRefKey (33:2 and 33:3).
                string frameRow = "{\"kind\":\"action\",\"nodeId\":31,\"sourceRefKey\":\"Par|conf/actions.xml|/Mascot[1]/ActionList[1]/Action[2]\","
                    + "\"emittedValue\":\"'b'\",\"emittedCache\":\"frame\",\"mode\":\"evaluated\"}";
                string booleanRow = "{\"kind\":\"action\",\"nodeId\":32,\"sourceRefKey\":\"Par|conf/actions.xml|/Mascot[1]/ActionList[1]/Action[3]\","
                    + "\"emittedValue\":\"true\",\"emittedCache\":\"none\",\"mode\":\"literal\",\"sourceValue\":\"true\",\"sourceKind\":\"boolean\"}";
                string constantRow = "{\"kind\":\"action\",\"nodeId\":33,\"sourceRefKey\":\"Par|conf/actions.xml|/Mascot[1]/ActionList[1]/Action[4]\","
                    + "\"emittedValue\":\" ${x}\",\"emittedCache\":\"none\",\"mode\":\"literal\",\"sourceValue\":\" ${x}\",\"sourceKind\":\"string\"}";
                Func<int, int, string, string> emptyChild = (index, reference, source) => "{\"kind\":\"child\",\"nodeId\":33,\"childIndex\":" + index
                    + ",\"sourceRefKey\":" + JsonLine.Quote(childRef + reference + "]") + ",\"emittedValue\":\"\",\"emittedCache\":\"none\",\"mode\":\"literal\","
                    + "\"sourceValue\":" + JsonLine.Quote(source) + ",\"sourceKind\":\"string\"}";
                var complete = new List<string> { ParSoundLiteral, ParSoundEvaluated, ParSoundChild, frameRow, booleanRow, constantRow,
                    emptyChild(1, 2, "/"), emptyChild(2, 3, @"\"), emptyChild(3, 3, "") };
                // The complete rows with row <index> replaced.
                Func<int, string, string> swap = (index, replacement) =>
                {
                    var changed = new List<string>(complete);
                    changed[index] = replacement;
                    return rows("[" + string.Join(",", changed) + "]");
                };
                string witnessJson = rows("[" + string.Join(",", complete) + "]");
                ParityMap witness = ParityMap.Parse(witnessJson);
                TickType witnessType = TickType.Compile("Par", document);
                string witnessFault = null;
                try { witness.Apply(witnessType, document); }
                catch (Exception e) { witnessFault = e.GetType().Name + ": " + e.Message; }
                var seen = new List<string>();
                foreach (ParityMap.SoundRow r in witness.SoundRows)
                    seen.Add(r.Kind + " " + r.NodeId + (r.ChildIndex >= 0 ? ":" + r.ChildIndex : "") + " " + r.Mode + " " + r.EmittedCache
                        + " [" + r.EmittedValue + "]" + (r.SourceValue == null ? "" : " [" + r.SourceValue + "] " + r.SourceKind));
                string witnessRows = string.Join("; ", seen);
                // The run leg's own WITNESS: the same map beside the same document runs to a complete trace, so a refusal
                // below is that map's rule, not the run setup's.
                string witnessRun = witnessFault == null ? judge(witnessJson, null) : null;
                check(witnessFault == null && witnessRun == null && witnessRows == "action 30 literal none [to] [/TO] string; behaviour 10 evaluated action ['a' + 1]; "
                      + "child 33:0 literal none [1] [ 1] number; action 31 evaluated frame ['b']; action 32 literal none [true] [true] boolean; "
                      + "action 33 literal none [ ${x}] [ ${x}] string; child 33:1 literal none [] [/] string; child 33:2 literal none [] [\\] string; "
                      + "child 33:3 literal none [] [] string",
                    "parity map/3: WITNESS a complete map/3 with rows of every kind is accepted (" + (witnessFault ?? witnessRun ?? witnessRows) + ")");

                // Control 5: /, a backslash and the empty string all emit "" and compile to equal String constants, yet each
                // definition OBJECT finds its own row and authored text. Looked up whether or not the WITNESS refused, so a
                // mutant that breaks only the lookup fails only this label.
                var own = new List<string>();
                TickActDef seq = witnessType.ActionsById[33];
                for (int c = 1; c <= 3; c++)
                {
                    ParityMap.SoundRow found;
                    own.Add("child 33:" + c + (witness.TryGetSoundRow(seq.Children[c].Binds["Sound"], out found) ? " [" + found.SourceValue + "] row " + found.Row : " no row"));
                }
                string ownRows = string.Join("; ", own);
                check(ownRows == "child 33:1 [/] row 6; child 33:2 [\\] row 7; child 33:3 [] row 8",
                    "parity map/3: each Sound definition finds its own row, three defs emitting the empty string (" + ownRows + ")");

                exact("parity map/3: soundParameters absent is refused", rows(null), "parity map has no soundParameters (map/3 requires the array)");
                exact("parity map/3: soundParameters that is not an array is refused", rows("{}"), "parity map soundParameters is not an array");
                exact("parity map/3: a Sound row that is not an object is refused", swap(0, "\"x\""), at + "is not an object");
                exact("parity map/3: a Sound row repeating a property is refused",
                    swap(0, ParSoundLiteral.Replace("\"mode\":\"literal\"", "\"mode\":\"literal\",\"mode\":\"literal\"")), at + "repeats its property 'mode'");
                exact("parity map/3: a Sound row with an unknown property is refused",
                    swap(0, ParSoundLiteral.Replace("\"mode\":\"literal\"", "\"mode\":\"literal\",\"clip\":\"to\"")),
                    at + "has the property 'clip'; a Sound row holds only kind, nodeId, childIndex, sourceRefKey, emittedValue, emittedCache, mode, sourceValue and sourceKind");
                exact("parity map/3: a Sound row missing a property is refused",
                    swap(0, ParSoundLiteral.Replace("\"sourceRefKey\":\"Par|conf/actions.xml|/Mascot[1]/ActionList[1]/Action[1]\",", "")), at + "lacks sourceRefKey");
                exact("parity map/3: a childIndex on a row that is not a child is refused",
                    swap(0, ParSoundLiteral.Replace("\"nodeId\":30,", "\"nodeId\":30,\"childIndex\":0,")), at + "is kind action and has a childIndex (only a child row has one)");
                exact("parity map/3: a child row with no childIndex is refused",
                    swap(2, ParSoundChild.Replace("\"childIndex\":0,", "")), at2 + "is kind child and has no childIndex");

                // Two-part rules: every part is refused under one label, so removing the rule fails one label.
                var parts = new List<string>();
                Action<string, string> part = (json, expected) =>
                {
                    string fault = judge(json, expected);
                    if (fault != null) parts.Add(fault);
                };
                part(swap(1, ParSoundEvaluated.Replace("\"mode\":\"evaluated\"", "\"mode\":\"evaluated\",\"sourceValue\":\"x\"")), at1 + "is evaluated and has sourceValue (only a literal row has one)");
                part(swap(1, ParSoundEvaluated.Replace("\"mode\":\"evaluated\"", "\"mode\":\"evaluated\",\"sourceKind\":\"string\"")), at1 + "is evaluated and has sourceKind (only a literal row has one)");
                check(parts.Count == 0, "parity map/3: sourceValue or sourceKind on an evaluated row is refused (" + string.Join("; ", parts) + ")");
                parts.Clear();
                part(swap(0, ParSoundLiteral.Replace(",\"sourceValue\":\"/TO\"", "")), at + "is literal and lacks sourceValue");
                part(swap(0, ParSoundLiteral.Replace(",\"sourceKind\":\"string\"", "")), at + "is literal and lacks sourceKind");
                check(parts.Count == 0, "parity map/3: a literal row lacking sourceValue or sourceKind is refused (" + string.Join("; ", parts) + ")");

                exact("parity map/3: a Sound row field that is not a string is refused",
                    swap(0, ParSoundLiteral.Replace("\"emittedValue\":\"to\"", "\"emittedValue\":5")), at + "has emittedValue that is not a string");
                parts.Clear();
                foreach (string token in new[] { "-0", "30.0", "3e1", "2147483648", "\"30\"" })
                    part(swap(0, ParSoundLiteral.Replace("\"nodeId\":30", "\"nodeId\":" + token)), at + "has nodeId " + token + ", not a canonical nonnegative Int32");
                check(parts.Count == 0, "parity map/3: a nodeId that is not a canonical nonnegative Int32 is refused (-0, 30.0, 3e1, 2147483648, a string) ("
                    + string.Join("; ", parts) + ")");
                parts.Clear();
                foreach (string token in new[] { "-0", "0.0", "2147483648", "\"0\"" })
                    part(swap(2, ParSoundChild.Replace("\"childIndex\":0", "\"childIndex\":" + token)), at2 + "has childIndex " + token + ", not a canonical nonnegative Int32");
                check(parts.Count == 0, "parity map/3: a childIndex that is not a canonical nonnegative Int32 is refused (-0, 0.0, 2147483648, a string) ("
                    + string.Join("; ", parts) + ")");

                exact("parity map/3: a kind outside action, behaviour and child is refused",
                    swap(0, ParSoundLiteral.Replace("\"kind\":\"action\"", "\"kind\":\"Action\"")), at + "has kind 'Action', not action, behaviour or child");
                exact("parity map/3: a mode outside literal and evaluated is refused",
                    swap(0, ParSoundLiteral.Replace("\"mode\":\"literal\"", "\"mode\":\"Literal\"")), at + "has mode 'Literal', not literal or evaluated");
                exact("parity map/3: an emittedCache outside none, action and frame is refused",
                    swap(1, ParSoundEvaluated.Replace("\"emittedCache\":\"action\"", "\"emittedCache\":\"Action\"")), at1 + "has emittedCache 'Action', not none, action or frame");
                exact("parity map/3: a sourceKind outside string, number and boolean is refused",
                    swap(0, ParSoundLiteral.Replace("\"sourceKind\":\"string\"", "\"sourceKind\":\"String\"")), at + "has sourceKind 'String', not string, number or boolean");
                exact("parity map/3: an evaluated row with emittedCache none is refused",
                    swap(1, ParSoundEvaluated.Replace("\"emittedCache\":\"action\"", "\"emittedCache\":\"none\"")), at1 + "is evaluated with emittedCache 'none' (an evaluated row has action or frame)");

                parts.Clear();
                foreach (string script in new[] { "${x}", "#{x}" })
                    part(swap(0, ParSoundLiteral.Replace("\"sourceValue\":\"/TO\"", "\"sourceValue\":\"" + script + "\"")), at + "has a literal sourceValue that is a source script, a source-kind disagreement");
                check(parts.Count == 0, "parity map/3: a literal sourceValue that is a source script is refused, on the untrimmed text (" + string.Join("; ", parts) + ")");

                // Control 1: U+00A0 then 1 is a string by Java trim, a number by .NET's; a row calling it a number is a
                // disagreement, and the same row calling it a string is accepted. The classification itself, beside it.
                string nbsp = ((char)0xA0).ToString();
                parts.Clear();
                part(swap(0, ParSoundLiteral.Replace("\"sourceValue\":\"/TO\",\"sourceKind\":\"string\"", "\"sourceValue\":\"" + nbsp + "1\",\"sourceKind\":\"number\"")),
                    at + "has sourceKind 'number', but its sourceValue classifies as string (source-kind disagreement)");
                part(swap(0, ParSoundLiteral.Replace("\"sourceValue\":\"/TO\",\"sourceKind\":\"string\"", "\"sourceValue\":\"1\",\"sourceKind\":\"string\"")),
                    at + "has sourceKind 'string', but its sourceValue classifies as number (source-kind disagreement)");
                part(swap(0, ParSoundLiteral.Replace("\"sourceValue\":\"/TO\",\"sourceKind\":\"string\"", "\"sourceValue\":\"" + nbsp + "1\",\"sourceKind\":\"string\"")), null);
                var corpus = new[]
                {
                    "true", "boolean", "false", "boolean", "TRUE", "string", "False", "string", " true", "string", "1", "number", " 1", "number",
                    ((char)9) + "1", "number", ((char)0) + "1" + ((char)31), "number", nbsp + "1", "string", ((char)0x85) + "1", "string",
                    ((char)0x3000) + "1", "string", "1" + nbsp, "string", "", "string", " ", "string", "null", "string", "/TO", "string",
                    "Infinity", "number", "-Infinity", "number", "NaN", "number", "+NaN", "number", "NaNd", "string", "Infinityf", "string",
                    "0x1p1", "number", "/0X1P1", "string", "1.", "number", ".5", "number", "1e", "string", "1_0", "string",
                };
                for (int i = 0; i < corpus.Length; i += 2)
                {
                    string kind = ParityMap.SourceKind(corpus[i]);
                    if (kind != corpus[i + 1]) parts.Add("SourceKind of entry " + (i / 2) + " is " + (kind ?? "null") + ", not " + corpus[i + 1]);
                }
                check(parts.Count == 0, "parity map/3: a sourceKind disagreement is refused, by Java trim and exact Boolean text (control 1) ("
                    + string.Join("; ", parts) + ")");

                exact("parity map/3: an unknown top-level property is refused", "{\"extra\":1," + witnessJson.Substring(1),
                    "parity map has the top-level property 'extra'; map/3 allows only schema, imageSet, frames, actions, children, behaviours, sourceNames, poses and soundParameters");

                // Commit 4b, the binding and the coverage (P1-D, P1-E): each map is the complete rows with exactly one rule
                // broken, so its declared message names the row and location that break it.
                string extra = "parity map soundParameters row " + complete.Count + " ";
                exact("parity map/3: a repeated Sound row location is refused", rows("[" + string.Join(",", complete) + "," + ParSoundLiteral + "]"),
                    extra + "repeats the location action 30 (one row per Sound definition)");
                exact("parity map/3: a surplus Sound row naming no compiled container is refused",
                    rows("[" + string.Join(",", complete) + "," + ParSoundLiteral.Replace("\"nodeId\":30", "\"nodeId\":99") + "]"),
                    extra + "names action 99, which is no compiled action (surplus)");
                exact("parity map/3: a surplus Sound row whose container defines no Sound is refused",
                    rows("[" + string.Join(",", complete) + "," + emptyChild(4, 9, "/") + "]"), extra + "names child 33:4, which defines no Sound (surplus)");
                var lessOne = new List<string>(complete);
                lessOne.RemoveAt(4);
                exact("parity map/3: a compiled Sound definition no row binds is refused (missing)", rows("[" + string.Join(",", lessOne) + "]"),
                    "parity map has no soundParameters row for the Sound definition at action 32 (missing)");
                exact("parity map/3: an emittedValue that is not the XML value is refused", swap(0, ParSoundLiteral.Replace("\"emittedValue\":\"to\"", "\"emittedValue\":\"TO\"")),
                    "parity map soundParameters row 0 has emittedValue 'TO', not the XML value 'to' of the Sound definition at action 30");
                exact("parity map/3: an emittedCache that is not the XML cache is refused", swap(3, frameRow.Replace("\"emittedCache\":\"frame\"", "\"emittedCache\":\"action\"")),
                    "parity map soundParameters row 3 has emittedCache 'action', not the XML cache 'frame' of the Sound definition at action 31");
                XmlData.RootNode noCache = ParDocument(true, true);
                foreach (XmlData.AnimationNode a in noCache.Animations.Animation)
                    if (a.Id == 30)
                        foreach (XmlData.ParamNode p in a.Act.Params)
                            if (p.Name == "Sound") p.Cache = null;
                string noCacheFault = judgeOn(witnessJson, noCache, Serialize(noCache, Color.Red, Color.Blue),
                    "parity map soundParameters row 0 binds action 30, whose Sound definition has no cache attribute (map/3 requires one)");
                check(noCacheFault == null, "parity map/3: a Sound definition with no cache attribute is refused (" + (noCacheFault ?? "reader and run") + ")");
                exact("parity map/3: a mode the compiled definition's cache contradicts is refused",
                    swap(4, "{\"kind\":\"action\",\"nodeId\":32,\"sourceRefKey\":\"Par|conf/actions.xml|/Mascot[1]/ActionList[1]/Action[3]\","
                        + "\"emittedValue\":\"true\",\"emittedCache\":\"action\",\"mode\":\"evaluated\"}"),
                    "parity map soundParameters row 4 is evaluated, but the Sound definition at action 32 compiles with cache none");
                exact("parity map/3: a sourceRefKey that is not its location's map entry is refused", swap(0, ParSoundLiteral.Replace("Action[1]\"", "Action[9]\"")),
                    "parity map soundParameters row 0 has sourceRefKey 'Par|conf/actions.xml|/Mascot[1]/ActionList[1]/Action[9]', not the map's actions entry "
                    + "'Par|conf/actions.xml|/Mascot[1]/ActionList[1]/Action[1]'");
                exact("parity map/3: a sourceRefKey outside the map's image set is refused", witnessJson.Replace("\"Par|conf/behaviors.xml|", "\"Other|conf/behaviors.xml|"),
                    "parity map soundParameters row 1 has a sourceRefKey in image set 'Other', not 'Par'");
                exact("parity map/3: a Sound-binding child with no children entry is refused (control 9)", witnessJson.Replace(firstChild + ",", ""),
                    "parity map soundParameters row 2 binds child 33:0, which has no children entry in the map (a Sound definition's location needs one)");

                // P1-E: the inventory is by node id, so a definition a name lookup shadows is still bound. The validator
                // requires an off-route behaviour to carry a root behaviour's name, and the engine's by-name entries go
                // through RootByName, which holds only the route's targets (the first of a name wins); so behaviour 11,
                // reached by an edge from 10 and named Walk like it, is shadowed by name and found only by id. A Mute read
                // inside it selects its own bind, and that definition must find its own row. Its own document, so a walk
                // that inventories behaviours by name fails this label alone.
                XmlData.RootNode shadowed = ParDocument(true, true);
                var shadowNodes = new List<XmlData.AnimationNode>(shadowed.Animations.Animation);
                XmlData.AnimationNode rootWalk = shadowNodes.Find(a => a.Id == 10);
                rootWalk.Sequence.Next = new[] { new XmlData.NextNode { Value = 11, Probability = 1 } };
                shadowNodes.Insert(shadowNodes.IndexOf(rootWalk) + 1, new XmlData.AnimationNode
                {
                    Id = 11, Name = "beh:Walk:next", Start = rootWalk.Start, End = rootWalk.End,
                    Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
                    Behaviour = new XmlData.BehaviourNode
                    {
                        Name = "Walk", Runs = "Walk", Frequency = 1,
                        Bind = new[] { new XmlData.BindNode { Name = "Sound", Value = "next", Cache = "none" } },
                    },
                });
                shadowed.Animations.Animation = shadowNodes.ToArray();
                string rootEntry = "\"10\":" + JsonLine.Quote("Par|conf/behaviors.xml|/Mascot[1]/BehaviorList[1]/Behavior[1]");
                string nextRef = "Par|conf/behaviors.xml|/Mascot[1]/BehaviorList[1]/Behavior[1]/NextBehaviorList[1]/Behavior[1]";
                string offRouteRow = "{\"kind\":\"behaviour\",\"nodeId\":11,\"sourceRefKey\":" + JsonLine.Quote(nextRef)
                    + ",\"emittedValue\":\"next\",\"emittedCache\":\"none\",\"mode\":\"literal\",\"sourceValue\":\"/Next\",\"sourceKind\":\"string\"}";
                string shadowJson = rows("[" + string.Join(",", complete) + "," + offRouteRow + "]").Replace(rootEntry, rootEntry + ",\"11\":" + JsonLine.Quote(nextRef));
                string shadowFault = judgeOn(shadowJson, shadowed, Serialize(shadowed, Color.Red, Color.Blue), null);
                TickType shadowType = TickType.Compile("Par", shadowed);
                string shadowRows;
                try
                {
                    ParityMap shadowMap = ParityMap.Parse(shadowJson);
                    shadowMap.Apply(shadowType, shadowed);
                    ParityMap.SoundRow r10, r11;
                    shadowRows = (shadowMap.TryGetSoundRow(shadowType.BehavioursById[10].Binds["Sound"], out r10) ? "behaviour 10 row " + r10.Row : "behaviour 10 no row")
                        + "; " + (shadowMap.TryGetSoundRow(shadowType.BehavioursById[11].Binds["Sound"], out r11) ? "behaviour 11 row " + r11.Row + " [" + r11.SourceValue + "]" : "behaviour 11 no row")
                        + (ReferenceEquals(shadowType.RootByName["Walk"], shadowType.BehavioursById[10]) ? "" : "; Walk by name is not behaviour 10");
                }
                catch (Exception e) { shadowRows = e.GetType().Name + ": " + e.Message; }
                check(shadowFault == null && shadowRows == "behaviour 10 row 1; behaviour 11 row 9 [/Next]",
                    "parity map/3: an off-route behaviour named like a root behaviour binds its own Sound row (shadowed by name lookup) ("
                    + (shadowFault ?? shadowRows) + ")");

                // Control 17, host half, at the other two name filters (33:5's child bind is the WITNESS's): an action param
                // and a behaviour bind named "sound", each after its container's Sound so a filter ignoring case would take
                // it in Sound's place, are no Sound definitions by the ordinal name, and the complete rows still bind the
                // document one to one. Its own document, so each filter ignoring case fails this label alone.
                XmlData.RootNode lowercase = ParDocument(true, true);
                foreach (XmlData.AnimationNode a in lowercase.Animations.Animation)
                {
                    if (a.Id == 32) a.Act.Params = new[] { a.Act.Params[0], new XmlData.ParamNode { Name = "sound", Value = "x", Cache = "none" } };
                    if (a.Id == 10) a.Behaviour.Bind = new[] { a.Behaviour.Bind[0], new XmlData.BindNode { Name = "sound", Value = "x", Cache = "none" } };
                }
                string lowercaseFault = judgeOn(witnessJson, lowercase, Serialize(lowercase, Color.Red, Color.Blue), null);
                check(lowercaseFault == null, "parity map/3: an action param and a behaviour bind named 'sound' are no Sound definitions (control 17, host half) ("
                    + (lowercaseFault ?? "reader and run accept") + ")");
            }
            catch (Exception e)
            {
                check(false, "parity map/3: the sound reader checks ran without an exception: " + e);
            }
        }

        // ---- burndown/host-mute-1 commit 5a: the trace writer's Mute dispositions (CONTRACT v1.4 P2-C, FC-2, FC-3, FC-6) ----

        private const string ParMuteAction = "Mu|conf/actions.xml|/Mascot[1]/ActionList[1]/Action[";

        /// <summary>The Mute fixture: Go (behaviour 10) runs the sequence Go (20) of Hush (30, an Embedded Mute holding
        /// <paramref name="own"/>) then Stand (31, Stay 50, one pose), and child 20:0, the reference to Hush, binds
        /// <paramref name="incoming"/> when given. Starting a companion in Go initialises Hush, which reads Sound once.</summary>
        private static XmlData.RootNode ParMuteDocument(XmlData.BindNode[] incoming, params XmlData.ParamNode[] own)
        {
            var still = new XmlData.MovingNode { X = "0", Y = "0", Interval = "40" };
            Func<int, string, XmlData.AnimationNode> node = (id, name) => new XmlData.AnimationNode
            {
                Id = id, Name = name, Start = still, End = still,
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
            };
            XmlData.AnimationNode route = node(1, "route");
            route.Route = new XmlData.RouteNode();
            route.Sequence.Next = new[] { new XmlData.NextNode { Value = 10, Probability = 1 } };
            XmlData.AnimationNode go = node(10, "beh:Go");
            go.Behaviour = new XmlData.BehaviourNode { Name = "Go", Runs = "Go", Frequency = 1 };
            XmlData.AnimationNode sequence = node(20, "Go");
            sequence.Act = new XmlData.ActNode
            {
                Type = "Sequence",
                Children = new XmlData.ChildrenNode
                {
                    Mode = "sequence",
                    Child = new[] { new XmlData.CompositeChildNode { Animation = "Hush", Bind = incoming }, new XmlData.CompositeChildNode { Animation = "Stand" } },
                },
            };
            XmlData.AnimationNode hush = node(30, "Hush");
            hush.Act = new XmlData.ActNode { Type = "Embedded", Class = "Mute", Params = own.Length == 0 ? null : own };
            XmlData.AnimationNode stand = node(31, "Stand");
            stand.Act = new XmlData.ActNode
            {
                Type = "Stay", Border = "Floor",
                Params = new[] { new XmlData.ParamNode { Name = "Duration", Value = "50", Cache = "none" } },
                Variants = new XmlData.VariantsNode { Variant = new[] { new XmlData.VariantNode { Pose = new[] { new XmlData.PoseNode { Frame = 0, Ticks = 1 } } } } },
            };
            return new XmlData.RootNode
            {
                Header = new XmlData.HeaderNode { Title = "Mu", Features = "tick actions behaviours expressions", Clock = "tick" },
                Image = new XmlData.ImageNode
                {
                    TilesX = 2, TilesY = 1, Png = PngHeader(128, 64),
                    AnchorX = 32, AnchorXSpecified = true, AnchorY = 64, AnchorYSpecified = true,
                },
                Animations = new XmlData.AnimationsNode { Animation = new[] { route, go, sequence, hush, stand } },
            };
        }

        /// <summary>A map/3 for <see cref="ParMuteDocument"/>, <paramref name="soundRows"/> its raw soundParameters;
        /// <paramref name="moreActions"/> and <paramref name="moreChildren"/> are further raw entries (each starting with a
        /// comma) for a document that adds actions or references.</summary>
        private static string ParMuteMap(string soundRows, string moreActions = "", string moreChildren = "")
        {
            return "{\"schema\":" + JsonLine.Quote(ParityMap.Schema) + ",\"imageSet\":\"Mu\","
                + "\"frames\":[{\"frame\":0,\"image\":\"img/Mu/a.png\"},{\"frame\":1,\"image\":\"img/Mu/b.png\"}],"
                + "\"actions\":{\"20\":" + JsonLine.Quote(ParMuteAction + "1]") + ",\"30\":" + JsonLine.Quote(ParMuteAction + "2]") + ",\"31\":" + JsonLine.Quote(ParMuteAction + "3]") + moreActions + "},"
                + "\"children\":{\"20:0\":" + JsonLine.Quote(ParMuteAction + "1]/ActionReference[1]") + ",\"20:1\":" + JsonLine.Quote(ParMuteAction + "1]/ActionReference[2]") + moreChildren + "},"
                + "\"behaviours\":{\"10\":" + JsonLine.Quote("Mu|conf/behaviors.xml|/Mascot[1]/BehaviorList[1]/Behavior[1]") + "},"
                + "\"sourceNames\":{},\"poses\":{\"31:0:0\":" + ParPoseValue("Mu", 3, 1, 1) + "},\"soundParameters\":" + soundRows + "}";
        }

        /// <summary>A soundParameters row for Hush's own Sound (action 30) or, with <paramref name="child"/>, child 20:0's
        /// bind: literal (cache none) when <paramref name="source"/> is given, else evaluated with <paramref name="cache"/>.</summary>
        private static string ParMuteRow(string emitted, string source, string sourceKind, string cache = "none", bool child = false)
        {
            return "{\"kind\":" + (child ? "\"child\",\"nodeId\":20,\"childIndex\":0" : "\"action\",\"nodeId\":30") + ",\"sourceRefKey\":"
                + JsonLine.Quote(ParMuteAction + (child ? "1]/ActionReference[1]" : "2]")) + ",\"emittedValue\":" + JsonLine.Quote(emitted)
                + ",\"emittedCache\":" + JsonLine.Quote(source == null ? cache : "none") + ",\"mode\":"
                + (source == null ? "\"evaluated\"" : "\"literal\",\"sourceValue\":" + JsonLine.Quote(source) + ",\"sourceKind\":" + JsonLine.Quote(sourceKind)) + "}";
        }

        /// <summary>A literal String row for a Sound bind on reference 20:<paramref name="index"/> or, with
        /// <paramref name="behaviour"/>, on behaviour 10.</summary>
        private static string ParMuteBindRow(int index, string emitted, string source, bool behaviour = false)
        {
            return "{\"kind\":" + (behaviour ? "\"behaviour\",\"nodeId\":10" : "\"child\",\"nodeId\":20,\"childIndex\":" + index) + ",\"sourceRefKey\":"
                + JsonLine.Quote(behaviour ? "Mu|conf/behaviors.xml|/Mascot[1]/BehaviorList[1]/Behavior[1]" : ParMuteAction + "1]/ActionReference[" + (index + 1) + "]")
                + ",\"emittedValue\":" + JsonLine.Quote(emitted) + ",\"emittedCache\":\"none\",\"mode\":\"literal\",\"sourceValue\":" + JsonLine.Quote(source)
                + ",\"sourceKind\":\"string\"}";
        }

        /// <summary>A scenario desktop with the global Sounds setting on or off; <paramref name="wrap"/>, when given, wraps
        /// its environment (the engine-Read leg's throwing decorator).</summary>
        private static TickWorld ParMuteWorld(ITickObserver observer, bool soundsOn, ITickRandom random, Func<ITickEnvironment, ITickEnvironment> wrap = null)
        {
            using (JsonDocument doc = JsonDocument.Parse("{\"screens\":[{\"x\":0,\"y\":0,\"w\":1280,\"h\":720}],\"workArea\":{\"x\":0,\"y\":0,\"w\":1280,\"h\":680},"
                + "\"rng\":{\"kind\":\"none\"},\"settings\":{\"Sounds\":" + (soundsOn ? "true" : "false") + "}}"))
            {
                JsonElement s = doc.RootElement;
                ITickEnvironment environment = new ParityEnvironment(s);
                if (wrap != null) environment = wrap(environment);
                return new TickWorld(environment, random ?? new ParityRandom(s.GetProperty("rng")), new ParitySettings(s), observer);
            }
        }

        /// <summary>An ITickRandom that counts every draw (control 7).</summary>
        private sealed class ParCountingRandom : ITickRandom
        {
            public int Draws;
            public double Next() { Draws++; return 0.5; }
        }

        /// <summary>C18's engine-Read leg: an environment whose cursor read throws while <see cref="Armed"/>, so a Sound that
        /// reads the cursor throws from inside the engine's own Read (SafeScript's Evaluate catches only ScriptFault). Every
        /// other member is the wrapped environment's.</summary>
        private sealed class ParThrowingEnvironment : ITickEnvironment
        {
            private readonly ITickEnvironment _inner;
            public bool Armed;
            public int CursorReads;
            public ParThrowingEnvironment(ITickEnvironment inner) { _inner = inner; }
            public void Tick(int tick) { _inner.Tick(tick); }
            public TickArea Screen { get { return _inner.Screen; } }
            public TickArea WorkArea { get { return _inner.WorkArea; } }
            public IReadOnlyList<TickArea> Screens { get { return _inner.Screens; } }
            public TickArea ActiveWindow { get { return _inner.ActiveWindow; } }
            public TickPoint Cursor
            {
                get
                {
                    CursorReads++;
                    if (Armed) throw new InvalidOperationException("c18 cursor");
                    return _inner.Cursor;
                }
            }
            public TickPoint CursorDelta { get { return _inner.CursorDelta; } }
            public IReadOnlyList<TickInput> Inputs { get { return _inner.Inputs; } }
            public bool MoveActiveWindow(TickPoint topLeft, out TickRect from, out TickRect to) { return _inner.MoveActiveWindow(topLeft, out from, out to); }
            public bool MinimizeActiveWindow() { return _inner.MinimizeActiveWindow(); }
        }

        /// <summary>One <see cref="ParMuteRun"/>, for a leg's hooks: the world, the compiled Mu, m1, the writer and the
        /// applied map (null for none).</summary>
        private sealed class ParMute
        {
            public TickWorld World;
            public TickType Type;
            public TickMascot Mascot;
            public ParityTraceWriter Writer;
            public ParityMap Map;
            public bool TraceOff;

            /// <summary>More Mute reads on m1 at <paramref name="tick"/>: Hush built for <paramref name="owner"/> (Mu when
            /// null), or with no owner type at all, a local Sound put first when <paramref name="local"/> has a value, then
            /// initialised <paramref name="times"/> times (each Init reads Sound once).</summary>
            public void Again(int tick, ScriptValue? local = null, TickType owner = null, bool noOwner = false, int times = 1)
            {
                Writer.Tick = tick;
                TickAction hush = noOwner ? TickInstantFamily.TryCreate(World, Type, Type.ActionsById[30], null) : World.BuildAction(owner ?? Type, "Hush", null);
                if (local.HasValue) hush.Vars.Put("Sound", local.Value);
                for (int i = 0; i < times; i++) hush.Init(Mascot);
            }

            /// <summary>A pose provenance fault at <paramref name="tick"/>: m1 shows a pose of Old, a member the writer has no
            /// map for, and the writer forms the tick's states (a trace-off writer forms them once, at the end).</summary>
            public void PoseFault(int tick)
            {
                TickType old = TickType.Compile("Old", ParDocument(false));
                Writer.Tick = tick;
                Mascot.ApplyPose(old.ActionsById[30].Variants[0].Poses[0], old);
                Writer.States(World);
                if (TraceOff) Writer.FinalStates(World);
            }
        }

        /// <summary>
        /// One Mute leg through the engine and this lane's writer: compiles <paramref name="document"/> as Mu, applies
        /// ParMuteMap(<paramref name="soundRows"/>) to it (null: the writer has no map for Mu; with
        /// <paramref name="foreignDocument"/> the map is that document's, applied to a compile of it, so it binds no
        /// definition the engine reads), runs <paramref name="before"/>, starts m1 in Go (one Mute read), runs
        /// <paramref name="after"/>, and ends through ParityRun.Finish. Gives the trace, the log and the exit code.
        /// <paramref name="map"/>, when given, is the whole map text in place of ParMuteMap(<paramref name="soundRows"/>).
        /// </summary>
        private static string[] ParMuteRun(XmlData.RootNode document, string soundRows, bool soundsOn, Action<ParMute> after = null, bool traceOff = false,
            ITickRandom random = null, ParityRandom finishRandom = null, XmlData.RootNode foreignDocument = null, Action<ParMute> before = null,
            Func<ITickEnvironment, ITickEnvironment> environment = null, string map = null)
        {
            var mute = new ParMute { Type = TickType.Compile("Mu", document), TraceOff = traceOff };
            var maps = new Dictionary<string, ParityMap>(StringComparer.Ordinal);
            if (soundRows != null || map != null)
            {
                mute.Map = ParityMap.Parse(map ?? ParMuteMap(soundRows));
                if (foreignDocument != null) mute.Map.Apply(TickType.Compile("Mu", foreignDocument), foreignDocument);
                else mute.Map.Apply(mute.Type, document);
                maps["Mu"] = mute.Map;
            }
            var trace = new StringWriter { NewLine = "\n" };
            mute.Writer = new ParityTraceWriter(trace, maps, traceOff);
            mute.World = ParMuteWorld(mute.Writer, soundsOn, random, environment);
            mute.World.AddType(mute.Type);
            mute.World.Start();
            if (before != null) before(mute);
            mute.Mascot = mute.World.CreateAtStart("m1", mute.Type, new TickPoint(640, 680), false, "Go");
            if (after != null) after(mute);
            var log = new StringWriter { NewLine = "\n" };
            int exit = ParityRun.Finish(mute.Writer, 1, "complete", finishRandom, log);
            return new[] { trace.ToString(), log.ToString(), exit.ToString(CultureInfo.InvariantCulture) };
        }

        /// <summary>
        /// burndown/host-mute-1 commit 5a, the host half of N-bd-codex-converter-1-02: the trace writer's Mute dispositions
        /// (CONTRACT v1.4 revision 3: P2-C, FC-2 steps 1-3, FC-3 and FC-6). Every leg drives a real Mute read through the
        /// engine into a ParityTraceWriter (<see cref="ParMuteRun"/>) and ends through ParityRun.Finish, so the read record,
        /// the activation and the winning definition object are the engine's own. Each leg's outcome (exit code, every mute
        /// line's clip as written, every log line) is declared here, never derived from the run. Never calls
        /// ParityTraceWriter.Header (critique D2). Commit 5b adds C18's observation-exception probes at every FC-2 site and
        /// the arming witness. Round 5: each label's legs run in that label's own try (<c>group</c>), so a leg that throws
        /// (a mutant's refusal or exception) fails only its own label and never hides a later one; three labels also hold a
        /// parity run through ParityRun.Run (<see cref="ParRunner"/>, the go pattern), which reads the generated map file
        /// through LoadPets and writes a real trace file and footer.
        /// </summary>
        private static void CheckParityMuteTrace(Action<bool, string> check, string root)
        {
            // Read before anything here sets the probe: no product code does (C18 arming, below).
            bool probeInert = TickParamRead.ObservationProbe == null;
            try
            {
                Func<string, string, XmlData.ParamNode> sound = (value, cache) => new XmlData.ParamNode { Name = "Sound", Value = value, Cache = cache };
                Func<string, string, XmlData.RootNode> own = (value, cache) => ParMuteDocument(null, sound(value, cache));
                Func<string, string, XmlData.BindNode> bind = (name, value) => new XmlData.BindNode { Name = name, Value = value, Cache = "none" };
                Func<string[], string> outcome = run =>
                {
                    var clips = new List<string>();
                    foreach (string line in Lines(run[0], "\"kind\":\"mute\""))
                        clips.Add(line.Substring(line.IndexOf("\"clip\":", StringComparison.Ordinal) + 7).TrimEnd('}'));
                    return "exit " + run[2] + ", mute [" + string.Join("|", clips) + "], log [" + OneLine(run[1]) + "]";
                };
                Func<string, string, string, string> declared = (exit, clips, logLines) => "exit " + exit + ", mute [" + clips + "], log [" + logLines + "]";
                // A parity run's outcome (ParRunner gives exit, trace or null, log, map path): the exit code, each mute line's
                // clip in the trace FILE, the footer's status, and the run log's MUTE_ lines (the rest of its log is the run's).
                Func<string[], string> runOutcome = run =>
                {
                    var clips = new List<string>();
                    foreach (string line in Lines(run[1], "\"kind\":\"mute\""))
                        clips.Add(line.Substring(line.IndexOf("\"clip\":", StringComparison.Ordinal) + 7).TrimEnd('}'));
                    string status = "no footer", footer = LastFooter(run[1]);
                    if (footer != null)
                        using (JsonDocument f = JsonDocument.Parse(footer)) status = f.RootElement.GetProperty("status").GetString();
                    var muteLog = new List<string>();
                    foreach (string line in run[2].Split('\n'))
                        if (line.Contains("MUTE_")) muteLog.Add(line.TrimEnd('\r'));
                    return "exit " + run[0] + ", mute [" + string.Join("|", clips) + "], status " + status + ", log [" + string.Join(" | ", muteLog) + "]";
                };
                Func<string, string, string, string, string> runDeclared = (exit, clips, status, logLines) =>
                    "exit " + exit + ", mute [" + clips + "], status " + status + ", log [" + logLines + "]";
                Func<string, string> engineLines = trace =>
                {
                    var kept = new List<string>();
                    foreach (string line in (trace ?? "").Split('\n'))
                        if (line.Length > 0 && !line.Contains("\"kind\":\"mute\"") && !line.Contains("\"type\":\"footer\"") && !line.Contains("\"type\":\"header\"")) kept.Add(line);
                    return string.Join("\n", kept);
                };
                Func<string, string, string[]> runMute = ParRunner(root, "Mu", "Go");
                var faults = new List<string>();
                Action<string, string, string> leg = (name, got, want) => { if (got != want) faults.Add(name + ": " + got + " (declared " + want + ")"); };
                Action<string, int> label = (text, legs) =>
                {
                    check(faults.Count == 0, text + " (" + (faults.Count == 0 ? legs + " legs as declared" : string.Join("; ", faults)) + ")");
                    faults.Clear();
                };
                // Round 5: one try per label. A leg that throws ends that label's legs, adds its exception to that label's
                // faults and fails that label alone; every later label still runs.
                Action<string, int, Action> group = (text, legs, body) =>
                {
                    try { body(); }
                    catch (Exception e) { faults.Add("threw " + e.GetType().Name + ": " + e.Message); }
                    label(text, legs);
                };
                const string info = " (information)";
                string literalRows = "[" + ParMuteRow("to", "to", "string") + "]";
                string nullRows = "[" + ParMuteRow("null", null, null, "action") + "]";
                string numberRows = "[" + ParMuteRow("1", null, null, "action") + "]";
                Func<int, string, string> missing = (tick, reason) => "parity trace: MUTE_PROVENANCE_MISSING tick " + tick + " m1: " + reason;

                // P2-C's qualified literal (controls 2, 5, 16, W1, W8; C1's first half): a literal row with sourceKind string
                // writes its authored text, whatever the engine compiled and displays: /1 and /0X1P1 compile as Numbers, /true,
                // TRUE and False as Booleans, and /, a backslash, one space and the empty string all emit "". No information.
                // The U+00A0 then 1 row needs the reader to call it a String (control 1), so a reader mutant that classifies it
                // as a number refuses this label's map too: inherent, see that case. The run leg reads the map file through
                // LoadPets and writes /TO into a real trace file with footer status complete.
                group("parity mute: a qualified String literal writes its source spelling, not the engine's text", 13, () =>
                {
                    string nbsp = ((char)0xA0).ToString();
                    string[] literals = { "to", "/TO", "1", "/1", "true", "/true", "0x1p1", "/0X1P1", "1", nbsp + "1", "true", "TRUE", "false", "False",
                        "null", "null", "", "", "", " ", "", "/", "", "\\" };
                    for (int i = 0; i < literals.Length; i += 2)
                        leg(JsonLine.Quote(literals[i + 1]), outcome(ParMuteRun(own(literals[i], "none"), "[" + ParMuteRow(literals[i], literals[i + 1], "string") + "]", true)),
                            declared("0", JsonLine.Quote(literals[i + 1]), ""));
                    leg("run /TO", runOutcome(runMute(Serialize(own("to", "none"), Color.Red, Color.Blue), ParMuteMap("[" + ParMuteRow("to", "/TO", "string") + "]"))),
                        runDeclared("0", "\"/TO\"", "complete", ""));
                });

                // The other literal rows (controls 1, 3, 4, W7): the engine's own text, and MUTE_VALUE_NOT_STRING by the row's
                // sourceKind, never by the compiled kind (lowered Infinity, -Infinity and NaN compile as Strings and still log
                // literal number; 0x1p1 writes the engine's 2; W7's own false is boolean and 01 is number 1). Status complete,
                // exit 0, no parity trace line.
                group("parity mute: a number or boolean literal writes the engine's text and logs MUTE_VALUE_NOT_STRING as information", 8, () =>
                {
                    string[] numbers = { "1", " 1", "number", "\"1\"", "infinity", "Infinity", "number", "\"infinity\"", "-infinity", "-Infinity", "number", "\"-infinity\"",
                        "nan", "NaN", "number", "\"nan\"", "0x1p1", "0x1p1", "number", "\"2\"", "true", "true", "boolean", "\"true\"",
                        "false", "false", "boolean", "\"false\"", "01", "01", "number", "\"1\"" };
                    for (int i = 0; i < numbers.Length; i += 4)
                        leg(numbers[i + 1], outcome(ParMuteRun(own(numbers[i], "none"), "[" + ParMuteRow(numbers[i], numbers[i + 1], numbers[i + 2]) + "]", true)),
                            declared("0", numbers[i + 3], "MUTE_VALUE_NOT_STRING tick 0 m1: literal " + numbers[i + 2] + info));
                });

                // Evaluated rows (control 10): a String is forwarded with no claim; a Number, a Boolean or a Point (mascot.anchor,
                // the start anchor 640, 680) writes the engine's text and logs MUTE_VALUE_NOT_STRING by the captured kind, which
                // display text cannot tell ('1' and 1 both display 1). Frame-cached rows behave the same. The run leg writes
                // an evaluated Boolean into a real trace file and logs its information line in the run's log, footer status
                // complete.
                group("parity mute: an evaluated String is forwarded and an evaluated Number or Boolean is not a String", 7, () =>
                {
                    string[] evaluated = { "'1'", "action", "\"1\"", "", "1", "action", "\"1\"", "evaluated number", "true", "action", "\"true\"", "evaluated boolean",
                        "'1'", "frame", "\"1\"", "", "1", "frame", "\"1\"", "evaluated number", "mascot.anchor", "action", "\"(640, 680)\"", "evaluated point" };
                    for (int i = 0; i < evaluated.Length; i += 4)
                        leg(evaluated[i] + " " + evaluated[i + 1], outcome(ParMuteRun(own(evaluated[i], evaluated[i + 1]), "[" + ParMuteRow(evaluated[i], null, null, evaluated[i + 1]) + "]", true)),
                            declared("0", evaluated[i + 2], evaluated[i + 3].Length == 0 ? "" : "MUTE_VALUE_NOT_STRING tick 0 m1: " + evaluated[i + 3] + info));
                    leg("run true action", runOutcome(runMute(Serialize(own("true", "action"), Color.Red, Color.Blue), ParMuteMap("[" + ParMuteRow("true", null, null, "action") + "]"))),
                        runDeclared("0", "\"true\"", "complete", "MUTE_VALUE_NOT_STRING tick 0 m1: evaluated boolean" + info));
                });

                // A reported fault is orthogonal (control 6 with its ${Sound} fallback and ${Foo}, Q2, Q1b): the engine's value and
                // branch are written as they are (null for a self-reference or an unbound name, x and 1 where a nested fault
                // still yields a value), the engine's error event comes first, MUTE_VALUE_FAULTED is logged once per read, and
                // the status stays complete. With Sounds off the null read writes no line and still logs it, once per read.
                // C6's first reference: the same ${Foo} with Foo bound to /a on the reference forwards /a, with no fault.
                group("parity mute: a faulted read writes the engine's value as passed, logs MUTE_VALUE_FAULTED and keeps status complete", 6, () =>
                {
                    string faulted = "MUTE_VALUE_FAULTED tick 0 m1" + info;
                    Func<string, bool, Action<ParMute>, string[]> faultRun = (value, on, after) => ParMuteRun(own(value, "action"), "[" + ParMuteRow(value, null, null, "action") + "]", on, after);
                    Func<string[], string> errorFirst = run =>
                    {
                        int errorAt = run[0].IndexOf("\"kind\":\"error\"", StringComparison.Ordinal), muteAt = run[0].IndexOf("\"kind\":\"mute\"", StringComparison.Ordinal);
                        return errorAt >= 0 && errorAt < muteAt ? "" : ", no error event before the mute";
                    };
                    string[] selfRead = faultRun("Sound", true, null);
                    leg("${Sound}", outcome(selfRead) + errorFirst(selfRead), declared("0", "null", faulted));
                    string[] unbound = faultRun("Foo", true, null);
                    leg("${Foo}, Foo unbound", outcome(unbound) + errorFirst(unbound), declared("0", "null", faulted));
                    leg("${Foo}, Foo=/a", outcome(ParMuteRun(ParMuteDocument(new[] { bind("Foo", "/a") }, sound("Foo", "action")), "[" + ParMuteRow("Foo", null, null, "action") + "]", true)),
                        declared("0", "\"/a\"", ""));
                    leg("Q2 x", outcome(faultRun("Sound == null ? 'x' : 'y'", true, null)), declared("0", "\"x\"", faulted));
                    leg("Q2 1", outcome(faultRun("Sound == null ? 1 : 2", true, null)), declared("0", "\"1\"", faulted + " | MUTE_VALUE_NOT_STRING tick 0 m1: evaluated number" + info));
                    leg("Q1b", outcome(faultRun("Sound", false, m => m.Again(0, times: 2))), declared("0", "", faulted + " | " + faulted + " | " + faulted));
                });

                // An absent Sound (B4; Q3's positive contrasts): the existing null branch, mute null with Sounds on and no line
                // with Sounds off, needing no owner map and claiming nothing, so no defect either way.
                group("parity mute: an absent Sound writes null with Sounds on and no line with Sounds off, with or without an owner map", 3, () =>
                {
                    leg("on, no map", outcome(ParMuteRun(ParMuteDocument(null), null, true)), declared("0", "null", ""));
                    leg("off, no map", outcome(ParMuteRun(ParMuteDocument(null), null, false)), declared("0", "", ""));
                    leg("on, map", outcome(ParMuteRun(ParMuteDocument(null), "[]", true)), declared("0", "null", ""));
                });

                // An evaluated null with no fault (B4): the null branch as it is, and no MUTE_VALUE_FAULTED.
                group("parity mute: an evaluated null without a fault writes null with Sounds on, nothing with Sounds off, and no MUTE_VALUE_FAULTED", 2, () =>
                {
                    leg("on", outcome(ParMuteRun(own("null", "action"), nullRows, true)), declared("0", "null", ""));
                    leg("off", outcome(ParMuteRun(own("null", "action"), nullRows, false)), declared("0", "", ""));
                });

                // P2-C's provenance defects (C11, Q1a, Q3; FC-2 steps 1-3): no mute line for that read, no information at all,
                // the named MUTE_PROVENANCE_MISSING as a parity trace line, status error and exit 1. Each reason is declared.
                // These legs' literal is authored as emitted ("to"), so only the qualified-literal label sees a writer that
                // writes the emitted value. The unbound leg's map is another document's, whose Sound is the literal
                // elsewhere (cache none) where the engine reads an action-cached evaluated null: a lookup by cache and
                // display text instead of by the definition object still finds nothing there (every evaluated definition
                // displays alike), so the map/3 value-key mutant stays the reader label's alone.
                const string localReason = "Sound is a local the action published, which no map row binds";
                const string noOwnerReason = "the Mute action has no owner type to find a map by";
                group("parity mute: a provenance defect writes no line and no information and ends the run in error (local, no owner, no map, unbound, inconsistent or incomplete row)", 7, () =>
                {
                    leg("local", outcome(ParMuteRun(own("to", "none"), literalRows, true, m => m.Again(3, ScriptValue.FromString("x")))),
                        declared("1", "\"to\"", missing(3, localReason)));
                    leg("local null, Sounds off", outcome(ParMuteRun(own("to", "none"), literalRows, false, m => m.Again(3, ScriptValue.Null))),
                        declared("1", "\"to\"", missing(3, localReason)));
                    leg("no owner type", outcome(ParMuteRun(own("to", "none"), literalRows, true, m => m.Again(3, noOwner: true))),
                        declared("1", "\"to\"", missing(3, noOwnerReason)));
                    leg("no owner map", outcome(ParMuteRun(own("Sound == null ? 1 : 2", "action"), null, true)), declared("1", "", missing(0, "member Mu has no parity map")));
                    leg("unbound definition, Sounds off", outcome(ParMuteRun(own("null", "action"), "[" + ParMuteRow("elsewhere", "elsewhere", "string") + "]", false,
                            foreignDocument: own("elsewhere", "none"))),
                        declared("1", "", missing(0, "the Sound definition read is bound to no soundParameters row of member Mu")));
                    leg("inconsistent row", outcome(ParMuteRun(own("Sound == null ? 1 : 2", "action"), "[" + ParMuteRow("Sound == null ? 1 : 2", null, null, "action") + "]", true,
                            before: m =>
                            {
                                ParityMap.SoundRow r = m.Map.SoundRows[0];
                                r.Mode = "literal";
                                r.SourceValue = "1";
                                r.SourceKind = "number";
                            })),
                        declared("1", "", missing(0, "soundParameters row 0 is literal, but the Sound definition read compiles with cache action")));
                    leg("literal row lacking sourceValue", outcome(ParMuteRun(own("to", "none"), literalRows, true, before: m => m.Map.SoundRows[0].SourceValue = null)),
                        declared("1", "", missing(0, "soundParameters row 0 is literal with no sourceValue or sourceKind")));
                });

                // Control 12 and FC-3: a pose provenance fault at tick 2 holds ProvenanceFault, and a Mute provenance defect at
                // tick 5 (a Mute with no owner type) is still logged, as the first Mute fault; status error, exit 1. Judged by
                // the exit code and the log alone: the defect read's withheld line is the provenance label's.
                group("parity mute: a pose fault then a Mute fault logs both parity trace lines (control 12, FC-3)", 1, () =>
                {
                    string[] both = ParMuteRun(own("to", "none"), literalRows, true, m => { m.PoseFault(2); m.Again(5, noOwner: true); });
                    leg("pose then Mute", "exit " + both[2] + ", log [" + OneLine(both[1]) + "]",
                        "exit 1, log [parity trace: tick 2 m1: applied pose has no source binding (member Old, which has no parity map) | " + missing(5, noOwnerReason) + "]");
                });

                // W5, the retained owner: a Mute built for Mu runs while the companion is already another member (Mv, which has no
                // map); its row comes from the map of the action's OwnerType, never of the current member.
                group("parity mute: a retained owner's read uses its OwnerType's map, not the current member's (W5)", 1, () =>
                {
                    leg("retained", outcome(ParMuteRun(own("to", "none"), literalRows, true, m =>
                        {
                            m.Mascot.Type = TickType.Compile("Mv", own("to", "none"));
                            m.Again(3, owner: m.Type);
                        })), declared("0", "\"to\"|\"to\"", ""));
                });

                // W3, incoming-over-own: the engine reads the incoming definition where the reference to Hush, or the behaviour
                // running Hush, binds Sound beside Hush's own distinct Sound, and the writer writes THAT definition's row (in or
                // beh), never the own row's (own). Each document's map binds both definitions. Every literal here is authored
                // as emitted, so only the qualified-literal label sees a writer that writes the emitted value. Round 6 adds the
                // next-reference half (see the leg).
                group("parity mute: incoming-over-own writes the winning definition's row, a reference bind, a behaviour bind or a next-list entry's bind over the own Sound (W3)", 4, () =>
                {
                    leg("reference bind", outcome(ParMuteRun(ParMuteDocument(new[] { bind("Sound", "in") }, sound("own", "none")),
                            "[" + ParMuteRow("own", "own", "string") + "," + ParMuteBindRow(0, "in", "in") + "]", true)),
                        declared("0", "\"in\"", ""));
                    XmlData.RootNode behaviourBound = own("own", "none");
                    foreach (XmlData.AnimationNode a in behaviourBound.Animations.Animation)
                        if (a.Id == 10)
                        {
                            a.Behaviour.Runs = "Hush";
                            a.Behaviour.Bind = new[] { bind("Sound", "beh") };
                        }
                    // Go running the instant Mute itself ends at once and Go is entered again, so this leg writes many mute
                    // lines; it is judged by the set of clips written, which must be the behaviour bind's beh alone.
                    string[] behaviourRun = ParMuteRun(behaviourBound, "[" + ParMuteRow("own", "own", "string") + "," + ParMuteBindRow(0, "beh", "beh", behaviour: true) + "]", true);
                    var behaviourClips = new SortedSet<string>(StringComparer.Ordinal);
                    foreach (string line in Lines(behaviourRun[0], "\"kind\":\"mute\""))
                        behaviourClips.Add(line.Substring(line.IndexOf("\"clip\":", StringComparison.Ordinal) + 7).TrimEnd('}'));
                    leg("behaviour bind", "exit " + behaviourRun[2] + ", clips written [" + string.Join("|", behaviourClips) + "], log [" + OneLine(behaviourRun[1]) + "]",
                        "exit 0, clips written [\"beh\"], log []");

                    // The next-reference half (round 6). The host's NextNode carries no binds, so a source next-list entry
                    // that binds Sound (a Behavior inside a NextBehaviorList, Action= naming what it runs) arrives as an
                    // off-route behaviour node with its own binds, named like the root behaviour whose list it continues into
                    // (the validator requires that name). The engine builds a chosen edge from the edge's own target
                    // (BuildNextBehaviour), never from its name, so the entry's bind is Hush's incoming definition. Go (10)
                    // runs Hush with no bind (own); its next list (add false) holds only 11, named Rest, running Hush with
                    // Sound bound to nxt; Rest (12, on the route, add false) runs Stand and continues into itself, so the run
                    // settles after the two reads. The run leg loads the same document and map through ParityRun.Run, so the
                    // validator and LoadPets accept them.
                    XmlData.RootNode nextBound = own("own", "none");
                    var nextNodes = new List<XmlData.AnimationNode>(nextBound.Animations.Animation);
                    XmlData.AnimationNode goNode = nextNodes.Find(a => a.Id == 10);
                    goNode.Behaviour.Runs = "Hush";
                    goNode.Behaviour.Add = false;
                    goNode.Behaviour.AddSpecified = true;
                    goNode.Sequence.Next = new[] { new XmlData.NextNode { Value = 11, Probability = 1 } };
                    nextNodes.Insert(nextNodes.IndexOf(goNode) + 1, new XmlData.AnimationNode
                    {
                        Id = 11, Name = "beh:Rest:next", Start = goNode.Start, End = goNode.End,
                        Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
                        Behaviour = new XmlData.BehaviourNode { Name = "Rest", Runs = "Hush", Frequency = 1, Bind = new[] { bind("Sound", "nxt") } },
                    });
                    nextNodes.Insert(nextNodes.IndexOf(goNode) + 2, new XmlData.AnimationNode
                    {
                        Id = 12, Name = "beh:Rest", Start = goNode.Start, End = goNode.End,
                        Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new[] { new XmlData.NextNode { Value = 12, Probability = 1 } } },
                        Behaviour = new XmlData.BehaviourNode { Name = "Rest", Runs = "Stand", Frequency = 1, Add = false, AddSpecified = true },
                    });
                    nextNodes.Find(a => a.Id == 1).Sequence.Next = new[] { new XmlData.NextNode { Value = 10, Probability = 1 }, new XmlData.NextNode { Value = 12, Probability = 1 } };
                    nextBound.Animations.Animation = nextNodes.ToArray();
                    string goEntry = "\"10\":" + JsonLine.Quote("Mu|conf/behaviors.xml|/Mascot[1]/BehaviorList[1]/Behavior[1]");
                    string nextEntryRef = "Mu|conf/behaviors.xml|/Mascot[1]/BehaviorList[1]/Behavior[1]/NextBehaviorList[1]/Behavior[1]";
                    string nextRow = "{\"kind\":\"behaviour\",\"nodeId\":11,\"sourceRefKey\":" + JsonLine.Quote(nextEntryRef)
                        + ",\"emittedValue\":\"nxt\",\"emittedCache\":\"none\",\"mode\":\"literal\",\"sourceValue\":\"nxt\",\"sourceKind\":\"string\"}";
                    string nextMap = ParMuteMap("[" + ParMuteRow("own", "own", "string") + "," + nextRow + "]").Replace(goEntry, goEntry
                        + ",\"11\":" + JsonLine.Quote(nextEntryRef) + ",\"12\":" + JsonLine.Quote("Mu|conf/behaviors.xml|/Mascot[1]/BehaviorList[1]/Behavior[2]"));
                    leg("next-list entry's bind", outcome(ParMuteRun(nextBound, null, true, map: nextMap)), declared("0", "\"own\"|\"nxt\"", ""));
                    leg("next-list entry's bind, run", runOutcome(runMute(Serialize(nextBound, Color.Red, Color.Blue), nextMap)),
                        runDeclared("0", "\"own\"|\"nxt\"", "complete", ""));
                });

                // W4, a nested reference: one Mute declaration (Hush, no own Sound) referenced twice with distinct binds keeps
                // each reference's own row across the two activations (a, then b), and a parent reference's bind (20:2 binds
                // Sound=out on the sequence Inner) does not leak into Inner's own reference to Hush2 (action 32, a second Mute
                // whose own Sound is own2), which binds nothing, so Hush2 reads own2. ActionBuilder builds each child with only
                // its own reference's parameters (TickRuntime BuildActionOfKind). No read here is absent (that is the absent
                // label's), Hush has no own Sound (so W3's own-definition mutant cannot reach it), and every literal is authored
                // as emitted.
                group("parity mute: a nested reference uses the winning definition, each reference's own bind and no parent's (W4)", 1, () =>
                {
                    XmlData.RootNode nested = ParMuteDocument(null);
                    var animations = new List<XmlData.AnimationNode>(nested.Animations.Animation);
                    foreach (XmlData.AnimationNode a in animations)
                        if (a.Id == 20)
                            a.Act.Children.Child = new[]
                            {
                                new XmlData.CompositeChildNode { Animation = "Hush", Bind = new[] { bind("Sound", "a") } },
                                new XmlData.CompositeChildNode { Animation = "Hush", Bind = new[] { bind("Sound", "b") } },
                                new XmlData.CompositeChildNode { Animation = "Inner", Bind = new[] { bind("Sound", "out") } },
                                new XmlData.CompositeChildNode { Animation = "Stand" },
                            };
                    var still = new XmlData.MovingNode { X = "0", Y = "0", Interval = "40" };
                    Func<int, string, XmlData.AnimationNode> node = (id, name) => new XmlData.AnimationNode
                    {
                        Id = id, Name = name, Start = still, End = still,
                        Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
                    };
                    XmlData.AnimationNode inner = node(21, "Inner");
                    inner.Act = new XmlData.ActNode
                    {
                        Type = "Sequence",
                        Children = new XmlData.ChildrenNode { Mode = "sequence", Child = new[] { new XmlData.CompositeChildNode { Animation = "Hush2" } } },
                    };
                    XmlData.AnimationNode hush2 = node(32, "Hush2");
                    hush2.Act = new XmlData.ActNode { Type = "Embedded", Class = "Mute", Params = new[] { sound("own2", "none") } };
                    animations.Add(inner);
                    animations.Add(hush2);
                    nested.Animations.Animation = animations.ToArray();
                    string hush2Row = "{\"kind\":\"action\",\"nodeId\":32,\"sourceRefKey\":" + JsonLine.Quote(ParMuteAction + "5]")
                        + ",\"emittedValue\":\"own2\",\"emittedCache\":\"none\",\"mode\":\"literal\",\"sourceValue\":\"own2\",\"sourceKind\":\"string\"}";
                    string nestedMap = ParMuteMap("[" + ParMuteBindRow(0, "a", "a") + "," + ParMuteBindRow(1, "b", "b") + "," + ParMuteBindRow(2, "out", "out") + "," + hush2Row + "]",
                        ",\"21\":" + JsonLine.Quote(ParMuteAction + "4]") + ",\"32\":" + JsonLine.Quote(ParMuteAction + "5]"),
                        ",\"20:2\":" + JsonLine.Quote(ParMuteAction + "1]/ActionReference[3]") + ",\"20:3\":" + JsonLine.Quote(ParMuteAction + "1]/ActionReference[4]")
                        + ",\"21:0\":" + JsonLine.Quote(ParMuteAction + "4]/ActionReference[1]"));
                    leg("two references, then a parent's bind", outcome(ParMuteRun(nested, null, true, m =>
                        {
                            for (int t = 1; t <= 4; t++)
                            {
                                m.Writer.Tick = t;
                                m.World.StepOnce();
                            }
                        }, map: nestedMap)), declared("0", "\"a\"|\"b\"|\"own2\"", ""));
                });

                // Control 7: observing adds no draw. Condition ${Sound == null} and an action-cached Sound that draws and is null
                // (so never cached), Sounds off: the Condition read and Apply's read draw once each, exactly 2, in the writer's
                // world and in a NullObserver world alike.
                group("parity mute: observation adds no draw, the writer's world against a NullObserver world (control 7)", 1, () =>
                {
                    XmlData.RootNode drawDocument = ParMuteDocument(null, new XmlData.ParamNode { Name = "Condition", Value = "Sound == null", Cache = "action" },
                        sound("Math.random() < 2 ? null : 'x'", "action"));
                    var observedDraws = new ParCountingRandom();
                    string[] drawRun = ParMuteRun(drawDocument, "[" + ParMuteRow("Math.random() < 2 ? null : 'x'", null, null, "action") + "]", false, random: observedDraws);
                    var plainDraws = new ParCountingRandom();
                    TickType plainType = TickType.Compile("Mu", drawDocument);
                    TickWorld plainWorld = ParMuteWorld(null, false, plainDraws);
                    plainWorld.AddType(plainType);
                    plainWorld.Start();
                    plainWorld.CreateAtStart("m1", plainType, new TickPoint(640, 680), false, "Go");
                    leg("draws", observedDraws.Draws + " and " + plainDraws.Draws + ", " + outcome(drawRun), "2 and 2, " + declared("0", "", ""));
                });

                // FC-6 (revision 3): information is logged after every parity trace line and the rng-tape-exhausted line, before
                // the footer, the same in both trace modes: one evaluated Number read, then a pose fault at tick 2, ended with a
                // tape drawn past its end.
                group("parity mute: information follows the parity trace and rng-tape-exhausted lines, before the footer, in both trace modes (FC-6)", 2, () =>
                {
                    ParityRandom exhausted;
                    using (JsonDocument tape = JsonDocument.Parse("{\"kind\":\"tape\",\"values\":[]}")) exhausted = new ParityRandom(tape.RootElement);
                    exhausted.Next();
                    string orderLines = "parity trace: tick 2 m1: applied pose has no source binding (member Old, which has no parity map) | rng-tape-exhausted (information): "
                        + "the run drew past the end of its 0-value tape; each draw past the end yielded 0.5 and the run went on | MUTE_VALUE_NOT_STRING tick 0 m1: evaluated number" + info;
                    leg("traced", outcome(ParMuteRun(own("1", "action"), numberRows, true, m => m.PoseFault(2), finishRandom: exhausted)), declared("1", "\"1\"", orderLines));
                    leg("trace-off", outcome(ParMuteRun(own("1", "action"), numberRows, true, m => m.PoseFault(2), traceOff: true, finishRandom: exhausted)), declared("1", "", orderLines));
                });

                // C18 (commit 5b; FC-2, revision 3 with host commitment S1 and review note S5): an InvalidOperationException
                // injected at each FC-2 observation site, the probe armed for the Sound read alone, is retained as that read's
                // provenance defect with the exact reason, writes no mute line and no information, and ends the run in error
                // with exit 1, while the engine's run is the unarmed run's: the same trace apart from that read's mute line and
                // the footer, and no error or dispose event. The sites are FC-2's list, never the catch regions: both fault-count
                // snapshots ("snapshot" before the read, "result" after it), the passive selection, both callback forms, the
                // identity lookup on each branch that makes one (named, null with Sounds on, null with Sounds off; each null an
                // evaluated null through a bound definition, never an absent or local one), the source-value lookup and the
                // writer. Sounds on except for the no-event branch. An exception escaping the run fails the label. Round 5 adds
                // the result and writer sites on an evaluated Number, a read that FORMS information (MUTE_VALUE_NOT_STRING).
                // Only the WRITER-site variant gives "no information for that read" teeth (FC-6: information formed for a
                // defect read is discarded; the AddRange-before-Probe mutant fails it there). The result-site variant throws
                // before read.Kind and read.Faulted are taken, so its record keeps Kind Null and no writer can form information
                // from it; it stays as the result site's run on an information-forming read and is credited with nothing more.
                // Round 5 also adds a parity run through ParityRun.Run with the writer site armed: exit 1, footer status error in the trace file,
                // the parity trace line in the run's log, and the engine's lines those of the same run unarmed (control 11's
                // synthetic pair for compare.py is these two trace files).
                group("parity mute: an observation exception at every FC-2 site is retained as MUTE_PROVENANCE_MISSING and the engine's run is unchanged (C18)", 13, () =>
                {
                    string[][] c18 =
                    {
                        new[] { "selection", "to", "none", "on" }, new[] { "snapshot", "to", "none", "on" }, new[] { "result", "to", "none", "on" },
                        new[] { "muted", "to", "none", "on" }, new[] { "identity", "to", "none", "on" }, new[] { "identity", "null", "action", "on" },
                        new[] { "identity", "null", "action", "off" }, new[] { "no-event", "null", "action", "off" },
                        new[] { "source-value", "to", "none", "on" }, new[] { "writer", "to", "none", "on" },
                        new[] { "result", "1", "action", "on" }, new[] { "writer", "1", "action", "on" },
                    };
                    foreach (string[] v in c18)
                    {
                        string rows = "[" + (v[2] == "none" ? ParMuteRow(v[1], v[1], "string") : ParMuteRow(v[1], null, null, v[2])) + "]";
                        bool on = v[3] == "on";
                        string site = v[0], name = site + " " + v[1] + " Sounds " + v[3];
                        string[] unarmed = ParMuteRun(own(v[1], v[2]), rows, on), armed = null;
                        string escaped = null;
                        TickParamRead.ObservationProbe = (at, parameter) => { if (at == site && parameter == "Sound") throw new InvalidOperationException("c18 " + at); };
                        try { armed = ParMuteRun(own(v[1], v[2]), rows, on); }
                        catch (Exception e) { escaped = e.GetType().Name + ": " + e.Message; }
                        finally { TickParamRead.ObservationProbe = null; }
                        if (escaped != null)
                        {
                            faults.Add(name + ": the exception escaped the run: " + escaped);
                            continue;
                        }
                        leg(name, outcome(armed), declared("1", "", missing(0, "observer exception InvalidOperationException: c18 " + site)));
                        bool engineSame = engineLines(armed[0]) == engineLines(unarmed[0]) && !armed[0].Contains("\"kind\":\"error\"") && !armed[0].Contains("\"kind\":\"dispose");
                        leg(name + " engine", engineSame ? "unchanged" : "changed: " + OneLine(armed[0]), "unchanged");
                    }
                    string xml = Serialize(own("to", "none"), Color.Red, Color.Blue), map = ParMuteMap(literalRows);
                    string[] runUnarmed = runMute(xml, map), runArmed;
                    TickParamRead.ObservationProbe = (at, parameter) => { if (at == "writer" && parameter == "Sound") throw new InvalidOperationException("c18 writer"); };
                    try { runArmed = runMute(xml, map); }
                    finally { TickParamRead.ObservationProbe = null; }
                    leg("run writer", runOutcome(runArmed) + (engineLines(runArmed[1]) == engineLines(runUnarmed[1]) ? "" : ", engine lines differ from the unarmed run's")
                            + (runUnarmed[0] == "0" && runUnarmed[1] != null ? "" : ", the unarmed run did not end 0 with a trace"),
                        runDeclared("1", "", "error", missing(0, "observer exception InvalidOperationException: c18 writer")));
                });

                // C18's engine-Read leg (the coordinator's ruling after round 4): an environment decorator whose cursor read
                // throws, armed in `before` and disarmed in `after`, under a Sound that reads the cursor. The exception comes from
                // the engine's OWN Read, which no observation region wraps, so it takes the engine's base path (the start
                // wrapper reports an internal fault and disposes m1): an error event, then a dispose, no mute line, no read
                // record, so no MUTE_PROVENANCE_MISSING and exit 0, exactly as the same run in a NullObserver world ends (m1
                // disposed, nothing thrown). The unarmed witness reads the cursor once and forwards 'a'. Round 6 (revision 3
                // C18: "the base exception, error event, disposal and call count"): the armed and NullObserver legs declare one
                // cursor read too, and the armed leg declares the error event's whole message, the start wrapper's text around
                // the exception's type and message, so an observed read that retried the throwing Read (two cursor reads) or
                // replaced the exception fails this label.
                group("parity mute: an exception from the engine's own Sound read takes the engine's path, never a provenance defect (C18 engine-Read)", 3, () =>
                {
                    string cursorSound = "mascot.environment.cursor.x > -1 ? 'a' : 'b'";
                    string cursorRows = "[" + ParMuteRow(cursorSound, null, null, "action") + "]";
                    var throwing = new ParThrowingEnvironment[1];
                    Func<ITickEnvironment, ITickEnvironment> wrap = inner => throwing[0] = new ParThrowingEnvironment(inner);
                    string[] unarmed = ParMuteRun(own(cursorSound, "action"), cursorRows, true, environment: wrap);
                    leg("unarmed", outcome(unarmed) + ", cursor read " + throwing[0].CursorReads, declared("0", "\"a\"", "") + ", cursor read 1");
                    int left = -1;
                    string[] armed = ParMuteRun(own(cursorSound, "action"), cursorRows, true, m => { left = m.World.Mascots.Count; throwing[0].Armed = false; },
                        environment: wrap, before: m => throwing[0].Armed = true);
                    int errorAt = armed[0].IndexOf("\"kind\":\"error\"", StringComparison.Ordinal), disposeAt = armed[0].IndexOf("\"kind\":\"dispose", StringComparison.Ordinal);
                    var messages = new List<string>();
                    foreach (string line in Lines(armed[0], "\"kind\":\"error\""))
                        using (JsonDocument errorEvent = JsonDocument.Parse(line)) messages.Add(errorEvent.RootElement.GetProperty("message").GetString());
                    leg("armed", outcome(armed) + ", " + (errorAt >= 0 && errorAt < disposeAt ? "error then dispose" : "no error then dispose")
                            + ", message [" + string.Join(" | ", messages) + "], cursor read " + throwing[0].CursorReads + ", companions left " + left,
                        declared("0", "", "") + ", error then dispose, message [internal fault: InvalidOperationException: the first behaviour could not be built: c18 cursor]"
                            + ", cursor read 1, companions left 0");
                    var plainEnvironment = new ParThrowingEnvironment[1];
                    TickType plainType = TickType.Compile("Mu", own(cursorSound, "action"));
                    TickWorld plainWorld = ParMuteWorld(null, true, null, inner => plainEnvironment[0] = new ParThrowingEnvironment(inner));
                    plainWorld.AddType(plainType);
                    plainWorld.Start();
                    plainEnvironment[0].Armed = true;
                    string plainThrew = "nothing thrown";
                    try { plainWorld.CreateAtStart("m1", plainType, new TickPoint(640, 680), false, "Go"); }
                    catch (Exception e) { plainThrew = "threw " + e.GetType().Name; }
                    leg("NullObserver world", plainThrew + ", cursor read " + plainEnvironment[0].CursorReads + ", companions left " + plainWorld.Mascots.Count,
                        "nothing thrown, cursor read 1, companions left 0");
                });

                // The arming (S5) and the round 5 ruling: the probe is null until a self-test sets it, it is handed each read's
                // own parameter name, and only the Mute Sound read reaches the observation sites. Stand here also has a
                // BorderType param read through the plain EvalString, an action-cached Math.random draw, so the one draw counted
                // proves that read ran, and no observation site sees it.
                group("parity mute: the observation probe is null in the product and sees the Mute Sound read alone, never another EvalString read (C18 arming, S5, round 5)", 1, () =>
                {
                    XmlData.RootNode bordered = own("to", "none");
                    foreach (XmlData.AnimationNode a in bordered.Animations.Animation)
                        if (a.Id == 31) a.Act.Params = new[] { a.Act.Params[0], new XmlData.ParamNode { Name = "BorderType", Value = "Math.random() < 2 ? 'Floor' : 'Wall'", Cache = "action" } };
                    var sites = new List<string>();
                    var borderDraws = new ParCountingRandom();
                    TickParamRead.ObservationProbe = (at, parameter) => sites.Add(at + " " + parameter);
                    string[] recorded;
                    try { recorded = ParMuteRun(bordered, literalRows, true, random: borderDraws); }
                    finally { TickParamRead.ObservationProbe = null; }
                    leg("probe", (probeInert ? "null in the product; " : "already set; ") + string.Join(", ", sites) + "; draws " + borderDraws.Draws + "; " + outcome(recorded),
                        "null in the product; selection Sound, snapshot Sound, result Sound, muted Sound, identity Sound, source-value Sound, writer Sound; draws 1; "
                        + declared("0", "\"to\"", ""));
                });
            }
            catch (Exception e)
            {
                check(false, "parity mute: the trace writer's Mute checks ran without an exception: " + e);
            }
        }

        /// <summary>CONTRACT v1.1 section 9's binding: one mapping per emitted pose, the placeholder exemption only for
        /// its exact shape, and surplus keys refused.</summary>
        private static void CheckParityPoseBinding(Action<bool, string> check)
        {
            Dictionary<string, string> poses = ParPoses("Par", true);
            XmlData.RootNode document = ParDocument(true);
            TickType type = TickType.Compile("Par", document);
            ParityMap map = ParityMap.Parse(ParMapJson("Par", true, poses));
            map.Apply(type, document);
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
            XmlData.RootNode authoredDocument = ParDocument(true);
            TickType authoredType = TickType.Compile("Par", authoredDocument);
            authored.Apply(authoredType, authoredDocument);
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
            XmlData.RootNode oldDocument = ParDocument(false), newDocument = ParDocument(false);
            TickType oldType = TickType.Compile("Old", oldDocument);
            ParityMap oldMap = ParityMap.Parse(ParMapJson("Old", false, ParPoses("Old", false)));
            oldMap.Apply(oldType, oldDocument);
            TickType newType = TickType.Compile("Par", newDocument);
            ParityMap newMap = ParityMap.Parse(ParMapJson("Par", false, ParPoses("Par", false)));
            newMap.Apply(newType, newDocument);
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
                  && retired[2].Contains("refused " + retired[3] + ": the map is shimeji-parity-map/1; regenerate it with the current converter (map/3 required)"),
                "parity run: a map/1 file is refused with exit 2 and no trace file (" + retired[2].Trim() + ")");

            // burndown/host-mute-1, CONTRACT v1.4 draft P1-F, FC-1, C13 and C14 (host half): the run's refusal is the
            // reader's. Each label below holds the reader's exact message (an InvalidDataException; ParApply prefixes any
            // other type) AND the run's exit 2, no trace and that same reason after "refused <map path>: ", so a mutant
            // that removes the one reader check fails exactly one label. Exit 2 and no trace alone cannot kill such a
            // mutant: a map/2 file with the retirement removed is still refused, as not map/3.
            string map2 = ParMapJson("Par", false, ParPoses("Par", false), "shimeji-parity-map/2");
            string map2Reader = ParApply(map2, false);
            string[] map2Run = go(map2, "", xml, false);
            check(map2Reader == "the map is shimeji-parity-map/2; regenerate it with the current converter (map/3 required)"
                  && map2Run[0] == "2" && map2Run[1] == null && map2Run[2].Contains("refused " + map2Run[3] + ": " + map2Reader + Environment.NewLine),
                "parity map/3: a map/2 file is refused with a regenerate message, by the reader and by a run (exit 2, no trace) ("
                + map2Reader + "; exit " + map2Run[0] + ", " + map2Run[2].Trim() + ")");
            string cachedLiteral = ParMapJson("Par", false, ParPoses("Par", false),
                soundRows: "[" + ParSoundLiteral.Replace("\"emittedCache\":\"none\"", "\"emittedCache\":\"action\"") + "]");
            string cachedReader = ParApply(cachedLiteral, false);
            string[] cachedRun = go(cachedLiteral, "", xml, false);
            check(cachedReader == "parity map soundParameters row 0 is literal with emittedCache 'action' (a literal row has none)"
                  && cachedRun[0] == "2" && cachedRun[1] == null && cachedRun[2].Contains("refused " + cachedRun[3] + ": " + cachedReader + Environment.NewLine),
                "parity map/3: a literal Sound row with emittedCache action is refused, by the reader and by a run (exit 2, no trace) ("
                + cachedReader + "; exit " + cachedRun[0] + ", " + cachedRun[2].Trim() + ")");
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
