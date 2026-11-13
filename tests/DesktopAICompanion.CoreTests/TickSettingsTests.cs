using System;
using System.Collections.Generic;
using System.IO;
using DesktopAICompanion.Shimeji;

namespace DesktopAICompanion
{
    /// <summary>
    /// A tick companion's per-type switches (lane feature/shimeji-host-settings, grimoire/06 section 2): how they are
    /// stored, their defaults, a header toggle turning its behaviour off, a transformed companion reading its new type's
    /// switches, and which switches the Pets pane offers a type. Companions are built in code; the settings file is a
    /// scratch one.
    /// </summary>
    internal static partial class Program
    {
        // ---- fakes

        /// <summary>The user's choices in memory, by (type id, key).</summary>
        private sealed class MemorySwitches : ITickSwitchStore
        {
            private readonly Dictionary<string, bool> _on = new Dictionary<string, bool>(StringComparer.Ordinal);
            public void Set(string typeId, string key, bool on) { _on[typeId + "|" + key] = on; }
            public bool? Get(string typeId, string key)
            {
                bool v;
                return _on.TryGetValue(typeId + "|" + key, out v) ? v : (bool?)null;
            }
        }

        /// <summary>The host's settings with type names as type ids (HostTickSettings with an identity map): the per-type
        /// switches through <see cref="TickTypeSwitches"/>, Throwing and Sounds on, Multiscreen off.</summary>
        private sealed class SwitchSettings : ITickSettings
        {
            public readonly MemorySwitches Store = new MemorySwitches();
            public bool Get(string setting, string imageSet)
            {
                if (setting == "Multiscreen") return false;
                if (setting == "Throwing" || setting == "Sounds") return true;
                return TickTypeSwitches.Get(Store, imageSet, setting);
            }
            public bool IsBehaviourEnabled(TickBehaviourDef behaviour, string imageSet)
            {
                return TickTypeSwitches.IsBehaviourEnabled(Store, imageSet, behaviour);
            }
        }

        private static XmlData.RootNode WithToggles(XmlData.RootNode doc, params XmlData.ToggleNode[] toggles)
        {
            doc.Header.Toggles = new XmlData.TogglesNode { Toggle = toggles };
            return doc;
        }

        private static XmlData.AnimationNode Toggled(XmlData.AnimationNode behaviour, string toggle)
        {
            behaviour.Behaviour.Toggle = toggle;
            return behaviour;
        }

        private static XmlData.AnimationNode Bound(XmlData.AnimationNode behaviour, string name, string value, string cache = "none")
        {
            behaviour.Behaviour.Bind = new[] { new XmlData.BindNode { Name = name, Value = value, Cache = cache } };
            return behaviour;
        }

        private static string RowKeys(TickType type)
        {
            var keys = new List<string>();
            foreach (TickSettingRow r in TickSettingRows.For(type)) keys.Add(r.Key);
            return string.Join(",", keys);
        }

        // ---- groups

        private static void TestTickSettingsPersist()
        {
            string directory = NewDirectory("tick-switches");
            string path = Path.Combine(directory, "settings.json");
            var store = new AppSettingsStore(path, null);
            AppSettingsDocument doc = store.Load();
            AssertTrue(doc.CompanionSwitches != null && doc.CompanionSwitches.Count == 0, "A fresh document carries choices.");
            doc.CompanionSwitches.Add(new CompanionSwitchEntry { Id = "Hornet", Key = TickTypeSwitches.WindowActions, On = true });
            doc.CompanionSwitches.Add(new CompanionSwitchEntry { Id = "Hornet", Key = "toggle:Hatching", On = false });
            AssertTrue(store.Save(doc), "Saving the choices failed.");

            AppSettingsDocument reread = new AppSettingsStore(path, null).Load();
            AssertTrue(reread.CompanionSwitches.Count == 2
                       && reread.CompanionSwitches[0].Id == "Hornet" && reread.CompanionSwitches[0].Key == "WindowActions" && reread.CompanionSwitches[0].On
                       && reread.CompanionSwitches[1].Key == "toggle:Hatching" && !reread.CompanionSwitches[1].On,
                "A per-type choice did not survive a restart (Save merges only the fields it names).");

            // The recovery path: the file is gone when the next save runs, so the store writes a clone of the document.
            File.Delete(path);
            reread.Volume = 0.4;
            var recovering = new AppSettingsStore(path, null);
            AppSettingsDocument loaded = recovering.Load();
            loaded.CompanionSwitches.Add(new CompanionSwitchEntry { Id = "Egg", Key = TickTypeSwitches.Transformation, On = false });
            File.Delete(path);
            AssertTrue(recovering.Save(loaded), "Saving over a missing file failed.");
            AppSettingsDocument cloned = new AppSettingsStore(path, null).Load();
            AssertTrue(cloned.CompanionSwitches.Count == 1 && cloned.CompanionSwitches[0].Id == "Egg" && !cloned.CompanionSwitches[0].On,
                "A per-type choice was lost by a save that could not read the old file (the document's clone).");

            // Normalised on load: the last choice for a (type, key) wins, an id that is not a pet id or a key with a
            // control character is dropped, and the key is exact (toggle ids are case-sensitive) while the id is not.
            var messy = AppSettingsDocument.CreateDefault();
            messy.CompanionSwitches = new List<CompanionSwitchEntry>
            {
                new CompanionSwitchEntry { Id = "Hornet", Key = "Breeding", On = false },
                null,
                new CompanionSwitchEntry { Id = "a/b", Key = "Breeding", On = false },
                new CompanionSwitchEntry { Id = "Hornet", Key = "toggle:x" + (char)7, On = false },
                new CompanionSwitchEntry { Id = "Hornet", Key = "", On = false },
                new CompanionSwitchEntry { Id = "HORNET", Key = "Breeding", On = true },
                new CompanionSwitchEntry { Id = "Hornet", Key = "toggle:A", On = false },
                new CompanionSwitchEntry { Id = "Hornet", Key = "toggle:a", On = true },
            };
            AssertTrue(messy.Normalize(), "Normalising a messy switch list reported no change.");
            AssertTrue(messy.CompanionSwitches.Count == 3
                       && messy.CompanionSwitches[0].Key == "Breeding" && messy.CompanionSwitches[0].On
                       && messy.CompanionSwitches[1].Key == "toggle:A" && messy.CompanionSwitches[2].Key == "toggle:a",
                "The switch list was not normalised: last wins per (type, key), unusable entries dropped, keys exact ("
                + messy.CompanionSwitches.Count + " entries).");
        }

        private static void TestTickSettingsDefaults()
        {
            foreach (ITickSwitchStore store in new ITickSwitchStore[] { null, new MemorySwitches() })
            {
                AssertTrue(TickTypeSwitches.Get(store, "Alpha", "Breeding") && TickTypeSwitches.Get(store, "Alpha", "Transients")
                           && TickTypeSwitches.Get(store, "Alpha", "Transformation"),
                    "A lifecycle switch the user has not touched is not on.");
                AssertFalse(TickTypeSwitches.Get(store, "Alpha", "WindowActions"),
                    "Window actions are on for a type the user has not let move windows (the owner's default is off).");
                AssertFalse(TickTypeSwitches.Get(store, "Alpha", "NoSuchSwitch") || TickTypeSwitches.Get(store, "Alpha", "toggle:Waving"),
                    "A key that is not a per-type switch answered on.");
            }
            TickType t = TickType.Compile("Alpha", WithToggles(TickDoc(
                    TRoute(TEdge(10, 1), TEdge(11, 1), TEdge(12, 1)),
                    Toggled(TBeh(10, "Wave", "Stand", 1, null), "Waving"),
                    Toggled(TBeh(11, "Nap", "Stand", 1, null), "Napping"),
                    TBeh(12, "Stand", "Stand", 1, null),
                    TAct(30, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(0, 1))),
                new XmlData.ToggleNode { Id = "Waving", Label = "Waving" },
                new XmlData.ToggleNode { Id = "Napping", Label = "Naps", Default = false }));
            var none = new MemorySwitches();
            AssertTrue(TickTypeSwitches.IsBehaviourEnabled(none, "Alpha", t.RootByName["Wave"]),
                "A toggle whose header default is on (the attribute's own default) read off.");
            AssertFalse(TickTypeSwitches.IsBehaviourEnabled(none, "Alpha", t.RootByName["Nap"]),
                "A toggle whose header says default=\"false\" read on before the user chose.");
            AssertTrue(TickTypeSwitches.IsBehaviourEnabled(none, "Alpha", t.RootByName["Stand"]),
                "A behaviour with no toggle is not always enabled.");
            none.Set("Alpha", "toggle:Napping", true);
            AssertTrue(TickTypeSwitches.IsBehaviourEnabled(none, "Alpha", t.RootByName["Nap"]),
                "Turning on a toggle that defaults off did not enable its behaviour.");
        }

        private static void TestTickSettingsToggleOff()
        {
            TickType t = TickType.Compile("Alpha", WithToggles(TickDoc(
                    TRoute(TEdge(10, 1), TEdge(11, 1), TEdge(12, 0)),
                    Toggled(TBeh(10, "Wave", "Stand", 1, null), "Waving"),
                    TBeh(11, "Stand", "Stand", 1, null),
                    TBeh(12, "Fall", "Stand", 0, null),
                    TAct(30, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(0, 1))),
                new XmlData.ToggleNode { Id = "Waving", Label = "Waving" }));
            var settings = new SwitchSettings();
            var tape = new TickTape();
            var world = new TickWorld(new TickDesk(), tape, settings, new TickLog());
            world.AddType(t);
            world.Start();

            // A draw of 0.25 over Wave(1) + Stand(1) lands in Wave; with Wave turned off, only Stand weighs anything.
            tape.Values.Enqueue(0.25);
            TickMascot on = world.CreateAtStart("m1", t, new TickPoint(640, 680), false, null);
            AssertEqual("Wave", on.Behaviour.Name, "WITNESS: the draw did not choose the toggled behaviour while it was on.");

            settings.Store.Set("Alpha", "toggle:Waving", false);
            tape.Values.Enqueue(0.25);
            TickMascot off = world.CreateAtStart("m2", t, new TickPoint(640, 680), false, null);
            AssertEqual("Stand", off.Behaviour.Name, "A behaviour whose toggle the user turned off was still chosen.");

            // Entered by name (a transition, a forced entry), a turned-off behaviour drops the companion in and falls.
            TickMascot forced = world.CreateAtStart("m3", t, new TickPoint(640, 680), false, "Wave");
            AssertEqual("Fall", forced.Behaviour.Name, "A behaviour whose toggle is off was entered by name.");
        }

        private static void TestTickSettingsTransform()
        {
            Func<string, string, TickType> egg = (name, entry) => TickType.Compile(name, TickDoc(
                TRoute(TEdge(10, 1)),
                TBeh(10, "Laid", "Laid", 1, null),
                TAct(30, "Laid", "Embedded", "Transform", new[] { TParam("TransformMascot", "Beta"), TParam("TransformBehavior", entry) },
                    TPose(0, 1), TPose(1, 1))));
            TickType beta = TickType.Compile("Beta", WithToggles(TickDoc(
                    TRoute(TEdge(10, 1), TEdge(11, 0), TEdge(12, 0), TEdge(13, 0)),
                    TBeh(10, "Stand", "Stand", 1, null),
                    TBeh(11, "Throw", "Throw", 0, null),
                    Toggled(TBeh(12, "Wave", "Stand", 0, null), "Waving"),
                    TBeh(13, "Fall", "Stand", 0, null),
                    TAct(30, "Throw", "Embedded", "Breed",
                        new[] { TParam("BornX", "40"), TParam("BornBehavior", "Stand"), TParam("BornMascot", "Beta") },
                        TPose(0, 1), TPose(1, 1)),
                    TAct(31, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(2, 1))),
                new XmlData.ToggleNode { Id = "Waving", Label = "Waving" }));

            // The target type's Breeding decides a birth after the transform; the source type's is the opposite each time.
            foreach (bool betaBreeds in new[] { false, true })
            {
                var settings = new SwitchSettings();
                settings.Store.Set("Egg", "Breeding", !betaBreeds);
                settings.Store.Set("Beta", "Breeding", betaBreeds);
                var log = new TickLog();
                TickType e = egg("Egg", "Throw");
                var world = new TickWorld(new TickDesk(), new TickTape(), settings, log);
                world.AddType(e);
                world.AddType(beta);
                world.Start();
                TickMascot m = world.CreateAtStart("m1", e, new TickPoint(640, 680), false, "Laid");
                for (int i = 0; i < 6; i++) world.StepOnce();
                bool born = log.Events.Exists(x => x.StartsWith("m1 birth", StringComparison.Ordinal));
                AssertEqual("Beta", m.Type.Name, "The egg did not transform.");
                AssertEqual(betaBreeds, born, "After a Transform, Breeding was read for the companion's OLD type ("
                    + (betaBreeds ? "the new type breeds and nothing was born" : "the new type does not breed and a birth happened") + ").");
            }

            // The behaviour a Transform enters is checked against the TARGET type's toggles: Beta has Waving off, the
            // egg's own settings say on, and the transformed companion falls instead of waving.
            var toggles = new SwitchSettings();
            toggles.Store.Set("Egg2", "toggle:Waving", true);
            toggles.Store.Set("Beta", "toggle:Waving", false);
            TickType e2 = egg("Egg2", "Wave");
            var w2 = new TickWorld(new TickDesk(), new TickTape(), toggles, new TickLog());
            w2.AddType(e2);
            w2.AddType(beta);
            w2.Start();
            TickMascot m2 = w2.CreateAtStart("m1", e2, new TickPoint(640, 680), false, "Laid");
            w2.StepOnce();
            w2.StepOnce();
            AssertTrue(m2.Type.Name == "Beta" && m2.Behaviour.Name == "Fall",
                "A Transform entered a behaviour its TARGET type has turned off (" + m2.Type.Name + ", " + m2.Behaviour.Name + ").");
        }

        /// <summary>
        /// The retained state of a failed Transform: Alpha's Morph transforms into Beta with a blank behaviour, which
        /// cannot be built, so the companion is a Beta while Morph, built from Alpha, stays (Transform.java:51-65). The
        /// step after, Morph ends and chooses its next behaviour through Alpha's configuration. <paramref name="morph"/>
        /// is Morph's behaviour node (id 10); the route lists it at weight 0 and then <paramref name="route"/>.
        /// </summary>
        private static TickType RetainedAlpha(XmlData.AnimationNode morph, XmlData.NextNode[] route, params XmlData.AnimationNode[] more)
        {
            var edges = new List<XmlData.NextNode> { TEdge(10, 0) };
            edges.AddRange(route);
            var nodes = new List<XmlData.AnimationNode> { TRoute(edges.ToArray()), morph };
            nodes.AddRange(more);
            nodes.Add(TAct(30, "Morph", "Embedded", "Transform", new[] { TParam("TransformMascot", "Beta"), TParam("TransformBehavior", "") },
                TPose(0, 1), TPose(1, 1)));
            nodes.Add(TAct(31, "Stand", "Stay", null, new[] { TParam("Duration", "500") }, TPose(0, 1)));
            return TickType.Compile("Alpha", WithToggles(TickDoc(nodes.ToArray()), new XmlData.ToggleNode { Id = "Waving", Label = "Waving" }));
        }

        private static TickType RetainedBeta()
        {
            return TickType.Compile("Beta", TickDoc(
                TRoute(TEdge(10, 1)),
                TBeh(10, "Stand", "Stand", 1, null),
                TAct(30, "Stand", "Stay", null, new[] { TParam("Duration", "500") }, TPose(2, 1))));
        }

        /// <summary>Runs <paramref name="alpha"/>'s Morph to its next choice in the retained state, with Waving set
        /// differently for the two types, and answers what it chose; asserts the retained state was reached.</summary>
        private static string RetainedChoice(TickType alpha, bool alphaWaves, bool betaWaves, TickLog log)
        {
            var settings = new SwitchSettings();
            settings.Store.Set("Alpha", "toggle:Waving", alphaWaves);
            settings.Store.Set("Beta", "toggle:Waving", betaWaves);
            var tape = new TickTape();
            tape.Values.Enqueue(0.25);
            var world = new TickWorld(new TickDesk(), tape, settings, log);
            world.AddType(alpha);
            world.AddType(RetainedBeta());
            world.Start();
            TickMascot m = world.CreateAtStart("m1", alpha, new TickPoint(640, 680), false, "Morph");
            world.StepOnce();
            world.StepOnce();
            AssertTrue(m.Type.Name == "Beta" && m.Behaviour.Name == "Morph" && ReferenceEquals(m.Behaviour.Type, alpha)
                       && log.Faults.Count == 1 && log.Faults[0].Contains("Transform into Beta"),
                "WITNESS: the failed Transform did not leave the companion a Beta in Alpha's Morph (" + m.Type.Name + ", "
                + (m.Behaviour == null ? "none" : m.Behaviour.Name) + "; " + string.Join("; ", log.Faults) + ").");
            world.StepOnce();
            AssertTrue(ReferenceEquals(m.Behaviour.Type, alpha),
                "WITNESS: Morph's next behaviour was not built from Alpha, the configuration that holds it (" + m.Behaviour.Type.Name + ").");
            return m.Behaviour.Name;
        }

        private static void TestTickSettingsRetainedRoot()
        {
            // N-bd-anchors-01 (receiver/Mute anchor packet note D1), a ROOT candidate. Morph's list adds to the route, so
            // Wave (toggle Waving, weight 1) and Stand (weight 1) are candidates; the draw 0.25 lands in Wave when it is
            // one and in Stand when it is not. The source checks each candidate with the companion, so the disabled list
            // is the CURRENT type's (Beta's), though the candidate's toggle is Alpha's definition. Both directions: Alpha
            // allows Wave and Beta does not, then the reverse; reading the holding type's switches fails both.
            TickType alpha = RetainedAlpha(TBeh(10, "Morph", "Morph", 0, null), new[] { TEdge(11, 1), TEdge(12, 1), TEdge(13, 0) },
                Toggled(TBeh(11, "Wave", "Stand", 1, null), "Waving"),
                TBeh(12, "Stand", "Stand", 1, null),
                TBeh(13, "Fall", "Stand", 0, null));
            foreach (bool betaWaves in new[] { false, true })
            {
                var log = new TickLog();
                string chosen = RetainedChoice(alpha, !betaWaves, betaWaves, log);
                AssertEqual(betaWaves ? "Wave" : "Stand", chosen,
                    "In the retained state a root candidate was filtered by the HOLDING type's switches, not the companion's current type's (Alpha "
                    + (betaWaves ? "off" : "on") + ", Beta " + (betaWaves ? "on" : "off") + "): " + string.Join("; ", log.Events));
                AssertTrue(log.Events.Contains("m1 behaviour Morph>" + chosen + " select"),
                    "The retained state's root choice was not entered as 'select': " + string.Join("; ", log.Events));
            }
        }

        private static void TestTickSettingsRetainedNext()
        {
            // N-bd-anchors-01, a NEXT-LIST candidate: Morph's list replaces the route (add false) with Wave (toggle Waving)
            // and Rest, weight 1 each, neither of them a route edge. The draw 0.25 lands in Wave when it is a candidate and
            // in Rest when it is not; as for a root candidate, the companion's current type decides.
            TickType alpha = RetainedAlpha(TBeh(10, "Morph", "Morph", 0, false, TEdge(14, 1), TEdge(15, 1)), new[] { TEdge(12, 1), TEdge(13, 0) },
                TBeh(12, "Stand", "Stand", 1, null),
                TBeh(13, "Fall", "Stand", 0, null),
                Toggled(TBeh(14, "Wave", "Stand", 1, null), "Waving"),
                TBeh(15, "Rest", "Stand", 1, null));
            foreach (bool betaWaves in new[] { false, true })
            {
                var log = new TickLog();
                string chosen = RetainedChoice(alpha, !betaWaves, betaWaves, log);
                AssertEqual(betaWaves ? "Wave" : "Rest", chosen,
                    "In the retained state a next-list candidate was filtered by the HOLDING type's switches, not the companion's current type's (Alpha "
                    + (betaWaves ? "off" : "on") + ", Beta " + (betaWaves ? "on" : "off") + "): " + string.Join("; ", log.Events));
                AssertTrue(log.Events.Contains("m1 behaviour Morph>" + chosen + " next"),
                    "The retained state's next-list choice was not entered as 'next': " + string.Join("; ", log.Events));
            }
        }

        private static void TestTickSettingsFallbackCause()
        {
            // N-bd-anchors-02 (anchor packet note D2): a behaviour entered by name that this companion has turned off drops
            // it in and enters Fall, and CONTRACT section 3 labels that entry `fallback`, whoever asked for the behaviour
            // (V1ScenarioRunner marks it on the drop-in draw). Each caller that builds by name with the enabled check is
            // driven once with the behaviour off and once (WITNESS) with it on, where the entry stays `forced`.
            const string drop = "builtin:disabled-behavior-drop-x";
            TickType alpha = TickType.Compile("Alpha", WithToggles(TickDoc(
                    TRoute(TEdge(10, 1), TEdge(11, 0), TEdge(12, 0)),
                    TBeh(10, "Stand", "Stand", 1, null),
                    Toggled(TBeh(11, "Wave", "Stand", 0, null), "Waving"),
                    TBeh(12, "Fall", "Stand", 0, null),
                    TAct(30, "Stand", "Stay", null, new[] { TParam("Duration", "500") }, TPose(0, 1))),
                new XmlData.ToggleNode { Id = "Waving", Label = "Waving" }));

            // Explicit startup (Main.createMascot): the first behaviour, named.
            foreach (bool on in new[] { true, false })
            {
                var settings = new SwitchSettings();
                settings.Store.Set("Alpha", "toggle:Waving", on);
                var log = new TickLog();
                var world = new TickWorld(new TickDesk(), new TickTape(), settings, log);
                world.AddType(alpha);
                world.Start();
                world.CreateAtStart("m1", alpha, new TickPoint(640, 680), false, "Wave");
                string want = on ? "m1 behaviour ->Wave forced" : "m1 behaviour ->Fall fallback";
                AssertTrue(log.Events.Contains(want) && log.Draws.Contains(drop) != on,
                    (on ? "WITNESS: an enabled startup behaviour named for a start companion was not entered forced"
                        : "A start companion's named startup behaviour, turned off, did not fall in labelled fallback")
                    + " (startup): " + string.Join("; ", log.Events) + " | " + string.Join("|", log.Draws));
            }

            // The Transform build (Transform.java:55): the target type's behaviour, checked against the target's switches.
            TickType egg = TickType.Compile("Egg", TickDoc(
                TRoute(TEdge(10, 1)),
                TBeh(10, "Laid", "Laid", 1, null),
                TAct(30, "Laid", "Embedded", "Transform", new[] { TParam("TransformMascot", "Alpha"), TParam("TransformBehavior", "Wave") },
                    TPose(0, 1), TPose(1, 1))));
            foreach (bool on in new[] { true, false })
            {
                var settings = new SwitchSettings();
                settings.Store.Set("Alpha", "toggle:Waving", on);
                var log = new TickLog();
                var world = new TickWorld(new TickDesk(), new TickTape(), settings, log);
                world.AddType(egg);
                world.AddType(alpha);
                world.Start();
                TickMascot m = world.CreateAtStart("m1", egg, new TickPoint(640, 680), false, "Laid");
                world.StepOnce();
                world.StepOnce();
                string want = on ? "m1 behaviour Laid>Wave forced" : "m1 behaviour Laid>Fall fallback";
                AssertTrue(m.Type.Name == "Alpha" && log.Events.Contains(want) && log.Draws.Contains(drop) != on,
                    (on ? "WITNESS: an enabled behaviour a Transform names was not entered forced"
                        : "A behaviour a Transform names, turned off for the target, did not fall in labelled fallback")
                    + " (Transform): " + string.Join("; ", log.Events) + " | " + string.Join("|", log.Draws));
            }

            // A held hotspot's re-entry (UserBehavior.next while the hotspot is held): no eligibility check comes first,
            // so a behaviour turned off while it is held is built with the enabled check and falls. The press (enabled)
            // and the first re-entry stay forced; the re-entry after Petting is turned off is the fallback.
            {
                TickType held = TickType.Compile("Alpha", WithToggles(TickDoc(
                        TRoute(TEdge(10, 1), TEdge(12, 0), TEdge(13, 0)),
                        TBeh(10, "Stand", "Stand", 1, null),
                        TBeh(12, "Fall", "Hold", 0, null),
                        Toggled(TBeh(13, "Petted", "Stand", 0, null), "Petting"),
                        HotspotAct(TAct(30, "Stand", "Stay", null, new[] { TParam("Duration", "500") }, TPose(0, 1)), "Petted"),
                        TAct(31, "Hold", "Stay", null, new[] { TParam("Duration", "500") }, TPose(1, 1))),
                    new XmlData.ToggleNode { Id = "Petting", Label = "Petting" }));
                var settings = new SwitchSettings();
                var log = new TickLog();
                var desk = new TickDesk();
                desk.Script[2] = new List<TickInput> { new TickInput(TickInputKind.Press, "m1", new TickPoint(618, 626)) };
                var world = new TickWorld(desk, new TickTape(), settings, log);
                world.AddType(held);
                world.Start();
                TickMascot m = world.CreateAtStart("m1", held, new TickPoint(640, 680), false, "Stand");
                world.StepOnce();
                world.StepOnce();
                AssertTrue(m.IsHotspotClicked && log.Events.Contains("m1 behaviour Stand>Petted forced") && log.Events.Contains("m1 behaviour Petted>Petted forced"),
                    "WITNESS: the enabled hotspot press and its held re-entry were not entered forced (held re-entry): " + string.Join("; ", log.Events));
                settings.Store.Set("Alpha", "toggle:Petting", false);
                world.StepOnce();
                AssertTrue(log.Events.Contains("m1 behaviour Petted>Fall fallback") && log.Draws.Contains(drop),
                    "A held hotspot's behaviour, turned off while held, did not fall in labelled fallback on its re-entry (held re-entry): "
                    + string.Join("; ", log.Events) + " | " + string.Join("|", log.Draws));
            }

            // The hotspot press itself (UserBehavior.mousePressed), which reaches the drop-in only in the retained state:
            // eligibility looks the name up in the CURRENT type (Beta's H, no toggle), construction in the HOLDING type
            // (Alpha's H, toggle Hing) with the current type's switches, where Beta has Hing off (packet leg L6).
            foreach (bool on in new[] { true, false })
            {
                TickType ra = RetainedAlpha(TBeh(10, "Morph", "Morph", 0, false, TEdge(15, 1)), new[] { TEdge(12, 0), TEdge(13, 0), TEdge(14, 0), TEdge(15, 0) },
                    TBeh(12, "Fall", "Hold", 0, null),
                    TBeh(13, "Dragged", "Hold", 0, null),
                    Toggled(TBeh(14, "H", "Hold", 0, null), "Hing"),
                    TBeh(15, "Pat", "Patting", 0, null),
                    HotspotAct(TAct(32, "Patting", "Stay", null, new[] { TParam("Duration", "500") }, TPose(0, 1)), "H"),
                    TAct(33, "Hold", "Stay", null, new[] { TParam("Duration", "500") }, TPose(1, 1)));
                TickType rb = TickType.Compile("Beta", TickDoc(
                    TRoute(TEdge(10, 1), TEdge(11, 0)),
                    TBeh(10, "Stand", "Stand", 1, null),
                    TBeh(11, "H", "Stand", 0, null),
                    TAct(30, "Stand", "Stay", null, new[] { TParam("Duration", "500") }, TPose(2, 1))));
                var settings = new SwitchSettings();
                settings.Store.Set("Beta", "toggle:Hing", on);
                var log = new TickLog();
                var desk = new TickDesk();
                desk.Script[6] = new List<TickInput> { new TickInput(TickInputKind.Press, "m1", new TickPoint(618, 626)) };
                var world = new TickWorld(desk, new TickTape(), settings, log);
                world.AddType(ra);
                world.AddType(rb);
                world.Start();
                TickMascot m = world.CreateAtStart("m1", ra, new TickPoint(640, 680), false, "Morph");
                for (int i = 0; i < 5; i++) world.StepOnce();
                AssertTrue(m.Type.Name == "Beta" && m.Behaviour.Name == "Pat" && ReferenceEquals(m.Behaviour.Type, ra) && m.Hotspots.Count == 1,
                    "WITNESS: the retained state before the press is not a Beta in Alpha's Pat with its hotspot (" + m.Type.Name + ", "
                    + m.Behaviour.Name + ", " + m.Hotspots.Count + " hotspot(s)): " + string.Join("; ", log.Events));
                world.StepOnce();
                string want = on ? "m1 behaviour Pat>H forced" : "m1 behaviour Pat>Fall fallback";
                AssertTrue(log.Events.Contains("m1 hotspot press H") && log.Events.Contains(want) && log.Draws.Contains(drop) != on
                           && ReferenceEquals(m.Behaviour.Type, ra),
                    (on ? "WITNESS: an enabled hotspot behaviour pressed in the retained state was not entered forced through the holding type"
                        : "A hotspot behaviour the current type's switches turn off for the holding type's definition did not fall in labelled fallback")
                    + " (hotspot press): " + string.Join("; ", log.Events) + " | " + string.Join("|", log.Draws));
            }

            // Kept as it is (policy:breed-parent-reposition): a born behaviour turned off moves no one and the child
            // enters Fall forced, with no drop-in draw, because the host does not drop the PARENT in as the source does.
            {
                TickType breeder = TickType.Compile("Alpha", WithToggles(TickDoc(
                        TRoute(TEdge(10, 1), TEdge(11, 0), TEdge(12, 0)),
                        TBeh(10, "Throw", "Throw", 1, null),
                        Toggled(TBeh(11, "Wave", "Stand", 0, null), "Waving"),
                        TBeh(12, "Fall", "Stand", 0, null),
                        TAct(30, "Throw", "Embedded", "Breed",
                            new[] { TParam("BornX", "40"), TParam("BornBehavior", "Wave"), TParam("BornMascot", "Alpha") },
                            TPose(0, 1), TPose(1, 1)),
                        TAct(31, "Stand", "Stay", null, new[] { TParam("Duration", "500") }, TPose(0, 1))),
                    new XmlData.ToggleNode { Id = "Waving", Label = "Waving" }));
                var settings = new SwitchSettings();
                settings.Store.Set("Alpha", "toggle:Waving", false);
                var log = new TickLog();
                var world = new TickWorld(new TickDesk(), new TickTape(), settings, log);
                world.AddType(breeder);
                world.Start();
                world.CreateAtStart("m1", breeder, new TickPoint(640, 680), false, "Throw");
                world.StepOnce();
                world.StepOnce();
                AssertTrue(log.Events.Contains("m1.b1 behaviour ->Fall forced") && !log.Draws.Contains(drop),
                    "WITNESS: a born behaviour turned off no longer enters Fall forced with no drop-in (policy:breed-parent-reposition): "
                    + string.Join("; ", log.Events) + " | " + string.Join("|", log.Draws));
            }
        }

        // ---- the hotspot press receiver (N-bd-parity-host-1-04, receiver/Mute anchor packet legs L0-L5; L6 is the
        // hotspot-press leg of TestTickSettingsFallbackCause above). The press judges eligibility in the companion's
        // CURRENT type (TickRuntime MousePressed, IsBehaviourEnabledByName(_mascot.Type, ...)) and builds through the
        // HOLDING type (EnterBehaviourByName(_mascot, Type, ...)); the two differ only in the retained state of a failed
        // Transform. Each leg compares OBJECTS (the behaviour's holding type, its definition and its running action's
        // definition), never a behaviour name, which both types share, and never a node id alone: the two types below use
        // disjoint id ranges as well, so an id-only assertion could not pass on the other type's definition either.

        /// <summary>The receiver fixture's holding type, Alpha. Morph transforms into Beta entering
        /// <paramref name="transformBehaviour"/> ("" cannot be built, which leaves the retained state; "BPat" succeeds);
        /// its next list (add false) then chooses Pat, a draggable Stay whose one hotspot names H. Alpha's H is not
        /// toggleable in any leg here: that is what lets L4 tell the eligibility receiver apart (anchor packet, L4 row).
        /// Ids 10-14 and 30-34.</summary>
        private static TickType ReceiverAlpha(string transformBehaviour)
        {
            return TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 0), TEdge(11, 0), TEdge(12, 0), TEdge(13, 0), TEdge(14, 0)),
                TBeh(10, "Morph", "Morph", 0, false, TEdge(11, 1)),
                TBeh(11, "Pat", "Patting", 0, null),
                TBeh(12, "H", "AlphaHAct", 0, null),
                TBeh(13, "Fall", "AlphaFallAct", 0, null),
                TBeh(14, "Dragged", "AlphaDragAct", 0, null),
                TAct(30, "Morph", "Embedded", "Transform", new[] { TParam("TransformMascot", "Beta"), TParam("TransformBehavior", transformBehaviour) },
                    TPose(0, 1), TPose(1, 1)),
                HotspotAct(TAct(31, "Patting", "Stay", null, new[] { TParam("Duration", "500"), TParam("Draggable", "true") }, TPose(0, 1)), "H"),
                TAct(32, "AlphaHAct", "Stay", null, new[] { TParam("Duration", "500") }, TPose(1, 1)),
                TAct(33, "AlphaFallAct", "Stay", null, new[] { TParam("Duration", "500") }, TPose(1, 1)),
                TAct(34, "AlphaDragAct", "Stay", null, new[] { TParam("Duration", "500") }, TPose(1, 1))));
        }

        /// <summary>The receiver fixture's current type after the Transform, Beta: its own H (toggle Hing when
        /// <paramref name="hToggleable"/>), only when <paramref name="hasH"/>; BPat, a Stay whose hotspot names H, only when
        /// <paramref name="hasBPat"/> (the L1 success control); its own Fall and Dragged. Ids 50-54 and 70-74.</summary>
        private static TickType ReceiverBeta(bool hasH, bool hToggleable, bool hasBPat)
        {
            var edges = new List<XmlData.NextNode> { TEdge(50, 1), TEdge(53, 0), TEdge(54, 0) };
            if (hasH) edges.Add(TEdge(51, 0));
            if (hasBPat) edges.Add(TEdge(52, 0));
            var nodes = new List<XmlData.AnimationNode> { TRoute(edges.ToArray()), TBeh(50, "Stand", "BetaStand", 1, null) };
            if (hasH) nodes.Add(hToggleable ? Toggled(TBeh(51, "H", "BetaHAct", 0, null), "Hing") : TBeh(51, "H", "BetaHAct", 0, null));
            if (hasBPat) nodes.Add(TBeh(52, "BPat", "BetaPatting", 0, null));
            nodes.Add(TBeh(53, "Fall", "BetaFallAct", 0, null));
            nodes.Add(TBeh(54, "Dragged", "BetaDragAct", 0, null));
            nodes.Add(TAct(70, "BetaStand", "Stay", null, new[] { TParam("Duration", "500") }, TPose(2, 1)));
            if (hasH) nodes.Add(TAct(71, "BetaHAct", "Stay", null, new[] { TParam("Duration", "500") }, TPose(3, 1)));
            if (hasBPat) nodes.Add(HotspotAct(TAct(72, "BetaPatting", "Stay", null, new[] { TParam("Duration", "500"), TParam("Draggable", "true") }, TPose(2, 1)), "H"));
            nodes.Add(TAct(73, "BetaFallAct", "Stay", null, new[] { TParam("Duration", "500") }, TPose(3, 1)));
            nodes.Add(TAct(74, "BetaDragAct", "Stay", null, new[] { TParam("Duration", "500") }, TPose(3, 1)));
            return TickType.Compile("Beta", WithToggles(TickDoc(nodes.ToArray()), new XmlData.ToggleNode { Id = "Hing", Label = "Hing" }));
        }

        /// <summary>Starts m1 as Alpha in <paramref name="start"/> and steps five times: Morph's Transform ends on step 2,
        /// Morph's next choice (Pat, through Alpha) comes on step 3 in the retained state, and the hotspot's owner then
        /// steps, which is what refreshes the companion's hotspot list (ActionBase.java:99), before one press at screen
        /// (<paramref name="x"/>, <paramref name="y"/>) on tick 6. <paramref name="witness"/> runs between the two.</summary>
        private static TickMascot ReceiverPress(TickType alpha, TickType beta, string start, SwitchSettings settings, TickLog log,
            int x, int y, Action<TickMascot> witness)
        {
            var desk = new TickDesk();
            desk.Script[6] = new List<TickInput> { new TickInput(TickInputKind.Press, "m1", new TickPoint(x, y)) };
            var world = new TickWorld(desk, new TickTape(), settings, log);
            world.AddType(alpha);
            world.AddType(beta);
            world.Start();
            TickMascot m = world.CreateAtStart("m1", alpha, new TickPoint(640, 680), false, start);
            for (int i = 0; i < 5; i++) world.StepOnce();
            witness(m);
            world.StepOnce();
            return m;
        }

        private static string ReceiverState(TickMascot m, TickLog log)
        {
            string b = m.Behaviour == null ? "no behaviour"
                : m.Behaviour.Name + " held by " + m.Behaviour.Type.Name + " running " + m.Behaviour.Action.Def.Name + " #" + m.Behaviour.Action.Def.NodeId;
            return m.Type.Name + ", " + b + ", " + m.Hotspots.Count + " hotspot(s): " + string.Join("; ", log.Events) + " | " + string.Join("; ", log.Faults);
        }

        /// <summary>The retained state, asserted before the press as L6 does: the companion is a Beta, in Alpha's Pat
        /// (held by Alpha, Alpha's own definition), with Pat's one hotspot, after exactly the failed Transform's fault.</summary>
        private static void ReceiverRetained(string leg, TickMascot m, TickType alpha, TickType beta, TickLog log)
        {
            AssertTrue(ReferenceEquals(m.Type, beta) && m.Behaviour != null && ReferenceEquals(m.Behaviour.Type, alpha)
                       && ReferenceEquals(m.Behaviour.Def, alpha.RootByName["Pat"]) && m.Hotspots.Count == 1
                       && log.Faults.Count == 1 && log.Faults[0].Contains("Transform into Beta"),
                "WITNESS: the retained state before the press is not a Beta in Alpha's Pat with its one hotspot (" + leg + "): " + ReceiverState(m, log));
        }

        /// <summary>The press took H's hotspot and built H through <paramref name="through"/>: that type's H definition,
        /// running that type's action <paramref name="actionId"/>.</summary>
        private static bool ReceiverPressedThrough(TickMascot m, TickLog log, string from, TickType through, int actionId)
        {
            return log.Events.Contains("m1 hotspot press H") && log.Events.Contains("m1 behaviour " + from + ">H forced")
                   && ReferenceEquals(m.Behaviour.Type, through) && ReferenceEquals(m.Behaviour.Def, through.RootByName["H"])
                   && ReferenceEquals(m.Behaviour.Action.Def, through.ActionsById[actionId]);
        }

        /// <summary>No hotspot took the press, and the companion was dragged through Alpha: Alpha's Dragged definition,
        /// running AlphaDragAct (Beta has its own Dragged, so a drag built through Beta would show here).</summary>
        private static bool ReceiverDraggedThroughAlpha(TickMascot m, TickLog log, TickType alpha)
        {
            return !log.Events.Exists(e => e.StartsWith("m1 hotspot", StringComparison.Ordinal))
                   && log.Events.Contains("m1 drag start") && log.Events.Contains("m1 behaviour Pat>Dragged forced")
                   && ReferenceEquals(m.Behaviour.Type, alpha) && ReferenceEquals(m.Behaviour.Def, alpha.RootByName["Dragged"])
                   && ReferenceEquals(m.Behaviour.Action.Def, alpha.ActionsById[34]);
        }

        private static void TestTickReceiverL2()
        {
            // L2: retained, Beta.H present and not toggleable. Eligible in Beta; built through Alpha, so AlphaHAct runs. A
            // construction receiver read from the current type (M2) builds Beta's H and runs BetaHAct.
            TickType alpha = ReceiverAlpha(""), beta = ReceiverBeta(true, false, false);
            var log = new TickLog();
            TickMascot m = ReceiverPress(alpha, beta, "Morph", new SwitchSettings(), log, 618, 626, x => ReceiverRetained("L2", x, alpha, beta, log));
            AssertTrue(ReceiverPressedThrough(m, log, "Pat", alpha, 32),
                "A hotspot press in the retained state was not built through the holding type's H and AlphaHAct (L2): " + ReceiverState(m, log));
        }

        private static void TestTickReceiverL3()
        {
            // L3: retained, Beta has no H. Not eligible in Beta, so no press; Pat is draggable, so the companion is dragged
            // through Alpha. An eligibility receiver read from the holding type (M1) finds Alpha's H and presses.
            TickType alpha = ReceiverAlpha(""), beta = ReceiverBeta(false, false, false);
            var log = new TickLog();
            TickMascot m = ReceiverPress(alpha, beta, "Morph", new SwitchSettings(), log, 618, 626, x => ReceiverRetained("L3", x, alpha, beta, log));
            AssertTrue(ReceiverDraggedThroughAlpha(m, log, alpha),
                "A hotspot naming a behaviour the current type lacks took the press in the retained state, or the drag was not built through the holding type (L3): "
                + ReceiverState(m, log));
        }

        private static void TestTickReceiverL4()
        {
            // L4: retained, Beta's H toggleable (Hing) and switched off for Beta; Alpha's H not toggleable. Not eligible in
            // Beta, so dragged through Alpha. An eligibility receiver read from Alpha (M1) sees an H with no toggle and
            // presses; with a toggleable Alpha.H it would read Beta's switches too and survive, hence Alpha's H has none.
            TickType alpha = ReceiverAlpha(""), beta = ReceiverBeta(true, true, false);
            var settings = new SwitchSettings();
            settings.Store.Set("Beta", "toggle:Hing", false);
            var log = new TickLog();
            TickMascot m = ReceiverPress(alpha, beta, "Morph", settings, log, 618, 626, x => ReceiverRetained("L4", x, alpha, beta, log));
            AssertTrue(ReceiverDraggedThroughAlpha(m, log, alpha),
                "A hotspot whose behaviour the current type switches off took the press in the retained state, or the drag was not built through the holding type (L4): "
                + ReceiverState(m, log));
        }

        private static void TestTickReceiverControls()
        {
            // L0, L1 and L5 behave the same under every receiver mutant (anchor packet): with no Transform, or a Transform that
            // succeeded, the holding type IS the current type; and a press outside every hotspot is a drag, which builds
            // through the holding type whatever the press receivers read.
            {
                TickType alpha = ReceiverAlpha(""), beta = ReceiverBeta(true, false, false);
                var log = new TickLog();
                TickMascot m = ReceiverPress(alpha, beta, "Pat", new SwitchSettings(), log, 618, 626, x =>
                    AssertTrue(ReferenceEquals(x.Type, alpha) && x.Behaviour != null && ReferenceEquals(x.Behaviour.Type, alpha)
                               && ReferenceEquals(x.Behaviour.Def, alpha.RootByName["Pat"]) && x.Hotspots.Count == 1 && log.Faults.Count == 0,
                        "WITNESS: before the press the companion is not an Alpha in Alpha's Pat with its one hotspot (L0): " + ReceiverState(x, log)));
                AssertTrue(ReceiverPressedThrough(m, log, "Pat", alpha, 32),
                    "WITNESS: with no Transform a hotspot press was not built through Alpha's H and AlphaHAct (L0): " + ReceiverState(m, log));
            }
            {
                TickType alpha = ReceiverAlpha("BPat"), beta = ReceiverBeta(true, false, true);
                var log = new TickLog();
                TickMascot m = ReceiverPress(alpha, beta, "Morph", new SwitchSettings(), log, 618, 626, x =>
                    AssertTrue(ReferenceEquals(x.Type, beta) && x.Behaviour != null && ReferenceEquals(x.Behaviour.Type, beta)
                               && ReferenceEquals(x.Behaviour.Def, beta.RootByName["BPat"]) && x.Hotspots.Count == 1 && log.Faults.Count == 0,
                        "WITNESS: before the press the Transform did not leave a Beta in Beta's BPat with its one hotspot (L1): " + ReceiverState(x, log)));
                AssertTrue(ReceiverPressedThrough(m, log, "BPat", beta, 71),
                    "WITNESS: after a Transform that succeeded a hotspot press was not built through Beta's H and BetaHAct (L1): " + ReceiverState(m, log));
            }
            {
                // (650, 660) is window point (42, 44): inside the 64x64 window, outside the 20x20 hotspot at its top left.
                TickType alpha = ReceiverAlpha(""), beta = ReceiverBeta(true, false, false);
                var log = new TickLog();
                TickMascot m = ReceiverPress(alpha, beta, "Morph", new SwitchSettings(), log, 650, 660, x => ReceiverRetained("L5", x, alpha, beta, log));
                AssertTrue(ReceiverDraggedThroughAlpha(m, log, alpha),
                    "WITNESS: a press outside every hotspot in the retained state was not a drag through the holding type (L5): " + ReceiverState(m, log));
            }
        }

        /// <summary>An action whose one variant carries a 20x20 hotspot at the cell's top left naming <paramref name="behaviour"/>.</summary>
        private static XmlData.AnimationNode HotspotAct(XmlData.AnimationNode act, string behaviour)
        {
            act.Act.Variants.Variant[0].Hotspots = new XmlData.HotspotsNode { Hotspot = new[] { THotspot(behaviour) } };
            return act;
        }

        private static void TestTickSettingsRows()
        {
            // The type the report names: Breed (BornTransient unset), Transform, WalkWithIE and two header toggles.
            TickType full = TickType.Compile("Full", WithToggles(TickDoc(
                    TRoute(TEdge(10, 1), TEdge(11, 1), TEdge(12, 1), TEdge(13, 1)),
                    TBeh(10, "Throw", "Throw", 1, null),
                    TBeh(11, "Morph", "Morph", 1, null),
                    Toggled(TBeh(12, "Carry", "Carry", 1, null), "Carrying"),
                    Toggled(TBeh(13, "Stand", "Stand", 1, null), "Standing"),
                    TAct(30, "Throw", "Embedded", "Breed", new[] { TParam("BornBehavior", "Stand") }, TPose(0, 1)),
                    TAct(31, "Morph", "Embedded", "Transform", new[] { TParam("TransformBehavior", "Stand") }, TPose(0, 1)),
                    TAct(32, "Carry", "Embedded", "WalkWithIE", null, TPose(0, 1)),
                    TAct(33, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(0, 1))),
                new XmlData.ToggleNode { Id = "Carrying", Label = "Carries things" },
                new XmlData.ToggleNode { Id = "Standing", Label = "Stands still", Default = false }));
            List<TickSettingRow> rows = TickSettingRows.For(full);
            AssertEqual("Breeding,Transformation,WindowActions,toggle:Carrying,toggle:Standing", RowKeys(full),
                "The pane's rows for Breed + Transform + WalkWithIE + two toggles are not exactly those, in that order.");
            AssertTrue(rows[2].Label == "Let it move windows" && !rows[2].DefaultOn && rows[0].DefaultOn && rows[1].DefaultOn
                       && rows[3].Label == "Carries things" && rows[3].DefaultOn && rows[4].Label == "Stands still" && !rows[4].DefaultOn,
                "A row's label or default is wrong (window actions off, a toggle its header's label and default).");

            // BornTransient decides which switch governs a birth: a literal true is Transients only; a script, either.
            Func<XmlData.ParamNode[], XmlData.AnimationNode, TickType> breeder = (ps, behaviour) => TickType.Compile("B", TickDoc(
                TRoute(TEdge(10, 1)), behaviour,
                TAct(30, "Throw", "Embedded", "Breed", ps, TPose(0, 1))));
            AssertEqual("Transients", RowKeys(breeder(new[] { TParam("BornTransient", "true") }, TBeh(10, "Throw", "Throw", 1, null))),
                "A Breed with BornTransient true offered Breeding, or no Transients.");
            AssertEqual("Breeding", RowKeys(breeder(new[] { TParam("BornTransient", "false") }, TBeh(10, "Throw", "Throw", 1, null))),
                "A Breed with BornTransient false offered Transients.");
            AssertEqual("Breeding,Transients", RowKeys(breeder(new[] { TParam("BornTransient", "mascot.anchor.x > 0", "action") }, TBeh(10, "Throw", "Throw", 1, null))),
                "A Breed with a scripted BornTransient did not offer both switches.");
            // A behaviour's binding replaces the action's own value, as ActionBuilder.createActions does.
            AssertEqual("Transients", RowKeys(breeder(new[] { TParam("BornTransient", "false") }, Bound(TBeh(10, "Throw", "Throw", 1, null), "BornTransient", "true"))),
                "A behaviour binding BornTransient true did not decide the switch.");

            // ComplexMove breeds only when its Characteristics name Breed, split on commas and compared exactly.
            Func<string, TickType> complex = c => TickType.Compile("C", TickDoc(
                TRoute(TEdge(10, 1)), TBeh(10, "Go", "Go", 1, null),
                TAct(30, "Go", "Embedded", "ComplexMove", new[] { TParam("Characteristics", c), TParam("BornTransient", "true") }, TPose(0, 1))));
            AssertEqual("", RowKeys(complex("Scan, Breed")), "A ComplexMove whose Characteristics do not name Breed exactly offered a breed switch.");
            AssertEqual("Transients", RowKeys(complex("Scan,Breed")), "A ComplexMove whose Characteristics name Breed offered no breed switch.");

            // Only what something runs: an action no behaviour or composite reaches offers nothing, and a type with
            // nothing to switch (the shape of a classic-style tick companion) offers no row at all.
            TickType unused = TickType.Compile("U", TickDoc(
                TRoute(TEdge(10, 1)), TBeh(10, "Stand", "Stand", 1, null),
                TAct(30, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(0, 1)),
                TAct(31, "Throw", "Embedded", "Breed", null, TPose(0, 1)),
                TAct(32, "Carry", "Embedded", "ThrowIE", null, TPose(0, 1))));
            AssertEqual("", RowKeys(unused), "An action nothing runs offered a switch.");
            // ...but a composite's child counts as running it.
            TickType viaChild = TickType.Compile("V", TickDoc(
                TRoute(TEdge(10, 1)), TBeh(10, "Act", "Seq", 1, null),
                new XmlData.AnimationNode
                {
                    Id = 33, Name = "Seq",
                    Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
                    Act = new XmlData.ActNode
                    {
                        Type = "Sequence",
                        Children = new XmlData.ChildrenNode { Child = new[] { new XmlData.CompositeChildNode { Animation = "Min" } } },
                    },
                },
                TAct(34, "Min", "Embedded", "MinimizeIE", null, TPose(0, 1))));
            AssertEqual("WindowActions", RowKeys(viaChild), "A window action run as a composite's child offered no window switch.");
        }
    }
}
