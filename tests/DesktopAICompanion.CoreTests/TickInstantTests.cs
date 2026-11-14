using System;
using System.Collections.Generic;
using DesktopAICompanion.Shimeji;

namespace DesktopAICompanion
{
    /// <summary>
    /// The instant family (Look, Offset, Mute, BringToFront) and SelfDestruct. Instants apply when a composite reaches
    /// them, at initialisation, and never step; so a Sequence of an instant and a Stay shows the instant's effect at
    /// tick 0 already.
    /// </summary>
    internal static partial class Program
    {
        private static XmlData.AnimationNode TInstant(int id, string name, string cls, params XmlData.ParamNode[] ps)
        {
            return new XmlData.AnimationNode
            {
                Id = id, Name = name,
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
                Act = new XmlData.ActNode { Type = "Embedded", Class = cls, Params = ps },
            };
        }

        private static XmlData.AnimationNode TSequence(int id, string name, params string[] children)
        {
            var kids = new List<XmlData.CompositeChildNode>();
            foreach (string c in children) kids.Add(new XmlData.CompositeChildNode { Animation = c });
            return new XmlData.AnimationNode
            {
                Id = id, Name = name,
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
                Act = new XmlData.ActNode { Type = "Sequence", Children = new XmlData.ChildrenNode { Mode = "sequence", Child = kids.ToArray() } },
            };
        }

        /// <summary>One behaviour, Go, running the sequence [instant, Stand].</summary>
        private static TickType TInstantType(XmlData.AnimationNode instant)
        {
            return TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1)),
                TBeh(10, "Go", "Go", 1, null),
                TSequence(20, "Go", instant.Name, "Stand"),
                instant,
                TAct(31, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(0, 1))));
        }

        private static void TestTickLookAndOffset()
        {
            TickType flip = TInstantType(TInstant(30, "Turn", "Look"));
            TickWorld w = TickWorldOf(new TickLog(), null, flip);
            TickMascot m = w.CreateAtStart("m1", flip, new TickPoint(640, 680), false, "Go");
            AssertTrue(m.LookRight, "Look with no LookRight did not flip a left-facing companion at initialisation.");

            // LookRight=true on a companion already facing right: the default (flip) would turn it left, so only reading
            // the parameter keeps it right.
            TickType right = TInstantType(TInstant(30, "Face", "Look", TParam("LookRight", "true")));
            TickWorld w2 = TickWorldOf(new TickLog(), null, right);
            TickMascot r = w2.CreateAtStart("m1", right, new TickPoint(640, 680), true, "Go");
            AssertTrue(r.LookRight, "Look did not take an explicit LookRight=true (it flipped instead).");

            // The Stand after the Offset is a floor action: (645, 677) is off the floor, so the companion will lose
            // the ground at its first step; the offset itself is applied at initialisation, before any step.
            TickType shift = TInstantType(TInstant(30, "Nudge", "Offset", TParam("X", "5"), TParam("Y", "-3")));
            TickWorld w3 = TickWorldOf(new TickLog(), null, shift);
            TickMascot o = w3.CreateAtStart("m1", shift, new TickPoint(640, 680), false, "Go");
            AssertEqual(645, o.Anchor.X, "Offset did not add X at initialisation.");
            AssertEqual(677, o.Anchor.Y, "Offset did not add Y at initialisation.");
        }

        private static void TestTickMuteAndFront()
        {
            var named = new TickLog();
            TickType clip = TInstantType(TInstant(30, "Hush", "Mute", TParam("Sound", "bell.wav")));
            TickWorldOf(named, null, clip).CreateAtStart("m1", clip, new TickPoint(640, 680), false, "Go");
            AssertTrue(named.Events.Contains("m1 mute bell.wav"), "Mute with a Sound did not ask for that clip to stop.");

            var all = new TickLog();
            TickType every = TInstantType(TInstant(30, "Hush", "Mute"));
            TickWorldOf(all, null, every).CreateAtStart("m1", every, new TickPoint(640, 680), false, "Go");
            AssertTrue(all.Events.Contains("m1 mute -"), "Mute with no Sound did not stop every clip while sounds are on.");

            var quiet = new TickLog();
            var switches = new TickSwitches();
            switches.Off["Alpha:Sounds"] = false;
            var silent = new TickWorld(new TickDesk(), new TickTape(), switches, quiet);
            silent.AddType(every);
            silent.Start();
            silent.CreateAtStart("m1", every, new TickPoint(640, 680), false, "Go");
            AssertFalse(quiet.Events.Exists(e => e.Contains(" mute ")), "Mute with no Sound acted while this companion's sounds were off.");

            TickType front = TInstantType(TInstant(30, "Up", "BringToFront"));
            TickMascot f = TickWorldOf(new TickLog(), null, front).CreateAtStart("m1", front, new TickPoint(640, 680), false, "Go");
            AssertTrue(f.BringToFrontRequested, "BringToFront did not ask for the window to be raised.");
        }

        // ---- the Mute Sound read record (N-bd-codex-converter-1-02, host half; CONTRACT v1.4 P2-A and P2-B). Each check is
        // its own Run group: a group stops at its first failure, and each mutation case names its own group.

        /// <summary>Go runs the sequence [Hush, Stand]; Hush is a Mute with <paramref name="own"/> parameters, and when
        /// <paramref name="incomingSound"/> is given the sequence's child reference binds Sound to it (the incoming
        /// definition, which wins over the own one).</summary>
        private static TickType TMuteType(string incomingSound, params XmlData.ParamNode[] own)
        {
            XmlData.AnimationNode seq = TSequence(20, "Go", "Hush", "Stand");
            if (incomingSound != null)
                seq.Act.Children.Child[0].Bind = new[] { new XmlData.BindNode { Name = "Sound", Value = incomingSound, Cache = "none" } };
            return TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1)),
                TBeh(10, "Go", "Go", 1, null),
                seq,
                TInstant(30, "Hush", "Mute", own),
                TAct(31, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(0, 1))));
        }

        /// <summary>Starts m1 in Go, which initialises the Mute and so reads Sound once, with this companion's sounds on or off.</summary>
        private static TickLog TMuteRun(TickType t, bool soundsOn, ITickRandom random = null)
        {
            var log = new TickLog();
            var switches = new TickSwitches();
            if (!soundsOn) switches.Off["Alpha:Sounds"] = false;
            var w = new TickWorld(new TickDesk(), random ?? new TickTape(), switches, log);
            w.AddType(t);
            w.Start();
            w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Go");
            return log;
        }

        private static string TMuteReads(TickLog log)
        {
            var parts = new List<string>();
            for (int i = 0; i < log.Reads.Count; i++)
            {
                TickParamRead r = log.Reads[i];
                parts.Add(log.ReadSites[i] + ":" + r.Selection + (r.Winner == null ? "" : "(" + r.Winner.Name + ")") + "," + r.Kind
                          + (r.Faulted ? ",faulted" : "") + (r.ObservationFault == null ? "" : "," + r.ObservationFault));
            }
            return "reads [" + string.Join("; ", parts) + "], events [" + string.Join("; ", log.Events) + "], faults [" + string.Join("; ", log.Faults) + "]";
        }

        /// <summary>An ITickRandom that counts every draw.</summary>
        private sealed class TCountingRandom : ITickRandom
        {
            public int Draws;
            public double Next() { Draws++; return 0.5; }
        }

        /// <summary>An ITickRandom whose every draw throws, so a script read of Math.random() throws out of the engine's Read.</summary>
        private sealed class TThrowingRandom : ITickRandom
        {
            public double Next() { throw new InvalidOperationException("engine read throws"); }
        }

        private static void TestTickMuteReadIncoming()
        {
            // The child reference's bind overwrites Hush's own Sound in the action's variables (TickVars' constructor); the
            // record must name that incoming OBJECT, because the parity map binds provenance by reference.
            TickType t = TMuteType("incoming.wav", TParam("Sound", "own.wav"));
            TickLog log = TMuteRun(t, true);
            TickParamDef incoming = t.ActionsById[20].Children[0].Binds["Sound"], own = t.ActionsById[30].Params["Sound"];
            AssertTrue(log.Events.Contains("m1 mute incoming.wav") && log.Reads.Count == 1 && log.ReadSites[0] == "named" && !ReferenceEquals(incoming, own),
                "WITNESS: the Mute did not read the incoming Sound exactly once and report it as a named mute: " + TMuteReads(log));
            AssertTrue(log.Reads[0].Selection == TickParamSelection.Definition && ReferenceEquals(log.Reads[0].Winner, incoming),
                "The read record does not select the incoming definition object (incoming over own): " + TMuteReads(log));
        }

        private static void TestTickMuteReadActivation()
        {
            // The observer is handed the Mute action itself, whose OwnerType is the type its definition was built from. In
            // the retained state of a failed Transform that is the HOLDING type (Alpha) while the companion is a Beta, which
            // is why provenance is looked up through the activation's OwnerType, never the companion's current type
            // (CONTRACT v1.4 P2-C, the retained-owner control). Morph's Transform fails (blank behaviour), then Morph's next
            // list chooses Go through Alpha, whose sequence initialises the Mute.
            TickType alpha = TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 0), TEdge(11, 0)),
                TBeh(10, "Morph", "Morph", 0, false, TEdge(11, 1)),
                TBeh(11, "Go", "Go", 0, null),
                TAct(30, "Morph", "Embedded", "Transform", new[] { TParam("TransformMascot", "Beta"), TParam("TransformBehavior", "") },
                    TPose(0, 1), TPose(1, 1)),
                TSequence(20, "Go", "Hush", "Stand"),
                TInstant(31, "Hush", "Mute", TParam("Sound", "own.wav")),
                TAct(32, "Stand", "Stay", null, new[] { TParam("Duration", "500") }, TPose(0, 1))));
            TickType beta = RetainedBeta();
            var log = new TickLog();
            var w = new TickWorld(new TickDesk(), new TickTape(), new TickSwitches(), log);
            w.AddType(alpha);
            w.AddType(beta);
            w.Start();
            TickMascot m = w.CreateAtStart("m1", alpha, new TickPoint(640, 680), false, "Morph");
            for (int i = 0; i < 4; i++) w.StepOnce();
            AssertTrue(ReferenceEquals(m.Type, beta) && m.Behaviour != null && m.Behaviour.Name == "Go" && ReferenceEquals(m.Behaviour.Type, alpha)
                       && log.Events.Contains("m1 mute own.wav") && log.Reads.Count == 1,
                "WITNESS: the retained Beta did not run Alpha's Go and read the Mute's Sound once: " + m.Type.Name + " | " + TMuteReads(log));
            AssertTrue(ReferenceEquals(log.ReadActivations[0].Def, alpha.ActionsById[31]) && ReferenceEquals(log.ReadActivations[0].OwnerType, alpha),
                "The Mute read's activation is not the Mute action owned by the holding type: "
                + log.ReadActivations[0].Def.Name + " owned by " + (log.ReadActivations[0].OwnerType == null ? "none" : log.ReadActivations[0].OwnerType.Name));
        }

        private static void TestTickMuteReadLocal()
        {
            // A local published into the action's variables (putVariable) replaces any definition until the instance ends;
            // Init clears only the cache, so a local put before a re-initialisation is what that read sees.
            TickType t = TMuteType(null, TParam("Sound", "own.wav"));
            var log = new TickLog();
            var w = new TickWorld(new TickDesk(), new TickTape(), new TickSwitches(), log);
            w.AddType(t);
            w.Start();
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Go");
            TickAction mute = TickInstantFamily.TryCreate(w, t, t.ActionsById[30], null);
            mute.Vars.Put("Sound", ScriptValue.FromString("local.wav"));
            mute.Init(m);
            AssertTrue(log.Events.Contains("m1 mute own.wav") && log.Events.Contains("m1 mute local.wav") && log.Reads.Count == 2
                       && log.Reads[0].Selection == TickParamSelection.Definition,
                "WITNESS: the definition read and then the local read were not both reported as named mutes: " + TMuteReads(log));
            AssertTrue(log.Reads[1].Selection == TickParamSelection.Local && log.Reads[1].Winner == null && log.Reads[1].Kind == ScriptValueKind.String,
                "A local Sound was not reported Local with no definition: " + TMuteReads(log));
        }

        private static void TestTickMuteReadLocalNullSoundsOff()
        {
            // The v1.4 Sounds-off local-null control, engine half (Q1a): a local null with this companion's sounds off asks
            // for no event, and the observer is still given that one read, selection Local, through the no-event site. The
            // trace writer's half (provenance missing, status error) arrives with P2-C.
            TickType t = TMuteType(null);
            var log = new TickLog();
            var switches = new TickSwitches();
            switches.Off["Alpha:Sounds"] = false;
            var w = new TickWorld(new TickDesk(), new TickTape(), switches, log);
            w.AddType(t);
            w.Start();
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Go");
            TickAction mute = TickInstantFamily.TryCreate(w, t, t.ActionsById[30], null);
            mute.Vars.Put("Sound", ScriptValue.Null);
            mute.Init(m);
            AssertTrue(!log.Events.Exists(e => e.Contains(" mute ")) && log.Reads.Count == 2
                       && log.ReadSites[0] == "no-event" && log.Reads[0].Selection == TickParamSelection.Absent
                       && log.ReadSites[1] == "no-event" && log.Reads[1].Selection == TickParamSelection.Local && log.Reads[1].Kind == ScriptValueKind.Null,
                "A Sounds-off Mute read (absent, then a local null) was not observed once each through the no-event site, or reported a mute: " + TMuteReads(log));
        }

        private static void TestTickMuteReadEveryBranch()
        {
            // Every completed read reaches the observer exactly once (CONTRACT v1.4 P2-B, Q1): named, null with sounds on
            // (the two mute events), and null with sounds off, absent or evaluated, which report no event.
            TickLog named = TMuteRun(TMuteType(null, TParam("Sound", "bell.wav")), false);
            TickLog absentOn = TMuteRun(TMuteType(null), true);
            TickLog absentOff = TMuteRun(TMuteType(null), false);
            TickLog nullOff = TMuteRun(TMuteType(null, TParam("Sound", "null", "action")), false);
            AssertTrue(named.Events.Contains("m1 mute bell.wav") && named.ReadSites.Count == 1 && named.ReadSites[0] == "named"
                       && absentOn.Events.Contains("m1 mute -") && absentOn.ReadSites.Count == 1 && absentOn.ReadSites[0] == "null"
                       && absentOn.Reads[0].Selection == TickParamSelection.Absent,
                "WITNESS: a named Mute and an absent Sound with sounds on were not each one read reported with its mute event: "
                + TMuteReads(named) + " | " + TMuteReads(absentOn));
            AssertTrue(!absentOff.Events.Exists(e => e.Contains(" mute ")) && absentOff.ReadSites.Count == 1 && absentOff.ReadSites[0] == "no-event"
                       && absentOff.Reads[0].Selection == TickParamSelection.Absent
                       && !nullOff.Events.Exists(e => e.Contains(" mute ")) && nullOff.ReadSites.Count == 1 && nullOff.ReadSites[0] == "no-event"
                       && nullOff.Reads[0].Selection == TickParamSelection.Definition && nullOff.Reads[0].Kind == ScriptValueKind.Null && !nullOff.Reads[0].Faulted,
                "A Sounds-off null read was not observed exactly once through the no-event site: " + TMuteReads(absentOff) + " | " + TMuteReads(nullOff));
        }

        private static void TestTickMuteReadKind()
        {
            // ${1} and ${'1'} both display as 1 and both stop clips named 1; only the kind taken before display coercion
            // tells the Number from the String (control 10, host half).
            TickLog num = TMuteRun(TMuteType(null, TParam("Sound", "1", "action")), true);
            TickLog str = TMuteRun(TMuteType(null, TParam("Sound", "'1'", "action")), true);
            AssertTrue(num.Events.Contains("m1 mute 1") && str.Events.Contains("m1 mute 1") && num.Reads.Count == 1 && str.Reads.Count == 1,
                "WITNESS: an evaluated Number 1 and an evaluated String '1' did not both mute the clip named 1: " + TMuteReads(num) + " | " + TMuteReads(str));
            AssertTrue(num.Reads[0].Kind == ScriptValueKind.Number && str.Reads[0].Kind == ScriptValueKind.String,
                "The read record's kind was taken after display coercion: " + TMuteReads(num) + " | " + TMuteReads(str));
        }

        private static void TestTickMuteReadFaulted()
        {
            // A self-reference is reported by TickVars.Get and reads null (TickRuntime TickVars.Get). Plain ${Sound}: the
            // result is null, sounds on, so the engine's null branch runs. The conditional ${Sound == null ? 'x' : 'y'}: the
            // inner self-reference faults, and the outer read still yields "x", the argument TickSound receives. Faulted is
            // orthogonal to the result in both (CONTRACT v1.4 Q2); an unfaulted read is not Faulted (WITNESS).
            TickLog plain = TMuteRun(TMuteType(null, TParam("Sound", "Sound", "action")), true);
            TickLog conditional = TMuteRun(TMuteType(null, TParam("Sound", "Sound == null ? 'x' : 'y'", "action")), true);
            TickLog clean = TMuteRun(TMuteType(null, TParam("Sound", "'x'", "action")), true);
            AssertTrue(clean.Events.Contains("m1 mute x") && clean.Reads.Count == 1 && !clean.Reads[0].Faulted && clean.Faults.Count == 0,
                "WITNESS: an unfaulted evaluated read was reported Faulted, or did not mute x: " + TMuteReads(clean));
            AssertTrue(plain.Events.Contains("m1 mute -") && plain.Faults.Exists(f => f.Contains("refers to itself"))
                       && conditional.Events.Contains("m1 mute x") && conditional.Faults.Exists(f => f.Contains("refers to itself")),
                "WITNESS: the self-referencing reads did not keep the engine's own results (null, then x) with their faults: "
                + TMuteReads(plain) + " | " + TMuteReads(conditional));
            AssertTrue(plain.Reads.Count == 1 && plain.Reads[0].Faulted && plain.Reads[0].Kind == ScriptValueKind.Null
                       && conditional.Reads.Count == 1 && conditional.Reads[0].Faulted && conditional.Reads[0].Kind == ScriptValueKind.String,
                "A self-referencing Sound read was not recorded Faulted (whatever its result): " + TMuteReads(plain) + " | " + TMuteReads(conditional));
        }

        private static void TestTickMuteReadDraws()
        {
            // CONTRACT v1.4 control 7: Condition reads Sound once (Math.random draws, null is not cached), then Apply reads it
            // once more: exactly 2 draws during the Mute's initialisation, an absolute count, because a comparison with another
            // observed read could not see an evaluation both add. Condition's own true IS cached, so the
            // sequence's HasNext poll after it draws nothing.
            var rng = new TCountingRandom();
            TickLog log = TMuteRun(TMuteType(null, TParam("Condition", "Sound == null", "action"), TParam("Sound", "Math.random() < 2 ? null : 'x'", "action")), true, rng);
            AssertTrue(log.Events.Contains("m1 mute -") && log.Reads.Count == 1 && log.Reads[0].Kind == ScriptValueKind.Null
                       && log.Draws.TrueForAll(d => d.Contains("#param:Sound#")),
                "WITNESS: the null-drawing Sound did not reach the null branch once, or something other than Sound drew: "
                + TMuteReads(log) + " | " + string.Join("|", log.Draws));
            AssertEqual(2, rng.Draws, "Observing the Mute read added an evaluation (exact draw count): " + string.Join("|", log.Draws));
        }

        private static void TestTickMuteReadObservationFault()
        {
            // An exception thrown by OBSERVATION code is kept in the record as a provenance defect and never reaches the
            // engine (CONTRACT v1.4 FC-2, control C18, engine half): the read, its result and the branch are the base's, no
            // fault is reported and the companion lives. One region at a time.
            foreach (string region in new[] { "selection", "snapshot", "result" })
            {
                TickLog log;
                TickParamRead.ObservationProbe = (at, parameter) => { if (at == region && parameter == "Sound") throw new InvalidOperationException("probe " + at); };
                try { log = TMuteRun(TMuteType(null, TParam("Sound", "own.wav")), true); }
                finally { TickParamRead.ObservationProbe = null; }
                AssertTrue(log.Events.Contains("m1 mute own.wav") && log.Faults.Count == 0 && !log.Events.Exists(e => e.Contains("dispose"))
                           && log.Reads.Count == 1 && log.Reads[0].ObservationFault == "observer exception InvalidOperationException: probe " + region,
                    "An observation exception was not kept as a provenance defect with the engine's read unchanged (" + region + "): " + TMuteReads(log));
            }
            TickLog none = TMuteRun(TMuteType(null, TParam("Sound", "own.wav")), true);
            AssertTrue(none.Reads.Count == 1 && none.Reads[0].ObservationFault == null,
                "WITNESS: a read with no observation exception carries a provenance defect: " + TMuteReads(none));
        }

        private static void TestTickMuteReadEngineThrow()
        {
            // An exception from the engine's OWN read is not a completed observation: it propagates exactly as before (here
            // into the start wrapper), and no read record is delivered. Wrapping Read in an observation catch would swallow it
            // into a record.
            TickLog log = new TickLog();
            var w = new TickWorld(new TickDesk(), new TThrowingRandom(), new TickSwitches(), log);
            TickType t = TMuteType(null, TParam("Sound", "Math.random() < 2 ? 'a' : 'b'", "action"));
            w.AddType(t);
            w.Start();
            string thrown = null;
            try { w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Go"); }
            catch (InvalidOperationException e) { thrown = e.Message; }
            AssertTrue(log.Reads.Count == 0 && !log.Events.Exists(e => e.Contains(" mute ")),
                "An exception from the engine's own Sound read was caught as an observation (thrown: " + (thrown ?? "nothing") + "): " + TMuteReads(log));
            AssertTrue(thrown == "engine read throws" || log.Faults.Exists(f => f.Contains("engine read throws")),
                "WITNESS: the engine's read exception neither propagated nor was reported: " + TMuteReads(log));
        }

        /// <summary>The Scaling setting, applied once at load: anchors and hotspots round(v * s) with Java's rounding,
        /// pose moves the same except that a non-zero move under one pixel stays one pixel.</summary>
        private static void TestTickScale()
        {
            XmlData.AnimationNode walk = TAct(30, "Walk", "Stay", null, new[] { TParam("Duration", "50") },
                TPose(0, 1, -3), TPose(1, 1, -7), TPose(2, 1, 0), TPose(3, 1, 1));
            walk.Act.Variants.Variant[0].Hotspots = new XmlData.HotspotsNode { Hotspot = new[] { THotspot("Walk") } };
            Func<double, TickType> make = s =>
            {
                TickType t = TickType.Compile("Alpha", TickDoc(TRoute(TEdge(10, 1)), TBeh(10, "Walk", "Walk", 1, null), walk));
                t.ApplyScale(s, TickScriptContext.JavaInt(64 * s), TickScriptContext.JavaInt(64 * s));
                return t;
            };
            TickType half = make(0.5);
            TickPoseDef[] hp = half.Actions["Walk"].Variants[0].Poses;
            AssertEqual("-1,-3,0,1", hp[0].Dx + "," + hp[1].Dx + "," + hp[2].Dx + "," + hp[3].Dx,
                "Half scale did not scale moves as the source (-3 -> round(-1.5) = -1, -7 -> round(-3.5) = -3 with halves up, 0 stays 0, 1 -> 0.5 kept at 1).");
            AssertTrue(half.AnchorX == 16 && half.AnchorY == 32 && half.CellWidth == 32, "Half scale did not scale the anchor and cell.");
            TickType quarter = make(0.25);
            TickPoseDef[] qp = quarter.Actions["Walk"].Variants[0].Poses;
            // A move of 1 at a quarter is 0.25, which rounds to 0: only the one-pixel rule keeps it moving.
            AssertTrue(qp[0].Dx == -1 && qp[3].Dx == 1, "A non-zero move scaled under one pixel did not stay one pixel in its direction.");
            TickHotspotDef h = make(1.5).Actions["Walk"].Variants[0].Hotspots[0];
            AssertTrue(h.Width == 30 && h.Height == 30 && h.X == 0, "Hotspot size was not scaled with the type.");

            // Java 8's Math.round: exact, halves up. floor(x + 0.5) gets 0.49999999999999994 wrong (the sum rounds to 1).
            AssertEqual(0, TickType.JavaRound(0.49999999999999994), "JavaRound(0.49999999999999994) is not 0, as Java 8 rounds it.");
            AssertEqual(-3, TickType.JavaRound(-3.5), "JavaRound(-3.5) is not -3 (halves go up).");
            AssertEqual(3, TickType.JavaRound(2.5), "JavaRound(2.5) is not 3.");

            // Breed's BornX is scaled by the PARENT type's scale (the source's global Scaling, kept per type here).
            TickType breeder = TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1), TEdge(11, 0)),
                TBeh(10, "Throw", "Throw", 1, null),
                TBeh(11, "Stand", "Stand", 0, null),
                TAct(30, "Throw", "Embedded", "Breed", new[] { TParam("BornX", "40"), TParam("BornBehavior", "Stand") }, TPose(0, 1), TPose(1, 1)),
                TAct(31, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(0, 1))));
            breeder.ApplyScale(2.0, 128, 128);
            var log = new TickLog();
            TickWorld w = TickWorldOf(log, null, breeder);
            w.CreateAtStart("m1", breeder, new TickPoint(640, 680), false, "Throw");
            w.StepOnce();
            w.StepOnce();
            AssertTrue(log.Events.Exists(e => e.StartsWith("m1 birth m1.b1 #1 try1 at (720, 680)")),
                "A birth's BornX was not scaled by its parent type's scale (40 at 2x is 80): " + string.Join("; ", log.Events));
        }

        /// <summary>Single-screen mode keeps an interactive window that only TOUCHES the companion's work area
        /// (MascotEnvironment.java:82 tests intersects), so a companion can stand on a window hanging off the edge.</summary>
        private static void TestTickWindowIntersects()
        {
            TickType t = TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1)),
                TBeh(10, "Stand", "Stand", 1, null),
                TAct(30, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(0, 1))));
            var desk = new MotionDesk { Window = new[] { 1200, 500, 200, 100 } };   // x 1200..1400: past the work area's right edge
            var w = new TickWorld(desk, new TickTape(), new TickSwitches(), new TickLog());
            w.AddType(t);
            w.Start();
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(1250, 500), false, "Stand");
            m.Environment.GetWorkArea();   // remembers the work area, so the single-screen window test applies
            ITickBorder floor = m.Environment.GetFloor();
            AssertTrue(floor.IsOn(m.Anchor), "A window partly outside the work area was dropped in single-screen mode; the source keeps any window that intersects it.");
        }

        private static void TestTickSelfDestruct()
        {
            TickType t = TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1)),
                TBeh(10, "Pop", "Pop", 1, null),
                TAct(30, "Pop", "Embedded", "SelfDestruct", null, TPose(0, 1), TPose(1, 1))));
            var log = new TickLog();
            TickWorld w = TickWorldOf(log, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Pop");
            w.StepOnce();   // age 0
            AssertFalse(m.Disposing, "SelfDestruct fired before its penultimate tick.");
            w.StepOnce();   // age 1 = duration - 1
            AssertTrue(m.Disposing && !m.Visible && log.Events.Contains("m1 dispose-request self-destruct"),
                "SelfDestruct did not dispose the companion on its penultimate tick: " + string.Join("; ", log.Events));
            AssertEqual(1, w.Mascots.Count, "A disposed companion left the manager before the next step.");
            w.StepOnce();
            AssertTrue(w.Mascots.Count == 0 && log.Events.Contains("m1 disposed"), "The disposed companion was not removed at the next step.");
        }
    }
}
