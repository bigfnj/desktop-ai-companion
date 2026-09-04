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
            };

            try
            {
                ShimejiConfig config = ShimejiParser.ParseActionsXml(SyntheticActionsXml);

                Func<string, Bitmap> load = delegate(string name) { return new Bitmap(owned[name]); };

                SpriteSheet sheet;
                string error;
                if (!SpriteSheetBuilder.Build(Emit.PetEmitter.PosesToComposite(config), load, false, out sheet, out error))
                {
                    detail = "emitter self-test: compositing failed -- " + error;
                    return false;
                }

                ConversionResult r = PetEmitter.Emit(config, sheet, load, "TestSkin");

                if (!r.Valid) failures.Add("emitted XML failed the validator: " + r.Error);
                if (!r.RoundTrips) failures.Add("emitted XML did not round-trip: " + r.Error);
                if (r.Graph == null || r.Graph.Unreachable.Count != 0)
                    failures.Add("emitted pet has unreachable animations: " + (r.Graph == null ? "(no graph)" : string.Join(",", r.Graph.Unreachable)));
                if (!r.Accepted) failures.Add("result not accepted (valid+roundtrip+reachable)");

                // Guard the invisible-pet bug: a spawn that places the pet fully off-screen horizontally and
                // routes to a stationary animation leaves it invisible. Evaluate each spawn's X against a
                // fake 1920-wide screen and require the pet to land within the horizontal bounds. (Y may be
                // above the top on purpose -- that spawn falls in.)
                if (r.Root != null && r.Root.Spawns != null && r.Root.Spawns.Spawn != null)
                {
                    foreach (XmlData.SpawnNode sp in r.Root.Spawns.Spawn)
                    {
                        int x = EvalOnFakeScreen(sp.X, sheet.CellWidth, sheet.CellHeight);
                        if (x < 0 || x > 1920 - sheet.CellWidth)
                            failures.Add("spawn " + sp.Id + " lands the pet off-screen horizontally (x=" + x + " of 1920)");
                    }
                }

                if (!HasAnimationNamed(r, "fall")) failures.Add("no 'fall' magic animation emitted");
                if (!HasAnimationNamed(r, "drag")) failures.Add("no 'drag' magic animation emitted");
                if (!HasAnimationNamed(r, "kill")) failures.Add("no 'kill' magic animation emitted");
                if (!HasAnimationNamed(r, "sync")) failures.Add("no 'sync' magic animation emitted");

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

                    // Reachable, and reachable ONLY from the wall. This is the assertion that keeps the
                    // top-border ambiguity harmless: if a FLOOR animation ever gained an only="horizontal"
                    // edge, the pet could snap to the ceiling from ground level.
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
                    int reach = PetEmitter.SurfaceReachOf(climbFrames, climbRepeat);
                    if (reach < 2000)
                        failures.Add("one climb pass covers only " + reach + "px, so the pet rolls the "
                            + "let-go dice before it can reach the top of a screen");

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
                        if (dy <= 0)
                            failures.Add("the descending wall pose does not travel DOWN (y=" + dy
                                + "); the reach budget must preserve direction, not turn every descent into a climb");
                        if (PetEmitter.SurfaceReachOf(dFrames, dRepeat) < 2000)
                            failures.Add("the descending wall pose covers only "
                                + PetEmitter.SurfaceReachOf(dFrames, dRepeat) + "px, so climbing DOWN rolls the "
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
                        if (PetEmitter.SurfaceReachOf(grabFrames, grabRepeat) >= 2000)
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
                    if (PetEmitter.SurfaceReachOf(frames, rep) < 2000)
                        failures.Add("one ceiling pass covers only " + PetEmitter.SurfaceReachOf(frames, rep)
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

                    // The fixture launches at -40. Anything steeper than the clamp means an unbounded launch
                    // reached the output, and the pet leaves the screen.
                    if (launch >= 0)
                        failures.Add("jump does not launch upward (start y=" + launch + ")");
                    if (launch < -15)
                        failures.Add("jump launch was NOT clamped (start y=" + launch + ", source asked for -40)");

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

                // The gaze whose art nothing else uses. Its only route into the sheet is the gaze arm of
                // PosesToComposite, so this is the assertion that fails when gaze poses stop being composited.
                XmlData.AnimationNode lonelyGaze = FindAnimationNamed(r, "StandAndWatchMouse");
                if (lonelyGaze == null)
                    failures.Add("a gaze with no shared art emitted nothing, so gaze poses are not reaching the sprite sheet");
                else if (lonelyGaze.Sequence == null || !string.Equals(lonelyGaze.Sequence.Action, "faceCursor", StringComparison.Ordinal))
                    failures.Add("the second gaze carries no faceCursor action");

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

            var sb = new StringBuilder();
            sb.AppendLine("emitter self-test: synthetic skin -> valid, reachable, round-tripping pet");
            if (failures.Count == 0) { sb.Append("  accepted; magic names emitted; residue captured drop + degrade; direction suffixes stripped safely"); detail = sb.ToString(); return true; }
            foreach (string f in failures) sb.AppendLine("  FAIL " + f);
            detail = sb.ToString();
            return false;
        }

        private static bool HasAnimationNamed(ConversionResult r, string name)
        {
            if (r.Root == null || r.Root.Animations == null || r.Root.Animations.Animation == null) return false;
            foreach (XmlData.AnimationNode a in r.Root.Animations.Animation)
                if (string.Equals(a.Name, name, StringComparison.Ordinal)) return true;
            return false;
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

        private static int EvalOnFakeScreen(string expr, int imageW, int imageH)
        {
            return DesktopAICompanion.SafeExpression.Evaluate(expr, delegate(string name)
            {
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
                    case "random": return 50;
                    case "randS": return 50;
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

        private const string SyntheticActionsXml =
@"<?xml version=""1.0"" encoding=""UTF-8"" ?>
<Mascot xmlns=""http://www.group-finity.com/Mascot"">
  <ActionList>
    <Action Name=""Stand"" Type=""Stay"" BorderType=""Floor"">
      <Animation><Pose Image=""/s.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" Sound=""/beep.wav"" /></Animation>
    </Action>
    <Action Name=""Walk"" Type=""Move"" BorderType=""Floor"">
      <Animation>
        <Pose Image=""/w1.png"" ImageAnchor=""20,60"" Velocity=""-2,0"" Duration=""${5+Math.random()*5}"" />
        <Pose Image=""/w2.png"" ImageAnchor=""20,60"" Velocity=""-2,0"" Duration=""6"" />
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
    <Action Name=""StandAndWatchMouse"" Type=""Stay"" BorderType=""Floor"">
      <Animation Condition=""#{mascot.environment.cursor.y &lt; 100}""><Pose Image=""/g1.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" /></Animation>
      <Animation><Pose Image=""/g2.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" /></Animation>
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
  </ActionList>
</Mascot>";
    }
}
