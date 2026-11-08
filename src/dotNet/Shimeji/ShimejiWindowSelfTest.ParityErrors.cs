using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DesktopAICompanion.Shimeji
{
    internal static partial class ShimejiWindowSelfTest
    {
        /// <summary>
        /// burndown/parity-host-2, part 2: CONTRACT 10.3's typed error object, as the host writes it. Each fault is driven
        /// through the real engine and the real trace writer on generated members, and every error line written is checked
        /// by <see cref="ValidateErrorEvent"/> against 10.3.1 and 10.3.2 before its code, operands and wrapper are compared
        /// with what 10.3.3 and 10.3.4 say. The messages stay the engine's own words: one golden list pins them all (CH12).
        /// </summary>
        private static void CheckParityErrors(Action<bool, string> check)
        {
            try
            {
                CheckErrorValidator(check);
                var golden = new List<string>();
                var emitted = new List<string>();
                CheckErrorBirthAndTransform(check, golden, emitted);
                CheckErrorWrappers(check, golden, emitted);
                CheckErrorWhereItArises(check, golden, emitted);
                CheckErrorNoEvent(check);

                var invalid = new List<string>();
                foreach (string line in emitted)
                {
                    string why = ValidateErrorEvent(line);
                    if (why != null) invalid.Add(why + " in " + line);
                }
                check(emitted.Count >= 12 && invalid.Count == 0,
                    "parity error: every error line the host wrote is valid under CONTRACT 10.3.1 and 10.3.2 (" + emitted.Count + " lines; "
                    + string.Join("; ", invalid) + ")");
                check(golden.Count == 0, "parity error: every message is byte-identical to the engine's own words (golden list): " + string.Join("; ", golden));
            }
            catch (Exception e)
            {
                check(false, "parity error: the checks ran without an exception: " + e);
            }
        }

        // ---- the validator (10.3.1, 10.3.2)

        private static readonly string[] ErrorCodes = { "behavior-missing", "action-missing", "variant-missing", "parameter-invalid", "scan-target-missing", "unclassified" };
        private static readonly string[] ErrorVia = { "startup", "press", "release", "birth", "transform", "arrive", "interact" };

        /// <summary>Each code's operand set, in 10.3.2's order (unclassified has none: its lookup is null).</summary>
        private static string[] ErrorOperands(string code)
        {
            switch (code)
            {
                case "behavior-missing": return new[] { "imageSet", "behavior" };
                case "action-missing": return new[] { "imageSet", "action" };
                case "parameter-invalid": return new[] { "refKey", "parameter" };
                case "variant-missing":
                case "scan-target-missing": return new[] { "refKey" };
                default: return null;
            }
        }

        /// <summary>The property names of an object, in order, duplicates kept (JsonDocument keeps a repeated property, so a
        /// duplicate is seen here and refused).</summary>
        private static List<string> NamesOf(JsonElement e)
        {
            var names = new List<string>();
            foreach (JsonProperty p in e.EnumerateObject()) names.Add(p.Name);
            return names;
        }

        private static bool ExactlyOnce(List<string> names, params string[] expected)
        {
            if (names.Count != expected.Length) return false;
            foreach (string n in expected)
                if (names.FindAll(x => x == n).Count != 1) return false;
            return true;
        }

        /// <summary>
        /// CONTRACT 10.3.1 and 10.3.2 for one trace line of kind error: null when valid, else why not. The error object has
        /// exactly code, lookup and via; code is in the closed table; lookup is null exactly for unclassified and otherwise
        /// has exactly the code's operands, each of its wire type (imageSet, refKey and parameter non-empty strings, a refKey
        /// in section 3's three-part form, behavior and action a string or null); via is an array of the closed wrapper
        /// labels; message is a non-empty string. A repeated property anywhere in it is refused.
        /// </summary>
        internal static string ValidateErrorEvent(string line)
        {
            try
            {
                using (JsonDocument d = JsonDocument.Parse(line))
                {
                    JsonElement root = d.RootElement, v;
                    List<string> top = NamesOf(root);
                    foreach (string n in new[] { "type", "kind", "message", "error" })
                        if (top.FindAll(x => x == n).Count != 1) return "the event has " + top.FindAll(x => x == n).Count + " '" + n + "'";
                    if (root.GetProperty("kind").ValueKind != JsonValueKind.String || root.GetProperty("kind").GetString() != "error") return "not an error event";
                    v = root.GetProperty("message");
                    if (v.ValueKind != JsonValueKind.String || v.GetString().Length == 0) return "message is not a non-empty string";
                    JsonElement error = root.GetProperty("error");
                    if (error.ValueKind != JsonValueKind.Object) return "error is not an object";
                    if (!ExactlyOnce(NamesOf(error), "code", "lookup", "via")) return "error's properties are " + string.Join(",", NamesOf(error));
                    JsonElement code = error.GetProperty("code"), lookup = error.GetProperty("lookup"), via = error.GetProperty("via");
                    if (code.ValueKind != JsonValueKind.String || Array.IndexOf(ErrorCodes, code.GetString()) < 0) return "code " + code.GetRawText() + " is not in the closed table";
                    string[] operands = ErrorOperands(code.GetString());
                    if (operands == null)
                    {
                        if (lookup.ValueKind != JsonValueKind.Null) return "unclassified has a non-null lookup";
                    }
                    else
                    {
                        if (lookup.ValueKind != JsonValueKind.Object) return code.GetString() + " has a lookup that is not an object";
                        if (!ExactlyOnce(NamesOf(lookup), operands)) return code.GetString() + "'s lookup has " + string.Join(",", NamesOf(lookup));
                        foreach (string operand in operands)
                        {
                            JsonElement o = lookup.GetProperty(operand);
                            bool nameOperand = operand == "behavior" || operand == "action";
                            if (nameOperand ? o.ValueKind != JsonValueKind.String && o.ValueKind != JsonValueKind.Null
                                            : o.ValueKind != JsonValueKind.String || o.GetString().Length == 0)
                                return operand + " " + o.GetRawText() + " is not of its wire type";
                            if (operand == "refKey")
                            {
                                string[] parts = o.GetString().Split('|');
                                if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0 || !parts[2].StartsWith("/", StringComparison.Ordinal))
                                    return "refKey '" + o.GetString() + "' is not a source refKey";
                            }
                        }
                    }
                    if (via.ValueKind != JsonValueKind.Array) return "via is not an array";
                    foreach (JsonElement label in via.EnumerateArray())
                        if (label.ValueKind != JsonValueKind.String || Array.IndexOf(ErrorVia, label.GetString()) < 0)
                            return "via label " + label.GetRawText() + " is not in the closed vocabulary";
                    return null;
                }
            }
            catch (Exception e)
            {
                return "unreadable (" + e.GetType().Name + ": " + e.Message + ")";
            }
        }

        /// <summary>The validator refuses each malformation 10.3.1 and 10.3.2 name, and accepts each code's own shape.</summary>
        private static void CheckErrorValidator(Action<bool, string> check)
        {
            const string head = "{\"type\":\"event\",\"tick\":1,\"sid\":\"m1\",\"kind\":\"error\",\"message\":\"m\",\"error\":";
            const string key = "\"A|conf/actions.xml|/Mascot[1]/ActionList[1]/Action[1]\"";
            var good = new[]
            {
                "{\"code\":\"behavior-missing\",\"lookup\":{\"imageSet\":\"A\",\"behavior\":\"\"},\"via\":[\"birth\"]}",
                "{\"code\":\"behavior-missing\",\"lookup\":{\"imageSet\":\"A\",\"behavior\":null},\"via\":[]}",
                "{\"code\":\"action-missing\",\"lookup\":{\"imageSet\":\"A\",\"action\":\" \"},\"via\":[\"transform\"]}",
                "{\"code\":\"variant-missing\",\"lookup\":{\"refKey\":" + key + "},\"via\":[]}",
                "{\"code\":\"parameter-invalid\",\"lookup\":{\"refKey\":" + key + ",\"parameter\":\"BornCount\"},\"via\":[\"startup\"]}",
                "{\"code\":\"scan-target-missing\",\"lookup\":{\"refKey\":" + key + "},\"via\":[\"arrive\"]}",
                "{\"code\":\"unclassified\",\"lookup\":null,\"via\":[\"press\"]}",
            };
            var refused = new List<string>();
            foreach (string g in good)
                if (ValidateErrorEvent(head + g + "}") != null) refused.Add(g + ": " + ValidateErrorEvent(head + g + "}"));
            check(refused.Count == 0, "parity error validator: WITNESS each code's own shape is valid (" + string.Join("; ", refused) + ")");

            var bad = new[]
            {
                "{\"code\":\"behaviour-missing\",\"lookup\":{\"imageSet\":\"A\",\"behavior\":\"\"},\"via\":[]}",
                "{\"code\":\"behavior-missing\",\"lookup\":{\"imageSet\":\"A\"},\"via\":[]}",
                "{\"code\":\"behavior-missing\",\"lookup\":{\"imageSet\":\"A\",\"behavior\":\"\",\"action\":\"\"},\"via\":[]}",
                "{\"code\":\"behavior-missing\",\"lookup\":{\"imageSet\":\"A\",\"behavior\":\"\",\"behavior\":\"\"},\"via\":[]}",
                "{\"code\":\"behavior-missing\",\"lookup\":{\"imageSet\":{},\"behavior\":\"\"},\"via\":[]}",
                "{\"code\":\"behavior-missing\",\"lookup\":{\"imageSet\":\"\",\"behavior\":\"\"},\"via\":[]}",
                "{\"code\":\"behavior-missing\",\"lookup\":{\"imageSet\":\"A\",\"behavior\":3},\"via\":[]}",
                "{\"code\":\"behavior-missing\",\"lookup\":null,\"via\":[]}",
                "{\"code\":\"variant-missing\",\"lookup\":{\"refKey\":\"\"},\"via\":[]}",
                "{\"code\":\"variant-missing\",\"lookup\":{\"refKey\":\"A|/Mascot[1]\"},\"via\":[]}",
                "{\"code\":\"parameter-invalid\",\"lookup\":{\"refKey\":" + key + ",\"parameter\":\"\"},\"via\":[]}",
                "{\"code\":\"unclassified\",\"lookup\":{},\"via\":[]}",
                "{\"code\":\"unclassified\",\"lookup\":null,\"via\":\"press\"}",
                "{\"code\":\"unclassified\",\"lookup\":null,\"via\":[\"step\"]}",
                "{\"code\":\"unclassified\",\"lookup\":null}",
                "{\"code\":\"unclassified\",\"lookup\":null,\"via\":[],\"extra\":1}",
                "{\"code\":\"unclassified\",\"code\":\"unclassified\",\"lookup\":null,\"via\":[]}",
                "{\"code\":7,\"lookup\":null,\"via\":[]}",
                "null",
            };
            var accepted = new List<string>();
            foreach (string b in bad)
                if (ValidateErrorEvent(head + b + "}") == null) accepted.Add(b);
            string noObject = ValidateErrorEvent("{\"type\":\"event\",\"tick\":1,\"sid\":\"m1\",\"kind\":\"error\",\"message\":\"m\"}");
            string noMessage = ValidateErrorEvent("{\"type\":\"event\",\"tick\":1,\"sid\":\"m1\",\"kind\":\"error\",\"message\":\"\",\"error\":" + good[6] + "}");
            if (noObject == null) accepted.Add("an event with no error object");
            if (noMessage == null) accepted.Add("an empty message");
            check(accepted.Count == 0, "parity error validator: every malformation of 10.3.1 and 10.3.2 is refused (accepted: " + string.Join("; ", accepted) + ")");
        }

        // ---- generated members

        private const string ErrFeatures = "tick actions behaviours expressions lifecycle social";

        private static XmlData.ParamNode EParam(string name, string value, string cache = "none")
        {
            return new XmlData.ParamNode { Name = name, Value = value, Cache = cache };
        }

        private static XmlData.AnimationNode EAct(int id, string name, string type, string cls, int ticks, params XmlData.ParamNode[] ps)
        {
            var still = new XmlData.MovingNode { X = "0", Y = "0", Interval = "40" };
            return new XmlData.AnimationNode
            {
                Id = id, Name = name, Start = still, End = still,
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
                Act = new XmlData.ActNode
                {
                    Type = type, Class = cls, Border = "Floor", Params = ps,
                    Variants = new XmlData.VariantsNode { Variant = new[] { new XmlData.VariantNode { Pose = new[] { new XmlData.PoseNode { Frame = 0, Ticks = ticks } } } } },
                },
            };
        }

        private static XmlData.AnimationNode EBeh(int id, string name, string runs)
        {
            var still = new XmlData.MovingNode { X = "0", Y = "0", Interval = "40" };
            return new XmlData.AnimationNode
            {
                Id = id, Name = "beh:" + name, Start = still, End = still,
                Behaviour = new XmlData.BehaviourNode { Name = name, Runs = runs, Frequency = 1 },
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
            };
        }

        /// <summary>A member: a route to every behaviour given (each a root behaviour; only the first weighs anything), the
        /// nodes given, and every node given a refKey as a map/2 would.</summary>
        private static TickType EType(string set, params XmlData.AnimationNode[] nodes)
        {
            var still = new XmlData.MovingNode { X = "0", Y = "0", Interval = "40" };
            var edges = new List<XmlData.NextNode>();
            foreach (XmlData.AnimationNode n in nodes)
                if (n.Behaviour != null) edges.Add(new XmlData.NextNode { Value = n.Id, Probability = edges.Count == 0 ? 1 : 0 });
            var route = new XmlData.AnimationNode
            {
                Id = 1, Name = "route", Start = still, End = still, Route = new XmlData.RouteNode(),
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = edges.ToArray() },
            };
            var all = new List<XmlData.AnimationNode> { route };
            all.AddRange(nodes);
            TickType type = TickType.Compile(set, new XmlData.RootNode
            {
                Header = new XmlData.HeaderNode { Title = set, Features = ErrFeatures, Clock = "tick" },
                Image = new XmlData.ImageNode { TilesX = 2, TilesY = 1, Png = PngHeader(128, 64), AnchorX = 32, AnchorXSpecified = true, AnchorY = 64, AnchorYSpecified = true },
                Animations = new XmlData.AnimationsNode { Animation = all.ToArray() },
            });
            foreach (TickActDef a in type.ActionsById.Values) a.RefKey = ErrActionKey(set, a.NodeId);
            foreach (TickBehaviourDef b in type.BehavioursById.Values) b.RefKey = set + "|conf/behaviors.xml|/Mascot[1]/BehaviorList[1]/Behavior[" + b.NodeId + "]";
            return type;
        }

        private static string ErrActionKey(string set, int node)
        {
            return set + "|conf/actions.xml|/Mascot[1]/ActionList[1]/Action[" + node + "]";
        }

        /// <summary>A world of the members given, traced into <paramref name="trace"/>.</summary>
        private static TickWorld EWorld(StringWriter trace, out ParityTraceWriter writer, params TickType[] types)
        {
            writer = new ParityTraceWriter(trace, new Dictionary<string, ParityMap>(StringComparer.Ordinal)) { Tick = 0 };
            TickWorld world = ParWorld(writer);
            foreach (TickType t in types) world.AddType(t);
            world.Start();
            return world;
        }

        /// <summary>The trace's error lines, each also collected for the validator.</summary>
        private static List<string> Errors(StringWriter trace, List<string> emitted)
        {
            List<string> found = Lines(trace.ToString(), "\"kind\":\"error\"");
            emitted.AddRange(found);
            return found;
        }

        /// <summary>The error object of one error line, as written (its raw JSON), and its message.</summary>
        private static string ErrorOf(string line, out string message)
        {
            message = null;
            if (line == null) return null;
            using (JsonDocument d = JsonDocument.Parse(line))
            {
                JsonElement m, e;
                if (d.RootElement.TryGetProperty("message", out m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
                return d.RootElement.TryGetProperty("error", out e) ? e.GetRawText() : null;
            }
        }

        private static void Golden(List<string> golden, string actual, string expected)
        {
            if (!string.Equals(actual, expected, StringComparison.Ordinal)) golden.Add("expected '" + expected + "', wrote '" + actual + "'");
        }

        private static string One(List<string> lines) { return lines.Count == 1 ? lines[0] : null; }

        // ---- CH2, CH3, CH4, CH6: the closure shapes and the requested-name operands

        /// <summary>A parent whose Breed asks for <paramref name="bornBehaviour"/> of member Kid, stepped once.</summary>
        private static StringWriter RunBirth(XmlData.ParamNode bornBehaviour, string bornMascot)
        {
            TickType par = EType("Par", EBeh(10, "Breeder", "Breeder"),
                EAct(30, "Breeder", "Embedded", "Breed", 1, EParam("BornMascot", bornMascot), EParam("BornCount", "1"), bornBehaviour));
            TickType kid = EType("Kid", EBeh(10, "Stand", "Stand"), EAct(30, "Stand", "Stay", null, 100));
            var trace = new StringWriter();
            ParityTraceWriter writer;
            TickWorld world = EWorld(trace, out writer, par, kid);
            world.CreateAtStart("m1", par, new TickPoint(640, 680), false, "Breeder");
            world.ActivateStartup();
            writer.Tick = 1;
            world.StepOnce();
            return trace;
        }

        private static void CheckErrorBirthAndTransform(Action<bool, string> check, List<string> golden, List<string> emitted)
        {
            // CH2 (10.9's S-breed-blankbehavior-01 shape): the PARENT reports it, behavior-missing in the CHILD's member,
            // the blank name as asked, via birth, and then the refusal.
            StringWriter blank = RunBirth(EParam("BornBehavior", ""), "Kid");
            string message;
            List<string> blankErrors = Errors(blank, emitted);
            string blankError = ErrorOf(One(blankErrors), out message);
            Golden(golden, message, "a birth failed: no behaviour named '' in Kid");
            string trace = blank.ToString();
            int at = trace.IndexOf("\"kind\":\"error\"", StringComparison.Ordinal), refusedAt = trace.IndexOf("\"kind\":\"birth-refused\"", StringComparison.Ordinal);
            check(blankError == "{\"code\":\"behavior-missing\",\"lookup\":{\"imageSet\":\"Kid\",\"behavior\":\"\"},\"via\":[\"birth\"]}"
                  && One(blankErrors).Contains("\"sid\":\"m1\"") && at >= 0 && refusedAt > at && trace.Contains("\"reason\":\"error\""),
                "parity error: a blank BornBehavior is behavior-missing at the parent, imageSet the child's member, behavior \"\", via birth,"
                + " then birth-refused error (" + blankError + ")");

            // CH6: "", " " and null are three operands, captured before name ?? "".
            string spaceError = ErrorOf(One(Errors(RunBirth(EParam("BornBehavior", " "), "Kid"), emitted)), out message);
            Golden(golden, message, "a birth failed: no behaviour named ' ' in Kid");
            string nullError = ErrorOf(One(Errors(RunBirth(EParam("BornBehavior", "null", "action"), "Kid"), emitted)), out message);
            Golden(golden, message, "a birth failed: no behaviour named '' in Kid");
            check(spaceError != null && spaceError.Contains("\"behavior\":\" \"}") && nullError != null && nullError.Contains("\"behavior\":null}"),
                "parity error: the requested names \"\", \" \" and null are three operands, taken before any normalisation (" + spaceError + "; " + nullError + ")");

            // CH3 (10.9's S-transform-blankbehavior-01 shape): behavior-missing in the member transformed INTO, via
            // transform; the companion keeps the new type and the behaviour it was in.
            TickType changer = EType("Par", EBeh(10, "Changer", "Changer"),
                EAct(30, "Changer", "Embedded", "Transform", 1, EParam("TransformMascot", "Other"), EParam("TransformBehavior", "")));
            TickType other = EType("Other", EBeh(10, "Stand", "Stand"), EAct(30, "Stand", "Stay", null, 100),
                EBeh(11, "Bad", "BadBreed"), EAct(31, "BadBreed", "Embedded", "Breed", 1, EParam("BornCount", "0")));
            var t1 = new StringWriter();
            ParityTraceWriter w1;
            TickWorld world = EWorld(t1, out w1, changer, other);
            TickMascot m = world.CreateAtStart("m1", changer, new TickPoint(640, 680), false, "Changer");
            world.ActivateStartup();
            w1.Tick = 1;
            world.StepOnce();
            string transformError = ErrorOf(One(Errors(t1, emitted)), out message);
            Golden(golden, message, "Transform into Other: no behaviour named '' in Other");
            check(transformError == "{\"code\":\"behavior-missing\",\"lookup\":{\"imageSet\":\"Other\",\"behavior\":\"\"},\"via\":[\"transform\"]}"
                  && m.Type == other && m.Behaviour != null && m.Behaviour.Name == "Changer" && !m.Disposing,
                "parity error: a blank TransformBehavior is behavior-missing in the target member, via transform; new type and old behaviour kept ("
                + transformError + "; " + m.Type.Name + ", " + (m.Behaviour == null ? "none" : m.Behaviour.Name) + ")");

            // CH4: an Init failure after the new behaviour was assigned: the behavior event to it, then the error, and the
            // new behaviour stays (TickMascot.SetBehaviour assigns and reports before Init). Its cause is the Breed's own
            // parameter, read at Init, inside the transform wrapper.
            TickType toBad = EType("Par", EBeh(10, "Changer", "Changer"),
                EAct(30, "Changer", "Embedded", "Transform", 1, EParam("TransformMascot", "Other"), EParam("TransformBehavior", "Bad")));
            var t2 = new StringWriter();
            ParityTraceWriter w2;
            TickWorld world2 = EWorld(t2, out w2, toBad, other);
            TickMascot m2 = world2.CreateAtStart("m1", toBad, new TickPoint(640, 680), false, "Changer");
            world2.ActivateStartup();
            w2.Tick = 1;
            world2.StepOnce();
            string initError = ErrorOf(One(Errors(t2, emitted)), out message);
            Golden(golden, message, "Transform into Other: BadBreed: BornCount must be positive");
            string trace2 = t2.ToString();
            int entered = trace2.IndexOf("\"kind\":\"behavior\",\"from\":\"Changer\",\"to\":\"Bad\"", StringComparison.Ordinal);
            int faulted = trace2.IndexOf("\"kind\":\"error\"", StringComparison.Ordinal);
            check(initError == "{\"code\":\"parameter-invalid\",\"lookup\":{\"refKey\":" + JsonLine.Quote(ErrActionKey("Other", 31)) + ",\"parameter\":\"BornCount\"},\"via\":[\"transform\"]}"
                  && entered >= 0 && faulted > entered && m2.Behaviour != null && m2.Behaviour.Name == "Bad" && m2.Type == other,
                "parity error: a Transform whose new behaviour fails in Init writes the behavior event, then the error, and keeps the new behaviour ("
                + initError + "; " + (m2.Behaviour == null ? "none" : m2.Behaviour.Name) + ")");
        }

        // ---- CH9, CH10, CH19 and the startup wrapper

        private static void CheckErrorWrappers(Action<bool, string> check, List<string> golden, List<string> emitted)
        {
            string message;
            TickType par = EType("Par", EBeh(10, "Stand", "Stand"), EAct(30, "Stand", "Stay", null, 100),
                EBeh(11, "Bad", "Bad"), EAct(31, "Bad", "Stay", null, 100, EParam("Duration", "100"), EParam("Affordance", "null", "action")),
                EBeh(12, "Meet", "Meet"), EAct(32, "Meet", "Embedded", "Interact", 1, EParam("Behavior", "Ghost")));

            // CH19 and the startup wrapper: a starter whose first behaviour is missing is reported via startup, disposed,
            // never queued, and has no state at tick 0.
            var ts = new StringWriter();
            ParityTraceWriter ws;
            TickWorld startup = EWorld(ts, out ws, par);
            TickMascot failed = startup.CreateAtStart("m1", par, new TickPoint(640, 680), false, "Nope");
            startup.ActivateStartup();
            ws.States(startup);
            string startupError = ErrorOf(One(Errors(ts, emitted)), out message);
            Golden(golden, message, "the first behaviour could not be built: no behaviour named 'Nope' in Par");
            check(startupError == "{\"code\":\"behavior-missing\",\"lookup\":{\"imageSet\":\"Par\",\"behavior\":\"Nope\"},\"via\":[\"startup\"]}"
                  && failed.Disposing && startup.Mascots.Count == 0 && startup.Pending.Count == 0 && Lines(ts.ToString(), "\"type\":\"state\"").Count == 0,
                "parity error: a failed starter is reported via startup, stays disposed and has no tick-0 state (" + startupError + ")");

            // CH9: an exception that is not the host's own fault class, caught by the step: unclassified, via [].
            var t = new StringWriter();
            ParityTraceWriter w;
            TickWorld world = EWorld(t, out w, par);
            TickMascot bad = world.CreateAtStart("m1", par, new TickPoint(640, 680), false, "Bad");
            TickMascot stander = world.CreateAtStart("m2", par, new TickPoint(100, 680), false, "Stand");
            TickMascot meet = world.CreateAtStart("m3", par, new TickPoint(900, 680), false, "Stand");
            TickMascot partner = world.CreateAtStart("m4", par, new TickPoint(900, 680), false, "Stand");
            world.ActivateStartup();
            // Interact cannot start before its companion has a manager (TickInteract.HasNext), so m3 enters it once active.
            meet.SetBehaviour(world.BuildBehaviourByName(par, "Meet", meet), "forced");
            w.Tick = 1;
            world.StepOnce();
            List<string> stepErrors = Errors(t, emitted);
            string internalLine = stepErrors.Find(l => l.Contains("\"sid\":\"m1\""));
            string internalError = ErrorOf(internalLine, out message);
            Golden(golden, message, "internal fault: NullReferenceException: Object reference not set to an instance of an object.");
            check(internalError == "{\"code\":\"unclassified\",\"lookup\":null,\"via\":[]}" && bad.Disposing,
                "parity error: a fault that is not the host's own class, caught by the step, is unclassified with via [] (" + internalError + ")");

            // CH10: interact, press and release each label their own wrapper, with their own consequence.
            string interactError = ErrorOf(stepErrors.Find(l => l.Contains("\"sid\":\"m3\"")), out message);
            Golden(golden, message, "Meet: its Behavior could not be entered: no behaviour named 'Ghost' in Par");
            check(interactError == "{\"code\":\"behavior-missing\",\"lookup\":{\"imageSet\":\"Par\",\"behavior\":\"Ghost\"},\"via\":[\"interact\"]}"
                  && !meet.Disposing && !partner.Disposing,
                "parity error: Interact's closing Behavior is via interact, and the companion carries on (" + interactError + ")");

            w.Tick = 2;
            int before = Lines(t.ToString(), "\"kind\":\"error\"").Count;
            world.Press(stander, new TickPoint(10, 10));
            List<string> pressed = Lines(t.ToString(), "\"kind\":\"error\"");
            string pressError = pressed.Count == before + 1 ? ErrorOf(pressed[pressed.Count - 1], out message) : null;
            Golden(golden, message, "no behaviour named 'Dragged' in Par");
            check(pressError == "{\"code\":\"behavior-missing\",\"lookup\":{\"imageSet\":\"Par\",\"behavior\":\"Dragged\"},\"via\":[\"press\"]}" && stander.Disposing,
                "parity error: a press whose Dragged is missing is via press, and the companion is disposed (" + pressError + ")");

            partner.Dragging = true;
            world.Release(partner);
            List<string> released = Lines(t.ToString(), "\"kind\":\"error\"");
            string releaseError = released.Count == before + 2 ? ErrorOf(released[released.Count - 1], out message) : null;
            Golden(golden, message, "no behaviour named 'Thrown' in Par");
            check(releaseError == "{\"code\":\"behavior-missing\",\"lookup\":{\"imageSet\":\"Par\",\"behavior\":\"Thrown\"},\"via\":[\"release\"]}" && partner.Disposing,
                "parity error: a release whose Thrown is missing is via release, and the companion is disposed (" + releaseError + ")");
            emitted.AddRange(released.GetRange(before, released.Count - before));
        }

        // ---- CH8, CH11: faults reported where they arise

        private static void CheckErrorWhereItArises(Action<bool, string> check, List<string> golden, List<string> emitted)
        {
            string message;
            var condition = EAct(30, "Picky", "Stay", null, 100, EParam("Condition", "yes"));
            var synthetic = EAct(31, "Odd", "Stay", null, 100);
            synthetic.Act.Variants.Variant[0].When = "1";
            TickType par = EType("Par", EBeh(10, "Picky", "Picky"), condition, EBeh(11, "Odd", "Odd"), synthetic);
            var t = new StringWriter();
            ParityTraceWriter w;
            TickWorld world = EWorld(t, out w, par);
            world.CreateAtStart("m1", par, new TickPoint(640, 680), false, "Picky");
            world.CreateAtStart("m2", par, new TickPoint(100, 680), false, "Odd");
            world.ActivateStartup();
            w.Tick = 1;
            world.StepOnce();
            List<string> errors = Errors(t, emitted);

            // CH11 and CH8's built-in leg: a guard read that is not a boolean, reported where it arises (via []), typed by
            // the reading action's refKey and the canonical key it read.
            string builtin = ErrorOf(errors.Find(l => l.Contains("\"sid\":\"m1\"")), out message);
            Golden(golden, message, "Picky: Condition is not a boolean (yes)");
            check(builtin == "{\"code\":\"parameter-invalid\",\"lookup\":{\"refKey\":" + JsonLine.Quote(ErrActionKey("Par", 30)) + ",\"parameter\":\"Condition\"},\"via\":[]}",
                "parity error: a fault reported where it arises has via [], typed parameter-invalid by the action's refKey and the key read (" + builtin + ")");

            // CH8's synthetic leg: a variant condition is no authored parameter, so it is unclassified.
            string variant = ErrorOf(errors.Find(l => l.Contains("\"sid\":\"m2\"")), out message);
            Golden(golden, message, "Odd: variant condition is not a boolean (1)");
            check(variant == "{\"code\":\"unclassified\",\"lookup\":null,\"via\":[]}",
                "parity error: a variant condition's fault names no synthetic parameter: unclassified (" + variant + ")");

            // CH7: a constructor fault reached through an ActionReference carries no reference context into construction:
            // unclassified, never the declaration's key.
            var mover = EAct(32, "Mover", "Embedded", "MoveWithTurn", 1);
            var seq = EAct(33, "Seq", "Sequence", null, 1);
            seq.Act.Variants = null;
            seq.Act.Children = new XmlData.ChildrenNode { Mode = "sequence", Child = new[] { new XmlData.CompositeChildNode { Animation = "Mover" } } };
            TickType refs = EType("Ref", EBeh(10, "Go", "Seq"), mover, seq);
            refs.ActionsById[33].Children[0].RefKey = ErrActionKey("Ref", 33) + "/ActionReference[1]";
            var tr = new StringWriter();
            ParityTraceWriter wr;
            TickWorld refWorld = EWorld(tr, out wr, refs);
            refWorld.CreateAtStart("m1", refs, new TickPoint(640, 680), false, "Go");
            string ctor = ErrorOf(One(Errors(tr, emitted)), out message);
            Golden(golden, message, "the first behaviour could not be built: action 'Mover' (MoveWithTurn) needs at least two variants");
            check(ctor == "{\"code\":\"unclassified\",\"lookup\":null,\"via\":[\"startup\"]}",
                "parity error: a constructor fault reached through an ActionReference is unclassified, never the declaration's key (" + ctor + ")");
        }

        // ---- CH18

        private static void CheckErrorNoEvent(Action<bool, string> check)
        {
            // A missing BornMascot or TransformMascot member falls back to the current one with no observer event (the
            // source says nothing), so no error is written for it.
            StringWriter born = RunBirth(EParam("BornBehavior", "Breeder"), "Ghost");
            TickType changer = EType("Par", EBeh(10, "Changer", "Changer"),
                EAct(30, "Changer", "Embedded", "Transform", 1, EParam("TransformMascot", "Ghost"), EParam("TransformBehavior", "Changer")));
            var t = new StringWriter();
            ParityTraceWriter w;
            TickWorld world = EWorld(t, out w, changer);
            world.CreateAtStart("m1", changer, new TickPoint(640, 680), false, "Changer");
            world.ActivateStartup();
            w.Tick = 1;
            world.StepOnce();
            check(born.ToString().Contains("\"kind\":\"birth-request\"") && t.ToString().Contains("\"kind\":\"transform\"")
                  && Lines(born.ToString(), "\"kind\":\"error\"").Count == 0 && Lines(t.ToString(), "\"kind\":\"error\"").Count == 0,
                "parity error: a missing BornMascot or TransformMascot member falls back with no error event ("
                + Lines(born.ToString(), "\"kind\":\"error\"").Count + ", " + Lines(t.ToString(), "\"kind\":\"error\"").Count + ")");
        }
    }
}
