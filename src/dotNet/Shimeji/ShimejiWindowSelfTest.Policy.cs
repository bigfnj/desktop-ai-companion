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
        /// names, written beside the pets folder; directed (rng none) unless <paramref name="rng"/> says otherwise.</summary>
        private static string PolicyScenario(string root, string id, int ticks, string mascots, string rng = "{\"kind\":\"none\"}")
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
                + "\"dpiScale\":1.0,\"ticks\":" + ticks.ToString(CultureInfo.InvariantCulture) + ",\"rng\":" + rng + ",\"settings\":{},"
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

            // Codex's closure ruling Q-c: a run the core run refuses after its entry still names its engine, line 1, with a
            // runId no header carries, ahead of the refusal. A scenario-invalid refusal first.
            string invalidScenario = Path.Combine(root, "S-policy-invalid.json");
            File.WriteAllText(invalidScenario, File.ReadAllText(scenario).Replace("\"ticks\":8,", "\"ticks\":-1,"), new UTF8Encoding(false));
            string invalidTrace = Path.Combine(root, "invalid.jsonl");
            var invalidLog = new StringWriter();
            int invalidExit = ParityRun.Run(invalidScenario, pets, invalidTrace, build, invalidLog);
            string invalidText = invalidLog.ToString();
            log.Append("LOG: parity policy invalid: " + invalidText);
            string invalidEngine = PolicyLine(invalidText, ParityRunLog.LoadedEngine + " "), invalidRunId = PolicyRunId(invalidEngine);
            string invalidRefusal = "refused " + invalidScenario + ": scenario-invalid: ticks is not a non-negative integer";
            int engineAt = invalidEngine == null ? -1 : invalidText.IndexOf(invalidEngine, StringComparison.Ordinal);
            check(invalidExit == 2 && !File.Exists(invalidTrace) && invalidEngine != null && invalidEngine.StartsWith(wantEngine, StringComparison.Ordinal)
                  && invalidRunId != null && invalidRunId.Length == 36 && invalidRunId != headerRunId
                  && engineAt >= 0 && invalidText.IndexOf(invalidRefusal, StringComparison.Ordinal) > engineAt,
                "parity policy: a scenario-invalid refusal logs the loaded-engine line first with its own runId and writes no header (exit "
                + invalidExit + ", trace " + (File.Exists(invalidTrace) ? "left" : "none") + ", " + invalidText.Trim().Replace("\r", "").Replace("\n", " | ") + ")");

            // An occupied trace path: the output claim keeps its precedence and refuses before the core run, so its refusal
            // names no engine; a file that appears after the claim is refused at the open, after the core run's entry, so
            // that refusal names the engine with a runId and no header is written. Neither occupant is touched.
            string claimed = Path.Combine(root, "occupied-at-claim.jsonl");
            File.WriteAllText(claimed, "sentinel\n", new UTF8Encoding(false));
            var claimLog = new StringWriter();
            int claimExit = ParityRun.Run(scenario, pets, claimed, build, claimLog);
            string late = Path.Combine(root, "occupied-at-open.jsonl");
            ParityRun.ScenarioFile lateFile = ParityRun.ScenarioFile.Read(scenario);
            var lateLog = new StringWriter();
            int lateExit = -1;
            if (ParityRun.TraceClaim.Claim(lateFile, pets, late, null, lateLog) != null)
            {
                File.WriteAllText(late, "occupant\n", new UTF8Encoding(false));
                lateExit = ParityRun.Run(lateFile, pets, late, build, lateLog, false);
            }
            string claimText = claimLog.ToString(), lateText = lateLog.ToString();
            log.Append("LOG: parity policy occupied: " + claimText + lateText);
            string lateEngine = PolicyLine(lateText, ParityRunLog.LoadedEngine + " "), lateRunId = PolicyRunId(lateEngine);
            check(claimExit == 2 && PolicyLine(claimText, ParityRunLog.LoadedEngine) == null && File.ReadAllText(claimed) == "sentinel\n"
                  && lateExit == 2 && lateEngine != null && lateEngine.StartsWith(wantEngine, StringComparison.Ordinal) && lateRunId != null && lateRunId.Length == 36
                  && lateText.Contains("is already there") && File.ReadAllText(late) == "occupant\n",
                "parity policy: a trace path occupied at the open logs the loaded-engine line, occupied at the claim does not, no header either way (exit "
                + claimExit + " and " + lateExit + ", " + (claimText + lateText).Trim().Replace("\r", "").Replace("\n", " | ") + ")");
        }

        /// <summary>
        /// burndown/host-policy-1, N-parity-1107-03 C6 (signed revision H step 3, :45, :49; K5 agreed with Codex): a legacy
        /// scenario whose read, syntax or eagerly decoded schema strings fail is the named producer-input diagnostic
        /// "scenario-input-unreadable" with its detail, exit 2 and no trace, never an exception out of the command: an
        /// escaped unpaired surrogate in the schema value (25 raw bytes, so no length shortcut tells it from the
        /// marker), a start sid, rng.kind or a start behavior. A pack value the claim cannot decode is no usable pack
        /// folder there, and the run still refuses the scenario by its own line. An accepted scenario/1 is unchanged by a
        /// deep unused member within .NET's depth. Every pet here is generated.
        /// </summary>
        private static void CheckParityInputDiagnostics(Action<bool, string> check, string root, StringBuilder log)
        {
            try
            {
                CheckParityInputDiagnosticLegs(check, root, log);
            }
            catch (Exception e)
            {
                check(false, "parity policy: the input diagnostic checks ran without an exception: " + e);
            }
        }

        private static void CheckParityInputDiagnosticLegs(Action<bool, string> check, string root, StringBuilder log)
        {
            Directory.CreateDirectory(root);
            var build = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "commit", "selftest" }, { "exeSha256", new string('0', 64) }, { "engineSha256", new string('1', 64) }, { "converterSha256", new string('2', 64) },
            };
            string pets = PolicyPets(root, "pets-input", new KeyValuePair<string, XmlData.RootNode>("Par", ParDocument(false)));
            string plain = PolicyScenario(root, "S-input-plain", 4,
                "{\"sid\":\"m1\",\"imageSet\":\"Par\",\"behavior\":\"Walk\",\"anchor\":[640,680],\"lookRight\":false}");
            string plainText = File.ReadAllText(plain);

            // One run on a scenario file written from the plain one with one textual change: exit, whether a trace was
            // left, and the log (an exception out of the run is caught and named, never passed).
            Func<string, string, string[]> run = (name, text) =>
            {
                string scenario = Path.Combine(root, name + ".json");
                if (text != null) File.WriteAllText(scenario, text, new UTF8Encoding(false));
                string trace = Path.Combine(root, name + ".jsonl");
                var runLog = new StringWriter();
                string exit;
                try { exit = ParityRun.Run(scenario, pets, trace, build, runLog).ToString(CultureInfo.InvariantCulture); }
                catch (Exception e) { exit = "threw " + e.GetType().Name; runLog.Write(e.Message); }
                // Always ends the line: a run that threw left its message unterminated, and the next check's FAIL line
                // must start a line of its own.
                log.AppendLine("LOG: parity input " + name + ": " + runLog.ToString().TrimEnd());
                // Codex Q-c: a refusal after the core run's entry has the loaded-engine line first; it is split off, so the
                // refusal itself is read from the start of what follows, and a refusal without it is not named.
                string logText = runLog.ToString().Trim(), engineLine = null;
                if (logText.StartsWith(ParityRunLog.LoadedEngine + " ", StringComparison.Ordinal))
                {
                    int end = logText.IndexOf('\n');
                    engineLine = end < 0 ? logText : logText.Substring(0, end).TrimEnd('\r');
                    logText = end < 0 ? "" : logText.Substring(end + 1);
                }
                return new[] { exit, File.Exists(trace) ? File.ReadAllText(trace) : null, logText, scenario, engineLine };
            };
            Func<string[], string, bool> named = (r, want) => r[0] == "2" && r[1] == null && r[4] != null && r[2].StartsWith(want, StringComparison.Ordinal);
            Func<string[], string> said = r => "exit " + r[0] + ", trace " + (r[1] == null ? "none" : "left") + ", " + r[2];
            Func<string, string, string> unreadable = (scenario, field) => ParityRun.ScenarioInputUnreadable + " schema-string: refused " + scenario
                + ": the scenario's '" + field + "' string cannot be decoded: ";

            // 25 raw bytes, the length of the marker and of scenario/1: \ud800 (6) + x (1) + -parity-scenario/1 (18).
            const string surrogateSchema = "\\ud800x-parity-scenario/1";
            string[] schema = run("S-input-schema", plainText.Replace("shimeji-parity-scenario/1", surrogateSchema));
            check(surrogateSchema.Length == 25 && named(schema, unreadable(schema[3], "schema")),
                "parity policy: a 25-raw-byte surrogate schema value gives the named schema-string diagnostic, exit 2, no trace (" + said(schema) + ")");
            string[] sid = run("S-input-sid", plainText.Replace("\"sid\":\"m1\"", "\"sid\":\"\\ud800\""));
            check(named(sid, unreadable(sid[3], "sid")),
                "parity policy: a surrogate sid gives the named diagnostic, exit 2, no trace (" + said(sid) + ")");
            string[] kind = run("S-input-rng-kind", plainText.Replace("\"rng\":{\"kind\":\"none\"}", "\"rng\":{\"kind\":\"\\udc00\"}"));
            check(named(kind, unreadable(kind[3], "rng.kind")),
                "parity policy: a surrogate rng.kind gives the named diagnostic, exit 2, no trace (" + said(kind) + ")");
            string[] behavior = run("S-input-behavior", plainText.Replace("\"behavior\":\"Walk\"", "\"behavior\":\"W\\ud800\""));
            check(named(behavior, unreadable(behavior[3], "behavior")),
                "parity policy: a surrogate start behavior gives the named diagnostic, exit 2, no trace (" + said(behavior) + ")");

            // The read fault and the syntax fault: the token in front, the line as it was after it.
            string[] absent = run("S-input-absent", null);
            string[] syntax = run("S-input-syntax", plainText.Substring(0, plainText.Length - 1));
            check(named(absent, ParityRun.ScenarioInputUnreadable + " read: refused " + absent[3] + ": the scenario cannot be read: ")
                  && named(syntax, ParityRun.ScenarioInputUnreadable + " syntax: refused " + syntax[3] + ": scenario-invalid: the scenario is not JSON: "),
                "parity policy: the read fault and the syntax fault carry the token (" + said(absent) + " | " + said(syntax) + ")");

            // Codex's correction of round 5 (2026-11-13): no whole-file strict UTF-8 prepass, so one 0xFF byte inside the
            // description, a string no reader decodes, runs as it did at the base: exit 0 and a complete trace, the plain
            // run's records after the header (signed revision H :49, a legacy file the old reader accepts stays accepted).
            byte[] badBytes = new UTF8Encoding(false).GetBytes(plainText.Replace("\"description\":\"generated\"", "\"description\":\"gen#rated\""));
            int hash = Array.IndexOf(badBytes, (byte)'#');
            badBytes[hash] = 0xFF;
            File.WriteAllBytes(Path.Combine(root, "S-input-utf8.json"), badBytes);
            string[] utf8 = run("S-input-utf8", null);
            string[] utf8Plain = run("S-input-utf8-plain", plainText);
            Func<string, string> afterHeader = t => t == null || t.IndexOf('\n') < 0 ? null : t.Substring(t.IndexOf('\n') + 1);
            check(hash > 0 && Array.LastIndexOf(badBytes, (byte)'#') < 0 && utf8[0] == "0" && utf8Plain[0] == "0" && utf8[1] != null
                  && utf8[1].Contains("\"status\":\"complete\"") && afterHeader(utf8[1]) != null && afterHeader(utf8[1]) == afterHeader(utf8Plain[1]),
                "parity policy: invalid UTF-8 in a string no reader decodes runs as at the base, exit 0, the plain run's records (" + said(utf8) + ")");

            // A pack value with an unpaired surrogate: no usable pack folder at the claim (no exception there), and the run
            // still refuses the scenario by its own scenario-invalid line.
            string[] pack = run("S-input-pack", plainText.Replace("\"pack\":\"pack\"", "\"pack\":\"pa\\ud800ck\""));
            check(pack[0] == "2" && pack[1] == null && pack[2].StartsWith("refused " + pack[3] + ": scenario-invalid: ", StringComparison.Ordinal),
                "parity policy: a surrogate pack value is no usable pack folder at the claim and the run still refuses it, exit 2, no trace (" + said(pack) + ")");

            // WITNESS: the accepted scenario/1 runs, and the same scenario with a deep unused member (60 nested arrays, inside
            // JsonDocument's default depth of 64) writes the same records after its header.
            var deep = new StringBuilder();
            for (int i = 0; i < 60; i++) deep.Append('[');
            for (int i = 0; i < 60; i++) deep.Append(']');
            string[] accepted = run("S-input-accepted", plainText);
            string[] deepRun = run("S-input-deep", plainText.Replace("\"description\":\"generated\",", "\"description\":\"generated\",\"unused\":" + deep + ","));
            Func<string, string> body = t => t == null || t.IndexOf('\n') < 0 ? null : t.Substring(t.IndexOf('\n') + 1);
            check(accepted[0] == "0" && deepRun[0] == "0" && body(accepted[1]) != null && body(accepted[1]) == body(deepRun[1])
                  && accepted[1].Contains("\"status\":\"complete\""),
                "parity policy: WITNESS an accepted scenario/1 is unchanged, a deep unused value included (exit " + accepted[0] + " and " + deepRun[0]
                + ", records " + (body(accepted[1]) != null && body(accepted[1]) == body(deepRun[1]) ? "equal" : "differ") + ")");
        }

        /// <summary>
        /// burndown/host-policy-1, N-parity-1107-03 C7 (signed revision H :45, :56-64, :230-232): a scenario/2 candidate, by
        /// the scan facts taken at the single read, reaches the registry drift self-test after the output claim, and under
        /// the not-adopted production table every candidate is the named registry-integrity internal defect, exit 2 and no
        /// trace: both duplicate-schema orders, an escaped marker, and a candidate whose pack value cannot be decoded. An
        /// occupied trace path is refused first, with no registry line. A byte order mark stops the scan, so a BOM-prefixed
        /// scenario/2 takes the legacy route. Then the drift self-test's own checks, on the pure Verify with tables built by
        /// hand: no table seam ships (Codex 2026-11-13, H :64). Every pet here is generated.
        /// </summary>
        private static void CheckParityRegistryRouting(Action<bool, string> check, string root, StringBuilder log)
        {
            try
            {
                CheckParityRegistryRoutingLegs(check, root, log);
            }
            catch (Exception e)
            {
                check(false, "parity policy: the registry routing checks ran without an exception: " + e);
            }
            try
            {
                CheckParityRegistryDrift(check);
            }
            catch (Exception e)
            {
                check(false, "parity policy: the registry drift checks ran without an exception: " + e);
            }
        }

        private static void CheckParityRegistryRoutingLegs(Action<bool, string> check, string root, StringBuilder log)
        {
            Directory.CreateDirectory(root);
            var build = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "commit", "selftest" }, { "exeSha256", new string('0', 64) }, { "engineSha256", new string('1', 64) }, { "converterSha256", new string('2', 64) },
            };
            string pets = PolicyPets(root, "pets-registry", new KeyValuePair<string, XmlData.RootNode>("Par", ParDocument(false)));
            string plainText = File.ReadAllText(PolicyScenario(root, "S-registry-plain", 4,
                "{\"sid\":\"m1\",\"imageSet\":\"Par\",\"behavior\":\"Walk\",\"anchor\":[640,680],\"lookRight\":false}"));
            const string v1 = "\"schema\":\"shimeji-parity-scenario/1\"", v2 = "\"schema\":\"shimeji-parity-scenario/2\"";

            // One run on the given bytes: exit (or what it threw), the trace if one was left, the log, the scenario path.
            Func<string, byte[], string[]> run = (name, bytes) =>
            {
                string scenario = Path.Combine(root, name + ".json");
                File.WriteAllBytes(scenario, bytes);
                string trace = Path.Combine(root, name + ".jsonl");
                var runLog = new StringWriter();
                string exit;
                try { exit = ParityRun.Run(scenario, pets, trace, build, runLog).ToString(CultureInfo.InvariantCulture); }
                catch (Exception e) { exit = "threw " + e.GetType().Name; runLog.Write(e.Message); }
                log.AppendLine("LOG: parity registry " + name + ": " + runLog.ToString().TrimEnd());
                return new[] { exit, File.Exists(trace) ? File.ReadAllText(trace) : null, runLog.ToString(), scenario };
            };
            Func<string, byte[]> utf8 = text => new UTF8Encoding(false).GetBytes(text);
            Func<string[], string> said = r => "exit " + r[0] + ", trace " + (r[1] == null ? "none" : "left") + ", " + r[2].Trim().Replace("\r", "").Replace("\n", " | ");
            // The candidate outcome under the not-adopted table: the loaded-engine line, then the registry-integrity
            // internal defect with the same runId, exit 2, no trace, and no legacy refusal.
            Func<string[], string> defect = r =>
            {
                string engine = PolicyLine(r[2], ParityRunLog.LoadedEngine + " "), integrity = PolicyLine(r[2], ParityRunLog.RegistryIntegrity + " ");
                bool ok = r[0] == "2" && r[1] == null && engine != null && integrity != null
                    && integrity.StartsWith(ParityRunLog.RegistryIntegrity + " internal-defect: the registry table is not adopted", StringComparison.Ordinal)
                    && PolicyRunId(integrity) == PolicyRunId(engine) && r[2].IndexOf(engine, StringComparison.Ordinal) < r[2].IndexOf(integrity, StringComparison.Ordinal)
                    && !r[2].Contains("scenario-invalid");
                return ok ? "" : said(r);
            };

            string[] candidate = run("S-registry-candidate", utf8(plainText.Replace(v1, v2)));
            string candidateFault = defect(candidate);
            check(candidateFault.Length == 0,
                "parity policy: a candidate under the not-adopted table gives registry-integrity internal-defect, exit 2, no trace ("
                + (candidateFault.Length == 0 ? PolicyLine(candidate[2], ParityRunLog.RegistryIntegrity + " ") : candidateFault) + ")");

            string twoThenOne = defect(run("S-registry-dup-21", utf8(plainText.Replace(v1, v2 + "," + v1))));
            string oneThenTwo = defect(run("S-registry-dup-12", utf8(plainText.Replace(v1, v1 + "," + v2))));
            check(twoThenOne.Length == 0 && oneThenTwo.Length == 0,
                "parity policy: both duplicate-schema orders route as candidates (" + (twoThenOne + oneThenTwo).Trim() + ")");

            string escapedValue = defect(run("S-registry-escaped-value", utf8(plainText.Replace(v1, "\"schema\":\"shimeji-parity-scenario\\/2\""))));
            string escapedName = defect(run("S-registry-escaped-name", utf8(plainText.Replace(v1, "\"\\u0073chema\":\"shimeji-parity-scenario/2\""))));
            check(escapedValue.Length == 0 && escapedName.Length == 0,
                "parity policy: an escaped marker routes as a candidate (" + (escapedValue + escapedName).Trim() + ")");

            string surrogatePack = defect(run("S-registry-surrogate-pack", utf8(plainText.Replace(v1, v2).Replace("\"pack\":\"pack\"", "\"pack\":\"pa\\ud800ck\""))));
            check(surrogatePack.Length == 0,
                "parity policy: a candidate with a surrogate pack value still reaches the registry step (" + surrogatePack + ")");

            // An occupied trace path with a candidate: the claim's refusal only, ahead of the core run, so neither the
            // loaded-engine nor any registry line, and the occupant's bytes unchanged.
            string occupiedScenario = Path.Combine(root, "S-registry-occupied.json");
            File.WriteAllBytes(occupiedScenario, utf8(plainText.Replace(v1, v2)));
            string occupied = Path.Combine(root, "S-registry-occupied.jsonl");
            File.WriteAllText(occupied, "sentinel\n", new UTF8Encoding(false));
            var occupiedLog = new StringWriter();
            string occupiedExit;
            try { occupiedExit = ParityRun.Run(occupiedScenario, pets, occupied, build, occupiedLog).ToString(CultureInfo.InvariantCulture); }
            catch (Exception e) { occupiedExit = "threw " + e.GetType().Name; occupiedLog.Write(e.Message); }
            string occupiedText = occupiedLog.ToString();
            log.AppendLine("LOG: parity registry occupied: " + occupiedText.TrimEnd());
            check(occupiedExit == "2" && occupiedText.Contains("is already there") && !occupiedText.Contains(ParityRunLog.RegistryIntegrity)
                  && !occupiedText.Contains(ParityRunLog.LoadedEngine) && File.ReadAllText(occupied) == "sentinel\n",
                "parity policy: an occupied trace path plus a candidate gives the occupied refusal only, no registry-integrity line, sentinel bytes unchanged (exit "
                + occupiedExit + ", " + occupiedText.Trim().Replace("\r", "").Replace("\n", " | ") + ")");

            // A byte order mark stops the scan at 0 (bom), so there is no candidate: the legacy route. Probed on .NET 10 and
            // pinned here (brief open question 8): JsonDocument.Parse(byte[]) does NOT skip the mark, it refuses it as
            // "'0xEF' is an invalid start of a value", so the legacy outcome is K5's syntax diagnostic, exit 2, no trace, as it
            // is for a BOM-prefixed scenario/1 (the base parsed the same bytes the same way).
            byte[] bomBytes = utf8(plainText.Replace(v1, v2));
            byte[] withBom = new byte[bomBytes.Length + 3];
            withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
            Array.Copy(bomBytes, 0, withBom, 3, bomBytes.Length);
            string[] bom = run("S-registry-bom", withBom);
            check(bom[0] == "2" && bom[1] == null
                  && bom[2].Contains(ParityRun.ScenarioInputUnreadable + " syntax: refused " + bom[3] + ": scenario-invalid: the scenario is not JSON: '0xEF' is an invalid start of a value.")
                  && !bom[2].Contains(ParityRunLog.RegistryIntegrity),
                "parity policy: a BOM-prefixed scenario/2 file takes the legacy route, where the parser refuses the mark as syntax, exit 2 (" + said(bom) + ")");

            // WITNESS: the plain scenario/1 runs as before, with no registry line.
            string[] plainRun = run("S-registry-legacy", utf8(plainText));
            check(plainRun[0] == "0" && plainRun[1] != null && !plainRun[2].Contains(ParityRunLog.RegistryIntegrity),
                "parity policy: WITNESS a scenario/1 file is no candidate and runs the legacy route with no registry line (exit " + plainRun[0] + ")");
        }

        /// <summary>The drift self-test's checks on the pure ParityPolicyRegistry.Verify, with a registry and tables built
        /// here (H :64: table checks are unit/build integrity; no admission table reaches the run).</summary>
        private static void CheckParityRegistryDrift(Action<bool, string> check)
        {
            Func<char, string> hex = c => new string(c, 64);
            Func<string, string, string, string> row = (caseName, scenario, legacy) =>
                "{\"case\":\"" + caseName + "\",\"id\":\"p-" + caseName + "\",\"legacyScenarioSha256\":\"" + legacy + "\",\"pack\":\"packs/" + caseName
                + "\",\"packSha256\":\"" + hex(caseName == "c1" ? '3' : '4') + "\",\"qualificationStatus\":\"q\",\"scenarioId\":\"S-" + caseName
                + "\",\"scenarioSha256\":\"" + scenario + "\",\"ticks\":" + (caseName == "c1" ? "20" : "30") + "}";
            Func<string, string, byte[]> registry = (secondScenario, firstLegacy) => new UTF8Encoding(false).GetBytes(
                "{\"cases\":[" + row("c1", hex('a'), firstLegacy) + "," + row("c2", secondScenario, hex('6')) + "],\"generatorSha256\":\"" + hex('7')
                + "\",\"registry\":\"shimeji-parity-policy/1\",\"schema\":\"shimeji-parity-policy-registry/1\"}");
            Func<string, string, ParityPolicyRow> compiled = (caseName, scenario) =>
                new ParityPolicyRow("p-" + caseName, caseName, "S-" + caseName, "packs/" + caseName, caseName == "c1" ? 20 : 30, scenario, hex(caseName == "c1" ? '3' : '4'));
            Func<byte[], IList<ParityPolicyRow>, string, string> verify = (bytes, rows, digest) =>
            {
                try { return ParityPolicyRegistry.Verify(bytes, rows, digest) ?? "ok"; }
                catch (Exception e) { return "threw " + e.GetType().Name + ": " + e.Message; }
            };
            byte[] good = registry(hex('b'), hex('5'));
            string goodDigest = ParityRun.Sha256(good);
            var goodRows = new List<ParityPolicyRow> { compiled("c1", hex('a')), compiled("c2", hex('b')) };
            string clean = verify(good, goodRows, goodDigest);
            check(clean == "ok", "parity policy: WITNESS the drift self-test passes a registry its compiled rows and digest were made from (" + clean + ")");

            string flipped = verify(good, new List<ParityPolicyRow> { compiled("c1", hex('a').Substring(0, 10) + "b" + hex('a').Substring(11)), compiled("c2", hex('b')) }, goodDigest);
            check(flipped.StartsWith("row 0: the compiled row p-c1/c1 is not the resource's", StringComparison.Ordinal),
                "parity policy: registry drift: a flipped hex digit in a compiled row is an internal defect (" + flipped + ")");
            string upper = verify(good, new List<ParityPolicyRow> { compiled("c1", hex('A')), compiled("c2", hex('b')) }, goodDigest);
            check(upper == "row 0: a compiled hex digest is not 64 lowercase hex characters",
                "parity policy: registry drift: an uppercase hex digest in a compiled row is an internal defect (" + upper + ")");
            string extra = verify(good, new List<ParityPolicyRow> { compiled("c1", hex('a')), compiled("c2", hex('b')), compiled("c3", hex('c')) }, goodDigest);
            check(extra.StartsWith("the compiled table holds an extra row: 3 compiled, 2 in the resource", StringComparison.Ordinal),
                "parity policy: registry drift: an extra compiled row is an internal defect (" + extra + ")");
            // The second row's admission key is the first row's legacy digest: every row equals the resource, so only the
            // admission-key rule can see it.
            byte[] legacyKeyed = registry(hex('5'), hex('5'));
            string legacyKey = verify(legacyKeyed, new List<ParityPolicyRow> { compiled("c1", hex('a')), compiled("c2", hex('5')) }, ParityRun.Sha256(legacyKeyed));
            check(legacyKey.StartsWith("the admission key " + hex('5') + " of p-c2/c2 is a legacy or provenance digest", StringComparison.Ordinal),
                "parity policy: registry drift: a legacy hash used as an admission key is an internal defect (" + legacyKey + ")");
            string missing = verify(null, goodRows, goodDigest);
            check(missing == "the registry resource " + ParityPolicyRegistry.ResourceName + " is missing",
                "parity policy: registry drift: a missing resource is an internal defect (" + missing + ")");
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
