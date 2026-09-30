using System;
using System.Collections.Generic;
using System.Text;
using DesktopAICompanion.Tools.ShimejiConvert.Emit;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// Committed, IP-free unit test of the parser + classifier. The real gil/shimeji-ee config cannot live in
    /// this repo (it is copyrighted, and the handoff forbids it), so the gate cannot assert the 91/53/32/6
    /// census against it. Instead this exercises every classification branch with a hand-written synthetic
    /// actions.xml whose actions are named "G1_", "G2_" or "G3_" after the group they must land in, and
    /// asserts each one buckets correctly. The 91/53/32/6 validation against the actual reference config is a
    /// dev step: `ShimejiConvert classify &lt;conf-dir&gt;` against an external clone.
    /// </summary>
    public static class ClassifierSelfTest
    {
        public static bool Run(out string detail)
        {
            var failures = new List<string>();

            ShimejiConfig config;
            try
            {
                config = ShimejiParser.ParseActionsXml(SyntheticActionsXml);
            }
            catch (Exception ex)
            {
                detail = "parser threw: " + ex.Message;
                return false;
            }

            // Every action name encodes its expected group as the first two characters (G1/G2/G3).
            int checkedCount = 0;
            foreach (ShimejiAction a in config.Actions)
            {
                if (string.IsNullOrEmpty(a.Name) || a.Name.Length < 2 || a.Name[0] != 'G')
                {
                    failures.Add("action '" + a.Name + "' is not named G1_/G2_/G3_");
                    continue;
                }
                FidelityGroup expected;
                switch (a.Name[1])
                {
                    case '1': expected = FidelityGroup.Group1; break;
                    case '2': expected = FidelityGroup.Group2; break;
                    case '3': expected = FidelityGroup.Group3; break;
                    default: failures.Add("action '" + a.Name + "' has no valid group digit"); continue;
                }
                checkedCount++;
                if (a.Group != expected)
                    failures.Add(string.Format("{0}: expected {1} but got {2} ({3})", a.Name, expected, a.Group, a.Reason));
            }

            // Guard against a fixture that silently stopped parsing: it must cover all three groups.
            if (checkedCount < 12)
                failures.Add("expected at least 12 classified actions from the fixture, got " + checkedCount);

            // A reason is user-facing: it is what the residue report prints for a degraded or dropped action.
            // So it must not PROMISE work. Three of these once read "added in Stage 5", naming a plan that
            // does not exist -- and one of them (the gaze) had already shipped as `faceCursor`, so the report
            // was describing a delivered capability as pending. The cursor-chase half is parked on a
            // judgement call and `totalCount` has zero occurrences across the shipping skins, so neither is
            // scheduled either. Asserted on the CONDITION (no reason contains the phrase) rather than on the
            // presence of any particular wording, because a check that a sentence exists survives the
            // sentence becoming false.
            foreach (ShimejiAction a in config.Actions)
            {
                string reason = a.Reason ?? "";
                foreach (string promise in PendingWorkPromises)
                {
                    if (reason.IndexOf(promise, StringComparison.OrdinalIgnoreCase) >= 0)
                        failures.Add("action '" + a.Name + "' reason promises unscheduled work (\"" + promise + "\"): " + reason);
                }
            }

            // The classifier and the emitter are two halves of one contract, and nothing compared them.
            // ActionClassifier graded Regist, the Broadcast family and MoveWithTurn as Group1 with reasons
            // that promise "converts as ordinary frames", and Jump as Group1 "jump arc" -- while
            // PetEmitter.IsFloorAction refused every action carrying a Class outright. The result was not a
            // wrong answer on either side: it was silent disagreement, reported as "not attempted" in the
            // residue and measured on 2026-09-25 at 29 animations across 11 of the 12 desktop-sourced
            // companions. Assert the AGREEMENT, so adding a class to one side without the other fails here.
            //
            // The first version of this check took BOTH booleans from PetEmitter (IsFloorAction, which is
            // implemented in terms of IsFramePlayingEmbeddedClass), so for every classed Group1 fixture
            // action they agreed by construction, and a class added to either side alone passed (F446).
            // The classifier's half is now read from the classifier: the REASON it printed. A reason that
            // promises frames ("frames", "jump arc") means IsFloorAction must admit the action; one that says
            // the action is absorbed elsewhere ("magic", "facing change", "positional nudge") means it must
            // refuse. Brittle on purpose: the reason is what the residue report shows the user, so a rewording
            // that breaks this line is a rewording that changed the promise.
            int contractCases = 0;
            foreach (ShimejiAction a in config.Actions)
            {
                if (a.Class == null || a.Group != FidelityGroup.Group1) continue;
                bool promisesFrames = ReasonPromisesFrames(a);
                bool absorbed = ReasonSaysAbsorbed(a);
                if (promisesFrames == absorbed)
                {
                    failures.Add("contract: '" + a.Name + "' (" + a.Class + ") is Group1 with a reason this check " +
                                 "cannot read as a frames promise or as an absorption: " + a.Reason);
                    continue;
                }
                contractCases++;
                bool emitterTakesIt = PetEmitter.IsFloorAction(a);
                if (promisesFrames && !emitterTakesIt)
                    failures.Add("contract: classifier grades '" + a.Name + "' (" + a.Class +
                                 ") Group1 with frames, but PetEmitter.IsFloorAction refuses it -- " +
                                 "it will be reported as 'not attempted'");
                if (absorbed && emitterTakesIt)
                    failures.Add("contract: '" + a.Name + "' (" + a.Class + ") is absorbed as a magic " +
                                 "animation, a flip or a nudge, yet IsFloorAction admits it -- it " +
                                 "would be emitted twice");
            }
            if (contractCases < 8)
                failures.Add("contract: expected at least 8 classed Group1 fixture actions to compare, got " +
                             contractCases + " -- the fixture stopped exercising the contract");

            // The list itself, from the emitter's side, on a synthetic one-pose action per class so no
            // fixture entry is needed for a class to be covered: everything the emitter would admit must come
            // back from the classifier as Group1 WITH a frames promise. A class added to FramePlayingClasses
            // that an earlier rule already grades Group3 (Breed, ScanMove, ...) fails here.
            int listed = 0;
            foreach (string shortClass in ActionClassifier.FramePlayingClasses)
            {
                listed++;
                ShimejiAction synthetic = OnePoseEmbedded(shortClass);
                ActionClassifier.Classify(synthetic);
                if (synthetic.Group != FidelityGroup.Group1 || !ReasonPromisesFrames(synthetic))
                    failures.Add("contract: '" + shortClass + "' is in ActionClassifier.FramePlayingClasses but " +
                                 "classifies as " + synthetic.Group + " (" + synthetic.Reason + ")");
                if (!PetEmitter.IsFloorAction(synthetic))
                    failures.Add("contract: a one-pose floor action of class '" + shortClass +
                                 "' is refused by PetEmitter.IsFloorAction");
            }
            if (listed < 5)
                failures.Add("contract: FramePlayingClasses lists " + listed + " classes; the shipped list had 7");
            // WITNESS: the loop above is not satisfied by "admit everything". A class outside the list that
            // the classifier grades Group1 by absorption must be REFUSED by the emitter.
            ShimejiAction magic = OnePoseEmbedded("Fall");
            ActionClassifier.Classify(magic);
            if (magic.Group != FidelityGroup.Group1 || !ReasonSaysAbsorbed(magic) || PetEmitter.IsFloorAction(magic))
                failures.Add("WITNESS: a synthetic Fall must be Group1 (magic 'fall'), absorbed, and refused by " +
                             "IsFloorAction; got " + magic.Group + " / " + magic.Reason + " / admitted=" +
                             PetEmitter.IsFloorAction(magic));

            // An embedded Jump carries its launch in VelocityParam on the action, not in a pose, so its
            // poses read Velocity="0,0". Admitting it to the floor graph WITHOUT recognising it as a jump
            // emits an animation that never leaves the ground, which looks like a defect rather than a gap.
            foreach (ShimejiAction a in config.Actions)
            {
                if (!string.Equals(a.Class, "Jump", StringComparison.Ordinal)) continue;
                if (!PetEmitter.QualifiesAsJump(a))
                    failures.Add("'" + a.Name + "' is an embedded Jump but QualifiesAsJump is false; it " +
                                 "would emit flat along the floor");
            }

            var sb = new StringBuilder();
            sb.AppendLine("classifier self-test: " + checkedCount + " synthetic actions across Group1/2/3");
            if (failures.Count == 0)
            {
                sb.Append("  all classified as named");
                detail = sb.ToString();
                return true;
            }
            foreach (string f in failures) sb.AppendLine("  FAIL " + f);
            detail = sb.ToString();
            return false;
        }

        /// <summary>Phrasings that turn a residue line into a roadmap. None may appear in a classifier
        /// reason.</summary>
        private static readonly string[] PendingWorkPromises =
        {
            "added in Stage", "will be added", "coming in Stage", "once Stage"
        };

        /// <summary>The classifier's half of the emitter contract, as the user reads it: a Group1 reason that
        /// says the frames will play (or that the jump arc is reproduced).</summary>
        private static bool ReasonPromisesFrames(ShimejiAction a)
        {
            string reason = a.Reason ?? "";
            return reason.IndexOf("frames", StringComparison.Ordinal) >= 0
                   || reason.IndexOf("jump arc", StringComparison.Ordinal) >= 0;
        }

        /// <summary>The opposite promise: the action is absorbed into a magic animation name, a flip or a
        /// positional nudge, so the emitter must NOT also play it as a spoke.</summary>
        private static bool ReasonSaysAbsorbed(ShimejiAction a)
        {
            string reason = a.Reason ?? "";
            return reason.IndexOf("magic", StringComparison.Ordinal) >= 0
                   || reason.IndexOf("facing change", StringComparison.Ordinal) >= 0
                   || reason.IndexOf("positional nudge", StringComparison.Ordinal) >= 0;
        }

        /// <summary>A minimal embedded action of the given class with one posed frame and no border, which is
        /// the shape IsFloorAction admits for every frame-playing class.</summary>
        private static ShimejiAction OnePoseEmbedded(string shortClass)
        {
            var action = new ShimejiAction { Name = "Synthetic_" + shortClass, Type = "Embedded", Class = shortClass };
            var animation = new ShimejiAnimation();
            animation.Poses.Add(new ShimejiPose { Image = "/synthetic.png", AnchorX = 64, AnchorY = 128, Duration = 5 });
            action.Animations.Add(animation);
            return action;
        }

        // One action per classification branch. Names carry the expected group. Kept minimal and clearly
        // synthetic -- this is our content, not Shimeji's.
        private const string SyntheticActionsXml =
@"<?xml version=""1.0"" encoding=""UTF-8"" ?>
<Mascot xmlns=""http://www.group-finity.com/Mascot"">
  <ActionList>
    <Action Name=""G1_Walk"" Type=""Move"" BorderType=""Floor"">
      <Animation><Pose Image=""/a.png"" ImageAnchor=""64,128"" Velocity=""-2,0"" Duration=""6"" /></Animation>
    </Action>
    <Action Name=""G1_GrabCeiling"" Type=""Stay"" BorderType=""Ceiling"">
      <Animation><Pose Image=""/b.png"" ImageAnchor=""64,48"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <Action Name=""G1_Look"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Look"" />
    <Action Name=""G1_Offset"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Offset"" />
    <Action Name=""G1_Falling"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Fall"" Gravity=""2"">
      <Animation><Pose Image=""/c.png"" ImageAnchor=""64,128"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <Action Name=""G1_Dragged"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Dragged"">
      <Animation><Pose Image=""/d.png"" ImageAnchor=""64,128"" Velocity=""0,0"" Duration=""5"" /></Animation>
    </Action>
    <Action Name=""G1_Jumping"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Jump"" VelocityParam=""20"">
      <Animation><Pose Image=""/e.png"" ImageAnchor=""64,128"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <Action Name=""G1_Resisting"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Regist"">
      <Animation><Pose Image=""/i.png"" ImageAnchor=""64,128"" Velocity=""0,0"" Duration=""5"" /></Animation>
    </Action>
    <Action Name=""G1_CryingBroadcast"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Broadcast"">
      <Animation><Pose Image=""/j.png"" ImageAnchor=""64,128"" Velocity=""0,0"" Duration=""8"" /></Animation>
    </Action>
    <Action Name=""G1_MoveWithTurn"" Type=""Embedded"" Class=""com.group_finity.mascot.action.MoveWithTurn"">
      <Animation><Pose Image=""/k.png"" ImageAnchor=""64,128"" Velocity=""-2,0"" Duration=""6"" /></Animation>
    </Action>
    <Action Name=""G1_SelfDestruct"" Type=""Embedded"" Class=""com.group_finity.mascot.action.SelfDestruct"">
      <Animation><Pose Image=""/l.png"" ImageAnchor=""64,128"" Velocity=""0,0"" Duration=""16"" /></Animation>
    </Action>
    <Action Name=""G2_Cursor"" Type=""Stay"" BorderType=""Floor"">
      <Animation Condition=""#{mascot.environment.cursor.x &lt; 100}""><Pose Image=""/f.png"" ImageAnchor=""64,128"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <Action Name=""G2_Ie"" Type=""Sequence"">
      <ActionReference Name=""G1_Walk"" TargetX=""${mascot.environment.activeIE.left}"" />
    </Action>
    <Action Name=""G2_BreedCap"" Type=""Sequence"" Condition=""#{mascot.totalCount &lt; 50}"">
      <ActionReference Name=""G1_Walk"" />
    </Action>
    <Action Name=""G2_Anchor"" Type=""Sequence"" Condition=""#{mascot.anchor.x &lt; 400}"">
      <ActionReference Name=""G1_Walk"" />
    </Action>
    <Action Name=""G3_ThrowIe"" Type=""Embedded"" Class=""com.group_finity.mascot.action.ThrowIE"" InitialVX=""32"">
      <Animation><Pose Image=""/g.png"" ImageAnchor=""64,128"" Velocity=""0,0"" Duration=""40"" /></Animation>
    </Action>
    <Action Name=""G3_Breed"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Breed"" BornBehavior=""PullUp"">
      <Animation><Pose Image=""/h.png"" ImageAnchor=""64,128"" Velocity=""0,0"" Duration=""16"" /></Animation>
    </Action>
  </ActionList>
</Mascot>";
    }
}
