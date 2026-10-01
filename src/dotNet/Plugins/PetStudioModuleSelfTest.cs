using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.Plugins
{
    /// <summary>
    /// --petstudio-selftest: proves the Companion Studio module loads through the real AssemblyLoadContext and that
    /// its analysis agrees with the host's own validator.
    ///
    /// The agreement half is the point. Companion Studio source-links the host's parser rather than copying it, and
    /// the whole justification for that is "its verdict cannot drift from what the host will actually run".
    /// That is a claim worth testing rather than asserting: the module's analyzer and the host's
    /// CompanionXmlValidator are run over the same inputs and required to reach the same conclusion.
    ///
    /// Reflected, because the base keeps no compile-time reference to any module. Skips-pass if absent.
    /// </summary>
    internal static class PetStudioModuleSelfTest
    {
        public static bool Run()
        {
            var sb = new StringBuilder();
            bool ok = true;
            string tempRoot = null;
            try
            {
                string bundled = Path.Combine(AppContext.BaseDirectory, "modules", "petstudio");
                if (!Directory.Exists(bundled))
                {
                    sb.AppendLine("SKIP: no bundled petstudio module at " + bundled);
                    return Finish(sb, true);
                }

                // Isolate so the recording host reflects this module's Init alone. The WHOLE tree (F354):
                // the shipped payload carries a native\ folder, and a top-level copy loaded a module without
                // it, on a code path no installed copy takes.
                tempRoot = SelfTestScratch.Create("petstudio");
                string dest = Path.Combine(tempRoot, "petstudio");
                SelfTestScratch.CopyTree(bundled, dest);
                ok &= Check(sb, "WITNESS the shipped petstudio payload carries a native\\ subfolder",
                    Directory.Exists(Path.Combine(bundled, "native")));
                ok &= Check(sb, "the isolated copy carries the module's subfolders (native\\), not only its top-level files",
                    Directory.Exists(Path.Combine(dest, "native")) &&
                    Directory.GetFiles(Path.Combine(dest, "native"), "*", SearchOption.AllDirectories).Length ==
                    Directory.GetFiles(Path.Combine(bundled, "native"), "*", SearchOption.AllDirectories).Length);

                var host = new RecordingHost();
                using (var loader = new ModuleHost())
                {
                    int loaded = loader.LoadFrom(tempRoot, host, s => sb.AppendLine("  " + s));
                    ok &= Check(sb, "exactly one module loaded (isolated)", loaded == 1);

                    IModule studio = null;
                    foreach (IModule m in loader.Modules)
                        if (m != null && m.Info != null &&
                            string.Equals(m.Info.Id, "petstudio", StringComparison.OrdinalIgnoreCase))
                            studio = m;
                    ok &= Check(sb, "petstudio module reports its id", studio != null);
                    if (studio == null) return Finish(sb, false);

                    ok &= Check(sb, "declares Pets + Storage",
                        studio.Info.Permissions.HasFlag(ModulePermissions.Companions) &&
                        studio.Info.Permissions.HasFlag(ModulePermissions.Storage));
                    // LaunchProcess too (N-petstudio-05): F226 made the studio disclose that it spawns dwebp, and
                    // ffmpeg when present, and only the module's own self-test pinned the disclosure, so the host's
                    // reading of the manifest could not notice it gone. WITNESS the declaration is not a blanket.
                    ok &= Check(sb, "declares LaunchProcess (F226: it spawns dwebp, and ffmpeg when present)",
                        studio.Info.Permissions.HasFlag(ModulePermissions.LaunchProcess));
                    ok &= Check(sb, "WITNESS the declaration is not a blanket: Microphone, which nothing here uses, is not declared",
                        !studio.Info.Permissions.HasFlag(ModulePermissions.Microphone));
                    // Animation, because "Preview highlighted action" calls IHost.TryPlayAnimation. Asserted
                    // even though CompanionHost does NOT gate that verb on it -- TryPlayAnimation takes no
                    // moduleId, so there is no caller identity to check and the declaration unlocks nothing.
                    // It is the module's honest statement of what it does, and an undeclared capability that
                    // happens to work is exactly the thing nobody notices has been dropped.
                    ok &= Check(sb, "declares Animation (it plays animations on the preview)",
                        studio.Info.Permissions.HasFlag(ModulePermissions.Animation));
                    // It needs the companion-manager verbs, so it must declare a host floor at all rather
                    // than loading into anything. This used to assert MinHostVersion != "1.0.0", using
                    // "1.0.0" as a stand-in for "the module named no real floor" -- which stopped being
                    // true when the whole product rebased TO 1.0.0. The capability itself is asserted on
                    // the line above through the Companions permission; what is left to check here is that
                    // a floor is declared and is a parseable version rather than a placeholder string.
                    Version parsedFloor;
                    ok &= Check(sb, "declares a parseable MinHostVersion floor",
                        !string.IsNullOrEmpty(studio.Info.MinHostVersion) &&
                        Version.TryParse(studio.Info.MinHostVersion, out parsedFloor));
                    ok &= Check(sb, "contributes a tray item and an options pane",
                        host.TrayItems.Count >= 1 && host.OptionsPanes.Count >= 1);
                    ok &= Check(sb, "the tray item ships an icon (embedded PNG resolves)",
                        host.TrayItems.Count >= 1 && host.TrayItems[0].IconPng != null && host.TrayItems[0].IconPng.Length > 0);
                    // The ACTION, not the array: `Actions != null` passed on an empty array and on any other
                    // action, while the one thing worth pinning -- that the studio can be opened from the
                    // settings window -- was not pinned (F355). The label ends in U+2026, hence StartsWith.
                    PaneAction openStudio = null;
                    if (host.OptionsPanes.Count > 0 && host.OptionsPanes[0].Actions != null)
                        foreach (PaneAction action in host.OptionsPanes[0].Actions)
                            if (action != null && action.Label != null &&
                                action.Label.StartsWith("Open Companion Studio", StringComparison.Ordinal))
                                openStudio = action;
                    ok &= Check(sb, "opening the studio is offered as a pane action (labelled, with an InvokeAsync)",
                        openStudio != null && openStudio.InvokeAsync != null);

                    ok &= PlayOnPreviewIsOfferedOnlyWhenUsable(sb, studio.GetType().Assembly);
                    ok &= AnalyzerAgreesWithTheHost(sb, studio.GetType().Assembly);
                    ok &= FixtureCarriesTheBundledGraph(sb, studio.GetType().Assembly);
                    ok &= DirectoryPolicyHolds(sb, studio.GetType().Assembly);
                    ok &= ThemeFollowsTheHost(sb, studio.GetType().Assembly);
                    ok &= ImportEngineIsWired(sb, studio.GetType().Assembly);
                    ok &= BehaviourChainIsSound(sb, studio.GetType().Assembly);
                    ok &= ModuleChecksPass(sb, studio.GetType().Assembly,
                        "DesktopAICompanion.PetStudioModule.AnimCapabilitySelfCheck",
                        "the map reports what each animation DOES, not just its name");

                    loader.ShutdownAll(s => sb.AppendLine("  " + s));
                }
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                // Expected to fail: the collectible ALC unloads asynchronously, so the module DLL is still
                // mapped. Reported rather than swallowed; the next run's sweep collects the directory.
                string releaseDetail;
                if (!SelfTestScratch.TryRelease(tempRoot, out releaseDetail))
                    sb.AppendLine("NOTE: scratch left for the next sweep (" + releaseDetail + ")");
            }
            return Finish(sb, ok);
        }

        /// <summary>
        /// The studio's window theme must follow IHost.IsDarkTheme, not the OS. Only the host knows whether the
        /// user's light/dark/SYSTEM choice resolves to dark, so a module reading the registry is right only while
        /// the host sits on "system" and wrong the moment someone pins the opposite. Driven both ways here, which
        /// is exactly what the retired DESKTOP_AI_COMPANION_FORCE_THEME env override existed to allow.
        /// </summary>
        private static bool ThemeFollowsTheHost(StringBuilder sb, Assembly moduleAssembly)
        {
            Type theme = moduleAssembly.GetType("DesktopAICompanion.PetStudioModule.PetStudioTheme");
            if (!Check(sb, "module exposes PetStudioTheme", theme != null)) return false;
            MethodInfo current = theme.GetMethod("Current", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (!Check(sb, "PetStudioTheme exposes Current(IHost)", current != null)) return false;
            FieldInfo dark = theme.GetField("Dark", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (!Check(sb, "PetStudioTheme exposes Dark", dark != null)) return false;

            bool ok = true;
            ok &= Check(sb, "a dark host gives a dark theme",
                (bool)dark.GetValue(current.Invoke(null, new object[] { new RecordingHost { IsDarkTheme = true } })));
            ok &= Check(sb, "a light host gives a light theme",
                !(bool)dark.GetValue(current.Invoke(null, new object[] { new RecordingHost { IsDarkTheme = false } })));
            // No host at all must not throw: a wrong-but-readable window beats an exception, and it is the
            // direction the host's own resolver fails in too.
            ok &= Check(sb, "no host falls back to light",
                !(bool)dark.GetValue(current.Invoke(null, new object[] { null })));
            return ok;
        }

        /// <summary>
        /// "Preview highlighted action" must be offered only when it can do something: a host, a LIVE preview,
        /// and a NAMED selection. The name is the condition that is easy to lose, because IHost.TryPlayAnimation
        /// resolves by name against the running pet's XML, so an unnamed animation cannot be requested at all
        /// however clearly the map draws it.
        ///
        /// Asserts the RULE, not the button: the four pieces of WPF state that feed it are unreachable headless,
        /// and a rule that silently inverts is the failure worth catching. All four combinations are driven,
        /// because "returns false always" passes any single negative case on its own.
        /// </summary>
        private static bool PlayOnPreviewIsOfferedOnlyWhenUsable(StringBuilder sb, Assembly moduleAssembly)
        {
            Type window = moduleAssembly.GetType("DesktopAICompanion.PetStudioModule.PetStudioWindow");
            if (!Check(sb, "module exposes PetStudioWindow", window != null)) return false;
            MethodInfo can = window.GetMethod("CanPlayOnPreview", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (!Check(sb, "PetStudioWindow exposes CanPlayOnPreview(host, alive, name)", can != null)) return false;

            Func<bool, bool, string, bool> rule = delegate (bool host, bool alive, string name)
            {
                return (bool)can.Invoke(null, new object[] { host, alive, name });
            };

            bool ok = true;
            ok &= Check(sb, "offered with a host, a live preview and a named selection", rule(true, true, "jump"));
            ok &= Check(sb, "withheld with no host", !rule(false, true, "jump"));
            ok &= Check(sb, "withheld with no live preview", !rule(true, false, "jump"));
            ok &= Check(sb, "withheld with nothing selected", !rule(true, true, null));
            ok &= Check(sb, "withheld when the selected animation has no name", !rule(true, true, "   "));
            return ok;
        }

        /// <summary>
        /// Companion Studio now hosts the Shimeji import flow, so the conversion engine must be source-compiled into
        /// this module's own assembly (not a dropped reference) and its bundled base conf must travel embedded.
        /// A full convert is already gated by the CLI's engine self-tests; this proves the wiring survived: the
        /// engine type is present and its embedded base conf parses to the reference census (91 actions).
        /// </summary>
        private static bool ImportEngineIsWired(StringBuilder sb, Assembly moduleAssembly)
        {
            Type engine = moduleAssembly.GetType("DesktopAICompanion.Tools.ShimejiConvert.ShimejiEngine");
            if (!Check(sb, "module compiles in ShimejiEngine (Shimeji import wired)", engine != null)) return false;
            bool ok = Check(sb, "ShimejiEngine exposes ConvertSkin",
                engine.GetMethod("ConvertSkin", BindingFlags.Static | BindingFlags.Public) != null);

            Type parser = moduleAssembly.GetType("DesktopAICompanion.Tools.ShimejiConvert.Shimeji.ShimejiParser");
            MethodInfo bundled = parser != null
                ? parser.GetMethod("ParseBundledConf", BindingFlags.Static | BindingFlags.Public)
                : null;
            if (!Check(sb, "ShimejiParser exposes ParseBundledConf", bundled != null)) return false;
            try
            {
                object cfg = bundled.Invoke(null, null);
                object actionsObj = null;
                FieldInfo f = cfg.GetType().GetField("Actions");
                if (f != null) actionsObj = f.GetValue(cfg);
                else { PropertyInfo p = cfg.GetType().GetProperty("Actions"); if (p != null) actionsObj = p.GetValue(cfg); }
                var actions = actionsObj as System.Collections.ICollection;
                ok &= Check(sb, "bundled base conf embeds and parses (91 actions)", actions != null && actions.Count == 91);
                // The BEHAVIOURS half too. The actions census says nothing about behaviors.xml (groups come
                // from the classifier alone), so the module's embedded copy of that file could vanish and
                // this check would still print the census intact (F442). A frequency table with entries is
                // what proves the second resource parsed.
                object frequencyObj = null;
                FieldInfo ff = cfg.GetType().GetField("BehaviorFrequency");
                if (ff != null) frequencyObj = ff.GetValue(cfg);
                var frequencies = frequencyObj as System.Collections.ICollection;
                ok &= Check(sb, "bundled base behaviours embed and parse (frequency table populated)",
                    frequencies != null && frequencies.Count > 0);
            }
            catch (Exception ex)
            {
                ok &= Check(sb, "bundled base conf parses without throwing (" + ex.GetType().Name + ": " + ex.Message + ")", false);
            }
            return ok;
        }

        /// <summary>
        /// The behaviour debugger compiles a timeline into a throwaway pet, and the host is the thing that has
        /// to run it, so the host gates the assertions even though they live module-side.
        ///
        /// Module-side because the alternative is unreadable: the checks need an IList&lt;ChainStep&gt; and an
        /// IDictionary&lt;int, AnimNode&gt; of types the base cannot reference, so building them from here would
        /// test the reflection as much as the logic. The host supplies the FIXTURE (its own bundled pet, which
        /// is the one pet guaranteed to exist and to be valid) and folds the module's verdict in, so a broken
        /// chain compiler still fails the gate rather than being reported only inside the module.
        ///
        /// A missing entry point is a FAILURE, not a skip. The whole value of these assertions is that they run
        /// on every gate, and a rename that silently stopped invoking them would leave the gate green.
        /// </summary>
        private static bool BehaviourChainIsSound(StringBuilder sb, Assembly moduleAssembly)
        {
            return ModuleChecksPass(sb, moduleAssembly,
                "DesktopAICompanion.PetStudioModule.BehaviourChainSelfCheck",
                "behaviour timeline compiles deterministic, host-valid debug pets");
        }

        /// <summary>
        /// Invoke one module-side <c>RunChecks(string fixturePetXml, out string detail)</c> and fold its
        /// verdict in, echoing its lines so a failure names the assertion rather than the group.
        ///
        /// A missing type or method is a FAILURE, not a skip. These assertions are worth having only because
        /// they run on every gate, and a rename that quietly stopped invoking them would leave it green.
        /// </summary>
        private static bool ModuleChecksPass(StringBuilder sb, Assembly moduleAssembly, string typeName, string verdict)
        {
            Type checks = moduleAssembly.GetType(typeName);
            string shortName = typeName.Substring(typeName.LastIndexOf('.') + 1);
            if (!Check(sb, "module exposes " + shortName, checks != null)) return false;
            MethodInfo run = checks.GetMethod("RunChecks", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (!Check(sb, shortName + " exposes RunChecks", run != null)) return false;

            object[] args = new object[] { Properties.Resources.animations, null };
            bool ok;
            try { ok = (bool)run.Invoke(null, args); }
            catch (Exception ex)
            {
                return Check(sb, shortName + " ran without throwing (" +
                    (ex.InnerException != null ? ex.InnerException.Message : ex.Message) + ")", false);
            }
            string detail = args[1] as string ?? "";
            foreach (string line in detail.Split('\n'))
                if (line.Trim().Length > 0) sb.AppendLine("  " + line.TrimEnd());
            return Check(sb, verdict, ok);
        }

        /// <summary>
        /// The module's analyzer and the host's validator must agree, because the module compiles its own copy
        /// of that validator from the host's source. A disagreement here means the source-link has rotted --
        /// exactly the failure that source-linking is supposed to make impossible, and the reason PetTester
        /// (which link-compiled a file that later moved) broke silently.
        /// </summary>
        private static bool AnalyzerAgreesWithTheHost(StringBuilder sb, Assembly moduleAssembly)
        {
            Type analyzer = moduleAssembly.GetType("DesktopAICompanion.PetStudioModule.PetAnalyzer");
            if (!Check(sb, "module exposes PetAnalyzer", analyzer != null)) return false;
            MethodInfo analyze = analyzer.GetMethod("Analyze", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (!Check(sb, "PetAnalyzer exposes Analyze", analyze != null)) return false;

            string bundledPet = Properties.Resources.animations;
            var cases = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("the bundled pet", bundledPet),
                new KeyValuePair<string, string>("a pet with a DTD", "<?xml version=\"1.0\"?><!DOCTYPE animations [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><animations xmlns=\"https://esheep.petrucci.ch/\"><header><author>&x;</author></header></animations>"),
                new KeyValuePair<string, string>("junk", "not xml at all"),
                new KeyValuePair<string, string>("empty", ""),
            };

            bool ok = true;
            foreach (KeyValuePair<string, string> testCase in cases)
            {
                XmlData.RootNode parsed;
                string hostError;
                bool hostAccepts = CompanionXmlValidator.TryParse(testCase.Value, out parsed, out hostError);

                object report = analyze.Invoke(null, new object[] { testCase.Value });
                bool moduleAccepts = (bool)report.GetType().GetField("IsValid").GetValue(report);

                ok &= Check(sb,
                    "verdicts agree on " + testCase.Key + " (host=" + hostAccepts + ", module=" + moduleAccepts + ")",
                    hostAccepts == moduleAccepts);
            }

            // The bundled pet must also come back with a readable report and no dead animations -- the same
            // invariant --security-selftest asserts host-side, checked here through the module's own path.
            object bundledReport = analyze.Invoke(null, new object[] { bundledPet });
            Type rt = bundledReport.GetType();
            var unreachable = (System.Collections.ICollection)rt.GetField("UnreachableAnimations").GetValue(bundledReport);
            ok &= Check(sb, "the bundled pet reports no unreachable animations", unreachable.Count == 0);

            string described = (string)rt.GetMethod("Describe").Invoke(bundledReport, null);
            ok &= Check(sb, "the report describes the companion in prose",
                !string.IsNullOrWhiteSpace(described) && described.IndexOf("Valid companion", StringComparison.Ordinal) >= 0);

            ok &= AnalysisDataIsSound(sb, bundledReport, rt, unreachable);
            return ok;
        }

        /// <summary>
        /// The per-animation data the map and detail panel draw from: every frame index a node references must
        /// land inside the sprite's TilesX×TilesY grid (a frame beyond the sheet renders nothing), and the set
        /// of nodes the map paints "dead" must be exactly the set the host's reachability walk reported. The
        /// second check is the map's answer to the same drift guard the verdict tests give the validator.
        /// </summary>
        private static bool AnalysisDataIsSound(StringBuilder sb, object report, Type rt, System.Collections.ICollection unreachable)
        {
            int tilesX = (int)rt.GetField("TilesX").GetValue(report);
            int tilesY = (int)rt.GetField("TilesY").GetValue(report);
            int tileCount = tilesX * tilesY;
            var nodes = (System.Collections.IEnumerable)rt.GetField("Nodes").GetValue(report);

            var dead = new HashSet<int>();
            foreach (object id in unreachable) dead.Add((int)id);

            int count = 0;
            bool framesInBounds = true;
            var mapDead = new HashSet<int>();
            foreach (object node in nodes)
            {
                count++;
                Type nt = node.GetType();
                int id = (int)nt.GetField("Id").GetValue(node);
                bool reachable = (bool)nt.GetField("IsReachable").GetValue(node);
                if (!reachable) mapDead.Add(id);
                var frames = (int[])nt.GetField("Frames").GetValue(node);
                if (frames != null)
                    foreach (int f in frames)
                        if (f < 0 || f >= tileCount) framesInBounds = false;
            }

            bool ok = Check(sb, "analysis: a node was produced for the pet's animations", count > 0);
            // The grid is asserted on its own (RA-298): the bounds check used to skip its upper half when the
            // analyzer reported a 0x0 grid, so that regression passed it vacuously; now a 0x0 grid fails here
            // and puts every frame out of bounds below.
            ok &= Check(sb, "analysis: the analyzer reports the sprite's tile grid (" + tilesX + "x" + tilesY + ")", tileCount > 0);
            ok &= Check(sb, "analysis: every frame index lands inside the tile grid", framesInBounds);
            ok &= Check(sb, "analysis: the map's dead set equals the host's unreachable set", mapDead.SetEquals(dead));
            return ok;
        }

        /// <summary>
        /// The Open-dialog directory policy (PetStudioPaths.ResolveInitialDir), pinned through the module's own
        /// copy: a remembered folder that still exists wins, else the pet library, else Documents. Kept as a
        /// pure function precisely so it can be asserted here without a window or a real disk.
        /// </summary>
        private static bool DirectoryPolicyHolds(StringBuilder sb, Assembly moduleAssembly)
        {
            Type paths = moduleAssembly.GetType("DesktopAICompanion.PetStudioModule.PetStudioPaths");
            if (!Check(sb, "module exposes PetStudioPaths", paths != null)) return false;
            MethodInfo resolve = paths.GetMethod("ResolveInitialDir",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (!Check(sb, "PetStudioPaths exposes ResolveInitialDir", resolve != null)) return false;

            // Only these folders "exist"; a stale saved path and an absent library must fall through.
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\wip", @"C:\pets", @"C:\docs" };
            Func<string, bool> exists = s => !string.IsNullOrEmpty(s) && existing.Contains(s);

            string a = (string)resolve.Invoke(null, new object[] { @"C:\wip", @"C:\pets", @"C:\docs", exists });
            bool ok = Check(sb, "open dir: a remembered folder that still exists wins", a == @"C:\wip");

            string b = (string)resolve.Invoke(null, new object[] { @"C:\gone", @"C:\pets", @"C:\docs", exists });
            ok &= Check(sb, "open dir: falls back to the pet library when the remembered folder is gone", b == @"C:\pets");

            string c = (string)resolve.Invoke(null, new object[] { "", @"C:\nopets", @"C:\docs", exists });
            ok &= Check(sb, "open dir: falls back to Documents when neither resolves", c == @"C:\docs");
            return ok;
        }

        /// <summary>
        /// The module's embedded self-test companion (Resources/selftest-companion.xml) must carry the SAME graph
        /// as the host's bundled animations.xml: the same animations (id and name) and the same edges (from, to,
        /// kind, probability, only=). The fixture exists so --module-selftest=petstudio can run the chain checks
        /// without the host's resources, and its literal WITNESS (walk #1 -> vertical_walk_up #37) pins the bundled
        /// pet from the module's side; this is the host's side of that agreement (R-035), so the two files cannot
        /// drift apart in silence. Compared through the module's own analyzer, so the comparison is over what the
        /// studio draws, and every difference is NAMED in the line.
        /// </summary>
        private static bool FixtureCarriesTheBundledGraph(StringBuilder sb, Assembly moduleAssembly)
        {
            string fixture = null;
            foreach (string name in moduleAssembly.GetManifestResourceNames())
            {
                if (!name.EndsWith("selftest-companion.xml", StringComparison.OrdinalIgnoreCase)) continue;
                using (Stream stream = moduleAssembly.GetManifestResourceStream(name))
                using (var reader = new StreamReader(stream, new UTF8Encoding(false), true))
                    fixture = reader.ReadToEnd().TrimStart('﻿');
            }
            if (!Check(sb, "parity: the module embeds selftest-companion.xml", fixture != null)) return false;

            Type analyzer = moduleAssembly.GetType("DesktopAICompanion.PetStudioModule.PetAnalyzer");
            MethodInfo analyze = analyzer != null
                ? analyzer.GetMethod("Analyze", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                : null;
            if (!Check(sb, "parity: PetAnalyzer.Analyze is reachable for the fixture", analyze != null)) return false;

            List<string> bundledNodes, bundledEdges, fixtureNodes, fixtureEdges;
            GraphSignature(analyze.Invoke(null, new object[] { Properties.Resources.animations }), out bundledNodes, out bundledEdges);
            GraphSignature(analyze.Invoke(null, new object[] { fixture }), out fixtureNodes, out fixtureEdges);
            bool ok = Check(sb, "parity: WITNESS both graphs have animations and edges to compare (bundled "
                                + bundledNodes.Count + "/" + bundledEdges.Count + ", fixture "
                                + fixtureNodes.Count + "/" + fixtureEdges.Count + ")",
                bundledNodes.Count > 0 && bundledEdges.Count > 0 && fixtureNodes.Count > 0 && fixtureEdges.Count > 0);
            string nodeDifferences = Differences(bundledNodes, fixtureNodes);
            string edgeDifferences = Differences(bundledEdges, fixtureEdges);
            ok &= Check(sb, "parity: the embedded self-test companion carries the bundled graph's animations" + nodeDifferences,
                nodeDifferences.Length == 0);
            ok &= Check(sb, "parity: the embedded self-test companion carries the bundled graph's edges" + edgeDifferences,
                edgeDifferences.Length == 0);
            return ok;
        }

        /// <summary>One sorted line per animation ("id:name") and per edge ("name(id)->to kind p=N only=flag"),
        /// read reflectively from the module's PetReport so the signature is what the studio draws.</summary>
        private static void GraphSignature(object report, out List<string> nodes, out List<string> edges)
        {
            nodes = new List<string>();
            edges = new List<string>();
            if (report == null) return;
            Type rt = report.GetType();
            var nodeList = (System.Collections.IEnumerable)rt.GetField("Nodes").GetValue(report);
            foreach (object node in nodeList)
            {
                Type nt = node.GetType();
                int id = (int)nt.GetField("Id").GetValue(node);
                string name = (string)nt.GetField("Name").GetValue(node) ?? "";
                nodes.Add(id + ":" + name);
                var edgeList = (System.Collections.IEnumerable)nt.GetField("Edges").GetValue(node);
                foreach (object edge in edgeList)
                {
                    Type et = edge.GetType();
                    edges.Add(name + "(" + id + ")->" + et.GetField("To").GetValue(edge) + " " + et.GetField("Kind").GetValue(edge)
                              + " p=" + et.GetField("Probability").GetValue(edge) + " only=" + (et.GetField("Only").GetValue(edge) ?? ""));
                }
            }
            nodes.Sort(StringComparer.Ordinal);
            edges.Sort(StringComparer.Ordinal);
        }

        /// <summary>"" when the two multisets agree; otherwise the differing entries, up to three a side.</summary>
        private static string Differences(List<string> bundled, List<string> fixture)
        {
            var bundledOnly = new List<string>(bundled);
            foreach (string f in fixture) bundledOnly.Remove(f);
            var fixtureOnly = new List<string>(fixture);
            foreach (string b in bundled) fixtureOnly.Remove(b);
            if (bundledOnly.Count == 0 && fixtureOnly.Count == 0) return "";
            return " -- bundled only (" + bundledOnly.Count + "): " + string.Join("; ", bundledOnly.GetRange(0, Math.Min(3, bundledOnly.Count)).ToArray())
                   + " | fixture only (" + fixtureOnly.Count + "): " + string.Join("; ", fixtureOnly.GetRange(0, Math.Min(3, fixtureOnly.Count)).ToArray());
        }

        private static bool Check(StringBuilder sb, string name, bool cond) { sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name); return cond; }
        private static bool Finish(StringBuilder sb, bool ok)
        {
            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dp-petstudio-selftest.txt"), sb.ToString()); } catch { }
            Console.Out.Write(sb.ToString());
            return ok;
        }

        private sealed class FakeCompanion : ICompanion
        {
            public int Id { get { return 1; } }
            public bool IsBusy { get { return false; } }
            public string TypeId { get { return ""; } }
        }

        /// <summary>A headless IHost: Companion Studio only needs it to contribute UI and hand back a pet manager.</summary>
        private sealed class RecordingHost : IHost
        {
            public string HostVersion { get { return "9999.0.0"; } }
            public bool SpeechEnabled { get { return true; } }
            public double Volume { get { return 0.5; } }
            public string OwnerName { get { return ""; } }
            public void SetOwnerName(string name) { }

            public readonly List<TrayItem> TrayItems = new List<TrayItem>();
            public readonly List<OptionsPane> OptionsPanes = new List<OptionsPane>();

            public event Action<ICompanion> CompanionSpawned;
            public event Action<PokeInfo> CompanionPoked;
            public event Action<ICompanion> CompanionLanded;
            public event Action HostShutdown;
            // Never called: it exists so the events count as "used" under TreatWarningsAsErrors (CS0067).
            internal void TouchEvents() { CompanionSpawned?.Invoke(new FakeCompanion()); CompanionPoked?.Invoke(null); CompanionLanded?.Invoke(null); HostShutdown?.Invoke(); }

            public void Say(ICompanion pet, string text) { }
            public void SayAll(string text) { }
            public void Say(ICompanion pet, string text, DesktopAICompanion.Modules.SpeechStyle style) { Say(pet, text); }
            public void SayAll(string text, DesktopAICompanion.Modules.SpeechStyle style) { SayAll(text); }
            public bool TryPlayAnimation(ICompanion pet, string animationName) { return true; }
            public void PlayAnimationAll(IReadOnlyList<string> animationCandidates) { }
            public ScreenContext CaptureScreenContext(ICompanion pet) { return new ScreenContext { WindowTitle = "", ProcessName = "", MonitorBounds = new PixelRect(0, 0, 1920, 1080) }; }
            public IDisposable RegisterHotkey(string combo, Action onPressed) { return new Noop(); }
            public IModuleStorage GetStorage(string moduleId) { return null; }
            public IModuleSettings GetSettings(string moduleId) { return null; }
            public IDisposable RegisterDropResponder(int priority, Func<bool> onDrop) { return new Noop(); }
            public IDisposable RegisterPokeResponder(string moduleId, int priority, Func<bool> onPoke) { return new Noop(); }
            public IDisposable RegisterCompanionDropResponder(int priority, Func<ICompanion, bool> onDrop) { return new Noop(); }
            public IDisposable RegisterCompanionPokeResponder(string moduleId, int priority, Func<ICompanion, bool> onPoke) { return new Noop(); }
            public bool IsCompanionAlive(ICompanion pet) { return pet != null; }
            // Fullscreen is environmental, so a double reports "no game running" unless a test says
            // otherwise; FullscreenActive lets one say otherwise.
            public bool FullscreenActive;
            public bool IsFullscreenActive { get { return FullscreenActive; } }
            public event Action<bool> FullscreenChanged;
            public void RaiseFullscreen(bool on)
            {
                FullscreenActive = on;
                var h = FullscreenChanged; if (h != null) h(on);
            }
            public bool PlaySound(string moduleId, byte[] audio, double volume) { return false; }
            public bool PlayNotificationSound(string moduleId) { return false; }
            public bool StopSound(string moduleId) { return false; }
            public IDisposable RegisterSpeechResponder(string moduleId, int priority, Func<SpeechRequest, bool> onSpeech) { return new Noop(); }
            public System.Threading.Tasks.Task<IReadOnlyList<CatalogItem>> FetchCatalogItemsAsync(string kind) { return System.Threading.Tasks.Task.FromResult((IReadOnlyList<CatalogItem>)new List<CatalogItem>()); }
            public System.Threading.Tasks.Task<byte[]> DownloadCatalogItemAsync(string kind, string id) { return System.Threading.Tasks.Task.FromResult(new byte[0]); }
            public ICompanionManager GetCompanionManager(string moduleId) { return new DenyingCompanionManager(); }
            // Settable so the theme assertion can drive the module BOTH ways without touching the machine's
            // OS setting -- which is the whole point of the module reading this instead of the registry.
            public bool IsDarkTheme { get; set; }
            public void Log(string moduleId, string message) { }
            public IReadOnlyList<string> PickFilesToOpen(string title, string fileKindLabel, IReadOnlyList<string> extensions) { return new List<string>(); }
            public bool OpenLink(string moduleId, string httpsUrl) { return false; }
            public void AddTrayItems(IEnumerable<TrayItem> items) { if (items != null) TrayItems.AddRange(items); }
            public void AddOptionsPane(OptionsPane pane) { if (pane != null) OptionsPanes.Add(pane); }
            public void PublishContext(string moduleId, string key, string valueJson) { }
            public string ReadContext(string key) { return ""; }
            public event Action<string> ContextChanged { add { } remove { } }

            private sealed class Noop : IDisposable { public void Dispose() { } }
        }
    }
}
