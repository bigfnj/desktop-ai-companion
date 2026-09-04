using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DesktopAICompanion.Tools.ShimejiConvert.Emit;
using DesktopAICompanion.Tools.ShimejiConvert.Shimeji;

namespace DesktopAICompanion.Tools.ShimejiConvert
{
    /// <summary>
    /// Console entry point for the Shimeji -> animations.xml converter (BACKLOG #4).
    ///
    /// The first verb is deliberately not "convert" but "verify": before emitting a single pet, the tool
    /// has to prove it can read, grade and re-emit the 22 pets this repo already ships. Those pets are a
    /// free correctness corpus for the output half of the converter -- if the round-trip cannot survive a
    /// hand-authored pet, it will not survive a generated one, and the failure would otherwise be blamed
    /// on the Shimeji mapping.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 0) return Usage();

            switch (args[0])
            {
                case "verify":
                    if (args.Length != 2) return Usage();
                    return Verify(args[1]);
                case "classify":
                    if (args.Length != 2) return Usage();
                    return Classify(args[1]);
                case "selftest":
                    if (args.Length != 1) return Usage();
                    return SelfTest();
                case "composite":
                    if (args.Length != 4) return Usage();
                    return Composite(args[1], args[2], args[3]);
                case "convert":
                    if (args.Length != 5) return Usage();
                    return ConvertVerb(args[1], args[2], args[3], args[4]);
                case "convertbundle":
                    if (args.Length != 3) return Usage();
                    return ConvertBundleVerb(args[1], args[2]);
                case "convertroot":
                    if (args.Length != 2 && args.Length != 3) return Usage();
                    return ConvertRoot(args[1], args.Length == 3 ? args[2] : null);
                case "rebalance":
                    if (args.Length != 2) return Usage();
                    return Rebalance(args[1]);
                case "reweight":
                    if (args.Length != 2) return Usage();
                    return Reweight(args[1]);
                case "rejump":
                    if (args.Length != 2) return Usage();
                    return Rejump(args[1]);
                case "reclimb":
                    if (args.Length != 2) return Usage();
                    return Reclimb(args[1]);
                case "restdwell":
                    if (args.Length != 2) return Usage();
                    return RestDwell(args[1]);
                case "restsplit":
                    if (args.Length != 2) return Usage();
                    return RestSplit(args[1]);
                case "dedupe":
                    if (args.Length != 2) return Usage();
                    return Dedupe(args[1]);
                case "undirect":
                    if (args.Length != 2) return Usage();
                    return Undirect(args[1]);
                default:
                    return Usage();
            }
        }

        private static int Usage()
        {
            Console.Error.WriteLine("ShimejiConvert -- Shimeji skin -> desktopPet animations.xml (offline)");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  verify <PetsDir>   Grade every pet under <PetsDir> with the app's own validator,");
            Console.Error.WriteLine("                     run the reachability pass, and round-trip the XML.");
            Console.Error.WriteLine("  classify <ConfDir> Parse a Shimeji conf dir (actions.xml + behaviors.xml) and print the");
            Console.Error.WriteLine("                     Group 1/2/3 fidelity census: what converts cleanly, what degrades,");
            Console.Error.WriteLine("                     and what is dropped as residue.");
            Console.Error.WriteLine("  selftest           Run the engine self-tests (classifier + compositor; synthetic fixtures).");
            Console.Error.WriteLine("  composite <ConfDir> <ImgDir> <out.png>");
            Console.Error.WriteLine("                     DEV: composite a real skin's sprites into one magenta-keyed sheet");
            Console.Error.WriteLine("                     and write it, for eyeballing. Point at an external clone.");
            Console.Error.WriteLine("  convert <ConfDir> <ImgDir> <SkinName> <out.xml>");
            Console.Error.WriteLine("                     Convert a Shimeji skin to a desktopPet animations.xml and write it");
            Console.Error.WriteLine("                     plus <out.xml>.residue.txt. Exit 0 only if the pet is accepted");
            Console.Error.WriteLine("                     (valid + round-trips + fully reachable). Pass - as <ConfDir> to");
            Console.Error.WriteLine("                     use the bundled base conf (a sprites-only skin).");
            Console.Error.WriteLine("  convertbundle <BundleDir> <out.xml>");
            Console.Error.WriteLine("                     Convert a modern Android Shimeji JSON+WebP bundle (manifest.json +");
            Console.Error.WriteLine("                     animation.json + sprites/*.webp) to a desktopPet animations.xml and");
            Console.Error.WriteLine("                     write it plus <out.xml>.residue.txt. Prints ACCEPTED and exits 0 only");
            Console.Error.WriteLine("                     if the pet is accepted (valid + round-trips + fully reachable).");
            Console.Error.WriteLine("  rebalance <PetsDir>");
            Console.Error.WriteLine("                     Migration: re-time already-emitted locomotion animations under <PetsDir>");
            Console.Error.WriteLine("                     to the current walk-time budget (so a slow walk no longer glides for");
            Console.Error.WriteLine("                     ~36s in one direction), rewriting only the pets that change through the");
            Console.Error.WriteLine("                     engine's own parser + serializer. Non-converted pets are left untouched.");
            Console.Error.WriteLine("  reweight <PetsDir>");
            Console.Error.WriteLine("                     Migration: re-weight the hub transitions of already-converted pets under");
            Console.Error.WriteLine("                     <PetsDir> through the current damped curve + minimum-share floor, so an");
            Console.Error.WriteLine("                     animation can no longer be technically reachable but practically never");
            Console.Error.WriteLine("                     played. Only touches pets whose header says they came from ShimejiConvert");
            Console.Error.WriteLine("                     AND are still at the pre-damping format version; hand-authored pets and");
            Console.Error.WriteLine("                     already-migrated pets are skipped, so it is safe to re-run.");
            Console.Error.WriteLine("  rejump <PetsDir>");
            Console.Error.WriteLine("                     Migration: give already-converted jumps the three-phase shape -- an arc");
            Console.Error.WriteLine("                     solved for a fixed height at a flat pace, a hand-off to `fall` when the");
            Console.Error.WriteLine("                     arc outlives the drop, and a landing that re-jumps or runs instead of");
            Console.Error.WriteLine("                     flipping and standing still. Rises too weak to be jumps are flattened.");
            Console.Error.WriteLine("                     Needs no source skins (no new sprite frames are involved). Same two gates");
            Console.Error.WriteLine("                     as reweight, so hand-authored and already-migrated pets are skipped.");
            Console.Error.WriteLine("  reclimb <PetsDir>");
            Console.Error.WriteLine("                     Migration: let a wall climb and a ceiling walk CROSS the surface in one");
            Console.Error.WriteLine("                     sequence instead of stopping every ~32px and rolling a 34% chance of");
            Console.Error.WriteLine("                     letting go, which put the screen ceiling 1 in 203,000 wall entries away.");
            Console.Error.WriteLine("                     Constant speed, flat interval, and enough repeats to cross any screen.");
            Console.Error.WriteLine("                     STATIC holds keep their time budget. Numbers only, so no source skins.");
            Console.Error.WriteLine("  restdwell <PetsDir>");
            Console.Error.WriteLine("                     Migration: shorten an over-long REST (held ~9s, single frames 10s) to the");
            Console.Error.WriteLine("                     hand-authored ~1.2s dwell, so a pet stops standing idle 79% of the time.");
            Console.Error.WriteLine("                     Only IDLE floor poses over the dwell ceiling are touched. Numbers only.");
            Console.Error.WriteLine("  restsplit <PetsDir>");
            Console.Error.WriteLine("                     Migration: split the rest dwell by role -- keep the HUB (return-to pose)");
            Console.Error.WriteLine("                     brief so the pet does not loiter, and lengthen every other idle to 9-12s");
            Console.Error.WriteLine("                     so a performance (sprawl, eat, dangle-legs) is long enough to watch.");
            Console.Error.WriteLine("                     Supersedes the over-correction of restdwell. Numbers only.");
            Console.Error.WriteLine("  dedupe <PetsDir>");
            Console.Error.WriteLine("                     Migration: drop sprite cells byte-identical to another cell, re-grid the");
            Console.Error.WriteLine("                     sheet and renumber every <frame>. Pixels only -- it proves every");
            Console.Error.WriteLine("                     animation renders the same images in the same order, and keeps the");
            Console.Error.WriteLine("                     original sheet for any pet the smaller grid does not actually shrink.");
            Console.Error.WriteLine("  undirect <PetsDir>");
            Console.Error.WriteLine("                     Migration: drop the _left / _right suffix from action names. A converted");
            Console.Error.WriteLine("                     pet mirrors its whole sheet on <action>flip</action>, so every animation");
            Console.Error.WriteLine("                     already plays both ways and the suffix reads as a limit it does not have.");
            Console.Error.WriteLine("                     Skips any rename that would collide or become a magic name. Names only.");
            return 2;
        }

        private static int Verify(string petsDirectory)
        {
            if (!Directory.Exists(petsDirectory))
            {
                Console.Error.WriteLine("No such directory: " + petsDirectory);
                return 2;
            }

            var pets = new List<string>();
            foreach (string candidate in Directory.GetDirectories(petsDirectory))
                if (File.Exists(Path.Combine(candidate, "animations.xml")))
                    pets.Add(candidate);

            pets.Sort(StringComparer.OrdinalIgnoreCase);

            if (pets.Count == 0)
            {
                Console.Error.WriteLine("Found no <dir>\animations.xml under " + petsDirectory);
                return 2;
            }

            Console.WriteLine(
                "pet".PadRight(20) + "KiB".PadLeft(7) + "anim".PadLeft(6) + "edge".PadLeft(6) +
                "term".PadLeft(6) + "unreach".PadLeft(9) + "  round-trip  valid");
            Console.WriteLine(new string('-', 78));

            int invalid = 0;
            int roundTripFailures = 0;
            int disconnected = 0;
            // Converted pets must be FULLY connected; hand-authored ones are reported but not failed. The
            // difference is authorship: a converter that strands an animation is a bug in the emitter or a
            // migration, and today the true value across all 31 converted pets is zero. An artist's own graph
            // is theirs. Provenance-unknown pets are counted separately and SAID, never silently exempted.
            var convertedStranded = new List<string>();
            int handAuthoredStranded = 0;
            int unknownProvenanceStranded = 0;

            foreach (string petDirectory in pets)
            {
                string name = Path.GetFileName(petDirectory);
                string path = Path.Combine(petDirectory, "animations.xml");
                string xml = File.ReadAllText(path, Encoding.UTF8);
                double kib = new FileInfo(path).Length / 1024.0;

                XmlData.RootNode root;
                string error;
                bool valid = ShimejiEngine.TryValidate(xml, out root, out error);

                string graphCells = "".PadLeft(6) + "".PadLeft(6) + "".PadLeft(6) + "".PadLeft(9);
                string roundTrip = "-";

                if (valid)
                {
                    GraphReport report = ShimejiEngine.Analyze(root);
                    if (!report.IsConnected)
                    {
                        disconnected++;
                        string author = root.Header != null ? (root.Header.Author ?? "") : null;
                        if (author == null || author.Length == 0)
                            unknownProvenanceStranded++;
                        else if (string.Equals(author, PetEmitter.ConvertedAuthor, StringComparison.Ordinal))
                            convertedStranded.Add(name + ": " + DescribeUnreachable(root, report.Unreachable));
                        else
                            handAuthoredStranded++;
                    }

                    graphCells =
                        report.AnimationCount.ToString().PadLeft(6) +
                        report.EdgeCount.ToString().PadLeft(6) +
                        report.Terminal.Count.ToString().PadLeft(6) +
                        report.Unreachable.Count.ToString().PadLeft(9);

                    string roundTripError;
                    roundTrip = ShimejiEngine.RoundTrips(root, out roundTripError) ? "ok" : "FAIL";
                    if (roundTrip == "FAIL")
                    {
                        roundTripFailures++;
                        Console.WriteLine(
                            name.PadRight(20) + kib.ToString("F0").PadLeft(7) + graphCells +
                            "  " + roundTrip.PadRight(11) + "ok");
                        Console.WriteLine("      round-trip: " + roundTripError);
                        continue;
                    }
                }
                else
                {
                    invalid++;
                }

                Console.WriteLine(
                    name.PadRight(20) + kib.ToString("F0").PadLeft(7) + graphCells +
                    "  " + roundTrip.PadRight(11) + (valid ? "ok" : "INVALID"));

                if (!valid) Console.WriteLine("      validator: " + error);
            }

            Console.WriteLine();
            Console.WriteLine("pets " + pets.Count +
                              "   invalid " + invalid +
                              "   round-trip failures " + roundTripFailures +
                              "   with unreachable animations " + disconnected);

            Console.WriteLine("   of those: converted " + convertedStranded.Count +
                              "   hand-authored " + handAuthoredStranded +
                              "   provenance unknown " + unknownProvenanceStranded);

            // A shipped pet failing the app's own validator, or failing to survive its own DTOs, means the
            // harness is wrong -- not the pet.
            //
            // Unreachable animations used to be reported and never failed, because whether hand-authored pets
            // are fully connected was an open question. It is answered: all 31 CONVERTED pets are fully
            // connected, and the seven hand-authored sheep each strand two. So a converted pet stranding an
            // animation is now a failure -- it means an emitter change or a migration quietly cut a pose off
            // from the graph, which is invisible in-app (the pose simply never plays) and is exactly what a
            // user cannot report except as "I have never seen this animation".
            //
            // Hand-authored pets stay reported-only: an artist's graph is theirs, and the standing rule here
            // is not to retime or rewire hand-authored content.
            if (convertedStranded.Count > 0)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("FAIL: " + convertedStranded.Count +
                    " converted pet(s) have animations nothing can reach:");
                foreach (string entry in convertedStranded) Console.Error.WriteLine("   " + entry);
                Console.Error.WriteLine("A converted pet's graph is generated, so an unreachable pose is an " +
                    "emitter or migration bug, not a choice.");
            }
            if (unknownProvenanceStranded > 0)
                Console.Error.WriteLine("NOTE: " + unknownProvenanceStranded +
                    " pet(s) with no author could not be classified, so they were not held to the converted rule.");

            return invalid == 0 && roundTripFailures == 0 && convertedStranded.Count == 0 ? 0 : 1;
        }

        /// <summary>Name the unreachable animations, not just their ids. "id 27" tells the reader nothing;
        /// "turn (id 27)" tells them which pose stopped playing, which is the whole point of the message.</summary>
        private static string DescribeUnreachable(XmlData.RootNode root, List<int> ids)
        {
            var names = new Dictionary<int, string>();
            if (root.Animations != null && root.Animations.Animation != null)
                foreach (XmlData.AnimationNode a in root.Animations.Animation)
                    if (a != null) names[a.Id] = a.Name ?? "";
            var parts = new List<string>();
            foreach (int id in ids)
            {
                string n;
                parts.Add(names.TryGetValue(id, out n) && n.Length > 0
                    ? n + " (id " + id + ")"
                    : "id " + id);
            }
            return string.Join(", ", parts);
        }

        private static int Classify(string confDirectory)
        {
            if (!Directory.Exists(confDirectory))
            {
                Console.Error.WriteLine("No such directory: " + confDirectory);
                return 2;
            }

            ShimejiConfig config;
            try
            {
                config = ShimejiParser.ParseConfDirectory(confDirectory);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Parse failed: " + ex.Message);
                return 2;
            }

            foreach (FidelityGroup g in new[] { FidelityGroup.Group1, FidelityGroup.Group2, FidelityGroup.Group3 })
            {
                var inGroup = config.Actions.Where(a => a.Group == g).ToList();
                Console.WriteLine();
                Console.WriteLine(string.Format("----- {0} ({1} actions) -----", g, inGroup.Count));
                foreach (ShimejiAction a in inGroup)
                {
                    string cls = a.Class != null ? " Class=" + a.Class : "";
                    string border = a.BorderType != null ? " Border=" + a.BorderType : "";
                    Console.WriteLine(string.Format("  {0,-30} Type={1}{2}{3}", a.Name, a.Type, cls, border));
                    Console.WriteLine("       -> " + a.Reason);
                }
            }

            int g1 = config.Actions.Count(a => a.Group == FidelityGroup.Group1);
            int g2 = config.Actions.Count(a => a.Group == FidelityGroup.Group2);
            int g3 = config.Actions.Count(a => a.Group == FidelityGroup.Group3);
            int cg1 = config.BehaviorConditions.Count(c => c.Group == FidelityGroup.Group1);
            int cg2 = config.BehaviorConditions.Count(c => c.Group == FidelityGroup.Group2);

            Console.WriteLine();
            Console.WriteLine(string.Format("actions {0}   Group1 {1}   Group2 {2}   Group3 {3}",
                config.Actions.Count, g1, g2, g3));
            Console.WriteLine(string.Format("behavior conditions {0}   maps-cleanly(G1) {1}   needs-state(G2) {2}",
                config.BehaviorConditions.Count, cg1, cg2));
            return 0;
        }

        private static int SelfTest()
        {
            string detail;
            bool ok = EngineSelfTest.RunAll(out detail);
            Console.WriteLine(detail);
            Console.WriteLine(ok ? "SELFTEST PASS" : "SELFTEST FAIL");
            return ok ? 0 : 1;
        }

        private static int Composite(string confDirectory, string imgDirectory, string outPng)
        {
            if (!Directory.Exists(confDirectory)) { Console.Error.WriteLine("No such conf directory: " + confDirectory); return 2; }
            if (!Directory.Exists(imgDirectory)) { Console.Error.WriteLine("No such img directory: " + imgDirectory); return 2; }

            ShimejiConfig config;
            try { config = ShimejiParser.ParseConfDirectory(confDirectory); }
            catch (Exception ex) { Console.Error.WriteLine("Parse failed: " + ex.Message); return 2; }

            SpriteSheet sheet;
            string error;
            if (!SpriteSheetBuilder.Build(PetEmitter.PosesToComposite(config), SpriteSheetBuilder.FileLoader(imgDirectory), false, out sheet, out error))
            {
                Console.Error.WriteLine("Composite failed: " + error);
                return 1;
            }

            File.WriteAllBytes(outPng, sheet.PngBytes);
            Console.WriteLine(string.Format(
                "sheet: {0}x{1} tiles, cell {2}x{3}, scale {4:0.###}, frames {5}, png {6:N0} bytes, projected XML {7:N0} bytes",
                sheet.TilesX, sheet.TilesY, sheet.CellWidth, sheet.CellHeight, sheet.Scale,
                sheet.FrameIndexByKey.Count, sheet.PngBytes.Length, sheet.ProjectedXmlBytes));
            Console.WriteLine("wrote " + outPng);
            return 0;
        }

        // DEV/batch: detect the skin(s) under a root dir (as Pet Studio's import does) and convert the first,
        // reporting a one-line tab-separated verdict. For measuring convert yield over a harvested collection;
        // no output file is written. Exit 0 = accepted.
        private static int ConvertRoot(string root, string outXml)
        {
            if (!Directory.Exists(root)) { Console.WriteLine("NO-DIR\t" + root); return 2; }
            string note;
            System.Collections.Generic.List<DetectedSkin> skins = SkinLayout.Detect(root, out note);
            if (skins == null || skins.Count == 0) { Console.WriteLine("DETECT-FAIL\t" + (note ?? "")); return 3; }
            DetectedSkin skin = skins[0];
            string error;
            ConversionResult r = ShimejiEngine.ConvertSkin(skin.ConfDir, skin.ImgDir, skin.Name, out error);
            if (r == null) { Console.WriteLine("CONVERT-FAIL\t" + error); return 1; }
            if (outXml != null && r.Accepted)
                File.WriteAllText(outXml, r.EmittedXml, new UTF8Encoding(false));
            int anims = r.Root != null && r.Root.Animations != null && r.Root.Animations.Animation != null
                ? r.Root.Animations.Animation.Length : 0;
            Console.WriteLine((r.Accepted ? "ACCEPTED" : "NOT-ACCEPTED") +
                "\tskins=" + skins.Count + "\tanims=" + anims + "\tvalid=" + r.Valid + "\tbundled=" + skin.UsesBundledConf);
            return r.Accepted ? 0 : 1;
        }

        private static int ConvertBundleVerb(string bundleDir, string outXml)
        {
            if (!Directory.Exists(bundleDir)) { Console.Error.WriteLine("No such bundle directory: " + bundleDir); return 2; }
            if (!BundleConverter.IsBundle(bundleDir))
            {
                Console.Error.WriteLine("Not an Android Shimeji bundle (need manifest.json + animation.json): " + bundleDir);
                return 2;
            }

            // Take the display name from the manifest, so the header and residue label read nicely.
            BundleInfo info;
            try { BundleParser.Parse(bundleDir, out info); }
            catch (Exception ex) { Console.Error.WriteLine("Parse failed: " + ex.Message); return 1; }
            string skinName = !string.IsNullOrWhiteSpace(info.Name) ? info.Name.Trim() : Path.GetFileName(bundleDir.TrimEnd(Path.DirectorySeparatorChar));

            string error;
            ConversionResult r = BundleConverter.ConvertBundle(bundleDir, skinName, out error);
            if (r == null) { Console.Error.WriteLine("Convert failed: " + error); return 1; }

            File.WriteAllText(outXml, r.EmittedXml, new UTF8Encoding(false));
            string residuePath = outXml + ".residue.txt";
            File.WriteAllText(residuePath, r.Residue.ToText(skinName), new UTF8Encoding(false));

            int animCount = r.Root != null && r.Root.Animations != null && r.Root.Animations.Animation != null
                ? r.Root.Animations.Animation.Length : 0;
            int unreachable = r.Graph != null ? r.Graph.Unreachable.Count : -1;
            Console.WriteLine(string.Format(
                "pet: {0} animations, valid={1}, roundtrip={2}, unreachable={3}, accepted={4}",
                animCount, r.Valid, r.RoundTrips, unreachable, r.Accepted));
            Console.WriteLine(string.Format("residue: {0} dropped, {1} degraded", r.Residue.Dropped.Count, r.Residue.Degraded.Count));
            Console.WriteLine("wrote " + outXml);
            Console.WriteLine("wrote " + residuePath);
            Console.WriteLine(r.Accepted ? "ACCEPTED" : "NOT-ACCEPTED");
            if (!r.Valid) Console.Error.WriteLine("validator: " + r.Error);
            return r.Accepted ? 0 : 1;
        }

        private static int ConvertVerb(string confDirectory, string imgDirectory, string skinName, string outXml)
        {
            bool bundled = confDirectory == "-";
            if (!bundled && !Directory.Exists(confDirectory)) { Console.Error.WriteLine("No such conf directory: " + confDirectory + " (pass - to use the bundled base conf)"); return 2; }
            if (!Directory.Exists(imgDirectory)) { Console.Error.WriteLine("No such img directory: " + imgDirectory); return 2; }

            string error;
            ConversionResult r = ShimejiEngine.ConvertSkin(bundled ? "" : confDirectory, imgDirectory, skinName, out error);
            if (r == null) { Console.Error.WriteLine("Convert failed: " + error); return 1; }

            File.WriteAllText(outXml, r.EmittedXml, new UTF8Encoding(false));
            string residuePath = outXml + ".residue.txt";
            File.WriteAllText(residuePath, r.Residue.ToText(skinName), new UTF8Encoding(false));

            int animCount = r.Root != null && r.Root.Animations != null && r.Root.Animations.Animation != null
                ? r.Root.Animations.Animation.Length : 0;
            int unreachable = r.Graph != null ? r.Graph.Unreachable.Count : -1;
            Console.WriteLine(string.Format(
                "pet: {0} animations, valid={1}, roundtrip={2}, unreachable={3}, accepted={4}",
                animCount, r.Valid, r.RoundTrips, unreachable, r.Accepted));
            Console.WriteLine(string.Format("residue: {0} dropped, {1} degraded", r.Residue.Dropped.Count, r.Residue.Degraded.Count));
            Console.WriteLine("wrote " + outXml);
            Console.WriteLine("wrote " + residuePath);
            if (!r.Valid) Console.Error.WriteLine("validator: " + r.Error);
            return r.Accepted ? 0 : 1;
        }

        // Migration: re-time already-emitted locomotion animations to the current walk-time budget without
        // re-converting from source. The emitter marks a locomotion spoke uniquely as repeat="6" with a
        // border that turns; recompute its repeat from the shipped interval x frame count via the SAME policy
        // the emitter now uses (PetEmitter.LocoRepeatCount), and rewrite only the pets that change -- through
        // the engine's own parser + serializer, so a file's encoding/shape is untouched apart from the repeat
        // attribute. Pets that aren't converter output (no matching loco spoke) are left exactly as they are.
        /// <summary>
        /// Re-weight the hub transitions of already-converted pets through the current curve.
        ///
        /// Why a migration rather than a re-conversion: the source Shimeji skins are deliberately NOT in this
        /// repo (IP), so the shipped animations.xml is the only artefact available. That turns out to be
        /// enough, because the old emitter wrote weight = HubBaseWeight + frequency, so the source frequency
        /// is exactly (probability - HubBaseWeight) and can be pushed back through the new curve.
        ///
        /// That recovery is ALSO why this must not run twice on the same pet: a second pass would treat an
        /// already-damped weight as a raw frequency. Hence the format-version gate rather than a "looks
        /// about right" heuristic.
        /// </summary>
        private static int Reweight(string petsDirectory)
        {
            if (!Directory.Exists(petsDirectory)) { Console.Error.WriteLine("No such directory: " + petsDirectory); return 2; }

            var pets = new List<string>();
            foreach (string candidate in Directory.GetDirectories(petsDirectory))
                if (File.Exists(Path.Combine(candidate, "animations.xml"))) pets.Add(candidate);
            pets.Sort(StringComparer.OrdinalIgnoreCase);
            if (pets.Count == 0) { Console.Error.WriteLine("Found no <dir>\\animations.xml under " + petsDirectory); return 2; }

            int petsChanged = 0, skipped = 0, failures = 0;
            foreach (string petDir in pets)
            {
                string name = Path.GetFileName(petDir);
                string path = Path.Combine(petDir, "animations.xml");
                string xml = File.ReadAllText(path, Encoding.UTF8);

                XmlData.RootNode root;
                string error;
                if (!ShimejiEngine.TryValidate(xml, out root, out error))
                {
                    Console.WriteLine(name.PadRight(36) + " SKIP (invalid: " + error + ")");
                    skipped++;
                    continue;
                }

                // Two gates, both required. The author gate keeps this off hand-authored pets entirely -- the
                // shipped sheep have real author names and must never be re-weighted. The version gate makes
                // the run idempotent.
                if (root.Header == null ||
                    !string.Equals(root.Header.Author, PetEmitter.ConvertedAuthor, StringComparison.Ordinal))
                {
                    Console.WriteLine(name.PadRight(36) + " skip (not converter output)");
                    skipped++;
                    continue;
                }
                if (!string.Equals(root.Header.Version, PetEmitter.ConvertedFormatVersionFlatWeights, StringComparison.Ordinal))
                {
                    Console.WriteLine(name.PadRight(36) + " skip (already at format " + (root.Header.Version ?? "?") + ")");
                    skipped++;
                    continue;
                }
                if (root.Animations == null || root.Animations.Animation == null)
                {
                    Console.WriteLine(name.PadRight(36) + " skip (no animations)");
                    skipped++;
                    continue;
                }

                // The hub is the animation fanning out to the most others; in every converted skin that is the
                // idle state the behaviour tree returns to.
                XmlData.AnimationNode hub = null;
                foreach (XmlData.AnimationNode a in root.Animations.Animation)
                {
                    if (a == null || a.Sequence == null || a.Sequence.Next == null) continue;
                    if (hub == null || a.Sequence.Next.Length > hub.Sequence.Next.Length) hub = a;
                }
                if (hub == null || hub.Sequence.Next.Length < 2)
                {
                    Console.WriteLine(name.PadRight(36) + " skip (no hub to re-weight)");
                    skipped++;
                    continue;
                }

                XmlData.NextNode[] edges = hub.Sequence.Next;
                var weights = new List<int>(edges.Length);
                int hubSelfIndex = -1;
                for (int i = 0; i < edges.Length; i++)
                {
                    int frequency = Math.Max(0, edges[i].Probability - PetEmitter.HubBaseWeight);
                    weights.Add(PetEmitter.HubWeightFromFrequency(frequency));
                    // The hub's own re-selection edge keeps the bare baseline; see ApplyMinimumShare.
                    if (edges[i].Value == hub.Id) { hubSelfIndex = i; weights[i] = PetEmitter.HubBaseWeight; }
                }
                PetEmitter.ApplyMinimumShare(weights, hubSelfIndex, PetEmitter.HubMinimumSharePercent);

                int changedEdges = 0;
                for (int i = 0; i < edges.Length; i++)
                    if (edges[i].Probability != weights[i]) { edges[i].Probability = weights[i]; changedEdges++; }

                root.Header.Version = PetEmitter.ConvertedFormatVersionDampedWeights;

                string outXml = ShimejiEngine.Serialize(root);
                XmlData.RootNode reparsed;
                string reError;
                if (!ShimejiEngine.TryValidate(outXml, out reparsed, out reError))
                {
                    Console.Error.WriteLine(name.PadRight(36) + " FAIL (re-validate: " + reError + ")");
                    failures++;
                    continue;
                }
                File.WriteAllText(path, outXml, new UTF8Encoding(false));
                petsChanged++;

                // Report the rarest REAL animation, excluding the hub's own re-selection edge. That edge is
                // deliberately pinned to the baseline, so including it would report ~0.6% on a pet whose
                // animations are all comfortably above the floor -- a misleading number that looks like a bug.
                int total = weights.Sum();
                int rarest = int.MaxValue;
                for (int i = 0; i < weights.Count; i++)
                    if (i != hubSelfIndex && weights[i] < rarest) rarest = weights[i];
                double worst = (total > 0 && rarest != int.MaxValue) ? rarest * 100.0 / total : 0;
                Console.WriteLine(name.PadRight(36) + " reweighted " + changedEdges + "/" + edges.Length +
                    " edges, rarest animation now " +
                    worst.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "%");
            }

            Console.WriteLine();
            Console.WriteLine("pets " + pets.Count + "   reweighted " + petsChanged +
                "   skipped " + skipped + "   failures " + failures);
            return failures == 0 ? 0 : 1;
        }

        private static int Rebalance(string petsDirectory)
        {
            if (!Directory.Exists(petsDirectory)) { Console.Error.WriteLine("No such directory: " + petsDirectory); return 2; }

            var pets = new List<string>();
            foreach (string candidate in Directory.GetDirectories(petsDirectory))
                if (File.Exists(Path.Combine(candidate, "animations.xml"))) pets.Add(candidate);
            pets.Sort(StringComparer.OrdinalIgnoreCase);
            if (pets.Count == 0) { Console.Error.WriteLine("Found no <dir>\\animations.xml under " + petsDirectory); return 2; }

            int petsChanged = 0, animsChanged = 0, failures = 0;
            foreach (string petDir in pets)
            {
                string name = Path.GetFileName(petDir);
                string path = Path.Combine(petDir, "animations.xml");
                string xml = File.ReadAllText(path, Encoding.UTF8);

                XmlData.RootNode root;
                string error;
                if (!ShimejiEngine.TryValidate(xml, out root, out error))
                {
                    Console.WriteLine(name.PadRight(28) + " SKIP (invalid: " + error + ")");
                    continue;
                }
                if (root.Animations == null || root.Animations.Animation == null) { Console.WriteLine(name.PadRight(28) + " unchanged"); continue; }

                // id -> name, so a border's target can be recognised by name ("turn" marks a loco spoke).
                var nameById = new Dictionary<int, string>();
                foreach (XmlData.AnimationNode a in root.Animations.Animation)
                    if (a != null) nameById[a.Id] = a.Name;

                int changedHere = 0;
                foreach (XmlData.AnimationNode a in root.Animations.Animation)
                {
                    if (a == null || a.Sequence == null) continue;
                    if (!string.Equals(a.Sequence.RepeatCount, "6", StringComparison.Ordinal)) continue;   // only the emitter's loco spokes
                    if (!TurnsAtBorder(a, nameById)) continue;                                              // and only a walk-and-turn

                    int frames = a.Sequence.Frame != null ? a.Sequence.Frame.Length : 0;
                    int passMs = frames * IntervalMs(a);
                    string updated = PetEmitter.LocoRepeatCount(passMs)
                        .ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (!string.Equals(updated, a.Sequence.RepeatCount, StringComparison.Ordinal))
                    {
                        a.Sequence.RepeatCount = updated;
                        changedHere++;
                    }
                }

                if (changedHere == 0) { Console.WriteLine(name.PadRight(28) + " unchanged"); continue; }

                string outXml = ShimejiEngine.Serialize(root);
                XmlData.RootNode reparsed;
                string reError;
                if (!ShimejiEngine.TryValidate(outXml, out reparsed, out reError))
                {
                    Console.Error.WriteLine(name.PadRight(28) + " FAIL (re-validate: " + reError + ")");
                    failures++;
                    continue;
                }
                File.WriteAllText(path, outXml, new UTF8Encoding(false));
                petsChanged++; animsChanged += changedHere;
                Console.WriteLine(name.PadRight(28) + " rebalanced " + changedHere + " loco animation(s)");
            }

            Console.WriteLine();
            Console.WriteLine("pets " + pets.Count + "   changed " + petsChanged +
                "   loco animations retimed " + animsChanged + "   failures " + failures);
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// Migration: give every already-emitted jump the three-phase shape (solved arc -&gt; descent -&gt;
        /// landing).
        ///
        /// A migration rather than a re-conversion, for the same reason `reweight` was one: nothing here needs
        /// a source skin, because no new sprite FRAME is involved. The jump uses the tiles it already had; what
        /// changes is the arc's numbers, the sequence's exit and the border's landing set. Re-converting the 25
        /// affected pets would regenerate 25 sprite sheets to produce identical pixels, and would silently wipe
        /// Hornet's hand-edited fall/Grapple3 frame swap.
        ///
        /// Every policy value comes from PetEmitter, so this and a fresh conversion cannot disagree.
        /// </summary>
        private static int Rejump(string petsDirectory)
        {
            if (!Directory.Exists(petsDirectory)) { Console.Error.WriteLine("No such directory: " + petsDirectory); return 2; }

            var pets = new List<string>();
            foreach (string candidate in Directory.GetDirectories(petsDirectory))
                if (File.Exists(Path.Combine(candidate, "animations.xml"))) pets.Add(candidate);
            pets.Sort(StringComparer.OrdinalIgnoreCase);
            if (pets.Count == 0) { Console.Error.WriteLine("Found no <dir>\\animations.xml under " + petsDirectory); return 2; }

            int petsChanged = 0, skipped = 0, failures = 0, arced = 0, flattened = 0;
            foreach (string petDir in pets)
            {
                string name = Path.GetFileName(petDir);
                string path = Path.Combine(petDir, "animations.xml");

                XmlData.RootNode root;
                string error;
                if (!ShimejiEngine.TryValidate(File.ReadAllText(path, Encoding.UTF8), out root, out error))
                {
                    Console.WriteLine(name.PadRight(36) + " SKIP (invalid: " + error + ")");
                    skipped++;
                    continue;
                }

                // The same two gates reweight uses. The author gate keeps this off the hand-authored sheep
                // absolutely -- they have real jumps, authored frame by frame, and re-arcing one would replace
                // an artist's 14-frame launch with a converter's guess. The version gate makes a run idempotent.
                if (root.Header == null ||
                    !string.Equals(root.Header.Author, PetEmitter.ConvertedAuthor, StringComparison.Ordinal))
                {
                    Console.WriteLine(name.PadRight(36) + " skip (not converter output)");
                    skipped++;
                    continue;
                }
                if (!string.Equals(root.Header.Version, PetEmitter.ConvertedFormatVersionLooseJumps, StringComparison.Ordinal))
                {
                    Console.WriteLine(name.PadRight(36) + " skip (already at format " + (root.Header.Version ?? "?") + ")");
                    skipped++;
                    continue;
                }
                if (root.Animations == null || root.Animations.Animation == null)
                {
                    Console.WriteLine(name.PadRight(36) + " skip (no animations)");
                    skipped++;
                    continue;
                }

                XmlData.AnimationNode[] all = root.Animations.Animation;
                XmlData.AnimationNode hub = null, fall = null;
                foreach (XmlData.AnimationNode a in all)
                {
                    if (a == null) continue;
                    if (string.Equals(a.Name, "fall", StringComparison.OrdinalIgnoreCase)) fall = a;
                    if (a.Sequence == null || a.Sequence.Next == null) continue;
                    if (hub == null || a.Sequence.Next.Length > hub.Sequence.Next.Length) hub = a;
                }
                if (hub == null || fall == null)
                {
                    Console.WriteLine(name.PadRight(36) + " skip (no hub or no fall to descend into)");
                    skipped++;
                    continue;
                }

                // Hub-selectable is what makes an animation a FLOOR animation here: the wall and ceiling
                // regions are deliberately unreachable from the hub, and they are full of upward velocities
                // that must not be touched. Weights come off the hub's own edges, which is how the landing
                // target is chosen the same way the emitter chooses it (the pet's most-used locomotion).
                var hubWeight = new Dictionary<int, int>();
                foreach (XmlData.NextNode n in hub.Sequence.Next)
                    if (n != null && !hubWeight.ContainsKey(n.Value)) hubWeight[n.Value] = n.Probability;

                XmlData.AnimationNode landRun = null;
                foreach (XmlData.AnimationNode a in all)
                {
                    if (a == null || a == hub || !hubWeight.ContainsKey(a.Id)) continue;
                    if (StartY(a) < 0 || StartX(a) == 0) continue;      // not a launcher, and it travels
                    if (landRun == null || hubWeight[a.Id] > hubWeight[landRun.Id]) landRun = a;
                }

                int arcedHere = 0, flattenedHere = 0;
                foreach (XmlData.AnimationNode a in all)
                {
                    if (a == null || !hubWeight.ContainsKey(a.Id) || StartY(a) >= 0) continue;
                    // A jump is the one floor animation emitted without a gravity node; anything else that
                    // rises and has one was never given the jump treatment in the first place.
                    if (a.Gravity != null) continue;

                    if (StartY(a) > PetEmitter.JumpMinLaunchY)
                    {
                        // Too weak to have been a jump. Flatten it: the rise goes, the sprites and the
                        // horizontal motion stay, gravity comes back, and the window underside (which only a
                        // jump can reach) goes with it.
                        SetXy(a.Start, StartX(a), 0);
                        SetXy(a.End, EndX(a), 0);
                        a.Gravity = new XmlData.HitNode
                        {
                            Next = new[] { new XmlData.NextNode { Value = fall.Id, Probability = 100, OnlyFlag = "none" } },
                        };
                        a.Border = new XmlData.HitNode { Next = WithoutOnly(a.Border, "window-bottom") };
                        flattenedHere++;
                        continue;
                    }

                    // Phase 1: re-arc. The repeat is chosen first, because the launch velocity has to be
                    // solved for the step count the sequence will actually declare.
                    int frames = a.Sequence != null && a.Sequence.Frame != null ? a.Sequence.Frame.Length : 0;
                    if (frames == 0) continue;
                    a.Sequence.RepeatFromFrame = 0;
                    int repeat = PetEmitter.JumpRepeatCount(frames, 0);
                    a.Sequence.RepeatCount = repeat.ToString(CultureInfo.InvariantCulture);
                    int steps = PetEmitter.JumpStepCount(frames);
                    int interval = PetEmitter.JumpInterval(steps);
                    SetXy(a.Start, PetEmitter.ClampJumpVelX(StartX(a), steps), PetEmitter.SolveJumpLaunchY(steps));
                    SetXy(a.End, PetEmitter.ClampJumpVelX(EndX(a), steps), PetEmitter.JumpDescentY);
                    a.Start.Interval = interval.ToString(CultureInfo.InvariantCulture);
                    a.End.Interval = interval.ToString(CultureInfo.InvariantCulture);

                    // Phase 2: the descent.
                    a.Sequence.Next = new[] { new XmlData.NextNode { Value = fall.Id, Probability = 100, OnlyFlag = "none" } };

                    // Phase 3: the landing. Added to whatever border edges the pet already had, so the wall
                    // entry and the window edges keep working exactly as they did.
                    var border = new List<XmlData.NextNode>(WithoutOnly(a.Border, "taskbar"));
                    border.Add(new XmlData.NextNode { Value = a.Id, Probability = PetEmitter.LandRejumpWeight, OnlyFlag = "taskbar" });
                    if (landRun != null)
                        border.Add(new XmlData.NextNode { Value = landRun.Id, Probability = PetEmitter.LandRunWeight, OnlyFlag = "taskbar" });
                    a.Border = new XmlData.HitNode { Next = border.ToArray() };
                    arcedHere++;
                }

                // Stamped even when nothing changed: a pet with no upward animation already behaves the way
                // 1.3 says, and leaving it at 1.2 would make every future run re-examine it.
                root.Header.Version = PetEmitter.ConvertedFormatVersion;

                string outXml = ShimejiEngine.Serialize(root);
                XmlData.RootNode reparsed;
                string reError;
                if (!ShimejiEngine.TryValidate(outXml, out reparsed, out reError))
                {
                    Console.Error.WriteLine(name.PadRight(36) + " FAIL (re-validate: " + reError + ")");
                    failures++;
                    continue;
                }
                // A migration must not orphan an animation: the acceptance bar for a fresh conversion is
                // reachability, and rewriting a jump's only sequence exit is exactly the kind of edit that
                // could strand something.
                GraphReport graph = ShimejiEngine.Analyze(reparsed);
                if (graph != null && graph.Unreachable.Count > 0)
                {
                    Console.Error.WriteLine(name.PadRight(36) + " FAIL (unreachable after migration: " +
                        string.Join(",", graph.Unreachable) + ")");
                    failures++;
                    continue;
                }

                File.WriteAllText(path, outXml, new UTF8Encoding(false));
                petsChanged++; arced += arcedHere; flattened += flattenedHere;
                Console.WriteLine(name.PadRight(36) + " " + arcedHere + " jump(s) re-arced, " +
                    flattenedHere + " weak rise(s) flattened" +
                    (landRun != null && arcedHere > 0 ? "   lands into " + landRun.Name : ""));
            }

            Console.WriteLine();
            Console.WriteLine("pets " + pets.Count + "   changed " + petsChanged + "   jumps re-arced " + arced +
                "   weak rises flattened " + flattened + "   skipped " + skipped + "   failures " + failures);
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// Migration: let a wall climb and a ceiling walk CROSS the surface in one sequence.
        ///
        /// Numbers only, so no source skin is needed. A surface pose is identified the way the app identifies
        /// one: it has no &lt;gravity&gt; (that absence IS the cling) and it is NOT hub-selectable, which is the
        /// mechanism that stops a cling playing mid-screen.
        /// </summary>
        private static int Reclimb(string petsDirectory)
        {
            if (!Directory.Exists(petsDirectory)) { Console.Error.WriteLine("No such directory: " + petsDirectory); return 2; }

            var pets = new List<string>();
            foreach (string candidate in Directory.GetDirectories(petsDirectory))
                if (File.Exists(Path.Combine(candidate, "animations.xml"))) pets.Add(candidate);
            pets.Sort(StringComparer.OrdinalIgnoreCase);
            if (pets.Count == 0) { Console.Error.WriteLine("Found no <dir>\\animations.xml under " + petsDirectory); return 2; }

            int petsChanged = 0, skipped = 0, failures = 0, retimed = 0, held = 0;
            foreach (string petDir in pets)
            {
                string name = Path.GetFileName(petDir);
                string path = Path.Combine(petDir, "animations.xml");

                XmlData.RootNode root;
                string error;
                if (!ShimejiEngine.TryValidate(File.ReadAllText(path, Encoding.UTF8), out root, out error))
                {
                    Console.WriteLine(name.PadRight(36) + " SKIP (invalid: " + error + ")");
                    skipped++;
                    continue;
                }
                // The hand-authored pets already do this properly -- yellow_sheep's walk_up repeats ~20000
                // times -- and re-timing one would replace an artist's pacing with a converter's.
                if (root.Header == null ||
                    !string.Equals(root.Header.Author, PetEmitter.ConvertedAuthor, StringComparison.Ordinal))
                {
                    Console.WriteLine(name.PadRight(36) + " skip (not converter output)");
                    skipped++;
                    continue;
                }
                if (!string.Equals(root.Header.Version, PetEmitter.ConvertedFormatVersionShortClimbs, StringComparison.Ordinal))
                {
                    Console.WriteLine(name.PadRight(36) + " skip (already at format " + (root.Header.Version ?? "?") + ")");
                    skipped++;
                    continue;
                }
                if (root.Animations == null || root.Animations.Animation == null)
                {
                    Console.WriteLine(name.PadRight(36) + " skip (no animations)");
                    skipped++;
                    continue;
                }

                XmlData.AnimationNode[] all = root.Animations.Animation;
                XmlData.AnimationNode hub = null;
                foreach (XmlData.AnimationNode a in all)
                {
                    if (a == null || a.Sequence == null || a.Sequence.Next == null) continue;
                    if (hub == null || a.Sequence.Next.Length > hub.Sequence.Next.Length) hub = a;
                }
                var hubSelectable = new HashSet<int>();
                if (hub != null)
                    foreach (XmlData.NextNode n in hub.Sequence.Next)
                        if (n != null) hubSelectable.Add(n.Value);

                int retimedHere = 0, heldHere = 0;
                foreach (XmlData.AnimationNode a in all)
                {
                    // No gravity AND not hub-selectable: a wall or ceiling pose. The magic names are
                    // hub-reachable or gravity-bearing in every emitted pet, so they fall out on their own,
                    // but `fall` is neither -- exclude it by name rather than by luck.
                    if (a == null || a.Gravity != null || hubSelectable.Contains(a.Id)) continue;
                    if (IsMagicName(a.Name)) continue;
                    if (a.Sequence == null || a.Sequence.Frame == null || a.Sequence.Frame.Length == 0) continue;

                    int sy = StartY(a), ey = EndY(a), sx = StartX(a), ex = EndX(a);
                    // ANY vertical motion crosses, not just upward: a DESCENDING wall pose is how a pet climbs
                    // back down, and leaving it short means descending 56px and rolling the same let-go dice.
                    bool vertical = sy != 0 || ey != 0;
                    bool horizontal = !vertical && (sx != 0 || ex != 0);
                    if (!vertical && !horizontal) { heldHere++; continue; }   // a static hold keeps its budget

                    int frames = a.Sequence.Frame.Length;
                    a.Sequence.RepeatFromFrame = 0;
                    a.Sequence.RepeatCount = PetEmitter.SurfaceRepeatForReach(frames)
                        .ToString(CultureInfo.InvariantCulture);
                    int step = PetEmitter.SurfaceStepPx;
                    if (vertical)
                    {
                        // Direction preserved, or every descent becomes a climb.
                        int direction = (sy != 0 ? sy : ey) < 0 ? -1 : 1;
                        SetXy(a.Start, 0, direction * step);
                        SetXy(a.End, 0, direction * step);
                    }
                    else
                    {
                        int direction = (sx != 0 ? sx : ex) < 0 ? -1 : 1;
                        SetXy(a.Start, direction * step, 0);
                        SetXy(a.End, direction * step, 0);
                    }
                    a.Start.Interval = PetEmitter.SurfaceStepIntervalMs.ToString(CultureInfo.InvariantCulture);
                    a.End.Interval = PetEmitter.SurfaceStepIntervalMs.ToString(CultureInfo.InvariantCulture);
                    retimedHere++;
                }

                root.Header.Version = PetEmitter.ConvertedFormatVersion;

                string outXml = ShimejiEngine.Serialize(root);
                XmlData.RootNode reparsed;
                string reError;
                if (!ShimejiEngine.TryValidate(outXml, out reparsed, out reError))
                {
                    Console.Error.WriteLine(name.PadRight(36) + " FAIL (re-validate: " + reError + ")");
                    failures++;
                    continue;
                }
                GraphReport graph = ShimejiEngine.Analyze(reparsed);
                if (graph != null && graph.Unreachable.Count > 0)
                {
                    Console.Error.WriteLine(name.PadRight(36) + " FAIL (unreachable after migration: " +
                        string.Join(",", graph.Unreachable) + ")");
                    failures++;
                    continue;
                }

                File.WriteAllText(path, outXml, new UTF8Encoding(false));
                petsChanged++; retimed += retimedHere; held += heldHere;
                Console.WriteLine(name.PadRight(36) + " " + retimedHere + " crossing pose(s) retimed, " +
                    heldHere + " hold(s) left alone");
            }

            Console.WriteLine();
            Console.WriteLine("pets " + pets.Count + "   changed " + petsChanged + "   crossing poses retimed " +
                retimed + "   holds left alone " + held + "   skipped " + skipped + "   failures " + failures);
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// Migration: shorten an over-long REST to the hand-authored reference dwell.
        ///
        /// A rest was held ~9s (single-frame poses 10s), so converted pets stood idle 79% of the time; the
        /// sheep holds each rest ~0.7s. This retimes idle floor poses to the emitter's current rest dwell.
        ///
        /// The one honest limitation, stated because the emitted form cannot resolve it: the source's
        /// Stay/Animate flag is gone, so a rare idle one-shot PERFORMANCE (an eat, a vanish) whose current
        /// hold is over the ceiling is shortened too. That is acceptable here -- such poses are rare and
        /// low-weight, and a multi-second idle hold is the very sluggishness this fixes -- but it is the reason
        /// a from-source re-conversion would be strictly cleaner if the corpus ever grows performances that
        /// matter. Only IDLE poses (zero velocity) are touched, so a moving performance (a trip) is safe.
        /// </summary>
        private static int RestDwell(string petsDirectory)
        {
            if (!Directory.Exists(petsDirectory)) { Console.Error.WriteLine("No such directory: " + petsDirectory); return 2; }
            var pets = new List<string>();
            foreach (string candidate in Directory.GetDirectories(petsDirectory))
                if (File.Exists(Path.Combine(candidate, "animations.xml"))) pets.Add(candidate);
            pets.Sort(StringComparer.OrdinalIgnoreCase);
            if (pets.Count == 0) { Console.Error.WriteLine("Found no <dir>\\animations.xml under " + petsDirectory); return 2; }

            const int ceilingMs = 2600;   // matches the self-test: RestDwellMs + roundUp overshoot
            int petsChanged = 0, skipped = 0, failures = 0, retimed = 0;
            foreach (string petDir in pets)
            {
                string name = Path.GetFileName(petDir);
                string path = Path.Combine(petDir, "animations.xml");
                XmlData.RootNode root;
                string error;
                if (!ShimejiEngine.TryValidate(File.ReadAllText(path, Encoding.UTF8), out root, out error))
                {
                    Console.WriteLine(name.PadRight(36) + " SKIP (invalid: " + error + ")"); skipped++; continue;
                }
                if (root.Header == null ||
                    !string.Equals(root.Header.Author, PetEmitter.ConvertedAuthor, StringComparison.Ordinal))
                {
                    Console.WriteLine(name.PadRight(36) + " skip (not converter output)"); skipped++; continue;
                }
                if (!string.Equals(root.Header.Version, PetEmitter.ConvertedFormatVersionLongRests, StringComparison.Ordinal))
                {
                    Console.WriteLine(name.PadRight(36) + " skip (already at format " + (root.Header.Version ?? "?") + ")"); skipped++; continue;
                }
                if (root.Animations == null || root.Animations.Animation == null)
                {
                    Console.WriteLine(name.PadRight(36) + " skip (no animations)"); skipped++; continue;
                }

                int here = 0;
                foreach (XmlData.AnimationNode a in root.Animations.Animation)
                {
                    if (a == null || a.Gravity == null) continue;                 // wall/ceiling: not a floor rest
                    if (IsMagicName(a.Name)) continue;
                    if (StartX(a) != 0 || StartY(a) != 0 || EndX(a) != 0 || EndY(a) != 0) continue;   // moving: not a rest
                    if (a.Sequence == null || a.Sequence.Frame == null || a.Sequence.Frame.Length == 0) continue;
                    if (TotalDwellMs(a) <= ceilingMs) continue;                   // already short enough

                    int frames = a.Sequence.Frame.Length;
                    a.Sequence.RepeatFromFrame = 0;
                    if (frames == 1)
                    {
                        int interval, repeat;
                        PetEmitter.SingleFrameRestTiming(PetEmitter.RestDwellTargetMs, out interval, out repeat);
                        a.Start.Interval = interval.ToString(CultureInfo.InvariantCulture);
                        a.End.Interval = interval.ToString(CultureInfo.InvariantCulture);
                        a.Sequence.RepeatCount = repeat.ToString(CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        int i0 = Math.Min(ParseCoord(a.Start.Interval), PetEmitter.RestIntervalCapMs);
                        int iN = Math.Min(ParseCoord(a.End.Interval), PetEmitter.RestIntervalCapMs);
                        if (i0 < 1) i0 = PetEmitter.RestIntervalCapMs;
                        if (iN < 1) iN = i0;
                        a.Start.Interval = i0.ToString(CultureInfo.InvariantCulture);
                        a.End.Interval = iN.ToString(CultureInfo.InvariantCulture);
                        int passMs = frames * ((i0 + iN) / 2);
                        a.Sequence.RepeatCount = PetEmitter.RepeatCountForBudget(passMs, PetEmitter.RestDwellTargetMs, 30, true)
                            .ToString(CultureInfo.InvariantCulture);
                    }
                    here++;
                }

                root.Header.Version = PetEmitter.ConvertedFormatVersion;
                string outXml = ShimejiEngine.Serialize(root);
                XmlData.RootNode reparsed; string reError;
                if (!ShimejiEngine.TryValidate(outXml, out reparsed, out reError))
                {
                    Console.Error.WriteLine(name.PadRight(36) + " FAIL (re-validate: " + reError + ")"); failures++; continue;
                }
                GraphReport graph = ShimejiEngine.Analyze(reparsed);
                if (graph != null && graph.Unreachable.Count > 0)
                {
                    Console.Error.WriteLine(name.PadRight(36) + " FAIL (unreachable: " + string.Join(",", graph.Unreachable) + ")"); failures++; continue;
                }
                File.WriteAllText(path, outXml, new UTF8Encoding(false));
                petsChanged++; retimed += here;
                Console.WriteLine(name.PadRight(36) + " " + here + " rest(s) shortened");
            }
            Console.WriteLine();
            Console.WriteLine("pets " + pets.Count + "   changed " + petsChanged + "   rests shortened " + retimed +
                "   skipped " + skipped + "   failures " + failures);
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// Migration: split the rest dwell by role. The previous pass (`restdwell`) shortened EVERY rest to
        /// ~1.2s, which cut the performances the user wants to watch (Sprawl, dangle-legs, eat-berry). This
        /// keeps the HUB brief (the return-to pose, so the pet does not loiter) and lengthens every other idle
        /// to 9-12s.
        ///
        /// The hub is the animation the pet fans out from -- the most-connected node -- which is exactly how
        /// the emitter and every other migration here identify it.
        /// </summary>
        private static int RestSplit(string petsDirectory)
        {
            if (!Directory.Exists(petsDirectory)) { Console.Error.WriteLine("No such directory: " + petsDirectory); return 2; }
            var pets = new List<string>();
            foreach (string candidate in Directory.GetDirectories(petsDirectory))
                if (File.Exists(Path.Combine(candidate, "animations.xml"))) pets.Add(candidate);
            pets.Sort(StringComparer.OrdinalIgnoreCase);
            if (pets.Count == 0) { Console.Error.WriteLine("Found no <dir>\\animations.xml under " + petsDirectory); return 2; }

            int petsChanged = 0, skipped = 0, failures = 0, longer = 0;
            foreach (string petDir in pets)
            {
                string name = Path.GetFileName(petDir);
                string path = Path.Combine(petDir, "animations.xml");
                XmlData.RootNode root; string error;
                if (!ShimejiEngine.TryValidate(File.ReadAllText(path, Encoding.UTF8), out root, out error))
                { Console.WriteLine(name.PadRight(36) + " SKIP (invalid: " + error + ")"); skipped++; continue; }
                if (root.Header == null ||
                    !string.Equals(root.Header.Author, PetEmitter.ConvertedAuthor, StringComparison.Ordinal))
                { Console.WriteLine(name.PadRight(36) + " skip (not converter output)"); skipped++; continue; }
                if (!string.Equals(root.Header.Version, PetEmitter.ConvertedFormatVersionFlatRests, StringComparison.Ordinal))
                { Console.WriteLine(name.PadRight(36) + " skip (already at format " + (root.Header.Version ?? "?") + ")"); skipped++; continue; }
                if (root.Animations == null || root.Animations.Animation == null)
                { Console.WriteLine(name.PadRight(36) + " skip (no animations)"); skipped++; continue; }

                XmlData.AnimationNode hub = null;
                foreach (XmlData.AnimationNode a in root.Animations.Animation)
                {
                    if (a == null || a.Sequence == null || a.Sequence.Next == null) continue;
                    if (hub == null || a.Sequence.Next.Length > hub.Sequence.Next.Length) hub = a;
                }
                int hubId = hub != null ? hub.Id : -1;

                int here = 0;
                foreach (XmlData.AnimationNode a in root.Animations.Animation)
                {
                    if (a == null || a.Gravity == null || IsMagicName(a.Name)) continue;
                    if (StartX(a) != 0 || StartY(a) != 0 || EndX(a) != 0 || EndY(a) != 0) continue;   // idle only
                    if (a.Sequence == null || a.Sequence.Frame == null || a.Sequence.Frame.Length == 0) continue;

                    int target = a.Id == hubId ? PetEmitter.HubDwellTargetMs : PetEmitter.RestDwellTargetMs;
                    int frames = a.Sequence.Frame.Length;
                    a.Sequence.RepeatFromFrame = 0;
                    if (frames == 1)
                    {
                        int interval, repeat;
                        PetEmitter.SingleFrameRestTiming(target, out interval, out repeat);
                        a.Start.Interval = interval.ToString(CultureInfo.InvariantCulture);
                        a.End.Interval = interval.ToString(CultureInfo.InvariantCulture);
                        a.Sequence.RepeatCount = repeat.ToString(CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        int i0 = Math.Min(ParseCoord(a.Start.Interval), PetEmitter.RestIntervalCapMs);
                        int iN = Math.Min(ParseCoord(a.End.Interval), PetEmitter.RestIntervalCapMs);
                        if (i0 < 1) i0 = PetEmitter.RestIntervalCapMs;
                        if (iN < 1) iN = i0;
                        a.Start.Interval = i0.ToString(CultureInfo.InvariantCulture);
                        a.End.Interval = iN.ToString(CultureInfo.InvariantCulture);
                        int passMs = frames * ((i0 + iN) / 2);
                        a.Sequence.RepeatCount = PetEmitter.RepeatCountForBudget(passMs, target, PetEmitter.MaxRestRepeatCount, false)
                            .ToString(CultureInfo.InvariantCulture);
                    }
                    if (a.Id != hubId) here++;
                }

                root.Header.Version = PetEmitter.ConvertedFormatVersion;
                string outXml = ShimejiEngine.Serialize(root);
                XmlData.RootNode reparsed; string reError;
                if (!ShimejiEngine.TryValidate(outXml, out reparsed, out reError))
                { Console.Error.WriteLine(name.PadRight(36) + " FAIL (re-validate: " + reError + ")"); failures++; continue; }
                GraphReport graph = ShimejiEngine.Analyze(reparsed);
                if (graph != null && graph.Unreachable.Count > 0)
                { Console.Error.WriteLine(name.PadRight(36) + " FAIL (unreachable: " + string.Join(",", graph.Unreachable) + ")"); failures++; continue; }
                File.WriteAllText(path, outXml, new UTF8Encoding(false));
                petsChanged++; longer += here;
                Console.WriteLine(name.PadRight(36) + " hub brief, " + here + " performance(s) lengthened");
            }
            Console.WriteLine();
            Console.WriteLine("pets " + pets.Count + "   changed " + petsChanged + "   performances lengthened " +
                longer + "   skipped " + skipped + "   failures " + failures);
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// Migration: drop sprite cells that are byte-identical to another cell, re-grid the sheet, and
        /// renumber every &lt;frame&gt; to point at the survivor.
        ///
        /// The sheet builder deduped poses by image NAME (ShimejiPose.FrameKey is image + anchor), so a skin
        /// that ships the same picture under two filenames got two cells. Both causes are real in the corpus:
        /// an Android-Shimeji template that duplicates sprite files, and a reversed sequence (brq51bkr's
        /// `descend` is its `climb` frame-for-frame backwards) which needs no new cells at all because
        /// &lt;sequence&gt; already takes an arbitrary frame list.
        ///
        /// Pixels only: no animation, timing, weight or transition is touched, and the pet must render the
        /// exact same images in the exact same order afterwards. That is asserted rather than assumed, by
        /// re-slicing the NEW sheet and comparing each frame to the OLD cell it replaced -- a regrid that
        /// pasted a cell one row off would otherwise corrupt art silently.
        /// </summary>
        private static int Dedupe(string petsDirectory)
        {
            if (!Directory.Exists(petsDirectory)) { Console.Error.WriteLine("No such directory: " + petsDirectory); return 2; }
            var pets = new List<string>();
            foreach (string candidate in Directory.GetDirectories(petsDirectory))
                if (File.Exists(Path.Combine(candidate, "animations.xml"))) pets.Add(candidate);
            pets.Sort(StringComparer.OrdinalIgnoreCase);
            if (pets.Count == 0) { Console.Error.WriteLine("Found no <dir>\\animations.xml under " + petsDirectory); return 2; }

            int petsChanged = 0, skipped = 0, failures = 0, cellsDropped = 0, stamped = 0;
            long bytesBefore = 0, bytesAfter = 0;
            foreach (string petDir in pets)
            {
                string name = Path.GetFileName(petDir);
                string path = Path.Combine(petDir, "animations.xml");
                XmlData.RootNode root; string error;
                if (!ShimejiEngine.TryValidate(File.ReadAllText(path, Encoding.UTF8), out root, out error))
                { Console.WriteLine(name.PadRight(36) + " SKIP (invalid: " + error + ")"); skipped++; continue; }
                if (root.Header == null ||
                    !string.Equals(root.Header.Author, PetEmitter.ConvertedAuthor, StringComparison.Ordinal))
                { Console.WriteLine(name.PadRight(36) + " skip (not converter output)"); skipped++; continue; }
                if (!string.Equals(root.Header.Version, PetEmitter.ConvertedFormatVersionDuplicateCells, StringComparison.Ordinal))
                { Console.WriteLine(name.PadRight(36) + " skip (already at format " + (root.Header.Version ?? "?") + ")"); skipped++; continue; }
                if (root.Image == null || string.IsNullOrEmpty(root.Image.Png) ||
                    root.Animations == null || root.Animations.Animation == null)
                { Console.WriteLine(name.PadRight(36) + " skip (no sheet or no animations)"); skipped++; continue; }

                // A pet with nothing to dedupe must STILL advance to the next format, or it can never
                // reach `undirect`: the version marks "this pet has been through the pass", not "this pass
                // changed it". Missing that stranded 3g8t9v4e, which has no duplicate cells and 8 names that
                // wanted renaming.
                string report;
                int dropped;
                DedupeOutcome outcome = DedupeSheet(root, out report, out dropped);
                if (outcome == DedupeOutcome.Failed)
                { Console.Error.WriteLine(name.PadRight(36) + " " + report); failures++; continue; }

                root.Header.Version = PetEmitter.ConvertedFormatVersionDirectionalNames;
                string outXml = ShimejiEngine.Serialize(root);
                XmlData.RootNode reparsed; string reError;
                if (!ShimejiEngine.TryValidate(outXml, out reparsed, out reError))
                { Console.Error.WriteLine(name.PadRight(36) + " FAIL (re-validate: " + reError + ")"); failures++; continue; }
                GraphReport graph = ShimejiEngine.Analyze(reparsed);
                if (graph != null && graph.Unreachable.Count > 0)
                { Console.Error.WriteLine(name.PadRight(36) + " FAIL (unreachable: " + string.Join(",", graph.Unreachable) + ")"); failures++; continue; }

                long before = new FileInfo(path).Length;
                File.WriteAllText(path, outXml, new UTF8Encoding(false));
                long after = new FileInfo(path).Length;
                if (outcome == DedupeOutcome.Changed)
                {
                    bytesBefore += before; bytesAfter += after;
                    petsChanged++; cellsDropped += dropped;
                    Console.WriteLine(name.PadRight(36) + " " + report +
                        "   " + (before / 1024) + "KB -> " + (after / 1024) + "KB");
                }
                else
                {
                    stamped++;
                    Console.WriteLine(name.PadRight(36) + " " + report);
                }
            }
            Console.WriteLine();
            Console.WriteLine("pets " + pets.Count + "   changed " + petsChanged + "   cells dropped " + cellsDropped +
                "   already lean " + stamped + "   skipped " + skipped + "   failures " + failures);
            if (bytesBefore > 0)
                Console.WriteLine("xml " + (bytesBefore / 1024) + "KB -> " + (bytesAfter / 1024) + "KB   saved " +
                    ((bytesBefore - bytesAfter) / 1024) + "KB (" +
                    (100.0 * (bytesBefore - bytesAfter) / bytesBefore).ToString("F1", CultureInfo.InvariantCulture) + "%)");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// Rewrite one pet's sheet in place with duplicate cells removed. False when there is nothing to do
        /// or the result would not actually be smaller, with the reason in <paramref name="report"/>.
        /// </summary>
        private enum DedupeOutcome { Changed, Unchanged, Failed }

        private static DedupeOutcome DedupeSheet(XmlData.RootNode root, out string report, out int dropped)
        {
            report = null;
            dropped = 0;
            int tilesX = root.Image.TilesX, tilesY = root.Image.TilesY;
            if (tilesX < 1 || tilesY < 1) { report = "no change (degenerate grid)"; return DedupeOutcome.Unchanged; }

            byte[] originalPng;
            try { originalPng = Convert.FromBase64String(root.Image.Png); }
            catch (FormatException) { report = "no change (sheet is not valid base64)"; return DedupeOutcome.Unchanged; }

            using (var msIn = new MemoryStream(originalPng))
            using (var decoded = new System.Drawing.Bitmap(msIn))
            {
                int total = tilesX * tilesY;
                int cw = decoded.Width / tilesX, ch = decoded.Height / tilesY;
                if (cw < 1 || ch < 1) { report = "no change (cell smaller than a pixel)"; return DedupeOutcome.Unchanged; }

                // Canonical cell per distinct content, in first-appearance order so the surviving indices
                // stay close to the originals and a diff of the XML remains readable.
                var canonical = new Dictionary<string, int>(StringComparer.Ordinal);
                var map = new int[total];
                var survivors = new List<int>();
                for (int i = 0; i < total; i++)
                {
                    string hash = CellHash(decoded, i, tilesX, cw, ch);
                    int owner;
                    if (canonical.TryGetValue(hash, out owner)) { map[i] = owner; continue; }
                    canonical[hash] = survivors.Count;
                    map[i] = survivors.Count;
                    survivors.Add(i);
                }
                if (survivors.Count == total) { report = "no change (no duplicate cells)"; return DedupeOutcome.Unchanged; }

                int newTilesX = (int)Math.Ceiling(Math.Sqrt(survivors.Count));
                int newTilesY = (int)Math.Ceiling((double)survivors.Count / newTilesX);

                byte[] newPng;
                using (var packed = new System.Drawing.Bitmap(
                    newTilesX * cw, newTilesY * ch, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    // Copy RAW PIXELS rather than Graphics.DrawImage. A 1:1 blit still goes through GDI+
                    // resampling -- the default InterpolationMode is bilinear -- so DrawImage altered edge
                    // pixels and the render-equivalence check below rejected every pet in the corpus. A row
                    // copy is exact by construction, and faster.
                    PackCells(decoded, packed, survivors, tilesX, newTilesX, cw, ch,
                        string.Equals(root.Image.Transparency, "Magenta", StringComparison.OrdinalIgnoreCase));

                    using (var msOut = new MemoryStream())
                    {
                        packed.Save(msOut, System.Drawing.Imaging.ImageFormat.Png);
                        newPng = msOut.ToArray();
                    }

                    // Re-grid can COMPRESS WORSE than the original: gengar came out 1KB bigger, because PNG
                    // filtering works on scanlines and the new layout puts different neighbours side by side.
                    // Dropping cells is only worth doing when it actually wins.
                    if (newPng.Length >= originalPng.Length)
                    {
                        int delta = newPng.Length - originalPng.Length;
                        report = "no change (" + (total - survivors.Count) + " duplicate cell(s), but the re-grid " +
                            (delta == 0
                                ? "saves nothing -- they are blank cells, so the grid does not shrink)"
                                : "is " + delta + " bytes BIGGER)");
                        return DedupeOutcome.Unchanged;
                    }

                    // Prove the pet still renders the same pictures in the same order, against the packed
                    // sheet actually produced rather than against the map that was supposed to produce it.
                    foreach (XmlData.AnimationNode a in root.Animations.Animation)
                    {
                        if (a == null || a.Sequence == null || a.Sequence.Frame == null) continue;
                        foreach (int f in a.Sequence.Frame)
                        {
                            if (f < 0 || f >= total) { report = "FAIL (frame " + f + " is outside the sheet)"; return DedupeOutcome.Failed; }
                            if (!CellHash(decoded, f, tilesX, cw, ch)
                                    .Equals(CellHash(packed, map[f], newTilesX, cw, ch), StringComparison.Ordinal))
                            {
                                report = "FAIL (frame " + f + " would render different art after the re-grid)";
                                return DedupeOutcome.Failed;
                            }
                        }
                    }
                }

                foreach (XmlData.AnimationNode a in root.Animations.Animation)
                {
                    if (a == null || a.Sequence == null || a.Sequence.Frame == null) continue;
                    for (int i = 0; i < a.Sequence.Frame.Length; i++)
                        a.Sequence.Frame[i] = map[a.Sequence.Frame[i]];
                }

                root.Image.TilesX = newTilesX;
                root.Image.TilesY = newTilesY;
                root.Image.Png = Convert.ToBase64String(newPng);
                dropped = total - survivors.Count;
                report = dropped + " of " + total + " cells were duplicates";
                return DedupeOutcome.Changed;
            }
        }

        /// <summary>
        /// Copy the surviving cells into the packed sheet, pixel for pixel.
        ///
        /// Tail cells past the last survivor are never referenced by any frame, but they are filled the way
        /// the pet DECLARES its transparency rather than left to chance: a keyed pet needs magenta there, an
        /// alpha pet needs zeroes (which is already a new Bitmap's state, so the fill only runs when keyed).
        /// </summary>
        private static void PackCells(System.Drawing.Bitmap source, System.Drawing.Bitmap target,
            List<int> survivors, int srcTilesX, int dstTilesX, int cw, int ch, bool keyed)
        {
            const System.Drawing.Imaging.PixelFormat Fmt = System.Drawing.Imaging.PixelFormat.Format32bppArgb;
            System.Drawing.Imaging.BitmapData src = source.LockBits(
                new System.Drawing.Rectangle(0, 0, source.Width, source.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, Fmt);
            try
            {
                System.Drawing.Imaging.BitmapData dst = target.LockBits(
                    new System.Drawing.Rectangle(0, 0, target.Width, target.Height),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly, Fmt);
                try
                {
                    var row = new byte[cw * 4];
                    if (keyed)
                    {
                        var fill = new byte[dst.Stride];                    // BGRA little-endian: magenta, opaque
                        for (int i = 0; i + 3 < fill.Length; i += 4)
                        { fill[i] = 255; fill[i + 1] = 0; fill[i + 2] = 255; fill[i + 3] = 255; }
                        for (int y = 0; y < target.Height; y++)
                            System.Runtime.InteropServices.Marshal.Copy(fill, 0, dst.Scan0 + y * dst.Stride, fill.Length);
                    }
                    for (int k = 0; k < survivors.Count; k++)
                    {
                        int s = survivors[k];
                        int sx = (s % srcTilesX) * cw, sy = (s / srcTilesX) * ch;
                        int dx = (k % dstTilesX) * cw, dy = (k / dstTilesX) * ch;
                        for (int y = 0; y < ch; y++)
                        {
                            System.Runtime.InteropServices.Marshal.Copy(
                                src.Scan0 + (sy + y) * src.Stride + sx * 4, row, 0, row.Length);
                            System.Runtime.InteropServices.Marshal.Copy(
                                row, 0, dst.Scan0 + (dy + y) * dst.Stride + dx * 4, row.Length);
                        }
                    }
                }
                finally { target.UnlockBits(dst); }
            }
            finally { source.UnlockBits(src); }
        }

        /// <summary>SHA-256 of one cell's raw pixels, read row by row so the sheet's stride padding never
        /// makes two identical pictures hash differently.</summary>
        private static string CellHash(System.Drawing.Bitmap sheet, int index, int tilesX, int cw, int ch)
        {
            var rect = new System.Drawing.Rectangle((index % tilesX) * cw, (index / tilesX) * ch, cw, ch);
            System.Drawing.Imaging.BitmapData data = sheet.LockBits(
                rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[cw * 4];
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    for (int y = 0; y < ch; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(
                            data.Scan0 + y * data.Stride, row, 0, row.Length);
                        sha.TransformBlock(row, 0, row.Length, null, 0);
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    return BitConverter.ToString(sha.Hash);
                }
            }
            finally { sheet.UnlockBits(data); }
        }

        /// <summary>
        /// Migration: drop the `_left` / `_right` suffix from action names.
        ///
        /// A converted pet keeps ONE copy of each mirrored pair and flips its entire sheet on
        /// `&lt;action&gt;flip&lt;/action&gt;`, so `walk_left` already walks both ways and the suffix reads as
        /// a restriction that does not exist. The maintainer hit this directly: looking at a reachability map
        /// of `_left` names and asking where `walk_right` went.
        ///
        /// Names only -- no frame, timing, weight or transition changes. The rules that make a rename unsafe
        /// live in <see cref="PetEmitter.UndirectNames"/> so the emitter and this migration cannot drift.
        /// </summary>
        private static int Undirect(string petsDirectory)
        {
            if (!Directory.Exists(petsDirectory)) { Console.Error.WriteLine("No such directory: " + petsDirectory); return 2; }
            var pets = new List<string>();
            foreach (string candidate in Directory.GetDirectories(petsDirectory))
                if (File.Exists(Path.Combine(candidate, "animations.xml"))) pets.Add(candidate);
            pets.Sort(StringComparer.OrdinalIgnoreCase);
            if (pets.Count == 0) { Console.Error.WriteLine("Found no <dir>\\animations.xml under " + petsDirectory); return 2; }

            int petsChanged = 0, skipped = 0, failures = 0, renamed = 0, refused = 0;
            foreach (string petDir in pets)
            {
                string name = Path.GetFileName(petDir);
                string path = Path.Combine(petDir, "animations.xml");
                XmlData.RootNode root; string error;
                if (!ShimejiEngine.TryValidate(File.ReadAllText(path, Encoding.UTF8), out root, out error))
                { Console.WriteLine(name.PadRight(36) + " SKIP (invalid: " + error + ")"); skipped++; continue; }
                if (root.Header == null ||
                    !string.Equals(root.Header.Author, PetEmitter.ConvertedAuthor, StringComparison.Ordinal))
                { Console.WriteLine(name.PadRight(36) + " skip (not converter output)"); skipped++; continue; }
                if (!string.Equals(root.Header.Version, PetEmitter.ConvertedFormatVersionDirectionalNames, StringComparison.Ordinal))
                { Console.WriteLine(name.PadRight(36) + " skip (already at format " + (root.Header.Version ?? "?") + ")"); skipped++; continue; }
                if (root.Animations == null || root.Animations.Animation == null)
                { Console.WriteLine(name.PadRight(36) + " skip (no animations)"); skipped++; continue; }

                var names = new List<string>();
                foreach (XmlData.AnimationNode a in root.Animations.Animation)
                    if (a != null) names.Add(a.Name ?? "");
                Dictionary<string, string> map = PetEmitter.UndirectNames(names);

                int couldHaveRenamed = 0;
                foreach (string n in names)
                    if (n.EndsWith("_left", StringComparison.OrdinalIgnoreCase) ||
                        n.EndsWith("_right", StringComparison.OrdinalIgnoreCase)) couldHaveRenamed++;
                refused += couldHaveRenamed - map.Count;

                if (map.Count == 0)
                { Console.WriteLine(name.PadRight(36) + " skip (no directional names)"); skipped++; continue; }

                foreach (XmlData.AnimationNode a in root.Animations.Animation)
                {
                    if (a == null || a.Name == null) continue;
                    string renamedTo;
                    if (map.TryGetValue(a.Name, out renamedTo)) a.Name = renamedTo;
                }

                root.Header.Version = PetEmitter.ConvertedFormatVersion;
                string outXml = ShimejiEngine.Serialize(root);
                XmlData.RootNode reparsed; string reError;
                if (!ShimejiEngine.TryValidate(outXml, out reparsed, out reError))
                { Console.Error.WriteLine(name.PadRight(36) + " FAIL (re-validate: " + reError + ")"); failures++; continue; }
                GraphReport graph = ShimejiEngine.Analyze(reparsed);
                if (graph != null && graph.Unreachable.Count > 0)
                { Console.Error.WriteLine(name.PadRight(36) + " FAIL (unreachable: " + string.Join(",", graph.Unreachable) + ")"); failures++; continue; }

                File.WriteAllText(path, outXml, new UTF8Encoding(false));
                petsChanged++; renamed += map.Count;
                Console.WriteLine(name.PadRight(36) + " renamed " + map.Count + " of " + names.Count);
            }
            Console.WriteLine();
            Console.WriteLine("pets " + pets.Count + "   changed " + petsChanged + "   renamed " + renamed +
                "   refused " + refused + "   skipped " + skipped + "   failures " + failures);
            return failures == 0 ? 0 : 1;
        }

        /// <summary>Total on-screen time of one animation in ms, replaying the engine's interval interpolation
        /// (start -&gt; end across the declared steps). The SCREEN time, not one pass.</summary>
        private static int TotalDwellMs(XmlData.AnimationNode a)
        {
            if (a == null || a.Sequence == null || a.Sequence.Frame == null || a.Sequence.Frame.Length == 0) return 0;
            int frames = a.Sequence.Frame.Length;
            int rf = Math.Max(0, Math.Min(frames - 1, a.Sequence.RepeatFromFrame));
            int rep = Math.Max(0, ParseCoord(a.Sequence.RepeatCount));
            int steps = Math.Max(1, frames + (frames - rf) * rep);
            int i0 = ParseCoord(a.Start != null ? a.Start.Interval : null);
            int iN = ParseCoord(a.End != null ? a.End.Interval : null);
            int ip = steps <= 1 ? 1 : steps - 1;
            double total = 0;
            for (int k = 0; k < steps; k++) total += i0 + (double)(iN - i0) * k / ip;
            return (int)Math.Round(total);
        }

        private static bool IsMagicName(string name)
        {
            foreach (string magic in new[] { "fall", "drag", "kill", "sync" })
                if (string.Equals(name, magic, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static int EndY(XmlData.AnimationNode a) { return ParseCoord(a.End != null ? a.End.Y : null); }

        private static int StartY(XmlData.AnimationNode a) { return ParseCoord(a.Start != null ? a.Start.Y : null); }
        private static int StartX(XmlData.AnimationNode a) { return ParseCoord(a.Start != null ? a.Start.X : null); }
        private static int EndX(XmlData.AnimationNode a) { return ParseCoord(a.End != null ? a.End.X : null); }

        // A coordinate may be an EXPRESSION in this format (the sheep use random*.../screenW). A converted pet
        // never has one, and returning 0 for anything unparseable is what keeps this migration off the ones
        // that do rather than mangling them.
        private static int ParseCoord(string value)
        {
            int parsed;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }

        private static void SetXy(XmlData.MovingNode m, int x, int y)
        {
            if (m == null) return;
            m.X = x.ToString(CultureInfo.InvariantCulture);
            m.Y = y.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>A border's edges with every edge carrying the given only-flag removed. Used both to strip
        /// an edge a flattened animation can no longer reach, and to make re-running idempotent.</summary>
        private static XmlData.NextNode[] WithoutOnly(XmlData.HitNode border, string onlyFlag)
        {
            var kept = new List<XmlData.NextNode>();
            if (border != null && border.Next != null)
                foreach (XmlData.NextNode n in border.Next)
                    if (n != null && !string.Equals(n.OnlyFlag, onlyFlag, StringComparison.Ordinal)) kept.Add(n);
            return kept.ToArray();
        }

        // A locomotion spoke turns at the screen edge: it has a border whose next targets an animation
        // named "turn". This keeps the migration from touching a hand-authored pet that merely uses repeat="6".
        private static bool TurnsAtBorder(XmlData.AnimationNode a, Dictionary<int, string> nameById)
        {
            if (a.Border == null || a.Border.Next == null) return false;
            foreach (XmlData.NextNode n in a.Border.Next)
            {
                string target;
                if (n != null && nameById.TryGetValue(n.Value, out target) &&
                    string.Equals(target, "turn", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // One frame's on-screen interval in ms: the mean of the animation's start and end interval (a
        // locomotion animation is near-constant, so the mean is its per-frame time). Falls back to whichever
        // end is present. 0 when neither parses, which keeps LocoRepeatCount at its ceiling (a no-op here).
        private static int IntervalMs(XmlData.AnimationNode a)
        {
            int s = ParseMs(a.Start != null ? a.Start.Interval : null);
            int e = ParseMs(a.End != null ? a.End.Interval : null);
            if (s > 0 && e > 0) return (s + e) / 2;
            return s > 0 ? s : e;
        }

        private static int ParseMs(string value)
        {
            int ms;
            return int.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out ms) && ms > 0 ? ms : 0;
        }
    }
}
