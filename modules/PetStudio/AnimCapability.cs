using System;
using System.Collections.Generic;
using System.Text;
using DesktopAICompanion.Tools.ShimejiConvert;   // PetGraph.ReservedEntryPointNames, the one reserved-name array

namespace DesktopAICompanion.PetStudioModule
{
    /// <summary>What an animation actually does, as opposed to what it is called.</summary>
    internal enum AnimCapability
    {
        /// <summary>Plays in place. The commonest kind by far, and deliberately unbadged so the map stays
        /// quiet and the interesting animations stand out.</summary>
        Idle,
        /// <summary>Rises off the ground under its own velocity.</summary>
        Jump,
        /// <summary>Drops under its own velocity while holding nothing: a descent at either end that is not a
        /// surface pose, with or without a &lt;gravity&gt; node and with or without sideways drift. The `fall`
        /// the runtime binds is <see cref="Engine"/>, not this; the coloured sheep's `fall fast`, `fall_die`
        /// and `king_fall*`, which the pet chooses, are this. Until 1.1.18 the vocabulary had no drop, so they
        /// read "Plays in place" while falling (N-petstudio-02).</summary>
        Fall,
        /// <summary>No gravity and it moves: climbing a wall, or traversing a ceiling.</summary>
        Climb,
        /// <summary>No gravity and it holds still: gripping a wall or hanging from a ceiling.</summary>
        Cling,
        /// <summary>Travels horizontally along the ground.</summary>
        Move,
        /// <summary>Aimed at the pointer when it starts (the faceCursor sequence action).</summary>
        Gaze,
        /// <summary>An animation the ENGINE starts itself: the runtime's fall / drag / kill / sync (bound by
        /// exact name, or for fall and drag by the fallback when no such name exists -- a lone "Falling", else
        /// the lowest id, which on a pet declaring neither is whatever comes first), or a flip turn by its
        /// action. The pet may reach it by an edge as well; the badge says the engine will, whatever the pet
        /// chooses, which is why it wins over the velocities.</summary>
        Engine,
    }

    /// <summary>
    /// Derives what each animation does from the physics already in its XML.
    ///
    /// The reachability map showed a name and a reachability colour and nothing else, which meant finding the
    /// jump in a converted pet required knowing that a Hollow Knight skin calls it "Grapple4". Names belong to
    /// the source skin and span five languages (`jump_up_left`, `jumping`, `PullUpShimeji2`, `Launching`,
    /// `Lay an Egg2`, `引っこ抜く2` are all jumps), so a naming convention was never going to answer it. The
    /// physics does, and the map was already parsing it.
    ///
    /// Pure, so the whole table can be asserted without a pet on screen.
    /// </summary>
    internal static class AnimCapabilities
    {
        // The four reserved names (PetGraph.ReservedEntryPointNames, the one array since F432) are not matched
        // here at all since 1.1.18: the analyzer reads WHICH animations the runtime bound as its entries off the
        // staged Animations and marks them AnimNode.IsEngineEntry, and that flag is what ENGINE means (RA-128).
        // The self-check below still iterates the array, so a fifth entry point is asserted the day it exists.

        /// <summary>Which surface a pose holds. A wall admits travel up or down and nothing sideways; a ceiling
        /// admits travel sideways and nothing up or down. A pose that travels across its surface's normal is
        /// leaving it, whatever edge put it there.</summary>
        private enum Surface { Wall, Ceiling }

        /// <summary>
        /// The <c>only=</c> values that mean "the companion has arrived on a surface it must hold onto", and
        /// which surface. The engine fires VERTICAL at the left and right edges of the work area and
        /// HORIZONTAL at its top (FormCompanion), so the names describe the EDGE that was hit, not the motion.
        ///
        /// This, and NOT the absence of a &lt;gravity&gt; element, is what identifies a wall or ceiling pose.
        /// Omitted gravity is how the CONVERTER expresses a cling, but it is not a general rule and reading it
        /// as one was wrong: the bundled hand-authored pet has 4 gravity elements across 54 animations, so the
        /// gravity test labelled 41 of its ordinary floor animations as wall poses. That pet marks its wall
        /// entries the other way, by only="vertical" border edges into the wall climb (and into the bounce
        /// OFF the wall, which the axis test in AlongSurface rejects), and it reaches its ceiling from that
        /// climb through a flip turn rather than by any flag, which is why SurfacePoses passes through turns.
        ///
        /// Excluded on purpose: "taskbar" and "horizontal+" are the FLOOR, and "window" / "window-top" mean
        /// standing on a title bar, which is standing rather than clinging.
        /// </summary>
        private static bool TryKindOf(string only, out Surface kind)
        {
            switch (only ?? "")
            {
                case "vertical":                           // a left/right screen edge
                case "window-left":                        // a window's side
                case "window-right":
                    kind = Surface.Wall; return true;
                case "horizontal":                         // the TOP of the screen ("horizontal+" is the floor)
                case "window-bottom":                      // a window's underside
                    kind = Surface.Ceiling; return true;
                default:
                    kind = Surface.Wall; return false;
            }
        }

        /// <summary>
        /// Classify every animation at once, because the answer is not a property of one node.
        ///
        /// A jump and a wall climb are indistinguishable by velocity -- both rise, and in converted pets both
        /// omit gravity -- so the tiebreak has to be how the pet GETS there, which only the graph knows.
        /// </summary>
        internal static Dictionary<int, AnimCapability> ClassifyAll(IList<AnimNode> nodes)
        {
            var result = new Dictionary<int, AnimCapability>();
            if (nodes == null) return result;
            HashSet<int> surfaces = SurfacePoses(nodes);
            foreach (AnimNode node in nodes)
                if (node != null) result[node.Id] = Of(node, surfaces.Contains(node.Id));
            return result;
        }

        /// <summary>
        /// Animations the pet can only be in while holding a wall or ceiling.
        ///
        /// Seeded from the border edges that PUT it there, then grown one relation at a time through the
        /// animations those chain to. The growth is needed and is not speculative: a ceiling walk is reached
        /// from the ceiling GRAB, never from a border, so seeding alone misses it. Every pose in the set is
        /// known to be on a WALL or a CEILING, and three rules bound the growth:
        ///
        ///   * the target must be able to hold -- no gravity, and not one of the engine's own names, without
        ///     which `fall` (which every wall pose exits to) would drag the whole floor in behind it -- and it
        ///     must travel ALONG its surface or hold still: a wall pose travels only up or down, a ceiling pose
        ///     only sideways. That keeps the bounce off a wall (x only, though reached by only="vertical") and
        ///     the drop from a ceiling (y only) out of the set (F150);
        ///   * a gravity-less &lt;action&gt;flip&lt;/action&gt; is the engine turning the pet round where it
        ///     stands, so the growth passes THROUGH it -- its exits are read as leaving the same surface --
        ///     without labelling it, and it stays ENGINE. Excluding turns as engine-owned also made them
        ///     opaque, and the bundled sheep's whole ceiling chain sits behind two of them (F150);
        ///   * a BORDER edge out of a surface pose fires when its travel meets an edge, and which edge depends
        ///     on the direction: down a wall meets the floor (a landing; the set stops there), up a wall meets
        ///     the ceiling, along a ceiling meets a wall, so the kind flips on the way through.
        /// </summary>
        private static HashSet<int> SurfacePoses(IList<AnimNode> nodes)
        {
            var byId = new Dictionary<int, AnimNode>();
            foreach (AnimNode n in nodes)
                if (n != null) byId[n.Id] = n;

            var surfaces = new HashSet<int>();
            var pending = new Queue<KeyValuePair<int, Surface>>();
            // The (id, kind) pairs already queued, so a cycle cannot spin and a pose reached as both kinds
            // is expanded once per kind.
            var queued = new HashSet<long>();

            foreach (AnimNode n in nodes)
            {
                if (n == null) continue;
                foreach (AnimEdge e in n.Edges)
                {
                    Surface kind;
                    if (e == null || e.Kind != "border" || e.Probability <= 0 || !TryKindOf(e.Only, out kind)) continue;
                    Offer(byId, e.To, kind, surfaces, pending, queued);
                }
            }

            while (pending.Count > 0)
            {
                KeyValuePair<int, Surface> current = pending.Dequeue();
                AnimNode from;
                if (!byId.TryGetValue(current.Key, out from)) continue;
                foreach (AnimEdge e in from.Edges)
                {
                    if (e == null || e.Probability <= 0) continue;
                    Surface next = current.Value;
                    if (e.Kind == "border")
                    {
                        if (Descends(from)) continue;   // the edge below a descent is the floor
                        if (Moves(from)) next = current.Value == Surface.Wall ? Surface.Ceiling : Surface.Wall;
                    }
                    Offer(byId, e.To, next, surfaces, pending, queued);
                }
            }
            return surfaces;
        }

        /// <summary>One candidate for the surface set, reached as <paramref name="kind"/>: a turn is passed
        /// through unlabelled, a pose that can hold and travels along the surface joins, and anything else
        /// ends the growth on this edge.</summary>
        private static void Offer(Dictionary<int, AnimNode> byId, int id, Surface kind, HashSet<int> surfaces,
            Queue<KeyValuePair<int, Surface>> pending, HashSet<long> queued)
        {
            AnimNode target;
            if (!byId.TryGetValue(id, out target)) return;
            long key = ((long)id << 1) | (kind == Surface.Ceiling ? 1L : 0L);
            if (IsTurn(target))
            {
                if (queued.Add(key)) pending.Enqueue(new KeyValuePair<int, Surface>(id, kind));
                return;
            }
            if (!Holdable(target) || !AlongSurface(target, kind)) return;
            surfaces.Add(id);
            if (queued.Add(key)) pending.Enqueue(new KeyValuePair<int, Surface>(id, kind));
        }

        /// <summary>A gravity-less flip: the engine turns the pet round where it stands, on whatever it is
        /// holding. A flip WITH gravity is a turn on the floor and is not one of these.</summary>
        private static bool IsTurn(AnimNode node)
        {
            return !node.HasGravity && string.Equals(node.Action, "flip", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Travels downward at either end: a wall descent, or a drop.</summary>
        private static bool Descends(AnimNode node)
        {
            return node.StartY > 0 || node.EndY > 0;
        }

        /// <summary>Holding still, or travelling purely along the surface: up or down a wall (x stays 0),
        /// sideways along a ceiling (y stays 0). A component across the surface's normal is a departure.</summary>
        private static bool AlongSurface(AnimNode node, Surface kind)
        {
            if (!Moves(node)) return true;
            bool horizontal = node.StartX != 0 || node.EndX != 0;
            bool vertical = node.StartY != 0 || node.EndY != 0;
            return kind == Surface.Wall ? vertical && !horizontal : horizontal && !vertical;
        }

        /// <summary>Could the pet be holding a surface in this animation? It must not be able to fall, and it
        /// must not be one the engine drives itself.</summary>
        private static bool Holdable(AnimNode node)
        {
            return node != null && !node.HasGravity && !IsEngineOwned(node);
        }

        /// <summary>
        /// The engine's own: an animation the runtime bound as its fall, drag, kill or sync
        /// (<see cref="AnimNode.IsEngineEntry"/>, read off the staged Animations by the analyzer), or a flip turn
        /// by its ACTION.
        ///
        /// Until 1.1.18 this matched the four reserved NAMES case-insensitively, which disagreed with the host in
        /// both directions: the loader binds the exact name only (Xml.cs's switch, no case folding, the LAST
        /// duplicate wins) and the runtime then falls back for fall and drag alone, so a hand-authored 'Kill' or
        /// 'Sync' -- never bound, chosen by the pet like any other animation -- was badged ENGINE and ended the
        /// surface growth at it, while a lone 'Falling' the runtime does bind as the fall was not (RA-128). The
        /// binding is the host's own answer, taken from the same loader the host runs, and is not re-derived
        /// here: a copy of the three-step fallback is the drift the source-link exists to prevent (F155).
        /// </summary>
        private static bool IsEngineOwned(AnimNode node)
        {
            if (node.IsEngineEntry) return true;
            // `turn` is identified by its ACTION, not its name: the converter renames it on a collision, so a
            // pet can legitimately carry "turn2".
            return string.Equals(node.Action, "flip", StringComparison.OrdinalIgnoreCase);
        }

        internal static AnimCapability Of(AnimNode node, bool isSurfacePose)
        {
            if (node == null) return AnimCapability.Idle;

            // The engine's own first: whatever their velocities say, the pet does not choose these.
            if (IsEngineOwned(node)) return AnimCapability.Engine;

            if (isSurfacePose)
                return Moves(node) ? AnimCapability.Climb : AnimCapability.Cling;

            // Rising, and not holding anything: that is a jump. The signal that was impossible to see.
            if (node.StartY < 0 || node.EndY < 0) return AnimCapability.Jump;

            // Descending at either end, holding nothing: a fall, whatever its <gravity> node says and whether or
            // not it drifts sideways -- the mirror of the jump rule above, which likewise reads a rise before it
            // reads the horizontal travel. Until 1.1.18 there was no such label, so the coloured sheep's `fall
            // fast`, `fall_die` and `king_fall*`, the converted `fall_` (10px per frame, WITH a gravity node) and
            // every diagonal descent read "Plays in place" or "travels along the ground" while dropping
            // (N-petstudio-02). Measured over the 55 shipped pets before shipping this line: every label that
            // changes is a descent; the per-pet diff is in DESIGN-REGISTER.md under `#### burn/petstudio`.
            if (Descends(node)) return AnimCapability.Fall;

            if (string.Equals(node.Action, "faceCursor", StringComparison.OrdinalIgnoreCase))
                return AnimCapability.Gaze;

            if (node.StartX != 0 || node.EndX != 0) return AnimCapability.Move;
            return AnimCapability.Idle;
        }

        private static bool Moves(AnimNode node)
        {
            return node.StartX != 0 || node.EndX != 0 || node.StartY != 0 || node.EndY != 0;
        }

        /// <summary>The short tag the map paints on a chip, or "" for the unbadged common case.</summary>
        internal static string Badge(AnimCapability capability)
        {
            switch (capability)
            {
                case AnimCapability.Jump: return "JUMP";
                case AnimCapability.Fall: return "FALL";
                case AnimCapability.Climb: return "CLIMB";
                case AnimCapability.Cling: return "CLING";
                case AnimCapability.Move: return "MOVE";
                case AnimCapability.Gaze: return "GAZE";
                case AnimCapability.Engine: return "ENGINE";
                default: return "";
            }
        }

        /// <summary>
        /// The sentence the detail panel shows: what it does, plus the facts a chip has no room for. Written
        /// from the same node the badge came from, so the two can never disagree.
        /// </summary>
        internal static string Describe(AnimNode node, AnimCapability capability)
        {
            if (node == null) return "";
            var sb = new StringBuilder();
            switch (capability)
            {
                case AnimCapability.Jump:
                    sb.Append("JUMPS — leaves the ground (launch y=").Append(node.StartY)
                      .Append(", descent y=").Append(node.EndY).Append(")");
                    break;
                case AnimCapability.Fall:
                    // Both components, as the MOVE sentence learned to: the descent is what earns the badge,
                    // and a drift it does not mention would read as a badge contradicting the velocities.
                    sb.Append("FALLS — drops ").Append(Math.Max(node.StartY, node.EndY))
                      .Append("px per frame while holding nothing");
                    int drift = Math.Max(Math.Abs(node.StartX), Math.Abs(node.EndX));
                    if (drift > 0) sb.Append(", drifting ").Append(drift).Append("px sideways");
                    break;
                case AnimCapability.Climb:
                    sb.Append("CLIMBS — no gravity, so it holds a surface, and it travels along it");
                    break;
                case AnimCapability.Cling:
                    sb.Append("CLINGS — no gravity, so it grips a wall or hangs from a ceiling without moving");
                    break;
                case AnimCapability.Move:
                    // Whichever end declares the travel. The classification above admits StartX OR EndX,
                    // and reading only StartX made blue_sheep's fall_wind -- which declares its travel on
                    // EndX alone -- render as "travels 0px per frame", i.e. the badge said MOVES and the
                    // sentence beside it said it did not.
                    sb.Append("MOVES — travels ")
                      .Append(Math.Max(Math.Abs(node.StartX), Math.Abs(node.EndX)))
                      .Append("px per frame along the ground");
                    break;
                case AnimCapability.Gaze:
                    sb.Append("GAZES — held in place, aimed at the pointer as it starts");
                    break;
                case AnimCapability.Engine:
                    sb.Append("ENGINE — the host starts this one itself: it is the companion's fall, drag, kill or sync (bound by name, or by fallback when none is declared), or a flip turn");
                    break;
                default:
                    sb.Append("Plays in place");
                    break;
            }
            if (node.VelocityIsExpression)
                sb.Append(". Its velocity is an EXPRESSION, so this reading is approximate");

            bool lands = false, underside = false, climbs = false;
            foreach (AnimEdge e in node.Edges)
            {
                if (e == null || e.Kind != "border") continue;
                if (e.Only == "taskbar") lands = true;
                if (e.Only == "window-bottom") underside = true;
                if (e.Only == "vertical") climbs = true;
            }
            if (lands) sb.Append(". Has a landing (only=\"taskbar\")");
            if (underside) sb.Append(". Can catch a window's underside");
            if (climbs) sb.Append(". Can grab a wall at a screen edge");
            return sb.Append('.').ToString();
        }

        /// <summary>
        /// A count per capability for the map's legend, over a classification the caller has ALREADY computed.
        /// Ordered so the interesting ones read first.
        ///
        /// RenderMap classifies the whole pet so each chip's badge can read from the result, then calls
        /// RenderCensus, which until 1.1.10 classified the whole pet again -- twice per analyze, on a ~750 ms
        /// debounce while the author is typing. Both callers pass their map, so the classify-it-yourself
        /// fallback (`null` meant "run ClassifyAll here") and the one-argument overload that fed it had no
        /// caller and are gone (RA-129); a null classification counts nothing rather than classifying.
        /// </summary>
        internal static List<KeyValuePair<AnimCapability, int>> Census(
            IList<AnimNode> nodes, Dictionary<int, AnimCapability> classified)
        {
            var counts = new Dictionary<AnimCapability, int>();
            if (nodes != null && classified != null)
                foreach (AnimNode n in nodes)
                {
                    AnimCapability c;
                    if (n == null || !classified.TryGetValue(n.Id, out c)) continue;
                    int existing;
                    counts.TryGetValue(c, out existing);
                    counts[c] = existing + 1;
                }
            var order = new[]
            {
                AnimCapability.Jump, AnimCapability.Fall, AnimCapability.Climb, AnimCapability.Cling,
                AnimCapability.Move, AnimCapability.Gaze, AnimCapability.Idle, AnimCapability.Engine,
            };
            var result = new List<KeyValuePair<AnimCapability, int>>();
            foreach (AnimCapability c in order)
            {
                int n;
                if (counts.TryGetValue(c, out n) && n > 0)
                    result.Add(new KeyValuePair<AnimCapability, int>(c, n));
            }
            return result;
        }
    }

    /// <summary>
    /// Assertions for <see cref="AnimCapabilities"/>, driven by --petstudio-selftest through reflection for
    /// the same reason <see cref="BehaviourChainSelfCheck"/> is: the host cannot reference the module's types,
    /// and reflecting far enough to build an AnimNode from outside would test the reflection.
    ///
    /// Named RunChecks, not SelfTest, so it cannot beat a module's own --module-selftest entry point.
    /// </summary>
    internal static class AnimCapabilitySelfCheck
    {
        internal static bool RunChecks(string fixturePetXml, out string detail)
        {
            var sb = new StringBuilder();
            bool ok = true;
            try
            {
                // Velocity alone, with nothing holding the pet.
                ok &= Check(sb, "rising while holding nothing is a JUMP",
                    AnimCapabilities.Of(Node(startY: -14, endY: 20, gravity: false), false) == AnimCapability.Jump);
                ok &= Check(sb, "gravity plus horizontal velocity is MOVE",
                    AnimCapabilities.Of(Node(startX: -2, gravity: true), false) == AnimCapability.Move);
                ok &= Check(sb, "gravity and no velocity is Idle, and Idle carries no badge",
                    AnimCapabilities.Of(Node(gravity: true), false) == AnimCapability.Idle &&
                    AnimCapabilities.Badge(AnimCapability.Idle) == "");
                ok &= Check(sb, "faceCursor is a GAZE",
                    AnimCapabilities.Of(Node(gravity: true, action: "faceCursor"), false) == AnimCapability.Gaze);

                // Holding a surface overrides the velocity, and is what tells a wall climb from a jump: both
                // rise, and in a converted pet both omit gravity.
                ok &= Check(sb, "rising WHILE holding a surface is a CLIMB, not a jump",
                    AnimCapabilities.Of(Node(startY: 0, endY: -2, gravity: false), true) == AnimCapability.Climb);
                ok &= Check(sb, "holding a surface without moving is a CLING",
                    AnimCapabilities.Of(Node(gravity: false), true) == AnimCapability.Cling);
                ok &= Check(sb, "holding a surface and travelling sideways is a CLIMB (a ceiling walk)",
                    AnimCapabilities.Of(Node(endX: -2, gravity: false), true) == AnimCapability.Climb);

                // The engine's entries win over everything: `fall` has a downward velocity and no gravity, and
                // must not be reported as something the pet chose to do. ENGINE is the runtime's BINDING
                // (AnimNode.IsEngineEntry, RA-128), so the name on each node here is documentation; the four are
                // iterated from the one array (F432) rather than spelled by hand (RA-139), and the WITNESS below
                // pins the array's contents so a name dropped from it fails here instead of going unasserted.
                foreach (string magic in PetGraph.ReservedEntryPointNames)
                    ok &= Check(sb, "'" + magic + "', bound by the runtime, is ENGINE whatever its velocities or surface say",
                        AnimCapabilities.Of(Node(name: magic, startY: 10, endY: 10, gravity: false, entry: true), true) == AnimCapability.Engine);
                string[] reserved = (string[])PetGraph.ReservedEntryPointNames.Clone();
                Array.Sort(reserved, StringComparer.Ordinal);
                ok &= Check(sb, "WITNESS the one reserved-name array still names drag, fall, kill and sync, so the loop above asserted four entries",
                    string.Join(",", reserved) == "drag,fall,kill,sync");
                // ...and `turn` by its ACTION, because the converter renames it on a collision.
                ok &= Check(sb, "a flipping animation is ENGINE even when it is not called 'turn'",
                    AnimCapabilities.Of(Node(name: "turn2", gravity: false, action: "flip"), true) == AnimCapability.Engine);

                // RA-128: the badge follows the runtime's binding, not the spelling. The host binds the exact
                // lowercase name and has no fallback for kill or sync, so a hand-authored 'Kill' is an ordinary
                // animation the pet chooses; until 1.1.18 a case-insensitive name match badged it ENGINE and
                // Holdable refused it, which ended a wall chain at it.
                ok &= Check(sb, "a hand-authored 'Kill' the runtime did not bind is not ENGINE",
                    AnimCapabilities.Of(Node(name: "Kill", gravity: false), false) != AnimCapability.Engine);
                var strayKill = Node(name: "Kill", endY: -2, gravity: false);
                strayKill.Id = 2;
                var boundKill = Node(name: "kill", gravity: false, entry: true);
                boundKill.Id = 3;
                var wallRunner = Node(name: "Run", startX: -4, gravity: true, edges: EdgeTo(2, "border", "vertical"));
                wallRunner.Id = 1;
                Dictionary<int, AnimCapability> spelled =
                    AnimCapabilities.ClassifyAll(new List<AnimNode> { wallRunner, strayKill, boundKill });
                ok &= Check(sb, "a hand-authored 'Kill' the runtime did not bind, reached by only=\"vertical\", is a CLIMB like any other wall pose (RA-128)",
                    spelled[2] == AnimCapability.Climb);
                ok &= Check(sb, "WITNESS the exact 'kill' the runtime DID bind, in the same graph, stays ENGINE",
                    spelled[3] == AnimCapability.Engine);

                // N-petstudio-02: a descent is a FALL, the mirror of the jump rule. Whether it carries a gravity
                // node (the converted `fall_` does, at 10px per frame) or drifts sideways (a diagonal descent
                // read "travels along the ground") is beside the point: it is dropping, and until 1.1.18 the
                // vocabulary could only say "Plays in place" or MOVE about it.
                ok &= Check(sb, "a gravity-less drop holding nothing is a FALL, not 'plays in place'",
                    AnimCapabilities.Of(Node(startY: 8, endY: 12, gravity: false), false) == AnimCapability.Fall);
                ok &= Check(sb, "a drop WITH a gravity node is a FALL too (the converted `fall_` drops 10px per frame)",
                    AnimCapabilities.Of(Node(startY: 10, endY: 10, gravity: true), false) == AnimCapability.Fall);
                ok &= Check(sb, "a descent that also drifts sideways is a FALL, not a MOVE along the ground",
                    AnimCapabilities.Of(Node(startX: -10, startY: 8, endX: -10, endY: 8, gravity: true), false) == AnimCapability.Fall);
                ok &= Check(sb, "WITNESS a rise at either end still wins: an arc that launches and then descends is a JUMP",
                    AnimCapabilities.Of(Node(startY: -14, endY: 20, gravity: false), false) == AnimCapability.Jump);
                ok &= Check(sb, "WITNESS a landing pose that has stopped moving is still Idle, not a FALL",
                    AnimCapabilities.Of(Node(name: "fall soft", gravity: false), false) == AnimCapability.Idle);
                string falling = AnimCapabilities.Describe(Node(startX: -10, startY: 8, endX: -10, endY: 8, gravity: true), AnimCapability.Fall);
                ok &= Check(sb, "a FALL describes its drop and its drift (" + falling + ")",
                    falling.IndexOf("FALLS", StringComparison.Ordinal) >= 0 &&
                    falling.IndexOf("8px per frame", StringComparison.Ordinal) >= 0 &&
                    falling.IndexOf("drifting 10px", StringComparison.Ordinal) >= 0);

                ok &= Check(sb, "an expression velocity is flagged in the description, not silently read as 0",
                    AnimCapabilities.Describe(Node(gravity: true, expression: true), AnimCapability.Idle)
                        .IndexOf("EXPRESSION", StringComparison.Ordinal) >= 0);
                ok &= Check(sb, "a null node is Idle rather than a throw",
                    AnimCapabilities.Of(null, false) == AnimCapability.Idle);

                // The graph half: a surface pose is found by the border edge that PUTS the pet there, and the
                // absence of a <gravity> element is NOT enough on its own. Reading it as enough labelled 41 of
                // the bundled pet's 54 ordinary floor animations as wall poses.
                var wall = Node(name: "ClimbWall", endY: -2, gravity: false);
                wall.Id = 2;
                var walk = Node(name: "Walk", startX: -2, gravity: false);   // no gravity, but a FLOOR animation
                walk.Id = 3;
                var loco = Node(name: "Run", startX: -4, gravity: true, edges: EdgeTo(2, "border", "vertical"));
                loco.Id = 1;
                Dictionary<int, AnimCapability> graph = AnimCapabilities.ClassifyAll(new List<AnimNode> { loco, wall, walk });
                ok &= Check(sb, "an animation reached by only=\"vertical\" is a surface pose",
                    graph[2] == AnimCapability.Climb);
                ok &= Check(sb, "a gravity-less FLOOR animation nothing puts on a surface is not a cling",
                    graph[3] == AnimCapability.Move);
                ok &= Check(sb, "the animation that offers the wall edge is itself still MOVE",
                    graph[1] == AnimCapability.Move);

                // The GROWTH step, which seeding alone cannot cover: a ceiling walk is reached from the
                // ceiling GRAB, never from a border, so without propagation it comes back as ordinary
                // horizontal motion and the pet appears to walk along the floor while on the ceiling.
                var walker = Node(name: "ClimbCeiling", endX: -2, gravity: false);
                walker.Id = 3;
                var grab = Node(name: "GrabCeiling", gravity: false, edges: EdgeTo(3, "sequence", "none"));
                grab.Id = 2;
                var climber = Node(name: "ClimbWall", endY: -2, gravity: false, edges: EdgeTo(2, "border", "horizontal"));
                climber.Id = 1;
                var entry = Node(name: "Walk", startX: -2, gravity: true, edges: EdgeTo(1, "border", "vertical"));
                entry.Id = 4;
                Dictionary<int, AnimCapability> chain =
                    AnimCapabilities.ClassifyAll(new List<AnimNode> { entry, climber, grab, walker });
                ok &= Check(sb, "a ceiling walk reached only from the ceiling GRAB is still a surface pose",
                    chain[3] == AnimCapability.Climb);
                ok &= Check(sb, "the whole wall/ceiling chain is surface, and the floor walk that enters it is not",
                    chain[1] == AnimCapability.Climb && chain[2] == AnimCapability.Cling &&
                    chain[4] == AnimCapability.Move);

                // TWO hops from the seed, so the growth is tested as a loop rather than as a single step. A
                // one-hop fixture is satisfied by a mutant that stops after the first pass, which is how this
                // gap was found: mutation testing reported the propagation guard SILENT.
                var far = Node(name: "HangCeiling2", endX: -2, gravity: false);
                far.Id = 5;
                var mid = Node(name: "ClimbCeiling", endX: -2, gravity: false, edges: EdgeTo(5, "sequence", "none"));
                mid.Id = 3;
                var near = Node(name: "GrabCeiling", gravity: false, edges: EdgeTo(3, "sequence", "none"));
                near.Id = 2;
                var seed = Node(name: "ClimbWall", endY: -2, gravity: false, edges: EdgeTo(2, "border", "horizontal"));
                seed.Id = 1;
                Dictionary<int, AnimCapability> deep =
                    AnimCapabilities.ClassifyAll(new List<AnimNode> { seed, near, mid, far });
                ok &= Check(sb, "the surface set grows more than one hop from its seed",
                    deep[5] == AnimCapability.Climb && deep[3] == AnimCapability.Climb);

                // ---- the three bounds on the growth, each asserted directly ----
                // Without these the bounds were mutation-SILENT: the bundled pet's wall poses happen not to
                // exercise them, so the census barely moved and "not everything is badged" still passed.

                // 1. The FLOOR is not a surface. A landing is an arrival on the ground, not a grip.
                var lander = Node(name: "Grapple4", startY: -14, endY: 20, gravity: false);
                lander.Id = 2;
                var jumper = Node(name: "Grapple4src", startY: -14, endY: 20, gravity: false,
                    edges: EdgeTo(2, "border", "taskbar"));
                jumper.Id = 1;
                ok &= Check(sb, "an animation reached by only=\"taskbar\" is NOT a surface pose (it landed)",
                    AnimCapabilities.ClassifyAll(new List<AnimNode> { jumper, lander })[2] == AnimCapability.Jump);

                // 2. Something that can FALL is not holding on, however it was reached.
                var falls = Node(name: "Walk", startX: -2, gravity: true);
                falls.Id = 2;
                var gripper = Node(name: "ClimbWall", endY: -2, gravity: false, edges: EdgeTo(2, "sequence", "none"));
                gripper.Id = 1;
                var enters = Node(name: "Run", startX: -4, gravity: true, edges: EdgeTo(1, "border", "vertical"));
                enters.Id = 3;
                ok &= Check(sb, "an animation WITH gravity reached from a surface pose is not a surface pose",
                    AnimCapabilities.ClassifyAll(new List<AnimNode> { enters, gripper, falls })[2] == AnimCapability.Move);

                // 3. `fall` is where every wall pose exits to, and it has no gravity of its own, so without the
                // engine exclusion in Holdable it joins the surface set and then drags the entire floor in behind
                // it. Routed by SEQUENCE edges on purpose (RA-130): with a BORDER edge out of the climb the kind
                // flips to Ceiling (the climb moves and does not descend) and the fall's y-only travel then
                // fails the axis test before Holdable is ever consulted, so this check passed with the
                // exclusion deleted. A sequence edge keeps the kind Wall, along which a y-only drop travels, and
                // the fall's own sequence edge reaches the floor (a border edge below a descent is cut by
                // Descends); the exclusion is the one rule left between `fall` and the set.
                var floorIdle = Node(name: "Stand", gravity: false);
                floorIdle.Id = 4;
                var theFall = Node(name: "fall", startY: 10, endY: 10, gravity: false, entry: true,
                    edges: EdgeTo(4, "sequence", "none"));
                theFall.Id = 2;
                var letsGo = Node(name: "ClimbWall", endY: -2, gravity: false, edges: EdgeTo(2, "sequence", "none"));
                letsGo.Id = 1;
                var wallEntry = Node(name: "Run", startX: -4, gravity: true, edges: EdgeTo(1, "border", "vertical"));
                wallEntry.Id = 3;
                Dictionary<int, AnimCapability> viaFall =
                    AnimCapabilities.ClassifyAll(new List<AnimNode> { wallEntry, letsGo, theFall, floorIdle });
                ok &= Check(sb, "`fall` does not join the surface set, so the floor behind it stays floor",
                    viaFall[2] == AnimCapability.Engine && viaFall[4] == AnimCapability.Idle);
                // A zero-probability edge is written down but can never be taken, so it must not confer a
                // capability the pet cannot reach.
                var deadEdge = Node(name: "Runner", startX: -4, gravity: true);
                deadEdge.Id = 1;
                deadEdge.Edges.Add(new AnimEdge { To = 2, Probability = 0, Kind = "border", Only = "vertical" });
                var unreachedWall = Node(name: "ClimbWall", endY: -2, gravity: false);
                unreachedWall.Id = 2;
                ok &= Check(sb, "a zero-probability border edge confers nothing",
                    AnimCapabilities.ClassifyAll(new List<AnimNode> { deadEdge, unreachedWall })[2] != AnimCapability.Climb);

                // ---- F150: the growth passes THROUGH a flip turn, and knows which surface it is on ----
                // The bundled sheep reaches its ceiling from the wall climb through `top_walk`, a gravity-less
                // <action>flip</action>, and leaves it for the wall descent through another. Excluding flips as
                // engine-owned (right: the pet does not choose them) also made them opaque to the growth, so
                // the ceiling traverse read MOVE "along the ground" and the descent "Plays in place". The same
                // graph shape as the fixture's #37 -> #38 -> #39 -> #40 -> #41 -> #42, hand-built so each rule
                // is asserted on its own and not only through the named fixture labels below.
                var floorWalk = Node(name: "walk", startX: -2, gravity: true, edges: EdgeTo(1, "border", "vertical"));
                floorWalk.Id = 7;
                var wallUp = Node(name: "vertical_walk_up", startY: -2, endY: -2, gravity: false, edges: EdgeTo(2, "border", "none"));
                wallUp.Id = 1;
                var turnUp = Node(name: "top_walk", endX: 2, gravity: false, action: "flip", edges: EdgeTo(3, "sequence", "none"));
                turnUp.Id = 2;
                var ceilingWalk = Node(name: "top_walk2", startX: -2, endX: -2, gravity: false, edges: EdgeTo(4, "border", "none"));
                ceilingWalk.Id = 3;
                var turnDown = Node(name: "top_walk3", gravity: false, action: "flip", edges: EdgeTo(5, "sequence", "none"));
                turnDown.Id = 4;
                var wallDown = Node(name: "vertical_walk_down", startY: 2, endY: 2, gravity: false, edges: EdgeTo(6, "border", "none"));
                wallDown.Id = 5;
                var landing = Node(name: "vertical_walk_over", gravity: false, edges: EdgeTo(7, "sequence", "none"));
                landing.Id = 6;
                Dictionary<int, AnimCapability> sheep = AnimCapabilities.ClassifyAll(
                    new List<AnimNode> { floorWalk, wallUp, turnUp, ceilingWalk, turnDown, wallDown, landing });
                ok &= Check(sb, "a ceiling walk reached through a flip turn is a CLIMB: the growth passes through the turn",
                    sheep[3] == AnimCapability.Climb);
                ok &= Check(sb, "a wall descent reached through a second flip turn is a CLIMB",
                    sheep[5] == AnimCapability.Climb);
                ok &= Check(sb, "the turns passed through stay ENGINE",
                    sheep[2] == AnimCapability.Engine && sheep[4] == AnimCapability.Engine);
                ok &= Check(sb, "the border below a wall descent is the floor: the pose it lands in is not a CLING",
                    sheep[6] == AnimCapability.Idle);
                ok &= Check(sb, "WITNESS the wall climb that seeds the chain is a CLIMB and the floor walk that enters it is MOVE",
                    sheep[1] == AnimCapability.Climb && sheep[7] == AnimCapability.Move);

                // The axis test: a pose is on its surface only when it travels along it, or holds still. The
                // sheep's `boing` is reached by only="vertical" (it hit a wall) and travels x 1..10: that is
                // the bounce OFF the wall, and the shipped rule badged it CLIMB.
                var runner = Node(name: "run", startX: -4, gravity: true, edges: EdgeTo(2, "border", "vertical"));
                runner.Id = 1;
                var bounce = Node(name: "boing", startX: 1, endX: 10, gravity: false);
                bounce.Id = 2;
                ok &= Check(sb, "a wall edge into a pose that travels AWAY from the wall (x only) is a bounce, MOVE, not a CLIMB",
                    AnimCapabilities.ClassifyAll(new List<AnimNode> { runner, bounce })[2] == AnimCapability.Move);
                var glide = Node(name: "run_ul", startX: -8, startY: -6, endX: -8, endY: -6, gravity: false);
                glide.Id = 2;
                ok &= Check(sb, "a wall edge into a diagonal glide is not a CLIMB either: a wall pose travels only up or down",
                    AnimCapabilities.ClassifyAll(new List<AnimNode> { runner, glide })[2] == AnimCapability.Jump);
                var leaper = Node(name: "jump", startY: -15, endY: 20, gravity: false, edges: EdgeTo(2, "border", "horizontal"));
                leaper.Id = 1;
                var drop = Node(name: "fall fast", startY: 8, endY: 12, gravity: false);
                drop.Id = 2;
                AnimCapability dropped = AnimCapabilities.ClassifyAll(new List<AnimNode> { leaper, drop })[2];
                ok &= Check(sb, "a ceiling edge into a pose that drops (y only) is a FALL, neither a CLING nor a CLIMB",
                    dropped == AnimCapability.Fall);
                var traverse = Node(name: "walk_top", startX: -2, endX: -2, gravity: false);
                traverse.Id = 2;
                ok &= Check(sb, "WITNESS the same ceiling edge into a sideways traverse is a CLIMB",
                    AnimCapabilities.ClassifyAll(new List<AnimNode> { leaper, traverse })[2] == AnimCapability.Climb);
                // A turn on the ceiling whose exit drops: passing through the turn must not carry the ceiling's
                // grip onto a fall (the coloured sheep's `hang` -> `fall fast`, which the first cut of this
                // rule badged CLIMB).
                var hang = Node(name: "hang", gravity: false, action: "flip", edges: EdgeTo(3, "sequence", "none"));
                hang.Id = 2;
                var letGo = Node(name: "fall fast", startY: 8, endY: 12, gravity: false);
                letGo.Id = 3;
                var hanger = Node(name: "jump", startY: -15, endY: 20, gravity: false, edges: EdgeTo(2, "border", "horizontal"));
                hanger.Id = 1;
                ok &= Check(sb, "a drop out of a turn on the ceiling is a FALL, not a CLIMB: the turn is passed through AS a ceiling, which admits no vertical travel",
                    AnimCapabilities.ClassifyAll(new List<AnimNode> { hanger, hang, letGo })[3] == AnimCapability.Fall);

                ok &= AgreesWithTheFixture(sb, fixturePetXml);
                ok &= TheBadgeFollowsTheRuntimeBinding(sb, fixturePetXml);
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("  EXC " + ex.GetType().Name + ": " + ex.Message);
            }
            detail = sb.ToString();
            return ok;
        }

        /// <summary>
        /// The table above is hand-built nodes; this drives the classifier over a REAL pet and asserts what it
        /// says about NAMED animations, so the check can fail on the one pet guaranteed to exist. Until 1.1.18
        /// it asserted only the census's shape, and passed on the mislabelled bundled sheep and with the growth
        /// loop deleted (F151); the identity it opened with ("the census covers every animation exactly once",
        /// Census over ClassifyAll) could not fail and is gone.
        /// </summary>
        private static bool AgreesWithTheFixture(StringBuilder sb, string fixturePetXml)
        {
            PetReport report = PetAnalyzer.Analyze(fixturePetXml);
            if (!Check(sb, "the fixture companion analyzes, so the census means something",
                    report.IsValid && report.Nodes.Count > 4))
                return false;

            Dictionary<int, AnimCapability> classified = AnimCapabilities.ClassifyAll(report.Nodes);
            var census = AnimCapabilities.Census(report.Nodes, classified);
            var parts = new List<string>();
            int badged = 0;
            foreach (KeyValuePair<AnimCapability, int> kv in census)
            {
                parts.Add(kv.Key + "=" + kv.Value);
                if (AnimCapabilities.Badge(kv.Key).Length > 0) badged += kv.Value;
            }
            sb.AppendLine("  note fixture census: " + string.Join(", ", parts.ToArray()));

            // The labels F150 found wrong, by id AND name, on the bundled sheep's graph (the fixture IS that
            // graph with a placeholder sheet). Each of these FAILED on the 1.1.17 classifier.
            bool ok = Labelled(sb, report, classified, 37, "vertical_walk_up", AnimCapability.Climb,
                "the sheep's wall climb is a CLIMB");
            ok &= Labelled(sb, report, classified, 39, "top_walk2", AnimCapability.Climb,
                "the sheep's ceiling traverse, reached only through a flip turn, is a CLIMB (it read MOVE 'along the ground' until 1.1.18)");
            ok &= Labelled(sb, report, classified, 41, "vertical_walk_down", AnimCapability.Climb,
                "the sheep's wall descent, behind a second turn, is a CLIMB (it read 'Plays in place' until 1.1.18)");
            ok &= Labelled(sb, report, classified, 8, "boing", AnimCapability.Move,
                "the sheep's wall bounce is MOVE, not a CLIMB: reached at a wall, it travels away from it");
            ok &= Labelled(sb, report, classified, 42, "vertical_walk_over", AnimCapability.Idle,
                "the pose the descent lands in is not a surface pose");
            // N-petstudio-02: the drops the sheep chooses for itself. Both read "Plays in place" until the label existed.
            ok &= Labelled(sb, report, classified, 6, "fall fast", AnimCapability.Fall,
                "the sheep's own fast drop is a FALL (it read 'Plays in place' until 1.1.18)");
            ok &= Labelled(sb, report, classified, 45, "jump_down2", AnimCapability.Fall,
                "the drop after the sheep's jump_down is a FALL");

            // The four magic names exist in every emitted pet and in the bundled one, so ENGINE must appear.
            bool hasEngine = false, hasNonIdle = false;
            foreach (KeyValuePair<AnimCapability, int> kv in census)
            {
                if (kv.Key == AnimCapability.Engine) hasEngine = true;
                if (kv.Key != AnimCapability.Idle && kv.Key != AnimCapability.Engine) hasNonIdle = true;
            }
            ok &= Check(sb, "the fixture's engine-reserved animations are recognised", hasEngine);
            // The whole point is that SOME animations stand out. A classifier that answered Idle for
            // everything would pass every assertion above this one.
            ok &= Check(sb, "the fixture has at least one animation that is neither idle nor engine", hasNonIdle);
            ok &= Check(sb, "not everything is badged, so a badge still means something",
                badged < report.Nodes.Count);

            // Every node must describe itself, and the description must name the capability the badge shows.
            bool consistent = true;
            foreach (AnimNode n in report.Nodes)
            {
                AnimCapability c;
                if (!classified.TryGetValue(n.Id, out c)) { consistent = false; continue; }
                string described = AnimCapabilities.Describe(n, c);
                if (string.IsNullOrWhiteSpace(described)) { consistent = false; continue; }
                string badge = AnimCapabilities.Badge(c);
                if (badge.Length > 0 && described.IndexOf(badge, StringComparison.Ordinal) < 0) consistent = false;
            }
            ok &= Check(sb, "every animation describes itself, and the description names its badge", consistent);
            return ok;
        }

        /// <summary>
        /// ENGINE follows the runtime's binding through the REAL path, not only the hand-built flag: the fixture
        /// with its <c>kill</c> and <c>fall</c> re-spelled with a capital is analyzed, so the same LoadAnimations
        /// and ResolveMagicAnimations the host runs decide. The host binds an exact name only and has no fallback
        /// for kill, so 'Kill' is an ordinary animation (and not a root); it does fall back for fall, to the lowest
        /// id whose name contains the word, so 'Fall' IS the pet's fall. A name rule copied into this module
        /// gave the wrong answer on both until 1.1.18 (RA-128). The unmodified fixture is the WITNESS.
        /// </summary>
        private static bool TheBadgeFollowsTheRuntimeBinding(StringBuilder sb, string fixturePetXml)
        {
            const string killTag = "<name>kill</name>", fallTag = "<name>fall</name>";
            bool once = CountOf(fixturePetXml, killTag) == 1 && CountOf(fixturePetXml, fallTag) == 1;
            if (!Check(sb, "the fixture spells kill and fall exactly once each, so both can be re-spelled", once))
                return false;
            string respelled = fixturePetXml.Replace(killTag, "<name>Kill</name>").Replace(fallTag, "<name>Fall</name>");

            PetReport report = PetAnalyzer.Analyze(respelled);
            if (!Check(sb, "the re-spelled fixture still analyzes (the validator bounds a name's length and nothing else)",
                    report.IsValid && report.Nodes.Count > 4))
                return false;
            Dictionary<int, AnimCapability> classified = AnimCapabilities.ClassifyAll(report.Nodes);
            AnimNode kill = ByName(report, "Kill");
            AnimNode fall = ByName(report, "Fall");
            bool ok = Check(sb, "'Kill' is no engine entry and not ENGINE: the host binds kill by its exact name and has no fallback, so the companion chooses it (RA-128)",
                kill != null && !kill.IsEngineEntry && !kill.IsRoot && classified[kill.Id] != AnimCapability.Engine);
            ok &= Check(sb, "'Fall' IS the engine's entry and ENGINE: with no exact fall the runtime binds the lowest id whose name contains the word",
                fall != null && fall.IsEngineEntry && fall.IsRoot && classified[fall.Id] == AnimCapability.Engine);

            PetReport plain = PetAnalyzer.Analyze(fixturePetXml);
            Dictionary<int, AnimCapability> plainLabels = AnimCapabilities.ClassifyAll(plain.Nodes);
            AnimNode exactKill = ByName(plain, "kill");
            AnimNode exactFall = ByName(plain, "fall");
            ok &= Check(sb, "WITNESS the fixture's own exact kill and fall are engine entries and ENGINE",
                exactKill != null && exactKill.IsEngineEntry && plainLabels[exactKill.Id] == AnimCapability.Engine &&
                exactFall != null && exactFall.IsEngineEntry && plainLabels[exactFall.Id] == AnimCapability.Engine);
            return ok;
        }

        private static int CountOf(string text, string needle)
        {
            int count = 0;
            for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
                count++;
            return count;
        }

        private static AnimNode ByName(PetReport report, string name)
        {
            foreach (AnimNode n in report.Nodes)
                if (n != null && string.Equals(n.Name, name, StringComparison.Ordinal)) return n;
            return null;
        }

        /// <summary>One named fixture label. The id AND the name are checked, so a fixture edit that renumbers
        /// or renames the animation fails here by name instead of quietly asserting some other node.</summary>
        private static bool Labelled(StringBuilder sb, PetReport report, Dictionary<int, AnimCapability> classified,
            int id, string name, AnimCapability expected, string what)
        {
            AnimNode node = null;
            foreach (AnimNode n in report.Nodes)
                if (n != null && n.Id == id) node = n;
            AnimCapability actual;
            if (node == null || !string.Equals(node.Name, name, StringComparison.Ordinal) || !classified.TryGetValue(id, out actual))
                return Check(sb, what + " -- the fixture has no #" + id + " named '" + name + "'", false);
            return Check(sb, what + " (#" + id + " " + name + " reads " + actual + ")", actual == expected);
        }

        private static AnimEdge EdgeTo(int to, string kind, string only)
        {
            return new AnimEdge { To = to, Probability = 100, Kind = kind, Only = only };
        }

        private static bool Check(StringBuilder sb, string what, bool pass)
        {
            sb.AppendLine((pass ? "  ok   " : "  FAIL ") + what);
            return pass;
        }

        /// <summary>A hand-built node. <paramref name="entry"/> is what BuildNodes sets for an animation the
        /// runtime bound as fall/drag/kill/sync; the fixture builder takes it explicitly rather than deriving it
        /// from the name, because deriving it from the name is the rule RA-128 retired.</summary>
        private static AnimNode Node(string name = "x", int startX = 0, int startY = 0, int endX = 0, int endY = 0,
            bool gravity = true, string action = "", bool expression = false, bool entry = false, params AnimEdge[] edges)
        {
            var node = new AnimNode
            {
                Id = 1,
                Name = name,
                StartX = startX,
                StartY = startY,
                EndX = endX,
                EndY = endY,
                HasGravity = gravity,
                Action = action,
                VelocityIsExpression = expression,
                IsEngineEntry = entry,
            };
            if (edges != null) node.Edges.AddRange(edges);
            return node;
        }

    }
}
