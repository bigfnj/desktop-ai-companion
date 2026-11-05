using System;
using System.Collections.Generic;
using System.IO;
using DesktopAICompanion.Shimeji;

namespace DesktopAICompanion
{
    /// <summary>
    /// Packs (grimoire/06 sections 2, 9 and 10): members resolve each other by key inside their own pack, a missing one
    /// keeps the current type with one diagnostic, the host stages an unknown member once, a main member starts as its
    /// startup says (an egg only while the user wants one), the index picks one installed member deterministically, and
    /// the pack-level rules refuse what cannot run as one unit. Every pack is built in code.
    /// </summary>
    internal static partial class Program
    {
        // ---- building pack members

        private static XmlData.RootNode PackDoc(string pack, string member, string role, XmlData.StartupNode startup,
            params XmlData.AnimationNode[] nodes)
        {
            XmlData.RootNode root = TickDoc(nodes);
            root.Header.Features = "tick actions behaviours expressions lifecycle packs";
            root.Header.Pack = new XmlData.PackNode { Id = pack, Member = member, Role = role };
            root.Header.Startup = startup;
            return root;
        }

        /// <summary>An egg: Laid transforms into <paramref name="into"/> on its penultimate tick (two one-tick poses).</summary>
        private static TickType PackEgg(string name, string pack, string into)
        {
            return TickType.Compile(name, PackDoc(pack, "Egg", "egg", null,
                TRoute(TEdge(10, 1)),
                TBeh(10, "Laid", "Laid", 1, null),
                TAct(30, "Laid", "Embedded", "Transform", new[] { TParam("TransformMascot", into), TParam("TransformBehavior", "Stand") },
                    TPose(0, 1), TPose(1, 1))));
        }

        /// <summary>A grown member: Stand, and Throw, which breeds one <paramref name="born"/>.</summary>
        private static TickType PackGrown(string name, string pack, string member, string born)
        {
            return TickType.Compile(name, PackDoc(pack, member, "main", null,
                TRoute(TEdge(10, 1), TEdge(11, 0)),
                TBeh(10, "Stand", "Stand", 1, null),
                TBeh(11, "Throw", "Throw", 0, null),
                TAct(30, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(2, 1)),
                TAct(31, "Throw", "Embedded", "Breed",
                    new[] { TParam("BornX", "0"), TParam("BornMascot", born), TParam("BornBehavior", "Stand") },
                    TPose(0, 1), TPose(1, 1))));
        }

        private sealed class PackDiagnostics
        {
            public readonly List<string> Unresolved = new List<string>();
            public void Attach(TickWorld w) { w.UnresolvedReference = (t, n) => Unresolved.Add(t.Name + "->" + n); }
        }

        // ---- groups

        private static void TestTickPacksResolveInScope()
        {
            // Two packs, both with an Egg and an Alpha; a packless type named "Alpha" too. Pack 1's Alpha is added first,
            // so a lookup by member key alone, or by name across scopes, would hatch pack 2's egg into the wrong Alpha.
            TickType alpha1 = PackGrown("p1-alpha", "one", "Alpha", "");
            TickType alpha2 = PackGrown("p2-alpha", "two", "Alpha", "");
            TickType loose = TickType.Compile("Alpha", TickDoc(TRoute(TEdge(10, 1)), TBeh(10, "Stand", "Stand", 1, null),
                TAct(30, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(3, 1))));
            TickType egg1 = PackEgg("p1-egg", "one", "Alpha");
            TickType egg2 = PackEgg("p2-egg", "two", "Alpha");
            var log = new TickLog();
            TickWorld w = TickWorldOf(log, null, loose, alpha1, alpha2, egg1, egg2);
            TickMascot a = w.CreateAtStart("m1", egg2, new TickPoint(600, 680), false, "Laid");
            TickMascot b = w.CreateAtStart("m2", egg1, new TickPoint(700, 680), false, "Laid");
            w.StepOnce();
            w.StepOnce();
            AssertTrue(ReferenceEquals(a.Type, alpha2) && ReferenceEquals(b.Type, alpha1),
                "An egg did not hatch into the Alpha of its OWN pack: " + a.Type.Name + ", " + b.Type.Name);
            AssertTrue(log.Events.Contains("m1 transform p2-egg>p2-alpha Stand"),
                "The transform event does not name the types by their unique names: " + string.Join("; ", log.Events));
            AssertTrue(ReferenceEquals(w.ResolveReference(loose, "Alpha"), loose) && ReferenceEquals(w.ResolveReference(egg1, "Alpha"), alpha1),
                "A packless reference did not resolve among packless types by name, or a pack reference left its pack.");
        }

        private static void TestTickPacksFallback()
        {
            // Alpha breeds a member its pack does not have: the child is an Alpha (Breed.java:76), and the reference is
            // reported once for Alpha however often it is made. Egg making the same reference is a second (type, reference).
            TickType alpha = PackGrown("p-alpha", "p", "Alpha", "Ghost");
            TickType egg = PackEgg("p-egg", "p", "Ghost");
            var diag = new PackDiagnostics();
            int asked = 0;
            var log = new TickLog();
            TickWorld w = TickWorldOf(log, null, alpha, egg);
            diag.Attach(w);
            w.TypeResolver = (from, name) => { asked++; return null; };
            w.CreateAtStart("m1", alpha, new TickPoint(600, 680), false, "Throw");
            w.StepOnce();
            w.StepOnce();   // penultimate tick: the birth
            AssertTrue(w.Pending.Count == 1 && ReferenceEquals(w.Pending[0].Type, alpha),
                "A birth of a missing member was not of the parent's own type.");
            AssertTrue(ReferenceEquals(w.ResolveReference(alpha, "Ghost"), alpha), "A missing member did not resolve to the current type.");
            AssertEqual(1, diag.Unresolved.Count, "A missing member was not reported exactly once for its (type, reference): " + string.Join("; ", diag.Unresolved));
            AssertEqual(1, asked, "The host was asked again for a member it had already failed to stage.");
            AssertTrue(ReferenceEquals(w.ResolveReference(egg, "Ghost"), egg) && diag.Unresolved.Count == 2 && diag.Unresolved[1] == "p-egg->Ghost",
                "The same missing reference from another type was not its own diagnostic: " + string.Join("; ", diag.Unresolved));
            AssertTrue(ReferenceEquals(w.ResolveReference(alpha, ""), alpha) && diag.Unresolved.Count == 2 && asked == 2,
                "An empty name (the source's default) was reported or asked of the host.");
        }

        private static void TestTickPacksResolver()
        {
            // Alpha breeds Ball, which the world does not have: the host stages it once, and every later birth finds it.
            TickType alpha = PackGrown("p-alpha", "p", "Alpha", "Ball");
            TickType ball = TickType.Compile("p-ball", PackDoc("p", "Ball", "helper", null,
                TRoute(TEdge(10, 1)), TBeh(10, "Stand", "Stand", 1, null),
                TAct(30, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(3, 1))));
            TickType stranger = TickType.Compile("q-ball", PackDoc("q", "Ball", "helper", null,
                TRoute(TEdge(10, 1)), TBeh(10, "Stand", "Stand", 1, null),
                TAct(30, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(3, 1))));
            var diag = new PackDiagnostics();
            var asked = new List<string>();
            var log = new TickLog();
            TickWorld w = TickWorldOf(log, null, alpha);
            diag.Attach(w);
            w.TypeResolver = (from, name) =>
            {
                asked.Add(from.Name + "->" + name);
                w.AddType(ball);
                return ball;
            };
            w.CreateAtStart("m1", alpha, new TickPoint(600, 680), false, "Throw");
            w.CreateAtStart("m2", alpha, new TickPoint(800, 680), false, "Throw");
            w.StepOnce();
            w.StepOnce();
            AssertTrue(w.Pending.Count == 2 && ReferenceEquals(w.Pending[0].Type, ball) && ReferenceEquals(w.Pending[1].Type, ball),
                "A birth of a member the host stages was not of that member.");
            AssertEqual("p-alpha->Ball", string.Join("|", asked), "The host was not asked exactly once for the member.");
            AssertEqual(0, diag.Unresolved.Count, "A member the host staged was reported missing.");

            // A host that answers with another pack's member is not believed: the reference falls back and is reported.
            TickType alpha2 = PackGrown("r-alpha", "r", "Alpha", "Ball");
            var log2 = new TickLog();
            TickWorld w2 = TickWorldOf(log2, null, alpha2);
            var diag2 = new PackDiagnostics();
            diag2.Attach(w2);
            w2.TypeResolver = (from, name) => { w2.AddType(stranger); return stranger; };
            AssertTrue(ReferenceEquals(w2.ResolveReference(alpha2, "Ball"), alpha2) && diag2.Unresolved.Count == 1,
                "A member staged from another pack answered the reference.");

            // A type removed from the world (its frames released) is asked of the host again at the next reference.
            w.RemoveType(ball);
            w.ResolveReference(alpha, "Ball");
            AssertEqual(2, asked.Count, "A removed member was still found without asking the host.");
        }

        private static void TestTickPacksStartPlan()
        {
            Func<string, TickType> main = startupMember =>
                TickType.Compile("p-alpha", PackDoc("p", "Alpha", "main",
                    startupMember == null ? null : new XmlData.StartupNode { Member = startupMember, Behaviour = "Laid" },
                    TRoute(TEdge(10, 1)), TBeh(10, "Laid", "Stand", 1, null),
                    TAct(30, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(2, 1))));
            TickType eggStart = main("Egg");
            TickStartPlan on = TickPack.StartPlan(eggStart, "egg", true);
            AssertTrue(on.Member == "Egg" && on.Behaviour == "Laid", "An egg startup did not start as the egg, entering the startup behaviour.");
            TickStartPlan off = TickPack.StartPlan(eggStart, "egg", false);
            AssertTrue(off.Member == null && off.Behaviour == null,
                "With Arrive as an egg off the main member did not start directly with the route's choice.");
            AssertTrue(TickPack.OffersEggArrival(eggStart, "egg") && !TickPack.OffersEggArrival(eggStart, "form"),
                "Arrive as an egg was not offered for an egg startup, or was offered for a non-egg one.");
            TickStartPlan form = TickPack.StartPlan(eggStart, "form", false);
            AssertTrue(form.Member == "Egg" && form.Behaviour == "Laid", "A non-egg startup member was skipped by the egg preference.");
            TickStartPlan self = TickPack.StartPlan(main("Alpha"), "main", false);
            AssertTrue(self.Member == null && self.Behaviour == "Laid" && !TickPack.OffersEggArrival(main("Alpha"), "egg"),
                "A startup naming the main member itself did not just give the entry behaviour.");
            TickStartPlan none = TickPack.StartPlan(main(null), null, true);
            AssertTrue(none.Member == null && none.Behaviour == null, "A main member with no startup did not start as the source's createMascot.");
        }

        private static void TestTickPacksIndex()
        {
            Func<string, bool, string, PackIndexEntry> entry = (id, library, member) => new PackIndexEntry
            {
                TypeId = id, FromLibrary = library, Facts = new PackHeaderFacts { PackId = "dragonet", Member = member, Role = "egg" },
            };
            CompanionPackIndex index = CompanionPackIndex.Build(new[]
            {
                entry("a-egg", false, "Egg"), entry("z-egg", true, "Egg"), entry("B-egg", true, "Egg"), entry("alpha", false, "Alpha"),
            });
            PackIndexEntry egg = index.Find("dragonet", "Egg");
            AssertTrue(egg != null && egg.TypeId == "B-egg",
                "The library's first folder (ordinal, case-insensitive) did not win a duplicated member: " + (egg == null ? "none" : egg.TypeId));
            AssertEqual(2, index.Duplicates.Count, "Each duplicate that lost was not reported.");
            AssertTrue(index.Find("dragonet", "Alpha").TypeId == "alpha" && index.Find("dragonet", "egg") == null && index.Find("other", "Egg") == null,
                "A member was found by another case or in another pack.");

            const string header = "<?xml version=\"1.0\"?><animations><header><author>a</author>"
                + "<pack id=\"dragonet\" member=\"Egg\" role=\"egg\" hidden=\"true\"/><startup member=\"Egg\" behaviour=\"Laid\"/>"
                + "<icon>AA==</icon></header><image>";
            PackHeaderFacts facts = PackHeaderFacts.FromHeaderText(header);
            AssertTrue(facts != null && facts.PackId == "dragonet" && facts.Member == "Egg" && facts.Role == "egg" && facts.Hidden
                       && facts.StartupMember == "Egg" && facts.StartupBehaviour == "Laid",
                "The header's pack and startup facts were not read.");
            AssertTrue(PackHeaderFacts.FromHeaderText("<animations><header><petname>x</petname></header>") == null
                       && PackHeaderFacts.FromHeaderText("<animations><header><pack id=\"p\" member=\"m\"></header>") == null
                       && !PackHeaderFacts.FromHeaderText(header.Replace(" hidden=\"true\"", "")).Hidden,
                "A header with no pack, a malformed one, or one without hidden was read as a hidden member.");
        }

        private static void TestTickPacksValidator()
        {
            Func<string, XmlData.AnimationNode[]> grown = born => new[]
            {
                TRoute(TEdge(10, 1), TEdge(11, 0)), TBeh(10, "Stand", "Stand", 1, null), TBeh(11, "Throw", "Throw", 0, null),
                TAct(30, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(2, 1)),
                TAct(31, "Throw", "Embedded", "Breed", new[] { TParam("BornMascot", born), TParam("BornBehavior", "Stand") }, TPose(0, 1), TPose(1, 1)),
            };
            Func<string, XmlData.AnimationNode[]> egg = into => new[]
            {
                TRoute(TEdge(10, 1)), TBeh(10, "Laid", "Laid", 1, null),
                TAct(30, "Laid", "Embedded", "Transform", new[] { TParam("TransformMascot", into), TParam("TransformBehavior", "Stand") }, TPose(0, 1), TPose(1, 1)),
            };
            var startEgg = new XmlData.StartupNode { Member = "Egg", Behaviour = "Laid" };
            Func<string, string, string, PackValidator.Report> pack = (born, into, eggRole) => PackValidator.Validate(new[]
            {
                new PackValidator.Member("alpha", PackDoc("p", "Alpha", "main", startEgg, grown(born))),
                new PackValidator.Member("egg", PackDoc("p", "Egg", eggRole, null, egg(into))),
                new PackValidator.Member("ball", PackDoc("p", "Ball", "helper", null, grown(""))),
            });
            PackValidator.Report good = pack("Ball", "Alpha", "egg");
            AssertTrue(good.Ok && good.Warnings.Count == 0, "A sound pack was refused or warned: " + string.Join("; ", good.Errors) + string.Join("; ", good.Warnings));
            PackValidator.Report missing = pack("Ghost", "Alpha", "egg");
            AssertTrue(missing.Ok && missing.Warnings.Count == 1 && missing.Warnings[0].Contains("'Ghost'"),
                "A reference to a missing member was refused or not reported: " + string.Join("; ", missing.Errors) + string.Join("; ", missing.Warnings));
            PackValidator.Report stuck = pack("Ball", "Ball", "egg");
            AssertTrue(!stuck.Ok && stuck.Errors[0].Contains("never transforms back"), "An egg startup that never hatches into the main member was accepted.");
            AssertTrue(pack("Ball", "Ball", "form").Ok, "A non-egg startup member was held to the egg rule.");
            PackValidator.Report twoMains = pack("Ball", "Alpha", "main");
            AssertTrue(!twoMains.Ok && twoMains.Errors[0].Contains("exactly one main"), "A pack with two main members was accepted.");

            XmlData.RootNode keyed = PackDoc("p", "Alpha", "helper", null, grown("Ball"));
            XmlData.RootNode tinted = PackDoc("p", "Ball", "helper", null, grown(""));
            tinted.Image.Transparency = "Magenta";
            PackValidator.Report clash = PackValidator.Validate(new[]
            {
                new PackValidator.Member("alpha", PackDoc("p", "Alpha", "main", null, grown("Ball"))),
                new PackValidator.Member("alpha2", keyed),
                new PackValidator.Member("ball", tinted),
            });
            AssertTrue(clash.Errors.Exists(e => e.Contains("'Alpha' is used by both")) && clash.Errors.Exists(e => e.Contains("same transparency")),
                "A repeated member key or a second transparency mode was accepted: " + string.Join("; ", clash.Errors));
            var noEgg = new XmlData.StartupNode { Member = "Nest", Behaviour = "Laid" };
            PackValidator.Report lost = PackValidator.Validate(new[] { new PackValidator.Member("alpha", PackDoc("p", "Alpha", "main", noEgg, grown(""))) });
            AssertTrue(!lost.Ok && lost.Errors[0].Contains("'Nest'"), "A startup naming a member the pack does not have was accepted.");
        }

        private static void TestTickPacksEggPreference()
        {
            string directory = NewDirectory("settings-egg-arrival");
            string path = Path.Combine(directory, "settings.json");
            AppSettingsDocument fresh = new AppSettingsStore(path, new string[0]).Load();
            AssertTrue(fresh.EggArrivalOff != null && fresh.EggArrivalOff.Count == 0, "Arrive as an egg was not on for every type by default.");

            var store = new AppSettingsStore(path, null);
            AppSettingsDocument doc = store.Load();
            doc.EggArrivalOff = new List<string> { "dragonet", "Dragonet", "", "../evil" };
            AssertTrue(store.Save(doc), "The egg-arrival doc could not be saved.");
            AppSettingsDocument reloaded = new AppSettingsStore(path, null).Load();
            AssertTrue(reloaded.EggArrivalOff.Count == 1 && reloaded.EggArrivalOff[0] == "dragonet",
                "The egg-arrival list was not deduped and filtered (no active slot, no unsafe id): " + string.Join(",", reloaded.EggArrivalOff));

            // Another writer saved in between: this store's change still lands (the merge copies the field).
            var mine = new AppSettingsStore(path, null);
            AppSettingsDocument mineDoc = mine.Load();
            var other = new AppSettingsStore(path, null);
            AppSettingsDocument otherDoc = other.Load();
            otherDoc.Volume = 0.25;
            AssertTrue(other.Save(otherDoc), "The other writer could not save.");
            mineDoc.EggArrivalOff = new List<string> { "dragonet", "wyvern" };
            AssertTrue(mine.Save(mineDoc), "The egg-arrival change could not be saved over another writer's.");
            AppSettingsDocument merged = new AppSettingsStore(path, null).Load();
            AssertTrue(merged.EggArrivalOff.Count == 2 && merged.EggArrivalOff.Contains("wyvern") && Math.Abs(merged.Volume - 0.25) < 1e-9,
                "A save merged over another writer's lost the egg-arrival change or the other writer's.");

            // The recovery path: the file is gone, so the save clones the document in memory.
            var recovering = new AppSettingsStore(path, null);
            AppSettingsDocument rec = recovering.Load();
            File.Delete(path);
            rec.Volume = 0.5;
            AssertTrue(recovering.Save(rec), "The recovery save failed.");
            AssertTrue(new AppSettingsStore(path, null).Load().EggArrivalOff.Contains("wyvern"),
                "A save that could not read the old file dropped the egg-arrival list.");
        }
    }
}
