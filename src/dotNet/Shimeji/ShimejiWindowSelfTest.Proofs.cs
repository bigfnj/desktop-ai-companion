using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

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
            Func<string, string, string, bool, int, string[], string> leg = (name, scenario, pets, traceOff, ticks, records) =>
            {
                string trace = Path.Combine(root, name + ".jsonl");
                var runLog = new StringWriter();
                int exit = ParityRun.Run(scenario, pets, trace, build, runLog, traceOff);
                string text = runLog.ToString();
                log.Append("LOG: parity proof " + name + ": " + text);
                string traceText = File.Exists(trace) ? File.ReadAllText(trace) : null;
                string runId = PolicyHeaderRunId(traceText);
                string want = ParityRunLog.Reconciliation + " active-pending-membership checkpoints " + (ticks + 1).ToString(CultureInfo.InvariantCulture)
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
            ledger.Checkpoint(0, new TickMembership(new[] { "s1" }, new string[0]));
            bool cleanZero = ledger.Failure == null;
            ledger.Commit("{\"type\":\"state\",\"tick\":1,\"sid\":\"s1\",\"pending\":false}");
            ledger.Commit("{\"type\":\"state\",\"tick\":1,\"sid\":\"x\",\"pending\":true}");
            ledger.Checkpoint(1, new TickMembership(new[] { "s1" }, new[] { "x" }));
            ledger.Commit("{\"type\":\"event\",\"tick\":2,\"sid\":\"s1\",\"kind\":\"dispose\"}");
            ledger.Checkpoint(2, new TickMembership(new string[0], new string[0]));
            string logged = ledgerLog.ToString().Trim();
            check(cleanZero && ledger.Checkpoints == 3 && ledger.Failure != null && ledger.Failure.StartsWith("checkpoint 1: the private lists hold [s1 active, x pending]", StringComparison.Ordinal)
                  && logged == ParityRunLog.ReconciliationFailedLine(ledger.Failure, "w") && ledger.ClosingLine(2) == null,
                "parity proof: WITNESS a ledger driven past a mismatch logs its first failing checkpoint once and writes no positive witness (" + logged + ")");
            var namedLog = new StringWriter();
            var named = new ParityRunLedger(true, namedLog, "w");
            named.Seed("s1");
            named.StartupActivated();
            named.Commit("{\"type\":\"event\",\"tick\":1,\"sid\":\"s1\",\"kind\":\"dispose\"}");
            named.Checkpoint(1, new TickMembership(new[] { "s1" }, new string[0]));
            check(named.Failure == "checkpoint 1: dispose of s1 with no active member carrying an earlier dispose-request",
                "parity proof: WITNESS a dispose with no earlier dispose-request is a named inconsistency (" + (named.Failure ?? "none") + ")");
        }
    }
}
