using System;
using System.Collections.Generic;
using DesktopAICompanion.ModuleKit;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.PetStudioModule
{
    /// <summary>
    /// Companion Studio: check a pet's animations.xml, see what will never play, watch it run on the real desktop,
    /// and install it. The replacement for the retired Tools\PetTester, as a module rather than a separate
    /// app, so it is built by the same pipeline, gated by the same CI, delivered by the same catalog, and
    /// installed only by people who actually author pets.
    ///
    /// It validates with the HOST's parser (source-linked, not copied), so its verdict cannot disagree with
    /// what the host will run, and it previews through ICompanionManager.SpawnPreview, so the author sees the pet
    /// on their actual desktop without it being installed, saved, or added to their pet mix.
    /// </summary>
    public sealed class PetStudioModule : IModule
    {
        private IHost _host;
        private PetStudioWindow _window;

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "petstudio",
            Name = "Companion Studio",
            Version = "1.0.0",   // 1.0.0: rebased with the host for the Desktop AI Companion rename. Not a
                                 //        rollback -- the previous line below is the higher number, and
                                 //        every module restarts its numbering here alongside the app.
                                 // 1.7.0: renamed to Companion Studio, following the host product rename to
                                 //        Desktop AI Companion. The module ID stays "petstudio": it is the
                                 //        folder name on disk, the catalog key and the published zip URL, so
                                 //        renaming it would orphan every installed copy for a display string.
                                 // 1.6.7: payload refresh: the bundled compositor now collapses sprite cells that
                                 //        are byte-identical, and emitted action names lose their _left/_right
                                 //        suffix, so an import matches the migrated corpus
                                 // 1.6.6: payload refresh: the bundled runtime gained ScaleVelocity, so a small
                                 //        pet previews a walk that MOVES instead of animating on the spot
                                 // 1.6.5: payload refresh: the bundled ModuleKit gained the fullscreen test
                                 //        double, and the source-linked emitter had its wall/ceiling art swap
                                 //        REVERTED (it was wrong -- the anchoring proved the original mapping
                                 //        correct), so an import behaves as it did before 1.6.4.
                                 // 1.6.4: picks up the wall/ceiling ART un-swap from the source-linked emitter,
                                 //        so a skin that labels its rotated art "Ceiling" and its upright art
                                 //        "Wall" imports with the two the right way round instead of the pet
                                 //        appearing to stand sideways in mid-air on the ceiling.
                                 // 1.6.3: picks up the ROLE-SPLIT rest dwell from the source-linked emitter --
                                 //        the hub (return-to pose) stays brief while performances linger 9-12s,
                                 //        so an imported skin neither loiters nor flashes its idle actions by.
                                 // 1.6.2: picks up the short REST dwell from the source-linked emitter, so an
                                 //        imported skin's idle poses hold ~1.2s (the hand-authored reference)
                                 //        instead of ~9s, and the pet stops standing idle most of the time.
                                 // 1.6.1: picks up the surface REACH budget from the source-linked emitter, so
                                 //        an imported skin's wall climb crosses the wall in one sequence and
                                 //        the ceiling is reachable at all.
                                 // 1.6.0:  NEW: the reachability map says what each animation DOES, not just
                                 //         its name -- JUMP / CLIMB / CLING / MOVE / GAZE / ENGINE badges, a
                                 //         census under the legend, and the physics in prose in the detail
                                 //         panel. Names belong to the source skin, so finding a converted
                                 //         pet's jump used to mean knowing a Hollow Knight skin calls it
                                 //         "Grapple4".
                                 // 1.5.0:  NEW: the behaviour timeline. Drag animations from the reachability
                                 //         map into a chain, colour-coded by whether the pet's own graph offers
                                 //         each join, and run it on a throwaway pet whose animations are cloned
                                 //         and wired nose-to-tail -- so the ENGINE runs the chain with its own
                                 //         timing and physics rather than a sequencer guessing durations.
                                 // 1.4.18: picks up the three-phase JUMP from the source-linked emitter, so an
                                 //         imported skin's jumps reach a consistent height at a flat pace and
                                 //         land into motion instead of a facing flip.
                                 // 1.4.17: picks up the window UNDERSIDE (window-bottom) from the source-linked
                                 //         emitter, validator and XSD, so an imported skin can hang under a
                                 //         window and the Studio validates a pet that says so.
                                 // 1.4.16: picks up window-SIDE cling from the source-linked emitter and the
                                 //         widened XSD, so an imported skin gets the window-edge transitions
                                 //         and the Studio's validator accepts them.
                                 // 1.4.15: picks up the window-EDGE only= vocabulary (window-left/-right/-top)
                                 //         from the source-linked Xml.cs and validator, so the Studio validates
                                 //         and previews a pet using them instead of rejecting it.
                                 // 1.4.14: picks up GAZE conversion from the source-linked emitter, so a skin's
                                 //         "sit and look at the mouse" imports as a real animation instead of
                                 //         being dropped for having no frames.
                                 // 1.4.13: picks up the drag SWING ARC from the source-linked emitter, so an
                                 //         imported skin's drag animation carries all its pose variants rather
                                 //         than only the first.
                                 // 1.4.12: picks up JUMPS from the source-linked emitter, so a skin imported
                                 //         through the Studio can jump instead of having every upward action
                                 //         silently refused.
                                 // 1.4.11: picks up the direction-pair collapse and the honest classification
                                 //         of target-relative gates from the source-linked converter engine.
                                 // 1.4.10: picks up the tile-bleed fix from the source-linked Xml.cs, so a
                                 //         preview frame no longer carries a dark rim when downscaled.
                                 // 1.4.9: picks up the blank-ceiling-tile fix from the source-linked
                                 //        compositor, so a bundle-format skin imported through the Studio
                                 //        gets visible ceiling frames instead of transparent ones.
                                 // 1.4.8: picks up the ceiling region from the source-linked emitter and
                                 //        compositor, so a skin imported through the Studio can hang from the
                                 //        ceiling and its ceiling poses anchor to the cell top.
                                 // 1.4.7: picks up authored rest durations (a 10s pose is 10s, not a multiple
                                 //        of the 4s interval cap).
                                 // 1.4.6: picks up the per-tile clip that stops a frame bleeding into its
                                 //        neighbour (the black-blob artifact), from the same source-linked file.
                                 // 1.4.5: picks up the anchor-on-cell-bottom fix from the source-linked
                                 //        Shimeji\SpriteSheetBuilder.cs, so a skin imported through the
                                 //        Studio stands on the floor rather than hovering above it.
                                 // 1.4.4: picks up the rest/wall animation TIME budgets, so a skin imported
                                 //        through the Studio rests and climbs for the same duration the CLI
                                 //        now emits.
                                 // 1.4.3: picks up the wall-climbing region from the source-linked
                                 //        Emit\PetEmitter.cs, so a skin imported through Companion Studio gets the
                                 //        same wall behaviour the CLI now emits.
                                 // 1.4.2: picks up the damped + floored hub weighting from the source-linked
                                 //        Emit\PetEmitter.cs, so a skin imported through Companion Studio gets the
                                 //        same fixed weighting the CLI now emits. Caught by the widened
                                 //        freshness check rather than by anyone remembering.
                                 // 1.4.1: payload refresh only, no behaviour change -- the bundled ModuleKit
                                 //        was 3 commits stale. See the note on Fortunes 1.2.4. This module is
                                 //        the reason the freshness check was widened: it also SOURCE-LINKS 7
                                 //        files from src\ and 13 from tools\, none of which were watched.
                                 // 1.4.0: "Analyze installed companion" dropdown -- pick any installed pet (bundled,
                                 //        library, or built-in) and analyze it without hunting for its xml;
                                 //        reads it via the host's new ICompanionManager.TryReadTypeXml (needs 1.8.0)
                                 // 1.3.0: import Android JSON+WebP bundles too (bundled dwebp decoder), not just desktop skins
            // 1.2.1: .zip import + converter gains (Japanese vocab, nested-sprite detection)
            // 1.2.0: Import Shimeji skin -> convert -> editor + loss report (workshop half)
                                 // 1.1.1: the window's theme comes from IHost.IsDarkTheme, not the OS registry
                                 // 1.1.0: authoring window (editable XML, reachability map, sprite playback)
            // 1.4.7 is the host that added IHost.IsDarkTheme, which the studio's window reads so it matches the
            // app even when the user has PINNED light or dark rather than following the OS. (1.4.6 added
            // ICompanionManager.CompanionsDirectory, which the file dialog still uses.) Declaring it means an older host
            // refuses this module with a legible reason instead of loading it and failing at a missing member.
            MinHostVersion = "1.0.0",
            Permissions = ModulePermissions.Companions | ModulePermissions.Storage,
        };

        public void Init(IHost host)
        {
            _host = host;

            host.AddTrayItems(new List<TrayItem>
            {
                new TrayItem
                {
                    Label = "Companion Studio…", Group = 40, Order = 0, Click = Open,
                    IconPng = EmbeddedResources.LoadBytes(typeof(PetStudioModule).Assembly, "petstudio.png"),
                },
            });

            host.AddOptionsPane(new OptionsPane
            {
                Title = "Companion Studio",
                Schema = new List<SettingField>
                {
                    new SettingField
                    {
                        Id = "about",
                        Label = "What this is",
                        Kind = SettingKind.Info,
                        Group = "Companion Studio",
                    },
                },
                Actions = new[]
                {
                    new PaneAction { Label = "Open Companion Studio…", InvokeAsync = OpenAsync, Group = "Companion Studio" },
                },
                Load = delegate
                {
                    return new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        {
                            "about",
                            "Check a pet's animations.xml before you use it: what the host would reject, " +
                            "which animations can never play, and how it actually looks running on your " +
                            "desktop. A preview companion is temporary — it is never saved and never joins your companions."
                        },
                    };
                },
            });
        }

        private System.Threading.Tasks.Task<string> OpenAsync()
        {
            Open();
            return System.Threading.Tasks.Task.FromResult("Companion Studio is open.");
        }

        /// <summary>Show the studio, or bring the existing one forward. One window: a second would let two
        /// previews fight over the same pet slots.</summary>
        private void Open()
        {
            try
            {
                if (_window != null && _window.IsLoaded)
                {
                    _window.Activate();
                    return;
                }
                _window = new PetStudioWindow(_host);
                _window.Closed += delegate { _window = null; };
                _window.Show();
            }
            catch (Exception ex)
            {
                if (_host != null) _host.SayAll("Companion Studio could not open: " + ex.Message);
            }
        }

        /// <summary>Open the studio (or bring it forward) and immediately start the Shimeji import flow. Public
        /// so the host's Pets pane can deep-link straight here, invoked by reflection over the loaded module
        /// instance (the host cannot cast across the module's load context, and IModule stays frozen).</summary>
        public void OpenForImport()
        {
            Open();
            try { if (_window != null) _window.BeginImport(); }
            catch (Exception ex) { if (_host != null) _host.SayAll("Companion Studio import could not start: " + ex.Message); }
        }

        public void Shutdown()
        {
            PetStudioWindow window = _window;
            _window = null;
            if (window == null) return;
            // Closing removes any live preview: the window owns that handle.
            try { window.Close(); } catch { }
        }
    }
}
