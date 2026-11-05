using System;
using System.Collections.Generic;
using DesktopAICompanion.Shimeji;

namespace DesktopAICompanion
{
    /// <summary>
    /// The social family of embedded classes (src/dotNet/Shimeji/TickSocialFamily.cs, grimoire/06 sections 5 and 7):
    /// scanning for an offer, the partner transitions on arrival, Interact's shared anchor, and ComplexMove/ComplexJump's
    /// Characteristics. Companions are built in code on the scripted desk; numbers are worked from the source arithmetic
    /// beside each assertion.
    /// </summary>
    internal static partial class Program
    {
        /// <summary>One subject behaviour (id 10, act 30) beside Stand (the only weighted route edge, so an ending
        /// subject selects it), Fall, Float, Offer (a floor Stay offering "hug"), Hugged and Hugging.</summary>
        private static TickType SocialType(string behaviour, XmlData.AnimationNode subject)
        {
            return TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 0), TEdge(11, 1), TEdge(12, 0), TEdge(13, 0), TEdge(14, 0), TEdge(15, 0), TEdge(16, 0)),
                TBeh(10, behaviour, subject.Name, 0, null),
                TBeh(11, "Stand", "Stand", 0, null),
                TBeh(12, "Fall", "Falling", 0, null),
                TBeh(13, "Float", "Float", 0, null),
                TBeh(14, "Offer", "Offering", 0, null),
                TBeh(15, "Hugged", "HuggedAct", 0, null),
                TBeh(16, "Hugging", "HuggingAct", 0, null),
                subject,
                TAct(31, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(0, 1)),
                MAct(32, "Falling", "Embedded", "Fall", "", null, MVar(null, false, TPose(0, 1))),
                MAct(33, "Float", "Stay", null, "", new[] { TParam("Duration", "50") }, MVar(null, false, TPose(0, 1))),
                TAct(34, "Offering", "Stay", null, new[] { TParam("Affordance", "hug"), TParam("Duration", "1000") }, TPose(1, 1)),
                TAct(35, "HuggedAct", "Stay", null, new[] { TParam("Duration", "1000") }, TPose(2, 1)),
                TAct(36, "HuggingAct", "Stay", null, new[] { TParam("Duration", "1000") }, TPose(3, 1))));
        }

        /// <summary>Enter a behaviour by name between steps, as a hotspot or a partner does.</summary>
        private static void SocialEnter(TickWorld w, TickMascot m, string behaviour)
        {
            m.SetBehaviour(w.BuildBehaviourByName(m.Type, behaviour, m), "forced");
        }

        private static XmlData.ParamNode[] SocialParams(string behaviour, string targetBehaviour, bool targetLook, params XmlData.ParamNode[] more)
        {
            var ps = new List<XmlData.ParamNode> { TParam("Affordance", "hug") };
            if (behaviour != null) ps.Add(TParam("Behavior", behaviour));
            if (targetBehaviour != null) ps.Add(TParam("TargetBehavior", targetBehaviour));
            if (targetLook) ps.Add(TParam("TargetLook", "true"));
            ps.AddRange(more);
            return ps.ToArray();
        }

        /// <summary>A ScanMove walking 5 px a tick on the floor.</summary>
        private static XmlData.AnimationNode SocialSeek(XmlData.ParamNode[] ps)
        {
            return MAct(30, "Seek", "Embedded", "ScanMove", "Floor", ps, MVar(null, false, TPose(0, 1, -5)));
        }

        private static string SocialScans(TickLog log, string sid)
        {
            return string.Join("|", log.Events.FindAll(e => e.StartsWith(sid + " scan-target ", StringComparison.Ordinal)));
        }

        // ---- ScanMove

        private static void TestTickSocialScanMoveFirst()
        {
            // Manager order m0 m1 m2 m3 m4. After step 1 m2 (540 px away) and m3 (20 px away) offer "hug". The scan is
            // Manager.getMascotWithAffordance: the FIRST offering companion in manager order, so m2, not the nearer m3.
            TickType t = SocialType("Seek", SocialSeek(SocialParams("Hugging", "Hugged", false)));
            var log = new TickLog();
            TickWorld w = MotionWorld(new TickDesk(), log, null, null, t);
            TickMascot m0 = w.CreateAtStart("m0", t, new TickPoint(300, 680), false, "Stand");
            TickMascot m1 = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Stand");
            TickMascot m2 = w.CreateAtStart("m2", t, new TickPoint(100, 680), false, "Offer");
            TickMascot m3 = w.CreateAtStart("m3", t, new TickPoint(660, 680), false, "Offer");
            TickMascot m4 = w.CreateAtStart("m4", t, new TickPoint(700, 680), false, "Stand");
            w.StepOnce();
            SocialEnter(w, m1, "Seek");
            AssertTrue(ReferenceEquals(m2, m1.Target), "ScanMove did not choose the first offering companion in manager order (m2): " + (m1.Target == null ? "none" : m1.Target.Sid));
            AssertEqual(100.0, Local(m1, "TargetX").Number, "ScanMove did not publish its target's x at init.");

            // A second seeker finds the same companion: nothing is reserved.
            SocialEnter(w, m4, "Seek");
            AssertTrue(ReferenceEquals(m2, m4.Target), "A second seeker did not pick the same target: " + (m4.Target == null ? "none" : m4.Target.Sid));
            AssertEqual("m1 scan-target hug m2", SocialScans(log, "m1"), "The scan was not reported once, with its affordance and target.");

            // m0, earlier in manager order, starts offering. ScanMove chose once, at init (ScanMove.java:57-58): it keeps
            // walking to m2 (640 -> 635 -> 630) and never scans again.
            SocialEnter(w, m0, "Offer");
            w.StepOnce();
            w.StepOnce();
            AssertTrue(ReferenceEquals(m2, m1.Target), "ScanMove changed its target after init.");
            AssertEqual(630, m1.Anchor.X, "ScanMove did not walk toward its target at 5 px a tick.");
            AssertEqual("m1 scan-target hug m2", SocialScans(log, "m1"), "ScanMove scanned again after init.");
            AssertTrue(m2.Affordances.Contains("hug") && m1.Affordances.Count == 0, "The target stopped offering, or the seeker offered while seeking.");
        }

        private static void TestTickSocialScanMoveArrival()
        {
            // m1 at 640 walks 5 px a tick to m2 at 630: 635 after step 2, 630 after step 3. Only EXACT arrival (both axes,
            // not turning) runs the transitions: the seeker's own Behavior, then the target's TargetBehavior in the
            // target's type, then TargetLook turns the target (facing left, like the seeker) to face it (ScanMove.java:119-129).
            TickType t = SocialType("Seek", SocialSeek(SocialParams("Hugging", "Hugged", true)));
            var log = new TickLog();
            TickWorld w = MotionWorld(new TickDesk(), log, null, null, t);
            TickMascot m1 = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Stand");
            TickMascot m2 = w.CreateAtStart("m2", t, new TickPoint(630, 680), false, "Offer");
            w.StepOnce();
            SocialEnter(w, m1, "Seek");
            w.StepOnce();
            AssertEqual(635, m1.Anchor.X, "ScanMove did not walk 5 px.");
            AssertTrue(m1.Behaviour.Name == "Seek" && m2.Behaviour.Name == "Offer", "The transitions ran before exact arrival.");
            w.StepOnce();
            AssertEqual(new TickPoint(630, 680), m1.Anchor, "ScanMove did not arrive on the target's anchor.");
            int own = log.Events.IndexOf("m1 behaviour Seek>Hugging forced");
            int theirs = log.Events.IndexOf("m2 behaviour Offer>Hugged partner");
            AssertTrue(own >= 0 && theirs > own, "Arrival did not enter the seeker's Behavior and then the target's TargetBehavior: " + string.Join("; ", log.Events));
            AssertTrue(m2.LookRight, "TargetLook did not turn the target to face the seeker.");
            w.StepOnce();
            AssertTrue(m1.Behaviour.Name == "Hugging" && m2.Behaviour.Name == "Hugged",
                "The pair did not stay in their new behaviours (the old action's second check ended one).");
            AssertEqual(0, log.Faults.Count, "A clean arrival reported a fault: " + string.Join("; ", log.Faults));
        }

        private static void TestTickSocialScanMoveMissing()
        {
            // A TargetBehavior that does not exist: the seeker has already entered Hugging; building the target's throws,
            // which the source catches and shows (ScanMove.java:135-139). The target keeps Offer and TargetLook never runs.
            TickType t = SocialType("Seek", SocialSeek(SocialParams("Hugging", "Nope", true)));
            var log = new TickLog();
            TickWorld w = MotionWorld(new TickDesk(), log, null, null, t);
            TickMascot m1 = w.CreateAtStart("m1", t, new TickPoint(635, 680), false, "Stand");
            TickMascot m2 = w.CreateAtStart("m2", t, new TickPoint(630, 680), false, "Offer");
            w.StepOnce();
            SocialEnter(w, m1, "Seek");
            w.StepOnce();
            AssertEqual("Hugging", m1.Behaviour.Name, "The seeker did not enter its own Behavior before the target's failed.");
            AssertTrue(m2.Behaviour.Name == "Offer" && !m2.LookRight, "A missing TargetBehavior changed the target, or TargetLook ran after the failure.");
            AssertTrue(log.Faults.Count == 1 && log.Faults[0].Contains("Nope"), "The missing TargetBehavior was not reported once: " + string.Join("; ", log.Faults));
            AssertFalse(m1.Disposing, "A failed partner transition ended the seeker.");

            // A blank own Behavior (the default) cannot be built either; it fails first, so neither companion changes,
            // and the seeker, still on the target, fails again every tick.
            TickType u = SocialType("Seek", SocialSeek(SocialParams(null, "Hugged", true)));
            var log2 = new TickLog();
            TickWorld v = MotionWorld(new TickDesk(), log2, null, null, u);
            TickMascot n1 = v.CreateAtStart("m1", u, new TickPoint(635, 680), false, "Stand");
            TickMascot n2 = v.CreateAtStart("m2", u, new TickPoint(630, 680), false, "Offer");
            v.StepOnce();
            SocialEnter(v, n1, "Seek");
            v.StepOnce();
            v.StepOnce();
            AssertTrue(n1.Behaviour.Name == "Seek" && n2.Behaviour.Name == "Offer" && !n2.LookRight,
                "A blank own Behavior did not stop both transitions: " + n1.Behaviour.Name + "/" + n2.Behaviour.Name);
            AssertEqual(2, log2.Faults.Count, "The blank own Behavior was not reported on each arrival tick.");
        }

        private static void TestTickSocialScanMoveLost()
        {
            // The target stops offering: m2 enters Stand between steps 2 and 3, but its offer list is replaced only when
            // it steps (after m1 in step 3), so m1 goes on in step 3 and ends at step 4's check, selecting Stand.
            TickType t = SocialType("Seek", SocialSeek(SocialParams("Hugging", "Hugged", false)));
            TickWorld w = MotionWorld(new TickDesk(), new TickLog(), null, null, t);
            TickMascot m1 = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Stand");
            TickMascot m2 = w.CreateAtStart("m2", t, new TickPoint(100, 680), false, "Offer");
            w.StepOnce();
            SocialEnter(w, m1, "Seek");
            w.StepOnce();
            SocialEnter(w, m2, "Stand");
            w.StepOnce();
            AssertEqual("Seek", m1.Behaviour.Name, "ScanMove ended before its target's offer list changed.");
            w.StepOnce();
            AssertEqual("Stand", m1.Behaviour.Name, "ScanMove went on after its target stopped offering.");
            AssertTrue(m1.Target == null, "The state's scan target outlived the action that chose it.");
        }

        // ---- ScanJump

        private static void TestTickSocialScanJump()
        {
            // ScanJump.java:82-146 from (100, 680) to the target at (160, 680), Arc on, VelocityParam 20:
            //   1: dx 60, dy 0 - 30 = -30, d = sqrt(4500) = 67.082; v = (int)(17.889, -8.944) = (17, -8) -> (117, 672)
            //   2: dx 43, dy 8 - 21.5 = -13.5, d = 45.069; v = (int)(19.082, -5.991) = (19, -5)    -> (136, 667)
            //   3: dx 24, dy 13 - 12 = 1, d = 24.021; v = (int)(19.983, 0.833) = (19, 0)            -> (155, 667)
            //   4: dx 5, dy 13 - 2.5 = 10.5, d = 11.630 <= 20: lands on (160, 680), then the transitions.
            TickType t = SocialType("Seek", MAct(30, "Seek", "Embedded", "ScanJump", "", SocialParams("Hugging", "Hugged", true),
                MVar(null, false, TPose(0, 1))));
            var log = new TickLog();
            TickWorld w = MotionWorld(new TickDesk(), log, null, null, t);
            TickMascot m1 = w.CreateAtStart("m1", t, new TickPoint(100, 680), false, "Stand");
            TickMascot m2 = w.CreateAtStart("m2", t, new TickPoint(160, 680), true, "Offer");
            w.StepOnce();
            SocialEnter(w, m1, "Seek");
            AssertTrue(ReferenceEquals(m2, m1.Target), "ScanJump did not choose the offering companion at init.");
            var seen = new List<string>();
            for (int i = 0; i < 4; i++) { w.StepOnce(); seen.Add(m1.Anchor.X + "," + m1.Anchor.Y); }
            AssertEqual("117,672|136,667|155,667|160,680", string.Join("|", seen), "ScanJump did not jump at its target with the arc and land within one step.");
            AssertTrue(log.Events.IndexOf("m1 behaviour Seek>Hugging forced") >= 0
                && log.Events.IndexOf("m2 behaviour Offer>Hugged partner") > log.Events.IndexOf("m1 behaviour Seek>Hugging forced"),
                "Landing did not enter the seeker's Behavior and then the target's: " + string.Join("; ", log.Events));
            // The seeker faces right (it jumped right); the target faced right too, so TargetLook turns it left.
            AssertTrue(m1.LookRight && !m2.LookRight, "ScanJump did not face its target, or TargetLook did not turn the target.");
        }

        // ---- ScanInteract

        private static XmlData.AnimationNode SocialPeek(XmlData.ParamNode[] ps, int ticks)
        {
            return MAct(30, "Peek", "Embedded", "ScanInteract", "Floor", ps, MVar(null, false, TPose(0, ticks)));
        }

        private static void TestTickSocialScanInteractReacquire()
        {
            // Remote: it scans in its tick whenever it has no offering target (ScanInteract.java:87-88), faces the target
            // (Look defaults to true) and keeps playing whether or not anyone offers.
            TickType t = SocialType("Peek", SocialPeek(SocialParams(null, null, false), 100));
            var log = new TickLog();
            TickWorld w = MotionWorld(new TickDesk(), log, null, null, t);
            TickMascot m1 = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Stand");
            TickMascot m2 = w.CreateAtStart("m2", t, new TickPoint(700, 680), false, "Offer");
            TickMascot m3 = w.CreateAtStart("m3", t, new TickPoint(500, 680), false, "Stand");
            w.StepOnce();
            SocialEnter(w, m1, "Peek");
            AssertTrue(m1.Target == null, "ScanInteract scanned at init; the source scans only in its tick.");
            w.StepOnce();
            AssertTrue(ReferenceEquals(m2, m1.Target) && m1.LookRight, "ScanInteract did not find m2 and face it (right).");

            // m2 stops offering and m3 starts. Their lists change when they step, after m1, so m1 rescans at step 4.
            SocialEnter(w, m2, "Stand");
            SocialEnter(w, m3, "Offer");
            w.StepOnce();
            AssertTrue(ReferenceEquals(m2, m1.Target), "ScanInteract dropped a target that still offered.");
            w.StepOnce();
            AssertTrue(ReferenceEquals(m3, m1.Target) && !m1.LookRight, "ScanInteract did not reacquire the next offering companion and face it (left).");

            // Nobody offers: it scans every tick and finds no one, and it does not end.
            SocialEnter(w, m3, "Stand");
            w.StepOnce();
            w.StepOnce();
            w.StepOnce();
            AssertEqual("m1 scan-target hug m2|m1 scan-target hug m3|m1 scan-target hug -|m1 scan-target hug -", SocialScans(log, "m1"),
                "ScanInteract did not rescan exactly when its target stopped offering.");
            AssertTrue(m1.Target == null && m1.Behaviour.Name == "Peek", "With no one offering ScanInteract kept a target or ended.");
        }

        private static void TestTickSocialScanInteractEnd()
        {
            // A two-tick animation: on its last tick (age 1), not turning, a non-blank own Behavior enters Hugging, then
            // the target's Hugged, then TargetLook turns the target (facing right, like the seeker) left.
            TickType t = SocialType("Peek", SocialPeek(SocialParams("Hugging", "Hugged", true), 2));
            var log = new TickLog();
            TickWorld w = MotionWorld(new TickDesk(), log, null, null, t);
            TickMascot m1 = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Stand");
            TickMascot m2 = w.CreateAtStart("m2", t, new TickPoint(700, 680), true, "Offer");
            w.StepOnce();
            SocialEnter(w, m1, "Peek");
            w.StepOnce();
            AssertTrue(m1.Behaviour.Name == "Peek" && m2.Behaviour.Name == "Offer", "ScanInteract's transitions ran before its last tick.");
            w.StepOnce();
            AssertTrue(m1.Behaviour.Name == "Hugging" && m2.Behaviour.Name == "Hugged" && !m2.LookRight,
                "ScanInteract's last tick did not enter both behaviours and turn the target.");

            // A blank own Behavior blocks everything (ScanInteract.java:114): the target keeps Offer, and at age 2 the
            // animation is over and the next behaviour (Stand) is selected.
            TickType u = SocialType("Peek", SocialPeek(SocialParams(null, "Hugged", true), 2));
            var log2 = new TickLog();
            TickWorld v = MotionWorld(new TickDesk(), log2, null, null, u);
            TickMascot n1 = v.CreateAtStart("m1", u, new TickPoint(640, 680), false, "Stand");
            TickMascot n2 = v.CreateAtStart("m2", u, new TickPoint(700, 680), true, "Offer");
            v.StepOnce();
            SocialEnter(v, n1, "Peek");
            for (int i = 0; i < 3; i++) v.StepOnce();
            AssertTrue(n1.Behaviour.Name == "Stand" && n2.Behaviour.Name == "Offer" && n2.LookRight,
                "A blank own Behavior did not block the target's transition: " + n1.Behaviour.Name + "/" + n2.Behaviour.Name);
            AssertEqual(0, log2.Faults.Count, "A blank own Behavior was treated as an error: " + string.Join("; ", log2.Faults));

            // A blank TargetBehavior only skips the target's transition; TargetLook still runs, and nothing is an error.
            TickType x = SocialType("Peek", SocialPeek(SocialParams("Hugging", null, true), 2));
            var log3 = new TickLog();
            TickWorld z = MotionWorld(new TickDesk(), log3, null, null, x);
            TickMascot p1 = z.CreateAtStart("m1", x, new TickPoint(640, 680), false, "Stand");
            TickMascot p2 = z.CreateAtStart("m2", x, new TickPoint(700, 680), true, "Offer");
            z.StepOnce();
            SocialEnter(z, p1, "Peek");
            z.StepOnce();
            z.StepOnce();
            AssertTrue(p1.Behaviour.Name == "Hugging" && p2.Behaviour.Name == "Offer" && !p2.LookRight && log3.Faults.Count == 0,
                "A blank TargetBehavior did not leave the target's behaviour alone while TargetLook still turned it.");
        }

        private static void TestTickSocialScanInteractPolicy()
        {
            // Duration 2 under a ten-tick animation. The source returns (guard && turning) || intime, so it would play all
            // ten ticks; the host ends it at age 2, when the guard fails (policy:scaninteract-precedence).
            TickType t = SocialType("Peek", SocialPeek(SocialParams(null, null, false, TParam("Duration", "2")), 10));
            TickWorld w = MotionWorld(new TickDesk(), new TickLog(), null, null, t);
            TickMascot m1 = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Stand");
            w.CreateAtStart("m2", t, new TickPoint(700, 680), false, "Offer");
            w.StepOnce();
            SocialEnter(w, m1, "Peek");
            w.StepOnce();
            w.StepOnce();
            AssertEqual("Peek", m1.Behaviour.Name, "ScanInteract ended before its Duration.");
            w.StepOnce();
            AssertEqual("Stand", m1.Behaviour.Name, "ScanInteract outlived its Duration (policy:scaninteract-precedence).");

            // A false Condition ends it at once, though its animation has time left.
            TickType u = SocialType("Peek", SocialPeek(SocialParams(null, null, false, TParam("Condition", "false")), 10));
            TickWorld v = MotionWorld(new TickDesk(), new TickLog(), null, null, u);
            TickMascot n1 = v.CreateAtStart("m1", u, new TickPoint(640, 680), false, "Stand");
            v.StepOnce();
            SocialEnter(v, n1, "Peek");
            AssertEqual("Stand", n1.Behaviour.Name, "ScanInteract started under a false Condition (policy:scaninteract-precedence).");
        }

        // ---- Interact

        private static void TestTickSocialInteract()
        {
            // Interact.java:34-37: it goes on while two or more active companions, itself included, stand on its anchor.
            TickType t = SocialType("Meet", MAct(30, "Meet", "Embedded", "Interact", "Floor", new[] { TParam("Behavior", "Hugging") },
                MVar(null, false, TPose(0, 5))));

            // Alone: the first check fails and the next behaviour starts at once.
            TickWorld a = MotionWorld(new TickDesk(), new TickLog(), null, null, t);
            TickMascot a1 = a.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Stand");
            a.CreateAtStart("m2", t, new TickPoint(700, 680), false, "Stand");
            a.StepOnce();
            SocialEnter(a, a1, "Meet");
            AssertEqual("Stand", a1.Behaviour.Name, "Interact held with no other companion on its anchor.");

            // Two on one anchor: it plays its five ticks and on the last (age 4) enters Hugging; the partner is untouched.
            var log = new TickLog();
            TickWorld b = MotionWorld(new TickDesk(), log, null, null, t);
            TickMascot b1 = b.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Stand");
            TickMascot b2 = b.CreateAtStart("m2", t, new TickPoint(640, 680), false, "Stand");
            b.StepOnce();
            SocialEnter(b, b1, "Meet");
            AssertEqual("Meet", b1.Behaviour.Name, "Interact did not hold with two companions on its anchor.");
            for (int i = 0; i < 4; i++) b.StepOnce();
            AssertEqual("Meet", b1.Behaviour.Name, "Interact ended before its animation's last tick.");
            b.StepOnce();
            AssertTrue(b1.Behaviour.Name == "Hugging" && b2.Behaviour.Name == "Stand", "Interact's Behavior did not change only this companion on its last tick.");
            AssertTrue(log.Events.Contains("m1 interact m2"), "No interact event naming the partner.");

            // Three: still holds. No partner is remembered: with m2 gone it holds on m3; with both gone it ends.
            var log3 = new TickLog();
            TickWorld c = MotionWorld(new TickDesk(), log3, null, null, t);
            TickMascot c1 = c.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Stand");
            TickMascot c2 = c.CreateAtStart("m2", t, new TickPoint(640, 680), false, "Stand");
            TickMascot c3 = c.CreateAtStart("m3", t, new TickPoint(640, 680), false, "Stand");
            c.StepOnce();
            SocialEnter(c, c1, "Meet");
            c.StepOnce();
            AssertTrue(log3.Events.Contains("m1 interact m2,m3"), "Interact did not report both companions sharing its anchor: " + string.Join("; ", log3.Events));
            c2.Anchor = new TickPoint(700, 680);
            c.StepOnce();
            AssertTrue(c1.Behaviour.Name == "Meet" && log3.Events.Contains("m1 interact m3"), "Interact remembered a partner instead of counting the anchor.");
            c3.Anchor = new TickPoint(500, 680);
            c.StepOnce();
            AssertEqual("Stand", c1.Behaviour.Name, "Interact held after everyone left its anchor.");

            // A start companion has no manager while its behaviour initialises: the source's unguarded manager call
            // fails there, and the engine refuses the companion.
            TickWorld d = MotionWorld(new TickDesk(), new TickLog(), null, null, t);
            AssertThrows<TickCantBeAliveException>(() => d.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Meet"),
                "Interact started on a companion with no manager.");
        }

        // ---- ComplexMove and ComplexJump

        private static XmlData.AnimationNode SocialComplexMove(string characteristics)
        {
            return MAct(30, "Seek", "Embedded", "ComplexMove", "Floor",
                SocialParams("Hugging", "Hugged", false, TParam("Characteristics", characteristics),
                    TParam("BornX", "10"), TParam("BornMascot", "Alpha"), TParam("BornBehavior", "Float"), TParam("BornInterval", "2")),
                MVar(null, false, TPose(0, 1, -4)), MVar(null, true, TPose(3, 2)));
        }

        private static List<string> SocialRunComplexMove(string characteristics, TickLog log, out TickMascot seeker, out TickMascot target)
        {
            TickType t = SocialType("Seek", SocialComplexMove(characteristics));
            TickWorld w = MotionWorld(new TickDesk(), log, null, null, t);
            seeker = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Stand");
            target = w.CreateAtStart("m2", t, new TickPoint(656, 680), false, "Offer");
            w.StepOnce();
            SocialEnter(w, seeker, "Seek");
            for (int i = 0; i < 6; i++) w.StepOnce();
            return log.Events.FindAll(e => e.StartsWith("m1 birth", StringComparison.Ordinal));
        }

        private static void TestTickSocialComplexMove()
        {
            // "Breed,Scan": scans for m2 at 656 (behind it, so it turns first), BornInterval 2, BornX 10. Ages 0-1 play the
            // two-tick turn (no birth at age 0 though it is an interval frame: never while turning); then it walks 4 px a
            // tick facing right: 644 (age 2, birth at 644 - 10 = 634), 648, 652 (age 4, birth at 642), 656 (age 5): exact
            // arrival, Hugging and Hugged.
            var log = new TickLog();
            TickMascot m1, m2;
            List<string> births = SocialRunComplexMove("Breed,Scan", log, out m1, out m2);
            AssertEqual("m1 birth m1.b1 #1 try1 at (634, 680)|m1 birth m1.b2 #2 try2 at (642, 680)", string.Join("|", births),
                "ComplexMove did not breed each BornInterval step after its turn.");
            AssertEqual("m1 scan-target hug m2", SocialScans(log, "m1"), "ComplexMove with Scan did not scan once at init.");
            AssertTrue(m1.Behaviour.Name == "Hugging" && m2.Behaviour.Name == "Hugged", "ComplexMove with Scan did not run the partner transitions on arrival.");

            // "Scan" alone: the same walk and arrival, and no births.
            var log2 = new TickLog();
            births = SocialRunComplexMove("Scan", log2, out m1, out m2);
            AssertEqual(0, births.Count, "ComplexMove bred without Breed in its Characteristics.");
            AssertTrue(m1.Behaviour.Name == "Hugging" && m2.Behaviour.Name == "Hugged", "ComplexMove with Scan alone did not arrive.");

            // "Breed, Scan": the split is exact, so " Scan" is not Scan. No scan; with no TargetX (Integer.MAX_VALUE) it
            // faces right, unguarded as Move is not (ComplexMove.java:144-149).
            var log3 = new TickLog();
            SocialRunComplexMove("Breed, Scan", log3, out m1, out m2);
            AssertEqual("", SocialScans(log3, "m1"), "ComplexMove scanned on a Characteristics piece that is not exactly Scan.");
            AssertTrue(m1.Target == null && m1.LookRight && m2.Behaviour.Name == "Offer", "ComplexMove without Scan did not walk right toward the default TargetX.");
        }

        private static void TestTickSocialComplexMovePolicy()
        {
            // Scanning off, from (600, 600) to TargetX/TargetY (610, 610), 5 px a tick on both axes, no border: it reaches
            // both at age 1. The source then enters its own Behavior and dereferences a null target; the host skips the
            // arrival (policy:complexmove-guarded), and the reached target ends the move as Move's does.
            var pose = new XmlData.PoseNode { Frame = 0, Ticks = 1, DX = -5, DY = 5 };
            TickType t = SocialType("Seek", MAct(30, "Seek", "Embedded", "ComplexMove", "",
                new[] { TParam("Behavior", "Hugging"), TParam("TargetX", "610"), TParam("TargetY", "610") },
                new XmlData.VariantNode { Pose = new[] { pose } }));
            var log = new TickLog();
            TickWorld w = MotionWorld(new TickDesk(), log, null, null, t);
            TickMascot m1 = w.CreateAtStart("m1", t, new TickPoint(600, 600), true, "Seek");
            w.StepOnce();
            AssertEqual(new TickPoint(605, 605), m1.Anchor, "ComplexMove did not move by its pose toward the target.");
            w.StepOnce();
            AssertEqual(new TickPoint(610, 610), m1.Anchor, "ComplexMove did not reach its target.");
            AssertFalse(log.Events.Exists(e => e.Contains(">Hugging")), "ComplexMove ran its arrival with scanning off (policy:complexmove-guarded).");
            AssertEqual(0, log.Faults.Count, "ComplexMove's arrival with scanning off reported a fault: " + string.Join("; ", log.Faults));
            AssertEqual("Stand", m1.Behaviour.Name, "ComplexMove did not end on reaching its target.");
        }

        private static void TestTickSocialComplexJump()
        {
            // "Scan": the ScanJump arithmetic (see that group) from (100, 680) to m2 at (160, 680), then both transitions.
            TickType t = SocialType("Seek", MAct(30, "Seek", "Embedded", "ComplexJump", "",
                SocialParams("Hugging", "Hugged", false, TParam("Characteristics", "Scan")), MVar(null, false, TPose(0, 1))));
            var log = new TickLog();
            TickWorld w = MotionWorld(new TickDesk(), log, null, null, t);
            TickMascot m1 = w.CreateAtStart("m1", t, new TickPoint(100, 680), false, "Stand");
            TickMascot m2 = w.CreateAtStart("m2", t, new TickPoint(160, 680), false, "Offer");
            w.StepOnce();
            SocialEnter(w, m1, "Seek");
            var seen = new List<string>();
            for (int i = 0; i < 4; i++) { w.StepOnce(); seen.Add(m1.Anchor.X + "," + m1.Anchor.Y); }
            AssertEqual("117,672|136,667|155,667|160,680", string.Join("|", seen), "ComplexJump with Scan did not jump at its target.");
            AssertTrue(m1.Behaviour.Name == "Hugging" && m2.Behaviour.Name == "Hugged", "ComplexJump with Scan did not run the partner transitions on landing.");

            // "Breed": a plain straight jump (Arc false) from (100, 600) to (160, 600): 120, 140, then within 20 it lands
            // on 160. It breeds after the jump at ages 0 and 2, landing included, at the anchor - 5 (facing right):
            // (115, 600), (155, 600). Its own Behavior is never entered: the source guards this arrival by Scan.
            TickType u = SocialType("Seek", MAct(30, "Seek", "Embedded", "ComplexJump", "",
                new[] { TParam("Characteristics", "Breed"), TParam("Behavior", "Hugging"), TParam("TargetX", "160"), TParam("TargetY", "600"),
                        TParam("Arc", "false"), TParam("BornX", "5"), TParam("BornMascot", "Alpha"), TParam("BornBehavior", "Float"), TParam("BornInterval", "2") },
                MVar(null, false, TPose(0, 1))));
            var log2 = new TickLog();
            TickWorld v = MotionWorld(new TickDesk(), log2, null, null, u);
            TickMascot n1 = v.CreateAtStart("m1", u, new TickPoint(100, 600), false, "Seek");
            for (int i = 0; i < 3; i++) v.StepOnce();
            AssertEqual("m1 birth m1.b1 #1 try1 at (115, 600)|m1 birth m1.b2 #2 try2 at (155, 600)",
                string.Join("|", log2.Events.FindAll(e => e.StartsWith("m1 birth", StringComparison.Ordinal))),
                "ComplexJump with Breed did not breed after each BornInterval jump step.");
            AssertFalse(log2.Events.Exists(e => e.Contains(">Hugging")) || log2.Faults.Count > 0,
                "ComplexJump without Scan ran its arrival: " + string.Join("; ", log2.Faults));
            AssertEqual(new TickPoint(160, 600), n1.Anchor, "ComplexJump without Scan did not land on TargetX/TargetY.");
        }
    }
}
