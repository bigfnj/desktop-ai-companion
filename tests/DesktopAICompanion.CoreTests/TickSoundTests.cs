using System;
using System.Collections.Generic;
using DesktopAICompanion.Shimeji;

namespace DesktopAICompanion
{
    /// <summary>
    /// The source's sound rules (TickSound: Mascot.apply's sound, Mute, the shared clip cache), against a recording clip
    /// output that says a clip is running from its start until the test says it finished or a Mute stops it.
    /// </summary>
    internal static partial class Program
    {
        private sealed class TickClipDesk : ITickClips
        {
            public readonly HashSet<string> Running = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<string> Calls = new List<string>();
            public bool IsRunning(TickClip clip) { return Running.Contains(clip.ToString()); }
            public void Start(TickClip clip) { Calls.Add("start " + clip); Running.Add(clip.ToString()); }
            public void Stop(TickClip clip) { Calls.Add("stop " + clip); Running.Remove(clip.ToString()); }
        }

        private static XmlData.PoseNode TSound(int frame, int ticks, string sound, double volume = 0)
        {
            return new XmlData.PoseNode { Frame = frame, Ticks = ticks, Sound = sound, Volume = volume, VolumeSpecified = volume != 0 };
        }

        /// <summary>Sing: "la" for 3 ticks, then a silent pose for 2. Soft: "la" at -6 dB. Low: "lo". Lab: "lab", whose name
        /// starts with "la", declared FIRST so the declaration order is not the sorted order. Hush: a Mute of "la", then
        /// Rest. HushAll: a Mute with no Sound, then Rest.</summary>
        private static TickType TSoundType(string name)
        {
            return TickType.Compile(name, TickDoc(
                TRoute(TEdge(10, 1), TEdge(11, 0), TEdge(12, 0), TEdge(13, 0), TEdge(14, 0), TEdge(15, 0)),
                TBeh(10, "Sing", "Sing", 1, null),
                TBeh(11, "Soft", "Soft", 0, null),
                TBeh(12, "Low", "Low", 0, null),
                TBeh(13, "Lab", "Lab", 0, null),
                TBeh(14, "Hush", "Hush", 0, null),
                TBeh(15, "HushAll", "HushAll", 0, null),
                TAct(39, "Lab", "Stay", null, new[] { TParam("Duration", "50") }, TSound(0, 5, "lab")),
                TAct(30, "Sing", "Stay", null, new[] { TParam("Duration", "50") }, TSound(0, 3, "la"), TPose(1, 2)),
                TAct(31, "Soft", "Stay", null, new[] { TParam("Duration", "50") }, TSound(0, 5, "la", -6)),
                TAct(32, "Low", "Stay", null, new[] { TParam("Duration", "50") }, TSound(0, 5, "lo")),
                TAct(34, "Rest", "Stay", null, new[] { TParam("Duration", "50") }, TPose(2, 1)),
                TSequence(35, "Hush", "MuteLa", "Rest"),
                TInstant(36, "MuteLa", "Mute", TParam("Sound", "la")),
                TSequence(37, "HushAll", "MuteEvery", "Rest"),
                TInstant(38, "MuteEvery", "Mute")));
        }

        private static TickWorld TSoundWorld(TickLog log, TickClipDesk clips, TickSwitches switches, params TickType[] types)
        {
            var w = new TickWorld(new TickDesk(), new TickTape(), switches ?? new TickSwitches(), log) { Clips = clips };
            foreach (TickType t in types) w.AddType(t);
            w.Start();
            return w;
        }

        private static void TestTickSoundApply()
        {
            var log = new TickLog();
            var clips = new TickClipDesk();
            TickType t = TSoundType("Alpha");
            TickWorld w = TSoundWorld(log, clips, null, t);
            w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Sing");
            AssertEqual(0, clips.Calls.Count, "A clip started before the companion's first apply.");
            w.StepOnce();   // age 0: the pose names "la"
            AssertEqual("start Alpha/la@0", string.Join("|", clips.Calls), "The pose's clip did not start at the companion's apply.");
            AssertTrue(log.Events.Contains("m1 sound la start"), "The start was not reported as a sound event: " + string.Join("; ", log.Events));
            w.StepOnce();   // age 1: still "la", and it is still running
            AssertEqual(1, clips.Calls.Count, "A running clip was started again (the source restarts only a clip that is not running).");
            clips.Running.Clear();   // the clip finished
            w.StepOnce();   // age 2: the pose still names it
            AssertEqual("start Alpha/la@0|start Alpha/la@0", string.Join("|", clips.Calls),
                "A clip that finished while its pose still names it did not play again at the next apply.");
            clips.Running.Clear();
            w.StepOnce();   // age 3: the silent pose
            AssertEqual(2, clips.Calls.Count, "A pose that names no clip started one.");

            // A world with no clip output plays nothing and reports nothing (no clip is loaded there).
            var quiet = new TickLog();
            var silent = new TickWorld(new TickDesk(), new TickTape(), new TickSwitches(), quiet);
            silent.AddType(t);
            silent.Start();
            silent.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Sing");
            silent.StepOnce();
            AssertFalse(quiet.Events.Exists(e => e.Contains(" sound ")) || quiet.Faults.Count > 0,
                "A world with no clip output reported a sound or faulted: " + string.Join("; ", quiet.Events) + string.Join("; ", quiet.Faults));
        }

        private static void TestTickSoundShared()
        {
            var log = new TickLog();
            var clips = new TickClipDesk();
            TickType t = TSoundType("Alpha");
            TickWorld w = TSoundWorld(log, clips, null, t);
            w.CreateAtStart("m1", t, new TickPoint(600, 680), false, "Sing");
            w.CreateAtStart("m2", t, new TickPoint(700, 680), false, "Sing");
            w.CreateAtStart("m3", t, new TickPoint(800, 680), false, "Soft");
            w.StepOnce();
            AssertEqual("start Alpha/la@0|start Alpha/la@-6", string.Join("|", clips.Calls),
                "Two companions naming one clip did not share it, or the same sound at another volume was not its own clip.");
            AssertFalse(log.Events.Contains("m2 sound la start"), "The second companion started a clip the first was already playing.");
            AssertTrue(log.Events.Contains("m3 sound la start"), "The -6 dB clip's start was not reported for its companion.");
        }

        private static void TestTickSoundSettingAndTransform()
        {
            TickType t = TSoundType("Alpha");
            var off = new TickSwitches();
            off.Off["Alpha:Sounds"] = false;
            var clips = new TickClipDesk();
            TickWorld w = TSoundWorld(new TickLog(), clips, off, t);
            w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Sing");
            w.StepOnce();
            AssertEqual(0, clips.Calls.Count, "A companion whose sounds are off started a clip.");

            // Egg's second pose names "crack", and on that tick the companion becomes a Beta. The apply plays the APPLIED
            // pose's clip, still the Egg's, under the setting of the type it is now: Egg's sounds are off, Beta's on.
            TickType beta = TickType.Compile("Beta", TickDoc(
                TRoute(TEdge(10, 1)),
                TBeh(10, "Stand", "Stand", 1, null),
                TAct(30, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(2, 1))));
            TickType egg = TickType.Compile("Egg", TickDoc(
                TRoute(TEdge(10, 1)),
                TBeh(10, "Laid", "Laid", 1, null),
                TAct(30, "Laid", "Embedded", "Transform", new[] { TParam("TransformMascot", "Beta"), TParam("TransformBehavior", "Stand") },
                    TPose(0, 1), TSound(1, 1, "crack"))));
            var eggOff = new TickSwitches();
            eggOff.Off["Egg:Sounds"] = false;
            var hatch = new TickClipDesk();
            var log = new TickLog();
            TickWorld w2 = TSoundWorld(log, hatch, eggOff, egg, beta);
            TickMascot m = w2.CreateAtStart("m1", egg, new TickPoint(640, 680), false, "Laid");
            w2.StepOnce();
            w2.StepOnce();
            AssertEqual("Beta", m.Type.Name, "The fixture did not transform on its second tick.");
            AssertEqual("start Egg/crack@0", string.Join("|", hatch.Calls),
                "A transformed companion did not play its applied pose's clip under its current type's Sounds setting: "
                + string.Join("|", hatch.Calls));
        }

        private static void TestTickSoundMuteNamed()
        {
            TickType alpha = TSoundType("Alpha"), beta = TSoundType("Beta");
            var log = new TickLog();
            var clips = new TickClipDesk();
            // Playing: Alpha's la (0 dB), lab and lo, and Beta's la. Alpha's la at -6 dB is not.
            foreach (string c in new[] { "Alpha/la@0", "Alpha/lab@0", "Alpha/lo@0", "Beta/la@0" }) clips.Running.Add(c);
            TickWorld w = TSoundWorld(log, clips, null, alpha, beta);
            w.CreateAtStart("m1", alpha, new TickPoint(640, 680), false, "Hush");   // the Mute applies at initialisation
            AssertEqual("stop Alpha/la@0|stop Alpha/lab@0", string.Join("|", clips.Calls),
                "A named Mute did not stop exactly the initiator's RUNNING clips whose names start with the Sound, at any volume, "
                + "in sorted order (lab is declared first): " + string.Join("|", clips.Calls));
            AssertTrue(clips.Running.Contains("Beta/la@0") && clips.Running.Contains("Alpha/lo@0"),
                "A named Mute stopped another type's clip of that name, or a clip whose name does not start with it.");
            AssertEqual("m1 mute la|m1 sound la stop|m1 sound lab stop",
                string.Join("|", log.Events.FindAll(e => e.Contains(" mute ") || e.Contains(" sound "))),
                "The Mute and its stops were not reported in order.");
        }

        private static void TestTickSoundMuteAll()
        {
            TickType alpha = TSoundType("Alpha"), beta = TSoundType("Beta");
            var log = new TickLog();
            var clips = new TickClipDesk();
            clips.Running.Add("Alpha/lo@0");
            clips.Running.Add("Beta/la@-6");
            TickWorld w = TSoundWorld(log, clips, null, alpha, beta);
            w.CreateAtStart("m1", alpha, new TickPoint(640, 680), false, "HushAll");
            AssertEqual(0, clips.Running.Count, "A Mute with no Sound did not stop every clip of every type.");
            AssertEqual("m1 mute -|m1 sound lo stop|m1 sound la stop",
                string.Join("|", log.Events.FindAll(e => e.Contains(" mute ") || e.Contains(" sound "))),
                "A Mute with no Sound did not report exactly the clips that were running (sorted by type, name, volume).");

            var off = new TickSwitches();
            off.Off["Alpha:Sounds"] = false;
            var kept = new TickClipDesk();
            kept.Running.Add("Beta/la@0");
            TickWorld w2 = TSoundWorld(new TickLog(), kept, off, alpha, beta);
            w2.CreateAtStart("m1", alpha, new TickPoint(640, 680), false, "HushAll");
            AssertTrue(kept.Calls.Count == 0 && kept.Running.Contains("Beta/la@0"),
                "A Mute with no Sound stopped clips while the initiator's sounds were off.");
        }

        private static void TestTickSoundVolume()
        {
            AssertEqual(-6f, TSoundType("Alpha").Actions["Soft"].Variants[0].Poses[0].Volume, "A pose's volume was not compiled.");
            Func<float, double, bool> near = (db, expected) => Math.Abs(TickSound.LinearGain(db) - expected) < 1e-6;
            AssertTrue(TickSound.LinearGain(0f) == 1.0, "0 dB is not a gain of 1.");
            AssertTrue(near(-20f, 0.1) && near(-6f, 0.501187233627), "Volume is not 10^(dB/20).");
            AssertTrue(near(6f, 1.99526231497), "A gain inside the control's range was clamped.");
            AssertTrue(near(-100f, 0.0001) && near(-80f, 0.0001), "A volume below -80 dB was not held at the control's floor.");
            AssertTrue(near(12f, 2.0), "A volume above +6.02 dB was not held at the control's ceiling (a factor of 2).");
        }
    }
}
