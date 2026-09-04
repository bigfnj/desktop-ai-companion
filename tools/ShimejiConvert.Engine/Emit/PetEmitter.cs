using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using DesktopAICompanion.Tools.ShimejiConvert.Shimeji;
using XmlData;

namespace DesktopAICompanion.Tools.ShimejiConvert.Emit
{
    /// <summary>The output of a conversion: the pet, its serialized XML, the honest loss report, and the
    /// reachability verdict the acceptance check reads.</summary>
    public sealed class ConversionResult
    {
        public RootNode Root;
        public string EmittedXml;
        public ResidueReport Residue;
        public GraphReport Graph;
        public bool Valid;         // the app's own validator accepted the emitted XML
        public bool RoundTrips;    // survives serialize -> re-validate
        public string Error;       // validator/emit error, if any

        /// <summary>The machine-checkable acceptance bar: validates, round-trips, and every animation is
        /// reachable (terminals are allowed -- they respawn by design).</summary>
        public bool Accepted { get { return Valid && RoundTrips && Graph != null && Graph.Unreachable.Count == 0; } }
    }

    /// <summary>
    /// Builds a valid, reachable desktopPet pet from a parsed Shimeji skin + its composited sheet.
    ///
    /// v1 is a HUB-AND-SPOKE state machine: each Group1 primitive with sprites becomes one animation, a
    /// standing pose is the hub, and the hub can reach every spoke (which returns to it). This uses every
    /// sprite and produces a lively pet that walks / sits / falls / is draggable, but it does NOT reproduce
    /// Shimeji's conditional Markov behaviour selection -- that simplification is stated in the residue, not
    /// hidden. Group2 actions are recorded as degraded, Group3 as dropped, and the four magic names
    /// (fall / drag / kill / sync) are emitted (kill/sync synthesised, as Shimeji has no equivalent).
    /// </summary>
    public static class PetEmitter
    {
        private const int TickMs = 40;
        private const int MinInterval = 20;
        private const int MaxInterval = 4000;   // cap idle holds at a restful-but-not-frozen 4s

        // A locomotion sequence's repeat count is chosen so the whole walk lasts about this long before the
        // pet re-decides (keep walking / rest / turn), regardless of how slow the source frames are. The old
        // fixed count ran a slow animation (a multi-second Creep) for 6 repeats = 7 passes = ~36s of gliding
        // in one direction -- the "walks weird" report. Total time is (repeat+1) * one-pass duration (see
        // AnimationRuntimeLimits.CalculateTotalSteps: frameCount + frameCount*repeat with repeatfrom 0), so
        // repeat = round(target / passMs) - 1, bounded below by a single pass and above by the old ceiling.
        private const int TargetLocoMs = 2500;
        private const int MinLocoRepeats = 0;   // 0 = play once (AnimationXML: a value of 0 or 1 is no-repeat)
        private const int MaxLocoRepeats = 6;   // the previous fixed value; a fast walk never runs longer than before

        // How long a RESTING pose stays on screen. This has been wrong in BOTH directions, and the resolution
        // is that a rest is not one thing -- it is two, and they want opposite lengths.
        //
        //   * The HUB (Stand) is the pose the pet returns to between every action. Held long, it reads as the
        //     pet standing around doing nothing: it was 9.6s and 67% of every cycle, which is the "doesn't do
        //     anything" report. It must be BRIEF.
        //   * A PERFORMANCE (Sprawl, dangle-legs-with-butterflies, eat-a-berry) is a thing the pet is DOING,
        //     and the user wants to watch it. Cut to ~1.2s (the over-correction after the first fix) it flashes
        //     by unreadably. It must LINGER.
        //
        // So the dwell is split by role. The hand-authored sheep does not need this because its hub is a
        // fast-cycling blink and it has no long performances; a converted pet has rich idle actions and a
        // sticky hub, so the two are timed apart.
        private const int HubDwellMs = 2000;     // the return-to pose: brief, so the pet does not loiter
        private const int RestDwellMs = 11000;   // a performance: 9-12s, long enough to actually watch
        // A rest's per-frame interval is capped so a "hold this frame for 3 seconds" baked into a source
        // interval (Stand's 3000ms) cannot freeze the animation; the dwell is reached by repeating instead. A
        // real performance's pacing (100-300ms/frame) sits well under this and is untouched.
        private const int MaxRestIntervalMs = 700;
        private const int MaxRestRepeats = 200;  // enough for a short cycle to reach 11s; bounded against runaway

        public static ConversionResult Emit(ShimejiConfig config, SpriteSheet sheet, Func<string, Bitmap> load, string skinName, Func<string, byte[]> loadSound = null)
        {
            var result = new ConversionResult { Residue = new ResidueReport() };
            skinName = string.IsNullOrWhiteSpace(skinName) ? "Shimeji" : skinName.Trim();

            // --- gather sprite-bearing primitives and the magic sources ---
            ShimejiAction fallAction = FirstWithClass(config, "Fall");
            ShimejiAction dragAction = FirstWithClass(config, "Dragged");

            var spokes = new List<Emitted>();
            foreach (ShimejiAction a in config.Actions)
            {
                bool gaze = IsGazeAction(a);
                if (!IsFloorAction(a) && !gaze) continue;      // the floor region (no ceiling/embedded)
                if (a == fallAction) continue;                 // becomes the 'fall' magic animation
                List<int> frames = FramesOf(a, sheet);
                if (frames.Count == 0) continue;               // no sprites (e.g. Look/Offset) -> not an animation
                spokes.Add(new Emitted { Name = SanitizeName(a.Name), Source = a, Frames = frames, IsGaze = gaze });
            }
            spokes = CollapseDirectionPairs(spokes);

            // The WALL region: a separate set, deliberately NOT reachable from the floor hub. Entry is only
            // ever a vertical-border edge on a locomotion animation, which is what stops a wall-cling from
            // playing in the middle of the screen -- the reason wall actions were excluded outright before.
            var wallSpokes = new List<Emitted>();
            foreach (ShimejiAction a in config.Actions)
            {
                if (!IsWallAction(a)) continue;
                List<int> frames = FramesOf(a, sheet);
                if (frames.Count == 0) continue;
                wallSpokes.Add(new Emitted { Name = SanitizeName(a.Name), Source = a, Frames = frames });
            }
            wallSpokes = CollapseDirectionPairs(wallSpokes);

            // The CEILING region. Reachable ONLY from the wall region's climb, via an only="horizontal" edge,
            // so like the wall it can never play mid-screen -- and unlike the wall it cannot even be entered
            // from the floor, since a floor animation never travels upward to reach the top border.
            var ceilingSpokes = new List<Emitted>();
            foreach (ShimejiAction a in config.Actions)
            {
                if (!IsCeilingAction(a)) continue;
                List<int> frames = FramesOf(a, sheet);
                if (frames.Count == 0) continue;
                ceilingSpokes.Add(new Emitted { Name = SanitizeName(a.Name), Source = a, Frames = frames });
            }
            ceilingSpokes = CollapseDirectionPairs(ceilingSpokes);
            // The ceiling is reached by an only="horizontal" edge on a wall spoke that CLIMBS, so a wall
            // region alone is not enough: something has to travel upward.
            //
            // KinitoPET is the case that surfaced this. Its ClimbWall is dropped (it branches on
            // mascot.anchor.*, which this format cannot express), leaving only a static GrabWall -- so the
            // ceiling animations were emitted with nothing able to reach them, and the pet failed acceptance
            // on an unreachable animation.
            //
            // Rather than lose the ceiling, synthesise the climb: a grab pose IS the pet gripping a wall, and
            // giving it upward velocity is what climbing looks like. The same move the emitter already makes
            // for 'turn', which is a hub frame with a flip attached. Only if that fails does the region go.
            SynthesiseClimbIfNeeded(wallSpokes);
            if (!wallSpokes.Any(WillClimb)) ceilingSpokes.Clear();

            // A hub every spoke can return to. Prefer a standing pose.
            Emitted hub = spokes.FirstOrDefault(e => e.Source != null && string.Equals(e.Source.Name, "Stand", StringComparison.OrdinalIgnoreCase))
                          ?? spokes.FirstOrDefault();
            if (hub == null)
            {
                // Degenerate skin with no usable primitive: synthesise a one-frame stand on tile 0.
                hub = new Emitted { Name = "stand", Source = null, Frames = new List<int> { 0 } };
                spokes.Add(hub);
            }

            // --- assemble the ordered animation list and assign ids ---
            var all = new List<Emitted>();
            all.AddRange(spokes);
            all.AddRange(wallSpokes);
            all.AddRange(ceilingSpokes);

            Emitted fall = null, drag = null;
            if (fallAction != null)
            {
                List<int> f = FramesOf(fallAction, sheet);
                if (f.Count > 0) fall = new Emitted { Name = "fall", Source = fallAction, Frames = f };
            }
            if (fall == null) fall = new Emitted { Name = "fall", Source = null, Frames = hub.Frames };
            all.Add(fall);

            if (dragAction != null)
            {
                List<int> d = DragSwingFramesOf(dragAction, sheet);
                if (d.Count > 0) drag = new Emitted { Name = "drag", Source = dragAction, Frames = d };
            }
            if (drag == null) drag = new Emitted { Name = "drag", Source = null, Frames = hub.Frames };
            all.Add(drag);

            var kill = new Emitted { Name = "kill", Source = null, Frames = hub.Frames };
            var sync = new Emitted { Name = "sync", Source = null, Frames = hub.Frames };
            // A one-frame "turn" that flips facing, so a walker reaching a screen edge turns and heads back
            // instead of standing against the wall doing idles forever.
            //
            // The name must not collide with a spoke's. Several skins already have an action called "Turn",
            // and emitting a second animation with that name shipped pets carrying TWO <animation> nodes
            // named "turn" -- only one of which had <action>flip</action>. Anything resolving an animation by
            // name (IHost.TryPlayAnimation, the debug menu, a module's reaction list) takes the FIRST match,
            // so which one you got was down to emit order.
            string turnName = UniqueName("turn", spokes, wallSpokes, ceilingSpokes);
            var turn = new Emitted { Name = turnName, Source = null, Frames = hub.Frames };
            all.Add(kill);
            all.Add(sync);
            all.Add(turn);

            // Drop the source's `_left` / `_right` suffixes now the WHOLE name set exists -- including
            // fall/drag/kill/sync and the resolved `turn` -- because whether a rename is safe depends on the
            // other names, and doing it per-spoke could rename `turn_left` onto a `turn` added later.
            {
                var emittedNames = new List<string>();
                foreach (Emitted e in all) emittedNames.Add(e.Name);
                Dictionary<string, string> undirect = UndirectNames(emittedNames);
                if (undirect.Count > 0)
                    foreach (Emitted e in all)
                    {
                        string bare;
                        if (e.Name != null && undirect.TryGetValue(e.Name, out bare)) e.Name = bare;
                    }
            }

            for (int i = 0; i < all.Count; i++) all[i].Id = i + 1;

            // --- build each animation node ---
            HubSpokes = spokes;   // so the hub's <next> set can reach every spoke
            Dictionary<string, int> spokeWeights = BuildSpokeWeights(config);
            foreach (Emitted s in spokes) s.Weight = HubWeightFor(s, hub, spokeWeights);
            // Then guarantee a minimum share, which needs the WHOLE set and so cannot live in HubWeightFor.
            // Without it the damped weights still leave a long tail that is technically reachable and
            // practically invisible: the shipped corpus had 392 of 609 hub options below 1%, the worst at
            // 0.03% (~54 minutes of idling to appear once).
            ApplyHubMinimumShare(spokes, hub);

            // Where a floor walker enters the wall. Prefer a climbing (Move) primitive over a static grab, so
            // hitting an edge actually goes somewhere; null when the skin has no wall sprites, in which case
            // everything below degrades to the previous floor-only behaviour.
            Emitted wallEntry = wallSpokes.FirstOrDefault(e => e.Source != null && ClimbsUpward(e.Source))
                                ?? wallSpokes.FirstOrDefault();

            // Where a climbing pet enters the ceiling, and where a ceiling walker gets back onto a wall.
            // Prefer a DESCENDING wall pose for the exit: leaving the ceiling onto a climb would send the pet
            // straight back up into the border it just left.
            Emitted ceilingEntry = ceilingSpokes.FirstOrDefault();
            Emitted wallExit = wallSpokes.FirstOrDefault(e => e.Source != null && !ClimbsUpward(e.Source))
                               ?? wallSpokes.FirstOrDefault();

            // Where a jump LANDS into, so the pet arrives on its feet and keeps moving rather than flipping
            // and standing still. The pet's most-used ground locomotion, taken from the hub weights already
            // computed above: that is the source's own statement of what this character mostly does, and it
            // needs no naming convention to survive a corpus written in five languages. A launcher is excluded
            // -- landing a jump into another jump is the re-jump edge's job, and picking one here would give a
            // pet two different hops in a row with no beat between them.
            Emitted landRun = spokes
                .Where(s => s != hub && IsLocomotion(s) && !Launches(s))
                .OrderByDescending(s => s.Weight)
                .FirstOrDefault();

            var nodes = new List<AnimationNode>();
            foreach (Emitted e in spokes)
                nodes.Add(BuildSpoke(e, hub, fall, turn, wallEntry, wallExit, ceilingEntry, landRun));
            foreach (Emitted e in wallSpokes)
                nodes.Add(BuildWallSpoke(e, fall, wallSpokes, ceilingEntry, hub));
            foreach (Emitted e in ceilingSpokes)
                nodes.Add(BuildCeilingSpoke(e, fall, wallExit, ceilingSpokes));
            nodes.Add(BuildFall(fall, hub));
            nodes.Add(BuildDrag(drag, fall));
            nodes.Add(BuildKill(kill));
            nodes.Add(BuildSync(sync, hub));
            nodes.Add(BuildTurn(turn, hub));

            // --- header (with a generated icon from the hub's first sprite) ---
            var header = BuildHeader(skinName, config, hub, load);

            var root = new RootNode
            {
                Header = header,
                Image = new ImageNode
                {
                    TilesX = sheet.TilesX,
                    TilesY = sheet.TilesY,
                    Png = sheet.Base64Png,
                    // "Alpha" is the host's reserved keyword (Xml.AlphaTransparencyKeyword) selecting the
                    // per-pixel render path; any real colour name keeps the magenta colour-key path.
                    Transparency = sheet.IsAlpha ? "Alpha" : "Magenta",
                },
                Spawns = new SpawnsNode
                {
                    Spawn = new[]
                    {
                        // Both spawns land ON-SCREEN (a spawn's <next> takes probability but NOT only): one
                        // drops in from the top, one appears standing on the floor. An off-screen "walk in
                        // from the edge" needs a locomotion next -- routing it to the stationary hub left the
                        // pet standing off-screen and invisible.
                        new SpawnNode { Id = 1, Probability = 50, X = "random*(screenW-imageW-50)/100+25", Y = "-imageH-20", Next = Next(fall.Id, 100, null) },
                        new SpawnNode { Id = 2, Probability = 50, X = "random*(screenW-imageW-50)/100+25", Y = "areaH-imageH", Next = Next(hub.Id, 100, null) },
                    },
                },
                Animations = new AnimationsNode { Animation = nodes.ToArray() },
                Childs = new ChildsNode(),   // Breed is Group3 -> no children
            };

            // --- sounds: transcode each sounded action's clip to MP3 and attach it to that animation. The
            // desktopPet format ties a sound to an animation (played at its start), so a per-pose clip is
            // mapped to the whole animation via its FIRST sounded pose; per-pose timing is not reproduced. All
            // best-effort and budgeted by the loader -- a missing clip / no transcoder just leaves it silent. ---
            int soundWanted = 0, soundCaptured = 0;
            var soundNodes = new List<SoundNode>();
            foreach (Emitted e in all)
            {
                if (e.Source == null) continue;
                string clip = FirstSoundClip(e.Source);
                if (clip == null) continue;
                soundWanted++;
                if (loadSound == null) continue;
                byte[] mp3;
                try { mp3 = loadSound(clip); } catch { mp3 = null; }
                if (mp3 == null || mp3.Length == 0) continue;
                soundNodes.Add(new SoundNode
                {
                    Id = e.Id,
                    Probability = 100,
                    Loop = 0,
                    Base64 = Convert.ToBase64String(mp3),
                });
                soundCaptured++;
            }
            if (soundNodes.Count > 0)
                root.Sounds = new SoundsNode { Sound = soundNodes.ToArray() };

            BuildResidue(config, result.Residue, sheet.IsAlpha);
            AppendSoundResidue(result.Residue, soundWanted, soundCaptured, loadSound != null);

            // --- validate + round-trip + reachability ---
            result.Root = root;
            result.EmittedXml = ShimejiEngine.Serialize(root);
            RootNode reparsed;
            string error;
            result.Valid = ShimejiEngine.TryValidate(result.EmittedXml, out reparsed, out error);
            result.Error = error;
            if (result.Valid)
            {
                result.Graph = ShimejiEngine.Analyze(reparsed);
                string rtError;
                result.RoundTrips = ShimejiEngine.RoundTrips(reparsed, out rtError);
                if (!result.RoundTrips && string.IsNullOrEmpty(result.Error)) result.Error = rtError;
            }
            else
            {
                result.Graph = ShimejiEngine.Analyze(root);
            }
            return result;
        }

        private sealed class Emitted
        {
            public int Id;
            public string Name;
            public ShimejiAction Source; // null for synthesised animations
            public List<int> Frames;
            public int Weight = HubBaseWeight; // how often the hub selects this spoke (source behaviour frequency + baseline)
            // Overrides the vertical velocity read from the source poses. Set only when a wall region has
            // sprites but no CLIMBING action, so a static grab pose can be animated upward -- see
            // SynthesiseClimbIfNeeded.
            public int? ForcedVelY;
            // A gaze pose: emitted with the faceCursor sequence action so it is aimed at the pointer on
            // entry, rather than held facing an arbitrary direction.
            public bool IsGaze;
        }

        private static ShimejiAction FirstWithClass(ShimejiConfig config, string shortClass)
        {
            foreach (ShimejiAction a in config.Actions)
                if (string.Equals(a.Class, shortClass, StringComparison.Ordinal)) return a;
            return null;
        }

        // A floor-appropriate primitive: Group1, has sprites, not Embedded (Look/Offset/Fall/Dragged/Jump/
        // Regist), and Floor (or unset) border context. Wall/ceiling/climb primitives are excluded from the
        // emitted behaviour -- they would play nonsensically on the floor -- and recorded in the residue.
        /// <summary>
        /// A wall primitive: Group1, has sprites, not Embedded, and its border context is Wall.
        ///
        /// Deliberately does NOT inherit IsFloorAction's rejection of upward velocity. On the floor a negative
        /// VelY launches the pet off the top of the screen, which is why that guard exists; on a wall climbing
        /// up IS the behaviour. The separation is what makes it safe to allow one and not the other.
        ///
        /// Wall poses need no anchor rework: the reference conf anchors ClimbWall and GrabWall at the same
        /// 64,128 as Stand and Walk, so admitting them to the sheet cannot change cell geometry. (Ceiling poses
        /// anchor at 64,48 and DO need normalising, which is why they are a separate step.)
        /// </summary>
        internal static bool IsWallAction(ShimejiAction a)
        {
            if (a == null) return false;
            // Group1 OR Group2, unlike the floor region. Group2 means "the SELECTION CONDITION needs host
            // state we do not have" (ShimejiModel: a condition referencing cursor/anchor/activeIE), not "the
            // animation is unconvertible" -- and the wall region replaces Shimeji's conditional selection with
            // its own border-driven graph, so that condition was never going to be honoured either way. The
            // emitter already discards conditions everywhere by using Animations[0] only.
            // Without this, the reference conf contributes GrabWall (Group1) but NOT ClimbWall (Group2), so
            // the pet grabs the wall and hangs there motionless -- half a feature.
            // Group3 stays out: those are Embedded classes (window throwing, breeding), which is code.
            if (a.Group != FidelityGroup.Group1 && a.Group != FidelityGroup.Group2) return false;
            if (a.Animations.Count == 0 || a.Animations[0].Poses.Count == 0) return false;
            if (a.Class != null) return false;
            return string.Equals(a.BorderType, "Wall", StringComparison.Ordinal);
        }

        /// <summary>
        /// A ceiling primitive: the same admission rules as <see cref="IsWallAction"/>, but BorderType Ceiling.
        ///
        /// Group2 is admitted for the same reason as the wall: the reference conf makes ClimbCeiling
        /// conditional, and the emitter replaces Shimeji's conditional selection with its own border-driven
        /// graph anyway.
        ///
        /// Unlike the wall, these poses DO need an anchor rework. The reference conf anchors GrabCeiling and
        /// ClimbCeiling at 64,48 rather than the 64,128 that Stand and Walk use, because for a hanging mascot
        /// the contact point is near the top of the sprite. Compositing them under the floor convention would
        /// hang them from their feet, so <see cref="PosesToComposite"/> marks them AnchorToTop.
        /// </summary>
        /// <summary>
        /// Collapse direction pairs into one animation each.
        ///
        /// A source skin stores ONE set of artwork and then defines walk_left AND walk_right over the very
        /// same frames, because the player is expected to MIRROR one of them. This engine does exactly that:
        /// <c>FormCompanion.GetSpriteFrame</c> draws <c>Xml.GetSpriteFrame(index, !IsMovingLeft)</c>, so unmirrored
        /// art is the left-facing direction and the mirror is applied when the pet faces right.
        ///
        /// Emitting BOTH variants therefore produced a pet that moonwalked. With the default
        /// IsMovingLeft=true, walk_left drew left-facing art moving left (right), while walk_right drew the
        /// same left-facing art moving RIGHT (wrong); after a flip the two swapped which one was broken. Half
        /// of all locomotion was wrong in either facing, which reads as "the pet only ever faces left".
        ///
        /// So keep ONE animation per identical frame list and let the flip handle facing, exactly as every
        /// hand-authored pet does. Identity is the FRAME LIST, not the name: it needs no naming convention,
        /// and it cannot merge two animations that genuinely differ in artwork. Where there is a choice the
        /// leftward variant wins, because unmirrored art is what the engine treats as left-facing.
        /// </summary>
        /// <summary>A name no emitted spoke already uses, so two animations can never share one. The magic
        /// names (fall/drag/kill/sync) are safe because the emitter takes those actions over as the magic
        /// animation rather than emitting them as spokes; "turn" is synthesised, so it can collide.</summary>
        private static string UniqueName(string preferred, params IEnumerable<Emitted>[] taken)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IEnumerable<Emitted> set in taken)
                if (set != null)
                    foreach (Emitted e in set)
                        if (e != null && e.Name != null) used.Add(e.Name);
            if (!used.Contains(preferred)) return preferred;
            for (int n = 2; n < 100; n++)
            {
                string candidate = preferred + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!used.Contains(candidate)) return candidate;
            }
            return preferred + Guid.NewGuid().ToString("N").Substring(0, 4);
        }

        /// <summary>
        /// Give a wall region an upward climb when the source did not supply one.
        ///
        /// A skin can have perfectly good wall SPRITES and still lose its climbing ACTION, because the climb
        /// was the thing that branched on host state this format cannot express (KinitoPET's ClimbWall reads
        /// mascot.anchor.*). What is left is a static grab, and the whole wall-and-ceiling chain needs
        /// something that moves up.
        ///
        /// A grab pose is the pet gripping a wall, so animating it upward reads as climbing. Applied to the
        /// LAST wall spoke rather than the first, so a descend (which the collapse tends to order after the
        /// grab) is not the one commandeered; if that leaves the region with a single spoke, that spoke both
        /// climbs and is the ceiling entry, which is exactly what a one-pose wall skin can support.
        /// </summary>
        private static void SynthesiseClimbIfNeeded(List<Emitted> wallSpokes)
        {
            if (wallSpokes.Count == 0) return;
            if (wallSpokes.Any(WillClimb)) return;

            // Prefer a spoke that is not already descending: turning a descend into a climb would fight the
            // source's own intent, and the border edges route descend -> fall deliberately.
            Emitted target = wallSpokes.LastOrDefault(e => e.Source == null || !DescendsDownward(e.Source))
                             ?? wallSpokes[wallSpokes.Count - 1];
            target.ForcedVelY = SynthesisedClimbVelY;
        }

        // Matches the hand-authored sheep's wall_slide (y=-30) in intent but far gentler, because this is a
        // STATIC pose being slid upward rather than a real climb cycle: fast enough to read as climbing,
        // slow enough that a single frame does not look like it is being yanked.
        private const int SynthesisedClimbVelY = -6;

        private static bool WillClimb(Emitted e)
        {
            if (e == null) return false;
            if (e.ForcedVelY.HasValue) return e.ForcedVelY.Value < 0;
            return e.Source != null && ClimbsUpward(e.Source);
        }

        private static bool DescendsDownward(ShimejiAction a)
        {
            if (a == null || a.Animations.Count == 0) return false;
            foreach (ShimejiPose p in a.Animations[0].Poses)
                if (p != null && p.VelY > 0) return true;
            return false;
        }

        private static List<Emitted> CollapseDirectionPairs(List<Emitted> candidates)
        {
            var kept = new List<Emitted>();
            foreach (Emitted e in candidates)
            {
                // IsGaze has to match. A gaze and an ordinary rest are frequently the SAME drawing held still
                // (Ralsei's gaze fallback is his sit pose), so frames and velocities agree and the collapse
                // would happily merge them -- keeping whichever came first and, half the time, discarding the
                // faceCursor tag that is the only difference between the two.
                int existing = kept.FindIndex(k => k.IsGaze == e.IsGaze && SameFrames(k, e) && MirrorsOrDuplicates(k, e));
                if (existing < 0) { kept.Add(e); continue; }
                // Prefer the LEFTWARD variant: unmirrored art is what the engine treats as left-facing.
                if (FirstVelX(e) < 0 && FirstVelX(kept[existing]) >= 0) kept[existing] = e;
            }
            return kept;
        }

        private static bool SameFrames(Emitted a, Emitted b)
        {
            if (a.Frames.Count != b.Frames.Count) return false;
            for (int i = 0; i < a.Frames.Count; i++) if (a.Frames[i] != b.Frames[i]) return false;
            return true;
        }

        /// <summary>
        /// True only when two same-framed animations are the SAME behaviour: either an exact duplicate, or a
        /// left/right pair (horizontal velocity mirrored, vertical velocity identical).
        ///
        /// Comparing frame lists alone is not enough, and getting that wrong cost KinitoPET its wall climb.
        /// Its GrabWall and ClimbWall are built from the very same four images -- because the art IS the pet
        /// gripping a wall -- but GrabWall holds still (0,0 throughout) while ClimbWall travels up
        /// (0,0 / 0,-1 / 0,-2 / 0,-1). Two genuinely different behaviours over one set of drawings. Merging
        /// them kept the static grab, threw away the climb, and with it the only route to the ceiling.
        /// </summary>
        private static bool MirrorsOrDuplicates(Emitted a, Emitted b)
        {
            List<ShimejiPose> pa = PosesOf(a), pb = PosesOf(b);
            if (pa.Count != pb.Count) return false;
            bool identical = true, mirrored = true;
            for (int i = 0; i < pa.Count; i++)
            {
                if (pa[i].VelX != pb[i].VelX || pa[i].VelY != pb[i].VelY) identical = false;
                if (pa[i].VelX != -pb[i].VelX || pa[i].VelY != pb[i].VelY) mirrored = false;
                if (!identical && !mirrored) return false;
            }
            return identical || mirrored;
        }

        private static List<ShimejiPose> PosesOf(Emitted e)
        {
            if (e == null || e.Source == null) return new List<ShimejiPose>();
            ShimejiAnimation variant = VariantFor(e.Source);
            return variant == null ? new List<ShimejiPose>() : variant.Poses;
        }

        private static int FirstVelX(Emitted e)
        {
            List<ShimejiPose> poses = PosesOf(e);
            return poses.Count > 0 ? poses[0].VelX : 0;
        }

        /// <summary>
        /// A GAZE primitive: a stationary floor pose whose only reason to exist is the pointer
        /// ("sit and look at the mouse"). Admitted as Group1 OR Group2, and emitted with the
        /// <c>faceCursor</c> sequence action so it is actually aimed.
        ///
        /// These were excluded outright before, because a cursor condition makes the whole action Group2 and
        /// IsFloorAction demands Group1 -- so 18 gaze actions across 13 pets converted to nothing at all.
        /// The wall region already makes the Group2 argument (a lost SELECTION condition is not an
        /// unconvertible animation), and here it is stronger: the condition is not being discarded, it is
        /// being IMPLEMENTED. "Face whichever way the pointer is" is exactly what faceCursor does.
        ///
        /// Stationary only. A cursor-conditioned action that MOVES is a chase, which needs per-tick steering
        /// this format cannot express, and admitting one would give a pet that lurches off in a fixed
        /// direction whenever it felt like chasing.
        /// </summary>
        internal static bool IsGazeAction(ShimejiAction a)
        {
            if (a == null) return false;
            if (a.Group != FidelityGroup.Group1 && a.Group != FidelityGroup.Group2) return false;
            if (a.Animations.Count == 0 || a.Animations[0].Poses.Count == 0) return false;
            if (a.Class != null) return false;   // Embedded behaviour (Pinched is Dragged), handled elsewhere
            if (a.BorderType != null && !string.Equals(a.BorderType, "Floor", StringComparison.Ordinal)) return false;
            if (!Has(a.SubtreeBlob, "cursor")) return false;
            // EVERY variant, not just the first: the whole point of the cascade is that any of them can be the
            // one that plays, so one moving variant makes the action a chase however still the others are.
            foreach (ShimejiAnimation variant in a.Animations)
            {
                if (variant == null) continue;
                foreach (ShimejiPose p in variant.Poses)
                    if (p != null && (p.VelX != 0 || p.VelY != 0)) return false;   // moving => a chase, not a gaze
            }
            return true;
        }

        private static bool Has(string blob, string needle)
        {
            return blob != null && blob.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// The <see cref="ShimejiAnimation"/> variant to use for an action. Animations[0] for everything
        /// except a gaze, where it is the UNCONDITIONAL fallback variant.
        ///
        /// Every other action's variants are alternatives whose first is as good as any. A gaze's are not:
        /// they are a cascade over where the pointer is, and taking the first would pin the pet in whichever
        /// extreme the author happened to write at the top. Measured across the seven skins that ship one, the
        /// first variant is always <c>cursor.y &lt; screen.height/2.5</c> or <c>/2</c> -- "the pointer is near
        /// the top of the screen" -- so Animations[0] is a pet permanently craning upward.
        ///
        /// The last variant carries no Condition in all four shapes present in the corpus (2, 3 and 7 variants
        /// wide), because Shimeji takes the first match top to bottom and an author must end the cascade with a
        /// catch-all or the action can fail to resolve. That catch-all is by construction the neutral pose, and
        /// it is the one frame that is correct under a horizontal-only aim.
        ///
        /// A median pick was the alternative and is wrong on the widest case: Serial Designation J's seven
        /// variants split on cursor.x as well as cursor.y, so the middle of the list is "up and to the left",
        /// not "level".
        /// </summary>
        private static ShimejiAnimation VariantFor(ShimejiAction a)
        {
            if (a == null || a.Animations.Count == 0) return null;
            if (!IsGazeAction(a)) return a.Animations[0];
            for (int i = a.Animations.Count - 1; i >= 0; i--)
            {
                ShimejiAnimation v = a.Animations[i];
                if (v != null && v.Poses.Count > 0 && string.IsNullOrWhiteSpace(v.Condition)) return v;
            }
            return a.Animations[0];
        }

        internal static bool IsCeilingAction(ShimejiAction a)
        {
            if (a == null) return false;
            if (a.Group != FidelityGroup.Group1 && a.Group != FidelityGroup.Group2) return false;
            if (a.Animations.Count == 0 || a.Animations[0].Poses.Count == 0) return false;
            if (a.Class != null) return false;
            return string.Equals(a.BorderType, "Ceiling", StringComparison.Ordinal);
        }

        internal static bool IsFloorAction(ShimejiAction a)
        {
            if (a == null || a.Group != FidelityGroup.Group1) return false;
            if (a.Animations.Count == 0 || a.Animations[0].Poses.Count == 0) return false;
            if (a.Class != null) return false;   // Embedded actions are handled as magic or excluded
            if (a.BorderType != null && !string.Equals(a.BorderType, "Floor", StringComparison.Ordinal)) return false;
            // Upward velocity used to be rejected outright here, because an unbounded climb or fling launches
            // the pet off the top of the screen. That guard also refused every JUMP: 81 actions across 27
            // pets, more than any other single gap, and the widest of the lot.
            //
            // It was never a format or engine limitation. The hand-authored pets jump constantly --
            // yellow_sheep carries 22 upward-start animations, its `jump` being -15 up then +20 down, and NOT
            // ONE of them has a <gravity> element, because the whole arc lives in the start/end
            // interpolation. Gravity would end the jump the instant the pet left the ground.
            //
            // So an upward floor action is admitted, and BuildSpoke emits it as a bounded arc (clamped launch,
            // forced descent, no gravity) rather than passing the source velocity through. Bounded is what
            // makes it safe: whatever the source asked for, the pet comes back down.
            return true;
        }

        /// <summary>
        /// The strongest upward velocity anywhere in a floor action, as a negative number (0 = never rises).
        /// </summary>
        private static int LaunchVelY(ShimejiAction a)
        {
            if (a == null || a.Animations.Count == 0) return 0;
            int strongest = 0;
            foreach (ShimejiPose p in a.Animations[0].Poses)
                if (p != null && p.VelY < strongest) strongest = p.VelY;
            return strongest;
        }

        /// <summary>
        /// True when a floor action LAUNCHES: it rises, and it rises hard enough to mean it. Emitted as a
        /// solved arc rather than with the source's own velocities, so neither a pathological launch nor a
        /// gentle drift decides how high the pet goes.
        ///
        /// The <see cref="JumpMinLaunchY"/> floor matters because the alternative is not "a smaller jump": an
        /// action that drifts up 5px per tick and has its end forced to +20 spends most of its sequence
        /// falling, which is how Grapple1 shipped as a 16px twitch and `fly` as an 8px one. Below the floor
        /// the rise is FLATTENED instead (see BuildSpoke), which keeps the animation and its sprites and only
        /// discards the vertical component the floor cannot carry safely.
        /// </summary>
        private static bool Launches(Emitted e)
        {
            return e != null && e.Source != null && QualifiesAsJump(e.Source);
        }

        /// <summary>Whether an action converts to a jump. Public so BuildResidue reports exactly the set
        /// BuildSpoke emits, rather than a second opinion about it.</summary>
        internal static bool QualifiesAsJump(ShimejiAction a)
        {
            return a != null && a.Animations.Count > 0 && LaunchVelY(a) <= JumpMinLaunchY;
        }

        /// <summary>True when an action rises, but too weakly to be a jump: its vertical velocity is dropped
        /// and the animation plays flat along the ground.</summary>
        internal static bool RiseIsFlattened(ShimejiAction a)
        {
            int launch = LaunchVelY(a);
            return launch < 0 && launch > JumpMinLaunchY;
        }

        // ---- the jump arc -------------------------------------------------------------------------------
        //
        // The first version clamped the source's launch velocity to -15 and forced the end to +20, then let
        // the LOCOMOTION budget pick the step count. That was wrong in a way only a measurement shows: with a
        // linear start->end ramp, the height a jump reaches depends on the number of STEPS, not just on the
        // launch velocity. Replaying the engine's own interpolation over the 32 jumps in the shipped corpus
        // gave a bimodal disaster -- 16 of them peaked under 20px (Hornet's Grapple1 at 16px, jump_up_left at
        // 11px: a twitch) and the other 16 at 72px (PullUpShimeji2 and friends, whose 3 frames were repeated
        // to 21 steps by MaxLocoRepeats: a fling). Nothing landed in between, and nobody chose either number.
        //
        // So the invariant is the PEAK HEIGHT, and the launch velocity is solved for it. Target and descent
        // are yellow_sheep's `jump` measured rather than guessed: -15 up, +20 down over its 14 authored frames
        // rises 48px and is airborne 1.2s.
        //
        // Public where the `rejump` migration needs them: it re-arcs the pets already shipped, and two
        // implementations of one policy drift. Same reason HubWeightFromFrequency and RepeatCountForBudget
        // are public.
        public const int JumpPeakPx = 48;
        public const int JumpDescentY = 20;

        // How far a jump may travel SIDEWAYS over the whole arc. The vertical fix exposed this one: Hornet's
        // Grapple4 is a grapple-hook dash at -100px per tick, and once the arc lasted a proper 15 steps it
        // crossed 1500px, so the pet reached a screen EDGE before it reached the ground and the landing set
        // almost never fired (measured: 16 of 18 jumps ended at a side border, 1 landed). yellow_sheep's jump
        // travels 10px per tick over 14 steps, so 150px is the sheep's own span rather than a new number.
        // Per-tick rather than total, because the cap has to be expressed in the units the format carries.
        public const int JumpSpanPx = 150;
        private const int JumpArcSteps = 14;    // the step budget a jump aims for, from the sheep
        private const int JumpTargetMs = 1200;  // ...and its airtime, which sets a FLAT interval
        private const int MaxJumpRepeats = 20;  // enough for a single-frame jump to reach JumpArcSteps
        private const int JumpLaunchMinMag = 4; // search bounds for the solved launch velocity
        private const int JumpLaunchMaxMag = 40;

        // Weakest upward velocity that still means "this action is a jump". Every genuine jump in the corpus
        // launches at -8 (jump_up_left, jumping) or -15 (Launching, PullUpShimeji2); the only things below
        // that line are Hornet's Grapple1 at -5 (a grapple-hook pose) and 1l2yvz73's `fly` at -5 (a flight
        // cycle). Velocity cannot separate a jump from a pull-up, and no naming convention would survive the
        // corpus's five languages, so this filters only what is too weak to be a jump at all.
        public const int JumpMinLaunchY = -8;

        // A jump's LANDING. Until now the only floor-eligible border edge a locomotion spoke carried was
        // `turn` (weight 2, only="none"), so every landing was a facing flip -- a pet that hopped straight up
        // reversed direction on the way down -- followed by the hub's full idle dwell. The hand-authored sheep
        // resolve a landing from a weighted set of MOVEMENT and recovery actions (from yellow_sheep's `jump`:
        // run 25, jump 30, run_jump_flip 25, jump_down 10, run_fail 5, ground_slide_short 5) and never into an
        // idle, which is the whole reason the sheep reads as landing on its feet. Against BorderTurnWeight = 2
        // these make a landing continue into motion 3 times in 4.
        public const int LandRejumpWeight = 3;
        public const int LandRunWeight = 3;

        /// <summary>
        /// Height in px an arc of <paramref name="totalSteps"/> steps rises before it turns over, replaying
        /// FormCompanion's per-step interpolation exactly (<c>y_k = y0 + (yN - y0) * k / (totalSteps - 1)</c>,
        /// applied once per tick). Shared by the solver and the self-test so the assertion and the code under
        /// test cannot drift apart.
        /// </summary>
        public static double ArcRisePx(int launchY, int descentY, int totalSteps)
        {
            if (totalSteps < 1 || launchY >= 0) return 0.0;
            int interp = totalSteps <= 1 ? 1 : totalSteps - 1;
            double height = 0.0, peak = 0.0;
            for (int k = 0; k < totalSteps; k++)
            {
                double y = launchY + (totalSteps > 1 ? (double)(descentY - launchY) * k / interp : 0.0);
                if (height - y < 0) break;   // the descent has brought it back to the ground
                height -= y;                 // engine y is positive DOWNWARD
                if (height > peak) peak = height;
            }
            return peak;
        }

        /// <summary>
        /// The launch velocity that makes an arc of <paramref name="totalSteps"/> steps rise about
        /// <see cref="JumpPeakPx"/>. An integer search rather than the closed form, because the quantity that
        /// has to be right is the SUM the engine actually accumulates, not its continuous approximation, and
        /// the search is over 37 candidates once per animation. Ties go to the gentler launch.
        /// </summary>
        public static int SolveJumpLaunchY(int totalSteps)
        {
            int best = -JumpLaunchMinMag;
            double bestErr = double.MaxValue;
            for (int mag = JumpLaunchMinMag; mag <= JumpLaunchMaxMag; mag++)
            {
                double err = Math.Abs(ArcRisePx(-mag, JumpDescentY, totalSteps) - JumpPeakPx);
                if (err < bestErr - 1e-9) { bestErr = err; best = -mag; }
            }
            return best;
        }

        /// <summary>
        /// Repeat count bringing a jump's declared sequence closest to <see cref="JumpArcSteps"/>. A jump is
        /// NOT budgeted like a walk: the loco budget measures 2.5s of travel, which on a 3-frame pose at 40ms
        /// hit the repeat ceiling and produced the 21-step, 72px fling. Ties go to the smaller repeat, so a
        /// source that already has enough frames is played once rather than looped over its own apex.
        /// </summary>
        public static int JumpRepeatCount(int frameCount, int repeatFrom)
        {
            if (frameCount < 1) return 0;
            int rf = Math.Max(0, Math.Min(frameCount - 1, repeatFrom));
            int span = Math.Max(1, frameCount - rf);
            int best = 0, bestErr = int.MaxValue;
            for (int repeat = 0; repeat <= MaxJumpRepeats; repeat++)
            {
                int err = Math.Abs(frameCount + span * repeat - JumpArcSteps);
                if (err < bestErr) { bestErr = err; best = repeat; }
            }
            return best;
        }

        /// <summary>
        /// Steps the emitted jump will actually declare, which is what the launch velocity must be solved
        /// against. Mirrors <c>AnimationRuntimeLimits.CalculateTotalSteps</c> for the repeat this emitter
        /// chooses; the emitter always writes <c>repeatfrom="0"</c>, so the span is the whole frame list.
        /// Reading the count back from the two helpers rather than recomputing it is deliberate: an arc solved
        /// for a different number of steps than the sequence declares reaches the wrong height, silently.
        /// </summary>
        public static int JumpStepCount(int frameCount)
        {
            if (frameCount < 1) return 1;
            return frameCount * (1 + JumpRepeatCount(frameCount, 0));
        }

        /// <summary>
        /// A jump's horizontal velocity, bounded so the arc cannot cross more than
        /// <see cref="JumpSpanPx"/> before it lands. Sign and any smaller value are kept, so a vertical hop
        /// stays vertical and a gentle forward jump is untouched.
        /// </summary>
        public static int ClampJumpVelX(int velX, int totalSteps)
        {
            int max = Math.Max(1, JumpSpanPx / Math.Max(1, totalSteps));
            if (velX > max) return max;
            if (velX < -max) return -max;
            return velX;
        }

        /// <summary>
        /// A jump's per-frame interval: FLAT, and set from the airtime budget rather than inherited.
        ///
        /// Flat is the load-bearing part. The interval is interpolated start->end like everything else, and
        /// Hornet's Grapple4 inherited its source's 80ms -> 4000ms ramp: three steps, of which the middle one
        /// held her motionless 12px off the ground for two seconds before the third brought her down. An arc
        /// is the one thing in the format that must not change pace.
        /// </summary>
        public static int JumpInterval(int totalSteps)
        {
            int ms = (int)Math.Round((double)JumpTargetMs / Math.Max(1, totalSteps),
                MidpointRounding.AwayFromZero);
            if (ms < MinInterval) ms = MinInterval;
            if (ms > MaxInterval) ms = MaxInterval;
            return ms;
        }

        // Passes the drag sequence repeats before it would end on its own. At ~100ms a frame that is minutes
        // of holding, which is the point: the animation must never run out while the pet is still held.
        private const int DragRepeatPasses = 240;

        // The poses the converter will actually use (floor + wall + ceiling animations, plus the fall/drag
        // sources), so the compositor sizes the sheet to exactly those frames and keeps the cell tight.
        //
        // The cell is sized as max(AnchorY), which the FLOOR poses set at 128 in the reference conf. Wall
        // poses share that anchor, so they were free. Ceiling poses anchor at 48 and are marked AnchorToTop
        // here, which makes them span (height - 48) = 80px downward from the cell top: comfortably inside the
        // 128 the floor already needs, and they never raise max(AnchorY), so admitting them cannot pad the
        // cell. That padding is the exact failure the old exclusion existed to avoid (pet floats, ground
        // detection breaks), so it is asserted in the self-test rather than left to this comment.
        public static List<ShimejiPose> PosesToComposite(ShimejiConfig config)
        {
            var poses = new List<ShimejiPose>();
            ShimejiAction fall = FirstWithClass(config, "Fall");
            ShimejiAction drag = FirstWithClass(config, "Dragged");
            foreach (ShimejiAction a in config.Actions)
            {
                bool ceiling = IsCeilingAction(a);
                // Gaze belongs here explicitly. It is not a floor action by IsFloorAction's reckoning (a cursor
                // condition makes it Group2), so leaving it out meant its sprite was never drawn into the sheet,
                // FramesOf found no matching key, and the spoke was dropped for having zero frames -- silently,
                // one step before the faceCursor tag that was supposed to be the whole point.
                if (!(IsFloorAction(a) || IsWallAction(a) || ceiling || IsGazeAction(a) || a == fall || a == drag)) continue;

                // The DRAG action is the one place every <Animation> block matters, not just the first. Its
                // blocks are not alternatives to choose between -- they are the frames of a SWING, one per
                // horizontal offset band between the pet's body and the cursor. Compositing only Animations[0]
                // is why a dragged pet used to hang frozen in a single extreme pose.
                if (a == drag)
                {
                    foreach (ShimejiAnimation swingFrame in a.Animations)
                        foreach (ShimejiPose p in swingFrame.Poses)
                            poses.Add(p);
                    continue;
                }

                ShimejiAnimation variant = VariantFor(a);
                if (variant != null)
                    foreach (ShimejiPose p in variant.Poses)
                    {
                        // Marked on the shared pose object on purpose: FramesOf later looks the frame up by
                        // FrameKey, and the flag is part of that key, so the tile the emitter references and
                        // the tile the compositor drew have to agree.
                        if (ceiling) p.AnchorToTop = true;
                        poses.Add(p);
                    }
            }
            return poses;
        }

        /// <summary>
        /// The drag animation's frames as a SWING ARC, one per pose variant, ordered body-far-left to
        /// body-far-right.
        ///
        /// Every other action treats multiple &lt;Animation&gt; blocks as conditional ALTERNATIVES and takes
        /// the first. Drag is the exception: its blocks are the frames of one motion, each gated on a band of
        /// horizontal offset between the pet and the cursor
        /// (<c>#{FootX &lt; cursor.x-120}</c>, <c>-30</c>, <c>-10</c>, centred, <c>+30</c>, ...). Taking only
        /// the first left a dragged pet frozen in the furthest-left pose.
        ///
        /// SOURCE ORDER IS THE SWING ORDER, and that is not a coincidence to be re-derived: Shimeji evaluates
        /// these conditions top to bottom and takes the first match, so an author MUST write them in
        /// ascending offset order for the cascade to work at all. Parsing the thresholds back out would add a
        /// fragile expression parser to re-learn something the file already guarantees.
        ///
        /// One frame per variant: a variant with several poses is a sub-animation of its own, and mixing those
        /// into the arc would break the index-to-offset mapping the host relies on.
        /// </summary>
        private static List<int> DragSwingFramesOf(ShimejiAction a, SpriteSheet sheet)
        {
            var frames = new List<int>();
            if (a == null) return frames;
            foreach (ShimejiAnimation variant in a.Animations)
            {
                if (variant == null || variant.Poses.Count == 0) continue;
                int index;
                if (sheet.FrameIndexByKey.TryGetValue(variant.Poses[0].FrameKey, out index))
                    frames.Add(index);
            }
            // A single-variant drag (most Android bundles) behaves exactly as before: one frame, no swing.
            return frames;
        }

        private static List<int> FramesOf(ShimejiAction a, SpriteSheet sheet)
        {
            var frames = new List<int>();
            ShimejiAnimation variant = VariantFor(a);
            if (variant == null) return frames;
            // One animation only: multiple <Animation> blocks are conditional alternatives, not a sequence.
            // WHICH one is VariantFor's business, and it must be the same choice PosesToComposite made or the
            // frame key looked up here was never drawn into the sheet.
            foreach (ShimejiPose p in variant.Poses)
            {
                int idx;
                if (p != null && !string.IsNullOrEmpty(p.Image) && sheet.FrameIndexByKey.TryGetValue(p.FrameKey, out idx))
                    frames.Add(idx);
            }
            return frames;
        }

        /// <summary>
        /// A resting pose: a Shimeji Stay action (Stand, Sit, Sprawl, SitWithLegsUp, GrabWall...) as opposed
        /// to an Animate performance. Type is the right discriminator because it is the source's own
        /// statement of intent: Stay means "hold this", Animate means "play this through".
        /// </summary>
        private static bool IsRestingPose(Emitted e)
        {
            if (e.Source == null) return false;
            if (IsLocomotion(e)) return false;
            return string.Equals(e.Source.Type, "Stay", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLocomotion(Emitted e)
        {
            if (e.Source == null || e.Source.Animations.Count == 0) return false;
            foreach (ShimejiPose p in e.Source.Animations[0].Poses)
                if (p.VelX != 0 || p.VelY != 0) return true;
            return false;
        }

        /// <summary>Does this wall action actually move UP the wall? Used to prefer a climb over a static grab
        /// as the wall entry point.</summary>
        private static bool ClimbsUpward(ShimejiAction a)
        {
            if (a == null || a.Animations.Count == 0) return false;
            foreach (ShimejiPose p in a.Animations[0].Poses)
                if (p != null && p.VelY < 0) return true;
            return false;
        }

        // Relative weights on a locomotion animation's border edge. The turn is only="none" so it is eligible
        // at EVERY border; the climb is only="vertical" so it competes only at a left/right screen edge, where
        // these weights make it win 1 in 3. Everywhere else the pet still just turns around.
        private const int BorderTurnWeight = 2;
        private const int BorderClimbWeight = 1;

        // How long one wall sequence should last before the pet re-decides (climb on / grab / let go).
        // A TIME budget, not a fixed repeat count. The first version used a fixed 3, which is the exact
        // mistake TargetLocoMs exists to prevent: on Hornet's 32-frame climb at 640..160ms that produced a
        // FIFTY-ONE SECOND sequence inching up ~256px, so the pet appeared stuck to the wall.
        //
        // Still used for a STATIC hold (a grab pose), where a time budget is exactly right. A pose that
        // TRAVELS along the surface uses the reach budget below instead.
        private const int TargetWallMs = 5000;
        private const int MaxWallRepeats = 6;

        // ---- crossing a surface in ONE sequence ----
        //
        // Read off yellow_sheep's own wall animations, and the shape is not what it looks like. Its climbs are
        // NOT fast: walk_up rises 2px per step at 150ms, which is 13px/s -- slower per pixel than Hornet's
        // converted climb. What it does differently is REPEAT ~20000 times, so one sequence covers the whole
        // wall and the "keep climbing / let go" roll never happens mid-climb. roll_up (8008 steps) and chasew2
        // (3003) are the same trick.
        //
        // The converted pets budgeted by TIME instead, so Hornet's 32-frame climb ran once, covered 32px, and
        // then rolled a 34% chance of letting go -- every 12.8 seconds. Reaching the top of a 940px screen took
        // 30 consecutive survivals: 1 in 203,000 wall entries, about one visit per FIVE YEARS of running. No
        // converted pet had ever touched the ceiling, which is why the region looked like it worked and did not.
        //
        // So: a constant speed, a flat interval, and enough repeats to cross any realistic screen. The surplus
        // repeats cost nothing, because the pet stops at the border long before the sequence ends. That is
        // exactly why the sheep can afford 20000 of them.
        //
        // 6px per 100ms is 60px/s, the middle of the sheep's own 13-100px/s band (chasew2 53, wall_slide_run
        // 57, king_run_up and roll_up 100).
        private const int SurfacePxPerStep = 6;
        private const int SurfaceIntervalMs = 100;
        /// <summary>Distance one pass must be able to cover: a 4K screen's longer side, with margin, so the
        /// same number serves a vertical climb and a horizontal ceiling walk on any display.</summary>
        private const int SurfaceReachPx = 4000;
        private const int MaxSurfaceRepeats = 500;

        /// <summary>
        /// Repeat count so one pass covers <see cref="SurfaceReachPx"/> at <see cref="SurfacePxPerStep"/>.
        ///
        /// Public because the `reclimb` migration applies the identical policy to already-emitted pets, and two
        /// implementations of one number would drift.
        /// </summary>
        public static int SurfaceRepeatForReach(int frameCount)
        {
            if (frameCount < 1) return 0;
            int stepsNeeded = (SurfaceReachPx + SurfacePxPerStep - 1) / SurfacePxPerStep;
            // TotalSteps is frameCount * (1 + repeat) at repeatfrom=0, so solve for repeat and round UP: a pass
            // that falls short of the reach is a pass that rolls the let-go dice mid-climb.
            int repeat = (stepsNeeded + frameCount - 1) / frameCount - 1;
            if (repeat < 0) repeat = 0;
            if (repeat > MaxSurfaceRepeats) repeat = MaxSurfaceRepeats;
            return repeat;
        }

        /// <summary>Distance one emitted pass actually covers, for the self-test to assert against the reach
        /// rather than against the repeat count.</summary>
        public static int SurfaceReachOf(int frameCount, int repeat)
        {
            if (frameCount < 1) return 0;
            return frameCount * (1 + Math.Max(0, repeat)) * SurfacePxPerStep;
        }

        // At the TOP of a climb: carry on across the ceiling, or let go. Weighted so the ceiling wins roughly
        // 2 in 3, because reaching the top is already the rare outcome of a 1-in-3 wall entry and a ceiling
        // walk is the payoff for it. Both edges live on the wall climb spoke only.
        private const int BorderCeilingWeight = 2;
        private const int BorderTopReleaseWeight = 1;

        // Walking off the SIDE of a window: grip it, or turn round. Against BorderTurnWeight = 2 this makes
        // the grip win 1 in 3, the same odds as entering the wall at a screen edge, and for the same reason:
        // it should be a thing the pet sometimes does, not the thing it always does. A pet that gripped every
        // window edge it met would spend its life on the outside of a browser window.
        private const int BorderWindowGripWeight = 1;
        // Arriving at the top of a window while climbing its side: come over the lip and stand, or let go.
        // Weighted toward standing, because getting there took a 1-in-3 grip and a whole climb, and dropping
        // off at the last moment throws that away.
        private const int BorderWindowTopWeight = 3;
        // Reaching the end of a window's UNDERSIDE while hanging: swing onto the side of the frame, or drop.
        // It competes with a fall edge weighted 100, so this is deliberately generous -- a pet that walked
        // the length of an overhang and then simply fell off every time would look like it had run out of
        // ideas rather than made a decision.
        private const int BorderWindowCornerWeight = 200;

        /// <summary>
        /// One wall animation. Two things make this different from a floor spoke, and both are load-bearing:
        ///
        ///   * NO &lt;gravity&gt; element. Presence of that element is what tells the engine to fall when
        ///     nothing is underneath, so OMITTING it is precisely what keeps the pet clinging to the wall.
        ///     This is how the hand-authored sheep do it (wall_slide has no gravity node).
        ///   * The border edge is only="none" -> fall, so reaching the top, the taskbar or a window edge means
        ///     letting go. Ceiling behaviour will later refine the only="horizontal" case specifically.
        /// </summary>
        private static AnimationNode BuildWallSpoke(Emitted e, Emitted fall, IList<Emitted> wallSpokes, Emitted ceilingEntry, Emitted hub)
        {
            List<ShimejiPose> poses = e.Source != null && e.Source.Animations.Count > 0
                ? e.Source.Animations[0].Poses : new List<ShimejiPose>();
            // Only the VERTICAL component is kept: horizontal motion on a wall would walk the pet off it, so
            // the source's VelX is read and discarded on purpose rather than never read.
            // ForcedVelY wins: this spoke is a static grab pose being animated upward because the skin lost
            // its real climb (see SynthesiseClimbIfNeeded).
            int vy0 = e.ForcedVelY ?? (poses.Count > 0 ? poses[0].VelY : 0);
            int vyN = e.ForcedVelY ?? (poses.Count > 0 ? poses[poses.Count - 1].VelY : 0);
            int iv0 = poses.Count > 0 ? Interval(poses[0].Duration) : 200;
            int ivN = poses.Count > 0 ? Interval(poses[poses.Count - 1].Duration) : 200;
            int repeat = RepeatCountForBudget(SequencePassMs(poses), TargetWallMs, MaxWallRepeats);

            // A pose that CLIMBS crosses the wall in one sequence; a static grab keeps its time budget, because
            // a hold is meant to end and re-decide. Constant speed rather than the source's ramp for the same
            // reason BuildFall uses one: the sequence self-loops, and a ramp snaps back to the slow start speed
            // on every loop and visibly pulses. Hornet's source ramps 0 -> -2, which also halved its average
            // speed to 1px per step.
            // ANY vertical motion crosses, not just upward: a DESCENDING wall pose is the one a pet uses to
            // climb back down, and leaving it short means descending 56px and then rolling the same let-go
            // dice. Direction is preserved, or every descent would be silently turned into a climb.
            bool travels = vy0 != 0 || vyN != 0;
            if (travels)
            {
                int direction = (vy0 != 0 ? vy0 : vyN) < 0 ? -1 : 1;
                vy0 = vyN = direction * SurfacePxPerStep;
                iv0 = ivN = SurfaceIntervalMs;
                repeat = SurfaceRepeatForReach(e.Frames.Count);
            }

            var next = new List<NextNode>();
            foreach (Emitted other in wallSpokes)
                next.Add(Next(other.Id, other == e ? 60 : 20, "none"));   // keep climbing, or switch wall pose
            if (fall != null) next.Add(Next(fall.Id, 25, "none"));        // or let go

            var node = new AnimationNode
            {
                Id = e.Id,
                Name = e.Name,
                Start = Moving(0, vy0, iv0, 1.0),
                End = Moving(0, vyN, ivN, 1.0),
                Sequence = new SequenceNode
                {
                    RepeatFromFrame = 0,
                    RepeatCount = repeat.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Frame = e.Frames.ToArray(),
                    Next = next.ToArray(),
                },
            };
            // Deliberately no Gravity: that is the cling.
            //
            // Border edges, most specific first. only="horizontal" is the TOP of the screen, and it is
            // attached HERE and nowhere else: that is what makes the ceiling reachable by climbing and
            // unreachable any other way. A floor animation could not use it even if it had the edge, because
            // IsFloorAction rejects upward velocity, so nothing on the floor ever travels up to meet it.
            var border = new List<NextNode>();
            bool climbs = ClimbsUpward(e.Source);
            if (ceilingEntry != null && climbs)
                border.Add(Next(ceilingEntry.Id, BorderCeilingWeight, "horizontal"));
            // The top of a WINDOW is a surface the pet can stand on, not a dead end, so a pet climbing the
            // side of one comes over the lip and walks along the title bar. Only a climbing spoke can arrive
            // here: the host checks the window's BOTTOM for downward motion, so a descending pose reaching
            // the top edge is not a state that exists.
            if (hub != null && climbs)
                border.Add(Next(hub.Id, BorderWindowTopWeight, "window-top"));
            if (fall != null)
            {
                // 100 only when letting go is the ONLY thing on offer -- a screen top on a skin with no
                // ceiling art. Where something else is eligible this competes with it instead, which is
                // what keeps the pre-existing 2:1 ceiling-vs-fall split at the screen top exactly as it was.
                border.Add(Next(fall.Id, border.Count > 0 ? BorderTopReleaseWeight : 100, "none"));
            }
            if (border.Count > 0) node.Border = new HitNode { Next = border.ToArray() };
            return node;
        }

        /// <summary>
        /// One ceiling animation. Structurally a wall spoke turned through ninety degrees:
        ///
        ///   * NO &lt;gravity&gt;, which is the cling, exactly as on the wall.
        ///   * Only the HORIZONTAL velocity component is kept. Vertical motion while pinned to the ceiling
        ///     would either fight the engine's PositionY pin or drop the pet, and dropping is what the
        ///     weighted fall edge is for.
        ///   * only="vertical" leaves for the wall at a left/right screen edge, so a pet that crosses the
        ///     whole ceiling climbs back DOWN a wall instead of vanishing. Falls back to the fall animation
        ///     when the skin has no wall region to return to.
        /// </summary>
        private static AnimationNode BuildCeilingSpoke(Emitted e, Emitted fall, Emitted wallExit, IList<Emitted> ceilingSpokes)
        {
            List<ShimejiPose> poses = e.Source != null && e.Source.Animations.Count > 0
                ? e.Source.Animations[0].Poses : new List<ShimejiPose>();
            int vx0 = poses.Count > 0 ? poses[0].VelX : 0;
            int vxN = poses.Count > 0 ? poses[poses.Count - 1].VelX : 0;
            int iv0 = poses.Count > 0 ? Interval(poses[0].Duration) : 200;
            int ivN = poses.Count > 0 ? Interval(poses[poses.Count - 1].Duration) : 200;
            int repeat = RepeatCountForBudget(SequencePassMs(poses), TargetWallMs, MaxWallRepeats);

            // Same reach budget as the wall, turned through ninety degrees: a ceiling walk that stops every
            // 32px rolls the let-go dice before it can reach a corner, so it never finds the only="vertical"
            // edge that would take it back down a wall. A static hang keeps its time budget.
            bool travels = vx0 != 0 || vxN != 0;
            if (travels)
            {
                int direction = (vx0 != 0 ? vx0 : vxN) < 0 ? -1 : 1;
                vx0 = vxN = direction * SurfacePxPerStep;
                iv0 = ivN = SurfaceIntervalMs;
                repeat = SurfaceRepeatForReach(e.Frames.Count);
            }

            var next = new List<NextNode>();
            foreach (Emitted other in ceilingSpokes)
                next.Add(Next(other.Id, other == e ? 60 : 20, "none"));   // keep going, or switch ceiling pose
            if (fall != null) next.Add(Next(fall.Id, 25, "none"));        // or let go

            var node = new AnimationNode
            {
                Id = e.Id,
                Name = e.Name,
                Start = Moving(vx0, 0, iv0, 1.0),
                End = Moving(vxN, 0, ivN, 1.0),
                Sequence = new SequenceNode
                {
                    RepeatFromFrame = 0,
                    RepeatCount = repeat.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Frame = e.Frames.ToArray(),
                    Next = next.ToArray(),
                },
            };
            // Deliberately no Gravity: that is the cling.
            var border = new List<NextNode>();
            if (wallExit != null)
            {
                border.Add(Next(wallExit.Id, 100, "vertical"));
                // ...and the corners of a WINDOW's underside, which are the same situation one scale down:
                // walk to the end of the overhang and swing onto the side of the frame. Without these a pet
                // hanging under a window reaches the corner and its only option is only="none", i.e. drop.
                border.Add(Next(wallExit.Id, BorderWindowCornerWeight, "window-left"));
                border.Add(Next(wallExit.Id, BorderWindowCornerWeight, "window-right"));
            }
            if (fall != null) border.Add(Next(fall.Id, 100, "none"));
            if (border.Count > 0) node.Border = new HitNode { Next = border.ToArray() };
            return node;
        }

        private static AnimationNode BuildSpoke(Emitted e, Emitted hub, Emitted fall, Emitted turn, Emitted wallEntry, Emitted wallExit, Emitted ceilingEntry, Emitted landRun)
        {
            List<ShimejiPose> poses = e.Source != null && e.Source.Animations.Count > 0
                ? e.Source.Animations[0].Poses : new List<ShimejiPose>();
            int vx0 = poses.Count > 0 ? poses[0].VelX : 0;
            int vy0 = poses.Count > 0 ? poses[0].VelY : 0;
            int vxN = poses.Count > 0 ? poses[poses.Count - 1].VelX : 0;
            int vyN = poses.Count > 0 ? poses[poses.Count - 1].VelY : 0;
            int iv0 = poses.Count > 0 ? Interval(poses[0].Duration) : 200;
            int ivN = poses.Count > 0 ? Interval(poses[poses.Count - 1].Duration) : 200;
            bool loco = IsLocomotion(e);

            // PHASE 1 of a jump, the LAUNCH. Nothing about the arc is inherited: the launch velocity is solved
            // from the step count so the pet always rises about JumpPeakPx, the descent is forced, and the
            // interval is flat. Passing the source's numbers through is what made the shipped jumps range from
            // an 11px twitch to a 72px fling with a two-second mid-air freeze in the middle of one of them.
            bool jump = Launches(e);
            int jumpSteps = 0;
            if (jump)
            {
                jumpSteps = JumpStepCount(e.Frames.Count);
                vy0 = SolveJumpLaunchY(jumpSteps);
                vyN = JumpDescentY;
                iv0 = ivN = JumpInterval(jumpSteps);
                vx0 = ClampJumpVelX(vx0, jumpSteps);
                vxN = ClampJumpVelX(vxN, jumpSteps);
            }
            else if (vy0 < 0 || vyN < 0)
            {
                // Rises, but not hard enough to be a jump (Grapple1 at -5, `fly` at -5). The rise is dropped
                // rather than the animation: on the floor an upward velocity with no arc behind it is either a
                // twitch or, before jumps existed at all, a reason to refuse the action outright. Flat keeps
                // the sprites and reports the loss in the residue.
                if (vy0 < 0) vy0 = 0;
                if (vyN < 0) vyN = 0;
            }

            NextNode[] next;
            if (e == hub) next = HubChoices(hub);
            // PHASE 2 of a jump, the DESCENT. A jump hands to `fall` rather than looping on itself or
            // returning to a standing hub, which is the shape yellow_sheep uses (`jump` -> `jump_down2`, a
            // dedicated falling pose that keeps going until something is underneath). It matters only when the
            // arc outlives the drop -- jumping off a window, or a source sequence longer than its own airtime
            // -- but in exactly that case the old edges put a STANDING pose in mid-air.
            else if (jump) next = new[] { Next(fall.Id, 100, "none") };
            else if (loco) next = new[] { Next(e.Id, 65, "none"), Next(hub.Id, 35, "none") }; // keep walking (same heading), or return to the hub to re-decide
            else next = new[] { Next(hub.Id, 100, "none") };

            // Four cases: an arc to a fixed height, a walk to a distance budget, a REST to a dwell, and a
            // one-shot performance (Animate: a trip, a bounce, a needle throw) which plays exactly once.
            int repeatCount;
            if (jump)
            {
                repeatCount = JumpRepeatCount(e.Frames.Count, 0);
            }
            else if (loco)
            {
                repeatCount = LocoRepeatCount(SequencePassMs(poses));
            }
            else if (IsRestingPose(e))
            {
                // The hub is brief; every other rest is a performance and lingers.
                int target = e == hub ? HubDwellMs : RestDwellMs;
                if (e.Frames.Count == 1)
                {
                    // The single frame's interval IS the dwell, so choose it to hit the target directly.
                    int restInterval;
                    SingleFrameRestTiming(target, out restInterval, out repeatCount);
                    iv0 = restInterval;
                    ivN = restInterval;
                }
                else
                {
                    // A cycle: cap each frame's interval so a "hold for 3s" baked into a source interval cannot
                    // freeze the animation, then repeat the capped pass to reach the target. Nearest rather
                    // than up, so a long performance lands inside the 9-12s band instead of overshooting. The
                    // repeat estimate uses the CAPPED intervals (the ones the engine actually plays).
                    iv0 = Math.Min(iv0, MaxRestIntervalMs);
                    ivN = Math.Min(ivN, MaxRestIntervalMs);
                    int cappedPassMs = e.Frames.Count * ((iv0 + ivN) / 2);
                    repeatCount = RepeatCountForBudget(cappedPassMs, target, MaxRestRepeats, false);
                }
            }
            else
            {
                repeatCount = 0;
            }

            var node = new AnimationNode
            {
                Id = e.Id,
                Name = e.Name,
                Start = Moving(vx0, vy0, iv0, 1.0),
                End = Moving(vxN, vyN, ivN, 1.0),
                Sequence = new SequenceNode
                {
                    RepeatFromFrame = 0,
                    RepeatCount = repeatCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Frame = e.Frames.ToArray(),
                    Next = next,
                    // A gaze pose is aimed at the pointer when it starts. Without this the animation still
                    // plays, but facing whichever way the pet happened to be looking, which is the whole
                    // point of "sit and look at the mouse" missed.
                    Action = e.IsGaze ? "faceCursor" : null,
                },
            };
            // Gravity routes to `fall` the moment nothing is underneath -- correct for a walk that steps off
            // an edge, fatal for a jump, which is airborne by design and would be cut off at frame one. Not
                // one of yellow_sheep's 22 upward animations carries a gravity node, for exactly this reason.
            // The arc's forced descent brings the pet down instead, and `fall`'s own border edge lands it.
            if (fall != null && e != fall && !jump)
                node.Gravity = new HitNode { Next = new[] { Next(fall.Id, 100, "none") } };
            if (loco)
            {
                // Reach an edge -> turn (flip) and head back. At a LEFT/RIGHT screen edge specifically, the
                // pet may instead grab the wall and climb: both entries are eligible there, so the weights
                // decide (climb wins 1 in 3). At any other border only the only="none" turn matches, so
                // behaviour away from the walls is exactly what it was.
                var borderNext = new List<NextNode> { Next(turn.Id, BorderTurnWeight, "none") };
                if (wallEntry != null) borderNext.Add(Next(wallEntry.Id, BorderClimbWeight, "vertical"));
                // The SIDE of a window is the same surface as a screen edge as far as the art is concerned,
                // and every converted pet already carries wall poses it could previously only use at the two
                // screen edges. This is the entry: walk off the side of a window you are standing on and
                // grip it instead of turning round.
                //
                // The DESCENDING pose, not the climbing one. Entering on a climb would send the pet straight
                // up into the window's top edge it just left, come over the lip, and put it back where it
                // started -- a loop that costs a tick and shows nothing. Going down is the behaviour worth
                // having, and the wall spokes chain among themselves, so it can still turn and climb back up.
                if (wallExit != null)
                {
                    borderNext.Add(Next(wallExit.Id, BorderWindowGripWeight, "window-left"));
                    borderNext.Add(Next(wallExit.Id, BorderWindowGripWeight, "window-right"));
                }
                // And the UNDERSIDE of a window, reachable only by jumping into it. Attached to jump spokes
                // and nowhere else, which is the same discipline the ceiling region uses at the screen top:
                // a walk cannot travel upward, so it can never meet this border however the edge is written,
                // but stating it here means the graph says so rather than relying on the physics to.
                if (ceilingEntry != null && jump)
                    borderNext.Add(Next(ceilingEntry.Id, 100, "window-bottom"));
                // PHASE 3 of a jump, the LANDING -- on only="taskbar" specifically, because the floor is the
                // surface a jump from the floor arrives back at. A descent that instead meets a window top
                // belongs to `fall`, which is where phase 2 sends it and which already has that edge.
                //
                // Without these the ONLY floor-eligible edge here is the only="none" turn, so every landing
                // was a facing flip into the hub's idle dwell: measured on Hornet, 30 of 31 landings went
                // turn -> Stand and then sat down about a third of the time. Re-jumping is what lets a
                // converted pet chain hops the way the sheep does, and it is unreachable any other way: the
                // taskbar border fires long before the sequence ends (12 steps of 28 on Grapple1), so the
                // locomotion self-edge a jump used to carry could never fire.
                if (jump)
                {
                    borderNext.Add(Next(e.Id, LandRejumpWeight, "taskbar"));
                    if (landRun != null && landRun != e)
                        borderNext.Add(Next(landRun.Id, LandRunWeight, "taskbar"));
                }
                node.Border = new HitNode { Next = borderNext.ToArray() };
            }
            return node;
        }

        // Flip facing, then return to the hub (which will pick a walk that now heads the other way).
        private static AnimationNode BuildTurn(Emitted turn, Emitted hub)
        {
            return new AnimationNode
            {
                Id = turn.Id,
                Name = turn.Name,
                Start = Moving(0, 0, 120, 1.0),
                End = Moving(0, 0, 120, 1.0),
                Sequence = new SequenceNode
                {
                    RepeatFromFrame = 0,
                    RepeatCount = "0",
                    Frame = turn.Frames.ToArray(),
                    Action = "flip",
                    Next = new[] { Next(hub.Id, 100, "none") },
                },
            };
        }

        // The hub can reach every spoke (so nothing is orphaned), weighted by each spoke's source behaviour
        // frequency: a character that walks and runs a lot in Shimeji does so here too, instead of a flat pick
        // that gave its many idle poses the same odds as its few movement ones (the "shuffles animations but
        // never goes anywhere" report).
        private static NextNode[] HubChoices(Emitted hub)
        {
            var list = new List<NextNode>();
            // handled by caller passing the full spoke list via a closure-free field:
            foreach (Emitted s in HubSpokes)
                list.Add(Next(s.Id, Math.Max(1, s.Weight), "none"));
            if (list.Count == 0) list.Add(Next(hub.Id, 100, "none"));
            return list.ToArray();
        }

        // The hub picks its next action weighted by the source's behaviour frequency (ShimejiConfig
        // .BehaviorFrequency; a behaviour's name is the action it runs), plus a small baseline so every spoke
        // stays reachable -- the acceptance check requires it -- even one the source only ever reaches as a
        // transition (Frequency 0 at root). The hub's OWN re-selection stays at the baseline: it is also every
        // spoke's return target, and folding the stand behaviour's often-high frequency back in here would just
        // make the pet loiter on the hub between actions rather than getting on with the next one.
        /// <summary>The header author every converted pet carries. The reweight migration matches on this to
        /// be certain it only ever touches converter output, never a hand-authored pet.</summary>
        public const string ConvertedAuthor = "Converted from a Shimeji skin";

        /// <summary>Header version stamped by the CURRENT emitter. See BuildHeader for why it matters.</summary>
        // 1.0 flat hub weights -> 1.1 damped+floored weights -> 1.2 adds the ceiling region -> 1.3 gives the
        // jump a solved arc, a descent and a landing -> 1.4 lets a climb CROSS the wall in one sequence, so
        // the ceiling is reachable at all -> 1.5 shortened EVERY rest to ~1.2s (which over-corrected: it also
        // cut the performances you want to watch) -> 1.6 splits the dwell by role: the hub is brief so the pet
        // does not loiter, performances linger 9-12s -> 1.7 drops sprite cells that are byte-identical to
        // another cell, which the sheet builder used to keep because it deduped by image NAME and two source
        // files can hold the same picture -> 1.8 drops the _left/_right suffix from an action name, because a
        // converted pet mirrors its whole sheet on `<action>flip</action>` and every animation therefore
        // plays in both directions. Each migration rewrites exactly one version and skips the rest, which is
        // what makes a run idempotent.
        public const string ConvertedFormatVersion = "1.8";

        /// <summary>The version emitted before the hub weighting was damped and floored; what the reweight
        /// migration looks for.</summary>
        public const string ConvertedFormatVersionFlatWeights = "1.0";

        /// <summary>What the reweight migration STAMPS, which is its own version rather than whatever the
        /// emitter is currently on. It fixes the hub weights and nothing else, so stamping the latest would
        /// claim the ceiling and the jump arc for a pet that has neither, and every later migration gates on
        /// an exact version and would then skip it.</summary>
        public const string ConvertedFormatVersionDampedWeights = "1.1";

        /// <summary>The version emitted while a jump was the source's own velocities clamped into a bounded
        /// arc, before the arc was solved for a height; what the `rejump` migration looks for.</summary>
        public const string ConvertedFormatVersionLooseJumps = "1.2";

        /// <summary>The version emitted while a wall climb was budgeted by TIME, so it covered ~32px and then
        /// rolled a 34% chance of letting go; what the `reclimb` migration looks for.</summary>
        public const string ConvertedFormatVersionShortClimbs = "1.3";

        /// <summary>The version emitted while a rest held ~9s and single-frame poses held 10s, so a pet stood
        /// idle 79% of the time; what the `restdwell` migration looks for.</summary>
        public const string ConvertedFormatVersionLongRests = "1.4";

        /// <summary>The version emitted while EVERY rest was ~1.2s, cutting the performances short; what the
        /// `restsplit` migration looks for, to restore long performances while keeping the hub brief.</summary>
        public const string ConvertedFormatVersionFlatRests = "1.5";

        /// <summary>The version emitted while the sheet could hold two byte-identical cells, because
        /// <see cref="Shimeji.SpriteSheetBuilder"/> deduped poses by image NAME and a skin can ship the same
        /// picture under several filenames; what the `dedupe` migration looks for.</summary>
        public const string ConvertedFormatVersionDuplicateCells = "1.6";

        /// <summary>The version emitted while an action still carried a `_left` / `_right` suffix taken
        /// verbatim from the source skin, which reads as a restriction the pet does not have; what the
        /// `undirect` migration looks for.</summary>
        public const string ConvertedFormatVersionDirectionalNames = "1.7";

        /// <summary>Per-step travel a crossing surface pose is given. Public for the migration.</summary>
        public static int SurfaceStepPx { get { return SurfacePxPerStep; } }

        /// <summary>Flat interval a crossing surface pose is given. Public for the migration.</summary>
        public static int SurfaceStepIntervalMs { get { return SurfaceIntervalMs; } }

        public const int HubBaseWeight = 4;
        private const int MaxResolveDepth = 8;   // guard the reference walk against deep or cyclic composites

        /// <summary>Smallest share of the hub's pool any single spoke may have, as a percentage.</summary>
        public const double HubMinimumSharePercent = 1.5;

        /// <summary>Damping applied to a source frequency before it becomes a weight.</summary>
        private const double HubFrequencyScale = 3.0;

        /// <summary>
        /// Turn an accumulated source behaviour frequency into a hub selection weight.
        ///
        /// The square root is the point. BuildSpokeWeights SUMS a frequency every time a behaviour references
        /// an action, so locomotion (referenced by many composites) accumulated to ~1100 while a one-off pose
        /// stayed at the baseline of 4 -- a 326x spread nobody chose, which fell out of the summing. Taking the
        /// root keeps the ORDERING (a character that walks a lot still walks a lot) while collapsing the range
        /// to roughly 10-25x, and the minimum-share pass then bounds the tail.
        ///
        /// Public because the reweight migration must apply the identical curve to already-emitted pets; two
        /// implementations of this would drift, exactly as two copies of the walk-time budget would.
        /// </summary>
        public static int HubWeightFromFrequency(int frequency)
        {
            if (frequency <= 0) return HubBaseWeight;
            return HubBaseWeight + (int)Math.Round(HubFrequencyScale * Math.Sqrt(frequency),
                MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Raise the smallest weights until none holds less than <see cref="HubMinimumSharePercent"/> of the
        /// pool, so every animation actually plays. Converges because lifting a floored entry raises its own
        /// share faster than it inflates the total; the iteration cap is belt-and-braces.
        ///
        /// <paramref name="excludeIndex"/> is the hub's own re-selection edge and is deliberately left at the
        /// baseline: the hub is also every spoke's RETURN target, so weighting it up makes the pet loiter on
        /// the hub between actions instead of getting on with the next one.
        /// </summary>
        public static void ApplyMinimumShare(IList<int> weights, int excludeIndex, double minimumPercent)
        {
            if (weights == null || weights.Count == 0 || minimumPercent <= 0) return;
            for (int pass = 0; pass < 64; pass++)
            {
                long total = 0;
                for (int i = 0; i < weights.Count; i++) total += weights[i];
                if (total <= 0) return;

                int need = (int)Math.Ceiling(total * minimumPercent / 100.0);
                bool changed = false;
                for (int i = 0; i < weights.Count; i++)
                {
                    if (i == excludeIndex) continue;
                    if (weights[i] < need) { weights[i] = need; changed = true; }
                }
                if (!changed) return;
            }
        }

        private static void ApplyHubMinimumShare(List<Emitted> spokes, Emitted hub)
        {
            if (spokes == null || spokes.Count == 0) return;
            var weights = new List<int>(spokes.Count);
            int hubIndex = -1;
            for (int i = 0; i < spokes.Count; i++)
            {
                weights.Add(spokes[i].Weight);
                if (spokes[i] == hub) hubIndex = i;
            }
            ApplyMinimumShare(weights, hubIndex, HubMinimumSharePercent);
            for (int i = 0; i < spokes.Count; i++) spokes[i].Weight = weights[i];
        }

        private static int HubWeightFor(Emitted e, Emitted hub, Dictionary<string, int> spokeWeights)
        {
            if (e == hub) return HubBaseWeight;
            int freq = 0;
            if (spokeWeights != null && e.Source != null && e.Source.Name != null)
                spokeWeights.TryGetValue(e.Source.Name, out freq);
            return HubWeightFromFrequency(freq);
        }

        // Turn root behaviour frequencies into per-spoke selection weights. A <Behavior Name="X" Frequency="N">
        // names action X, which is usually a Sequence/Select composite whose sprites live on the low-level
        // posed actions it references; walk those references down to the actual floor spokes and credit each
        // with N. A behaviour that already names a posed floor action credits it directly. Frequencies
        // accumulate, so an action many behaviours play (Walk is reached by walk-and-sit, walk-and-jump, ...)
        // ends up correctly dominant -- which is what makes the pet actually move.
        private static Dictionary<string, int> BuildSpokeWeights(ShimejiConfig config)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            if (config == null || config.BehaviorFrequency.Count == 0) return result;

            var byName = new Dictionary<string, ShimejiAction>(StringComparer.Ordinal);
            foreach (ShimejiAction a in config.Actions)
                if (a != null && a.Name != null && !byName.ContainsKey(a.Name)) byName[a.Name] = a;

            foreach (KeyValuePair<string, int> kv in config.BehaviorFrequency)
            {
                var spokes = new HashSet<string>(StringComparer.Ordinal);
                ResolveSpokes(kv.Key, byName, spokes, new HashSet<string>(StringComparer.Ordinal), 0);
                foreach (string spoke in spokes)
                {
                    int current;
                    result.TryGetValue(spoke, out current);
                    result[spoke] = current + kv.Value;
                }
            }
            return result;
        }

        // Resolve an action name to the set of floor spokes it (transitively) plays. A directly-posed floor
        // action IS a spoke; a composite recurses into the actions it references. Bounded depth + a visited set
        // guard malformed self-referential configs.
        private static void ResolveSpokes(string name, Dictionary<string, ShimejiAction> byName,
            HashSet<string> outSpokes, HashSet<string> visited, int depth)
        {
            if (name == null || depth > MaxResolveDepth || !visited.Add(name)) return;
            ShimejiAction a;
            if (!byName.TryGetValue(name, out a)) return;
            if (IsFloorAction(a)) { outSpokes.Add(name); return; }
            foreach (string reference in a.ReferencedActions)
                ResolveSpokes(reference, byName, outSpokes, visited, depth + 1);
        }

        // Set just before building the hub node so HubChoices can see every spoke without threading it
        // through every helper. Single-threaded emit, so a static is safe and keeps the signatures clean.
        private static List<Emitted> HubSpokes = new List<Emitted>();

        private static AnimationNode BuildFall(Emitted fall, Emitted hub)
        {
            return new AnimationNode
            {
                Id = fall.Id,
                Name = "fall",
                // Constant terminal velocity, not an accelerating start->end ramp: the sequence self-loops
                // (below), and a ramp would snap back to the slow start speed on every loop and visibly pulse.
                Start = Moving(0, 10, 40, 1.0),
                End = Moving(0, 10, 40, 1.0),
                Sequence = new SequenceNode
                {
                    RepeatFromFrame = 0,
                    RepeatCount = "20",
                    Frame = fall.Frames.ToArray(),
                    // Keep falling: when the repeats run out mid-air, restart THIS animation (a seamless 1-tick
                    // restart), never hand back to a standing hub. A hub carries gravity->fall, so that round
                    // trip is the ~2s "thinks it hit the floor then keeps falling" stutter. This is esheep64's
                    // canonical self-looping fall.
                    Next = new[] { Next(fall.Id, 100, "none") },
                },
                // The one exit: the instant the pet reaches the floor / a border, land at the hub.
                Border = new HitNode { Next = new[] { Next(hub.Id, 100, "none") } },
            };
        }

        private static AnimationNode BuildDrag(Emitted drag, Emitted fall)
        {
            return new AnimationNode
            {
                Id = drag.Id,
                Name = "drag",
                Start = Moving(0, 0, 100, 1.0),
                End = Moving(0, 0, 100, 1.0),
                Sequence = new SequenceNode
                {
                    RepeatFromFrame = 0,
                    // Long enough to outlast any realistic drag. One pass was fine when drag was a single
                    // frozen frame, but a swing arc has up to 7, so a single pass ENDED mid-drag and dropped
                    // the pet into `fall` while it was still in the user's hand. The host forces `fall` on
                    // MouseUp anyway, so the next edge below is only the belt-and-braces release path.
                    RepeatCount = DragRepeatPasses.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Frame = drag.Frames.ToArray(),
                    Next = new[] { Next(fall.Id, 100, "none") },   // released -> fall
                },
            };
        }

        private static AnimationNode BuildKill(Emitted kill)
        {
            // Fade out, then the engine closes the pet (a terminal animation with no next).
            return new AnimationNode
            {
                Id = kill.Id,
                Name = "kill",
                Start = Moving(0, 0, 60, 1.0),
                End = Moving(0, 0, 60, 0.0),
                Sequence = new SequenceNode
                {
                    RepeatFromFrame = 0,
                    RepeatCount = "3",
                    Frame = kill.Frames.ToArray(),
                    Next = new NextNode[0],
                },
            };
        }

        private static AnimationNode BuildSync(Emitted sync, Emitted hub)
        {
            return new AnimationNode
            {
                Id = sync.Id,
                Name = "sync",
                Start = Moving(0, 0, 100, 1.0),
                End = Moving(0, 0, 100, 1.0),
                Sequence = new SequenceNode
                {
                    RepeatFromFrame = 0,
                    RepeatCount = "0",
                    Frame = sync.Frames.ToArray(),
                    Next = new[] { Next(hub.Id, 100, "none") },
                },
            };
        }

        private static HeaderNode BuildHeader(string skinName, ShimejiConfig config, Emitted hub, Func<string, Bitmap> load)
        {
            string icon = null;
            try
            {
                string iconImage = HubImage(hub);
                if (load != null && iconImage != null)
                    using (Bitmap src = load(iconImage))
                        icon = Convert.ToBase64String(IconBuilder.BuildIco(src));
            }
            catch { /* icon is best-effort; a null becomes a placeholder below */ }
            if (icon == null)
                using (var blank = new Bitmap(IconBuilder.Size, IconBuilder.Size))
                    icon = Convert.ToBase64String(IconBuilder.BuildIco(blank));

            // Keep the character's real name (the validator allows up to 128); only guard against something
            // absurdly long. The Pets gallery reads this <petname> as the display label.
            string petname = skinName.Length > 60 ? skinName.Substring(0, 60) : skinName;
            return new HeaderNode
            {
                Author = ConvertedAuthor,
                Title = skinName + " (converted)",
                Petname = petname,
                // Not decoration: the reweight migration recovers a source frequency as (weight -
                // HubBaseWeight), which only holds for a pet emitted BEFORE the damped curve. It therefore
                // rewrites 1.0 -> 1.1 and skips anything already at 1.1, and new conversions start at 1.1 so
                // they are never re-curved. Bump this if the hub weighting ever changes shape again.
                Version = ConvertedFormatVersion,
                Info = "Converted from a Shimeji skin by ShimejiConvert. Behaviour is approximated; see the import report for what was simplified or dropped.",
                Application = "1",
                Icon = icon,
            };
        }

        private static string HubImage(Emitted hub)
        {
            if (hub.Source != null && hub.Source.Animations.Count > 0 && hub.Source.Animations[0].Poses.Count > 0)
                return hub.Source.Animations[0].Poses[0].Image;
            return null;
        }

        private static void BuildResidue(ShimejiConfig config, ResidueReport residue, bool alpha)
        {
            ShimejiAction fallAction = FirstWithClass(config, "Fall");
            ShimejiAction dragAction = FirstWithClass(config, "Dragged");
            var notOnFloor = new List<string>();
            foreach (ShimejiAction a in config.Actions)
            {
                if (a.Group == FidelityGroup.Group3)
                    residue.Dropped.Add(new ResidueItem { Name = a.Name, Kind = "dropped", Detail = a.Reason });
                else if (a.Group == FidelityGroup.Group2)
                    // A gaze keeps its Group2 classification (the cursor condition IS host state), but the
                    // classifier's stock reason is now false for it: the horizontal half is implemented rather
                    // than pending, and saying otherwise sends a reader looking for a host change that already
                    // shipped. Only the vertical axis is actually lost.
                    residue.Degraded.Add(new ResidueItem
                    {
                        Name = a.Name,
                        Kind = "degraded",
                        Detail = IsGazeAction(a)
                            ? "aims at the pointer horizontally (faceCursor); the up/down variants collapse to the pose the source uses when no cursor-height condition matches"
                            : a.Reason,
                    });
                else if (a.Group == FidelityGroup.Group1 && a.Animations.Count > 0 && a.Animations[0].Poses.Count > 0
                         && !IsFloorAction(a) && !IsWallAction(a) && !IsCeilingAction(a)
                         && a != fallAction && a != dragAction)
                    // Whatever is left over once floor, wall, ceiling and jump have all had their turn: a
                    // Group1 posed action whose BorderType is none of the four regions.
                    notOnFloor.Add(a.Name);
            }

            // Wall and ceiling actions that DID convert, so the report can say what the pet gained rather than
            // only what it lost. Reported separately from notOnFloor because they are no longer residue.
            var wallConverted = new List<string>();
            var ceilingConverted = new List<string>();
            var jumpConverted = new List<string>();
            var riseFlattened = new List<string>();
            foreach (ShimejiAction a in config.Actions)
            {
                if (IsWallAction(a) && a.Animations[0].Poses.Count > 0) wallConverted.Add(a.Name);
                if (IsCeilingAction(a) && a.Animations[0].Poses.Count > 0) ceilingConverted.Add(a.Name);
                if (a == fallAction || a == dragAction || !IsFloorAction(a)) continue;
                if (QualifiesAsJump(a)) jumpConverted.Add(a.Name);
                else if (RiseIsFlattened(a)) riseFlattened.Add(a.Name);
            }
            // Ceiling needs a wall to be reachable, so a skin with ceiling sprites and no wall region emits
            // none. Report that honestly instead of claiming a ceiling the pet does not have.
            bool ceilingReachable = wallConverted.Count > 0 && ceilingConverted.Count > 0;

            int condNeedsState = config.BehaviorConditions.Count(c => c.Group == FidelityGroup.Group2);
            if (alpha)
                residue.Notes.Add("Smooth edges are preserved: this pet keeps its real (anti-aliased) transparency and the app renders it per-pixel. Because that uses a desktopPet-only render mode, this converted pet will not run in web-esheep or other magenta-key players.");
            else
                residue.Notes.Add("Sprite edges are hard, not anti-aliased: the app renders every pet with a 1-bit magenta transparency key (a pixel is either shown or invisible, no partial transparency), so soft/smooth edges cannot be preserved -- mild for hard-outlined art, more visible on glows or shadows.");
            residue.Notes.Add("The pet gets a coherent FLOOR behaviour (idle / walk-and-turn / fall / drag). Shimeji's full conditional behaviour selection (its Markov chain and " + condNeedsState + " state-dependent conditions) is not reproduced; it wanders and rests rather than following the original's exact routine.");
            if (wallConverted.Count > 0)
                residue.Notes.Add("Wall climbing IS converted: on reaching a left/right screen edge the pet may grab the wall and climb it, then let go and fall. Converted for this skin: " + string.Join(", ", wallConverted) + ".");
            if (ceilingReachable)
                residue.Notes.Add("Ceiling walking IS converted: a pet that climbs a wall to the top of the screen usually carries on across the ceiling, then either drops or climbs back down the far wall. Converted for this skin: " + string.Join(", ", ceilingConverted) + ".");
            else if (ceilingConverted.Count > 0)
                residue.Notes.Add("This skin has ceiling animations (" + string.Join(", ", ceilingConverted) + ") but no wall animations, and the ceiling is only reachable by climbing a wall. They are left out rather than emitted as animations nothing can reach.");
            if (jumpConverted.Count > 0)
                residue.Notes.Add("Jumping IS converted: the pet launches into a fixed-height arc (about " + JumpPeakPx + "px), falls if the arc outlives the drop, and on landing either hops again or runs off rather than stopping dead. The HEIGHT is the converter's, not the source's -- a Shimeji jump describes a per-tick velocity whose result depends on how many frames the action happens to have, which across this corpus ranged from an 11px twitch to a 72px fling. Converted for this skin: " + string.Join(", ", jumpConverted) + ".");
            // Wording matters here. This used to read "Wall, ceiling and jump animations are not represented",
            // which describes a format limitation that does not exist -- the engine handles walls and ceilings
            // (17 of the 22 hand-authored pets use them). Say what is true: these specific animations were not
            // ATTEMPTED, and why.
            if (notOnFloor.Count > 0)
                residue.Notes.Add("These animations are not attempted: " + string.Join(", ", notOnFloor) + ". This is a converter gap rather than a format limit.");
            if (riseFlattened.Count > 0)
                residue.Notes.Add("These animations rise in the original but too gently to be jumps, so they play flat along the ground instead: " + string.Join(", ", riseFlattened) + ". The sprites and timing are kept; only the upward drift is dropped. Passing it through unchanged is what made a grapple pose and a flight cycle read as broken little jumps.");
            residue.Notes.Add("Per-pose velocity is reduced to one start/end pair per animation, and 'walk to a target x' becomes a fixed-length walk that turns at the screen edge.");

            // (Sound residue is appended by AppendSoundResidue after emit, which knows how many clips were
            // actually captured vs dropped -- BuildResidue can only see that a pose named one.)

            int scriptPoses = config.Poses.Count(p => p.ScriptFlattened);
            if (scriptPoses > 0)
                residue.Notes.Add(scriptPoses + " pose(s) used script-computed durations or velocities; these are flattened to fixed values, so their timing or motion is approximate.");

            int scriptActions = config.Actions.Count(a =>
                a.SubtreeBlob != null && (a.SubtreeBlob.Contains("${") || a.SubtreeBlob.Contains("#{")));
            if (scriptActions > 0)
                residue.Notes.Add(scriptActions + " action(s) use script-computed values (${...} / #{...}) for timing, targets or conditions. desktopPet can't evaluate scripts, so those are approximated by fixed timing and a bounded wander, or dropped.");
        }

        // ---- small builders ----

        private static MovingNode Moving(int x, int y, int intervalMs, double opacity)
        {
            return new MovingNode
            {
                X = x.ToString(),
                Y = y.ToString(),
                OffsetY = 0,          // anchors are already baked into the sheet
                Opacity = opacity,
                Interval = intervalMs.ToString(),
            };
        }

        private static NextNode Next(int target, int probability, string only)
        {
            return new NextNode { Value = target, Probability = probability, OnlyFlag = only };
        }

        private static int Interval(int durationTicks)
        {
            int ms = durationTicks * TickMs;
            if (ms < MinInterval) ms = MinInterval;
            if (ms > MaxInterval) ms = MaxInterval;
            return ms;
        }

        // One full pass of a sequence, in ms: the sum of every pose's clamped frame interval -- the artist's
        // intended pace for one play-through, which is what the walk-time budget is measured against.
        private static int SequencePassMs(List<ShimejiPose> poses)
        {
            if (poses == null || poses.Count == 0) return 0;
            long total = 0;
            foreach (ShimejiPose p in poses) total += Interval(p != null ? p.Duration : 0);
            return total > int.MaxValue ? int.MaxValue : (int)total;
        }

        // How many times to REPEAT a locomotion sequence so the whole walk lasts ~TargetLocoMs. Total passes
        // is repeat+1, so repeat = round(target / passMs) - 1, clamped to [MinLocoRepeats, MaxLocoRepeats].
        // Shared by the emitter and the `rebalance` migration so shipped and freshly-converted pets use one
        // policy. passMs <= 0 (unknown timing) keeps the old ceiling.
        /// <summary>
        /// Repeat count so a sequence lasts about <paramref name="targetMs"/>, given one pass takes
        /// <paramref name="passMs"/>. Total passes is repeat+1, so repeat = round(target/pass) - 1.
        ///
        /// Exists because "pick a fixed repeat" is wrong twice over and has now been the bug twice: a fast
        /// animation finishes instantly and a slow one runs for the best part of a minute. Budget the TIME and
        /// let the frame rate decide the count.
        /// </summary>
        public static int RepeatCountForBudget(int passMs, int targetMs, int maxRepeats)
        {
            return RepeatCountForBudget(passMs, targetMs, maxRepeats, false);
        }

        /// <param name="roundUp">Never land SHORT of the target. Used for rests, where undershooting is the
        /// thing that reads as wrong -- a pose that cuts off early looks like a twitch, whereas one that runs
        /// a little long just looks restful. Walking keeps nearest-rounding, where overshooting means the pet
        /// glides past where you expected it to stop.</param>
        public static int RepeatCountForBudget(int passMs, int targetMs, int maxRepeats, bool roundUp)
        {
            if (passMs <= 0) return 0;
            double exact = (double)targetMs / passMs;
            int passes = roundUp
                ? (int)Math.Ceiling(exact)
                : (int)Math.Round(exact, MidpointRounding.AwayFromZero);
            int repeat = passes - 1;
            if (repeat < 0) repeat = 0;
            if (repeat > maxRepeats) repeat = maxRepeats;
            return repeat;
        }

        /// <summary>
        /// Repeat count for a RESTING pose, so it stays on screen ~RestDwellMs instead of a single pass.
        ///
        /// Shimeji holds a Stay action for as long as the BEHAVIOUR that ran it says to, and the behaviour
        /// layer is exactly what this converter does not reproduce. Emitting repeat="0" therefore turned every
        /// rest into one pass: Hornet's BePet lasted 0.2s and read as a twitch. Only Stay-type actions get
        /// this; a one-shot performance (Animate: a trip, a bounce, a needle throw) must still play once.
        /// </summary>
        public static int RestRepeatCount(int passMs)
        {
            return RepeatCountForBudget(passMs, RestDwellMs, MaxRestRepeats, true);
        }

        /// <summary>Dwell for a PERFORMANCE rest (anything but the hub): long enough to watch, 9-12s.</summary>
        public static int RestDwellTargetMs { get { return RestDwellMs; } }

        /// <summary>Dwell for the HUB (the return-to pose): brief, so the pet does not loiter.</summary>
        public static int HubDwellTargetMs { get { return HubDwellMs; } }

        /// <summary>Repeats allowed to reach a rest dwell. Public so the migration matches the emitter.</summary>
        public static int MaxRestRepeatCount { get { return MaxRestRepeats; } }

        /// <summary>Per-frame interval a rest may use, before the cap. Public for the migration.</summary>
        public static int RestIntervalCapMs { get { return MaxRestIntervalMs; } }

        /// <summary>
        /// Timing for a SINGLE-FRAME rest, where the frame interval IS the dwell and so must be chosen rather
        /// than inherited.
        ///
        /// The reference conf authors these as Duration=250, i.e. exactly 10s. Clamping the interval to
        /// MaxInterval (4s) and then repeating can only ever reach multiples of 4 -- 8s for a 10s pose, which
        /// is what shipped and what was reported. Instead pick the fewest passes that keep each interval under
        /// the cap and divide the target evenly between them: 10s becomes 3 passes of 3333ms, which is exactly
        /// 10s on screen while the pet still re-evaluates roughly every 3 seconds.
        ///
        /// Splitting rather than one long interval matters: the interval is the animation's tick, so a single
        /// 10s frame would also mean 10s before the pet notices it should fall.
        /// </summary>
        public static void SingleFrameRestTiming(int targetMs, out int intervalMs, out int repeat)
        {
            if (targetMs < MinInterval) targetMs = MinInterval;
            int passes = (int)Math.Ceiling((double)targetMs / MaxInterval);
            if (passes < 1) passes = 1;
            if (passes > MaxRestRepeats + 1) passes = MaxRestRepeats + 1;
            intervalMs = targetMs / passes;
            if (intervalMs < MinInterval) intervalMs = MinInterval;
            if (intervalMs > MaxInterval) intervalMs = MaxInterval;
            repeat = passes - 1;
        }

        public static int LocoRepeatCount(int passMs)
        {
            if (passMs <= 0) return MaxLocoRepeats;
            int passes = (int)Math.Round((double)TargetLocoMs / passMs, MidpointRounding.AwayFromZero);
            int repeat = passes - 1;
            if (repeat < MinLocoRepeats) repeat = MinLocoRepeats;
            if (repeat > MaxLocoRepeats) repeat = MaxLocoRepeats;
            return repeat;
        }

        // The first pose in an action's first animation that carries a Sound clip, or null. The pet format
        // attaches a sound to an animation (played at its start), so one representative clip per animation is
        // the honest mapping of Shimeji's per-pose sounds.
        private static string FirstSoundClip(ShimejiAction a)
        {
            if (a == null || a.Animations.Count == 0) return null;
            foreach (ShimejiPose p in a.Animations[0].Poses)
                if (p != null && !string.IsNullOrWhiteSpace(p.Sound)) return p.Sound;
            return null;
        }

        private static void AppendSoundResidue(ResidueReport residue, int wanted, int captured, bool attempted)
        {
            if (wanted <= 0) return;
            if (captured == wanted)
                residue.Notes.Add(captured + " animation sound(s) captured -- transcoded to MP3 and embedded; " +
                    "they play at each animation's start (Shimeji's per-pose sound timing is not reproduced).");
            else if (captured > 0)
                residue.Notes.Add(captured + " of " + wanted + " animation sound(s) captured (MP3, played at " +
                    "animation start); the rest were dropped -- the per-pet audio budget, or an unreadable/oversize clip.");
            else
                residue.Notes.Add(wanted + " animation(s) carry sound, but none was captured (" +
                    (attempted ? "the clips were missing or over the audio budget" : "no MP3 transcoder was available") +
                    "), so the pet is silent.");
        }

        private static readonly string[] MagicNames = { "fall", "drag", "kill", "sync" };

        private static string SanitizeName(string name)
        {
            string n = string.IsNullOrWhiteSpace(name) ? "anim" : name.Trim();
            foreach (string m in MagicNames)
                if (string.Equals(n, m, StringComparison.OrdinalIgnoreCase)) return n + "_";
            return n;
        }

        /// <summary>
        /// Drop the `_left` / `_right` suffix a source skin puts on an action name, for the whole name set at
        /// once.
        ///
        /// The suffix describes the art the skin shipped, NOT a restriction on the pet. A converted pet keeps
        /// one copy of each mirrored pair and flips its entire sheet on <c>&lt;action&gt;flip&lt;/action&gt;</c>
        /// (FormCompanion calls FlipOrientation, which mirrors the sprites and negates the x-velocities), so
        /// `walk_left` is really "walk, whichever way the pet is facing". Reading a reachability map full of
        /// `_left` and looking for the missing `_right` is a genuine confusion the maintainer hit.
        ///
        /// Takes the whole set because the two things that make a rename UNSAFE are set-level, and putting
        /// them anywhere but here is how the emitter and the `undirect` migration would drift apart:
        ///   * The result must never be one of <see cref="MagicNames"/>. Xml.cs matches those exactly and
        ///     would start invoking the animation as a system behaviour. (SanitizeName would then append `_`,
        ///     so the name would not even be the one asked for.)
        ///   * The result must not collide with another name, including one produced by another rename in the
        ///     same pass. Two animations sharing a name are indistinguishable in Pet Studio, and a collision
        ///     onto a magic name would shadow the engine's lookup.
        /// </summary>
        /// <returns>Only the names that change, old -&gt; new. Empty when nothing is safe to rename.</returns>
        public static Dictionary<string, string> UndirectNames(IEnumerable<string> names)
        {
            var all = new List<string>();
            if (names != null)
                foreach (string n in names)
                    if (!string.IsNullOrEmpty(n)) all.Add(n);

            var taken = new HashSet<string>(all, StringComparer.OrdinalIgnoreCase);

            // Count proposals first: two animations wanting the same new name must BOTH keep their old one,
            // so the decision cannot depend on iteration order.
            var wantedBy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var proposal = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string n in all)
            {
                string stripped = StripDirection(n);
                if (stripped == null) continue;
                proposal[n] = stripped;
                int c;
                wantedBy[stripped] = wantedBy.TryGetValue(stripped, out c) ? c + 1 : 1;
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in proposal)
            {
                string dst = kv.Value;
                if (wantedBy[dst] > 1) continue;                 // two claimants: neither wins
                if (taken.Contains(dst)) continue;               // an existing animation already owns it
                bool magic = false;
                foreach (string m in MagicNames)
                    if (string.Equals(dst, m, StringComparison.OrdinalIgnoreCase)) { magic = true; break; }
                if (magic) continue;
                map[kv.Key] = dst;
            }
            return map;
        }

        /// <summary>The bare name with a trailing `_left` / `_right` removed, or null when there is none to
        /// remove or removing it would leave nothing.</summary>
        private static string StripDirection(string name)
        {
            string[] suffixes = { "_left", "_right" };
            foreach (string s in suffixes)
                if (name.Length > s.Length && name.EndsWith(s, StringComparison.OrdinalIgnoreCase))
                    return name.Substring(0, name.Length - s.Length);
            return null;
        }
    }
}
