using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using DesktopAICompanion.Tools.ShimejiConvert.Emit;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// Committed, IP-free end-to-end test of the emitter: a synthetic Shimeji config (a few primitives + a
    /// Fall + a Dragged + a cursor action + a ThrowIE) is parsed, composited from synthetic sprites, and
    /// emitted, then the result must be ACCEPTED -- the app's own validator passes it, it round-trips, and
    /// every animation is reachable. It also checks the residue captured the Group3 drop and Group2 degrade.
    /// Converting a REAL skin is the dev command `ShimejiConvert convert`.
    /// </summary>
    public static class EmitterSelfTest
    {
        public static bool Run(out string detail)
        {
            var failures = new List<string>();

            var owned = new Dictionary<string, Bitmap>(StringComparer.Ordinal)
            {
                { "/s.png", Solid(40, 60, Color.FromArgb(255, 200, 200, 200)) },
                { "/w1.png", Solid(40, 60, Color.FromArgb(255, 180, 180, 180)) },
                { "/w2.png", Solid(40, 60, Color.FromArgb(255, 160, 160, 160)) },
                { "/f.png", Solid(40, 60, Color.FromArgb(255, 120, 120, 255)) },
                { "/p.png", Solid(40, 60, Color.FromArgb(255, 255, 200, 120)) },
                { "/m.png", Solid(40, 60, Color.FromArgb(255, 200, 255, 200)) },
                { "/m2.png", Solid(40, 60, Color.FromArgb(255, 185, 245, 185)) },
                { "/mn.png", Solid(40, 60, Color.FromArgb(255, 170, 235, 170)) },
                { "/g1.png", Solid(40, 60, Color.FromArgb(255, 140, 210, 150)) },
                { "/g2.png", Solid(40, 60, Color.FromArgb(255, 120, 190, 130)) },
                // For the action whose NAME contains "Cursor" but whose behaviour has nothing to do with
                // one. Its own art, so a direction collapse cannot merge it into a neighbour and hide the
                // answer.
                { "/nc.png", Solid(40, 60, Color.FromArgb(255, 90, 160, 110)) },
                // For the action whose sprite FILE is named after the cursor (the Victim skin's shape). Its
                // own art, like /nc.png, so a collapse cannot hide the answer.
                { "/cursorsetup01.png", Solid(40, 60, Color.FromArgb(255, 80, 150, 100)) },
                { "/t.png", Solid(40, 60, Color.FromArgb(255, 255, 120, 120)) },
                { "/c1.png", Solid(40, 60, Color.FromArgb(255, 120, 255, 255)) },
                { "/c2.png", Solid(40, 60, Color.FromArgb(255, 100, 235, 235)) },
                { "/k1.png", Solid(40, 60, Color.FromArgb(255, 250, 240, 60)) },
                { "/k2.png", Solid(40, 60, Color.FromArgb(255, 230, 220, 40)) },
                { "/k3.png", Solid(40, 60, Color.FromArgb(255, 190, 120, 240)) },
                { "/k4.png", Solid(40, 60, Color.FromArgb(255, 170, 100, 220)) },
                { "/j1.png", Solid(40, 60, Color.FromArgb(255, 120, 200, 120)) },
                { "/j2.png", Solid(40, 60, Color.FromArgb(255, 100, 180, 100)) },
                { "/h1.png", Solid(40, 60, Color.FromArgb(255, 200, 160, 240)) },
                { "/h2.png", Solid(40, 60, Color.FromArgb(255, 180, 140, 220)) },
                { "/u1.png", Solid(40, 60, Color.FromArgb(255, 240, 200, 160)) },
                { "/u2.png", Solid(40, 60, Color.FromArgb(255, 220, 180, 140)) },
                { "/u3.png", Solid(40, 60, Color.FromArgb(255, 200, 160, 120)) },
                { "/hu.png", Solid(40, 60, Color.FromArgb(255, 160, 200, 240)) },
                { "/l1.png", Solid(40, 60, Color.FromArgb(255, 90, 140, 200)) },
                { "/l2.png", Solid(40, 60, Color.FromArgb(255, 70, 120, 180)) },
                // Distinct colours on purpose: reusing BigJump's /j1 and /j2 made the embedded hop a
                // byte-identical twin, CollapseDirectionPairs merged the two, and the fixture silently
                // stopped testing the case it was added for.
                { "/e1.png", Solid(40, 60, Color.FromArgb(255, 250, 150, 200)) },
                { "/e2.png", Solid(40, 60, Color.FromArgb(255, 230, 130, 180)) },
            };

            try
            {
                ShimejiConfig config = ShimejiParser.ParseActionsXml(SyntheticActionsXml);
                // ParseActionsXml reads actions only; a real skin's frequencies arrive from behaviors.xml.
                // GatorRide's frequency weights its hub entry but does NOT decide whether it is chained: its
                // legs are withheld (BorderType="None"), so it enters ExpandSetPieces through the RECOVERS
                // gate whatever this says. The comment here used to claim it exercised both halves of the
                // gate; it exercised one, and the PLAYED term could be deleted with the suite green (F447).
                // The PLAYED half is decided by StrollAndHop below, whose members are ordinary spokes and
                // which is chained ONLY because of this frequency; its twin StrollAndHopUnplayed has none.
                const int StrollFrequency = 40;
                config.BehaviorFrequency["GatorRide"] = 60;
                config.BehaviorFrequency["StrollAndHop"] = StrollFrequency;
                // StrollBackPlayed is StrollBack (the collapsed WalkBack, then Bounce) WITH a frequency, so it
                // is chained through PLAYED and its first step must play the survivor's poses (N-tools-01).
                config.BehaviorFrequency["StrollBackPlayed"] = 25;
                // A frequency on the COLLAPSED mirror and one on the GAZE (RA-375). WalkBack merges into Walk,
                // so its 30 must reach Walk's hub edge; SitAndLookAtMouse is Group2 (cursor state) and used to
                // sit at the base weight however often the artist played it. Both are asserted below against
                // HubWeightFromFrequency, with the base weight as the WITNESS that the frequency was read.
                const int WalkBackFrequency = 30;
                const int GazeFrequency = 80;
                config.BehaviorFrequency["WalkBack"] = WalkBackFrequency;
                config.BehaviorFrequency["SitAndLookAtMouse"] = GazeFrequency;

                Func<string, Bitmap> load = delegate(string name) { return new Bitmap(owned[name]); };

                SpriteSheet sheet;
                string error;
                if (!SpriteSheetBuilder.Build(Emit.PetEmitter.PosesToComposite(config), load, false, out sheet, out error))
                {
                    detail = "emitter self-test: compositing failed -- " + error;
                    return false;
                }

                ConversionResult r = PetEmitter.Emit(config, sheet, load, "TestSkin");

                // The conversion's graph is RELEASED when Emit returns. The three per-conversion statics
                // used to keep the last skin's every floor Emitted -- and through it the parsed action,
                // its SubtreeBlob, poses and references -- rooted until the next conversion or, in
                // PetStudio, for the life of the module after one import (F430). This fixture has chains
                // and a collapsed mirror, so all three sets are non-empty during the emit; zero afterwards
                // is the assertion, and it cannot pass by the sets never having been filled.
                if (PetEmitter.RetainedConversionState != 0)
                    failures.Add("PetEmitter still holds " + PetEmitter.RetainedConversionState + " item(s) of "
                        + "per-conversion state after Emit returned, so the last skin's action graph stays "
                        + "rooted until the next conversion");

                if (!r.Valid) failures.Add("emitted XML failed the validator: " + r.Error);
                if (!r.RoundTrips) failures.Add("emitted XML did not round-trip: " + r.Error);
                if (r.Graph == null || r.Graph.Unreachable.Count != 0)
                    failures.Add("emitted pet has unreachable animations: " + (r.Graph == null ? "(no graph)" : string.Join(",", r.Graph.Unreachable)));
                if (!r.Accepted) failures.Add("result not accepted (valid+roundtrip+reachable)");

                // The header must carry the character's name UNDECORATED. Title used to be
                // skinName + " (converted)", which said nothing the Author line does not already say, and
                // CompanionCatalog.ReadHeaderName stripped it straight back off to get a usable label.
                // Asserting EQUALITY with the skin name rather than the absence of a suffix, because
                // "does not end with (converted)" would also pass for a title that had picked up some other
                // decoration, and the property wanted is that the name arrives untouched.
                if (r.Root == null || r.Root.Header == null)
                    failures.Add("emitted pet has no header to check");
                else
                {
                    if (r.Root.Header.Title != "TestSkin")
                        failures.Add("header title should be the bare skin name, got '" + (r.Root.Header.Title ?? "(null)") + "'");
                    if (r.Root.Header.Petname != "TestSkin")
                        failures.Add("header petname should be the bare skin name, got '" + (r.Root.Header.Petname ?? "(null)") + "'");
                    // Provenance still has to be stated, just in the field that is for it.
                    if (r.Root.Header.Author != PetEmitter.ConvertedAuthor)
                        failures.Add("header author should name the converter, got '" + (r.Root.Header.Author ?? "(null)") + "'");
                }

                // Guard the invisible-pet bug: a spawn that places the pet fully off-screen horizontally and
                // routes to a stationary animation leaves it invisible. Evaluate each spawn's X against a
                // fake 1920-wide screen and require the pet to land within the horizontal bounds. (Y may be
                // above the top on purpose -- that spawn falls in.)
                if (r.Root != null && r.Root.Spawns != null && r.Root.Spawns.Spawn != null)
                {
                    foreach (XmlData.SpawnNode sp in r.Root.Spawns.Spawn)
                    {
                        // Over the engine's REAL ranges -- random is 0..99 and randS 10..89 -- not one draw
                        // at 50. Any linear expression in random lands inside a 1920-wide screen at its
                        // midpoint, so a spawn that had lost its imageW or margin term passed here while
                        // leaving the pet part-way off screen at the top of the range (F448). The endpoints
                        // are where that shows, and the FAIL names the draw that failed.
                        //
                        // The randS axis is DEGENERATE today, and that is recorded rather than hidden: the two
                        // spawn expressions the emitter writes read `random` alone, so the inner loop evaluates
                        // each `random` draw three times over and F448's recorded 3x3 sweep is a 3-point sweep
                        // (R-071). It stays, at the cost of six trivial evaluations per spawn, so a spawn written
                        // against randS tomorrow is covered the day it lands. What the sweep cannot be allowed
                        // to do is pass on a spawn that reads NEITHER variable -- a constant X sits inside the
                        // screen at every draw -- so the resolver records the names each spawn asked for and
                        // `random` must be among them.
                        var requested = new HashSet<string>(StringComparer.Ordinal);
                        foreach (int random in new[] { 0, 50, 99 })
                            foreach (int randS in new[] { 10, 50, 89 })
                            {
                                int x = EvalOnFakeScreen(sp.X, sheet.CellWidth, sheet.CellHeight, random, randS, requested);
                                if (x < 0 || x > 1920 - sheet.CellWidth)
                                    failures.Add("spawn " + sp.Id + " lands the pet off-screen horizontally (x=" + x +
                                        " of 1920 at random=" + random + ", randS=" + randS + ")");
                            }
                        if (!requested.Contains("random"))
                            failures.Add("WITNESS: spawn " + sp.Id + "'s X expression never read `random`, so the sweep above "
                                + "evaluated a constant and exercised nothing");
                    }
                }

                // ---- SET-PIECE CHAIN: ORDER IS A GUARANTEE, NOT A WEIGHTING ----
                // The property under test is not "a chain was emitted" but "a member cannot be reached except
                // through the run, and the run cannot be abandoned part-way". Those are different claims, and
                // only the second one prevents a pet walking off screen and staying there. Every step is
                // checked, not just the entry: the bug this replaces (fixed in 40ab832) had a correct entry
                // and correct sequence edges while gravity and the border still offered every step a way out.
                var chainSteps = new List<XmlData.AnimationNode>();
                foreach (XmlData.AnimationNode a in r.Root != null && r.Root.Animations != null &&
                                                    r.Root.Animations.Animation != null
                             ? r.Root.Animations.Animation : new XmlData.AnimationNode[0])
                    if (a != null && a.Name != null && a.Name.StartsWith("GatorRide_", StringComparison.Ordinal))
                        chainSteps.Add(a);
                if (chainSteps.Count != 3)
                    failures.Add("expected 3 set-piece chain steps for GatorRide, got " + chainSteps.Count +
                        "; the whole set-piece block below is untested without them");
                else
                {
                    int chainHubId = HubId(r);
                    List<int> hubTargetsForChain = HubSequenceTargets(r);
                    XmlData.AnimationNode s1 = FindAnimationNamed(r, "GatorRide_1_Walk");
                    XmlData.AnimationNode s2 = FindAnimationNamed(r, "GatorRide_2_RunOff");
                    XmlData.AnimationNode s3 = FindAnimationNamed(r, "GatorRide_3_ReturnOn");
                    if (s1 == null || s2 == null || s3 == null)
                        failures.Add("set-piece steps are not named <sequence>_<n>_<member> in source order: " +
                            string.Join(", ", chainSteps.ConvertAll(delegate(XmlData.AnimationNode a) { return a.Name; }).ToArray()));
                    else
                    {
                        // Exactly ONE entry point. A member that is selectable on its own is the stranding
                        // hazard itself: RunOff travels off screen and only ReturnOn brings the pet back.
                        if (!hubTargetsForChain.Contains(s1.Id))
                            failures.Add("the set-piece's first step is not selectable from the hub, so the run can never start");
                        if (hubTargetsForChain.Contains(s2.Id) || hubTargetsForChain.Contains(s3.Id))
                            failures.Add("a set-piece MEMBER is selectable from the hub on its own; RunOff " +
                                "walks the pet off screen and its return leg is then merely likely");
                        // Chaining must not consume the ordinary spoke. Walk is a member here AND a walk.
                        XmlData.AnimationNode plainWalk = FindAnimationNamed(r, "Walk");
                        if (plainWalk == null || !hubTargetsForChain.Contains(plainWalk.Id))
                            failures.Add("'Walk' stopped being an ordinary hub spoke because a set-piece uses it");

                        var order = new List<XmlData.AnimationNode> { s1, s2, s3 };
                        for (int i = 0; i < order.Count; i++)
                        {
                            XmlData.AnimationNode step = order[i];
                            int want = i + 1 < order.Count ? order[i + 1].Id : chainHubId;
                            List<int> seq = SequenceTargetsOf(step);
                            if (seq.Count != 1 || seq[0] != want)
                                failures.Add("set-piece step '" + step.Name + "' should hand to exactly one " +
                                    "successor (" + want + "), got [" + string.Join(",", seq.ConvertAll(delegate(int v) { return v.ToString(); }).ToArray()) + "]");
                            // A border must ANSWER, not escape. Nothing at all was measurably worse: with the
                            // border ignored, a travelling step drove the pet into the screen edge and kept
                            // pushing for the rest of its frames (across the 13 desktop pets that multiplied
                            // wall arrivals 2-5x while climbs fell). Sending it to the next step ends the leg
                            // early and keeps the run in order.
                            if (step.Border == null || step.Border.Next == null || step.Border.Next.Length != 1)
                                failures.Add("set-piece step '" + step.Name + "' has no single border edge, so " +
                                    "a wall either strands it against the edge or offers it a way out of the run");
                            else if (step.Border.Next[0].Value != want)
                                failures.Add("set-piece step '" + step.Name + "' answers a border with " +
                                    step.Border.Next[0].Value + " instead of its own successor " + want +
                                    ", which abandons the run");
                            if (step.Gravity != null)
                                failures.Add("set-piece step '" + step.Name + "' kept a gravity edge; it routes " +
                                    "to 'fall', and 'fall' returns to the hub, so the rest of the run is skipped");
                            // ZERO VERTICAL VELOCITY, and this is the assertion that makes the only="none"
                            // border above safe rather than lucky. The engine raises a border only when the
                            // MOVE would cross it, so a step that never travels vertically can only ever meet
                            // a left/right screen edge, and meeting one advances the run by a step, which is
                            // the behaviour we want. Give a step downward velocity and it raises the taskbar
                            // border on every tick while standing on the floor, so the chain would race
                            // through its remaining legs in a handful of ticks instead of performing them.
                            // Measured across the 13 shipped desktop pets: 0 of 327 chain steps carry vertical
                            // velocity, so the property holds today by accident of the corpus. This is what
                            // stops it being an accident.
                            int vy0 = ParseIntOrZero(step.Start != null ? step.Start.Y : null);
                            int vy1 = ParseIntOrZero(step.End != null ? step.End.Y : null);
                            if (vy0 != 0 || vy1 != 0)
                                failures.Add("set-piece step '" + step.Name + "' travels vertically (start y=" +
                                    vy0 + ", end y=" + vy1 + "), so it raises the floor border every tick and " +
                                    "its only=\"none\" border edge skips the rest of the run in a few ticks");
                        }
                    }
                }

                // ---- THE PLAYED GATE, IN BOTH DIRECTIONS ----
                // StrollAndHop and StrollAndHopUnplayed are the same run of two ordinary floor spokes, so
                // neither RECOVERS anything; only the first carries a behaviour frequency. It must be chained,
                // with its entry weighted by that frequency, and its twin must not be chained at all. Delete
                // the PLAYED term and the first block fails; make the gate admit everything and the second does.
                XmlData.AnimationNode strollEntry = FindAnimationNamed(r, "StrollAndHop_1_Walk");
                XmlData.AnimationNode strollHop = FindAnimationNamed(r, "StrollAndHop_2_Bounce");
                if (strollEntry == null || strollHop == null)
                    failures.Add("the PLAYED sequence 'StrollAndHop' (frequency " + StrollFrequency + ", members already " +
                        "spokes) was not chained: expected StrollAndHop_1_Walk and StrollAndHop_2_Bounce, got [" +
                        string.Join(", ", NamesStartingWith(r, "StrollAndHop_").ToArray()) + "]");
                else
                {
                    List<int> strollHubTargets = HubSequenceTargets(r);
                    if (!strollHubTargets.Contains(strollEntry.Id))
                        failures.Add("the PLAYED set-piece's first step is not selectable from the hub, so the run can never start");
                    if (strollHubTargets.Contains(strollHop.Id))
                        failures.Add("the PLAYED set-piece's second step is selectable from the hub on its own");
                    List<int> strollSeq = SequenceTargetsOf(strollEntry);
                    if (strollSeq.Count != 1 || strollSeq[0] != strollHop.Id)
                        failures.Add("StrollAndHop_1_Walk should hand to exactly StrollAndHop_2_Bounce (" + strollHop.Id +
                            "), got [" + string.Join(",", strollSeq.ConvertAll(delegate(int v) { return v.ToString(); }).ToArray()) + "]");
                    // The entry carries the SEQUENCE's frequency, length-corrected -- not its first member's
                    // weight and not the base weight. The formula rather than a literal, so the constants can
                    // move without moving this line; both numbers are printed when it fails.
                    int expectedEntryWeight = Math.Max(1, PetEmitter.HubWeightFromFrequency(StrollFrequency) / 2);
                    int actualEntryWeight = HubEdgeWeightTo(r, strollEntry.Id);
                    if (actualEntryWeight != expectedEntryWeight)
                        failures.Add("the PLAYED set-piece's hub entry weight is " + actualEntryWeight + ", expected " +
                            expectedEntryWeight + " (HubWeightFromFrequency(" + StrollFrequency + ") over 2 members)");
                    // WITNESS: the expectation differs from what a frequency-less chain would get, so the line
                    // above is reading the frequency and not the base weight.
                    if (expectedEntryWeight == Math.Max(1, PetEmitter.HubBaseWeight / 2))
                        failures.Add("WITNESS: the fixture frequency yields the base entry weight, so the weight " +
                            "assertion cannot tell a frequency from none");
                }
                List<string> unplayedSteps = NamesStartingWith(r, "StrollAndHopUnplayed_");
                if (unplayedSteps.Count != 0)
                    failures.Add("'StrollAndHopUnplayed' has no behaviour frequency and recovers nothing, yet it was " +
                        "chained (" + string.Join(", ", unplayedSteps.ToArray()) + "): the PLAYED gate admits everything");

                // ---- A COLLAPSED MIRROR IS NOT A WITHHELD MEMBER ----
                // WalkBack is Walk over the same frames with the x-velocity mirrored, so CollapseDirectionPairs
                // merges it into Walk: converted, under Walk's name. StrollBack names it and nothing plays
                // StrollBack, so the sequence must NOT be chained -- the RECOVERS gate used to read the
                // post-collapse spoke list, saw no WalkBack, and chained an unplayed sequence whose entry then
                // took a floor share of the hub (F431). Both halves are asserted: that the collapse happened
                // (or the chain assertion is vacuous) and that the residue files WalkBack under merged rather
                // than emitted.
                if (FindAnimationNamed(r, "WalkBack") != null)
                    failures.Add("WITNESS: 'WalkBack' was emitted as its own animation, so it did not collapse into "
                        + "Walk and the collapsed-member chain assertion below tests nothing");
                List<string> strollBackSteps = NamesStartingWith(r, "StrollBack_");
                if (strollBackSteps.Count != 0)
                    failures.Add("'StrollBack' names only a collapsed mirror and an ordinary spoke, and no behaviour "
                        + "plays it, yet it was chained (" + string.Join(", ", strollBackSteps.ToArray())
                        + "): the RECOVERS gate treats a direction-collapsed member as withheld");
                if (r.Residue == null || !r.Residue.Notes.Exists(s => s.IndexOf("Merged into an identical sibling", StringComparison.Ordinal) >= 0
                                                 && s.IndexOf("WalkBack", StringComparison.Ordinal) >= 0))
                    failures.Add("the residue does not list 'WalkBack' under 'Merged into an identical sibling', so "
                        + "the accounting counted a collapsed member as emitted");

                // ---- A COLLAPSED MIRROR'S FREQUENCY REACHES ITS SURVIVOR; A GAZE'S REACHES THE GAZE (RA-375) ----
                // The file's ground truth is share of PLAYS with every leaf credited fully, so a merged
                // walk_left/walk_right carries both behaviours' frequencies and a gaze carries its own. WalkBack's
                // 30 was computed under its own name and read by nothing; the gaze walk ended at Group2 crediting
                // nothing. Formula rather than literal, so the curve can move without moving this line.
                XmlData.AnimationNode weightedWalk = FindAnimationNamed(r, "Walk");
                XmlData.AnimationNode weightedGaze = FindAnimationNamed(r, "SitAndLookAtMouse");
                if (weightedWalk == null || weightedGaze == null)
                    failures.Add("the weight fixtures lost Walk or SitAndLookAtMouse, so the frequency assertions are untested");
                else
                {
                    int walkWeight = HubEdgeWeightTo(r, weightedWalk.Id);
                    int expectedWalk = PetEmitter.HubWeightFromFrequency(WalkBackFrequency);
                    if (walkWeight != expectedWalk)
                        failures.Add("the hub selects 'Walk' at " + walkWeight + ", expected HubWeightFromFrequency("
                            + WalkBackFrequency + ") = " + expectedWalk + ": the collapsed mirror 'WalkBack' carries the "
                            + "frequency and its survivor was weighted as if nobody played it");
                    int gazeWeight = HubEdgeWeightTo(r, weightedGaze.Id);
                    int expectedGaze = PetEmitter.HubWeightFromFrequency(GazeFrequency);
                    if (gazeWeight != expectedGaze)
                        failures.Add("the hub selects the gaze 'SitAndLookAtMouse' at " + gazeWeight + ", expected "
                            + "HubWeightFromFrequency(" + GazeFrequency + ") = " + expectedGaze + ": a Group2 gaze is not a "
                            + "leaf of the frequency walk, so it sits at the base weight however often the artist plays it");
                    // WITNESS: both expectations differ from the unweighted answer, or the two lines above could
                    // not tell a read frequency from none.
                    if (expectedWalk == PetEmitter.HubBaseWeight || expectedGaze == PetEmitter.HubBaseWeight)
                        failures.Add("WITNESS: a fixture frequency yields the base weight, so the weight assertions cannot tell "
                            + "a frequency from none");
                }

                // ---- THE COMPOSITOR AND THE EMITTER ADMIT THE SAME SET-PIECE MEMBERS (RA-374) ----
                // One shared admission (TryResolveSetPieceMembers) now decides both what SetPieceMemberNames sends
                // to the sheet and what ExpandSetPieces chains; this pins the contract from outside: every chain
                // step the emitter built is a member the compositor was told to draw. The two used to be
                // hand-copied clauses, and a member the emitter admitted but the compositor did not would have
                // refused the whole run with the residue blaming the skin.
                HashSet<string> compositedMembers = PetEmitter.SetPieceMemberNames(config);
                var chainedMembers = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "GatorRide_1_Walk", "Walk" }, { "GatorRide_2_RunOff", "RunOff" }, { "GatorRide_3_ReturnOn", "ReturnOn" },
                    { "StrollAndHop_1_Walk", "Walk" }, { "StrollAndHop_2_Bounce", "Bounce" },
                    { "StrollBackPlayed_1_WalkBack", "WalkBack" }, { "StrollBackPlayed_2_Bounce", "Bounce" },
                };
                foreach (KeyValuePair<string, string> step in chainedMembers)
                {
                    if (FindAnimationNamed(r, step.Key) == null) continue;   // its absence is reported by the chain blocks above
                    if (!compositedMembers.Contains(step.Value))
                        failures.Add("chain step '" + step.Key + "' plays member '" + step.Value + "', which SetPieceMemberNames did "
                            + "not name for the sheet: the compositor and the emitter disagree about what a set-piece member is");
                }
                if (!compositedMembers.Contains("RunOff") || !compositedMembers.Contains("ReturnOn"))
                    failures.Add("WITNESS: SetPieceMemberNames does not name the gator ride's off-screen legs, so the contract "
                        + "assertion above has nothing to hold");

                // ---- A PLAYED SEQUENCE NAMING A COLLAPSED MEMBER PLAYS THE SURVIVOR (N-tools-01) ----
                // StrollBackPlayed is StrollBack with a behaviour frequency. WalkBack is collapsed into Walk
                // (asserted just above), so the chain's first step must be built from WALK's poses -- the
                // survivor's, whose -2 x-velocity matches the unmirrored, left-facing art -- and not from
                // WalkBack's own +2, which walked the pet rightwards over left-facing frames: a moonwalk inside
                // the run. The step keeps the member's declared name, which the sequence and the residue name.
                XmlData.AnimationNode strollBackEntry = FindAnimationNamed(r, "StrollBackPlayed_1_WalkBack");
                XmlData.AnimationNode strollBackHop = FindAnimationNamed(r, "StrollBackPlayed_2_Bounce");
                XmlData.AnimationNode plainWalkNode = FindAnimationNamed(r, "Walk");
                XmlData.AnimationNode gatorRunOff = FindAnimationNamed(r, "GatorRide_2_RunOff");
                if (strollBackEntry == null || strollBackHop == null || plainWalkNode == null || gatorRunOff == null)
                    failures.Add("the PLAYED sequence 'StrollBackPlayed', naming the collapsed WalkBack, was not chained: "
                        + "expected StrollBackPlayed_1_WalkBack and StrollBackPlayed_2_Bounce beside Walk and GatorRide_2_RunOff, got ["
                        + string.Join(", ", NamesStartingWith(r, "StrollBackPlayed_").ToArray()) + "]");
                else
                {
                    int walkVx = ParseIntOrZero(plainWalkNode.Start != null ? plainWalkNode.Start.X : null);
                    int stepVx0 = ParseIntOrZero(strollBackEntry.Start != null ? strollBackEntry.Start.X : null);
                    int stepVxN = ParseIntOrZero(strollBackEntry.End != null ? strollBackEntry.End.X : null);
                    if (walkVx >= 0)
                        failures.Add("WITNESS: 'Walk' does not travel leftward (x=" + walkVx + "), so the direction "
                            + "assertion below could not tell the survivor from its mirror");
                    if (stepVx0 != walkVx || stepVxN != walkVx)
                        failures.Add("the chain step built for the collapsed 'WalkBack' travels x=" + stepVx0 + ".." + stepVxN
                            + " where its survivor 'Walk' travels x=" + walkVx + ": the step was built from the collapsed "
                            + "member's own mirrored poses, so it moonwalks over the left-facing art");
                    // WITNESS: an uncollapsed member keeps its own poses. Bounce stands still; RunOff (a GatorRide
                    // member no collapse touched) runs off at its own -6, which is neither Walk's speed nor zero,
                    // so a substitution that reached beyond the collapsed members would show here.
                    int hopVx0 = ParseIntOrZero(strollBackHop.Start != null ? strollBackHop.Start.X : null);
                    int runOffVx0 = ParseIntOrZero(gatorRunOff.Start != null ? gatorRunOff.Start.X : null);
                    if (hopVx0 != 0)
                        failures.Add("WITNESS: the uncollapsed member 'Bounce' lost its own poses (x=" + hopVx0 + " where it declares 0)");
                    if (runOffVx0 != -6)
                        failures.Add("WITNESS: the uncollapsed member 'RunOff' lost its own poses (x=" + runOffVx0 + " where it declares -6)");
                }

                // ---- A JUMP LANDS, WHETHER OR NOT IT IS LOCOMOTION ----
                // The whole border block used to sit behind `loco`, which requires Type="Move". The
                // canonical Shimeji jump is Type="Embedded" Class="...action.Jump", so it got no <border>
                // at all: no taskbar re-jump, no landRun, no window-underside entry. Every hop ended in
                // `fall` and then the hub's idle dwell, on 27 animations across the shipped pets, while the
                // residue report told the user they would "hop again or run off rather than stopping dead".
                // The assertion names the LANDING EDGE rather than "has a border", because a border carrying
                // only a turn would satisfy the weaker claim and is the exact behaviour being replaced.
                XmlData.AnimationNode embeddedHop = FindAnimationNamed(r, "HopEmbedded");
                if (embeddedHop == null)
                    failures.Add("no 'HopEmbedded' animation emitted, so the class-based jump is untested " +
                        "and every border assertion here only ever saw a Type=Move jump");
                else
                {
                    if (embeddedHop.Border == null || embeddedHop.Border.Next == null)
                        failures.Add("the embedded-class jump 'HopEmbedded' got no border set, so it cannot " +
                            "land: it falls to 'fall' and then stands there");
                    else
                    {
                        bool landsOnTaskbar = false, turnsAtAnyEdge = false;
                        foreach (XmlData.NextNode n in embeddedHop.Border.Next)
                        {
                            if (n == null) continue;
                            if (string.Equals(n.OnlyFlag, "taskbar", StringComparison.Ordinal)) landsOnTaskbar = true;
                            XmlData.AnimationNode t = FindAnimationById(r, n.Value);
                            if (t != null && string.Equals(t.Name, "turn", StringComparison.Ordinal) &&
                                (string.IsNullOrEmpty(n.OnlyFlag) || string.Equals(n.OnlyFlag, "none", StringComparison.Ordinal)))
                                turnsAtAnyEdge = true;
                        }
                        if (!landsOnTaskbar)
                            failures.Add("the embedded-class jump has a border but no only=\"taskbar\" edge, " +
                                "so landing on the floor still decides nothing and the hop ends in the hub");
                        // And it must NOT pick up travel's only="none" turn, which is eligible at the taskbar
                        // too: a turn there is the facing-flip-into-idle outcome the taskbar edges replace.
                        if (turnsAtAnyEdge)
                            failures.Add("the embedded-class jump took locomotion's only=\"none\" turn, which " +
                                "is eligible on landing and flips the pet into the hub instead of re-jumping");

                        // AND IT MUST HAVE AN EDGE FOR EVERY BORDER IT CAN MEET, not just the floor.
                        //
                        // The two assertions above are both right and between them say nothing about the
                        // other three borders the host raises. That gap shipped: measured across the 54
                        // companions with the host's own Eligible, 87 (state, situation) pairs in 14
                        // CONVERTED pets had zero eligible weight, split exactly 29 / 29 / 29 over
                        // VERTICAL, HORIZONTAL and WINDOW|WINDOW_TOP -- because a non-locomotion jump's
                        // only border edges were `taskbar` and `window-bottom`. The host then returns -1,
                        // FormCompanion sets bLeavingScreen, and the pet walks off the screen and respawns
                        // with its monitor re-rolled under multiscreen.
                        //
                        // Checked as STRINGS, deliberately. The real property is "positive eligible weight
                        // in every situation", which is the host's Eligible bitmask; re-implementing that
                        // here would test a copy of the rule that can drift from it in silence. What this
                        // file can honestly assert is that the emitter NAMED each situation.
                        foreach (string situation in new string[] { "vertical", "horizontal", "window-top" })
                        {
                            bool covered = false;
                            foreach (XmlData.NextNode n in embeddedHop.Border.Next)
                            {
                                if (n == null) continue;
                                // `none` would cover it, and is excluded on purpose: the assertion above
                                // forbids it here, so a fix that reintroduced it would satisfy this check
                                // while breaking that one. Only a NAMED match counts.
                                if (string.Equals(n.OnlyFlag, situation, StringComparison.Ordinal)) { covered = true; break; }
                            }
                            if (!covered)
                                failures.Add("the embedded-class jump has no only=\"" + situation + "\" border " +
                                    "edge, so meeting that border leaves nothing eligible: the host returns -1 " +
                                    "and the pet walks off the screen and respawns instead of landing");
                        }
                    }
                }

                if (!HasAnimationNamed(r, "fall")) failures.Add("no 'fall' magic animation emitted");
                if (!HasAnimationNamed(r, "drag")) failures.Add("no 'drag' magic animation emitted");
                if (!HasAnimationNamed(r, "kill")) failures.Add("no 'kill' magic animation emitted");
                if (!HasAnimationNamed(r, "sync")) failures.Add("no 'sync' magic animation emitted");

                // ---- A PERFORMANCE THAT TRAVELS IS NOT LOCOMOTION ----
                // Both halves, deliberately. Stumble must lose the self-edge AND Walk must keep it: "nothing
                // loops" would satisfy the first assertion alone while destroying every walk in the pack, and
                // that is the mutation this pair exists to catch. The border goes with it -- turning at a
                // screen edge and grabbing a wall are travel's, and a trip that reaches the edge should
                // finish and hand back rather than start climbing.
                XmlData.AnimationNode stumble = FindAnimationNamed(r, "Stumble");
                XmlData.AnimationNode walk = FindAnimationNamed(r, "Walk");
                if (stumble == null) failures.Add("no 'Stumble' animation emitted, so the Animate case is untested");
                else if (stumble.Sequence == null || stumble.Sequence.Next == null)
                    failures.Add("'Stumble' emitted with no sequence edges at all");
                else
                {
                    foreach (XmlData.NextNode n in stumble.Sequence.Next)
                        if (n != null && n.Value == stumble.Id)
                            failures.Add("Type=Animate 'Stumble' kept locomotion's self-edge at " +
                                n.Probability + "%, so it replays itself instead of playing once");
                    if (stumble.Border != null)
                        failures.Add("Type=Animate 'Stumble' kept locomotion's border set, so a trip that " +
                            "reaches a screen edge turns or grabs a wall");
                    if (!string.Equals(stumble.Sequence.RepeatCount, "0", StringComparison.Ordinal))
                        failures.Add("Type=Animate 'Stumble' repeats '" + (stumble.Sequence.RepeatCount ?? "(null)") +
                            "' times; a performance plays exactly once");
                }
                // The rule the `reloop` migration encodes, asserted on the WHOLE graph rather than on one
                // named fixture, so a future action cannot quietly acquire a dwell it should not have.
                // Measured against a fresh conversion of the Hornet bundle: all 10 of its Animate
                // animations come out at repeat 0, while Move keeps 4-20 and Stay keeps 1-11.
                int animateSeen = 0;
                foreach (XmlData.AnimationNode a in r.Root != null && r.Root.Animations != null &&
                                                    r.Root.Animations.Animation != null
                             ? r.Root.Animations.Animation : new XmlData.AnimationNode[0])
                {
                    if (a == null || a.Sequence == null) continue;
                    if (!string.Equals(SourceTypeOf(config, a.Name), "Animate", StringComparison.OrdinalIgnoreCase)) continue;
                    animateSeen++;
                    if (!string.Equals(a.Sequence.RepeatCount, "0", StringComparison.Ordinal))
                        failures.Add("Type=Animate '" + a.Name + "' repeats '" +
                            (a.Sequence.RepeatCount ?? "(null)") + "' times; a performance plays exactly once, " +
                            "and an inflated repeat is what made two frames juggle for eleven seconds");
                }
                // Guard the guard: the assertion above is vacuous if the fixture stops producing Animate
                // animations, and it silently was a ONE-case test until Bounce was added beside Stumble.
                if (animateSeen < 2)
                    failures.Add("only " + animateSeen + " Animate animation(s) emitted, so 'every performance " +
                        "plays once' is not being tested across both the travelling and stationary kinds");

                XmlData.AnimationNode brace = FindAnimationNamed(r, "Brace");
                if (brace == null) failures.Add("no 'Brace' animation emitted, so the zero-velocity Move case is untested");
                else if (brace.Sequence != null && brace.Sequence.Next != null)
                    foreach (XmlData.NextNode n in brace.Sequence.Next)
                        if (n != null && n.Value == brace.Id)
                            failures.Add("Type=Move 'Brace' never moves, yet it got locomotion's self-edge at " +
                                n.Probability + "%; travel is declared intent AND real motion");

                if (walk == null) failures.Add("no 'Walk' animation emitted, so the Move case is untested");
                else if (walk.Sequence == null || walk.Sequence.Next == null)
                    failures.Add("'Walk' emitted with no sequence edges at all");
                else
                {
                    bool walkLoops = false;
                    foreach (XmlData.NextNode n in walk.Sequence.Next)
                        if (n != null && n.Value == walk.Id) walkLoops = true;
                    if (!walkLoops)
                        failures.Add("Type=Move 'Walk' lost its self-edge, so the pet re-decides every two " +
                            "frames and never crosses the screen");
                    if (walk.Border == null)
                        failures.Add("Type=Move 'Walk' lost its border set, so it cannot turn at a screen edge");
                }

                // ---- REST DWELL, SPLIT BY ROLE ----
                // The dwell was wrong twice: ~9s everywhere (the pet stood around, "doesn't do anything") then
                // ~1.2s everywhere (which cut the performances the user wants to watch). The resolution is that
                // a rest is two things: the HUB, the pose the pet returns to between actions, must be BRIEF so
                // it does not loiter; a PERFORMANCE (sprawl, eat, dangle-legs) must LINGER 9-12s. The fixture
                // exercises both: Stand is the hub (single-frame path), Lounge is a non-hub Stay (multi-frame,
                // with a 3000ms hold baked in that must be capped).
                const int hubCeilingMs = 3200;      // brief: HubDwellMs(2000) + single-frame split slack
                const int perfFloorMs = 8000;       // a performance must clearly linger
                const int perfCeilingMs = 13500;    // ...but not overshoot the 9-12s band by much
                int restHubId = HubId(r);
                XmlData.AnimationNode hubNode = FindAnimationById(r, restHubId);
                XmlData.AnimationNode lounge = FindAnimationNamed(r, "Lounge");
                if (hubNode == null || lounge == null)
                {
                    failures.Add("the fixture lost a rest pose, so the dwell split is untested (hub="
                        + (hubNode != null) + ", Lounge=" + (lounge != null) + ")");
                }
                else
                {
                    int hubDwell = TotalDwellMs(hubNode);
                    if (hubDwell > hubCeilingMs)
                        failures.Add("the hub '" + hubNode.Name + "' holds " + hubDwell + "ms; the return-to "
                            + "pose must be brief or the pet loiters (was 9.6s, the 'doesn't do anything' report)");

                    int loungeDwell = TotalDwellMs(lounge);
                    if (loungeDwell < perfFloorMs)
                        failures.Add("the performance rest holds only " + loungeDwell + "ms; a performance must "
                            + "linger 9-12s, not flash by (the over-correction that cut sprawl to 1.4s)");
                    if (loungeDwell > perfCeilingMs)
                        failures.Add("the performance rest holds " + loungeDwell + "ms, well over the 9-12s band");
                    // Its per-frame interval must be capped, or the 3000ms baked-in hold freezes the frame
                    // instead of the pose lingering via repeats.
                    int li0 = ParseIntOrZero(lounge.Start != null ? lounge.Start.Interval : null);
                    int liN = ParseIntOrZero(lounge.End != null ? lounge.End.Interval : null);
                    if (li0 > PetEmitter.RestIntervalCapMs || liN > PetEmitter.RestIntervalCapMs)
                        failures.Add("a performance keeps a per-frame interval over the cap (" + li0 + "/" + liN
                            + " vs " + PetEmitter.RestIntervalCapMs + "); a baked-in hold froze instead of lingering");
                }
                // Per-pet, not just the named fixtures: the hub is brief and every OTHER hub-selectable idle
                // rest lingers. This is the split, asserted on the whole graph so a future pose cannot quietly
                // put the pet back to standing around or flashing a performance by.
                foreach (int id in HubSequenceTargets(r))
                {
                    XmlData.AnimationNode a = FindAnimationById(r, id);
                    if (a == null || a.Gravity == null) continue;                              // floor only
                    if (ParseIntOrZero(a.Start != null ? a.Start.X : null) != 0) continue;      // idle only
                    if (ParseIntOrZero(a.Start != null ? a.Start.Y : null) != 0) continue;
                    if (ParseIntOrZero(a.End != null ? a.End.X : null) != 0) continue;
                    if (ParseIntOrZero(a.End != null ? a.End.Y : null) != 0) continue;
                    // A REST is what the source called Stay, not merely anything that ends up standing still.
                    // Zero velocity was a proxy for intent here, which is the same mistake the emitter made
                    // when it read a trip as a walk: `Brace` is declared Move and simply never moves, so it
                    // is a one-shot and lingering 9-12s on it would be wrong. Only skip when the source
                    // positively says otherwise -- a name absent from the config (the magic fall/drag/kill
                    // animations) keeps the old, broader assertion rather than quietly losing coverage.
                    string sourceType = SourceTypeOf(config, a.Name);
                    if (sourceType != null && !string.Equals(sourceType, "Stay", StringComparison.OrdinalIgnoreCase))
                        continue;
                    int dwell = TotalDwellMs(a);
                    if (a.Id == restHubId)
                    {
                        if (dwell > hubCeilingMs)
                            failures.Add("the hub idle '" + a.Name + "' holds " + dwell + "ms, over the brief-hub ceiling");
                    }
                    else if (dwell < perfFloorMs)
                    {
                        failures.Add("idle performance '" + a.Name + "' holds only " + dwell + "ms; a non-hub "
                            + "rest must linger 9-12s");
                    }
                }

                // ---- the wall region ----
                // Four properties, each of which was a real bug or is the mechanism the feature rests on.
                XmlData.AnimationNode wall = FindAnimationNamed(r, "ClimbWall");
                if (wall == null)
                {
                    // Was a live failure: a Group1-only wall filter dropped the reference conf's ClimbWall
                    // (Group2 because its CONDITION reads mascot.anchor), leaving a pet that clings motionless.
                    failures.Add("no wall animation emitted (a Group2 wall action must still convert)");
                }
                else
                {
                    // The cling. Presence of <gravity> is what makes the engine drop an unsupported pet, so a
                    // wall animation must NOT have one. This is how the hand-authored sheep stay on walls.
                    if (wall.Gravity != null)
                        failures.Add("wall animation has a <gravity> node, so the pet would fall off the wall instead of clinging");

                    // The climb: negative Y is upward.
                    int wallEndY = ParseIntOrZero(wall.End != null ? wall.End.Y : null);
                    if (wallEndY >= 0)
                        failures.Add("wall animation does not move upward (end y=" + wallEndY + ")");

                    // It must be unreachable from the floor hub's own choice list, or a wall-cling would play
                    // in the middle of the screen -- the reason wall actions were excluded outright before.
                    if (HubSequenceTargets(r).Contains(wall.Id))
                        failures.Add("the floor hub can select the wall animation directly; it must only be entered from a vertical border");

                    // And it must be reachable, via a vertical-border edge on a locomotion animation.
                    if (!HasBorderEdgeTo(r, wall.Id, "vertical"))
                        failures.Add("no only=\"vertical\" border edge enters the wall region");
                }

                // ---- the ceiling region ----
                // The ceiling exists to be entered by CLIMBING and no other way, so most of what is asserted
                // here is about what must NOT reach it.
                XmlData.AnimationNode ceiling = FindAnimationNamed(r, "ClimbCeiling");
                if (ceiling == null)
                {
                    failures.Add("no ceiling animation emitted");
                }
                else
                {
                    // Same cling mechanism as the wall: <gravity> is what makes the engine drop an
                    // unsupported pet, so a hanging animation must not carry one.
                    if (ceiling.Gravity != null)
                        failures.Add("ceiling animation has a <gravity> node, so the pet would drop instead of hanging");

                    // It travels ALONG the ceiling, not through it. A non-zero Y here would either fight the
                    // engine's PositionY pin at the top border or walk the pet off the ceiling.
                    int ceilEndY = ParseIntOrZero(ceiling.End != null ? ceiling.End.Y : null);
                    if (ceilEndY != 0)
                        failures.Add("ceiling animation has vertical velocity (end y=" + ceilEndY + "); it must move horizontally only");
                    if (ParseIntOrZero(ceiling.End != null ? ceiling.End.X : null) == 0)
                        failures.Add("ceiling animation does not move horizontally, so the pet would hang motionless");

                    // Never selectable mid-screen.
                    if (HubSequenceTargets(r).Contains(ceiling.Id))
                        failures.Add("the floor hub can select the ceiling animation directly; it must only be entered from the top border");

                    // Reachable, and reachable ONLY from the wall. A design assertion, not a safety net: the
                    // engine offers only="horizontal" edges at the TOP border alone (FormCompanion.cs:1327; the
                    // floor raises bare taskbar, :1258), so a floor animation's horizontal edge could never fire
                    // at ground level anyway. What this pins is that the ceiling is entered by climbing and
                    // nothing else (PetEmitter.cs attaches the edge to the climbing wall spoke only). Until
                    // 2026-10-01 this comment claimed a floor animation could snap to the ceiling through such
                    // an edge, which the engine cannot do (N-scripts-02).
                    if (!HasBorderEdgeTo(r, ceiling.Id, "horizontal"))
                        failures.Add("no only=\"horizontal\" border edge enters the ceiling region");
                    foreach (XmlData.AnimationNode src in BorderSourcesOf(r, ceiling.Id, "horizontal"))
                        if (FindAnimationNamed(r, "ClimbWall") == null || src.Id != FindAnimationNamed(r, "ClimbWall").Id)
                            failures.Add("ceiling is entered from '" + src.Name + "', which is not the wall climb; it must be reachable only by climbing");

                    // And it must lead back out, or a pet that reaches the ceiling stays there for good.
                    if (ceiling.Border == null || ceiling.Border.Next == null || ceiling.Border.Next.Length == 0)
                        failures.Add("ceiling animation has no border edge, so the pet could never leave the ceiling");
                }

                // ---- the WINDOW SIDE region ----
                // A pet standing on a window and walking off its edge can grip the side instead of turning
                // round. No new art: it is the wall region entered from a different border.
                XmlData.AnimationNode descend = FindAnimationNamed(r, "DescendWall");
                XmlData.AnimationNode climb = FindAnimationNamed(r, "ClimbWall");
                int hubId = HubId(r);
                // The ceiling region: everything with no <gravity> that a ceiling pose chains to, seeded from
                // the pair the fixture names. Collected here because both the window-side and the underside
                // assertions need to know which animations count as "hanging".
                var ceilingIds = new List<int>();
                foreach (string ceilName in new[] { "ClimbCeiling", "HangCeiling" })
                {
                    XmlData.AnimationNode c = FindAnimationNamed(r, ceilName);
                    if (c != null) ceilingIds.Add(c.Id);
                }
                if (descend == null || climb == null)
                {
                    failures.Add("the fixture's wall poses did not both emit, so the window-side assertions prove nothing");
                }
                else
                {
                    // Entered from BOTH sides, or the pet grips one edge of a window and turns at the other.
                    if (!HasBorderEdgeTo(r, descend.Id, "window-left"))
                        failures.Add("no only=\"window-left\" border edge enters the wall region, so the pet cannot grip a window's left side");
                    if (!HasBorderEdgeTo(r, descend.Id, "window-right"))
                        failures.Add("no only=\"window-right\" border edge enters the wall region, so the pet cannot grip a window's right side");

                    // Entered on the DESCENDING pose. Entering on the climb sends the pet straight back up
                    // into the window top it just left, which is a loop that shows nothing.
                    foreach (string side in new[] { "window-left", "window-right" })
                        foreach (XmlData.AnimationNode src in BorderSourcesOf(r, climb.Id, side))
                            failures.Add("only=\"" + side + "\" enters the CLIMB from '" + src.Name
                                + "'; entering on a climb returns the pet to the window top it just left");

                    // Offered only from somewhere the pet can actually BE on a window: standing on its top
                    // (a floor animation, hub-selectable) or hanging from its underside (a ceiling pose,
                    // which reaches the window's corners). A WALL pose offering it would be a pet already
                    // gripping one side reaching for another, which is not a situation that exists.
                    //
                    // Hub reachability, NOT the absence of <gravity>. That was the first version and it
                    // rejected the fixture's own jump: a jump is a floor animation that deliberately carries
                    // no gravity node, because gravity would cut its arc off at frame one.
                    List<int> hubTargets = HubSequenceTargets(r);
                    var onAWindow = new List<int>(hubTargets);
                    if (ceilingIds != null) onAWindow.AddRange(ceilingIds);
                    foreach (string side in new[] { "window-left", "window-right" })
                        foreach (XmlData.AnimationNode src in BorderSourcesOf(r, descend.Id, side))
                            if (!onAWindow.Contains(src.Id))
                                failures.Add("only=\"" + side + "\" is offered by '" + src.Name
                                    + "', which is neither on a window's top nor under it, so the pet was never on a window");

                    // And back off the top: a pet that climbs a window's side must be able to stand on it.
                    if (!HasBorderEdgeTo(r, hubId, "window-top"))
                        failures.Add("no only=\"window-top\" edge returns a climbing pet to the floor hub, so it can only ever let go");
                    foreach (XmlData.AnimationNode src in BorderSourcesOf(r, hubId, "window-top"))
                        if (src.Id != climb.Id)
                            failures.Add("only=\"window-top\" is offered by '" + src.Name
                                + "'; only a CLIMBING pose can reach a window's top edge");

                    // ---- the window UNDERSIDE ----
                    // Reached by jumping into it, and by nothing else. This is the same discipline the
                    // ceiling region uses at the screen top: only an animation that travels upward can meet
                    // the border, and the graph should say so rather than leaving it to the physics.
                    if (ceiling != null)
                    {
                        if (!HasBorderEdgeTo(r, ceiling.Id, "window-bottom"))
                            failures.Add("no only=\"window-bottom\" border edge enters the ceiling region, so the pet can never hang under a window");
                        foreach (XmlData.AnimationNode src in BorderSourcesOf(r, ceiling.Id, "window-bottom"))
                        {
                            if (!hubTargets.Contains(src.Id))
                                failures.Add("only=\"window-bottom\" is offered by '" + src.Name
                                    + "', which the floor hub cannot select, so the pet was never on the ground to jump");
                            // ...and it must actually LAUNCH. A walk offering this edge could never meet it,
                            // so the edge would be decoration that reads as a capability.
                            if (ParseIntOrZero(src.Start != null ? src.Start.Y : null) >= 0)
                                failures.Add("only=\"window-bottom\" is offered by '" + src.Name
                                    + "', which does not travel upward, so it can never reach a window's underside");
                        }

                        // And back out at the corners, or a pet that walks the length of an overhang can only
                        // ever drop off the end.
                        foreach (string side in new[] { "window-left", "window-right" })
                            if (BorderSourcesOf(r, descend.Id, side).FindIndex(delegate(XmlData.AnimationNode n) { return n.Id == ceiling.Id; }) < 0)
                                failures.Add("a pet hanging under a window has no only=\"" + side
                                    + "\" edge onto the frame's side, so the corner is a dead end");
                    }

                    // The pre-existing screen-top split must not have moved. The fall weight used to be a
                    // flat 100 whenever there was no ceiling edge, and the window-top edge now shares that
                    // slot -- get the condition wrong and a pet at the screen top stops falling.
                    if (climb.Border != null && climb.Border.Next != null)
                    {
                        int ceilingWeight = 0, fallWeight = 0;
                        foreach (XmlData.NextNode n in climb.Border.Next)
                        {
                            if (n == null) continue;
                            if (n.OnlyFlag == "horizontal") ceilingWeight = n.Probability;
                            if (string.IsNullOrEmpty(n.OnlyFlag) || n.OnlyFlag == "none") fallWeight = n.Probability;
                        }
                        if (ceilingWeight != 2 || fallWeight != 1)
                            failures.Add("the screen-top ceiling/fall split moved (ceiling=" + ceilingWeight
                                + ", fall=" + fallWeight + ", expected 2 and 1)");
                    }

                    // ---- CROSSING the surface, which is what makes the ceiling reachable at all ----
                    // A climb that stops short rolls a 34% chance of letting go at every sequence end, so
                    // reaching a 940px screen top in 32px passes needed 30 consecutive survivals: 1 in 203,000
                    // wall entries, one visit per five years. The reach, not the speed, is the property.
                    int climbFrames = climb.Sequence != null && climb.Sequence.Frame != null ? climb.Sequence.Frame.Length : 0;
                    int climbRepeat = ParseIntOrZero(climb.Sequence != null ? climb.Sequence.RepeatCount : null);
                    // Replayed with the repeatfrom the NODE carries, against the distance the solver was asked for:
                    // a solver fed a different repeatfrom than the emitter wrote lands short of its own target.
                    int reach = PetEmitter.SurfaceReachOf(climbFrames, climbRepeat, climb.Sequence != null ? climb.Sequence.RepeatFromFrame : 0);
                    if (reach < PetEmitter.SurfaceReachTargetPx)
                        failures.Add("one climb pass covers only " + reach + "px of the " + PetEmitter.SurfaceReachTargetPx
                            + " the reach solver targets, so the pet rolls the let-go dice before it can reach the top of a screen");
                    // The stock climb opens with a still pose inside a block the source LOOPS (Type="Move"), which is
                    // the two-beat pause-step rhythm every Shimeji-EE derivative ships, not a mount: the cycle stays
                    // whole (the mount-prefix rule is pinned on its own fixture further down).
                    if (climb.Sequence != null && climb.Sequence.RepeatFromFrame != 0)
                        failures.Add("the looping ClimbWall repeats from frame " + climb.Sequence.RepeatFromFrame + "; a still opening "
                            + "pose inside a declared loop is the stock pause-step rhythm and must stay in the cycle");

                    // Constant, not a ramp. The sequence self-loops, so a ramp snaps back to the slow start
                    // speed on every loop and pulses; Hornet's source ramp 0 -> -2 also halved its speed.
                    int climbStart = ParseIntOrZero(climb.Start != null ? climb.Start.Y : null);
                    int climbEnd = ParseIntOrZero(climb.End != null ? climb.End.Y : null);
                    if (climbStart != climbEnd)
                        failures.Add("the climb's vertical speed ramps (" + climbStart + " -> " + climbEnd
                            + "); a self-looping sequence must hold a constant speed");
                    if (climbStart >= 0)
                        failures.Add("the climb does not travel upward (y=" + climbStart + ")");
                    int climbIv0 = ParseIntOrZero(climb.Start != null ? climb.Start.Interval : null);
                    int climbIvN = ParseIntOrZero(climb.End != null ? climb.End.Interval : null);
                    if (climbIv0 != climbIvN)
                        failures.Add("the climb's interval ramps (" + climbIv0 + " -> " + climbIvN + ")");

                    // A DESCENDING wall pose crosses too, and must keep its DIRECTION. Turning every descent
                    // into a climb would be silent: both are wall poses reached from the same edges, and the
                    // pet would simply never come back down a wall again.
                    if (descend != null)
                    {
                        int dy = ParseIntOrZero(descend.Start != null ? descend.Start.Y : null);
                        int dFrames = descend.Sequence != null && descend.Sequence.Frame != null ? descend.Sequence.Frame.Length : 0;
                        int dRepeat = ParseIntOrZero(descend.Sequence != null ? descend.Sequence.RepeatCount : null);
                        int dRepeatFrom = descend.Sequence != null ? descend.Sequence.RepeatFromFrame : 0;
                        if (dy <= 0)
                            failures.Add("the descending wall pose does not travel DOWN (y=" + dy
                                + "); the reach budget must preserve direction, not turn every descent into a climb");
                        if (PetEmitter.SurfaceReachOf(dFrames, dRepeat, dRepeatFrom) < PetEmitter.SurfaceReachTargetPx)
                            failures.Add("the descending wall pose covers only "
                                + PetEmitter.SurfaceReachOf(dFrames, dRepeat, dRepeatFrom) + "px, so climbing DOWN rolls the "
                                + "same let-go dice the climb up used to");
                    }

                    // A STATIC grab must NOT be given the reach budget: a hold is meant to end and re-decide,
                    // and a 4000px hold would pin the pet to the wall for a minute doing nothing.
                    XmlData.AnimationNode grab = FindAnimationNamed(r, "GrabWall");
                    if (grab == null)
                    {
                        failures.Add("the fixture has no static wall grab, so the hold/travel split is untested");
                    }
                    else
                    {
                        int grabFrames = grab.Sequence != null && grab.Sequence.Frame != null ? grab.Sequence.Frame.Length : 0;
                        int grabRepeat = ParseIntOrZero(grab.Sequence != null ? grab.Sequence.RepeatCount : null);
                        if (PetEmitter.SurfaceReachOf(grabFrames, grabRepeat, grab.Sequence != null ? grab.Sequence.RepeatFromFrame : 0) >= 2000)
                            failures.Add("a static wall grab was given the travel reach budget; a hold must "
                                + "end and let the pet re-decide");
                    }
                }

                // The same property on the CEILING, which needs it for a different reason: a ceiling walk that
                // stops every 32px never reaches a corner, so it never finds the only="vertical" edge that
                // would take it back down a wall, and its only exit is to drop.
                XmlData.AnimationNode ceilingWalk = FindAnimationNamed(r, "ClimbCeiling");
                if (ceilingWalk != null)
                {
                    int frames = ceilingWalk.Sequence != null && ceilingWalk.Sequence.Frame != null ? ceilingWalk.Sequence.Frame.Length : 0;
                    int rep = ParseIntOrZero(ceilingWalk.Sequence != null ? ceilingWalk.Sequence.RepeatCount : null);
                    int cRepeatFrom = ceilingWalk.Sequence != null ? ceilingWalk.Sequence.RepeatFromFrame : 0;
                    if (PetEmitter.SurfaceReachOf(frames, rep, cRepeatFrom) < PetEmitter.SurfaceReachTargetPx)
                        failures.Add("one ceiling pass covers only " + PetEmitter.SurfaceReachOf(frames, rep, cRepeatFrom)
                            + "px, so the pet drops off before it can reach a corner");
                    int cx0 = ParseIntOrZero(ceilingWalk.Start != null ? ceilingWalk.Start.X : null);
                    int cxN = ParseIntOrZero(ceilingWalk.End != null ? ceilingWalk.End.X : null);
                    if (cx0 != cxN)
                        failures.Add("the ceiling walk's speed ramps (" + cx0 + " -> " + cxN + ")");
                    if (cx0 == 0)
                        failures.Add("the ceiling walk does not travel horizontally");
                }

                // The geometry the old exclusion existed to protect: admitting a ceiling pose whose anchor is
                // ABOVE the floor anchor must not pad the cell, because a padded cell lifts every floor pet
                // off the ground. The floor poses anchor at 60, so an unscaled cell taller than that means
                // the ceiling anchor leaked into the cell height.
                if (sheet.CellHeight > 60)
                    failures.Add("cell height grew to " + sheet.CellHeight + " (>60): a ceiling anchor padded the cell, which floats every floor animation");

                // ...and the mechanism itself. Cell height alone cannot catch a ceiling pose composited under
                // the FLOOR convention: the cell stays 60 either way, but the sprite lands at the cell BOTTOM,
                // so the pet hangs a full cell below the ceiling it is meant to be gripping.
                //
                // The fixture makes the two conventions exact opposites, which is what gives this teeth. The
                // ceiling sprite is 60 tall anchored at 24, so top-anchored it occupies rows 0..35 and leaves
                // the bottom empty, while bottom-anchored it occupies rows 36..59 and leaves the TOP empty.
                // Asserting both ends distinguishes them; asserting only the top would also pass on a sprite
                // that happened to fill the cell.
                // THE guard, and the one that actually matters: no animation may reference a blank tile.
                // Anchor arithmetic that skips too much of the source produces a fully transparent tile, the
                // pet vanishes mid-animation, and nothing else notices -- the XML validates, the graph is
                // reachable, the round-trip passes. That shipped in 1.9.4 for every Android-bundle pet
                // because bundles anchor bottom-centre and the ceiling path skipped AnchorY rows.
                var blank = new List<string>();
                if (r.Root != null && r.Root.Animations != null && r.Root.Animations.Animation != null)
                {
                    foreach (XmlData.AnimationNode a in r.Root.Animations.Animation)
                    {
                        if (a == null || a.Sequence == null || a.Sequence.Frame == null) continue;
                        foreach (int tile in a.Sequence.Frame)
                            if (!TileIsPainted(sheet, tile))
                                blank.Add(a.Name + " -> tile " + tile);
                    }
                }
                if (blank.Count > 0)
                    failures.Add("animations reference blank (fully transparent) tiles, so the pet vanishes: "
                        + string.Join(", ", blank.ToArray()));

                string ceilKey = FirstPoseKey(config, "ClimbCeiling");
                if (ceilKey != null)
                {
                    if (!TileRowIsPainted(sheet, ceilKey, 0))
                        failures.Add("the ceiling frame is not drawn at the top of its tile, so the pet would hang a whole cell below the ceiling");
                    if (TileRowIsPainted(sheet, ceilKey, sheet.CellHeight - 1))
                        failures.Add("the ceiling frame reaches the bottom of its tile, so it was composited under the floor anchor convention");
                }

                // ---- jumps ----
                // Upward velocity on the floor was rejected outright for the whole project's life, which
                // silently refused 81 jump actions across 27 pets. It is admitted now, but ONLY as a bounded
                // arc, and "bounded" is the entire safety argument: whatever the source asked for, the pet
                // must come back down.
                XmlData.AnimationNode jump = FindAnimationNamed(r, "BigJump");
                XmlData.AnimationNode fallAnim = FindAnimationNamed(r, "fall");
                if (jump == null)
                {
                    failures.Add("no jump animation emitted (an upward-velocity floor action must convert)");
                }
                else
                {
                    int launch = ParseIntOrZero(jump.Start != null ? jump.Start.Y : null);
                    int descent = ParseIntOrZero(jump.End != null ? jump.End.Y : null);

                    // THE DIRECTION ONLY. There is no launch clamp to assert: BuildSpoke sets
                    // `vy0 = SolveJumpLaunchY(jumpSteps)` and discards the source pose velocity outright,
                    // and that solver searches JumpLaunchMinMag(4)..JumpLaunchMaxMag(40), so -40 is a
                    // legitimate answer rather than an escape.
                    //
                    // A `launch < -15` check used to sit here, reported as "jump launch was NOT clamped
                    // ... source asked for -40". It tracked no constant any caller uses and passed only
                    // because this fixture's 2-pose BigJump gives 14 steps, for which the solver returns
                    // exactly -15 -- true by ONE UNIT, with zero margin. Any change to JumpPeakPx,
                    // JumpDescentY, JumpArcSteps or the fixture's frame count would have reddened it with
                    // a diagnosis naming a clamp that is not in the code.
                    //
                    // The real property -- that the arc rises about as far as intended -- is asserted 45
                    // lines below against PetEmitter.ArcRisePx, which is the same idea measured against
                    // the thing that actually decides it.
                    if (launch >= 0)
                        failures.Add("jump does not launch upward (start y=" + launch + ")");

                    // The fixture never descends on its own; the arc has to be closed for it.
                    if (descent <= 0)
                        failures.Add("jump never descends (end y=" + descent + "), so the pet does not come back down");

                    // Gravity would end the jump at frame one, the instant the pet left the ground. Not one of
                    // yellow_sheep's 22 upward animations carries it.
                    if (jump.Gravity != null)
                        failures.Add("jump has a <gravity> node, so it is cut off the moment it leaves the ground");

                    // A jump must not be mistaken for a wall animation: it belongs to the floor hub.
                    if (!HubSequenceTargets(r).Contains(jump.Id))
                        failures.Add("the floor hub cannot select the jump, so it never plays");
                }

                // The three-phase assertions hold for EVERY jump the pet emitted, not just the one the fixture
                // names. BigJump alone proved nothing about the arc: its 2 poses at 4 ticks make the old
                // locomotion budget pick the same 14 steps the solved arc wants, so a pass-through launch came
                // out at the right height by luck. PullUp and HopUp are the shapes that actually failed.
                var jumps = new List<XmlData.AnimationNode>();
                List<int> hubSelectable = HubSequenceTargets(r);
                if (r.Root != null && r.Root.Animations != null && r.Root.Animations.Animation != null)
                    foreach (XmlData.AnimationNode a in r.Root.Animations.Animation)
                        if (a != null && hubSelectable.Contains(a.Id)
                            && ParseIntOrZero(a.Start != null ? a.Start.Y : null) < 0)
                            jumps.Add(a);

                if (jumps.Count < 4)
                    failures.Add("expected the fixture's four jump shapes to emit as jumps, got "
                        + jumps.Count + "; the height assertions below cannot distinguish anything with fewer");

                foreach (XmlData.AnimationNode j in jumps)
                {
                    int launch = ParseIntOrZero(j.Start != null ? j.Start.Y : null);
                    int descent = ParseIntOrZero(j.End != null ? j.End.Y : null);

                    // ---- PHASE 1: the arc reaches a KNOWN HEIGHT ----
                    // The assertion that "bounded" alone never made. Clamping the launch and forcing the
                    // descent still left the height to the STEP COUNT, which is the source's business: across
                    // the 32 shipped jumps that gave 8-16px (a twitch) or 72px (a fling) and nothing between.
                    // Computed from the emitted numbers the engine will actually interpolate, so a launch
                    // solved against a different step count than the sequence declares fails here.
                    int declaredSteps = DeclaredSteps(j);
                    double rise = PetEmitter.ArcRisePx(launch, descent, declaredSteps);
                    if (rise < 36.0 || rise > 60.0)
                        failures.Add("'" + j.Name + "' rises " + rise.ToString("0") + "px over its "
                            + declaredSteps + " declared steps; every jump must reach about the same height (~48px)");
                    // The solver was fed the repeatfrom the node WRITES. DeclaredSteps replays the engine's own
                    // formula off the emitted node; JumpStepCount is what the launch was solved against, given the
                    // node's repeatfrom. They agree only when the emitter handed the same value to both.
                    int jFrames = j.Sequence != null && j.Sequence.Frame != null ? j.Sequence.Frame.Length : 0;
                    int jRepeatFrom = j.Sequence != null ? j.Sequence.RepeatFromFrame : 0;
                    if (declaredSteps != PetEmitter.JumpStepCount(jFrames, jRepeatFrom))
                        failures.Add("'" + j.Name + "' declares " + declaredSteps + " steps but its arc was solved for "
                            + PetEmitter.JumpStepCount(jFrames, jRepeatFrom) + " (repeatfrom " + jRepeatFrom
                            + "): the solver was fed a different repeatfrom than the node carries, so the height is wrong silently");

                    // FLAT interval. Hornet's Grapple4 inherited an 80ms -> 4000ms ramp and hung motionless
                    // 12px off the ground for two of its three steps.
                    int iv0 = ParseIntOrZero(j.Start != null ? j.Start.Interval : null);
                    int ivN = ParseIntOrZero(j.End != null ? j.End.Interval : null);
                    if (iv0 != ivN)
                        failures.Add("'" + j.Name + "' has a ramping interval (" + iv0 + " -> " + ivN
                            + "); an arc must not change pace, or the pet freezes in mid-air");

                    // Bounded SIDEWAYS travel, for the same reason the height is bounded and found the same
                    // way: with the arc fixed, Grapple4's -100px per tick crossed the screen and 16 of 18
                    // jumps ended at a side border instead of landing. The landing set is unreachable on a
                    // jump that never comes down where it took off.
                    int span = Math.Abs(ParseIntOrZero(j.Start != null ? j.Start.X : null)) * declaredSteps;
                    if (span > PetEmitter.JumpSpanPx + declaredSteps)
                        failures.Add("'" + j.Name + "' travels " + span + "px sideways over its arc; a jump "
                            + "that crosses the screen meets a side border instead of landing");

                    // ---- PHASE 2: the sequence hands to the DESCENT, not to a standing hub ----
                    if (fallAnim == null)
                    {
                        failures.Add("no fall animation emitted, so a jump has nothing to descend into");
                    }
                    else
                    {
                        List<int> seqTargets = SequenceTargetsOf(j);
                        if (!seqTargets.Contains(fallAnim.Id))
                            failures.Add("'" + j.Name + "' does not lead to `fall` at its sequence end, so an "
                                + "arc that outlives its drop leaves the pet in a standing pose in mid-air");
                        if (seqTargets.Contains(j.Id))
                            failures.Add("'" + j.Name + "' can re-enter itself at its sequence end; re-jumping "
                                + "belongs on the LANDING edge, because the taskbar border fires first");
                    }

                    // ---- PHASE 3: the LANDING ----
                    // only="taskbar", which is what the host raises when the pet reaches the floor. Before
                    // this the only floor-eligible edge was the only="none" turn, so every landing was a
                    // facing flip into the hub's idle dwell.
                    if (!HasBorderEdgeTo(r, j.Id, "taskbar"))
                        failures.Add("'" + j.Name + "' has no only=\"taskbar\" self edge, so it can never chain "
                            + "hops (the sheep's jump re-enters itself on landing at weight 30)");

                    int landRunId = -1, landRunWeight = 0, turnWeight = 0;
                    if (j.Border != null && j.Border.Next != null)
                        foreach (XmlData.NextNode n in j.Border.Next)
                        {
                            if (n == null) continue;
                            if (n.OnlyFlag == "taskbar" && n.Value != j.Id)
                            {
                                landRunId = n.Value;
                                landRunWeight = n.Probability;
                            }
                            if (string.IsNullOrEmpty(n.OnlyFlag) || n.OnlyFlag == "none") turnWeight = n.Probability;
                        }
                    if (landRunId < 0)
                    {
                        failures.Add("'" + j.Name + "' lands into nothing but itself and the turn; it must be "
                            + "able to arrive on its feet and keep moving");
                    }
                    else
                    {
                        // ...and what it lands into must actually MOVE. An edge to another idle would be the
                        // reported bug with extra steps.
                        XmlData.AnimationNode landRun = FindAnimationById(r, landRunId);
                        if (landRun == null || ParseIntOrZero(landRun.Start != null ? landRun.Start.X : null) == 0)
                            failures.Add("'" + j.Name + "' lands into '" + (landRun == null ? "?" : landRun.Name)
                                + "', which does not travel horizontally, so the landing still stops dead");
                        if (landRun != null && ParseIntOrZero(landRun.Start != null ? landRun.Start.Y : null) < 0)
                            failures.Add("'" + j.Name + "' lands into '" + landRun.Name + "', itself a launcher; "
                                + "that gives two hops with no beat between them");
                    }
                    // The landing must OUTWEIGH the turn, or the fix is decoration: turn is only="none" and so
                    // competes at the taskbar too.
                    if (turnWeight > 0 && landRunWeight + LandingSelfWeight(j) <= turnWeight)
                        failures.Add("'" + j.Name + "' lands into motion at " + (landRunWeight + LandingSelfWeight(j))
                            + " against a turn at " + turnWeight + ", so a landing still mostly flips and stands");
                }

                // ---- a rise too weak to be a jump is FLATTENED, not passed through ----
                // The negative case. Without it every assertion above passes on a converter that treats any
                // VelY < 0 as a jump, which is what shipped Grapple1 as a 16px twitch.
                XmlData.AnimationNode hover = FindAnimationNamed(r, "Hover");
                if (hover == null)
                {
                    failures.Add("the weak launcher emitted nothing; it must convert and keep its sprites");
                }
                else
                {
                    if (ParseIntOrZero(hover.Start != null ? hover.Start.Y : null) < 0
                        || ParseIntOrZero(hover.End != null ? hover.End.Y : null) < 0)
                        failures.Add("a rise too weak to be a jump reached the output unflattened, so it plays "
                            + "as a twitch (source y=-5, below the -8 a jump needs)");
                    if (ParseIntOrZero(hover.Start != null ? hover.Start.X : null) == 0)
                        failures.Add("flattening the weak rise also dropped the horizontal motion");
                    // It is NOT a jump, so it must carry neither the jump's landing edge nor the window
                    // underside: an animation that cannot leave the ground can never meet either.
                    if (HasBorderEdgeTo(r, hover.Id, "taskbar"))
                        failures.Add("the flattened animation carries a jump landing edge it can never reach");
                    if (ceiling != null && BorderSourcesOf(r, ceiling.Id, "window-bottom")
                            .FindIndex(delegate(XmlData.AnimationNode n) { return n.Id == hover.Id; }) >= 0)
                        failures.Add("the flattened animation is offered the window underside, which only a jump can reach");
                    // Gravity is the counterpart: a jump omits it, everything on the floor keeps it.
                    if (hover.Gravity == null)
                        failures.Add("the flattened animation lost its <gravity> node, so it hangs when it walks off an edge");
                }

                if (!r.Residue.Notes.Exists(s => s.IndexOf("Jumping IS converted", StringComparison.Ordinal) >= 0))
                    failures.Add("residue does not report the converted jump");
                if (!r.Residue.Notes.Exists(s => s.IndexOf("too gently to be jumps", StringComparison.Ordinal) >= 0))
                    failures.Add("residue does not report the flattened rise, so the loss is silent");

                // --- GAZE ---------------------------------------------------------------------------------
                // A stationary cursor-conditioned action converts to a real animation tagged faceCursor, which
                // the host reads to aim the pet at the pointer as the animation starts. Before this it emitted
                // NOTHING: the cursor condition makes it Group2, IsFloorAction demands Group1, and it fell out
                // of the sheet, the spoke list and the pet in silence.
                XmlData.AnimationNode gaze = FindAnimationNamed(r, "SitAndLookAtMouse");
                if (gaze == null)
                {
                    failures.Add("the gaze action emitted nothing, so the pet never looks at the pointer");
                }
                else
                {
                    if (gaze.Sequence == null || !string.Equals(gaze.Sequence.Action, "faceCursor", StringComparison.Ordinal))
                        failures.Add("the gaze animation carries no faceCursor action, so it plays facing whichever way the pet already was");

                    // The UNCONDITIONAL variant, not the first. The first is "pointer near the top of the
                    // screen"; shipping that would leave the pet permanently craning upward. Three variants,
                    // so "took the last CONDITIONAL one" is a distinguishable wrong answer too.
                    int neutral, craning;
                    bool haveNeutral = sheet.FrameIndexByKey.TryGetValue(
                        PoseKeyOfVariant(config, "SitAndLookAtMouse", 2), out neutral);
                    bool haveCraning = sheet.FrameIndexByKey.TryGetValue(
                        PoseKeyOfVariant(config, "SitAndLookAtMouse", 0), out craning);
                    if (!haveNeutral)
                        failures.Add("the gaze's unconditional variant was never composited into the sheet");
                    else if (gaze.Sequence == null || gaze.Sequence.Frame == null || gaze.Sequence.Frame.Length == 0
                             || gaze.Sequence.Frame[0] != neutral)
                        failures.Add("the gaze used a conditional variant instead of the unconditional fallback pose");
                    if (haveCraning && !TileIsPainted(sheet, craning))
                        failures.Add("the gaze's conditional variant is a blank tile");

                    // Reachable, or it is decoration in the file that never plays.
                    if (!HubSequenceTargets(r).Contains(gaze.Id))
                        failures.Add("the floor hub cannot select the gaze, so it never plays");

                    // Frame-identical, velocity-identical to Doze, and it must NOT have been collapsed into it:
                    // the faceCursor tag is the entire difference between the two.
                    XmlData.AnimationNode doze = FindAnimationNamed(r, "Doze");
                    if (doze == null)
                        failures.Add("the gaze and the same-framed plain rest collapsed together, losing one of them");
                    else if (doze.Sequence != null && string.Equals(doze.Sequence.Action, "faceCursor", StringComparison.Ordinal))
                        failures.Add("a plain rest was tagged faceCursor, so faceCursor is being applied by frame rather than by action");
                }

                // NAME IS NOT EVIDENCE. PetEmitter.Has matched case-insensitively while its opposite number
                // in ActionClassifier used Ordinal, and both run over the same SubtreeBlob -- which includes
                // the action's Name -- looking for the same lowercase "cursor". So an action merely CALLED
                // something with "Cursor" in it was emitted as a gaze while the classifier reported no cursor
                // state, and the stray tag was the least of it: VariantFor switches to the
                // last-unconditional-variant rule, and CollapseDirectionPairs refuses to merge it with an
                // identical non-gaze sibling because IsGaze is part of the match key.
                //
                // Ordinal is the right reading, and consistently so: every other token the classifier tests
                // for is a case-sensitive Shimeji identifier -- activeIE, totalCount, TargetX, Math.random --
                // and "cursor" comes from mascot.environment.cursor.
                XmlData.AnimationNode namedNotGaze = FindAnimationNamed(r, "RestNearCursor");
                if (namedNotGaze == null)
                {
                    failures.Add("the cursor-NAMED plain rest emitted nothing, so the case below is untested");
                }
                else if (namedNotGaze.Sequence != null
                         && string.Equals(namedNotGaze.Sequence.Action, "faceCursor", StringComparison.Ordinal))
                {
                    failures.Add("an action was tagged faceCursor for having \"Cursor\" in its NAME, with no cursor condition anywhere");
                }

                // A FILENAME IS NOT EVIDENCE EITHER. The blob both Has() helpers read used to hold every
                // attribute value, so a plain Stay whose sprite is /cursorsetup01.png -- the Victim skin's
                // CursorHate, seven copies in the corpus, no Condition at all -- was Group2 "branches on cursor
                // position" and emitted as a faceCursor gaze on the strength of its art's name (F441). The blob
                // is expression text now (Conditions and scripted values), so this must be a plain Group1 rest:
                // emitted, untagged, and in no residue bucket.
                XmlData.AnimationNode cursorArt = FindAnimationNamed(r, "CursorHate");
                if (cursorArt == null)
                    failures.Add("the action whose sprite is named cursor*.png emitted nothing, so the case below is untested");
                else if (cursorArt.Sequence != null
                         && string.Equals(cursorArt.Sequence.Action, "faceCursor", StringComparison.Ordinal))
                    failures.Add("an action was tagged faceCursor for having \"cursor\" in its sprite FILENAME, with no cursor condition anywhere");
                if (ResidueHas(r.Residue.Degraded, "CursorHate"))
                    failures.Add("an action was filed as degraded (cursor state) because its sprite file is named cursor*.png");
                foreach (ShimejiAction act in config.Actions)
                    if (string.Equals(act.Name, "CursorHate", StringComparison.Ordinal) && act.Group != FidelityGroup.Group1)
                        failures.Add("the classifier graded 'CursorHate' " + act.Group + " (" + act.Reason + ") on the strength of a sprite filename");

                // The gaze whose art nothing else uses. Its only route into the sheet is the gaze arm of
                // PosesToComposite, so this is the assertion that fails when gaze poses stop being composited.
                XmlData.AnimationNode lonelyGaze = FindAnimationNamed(r, "StandAndWatchMouse");
                if (lonelyGaze == null)
                    failures.Add("a gaze with no shared art emitted nothing, so gaze poses are not reaching the sprite sheet");
                else if (lonelyGaze.Sequence == null || !string.Equals(lonelyGaze.Sequence.Action, "faceCursor", StringComparison.Ordinal))
                    failures.Add("the second gaze carries no faceCursor action");
                else
                {
                    // ITS TIMING COMES FROM THE VARIANT IT PLAYS. The frames come from the catch-all (asserted
                    // for SitAndLookAtMouse above) while BuildSpoke read its intervals from Animations[0], the
                    // cursor-conditioned variant, so a gaze whose variants differ in Duration cycled at the
                    // wrong pace (F428). This fixture's catch-all holds two frames at 10 ticks (400ms) and its
                    // conditional variant one frame at 2 ticks (80ms): a two-frame rest keeps its (capped)
                    // per-frame interval, so the emitted interval names which variant was read.
                    int gazeFrames = lonelyGaze.Sequence.Frame != null ? lonelyGaze.Sequence.Frame.Length : 0;
                    if (gazeFrames != 2)
                        failures.Add("the two-frame gaze catch-all emitted " + gazeFrames + " frame(s); the timing "
                            + "assertion below needs the multi-frame rest path");
                    int gi0 = ParseIntOrZero(lonelyGaze.Start != null ? lonelyGaze.Start.Interval : null);
                    int giN = ParseIntOrZero(lonelyGaze.End != null ? lonelyGaze.End.Interval : null);
                    if (gi0 != 400 || giN != 400)
                        failures.Add("the gaze plays its catch-all frames at " + gi0 + "/" + giN + "ms; the catch-all "
                            + "is authored at 10 ticks (400ms), so its timing was read from a variant it does not play");
                    // WITNESS: the two variants' durations differ in the fixture, or 400 could have come from
                    // either and the line above could not tell them apart.
                    if (DurationOfVariantPose(config, "StandAndWatchMouse", 0) == DurationOfVariantPose(config, "StandAndWatchMouse", 1))
                        failures.Add("WITNESS: the gaze fixture's variants share a Duration, so the interval assertion "
                            + "cannot tell which variant was read");
                }

                // ---- AND ITS SOUND. The same Animations[0] read sat in FirstSoundClip, so the clip attached
                // to a gaze was the cursor-conditioned variant's while the frames were the catch-all's. The
                // loader here records what was asked for and embeds nothing, so the assertion is on the
                // REQUEST: the conditional variant's clip must never be asked for, and Stand's clip must be
                // (the witness that the recorder saw the requests at all).
                var requestedClips = new List<string>();
                Func<string, byte[]> recordClips = delegate(string clip) { requestedClips.Add(clip); return null; };
                ConversionResult rq = PetEmitter.Emit(config, sheet, load, "TestSkinClips", recordClips);
                if (!rq.Valid)
                    failures.Add("the clip-recording emit produced invalid XML: " + rq.Error);
                if (requestedClips.Contains("/look.wav"))
                    failures.Add("the gaze's cursor-conditioned variant's clip (/look.wav) was requested, but the pet "
                        + "plays the catch-all variant, which carries no sound");
                if (!requestedClips.Contains("/beep.wav"))
                    failures.Add("WITNESS: Stand's clip (/beep.wav) was never requested, so the clip assertion above "
                        + "proves nothing");

                // ---- THE ROOM THE SHEET LEFT, CHARGED PER EMBEDDING ----
                // Two budgets used to be written as if each owned the whole 12 MiB: the compositor accepts a
                // sheet as soon as it plus a markup allowance fits, and SoundBaker allowed a fixed 3 MiB of
                // MP3 knowing nothing about the sheet, so a near-cap sheet plus clips validated on a box
                // without ffmpeg and failed the 12 MiB check on one with it (F435); and the baker charged a
                // clip once while the emitter embeds it once per animation that plays it (F426). The emitter
                // now budgets each embedding against XmlBudgetBytes - the sheet's projection. The loader here
                // hands back a 200 KiB MPEG-synced stub for every clip, so the validator's sniff accepts it and
                // the count of <sound> nodes is the whole observation.
                byte[] fakeMp3 = FakeMp3(200 * 1024);
                Func<string, byte[]> stubClips = delegate(string clip) { return fakeMp3; };
                // WITNESS: with the sheet's real projection every embedding fits, and there are FIVE of them
                // for two clips -- Stand's, and Walk's on Walk plus the three chain steps that replay it,
                // StrollBackPlayed_1_WalkBack among them because it plays Walk's poses and so Walk's clip
                // (N-tools-01) -- which is more than the near-cap room below admits.
                ConversionResult rs = PetEmitter.Emit(config, sheet, load, "TestSkinSound", stubClips);
                int embedded = EmbeddedSoundCount(rs);
                if (!rs.Valid || !rs.Accepted)
                    failures.Add("with room for every clip the sounded pet was not accepted: " + rs.Error);
                if (embedded != 5)
                    failures.Add("WITNESS: expected 5 embedded sounds (Stand, Walk, GatorRide_1_Walk, StrollAndHop_1_Walk, "
                        + "StrollBackPlayed_1_WalkBack), got " + embedded + " [" + string.Join(", ", EmbeddedSoundNames(rs).ToArray())
                        + "]; the room assertion below needs more embeddings than the room admits");
                // A clip embedded N times is encoded ONCE (RA-371). The stub hands back the same byte[] for every
                // clip, as SoundBaker does for a repeat of one clip, so the five nodes must share one base64
                // string instance; per-node encoding built five copies of ~270 KB, and built one for every
                // REFUSED embedding too, before the room test threw it away. The document is byte-identical
                // either way, so identity is the only observation.
                if (embedded == 5 && rs.Root.Sounds != null && rs.Root.Sounds.Sound != null)
                {
                    string firstEncoding = rs.Root.Sounds.Sound[0].Base64;
                    foreach (XmlData.SoundNode sn in rs.Root.Sounds.Sound)
                        if (!ReferenceEquals(sn.Base64, firstEncoding))
                        {
                            failures.Add("a clip embedded five times was base64-encoded more than once (the <sound> nodes do not "
                                + "share one string), so every embedding of a shared clip re-encodes it");
                            break;
                        }
                }
                // A sheet that left room for exactly TWO embeddings: the projection the compositor would have
                // reported for a near-cap sheet, set on the real sheet so the document stays small and valid
                // while the arithmetic under test sees a 12 MiB document. Restored afterwards.
                int base64Length = Convert.ToBase64String(fakeMp3).Length;
                int realProjection = sheet.ProjectedXmlBytes;
                sheet.ProjectedXmlBytes = SpriteSheetBuilder.XmlBudgetBytes
                    - (2 * (base64Length + PetEmitter.SoundMarkupAllowanceBytes) + 64);
                try
                {
                    ConversionResult rn = PetEmitter.Emit(config, sheet, load, "TestSkinNearCap", stubClips);
                    int nearCap = EmbeddedSoundCount(rn);
                    if (!rn.Valid)
                        failures.Add("a near-cap sheet plus clips emitted invalid XML: " + rn.Error);
                    if (nearCap != 2)
                        failures.Add("a sheet with room for two clips embedded " + nearCap
                            + "; the audio is budgeted against a fixed allowance that knows nothing about the sheet");
                    long audioInDocument = 0;
                    if (rn.Root != null && rn.Root.Sounds != null && rn.Root.Sounds.Sound != null)
                        foreach (XmlData.SoundNode sn in rn.Root.Sounds.Sound)
                            if (sn != null && sn.Base64 != null) audioInDocument += sn.Base64.Length;
                    if (sheet.ProjectedXmlBytes + audioInDocument > SpriteSheetBuilder.XmlBudgetBytes)
                        failures.Add("the sheet's projection plus the embedded audio (" + sheet.ProjectedXmlBytes + " + "
                            + audioInDocument + ") exceeds the " + SpriteSheetBuilder.XmlBudgetBytes
                            + " byte budget the validator enforces");
                    if (rn.Residue == null || !rn.Residue.Notes.Exists(s => s.IndexOf("left no room", StringComparison.Ordinal) >= 0))
                        failures.Add("the residue does not say the dropped clips had no room under the pet limit, so "
                            + "the loss reads as missing clips");
                    // The figures in that sentence come from the constants the budget reads, not from typed
                    // literals (RA-376): "12 MiB" and "8 MiB" used to be prose that would have stayed put when
                    // MaximumXmlBytes moved, as it already had once.
                    string expectedLimit = (SpriteSheetBuilder.XmlBudgetBytes / (1024 * 1024)) + " MiB pet limit";
                    string expectedAudio = (DesktopAICompanion.CompanionXmlValidator.MaximumAudioBytesTotal / (1024 * 1024)) + " MiB audio total";
                    if (rn.Residue != null && !rn.Residue.Notes.Exists(s => s.IndexOf(expectedLimit, StringComparison.Ordinal) >= 0
                                                                          && s.IndexOf(expectedAudio, StringComparison.Ordinal) >= 0))
                        failures.Add("the no-room note does not state the pet limit and audio total from the validator's constants ('"
                            + expectedLimit + "', '" + expectedAudio + "')");
                }
                finally
                {
                    sheet.ProjectedXmlBytes = realProjection;
                }

                // ---- THE VALIDATOR'S SOUND COUNT CAP IS BUDGETED TOO (R-068) ----
                // One <sound> node per sounded emitted animation, and the animation cap is 1024, so a skin that
                // plays a small clip on more animations than MaximumSounds passed every BYTE check and produced
                // a document the validator refuses on the 257th node. Built programmatically in F426's own
                // shape, because the hub itself may carry at most MaximumTransitions edges: eight sounded
                // one-frame members (distinct art, or the direction collapse would merge them) and forty
                // behaviour-played set-pieces that each run all eight, so every member is embedded once as a
                // spoke and forty more times as a chain step -- 328 embeddings from a 49-edge hub -- with a 1 KiB
                // stub so the byte caps are nowhere near. The pet must be ACCEPTED with exactly MaximumSounds
                // embeddings and the residue must name the count cap, with its count, as the cause of the rest.
                var manyOwned = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
                try
                {
                    const int SoundedMembers = 8, PlayedRuns = 40;
                    int wanted = SoundedMembers + PlayedRuns * SoundedMembers;
                    var many = new ShimejiConfig();
                    var manyStand = new ShimejiAction { Name = "Stand", Type = "Stay", BorderType = "Floor" };
                    manyStand.Animations.Add(new ShimejiAnimation());
                    manyStand.Animations[0].Poses.Add(new ShimejiPose { Image = "/s.png", AnchorX = 20, AnchorY = 60, Duration = 250 });
                    many.Actions.Add(manyStand);
                    for (int i = 0; i < SoundedMembers; i++)
                    {
                        string image = "/cap" + i + ".png";
                        manyOwned[image] = Solid(40, 60, Color.FromArgb(255, 10 + i * 20, 200 - i * 15, 40 + i * 25));
                        var stay = new ShimejiAction { Name = "Member" + i, Type = "Stay", BorderType = "Floor" };
                        stay.Animations.Add(new ShimejiAnimation());
                        stay.Animations[0].Poses.Add(new ShimejiPose { Image = image, AnchorX = 20, AnchorY = 60, Duration = 10, Sound = "/beep.wav" });
                        many.Actions.Add(stay);
                    }
                    for (int run = 0; run < PlayedRuns; run++)
                    {
                        var seq = new ShimejiAction { Name = "Run" + run, Type = "Sequence" };
                        for (int i = 0; i < SoundedMembers; i++) seq.ReferencedActions.Add("Member" + i);
                        many.Actions.Add(seq);
                        many.BehaviorFrequency["Run" + run] = 10;   // PLAYED, so the run is chained
                    }
                    var manyFall = new ShimejiAction { Name = "Falling", Type = "Embedded", Class = "Fall" };
                    manyFall.Animations.Add(new ShimejiAnimation());
                    manyFall.Animations[0].Poses.Add(new ShimejiPose { Image = "/f.png", AnchorX = 20, AnchorY = 60, Duration = 4, VelY = 2 });
                    many.Actions.Add(manyFall);
                    foreach (ShimejiAction a in many.Actions) ActionClassifier.Classify(a);
                    Func<string, Bitmap> manyLoad = delegate(string name)
                    {
                        Bitmap own;
                        return new Bitmap(manyOwned.TryGetValue(name, out own) ? own : owned[name]);
                    };
                    SpriteSheet manySheet; string manyErr;
                    if (!SpriteSheetBuilder.Build(Emit.PetEmitter.PosesToComposite(many), manyLoad, false, out manySheet, out manyErr))
                        failures.Add("the sound-count fixture would not composite: " + manyErr);
                    else
                    {
                        byte[] smallClip = FakeMp3(1024);
                        ConversionResult rc = PetEmitter.Emit(many, manySheet, manyLoad, "ManySounds", delegate(string clip) { return smallClip; });
                        int capped = EmbeddedSoundCount(rc);
                        if (!rc.Valid || !rc.Accepted)
                            failures.Add("a skin with more sounded animations than the format allows sounds was not accepted ("
                                + rc.Error + "): the sound loop budgets bytes but not the validator's "
                                + DesktopAICompanion.CompanionXmlValidator.MaximumSounds + "-sound count cap");
                        if (capped != DesktopAICompanion.CompanionXmlValidator.MaximumSounds)
                            failures.Add("expected exactly " + DesktopAICompanion.CompanionXmlValidator.MaximumSounds
                                + " embedded sounds on the count-capped skin, got " + capped);
                        int refused = wanted - DesktopAICompanion.CompanionXmlValidator.MaximumSounds;
                        string countNote = refused + " because the pet format allows at most " + DesktopAICompanion.CompanionXmlValidator.MaximumSounds + " sounds";
                        if (rc.Residue == null || !rc.Residue.Notes.Exists(s => s.IndexOf(countNote, StringComparison.Ordinal) >= 0
                                                                              && s.IndexOf("of " + wanted + " animation sound", StringComparison.Ordinal) >= 0))
                            failures.Add("the residue does not name the sound count cap, with its count, as the cause of the "
                                + refused + " dropped clips ('" + countNote + "' of " + wanted + "); a refusal filed under the wrong "
                                + "cause sends the user to the wrong number");
                        // WITNESS: the fixture really did want more than the cap, or the assertions above are idle;
                        // read off the emitted pet rather than the fixture's arithmetic.
                        int emittedAnimations = rc.Root != null && rc.Root.Animations != null && rc.Root.Animations.Animation != null
                            ? rc.Root.Animations.Animation.Length : 0;
                        if (emittedAnimations < wanted || wanted <= DesktopAICompanion.CompanionXmlValidator.MaximumSounds)
                            failures.Add("WITNESS: the sound-count fixture emitted " + emittedAnimations + " animations for " + wanted
                                + " sounded ones against a cap of " + DesktopAICompanion.CompanionXmlValidator.MaximumSounds
                                + ", so the count cap was never reached");
                    }
                }
                catch (Exception ex) { failures.Add("the sound-count fixture threw: " + ex); }
                finally
                {
                    foreach (Bitmap b in manyOwned.Values) b.Dispose();
                }

                // REFUSED BY POLICY is its own bucket, and it must be VISIBLE. Asserting on the named note
                // rather than on a count, because a silent refusal reads exactly like a silent loss and the
                // whole accounting rewrite exists to stop the report going quiet about an action.
                if (!r.Residue.Notes.Exists(s => s.IndexOf("REFUSED on purpose", StringComparison.Ordinal) >= 0
                                                 && s.IndexOf("OpenSomething", StringComparison.Ordinal) >= 0))
                    failures.Add("a Type=OpenURL action was not reported as refused by policy; if it reached "
                        + "no bucket at all the accounting line prints it as UNACCOUNTED instead");
                if (r.Residue.Notes.Exists(s => s.IndexOf("UNACCOUNTED", StringComparison.Ordinal) >= 0))
                    failures.Add("the residue reports UNACCOUNTED actions, which it calls a bug in the "
                        + "accounting in its own words");
                if (ResidueHas(r.Residue.Dropped, "OpenSomething"))
                    failures.Add("a refused action was filed under DROPPED, which says the converter could "
                        + "not do it rather than that it will not");

                // ---- A SKIN WHOSE WALL ART DOES NOT CLIMB ----
                // SynthesiseClimbIfNeeded invents an upward velocity for a skin that has wall sprites but no
                // CLIMBING action, so the ceiling region it already owns stays reachable. The main fixture
                // cannot exercise it: its ClimbWall genuinely climbs, so the synthesis never runs and every
                // assertion about it is vacuous. Reverting the fix below to read the SOURCE velocity passed
                // the whole suite, which is how that was discovered.
                //
                // The variant is the same config with ClimbWall's upward pose flattened to 0,0 -- one edit
                // to the XML, and the behaviour frequencies copied across below (ParseActionsXml reads none),
                // so nothing else about the skin differs and the ceiling's fate is the only variable.
                string flatWallXml = SyntheticActionsXml.Replace(
                    "<Pose Image=\"/c2.png\" ImageAnchor=\"20,60\" Velocity=\"0,-2\" Duration=\"4\" />",
                    "<Pose Image=\"/c2.png\" ImageAnchor=\"20,60\" Velocity=\"0,0\" Duration=\"4\" />");
                if (flatWallXml == SyntheticActionsXml)
                    failures.Add("the flat-wall variant did not change the fixture, so the synthesised-climb "
                        + "case below is untested -- ClimbWall's pose must have been edited");
                else
                {
                    ShimejiConfig flatConfig = ShimejiParser.ParseActionsXml(flatWallXml);
                    // Without this the variant differed in a SECOND way -- no frequencies at all -- and the
                    // "one edit" claim above was false, harmlessly today because its chains entered through
                    // RECOVERS, and not harmlessly the day a PLAYED-only set-piece was added (F447).
                    foreach (KeyValuePair<string, int> frequency in config.BehaviorFrequency)
                        flatConfig.BehaviorFrequency[frequency.Key] = frequency.Value;
                    SpriteSheet flatSheet; string flatErr;
                    if (!SpriteSheetBuilder.Build(Emit.PetEmitter.PosesToComposite(flatConfig), load, false,
                                                  out flatSheet, out flatErr))
                        failures.Add("the flat-wall variant would not composite: " + flatErr);
                    else
                    {
                        ConversionResult fr = PetEmitter.Emit(flatConfig, flatSheet, load, "FlatWall");
                        if (fr.Graph != null && fr.Graph.Unreachable.Count != 0)
                            failures.Add("a skin whose wall art does not climb emitted UNREACHABLE animations ("
                                + string.Join(",", fr.Graph.Unreachable) + "); the synthesised climb exists to "
                                + "keep the ceiling reachable and it is not being consulted");
                        if (!fr.Accepted)
                            failures.Add("a skin whose wall art does not climb was NOT accepted, so it cannot "
                                + "be converted at all: " + fr.Error);
                        // And the synthesis must have actually happened, or the two assertions above pass for
                        // the wrong reason (a ceiling that was dropped is also never unreachable).
                        XmlData.AnimationNode flatCeiling = null;
                        foreach (XmlData.AnimationNode a in fr.Root != null && fr.Root.Animations != null &&
                                                            fr.Root.Animations.Animation != null
                                     ? fr.Root.Animations.Animation : new XmlData.AnimationNode[0])
                            if (a != null && a.Name != null &&
                                a.Name.IndexOf("Ceiling", StringComparison.OrdinalIgnoreCase) >= 0)
                            { flatCeiling = a; break; }
                        if (flatCeiling == null)
                            failures.Add("the flat-wall variant kept no ceiling animation at all, so 'the "
                                + "ceiling is still reachable' is vacuous");
                        else if (!HasBorderEdgeTo(fr, flatCeiling.Id, "horizontal"))
                            failures.Add("no only=\"horizontal\" edge reaches the ceiling on the flat-wall "
                                + "variant, so the synthesised climb never offers the way in");
                        else
                        {
                            // AND THE FLOOR ENTERS THE WALL ON THAT CLIMBER. wallEntry was chosen by reading
                            // the SOURCE poses, which know nothing of the climb the synthesis invented, so on
                            // this variant it fell back to wallSpokes[0] -- the flattened ClimbWall, a static
                            // hold -- while the ceiling hung off GrabWall (F425). Reachability survived because
                            // wall spokes chain to each other, which is why nothing above caught it. The
                            // assertion: a locomotion spoke's only="vertical" edge targets the spoke that
                            // carries the only="horizontal" edge into the ceiling.
                            List<XmlData.AnimationNode> climbers = BorderSourcesOf(fr, flatCeiling.Id, "horizontal");
                            XmlData.AnimationNode flatWalk = FindAnimationNamed(fr, "Walk");
                            int entryId = BorderTargetOf(flatWalk, "vertical");
                            if (flatWalk == null || entryId < 0)
                                failures.Add("the flat-wall variant's Walk has no only=\"vertical\" edge, so where a "
                                    + "walker enters the wall is untested");
                            else if (climbers.FindIndex(delegate(XmlData.AnimationNode c) { return c.Id == entryId; }) < 0)
                                failures.Add("a floor walker enters the wall on '" + NameOfId(fr, entryId) + "', not "
                                    + "on the spoke that climbs to the ceiling (" + string.Join(", ", climbers.ConvertAll(
                                        delegate(XmlData.AnimationNode c) { return c.Name; }).ToArray())
                                    + "); the synthesised climb is invisible to the entry choice, so from the floor "
                                    + "the ceiling is never reached");
                        }
                    }
                }

                // ---- EVERY RUNG OF THE MIGRATION LADDER REACHES THE CURRENT FORMAT ----
                // Six of the eight migrations used to stamp the LATEST version instead of their own next
                // rung, and the damage was invisible from any one of them: a pet at 0.3 ran `rejump`, which
                // fixed its jumps and stamped 1.1, and then reclimb, restsplit, dedupe and undirect each
                // printed "skip (already at format 1.1)". It ended up claiming to be fully migrated while
                // still carrying short climbs, wrong rest dwells, duplicate cells and _left/_right names.
                // This walks the table instead of trusting it.
                foreach (string from in PetEmitter.KnownFormatVersions())
                {
                    string at = from;
                    int hops = 0;
                    while (!string.Equals(at, PetEmitter.ConvertedFormatVersion, StringComparison.Ordinal))
                    {
                        string verb = PetEmitter.MigrationVerbFor(at);
                        if (verb == null)
                        {
                            // 0.2 is the ONE allowed terminus, and it is allowed because a migration cannot
                            // add a ceiling region: that needs sprite frames only a fresh conversion makes.
                            // Any OTHER dead end is a rung somebody added without a verb to leave it by, and
                            // a pet that lands there is stuck with no way to find out.
                            if (!string.Equals(at, PetEmitter.ConvertedFormatVersionDampedWeights, StringComparison.Ordinal))
                                failures.Add("format " + at + " (reached from " + from + ") has no migration "
                                    + "verb, so a pet there can never reach " + PetEmitter.ConvertedFormatVersion
                                    + "; only 0.2 is allowed to be a dead end");
                            break;
                        }
                        string next = PetEmitter.NextFormatVersionAfter(at);
                        if (string.Equals(next, at, StringComparison.Ordinal))
                        {
                            failures.Add("format " + at + " advances to itself via `" + verb + "`, so the "
                                + "ladder loops for ever");
                            break;
                        }
                        at = next;
                        if (++hops > 20) { failures.Add("the ladder from " + from + " did not terminate"); break; }
                    }
                }
                // The walk above is vacuous if the table is empty, and would also pass if it held exactly one
                // entry naming the current version.
                if (PetEmitter.KnownFormatVersions().Count < 5)
                    failures.Add("the format ladder lists only " + PetEmitter.KnownFormatVersions().Count
                        + " versions, so walking it proves nothing");

                // ONLY THE LAST RUNG MAY STAMP THE CURRENT VERSION, and this is the assertion that catches
                // the original defect. Termination alone does not: a rung that jumps straight to 1.1 also
                // terminates, which is exactly how six migrations passed for months while skipping every
                // migration after themselves. Mutation-tested by pointing `rejump` at the current version.
                List<string[]> rungs = PetEmitter.FormatLadderRungs();
                if (rungs.Count < 6)
                    failures.Add("the format ladder holds only " + rungs.Count + " rungs; the shape assertions "
                        + "below prove little against a gutted table");
                for (int i = 0; i < rungs.Count; i++)
                {
                    bool last = i == rungs.Count - 1;
                    bool stampsCurrent = string.Equals(rungs[i][2], PetEmitter.ConvertedFormatVersion,
                                                       StringComparison.Ordinal);
                    if (stampsCurrent && !last)
                        failures.Add("rung " + rungs[i][0] + " (`" + rungs[i][1] + "`) stamps the CURRENT "
                            + "format " + rungs[i][2] + " instead of its own next rung, so every migration "
                            + "after it will skip the pet while it still needs them");
                    if (last && !stampsCurrent)
                        failures.Add("the last rung " + rungs[i][0] + " (`" + rungs[i][1] + "`) stamps "
                            + rungs[i][2] + ", not the current format, so nothing ever reaches "
                            + PetEmitter.ConvertedFormatVersion);
                    if (string.Equals(rungs[i][0], rungs[i][2], StringComparison.Ordinal))
                        failures.Add("rung " + rungs[i][0] + " (`" + rungs[i][1] + "`) stamps what it gates on");
                }

                // 0.2 is the one version a migration genuinely cannot move on: 0.3 gained the ceiling region
                // and a region needs sprite frames only a fresh conversion produces. The tool has to SAY that
                // rather than print a skip line, so the predicate that decides it is asserted both ways.
                if (!PetEmitter.FormatVersionIsStranded(PetEmitter.ConvertedFormatVersionDampedWeights))
                    failures.Add("format 0.2 is not reported as stranded, so a pet reweighted from 0.1 gets a "
                        + "skip line that reads as 'nothing to do' and is never re-converted");
                if (PetEmitter.FormatVersionIsStranded(PetEmitter.ConvertedFormatVersion))
                    failures.Add("the CURRENT format is reported as stranded, which would tell every up-to-date "
                        + "pet to be re-converted");
                foreach (string v in PetEmitter.KnownFormatVersions())
                    if (!string.Equals(v, PetEmitter.ConvertedFormatVersionDampedWeights, StringComparison.Ordinal)
                        && PetEmitter.FormatVersionIsStranded(v))
                        failures.Add("format " + v + " is on the ladder AND reported as stranded");

                if (!ResidueHas(r.Residue.Dropped, "ThrowIe")) failures.Add("Group3 ThrowIe not recorded as dropped");
                if (!ResidueHas(r.Residue.Degraded, "SitAndLookAtMouse")) failures.Add("Group2 cursor action not recorded as degraded");
                // ...and says what was actually lost. The classifier's stock reason ("needs cursorX/cursorY,
                // added in Stage 5") is now false for a gaze, and a residue report that reports a shipped
                // capability as pending is worse than one that says nothing.
                if (ResidueDetailOf(r.Residue.Degraded, "SitAndLookAtMouse").IndexOf("faceCursor", StringComparison.Ordinal) < 0)
                    failures.Add("the residue still describes the gaze as needing a host change that has shipped");
                if (!r.Residue.Notes.Exists(s => s.IndexOf("sound", StringComparison.OrdinalIgnoreCase) >= 0))
                    failures.Add("residue did not note the dropped pose sound");
                if (!r.Residue.Notes.Exists(s => s.IndexOf("script", StringComparison.OrdinalIgnoreCase) >= 0))
                    failures.Add("residue did not note script-computed values");

                // Colour-key path keeps writing the magenta key.
                if (r.Root == null || r.Root.Image == null || r.Root.Image.Transparency != "Magenta")
                    failures.Add("colour-key pet did not declare <transparency>Magenta</transparency>");

                // Alpha path: same skin composited with real alpha must (a) declare the reserved
                // "Alpha" keyword the host renders per-pixel, and (b) leave genuinely-transparent
                // pixels in the sheet (empty cell area) instead of flattening onto magenta.
                SpriteSheet alphaSheet;
                if (!SpriteSheetBuilder.Build(Emit.PetEmitter.PosesToComposite(config), load, true, out alphaSheet, out error))
                {
                    failures.Add("alpha-mode compositing failed -- " + error);
                }
                else
                {
                    if (!alphaSheet.IsAlpha) failures.Add("alpha sheet did not carry IsAlpha");
                    if (!HasFullyTransparentPixel(alphaSheet.PngBytes))
                        failures.Add("alpha sheet has no fully-transparent pixel (background was flattened, not kept)");

                    ConversionResult ra = PetEmitter.Emit(config, alphaSheet, load, "TestSkinAlpha");
                    if (ra.Root == null || ra.Root.Image == null || ra.Root.Image.Transparency != "Alpha")
                        failures.Add("alpha pet did not declare <transparency>Alpha</transparency>");
                    if (!ra.Valid) failures.Add("alpha-mode emitted XML failed the validator: " + ra.Error);
                    if (!ra.Accepted) failures.Add("alpha-mode result not accepted (valid+roundtrip+reachable)");
                }
            }
            // A CATCH, like the two sibling blocks below have. Without one a throw anywhere in the two thousand
            // lines above unwound past the failures already collected, and the suite reported one line -- the
            // exception -- with none of the earlier FAILs that would have pointed at the cause (RA-382). The
            // whole exception, so the frame that threw is in the report.
            catch (Exception ex) { failures.Add("main fixtures threw: " + ex); }
            finally
            {
                foreach (Bitmap b in owned.Values) b.Dispose();
            }

            // --- direction-suffix stripping (PetEmitter.UndirectNames) -----------------------------------
            // A converted pet mirrors its whole sheet on <action>flip</action>, so `walk_left` already walks
            // both ways and the suffix reads as a limit the pet does not have. What must NEVER happen is a
            // rename that changes meaning, so each refusal below is asserted, not assumed.
            {
                Dictionary<string, string> m = PetEmitter.UndirectNames(new[]
                {
                    "walk_left", "climb_ceiling_left", "pull_up_right",
                    "sit", "sit_left",          // target already exists -> both keep their names
                    "fall_left",                // would become the magic "fall"
                    "creep_left", "creep_right" // two claimants for "creep" -> neither wins
                });
                string got;
                if (!(m.TryGetValue("walk_left", out got) && got == "walk"))
                    failures.Add("undirect: walk_left should become walk");
                if (!(m.TryGetValue("climb_ceiling_left", out got) && got == "climb_ceiling"))
                    failures.Add("undirect: climb_ceiling_left should become climb_ceiling");
                if (!(m.TryGetValue("pull_up_right", out got) && got == "pull_up"))
                    failures.Add("undirect: a _right suffix should strip too");
                if (m.ContainsKey("sit_left"))
                    failures.Add("undirect: sit_left must NOT take a name an existing animation already has");
                if (m.ContainsKey("fall_left"))
                    failures.Add("undirect: fall_left must NOT become the magic name fall");
                if (m.ContainsKey("creep_left") || m.ContainsKey("creep_right"))
                    failures.Add("undirect: two animations wanting one name must both keep theirs");
                if (m.ContainsKey("sit"))
                    failures.Add("undirect: a name with no direction suffix must be left alone");
                // Idempotent: running it on already-bare names must be a no-op, so a re-run cannot churn.
                if (PetEmitter.UndirectNames(new[] { "walk", "climb_ceiling", "sit" }).Count != 0)
                    failures.Add("undirect: a second pass over bare names should change nothing");
            }



            // ---- the drag swing draws exactly what it references ----
            // PosesToComposite decides what goes INTO the sheet; DragSwingFramesOf decides what is named
            // out of it. They disagreed: the first took every pose of every variant, the second took
            // Poses[0] of each, so the remainder were drawn and never referenced. Asserted here rather
            // than by counting tiles in a converted pet, because the tile count also moves with grid-tail
            // padding and would not say WHICH cause moved it.
            try
            {
                var dragConfig = new ShimejiConfig();
                var dragged = new ShimejiAction { Name = "Dragged", Class = "Dragged", Type = "Stay" };
                for (int variant = 0; variant < 3; variant++)
                {
                    var swing = new ShimejiAnimation();
                    for (int pose = 0; pose < 4; pose++)
                        swing.Poses.Add(new ShimejiPose
                        {
                            Image = "/drag" + variant + "_" + pose + ".png",
                            AnchorX = 64,
                            AnchorY = 128,
                            Duration = 1,
                        });
                    dragged.Animations.Add(swing);
                }
                dragConfig.Actions.Add(dragged);

                List<ShimejiPose> composited = PetEmitter.PosesToComposite(dragConfig);
                if (composited.Count != 3)
                    failures.Add("drag swing composited " + composited.Count +
                        " poses for 3 variants of 4 poses; expected 3, one per variant");
                // The FIRST of each variant, because that is the one the swing arc names.
                for (int variant = 0; variant < 3 && variant < composited.Count; variant++)
                    if (composited[variant].Image != "/drag" + variant + "_0.png")
                        failures.Add("drag variant " + variant + " composited '" +
                            composited[variant].Image + "', expected its first pose");
            }
            catch (Exception ex) { failures.Add("drag swing compositing threw: " + ex.Message); }

            // --- A SKIN WITH NO LOCOMOTION MUST STILL BE ACCEPTED -------------------------------------
            // The synthesised `turn` used to be emitted unconditionally while its only inbound edge sat
            // behind `if (loco)`, so a skin with no Type="Move" action produced an animation nothing could
            // reach: Graph.Unreachable.Count == 1, Accepted == false, CLI exit 1, on a pet that is
            // otherwise valid and playable. Hand-trimmed and single-pose skins hit it; the shipped corpus
            // never did, because every pet in it carries a Walk -- which is exactly why the main fixture
            // above could not catch it, and why this needs a fixture of its own.
            //
            // Declared OUTSIDE the try and disposed in a finally, like `owned` above and the compositor's
            // fixtures: this was the one fixture in the suite that left its source bitmaps to the finalizer
            // (F449), and a fixture copied from it inherited the omission.
            var flat = new Dictionary<string, Bitmap>(StringComparer.Ordinal)
            {
                { "/n1.png", Solid(40, 60, Color.FromArgb(255, 210, 190, 120)) },
                { "/n2.png", Solid(40, 60, Color.FromArgb(255, 190, 170, 100)) },
                // Wall and ceiling art for the two region fixtures below; distinct, so nothing collapses.
                { "/n3.png", Solid(40, 60, Color.FromArgb(255, 120, 150, 190)) },
                { "/n4.png", Solid(40, 60, Color.FromArgb(255, 100, 130, 170)) },
                // The mount-prefix fixture's own climbing art, distinct from its mount frames.
                { "/n5.png", Solid(40, 60, Color.FromArgb(255, 80, 110, 150)) },
                { "/n6.png", Solid(40, 60, Color.FromArgb(255, 60, 90, 130)) },
            };
            try
            {
                ShimejiConfig still = ShimejiParser.ParseActionsXml(NoLocomotionActionsXml);
                Func<string, Bitmap> loadFlat = delegate(string name) { return new Bitmap(flat[name]); };

                SpriteSheet flatSheet;
                string flatError;
                if (!SpriteSheetBuilder.Build(
                        Emit.PetEmitter.PosesToComposite(still), loadFlat, false, out flatSheet, out flatError))
                {
                    failures.Add("no-locomotion fixture failed to composite: " + flatError);
                }
                else
                {
                    ConversionResult nr = PetEmitter.Emit(still, flatSheet, loadFlat, "StillSkin");
                    if (!nr.Valid)
                        failures.Add("a skin with no Move action emitted invalid XML: " + nr.Error);
                    if (nr.Graph == null || nr.Graph.Unreachable.Count != 0)
                        failures.Add("a skin with no Move action left unreachable animations: " +
                            (nr.Graph == null ? "(no graph)" : string.Join(",", nr.Graph.Unreachable)));
                    if (!nr.Accepted)
                        failures.Add("a skin with no Move action was not accepted, so a valid playable pet fails conversion");
                    // The POINT of the fix: no turn at all, rather than a turn nothing can enter. This
                    // asserts ABSENCE, which is only the right assertion while `turn`'s sole inbound edge
                    // needs locomotion -- give it another and this is the line to revisit.
                    if (nr.EmittedXml != null &&
                        nr.EmittedXml.IndexOf(">turn<", StringComparison.Ordinal) >= 0)
                        failures.Add("a skin with no locomotion still emitted a `turn` animation");
                }

                // ---- THE SAME SKIN WITH WALL AND CEILING ART ----
                // Every inbound edge to a wall spoke is a LOCOMOTION floor spoke's border edge, another wall
                // spoke, or a ceiling exit -- and the ceiling is entered from a climbing wall spoke or a
                // jump. So a skin with wall sprites and no Type="Move" floor action emitted its whole wall
                // region with nothing able to enter it: Unreachable non-empty, Accepted false, the CLI
                // exiting 1 on XML the app's validator accepted (F429). The `turn` fix above records the
                // identical failure and stopped at `turn`. ClimbWall here is Type="Move" ON THE WALL, which
                // is not floor locomotion and must not count as a way in.
                ShimejiConfig walled = ShimejiParser.ParseActionsXml(NoLocomotionWallActionsXml);
                SpriteSheet walledSheet;
                string walledError;
                if (!SpriteSheetBuilder.Build(
                        Emit.PetEmitter.PosesToComposite(walled), loadFlat, false, out walledSheet, out walledError))
                {
                    failures.Add("no-locomotion wall fixture failed to composite: " + walledError);
                }
                else
                {
                    ConversionResult wr = PetEmitter.Emit(walled, walledSheet, loadFlat, "WalledStillSkin");
                    if (!wr.Valid)
                        failures.Add("a skin with wall art and no Move action emitted invalid XML: " + wr.Error);
                    if (wr.Graph == null || wr.Graph.Unreachable.Count != 0)
                        failures.Add("a skin with wall art and no locomotion left unreachable animations: "
                            + (wr.Graph == null ? "(no graph)" : string.Join(",", wr.Graph.Unreachable))
                            + "; the wall region was emitted with nothing able to enter it");
                    if (!wr.Accepted)
                        failures.Add("a skin with wall art and no locomotion was not accepted, so a valid playable "
                            + "pet fails conversion");
                    // Left OUT, not emitted-and-stranded: the same shape the ceiling and `turn` guards take.
                    foreach (string region in new[] { "GrabWall", "ClimbWall", "HangCeiling" })
                        if (FindAnimationNamed(wr, region) != null)
                            failures.Add("'" + region + "' was emitted although nothing on this skin can reach a wall");
                    // And the residue SAYS so, from what was emitted rather than from what the config offered.
                    if (wr.Residue == null || !wr.Residue.Notes.Exists(s => s.IndexOf("nothing can reach a wall", StringComparison.Ordinal) >= 0))
                        failures.Add("the residue does not say the wall region was left out because nothing can reach it");
                    if (wr.Residue != null && wr.Residue.Notes.Exists(s => s.IndexOf("Wall climbing IS converted", StringComparison.Ordinal) >= 0))
                        failures.Add("the residue claims wall climbing IS converted on a pet that carries no wall animation");
                    if (wr.Residue == null || !wr.Residue.Notes.Exists(s => s.IndexOf("has ceiling animations", StringComparison.Ordinal) >= 0
                                                                          && s.IndexOf("wall region is left out", StringComparison.Ordinal) >= 0))
                        failures.Add("the residue does not explain that the ceiling went with the wall");
                    // A Group1 GrabWall that is neither emitted nor merged used to reach no accounting bucket.
                    if (wr.Residue != null && wr.Residue.Notes.Exists(s => s.IndexOf("UNACCOUNTED", StringComparison.Ordinal) >= 0))
                        failures.Add("the residue reports UNACCOUNTED actions for the left-out wall and ceiling art");
                    // A Group2 wall action on the left-out region is LEFT OUT, not "kept but simplified" (R-069). The
                    // fixture's ClimbWall carries the reference conf's mascot.anchor condition, so it is Group2; the
                    // Degraded list used to name it a few lines above the note saying it was left out.
                    ShimejiAction walledClimb = walled.Actions.Find(delegate(ShimejiAction a) { return a.Name == "ClimbWall"; });
                    if (walledClimb == null || walledClimb.Group != FidelityGroup.Group2)
                        failures.Add("WITNESS: the no-locomotion wall fixture's ClimbWall is not Group2 ("
                            + (walledClimb == null ? "missing" : walledClimb.Group.ToString()) + "), so the left-out-vs-degraded case is untested");
                    if (wr.Residue != null && ResidueHas(wr.Residue.Degraded, "ClimbWall"))
                        failures.Add("the left-out Group2 'ClimbWall' is listed under 'Kept but simplified' although its region was "
                            + "not emitted; the same residue then says it was left out");
                    if (wr.Residue != null && !wr.Residue.Notes.Exists(s => s.IndexOf("nothing can reach a wall", StringComparison.Ordinal) >= 0
                                                                          && s.IndexOf("ClimbWall", StringComparison.Ordinal) >= 0))
                        failures.Add("the left-out note does not name 'ClimbWall', so the Group2 wall action is accounted for nowhere");
                }

                // ---- MOUNT PREFIX SETS REPEATFROM ----
                // A climb whose source frames begin with the pet turning onto the wall used to replay that turn
                // on every loop: the emitter wrote repeatfrom="0" on every sequence, and the reach repeat then
                // ran the intro every 2.6 s of climbing (brq51bkr, fixed by hand). The rule that sets it needs two
                // declared facts from the source's own action block, which is why no migration over emitted XML
                // could do it: the block plays ONCE (Type="Animate" here; a bundle's loop=ONESHOT) and its leading
                // poses hold still while the rest travel. MountAndClimb below is that shape: two still frames, three
                // climbing ones. The repeatfrom the node carries must equal the prefix, and the reach solver must
                // have been fed that same value -- a solver still assuming 0 repeats too few frames and lands
                // 1600px short of its own target.
                ShimejiConfig mounted = ShimejiParser.ParseActionsXml(MountPrefixActionsXml);
                SpriteSheet mountedSheet;
                string mountedError;
                if (!SpriteSheetBuilder.Build(
                        Emit.PetEmitter.PosesToComposite(mounted), loadFlat, false, out mountedSheet, out mountedError))
                {
                    failures.Add("mount-prefix fixture failed to composite: " + mountedError);
                }
                else
                {
                    ConversionResult mr = PetEmitter.Emit(mounted, mountedSheet, loadFlat, "MountedSkin");
                    if (!mr.Accepted)
                        failures.Add("the mount-prefix skin was not accepted: unreachable="
                            + (mr.Graph == null ? "(no graph)" : string.Join(",", mr.Graph.Unreachable)) + " " + mr.Error);
                    XmlData.AnimationNode mountAndClimb = FindAnimationNamed(mr, "MountAndClimb");
                    XmlData.AnimationNode loopingClimb = FindAnimationNamed(mr, "Climb");
                    if (mountAndClimb == null || mountAndClimb.Sequence == null || mountAndClimb.Sequence.Frame == null)
                        failures.Add("mount prefix sets repeatfrom: the one-shot climb with a still intro emitted nothing, so the rule is untested");
                    else
                    {
                        const int MountFrames = 2;
                        int mFrames = mountAndClimb.Sequence.Frame.Length;
                        int mRepeat = ParseIntOrZero(mountAndClimb.Sequence.RepeatCount);
                        int mRepeatFrom = mountAndClimb.Sequence.RepeatFromFrame;
                        if (mFrames != 5)
                            failures.Add("mount prefix sets repeatfrom: the one-shot climb emitted " + mFrames + " frames, expected all 5 (the mount is played, then skipped)");
                        if (mRepeatFrom != MountFrames)
                            failures.Add("mount prefix sets repeatfrom: the one-shot climb repeats from frame " + mRepeatFrom
                                + ", expected " + MountFrames + " (its two still frames are the mount), so the pet replays its "
                                + "turn onto the wall on every cycle");
                        // Fed the SAME value: the reach replayed with the node's own repeatfrom must meet the target.
                        int mReach = PetEmitter.SurfaceReachOf(mFrames, mRepeat, mRepeatFrom);
                        if (mReach < PetEmitter.SurfaceReachTargetPx)
                            failures.Add("mount prefix sets repeatfrom: the climb's " + mFrames + " frames repeating from " + mRepeatFrom
                                + " " + mRepeat + " times cover " + mReach + "px of the " + PetEmitter.SurfaceReachTargetPx
                                + " the reach solver targets, so the solver was fed a different repeatfrom than the node carries");
                        if (mRepeat != PetEmitter.SurfaceRepeatForReach(mFrames, mRepeatFrom))
                            failures.Add("mount prefix sets repeatfrom: the emitted repeat " + mRepeat + " is not SurfaceRepeatForReach("
                                + mFrames + ", " + mRepeatFrom + ") = " + PetEmitter.SurfaceRepeatForReach(mFrames, mRepeatFrom));
                        int mVy = ParseIntOrZero(mountAndClimb.Start != null ? mountAndClimb.Start.Y : null);
                        if (mVy >= 0)
                            failures.Add("mount prefix sets repeatfrom: the one-shot climb does not travel upward (y=" + mVy
                                + "); its still intro made it read as a static hold");
                        // WITNESS: the mount frames are the ones emitted first, so the prefix is the intro and not
                        // an offset into the cycle.
                        int mountTile0 = -1, mountTile1 = -1;
                        bool haveMountTile0 = mountedSheet.FrameIndexByKey.TryGetValue(FrameKeyOfPose(mounted, "MountAndClimb", 0) ?? "", out mountTile0);
                        bool haveMountTile1 = mountedSheet.FrameIndexByKey.TryGetValue(FrameKeyOfPose(mounted, "MountAndClimb", 1) ?? "", out mountTile1);
                        if (!haveMountTile0 || !haveMountTile1 || mountAndClimb.Sequence.Frame[0] != mountTile0 || mountAndClimb.Sequence.Frame[1] != mountTile1)
                            failures.Add("WITNESS: the one-shot climb's first two frames are not its two mount poses, so the repeatfrom "
                                + "assertion is not measuring the intro");
                    }
                    // WITNESS: the SAME still-then-climb shape inside a declared LOOP (Type="Move") keeps the whole
                    // block as its cycle. That is the stock ClimbWall rhythm; repeating from inside it would change
                    // twenty-odd shipped pets on a misread.
                    if (loopingClimb == null || loopingClimb.Sequence == null)
                        failures.Add("WITNESS: the looping climb beside the one-shot one emitted nothing");
                    else if (loopingClimb.Sequence.RepeatFromFrame != 0)
                        failures.Add("WITNESS: the looping climb (Type=\"Move\", a still first pose) repeats from frame "
                            + loopingClimb.Sequence.RepeatFromFrame + "; a still opening inside a declared loop is a pause, not a mount");
                }

                // ---- WITNESS: A JUMP INTO A CEILING IS A WAY IN, so the wall stays ----
                // The guard's second term. No floor locomotion here either, but an embedded Jump reaches the
                // ceiling through its only="window-bottom" edge and the ceiling exits onto a wall, so every
                // wall animation IS reachable and must be kept; a guard that cleared the region on "no Move
                // action" alone would fail this. The same fixture pins the ceiling EXIT (F425): the wall art
                // is a static GrabWall, first in source order, and a DescendWall; the synthesis gives GrabWall
                // the climb, and the exit must be the descent -- choosing by the SOURCE velocity picked the
                // static grab that is now the climber, and a ceiling walker was sent back up the wall it had
                // just climbed.
                ShimejiConfig jumper = ShimejiParser.ParseActionsXml(JumpOnlyWallActionsXml);
                SpriteSheet jumperSheet;
                string jumperError;
                if (!SpriteSheetBuilder.Build(
                        Emit.PetEmitter.PosesToComposite(jumper), loadFlat, false, out jumperSheet, out jumperError))
                {
                    failures.Add("jump-only wall fixture failed to composite: " + jumperError);
                }
                else
                {
                    ConversionResult jr = PetEmitter.Emit(jumper, jumperSheet, loadFlat, "JumperSkin");
                    if (!jr.Accepted)
                        failures.Add("a skin whose only way up is a jump was not accepted: unreachable="
                            + (jr.Graph == null ? "(no graph)" : string.Join(",", jr.Graph.Unreachable)) + " " + jr.Error);
                    XmlData.AnimationNode jGrab = FindAnimationNamed(jr, "GrabWall");
                    XmlData.AnimationNode jDescend = FindAnimationNamed(jr, "DescendWall");
                    XmlData.AnimationNode jCeiling = FindAnimationNamed(jr, "HangCeiling");
                    if (jGrab == null || jDescend == null || jCeiling == null)
                        failures.Add("WITNESS: the jump-only skin lost a region animation (GrabWall=" + (jGrab != null)
                            + ", DescendWall=" + (jDescend != null) + ", HangCeiling=" + (jCeiling != null)
                            + "); a jump into the ceiling makes the wall reachable, so the regions must be kept");
                    else
                    {
                        int exitId = BorderTargetOf(jCeiling, "vertical");
                        if (exitId != jDescend.Id)
                            failures.Add("a ceiling walker leaves the ceiling onto '" + NameOfId(jr, exitId)
                                + "' instead of the descending wall pose; leaving onto the synthesised climber sends "
                                + "the pet straight back up into the border it just left");
                    }
                }
            }
            catch (Exception ex) { failures.Add("no-locomotion fixtures threw: " + ex.Message); }
            finally
            {
                foreach (Bitmap b in flat.Values) b.Dispose();
            }

            var sb = new StringBuilder();
            sb.AppendLine("emitter self-test: synthetic skin -> valid, reachable, round-tripping pet");
            if (failures.Count == 0) { sb.Append("  accepted; magic names emitted; residue captured drop + degrade; direction suffixes stripped safely"); detail = sb.ToString(); return true; }
            foreach (string f in failures) sb.AppendLine("  FAIL " + f);
            detail = sb.ToString();
            return false;
        }

        /// <summary>A stub clip the validator's MP3 sniff accepts: an MPEG frame sync (0xFF, 0xFB) followed by
        /// zeroes. Only its LENGTH matters to the budget under test.</summary>
        private static byte[] FakeMp3(int length)
        {
            var bytes = new byte[Math.Max(2, length)];
            bytes[0] = 0xFF;
            bytes[1] = 0xFB;
            return bytes;
        }

        private static int EmbeddedSoundCount(ConversionResult r)
        {
            return r != null && r.Root != null && r.Root.Sounds != null && r.Root.Sounds.Sound != null
                ? r.Root.Sounds.Sound.Length : 0;
        }

        /// <summary>The names of the animations that carry an embedded sound, for a failure message.</summary>
        private static List<string> EmbeddedSoundNames(ConversionResult r)
        {
            var names = new List<string>();
            if (r == null || r.Root == null || r.Root.Sounds == null || r.Root.Sounds.Sound == null) return names;
            foreach (XmlData.SoundNode sn in r.Root.Sounds.Sound)
                if (sn != null) names.Add(NameOfId(r, sn.Id));
            return names;
        }

        private static bool HasAnimationNamed(ConversionResult r, string name)
        {
            if (r.Root == null || r.Root.Animations == null || r.Root.Animations.Animation == null) return false;
            foreach (XmlData.AnimationNode a in r.Root.Animations.Animation)
                if (string.Equals(a.Name, name, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>The Type the SOURCE declared for this action name, or null when the name is not one of
        /// the source's actions (the emitter's own magic fall/drag/kill/sync animations).</summary>
        private static string SourceTypeOf(ShimejiConfig config, string name)
        {
            if (config == null || name == null) return null;
            foreach (ShimejiAction act in config.Actions)
                if (act != null && string.Equals(act.Name, name, StringComparison.Ordinal)) return act.Type;
            return null;
        }

        private static XmlData.AnimationNode FindAnimationNamed(ConversionResult r, string name)
        {
            if (r.Root == null || r.Root.Animations == null || r.Root.Animations.Animation == null) return null;
            foreach (XmlData.AnimationNode a in r.Root.Animations.Animation)
                if (string.Equals(a.Name, name, StringComparison.Ordinal)) return a;
            return null;
        }

        private static XmlData.AnimationNode FindAnimationById(ConversionResult r, int id)
        {
            if (r.Root == null || r.Root.Animations == null || r.Root.Animations.Animation == null) return null;
            foreach (XmlData.AnimationNode a in r.Root.Animations.Animation)
                if (a != null && a.Id == id) return a;
            return null;
        }

        /// <summary>
        /// Steps the engine will interpolate over, the same way <c>AnimationRuntimeLimits.CalculateTotalSteps</c>
        /// derives it: <c>frames + (frames - repeatFrom) * repeat</c>. Read back off the emitted node rather
        /// than asked of the emitter, so a launch solved for the wrong step count is visible here.
        /// </summary>
        private static int DeclaredSteps(XmlData.AnimationNode a)
        {
            if (a == null || a.Sequence == null || a.Sequence.Frame == null || a.Sequence.Frame.Length == 0) return 1;
            int frames = a.Sequence.Frame.Length;
            int repeatFrom = Math.Max(0, Math.Min(frames - 1, a.Sequence.RepeatFromFrame));
            int repeat = ParseIntOrZero(a.Sequence.RepeatCount);
            if (repeat < 0) repeat = 0;
            return Math.Max(1, frames + (frames - repeatFrom) * repeat);
        }

        /// <summary>Total on-screen time of one animation in ms, replaying the engine's per-step interval
        /// interpolation (start -&gt; end across the declared steps). This is the SCREEN time -- what a viewer
        /// experiences -- as opposed to a single pass, which is what the old rest budget confused with it.</summary>
        private static int TotalDwellMs(XmlData.AnimationNode a)
        {
            int steps = DeclaredSteps(a);
            int i0 = ParseIntOrZero(a.Start != null ? a.Start.Interval : null);
            int iN = ParseIntOrZero(a.End != null ? a.End.Interval : null);
            int ip = steps <= 1 ? 1 : steps - 1;
            double total = 0;
            for (int k = 0; k < steps; k++) total += i0 + (double)(iN - i0) * k / ip;
            return (int)Math.Round(total);
        }

        private static List<int> SequenceTargetsOf(XmlData.AnimationNode a)
        {
            var targets = new List<int>();
            if (a == null || a.Sequence == null || a.Sequence.Next == null) return targets;
            foreach (XmlData.NextNode n in a.Sequence.Next)
                if (n != null) targets.Add(n.Value);
            return targets;
        }

        /// <summary>Weight of the animation's only="taskbar" edge back into itself, i.e. how often a landing
        /// becomes another hop. 0 when there is none.</summary>
        private static int LandingSelfWeight(XmlData.AnimationNode a)
        {
            if (a == null || a.Border == null || a.Border.Next == null) return 0;
            foreach (XmlData.NextNode n in a.Border.Next)
                if (n != null && n.Value == a.Id && n.OnlyFlag == "taskbar") return n.Probability;
            return 0;
        }

        private static int ParseIntOrZero(string value)
        {
            int parsed;
            return int.TryParse((value ?? "").Trim(), out parsed) ? parsed : 0;
        }

        /// <summary>Ids the FLOOR hub can select directly: the floor animation whose sequence fans out to the
        /// most others.
        ///
        /// "Floor" is decided by the presence of a &lt;gravity&gt; node, not by fan-out alone. Fan-out on its
        /// own used to be enough, but it silently stops identifying the floor once the wall region has more
        /// than one spoke: in a small fixture a wall animation (which lists its sibling wall poses plus fall)
        /// can out-fan the hub, and the test then reports the hub selecting a wall animation when what it
        /// actually found WAS the wall. Gravity is the right discriminator because omitting it is precisely
        /// what defines a wall or ceiling animation.</summary>
        private static List<int> HubSequenceTargets(ConversionResult r)
        {
            var targets = new List<int>();
            if (r.Root == null || r.Root.Animations == null || r.Root.Animations.Animation == null) return targets;
            XmlData.AnimationNode hub = null;
            foreach (XmlData.AnimationNode a in r.Root.Animations.Animation)
            {
                if (a == null || a.Sequence == null || a.Sequence.Next == null) continue;
                if (a.Gravity == null) continue;   // wall / ceiling / fall, not the floor
                if (hub == null || a.Sequence.Next.Length > hub.Sequence.Next.Length) hub = a;
            }
            if (hub != null)
                foreach (XmlData.NextNode n in hub.Sequence.Next) targets.Add(n.Value);
            return targets;
        }

        /// <summary>The floor hub's id, found the same way <see cref="HubSequenceTargets"/> finds the hub
        /// itself: the falling animation with the most outgoing sequence edges. -1 when there is none.</summary>
        private static int HubId(ConversionResult r)
        {
            if (r.Root == null || r.Root.Animations == null || r.Root.Animations.Animation == null) return -1;
            XmlData.AnimationNode hub = null;
            foreach (XmlData.AnimationNode a in r.Root.Animations.Animation)
            {
                if (a == null || a.Sequence == null || a.Sequence.Next == null) continue;
                if (a.Gravity == null) continue;
                if (hub == null || a.Sequence.Next.Length > hub.Sequence.Next.Length) hub = a;
            }
            return hub == null ? -1 : hub.Id;
        }

        /// <summary>The probability the floor hub's sequence edge to <paramref name="targetId"/> carries, or
        /// -1 when the hub has no such edge. The hub is found exactly as <see cref="HubId"/> finds it.</summary>
        private static int HubEdgeWeightTo(ConversionResult r, int targetId)
        {
            int hubId = HubId(r);
            if (hubId < 0) return -1;
            XmlData.AnimationNode hub = FindAnimationById(r, hubId);
            if (hub == null || hub.Sequence == null || hub.Sequence.Next == null) return -1;
            foreach (XmlData.NextNode n in hub.Sequence.Next)
                if (n != null && n.Value == targetId) return n.Probability;
            return -1;
        }

        /// <summary>Names of every emitted animation starting with <paramref name="prefix"/>, in emission order.</summary>
        private static List<string> NamesStartingWith(ConversionResult r, string prefix)
        {
            var names = new List<string>();
            if (r.Root == null || r.Root.Animations == null || r.Root.Animations.Animation == null) return names;
            foreach (XmlData.AnimationNode a in r.Root.Animations.Animation)
                if (a != null && a.Name != null && a.Name.StartsWith(prefix, StringComparison.Ordinal)) names.Add(a.Name);
            return names;
        }

        /// <summary>The target of an animation's &lt;border&gt; edge carrying the given only-flag, or -1 when it
        /// has none. The first such edge: the emitter writes at most one per situation on the spokes this
        /// is asked about.</summary>
        private static int BorderTargetOf(XmlData.AnimationNode a, string onlyFlag)
        {
            if (a == null || a.Border == null || a.Border.Next == null) return -1;
            foreach (XmlData.NextNode n in a.Border.Next)
                if (n != null && string.Equals(n.OnlyFlag, onlyFlag, StringComparison.Ordinal)) return n.Value;
            return -1;
        }

        /// <summary>The emitted animation's name for an id, or the id itself when nothing carries it, so a
        /// failure names the pose that was chosen rather than a number.</summary>
        private static string NameOfId(ConversionResult r, int id)
        {
            XmlData.AnimationNode a = FindAnimationById(r, id);
            return a != null && a.Name != null ? a.Name : "id " + id;
        }

        /// <summary>The Duration of the first pose of a NAMED variant of an action, or -1 when absent, so the
        /// gaze timing assertion can prove the fixture's variants actually differ.</summary>
        private static int DurationOfVariantPose(ShimejiConfig config, string actionName, int variantIndex)
        {
            foreach (ShimejiAction a in config.Actions)
                if (string.Equals(a.Name, actionName, StringComparison.Ordinal)
                    && variantIndex >= 0 && variantIndex < a.Animations.Count
                    && a.Animations[variantIndex].Poses.Count > 0)
                    return a.Animations[variantIndex].Poses[0].Duration;
            return -1;
        }

        /// <summary>True when some animation has a &lt;border&gt; edge with the given only-flag pointing at the
        /// target id.</summary>
        private static bool HasBorderEdgeTo(ConversionResult r, int targetId, string onlyFlag)
        {
            if (r.Root == null || r.Root.Animations == null || r.Root.Animations.Animation == null) return false;
            foreach (XmlData.AnimationNode a in r.Root.Animations.Animation)
            {
                if (a == null || a.Border == null || a.Border.Next == null) continue;
                foreach (XmlData.NextNode n in a.Border.Next)
                    if (n.Value == targetId && string.Equals(n.OnlyFlag, onlyFlag, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // Every animation carrying a border edge of this only= flag INTO the target. The ceiling test needs
        // the sources, not just "does an edge exist": the property that matters is that nothing except the
        // wall climb can reach it.
        private static List<XmlData.AnimationNode> BorderSourcesOf(ConversionResult r, int targetId, string onlyFlag)
        {
            var sources = new List<XmlData.AnimationNode>();
            if (r.Root == null || r.Root.Animations == null || r.Root.Animations.Animation == null) return sources;
            foreach (XmlData.AnimationNode a in r.Root.Animations.Animation)
            {
                if (a == null || a.Border == null || a.Border.Next == null) continue;
                foreach (XmlData.NextNode n in a.Border.Next)
                    if (n.Value == targetId && string.Equals(n.OnlyFlag, onlyFlag, StringComparison.Ordinal))
                    {
                        sources.Add(a);
                        break;
                    }
            }
            return sources;
        }

        /// <summary>The sheet FrameKey of an action's first pose, or null when the fixture has no such action.
        /// Read AFTER PosesToComposite has run, so the AnchorToTop part of the key is already set.</summary>
        private static string FirstPoseKey(ShimejiConfig config, string actionName)
        {
            foreach (ShimejiAction a in config.Actions)
                if (string.Equals(a.Name, actionName, StringComparison.Ordinal)
                    && a.Animations.Count > 0 && a.Animations[0].Poses.Count > 0)
                    return a.Animations[0].Poses[0].FrameKey;
            return null;
        }

        /// <summary>The sheet FrameKey of the first pose of a NAMED variant of an action, so a test can name
        /// which of a cascade's alternatives it expects rather than trusting the emitter's own pick.</summary>
        /// <summary>The FrameKey of pose <paramref name="poseIndex"/> in an action's first variant, or null.</summary>
        private static string FrameKeyOfPose(ShimejiConfig config, string actionName, int poseIndex)
        {
            foreach (ShimejiAction a in config.Actions)
                if (string.Equals(a.Name, actionName, StringComparison.Ordinal) && a.Animations.Count > 0
                    && a.Animations[0].Poses.Count > poseIndex)
                    return a.Animations[0].Poses[poseIndex].FrameKey;
            return null;
        }

        private static string PoseKeyOfVariant(ShimejiConfig config, string actionName, int variantIndex)
        {
            foreach (ShimejiAction a in config.Actions)
                if (string.Equals(a.Name, actionName, StringComparison.Ordinal)
                    && variantIndex >= 0 && variantIndex < a.Animations.Count
                    && a.Animations[variantIndex].Poses.Count > 0)
                    return a.Animations[variantIndex].Poses[0].FrameKey ?? "";
            // "" rather than null: the callers hand this straight to a Dictionary lookup, which throws on a
            // null key, and a missing fixture variant should fail an assertion rather than the whole test host.
            return "";
        }

        /// <summary>The recorded reason for a residue entry, or "" when it is absent. Separate from
        /// <see cref="ResidueHas"/> because "it is listed" and "it is described honestly" are two claims.</summary>
        private static string ResidueDetailOf(List<ResidueItem> items, string name)
        {
            if (items == null) return "";
            foreach (ResidueItem i in items)
                if (string.Equals(i.Name, name, StringComparison.Ordinal)) return i.Detail ?? "";
            return "";
        }

        /// <summary>True when a tile has ANY sprite pixel. A tile that is entirely the transparency key
        /// renders as an invisible pet, which no other check in the pipeline can see.</summary>
        private static bool TileIsPainted(SpriteSheet sheet, int index)
        {
            if (sheet == null || index < 0) return false;
            int col = index % sheet.TilesX;
            int row = index / sheet.TilesX;
            using (var ms = new System.IO.MemoryStream(sheet.PngBytes, false))
            using (var bmp = new Bitmap(ms))
            {
                int x0 = col * sheet.CellWidth;
                int y0 = row * sheet.CellHeight;
                // Every 2nd pixel: enough to catch a fully blank tile without scanning the whole sheet once
                // per frame reference.
                for (int y = y0; y < y0 + sheet.CellHeight && y < bmp.Height; y += 2)
                    for (int x = x0; x < x0 + sheet.CellWidth && x < bmp.Width; x += 2)
                    {
                        Color c = bmp.GetPixel(x, y);
                        if (c.A != 0 && !(c.R == 255 && c.G == 0 && c.B == 255)) return true;
                    }
                return false;
            }
        }

        /// <summary>True when the given row WITHIN this frame's tile has sprite pixels on it, i.e. anything
        /// other than the magenta key the compositor clears the background to.</summary>
        private static bool TileRowIsPainted(SpriteSheet sheet, string frameKey, int rowInCell)
        {
            int index;
            if (sheet == null || !sheet.FrameIndexByKey.TryGetValue(frameKey, out index)) return false;
            if (rowInCell < 0 || rowInCell >= sheet.CellHeight) return false;
            int col = index % sheet.TilesX;
            int row = index / sheet.TilesX;
            using (var ms = new System.IO.MemoryStream(sheet.PngBytes, false))
            using (var bmp = new Bitmap(ms))
            {
                int y = row * sheet.CellHeight + rowInCell;
                if (y >= bmp.Height) return false;
                int x0 = col * sheet.CellWidth;
                for (int x = x0; x < x0 + sheet.CellWidth && x < bmp.Width; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    if (!(c.R == 255 && c.G == 0 && c.B == 255)) return true;
                }
                return false;
            }
        }

        private static bool ResidueHas(List<ResidueItem> items, string name)
        {
            foreach (ResidueItem i in items)
                if (string.Equals(i.Name, name, StringComparison.Ordinal)) return true;
            return false;
        }

        // True if the decoded sheet has at least one fully-transparent pixel -- the signature of the
        // alpha path (empty cell area kept transparent) versus the magenta path (everything opaque).
        private static bool HasFullyTransparentPixel(byte[] png)
        {
            if (png == null || png.Length == 0) return false;
            using (var ms = new System.IO.MemoryStream(png, false))
            using (var bmp = new Bitmap(ms))
            {
                int stepY = Math.Max(1, bmp.Height / 32);
                int stepX = Math.Max(1, bmp.Width / 32);
                for (int y = 0; y < bmp.Height; y += stepY)
                    for (int x = 0; x < bmp.Width; x += stepX)
                        if (bmp.GetPixel(x, y).A == 0) return true;
                return false;
            }
        }

        private static int EvalOnFakeScreen(string expr, int imageW, int imageH, int random, int randS,
                                            HashSet<string> requested)
        {
            return DesktopAICompanion.SafeExpression.Evaluate(expr, delegate(string name)
            {
                if (requested != null) requested.Add(name);
                switch (name)
                {
                    case "screenW": return 1920;
                    case "screenH": return 1080;
                    case "areaW": return 1920;
                    case "areaH": return 1040;
                    case "imageW": return imageW;
                    case "imageH": return imageH;
                    case "imageX": return -1;
                    case "imageY": return -1;
                    // The caller sweeps these across the engine's ranges (Xml.cs draws random in 0..99 and
                    // randS in 10..89); a single midpoint draw hid an off-screen spawn at the extremes (F448).
                    case "random": return random;
                    case "randS": return randS;
                    case "scale": return 1;
                    default: throw new System.FormatException("unexpected variable in a spawn expression: " + name);
                }
            });
        }

        private static Bitmap Solid(int w, int h, Color c)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) { g.CompositingMode = CompositingMode.SourceCopy; g.Clear(c); }
            return bmp;
        }

        /// <summary>Three actions, NONE of them Type="Move": a hand-trimmed skin with no locomotion at
        /// all. Deliberately minimal, because the point is the ABSENCE of a Move action.</summary>
        private const string NoLocomotionActionsXml =
@"<?xml version=""1.0"" encoding=""UTF-8"" ?>
<Mascot xmlns=""http://www.group-finity.com/Mascot"">
  <ActionList>
    <Action Name=""Stand"" Type=""Stay"" BorderType=""Floor"">
      <Animation><Pose Image=""/n1.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <Action Name=""Wave"" Type=""Animate"" BorderType=""Floor"">
      <Animation>
        <Pose Image=""/n1.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""8"" />
        <Pose Image=""/n2.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""8"" />
      </Animation>
    </Action>
    <Action Name=""Falling"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Fall"" Gravity=""2"">
      <Animation><Pose Image=""/n2.png"" ImageAnchor=""20,60"" Velocity=""0,2"" Duration=""4"" /></Animation>
    </Action>
  </ActionList>
</Mascot>";

        /// <summary>The no-locomotion skin with WALL and CEILING art added: a static grab, a climb that is
        /// Type="Move" on the wall (not floor locomotion), and a ceiling hang. Still no Type="Move" floor
        /// action, and no jump, so nothing can reach the wall region and it must be left out (F429).</summary>
        private const string NoLocomotionWallActionsXml =
@"<?xml version=""1.0"" encoding=""UTF-8"" ?>
<Mascot xmlns=""http://www.group-finity.com/Mascot"">
  <ActionList>
    <Action Name=""Stand"" Type=""Stay"" BorderType=""Floor"">
      <Animation><Pose Image=""/n1.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <Action Name=""Wave"" Type=""Animate"" BorderType=""Floor"">
      <Animation>
        <Pose Image=""/n1.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""8"" />
        <Pose Image=""/n2.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""8"" />
      </Animation>
    </Action>
    <Action Name=""GrabWall"" Type=""Stay"" BorderType=""Wall"">
      <Animation><Pose Image=""/n3.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""6"" /></Animation>
    </Action>
    <!-- The reference conf's ClimbWall shape: a Condition on mascot.anchor makes it Group2, which is what the
         left-out accounting case (R-069) needs. -->
    <Action Name=""ClimbWall"" Type=""Move"" BorderType=""Wall"">
      <Animation Condition=""#{mascot.anchor.y &gt; 100}"">
        <Pose Image=""/n3.png"" ImageAnchor=""20,60"" Velocity=""0,-2"" Duration=""4"" />
        <Pose Image=""/n4.png"" ImageAnchor=""20,60"" Velocity=""0,-2"" Duration=""4"" />
      </Animation>
    </Action>
    <Action Name=""HangCeiling"" Type=""Move"" BorderType=""Ceiling"">
      <Animation>
        <Pose Image=""/n4.png"" ImageAnchor=""20,60"" Velocity=""2,0"" Duration=""4"" />
        <Pose Image=""/n3.png"" ImageAnchor=""20,60"" Velocity=""2,0"" Duration=""4"" />
      </Animation>
    </Action>
    <Action Name=""Falling"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Fall"" Gravity=""2"">
      <Animation><Pose Image=""/n2.png"" ImageAnchor=""20,60"" Velocity=""0,2"" Duration=""4"" /></Animation>
    </Action>
  </ActionList>
</Mascot>";

        /// <summary>A walker (so the wall is reachable) and two wall climbs over the same idea in the two shapes
        /// the mount-prefix rule has to tell apart. MountAndClimb is Type="Animate": the author says it plays
        /// through once, and its first two poses hold still while the last three climb -- a mount, then the
        /// cycle. Climb is Type="Move" with the stock still-first-pose rhythm: a declared loop, no mount. Distinct
        /// art per action, so the direction collapse cannot merge them.</summary>
        private const string MountPrefixActionsXml =
@"<?xml version=""1.0"" encoding=""UTF-8"" ?>
<Mascot xmlns=""http://www.group-finity.com/Mascot"">
  <ActionList>
    <Action Name=""Stand"" Type=""Stay"" BorderType=""Floor"">
      <Animation><Pose Image=""/n1.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <Action Name=""Walk"" Type=""Move"" BorderType=""Floor"">
      <Animation>
        <Pose Image=""/n1.png"" ImageAnchor=""20,60"" Velocity=""-2,0"" Duration=""6"" />
        <Pose Image=""/n2.png"" ImageAnchor=""20,60"" Velocity=""-2,0"" Duration=""6"" />
      </Animation>
    </Action>
    <Action Name=""MountAndClimb"" Type=""Animate"" BorderType=""Wall"">
      <Animation>
        <Pose Image=""/n3.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""6"" />
        <Pose Image=""/n4.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""6"" />
        <Pose Image=""/n5.png"" ImageAnchor=""20,60"" Velocity=""0,-2"" Duration=""4"" />
        <Pose Image=""/n6.png"" ImageAnchor=""20,60"" Velocity=""0,-2"" Duration=""4"" />
        <Pose Image=""/n3.png"" ImageAnchor=""20,60"" Velocity=""0,-2"" Duration=""4"" />
      </Animation>
    </Action>
    <Action Name=""Climb"" Type=""Move"" BorderType=""Wall"">
      <Animation>
        <Pose Image=""/n5.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""16"" />
        <Pose Image=""/n6.png"" ImageAnchor=""20,60"" Velocity=""0,-2"" Duration=""4"" />
      </Animation>
    </Action>
    <Action Name=""Falling"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Fall"" Gravity=""2"">
      <Animation><Pose Image=""/n2.png"" ImageAnchor=""20,60"" Velocity=""0,2"" Duration=""4"" /></Animation>
    </Action>
  </ActionList>
</Mascot>";

        /// <summary>No floor locomotion, but an embedded JUMP plus wall and ceiling art: the jump reaches the
        /// ceiling at a window's underside and the ceiling exits onto a wall, so the regions ARE reachable and
        /// must be kept. The wall art is a static GrabWall FIRST and a DescendWall second, on purpose: the
        /// synthesis gives the grab the climb, and the ceiling exit must still be the descent (F425).</summary>
        private const string JumpOnlyWallActionsXml =
@"<?xml version=""1.0"" encoding=""UTF-8"" ?>
<Mascot xmlns=""http://www.group-finity.com/Mascot"">
  <ActionList>
    <Action Name=""Stand"" Type=""Stay"" BorderType=""Floor"">
      <Animation><Pose Image=""/n1.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <Action Name=""HopEmbedded"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Jump""
            VelocityParam=""14"" BorderType=""Floor"">
      <Animation>
        <Pose Image=""/n1.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""4"" />
        <Pose Image=""/n2.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""4"" />
      </Animation>
    </Action>
    <Action Name=""GrabWall"" Type=""Stay"" BorderType=""Wall"">
      <Animation><Pose Image=""/n3.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""6"" /></Animation>
    </Action>
    <Action Name=""DescendWall"" Type=""Move"" BorderType=""Wall"">
      <Animation>
        <Pose Image=""/n4.png"" ImageAnchor=""20,60"" Velocity=""0,2"" Duration=""4"" />
        <Pose Image=""/n3.png"" ImageAnchor=""20,60"" Velocity=""0,2"" Duration=""4"" />
      </Animation>
    </Action>
    <Action Name=""HangCeiling"" Type=""Move"" BorderType=""Ceiling"">
      <Animation>
        <Pose Image=""/n4.png"" ImageAnchor=""20,60"" Velocity=""2,0"" Duration=""4"" />
        <Pose Image=""/n3.png"" ImageAnchor=""20,60"" Velocity=""2,0"" Duration=""4"" />
      </Animation>
    </Action>
    <Action Name=""Falling"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Fall"" Gravity=""2"">
      <Animation><Pose Image=""/n2.png"" ImageAnchor=""20,60"" Velocity=""0,2"" Duration=""4"" /></Animation>
    </Action>
  </ActionList>
</Mascot>";

        private const string SyntheticActionsXml =
@"<?xml version=""1.0"" encoding=""UTF-8"" ?>
<Mascot xmlns=""http://www.group-finity.com/Mascot"">
  <ActionList>
    <Action Name=""Stand"" Type=""Stay"" BorderType=""Floor"">
      <Animation><Pose Image=""/s.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" Sound=""/beep.wav"" /></Animation>
    </Action>
    <!-- SOUNDED, and a member of two set-pieces below, so its clip is embedded once per animation that
         plays it (Walk, GatorRide_1_Walk, StrollAndHop_1_Walk): the shape that made per-clip budgeting
         understate what the document carries (F426). -->
    <Action Name=""Walk"" Type=""Move"" BorderType=""Floor"">
      <Animation>
        <Pose Image=""/w1.png"" ImageAnchor=""20,60"" Velocity=""-2,0"" Duration=""${5+Math.random()*5}"" Sound=""/step.wav"" />
        <Pose Image=""/w2.png"" ImageAnchor=""20,60"" Velocity=""-2,0"" Duration=""6"" />
      </Animation>
    </Action>
    <!-- A PERFORMANCE THAT TRAVELS, which is the stock conf's Tripping in miniature: Type=""Animate"", so the
         author says ""play this through"", but it moves the pet along the ground exactly like the Walk above.
         The emitter used to classify locomotion by velocity alone and so could not tell these two apart; it
         handed this one the walk's ""65% do it again"" self-edge and the pet stumbled 2.9 times in a row.
         Reported from a real desktop 2026-09-22 on 24 of the 31 converted pets that ship.
         Walk and Stumble must come out DIFFERENT, and the assertions check both halves, because a fix that
         stops the stumble looping by stopping everything looping would break the walk and pass a one-sided
         test. -->
    <Action Name=""Stumble"" Type=""Animate"" BorderType=""Floor"">
      <Animation>
        <Pose Image=""/w1.png"" ImageAnchor=""20,60"" Velocity=""-8,0"" Duration=""8"" />
        <Pose Image=""/w2.png"" ImageAnchor=""20,60"" Velocity=""-4,0"" Duration=""4"" />
      </Animation>
    </Action>
    <!-- A performance that does NOT travel, which is Hornet's Bouncing exactly: Type=""Animate"", two
         poses, zero velocity. Reported from a real desktop 2026-09-22 as a two-frame juggle, AFTER the
         trip was fixed and separately from it. It never had a self-edge to remove; what it had was a
         9-12s idle dwell, because the `restsplit` migration decided ""performance"" by velocity instead
         of by declared Type and stretched two frames over eleven seconds.
         Its presence is what stops the assertion below being a one-case test. Stumble travels and Bounce
         does not, so ""every Animate plays once"" is checked across both, and a fix that only handles the
         moving kind cannot pass. -->
    <Action Name=""Bounce"" Type=""Animate"" BorderType=""Floor"">
      <Animation>
        <Pose Image=""/j1.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""4"" />
        <Pose Image=""/j2.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""4"" />
      </Animation>
    </Action>
    <!-- The OTHER half of the same predicate: Type=""Move"" but it never actually moves. Travel is declared
         intent AND real motion, and until this action existed the velocity half of the test was dead weight
         that no fixture could exercise. Deleting it from the emitter left the suite green, which is how a
         guard nobody can fail gets shipped. -->
    <Action Name=""Brace"" Type=""Move"" BorderType=""Floor"">
      <Animation>
        <Pose Image=""/w2.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""6"" />
        <Pose Image=""/w1.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""6"" />
      </Animation>
    </Action>
    <!-- A MULTI-frame rest whose first frame bakes in a long hold (75 ticks = 3000ms), exactly like Hornet's
         Stand. It is the case that made converted pets sluggish: the source interval is a dwell, not pacing,
         and taking it literally held the pose 3s+ per pass. The emitter must cap the per-frame interval and
         still reach the short rest dwell. A single-frame rest (Stand, above) exercises the other path. -->
    <Action Name=""Lounge"" Type=""Stay"" BorderType=""Floor"">
      <Animation>
        <Pose Image=""/s.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""75"" />
        <Pose Image=""/w1.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""5"" />
      </Animation>
    </Action>
    <Action Name=""Falling"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Fall"" Gravity=""2"">
      <Animation><Pose Image=""/f.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <Action Name=""Pinched"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Dragged"">
      <Animation><Pose Image=""/p.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""5"" /></Animation>
    </Action>
    <!-- A GAZE, in the shape the corpus actually ships: a cascade over cursor height whose first variant is
         'pointer near the top of the screen' and whose last carries no Condition at all. The emitter must take
         the LAST one, because taking the first pins the pet permanently craning upward. -->
    <Action Name=""SitAndLookAtMouse"" Type=""Stay"" BorderType=""Floor"">
      <Animation Condition=""#{mascot.environment.cursor.y &lt; 100}""><Pose Image=""/m.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" /></Animation>
      <Animation Condition=""#{mascot.environment.cursor.y &lt; 300}""><Pose Image=""/m2.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" /></Animation>
      <Animation><Pose Image=""/mn.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <!-- Deliberately drawn with the gaze's neutral image: Ralsei's gaze fallback IS his sit pose, so this pair
         is frame-identical and velocity-identical and the direction collapse would merge them, taking whichever
         came first and half the time throwing away the faceCursor tag. -->
    <Action Name=""Doze"" Type=""Stay"" BorderType=""Floor"">
      <Animation><Pose Image=""/mn.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <!-- A SECOND gaze, drawn with images no other action uses. It exists because the first one cannot test
         whether gaze poses reach the sprite sheet: Doze shares its neutral image, so the tile is composited
         either way and dropping the gaze from PosesToComposite left every assertion green. This one has no
         such cover, so if gaze poses stop being composited its frames vanish and it emits nothing. -->
    <!-- Its two variants DIFFER in Duration (2 ticks against 10) and only the conditional one carries a
         Sound: the emitted rest must take its interval, its dwell arithmetic and its clip from the catch-all
         it actually plays, not from Animations[0], which is the variant the frames already do not come
         from (F428). Two frames in the catch-all, so the multi-frame rest path is the one exercised. -->
    <Action Name=""StandAndWatchMouse"" Type=""Stay"" BorderType=""Floor"">
      <Animation Condition=""#{mascot.environment.cursor.y &lt; 100}""><Pose Image=""/g1.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""2"" Sound=""/look.wav"" /></Animation>
      <Animation>
        <Pose Image=""/g2.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""10"" />
        <Pose Image=""/g1.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""10"" />
      </Animation>
    </Action>
    <!-- NAMED for the cursor, but with nothing cursor-shaped about it: no condition, no expression, no
         reference to mascot.environment. It must convert as an ordinary floor rest. The gaze test is
         whether an action READS cursor state, and the action's own display name is not evidence of that.
         Capital C on purpose: that is the character the two Has() helpers disagreed about. -->
    <Action Name=""RestNearCursor"" Type=""Stay"" BorderType=""Floor"">
      <Animation><Pose Image=""/nc.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <!-- And one whose SPRITE FILE is named after the cursor while nothing else is: alan becker's Victim skin
         ships exactly this (CursorHate over /cursorsetup01.png, a Stay with no Condition). The classifier's
         blob used to hold every attribute value, so this was Group2 and a gaze on the strength of a filename;
         it must convert as a plain Group1 rest (F441). -->
    <Action Name=""CursorHate"" Type=""Stay"" BorderType=""Floor"">
      <Animation><Pose Image=""/cursorsetup01.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <Action Name=""ThrowIe"" Type=""Embedded"" Class=""com.group_finity.mascot.action.ThrowIE"" InitialVX=""32"">
      <Animation><Pose Image=""/t.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""40"" /></Animation>
    </Action>
    <!-- A JUMP with a deliberately violent launch and NO descent of its own. The corpus really does contain
         launches this hard (shipc2 at -40), and a converted pet handed that on the open floor would leave the
         screen. The emitter must clamp the launch and force a descent, so the assertions below check the
         EMITTED arc rather than the source's numbers. -->
    <Action Name=""BigJump"" Type=""Move"" BorderType=""Floor"">
      <Animation>
        <Pose Image=""/j1.png"" ImageAnchor=""20,60"" Velocity=""4,-40"" Duration=""4"" />
        <Pose Image=""/j2.png"" ImageAnchor=""20,60"" Velocity=""4,-30"" Duration=""4"" />
      </Animation>
    </Action>
    <!-- A launcher too WEAK to be a jump, which the corpus supplies twice (Hornet's Grapple1 and 1l2yvz73's
         `fly`, both at -5). Passing the rise through gave an arc that spent most of its sequence descending: an
         8-16px twitch that read as a broken jump. It must convert, keep its sprites, and play FLAT, so this
         action is the negative case for every jump assertion below, and the only one that tells a converter
         which flattens a weak rise apart from one which treats every rise as a jump. -->
    <Action Name=""Hover"" Type=""Move"" BorderType=""Floor"">
      <Animation>
        <Pose Image=""/h1.png"" ImageAnchor=""20,60"" Velocity=""-3,-5"" Duration=""6"" />
        <Pose Image=""/h2.png"" ImageAnchor=""20,60"" Velocity=""-3,-5"" Duration=""6"" />
      </Animation>
    </Action>
    <!-- The two jump SHAPES the corpus actually broke on, and the reason BigJump alone proved nothing: with 2
         poses at 4 ticks the locomotion budget happens to pick the same 14 steps the solved arc wants, so the
         old pass-through code passes every height assertion on it by luck.

         PullUp is PullUpShimeji2 / Launching / Lay an Egg2 (16 animations across 14 pets): 3 poses at ONE tick,
         which the loco budget repeated to 21 steps and turned a -15 launch into a 72px fling.
         HopUp is jump_up_left / jumping (14 animations across 14 pets): a single pose whose 7 steps left a
         -8 launch rising 11px, a twitch. It also carries Grapple4's violent HORIZONTAL velocity, so it is the
         fixture that makes the span cap reachable: unbounded, 100px per tick over a proper 14-step arc crosses
         1400px and the pet meets a screen edge before it meets the ground.
         Both must come out at the same height as BigJump. -->
    <Action Name=""PullUp"" Type=""Move"" BorderType=""Floor"">
      <Animation>
        <Pose Image=""/u1.png"" ImageAnchor=""20,60"" Velocity=""0,-15"" Duration=""1"" />
        <Pose Image=""/u2.png"" ImageAnchor=""20,60"" Velocity=""0,-15"" Duration=""1"" />
        <Pose Image=""/u3.png"" ImageAnchor=""20,60"" Velocity=""0,-15"" Duration=""1"" />
      </Animation>
    </Action>
    <Action Name=""HopUp"" Type=""Move"" BorderType=""Floor"">
      <Animation>
        <Pose Image=""/hu.png"" ImageAnchor=""20,60"" Velocity=""-100,-8"" Duration=""2"" />
      </Animation>
    </Action>
    <!-- A jump with MORE authored frames than the arc's step budget, which is the one case a fixed launch
         velocity cannot serve: the repeat count can pad a short sequence up to the budget but it cannot cut a
         long one down, so the launch has to be solved for the steps the sequence actually declares. At 24
         steps a flat -15 rises 82px. Two images are reused across the 24 poses on purpose, so this costs the
         sheet two tiles rather than 24, because poses sharing an Image and anchor share a frame.

         Its first and last Duration also differ by 50x (80ms -> 4000ms, Grapple4's exact ramp), so it is the
         fixture that makes the flat-interval assertion reachable too: every other jump here is already flat. -->
    <Action Name=""LongLeap"" Type=""Move"" BorderType=""Floor"">
      <Animation>
        <Pose Image=""/l1.png"" ImageAnchor=""20,60"" Velocity=""2,-20"" Duration=""2"" />
        <Pose Image=""/l2.png"" ImageAnchor=""20,60"" Velocity=""2,-18"" Duration=""2"" />
        <Pose Image=""/l1.png"" ImageAnchor=""20,60"" Velocity=""2,-16"" Duration=""2"" />
        <Pose Image=""/l2.png"" ImageAnchor=""20,60"" Velocity=""2,-14"" Duration=""2"" />
        <Pose Image=""/l1.png"" ImageAnchor=""20,60"" Velocity=""2,-12"" Duration=""2"" />
        <Pose Image=""/l2.png"" ImageAnchor=""20,60"" Velocity=""2,-10"" Duration=""2"" />
        <Pose Image=""/l1.png"" ImageAnchor=""20,60"" Velocity=""2,-8"" Duration=""2"" />
        <Pose Image=""/l2.png"" ImageAnchor=""20,60"" Velocity=""2,-6"" Duration=""2"" />
        <Pose Image=""/l1.png"" ImageAnchor=""20,60"" Velocity=""2,-4"" Duration=""2"" />
        <Pose Image=""/l2.png"" ImageAnchor=""20,60"" Velocity=""2,-2"" Duration=""2"" />
        <Pose Image=""/l1.png"" ImageAnchor=""20,60"" Velocity=""2,0"" Duration=""2"" />
        <Pose Image=""/l2.png"" ImageAnchor=""20,60"" Velocity=""2,2"" Duration=""2"" />
        <Pose Image=""/l1.png"" ImageAnchor=""20,60"" Velocity=""2,4"" Duration=""2"" />
        <Pose Image=""/l2.png"" ImageAnchor=""20,60"" Velocity=""2,6"" Duration=""2"" />
        <Pose Image=""/l1.png"" ImageAnchor=""20,60"" Velocity=""2,8"" Duration=""2"" />
        <Pose Image=""/l2.png"" ImageAnchor=""20,60"" Velocity=""2,10"" Duration=""2"" />
        <Pose Image=""/l1.png"" ImageAnchor=""20,60"" Velocity=""2,12"" Duration=""2"" />
        <Pose Image=""/l2.png"" ImageAnchor=""20,60"" Velocity=""2,14"" Duration=""2"" />
        <Pose Image=""/l1.png"" ImageAnchor=""20,60"" Velocity=""2,16"" Duration=""2"" />
        <Pose Image=""/l2.png"" ImageAnchor=""20,60"" Velocity=""2,18"" Duration=""2"" />
        <Pose Image=""/l1.png"" ImageAnchor=""20,60"" Velocity=""2,20"" Duration=""2"" />
        <Pose Image=""/l2.png"" ImageAnchor=""20,60"" Velocity=""2,20"" Duration=""2"" />
        <Pose Image=""/l1.png"" ImageAnchor=""20,60"" Velocity=""2,20"" Duration=""2"" />
        <Pose Image=""/l2.png"" ImageAnchor=""20,60"" Velocity=""2,20"" Duration=""100"" />
      </Animation>
    </Action>
    <!-- Wall region. The Condition makes this Group2 ON PURPOSE: the reference conf's ClimbWall is Group2 for
         exactly this reason, and a Group1-only wall filter silently produced a pet that grabs a wall and hangs
         there motionless. Negative Velocity y is the climb, and the anchor matches the floor poses. -->
    <!-- The velocity and the duration both RAMP, which is the shape the corpus actually ships: Hornet's climb
         goes 0 to -2 at 640 down to 160ms, and the ramp is why it averaged 1px per step and crawled at 2.5px/s.
         A flat fixture cannot exercise the constant-speed assertions at all, which mutation testing reported as
         two silent guards. -->
    <Action Name=""ClimbWall"" Type=""Move"" BorderType=""Wall"">
      <Animation Condition=""#{mascot.anchor.y &gt; 100}"">
        <Pose Image=""/c1.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""16"" />
        <Pose Image=""/c2.png"" ImageAnchor=""20,60"" Velocity=""0,-2"" Duration=""4"" />
      </Animation>
    </Action>
    <!-- A DESCENDING wall pose, so the ceiling has somewhere to hand back to. Without one the ceiling exit
         would fall back to the climb and send the pet straight back into the border it just left. -->
    <Action Name=""DescendWall"" Type=""Move"" BorderType=""Wall"">
      <Animation>
        <Pose Image=""/c2.png"" ImageAnchor=""20,60"" Velocity=""0,2"" Duration=""4"" />
        <Pose Image=""/c1.png"" ImageAnchor=""20,60"" Velocity=""0,2"" Duration=""4"" />
      </Animation>
    </Action>
    <!-- A STATIC wall grab: velocity 0, so it holds rather than travels. It is the negative case for the reach
         budget, and the only thing that separates crossing a surface in one sequence from giving EVERY wall
         pose a four-thousand-pixel sequence, which on a hold would pin the pet to the wall for a minute doing
         nothing. The self-test reported the split untested until this existed. -->
    <Action Name=""GrabWall"" Type=""Stay"" BorderType=""Wall"">
      <Animation>
        <Pose Image=""/c1.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""6"" />
      </Animation>
    </Action>
    <!-- Ceiling region. The anchor is deliberately 20,24 rather than the floor's 20,60, mirroring the
         reference conf's 64,48-vs-64,128: for a hanging mascot the contact point is near the TOP of the
         sprite. That difference is the whole reason ceiling poses need AnchorToTop compositing. -->
    <Action Name=""ClimbCeiling"" Type=""Move"" BorderType=""Ceiling"">
      <Animation>
        <Pose Image=""/k1.png"" ImageAnchor=""20,24"" Velocity=""-2,0"" Duration=""4"" />
        <Pose Image=""/k2.png"" ImageAnchor=""20,24"" Velocity=""-2,0"" Duration=""4"" />
      </Animation>
    </Action>
    <!-- A BOTTOM-anchored ceiling pose, which is what every Android bundle produces: the bundle format
         anchors every pose bottom-centre, so the anchor carries no ceiling meaning. Skipping AnchorY source
         rows here skipped the entire sprite and emitted a blank tile. That shipped in 1.9.4 and was only
         caught by eye on Kopo, because the fixture had only top-anchored ceiling poses. -->
    <Action Name=""HangCeiling"" Type=""Move"" BorderType=""Ceiling"">
      <Animation>
        <Pose Image=""/k3.png"" ImageAnchor=""20,60"" Velocity=""2,0"" Duration=""4"" />
        <Pose Image=""/k4.png"" ImageAnchor=""20,60"" Velocity=""2,0"" Duration=""4"" />
      </Animation>
    </Action>
    <!-- A SET-PIECE, in the shape the corpus actually ships it: Capybara's gator ride in miniature. The two
         legs carry BorderType=""None"", which is what makes this worth testing rather than a formality:
         IsFloorAction rejects them, so they are never hub spokes, their sprites reach the sheet only because
         SetPieceMemberNames puts them there, and the only way the pet can ever play them is the chain. Walk
         is a member AND an ordinary spoke, which is the case that catches a chain stealing the walk.
         RunOff travels left and ReturnOn travels back right, so the run REVERSES DIRECTION: that is exactly
         what animations.xsd cannot express as one animation (one <start>, one <end>, interpolated across
         every frame) and it is why a chain exists at all rather than a concatenation. -->
    <!-- THE CANONICAL SHIMEJI JUMP, which is not Type=""Move"". base-conf/actions.xml declares Jumping as
         Type=""Embedded"" Class=""...action.Jump"": the launch lives in VelocityParam and every pose reads
         Velocity=""0,0"", so IsLocomotion is false for it. Every jump fixture above is Type=""Move"", which
         is why the border assertions below passed for years while 27 jump-shaped animations across the
         shipped pets were emitted with no <border> element at all. -->
    <Action Name=""HopEmbedded"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Jump""
            VelocityParam=""14"" BorderType=""Floor"">
      <Animation>
        <Pose Image=""/e1.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""4"" />
        <Pose Image=""/e2.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""4"" />
      </Animation>
    </Action>
    <!-- An action this converter REFUSES rather than cannot do. KinitoPET ships three of these and one
         points at a file host. A companion installed from a skin catalogue does not get to open links, so
         the residue report has to SAY it was refused: before this bucket existed the three reached none of
         the accounting categories and were printed as UNACCOUNTED, which was the report admitting it could
         not say what became of them. -->
    <Action Name=""OpenSomething"" Type=""OpenURL"" URL=""https://example.invalid/"" />
    <Action Name=""RunOff"" Type=""Move"" BorderType=""None"">
      <Animation>
        <Pose Image=""/m.png"" ImageAnchor=""20,60"" Velocity=""-6,0"" Duration=""6"" />
        <Pose Image=""/m2.png"" ImageAnchor=""20,60"" Velocity=""-6,0"" Duration=""6"" />
      </Animation>
    </Action>
    <Action Name=""ReturnOn"" Type=""Move"" BorderType=""None"">
      <Animation>
        <Pose Image=""/m2.png"" ImageAnchor=""20,60"" Velocity=""6,0"" Duration=""6"" />
        <Pose Image=""/mn.png"" ImageAnchor=""20,60"" Velocity=""6,0"" Duration=""6"" />
      </Animation>
    </Action>
    <Action Name=""GatorRide"" Type=""Sequence"">
      <ActionReference Name=""Walk"" />
      <ActionReference Name=""RunOff"" />
      <ActionReference Name=""ReturnOn"" />
    </Action>
    <!-- TWO MORE SEQUENCES WHOSE MEMBERS ARE ALL ORDINARY FLOOR SPOKES. Walk and Bounce are hub spokes
         already, so neither run RECOVERS anything and the only way into ExpandSetPieces is PLAYED: a
         <Behavior Frequency> naming the run, which the test sets on the config where a skin's behaviors.xml
         would. StrollAndHop gets one and must be chained; StrollAndHopUnplayed is the same run without one
         and must NOT be. GatorRide above always enters through RECOVERS, so until these two existed the
         PLAYED term could be deleted with the suite green (F447). Distinct art per member (/w1,/w2 vs
         /j1,/j2), so CollapseDirectionPairs cannot merge the steps and quietly untest this. -->
    <Action Name=""StrollAndHop"" Type=""Sequence"">
      <ActionReference Name=""Walk"" />
      <ActionReference Name=""Bounce"" />
    </Action>
    <Action Name=""StrollAndHopUnplayed"" Type=""Sequence"">
      <ActionReference Name=""Walk"" />
      <ActionReference Name=""Bounce"" />
    </Action>
    <!-- THE MIRROR OF Walk: the same two frames with the x-velocity negated, which is how a fan skin writes
         walk_left / walk_right over one set of art. CollapseDirectionPairs merges it into Walk, so it is
         converted, under Walk's name. StrollBack names it and nothing plays StrollBack; a RECOVERS gate that
         read the post-collapse spoke list saw no WalkBack there and chained the run anyway (F431). -->
    <Action Name=""WalkBack"" Type=""Move"" BorderType=""Floor"">
      <Animation>
        <Pose Image=""/w1.png"" ImageAnchor=""20,60"" Velocity=""2,0"" Duration=""6"" />
        <Pose Image=""/w2.png"" ImageAnchor=""20,60"" Velocity=""2,0"" Duration=""6"" />
      </Animation>
    </Action>
    <Action Name=""StrollBack"" Type=""Sequence"">
      <ActionReference Name=""WalkBack"" />
      <ActionReference Name=""Bounce"" />
    </Action>
    <!-- THE SAME RUN, PLAYED: the test gives StrollBackPlayed a <Behavior Frequency>, so it IS chained, and
         its first step names the collapsed WalkBack. That step must be built from Walk's poses (the survivor,
         leftward over the unmirrored art), not WalkBack's own rightward ones, or the pet moonwalks through the
         run (N-tools-01). -->
    <Action Name=""StrollBackPlayed"" Type=""Sequence"">
      <ActionReference Name=""WalkBack"" />
      <ActionReference Name=""Bounce"" />
    </Action>
  </ActionList>
</Mascot>";
    }
}
