using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DesktopAICompanion.Shimeji
{
    internal static partial class ShimejiWindowSelfTest
    {
        /// <summary>
        /// burndown/host-policy-1, N-parity-1107-03 C4 (signed revision H :136, :142-150): A15 population reconciliation on
        /// generated packs, traced and trace-off. Each run must end with the positive witness naming N+1 checkpoints and
        /// the header's runId, and no failure line: births at the cap with fifteen children still pending at N; arbitrary
        /// start sids with one starter that fails and never joins (a pending disposal with no dispose); and a companion
        /// that self-destructs, an active disposal that waits for its dispose. A ledger driven by hand past a mismatch shows
        /// the failing side can speak. Every pet here is generated.
        /// </summary>
        private static void CheckParityProofs(Action<bool, string> check, string root, StringBuilder log)
        {
            try
            {
                CheckParityProofLegs(check, root, log);
            }
            catch (Exception e)
            {
                check(false, "parity proof: the reconciliation checks ran without an exception: " + e);
            }
        }

        /// <summary>ParDocument's Walk made a SelfDestruct of its three one-tick poses: a companion entering Walk asks to be
        /// disposed at its penultimate tick and leaves the manager at the next step. ParMapJson's map binds it unchanged.</summary>
        private static XmlData.RootNode ProofPopDocument(string set)
        {
            XmlData.RootNode doc = ParDocument(false);
            doc.Header.Title = set;
            doc.Header.Features += " lifecycle";
            foreach (XmlData.AnimationNode n in doc.Animations.Animation)
            {
                if (n.Id != 30) continue;
                n.Act.Type = "Embedded";
                n.Act.Class = "SelfDestruct";
                n.Act.Params = new XmlData.ParamNode[0];
            }
            return doc;
        }

        /// <summary>PolicyBreedDocument with BornCount 1 and a BornBehavior read once per action from a draw: below one half it
        /// names a behaviour the member lacks, so that birth fails and its provisional sid (parent.b1) is never used; the
        /// next action's draw names Walk, and that birth takes the same sid.</summary>
        private static XmlData.RootNode ProofReuseDocument(string set)
        {
            XmlData.RootNode doc = PolicyBreedDocument(set);
            foreach (XmlData.AnimationNode n in doc.Animations.Animation)
            {
                if (n.Id != 30) continue;
                n.Act.Params = new[]
                {
                    new XmlData.ParamNode { Name = "BornCount", Value = "1", Cache = "none" },
                    new XmlData.ParamNode { Name = "BornBehavior", Value = "Math.random() < 0.5 ? \"Nope\" : \"Walk\"", Cache = "action" },
                };
            }
            return doc;
        }

        private static void CheckParityProofLegs(Action<bool, string> check, string root, StringBuilder log)
        {
            Directory.CreateDirectory(root);
            var build = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "commit", "selftest" }, { "exeSha256", new string('0', 64) }, { "engineSha256", new string('1', 64) }, { "converterSha256", new string('2', 64) },
            };
            string capPets = PolicyPets(root, "pets-cap", new KeyValuePair<string, XmlData.RootNode>("Alpha", PolicyBreedDocument("Alpha")));
            // Births at tick 6, the last tick: fifteen children pending at N, attempts 16 and 17 refused there.
            string capScenario = PolicyScenario(root, "S-proof-cap", 6,
                "{\"sid\":\"m1\",\"imageSet\":\"Alpha\",\"behavior\":\"Walk\",\"anchor\":[640,680],\"lookRight\":false}");
            string mixedPets = PolicyPets(root, "pets-mixed",
                new KeyValuePair<string, XmlData.RootNode>("Par", ParDocument(false)), new KeyValuePair<string, XmlData.RootNode>("Pop", ProofPopDocument("Pop")));
            // zed.7 and a1 start; q-9 names a behaviour its member lacks, so its start fails, it is disposed while pending at
            // tick 0 and never joins; a1 self-destructs, active until its dispose a step later.
            string mixedScenario = PolicyScenario(root, "S-proof-mixed", 5,
                "{\"sid\":\"zed.7\",\"imageSet\":\"Par\",\"behavior\":\"Walk\",\"anchor\":[400,680],\"lookRight\":false},"
                + "{\"sid\":\"q-9\",\"imageSet\":\"Par\",\"behavior\":\"Nope\",\"anchor\":[600,680],\"lookRight\":true},"
                + "{\"sid\":\"a1\",\"imageSet\":\"Pop\",\"behavior\":\"Walk\",\"anchor\":[800,680],\"lookRight\":false}");

            // A traced leg also requires the records that make it the case it claims (a leg that never refused, disposed or
            // failed a start would pass vacuously); a trace-off run has no event lines to read them from.
            string reusePets = PolicyPets(root, "pets-reuse", new KeyValuePair<string, XmlData.RootNode>("Kin", ProofReuseDocument("Kin")));
            // The first draw (the first action's BornBehavior) is low, every later one high.
            string reuseScenario = PolicyScenario(root, "S-proof-reuse", 15,
                "{\"sid\":\"m1\",\"imageSet\":\"Kin\",\"behavior\":\"Walk\",\"anchor\":[640,680],\"lookRight\":false}",
                "{\"kind\":\"tape\",\"values\":[0.1,0.9,0.9,0.9,0.9,0.9,0.9,0.9,0.9,0.9,0.9,0.9]}");

            Func<string, string, string, bool, int, string[], string> leg = (name, scenario, pets, traceOff, ticks, records) =>
            {
                string trace = Path.Combine(root, name + ".jsonl");
                var runLog = new StringWriter();
                int exit = ParityRun.Run(scenario, pets, trace, build, runLog, traceOff);
                string text = runLog.ToString();
                log.Append("LOG: parity proof " + name + ": " + text);
                string traceText = File.Exists(trace) ? File.ReadAllText(trace) : null;
                string runId = PolicyHeaderRunId(traceText);
                string want = ParityRunLog.Reconciliation + " window-effects, active-pending-membership checkpoints " + (ticks + 1).ToString(CultureInfo.InvariantCulture)
                    + " stateRecords " + (traceOff ? "final" : "every-checkpoint") + ParityRunLog.RunIdField + runId;
                string witness = PolicyLine(text, ParityRunLog.Reconciliation + " ");
                bool complete = traceText != null && traceText.Contains("{\"type\":\"footer\",\"ticks\":" + ticks.ToString(CultureInfo.InvariantCulture) + ",\"status\":\"complete\"");
                var lacking = new List<string>();
                if (!traceOff && records != null)
                    foreach (string record in records)
                        if (record.StartsWith("!", StringComparison.Ordinal) ? traceText != null && traceText.Contains(record.Substring(1))
                                : traceText == null || !traceText.Contains(record))
                            lacking.Add(record);
                return exit == 0 && complete && runId != null && witness == want && lacking.Count == 0 ? ""
                    : "exit " + exit + ", footer " + (complete ? "complete" : "not complete") + ", " + (witness ?? "no reconciliation line")
                      + (lacking.Count == 0 ? "" : ", trace record condition failed: " + string.Join(" | ", lacking));
            };
            string[] capRecords =
            {
                "{\"type\":\"event\",\"tick\":6,\"sid\":\"m1\",\"kind\":\"birth-refused\",\"child\":null,\"attempt\":17,",
                "{\"type\":\"state\",\"tick\":6,\"sid\":\"m1.b15\",",
            };
            string[] mixedRecords =
            {
                "{\"type\":\"event\",\"tick\":0,\"sid\":\"q-9\",\"kind\":\"dispose-request\"",
                "!{\"type\":\"state\",\"tick\":0,\"sid\":\"q-9\"",
                "\"sid\":\"a1\",\"kind\":\"dispose-request\"",
                "\"sid\":\"a1\",\"kind\":\"dispose\"}",
                "{\"type\":\"state\",\"tick\":5,\"sid\":\"zed.7\",",
            };
            string capOn = leg("cap-traced", capScenario, capPets, false, 6, capRecords);
            check(capOn.Length == 0, "parity proof: births at the cap reconcile at every checkpoint, traced (" + (capOn.Length == 0 ? "witness 7 checkpoints" : capOn) + ")");
            string capOff = leg("cap-trace-off", capScenario, capPets, true, 6, capRecords);
            check(capOff.Length == 0, "parity proof: births at the cap reconcile at every checkpoint, trace-off (" + (capOff.Length == 0 ? "witness 7 checkpoints" : capOff) + ")");
            string[] reuseRecords =
            {
                "\"sid\":\"m1\",\"kind\":\"birth-refused\",\"child\":null,\"attempt\":1,\"reason\":\"error\"}",
                "\"sid\":\"m1\",\"kind\":\"birth-request\",\"child\":\"m1.b1\",",
                "\"attempt\":2}",
                "{\"type\":\"state\",\"tick\":15,\"sid\":\"m1.b1\",",
            };
            string reuseOn = leg("reuse-traced", reuseScenario, reusePets, false, 15, reuseRecords);
            check(reuseOn.Length == 0,
                "parity proof: a failed child's provisional sid taken by the next birth reconciles, traced (" + (reuseOn.Length == 0 ? "witness 16 checkpoints" : reuseOn) + ")");
            string reuseOff = leg("reuse-trace-off", reuseScenario, reusePets, true, 15, reuseRecords);
            check(reuseOff.Length == 0,
                "parity proof: a failed child's provisional sid taken by the next birth reconciles, trace-off (" + (reuseOff.Length == 0 ? "witness 16 checkpoints" : reuseOff) + ")");
            string mixedOn = leg("mixed-traced", mixedScenario, mixedPets, false, 5, mixedRecords);
            check(mixedOn.Length == 0,
                "parity proof: arbitrary failed and successful start sids and both disposals reconcile, traced (" + (mixedOn.Length == 0 ? "witness 6 checkpoints" : mixedOn) + ")");
            string mixedOff = leg("mixed-trace-off", mixedScenario, mixedPets, true, 5, mixedRecords);
            check(mixedOff.Length == 0,
                "parity proof: arbitrary failed and successful start sids and both disposals reconcile, trace-off (" + (mixedOff.Length == 0 ? "witness 6 checkpoints" : mixedOff) + ")");

            // WITNESS: the ledger itself, driven by hand. A private list holding a member the replay never saw fails that
            // checkpoint, the failure is logged once with its checkpoint, a later checkpoint does not replace it, and no
            // positive witness follows; a dispose with no earlier request is one of the named inconsistencies.
            var ledgerLog = new StringWriter();
            var ledger = new ParityRunLedger(false, ledgerLog, "w");
            ledger.Seed("s1");
            ledger.StartupActivated();
            ledger.Commit("{\"type\":\"state\",\"tick\":0,\"sid\":\"s1\",\"pending\":false}");
            ledger.Checkpoint(0, new TickMembership(new[] { "s1" }, new string[0]), default(ParityWindowState));
            bool cleanZero = ledger.Failure == null;
            ledger.Commit("{\"type\":\"state\",\"tick\":1,\"sid\":\"s1\",\"pending\":false}");
            ledger.Commit("{\"type\":\"state\",\"tick\":1,\"sid\":\"x\",\"pending\":true}");
            ledger.Checkpoint(1, new TickMembership(new[] { "s1" }, new[] { "x" }), default(ParityWindowState));
            ledger.Commit("{\"type\":\"event\",\"tick\":2,\"sid\":\"s1\",\"kind\":\"dispose\"}");
            ledger.Checkpoint(2, new TickMembership(new string[0], new string[0]), default(ParityWindowState));
            string logged = ledgerLog.ToString().Trim();
            check(cleanZero && ledger.Checkpoints == 3 && ledger.Failure != null && ledger.Failure.StartsWith("checkpoint 1: the private lists hold [s1 active, x pending]", StringComparison.Ordinal)
                  && logged == ParityRunLog.ReconciliationFailedLine(ledger.Failure, "w") && ledger.ClosingLine(2) == null,
                "parity proof: WITNESS a ledger driven past a mismatch logs its first failing checkpoint once and writes no positive witness (" + logged + ")");
            var namedLog = new StringWriter();
            var named = new ParityRunLedger(true, namedLog, "w");
            named.Seed("s1");
            named.StartupActivated();
            named.Commit("{\"type\":\"event\",\"tick\":1,\"sid\":\"s1\",\"kind\":\"dispose\"}");
            named.Checkpoint(1, new TickMembership(new[] { "s1" }, new string[0]), default(ParityWindowState));
            check(named.Failure == "checkpoint 1: dispose of s1 with no active member carrying an earlier dispose-request",
                "parity proof: WITNESS a dispose with no earlier dispose-request is a named inconsistency (" + (named.Failure ?? "none") + ")");

            // The unknown dispose-request rule, fed as a synthetic committed-line stream (no engine change, no admission
            // table): a dispose-request for a sid with no member is a no-op that leaves no tombstone, so a later
            // birth-request for that sid is a new pending member, and every checkpoint passes.
            var ghostLog = new StringWriter();
            var ghost = new ParityRunLedger(false, ghostLog, "w");
            ghost.Seed("s1");
            ghost.StartupActivated();
            ghost.Commit("{\"type\":\"state\",\"tick\":0,\"sid\":\"s1\",\"pending\":false}");
            ghost.Checkpoint(0, new TickMembership(new[] { "s1" }, new string[0]), default(ParityWindowState));
            ghost.Commit("{\"type\":\"event\",\"tick\":1,\"sid\":\"s1.b1\",\"kind\":\"dispose-request\",\"reason\":\"error\"}");
            ghost.Commit("{\"type\":\"state\",\"tick\":1,\"sid\":\"s1\",\"pending\":false}");
            ghost.Checkpoint(1, new TickMembership(new[] { "s1" }, new string[0]), default(ParityWindowState));
            ghost.Commit("{\"type\":\"event\",\"tick\":2,\"sid\":\"s1\",\"kind\":\"birth-request\",\"child\":\"s1.b1\"}");
            ghost.Commit("{\"type\":\"state\",\"tick\":2,\"sid\":\"s1\",\"pending\":false}");
            ghost.Commit("{\"type\":\"state\",\"tick\":2,\"sid\":\"s1.b1\",\"pending\":true}");
            ghost.Checkpoint(2, new TickMembership(new[] { "s1" }, new[] { "s1.b1" }), default(ParityWindowState));
            string ghostClosing = ghost.ClosingLine(2);
            check(ghost.Failure == null && ghostClosing != null && ghostClosing.StartsWith(ParityRunLog.Reconciliation + " window-effects, active-pending-membership checkpoints 3 ", StringComparison.Ordinal),
                "parity proof: WITNESS an unknown dispose-request is a no-op and its sid may be born later (" + (ghost.Failure ?? ghostClosing ?? "no closing line") + ")");

            // A trace-off ledger whose N+1 checkpoints passed writes the positive witness only once its final states were
            // compared; when the final-state write failed before that (ParityRun catches it as status error), its closing
            // line says so instead of naming a comparison that never ran.
            Func<bool, string> traceOffClosing = compareFinal =>
            {
                var l = new ParityRunLedger(true, new StringWriter(), "w");
                l.Seed("s1");
                l.StartupActivated();
                l.Checkpoint(0, new TickMembership(new[] { "s1" }, new string[0]), default(ParityWindowState));
                l.Checkpoint(1, new TickMembership(new[] { "s1" }, new string[0]), default(ParityWindowState));
                if (compareFinal)
                {
                    l.Commit("{\"type\":\"state\",\"tick\":1,\"sid\":\"s1\",\"pending\":false}");
                    l.FinalStates(1, new TickMembership(new[] { "s1" }, new string[0]));
                }
                return l.ClosingLine(1);
            };
            string withFinal = traceOffClosing(true), withoutFinal = traceOffClosing(false);
            check(withFinal == ParityRunLog.ReconciliationLine(2, true, "w") && withoutFinal == ParityRunLog.ReconciliationFinalStatesMissingLine(2, "w"),
                "parity proof: WITNESS a trace-off ledger whose final states were never compared writes no positive witness (" + withoutFinal + " | " + withFinal + ")");

            // Codex's closure ruling Q-e, on the run's own abort ending (ParityRun.EndAborted) with a ledger failed by hand,
            // since no unmutated run fails its ledger: a scenario-invalid abort after a reconciliation failure keeps the
            // footer error and the refusal's exit 2, and logs the incomplete count beside the first-failure line; with no
            // failure the footer says aborted. Neither writes a positive witness.
            Func<bool, string> aborted = fail =>
            {
                var abortLog = new StringWriter();
                var abortTrace = new StringWriter();
                var abortWriter = new ParityTraceWriter(abortTrace, new Dictionary<string, ParityMap>(StringComparer.Ordinal), false);
                var l = new ParityRunLedger(false, abortLog, "w");
                l.Seed("s1");
                l.StartupActivated();
                l.Commit("{\"type\":\"state\",\"tick\":0,\"sid\":\"s1\",\"pending\":false}");
                l.Checkpoint(0, new TickMembership(new[] { "s1" }, fail ? new[] { "x" } : new string[0]), default(ParityWindowState));
                int abortExit = ParityRun.EndAborted(abortWriter, l, 5, null, abortLog);
                string footer = abortTrace.ToString().Contains("\"status\":\"error\"") ? "error" : abortTrace.ToString().Contains("\"status\":\"aborted\"") ? "aborted" : "other";
                return "exit " + abortExit + ", footer " + footer + ", " + abortLog.ToString().Trim().Replace("\r", "").Replace("\n", " | ");
            };
            string abortedFailed = aborted(true), abortedClean = aborted(false);
            string incomplete = ParityRunLog.ReconciliationIncompleteLine(1, 6, "w");
            check(abortedFailed.StartsWith("exit 2, footer error, " + ParityRunLog.Reconciliation + " failed at checkpoint 0: ", StringComparison.Ordinal)
                  && abortedFailed.EndsWith(" | " + incomplete, StringComparison.Ordinal)
                  && abortedClean == "exit 2, footer aborted, " + incomplete,
                "parity proof: an abort after a reconciliation failure keeps the footer error, exit 2, and logs its incomplete coverage (" + abortedFailed + " || " + abortedClean + ")");
        }

        /// <summary>
        /// burndown/host-policy-1, N-parity-1107-03 C5 (signed revision H :136): A14 window reconciliation on generated
        /// packs, traced and trace-off. ThrowIE and MinimizeIE with Throwing on and WindowActions on and off, a minimise and
        /// a throw in one tick, and a run that stops part way. Each complete run must end with the positive witness naming
        /// window-effects and N+1 checkpoints and the header's runId. The carry class (WalkWithIE) holds a window by its
        /// lower corner, on and off, in both modes (review of round 4). A rectangle no validator checks fails the run as at
        /// the base, or is never read when its entry never takes effect. A unit leg shows the counters count only a
        /// success. Every pet here is generated.
        /// </summary>
        private static void CheckParityWindowProofs(Action<bool, string> check, string root, StringBuilder log)
        {
            try
            {
                CheckParityWindowProofLegs(check, root, log);
            }
            catch (Exception e)
            {
                check(false, "parity proof: the window reconciliation checks ran without an exception: " + e);
            }
        }

        /// <summary>ParDocument's Walk made the window class <paramref name="cls"/>, an Animate of its three poses at
        /// <paramref name="ticks"/> ticks each, with the class's default parameters.</summary>
        private static XmlData.RootNode ProofWindowDocument(string set, string cls, int ticks)
        {
            XmlData.RootNode doc = ParDocument(false);
            doc.Header.Title = set;
            doc.Header.Features += " windows";
            foreach (XmlData.AnimationNode n in doc.Animations.Animation)
            {
                if (n.Id != 30) continue;
                n.Act.Type = "Embedded";
                n.Act.Class = cls;
                n.Act.Params = new XmlData.ParamNode[0];
                foreach (XmlData.PoseNode p in n.Act.Variants.Variant[0].Pose) p.Ticks = ticks;
            }
            return doc;
        }

        /// <summary>WalkWithIE (the carry class) on ParDocument's Walk: a walk left to x 600, two pixels a pose, holding the
        /// window by its lower corner with no offset, so a companion at [640, 680] facing left holds a window whose right
        /// is 640 and bottom 680.</summary>
        private static XmlData.RootNode ProofCarryDocument(string set)
        {
            XmlData.RootNode doc = ProofWindowDocument(set, "WalkWithIE", 1);
            foreach (XmlData.AnimationNode n in doc.Animations.Animation)
            {
                if (n.Id != 30) continue;
                n.Act.Params = new[]
                {
                    new XmlData.ParamNode { Name = "TargetX", Value = "600", Cache = "none" },
                    new XmlData.ParamNode { Name = "IeOffsetX", Value = "0", Cache = "none" },
                    new XmlData.ParamNode { Name = "IeOffsetY", Value = "0", Cache = "none" },
                };
                foreach (XmlData.PoseNode p in n.Act.Variants.Variant[0].Pose) p.DX = -2;
            }
            return doc;
        }

        /// <summary>A generated scenario/1 with a shown window [300, 200, 700, 500] from tick 0 (or the given activeWindow
        /// timeline), the given settings and input entries after the tick-0 cursor.</summary>
        private static string ProofWindowScenario(string root, string id, int ticks, string mascots, bool windowActions, string input,
            string activeWindow = "[{\"tick\":0,\"visible\":true,\"rect\":[300,200,700,500]}]")
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
                + "\"dpiScale\":1.0,\"ticks\":" + ticks.ToString(CultureInfo.InvariantCulture) + ",\"rng\":{\"kind\":\"none\"},"
                + "\"settings\":{\"Throwing\":true,\"WindowActions\":" + (windowActions ? "true" : "false") + "},"
                + "\"mascots\":[" + mascots + "],\"activeWindow\":" + activeWindow + ","
                + "\"input\":[{\"tick\":0,\"cursor\":[100,100]}" + input + "]}",
                new UTF8Encoding(false));
            return scenario;
        }

        private static void CheckParityWindowProofLegs(Action<bool, string> check, string root, StringBuilder log)
        {
            Directory.CreateDirectory(root);
            var build = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "commit", "selftest" }, { "exeSha256", new string('0', 64) }, { "engineSha256", new string('1', 64) }, { "converterSha256", new string('2', 64) },
            };
            string pets = PolicyPets(root, "pets-window",
                new KeyValuePair<string, XmlData.RootNode>("Thrower", ProofWindowDocument("Thrower", "ThrowIE", 2)),
                new KeyValuePair<string, XmlData.RootNode>("Minimiser", ProofWindowDocument("Minimiser", "MinimizeIE", 1)),
                new KeyValuePair<string, XmlData.RootNode>("Carrier", ProofCarryDocument("Carrier")));
            const string thrower = "{\"sid\":\"t1\",\"imageSet\":\"Thrower\",\"behavior\":\"Walk\",\"anchor\":[640,680],\"lookRight\":false}";
            const string minimiser = "{\"sid\":\"n1\",\"imageSet\":\"Minimiser\",\"behavior\":\"Walk\",\"anchor\":[200,680],\"lookRight\":false}";

            // One run: exit, footer, the closing line against the exact witness with the header's runId, and in a traced run
            // the records that make the leg the case it claims ("!" = must be absent).
            Func<string, string, bool, int, string[], string> run = (name, scenario, traceOff, ticks, records) =>
            {
                string trace = Path.Combine(root, name + ".jsonl");
                var runLog = new StringWriter();
                int exit = ParityRun.Run(scenario, pets, trace, build, runLog, traceOff);
                string text = runLog.ToString();
                log.Append("LOG: parity window proof " + name + ": " + text);
                string traceText = File.Exists(trace) ? File.ReadAllText(trace) : null;
                string runId = PolicyHeaderRunId(traceText);
                string want = ParityRunLog.ReconciliationLine(ticks + 1, traceOff, runId);
                string witness = PolicyLine(text, ParityRunLog.Reconciliation + " ");
                bool complete = traceText != null && traceText.Contains("{\"type\":\"footer\",\"ticks\":" + ticks.ToString(CultureInfo.InvariantCulture) + ",\"status\":\"complete\"");
                var lacking = new List<string>();
                if (!traceOff && records != null)
                    foreach (string record in records)
                        if (record.StartsWith("!", StringComparison.Ordinal) ? traceText != null && traceText.Contains(record.Substring(1))
                                : traceText == null || !traceText.Contains(record))
                            lacking.Add(record);
                return exit == 0 && complete && runId != null && witness == want && lacking.Count == 0 ? ""
                    : name + ": exit " + exit + ", footer " + (complete ? "complete" : "not complete") + ", " + (witness ?? "no reconciliation line")
                      + (lacking.Count == 0 ? "" : ", trace record condition failed: " + string.Join(" | ", lacking));
            };
            // Both modes of one scenario under one label: a defect that shows in both fails this one label once.
            Action<string, string, int, string[]> both = (label, scenario, ticks, records) =>
            {
                string id = Path.GetFileNameWithoutExtension(scenario);
                string traced = run(id + "-traced", scenario, false, ticks, records);
                string off = run(id + "-trace-off", scenario, true, ticks, records);
                string failed = (traced + (traced.Length > 0 && off.Length > 0 ? " | " : "") + off);
                check(failed.Length == 0, label + " (" + (failed.Length == 0 ? "witness window-effects " + (ticks + 1) + " checkpoints, both modes" : failed) + ")");
            };

            const string moved = "\"sid\":\"t1\",\"kind\":\"window-move\",\"from\":[300,200,700,500],";
            const string minimised = "\"sid\":\"n1\",\"kind\":\"window-minimize\"}";
            both("parity proof: window throw with WindowActions on reconciles its moves, traced and trace-off",
                ProofWindowScenario(root, "S-window-proof-throw-on", 8, thrower, true, ""), 8, new[] { moved });
            both("parity proof: window throw with WindowActions off moves nothing and reconciles, traced and trace-off",
                ProofWindowScenario(root, "S-window-proof-throw-off", 8, thrower, false, ""), 8, new[] { "!\"kind\":\"window-move\"" });
            both("parity proof: window minimize with WindowActions on reconciles its minimise, traced and trace-off",
                ProofWindowScenario(root, "S-window-proof-minimize-on", 6, minimiser, true, ""), 6, new[] { minimised });
            both("parity proof: window minimize with WindowActions off minimises nothing and reconciles, traced and trace-off",
                ProofWindowScenario(root, "S-window-proof-minimize-off", 6, minimiser, false, ""), 6, new[] { "!\"kind\":\"window-minimize\"" });
            // n1 ticks before t1: in the tick n1 minimises the window, t1's throw still sees last tick's visible area and
            // asks the desktop for a move that finds no window, a failed call with no event and no count.
            both("parity proof: a minimise and a throw in one tick reconcile, traced and trace-off",
                ProofWindowScenario(root, "S-window-proof-both", 8, minimiser + "," + thrower, true, ""), 8, new[] { moved, minimised });
            // That the leg holds the failed call it claims: at the tick n1 minimises, t1's state shows it still in its
            // ThrowIE (whose Tick asks for a move while last tick's area is visible) and no window-move is committed then.
            string bothTrace = Path.Combine(root, "S-window-proof-both-traced.jsonl");
            string failedCall = "no traced trace";
            if (File.Exists(bothTrace))
            {
                int minimiseTick = -1;
                bool throwing = false, movedThen = false;
                string[] lines = File.ReadAllText(bothTrace).Split('\n');
                foreach (string line in lines)
                    if (line.Contains(minimised))
                        using (JsonDocument d = JsonDocument.Parse(line)) minimiseTick = d.RootElement.GetProperty("tick").GetInt32();
                string at = "\"tick\":" + minimiseTick.ToString(CultureInfo.InvariantCulture) + ",";
                foreach (string line in lines)
                {
                    if (!line.Contains(at)) continue;
                    if (line.StartsWith("{\"type\":\"state\",", StringComparison.Ordinal) && line.Contains("\"sid\":\"t1\",") && line.Contains("\"actionClass\":\"ThrowIE\"")) throwing = true;
                    if (line.Contains("\"kind\":\"window-move\"")) movedThen = true;
                }
                failedCall = "minimise at tick " + minimiseTick + ", t1 " + (throwing ? "in ThrowIE" : "not in ThrowIE") + ", " + (movedThen ? "a window-move then" : "no window-move then");
                check(minimiseTick > 0 && throwing && !movedThen, "parity proof: the one-tick leg holds a throw call that finds no window (" + failedCall + ")");
            }
            else check(false, "parity proof: the one-tick leg holds a throw call that finds no window (" + failedCall + ")");

            // The carry class (review of round 4): WalkWithIE holds the window [400, 380, 640, 680] by its lower right corner
            // and walks left to x 600, the window moving with it each tick; with window actions off it walks as a Move.
            const string carrier = "{\"sid\":\"c1\",\"imageSet\":\"Carrier\",\"behavior\":\"Walk\",\"anchor\":[640,680],\"lookRight\":false}";
            const string carryWindow = "[{\"tick\":0,\"visible\":true,\"rect\":[400,380,640,680]}]";
            both("parity proof: window carry with WindowActions on reconciles its moves, traced and trace-off",
                ProofWindowScenario(root, "S-window-proof-carry-on", 12, carrier, true, "", carryWindow), 12,
                new[] { "\"sid\":\"c1\",\"kind\":\"window-move\",\"from\":[400,380,640,680],\"to\":[398,380,638,680]" });
            both("parity proof: window carry with WindowActions off moves nothing and reconciles, traced and trace-off",
                ProofWindowScenario(root, "S-window-proof-carry-off", 12, carrier, false, "", carryWindow), 12, new[] { "!\"kind\":\"window-move\"" });

            // Review of round 4: neither validator checks an activeWindow rectangle. One the environment reads when its entry
            // takes effect and cannot (three numbers) fails the run as at the base, exit 1, an error footer and the run's
            // failure line, never an exception out of the run; one in an entry superseded at its own tick is never read, by
            // the environment or the window replay, and the run completes with its witness.
            Func<string, string, string[]> guarded = (name, scenario) =>
            {
                string trace = Path.Combine(root, name + ".jsonl");
                var runLog = new StringWriter();
                string exit;
                try { exit = ParityRun.Run(scenario, pets, trace, build, runLog, false).ToString(CultureInfo.InvariantCulture); }
                catch (Exception e) { exit = "threw " + e.GetType().Name; runLog.Write(e.Message); }
                log.AppendLine("LOG: parity window proof " + name + ": " + runLog.ToString().TrimEnd());
                return new[] { exit, File.Exists(trace) ? File.ReadAllText(trace) : null, runLog.ToString() };
            };
            string[] shortRect = guarded("S-window-proof-short-rect",
                ProofWindowScenario(root, "S-window-proof-short-rect", 4, thrower, false, "", "[{\"tick\":0,\"visible\":true,\"rect\":[300,200,700]}]"));
            string shortRunId = PolicyHeaderRunId(shortRect[1]);
            string shortClosing = PolicyLine(shortRect[2], ParityRunLog.Reconciliation + " ");
            check(shortRect[0] == "1" && shortRunId != null && shortRect[1].Contains("{\"type\":\"footer\",\"ticks\":4,\"status\":\"error\"")
                  && shortRect[2].Contains("run failed: ") && shortClosing == ParityRunLog.ReconciliationIncompleteLine(0, 5, shortRunId),
                "parity proof: a shown window entry with a three-number rect fails the run as at the base, exit 1, error footer (exit " + shortRect[0]
                + ", trace " + (shortRect[1] == null ? "none" : shortRect[1].Contains("\"status\":\"error\"") ? "error footer" : "no error footer") + ", "
                + (shortClosing ?? "no reconciliation line") + ")");
            both("parity proof: a malformed rect in a window entry superseded at its own tick is never read, traced and trace-off",
                ProofWindowScenario(root, "S-window-proof-superseded-rect", 4, thrower, false, "",
                    "[{\"tick\":0,\"visible\":true,\"rect\":[300,200,700,500]},{\"tick\":2,\"visible\":true,\"rect\":[300,200,700]},{\"tick\":2,\"visible\":false}]"),
                4, null);

            // A run that stops at tick 3 (a left-button edge for a companion that does not exist): checkpoints 0..2 ran, the
            // closing line counts them, and no positive witness is written.
            string stopScenario = ProofWindowScenario(root, "S-window-proof-stop", 8, thrower, true, ",{\"tick\":3,\"sid\":\"ghost\",\"press\":\"left\"}");
            string stopTrace = Path.Combine(root, "S-window-proof-stop.jsonl");
            var stopLog = new StringWriter();
            int stopExit = ParityRun.Run(stopScenario, pets, stopTrace, build, stopLog, false);
            string stopText = stopLog.ToString();
            log.Append("LOG: parity window proof stop: " + stopText);
            string stopRunId = PolicyHeaderRunId(File.Exists(stopTrace) ? File.ReadAllText(stopTrace) : null);
            string stopLine = PolicyLine(stopText, ParityRunLog.Reconciliation + " ");
            check(stopExit == 2 && stopRunId != null && stopLine == ParityRunLog.ReconciliationIncompleteLine(3, 9, stopRunId),
                "parity proof: a run that stops at tick k prints no positive witness and logs checkpoint k (exit " + stopExit + ", " + (stopLine ?? "no reconciliation line") + ")");

            // WITNESS, the counters themselves: a call that finds no window (hidden, or minimised) changes nothing and counts
            // nothing; a found window counts its move and its minimise once each.
            var screen = new TickArea();
            screen.Set(0, 0, 1280, 720);
            var desk = new TickDesktopWindow();
            TickRect from, to;
            bool hiddenMove = desk.Move(new TickPoint(10, 10), 400, 300, screen, out from, out to);
            bool hiddenMinimise = desk.Minimize(screen);
            desk.Show(new TickRect(300, 200, 400, 300));
            bool foundMove = desk.Move(new TickPoint(10, 10), 400, 300, screen, out from, out to);
            bool foundMinimise = desk.Minimize(screen);
            bool minimisedMove = desk.Move(new TickPoint(20, 20), 400, 300, screen, out from, out to);
            check(!hiddenMove && !hiddenMinimise && foundMove && foundMinimise && !minimisedMove && desk.Moves == 1 && desk.Minimises == 1 && desk.Rect.X == 10,
                "parity proof: WITNESS a missing-window failed call moves and counts nothing, a found window counts its move and its minimise ("
                + desk.Moves + " moves, " + desk.Minimises + " minimises)");
        }
    }
}
