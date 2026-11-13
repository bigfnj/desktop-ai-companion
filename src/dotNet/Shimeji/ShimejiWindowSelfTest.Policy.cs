using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DesktopAICompanion.Shimeji
{
    internal static partial class ShimejiWindowSelfTest
    {
        /// <summary>
        /// burndown/host-policy-1, N-parity-1107-03 C3 (signed revision H :78-80, :150, :158): ParityRun.Run logs the engine
        /// this process loaded and, below the command, the synthetic label; a self-hash it cannot read is the named
        /// build-witness-unreadable refusal; and the policy-blind birth witness carries every birth decision at the real
        /// comparison and the limit's write record, with the header's runId. Also the product writer of that record:
        /// TickHost.Step writes the cap through the CompanionBudget writer. The 17-birth pack is a unit control only; the
        /// graded cap-at variants (H :188-189) need the admitted input and stay closure work. Every pet here is generated.
        /// </summary>
        private static void CheckParityPolicy(Action<bool, string> check, string root, StringBuilder log)
        {
            try
            {
                CheckParityPolicyWitness(check, root, log);
            }
            catch (Exception e)
            {
                check(false, "parity policy: the birth witness and engine checks ran without an exception: " + e);
            }
            try
            {
                CheckParityPolicyHostWriter(check, log);
            }
            catch (Exception e)
            {
                check(false, "parity policy: the host writer check ran without an exception: " + e);
            }
        }

        /// <summary>ParDocument's Walk (node 30) made a Breed of BornCount 17 children of its own type that enter Walk, its
        /// three poses two ticks each, so the parent's decisions fall at tick 6 and no child ticks long enough to breed in
        /// a run of 6 or 8 ticks. ParMapJson's map binds it unchanged, since the poses are the same.</summary>
        private static XmlData.RootNode PolicyBreedDocument(string set)
        {
            XmlData.RootNode doc = ParDocument(false);
            doc.Header.Title = set;
            doc.Header.Features += " lifecycle";
            foreach (XmlData.AnimationNode n in doc.Animations.Animation)
            {
                if (n.Id != 30) continue;
                n.Act.Type = "Embedded";
                n.Act.Class = "Breed";
                n.Act.Params = new[]
                {
                    new XmlData.ParamNode { Name = "BornCount", Value = "17", Cache = "none" },
                    new XmlData.ParamNode { Name = "BornBehavior", Value = "Walk", Cache = "none" },
                };
                foreach (XmlData.PoseNode p in n.Act.Variants.Variant[0].Pose) p.Ticks = 2;
            }
            return doc;
        }

        /// <summary>A generated scenario/1 over <paramref name="mascots"/> (a JSON array's members), with the pack folder it
        /// names, written beside the pets folder.</summary>
        private static string PolicyScenario(string root, string id, int ticks, string mascots)
        {
            string pack = Path.Combine(root, "pack");
            if (!Directory.Exists(pack))
            {
                Directory.CreateDirectory(pack);
                File.WriteAllText(Path.Combine(pack, "readme.txt"), "a generated stand-in pack\n", new UTF8Encoding(false));
            }
            string scenario = Path.Combine(root, id + ".json");
            File.WriteAllText(scenario, "{\"schema\":\"shimeji-parity-scenario/1\",\"id\":" + JsonLine.Quote(id) + ",\"description\":\"generated\","
                + "\"pack\":\"pack\",\"screens\":[{\"x\":0,\"y\":0,\"w\":1280,\"h\":720}],\"workArea\":{\"x\":0,\"y\":0,\"w\":1280,\"h\":680},"
                + "\"dpiScale\":1.0,\"ticks\":" + ticks.ToString(CultureInfo.InvariantCulture) + ",\"rng\":{\"kind\":\"none\"},\"settings\":{},"
                + "\"mascots\":[" + mascots + "],\"activeWindow\":[{\"tick\":0,\"visible\":false}],\"input\":[{\"tick\":0,\"cursor\":[100,100]}]}",
                new UTF8Encoding(false));
            return scenario;
        }

        /// <summary>One converted pet folder per (set, document), each with ParMapJson's map for it.</summary>
        private static string PolicyPets(string root, string name, params KeyValuePair<string, XmlData.RootNode>[] members)
        {
            string pets = Path.Combine(root, name);
            foreach (KeyValuePair<string, XmlData.RootNode> member in members)
            {
                string folder = Path.Combine(pets, member.Key);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "animations.xml"), Serialize(member.Value, Color.Red, Color.Blue), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(folder, "parity-map.json"), ParMapJson(member.Key, false, ParPoses(member.Key, false)), new UTF8Encoding(false));
            }
            return pets;
        }

        /// <summary>The first log line starting with <paramref name="prefix"/>, or null.</summary>
        private static string PolicyLine(string text, string prefix)
        {
            foreach (string line in text.Replace("\r", "").Split('\n'))
                if (line.StartsWith(prefix, StringComparison.Ordinal)) return line;
            return null;
        }

        /// <summary>The runId a closure line ends with, or null.</summary>
        private static string PolicyRunId(string line)
        {
            if (line == null) return null;
            int at = line.LastIndexOf(ParityRunLog.RunIdField, StringComparison.Ordinal);
            return at < 0 ? null : line.Substring(at + ParityRunLog.RunIdField.Length);
        }

        private static string PolicyHeaderRunId(string trace)
        {
            if (trace == null) return null;
            int end = trace.IndexOf('\n');
            using (JsonDocument header = JsonDocument.Parse(end < 0 ? trace : trace.Substring(0, end)))
            {
                JsonElement id;
                return header.RootElement.TryGetProperty("runId", out id) ? id.GetString() : null;
            }
        }

        private static void CheckParityPolicyWitness(Action<bool, string> check, string root, StringBuilder log)
        {
            Directory.CreateDirectory(root);
            var build = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "commit", "selftest" }, { "exeSha256", new string('0', 64) }, { "engineSha256", new string('1', 64) }, { "converterSha256", new string('2', 64) },
            };
            string pets = PolicyPets(root, "pets-alpha", new KeyValuePair<string, XmlData.RootNode>("Alpha", PolicyBreedDocument("Alpha")));
            string scenario = PolicyScenario(root, "S-policy-cap", 8,
                "{\"sid\":\"m1\",\"imageSet\":\"Alpha\",\"behavior\":\"Walk\",\"anchor\":[640,680],\"lookRight\":false}");
            string trace = Path.Combine(root, "cap.jsonl");
            var runLog = new StringWriter();
            int exit = ParityRun.Run(scenario, pets, trace, build, runLog);
            string text = runLog.ToString();
            log.Append("LOG: parity policy run: " + text);
            string traceText = File.Exists(trace) ? File.ReadAllText(trace) : null;
            string witness = PolicyLine(text, ParityRunLog.BirthWitness + " ");

            // The decisions, as the comparison made them: attempt k compares 1 active plus k-1 pending against 16.
            var expected = new List<string>();
            for (int k = 1; k <= 17; k++)
                expected.Add("t6 m1 #" + k + " 16 vs " + (k <= 15 ? "1+" + (k - 1) + " admit" : "1+15 refuse"));
            string decisions = witness == null ? null : Between(witness, " decisions ", ParityRunLog.RunIdField);
            check(exit == 0 && decisions != null && decisions.StartsWith("17 [t6 m1 #1 ", StringComparison.Ordinal)
                  && System.Text.RegularExpressions.Regex.Matches(decisions, @"t\d+ ").Count == 17
                  && System.Text.RegularExpressions.Regex.Matches(decisions, @"t6 m1 #").Count == 17,
                "parity policy: birth witness 17 decisions at tick 6 for m1 (exit " + exit + ", " + (witness ?? "no witness line") + ")");
            check(decisions == "17 [" + string.Join(", ", expected) + "]",
                "parity policy: attempts 1..15 admit at population k, 16 and 17 refuse at 16 vs 16 (" + (decisions ?? "no witness line") + ")");
            check(witness != null && witness.StartsWith(ParityRunLog.BirthWitness + " limit 16 limitWrites 0 none decisions ", StringComparison.Ordinal),
                "parity policy: limit writes none (" + (witness ?? "no witness line") + ")");

            // WITNESS: the same formatter on a world with one recorded write prints that write, its writer, base and
            // deduction, so "limitWrites 0 none" above is read from a formatter that can say otherwise.
            TickWorld written = ParWorld(null);
            written.SetMaximumCompanions(18 - 2, TickLimitWriter.WithParts("budget", "an installed budget", 18, 2));
            string formatted = ParityRunLog.BirthWitnessLine(new List<TickBirthDecision>(), written.LimitWrites, written.MaximumCompanions, "w");
            check(formatted == ParityRunLog.BirthWitness + " limit 16 limitWrites 1 [16 by budget (an installed budget; base 18 deduction 2) x1 from write 0] decisions 0 none runId w",
                "parity policy: WITNESS the birth witness of a world with one limit write prints that write with base 18 and deduction 2 (" + formatted + ")");

            // The engine this process loaded, hashed here independently of the run.
            string enginePath = typeof(TickWorld).Assembly.Location, exePath = Environment.ProcessPath;
            string engineLine = PolicyLine(text, ParityRunLog.LoadedEngine + " ");
            string wantEngine = ParityRunLog.LoadedEngine + " engineSha256 " + ParityRun.Sha256(File.ReadAllBytes(enginePath))
                + " exeSha256 " + ParityRun.Sha256(File.ReadAllBytes(exePath)) + " enginePath " + enginePath + " exePath " + exePath + ParityRunLog.RunIdField;
            check(engineLine != null && engineLine.StartsWith(wantEngine, StringComparison.Ordinal),
                "parity policy: loaded engine names the loaded assembly and its sha256 (" + (engineLine ?? "no loaded-engine line") + ")");
            string syntheticLine = PolicyLine(text, ParityRunLog.SyntheticWitness);
            check(syntheticLine != null && engineLine != null && PolicyRunId(syntheticLine) == PolicyRunId(engineLine),
                "parity policy: self-test runs carry the synthetic label (" + (syntheticLine ?? "no synthetic line") + ")");
            string headerRunId = PolicyHeaderRunId(traceText);
            check(headerRunId != null && headerRunId.Length == 36 && PolicyRunId(engineLine) == headerRunId && PolicyRunId(witness) == headerRunId,
                "parity policy: the header, the loaded-engine line and the birth witness carry one runId (header " + headerRunId
                + ", engine " + PolicyRunId(engineLine) + ", witness " + PolicyRunId(witness) + ")");

            // H :80: a self-hash this process cannot read is the named producer refusal, exit 2 and no trace, never an
            // unhandled exception. No real process shows an empty Location, so the path provider is handed one.
            Func<string, ParityEnginePaths, string> refused = (name, paths) =>
            {
                string refusedTrace = Path.Combine(root, name + ".jsonl");
                var refusedLog = new StringWriter();
                string refusedExit;
                try { refusedExit = ParityRun.Run(scenario, pets, refusedTrace, build, refusedLog, false, paths).ToString(CultureInfo.InvariantCulture); }
                catch (Exception e) { refusedExit = "threw " + e.GetType().Name; refusedLog.Write(e.Message); }
                return "exit " + refusedExit + ", trace " + (File.Exists(refusedTrace) ? "left" : "none") + ", " + refusedLog.ToString().Trim();
            };
            string empty = refused("empty-engine", new ParityEnginePaths(() => exePath, () => ""));
            check(empty.StartsWith("exit 2, trace none, " + ParityRunLog.BuildWitnessUnreadable + ": the engine assembly path is empty;", StringComparison.Ordinal),
                "parity policy: an empty engine path gives build-witness-unreadable, exit 2, no trace (" + empty + ")");
            string nullExe = refused("null-exe", new ParityEnginePaths(() => null, () => enginePath));
            string missing = refused("missing-engine", new ParityEnginePaths(() => exePath, () => Path.Combine(root, "no-such-engine.dll")));
            check(nullExe.StartsWith("exit 2, trace none, " + ParityRunLog.BuildWitnessUnreadable + ": the process EXE path is null;", StringComparison.Ordinal)
                  && missing.StartsWith("exit 2, trace none, " + ParityRunLog.BuildWitnessUnreadable + ": the engine assembly path is '", StringComparison.Ordinal),
                "parity policy: a null process path or an unreadable engine file gives build-witness-unreadable too (" + nullExe + " | " + missing + ")");
        }

        /// <summary>The text between the first <paramref name="start"/> and the last <paramref name="end"/>, or null.</summary>
        private static string Between(string text, string start, string end)
        {
            int a = text.IndexOf(start, StringComparison.Ordinal), b = text.LastIndexOf(end, StringComparison.Ordinal);
            return a < 0 || b < a + start.Length ? null : text.Substring(a + start.Length, b - a - start.Length);
        }

        /// <summary>
        /// The product writer of the limit (H :158, review of round 2): TickHost.Step writes the world's cap through the
        /// recording setter with TickLimitWriter.CompanionBudget, an opaque writer, so the write record names the budget
        /// with base and deduction unavailable. TickHost is not in CoreTests, so this is where a writer swapped for the
        /// direct one would show. No companion is spawned and no window opened: the step runs on an empty world.
        /// </summary>
        private static void CheckParityPolicyHostWriter(Action<bool, string> check, StringBuilder log)
        {
            TickHost host = null;
            try
            {
                host = new TickHost(t => null, form => { }, message => log.AppendLine("LOG: " + message)) { RenderOpacity = 0.0 };
                host.Environment.SearchesWindows = false;
                host.Step();
                long unbudgeted = host.World.LimitWrites.Count;
                check(unbudgeted == 0, "parity policy: WITNESS a host with no budget writes no cap at a step (" + unbudgeted + " writes)");
                host.CompanionBudget = () => 14;
                host.Step();
                host.Step();
                IReadOnlyList<TickLimitWrite> runs = host.World.LimitWrites.Runs;
                TickLimitWrite run = runs.Count == 1 ? runs[0] : null;
                check(run != null && run.Value == 14 && run.Count == 2 && ReferenceEquals(run.Writer, TickLimitWriter.CompanionBudget)
                      && !run.Writer.PartsKnown && host.World.MaximumCompanions == 14,
                    "parity policy: TickHost.Step writes the cap through the CompanionBudget writer, base and deduction unavailable ("
                    + (run == null ? runs.Count + " runs" : run.Value + " by " + run.Writer + " x" + run.Count) + ")");
            }
            finally
            {
                if (host != null) host.Dispose();
            }
        }
    }
}
