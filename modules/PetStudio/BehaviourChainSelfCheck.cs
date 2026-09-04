using System;
using System.Collections.Generic;
using System.Text;

namespace DesktopAICompanion.PetStudioModule
{
    /// <summary>
    /// Assertions for <see cref="BehaviourChain"/>, living in the module rather than host-side.
    ///
    /// The host keeps no compile-time reference to any module, so <c>--petstudio-selftest</c> drives this
    /// assembly by reflection. Reflecting far enough to build an <c>IList&lt;ChainStep&gt;</c> and a
    /// <c>IDictionary&lt;int, AnimNode&gt;</c> from outside would make the assertions unreadable and would test
    /// the reflection as much as the logic, so the host passes in a fixture pet and calls one entry point.
    ///
    /// Named RunChecks, NOT SelfTest: <c>--module-selftest=&lt;id&gt;</c> invokes the FIRST
    /// <c>bool SelfTest(out string)</c> found anywhere in the assembly, across all types including non-public,
    /// so a helper sharing that signature can beat the module's own entry point non-deterministically. That has
    /// already happened once (Reminder had six).
    /// </summary>
    internal static class BehaviourChainSelfCheck
    {
        internal static bool RunChecks(string fixturePetXml, out string detail)
        {
            var sb = new StringBuilder();
            bool ok = true;
            try
            {
                PetReport report = PetAnalyzer.Analyze(fixturePetXml);
                if (!report.IsValid || report.Nodes.Count < 4)
                {
                    detail = "the fixture companion did not analyze, so nothing below could be exercised: " + report.Error;
                    return false;
                }

                var byId = new Dictionary<int, AnimNode>();
                foreach (AnimNode n in report.Nodes) byId[n.Id] = n;

                ok &= ClassifyIsHonest(sb, report, byId);
                ok &= BuildProducesARunnableChain(sb, fixturePetXml, report, byId);
                ok &= RepeatMakesDistinctNodes(sb, fixturePetXml, report);
                ok &= LimitsHold(sb, fixturePetXml, report);
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("  EXC " + ex.GetType().Name + ": " + ex.Message);
            }
            detail = sb.ToString();
            return ok;
        }

        private static bool Check(StringBuilder sb, string what, bool pass)
        {
            sb.AppendLine((pass ? "  ok   " : "  FAIL ") + what);
            return pass;
        }

        /// <summary>
        /// A join must report what the pet's graph actually says, and the three answers must be
        /// distinguishable. This is the assertion the colour coding rests on: if Classify called a forced
        /// transition natural, the timeline would be confidently wrong.
        /// </summary>
        private static bool ClassifyIsHonest(StringBuilder sb, PetReport report, Dictionary<int, AnimNode> byId)
        {
            bool ok = true;

            // Find one real example of each kind in the fixture, so the cases are exercised against a pet
            // rather than a hand-built stub. A fixture that cannot supply one is a FAILURE, not a skip: a
            // silently unexercised assertion is worse than an absent one.
            AnimNode seqFrom = null; int seqTo = 0;
            AnimNode borderFrom = null; int borderTo = 0;
            foreach (AnimNode n in report.Nodes)
                foreach (AnimEdge e in n.Edges)
                {
                    if (!byId.ContainsKey(e.To)) continue;
                    if (seqFrom == null && e.Kind == "sequence") { seqFrom = n; seqTo = e.To; }
                    // A border edge to a target the SAME node cannot also reach by sequence, or the
                    // preference rule would (correctly) answer "sequence" and this case would prove nothing.
                    if (borderFrom == null && e.Kind == "border" && !HasEdge(n, e.To, "sequence"))
                    {
                        borderFrom = n; borderTo = e.To;
                    }
                }

            if (!Check(sb, "the fixture supplies a sequence edge to classify", seqFrom != null)) return false;
            if (!Check(sb, "the fixture supplies a border-only edge to classify", borderFrom != null)) return false;

            ChainJoin seq = BehaviourChain.Classify(seqFrom, seqTo);
            ok &= Check(sb, "a <next> edge classifies as Sequence (natural)",
                seq.Kind == ChainLink.Sequence && seq.IsNatural);

            ChainJoin border = BehaviourChain.Classify(borderFrom, borderTo);
            ok &= Check(sb, "a border-only edge classifies as Border, not Sequence",
                border.Kind == ChainLink.Border && border.IsNatural);
            // The only= flag is the difference between "on contact" and "on contact with the taskbar", and a
            // jump's landing is the second. Losing it would make the tooltip a guess.
            ok &= Check(sb, "the border join carries the edge's only= flag through",
                border.Only == FindEdgeOnly(borderFrom, borderTo, "border"));

            // A pair with no edge at all. Searched rather than assumed: on a densely wired pet most pairs
            // qualify, but asserting that without checking would be asserting the fixture.
            AnimNode a = null; int unreachedTarget = -1;
            foreach (AnimNode from in report.Nodes)
            {
                foreach (AnimNode to in report.Nodes)
                {
                    if (from.Id == to.Id || HasAnyEdge(from, to.Id)) continue;
                    a = from; unreachedTarget = to.Id; break;
                }
                if (a != null) break;
            }
            if (!Check(sb, "the fixture supplies an unconnected pair to classify", a != null)) return false;
            ChainJoin forced = BehaviourChain.Classify(a, unreachedTarget);
            ok &= Check(sb, "an absent edge classifies as Forced (and IsNatural is false)",
                forced.Kind == ChainLink.Forced && !forced.IsNatural);

            // Preference: sequence beats border when the SAME pair has both, because a sequence edge is the
            // one the pet takes without help from the world.
            AnimNode both = null; int bothTo = 0;
            foreach (AnimNode n in report.Nodes)
                foreach (AnimEdge e in n.Edges)
                    if (both == null && e.Kind == "border" && HasEdge(n, e.To, "sequence")) { both = n; bothTo = e.To; }
            if (both != null)
                ok &= Check(sb, "sequence is preferred over border when a pair has both",
                    BehaviourChain.Classify(both, bothTo).Kind == ChainLink.Sequence);
            else
                sb.AppendLine("  note the fixture has no pair carrying both a sequence and a border edge; " +
                              "the preference rule is unexercised on this companion");

            // A null source (a step whose animation vanished under an edit) must be Forced, not a throw.
            ok &= Check(sb, "a missing source animation classifies as Forced rather than throwing",
                BehaviourChain.Classify(null, 1).Kind == ChainLink.Forced);
            return ok;
        }

        /// <summary>
        /// The compiled pet must be one the HOST will run, and the chain in it must be deterministic. Both
        /// halves matter: a chain that validates but branches is a debugger that shows you something other
        /// than what you asked for, which is worse than no debugger.
        /// </summary>
        private static bool BuildProducesARunnableChain(StringBuilder sb, string fixturePetXml, PetReport report, Dictionary<int, AnimNode> byId)
        {
            bool ok = true;
            var steps = new List<ChainStep>();
            int wanted = Math.Min(3, report.Nodes.Count);
            for (int i = 0; i < wanted; i++)
                steps.Add(new ChainStep { AnimationId = report.Nodes[i].Id, Name = report.Nodes[i].Name, Repeat = 1 });

            string error;
            string built = BehaviourChain.BuildDebugXml(fixturePetXml, steps, false, out error);
            if (!Check(sb, "a 3-step chain builds (" + (error ?? "") + ")", built != null)) return false;

            // The drift guard, the same one the analyzer test makes: the module compiles its own copy of the
            // host's validator, so agreement is the entire justification for source-linking it.
            XmlData.RootNode parsed;
            string hostError;
            ok &= Check(sb, "the host's validator accepts the compiled chain (" + (hostError = "") + ")",
                CompanionXmlValidator.TryParse(built, out parsed, out hostError) && parsed != null);
            if (parsed == null) return false;

            PetReport after = PetAnalyzer.Analyze(built);
            ok &= Check(sb, "the compiled chain analyzes as a valid companion", after.IsValid);

            // The clones: the last `wanted` animations, since BuildDebugXml appends them.
            var clones = new List<XmlData.AnimationNode>();
            for (int i = parsed.Animations.Animation.Length - wanted; i < parsed.Animations.Animation.Length; i++)
                clones.Add(parsed.Animations.Animation[i]);

            ok &= Check(sb, "one clone per step was appended", clones.Count == wanted);
            ok &= Check(sb, "the original animations are left untouched",
                parsed.Animations.Animation.Length == report.Nodes.Count + wanted);

            ok &= MagicNamesAreNeverCloned(sb, fixturePetXml, report);

            // Determinism: every exit of every non-final clone points at exactly the next clone.
            bool wired = true;
            for (int i = 0; i < clones.Count - 1; i++)
            {
                int next = clones[i + 1].Id;
                wired &= OnlyEdgeIs(clones[i].Sequence != null ? clones[i].Sequence.Next : null, next);
                wired &= clones[i].Border != null && OnlyEdgeIs(clones[i].Border.Next, next);
                if (clones[i].Gravity != null) wired &= OnlyEdgeIs(clones[i].Gravity.Next, next);
            }
            ok &= Check(sb, "every exit of every non-final clone points at the next clone alone", wired);

            // A border edge is not optional on a chained clone: an animation that ends on contact (a jump does)
            // would otherwise leave the chain at the border and never reach the next step.
            bool borderPresent = true;
            for (int i = 0; i < clones.Count - 1; i++)
                if (clones[i].Border == null) borderPresent = false;
            ok &= Check(sb, "every chained clone has a border exit, so a contact-terminated step still advances",
                borderPresent);

            // Gravity is replaced, never added or removed: adding one to a wall pose drops the pet off the
            // wall, removing one leaves it hanging in mid-air.
            bool gravityMatches = true;
            for (int i = 0; i < clones.Count; i++)
            {
                XmlData.AnimationNode original = FindById(parsed, report.Nodes[i].Id);
                if (original == null) continue;
                if ((original.Gravity == null) != (clones[i].Gravity == null)) gravityMatches = false;
            }
            ok &= Check(sb, "a clone has a <gravity> node exactly when its original did", gravityMatches);

            // The last clone hands back to the pet's own graph, so behaviour after the chain is real behaviour
            // and the originals stay reachable rather than the debug pet dead-ending.
            XmlData.AnimationNode lastOriginal = FindById(parsed, report.Nodes[wanted - 1].Id);
            XmlData.AnimationNode lastClone = clones[clones.Count - 1];
            int originalEdges = lastOriginal != null && lastOriginal.Sequence != null && lastOriginal.Sequence.Next != null
                ? lastOriginal.Sequence.Next.Length : 0;
            int lastEdges = lastClone.Sequence != null && lastClone.Sequence.Next != null ? lastClone.Sequence.Next.Length : 0;
            ok &= Check(sb, "the final clone keeps the original's own exits (" + lastEdges + " vs " + originalEdges + ")",
                lastEdges == originalEdges);

            // One spawn, into step 1. Otherwise half the runs start with a four-second fall from the top.
            ok &= Check(sb, "the debug companion has exactly one spawn and it enters step 1",
                parsed.Spawns != null && parsed.Spawns.Spawn != null && parsed.Spawns.Spawn.Length == 1 &&
                parsed.Spawns.Spawn[0].Next != null && parsed.Spawns.Spawn[0].Next.Value == clones[0].Id);

            // Looping closes the ring rather than handing back.
            string loopError;
            string looped = BehaviourChain.BuildDebugXml(fixturePetXml, steps, true, out loopError);
            if (Check(sb, "a looped chain builds (" + (loopError ?? "") + ")", looped != null))
            {
                XmlData.RootNode loopParsed;
                string e2;
                if (CompanionXmlValidator.TryParse(looped, out loopParsed, out e2))
                {
                    XmlData.AnimationNode first = loopParsed.Animations.Animation[loopParsed.Animations.Animation.Length - wanted];
                    XmlData.AnimationNode last = loopParsed.Animations.Animation[loopParsed.Animations.Animation.Length - 1];
                    ok &= Check(sb, "a looped chain's last step returns to its first",
                        last.Sequence != null && OnlyEdgeIs(last.Sequence.Next, first.Id));
                }
            }
            return ok;
        }

        /// <summary>
        /// A clone must never carry one of the four magic names, because the host resolves fall / drag / kill /
        /// sync by taking the FIRST animation with that name: a clone called "fall" becomes the pet's falling
        /// animation, and the debug pet stops falling correctly.
        ///
        /// The chain here is built FROM the magic-named animations on purpose. Chaining the pet's first three
        /// animations proved nothing: they are not magic-named, so dropping the prefix entirely left this
        /// assertion green. Found by mutation testing, which is the only thing that would have found it.
        /// </summary>
        private static bool MagicNamesAreNeverCloned(StringBuilder sb, string fixturePetXml, PetReport report)
        {
            string[] magic = { "fall", "drag", "kill", "sync" };
            var steps = new List<ChainStep>();
            foreach (AnimNode n in report.Nodes)
                foreach (string m in magic)
                    if (string.Equals(n.Name, m, StringComparison.OrdinalIgnoreCase))
                        steps.Add(new ChainStep { AnimationId = n.Id, Name = n.Name, Repeat = 1 });

            // A fixture with none of them cannot exercise this, and saying so is the point: a silently
            // unexercised guard is what this whole check exists to stop.
            if (!Check(sb, "the fixture supplies magic-named animations to chain (" + steps.Count + " found)",
                    steps.Count > 0))
                return false;

            string error;
            string built = BehaviourChain.BuildDebugXml(fixturePetXml, steps, false, out error);
            if (!Check(sb, "a chain of magic-named animations builds (" + (error ?? "") + ")", built != null))
                return false;

            XmlData.RootNode parsed;
            string parseError;
            if (!Check(sb, "the magic-name chain validates (" + (parseError = "") + ")",
                    CompanionXmlValidator.TryParse(built, out parsed, out parseError)))
                return false;

            var names = new List<string>();
            var duplicated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool unique = true, magicSafe = true;
            for (int i = parsed.Animations.Animation.Length - steps.Count; i < parsed.Animations.Animation.Length; i++)
                names.Add(parsed.Animations.Animation[i].Name ?? "");
            foreach (string name in names)
                foreach (string m in magic)
                    if (string.Equals(name, m, StringComparison.OrdinalIgnoreCase)) magicSafe = false;
            // And unique across the WHOLE pet, not just among the clones: two animations sharing a name means
            // anything resolving by name (a module's reaction list, the debug menu) gets whichever came first.
            foreach (XmlData.AnimationNode a in parsed.Animations.Animation)
                if (a != null && !duplicated.Add(a.Name ?? "")) unique = false;

            bool ok = Check(sb, "cloning a magic-named animation does not produce a magic-named clone (" +
                string.Join(", ", names.ToArray()) + ")", magicSafe);
            ok &= Check(sb, "every animation name in the debug companion is still unique", unique);
            return ok;
        }

        /// <summary>
        /// "Play it 10 times" must become 10 nodes, not one node pointing at itself. A self-edge is an infinite
        /// loop, which is a different behaviour and one the user did not ask for.
        /// </summary>
        private static bool RepeatMakesDistinctNodes(StringBuilder sb, string fixturePetXml, PetReport report)
        {
            var steps = new List<ChainStep>
            {
                new ChainStep { AnimationId = report.Nodes[0].Id, Name = report.Nodes[0].Name, Repeat = 4 },
            };
            string error;
            string built = BehaviourChain.BuildDebugXml(fixturePetXml, steps, false, out error);
            if (!Check(sb, "a x4 chip builds (" + (error ?? "") + ")", built != null)) return false;

            XmlData.RootNode parsed;
            string parseError;
            if (!Check(sb, "the x4 chain validates", CompanionXmlValidator.TryParse(built, out parsed, out parseError)))
                return false;

            bool ok = Check(sb, "a x4 chip becomes FOUR distinct animations, not one self-loop",
                parsed.Animations.Animation.Length == report.Nodes.Count + 4);

            var ids = new HashSet<int>();
            for (int i = parsed.Animations.Animation.Length - 4; i < parsed.Animations.Animation.Length; i++)
                ids.Add(parsed.Animations.Animation[i].Id);
            ok &= Check(sb, "the four clones have four distinct ids", ids.Count == 4);

            bool noSelfEdge = true;
            for (int i = parsed.Animations.Animation.Length - 4; i < parsed.Animations.Animation.Length - 1; i++)
            {
                XmlData.AnimationNode c = parsed.Animations.Animation[i];
                if (c.Sequence != null && c.Sequence.Next != null)
                    foreach (XmlData.NextNode n in c.Sequence.Next)
                        if (n != null && n.Value == c.Id) noSelfEdge = false;
            }
            ok &= Check(sb, "no chained clone points at itself", noSelfEdge);
            return ok;
        }

        private static bool LimitsHold(StringBuilder sb, string fixturePetXml, PetReport report)
        {
            bool ok = true;
            string error;

            ok &= Check(sb, "an empty timeline is refused with a reason",
                BehaviourChain.BuildDebugXml(fixturePetXml, new List<ChainStep>(), false, out error) == null &&
                !string.IsNullOrEmpty(error));

            ok &= Check(sb, "a step naming an animation the companion does not have is refused with a reason",
                BehaviourChain.BuildDebugXml(fixturePetXml,
                    new List<ChainStep> { new ChainStep { AnimationId = 999999, Repeat = 1 } }, false, out error) == null &&
                !string.IsNullOrEmpty(error));

            // Over the cap: one step repeated past MaxChainNodes. Refused rather than truncated, because a
            // silently shortened chain is a chain that does not match the timeline on screen.
            var huge = new List<ChainStep>();
            for (int i = 0; i < BehaviourChain.MaxChainNodes; i++)
                huge.Add(new ChainStep { AnimationId = report.Nodes[0].Id, Repeat = BehaviourChain.MaxRepeatPerStep });
            ok &= Check(sb, "a chain over the node cap is refused rather than truncated",
                BehaviourChain.BuildDebugXml(fixturePetXml, huge, false, out error) == null &&
                !string.IsNullOrEmpty(error));

            ok &= Check(sb, "junk XML is refused with a reason",
                BehaviourChain.BuildDebugXml("not xml", new List<ChainStep> { new ChainStep { AnimationId = 1 } },
                    false, out error) == null && !string.IsNullOrEmpty(error));
            return ok;
        }

        // ---- small helpers ----

        private static bool HasEdge(AnimNode node, int to, string kind)
        {
            foreach (AnimEdge e in node.Edges)
                if (e != null && e.To == to && e.Kind == kind) return true;
            return false;
        }

        private static bool HasAnyEdge(AnimNode node, int to)
        {
            foreach (AnimEdge e in node.Edges)
                if (e != null && e.To == to) return true;
            return false;
        }

        private static string FindEdgeOnly(AnimNode node, int to, string kind)
        {
            AnimEdge best = null;
            foreach (AnimEdge e in node.Edges)
            {
                if (e == null || e.To != to || e.Kind != kind) continue;
                if (best == null || e.Probability > best.Probability) best = e;
            }
            return best == null ? "" : (best.Only ?? "");
        }

        private static XmlData.AnimationNode FindById(XmlData.RootNode root, int id)
        {
            if (root.Animations == null || root.Animations.Animation == null) return null;
            foreach (XmlData.AnimationNode a in root.Animations.Animation)
                if (a != null && a.Id == id) return a;
            return null;
        }

        private static bool OnlyEdgeIs(XmlData.NextNode[] edges, int target)
        {
            return edges != null && edges.Length == 1 && edges[0] != null && edges[0].Value == target;
        }
    }
}
